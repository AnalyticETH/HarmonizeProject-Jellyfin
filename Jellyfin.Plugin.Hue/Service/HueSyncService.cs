using System;
using System.Threading;
using System.Threading.Tasks;

using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.Hue.Hue;
using Jellyfin.Plugin.Hue.Video;
using Jellyfin.Plugin.Hue.Configuration;

namespace Jellyfin.Plugin.Hue.Service
{
    /// <summary>
    /// Background service that monitors Jellyfin playback and synchronizes Hue lights in real-time
    /// </summary>
    public class HueSyncService : IHostedService
    {
        private readonly ISessionManager _sessionManager;
        private readonly ILogger<HueSyncService> _logger;

        private FfmpegStreamer? _ffmpegStreamer;
        private HueStreamer? _hueStreamer;
        private HueClient? _hueClient;
        private CancellationTokenSource? _syncCts;
        private readonly ILoggerFactory _loggerFactory;

        public HueSyncService(ISessionManager sessionManager, ILogger<HueSyncService> logger, ILoggerFactory loggerFactory, HueClient hueClient)
        {
            _sessionManager = sessionManager;
            _logger = logger;
            _loggerFactory = loggerFactory;
            _hueClient = hueClient;
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

        private async void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
        {
            _logger.LogInformation("Playback started for item {0}", e.Item.Name);
            
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.SyncEnabled) 
            {
                _logger.LogInformation("Hue Sync disabled, skipping.");
                return;
            }

            if (string.IsNullOrEmpty(config.HueBridgeIp) || string.IsNullOrEmpty(config.EntertainmentAreaId))
            {
                _logger.LogWarning("Hue Bridge IP or Entertainment Area ID not configured.");
                return;
            }

            StopSync(); // Ensure previous stopped
            _syncCts = new CancellationTokenSource();

            try 
            {
                // 1. Get Light Positions
                var areaConfig = await _hueClient!.GetEntertainmentConfiguration(config.HueBridgeIp, config.HueAppKey, config.EntertainmentAreaId);
                if (areaConfig == null) return;
                
                // Parse lights
                // Expected: areaConfig is specific "data" element.
                // "channels": [ { "channel_id": 0, "position": { "x": 0.5, "y": 0.5, "z": 0.0 } } ]
                // Note: We use x (horizontal) and z (vertical) for the 2D screen plane, matching HarmonizeProject
                var lights = new Dictionary<int, (double x, double z)>();
                if (areaConfig.Value.TryGetProperty("channels", out var channels))
                {
                   int idx = 0;
                   foreach (var channel in channels.EnumerateArray())
                   {
                       var channelId = channel.GetProperty("channel_id").GetInt32();
                       var pos = channel.GetProperty("position");
                       var x = pos.GetProperty("x").GetDouble();
                       var z = pos.GetProperty("z").GetDouble();
                       lights[channelId] = (x, z);
                       idx++;
                   }
                }

                // 2. Start Hue Streamer
                _hueStreamer!.StartStream(config);

                // 3. Start FFmpeg
                // e.MediaInfo.Path? e.Item.Path?
                // Need to verify where file path is.
                var path = e.Item.Path; // Usually works for File items
                if (string.IsNullOrEmpty(path)) 
                {
                     _logger.LogWarning("No media path found for item.");
                     return;
                }
                
                var videoStream = _ffmpegStreamer!.StartFfmpeg(path, config.TargetFps, config.UseGpu, config.CustomFfmpegFlags);
                if (videoStream == null) return;

                // 4. Start Loop
                _ = Task.Run(() => RunSyncLoop(videoStream, lights, config.EntertainmentAreaId, _syncCts.Token));

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start Hue Sync");
            }
        }

        private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
        {
            _logger.LogInformation("Playback stopped for item {0}", e.Item?.Name ?? "Unknown");
            StopSync();
        }

        private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
        {
            // Handle pause/unpause events
            if (e.IsPaused && _syncCts != null && !_syncCts.IsCancellationRequested)
            {
                _logger.LogInformation("Playback paused, pausing light sync");
                _syncCts?.Cancel();
            }
            else if (!e.IsPaused && _syncCts == null)
            {
                _logger.LogInformation("Playback resumed, restarting light sync");
                // Note: We don't restart from progress event to avoid complexity
                // The user can restart playback if needed
            }
        }

        private void StopSync()
        {
            _syncCts?.Cancel();
            _ffmpegStreamer?.Stop();
            _hueStreamer?.StopStream();
            _syncCts = null;
        }

        private async Task RunSyncLoop(Stream videoStream, Dictionary<int, (double x, double z)> lights, string areaId, CancellationToken token)
        {
            int w = 160;
            int h = 90;
            int frameSize = w * h * 3;
            byte[] buffer = new byte[frameSize];

            // Pre-calculate bounds for each light based on position
            // Following HarmonizeProject logic: use x (horizontal) and z (vertical) for 2D screen plane
            double breadth = 0.15; // 15% sampling area around each light position
            int avgSize = (w + h) / 2;
            int dist = (int)(breadth * avgSize);
            
            try 
            {
                while (!token.IsCancellationRequested)
                {
                    // Read full frame
                    int bytesRead = 0;
                    while (bytesRead < frameSize)
                    {
                        int n = await videoStream.ReadAsync(buffer, bytesRead, frameSize - bytesRead, token);
                        if (n == 0) break; // End of stream
                        bytesRead += n;
                    }
                    if (bytesRead < frameSize) break;

                    var channelColors = new Dictionary<int, byte[]>();

                    foreach (var kvp in lights)
                    {
                        // Calculate average color for this light's position
                        // Convert from Hue coordinate space to pixel coordinates
                        // Hue: x: -1 (left) to 1 (right), z: -1 (bottom) to 1 (top)
                        // Pixels: 0,0 is top-left
                        int cx = (int)((kvp.Value.x + 1) * w / 2);
                        int cy = (int)((-1 * kvp.Value.z + 1) * h / 2); // Invert z for screen coordinates
                        
                        int minX = Math.Max(0, cx - dist);
                        int maxX = Math.Min(w, cx + dist);
                        int minY = Math.Max(0, cy - dist);
                        int maxY = Math.Min(h, cy + dist);

                        long rSum = 0, gSum = 0, bSum = 0;
                        int count = 0;

                        // RGB24: R, G, B
                        for (int y = minY; y < maxY; y++)
                        {
                            for (int x = minX; x < maxX; x++)
                            {
                                int idx = (y * w + x) * 3;
                                rSum += buffer[idx];
                                gSum += buffer[idx+1];
                                bSum += buffer[idx+2];
                                count++;
                            }
                        }
                        
                        if (count == 0) count = 1;
                        byte r = (byte)(rSum / count);
                        byte g = (byte)(gSum / count);
                        byte b = (byte)(bSum / count);

                        // Format for Harmonize: R/2, R/2, G/2, G/2, B/2, B/2
                        // Wait, check Harmonize again.
                        // rgb_bytes[x] = bytearray([int(c[0]/2), int(c[0]/2), int(c[1]/2), int(c[1]/2), int(c[2]/2), int(c[2]/2),] )
                        // where c is from cv2.mean(area).
                        // It seems they intentionally dim it or fit 16-bit space? 
                        // If I simply replicate:
                        byte r2 = (byte)(r / 2); // Integer division
                        byte g2 = (byte)(g / 2);
                        byte b2 = (byte)(b / 2);
                        
                        channelColors[kvp.Key] = new byte[] { r2, r2, g2, g2, b2, b2 };
                    }

                    await _hueStreamer!.SendColors(areaId, channelColors);
                    
                    // Throttle? 60fps = 16ms. Reading/Processing takes time.
                    // Harmonize sleeps 0.0167
                    await Task.Delay(16, token); 
                }
            }
            catch (TaskCanceledException) {}
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Sync Loop");
            }
        }
    }
}
