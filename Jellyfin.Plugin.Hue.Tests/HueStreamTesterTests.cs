using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Jellyfin.Plugin.Hue.Configuration;
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
    public void BuildTransitionColors_RampsAndClampsTargetFrames()
    {
        var target = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 0, 20, 40, 60, 100, 127 }
        };

        var midpoint = HueStreamTester.BuildTransitionColors(target, 0.5);
        var completed = HueStreamTester.BuildTransitionColors(target, 2);
        var initial = HueStreamTester.BuildTransitionColors(target, -1);

        Assert.Equal(new byte[] { 0, 10, 20, 30, 50, 64 }, midpoint[1]);
        Assert.Equal(target[1], completed[1]);
        Assert.Equal(new byte[6], initial[1]);
        Assert.NotSame(target[1], completed[1]);
    }

    [Fact]
    public void BuildEffectColors_PulseModulatesTheSelectedFrame()
    {
        var target = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 63, 63, 32, 32, 0, 0 }
        };

        var dim = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectPulse,
            elapsedSeconds: 0,
            durationSeconds: 5);
        var midpoint = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectPulse,
            elapsedSeconds: 0.6,
            durationSeconds: 5);

        Assert.Equal(new byte[] { 13, 13, 6, 6, 0, 0 }, dim[1]);
        Assert.Equal(new byte[] { 38, 38, 19, 19, 0, 0 }, midpoint[1]);
        Assert.NotSame(target[1], dim[1]);
    }

    [Fact]
    public void BuildEffectColors_RainbowRotatesFromTheSelectedColorHue()
    {
        var target = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 63, 63, 0, 0, 0, 0 }
        };

        var start = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectRainbow,
            elapsedSeconds: 0,
            durationSeconds: 1);
        var green = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectRainbow,
            elapsedSeconds: 1d / 3d,
            durationSeconds: 1);

        Assert.Equal(new byte[] { 63, 63, 0, 0, 0, 0 }, start[1]);
        Assert.Equal(new byte[] { 0, 0, 63, 63, 0, 0 }, green[1]);
    }

    [Fact]
    public void BuildEffectColors_AnimatedSpeedChangesThePhase()
    {
        var target = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 63, 63, 0, 0, 0, 0 }
        };

        var normal = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectRainbow,
            elapsedSeconds: 0.25,
            durationSeconds: 1,
            effectSpeedPercent: PluginConfiguration.DefaultColorPresetEffectSpeedPercent);
        var fast = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectRainbow,
            elapsedSeconds: 0.25,
            durationSeconds: 1,
            effectSpeedPercent: PluginConfiguration.MaxColorPresetEffectSpeedPercent);
        var slow = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectRainbow,
            elapsedSeconds: 0.25,
            durationSeconds: 1,
            effectSpeedPercent: PluginConfiguration.MinColorPresetEffectSpeedPercent);

        Assert.NotEqual(normal[1], fast[1]);
        Assert.NotEqual(normal[1], slow[1]);
    }

    [Fact]
    public void BuildEffectColors_CandleIsWarmDeterministicAndAnimated()
    {
        var target = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 127, 127, 127, 127, 127, 127 },
            [2] = new byte[] { 127, 127, 127, 127, 127, 127 }
        };

        var first = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectCandle,
            elapsedSeconds: 0,
            durationSeconds: 5);
        var changed = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectCandle,
            elapsedSeconds: 0.41,
            durationSeconds: 5);
        var repeat = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectCandle,
            elapsedSeconds: 0,
            durationSeconds: 5);

        Assert.Equal(first[1], repeat[1]);
        Assert.NotEqual(first[1], changed[1]);
        Assert.True(first[1][0] > first[1][2]);
        Assert.True(first[1][2] > first[1][4]);
        Assert.NotEqual(first[1], first[2]);
    }

    [Fact]
    public void BuildEffectColors_TemperatureSweepsWarmToCoolDeterministically()
    {
        var target = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 127, 127, 127, 127, 127, 127 }
        };

        var warm = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectTemperature,
            elapsedSeconds: 0,
            durationSeconds: 5);
        var cool = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectTemperature,
            elapsedSeconds: 4,
            durationSeconds: 5);
        var repeat = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectTemperature,
            elapsedSeconds: 0,
            durationSeconds: 5);

        Assert.Equal(warm[1], repeat[1]);
        Assert.NotEqual(warm[1], cool[1]);
        Assert.True(warm[1][0] > warm[1][4]);
        Assert.True(cool[1][4] > warm[1][4]);
        Assert.True(cool[1][2] > warm[1][2]);
    }

    [Fact]
    public async Task PreviewAsync_RejectsTransitionLongerThanDurationWithoutTouchingBridge()
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

        var result = await tester.PreviewAsync(
            "192.168.1.100",
            "app-key",
            "client-key",
            "area-id",
            document.RootElement,
            null,
            255,
            255,
            255,
            100,
            4,
            transitionSeconds: 5);

        Assert.False(result.Succeeded);
        Assert.Contains("cannot exceed", result.Message, StringComparison.OrdinalIgnoreCase);
        handler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PreviewAsync_RejectsCombinedFadeDurationsWithoutTouchingBridge()
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

        var result = await tester.PreviewAsync(
            "192.168.1.100",
            "app-key",
            "client-key",
            "area-id",
            document.RootElement,
            null,
            255,
            255,
            255,
            100,
            4,
            transitionSeconds: 2,
            transitionOutSeconds: 3);

        Assert.False(result.Succeeded);
        Assert.Contains("together", result.Message, StringComparison.OrdinalIgnoreCase);
        handler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PreviewAsync_RejectsEffectSpeedOutsideBoundsWithoutTouchingBridge()
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

        var result = await tester.PreviewAsync(
            "192.168.1.100",
            "app-key",
            "client-key",
            "area-id",
            document.RootElement,
            null,
            255,
            255,
            255,
            100,
            4,
            effectSpeedPercent: PluginConfiguration.MaxColorPresetEffectSpeedPercent + 1);

        Assert.False(result.Succeeded);
        Assert.Contains("effect speed", result.Message, StringComparison.OrdinalIgnoreCase);
        handler.VerifyNoOtherCalls();
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
    public async Task TestAsync_CancelActiveDiagnosticDuringCaptureDoesNotActivateBridge()
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
        var probeTask = tester.TestAsync(
            "192.168.1.100",
            "app-key",
            "client-key",
            "area-id",
            document.RootElement,
            cancellationToken: CancellationToken.None);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(tester.CancelActiveDiagnostic());

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
        Assert.False(tester.CancelActiveDiagnostic());
    }

    [Fact]
    public async Task TestAsync_WhenCanceledDuringActivationStillRestoresBridgeState()
    {
        var handler = new Mock<HttpMessageHandler>();
        var activationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restoreCount = 0;
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(async (request, cancellationToken) =>
            {
                if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.Contains("/light/", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(@"{
                            ""data"": [{
                                ""on"": {""on"": true},
                                ""dimming"": {""brightness"": 50},
                                ""color"": {""xy"": {""x"": 0.3, ""y"": 0.3}}
                            }]
                        }")
                    };
                }

                if (request.Method == HttpMethod.Put)
                {
                    var body = request.Content == null
                        ? string.Empty
                        : await request.Content.ReadAsStringAsync();
                    if (body.Contains("\"start\"", StringComparison.Ordinal))
                    {
                        activationStarted.TrySetResult(true);
                        return await new TaskCompletionSource<HttpResponseMessage>().Task.WaitAsync(cancellationToken);
                    }

                    if (body.Contains("\"stop\"", StringComparison.Ordinal))
                    {
                        stopSent.TrySetResult(true);
                    }
                    else if (request.RequestUri!.AbsolutePath.Contains("/light/", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref restoreCount);
                    }
                }

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
        await activationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellationSource.Cancel();

        var result = await probeTask;
        await stopSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Succeeded);
        Assert.Contains("canceled", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, Volatile.Read(ref restoreCount));
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
    public async Task TestAsync_WhenActivationFailsStillCleansUpCapturedState()
    {
        var handler = new Mock<HttpMessageHandler>();
        var stopCount = 0;
        var restoreCount = 0;
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(async (request, _) =>
            {
                if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.Contains("/light/", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(@"{
                            ""data"": [{
                                ""on"": {""on"": true},
                                ""dimming"": {""brightness"": 50},
                                ""color"": {""xy"": {""x"": 0.3, ""y"": 0.3}}
                            }]
                        }")
                    };
                }

                if (request.Method == HttpMethod.Put)
                {
                    var body = request.Content == null
                        ? string.Empty
                        : await request.Content.ReadAsStringAsync();
                    if (body.Contains("\"start\"", StringComparison.Ordinal))
                    {
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    }

                    if (body.Contains("\"stop\"", StringComparison.Ordinal))
                    {
                        stopCount++;
                    }
                    else if (request.RequestUri!.AbsolutePath.Contains("/light/", StringComparison.Ordinal))
                    {
                        restoreCount++;
                    }
                }

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

        var result = await tester.TestAsync(
            "192.168.1.100",
            "app-key",
            "client-key",
            "area-id",
            document.RootElement);

        Assert.False(result.Succeeded);
        Assert.Contains("being restored", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, stopCount);
        Assert.Equal(1, restoreCount);
    }

    [Fact]
    public async Task PreviewAsync_WhenActivationFailsStillCleansUpCapturedState()
    {
        var handler = new Mock<HttpMessageHandler>();
        var stopCount = 0;
        var restoreCount = 0;
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(async (request, _) =>
            {
                if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.Contains("/light/", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(@"{
                            ""data"": [{
                                ""on"": {""on"": true},
                                ""dimming"": {""brightness"": 50},
                                ""color"": {""xy"": {""x"": 0.3, ""y"": 0.3}}
                            }]
                        }")
                    };
                }

                if (request.Method == HttpMethod.Put)
                {
                    var body = request.Content == null
                        ? string.Empty
                        : await request.Content.ReadAsStringAsync();
                    if (body.Contains("\"start\"", StringComparison.Ordinal))
                    {
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    }

                    if (body.Contains("\"stop\"", StringComparison.Ordinal))
                    {
                        stopCount++;
                    }
                    else if (request.RequestUri!.AbsolutePath.Contains("/light/", StringComparison.Ordinal))
                    {
                        restoreCount++;
                    }
                }

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

        var result = await tester.PreviewAsync(
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

        Assert.False(result.Succeeded);
        Assert.Contains("being restored", result.Message, StringComparison.OrdinalIgnoreCase);
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
