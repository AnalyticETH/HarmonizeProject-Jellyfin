using Jellyfin.Plugin.Hue.Hue;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

public sealed class HueColorMathTests
{
    [Fact]
    public void TryConvertXyToRgb_ConvertsRedDominantHueCoordinate()
    {
        var converted = HueColorMath.TryConvertXyToRgb(
            0.64,
            0.33,
            out var red,
            out var green,
            out var blue);

        Assert.True(converted);
        Assert.True(red > 0.9);
        Assert.True(red > green);
        Assert.True(red > blue);
    }

    [Fact]
    public void TryConvertMirekToRgb_ConvertsWarmColorTemperature()
    {
        var converted = HueColorMath.TryConvertMirekToRgb(
            325,
            out var red,
            out var green,
            out var blue);

        Assert.True(converted);
        Assert.True(red > green);
        Assert.True(green > blue);
    }

    [Fact]
    public void TryAverageLightStates_IgnoresOffChromaticityAndRetainsAverageBrightness()
    {
        var states = new[]
        {
            new HueClient.LightState("on", true, 80, 0.64, 0.33, null, true),
            new HueClient.LightState("off", false, 0, 0.17, 0.7, null, true)
        };

        var converted = HueColorMath.TryAverageLightStates(states, out var sample);

        Assert.True(converted);
        Assert.Equal(1, sample.SampledLightCount);
        Assert.Equal(40, sample.BrightnessPercent);
        Assert.True(sample.Red > sample.Green);
        Assert.True(sample.Red > sample.Blue);
    }

    [Fact]
    public void TryAverageLightStates_AllLightsOffReturnsBlackSample()
    {
        var states = new[]
        {
            new HueClient.LightState("off-1", false, 0, 0.64, 0.33, null, true),
            new HueClient.LightState("off-2", false, 0, 0.17, 0.7, null, true)
        };

        var converted = HueColorMath.TryAverageLightStates(states, out var sample);

        Assert.True(converted);
        Assert.Equal(0, sample.Red);
        Assert.Equal(0, sample.Green);
        Assert.Equal(0, sample.Blue);
        Assert.Equal(0, sample.BrightnessPercent);
        Assert.Equal(0, sample.SampledLightCount);
    }

    [Fact]
    public void TryAverageLightStates_RejectsMissingOrInvalidSamples()
    {
        Assert.False(HueColorMath.TryAverageLightStates(null, out _));
        Assert.False(HueColorMath.TryAverageLightStates(Array.Empty<HueClient.LightState>(), out _));
        Assert.False(HueColorMath.TryConvertXyToRgb(0, 0.5, out _, out _, out _));
        Assert.False(HueColorMath.TryConvertMirekToRgb(0, out _, out _, out _));
    }

    [Fact]
    public void TryConvertXyToRgb_RejectsSubnormalRatioOverflow()
    {
        var converted = HueColorMath.TryConvertXyToRgb(
            0.5,
            double.Epsilon,
            out var red,
            out var green,
            out var blue);

        Assert.False(converted);
        Assert.Equal(0, red);
        Assert.Equal(0, green);
        Assert.Equal(0, blue);
    }
}
