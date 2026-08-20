using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Hue.Configuration;
using Jellyfin.Plugin.Hue.Hue;
using Jellyfin.Plugin.Hue.Video;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Hue.Service
{
    /// <summary>
    /// Background service that monitors Jellyfin playback and synchronizes Hue lights in real-time
    /// </summary>
    public class HueSyncService : IHostedService
    {
        // Video frame processing constants
        private const int FrameWidth = 160;
        private const int FrameHeight = 90;
        private const int BytesPerPixel = 3; // RGB24 format
        private const int DefaultSamplingBreadthPercent = 15;
        private const int MinSamplingBreadthPercent = 1;
        private const int MaxSamplingBreadthPercent = 50;
        private const int MinColorSmoothingPercent = 0;
        private const int MaxColorSmoothingPercent = 90;

        // Timing constants
        private const int CinemaModeDimmingDelayMs = 500;
        private const int EntertainmentAreaActivationDelayMs = 200;
        private const int RestoreLightsDelayMs = 300;
        private const int DefaultTargetFps = 20;
        private const int MinFps = 1;
        private const int MaxFps = 60;
        private const int DefaultFrameDurationMs = 50;
        private const int DefaultFfmpegStallTimeoutSeconds = 5;
        private const int MaxConsecutiveDtlsSendFailures = 5;

        // Audio-reactive capture uses a small, dependency-free PCM window. Eight kHz is
        // sufficient for the low/mid/high bands while keeping per-frame work bounded.
        private const int AudioSampleRate = 8000;
        private const int AudioChannels = 2;
        private const int AudioBytesPerSample = 2;

        // Jellyfin emits progress events throughout playback. Small timing differences
        // between those events are normal, so only a meaningful discontinuity is treated
        // as a seek that needs a fresh FFmpeg capture position.
        private const double PlaybackSeekBackwardToleranceSeconds = 2;
        private const double PlaybackSeekMinimumForwardJumpSeconds = 5;
        private const double PlaybackSeekForwardToleranceSeconds = 8;
        private const string RecoveredPlaySessionPrefix = "hue-recovered:";

        // Color processing constants
        private const int ColorDivisor = 2; // Divide by 2 for 16-bit color compatibility
        private const int FullBrightnessValue = 127; // Full brightness for 16-bit representation

        private readonly ISessionManager _sessionManager;
        private readonly ILogger<HueSyncService> _logger;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly HueBridgeLifecycleGate _bridgeLifecycleGate;
        private readonly bool _managesPlaybackEvents;
        private readonly Dictionary<string, ConcurrentPlaybackWorker> _concurrentPlaybackWorkers =
            new(StringComparer.Ordinal);

        private FfmpegStreamer? _ffmpegStreamer;
        private HueStreamer? _hueStreamer;
        private readonly HueClient _hueClient;
        private CancellationTokenSource? _syncCts;
        private readonly ILoggerFactory _loggerFactory;
        private string? _currentPlaySessionId;
        private string? _recoveredSessionId;
        private Guid? _currentUserId;
        private string? _currentUserName;
        private List<HueClient.LightState>? _savedLightStates;
        private string? _savedLightStatePlaySessionId;
        private DateTime _syncStartTime;
        private long? _lastPlaybackPositionTicks;
        private DateTime _lastPlaybackPositionObservedUtc;
        private bool _lastPlaybackProgressWasPaused;
        private bool _seekRestartInFlight;
        private int _seekRestartCount;
        private double? _lastSeekPositionSeconds;
        private const int MinSyncDurationBeforePauseMs = 5000; // Ignore pause events for first 5 seconds
        private string? _startingPlaySessionId;
        private int _syncStartsInFlight;
        private readonly object _syncLock = new object();
        private CancellationTokenSource? _startupCts;
        private readonly SemaphoreSlim _syncLifecycleLock = new SemaphoreSlim(1, 1);
        private Task? _pauseCleanupTask;
        private string? _pauseCleanupSessionId;
        private (string BridgeIp, string AppKey, string ClientKey, string AreaId)? _currentBridgeConfig;
        private bool _bridgeAreaDeactivated;
        private volatile bool _isStopping;
        private string? _currentItemName;
        private string? _currentFrameResolution;
        private string? _currentVideoScalingMode;
        private string? _currentVideoDeinterlaceMode;
        private int? _currentTargetFps;
        private int? _currentAudioSensitivityPercent;
        private int? _currentAudioNoiseGatePercent;
        private (int LowFrequencyHz, int MidFrequencyHz, int HighFrequencyHz)? _currentAudioFrequencies;
        private (int LowGainPercent, int MidGainPercent, int HighGainPercent)? _currentAudioBandGains;
        private int? _currentAudioBandSpreadPercent;
        private int? _currentAudioBeatPulsePercent;
        private int? _currentAudioBeatPulseDecayPercent;
        private string? _currentAudioColorPalette;
        private string? _currentAudioSpatialMode;
        private string? _currentAudioChannelMode;
        private int? _currentSamplingBreadthPercent;
        private string? _currentSamplingMode;
        private int? _currentColorSmoothingPercent;
        private (
            int BrightnessBoost,
            int RedGain,
            int GreenGain,
            int BlueGain,
            int ColorSaturation,
            int HueShiftDegrees,
            int OutputBrightnessPercent,
            int BlackoutThreshold,
            int ColorChangeThreshold)? _activeColorProcessingSettings;
        private (
            bool UseGpu,
            string CustomFfmpegFlags,
            int FfmpegStallTimeoutSeconds,
            int NetworkRetryAttempts)? _activeExecutionSettings;
        private IReadOnlySet<int>? _activeChannelIds;
        private bool? _activeUseCinemaMode;
        private bool? _activeCinemaModeAttempted;
        private bool? _activeRestoreLightState;
        private IDisposable? _playbackLifecycleLease;
        private string? _activePauseBehavior;
        private string? _manuallyStoppedPlaySessionId;
        private bool _externalPlaybackStartPending;
        private bool _externalPlaybackStopRequested;
        private string _runtimeState = "Idle";
        private string _runtimeMessage = "Waiting for playback.";
        private string? _lastError;
        private string? _lastCleanupWarning;
        private HueSessionSummary? _lastSessionSummary;
        private readonly List<HueSessionSummary> _sessionHistory = new();
        private readonly Action<HueSessionSummary>? _sessionSummarySink;

        /// <summary>
        /// Maximum number of sanitized in-memory session summaries retained for the
        /// administrator history endpoint. History is intentionally bounded; optional
        /// persistence stores only sanitized entries and never adds credentials or tokens.
        /// </summary>
        public const int MaxSessionHistoryCount = PluginConfiguration.MaxSessionHistoryCount;

        // Public property to track sync state
        public bool IsSyncing
        {
            get
            {
                lock (_syncLock)
                {
                    return _syncCts != null && !_syncCts.IsCancellationRequested;
                }
            }
        }

        public string? CurrentItemName
        {
            get
            {
                lock (_syncLock)
                {
                    return _currentItemName;
                }
            }
            private set
            {
                lock (_syncLock)
                {
                    _currentItemName = value;
                }
            }
        }

        /// <summary>
        /// Indicates whether an administrator can stop the current playback session's Hue output
        /// without stopping Jellyfin playback.
        /// </summary>
        public bool CanStopSync
        {
            get
            {
                lock (_syncLock)
                {
                    if (_isStopping ||
                        (_manuallyStoppedPlaySessionId != null &&
                         string.Equals(_manuallyStoppedPlaySessionId, _currentPlaySessionId, StringComparison.Ordinal) &&
                         _syncCts == null &&
                         _currentBridgeConfig == null &&
                         _startingPlaySessionId == null))
                    {
                        return false;
                    }

                    return _currentPlaySessionId != null ||
                           _startingPlaySessionId != null ||
                           _syncCts != null ||
                           _currentBridgeConfig != null;
                }
            }
        }

        public HueSyncService(
            ISessionManager sessionManager,
            ILogger<HueSyncService> logger,
            ILoggerFactory loggerFactory,
            HueClient hueClient,
            IMediaEncoder mediaEncoder,
            HueBridgeLifecycleGate? bridgeLifecycleGate = null)
            : this(
                sessionManager,
                logger,
                loggerFactory,
                hueClient,
                mediaEncoder,
                bridgeLifecycleGate ?? new HueBridgeLifecycleGate(),
                managesPlaybackEvents: true)
        {
        }

        private HueSyncService(
            ISessionManager sessionManager,
            ILogger<HueSyncService> logger,
            ILoggerFactory loggerFactory,
            HueClient hueClient,
            IMediaEncoder mediaEncoder,
            HueBridgeLifecycleGate bridgeLifecycleGate,
            bool managesPlaybackEvents,
            Action<HueSessionSummary>? sessionSummarySink = null)
        {
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _hueClient = hueClient ?? throw new ArgumentNullException(nameof(hueClient));
            _mediaEncoder = mediaEncoder ?? throw new ArgumentNullException(nameof(mediaEncoder));
            _bridgeLifecycleGate = bridgeLifecycleGate ?? throw new ArgumentNullException(nameof(bridgeLifecycleGate));
            _managesPlaybackEvents = managesPlaybackEvents;
            _sessionSummarySink = sessionSummarySink;
        }

        private HueSyncService CreateConcurrentPlaybackWorker()
        {
            return new HueSyncService(
                _sessionManager,
                _loggerFactory.CreateLogger<HueSyncService>(),
                _loggerFactory,
                _hueClient.CreatePlaybackClient(),
                _mediaEncoder,
                _bridgeLifecycleGate,
                managesPlaybackEvents: false,
                sessionSummarySink: AddConcurrentSessionSummary);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _isStopping = false;
            SetRuntimeStatus("Idle", "Waiting for playback.", clearError: true);
            _logger.LogInformation("Hue Sync Service Started.");
            if (_managesPlaybackEvents)
            {
                LoadPersistedSessionHistory();
                _sessionManager.PlaybackStart += OnPlaybackStart;
                _sessionManager.PlaybackStopped += OnPlaybackStopped;
                _sessionManager.PlaybackProgress += OnPlaybackProgress;
            }

            // Helpers
            _hueStreamer = new HueStreamer(_loggerFactory.CreateLogger<HueStreamer>());
            _ffmpegStreamer = new FfmpegStreamer(_loggerFactory.CreateLogger<FfmpegStreamer>());

            if (_managesPlaybackEvents)
                RecoverActiveVideoSession();

            return Task.CompletedTask;
        }

        /// <summary>
        /// Reconnects Hue synchronization when the plugin is started while Jellyfin is
        /// already playing supported media. Jellyfin does not replay PlaybackStart for a service
        /// that subscribes after the session began, so without this recovery the viewer
        /// would need to stop and restart playback manually.
        ///
        /// The primary service recovers the first eligible session, and additional active
        /// sessions are recovered through isolated workers when their configured Hue
        /// targets are distinct. A later playback event still follows the normal lifecycle
        /// arbitration and remains observable in runtime status.
        /// </summary>
        private void RecoverActiveVideoSession()
        {
            try
            {
                var activeSessions = _sessionManager.Sessions
                    ?.Where(session =>
                        session.IsActive &&
                        session.PlayState != null &&
                        !session.PlayState.IsPaused &&
                        session.FullNowPlayingItem != null &&
                        IsSupportedPlaybackItem(session.FullNowPlayingItem) &&
                        !string.IsNullOrWhiteSpace(session.Id))
                    .ToArray();

                if (activeSessions == null || activeSessions.Length == 0)
                    return;

                foreach (var activeSession in activeSessions)
                {
                    lock (_syncLock)
                    {
                        if (_isStopping)
                            return;
                    }

                    var playState = activeSession.PlayState!;
                    var progress = new PlaybackProgressEventArgs
                    {
                        Item = activeSession.FullNowPlayingItem!,
                        Session = activeSession,
                        PlaySessionId = RecoveredPlaySessionPrefix + activeSession.Id,
                        PlaybackPositionTicks = playState.PositionTicks,
                        IsPaused = playState.IsPaused
                    };

                    _logger.LogInformation(
                        "Recovering Hue sync for active playback session {0} after service startup",
                        progress.PlaySessionId);
                    OnPlaybackStart(this, progress);
                }
            }
            catch (Exception ex)
            {
                // Startup recovery must never prevent Jellyfin from finishing plugin
                // initialization; a later PlaybackStart event remains the fallback.
                _logger.LogWarning(ex, "Could not recover Hue sync for active playback after service startup");
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Hue Sync Service Stopping.");
            if (_managesPlaybackEvents)
            {
                _sessionManager.PlaybackStart -= OnPlaybackStart;
                _sessionManager.PlaybackStopped -= OnPlaybackStopped;
                _sessionManager.PlaybackProgress -= OnPlaybackProgress;
            }

            ConcurrentPlaybackWorker[] concurrentWorkers;
            lock (_syncLock)
            {
                concurrentWorkers = _concurrentPlaybackWorkers.Values.ToArray();
                _concurrentPlaybackWorkers.Clear();
            }

            if (concurrentWorkers.Length > 0)
            {
                await Task.WhenAll(concurrentWorkers.Select(worker =>
                    worker.Service.StopAsync(CancellationToken.None))).ConfigureAwait(false);
            }

            Task? pauseCleanup;
            lock (_syncLock)
            {
                _isStopping = true;
                _startupCts?.Cancel();
                _syncCts?.Cancel();
                _manuallyStoppedPlaySessionId = null;
                _externalPlaybackStartPending = false;
                _externalPlaybackStopRequested = false;
                ResetPlaybackProgressTrackingLocked();
                pauseCleanup = _pauseCleanupTask;
            }

            try
            {
                if (pauseCleanup != null)
                    await pauseCleanup.ConfigureAwait(false);
            }
            finally
            {
                lock (_syncLock)
                {
                    if (ReferenceEquals(_pauseCleanupTask, pauseCleanup))
                    {
                        _pauseCleanupTask = null;
                        _pauseCleanupSessionId = null;
                    }
                }

                await _syncLifecycleLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    var config = Plugin.Instance?.Configuration;
                    var bridgeConfig = _currentBridgeConfig;
                    var areaAlreadyDeactivated = _bridgeAreaDeactivated;
                    var savedLightStates = _savedLightStates;
                    StopSync(deactivateArea: false);
                    _currentBridgeConfig = null;
                    _currentFrameResolution = null;
                    _currentVideoScalingMode = null;
                    _currentVideoDeinterlaceMode = null;
                    _currentTargetFps = null;
                    _currentAudioSensitivityPercent = null;
                    _currentAudioNoiseGatePercent = null;
                    _currentAudioFrequencies = null;
                    _currentAudioBandGains = null;
                    _currentAudioBandSpreadPercent = null;
                    _currentAudioBeatPulsePercent = null;
                    _currentAudioBeatPulseDecayPercent = null;
                    _currentAudioColorPalette = null;
                    _currentAudioSpatialMode = null;
                    _currentAudioChannelMode = null;
                    _currentSamplingBreadthPercent = null;
                    _currentSamplingMode = null;
                    _currentColorSmoothingPercent = null;

                    if (bridgeConfig != null && (!areaAlreadyDeactivated || savedLightStates != null))
                    {
                        await RestoreAndDeactivateAsync(
                            config,
                            bridgeConfig,
                            savedLightStates,
                            publishIdleStatus: false,
                            sessionOutcome: "ServiceStopped").ConfigureAwait(false);
                    }
                    else
                    {
                        var sessionSummarySeed = CaptureSessionSummarySeed(bridgeConfig, "ServiceStopped");
                        if (sessionSummarySeed != null)
                            RecordSessionSummary(sessionSummarySeed, null);

                        ReleasePlaybackLifecycleLease();
                        CurrentItemName = null;
                        lock (_syncLock)
                        {
                            _currentUserId = null;
                            _currentUserName = null;
                        }
                    }

                    lock (_syncLock)
                    {
                        _activeUseCinemaMode = null;
                        _activeCinemaModeAttempted = null;
                        _activeRestoreLightState = null;
                        _activePauseBehavior = null;
                        _activeColorProcessingSettings = null;
                        _activeExecutionSettings = null;
                        _activeChannelIds = null;
                    }

                    _savedLightStates = null;
                    _savedLightStatePlaySessionId = null;
                }
                finally
                {
                    _syncLifecycleLock.Release();
                }

                SetRuntimeStatus("Idle", "Sync service stopped.");
            }
        }

        /// <summary>
        /// Stops Hue output for the current playback session while leaving Jellyfin playback running.
        /// The session is suppressed until its playback-stop event, so progress notifications cannot
        /// immediately restart synchronization.
        /// </summary>
        public async Task<bool> StopCurrentSyncAsync(string? playSessionId = null)
        {
            if (!string.IsNullOrWhiteSpace(playSessionId) &&
                TryGetConcurrentPlaybackWorker(playSessionId, clientSessionId: null, out var concurrentWorker))
            {
                return await concurrentWorker.Service.StopCurrentSyncAsync().ConfigureAwait(false);
            }

            await _syncLifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                string? activePlaySessionId;
                lock (_syncLock)
                {
                    if (!CanStopSync)
                        return false;

                    activePlaySessionId = _currentPlaySessionId ?? _startingPlaySessionId;
                    _manuallyStoppedPlaySessionId = activePlaySessionId;
                    if (_externalPlaybackStartPending)
                        _externalPlaybackStopRequested = true;
                    _startupCts?.Cancel();
                    _syncCts?.Cancel();
                }

                var config = Plugin.Instance?.Configuration;
                var bridgeConfig = _currentBridgeConfig;
                var savedLightStates = _savedLightStates;
                StopSync(deactivateArea: false, expectedPlaySessionId: activePlaySessionId, clearSession: false);
                _currentBridgeConfig = null;
                _currentFrameResolution = null;
                _currentVideoScalingMode = null;
                _currentVideoDeinterlaceMode = null;
                _currentTargetFps = null;
                _currentAudioSensitivityPercent = null;
                _currentAudioNoiseGatePercent = null;
                _currentAudioFrequencies = null;
                _currentAudioBandGains = null;
                _currentAudioBandSpreadPercent = null;
                _currentAudioBeatPulsePercent = null;
                _currentAudioBeatPulseDecayPercent = null;
                _currentAudioColorPalette = null;
                _currentAudioSpatialMode = null;
                _currentAudioChannelMode = null;
                _currentSamplingBreadthPercent = null;
                _currentSamplingMode = null;
                _currentColorSmoothingPercent = null;

                await RestoreAndDeactivateAsync(
                    config,
                    bridgeConfig,
                    savedLightStates,
                    sessionOutcome: "StoppedByAdministrator").ConfigureAwait(false);
                SetRuntimeStatus("Stopped", "Hue sync stopped by an administrator; playback continues.");
                return true;
            }
            finally
            {
                _syncLifecycleLock.Release();
            }
        }

        /// <summary>
        /// Returns a point-in-time snapshot of the active playback synchronization session.
        /// Credentials are intentionally excluded so this can be safely exposed to the
        /// administrator status page.
        /// </summary>
        public HueRuntimeStatus GetRuntimeStatus()
        {
            (string BridgeIp, string AppKey, string ClientKey, string AreaId)? bridgeConfig;
            string? currentItem;
            Guid? currentUserId;
            string? currentUserName;
            string? currentFrameResolution;
            string? currentVideoScalingMode;
            string? currentVideoDeinterlaceMode;
            int? currentTargetFps;
            int? currentAudioSensitivityPercent;
            int? currentAudioNoiseGatePercent;
            (int LowFrequencyHz, int MidFrequencyHz, int HighFrequencyHz)? currentAudioFrequencies;
            (int LowGainPercent, int MidGainPercent, int HighGainPercent)? currentAudioBandGains;
            int? currentAudioBandSpreadPercent;
            int? currentAudioBeatPulsePercent;
            int? currentAudioBeatPulseDecayPercent;
            string? currentAudioColorPalette;
            string? currentAudioSpatialMode;
            string? currentAudioChannelMode;
            int? currentSamplingBreadthPercent;
            string? currentSamplingMode;
            int? currentColorSmoothingPercent;
            (
                int BrightnessBoost,
                int RedGain,
                int GreenGain,
                int BlueGain,
                int ColorSaturation,
                int HueShiftDegrees,
                int OutputBrightnessPercent,
                int BlackoutThreshold,
                int ColorChangeThreshold)? activeColorProcessingSettings;
            (
                bool UseGpu,
                string CustomFfmpegFlags,
                int FfmpegStallTimeoutSeconds,
                int NetworkRetryAttempts)? activeExecutionSettings;
            IReadOnlySet<int>? activeChannelIds;
            bool? activeRestoreLightState;
            string state;
            string message;
            string? lastError;
            string? cleanupWarning;
            DateTime syncStartTime;
            CancellationTokenSource? syncCts;
            bool canStopSync;
            int seekRestartCount;
            double? lastSeekPositionSeconds;
            HueSessionSummary? lastSessionSummary;
            string? playSessionId;

            lock (_syncLock)
            {
                bridgeConfig = _currentBridgeConfig;
                currentItem = _currentItemName;
                currentUserId = _currentUserId;
                currentUserName = _currentUserName;
                currentFrameResolution = _currentFrameResolution;
                currentVideoScalingMode = _currentVideoScalingMode;
                currentVideoDeinterlaceMode = _currentVideoDeinterlaceMode;
                currentTargetFps = _currentTargetFps;
                currentAudioSensitivityPercent = _currentAudioSensitivityPercent;
                currentAudioNoiseGatePercent = _currentAudioNoiseGatePercent;
                currentAudioFrequencies = _currentAudioFrequencies;
                currentAudioBandGains = _currentAudioBandGains;
                currentAudioBandSpreadPercent = _currentAudioBandSpreadPercent;
                currentAudioBeatPulsePercent = _currentAudioBeatPulsePercent;
                currentAudioBeatPulseDecayPercent = _currentAudioBeatPulseDecayPercent;
                currentAudioColorPalette = _currentAudioColorPalette;
                currentAudioSpatialMode = _currentAudioSpatialMode;
                currentAudioChannelMode = _currentAudioChannelMode;
                currentSamplingBreadthPercent = _currentSamplingBreadthPercent;
                currentSamplingMode = _currentSamplingMode;
                currentColorSmoothingPercent = _currentColorSmoothingPercent;
                activeColorProcessingSettings = _activeColorProcessingSettings;
                activeExecutionSettings = _activeExecutionSettings;
                activeChannelIds = _activeChannelIds;
                activeRestoreLightState = _activeRestoreLightState;
                state = _runtimeState;
                message = _runtimeMessage;
                lastError = _lastError;
                cleanupWarning = _lastCleanupWarning;
                syncStartTime = _syncStartTime;
                syncCts = _syncCts;
                canStopSync = CanStopSync;
                seekRestartCount = _seekRestartCount;
                lastSeekPositionSeconds = _lastSeekPositionSeconds;
                lastSessionSummary = _lastSessionSummary;
                playSessionId = _currentPlaySessionId;
            }

            var isSyncing = syncCts != null && !syncCts.IsCancellationRequested;
            var ffmpeg = _ffmpegStreamer;
            var hueStreamer = _hueStreamer;
            var syncDuration = isSyncing && syncStartTime != default
                ? Math.Max(0, (DateTime.UtcNow - syncStartTime).TotalSeconds)
                : (double?)null;
            var framesProcessed = ffmpeg?.FramesProcessed ?? 0;
            var effectiveFps = isSyncing && syncDuration > 0 && framesProcessed > 0
                ? framesProcessed / syncDuration.Value
                : (double?)null;

            return new HueRuntimeStatus
            {
                PlaySessionId = playSessionId,
                State = state,
                Message = message,
                LastError = lastError,
                CleanupWarning = cleanupWarning,
                CurrentItem = currentItem,
                ActiveUserId = isSyncing ? currentUserId?.ToString() : null,
                ActiveUserName = isSyncing ? currentUserName : null,
                ActivePlaybackMediaFilter = isSyncing
                    ? Plugin.Instance?.Configuration?.GetPlaybackMediaFilterForUser(currentUserId ?? Guid.Empty)
                    : null,
                ActiveFrameResolution = isSyncing ? currentFrameResolution : null,
                ActiveVideoScalingMode = isSyncing ? currentVideoScalingMode : null,
                ActiveVideoDeinterlaceMode = isSyncing ? currentVideoDeinterlaceMode : null,
                ActiveTargetFps = isSyncing ? currentTargetFps : null,
                ActiveAudioSensitivityPercent = isSyncing ? currentAudioSensitivityPercent : null,
                ActiveAudioNoiseGatePercent = isSyncing ? currentAudioNoiseGatePercent : null,
                ActiveAudioLowFrequencyHz = isSyncing ? currentAudioFrequencies?.LowFrequencyHz : null,
                ActiveAudioMidFrequencyHz = isSyncing ? currentAudioFrequencies?.MidFrequencyHz : null,
                ActiveAudioHighFrequencyHz = isSyncing ? currentAudioFrequencies?.HighFrequencyHz : null,
                ActiveAudioLowGainPercent = isSyncing ? currentAudioBandGains?.LowGainPercent : null,
                ActiveAudioMidGainPercent = isSyncing ? currentAudioBandGains?.MidGainPercent : null,
                ActiveAudioHighGainPercent = isSyncing ? currentAudioBandGains?.HighGainPercent : null,
                ActiveAudioBandSpreadPercent = isSyncing ? currentAudioBandSpreadPercent : null,
                ActiveAudioBeatPulsePercent = isSyncing ? currentAudioBeatPulsePercent : null,
                ActiveAudioBeatPulseDecayPercent = isSyncing ? currentAudioBeatPulseDecayPercent : null,
                ActiveAudioColorPalette = isSyncing ? currentAudioColorPalette : null,
                ActiveAudioSpatialMode = isSyncing ? currentAudioSpatialMode : null,
                ActiveAudioChannelMode = isSyncing ? currentAudioChannelMode : null,
                ActiveSamplingBreadthPercent = isSyncing ? currentSamplingBreadthPercent : null,
                ActiveSamplingMode = isSyncing ? currentSamplingMode : null,
                ActiveColorSmoothingPercent = isSyncing ? currentColorSmoothingPercent : null,
                ActiveBrightnessBoost = isSyncing ? activeColorProcessingSettings?.BrightnessBoost : null,
                ActiveRedGain = isSyncing ? activeColorProcessingSettings?.RedGain : null,
                ActiveGreenGain = isSyncing ? activeColorProcessingSettings?.GreenGain : null,
                ActiveBlueGain = isSyncing ? activeColorProcessingSettings?.BlueGain : null,
                ActiveColorSaturation = isSyncing ? activeColorProcessingSettings?.ColorSaturation : null,
                ActiveHueShiftDegrees = isSyncing ? activeColorProcessingSettings?.HueShiftDegrees : null,
                ActiveOutputBrightnessPercent = isSyncing ? activeColorProcessingSettings?.OutputBrightnessPercent : null,
                ActiveBlackoutThreshold = isSyncing ? activeColorProcessingSettings?.BlackoutThreshold : null,
                ActiveColorChangeThreshold = isSyncing ? activeColorProcessingSettings?.ColorChangeThreshold : null,
                ActiveUseGpu = isSyncing ? activeExecutionSettings?.UseGpu : null,
                ActiveCustomFfmpegFlagsConfigured = isSyncing
                    ? !string.IsNullOrWhiteSpace(activeExecutionSettings?.CustomFfmpegFlags)
                    : null,
                ActiveFfmpegStallTimeoutSeconds = isSyncing ? activeExecutionSettings?.FfmpegStallTimeoutSeconds : null,
                ActiveNetworkRetryAttempts = isSyncing ? activeExecutionSettings?.NetworkRetryAttempts : null,
                ActiveChannelIds = isSyncing ? FormatChannelIds(activeChannelIds) : null,
                ActiveRestoreLightState = isSyncing ? activeRestoreLightState : null,
                ActiveBridgeIp = isSyncing ? bridgeConfig?.BridgeIp : null,
                ActiveEntertainmentAreaId = isSyncing ? bridgeConfig?.AreaId : null,
                IsSyncing = isSyncing,
                CanStopSync = canStopSync,
                FramesProcessed = framesProcessed,
                EffectiveFps = effectiveFps,
                PacketsSent = isSyncing ? hueStreamer?.PacketsSent ?? 0 : 0,
                PacketsSkippedByThreshold = isSyncing ? hueStreamer?.PacketsSkippedByThreshold ?? 0 : 0,
                PacketSendFailures = isSyncing ? hueStreamer?.PacketSendFailures ?? 0 : 0,
                ReconnectAttempts = isSyncing ? hueStreamer?.ReconnectAttempts ?? 0 : 0,
                SeekRestartCount = isSyncing ? seekRestartCount : 0,
                LastSeekPositionSeconds = isSyncing ? lastSeekPositionSeconds : null,
                IsFfmpegHealthy = isSyncing && ffmpeg?.IsHealthy(
                    activeExecutionSettings?.FfmpegStallTimeoutSeconds
                        ?? Plugin.Instance?.Configuration?.FfmpegStallTimeoutSeconds
                        ?? DefaultFfmpegStallTimeoutSeconds) == true,
                IsDtlsHealthy = isSyncing && hueStreamer?.IsHealthy() == true,
                SyncDurationSeconds = syncDuration,
                SyncStartedAtUtc = isSyncing && syncStartTime != default ? syncStartTime : null,
                LastSession = lastSessionSummary
            };
        }

        /// <summary>
        /// Returns sanitized status snapshots for playback workers that are streaming to
        /// targets other than the primary service lifecycle. The primary status remains
        /// available through <see cref="GetRuntimeStatus"/> for existing clients.
        /// </summary>
        public IReadOnlyList<HueRuntimeStatus> GetConcurrentRuntimeStatuses()
        {
            ConcurrentPlaybackWorker[] workers;
            lock (_syncLock)
            {
                workers = _concurrentPlaybackWorkers.Values.ToArray();
            }

            return workers
                .Select(worker => worker.Service.GetRuntimeStatus())
                .ToArray();
        }

        /// <summary>
        /// Returns all active playback status snapshots, including the primary lifecycle
        /// and any isolated workers serving distinct Hue targets.
        /// </summary>
        public IReadOnlyList<HueRuntimeStatus> GetPlaybackRuntimeStatuses()
        {
            var statuses = new List<HueRuntimeStatus>();
            var primary = GetRuntimeStatus();
            if (primary.IsSyncing || primary.CurrentItem != null || primary.State is not "Idle")
                statuses.Add(primary);

            statuses.AddRange(GetConcurrentRuntimeStatuses());
            return statuses;
        }

        /// <summary>
        /// Gets whether any playback worker still owns an active or pending playback
        /// lifecycle. Terminal error/stopped status snapshots do not count as active.
        /// </summary>
        public bool HasActivePlaybackSessions => GetPlaybackRuntimeStatuses().Any(status =>
            status.IsSyncing ||
            status.CurrentItem != null ||
            status.State is "Starting" or "Syncing" or "Resyncing" or "Paused" or "Stopping");

        /// <summary>
        /// Returns the most recently completed sanitized playback summaries. The list is
        /// bounded, newest first, and contains no bridge credentials or playback tokens.
        /// An outcome filter can be supplied for focused administrator diagnostics.
        /// </summary>
        public IReadOnlyList<HueSessionSummary> GetSessionHistory(
            int limit = MaxSessionHistoryCount,
            string? outcome = null)
        {
            var boundedLimit = Math.Clamp(limit, 1, MaxSessionHistoryCount);
            var normalizedOutcome = outcome?.Trim();
            lock (_syncLock)
            {
                return _sessionHistory
                    .Where(summary => string.IsNullOrWhiteSpace(normalizedOutcome) ||
                        string.Equals(summary.Outcome, normalizedOutcome, StringComparison.OrdinalIgnoreCase))
                    .Take(boundedLimit)
                    .ToArray();
            }
        }

        /// <summary>
        /// Clears all retained completed-session summaries and the status endpoint's last-session
        /// pointer. Active playback is not stopped or changed.
        /// </summary>
        public int ClearSessionHistory()
        {
            int clearedCount;
            lock (_syncLock)
            {
                clearedCount = _sessionHistory.Count;
                _sessionHistory.Clear();
                _lastSessionSummary = null;
            }

            PersistSessionHistory();
            return clearedCount;
        }

        /// <summary>
        /// Reconciles persisted history with the current administrator setting. This is
        /// called after configuration changes so enabling retention captures the current
        /// in-memory window immediately and disabling retention removes stored entries.
        /// </summary>
        public void RefreshSessionHistoryPersistence()
        {
            if (!_managesPlaybackEvents)
                return;

            lock (_syncLock)
            {
                TrimSessionHistoryLocked();
                _lastSessionSummary = _sessionHistory.FirstOrDefault();
            }

            PersistSessionHistory();
        }

        private void LoadPersistedSessionHistory()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return;

            config.PersistedSessionHistory ??= new List<HueSessionHistoryEntry>();
            if (!config.PersistSessionHistory)
            {
                if (config.PersistedSessionHistory.Count > 0)
                {
                    config.PersistedSessionHistory.Clear();
                    SavePersistedSessionHistoryConfiguration();
                }

                return;
            }

            var persistedEntries = config.PersistedSessionHistory
                .Where(entry => entry != null)
                .Take(config.GetSessionHistoryRetentionCount())
                .ToList();
            if (persistedEntries.Count != config.PersistedSessionHistory.Count)
            {
                config.PersistedSessionHistory = persistedEntries;
                SavePersistedSessionHistoryConfiguration();
            }

            var entries = persistedEntries
                .Select(ToSessionSummary)
                .ToArray();
            lock (_syncLock)
            {
                _sessionHistory.Clear();
                _sessionHistory.AddRange(entries);
                _lastSessionSummary = _sessionHistory.FirstOrDefault();
            }
        }

        private void PersistSessionHistory()
        {
            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
                return;

            HueSessionHistoryEntry[] entries;
            lock (_syncLock)
            {
                entries = _sessionHistory
                    .Take(config.GetSessionHistoryRetentionCount())
                    .Select(ToPersistedSessionSummary)
                    .ToArray();
            }

            config.PersistedSessionHistory ??= new List<HueSessionHistoryEntry>();
            if (config.PersistSessionHistory)
            {
                config.PersistedSessionHistory = entries.ToList();
            }
            else
            {
                if (config.PersistedSessionHistory.Count == 0)
                    return;

                config.PersistedSessionHistory.Clear();
            }

            SavePersistedSessionHistoryConfiguration();
        }

        private void SavePersistedSessionHistoryConfiguration()
        {
            try
            {
                Plugin.Instance?.SaveConfiguration();
            }
            catch (Exception ex)
            {
                // Persistence is diagnostic-only and must never interrupt playback cleanup.
                _logger.LogWarning(ex, "Could not persist Hue session history");
            }
        }

        private static HueSessionSummary ToSessionSummary(HueSessionHistoryEntry entry)
        {
            return new HueSessionSummary
            {
                Outcome = entry.Outcome,
                Item = entry.Item,
                UserId = entry.UserId,
                UserName = entry.UserName,
                BridgeIp = entry.BridgeIp,
                EntertainmentAreaId = entry.EntertainmentAreaId,
                StartedAtUtc = entry.StartedAtUtc,
                EndedAtUtc = entry.EndedAtUtc,
                DurationSeconds = entry.DurationSeconds,
                EffectiveFps = entry.EffectiveFps,
                FramesProcessed = entry.FramesProcessed,
                PacketsSent = entry.PacketsSent,
                PacketsSkippedByThreshold = entry.PacketsSkippedByThreshold,
                PacketSendFailures = entry.PacketSendFailures,
                ReconnectAttempts = entry.ReconnectAttempts,
                SeekRestartCount = entry.SeekRestartCount,
                LastSeekPositionSeconds = entry.LastSeekPositionSeconds,
                Error = entry.Error,
                CleanupWarning = entry.CleanupWarning
            };
        }

        private static HueSessionHistoryEntry ToPersistedSessionSummary(HueSessionSummary summary)
        {
            return new HueSessionHistoryEntry
            {
                Outcome = summary.Outcome,
                Item = summary.Item,
                UserId = summary.UserId,
                UserName = summary.UserName,
                BridgeIp = summary.BridgeIp,
                EntertainmentAreaId = summary.EntertainmentAreaId,
                StartedAtUtc = summary.StartedAtUtc,
                EndedAtUtc = summary.EndedAtUtc,
                DurationSeconds = summary.DurationSeconds,
                EffectiveFps = summary.EffectiveFps,
                FramesProcessed = summary.FramesProcessed,
                PacketsSent = summary.PacketsSent,
                PacketsSkippedByThreshold = summary.PacketsSkippedByThreshold,
                PacketSendFailures = summary.PacketSendFailures,
                ReconnectAttempts = summary.ReconnectAttempts,
                SeekRestartCount = summary.SeekRestartCount,
                LastSeekPositionSeconds = summary.LastSeekPositionSeconds,
                Error = summary.Error,
                CleanupWarning = summary.CleanupWarning
            };
        }

        private void SetRuntimeStatus(string state, string message, bool clearError = false)
        {
            lock (_syncLock)
            {
                _runtimeState = state;
                _runtimeMessage = message;
                if (clearError)
                {
                    _lastError = null;
                    _lastCleanupWarning = null;
                }
            }
        }

        private void SetCleanupWarning(string? message)
        {
            lock (_syncLock)
            {
                _lastCleanupWarning = message;
            }
        }

        private void SetRuntimeError(string message)
        {
            lock (_syncLock)
            {
                _runtimeState = "Error";
                _runtimeMessage = message;
                _lastError = message;
            }
        }

        private void SetRuntimeWarning(string message)
        {
            lock (_syncLock)
            {
                if (!string.Equals(_runtimeState, "Error", StringComparison.Ordinal))
                    _runtimeState = "Syncing";
                _runtimeMessage = message;
                _lastError = message;
            }
        }

        /// <summary>
        /// Hue synchronization extracts video frames or an audio waveform, so unrelated
        /// Jellyfin playback events must never enter the FFmpeg lifecycle.
        /// </summary>
        internal static bool IsSupportedVideoPlaybackItem(BaseItem? item)
        {
            return item is MediaBrowser.Controller.Entities.Video ||
                   item?.MediaType == Jellyfin.Data.Enums.MediaType.Video;
        }

        /// <summary>
        /// Identifies audio-only playback items. Audio synchronization is opt-in through
        /// the Audio or AllMedia playback scope; keeping this predicate independent of
        /// configuration lets startup recovery and lifecycle cleanup observe the session
        /// before the effective scope decides whether to activate Hue.
        /// </summary>
        internal static bool IsAudioPlaybackItem(BaseItem? item)
        {
            return item?.MediaType == Jellyfin.Data.Enums.MediaType.Audio;
        }

        internal static bool IsSupportedPlaybackItem(BaseItem? item)
        {
            return IsSupportedVideoPlaybackItem(item) || IsAudioPlaybackItem(item);
        }

        /// <summary>
        /// Applies the global playback media scope to a Jellyfin item. Unknown or legacy
        /// values fall back to all-video behavior so a malformed optional setting cannot
        /// silently disable playback after an upgrade.
        /// </summary>
        internal static bool MatchesPlaybackMediaFilter(BaseItem? item, string? playbackMediaFilter)
        {
            if (!IsSupportedPlaybackItem(item))
                return false;

            var isAudio = IsAudioPlaybackItem(item);
            var isVideo = IsSupportedVideoPlaybackItem(item);

            if (!PluginConfiguration.TryNormalizePlaybackMediaFilter(playbackMediaFilter, out var normalized))
                normalized = PluginConfiguration.PlaybackMediaFilterAllVideo;

            return normalized switch
            {
                PluginConfiguration.PlaybackMediaFilterAudio => isAudio,
                PluginConfiguration.PlaybackMediaFilterAllMedia => isVideo || isAudio,
                PluginConfiguration.PlaybackMediaFilterMovies =>
                    isVideo && item is MediaBrowser.Controller.Entities.Movies.Movie,
                PluginConfiguration.PlaybackMediaFilterEpisodes =>
                    isVideo && item is MediaBrowser.Controller.Entities.TV.Episode,
                PluginConfiguration.PlaybackMediaFilterOtherVideo =>
                    isVideo &&
                    item is not MediaBrowser.Controller.Entities.Movies.Movie &&
                    item is not MediaBrowser.Controller.Entities.TV.Episode,
                _ => isVideo
            };
        }

        /// <summary>
        /// Builds the non-secret resource identity used to arbitrate independent playback
        /// lifecycles. A bridge entertainment area can only have one active stream, while
        /// separate areas (including areas on separate bridges) can run concurrently.
        /// </summary>
        internal static string GetPlaybackResourceKey(string bridgeIp, string areaId)
        {
            return $"{bridgeIp.Trim().TrimEnd('.').ToLowerInvariant()}|{areaId.Trim().ToLowerInvariant()}";
        }

        private bool IsPlaybackUserSyncEnabled(PlaybackProgressEventArgs e)
        {
            var config = Plugin.Instance?.Configuration;
            return config == null || config.IsSyncEnabledForUser(e.Session?.UserId ?? Guid.Empty);
        }

        private string GetPlaybackMediaFilter(PlaybackProgressEventArgs e)
        {
            return Plugin.Instance?.Configuration?.GetPlaybackMediaFilterForUser(e.Session?.UserId ?? Guid.Empty)
                ?? PluginConfiguration.PlaybackMediaFilterAllVideo;
        }

        /// <summary>
        /// Determines whether two playback progress samples describe a real seek rather
        /// than ordinary progress between Jellyfin notifications. The comparison uses the
        /// elapsed wall-clock time so a slow event cadence does not cause unnecessary
        /// restarts.
        /// </summary>
        internal static bool IsPlaybackSeek(
            long? previousPositionTicks,
            DateTime previousObservedUtc,
            long? currentPositionTicks,
            DateTime currentObservedUtc)
        {
            if (!previousPositionTicks.HasValue || !currentPositionTicks.HasValue ||
                previousPositionTicks.Value < 0 || currentPositionTicks.Value < 0 ||
                previousObservedUtc == default || currentObservedUtc == default)
            {
                return false;
            }

            var positionDeltaSeconds = TimeSpan.FromTicks(
                currentPositionTicks.Value - previousPositionTicks.Value).TotalSeconds;
            if (positionDeltaSeconds < -PlaybackSeekBackwardToleranceSeconds)
                return true;

            if (positionDeltaSeconds < PlaybackSeekMinimumForwardJumpSeconds)
                return false;

            var elapsedSeconds = Math.Max(0, (currentObservedUtc - previousObservedUtc).TotalSeconds);
            return positionDeltaSeconds - elapsedSeconds >= PlaybackSeekForwardToleranceSeconds;
        }

        private void ResetPlaybackProgressTrackingLocked()
        {
            _lastPlaybackPositionTicks = null;
            _lastPlaybackPositionObservedUtc = default;
            _lastPlaybackProgressWasPaused = false;
            _seekRestartInFlight = false;
            _seekRestartCount = 0;
            _lastSeekPositionSeconds = null;
        }

        private void ClearTransientRuntimeWarning()
        {
            lock (_syncLock)
            {
                if (_lastError != null && _lastError.StartsWith("The DTLS stream could not send", StringComparison.Ordinal))
                {
                    _runtimeState = "Syncing";
                    _runtimeMessage = "Streaming media colors to Hue.";
                    _lastError = null;
                }
            }
        }

        private TimeSpan GetFrameReadTimeout()
        {
            int configuredTimeout;
            lock (_syncLock)
            {
                configuredTimeout = _activeExecutionSettings?.FfmpegStallTimeoutSeconds
                    ?? Plugin.Instance?.Configuration?.FfmpegStallTimeoutSeconds
                    ?? DefaultFfmpegStallTimeoutSeconds;
            }

            return _ffmpegStreamer?.GetFrameReadTimeout(configuredTimeout)
                ?? TimeSpan.FromSeconds(Math.Clamp(configuredTimeout, 1, 60));
        }

        private bool HandleDtlsSendResult(
            bool sent,
            CancellationToken token,
            ref int consecutiveFailures)
        {
            if (token.IsCancellationRequested)
                return true;

            if (sent)
            {
                consecutiveFailures = 0;
                ClearTransientRuntimeWarning();
                return true;
            }

            consecutiveFailures++;
            if (consecutiveFailures >= MaxConsecutiveDtlsSendFailures)
            {
                SetRuntimeError("The DTLS stream failed to send colors after repeated reconnect attempts.");
                return false;
            }

            SetRuntimeWarning("The DTLS stream could not send colors; reconnect is being attempted.");
            return true;
        }

        private string? GetRecoveredLifecyclePlaySessionId(string? eventPlaySessionId, SessionInfo? session)
        {
            lock (_syncLock)
            {
                if (_recoveredSessionId == null ||
                    _currentPlaySessionId == null ||
                    !string.Equals(_recoveredSessionId, session?.Id, StringComparison.Ordinal))
                {
                    return null;
                }

                return string.Equals(eventPlaySessionId, _currentPlaySessionId, StringComparison.Ordinal)
                    ? null
                    : _currentPlaySessionId;
            }
        }

        private PlaybackProgressEventArgs NormalizeRecoveredPlaybackEvent(PlaybackProgressEventArgs e)
        {
            var lifecyclePlaySessionId = GetRecoveredLifecyclePlaySessionId(e.PlaySessionId, e.Session);
            if (lifecyclePlaySessionId == null)
                return e;

            return new PlaybackProgressEventArgs
            {
                Item = e.Item,
                Session = e.Session,
                PlaySessionId = lifecyclePlaySessionId,
                PlaybackPositionTicks = e.PlaybackPositionTicks,
                IsPaused = e.IsPaused
            };
        }

        private PlaybackStopEventArgs NormalizeRecoveredPlaybackStop(PlaybackStopEventArgs e)
        {
            var lifecyclePlaySessionId = GetRecoveredLifecyclePlaySessionId(e.PlaySessionId, e.Session);
            if (lifecyclePlaySessionId == null)
                return e;

            return new PlaybackStopEventArgs
            {
                Item = e.Item,
                Session = e.Session,
                PlaySessionId = lifecyclePlaySessionId,
                PlayedToCompletion = e.PlayedToCompletion
            };
        }

        private bool TryGetConcurrentPlaybackWorker(
            PlaybackProgressEventArgs e,
            out ConcurrentPlaybackWorker worker)
        {
            return TryGetConcurrentPlaybackWorker(e.PlaySessionId, e.Session?.Id, out worker);
        }

        private bool TryGetConcurrentPlaybackWorker(
            PlaybackStopEventArgs e,
            out ConcurrentPlaybackWorker worker)
        {
            return TryGetConcurrentPlaybackWorker(e.PlaySessionId, e.Session?.Id, out worker);
        }

        private bool TryGetConcurrentPlaybackWorker(
            string? playSessionId,
            string? clientSessionId,
            out ConcurrentPlaybackWorker worker)
        {
            lock (_syncLock)
            {
                if (!string.IsNullOrWhiteSpace(playSessionId) &&
                    _concurrentPlaybackWorkers.TryGetValue(playSessionId, out worker!))
                {
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(clientSessionId))
                {
                    var matchingWorker = _concurrentPlaybackWorkers.Values.FirstOrDefault(candidate =>
                        string.Equals(candidate.ClientSessionId, clientSessionId, StringComparison.Ordinal));
                    if (matchingWorker != null)
                    {
                        worker = matchingWorker;
                        return true;
                    }
                }
            }

            worker = null!;
            return false;
        }

        private bool TryStartConcurrentPlayback(PlaybackProgressEventArgs e)
        {
            if (!_managesPlaybackEvents || string.IsNullOrWhiteSpace(e.PlaySessionId))
                return false;

            PluginConfiguration? config;
            string? primaryResourceKey;
            lock (_syncLock)
            {
                if (_isStopping ||
                    _currentPlaySessionId == null ||
                    string.Equals(_currentPlaySessionId, e.PlaySessionId, StringComparison.Ordinal))
                {
                    return false;
                }

                if (_concurrentPlaybackWorkers.ContainsKey(e.PlaySessionId) ||
                    (!string.IsNullOrWhiteSpace(e.Session?.Id) &&
                     _concurrentPlaybackWorkers.Values.Any(worker =>
                         string.Equals(worker.ClientSessionId, e.Session?.Id, StringComparison.Ordinal))))
                {
                    return true;
                }

                primaryResourceKey = _currentBridgeConfig is { } bridgeConfig
                    ? GetPlaybackResourceKey(bridgeConfig.BridgeIp, bridgeConfig.AreaId)
                    : null;
                config = Plugin.Instance?.Configuration;
                if (primaryResourceKey == null && config != null && _currentUserId.HasValue)
                {
                    var primaryTarget = config.GetBridgeConfigForUser(_currentUserId.Value);
                    if (!string.IsNullOrWhiteSpace(primaryTarget.BridgeIp) &&
                        !string.IsNullOrWhiteSpace(primaryTarget.AreaId))
                    {
                        primaryResourceKey = GetPlaybackResourceKey(primaryTarget.BridgeIp, primaryTarget.AreaId);
                    }
                }
            }

            if (config == null || primaryResourceKey == null)
                return false;

            var userId = e.Session?.UserId ?? Guid.Empty;
            var target = config.GetBridgeConfigForUser(userId);
            if (string.IsNullOrWhiteSpace(target.BridgeIp) || string.IsNullOrWhiteSpace(target.AreaId))
                return false;

            var resourceKey = GetPlaybackResourceKey(target.BridgeIp, target.AreaId);
            if (string.Equals(primaryResourceKey, resourceKey, StringComparison.OrdinalIgnoreCase))
                return false;

            lock (_syncLock)
            {
                if (_concurrentPlaybackWorkers.Values.Any(worker =>
                        string.Equals(worker.ResourceKey, resourceKey, StringComparison.OrdinalIgnoreCase)))
                {
                    SetRuntimeStatus("Busy", "Another playback session already owns this Hue target.");
                    return true;
                }

                var worker = new ConcurrentPlaybackWorker
                {
                    Service = CreateConcurrentPlaybackWorker(),
                    PlaySessionId = e.PlaySessionId,
                    ClientSessionId = e.Session?.Id,
                    ResourceKey = resourceKey
                };

                // StartAsync only initializes the worker-owned helpers. Prepare the
                // lifecycle identity before publishing the worker so a stop event that
                // arrives during this tiny handoff can cancel the pending start safely.
                worker.Service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
                if (!worker.Service.PrepareExternalPlaybackStart(e))
                    return true;

                _concurrentPlaybackWorkers[e.PlaySessionId] = worker;
                _logger.LogInformation(
                    "Starting concurrent Hue sync worker for session {0} on target {1}",
                    e.PlaySessionId,
                    resourceKey);
                ObserveTask(StartConcurrentPlaybackAsync(worker, e));
                return true;
            }
        }

        private async Task StartConcurrentPlaybackAsync(
            ConcurrentPlaybackWorker worker,
            PlaybackProgressEventArgs e)
        {
            try
            {
                await worker.Service.HandlePreparedExternalPlaybackStartAsync(e).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Concurrent Hue sync worker failed for session {0}", worker.PlaySessionId);
            }
        }

        private void RemoveConcurrentPlaybackWorker(ConcurrentPlaybackWorker worker)
        {
            lock (_syncLock)
            {
                if (_concurrentPlaybackWorkers.TryGetValue(worker.PlaySessionId, out var current) &&
                    ReferenceEquals(current, worker))
                {
                    _concurrentPlaybackWorkers.Remove(worker.PlaySessionId);
                }
                else
                {
                    var matchingKey = _concurrentPlaybackWorkers.FirstOrDefault(pair =>
                        ReferenceEquals(pair.Value, worker)).Key;
                    if (matchingKey != null)
                        _concurrentPlaybackWorkers.Remove(matchingKey);
                }
            }
        }

        internal async Task HandleExternalPlaybackStartAsync(PlaybackProgressEventArgs e)
        {
            if (_isStopping)
                return;

            e = NormalizeRecoveredPlaybackEvent(e);
            if (!IsPlaybackUserSyncEnabled(e) ||
                !MatchesPlaybackMediaFilter(e.Item, GetPlaybackMediaFilter(e)))
                return;

            if (!PrepareExternalPlaybackStart(e))
                return;

            await HandlePreparedExternalPlaybackStartAsync(e).ConfigureAwait(false);
        }

        private async Task HandlePreparedExternalPlaybackStartAsync(PlaybackProgressEventArgs e)
        {
            lock (_syncLock)
            {
                if (_isStopping || !_externalPlaybackStartPending)
                    return;

                _externalPlaybackStartPending = false;
                if (_externalPlaybackStopRequested)
                {
                    _externalPlaybackStopRequested = false;
                    return;
                }
            }

            await StartSyncForItem(e).ConfigureAwait(false);
        }

        private bool PrepareExternalPlaybackStart(PlaybackProgressEventArgs e)
        {
            lock (_syncLock)
            {
                if (_isStopping ||
                    (_currentPlaySessionId != null &&
                     !string.Equals(_currentPlaySessionId, e.PlaySessionId, StringComparison.Ordinal)) ||
                    _externalPlaybackStartPending ||
                    string.Equals(_startingPlaySessionId, e.PlaySessionId, StringComparison.Ordinal) ||
                    (IsSyncing && string.Equals(_currentPlaySessionId, e.PlaySessionId, StringComparison.Ordinal)))
                {
                    return false;
                }

                _currentPlaySessionId ??= e.PlaySessionId;
                if (e.PlaySessionId.StartsWith(RecoveredPlaySessionPrefix, StringComparison.Ordinal))
                    _recoveredSessionId = e.Session?.Id;
                else
                    _recoveredSessionId = null;
                _currentUserId = e.Session?.UserId is { } sessionUserId && sessionUserId != Guid.Empty
                    ? sessionUserId
                    : null;
                _currentUserName = string.IsNullOrWhiteSpace(e.Session?.UserName)
                    ? null
                    : e.Session!.UserName.Trim();
                _lastPlaybackPositionTicks = e.PlaybackPositionTicks;
                _lastPlaybackPositionObservedUtc = DateTime.UtcNow;
                _lastPlaybackProgressWasPaused = e.IsPaused;
                _externalPlaybackStartPending = true;
                _externalPlaybackStopRequested = false;
                return true;
            }
        }

        internal void HandleExternalPlaybackStart(PlaybackProgressEventArgs e)
        {
            ObserveTask(HandleExternalPlaybackStartAsync(e));
        }

        private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
        {
            if (_isStopping)
                return;

            if (TryGetConcurrentPlaybackWorker(e, out var concurrentWorker))
            {
                concurrentWorker.Service.HandleExternalPlaybackStart(e);
                return;
            }

            e = NormalizeRecoveredPlaybackEvent(e);
            _logger.LogInformation("Playback started for item {0}", e.Item.Name);

            if (!IsPlaybackUserSyncEnabled(e))
            {
                _logger.LogInformation("Hue Sync is disabled for this playback user, skipping start");
                SetRuntimeStatus("Idle", "Sync is disabled for this user.");
                return;
            }

            if (!IsSupportedPlaybackItem(e.Item))
            {
                _logger.LogDebug("Skipping unsupported playback item {0}; Hue Sync supports video and audio playback", e.Item?.Name ?? "Unknown");
                var publishUnsupportedStatus = false;
                lock (_syncLock)
                {
                    publishUnsupportedStatus = _currentPlaySessionId == null &&
                                               _startingPlaySessionId == null &&
                                               _syncCts == null;
                }

                if (publishUnsupportedStatus)
                    SetRuntimeStatus("Idle", "Hue Sync supports video or audio playback, depending on the selected media scope.");
                return;
            }

            var playbackMediaFilter = GetPlaybackMediaFilter(e);
            if (!MatchesPlaybackMediaFilter(e.Item, playbackMediaFilter))
            {
                _logger.LogDebug(
                    "Skipping playback item {0}; configured Hue Sync media scope is {1}",
                    e.Item?.Name ?? "Unknown",
                    playbackMediaFilter);
                var publishFilteredStatus = false;
                lock (_syncLock)
                {
                    publishFilteredStatus = _currentPlaySessionId == null &&
                                             _startingPlaySessionId == null &&
                                             _syncCts == null;
                }

                if (publishFilteredStatus)
                    SetRuntimeStatus("Idle", $"Hue Sync playback media scope excludes this item ({playbackMediaFilter}).");
                return;
            }

            // A distinct bridge/area target gets an isolated worker instead of replacing
            // the primary session. Same-target playback retains the existing replacement
            // behavior because Hue cannot stream two sessions through one area.
            if (TryStartConcurrentPlayback(e))
                return;

            // Skip duplicate notifications for the same session, but allow a new session
            // to queue while an earlier startup is being cancelled.
            lock (_syncLock)
            {
                if (_isStopping)
                    return;

                if (string.Equals(_manuallyStoppedPlaySessionId, e.PlaySessionId, StringComparison.Ordinal))
                {
                    _logger.LogDebug("Hue sync was manually stopped for this playback session, skipping restart");
                    return;
                }

                if (string.Equals(_startingPlaySessionId, e.PlaySessionId, StringComparison.Ordinal) ||
                    (IsSyncing && _currentPlaySessionId == e.PlaySessionId))
                {
                    _logger.LogDebug("Sync already in progress for this session, skipping duplicate start");
                    return;
                }

                if (_currentPlaySessionId == null)
                {
                    _currentPlaySessionId = e.PlaySessionId;
                    if (e.PlaySessionId.StartsWith(RecoveredPlaySessionPrefix, StringComparison.Ordinal))
                        _recoveredSessionId = e.Session?.Id;
                    else
                        _recoveredSessionId = null;
                    _currentUserId = e.Session?.UserId is { } sessionUserId && sessionUserId != Guid.Empty
                        ? sessionUserId
                        : null;
                    _currentUserName = string.IsNullOrWhiteSpace(e.Session?.UserName)
                        ? null
                        : e.Session!.UserName.Trim();
                    ResetPlaybackProgressTrackingLocked();
                }

                _lastPlaybackPositionTicks = e.PlaybackPositionTicks;
                _lastPlaybackPositionObservedUtc = DateTime.UtcNow;
                _lastPlaybackProgressWasPaused = e.IsPaused;
            }

            ObserveTask(StartSyncForItem(e));
        }

        private async void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
        {
            if (TryGetConcurrentPlaybackWorker(e, out var concurrentWorker))
            {
                try
                {
                    await concurrentWorker.Service.HandleExternalPlaybackStoppedAsync(e).ConfigureAwait(false);
                }
                finally
                {
                    RemoveConcurrentPlaybackWorker(concurrentWorker);
                }

                return;
            }

            await HandlePlaybackStoppedAsync(e).ConfigureAwait(false);
        }

        internal async Task HandleExternalPlaybackStoppedAsync(PlaybackStopEventArgs e)
        {
            await HandlePlaybackStoppedAsync(e).ConfigureAwait(false);
        }

        private async Task HandlePlaybackStoppedAsync(PlaybackStopEventArgs e)
        {
            if (_isStopping)
                return;

            e = NormalizeRecoveredPlaybackStop(e);

            lock (_syncLock)
            {
                if (_externalPlaybackStartPending &&
                    string.Equals(_currentPlaySessionId, e.PlaySessionId, StringComparison.Ordinal))
                {
                    _externalPlaybackStopRequested = true;
                    _startupCts?.Cancel();
                }
            }

            if (e.Item != null && !IsSupportedPlaybackItem(e.Item))
            {
                lock (_syncLock)
                {
                    if (!string.Equals(_currentPlaySessionId, e.PlaySessionId, StringComparison.Ordinal) &&
                        !string.Equals(_startingPlaySessionId, e.PlaySessionId, StringComparison.Ordinal))
                    {
                        return;
                    }
                }
            }

            var manuallyStopped = false;
            var manualStopNotification = false;
            lock (_syncLock)
            {
                if (string.Equals(_manuallyStoppedPlaySessionId, e.PlaySessionId, StringComparison.Ordinal))
                {
                    manualStopNotification = true;
                    _manuallyStoppedPlaySessionId = null;
                    // A newer playback session may already be starting while the old
                    // session's stop notification is in flight. In that case, clear only
                    // the suppression marker and leave the newer session untouched.
                    manuallyStopped = string.Equals(_currentPlaySessionId, e.PlaySessionId, StringComparison.Ordinal) &&
                                      (_startingPlaySessionId == null ||
                                       string.Equals(_startingPlaySessionId, e.PlaySessionId, StringComparison.Ordinal));
                    if (manuallyStopped)
                    {
                        _currentPlaySessionId = null;
                        _recoveredSessionId = null;
                        _currentItemName = null;
                    }
                }
            }

            if (manualStopNotification)
            {
                if (manuallyStopped)
                    SetRuntimeStatus("Idle", "Playback stopped.");
                return;
            }

            if (_currentPlaySessionId != null &&
                !string.Equals(_currentPlaySessionId, e.PlaySessionId, StringComparison.Ordinal) &&
                !string.Equals(_startingPlaySessionId, e.PlaySessionId, StringComparison.Ordinal))
            {
                return;
            }

            _logger.LogInformation("Playback stopped for item {0}", e.Item?.Name ?? "Unknown");
            SetRuntimeStatus("Stopping", "Playback stopped; cleaning up.");

            // Cancel a startup before waiting for the lifecycle lock. The startup token is
            // assigned before it waits, so this also covers the window before _syncCts exists.
            lock (_syncLock)
            {
                if (_currentPlaySessionId != null &&
                    !string.Equals(_currentPlaySessionId, e.PlaySessionId, StringComparison.Ordinal) &&
                    !string.Equals(_startingPlaySessionId, e.PlaySessionId, StringComparison.Ordinal))
                {
                    return;
                }

                if (string.Equals(_startingPlaySessionId, e.PlaySessionId, StringComparison.Ordinal))
                    _startupCts?.Cancel();
                if (string.Equals(_currentPlaySessionId, e.PlaySessionId, StringComparison.Ordinal))
                    _syncCts?.Cancel();
            }

            // Serialize capture, process shutdown, restoration, and area deactivation with
            // the next startup. No state is captured before this lock is acquired.
            await _syncLifecycleLock.WaitAsync().ConfigureAwait(false);
            lock (_syncLock)
            {
                if (_isStopping ||
                    (_currentPlaySessionId != null &&
                     !string.Equals(_currentPlaySessionId, e.PlaySessionId, StringComparison.Ordinal)))
                {
                    _syncLifecycleLock.Release();
                    return;
                }
            }

            var config = Plugin.Instance?.Configuration;
            var bridgeConfig = _currentBridgeConfig;
            var savedLightStates = _savedLightStates;
            StopSync(deactivateArea: false, expectedPlaySessionId: e.PlaySessionId, clearSession: false);
            _currentBridgeConfig = null;
            _currentFrameResolution = null;
            _currentVideoScalingMode = null;
            _currentVideoDeinterlaceMode = null;
            _currentTargetFps = null;
            _currentAudioSensitivityPercent = null;
            _currentAudioNoiseGatePercent = null;
            _currentAudioFrequencies = null;
            _currentAudioBandGains = null;
            _currentAudioBandSpreadPercent = null;
            _currentAudioBeatPulsePercent = null;
            _currentAudioBeatPulseDecayPercent = null;
            _currentAudioColorPalette = null;
            _currentAudioSpatialMode = null;
            _currentAudioChannelMode = null;
            _currentSamplingBreadthPercent = null;
            _currentSamplingMode = null;
            _currentColorSmoothingPercent = null;

            try
            {
                await RestoreAndDeactivateAsync(
                    config,
                    bridgeConfig,
                    savedLightStates,
                    sessionOutcome: "Stopped");
            }
            finally
            {
                lock (_syncLock)
                {
                    if (string.Equals(_currentPlaySessionId, e.PlaySessionId, StringComparison.Ordinal))
                    {
                        _currentPlaySessionId = null;
                        _recoveredSessionId = null;
                        _externalPlaybackStartPending = false;
                        _externalPlaybackStopRequested = false;
                        ResetPlaybackProgressTrackingLocked();
                    }
                }
                _syncLifecycleLock.Release();
            }
        }

        private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
        {
            if (_isStopping)
                return;

            if (TryGetConcurrentPlaybackWorker(e, out var concurrentWorker))
            {
                concurrentWorker.Service.HandleExternalPlaybackProgress(e);
                return;
            }

            if (!IsPlaybackUserSyncEnabled(e))
                return;

            e = NormalizeRecoveredPlaybackEvent(e);

            if (e.Item != null && !IsSupportedPlaybackItem(e.Item))
                return;

            lock (_syncLock)
            {
                if (string.Equals(_manuallyStoppedPlaySessionId, e.PlaySessionId, StringComparison.Ordinal))
                    return;
            }

            if (_currentPlaySessionId != null && !string.Equals(_currentPlaySessionId, e.PlaySessionId, StringComparison.Ordinal))
            {
                return;
            }

            var observedAtUtc = DateTime.UtcNow;
            var shouldRestartForSeek = false;
            lock (_syncLock)
            {
                var isCurrentSession = string.Equals(_currentPlaySessionId, e.PlaySessionId, StringComparison.Ordinal);
                if (isCurrentSession)
                {
                    var wasPaused = _lastPlaybackProgressWasPaused;
                    shouldRestartForSeek = !e.IsPaused &&
                        !wasPaused &&
                        !_seekRestartInFlight &&
                        _startingPlaySessionId == null &&
                        _syncCts != null &&
                        !_syncCts.IsCancellationRequested &&
                        IsPlaybackSeek(
                            _lastPlaybackPositionTicks,
                            _lastPlaybackPositionObservedUtc,
                            e.PlaybackPositionTicks,
                            observedAtUtc);

                    if (e.PlaybackPositionTicks.HasValue)
                    {
                        _lastPlaybackPositionTicks = e.PlaybackPositionTicks;
                        _lastPlaybackPositionObservedUtc = observedAtUtc;
                    }
                    else if (e.IsPaused)
                    {
                        // Reset the wall-clock baseline when a pause event has no position;
                        // the pause duration must not look like a forward seek on resume.
                        _lastPlaybackPositionObservedUtc = observedAtUtc;
                    }

                    _lastPlaybackProgressWasPaused = e.IsPaused;
                    if (shouldRestartForSeek)
                    {
                        _seekRestartInFlight = true;
                        _seekRestartCount++;
                        _lastSeekPositionSeconds = e.PlaybackPositionTicks.HasValue
                            ? TimeSpan.FromTicks(e.PlaybackPositionTicks.Value).TotalSeconds
                            : null;
                    }
                }
            }

            if (shouldRestartForSeek)
            {
                var seekPosition = e.PlaybackPositionTicks.HasValue
                    ? TimeSpan.FromTicks(e.PlaybackPositionTicks.Value).TotalSeconds
                    : 0;
                _logger.LogInformation(
                    "Playback seek detected at {0:0.0}s for session {1}; restarting the video capture stream",
                    seekPosition,
                    e.PlaySessionId);
                SetRuntimeStatus("Resyncing", "Playback seek detected; resynchronizing Hue output.");
                ObserveTask(RestartSyncAfterSeekAsync(e));
                return;
            }

            // Handle pause/unpause events
            if (e.IsPaused && _syncCts != null && !_syncCts.IsCancellationRequested)
            {
                // Ignore pause events during initial buffering/startup grace period
                var elapsed = (DateTime.UtcNow - _syncStartTime).TotalMilliseconds;
                if (elapsed < MinSyncDurationBeforePauseMs)
                {
                    _logger.LogDebug("Ignoring pause event during startup grace period ({0}ms elapsed)", (int)elapsed);
                    return;
                }

                var configuredPauseBehavior = Plugin.Instance?.Configuration?.PauseBehavior;
                string? activePauseBehavior;
                lock (_syncLock)
                {
                    activePauseBehavior = _activePauseBehavior;
                }

                var pauseRestoresLights = string.Equals(
                    activePauseBehavior ?? configuredPauseBehavior,
                    PluginConfiguration.PauseBehaviorRestoreLightState,
                    StringComparison.OrdinalIgnoreCase);
                _logger.LogInformation(
                    "Playback paused, stopping light sync; pause behavior is {0}",
                    pauseRestoresLights ? "restore" : "keep-last-colors");
                SetRuntimeStatus(
                    "Paused",
                    pauseRestoresLights
                        ? "Playback paused; lights are being restored."
                        : "Playback paused; keeping the last synced colors.");
                Task pauseCleanup;
                lock (_syncLock)
                {
                    if (_isStopping || _pauseCleanupTask != null)
                        return;

                    _pauseCleanupSessionId = e.PlaySessionId;
                    pauseCleanup = StopSyncForPauseAsync(e.PlaySessionId);
                    _pauseCleanupTask = pauseCleanup;
                }

                _ = pauseCleanup.ContinueWith(
                    _ =>
                    {
                        lock (_syncLock)
                        {
                            if (ReferenceEquals(_pauseCleanupTask, pauseCleanup))
                            {
                                _pauseCleanupTask = null;
                                _pauseCleanupSessionId = null;
                            }
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                ObserveTask(pauseCleanup);
            }
            else if (!e.IsPaused &&
                     (_syncCts == null || _syncCts.IsCancellationRequested) &&
                     Volatile.Read(ref _syncStartsInFlight) == 0)
            {
                _logger.LogInformation("Playback resumed, restarting light sync");
                lock (_syncLock)
                {
                    if (_isStopping)
                        return;

                    _currentPlaySessionId = e.PlaySessionId;
                }
                ObserveTask(StartSyncForItem(e));
            }
        }

        internal void HandleExternalPlaybackProgress(PlaybackProgressEventArgs e)
        {
            OnPlaybackProgress(null, e);
        }

        private async Task StopSyncForPauseAsync(string playSessionId)
        {
            lock (_syncLock)
            {
                if (_currentPlaySessionId != null &&
                    !string.Equals(_currentPlaySessionId, playSessionId, StringComparison.Ordinal))
                {
                    return;
                }

                _syncCts?.Cancel();
            }

            await _syncLifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_syncLock)
                {
                    if (_isStopping ||
                        (_currentPlaySessionId != null &&
                         !string.Equals(_currentPlaySessionId, playSessionId, StringComparison.Ordinal)))
                    {
                        return;
                    }
                }

                var config = Plugin.Instance?.Configuration;
                string? activePauseBehavior;
                lock (_syncLock)
                {
                    activePauseBehavior = _activePauseBehavior;
                }

                var restoreOnPause = string.Equals(
                    activePauseBehavior ?? config?.PauseBehavior,
                    PluginConfiguration.PauseBehaviorRestoreLightState,
                    StringComparison.OrdinalIgnoreCase);
                var bridgeConfig = _currentBridgeConfig;
                var savedLightStates = restoreOnPause ? _savedLightStates : null;
                StopSync(deactivateArea: false, expectedPlaySessionId: playSessionId);

                if (restoreOnPause)
                {
                    _currentBridgeConfig = null;
                    _currentFrameResolution = null;
                    _currentVideoScalingMode = null;
                    _currentVideoDeinterlaceMode = null;
                    _currentTargetFps = null;
                    _currentAudioSensitivityPercent = null;
                    _currentAudioNoiseGatePercent = null;
                    _currentAudioFrequencies = null;
                    _currentAudioBandGains = null;
                    _currentAudioBandSpreadPercent = null;
                    _currentAudioBeatPulsePercent = null;
                    _currentAudioBeatPulseDecayPercent = null;
                    _currentAudioColorPalette = null;
                    _currentAudioSpatialMode = null;
                    _currentAudioChannelMode = null;
                    _currentSamplingBreadthPercent = null;
                    _currentSamplingMode = null;
                    _currentColorSmoothingPercent = null;
                    await RestoreAndDeactivateAsync(
                        config,
                        bridgeConfig,
                        savedLightStates,
                        publishIdleStatus: false,
                        clearCurrentItem: false,
                        recordSessionSummary: false).ConfigureAwait(false);
                    SetRuntimeStatus(
                        "Paused",
                        GetRuntimeStatus().CleanupWarning == null
                            ? "Playback paused; original light state restored."
                            : "Playback paused; light restoration completed with warnings.");
                }
                else if (bridgeConfig != null)
                {
                    await _hueClient.StopEntertainmentArea(
                        bridgeConfig.Value.BridgeIp,
                        bridgeConfig.Value.AppKey,
                        bridgeConfig.Value.AreaId).ConfigureAwait(false);
                    _bridgeAreaDeactivated = true;
                    ReleasePlaybackLifecycleLease();
                    SetRuntimeStatus("Paused", "Playback paused; waiting to resume.");
                }
                else
                {
                    ReleasePlaybackLifecycleLease();
                }

                lock (_syncLock)
                {
                    _activeColorProcessingSettings = null;
                    _activeExecutionSettings = null;
                    _activeChannelIds = null;
                }
            }
            finally
            {
                _syncLifecycleLock.Release();
            }
        }

        private void StopSync(bool deactivateArea = true, string? expectedPlaySessionId = null, bool clearSession = true)
        {
            CancellationTokenSource? syncCts;
            lock (_syncLock)
            {
                if (expectedPlaySessionId != null &&
                    _currentPlaySessionId != null &&
                    !string.Equals(_currentPlaySessionId, expectedPlaySessionId, StringComparison.Ordinal))
                {
                    return;
                }

                syncCts = _syncCts;
                _syncCts = null;
                if (clearSession)
                {
                    _currentPlaySessionId = null;
                    _recoveredSessionId = null;
                }
            }

            syncCts?.Cancel();
            _ffmpegStreamer?.Stop();
            _hueStreamer?.StopStream();
            syncCts?.Dispose();

            if (deactivateArea && _currentBridgeConfig != null && !_bridgeAreaDeactivated)
            {
                var cfg = _currentBridgeConfig.Value;
                ObserveTask(_hueClient.StopEntertainmentArea(cfg.BridgeIp, cfg.AppKey, cfg.AreaId));
            }
        }

        /// <summary>
        /// Sends colors to lights using a temporary streamer instance with explicit bridge config
        /// </summary>
        private async Task<bool> SendTemporaryColorsWithConfig(
            string bridgeIp,
            string appKey,
            string clientKey,
            string areaId,
            Dictionary<int, byte[]> channelColors,
            int delayMs,
            CancellationToken cancellationToken = default)
        {
            var activated = false;
            var succeeded = false;
            try
            {
                // Must activate the area before opening a DTLS session
                activated = await _hueClient.StartEntertainmentArea(
                    bridgeIp,
                    appKey,
                    areaId,
                    cancellationToken).ConfigureAwait(false);
                if (!activated)
                {
                    _logger.LogWarning("SendTemporaryColorsWithConfig: could not activate area {0}, skipping", areaId);
                    return false;
                }

                await Task.Delay(EntertainmentAreaActivationDelayMs, cancellationToken).ConfigureAwait(false);

                var tempStreamer = new HueStreamer(_loggerFactory.CreateLogger<HueStreamer>());
                try
                {
                    tempStreamer.OnBeforeReconnectWithCancellation = token =>
                        _hueClient.StartEntertainmentArea(bridgeIp, appKey, areaId, token);
                    await tempStreamer.StartStreamAsync(
                        bridgeIp,
                        appKey,
                        clientKey,
                        cancellationToken).ConfigureAwait(false);
                    if (!tempStreamer.IsHealthy())
                    {
                        _logger.LogWarning("SendTemporaryColorsWithConfig: DTLS stream did not start for area {0}", areaId);
                        return false;
                    }

                    if (!await tempStreamer.SendColors(
                            areaId,
                            channelColors,
                            cancellationToken: cancellationToken).ConfigureAwait(false))
                    {
                        _logger.LogWarning("SendTemporaryColorsWithConfig: DTLS stream could not send colors for area {0}", areaId);
                        return false;
                    }

                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    succeeded = true;
                }
                finally
                {
                    tempStreamer.StopStream();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SendTemporaryColorsWithConfig failed for area {0}", areaId);
                return false;
            }
            finally
            {
                if (activated && !await _hueClient.StopEntertainmentAreaWithResult(bridgeIp, appKey, areaId).ConfigureAwait(false))
                {
                    _logger.LogWarning("SendTemporaryColorsWithConfig: could not deactivate area {0}", areaId);
                    succeeded = false;
                }
            }

            return succeeded;
        }

        /// <summary>
        /// Applies cinema mode by dimming lights to the effective user level
        /// </summary>
        private async Task ApplyCinemaMode(
            string bridgeIp,
            string appKey,
            string clientKey,
            string areaId,
            System.Text.Json.JsonElement areaConfig,
            int brightnessDimLevel,
            IReadOnlySet<int>? channelIds,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (!areaConfig.TryGetProperty("channels", out var channels))
                    return;

                var dimLevel = Math.Clamp(brightnessDimLevel, 0, 100);
                var dimBrightness = (byte)(dimLevel * 255 / 100 / 2); // Divide by 2 for 16-bit compatibility

                var channelColors = new Dictionary<int, byte[]>();
                foreach (var channel in channels.EnumerateArray())
                {
                    var channelId = channel.GetProperty("channel_id").GetInt32();
                    if (channelIds != null && !channelIds.Contains(channelId))
                        continue;

                    // Warm white color at dim level
                    channelColors[channelId] = new byte[] { dimBrightness, dimBrightness, dimBrightness, dimBrightness, (byte)(dimBrightness * 0.8), (byte)(dimBrightness * 0.8) };
                }

                if (channelColors.Count == 0)
                    return;

                // Send dim command before starting stream
                await SendTemporaryColorsWithConfig(
                    bridgeIp,
                    appKey,
                    clientKey,
                    areaId,
                    channelColors,
                    CinemaModeDimmingDelayMs,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply cinema mode");
            }
        }

        /// <summary>
        /// Restores lights to normal brightness after playback
        /// </summary>
        private async Task<bool> RestoreLightsAfterPlayback(
            string bridgeIp,
            string appKey,
            string clientKey,
            string areaId,
            IReadOnlySet<int>? channelIds)
        {
            try
            {
                var areaConfig = await _hueClient.GetEntertainmentConfiguration(bridgeIp, appKey, areaId);
                if (areaConfig == null || !areaConfig.Value.TryGetProperty("channels", out var channels))
                    return false;

                var channelColors = new Dictionary<int, byte[]>();
                foreach (var channel in channels.EnumerateArray())
                {
                    var channelId = channel.GetProperty("channel_id").GetInt32();
                    if (channelIds != null && !channelIds.Contains(channelId))
                        continue;

                    // Full white
                    channelColors[channelId] = new byte[] { FullBrightnessValue, FullBrightnessValue, FullBrightnessValue, FullBrightnessValue, FullBrightnessValue, FullBrightnessValue };
                }

                if (channelColors.Count == 0)
                    return true;

                return await SendTemporaryColorsWithConfig(
                    bridgeIp,
                    appKey,
                    clientKey,
                    areaId,
                    channelColors,
                    RestoreLightsDelayMs).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to restore lights");
                return false;
            }
        }

        internal static (
            bool UseCinemaMode,
            int BrightnessDimLevel,
            bool RestoreLightState) ResolvePlaybackSettings(
            PluginConfiguration config,
            Guid userId)
        {
            ArgumentNullException.ThrowIfNull(config);
            var overrides = config.GetPlaybackOverridesForUser(userId);
            return (
                overrides.UseCinemaMode ?? config.UseCinemaMode,
                Math.Clamp(overrides.BrightnessDimLevel ?? config.BrightnessDimLevel, 0, 100),
                overrides.RestoreLightState ?? config.RestoreLightState);
        }

        internal static string ResolvePauseBehavior(PluginConfiguration config, Guid userId)
        {
            ArgumentNullException.ThrowIfNull(config);
            return config.GetPauseBehaviorOverrideForUser(userId) ?? config.PauseBehavior;
        }

        internal static int ResolveAudioSensitivityPercent(PluginConfiguration config, Guid userId)
        {
            ArgumentNullException.ThrowIfNull(config);
            return Math.Clamp(
                config.GetAudioSensitivityPercentForUser(userId),
                PluginConfiguration.MinAudioSensitivityPercent,
                PluginConfiguration.MaxAudioSensitivityPercent);
        }

        internal static int ResolveAudioNoiseGatePercent(PluginConfiguration config, Guid userId)
        {
            ArgumentNullException.ThrowIfNull(config);
            return Math.Clamp(
                config.GetAudioNoiseGatePercentForUser(userId),
                PluginConfiguration.MinAudioNoiseGatePercent,
                PluginConfiguration.MaxAudioNoiseGatePercent);
        }

        internal static (int LowFrequencyHz, int MidFrequencyHz, int HighFrequencyHz) ResolveAudioFrequencies(
            PluginConfiguration config,
            Guid userId)
        {
            ArgumentNullException.ThrowIfNull(config);
            var frequencies = config.GetAudioFrequenciesForUser(userId);
            return PluginConfiguration.NormalizeAudioFrequencyProfile(
                frequencies.LowFrequencyHz,
                frequencies.MidFrequencyHz,
                frequencies.HighFrequencyHz);
        }

        internal static (int LowGainPercent, int MidGainPercent, int HighGainPercent) ResolveAudioBandGains(
            PluginConfiguration config,
            Guid userId)
        {
            ArgumentNullException.ThrowIfNull(config);
            var gains = config.GetAudioBandGainsForUser(userId);
            return (
                Math.Clamp(gains.LowGainPercent, PluginConfiguration.MinAudioBandGainPercent, PluginConfiguration.MaxAudioBandGainPercent),
                Math.Clamp(gains.MidGainPercent, PluginConfiguration.MinAudioBandGainPercent, PluginConfiguration.MaxAudioBandGainPercent),
                Math.Clamp(gains.HighGainPercent, PluginConfiguration.MinAudioBandGainPercent, PluginConfiguration.MaxAudioBandGainPercent));
        }

        internal static int ResolveAudioBandSpreadPercent(PluginConfiguration config, Guid userId)
        {
            ArgumentNullException.ThrowIfNull(config);
            return Math.Clamp(
                config.GetAudioBandSpreadPercentForUser(userId),
                PluginConfiguration.MinAudioBandSpreadPercent,
                PluginConfiguration.MaxAudioBandSpreadPercent);
        }

        internal static int ResolveAudioBeatPulsePercent(PluginConfiguration config, Guid userId)
        {
            ArgumentNullException.ThrowIfNull(config);
            return Math.Clamp(
                config.GetAudioBeatPulsePercentForUser(userId),
                PluginConfiguration.MinAudioBeatPulsePercent,
                PluginConfiguration.MaxAudioBeatPulsePercent);
        }

        internal static int ResolveAudioBeatPulseDecayPercent(PluginConfiguration config, Guid userId)
        {
            ArgumentNullException.ThrowIfNull(config);
            return Math.Clamp(
                config.GetAudioBeatPulseDecayPercentForUser(userId),
                PluginConfiguration.MinAudioBeatPulseDecayPercent,
                PluginConfiguration.MaxAudioBeatPulseDecayPercent);
        }

        internal static string ResolveAudioColorPalette(PluginConfiguration config, Guid userId)
        {
            ArgumentNullException.ThrowIfNull(config);
            return PluginConfiguration.TryNormalizeAudioColorPalette(
                    config.GetAudioColorPaletteForUser(userId),
                    out var normalized)
                ? normalized
                : PluginConfiguration.AudioColorPaletteSpectrum;
        }

        internal static string ResolveAudioSpatialMode(PluginConfiguration config, Guid userId)
        {
            ArgumentNullException.ThrowIfNull(config);
            return PluginConfiguration.TryNormalizeAudioSpatialMode(
                    config.GetAudioSpatialModeForUser(userId),
                    out var normalized)
                ? normalized
                : PluginConfiguration.AudioSpatialModeSpatial;
        }

        internal static string ResolveAudioChannelMode(PluginConfiguration config, Guid userId)
        {
            ArgumentNullException.ThrowIfNull(config);
            return PluginConfiguration.TryNormalizeAudioChannelMode(
                    config.GetAudioChannelModeForUser(userId),
                    out var normalized)
                ? normalized
                : PluginConfiguration.AudioChannelModeMono;
        }

        internal static (
            int TargetFps,
            string FrameResolution,
            string VideoScalingMode,
            string VideoDeinterlaceMode,
            int SamplingBreadthPercent,
            string SamplingMode,
            int ColorSmoothingPercent) ResolvePerformanceSettings(
            PluginConfiguration config,
            Guid userId)
        {
            ArgumentNullException.ThrowIfNull(config);
            var overrides = config.GetPerformanceOverridesForUser(userId);
            return (
                Math.Clamp(overrides.TargetFps ?? config.TargetFps, MinFps, MaxFps),
                NormalizeFrameResolution(overrides.FrameResolution ?? config.FrameResolution),
                NormalizeVideoScalingMode(overrides.VideoScalingMode ?? config.VideoScalingMode),
                NormalizeVideoDeinterlaceMode(overrides.VideoDeinterlaceMode ?? config.VideoDeinterlaceMode),
                Math.Clamp(
                    overrides.SamplingBreadthPercent ?? config.SamplingBreadthPercent,
                    MinSamplingBreadthPercent,
                    MaxSamplingBreadthPercent),
                NormalizeSamplingMode(overrides.SamplingMode ?? config.SamplingMode),
                Math.Clamp(
                    overrides.ColorSmoothingPercent ?? config.ColorSmoothingPercent,
                    MinColorSmoothingPercent,
                    MaxColorSmoothingPercent));
        }

        internal static (
            int BrightnessBoost,
            int RedGain,
            int GreenGain,
            int BlueGain,
            int ColorSaturation,
            int HueShiftDegrees,
            int OutputBrightnessPercent,
            int BlackoutThreshold,
            int ColorChangeThreshold) ResolveColorProcessingSettings(
            PluginConfiguration config,
            Guid userId)
        {
            ArgumentNullException.ThrowIfNull(config);
            var processingOverrides = config.GetColorProcessingOverridesForUser(userId);
            var channelGainOverrides = config.GetColorChannelGainOverridesForUser(userId);
            var thresholdOverrides = config.GetColorThresholdOverridesForUser(userId);
            return (
                Math.Clamp(processingOverrides.BrightnessBoost ?? config.BrightnessBoost, 50, 200),
                Math.Clamp(channelGainOverrides.RedGain ?? config.RedGain, 50, 200),
                Math.Clamp(channelGainOverrides.GreenGain ?? config.GreenGain, 50, 200),
                Math.Clamp(channelGainOverrides.BlueGain ?? config.BlueGain, 50, 200),
                Math.Clamp(processingOverrides.ColorSaturation ?? config.ColorSaturation, 0, 200),
                Math.Clamp(processingOverrides.HueShiftDegrees ?? config.HueShiftDegrees, -180, 180),
                Math.Clamp(processingOverrides.OutputBrightnessPercent ?? config.OutputBrightnessPercent, 0, 100),
                Math.Clamp(thresholdOverrides.BlackoutThreshold ?? config.BlackoutThreshold, 0, 255),
                Math.Clamp(thresholdOverrides.ColorChangeThreshold ?? config.ColorChangeThreshold, 0, 255));
        }

        internal static (
            bool UseGpu,
            string CustomFfmpegFlags,
            int FfmpegStallTimeoutSeconds,
            int NetworkRetryAttempts) ResolveExecutionSettings(
            PluginConfiguration config,
            Guid userId)
        {
            ArgumentNullException.ThrowIfNull(config);
            var overrides = config.GetExecutionOverridesForUser(userId);
            return (
                overrides.UseGpu ?? config.UseGpu,
                (overrides.CustomFfmpegFlags ?? config.CustomFfmpegFlags ?? string.Empty).Trim(),
                Math.Clamp(
                    overrides.FfmpegStallTimeoutSeconds ?? config.FfmpegStallTimeoutSeconds,
                    1,
                    60),
                Math.Clamp(
                    overrides.NetworkRetryAttempts ?? config.NetworkRetryAttempts,
                    0,
                    10));
        }

        internal static IReadOnlySet<int>? ResolveChannelIds(PluginConfiguration config, Guid userId)
        {
            ArgumentNullException.ThrowIfNull(config);
            var channelIds = config.GetChannelIdsForUser(userId);
            return channelIds == null ? null : new HashSet<int>(channelIds);
        }

        private static string FormatChannelIds(IReadOnlySet<int>? channelIds)
            => channelIds == null
                ? "All channels"
                : string.Join(", ", channelIds.OrderBy(channelId => channelId));

        private static string NormalizeFrameResolution(string? value)
        {
            if (string.Equals(value?.Trim(), PluginConfiguration.FrameResolutionLow, StringComparison.OrdinalIgnoreCase))
                return PluginConfiguration.FrameResolutionLow;

            if (string.Equals(value?.Trim(), PluginConfiguration.FrameResolutionHigh, StringComparison.OrdinalIgnoreCase))
                return PluginConfiguration.FrameResolutionHigh;

            return PluginConfiguration.FrameResolutionStandard;
        }

        private static string NormalizeVideoScalingMode(string? value)
        {
            if (string.Equals(value?.Trim(), PluginConfiguration.VideoScalingModeFit, StringComparison.OrdinalIgnoreCase))
                return PluginConfiguration.VideoScalingModeFit;

            if (string.Equals(value?.Trim(), PluginConfiguration.VideoScalingModeCrop, StringComparison.OrdinalIgnoreCase))
                return PluginConfiguration.VideoScalingModeCrop;

            return PluginConfiguration.VideoScalingModeStretch;
        }

        private static string NormalizeVideoDeinterlaceMode(string? value)
        {
            if (string.Equals(value?.Trim(), PluginConfiguration.VideoDeinterlaceModeAuto, StringComparison.OrdinalIgnoreCase))
                return PluginConfiguration.VideoDeinterlaceModeAuto;

            if (string.Equals(value?.Trim(), PluginConfiguration.VideoDeinterlaceModeOn, StringComparison.OrdinalIgnoreCase))
                return PluginConfiguration.VideoDeinterlaceModeOn;

            return PluginConfiguration.VideoDeinterlaceModeOff;
        }

        private static string NormalizeSamplingMode(string? value)
        {
            if (string.Equals(value?.Trim(), PluginConfiguration.SamplingModeCenterWeighted, StringComparison.OrdinalIgnoreCase))
                return PluginConfiguration.SamplingModeCenterWeighted;

            if (string.Equals(value?.Trim(), PluginConfiguration.SamplingModeCenterPixel, StringComparison.OrdinalIgnoreCase))
                return PluginConfiguration.SamplingModeCenterPixel;

            return PluginConfiguration.SamplingModeAverage;
        }

        internal static int CalculateSamplingDistance(int samplingBreadthPercent)
            => CalculateSamplingDistance(samplingBreadthPercent, FrameWidth, FrameHeight);

        internal static int CalculateSamplingDistance(
            int samplingBreadthPercent,
            int frameWidth,
            int frameHeight)
        {
            var normalizedPercent = samplingBreadthPercent <= 0
                ? DefaultSamplingBreadthPercent
                : Math.Clamp(samplingBreadthPercent, MinSamplingBreadthPercent, MaxSamplingBreadthPercent);
            var averageFrameSize = (frameWidth + frameHeight) / 2;
            return Math.Max(1, (int)(normalizedPercent / 100.0 * averageFrameSize));
        }

        internal static byte[] SampleRegionColor(
            byte[] frame,
            int centerX,
            int centerY,
            int distance,
            string? samplingMode)
            => SampleRegionColor(
                frame,
                centerX,
                centerY,
                distance,
                samplingMode,
                FrameWidth,
                FrameHeight);

        internal static byte[] SampleRegionColor(
            byte[] frame,
            int centerX,
            int centerY,
            int distance,
            string? samplingMode,
            int frameWidth,
            int frameHeight)
        {
            var normalizedDistance = Math.Clamp(distance, 0, Math.Max(frameWidth, frameHeight));
            var mode = string.Equals(samplingMode, PluginConfiguration.SamplingModeCenterPixel, StringComparison.OrdinalIgnoreCase)
                ? PluginConfiguration.SamplingModeCenterPixel
                : string.Equals(samplingMode, PluginConfiguration.SamplingModeCenterWeighted, StringComparison.OrdinalIgnoreCase)
                    ? PluginConfiguration.SamplingModeCenterWeighted
                    : PluginConfiguration.SamplingModeAverage;

            if (mode == PluginConfiguration.SamplingModeCenterPixel)
            {
                var clampedCenterX = Math.Clamp(centerX, 0, frameWidth - 1);
                var clampedCenterY = Math.Clamp(centerY, 0, frameHeight - 1);
                var centerIndex = (clampedCenterY * frameWidth + clampedCenterX) * BytesPerPixel;
                return new[] { frame[centerIndex], frame[centerIndex + 1], frame[centerIndex + 2] };
            }

            // Keep the Average mode's original half-open bounds unchanged so existing
            // configurations produce the same colors as before this setting was added.
            var minX = (int)Math.Max(0L, (long)centerX - normalizedDistance);
            var maxX = (int)Math.Min(frameWidth, (long)centerX + normalizedDistance);
            var minY = (int)Math.Max(0L, (long)centerY - normalizedDistance);
            var maxY = (int)Math.Min(frameHeight, (long)centerY + normalizedDistance);
            long redSum = 0;
            long greenSum = 0;
            long blueSum = 0;
            long totalWeight = 0;

            for (var y = minY; y < maxY; y++)
            {
                var rowStart = y * frameWidth * BytesPerPixel;
                for (var x = minX; x < maxX; x++)
                {
                    var index = rowStart + x * BytesPerPixel;
                    var weight = mode == PluginConfiguration.SamplingModeCenterWeighted
                        ? (int)Math.Max(
                            1L,
                            normalizedDistance + 1L - Math.Max(Math.Abs((long)x - centerX), Math.Abs((long)y - centerY)))
                        : 1;
                    redSum += (long)frame[index] * weight;
                    greenSum += (long)frame[index + 1] * weight;
                    blueSum += (long)frame[index + 2] * weight;
                    totalWeight += weight;
                }
            }

            if (totalWeight == 0)
                return new byte[BytesPerPixel];

            return new[]
            {
                (byte)(redSum / totalWeight),
                (byte)(greenSum / totalWeight),
                (byte)(blueSum / totalWeight)
            };
        }

        internal static bool ShouldCaptureLightState(
            bool restoreLightState,
            bool hasSavedLightStates,
            string? savedLightStatePlaySessionId,
            string playSessionId)
        {
            return restoreLightState &&
                   (!hasSavedLightStates ||
                    !string.Equals(savedLightStatePlaySessionId, playSessionId, StringComparison.Ordinal));
        }

        internal static Dictionary<int, byte[]> ApplyTemporalSmoothing(
            IReadOnlyDictionary<int, byte[]> currentColors,
            IReadOnlyDictionary<int, byte[]> previousColors,
            int smoothingPercent)
        {
            var normalizedPercent = Math.Clamp(
                smoothingPercent,
                MinColorSmoothingPercent,
                MaxColorSmoothingPercent);
            var previousWeight = normalizedPercent / 100.0;
            var currentWeight = 1.0 - previousWeight;
            var smoothedColors = new Dictionary<int, byte[]>(currentColors.Count);

            foreach (var kvp in currentColors)
            {
                var current = kvp.Value;
                if (current.Length < 3)
                {
                    smoothedColors[kvp.Key] = current;
                    continue;
                }

                if (normalizedPercent == 0 ||
                    !previousColors.TryGetValue(kvp.Key, out var previous) ||
                    previous.Length < 3)
                {
                    smoothedColors[kvp.Key] = new[] { current[0], current[1], current[2] };
                    continue;
                }

                smoothedColors[kvp.Key] = new[]
                {
                    BlendColorChannel(current[0], previous[0], currentWeight, previousWeight),
                    BlendColorChannel(current[1], previous[1], currentWeight, previousWeight),
                    BlendColorChannel(current[2], previous[2], currentWeight, previousWeight)
                };
            }

            return smoothedColors;
        }

        /// <summary>
        /// Applies the final output-brightness scale after color adjustments while keeping
        /// the channel in the RGB byte range used by the Hue stream encoder.
        /// </summary>
        internal static double ApplyOutputBrightness(double channel, int outputBrightnessPercent)
        {
            var normalizedPercent = Math.Clamp(outputBrightnessPercent, 0, 100) / 100.0;
            return Math.Clamp(channel * normalizedPercent, 0, 255);
        }

        /// <summary>
        /// Applies independent RGB channel gains for room-specific white-balance correction.
        /// Gains are clamped to the supported 50-200% range and output remains byte-safe.
        /// </summary>
        internal static (double Red, double Green, double Blue) ApplyColorChannelGains(
            double red,
            double green,
            double blue,
            int redGain,
            int greenGain,
            int blueGain)
        {
            return (
                ApplyColorChannelGain(red, redGain),
                ApplyColorChannelGain(green, greenGain),
                ApplyColorChannelGain(blue, blueGain));
        }

        private static double ApplyColorChannelGain(double channel, int gain)
        {
            var normalizedGain = Math.Clamp(gain, 50, 200) / 100.0;
            return Math.Clamp(channel * normalizedGain, 0, 255);
        }

        /// <summary>
        /// Rotates a normalized HSL hue and wraps the result into the [0, 1) range.
        /// </summary>
        internal static double ApplyHueShift(double hue, int hueShiftDegrees)
        {
            var shiftedHue = (hue + hueShiftDegrees / 360.0) % 1.0;
            return shiftedHue < 0 ? shiftedHue + 1.0 : shiftedHue;
        }

        private static byte BlendColorChannel(
            byte current,
            byte previous,
            double currentWeight,
            double previousWeight)
        {
            var blended = Math.Round(
                current * currentWeight + previous * previousWeight,
                MidpointRounding.AwayFromZero);
            return (byte)Math.Clamp((int)blended, 0, byte.MaxValue);
        }

        private static async Task<(int BytesRead, bool TimedOut)> ReadFrameAsync(
            Stream videoStream,
            byte[] buffer,
            int frameSize,
            CancellationToken token,
            TimeSpan timeout)
        {
            using var frameReadCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            frameReadCts.CancelAfter(timeout);

            var bytesRead = 0;
            try
            {
                while (bytesRead < frameSize)
                {
                    var bytes = await videoStream.ReadAsync(
                        buffer,
                        bytesRead,
                        frameSize - bytesRead,
                        frameReadCts.Token).ConfigureAwait(false);
                    if (bytes == 0)
                        break;

                    bytesRead += bytes;
                }

                return (bytesRead, false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && frameReadCts.IsCancellationRequested)
            {
                return (bytesRead, true);
            }
        }

        private Task RunSyncLoop(
            Stream videoStream,
            Dictionary<int, (double x, double z)> lights,
            string areaId,
            int targetFrameDurationMs,
            CancellationTokenSource expectedSyncCts,
            string playSessionId)
        {
            return RunSyncLoopWithSampling(
                videoStream,
                lights,
                areaId,
                targetFrameDurationMs,
                expectedSyncCts,
                playSessionId,
                DefaultSamplingBreadthPercent,
                PluginConfiguration.SamplingModeAverage,
                PluginConfiguration.FrameResolutionStandard,
                0,
                (
                    BrightnessBoost: 100,
                    RedGain: 100,
                    GreenGain: 100,
                    BlueGain: 100,
                    ColorSaturation: 100,
                    HueShiftDegrees: 0,
                    OutputBrightnessPercent: 100,
                    BlackoutThreshold: 15,
                    ColorChangeThreshold: 10));
        }

        private async Task RunSyncLoopWithSampling(
            Stream videoStream,
            Dictionary<int, (double x, double z)> lights,
            string areaId,
            int targetFrameDurationMs,
            CancellationTokenSource expectedSyncCts,
            string playSessionId,
            int samplingBreadthPercent,
            string samplingMode,
            string frameResolution,
            int colorSmoothingPercent,
            (
                int BrightnessBoost,
                int RedGain,
                int GreenGain,
                int BlueGain,
                int ColorSaturation,
                int HueShiftDegrees,
                int OutputBrightnessPercent,
                int BlackoutThreshold,
                int ColorChangeThreshold) colorProcessingSettings)
        {
            var (frameWidth, frameHeight) = PluginConfiguration.GetFrameDimensions(frameResolution);
            int frameSize = frameWidth * frameHeight * BytesPerPixel;
            byte[] buffer = new byte[frameSize];
            var token = expectedSyncCts.Token;
            var streamEnded = false;
            var streamFailed = false;
            var consecutiveSendFailures = 0;
            var previousChannelColors = new Dictionary<int, byte[]>();

            // Pre-calculate bounds for each light based on position
            // Following HarmonizeProject logic: use x (horizontal) and z (vertical) for 2D screen plane
            int dist = CalculateSamplingDistance(samplingBreadthPercent, frameWidth, frameHeight);

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var loopTimer = Stopwatch.StartNew();

                    // Read full frame
                    var frameRead = await ReadFrameAsync(
                        videoStream,
                        buffer,
                        frameSize,
                        token,
                        GetFrameReadTimeout()).ConfigureAwait(false);
                    if (frameRead.TimedOut)
                    {
                        streamFailed = true;
                        SetRuntimeError("FFmpeg stopped producing video frames within the configured stall timeout.");
                        _logger.LogError(
                            "FFmpeg frame stream stalled after {0} bytes; stopping Hue sync for session {1}",
                            frameRead.BytesRead,
                            playSessionId);
                        break;
                    }

                    var bytesRead = frameRead.BytesRead;
                    if (bytesRead < frameSize)
                    {
                        _logger.LogInformation("End of video stream reached. Total frames processed: {0}", _ffmpegStreamer?.FramesProcessed ?? 0);
                        if (!token.IsCancellationRequested)
                        {
                            streamEnded = true;
                            SetRuntimeStatus("Ended", "The video stream ended.");
                        }
                        break;
                    }

                    // Mark frame read for health monitoring
                    _ffmpegStreamer?.MarkFrameRead();

                    var channelColors = new Dictionary<int, byte[]>();

                    // Performance optimization: Pre-allocate color calculation space
                    foreach (var kvp in lights)
                    {
                        // Calculate average color for this light's position
                        // Convert from Hue coordinate space to pixel coordinates
                        // Hue: x: -1 (left) to 1 (right), z: -1 (bottom) to 1 (top)
                        // Pixels: 0,0 is top-left; clamp to valid range [0, dimension-1]
                        int cx = (int)((kvp.Value.x + 1.0) * (frameWidth - 1) / 2.0);
                        int cy = (int)((1.0 - kvp.Value.z) * (frameHeight - 1) / 2.0); // Invert z for screen coordinates

                        channelColors[kvp.Key] = SampleRegionColor(
                            buffer,
                            cx,
                            cy,
                            dist,
                            samplingMode,
                            frameWidth,
                            frameHeight);
                    }

                    var isBlackout = colorProcessingSettings.BlackoutThreshold > 0 &&
                        channelColors.Count > 0 &&
                        channelColors.Values.Average(c => (c[0] + c[1] + c[2]) / 3.0) < colorProcessingSettings.BlackoutThreshold;

                    // Check blackout threshold - send dark colors if frame is mostly black.
                    if (isBlackout)
                    {
                        // Send black to all channels so lights actually dim during dark scenes.
                        // Clear temporal history so a later bright scene starts immediately
                        // instead of blending with a stale pre-blackout frame.
                        previousChannelColors.Clear();
                        var blackColors = new Dictionary<int, byte[]>();
                        foreach (var kvp in channelColors)
                        {
                            blackColors[kvp.Key] = new byte[] { 0, 0, 0, 0, 0, 0 };
                        }
                        var blackoutSent = await _hueStreamer!.SendColors(
                            areaId,
                            blackColors,
                            colorProcessingSettings.ColorChangeThreshold,
                            token);
                        if (!HandleDtlsSendResult(blackoutSent, token, ref consecutiveSendFailures))
                        {
                            streamFailed = true;
                            break;
                        }

                        var elapsedBlackout = loopTimer.ElapsedMilliseconds;
                        if (targetFrameDurationMs > 0)
                        {
                            var remainingBlackout = targetFrameDurationMs - (int)Math.Min(int.MaxValue, elapsedBlackout);
                            if (remainingBlackout > 0)
                            {
                                await Task.Delay(remainingBlackout, token);
                            }
                        }
                        continue;
                    }

                    if (colorSmoothingPercent > 0)
                    {
                        channelColors = ApplyTemporalSmoothing(
                            channelColors,
                            previousChannelColors,
                            colorSmoothingPercent);
                        previousChannelColors = channelColors;
                    }
                    else
                    {
                        previousChannelColors.Clear();
                    }

                    // Apply the captured brightness, channel-gain, saturation, hue, and output policies.
                    var processedColors = new Dictionary<int, byte[]>();
                    foreach (var kvp in channelColors)
                    {
                        var rgb = kvp.Value;
                        double r = rgb[0], g = rgb[1], b = rgb[2];

                        if (colorProcessingSettings.BrightnessBoost != 100)
                        {
                            double multiplier = colorProcessingSettings.BrightnessBoost / 100.0;
                            r = Math.Min(255, r * multiplier);
                            g = Math.Min(255, g * multiplier);
                            b = Math.Min(255, b * multiplier);
                        }

                        if (colorProcessingSettings.RedGain != 100 ||
                            colorProcessingSettings.GreenGain != 100 ||
                            colorProcessingSettings.BlueGain != 100)
                        {
                            var gainedRgb = ApplyColorChannelGains(
                                r,
                                g,
                                b,
                                colorProcessingSettings.RedGain,
                                colorProcessingSettings.GreenGain,
                                colorProcessingSettings.BlueGain);
                            r = gainedRgb.Red;
                            g = gainedRgb.Green;
                            b = gainedRgb.Blue;
                        }

                        if (colorProcessingSettings.ColorSaturation != 100 ||
                            colorProcessingSettings.HueShiftDegrees != 0)
                        {
                            // Convert to HSL, adjust saturation/hue, convert back to RGB.
                            var (hue, sat, lightness) = RgbToHsl(r / 255.0, g / 255.0, b / 255.0);
                            sat = Math.Clamp(
                                sat * (colorProcessingSettings.ColorSaturation / 100.0),
                                0,
                                1);
                            hue = ApplyHueShift(hue, colorProcessingSettings.HueShiftDegrees);
                            var (r2, g2, b2) = HslToRgb(hue, sat, lightness);
                            r = r2 * 255;
                            g = g2 * 255;
                            b = b2 * 255;
                        }

                        if (colorProcessingSettings.OutputBrightnessPercent != 100)
                        {
                            r = ApplyOutputBrightness(r, colorProcessingSettings.OutputBrightnessPercent);
                            g = ApplyOutputBrightness(g, colorProcessingSettings.OutputBrightnessPercent);
                            b = ApplyOutputBrightness(b, colorProcessingSettings.OutputBrightnessPercent);
                        }

                        // Format following HarmonizeProject: divide by 2 for 16-bit color compatibility.
                        byte r16 = (byte)(Math.Clamp(r, 0, 255) / ColorDivisor);
                        byte g16 = (byte)(Math.Clamp(g, 0, 255) / ColorDivisor);
                        byte b16 = (byte)(Math.Clamp(b, 0, 255) / ColorDivisor);

                        processedColors[kvp.Key] = new byte[] { r16, r16, g16, g16, b16, b16 };
                    }

                    var processedSent = await _hueStreamer!.SendColors(
                        areaId,
                        processedColors,
                        colorProcessingSettings.ColorChangeThreshold,
                        token);
                    if (!HandleDtlsSendResult(processedSent, token, ref consecutiveSendFailures))
                    {
                        streamFailed = true;
                        break;
                    }

                    var elapsedMs = loopTimer.ElapsedMilliseconds;
                    if (targetFrameDurationMs > 0)
                    {
                        var remaining = targetFrameDurationMs - (int)Math.Min(int.MaxValue, elapsedMs);
                        if (remaining > 0)
                        {
                            await Task.Delay(remaining, token);
                        }
                    }
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                streamFailed = true;
                _logger.LogError(ex, "Error in Sync Loop");
                if (!token.IsCancellationRequested)
                    SetRuntimeError("The video sync loop stopped unexpectedly.");
            }
            finally
            {
                try
                { videoStream.Dispose(); }
                catch { }

                if (!token.IsCancellationRequested && (streamEnded || streamFailed))
                {
                    ObserveTask(FinalizeSyncLoopAsync(token, expectedSyncCts, playSessionId, streamEnded));
                }
            }
        }

        /// <summary>
        /// Holds the mixed and source-channel energy calculated from one PCM window.
        /// The legacy mixed values remain the default runtime path; the optional left and
        /// right values allow stereo playback to preserve source panning without a native
        /// DSP dependency.
        /// </summary>
        internal readonly record struct AudioChannelAnalysis(
            double Rms,
            double Low,
            double Mid,
            double High,
            double LeftRms,
            double LeftLow,
            double LeftMid,
            double LeftHigh,
            double RightRms,
            double RightLow,
            double RightMid,
            double RightHigh)
        {
            public (double Rms, double Low, double Mid, double High) MixedEnergy =>
                (Rms, Low, Mid, High);

            public (double Rms, double Low, double Mid, double High) LeftEnergy =>
                (LeftRms, LeftLow, LeftMid, LeftHigh);

            public (double Rms, double Low, double Mid, double High) RightEnergy =>
                (RightRms, RightLow, RightMid, RightHigh);
        }

        /// <summary>
        /// Converts a raw PCM window into mixed and source-channel low, middle, and
        /// high-band energy. The small direct DFT keeps the visualizer deterministic and
        /// avoids pulling a native DSP package into the Jellyfin plugin.
        /// </summary>
        internal static AudioChannelAnalysis AnalyzeAudioChannelSamples(
            byte[] buffer,
            int bytesRead,
            int sampleRate = AudioSampleRate,
            int channels = AudioChannels,
            int lowFrequencyHz = PluginConfiguration.DefaultAudioLowFrequencyHz,
            int midFrequencyHz = PluginConfiguration.DefaultAudioMidFrequencyHz,
            int highFrequencyHz = PluginConfiguration.DefaultAudioHighFrequencyHz,
            int audioBandSpreadPercent = PluginConfiguration.DefaultAudioBandSpreadPercent)
        {
            if (buffer == null || bytesRead < AudioBytesPerSample || sampleRate <= 0 || channels <= 0)
                return new AudioChannelAnalysis();

            var frameBytes = AudioBytesPerSample * channels;
            var sampleCount = Math.Min(bytesRead, buffer.Length) / frameBytes;
            if (sampleCount <= 0)
                return new AudioChannelAnalysis();

            var channelSamples = new double[channels][];
            for (var channel = 0; channel < channels; channel++)
                channelSamples[channel] = new double[sampleCount];
            var mono = new double[sampleCount];
            double sumSquares = 0;
            for (var index = 0; index < sampleCount; index++)
            {
                var offset = index * frameBytes;
                var sum = 0.0;
                for (var channel = 0; channel < channels; channel++)
                {
                    var sampleOffset = offset + channel * AudioBytesPerSample;
                    var raw = (short)(buffer[sampleOffset] | (buffer[sampleOffset + 1] << 8));
                    var value = raw / (double)short.MaxValue;
                    channelSamples[channel][index] = value;
                    sum += value;
                }

                var mixed = Math.Clamp(sum / channels, -1.0, 1.0);
                mono[index] = mixed;
                sumSquares += mixed * mixed;
            }

            var maximumFrequency = Math.Max(2, sampleRate / 2 - 1);
            var normalizedLow = Math.Clamp(lowFrequencyHz, 1, maximumFrequency);
            var normalizedMid = Math.Clamp(midFrequencyHz, 1, maximumFrequency);
            var normalizedHigh = Math.Clamp(highFrequencyHz, 1, maximumFrequency);
            if (normalizedLow >= normalizedMid || normalizedMid >= normalizedHigh)
            {
                normalizedLow = Math.Clamp(PluginConfiguration.DefaultAudioLowFrequencyHz, 1, maximumFrequency);
                normalizedMid = Math.Clamp(PluginConfiguration.DefaultAudioMidFrequencyHz, 1, maximumFrequency);
                normalizedHigh = Math.Clamp(PluginConfiguration.DefaultAudioHighFrequencyHz, 1, maximumFrequency);
            }

            var left = channelSamples[0];
            var right = channels > 1 ? channelSamples[1] : left;
            return new AudioChannelAnalysis(
                Math.Sqrt(sumSquares / sampleCount),
                CalculateAudioBandEnergy(mono, sampleRate, normalizedLow, audioBandSpreadPercent),
                CalculateAudioBandEnergy(mono, sampleRate, normalizedMid, audioBandSpreadPercent),
                CalculateAudioBandEnergy(mono, sampleRate, normalizedHigh, audioBandSpreadPercent),
                Math.Sqrt(left.Sum(sample => sample * sample) / sampleCount),
                CalculateAudioBandEnergy(left, sampleRate, normalizedLow, audioBandSpreadPercent),
                CalculateAudioBandEnergy(left, sampleRate, normalizedMid, audioBandSpreadPercent),
                CalculateAudioBandEnergy(left, sampleRate, normalizedHigh, audioBandSpreadPercent),
                Math.Sqrt(right.Sum(sample => sample * sample) / sampleCount),
                CalculateAudioBandEnergy(right, sampleRate, normalizedLow, audioBandSpreadPercent),
                CalculateAudioBandEnergy(right, sampleRate, normalizedMid, audioBandSpreadPercent),
                CalculateAudioBandEnergy(right, sampleRate, normalizedHigh, audioBandSpreadPercent));
        }

        /// <summary>
        /// Suppresses a complete PCM analysis window when its mixed RMS level is below
        /// the configured normalized full-scale gate. A zero gate preserves the exact
        /// analyzer output and source-channel values.
        /// </summary>
        internal static AudioChannelAnalysis ApplyAudioNoiseGate(
            AudioChannelAnalysis analysis,
            int audioNoiseGatePercent = PluginConfiguration.DefaultAudioNoiseGatePercent)
        {
            var threshold = Math.Clamp(
                audioNoiseGatePercent,
                PluginConfiguration.MinAudioNoiseGatePercent,
                PluginConfiguration.MaxAudioNoiseGatePercent) / 100.0;
            return threshold <= 0 || analysis.Rms >= threshold
                ? analysis
                : new AudioChannelAnalysis();
        }

        /// <summary>
        /// Applies independent bounded low/mid/high multipliers to band energy while
        /// preserving RMS loudness for the shared sensitivity envelope. Neutral 100%
        /// gains preserve the exact analyzer output and source-channel routing.
        /// </summary>
        internal static AudioChannelAnalysis ApplyAudioBandGains(
            AudioChannelAnalysis analysis,
            int lowGainPercent = PluginConfiguration.DefaultAudioBandGainPercent,
            int midGainPercent = PluginConfiguration.DefaultAudioBandGainPercent,
            int highGainPercent = PluginConfiguration.DefaultAudioBandGainPercent)
        {
            var lowGain = Math.Clamp(
                lowGainPercent,
                PluginConfiguration.MinAudioBandGainPercent,
                PluginConfiguration.MaxAudioBandGainPercent) / 100.0;
            var midGain = Math.Clamp(
                midGainPercent,
                PluginConfiguration.MinAudioBandGainPercent,
                PluginConfiguration.MaxAudioBandGainPercent) / 100.0;
            var highGain = Math.Clamp(
                highGainPercent,
                PluginConfiguration.MinAudioBandGainPercent,
                PluginConfiguration.MaxAudioBandGainPercent) / 100.0;
            return analysis with
            {
                Low = analysis.Low * lowGain,
                Mid = analysis.Mid * midGain,
                High = analysis.High * highGain,
                LeftLow = analysis.LeftLow * lowGain,
                LeftMid = analysis.LeftMid * midGain,
                LeftHigh = analysis.LeftHigh * highGain,
                RightLow = analysis.RightLow * lowGain,
                RightMid = analysis.RightMid * midGain,
                RightHigh = analysis.RightHigh * highGain
            };
        }

        /// <summary>
        /// Converts a raw PCM window into mixed low, middle, and high-band energy. This
        /// compatibility wrapper preserves the original mono analyzer contract.
        /// </summary>
        internal static (double Rms, double Low, double Mid, double High) AnalyzeAudioSamples(
            byte[] buffer,
            int bytesRead,
            int sampleRate = AudioSampleRate,
            int channels = AudioChannels,
            int lowFrequencyHz = PluginConfiguration.DefaultAudioLowFrequencyHz,
            int midFrequencyHz = PluginConfiguration.DefaultAudioMidFrequencyHz,
            int highFrequencyHz = PluginConfiguration.DefaultAudioHighFrequencyHz,
            int audioBandSpreadPercent = PluginConfiguration.DefaultAudioBandSpreadPercent)
        {
            return AnalyzeAudioChannelSamples(
                buffer,
                bytesRead,
                sampleRate,
                channels,
                lowFrequencyHz,
                midFrequencyHz,
                highFrequencyHz,
                audioBandSpreadPercent).MixedEnergy;
        }

        private static double CalculateAudioBandEnergy(
            double[] samples,
            int sampleRate,
            double frequency,
            int audioBandSpreadPercent = PluginConfiguration.DefaultAudioBandSpreadPercent)
        {
            if (samples.Length == 0)
                return 0;

            var spreadPercent = Math.Clamp(
                audioBandSpreadPercent,
                PluginConfiguration.MinAudioBandSpreadPercent,
                PluginConfiguration.MaxAudioBandSpreadPercent);
            if (spreadPercent <= PluginConfiguration.MinAudioBandSpreadPercent)
                return CalculateAudioFrequencyEnergy(samples, sampleRate, frequency);

            var maximumFrequency = Math.Max(1, sampleRate / 2 - 1);
            var halfWidth = Math.Max(1.0, frequency * spreadPercent / 100.0);
            var lowerFrequency = Math.Clamp(frequency - halfWidth, 1, maximumFrequency);
            var upperFrequency = Math.Clamp(frequency + halfWidth, 1, maximumFrequency);
            const int frequencySamples = 5;
            var total = 0.0;
            for (var index = 0; index < frequencySamples; index++)
            {
                var sampleFrequency = lowerFrequency +
                    (upperFrequency - lowerFrequency) * index / (frequencySamples - 1);
                total += CalculateAudioFrequencyEnergy(samples, sampleRate, sampleFrequency);
            }

            return total / frequencySamples;
        }

        private static double CalculateAudioFrequencyEnergy(double[] samples, int sampleRate, double frequency)
        {
            if (samples.Length == 0)
                return 0;

            var real = 0.0;
            var imaginary = 0.0;
            var phaseStep = 2 * Math.PI * frequency / sampleRate;
            for (var index = 0; index < samples.Length; index++)
            {
                var phase = phaseStep * index;
                real += samples[index] * Math.Cos(phase);
                imaginary -= samples[index] * Math.Sin(phase);
            }

            return Math.Clamp(2 * Math.Sqrt(real * real + imaginary * imaginary) / samples.Length, 0, 1);
        }

        /// <summary>
        /// Converts a rising PCM envelope into a bounded transient pulse. The previous
        /// frame is required so steady loudness does not continually re-trigger the pulse;
        /// the default response of zero preserves the original audio color envelope.
        /// </summary>
        internal static double CalculateAudioBeatPulse(
            (double Rms, double Low, double Mid, double High)? previousEnergy,
            (double Rms, double Low, double Mid, double High) currentEnergy,
            int audioBeatPulsePercent = PluginConfiguration.DefaultAudioBeatPulsePercent,
            double previousPulse = 0,
            int audioBeatPulseDecayPercent = PluginConfiguration.DefaultAudioBeatPulseDecayPercent)
        {
            var response = Math.Clamp(
                audioBeatPulsePercent,
                PluginConfiguration.MinAudioBeatPulsePercent,
                PluginConfiguration.MaxAudioBeatPulsePercent) / 100.0;
            if (!previousEnergy.HasValue || response <= 0)
                return 0;

            var decayRetention = Math.Clamp(
                audioBeatPulseDecayPercent,
                PluginConfiguration.MinAudioBeatPulseDecayPercent,
                PluginConfiguration.MaxAudioBeatPulseDecayPercent) / 100.0 * 0.95;
            var retainedPulse = Math.Clamp(previousPulse, 0, 1) * decayRetention;

            var previous = previousEnergy.Value;
            var risingRms = Math.Max(0, currentEnergy.Rms - previous.Rms);
            var risingBands = (
                Math.Max(0, currentEnergy.Low - previous.Low) +
                Math.Max(0, currentEnergy.Mid - previous.Mid) +
                Math.Max(0, currentEnergy.High - previous.High)) / 3.0;
            var onset = Math.Max(risingRms, risingBands);
            var attack = Math.Clamp(onset * 4.0 * response, 0, 1);
            return Math.Max(attack, retainedPulse);
        }

        /// <summary>
        /// Maps the analyzed bands onto an entertainment area's channels. Spatial mode
        /// favors bass on the left, mids in the center, and treble on the right; Uniform
        /// sends the same mixed response to every channel; Mirror produces a symmetric
        /// center-to-edge pattern for rooms with matching left/right placement. Spatial
        /// source-channel selection can preserve stereo placement or intentionally use
        /// only the left or right source channel; non-Spatial routing remains symmetric.
        /// </summary>
        internal static Dictionary<int, byte[]> BuildAudioChannelColors(
            IReadOnlyDictionary<int, (double x, double z)> lights,
            (double Rms, double Low, double Mid, double High) energy,
            long frameIndex,
            int audioSensitivityPercent = PluginConfiguration.DefaultAudioSensitivityPercent,
            double audioBeatPulse = 0,
            string? audioColorPalette = null,
            string? audioSpatialMode = null,
            string? audioChannelMode = null,
            AudioChannelAnalysis? audioChannelAnalysis = null)
        {
            var colors = new Dictionary<int, byte[]>(lights.Count);
            var normalizedPalette = PluginConfiguration.TryNormalizeAudioColorPalette(
                    audioColorPalette,
                    out var palette)
                ? palette
                : PluginConfiguration.AudioColorPaletteSpectrum;
            var normalizedSpatialMode = PluginConfiguration.TryNormalizeAudioSpatialMode(
                    audioSpatialMode,
                    out var spatialMode)
                ? spatialMode
                : PluginConfiguration.AudioSpatialModeSpatial;
            var normalizedChannelMode = PluginConfiguration.TryNormalizeAudioChannelMode(
                    audioChannelMode,
                    out var channelMode)
                ? channelMode
                : PluginConfiguration.AudioChannelModeMono;

            foreach (var light in lights)
            {
                var physicalX = Math.Clamp((light.Value.x + 1) / 2.0, 0, 1);
                var physicalZ = Math.Clamp((light.Value.z + 1) / 2.0, 0, 1);
                var x = normalizedSpatialMode == PluginConfiguration.AudioSpatialModeUniform
                    ? 0.5
                    : normalizedSpatialMode == PluginConfiguration.AudioSpatialModeMirror
                        ? Math.Abs(physicalX * 2 - 1)
                        : physicalX;
                var z = normalizedSpatialMode == PluginConfiguration.AudioSpatialModeUniform ? 0.5 : physicalZ;
                var lightEnergy = energy;
                if (normalizedSpatialMode == PluginConfiguration.AudioSpatialModeSpatial &&
                    normalizedChannelMode != PluginConfiguration.AudioChannelModeMono &&
                    audioChannelAnalysis.HasValue)
                {
                    lightEnergy = normalizedChannelMode switch
                    {
                        PluginConfiguration.AudioChannelModeStereo => BlendAudioEnergy(
                            audioChannelAnalysis.Value.LeftEnergy,
                            audioChannelAnalysis.Value.RightEnergy,
                            physicalX),
                        PluginConfiguration.AudioChannelModeLeft => audioChannelAnalysis.Value.LeftEnergy,
                        PluginConfiguration.AudioChannelModeRight => audioChannelAnalysis.Value.RightEnergy,
                        _ => energy
                    };
                }

                var loudness = Math.Clamp(
                    lightEnergy.Rms * 2.8 * Math.Clamp(
                        audioSensitivityPercent,
                        PluginConfiguration.MinAudioSensitivityPercent,
                        PluginConfiguration.MaxAudioSensitivityPercent) / 100.0,
                    0,
                    1);
                loudness = Math.Clamp(loudness + Math.Clamp(audioBeatPulse, 0, 1) * 0.55, 0, 1);
                var totalBands = lightEnergy.Low + lightEnergy.Mid + lightEnergy.High;
                var dominantHue = totalBands <= 0.0001
                    ? 0.58
                    : (lightEnergy.Low * 0.02 + lightEnergy.Mid * 0.34 + lightEnergy.High * 0.66) / totalBands;
                var spatialPulse = 0.58 + 0.42 * (0.5 + 0.5 * Math.Sin(frameIndex * 0.37 + x * Math.PI * 2 + z));
                var value = Math.Clamp(0.025 + loudness * spatialPulse, 0, 1);
                if (normalizedPalette == PluginConfiguration.AudioColorPaletteBand)
                {
                    var low = Math.Clamp(lightEnergy.Low * 2.8, 0, 1) * (0.65 + 0.35 * (1 - x));
                    var mid = Math.Clamp(lightEnergy.Mid * 2.8, 0, 1) *
                        (0.55 + 0.45 * (1 - Math.Abs(x * 2 - 1)));
                    var high = Math.Clamp(lightEnergy.High * 2.8, 0, 1) * (0.65 + 0.35 * x);
                    var bandPeak = Math.Max(low, Math.Max(mid, high));
                    if (bandPeak <= 0.0001)
                    {
                        colors[light.Key] = HsvToRgb(dominantHue, 0.25, value);
                    }
                    else
                    {
                        var bandScale = value / bandPeak;
                        colors[light.Key] = new[]
                        {
                            (byte)Math.Round(Math.Clamp(low * bandScale, 0, 1) * 255, MidpointRounding.AwayFromZero),
                            (byte)Math.Round(Math.Clamp(mid * bandScale, 0, 1) * 255, MidpointRounding.AwayFromZero),
                            (byte)Math.Round(Math.Clamp(high * bandScale, 0, 1) * 255, MidpointRounding.AwayFromZero)
                        };
                    }
                    continue;
                }

                if (normalizedPalette == PluginConfiguration.AudioColorPaletteMonochrome)
                {
                    colors[light.Key] = HsvToRgb(0, 0, value);
                    continue;
                }

                var hue = normalizedPalette == PluginConfiguration.AudioColorPaletteWarm
                    ? 0.015 + dominantHue * 0.10 + (x - 0.5) * 0.02 + frameIndex * 0.0004
                    : normalizedPalette == PluginConfiguration.AudioColorPaletteCool
                        ? 0.52 + dominantHue * 0.14 + (x - 0.5) * 0.03 + frameIndex * 0.0005
                        : dominantHue + (x - 0.5) * 0.14 + (z - 0.5) * 0.04 + frameIndex * 0.0025;
                var saturation = normalizedPalette == PluginConfiguration.AudioColorPaletteWarm
                    ? Math.Clamp(0.78 + loudness * 0.20, 0, 1)
                    : normalizedPalette == PluginConfiguration.AudioColorPaletteCool
                        ? Math.Clamp(0.72 + loudness * 0.24, 0, 1)
                        : Math.Clamp(0.68 + loudness * 0.28, 0, 1);
                colors[light.Key] = HsvToRgb(hue, saturation, value);
            }

            return colors;
        }

        private static (double Rms, double Low, double Mid, double High) BlendAudioEnergy(
            (double Rms, double Low, double Mid, double High) left,
            (double Rms, double Low, double Mid, double High) right,
            double rightWeight)
        {
            var weight = Math.Clamp(rightWeight, 0, 1);
            var leftWeight = 1 - weight;
            return (
                left.Rms * leftWeight + right.Rms * weight,
                left.Low * leftWeight + right.Low * weight,
                left.Mid * leftWeight + right.Mid * weight,
                left.High * leftWeight + right.High * weight);
        }

        private static byte[] HsvToRgb(double hue, double saturation, double value)
        {
            hue = ((hue % 1) + 1) % 1;
            saturation = Math.Clamp(saturation, 0, 1);
            value = Math.Clamp(value, 0, 1);
            var scaled = hue * 6;
            var sector = (int)Math.Floor(scaled);
            var fraction = scaled - sector;
            var p = value * (1 - saturation);
            var q = value * (1 - saturation * fraction);
            var t = value * (1 - saturation * (1 - fraction));
            var rgb = (sector % 6) switch
            {
                0 => (value, t, p),
                1 => (q, value, p),
                2 => (p, value, t),
                3 => (p, q, value),
                4 => (t, p, value),
                _ => (value, p, q)
            };

            return new[]
            {
                (byte)Math.Round(rgb.Item1 * 255, MidpointRounding.AwayFromZero),
                (byte)Math.Round(rgb.Item2 * 255, MidpointRounding.AwayFromZero),
                (byte)Math.Round(rgb.Item3 * 255, MidpointRounding.AwayFromZero)
            };
        }

        private async Task RunAudioSyncLoop(
            Stream audioStream,
            Dictionary<int, (double x, double z)> lights,
            string areaId,
            int targetFrameDurationMs,
            int targetFps,
            CancellationTokenSource expectedSyncCts,
            string playSessionId,
            int audioSensitivityPercent,
            int audioNoiseGatePercent,
            (int LowFrequencyHz, int MidFrequencyHz, int HighFrequencyHz) audioFrequencies,
            (int LowGainPercent, int MidGainPercent, int HighGainPercent) audioBandGains,
            int audioBandSpreadPercent,
            int audioBeatPulsePercent,
            int audioBeatPulseDecayPercent,
            string audioColorPalette,
            string audioSpatialMode,
            string audioChannelMode,
            int colorSmoothingPercent,
            (
                int BrightnessBoost,
                int RedGain,
                int GreenGain,
                int BlueGain,
                int ColorSaturation,
                int HueShiftDegrees,
                int OutputBrightnessPercent,
                int BlackoutThreshold,
                int ColorChangeThreshold) colorProcessingSettings)
        {
            var samplesPerChunk = Math.Max(80, AudioSampleRate / Math.Clamp(targetFps, MinFps, MaxFps));
            var chunkSize = samplesPerChunk * AudioChannels * AudioBytesPerSample;
            var buffer = new byte[chunkSize];
            var token = expectedSyncCts.Token;
            var streamEnded = false;
            var streamFailed = false;
            var consecutiveSendFailures = 0;
            var previousChannelColors = new Dictionary<int, byte[]>();
            (double Rms, double Low, double Mid, double High)? previousEnergy = null;
            var previousBeatPulse = 0.0;
            long frameIndex = 0;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var loopTimer = Stopwatch.StartNew();
                    var sampleRead = await ReadFrameAsync(
                        audioStream,
                        buffer,
                        chunkSize,
                        token,
                        GetFrameReadTimeout()).ConfigureAwait(false);
                    if (sampleRead.TimedOut)
                    {
                        streamFailed = true;
                        SetRuntimeError("FFmpeg stopped producing audio samples within the configured stall timeout.");
                        _logger.LogError(
                            "FFmpeg audio stream stalled after {0} bytes; stopping Hue sync for session {1}",
                            sampleRead.BytesRead,
                            playSessionId);
                        break;
                    }

                    if (sampleRead.BytesRead < chunkSize)
                    {
                        if (!token.IsCancellationRequested)
                        {
                            streamEnded = true;
                            SetRuntimeStatus("Ended", "The audio stream ended.");
                        }
                        break;
                    }

                    _ffmpegStreamer?.MarkFrameRead();
                    var audioAnalysis = ApplyAudioBandGains(
                        ApplyAudioNoiseGate(
                            AnalyzeAudioChannelSamples(
                                buffer,
                                sampleRead.BytesRead,
                                AudioSampleRate,
                                AudioChannels,
                                audioFrequencies.LowFrequencyHz,
                                audioFrequencies.MidFrequencyHz,
                                audioFrequencies.HighFrequencyHz,
                                audioBandSpreadPercent),
                            audioNoiseGatePercent),
                        audioBandGains.LowGainPercent,
                        audioBandGains.MidGainPercent,
                        audioBandGains.HighGainPercent);
                    var energy = audioAnalysis.MixedEnergy;
                    var beatPulse = CalculateAudioBeatPulse(
                        previousEnergy,
                        energy,
                        audioBeatPulsePercent,
                        previousBeatPulse,
                        audioBeatPulseDecayPercent);
                    previousEnergy = energy;
                    previousBeatPulse = beatPulse;
                    var channelColors = BuildAudioChannelColors(
                        lights,
                        energy,
                        frameIndex++,
                        audioSensitivityPercent,
                        beatPulse,
                        audioColorPalette,
                        audioSpatialMode,
                        audioChannelMode,
                        !string.Equals(audioChannelMode, PluginConfiguration.AudioChannelModeMono, StringComparison.Ordinal)
                            ? audioAnalysis
                            : null);

                    var isBlackout = colorProcessingSettings.BlackoutThreshold > 0 &&
                        channelColors.Count > 0 &&
                        channelColors.Values.Average(c => (c[0] + c[1] + c[2]) / 3.0) < colorProcessingSettings.BlackoutThreshold;
                    if (isBlackout)
                    {
                        previousChannelColors.Clear();
                        var blackColors = channelColors.Keys.ToDictionary(
                            key => key,
                            _ => new byte[] { 0, 0, 0, 0, 0, 0 });
                        var blackoutSent = await _hueStreamer!.SendColors(
                            areaId,
                            blackColors,
                            colorProcessingSettings.ColorChangeThreshold,
                            token);
                        if (!HandleDtlsSendResult(blackoutSent, token, ref consecutiveSendFailures))
                        {
                            streamFailed = true;
                            break;
                        }

                        await DelayForLoopAsync(loopTimer, targetFrameDurationMs, token).ConfigureAwait(false);
                        continue;
                    }

                    if (colorSmoothingPercent > 0)
                    {
                        channelColors = ApplyTemporalSmoothing(
                            channelColors,
                            previousChannelColors,
                            colorSmoothingPercent);
                        previousChannelColors = channelColors;
                    }
                    else
                    {
                        previousChannelColors.Clear();
                    }

                    var processedColors = new Dictionary<int, byte[]>(channelColors.Count);
                    foreach (var color in channelColors)
                    {
                        double r = color.Value[0];
                        double g = color.Value[1];
                        double b = color.Value[2];

                        if (colorProcessingSettings.BrightnessBoost != 100)
                        {
                            var multiplier = colorProcessingSettings.BrightnessBoost / 100.0;
                            r = Math.Min(255, r * multiplier);
                            g = Math.Min(255, g * multiplier);
                            b = Math.Min(255, b * multiplier);
                        }

                        if (colorProcessingSettings.RedGain != 100 ||
                            colorProcessingSettings.GreenGain != 100 ||
                            colorProcessingSettings.BlueGain != 100)
                        {
                            var gained = ApplyColorChannelGains(
                                r,
                                g,
                                b,
                                colorProcessingSettings.RedGain,
                                colorProcessingSettings.GreenGain,
                                colorProcessingSettings.BlueGain);
                            r = gained.Red;
                            g = gained.Green;
                            b = gained.Blue;
                        }

                        if (colorProcessingSettings.ColorSaturation != 100 ||
                            colorProcessingSettings.HueShiftDegrees != 0)
                        {
                            var (hue, saturation, lightness) = RgbToHsl(r / 255.0, g / 255.0, b / 255.0);
                            saturation = Math.Clamp(
                                saturation * (colorProcessingSettings.ColorSaturation / 100.0),
                                0,
                                1);
                            hue = ApplyHueShift(hue, colorProcessingSettings.HueShiftDegrees);
                            var adjusted = HslToRgb(hue, saturation, lightness);
                            r = adjusted.r * 255;
                            g = adjusted.g * 255;
                            b = adjusted.b * 255;
                        }

                        r = ApplyOutputBrightness(r, colorProcessingSettings.OutputBrightnessPercent);
                        g = ApplyOutputBrightness(g, colorProcessingSettings.OutputBrightnessPercent);
                        b = ApplyOutputBrightness(b, colorProcessingSettings.OutputBrightnessPercent);

                        var r16 = (byte)(Math.Clamp(r, 0, 255) / ColorDivisor);
                        var g16 = (byte)(Math.Clamp(g, 0, 255) / ColorDivisor);
                        var b16 = (byte)(Math.Clamp(b, 0, 255) / ColorDivisor);
                        processedColors[color.Key] = new[] { r16, r16, g16, g16, b16, b16 };
                    }

                    var sent = await _hueStreamer!.SendColors(
                        areaId,
                        processedColors,
                        colorProcessingSettings.ColorChangeThreshold,
                        token);
                    if (!HandleDtlsSendResult(sent, token, ref consecutiveSendFailures))
                    {
                        streamFailed = true;
                        break;
                    }

                    await DelayForLoopAsync(loopTimer, targetFrameDurationMs, token).ConfigureAwait(false);
                }
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                streamFailed = true;
                _logger.LogError(ex, "Error in audio sync loop");
                if (!token.IsCancellationRequested)
                    SetRuntimeError("The audio sync loop stopped unexpectedly.");
            }
            finally
            {
                try
                { audioStream.Dispose(); }
                catch { }

                if (!token.IsCancellationRequested && (streamEnded || streamFailed))
                    ObserveTask(FinalizeSyncLoopAsync(token, expectedSyncCts, playSessionId, streamEnded));
            }
        }

        private static async Task DelayForLoopAsync(
            Stopwatch loopTimer,
            int targetFrameDurationMs,
            CancellationToken token)
        {
            if (targetFrameDurationMs <= 0)
                return;

            var remaining = targetFrameDurationMs - (int)Math.Min(int.MaxValue, loopTimer.ElapsedMilliseconds);
            if (remaining > 0)
                await Task.Delay(remaining, token).ConfigureAwait(false);
        }

        private async Task FinalizeSyncLoopAsync(
            CancellationToken token,
            CancellationTokenSource expectedSyncCts,
            string playSessionId,
            bool streamEnded)
        {
            if (token.IsCancellationRequested)
                return;

            await _syncLifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_syncLock)
                {
                    if (_isStopping ||
                        token.IsCancellationRequested ||
                        !ReferenceEquals(_syncCts, expectedSyncCts) ||
                        !string.Equals(_currentPlaySessionId, playSessionId, StringComparison.Ordinal))
                    {
                        return;
                    }
                }

                var config = Plugin.Instance?.Configuration;
                var bridgeConfig = _currentBridgeConfig;
                var savedLightStates = _savedLightStates;
                StopSync(deactivateArea: false, expectedPlaySessionId: playSessionId, clearSession: false);
                _currentBridgeConfig = null;
                _currentFrameResolution = null;
                _currentVideoScalingMode = null;
                _currentVideoDeinterlaceMode = null;
                _currentTargetFps = null;
                _currentAudioSensitivityPercent = null;
                _currentAudioNoiseGatePercent = null;
                _currentAudioFrequencies = null;
                _currentAudioBandGains = null;
                _currentAudioBandSpreadPercent = null;
                _currentAudioBeatPulsePercent = null;
                _currentAudioBeatPulseDecayPercent = null;
                _currentAudioColorPalette = null;
                _currentAudioSpatialMode = null;
                _currentAudioChannelMode = null;
                _currentSamplingBreadthPercent = null;
                _currentSamplingMode = null;
                _currentColorSmoothingPercent = null;

                try
                {
                    await RestoreAndDeactivateAsync(
                        config,
                        bridgeConfig,
                        savedLightStates,
                        sessionOutcome: streamEnded ? "Ended" : "Error").ConfigureAwait(false);
                    if (streamEnded)
                    {
                        SetRuntimeStatus(
                            "Idle",
                            GetRuntimeStatus().CleanupWarning == null
                                ? "Video stream ended; lights were restored."
                                : "Video stream ended; cleanup completed with warnings.");
                    }
                }
                finally
                {
                    lock (_syncLock)
                    {
                        if (string.Equals(_currentPlaySessionId, playSessionId, StringComparison.Ordinal))
                        {
                            _currentPlaySessionId = null;
                            _recoveredSessionId = null;
                        }
                    }
                }
            }
            finally
            {
                _syncLifecycleLock.Release();
            }
        }

        private async Task RestartSyncAfterSeekAsync(PlaybackProgressEventArgs e)
        {
            try
            {
                lock (_syncLock)
                {
                    if (_isStopping ||
                        !string.Equals(_currentPlaySessionId, e.PlaySessionId, StringComparison.Ordinal) ||
                        _lastPlaybackProgressWasPaused)
                    {
                        return;
                    }
                }

                // Reuse the current Hue target and saved light-state snapshot. The restart
                // stops only the active stream/capture pipeline; it does not run playback
                // cleanup, so the lights do not flash back to their pre-playback state.
                await StartSyncForItemWithOptions(e, preserveSessionMetadata: true).ConfigureAwait(false);
            }
            finally
            {
                lock (_syncLock)
                {
                    _seekRestartInFlight = false;
                }
            }
        }

        private async Task StartSyncForItem(PlaybackProgressEventArgs e)
            => await StartSyncForItemWithOptions(e, preserveSessionMetadata: false).ConfigureAwait(false);

        private async Task StartSyncForItemWithOptions(
            PlaybackProgressEventArgs e,
            bool preserveSessionMetadata)
        {
            lock (_syncLock)
            {
                if (_isStopping)
                    return;

                if (string.Equals(_startingPlaySessionId, e.PlaySessionId, StringComparison.Ordinal))
                {
                    _logger.LogDebug("Sync start already in progress for this session, skipping");
                    return;
                }

                _startingPlaySessionId = e.PlaySessionId;
                _syncStartsInFlight++;
            }

            try
            {
                await StartSyncForItemInternal(e, preserveSessionMetadata);
            }
            finally
            {
                lock (_syncLock)
                {
                    if (string.Equals(_startingPlaySessionId, e.PlaySessionId, StringComparison.Ordinal))
                        _startingPlaySessionId = null;
                    _syncStartsInFlight--;
                    if (string.Equals(_currentPlaySessionId, e.PlaySessionId, StringComparison.Ordinal) &&
                        (_syncCts == null || _syncCts.IsCancellationRequested))
                    {
                        _currentPlaySessionId = null;
                        _recoveredSessionId = null;
                    }
                }
            }
        }

        private async Task StartSyncForItemInternal(
            PlaybackProgressEventArgs e,
            bool preserveSessionMetadata)
        {
            if (!IsSupportedPlaybackItem(e.Item))
            {
                _logger.LogDebug("Skipping unsupported playback item {0}; Hue Sync supports video and audio playback", e.Item?.Name ?? "Unknown");
                SetRuntimeStatus("Idle", "Hue Sync supports video or audio playback, depending on the selected media scope.");
                return;
            }

            var startupCts = new CancellationTokenSource();
            lock (_syncLock)
            {
                if (_isStopping)
                {
                    startupCts.Cancel();
                }
                else
                {
                    _startupCts?.Cancel();
                    _startupCts = startupCts;
                }
            }

            if (startupCts.IsCancellationRequested)
            {
                lock (_syncLock)
                {
                    if (ReferenceEquals(_startupCts, startupCts))
                        _startupCts = null;
                }

                startupCts.Dispose();
                return;
            }

            try
            {
                await _syncLifecycleLock.WaitAsync().ConfigureAwait(false);
                lock (_syncLock)
                {
                    if (_isStopping)
                        return;
                }

                if (startupCts.IsCancellationRequested)
                    return;

                await StartSyncForItemCoreWithOptions(e, startupCts.Token, preserveSessionMetadata).ConfigureAwait(false);
            }
            finally
            {
                lock (_syncLock)
                {
                    if (ReferenceEquals(_startupCts, startupCts))
                        _startupCts = null;
                }

                startupCts.Dispose();
                _syncLifecycleLock.Release();
            }
        }

        // Keep the original two-argument helper for lifecycle tests and older internal
        // callers; seek restarts use the options-aware implementation below.
        private Task StartSyncForItemCore(PlaybackProgressEventArgs e, CancellationToken startupToken)
            => StartSyncForItemCoreWithOptions(e, startupToken, preserveSessionMetadata: false);

        private async Task StartSyncForItemCoreWithOptions(
            PlaybackProgressEventArgs e,
            CancellationToken startupToken,
            bool preserveSessionMetadata)
        {
            lock (_syncLock)
            {
                if (_isStopping || startupToken.IsCancellationRequested)
                    return;
            }

            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.SyncEnabled)
            {
                _logger.LogInformation("Hue Sync disabled, skipping.");
                SetRuntimeStatus("Idle", "Sync is disabled.");
                return;
            }

            var userId = e.Session?.UserId ?? Guid.Empty;
            var userName = e.Session?.UserName?.Trim();
            if (!config.IsSyncEnabledForUser(userId))
            {
                _logger.LogInformation("Hue Sync is disabled for user {0}, skipping.", userId);
                SetRuntimeStatus("Idle", "Sync is disabled for this user.");
                return;
            }

            if (_hueStreamer == null || _ffmpegStreamer == null)
            {
                _logger.LogWarning("Hue sync helpers are not initialized yet");
                SetRuntimeError("Sync service is still initializing.");
                return;
            }

            var validationErrors = config.Validate();
            if (validationErrors.Count > 0)
            {
                _logger.LogWarning("Configuration validation failed: {0}", string.Join(", ", validationErrors));
                SetRuntimeError("Configuration validation failed. Review the plugin settings.");
                return;
            }

            var isAudioPlayback = IsAudioPlaybackItem(e.Item);
            var playbackMediaFilter = GetPlaybackMediaFilter(e);
            if (!MatchesPlaybackMediaFilter(e.Item, playbackMediaFilter))
            {
                _logger.LogDebug(
                    "Skipping playback item {0}; configured Hue Sync media scope is {1}",
                    e.Item?.Name ?? "Unknown",
                    playbackMediaFilter);
                SetRuntimeStatus("Idle", $"Hue Sync playback media scope excludes this item ({playbackMediaFilter}).");
                return;
            }

            var (useCinemaMode, brightnessDimLevel, restoreLightState) = ResolvePlaybackSettings(config, userId);
            var pauseBehavior = ResolvePauseBehavior(config, userId);
            var performanceSettings = ResolvePerformanceSettings(config, userId);
            var audioSensitivityPercent = ResolveAudioSensitivityPercent(config, userId);
            var audioNoiseGatePercent = ResolveAudioNoiseGatePercent(config, userId);
            var audioFrequencies = ResolveAudioFrequencies(config, userId);
            var audioBandGains = ResolveAudioBandGains(config, userId);
            var audioBandSpreadPercent = ResolveAudioBandSpreadPercent(config, userId);
            var audioBeatPulsePercent = ResolveAudioBeatPulsePercent(config, userId);
            var audioBeatPulseDecayPercent = ResolveAudioBeatPulseDecayPercent(config, userId);
            var audioColorPalette = ResolveAudioColorPalette(config, userId);
            var audioSpatialMode = ResolveAudioSpatialMode(config, userId);
            var audioChannelMode = ResolveAudioChannelMode(config, userId);
            var colorProcessingSettings = ResolveColorProcessingSettings(config, userId);
            var executionSettings = ResolveExecutionSettings(config, userId);
            var selectedChannelIds = ResolveChannelIds(config, userId);

            var videoPath = e.Item?.Path;
            if (string.IsNullOrWhiteSpace(videoPath))
            {
                _logger.LogWarning("Unable to determine media path for playback item {0}", e.Item?.Name ?? "Unknown");
                SetRuntimeError("The playback item has no readable media path.");
                return;
            }

            var targetFps = performanceSettings.TargetFps;
            var frameResolution = performanceSettings.FrameResolution;
            var videoScalingMode = performanceSettings.VideoScalingMode;
            var videoDeinterlaceMode = performanceSettings.VideoDeinterlaceMode;

            // Get user-specific bridge configuration
            var (bridgeIp, appKey, clientKey, areaId) = config.GetBridgeConfigForUser(userId);

            if (string.IsNullOrWhiteSpace(bridgeIp) || string.IsNullOrWhiteSpace(appKey) ||
                string.IsNullOrWhiteSpace(clientKey) || string.IsNullOrWhiteSpace(areaId))
            {
                _logger.LogWarning("No valid bridge configuration found for user {0}", userId);
                SetRuntimeError("No valid bridge configuration is available for this user.");
                return;
            }

            _logger.LogInformation("Starting sync for user {0} with bridge {1} and area {2}", userId, bridgeIp, areaId);

            _hueClient.RetryAttempts = executionSettings.NetworkRetryAttempts;
            _hueStreamer.MaxReconnectAttempts = executionSettings.NetworkRetryAttempts;
            if (startupToken.IsCancellationRequested)
                return;

            IDisposable? playbackLifecycleLease = null;
            var reusingPlaybackLease = false;
            if (preserveSessionMetadata)
            {
                lock (_syncLock)
                {
                    reusingPlaybackLease = string.Equals(_currentPlaySessionId, e.PlaySessionId, StringComparison.Ordinal) &&
                        _playbackLifecycleLease != null;
                }
            }

            if (!reusingPlaybackLease)
                playbackLifecycleLease = _bridgeLifecycleGate.TryEnterPlayback(
                    GetPlaybackResourceKey(bridgeIp, areaId));

            if (!reusingPlaybackLease && playbackLifecycleLease == null)
            {
                _logger.LogWarning("Hue playback startup was blocked because a diagnostic is using the bridge");
                SetRuntimeError("Hue playback could not start while a Hue diagnostic (connection probe or preview) is using the bridge.");
                return;
            }

            DateTime? preservedSyncStartTime = null;
            if (preserveSessionMetadata)
            {
                lock (_syncLock)
                {
                    if (string.Equals(_currentPlaySessionId, e.PlaySessionId, StringComparison.Ordinal) &&
                        _syncStartTime != default)
                    {
                        preservedSyncStartTime = _syncStartTime;
                    }
                }
            }

            StopSync();
            var syncCts = CancellationTokenSource.CreateLinkedTokenSource(startupToken);
            var syncStatePublished = false;
            var syncLoopStarted = false;
            Stream? videoStream = null;
            lock (_syncLock)
            {
                if (_isStopping || startupToken.IsCancellationRequested)
                {
                    syncCts.Cancel();
                }
                else
                {
                    if (!reusingPlaybackLease)
                    {
                        _playbackLifecycleLease = playbackLifecycleLease;
                        playbackLifecycleLease = null;
                    }
                    _syncCts = syncCts;
                    _currentPlaySessionId = e.PlaySessionId;
                    _currentUserId = userId == Guid.Empty ? null : userId;
                    _currentUserName = string.IsNullOrWhiteSpace(userName) ? null : userName;
                    _currentBridgeConfig = (bridgeIp, appKey, clientKey, areaId);
                    _bridgeAreaDeactivated = false;
                    _syncStartTime = preservedSyncStartTime ?? DateTime.UtcNow;
                    _currentItemName = e.Item?.Name;
                    _currentFrameResolution = isAudioPlayback ? null : frameResolution;
                    _currentVideoScalingMode = isAudioPlayback ? null : videoScalingMode;
                    _currentVideoDeinterlaceMode = isAudioPlayback ? null : videoDeinterlaceMode;
                    _currentTargetFps = targetFps;
                    _currentAudioSensitivityPercent = isAudioPlayback ? audioSensitivityPercent : null;
                    _currentAudioNoiseGatePercent = isAudioPlayback ? audioNoiseGatePercent : null;
                    _currentAudioFrequencies = isAudioPlayback ? audioFrequencies : null;
                    _currentAudioBandGains = isAudioPlayback ? audioBandGains : null;
                    _currentAudioBandSpreadPercent = isAudioPlayback ? audioBandSpreadPercent : null;
                    _currentAudioBeatPulsePercent = isAudioPlayback ? audioBeatPulsePercent : null;
                    _currentAudioBeatPulseDecayPercent = isAudioPlayback ? audioBeatPulseDecayPercent : null;
                    _currentAudioColorPalette = isAudioPlayback ? audioColorPalette : null;
                    _currentAudioSpatialMode = isAudioPlayback ? audioSpatialMode : null;
                    _currentAudioChannelMode = isAudioPlayback ? audioChannelMode : null;
                    _currentSamplingBreadthPercent = performanceSettings.SamplingBreadthPercent;
                    _currentSamplingMode = performanceSettings.SamplingMode;
                    _currentColorSmoothingPercent = performanceSettings.ColorSmoothingPercent;
                    _activeColorProcessingSettings = colorProcessingSettings;
                    _activeExecutionSettings = executionSettings;
                    _activeChannelIds = selectedChannelIds;
                    _activeUseCinemaMode = useCinemaMode;
                    _activeCinemaModeAttempted = false;
                    _activeRestoreLightState = restoreLightState;
                    _activePauseBehavior = pauseBehavior;
                    syncStatePublished = true;
                }
            }

            playbackLifecycleLease?.Dispose();

            var token = syncCts.Token;

            try
            {
                if (syncStatePublished)
                    SetRuntimeStatus("Starting", $"Preparing '{e.Item?.Name ?? "playback"}'...", clearError: true);

                if (token.IsCancellationRequested)
                    return;

                var areaConfig = await _hueClient.GetEntertainmentConfiguration(
                    bridgeIp,
                    appKey,
                    areaId,
                    token);
                if (token.IsCancellationRequested)
                    return;
                if (areaConfig == null)
                {
                    _logger.LogWarning("Failed to load entertainment configuration from bridge");
                    SetRuntimeError("Could not load the selected entertainment area configuration.");
                    return;
                }

                var lights = new Dictionary<int, (double x, double z)>();
                if (areaConfig.Value.TryGetProperty("channels", out var channels) &&
                    channels.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var channel in channels.EnumerateArray())
                    {
                        if (!channel.TryGetProperty("channel_id", out var channelIdProperty) ||
                            !channelIdProperty.TryGetInt32(out var channelId) ||
                            channelId < 0 ||
                            channelId > ushort.MaxValue ||
                            !channel.TryGetProperty("position", out var position) ||
                            !position.TryGetProperty("x", out var xProperty) ||
                            !position.TryGetProperty("z", out var zProperty) ||
                            !xProperty.TryGetDouble(out var x) ||
                            !zProperty.TryGetDouble(out var z))
                        {
                            continue;
                        }

                        lights[channelId] = (x, z);
                    }
                }

                if (selectedChannelIds != null)
                {
                    var missingChannelIds = selectedChannelIds
                        .Where(channelId => !lights.ContainsKey(channelId))
                        .OrderBy(channelId => channelId)
                        .ToArray();
                    if (missingChannelIds.Length > 0)
                    {
                        _logger.LogWarning(
                            "Configured channel IDs {0} were not found in entertainment area {1}",
                            string.Join(", ", missingChannelIds),
                            areaId);
                        SetRuntimeError("One or more configured channel IDs are not present in the selected entertainment area.");
                        return;
                    }

                    lights = lights
                        .Where(pair => selectedChannelIds.Contains(pair.Key))
                        .ToDictionary(pair => pair.Key, pair => pair.Value);
                }

                if (lights.Count == 0)
                {
                    _logger.LogWarning("Entertainment area {0} returned no channels to control", areaId);
                    SetRuntimeError("The selected entertainment area has no controllable channels.");
                    return;
                }

                if (token.IsCancellationRequested)
                    return;

                // Save current light states if configured. A channel profile limits the
                // capture to the same subset that playback will control.
                var shouldCaptureLightState = ShouldCaptureLightState(
                    restoreLightState,
                    _savedLightStates != null,
                    _savedLightStatePlaySessionId,
                    e.PlaySessionId);
                if (shouldCaptureLightState)
                {
                    _logger.LogInformation("Saving current light states for restoration");
                    var captureResult = await _hueClient.GetLightStatesWithResult(
                        bridgeIp,
                        appKey,
                        areaConfig.Value,
                        selectedChannelIds,
                        token);
                    if (token.IsCancellationRequested)
                        return;

                    if (!captureResult.Succeeded || captureResult.AttemptedCount == 0)
                    {
                        _logger.LogError(
                            "Could not capture a complete light-state snapshot: captured {0} of {1}, failed {2}",
                            captureResult.CapturedCount,
                            captureResult.AttemptedCount,
                            captureResult.FailedCount);
                        SetRuntimeError(
                            captureResult.AttemptedCount == 0
                                ? "Could not capture any light states for safe restoration."
                                : $"Could not capture all light states for safe restoration ({captureResult.CapturedCount} of {captureResult.AttemptedCount} captured; {captureResult.FailedCount} failed).");
                        return;
                    }

                    _savedLightStates = captureResult.States;
                    _savedLightStatePlaySessionId = e.PlaySessionId;
                }

                if (useCinemaMode)
                {
                    lock (_syncLock)
                    {
                        // Treat the attempt as a mutation even if the temporary stream
                        // reports failure: a packet may have reached the bridge before
                        // the failure was observed, so cleanup must still restore it.
                        _activeCinemaModeAttempted = true;
                    }
                    if (!preserveSessionMetadata)
                    {
                        _logger.LogInformation("Cinema mode enabled, dimming lights to {0}%", brightnessDimLevel);
                        await ApplyCinemaMode(
                            bridgeIp,
                            appKey,
                            clientKey,
                            areaId,
                            areaConfig.Value,
                            brightnessDimLevel,
                            selectedChannelIds,
                            startupToken);
                    }
                    else
                    {
                        _logger.LogInformation("Reusing the active cinema-mode light policy after playback seek");
                    }
                    if (token.IsCancellationRequested)
                        return;
                }

                // CRITICAL: Activate the entertainment area on the bridge BEFORE opening the DTLS tunnel.
                // The bridge silently drops all DTLS packets if the area is not in streaming mode.
                _logger.LogInformation("Activating entertainment area {0} for streaming", areaId);
                SetRuntimeStatus("Starting", "Activating the entertainment area...");
                var activated = await _hueClient.StartEntertainmentArea(
                    bridgeIp,
                    appKey,
                    areaId,
                    token);
                if (token.IsCancellationRequested)
                    return;
                if (!activated)
                {
                    _logger.LogError("Could not activate entertainment area {0} — aborting sync", areaId);
                    SetRuntimeError("The Hue bridge could not activate the entertainment area.");
                    return;
                }

                // Small delay to let the bridge switch to streaming mode before the DTLS tunnel
                await Task.Delay(EntertainmentAreaActivationDelayMs);
                if (token.IsCancellationRequested)
                    return;

                // Set reconnect callback so DTLS reconnections re-activate the area first
                _hueStreamer!.OnBeforeReconnectWithCancellation = reconnectToken =>
                    _hueClient.StartEntertainmentArea(bridgeIp, appKey, areaId, reconnectToken);
                SetRuntimeStatus("Starting", "Opening the DTLS light stream...");
                await _hueStreamer.StartStreamAsync(
                    bridgeIp,
                    appKey,
                    clientKey,
                    token).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                    return;
                if (!_hueStreamer.IsHealthy())
                {
                    _logger.LogError("Could not establish DTLS stream for entertainment area {0}", areaId);
                    SetRuntimeError("The Hue bridge DTLS stream could not be established.");
                    return;
                }

                var targetFrameDurationMs = targetFps > 0
                    ? 1000 / Math.Clamp(targetFps, MinFps, MaxFps)
                    : DefaultFrameDurationMs;
                var (frameWidth, frameHeight) = PluginConfiguration.GetFrameDimensions(frameResolution);

                // Seek to current playback position so lights sync to the current media position.
                double seekSeconds = 0;
                if (e.PlaybackPositionTicks.HasValue && e.PlaybackPositionTicks.Value > 0)
                    seekSeconds = TimeSpan.FromTicks(e.PlaybackPositionTicks.Value).TotalSeconds;

                if (token.IsCancellationRequested)
                    return;
                _ffmpegStreamer!.StallTimeoutSeconds = executionSettings.FfmpegStallTimeoutSeconds;
                videoStream = isAudioPlayback
                    ? _ffmpegStreamer!.StartAudioFfmpeg(
                        videoPath,
                        executionSettings.UseGpu,
                        executionSettings.CustomFfmpegFlags,
                        _mediaEncoder.EncoderPath,
                        seekPositionSeconds: seekSeconds,
                        sampleRate: AudioSampleRate,
                        channels: AudioChannels)
                    : _ffmpegStreamer!.StartFfmpeg(
                        videoPath,
                        targetFps,
                        executionSettings.UseGpu,
                        executionSettings.CustomFfmpegFlags,
                        _mediaEncoder.EncoderPath,
                        seekPositionSeconds: seekSeconds,
                        frameWidth: frameWidth,
                        frameHeight: frameHeight,
                        scalingMode: videoScalingMode,
                        deinterlaceMode: videoDeinterlaceMode);
                if (videoStream == null)
                {
                    _logger.LogWarning(
                        "FFmpeg {0} stream could not be started for path {1}",
                        isAudioPlayback ? "audio" : "video",
                        videoPath);
                    SetRuntimeError(isAudioPlayback
                        ? "FFmpeg could not start the audio capture stream."
                        : "FFmpeg could not start the video capture stream.");
                    return;
                }

                // Capture sampling settings with the playback session so an administrator
                // changing configuration mid-playback does not alter an in-flight loop.
                var samplingBreadthPercent = performanceSettings.SamplingBreadthPercent;
                var samplingMode = performanceSettings.SamplingMode;

                // Let the selected loop own disposal even when cancellation wins before scheduling.
                SetRuntimeStatus(
                    "Syncing",
                    isAudioPlayback
                        ? "Streaming audio-reactive colors to Hue."
                        : "Streaming video colors to Hue.");
                if (isAudioPlayback)
                {
                    _ = Task.Run(() => RunAudioSyncLoop(
                        videoStream!,
                        lights,
                        areaId,
                        targetFrameDurationMs,
                        targetFps,
                        syncCts,
                        e.PlaySessionId,
                        audioSensitivityPercent,
                        audioNoiseGatePercent,
                        audioFrequencies,
                        audioBandGains,
                        audioBandSpreadPercent,
                        audioBeatPulsePercent,
                        audioBeatPulseDecayPercent,
                        audioColorPalette,
                        audioSpatialMode,
                        audioChannelMode,
                        performanceSettings.ColorSmoothingPercent,
                        colorProcessingSettings));
                }
                else
                {
                    _ = Task.Run(() => RunSyncLoopWithSampling(
                        videoStream!,
                        lights,
                        areaId,
                        targetFrameDurationMs,
                        syncCts,
                        e.PlaySessionId,
                        samplingBreadthPercent,
                        samplingMode,
                        frameResolution,
                        performanceSettings.ColorSmoothingPercent,
                        colorProcessingSettings));
                }
                syncLoopStarted = true;
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested)
                    return;
                _logger.LogError(ex, "Error starting Hue sync session");
                SetRuntimeError("Hue sync could not start. Check the bridge and FFmpeg diagnostics.");
            }
            finally
            {
                try
                {
                    // Before RunSyncLoop starts, this method still owns every partially
                    // initialized resource. Roll back immediately on cancellation or any
                    // startup failure instead of waiting for PlaybackStopped.
                    if (syncStatePublished && (token.IsCancellationRequested || !syncLoopStarted))
                        await CleanupAbortedStartup(e.PlaySessionId, syncCts).ConfigureAwait(false);
                }
                finally
                {
                    if (!syncStatePublished && reusingPlaybackLease)
                        ReleasePlaybackLifecycleLease();

                    if (!syncLoopStarted)
                    {
                        try
                        {
                            videoStream?.Dispose();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Error disposing an unowned FFmpeg stream during startup rollback");
                        }
                    }

                    if (!syncStatePublished)
                        syncCts.Dispose();
                }
            }
        }

        private async Task RestoreAndDeactivateAsync(
            PluginConfiguration? config,
            (string BridgeIp, string AppKey, string ClientKey, string AreaId)? bridgeConfig,
            List<HueClient.LightState>? savedLightStates,
            bool publishIdleStatus = true,
            bool clearCurrentItem = true,
            string sessionOutcome = "Stopped",
            bool recordSessionSummary = true)
        {
            var sessionSummarySeed = recordSessionSummary
                ? CaptureSessionSummarySeed(bridgeConfig, sessionOutcome)
                : null;
            bool effectiveUseCinemaMode;
            bool cinemaModeAttempted;
            bool effectiveRestoreLightState;
            IReadOnlySet<int>? activeChannelIds;
            lock (_syncLock)
            {
                effectiveUseCinemaMode = _activeUseCinemaMode ?? config?.UseCinemaMode ?? false;
                cinemaModeAttempted = _activeCinemaModeAttempted ?? effectiveUseCinemaMode;
                effectiveRestoreLightState = _activeRestoreLightState ?? config?.RestoreLightState ?? true;
                activeChannelIds = _activeChannelIds;
            }

            string? cleanupWarning = null;
            try
            {
                if (effectiveRestoreLightState && savedLightStates != null && bridgeConfig != null)
                {
                    _logger.LogInformation("Restoring saved light states");
                    var restoreResult = await _hueClient.RestoreLightStatesWithResult(
                        bridgeConfig.Value.BridgeIp,
                        bridgeConfig.Value.AppKey,
                        savedLightStates).ConfigureAwait(false);
                    if (!restoreResult.Succeeded)
                    {
                        cleanupWarning = $"Light restoration was incomplete: restored {restoreResult.RestoredCount} of {restoreResult.AttemptedCount} light(s); {restoreResult.FailedCount} failed. Some lights may need manual recovery.";
                    }

                    if (ReferenceEquals(_savedLightStates, savedLightStates))
                    {
                        _savedLightStates = null;
                        _savedLightStatePlaySessionId = null;
                    }
                }
                else if (effectiveUseCinemaMode && cinemaModeAttempted && bridgeConfig != null)
                {
                    _logger.LogInformation("Restoring lights after playback");
                    if (!await RestoreLightsAfterPlayback(
                            bridgeConfig.Value.BridgeIp,
                            bridgeConfig.Value.AppKey,
                            bridgeConfig.Value.ClientKey,
                            bridgeConfig.Value.AreaId,
                            activeChannelIds).ConfigureAwait(false))
                    {
                        cleanupWarning = "Cinema-mode light restoration did not complete. Some lights may need manual recovery.";
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during playback light restoration");
                cleanupWarning = "Light restoration failed. Some lights may need manual recovery.";
            }
            finally
            {
                if (clearCurrentItem)
                    CurrentItemName = null;
                lock (_syncLock)
                {
                    _currentUserId = null;
                    _currentUserName = null;
                }
                if (publishIdleStatus)
                {
                    lock (_syncLock)
                    {
                        if (!string.Equals(_runtimeState, "Error", StringComparison.Ordinal))
                        {
                            _runtimeState = "Idle";
                            _runtimeMessage = "Playback stopped.";
                        }
                    }
                }
                lock (_syncLock)
                {
                    _activeUseCinemaMode = null;
                    _activeCinemaModeAttempted = null;
                    _activeRestoreLightState = null;
                    _activePauseBehavior = null;
                    _activeColorProcessingSettings = null;
                    _activeExecutionSettings = null;
                    _activeChannelIds = null;
                }
                if (bridgeConfig != null)
                {
                    try
                    {
                        var deactivated = await _hueClient.StopEntertainmentAreaWithResult(
                            bridgeConfig.Value.BridgeIp,
                            bridgeConfig.Value.AppKey,
                            bridgeConfig.Value.AreaId).ConfigureAwait(false);
                        _bridgeAreaDeactivated = deactivated;
                        if (!deactivated)
                        {
                            cleanupWarning = cleanupWarning == null
                                ? "The entertainment area could not be deactivated during cleanup."
                                : $"{cleanupWarning} The entertainment area could not be deactivated during cleanup.";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error deactivating entertainment area during playback cleanup");
                        cleanupWarning = cleanupWarning == null
                            ? "The entertainment area could not be deactivated during cleanup."
                            : $"{cleanupWarning} The entertainment area could not be deactivated during cleanup.";
                    }
                }

                SetCleanupWarning(cleanupWarning);
                if (sessionSummarySeed != null)
                {
                    RecordSessionSummary(sessionSummarySeed, cleanupWarning);
                }
                ReleasePlaybackLifecycleLease();
            }
        }

        private SessionSummarySeed? CaptureSessionSummarySeed(
            (string BridgeIp, string AppKey, string ClientKey, string AreaId)? bridgeConfig,
            string outcome)
        {
            string? itemName;
            Guid? userId;
            string? userName;
            DateTime startedAtUtc;
            int seekRestartCount;
            double? lastSeekPositionSeconds;
            string? lastError;
            lock (_syncLock)
            {
                if (_currentPlaySessionId == null &&
                    _startingPlaySessionId == null &&
                    string.IsNullOrWhiteSpace(_currentItemName))
                {
                    return null;
                }

                itemName = _currentItemName;
                userId = _currentUserId;
                userName = _currentUserName;
                startedAtUtc = _syncStartTime;
                seekRestartCount = _seekRestartCount;
                lastSeekPositionSeconds = _lastSeekPositionSeconds;
                lastError = _lastError;
            }

            var endedAtUtc = DateTime.UtcNow;
            var durationSeconds = startedAtUtc == default
                ? (double?)null
                : Math.Max(0, (endedAtUtc - startedAtUtc).TotalSeconds);
            var framesProcessed = _ffmpegStreamer?.FramesProcessed ?? 0;
            var hueStreamer = _hueStreamer;
            var effectiveFps = durationSeconds > 0 && framesProcessed > 0
                ? framesProcessed / durationSeconds.Value
                : (double?)null;

            return new SessionSummarySeed
            {
                Outcome = string.IsNullOrWhiteSpace(outcome) ? "Stopped" : outcome,
                Item = itemName,
                UserId = userId == Guid.Empty ? null : userId?.ToString(),
                UserName = string.IsNullOrWhiteSpace(userName) ? null : userName,
                BridgeIp = bridgeConfig?.BridgeIp,
                EntertainmentAreaId = bridgeConfig?.AreaId,
                StartedAtUtc = startedAtUtc == default ? null : startedAtUtc,
                EndedAtUtc = endedAtUtc,
                DurationSeconds = durationSeconds,
                EffectiveFps = effectiveFps,
                FramesProcessed = framesProcessed,
                PacketsSent = hueStreamer?.PacketsSent ?? 0,
                PacketsSkippedByThreshold = hueStreamer?.PacketsSkippedByThreshold ?? 0,
                PacketSendFailures = hueStreamer?.PacketSendFailures ?? 0,
                ReconnectAttempts = hueStreamer?.ReconnectAttempts ?? 0,
                SeekRestartCount = seekRestartCount,
                LastSeekPositionSeconds = lastSeekPositionSeconds,
                Error = lastError
            };
        }

        private void RecordSessionSummary(SessionSummarySeed seed, string? cleanupWarning)
        {
            HueSessionSummary summary;
            Action<HueSessionSummary>? sessionSummarySink;
            lock (_syncLock)
            {
                summary = new HueSessionSummary
                {
                    Outcome = seed.Outcome,
                    Item = seed.Item,
                    UserId = seed.UserId,
                    UserName = seed.UserName,
                    BridgeIp = seed.BridgeIp,
                    EntertainmentAreaId = seed.EntertainmentAreaId,
                    StartedAtUtc = seed.StartedAtUtc,
                    EndedAtUtc = seed.EndedAtUtc,
                    DurationSeconds = seed.DurationSeconds,
                    EffectiveFps = seed.EffectiveFps,
                    FramesProcessed = seed.FramesProcessed,
                    PacketsSent = seed.PacketsSent,
                    PacketsSkippedByThreshold = seed.PacketsSkippedByThreshold,
                    PacketSendFailures = seed.PacketSendFailures,
                    ReconnectAttempts = seed.ReconnectAttempts,
                    SeekRestartCount = seed.SeekRestartCount,
                    LastSeekPositionSeconds = seed.LastSeekPositionSeconds,
                    Error = seed.Error,
                    CleanupWarning = cleanupWarning
                };
                _lastSessionSummary = summary;
                AddSessionHistoryLocked(summary);
                sessionSummarySink = _sessionSummarySink;
            }

            if (_managesPlaybackEvents)
                PersistSessionHistory();

            if (sessionSummarySink != null)
            {
                try
                {
                    sessionSummarySink(summary);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not publish a completed Hue session summary to the parent service");
                }
            }
        }

        private void AddConcurrentSessionSummary(HueSessionSummary summary)
        {
            lock (_syncLock)
            {
                AddSessionHistoryLocked(summary);
            }

            PersistSessionHistory();
        }

        private void AddSessionHistoryLocked(HueSessionSummary summary)
        {
            _sessionHistory.Insert(0, summary);
            TrimSessionHistoryLocked();
        }

        private void TrimSessionHistoryLocked()
        {
            var retentionCount = Plugin.Instance?.Configuration?.GetSessionHistoryRetentionCount()
                ?? PluginConfiguration.DefaultSessionHistoryRetentionCount;
            if (_sessionHistory.Count > retentionCount)
                _sessionHistory.RemoveRange(retentionCount, _sessionHistory.Count - retentionCount);
        }

        private void ReleasePlaybackLifecycleLease()
        {
            IDisposable? lifecycleLease;
            lock (_syncLock)
            {
                lifecycleLease = _playbackLifecycleLease;
                _playbackLifecycleLease = null;
            }

            lifecycleLease?.Dispose();
        }

        private async Task CleanupAbortedStartup(string playSessionId, CancellationTokenSource syncCts)
        {
            bool pauseCleanupPending;
            lock (_syncLock)
            {
                if (!ReferenceEquals(_syncCts, syncCts) ||
                    (_currentPlaySessionId != null &&
                     !string.Equals(_currentPlaySessionId, playSessionId, StringComparison.Ordinal)))
                {
                    return;
                }

                pauseCleanupPending = string.Equals(_pauseCleanupSessionId, playSessionId, StringComparison.Ordinal);
            }

            if (pauseCleanupPending)
            {
                StopSync(deactivateArea: false, expectedPlaySessionId: playSessionId, clearSession: false);
                return;
            }

            var config = Plugin.Instance?.Configuration;
            var bridgeConfig = _currentBridgeConfig;
            var savedLightStates = _savedLightStates;
            StopSync(deactivateArea: false, expectedPlaySessionId: playSessionId);
            _currentBridgeConfig = null;
            _currentFrameResolution = null;
            _currentVideoScalingMode = null;
            _currentVideoDeinterlaceMode = null;
            _currentTargetFps = null;
            _currentAudioSensitivityPercent = null;
            _currentAudioNoiseGatePercent = null;
            _currentAudioFrequencies = null;
            _currentAudioBandGains = null;
            _currentAudioBandSpreadPercent = null;
            _currentAudioBeatPulsePercent = null;
            _currentAudioBeatPulseDecayPercent = null;
            _currentAudioColorPalette = null;
            _currentAudioSpatialMode = null;
            _currentAudioChannelMode = null;
            _currentSamplingBreadthPercent = null;
            _currentSamplingMode = null;
            _currentColorSmoothingPercent = null;
            await RestoreAndDeactivateAsync(
                config,
                bridgeConfig,
                savedLightStates,
                sessionOutcome: "StartupFailed").ConfigureAwait(false);
        }

        /// <summary>
        /// Observes a fire-and-forget task so that exceptions are logged instead of
        /// becoming unobserved task exceptions (which can crash the process).
        /// </summary>
        private void ObserveTask(Task task)
        {
            task.ContinueWith(
                t => _logger.LogError(t.Exception!.GetBaseException(), "Unobserved exception in background task"),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        /// <summary>
        /// Converts RGB color to HSL (Hue, Saturation, Lightness)
        /// </summary>
        internal (double h, double s, double l) RgbToHsl(double r, double g, double b)
        {
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double delta = max - min;

            double h = 0, s = 0, l = (max + min) / 2.0;

            if (delta != 0)
            {
                s = l < 0.5 ? delta / (max + min) : delta / (2.0 - max - min);

                if (max == r)
                    h = ((g - b) / delta) + (g < b ? 6 : 0);
                else if (max == g)
                    h = ((b - r) / delta) + 2;
                else
                    h = ((r - g) / delta) + 4;

                h /= 6.0;
            }

            return (h, s, l);
        }

        /// <summary>
        /// Converts HSL color to RGB
        /// </summary>
        internal (double r, double g, double b) HslToRgb(double h, double s, double l)
        {
            double r, g, b;

            if (s == 0)
            {
                r = g = b = l; // Achromatic
            }
            else
            {
                double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
                double p = 2 * l - q;
                r = HueToRgb(p, q, h + 1.0 / 3.0);
                g = HueToRgb(p, q, h);
                b = HueToRgb(p, q, h - 1.0 / 3.0);
            }

            return (r, g, b);
        }

        /// <summary>
        /// Helper method for HSL to RGB conversion
        /// </summary>
        internal double HueToRgb(double p, double q, double t)
        {
            if (t < 0)
                t += 1;
            if (t > 1)
                t -= 1;
            if (t < 1.0 / 6.0)
                return p + (q - p) * 6 * t;
            if (t < 1.0 / 2.0)
                return q;
            if (t < 2.0 / 3.0)
                return p + (q - p) * (2.0 / 3.0 - t) * 6;
            return p;
        }

        private sealed class ConcurrentPlaybackWorker
        {
            public required HueSyncService Service { get; init; }
            public required string PlaySessionId { get; init; }
            public string? ClientSessionId { get; init; }
            public required string ResourceKey { get; init; }
        }

        private sealed class SessionSummarySeed
        {
            public string Outcome { get; init; } = "Stopped";
            public string? Item { get; init; }
            public string? UserId { get; init; }
            public string? UserName { get; init; }
            public string? BridgeIp { get; init; }
            public string? EntertainmentAreaId { get; init; }
            public DateTime? StartedAtUtc { get; init; }
            public DateTime EndedAtUtc { get; init; }
            public double? DurationSeconds { get; init; }
            public double? EffectiveFps { get; init; }
            public long FramesProcessed { get; init; }
            public long PacketsSent { get; init; }
            public long PacketsSkippedByThreshold { get; init; }
            public long PacketSendFailures { get; init; }
            public int ReconnectAttempts { get; init; }
            public int SeekRestartCount { get; init; }
            public double? LastSeekPositionSeconds { get; init; }
            public string? Error { get; init; }
        }
    }

    /// <summary>
    /// Sanitized point-in-time diagnostics for the active Hue synchronization session.
    /// This type intentionally contains no bridge credentials.
    /// </summary>
    public sealed class HueRuntimeStatus
    {
        /// <summary>
        /// Jellyfin playback lifecycle identifier. This is a routing label only and
        /// never contains bridge credentials.
        /// </summary>
        public string? PlaySessionId { get; init; }
        public string State { get; init; } = "Idle";
        public string Message { get; init; } = "Waiting for playback.";
        public string? LastError { get; init; }
        public string? CleanupWarning { get; init; }
        public string? CurrentItem { get; init; }
        /// <summary>
        /// Sanitized identity of the Jellyfin user whose playback owns the active Hue
        /// lifecycle. No bridge credentials or session tokens are included.
        /// </summary>
        public string? ActiveUserId { get; init; }
        public string? ActiveUserName { get; init; }
        public string? ActivePlaybackMediaFilter { get; init; }
        public string? ActiveFrameResolution { get; init; }
        public string? ActiveVideoScalingMode { get; init; }
        public string? ActiveVideoDeinterlaceMode { get; init; }
        public int? ActiveTargetFps { get; init; }
        public int? ActiveAudioSensitivityPercent { get; init; }
        public int? ActiveAudioNoiseGatePercent { get; init; }
        public int? ActiveAudioLowFrequencyHz { get; init; }
        public int? ActiveAudioMidFrequencyHz { get; init; }
        public int? ActiveAudioHighFrequencyHz { get; init; }
        public int? ActiveAudioLowGainPercent { get; init; }
        public int? ActiveAudioMidGainPercent { get; init; }
        public int? ActiveAudioHighGainPercent { get; init; }
        public int? ActiveAudioBandSpreadPercent { get; init; }
        public int? ActiveAudioBeatPulsePercent { get; init; }
        public int? ActiveAudioBeatPulseDecayPercent { get; init; }
        public string? ActiveAudioColorPalette { get; init; }
        public string? ActiveAudioSpatialMode { get; init; }
        public string? ActiveAudioChannelMode { get; init; }
        public int? ActiveSamplingBreadthPercent { get; init; }
        public string? ActiveSamplingMode { get; init; }
        public int? ActiveColorSmoothingPercent { get; init; }
        public int? ActiveBrightnessBoost { get; init; }
        public int? ActiveRedGain { get; init; }
        public int? ActiveGreenGain { get; init; }
        public int? ActiveBlueGain { get; init; }
        public int? ActiveColorSaturation { get; init; }
        public int? ActiveHueShiftDegrees { get; init; }
        public int? ActiveOutputBrightnessPercent { get; init; }
        public int? ActiveBlackoutThreshold { get; init; }
        public int? ActiveColorChangeThreshold { get; init; }
        public bool? ActiveUseGpu { get; init; }
        public bool? ActiveCustomFfmpegFlagsConfigured { get; init; }
        public int? ActiveFfmpegStallTimeoutSeconds { get; init; }
        public int? ActiveNetworkRetryAttempts { get; init; }
        public string? ActiveChannelIds { get; init; }
        public bool? ActiveRestoreLightState { get; init; }
        public string? ActiveBridgeIp { get; init; }
        public string? ActiveEntertainmentAreaId { get; init; }
        public bool IsSyncing { get; init; }
        public bool CanStopSync { get; init; }
        public long FramesProcessed { get; init; }
        public double? EffectiveFps { get; init; }
        public long PacketsSent { get; init; }
        public long PacketsSkippedByThreshold { get; init; }
        public long PacketSendFailures { get; init; }
        public int ReconnectAttempts { get; init; }
        public int SeekRestartCount { get; init; }
        public double? LastSeekPositionSeconds { get; init; }
        public bool IsFfmpegHealthy { get; init; }
        public bool IsDtlsHealthy { get; init; }
        public double? SyncDurationSeconds { get; init; }
        public DateTime? SyncStartedAtUtc { get; init; }
        public HueSessionSummary? LastSession { get; init; }
    }

    /// <summary>
    /// Sanitized summary of the most recently completed Hue playback session. It contains
    /// playback and bridge target labels plus aggregate telemetry, but never credentials or
    /// Jellyfin playback tokens.
    /// </summary>
    public sealed class HueSessionSummary
    {
        public string Outcome { get; init; } = "Stopped";
        public string? Item { get; init; }
        public string? UserId { get; init; }
        public string? UserName { get; init; }
        public string? BridgeIp { get; init; }
        public string? EntertainmentAreaId { get; init; }
        public DateTime? StartedAtUtc { get; init; }
        public DateTime? EndedAtUtc { get; init; }
        public double? DurationSeconds { get; init; }
        public double? EffectiveFps { get; init; }
        public long FramesProcessed { get; init; }
        public long PacketsSent { get; init; }
        public long PacketsSkippedByThreshold { get; init; }
        public long PacketSendFailures { get; init; }
        public int ReconnectAttempts { get; init; }
        public int SeekRestartCount { get; init; }
        public double? LastSeekPositionSeconds { get; init; }
        public string? Error { get; init; }
        public string? CleanupWarning { get; init; }
    }
}
