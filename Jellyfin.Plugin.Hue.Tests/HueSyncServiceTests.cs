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
        Assert.Equal(new byte[] { 0, 0, 0 }, weighted[1..]);
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
}
