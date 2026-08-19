using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Hue.Api;
using Jellyfin.Plugin.Hue.Configuration;
using Jellyfin.Plugin.Hue.Hue;
using Jellyfin.Plugin.Hue.Service;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

[Collection("PluginState")]
public sealed class HueApiControllerTests : IDisposable
{
    private readonly Mock<ILogger<HueClient>> _loggerMock = new();
    private readonly Mock<HttpMessageHandler> _httpHandlerMock = new();
    private readonly HttpClient _httpClient;

    public HueApiControllerTests()
    {
        _httpClient = new HttpClient(_httpHandlerMock.Object);
    }

    public void Dispose() => _httpClient.Dispose();

    [Fact]
    public async Task DiscoverBridge_ReturnsDiscoveredAddress()
    {
        SetupHttpResponse(HttpStatusCode.OK, "[{\"internalipaddress\":\"192.168.1.100\"}]");
        var controller = CreateController();

        var action = await controller.DiscoverBridge();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var discovery = Assert.IsType<HueBridgeDiscoveryResult>(response.Value);
        Assert.Equal("192.168.1.100", discovery.IpAddress);
    }

    [Fact]
    public async Task DiscoverBridges_ReturnsEveryDiscoveredAddress()
    {
        SetupHttpResponse(
            HttpStatusCode.OK,
            "[{\"internalipaddress\":\"192.168.1.100\"},{\"internalipaddress\":\"192.168.1.101\"}]");
        var controller = CreateController();

        var action = await controller.DiscoverBridges();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var discovery = Assert.IsType<HueBridgeDiscoveryResult>(response.Value);
        Assert.Equal("192.168.1.100", discovery.IpAddress);
        Assert.Equal(new[] { "192.168.1.100", "192.168.1.101" }, discovery.IpAddresses);
    }

    [Fact]
    public async Task DiscoverBridge_WhenServiceFindsNothing_ReturnsBadGateway()
    {
        SetupHttpResponse(HttpStatusCode.OK, "[]");
        var controller = CreateController();

        var action = await controller.DiscoverBridge();

        var response = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task PostEntertainmentAreas_UsesRequestBodyAndReturnsAreas()
    {
        HttpRequestMessage? capturedRequest = null;
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"id\":\"area-1\",\"metadata\":{\"name\":\"Living Room\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            });
        var controller = CreateController();

        var action = await controller.PostEntertainmentAreas(new HueEntertainmentAreasRequest
        {
            IpAddress = "192.168.1.100",
            AppKey = "app-key"
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var areas = Assert.IsAssignableFrom<IEnumerable<HueClient.EntertainmentArea>>(response.Value);
        var area = Assert.Single(areas);
        Assert.Equal("area-1", area.Id);
        Assert.Equal("Living Room", area.Name);
        Assert.NotNull(capturedRequest);
        Assert.Empty(capturedRequest!.RequestUri!.Query);
        Assert.Equal("app-key", capturedRequest.Headers.GetValues("hue-application-key").Single());
    }

    [Fact]
    public async Task PostEntertainmentAreas_BlankAppKeyUsesStoredGlobalCredentialForConfiguredBridge()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "stored-app-key"
        });
        HttpRequestMessage? capturedRequest = null;
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json")
            });
        var controller = CreateController();

        var action = await controller.PostEntertainmentAreas(new HueEntertainmentAreasRequest
        {
            IpAddress = "192.168.1.100",
            AppKey = ""
        });

        Assert.IsType<OkObjectResult>(action.Result);
        Assert.NotNull(capturedRequest);
        Assert.Equal("stored-app-key", capturedRequest!.Headers.GetValues("hue-application-key").Single());
    }

    [Fact]
    public async Task PostEntertainmentAreas_BlankAppKeyCannotUseStoredCredentialForDifferentBridge()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "stored-app-key"
        });
        var controller = CreateController();

        var action = await controller.PostEntertainmentAreas(new HueEntertainmentAreasRequest
        {
            IpAddress = "192.168.1.101",
            AppKey = ""
        });

        Assert.IsType<BadRequestObjectResult>(action.Result);
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PostEntertainmentAreas_BlankAppKeyUsesStoredCustomMappingCredentialForMatchingUser()
    {
        InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-custom",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "mapping-app-key",
                    HueClientKey = "mapping-client-key",
                    EntertainmentAreaId = "area-1"
                }
            }
        });
        HttpRequestMessage? capturedRequest = null;
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json")
            });
        var controller = CreateController();

        var action = await controller.PostEntertainmentAreas(new HueEntertainmentAreasRequest
        {
            UserId = "user-custom",
            IpAddress = "192.168.1.101",
            AppKey = ""
        });

        Assert.IsType<OkObjectResult>(action.Result);
        Assert.NotNull(capturedRequest);
        Assert.Equal("mapping-app-key", capturedRequest!.Headers.GetValues("hue-application-key").Single());
    }

    [Fact]
    public async Task PostEntertainmentAreas_BlankAppKeyCannotUseCustomMappingForWrongUser()
    {
        InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-custom",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "mapping-app-key",
                    HueClientKey = "mapping-client-key",
                    EntertainmentAreaId = "area-1"
                }
            }
        });
        var controller = CreateController();

        var action = await controller.PostEntertainmentAreas(new HueEntertainmentAreasRequest
        {
            UserId = "other-user",
            IpAddress = "192.168.1.101",
            AppKey = ""
        });

        Assert.IsType<BadRequestObjectResult>(action.Result);
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PostEntertainmentAreas_ForwardsRequestCancellationToBridgeCall()
    {
        CancellationToken observedToken = default;
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((_, token) => observedToken = token)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json")
            });
        var controller = CreateController();
        using var cancellationSource = new CancellationTokenSource();

        var action = await controller.PostEntertainmentAreas(
            new HueEntertainmentAreasRequest
            {
                IpAddress = "192.168.1.100",
                AppKey = "app-key"
            },
            cancellationSource.Token);

        Assert.IsType<OkObjectResult>(action.Result);
        // HttpClient passes a linked transport token to the handler, so identity is
        // intentionally different from the MVC request token; it must still be cancelable.
        Assert.True(observedToken.CanBeCanceled);
    }

    [Fact]
    public async Task PostEntertainmentAreas_WithoutCredentials_ReturnsBadRequest()
    {
        var controller = CreateController();

        var action = await controller.PostEntertainmentAreas(new HueEntertainmentAreasRequest());

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostEntertainmentChannels_ReturnsSortedChannelIdsAndMemberCounts()
    {
        SetupHttpResponse(
            HttpStatusCode.OK,
            "{\"data\":[{\"channels\":[{\"channel_id\":9,\"members\":[{\"service\":{\"rid\":\"light-9\"}}]},{\"channel_id\":2,\"members\":[]}]}]}");
        var controller = CreateController();

        var action = await controller.PostEntertainmentChannels(new HueEntertainmentChannelsRequest
        {
            IpAddress = "192.168.1.100",
            AppKey = "app-key",
            EntertainmentAreaId = "area-1"
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var channels = Assert.IsAssignableFrom<IEnumerable<HueEntertainmentChannel>>(response.Value).ToArray();
        Assert.Equal(new[] { 2, 9 }, channels.Select(channel => channel.ChannelId));
        Assert.Equal(new[] { 0, 1 }, channels.Select(channel => channel.MemberCount));
    }

    [Fact]
    public async Task PostEntertainmentChannels_BlankAppKeyUsesStoredGlobalCredential()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "stored-app-key"
        });
        HttpRequestMessage? capturedRequest = null;
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"channels\":[{\"channel_id\":1}]}]}",
                    Encoding.UTF8,
                    "application/json")
            });
        var controller = CreateController();

        var action = await controller.PostEntertainmentChannels(new HueEntertainmentChannelsRequest
        {
            IpAddress = "192.168.1.100",
            AppKey = "",
            EntertainmentAreaId = "area-1"
        });

        Assert.IsType<OkObjectResult>(action.Result);
        Assert.NotNull(capturedRequest);
        Assert.Equal("stored-app-key", capturedRequest!.Headers.GetValues("hue-application-key").Single());
    }

    [Fact]
    public async Task PostEntertainmentChannels_BlankAppKeyUsesStoredCustomMappingCredential()
    {
        InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-custom",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "mapping-app-key",
                    HueClientKey = "mapping-client-key",
                    EntertainmentAreaId = "area-1"
                }
            }
        });
        HttpRequestMessage? capturedRequest = null;
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"channels\":[{\"channel_id\":3}]}]}",
                    Encoding.UTF8,
                    "application/json")
            });
        var controller = CreateController();

        var action = await controller.PostEntertainmentChannels(new HueEntertainmentChannelsRequest
        {
            UserId = "user-custom",
            IpAddress = "192.168.1.101",
            AppKey = "",
            EntertainmentAreaId = "area-1"
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var channels = Assert.IsAssignableFrom<IEnumerable<HueEntertainmentChannel>>(response.Value);
        Assert.Equal(3, Assert.Single(channels).ChannelId);
        Assert.NotNull(capturedRequest);
        Assert.Equal("mapping-app-key", capturedRequest!.Headers.GetValues("hue-application-key").Single());
    }

    [Fact]
    public async Task TestConnection_ReturnsReachabilityAndAreaCount()
    {
        SetupHttpResponse(
            HttpStatusCode.OK,
            "{\"data\":[{\"id\":\"area-1\",\"metadata\":{\"name\":\"Living Room\"}}]}");
        var controller = CreateController();

        var action = await controller.TestConnection(new HueConnectionTestRequest
        {
            IpAddress = "192.168.1.100",
            AppKey = "app-key"
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueConnectionTestResult>(response.Value);
        Assert.True(result.IsReachable);
        Assert.Equal(1, result.AreaCount);
        Assert.Null(result.AreaFound);
        Assert.Contains("Found 1", result.Message);
    }

    [Fact]
    public async Task TestConnection_BlankRedactedCredentialsUsesStoredGlobalKeys()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "stored-app-key",
            HueClientKey = "stored-client-key"
        });
        HttpRequestMessage? capturedRequest = null;
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"id\":\"area-1\",\"metadata\":{\"name\":\"Living Room\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            });
        var controller = CreateController();

        var action = await controller.TestConnection(new HueConnectionTestRequest
        {
            IpAddress = "192.168.1.100",
            AppKey = "",
            ClientKey = ""
        });

        Assert.IsType<OkObjectResult>(action.Result);
        Assert.NotNull(capturedRequest);
        Assert.Equal("stored-app-key", capturedRequest!.Headers.GetValues("hue-application-key").Single());
    }

    [Fact]
    public async Task TestConnection_BlankRedactedCredentialsUsesStoredCustomMappingKeys()
    {
        InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-custom",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "mapping-app-key",
                    HueClientKey = "mapping-client-key",
                    EntertainmentAreaId = "area-1"
                }
            }
        });
        _httpHandlerMock
            .Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"id\":\"area-1\",\"metadata\":{\"name\":\"Study\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"channels\":[{\"channel_id\":1}]}]}",
                    Encoding.UTF8,
                    "application/json")
            });
        var streamTester = new Mock<IHueStreamTester>();
        streamTester
            .Setup(tester => tester.TestAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<System.Text.Json.JsonElement>(),
                It.IsAny<IReadOnlySet<int>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HueStreamProbeResult { Succeeded = true, Message = "DTLS probe succeeded." });
        var controller = CreateController(streamTester.Object);

        var action = await controller.TestConnection(new HueConnectionTestRequest
        {
            UserId = "user-custom",
            IpAddress = "192.168.1.101",
            AppKey = "",
            ClientKey = "",
            EntertainmentAreaId = "area-1"
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueConnectionTestResult>(response.Value);
        Assert.True(result.StreamReady);
        streamTester.Verify(tester => tester.TestAsync(
            "192.168.1.101",
            "mapping-app-key",
            "mapping-client-key",
            "area-1",
            It.IsAny<System.Text.Json.JsonElement>(),
            It.IsAny<IReadOnlySet<int>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TestConnection_ForwardsRequestCancellationToAllBridgeReads()
    {
        var observedTokens = new List<CancellationToken>();
        var requestCount = 0;
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((_, token) =>
            {
                observedTokens.Add(token);
                requestCount++;
                var content = requestCount == 1
                    ? "{\"data\":[{\"id\":\"area-1\",\"metadata\":{\"name\":\"Living Room\"}}]}"
                    : "{\"data\":[{\"channels\":[{\"channel_id\":1}]}]}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(content, Encoding.UTF8, "application/json")
                });
            });
        var controller = CreateController();
        using var cancellationSource = new CancellationTokenSource();

        var action = await controller.TestConnection(
            new HueConnectionTestRequest
            {
                IpAddress = "192.168.1.100",
                AppKey = "app-key",
                EntertainmentAreaId = "area-1"
            },
            cancellationSource.Token);

        Assert.IsType<OkObjectResult>(action.Result);
        Assert.Equal(2, observedTokens.Count);
        // HttpClient passes linked transport tokens to the handler rather than the
        // controller's token instance; every bridge read must still be cancelable.
        Assert.All(observedTokens, token => Assert.True(token.CanBeCanceled));
    }

    [Fact]
    public async Task TestConnection_WithInvalidAddress_ReturnsBadRequest()
    {
        var controller = CreateController();

        var action = await controller.TestConnection(new HueConnectionTestRequest
        {
            IpAddress = "8.8.8.8",
            AppKey = "app-key"
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TestConnection_WithSelectedAreaVerifiesControllableChannels()
    {
        _httpHandlerMock
            .Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"id\":\"area-1\",\"metadata\":{\"name\":\"Living Room\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"channels\":[{\"channel_id\":1}]}]}",
                    Encoding.UTF8,
                    "application/json")
            });
        var controller = CreateController();

        var action = await controller.TestConnection(new HueConnectionTestRequest
        {
            IpAddress = "192.168.1.100",
            AppKey = "app-key",
            EntertainmentAreaId = "area-1"
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueConnectionTestResult>(response.Value);
        Assert.True(result.AreaFound);
        Assert.Equal("Living Room", result.AreaName);
        Assert.Contains("ready", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestConnection_WithChannelProfileValidatesAndPassesSelectedChannelsToProbe()
    {
        _httpHandlerMock
            .Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"id\":\"area-1\",\"metadata\":{\"name\":\"Living Room\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"channels\":[{\"channel_id\":1},{\"channel_id\":2}]}]}",
                    Encoding.UTF8,
                    "application/json")
            });
        var streamTester = new Mock<IHueStreamTester>();
        streamTester
            .Setup(tester => tester.TestAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<System.Text.Json.JsonElement>(),
                It.IsAny<IReadOnlySet<int>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HueStreamProbeResult
            {
                Succeeded = true,
                Message = "DTLS probe succeeded."
            });
        var controller = CreateController(streamTester.Object);

        var action = await controller.TestConnection(new HueConnectionTestRequest
        {
            IpAddress = "192.168.1.100",
            AppKey = "app-key",
            ClientKey = "client-key",
            EntertainmentAreaId = "area-1",
            ChannelIds = "2"
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueConnectionTestResult>(response.Value);
        Assert.True(result.ChannelProfileValid);
        Assert.Equal(2, result.AvailableChannelCount);
        Assert.Equal(1, result.SelectedChannelCount);
        Assert.True(result.StreamReady);
        streamTester.Verify(tester => tester.TestAsync(
            "192.168.1.100",
            "app-key",
            "client-key",
            "area-1",
            It.IsAny<System.Text.Json.JsonElement>(),
            It.Is<IReadOnlySet<int>?>(ids => ids != null && ids.Count == 1 && ids.Contains(2)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TestConnection_WithStaleChannelProfileReportsMissingIdsWithoutProbing()
    {
        _httpHandlerMock
            .Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"id\":\"area-1\",\"metadata\":{\"name\":\"Living Room\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"channels\":[{\"channel_id\":1}]}]}",
                    Encoding.UTF8,
                    "application/json")
            });
        var streamTester = new Mock<IHueStreamTester>();
        var controller = CreateController(streamTester.Object);

        var action = await controller.TestConnection(new HueConnectionTestRequest
        {
            IpAddress = "192.168.1.100",
            AppKey = "app-key",
            ClientKey = "client-key",
            EntertainmentAreaId = "area-1",
            ChannelIds = "9"
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueConnectionTestResult>(response.Value);
        Assert.False(result.ChannelProfileValid);
        Assert.Equal("9", result.MissingChannelIds);
        Assert.False(result.StreamTested);
        Assert.Contains("not present", result.Message, StringComparison.OrdinalIgnoreCase);
        streamTester.Verify(tester => tester.TestAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<System.Text.Json.JsonElement>(),
            It.IsAny<IReadOnlySet<int>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TestConnection_WithMalformedChannelProfileReturnsBadRequest()
    {
        var controller = CreateController();

        var action = await controller.TestConnection(new HueConnectionTestRequest
        {
            IpAddress = "192.168.1.100",
            AppKey = "app-key",
            EntertainmentAreaId = "area-1",
            ChannelIds = "2, nope"
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Preview_WithSelectedChannelsPassesColorAndDurationToStreamTester()
    {
        SetupHttpResponse(
            HttpStatusCode.OK,
            "{\"data\":[{\"channels\":[{\"channel_id\":1},{\"channel_id\":2}]}]}");
        var streamTester = new Mock<IHueStreamTester>();
        streamTester
            .Setup(tester => tester.PreviewAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<System.Text.Json.JsonElement>(),
                It.IsAny<IReadOnlySet<int>?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                PluginConfiguration.ColorPresetEffectSolid,
                PluginConfiguration.DefaultColorPresetEffectSpeedPercent))
            .ReturnsAsync(new HueStreamProbeResult
            {
                Succeeded = true,
                Message = "Preview sent."
            });
        var controller = CreateController(streamTester.Object);

        var action = await controller.Preview(new HuePreviewRequest
        {
            IpAddress = "192.168.1.100",
            AppKey = "app-key",
            ClientKey = "client-key",
            EntertainmentAreaId = "area-1",
            ChannelIds = "2",
            Red = 255,
            Green = 128,
            Blue = 32,
            BrightnessPercent = 75,
            DurationSeconds = 4,
            TransitionSeconds = 2,
            TransitionOutSeconds = 1
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HuePreviewResult>(response.Value);
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.AvailableChannelCount);
        Assert.Equal(1, result.SelectedChannelCount);
        Assert.Equal(1, result.TransitionOutSeconds);
        streamTester.Verify(tester => tester.PreviewAsync(
            "192.168.1.100",
            "app-key",
            "client-key",
            "area-1",
            It.IsAny<System.Text.Json.JsonElement>(),
            It.Is<IReadOnlySet<int>?>(ids => ids != null && ids.Count == 1 && ids.Contains(2)),
            255,
            128,
            32,
            75,
            4,
            It.IsAny<CancellationToken>(),
            2,
            1,
            PluginConfiguration.ColorPresetEffectSolid,
            PluginConfiguration.DefaultColorPresetEffectSpeedPercent), Times.Once);
    }

    [Fact]
    public async Task Preview_WithRainbowEffectPassesCanonicalEffectToStreamTester()
    {
        SetupHttpResponse(
            HttpStatusCode.OK,
            "{\"data\":[{\"channels\":[{\"channel_id\":1}]}]}");
        var streamTester = new Mock<IHueStreamTester>();
        streamTester
            .Setup(tester => tester.PreviewAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<System.Text.Json.JsonElement>(),
                It.IsAny<IReadOnlySet<int>?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                PluginConfiguration.ColorPresetEffectRainbow,
                175))
            .ReturnsAsync(new HueStreamProbeResult { Succeeded = true, Message = "Rainbow preview sent." });
        var controller = CreateController(streamTester.Object);

        var action = await controller.Preview(new HuePreviewRequest
        {
            IpAddress = "192.168.1.100",
            AppKey = "app-key",
            ClientKey = "client-key",
            EntertainmentAreaId = "area-1",
            Effect = " rainbow ",
            DurationSeconds = 3,
            EffectSpeedPercent = 175
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HuePreviewResult>(response.Value);
        Assert.Equal(PluginConfiguration.ColorPresetEffectRainbow, result.Effect);
        Assert.Equal(175, result.EffectSpeedPercent);
        streamTester.Verify(tester => tester.PreviewAsync(
            "192.168.1.100",
            "app-key",
            "client-key",
            "area-1",
            It.IsAny<System.Text.Json.JsonElement>(),
            It.IsAny<IReadOnlySet<int>?>(),
            255,
            255,
            255,
            100,
            3,
            It.IsAny<CancellationToken>(),
            0,
            0,
            PluginConfiguration.ColorPresetEffectRainbow,
            175), Times.Once);
    }

    [Fact]
    public void CancelPreview_RequestsCancellationAndReturnsSanitizedResult()
    {
        var streamTester = new Mock<IHueStreamTester>();
        streamTester
            .Setup(tester => tester.CancelActiveDiagnostic())
            .Returns(true);
        var controller = CreateController(streamTester.Object);

        var action = controller.CancelPreview();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HuePreviewCancellationResult>(response.Value);
        Assert.True(result.Canceled);
        Assert.Contains("restore", result.Message, StringComparison.OrdinalIgnoreCase);
        streamTester.Verify(tester => tester.CancelActiveDiagnostic(), Times.Once);
    }

    [Fact]
    public void CancelPreview_WithoutPreviewServiceReturnsUnavailable()
    {
        var action = CreateController().CancelPreview();

        var response = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Preview_BlankRedactedCredentialsUsesStoredGlobalKeys()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "stored-app-key",
            HueClientKey = "stored-client-key"
        });
        SetupHttpResponse(
            HttpStatusCode.OK,
            "{\"data\":[{\"channels\":[{\"channel_id\":1}]}]}");
        var streamTester = new Mock<IHueStreamTester>();
        streamTester
            .Setup(tester => tester.PreviewAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<System.Text.Json.JsonElement>(),
                It.IsAny<IReadOnlySet<int>?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                0,
                0,
                PluginConfiguration.ColorPresetEffectSolid,
                PluginConfiguration.DefaultColorPresetEffectSpeedPercent))
            .ReturnsAsync(new HueStreamProbeResult { Succeeded = true, Message = "Preview sent." });
        var controller = CreateController(streamTester.Object);

        var action = await controller.Preview(new HuePreviewRequest
        {
            IpAddress = "192.168.1.100",
            AppKey = "",
            ClientKey = "",
            EntertainmentAreaId = "area-1",
            Red = 20,
            Green = 30,
            Blue = 40,
            DurationSeconds = 2
        });

        Assert.IsType<OkObjectResult>(action.Result);
        streamTester.Verify(tester => tester.PreviewAsync(
            "192.168.1.100",
            "stored-app-key",
            "stored-client-key",
            "area-1",
            It.IsAny<System.Text.Json.JsonElement>(),
            It.IsAny<IReadOnlySet<int>?>(),
            20,
            30,
            40,
            100,
            2,
            It.IsAny<CancellationToken>(),
            0,
            0,
            PluginConfiguration.ColorPresetEffectSolid,
            PluginConfiguration.DefaultColorPresetEffectSpeedPercent), Times.Once);
    }

    [Fact]
    public async Task Preview_BlankRedactedCredentialsUsesStoredCustomMappingKeys()
    {
        InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-custom",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "mapping-app-key",
                    HueClientKey = "mapping-client-key",
                    EntertainmentAreaId = "area-1"
                }
            }
        });
        SetupHttpResponse(
            HttpStatusCode.OK,
            "{\"data\":[{\"channels\":[{\"channel_id\":1}]}]}");
        var streamTester = new Mock<IHueStreamTester>();
        streamTester
            .Setup(tester => tester.PreviewAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<System.Text.Json.JsonElement>(),
                It.IsAny<IReadOnlySet<int>?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                0,
                0,
                PluginConfiguration.ColorPresetEffectSolid,
                PluginConfiguration.DefaultColorPresetEffectSpeedPercent))
            .ReturnsAsync(new HueStreamProbeResult { Succeeded = true, Message = "Preview sent." });
        var controller = CreateController(streamTester.Object);

        var action = await controller.Preview(new HuePreviewRequest
        {
            UserId = "user-custom",
            IpAddress = "192.168.1.101",
            AppKey = "",
            ClientKey = "",
            EntertainmentAreaId = "area-1",
            Red = 20,
            Green = 30,
            Blue = 40,
            DurationSeconds = 2
        });

        Assert.IsType<OkObjectResult>(action.Result);
        streamTester.Verify(tester => tester.PreviewAsync(
            "192.168.1.101",
            "mapping-app-key",
            "mapping-client-key",
            "area-1",
            It.IsAny<System.Text.Json.JsonElement>(),
            It.IsAny<IReadOnlySet<int>?>(),
            20,
            30,
            40,
            100,
            2,
            It.IsAny<CancellationToken>(),
            0,
            0,
            PluginConfiguration.ColorPresetEffectSolid,
            PluginConfiguration.DefaultColorPresetEffectSpeedPercent), Times.Once);
    }

    [Fact]
    public async Task Preview_AllEnabledTargetsRunsConfiguredTargetsAndReturnsSanitizedOutcomes()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "global-app-secret",
            HueClientKey = "global-client-secret",
            EntertainmentAreaId = "global-area",
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-kitchen",
                    UserName = "Kitchen",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "mapping-app-secret",
                    HueClientKey = "mapping-client-secret",
                    EntertainmentAreaId = "kitchen-area",
                    ChannelIdsOverride = "1"
                }
            }
        });
        SetupHttpResponse(
            HttpStatusCode.OK,
            "{\"data\":[{\"channels\":[{\"channel_id\":0},{\"channel_id\":1}]}]}");
        var streamTester = new Mock<IHueStreamTester>();
        streamTester
            .Setup(tester => tester.PreviewAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<JsonElement>(),
                It.IsAny<IReadOnlySet<int>?>(),
                12,
                34,
                56,
                80,
                4,
                It.IsAny<CancellationToken>(),
                1,
                1,
                PluginConfiguration.ColorPresetEffectPulse,
                150))
            .Returns((string bridgeIp, string _, string _, string _, JsonElement _, IReadOnlySet<int>? _, int _, int _, int _, int _, int _, CancellationToken _, int _, int _, string _, int _) =>
                Task.FromResult(new HueStreamProbeResult
                {
                    Succeeded = !string.Equals(bridgeIp, "192.168.1.101", StringComparison.Ordinal),
                    Message = string.Equals(bridgeIp, "192.168.1.101", StringComparison.Ordinal)
                        ? "Kitchen preview failed."
                        : "Default preview completed."
                }));
        var service = new HueSceneAutomationService(
            streamTester.Object,
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(streamTester.Object, hostedServices: new[] { service });

        var action = await controller.Preview(new HuePreviewRequest
        {
            TargetAllEnabledMappings = true,
            Effect = "Pulse",
            EffectSpeedPercent = 150,
            Red = 12,
            Green = 34,
            Blue = 56,
            BrightnessPercent = 80,
            DurationSeconds = 4,
            TransitionSeconds = 1,
            TransitionOutSeconds = 1
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HuePreviewResult>(response.Value);
        Assert.True(result.TargetAllEnabledMappings);
        Assert.False(result.Succeeded);
        Assert.Equal(2, result.TargetResults.Count);
        Assert.Equal("Default bridge target", result.TargetResults[0].TargetLabel);
        Assert.True(result.TargetResults[0].Succeeded);
        Assert.Equal(2, result.TargetResults[0].AvailableChannelCount);
        Assert.Equal(2, result.TargetResults[0].SelectedChannelCount);
        Assert.Equal("Kitchen", result.TargetResults[1].TargetLabel);
        Assert.False(result.TargetResults[1].Succeeded);
        Assert.Equal(1, result.TargetResults[1].SelectedChannelCount);
        Assert.Equal(4, result.AvailableChannelCount);
        Assert.Equal(3, result.SelectedChannelCount);
        streamTester.Verify(tester => tester.PreviewAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<JsonElement>(),
            It.IsAny<IReadOnlySet<int>?>(), 12, 34, 56, 80, 4, It.IsAny<CancellationToken>(), 1, 1,
            PluginConfiguration.ColorPresetEffectPulse, 150), Times.Exactly(2));
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("global-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("global-client-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("mapping-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("mapping-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewColorPreset_ResolvesSavedSceneAgainstDefaultTargetWithoutCredentials()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "global-app-secret",
            HueClientKey = "global-client-secret",
            EntertainmentAreaId = "global-area",
            ColorPresets = new List<HueColorPreset>
            {
                new()
                {
                    Name = "Evening",
                    Effect = PluginConfiguration.ColorPresetEffectPulse,
                    EffectSpeedPercent = 150,
                    Red = 12,
                    Green = 34,
                    Blue = 56,
                    BrightnessPercent = 80,
                    DurationSeconds = 4,
                    TransitionSeconds = 1,
                    TransitionOutSeconds = 1
                }
            }
        });
        SetupHttpResponse(
            HttpStatusCode.OK,
            "{\"data\":[{\"channels\":[{\"channel_id\":0},{\"channel_id\":1}]}]}");
        var streamTester = new Mock<IHueStreamTester>();
        streamTester
            .Setup(tester => tester.PreviewAsync(
                "192.168.1.100",
                "global-app-secret",
                "global-client-secret",
                "global-area",
                It.IsAny<JsonElement>(),
                It.IsAny<IReadOnlySet<int>?>(),
                12,
                34,
                56,
                80,
                4,
                It.IsAny<CancellationToken>(),
                1,
                1,
                PluginConfiguration.ColorPresetEffectPulse,
                150))
            .ReturnsAsync(new HueStreamProbeResult { Succeeded = true, Message = "Saved scene preview completed." });
        var service = new HueSceneAutomationService(
            streamTester.Object,
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(streamTester.Object, hostedServices: new[] { service });

        var action = await controller.PreviewColorPreset(
            " evening ",
            new HueSavedColorPresetPreviewRequest());

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HuePreviewResult>(response.Value);
        Assert.True(result.Succeeded);
        Assert.False(result.TargetAllEnabledMappings);
        Assert.Equal("Pulse", result.Effect);
        Assert.Equal(150, result.EffectSpeedPercent);
        Assert.Equal(4, result.DurationSeconds);
        Assert.Equal(1, result.TransitionSeconds);
        Assert.Equal(1, result.TransitionOutSeconds);
        var target = Assert.Single(result.TargetResults);
        Assert.Equal("Default bridge target", target.TargetLabel);
        Assert.Equal(2, target.AvailableChannelCount);
        Assert.Equal(2, target.SelectedChannelCount);
        streamTester.Verify(tester => tester.PreviewAsync(
            "192.168.1.100",
            "global-app-secret",
            "global-client-secret",
            "global-area",
            It.IsAny<JsonElement>(),
            It.IsAny<IReadOnlySet<int>?>(),
            12,
            34,
            56,
            80,
            4,
            It.IsAny<CancellationToken>(),
            1,
            1,
            PluginConfiguration.ColorPresetEffectPulse,
            150), Times.Once);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("global-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("global-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewColorPreset_AllTargetsUsesEachSavedTargetProfileAndReturnsSanitizedOutcomes()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "global-app-secret",
            HueClientKey = "global-client-secret",
            EntertainmentAreaId = "global-area",
            ColorPresets = new List<HueColorPreset>
            {
                new()
                {
                    Name = "Kitchen Glow",
                    Effect = PluginConfiguration.ColorPresetEffectCandle,
                    EffectSpeedPercent = 225,
                    Red = 230,
                    Green = 90,
                    Blue = 20,
                    BrightnessPercent = 75,
                    DurationSeconds = 5,
                    TransitionSeconds = 2,
                    TransitionOutSeconds = 1
                }
            },
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-kitchen",
                    UserName = "Kitchen",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "mapping-app-secret",
                    HueClientKey = "mapping-client-secret",
                    EntertainmentAreaId = "kitchen-area",
                    ChannelIdsOverride = "1"
                }
            }
        });
        SetupHttpResponse(
            HttpStatusCode.OK,
            "{\"data\":[{\"channels\":[{\"channel_id\":0},{\"channel_id\":1}]}]}");
        var streamTester = new Mock<IHueStreamTester>();
        streamTester
            .Setup(tester => tester.PreviewAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<JsonElement>(),
                It.IsAny<IReadOnlySet<int>?>(),
                230,
                90,
                20,
                75,
                5,
                It.IsAny<CancellationToken>(),
                2,
                1,
                PluginConfiguration.ColorPresetEffectCandle,
                225))
            .Returns((string bridgeIp, string _, string _, string _, JsonElement _, IReadOnlySet<int>? _, int _, int _, int _, int _, int _, CancellationToken _, int _, int _, string _, int _) =>
                Task.FromResult(new HueStreamProbeResult
                {
                    Succeeded = !string.Equals(bridgeIp, "192.168.1.101", StringComparison.Ordinal),
                    Message = string.Equals(bridgeIp, "192.168.1.101", StringComparison.Ordinal)
                        ? "Kitchen saved-scene preview failed."
                        : "Default saved-scene preview completed."
                }));
        var service = new HueSceneAutomationService(
            streamTester.Object,
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(streamTester.Object, hostedServices: new[] { service });

        var action = await controller.PreviewColorPreset(
            "Kitchen Glow",
            new HueSavedColorPresetPreviewRequest { TargetAllEnabledMappings = true });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HuePreviewResult>(response.Value);
        Assert.True(result.TargetAllEnabledMappings);
        Assert.False(result.Succeeded);
        Assert.Equal(2, result.TargetResults.Count);
        Assert.Equal("Default bridge target", result.TargetResults[0].TargetLabel);
        Assert.True(result.TargetResults[0].Succeeded);
        Assert.Equal(2, result.TargetResults[0].SelectedChannelCount);
        Assert.Equal("Kitchen", result.TargetResults[1].TargetLabel);
        Assert.False(result.TargetResults[1].Succeeded);
        Assert.Equal(1, result.TargetResults[1].SelectedChannelCount);
        Assert.Equal(4, result.AvailableChannelCount);
        Assert.Equal(3, result.SelectedChannelCount);
        streamTester.Verify(tester => tester.PreviewAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<JsonElement>(),
            It.IsAny<IReadOnlySet<int>?>(), 230, 90, 20, 75, 5, It.IsAny<CancellationToken>(), 2, 1,
            PluginConfiguration.ColorPresetEffectCandle, 225), Times.Exactly(2));
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("global-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("global-client-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("mapping-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("mapping-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewColorPreset_MissingSceneReturnsNotFoundWithoutContactingBridge()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "global-app-secret",
            HueClientKey = "global-client-secret",
            EntertainmentAreaId = "global-area"
        });
        var action = await CreateController(Mock.Of<IHueStreamTester>()).PreviewColorPreset(
            "Missing",
            new HueSavedColorPresetPreviewRequest());

        Assert.IsType<NotFoundObjectResult>(action.Result);
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ScenePlaylists_CrudAndPreviewRunsOrderedScenesWithoutReturningCredentials()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "playlist-global-app-secret",
            HueClientKey = "playlist-global-client-secret",
            EntertainmentAreaId = "global-area",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Warm", Red = 25, Green = 50, Blue = 75, DurationSeconds = 1 },
                new() { Name = "Cool", Red = 220, Green = 180, Blue = 140, DurationSeconds = 2 }
            }
        });
        var controller = CreateController();

        var saved = controller.SaveScenePlaylist(new HueScenePlaylistRequest
        {
            Name = " Evening sequence ",
            PresetNames = new List<string> { "Warm", "Cool" }
        });
        var savedResponse = Assert.IsType<OkObjectResult>(saved.Result);
        var savedResult = Assert.IsType<HueScenePlaylistResult>(savedResponse.Value);
        Assert.Equal("Evening sequence", savedResult.Name);
        Assert.Equal(3, savedResult.TotalDurationSeconds);
        Assert.Equal("Default bridge target", savedResult.TargetLabel);
        Assert.DoesNotContain("playlist-global-app-secret", JsonSerializer.Serialize(savedResult), StringComparison.Ordinal);

        var listedResponse = Assert.IsType<OkObjectResult>(controller.GetScenePlaylists().Result);
        var listed = Assert.IsAssignableFrom<IEnumerable<HueScenePlaylistResult>>(listedResponse.Value).ToArray();
        Assert.Single(listed);
        Assert.Equal(new[] { "Warm", "Cool" }, listed[0].PresetNames);

        SetupHttpResponse(
            HttpStatusCode.OK,
            "{\"data\":[{\"channels\":[{\"channel_id\":0}]}]}");
        var streamTester = new Mock<IHueStreamTester>();
        streamTester
            .Setup(tester => tester.PreviewAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<JsonElement>(),
                It.IsAny<IReadOnlySet<int>?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<int>()))
            .ReturnsAsync(new HueStreamProbeResult
            {
                Succeeded = true,
                Message = "Playlist step completed."
            });
        var service = new HueSceneAutomationService(
            streamTester.Object,
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        controller = CreateController(streamTester.Object, hostedServices: new[] { service });

        var preview = await controller.PreviewScenePlaylist(
            "evening sequence",
            new HueScenePlaylistPreviewRequest());

        var previewResponse = Assert.IsType<OkObjectResult>(preview.Result);
        var result = Assert.IsType<HueScenePlaylistRunResult>(previewResponse.Value);
        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "Warm", "Cool" }, result.Steps.Select(step => step.PresetName));
        Assert.Equal(new[] { 25, 220 }, streamTester.Invocations
            .Where(invocation => invocation.Method.Name == nameof(IHueStreamTester.PreviewAsync))
            .Select(invocation => (int)invocation.Arguments[6]!)
            .ToArray());
        var target = Assert.Single(result.TargetResults);
        Assert.True(target.Succeeded);
        Assert.Equal(2, target.CompletedStepCount);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("playlist-global-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("playlist-global-client-secret", serialized, StringComparison.Ordinal);

        var duplicate = controller.DuplicateScenePlaylist("evening sequence");
        var duplicateResponse = Assert.IsType<OkObjectResult>(duplicate.Result);
        var duplicateResult = Assert.IsType<HueScenePlaylistResult>(duplicateResponse.Value);
        Assert.Equal("Evening sequence (Copy)", duplicateResult.Name);
        Assert.Equal(2, configuration.ScenePlaylists.Count);

        var deleted = controller.DeleteScenePlaylist(duplicateResult.Name);
        Assert.IsType<OkObjectResult>(deleted);
        Assert.Single(configuration.ScenePlaylists);
    }

    [Fact]
    public async Task ScenePlaylists_AllTargetsAggregatesEachStepAndNeverLeaksMappingCredentials()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "playlist-global-app-secret",
            HueClientKey = "playlist-global-client-secret",
            EntertainmentAreaId = "global-area",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Broadcast scene", Red = 100, Green = 40, Blue = 20, DurationSeconds = 1 },
                new() { Name = "Broadcast finale", Red = 240, Green = 180, Blue = 60, DurationSeconds = 1 }
            },
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-kitchen",
                    UserName = "Kitchen",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "playlist-mapping-app-secret",
                    HueClientKey = "playlist-mapping-client-secret",
                    EntertainmentAreaId = "kitchen-area",
                    ChannelIdsOverride = "1"
                }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "broadcast-playlist",
                    Name = "Broadcast",
                    PresetNames = new List<string> { "Broadcast scene", "Broadcast finale" },
                    TargetAllEnabledMappings = true
                }
            }
        });
        SetupHttpResponse(
            HttpStatusCode.OK,
            "{\"data\":[{\"channels\":[{\"channel_id\":0},{\"channel_id\":1}]}]}");
        var streamTester = new Mock<IHueStreamTester>();
        streamTester
            .Setup(tester => tester.PreviewAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<JsonElement>(), It.IsAny<IReadOnlySet<int>?>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string bridgeIp, string _, string _, string _, JsonElement _, IReadOnlySet<int>? _, int _, int _, int _, int _, int _, CancellationToken _, int _, int _, string _, int _) =>
                Task.FromResult(new HueStreamProbeResult
                {
                    Succeeded = !string.Equals(bridgeIp, "192.168.1.101", StringComparison.Ordinal),
                    Message = string.Equals(bridgeIp, "192.168.1.101", StringComparison.Ordinal)
                        ? "Kitchen playlist step failed."
                        : "Default playlist step completed."
                }));
        var service = new HueSceneAutomationService(
            streamTester.Object,
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(streamTester.Object, hostedServices: new[] { service });

        var action = await controller.PreviewScenePlaylist(
            "Broadcast",
            new HueScenePlaylistPreviewRequest());

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueScenePlaylistRunResult>(response.Value);
        Assert.True(result.TargetAllEnabledMappings);
        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Steps.Count);
        Assert.Equal(2, result.TargetResults.Count);
        var defaultTarget = Assert.Single(result.TargetResults.Where(target => target.TargetLabel == "Default bridge target"));
        Assert.True(defaultTarget.Succeeded);
        Assert.Equal(2, defaultTarget.CompletedStepCount);
        var kitchenTarget = Assert.Single(result.TargetResults.Where(target => target.TargetLabel == "Kitchen"));
        Assert.False(kitchenTarget.Succeeded);
        Assert.Equal(2, kitchenTarget.CompletedStepCount);
        streamTester.Verify(tester => tester.PreviewAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<JsonElement>(),
            It.IsAny<IReadOnlySet<int>?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(),
            It.IsAny<int>()), Times.Exactly(4));
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("playlist-global-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("playlist-global-client-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("playlist-mapping-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("playlist-mapping-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScenePlaylists_MissingSceneReturnsBadRequestWithoutContactingBridge()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "playlist-app-secret",
            HueClientKey = "playlist-client-secret",
            EntertainmentAreaId = "global-area",
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "broken-playlist",
                    Name = "Broken",
                    PresetNames = new List<string> { "Missing scene" }
                }
            }
        });
        var action = await CreateController(Mock.Of<IHueStreamTester>()).PreviewScenePlaylist(
            "Broken",
            new HueScenePlaylistPreviewRequest());

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("scene playlist", response.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Preview_WithInvalidColorOrDurationReturnsBadRequestWithoutTouchingBridge()
    {
        var controller = CreateController(Mock.Of<IHueStreamTester>());

        var invalidColor = await controller.Preview(new HuePreviewRequest
        {
            IpAddress = "192.168.1.100",
            AppKey = "app-key",
            ClientKey = "client-key",
            EntertainmentAreaId = "area-1",
            Red = 256
        });
        Assert.IsType<BadRequestObjectResult>(invalidColor.Result);

        var invalidDuration = await controller.Preview(new HuePreviewRequest
        {
            IpAddress = "192.168.1.100",
            AppKey = "app-key",
            ClientKey = "client-key",
            EntertainmentAreaId = "area-1",
            DurationSeconds = HueStreamTester.MaxPreviewDurationSeconds + 1
        });
        Assert.IsType<BadRequestObjectResult>(invalidDuration.Result);

        var invalidTransition = await controller.Preview(new HuePreviewRequest
        {
            IpAddress = "192.168.1.100",
            AppKey = "app-key",
            ClientKey = "client-key",
            EntertainmentAreaId = "area-1",
            DurationSeconds = 4,
            TransitionSeconds = 5
        });
        Assert.IsType<BadRequestObjectResult>(invalidTransition.Result);

        var invalidCombinedTransition = await controller.Preview(new HuePreviewRequest
        {
            IpAddress = "192.168.1.100",
            AppKey = "app-key",
            ClientKey = "client-key",
            EntertainmentAreaId = "area-1",
            DurationSeconds = 4,
            TransitionSeconds = 2,
            TransitionOutSeconds = 3
        });
        Assert.IsType<BadRequestObjectResult>(invalidCombinedTransition.Result);

        var invalidEffect = await controller.Preview(new HuePreviewRequest
        {
            IpAddress = "192.168.1.100",
            AppKey = "app-key",
            ClientKey = "client-key",
            EntertainmentAreaId = "area-1",
            Effect = "Strobe"
        });
        Assert.IsType<BadRequestObjectResult>(invalidEffect.Result);

        var invalidEffectSpeed = await controller.Preview(new HuePreviewRequest
        {
            IpAddress = "192.168.1.100",
            AppKey = "app-key",
            ClientKey = "client-key",
            EntertainmentAreaId = "area-1",
            EffectSpeedPercent = PluginConfiguration.MinColorPresetEffectSpeedPercent - 1
        });
        var invalidEffectSpeedResponse = Assert.IsType<BadRequestObjectResult>(invalidEffectSpeed.Result);
        Assert.Contains("effect speed", invalidEffectSpeedResponse.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public void GetColorPresets_ReturnsSortedVisualScenesWithoutCredentials()
    {
        InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Zest", Red = 255, Green = 20, Blue = 10 },
                new() { Name = "Ambient", Effect = PluginConfiguration.ColorPresetEffectPulse, EffectSpeedPercent = 150, Red = 10, Green = 20, Blue = 30, BrightnessPercent = 60, DurationSeconds = 7, TransitionSeconds = 3, TransitionOutSeconds = 2 }
            }
        });

        var action = CreateController().GetColorPresets();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var presets = Assert.IsAssignableFrom<IEnumerable<HueColorPresetResult>>(response.Value).ToArray();
        Assert.Equal(new[] { "Ambient", "Zest" }, presets.Select(preset => preset.Name));
        Assert.Equal(60, presets[0].BrightnessPercent);
        Assert.Equal(7, presets[0].DurationSeconds);
        Assert.Equal(3, presets[0].TransitionSeconds);
        Assert.Equal(2, presets[0].TransitionOutSeconds);
        Assert.Equal(PluginConfiguration.ColorPresetEffectPulse, presets[0].Effect);
        Assert.Equal(150, presets[0].EffectSpeedPercent);
        var serialized = System.Text.Json.JsonSerializer.Serialize(presets);
        Assert.DoesNotContain("HueAppKey", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HueClientKey", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SaveColorPreset_AddsAndUpdatesByName()
    {
        var configuration = InstallConfiguration(new PluginConfiguration());

        var addAction = CreateController().SaveColorPreset(new HueColorPresetRequest
        {
            Name = " Movie Night ",
            Red = 230,
            Green = 90,
            Blue = 20,
            Effect = "Pulse",
            EffectSpeedPercent = 175,
            BrightnessPercent = 75,
            DurationSeconds = 8,
            TransitionSeconds = 2,
            TransitionOutSeconds = 2
        });
        Assert.IsType<OkObjectResult>(addAction.Result);

        var updateAction = CreateController().SaveColorPreset(new HueColorPresetRequest
        {
            Name = "movie night",
            Red = 10,
            Green = 40,
            Blue = 200,
            BrightnessPercent = 55,
            DurationSeconds = 3,
            TransitionSeconds = 1,
            TransitionOutSeconds = 1,
            EffectSpeedPercent = 125
        });
        Assert.IsType<OkObjectResult>(updateAction.Result);

        var preset = Assert.Single(configuration.ColorPresets);
        Assert.Equal("movie night", preset.Name);
        Assert.Equal(10, preset.Red);
        Assert.Equal(PluginConfiguration.ColorPresetEffectSolid, preset.Effect);
        Assert.Equal(55, preset.BrightnessPercent);
        Assert.Equal(3, preset.DurationSeconds);
        Assert.Equal(1, preset.TransitionSeconds);
        Assert.Equal(1, preset.TransitionOutSeconds);
        Assert.Equal(125, preset.EffectSpeedPercent);
    }

    [Fact]
    public void SaveColorPreset_InvalidValuesReturnsBadRequestWithoutSaving()
    {
        var configuration = InstallConfiguration(new PluginConfiguration());

        var action = CreateController().SaveColorPreset(new HueColorPresetRequest
        {
            Name = "Broken",
            Red = 256,
            DurationSeconds = PluginConfiguration.MaxPreviewDurationSeconds + 1
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Empty(configuration.ColorPresets);
    }

    [Fact]
    public void DuplicateColorPreset_CopiesVisualMetadataWithUniqueName()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset>
            {
                new()
                {
                    Name = "Movie Night",
                    Effect = PluginConfiguration.ColorPresetEffectCandle,
                    EffectSpeedPercent = 225,
                    Red = 230,
                    Green = 90,
                    Blue = 20,
                    BrightnessPercent = 75,
                    DurationSeconds = 8,
                    TransitionSeconds = 2,
                    TransitionOutSeconds = 3
                }
            }
        });

        var action = CreateController().DuplicateColorPreset(" movie night ");

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueColorPresetResult>(response.Value);
        Assert.Equal("Movie Night (Copy)", result.Name);
        Assert.Equal(PluginConfiguration.ColorPresetEffectCandle, result.Effect);
        Assert.Equal(225, result.EffectSpeedPercent);
        Assert.Equal(8, result.DurationSeconds);
        Assert.Equal(2, result.TransitionSeconds);
        Assert.Equal(3, result.TransitionOutSeconds);
        Assert.Equal(2, configuration.ColorPresets.Count);
        var duplicate = configuration.ColorPresets.Single(preset => preset.Name == result.Name);
        Assert.Equal(230, duplicate.Red);
        Assert.Equal(90, duplicate.Green);
        Assert.Equal(20, duplicate.Blue);
    }

    [Fact]
    public void DuplicateColorPreset_UsesNextUniqueBoundedName()
    {
        var sourceName = new string('A', PluginConfiguration.MaxColorPresetNameLength);
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = sourceName },
                new() { Name = sourceName[..(PluginConfiguration.MaxColorPresetNameLength - " (Copy)".Length)] + " (Copy)" }
            }
        });

        var action = CreateController().DuplicateColorPreset(sourceName);

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueColorPresetResult>(response.Value);
        Assert.Equal(PluginConfiguration.MaxColorPresetNameLength, result.Name.Length);
        Assert.EndsWith(" (Copy 2)", result.Name, StringComparison.Ordinal);
        Assert.Equal(3, configuration.ColorPresets.Count);
    }

    [Fact]
    public void DuplicateColorPreset_RejectsMissingSourceAndCapacityLimit()
    {
        var missingConfiguration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Existing" } }
        });

        var missing = CreateController().DuplicateColorPreset("Missing");
        Assert.IsType<NotFoundObjectResult>(missing.Result);
        Assert.Single(missingConfiguration.ColorPresets);

        var fullConfiguration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = Enumerable.Range(0, PluginConfiguration.MaxColorPresets)
                .Select(index => new HueColorPreset { Name = $"Scene {index}" })
                .ToList()
        });

        var full = CreateController().DuplicateColorPreset("Scene 0");
        var response = Assert.IsType<ConflictObjectResult>(full.Result);
        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        Assert.Equal(PluginConfiguration.MaxColorPresets, fullConfiguration.ColorPresets.Count);
    }

    [Fact]
    public void DeleteColorPreset_RemovesSceneAndReportsMissingNames()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Accent" } }
        });
        var controller = CreateController();

        var deleted = controller.DeleteColorPreset("accent");
        Assert.IsType<OkObjectResult>(deleted);
        Assert.Empty(configuration.ColorPresets);

        var missing = controller.DeleteColorPreset("accent");
        Assert.IsType<NotFoundObjectResult>(missing);
    }

    [Fact]
    public void SceneSchedules_CrudUsesSavedScenesAndNeverReturnsCredentials()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "secret-app-key",
            HueClientKey = "secret-client-key",
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening", EffectSpeedPercent = 150, Red = 12, Green = 34, Blue = 56, DurationSeconds = 8, TransitionSeconds = 4, TransitionOutSeconds = 2 } },
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = "user-1", UserName = "Living Room", SyncEnabled = true }
            }
        });
        var controller = CreateController();

        var saved = controller.SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Name = " Evening Cue ",
            PresetName = "evening",
            Priority = 42,
            TargetUserId = "user-1",
            TimeOfDay = "07:05",
            TimeZoneId = TimeZoneInfo.Utc.Id,
            StartDate = " 2026-08-01 ",
            EndDate = "2026-12-31",
            ExcludedDates = new List<string> { "2026-12-31", " 2026-12-24 ", "2026-12-31" },
            DaysOfWeekMask = 1 | 32,
            DurationSeconds = 12,
            MaxRuns = 3,
            Enabled = true
        });

        var savedResponse = Assert.IsType<OkObjectResult>(saved.Result);
        var savedResult = Assert.IsType<HueSceneScheduleResult>(savedResponse.Value);
        Assert.False(string.IsNullOrWhiteSpace(savedResult.Id));
        Assert.Equal("Living Room", savedResult.TargetLabel);
        Assert.Equal(42, savedResult.Priority);
        Assert.Equal("07:05", savedResult.TimeOfDay);
        Assert.Equal(TimeZoneInfo.Utc.Id, savedResult.TimeZoneId);
        Assert.Equal("2026-08-01", savedResult.StartDate);
        Assert.Equal("2026-12-31", savedResult.EndDate);
        Assert.Equal(new[] { "2026-12-24", "2026-12-31" }, savedResult.ExcludedDates);
        Assert.Equal(12, savedResult.DurationSeconds);
        Assert.Equal(3, savedResult.MaxRuns);
        Assert.Equal(0, savedResult.RunCount);
        Assert.Equal(4, savedResult.TransitionSeconds);
        Assert.Equal(2, savedResult.TransitionOutSeconds);
        Assert.Equal(150, savedResult.EffectSpeedPercent);
        Assert.Equal(new[] { "2026-12-24", "2026-12-31" }, configuration.SceneSchedules[0].ExcludedDates);
        Assert.Equal(12, configuration.SceneSchedules[0].DurationSeconds);
        Assert.Equal(42, configuration.SceneSchedules[0].Priority);
        Assert.Single(configuration.SceneSchedules);

        var updated = controller.SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Id = savedResult.Id,
            Name = "Evening Cue Updated",
            PresetName = "Evening",
            TimeOfDay = "21:30",
            DaysOfWeekMask = 127,
            Enabled = false
        });
        Assert.IsType<OkObjectResult>(updated.Result);
        Assert.Equal("Evening Cue Updated", Assert.Single(configuration.SceneSchedules).Name);
        Assert.False(configuration.SceneSchedules[0].Enabled);
        Assert.Equal(3, configuration.SceneSchedules[0].MaxRuns);
        Assert.Equal(0, configuration.SceneSchedules[0].RunCount);
        Assert.Equal(42, configuration.SceneSchedules[0].Priority);

        var list = controller.GetSceneSchedules();
        var listResponse = Assert.IsType<OkObjectResult>(list.Result);
        var listed = Assert.Single(Assert.IsAssignableFrom<IEnumerable<HueSceneScheduleResult>>(listResponse.Value));
        Assert.Equal("Evening Cue Updated", listed.Name);
        Assert.Equal(42, listed.Priority);
        Assert.Equal(string.Empty, listed.TimeZoneId);
        var serialized = System.Text.Json.JsonSerializer.Serialize(listed);
        Assert.DoesNotContain("secret-app-key", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-client-key", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("HueAppKey", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HueClientKey", serialized, StringComparison.OrdinalIgnoreCase);

        Assert.IsType<OkObjectResult>(controller.DeleteSceneSchedule(savedResult.Id));
        Assert.Empty(configuration.SceneSchedules);
    }

    [Fact]
    public void SceneSchedules_CrudSupportsPlaylistSourcesAndReportsAggregateDuration()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "playlist-api-app-secret",
            HueClientKey = "playlist-api-client-secret",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Warm", DurationSeconds = 2 },
                new() { Name = "Cool", DurationSeconds = 3 }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new() { Id = "playlist-api", Name = "API sequence", PresetNames = new List<string> { "Warm", "Cool" } }
            }
        });
        var controller = CreateController();

        var saved = controller.SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Name = "Playlist cue",
            PlaylistName = "API sequence",
            TimeOfDay = "07:05",
            TimeZoneId = TimeZoneInfo.Utc.Id,
            Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
            DaysOfWeekMask = 0
        });

        var response = Assert.IsType<OkObjectResult>(saved.Result);
        var result = Assert.IsType<HueSceneScheduleResult>(response.Value);
        Assert.Equal(string.Empty, result.PresetName);
        Assert.Equal("API sequence", result.PlaylistName);
        Assert.Equal(PluginConfiguration.SceneScheduleEffectPlaylist, result.Effect);
        Assert.Equal(2, result.PlaylistStepCount);
        Assert.Equal(5, result.PlaylistTotalDurationSeconds);
        Assert.Equal(0, result.DurationSeconds);
        Assert.Equal(0, result.TransitionSeconds);
        Assert.Equal("API sequence", Assert.Single(configuration.SceneSchedules).PlaylistName);

        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("playlist-api-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("playlist-api-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void SceneSchedules_BroadcastTargetRoundTripsAndPreservesOnPartialUpdate()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Welcome" } },
            SceneSchedules = new List<HueSceneSchedule>()
        });
        var controller = CreateController();

        var saved = controller.SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Name = "Whole home welcome",
            PresetName = "Welcome",
            TargetAllEnabledMappings = true,
            TimeOfDay = "08:00",
            DaysOfWeekMask = 127
        });

        var savedResult = Assert.IsType<HueSceneScheduleResult>(Assert.IsType<OkObjectResult>(saved.Result).Value);
        Assert.True(savedResult.TargetAllEnabledMappings);
        Assert.Equal("All enabled targets", savedResult.TargetLabel);
        Assert.True(configuration.SceneSchedules[0].TargetAllEnabledMappings);

        var updated = controller.SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Id = savedResult.Id,
            Name = "Whole home welcome updated",
            PresetName = "Welcome",
            TimeOfDay = "09:00",
            DaysOfWeekMask = 127,
            Enabled = false
        });

        var updatedResult = Assert.IsType<HueSceneScheduleResult>(Assert.IsType<OkObjectResult>(updated.Result).Value);
        Assert.True(updatedResult.TargetAllEnabledMappings);
        Assert.Equal("All enabled targets", updatedResult.TargetLabel);
        Assert.True(configuration.SceneSchedules[0].TargetAllEnabledMappings);
    }

    [Fact]
    public void SceneSchedules_DuplicateCreatesDisabledFreshCueWithUniqueIdentity()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "original-cue",
                    Name = "Evening Cue",
                    PresetName = "Evening",
                    Priority = 73,
                    TargetUserId = "",
                    TimeOfDay = "21:30",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceMonthlyWeekday,
                    RecurrenceInterval = 2,
                    WeekOfMonth = PluginConfiguration.SceneScheduleLastWeekOfMonth,
                    DayOfWeek = (int)DayOfWeek.Friday,
                    StartDate = "2026-01-01",
                    ExcludedDates = new List<string> { "2026-12-25" },
                    MaxRuns = 4,
                    RunCount = 2,
                    Enabled = true
                }
            }
        });
        var controller = CreateController();

        var action = controller.DuplicateSceneSchedule(" original-cue ");

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var duplicate = Assert.IsType<HueSceneScheduleResult>(response.Value);
        Assert.NotEqual("original-cue", duplicate.Id);
        Assert.Equal("Evening Cue (Copy)", duplicate.Name);
        Assert.Equal("Evening", duplicate.PresetName);
        Assert.Equal(73, duplicate.Priority);
        Assert.Equal(TimeZoneInfo.Utc.Id, duplicate.TimeZoneId);
        Assert.Equal(PluginConfiguration.SceneScheduleRecurrenceMonthlyWeekday, duplicate.Recurrence);
        Assert.Equal(2, duplicate.RecurrenceInterval);
        Assert.Equal(PluginConfiguration.SceneScheduleLastWeekOfMonth, duplicate.WeekOfMonth);
        Assert.Equal((int)DayOfWeek.Friday, duplicate.DayOfWeek);
        Assert.Equal(new[] { "2026-12-25" }, duplicate.ExcludedDates);
        Assert.Equal(4, duplicate.MaxRuns);
        Assert.Equal(0, duplicate.RunCount);
        Assert.False(duplicate.Enabled);

        Assert.Equal(2, configuration.SceneSchedules.Count);
        var savedDuplicate = configuration.SceneSchedules.Single(schedule => schedule.Id == duplicate.Id);
        Assert.False(savedDuplicate.Enabled);
        Assert.Equal(0, savedDuplicate.RunCount);
        Assert.Equal(73, savedDuplicate.Priority);

        var secondAction = controller.DuplicateSceneSchedule("original-cue");
        var secondResponse = Assert.IsType<OkObjectResult>(secondAction.Result);
        var secondDuplicate = Assert.IsType<HueSceneScheduleResult>(secondResponse.Value);
        Assert.Equal("Evening Cue (Copy 2)", secondDuplicate.Name);
        Assert.NotEqual(duplicate.Id, secondDuplicate.Id);
    }

    [Fact]
    public void SceneSchedules_DuplicateAtConfiguredLimitReturnsConflictWithoutMutation()
    {
        var schedules = Enumerable.Range(1, PluginConfiguration.MaxSceneSchedules)
            .Select(index => new HueSceneSchedule
            {
                Id = $"cue-{index}",
                Name = $"Cue {index}",
                PresetName = "Evening",
                TimeOfDay = "20:00",
                DaysOfWeekMask = PluginConfiguration.AllSceneScheduleDaysMask
            })
            .ToList();
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            SceneSchedules = schedules
        });
        var controller = CreateController();

        var action = controller.DuplicateSceneSchedule("cue-1");

        var response = Assert.IsType<ConflictObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        Assert.Equal(PluginConfiguration.MaxSceneSchedules, configuration.SceneSchedules.Count);
    }

    [Fact]
    public void SceneSchedules_DuplicateBoundsLongGeneratedName()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "long-name-cue",
                    Name = new string('L', PluginConfiguration.MaxSceneScheduleNameLength),
                    PresetName = "Evening",
                    TimeOfDay = "20:00",
                    DaysOfWeekMask = PluginConfiguration.AllSceneScheduleDaysMask
                }
            }
        });

        var action = CreateController().DuplicateSceneSchedule("long-name-cue");

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var duplicate = Assert.IsType<HueSceneScheduleResult>(response.Value);
        Assert.True(duplicate.Name.Length <= PluginConfiguration.MaxSceneScheduleNameLength);
        Assert.EndsWith(" (Copy)", duplicate.Name, StringComparison.Ordinal);
        Assert.False(duplicate.Enabled);
    }

    [Fact]
    public void SceneSchedules_SavesAndReturnsOneTimeCueWithoutWeekdayMask()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            HueAppKey = "one-time-app-secret",
            HueClientKey = "one-time-client-secret",
            ColorPresets = new List<HueColorPreset> { new() { Name = "Movie Night" } }
        });
        var controller = CreateController();

        var action = controller.SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Name = "Premiere cue",
            PresetName = "Movie Night",
            TimeOfDay = "21:30",
            TimeZoneId = TimeZoneInfo.Utc.Id,
            RunDate = " 2026-12-24 ",
            DaysOfWeekMask = 0,
            Enabled = true
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueSceneScheduleResult>(response.Value);
        Assert.Equal("2026-12-24", result.RunDate);
        Assert.Equal(0, result.DaysOfWeekMask);
        var saved = Assert.Single(configuration.SceneSchedules);
        Assert.Equal("2026-12-24", saved.RunDate);
        Assert.Empty(saved.StartDate);
        Assert.Empty(saved.EndDate);
        Assert.Empty(saved.ExcludedDates);

        var serialized = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("one-time-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("one-time-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void SceneSchedules_CrudPreservesMonthlyRecurrence()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Month End" } }
        });
        var controller = CreateController();

        var action = controller.SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Name = "Month-end cue",
            PresetName = "Month End",
            TimeOfDay = "21:30",
            Recurrence = PluginConfiguration.SceneScheduleRecurrenceMonthly,
            DayOfMonth = 31,
            DaysOfWeekMask = 0,
            Enabled = true
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueSceneScheduleResult>(response.Value);
        Assert.Equal(PluginConfiguration.SceneScheduleRecurrenceMonthly, result.Recurrence);
        Assert.Equal(31, result.DayOfMonth);
        var saved = Assert.Single(configuration.SceneSchedules);
        Assert.Equal(PluginConfiguration.SceneScheduleRecurrenceMonthly, saved.Recurrence);
        Assert.Equal(31, saved.DayOfMonth);

        var listedResponse = Assert.IsType<OkObjectResult>(controller.GetSceneSchedules().Result);
        var listed = Assert.Single(Assert.IsAssignableFrom<IEnumerable<HueSceneScheduleResult>>(listedResponse.Value));
        Assert.Equal(PluginConfiguration.SceneScheduleRecurrenceMonthly, listed.Recurrence);
        Assert.Equal(31, listed.DayOfMonth);
    }

    [Fact]
    public void SceneSchedules_CrudPreservesDailyRecurrenceWithoutWeekdayMask()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Everyday" } }
        });
        var controller = CreateController();

        var action = controller.SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Name = "Everyday cue",
            PresetName = "Everyday",
            TimeOfDay = "07:05",
            Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
            DaysOfWeekMask = 0,
            Enabled = true
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueSceneScheduleResult>(response.Value);
        Assert.Equal(PluginConfiguration.SceneScheduleRecurrenceDaily, result.Recurrence);
        Assert.Equal(0, result.DaysOfWeekMask);
        var saved = Assert.Single(configuration.SceneSchedules);
        Assert.Equal(PluginConfiguration.SceneScheduleRecurrenceDaily, saved.Recurrence);

        var listedResponse = Assert.IsType<OkObjectResult>(controller.GetSceneSchedules().Result);
        var listed = Assert.Single(Assert.IsAssignableFrom<IEnumerable<HueSceneScheduleResult>>(listedResponse.Value));
        Assert.Equal(PluginConfiguration.SceneScheduleRecurrenceDaily, listed.Recurrence);
    }

    [Fact]
    public void SceneSchedules_CrudPreservesMonthlyWeekdayRecurrence()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Weekday" } }
        });
        var controller = CreateController();

        var action = controller.SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Name = "Last Friday cue",
            PresetName = "Weekday",
            TimeOfDay = "18:00",
            Recurrence = PluginConfiguration.SceneScheduleRecurrenceMonthlyWeekday,
            WeekOfMonth = PluginConfiguration.SceneScheduleLastWeekOfMonth,
            DayOfWeek = (int)DayOfWeek.Friday,
            DaysOfWeekMask = 0,
            Enabled = true
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueSceneScheduleResult>(response.Value);
        Assert.Equal(PluginConfiguration.SceneScheduleRecurrenceMonthlyWeekday, result.Recurrence);
        Assert.Equal(PluginConfiguration.SceneScheduleLastWeekOfMonth, result.WeekOfMonth);
        Assert.Equal((int)DayOfWeek.Friday, result.DayOfWeek);
        var saved = Assert.Single(configuration.SceneSchedules);
        Assert.Equal(PluginConfiguration.SceneScheduleLastWeekOfMonth, saved.WeekOfMonth);
        Assert.Equal((int)DayOfWeek.Friday, saved.DayOfWeek);

        var listedResponse = Assert.IsType<OkObjectResult>(controller.GetSceneSchedules().Result);
        var listed = Assert.Single(Assert.IsAssignableFrom<IEnumerable<HueSceneScheduleResult>>(listedResponse.Value));
        Assert.Equal(PluginConfiguration.SceneScheduleRecurrenceMonthlyWeekday, listed.Recurrence);
        Assert.Equal(PluginConfiguration.SceneScheduleLastWeekOfMonth, listed.WeekOfMonth);
        Assert.Equal((int)DayOfWeek.Friday, listed.DayOfWeek);
    }

    [Fact]
    public void SceneSchedules_CrudPreservesYearlyRecurrence()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Annual" } }
        });
        var controller = CreateController();

        var action = controller.SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Name = "New Year's Eve cue",
            PresetName = "Annual",
            TimeOfDay = "23:30",
            Recurrence = PluginConfiguration.SceneScheduleRecurrenceYearly,
            RecurrenceInterval = 2,
            MonthOfYear = 12,
            DayOfMonth = 31,
            StartDate = "2026-01-01",
            DaysOfWeekMask = 0,
            Enabled = true
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueSceneScheduleResult>(response.Value);
        Assert.Equal(PluginConfiguration.SceneScheduleRecurrenceYearly, result.Recurrence);
        Assert.Equal(2, result.RecurrenceInterval);
        Assert.Equal(12, result.MonthOfYear);
        Assert.Equal(31, result.DayOfMonth);
        var saved = Assert.Single(configuration.SceneSchedules);
        Assert.Equal(12, saved.MonthOfYear);
        Assert.Equal(31, saved.DayOfMonth);
        Assert.Equal(2, saved.RecurrenceInterval);

        var listedResponse = Assert.IsType<OkObjectResult>(controller.GetSceneSchedules().Result);
        var listed = Assert.Single(Assert.IsAssignableFrom<IEnumerable<HueSceneScheduleResult>>(listedResponse.Value));
        Assert.Equal(PluginConfiguration.SceneScheduleRecurrenceYearly, listed.Recurrence);
        Assert.Equal(2, listed.RecurrenceInterval);
        Assert.Equal(12, listed.MonthOfYear);
        Assert.Equal(31, listed.DayOfMonth);
    }

    [Fact]
    public void SceneScheduleTimeZones_ReturnsSystemChoicesWithoutCredentials()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueAppKey = "secret-app-key",
            HueClientKey = "secret-client-key"
        });

        var action = CreateController().GetSceneScheduleTimeZones();
        var response = Assert.IsType<OkObjectResult>(action.Result);
        var zones = Assert.IsAssignableFrom<IEnumerable<HueSceneScheduleTimeZoneResult>>(response.Value);
        Assert.Contains(zones, zone => zone.Id == TimeZoneInfo.Utc.Id);
        var serialized = System.Text.Json.JsonSerializer.Serialize(zones);
        Assert.DoesNotContain("secret-app-key", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-client-key", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void SceneSchedules_RejectMissingPresetAndInvalidTimeWithoutSaving()
    {
        var configuration = InstallConfiguration(new PluginConfiguration());

        var action = CreateController().SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Name = "Broken cue",
            PresetName = "Missing",
            TimeOfDay = "25:00",
            ExcludedDates = new List<string> { "2026-02-30" },
            DaysOfWeekMask = 0
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Empty(configuration.SceneSchedules);
    }

    [Fact]
    public void SceneSchedules_RejectInvalidDurationOverrideWithoutSaving()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } }
        });

        var action = CreateController().SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Name = "Broken duration cue",
            PresetName = "Evening",
            TimeOfDay = "20:00",
            DurationSeconds = PluginConfiguration.MaxPreviewDurationSeconds + 1,
            DaysOfWeekMask = 127
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Empty(configuration.SceneSchedules);
    }

    [Fact]
    public void SceneSchedules_RejectInvalidRecurrenceWithoutSaving()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } }
        });

        var action = CreateController().SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Name = "Broken recurrence cue",
            PresetName = "Evening",
            TimeOfDay = "20:00",
            Recurrence = "Hourly",
            DayOfMonth = 1,
            DaysOfWeekMask = 127
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Empty(configuration.SceneSchedules);
    }

    [Fact]
    public void SceneSchedules_RejectNegativeMaximumExecutionsWithoutSaving()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } }
        });

        var action = CreateController().SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Name = "Broken run limit cue",
            PresetName = "Evening",
            TimeOfDay = "20:00",
            MaxRuns = -1,
            DaysOfWeekMask = 127
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Empty(configuration.SceneSchedules);
    }

    [Fact]
    public void SceneSchedules_ResetRunCountReenablesCueThroughAdministratorApi()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "exhausted-cue",
                    Name = "Exhausted cue",
                    PresetName = "Evening",
                    MaxRuns = 2,
                    RunCount = 2,
                    Enabled = false
                }
            }
        });
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        var action = CreateController(hostedServices: new[] { service }).ResetSceneScheduleRunCount(" exhausted-cue ");

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueSceneScheduleResult>(response.Value);
        Assert.Equal(0, result.RunCount);
        Assert.Equal(2, result.MaxRuns);
        Assert.True(result.Enabled);
        Assert.Equal(0, configuration.SceneSchedules[0].RunCount);
        Assert.True(configuration.SceneSchedules[0].Enabled);
    }

    [Fact]
    public void SceneSchedules_EnabledActionTogglesOnlyCueStateThroughAdministratorApi()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "toggle-api-cue",
                    Name = "Toggle API cue",
                    PresetName = "Evening",
                    TimeOfDay = "06:30",
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    StartDate = "2026-08-01",
                    DurationSeconds = 7,
                    MaxRuns = 3,
                    RunCount = 1,
                    Enabled = true
                }
            }
        });
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(hostedServices: new[] { service });

        var disabled = controller.SetSceneScheduleEnabled(
            " toggle-api-cue ",
            new HueSceneScheduleEnabledRequest { Enabled = false });
        var disabledResponse = Assert.IsType<OkObjectResult>(disabled.Result);
        var disabledResult = Assert.IsType<HueSceneScheduleResult>(disabledResponse.Value);
        Assert.False(disabledResult.Enabled);
        Assert.Equal("06:30", disabledResult.TimeOfDay);
        Assert.Equal(PluginConfiguration.SceneScheduleRecurrenceDaily, disabledResult.Recurrence);
        Assert.Equal(1, disabledResult.RunCount);

        var enabled = controller.SetSceneScheduleEnabled(
            "toggle-api-cue",
            new HueSceneScheduleEnabledRequest { Enabled = true });
        var enabledResponse = Assert.IsType<OkObjectResult>(enabled.Result);
        var enabledResult = Assert.IsType<HueSceneScheduleResult>(enabledResponse.Value);
        Assert.True(enabledResult.Enabled);
        Assert.Equal(7, enabledResult.DurationSeconds);
        Assert.Equal(3, enabledResult.MaxRuns);
        Assert.Equal(1, configuration.SceneSchedules[0].RunCount);
    }

    [Fact]
    public void SceneSchedules_EnabledActionRejectsExhaustedCueThroughAdministratorApi()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "exhausted-api-cue",
                    Name = "Exhausted API cue",
                    PresetName = "Evening",
                    MaxRuns = 2,
                    RunCount = 2,
                    Enabled = false
                }
            }
        });
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(hostedServices: new[] { service });

        var action = controller.SetSceneScheduleEnabled(
            "exhausted-api-cue",
            new HueSceneScheduleEnabledRequest { Enabled = true });

        var response = Assert.IsType<ConflictObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        Assert.False(configuration.SceneSchedules[0].Enabled);
        Assert.Equal(2, configuration.SceneSchedules[0].RunCount);
    }

    [Fact]
    public void SceneSchedules_SkipNextActionTogglesOnlyPendingOccurrenceThroughAdministratorApi()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Skip scene" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "skip-api-cue",
                    Name = "Skip API cue",
                    PresetName = "Skip scene",
                    TimeOfDay = "06:30",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    DurationSeconds = 9,
                    MaxRuns = 4,
                    RunCount = 1,
                    Enabled = true
                }
            }
        });
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(hostedServices: new[] { service });

        var skipped = controller.SkipNextSceneSchedule(" skip-api-cue ");
        var skippedResponse = Assert.IsType<OkObjectResult>(skipped.Result);
        var skippedResult = Assert.IsType<HueSceneScheduleResult>(skippedResponse.Value);
        Assert.True(skippedResult.SkipNextOccurrence);
        Assert.True(skippedResult.Enabled);
        Assert.Equal("06:30", skippedResult.TimeOfDay);
        Assert.Equal(1, skippedResult.RunCount);
        Assert.Equal(4, skippedResult.MaxRuns);

        var restored = controller.ClearSkippedSceneSchedule("skip-api-cue");
        var restoredResponse = Assert.IsType<OkObjectResult>(restored.Result);
        var restoredResult = Assert.IsType<HueSceneScheduleResult>(restoredResponse.Value);
        Assert.False(restoredResult.SkipNextOccurrence);
        Assert.True(restoredResult.Enabled);
        Assert.Equal(1, configuration.SceneSchedules[0].RunCount);
    }

    [Fact]
    public void SceneSchedules_SkipNextActionRejectsDisabledCueThroughAdministratorApi()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Skip scene" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "disabled-skip-api-cue",
                    Name = "Disabled skip API cue",
                    PresetName = "Skip scene",
                    TimeOfDay = "06:30",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    Enabled = false
                }
            }
        });
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(hostedServices: new[] { service });

        var action = controller.SkipNextSceneSchedule("disabled-skip-api-cue");

        var response = Assert.IsType<ConflictObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        Assert.False(configuration.SceneSchedules[0].SkipNextOccurrence);
    }

    [Fact]
    public void SceneScheduleHistory_ReturnsSanitizedRunsAndClearsHistory()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            PersistSceneScheduleHistory = true,
            PersistedSceneScheduleHistory = new List<HueSceneScheduleHistoryEntry>
            {
                new()
                {
                    ScheduleId = "cue-1",
                    ScheduleName = "Evening cue",
                    PresetName = "Evening",
                    TargetLabel = "Living Room",
                    Succeeded = true,
                    Message = "Displayed scene.",
                    RunAtUtc = DateTime.UtcNow,
                    RunCount = 2
                }
            }
        });
        var sceneService = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(hostedServices: new IHostedService[] { sceneService });

        var action = controller.GetSceneScheduleHistory(5, " cue-1 ");
        var response = Assert.IsType<OkObjectResult>(action.Result);
        var document = Assert.IsType<HueSceneScheduleHistoryResult>(response.Value);
        var run = Assert.Single(document.Runs);
        Assert.True(document.ServiceAvailable);
        Assert.True(document.PersistenceEnabled);
        Assert.Equal("cue-1", document.ScheduleIdFilter);
        Assert.Equal(2, run.RunCount);
        var serialized = System.Text.Json.JsonSerializer.Serialize(document);
        Assert.DoesNotContain("AppKey", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ClientKey", serialized, StringComparison.OrdinalIgnoreCase);

        var cleared = controller.ClearSceneScheduleHistory();
        var clearedResponse = Assert.IsType<OkObjectResult>(cleared.Result);
        var clearResult = Assert.IsType<HueSceneScheduleHistoryClearResult>(clearedResponse.Value);
        Assert.Equal(1, clearResult.ClearedCount);
        Assert.Empty(configuration.PersistedSceneScheduleHistory);
    }

    [Fact]
    public void DeleteColorPreset_ProtectsScheduledCueReferences()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Accent" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "cue-1", Name = "Accent cue", PresetName = "Accent" }
            }
        });

        var action = CreateController().DeleteColorPreset("accent");

        var response = Assert.IsType<ConflictObjectResult>(action);
        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        Assert.Single(configuration.ColorPresets);
    }

    [Fact]
    public async Task RunSceneSchedule_WithoutHostedAutomationServiceReturnsUnavailable()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Cue" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "cue-1", Name = "Cue", PresetName = "Cue" }
            }
        });

        var action = await CreateController().RunSceneSchedule("cue-1");

        var response = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);
        Assert.Single(configuration.SceneSchedules);
    }

    [Fact]
    public void CancelSceneSchedule_WhenNoManualRunIsActiveReturnsFalseResult()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Cue" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "cue-1", Name = "Cue", PresetName = "Cue" }
            }
        });
        var sceneService = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(hostedServices: new IHostedService[] { sceneService });

        var action = controller.CancelSceneSchedule(" cue-1 ");

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueSceneScheduleCancellationResult>(response.Value);
        Assert.False(result.Canceled);
        Assert.Contains("no manually", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(configuration.SceneSchedules);
    }

    [Fact]
    public async Task TestConnection_WithClientKeyRunsDtlsProbe()
    {
        _httpHandlerMock
            .Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"id\":\"area-1\",\"metadata\":{\"name\":\"Living Room\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"channels\":[{\"channel_id\":1}]}]}",
                    Encoding.UTF8,
                    "application/json")
            });
        var streamTester = new Mock<IHueStreamTester>();
        streamTester
            .Setup(tester => tester.TestAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<System.Text.Json.JsonElement>(),
                It.IsAny<IReadOnlySet<int>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HueStreamProbeResult
            {
                Succeeded = true,
                Message = "DTLS probe succeeded."
            });
        var controller = CreateController(streamTester.Object);
        using var cancellationSource = new CancellationTokenSource();
        var cancellationToken = cancellationSource.Token;

        var action = await controller.TestConnection(new HueConnectionTestRequest
        {
            IpAddress = "192.168.1.100",
            AppKey = "app-key",
            ClientKey = "client-key",
            EntertainmentAreaId = "area-1"
        }, cancellationToken);

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueConnectionTestResult>(response.Value);
        Assert.True(result.StreamTested);
        Assert.True(result.StreamReady);
        Assert.Contains("DTLS stream", result.Message, StringComparison.OrdinalIgnoreCase);
        streamTester.Verify(tester => tester.TestAsync(
            "192.168.1.100",
            "app-key",
            "client-key",
            "area-1",
            It.IsAny<System.Text.Json.JsonElement>(),
            It.IsAny<IReadOnlySet<int>?>(),
            It.Is<CancellationToken>(token => token == cancellationToken)), Times.Once);
    }

    [Fact]
    public void GetStatus_WithoutHostedSyncServiceDoesNotExposeRuntimeSecrets()
    {
        var controller = CreateController();

        var action = controller.GetStatus();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var status = Assert.IsType<HueSyncStatus>(response.Value);
        Assert.False(status.ServiceAvailable);
        Assert.Equal("Unavailable", status.State);
        Assert.Null(status.ActiveBridgeIp);
        Assert.Null(status.ActiveEntertainmentAreaId);
        Assert.Null(status.ActiveTargetFps);
        Assert.Null(status.ActiveFrameResolution);
        Assert.Null(status.ActiveVideoScalingMode);
        Assert.Null(status.ActiveVideoDeinterlaceMode);
        Assert.Null(status.ActiveSamplingBreadthPercent);
        Assert.Null(status.ActiveSamplingMode);
        Assert.Null(status.ActiveColorSmoothingPercent);
        Assert.Null(status.ActiveBrightnessBoost);
        Assert.Null(status.ActiveRedGain);
        Assert.Null(status.ActiveGreenGain);
        Assert.Null(status.ActiveBlueGain);
        Assert.Null(status.ActiveColorSaturation);
        Assert.Null(status.ActiveHueShiftDegrees);
        Assert.Null(status.ActiveOutputBrightnessPercent);
        Assert.Null(status.ActiveBlackoutThreshold);
        Assert.Null(status.ActiveColorChangeThreshold);
        Assert.Null(status.ActiveUseGpu);
        Assert.Null(status.ActiveCustomFfmpegFlagsConfigured);
        Assert.Null(status.ActiveFfmpegStallTimeoutSeconds);
        Assert.Null(status.ActiveNetworkRetryAttempts);
        Assert.Null(status.ActiveChannelIds);
        Assert.Null(status.ActiveRestoreLightState);
        Assert.Null(status.EffectiveFps);
        Assert.Equal(0, status.PacketsSent);
        Assert.Equal(0, status.PacketsSkippedByThreshold);
        Assert.Equal(0, status.PacketSendFailures);
        Assert.Equal(0, status.ReconnectAttempts);
        Assert.Equal(0, status.SeekRestartCount);
        Assert.Null(status.LastSeekPositionSeconds);
        Assert.Null(status.LastError);
        Assert.Null(status.CleanupWarning);
        Assert.Null(status.LastSession);
        Assert.False(status.CanStopSync);
    }

    [Fact]
    public void GetSceneScheduleStatus_WithoutHostedAutomationServiceReturnsUnavailableWithoutSecrets()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "app-secret",
            HueClientKey = "client-secret",
            SceneAutomationEnabled = false,
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "cue-1", Name = "Evening cue", PresetName = "Evening" }
            }
        });

        var action = CreateController().GetSceneScheduleStatus();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var status = Assert.IsType<HueSceneAutomationStatus>(response.Value);
        Assert.False(status.ServiceAvailable);
        Assert.False(status.AutomationEnabled);
        Assert.Empty(status.Schedules);
        var serialized = System.Text.Json.JsonSerializer.Serialize(status);
        Assert.DoesNotContain("app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSceneScheduleStatus_WithHostedAutomationServiceReturnsNextRunWithoutSecrets()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "app-secret",
            HueClientKey = "client-secret",
            EntertainmentAreaId = "area-1",
            SceneAutomationEnabled = false,
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening", EffectSpeedPercent = 135 } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "cue-1",
                    Name = "Evening cue",
                    PresetName = "Evening",
                    Priority = 61,
                    TimeOfDay = "23:59",
                    StartDate = "2026-08-01",
                    EndDate = "2026-12-31",
                    ExcludedDates = new List<string> { "2026-12-24" },
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceMonthly,
                    DayOfMonth = 20,
                    DurationSeconds = 9,
                    DaysOfWeekMask = 127,
                    Enabled = true
                }
            }
        });
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        var action = CreateController(hostedServices: new[] { service }).GetSceneScheduleStatus();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var status = Assert.IsType<HueSceneAutomationStatus>(response.Value);
        var schedule = Assert.Single(status.Schedules);
        Assert.True(status.ServiceAvailable);
        Assert.False(status.AutomationEnabled);
        Assert.Equal("cue-1", schedule.ScheduleId);
        Assert.Equal(61, schedule.Priority);
        Assert.Equal("Default bridge target", schedule.TargetLabel);
        Assert.Equal("2026-08-01", schedule.StartDate);
        Assert.Equal("2026-12-31", schedule.EndDate);
        Assert.Equal(new[] { "2026-12-24" }, schedule.ExcludedDates);
        Assert.Equal(PluginConfiguration.SceneScheduleRecurrenceMonthly, schedule.Recurrence);
        Assert.Equal(20, schedule.DayOfMonth);
        Assert.Equal(9, schedule.DurationSeconds);
        Assert.Equal(135, schedule.EffectSpeedPercent);
        Assert.NotNull(schedule.NextRunLocal);
        var serialized = System.Text.Json.JsonSerializer.Serialize(status);
        Assert.DoesNotContain("app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSceneScheduleOccurrences_ReturnsBoundedSortedPreviewWithoutSecrets()
    {
        var cueTime = DateTime.Now.AddMinutes(10).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "app-secret",
            HueClientKey = "client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Evening", Effect = PluginConfiguration.ColorPresetEffectPulse, EffectSpeedPercent = 175, TransitionSeconds = 2, TransitionOutSeconds = 3 }
            },
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = "user-1", UserName = "Living Room", SyncEnabled = true }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "cue-1",
                    Name = "Living cue",
                    PresetName = "Evening",
                    Priority = 80,
                    TargetUserId = "user-1",
                    TimeOfDay = cueTime,
                    TimeZoneId = TimeZoneInfo.Local.Id,
                    DurationSeconds = 7,
                    DaysOfWeekMask = 127
                },
                new()
                {
                    Id = "cue-2",
                    Name = "Default cue",
                    PresetName = "Evening",
                    TimeOfDay = cueTime,
                    TimeZoneId = TimeZoneInfo.Local.Id,
                    DaysOfWeekMask = 127
                }
            }
        });

        var action = CreateController().GetSceneScheduleOccurrences(limit: 2, days: 7);

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueSceneScheduleOccurrencesResult>(response.Value);
        Assert.Equal(2, result.Limit);
        Assert.Equal(7, result.HorizonDays);
        Assert.Equal(2, result.Occurrences.Count);
        Assert.True(result.Occurrences[0].UtcTime <= result.Occurrences[1].UtcTime);
        Assert.Contains(result.Occurrences, occurrence => occurrence.ScheduleId == "cue-1" && occurrence.DurationSeconds == 7);
        Assert.Contains(result.Occurrences, occurrence => occurrence.ScheduleId == "cue-1" && occurrence.Priority == 80);
        Assert.Contains(result.Occurrences, occurrence => occurrence.ScheduleId == "cue-1" && occurrence.TransitionSeconds == 2);
        Assert.Contains(result.Occurrences, occurrence => occurrence.ScheduleId == "cue-1" && occurrence.TransitionOutSeconds == 3);
        Assert.Contains(result.Occurrences, occurrence => occurrence.ScheduleId == "cue-1" && occurrence.Effect == PluginConfiguration.ColorPresetEffectPulse);
        Assert.Contains(result.Occurrences, occurrence => occurrence.ScheduleId == "cue-1" && occurrence.EffectSpeedPercent == 175);
        Assert.Contains(result.Occurrences, occurrence => occurrence.ScheduleId == "cue-1" && occurrence.Recurrence == PluginConfiguration.SceneScheduleRecurrenceWeekly);
        Assert.Contains(result.Occurrences, occurrence => occurrence.TargetLabel == "Living Room");
        Assert.DoesNotContain(result.Occurrences, occurrence => occurrence.TargetLabel.Contains("secret", StringComparison.OrdinalIgnoreCase));
        var serialized = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("client-secret", serialized, StringComparison.Ordinal);

        var filtered = CreateController().GetSceneScheduleOccurrences(limit: 10, days: 7, scheduleId: "cue-1");
        var filteredResponse = Assert.IsType<OkObjectResult>(filtered.Result);
        var filteredResult = Assert.IsType<HueSceneScheduleOccurrencesResult>(filteredResponse.Value);
        Assert.Equal("cue-1", filteredResult.ScheduleIdFilter);
        Assert.NotEmpty(filteredResult.Occurrences);
        Assert.All(filteredResult.Occurrences, occurrence => Assert.Equal("cue-1", occurrence.ScheduleId));
        Assert.Equal(2, configuration.SceneSchedules.Count);
    }

    [Fact]
    public void GetSceneScheduleCalendar_ReturnsBoundedUtcEventsWithEscapedCredentialFreeMetadata()
    {
        var cueTime = DateTime.Now.AddMinutes(10).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "calendar-app-secret",
            HueClientKey = "calendar-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Evening", Effect = PluginConfiguration.ColorPresetEffectRainbow, EffectSpeedPercent = 225, DurationSeconds = 8, TransitionSeconds = 2, TransitionOutSeconds = 3 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "calendar-cue",
                    Name = "Movie, Night; Cue",
                    PresetName = "Evening",
                    Priority = 64,
                    TimeOfDay = cueTime,
                    TimeZoneId = TimeZoneInfo.Local.Id,
                    DurationSeconds = 4,
                    DaysOfWeekMask = 127
                }
            }
        });

        var action = CreateController().GetSceneScheduleCalendar(limit: 1, days: 7);

        var response = Assert.IsType<FileContentResult>(action);
        Assert.Equal("text/calendar; charset=utf-8", response.ContentType);
        Assert.Equal("jellyfin-hue-scene-cues.ics", response.FileDownloadName);
        var calendar = Encoding.UTF8.GetString(response.FileContents);
        Assert.Contains("BEGIN:VCALENDAR\r\n", calendar, StringComparison.Ordinal);
        Assert.Contains("VERSION:2.0\r\n", calendar, StringComparison.Ordinal);
        Assert.Contains("BEGIN:VEVENT\r\n", calendar, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:Movie\\, Night\\; Cue\r\n", calendar, StringComparison.Ordinal);
        Assert.Contains("DTSTART:", calendar, StringComparison.Ordinal);
        Assert.Contains("DTEND:", calendar, StringComparison.Ordinal);
        Assert.Contains("X-HUE-EFFECT:Rainbow\r\n", calendar, StringComparison.Ordinal);
        Assert.Contains("X-HUE-EFFECT-SPEED-PERCENT:225\r\n", calendar, StringComparison.Ordinal);
        Assert.Contains("X-HUE-PRIORITY:64\r\n", calendar, StringComparison.Ordinal);
        var startText = calendar.Split("\r\n", StringSplitOptions.None)
            .Single(line => line.StartsWith("DTSTART:", StringComparison.Ordinal))
            .Substring("DTSTART:".Length);
        var endText = calendar.Split("\r\n", StringSplitOptions.None)
            .Single(line => line.StartsWith("DTEND:", StringComparison.Ordinal))
            .Substring("DTEND:".Length);
        var start = DateTime.ParseExact(
            startText,
            "yyyyMMdd'T'HHmmss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        var end = DateTime.ParseExact(
            endText,
            "yyyyMMdd'T'HHmmss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        Assert.Equal(TimeSpan.FromSeconds(4), end - start);
        Assert.Contains("X-HUE-TIMEZONE:", calendar, StringComparison.Ordinal);
        Assert.Contains("X-HUE-RECURRENCE-INTERVAL:1\r\n", calendar, StringComparison.Ordinal);
        Assert.Contains("X-HUE-TRANSITION-SECONDS:2\r\n", calendar, StringComparison.Ordinal);
        Assert.Contains("X-HUE-TRANSITION-OUT-SECONDS:2\r\n", calendar, StringComparison.Ordinal);
        Assert.Contains("TRANSP:TRANSPARENT\r\n", calendar, StringComparison.Ordinal);
        Assert.Equal(1, calendar.Split("BEGIN:VEVENT", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("calendar-app-secret", calendar, StringComparison.Ordinal);
        Assert.DoesNotContain("calendar-client-secret", calendar, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSessionHistory_WithoutHostedSyncServiceReturnsBoundedEmptyHistory()
    {
        var controller = CreateController();

        var action = controller.GetSessionHistory(999, "Error");

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var history = Assert.IsType<HueSessionHistoryResult>(response.Value);
        Assert.False(history.ServiceAvailable);
        Assert.Equal(HueSyncService.MaxSessionHistoryCount, history.Limit);
        Assert.Equal("Error", history.OutcomeFilter);
        Assert.Empty(history.Sessions);
        Assert.True(history.GeneratedAtUtc > DateTime.UtcNow.AddMinutes(-1));

        var exportAction = controller.ExportSessionHistory(999, "Error");
        var exportResponse = Assert.IsType<OkObjectResult>(exportAction.Result);
        var export = Assert.IsType<HueSessionHistoryResult>(exportResponse.Value);
        Assert.Equal(history.Limit, export.Limit);
        Assert.Equal(history.OutcomeFilter, export.OutcomeFilter);
        Assert.Empty(export.Sessions);

        var clearAction = controller.ClearSessionHistory();
        var clearResponse = Assert.IsType<OkObjectResult>(clearAction.Result);
        var clear = Assert.IsType<HueSessionHistoryClearResult>(clearResponse.Value);
        Assert.False(clear.ServiceAvailable);
        Assert.Equal(0, clear.ClearedCount);
    }

    [Fact]
    public async Task Diagnostics_ReturnsSanitizedPrerequisiteAndLifecycleState()
    {
        InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "app-secret",
            HueClientKey = "client-secret",
            EntertainmentAreaId = "area-1"
        });
        var probe = new Mock<IHueEnvironmentProbe>();
        probe
            .Setup(environment => environment.CheckAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HueEnvironmentProbeResult
            {
                Ffmpeg = new HueToolStatus
                {
                    Available = true,
                    ExecutablePath = "/usr/bin/ffmpeg",
                    Version = "ffmpeg version 7.0"
                },
                OpenSsl = new HueToolStatus
                {
                    Available = true,
                    ExecutablePath = "/usr/bin/openssl",
                    Version = "OpenSSL 3.0"
                }
            });
        var gate = new HueBridgeLifecycleGate();
        using var diagnosticLease = gate.TryEnterDiagnostic();
        var controller = CreateController(null, gate, probe.Object);
        using var cancellationSource = new CancellationTokenSource();

        var action = await controller.GetDiagnostics(cancellationSource.Token);

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var diagnostics = Assert.IsType<HueDiagnosticsResult>(response.Value);
        Assert.True(diagnostics.ConfigurationValid);
        Assert.True(diagnostics.DefaultBridgeConfigured);
        Assert.True(diagnostics.Ffmpeg.Available);
        Assert.True(diagnostics.OpenSsl.Available);
        Assert.True(diagnostics.DiagnosticLifecycleActive);
        Assert.Equal("Diagnostic", diagnostics.BridgeLifecycleState);
        Assert.False(diagnostics.CanRunDiagnostics);
        Assert.False(diagnostics.CanStartPlayback);
        Assert.DoesNotContain("app-secret", System.Text.Json.JsonSerializer.Serialize(diagnostics));
        Assert.DoesNotContain("client-secret", System.Text.Json.JsonSerializer.Serialize(diagnostics));
        probe.Verify(
            environment => environment.CheckAsync(It.Is<CancellationToken>(token => token.CanBeCanceled)),
            Times.Once);
    }

    [Fact]
    public void CancelDiagnostics_RequestsCancellationForActiveChecks()
    {
        var cancellationGate = new HueDiagnosticsCancellationGate();
        using var operation = cancellationGate.Begin(CancellationToken.None);
        var controller = CreateController(diagnosticsCancellationGate: cancellationGate);

        var action = controller.CancelDiagnostics();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueDiagnosticsCancellationResult>(response.Value);
        Assert.True(result.Canceled);
        Assert.Equal(1, result.CanceledCount);
        Assert.True(operation.Token.IsCancellationRequested);
        Assert.Contains("Cancellation requested", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TargetDiagnostics_ValidatesDefaultInheritedAndCustomTargetsWithoutSecrets()
    {
        InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "global-app-secret",
            HueClientKey = "global-client-secret",
            EntertainmentAreaId = "area-global",
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-inherited",
                    UserName = "Inherited Viewer",
                    SyncEnabled = true
                },
                new()
                {
                    UserId = "user-custom",
                    UserName = "Custom Viewer",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "custom-app-secret",
                    HueClientKey = "custom-client-secret",
                    EntertainmentAreaId = "area-custom",
                    EntertainmentAreaName = "Custom Room"
                },
                new()
                {
                    UserId = "user-disabled",
                    UserName = "Disabled Viewer",
                    SyncEnabled = false
                }
            }
        });

        var requestedPaths = new List<string>();
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) =>
                requestedPaths.Add(request.RequestUri!.AbsolutePath))
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                var body = path.Contains("entertainment_configuration/", StringComparison.Ordinal)
                    ? "{\"data\":[{\"channels\":[{\"channel_id\":1,\"members\":[{\"service\":{\"rid\":\"light-1\"}}]}]}]}"
                    : "{\"data\":[{\"id\":\"area-global\",\"metadata\":{\"name\":\"Global Room\"}},{\"id\":\"area-custom\",\"metadata\":{\"name\":\"Custom Room\"}}]}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            });

        var action = await CreateController().GetTargetDiagnostics();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var diagnostics = Assert.IsType<HueTargetDiagnosticsResult>(response.Value);
        Assert.True(diagnostics.HasConfiguredTargets);
        Assert.True(diagnostics.AllTargetsReady);
        Assert.Equal(3, diagnostics.TargetCount);
        Assert.Equal(3, diagnostics.ReadyTargetCount);
        Assert.All(diagnostics.Targets, target => Assert.True(target.Ready));
        Assert.All(diagnostics.Targets, target => Assert.True(target.ChannelProfileValid));
        Assert.All(diagnostics.Targets, target => Assert.Equal(1, target.SelectedChannelCount));
        var inherited = Assert.Single(diagnostics.Targets.Where(target => target.UserId == "user-inherited"));
        Assert.True(inherited.InheritsDefaultBridge);
        Assert.Equal("Global Room", inherited.EntertainmentAreaName);
        var custom = Assert.Single(diagnostics.Targets.Where(target => target.UserId == "user-custom"));
        Assert.False(custom.InheritsDefaultBridge);
        Assert.Equal("Custom Room", custom.EntertainmentAreaName);
        Assert.Equal(2, requestedPaths.Count(path => path.EndsWith("/entertainment_configuration", StringComparison.Ordinal)));
        Assert.Equal(2, requestedPaths.Count(path => path.Contains("/entertainment_configuration/", StringComparison.Ordinal)));

        var serialized = System.Text.Json.JsonSerializer.Serialize(diagnostics);
        Assert.DoesNotContain("global-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("global-client-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("custom-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("custom-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TargetDiagnostics_ReportsStaleSavedChannelProfileIdsBeforePlayback()
    {
        InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "global-app-secret",
            HueClientKey = "global-client-secret",
            EntertainmentAreaId = "area-1",
            ChannelIds = "9"
        });
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                var body = path.Contains("entertainment_configuration/", StringComparison.Ordinal)
                    ? "{\"data\":[{\"channels\":[{\"channel_id\":1}]}]}"
                    : "{\"data\":[{\"id\":\"area-1\",\"metadata\":{\"name\":\"Living Room\"}}]}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            });

        var action = await CreateController().GetTargetDiagnostics();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var diagnostics = Assert.IsType<HueTargetDiagnosticsResult>(response.Value);
        var target = Assert.Single(diagnostics.Targets);
        Assert.False(diagnostics.AllTargetsReady);
        Assert.False(target.Ready);
        Assert.True(target.BridgeReachable);
        Assert.True(target.AreaFound);
        Assert.Equal(1, target.ChannelCount);
        Assert.Equal(1, target.SelectedChannelCount);
        Assert.False(target.ChannelProfileValid);
        Assert.Equal("9", target.MissingChannelIds);
        Assert.Contains("not present", target.Status, StringComparison.OrdinalIgnoreCase);
        var serialized = JsonSerializer.Serialize(diagnostics);
        Assert.DoesNotContain("global-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("global-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TargetDiagnostics_ReportsMissingCredentialsWithoutContactingBridge()
    {
        InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = true,
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-missing",
                    UserName = "Missing Keys",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    EntertainmentAreaId = "area-1"
                }
            }
        });
        var controller = CreateController();

        var action = await controller.GetTargetDiagnostics();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var diagnostics = Assert.IsType<HueTargetDiagnosticsResult>(response.Value);
        var target = Assert.Single(diagnostics.Targets);
        Assert.False(target.Ready);
        Assert.False(target.ConfigurationValid);
        Assert.False(target.BridgeReachable);
        Assert.Contains("App Key is missing", target.Status, StringComparison.Ordinal);
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StopSync_WithoutHostedServiceReturnsServiceUnavailable()
    {
        var controller = CreateController();

        var action = await controller.StopSync();

        var response = Assert.IsType<ObjectResult>(action);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public void GetUserMappings_RedactsStoredCredentialsAndReportsPresence()
    {
        InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-1",
                    UserName = "Viewer",
                    HueBridgeIp = "192.168.1.100",
                    HueAppKey = "mapping-app-secret",
                    HueClientKey = "mapping-client-secret",
                    EntertainmentAreaId = "area-1",
                    EntertainmentAreaName = "Living Room",
                    UseCinemaModeOverride = false,
                    BrightnessDimLevelOverride = 10,
                    PauseBehaviorOverride = PluginConfiguration.PauseBehaviorRestoreLightState,
                    RestoreLightStateOverride = true,
                    BrightnessBoostOverride = 150,
                    RedGainOverride = 120,
                    GreenGainOverride = 90,
                    BlueGainOverride = 110,
                    ColorSaturationOverride = 0,
                    HueShiftDegreesOverride = -45,
                    OutputBrightnessPercentOverride = 75,
                    BlackoutThresholdOverride = 30,
                    ColorChangeThresholdOverride = 5,
                    UseGpuOverride = false,
                    CustomFfmpegFlagsOverride = "-hwaccel vaapi",
                    FfmpegStallTimeoutSecondsOverride = 20,
                    NetworkRetryAttemptsOverride = 1,
                    ChannelIdsOverride = "2, 9",
                    TargetFpsOverride = 30,
                    FrameResolutionOverride = PluginConfiguration.FrameResolutionHigh,
                    VideoScalingModeOverride = PluginConfiguration.VideoScalingModeFit,
                    VideoDeinterlaceModeOverride = PluginConfiguration.VideoDeinterlaceModeAuto,
                    SamplingBreadthPercentOverride = 25,
                    SamplingModeOverride = PluginConfiguration.SamplingModeCenterWeighted,
                    ColorSmoothingPercentOverride = 40
                }
            }
        });

        var action = CreateController().GetUserMappings();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var mapping = Assert.Single(Assert.IsAssignableFrom<IEnumerable<UserBridgeMappingSummary>>(response.Value));
        Assert.Equal("user-1", mapping.UserId);
        Assert.False(mapping.InheritsDefaultBridge);
        Assert.True(mapping.HasAppKey);
        Assert.True(mapping.HasClientKey);
        Assert.Equal((bool?)false, mapping.UseCinemaModeOverride);
        Assert.Equal((int?)10, mapping.BrightnessDimLevelOverride);
        Assert.Equal(PluginConfiguration.PauseBehaviorRestoreLightState, mapping.PauseBehaviorOverride);
        Assert.Equal((bool?)true, mapping.RestoreLightStateOverride);
        Assert.Equal((int?)150, mapping.BrightnessBoostOverride);
        Assert.Equal((int?)120, mapping.RedGainOverride);
        Assert.Equal((int?)90, mapping.GreenGainOverride);
        Assert.Equal((int?)110, mapping.BlueGainOverride);
        Assert.Equal((int?)0, mapping.ColorSaturationOverride);
        Assert.Equal((int?)-45, mapping.HueShiftDegreesOverride);
        Assert.Equal((int?)75, mapping.OutputBrightnessPercentOverride);
        Assert.Equal((int?)30, mapping.BlackoutThresholdOverride);
        Assert.Equal((int?)5, mapping.ColorChangeThresholdOverride);
        Assert.Equal((bool?)false, mapping.UseGpuOverride);
        Assert.Equal("-hwaccel vaapi", mapping.CustomFfmpegFlagsOverride);
        Assert.Equal((int?)20, mapping.FfmpegStallTimeoutSecondsOverride);
        Assert.Equal((int?)1, mapping.NetworkRetryAttemptsOverride);
        Assert.Equal("2, 9", mapping.ChannelIdsOverride);
        Assert.Equal((int?)30, mapping.TargetFpsOverride);
        Assert.Equal(PluginConfiguration.FrameResolutionHigh, mapping.FrameResolutionOverride);
        Assert.Equal(PluginConfiguration.VideoScalingModeFit, mapping.VideoScalingModeOverride);
        Assert.Equal(PluginConfiguration.VideoDeinterlaceModeAuto, mapping.VideoDeinterlaceModeOverride);
        Assert.Equal((int?)25, mapping.SamplingBreadthPercentOverride);
        Assert.Equal(PluginConfiguration.SamplingModeCenterWeighted, mapping.SamplingModeOverride);
        Assert.Equal((int?)40, mapping.ColorSmoothingPercentOverride);
        var serialized = System.Text.Json.JsonSerializer.Serialize(mapping);
        Assert.DoesNotContain("mapping-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("mapping-client-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("HueAppKey", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HueClientKey", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetUserMappings_ReportsDefaultBridgeInheritanceWithoutCredentials()
    {
        InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-default",
                    UserName = "Viewer",
                    SyncEnabled = true,
                    BrightnessBoostOverride = 125
                }
            }
        });

        var action = CreateController().GetUserMappings();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var mapping = Assert.Single(Assert.IsAssignableFrom<IEnumerable<UserBridgeMappingSummary>>(response.Value));
        Assert.True(mapping.InheritsDefaultBridge);
        Assert.False(mapping.HasAppKey);
        Assert.False(mapping.HasClientKey);
        Assert.Equal((int?)125, mapping.BrightnessBoostOverride);
        var serialized = System.Text.Json.JsonSerializer.Serialize(mapping);
        Assert.DoesNotContain("HueAppKey", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HueClientKey", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetConfiguration_ExcludesPerUserMappings()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "default-app-key",
            HueClientKey = "default-client-key",
            ChannelIds = "2, 9",
            FrameResolution = PluginConfiguration.FrameResolutionHigh,
            VideoScalingMode = PluginConfiguration.VideoScalingModeFit,
            VideoDeinterlaceMode = PluginConfiguration.VideoDeinterlaceModeAuto,
            SamplingBreadthPercent = 25,
            SamplingMode = PluginConfiguration.SamplingModeCenterWeighted,
            ColorSmoothingPercent = 65,
            HueShiftDegrees = 45,
            OutputBrightnessPercent = 75,
            RedGain = 120,
            GreenGain = 90,
            BlueGain = 110,
            NetworkRetryAttempts = 6,
            PauseBehavior = PluginConfiguration.PauseBehaviorRestoreLightState,
            PersistSessionHistory = true,
            SceneAutomationEnabled = false,
            SceneAutomationCatchUpMinutes = 37,
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-1",
                    HueAppKey = "mapping-app-secret",
                    HueClientKey = "mapping-client-secret"
                }
            }
        });

        var action = CreateController().GetConfiguration();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var settings = Assert.IsType<HuePluginConfigurationSettings>(response.Value);
        Assert.Empty(settings.HueAppKey);
        Assert.Empty(settings.HueClientKey);
        Assert.True(settings.HasAppKey);
        Assert.True(settings.HasClientKey);
        Assert.Equal("2, 9", settings.ChannelIds);
        Assert.Equal(PluginConfiguration.FrameResolutionHigh, settings.FrameResolution);
        Assert.Equal(PluginConfiguration.VideoScalingModeFit, settings.VideoScalingMode);
        Assert.Equal(PluginConfiguration.VideoDeinterlaceModeAuto, settings.VideoDeinterlaceMode);
        Assert.Equal(25, settings.SamplingBreadthPercent);
        Assert.Equal(PluginConfiguration.SamplingModeCenterWeighted, settings.SamplingMode);
        Assert.Equal(65, settings.ColorSmoothingPercent);
        Assert.Equal(45, settings.HueShiftDegrees);
        Assert.Equal(75, settings.OutputBrightnessPercent);
        Assert.Equal(120, settings.RedGain);
        Assert.Equal(90, settings.GreenGain);
        Assert.Equal(110, settings.BlueGain);
        Assert.Equal(6, settings.NetworkRetryAttempts);
        Assert.Equal(PluginConfiguration.PauseBehaviorRestoreLightState, settings.PauseBehavior);
        Assert.True(settings.PersistSessionHistory);
        Assert.Equal(false, settings.SceneAutomationEnabled);
        Assert.Equal(37, settings.SceneAutomationCatchUpMinutes);
        var serialized = System.Text.Json.JsonSerializer.Serialize(settings);
        Assert.DoesNotContain("default-app-key", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("default-client-key", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("mapping-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("UserMappings", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExportConfiguration_IncludesProfilesAndScenesWithoutSecrets()
    {
        InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "default-app-secret",
            HueClientKey = "default-client-secret",
            EntertainmentAreaId = "area-1",
            SceneAutomationEnabled = false,
            SceneAutomationCatchUpMinutes = 22,
            PersistSessionHistory = true,
            PersistedSessionHistory = new List<HueSessionHistoryEntry>
            {
                new() { Item = "Private title", UserName = "Private viewer" }
            },
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-1",
                    UserName = "Viewer",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "mapping-app-secret",
                    HueClientKey = "mapping-client-secret",
                    EntertainmentAreaId = "area-2",
                    BrightnessBoostOverride = 135
                }
            },
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Accent", Effect = PluginConfiguration.ColorPresetEffectPulse, EffectSpeedPercent = 225, Red = 12, Green = 34, Blue = 56, DurationSeconds = 6, TransitionSeconds = 3, TransitionOutSeconds = 2 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "cue-1",
                    Name = "Morning cue",
                    PresetName = "Accent",
                    Priority = 55,
                    TimeOfDay = "08:15",
                    StartDate = "2026-08-01",
                    EndDate = "2026-12-31",
                    ExcludedDates = new List<string> { "2026-12-24" },
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceMonthly,
                    DayOfMonth = 31,
                    DurationSeconds = 11,
                    MaxRuns = 4,
                    RunCount = 2,
                    SkipNextOccurrence = true,
                    DaysOfWeekMask = 127
                }
            }
        });

        var action = CreateController().ExportConfiguration();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var document = Assert.IsType<HueConfigurationExportDocument>(response.Value);
        Assert.Equal(HueConfigurationExportDocument.CurrentSchemaVersion, document.SchemaVersion);
        Assert.False(document.CredentialsIncluded);
        Assert.Empty(document.Configuration.HueAppKey);
        Assert.Empty(document.Configuration.HueClientKey);
        Assert.True(document.Configuration.HasAppKey);
        Assert.True(document.Configuration.HasClientKey);
        var mapping = Assert.Single(document.UserMappings);
        Assert.True(mapping.HasAppKey);
        Assert.True(mapping.HasClientKey);
        Assert.Equal(135, mapping.BrightnessBoostOverride);
        Assert.Single(document.ColorPresets);
        Assert.Single(document.SceneSchedules);
        Assert.Equal("Morning cue", document.SceneSchedules[0].Name);
        Assert.Equal(55, document.SceneSchedules[0].Priority);
        Assert.Equal("2026-08-01", document.SceneSchedules[0].StartDate);
        Assert.Equal("2026-12-31", document.SceneSchedules[0].EndDate);
        Assert.Equal(new[] { "2026-12-24" }, document.SceneSchedules[0].ExcludedDates);
        Assert.Equal(PluginConfiguration.SceneScheduleRecurrenceMonthly, document.SceneSchedules[0].Recurrence);
        Assert.Equal(31, document.SceneSchedules[0].DayOfMonth);
        Assert.Equal(11, document.SceneSchedules[0].DurationSeconds);
        Assert.Equal(4, document.SceneSchedules[0].MaxRuns);
        Assert.Equal(2, document.SceneSchedules[0].RunCount);
        Assert.True(document.SceneSchedules[0].SkipNextOccurrence);
        Assert.Equal(3, document.ColorPresets[0].TransitionSeconds);
        Assert.Equal(2, document.ColorPresets[0].TransitionOutSeconds);
        Assert.Equal(PluginConfiguration.ColorPresetEffectPulse, document.ColorPresets[0].Effect);
        Assert.Equal(225, document.ColorPresets[0].EffectSpeedPercent);
        Assert.Equal(PluginConfiguration.ColorPresetEffectPulse, document.SceneSchedules[0].Effect);
        Assert.Equal(225, document.SceneSchedules[0].EffectSpeedPercent);
        Assert.Equal(3, document.SceneSchedules[0].TransitionSeconds);
        Assert.Equal(2, document.SceneSchedules[0].TransitionOutSeconds);
        Assert.True(document.Configuration.PersistSessionHistory);
        Assert.Equal(false, document.Configuration.SceneAutomationEnabled);
        Assert.Equal(22, document.Configuration.SceneAutomationCatchUpMinutes);

        var serialized = System.Text.Json.JsonSerializer.Serialize(document);
        Assert.DoesNotContain("default-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("default-client-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("mapping-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("mapping-client-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Private title", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Private viewer", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("PersistedSessionHistory", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-app", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationExportAndImport_PreservesScenePlaylistsWithoutCredentials()
    {
        var sourceConfiguration = new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "source-app-secret",
            HueClientKey = "source-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Sunrise", Red = 240, Green = 120, Blue = 40, DurationSeconds = 3 },
                new() { Name = "Midnight", Red = 20, Green = 30, Blue = 90, DurationSeconds = 5 }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "playlist-portable",
                    Name = "Portable sequence",
                    PresetNames = new List<string> { "Sunrise", "Midnight" },
                    TargetAllEnabledMappings = true
                }
            }
        };
        var exported = HueConfigurationExportDocument.From(sourceConfiguration);
        var exportedPlaylist = Assert.Single(exported.ScenePlaylists);
        Assert.Equal("playlist-portable", exportedPlaylist.Id);
        Assert.Equal(new[] { "Sunrise", "Midnight" }, exportedPlaylist.PresetNames);
        Assert.True(exportedPlaylist.TargetAllEnabledMappings);
        Assert.Equal(8, exportedPlaylist.TotalDurationSeconds);
        var serialized = JsonSerializer.Serialize(exported);
        Assert.DoesNotContain("source-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("source-client-secret", serialized, StringComparison.Ordinal);

        var destination = InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.110",
            HueAppKey = "destination-app-secret",
            HueClientKey = "destination-client-secret",
            EntertainmentAreaId = "destination-area"
        });
        var action = CreateController().ImportConfiguration(new HueConfigurationImportRequest
        {
            SchemaVersion = exported.SchemaVersion,
            Configuration = exported.Configuration,
            ColorPresets = new List<HueColorPresetRequest>
            {
                new() { Name = "Sunrise", Red = 240, Green = 120, Blue = 40, DurationSeconds = 3 },
                new() { Name = "Midnight", Red = 20, Green = 30, Blue = 90, DurationSeconds = 5 }
            },
            ScenePlaylists = new List<HueScenePlaylistRequest>
            {
                new()
                {
                    Id = exportedPlaylist.Id,
                    Name = exportedPlaylist.Name,
                    PresetNames = exportedPlaylist.PresetNames.ToList(),
                    TargetAllEnabledMappings = exportedPlaylist.TargetAllEnabledMappings
                }
            }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueConfigurationImportResult>(response.Value);
        Assert.Equal(1, result.ScenePlaylistsImported);
        Assert.Equal(1, result.TotalScenePlaylists);
        Assert.Equal("destination-app-secret", destination.HueAppKey);
        Assert.Equal("destination-client-secret", destination.HueClientKey);
        var imported = Assert.Single(destination.ScenePlaylists);
        Assert.Equal("playlist-portable", imported.Id);
        Assert.Equal(new[] { "Sunrise", "Midnight" }, imported.PresetNames);
        Assert.True(imported.TargetAllEnabledMappings);
        var serializedResult = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("destination-app-secret", serializedResult, StringComparison.Ordinal);
        Assert.DoesNotContain("destination-client-secret", serializedResult, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationExportAndImport_PreservesPlaylistBackedCueAndZeroDurationOverride()
    {
        var source = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "First", DurationSeconds = 2 },
                new() { Name = "Second", DurationSeconds = 4 }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new() { Id = "portable-playlist", Name = "Portable playlist", PresetNames = new List<string> { "First", "Second" } }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "portable-playlist-cue",
                    Name = "Portable playlist cue",
                    PlaylistName = "Portable playlist",
                    TimeOfDay = "08:15",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0
                }
            }
        };
        var exported = HueConfigurationExportDocument.From(source);
        var exportedCue = Assert.Single(exported.SceneSchedules);
        Assert.Equal("Portable playlist", exportedCue.PlaylistName);
        Assert.Equal(PluginConfiguration.SceneScheduleEffectPlaylist, exportedCue.Effect);
        Assert.Equal(6, exportedCue.PlaylistTotalDurationSeconds);
        Assert.Equal(0, exportedCue.DurationSeconds);

        var destination = InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.111",
            HueAppKey = "existing-app-secret",
            HueClientKey = "existing-client-secret",
            EntertainmentAreaId = "area-destination"
        });
        var action = CreateController().ImportConfiguration(new HueConfigurationImportRequest
        {
            SchemaVersion = exported.SchemaVersion,
            Configuration = exported.Configuration,
            ReplaceColorPresets = true,
            ReplaceScenePlaylists = true,
            ReplaceSceneSchedules = true,
            ColorPresets = source.ColorPresets.Select(preset => new HueColorPresetRequest
            {
                Name = preset.Name,
                Red = preset.Red,
                Green = preset.Green,
                Blue = preset.Blue,
                BrightnessPercent = preset.BrightnessPercent,
                DurationSeconds = preset.DurationSeconds
            }).ToList(),
            ScenePlaylists = source.ScenePlaylists.Select(playlist => new HueScenePlaylistRequest
            {
                Id = playlist.Id,
                Name = playlist.Name,
                PresetNames = playlist.PresetNames.ToList()
            }).ToList(),
            SceneSchedules = new List<HueSceneScheduleRequest>
            {
                new()
                {
                    Id = exportedCue.Id,
                    Name = exportedCue.Name,
                    PlaylistName = exportedCue.PlaylistName,
                    TimeOfDay = exportedCue.TimeOfDay,
                    TimeZoneId = exportedCue.TimeZoneId,
                    Recurrence = exportedCue.Recurrence,
                    DaysOfWeekMask = exportedCue.DaysOfWeekMask,
                    DurationSeconds = exportedCue.DurationSeconds
                }
            }
        });

        Assert.IsType<OkObjectResult>(action.Result);
        var importedCue = Assert.Single(destination.SceneSchedules);
        Assert.Equal("Portable playlist", importedCue.PlaylistName);
        Assert.Equal(string.Empty, importedCue.PresetName);
        Assert.Equal(0, importedCue.DurationSeconds);
    }

    [Fact]
    public void ConfigurationExportAndImport_PreservesOneTimeCueDateAndOrdinalWeekday()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Accent", Red = 20, Green = 30, Blue = 40 } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "one-time-cue",
                    Name = "Holiday cue",
                    PresetName = "Accent",
                    TimeOfDay = "18:45",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    RunDate = "2026-12-24",
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceMonthlyWeekday,
                    WeekOfMonth = PluginConfiguration.SceneScheduleLastWeekOfMonth,
                    DayOfWeek = (int)DayOfWeek.Friday,
                    DurationSeconds = 10,
                    DaysOfWeekMask = 0
                }
            }
        });

        var exported = HueConfigurationExportDocument.From(configuration);
        var exportedCue = Assert.Single(exported.SceneSchedules);
        Assert.Equal("2026-12-24", exportedCue.RunDate);
        Assert.Equal(PluginConfiguration.SceneScheduleRecurrenceMonthlyWeekday, exportedCue.Recurrence);
        Assert.Equal(PluginConfiguration.SceneScheduleLastWeekOfMonth, exportedCue.WeekOfMonth);
        Assert.Equal((int)DayOfWeek.Friday, exportedCue.DayOfWeek);
        Assert.Equal(0, exportedCue.DaysOfWeekMask);

        var action = CreateController().ImportConfiguration(new HueConfigurationImportRequest
        {
            SchemaVersion = exported.SchemaVersion,
            Configuration = exported.Configuration,
            ColorPresets = new List<HueColorPresetRequest>
            {
                new() { Name = "Accent", Red = 20, Green = 30, Blue = 40, EffectSpeedPercent = 275, TransitionSeconds = 2, TransitionOutSeconds = 1 }
            },
            SceneSchedules = new List<HueSceneScheduleRequest>
            {
                new()
                {
                    Id = exportedCue.Id,
                    Name = exportedCue.Name,
                    PresetName = exportedCue.PresetName,
                    Priority = exportedCue.Priority,
                    TimeOfDay = exportedCue.TimeOfDay,
                    TimeZoneId = exportedCue.TimeZoneId,
                    RunDate = exportedCue.RunDate,
                    Recurrence = exportedCue.Recurrence,
                    WeekOfMonth = exportedCue.WeekOfMonth,
                    DayOfWeek = exportedCue.DayOfWeek,
                    DurationSeconds = exportedCue.DurationSeconds,
                    MaxRuns = exportedCue.MaxRuns,
                    RunCount = exportedCue.RunCount,
                    DaysOfWeekMask = exportedCue.DaysOfWeekMask,
                    Enabled = exportedCue.Enabled
                }
            }
        });

        Assert.IsType<OkObjectResult>(action.Result);
        var importedCue = Assert.Single(configuration.SceneSchedules);
        Assert.Equal("2026-12-24", importedCue.RunDate);
        Assert.Equal(PluginConfiguration.SceneScheduleRecurrenceMonthlyWeekday, importedCue.Recurrence);
        Assert.Equal(PluginConfiguration.SceneScheduleLastWeekOfMonth, importedCue.WeekOfMonth);
        Assert.Equal((int)DayOfWeek.Friday, importedCue.DayOfWeek);
        Assert.Equal(exportedCue.DurationSeconds, importedCue.DurationSeconds);
        Assert.Equal(exportedCue.Priority, importedCue.Priority);
        Assert.Equal(exportedCue.MaxRuns, importedCue.MaxRuns);
        Assert.Equal(exportedCue.RunCount, importedCue.RunCount);
        Assert.Equal(0, importedCue.DaysOfWeekMask);
        Assert.Equal(2, Assert.Single(configuration.ColorPresets).TransitionSeconds);
        Assert.Equal(1, Assert.Single(configuration.ColorPresets).TransitionOutSeconds);
        Assert.Equal(275, Assert.Single(configuration.ColorPresets).EffectSpeedPercent);
    }

    [Fact]
    public void ConfigurationExportAndImport_PreservesYearlyRecurrenceMonth()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Annual" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "yearly-cue",
                    Name = "New Year's Eve",
                    PresetName = "Annual",
                    TimeOfDay = "23:30",
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceYearly,
                    RecurrenceInterval = 2,
                    MonthOfYear = 12,
                    DayOfMonth = 31,
                    StartDate = "2026-01-01",
                    DaysOfWeekMask = 0
                }
            }
        });

        var exported = HueConfigurationExportDocument.From(configuration);
        var exportedCue = Assert.Single(exported.SceneSchedules);
        Assert.Equal(PluginConfiguration.SceneScheduleRecurrenceYearly, exportedCue.Recurrence);
        Assert.Equal(2, exportedCue.RecurrenceInterval);
        Assert.Equal(12, exportedCue.MonthOfYear);
        Assert.Equal(31, exportedCue.DayOfMonth);
        Assert.Equal("2026-01-01", exportedCue.StartDate);

        var action = CreateController().ImportConfiguration(new HueConfigurationImportRequest
        {
            SchemaVersion = exported.SchemaVersion,
            Configuration = exported.Configuration,
            ColorPresets = new List<HueColorPresetRequest>
            {
                new() { Name = "Annual", DurationSeconds = 5 }
            },
            SceneSchedules = new List<HueSceneScheduleRequest>
            {
                new()
                {
                    Id = exportedCue.Id,
                    Name = exportedCue.Name,
                    PresetName = exportedCue.PresetName,
                    TimeOfDay = exportedCue.TimeOfDay,
                    Recurrence = exportedCue.Recurrence,
                    RecurrenceInterval = exportedCue.RecurrenceInterval,
                    MonthOfYear = exportedCue.MonthOfYear,
                    DayOfMonth = exportedCue.DayOfMonth,
                    StartDate = exportedCue.StartDate,
                    DaysOfWeekMask = exportedCue.DaysOfWeekMask,
                    Enabled = exportedCue.Enabled
                }
            }
        });

        Assert.IsType<OkObjectResult>(action.Result);
        var importedCue = Assert.Single(configuration.SceneSchedules);
        Assert.Equal(PluginConfiguration.SceneScheduleRecurrenceYearly, importedCue.Recurrence);
        Assert.Equal(2, importedCue.RecurrenceInterval);
        Assert.Equal(12, importedCue.MonthOfYear);
        Assert.Equal(31, importedCue.DayOfMonth);
    }

    [Fact]
    public void ImportConfiguration_PreservesMatchingSecretsAndReplacesProfilesAtomically()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "default-app-secret",
            HueClientKey = "default-client-secret",
            EntertainmentAreaId = "area-1",
            SceneAutomationEnabled = false,
            SceneAutomationCatchUpMinutes = 22,
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-1",
                    UserName = "Viewer",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "mapping-app-secret",
                    HueClientKey = "mapping-client-secret",
                    EntertainmentAreaId = "area-2",
                    BrightnessBoostOverride = 100
                }
            },
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Old Scene", Red = 1 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "old-cue", Name = "Old cue", PresetName = "Old Scene" }
            }
        });
        var exported = HueConfigurationExportDocument.From(configuration);
        var controller = CreateController();

        var action = controller.ImportConfiguration(new HueConfigurationImportRequest
        {
            SchemaVersion = exported.SchemaVersion,
            Configuration = exported.Configuration,
            UserMappings = exported.UserMappings
                .Select(summary => new UserBridgeMappingImport
                {
                    UserId = summary.UserId,
                    UserName = summary.UserName,
                    SyncEnabled = summary.SyncEnabled,
                    HueBridgeIp = summary.HueBridgeIp,
                    EntertainmentAreaId = summary.EntertainmentAreaId,
                    BrightnessBoostOverride = 150
                })
                .ToList(),
            ColorPresets = new List<HueColorPresetRequest>
            {
                new() { Name = "New Scene", Red = 20, Green = 30, Blue = 40 }
            },
            SceneSchedules = new List<HueSceneScheduleRequest>
            {
                new()
                {
                    Id = "new-cue",
                    Name = "New cue",
                    PresetName = "New Scene",
                    TimeOfDay = "22:10",
                    StartDate = "2026-08-01",
                    EndDate = "2026-12-31",
                    ExcludedDates = new List<string> { "2026-12-24" },
                    DaysOfWeekMask = 127
                }
            }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueConfigurationImportResult>(response.Value);
        Assert.True(result.GlobalAppKeyPreserved);
        Assert.True(result.GlobalClientKeyPreserved);
        Assert.Equal(1, result.MappingCredentialPairsPreserved);
        Assert.Equal(1, result.MappingsImported);
        Assert.Equal(1, result.ColorPresetsImported);
        Assert.Equal(1, result.SceneSchedulesImported);
        Assert.Equal("default-app-secret", configuration.HueAppKey);
        Assert.Equal("default-client-secret", configuration.HueClientKey);
        Assert.False(configuration.SceneAutomationEnabled);
        Assert.Equal(22, configuration.SceneAutomationCatchUpMinutes);
        var mapping = Assert.Single(configuration.UserMappings);
        Assert.Equal("mapping-app-secret", mapping.HueAppKey);
        Assert.Equal("mapping-client-secret", mapping.HueClientKey);
        Assert.Equal(150, mapping.BrightnessBoostOverride);
        Assert.Equal("New Scene", Assert.Single(configuration.ColorPresets).Name);
        var importedSchedule = Assert.Single(configuration.SceneSchedules);
        Assert.Equal("New cue", importedSchedule.Name);
        Assert.Equal("2026-08-01", importedSchedule.StartDate);
        Assert.Equal("2026-12-31", importedSchedule.EndDate);
        Assert.Equal(new[] { "2026-12-24" }, importedSchedule.ExcludedDates);
    }

    [Fact]
    public void ImportConfiguration_AcceptsExplicitReplacementCredentialsForMigration()
    {
        var configuration = InstallConfiguration(new PluginConfiguration());

        var action = CreateController().ImportConfiguration(new HueConfigurationImportRequest
        {
            Configuration = new HuePluginConfigurationSettings
            {
                SyncEnabled = true,
                HueBridgeIp = "192.168.1.100",
                HueAppKey = "migrated-global-app-secret",
                HueClientKey = "migrated-global-client-secret",
                EntertainmentAreaId = "global-area"
            },
            UserMappings = new List<UserBridgeMappingImport>
            {
                new()
                {
                    UserId = "migrated-user",
                    UserName = "Migrated Viewer",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "migrated-mapping-app-secret",
                    HueClientKey = "migrated-mapping-client-secret",
                    EntertainmentAreaId = "mapping-area"
                }
            }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueConfigurationImportResult>(response.Value);
        Assert.Equal(1, result.MappingsImported);
        Assert.Equal("migrated-global-app-secret", configuration.HueAppKey);
        Assert.Equal("migrated-global-client-secret", configuration.HueClientKey);
        var mapping = Assert.Single(configuration.UserMappings);
        Assert.Equal("migrated-mapping-app-secret", mapping.HueAppKey);
        Assert.Equal("migrated-mapping-client-secret", mapping.HueClientKey);

        var serializedResult = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("migrated-global-app-secret", serializedResult, StringComparison.Ordinal);
        Assert.DoesNotContain("migrated-global-client-secret", serializedResult, StringComparison.Ordinal);
        Assert.DoesNotContain("migrated-mapping-app-secret", serializedResult, StringComparison.Ordinal);
        Assert.DoesNotContain("migrated-mapping-client-secret", serializedResult, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportConfiguration_RejectsIncompleteNewTargetWithoutChangingConfiguration()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "default-app-secret",
            HueClientKey = "default-client-secret",
            EntertainmentAreaId = "area-1"
        });
        var controller = CreateController();

        var action = controller.ImportConfiguration(new HueConfigurationImportRequest
        {
            Configuration = HuePluginConfigurationSettings.From(configuration),
            UserMappings = new List<UserBridgeMappingImport>
            {
                new()
                {
                    UserId = "new-user",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    EntertainmentAreaId = "area-2"
                }
            }
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Empty(configuration.UserMappings);
        Assert.Equal("default-app-secret", configuration.HueAppKey);
        Assert.Equal("default-client-secret", configuration.HueClientKey);
    }

    [Fact]
    public void SaveConfiguration_UpdatesSettingsWithoutReplacingUserMappings()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-1",
                    HueAppKey = "mapping-app-secret",
                    HueClientKey = "mapping-client-secret"
                }
            }
        });

        var action = CreateController().SaveConfiguration(new HuePluginConfigurationSettings
        {
            SyncEnabled = false,
            ChannelIds = "4, 8",
            TargetFps = 30,
            FrameResolution = PluginConfiguration.FrameResolutionLow,
            VideoScalingMode = PluginConfiguration.VideoScalingModeCrop,
            VideoDeinterlaceMode = PluginConfiguration.VideoDeinterlaceModeOn,
            SamplingBreadthPercent = 25,
            SamplingMode = PluginConfiguration.SamplingModeCenterPixel,
            ColorSmoothingPercent = 40,
            HueShiftDegrees = -30,
            OutputBrightnessPercent = 60,
            RedGain = 115,
            GreenGain = 95,
            BlueGain = 105,
            NetworkRetryAttempts = 4,
            PauseBehavior = PluginConfiguration.PauseBehaviorRestoreLightState,
            PersistSessionHistory = true,
            PersistSceneScheduleHistory = true,
            SceneAutomationEnabled = false,
            SceneAutomationCatchUpMinutes = 18
        });

        Assert.IsType<OkObjectResult>(action.Result);
        Assert.Equal("4, 8", configuration.ChannelIds);
        Assert.Equal(30, configuration.TargetFps);
        Assert.Equal(PluginConfiguration.FrameResolutionLow, configuration.FrameResolution);
        Assert.Equal(PluginConfiguration.VideoScalingModeCrop, configuration.VideoScalingMode);
        Assert.Equal(PluginConfiguration.VideoDeinterlaceModeOn, configuration.VideoDeinterlaceMode);
        Assert.Equal(25, configuration.SamplingBreadthPercent);
        Assert.Equal(PluginConfiguration.SamplingModeCenterPixel, configuration.SamplingMode);
        Assert.Equal(40, configuration.ColorSmoothingPercent);
        Assert.Equal(-30, configuration.HueShiftDegrees);
        Assert.Equal(60, configuration.OutputBrightnessPercent);
        Assert.Equal(115, configuration.RedGain);
        Assert.Equal(95, configuration.GreenGain);
        Assert.Equal(105, configuration.BlueGain);
        Assert.Equal(4, configuration.NetworkRetryAttempts);
        Assert.Equal(PluginConfiguration.PauseBehaviorRestoreLightState, configuration.PauseBehavior);
        Assert.True(configuration.PersistSessionHistory);
        Assert.True(configuration.PersistSceneScheduleHistory);
        Assert.False(configuration.SceneAutomationEnabled);
        Assert.Equal(18, configuration.SceneAutomationCatchUpMinutes);
        var mapping = Assert.Single(configuration.UserMappings);
        Assert.Equal("mapping-app-secret", mapping.HueAppKey);
        Assert.Equal("mapping-client-secret", mapping.HueClientKey);
    }

    [Fact]
    public void SaveConfiguration_DisablingPersistentHistoryClearsStoredEntries()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            PersistSessionHistory = true,
            PersistedSessionHistory = new List<HueSessionHistoryEntry>
            {
                new() { Item = "Private title" }
            }
        });

        var action = CreateController().SaveConfiguration(new HuePluginConfigurationSettings
        {
            PersistSessionHistory = false
        });

        Assert.IsType<OkObjectResult>(action.Result);
        Assert.False(configuration.PersistSessionHistory);
        Assert.Empty(configuration.PersistedSessionHistory);
    }

    [Fact]
    public void SaveConfiguration_DisablingPersistentCueHistoryClearsStoredEntries()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            PersistSceneScheduleHistory = true,
            PersistedSceneScheduleHistory = new List<HueSceneScheduleHistoryEntry>
            {
                new() { ScheduleId = "cue-1", ScheduleName = "Private cue" }
            }
        });

        var action = CreateController().SaveConfiguration(new HuePluginConfigurationSettings
        {
            PersistSceneScheduleHistory = false
        });

        Assert.IsType<OkObjectResult>(action.Result);
        Assert.False(configuration.PersistSceneScheduleHistory);
        Assert.Empty(configuration.PersistedSceneScheduleHistory);
    }

    [Fact]
    public void SaveConfiguration_BlankGlobalCredentialsPreserveStoredKeysAndExplicitClearRemovesThem()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "stored-app-key",
            HueClientKey = "stored-client-key"
        });
        var controller = CreateController();

        var preserveAction = controller.SaveConfiguration(new HuePluginConfigurationSettings
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "",
            HueClientKey = ""
        });

        var preserveResponse = Assert.IsType<OkObjectResult>(preserveAction.Result);
        var preservedSettings = Assert.IsType<HuePluginConfigurationSettings>(preserveResponse.Value);
        Assert.Equal("stored-app-key", configuration.HueAppKey);
        Assert.Equal("stored-client-key", configuration.HueClientKey);
        Assert.True(preservedSettings.HasAppKey);
        Assert.True(preservedSettings.HasClientKey);
        Assert.Empty(preservedSettings.HueAppKey);
        Assert.Empty(preservedSettings.HueClientKey);

        var clearAction = controller.SaveConfiguration(new HuePluginConfigurationSettings
        {
            HueBridgeIp = "192.168.1.100",
            ClearStoredCredentials = true
        });

        Assert.IsType<OkObjectResult>(clearAction.Result);
        Assert.Empty(configuration.HueAppKey);
        Assert.Empty(configuration.HueClientKey);
    }

    [Fact]
    public void SaveConfiguration_InvalidGlobalChannelProfileReturnsBadRequestWithoutSaving()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "app-key",
            HueClientKey = "client-key",
            EntertainmentAreaId = "area-1",
            ChannelIds = "2, 9"
        });

        var action = CreateController().SaveConfiguration(new HuePluginConfigurationSettings
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "app-key",
            HueClientKey = "client-key",
            EntertainmentAreaId = "area-1",
            ChannelIds = "1, nope"
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Equal("2, 9", configuration.ChannelIds);
        Assert.Equal("app-key", configuration.HueAppKey);
        Assert.Equal("client-key", configuration.HueClientKey);
    }

    [Fact]
    public void SaveConfiguration_InvalidCatchUpWindowReturnsBadRequestWithoutSaving()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SceneAutomationCatchUpMinutes = 12
        });

        var action = CreateController().SaveConfiguration(new HuePluginConfigurationSettings
        {
            SceneAutomationCatchUpMinutes = PluginConfiguration.MaxSceneAutomationCatchUpMinutes + 1
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Equal(12, configuration.SceneAutomationCatchUpMinutes);
    }

    [Fact]
    public void SaveUserMapping_BlankSecretsPreserveExistingCredentials()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-1",
                    UserName = "Viewer",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.100",
                    HueAppKey = "old-app-secret",
                    HueClientKey = "old-client-secret",
                    EntertainmentAreaId = "old-area",
                    EntertainmentAreaName = "Old Room"
                }
            }
        });

        var action = CreateController().SaveUserMapping(new UserBridgeMapping
        {
            UserId = "user-1",
            UserName = "Viewer",
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.101",
            EntertainmentAreaId = "new-area",
            EntertainmentAreaName = "New Room",
            UseCinemaModeOverride = true,
            BrightnessDimLevelOverride = 20,
            PauseBehaviorOverride = PluginConfiguration.PauseBehaviorKeepLastColors,
            RestoreLightStateOverride = false,
            BrightnessBoostOverride = 125,
            RedGainOverride = 115,
            GreenGainOverride = 95,
            BlueGainOverride = 105,
            ColorSaturationOverride = 80,
            HueShiftDegreesOverride = 30,
            OutputBrightnessPercentOverride = 65,
            BlackoutThresholdOverride = 20,
            ColorChangeThresholdOverride = 3,
            UseGpuOverride = true,
            CustomFfmpegFlagsOverride = "-threads 2",
            FfmpegStallTimeoutSecondsOverride = 15,
            NetworkRetryAttemptsOverride = 2,
            ChannelIdsOverride = "1, 3",
            TargetFpsOverride = 30,
            FrameResolutionOverride = PluginConfiguration.FrameResolutionLow,
            VideoScalingModeOverride = PluginConfiguration.VideoScalingModeCrop,
            VideoDeinterlaceModeOverride = PluginConfiguration.VideoDeinterlaceModeOn,
            SamplingBreadthPercentOverride = 20,
            SamplingModeOverride = PluginConfiguration.SamplingModeCenterPixel,
            ColorSmoothingPercentOverride = 35
        });

        Assert.IsType<OkObjectResult>(action);
        var mapping = Assert.Single(configuration.UserMappings);
        Assert.Equal("old-app-secret", mapping.HueAppKey);
        Assert.Equal("old-client-secret", mapping.HueClientKey);
        Assert.Equal("192.168.1.101", mapping.HueBridgeIp);
        Assert.Equal("new-area", mapping.EntertainmentAreaId);
        Assert.Equal((bool?)true, mapping.UseCinemaModeOverride);
        Assert.Equal((int?)20, mapping.BrightnessDimLevelOverride);
        Assert.Equal(PluginConfiguration.PauseBehaviorKeepLastColors, mapping.PauseBehaviorOverride);
        Assert.Equal((bool?)false, mapping.RestoreLightStateOverride);
        Assert.Equal((int?)125, mapping.BrightnessBoostOverride);
        Assert.Equal((int?)115, mapping.RedGainOverride);
        Assert.Equal((int?)95, mapping.GreenGainOverride);
        Assert.Equal((int?)105, mapping.BlueGainOverride);
        Assert.Equal((int?)80, mapping.ColorSaturationOverride);
        Assert.Equal((int?)30, mapping.HueShiftDegreesOverride);
        Assert.Equal((int?)65, mapping.OutputBrightnessPercentOverride);
        Assert.Equal((int?)20, mapping.BlackoutThresholdOverride);
        Assert.Equal((int?)3, mapping.ColorChangeThresholdOverride);
        Assert.Equal((bool?)true, mapping.UseGpuOverride);
        Assert.Equal("-threads 2", mapping.CustomFfmpegFlagsOverride);
        Assert.Equal((int?)15, mapping.FfmpegStallTimeoutSecondsOverride);
        Assert.Equal((int?)2, mapping.NetworkRetryAttemptsOverride);
        Assert.Equal("1, 3", mapping.ChannelIdsOverride);
        Assert.Equal((int?)30, mapping.TargetFpsOverride);
        Assert.Equal(PluginConfiguration.FrameResolutionLow, mapping.FrameResolutionOverride);
        Assert.Equal(PluginConfiguration.VideoScalingModeCrop, mapping.VideoScalingModeOverride);
        Assert.Equal(PluginConfiguration.VideoDeinterlaceModeOn, mapping.VideoDeinterlaceModeOverride);
        Assert.Equal((int?)20, mapping.SamplingBreadthPercentOverride);
        Assert.Equal(PluginConfiguration.SamplingModeCenterPixel, mapping.SamplingModeOverride);
        Assert.Equal((int?)35, mapping.ColorSmoothingPercentOverride);
    }

    [Fact]
    public void SaveUserMapping_WithoutBridgeInheritsGlobalConfiguration()
    {
        var userId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "global-app",
            HueClientKey = "global-client",
            EntertainmentAreaId = "global-area",
            ChannelIds = "2, 9"
        });

        var action = CreateController().SaveUserMapping(new UserBridgeMapping
        {
            UserId = userId.ToString(),
            UserName = "Viewer",
            SyncEnabled = true,
            BrightnessBoostOverride = 125,
            ChannelIdsOverride = "1, 3"
        });

        Assert.IsType<OkObjectResult>(action);
        var mapping = Assert.Single(configuration.UserMappings);
        Assert.Empty(mapping.HueBridgeIp);
        Assert.Empty(mapping.HueAppKey);
        Assert.Empty(mapping.HueClientKey);
        Assert.Empty(mapping.EntertainmentAreaId);
        Assert.Empty(mapping.EntertainmentAreaName);
        Assert.Equal((int?)125, mapping.BrightnessBoostOverride);
        Assert.Equal("1, 3", mapping.ChannelIdsOverride);

        var bridge = configuration.GetBridgeConfigForUser(userId);
        Assert.Equal("192.168.1.100", bridge.BridgeIp);
        Assert.Equal("global-app", bridge.AppKey);
        Assert.Equal("global-client", bridge.ClientKey);
        Assert.Equal("global-area", bridge.AreaId);
    }

    [Fact]
    public void SaveUserMapping_ChangingCustomTargetToDefaultClearsCredentials()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "global-app",
            HueClientKey = "global-client",
            EntertainmentAreaId = "global-area",
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-1",
                    UserName = "Viewer",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "old-app-secret",
                    HueClientKey = "old-client-secret",
                    EntertainmentAreaId = "old-area",
                    EntertainmentAreaName = "Old Room"
                }
            }
        });

        var action = CreateController().SaveUserMapping(new UserBridgeMapping
        {
            UserId = "user-1",
            UserName = "Viewer",
            SyncEnabled = true,
            BrightnessBoostOverride = 150
        });

        Assert.IsType<OkObjectResult>(action);
        var mapping = Assert.Single(configuration.UserMappings);
        Assert.Empty(mapping.HueBridgeIp);
        Assert.Empty(mapping.HueAppKey);
        Assert.Empty(mapping.HueClientKey);
        Assert.Empty(mapping.EntertainmentAreaId);
        Assert.Empty(mapping.EntertainmentAreaName);
        Assert.Equal((int?)150, mapping.BrightnessBoostOverride);
    }

    [Fact]
    public void SaveUserMapping_DefaultBridgeRejectsPartialCredentials()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "global-app",
            HueClientKey = "global-client",
            EntertainmentAreaId = "global-area"
        });

        var action = CreateController().SaveUserMapping(new UserBridgeMapping
        {
            UserId = "user-default",
            SyncEnabled = true,
            HueAppKey = "accidental-mapping-secret"
        });

        var response = Assert.IsType<BadRequestObjectResult>(action);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Contains("Leave mapping bridge credentials", response.Value?.ToString(), StringComparison.Ordinal);
        Assert.Empty(configuration.UserMappings);
    }

    [Fact]
    public void SaveUserMapping_NewEnabledMappingStillRequiresCredentials()
    {
        InstallConfiguration(new PluginConfiguration());

        var action = CreateController().SaveUserMapping(new UserBridgeMapping
        {
            UserId = "new-user",
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            EntertainmentAreaId = "area-1"
        });

        var response = Assert.IsType<BadRequestObjectResult>(action);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
    }

    [Fact]
    public void SaveUserMapping_InvalidProfileOverrideReturnsBadRequestWithoutSaving()
    {
        var configuration = InstallConfiguration(new PluginConfiguration());

        var action = CreateController().SaveUserMapping(new UserBridgeMapping
        {
            UserId = "new-user",
            SyncEnabled = false,
            BrightnessDimLevelOverride = 101,
            PauseBehaviorOverride = "InvalidPauseBehavior",
            TargetFpsOverride = 0,
            FfmpegStallTimeoutSecondsOverride = 0,
            NetworkRetryAttemptsOverride = 11,
            ChannelIdsOverride = "1, bad"
        });

        var response = Assert.IsType<BadRequestObjectResult>(action);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        var validationBody = System.Text.Json.JsonSerializer.Serialize(response.Value);
        Assert.Contains("target FPS override must be between 1 and 60", validationBody, StringComparison.Ordinal);
        Assert.Contains("FFmpeg stall timeout override must be between 1 and 60 seconds", validationBody, StringComparison.Ordinal);
        Assert.Contains("network retry attempts override must be between 0 and 10", validationBody, StringComparison.Ordinal);
        Assert.Contains("channel IDs override must be a comma-separated list of IDs from 0 to 65535", validationBody, StringComparison.Ordinal);
        Assert.Empty(configuration.UserMappings);
    }

    [Fact]
    public void SaveUserMapping_DisabledMappingClearsStoredCredentials()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-1",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.100",
                    HueAppKey = "old-app-secret",
                    HueClientKey = "old-client-secret",
                    EntertainmentAreaId = "area-1"
                }
            }
        });

        var action = CreateController().SaveUserMapping(new UserBridgeMapping
        {
            UserId = "user-1",
            SyncEnabled = false
        });

        Assert.IsType<OkObjectResult>(action);
        var mapping = Assert.Single(configuration.UserMappings);
        Assert.Empty(mapping.HueAppKey);
        Assert.Empty(mapping.HueClientKey);
        Assert.Empty(mapping.HueBridgeIp);
        Assert.Empty(mapping.EntertainmentAreaId);
    }

    private HueApiController CreateController(
        IHueStreamTester? streamTester = null,
        HueBridgeLifecycleGate? bridgeLifecycleGate = null,
        IHueEnvironmentProbe? environmentProbe = null,
        HueDiagnosticsCancellationGate? diagnosticsCancellationGate = null,
        IEnumerable<IHostedService>? hostedServices = null)
    {
        var client = new HueClient(_httpClient, _loggerMock.Object);
        return new HueApiController(
            client,
            hostedServices ?? Array.Empty<IHostedService>(),
            streamTester,
            bridgeLifecycleGate,
            environmentProbe,
            diagnosticsCancellationGate);
    }

    private static PluginConfiguration InstallConfiguration(PluginConfiguration configuration)
    {
        var pluginDataPath = Path.Combine(Path.GetTempPath(), "jellyfin-hue-api-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pluginDataPath);
        var applicationPaths = new Mock<IApplicationPaths>();
        applicationPaths.SetupGet(paths => paths.ProgramDataPath).Returns(pluginDataPath);
        applicationPaths.SetupGet(paths => paths.WebPath).Returns(pluginDataPath);
        applicationPaths.SetupGet(paths => paths.ProgramSystemPath).Returns(pluginDataPath);
        applicationPaths.SetupGet(paths => paths.DataPath).Returns(pluginDataPath);
        applicationPaths.SetupGet(paths => paths.ImageCachePath).Returns(pluginDataPath);
        applicationPaths.SetupGet(paths => paths.PluginsPath).Returns(pluginDataPath);
        applicationPaths.SetupGet(paths => paths.PluginConfigurationsPath).Returns(pluginDataPath);
        applicationPaths.SetupGet(paths => paths.LogDirectoryPath).Returns(pluginDataPath);
        applicationPaths.SetupGet(paths => paths.ConfigurationDirectoryPath).Returns(pluginDataPath);
        applicationPaths.SetupGet(paths => paths.SystemConfigurationFilePath).Returns(Path.Combine(pluginDataPath, "system.xml"));
        applicationPaths.SetupGet(paths => paths.CachePath).Returns(pluginDataPath);
        applicationPaths.SetupGet(paths => paths.TempDirectory).Returns(pluginDataPath);
        applicationPaths.SetupGet(paths => paths.VirtualDataPath).Returns(pluginDataPath);

        var plugin = new Plugin(applicationPaths.Object, Mock.Of<IXmlSerializer>());
        var configurationField = plugin.GetType().BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!;
        configurationField.SetValue(plugin, configuration);
        return plugin.Configuration;
    }

    private void SetupHttpResponse(HttpStatusCode statusCode, string body)
    {
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
