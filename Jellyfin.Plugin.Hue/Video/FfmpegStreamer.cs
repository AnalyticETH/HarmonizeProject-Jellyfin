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
        private DateTime _lastFrameTime;
        private DateTime _startTime;          // When StartFfmpeg was called
        private long _framesProcessed = 0;
        private CancellationTokenSource? _monitorCts;
        private readonly object _stateLock = new object();

        // FFmpeg typically takes 1-3 seconds to start producing frames (codec init, seek, etc.).
        // During this startup window IsHealthy() must not falsely report unhealthy.
        private const int StartupGracePeriodSeconds = 8;
        private const int DefaultStallTimeoutSeconds = 5;
        private const int MinStallTimeoutSeconds = 1;
        private const int MaxStallTimeoutSeconds = 60;

        public FfmpegStreamer(ILogger<FfmpegStreamer> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Maximum time without a frame before health monitoring reports a stall.
        /// The sync service sets this from plugin configuration for each playback.
        /// </summary>
        public int StallTimeoutSeconds { get; set; } = DefaultStallTimeoutSeconds;

        /// <summary>
        /// Checks if FFmpeg process is healthy and running
        /// </summary>
        public bool IsHealthy()
        {
            return IsHealthy(StallTimeoutSeconds);
        }

        /// <summary>
        /// Checks whether FFmpeg is healthy using the configured no-frame timeout.
        /// </summary>
        public bool IsHealthy(int stallTimeoutSeconds)
        {
            Process? process;
            DateTime startTime;
            DateTime lastFrameTime;
            lock (_stateLock)
            {
                process = _ffmpegProcess;
                startTime = _startTime;
                lastFrameTime = _lastFrameTime;
            }

            return IsHealthy(process, startTime, lastFrameTime, NormalizeStallTimeout(stallTimeoutSeconds));
        }

        /// <summary>
        /// Gets the maximum time the sync loop should wait for the current frame.
        /// FFmpeg gets a longer initial grace period for codec initialization; once a
        /// frame has been observed, the administrator's stall timeout is used.
        /// </summary>
        public TimeSpan GetFrameReadTimeout(int stallTimeoutSeconds)
        {
            var normalizedTimeout = NormalizeStallTimeout(stallTimeoutSeconds);
            lock (_stateLock)
            {
                if (_framesProcessed == 0 &&
                    _ffmpegProcess != null &&
                    DateTime.UtcNow - _startTime < TimeSpan.FromSeconds(StartupGracePeriodSeconds))
                {
                    return TimeSpan.FromSeconds(StartupGracePeriodSeconds);
                }
            }

            return TimeSpan.FromSeconds(normalizedTimeout);
        }

        private static int NormalizeStallTimeout(int stallTimeoutSeconds) =>
            Math.Clamp(stallTimeoutSeconds, MinStallTimeoutSeconds, MaxStallTimeoutSeconds);

        private static bool IsHealthy(Process? process, DateTime startTime, DateTime lastFrameTime, int stallTimeoutSeconds)
        {
            if (process == null)
                return false;

            try
            {
                if (process.HasExited)
                    return false;

                // During the startup grace period, trust that FFmpeg is starting up normally
                if ((DateTime.UtcNow - startTime).TotalSeconds < StartupGracePeriodSeconds)
                    return true;

                // After the grace period, require frames to have been received within the
                // administrator-configured timeout.
                var timeSinceLastFrame = DateTime.UtcNow - lastFrameTime;
                return timeSinceLastFrame.TotalSeconds < stallTimeoutSeconds;
            }
            catch (InvalidOperationException)
            {
                // The process may be disposed concurrently by Stop().
                return false;
            }
        }

        /// <summary>
        /// Gets the number of frames processed
        /// </summary>
        public long FramesProcessed => Interlocked.Read(ref _framesProcessed);

        /// <summary>
        /// Marks that a frame was just read
        /// </summary>
        public void MarkFrameRead()
        {
            lock (_stateLock)
            {
                _lastFrameTime = DateTime.UtcNow;
                Interlocked.Increment(ref _framesProcessed);
            }
        }

        /// <summary>
        /// Starts FFmpeg process to extract RGB24 frames from a video file
        /// </summary>
        /// <param name="videoPath">Path to the video file</param>
        /// <param name="fps">Target frames per second for extraction (default: 20)</param>
        /// <param name="useGpu">Enable GPU acceleration if available (default: true)</param>
        /// <param name="customFlags">Additional FFmpeg flags to append</param>
        /// <param name="ffmpegPath">Path to ffmpeg executable (default: "ffmpeg")</param>
        /// <param name="seekPositionSeconds">Seek to this position before extracting (default: 0 = start)</param>
        /// <returns>Stream of raw RGB24 frames, or null if failed</returns>
        public Stream? StartFfmpeg(string videoPath, int fps = 20, bool useGpu = true, string customFlags = "", string ffmpegPath = "ffmpeg", double seekPositionSeconds = 0)
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

            if (double.IsNaN(seekPositionSeconds) || double.IsInfinity(seekPositionSeconds) || seekPositionSeconds < 0)
            {
                _logger.LogWarning("Ignoring invalid FFmpeg seek position {0}", seekPositionSeconds);
                seekPositionSeconds = 0;
            }

            // A streamer owns one FFmpeg process. Stop any previous process before
            // replacing the field so repeated playback-start events cannot leak it.
            Stop();

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

            // Seek prefix: if seekPositionSeconds > 0 seek before the input for fast seeking
            var seekPrefix = seekPositionSeconds > 1.0 ? $"-ss {seekPositionSeconds:F3} " : string.Empty;

            // NOTE: We deliberately omit -re here.
            // -re reads input at native frame rate which would throttle a 24fps source to only
            // 24 frames/sec even if targetFps is 20 — this causes the sync loop to block on reads.
            // Instead we let FFmpeg decode as fast as possible; the RunSyncLoop delay enforces timing.
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"{flags}{seekPrefix}-i \"{videoPath}\" -vf scale=160:90 -r {fps} -f rawvideo -pix_fmt rgb24 pipe:1",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _logger.LogInformation("Starting FFmpeg: {0} {1}", startInfo.FileName, startInfo.Arguments);

            try
            {
                // Dispose any leftover CTS from a previous run (Stop() intentionally defers disposal)
                _monitorCts?.Dispose();

                _ffmpegProcess = new Process { StartInfo = startInfo };
                _ffmpegProcess.Start();
                lock (_stateLock)
                {
                    _lastFrameTime = DateTime.UtcNow;
                    _startTime = DateTime.UtcNow;
                    Interlocked.Exchange(ref _framesProcessed, 0);
                }
                _monitorCts = new CancellationTokenSource();
                var monitorToken = _monitorCts.Token;

                // Capture a local reference so the background tasks don't race with Stop() nulling the field
                var capturedProcess = _ffmpegProcess;

                // Log stderr asynchronously to help with debugging
                _ = Task.Run(() =>
                {
                    try
                    {
                        using var reader = capturedProcess.StandardError;
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
                }, monitorToken);

                // Monitor process health — uses capturedProcess to avoid the race where
                // Stop() sets _ffmpegProcess = null while this task is still running
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!capturedProcess.HasExited && !monitorToken.IsCancellationRequested)
                        {
                            await Task.Delay(10000, monitorToken).ConfigureAwait(false);
                            DateTime startTime;
                            DateTime lastFrameTime;
                            lock (_stateLock)
                            {
                                startTime = _startTime;
                                lastFrameTime = _lastFrameTime;
                            }

                            if (!monitorToken.IsCancellationRequested &&
                                !IsHealthy(capturedProcess, startTime, lastFrameTime, NormalizeStallTimeout(StallTimeoutSeconds)))
                            {
                                _logger.LogWarning(
                                    "FFmpeg appears stalled — no frames in {0}+ seconds. Processed {1} frames total.",
                                    NormalizeStallTimeout(StallTimeoutSeconds),
                                    FramesProcessed);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }, monitorToken);

                return capturedProcess.StandardOutput.BaseStream;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start FFmpeg process. Ensure ffmpeg is installed and in PATH.");
                // Clean up partially-started process to prevent leaks
                try
                { if (_ffmpegProcess != null && !_ffmpegProcess.HasExited) _ffmpegProcess.Kill(); }
                catch { }
                _ffmpegProcess?.Dispose();
                _ffmpegProcess = null;
                return null;
            }
        }

        public void Stop()
        {
            try
            {
                // Cancel the health monitor and stderr reader tasks.
                // Don't dispose immediately — background tasks may still be checking the token.
                // The CTS will be disposed on the next StartFfmpeg call or by GC.
                var oldCts = _monitorCts;
                _monitorCts = null;
                oldCts?.Cancel();

                if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
                {
                    _ffmpegProcess.Kill();
                    if (!_ffmpegProcess.WaitForExit(1000))
                    {
                        _logger.LogWarning("FFmpeg process did not exit within 1 second after Kill()");
                    }
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
