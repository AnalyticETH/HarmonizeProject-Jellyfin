using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    public void PlaybackStopEventHandler_ObservesTaskInsteadOfAsyncVoid()
    {
        var method = typeof(HueSyncService).GetMethod("OnPlaybackStopped", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        Assert.Equal(typeof(void), method!.ReturnType);
        Assert.Null(method.GetCustomAttribute<AsyncStateMachineAttribute>());
    }

    [Fact]
    public void Constructor_IsolatesPlaybackRetryPolicyFromInjectedClient()
    {
        using var httpClient = new HttpClient(new BlockingHueHandler());
        var injectedClient = new HueClient(httpClient, Mock.Of<ILogger<HueClient>>())
        {
            RetryAttempts = 7
        };

        var service = new HueSyncService(
            Mock.Of<ISessionManager>(),
            Mock.Of<ILogger<HueSyncService>>(),
            Mock.Of<ILoggerFactory>(),
            injectedClient,
            Mock.Of<IMediaEncoder>());

        var serviceClient = Assert.IsType<HueClient>(GetPrivateField(service, "_hueClient"));
        Assert.NotSame(injectedClient, serviceClient);

        serviceClient.RetryAttempts = 1;

        Assert.Equal(7, injectedClient.RetryAttempts);
    }

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
        SetPrivateField(service, "_currentSpatialOrientation", PluginConfiguration.SpatialOrientationRotate180);
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
        SetPrivateField(service, "_currentPlaybackMediaFilter", PluginConfiguration.PlaybackMediaFilterEpisodes);
        SetPrivateField(service, "_currentUserName", "Living Room Viewer");
        SetPrivateField(service, "_currentDeviceId", "device-living-room");
        SetPrivateField(service, "_currentDeviceName", "Living Room TV");
        SetPrivateField(service, "_currentDeviceRouteMatched", true);
        SetPrivateField(service, "_currentItemName", "Feature film");
        SetPrivateField(service, "_syncStartTime", DateTime.UtcNow.AddSeconds(-3));
        SetPrivateField(service, "_seekRestartCount", 2);
        SetPrivateField(service, "_lastSeekPositionSeconds", 142.5);
        SetPrivateField(service, "_runtimeState", "Syncing");
        SetPrivateField(service, "_runtimeMessage", "Streaming video colors to Hue.");
        SetPrivateField(service, "_currentPlaySessionId", "timeline-session");
        SetPrivateField(service, "_currentPlaybackPositionTicks", TimeSpan.FromSeconds(142.5).Ticks);
        SetPrivateField(service, "_currentPlaybackDurationTicks", TimeSpan.FromMinutes(120).Ticks);
        SetPrivateField(service, "_currentPlaybackIsPaused", true);
        var playbackObservedAtUtc = DateTime.UtcNow.AddSeconds(-1);
        SetPrivateField(service, "_currentPlaybackObservedAtUtc", playbackObservedAtUtc);

        // Runtime status must report the policy captured for this playback session,
        // even when an administrator changes the global or per-user scope afterward.
        Plugin.Instance.Configuration.PlaybackMediaFilter = PluginConfiguration.PlaybackMediaFilterAudio;
        Plugin.Instance.Configuration.UserMappings[0].PlaybackMediaFilterOverride = PluginConfiguration.PlaybackMediaFilterMovies;
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
        Assert.Equal("device-living-room", status.ActiveDeviceId);
        Assert.Equal("Living Room TV", status.ActiveDeviceName);
        Assert.Equal((bool?)true, status.ActiveDeviceRouteMatched);
        Assert.Equal(PluginConfiguration.PlaybackMediaFilterEpisodes, status.ActivePlaybackMediaFilter);
        Assert.Equal(PluginConfiguration.FrameResolutionHigh, status.ActiveFrameResolution);
        Assert.Equal(PluginConfiguration.VideoScalingModeFit, status.ActiveVideoScalingMode);
        Assert.Equal(PluginConfiguration.VideoDeinterlaceModeAuto, status.ActiveVideoDeinterlaceMode);
        Assert.Equal(30, status.ActiveTargetFps);
        Assert.Equal(25, status.ActiveSamplingBreadthPercent);
        Assert.Equal(PluginConfiguration.SamplingModeCenterWeighted, status.ActiveSamplingMode);
        Assert.Equal(PluginConfiguration.SpatialOrientationRotate180, status.ActiveSpatialOrientation);
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
        Assert.Equal(142.5, status.PlaybackPositionSeconds);
        Assert.Equal(7200, status.PlaybackDurationSeconds);
        Assert.True(Math.Abs((status.PlaybackProgressPercent ?? 0) - 1.9791666666666667) < 0.0001);
        Assert.True(status.PlaybackIsPaused);
        Assert.Equal(playbackObservedAtUtc, status.PlaybackObservedAtUtc);
        Assert.True(status.SyncDurationSeconds >= 2);
        Assert.DoesNotContain("secret-app-key", status.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-client-key", status.Message, StringComparison.Ordinal);

        // Keep shutdown focused on local resources for this snapshot test.
        SetPrivateField(service, "_bridgeAreaDeactivated", true);
        await service.StopAsync(CancellationToken.None);
        var stoppedStatus = service.GetRuntimeStatus();
        Assert.Null(stoppedStatus.ActiveUserId);
        Assert.Null(stoppedStatus.ActiveUserName);
        Assert.Null(stoppedStatus.ActiveDeviceId);
        Assert.Null(stoppedStatus.ActiveDeviceName);
        Assert.Null(stoppedStatus.ActiveDeviceRouteMatched);
        Assert.Null(stoppedStatus.ActivePlaybackMediaFilter);
        Assert.Equal(0, stoppedStatus.SeekRestartCount);
        Assert.Null(stoppedStatus.LastSeekPositionSeconds);
        Assert.Null(stoppedStatus.PlaybackPositionSeconds);
        Assert.Null(stoppedStatus.PlaybackDurationSeconds);
        Assert.Null(stoppedStatus.PlaybackProgressPercent);
        Assert.Null(stoppedStatus.PlaybackIsPaused);
        Assert.Null(stoppedStatus.PlaybackObservedAtUtc);
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
    public async Task StartAsync_RecoversAnAlreadyPlayingAudioSession()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var sessionManager = new Mock<ISessionManager>();
        var activeSession = new SessionInfo(sessionManager.Object, Mock.Of<ILogger>())
        {
            Id = "client-audio-session-1",
            UserId = Guid.NewGuid(),
            UserName = "Already Playing Listener",
            LastActivityDate = DateTime.UtcNow,
            FullNowPlayingItem = new MediaBrowser.Controller.Entities.Audio.Audio
            {
                Name = "Already playing album track",
                Path = "/tmp/already-playing-track.flac"
            },
            PlayState = new PlayerStateInfo
            {
                IsPaused = false,
                PositionTicks = TimeSpan.FromSeconds(19).Ticks
            }
        };
        sessionManager
            .SetupGet(manager => manager.Sessions)
            .Returns(new[] { activeSession });
        var service = CreateService(httpClient, sessionManager: sessionManager.Object);
        Plugin.Instance!.Configuration.PlaybackMediaFilter = PluginConfiguration.PlaybackMediaFilterAudio;

        await service.StartAsync(CancellationToken.None);
        await handler.FirstConfigurationRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var status = service.GetRuntimeStatus();
        Assert.True(status.IsSyncing);
        Assert.Equal("Already playing album track", status.CurrentItem);
        Assert.Equal("Already Playing Listener", status.ActiveUserName);
        Assert.Equal(19, status.PlaybackPositionSeconds);
        Assert.Equal(PluginConfiguration.PlaybackMediaFilterAudio, status.ActivePlaybackMediaFilter);

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
        var gate = new HueBridgeLifecycleGate();
        var service = CreateService(httpClient, gate, persistSessionHistory: true);
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
            true,
            CancellationToken.None
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

        using var playbackLease = gate.TryEnterPlayback("192.168.1.10|living-room");
        using var schedulerLease = gate.TryEnterSchedulerEvaluation();
        Assert.NotNull(playbackLease);
        Assert.NotNull(schedulerLease);
        Assert.Equal(1, service.ClearSessionHistory());
        Assert.Empty(service.GetSessionHistory());
        Assert.Null(service.GetRuntimeStatus().LastSession);
        Assert.Empty(Plugin.Instance!.Configuration.PersistedSessionHistory);

        schedulerLease!.Dispose();
        playbackLease!.Dispose();
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
    public async Task AudioSyncLoopEnd_ReportsAudioStreamStatus()
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

        var finalizeMethod = typeof(HueSyncService).GetMethod(
            "FinalizeSyncLoopAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var finalizeTask = Assert.IsAssignableFrom<Task>(finalizeMethod.Invoke(service, new object?[]
        {
            syncCts.Token,
            syncCts,
            "session-a",
            true,
            true
        }));

        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handler.ReleaseStopRequest();
        await handler.StopRequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await finalizeTask;
        await WaitForRuntimeStatusAsync(
            service,
            "Idle",
            "Audio stream ended; lights were restored.");

        Assert.Equal("Audio stream ended; lights were restored.", service.GetRuntimeStatus().Message);
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
    public async Task StopCurrentSync_WithUnknownSessionIdDoesNotStopPrimarySession()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);

        await service.StartAsync(CancellationToken.None);
        using var syncCts = new CancellationTokenSource();
        SetPrivateField(service, "_syncCts", syncCts);
        SetPrivateField(service, "_currentPlaySessionId", "primary-session");
        SetPrivateField(service, "_currentItemName", "Feature film");
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "app-key", "client-key", "area-id"));
        SetPrivateField(service, "_runtimeState", "Syncing");
        SetPrivateField(service, "_runtimeMessage", "Streaming video colors to Hue.");

        Assert.False(await service.StopCurrentSyncAsync("stale-worker-session"));

        Assert.Same(syncCts, GetPrivateField(service, "_syncCts"));
        Assert.False(syncCts.IsCancellationRequested);
        Assert.Equal("primary-session", GetPrivateField(service, "_currentPlaySessionId"));
        Assert.Equal(
            ("192.168.1.100", "app-key", "client-key", "area-id"),
            GetPrivateField(service, "_currentBridgeConfig"));
        Assert.Null(GetPrivateField(service, "_manuallyStoppedPlaySessionId"));
        Assert.Equal("Syncing", service.GetRuntimeStatus().State);
        Assert.True(service.CanStopSync);
        Assert.False(handler.StopRequest.Task.IsCompleted);

        // Avoid making service shutdown perform bridge cleanup for this synthetic state.
        SetPrivateField(service, "_bridgeAreaDeactivated", true);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopCurrentSync_WithMatchingPrimarySessionIdStopsPrimarySession()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);

        await service.StartAsync(CancellationToken.None);
        SetPrivateField(service, "_syncCts", new CancellationTokenSource());
        SetPrivateField(service, "_currentPlaySessionId", "primary-session");
        SetPrivateField(service, "_currentItemName", "Feature film");
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "app-key", "client-key", "area-id"));
        SetPrivateField(service, "_syncStartTime", DateTime.UtcNow.AddSeconds(-10));

        var stopTask = service.StopCurrentSyncAsync("primary-session");
        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(service.CanStopSync);

        handler.ReleaseStopRequest();
        Assert.True(await stopTask);
        Assert.False(service.IsSyncing);
        Assert.False(service.CanStopSync);
        Assert.Equal("Stopped", service.GetRuntimeStatus().State);

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

        var deferredStopTask = Assert.IsAssignableFrom<Task>(GetPrivateField(service, "_deferredStopTask"));
        await deferredStopTask.WaitAsync(TimeSpan.FromSeconds(5));

        var status = service.GetRuntimeStatus();
        Assert.Contains("restored 0 of 1", status.CleanupWarning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 failed", status.CleanupWarning, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(GetPrivateField(service, "_savedLightStates"));
        Assert.NotNull(GetPrivateField(service, "_currentBridgeConfig"));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PlaybackStop_WhenRestorationFailsOnce_RetainsSnapshotUntilDeferredRetry()
    {
        var handler = new BlockingHueHandler
        {
            FailFirstRestorationRequest = true,
            BlockSecondRestorationRequest = true
        };
        using var httpClient = new HttpClient(handler);
        var lifecycleGate = new HueBridgeLifecycleGate();
        var service = CreateService(httpClient, lifecycleGate);
        await service.StartAsync(CancellationToken.None);

        Plugin.Instance!.Configuration.RestoreLightState = true;
        ((HueClient)GetPrivateField(service, "_hueClient")!).RetryAttempts = 0;
        var savedLightStates = new List<HueClient.LightState>
        {
            new("light-id", true, 50, 0.1, 0.2)
        };
        SetPrivateField(service, "_savedLightStates", savedLightStates);
        SetPrivateField(service, "_activeRestoreLightState", true);
        SetPrivateField(service, "_syncCts", new CancellationTokenSource());
        SetPrivateField(service, "_currentPlaySessionId", "session-a");
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "app-key", "client-key", "area-id"));
        var resourceKey = HueSyncService.GetPlaybackResourceKey("192.168.1.100", "area-id");
        var playbackLease = lifecycleGate.TryEnterPlayback(resourceKey);
        Assert.NotNull(playbackLease);
        SetPrivateField(service, "_playbackLifecycleLease", playbackLease);

        var stopMethod = typeof(HueSyncService).GetMethod(
            "HandlePlaybackStoppedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var stopTask = Assert.IsAssignableFrom<Task>(stopMethod.Invoke(service, new object?[]
        {
            CreateStop("session-a")
        }));

        await handler.RestorationRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var startMethod = typeof(HueSyncService).GetMethod("OnPlaybackStart", BindingFlags.Instance | BindingFlags.NonPublic)!;
        startMethod.Invoke(service, new object?[] { null, CreateProgress("session-b") });
        handler.ReleaseStopRequest();
        await handler.StopRequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await handler.SecondRestorationRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(savedLightStates, GetPrivateField(service, "_savedLightStates"));
        Assert.True((bool)GetPrivateField(service, "_activeRestoreLightState")!);
        var diagnosticLease = lifecycleGate.TryEnterDiagnostic(resourceKey, out var blockedByPlayback);
        Assert.Null(diagnosticLease);
        Assert.True(blockedByPlayback);
        var deferredCleanupTask = Assert.IsAssignableFrom<Task>(GetPrivateField(service, "_deferredPlaybackCleanupTask"));

        handler.ReleaseSecondRestorationRequest();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        await deferredCleanupTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, handler.RestorationRequestCount);
        Assert.Null(GetPrivateField(service, "_savedLightStates"));
        Assert.Null(GetPrivateField(service, "_currentBridgeConfig"));
        Assert.Null(GetPrivateField(service, "_activeRestoreLightState"));
        Assert.False((bool)GetPrivateField(service, "_playbackCleanupRetryPending")!);
        Assert.False(handler.FirstConfigurationRequest.Task.IsCompleted);
        Assert.False(lifecycleGate.IsPlaybackActiveForResource(resourceKey));

        await service.StopAsync(CancellationToken.None);
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
    public async Task PlaybackStart_WithNullItem_SkipsUnsupportedPlaybackWithoutThrowing()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);

        await service.StartAsync(CancellationToken.None);

        var startMethod = typeof(HueSyncService).GetMethod("OnPlaybackStart", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var playbackStart = new PlaybackProgressEventArgs
        {
            Item = null,
            PlaySessionId = "null-item-session"
        };

        var exception = Record.Exception(() => startMethod.Invoke(service, new object?[] { null, playbackStart }));

        Assert.Null(exception);
        var status = service.GetRuntimeStatus();
        Assert.False(status.IsSyncing);
        Assert.Equal("Idle", status.State);
        Assert.Equal(
            "Hue Sync supports video or audio playback, depending on the selected media scope.",
            status.Message);
        Assert.Null(GetPrivateField(service, "_currentPlaySessionId"));
        Assert.False(handler.FirstConfigurationRequest.Task.IsCompleted);

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
    public async Task ConcurrentPlaybackLookup_DoesNotUseClientSessionForUnknownPlaySession()
    {
        using var httpClient = new HttpClient(new ImmediateHueHandler());
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);

        var workers = GetConcurrentPlaybackWorkers(service);
        workers.Add(
            "old-play-session",
            CreateSyntheticConcurrentWorker("old-play-session", "client-session", "bridge|area"));

        var lookup = typeof(HueSyncService)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "TryGetConcurrentPlaybackWorker" &&
                              method.GetParameters().Length == 3 &&
                              method.GetParameters()[0].ParameterType == typeof(string));
        var arguments = new object?[] { "new-play-session", "client-session", null };

        var found = (bool)lookup.Invoke(service, arguments)!;

        Assert.False(found);
        Assert.Null(arguments[2]);
        Assert.Single(workers);

        workers.Clear();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConcurrentPlaybackStart_AllowsNewPlaySessionFromSameClientOnNewTarget()
    {
        using var httpClient = new HttpClient(new ImmediateHueHandler());
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);

        var userId = Guid.NewGuid();
        Plugin.Instance!.Configuration.UserMappings.Add(new UserBridgeMapping
        {
            UserId = userId.ToString(),
            DeviceTargets = new List<UserDeviceBridgeTarget>
            {
                new()
                {
                    DeviceId = "device-old",
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "old-app-key",
                    HueClientKey = "old-client-key",
                    EntertainmentAreaId = "old-area"
                },
                new()
                {
                    DeviceId = "device-new",
                    HueBridgeIp = "192.168.1.102",
                    HueAppKey = "new-app-key",
                    HueClientKey = "new-client-key",
                    EntertainmentAreaId = "new-area"
                }
            }
        });
        SetPrivateField(service, "_currentPlaySessionId", "primary-play-session");
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "app-key", "client-key", "primary-area"));

        var workers = GetConcurrentPlaybackWorkers(service);
        workers.Add(
            "old-play-session",
            CreateSyntheticConcurrentWorker(
                "old-play-session",
                "client-session",
                HueSyncService.GetPlaybackResourceKey("192.168.1.101", "old-area")));

        var session = new SessionInfo(Mock.Of<ISessionManager>(), Mock.Of<ILogger>())
        {
            Id = "client-session",
            UserId = userId,
            DeviceId = "device-new",
            DeviceName = "New playback device"
        };
        var progress = CreateProgress("new-play-session");
        progress.Session = session;
        var start = typeof(HueSyncService).GetMethod(
            "TryStartConcurrentPlayback",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        var handled = (bool)start.Invoke(service, new object?[]
        {
            progress,
            PluginConfiguration.PlaybackMediaFilterAllVideo
        })!;

        Assert.True(handled);
        Assert.True(workers.Contains("old-play-session"));
        Assert.True(workers.Contains("new-play-session"));
        Assert.Equal(2, workers.Count);

        // The synthetic predecessor is only test scaffolding and has no child service;
        // remove it before normal hosted-service cleanup stops the newly created worker.
        workers.Remove("old-play-session");
        SetPrivateField(service, "_bridgeAreaDeactivated", true);
        SetPrivateField(service, "_currentBridgeConfig", null);
        SetPrivateField(service, "_currentPlaySessionId", null);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PlaybackStart_CapturesMediaScopeForRuntimeStatus()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);
        Plugin.Instance!.Configuration.PlaybackMediaFilter = PluginConfiguration.PlaybackMediaFilterAllVideo;

        await service.StartAsync(CancellationToken.None);

        var onStartMethod = typeof(HueSyncService).GetMethod("OnPlaybackStart", BindingFlags.Instance | BindingFlags.NonPublic)!;
        onStartMethod.Invoke(service, new object?[] { null, CreateProgress("scope-session") });
        await handler.FirstConfigurationRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Changing the administrator policy must not rewrite the policy captured by
        // the active playback session or the status snapshot shown to the administrator.
        Plugin.Instance.Configuration.PlaybackMediaFilter = PluginConfiguration.PlaybackMediaFilterAudio;
        var status = service.GetRuntimeStatus();
        Assert.True(status.IsSyncing);
        Assert.Equal(PluginConfiguration.PlaybackMediaFilterAllVideo, status.ActivePlaybackMediaFilter);

        Plugin.Instance.Configuration.PlaybackMediaFilter = PluginConfiguration.PlaybackMediaFilterAllVideo;
        handler.ReleaseFirstConfiguration();
        handler.ReleaseStopRequest();
        await service.StopAsync(CancellationToken.None);
        Assert.Null(GetPrivateField(service, "_currentPlaybackMediaFilter"));
    }

    [Fact]
    public async Task SameTargetPlaybackReplacement_ReusesLeaseDuringTransition()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var gate = new HueBridgeLifecycleGate();
        var service = CreateService(httpClient, gate);
        await service.StartAsync(CancellationToken.None);

        const string bridgeIp = "192.168.1.100";
        const string areaId = "area-id";
        var playbackLease = gate.TryEnterPlayback(HueSyncService.GetPlaybackResourceKey(bridgeIp, areaId));
        Assert.NotNull(playbackLease);
        SetPrivateField(service, "_playbackLifecycleLease", playbackLease);
        SetPrivateField(service, "_currentPlaySessionId", "session-old");
        SetPrivateField(
            service,
            "_currentBridgeConfig",
            new ValueTuple<string, string, string, string>(bridgeIp, "app-key", "client-key", areaId));
        SetPrivateField(service, "_bridgeAreaDeactivated", true);

        var startMethod = typeof(HueSyncService).GetMethod(
            "StartSyncForItemCore",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var startTask = Assert.IsAssignableFrom<Task>(startMethod.Invoke(service, new object?[]
        {
            CreateProgress("session-new"),
            CancellationToken.None
        }));

        // The replacement must get past lifecycle arbitration while the old lease is
        // still held. The original ordering rejected the replacement before stopping
        // the predecessor and never issued this configuration request.
        await handler.FirstConfigurationRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("session-new", GetPrivateField(service, "_currentPlaySessionId"));
        Assert.True(gate.IsPlaybackActiveForResource(HueSyncService.GetPlaybackResourceKey(bridgeIp, areaId)));

        handler.ReleaseFirstConfiguration();
        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handler.ReleaseStopRequest();
        await startTask;

        Assert.False(gate.IsPlaybackActive);
        Assert.Null(GetPrivateField(service, "_playbackLifecycleLease"));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PlaybackResourceKey_UsesPinnedBridgeIdentityAcrossIpAndLocalAliases()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);

        const string bridgeFingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string otherBridgeFingerprint = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        Plugin.Instance!.Configuration.HueBridgeCertificatePins = new Dictionary<string, string>
        {
            ["192.168.1.100"] = bridgeFingerprint,
            ["hue-bridge.local"] = bridgeFingerprint,
            ["192.168.1.101"] = otherBridgeFingerprint
        };

        var ipKey = HueSyncService.GetPlaybackResourceKey("192.168.1.100", "area-id");
        var localKey = HueSyncService.GetPlaybackResourceKey("hue-bridge.local", "area-id");
        var otherBridgeKey = HueSyncService.GetPlaybackResourceKey("192.168.1.101", "area-id");

        Assert.Equal(ipKey, localKey);
        Assert.NotEqual(ipKey, otherBridgeKey);

        var gate = new HueBridgeLifecycleGate();
        using var ipLease = gate.TryEnterPlayback(ipKey);
        Assert.NotNull(ipLease);
        Assert.Null(gate.TryEnterPlayback(localKey));
        using var otherBridgeLease = gate.TryEnterPlayback(otherBridgeKey);
        Assert.NotNull(otherBridgeLease);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SameTargetPlaybackReplacement_PreservesSavedLightStateSnapshot()
    {
        var handler = new BlockingHueHandler
        {
            ConfigurationJson = "{\"data\":[{\"channels\":[{\"channel_id\":1,\"position\":{\"x\":0,\"z\":0},\"members\":[{\"service\":{\"rid\":\"light-id\"}}]}]}]}"
        };
        using var httpClient = new HttpClient(handler);
        var gate = new HueBridgeLifecycleGate();
        var service = CreateService(httpClient, gate);
        await service.StartAsync(CancellationToken.None);

        Plugin.Instance!.Configuration.RestoreLightState = true;
        Plugin.Instance.Configuration.UseCinemaMode = false;
        var savedLightStates = new List<HueClient.LightState>
        {
            new("light-id", true, 50, 0.1, 0.2)
        };
        SetPrivateField(service, "_savedLightStates", savedLightStates);
        SetPrivateField(service, "_savedLightStatePlaySessionId", "session-old");
        var playbackLease = gate.TryEnterPlayback(HueSyncService.GetPlaybackResourceKey("192.168.1.100", "area-id"));
        Assert.NotNull(playbackLease);
        SetPrivateField(service, "_playbackLifecycleLease", playbackLease);
        SetPrivateField(service, "_currentPlaySessionId", "session-old");
        SetPrivateField(
            service,
            "_currentBridgeConfig",
            new ValueTuple<string, string, string, string>("192.168.1.100", "app-key", "client-key", "area-id"));
        SetPrivateField(service, "_bridgeAreaDeactivated", true);

        using var startupCancellation = new CancellationTokenSource();
        var startMethod = typeof(HueSyncService).GetMethod(
            "StartSyncForItemCore",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var startTask = Assert.IsAssignableFrom<Task>(startMethod.Invoke(service, new object?[]
        {
            CreateProgress("session-new"),
            startupCancellation.Token
        }));

        await handler.FirstConfigurationRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(savedLightStates, GetPrivateField(service, "_savedLightStates"));
        Assert.Equal("session-new", GetPrivateField(service, "_savedLightStatePlaySessionId"));

        handler.ReleaseFirstConfiguration();
        await Task.Delay(100);
        Assert.False(handler.LightCaptureRequest.Task.IsCompleted);

        startupCancellation.Cancel();
        await handler.RestorationRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handler.ReleaseStopRequest();
        await startTask;

        Assert.Null(GetPrivateField(service, "_savedLightStates"));
        Assert.False(gate.IsPlaybackActive);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PlaybackProgressAfterNaturalStopDoesNotRestartSync()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);

        await service.StartAsync(CancellationToken.None);
        SetPrivateField(service, "_syncCts", new CancellationTokenSource());
        SetPrivateField(service, "_currentPlaySessionId", "session-a");

        var stopMethod = typeof(HueSyncService).GetMethod(
            "HandlePlaybackStoppedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var stopTask = Assert.IsAssignableFrom<Task>(stopMethod.Invoke(
            service,
            new object?[] { CreateStop("session-a") }));
        await stopTask;

        Assert.Null(GetPrivateField(service, "_currentPlaySessionId"));
        Assert.Null(GetPrivateField(service, "_playbackStopInFlightSessionId"));

        var progressMethod = typeof(HueSyncService).GetMethod(
            "OnPlaybackProgress",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        progressMethod.Invoke(service, new object?[] { null, CreateProgress("session-a") });

        Assert.Null(GetPrivateField(service, "_currentPlaySessionId"));
        Assert.Null(GetPrivateField(service, "_syncCts"));
        Assert.False(handler.FirstConfigurationRequest.Task.IsCompleted);

        // If a regression queues a startup, release its blocking request before teardown.
        handler.ReleaseFirstConfiguration();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PlaybackProgressDuringNaturalStopDoesNotQueueRestart()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);

        await service.StartAsync(CancellationToken.None);
        SetPrivateField(service, "_syncCts", new CancellationTokenSource());
        SetPrivateField(service, "_currentPlaySessionId", "session-a");

        var lifecycleLock = Assert.IsType<SemaphoreSlim>(GetPrivateField(service, "_syncLifecycleLock"));
        await lifecycleLock.WaitAsync();
        Task? stopTask = null;
        try
        {
            var stopMethod = typeof(HueSyncService).GetMethod(
                "HandlePlaybackStoppedAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            stopTask = Assert.IsAssignableFrom<Task>(stopMethod.Invoke(
                service,
                new object?[] { CreateStop("session-a") }));

            Assert.True(SpinWait.SpinUntil(
                () => string.Equals(
                    (string?)GetPrivateField(service, "_playbackStopInFlightSessionId"),
                    "session-a",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(5)));

            var progressMethod = typeof(HueSyncService).GetMethod(
                "OnPlaybackProgress",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            progressMethod.Invoke(service, new object?[] { null, CreateProgress("session-a") });

            Assert.Equal("session-a", GetPrivateField(service, "_currentPlaySessionId"));
            Assert.Null(GetPrivateField(service, "_startingPlaySessionId"));
            Assert.False(handler.FirstConfigurationRequest.Task.IsCompleted);
        }
        finally
        {
            lifecycleLock.Release();
        }

        Assert.NotNull(stopTask);
        await stopTask!;
        Assert.Null(GetPrivateField(service, "_playbackStopInFlightSessionId"));
        Assert.Null(GetPrivateField(service, "_currentPlaySessionId"));
        await service.StopAsync(CancellationToken.None);
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
        Assert.True(SpinWait.SpinUntil(
            () => GetPrivateField(service, "_savedLightStates") == null &&
                  GetPrivateField(service, "_currentBridgeConfig") == null &&
                  GetPrivateField(service, "_currentPlaySessionId") == null,
            TimeSpan.FromSeconds(5)));
        Assert.Null(GetPrivateField(service, "_syncCts"));
        Assert.Null(GetPrivateField(service, "_currentPlaySessionId"));
        Assert.Null(GetPrivateField(service, "_currentBridgeConfig"));
        Assert.Null(GetPrivateField(service, "_savedLightStates"));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopSyncAsync_AwaitsPredecessorLoopBeforeReplacementCanSend()
    {
        using var httpClient = new HttpClient(new BlockingHueHandler());
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);

        var streamer = new GatedHueStreamer();
        SetPrivateField(service, "_hueStreamer", streamer);
        SetPrivateField(service, "_syncCts", new CancellationTokenSource());
        SetPrivateField(service, "_currentPlaySessionId", "session-old");
        SetPrivateField(
            service,
            "_currentBridgeConfig",
            new ValueTuple<string, string, string, string>(
                "192.168.1.100",
                "app-key",
                "client-key",
                "old-area"));

        const int frameSize = 160 * 90 * 3;
        var runLoopMethod = typeof(HueSyncService).GetMethod(
            "RunSyncLoop",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var oldCts = Assert.IsType<CancellationTokenSource>(GetPrivateField(service, "_syncCts"));
        var oldLoop = Assert.IsAssignableFrom<Task>(runLoopMethod.Invoke(service, new object?[]
        {
            new MemoryStream(new byte[frameSize]),
            new Dictionary<int, (double x, double z)> { [1] = (0, 0) },
            "old-area",
            50,
            oldCts,
            "session-old"
        }));
        SetPrivateField(service, "_syncLoopTask", oldLoop);

        await streamer.FirstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stopMethod = typeof(HueSyncService).GetMethod(
            "StopSyncAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var stopTask = Assert.IsAssignableFrom<Task>(stopMethod.Invoke(service, new object?[]
        {
            false,
            "session-old",
            false,
            CancellationToken.None
        }));

        await streamer.FirstSendCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stopTask.IsCompleted);
        streamer.ReleaseFirstSend.TrySetResult(true);
        await stopTask;
        await oldLoop;

        var newCts = new CancellationTokenSource();
        SetPrivateField(service, "_syncCts", newCts);
        SetPrivateField(service, "_currentPlaySessionId", "session-new");
        var newLoop = Assert.IsAssignableFrom<Task>(runLoopMethod.Invoke(service, new object?[]
        {
            new MemoryStream(new byte[frameSize]),
            new Dictionary<int, (double x, double z)> { [1] = (0, 0) },
            "new-area",
            50,
            newCts,
            "session-new"
        }));
        SetPrivateField(service, "_syncLoopTask", newLoop);
        await streamer.SecondSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        newCts.Cancel();
        await newLoop;

        Assert.Equal(new[] { "old-area", "new-area" }, streamer.Areas);

        var finishNewStop = Assert.IsAssignableFrom<Task>(stopMethod.Invoke(service, new object?[]
        {
            false,
            "session-new",
            true,
            CancellationToken.None
        }));
        await finishNewStop;
        SetPrivateField(service, "_currentBridgeConfig", null);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_HonorsHostCancellationWhileSyncLoopBlocks()
    {
        using var httpClient = new HttpClient(new BlockingHueHandler());
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);

        var syncCts = new CancellationTokenSource();
        var blockedLoop = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetPrivateField(service, "_syncCts", syncCts);
        SetPrivateField(service, "_syncLoopTask", blockedLoop.Task);
        SetPrivateField(service, "_currentPlaySessionId", "shutdown-session");

        using var hostShutdown = new CancellationTokenSource();
        var stopTask = service.StopAsync(hostShutdown.Token);
        await Task.Delay(50);
        Assert.False(stopTask.IsCompleted);

        hostShutdown.Cancel();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(GetPrivateField(service, "_syncCts"));
        Assert.Null(GetPrivateField(service, "_syncLoopTask"));
        Assert.Equal("Idle", service.GetRuntimeStatus().State);
        Assert.Contains(
            "host shutdown",
            service.GetRuntimeStatus().CleanupWarning,
            StringComparison.OrdinalIgnoreCase);

        // Allow the deferred CTS disposal to observe the predecessor task.
        blockedLoop.TrySetResult(true);
    }

    [Fact]
    public async Task StopAsync_WhenSyncLoopWaitIsCanceled_DefersCleanupAndDeactivatesWithFreshToken()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);

        var syncCts = new CancellationTokenSource();
        var blockedLoop = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetPrivateField(service, "_syncCts", syncCts);
        SetPrivateField(service, "_syncLoopTask", blockedLoop.Task);
        SetPrivateField(service, "_currentPlaySessionId", "shutdown-session");
        SetPrivateField(service, "_currentItemName", "Test item");
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "app-key", "client-key", "area-id"));

        using var hostShutdown = new CancellationTokenSource();
        var stopTask = service.StopAsync(hostShutdown.Token);
        await Task.Delay(50);
        Assert.False(stopTask.IsCompleted);

        hostShutdown.Cancel();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(2));

        var deferredStopTask = Assert.IsAssignableFrom<Task>(GetPrivateField(service, "_deferredStopTask"));
        await Task.Delay(100);
        Assert.Equal(0, handler.StopRequestCount);
        Assert.False(handler.StopRequest.Task.IsCompleted);
        blockedLoop.TrySetResult(true);

        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handler.ReleaseStopRequest();
        await handler.StopRequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await deferredStopTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, handler.StopRequestCount);
        Assert.False(handler.LastStopRequestWasCanceled);
        Assert.Null(GetPrivateField(service, "_currentBridgeConfig"));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_WhenHostCancellationInterruptsRestoration_DefersCleanupAndRetriesBridgeState()
    {
        using var hostShutdown = new CancellationTokenSource();
        var handler = new CancelDuringRestorationHueHandler(hostShutdown);
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);

        Plugin.Instance!.Configuration.RestoreLightState = true;
        SetPrivateField(service, "_savedLightStates", new List<HueClient.LightState>
        {
            new("light-id", true, 50, 0.1, 0.2)
        });
        SetPrivateField(service, "_syncCts", new CancellationTokenSource());
        SetPrivateField(service, "_currentPlaySessionId", "shutdown-session");
        SetPrivateField(service, "_currentItemName", "Test item");
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "app-key", "client-key", "area-id"));

        var stopTask = service.StopAsync(hostShutdown.Token);
        await handler.RestorationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stopTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(GetPrivateField(service, "_deferredStopTask"));
        var deferredStopTask = Assert.IsAssignableFrom<Task>(GetPrivateField(service, "_deferredStopTask"));
        await deferredStopTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, handler.RestorationCount);
        Assert.Equal(1, handler.StopCount);
        Assert.False(handler.LastStopRequestWasCanceled);
        Assert.Null(GetPrivateField(service, "_currentBridgeConfig"));
        Assert.Null(GetPrivateField(service, "_savedLightStates"));
    }

    [Fact]
    public async Task StopAsync_WhenAreaDeactivationFails_RetainsTargetForDeferredRetry()
    {
        var handler = new FailFirstDeactivationHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);

        ((HueClient)GetPrivateField(service, "_hueClient")!).RetryAttempts = 0;
        SetPrivateField(service, "_syncCts", new CancellationTokenSource());
        SetPrivateField(service, "_currentPlaySessionId", "shutdown-session");
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "app-key", "client-key", "area-id"));

        await service.StopAsync(CancellationToken.None);

        var deferredStopTask = Assert.IsAssignableFrom<Task>(GetPrivateField(service, "_deferredStopTask"));
        await deferredStopTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, handler.StopRequestCount);
        Assert.Null(GetPrivateField(service, "_currentBridgeConfig"));
    }

    [Fact]
    public async Task StopAsync_WhenLifecycleLockWaitIsCanceled_DefersCleanupUntilLockAvailable()
    {
        var gate = new HueBridgeLifecycleGate();
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, gate);
        await service.StartAsync(CancellationToken.None);

        var playbackLease = gate.TryEnterPlayback();
        Assert.NotNull(playbackLease);
        SetPrivateField(service, "_playbackLifecycleLease", playbackLease);
        SetPrivateField(service, "_syncCts", new CancellationTokenSource());
        SetPrivateField(service, "_currentPlaySessionId", "shutdown-session");
        SetPrivateField(service, "_currentItemName", "Test item");
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "app-key", "client-key", "area-id"));

        var lifecycleLock = Assert.IsType<SemaphoreSlim>(GetPrivateField(service, "_syncLifecycleLock"));
        await lifecycleLock.WaitAsync();
        try
        {
            using var hostShutdown = new CancellationTokenSource();
            var stopTask = service.StopAsync(hostShutdown.Token);
            await Task.Delay(50);
            Assert.False(stopTask.IsCompleted);

            hostShutdown.Cancel();
            await stopTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.NotNull(GetPrivateField(service, "_deferredStopTask"));
            Assert.NotNull(GetPrivateField(service, "_currentBridgeConfig"));
            Assert.True(gate.IsPlaybackActive);
        }
        finally
        {
            lifecycleLock.Release();
        }

        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handler.ReleaseStopRequest();
        await handler.StopRequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deferredStopTask = Assert.IsAssignableFrom<Task>(GetPrivateField(service, "_deferredStopTask"));
        await deferredStopTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(GetPrivateField(service, "_syncCts"));
        Assert.Null(GetPrivateField(service, "_currentPlaySessionId"));
        Assert.Null(GetPrivateField(service, "_currentBridgeConfig"));
        Assert.False(gate.IsPlaybackActive);

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
    public async Task PlaybackPauseAdmission_RejectsStaleSessionWithoutPublishingPausedState()
    {
        using var httpClient = new HttpClient(new BlockingHueHandler());
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);

        SetPrivateField(service, "_syncCts", new CancellationTokenSource());
        SetPrivateField(service, "_currentPlaySessionId", "session-b");
        SetPrivateField(service, "_runtimeState", "Syncing");
        SetPrivateField(service, "_runtimeMessage", "Streaming media colors to Hue.");

        Assert.False(service.TryBeginPlaybackPause(
            "session-a",
            "restore",
            "Playback paused; lights are being restored.",
            out _));

        Assert.Equal("session-b", GetPrivateField(service, "_currentPlaySessionId"));
        Assert.Null(GetPrivateField(service, "_pauseCleanupTask"));
        Assert.Null(GetPrivateField(service, "_pauseCleanupSessionId"));
        Assert.Null(GetPrivateField(service, "_pausedPlaySessionId"));
        Assert.Equal("Syncing", service.GetRuntimeStatus().State);
        Assert.Equal("Streaming media colors to Hue.", service.GetRuntimeStatus().Message);

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
        Assert.Null(GetPrivateField(service, "_activeRestoreLightState"));
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
    public async Task PlaybackPause_WhenDeactivationFails_RetriesWithFreshCleanupAttempt()
    {
        var handler = new BlockingHueHandler
        {
            FailStopRequests = true,
            BlockSecondStopRequest = true
        };
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);

        ((HueClient)GetPrivateField(service, "_hueClient")!).RetryAttempts = 0;
        SetPrivateField(service, "_syncCts", new CancellationTokenSource());
        SetPrivateField(service, "_currentPlaySessionId", "session-a");
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "app-key", "client-key", "area-id"));
        SetPrivateField(service, "_syncStartTime", DateTime.UtcNow.AddSeconds(-10));

        var progressMethod = typeof(HueSyncService).GetMethod("OnPlaybackProgress", BindingFlags.Instance | BindingFlags.NonPublic)!;
        progressMethod.Invoke(service, new object?[] { null, CreateProgress("session-a", isPaused: true) });

        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handler.ReleaseStopRequest();
        await handler.StopRequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await handler.SecondStopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForRuntimeStatusAsync(
            service,
            "Paused",
            "Playback paused; waiting to resume. The entertainment area could not be deactivated; cleanup will retry.");

        Assert.False((bool)GetPrivateField(service, "_bridgeAreaDeactivated")!);
        Assert.Contains("deactivated", service.GetRuntimeStatus().CleanupWarning, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.StopRequestCount);
        Assert.True((bool)GetPrivateField(service, "_playbackCleanupRetryPending")!);
        var deferredCleanupTask = Assert.IsAssignableFrom<Task>(GetPrivateField(service, "_deferredPlaybackCleanupTask"));

        // Let the bounded pause retry succeed before teardown.
        handler.FailStopRequests = false;
        handler.ReleaseSecondStopRequest();
        await deferredCleanupTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((bool)GetPrivateField(service, "_bridgeAreaDeactivated")!);
        Assert.False((bool)GetPrivateField(service, "_playbackCleanupRetryPending")!);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(2, handler.StopRequestCount);
    }

    [Fact]
    public async Task PlaybackPause_WithDimBehavior_WhenDeactivationFails_RetriesWithoutRestoringSnapshot()
    {
        var handler = new BlockingHueHandler
        {
            FailStopRequests = true,
            BlockSecondStopRequest = true
        };
        using var httpClient = new HttpClient(handler);
        var lifecycleGate = new HueBridgeLifecycleGate();
        var service = CreateService(httpClient, lifecycleGate);
        await service.StartAsync(CancellationToken.None);

        Plugin.Instance!.Configuration.RestoreLightState = true;
        Plugin.Instance.Configuration.PauseBehavior = PluginConfiguration.PauseBehaviorDimToCinemaLevel;
        Plugin.Instance.Configuration.BrightnessDimLevel = 25;
        ((HueClient)GetPrivateField(service, "_hueClient")!).RetryAttempts = 0;
        SetPrivateField(service, "_activePauseBehavior", PluginConfiguration.PauseBehaviorDimToCinemaLevel);
        SetPrivateField(service, "_activePauseBrightnessPercent", 25);
        SetPrivateField(service, "_activeRestoreLightState", true);
        var savedLightStates = new List<HueClient.LightState>
        {
            new("light-id", true, 50, 0.1, 0.2)
        };
        SetPrivateField(service, "_savedLightStates", savedLightStates);
        SetPrivateField(service, "_syncCts", new CancellationTokenSource());
        SetPrivateField(service, "_currentPlaySessionId", "session-a");
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "app-key", "client-key", "area-id"));
        SetPrivateField(service, "_syncStartTime", DateTime.UtcNow.AddSeconds(-10));

        var resourceKey = HueSyncService.GetPlaybackResourceKey("192.168.1.100", "area-id");
        var playbackLease = lifecycleGate.TryEnterPlayback(resourceKey);
        Assert.NotNull(playbackLease);
        SetPrivateField(service, "_playbackLifecycleLease", playbackLease);

        var progressMethod = typeof(HueSyncService).GetMethod("OnPlaybackProgress", BindingFlags.Instance | BindingFlags.NonPublic)!;
        progressMethod.Invoke(service, new object?[] { null, CreateProgress("session-a", isPaused: true) });

        await handler.BrightnessRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handler.ReleaseStopRequest();
        await handler.StopRequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await handler.SecondStopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(savedLightStates, GetPrivateField(service, "_savedLightStates"));
        Assert.Equal(1, handler.RestorationRequestCount);
        Assert.True((bool)GetPrivateField(service, "_playbackCleanupRetryPending")!);
        var diagnosticLease = lifecycleGate.TryEnterDiagnostic(resourceKey, out var blockedByPlayback);
        Assert.Null(diagnosticLease);
        Assert.True(blockedByPlayback);
        var deferredCleanupTask = Assert.IsAssignableFrom<Task>(GetPrivateField(service, "_deferredPlaybackCleanupTask"));

        handler.FailStopRequests = false;
        handler.ReleaseSecondStopRequest();
        await deferredCleanupTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, handler.RestorationRequestCount);
        Assert.Same(savedLightStates, GetPrivateField(service, "_savedLightStates"));
        Assert.True((bool)GetPrivateField(service, "_bridgeAreaDeactivated")!);
        Assert.False((bool)GetPrivateField(service, "_playbackCleanupRetryPending")!);
        Assert.False(lifecycleGate.IsPlaybackActiveForResource(resourceKey));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PlaybackPause_WithDimBehavior_WhenDeactivationThrows_RetainsLeaseForRetry()
    {
        var handler = new BlockingHueHandler
        {
            ThrowFirstStopRequest = true,
            BlockSecondStopRequest = true
        };
        using var httpClient = new HttpClient(handler);
        var lifecycleGate = new HueBridgeLifecycleGate();
        var service = CreateService(httpClient, lifecycleGate);
        await service.StartAsync(CancellationToken.None);

        Plugin.Instance!.Configuration.RestoreLightState = true;
        Plugin.Instance.Configuration.PauseBehavior = PluginConfiguration.PauseBehaviorDimToCinemaLevel;
        Plugin.Instance.Configuration.BrightnessDimLevel = 25;
        ((HueClient)GetPrivateField(service, "_hueClient")!).RetryAttempts = 0;
        SetPrivateField(service, "_activePauseBehavior", PluginConfiguration.PauseBehaviorDimToCinemaLevel);
        SetPrivateField(service, "_activePauseBrightnessPercent", 25);
        SetPrivateField(service, "_activeRestoreLightState", true);
        var savedLightStates = new List<HueClient.LightState>
        {
            new("light-id", true, 50, 0.1, 0.2)
        };
        SetPrivateField(service, "_savedLightStates", savedLightStates);
        SetPrivateField(service, "_syncCts", new CancellationTokenSource());
        SetPrivateField(service, "_currentPlaySessionId", "session-a");
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "app-key", "client-key", "area-id"));
        SetPrivateField(service, "_syncStartTime", DateTime.UtcNow.AddSeconds(-10));

        var resourceKey = HueSyncService.GetPlaybackResourceKey("192.168.1.100", "area-id");
        var playbackLease = lifecycleGate.TryEnterPlayback(resourceKey);
        Assert.NotNull(playbackLease);
        SetPrivateField(service, "_playbackLifecycleLease", playbackLease);

        var progressMethod = typeof(HueSyncService).GetMethod("OnPlaybackProgress", BindingFlags.Instance | BindingFlags.NonPublic)!;
        progressMethod.Invoke(service, new object?[] { null, CreateProgress("session-a", isPaused: true) });

        await handler.BrightnessRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await handler.SecondStopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(savedLightStates, GetPrivateField(service, "_savedLightStates"));
        Assert.Equal(1, handler.RestorationRequestCount);
        Assert.True((bool)GetPrivateField(service, "_playbackCleanupRetryPending")!);
        Assert.Equal("Paused", service.GetRuntimeStatus().State);
        Assert.Contains("cleanup will retry", service.GetRuntimeStatus().Message, StringComparison.OrdinalIgnoreCase);
        var diagnosticLease = lifecycleGate.TryEnterDiagnostic(resourceKey, out var blockedByPlayback);
        Assert.Null(diagnosticLease);
        Assert.True(blockedByPlayback);
        var deferredCleanupTask = Assert.IsAssignableFrom<Task>(GetPrivateField(service, "_deferredPlaybackCleanupTask"));

        handler.ThrowFirstStopRequest = false;
        handler.ReleaseStopRequest();
        handler.ReleaseSecondStopRequest();
        await deferredCleanupTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, handler.RestorationRequestCount);
        Assert.Same(savedLightStates, GetPrivateField(service, "_savedLightStates"));
        Assert.True((bool)GetPrivateField(service, "_bridgeAreaDeactivated")!);
        Assert.False((bool)GetPrivateField(service, "_playbackCleanupRetryPending")!);
        Assert.False(lifecycleGate.IsPlaybackActiveForResource(resourceKey));

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

    [Fact]
    public async Task ConfigurationDisable_StopsActivePlaybackAndRestoresLightsWithoutRestartingWhenReenabled()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);

        var userId = Guid.NewGuid();
        SetPrivateField(service, "_syncCts", new CancellationTokenSource());
        SetPrivateField(service, "_currentPlaySessionId", "configuration-disable-session");
        SetPrivateField(service, "_currentUserId", userId);
        SetPrivateField(service, "_currentItemName", "Configuration disable item");
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "app-key", "client-key", "area-id"));
        SetPrivateField(service, "_activeRestoreLightState", true);
        SetPrivateField(service, "_activeUseCinemaMode", false);
        SetPrivateField(service, "_savedLightStates", new List<HueClient.LightState>
        {
            new("light-id", true, 50, 0.1, 0.2)
        });

        var stopTask = service.StopPlaybackSessionsForDisabledConfigurationAsync(globalSyncDisabled: true);
        await handler.RestorationRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handler.ReleaseStopRequest();

        Assert.Equal(1, await stopTask);
        Assert.Null(GetPrivateField(service, "_syncCts"));
        Assert.Null(GetPrivateField(service, "_currentBridgeConfig"));
        Assert.Null(GetPrivateField(service, "_savedLightStates"));
        Assert.False(service.GetRuntimeStatus().IsSyncing);

        // Re-enabling policy must not resurrect the session that was stopped by the
        // administrator mutation. A fresh playback-start event is required.
        Plugin.Instance!.Configuration.SyncEnabled = true;
        var progressMethod = typeof(HueSyncService).GetMethod(
            "OnPlaybackProgress",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        progressMethod.Invoke(service, new object?[] { null, CreateProgress("configuration-disable-session") });
        Assert.Null(GetPrivateField(service, "_syncCts"));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConfigurationDisable_StopsOnlyTheSelectedUserPlaybackSession()
    {
        var handler = new BlockingHueHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);
        await service.StartAsync(CancellationToken.None);

        var activeUserId = Guid.NewGuid();
        var unrelatedUserId = Guid.NewGuid();
        SetPrivateField(service, "_syncCts", new CancellationTokenSource());
        SetPrivateField(service, "_currentPlaySessionId", "selective-disable-session");
        SetPrivateField(service, "_currentUserId", activeUserId);
        SetPrivateField(service, "_currentItemName", "Selective disable item");
        SetPrivateField(service, "_currentBridgeConfig", new ValueTuple<string, string, string, string>(
            "192.168.1.100", "app-key", "client-key", "area-id"));
        SetPrivateField(service, "_activeRestoreLightState", false);
        SetPrivateField(service, "_activeUseCinemaMode", false);

        Assert.Equal(
            0,
            await service.StopPlaybackSessionsForDisabledConfigurationAsync(
                globalSyncDisabled: false,
                disabledUserIds: new[] { unrelatedUserId }));
        Assert.NotNull(GetPrivateField(service, "_syncCts"));

        var stopTask = service.StopPlaybackSessionsForDisabledConfigurationAsync(
            globalSyncDisabled: false,
            disabledUserIds: new[] { activeUserId });
        await handler.StopRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handler.ReleaseStopRequest();

        Assert.Equal(1, await stopTask);
        Assert.Null(GetPrivateField(service, "_syncCts"));
        Assert.Null(GetPrivateField(service, "_currentBridgeConfig"));

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

    private static System.Collections.IDictionary GetConcurrentPlaybackWorkers(HueSyncService service) =>
        Assert.IsAssignableFrom<System.Collections.IDictionary>(GetPrivateField(service, "_concurrentPlaybackWorkers"));

    private static object CreateSyntheticConcurrentWorker(
        string playSessionId,
        string clientSessionId,
        string resourceKey)
    {
        var workerType = typeof(HueSyncService).GetNestedType(
            "ConcurrentPlaybackWorker",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Static)!;
        var worker = Activator.CreateInstance(workerType, nonPublic: true)!;
        workerType.GetProperty("PlaySessionId")!.SetValue(worker, playSessionId);
        workerType.GetProperty("ClientSessionId")!.SetValue(worker, clientSessionId);
        workerType.GetProperty("ResourceKey")!.SetValue(worker, resourceKey);
        return worker;
    }

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

    private sealed class GatedHueStreamer : HueStreamer
    {
        private readonly object _areaLock = new();
        private int _sendCount;

        public GatedHueStreamer()
            : base(Mock.Of<ILogger<HueStreamer>>())
        {
        }

        public TaskCompletionSource<bool> FirstSendStarted { get; } = NewSignal();
        public TaskCompletionSource<bool> FirstSendCanceled { get; } = NewSignal();
        public TaskCompletionSource<bool> ReleaseFirstSend { get; } = NewSignal();
        public TaskCompletionSource<bool> SecondSendStarted { get; } = NewSignal();
        public List<string> Areas { get; } = new();

        public override void StopStream()
        {
        }

        public override async Task<bool> SendColors(
            string areaId,
            Dictionary<int, byte[]> channelColors,
            int colorChangeThreshold = 0,
            CancellationToken cancellationToken = default)
        {
            var sendNumber = Interlocked.Increment(ref _sendCount);
            lock (_areaLock)
            {
                Areas.Add(areaId);
            }

            if (sendNumber == 1)
            {
                FirstSendStarted.TrySetResult(true);
                using var cancellationRegistration = cancellationToken.Register(
                    () => FirstSendCanceled.TrySetResult(true));
                await ReleaseFirstSend.Task.ConfigureAwait(false);
            }
            else if (sendNumber == 2)
            {
                SecondSendStarted.TrySetResult(true);
            }

            return true;
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CancelDuringRestorationHueHandler : HttpMessageHandler
    {
        private readonly CancellationTokenSource _hostShutdown;
        private int _restorationCount;
        private int _stopCount;

        public CancelDuringRestorationHueHandler(CancellationTokenSource hostShutdown)
        {
            _hostShutdown = hostShutdown;
        }

        public TaskCompletionSource<bool> RestorationStarted { get; } = NewSignal();
        public int RestorationCount => Volatile.Read(ref _restorationCount);
        public int StopCount => Volatile.Read(ref _stopCount);
        public bool LastStopRequestWasCanceled { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put &&
                request.RequestUri?.AbsolutePath.Contains("/light/", StringComparison.Ordinal) == true)
            {
                if (Interlocked.Increment(ref _restorationCount) == 1)
                {
                    RestorationStarted.TrySetResult(true);
                    _hostShutdown.Cancel();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return OkResponse();
            }

            if (request.Method == HttpMethod.Put &&
                request.RequestUri?.AbsolutePath.Contains("entertainment_configuration", StringComparison.Ordinal) == true)
            {
                LastStopRequestWasCanceled = cancellationToken.IsCancellationRequested;
                Interlocked.Increment(ref _stopCount);
            }

            return OkResponse();
        }

        private static HttpResponseMessage OkResponse()
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FailFirstDeactivationHueHandler : HttpMessageHandler
    {
        private int _stopRequestCount;

        public int StopRequestCount => Volatile.Read(ref _stopRequestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put &&
                request.RequestUri?.AbsolutePath.Contains("entertainment_configuration", StringComparison.Ordinal) == true)
            {
                var requestNumber = Interlocked.Increment(ref _stopRequestCount);
                if (requestNumber == 1)
                {
                    // Return a retriable status once; the test disables client retries so
                    // the service's deferred cleanup is the second attempt.
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ImmediateHueHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = request.Method == HttpMethod.Get &&
                          request.RequestUri?.AbsolutePath.Contains("entertainment_configuration", StringComparison.Ordinal) == true
                ? "{\"data\":[{\"channels\":[]}] }"
                : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class BlockingHueHandler : HttpMessageHandler
    {
        public string ConfigurationJson { get; set; } = "{\"data\":[{\"channels\":[]}]}";
        public TaskCompletionSource<bool> FirstConfigurationRequest { get; } = NewSignal();
        public TaskCompletionSource<bool> SecondConfigurationRequest { get; } = NewSignal();
        public TaskCompletionSource<bool> StopRequest { get; } = NewSignal();
        public TaskCompletionSource<bool> SecondStopRequest { get; } = NewSignal();
        public TaskCompletionSource<bool> StopRequestCompleted { get; } = NewSignal();
        public TaskCompletionSource<bool> RestorationRequest { get; } = NewSignal();
        public TaskCompletionSource<bool> SecondRestorationRequest { get; } = NewSignal();
        public TaskCompletionSource<bool> BrightnessRequest { get; } = NewSignal();
        public string? LastLightPutBody { get; private set; }
        public bool LastStopRequestWasCanceled { get; private set; }
        public bool FailRestorationRequests { get; set; }
        public bool FailFirstRestorationRequest { get; set; }
        public bool BlockSecondRestorationRequest { get; set; }
        public bool BlockSecondStopRequest { get; set; }
        public bool ThrowFirstStopRequest { get; set; }
        public bool FailStopRequests { get; set; }
        public bool FailLightCaptureRequests { get; set; }
        public TaskCompletionSource<bool> LightCaptureRequest { get; } = NewSignal();
        public int StartAreaRequestCount { get; private set; }
        public int StopRequestCount => Volatile.Read(ref _stopRequestCount);
        public int RestorationRequestCount => Volatile.Read(ref _restorationRequestCount);

        private readonly TaskCompletionSource<bool> _firstConfigurationRelease = NewSignal();
        private readonly TaskCompletionSource<bool> _stopRelease = NewSignal();
        private int _configurationRequestCount;
        private int _stopRequestCount;
        private int _restorationRequestCount;
        private readonly TaskCompletionSource<bool> _secondRestorationRelease = NewSignal();
        private readonly TaskCompletionSource<bool> _secondStopRelease = NewSignal();

        public void ReleaseFirstConfiguration() => _firstConfigurationRelease.TrySetResult(true);

        public void ReleaseStopRequest() => _stopRelease.TrySetResult(true);

        public void ReleaseSecondRestorationRequest() => _secondRestorationRelease.TrySetResult(true);

        public void ReleaseSecondStopRequest() => _secondStopRelease.TrySetResult(true);

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
                    var restorationNumber = Interlocked.Increment(ref _restorationRequestCount);
                    if (body.Contains("\"dimming\"", StringComparison.Ordinal) &&
                        !body.Contains("\"color\"", StringComparison.Ordinal) &&
                        !body.Contains("\"color_temperature\"", StringComparison.Ordinal))
                    {
                        BrightnessRequest.TrySetResult(true);
                    }
                    if (BlockSecondRestorationRequest && restorationNumber == 2)
                    {
                        SecondRestorationRequest.TrySetResult(true);
                        await _secondRestorationRelease.Task;
                    }
                    if (FailRestorationRequests ||
                        (FailFirstRestorationRequest && restorationNumber == 1))
                    {
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    }
                }
                if (body.Contains("\"stop\"", StringComparison.Ordinal))
                {
                    LastStopRequestWasCanceled = cancellationToken.IsCancellationRequested;
                    var stopNumber = Interlocked.Increment(ref _stopRequestCount);
                    StopRequest.TrySetResult(true);
                    if (ThrowFirstStopRequest && stopNumber == 1)
                        throw new HttpRequestException("Synthetic stop transport failure.");
                    if (BlockSecondStopRequest && stopNumber == 2)
                    {
                        SecondStopRequest.TrySetResult(true);
                        await _secondStopRelease.Task;
                    }
                    await _stopRelease.Task;
                    StopRequestCompleted.TrySetResult(true);
                    if (FailStopRequests)
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
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
