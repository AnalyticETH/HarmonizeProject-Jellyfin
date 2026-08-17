using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Hue.Configuration;
using Jellyfin.Plugin.Hue.Hue;
using Jellyfin.Plugin.Hue.Video;
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

        // Color processing constants
        private const int ColorDivisor = 2; // Divide by 2 for 16-bit color compatibility
        private const int FullBrightnessValue = 127; // Full brightness for 16-bit representation

        private readonly ISessionManager _sessionManager;
        private readonly ILogger<HueSyncService> _logger;
        private readonly IMediaEncoder _mediaEncoder;

        private FfmpegStreamer? _ffmpegStreamer;
        private HueStreamer? _hueStreamer;
        private readonly HueClient _hueClient;
        private CancellationTokenSource? _syncCts;
        private readonly ILoggerFactory _loggerFactory;
        private string? _currentPlaySessionId;
        private List<HueClient.LightState>? _savedLightStates;
        private DateTime _syncStartTime;
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
        private string? _manuallyStoppedPlaySessionId;
        private string _runtimeState = "Idle";
        private string _runtimeMessage = "Waiting for playback.";
        private string? _lastError;

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

        public HueSyncService(ISessionManager sessionManager, ILogger<HueSyncService> logger, ILoggerFactory loggerFactory, HueClient hueClient, IMediaEncoder mediaEncoder)
        {
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _hueClient = hueClient ?? throw new ArgumentNullException(nameof(hueClient));
            _mediaEncoder = mediaEncoder ?? throw new ArgumentNullException(nameof(mediaEncoder));
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _isStopping = false;
            SetRuntimeStatus("Idle", "Waiting for playback.", clearError: true);
            _logger.LogInformation("Hue Sync Service Started.");
            _sessionManager.PlaybackStart += OnPlaybackStart;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            _sessionManager.PlaybackProgress += OnPlaybackProgress;

            // Helpers
            _hueStreamer = new HueStreamer(_loggerFactory.CreateLogger<HueStreamer>());
            _ffmpegStreamer = new FfmpegStreamer(_loggerFactory.CreateLogger<FfmpegStreamer>());

            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Hue Sync Service Stopping.");
            _sessionManager.PlaybackStart -= OnPlaybackStart;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            _sessionManager.PlaybackProgress -= OnPlaybackProgress;

            Task? pauseCleanup;
            lock (_syncLock)
            {
                _isStopping = true;
                _startupCts?.Cancel();
                _syncCts?.Cancel();
                _manuallyStoppedPlaySessionId = null;
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
                    var bridgeConfig = _currentBridgeConfig;
                    var areaAlreadyDeactivated = _bridgeAreaDeactivated;
                    StopSync(deactivateArea: false);
                    _currentBridgeConfig = null;
                    _currentItemName = null;

                    if (bridgeConfig != null && !areaAlreadyDeactivated)
                    {
                        await _hueClient.StopEntertainmentArea(
                            bridgeConfig.Value.BridgeIp,
                            bridgeConfig.Value.AppKey,
                            bridgeConfig.Value.AreaId).ConfigureAwait(false);
                        _bridgeAreaDeactivated = true;
                    }
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
        public async Task<bool> StopCurrentSyncAsync()
        {
            await _syncLifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                string? playSessionId;
                lock (_syncLock)
                {
                    if (!CanStopSync)
                        return false;

                    playSessionId = _currentPlaySessionId ?? _startingPlaySessionId;
                    _manuallyStoppedPlaySessionId = playSessionId;
                    _startupCts?.Cancel();
                    _syncCts?.Cancel();
                }

                var config = Plugin.Instance?.Configuration;
                var bridgeConfig = _currentBridgeConfig;
                var savedLightStates = _savedLightStates;
                StopSync(deactivateArea: false, expectedPlaySessionId: playSessionId, clearSession: false);
                _currentBridgeConfig = null;

                await RestoreAndDeactivateAsync(config, bridgeConfig, savedLightStates).ConfigureAwait(false);
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
            string state;
            string message;
            string? lastError;
            DateTime syncStartTime;
            CancellationTokenSource? syncCts;
            bool canStopSync;

            lock (_syncLock)
            {
                bridgeConfig = _currentBridgeConfig;
                currentItem = _currentItemName;
                state = _runtimeState;
                message = _runtimeMessage;
                lastError = _lastError;
                syncStartTime = _syncStartTime;
                syncCts = _syncCts;
                canStopSync = CanStopSync;
            }

            var isSyncing = syncCts != null && !syncCts.IsCancellationRequested;
            var ffmpeg = _ffmpegStreamer;
            var hueStreamer = _hueStreamer;
            var syncDuration = isSyncing && syncStartTime != default
                ? Math.Max(0, (DateTime.UtcNow - syncStartTime).TotalSeconds)
                : (double?)null;

            return new HueRuntimeStatus
            {
                State = state,
                Message = message,
                LastError = lastError,
                CurrentItem = currentItem,
                ActiveBridgeIp = isSyncing ? bridgeConfig?.BridgeIp : null,
                ActiveEntertainmentAreaId = isSyncing ? bridgeConfig?.AreaId : null,
                IsSyncing = isSyncing,
                CanStopSync = canStopSync,
                FramesProcessed = ffmpeg?.FramesProcessed ?? 0,
                IsFfmpegHealthy = isSyncing && ffmpeg?.IsHealthy(
                    Plugin.Instance?.Configuration?.FfmpegStallTimeoutSeconds ?? DefaultFfmpegStallTimeoutSeconds) == true,
                IsDtlsHealthy = isSyncing && hueStreamer?.IsHealthy() == true,
                SyncDurationSeconds = syncDuration,
                SyncStartedAtUtc = isSyncing && syncStartTime != default ? syncStartTime : null
            };
        }

        private void SetRuntimeStatus(string state, string message, bool clearError = false)
        {
            lock (_syncLock)
            {
                _runtimeState = state;
                _runtimeMessage = message;
                if (clearError)
                    _lastError = null;
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

        private bool IsPlaybackUserSyncEnabled(PlaybackProgressEventArgs e)
        {
            var config = Plugin.Instance?.Configuration;
            return config == null || config.IsSyncEnabledForUser(e.Session?.UserId ?? Guid.Empty);
        }

        private void ClearTransientRuntimeWarning()
        {
            lock (_syncLock)
            {
                if (_lastError != null && _lastError.StartsWith("The DTLS stream could not send", StringComparison.Ordinal))
                {
                    _runtimeState = "Syncing";
                    _runtimeMessage = "Streaming video colors to Hue.";
                    _lastError = null;
                }
            }
        }

        private TimeSpan GetFrameReadTimeout()
        {
            var configuredTimeout = Plugin.Instance?.Configuration?.FfmpegStallTimeoutSeconds
                ?? DefaultFfmpegStallTimeoutSeconds;
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

        private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
        {
            if (_isStopping)
                return;
            _logger.LogInformation("Playback started for item {0}", e.Item.Name);

            if (!IsPlaybackUserSyncEnabled(e))
            {
                _logger.LogInformation("Hue Sync is disabled for this playback user, skipping start");
                SetRuntimeStatus("Idle", "Sync is disabled for this user.");
                return;
            }

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
                    _currentPlaySessionId = e.PlaySessionId;
            }

            ObserveTask(StartSyncForItem(e));
        }

        private async void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
        {
            if (_isStopping)
                return;

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

            try
            {
                await RestoreAndDeactivateAsync(config, bridgeConfig, savedLightStates);
            }
            finally
            {
                lock (_syncLock)
                {
                    if (string.Equals(_currentPlaySessionId, e.PlaySessionId, StringComparison.Ordinal))
                        _currentPlaySessionId = null;
                }
                _syncLifecycleLock.Release();
            }
        }

        private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
        {
            if (_isStopping)
                return;

            if (!IsPlaybackUserSyncEnabled(e))
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

                _logger.LogInformation("Playback paused, stopping light sync");
                SetRuntimeStatus("Paused", "Playback paused; lights are being restored.");
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

                var bridgeConfig = _currentBridgeConfig;
                StopSync(deactivateArea: false, expectedPlaySessionId: playSessionId);

                if (bridgeConfig != null)
                {
                    await _hueClient.StopEntertainmentArea(
                        bridgeConfig.Value.BridgeIp,
                        bridgeConfig.Value.AppKey,
                        bridgeConfig.Value.AreaId).ConfigureAwait(false);
                    _bridgeAreaDeactivated = true;
                }

                SetRuntimeStatus("Paused", "Playback paused; waiting to resume.");
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
                    _currentPlaySessionId = null;
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
        private async Task SendTemporaryColorsWithConfig(string bridgeIp, string appKey, string clientKey, string areaId, Dictionary<int, byte[]> channelColors, int delayMs)
        {
            // Must activate the area before opening a DTLS session
            var activated = await _hueClient.StartEntertainmentArea(bridgeIp, appKey, areaId);
            if (!activated)
            {
                _logger.LogWarning("SendTemporaryColorsWithConfig: could not activate area {0}, skipping", areaId);
                return;
            }

            try
            {
                await Task.Delay(EntertainmentAreaActivationDelayMs);

                var tempStreamer = new HueStreamer(_loggerFactory.CreateLogger<HueStreamer>());
                try
                {
                    await tempStreamer.StartStreamAsync(bridgeIp, appKey, clientKey).ConfigureAwait(false);
                    if (!tempStreamer.IsHealthy())
                    {
                        _logger.LogWarning("SendTemporaryColorsWithConfig: DTLS stream did not start for area {0}", areaId);
                        return;
                    }

                    await tempStreamer.SendColors(areaId, channelColors);
                    await Task.Delay(delayMs);
                }
                finally
                {
                    tempStreamer.StopStream();
                }
            }
            finally
            {
                await _hueClient.StopEntertainmentArea(bridgeIp, appKey, areaId);
            }
        }

        /// <summary>
        /// Applies cinema mode by dimming lights to configured level
        /// </summary>
        private async Task ApplyCinemaMode(PluginConfiguration config, string bridgeIp, string appKey, string clientKey, string areaId, System.Text.Json.JsonElement areaConfig)
        {
            try
            {
                if (!areaConfig.TryGetProperty("channels", out var channels))
                    return;

                var dimLevel = Math.Clamp(config.BrightnessDimLevel, 0, 100);
                var dimBrightness = (byte)(dimLevel * 255 / 100 / 2); // Divide by 2 for 16-bit compatibility

                var channelColors = new Dictionary<int, byte[]>();
                foreach (var channel in channels.EnumerateArray())
                {
                    var channelId = channel.GetProperty("channel_id").GetInt32();
                    // Warm white color at dim level
                    channelColors[channelId] = new byte[] { dimBrightness, dimBrightness, dimBrightness, dimBrightness, (byte)(dimBrightness * 0.8), (byte)(dimBrightness * 0.8) };
                }

                // Send dim command before starting stream
                await SendTemporaryColorsWithConfig(bridgeIp, appKey, clientKey, areaId, channelColors, CinemaModeDimmingDelayMs);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply cinema mode");
            }
        }

        /// <summary>
        /// Restores lights to normal brightness after playback
        /// </summary>
        private async Task RestoreLightsAfterPlayback(string bridgeIp, string appKey, string clientKey, string areaId)
        {
            try
            {
                var areaConfig = await _hueClient.GetEntertainmentConfiguration(bridgeIp, appKey, areaId);
                if (areaConfig == null || !areaConfig.Value.TryGetProperty("channels", out var channels))
                    return;

                var channelColors = new Dictionary<int, byte[]>();
                foreach (var channel in channels.EnumerateArray())
                {
                    var channelId = channel.GetProperty("channel_id").GetInt32();
                    // Full white
                    channelColors[channelId] = new byte[] { FullBrightnessValue, FullBrightnessValue, FullBrightnessValue, FullBrightnessValue, FullBrightnessValue, FullBrightnessValue };
                }

                await SendTemporaryColorsWithConfig(bridgeIp, appKey, clientKey, areaId, channelColors, RestoreLightsDelayMs);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to restore lights");
            }
        }

        internal static int CalculateSamplingDistance(int samplingBreadthPercent)
        {
            var normalizedPercent = samplingBreadthPercent <= 0
                ? DefaultSamplingBreadthPercent
                : Math.Clamp(samplingBreadthPercent, MinSamplingBreadthPercent, MaxSamplingBreadthPercent);
            var averageFrameSize = (FrameWidth + FrameHeight) / 2;
            return Math.Max(1, (int)(normalizedPercent / 100.0 * averageFrameSize));
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
                DefaultSamplingBreadthPercent);
        }

        private async Task RunSyncLoopWithSampling(
            Stream videoStream,
            Dictionary<int, (double x, double z)> lights,
            string areaId,
            int targetFrameDurationMs,
            CancellationTokenSource expectedSyncCts,
            string playSessionId,
            int samplingBreadthPercent)
        {
            int frameSize = FrameWidth * FrameHeight * BytesPerPixel;
            byte[] buffer = new byte[frameSize];
            var token = expectedSyncCts.Token;
            var streamEnded = false;
            var streamFailed = false;
            var consecutiveSendFailures = 0;
            var previousChannelColors = new Dictionary<int, byte[]>();

            // Pre-calculate bounds for each light based on position
            // Following HarmonizeProject logic: use x (horizontal) and z (vertical) for 2D screen plane
            int dist = CalculateSamplingDistance(samplingBreadthPercent);

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
                        int cx = (int)((kvp.Value.x + 1.0) * (FrameWidth - 1) / 2.0);
                        int cy = (int)((1.0 - kvp.Value.z) * (FrameHeight - 1) / 2.0); // Invert z for screen coordinates

                        int minX = Math.Max(0, cx - dist);
                        int maxX = Math.Min(FrameWidth, cx + dist);
                        int minY = Math.Max(0, cy - dist);
                        int maxY = Math.Min(FrameHeight, cy + dist);

                        long rSum = 0, gSum = 0, bSum = 0;
                        int count = 0;

                        // RGB24: R, G, B - Optimized tight loop
                        for (int y = minY; y < maxY; y++)
                        {
                            int rowStart = y * FrameWidth * BytesPerPixel;
                            for (int x = minX; x < maxX; x++)
                            {
                                int idx = rowStart + x * BytesPerPixel;
                                rSum += buffer[idx];
                                gSum += buffer[idx + 1];
                                bSum += buffer[idx + 2];
                                count++;
                            }
                        }

                        if (count == 0)
                            count = 1;
                        byte r = (byte)(rSum / count);
                        byte g = (byte)(gSum / count);
                        byte b = (byte)(bSum / count);

                        channelColors[kvp.Key] = new byte[] { r, g, b };
                    }

                    // Get configuration for advanced color processing
                    var config = Plugin.Instance?.Configuration;
                    if (config != null)
                    {
                        var isBlackout = config.BlackoutThreshold > 0 &&
                            channelColors.Count > 0 &&
                            channelColors.Values.Average(c => (c[0] + c[1] + c[2]) / 3.0) < config.BlackoutThreshold;

                        // Check blackout threshold - send dark colors if frame is mostly black
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
                            var blackoutSent = await _hueStreamer!.SendColors(areaId, blackColors, config.ColorChangeThreshold);
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

                        if (config.ColorSmoothingPercent > 0)
                        {
                            channelColors = ApplyTemporalSmoothing(
                                channelColors,
                                previousChannelColors,
                                config.ColorSmoothingPercent);
                            previousChannelColors = channelColors;
                        }
                        else
                        {
                            previousChannelColors.Clear();
                        }

                        // Apply brightness boost and color saturation adjustments
                        var processedColors = new Dictionary<int, byte[]>();
                        foreach (var kvp in channelColors)
                        {
                            var rgb = kvp.Value;
                            double r = rgb[0], g = rgb[1], b = rgb[2];

                            // Apply brightness boost
                            if (config.BrightnessBoost != 100)
                            {
                                double multiplier = config.BrightnessBoost / 100.0;
                                r = Math.Min(255, r * multiplier);
                                g = Math.Min(255, g * multiplier);
                                b = Math.Min(255, b * multiplier);
                            }

                            // Apply color saturation adjustment
                            if (config.ColorSaturation != 100)
                            {
                                // Convert to HSL, adjust saturation, convert back to RGB
                                var (hue, sat, lightness) = RgbToHsl(r / 255.0, g / 255.0, b / 255.0);
                                sat = Math.Clamp(sat * (config.ColorSaturation / 100.0), 0, 1);
                                var (r2, g2, b2) = HslToRgb(hue, sat, lightness);
                                r = r2 * 255;
                                g = g2 * 255;
                                b = b2 * 255;
                            }

                            // Format following HarmonizeProject: divide by 2 for 16-bit color compatibility
                            byte r16 = (byte)(Math.Clamp(r, 0, 255) / ColorDivisor);
                            byte g16 = (byte)(Math.Clamp(g, 0, 255) / ColorDivisor);
                            byte b16 = (byte)(Math.Clamp(b, 0, 255) / ColorDivisor);

                            processedColors[kvp.Key] = new byte[] { r16, r16, g16, g16, b16, b16 };
                        }

                        var processedSent = await _hueStreamer!.SendColors(areaId, processedColors, config.ColorChangeThreshold);
                        if (!HandleDtlsSendResult(processedSent, token, ref consecutiveSendFailures))
                        {
                            streamFailed = true;
                            break;
                        }
                    }
                    else
                    {
                        previousChannelColors.Clear();
                        // Fallback without advanced processing
                        var simpleColors = new Dictionary<int, byte[]>();
                        foreach (var kvp in channelColors)
                        {
                            byte r2 = (byte)(kvp.Value[0] / ColorDivisor);
                            byte g2 = (byte)(kvp.Value[1] / ColorDivisor);
                            byte b2 = (byte)(kvp.Value[2] / ColorDivisor);
                            simpleColors[kvp.Key] = new byte[] { r2, r2, g2, g2, b2, b2 };
                        }
                        var simpleSent = await _hueStreamer!.SendColors(areaId, simpleColors);
                        if (!HandleDtlsSendResult(simpleSent, token, ref consecutiveSendFailures))
                        {
                            streamFailed = true;
                            break;
                        }
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

                try
                {
                    await RestoreAndDeactivateAsync(config, bridgeConfig, savedLightStates).ConfigureAwait(false);
                    if (streamEnded)
                        SetRuntimeStatus("Idle", "Video stream ended; lights were restored.");
                }
                finally
                {
                    lock (_syncLock)
                    {
                        if (string.Equals(_currentPlaySessionId, playSessionId, StringComparison.Ordinal))
                            _currentPlaySessionId = null;
                    }
                }
            }
            finally
            {
                _syncLifecycleLock.Release();
            }
        }

        private async Task StartSyncForItem(PlaybackProgressEventArgs e)
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
                await StartSyncForItemInternal(e);
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
                    }
                }
            }
        }

        private async Task StartSyncForItemInternal(PlaybackProgressEventArgs e)
        {
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

                await StartSyncForItemCore(e, startupCts.Token).ConfigureAwait(false);
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

        private async Task StartSyncForItemCore(PlaybackProgressEventArgs e, CancellationToken startupToken)
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

            var videoPath = e.Item?.Path;
            if (string.IsNullOrWhiteSpace(videoPath))
            {
                _logger.LogWarning("Unable to determine media path for playback item {0}", e.Item?.Name ?? "Unknown");
                SetRuntimeError("The playback item has no readable media path.");
                return;
            }

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

            _hueClient.RetryAttempts = config.NetworkRetryAttempts;
            if (startupToken.IsCancellationRequested)
                return;

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
                    _syncCts = syncCts;
                    _currentPlaySessionId = e.PlaySessionId;
                    _currentBridgeConfig = (bridgeIp, appKey, clientKey, areaId);
                    _bridgeAreaDeactivated = false;
                    _syncStartTime = DateTime.UtcNow;
                    _currentItemName = e.Item?.Name;
                    syncStatePublished = true;
                }
            }

            var token = syncCts.Token;

            try
            {
                if (syncStatePublished)
                    SetRuntimeStatus("Starting", $"Preparing '{e.Item?.Name ?? "playback"}'...", clearError: true);

                if (token.IsCancellationRequested)
                    return;

                var areaConfig = await _hueClient.GetEntertainmentConfiguration(bridgeIp, appKey, areaId);
                if (token.IsCancellationRequested)
                    return;
                if (areaConfig == null)
                {
                    _logger.LogWarning("Failed to load entertainment configuration from bridge");
                    SetRuntimeError("Could not load the selected entertainment area configuration.");
                    return;
                }

                // Save current light states if configured
                if (config.RestoreLightState)
                {
                    _logger.LogInformation("Saving current light states for restoration");
                    var savedLightStates = await _hueClient.GetLightStates(bridgeIp, appKey, areaConfig.Value);
                    if (token.IsCancellationRequested)
                        return;
                    _savedLightStates = savedLightStates;
                }

                if (config.UseCinemaMode)
                {
                    _logger.LogInformation("Cinema mode enabled, dimming lights to {0}%", config.BrightnessDimLevel);
                    await ApplyCinemaMode(config, bridgeIp, appKey, clientKey, areaId, areaConfig.Value);
                    if (token.IsCancellationRequested)
                        return;
                }

                var lights = new Dictionary<int, (double x, double z)>();
                if (areaConfig.Value.TryGetProperty("channels", out var channels))
                {
                    foreach (var channel in channels.EnumerateArray())
                    {
                        var channelId = channel.GetProperty("channel_id").GetInt32();
                        var pos = channel.GetProperty("position");
                        var x = pos.GetProperty("x").GetDouble();
                        var z = pos.GetProperty("z").GetDouble();
                        lights[channelId] = (x, z);
                    }
                }

                if (lights.Count == 0)
                {
                    _logger.LogWarning("Entertainment area {0} returned no channels to control", areaId);
                    SetRuntimeError("The selected entertainment area has no controllable channels.");
                    return;
                }

                if (token.IsCancellationRequested)
                    return;

                // CRITICAL: Activate the entertainment area on the bridge BEFORE opening the DTLS tunnel.
                // The bridge silently drops all DTLS packets if the area is not in streaming mode.
                _logger.LogInformation("Activating entertainment area {0} for streaming", areaId);
                SetRuntimeStatus("Starting", "Activating the entertainment area...");
                var activated = await _hueClient.StartEntertainmentArea(bridgeIp, appKey, areaId);
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
                _hueStreamer!.OnBeforeReconnect = () => _hueClient.StartEntertainmentArea(bridgeIp, appKey, areaId);
                SetRuntimeStatus("Starting", "Opening the DTLS light stream...");
                await _hueStreamer.StartStreamAsync(bridgeIp, appKey, clientKey).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                    return;
                if (!_hueStreamer.IsHealthy())
                {
                    _logger.LogError("Could not establish DTLS stream for entertainment area {0}", areaId);
                    SetRuntimeError("The Hue bridge DTLS stream could not be established.");
                    return;
                }

                var targetFrameDurationMs = config.TargetFps > 0
                    ? 1000 / Math.Clamp(config.TargetFps, MinFps, MaxFps)
                    : DefaultFrameDurationMs;

                // Seek to current playback position so lights sync to what's actually on screen
                double seekSeconds = 0;
                if (e.PlaybackPositionTicks.HasValue && e.PlaybackPositionTicks.Value > 0)
                    seekSeconds = TimeSpan.FromTicks(e.PlaybackPositionTicks.Value).TotalSeconds;

                if (token.IsCancellationRequested)
                    return;
                _ffmpegStreamer!.StallTimeoutSeconds = config.FfmpegStallTimeoutSeconds;
                videoStream = _ffmpegStreamer!.StartFfmpeg(videoPath, config.TargetFps, config.UseGpu, config.CustomFfmpegFlags, _mediaEncoder.EncoderPath, seekPositionSeconds: seekSeconds);
                if (videoStream == null)
                {
                    _logger.LogWarning("FFmpeg stream could not be started for path {0}", videoPath);
                    SetRuntimeError("FFmpeg could not start the video capture stream.");
                    return;
                }

                // Let RunSyncLoop own disposal even when cancellation wins before scheduling.
                SetRuntimeStatus("Syncing", "Streaming video colors to Hue.");
                _ = Task.Run(() => RunSyncLoopWithSampling(
                    videoStream!,
                    lights,
                    areaId,
                    targetFrameDurationMs,
                    syncCts,
                    e.PlaySessionId,
                    config.SamplingBreadthPercent));
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
            List<HueClient.LightState>? savedLightStates)
        {
            try
            {
                if (config != null && config.SyncEnabled && config.RestoreLightState && savedLightStates != null && bridgeConfig != null)
                {
                    _logger.LogInformation("Restoring saved light states");
                    await _hueClient.RestoreLightStates(bridgeConfig.Value.BridgeIp, bridgeConfig.Value.AppKey, savedLightStates);
                    if (ReferenceEquals(_savedLightStates, savedLightStates))
                        _savedLightStates = null;
                }
                else if (config != null && config.UseCinemaMode && config.SyncEnabled && bridgeConfig != null)
                {
                    _logger.LogInformation("Restoring lights after playback");
                    await RestoreLightsAfterPlayback(
                        bridgeConfig.Value.BridgeIp,
                        bridgeConfig.Value.AppKey,
                        bridgeConfig.Value.ClientKey,
                        bridgeConfig.Value.AreaId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during playback light restoration");
            }
            finally
            {
                CurrentItemName = null;
                lock (_syncLock)
                {
                    if (!string.Equals(_runtimeState, "Error", StringComparison.Ordinal))
                    {
                        _runtimeState = "Idle";
                        _runtimeMessage = "Playback stopped.";
                    }
                }
                if (bridgeConfig != null)
                {
                    try
                    {
                        await _hueClient.StopEntertainmentArea(
                            bridgeConfig.Value.BridgeIp,
                            bridgeConfig.Value.AppKey,
                            bridgeConfig.Value.AreaId);
                        _bridgeAreaDeactivated = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error deactivating entertainment area during playback cleanup");
                    }
                }
            }
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
            await RestoreAndDeactivateAsync(config, bridgeConfig, savedLightStates).ConfigureAwait(false);
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
    }

    /// <summary>
    /// Sanitized point-in-time diagnostics for the active Hue synchronization session.
    /// This type intentionally contains no bridge credentials.
    /// </summary>
    public sealed class HueRuntimeStatus
    {
        public string State { get; init; } = "Idle";
        public string Message { get; init; } = "Waiting for playback.";
        public string? LastError { get; init; }
        public string? CurrentItem { get; init; }
        public string? ActiveBridgeIp { get; init; }
        public string? ActiveEntertainmentAreaId { get; init; }
        public bool IsSyncing { get; init; }
        public bool CanStopSync { get; init; }
        public long FramesProcessed { get; init; }
        public bool IsFfmpegHealthy { get; init; }
        public bool IsDtlsHealthy { get; init; }
        public double? SyncDurationSeconds { get; init; }
        public DateTime? SyncStartedAtUtc { get; init; }
    }
}
