using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using Jellyfin.Plugin.Hue.Hue;
using Jellyfin.Plugin.Hue.Service;
using Microsoft.Extensions.Logging;
using Moq;
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
}
