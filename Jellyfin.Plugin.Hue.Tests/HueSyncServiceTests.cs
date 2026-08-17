using System.Collections.Generic;
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
