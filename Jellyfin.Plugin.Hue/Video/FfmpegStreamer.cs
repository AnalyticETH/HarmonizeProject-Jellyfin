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
        // Start/stop are synchronous API calls, so serialize the complete process
        // lifecycle.  In particular, a replacement capture must not publish its
        // process while an earlier Stop() is still disposing redirected streams.
        private readonly object _lifecycleLock = new();
        private Process? _ffmpegProcess;
        private Stream? _outputStream;
        private DateTime _lastFrameTime;
        private DateTime _startTime;          // When StartFfmpeg was called
        private long _framesProcessed = 0;
        private CancellationTokenSource? _monitorCts;
        private Task? _stderrReaderTask;
        private Task? _healthMonitorTask;
        private readonly object _stateLock = new object();

        // FFmpeg typically takes 1-3 seconds to start producing frames (codec init, seek, etc.).
        // During this startup window IsHealthy() must not falsely report unhealthy.
        private const int StartupGracePeriodSeconds = 8;
        private const int DefaultStallTimeoutSeconds = 5;
        private const int MinStallTimeoutSeconds = 1;
        private const int MaxStallTimeoutSeconds = 60;
        private const int MaxCustomFlagTextLength = 768;
        internal const int MaximumStandardErrorLineChars = 8 * 1024;
        private const int StandardErrorReadBufferSize = 4096;
        private static readonly TimeSpan ProcessCleanupTimeout = TimeSpan.FromSeconds(1);
        private static readonly HashSet<string> SafeCustomFlags = new(StringComparer.OrdinalIgnoreCase)
        {
            "-c:v",
            "-filter_threads",
            "-hwaccel",
            "-hwaccel_device",
            "-hwaccel_output_format",
            "-threads"
        };

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

        internal static TimeSpan GetHealthMonitorInterval(int stallTimeoutSeconds)
        {
            var normalizedTimeout = NormalizeStallTimeout(stallTimeoutSeconds);
            return TimeSpan.FromSeconds(Math.Min(10, normalizedTimeout));
        }

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
        /// Parses and validates administrator FFmpeg overrides against the deliberately
        /// small execution-safe option set. Custom flags may tune decoder, threading,
        /// and hardware acceleration behavior, but cannot add inputs,
        /// outputs, protocols, filters, scripts, or arbitrary file/network access.
        /// </summary>
        internal static IReadOnlyList<string> ParseSafeCustomArguments(string? customFlags)
        {
            if (customFlags != null && customFlags.Length > MaxCustomFlagTextLength)
            {
                throw new FormatException("FFmpeg custom flags are too long; keep the option text to 768 characters or fewer.");
            }

            var arguments = ParseCustomArguments(customFlags);
            if (arguments.Count > 12)
            {
                throw new FormatException("FFmpeg custom flags contain too many tokens; at most six option/value pairs are supported.");
            }

            var seenFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < arguments.Count; index++)
            {
                var flag = arguments[index];
                if (!SafeCustomFlags.Contains(flag))
                {
                    throw new FormatException($"FFmpeg custom flag '{flag}' is not allowed; only decoder, thread, and hardware options are supported.");
                }

                if (!seenFlags.Add(flag))
                {
                    throw new FormatException($"FFmpeg custom flag '{flag}' may only be specified once.");
                }

                if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    throw new FormatException($"FFmpeg custom flag '{flag}' requires a value.");
                }

                var value = arguments[++index];
                if (!IsSafeCustomFlagValue(flag, value))
                {
                    throw new FormatException($"FFmpeg custom flag '{flag}' has an unsafe value.");
                }
            }

            return arguments;
        }

        private static bool IsSafeCustomFlagValue(string flag, string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
                return false;

            if (string.Equals(flag, "-hwaccel_device", StringComparison.OrdinalIgnoreCase))
            {
                return IsSafeDeviceIdentifier(value) || IsSafeLinuxDeviceNode(value);
            }

            if (string.Equals(flag, "-threads", StringComparison.OrdinalIgnoreCase))
                return int.TryParse(value, out var threads) && threads >= 0 && threads <= 256;

            if (string.Equals(flag, "-filter_threads", StringComparison.OrdinalIgnoreCase))
                return int.TryParse(value, out var filterThreads) && filterThreads >= 1 && filterThreads <= 256;

            return IsSafeIdentifier(value, allowDash: false);
        }

        private static bool IsSafeDeviceIdentifier(string value)
        {
            return IsSafeIdentifier(value, allowDash: true);
        }

        private static bool IsSafeLinuxDeviceNode(string value)
        {
            const string prefix = "/dev/dri/";
            if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length == prefix.Length)
                return false;

            var node = value[prefix.Length..];
            if (!(node.StartsWith("renderD", StringComparison.Ordinal) || node.StartsWith("card", StringComparison.Ordinal)))
                return false;

            var digitStart = node.StartsWith("renderD", StringComparison.Ordinal) ? "renderD".Length : "card".Length;
            if (node.Length == digitStart || node.Length - digitStart > 3)
                return false;

            for (var index = digitStart; index < node.Length; index++)
            {
                if (node[index] < '0' || node[index] > '9')
                    return false;
            }

            return true;
        }

        private static bool IsSafeIdentifier(string value, bool allowDash)
        {
            if (value.Length == 0 || value.Length > 64)
                return false;

            foreach (var character in value)
            {
                var isAsciiLetter = (character >= 'A' && character <= 'Z') || (character >= 'a' && character <= 'z');
                var isAsciiDigit = character >= '0' && character <= '9';
                var isSafePunctuation = character == '_' ||
                    (allowDash && (character == '.' || character == '-'));
                if (!(isAsciiLetter || isAsciiDigit || isSafePunctuation))
                    return false;
            }

            return true;
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

            arguments.AddRange(ParseSafeCustomArguments(customFlags));
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

            arguments.AddRange(ParseSafeCustomArguments(customFlags));
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
            catch (ObjectDisposedException)
            {
                // The process may be disposed concurrently by Stop().
                return false;
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

            lock (_lifecycleLock)
            {
                // A streamer owns one FFmpeg process. Stop any previous process before
                // validating/replacing the command so an invalid new request cannot leave
                // an older capture running unexpectedly.
                StopCore();

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

            lock (_lifecycleLock)
            {
                // Stop an earlier video/audio capture before parsing a replacement command;
                // malformed custom flags must not leave the previous process alive.
                StopCore();

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
                catch (ArgumentOutOfRangeException ex)
                {
                    _logger.LogError(ex, "Invalid FFmpeg audio output parameters; refusing to start the audio process.");
                    return null;
                }

                return StartProcess(arguments, audioPath, ffmpegPath);
            }
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

            Process? process = null;
            Stream? outputStream = null;
            CancellationTokenSource? monitorCts = null;
            try
            {
                process = new Process { StartInfo = startInfo };
                if (!process.Start())
                    throw new InvalidOperationException("FFmpeg did not start.");

                outputStream = process.StandardOutput.BaseStream;
                monitorCts = new CancellationTokenSource();
                var monitorToken = monitorCts.Token;

                lock (_stateLock)
                {
                    _ffmpegProcess = process;
                    _lastFrameTime = DateTime.UtcNow;
                    _startTime = DateTime.UtcNow;
                    Interlocked.Exchange(ref _framesProcessed, 0);
                }
                _outputStream = outputStream;
                _monitorCts = monitorCts;

                // Capture a local reference so the background tasks do not race with
                // Stop() detaching the current process.  Do not pass the monitor token
                // to Task.Run: if cancellation wins before a worker is scheduled, the
                // task still needs to run and be retained/observed by Stop().
                var capturedProcess = process;

                // Log stderr asynchronously to help with debugging
                _stderrReaderTask = Task.Run(
                    () => DrainStandardError(capturedProcess, monitorToken),
                    CancellationToken.None);

                // Monitor process health — uses capturedProcess to avoid the race where
                // Stop() sets _ffmpegProcess = null while this task is still running.
                _healthMonitorTask = Task.Run(
                    () => MonitorProcessHealthAsync(capturedProcess, monitorToken),
                    CancellationToken.None);

                return outputStream;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start FFmpeg process. Ensure ffmpeg is installed and in PATH.");
                if (process != null && IsCurrentProcess(process))
                {
                    StopCore();
                }
                else
                {
                    CleanupProcess(process, outputStream, monitorCts, null, null);
                }

                return null;
            }
        }

        public void Stop()
        {
            lock (_lifecycleLock)
            {
                try
                {
                    StopCore();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error stopping FFmpeg process");
                }
            }
        }

        /// <summary>
        /// Detaches and tears down the current process. The caller must hold
        /// <see cref="_lifecycleLock"/> so concurrent starts and stops cannot interleave.
        /// </summary>
        private void StopCore()
        {
            Process? process;
            Stream? outputStream;
            CancellationTokenSource? monitorCts;
            Task? stderrReaderTask;
            Task? healthMonitorTask;

            lock (_stateLock)
            {
                process = _ffmpegProcess;
                _ffmpegProcess = null;
            }

            outputStream = _outputStream;
            _outputStream = null;
            monitorCts = _monitorCts;
            _monitorCts = null;
            stderrReaderTask = _stderrReaderTask;
            _stderrReaderTask = null;
            healthMonitorTask = _healthMonitorTask;
            _healthMonitorTask = null;

            CleanupProcess(process, outputStream, monitorCts, stderrReaderTask, healthMonitorTask);
        }

        private bool IsCurrentProcess(Process process)
        {
            lock (_stateLock)
            {
                return ReferenceEquals(_ffmpegProcess, process);
            }
        }

        private void CleanupProcess(
            Process? process,
            Stream? outputStream,
            CancellationTokenSource? monitorCts,
            Task? stderrReaderTask,
            Task? healthMonitorTask)
        {
            var cleanupDeadline = Stopwatch.GetTimestamp() +
                (long)(ProcessCleanupTimeout.TotalSeconds * Stopwatch.Frequency);

            try
            {
                monitorCts?.Cancel();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not cancel the FFmpeg monitor token during cleanup");
            }

            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (ObjectDisposedException)
                {
                    // Another cleanup path may have already disposed the process.
                }
                catch (InvalidOperationException)
                {
                    // The process may have exited between HasExited and Kill().
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not terminate the FFmpeg process tree");
                }
            }

            // Closing the pipes is required even after Kill(): a child that inherited
            // the descriptors can otherwise keep the retained stderr task blocked.
            CloseProcessStreams(process, outputStream);

            if (process != null)
            {
                try
                {
                    var remaining = GetRemainingCleanupTime(cleanupDeadline);
                    if (remaining > TimeSpan.Zero && !process.HasExited &&
                        !process.WaitForExit(ToTimeoutMilliseconds(remaining)))
                    {
                        _logger.LogWarning("FFmpeg process did not exit within the cleanup deadline after Kill()");
                    }
                }
                catch (ObjectDisposedException)
                {
                    // The process may have already been disposed by an earlier cleanup.
                }
                catch (InvalidOperationException)
                {
                    // The process may have won the exit/dispose race.
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not wait for the FFmpeg process during cleanup");
                }
            }

            var backgroundTasks = new List<Task>(2);
            if (stderrReaderTask != null)
                backgroundTasks.Add(stderrReaderTask);
            if (healthMonitorTask != null)
                backgroundTasks.Add(healthMonitorTask);

            var allBackgroundTasks = Task.WhenAll(backgroundTasks);
            ObserveTaskFailure(allBackgroundTasks);
            foreach (var backgroundTask in backgroundTasks)
                ObserveTaskFailure(backgroundTask);

            try
            {
                var remaining = GetRemainingCleanupTime(cleanupDeadline);
                if (remaining > TimeSpan.Zero && !allBackgroundTasks.Wait(remaining))
                {
                    _logger.LogWarning("FFmpeg background cleanup tasks did not finish within the cleanup deadline");
                }
            }
            catch (AggregateException ex)
            {
                _logger.LogDebug(ex.GetBaseException(), "An FFmpeg background cleanup task ended with an exception");
            }
            catch (ObjectDisposedException)
            {
                // A task's process/stream dependency may have been disposed as part
                // of cleanup; the task continuation still observes its result.
            }

            DisposeProcess(process);
            if (monitorCts != null)
            {
                if (allBackgroundTasks.IsCompleted)
                {
                    monitorCts.Dispose();
                }
                else
                {
                    // Stop() is deliberately bounded. If a platform keeps a
                    // redirected descriptor alive past the deadline, finish disposing
                    // the CTS when the retained tasks eventually settle.
                    _ = allBackgroundTasks.ContinueWith(
                        completedTask =>
                        {
                            _ = completedTask.Exception;
                            monitorCts.Dispose();
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }

            if (process != null)
                _logger.LogInformation("FFmpeg process stopped");
        }

        private void DrainStandardError(Process process, CancellationToken cancellationToken)
        {
            try
            {
                using var reader = process.StandardError;
                var buffer = new char[StandardErrorReadBufferSize];
                var line = new StringBuilder(StandardErrorReadBufferSize);
                var lineWasTruncated = false;
                var skipLineFeed = false;

                while (!cancellationToken.IsCancellationRequested)
                {
                    var read = reader.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        if (!lineWasTruncated)
                            LogStandardErrorLine(line);
                        break;
                    }

                    for (var index = 0; index < read; index++)
                    {
                        var character = buffer[index];
                        if (skipLineFeed)
                        {
                            skipLineFeed = false;
                            if (character == '\n')
                                continue;
                        }

                        if (character == '\r' || character == '\n')
                        {
                            if (!lineWasTruncated)
                                LogStandardErrorLine(line);

                            line.Clear();
                            lineWasTruncated = false;
                            skipLineFeed = character == '\r';
                            continue;
                        }

                        if (lineWasTruncated)
                            continue;

                        if (line.Length < MaximumStandardErrorLineChars)
                        {
                            line.Append(character);
                            continue;
                        }

                        LogStandardErrorLine(line, truncated: true);
                        line.Clear();
                        lineWasTruncated = true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (InvalidOperationException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading FFmpeg stderr");
            }
        }

        private void LogStandardErrorLine(StringBuilder line, bool truncated = false)
        {
            if (line.Length == 0)
                return;

            _logger.LogDebug(
                truncated ? "FFmpeg: {0} [truncated]" : "FFmpeg: {0}",
                line.ToString());
        }

        private async Task MonitorProcessHealthAsync(Process process, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && !process.HasExited)
                {
                    var stallTimeoutSeconds = NormalizeStallTimeout(StallTimeoutSeconds);
                    await Task.Delay(GetHealthMonitorInterval(stallTimeoutSeconds), cancellationToken).ConfigureAwait(false);
                    DateTime startTime;
                    DateTime lastFrameTime;
                    lock (_stateLock)
                    {
                        startTime = _startTime;
                        lastFrameTime = _lastFrameTime;
                    }

                    if (!cancellationToken.IsCancellationRequested &&
                        !IsHealthy(process, startTime, lastFrameTime, stallTimeoutSeconds))
                    {
                        _logger.LogWarning(
                            "FFmpeg appears stalled — no media samples in {0}+ seconds. Processed {1} samples total.",
                            stallTimeoutSeconds,
                            FramesProcessed);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
                // Stop() may dispose the process after its bounded wait.
            }
            catch (InvalidOperationException)
            {
                // Stop() may dispose the process after its bounded wait.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FFmpeg health monitor failed");
            }
        }

        private static TimeSpan GetRemainingCleanupTime(long cleanupDeadline)
        {
            var remainingTicks = cleanupDeadline - Stopwatch.GetTimestamp();
            return remainingTicks <= 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency);
        }

        private static int ToTimeoutMilliseconds(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
                return 0;

            return (int)Math.Min(int.MaxValue, Math.Max(1, timeout.TotalMilliseconds));
        }

        private void CloseProcessStreams(Process? process, Stream? outputStream)
        {
            try
            {
                outputStream?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not close the FFmpeg output stream");
            }

            if (process == null)
                return;

            try
            {
                process.StandardOutput.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not close the FFmpeg standard output stream");
            }

            try
            {
                process.StandardError.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not close the FFmpeg standard error stream");
            }
        }

        private void DisposeProcess(Process? process)
        {
            if (process == null)
                return;

            try
            {
                process.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not dispose the FFmpeg process handle");
            }
        }

        private static void ObserveTaskFailure(Task task)
        {
            _ = task.ContinueWith(
                completedTask => _ = completedTask.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
