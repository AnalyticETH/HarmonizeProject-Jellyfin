using Jellyfin.Plugin.Hue.Service;
using Jellyfin.Plugin.Hue.Hue;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

public class ColorProcessingTests : IDisposable
{
    private readonly HueSyncService _service;
    private readonly HttpClient _httpClient;

    public ColorProcessingTests()
    {
        var mockSessionManager = new Mock<ISessionManager>();
        var mockLogger = new Mock<ILogger<HueSyncService>>();
        var mockLoggerFactory = new Mock<ILoggerFactory>();
        var mockHueClientLogger = new Mock<ILogger<HueClient>>();

        _httpClient = new HttpClient();
        var hueClient = new HueClient(_httpClient, mockHueClientLogger.Object);

        mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(Mock.Of<ILogger>());

        _service = new HueSyncService(
            mockSessionManager.Object,
            mockLogger.Object,
            mockLoggerFactory.Object,
            hueClient
        );
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    [Theory]
    [InlineData(1.0, 0.0, 0.0, 0.0, 1.0, 0.5)] // Pure red
    [InlineData(0.0, 1.0, 0.0, 0.333, 1.0, 0.5)] // Pure green (approximately)
    [InlineData(0.0, 0.0, 1.0, 0.667, 1.0, 0.5)] // Pure blue (approximately)
    [InlineData(1.0, 1.0, 1.0, 0.0, 0.0, 1.0)] // White
    [InlineData(0.0, 0.0, 0.0, 0.0, 0.0, 0.0)] // Black
    [InlineData(0.5, 0.5, 0.5, 0.0, 0.0, 0.5)] // Gray
    public void RgbToHsl_ConvertsCorrectly(double r, double g, double b, double expectedH, double expectedS, double expectedL)
    {
        // Act
        var (h, s, l) = _service.RgbToHsl(r, g, b);

        // Assert
        Assert.InRange(h, expectedH - 0.01, expectedH + 0.01);
        Assert.InRange(s, expectedS - 0.01, expectedS + 0.01);
        Assert.InRange(l, expectedL - 0.01, expectedL + 0.01);
    }

    [Theory]
    [InlineData(0.0, 1.0, 0.5, 1.0, 0.0, 0.0)] // Red
    [InlineData(0.0, 0.0, 1.0, 1.0, 1.0, 1.0)] // White (no saturation)
    [InlineData(0.0, 0.0, 0.0, 0.0, 0.0, 0.0)] // Black
    [InlineData(0.5, 1.0, 0.5, 0.0, 1.0, 1.0)] // Cyan
    public void HslToRgb_ConvertsCorrectly(double h, double s, double l, double expectedR, double expectedG, double expectedB)
    {
        // Act
        var (r, g, b) = _service.HslToRgb(h, s, l);

        // Assert
        Assert.InRange(r, expectedR - 0.01, expectedR + 0.01);
        Assert.InRange(g, expectedG - 0.01, expectedG + 0.01);
        Assert.InRange(b, expectedB - 0.01, expectedB + 0.01);
    }

    [Fact]
    public void RgbToHsl_AndBack_PreservesOriginalColor()
    {
        // Arrange
        double r = 0.75, g = 0.25, b = 0.5;

        // Act
        var (h, s, l) = _service.RgbToHsl(r, g, b);
        var (r2, g2, b2) = _service.HslToRgb(h, s, l);

        // Assert
        Assert.InRange(r2, r - 0.001, r + 0.001);
        Assert.InRange(g2, g - 0.001, g + 0.001);
        Assert.InRange(b2, b - 0.001, b + 0.001);
    }

    [Theory]
    [InlineData(0.2, 0.8, 0.1, 0.56)] // t < 1/6: p + (q-p)*6*t = 0.2 + 0.6*0.6 = 0.56
    [InlineData(0.2, 0.8, 0.4, 0.8)] // t < 1/2: returns q
    [InlineData(0.2, 0.8, 0.6, 0.44)] // t < 2/3: p + (q-p)*(2/3-t)*6 = 0.2 + 0.6*0.067*6 = 0.44
    [InlineData(0.2, 0.8, 0.9, 0.2)] // t > 2/3: returns p
    public void HueToRgb_CalculatesCorrectly(double p, double q, double t, double expected)
    {
        // Act
        var result = _service.HueToRgb(p, q, t);

        // Assert
        Assert.InRange(result, expected - 0.01, expected + 0.01);
    }

    [Fact]
    public void HueToRgb_HandlesNegativeT()
    {
        // Arrange
        double p = 0.2, q = 0.8, t = -0.2;

        // Act
        var result = _service.HueToRgb(p, q, t);

        // Assert - t should wrap around to 0.8
        Assert.True(result >= 0 && result <= 1);
    }

    [Fact]
    public void HueToRgb_HandlesTGreaterThan1()
    {
        // Arrange
        double p = 0.2, q = 0.8, t = 1.2;

        // Act
        var result = _service.HueToRgb(p, q, t);

        // Assert - t should wrap around to 0.2
        Assert.True(result >= 0 && result <= 1);
    }

    [Theory]
    [InlineData(0.0, 0.0, 0.0)] // Black edge case
    [InlineData(1.0, 1.0, 1.0)] // White edge case
    [InlineData(0.5, 0.0, 0.0)] // Dark red
    [InlineData(0.0, 0.5, 0.0)] // Dark green
    [InlineData(0.0, 0.0, 0.5)] // Dark blue
    public void RgbToHsl_HandlesEdgeCases(double r, double g, double b)
    {
        // Act
        var (h, s, l) = _service.RgbToHsl(r, g, b);

        // Assert - values should be in valid range
        Assert.InRange(h, 0.0, 1.0);
        Assert.InRange(s, 0.0, 1.0);
        Assert.InRange(l, 0.0, 1.0);
    }

    [Theory]
    [InlineData(0.0, 0.0, 0.0)] // Black
    [InlineData(1.0, 1.0, 1.0)] // White (max saturation, max lightness)
    [InlineData(0.5, 0.5, 0.5)] // Mid gray
    [InlineData(0.5, 0.0, 0.0)] // Dark (low lightness)
    [InlineData(0.5, 1.0, 1.0)] // Bright (high lightness)
    public void HslToRgb_HandlesEdgeCases(double h, double s, double l)
    {
        // Act
        var (r, g, b) = _service.HslToRgb(h, s, l);

        // Assert - values should be in valid range
        Assert.InRange(r, 0.0, 1.0);
        Assert.InRange(g, 0.0, 1.0);
        Assert.InRange(b, 0.0, 1.0);
    }

    [Fact]
    public void RgbToHsl_MultipleConversions_AreConsistent()
    {
        // Arrange
        double r = 0.6, g = 0.3, b = 0.9;

        // Act - Convert multiple times
        var (h1, s1, l1) = _service.RgbToHsl(r, g, b);
        var (r2, g2, b2) = _service.HslToRgb(h1, s1, l1);
        var (h2, s2, l2) = _service.RgbToHsl(r2, g2, b2);

        // Assert - Second conversion should match first
        Assert.InRange(h2, h1 - 0.001, h1 + 0.001);
        Assert.InRange(s2, s1 - 0.001, s1 + 0.001);
        Assert.InRange(l2, l1 - 0.001, l1 + 0.001);
    }
}
