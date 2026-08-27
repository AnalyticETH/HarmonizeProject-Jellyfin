using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Hue.Api;
using Jellyfin.Plugin.Hue.Hue;
using Jellyfin.Plugin.Hue.Service;
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

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(10, 10)]
    [InlineData(11, 10)]
    [InlineData(int.MaxValue, 10)]
    public void RetryAttempts_IsClampedToBoundedNetworkPolicy(int configured, int expected)
    {
        var client = new HueClient(_httpClient, _loggerMock.Object)
        {
            RetryAttempts = configured
        };

        Assert.Equal(expected, client.RetryAttempts);
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
    public async Task DiscoverBridgeIp_WhenCloudDiscoveryFailsUsesLocalDiscovery()
    {
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Cloud discovery unavailable"));
        var localDiscovery = new Mock<IHueBridgeLocalDiscovery>();
        localDiscovery
            .Setup(discovery => discovery.DiscoverAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "192.168.1.120" });

        var client = new HueClient(_httpClient, _loggerMock.Object, localDiscovery.Object);

        var result = await client.DiscoverBridgeIp();

        Assert.Equal("192.168.1.120", result);
        localDiscovery.Verify(
            discovery => discovery.DiscoverAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DiscoverBridgeIp_LocalDiscoverySkipsPublicAddresses()
    {
        SetupHttpResponse(HttpStatusCode.OK, "[]");
        var localDiscovery = new Mock<IHueBridgeLocalDiscovery>();
        localDiscovery
            .Setup(discovery => discovery.DiscoverAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "8.8.8.8", "192.168.1.121" });

        var client = new HueClient(_httpClient, _loggerMock.Object, localDiscovery.Object);

        var result = await client.DiscoverBridgeIp();

        Assert.Equal("192.168.1.121", result);
    }

    [Fact]
    public async Task DiscoverBridgeIp_WhenCanceled_PropagatesCancellation()
    {
        SetupHttpResponse(HttpStatusCode.OK, "[]");
        var client = new HueClient(_httpClient, _loggerMock.Object);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.DiscoverBridgeIp(cancellationSource.Token));
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
    public async Task DiscoverBridgeIps_CombinesDistinctCloudAndLocalBridges()
    {
        SetupHttpResponse(HttpStatusCode.OK, @"[
            {""id"":""bridge1"",""internalipaddress"":""192.168.1.100""},
            {""id"":""bridge2"",""internalipaddress"":""192.168.1.101""},
            {""id"":""public"",""internalipaddress"":""8.8.8.8""},
            {""id"":""duplicate"",""internalipaddress"":""192.168.1.100""}
        ]");
        var localDiscovery = new Mock<IHueBridgeLocalDiscovery>();
        localDiscovery
            .Setup(discovery => discovery.DiscoverAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "192.168.1.101", "192.168.1.102", "8.8.4.4" });
        var client = new HueClient(_httpClient, _loggerMock.Object, localDiscovery.Object);

        var result = await client.DiscoverBridgeIps();

        Assert.Equal(
            new[] { "192.168.1.100", "192.168.1.101", "192.168.1.102" },
            result);
        localDiscovery.Verify(discovery => discovery.DiscoverAsync(It.IsAny<CancellationToken>()), Times.Once);
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

    [Fact]
    public async Task DiscoverBridgeIp_SkipsPublicAddresses()
    {
        var responseJson = @"[
            {""internalipaddress"":""8.8.8.8""},
            {""internalipaddress"":""192.168.1.102""}
        ]";
        SetupHttpResponse(HttpStatusCode.OK, responseJson);

        var client = new HueClient(_httpClient, _loggerMock.Object);

        var result = await client.DiscoverBridgeIp();

        Assert.Equal("192.168.1.102", result);
    }

    #endregion

    #region GetEntertainmentAreas Tests

    [Fact]
    public async Task GetBridgeCertificateFingerprint_RequiresHueConfigurationResponse()
    {
        SetupHttpResponse(HttpStatusCode.OK, "{\"status\":\"ok\"}");
        var client = new HueClient(_httpClient, _loggerMock.Object);

        var result = await client.GetBridgeCertificateFingerprint("192.168.1.100");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetBridgeCertificateFingerprint_RejectsUnsuccessfulResponse()
    {
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = new HueClient(_httpClient, _loggerMock.Object);

        var result = await client.GetBridgeCertificateFingerprint("192.168.1.100");

        Assert.Null(result);
    }

    [Fact]
    public async Task CreatePlaybackClient_AfterSharedClientRequest_DoesNotMutateHttpClientTimeout()
    {
        SetupHttpResponse(HttpStatusCode.OK, "{\"data\":[]}");
        var primary = new HueClient(_httpClient, Mock.Of<ILogger<HueClient>>());

        await primary.GetEntertainmentAreas("192.168.1.100", "test-app-key");

        var playback = primary.CreatePlaybackClient();

        Assert.NotNull(playback);
        Assert.Equal(TimeSpan.FromSeconds(10), _httpClient.Timeout);
    }

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

    [Fact]
    public async Task GetEntertainmentConfiguration_MismatchedResourceId_ReturnsNull()
    {
        // A bridge response for a different area must never be used for the requested target.
        var responseJson = @"{
            ""data"": [{
                ""id"": ""different-area"",
                ""channels"": [{""channel_id"": 0}]
            }]
        }";
        SetupHttpResponse(HttpStatusCode.OK, responseJson);

        var client = new HueClient(_httpClient, _loggerMock.Object);

        var result = await client.GetEntertainmentConfiguration("192.168.1.100", "test-app-key", "area-1");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetEntertainmentConfiguration_SelectsRequestedResourceWhenNotFirst()
    {
        var responseJson = @"{
            ""data"": [
                {""id"": ""different-area"", ""channels"": [{""channel_id"": 99}]},
                {""id"": ""area-1"", ""channels"": [{""channel_id"": 1}]}
            ]
        }";
        SetupHttpResponse(HttpStatusCode.OK, responseJson);

        var client = new HueClient(_httpClient, _loggerMock.Object);

        var result = await client.GetEntertainmentConfiguration("192.168.1.100", "test-app-key", "area-1");

        Assert.NotNull(result);
        Assert.Equal("area-1", result.Value.GetProperty("id").GetString());
        Assert.Equal(1, result.Value.GetProperty("channels")[0].GetProperty("channel_id").GetInt32());
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
    public async Task GetEntertainmentConfiguration_WhenCanceled_PropagatesCancellation()
    {
        SetupHttpResponse(HttpStatusCode.OK, "{}");
        var client = new HueClient(_httpClient, _loggerMock.Object);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetEntertainmentConfiguration(
                "192.168.1.100",
                "test-app-key",
                "area-1",
                cancellationSource.Token));
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
    public async Task StartEntertainmentArea_WhenCanceled_PropagatesCancellation()
    {
        SetupHttpResponse(HttpStatusCode.OK, "{}");
        var client = new HueClient(_httpClient, _loggerMock.Object);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.StartEntertainmentArea(
                "192.168.1.100",
                "test-app-key",
                "area-uuid",
                cancellationSource.Token));
    }

    [Fact]
    public async Task GetEntertainmentAreas_InvalidJson_ReturnsNull()
    {
        SetupHttpResponse(HttpStatusCode.OK, "not valid json");
        var client = new HueClient(_httpClient, _loggerMock.Object);

        var result = await client.GetEntertainmentAreas("192.168.1.100", "test-app-key");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetEntertainmentAreas_WhenCanceled_PropagatesCancellation()
    {
        SetupHttpResponse(HttpStatusCode.OK, "{\"data\":[]}");
        var client = new HueClient(_httpClient, _loggerMock.Object);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetEntertainmentAreas("192.168.1.100", "test-app-key", cancellationSource.Token));
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
        Assert.Null(result[0].Mirek);
        Assert.True(result[0].HasColor);
    }

    [Fact]
    public async Task GetLightStates_CapturesWritableAdvancedStateFields()
    {
        using var doc = JsonDocument.Parse(@"{
            ""channels"": [
                { ""channel_id"": 0, ""members"": [{""service"": {""rid"": ""light-advanced""}}] }
            ]
        }");
        SetupHttpResponse(HttpStatusCode.OK, @"{
            ""data"": [{
                ""on"": {""on"": true},
                ""dimming"": {""brightness"": 75},
                ""color"": {""xy"": {""x"": 0.3127, ""y"": 0.329}},
                ""gradient"": {
                    ""points"": [
                        {""xy"": {""x"": 0.1, ""y"": 0.2}},
                        {""color"": {""xy"": {""x"": 0.3, ""y"": 0.4}}}
                    ],
                    ""mode"": ""interpolated_palette"",
                    ""points_capable"": 5,
                    ""mode_values"": [""interpolated_palette""],
                    ""pixel_count"": 250
                },
                ""effects"": {
                    ""status"": ""candle"",
                    ""status_values"": [""stopped""],
                    ""effect_values"": [""candle""]
                },
                ""effects_v2"": {
                    ""status"": {
                        ""effect"": ""cosmos"",
                        ""parameters"": {
                            ""color"": {""xy"": {""x"": 0.5, ""y"": 0.6}},
                            ""color_temperature"": {""mirek"": 280},
                            ""speed"": 0.5
                        }
                    },
                    ""effect_values"": [""cosmos""]
                },
                ""timed_effects"": {
                    ""status"": ""sunrise"",
                    ""duration"": 5000,
                    ""status_values"": [""active""],
                    ""effect_values"": [""sunrise""]
                },
                ""alert"": {
                    ""action"": ""none"",
                    ""action_values"": [""breathe""]
                }
            }]
        }");

        var client = new HueClient(_httpClient, _loggerMock.Object);

        var result = await client.GetLightStates("192.168.1.100", "test-app-key", doc.RootElement);

        var state = Assert.Single(result);
        Assert.NotNull(state.Snapshot);
        var snapshot = state.Snapshot!;
        Assert.NotNull(snapshot.Gradient);
        var gradient = snapshot.Gradient!;
        Assert.Equal(2, gradient.Points.Count);
        Assert.Equal(0.1, gradient.Points[0].X, 3);
        Assert.Equal(0.4, gradient.Points[1].Y, 3);
        Assert.Equal("interpolated_palette", gradient.Mode);
        Assert.NotNull(snapshot.Effects);
        Assert.Equal("candle", snapshot.Effects!.Effect);
        Assert.NotNull(snapshot.EffectsV2);
        Assert.Equal("cosmos", snapshot.EffectsV2!.Effect);
        Assert.NotNull(snapshot.EffectsV2.Parameters);
        Assert.Equal(0.5, snapshot.EffectsV2.Parameters!.Color!.X, 3);
        Assert.Equal(280, snapshot.EffectsV2.Parameters.Mirek);
        Assert.Equal(0.5, snapshot.EffectsV2.Parameters.Speed);
        Assert.NotNull(snapshot.TimedEffects);
        var timedEffects = snapshot.TimedEffects!;
        Assert.Equal("sunrise", timedEffects.Effect);
        Assert.Equal(5000, timedEffects.Duration);
        Assert.NotNull(snapshot.Alert);
        Assert.Equal("none", snapshot.Alert!.Action);
    }

    [Fact]
    public async Task GetLightStates_MalformedAdvancedStateFieldsAreIgnored()
    {
        using var doc = JsonDocument.Parse(@"{
            ""channels"": [
                { ""channel_id"": 0, ""members"": [{""service"": {""rid"": ""light-malformed""}}] }
            ]
        }");
        SetupHttpResponse(HttpStatusCode.OK, @"{
            ""data"": [{
                ""on"": {""on"": true},
                ""dimming"": {""brightness"": 75},
                ""gradient"": {
                    ""points"": [{""color"": {""xy"": {""x"": 0.1, ""y"": 0.2}}}]
                },
                ""effects"": {""status"": 42},
                ""effects_v2"": {""status"": {""effect"": ""\u0001bad""}},
                ""timed_effects"": {""status"": 42, ""duration"": ""5000""},
                ""alert"": {""action_values"": [""breathe""]}
            }]
        }");

        var client = new HueClient(_httpClient, _loggerMock.Object);

        var result = await client.GetLightStates("192.168.1.100", "test-app-key", doc.RootElement);

        var state = Assert.Single(result);
        Assert.Null(state.Snapshot);
        Assert.True(state.IsOn);
        Assert.Equal(75, state.Brightness);
    }

    [Fact]
    public async Task GetLightStatesWithResult_MismatchedResourceIdRejectsState()
    {
        using var doc = JsonDocument.Parse(@"{
            ""channels"": [
                { ""channel_id"": 0, ""members"": [{""service"": {""rid"": ""requested-light""}}] }
            ]
        }");
        SetupHttpResponse(HttpStatusCode.OK, @"{
            ""data"": [{
                ""id"": ""different-light"",
                ""on"": {""on"": true},
                ""dimming"": {""brightness"": 75}
            }]
        }");

        var client = new HueClient(_httpClient, _loggerMock.Object)
        {
            RetryAttempts = 0
        };

        var result = await client.GetLightStatesWithResult(
            "192.168.1.100",
            "test-app-key",
            doc.RootElement);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.AttemptedCount);
        Assert.Equal(0, result.CapturedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Empty(result.States);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    public async Task GetLightStatesWithResult_MalformedResourceIdRejectsState(string malformedId)
    {
        using var doc = JsonDocument.Parse(@"{
            ""channels"": [
                { ""channel_id"": 0, ""members"": [{""service"": {""rid"": ""requested-light""}}] }
            ]
        }");
        SetupHttpResponse(HttpStatusCode.OK, $@"{{
            ""data"": [{{
                ""id"": {malformedId},
                ""on"": {{""on"": true}},
                ""dimming"": {{""brightness"": 75}}
            }}]
        }}");

        var client = new HueClient(_httpClient, _loggerMock.Object)
        {
            RetryAttempts = 0
        };

        var result = await client.GetLightStatesWithResult(
            "192.168.1.100",
            "test-app-key",
            doc.RootElement);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.AttemptedCount);
        Assert.Equal(0, result.CapturedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Empty(result.States);
    }

    [Fact]
    public async Task GetLightStates_ChannelFilterReadsOnlySelectedChannels()
    {
        using var doc = JsonDocument.Parse(@"{
            ""channels"": [
                {
                    ""channel_id"": 0,
                    ""members"": [{""service"": {""rid"": ""light-0""}}]
                },
                {
                    ""channel_id"": 1,
                    ""members"": [{""service"": {""rid"": ""light-1""}}]
                }
            ]
        }");
        var requestedLightIds = new List<string>();
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) =>
            {
                requestedLightIds.Add(request.RequestUri!.Segments[^1].Trim('/'));
            })
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

        var client = new HueClient(_httpClient, _loggerMock.Object);
        var result = await client.GetLightStates(
            "192.168.1.100",
            "test-app-key",
            doc.RootElement,
            new HashSet<int> { 1 });

        var state = Assert.Single(result!);
        Assert.Equal("light-1", state.Id);
        Assert.Equal(new[] { "light-1" }, requestedLightIds);
    }

    [Fact]
    public async Task GetLightStates_ColorTemperature_PreservesMirekMode()
    {
        using var doc = JsonDocument.Parse(@"{
            ""channels"": [
                {
                    ""channel_id"": 0,
                    ""members"": [{""service"": {""rid"": ""light-ct""}}]
                }
            ]
        }");

        SetupHttpResponse(HttpStatusCode.OK, @"{
            ""data"": [{
                ""on"": {""on"": true},
                ""dimming"": {""brightness"": 62.5},
                ""color_temperature"": {""mirek"": 325, ""mirek_valid"": true}
            }]
        }");

        var client = new HueClient(_httpClient, _loggerMock.Object);

        var result = await client.GetLightStates("192.168.1.100", "test-app-key", doc.RootElement);

        var state = Assert.Single(result!);
        Assert.Equal(325, state.Mirek);
        Assert.False(state.HasColor);
    }

    [Fact]
    public async Task GetLightStates_InvalidMirekFallsBackToColorWhenAvailable()
    {
        using var doc = JsonDocument.Parse(@"{
            ""channels"": [
                {
                    ""channel_id"": 0,
                    ""members"": [{""service"": {""rid"": ""light-color""}}]
                }
            ]
        }");

        SetupHttpResponse(HttpStatusCode.OK, @"{
            ""data"": [{
                ""on"": {""on"": true},
                ""dimming"": {""brightness"": 50},
                ""color"": {""xy"": {""x"": 0.25, ""y"": 0.35}},
                ""color_temperature"": {""mirek"": 300, ""mirek_valid"": false}
            }]
        }");

        var client = new HueClient(_httpClient, _loggerMock.Object);

        var result = await client.GetLightStates("192.168.1.100", "test-app-key", doc.RootElement);

        var state = Assert.Single(result!);
        Assert.Null(state.Mirek);
        Assert.True(state.HasColor);
        Assert.Equal(0.25, state.X, 3);
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

    [Fact]
    public async Task GetLightStatesWithResult_RetriesTransientFailureAndReportsSuccess()
    {
        using var doc = JsonDocument.Parse(@"{
            ""channels"": [
                { ""channel_id"": 0, ""members"": [{""service"": {""rid"": ""light-1""}}] }
            ]
        }");
        var requestCount = 0;
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                requestCount++;
                return requestCount == 1
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(@"{
                            ""data"": [{
                                ""on"": {""on"": true},
                                ""dimming"": {""brightness"": 50},
                                ""color"": {""xy"": {""x"": 0.3, ""y"": 0.3}}
                            }]
                        }")
                    };
            });

        var client = new HueClient(_httpClient, _loggerMock.Object)
        {
            RetryAttempts = 1
        };

        var result = await client.GetLightStatesWithResult(
            "192.168.1.100",
            "test-app-key",
            doc.RootElement);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.AttemptedCount);
        Assert.Equal(1, result.CapturedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task GetLightStatesWithResult_ReportsPartialCaptureFailure()
    {
        using var doc = JsonDocument.Parse(@"{
            ""channels"": [
                { ""channel_id"": 0, ""members"": [{""service"": {""rid"": ""light-1""}}] },
                { ""channel_id"": 1, ""members"": [{""service"": {""rid"": ""light-2""}}] }
            ]
        }");
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
                request.RequestUri!.AbsolutePath.EndsWith("light-2", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(@"{
                            ""data"": [{
                                ""on"": {""on"": true},
                                ""dimming"": {""brightness"": 50},
                                ""color"": {""xy"": {""x"": 0.3, ""y"": 0.3}}
                            }]
                        }")
                    });

        var client = new HueClient(_httpClient, _loggerMock.Object)
        {
            RetryAttempts = 0
        };

        var result = await client.GetLightStatesWithResult(
            "192.168.1.100",
            "test-app-key",
            doc.RootElement);

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.AttemptedCount);
        Assert.Equal(1, result.CapturedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Single(result.States);
        Assert.Equal("light-1", result.States[0].Id);
    }

    [Fact]
    public async Task GetLightStatesWithResult_CapturesSharedLightOnlyOnce()
    {
        using var doc = JsonDocument.Parse(@"{
            ""channels"": [
                { ""channel_id"": 0, ""members"": [{""service"": {""rid"": ""shared-light""}}] },
                { ""channel_id"": 1, ""members"": [{""service"": {""rid"": ""shared-light""}}] }
            ]
        }");
        var requestedLightIds = new List<string>();
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) =>
            {
                requestedLightIds.Add(request.RequestUri!.Segments[^1].Trim('/'));
            })
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

        var client = new HueClient(_httpClient, _loggerMock.Object)
        {
            RetryAttempts = 0
        };

        var result = await client.GetLightStatesWithResult(
            "192.168.1.100",
            "test-app-key",
            doc.RootElement);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.AttemptedCount);
        Assert.Equal(1, result.CapturedCount);
        Assert.Equal(new[] { "shared-light" }, requestedLightIds);
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
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequests.Add(req))
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
    public async Task RestoreLightStates_ColorTemperatureSendsMirekWithoutColorPayload()
    {
        var lightStates = new List<HueClient.LightState>
        {
            new("light-ct", true, 62, 0, 0, 325, false)
        };
        Task<string>? capturedBodyTask = null;
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) =>
                capturedBodyTask = request.Content!.ReadAsStringAsync())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });

        var client = new HueClient(_httpClient, _loggerMock.Object);

        await client.RestoreLightStates("192.168.1.100", "test-app-key", lightStates);

        Assert.NotNull(capturedBodyTask);
        using var payload = JsonDocument.Parse(await capturedBodyTask!);
        var root = payload.RootElement;
        Assert.Equal(325, root.GetProperty("color_temperature").GetProperty("mirek").GetInt32());
        Assert.False(root.TryGetProperty("color", out _));
    }

    [Fact]
    public async Task RestoreLightStates_SendsSanitizedWritableAdvancedStateFields()
    {
        var snapshot = new HueClient.LightStateSnapshot(
            new HueClient.LightGradientSnapshot(
                new[]
                {
                    new HueClient.LightGradientPoint(0.1, 0.2),
                    new HueClient.LightGradientPoint(0.3, 0.4)
                },
                "interpolated_palette"),
            new HueClient.LightEffectsSnapshot("candle"),
            new HueClient.LightEffectsV2Snapshot(
                "cosmos",
                new HueClient.LightEffectParameters(
                    new HueClient.LightGradientPoint(0.5, 0.6),
                    280,
                    0.5)),
            null,
            new HueClient.LightAlertSnapshot("none"));
        var lightStates = new List<HueClient.LightState>
        {
            new("light-advanced", true, 80, 0, 0, null, false, snapshot)
        };
        Task<string>? capturedBodyTask = null;
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) =>
                capturedBodyTask = request.Content!.ReadAsStringAsync())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });

        var client = new HueClient(_httpClient, _loggerMock.Object)
        {
            RetryAttempts = 0
        };

        var result = await client.RestoreLightStatesWithResult(
            "192.168.1.100",
            "test-app-key",
            lightStates);

        Assert.True(result.Succeeded);
        Assert.NotNull(capturedBodyTask);
        using var payload = JsonDocument.Parse(await capturedBodyTask!);
        var root = payload.RootElement;
        Assert.True(root.GetProperty("on").GetProperty("on").GetBoolean());
        Assert.Equal(80, root.GetProperty("dimming").GetProperty("brightness").GetInt32());

        var gradient = root.GetProperty("gradient");
        Assert.Equal(2, gradient.GetProperty("points").GetArrayLength());
        Assert.Equal("interpolated_palette", gradient.GetProperty("mode").GetString());
        var firstPoint = gradient.GetProperty("points")[0];
        Assert.True(firstPoint.TryGetProperty("xy", out _));
        Assert.False(firstPoint.TryGetProperty("color", out _));
        Assert.False(gradient.TryGetProperty("points_capable", out _));
        Assert.False(gradient.TryGetProperty("pixel_count", out _));

        var effectsV2 = root.GetProperty("effects_v2");
        Assert.Equal("cosmos", effectsV2.GetProperty("action").GetProperty("effect").GetString());
        var parameters = effectsV2.GetProperty("action").GetProperty("parameters");
        Assert.Equal(0.5, parameters.GetProperty("color").GetProperty("xy").GetProperty("x").GetDouble(), 3);
        Assert.Equal(280, parameters.GetProperty("color_temperature").GetProperty("mirek").GetInt32());
        Assert.Equal(0.5, parameters.GetProperty("speed").GetDouble(), 3);
        Assert.False(effectsV2.TryGetProperty("status", out _));
        Assert.False(root.TryGetProperty("effects", out _));
        Assert.False(root.TryGetProperty("timed_effects", out _));
        Assert.Equal("none", root.GetProperty("alert").GetProperty("action").GetString());
        Assert.False(root.GetProperty("alert").TryGetProperty("action_values", out _));
    }

    [Fact]
    public async Task RestoreLightStates_PrefersTimedEffectOverConflictingNormalEffects()
    {
        var snapshot = new HueClient.LightStateSnapshot(
            Effects: new HueClient.LightEffectsSnapshot("candle"),
            EffectsV2: new HueClient.LightEffectsV2Snapshot("cosmos"),
            TimedEffects: new HueClient.LightTimedEffectsSnapshot("sunrise", 5000));
        Task<string>? capturedBodyTask = null;
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) =>
                capturedBodyTask = request.Content!.ReadAsStringAsync())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });

        var client = new HueClient(_httpClient, _loggerMock.Object)
        {
            RetryAttempts = 0
        };

        var result = await client.RestoreLightStatesWithResult(
            "192.168.1.100",
            "test-app-key",
            new List<HueClient.LightState>
            {
                new("light-timed", true, 80, 0.3, 0.33, null, true, snapshot)
            });

        Assert.True(result.Succeeded);
        Assert.NotNull(capturedBodyTask);
        using var payload = JsonDocument.Parse(await capturedBodyTask!);
        var root = payload.RootElement;
        Assert.Equal("sunrise", root.GetProperty("timed_effects").GetProperty("effect").GetString());
        Assert.Equal(5000, root.GetProperty("timed_effects").GetProperty("duration").GetInt64());
        Assert.False(root.TryGetProperty("effects", out _));
        Assert.False(root.TryGetProperty("effects_v2", out _));
    }

    [Fact]
    public async Task RestoreLightStates_InvalidAdvancedStateFieldsAreOmitted()
    {
        var snapshot = new HueClient.LightStateSnapshot(
            new HueClient.LightGradientSnapshot(
                new[] { new HueClient.LightGradientPoint(0.1, 0.2) },
                "\u0001invalid"),
            new HueClient.LightEffectsSnapshot("\u0001invalid"),
            new HueClient.LightEffectsV2Snapshot(
                "\u0001invalid",
                new HueClient.LightEffectParameters(
                    new HueClient.LightGradientPoint(2, 0.5),
                    -1,
                    double.NaN)),
            new HueClient.LightTimedEffectsSnapshot("sunrise", 21_600_001),
            new HueClient.LightAlertSnapshot("   "));
        Task<string>? capturedBodyTask = null;
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) =>
                capturedBodyTask = request.Content!.ReadAsStringAsync())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });

        var client = new HueClient(_httpClient, _loggerMock.Object)
        {
            RetryAttempts = 0
        };

        var result = await client.RestoreLightStatesWithResult(
            "192.168.1.100",
            "test-app-key",
            new List<HueClient.LightState>
            {
                new("light-invalid-advanced", false, 30, 0, 0, null, false, snapshot)
            });

        Assert.True(result.Succeeded);
        Assert.NotNull(capturedBodyTask);
        using var payload = JsonDocument.Parse(await capturedBodyTask!);
        var root = payload.RootElement;
        Assert.False(root.TryGetProperty("gradient", out _));
        Assert.False(root.TryGetProperty("effects", out _));
        Assert.False(root.TryGetProperty("effects_v2", out _));
        Assert.True(root.TryGetProperty("timed_effects", out var timedEffects));
        Assert.Equal("sunrise", timedEffects.GetProperty("effect").GetString());
        Assert.False(timedEffects.TryGetProperty("duration", out _));
        Assert.False(root.TryGetProperty("alert", out _));
    }

    [Fact]
    public async Task RestoreLightStates_WithoutColorOrMirekOmitsColorPayload()
    {
        var lightStates = new List<HueClient.LightState>
        {
            new("light-white", true, 80, 0, 0, null, false)
        };
        Task<string>? capturedBodyTask = null;
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) =>
                capturedBodyTask = request.Content!.ReadAsStringAsync())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });

        var client = new HueClient(_httpClient, _loggerMock.Object);

        await client.RestoreLightStates("192.168.1.100", "test-app-key", lightStates);

        Assert.NotNull(capturedBodyTask);
        using var payload = JsonDocument.Parse(await capturedBodyTask!);
        Assert.False(payload.RootElement.TryGetProperty("color", out _));
        Assert.False(payload.RootElement.TryGetProperty("color_temperature", out _));
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

    [Fact]
    public async Task SetLightBrightnessWithResult_ChangesOnlyDimmingAndPreservesPowerState()
    {
        var lightStates = new List<HueClient.LightState>
        {
            new("light-on", true, 80, 0.3, 0.33),
            new("light-off", false, 50, 0.4, 0.4)
        };
        var capturedBodies = new List<string>();
        async Task<HttpResponseMessage> CaptureRequestAsync(HttpRequestMessage request)
        {
            capturedBodies.Add(await request.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }

        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns((HttpRequestMessage request, CancellationToken _) => CaptureRequestAsync(request));

        var client = new HueClient(_httpClient, _loggerMock.Object)
        {
            RetryAttempts = 0
        };

        var result = await client.SetLightBrightnessWithResult(
            "192.168.1.100",
            "test-app-key",
            lightStates,
            25);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.AttemptedCount);
        Assert.Equal(2, result.UpdatedCount);
        Assert.All(capturedBodies, body => Assert.DoesNotContain("color", body, StringComparison.OrdinalIgnoreCase));
        using var firstPayload = JsonDocument.Parse(capturedBodies[0]);
        using var secondPayload = JsonDocument.Parse(capturedBodies[1]);
        Assert.True(firstPayload.RootElement.GetProperty("on").GetProperty("on").GetBoolean());
        Assert.False(secondPayload.RootElement.GetProperty("on").GetProperty("on").GetBoolean());
        Assert.Equal(25, firstPayload.RootElement.GetProperty("dimming").GetProperty("brightness").GetInt32());
        Assert.Equal(25, secondPayload.RootElement.GetProperty("dimming").GetProperty("brightness").GetInt32());
    }

    [Fact]
    public async Task SetLightBrightnessWithResult_ClampsBrightnessAndReportsFailures()
    {
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.BadRequest));

        var client = new HueClient(_httpClient, _loggerMock.Object)
        {
            RetryAttempts = 0
        };

        var result = await client.SetLightBrightnessWithResult(
            "192.168.1.100",
            "test-app-key",
            new List<HueClient.LightState> { new("light-1", true, 80, 0.3, 0.33) },
            150);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.AttemptedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(1, result.FailedCount);
    }

    [Fact]
    public async Task RestoreLightStatesWithResult_RetriesTransientFailureAndReportsSuccess()
    {
        var requestCount = 0;
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                requestCount++;
                return requestCount == 1
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : new HttpResponseMessage(HttpStatusCode.OK);
            });

        var client = new HueClient(_httpClient, _loggerMock.Object)
        {
            RetryAttempts = 1
        };

        var result = await client.RestoreLightStatesWithResult(
            "192.168.1.100",
            "test-app-key",
            new List<HueClient.LightState> { new("light-1", true, 80, 0.3, 0.33) });

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.AttemptedCount);
        Assert.Equal(1, result.RestoredCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task RestoreLightStatesWithResult_ReportsFailedLightAfterRetries()
    {
        var requestCount = 0;
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((_, _) => requestCount++)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var client = new HueClient(_httpClient, _loggerMock.Object)
        {
            RetryAttempts = 0
        };

        var result = await client.RestoreLightStatesWithResult(
            "192.168.1.100",
            "test-app-key",
            new List<HueClient.LightState> { new("light-1", true, 80, 0.3, 0.33) });

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.AttemptedCount);
        Assert.Equal(0, result.RestoredCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task StopEntertainmentAreaWithResult_CancellationBoundsNonCompletingRequest()
    {
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns((HttpRequestMessage _, CancellationToken _) =>
            {
                cancellation.Cancel();
                return pending.Task;
            });

        var client = new HueClient(_httpClient, _loggerMock.Object)
        {
            RetryAttempts = 3
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await client.StopEntertainmentAreaWithResult(
            "192.168.1.100",
            "test-app-key",
            "area-1",
            cancellation.Token);
        stopwatch.Stop();

        Assert.False(result);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RestoreLightStatesWithResult_CancellationReportsUnattemptedLightsAsFailed()
    {
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns((HttpRequestMessage _, CancellationToken _) =>
            {
                cancellation.Cancel();
                return pending.Task;
            });

        var client = new HueClient(_httpClient, _loggerMock.Object)
        {
            RetryAttempts = 3
        };
        var lightStates = new List<HueClient.LightState>
        {
            new("light-1", true, 80, 0.3, 0.33),
            new("light-2", true, 70, 0.4, 0.34),
            new("light-3", false, 50, 0, 0)
        };

        var result = await client.RestoreLightStatesWithResult(
            "192.168.1.100",
            "test-app-key",
            lightStates,
            cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(lightStates.Count, result.AttemptedCount);
        Assert.Equal(0, result.RestoredCount);
        Assert.Equal(lightStates.Count, result.FailedCount);
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

    [Fact]
    public async Task RegisterWithBridge_TransientFailureDoesNotRetryNonIdempotentLinkButtonRequest()
    {
        var requestCount = 0;
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((_, _) => requestCount++)
            .ThrowsAsync(new HttpRequestException("transient registration failure"));

        var client = new HueClient(_httpClient, _loggerMock.Object)
        {
            RetryAttempts = 3
        };

        Assert.Null(await client.RegisterWithBridge("192.168.1.100"));
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task RegisterWithBridge_FailureDoesNotLogBridgeResponseBody()
    {
        const string secretSentinel = "bridge-response-secret-username-clientkey";
        SetupHttpResponse(
            HttpStatusCode.OK,
            $"[{{\"error\":{{\"type\":101,\"description\":\"{secretSentinel}\"}}}}]");

        var client = new HueClient(_httpClient, _loggerMock.Object);

        Assert.Null(await client.RegisterWithBridge("192.168.1.100"));

        var logText = string.Join(
            "\n",
            _loggerMock.Invocations.Select(invocation =>
                string.Join(" ", invocation.Arguments.Select(argument => argument?.ToString() ?? string.Empty))));
        Assert.DoesNotContain(secretSentinel, logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterWithBridge_WhenCanceled_PropagatesCancellation()
    {
        SetupHttpResponse(HttpStatusCode.OK, "[]");
        var client = new HueClient(_httpClient, _loggerMock.Object);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.RegisterWithBridge("192.168.1.100", cancellationSource.Token));
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
