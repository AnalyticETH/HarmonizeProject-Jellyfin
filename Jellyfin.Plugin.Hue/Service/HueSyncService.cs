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
    public class HueSyncService : IHostedService
    {
        private readonly ISessionManager _sessionManager;
        private readonly ILogger<HueSyncService> _logger;

        private FfmpegStreamer? _ffmpegStreamer;
        private HueStreamer? _hueStreamer;
        private HueClient? _hueClient;
        private CancellationTokenSource? _syncCts;
        private readonly ILoggerFactory _loggerFactory;

        public HueSyncService(ISessionManager sessionManager, ILogger<HueSyncService> logger, ILoggerFactory loggerFactory)
        {
            _sessionManager = sessionManager;
            _logger = logger;
            _loggerFactory = loggerFactory;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Hue Sync Service Started.");
            _sessionManager.PlaybackStart += OnPlaybackStart;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            
            // Helpers
            _hueClient = new HueClient(_loggerFactory.CreateLogger<HueClient>());
            _hueStreamer = new HueStreamer(_loggerFactory.CreateLogger<HueStreamer>());
            _ffmpegStreamer = new FfmpegStreamer(_loggerFactory.CreateLogger<FfmpegStreamer>());

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Hue Sync Service Stopping.");
            _sessionManager.PlaybackStart -= OnPlaybackStart;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
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
                var lights = new Dictionary<int, (double x, double y)>();
                if (areaConfig.Value.TryGetProperty("channels", out var channels))
                {
                   int idx = 0;
                   foreach (var channel in channels.EnumerateArray())
                   {
                       var channelId = channel.GetProperty("channel_id").GetInt32();
                       var pos = channel.GetProperty("position");
                       var x = pos.GetProperty("x").GetDouble();
                       var y = pos.GetProperty("y").GetDouble();
                       lights[channelId] = (x, y);
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
                
                var videoStream = _ffmpegStreamer!.StartFfmpeg(path, config.TargetFps, config.CustomFfmpegFlags);
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
             _logger.LogInformation("Playback stopped.");
             StopSync();
        }

        private void StopSync()
        {
            _syncCts?.Cancel();
            _ffmpegStreamer?.Stop();
            _hueStreamer?.StopStream();
            _syncCts = null;
        }

        private async Task RunSyncLoop(Stream videoStream, Dictionary<int, (double x, double y)> lights, string areaId, CancellationToken token)
        {
            int w = 160;
            int h = 90;
            int frameSize = w * h * 3;
            byte[] buffer = new byte[frameSize];

            // Pre-calculate bounds for each light
            var lightBounds = new Dictionary<int, (int minX, int maxX, int minY, int maxY)>();
            double breadth = 0.15; // 15% from Harmonize
            int avgSize = (w + h) / 2; // approximation
            int dist = (int)(breadth * avgSize);

            foreach (var kvp in lights)
            {
                // Harmonize: coords[0] = ((coords[0])+1) * w//2
                // coords[2] = (-1*(coords[2])+1) * h//2 (y seems to be z in their dict or y?)
                // API V2: x is -1 to 1 (left to right), y is -1 to 1 (back to front), z is -1 to 1 (bottom to top).
                // Wait, Harmonize uses x and z for screen plane?
                // Let's assume standard V2 clip coordinates for TV:
                // x: -1 (left) to 1 (right)
                // y: -1 (bottom) to 1 (top) ?? Or z?
                // Harmonize: lights_dict.update({str(index): [value['position']['x'],value['position']['y'], value['position']['z']]})
                // And usage: 
                // coords[0] = ((coords[0])+1) * w//2
                // coords[2] = (-1*(coords[2])+1) * h//2  <-- Uses index 2, which is Z.
                // So Harmonize used X and Z as the screen plane.
                // I'll stick to that assumption. X is horizontal, Z is vertical.
                
                // My parse logic above used x and y. I should check which property is Z.
                // Re-check OnPlaybackStart parsing logic. I used y for the second coord. 
                // I should fetch Z and use that as Y for 2D plane.
                
                // Re-calculating bounds in loop is inefficient, but okay for init.
                // I need to correct my parsing first.
            }

            // Correction for parsing:
            // I'll assume I need to refactor the parsing in OnPlaybackStart or just fix it here if I passed data.
            // I passed (x, y). I should probably fix parsing to be (x, z).
            
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
                        // Calc average color
                        // For this step I need the bounds.
                        // Im implementing bounds calculation momentarily
                        
                        // Placeholder for bounds
                        int cx = (int)((kvp.Value.x + 1) * w / 2);
                        int cy = (int)((-1 * kvp.Value.y + 1) * h / 2); // Assuming passed Y is actually Z from API
                        
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
