using System.Net;
using System.Text;
using Jellyfin.Plugin.Hue.Api;
using Jellyfin.Plugin.Hue.Hue;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

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
    public async Task PostEntertainmentAreas_WithoutCredentials_ReturnsBadRequest()
    {
        var controller = CreateController();

        var action = await controller.PostEntertainmentAreas(new HueEntertainmentAreasRequest());

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
    }

    private HueApiController CreateController()
    {
        var client = new HueClient(_httpClient, _loggerMock.Object);
        return new HueApiController(client, Array.Empty<IHostedService>());
    }

    private void SetupHttpResponse(HttpStatusCode statusCode, string body)
    {
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
