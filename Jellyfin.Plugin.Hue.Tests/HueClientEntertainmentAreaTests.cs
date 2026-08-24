using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Hue.Api;
using Jellyfin.Plugin.Hue.Hue;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

/// <summary>
/// Tests for HueClient entertainment area activation/deactivation and registration URL scheme.
/// These cover the critical new methods added to fix the DTLS streaming bug.
/// </summary>
public class HueClientEntertainmentAreaTests : IDisposable
{
    private readonly Mock<ILogger<HueClient>> _loggerMock;
    private readonly Mock<HttpMessageHandler> _httpHandlerMock;
    private readonly HttpClient _httpClient;

    public HueClientEntertainmentAreaTests()
    {
        _loggerMock = new Mock<ILogger<HueClient>>();
        _httpHandlerMock = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_httpHandlerMock.Object) { Timeout = TimeSpan.FromSeconds(10) };
    }

    public void Dispose() => _httpClient.Dispose();

    #region RegisterWithBridge URL Scheme Tests

    [Fact]
    public async Task RegisterWithBridge_UsesHttps()
    {
        // Current Hue firmware requires the local API to be accessed over TLS.
        HttpRequestMessage? capturedRequest = null;
        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    @"[{""success"":{""username"":""u"",""clientkey"":""k""}}]",
                    Encoding.UTF8, "application/json")
            });

        var client = new HueClient(_httpClient, _loggerMock.Object);
        await client.RegisterWithBridge("192.168.1.100");

        Assert.NotNull(capturedRequest);
        Assert.Equal("https", capturedRequest!.RequestUri!.Scheme);
        Assert.Contains("/api", capturedRequest.RequestUri.AbsolutePath);
    }

    [Fact]
    public async Task RegisterWithBridge_ScopedLinkLocalAddressPreservesZoneInUri()
    {
        HttpRequestMessage? capturedRequest = null;
        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[{\"success\":{\"username\":\"u\",\"clientkey\":\"k\"}}]",
                    Encoding.UTF8, "application/json")
            });

        var client = new HueClient(_httpClient, _loggerMock.Object);
        await client.RegisterWithBridge("fe80::50%42");

        Assert.NotNull(capturedRequest);
        Assert.Equal("fe80::50%42", capturedRequest!.RequestUri!.DnsSafeHost);
    }

    #endregion

    #region StartEntertainmentArea Tests

    [Fact]
    public async Task StartEntertainmentArea_BridgeReturns200_ReturnsTrue()
    {
        SetupHttpResponse(HttpStatusCode.OK, "{}");
        var client = new HueClient(_httpClient, _loggerMock.Object);

        var result = await client.StartEntertainmentArea("192.168.1.100", "test-app-key", "area-uuid");

        Assert.True(result);
    }

    [Fact]
    public async Task StartEntertainmentArea_BridgeReturns403_ReturnsFalse()
    {
        // 403 means invalid app key or area already streaming — must return false so
        // HueSyncService aborts instead of opening a DTLS tunnel that won't work
        SetupHttpResponse(HttpStatusCode.Forbidden, @"{""errors"":[{""description"":""Not authorized""}]}");
        var client = new HueClient(_httpClient, _loggerMock.Object);

        var result = await client.StartEntertainmentArea("192.168.1.100", "test-app-key", "area-uuid");

        Assert.False(result);
    }

    [Fact]
    public async Task StartEntertainmentArea_FailureDoesNotLogBridgeResponseBody()
    {
        const string secretSentinel = "start-response-secret";
        SetupHttpResponse(
            HttpStatusCode.Forbidden,
            $"{{\"errors\":[{{\"description\":\"{secretSentinel}\"}}]}}");
        var client = new HueClient(_httpClient, _loggerMock.Object);

        Assert.False(await client.StartEntertainmentArea("192.168.1.100", "test-app-key", "area-uuid"));

        Assert.DoesNotContain(secretSentinel, GetLoggerText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartEntertainmentArea_SendsPutWithActionStart()
    {
        // CRITICAL: The bridge requires PUT {"action":"start"} to activate streaming mode.
        // Without this, the bridge silently ignores all DTLS packets.
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>(async (req, _) =>
            {
                capturedRequest = req;
                capturedBody = req.Content != null ? await req.Content.ReadAsStringAsync() : null;
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });

        var client = new HueClient(_httpClient, _loggerMock.Object);
        await client.StartEntertainmentArea("192.168.1.100", "my-app-key", "my-area-id");

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Put, capturedRequest!.Method);
        Assert.Contains("my-area-id", capturedRequest.RequestUri!.AbsolutePath);
        Assert.Contains("entertainment_configuration", capturedRequest.RequestUri.AbsolutePath);
        Assert.Contains("hue-application-key", capturedRequest.Headers.Select(h => h.Key));
        Assert.NotNull(capturedBody);
        Assert.Contains("\"start\"", capturedBody);
    }

    [Fact]
    public async Task StartEntertainmentArea_BridgeNetworkError_ReturnsFalse()
    {
        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network unreachable"));

        var client = new HueClient(_httpClient, _loggerMock.Object);

        var result = await client.StartEntertainmentArea("192.168.1.100", "test-app-key", "area-uuid");

        Assert.False(result);
    }

    #endregion

    #region StopEntertainmentArea Tests

    [Fact]
    public async Task StopEntertainmentArea_SendsPutWithActionStop()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>(async (req, _) =>
            {
                capturedRequest = req;
                capturedBody = req.Content != null ? await req.Content.ReadAsStringAsync() : null;
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });

        var client = new HueClient(_httpClient, _loggerMock.Object);
        await client.StopEntertainmentArea("192.168.1.100", "my-app-key", "my-area-id");

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Put, capturedRequest!.Method);
        Assert.Contains("my-area-id", capturedRequest.RequestUri!.AbsolutePath);
        Assert.NotNull(capturedBody);
        Assert.Contains("\"stop\"", capturedBody);
    }

    [Fact]
    public async Task StopEntertainmentArea_NetworkError_DoesNotThrow()
    {
        // StopEntertainmentArea must not throw even if the bridge is unreachable —
        // stop is best-effort and should never crash the service shutdown path
        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network unreachable"));

        var client = new HueClient(_httpClient, _loggerMock.Object);

        // Must complete without throwing
        await client.StopEntertainmentArea("192.168.1.100", "test-app-key", "area-uuid");
    }

    [Fact]
    public async Task StopEntertainmentAreaWithResult_ReportsFailure()
    {
        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var client = new HueClient(_httpClient, _loggerMock.Object)
        {
            RetryAttempts = 0
        };

        var result = await client.StopEntertainmentAreaWithResult(
            "192.168.1.100",
            "test-app-key",
            "area-uuid");

        Assert.False(result);
    }

    [Fact]
    public async Task StopEntertainmentAreaWithResult_FailureDoesNotLogBridgeResponseBody()
    {
        const string secretSentinel = "stop-response-secret";
        SetupHttpResponse(
            HttpStatusCode.BadRequest,
            $"{{\"errors\":[{{\"description\":\"{secretSentinel}\"}}]}}");
        var client = new HueClient(_httpClient, _loggerMock.Object)
        {
            RetryAttempts = 0
        };

        Assert.False(await client.StopEntertainmentAreaWithResult(
            "192.168.1.100",
            "test-app-key",
            "area-uuid"));

        Assert.DoesNotContain(secretSentinel, GetLoggerText(), StringComparison.Ordinal);
    }

    #endregion

    #region Helper Methods

    private void SetupHttpResponse(HttpStatusCode statusCode, string content)
    {
        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
    }

    private string GetLoggerText()
    {
        return string.Join(
            "\n",
            _loggerMock.Invocations.Select(invocation =>
                string.Join(" ", invocation.Arguments.Select(argument => argument?.ToString() ?? string.Empty))));
    }

    #endregion
}
