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

    [Fact]
    public async Task TestAsync_WhenCanceledDuringCaptureDoesNotActivateBridge()
    {
        var handler = new Mock<HttpMessageHandler>();
        var requestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(request =>
                    request.Method == HttpMethod.Get &&
                    request.RequestUri!.AbsolutePath.Contains("/light/", StringComparison.Ordinal)),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((_, cancellationToken) =>
            {
                requestStarted.TrySetResult(true);
                return releaseRequest.Task.WaitAsync(cancellationToken);
            });
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
        using var cancellationSource = new CancellationTokenSource();

        var probeTask = tester.TestAsync(
            "192.168.1.100",
            "app-key",
            "client-key",
            "area-id",
            document.RootElement,
            cancellationToken: cancellationSource.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellationSource.Cancel();

        var result = await probeTask;

        Assert.False(result.Succeeded);
        Assert.Contains("canceled", result.Message, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task TestAsync_WhenCanceledAfterActivationStillRestoresBridgeState()
    {
        var handler = new Mock<HttpMessageHandler>();
        var startSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopCount = 0;
        var restoreCount = 0;
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(request =>
                    request.Method == HttpMethod.Get &&
                    request.RequestUri!.AbsolutePath.Contains("/light/", StringComparison.Ordinal)),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{
                    ""data"": [{
                        ""on"": {""on"": true},
                        ""dimming"": {""brightness"": 50},
                        ""color"": {""xy"": {""x"": 0.3, ""y"": 0.3}}
                    }]
                }")
            });
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(request => request.Method == HttpMethod.Put),
                ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage request, CancellationToken _) =>
            {
                var body = request.Content == null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync();
                if (body.Contains("\"start\"", StringComparison.Ordinal))
                    startSent.TrySetResult(true);
                if (body.Contains("\"stop\"", StringComparison.Ordinal))
                {
                    stopCount++;
                    stopSent.TrySetResult(true);
                }
                if (request.RequestUri?.AbsolutePath.Contains("/light/", StringComparison.Ordinal) == true)
                    restoreCount++;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}")
                };
            });
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
        using var cancellationSource = new CancellationTokenSource();

        var probeTask = tester.TestAsync(
            "192.168.1.100",
            "app-key",
            "client-key",
            "area-id",
            document.RootElement,
            cancellationToken: cancellationSource.Token);
        await startSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellationSource.Cancel();

        var result = await probeTask;
        await stopSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Succeeded);
        Assert.Contains("canceled", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, stopCount);
        Assert.Equal(1, restoreCount);
    }

    [Fact]
    public async Task PreviewAsync_WhileProbeIsRunningReturnsBusyWithoutCapturing()
    {
        var handler = new Mock<HttpMessageHandler>();
        var lightRequestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLightRequest = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(request =>
                    request.Method == HttpMethod.Get &&
                    request.RequestUri!.AbsolutePath.Contains("/light/", StringComparison.Ordinal)),
                ItExpr.IsAny<CancellationToken>())
            .Returns(async () =>
            {
                lightRequestStarted.TrySetResult(true);
                return await releaseLightRequest.Task;
            });
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

        var firstProbe = tester.TestAsync(
            "192.168.1.100",
            "app-key",
            "client-key",
            "area-id",
            document.RootElement);
        await lightRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondProbe = await tester.PreviewAsync(
            "192.168.1.100",
            "app-key",
            "client-key",
            "area-id",
            document.RootElement,
            null,
            255,
            0,
            0,
            50,
            1);

        Assert.False(secondProbe.Succeeded);
        Assert.Contains("already running", secondProbe.Message, StringComparison.OrdinalIgnoreCase);

        releaseLightRequest.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(@"{
                ""data"": [{
                    ""on"": {""on"": true},
                    ""dimming"": {""brightness"": 50},
                    ""color"": {""xy"": {""x"": 0.3, ""y"": 0.3}}
                }]
            }")
        });
        await firstProbe;

        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(request =>
                request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath.Contains("/light/", StringComparison.Ordinal)),
            ItExpr.IsAny<CancellationToken>());
    }
}
