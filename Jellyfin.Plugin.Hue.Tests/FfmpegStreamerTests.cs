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
    [InlineData(0, 90)]
    [InlineData(160, 0)]
    public void BuildVideoFilter_RejectsNonPositiveDimensions(int frameWidth, int frameHeight)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FfmpegStreamer.BuildVideoFilter(frameWidth, frameHeight));
    }
}
