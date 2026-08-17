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
    [InlineData(0, 90)]
    [InlineData(160, 0)]
    public void BuildVideoFilter_RejectsNonPositiveDimensions(int frameWidth, int frameHeight)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FfmpegStreamer.BuildVideoFilter(frameWidth, frameHeight));
    }
}
