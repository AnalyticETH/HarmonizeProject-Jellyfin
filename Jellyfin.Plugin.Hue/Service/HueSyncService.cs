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
        private const double SamplingBreadth = 0.15; // 15% sampling area around each light position

        // Timing constants
        private const int CinemaModeDimmingDelayMs = 500;
        private const int RestoreLightsDelayMs = 300;
        private const int DefaultTargetFps = 20;
        private const int MinFps = 1;
        private const int MaxFps = 60;
        private const int DefaultFrameDurationMs = 50;

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
        private volatile bool _isSyncStarting; // Prevent concurrent sync starts
        private readonly object _syncLock = new object();
        private (string BridgeIp, string AppKey, string ClientKey, string AreaId)? _currentBridgeConfig;

        // Public property to track sync state
        public bool IsSyncing => _syncCts != null && !_syncCts.IsCancellationRequested;
        public string? CurrentItemName { get; private set; }

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
            _logger.LogInformation("Hue Sync Service Started.");
            _sessionManager.PlaybackStart += OnPlaybackStart;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            _sessionManager.PlaybackProgress += OnPlaybackProgress;

            // Helpers
            _hueStreamer = new HueStreamer(_loggerFactory.CreateLogger<HueStreamer>());
            _ffmpegStreamer = new FfmpegStreamer(_loggerFactory.CreateLogger<FfmpegStreamer>());

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Hue Sync Service Stopping.");
            _sessionManager.PlaybackStart -= OnPlaybackStart;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            _sessionManager.PlaybackProgress -= OnPlaybackProgress;
            StopSync();
            return Task.CompletedTask;
        }

        private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
        {
            _logger.LogInformation("Playback started for item {0}", e.Item.Name);

            // Skip if already syncing or starting for this session
            if (_isSyncStarting || (IsSyncing && _currentPlaySessionId == e.PlaySessionId))
            {
                _logger.LogDebug("Sync already in progress for this session, skipping duplicate start");
                return;
            }

            _currentPlaySessionId = e.PlaySessionId;
            ObserveTask(StartSyncForItem(e));
        }

        private async void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
        {
            if (_currentPlaySessionId != null && !string.Equals(_currentPlaySessionId, e.PlaySessionId, StringComparison.Ordinal))
            {
                return;
            }

            _logger.LogInformation("Playback stopped for item {0}", e.Item?.Name ?? "Unknown");

            // Wrap in try/catch: this is async void (required by event signature).
            // An unhandled exception here would crash the entire Jellyfin process.
            try
            {
                var config = Plugin.Instance?.Configuration;
                var bridgeConfig = _currentBridgeConfig;

                // Restore saved light states if configured
                if (config != null && config.SyncEnabled && config.RestoreLightState && _savedLightStates != null && bridgeConfig != null)
                {
                    _logger.LogInformation("Restoring saved light states");
                    await _hueClient.RestoreLightStates(bridgeConfig.Value.BridgeIp, bridgeConfig.Value.AppKey, _savedLightStates);
                    _savedLightStates = null;
                }
                // Otherwise restore lights if cinema mode was used
                else if (config != null && config.UseCinemaMode && config.SyncEnabled && bridgeConfig != null)
                {
                    _logger.LogInformation("Restoring lights after playback");
                    await RestoreLightsAfterPlayback(bridgeConfig.Value.BridgeIp, bridgeConfig.Value.AppKey, bridgeConfig.Value.AreaId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during playback stopped cleanup");
            }
            finally
            {
                CurrentItemName = null;
                StopSync();
                _currentBridgeConfig = null; // Clear only after StopSync has used it
            }
        }

        private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
        {
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
                StopSync();
            }
            else if (!e.IsPaused && _syncCts == null && !_isSyncStarting)
            {
                _logger.LogInformation("Playback resumed, restarting light sync");
                _currentPlaySessionId = e.PlaySessionId;
                ObserveTask(StartSyncForItem(e));
            }
        }

        private void StopSync()
        {
            _syncCts?.Cancel();
            _ffmpegStreamer?.Stop();
            _hueStreamer?.StopStream();
            _syncCts?.Dispose();
            _syncCts = null;
            _currentPlaySessionId = null;

            // Deactivate entertainment area so lights return to normal Hue control.
            // _currentBridgeConfig is cleared by the caller (OnPlaybackStopped / StopAsync) after this returns.
            if (_currentBridgeConfig != null)
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
            await Task.Delay(200); // Let bridge enter streaming mode

            var tempStreamer = new HueStreamer(_loggerFactory.CreateLogger<HueStreamer>());
            await tempStreamer.StartStreamAsync(bridgeIp, appKey, clientKey).ConfigureAwait(false);
            await tempStreamer.SendColors(areaId, channelColors);
            await Task.Delay(delayMs);
            tempStreamer.StopStream();

            await _hueClient.StopEntertainmentArea(bridgeIp, appKey, areaId);
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
        private async Task RestoreLightsAfterPlayback(string bridgeIp, string appKey, string areaId)
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

                // Use current bridge config for temporary streamer
                if (_currentBridgeConfig != null)
                {
                    await SendTemporaryColorsWithConfig(_currentBridgeConfig.Value.BridgeIp, _currentBridgeConfig.Value.AppKey, _currentBridgeConfig.Value.ClientKey, areaId, channelColors, RestoreLightsDelayMs);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to restore lights");
            }
        }

        private async Task RunSyncLoop(Stream videoStream, Dictionary<int, (double x, double z)> lights, string areaId, int targetFrameDurationMs, CancellationToken token)
        {
            int frameSize = FrameWidth * FrameHeight * BytesPerPixel;
            byte[] buffer = new byte[frameSize];

            // Pre-calculate bounds for each light based on position
            // Following HarmonizeProject logic: use x (horizontal) and z (vertical) for 2D screen plane
            int avgSize = (FrameWidth + FrameHeight) / 2;
            int dist = (int)(SamplingBreadth * avgSize);

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var loopTimer = Stopwatch.StartNew();

                    // Read full frame
                    int bytesRead = 0;
                    while (bytesRead < frameSize)
                    {
                        int n = await videoStream.ReadAsync(buffer, bytesRead, frameSize - bytesRead, token);
                        if (n == 0) break; // End of stream
                        bytesRead += n;
                    }
                    if (bytesRead < frameSize)
                    {
                        _logger.LogInformation("End of video stream reached. Total frames processed: {0}", _ffmpegStreamer?.FramesProcessed ?? 0);
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

                        if (count == 0) count = 1;
                        byte r = (byte)(rSum / count);
                        byte g = (byte)(gSum / count);
                        byte b = (byte)(bSum / count);

                        channelColors[kvp.Key] = new byte[] { r, g, b };
                    }

                    // Get configuration for advanced color processing
                    var config = Plugin.Instance?.Configuration;
                    if (config != null)
                    {
                        // Check blackout threshold - send dark colors if frame is mostly black
                        if (config.BlackoutThreshold > 0)
                        {
                            var avgBrightness = channelColors.Values.Average(c => (c[0] + c[1] + c[2]) / 3.0);
                            if (avgBrightness < config.BlackoutThreshold)
                            {
                                // Send black to all channels so lights actually dim during dark scenes
                                var blackColors = new Dictionary<int, byte[]>();
                                foreach (var kvp in channelColors)
                                {
                                    blackColors[kvp.Key] = new byte[] { 0, 0, 0, 0, 0, 0 };
                                }
                                await _hueStreamer!.SendColors(areaId, blackColors, config.ColorChangeThreshold);

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

                        await _hueStreamer!.SendColors(areaId, processedColors, config.ColorChangeThreshold);
                    }
                    else
                    {
                        // Fallback without advanced processing
                        var simpleColors = new Dictionary<int, byte[]>();
                        foreach (var kvp in channelColors)
                        {
                            byte r2 = (byte)(kvp.Value[0] / ColorDivisor);
                            byte g2 = (byte)(kvp.Value[1] / ColorDivisor);
                            byte b2 = (byte)(kvp.Value[2] / ColorDivisor);
                            simpleColors[kvp.Key] = new byte[] { r2, r2, g2, g2, b2, b2 };
                        }
                        await _hueStreamer!.SendColors(areaId, simpleColors);
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
                _logger.LogError(ex, "Error in Sync Loop");
            }
            finally
            {
                try { videoStream.Dispose(); } catch { }
            }
        }

        private async Task StartSyncForItem(PlaybackProgressEventArgs e)
        {
            // Prevent concurrent sync starts
            lock (_syncLock)
            {
                if (_isSyncStarting)
                {
                    _logger.LogDebug("Sync start already in progress, skipping");
                    return;
                }
                _isSyncStarting = true;
            }

            try
            {
                await StartSyncForItemInternal(e);
            }
            finally
            {
                _isSyncStarting = false;
            }
        }

        private async Task StartSyncForItemInternal(PlaybackProgressEventArgs e)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.SyncEnabled)
            {
                _logger.LogInformation("Hue Sync disabled, skipping.");
                return;
            }

            if (_hueStreamer == null || _ffmpegStreamer == null)
            {
                _logger.LogWarning("Hue sync helpers are not initialized yet");
                return;
            }

            var validationErrors = config.Validate();
            if (validationErrors.Count > 0)
            {
                _logger.LogWarning("Configuration validation failed: {0}", string.Join(", ", validationErrors));
                return;
            }

            var videoPath = e.Item?.Path;
            if (string.IsNullOrWhiteSpace(videoPath))
            {
                _logger.LogWarning("Unable to determine media path for playback item {0}", e.Item?.Name ?? "Unknown");
                return;
            }

            // Get user-specific bridge configuration
            var userId = e.Session?.UserId ?? Guid.Empty;
            var (bridgeIp, appKey, clientKey, areaId) = config.GetBridgeConfigForUser(userId);

            if (string.IsNullOrWhiteSpace(bridgeIp) || string.IsNullOrWhiteSpace(appKey) || string.IsNullOrWhiteSpace(areaId))
            {
                _logger.LogWarning("No valid bridge configuration found for user {0}", userId);
                return;
            }

            _logger.LogInformation("Starting sync for user {0} with bridge {1} and area {2}", userId, bridgeIp, areaId);

            StopSync();
            _currentPlaySessionId = e.PlaySessionId;
            _currentBridgeConfig = (bridgeIp, appKey, clientKey, areaId);
            _syncCts = new CancellationTokenSource();
            _syncStartTime = DateTime.UtcNow;

            try
            {
                var areaConfig = await _hueClient.GetEntertainmentConfiguration(bridgeIp, appKey, areaId);
                if (areaConfig == null)
                {
                    _logger.LogWarning("Failed to load entertainment configuration from bridge");
                    return;
                }

                // Save current light states if configured
                if (config.RestoreLightState)
                {
                    _logger.LogInformation("Saving current light states for restoration");
                    _savedLightStates = await _hueClient.GetLightStates(bridgeIp, appKey, areaConfig.Value);
                }

                if (config.UseCinemaMode)
                {
                    _logger.LogInformation("Cinema mode enabled, dimming lights to {0}%", config.BrightnessDimLevel);
                    await ApplyCinemaMode(config, bridgeIp, appKey, clientKey, areaId, areaConfig.Value);
                }

                CurrentItemName = e.Item?.Name;

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
                    return;
                }

                // CRITICAL: Activate the entertainment area on the bridge BEFORE opening the DTLS tunnel.
                // The bridge silently drops all DTLS packets if the area is not in streaming mode.
                _logger.LogInformation("Activating entertainment area {0} for streaming", areaId);
                var activated = await _hueClient.StartEntertainmentArea(bridgeIp, appKey, areaId);
                if (!activated)
                {
                    _logger.LogError("Could not activate entertainment area {0} — aborting sync", areaId);
                    StopSync();
                    return;
                }

                // Small delay to let the bridge switch to streaming mode before the DTLS tunnel
                await Task.Delay(200);

                // Set reconnect callback so DTLS reconnections re-activate the area first
                _hueStreamer!.OnBeforeReconnect = () => _hueClient.StartEntertainmentArea(bridgeIp, appKey, areaId);
                await _hueStreamer.StartStreamAsync(bridgeIp, appKey, clientKey).ConfigureAwait(false);

                var targetFrameDurationMs = config.TargetFps > 0
                    ? 1000 / Math.Clamp(config.TargetFps, MinFps, MaxFps)
                    : DefaultFrameDurationMs;

                // Seek to current playback position so lights sync to what's actually on screen
                double seekSeconds = 0;
                if (e.PlaybackPositionTicks.HasValue && e.PlaybackPositionTicks.Value > 0)
                    seekSeconds = TimeSpan.FromTicks(e.PlaybackPositionTicks.Value).TotalSeconds;

                var videoStream = _ffmpegStreamer!.StartFfmpeg(videoPath, config.TargetFps, config.UseGpu, config.CustomFfmpegFlags, _mediaEncoder.EncoderPath, seekPositionSeconds: seekSeconds);
                if (videoStream == null)
                {
                    _logger.LogWarning("FFmpeg stream could not be started for path {0}", videoPath);
                    StopSync();
                    return;
                }

                var token = _syncCts.Token;
                _ = Task.Run(() => RunSyncLoop(videoStream, lights, areaId, targetFrameDurationMs, token), token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting Hue sync session");
                StopSync();
            }
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
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
            return p;
        }
    }
}
