using Jellyfin.Plugin.Hue.Configuration;
using Jellyfin.Plugin.Hue.Video;
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
}
