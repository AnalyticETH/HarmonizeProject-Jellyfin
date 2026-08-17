using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Jellyfin.Plugin.Hue.Hue;
using Jellyfin.Plugin.Hue.Service;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

public sealed class HueStreamTesterTests
{
    [Fact]
    public void TryBuildProbeColors_UsesEveryValidChannel()
    {
        using var document = JsonDocument.Parse(
            "{\"channels\":[{\"channel_id\":1},{\"channel_id\":65535}]}");

        var valid = HueStreamTester.TryBuildProbeColors(document.RootElement, out var colors);

        Assert.True(valid);
        Assert.Equal(2, colors.Count);
        Assert.Equal(new byte[] { 1, 1, 1, 1, 1, 1 }, colors[1]);
        Assert.Equal(new byte[] { 1, 1, 1, 1, 1, 1 }, colors[65535]);
    }

    [Fact]
    public void TryBuildProbeColors_RejectsMalformedChannels()
    {
        using var document = JsonDocument.Parse(
            "{\"channels\":[{\"channel_id\":-1}]}");

        var valid = HueStreamTester.TryBuildProbeColors(document.RootElement, out var colors);

        Assert.False(valid);
        Assert.Empty(colors);
    }

    [Fact]
    public void TryBuildProbeColors_WithChannelProfileUsesOnlySelectedChannels()
    {
        using var document = JsonDocument.Parse(
            "{\"channels\":[{\"channel_id\":1},{\"channel_id\":2},{\"channel_id\":3}]}");

        var valid = HueStreamTester.TryBuildProbeColors(
            document.RootElement,
            new HashSet<int> { 2 },
            out var colors);

        Assert.True(valid);
        Assert.Single(colors);
        Assert.Contains(2, colors);
    }

    [Fact]
    public void TryBuildProbeColors_WithUnknownChannelProfileIsRejected()
    {
        using var document = JsonDocument.Parse(
            "{\"channels\":[{\"channel_id\":1}]}");

        var valid = HueStreamTester.TryBuildProbeColors(
            document.RootElement,
            new HashSet<int> { 9 },
            out var colors);

        Assert.False(valid);
        Assert.Empty(colors);
    }

    [Fact]
    public void TryBuildSolidColors_AppliesBrightnessAndUsesSelectedChannels()
    {
        using var document = JsonDocument.Parse(
            "{\"channels\":[{\"channel_id\":1},{\"channel_id\":2},{\"channel_id\":3}]}");

        var valid = HueStreamTester.TryBuildSolidColors(
            document.RootElement,
            new HashSet<int> { 2 },
            red: 255,
            green: 128,
            blue: 0,
            brightnessPercent: 50,
            out var colors);

        Assert.True(valid);
        Assert.Single(colors);
        Assert.Equal(new byte[] { 63, 63, 32, 32, 0, 0 }, colors[2]);
    }

    [Fact]
    public void TryBuildSolidColors_RejectsUnknownOrInvalidPreviewValues()
    {
        using var document = JsonDocument.Parse(
            "{\"channels\":[{\"channel_id\":1}]}");

        Assert.False(HueStreamTester.TryBuildSolidColors(
            document.RootElement,
            new HashSet<int> { 9 },
            255,
            255,
            255,
            100,
            out var unknownColors));
        Assert.Empty(unknownColors);

        Assert.False(HueStreamTester.TryBuildSolidColors(
            document.RootElement,
            null,
            256,
            0,
            0,
            100,
            out var invalidColors));
        Assert.Empty(invalidColors);
    }

    [Fact]
    public async Task TestAsync_WithMalformedAreaDoesNotTouchBridge()
    {
        var handler = new Mock<HttpMessageHandler>();
        using var httpClient = new HttpClient(handler.Object);
        var hueClient = new HueClient(httpClient, Mock.Of<ILogger<HueClient>>());
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(factory => factory.CreateLogger(It.IsAny<string>())).Returns(Mock.Of<ILogger>());
        var tester = new HueStreamTester(
            hueClient,
            loggerFactory.Object,
            Mock.Of<ILogger<HueStreamTester>>());
        using var document = JsonDocument.Parse("{\"channels\":[]}");

        var result = await tester.TestAsync(
            "192.168.1.100",
            "app-key",
            "client-key",
            "area-id",
            document.RootElement);

        Assert.False(result.Succeeded);
        Assert.Contains("valid controllable channels", result.Message, StringComparison.OrdinalIgnoreCase);
        handler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TestAsync_WithIncompleteLightCaptureDoesNotActivateBridge()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(request =>
                    request.Method == HttpMethod.Get &&
                    request.RequestUri!.AbsolutePath.Contains("/light/", StringComparison.Ordinal)),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var httpClient = new HttpClient(handler.Object);
        var hueClient = new HueClient(httpClient, Mock.Of<ILogger<HueClient>>())
        {
            RetryAttempts = 0
        };
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(factory => factory.CreateLogger(It.IsAny<string>())).Returns(Mock.Of<ILogger>());
        var tester = new HueStreamTester(
            hueClient,
            loggerFactory.Object,
            Mock.Of<ILogger<HueStreamTester>>());
        using var document = JsonDocument.Parse(
            "{\"channels\":[{\"channel_id\":1,\"members\":[{\"service\":{\"rid\":\"light-1\"}}]}]}");

        var result = await tester.TestAsync(
            "192.168.1.100",
            "app-key",
            "client-key",
            "area-id",
            document.RootElement);

        Assert.False(result.Succeeded);
        Assert.Contains("capture", result.Message, StringComparison.OrdinalIgnoreCase);
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(request =>
                request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath.Contains("/light/", StringComparison.Ordinal)),
            ItExpr.IsAny<CancellationToken>());
        handler.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.Is<HttpRequestMessage>(request => request.Method == HttpMethod.Put),
            ItExpr.IsAny<CancellationToken>());
    }
}
