using Jellyfin.Plugin.Hue.Service;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

public sealed class HueSyncServiceTests
{
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
}
