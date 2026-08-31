using System.Diagnostics;
using System.Reflection;
using Jellyfin.Plugin.Hue.Configuration;
using Jellyfin.Plugin.Hue.Video;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

public sealed class FfmpegStreamerTests
{
    [Theory]
    [InlineData(80, 45, "scale=80:45")]
    [InlineData(160, 90, "scale=160:90")]
    [InlineData(320, 180, "scale=320:180")]
    public void BuildVideoFilter_UsesConfiguredFrameDimensions(
        int frameWidth,
        int frameHeight,
        string expected)
    {
        Assert.Equal(expected, FfmpegStreamer.BuildVideoFilter(frameWidth, frameHeight));
    }

    [Theory]
    [InlineData("Stretch", "scale=160:90")]
    [InlineData("Fit", "scale=160:90:force_original_aspect_ratio=decrease,pad=160:90:(ow-iw)/2:(oh-ih)/2")]
    [InlineData("Crop", "scale=160:90:force_original_aspect_ratio=increase,crop=160:90:(in_w-out_w)/2:(in_h-out_h)/2")]
    [InlineData("fit", "scale=160:90:force_original_aspect_ratio=decrease,pad=160:90:(ow-iw)/2:(oh-ih)/2")]
    [InlineData("Unsupported", "scale=160:90")]
    public void BuildVideoFilter_UsesRequestedScalingMode(string scalingMode, string expected)
    {
        Assert.Equal(expected, FfmpegStreamer.BuildVideoFilter(160, 90, scalingMode));
    }

    [Theory]
    [InlineData("Off", "scale=160:90")]
    [InlineData("Auto", "yadif=mode=send_frame:deint=interlaced,scale=160:90")]
    [InlineData("On", "yadif=mode=send_frame:deint=all,scale=160:90")]
    [InlineData("auto", "yadif=mode=send_frame:deint=interlaced,scale=160:90")]
    [InlineData("Unsupported", "scale=160:90")]
    public void BuildVideoFilter_UsesRequestedDeinterlaceMode(string deinterlaceMode, string expected)
    {
        Assert.Equal(
            expected,
            FfmpegStreamer.BuildVideoFilter(
                160,
                90,
                PluginConfiguration.VideoScalingModeStretch,
                deinterlaceMode));
    }

    [Fact]
    public void BuildVideoFilter_ComposesDeinterlaceBeforeAspectPreservingScale()
    {
        Assert.Equal(
            "yadif=mode=send_frame:deint=interlaced,scale=160:90:force_original_aspect_ratio=decrease,pad=160:90:(ow-iw)/2:(oh-ih)/2",
            FfmpegStreamer.BuildVideoFilter(
                160,
                90,
                PluginConfiguration.VideoScalingModeFit,
                PluginConfiguration.VideoDeinterlaceModeAuto));
    }

    [Theory]
    [InlineData(0, 90)]
    [InlineData(160, 0)]
    public void BuildVideoFilter_RejectsNonPositiveDimensions(int frameWidth, int frameHeight)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FfmpegStreamer.BuildVideoFilter(frameWidth, frameHeight));
    }

    [Fact]
    public void ParseCustomArguments_PreservesQuotedValuesAndWindowsPaths()
    {
        var arguments = FfmpegStreamer.ParseCustomArguments(
            "-vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2\" -metadata \"title=Movie Night\" -map C:\\media\\input.mkv");

        Assert.Equal(
            new[]
            {
                "-vf",
                "scale=trunc(iw/2)*2:trunc(ih/2)*2",
                "-metadata",
                "title=Movie Night",
                "-map",
                "C:\\media\\input.mkv"
            },
            arguments);
    }

    [Fact]
    public void ParseCustomArguments_RejectsUnterminatedQuotes()
    {
        Assert.Throws<FormatException>(() => FfmpegStreamer.ParseCustomArguments("-vf \"scale=160:90"));
    }

    [Fact]
    public void ParseSafeCustomArguments_AllowsDecoderThreadAndHardwareTuning()
    {
        var arguments = FfmpegStreamer.ParseSafeCustomArguments(
            "-hwaccel vaapi -hwaccel_device /dev/dri/renderD128 -hwaccel_output_format vaapi -c:v h264_cuvid -threads 2 -filter_threads 1");

        Assert.Equal(
            new[]
            {
                "-hwaccel", "vaapi",
                "-hwaccel_device", "/dev/dri/renderD128",
                "-hwaccel_output_format", "vaapi",
                "-c:v", "h264_cuvid",
                "-threads", "2",
                "-filter_threads", "1"
            },
            arguments);
    }

    [Theory]
    [InlineData("-i /tmp/extra-input.mkv")]
    [InlineData("-filter_complex \"movie=/tmp/secret.txt\"")]
    [InlineData("-protocol_whitelist file,http,https")]
    [InlineData("-headers Authorization:Bearer-secret")]
    [InlineData("-threads=2")]
    [InlineData("-threads 2 output.mp4")]
    [InlineData("-threads 2 -threads 3")]
    public void ParseSafeCustomArguments_RejectsCapabilityExpandingFlags(string customFlags)
    {
        var exception = Assert.Throws<FormatException>(() => FfmpegStreamer.ParseSafeCustomArguments(customFlags));

        Assert.True(
            exception.Message.Contains("not allowed", StringComparison.Ordinal) ||
            exception.Message.Contains("may only be specified once", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildFfmpegArguments_RejectsUnsafeCustomFlagValues()
    {
        var exception = Assert.Throws<FormatException>(() => FfmpegStreamer.BuildFfmpegArguments(
            "/media/Movie.mkv",
            20,
            useGpu: true,
            customFlags: "-hwaccel_device /etc/passwd",
            seekPositionSeconds: 0,
            frameWidth: 160,
            frameHeight: 90,
            scalingMode: PluginConfiguration.VideoScalingModeStretch,
            deinterlaceMode: PluginConfiguration.VideoDeinterlaceModeOff));

        Assert.Contains("unsafe value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAudioFfmpegArguments_RejectsUnsafeCustomFlags()
    {
        var exception = Assert.Throws<FormatException>(() => FfmpegStreamer.BuildAudioFfmpegArguments(
            "/media/Music.mkv",
            useGpu: false,
            customFlags: "-lavfi amovie=/tmp/secret.txt",
            seekPositionSeconds: 0));

        Assert.Contains("not allowed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseSafeCustomArguments_RejectsExcessiveOptionText()
    {
        var customFlags = string.Join(' ', Enumerable.Repeat("-threads 2", 7));

        var exception = Assert.Throws<FormatException>(() => FfmpegStreamer.ParseSafeCustomArguments(customFlags));

        Assert.Contains("too many tokens", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFfmpegArguments_UsesSafeTokensForPathsAndSeek()
    {
        var arguments = FfmpegStreamer.BuildFfmpegArguments(
            "/media/Movies/Movie Night \"Director's Cut\".mkv",
            20,
            true,
            "-threads 2 -filter_threads \"1\"",
            12.3456,
            160,
            90,
            PluginConfiguration.VideoScalingModeStretch,
            PluginConfiguration.VideoDeinterlaceModeOff);

        Assert.Equal(
            new[]
            {
                "-hwaccel", "auto",
                "-threads", "2",
                "-filter_threads", "1",
                "-ss", "12.346",
                "-i", "/media/Movies/Movie Night \"Director's Cut\".mkv",
                "-vf", "scale=160:90",
                "-r", "20",
                "-f", "rawvideo",
                "-pix_fmt", "rgb24",
                "pipe:1"
            },
            arguments);
    }

    [Fact]
    public void BuildAudioFfmpegArguments_UsesSafePcmTokensAndSeek()
    {
        var arguments = FfmpegStreamer.BuildAudioFfmpegArguments(
            "/media/Music/Live Set \"2026\".flac",
            useGpu: true,
            customFlags: "-threads 2 -filter_threads \"1\"",
            seekPositionSeconds: 12.3456);

        Assert.Equal(
            new[]
            {
                "-hwaccel", "auto",
                "-threads", "2",
                "-filter_threads", "1",
                "-ss", "12.346",
                "-i", "/media/Music/Live Set \"2026\".flac",
                "-vn",
                "-ac", "2",
                "-ar", "8000",
                "-f", "s16le",
                "-acodec", "pcm_s16le",
                "pipe:1"
            },
            arguments);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(60, 10)]
    [InlineData(999, 10)]
    public void GetHealthMonitorInterval_TracksConfiguredStallBudget(
        int stallTimeoutSeconds,
        int expectedSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            FfmpegStreamer.GetHealthMonitorInterval(stallTimeoutSeconds));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(8000, 0)]
    public void StartAudioFfmpeg_RejectsInvalidPcmParametersWithoutLaunching(
        int sampleRate,
        int channels)
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temporaryDirectory = new TemporaryDirectory();
        var markerPath = Path.Combine(temporaryDirectory.Path, "started.marker");
        var scriptPath = Path.Combine(temporaryDirectory.Path, "fake-ffmpeg-audio.sh");
        var mediaPath = Path.Combine(temporaryDirectory.Path, "input.m4a");
        File.WriteAllText(
            scriptPath,
            $"#!/bin/sh\nprintf started > '{markerPath}'\nprintf PCM\n");
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.WriteAllBytes(mediaPath, Array.Empty<byte>());

        var streamer = new FfmpegStreamer(Mock.Of<ILogger<FfmpegStreamer>>());
        try
        {
            Assert.Null(streamer.StartAudioFfmpeg(
                mediaPath,
                useGpu: false,
                ffmpegPath: scriptPath,
                sampleRate: sampleRate,
                channels: channels));
            Assert.False(File.Exists(markerPath));
        }
        finally
        {
            streamer.Stop();
        }
    }

    [Fact]
    public async Task StartAudioFfmpeg_StreamsPcmAndStopsProcess()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temporaryDirectory = new TemporaryDirectory();
        var scriptPath = Path.Combine(temporaryDirectory.Path, "fake-ffmpeg-audio.sh");
        var mediaPath = Path.Combine(temporaryDirectory.Path, "input.m4a");
        File.WriteAllText(
            scriptPath,
            "#!/bin/sh\nprintf 'PCM'\nprintf 'fake audio stderr\\n' >&2\nwhile :; do sleep 1; done\n");
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.WriteAllBytes(mediaPath, Array.Empty<byte>());

        var streamer = new FfmpegStreamer(Mock.Of<ILogger<FfmpegStreamer>>());
        Stream? output = null;
        var processId = 0;
        try
        {
            output = streamer.StartAudioFfmpeg(
                mediaPath,
                useGpu: false,
                ffmpegPath: scriptPath,
                seekPositionSeconds: 0,
                sampleRate: 8000,
                channels: 2);

            Assert.NotNull(output);
            Assert.Equal((byte)'P', output!.ReadByte());
            Assert.Equal((byte)'C', output.ReadByte());

            var process = GetPrivateField<Process>(streamer, "_ffmpegProcess");
            Assert.NotNull(process);
            processId = process!.Id;

            streamer.Stop();

            Assert.NotNull(Record.Exception(() => output.ReadByte()));
            Assert.True(await WaitForProcessExitAsync(processId));
        }
        finally
        {
            streamer.Stop();
            output?.Dispose();
            if (processId > 0)
                await WaitForProcessExitAsync(processId);
        }
    }

    [Fact]
    public async Task Stop_ClosesOutputAndStopsTheEntireProcessTree()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temporaryDirectory = new TemporaryDirectory();
        var childPidPath = Path.Combine(temporaryDirectory.Path, "child.pid");
        var scriptPath = Path.Combine(temporaryDirectory.Path, "fake-ffmpeg.sh");
        var mediaPath = Path.Combine(temporaryDirectory.Path, "input.mkv");
        File.WriteAllText(
            scriptPath,
            $"#!/bin/sh\n(sleep 30) &\nchild_pid=$!\nprintf '%s' \"$child_pid\" > '{childPidPath}'\nprintf x\nprintf 'fake stderr\\n' >&2\nwhile :; do sleep 1; done\n");
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.WriteAllBytes(mediaPath, Array.Empty<byte>());

        var streamer = new FfmpegStreamer(Mock.Of<ILogger<FfmpegStreamer>>());
        Stream? output = null;
        var mainProcessId = 0;
        var childProcessId = 0;
        try
        {
            output = streamer.StartFfmpeg(
                mediaPath,
                fps: 1,
                useGpu: false,
                ffmpegPath: scriptPath);

            Assert.NotNull(output);
            Assert.Equal((byte)'x', output!.ReadByte());
            childProcessId = await WaitForPidAsync(childPidPath);

            var process = GetPrivateField<Process>(streamer, "_ffmpegProcess");
            Assert.NotNull(process);
            mainProcessId = process!.Id;

            var stderrTask = GetPrivateField<Task>(streamer, "_stderrReaderTask");
            var healthTask = GetPrivateField<Task>(streamer, "_healthMonitorTask");
            Assert.NotNull(stderrTask);
            Assert.NotNull(healthTask);

            streamer.Stop();

            Assert.NotNull(Record.Exception(() => output.ReadByte()));
            Assert.True(stderrTask!.IsCompleted);
            Assert.True(healthTask!.IsCompleted);
            Assert.True(await WaitForProcessExitAsync(mainProcessId));
            Assert.True(await WaitForProcessExitAsync(childProcessId));
        }
        finally
        {
            streamer.Stop();
            output?.Dispose();
            if (mainProcessId > 0)
                await WaitForProcessExitAsync(mainProcessId);
            if (childProcessId > 0)
                await WaitForProcessExitAsync(childProcessId);
        }
    }

    [Fact]
    public async Task StderrReader_CapsUnterminatedLineAndStopCompletes()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temporaryDirectory = new TemporaryDirectory();
        var scriptPath = Path.Combine(temporaryDirectory.Path, "fake-ffmpeg-large-stderr.sh");
        var mediaPath = Path.Combine(temporaryDirectory.Path, "input.mkv");
        File.WriteAllText(
            scriptPath,
            "#!/bin/sh\n" +
            "dd if=/dev/zero bs=1048576 count=4 2>/dev/null | tr '\\000' A >&2\n" +
            "printf x\n" +
            "while :; do sleep 1; done\n");
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.WriteAllBytes(mediaPath, Array.Empty<byte>());

        var logger = new RecordingLogger();
        var streamer = new FfmpegStreamer(logger);
        Stream? output = null;
        try
        {
            output = streamer.StartFfmpeg(
                mediaPath,
                fps: 1,
                useGpu: false,
                ffmpegPath: scriptPath);

            Assert.NotNull(output);

            // The script writes several megabytes to stderr before stdout. This
            // read proves the stderr worker continues draining instead of letting
            // the child block on its redirected pipe.
            var outputReadTask = Task.Run(() => output!.ReadByte());
            Assert.Same(outputReadTask, await Task.WhenAny(outputReadTask, Task.Delay(TimeSpan.FromSeconds(5))));
            Assert.Equal((byte)'x', await outputReadTask);

            Assert.True(await WaitForConditionAsync(
                () => logger.Messages.Any(message => message.EndsWith("[truncated]", StringComparison.Ordinal))));

            var stderrMessages = logger.Messages
                .Where(message => message.StartsWith("FFmpeg: ", StringComparison.Ordinal))
                .ToArray();
            Assert.Contains(stderrMessages, message => message.EndsWith("[truncated]", StringComparison.Ordinal));
            Assert.All(
                stderrMessages,
                message => Assert.True(
                    message.Length <=
                        "FFmpeg: ".Length +
                        FfmpegStreamer.MaximumStandardErrorLineChars +
                        " [truncated]".Length));

            var stderrTask = GetPrivateField<Task>(streamer, "_stderrReaderTask");
            Assert.NotNull(stderrTask);

            var stopTask = Task.Run(streamer.Stop);
            Assert.Same(stopTask, await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(5))));
            await stopTask;
            // Stop() has a bounded cleanup deadline. A shell pipeline may keep an
            // inherited stderr descriptor alive briefly after the process tree is
            // killed, so allow the retained reader to settle within the test's
            // existing five-second deadline instead of racing its completion.
            Assert.True(await WaitForConditionAsync(() => stderrTask!.IsCompleted));
        }
        finally
        {
            streamer.Stop();
            output?.Dispose();
        }
    }

    private static T? GetPrivateField<T>(FfmpegStreamer streamer, string name)
    {
        return (T?)typeof(FfmpegStreamer)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(streamer);
    }

    private static async Task<int> WaitForPidAsync(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) && int.TryParse(File.ReadAllText(path), out var pid))
                return pid;

            await Task.Delay(25);
        }

        throw new TimeoutException("The fake FFmpeg process did not publish its child PID.");
    }

    private static async Task<bool> WaitForConditionAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;

            await Task.Delay(25);
        }

        return false;
    }

    private static async Task<bool> WaitForProcessExitAsync(int processId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                    return true;
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("hue-ffmpeg-test-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // The test has already asserted process cleanup; leave a diagnostic
                // directory in place if the host has not released it yet.
            }
        }
    }

    private sealed class RecordingLogger : ILogger<FfmpegStreamer>
    {
        private readonly object _gate = new();
        private readonly List<string> _messages = new();

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_gate)
                    return _messages.ToArray();
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
                _messages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
