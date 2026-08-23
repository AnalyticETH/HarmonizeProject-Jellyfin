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
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

[Collection("PluginState")]
public sealed class HueSyncServiceLifecycleTests
{
    [Fact]
    public async Task HasActivePlaybackSessions_IgnoresTerminalSnapshots()
    {
        using var httpClient = new HttpClient(new BlockingHueHandler());
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);

        SetPrivateField(service, "_runtimeState", "Error");
        SetPrivateField(service, "_runtimeMessage", "A previous playback session failed.");
        Assert.False(service.HasActivePlaybackSessions);

        SetPrivateField(service, "_runtimeState", "Starting");
        Assert.True(service.HasActivePlaybackSessions);

        await service.StopAsync(CancellationToken.None);
    }

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
        SetPrivateField(service, "_currentFrameResolution", PluginConfiguration.FrameResolutionHigh);
        SetPrivateField(service, "_currentVideoScalingMode", PluginConfiguration.VideoScalingModeFit);
        SetPrivateField(service, "_currentVideoDeinterlaceMode", PluginConfiguration.VideoDeinterlaceModeAuto);
        SetPrivateField(service, "_currentTargetFps", 30);
        SetPrivateField(service, "_currentSamplingBreadthPercent", 25);
        SetPrivateField(service, "_currentSamplingMode", PluginConfiguration.SamplingModeCenterWeighted);
        SetPrivateField(service, "_currentColorSmoothingPercent", 40);
        SetPrivateField(
            service,
            "_activeColorProcessingSettings",
            (
                BrightnessBoost: 150,
                RedGain: 120,
                GreenGain: 90,
                BlueGain: 110,
                ColorSaturation: 125,
                HueShiftDegrees: -30,
                OutputBrightnessPercent: 75,
                GammaCorrection: 1.25,
                ContrastPercent: 125,
                ColorTemperatureKelvin: 4200,
                BlackoutThreshold: 30,
                BlackoutBehavior: PluginConfiguration.BlackoutBehaviorKeepLastColors,
                ColorChangeThreshold: 5));
        SetPrivateField(
            service,
            "_activeExecutionSettings",
            (
                UseGpu: false,
                CustomFfmpegFlags: "-hwaccel vaapi",
                FfmpegStallTimeoutSeconds: 20,
                NetworkRetryAttempts: 1));
        SetPrivateField(service, "_activeChannelIds", new HashSet<int> { 9, 2 });
        SetPrivateField(service, "_activeRestoreLightState", true);
        var activeUserId = Guid.NewGuid();
        Plugin.Instance!.Configuration.PlaybackMediaFilter = PluginConfiguration.PlaybackMediaFilterMovies;
        Plugin.Instance.Configuration.UserMappings.Add(new UserBridgeMapping
        {
            UserId = activeUserId.ToString(),
            PlaybackMediaFilterOverride = PluginConfiguration.PlaybackMediaFilterEpisodes
        });
        SetPrivateField(service, "_currentUserId", activeUserId);
        SetPrivateField(service, "_currentUserName", "Living Room Viewer");
        SetPrivateField(service, "_currentItemName", "Feature film");
        SetPrivateField(service, "_syncStartTime", DateTime.UtcNow.AddSeconds(-3));
        SetPrivateField(service, "_seekRestartCount", 2);
        SetPrivateField(service, "_lastSeekPositionSeconds", 142.5);
        SetPrivateField(service, "_runtimeState", "Syncing");
        SetPrivateField(service, "_runtimeMessage", "Streaming video colors to Hue.");
        var hueStreamer = Assert.IsType<HueStreamer>(GetPrivateField(service, "_hueStreamer"));
        SetPrivateField(hueStreamer, "_packetsSent", 42L);
        SetPrivateField(hueStreamer, "_packetsSkippedByThreshold", 7L);
        SetPrivateField(hueStreamer, "_packetSendFailures", 2L);
        SetPrivateField(hueStreamer, "_totalReconnectAttempts", 1);
        var ffmpegStreamer = Assert.IsType<Jellyfin.Plugin.Hue.Video.FfmpegStreamer>(GetPrivateField(service, "_ffmpegStreamer"));
        SetPrivateField(ffmpegStreamer, "_framesProcessed", 60L);

        var status = service.GetRuntimeStatus();

        Assert.True(status.IsSyncing);
        Assert.Equal("Syncing", status.State);
        Assert.Equal("Feature film", status.CurrentItem);
        Assert.Equal(activeUserId.ToString(), status.ActiveUserId);
        Assert.Equal("Living Room Viewer", status.ActiveUserName);
        Assert.Equal(PluginConfiguration.PlaybackMediaFilterEpisodes, status.ActivePlaybackMediaFilter);
        Assert.Equal(PluginConfiguration.FrameResolutionHigh, status.ActiveFrameResolution);
        Assert.Equal(PluginConfiguration.VideoScalingModeFit, status.ActiveVideoScalingMode);
        Assert.Equal(PluginConfiguration.VideoDeinterlaceModeAuto, status.ActiveVideoDeinterlaceMode);
        Assert.Equal(30, status.ActiveTargetFps);
        Assert.Equal(25, status.ActiveSamplingBreadthPercent);
        Assert.Equal(PluginConfiguration.SamplingModeCenterWeighted, status.ActiveSamplingMode);
        Assert.Equal(40, status.ActiveColorSmoothingPercent);
        Assert.Equal(150, status.ActiveBrightnessBoost);
        Assert.Equal(120, status.ActiveRedGain);
        Assert.Equal(90, status.ActiveGreenGain);
        Assert.Equal(110, status.ActiveBlueGain);
        Assert.Equal(125, status.ActiveColorSaturation);
        Assert.Equal(-30, status.ActiveHueShiftDegrees);
        Assert.Equal(75, status.ActiveOutputBrightnessPercent);
        Assert.Equal(1.25, status.ActiveGammaCorrection);
        Assert.Equal(125, status.ActiveContrastPercent);
        Assert.Equal(4200, status.ActiveColorTemperatureKelvin);
        Assert.Equal(30, status.ActiveBlackoutThreshold);
        Assert.Equal(PluginConfiguration.BlackoutBehaviorKeepLastColors, status.ActiveBlackoutBehavior);
        Assert.Equal(5, status.ActiveColorChangeThreshold);
        Assert.Equal((bool?)false, status.ActiveUseGpu);
        Assert.Equal((bool?)true, status.ActiveCustomFfmpegFlagsConfigured);
        Assert.Equal(20, status.ActiveFfmpegStallTimeoutSeconds);
        Assert.Equal(1, status.ActiveNetworkRetryAttempts);
        Assert.Equal("2, 9", status.ActiveChannelIds);
        Assert.Equal((bool?)true, status.ActiveRestoreLightState);
        Assert.Equal("192.168.1.100", status.ActiveBridgeIp);
        Assert.Equal("area-id", status.ActiveEntertainmentAreaId);
        Assert.Equal(60, status.FramesProcessed);
        Assert.True(status.EffectiveFps >= 19 && status.EffectiveFps <= 21);
        Assert.Equal(42, status.PacketsSent);
        Assert.Equal(7, status.PacketsSkippedByThreshold);
        Assert.Equal(2, status.PacketSendFailures);
        Assert.Equal(1, status.ReconnectAttempts);
        Assert.Equal(2, status.SeekRestartCount);
        Assert.Equal(142.5, status.LastSeekPositionSeconds);
        Assert.True(status.SyncDurationSeconds >= 2);
        Assert.DoesNotContain("secret-app-key", status.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-client-key", status.Message, StringComparison.Ordinal);

        // Keep shutdown focused on local resources for this snapshot test.
        SetPrivateField(service, "_bridgeAreaDeactivated", true);
        await service.StopAsync(CancellationToken.None);
        var stoppedStatus = service.GetRuntimeStatus();
        Assert.Null(stoppedStatus.ActiveUserId);
        Assert.Null(stoppedStatus.ActiveUserName);
        Assert.Null(stoppedStatus.ActivePlaybackMediaFilter);
        Assert.Equal(0, stoppedStatus.SeekRestartCount);
        Assert.Null(stoppedStatus.LastSeekPositionSeconds);
    }

    [Fact]
    public async Task RuntimeStatus_ReportsCleanupWarningAndClearsItForNewPlayback()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);

        SetPrivateField(service, "_lastCleanupWarning", "Light restoration was incomplete.");
        Assert.Equal("Light restoration was incomplete.", service.GetRuntimeStatus().CleanupWarning);

        var setStatusMethod = typeof(HueSyncService).GetMethod("SetRuntimeStatus", BindingFlags.Instance | BindingFlags.NonPublic)!;
        setStatusMethod.Invoke(service, new object?[] { "Starting", "Preparing playback.", true });

        Assert.Null(service.GetRuntimeStatus().CleanupWarning);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_RecoversAnAlreadyPlayingVideoSession()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var sessionManager = new Mock<ISessionManager>();
        var activeSession = new SessionInfo(sessionManager.Object, Mock.Of<ILogger>())
        {
            Id = "client-session-1",
            UserId = Guid.NewGuid(),
            UserName = "Already Playing Viewer",
            LastActivityDate = DateTime.UtcNow,
            FullNowPlayingItem = new MediaBrowser.Controller.Entities.Video
            {
                Name = "Already playing film",
                Path = "/tmp/already-playing-film.mkv"
            },
            PlayState = new PlayerStateInfo
            {
                IsPaused = false,
                PositionTicks = TimeSpan.FromSeconds(37).Ticks
            }
        };
        sessionManager
            .SetupGet(manager => manager.Sessions)
            .Returns(new[] { activeSession });
        var service = CreateService(httpClient, sessionManager: sessionManager.Object);

        await service.StartAsync(CancellationToken.None);
        await handler.FirstConfigurationRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var status = service.GetRuntimeStatus();
        Assert.True(status.IsSyncing);
        Assert.Equal("Already playing film", status.CurrentItem);
        Assert.Equal("Already Playing Viewer", status.ActiveUserName);

        handler.ReleaseFirstConfiguration();
        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handler.ReleaseStopRequest();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void RecoveredPlaybackEventsUseTheStableLifecycleSessionId()
    {
        using var httpClient = new HttpClient(new BlockingHueHandler());
        var service = CreateService(httpClient);
        SetPrivateField(service, "_currentPlaySessionId", "hue-recovered:client-session-1");
        SetPrivateField(service, "_recoveredSessionId", "client-session-1");
        var session = new SessionInfo(Mock.Of<ISessionManager>(), Mock.Of<ILogger>())
        {
            Id = "client-session-1"
        };

        var progress = new PlaybackProgressEventArgs
        {
            Item = CreateItem("/tmp/recovered-video"),
            Session = session,
            PlaySessionId = "jellyfin-play-session",
            PlaybackPositionTicks = 10L
        };
        var normalizeProgress = typeof(HueSyncService).GetMethod(
            "NormalizeRecoveredPlaybackEvent",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var normalizedProgress = Assert.IsType<PlaybackProgressEventArgs>(normalizeProgress.Invoke(service, new object?[] { progress }));
        Assert.Equal("hue-recovered:client-session-1", normalizedProgress.PlaySessionId);

        var stop = new PlaybackStopEventArgs
        {
            Item = CreateItem("/tmp/recovered-video"),
            Session = session,
            PlaySessionId = "jellyfin-play-session"
        };
        var normalizeStop = typeof(HueSyncService).GetMethod(
            "NormalizeRecoveredPlaybackStop",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var normalizedStop = Assert.IsType<PlaybackStopEventArgs>(normalizeStop.Invoke(service, new object?[] { stop }));
        Assert.Equal("hue-recovered:client-session-1", normalizedStop.PlaySessionId);
    }

    [Fact]
    public async Task Cleanup_PublishesLastSessionSummaryWithoutCredentials()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, persistSessionHistory: true);
        await service.StartAsync(CancellationToken.None);

        SetPrivateField(service, "_syncCts", new CancellationTokenSource());
        SetPrivateField(service, "_currentPlaySessionId", "session-summary");
        SetPrivateField(service, "_currentItemName", "Feature film");
        SetPrivateField(service, "_currentUserId", Guid.NewGuid());
        SetPrivateField(service, "_currentUserName", "Living Room Viewer");
        SetPrivateField(service, "_syncStartTime", DateTime.UtcNow.AddSeconds(-4));
        SetPrivateField(service, "_seekRestartCount", 2);
        SetPrivateField(service, "_lastSeekPositionSeconds", 142.5);
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "secret-app-key", "secret-client-key", "area-id"));

        var hueStreamer = Assert.IsType<HueStreamer>(GetPrivateField(service, "_hueStreamer"));
        SetPrivateField(hueStreamer, "_packetsSent", 42L);
        SetPrivateField(hueStreamer, "_packetsSkippedByThreshold", 7L);
        SetPrivateField(hueStreamer, "_packetSendFailures", 2L);
        SetPrivateField(hueStreamer, "_totalReconnectAttempts", 1);
        var ffmpegStreamer = Assert.IsType<Jellyfin.Plugin.Hue.Video.FfmpegStreamer>(GetPrivateField(service, "_ffmpegStreamer"));
        SetPrivateField(ffmpegStreamer, "_framesProcessed", 60L);

        var restoreMethod = typeof(HueSyncService).GetMethod("RestoreAndDeactivateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var restoreTask = Assert.IsAssignableFrom<Task>(restoreMethod.Invoke(service, new object?[]
        {
            Plugin.Instance!.Configuration,
            new ValueTuple<string, string, string, string>("192.168.1.100", "secret-app-key", "secret-client-key", "area-id"),
            null,
            true,
            true,
            "Stopped",
            true
        }));

        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handler.ReleaseStopRequest();
        await restoreTask;

        var summary = service.GetRuntimeStatus().LastSession;
        Assert.NotNull(summary);
        var history = service.GetSessionHistory();
        Assert.Single(history);
        Assert.Same(summary, history[0]);
        Assert.Single(service.GetSessionHistory(10, "stopped"));
        Assert.Empty(service.GetSessionHistory(10, "Error"));
        Assert.Equal("Stopped", summary!.Outcome);
        Assert.Equal("Feature film", summary.Item);
        Assert.Equal("Living Room Viewer", summary.UserName);
        Assert.Equal("192.168.1.100", summary.BridgeIp);
        Assert.Equal("area-id", summary.EntertainmentAreaId);
        Assert.Equal(60, summary.FramesProcessed);
        Assert.Equal(42, summary.PacketsSent);
        Assert.Equal(7, summary.PacketsSkippedByThreshold);
        Assert.Equal(2, summary.PacketSendFailures);
        Assert.Equal(1, summary.ReconnectAttempts);
        Assert.Equal(2, summary.SeekRestartCount);
        Assert.Equal(142.5, summary.LastSeekPositionSeconds);
        Assert.True(summary.DurationSeconds >= 3);
        var summaryJson = System.Text.Json.JsonSerializer.Serialize(summary);
        Assert.DoesNotContain("secret-app-key", summaryJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-client-key", summaryJson, StringComparison.Ordinal);
        var historyJson = System.Text.Json.JsonSerializer.Serialize(history);
        Assert.DoesNotContain("secret-app-key", historyJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-client-key", historyJson, StringComparison.Ordinal);
        var persisted = Assert.Single(Plugin.Instance!.Configuration.PersistedSessionHistory);
        Assert.Equal(summary.Outcome, persisted.Outcome);
        var persistedJson = System.Text.Json.JsonSerializer.Serialize(persisted);
        Assert.DoesNotContain("secret-app-key", persistedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-client-key", persistedJson, StringComparison.Ordinal);

        var sessionHistory = Assert.IsType<List<HueSessionSummary>>(GetPrivateField(service, "_sessionHistory"));
        sessionHistory.Clear();
        SetPrivateField(service, "_lastSessionSummary", null);
        var loadMethod = typeof(HueSyncService).GetMethod("LoadPersistedSessionHistory", BindingFlags.Instance | BindingFlags.NonPublic)!;
        loadMethod.Invoke(service, null);
        Assert.Single(service.GetSessionHistory());
        Assert.Equal(summary.Item, service.GetRuntimeStatus().LastSession!.Item);

        Assert.Equal(1, service.ClearSessionHistory());
        Assert.Empty(service.GetSessionHistory());
        Assert.Null(service.GetRuntimeStatus().LastSession);
        Assert.Empty(Plugin.Instance!.Configuration.PersistedSessionHistory);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PersistedSessionHistory_LoadsNewestEntriesAndEnforcesBound()
    {
        using var httpClient = new HttpClient(new BlockingHueHandler());
        var service = CreateService(httpClient, persistSessionHistory: true);
        var configuration = Plugin.Instance!.Configuration;
        configuration.SessionHistoryRetentionCount = 7;
        configuration.PersistedSessionHistory = Enumerable.Range(0, 12)
            .Select(index => new HueSessionHistoryEntry
            {
                Outcome = index % 2 == 0 ? "Ended" : "Error",
                Item = "Persisted item " + index,
                UserName = "Persisted viewer " + index
            })
            .ToList();

        await service.StartAsync(CancellationToken.None);

        var history = service.GetSessionHistory();
        Assert.Equal(7, history.Count);
        Assert.Equal("Persisted item 0", history[0].Item);
        Assert.Equal(7, configuration.PersistedSessionHistory.Count);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RefreshSessionHistoryPersistence_TrimsInMemoryAndPersistedWindow()
    {
        using var httpClient = new HttpClient(new BlockingHueHandler());
        var service = CreateService(httpClient, persistSessionHistory: true);
        var configuration = Plugin.Instance!.Configuration;
        configuration.SessionHistoryRetentionCount = 2;

        var sessionHistory = Assert.IsType<List<HueSessionSummary>>(GetPrivateField(service, "_sessionHistory"));
        sessionHistory.AddRange(Enumerable.Range(0, 4).Select(index => new HueSessionSummary
        {
            Item = "Session " + index,
            Outcome = "Ended"
        }));

        service.RefreshSessionHistoryPersistence();

        Assert.Equal(2, service.GetSessionHistory().Count);
        Assert.Equal(new[] { "Session 0", "Session 1" }, service.GetSessionHistory().Select(summary => summary.Item));
        Assert.Equal(2, configuration.PersistedSessionHistory.Count);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SyncLoopEnd_RestoresLightsAndClearsRuntimeState()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);

        var syncCts = new CancellationTokenSource();
        SetPrivateField(service, "_syncCts", syncCts);
        SetPrivateField(service, "_currentPlaySessionId", "session-a");
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "app-key", "client-key", "area-id"));

        var loopMethod = typeof(HueSyncService).GetMethod("RunSyncLoop", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var loopTask = Assert.IsAssignableFrom<Task>(loopMethod.Invoke(service, new object?[]
        {
            new MemoryStream(),
            new Dictionary<int, (double x, double z)>(),
            "area-id",
            50,
            syncCts,
            "session-a"
        }));
        await loopTask;

        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handler.ReleaseStopRequest();
        await handler.StopRequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForRuntimeStatusAsync(
            service,
            "Idle",
            "Video stream ended; lights were restored.");
        await WaitForSyncStateClearedAsync(service);

        Assert.False(service.IsSyncing);
        Assert.Equal("Idle", service.GetRuntimeStatus().State);
        Assert.Equal("Video stream ended; lights were restored.", service.GetRuntimeStatus().Message);
        Assert.Null(GetPrivateField(service, "_syncCts"));
        Assert.Null(GetPrivateField(service, "_currentPlaySessionId"));
        Assert.Null(GetPrivateField(service, "_currentBridgeConfig"));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SyncLoopFailure_RestoresLightsAndPreservesErrorStatus()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);

        var syncCts = new CancellationTokenSource();
        SetPrivateField(service, "_syncCts", syncCts);
        SetPrivateField(service, "_currentPlaySessionId", "session-a");
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "app-key", "client-key", "area-id"));

        var loopMethod = typeof(HueSyncService).GetMethod("RunSyncLoop", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var loopTask = Assert.IsAssignableFrom<Task>(loopMethod.Invoke(service, new object?[]
        {
            new ThrowingReadStream(),
            new Dictionary<int, (double x, double z)>(),
            "area-id",
            50,
            syncCts,
            "session-a"
        }));
        await loopTask;

        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handler.ReleaseStopRequest();
        await handler.StopRequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForSyncStateClearedAsync(service);

        var status = service.GetRuntimeStatus();
        Assert.False(service.IsSyncing);
        Assert.Equal("Error", status.State);
        Assert.Equal("The video sync loop stopped unexpectedly.", status.LastError);
        Assert.Null(GetPrivateField(service, "_syncCts"));
        Assert.Null(GetPrivateField(service, "_currentPlaySessionId"));
        Assert.Null(GetPrivateField(service, "_currentBridgeConfig"));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SyncLoopStall_StopsAfterConfiguredTimeoutAndCleansUp()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);
        Plugin.Instance!.Configuration.FfmpegStallTimeoutSeconds = 1;
        await service.StartAsync(CancellationToken.None);

        var syncCts = new CancellationTokenSource();
        SetPrivateField(service, "_syncCts", syncCts);
        SetPrivateField(service, "_currentPlaySessionId", "session-a");
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "app-key", "client-key", "area-id"));

        var loopMethod = typeof(HueSyncService).GetMethod("RunSyncLoop", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var loopTask = Assert.IsAssignableFrom<Task>(loopMethod.Invoke(service, new object?[]
        {
            new BlockingReadStream(),
            new Dictionary<int, (double x, double z)> { [1] = (0, 0) },
            "area-id",
            50,
            syncCts,
            "session-a"
        }));
        await loopTask;

        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handler.ReleaseStopRequest();
        await handler.StopRequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForSyncStateClearedAsync(service);

        var status = service.GetRuntimeStatus();
        Assert.False(service.IsSyncing);
        Assert.Equal("Error", status.State);
        Assert.Equal("FFmpeg stopped producing video frames within the configured stall timeout.", status.LastError);
        Assert.Null(GetPrivateField(service, "_syncCts"));
        Assert.Null(GetPrivateField(service, "_currentPlaySessionId"));
        Assert.Null(GetPrivateField(service, "_currentBridgeConfig"));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SyncLoopSendFailures_StopAfterThresholdAndCleansUp()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);

        var syncCts = new CancellationTokenSource();
        SetPrivateField(service, "_syncCts", syncCts);
        SetPrivateField(service, "_currentPlaySessionId", "session-a");
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "app-key", "client-key", "area-id"));

        const int frameSize = 160 * 90 * 3;
        var loopMethod = typeof(HueSyncService).GetMethod("RunSyncLoop", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var loopTask = Assert.IsAssignableFrom<Task>(loopMethod.Invoke(service, new object?[]
        {
            new MemoryStream(new byte[frameSize * 5]),
            new Dictionary<int, (double x, double z)> { [1] = (0, 0) },
            "area-id",
            1,
            syncCts,
            "session-a"
        }));
        await loopTask;

        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handler.ReleaseStopRequest();
        await handler.StopRequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForSyncStateClearedAsync(service);

        var status = service.GetRuntimeStatus();
        Assert.False(service.IsSyncing);
        Assert.Equal("Error", status.State);
        Assert.Equal("The DTLS stream failed to send colors after repeated reconnect attempts.", status.LastError);
        Assert.Null(GetPrivateField(service, "_syncCts"));
        Assert.Null(GetPrivateField(service, "_currentPlaySessionId"));
        Assert.Null(GetPrivateField(service, "_currentBridgeConfig"));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartupFailure_RollsBackPublishedStateImmediately()
    {
        var handler = new BlockingHueHandler
        {
            ConfigurationJson = "{\"broken\":true}"
        };
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);
        Plugin.Instance!.Configuration.RestoreLightState = true;
        SetPrivateField(service, "_savedLightStates", new List<HueClient.LightState>
        {
            new("light-id", true, 50, 0.1, 0.2)
        });

        var startMethod = typeof(HueSyncService).GetMethod("StartSyncForItemCore", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var startTask = Assert.IsAssignableFrom<Task>(startMethod.Invoke(service, new object?[]
        {
            CreateProgress("session-a"),
            CancellationToken.None
        }));

        await handler.FirstConfigurationRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handler.ReleaseFirstConfiguration();
        await handler.RestorationRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Error", service.GetRuntimeStatus().State);
        handler.ReleaseStopRequest();
        await handler.StopRequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await startTask;
        await WaitForSyncStateClearedAsync(service);

        var status = service.GetRuntimeStatus();
        Assert.Equal("Error", status.State);
        Assert.Equal("Could not load the selected entertainment area configuration.", status.LastError);
        Assert.Null(GetPrivateField(service, "_syncCts"));
        Assert.Null(GetPrivateField(service, "_currentPlaySessionId"));
        Assert.Null(GetPrivateField(service, "_currentBridgeConfig"));
        Assert.Null(GetPrivateField(service, "_savedLightStates"));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task IncompleteLightCapture_DoesNotApplyCinemaModeDuringRollback()
    {
        var handler = new BlockingHueHandler
        {
            ConfigurationJson = "{\"data\":[{\"channels\":[{\"channel_id\":1,\"position\":{\"x\":0,\"z\":0},\"members\":[{\"service\":{\"rid\":\"light-id\"}}]}]}]}",
            FailLightCaptureRequests = true
        };
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);
        Plugin.Instance!.Configuration.RestoreLightState = true;
        Plugin.Instance.Configuration.UseCinemaMode = true;
        Plugin.Instance.Configuration.NetworkRetryAttempts = 0;
        ((HueClient)GetPrivateField(service, "_hueClient")!).RetryAttempts = 0;

        var startMethod = typeof(HueSyncService).GetMethod("StartSyncForItemCore", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var startTask = Assert.IsAssignableFrom<Task>(startMethod.Invoke(service, new object?[]
        {
            CreateProgress("capture-failure-session"),
            CancellationToken.None
        }));

        await handler.FirstConfigurationRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handler.ReleaseFirstConfiguration();
        await handler.LightCaptureRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cleanupSignal = await Task.WhenAny(
            handler.StopRequest.Task,
            handler.RestorationRequest.Task,
            startTask).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(
            ReferenceEquals(cleanupSignal, handler.StopRequest.Task),
            $"Unexpected cleanup state: stop={handler.StopRequest.Task.IsCompleted}, restoration={handler.RestorationRequest.Task.IsCompleted}, start={startTask.IsCompleted}");

        Assert.Equal(0, handler.StartAreaRequestCount);
        Assert.Contains("capture", service.GetRuntimeStatus().LastError, StringComparison.OrdinalIgnoreCase);

        handler.ReleaseStopRequest();
        await handler.StopRequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await startTask;
        await WaitForSyncStateClearedAsync(service);

        Assert.Equal(0, handler.StartAreaRequestCount);
        Assert.Null(GetPrivateField(service, "_savedLightStates"));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PlaybackStartup_IsBlockedWhileDiagnosticLeaseIsHeld()
    {
        var gate = new HueBridgeLifecycleGate();
        var diagnosticLease = gate.TryEnterDiagnostic();
        Assert.NotNull(diagnosticLease);

        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, gate);
        await service.StartAsync(CancellationToken.None);

        var startMethod = typeof(HueSyncService).GetMethod("StartSyncForItemCore", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var startTask = Assert.IsAssignableFrom<Task>(startMethod.Invoke(service, new object?[]
        {
            CreateProgress("diagnostic-blocked-session"),
            CancellationToken.None
        }));

        await startTask;
        Assert.False(handler.FirstConfigurationRequest.Task.IsCompleted);
        Assert.Contains("diagnostic", service.GetRuntimeStatus().LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Null(GetPrivateField(service, "_syncCts"));

        diagnosticLease!.Dispose();
        await service.StopAsync(CancellationToken.None);
    }

    private static async Task WaitForRuntimeStatusAsync(
        HueSyncService service,
        string expectedState,
        string expectedMessage)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var status = service.GetRuntimeStatus();
            if (string.Equals(status.State, expectedState, StringComparison.Ordinal) &&
                string.Equals(status.Message, expectedMessage, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(10);
        }

        var finalStatus = service.GetRuntimeStatus();
        Assert.Equal(expectedState, finalStatus.State);
        Assert.Equal(expectedMessage, finalStatus.Message);
    }

    private static async Task WaitForSyncStateClearedAsync(HueSyncService service)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (GetPrivateField(service, "_syncCts") == null &&
                GetPrivateField(service, "_currentPlaySessionId") == null &&
                GetPrivateField(service, "_currentBridgeConfig") == null)
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Null(GetPrivateField(service, "_syncCts"));
        Assert.Null(GetPrivateField(service, "_currentPlaySessionId"));
        Assert.Null(GetPrivateField(service, "_currentBridgeConfig"));
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
    public async Task StopAsync_RestoresSavedStateBeforeServiceShutdown()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);

        Plugin.Instance!.Configuration.RestoreLightState = true;
        SetPrivateField(service, "_savedLightStates", new List<HueClient.LightState>
        {
            new("light-id", true, 50, 0.1, 0.2)
        });
        SetPrivateField(service, "_syncCts", new CancellationTokenSource());
        SetPrivateField(service, "_currentPlaySessionId", "session-a");
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "app-key", "client-key", "area-id"));

        var stopTask = service.StopAsync(CancellationToken.None);
        await handler.RestorationRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handler.ReleaseStopRequest();
        await stopTask;

        Assert.Null(GetPrivateField(service, "_savedLightStates"));
        Assert.Equal("Idle", service.GetRuntimeStatus().State);
        Assert.Equal("Sync service stopped.", service.GetRuntimeStatus().Message);
    }

    [Fact]
    public async Task StopAsync_UsesActiveRestoreOverrideWhenGlobalRestorationIsDisabled()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);

        Plugin.Instance!.Configuration.RestoreLightState = false;
        Plugin.Instance.Configuration.UseCinemaMode = false;
        SetPrivateField(service, "_activeRestoreLightState", true);
        SetPrivateField(service, "_savedLightStates", new List<HueClient.LightState>
        {
            new("light-id", true, 50, 0.1, 0.2)
        });
        SetPrivateField(service, "_syncCts", new CancellationTokenSource());
        SetPrivateField(service, "_currentPlaySessionId", "session-a");
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "app-key", "client-key", "area-id"));

        var stopTask = service.StopAsync(CancellationToken.None);
        await handler.RestorationRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handler.ReleaseStopRequest();
        await stopTask;

        Assert.Null(GetPrivateField(service, "_savedLightStates"));
        Assert.Null(GetPrivateField(service, "_activeRestoreLightState"));
        Assert.Equal("Sync service stopped.", service.GetRuntimeStatus().Message);
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
    public async Task StopAsync_ReportsPartialLightRestorationWarning()
    {
        var handler = new BlockingHueHandler { FailRestorationRequests = true };
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);

        Plugin.Instance!.Configuration.RestoreLightState = true;
        ((HueClient)GetPrivateField(service, "_hueClient")!).RetryAttempts = 0;
        SetPrivateField(service, "_savedLightStates", new List<HueClient.LightState>
        {
            new("light-id", true, 50, 0.1, 0.2)
        });
        SetPrivateField(service, "_syncCts", new CancellationTokenSource());
        SetPrivateField(service, "_currentPlaySessionId", "session-a");
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "app-key", "client-key", "area-id"));

        var stopTask = service.StopAsync(CancellationToken.None);
        await handler.RestorationRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handler.ReleaseStopRequest();
        await stopTask;

        var status = service.GetRuntimeStatus();
        Assert.Contains("restored 0 of 1", status.CleanupWarning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 failed", status.CleanupWarning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisabledUserMappingSkipsSyncStartup()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);
        Plugin.Instance!.Configuration.UserMappings.Add(new UserBridgeMapping
        {
            UserId = Guid.Empty.ToString(),
            SyncEnabled = false
        });

        await service.StartAsync(CancellationToken.None);

        var startMethod = typeof(HueSyncService).GetMethod("OnPlaybackStart", BindingFlags.Instance | BindingFlags.NonPublic)!;
        startMethod.Invoke(service, new object?[] { null, CreateProgress("disabled-session") });

        var status = service.GetRuntimeStatus();
        Assert.False(status.IsSyncing);
        Assert.Equal("Idle", status.State);
        Assert.Equal("Sync is disabled for this user.", status.Message);
        Assert.Null(GetPrivateField(service, "_currentPlaySessionId"));
        Assert.False(handler.FirstConfigurationRequest.Task.IsCompleted);

        var progressMethod = typeof(HueSyncService).GetMethod("OnPlaybackProgress", BindingFlags.Instance | BindingFlags.NonPublic)!;
        progressMethod.Invoke(service, new object?[] { null, CreateProgress("disabled-session") });
        Assert.Null(GetPrivateField(service, "_currentPlaySessionId"));

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
        SetPrivateField(service, "_activeRestoreLightState", true);
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
        await WaitForRuntimeStatusAsync(
            service,
            "Paused",
            "Playback paused; keeping the last synced colors.");
        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));

        progressMethod.Invoke(service, new object?[] { null, CreateProgress("session-a") });
        Assert.False(handler.FirstConfigurationRequest.Task.IsCompleted);

        handler.ReleaseStopRequest();
        handler.ReleaseFirstConfiguration();
        await handler.FirstConfigurationRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PlaybackPause_WithActiveRestoreBehaviorRestoresSavedStateImmediately()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);

        Plugin.Instance!.Configuration.RestoreLightState = true;
        Plugin.Instance.Configuration.PauseBehavior = PluginConfiguration.PauseBehaviorKeepLastColors;
        SetPrivateField(service, "_activePauseBehavior", PluginConfiguration.PauseBehaviorRestoreLightState);
        SetPrivateField(service, "_savedLightStates", new List<HueClient.LightState>
        {
            new("light-id", true, 50, 0.1, 0.2)
        });
        SetPrivateField(service, "_syncCts", new CancellationTokenSource());
        SetPrivateField(service, "_currentPlaySessionId", "session-a");
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "app-key", "client-key", "area-id"));
        SetPrivateField(service, "_syncStartTime", DateTime.UtcNow.AddSeconds(-10));
        SetPrivateField(service, "_currentItemName", "Test item");

        var progressMethod = typeof(HueSyncService).GetMethod("OnPlaybackProgress", BindingFlags.Instance | BindingFlags.NonPublic)!;
        progressMethod.Invoke(service, new object?[] { null, CreateProgress("session-a", isPaused: true) });

        await handler.RestorationRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handler.ReleaseStopRequest();
        await handler.StopRequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForRuntimeStatusAsync(
            service,
            "Paused",
            "Playback paused; original light state restored.");

        Assert.Null(GetPrivateField(service, "_savedLightStates"));
        Assert.Null(GetPrivateField(service, "_currentBridgeConfig"));
        Assert.Null(GetPrivateField(service, "_activePauseBehavior"));
        Assert.Equal("Test item", service.GetRuntimeStatus().CurrentItem);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PlaybackPause_WithDimBehaviorAppliesCinemaLevelAndKeepsLifecycleForResume()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);

        Plugin.Instance!.Configuration.PauseBehavior = PluginConfiguration.PauseBehaviorDimToCinemaLevel;
        Plugin.Instance.Configuration.BrightnessDimLevel = 25;
        SetPrivateField(service, "_activePauseBehavior", PluginConfiguration.PauseBehaviorDimToCinemaLevel);
        SetPrivateField(service, "_activePauseBrightnessPercent", 25);
        SetPrivateField(service, "_savedLightStates", new List<HueClient.LightState>
        {
            new("light-id", true, 50, 0.1, 0.2)
        });
        SetPrivateField(service, "_syncCts", new CancellationTokenSource());
        SetPrivateField(service, "_currentPlaySessionId", "session-a");
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "app-key", "client-key", "area-id"));
        SetPrivateField(service, "_syncStartTime", DateTime.UtcNow.AddSeconds(-10));

        var progressMethod = typeof(HueSyncService).GetMethod("OnPlaybackProgress", BindingFlags.Instance | BindingFlags.NonPublic)!;
        progressMethod.Invoke(service, new object?[] { null, CreateProgress("session-a", isPaused: true) });

        await handler.BrightnessRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(handler.LastLightPutBody);
        Assert.Contains("\"brightness\":25", handler.LastLightPutBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("\"color\"", handler.LastLightPutBody!, StringComparison.Ordinal);
        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handler.ReleaseStopRequest();
        await handler.StopRequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForRuntimeStatusAsync(
            service,
            "Paused",
            "Playback paused; lights dimmed to 25%.");

        Assert.NotNull(GetPrivateField(service, "_savedLightStates"));
        Assert.NotNull(GetPrivateField(service, "_currentBridgeConfig"));
        Assert.Equal(PluginConfiguration.PauseBehaviorDimToCinemaLevel, service.GetRuntimeStatus().ActivePauseBehavior);
        Assert.Equal(25, service.GetRuntimeStatus().ActivePauseBrightnessPercent);

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

    private static HueSyncService CreateService(
        HttpClient httpClient,
        HueBridgeLifecycleGate? bridgeLifecycleGate = null,
        ISessionManager? sessionManager = null,
        bool persistSessionHistory = false)
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
        configuration.PersistSessionHistory = persistSessionHistory;

        return new HueSyncService(
            sessionManager ?? Mock.Of<ISessionManager>(),
            Mock.Of<ILogger<HueSyncService>>(),
            loggerFactory.Object,
            hueClient,
            Mock.Of<IMediaEncoder>(),
            bridgeLifecycleGate);
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

    private sealed class ThrowingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("Synthetic frame-read failure.");

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException<int>(new IOException("Synthetic frame-read failure."));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingHueHandler : HttpMessageHandler
    {
        public string ConfigurationJson { get; set; } = "{\"data\":[{\"channels\":[]}]}";
        public TaskCompletionSource<bool> FirstConfigurationRequest { get; } = NewSignal();
        public TaskCompletionSource<bool> SecondConfigurationRequest { get; } = NewSignal();
        public TaskCompletionSource<bool> StopRequest { get; } = NewSignal();
        public TaskCompletionSource<bool> StopRequestCompleted { get; } = NewSignal();
        public TaskCompletionSource<bool> RestorationRequest { get; } = NewSignal();
        public TaskCompletionSource<bool> BrightnessRequest { get; } = NewSignal();
        public string? LastLightPutBody { get; private set; }
        public bool FailRestorationRequests { get; set; }
        public bool FailLightCaptureRequests { get; set; }
        public TaskCompletionSource<bool> LightCaptureRequest { get; } = NewSignal();
        public int StartAreaRequestCount { get; private set; }

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

                return ConfigurationResponse(ConfigurationJson);
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath.Contains("/light/", StringComparison.Ordinal) == true)
            {
                LightCaptureRequest.TrySetResult(true);
                if (FailLightCaptureRequests)
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            if (request.Method == HttpMethod.Put)
            {
                var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
                if (body.Contains("\"start\"", StringComparison.Ordinal))
                    StartAreaRequestCount++;
                if (request.RequestUri?.AbsolutePath.Contains("/light/", StringComparison.Ordinal) == true)
                {
                    LastLightPutBody = body;
                    RestorationRequest.TrySetResult(true);
                    if (body.Contains("\"dimming\"", StringComparison.Ordinal) &&
                        !body.Contains("\"color\"", StringComparison.Ordinal) &&
                        !body.Contains("\"color_temperature\"", StringComparison.Ordinal))
                    {
                        BrightnessRequest.TrySetResult(true);
                    }
                    if (FailRestorationRequests)
                    {
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    }
                }
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

        private static HttpResponseMessage ConfigurationResponse(string configurationJson)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    configurationJson,
                    Encoding.UTF8,
                    "application/json")
            };
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
