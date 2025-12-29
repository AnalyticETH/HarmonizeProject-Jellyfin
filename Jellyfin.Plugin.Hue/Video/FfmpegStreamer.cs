using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Hue.Video
{
    /// <summary>
    /// Manages FFmpeg process for extracting and scaling video frames
    /// </summary>
    public class FfmpegStreamer
    {
        private readonly ILogger<FfmpegStreamer> _logger;
        private Process? _ffmpegProcess;

        public FfmpegStreamer(ILogger<FfmpegStreamer> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Starts FFmpeg process to extract RGB24 frames from a video file
        /// </summary>
        /// <param name="videoPath">Path to the video file</param>
        /// <param name="fps">Target frames per second for extraction (default: 20)</param>
        /// <param name="useGpu">Enable GPU acceleration if available (default: true)</param>
        /// <param name="customFlags">Additional FFmpeg flags to append</param>
        /// <param name="ffmpegPath">Path to ffmpeg executable (default: "ffmpeg")</param>
        /// <returns>Stream of raw RGB24 frames, or null if failed</returns>
        public Stream? StartFfmpeg(string videoPath, int fps = 20, bool useGpu = true, string customFlags = "", string ffmpegPath = "ffmpeg")
        {
            if (string.IsNullOrWhiteSpace(videoPath))
            {
                _logger.LogError("Video path is null or empty");
                return null;
            }

            if (!System.IO.File.Exists(videoPath))
            {
                _logger.LogError("Video file not found: {0}", videoPath);
                return null;
            }

            // -vf scale=160:90 -f rawvideo -pix_fmt rgb24
            // Add -r {fps} and custom flags
            var flagParts = new System.Collections.Generic.List<string>();
            if (useGpu)
            {
                flagParts.Add("-hwaccel auto");
            }

            if (!string.IsNullOrWhiteSpace(customFlags))
            {
                flagParts.Add(customFlags.Trim());
            }

            var flags = flagParts.Count > 0 ? string.Join(" ", flagParts) + " " : string.Empty;
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"{flags}-re -i \"{videoPath}\" -vf scale=160:90 -r {fps} -f rawvideo -pix_fmt rgb24 pipe:1",
                RedirectStandardOutput = true,
                RedirectStandardError = true, // Capture errors without cluttering stdout
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _logger.LogInformation("Starting FFmpeg: {0} {1}", startInfo.FileName, startInfo.Arguments);

            try
            {
                _ffmpegProcess = new Process { StartInfo = startInfo };
                _ffmpegProcess.Start();

                // Log stderr asynchronously to help with debugging
                _ = Task.Run(() =>
                {
                    try
                    {
                        using var reader = _ffmpegProcess.StandardError;
                        while (!reader.EndOfStream)
                        {
                            var line = reader.ReadLine();
                            if (!string.IsNullOrEmpty(line))
                            {
                                _logger.LogDebug("FFmpeg: {0}", line);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error reading FFmpeg stderr");
                    }
                });

                return _ffmpegProcess.StandardOutput.BaseStream;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start FFmpeg process. Ensure ffmpeg is installed and in PATH.");
                return null;
            }
        }

        public void Stop()
        {
            try
            {
                if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
                {
                    _ffmpegProcess.Kill();
                    _ffmpegProcess.WaitForExit(1000); // Wait up to 1 second
                    _logger.LogInformation("FFmpeg process stopped");
                }
                _ffmpegProcess?.Dispose();
                _ffmpegProcess = null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping FFmpeg process");
            }
        }
    }
}
