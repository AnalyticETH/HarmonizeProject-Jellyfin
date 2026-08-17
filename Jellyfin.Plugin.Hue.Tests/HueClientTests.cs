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
/// Integration tests for HueClient REST API communication.
/// These tests verify JSON parsing, error handling, and retry logic.
/// </summary>
public class HueClientTests : IDisposable
{
    private readonly Mock<ILogger<HueClient>> _loggerMock;
    private readonly Mock<HttpMessageHandler> _httpHandlerMock;
    private readonly HttpClient _httpClient;

    public HueClientTests()
    {
        _loggerMock = new Mock<ILogger<HueClient>>();
        _httpHandlerMock = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_httpHandlerMock.Object)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    #region DiscoverBridgeIp Tests

    [Fact]
    public async Task DiscoverBridgeIp_ValidResponse_ReturnsIpAddress()
    {
        // Arrange
        var responseJson = @"[{""id"":""ecb5fafffe123456"",""internalipaddress"":""192.168.1.100"",""port"":443}]";
        SetupHttpResponse(HttpStatusCode.OK, responseJson);

        var client = new HueClient(_httpClient, _loggerMock.Object);

        // Act
        var result = await client.DiscoverBridgeIp();

        // Assert
        Assert.Equal("192.168.1.100", result);
    }

    [Fact]
    public async Task DiscoverBridgeIp_EmptyArray_ReturnsEmptyString()
    {
        // Arrange
        SetupHttpResponse(HttpStatusCode.OK, "[]");
        var client = new HueClient(_httpClient, _loggerMock.Object);

        // Act
        var result = await client.DiscoverBridgeIp();

        // Assert
        Assert.Equal("", result);
    }

    [Fact]
    public async Task DiscoverBridgeIp_InvalidJson_ReturnsEmptyString()
    {
        // Arrange
        SetupHttpResponse(HttpStatusCode.OK, "not valid json");
        var client = new HueClient(_httpClient, _loggerMock.Object);

        // Act
        var result = await client.DiscoverBridgeIp();

        // Assert
        Assert.Equal("", result);
    }

    [Fact]
    public async Task DiscoverBridgeIp_NetworkError_ReturnsEmptyString()
    {
        // Arrange
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        var client = new HueClient(_httpClient, _loggerMock.Object);

        // Act
        var result = await client.DiscoverBridgeIp();

        // Assert
        Assert.Equal("", result);
    }

    [Fact]
    public async Task DiscoverBridgeIp_MultipleBridges_ReturnsFirstIp()
    {
        // Arrange
        var responseJson = @"[
            {""id"":""bridge1"",""internalipaddress"":""192.168.1.100""},
            {""id"":""bridge2"",""internalipaddress"":""192.168.1.101""}
        ]";
        SetupHttpResponse(HttpStatusCode.OK, responseJson);

        var client = new HueClient(_httpClient, _loggerMock.Object);

        // Act
        var result = await client.DiscoverBridgeIp();

        // Assert
        Assert.Equal("192.168.1.100", result);
    }

    [Fact]
    public async Task DiscoverBridgeIp_SkipsMalformedEntries()
    {
        var responseJson = @"[
            {""internalipaddress"":""not-an-ip""},
            {""internalipaddress"":""192.168.1.101""}
        ]";
        SetupHttpResponse(HttpStatusCode.OK, responseJson);

        var client = new HueClient(_httpClient, _loggerMock.Object);

        var result = await client.DiscoverBridgeIp();

        Assert.Equal("192.168.1.101", result);
    }

    #endregion

    #region GetEntertainmentAreas Tests

    [Fact]
    public async Task GetEntertainmentAreas_ValidResponse_ReturnsAreaList()
    {
        // Arrange
        var responseJson = @"{
            ""data"": [
                {""id"": ""area-1"", ""metadata"": {""name"": ""Living Room""}},
                {""id"": ""area-2"", ""metadata"": {""name"": ""Bedroom""}}
            ]
        }";
        SetupHttpResponse(HttpStatusCode.OK, responseJson);

        var client = new HueClient(_httpClient, _loggerMock.Object);

        // Act
        var result = await client.GetEntertainmentAreas("192.168.1.100", "test-app-key");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("area-1", result[0].Id);
        Assert.Equal("Living Room", result[0].Name);
        Assert.Equal("area-2", result[1].Id);
        Assert.Equal("Bedroom", result[1].Name);
    }

    [Fact]
    public async Task GetEntertainmentAreas_EmptyData_ReturnsEmptyList()
    {
        // Arrange
        var responseJson = @"{""data"": []}";
        SetupHttpResponse(HttpStatusCode.OK, responseJson);

        var client = new HueClient(_httpClient, _loggerMock.Object);

        // Act
        var result = await client.GetEntertainmentAreas("192.168.1.100", "test-app-key");

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetEntertainmentAreas_MissingMetadata_HandlesGracefully()
    {
        // Arrange
        var responseJson = @"{
            ""data"": [
                {""id"": ""area-1""}
            ]
        }";
        SetupHttpResponse(HttpStatusCode.OK, responseJson);

        var client = new HueClient(_httpClient, _loggerMock.Object);

        // Act
        var result = await client.GetEntertainmentAreas("192.168.1.100", "test-app-key");

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("area-1", result[0].Id);
    }

    #endregion

    #region GetEntertainmentConfiguration Tests

    [Fact]
    public async Task GetEntertainmentConfiguration_ValidResponse_ReturnsConfiguration()
    {
        // Arrange
        var responseJson = @"{
            ""data"": [{
                ""id"": ""area-1"",
                ""channels"": [
                    {
                        ""channel_id"": 0,
                        ""position"": {""x"": -1.0, ""y"": 0.0, ""z"": 1.0},
                        ""members"": [{""service"": {""rid"": ""light-1"", ""rtype"": ""light""}}]
                    },
                    {
                        ""channel_id"": 1,
                        ""position"": {""x"": 1.0, ""y"": 0.0, ""z"": 1.0},
                        ""members"": [{""service"": {""rid"": ""light-2"", ""rtype"": ""light""}}]
                    }
                ]
            }]
        }";
        SetupHttpResponse(HttpStatusCode.OK, responseJson);

        var client = new HueClient(_httpClient, _loggerMock.Object);

        // Act
        var result = await client.GetEntertainmentConfiguration("192.168.1.100", "test-app-key", "area-1");

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Value.TryGetProperty("channels", out var channels));
        Assert.Equal(2, channels.GetArrayLength());
    }

    [Theory]
    [InlineData(@"{}")]
    [InlineData(@"{""data"":[]}")]
    [InlineData(@"{""data"":{}}")]
    public async Task GetEntertainmentConfiguration_MissingArea_ReturnsNull(string responseJson)
    {
        SetupHttpResponse(HttpStatusCode.OK, responseJson);
        var client = new HueClient(_httpClient, _loggerMock.Object);

        var result = await client.GetEntertainmentConfiguration("192.168.1.100", "test-app-key", "area-1");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetEntertainmentConfiguration_InvalidJson_ReturnsNull()
    {
        SetupHttpResponse(HttpStatusCode.OK, "not valid json");
        var client = new HueClient(_httpClient, _loggerMock.Object);

        var result = await client.GetEntertainmentConfiguration("192.168.1.100", "test-app-key", "area-1");

        Assert.Null(result);
    }

    [Fact]
    public async Task StartEntertainmentArea_TransientServerError_RetriesAndSucceeds()
    {
        _httpHandlerMock.Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("busy", Encoding.UTF8, "text/plain")
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });

        var client = new HueClient(_httpClient, _loggerMock.Object)
        {
            RetryAttempts = 1
        };

        var result = await client.StartEntertainmentArea("192.168.1.100", "test-app-key", "area-uuid");

        Assert.True(result);
        _httpHandlerMock.Protected().Verify(
            "SendAsync",
            Times.Exactly(2),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetEntertainmentAreas_InvalidJson_ReturnsNull()
    {
        SetupHttpResponse(HttpStatusCode.OK, "not valid json");
        var client = new HueClient(_httpClient, _loggerMock.Object);

        var result = await client.GetEntertainmentAreas("192.168.1.100", "test-app-key");

        Assert.Null(result);
    }

    #endregion

    #region GetLightStates Tests

    [Fact]
    public async Task GetLightStates_ValidConfiguration_ReturnsLightStates()
    {
        // Arrange
        using var doc = JsonDocument.Parse(@"{
            ""channels"": [
                {
                    ""channel_id"": 0,
                    ""members"": [{""service"": {""rid"": ""light-1""}}]
                }
            ]
        }");
        var configJson = doc.RootElement;

        var lightStateJson = @"{
            ""data"": [{
                ""on"": {""on"": true},
                ""dimming"": {""brightness"": 75.5},
                ""color"": {""xy"": {""x"": 0.3127, ""y"": 0.329}}
            }]
        }";
        SetupHttpResponse(HttpStatusCode.OK, lightStateJson);

        var client = new HueClient(_httpClient, _loggerMock.Object);

        // Act
        var result = await client.GetLightStates("192.168.1.100", "test-app-key", configJson);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("light-1", result[0].Id);
        Assert.True(result[0].IsOn);
        Assert.Equal(75, result[0].Brightness); // Converted to int
        Assert.Equal(0.3127, result[0].X, 4);
        Assert.Equal(0.329, result[0].Y, 3);
    }

    [Fact]
    public async Task GetLightStates_NoChannels_ReturnsEmptyList()
    {
        // Arrange
        using var doc = JsonDocument.Parse(@"{""channels"": []}");
        var configJson = doc.RootElement;

        var client = new HueClient(_httpClient, _loggerMock.Object);

        // Act
        var result = await client.GetLightStates("192.168.1.100", "test-app-key", configJson);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    #endregion

    #region RestoreLightStates Tests

    [Fact]
    public async Task RestoreLightStates_ValidStates_SendsCorrectPayloads()
    {
        // Arrange
        var lightStates = new List<HueClient.LightState>
        {
            new("light-1", true, 80, 0.3, 0.33),
            new("light-2", false, 50, 0.4, 0.4)
        };

        var capturedRequests = new List<HttpRequestMessage>();
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) =>
            {
                capturedRequests.Add(req);
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });

        var client = new HueClient(_httpClient, _loggerMock.Object);

        // Act
        await client.RestoreLightStates("192.168.1.100", "test-app-key", lightStates);

        // Assert
        Assert.Equal(2, capturedRequests.Count);
        Assert.All(capturedRequests, req => Assert.Equal(HttpMethod.Put, req.Method));
    }

    [Fact]
    public async Task RestoreLightStates_EmptyList_DoesNothing()
    {
        // Arrange
        var lightStates = new List<HueClient.LightState>();
        var requestCount = 0;

        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((_, _) => requestCount++)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        var client = new HueClient(_httpClient, _loggerMock.Object);

        // Act
        await client.RestoreLightStates("192.168.1.100", "test-app-key", lightStates);

        // Assert
        Assert.Equal(0, requestCount);
    }

    #endregion

    #region RegisterWithBridge Tests

    [Fact]
    public async Task RegisterWithBridge_LinkButtonPressed_ReturnsCredentials()
    {
        // Arrange
        var responseJson = @"[{""success"":{""username"":""test-username"",""clientkey"":""test-clientkey""}}]";
        SetupHttpResponse(HttpStatusCode.OK, responseJson);

        var client = new HueClient(_httpClient, _loggerMock.Object);

        // Act
        var result = await client.RegisterWithBridge("192.168.1.100");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("test-username", result.Username);
        Assert.Equal("test-clientkey", result.ClientKey);
    }

    [Fact]
    public async Task RegisterWithBridge_LinkButtonNotPressed_ReturnsNull()
    {
        // Arrange
        var responseJson = @"[{""error"":{""type"":101,""description"":""link button not pressed""}}]";
        SetupHttpResponse(HttpStatusCode.OK, responseJson);

        var client = new HueClient(_httpClient, _loggerMock.Object);

        // Act
        var result = await client.RegisterWithBridge("192.168.1.100");

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region Helper Methods

    private void SetupHttpResponse(HttpStatusCode statusCode, string content)
    {
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
    }

    #endregion
}
