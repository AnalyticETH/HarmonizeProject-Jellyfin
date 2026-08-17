using System.Net;
using System.Reflection;
using System.Text;
using Jellyfin.Plugin.Hue.Configuration;
using Jellyfin.Plugin.Hue.Hue;
using Jellyfin.Plugin.Hue.Service;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

public sealed class HueSyncServiceLifecycleTests
{
    [Fact]
    public async Task RuntimeStatus_ReportsActiveDiagnosticsWithoutCredentials()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);

        await service.StartAsync(CancellationToken.None);

        SetPrivateField(service, "_syncCts", new CancellationTokenSource());
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "secret-app-key", "secret-client-key", "area-id"));
        SetPrivateField(service, "_currentItemName", "Feature film");
        SetPrivateField(service, "_syncStartTime", DateTime.UtcNow.AddSeconds(-3));
        SetPrivateField(service, "_runtimeState", "Syncing");
        SetPrivateField(service, "_runtimeMessage", "Streaming video colors to Hue.");

        var status = service.GetRuntimeStatus();

        Assert.True(status.IsSyncing);
        Assert.Equal("Syncing", status.State);
        Assert.Equal("Feature film", status.CurrentItem);
        Assert.Equal("192.168.1.100", status.ActiveBridgeIp);
        Assert.Equal("area-id", status.ActiveEntertainmentAreaId);
        Assert.True(status.SyncDurationSeconds >= 2);
        Assert.DoesNotContain("secret-app-key", status.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-client-key", status.Message, StringComparison.Ordinal);

        // Keep shutdown focused on local resources for this snapshot test.
        SetPrivateField(service, "_bridgeAreaDeactivated", true);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopCurrentSync_SuppressesProgressUntilPlaybackStops()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);

        await service.StartAsync(CancellationToken.None);
        SetPrivateField(service, "_syncCts", new CancellationTokenSource());
        SetPrivateField(service, "_currentPlaySessionId", "session-a");
        SetPrivateField(service, "_currentItemName", "Feature film");
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "app-key", "client-key", "area-id"));
        SetPrivateField(service, "_syncStartTime", DateTime.UtcNow.AddSeconds(-10));

        var stopTask = service.StopCurrentSyncAsync();
        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(service.CanStopSync);

        handler.ReleaseStopRequest();
        Assert.True(await stopTask);
        Assert.False(service.IsSyncing);
        Assert.False(service.CanStopSync);
        Assert.Equal("Stopped", service.GetRuntimeStatus().State);

        var progressMethod = typeof(HueSyncService).GetMethod("OnPlaybackProgress", BindingFlags.Instance | BindingFlags.NonPublic)!;
        progressMethod.Invoke(service, new object?[] { null, CreateProgress("session-a") });
        Assert.Null(GetPrivateField(service, "_syncCts"));

        var stopMethod = typeof(HueSyncService).GetMethod("OnPlaybackStopped", BindingFlags.Instance | BindingFlags.NonPublic)!;
        stopMethod.Invoke(service, new object?[] { null, CreateStop("session-a") });
        Assert.Equal("Idle", service.GetRuntimeStatus().State);
        Assert.Null(GetPrivateField(service, "_manuallyStoppedPlaySessionId"));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StaleManualStopNotificationDoesNotResetNewerSession()
    {
        using var httpClient = new HttpClient(new BlockingHueHandler());
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);

        SetPrivateField(service, "_currentPlaySessionId", "session-a");
        SetPrivateField(service, "_startingPlaySessionId", "session-b");
        SetPrivateField(service, "_manuallyStoppedPlaySessionId", "session-a");
        SetPrivateField(service, "_runtimeState", "Starting");
        SetPrivateField(service, "_runtimeMessage", "Preparing newer playback.");

        var stopMethod = typeof(HueSyncService).GetMethod("OnPlaybackStopped", BindingFlags.Instance | BindingFlags.NonPublic)!;
        stopMethod.Invoke(service, new object?[] { null, CreateStop("session-a") });

        Assert.Null(GetPrivateField(service, "_manuallyStoppedPlaySessionId"));
        Assert.Equal("session-a", GetPrivateField(service, "_currentPlaySessionId"));
        Assert.Equal("session-b", GetPrivateField(service, "_startingPlaySessionId"));
        Assert.Equal("Starting", service.GetRuntimeStatus().State);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PlaybackStop_QueuesImmediateNextStartUntilCleanupCompletes()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);

        await service.StartAsync(CancellationToken.None);

        var onStartMethod = typeof(HueSyncService).GetMethod("OnPlaybackStart", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var stopMethod = typeof(HueSyncService).GetMethod("OnPlaybackStopped", BindingFlags.Instance | BindingFlags.NonPublic)!;
        onStartMethod.Invoke(service, new object?[] { null, CreateProgress("session-a") });
        await handler.FirstConfigurationRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _ = stopMethod.Invoke(service, new object?[] { null, CreateStop("session-a") });
        onStartMethod.Invoke(service, new object?[] { null, CreateProgress("session-b") });

        handler.ReleaseFirstConfiguration();
        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(handler.SecondConfigurationRequest.Task.IsCompleted);

        handler.ReleaseStopRequest();
        await handler.SecondConfigurationRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.True(handler.StopRequest.Task.IsCompletedSuccessfully);
        Assert.True(handler.SecondConfigurationRequest.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task PlaybackStop_CancelsQueuedStartAndCleansCancelledPredecessor()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);

        await service.StartAsync(CancellationToken.None);

        var onStartMethod = typeof(HueSyncService).GetMethod("OnPlaybackStart", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var stopMethod = typeof(HueSyncService).GetMethod("OnPlaybackStopped", BindingFlags.Instance | BindingFlags.NonPublic)!;
        onStartMethod.Invoke(service, new object?[] { null, CreateProgress("session-a") });
        await handler.FirstConfigurationRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Plugin.Instance!.Configuration.RestoreLightState = true;
        typeof(HueSyncService).GetField("_savedLightStates", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, new List<HueClient.LightState> { new("light-id", true, 50, 0.1, 0.2) });

        onStartMethod.Invoke(service, new object?[] { null, CreateProgress("session-b") });
        _ = stopMethod.Invoke(service, new object?[] { null, CreateStop("session-b") });

        handler.ReleaseFirstConfiguration();
        await handler.RestorationRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handler.ReleaseStopRequest();
        await handler.StopRequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(handler.SecondConfigurationRequest.Task.IsCompleted);
        Assert.Null(GetPrivateField(service, "_syncCts"));
        Assert.Null(GetPrivateField(service, "_currentPlaySessionId"));
        Assert.Null(GetPrivateField(service, "_currentBridgeConfig"));
        Assert.Null(GetPrivateField(service, "_savedLightStates"));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PlaybackPause_QueuesImmediateResumeUntilDeactivationCompletes()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);

        SetPrivateField(service, "_syncCts", new CancellationTokenSource());
        SetPrivateField(service, "_currentPlaySessionId", "session-a");
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "app-key", "client-key", "area-id"));
        SetPrivateField(service, "_syncStartTime", DateTime.UtcNow.AddSeconds(-10));

        var progressMethod = typeof(HueSyncService).GetMethod("OnPlaybackProgress", BindingFlags.Instance | BindingFlags.NonPublic)!;
        progressMethod.Invoke(service, new object?[] { null, CreateProgress("session-a", isPaused: true) });
        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));

        progressMethod.Invoke(service, new object?[] { null, CreateProgress("session-a") });
        Assert.False(handler.FirstConfigurationRequest.Task.IsCompleted);

        handler.ReleaseStopRequest();
        handler.ReleaseFirstConfiguration();
        await handler.FirstConfigurationRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PlaybackPause_DuringShutdownDoesNotPublishCleanupTask()
    {
        using var httpClient = new HttpClient(new BlockingHueHandler());
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);

        SetPrivateField(service, "_syncCts", new CancellationTokenSource());
        SetPrivateField(service, "_currentPlaySessionId", "session-a");
        SetPrivateField(service, "_syncStartTime", DateTime.UtcNow.AddSeconds(-10));

        var syncLock = GetPrivateField(service, "_syncLock")!;
        var progressMethod = typeof(HueSyncService).GetMethod("OnPlaybackProgress", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var eventThread = new Thread(() => progressMethod.Invoke(service, new object?[] { null, CreateProgress("session-a", isPaused: true) }));

        Monitor.Enter(syncLock);
        try
        {
            eventThread.Start();
            var eventReachedStateLock = SpinWait.SpinUntil(
                () => (eventThread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(5));
            // Publish the same shutdown flag that StopAsync sets while the event is queued on _syncLock.
            SetPrivateField(service, "_isStopping", true);
            Assert.True(eventReachedStateLock);
        }
        finally
        {
            Monitor.Exit(syncLock);
        }

        Assert.True(eventThread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(GetPrivateField(service, "_pauseCleanupTask"));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PlaybackPauseThenStop_RestoresSavedState()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);

        Plugin.Instance!.Configuration.RestoreLightState = true;
        SetPrivateField(service, "_savedLightStates", new List<HueClient.LightState> { new("light-id", true, 50, 0.1, 0.2) });
        SetPrivateField(service, "_syncCts", new CancellationTokenSource());
        SetPrivateField(service, "_currentPlaySessionId", "session-a");
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "app-key", "client-key", "area-id"));
        SetPrivateField(service, "_syncStartTime", DateTime.UtcNow.AddSeconds(-10));

        var progressMethod = typeof(HueSyncService).GetMethod("OnPlaybackProgress", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var stopMethod = typeof(HueSyncService).GetMethod("OnPlaybackStopped", BindingFlags.Instance | BindingFlags.NonPublic)!;
        progressMethod.Invoke(service, new object?[] { null, CreateProgress("session-a", isPaused: true) });
        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _ = stopMethod.Invoke(service, new object?[] { null, CreateStop("session-a") });
        Assert.False(handler.RestorationRequest.Task.IsCompleted);

        handler.ReleaseStopRequest();
        await handler.RestorationRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await handler.StopRequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(GetPrivateField(service, "_savedLightStates"));
        Assert.Null(GetPrivateField(service, "_currentBridgeConfig"));
        Assert.Null(GetPrivateField(service, "_currentPlaySessionId"));

        await service.StopAsync(CancellationToken.None);
    }

    private static HueSyncService CreateService(HttpClient httpClient)
    {
        var hueClient = new HueClient(httpClient, Mock.Of<ILogger<HueClient>>());
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(factory => factory.CreateLogger(It.IsAny<string>())).Returns(Mock.Of<ILogger>());
        var applicationPaths = new Mock<IApplicationPaths>();
        var pluginDataPath = Path.Combine(Path.GetTempPath(), "jellyfin-hue-lifecycle-test");
        Directory.CreateDirectory(pluginDataPath);
        applicationPaths.SetupGet(paths => paths.ProgramDataPath).Returns(pluginDataPath);
        applicationPaths.SetupGet(paths => paths.WebPath).Returns(pluginDataPath);
        applicationPaths.SetupGet(paths => paths.ProgramSystemPath).Returns(pluginDataPath);
        applicationPaths.SetupGet(paths => paths.DataPath).Returns(pluginDataPath);
        applicationPaths.SetupGet(paths => paths.ImageCachePath).Returns(pluginDataPath);
        applicationPaths.SetupGet(paths => paths.PluginsPath).Returns(pluginDataPath);
        applicationPaths.SetupGet(paths => paths.PluginConfigurationsPath).Returns(pluginDataPath);
        applicationPaths.SetupGet(paths => paths.LogDirectoryPath).Returns(pluginDataPath);
        applicationPaths.SetupGet(paths => paths.ConfigurationDirectoryPath).Returns(pluginDataPath);
        applicationPaths.SetupGet(paths => paths.SystemConfigurationFilePath).Returns(Path.Combine(pluginDataPath, "system.xml"));
        applicationPaths.SetupGet(paths => paths.CachePath).Returns(pluginDataPath);
        applicationPaths.SetupGet(paths => paths.TempDirectory).Returns(pluginDataPath);
        applicationPaths.SetupGet(paths => paths.VirtualDataPath).Returns(pluginDataPath);
        var plugin = new Plugin(applicationPaths.Object, Mock.Of<IXmlSerializer>());
        var configurationField = plugin.GetType().BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!;
        configurationField.SetValue(plugin, new PluginConfiguration());
        var configuration = plugin.Configuration;
        configuration.SyncEnabled = true;
        configuration.HueBridgeIp = "192.168.1.100";
        configuration.HueAppKey = "app-key";
        configuration.HueClientKey = "client-key";
        configuration.EntertainmentAreaId = "area-id";
        configuration.RestoreLightState = false;
        configuration.UseCinemaMode = false;

        return new HueSyncService(
            Mock.Of<ISessionManager>(),
            Mock.Of<ILogger<HueSyncService>>(),
            loggerFactory.Object,
            hueClient,
            Mock.Of<IMediaEncoder>());
    }

    private static object? GetPrivateField(HueSyncService service, string fieldName) =>
        typeof(HueSyncService).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(service);
    private static void SetPrivateField(object target, string fieldName, object? value) =>
        target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static PlaybackProgressEventArgs CreateProgress(string playSessionId, bool isPaused = false)
    {
        return new PlaybackProgressEventArgs
        {
            Item = CreateItem("/tmp/nonexistent-video"),
            PlaySessionId = playSessionId,
            PlaybackPositionTicks = 0L,
            IsPaused = isPaused
        };
    }

    private static PlaybackStopEventArgs CreateStop(string playSessionId)
    {
        return new PlaybackStopEventArgs
        {
            PlaySessionId = playSessionId,
            Item = CreateItem(null)
        };
    }

    private static BaseItem CreateItem(string? path)
    {
        return new MediaBrowser.Controller.Entities.Video
        {
            Name = "Test item",
            Path = path
        };
    }
    private sealed class BlockingHueHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> FirstConfigurationRequest { get; } = NewSignal();
        public TaskCompletionSource<bool> SecondConfigurationRequest { get; } = NewSignal();
        public TaskCompletionSource<bool> StopRequest { get; } = NewSignal();
        public TaskCompletionSource<bool> StopRequestCompleted { get; } = NewSignal();
        public TaskCompletionSource<bool> RestorationRequest { get; } = NewSignal();

        private readonly TaskCompletionSource<bool> _firstConfigurationRelease = NewSignal();
        private readonly TaskCompletionSource<bool> _stopRelease = NewSignal();
        private int _configurationRequestCount;

        public void ReleaseFirstConfiguration() => _firstConfigurationRelease.TrySetResult(true);

        public void ReleaseStopRequest() => _stopRelease.TrySetResult(true);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath.Contains("entertainment_configuration", StringComparison.Ordinal) == true)
            {
                if (Interlocked.Increment(ref _configurationRequestCount) == 1)
                {
                    FirstConfigurationRequest.TrySetResult(true);
                    await _firstConfigurationRelease.Task;
                }
                else
                {
                    SecondConfigurationRequest.TrySetResult(true);
                }

                return ConfigurationResponse();
            }

            if (request.Method == HttpMethod.Put)
            {
                var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
                if (request.RequestUri?.AbsolutePath.Contains("/light/", StringComparison.Ordinal) == true)
                    RestorationRequest.TrySetResult(true);
                if (body.Contains("\"stop\"", StringComparison.Ordinal))
                {
                    StopRequest.TrySetResult(true);
                    await _stopRelease.Task;
                    StopRequestCompleted.TrySetResult(true);
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }

        private static HttpResponseMessage ConfigurationResponse()
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"channels\":[]}]}",
                    Encoding.UTF8,
                    "application/json")
            };
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
