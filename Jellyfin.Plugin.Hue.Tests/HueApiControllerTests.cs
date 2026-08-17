using System.Net;
using System.Reflection;
using System.Text;
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
                It.IsAny<System.Text.Json.JsonElement>()))
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
            EntertainmentAreaId = "area-1"
        });

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
            It.IsAny<System.Text.Json.JsonElement>()), Times.Once);
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
        Assert.Null(status.LastError);
        Assert.False(status.CanStopSync);
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
                    EntertainmentAreaName = "Living Room"
                }
            }
        });

        var action = CreateController().GetUserMappings();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var mapping = Assert.Single(Assert.IsAssignableFrom<IEnumerable<UserBridgeMappingSummary>>(response.Value));
        Assert.Equal("user-1", mapping.UserId);
        Assert.True(mapping.HasAppKey);
        Assert.True(mapping.HasClientKey);
        var serialized = System.Text.Json.JsonSerializer.Serialize(mapping);
        Assert.DoesNotContain("mapping-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("mapping-client-secret", serialized, StringComparison.Ordinal);
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
            SamplingBreadthPercent = 25,
            ColorSmoothingPercent = 65,
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
        Assert.Equal("default-app-key", settings.HueAppKey);
        Assert.Equal(25, settings.SamplingBreadthPercent);
        Assert.Equal(65, settings.ColorSmoothingPercent);
        var serialized = System.Text.Json.JsonSerializer.Serialize(settings);
        Assert.DoesNotContain("mapping-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("UserMappings", serialized, StringComparison.OrdinalIgnoreCase);
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
            TargetFps = 30,
            SamplingBreadthPercent = 25,
            ColorSmoothingPercent = 40
        });

        Assert.IsType<OkObjectResult>(action.Result);
        Assert.Equal(30, configuration.TargetFps);
        Assert.Equal(25, configuration.SamplingBreadthPercent);
        Assert.Equal(40, configuration.ColorSmoothingPercent);
        var mapping = Assert.Single(configuration.UserMappings);
        Assert.Equal("mapping-app-secret", mapping.HueAppKey);
        Assert.Equal("mapping-client-secret", mapping.HueClientKey);
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
            EntertainmentAreaName = "New Room"
        });

        Assert.IsType<OkObjectResult>(action);
        var mapping = Assert.Single(configuration.UserMappings);
        Assert.Equal("old-app-secret", mapping.HueAppKey);
        Assert.Equal("old-client-secret", mapping.HueClientKey);
        Assert.Equal("192.168.1.101", mapping.HueBridgeIp);
        Assert.Equal("new-area", mapping.EntertainmentAreaId);
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

    private HueApiController CreateController(IHueStreamTester? streamTester = null)
    {
        var client = new HueClient(_httpClient, _loggerMock.Object);
        return new HueApiController(client, Array.Empty<IHostedService>(), streamTester);
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
            .ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
