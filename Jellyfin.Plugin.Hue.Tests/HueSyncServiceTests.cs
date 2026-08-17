using System.Collections.Generic;
using Jellyfin.Plugin.Hue.Configuration;
using Jellyfin.Plugin.Hue.Service;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

public sealed class HueSyncServiceTests
{
    [Theory]
    [InlineData(true, false, null, "session-a", true)]
    [InlineData(true, true, "session-a", "session-a", false)]
    [InlineData(true, true, "session-a", "session-b", true)]
    [InlineData(false, true, "session-a", "session-a", false)]
    public void ShouldCaptureLightState_PreservesSnapshotForSamePlaybackSession(
        bool restoreLightState,
        bool hasSavedLightStates,
        string? savedLightStatePlaySessionId,
        string playSessionId,
        bool expected)
    {
        Assert.Equal(
            expected,
            HueSyncService.ShouldCaptureLightState(
                restoreLightState,
                hasSavedLightStates,
                savedLightStatePlaySessionId,
                playSessionId));
    }

    [Fact]
    public void ResolvePlaybackSettings_UsesPerUserOverridesAndGlobalFallback()
    {
        var userId = System.Guid.NewGuid();
        var configuration = new PluginConfiguration
        {
            UseCinemaMode = true,
            BrightnessDimLevel = 40,
            RestoreLightState = true,
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = userId.ToString(),
                    UseCinemaModeOverride = false,
                    BrightnessDimLevelOverride = 10,
                    RestoreLightStateOverride = false
                }
            }
        };

        var effective = HueSyncService.ResolvePlaybackSettings(configuration, userId);
        var fallback = HueSyncService.ResolvePlaybackSettings(configuration, System.Guid.NewGuid());

        Assert.False(effective.UseCinemaMode);
        Assert.Equal(10, effective.BrightnessDimLevel);
        Assert.False(effective.RestoreLightState);
        Assert.True(fallback.UseCinemaMode);
        Assert.Equal(40, fallback.BrightnessDimLevel);
        Assert.True(fallback.RestoreLightState);
    }

    [Fact]
    public void ResolvePauseBehavior_UsesPerUserOverrideAndGlobalFallback()
    {
        var userId = System.Guid.NewGuid();
        var configuration = new PluginConfiguration
        {
            PauseBehavior = PluginConfiguration.PauseBehaviorKeepLastColors,
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = userId.ToString(),
                    PauseBehaviorOverride = PluginConfiguration.PauseBehaviorRestoreLightState
                }
            }
        };

        var effective = HueSyncService.ResolvePauseBehavior(configuration, userId);
        var fallback = HueSyncService.ResolvePauseBehavior(configuration, System.Guid.NewGuid());

        Assert.Equal(PluginConfiguration.PauseBehaviorRestoreLightState, effective);
        Assert.Equal(PluginConfiguration.PauseBehaviorKeepLastColors, fallback);
    }

    [Fact]
    public void ResolvePerformanceSettings_UsesPerUserOverridesAndGlobalFallback()
    {
        var userId = System.Guid.NewGuid();
        var configuration = new PluginConfiguration
        {
            TargetFps = 20,
            FrameResolution = PluginConfiguration.FrameResolutionStandard,
            VideoScalingMode = PluginConfiguration.VideoScalingModeStretch,
            VideoDeinterlaceMode = PluginConfiguration.VideoDeinterlaceModeOff,
            SamplingBreadthPercent = 15,
            SamplingMode = PluginConfiguration.SamplingModeAverage,
            ColorSmoothingPercent = 0,
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = userId.ToString(),
                    TargetFpsOverride = 30,
                    FrameResolutionOverride = PluginConfiguration.FrameResolutionHigh,
                    VideoScalingModeOverride = PluginConfiguration.VideoScalingModeFit,
                    VideoDeinterlaceModeOverride = PluginConfiguration.VideoDeinterlaceModeAuto,
                    SamplingBreadthPercentOverride = 25,
                    SamplingModeOverride = PluginConfiguration.SamplingModeCenterWeighted,
                    ColorSmoothingPercentOverride = 40
                }
            }
        };

        var effective = HueSyncService.ResolvePerformanceSettings(configuration, userId);
        var fallback = HueSyncService.ResolvePerformanceSettings(configuration, System.Guid.NewGuid());

        Assert.Equal(30, effective.TargetFps);
        Assert.Equal(PluginConfiguration.FrameResolutionHigh, effective.FrameResolution);
        Assert.Equal(PluginConfiguration.VideoScalingModeFit, effective.VideoScalingMode);
        Assert.Equal(PluginConfiguration.VideoDeinterlaceModeAuto, effective.VideoDeinterlaceMode);
        Assert.Equal(25, effective.SamplingBreadthPercent);
        Assert.Equal(PluginConfiguration.SamplingModeCenterWeighted, effective.SamplingMode);
        Assert.Equal(40, effective.ColorSmoothingPercent);
        Assert.Equal(20, fallback.TargetFps);
        Assert.Equal(PluginConfiguration.FrameResolutionStandard, fallback.FrameResolution);
        Assert.Equal(PluginConfiguration.VideoScalingModeStretch, fallback.VideoScalingMode);
        Assert.Equal(PluginConfiguration.VideoDeinterlaceModeOff, fallback.VideoDeinterlaceMode);
        Assert.Equal(15, fallback.SamplingBreadthPercent);
        Assert.Equal(PluginConfiguration.SamplingModeAverage, fallback.SamplingMode);
        Assert.Equal(0, fallback.ColorSmoothingPercent);
    }

    [Fact]
    public void ResolveColorProcessingSettings_UsesPerUserThresholdsAndGlobalFallback()
    {
        var userId = System.Guid.NewGuid();
        var configuration = new PluginConfiguration
        {
            BrightnessBoost = 110,
            RedGain = 95,
            GreenGain = 105,
            BlueGain = 115,
            ColorSaturation = 90,
            HueShiftDegrees = 20,
            OutputBrightnessPercent = 80,
            BlackoutThreshold = 15,
            ColorChangeThreshold = 10,
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = userId.ToString(),
                    BrightnessBoostOverride = 150,
                    RedGainOverride = 120,
                    GreenGainOverride = 90,
                    BlueGainOverride = 130,
                    ColorSaturationOverride = 125,
                    HueShiftDegreesOverride = -45,
                    OutputBrightnessPercentOverride = 70,
                    BlackoutThresholdOverride = 35,
                    ColorChangeThresholdOverride = 4
                }
            }
        };

        var effective = HueSyncService.ResolveColorProcessingSettings(configuration, userId);
        var fallback = HueSyncService.ResolveColorProcessingSettings(configuration, System.Guid.NewGuid());

        Assert.Equal(150, effective.BrightnessBoost);
        Assert.Equal(120, effective.RedGain);
        Assert.Equal(90, effective.GreenGain);
        Assert.Equal(130, effective.BlueGain);
        Assert.Equal(125, effective.ColorSaturation);
        Assert.Equal(-45, effective.HueShiftDegrees);
        Assert.Equal(70, effective.OutputBrightnessPercent);
        Assert.Equal(35, effective.BlackoutThreshold);
        Assert.Equal(4, effective.ColorChangeThreshold);
        Assert.Equal(110, fallback.BrightnessBoost);
        Assert.Equal(95, fallback.RedGain);
        Assert.Equal(105, fallback.GreenGain);
        Assert.Equal(115, fallback.BlueGain);
        Assert.Equal(90, fallback.ColorSaturation);
        Assert.Equal(20, fallback.HueShiftDegrees);
        Assert.Equal(80, fallback.OutputBrightnessPercent);
        Assert.Equal(15, fallback.BlackoutThreshold);
        Assert.Equal(10, fallback.ColorChangeThreshold);
    }

    [Theory]
    [InlineData(0, 18)]
    [InlineData(1, 1)]
    [InlineData(15, 18)]
    [InlineData(50, 62)]
    [InlineData(100, 62)]
    public void CalculateSamplingDistance_NormalizesBreadth(int samplingBreadthPercent, int expectedDistance)
    {
        Assert.Equal(expectedDistance, HueSyncService.CalculateSamplingDistance(samplingBreadthPercent));
    }

    [Theory]
    [InlineData(80, 45, 9)]
    [InlineData(160, 90, 18)]
    [InlineData(320, 180, 37)]
    public void CalculateSamplingDistance_ScalesWithFrameResolution(
        int frameWidth,
        int frameHeight,
        int expectedDistance)
    {
        Assert.Equal(
            expectedDistance,
            HueSyncService.CalculateSamplingDistance(15, frameWidth, frameHeight));
    }

    [Fact]
    public void SampleRegionColor_CenterPixelReturnsMappedPixel()
    {
        var frame = new byte[160 * 90 * 3];
        var index = (45 * 160 + 80) * 3;
        frame[index] = 231;
        frame[index + 1] = 87;
        frame[index + 2] = 14;

        var sampled = HueSyncService.SampleRegionColor(
            frame,
            80,
            45,
            18,
            "centerpixel");

        Assert.Equal(new byte[] { 231, 87, 14 }, sampled);
    }

    [Fact]
    public void SampleRegionColor_SupportsHighResolutionFrames()
    {
        var frame = new byte[320 * 180 * 3];
        var index = (90 * 320 + 160) * 3;
        frame[index] = 12;
        frame[index + 1] = 34;
        frame[index + 2] = 56;

        var sampled = HueSyncService.SampleRegionColor(
            frame,
            160,
            90,
            37,
            PluginConfiguration.SamplingModeCenterPixel,
            320,
            180);

        Assert.Equal(new byte[] { 12, 34, 56 }, sampled);
    }

    [Fact]
    public void SampleRegionColor_AveragePreservesNeighborhoodMean()
    {
        var frame = new byte[160 * 90 * 3];
        for (var y = 43; y < 47; y++)
        {
            for (var x = 78; x < 82; x++)
            {
                var index = (y * 160 + x) * 3;
                frame[index] = 10;
                frame[index + 1] = 20;
                frame[index + 2] = 30;
            }
        }

        var centerIndex = (45 * 160 + 80) * 3;
        frame[centerIndex] = 250;
        frame[centerIndex + 1] = 100;
        frame[centerIndex + 2] = 50;

        var sampled = HueSyncService.SampleRegionColor(
            frame,
            80,
            45,
            2,
            PluginConfiguration.SamplingModeAverage);

        Assert.Equal(new byte[] { 25, 25, 31 }, sampled);
    }

    [Fact]
    public void SampleRegionColor_CenterWeightedFavorsMappedPixel()
    {
        var frame = new byte[160 * 90 * 3];
        var centerIndex = (45 * 160 + 80) * 3;
        frame[centerIndex] = byte.MaxValue;

        var average = HueSyncService.SampleRegionColor(
            frame,
            80,
            45,
            2,
            PluginConfiguration.SamplingModeAverage);
        var weighted = HueSyncService.SampleRegionColor(
            frame,
            80,
            45,
            2,
            PluginConfiguration.SamplingModeCenterWeighted);

        Assert.True(weighted[0] > average[0]);
        Assert.Equal(new byte[] { 0, 0 }, weighted[1..]);
    }

    [Fact]
    public void SampleRegionColor_UnknownModeFallsBackToAverage()
    {
        var frame = new byte[160 * 90 * 3];
        for (var y = 44; y < 46; y++)
        {
            for (var x = 79; x < 81; x++)
            {
                var index = (y * 160 + x) * 3;
                frame[index] = 40;
                frame[index + 1] = 50;
                frame[index + 2] = 60;
            }
        }

        var expected = HueSyncService.SampleRegionColor(frame, 80, 45, 1, "Average");
        var sampled = HueSyncService.SampleRegionColor(frame, 80, 45, 1, "not-a-mode");

        Assert.Equal(expected, sampled);
    }

    [Fact]
    public void ApplyTemporalSmoothing_BlendsPreviousFrameByConfiguredWeight()
    {
        var current = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 1, 100, 200 }
        };
        var previous = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 101, 50, 0 }
        };

        var smoothed = HueSyncService.ApplyTemporalSmoothing(current, previous, 50);

        Assert.Equal(new byte[] { 51, 75, 100 }, smoothed[1]);
    }

    [Fact]
    public void ApplyTemporalSmoothing_UsesCurrentFrameWhenHistoryIsMissing()
    {
        var current = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 10, 20, 30 }
        };

        var smoothed = HueSyncService.ApplyTemporalSmoothing(
            current,
            new Dictionary<int, byte[]>(),
            90);

        Assert.Equal(new byte[] { 10, 20, 30 }, smoothed[1]);
    }

    [Fact]
    public void ApplyTemporalSmoothing_ClampsStrengthToSafeMaximum()
    {
        var current = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 100, 100, 100 }
        };
        var previous = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 0, 0, 0 }
        };

        var smoothed = HueSyncService.ApplyTemporalSmoothing(current, previous, 100);

        Assert.Equal(new byte[] { 10, 10, 10 }, smoothed[1]);
    }

    [Theory]
    [InlineData(255, 100, 255)]
    [InlineData(255, 50, 127.5)]
    [InlineData(255, 0, 0)]
    [InlineData(-10, 100, 0)]
    [InlineData(300, 100, 255)]
    public void ApplyOutputBrightness_ScalesAndClamps(
        double channel,
        int outputBrightnessPercent,
        double expected)
    {
        Assert.Equal(
            expected,
            HueSyncService.ApplyOutputBrightness(channel, outputBrightnessPercent),
            precision: 6);
    }

    [Theory]
    [InlineData(0, 90, 0.25)]
    [InlineData(0.9, 90, 0.15)]
    [InlineData(0.1, -90, 0.85)]
    [InlineData(0.5, 180, 0)]
    public void ApplyHueShift_RotatesAndWrapsNormalizedHue(
        double hue,
        int hueShiftDegrees,
        double expected)
    {
        Assert.Equal(
            expected,
            HueSyncService.ApplyHueShift(hue, hueShiftDegrees),
            precision: 6);
    }
}
