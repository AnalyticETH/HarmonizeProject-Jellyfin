using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Hue.Configuration;
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

        /// <summary>
        /// Splits the administrator's additional FFmpeg flags into process arguments
        /// without invoking a shell. Quotes group values containing spaces and a
        /// backslash only escapes a quote or another backslash, preserving Windows
        /// paths and FFmpeg filter expressions.
        /// </summary>
        internal static IReadOnlyList<string> ParseCustomArguments(string? customFlags)
        {
            var arguments = new List<string>();
            if (string.IsNullOrWhiteSpace(customFlags))
                return arguments;

            var current = new StringBuilder();
            var quote = '\0';
            var tokenStarted = false;

            void FlushToken()
            {
                if (!tokenStarted)
                    return;

                arguments.Add(current.ToString());
                current.Clear();
                tokenStarted = false;
            }

            for (var index = 0; index < customFlags.Length; index++)
            {
                var character = customFlags[index];
                if (quote != '\0')
                {
                    if (character == quote)
                    {
                        quote = '\0';
                        continue;
                    }

                    if (character == '\\' && index + 1 < customFlags.Length &&
                        (customFlags[index + 1] == quote || customFlags[index + 1] == '\\'))
                    {
                        current.Append(customFlags[++index]);
                        continue;
                    }

                    current.Append(character);
                    continue;
                }

                if (character == '\'' || character == '"')
                {
                    quote = character;
                    tokenStarted = true;
                    continue;
                }

                if (char.IsWhiteSpace(character))
                {
                    FlushToken();
                    continue;
                }

                if (character == '\\' && index + 1 < customFlags.Length &&
                    (customFlags[index + 1] == '\'' || customFlags[index + 1] == '"' ||
                     customFlags[index + 1] == '\\'))
                {
                    current.Append(customFlags[++index]);
                    tokenStarted = true;
                    continue;
                }

                current.Append(character);
                tokenStarted = true;
            }

            if (quote != '\0')
                throw new FormatException("FFmpeg custom flags contain an unterminated quote.");

            FlushToken();
            return arguments;
        }

        /// <summary>
        /// Builds the complete FFmpeg argument list using one argument per token so
        /// media paths and administrator flags cannot change process parsing.
        /// </summary>
        internal static IReadOnlyList<string> BuildFfmpegArguments(
            string videoPath,
            int fps,
            bool useGpu,
            string customFlags,
            double seekPositionSeconds,
            int frameWidth,
            int frameHeight,
            string scalingMode,
            string deinterlaceMode)
        {
            if (string.IsNullOrWhiteSpace(videoPath))
                throw new ArgumentException("A video path is required.", nameof(videoPath));

            var arguments = new List<string>();
            if (useGpu)
            {
                arguments.Add("-hwaccel");
                arguments.Add("auto");
            }

            arguments.AddRange(ParseCustomArguments(customFlags));
            if (seekPositionSeconds > 1.0)
            {
                arguments.Add("-ss");
                arguments.Add(seekPositionSeconds.ToString("F3", CultureInfo.InvariantCulture));
            }

            arguments.Add("-i");
            arguments.Add(videoPath);
            arguments.Add("-vf");
            arguments.Add(BuildVideoFilter(frameWidth, frameHeight, scalingMode, deinterlaceMode));
            arguments.Add("-r");
            arguments.Add(fps.ToString(CultureInfo.InvariantCulture));
            arguments.Add("-f");
            arguments.Add("rawvideo");
            arguments.Add("-pix_fmt");
            arguments.Add("rgb24");
            arguments.Add("pipe:1");
            return arguments;
        }

        /// <summary>
        /// Builds a safe FFmpeg command that emits a bounded-rate stereo PCM stream for
        /// audio-reactive Hue synchronization. The output is deliberately uncompressed
        /// signed 16-bit little-endian samples so the service can analyze it without a
        /// native audio dependency or shell interpolation.
        /// </summary>
        internal static IReadOnlyList<string> BuildAudioFfmpegArguments(
            string audioPath,
            bool useGpu,
            string customFlags,
            double seekPositionSeconds,
            int sampleRate = 8000,
            int channels = 2)
        {
            if (string.IsNullOrWhiteSpace(audioPath))
                throw new ArgumentException("An audio path is required.", nameof(audioPath));

            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate), "Audio sample rate must be positive.");

            if (channels <= 0)
                throw new ArgumentOutOfRangeException(nameof(channels), "Audio channel count must be positive.");

            var arguments = new List<string>();
            if (useGpu)
            {
                arguments.Add("-hwaccel");
                arguments.Add("auto");
            }

            arguments.AddRange(ParseCustomArguments(customFlags));
            if (seekPositionSeconds > 1.0)
            {
                arguments.Add("-ss");
                arguments.Add(seekPositionSeconds.ToString("F3", CultureInfo.InvariantCulture));
            }

            arguments.Add("-i");
            arguments.Add(audioPath);
            arguments.Add("-vn");
            arguments.Add("-ac");
            arguments.Add(channels.ToString(CultureInfo.InvariantCulture));
            arguments.Add("-ar");
            arguments.Add(sampleRate.ToString(CultureInfo.InvariantCulture));
            arguments.Add("-f");
            arguments.Add("s16le");
            arguments.Add("-acodec");
            arguments.Add("pcm_s16le");
            arguments.Add("pipe:1");
            return arguments;
        }

        internal static string BuildVideoFilter(int frameWidth, int frameHeight)
            => BuildVideoFilter(frameWidth, frameHeight, PluginConfiguration.VideoScalingModeStretch);

        internal static string BuildVideoFilter(int frameWidth, int frameHeight, string? scalingMode)
            => BuildVideoFilter(
                frameWidth,
                frameHeight,
                scalingMode,
                PluginConfiguration.VideoDeinterlaceModeOff);

        internal static string BuildVideoFilter(
            int frameWidth,
            int frameHeight,
            string? scalingMode,
            string? deinterlaceMode)
        {
            if (frameWidth <= 0 || frameHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(frameWidth), "FFmpeg output dimensions must be positive.");

            var scalingFilter = BuildScalingFilter(frameWidth, frameHeight, scalingMode);
            if (string.Equals(deinterlaceMode, PluginConfiguration.VideoDeinterlaceModeOn, StringComparison.OrdinalIgnoreCase))
                return $"yadif=mode=send_frame:deint=all,{scalingFilter}";

            if (string.Equals(deinterlaceMode, PluginConfiguration.VideoDeinterlaceModeAuto, StringComparison.OrdinalIgnoreCase))
                return $"yadif=mode=send_frame:deint=interlaced,{scalingFilter}";

            return scalingFilter;
        }

        private static string BuildScalingFilter(int frameWidth, int frameHeight, string? scalingMode)
        {
            if (string.Equals(scalingMode, PluginConfiguration.VideoScalingModeFit, StringComparison.OrdinalIgnoreCase))
            {
                return $"scale={frameWidth}:{frameHeight}:force_original_aspect_ratio=decrease," +
                       $"pad={frameWidth}:{frameHeight}:(ow-iw)/2:(oh-ih)/2";
            }

            if (string.Equals(scalingMode, PluginConfiguration.VideoScalingModeCrop, StringComparison.OrdinalIgnoreCase))
            {
                return $"scale={frameWidth}:{frameHeight}:force_original_aspect_ratio=increase," +
                       $"crop={frameWidth}:{frameHeight}:(in_w-out_w)/2:(in_h-out_h)/2";
            }

            return $"scale={frameWidth}:{frameHeight}";
        }

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
        /// <param name="frameWidth">Output frame width in pixels (default: 160)</param>
        /// <param name="frameHeight">Output frame height in pixels (default: 90)</param>
        /// <param name="scalingMode">Video fit mode: Stretch, Fit, or Crop (default: Stretch)</param>
        /// <param name="deinterlaceMode">Video deinterlacing: Off, Auto for flagged interlaced frames, or On (default: Off)</param>
        /// <returns>Stream of raw RGB24 frames, or null if failed</returns>
        public Stream? StartFfmpeg(
            string videoPath,
            int fps = 20,
            bool useGpu = true,
            string customFlags = "",
            string ffmpegPath = "ffmpeg",
            double seekPositionSeconds = 0,
            int frameWidth = 160,
            int frameHeight = 90,
            string scalingMode = PluginConfiguration.VideoScalingModeStretch,
            string deinterlaceMode = PluginConfiguration.VideoDeinterlaceModeOff)
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

            if (frameWidth <= 0 || frameHeight <= 0)
            {
                _logger.LogError("FFmpeg output dimensions must be positive: {0}x{1}", frameWidth, frameHeight);
                return null;
            }

            // A streamer owns one FFmpeg process. Stop any previous process before
            // validating/replacing the command so an invalid new request cannot leave
            // an older capture running unexpectedly.
            Stop();

            // NOTE: We deliberately omit -re here.
            // -re reads input at native frame rate which would throttle a 24fps source to only
            // 24 frames/sec even if targetFps is 20 — this causes the sync loop to block on reads.
            // Instead we let FFmpeg decode as fast as possible; the RunSyncLoop delay enforces timing.
            IReadOnlyList<string> arguments;
            try
            {
                arguments = BuildFfmpegArguments(
                    videoPath,
                    fps,
                    useGpu,
                    customFlags,
                    seekPositionSeconds,
                    frameWidth,
                    frameHeight,
                    scalingMode,
                    deinterlaceMode);
            }
            catch (FormatException ex)
            {
                _logger.LogError(ex, "Invalid FFmpeg custom flags; refusing to start the process.");
                return null;
            }

            return StartProcess(arguments, videoPath, ffmpegPath);
        }

        /// <summary>
        /// Starts FFmpeg in audio-reactive mode and returns its raw PCM output stream.
        /// </summary>
        public Stream? StartAudioFfmpeg(
            string audioPath,
            bool useGpu = true,
            string customFlags = "",
            string ffmpegPath = "ffmpeg",
            double seekPositionSeconds = 0,
            int sampleRate = 8000,
            int channels = 2)
        {
            if (string.IsNullOrWhiteSpace(audioPath))
            {
                _logger.LogError("Audio path is null or empty");
                return null;
            }

            if (!File.Exists(audioPath))
            {
                _logger.LogError("Audio file not found: {0}", audioPath);
                return null;
            }

            if (double.IsNaN(seekPositionSeconds) || double.IsInfinity(seekPositionSeconds) || seekPositionSeconds < 0)
            {
                _logger.LogWarning("Ignoring invalid FFmpeg audio seek position {0}", seekPositionSeconds);
                seekPositionSeconds = 0;
            }

            // Stop an earlier video/audio capture before parsing a replacement command;
            // malformed custom flags must not leave the previous process alive.
            Stop();

            IReadOnlyList<string> arguments;
            try
            {
                arguments = BuildAudioFfmpegArguments(
                    audioPath,
                    useGpu,
                    customFlags,
                    seekPositionSeconds,
                    sampleRate,
                    channels);
            }
            catch (FormatException ex)
            {
                _logger.LogError(ex, "Invalid FFmpeg custom flags; refusing to start the audio process.");
                return null;
            }

            return StartProcess(arguments, audioPath, ffmpegPath);
        }

        private Stream? StartProcess(
            IReadOnlyList<string> arguments,
            string mediaPath,
            string ffmpegPath)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            _logger.LogInformation("Starting FFmpeg process for {0}", mediaPath);

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
                                _logger.LogDebug("FFmpeg: {0}", line);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error reading FFmpeg stderr");
                    }
                }, monitorToken);

                // Monitor process health — uses capturedProcess to avoid the race where
                // Stop() sets _ffmpegProcess = null while this task is still running.
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
                                    "FFmpeg appears stalled — no media samples in {0}+ seconds. Processed {1} samples total.",
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
