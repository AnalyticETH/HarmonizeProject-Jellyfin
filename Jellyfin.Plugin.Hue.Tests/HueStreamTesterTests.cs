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
    public void ApplyTransitionCurve_UsesDeterministicBoundedEasing()
    {
        Assert.Equal(0.25d, HueStreamTester.ApplyTransitionCurve(0.25d, PluginConfiguration.ColorPresetTransitionCurveLinear), 6);
        Assert.Equal(0.0625d, HueStreamTester.ApplyTransitionCurve(0.25d, PluginConfiguration.ColorPresetTransitionCurveEaseIn), 6);
        Assert.Equal(0.4375d, HueStreamTester.ApplyTransitionCurve(0.25d, PluginConfiguration.ColorPresetTransitionCurveEaseOut), 6);
        Assert.Equal(0.15625d, HueStreamTester.ApplyTransitionCurve(0.25d, PluginConfiguration.ColorPresetTransitionCurveSmoothStep), 6);
        Assert.Equal(0.125d, HueStreamTester.ApplyTransitionCurve(0.25d, PluginConfiguration.ColorPresetTransitionCurveEaseInOut), 6);
        Assert.Equal(0d, HueStreamTester.ApplyTransitionCurve(-1d, PluginConfiguration.ColorPresetTransitionCurveEaseOut), 6);
        Assert.Equal(1d, HueStreamTester.ApplyTransitionCurve(2d, PluginConfiguration.ColorPresetTransitionCurveEaseOut), 6);
    }

    [Fact]
    public void BuildTransitionColors_DefaultsToLinearAndAppliesSelectedCurve()
    {
        var target = new Dictionary<int, byte[]> { [1] = new byte[] { 100, 0, 0, 0, 0, 0 } };

        var legacy = HueStreamTester.BuildTransitionColors(target, 0.5d);
        var eased = HueStreamTester.BuildTransitionColors(
            target,
            0.5d,
            PluginConfiguration.ColorPresetTransitionCurveEaseIn);

        Assert.Equal(50, legacy[1][0]);
        Assert.Equal(25, eased[1][0]);
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
    public void BuildEffectColors_AuroraDriftsAcrossGreenBlueAndVioletDeterministically()
    {
        var target = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 127, 127, 127, 127, 127, 127 },
            [2] = new byte[] { 127, 127, 127, 127, 127, 127 }
        };

        var first = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectAurora,
            elapsedSeconds: 0,
            durationSeconds: 5);
        var later = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectAurora,
            elapsedSeconds: 2,
            durationSeconds: 5);
        var repeat = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectAurora,
            elapsedSeconds: 0,
            durationSeconds: 5);

        Assert.Equal(first[1], repeat[1]);
        Assert.NotEqual(first[1], later[1]);
        Assert.True(first[1][2] > first[1][0]);
        Assert.True(first[1][2] > first[1][4]);
        Assert.True(later[1][0] > first[1][0]);
        Assert.True(later[1][2] < first[1][2]);
        Assert.NotEqual(first[1], first[2]);
    }

    [Fact]
    public void BuildEffectColors_FireIsWarmDeterministicAndAnimated()
    {
        var target = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 127, 127, 127, 127, 127, 127 },
            [2] = new byte[] { 127, 127, 127, 127, 127, 127 }
        };

        var first = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectFire,
            elapsedSeconds: 0,
            durationSeconds: 5);
        var later = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectFire,
            elapsedSeconds: 0.7,
            durationSeconds: 5);
        var repeat = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectFire,
            elapsedSeconds: 0,
            durationSeconds: 5);

        Assert.Equal(first[1], repeat[1]);
        Assert.NotEqual(first[1], later[1]);
        Assert.True(first[1][0] > first[1][4]);
        Assert.NotEqual(first[1], first[2]);
        Assert.All(first.Values.SelectMany(frame => frame), value => Assert.InRange(value, 0, 127));
    }

    [Fact]
    public void BuildEffectColors_OceanIsCoolDeterministicAndAnimated()
    {
        var target = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 127, 127, 127, 127, 127, 127 },
            [2] = new byte[] { 127, 127, 127, 127, 127, 127 }
        };

        var first = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectOcean,
            elapsedSeconds: 0,
            durationSeconds: 5);
        var later = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectOcean,
            elapsedSeconds: 3,
            durationSeconds: 5);
        var repeat = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectOcean,
            elapsedSeconds: 0,
            durationSeconds: 5);

        Assert.Equal(first[1], repeat[1]);
        Assert.NotEqual(first[1], later[1]);
        Assert.True(first[1][4] > first[1][0]);
        Assert.NotEqual(first[1], first[2]);
        Assert.All(first.Values.SelectMany(frame => frame), value => Assert.InRange(value, 0, 127));
    }

    [Fact]
    public void BuildEffectColors_LightningIsElectricDeterministicAndChannelPhased()
    {
        var target = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 127, 127, 127, 127, 127, 127 },
            [2] = new byte[] { 127, 127, 127, 127, 127, 127 }
        };

        var first = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectLightning,
            elapsedSeconds: 0,
            durationSeconds: 5);
        var later = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectLightning,
            elapsedSeconds: 0.7,
            durationSeconds: 5);
        var repeat = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectLightning,
            elapsedSeconds: 0,
            durationSeconds: 5);

        Assert.Equal(first[1], repeat[1]);
        Assert.NotEqual(first[1], later[1]);
        Assert.True(first[1][4] > first[1][0]);
        Assert.NotEqual(first[1], first[2]);
        Assert.All(first.Values.SelectMany(frame => frame), value => Assert.InRange(value, 0, 127));
    }

    [Fact]
    public void BuildEffectColors_StarlightIsCoolDeterministicAndChannelPhased()
    {
        var target = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 127, 127, 127, 127, 127, 127 },
            [2] = new byte[] { 127, 127, 127, 127, 127, 127 }
        };

        var first = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectStarlight,
            elapsedSeconds: 0,
            durationSeconds: 5);
        var later = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectStarlight,
            elapsedSeconds: 0.7,
            durationSeconds: 5);
        var repeat = HueStreamTester.BuildEffectColors(
            target,
            PluginConfiguration.ColorPresetEffectStarlight,
            elapsedSeconds: 0,
            durationSeconds: 5);

        Assert.Equal(first[1], repeat[1]);
        Assert.NotEqual(first[1], later[1]);
        Assert.True(first[1][4] > first[1][0]);
        Assert.NotEqual(first[1], first[2]);
        Assert.All(first.Values.SelectMany(frame => frame), value => Assert.InRange(value, 0, 127));
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

    [Fact]
    public async Task PreviewPlaylistAsync_PreflightsEveryStepBeforeBridgeMutation()
    {
        var lifecycleHandler = new PlaylistLifecycleHandler();
        using var httpClient = new HttpClient(lifecycleHandler);
        var streamFactory = new RecordingPreviewStreamFactory();
        var tester = CreatePlaylistTester(httpClient, streamFactory);
        using var document = CreatePlaylistAreaConfiguration();

        var result = await tester.PreviewPlaylistAsync(
            "192.168.1.100",
            "app-key",
            "client-key",
            "area-id",
            document.RootElement,
            null,
            new[]
            {
                CreatePlaylistStep(1, red: 255),
                CreatePlaylistStep(2, red: 0, durationSeconds: PluginConfiguration.MaxPreviewDurationSeconds + 1)
            });

        Assert.False(result.Succeeded);
        var failedStep = Assert.Single(result.Steps);
        Assert.Equal(2, failedStep.Index);
        Assert.Contains("duration", failedStep.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, lifecycleHandler.CaptureCount);
        Assert.Equal(0, lifecycleHandler.ActivationCount);
        Assert.Equal(0, lifecycleHandler.DeactivationCount);
        Assert.Equal(0, lifecycleHandler.RestoreCount);
        Assert.Equal(0, streamFactory.CreateCount);
    }

    [Fact]
    public async Task PreviewPlaylistAsyncForTarget_UsesOneLifecycleForEverySuccessfulStep()
    {
        var lifecycleHandler = new PlaylistLifecycleHandler();
        using var httpClient = new HttpClient(lifecycleHandler);
        var streamFactory = new RecordingPreviewStreamFactory();
        var tester = CreatePlaylistTester(httpClient, streamFactory);
        using var document = CreatePlaylistAreaConfiguration();

        var result = await tester.PreviewPlaylistAsyncForTarget(
            "192.168.1.100",
            "app-key",
            "client-key",
            "area-id",
            document.RootElement,
            null,
            new[]
            {
                CreatePlaylistStep(1, red: 255),
                CreatePlaylistStep(2, red: 0, blue: 255)
            });

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { 1, 2 }, result.Steps.Select(step => step.Index));
        Assert.All(result.Steps, step => Assert.True(step.Succeeded));
        Assert.Contains("one continuous DTLS session", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, lifecycleHandler.CaptureCount);
        Assert.Equal(1, lifecycleHandler.ActivationCount);
        Assert.Equal(1, lifecycleHandler.DeactivationCount);
        Assert.Equal(1, lifecycleHandler.RestoreCount);
        Assert.Equal(1, streamFactory.CreateCount);
        Assert.Equal(1, streamFactory.Stream.StartCount);
        Assert.Equal(1, streamFactory.Stream.StopCount);
        Assert.True(streamFactory.Stream.SendCount >= 2);
    }

    [Fact]
    public async Task PreviewPlaylistAsync_MidSequenceSendFailureStillCleansUpOnce()
    {
        var lifecycleHandler = new PlaylistLifecycleHandler();
        using var httpClient = new HttpClient(lifecycleHandler);
        var streamFactory = new RecordingPreviewStreamFactory(colors =>
            colors.Values.All(frame => frame[2] == 0));
        var tester = CreatePlaylistTester(httpClient, streamFactory);
        using var document = CreatePlaylistAreaConfiguration();

        var result = await tester.PreviewPlaylistAsync(
            "192.168.1.100",
            "app-key",
            "client-key",
            "area-id",
            document.RootElement,
            null,
            new[]
            {
                CreatePlaylistStep(1, red: 255),
                CreatePlaylistStep(2, red: 0, green: 255),
                CreatePlaylistStep(3, red: 0, blue: 255)
            });

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Steps.Count);
        Assert.True(result.Steps[0].Succeeded);
        Assert.False(result.Steps[1].Succeeded);
        Assert.Equal(2, result.Steps[1].Index);
        Assert.Equal(1, lifecycleHandler.CaptureCount);
        Assert.Equal(1, lifecycleHandler.ActivationCount);
        Assert.Equal(1, lifecycleHandler.DeactivationCount);
        Assert.Equal(1, lifecycleHandler.RestoreCount);
        Assert.Equal(1, streamFactory.Stream.StartCount);
        Assert.Equal(1, streamFactory.Stream.StopCount);
    }

    [Fact]
    public async Task PreviewPlaylistAsync_CancelActiveDiagnosticStopsSequenceAndCleansUpOnce()
    {
        var lifecycleHandler = new PlaylistLifecycleHandler();
        using var httpClient = new HttpClient(lifecycleHandler);
        var streamFactory = new RecordingPreviewStreamFactory();
        var tester = CreatePlaylistTester(httpClient, streamFactory);
        using var document = CreatePlaylistAreaConfiguration();

        var preview = tester.PreviewPlaylistAsync(
            "192.168.1.100",
            "app-key",
            "client-key",
            "area-id",
            document.RootElement,
            null,
            new[]
            {
                CreatePlaylistStep(1, red: 255),
                CreatePlaylistStep(2, red: 0, green: 255),
                CreatePlaylistStep(3, red: 0, blue: 255)
            });
        await streamFactory.Stream.FirstSend.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(tester.CancelActiveDiagnostic());
        var result = await preview;

        Assert.False(result.Succeeded);
        Assert.Contains("canceled", result.Message, StringComparison.OrdinalIgnoreCase);
        var canceledStep = Assert.Single(result.Steps);
        Assert.Equal(1, canceledStep.Index);
        Assert.False(canceledStep.Succeeded);
        Assert.Equal(1, lifecycleHandler.CaptureCount);
        Assert.Equal(1, lifecycleHandler.ActivationCount);
        Assert.Equal(1, lifecycleHandler.DeactivationCount);
        Assert.Equal(1, lifecycleHandler.RestoreCount);
        Assert.Equal(1, streamFactory.Stream.StartCount);
        Assert.Equal(1, streamFactory.Stream.StopCount);
    }

    [Fact]
    public async Task PreviewPlaylistAsync_CancelDuringCleanupConvertsSuccessToCanceledResult()
    {
        var lifecycleHandler = new PlaylistLifecycleHandler { BlockDeactivation = true };
        using var httpClient = new HttpClient(lifecycleHandler);
        var streamFactory = new RecordingPreviewStreamFactory();
        var tester = CreatePlaylistTester(httpClient, streamFactory);
        using var document = CreatePlaylistAreaConfiguration();

        var preview = tester.PreviewPlaylistAsync(
            "192.168.1.100",
            "app-key",
            "client-key",
            "area-id",
            document.RootElement,
            null,
            new[] { CreatePlaylistStep(1, red: 255) });
        await lifecycleHandler.DeactivationStarted.WaitAsync(TimeSpan.FromSeconds(5));

        var cancellationAccepted = tester.CancelActiveDiagnostic();
        lifecycleHandler.ReleaseDeactivation();
        var result = await preview;

        Assert.True(cancellationAccepted);
        Assert.False(result.Succeeded);
        Assert.Contains("canceled during cleanup", result.Message, StringComparison.OrdinalIgnoreCase);
        var completedStep = Assert.Single(result.Steps);
        Assert.True(completedStep.Succeeded);
        Assert.Equal(1, lifecycleHandler.CaptureCount);
        Assert.Equal(1, lifecycleHandler.ActivationCount);
        Assert.Equal(1, lifecycleHandler.DeactivationCount);
        Assert.Equal(1, lifecycleHandler.RestoreCount);
        Assert.Equal(1, streamFactory.Stream.StopCount);
    }

    [Fact]
    public async Task PreviewPlaylistAsync_ActivationFailureRestoresCapturedStateWithoutCreatingStream()
    {
        var lifecycleHandler = new PlaylistLifecycleHandler { ActivationSucceeds = false };
        using var httpClient = new HttpClient(lifecycleHandler);
        var streamFactory = new RecordingPreviewStreamFactory();
        var tester = CreatePlaylistTester(httpClient, streamFactory);
        using var document = CreatePlaylistAreaConfiguration();

        var result = await tester.PreviewPlaylistAsync(
            "192.168.1.100",
            "app-key",
            "client-key",
            "area-id",
            document.RootElement,
            null,
            new[] { CreatePlaylistStep(1, red: 255) });

        Assert.False(result.Succeeded);
        Assert.Contains("being restored", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, lifecycleHandler.CaptureCount);
        Assert.Equal(1, lifecycleHandler.ActivationCount);
        Assert.Equal(1, lifecycleHandler.DeactivationCount);
        Assert.Equal(1, lifecycleHandler.RestoreCount);
        Assert.Equal(0, streamFactory.CreateCount);
    }

    private static HueStreamTester CreatePlaylistTester(
        HttpClient httpClient,
        IHuePreviewStreamFactory streamFactory)
    {
        var hueClient = new HueClient(httpClient, Mock.Of<ILogger<HueClient>>())
        {
            RetryAttempts = 0
        };
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(factory => factory.CreateLogger(It.IsAny<string>()))
            .Returns(Mock.Of<ILogger>());
        return new HueStreamTester(
            hueClient,
            loggerFactory.Object,
            Mock.Of<ILogger<HueStreamTester>>(),
            new HueBridgeLifecycleGate(),
            streamFactory);
    }

    private static JsonDocument CreatePlaylistAreaConfiguration()
        => JsonDocument.Parse(
            "{\"channels\":[{\"channel_id\":1,\"members\":[{\"service\":{\"rid\":\"light-1\"}}]}]}");

    private static HuePlaylistPreviewStep CreatePlaylistStep(
        int index,
        int red,
        int green = 0,
        int blue = 0,
        int durationSeconds = 1)
        => new()
        {
            Index = index,
            Red = red,
            Green = green,
            Blue = blue,
            BrightnessPercent = 50,
            DurationSeconds = durationSeconds,
            Effect = PluginConfiguration.ColorPresetEffectSolid,
            EffectSpeedPercent = PluginConfiguration.DefaultColorPresetEffectSpeedPercent,
            TransitionCurve = PluginConfiguration.ColorPresetTransitionCurveLinear
        };

    private sealed class PlaylistLifecycleHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _deactivationStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseDeactivation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ActivationSucceeds { get; init; } = true;
        public bool BlockDeactivation { get; init; }
        public int CaptureCount { get; private set; }
        public int ActivationCount { get; private set; }
        public int DeactivationCount { get; private set; }
        public int RestoreCount { get; private set; }
        public Task DeactivationStarted => _deactivationStarted.Task;

        public void ReleaseDeactivation() => _releaseDeactivation.TrySetResult(true);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath.Contains("/light/", StringComparison.Ordinal))
            {
                CaptureCount++;
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
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                if (body.Contains("\"start\"", StringComparison.Ordinal))
                {
                    ActivationCount++;
                    return new HttpResponseMessage(
                        ActivationSucceeds ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent("{}")
                    };
                }

                if (body.Contains("\"stop\"", StringComparison.Ordinal))
                {
                    DeactivationCount++;
                    _deactivationStarted.TrySetResult(true);
                    if (BlockDeactivation)
                        await _releaseDeactivation.Task.ConfigureAwait(false);
                }
                else if (request.RequestUri!.AbsolutePath.Contains("/light/", StringComparison.Ordinal))
                    RestoreCount++;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
        }
    }

    private sealed class RecordingPreviewStreamFactory : IHuePreviewStreamFactory
    {
        public RecordingPreviewStreamFactory(
            Func<Dictionary<int, byte[]>, bool>? sendResult = null)
        {
            Stream = new RecordingPreviewStream(sendResult);
        }

        public int CreateCount { get; private set; }
        public RecordingPreviewStream Stream { get; }

        public IHuePreviewStream Create()
        {
            CreateCount++;
            return Stream;
        }
    }

    private sealed class RecordingPreviewStream : IHuePreviewStream
    {
        private readonly Func<Dictionary<int, byte[]>, bool> _sendResult;

        public RecordingPreviewStream(
            Func<Dictionary<int, byte[]>, bool>? sendResult)
        {
            _sendResult = sendResult ?? (_ => true);
        }

        public Func<CancellationToken, Task<bool>>? OnBeforeReconnectWithCancellation
        {
            set { }
        }
        public int StartCount { get; private set; }
        public int SendCount { get; private set; }
        public int StopCount { get; private set; }
        public Task FirstSend => _firstSend.Task;

        private readonly TaskCompletionSource<bool> _firstSend =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartStreamAsync(
            string bridgeIp,
            string appKey,
            string clientKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            return Task.CompletedTask;
        }

        public bool IsHealthy() => true;

        public Task<bool> SendColors(
            string areaId,
            Dictionary<int, byte[]> channelColors,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendCount++;
            _firstSend.TrySetResult(true);
            return Task.FromResult(_sendResult(channelColors));
        }

        public void StopStream()
        {
            StopCount++;
        }
    }
}
