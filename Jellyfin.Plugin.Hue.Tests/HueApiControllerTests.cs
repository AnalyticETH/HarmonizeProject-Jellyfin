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
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
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
    public void EntertainmentAreas_DoesNotExposeSecretBearingGetRoute()
    {
        var getRoutes = typeof(HueApiController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SelectMany(method => method.GetCustomAttributes<HttpGetAttribute>())
            .Where(route => string.Equals(route.Template, "EntertainmentAreas", StringComparison.OrdinalIgnoreCase));

        Assert.Empty(getRoutes);
    }

    [Fact]
    public void UserMappingSummariesAndExportsIgnoreNullEntries()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                null!,
                new() { UserId = "valid-user", UserName = "Viewer", SyncEnabled = false }
            }
        });
        var controller = CreateController();

        var mappingsAction = controller.GetUserMappings();
        var mappingsResponse = Assert.IsType<OkObjectResult>(mappingsAction.Result);
        var mappings = Assert.IsAssignableFrom<IEnumerable<UserBridgeMappingSummary>>(mappingsResponse.Value).ToArray();
        Assert.Collection(mappings, mapping => Assert.Equal("valid-user", mapping.UserId));
        Assert.Empty(configuration.UserMappings[1].MappingId);

        var exportAction = controller.ExportConfiguration();
        var exportResponse = Assert.IsType<OkObjectResult>(exportAction.Result);
        var export = Assert.IsType<HueConfigurationExportDocument>(exportResponse.Value);
        Assert.Collection(export.UserMappings, mapping => Assert.Equal("valid-user", mapping.UserId));
    }

    [Fact]
    public void GetUserMappingReconciliationReportsLiveIdentityDriftWithoutCredentials()
    {
        var healthyUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var renamedUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var missingUserId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var duplicateUserId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var liveHealthyUser = new Jellyfin.Data.Entities.User("Healthy Viewer", "auth", "reset") { Id = healthyUserId };
        var liveRenamedUser = new Jellyfin.Data.Entities.User("Renamed Viewer", "auth", "reset") { Id = renamedUserId };
        var userManager = new Mock<IUserManager>();
        userManager.Setup(manager => manager.GetUserById(healthyUserId)).Returns(liveHealthyUser);
        userManager.Setup(manager => manager.GetUserById(renamedUserId)).Returns(liveRenamedUser);
        userManager.Setup(manager => manager.GetUserById(missingUserId)).Returns((Jellyfin.Data.Entities.User?)null);
        userManager.Setup(manager => manager.GetUserById(duplicateUserId)).Returns(liveHealthyUser);
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = healthyUserId.ToString("D"), UserName = "Healthy Viewer", HueAppKey = "secret-app", HueClientKey = "secret-client" },
                new() { UserId = renamedUserId.ToString("D"), UserName = "Old Viewer" },
                new() { UserId = missingUserId.ToString("D"), UserName = "Deleted Viewer" },
                new() { UserId = "not-a-guid", UserName = "Malformed Viewer" },
                new() { UserId = duplicateUserId.ToString("D"), UserName = "Duplicate One" },
                new() { UserId = "{" + duplicateUserId.ToString("D") + "}", UserName = "Duplicate Two" }
            }
        });

        var action = CreateController(userManager: userManager.Object).GetUserMappingReconciliation();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueUserMappingReconciliationResult>(response.Value);
        Assert.True(result.UserDirectoryAvailable);
        Assert.Equal(6, result.MappingCount);
        Assert.Equal(1, result.HealthyCount);
        Assert.Equal(1, result.RenamedCount);
        Assert.Equal(1, result.MissingCount);
        Assert.Equal(1, result.InvalidCount);
        Assert.Equal(2, result.DuplicateCount);
        Assert.Contains(result.Mappings, mapping =>
            mapping.Status == HueUserMappingReconciliationStatus.RenamedUser &&
            mapping.CurrentUserName == "Renamed Viewer" &&
            mapping.NeedsRepair);
        Assert.DoesNotContain("secret-app", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-client", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.All(configuration.UserMappings, mapping => Assert.Empty(mapping.MappingId));
    }

    [Fact]
    public void ApplyUserMappingReconciliationCanonicalizesAndRefreshesExistingUsers()
    {
        var userId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var liveUser = new Jellyfin.Data.Entities.User("Current Viewer", "auth", "reset") { Id = userId };
        var userManager = new Mock<IUserManager>();
        userManager.Setup(manager => manager.GetUserById(userId)).Returns(liveUser);
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "{" + userId.ToString("D") + "}",
                    UserName = "Stale Viewer",
                    SyncEnabled = false,
                    HueAppKey = "preserve-app",
                    HueClientKey = "preserve-client"
                }
            }
        });

        var action = CreateController(userManager: userManager.Object).ApplyUserMappingReconciliation();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueUserMappingReconciliationResult>(response.Value);
        Assert.True(result.Applied);
        Assert.Equal(1, result.UpdatedCount);
        var mapping = Assert.Single(configuration.UserMappings);
        Assert.Equal(userId.ToString("D"), mapping.UserId);
        Assert.Equal("Current Viewer", mapping.UserName);
        Assert.Equal("preserve-app", mapping.HueAppKey);
        Assert.Equal("preserve-client", mapping.HueClientKey);
    }

    [Fact]
    public void ResolveDuplicateUserMappingsRetainsExactKeeperAndMakesRuntimeUnique()
    {
        var userId = Guid.Parse("56565656-5656-5656-5656-565656565656");
        var liveUser = new Jellyfin.Data.Entities.User("Current Keeper", "auth", "reset") { Id = userId };
        var userManager = new Mock<IUserManager>();
        userManager.Setup(manager => manager.GetUserById(userId)).Returns(liveUser);
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
                    MappingId = "duplicate-keeper",
                    UserId = "{" + userId.ToString("D") + "}",
                    UserName = "Old Keeper",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "keeper-app-secret",
                    HueClientKey = "keeper-client-secret",
                    EntertainmentAreaId = "keeper-area"
                },
                new()
                {
                    MappingId = "duplicate-remove",
                    UserId = userId.ToString("N"),
                    UserName = "Sibling Row",
                    SyncEnabled = false,
                    HueBridgeIp = "192.168.1.102",
                    HueAppKey = "sibling-app-secret",
                    HueClientKey = "sibling-client-secret",
                    EntertainmentAreaId = "sibling-area"
                }
            }
        });
        var controller = CreateController(userManager: userManager.Object);
        var report = Assert.IsType<HueUserMappingReconciliationResult>(
            Assert.IsType<OkObjectResult>(controller.GetUserMappingReconciliation().Result).Value);

        var action = controller.ResolveDuplicateUserMappings(new HueUserMappingDuplicateResolutionRequest
        {
            RetainMappingId = "duplicate-keeper",
            RemoveMappingIds = new List<string> { "duplicate-remove" },
            ExpectedReportVersion = report.ReportVersion
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueUserMappingDuplicateResolutionResult>(response.Value);
        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(userId.ToString("D"), result.UserId);
        Assert.Equal("duplicate-keeper", result.RetainedMapping!.MappingId);
        Assert.Single(result.RemovedMappings);
        Assert.Single(configuration.UserMappings);
        Assert.Equal(userId.ToString("D"), configuration.UserMappings[0].UserId);
        Assert.Equal("Current Keeper", configuration.UserMappings[0].UserName);
        Assert.False(configuration.HasAmbiguousUserMapping(userId));
        Assert.Equal("192.168.1.101", configuration.GetBridgeConfigForUser(userId).BridgeIp);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("keeper-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("sibling-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("keeper-client-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("sibling-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveDuplicateUserMappingsRejectsStaleReportWithoutMutation()
    {
        var userId = Guid.Parse("57575757-5757-5757-5757-575757575757");
        var liveUser = new Jellyfin.Data.Entities.User("Viewer", "auth", "reset") { Id = userId };
        var userManager = new Mock<IUserManager>();
        userManager.Setup(manager => manager.GetUserById(userId)).Returns(liveUser);
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new() { MappingId = "stale-keeper", UserId = userId.ToString("D"), UserName = "First", SyncEnabled = true },
                new() { MappingId = "stale-remove", UserId = userId.ToString("D"), UserName = "Second", SyncEnabled = false }
            }
        });
        var controller = CreateController(userManager: userManager.Object);
        var report = Assert.IsType<HueUserMappingReconciliationResult>(
            Assert.IsType<OkObjectResult>(controller.GetUserMappingReconciliation().Result).Value);
        configuration.UserMappings[0].UserName = "Changed after report";

        var action = controller.ResolveDuplicateUserMappings(new HueUserMappingDuplicateResolutionRequest
        {
            RetainMappingId = "stale-keeper",
            RemoveMappingIds = new List<string> { "stale-remove" },
            ExpectedReportVersion = report.ReportVersion
        });

        var response = Assert.IsType<ConflictObjectResult>(action.Result);
        var result = Assert.IsType<HueUserMappingDuplicateResolutionResult>(response.Value);
        Assert.Equal(0, result.RemovedCount);
        Assert.Equal(2, configuration.UserMappings.Count);
        Assert.Equal("Changed after report", configuration.UserMappings[0].UserName);
        Assert.NotEqual(report.ReportVersion, result.ReportVersion);
    }

    [Fact]
    public void ResolveDuplicateUserMappingsRejectsDisabledKeeperBeforeMutation()
    {
        var userId = Guid.Parse("58585858-5858-5858-5858-585858585858");
        var liveUser = new Jellyfin.Data.Entities.User("Viewer", "auth", "reset") { Id = userId };
        var userManager = new Mock<IUserManager>();
        userManager.Setup(manager => manager.GetUserById(userId)).Returns(liveUser);
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new() { MappingId = "disabled-keeper", UserId = userId.ToString("D"), UserName = "Disabled", SyncEnabled = false },
                new() { MappingId = "enabled-sibling", UserId = userId.ToString("D"), UserName = "Enabled", SyncEnabled = true }
            }
        });
        var controller = CreateController(userManager: userManager.Object);
        var report = Assert.IsType<HueUserMappingReconciliationResult>(
            Assert.IsType<OkObjectResult>(controller.GetUserMappingReconciliation().Result).Value);

        var action = controller.ResolveDuplicateUserMappings(new HueUserMappingDuplicateResolutionRequest
        {
            RetainMappingId = "disabled-keeper",
            RemoveMappingIds = new List<string> { "enabled-sibling" },
            ExpectedReportVersion = report.ReportVersion
        });

        var response = Assert.IsType<ConflictObjectResult>(action.Result);
        var result = Assert.IsType<HueUserMappingDuplicateResolutionResult>(response.Value);
        Assert.Contains(result.ValidationErrors, error => error.Contains("must be enabled", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, configuration.UserMappings.Count);
    }

    [Fact]
    public void ResolveDuplicateUserMappingsRejectsKeeperThatWouldBreakSavedDeviceRoute()
    {
        var userId = Guid.Parse("5a5a5a5a-5a5a-5a5a-5a5a-5a5a5a5a5a5a");
        var liveUser = new Jellyfin.Data.Entities.User("Viewer", "auth", "reset") { Id = userId };
        var userManager = new Mock<IUserManager>();
        userManager.Setup(manager => manager.GetUserById(userId)).Returns(liveUser);
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "global-app",
            HueClientKey = "global-client",
            EntertainmentAreaId = "global-area",
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            UserMappings = new List<UserBridgeMapping>
            {
                new() { MappingId = "route-keeper", UserId = userId.ToString("D"), UserName = "Keeper", SyncEnabled = true },
                new()
                {
                    MappingId = "route-remove",
                    UserId = userId.ToString("D"),
                    UserName = "Route sibling",
                    SyncEnabled = false,
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new() { DeviceId = "living-room", HueBridgeIp = "192.168.1.102", HueAppKey = "route-app", HueClientKey = "route-client", EntertainmentAreaId = "route-area" }
                    }
                }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "route-cue",
                    Name = "Route cue",
                    PresetName = "Evening",
                    TargetRoutes = new List<HueSceneScheduleTargetRoute>
                    {
                        new() { UserId = userId.ToString("D"), DeviceId = "living-room" }
                    }
                }
            }
        });
        var controller = CreateController(userManager: userManager.Object);
        var report = Assert.IsType<HueUserMappingReconciliationResult>(
            Assert.IsType<OkObjectResult>(controller.GetUserMappingReconciliation().Result).Value);

        var action = controller.ResolveDuplicateUserMappings(new HueUserMappingDuplicateResolutionRequest
        {
            RetainMappingId = "route-keeper",
            RemoveMappingIds = new List<string> { "route-remove" },
            ExpectedReportVersion = report.ReportVersion
        });

        var response = Assert.IsType<ConflictObjectResult>(action.Result);
        var result = Assert.IsType<HueUserMappingDuplicateResolutionResult>(response.Value);
        Assert.Contains(result.ValidationErrors, error => error.Contains("device route", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, configuration.UserMappings.Count);
        Assert.Equal("route-remove", configuration.UserMappings[1].MappingId);
    }

    [Fact]
    public void ResolveDuplicateUserMappingsPersistenceFailureRestoresEveryRow()
    {
        var serializer = new Mock<IXmlSerializer>();
        serializer
            .Setup(xml => xml.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("duplicate resolution persistence failed"));
        var userId = Guid.Parse("59595959-5959-5959-5959-595959595959");
        var liveUser = new Jellyfin.Data.Entities.User("Viewer", "auth", "reset") { Id = userId };
        var userManager = new Mock<IUserManager>();
        userManager.Setup(manager => manager.GetUserById(userId)).Returns(liveUser);
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "global-app",
            HueClientKey = "global-client",
            EntertainmentAreaId = "global-area",
            UserMappings = new List<UserBridgeMapping>
            {
                new() { MappingId = "rollback-keeper", UserId = userId.ToString("D"), UserName = "Original Keeper", SyncEnabled = true },
                new() { MappingId = "rollback-remove", UserId = userId.ToString("D"), UserName = "Original Sibling", SyncEnabled = false }
            }
        }, serializer.Object);
        var previousMappings = configuration.UserMappings;
        var controller = CreateController(userManager: userManager.Object);
        var report = Assert.IsType<HueUserMappingReconciliationResult>(
            Assert.IsType<OkObjectResult>(controller.GetUserMappingReconciliation().Result).Value);

        var action = controller.ResolveDuplicateUserMappings(new HueUserMappingDuplicateResolutionRequest
        {
            RetainMappingId = "rollback-keeper",
            RemoveMappingIds = new List<string> { "rollback-remove" },
            ExpectedReportVersion = report.ReportVersion
        });

        var response = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, response.StatusCode);
        Assert.Same(previousMappings, configuration.UserMappings);
        Assert.Equal(new[] { "rollback-keeper", "rollback-remove" }, configuration.UserMappings.Select(mapping => mapping.MappingId));
        Assert.Equal("Original Keeper", configuration.UserMappings[0].UserName);
    }

    [Fact]
    public void CleanupStaleUserMappingsDeletesExactMissingAndMalformedRows()
    {
        var missingUserId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var userManager = new Mock<IUserManager>();
        userManager.Setup(manager => manager.GetUserById(missingUserId))
            .Returns((Jellyfin.Data.Entities.User?)null);
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    MappingId = "missing-row",
                    UserId = missingUserId.ToString("D"),
                    UserName = "Deleted Viewer",
                    HueAppKey = "secret-app",
                    HueClientKey = "secret-client"
                },
                new()
                {
                    MappingId = "malformed-row",
                    UserId = "not-a-guid",
                    UserName = "Malformed Viewer"
                }
            }
        });
        var controller = CreateController(userManager: userManager.Object);
        var reportResponse = Assert.IsType<OkObjectResult>(controller.GetUserMappingReconciliation().Result);
        var report = Assert.IsType<HueUserMappingReconciliationResult>(reportResponse.Value);
        Assert.NotEmpty(report.ReportVersion);

        var action = controller.CleanupStaleUserMappings(new HueUserMappingCleanupRequest
        {
            MappingIds = new List<string> { "missing-row", "malformed-row" },
            ExpectedReportVersion = report.ReportVersion
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueUserMappingCleanupResult>(response.Value);
        Assert.Equal(2, result.RequestedCount);
        Assert.Equal(2, result.DeletedCount);
        Assert.DoesNotContain(configuration.UserMappings, mapping => mapping != null);
        Assert.DoesNotContain("secret-app", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-client", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void CleanupStaleUserMappingsRejectsStaleReportWithoutMutation()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new() { MappingId = "stale-row", UserId = "not-a-guid", UserName = "Original" }
            }
        });
        var controller = CreateController(userManager: new Mock<IUserManager>().Object);
        var reportResponse = Assert.IsType<OkObjectResult>(controller.GetUserMappingReconciliation().Result);
        var report = Assert.IsType<HueUserMappingReconciliationResult>(reportResponse.Value);
        configuration.UserMappings[0].UserName = "Changed after report";

        var action = controller.CleanupStaleUserMappings(new HueUserMappingCleanupRequest
        {
            MappingIds = new List<string> { "stale-row" },
            ExpectedReportVersion = report.ReportVersion
        });

        var response = Assert.IsType<ConflictObjectResult>(action.Result);
        var result = Assert.IsType<HueUserMappingCleanupResult>(response.Value);
        Assert.Equal(0, result.DeletedCount);
        Assert.Single(configuration.UserMappings);
        Assert.Equal("Changed after report", configuration.UserMappings[0].UserName);
        Assert.NotEqual(report.ReportVersion, result.ReportVersion);
    }

    [Fact]
    public void CleanupStaleUserMappingsBlocksReferencedRowsAtomically()
    {
        var missingUserId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var userManager = new Mock<IUserManager>();
        userManager.Setup(manager => manager.GetUserById(missingUserId))
            .Returns((Jellyfin.Data.Entities.User?)null);
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new() { MappingId = "referenced-row", UserId = missingUserId.ToString("D"), UserName = "Deleted Viewer" }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "schedule-1", Name = "Keep this cue", TargetUserId = missingUserId.ToString("D") }
            }
        });
        var controller = CreateController(userManager: userManager.Object);
        var reportResponse = Assert.IsType<OkObjectResult>(controller.GetUserMappingReconciliation().Result);
        var report = Assert.IsType<HueUserMappingReconciliationResult>(reportResponse.Value);

        var action = controller.CleanupStaleUserMappings(new HueUserMappingCleanupRequest
        {
            MappingIds = new List<string> { "referenced-row" },
            ExpectedReportVersion = report.ReportVersion
        });

        var response = Assert.IsType<ConflictObjectResult>(action.Result);
        var result = Assert.IsType<HueUserMappingCleanupResult>(response.Value);
        var blocked = Assert.Single(result.BlockedMappings);
        Assert.Equal("referenced-row", blocked.MappingId);
        Assert.False(blocked.CanDelete);
        Assert.Single(configuration.UserMappings);
    }

    [Fact]
    public async Task RegisterBridge_RejectsPublicAddressWithoutContactingBridge()
    {
        var controller = CreateController();

        var action = await controller.RegisterBridge(new HueRegistrationRequest
        {
            IpAddress = "8.8.8.8"
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Equal(
            "A valid private bridge IP address or .local host name is required.",
            response.Value);
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RegisterBridge_ValidPrivateAddressReturnsCredentialsAndTrimsAddress()
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
                    "[{\"success\":{\"username\":\"bridge-user\",\"clientkey\":\"bridge-client-key\"}}]",
                    Encoding.UTF8,
                    "application/json")
            });
        var controller = CreateController();

        var action = await controller.RegisterBridge(new HueRegistrationRequest
        {
            IpAddress = " 192.168.1.100 "
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var registration = Assert.IsType<HueRegistrationResult>(response.Value);
        Assert.Equal("bridge-user", registration.Username);
        Assert.Equal("bridge-client-key", registration.ClientKey);
        Assert.NotNull(capturedRequest);
        Assert.Equal("https://192.168.1.100/api", capturedRequest!.RequestUri!.ToString());
    }

    [Fact]
    public void SaveUserMappingWithNullExistingEntryDoesNotThrow()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping> { null! }
        });

        var action = CreateController().SaveUserMapping(new UserBridgeMapping
        {
            UserId = "new-user",
            SyncEnabled = false
        });

        Assert.IsType<OkObjectResult>(action);
        var mapping = Assert.Single(configuration.UserMappings);
        Assert.Equal("new-user", mapping.UserId);
    }

    [Fact]
    public void SaveUserMappingJsonRequestPreservesEnteredCredentialsWithoutMakingReadModelsSecretBearing()
    {
        var configuration = InstallConfiguration(new PluginConfiguration());
        var request = JsonSerializer.Deserialize<HueUserMappingRequest>("""
            {
              "UserId": "{11111111-1111-1111-1111-111111111111}",
              "UserName": "JSON Viewer",
              "SyncEnabled": true,
              "HueBridgeIp": "192.168.1.101",
              "HueAppKey": "mapping-app-key",
              "HueClientKey": "mapping-client-key",
              "EntertainmentAreaId": "area-json",
              "DeviceTargets": [
                {
                  "DeviceId": "living-room-tv",
                  "DeviceName": "Living Room TV",
                  "HueBridgeIp": "192.168.1.102",
                  "HueAppKey": "device-app-key",
                  "HueClientKey": "device-client-key",
                  "EntertainmentAreaId": "area-device"
                }
              ]
            }
            """)!;

        var action = CreateController().SaveUserMapping(request);

        Assert.IsType<OkObjectResult>(action);
        var mapping = Assert.Single(configuration.UserMappings);
        Assert.Equal("11111111-1111-1111-1111-111111111111", mapping.UserId);
        Assert.Equal("mapping-app-key", mapping.HueAppKey);
        Assert.Equal("mapping-client-key", mapping.HueClientKey);
        var deviceTarget = Assert.Single(mapping.DeviceTargets);
        Assert.Equal("device-app-key", deviceTarget.HueAppKey);
        Assert.Equal("device-client-key", deviceTarget.HueClientKey);

        var serializedMapping = JsonSerializer.Serialize(mapping);
        Assert.DoesNotContain("mapping-app-key", serializedMapping, StringComparison.Ordinal);
        Assert.DoesNotContain("mapping-client-key", serializedMapping, StringComparison.Ordinal);
        Assert.DoesNotContain("device-app-key", serializedMapping, StringComparison.Ordinal);
        Assert.DoesNotContain("device-client-key", serializedMapping, StringComparison.Ordinal);
        Assert.DoesNotContain("HueAppKey", serializedMapping, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HueClientKey", serializedMapping, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SaveUserMappingJsonRequest_RejectsOversizedDeviceTargetCollectionBeforeMaterializingIt()
    {
        var configuration = InstallConfiguration(new PluginConfiguration());
        var deviceTargets = string.Join(
            ",",
            Enumerable.Range(0, PluginConfiguration.MaxDeviceTargetsPerUser + 1)
                .Select(index => $"{{\"DeviceId\":\"device-{index}\"}}"));
        var requestJson = """
            {
              "UserId": "11111111-1111-1111-1111-111111111111",
              "SyncEnabled": false,
              "DeviceTargets": [__DEVICE_TARGETS__]
            }
            """.Replace("__DEVICE_TARGETS__", deviceTargets, StringComparison.Ordinal);
        var request = JsonSerializer.Deserialize<HueUserMappingRequest>(requestJson)!;

        var action = CreateController().SaveUserMapping(request);

        var response = Assert.IsType<BadRequestObjectResult>(action);
        Assert.Equal(
            $"User mapping may define no more than {PluginConfiguration.MaxDeviceTargetsPerUser} device targets.",
            response.Value);
        Assert.Empty(configuration.UserMappings);
    }

    [Fact]
    public void SaveUserMappingJsonRequest_RejectsMalformedDeviceTargetValuesWithoutMutation()
    {
        var existingMapping = new UserBridgeMapping
        {
            UserId = "22222222-2222-2222-2222-222222222222",
            UserName = "Existing Viewer",
            SyncEnabled = false
        };
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping> { existingMapping }
        });
        var request = JsonSerializer.Deserialize<HueUserMappingRequest>("""
            {
              "UserId": "11111111-1111-1111-1111-111111111111",
              "UserName": "Rejected Viewer",
              "SyncEnabled": false,
              "DeviceTargets": [42]
            }
            """)!;

        var action = CreateController().SaveUserMapping(request);

        var response = Assert.IsType<BadRequestObjectResult>(action);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Equal("Mapping contains invalid property values.", response.Value);
        Assert.Same(existingMapping, Assert.Single(configuration.UserMappings));
    }

    [Fact]
    public void SaveUserMappingJsonRequest_RejectsCaseVariantDuplicatePropertiesBeforeMaterializingTargets()
    {
        var configuration = InstallConfiguration(new PluginConfiguration());
        var deviceTargets = string.Join(
            ",",
            Enumerable.Range(0, PluginConfiguration.MaxDeviceTargetsPerUser + 1)
                .Select(index => $"{{\"DeviceId\":\"device-{index}\"}}"));
        var requestJson = """
            {
              "UserId": "11111111-1111-1111-1111-111111111111",
              "SyncEnabled": false,
              "DeviceTargets": [],
              "devicetargets": [__DEVICE_TARGETS__]
            }
            """.Replace("__DEVICE_TARGETS__", deviceTargets, StringComparison.Ordinal);
        var request = JsonSerializer.Deserialize<HueUserMappingRequest>(requestJson)!;

        var action = CreateController().SaveUserMapping(request);

        var response = Assert.IsType<BadRequestObjectResult>(action);
        Assert.Equal(
            "Mapping contains duplicate property names that differ only by case.",
            response.Value);
        Assert.Empty(configuration.UserMappings);
    }

    [Fact]
    public void SaveUserMappingJsonRequest_RejectsCaseVariantDuplicateNestedPropertiesBeforeMaterializingTargets()
    {
        var configuration = InstallConfiguration(new PluginConfiguration());
        var request = JsonSerializer.Deserialize<HueUserMappingRequest>("""
            {
              "UserId": "11111111-1111-1111-1111-111111111111",
              "SyncEnabled": false,
              "DeviceTargets": [
                {
                  "DeviceId": "living-room-tv",
                  "deviceid": "other-device"
                }
              ]
            }
            """)!;

        var action = CreateController().SaveUserMapping(request);

        var response = Assert.IsType<BadRequestObjectResult>(action);
        Assert.Equal(
            "Mapping contains duplicate property names that differ only by case.",
            response.Value);
        Assert.Empty(configuration.UserMappings);
    }

    [Fact]
    public void SaveUserMappingJsonRequest_RejectsMalformedUserIdWithoutMutation()
    {
        var existingMapping = new UserBridgeMapping
        {
            UserId = "22222222-2222-2222-2222-222222222222",
            UserName = "Existing Viewer",
            SyncEnabled = false
        };
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping> { existingMapping }
        });
        var request = JsonSerializer.Deserialize<HueUserMappingRequest>("""
            {
              "UserId": "not-a-jellyfin-user-id",
              "UserName": "Rejected Viewer",
              "SyncEnabled": false
            }
            """)!;

        var action = CreateController().SaveUserMapping(request);

        var response = Assert.IsType<BadRequestObjectResult>(action);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Equal("userId must be a valid Jellyfin user ID.", response.Value);
        Assert.Same(existingMapping, Assert.Single(configuration.UserMappings));
        Assert.Equal("Existing Viewer", configuration.UserMappings[0].UserName);
    }

    [Fact]
    public void SaveUserMappingJsonRequest_RejectsNewMappingAtMaximumCapacityWithoutMutation()
    {
        var mappings = Enumerable.Range(0, PluginConfiguration.MaxUserMappings)
            .Select(index => new UserBridgeMapping
            {
                MappingId = $"mapping-{index}",
                UserId = Guid.NewGuid().ToString("D"),
                UserName = $"Viewer {index}",
                SyncEnabled = false
            })
            .ToList();
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = mappings
        });
        var previousMappings = configuration.UserMappings;
        var newUserId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var request = JsonSerializer.Deserialize<HueUserMappingRequest>(
            "{\"UserId\":\"" + newUserId + "\",\"UserName\":\"Rejected Viewer\",\"SyncEnabled\":false}")!;

        var action = CreateController().SaveUserMapping(request);

        var response = Assert.IsType<BadRequestObjectResult>(action);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Contains(
            $"No more than {PluginConfiguration.MaxUserMappings} user mappings may be saved",
            JsonSerializer.Serialize(response.Value),
            StringComparison.Ordinal);
        Assert.Same(previousMappings, configuration.UserMappings);
        Assert.Equal(PluginConfiguration.MaxUserMappings, configuration.UserMappings.Count);
        Assert.DoesNotContain(configuration.UserMappings, mapping => mapping.UserId == newUserId);
    }

    [Fact]
    public void SaveUserMappingJsonRequest_UpdatesExistingMappingAtMaximumCapacity()
    {
        var mappings = Enumerable.Range(0, PluginConfiguration.MaxUserMappings)
            .Select(index => new UserBridgeMapping
            {
                MappingId = $"mapping-{index}",
                UserId = Guid.NewGuid().ToString("D"),
                UserName = $"Viewer {index}",
                SyncEnabled = false
            })
            .ToList();
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = mappings
        });
        var existing = configuration.UserMappings[0];
        var request = JsonSerializer.Deserialize<HueUserMappingRequest>(
            "{\"MappingId\":\"" + existing.MappingId + "\",\"UserId\":\"" + existing.UserId + "\",\"UserName\":\"Updated Viewer\",\"SyncEnabled\":false}")!;

        var action = CreateController().SaveUserMapping(request);

        Assert.IsType<OkObjectResult>(action);
        Assert.Equal(PluginConfiguration.MaxUserMappings, configuration.UserMappings.Count);
        var updated = Assert.Single(configuration.UserMappings, mapping => mapping.MappingId == existing.MappingId);
        Assert.Equal("Updated Viewer", updated.UserName);
        Assert.Equal(
            PluginConfiguration.MaxUserMappings,
            configuration.UserMappings.Select(mapping => mapping.MappingId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

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
    public async Task PostEntertainmentAreas_DeviceRouteUsesExactStoredCredentials()
    {
        InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-device",
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new()
                        {
                            DeviceId = "Living-Room-TV",
                            HueBridgeIp = "192.168.1.101",
                            HueAppKey = "device-app-key",
                            HueClientKey = "device-client-key"
                        }
                    }
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
            UserId = "user-device",
            DeviceId = "Living-Room-TV",
            IpAddress = "192.168.1.101"
        });

        Assert.IsType<OkObjectResult>(action.Result);
        Assert.NotNull(capturedRequest);
        Assert.Equal("device-app-key", capturedRequest!.Headers.GetValues("hue-application-key").Single());
    }

    [Fact]
    public async Task PostEntertainmentAreas_DeviceRouteDoesNotFallBackForWrongCaseOrBridge()
    {
        InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-device",
                    HueBridgeIp = "192.168.1.100",
                    HueAppKey = "outer-app-key",
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new()
                        {
                            DeviceId = "Living-Room-TV",
                            HueBridgeIp = "192.168.1.101",
                            HueAppKey = "device-app-key"
                        }
                    }
                }
            }
        });
        var controller = CreateController();

        var wrongCase = await controller.PostEntertainmentAreas(new HueEntertainmentAreasRequest
        {
            UserId = "user-device",
            DeviceId = "living-room-tv",
            IpAddress = "192.168.1.101"
        });
        Assert.IsType<BadRequestObjectResult>(wrongCase.Result);

        var wrongBridge = await controller.PostEntertainmentAreas(new HueEntertainmentAreasRequest
        {
            UserId = "user-device",
            DeviceId = "Living-Room-TV",
            IpAddress = "192.168.1.100"
        });
        Assert.IsType<BadRequestObjectResult>(wrongBridge.Result);
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PostEntertainmentChannels_DeviceRouteUsesStoredCredentials()
    {
        InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-device",
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new()
                        {
                            DeviceId = "tv-1",
                            HueBridgeIp = "192.168.1.101",
                            HueAppKey = "device-app-key"
                        }
                    }
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
                Content = new StringContent("{\"data\":[{\"channels\":[{\"channel_id\":4}]}]}", Encoding.UTF8, "application/json")
            });
        var controller = CreateController();

        var action = await controller.PostEntertainmentChannels(new HueEntertainmentChannelsRequest
        {
            UserId = "user-device",
            DeviceId = "tv-1",
            IpAddress = "192.168.1.101",
            EntertainmentAreaId = "area-1"
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        Assert.Equal(4, Assert.Single(Assert.IsAssignableFrom<IEnumerable<HueEntertainmentChannel>>(response.Value)).ChannelId);
        Assert.NotNull(capturedRequest);
        Assert.Equal("device-app-key", capturedRequest!.Headers.GetValues("hue-application-key").Single());
    }

    [Fact]
    public async Task TestConnection_DeviceRouteUsesStoredCredentialsWithoutGlobalFallback()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "global-app-key",
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-device",
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new()
                        {
                            DeviceId = "tv-1",
                            HueBridgeIp = "192.168.1.101",
                            HueAppKey = "device-app-key",
                            HueClientKey = "device-client-key"
                        }
                    }
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
                    "{\"data\":[{\"id\":\"area-1\",\"metadata\":{\"name\":\"Living Room\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            });
        var controller = CreateController();

        var action = await controller.TestConnection(new HueConnectionTestRequest
        {
            UserId = "user-device",
            DeviceId = "tv-1",
            IpAddress = "192.168.1.101"
        });

        Assert.IsType<OkObjectResult>(action.Result);
        Assert.NotNull(capturedRequest);
        Assert.Equal("device-app-key", capturedRequest!.Headers.GetValues("hue-application-key").Single());
    }

    [Fact]
    public void GetPlaybackDevices_ReturnsBoundedCredentialFreeExactRoutes()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var sessions = new[]
        {
            new SessionInfoDto
            {
                UserId = userId,
                UserName = "Viewer",
                DeviceId = "Living-Room-TV",
                DeviceName = "Living Room TV",
                Client = "Jellyfin Web",
                DeviceType = "Web",
                ApplicationVersion = "10.10",
                IsActive = true,
                LastActivityDate = DateTime.UtcNow
            },
            new SessionInfoDto
            {
                UserId = userId,
                UserName = "Viewer",
                DeviceId = "Living-Room-TV",
                DeviceName = "Older name",
                IsActive = false,
                LastActivityDate = DateTime.UtcNow.AddHours(-1)
            },
            new SessionInfoDto
            {
                UserId = otherUserId,
                UserName = "Other",
                DeviceId = "Other-TV",
                DeviceName = "Other TV"
            },
            new SessionInfoDto
            {
                UserId = userId,
                DeviceId = "   "
            }
        };
        var sessionManager = new Mock<ISessionManager>();
        sessionManager
            .Setup(manager => manager.GetSessions(Guid.Empty, null, 86400, null, false))
            .Returns(sessions);
        var controller = CreateController(sessionManager: sessionManager.Object);

        var action = controller.GetPlaybackDevices(userId.ToString());

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var devices = Assert.IsAssignableFrom<IEnumerable<HuePlaybackDeviceSummary>>(response.Value).ToArray();
        var device = Assert.Single(devices);
        Assert.Equal(userId.ToString(), device.UserId);
        Assert.Equal("Living-Room-TV", device.DeviceId);
        Assert.Equal("Living Room TV", device.DeviceName);
        Assert.True(device.IsActive);
        var serialized = JsonSerializer.Serialize(device);
        Assert.DoesNotContain("app-key", serialized, StringComparison.OrdinalIgnoreCase);
        sessionManager.Verify(manager => manager.GetSessions(Guid.Empty, null, 86400, null, false), Times.Once);
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
    public async Task CaptureCurrentColor_UsesConfiguredDefaultTargetAndReturnsSanitizedSample()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "capture-app-secret",
            HueClientKey = "capture-client-secret",
            EntertainmentAreaId = "area-1",
            ChannelIds = "1"
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
                    "{\"data\":[{\"id\":\"area-1\",\"metadata\":{\"name\":\"Living Room\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"channels\":[{\"channel_id\":1,\"members\":[{\"service\":{\"rid\":\"light-1\"}}]}]}]}",
                    Encoding.UTF8,
                    "application/json")
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"on\":{\"on\":true},\"dimming\":{\"brightness\":62.5},\"color\":{\"xy\":{\"x\":0.64,\"y\":0.33}}}]}",
                    Encoding.UTF8,
                    "application/json")
            });

        var action = await CreateController().CaptureCurrentColor(new HueCurrentLightColorRequest());

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueCurrentLightColorResult>(response.Value);
        Assert.True(result.Succeeded);
        Assert.Equal("Default bridge", result.TargetLabel);
        Assert.Equal(1, result.AttemptedLightCount);
        Assert.Equal(1, result.CapturedLightCount);
        Assert.Equal(1, result.SampledLightCount);
        Assert.Equal(62, result.BrightnessPercent);
        Assert.True(result.Red > result.Green);
        Assert.True(result.Red > result.Blue);

        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("capture-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("capture-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureCurrentColor_RejectsUnknownTargetBeforeBridgeActivity()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "capture-app-secret",
            EntertainmentAreaId = "area-1"
        });

        var action = await CreateController().CaptureCurrentColor(new HueCurrentLightColorRequest
        {
            TargetUserId = "missing-user"
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CaptureCurrentColor_RejectsEnabledAndDisabledDuplicateBeforeBridgeActivity()
    {
        InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "ambiguous-user",
                    UserName = "Enabled row",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.111",
                    HueAppKey = "enabled-app-secret",
                    EntertainmentAreaId = "area-enabled"
                },
                new()
                {
                    UserId = "ambiguous-user",
                    UserName = "Disabled row",
                    SyncEnabled = false
                }
            }
        });

        var action = await CreateController().CaptureCurrentColor(new HueCurrentLightColorRequest
        {
            TargetUserId = "ambiguous-user"
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Contains("multiple mapping rows", response.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CaptureCurrentColor_RejectsTwoEnabledDuplicateMappingsBeforeBridgeActivity()
    {
        InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "ambiguous-user",
                    UserName = "First room",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.111",
                    HueAppKey = "first-app-secret",
                    EntertainmentAreaId = "area-first"
                },
                new()
                {
                    UserId = "ambiguous-user",
                    UserName = "Second room",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.112",
                    HueAppKey = "second-app-secret",
                    EntertainmentAreaId = "area-second"
                }
            }
        });

        var action = await CreateController().CaptureCurrentColor(new HueCurrentLightColorRequest
        {
            TargetUserId = "ambiguous-user"
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Contains("multiple mapping rows", response.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CaptureCurrentColors_RejectsAmbiguousDeviceRouteBeforeBridgeActivity()
    {
        InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "ambiguous-user",
                    UserName = "First room",
                    SyncEnabled = true,
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new()
                        {
                            DeviceId = "device-tv",
                            HueBridgeIp = "192.168.1.111",
                            HueAppKey = "first-device-secret",
                            EntertainmentAreaId = "area-first"
                        }
                    }
                },
                new()
                {
                    UserId = "ambiguous-user",
                    UserName = "Second room",
                    SyncEnabled = true,
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new()
                        {
                            DeviceId = "device-tv",
                            HueBridgeIp = "192.168.1.112",
                            HueAppKey = "second-device-secret",
                            EntertainmentAreaId = "area-second"
                        }
                    }
                }
            }
        });

        var action = await CreateController().CaptureCurrentColors(new HueCurrentLightColorBatchRequest
        {
            TargetRoutes = new List<HueCurrentLightColorTargetRoute>
            {
                new() { UserId = "ambiguous-user", DeviceId = "device-tv" }
            }
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Contains("ambiguous", response.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CaptureCurrentColors_AllTargetsRejectsDuplicateMappingsBeforeBridgeActivity()
    {
        InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "ambiguous-user",
                    UserName = "First room",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.111",
                    HueAppKey = "first-app-secret",
                    EntertainmentAreaId = "area-first"
                },
                new()
                {
                    UserId = "ambiguous-user",
                    UserName = "Second room",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.112",
                    HueAppKey = "second-app-secret",
                    EntertainmentAreaId = "area-second"
                }
            }
        });

        var action = await CreateController().CaptureCurrentColors(new HueCurrentLightColorBatchRequest
        {
            TargetAllEnabledMappings = true
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Contains("ambiguous", response.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CaptureCurrentColor_ResolvesExplicitDeviceRouteWithoutBaseMapping()
    {
        InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-device",
                    UserName = "Living room",
                    SyncEnabled = true,
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new()
                        {
                            DeviceId = "device-tv",
                            DeviceName = "Living room TV",
                            HueBridgeIp = "192.168.1.111",
                            HueAppKey = "device-app-secret",
                            HueClientKey = "device-client-secret",
                            EntertainmentAreaId = "area-device",
                            EntertainmentAreaName = "Living room",
                            ChannelIdsOverride = "4"
                        }
                    }
                }
            }
        });
        var requests = new List<HttpRequestMessage>();
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"id\":\"area-device\",\"metadata\":{\"name\":\"Living room\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"channels\":[{\"channel_id\":4,\"members\":[{\"service\":{\"rid\":\"light-device\"}}]}]}]}",
                    Encoding.UTF8,
                    "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"on\":{\"on\":true},\"dimming\":{\"brightness\":55},\"color\":{\"xy\":{\"x\":0.64,\"y\":0.33}}}]}",
                    Encoding.UTF8,
                    "application/json")
            }
        });
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => requests.Add(request))
            .Returns(() => Task.FromResult(responses.Dequeue()));

        var action = await CreateController().CaptureCurrentColor(new HueCurrentLightColorRequest
        {
            TargetUserId = "user-device",
            TargetDeviceId = "device-tv"
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueCurrentLightColorResult>(response.Value);
        Assert.True(result.Succeeded);
        Assert.Equal("Living room / Living room TV", result.TargetLabel);
        Assert.Equal("user-device", result.TargetUserId);
        Assert.Equal("device-tv", result.TargetDeviceId);
        Assert.Equal("Living room TV", result.TargetDeviceName);
        Assert.Equal(3, requests.Count);
        Assert.All(requests, request => Assert.Equal("192.168.1.111", request.RequestUri!.Host));
        Assert.All(requests, request => Assert.Equal("device-app-secret", request.Headers.GetValues("hue-application-key").Single()));

        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("device-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("device-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureCurrentColor_DeviceRouteIdsRemainCaseSensitive()
    {
        InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-device",
                    UserName = "Living room",
                    SyncEnabled = true,
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new()
                        {
                            DeviceId = "Device-TV",
                            HueBridgeIp = "192.168.1.111",
                            HueAppKey = "device-app-secret",
                            EntertainmentAreaId = "area-device"
                        }
                    }
                }
            }
        });

        var action = await CreateController().CaptureCurrentColor(new HueCurrentLightColorRequest
        {
            TargetUserId = "user-device",
            TargetDeviceId = "device-tv"
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CaptureCurrentColors_ResolvesSelectedDeviceRoutesAndReturnsRouteMetadata()
    {
        InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-device",
                    UserName = "Bedroom",
                    SyncEnabled = true,
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new()
                        {
                            DeviceId = "device-bedroom",
                            DeviceName = "Bedroom TV",
                            HueBridgeIp = "192.168.1.112",
                            HueAppKey = "batch-device-app-secret",
                            HueClientKey = "batch-device-client-secret",
                            EntertainmentAreaId = "area-bedroom",
                            ChannelIdsOverride = "2"
                        }
                    }
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
                    "{\"data\":[{\"id\":\"area-bedroom\",\"metadata\":{\"name\":\"Bedroom\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"channels\":[{\"channel_id\":2,\"members\":[{\"service\":{\"rid\":\"light-bedroom\"}}]}]}]}",
                    Encoding.UTF8,
                    "application/json")
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"on\":{\"on\":true},\"dimming\":{\"brightness\":70},\"color\":{\"xy\":{\"x\":0.15,\"y\":0.06}}}]}",
                    Encoding.UTF8,
                    "application/json")
            });

        var action = await CreateController().CaptureCurrentColors(new HueCurrentLightColorBatchRequest
        {
            TargetRoutes = new List<HueCurrentLightColorTargetRoute>
            {
                new() { UserId = "user-device", DeviceId = "device-bedroom" }
            }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueCurrentLightColorBatchResult>(response.Value);
        var capture = Assert.Single(result.Captures);
        Assert.True(result.Succeeded);
        Assert.Equal("user-device", result.TargetRoutes.Single().UserId);
        Assert.Equal("device-bedroom", result.TargetRoutes.Single().DeviceId);
        Assert.Equal("Bedroom / Bedroom TV", capture.TargetLabel);
        Assert.Equal("device-bedroom", capture.TargetDeviceId);
        Assert.Equal(70, capture.BrightnessPercent);

        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("batch-device-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("batch-device-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureCurrentColors_MalformedRouteFailsClosedWithoutContactingBridge()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "capture-app-secret",
            EntertainmentAreaId = "area-1"
        });

        var action = await CreateController().CaptureCurrentColors(new HueCurrentLightColorBatchRequest
        {
            TargetRoutes = new List<HueCurrentLightColorTargetRoute>
            {
                new() { UserId = " ", DeviceId = "device-tv" }
            }
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Contains("user mapping", response.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CaptureCurrentColors_BlankTargetUserIdFailsClosedWithoutContactingBridge()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "capture-app-secret",
            EntertainmentAreaId = "area-1"
        });

        var action = await CreateController().CaptureCurrentColors(new HueCurrentLightColorBatchRequest
        {
            TargetUserIds = new List<string> { " " }
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Contains("user mapping", response.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CaptureCurrentColors_AllowsDistinctCaseSensitiveDeviceRoutes()
    {
        InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-device",
                    UserName = "Living room",
                    SyncEnabled = true,
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new()
                        {
                            DeviceId = "Device-TV",
                            DeviceName = "Upper TV",
                            HueBridgeIp = "192.168.1.111",
                            HueAppKey = "device-upper-secret",
                            EntertainmentAreaId = "area-upper",
                            ChannelIdsOverride = "4"
                        },
                        new()
                        {
                            DeviceId = "device-tv",
                            DeviceName = "Lower TV",
                            HueBridgeIp = "192.168.1.112",
                            HueAppKey = "device-lower-secret",
                            EntertainmentAreaId = "area-lower",
                            ChannelIdsOverride = "5"
                        }
                    }
                }
            }
        });

        var responses = new Queue<HttpResponseMessage>(new[]
        {
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"id\":\"area-upper\",\"metadata\":{\"name\":\"Upper\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"channels\":[{\"channel_id\":4,\"members\":[{\"service\":{\"rid\":\"light-upper\"}}]}]}]}",
                    Encoding.UTF8,
                    "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"on\":{\"on\":true},\"dimming\":{\"brightness\":55},\"color\":{\"xy\":{\"x\":0.64,\"y\":0.33}}}]}",
                    Encoding.UTF8,
                    "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"id\":\"area-lower\",\"metadata\":{\"name\":\"Lower\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"channels\":[{\"channel_id\":5,\"members\":[{\"service\":{\"rid\":\"light-lower\"}}]}]}]}",
                    Encoding.UTF8,
                    "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"on\":{\"on\":true},\"dimming\":{\"brightness\":65},\"color\":{\"xy\":{\"x\":0.15,\"y\":0.06}}}]}",
                    Encoding.UTF8,
                    "application/json")
            }
        });
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(() => Task.FromResult(responses.Dequeue()));

        var action = await CreateController().CaptureCurrentColors(new HueCurrentLightColorBatchRequest
        {
            TargetRoutes = new List<HueCurrentLightColorTargetRoute>
            {
                new() { UserId = "user-device", DeviceId = "Device-TV" },
                new() { UserId = "USER-DEVICE", DeviceId = "device-tv" }
            }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueCurrentLightColorBatchResult>(response.Value);
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.AttemptedTargetCount);
        Assert.Equal(new[] { "Device-TV", "device-tv" }, result.Captures.Select(capture => capture.TargetDeviceId));
        Assert.Equal(new[] { "Upper TV", "Lower TV" }, result.Captures.Select(capture => capture.TargetDeviceName));
        Assert.Empty(responses);

        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("device-upper-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("device-lower-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureCurrentColors_AggregatesSelectedTargetsWithoutReturningSecrets()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "default-app-secret",
            HueClientKey = "default-client-secret",
            EntertainmentAreaId = "area-1",
            ChannelIds = "1",
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-custom",
                    UserName = "Bedroom",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "mapping-app-secret",
                    HueClientKey = "mapping-client-secret",
                    EntertainmentAreaId = "area-2",
                    ChannelIdsOverride = "2"
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
                    "{\"data\":[{\"id\":\"area-1\",\"metadata\":{\"name\":\"Living Room\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"channels\":[{\"channel_id\":1,\"members\":[{\"service\":{\"rid\":\"light-1\"}}]}]}]}",
                    Encoding.UTF8,
                    "application/json")
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"on\":{\"on\":true},\"dimming\":{\"brightness\":50},\"color\":{\"xy\":{\"x\":0.64,\"y\":0.33}}}]}",
                    Encoding.UTF8,
                    "application/json")
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"id\":\"area-2\",\"metadata\":{\"name\":\"Bedroom\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"channels\":[{\"channel_id\":2,\"members\":[{\"service\":{\"rid\":\"light-2\"}},{\"service\":{\"rid\":\"light-3\"}}]}]}]}",
                    Encoding.UTF8,
                    "application/json")
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"on\":{\"on\":true},\"dimming\":{\"brightness\":80},\"color\":{\"xy\":{\"x\":0.15,\"y\":0.06}}}]}",
                    Encoding.UTF8,
                    "application/json")
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"on\":{\"on\":true},\"dimming\":{\"brightness\":80},\"color\":{\"xy\":{\"x\":0.15,\"y\":0.06}}}]}",
                    Encoding.UTF8,
                    "application/json")
            });

        var action = await CreateController().CaptureCurrentColors(new HueCurrentLightColorBatchRequest
        {
            IncludeDefaultTarget = true,
            TargetUserIds = new List<string> { "user-custom" }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueCurrentLightColorBatchResult>(response.Value);
        Assert.True(result.Succeeded, result.Message + " :: " + string.Join(" | ", result.Captures.Select(capture => capture.TargetLabel + "=" + capture.Message)));
        Assert.Equal(2, result.AttemptedTargetCount);
        Assert.Equal(2, result.SuccessfulTargetCount);
        Assert.Equal(3, result.SampledLightCount);
        Assert.Equal(65, result.BrightnessPercent);
        Assert.Equal(new[] { "user-custom" }, result.TargetUserIds);
        Assert.Equal(new[] { "Default bridge", "Bedroom" }, result.Captures.Select(capture => capture.TargetLabel));
        Assert.True(result.Red > 0);
        Assert.True(result.Blue > 0);

        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("default-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("default-client-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("mapping-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("mapping-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureCurrentColors_AllTargetsDeduplicatesInheritedMappingsAndReportsPartialFailure()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "default-app-secret",
            EntertainmentAreaId = "area-1",
            ChannelIds = "1",
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-inherited",
                    UserName = "Inherited",
                    SyncEnabled = true
                },
                new()
                {
                    UserId = "user-broken",
                    UserName = "Broken",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.102",
                    HueAppKey = "broken-app-secret",
                    EntertainmentAreaId = "area-broken",
                    ChannelIdsOverride = "1"
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
                    "{\"data\":[{\"id\":\"area-1\",\"metadata\":{\"name\":\"Living Room\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"channels\":[{\"channel_id\":1,\"members\":[{\"service\":{\"rid\":\"light-1\"}}]}]}]}",
                    Encoding.UTF8,
                    "application/json")
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"on\":{\"on\":true},\"dimming\":{\"brightness\":40},\"color\":{\"xy\":{\"x\":0.64,\"y\":0.33}}}]}",
                    Encoding.UTF8,
                    "application/json")
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("bridge unavailable", Encoding.UTF8, "text/plain")
            });

        var action = await CreateController().CaptureCurrentColors(new HueCurrentLightColorBatchRequest
        {
            TargetAllEnabledMappings = true
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueCurrentLightColorBatchResult>(response.Value);
        Assert.False(result.Succeeded);
        Assert.Equal(2, result.AttemptedTargetCount);
        Assert.Equal(1, result.SuccessfulTargetCount);
        Assert.Equal(1, result.SampledLightCount);
        Assert.Equal(1, result.Captures.Count(capture => capture.TargetLabel == "Default bridge"));
        Assert.Contains(result.Captures, capture => capture.TargetLabel == "Broken" && !capture.Succeeded);
    }

    [Fact]
    public async Task CaptureCurrentColor_RejectsConcurrentDiagnosticLifecycle()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "capture-app-secret",
            EntertainmentAreaId = "area-1"
        });
        var gate = new HueBridgeLifecycleGate();
        using var activeDiagnostic = gate.TryEnterDiagnostic();
        Assert.NotNull(activeDiagnostic);

        var action = await CreateController(bridgeLifecycleGate: gate).CaptureCurrentColor(
            new HueCurrentLightColorRequest());

        var response = Assert.IsType<ConflictObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CaptureCurrentColorAndBatch_RejectConfigurationMutationBeforeTargetResolution()
    {
        InstallConfiguration(new PluginConfiguration());
        var gate = new HueBridgeLifecycleGate();
        using var mutation = gate.TryEnterConfigurationMutation();
        Assert.NotNull(mutation);

        var controller = CreateController(bridgeLifecycleGate: gate);
        var single = await controller.CaptureCurrentColor(new HueCurrentLightColorRequest());
        var batch = await controller.CaptureCurrentColors(new HueCurrentLightColorBatchRequest());

        AssertConflict(single.Result!);
        AssertConflict(batch.Result!);
        _httpHandlerMock.VerifyNoOtherCalls();
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
    public async Task TestConnection_WithNoValidEntertainmentChannelsReportsNoControllableChannels()
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
                    "{\"data\":[{\"channels\":[{\"channel_id\":-1},{\"channel_id\":65536},{\"channel_id\":\"2\"}]}]}",
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
            EntertainmentAreaId = "area-1"
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueConnectionTestResult>(response.Value);
        Assert.False(result.AreaFound);
        Assert.Null(result.AreaName);
        Assert.Contains("no controllable channels", result.Message, StringComparison.OrdinalIgnoreCase);
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
    public async Task Preview_WithTemperatureEffectPassesCanonicalEffectToStreamTester()
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
                PluginConfiguration.ColorPresetEffectTemperature,
                125))
            .ReturnsAsync(new HueStreamProbeResult { Succeeded = true, Message = "Temperature preview sent." });
        var controller = CreateController(streamTester.Object);

        var action = await controller.Preview(new HuePreviewRequest
        {
            IpAddress = "192.168.1.100",
            AppKey = "app-key",
            ClientKey = "client-key",
            EntertainmentAreaId = "area-1",
            Effect = " temperature ",
            DurationSeconds = 3,
            EffectSpeedPercent = 125
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HuePreviewResult>(response.Value);
        Assert.True(result.Succeeded);
        Assert.Equal(PluginConfiguration.ColorPresetEffectTemperature, result.Effect);
        Assert.Equal(125, result.EffectSpeedPercent);
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
            PluginConfiguration.ColorPresetEffectTemperature,
            125), Times.Once);
    }

    [Fact]
    public async Task Preview_WithAuroraEffectPassesCanonicalEffectToStreamTester()
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
                PluginConfiguration.ColorPresetEffectAurora,
                140))
            .ReturnsAsync(new HueStreamProbeResult { Succeeded = true, Message = "Aurora preview sent." });
        var controller = CreateController(streamTester.Object);

        var action = await controller.Preview(new HuePreviewRequest
        {
            IpAddress = "192.168.1.100",
            AppKey = "app-key",
            ClientKey = "client-key",
            EntertainmentAreaId = "area-1",
            Effect = " aurora ",
            DurationSeconds = 4,
            EffectSpeedPercent = 140
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HuePreviewResult>(response.Value);
        Assert.True(result.Succeeded);
        Assert.Equal(PluginConfiguration.ColorPresetEffectAurora, result.Effect);
        Assert.Equal(140, result.EffectSpeedPercent);
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
            4,
            It.IsAny<CancellationToken>(),
            0,
            0,
            PluginConfiguration.ColorPresetEffectAurora,
            140), Times.Once);
    }

    [Fact]
    public async Task Preview_WithLightningEffectPassesCanonicalEffectToStreamTester()
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
                PluginConfiguration.ColorPresetEffectLightning,
                160))
            .ReturnsAsync(new HueStreamProbeResult { Succeeded = true, Message = "Lightning preview sent." });
        var controller = CreateController(streamTester.Object);

        var action = await controller.Preview(new HuePreviewRequest
        {
            IpAddress = "192.168.1.100",
            AppKey = "app-key",
            ClientKey = "client-key",
            EntertainmentAreaId = "area-1",
            Effect = " lightning ",
            DurationSeconds = 4,
            EffectSpeedPercent = 160
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HuePreviewResult>(response.Value);
        Assert.True(result.Succeeded);
        Assert.Equal(PluginConfiguration.ColorPresetEffectLightning, result.Effect);
        Assert.Equal(160, result.EffectSpeedPercent);
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
            4,
            It.IsAny<CancellationToken>(),
            0,
            0,
            PluginConfiguration.ColorPresetEffectLightning,
            160), Times.Once);
    }

    [Fact]
    public async Task Preview_WithStarlightEffectPassesCanonicalEffectToStreamTester()
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
                PluginConfiguration.ColorPresetEffectStarlight,
                180))
            .ReturnsAsync(new HueStreamProbeResult { Succeeded = true, Message = "Starlight preview sent." });
        var controller = CreateController(streamTester.Object);

        var action = await controller.Preview(new HuePreviewRequest
        {
            IpAddress = "192.168.1.100",
            AppKey = "app-key",
            ClientKey = "client-key",
            EntertainmentAreaId = "area-1",
            Effect = " starlight ",
            DurationSeconds = 4,
            EffectSpeedPercent = 180
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HuePreviewResult>(response.Value);
        Assert.True(result.Succeeded);
        Assert.Equal(PluginConfiguration.ColorPresetEffectStarlight, result.Effect);
        Assert.Equal(180, result.EffectSpeedPercent);
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
            4,
            It.IsAny<CancellationToken>(),
            0,
            0,
            PluginConfiguration.ColorPresetEffectStarlight,
            180), Times.Once);
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

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HuePreviewResult>(response.Value);
        Assert.Empty(result.TargetUserIds);
        Assert.Empty(result.TargetRoutes);
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
    public async Task Preview_RejectsConfigurationMutationBeforeResolvingCredentials()
    {
        InstallConfiguration(new PluginConfiguration());
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

        var gate = new HueBridgeLifecycleGate();
        using var mutation = gate.TryEnterConfigurationMutation();
        Assert.NotNull(mutation);

        var action = await CreateController(
            streamTester.Object,
            bridgeLifecycleGate: gate).Preview(new HuePreviewRequest
            {
                IpAddress = "192.168.1.100",
                AppKey = "request-app-key",
                ClientKey = "request-client-key",
                EntertainmentAreaId = "area-1",
                DurationSeconds = 2
            });

        AssertConflict(action.Result!);
        _httpHandlerMock.VerifyNoOtherCalls();
        streamTester.VerifyNoOtherCalls();
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

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HuePreviewResult>(response.Value);
        Assert.Equal(new[] { "user-custom" }, result.TargetUserIds);
        Assert.Empty(result.TargetRoutes);
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
    public async Task Preview_BlankRedactedCredentialsUsesExactNestedDeviceRouteKeys()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "global-app-key",
            HueClientKey = "global-client-key",
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-custom",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "mapping-app-key",
                    HueClientKey = "mapping-client-key",
                    EntertainmentAreaId = "outer-area",
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new()
                        {
                            DeviceId = "tv-1",
                            HueBridgeIp = "192.168.1.102",
                            HueAppKey = "device-app-key",
                            HueClientKey = "device-client-key",
                            EntertainmentAreaId = "device-area"
                        }
                    }
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
            DeviceId = "tv-1",
            IpAddress = "192.168.1.102",
            AppKey = "",
            ClientKey = "",
            EntertainmentAreaId = "device-area",
            Red = 20,
            Green = 30,
            Blue = 40,
            DurationSeconds = 2
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HuePreviewResult>(response.Value);
        Assert.Equal(new[] { "user-custom" }, result.TargetUserIds);
        var route = Assert.Single(result.TargetRoutes);
        Assert.Equal("user-custom", route.UserId);
        Assert.Equal("tv-1", route.DeviceId);
        var serializedResult = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("device-app-key", serializedResult, StringComparison.Ordinal);
        Assert.DoesNotContain("device-client-key", serializedResult, StringComparison.Ordinal);
        streamTester.Verify(tester => tester.PreviewAsync(
            "192.168.1.102",
            "device-app-key",
            "device-client-key",
            "device-area",
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
    public async Task Preview_DeviceRouteRequiresSpecificUserMappingWithoutContactingBridge()
    {
        var streamTester = new Mock<IHueStreamTester>();
        var controller = CreateController(streamTester.Object);

        var action = await controller.Preview(new HuePreviewRequest
        {
            DeviceId = "tv-1",
            IpAddress = "192.168.1.102",
            AppKey = "",
            ClientKey = "",
            EntertainmentAreaId = "device-area"
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("one specific user mapping", response.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        streamTester.VerifyNoOtherCalls();
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Preview_DeviceRouteCannotUseBroadcastOrSelectedTargets()
    {
        var streamTester = new Mock<IHueStreamTester>();
        var controller = CreateController(streamTester.Object);

        var broadcast = await controller.Preview(new HuePreviewRequest
        {
            UserId = "user-custom",
            DeviceId = "tv-1",
            TargetAllEnabledMappings = true
        });
        var broadcastResponse = Assert.IsType<BadRequestObjectResult>(broadcast.Result);
        Assert.Contains("cannot be combined", broadcastResponse.Value?.ToString(), StringComparison.OrdinalIgnoreCase);

        var selected = await controller.Preview(new HuePreviewRequest
        {
            UserId = "user-custom",
            DeviceId = "tv-1",
            TargetRoutes = new List<HueCurrentLightColorTargetRoute>
            {
                new() { UserId = "selected-user", DeviceId = "selected-tv" }
            }
        });
        var selectedResponse = Assert.IsType<BadRequestObjectResult>(selected.Result);
        Assert.Contains("cannot be combined", selectedResponse.Value?.ToString(), StringComparison.OrdinalIgnoreCase);

        streamTester.VerifyNoOtherCalls();
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Preview_DeviceRouteWrongCaseOrBridgeFailsClosedWithoutStoredCredentialFallback()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "global-app-key",
            HueClientKey = "global-client-key",
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-custom",
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "mapping-app-key",
                    HueClientKey = "mapping-client-key",
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new()
                        {
                            DeviceId = "tv-1",
                            HueBridgeIp = "192.168.1.102",
                            HueAppKey = "device-app-key",
                            HueClientKey = "device-client-key"
                        }
                    }
                }
            }
        });
        var streamTester = new Mock<IHueStreamTester>();
        var controller = CreateController(streamTester.Object);

        var wrongCase = await controller.Preview(new HuePreviewRequest
        {
            UserId = "user-custom",
            DeviceId = "TV-1",
            IpAddress = "192.168.1.102",
            EntertainmentAreaId = "device-area"
        });
        Assert.IsType<BadRequestObjectResult>(wrongCase.Result);

        var wrongBridge = await controller.Preview(new HuePreviewRequest
        {
            UserId = "user-custom",
            DeviceId = "tv-1",
            IpAddress = "192.168.1.103",
            EntertainmentAreaId = "device-area"
        });
        Assert.IsType<BadRequestObjectResult>(wrongBridge.Result);

        streamTester.VerifyNoOtherCalls();
        _httpHandlerMock.VerifyNoOtherCalls();
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
    public async Task Preview_SelectedTargetsUsesConfiguredProfilesWithoutDirectCredentials()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "selected-preview-global-app-secret",
            HueClientKey = "selected-preview-global-client-secret",
            EntertainmentAreaId = "global-area",
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-kitchen",
                    UserName = "Kitchen",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "selected-preview-kitchen-app-secret",
                    HueClientKey = "selected-preview-kitchen-client-secret",
                    EntertainmentAreaId = "kitchen-area",
                    ChannelIdsOverride = "1"
                },
                new()
                {
                    UserId = "user-office",
                    UserName = "Office",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.102",
                    HueAppKey = "selected-preview-office-app-secret",
                    HueClientKey = "selected-preview-office-client-secret",
                    EntertainmentAreaId = "office-area"
                }
            }
        });
        SetupHttpResponse(
            HttpStatusCode.OK,
            "{\"data\":[{\"channels\":[{\"channel_id\":0},{\"channel_id\":1}]}]}");
        var streamTester = new Mock<IHueStreamTester>();
        streamTester
            .Setup(tester => tester.PreviewAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<JsonElement>(),
                It.IsAny<IReadOnlySet<int>?>(), 12, 34, 56, 80, 4, It.IsAny<CancellationToken>(), 1, 1,
                PluginConfiguration.ColorPresetEffectPulse, 150))
            .ReturnsAsync(new HueStreamProbeResult { Succeeded = true, Message = "Selected preview completed." });
        var service = new HueSceneAutomationService(
            streamTester.Object,
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(streamTester.Object, hostedServices: new[] { service });

        var action = await controller.Preview(new HuePreviewRequest
        {
            TargetUserIds = new List<string> { " user-kitchen " },
            IncludeDefaultTarget = true,
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
        Assert.True(result.Succeeded);
        Assert.False(result.TargetAllEnabledMappings);
        Assert.Equal(new[] { "user-kitchen" }, result.TargetUserIds);
        Assert.True(result.IncludeDefaultTarget);
        Assert.Equal(new[] { "Default bridge target", "Kitchen" }, result.TargetResults.Select(target => target.TargetLabel));
        Assert.Equal(2, streamTester.Invocations.Count(invocation => invocation.Method.Name == nameof(IHueStreamTester.PreviewAsync)));
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("selected-preview-global-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("selected-preview-global-client-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("selected-preview-kitchen-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("selected-preview-kitchen-client-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("selected-preview-office-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("selected-preview-office-client-secret", serialized, StringComparison.Ordinal);
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
    public async Task PreviewColorPreset_SelectedTargetsIncludesDefaultAndOnlyRequestedMapping()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "selected-global-app-secret",
            HueClientKey = "selected-global-client-secret",
            EntertainmentAreaId = "global-area",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Selected Glow", Red = 40, Green = 80, Blue = 120, DurationSeconds = 1 }
            },
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-kitchen",
                    UserName = "Kitchen",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "selected-kitchen-app-secret",
                    HueClientKey = "selected-kitchen-client-secret",
                    EntertainmentAreaId = "kitchen-area",
                    ChannelIdsOverride = "1"
                },
                new()
                {
                    UserId = "user-office",
                    UserName = "Office",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.102",
                    HueAppKey = "selected-office-app-secret",
                    HueClientKey = "selected-office-client-secret",
                    EntertainmentAreaId = "office-area"
                }
            }
        });
        SetupHttpResponse(
            HttpStatusCode.OK,
            "{\"data\":[{\"channels\":[{\"channel_id\":0},{\"channel_id\":1}]}]}");
        var streamTester = new Mock<IHueStreamTester>();
        streamTester
            .Setup(tester => tester.PreviewAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<JsonElement>(),
                It.IsAny<IReadOnlySet<int>?>(), 40, 80, 120, 100, 1, It.IsAny<CancellationToken>(), 0, 0,
                PluginConfiguration.ColorPresetEffectSolid, 100))
            .ReturnsAsync(new HueStreamProbeResult { Succeeded = true, Message = "Selected scene preview completed." });
        var service = new HueSceneAutomationService(
            streamTester.Object,
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(streamTester.Object, hostedServices: new[] { service });

        var action = await controller.PreviewColorPreset(
            "Selected Glow",
            new HueSavedColorPresetPreviewRequest
            {
                TargetUserIds = new List<string> { " user-kitchen " },
                IncludeDefaultTarget = true
            });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HuePreviewResult>(response.Value);
        Assert.True(result.Succeeded);
        Assert.False(result.TargetAllEnabledMappings);
        Assert.Equal(new[] { "user-kitchen" }, result.TargetUserIds);
        Assert.True(result.IncludeDefaultTarget);
        Assert.Equal(new[] { "Default bridge target", "Kitchen" }, result.TargetResults.Select(target => target.TargetLabel));
        Assert.DoesNotContain(result.TargetResults, target => target.TargetLabel == "Office");
        Assert.Equal(2, streamTester.Invocations.Count(invocation => invocation.Method.Name == nameof(IHueStreamTester.PreviewAsync)));
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("selected-global-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("selected-global-client-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("selected-kitchen-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("selected-kitchen-client-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("selected-office-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("selected-office-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewColorPreset_ExplicitDeviceRouteUsesNestedTargetCredentials()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "device-global-app-secret",
            HueClientKey = "device-global-client-secret",
            EntertainmentAreaId = "global-area",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Device Glow", Red = 40, Green = 80, Blue = 120, DurationSeconds = 1 }
            },
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-bedroom",
                    UserName = "Bedroom",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "device-mapping-app-secret",
                    HueClientKey = "device-mapping-client-secret",
                    EntertainmentAreaId = "mapping-area",
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new()
                        {
                            DeviceId = "device-tv",
                            DeviceName = "Bedroom TV",
                            HueBridgeIp = "192.168.1.102",
                            HueAppKey = "device-route-app-secret",
                            HueClientKey = "device-route-client-secret",
                            EntertainmentAreaId = "device-area",
                            ChannelIdsOverride = "2"
                        }
                    }
                }
            }
        });
        SetupHttpResponse(
            HttpStatusCode.OK,
            "{\"data\":[{\"channels\":[{\"channel_id\":2}]}]}");
        var streamTester = new Mock<IHueStreamTester>();
        streamTester
            .Setup(tester => tester.PreviewAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<JsonElement>(),
                It.IsAny<IReadOnlySet<int>?>(), 40, 80, 120, 100, 1, It.IsAny<CancellationToken>(), 0, 0,
                PluginConfiguration.ColorPresetEffectSolid, 100))
            .ReturnsAsync(new HueStreamProbeResult { Succeeded = true, Message = "Device scene preview completed." });
        var service = new HueSceneAutomationService(
            streamTester.Object,
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(streamTester.Object, hostedServices: new[] { service });

        var action = await controller.PreviewColorPreset(
            "Device Glow",
            new HueSavedColorPresetPreviewRequest
            {
                TargetRoutes = new List<HueCurrentLightColorTargetRoute>
                {
                    new() { UserId = " user-bedroom ", DeviceId = " device-tv " }
                }
            });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HuePreviewResult>(response.Value);
        var target = Assert.Single(result.TargetResults);
        Assert.Equal("Bedroom / Bedroom TV", target.TargetLabel);
        var returnedRoute = Assert.Single(result.TargetRoutes);
        Assert.Equal("user-bedroom", returnedRoute.UserId);
        Assert.Equal("device-tv", returnedRoute.DeviceId);
        streamTester.Verify(tester => tester.PreviewAsync(
            "192.168.1.102",
            "device-route-app-secret",
            "device-route-client-secret",
            "device-area",
            It.IsAny<JsonElement>(),
            It.Is<IReadOnlySet<int>?>(ids => ids != null && ids.SetEquals(new[] { 2 })),
            40,
            80,
            120,
            100,
            1,
            It.IsAny<CancellationToken>(),
            0,
            0,
            PluginConfiguration.ColorPresetEffectSolid,
            100), Times.Once);
    }

    [Fact]
    public async Task PreviewColorPreset_MalformedDeviceRouteFailsClosed()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "malformed-route-app-secret",
            HueClientKey = "malformed-route-client-secret",
            EntertainmentAreaId = "global-area",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Malformed Route Scene", DurationSeconds = 1 }
            }
        });

        var action = await CreateController().PreviewColorPreset(
            "Malformed Route Scene",
            new HueSavedColorPresetPreviewRequest
            {
                TargetRoutes = new List<HueCurrentLightColorTargetRoute>
                {
                    new() { UserId = " ", DeviceId = "device-tv" }
                }
            });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("user mapping ID", response.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Preview_BlankTargetUserIdFailsClosedWithoutContactingBridge()
    {
        var streamTester = new Mock<IHueStreamTester>();

        var action = await CreateController(streamTester.Object).Preview(new HuePreviewRequest
        {
            TargetUserIds = new List<string> { " " }
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("target IDs", response.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        streamTester.VerifyNoOtherCalls();
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Preview_MixedLegacyAndSelectedTargetModesFailClosedWithoutContactingBridge()
    {
        var streamTester = new Mock<IHueStreamTester>();
        var controller = CreateController(streamTester.Object);

        var selected = await controller.Preview(new HuePreviewRequest
        {
            UserId = "legacy-user",
            TargetUserIds = new List<string> { "selected-user" }
        });
        var selectedResponse = Assert.IsType<BadRequestObjectResult>(selected.Result);
        Assert.Contains("cannot combine", selectedResponse.Value?.ToString(), StringComparison.OrdinalIgnoreCase);

        var broadcast = await controller.Preview(new HuePreviewRequest
        {
            UserId = "legacy-user",
            TargetAllEnabledMappings = true
        });
        var broadcastResponse = Assert.IsType<BadRequestObjectResult>(broadcast.Result);
        Assert.Contains("cannot combine", broadcastResponse.Value?.ToString(), StringComparison.OrdinalIgnoreCase);

        streamTester.VerifyNoOtherCalls();
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PreviewColorPreset_BlankTargetUserIdFailsClosedWithoutContactingBridge()
    {
        InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Blank target scene", DurationSeconds = 1 }
            }
        });
        var streamTester = new Mock<IHueStreamTester>();

        var action = await CreateController(streamTester.Object).PreviewColorPreset(
            "Blank target scene",
            new HueSavedColorPresetPreviewRequest
            {
                TargetUserIds = new List<string> { " " }
            });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("target IDs", response.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        streamTester.VerifyNoOtherCalls();
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PreviewColorPresetsBulk_BlankTargetUserIdFailsClosedWithoutContactingBridge()
    {
        InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Bulk blank target scene", DurationSeconds = 1 }
            }
        });
        var streamTester = new Mock<IHueStreamTester>();

        var action = await CreateController(streamTester.Object).PreviewColorPresetsBulk(
            new HueColorPresetBulkPreviewRequest
            {
                PresetNames = new List<string> { "Bulk blank target scene" },
                TargetUserIds = new List<string> { " " }
            });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("target IDs", response.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        streamTester.VerifyNoOtherCalls();
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PreviewScenePlaylist_BlankTargetUserIdFailsClosedWithoutContactingBridge()
    {
        InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Playlist blank target scene", DurationSeconds = 1 }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "playlist-blank-target",
                    Name = "Blank target playlist",
                    PresetNames = new List<string> { "Playlist blank target scene" }
                }
            }
        });
        var streamTester = new Mock<IHueStreamTester>();

        var action = await CreateController(streamTester.Object).PreviewScenePlaylist(
            "Blank target playlist",
            new HueScenePlaylistPreviewRequest
            {
                TargetUserIds = new List<string> { " " }
            });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("target IDs", response.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        streamTester.VerifyNoOtherCalls();
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public void GetScenePlaylists_DoesNotInitializeMissingCollection()
    {
        var configuration = InstallConfiguration(new PluginConfiguration());
        configuration.ScenePlaylists = null!;

        var action = CreateController().GetScenePlaylists();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<HueScenePlaylistResult>>(response.Value));
        Assert.Null(configuration.ScenePlaylists);
    }

    [Fact]
    public async Task PreviewScenePlaylistsBulk_BlankTargetUserIdFailsClosedWithoutContactingBridge()
    {
        InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Bulk playlist blank target scene", DurationSeconds = 1 }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "bulk-playlist-blank-target",
                    Name = "Bulk blank target playlist",
                    PresetNames = new List<string> { "Bulk playlist blank target scene" }
                }
            }
        });
        var streamTester = new Mock<IHueStreamTester>();

        var action = await CreateController(streamTester.Object).PreviewScenePlaylistsBulk(
            new HueScenePlaylistBulkPreviewRequest
            {
                PlaylistIds = new List<string> { "bulk-playlist-blank-target" },
                TargetUserIds = new List<string> { " " }
            });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("target IDs", response.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        streamTester.VerifyNoOtherCalls();
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PreviewColorPresetsBulk_SelectedTargetsAreAppliedToEveryScene()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "bulk-selected-global-app-secret",
            HueClientKey = "bulk-selected-global-client-secret",
            EntertainmentAreaId = "global-area",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Warm", Red = 20, Green = 40, Blue = 60, DurationSeconds = 1 },
                new() { Name = "Cool", Red = 200, Green = 180, Blue = 160, DurationSeconds = 1 }
            },
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-kitchen",
                    UserName = "Kitchen",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "bulk-selected-kitchen-app-secret",
                    HueClientKey = "bulk-selected-kitchen-client-secret",
                    EntertainmentAreaId = "kitchen-area",
                    ChannelIdsOverride = "1"
                },
                new()
                {
                    UserId = "user-office",
                    UserName = "Office",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.102",
                    HueAppKey = "bulk-selected-office-app-secret",
                    HueClientKey = "bulk-selected-office-client-secret",
                    EntertainmentAreaId = "office-area"
                }
            }
        });
        SetupHttpResponse(
            HttpStatusCode.OK,
            "{\"data\":[{\"channels\":[{\"channel_id\":0},{\"channel_id\":1}]}]}");
        var streamTester = new Mock<IHueStreamTester>();
        streamTester
            .Setup(tester => tester.PreviewAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<JsonElement>(),
                It.IsAny<IReadOnlySet<int>?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new HueStreamProbeResult { Succeeded = true, Message = "Bulk selected scene completed." });
        var service = new HueSceneAutomationService(
            streamTester.Object,
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(streamTester.Object, hostedServices: new[] { service });

        var action = await controller.PreviewColorPresetsBulk(new HueColorPresetBulkPreviewRequest
        {
            PresetNames = new List<string> { "Warm", "Cool" },
            TargetUserIds = new List<string> { "user-kitchen" },
            IncludeDefaultTarget = true
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueColorPresetBulkPreviewResult>(response.Value);
        Assert.Equal(2, result.CompletedCount);
        Assert.Equal(2, result.SucceededCount);
        Assert.All(result.Previews, preview =>
        {
            Assert.Equal(new[] { "user-kitchen" }, preview.Preview.TargetUserIds);
            Assert.True(preview.Preview.IncludeDefaultTarget);
            Assert.Equal(new[] { "Default bridge target", "Kitchen" }, preview.Preview.TargetResults.Select(target => target.TargetLabel));
        });
        Assert.Equal(4, streamTester.Invocations.Count(invocation => invocation.Method.Name == nameof(IHueStreamTester.PreviewAsync)));
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
    public async Task PreviewColorPresetsBulk_PreflightsAndRunsSelectionsSequentiallyWithoutCredentials()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "bulk-scene-app-secret",
            HueClientKey = "bulk-scene-client-secret",
            EntertainmentAreaId = "global-area",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Warm", Red = 20, Green = 40, Blue = 60, DurationSeconds = 1 },
                new() { Name = "Cool", Red = 200, Green = 180, Blue = 160, DurationSeconds = 1 }
            }
        });
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
            .ReturnsAsync(new HueStreamProbeResult { Succeeded = true, Message = "Bulk scene completed." });
        var service = new HueSceneAutomationService(
            streamTester.Object,
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(streamTester.Object, hostedServices: new[] { service });

        var action = await controller.PreviewColorPresetsBulk(new HueColorPresetBulkPreviewRequest
        {
            PresetNames = new List<string> { " cool ", "Warm", "COOL" }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueColorPresetBulkPreviewResult>(response.Value);
        Assert.Equal(2, result.RequestedCount);
        Assert.Equal(2, result.CompletedCount);
        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);
        Assert.False(result.Canceled);
        Assert.Equal(new[] { "Cool", "Warm" }, result.Previews.Select(preview => preview.Name));
        Assert.All(result.Previews, preview => Assert.True(preview.Preview.Succeeded));
        Assert.Equal(new[] { 200, 20 }, streamTester.Invocations
            .Where(invocation => invocation.Method.Name == nameof(IHueStreamTester.PreviewAsync))
            .Select(invocation => (int)invocation.Arguments[6]!)
            .ToArray());
        Assert.Equal(2, streamTester.Invocations.Count(invocation => invocation.Method.Name == nameof(IHueStreamTester.PreviewAsync)));
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("bulk-scene-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("bulk-scene-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewColorPresetsBulk_MissingOrInvalidSelectionDoesNotContactBridge()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "bulk-scene-app-secret",
            HueClientKey = "bulk-scene-client-secret",
            EntertainmentAreaId = "global-area",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Valid", DurationSeconds = 1 },
                new() { Name = "Invalid", Effect = "Unknown", DurationSeconds = 1 }
            }
        });
        var controller = CreateController(Mock.Of<IHueStreamTester>());

        var missing = await controller.PreviewColorPresetsBulk(new HueColorPresetBulkPreviewRequest
        {
            PresetNames = new List<string> { "Valid", "Missing" }
        });
        var missingResponse = Assert.IsType<NotFoundObjectResult>(missing.Result);
        var missingResult = Assert.IsType<HueColorPresetBulkPreviewResult>(missingResponse.Value);
        Assert.Equal(new[] { "Missing" }, missingResult.MissingNames);

        var invalid = await controller.PreviewColorPresetsBulk(new HueColorPresetBulkPreviewRequest
        {
            PresetNames = new List<string> { "Invalid" }
        });
        var invalidResponse = Assert.IsType<BadRequestObjectResult>(invalid.Result);
        var invalidResult = Assert.IsType<HueColorPresetBulkPreviewResult>(invalidResponse.Value);
        Assert.NotEmpty(invalidResult.ValidationErrors);
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
                new() { Name = "Warm", Effect = PluginConfiguration.ColorPresetEffectPulse, Red = 25, Green = 50, Blue = 75, BrightnessPercent = 75, DurationSeconds = 1 },
                new() { Name = "Cool", Effect = PluginConfiguration.ColorPresetEffectRainbow, Red = 220, Green = 180, Blue = 140, BrightnessPercent = 60, DurationSeconds = 2 }
            }
        });
        var controller = CreateController();

        var saved = controller.SaveScenePlaylist(new HueScenePlaylistRequest
        {
            Name = " Evening sequence ",
            PresetNames = new List<string> { "Warm", "Cool" },
            StepDurationSeconds = new List<int> { 3, 0 },
            StepRed = new List<int?> { 200, null },
            StepGreen = new List<int?> { 100, null },
            StepBlue = new List<int?> { 50, null },
            StepBrightnessPercent = new List<int?> { 40, null },
            StepEffects = new List<string?> { " starlight ", null },
            StepEffectSpeedPercent = new List<int?> { 225, null },
            StepTransitionSeconds = new List<int?> { 1, null },
            StepTransitionOutSeconds = new List<int?> { null, 1 },
            StepTransitionCurves = new List<string?> { "EaseInOut", null },
            RepeatCount = 2
        });
        var savedResponse = Assert.IsType<OkObjectResult>(saved.Result);
        var savedResult = Assert.IsType<HueScenePlaylistResult>(savedResponse.Value);
        Assert.Equal("Evening sequence", savedResult.Name);
        Assert.Equal(2, savedResult.RepeatCount);
        Assert.Equal(10, savedResult.TotalDurationSeconds);
        Assert.Equal(new[] { 3, 0 }, savedResult.StepDurationSeconds);
        Assert.Equal(new int?[] { 200, null }, savedResult.StepRed);
        Assert.Equal(new int?[] { 100, null }, savedResult.StepGreen);
        Assert.Equal(new int?[] { 50, null }, savedResult.StepBlue);
        Assert.Equal(new int?[] { 40, null }, savedResult.StepBrightnessPercent);
        Assert.Equal(new[] { PluginConfiguration.ColorPresetEffectStarlight, null }, savedResult.StepEffects);
        Assert.Equal(new int?[] { 225, null }, savedResult.StepEffectSpeedPercent);
        Assert.Equal(new int?[] { 1, null }, savedResult.StepTransitionSeconds);
        Assert.Equal(new int?[] { null, 1 }, savedResult.StepTransitionOutSeconds);
        Assert.Equal(new[] { "EaseInOut", null }, savedResult.StepTransitionCurves);
        Assert.Equal("Default bridge target", savedResult.TargetLabel);
        Assert.DoesNotContain("playlist-global-app-secret", JsonSerializer.Serialize(savedResult), StringComparison.Ordinal);

        var listedResponse = Assert.IsType<OkObjectResult>(controller.GetScenePlaylists().Result);
        var listed = Assert.IsAssignableFrom<IEnumerable<HueScenePlaylistResult>>(listedResponse.Value).ToArray();
        Assert.Single(listed);
        Assert.Equal(new[] { "Warm", "Cool" }, listed[0].PresetNames);
        Assert.Equal(2, listed[0].RepeatCount);
        Assert.Equal(new int?[] { 200, null }, listed[0].StepRed);
        Assert.Equal(new int?[] { 100, null }, listed[0].StepGreen);
        Assert.Equal(new int?[] { 50, null }, listed[0].StepBlue);
        Assert.Equal(new int?[] { 40, null }, listed[0].StepBrightnessPercent);
        Assert.Equal(new[] { PluginConfiguration.ColorPresetEffectStarlight, null }, listed[0].StepEffects);
        Assert.Equal(new int?[] { 225, null }, listed[0].StepEffectSpeedPercent);
        Assert.Equal(new int?[] { 1, null }, listed[0].StepTransitionSeconds);
        Assert.Equal(new int?[] { null, 1 }, listed[0].StepTransitionOutSeconds);
        Assert.Equal(new[] { "EaseInOut", null }, listed[0].StepTransitionCurves);

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
        Assert.Equal(2, result.RepeatCount);
        Assert.Equal(new[] { "Warm", "Cool", "Warm", "Cool" }, result.Steps.Select(step => step.PresetName));
        Assert.Equal(
            new[]
            {
                PluginConfiguration.ColorPresetEffectStarlight,
                PluginConfiguration.ColorPresetEffectRainbow,
                PluginConfiguration.ColorPresetEffectStarlight,
                PluginConfiguration.ColorPresetEffectRainbow
            },
            result.Steps.Select(step => step.Effect));
        Assert.Equal(new[] { "EaseInOut", "Linear", "EaseInOut", "Linear" }, result.Steps.Select(step => step.TransitionCurve));
        Assert.Equal(new[] { 200, 220, 200, 220 }, streamTester.Invocations
            .Where(invocation => invocation.Method.Name == nameof(IHueStreamTester.PreviewAsync))
            .Select(invocation => (int)invocation.Arguments[6]!)
            .ToArray());
        Assert.Equal(new[] { 40, 60, 40, 60 }, streamTester.Invocations
            .Where(invocation => invocation.Method.Name == nameof(IHueStreamTester.PreviewAsync))
            .Select(invocation => (int)invocation.Arguments[9]!)
            .ToArray());
        Assert.Equal(new[] { 3, 2, 3, 2 }, streamTester.Invocations
            .Where(invocation => invocation.Method.Name == nameof(IHueStreamTester.PreviewAsync))
            .Select(invocation => (int)invocation.Arguments[10]!)
            .ToArray());
        Assert.Equal(new[] { 1, 0, 1, 0 }, streamTester.Invocations
            .Where(invocation => invocation.Method.Name == nameof(IHueStreamTester.PreviewAsync))
            .Select(invocation => (int)invocation.Arguments[12]!)
            .ToArray());
        Assert.Equal(new[] { 225, PluginConfiguration.DefaultColorPresetEffectSpeedPercent, 225, PluginConfiguration.DefaultColorPresetEffectSpeedPercent }, streamTester.Invocations
            .Where(invocation => invocation.Method.Name == nameof(IHueStreamTester.PreviewAsync))
            .Select(invocation => (int)invocation.Arguments[15]!)
            .ToArray());
        Assert.Equal(
            new[]
            {
                PluginConfiguration.ColorPresetEffectStarlight,
                PluginConfiguration.ColorPresetEffectRainbow,
                PluginConfiguration.ColorPresetEffectStarlight,
                PluginConfiguration.ColorPresetEffectRainbow
            },
            streamTester.Invocations
                .Where(invocation => invocation.Method.Name == nameof(IHueStreamTester.PreviewAsync))
                .Select(invocation => (string)invocation.Arguments[14]!)
                .ToArray());
        Assert.Equal(new[] { 0, 1, 0, 1 }, streamTester.Invocations
            .Where(invocation => invocation.Method.Name == nameof(IHueStreamTester.PreviewAsync))
            .Select(invocation => (int)invocation.Arguments[13]!)
            .ToArray());
        var target = Assert.Single(result.TargetResults);
        Assert.True(target.Succeeded);
        Assert.Equal(4, target.CompletedStepCount);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("playlist-global-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("playlist-global-client-secret", serialized, StringComparison.Ordinal);

        var duplicate = controller.DuplicateScenePlaylist("evening sequence");
        var duplicateResponse = Assert.IsType<OkObjectResult>(duplicate.Result);
        var duplicateResult = Assert.IsType<HueScenePlaylistResult>(duplicateResponse.Value);
        Assert.Equal("Evening sequence (Copy)", duplicateResult.Name);
        Assert.Equal(new[] { 3, 0 }, duplicateResult.StepDurationSeconds);
        Assert.Equal(new int?[] { 200, null }, duplicateResult.StepRed);
        Assert.Equal(new int?[] { 100, null }, duplicateResult.StepGreen);
        Assert.Equal(new int?[] { 50, null }, duplicateResult.StepBlue);
        Assert.Equal(new int?[] { 40, null }, duplicateResult.StepBrightnessPercent);
        Assert.Equal(new[] { PluginConfiguration.ColorPresetEffectStarlight, null }, duplicateResult.StepEffects);
        Assert.Equal(new int?[] { 225, null }, duplicateResult.StepEffectSpeedPercent);
        Assert.Equal(new int?[] { 1, null }, duplicateResult.StepTransitionSeconds);
        Assert.Equal(new int?[] { null, 1 }, duplicateResult.StepTransitionOutSeconds);
        Assert.Equal(new[] { "EaseInOut", null }, duplicateResult.StepTransitionCurves);
        Assert.Equal(2, configuration.ScenePlaylists.Count);

        var deleted = controller.DeleteScenePlaylist(duplicateResult.Name);
        Assert.IsType<OkObjectResult>(deleted);
        Assert.Single(configuration.ScenePlaylists);
    }

    [Fact]
    public void ScenePlaylists_UpdateOmittingStepEffectsPreservesExistingOverridesAndExplicitEmptyClearsThem()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Warm" },
                new() { Name = "Cool", EffectSpeedPercent = 275 }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "playlist-speed-update",
                    Name = "Speed update",
                    PresetNames = new List<string> { "Warm", "Cool" },
                    StepRed = new List<int?> { 180, null },
                    StepGreen = new List<int?> { 90, null },
                    StepBlue = new List<int?> { 45, null },
                    StepEffects = new List<string?> { PluginConfiguration.ColorPresetEffectStarlight, null },
                    StepEffectSpeedPercent = new List<int?> { 225, null }
                }
            }
        });

        var action = CreateController().SaveScenePlaylist(new HueScenePlaylistRequest
        {
            Id = "playlist-speed-update",
            Name = "Speed update renamed",
            PresetNames = new List<string> { "Warm", "Cool" }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueScenePlaylistResult>(response.Value);
        Assert.Equal("Speed update renamed", result.Name);
        Assert.Equal(new int?[] { 180, null }, result.StepRed);
        Assert.Equal(new int?[] { 90, null }, result.StepGreen);
        Assert.Equal(new int?[] { 45, null }, result.StepBlue);
        Assert.Equal(new[] { PluginConfiguration.ColorPresetEffectStarlight, null }, result.StepEffects);
        Assert.Equal(new int?[] { 225, null }, result.StepEffectSpeedPercent);
        var persisted = Assert.Single(configuration.ScenePlaylists);
        Assert.Equal(new int?[] { 180, null }, persisted.StepRed);
        Assert.Equal(new int?[] { 90, null }, persisted.StepGreen);
        Assert.Equal(new int?[] { 45, null }, persisted.StepBlue);
        Assert.Equal(new[] { PluginConfiguration.ColorPresetEffectStarlight, null }, persisted.StepEffects);
        Assert.Equal(new int?[] { 225, null }, persisted.StepEffectSpeedPercent);

        var clearAction = CreateController().SaveScenePlaylist(new HueScenePlaylistRequest
        {
            Id = "playlist-speed-update",
            Name = "Speed update renamed",
            PresetNames = new List<string> { "Warm", "Cool" },
            StepRed = new List<int?>(),
            StepGreen = new List<int?>(),
            StepBlue = new List<int?>(),
            StepEffects = new List<string?>()
        });

        var clearResponse = Assert.IsType<OkObjectResult>(clearAction.Result);
        var cleared = Assert.IsType<HueScenePlaylistResult>(clearResponse.Value);
        Assert.Empty(cleared.StepRed);
        Assert.Empty(cleared.StepGreen);
        Assert.Empty(cleared.StepBlue);
        Assert.Empty(cleared.StepEffects);
        Assert.Equal(new int?[] { 225, null }, cleared.StepEffectSpeedPercent);
        Assert.Empty(Assert.Single(configuration.ScenePlaylists).StepEffects);
    }

    [Fact]
    public void ScenePlaylists_ShufflePlaybackOrderRoundTripsThroughApiResults()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "shuffle-global-app-secret",
            HueClientKey = "shuffle-global-client-secret",
            EntertainmentAreaId = "global-area",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "One", DurationSeconds = 1 },
                new() { Name = "Two", DurationSeconds = 1 },
                new() { Name = "Three", DurationSeconds = 1 }
            }
        });
        var controller = CreateController();

        var saved = controller.SaveScenePlaylist(new HueScenePlaylistRequest
        {
            Name = " Stable shuffle ",
            PresetNames = new List<string> { "One", "Two", "Three" },
            PlaybackOrder = "shuffle"
        });

        var savedResponse = Assert.IsType<OkObjectResult>(saved.Result);
        var savedResult = Assert.IsType<HueScenePlaylistResult>(savedResponse.Value);
        Assert.Equal(PluginConfiguration.ScenePlaylistOrderShuffle, savedResult.PlaybackOrder);
        Assert.Equal(PluginConfiguration.ScenePlaylistOrderShuffle, configuration.ScenePlaylists[0].PlaybackOrder);

        var listedResponse = Assert.IsType<OkObjectResult>(controller.GetScenePlaylists().Result);
        var listedResult = Assert.Single(Assert.IsAssignableFrom<IEnumerable<HueScenePlaylistResult>>(listedResponse.Value));
        Assert.Equal(PluginConfiguration.ScenePlaylistOrderShuffle, listedResult.PlaybackOrder);

        var serialized = JsonSerializer.Serialize(listedResult);
        Assert.Contains("\"playbackOrder\":\"Shuffle\"", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("shuffle-global-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("shuffle-global-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScenePlaylists_SelectedTargetsRoundTripAndPreviewOnlyRequestedMappings()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "playlist-global-app-secret",
            HueClientKey = "playlist-global-client-secret",
            EntertainmentAreaId = "global-area",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Welcome", DurationSeconds = 1 }
            },
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-1",
                    UserName = "Kitchen",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "kitchen-app-secret",
                    HueClientKey = "kitchen-client-secret",
                    EntertainmentAreaId = "kitchen-area"
                },
                new()
                {
                    UserId = "user-2",
                    UserName = "Office",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.102",
                    HueAppKey = "office-app-secret",
                    HueClientKey = "office-client-secret",
                    EntertainmentAreaId = "office-area"
                }
            }
        });
        var saved = CreateController().SaveScenePlaylist(new HueScenePlaylistRequest
        {
            Name = "Selected sequence",
            PresetNames = new List<string> { "Welcome" },
            TargetUserIds = new List<string> { " user-1 " },
            IncludeDefaultTarget = true
        });

        var savedResponse = Assert.IsType<OkObjectResult>(saved.Result);
        var savedResult = Assert.IsType<HueScenePlaylistResult>(savedResponse.Value);
        Assert.Equal(new[] { "user-1" }, savedResult.TargetUserIds);
        Assert.True(savedResult.IncludeDefaultTarget);
        Assert.Equal("Default bridge + 1 selected target(s)", savedResult.TargetLabel);
        Assert.Equal(string.Empty, savedResult.TargetUserId);
        Assert.Equal(new[] { "user-1" }, configuration.ScenePlaylists[0].TargetUserIds);

        var listed = Assert.IsType<OkObjectResult>(CreateController().GetScenePlaylists().Result);
        var listedPlaylist = Assert.Single(Assert.IsAssignableFrom<IEnumerable<HueScenePlaylistResult>>(listed.Value));
        Assert.Equal(new[] { "user-1" }, listedPlaylist.TargetUserIds);
        Assert.True(listedPlaylist.IncludeDefaultTarget);

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
            .ReturnsAsync(new HueStreamProbeResult { Succeeded = true, Message = "Playlist step completed." });
        var service = new HueSceneAutomationService(
            streamTester.Object,
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var preview = await CreateController(streamTester.Object, hostedServices: new[] { service })
            .PreviewScenePlaylist("Selected sequence", new HueScenePlaylistPreviewRequest
            {
                TargetUserIds = new List<string>()
            });

        var previewResponse = Assert.IsType<OkObjectResult>(preview.Result);
        var result = Assert.IsType<HueScenePlaylistRunResult>(previewResponse.Value);
        Assert.True(result.Succeeded);
        Assert.Equal("Default bridge + 1 selected target(s)", result.TargetLabel);
        Assert.Equal(new[] { "user-1" }, result.TargetUserIds);
        Assert.True(result.IncludeDefaultTarget);
        Assert.Equal(new[] { "Default bridge target", "Kitchen" }, result.TargetResults.Select(target => target.TargetLabel));
        Assert.DoesNotContain(result.TargetResults, target => target.TargetLabel == "Office");
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("playlist-global-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("kitchen-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("office-app-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void ScenePlaylistRename_MigratesCueReferencesAndReferencedDeleteIsRejected()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Warm", DurationSeconds = 2 }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "playlist-renamed",
                    Name = "Old sequence",
                    PresetNames = new List<string> { "Warm" },
                    RepeatCount = 2
                }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "playlist-cue",
                    Name = "Playlist cue",
                    PlaylistName = "Old sequence",
                    TimeZoneId = TimeZoneInfo.Utc.Id
                }
            }
        });
        var controller = CreateController();

        var renamed = controller.SaveScenePlaylist(new HueScenePlaylistRequest
        {
            Id = "playlist-renamed",
            Name = "New sequence",
            PresetNames = new List<string> { "Warm" },
            RepeatCount = 2
        });

        var renamedResponse = Assert.IsType<OkObjectResult>(renamed.Result);
        var renamedResult = Assert.IsType<HueScenePlaylistResult>(renamedResponse.Value);
        Assert.Equal("New sequence", renamedResult.Name);
        Assert.Equal("New sequence", Assert.Single(configuration.SceneSchedules).PlaylistName);

        var listedSchedules = Assert.IsType<OkObjectResult>(controller.GetSceneSchedules().Result);
        var scheduleResult = Assert.Single(
            Assert.IsAssignableFrom<IEnumerable<HueSceneScheduleResult>>(listedSchedules.Value));
        Assert.Equal("New sequence", scheduleResult.PlaylistName);
        Assert.Equal(2, scheduleResult.PlaylistRepeatCount);

        var blockedDelete = controller.DeleteScenePlaylist("new sequence");
        var conflict = Assert.IsType<ConflictObjectResult>(blockedDelete);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Single(configuration.ScenePlaylists);

        Assert.IsType<OkObjectResult>(controller.DeleteSceneSchedule("playlist-cue"));
        Assert.IsType<OkObjectResult>(controller.DeleteScenePlaylist("new sequence"));
        Assert.Empty(configuration.ScenePlaylists);
    }

    [Fact]
    public async Task DeleteSceneSchedule_RefusesActiveCueAndPreservesConfiguration()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "active-delete-app-key",
            HueClientKey = "active-delete-client-key",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Active delete scene", DurationSeconds = 8 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "active-delete-cue",
                    Name = "Active delete cue",
                    PresetName = "Active delete scene",
                    MaxRuns = 3
                }
            }
        });
        SetupHttpResponse(
            HttpStatusCode.OK,
            "{\"data\":[{\"channels\":[{\"channel_id\":0}]}]}");
        var streamTester = new BlockingPreviewStreamTester();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(hostedServices: new[] { service });
        var runTask = service.RunScheduleAsync("active-delete-cue");

        await streamTester.PreviewStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var action = controller.DeleteSceneSchedule("active-delete-cue");

            var response = Assert.IsType<ConflictObjectResult>(action);
            Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
            Assert.Contains("currently running", Assert.IsType<string>(response.Value), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("active-delete-cue", Assert.Single(configuration.SceneSchedules).Id);
        }
        finally
        {
            streamTester.ReleasePreview.TrySetResult(true);
            await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task SaveSceneSchedule_RefusesActiveCueUpdateAndPreservesConfiguration()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "active-update-app-key",
            HueClientKey = "active-update-client-key",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Active update scene", DurationSeconds = 8 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "active-update-cue",
                    Name = "Active update cue",
                    PresetName = "Active update scene",
                    MaxRuns = 3
                }
            }
        });
        SetupHttpResponse(
            HttpStatusCode.OK,
            "{\"data\":[{\"channels\":[{\"channel_id\":0}]}]}");
        var streamTester = new BlockingPreviewStreamTester();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(hostedServices: new[] { service });
        var runTask = service.RunScheduleAsync("active-update-cue");

        await streamTester.PreviewStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var action = controller.SaveSceneSchedule(new HueSceneScheduleRequest
            {
                Id = "active-update-cue",
                Name = "Changed while active",
                PresetName = "Active update scene",
                TimeOfDay = "07:30",
                Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                DaysOfWeekMask = 0,
                Enabled = false
            });

            var response = Assert.IsType<ConflictObjectResult>(action.Result);
            Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
            Assert.Contains("running", Assert.IsType<string>(response.Value), StringComparison.OrdinalIgnoreCase);

            var unchanged = Assert.Single(configuration.SceneSchedules);
            Assert.Equal("active-update-cue", unchanged.Id);
            Assert.Equal("Active update cue", unchanged.Name);
            Assert.Equal("Active update scene", unchanged.PresetName);
            Assert.True(unchanged.Enabled);
            Assert.Equal(3, unchanged.MaxRuns);
        }
        finally
        {
            streamTester.ReleasePreview.TrySetResult(true);
            await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void RenameScenePlaylist_MigratesCueReferencesAndReturnsCredentialFreeMetadata()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            HueAppKey = "playlist-rename-app-secret",
            HueClientKey = "playlist-rename-client-secret",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Accent", DurationSeconds = 2 }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "rename-playlist",
                    Name = "Original sequence",
                    PresetNames = new List<string> { "Accent" },
                    StepRed = new List<int?> { 210 },
                    StepGreen = new List<int?> { 120 },
                    StepBlue = new List<int?> { 30 },
                    RepeatCount = 2
                },
                new() { Id = "other-playlist", Name = "Existing sequence", PresetNames = new List<string> { "Accent" } }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "rename-cue", Name = "Renamed cue", PlaylistName = " original sequence " }
            }
        });
        var controller = CreateController();

        var renamed = controller.RenameScenePlaylist(
            " ORIGINAL SEQUENCE ",
            new HueScenePlaylistRenameRequest { NewName = " New sequence " });

        var response = Assert.IsType<OkObjectResult>(renamed.Result);
        var result = Assert.IsType<HueScenePlaylistResult>(response.Value);
        Assert.Equal("New sequence", result.Name);
        Assert.Equal(new int?[] { 210 }, result.StepRed);
        Assert.Equal(new int?[] { 120 }, result.StepGreen);
        Assert.Equal(new int?[] { 30 }, result.StepBlue);
        Assert.Equal(2, result.RepeatCount);
        Assert.Equal("New sequence", Assert.Single(configuration.SceneSchedules).PlaylistName);
        Assert.DoesNotContain("playlist-rename-app-secret", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.DoesNotContain("playlist-rename-client-secret", JsonSerializer.Serialize(result), StringComparison.Ordinal);

        var blank = controller.RenameScenePlaylist(
            "New sequence",
            new HueScenePlaylistRenameRequest { NewName = "  " });
        Assert.IsType<BadRequestObjectResult>(blank.Result);

        var collision = controller.RenameScenePlaylist(
            "New sequence",
            new HueScenePlaylistRenameRequest { NewName = " existing sequence " });
        Assert.IsType<ConflictObjectResult>(collision.Result);
        Assert.Equal("New sequence", configuration.ScenePlaylists[0].Name);
        Assert.Equal("New sequence", configuration.SceneSchedules[0].PlaylistName);
    }

    [Fact]
    public void GetScenePlaylistDependencies_ReturnsCredentialFreeCueDetails()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            HueAppKey = "playlist-dependency-app-secret",
            HueClientKey = "playlist-dependency-client-secret",
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "playlist-dependency-id",
                    Name = "Accent sequence",
                    PresetNames = new List<string> { "Warm" }
                }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "cue-disabled",
                    Name = "Disabled cue",
                    PlaylistName = " accent sequence ",
                    Enabled = false
                },
                new()
                {
                    Id = "cue-enabled",
                    Name = "Enabled cue",
                    PlaylistName = "Accent sequence",
                    Enabled = true
                },
                new()
                {
                    Id = "cue-other",
                    Name = "Other playlist cue",
                    PlaylistName = "Different sequence",
                    Enabled = true
                }
            }
        });
        var controller = CreateController();

        var action = controller.GetScenePlaylistDependencies(" ACCENT SEQUENCE ");

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueScenePlaylistDependenciesResult>(response.Value);
        Assert.Equal("playlist-dependency-id", result.Id);
        Assert.Equal("Accent sequence", result.Name);
        Assert.False(result.CanDelete);
        Assert.Equal(2, result.ScheduledCueCount);
        Assert.Equal(new[] { "Disabled cue", "Enabled cue" }, result.ScheduledCues.Select(cue => cue.Name));
        Assert.False(result.ScheduledCues[0].Enabled);
        Assert.True(result.ScheduledCues[1].Enabled);
        Assert.Equal(new[] { "cue-disabled", "cue-enabled" }, result.ScheduledCues.Select(cue => cue.Id));

        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("playlist-dependency-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("playlist-dependency-client-secret", serialized, StringComparison.Ordinal);

        configuration.SceneSchedules.Clear();
        var noReferences = controller.GetScenePlaylistDependencies("accent sequence");
        var noReferencesResponse = Assert.IsType<OkObjectResult>(noReferences.Result);
        var noReferencesResult = Assert.IsType<HueScenePlaylistDependenciesResult>(noReferencesResponse.Value);
        Assert.True(noReferencesResult.CanDelete);
        Assert.Equal(0, noReferencesResult.ScheduledCueCount);
        Assert.Empty(noReferencesResult.ScheduledCues);
    }

    [Fact]
    public void ScenePlaylists_BulkDeleteRemovesSelectedPlaylistsById()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            HueAppKey = "bulk-playlist-app-secret",
            HueClientKey = "bulk-playlist-client-secret",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Warm" }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new() { Id = "playlist-one", Name = "One", PresetNames = new List<string> { "Warm" } },
                new() { Id = "playlist-two", Name = "Two", PresetNames = new List<string> { "Warm" } },
                new() { Id = "playlist-keep", Name = "Keep", PresetNames = new List<string> { "Warm" } }
            }
        });

        var action = CreateController().DeleteScenePlaylistsBulk(new HueScenePlaylistBulkDeleteRequest
        {
            PlaylistIds = new List<string> { " playlist-one ", "PLAYLIST-TWO" }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueScenePlaylistBulkDeleteResult>(response.Value);
        Assert.Equal(2, result.RequestedCount);
        Assert.Equal(2, result.DeletedCount);
        Assert.Equal(1, result.RemainingCount);
        Assert.Equal(new[] { "One", "Two" }, result.Playlists.Select(playlist => playlist.Name));
        Assert.Equal("Keep", Assert.Single(configuration.ScenePlaylists).Name);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("bulk-playlist-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("bulk-playlist-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void ScenePlaylists_BulkDeleteRefusesDependenciesAndMissingIdsAtomically()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Warm" }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new() { Id = "playlist-dependent", Name = "Dependent", PresetNames = new List<string> { "Warm" } },
                new() { Id = "playlist-free", Name = "Free", PresetNames = new List<string> { "Warm" } }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "playlist-reference", Name = "Uses dependent", PlaylistName = " dependent " }
            }
        });
        var controller = CreateController();

        var blocked = controller.DeleteScenePlaylistsBulk(new HueScenePlaylistBulkDeleteRequest
        {
            PlaylistIds = new List<string> { "playlist-dependent", "playlist-free" }
        });

        var blockedResponse = Assert.IsType<ConflictObjectResult>(blocked.Result);
        var blockedResult = Assert.IsType<HueScenePlaylistBulkDeleteResult>(blockedResponse.Value);
        Assert.Equal(2, blockedResult.RequestedCount);
        var dependency = Assert.Single(blockedResult.BlockedPlaylists);
        Assert.Equal("playlist-dependent", dependency.Id);
        Assert.Equal(1, dependency.ScheduledCueCount);
        Assert.Equal(2, configuration.ScenePlaylists.Count);

        var missing = controller.DeleteScenePlaylistsBulk(new HueScenePlaylistBulkDeleteRequest
        {
            PlaylistIds = new List<string> { "playlist-free", "missing-playlist" }
        });

        var missingResponse = Assert.IsType<NotFoundObjectResult>(missing.Result);
        var missingResult = Assert.IsType<HueScenePlaylistBulkDeleteResult>(missingResponse.Value);
        Assert.Equal(2, missingResult.RequestedCount);
        Assert.Equal(2, configuration.ScenePlaylists.Count);
    }

    [Fact]
    public void ScenePlaylists_BulkDuplicateCreatesFreshIndependentCopiesAtomically()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            HueAppKey = "bulk-duplicate-playlist-app-secret",
            HueClientKey = "bulk-duplicate-playlist-client-secret",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Warm", DurationSeconds = 2 },
                new() { Name = "Cool", DurationSeconds = 3 }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new() { Id = "playlist-one", Name = "Evening", PresetNames = new List<string> { "Warm", "Cool" }, RepeatCount = 2 },
                new() { Id = "playlist-two", Name = "Morning", PresetNames = new List<string> { "Cool" }, TargetAllEnabledMappings = true },
                new() { Id = "playlist-keep", Name = "Keep", PresetNames = new List<string> { "Warm" } }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "playlist-copy-cue", Name = "Original playlist cue", PlaylistName = "Evening" }
            }
        });

        var action = CreateController().DuplicateScenePlaylistsBulk(new HueScenePlaylistBulkDuplicateRequest
        {
            PlaylistIds = new List<string> { " playlist-one ", "PLAYLIST-TWO", "playlist-one" }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueScenePlaylistBulkDuplicateResult>(response.Value);
        Assert.Equal(2, result.RequestedCount);
        Assert.Equal(2, result.DuplicatedCount);
        Assert.Equal(5, result.RemainingCount);
        Assert.Equal(new[] { "Evening (Copy)", "Morning (Copy)" }, result.Playlists.Select(playlist => playlist.Name));
        Assert.All(result.Playlists, playlist => Assert.NotEqual("", playlist.Id));
        Assert.Equal(2, result.Playlists[0].RepeatCount);
        Assert.True(result.Playlists[1].TargetAllEnabledMappings);
        Assert.Equal(new[] { "Evening", "Morning", "Keep", "Evening (Copy)", "Morning (Copy)" },
            configuration.ScenePlaylists.Select(playlist => playlist.Name));
        Assert.Equal(2, configuration.ScenePlaylists[3].PresetNames.Count);
        Assert.Single(configuration.ScenePlaylists[4].PresetNames);
        Assert.Equal("Evening", Assert.Single(configuration.SceneSchedules).PlaylistName);
        Assert.Equal(5, configuration.ScenePlaylists.Select(playlist => playlist.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("bulk-duplicate-playlist-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("bulk-duplicate-playlist-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void ScenePlaylists_BulkDuplicateRefusesMissingIdsAndCapacityAtomically()
    {
        var missingConfiguration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Warm" } },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new() { Id = "playlist-existing", Name = "Existing", PresetNames = new List<string> { "Warm" } }
            }
        });
        var controller = CreateController();

        var missing = controller.DuplicateScenePlaylistsBulk(new HueScenePlaylistBulkDuplicateRequest
        {
            PlaylistIds = new List<string> { "playlist-existing", "missing-playlist" }
        });

        var missingResponse = Assert.IsType<NotFoundObjectResult>(missing.Result);
        var missingResult = Assert.IsType<HueScenePlaylistBulkDuplicateResult>(missingResponse.Value);
        Assert.Equal(2, missingResult.RequestedCount);
        Assert.Equal(new[] { "missing-playlist" }, missingResult.MissingIds);
        Assert.Single(missingConfiguration.ScenePlaylists);

        var capacityConfiguration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Warm" } },
            ScenePlaylists = Enumerable.Range(0, PluginConfiguration.MaxScenePlaylists - 1)
                .Select(index => new HueScenePlaylist
                {
                    Id = $"playlist-{index}",
                    Name = $"Playlist {index}",
                    PresetNames = new List<string> { "Warm" }
                })
                .ToList()
        });
        var capacity = CreateController().DuplicateScenePlaylistsBulk(new HueScenePlaylistBulkDuplicateRequest
        {
            PlaylistIds = new List<string> { "playlist-0", "playlist-1" }
        });

        var capacityResponse = Assert.IsType<ConflictObjectResult>(capacity.Result);
        var capacityResult = Assert.IsType<HueScenePlaylistBulkDuplicateResult>(capacityResponse.Value);
        Assert.Equal(2, capacityResult.RequestedCount);
        Assert.Equal(1, capacityResult.AvailableCapacity);
        Assert.Equal(PluginConfiguration.MaxScenePlaylists - 1, capacityConfiguration.ScenePlaylists.Count);
    }

    [Fact]
    public void ScenePlaylists_BulkDuplicatePersistenceFailureRestoresCollection()
    {
        var serializer = new Mock<IXmlSerializer>();
        serializer
            .Setup(xml => xml.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("bulk playlist duplication failed"));
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Warm" } },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new() { Id = "playlist-source", Name = "Source", PresetNames = new List<string> { "Warm" } },
                new() { Id = "playlist-keep", Name = "Keep", PresetNames = new List<string> { "Warm" } }
            }
        }, serializer.Object);
        var previousPlaylists = configuration.ScenePlaylists;

        var action = CreateController().DuplicateScenePlaylistsBulk(new HueScenePlaylistBulkDuplicateRequest
        {
            PlaylistIds = new List<string> { "playlist-source" }
        });

        var response = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, response.StatusCode);
        Assert.Same(previousPlaylists, configuration.ScenePlaylists);
        Assert.Equal(new[] { "Source", "Keep" }, configuration.ScenePlaylists.Select(playlist => playlist.Name));
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
            new HueScenePlaylistPreviewRequest
            {
                TargetAllEnabledMappings = true,
                TargetUserIds = new List<string>()
            });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueScenePlaylistRunResult>(response.Value);
        Assert.True(result.TargetAllEnabledMappings);
        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Steps.Count);
        Assert.Equal(2, result.TargetResults.Count);
        var defaultTarget = Assert.Single(result.TargetResults, target => target.TargetLabel == "Default bridge target");
        Assert.True(defaultTarget.Succeeded);
        Assert.Equal(2, defaultTarget.CompletedStepCount);
        var kitchenTarget = Assert.Single(result.TargetResults, target => target.TargetLabel == "Kitchen");
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
    public async Task ScenePlaylistsBulkPreview_RunsSelectedPlaylistsSequentiallyWithoutCredentials()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "bulk-playlist-app-secret",
            HueClientKey = "bulk-playlist-client-secret",
            EntertainmentAreaId = "global-area",
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "mapping-cool",
                    UserName = "Cool room",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "mapping-cool-app-secret",
                    HueClientKey = "mapping-cool-client-secret",
                    EntertainmentAreaId = "cool-area"
                }
            },
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Warm", Red = 25, Green = 50, Blue = 75, DurationSeconds = 1 },
                new() { Name = "Cool", Red = 220, Green = 180, Blue = 140, DurationSeconds = 1 }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new() { Id = "playlist-warm", Name = "Warm sequence", PresetNames = new List<string> { "Warm" } },
                new()
                {
                    Id = "playlist-cool",
                    Name = "Cool sequence",
                    PresetNames = new List<string> { "Cool" },
                    TargetUserIds = new List<string> { "mapping-cool" }
                }
            }
        });
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
            .ReturnsAsync(new HueStreamProbeResult { Succeeded = true, Message = "Bulk playlist step completed." });
        var service = new HueSceneAutomationService(
            streamTester.Object,
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(streamTester.Object, hostedServices: new[] { service });

        var action = await controller.PreviewScenePlaylistsBulk(new HueScenePlaylistBulkPreviewRequest
        {
            PlaylistIds = new List<string> { "playlist-cool", "playlist-warm", "playlist-cool" }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueScenePlaylistBulkPreviewResult>(response.Value);
        Assert.Equal(2, result.RequestedCount);
        Assert.Equal(2, result.CompletedCount);
        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);
        Assert.False(result.Canceled);
        Assert.Equal(new[] { "Cool sequence", "Warm sequence" }, result.Results.Select(playlist => playlist.PlaylistName));
        var coolResult = result.Results[0];
        Assert.Equal(new[] { "mapping-cool" }, coolResult.TargetUserIds);
        Assert.False(coolResult.IncludeDefaultTarget);
        Assert.False(coolResult.TargetAllEnabledMappings);
        Assert.All(result.Results, playlist =>
        {
            Assert.True(playlist.Succeeded);
            Assert.Single(playlist.Steps);
        });
        Assert.Equal(new[] { 220, 25 }, streamTester.Invocations
            .Where(invocation => invocation.Method.Name == nameof(IHueStreamTester.PreviewAsync))
            .Select(invocation => (int)invocation.Arguments[6]!)
            .ToArray());
        Assert.Contains(streamTester.Invocations, invocation =>
            invocation.Method.Name == nameof(IHueStreamTester.PreviewAsync) &&
            string.Equals(invocation.Arguments[1] as string, "mapping-cool-app-secret", StringComparison.Ordinal) &&
            string.Equals(invocation.Arguments[2] as string, "mapping-cool-client-secret", StringComparison.Ordinal) &&
            string.Equals(invocation.Arguments[3] as string, "cool-area", StringComparison.Ordinal));
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("bulk-playlist-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("bulk-playlist-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScenePlaylistsBulkPreview_NestedContinuousCancellationStopsRemainingPlaylists()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "bulk-cancel-app-secret",
            HueClientKey = "bulk-cancel-client-secret",
            EntertainmentAreaId = "global-area",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Warm", DurationSeconds = 1 }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new() { Id = "playlist-cancel-first", Name = "Cancel first", PresetNames = new List<string> { "Warm" } },
                new() { Id = "playlist-must-not-run", Name = "Must not run", PresetNames = new List<string> { "Warm" } }
            }
        });
        SetupHttpResponse(
            HttpStatusCode.OK,
            "{\"data\":[{\"channels\":[{\"channel_id\":0}]}]}");
        var streamTester = new NestedCancellationPlaylistStreamTester();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(streamTester, hostedServices: new[] { service });

        var action = await controller.PreviewScenePlaylistsBulk(new HueScenePlaylistBulkPreviewRequest
        {
            PlaylistIds = new List<string> { "playlist-cancel-first", "playlist-must-not-run" }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueScenePlaylistBulkPreviewResult>(response.Value);
        Assert.True(result.Canceled);
        Assert.Equal(2, result.RequestedCount);
        Assert.Equal(1, result.CompletedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, streamTester.PlaylistCallCount);
        var canceledPlaylist = Assert.Single(result.Results);
        Assert.Equal("Cancel first", canceledPlaylist.PlaylistName);
        Assert.Contains("canceled", canceledPlaylist.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "canceled",
            Assert.Single(canceledPlaylist.Steps).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScenePlaylistsBulkPreview_EmptyTargetIdsDoNotOverrideAllTargetMode()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "bulk-empty-target-global-app-secret",
            HueClientKey = "bulk-empty-target-global-client-secret",
            EntertainmentAreaId = "global-area",
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "mapping-empty-target",
                    UserName = "Empty target room",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "bulk-empty-target-mapping-app-secret",
                    HueClientKey = "bulk-empty-target-mapping-client-secret",
                    EntertainmentAreaId = "mapping-area"
                }
            },
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Warm", DurationSeconds = 1 }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "playlist-empty-target",
                    Name = "Empty target sequence",
                    PresetNames = new List<string> { "Warm" }
                }
            }
        });
        SetupHttpResponse(
            HttpStatusCode.OK,
            "{\"data\":[{\"channels\":[{\"channel_id\":0}]}]}");
        var streamTester = new Mock<IHueStreamTester>();
        streamTester
            .Setup(tester => tester.PreviewAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<JsonElement>(), It.IsAny<IReadOnlySet<int>?>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new HueStreamProbeResult { Succeeded = true, Message = "Broadcast playlist step completed." });
        var service = new HueSceneAutomationService(
            streamTester.Object,
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(streamTester.Object, hostedServices: new[] { service });

        var action = await controller.PreviewScenePlaylistsBulk(new HueScenePlaylistBulkPreviewRequest
        {
            PlaylistIds = new List<string> { "playlist-empty-target" },
            TargetAllEnabledMappings = true,
            TargetUserIds = new List<string>()
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueScenePlaylistBulkPreviewResult>(response.Value);
        var playlistResult = Assert.Single(result.Results);
        Assert.True(playlistResult.Succeeded);
        Assert.True(playlistResult.TargetAllEnabledMappings);
        Assert.Empty(playlistResult.TargetUserIds);
        Assert.False(playlistResult.IncludeDefaultTarget);
        Assert.Equal(2, playlistResult.TargetResults.Count);
        Assert.Equal(2, streamTester.Invocations.Count(invocation =>
            invocation.Method.Name == nameof(IHueStreamTester.PreviewAsync)));
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("bulk-empty-target-global-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("bulk-empty-target-mapping-app-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScenePlaylistsBulkPreview_SelectedTargetOverrideUsesDefaultAndMappingTargets()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "bulk-override-global-app-secret",
            HueClientKey = "bulk-override-global-client-secret",
            EntertainmentAreaId = "global-area",
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "mapping-selected",
                    UserName = "Selected room",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "bulk-override-mapping-app-secret",
                    HueClientKey = "bulk-override-mapping-client-secret",
                    EntertainmentAreaId = "selected-area"
                }
            },
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Warm", Red = 25, Green = 50, Blue = 75, DurationSeconds = 1 }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "playlist-override",
                    Name = "Override sequence",
                    PresetNames = new List<string> { "Warm" },
                    TargetUserIds = new List<string> { "mapping-selected" }
                }
            }
        });
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
            .ReturnsAsync(new HueStreamProbeResult { Succeeded = true, Message = "Selected-target playlist completed." });
        var service = new HueSceneAutomationService(
            streamTester.Object,
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(streamTester.Object, hostedServices: new[] { service });

        var action = await controller.PreviewScenePlaylistsBulk(new HueScenePlaylistBulkPreviewRequest
        {
            PlaylistIds = new List<string> { "playlist-override" },
            TargetUserIds = new List<string> { "mapping-selected" },
            IncludeDefaultTarget = true
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueScenePlaylistBulkPreviewResult>(response.Value);
        var playlistResult = Assert.Single(result.Results);
        Assert.Equal(new[] { "mapping-selected" }, playlistResult.TargetUserIds);
        Assert.True(playlistResult.IncludeDefaultTarget);
        Assert.False(playlistResult.TargetAllEnabledMappings);
        Assert.Equal(2, playlistResult.TargetResults.Count);
        Assert.Contains(streamTester.Invocations, invocation =>
            invocation.Method.Name == nameof(IHueStreamTester.PreviewAsync) &&
            string.Equals(invocation.Arguments[0] as string, "192.168.1.100", StringComparison.Ordinal));
        Assert.Contains(streamTester.Invocations, invocation =>
            invocation.Method.Name == nameof(IHueStreamTester.PreviewAsync) &&
            string.Equals(invocation.Arguments[0] as string, "192.168.1.101", StringComparison.Ordinal));
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("bulk-override-global-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("bulk-override-global-client-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("bulk-override-mapping-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("bulk-override-mapping-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScenePlaylistsBulkPreview_FailedDeviceRouteRetainsRouteTelemetry()
    {
        InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Warm", Red = 25, Green = 50, Blue = 75, DurationSeconds = 1 }
            },
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "mapping-route-failure",
                    UserName = "Route failure room",
                    SyncEnabled = true,
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new()
                        {
                            DeviceId = "living-room-tv",
                            DeviceName = "Living Room TV",
                            HueBridgeIp = "192.168.1.102",
                            HueAppKey = "route-failure-app-secret",
                            HueClientKey = "route-failure-client-secret",
                            EntertainmentAreaId = "route-failure-area"
                        }
                    }
                }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "playlist-route-failure",
                    Name = "Route failure playlist",
                    PresetNames = new List<string> { "Warm" }
                }
            }
        });
        SetupHttpResponse(
            HttpStatusCode.OK,
            "{\"data\":[{\"channels\":[{\"channel_id\":0}]}]}");
        var streamTester = new Mock<IHueStreamTester>();
        streamTester
            .Setup(tester => tester.PreviewAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<JsonElement>(), It.IsAny<IReadOnlySet<int>?>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new HueStreamProbeResult
            {
                Succeeded = false,
                Message = "The route preview failed during bridge cleanup."
            });
        var service = new HueSceneAutomationService(
            streamTester.Object,
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(streamTester.Object, hostedServices: new[] { service });

        var action = await controller.PreviewScenePlaylistsBulk(new HueScenePlaylistBulkPreviewRequest
        {
            PlaylistIds = new List<string> { "playlist-route-failure" },
            TargetRoutes = new List<HueCurrentLightColorTargetRoute>
            {
                new() { UserId = " mapping-route-failure ", DeviceId = " living-room-tv " }
            }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueScenePlaylistBulkPreviewResult>(response.Value);
        var playlistResult = Assert.Single(result.Results);
        Assert.False(playlistResult.Succeeded);
        var route = Assert.Single(playlistResult.TargetRoutes);
        Assert.Equal("mapping-route-failure", route.UserId);
        Assert.Equal("living-room-tv", route.DeviceId);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("route-failure-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("route-failure-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScenePlaylistsBulkPreview_MissingOrInvalidSelectionDoesNotContactBridge()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "bulk-playlist-app-secret",
            HueClientKey = "bulk-playlist-client-secret",
            EntertainmentAreaId = "global-area",
            ColorPresets = new List<HueColorPreset> { new() { Name = "Warm", DurationSeconds = 1 } },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new() { Id = "playlist-valid", Name = "Valid", PresetNames = new List<string> { "Warm" } },
                new() { Id = "playlist-invalid", Name = "Invalid", PresetNames = new List<string> { "Missing" } }
            }
        });
        var controller = CreateController(Mock.Of<IHueStreamTester>());

        var missing = await controller.PreviewScenePlaylistsBulk(new HueScenePlaylistBulkPreviewRequest
        {
            PlaylistIds = new List<string> { "playlist-valid", "missing-playlist" }
        });
        var missingResponse = Assert.IsType<NotFoundObjectResult>(missing.Result);
        var missingResult = Assert.IsType<HueScenePlaylistBulkPreviewResult>(missingResponse.Value);
        Assert.Equal(new[] { "missing-playlist" }, missingResult.MissingIds);

        var invalid = await controller.PreviewScenePlaylistsBulk(new HueScenePlaylistBulkPreviewRequest
        {
            PlaylistIds = new List<string> { "playlist-invalid" }
        });
        var invalidResponse = Assert.IsType<BadRequestObjectResult>(invalid.Result);
        var invalidResult = Assert.IsType<HueScenePlaylistBulkPreviewResult>(invalidResponse.Value);
        Assert.NotEmpty(invalidResult.ValidationErrors);
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
            TransitionOutSeconds = 2,
            TransitionCurve = " easeinout "
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
        Assert.Equal(PluginConfiguration.ColorPresetTransitionCurveLinear, preset.TransitionCurve);
    }

    [Fact]
    public void SaveColorPreset_ReturnsCanonicalTransitionCurve()
    {
        var configuration = InstallConfiguration(new PluginConfiguration());

        var action = CreateController().SaveColorPreset(new HueColorPresetRequest
        {
            Name = "Curve scene",
            DurationSeconds = 6,
            TransitionSeconds = 2,
            TransitionOutSeconds = 2,
            TransitionCurve = " easeout "
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueColorPresetResult>(response.Value);
        Assert.Equal(PluginConfiguration.ColorPresetTransitionCurveEaseOut, result.TransitionCurve);
        Assert.Equal(PluginConfiguration.ColorPresetTransitionCurveEaseOut, Assert.Single(configuration.ColorPresets).TransitionCurve);
    }

    [Fact]
    public void SaveColorPreset_NormalizesStarlightEffectForPersistence()
    {
        var configuration = InstallConfiguration(new PluginConfiguration());

        var action = CreateController().SaveColorPreset(new HueColorPresetRequest
        {
            Name = "Night sky",
            Effect = " starlight ",
            EffectSpeedPercent = 140,
            Red = 180,
            Green = 190,
            Blue = 220,
            BrightnessPercent = 80,
            DurationSeconds = 6
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueColorPresetResult>(response.Value);
        Assert.Equal(PluginConfiguration.ColorPresetEffectStarlight, result.Effect);
        Assert.Equal(PluginConfiguration.ColorPresetEffectStarlight, Assert.Single(configuration.ColorPresets).Effect);
    }

    [Fact]
    public void RenameColorPreset_MigratesReferencesAndPreservesVisualMetadata()
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
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "playlist-1",
                    Name = "Movie sequence",
                    PresetNames = new List<string> { " movie night ", "Movie Night" }
                }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "cue-1",
                    Name = "Movie cue",
                    PresetName = " MOVIE NIGHT "
                }
            }
        });
        var controller = CreateController();

        var action = controller.RenameColorPreset(
            " movie night ",
            new HueColorPresetRenameRequest { NewName = " New Year's Eve " });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueColorPresetResult>(response.Value);
        Assert.Equal("New Year's Eve", result.Name);
        Assert.Equal(PluginConfiguration.ColorPresetEffectCandle, result.Effect);
        Assert.Equal(225, result.EffectSpeedPercent);
        Assert.Equal(230, result.Red);
        Assert.Equal(90, result.Green);
        Assert.Equal(20, result.Blue);
        Assert.Equal(75, result.BrightnessPercent);
        Assert.Equal(8, result.DurationSeconds);
        Assert.Equal(2, result.TransitionSeconds);
        Assert.Equal(3, result.TransitionOutSeconds);

        var preset = Assert.Single(configuration.ColorPresets);
        Assert.Equal("New Year's Eve", preset.Name);
        Assert.Equal(new[] { "New Year's Eve", "New Year's Eve" }, configuration.ScenePlaylists[0].PresetNames);
        Assert.Equal("New Year's Eve", configuration.SceneSchedules[0].PresetName);
    }

    [Fact]
    public void RenameColorPreset_RejectsMissingBlankAndCollidingNamesWithoutMutation()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Original", Red = 10 },
                new() { Name = "Existing", Red = 20 }
            }
        });
        var controller = CreateController();

        var missing = controller.RenameColorPreset(
            "Missing",
            new HueColorPresetRenameRequest { NewName = "Renamed" });
        Assert.IsType<NotFoundObjectResult>(missing.Result);

        var blank = controller.RenameColorPreset(
            "Original",
            new HueColorPresetRenameRequest { NewName = "  " });
        Assert.IsType<BadRequestObjectResult>(blank.Result);

        var collision = controller.RenameColorPreset(
            "Original",
            new HueColorPresetRenameRequest { NewName = " existing " });
        var collisionResponse = Assert.IsType<ConflictObjectResult>(collision.Result);
        Assert.Equal(StatusCodes.Status409Conflict, collisionResponse.StatusCode);
        Assert.Equal(new[] { "Original", "Existing" }, configuration.ColorPresets.Select(preset => preset.Name));
    }

    [Fact]
    public void RenameColorPreset_ReturnsValidationErrorForNullPlaylistSceneReference()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Original", Red = 10 }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "playlist-null-scene",
                    Name = "Malformed playlist",
                    PresetNames = new List<string> { null!, "Original" }
                }
            }
        });

        var action = CreateController().RenameColorPreset(
            "Original",
            new HueColorPresetRenameRequest { NewName = "Renamed" });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Equal("Original", Assert.Single(configuration.ColorPresets).Name);
        Assert.Null(configuration.ScenePlaylists[0].PresetNames[0]);
    }

    [Fact]
    public void GetColorPresetDependencies_ReturnsCredentialFreeReferenceDetails()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueAppKey = "secret-app-key",
            HueClientKey = "secret-client-key",
            ColorPresets = new List<HueColorPreset> { new() { Name = "Accent" } },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "playlist-1",
                    Name = "Opening",
                    PresetNames = new List<string> { "Accent", " accent " }
                }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "cue-1", Name = "Opening cue", PresetName = " accent ", Enabled = true },
                new() { Id = "cue-2", Name = "Playlist cue", PlaylistName = " opening ", Enabled = false }
            }
        });

        var action = CreateController().GetColorPresetDependencies(" accent ");

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueColorPresetDependenciesResult>(response.Value);
        Assert.Equal("Accent", result.Name);
        Assert.False(result.CanDelete);
        Assert.Equal(1, result.PlaylistCount);
        Assert.Equal(2, result.ScheduledCueCount);
        var playlist = Assert.Single(result.Playlists);
        Assert.Equal("playlist-1", playlist.Id);
        Assert.Equal("Opening", playlist.Name);
        Assert.Equal(2, playlist.ReferenceCount);
        var directSchedule = Assert.Single(result.ScheduledCues, schedule => schedule.ReferenceType == "DirectScene");
        Assert.Equal("cue-1", directSchedule.Id);
        Assert.Equal("Opening cue", directSchedule.Name);
        Assert.True(directSchedule.Enabled);
        Assert.Empty(directSchedule.PlaylistName);
        var playlistSchedule = Assert.Single(result.ScheduledCues, schedule => schedule.ReferenceType == "Playlist");
        Assert.Equal("cue-2", playlistSchedule.Id);
        Assert.Equal("Playlist cue", playlistSchedule.Name);
        Assert.False(playlistSchedule.Enabled);
        Assert.Equal("opening", playlistSchedule.PlaylistName);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("secret-app-key", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-client-key", serialized, StringComparison.Ordinal);
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
    public void ColorPresets_BulkDuplicateCreatesIndependentCopiesAtomically()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            HueAppKey = "bulk-duplicate-scene-app-secret",
            HueClientKey = "bulk-duplicate-scene-client-secret",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Warm", Effect = PluginConfiguration.ColorPresetEffectPulse, EffectSpeedPercent = 175, DurationSeconds = 8 },
                new() { Name = "Cool", Effect = PluginConfiguration.ColorPresetEffectRainbow, Red = 10, Green = 20, Blue = 30 },
                new() { Name = "Keep" }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new() { Id = "scene-copy-playlist", Name = "Original playlist", PresetNames = new List<string> { "Warm" } }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "scene-copy-cue", Name = "Original cue", PresetName = "Cool" }
            }
        });

        var action = CreateController().DuplicateColorPresetsBulk(new HueColorPresetBulkDuplicateRequest
        {
            PresetNames = new List<string> { " warm ", "COOL", "Warm" }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueColorPresetBulkDuplicateResult>(response.Value);
        Assert.Equal(2, result.RequestedCount);
        Assert.Equal(2, result.DuplicatedCount);
        Assert.Equal(5, result.RemainingCount);
        Assert.Equal(2, result.Presets.Count);
        Assert.Equal(new[] { "Warm (Copy)", "Cool (Copy)" }, result.Presets.Select(preset => preset.Name));
        Assert.Equal(PluginConfiguration.ColorPresetEffectPulse, result.Presets[0].Effect);
        Assert.Equal(175, result.Presets[0].EffectSpeedPercent);
        Assert.Equal(new[] { "Warm", "Cool", "Keep", "Warm (Copy)", "Cool (Copy)" },
            configuration.ColorPresets.Select(preset => preset.Name));
        Assert.Equal("Warm", Assert.Single(configuration.ScenePlaylists).PresetNames.Single());
        Assert.Equal("Cool", Assert.Single(configuration.SceneSchedules).PresetName);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("bulk-duplicate-scene-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("bulk-duplicate-scene-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void ColorPresets_BulkDuplicateRefusesMissingNamesAndCapacityAtomically()
    {
        var missingConfiguration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Existing" } }
        });
        var controller = CreateController();

        var missing = controller.DuplicateColorPresetsBulk(new HueColorPresetBulkDuplicateRequest
        {
            PresetNames = new List<string> { "Existing", "Missing" }
        });

        var missingResponse = Assert.IsType<NotFoundObjectResult>(missing.Result);
        var missingResult = Assert.IsType<HueColorPresetBulkDuplicateResult>(missingResponse.Value);
        Assert.Equal(2, missingResult.RequestedCount);
        Assert.Equal(new[] { "Missing" }, missingResult.MissingNames);
        Assert.Single(missingConfiguration.ColorPresets);

        var capacityConfiguration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = Enumerable.Range(0, PluginConfiguration.MaxColorPresets - 1)
                .Select(index => new HueColorPreset { Name = $"Scene {index}" })
                .ToList()
        });
        var capacity = CreateController().DuplicateColorPresetsBulk(new HueColorPresetBulkDuplicateRequest
        {
            PresetNames = new List<string> { "Scene 0", "Scene 1" }
        });

        var capacityResponse = Assert.IsType<ConflictObjectResult>(capacity.Result);
        var capacityResult = Assert.IsType<HueColorPresetBulkDuplicateResult>(capacityResponse.Value);
        Assert.Equal(2, capacityResult.RequestedCount);
        Assert.Equal(1, capacityResult.AvailableCapacity);
        Assert.Equal(PluginConfiguration.MaxColorPresets - 1, capacityConfiguration.ColorPresets.Count);
    }

    [Fact]
    public void ColorPresets_BulkDuplicatePersistenceFailureRestoresCollection()
    {
        var serializer = new Mock<IXmlSerializer>();
        serializer
            .Setup(xml => xml.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("bulk scene duplication failed"));
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Copy source" },
                new() { Name = "Keep" }
            }
        }, serializer.Object);
        var previousPresets = configuration.ColorPresets;

        var action = CreateController().DuplicateColorPresetsBulk(new HueColorPresetBulkDuplicateRequest
        {
            PresetNames = new List<string> { "copy source" }
        });

        var response = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, response.StatusCode);
        Assert.Same(previousPresets, configuration.ColorPresets);
        Assert.Equal(new[] { "Copy source", "Keep" }, configuration.ColorPresets.Select(preset => preset.Name));
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
    public void ColorPresets_BulkDeleteRemovesSelectedScenesByName()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            HueAppKey = "bulk-scene-app-secret",
            HueClientKey = "bulk-scene-client-secret",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Warm", Effect = PluginConfiguration.ColorPresetEffectPulse, EffectSpeedPercent = 140 },
                new() { Name = "Cool", Effect = PluginConfiguration.ColorPresetEffectRainbow, EffectSpeedPercent = 180 },
                new() { Name = "Keep", Red = 15, Green = 25, Blue = 35 }
            }
        });

        var action = CreateController().DeleteColorPresetsBulk(new HueColorPresetBulkDeleteRequest
        {
            PresetNames = new List<string> { " warm ", "COOL" }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueColorPresetBulkDeleteResult>(response.Value);
        Assert.Equal(2, result.RequestedCount);
        Assert.Equal(2, result.DeletedCount);
        Assert.Equal(1, result.RemainingCount);
        Assert.Equal(new[] { "Warm", "Cool" }, result.Presets.Select(preset => preset.Name));
        Assert.Equal("Keep", Assert.Single(configuration.ColorPresets).Name);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("bulk-scene-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("bulk-scene-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void ColorPresets_BulkDeleteRefusesDependenciesAndMissingNamesAtomically()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Dependent" },
                new() { Name = "Direct" },
                new() { Name = "Free" }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "scene-bulk-playlist",
                    Name = "Dependent playlist",
                    PresetNames = new List<string> { " dependent " }
                }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "scene-bulk-direct-cue", Name = "Direct cue", PresetName = " DIRECT " },
                new() { Id = "scene-bulk-playlist-cue", Name = "Playlist cue", PlaylistName = "dependent playlist" }
            }
        });
        var controller = CreateController();

        var blocked = controller.DeleteColorPresetsBulk(new HueColorPresetBulkDeleteRequest
        {
            PresetNames = new List<string> { "Dependent", "Direct", "Free" }
        });

        var blockedResponse = Assert.IsType<ConflictObjectResult>(blocked.Result);
        var blockedResult = Assert.IsType<HueColorPresetBulkDeleteResult>(blockedResponse.Value);
        Assert.Equal(3, blockedResult.RequestedCount);
        Assert.Equal(new[] { "Dependent", "Direct" }, blockedResult.BlockedPresets.Select(preset => preset.Name));
        var dependent = blockedResult.BlockedPresets.Single(preset => preset.Name == "Dependent");
        Assert.Equal(1, dependent.PlaylistCount);
        Assert.Equal(1, dependent.ScheduledCueCount);
        var direct = blockedResult.BlockedPresets.Single(preset => preset.Name == "Direct");
        Assert.Equal(0, direct.PlaylistCount);
        Assert.Equal(1, direct.ScheduledCueCount);
        Assert.Equal(3, configuration.ColorPresets.Count);

        var missing = controller.DeleteColorPresetsBulk(new HueColorPresetBulkDeleteRequest
        {
            PresetNames = new List<string> { "Free", "Missing" }
        });

        var missingResponse = Assert.IsType<NotFoundObjectResult>(missing.Result);
        var missingResult = Assert.IsType<HueColorPresetBulkDeleteResult>(missingResponse.Value);
        Assert.Equal(2, missingResult.RequestedCount);
        Assert.Equal(new[] { "Missing" }, missingResult.MissingNames);
        Assert.Equal(3, configuration.ColorPresets.Count);
    }

    [Fact]
    public void ColorPresets_BulkDeletePersistenceFailureRestoresCollection()
    {
        var serializer = new Mock<IXmlSerializer>();
        serializer
            .Setup(xml => xml.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("bulk scene persistence failed"));
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Remove" },
                new() { Name = "Keep" }
            }
        }, serializer.Object);

        var action = CreateController().DeleteColorPresetsBulk(new HueColorPresetBulkDeleteRequest
        {
            PresetNames = new List<string> { "remove" }
        });

        var response = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, response.StatusCode);
        Assert.Equal(new[] { "Remove", "Keep" }, configuration.ColorPresets.Select(preset => preset.Name));
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
            PlaybackPolicy = PluginConfiguration.SceneAutomationPlaybackPolicyDefer,
            TargetUserId = "user-1",
            TimeOfDay = "07:05",
            TimeZoneId = TimeZoneInfo.Utc.Id,
            StartDate = " 2026-08-01 ",
            EndDate = "2026-12-31",
            ExcludedDates = new List<string> { "2026-12-31", " 2026-12-24 ", "2026-12-31" },
            DaysOfWeekMask = 1 | 32,
            DurationSeconds = 12,
            BrightnessPercent = 25,
            Red = 101,
            Green = 102,
            Blue = 103,
            MaxRuns = 3,
            Enabled = true
        });

        var savedResponse = Assert.IsType<OkObjectResult>(saved.Result);
        var savedResult = Assert.IsType<HueSceneScheduleResult>(savedResponse.Value);
        Assert.False(string.IsNullOrWhiteSpace(savedResult.Id));
        Assert.Equal("Living Room", savedResult.TargetLabel);
        Assert.Equal(42, savedResult.Priority);
        Assert.Equal(PluginConfiguration.SceneAutomationPlaybackPolicyDefer, savedResult.PlaybackPolicy);
        Assert.Equal(PluginConfiguration.SceneAutomationPlaybackPolicyDefer, savedResult.EffectivePlaybackPolicy);
        Assert.Equal("07:05", savedResult.TimeOfDay);
        Assert.Equal(TimeZoneInfo.Utc.Id, savedResult.TimeZoneId);
        Assert.Equal("2026-08-01", savedResult.StartDate);
        Assert.Equal("2026-12-31", savedResult.EndDate);
        Assert.Equal(new[] { "2026-12-24", "2026-12-31" }, savedResult.ExcludedDates);
        Assert.Equal(12, savedResult.DurationSeconds);
        Assert.Equal(25, savedResult.BrightnessPercent);
        Assert.Equal(3, savedResult.MaxRuns);
        Assert.Equal(0, savedResult.RunCount);
        Assert.Equal(4, savedResult.TransitionSeconds);
        Assert.Equal(2, savedResult.TransitionOutSeconds);
        Assert.Equal(150, savedResult.EffectSpeedPercent);
        Assert.Equal(101, savedResult.Red);
        Assert.Equal(102, savedResult.Green);
        Assert.Equal(103, savedResult.Blue);
        Assert.Equal(new[] { "2026-12-24", "2026-12-31" }, configuration.SceneSchedules[0].ExcludedDates);
        Assert.Equal(12, configuration.SceneSchedules[0].DurationSeconds);
        Assert.Equal(25, configuration.SceneSchedules[0].BrightnessPercent);
        Assert.Equal(101, configuration.SceneSchedules[0].Red);
        Assert.Equal(102, configuration.SceneSchedules[0].Green);
        Assert.Equal(103, configuration.SceneSchedules[0].Blue);
        Assert.Equal(42, configuration.SceneSchedules[0].Priority);
        Assert.Equal(PluginConfiguration.SceneAutomationPlaybackPolicyDefer, configuration.SceneSchedules[0].PlaybackPolicy);
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
        Assert.Equal(PluginConfiguration.SceneAutomationPlaybackPolicyDefer, configuration.SceneSchedules[0].PlaybackPolicy);
        Assert.Equal(101, configuration.SceneSchedules[0].Red);
        Assert.Equal(102, configuration.SceneSchedules[0].Green);
        Assert.Equal(103, configuration.SceneSchedules[0].Blue);
        Assert.Equal(12, configuration.SceneSchedules[0].DurationSeconds);
        Assert.Equal(25, configuration.SceneSchedules[0].BrightnessPercent);

        var cleared = controller.SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Id = savedResult.Id,
            Name = "Evening Cue Cleared",
            PresetName = "Evening",
            TimeOfDay = "21:45",
            DaysOfWeekMask = 127,
            Red = null,
            Green = null,
            Blue = null,
            BrightnessPercent = null,
            DurationSeconds = 0,
            Enabled = false
        });
        Assert.IsType<OkObjectResult>(cleared.Result);
        Assert.Null(configuration.SceneSchedules[0].Red);
        Assert.Null(configuration.SceneSchedules[0].Green);
        Assert.Null(configuration.SceneSchedules[0].Blue);
        Assert.Null(configuration.SceneSchedules[0].BrightnessPercent);
        Assert.Equal(0, configuration.SceneSchedules[0].DurationSeconds);

        var list = controller.GetSceneSchedules();
        var listResponse = Assert.IsType<OkObjectResult>(list.Result);
        var listed = Assert.Single(Assert.IsAssignableFrom<IEnumerable<HueSceneScheduleResult>>(listResponse.Value));
        Assert.Equal("Evening Cue Cleared", listed.Name);
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
    public void SceneSchedules_CrudSupportsSolarTimingAndPortableMetadata()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Morning" } }
        });
        var controller = CreateController();

        var saved = controller.SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Name = "Solar morning",
            PresetName = "Morning",
            TimeMode = PluginConfiguration.SceneScheduleTimeModeSunrise.ToLowerInvariant(),
            SolarOffsetMinutes = 15,
            SolarLatitude = 40.7128,
            SolarLongitude = -74.0060,
            TimeOfDay = string.Empty,
            TimeZoneId = TimeZoneInfo.Utc.Id,
            Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
            DaysOfWeekMask = 0
        });

        var savedResult = Assert.IsType<HueSceneScheduleResult>(Assert.IsType<OkObjectResult>(saved.Result).Value);
        Assert.Equal(PluginConfiguration.SceneScheduleTimeModeSunrise, savedResult.TimeMode);
        Assert.Equal(15, savedResult.SolarOffsetMinutes);
        Assert.Equal(40.7128, savedResult.SolarLatitude);
        Assert.Equal(-74.0060, savedResult.SolarLongitude);
        Assert.Equal(PluginConfiguration.SceneScheduleTimeModeSunrise, configuration.SceneSchedules[0].TimeMode);

        var serialized = JsonSerializer.Serialize(savedResult);
        Assert.Contains("\"timeMode\":\"Sunrise\"", serialized, StringComparison.Ordinal);
        Assert.Contains("\"solarOffsetMinutes\":15", serialized, StringComparison.Ordinal);
        Assert.Contains("\"solarLatitude\":40.7128", serialized, StringComparison.Ordinal);
        Assert.Contains("\"solarLongitude\":-74.006", serialized, StringComparison.Ordinal);

        var updated = controller.SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Id = savedResult.Id,
            Name = "Solar morning updated",
            PresetName = "Morning",
            TimeMode = PluginConfiguration.SceneScheduleTimeModeSunrise,
            SolarOffsetMinutes = 15,
            SolarLatitude = 40.7128,
            SolarLongitude = -74.0060,
            Enabled = false
        });

        var updatedResult = Assert.IsType<HueSceneScheduleResult>(Assert.IsType<OkObjectResult>(updated.Result).Value);
        Assert.Equal(PluginConfiguration.SceneScheduleTimeModeSunrise, updatedResult.TimeMode);
        Assert.Equal(15, updatedResult.SolarOffsetMinutes);
        Assert.Equal(40.7128, updatedResult.SolarLatitude);
        Assert.Equal(-74.0060, updatedResult.SolarLongitude);
        Assert.False(updatedResult.Enabled);

        var solarNoonUpdated = controller.SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Id = savedResult.Id,
            Name = "Solar noon updated",
            PresetName = "Morning",
            TimeMode = PluginConfiguration.SceneScheduleTimeModeSolarNoon.ToLowerInvariant(),
            SolarOffsetMinutes = 5,
            SolarLatitude = 40.7128,
            SolarLongitude = -74.0060,
            Enabled = true
        });

        var solarNoonResult = Assert.IsType<HueSceneScheduleResult>(Assert.IsType<OkObjectResult>(solarNoonUpdated.Result).Value);
        Assert.Equal(PluginConfiguration.SceneScheduleTimeModeSolarNoon, solarNoonResult.TimeMode);
        Assert.Equal(5, solarNoonResult.SolarOffsetMinutes);
        Assert.Equal(PluginConfiguration.SceneScheduleTimeModeSolarNoon, configuration.SceneSchedules[0].TimeMode);
        Assert.Contains("\"timeMode\":\"SolarNoon\"", JsonSerializer.Serialize(solarNoonResult), StringComparison.Ordinal);

        var civilUpdated = controller.SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Id = savedResult.Id,
            Name = "Civil dusk updated",
            PresetName = "Morning",
            TimeMode = PluginConfiguration.SceneScheduleTimeModeCivilDusk.ToLowerInvariant(),
            SolarOffsetMinutes = -20,
            SolarLatitude = 40.7128,
            SolarLongitude = -74.0060,
            Enabled = true
        });

        var civilResult = Assert.IsType<HueSceneScheduleResult>(Assert.IsType<OkObjectResult>(civilUpdated.Result).Value);
        Assert.Equal(PluginConfiguration.SceneScheduleTimeModeCivilDusk, civilResult.TimeMode);
        Assert.Equal(-20, civilResult.SolarOffsetMinutes);
        Assert.True(civilResult.Enabled);
        Assert.Equal(PluginConfiguration.SceneScheduleTimeModeCivilDusk, configuration.SceneSchedules[0].TimeMode);
        Assert.Contains("\"timeMode\":\"CivilDusk\"", JsonSerializer.Serialize(civilResult), StringComparison.Ordinal);

        var astronomicalUpdated = controller.SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Id = savedResult.Id,
            Name = "Astronomical dusk updated",
            PresetName = "Morning",
            TimeMode = PluginConfiguration.SceneScheduleTimeModeAstronomicalDusk.ToLowerInvariant(),
            SolarOffsetMinutes = 10,
            SolarLatitude = 40.7128,
            SolarLongitude = -74.0060,
            Enabled = true
        });

        var astronomicalResult = Assert.IsType<HueSceneScheduleResult>(Assert.IsType<OkObjectResult>(astronomicalUpdated.Result).Value);
        Assert.Equal(PluginConfiguration.SceneScheduleTimeModeAstronomicalDusk, astronomicalResult.TimeMode);
        Assert.Equal(10, astronomicalResult.SolarOffsetMinutes);
        Assert.Equal(PluginConfiguration.SceneScheduleTimeModeAstronomicalDusk, configuration.SceneSchedules[0].TimeMode);
        Assert.Contains("\"timeMode\":\"AstronomicalDusk\"", JsonSerializer.Serialize(astronomicalResult), StringComparison.Ordinal);
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
    public void SceneSchedules_SelectedTargetsRoundTripAndPreserveOnPartialUpdate()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "selected-app-secret",
            HueClientKey = "selected-client-secret",
            EntertainmentAreaId = "global-area",
            ColorPresets = new List<HueColorPreset> { new() { Name = "Welcome" } },
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-1",
                    UserName = "Kitchen",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "mapping-app-secret",
                    HueClientKey = "mapping-client-secret",
                    EntertainmentAreaId = "mapping-area"
                }
            }
        });
        var controller = CreateController();

        var saved = controller.SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Name = "Selected welcome",
            PresetName = "Welcome",
            TargetUserIds = new List<string> { "user-1" },
            IncludeDefaultTarget = true,
            TimeOfDay = "08:00",
            DaysOfWeekMask = 127
        });

        var savedResult = Assert.IsType<HueSceneScheduleResult>(Assert.IsType<OkObjectResult>(saved.Result).Value);
        Assert.Equal(new[] { "user-1" }, savedResult.TargetUserIds);
        Assert.True(savedResult.IncludeDefaultTarget);
        Assert.Equal("Default bridge + 1 selected target(s)", savedResult.TargetLabel);
        Assert.Equal(new[] { "user-1" }, configuration.SceneSchedules[0].TargetUserIds);

        var updated = controller.SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Id = savedResult.Id,
            Name = "Selected welcome updated",
            PresetName = "Welcome",
            TimeOfDay = "09:00",
            DaysOfWeekMask = 127,
            Enabled = false
        });

        var updatedResult = Assert.IsType<HueSceneScheduleResult>(Assert.IsType<OkObjectResult>(updated.Result).Value);
        Assert.Equal(new[] { "user-1" }, updatedResult.TargetUserIds);
        Assert.True(updatedResult.IncludeDefaultTarget);
        Assert.Equal("Default bridge + 1 selected target(s)", updatedResult.TargetLabel);
    }

    [Fact]
    public void SceneSchedules_DeviceRouteRoundTripsAndPreservesOnPartialUpdate()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Welcome" } },
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-device",
                    UserName = "Living Room",
                    SyncEnabled = true,
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new()
                        {
                            DeviceId = "living-room-tv",
                            DeviceName = "Living Room TV",
                            HueBridgeIp = "192.168.1.102",
                            HueAppKey = "device-app-secret",
                            HueClientKey = "device-client-secret",
                            EntertainmentAreaId = "device-area"
                        }
                    }
                }
            }
        });
        var controller = CreateController();

        var saved = controller.SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Name = "Device welcome",
            PresetName = "Welcome",
            TargetRoutes = new List<HueSceneScheduleTargetRoute>
            {
                new() { UserId = " user-device ", DeviceId = " living-room-tv " }
            },
            TimeOfDay = "08:00",
            DaysOfWeekMask = 127
        });

        var savedResult = Assert.IsType<HueSceneScheduleResult>(Assert.IsType<OkObjectResult>(saved.Result).Value);
        var savedRoute = Assert.Single(savedResult.TargetRoutes);
        Assert.Equal("user-device", savedRoute.UserId);
        Assert.Equal("living-room-tv", savedRoute.DeviceId);
        Assert.Equal("user-device / living-room-tv", savedResult.TargetLabel);
        var persistedRoute = Assert.Single(configuration.SceneSchedules[0].TargetRoutes);
        Assert.Equal("user-device", persistedRoute.UserId);
        Assert.Equal("living-room-tv", persistedRoute.DeviceId);
        Assert.DoesNotContain("device-app-secret", JsonSerializer.Serialize(savedResult), StringComparison.Ordinal);

        var updated = controller.SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Id = savedResult.Id,
            Name = "Device welcome updated",
            PresetName = "Welcome",
            TimeOfDay = "09:00",
            DaysOfWeekMask = 127,
            Enabled = false
        });

        var updatedResult = Assert.IsType<HueSceneScheduleResult>(Assert.IsType<OkObjectResult>(updated.Result).Value);
        var updatedRoute = Assert.Single(updatedResult.TargetRoutes);
        Assert.Equal("user-device", updatedRoute.UserId);
        Assert.Equal("living-room-tv", updatedRoute.DeviceId);
        Assert.False(configuration.SceneSchedules[0].Enabled);
    }

    [Fact]
    public void ConfigurationExportAndImport_PreservesScheduledDeviceRoutesWithoutCredentials()
    {
        var source = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Welcome" } },
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-device",
                    SyncEnabled = true,
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new()
                        {
                            DeviceId = "living-room-tv",
                            HueBridgeIp = "192.168.1.102",
                            HueAppKey = "source-device-app-secret",
                            HueClientKey = "source-device-client-secret",
                            EntertainmentAreaId = "device-area"
                        }
                    }
                }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "device-schedule",
                    Name = "Device welcome",
                    PresetName = "Welcome",
                    TargetRoutes = new List<HueSceneScheduleTargetRoute>
                    {
                        new() { UserId = "user-device", DeviceId = "living-room-tv" }
                    }
                }
            }
        };
        var exported = HueConfigurationExportDocument.From(source);
        var exportedSchedule = Assert.Single(exported.SceneSchedules);
        var exportedRoute = Assert.Single(exportedSchedule.TargetRoutes);
        Assert.Equal("user-device", exportedRoute.UserId);
        Assert.Equal("living-room-tv", exportedRoute.DeviceId);
        Assert.DoesNotContain("source-device-app-secret", JsonSerializer.Serialize(exported), StringComparison.Ordinal);

        var destination = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Welcome" } },
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-device",
                    SyncEnabled = true,
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new() { DeviceId = "living-room-tv", HueBridgeIp = "192.168.1.103", HueAppKey = "destination-app", HueClientKey = "destination-client", EntertainmentAreaId = "destination-area" }
                    }
                }
            }
        });
        var action = CreateController().ImportConfiguration(new HueConfigurationImportRequest
        {
            Configuration = exported.Configuration,
            ReplaceMappings = false,
            ReplaceColorPresets = false,
            ReplaceSceneSchedules = true,
            SceneSchedules = new List<HueSceneScheduleRequest>
            {
                new()
                {
                    Id = exportedSchedule.Id,
                    Name = exportedSchedule.Name,
                    PresetName = exportedSchedule.PresetName,
                    TargetRoutes = exportedSchedule.TargetRoutes.Select(route => new HueSceneScheduleTargetRoute
                    {
                        UserId = route.UserId,
                        DeviceId = route.DeviceId
                    }).ToList()
                }
            }
        });

        Assert.IsType<OkObjectResult>(action.Result);
        var importedRoute = Assert.Single(Assert.Single(destination.SceneSchedules).TargetRoutes);
        Assert.Equal("user-device", importedRoute.UserId);
        Assert.Equal("living-room-tv", importedRoute.DeviceId);
        Assert.Equal("destination-app", destination.UserMappings[0].DeviceTargets[0].HueAppKey);

        var partialImport = CreateController().ImportConfiguration(new HueConfigurationImportRequest
        {
            Configuration = exported.Configuration,
            ReplaceMappings = false,
            ReplaceColorPresets = false,
            ReplaceSceneSchedules = false,
            SceneSchedules = new List<HueSceneScheduleRequest>
            {
                new()
                {
                    Id = exportedSchedule.Id,
                    Name = "Device welcome retained",
                    PresetName = exportedSchedule.PresetName
                }
            }
        });

        Assert.IsType<OkObjectResult>(partialImport.Result);
        var retainedRoute = Assert.Single(Assert.Single(destination.SceneSchedules).TargetRoutes);
        Assert.Equal("user-device", retainedRoute.UserId);
        Assert.Equal("living-room-tv", retainedRoute.DeviceId);
        Assert.Equal("Device welcome retained", destination.SceneSchedules[0].Name);
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
    public void SceneSchedules_BulkDuplicateCreatesDisabledFreshCopiesAtomically()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Bulk Evening" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "bulk-copy-one",
                    Name = "Bulk Copy One",
                    PresetName = "Bulk Evening",
                    Priority = 61,
                    TimeOfDay = "20:15",
                    MaxRuns = 4,
                    RunCount = 3,
                    Enabled = true,
                    SkipNextOccurrence = true
                },
                new()
                {
                    Id = "bulk-copy-two",
                    Name = "Bulk Copy Two",
                    PresetName = "Bulk Evening",
                    Priority = 12,
                    TimeOfDay = "21:15",
                    MaxRuns = 6,
                    RunCount = 2,
                    Enabled = false
                },
                new()
                {
                    Id = "bulk-copy-untouched",
                    Name = "Bulk Copy Untouched",
                    PresetName = "Bulk Evening",
                    TimeOfDay = "22:15",
                    Enabled = true
                }
            }
        });
        var controller = CreateController();

        var action = controller.DuplicateSceneSchedulesBulk(new HueSceneScheduleBulkDuplicateRequest
        {
            ScheduleIds = new List<string> { " BULK-COPY-ONE ", "bulk-copy-two", "bulk-copy-one" }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueSceneScheduleBulkDuplicateResult>(response.Value);
        Assert.Equal(2, result.RequestedCount);
        Assert.Equal(2, result.DuplicatedCount);
        Assert.Equal(new[] { "Bulk Copy One (Copy)", "Bulk Copy Two (Copy)" }, result.Schedules.Select(schedule => schedule.Name));
        Assert.All(result.Schedules, schedule =>
        {
            Assert.False(schedule.Enabled);
            Assert.Equal(0, schedule.RunCount);
            Assert.False(schedule.SkipNextOccurrence);
            Assert.NotEqual("bulk-copy-one", schedule.Id);
            Assert.NotEqual("bulk-copy-two", schedule.Id);
        });
        Assert.Equal(5, configuration.SceneSchedules.Count);
        Assert.True(configuration.SceneSchedules.Single(schedule => schedule.Id == "bulk-copy-one").Enabled);
        Assert.Equal(3, configuration.SceneSchedules.Single(schedule => schedule.Id == "bulk-copy-one").RunCount);
        Assert.True(configuration.SceneSchedules.Single(schedule => schedule.Id == "bulk-copy-untouched").Enabled);
        Assert.Equal(61, result.Schedules[0].Priority);
        Assert.Equal(12, result.Schedules[1].Priority);
        Assert.Equal(result.Schedules.Count, result.Schedules.Select(schedule => schedule.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void SceneSchedules_BulkDuplicateRefusesMissingIdsAndCapacityAtomically()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Bulk Missing" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "bulk-missing-existing", Name = "Existing", PresetName = "Bulk Missing" }
            }
        });
        var controller = CreateController();

        var missing = controller.DuplicateSceneSchedulesBulk(new HueSceneScheduleBulkDuplicateRequest
        {
            ScheduleIds = new List<string> { "bulk-missing-existing", "not-present" }
        });

        var missingResponse = Assert.IsType<NotFoundObjectResult>(missing.Result);
        var missingResult = Assert.IsType<HueSceneScheduleBulkDuplicateResult>(missingResponse.Value);
        Assert.Equal(new[] { "not-present" }, missingResult.MissingScheduleIds);
        Assert.Single(configuration.SceneSchedules);

        var capacitySchedules = Enumerable.Range(1, PluginConfiguration.MaxSceneSchedules - 1)
            .Select(index => new HueSceneSchedule
            {
                Id = $"bulk-capacity-{index}",
                Name = $"Capacity {index}",
                PresetName = "Bulk Capacity",
                TimeOfDay = "20:00",
                DaysOfWeekMask = PluginConfiguration.AllSceneScheduleDaysMask
            })
            .ToList();
        var capacityConfiguration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Bulk Capacity" } },
            SceneSchedules = capacitySchedules
        });
        var capacityController = CreateController();

        var capacity = capacityController.DuplicateSceneSchedulesBulk(new HueSceneScheduleBulkDuplicateRequest
        {
            ScheduleIds = new List<string> { "bulk-capacity-1", "bulk-capacity-2" }
        });

        var capacityResponse = Assert.IsType<ConflictObjectResult>(capacity.Result);
        var capacityResult = Assert.IsType<HueSceneScheduleBulkDuplicateResult>(capacityResponse.Value);
        Assert.Equal(2, capacityResult.RequestedCount);
        Assert.Equal(1, capacityResult.AvailableCapacity);
        Assert.Equal(PluginConfiguration.MaxSceneSchedules - 1, capacityConfiguration.SceneSchedules.Count);
    }

    [Fact]
    public void SceneSchedules_BulkDuplicatePersistenceFailureRestoresCollection()
    {
        var serializer = new Mock<IXmlSerializer>();
        serializer
            .Setup(xml => xml.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("bulk cue duplicate persistence failed"));
        var schedules = new List<HueSceneSchedule>
        {
            new()
            {
                Id = "bulk-persist-source",
                Name = "Bulk persistence source",
                PresetName = "Bulk Persistence",
                RunCount = 2,
                Enabled = true
            }
        };
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Bulk Persistence" } },
            SceneSchedules = schedules
        }, serializer.Object);

        var action = CreateController().DuplicateSceneSchedulesBulk(new HueSceneScheduleBulkDuplicateRequest
        {
            ScheduleIds = new List<string> { "bulk-persist-source" }
        });

        var response = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, response.StatusCode);
        Assert.Same(schedules, configuration.SceneSchedules);
        Assert.Single(configuration.SceneSchedules);
        Assert.Equal(2, configuration.SceneSchedules[0].RunCount);
        Assert.True(configuration.SceneSchedules[0].Enabled);
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
        Assert.Equal("Etc/UTC", result.TimeZoneIanaId);
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
        var utcZone = Assert.Single(zones, zone => zone.Id == TimeZoneInfo.Utc.Id);
        Assert.Equal("Etc/UTC", utcZone.TimeZoneIanaId);
        var easternZone = zones.FirstOrDefault(zone =>
            string.Equals(zone.Id, "America/New_York", StringComparison.Ordinal) ||
            string.Equals(zone.Id, "Eastern Standard Time", StringComparison.Ordinal));
        Assert.NotNull(easternZone);
        Assert.Equal("America/New_York", easternZone!.TimeZoneIanaId);
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
    public void SceneSchedules_RejectInvalidBrightnessAndPlaylistBrightnessOverridesWithoutSaving()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new() { Id = "playlist-1", Name = "Evening sequence", PresetNames = new List<string> { "Evening" } }
            }
        });
        var controller = CreateController();

        var invalidDirect = controller.SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Name = "Broken brightness cue",
            PresetName = "Evening",
            TimeOfDay = "20:00",
            BrightnessPercent = 101,
            DaysOfWeekMask = 127
        });

        var invalidDirectResponse = Assert.IsType<BadRequestObjectResult>(invalidDirect.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, invalidDirectResponse.StatusCode);
        Assert.Empty(configuration.SceneSchedules);

        var invalidPlaylist = controller.SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Name = "Playlist brightness cue",
            PlaylistName = "Evening sequence",
            TimeOfDay = "20:00",
            BrightnessPercent = 50,
            DaysOfWeekMask = 127
        });

        var invalidPlaylistResponse = Assert.IsType<BadRequestObjectResult>(invalidPlaylist.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, invalidPlaylistResponse.StatusCode);
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
    public void SceneSchedules_ResetRunCountWithoutHostedServicePersistsAndClearsPendingSkip()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "direct-reset-cue",
                    Name = "Direct reset cue",
                    PresetName = "Evening",
                    MaxRuns = 3,
                    RunCount = 2,
                    Enabled = false,
                    SkipNextOccurrence = true
                }
            }
        });

        var action = CreateController().ResetSceneScheduleRunCount(" direct-reset-cue ");

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueSceneScheduleResult>(response.Value);
        Assert.Equal(0, result.RunCount);
        Assert.True(result.Enabled);
        Assert.False(result.SkipNextOccurrence);
        Assert.Equal(0, configuration.SceneSchedules[0].RunCount);
        Assert.True(configuration.SceneSchedules[0].Enabled);
        Assert.False(configuration.SceneSchedules[0].SkipNextOccurrence);
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
    public void SceneSchedules_BulkEnabledActionUpdatesSelectedCuesAtomically()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Bulk API scene" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "bulk-api-one",
                    Name = "Bulk API one",
                    PresetName = "Bulk API scene",
                    TimeOfDay = "06:30",
                    DurationSeconds = 7,
                    Enabled = true
                },
                new()
                {
                    Id = "bulk-api-two",
                    Name = "Bulk API two",
                    PresetName = "Bulk API scene",
                    TimeOfDay = "07:30",
                    DurationSeconds = 11,
                    Enabled = true
                },
                new()
                {
                    Id = "bulk-api-untouched",
                    Name = "Bulk API untouched",
                    PresetName = "Bulk API scene",
                    TimeOfDay = "08:30",
                    Enabled = true
                }
            }
        });
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(hostedServices: new[] { service });

        var action = controller.SetSceneSchedulesEnabledBulk(new HueSceneScheduleBulkEnabledRequest
        {
            ScheduleIds = new List<string> { " bulk-api-one ", "bulk-api-two", "bulk-api-one" },
            Enabled = false
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueSceneScheduleBulkEnabledResult>(response.Value);
        Assert.False(result.Enabled);
        Assert.Equal(2, result.RequestedCount);
        Assert.Equal(2, result.UpdatedCount);
        Assert.Equal(new[] { "bulk-api-one", "bulk-api-two" }, result.Schedules.Select(schedule => schedule.Id));
        Assert.False(configuration.SceneSchedules[0].Enabled);
        Assert.False(configuration.SceneSchedules[1].Enabled);
        Assert.True(configuration.SceneSchedules[2].Enabled);
        Assert.Equal(7, configuration.SceneSchedules[0].DurationSeconds);
        Assert.Equal(11, configuration.SceneSchedules[1].DurationSeconds);
    }

    [Fact]
    public void SceneSchedules_BulkEnabledActionRefusesPartialEnableWhenCueIsExhausted()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Bulk guarded API scene" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "bulk-api-ready",
                    Name = "Bulk API ready",
                    PresetName = "Bulk guarded API scene",
                    Enabled = false
                },
                new()
                {
                    Id = "bulk-api-exhausted",
                    Name = "Bulk API exhausted",
                    PresetName = "Bulk guarded API scene",
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

        var action = controller.SetSceneSchedulesEnabledBulk(new HueSceneScheduleBulkEnabledRequest
        {
            ScheduleIds = new List<string> { "bulk-api-ready", "bulk-api-exhausted" },
            Enabled = true
        });

        var response = Assert.IsType<ConflictObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        var result = Assert.IsType<HueSceneScheduleBulkEnabledResult>(response.Value);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Contains("Bulk API exhausted", result.Message, StringComparison.Ordinal);
        Assert.False(configuration.SceneSchedules[0].Enabled);
        Assert.False(configuration.SceneSchedules[1].Enabled);
    }

    [Fact]
    public void SceneSchedules_BulkResetRunCountReenablesSelectedCuesAtomically()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Bulk reset API scene" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "bulk-reset-api-one",
                    Name = "Bulk reset API one",
                    PresetName = "Bulk reset API scene",
                    MaxRuns = 3,
                    RunCount = 3,
                    Enabled = false,
                    SkipNextOccurrence = true
                },
                new()
                {
                    Id = "bulk-reset-api-two",
                    Name = "Bulk reset API two",
                    PresetName = "Bulk reset API scene",
                    MaxRuns = 5,
                    RunCount = 2,
                    Enabled = false,
                    SkipNextOccurrence = true
                },
                new()
                {
                    Id = "bulk-reset-api-untouched",
                    Name = "Bulk reset API untouched",
                    PresetName = "Bulk reset API scene",
                    MaxRuns = 4,
                    RunCount = 1,
                    Enabled = false,
                    SkipNextOccurrence = true
                }
            }
        });
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(hostedServices: new[] { service });

        var action = controller.ResetSceneSchedulesRunCountBulk(new HueSceneScheduleBulkResetRunCountRequest
        {
            ScheduleIds = new List<string> { " bulk-reset-api-one ", "bulk-reset-api-two", "bulk-reset-api-one" }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueSceneScheduleBulkResetRunCountResult>(response.Value);
        Assert.Equal(2, result.RequestedCount);
        Assert.Equal(2, result.ResetCount);
        Assert.Equal(new[] { "bulk-reset-api-one", "bulk-reset-api-two" }, result.Schedules.Select(schedule => schedule.Id));
        Assert.All(configuration.SceneSchedules.Take(2), schedule =>
        {
            Assert.Equal(0, schedule.RunCount);
            Assert.True(schedule.Enabled);
            Assert.False(schedule.SkipNextOccurrence);
        });
        Assert.Equal(1, configuration.SceneSchedules[2].RunCount);
        Assert.False(configuration.SceneSchedules[2].Enabled);
        Assert.True(configuration.SceneSchedules[2].SkipNextOccurrence);
    }

    [Fact]
    public void SceneSchedules_BulkResetRunCountRefusesMissingCueWithoutMutation()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Bulk guarded reset API scene" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "bulk-reset-api-existing",
                    Name = "Bulk reset API existing",
                    PresetName = "Bulk guarded reset API scene",
                    MaxRuns = 3,
                    RunCount = 2,
                    Enabled = false,
                    SkipNextOccurrence = true
                },
                new()
                {
                    Id = "bulk-reset-api-safe",
                    Name = "Bulk reset API safe",
                    PresetName = "Bulk guarded reset API scene",
                    MaxRuns = 3,
                    RunCount = 1,
                    Enabled = false
                }
            }
        });
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(hostedServices: new[] { service });

        var action = controller.ResetSceneSchedulesRunCountBulk(new HueSceneScheduleBulkResetRunCountRequest
        {
            ScheduleIds = new List<string> { "bulk-reset-api-existing", "missing-reset-api-cue" }
        });

        var response = Assert.IsType<NotFoundObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status404NotFound, response.StatusCode);
        var result = Assert.IsType<HueSceneScheduleBulkResetRunCountResult>(response.Value);
        Assert.Equal(2, result.RequestedCount);
        Assert.Equal(new[] { "missing-reset-api-cue" }, result.MissingScheduleIds);
        Assert.Equal(2, configuration.SceneSchedules[0].RunCount);
        Assert.False(configuration.SceneSchedules[0].Enabled);
        Assert.True(configuration.SceneSchedules[0].SkipNextOccurrence);
    }

    [Fact]
    public void SceneSchedules_BulkResetRunCountPersistenceFailureRestoresAllSelectedCues()
    {
        var serializer = new Mock<IXmlSerializer>();
        serializer
            .Setup(xml => xml.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("bulk reset persistence failed"));
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Bulk failing reset API scene" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "bulk-reset-api-failing-one",
                    Name = "Bulk reset API failing one",
                    PresetName = "Bulk failing reset API scene",
                    MaxRuns = 3,
                    RunCount = 3,
                    Enabled = false,
                    SkipNextOccurrence = true
                },
                new()
                {
                    Id = "bulk-reset-api-failing-two",
                    Name = "Bulk reset API failing two",
                    PresetName = "Bulk failing reset API scene",
                    MaxRuns = 4,
                    RunCount = 2,
                    Enabled = false
                }
            }
        }, serializer.Object);

        var action = CreateController().ResetSceneSchedulesRunCountBulk(new HueSceneScheduleBulkResetRunCountRequest
        {
            ScheduleIds = new List<string> { "bulk-reset-api-failing-one", "bulk-reset-api-failing-two" }
        });

        var response = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, response.StatusCode);
        Assert.Equal(3, configuration.SceneSchedules[0].RunCount);
        Assert.False(configuration.SceneSchedules[0].Enabled);
        Assert.True(configuration.SceneSchedules[0].SkipNextOccurrence);
        Assert.Equal(2, configuration.SceneSchedules[1].RunCount);
        Assert.False(configuration.SceneSchedules[1].Enabled);
    }

    [Fact]
    public void SceneSchedules_BulkSkipNextActionUpdatesSelectedCuesAtomically()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Bulk skip API scene" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "bulk-skip-api-one",
                    Name = "Bulk skip API one",
                    PresetName = "Bulk skip API scene",
                    TimeOfDay = "06:30",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    Enabled = true
                },
                new()
                {
                    Id = "bulk-skip-api-two",
                    Name = "Bulk skip API two",
                    PresetName = "Bulk skip API scene",
                    TimeOfDay = "07:30",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    Enabled = true
                },
                new()
                {
                    Id = "bulk-skip-api-untouched",
                    Name = "Bulk skip API untouched",
                    PresetName = "Bulk skip API scene",
                    TimeOfDay = "08:30",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    Enabled = true
                }
            }
        });
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(hostedServices: new[] { service });

        var action = controller.SetSceneSchedulesSkipNextBulk(new HueSceneScheduleBulkSkipNextRequest
        {
            ScheduleIds = new List<string> { " bulk-skip-api-one ", "bulk-skip-api-two", "bulk-skip-api-one" },
            SkipNextOccurrence = true
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueSceneScheduleBulkSkipNextResult>(response.Value);
        Assert.True(result.SkipNextOccurrence);
        Assert.Equal(2, result.RequestedCount);
        Assert.Equal(2, result.UpdatedCount);
        Assert.Equal(new[] { "bulk-skip-api-one", "bulk-skip-api-two" }, result.Schedules.Select(schedule => schedule.Id));
        Assert.True(configuration.SceneSchedules[0].SkipNextOccurrence);
        Assert.True(configuration.SceneSchedules[1].SkipNextOccurrence);
        Assert.False(configuration.SceneSchedules[2].SkipNextOccurrence);

        var clearAction = controller.SetSceneSchedulesSkipNextBulk(new HueSceneScheduleBulkSkipNextRequest
        {
            ScheduleIds = new List<string> { "bulk-skip-api-one", "bulk-skip-api-two" },
            SkipNextOccurrence = false
        });

        var clearResponse = Assert.IsType<OkObjectResult>(clearAction.Result);
        var clearResult = Assert.IsType<HueSceneScheduleBulkSkipNextResult>(clearResponse.Value);
        Assert.False(clearResult.SkipNextOccurrence);
        Assert.Equal(2, clearResult.UpdatedCount);
        Assert.False(configuration.SceneSchedules[0].SkipNextOccurrence);
        Assert.False(configuration.SceneSchedules[1].SkipNextOccurrence);
    }

    [Fact]
    public void SceneSchedules_BulkSkipNextActionRefusesPartialSkipWhenCueIsDisabled()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Bulk guarded skip API scene" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "bulk-skip-api-ready",
                    Name = "Bulk skip API ready",
                    PresetName = "Bulk guarded skip API scene",
                    TimeOfDay = "06:30",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    Enabled = true
                },
                new()
                {
                    Id = "bulk-skip-api-disabled",
                    Name = "Bulk skip API disabled",
                    PresetName = "Bulk guarded skip API scene",
                    TimeOfDay = "07:30",
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

        var action = controller.SetSceneSchedulesSkipNextBulk(new HueSceneScheduleBulkSkipNextRequest
        {
            ScheduleIds = new List<string> { "bulk-skip-api-ready", "bulk-skip-api-disabled" },
            SkipNextOccurrence = true
        });

        var response = Assert.IsType<ConflictObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        var result = Assert.IsType<HueSceneScheduleBulkSkipNextResult>(response.Value);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Contains("Bulk skip API disabled", result.Message, StringComparison.Ordinal);
        Assert.False(configuration.SceneSchedules[0].SkipNextOccurrence);
        Assert.False(configuration.SceneSchedules[1].SkipNextOccurrence);
    }

    [Fact]
    public void SceneSchedules_BulkDeleteActionRemovesSelectedCuesAndPreservesHistory()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            PersistSceneScheduleHistory = true,
            PersistedSceneScheduleHistory = new List<HueSceneScheduleHistoryEntry>
            {
                new() { ScheduleId = "bulk-delete-api-one", ScheduleName = "Bulk delete API one" }
            },
            ColorPresets = new List<HueColorPreset> { new() { Name = "Bulk delete API scene" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "bulk-delete-api-one",
                    Name = "Bulk delete API one",
                    PresetName = "Bulk delete API scene",
                    TimeOfDay = "06:30"
                },
                new()
                {
                    Id = "bulk-delete-api-two",
                    Name = "Bulk delete API two",
                    PresetName = "Bulk delete API scene",
                    TimeOfDay = "07:30"
                },
                new()
                {
                    Id = "bulk-delete-api-untouched",
                    Name = "Bulk delete API untouched",
                    PresetName = "Bulk delete API scene",
                    TimeOfDay = "08:30"
                }
            }
        });
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(hostedServices: new[] { service });

        var action = controller.DeleteSceneSchedulesBulk(new HueSceneScheduleBulkDeleteRequest
        {
            ScheduleIds = new List<string> { " bulk-delete-api-one ", "bulk-delete-api-two", "bulk-delete-api-one" }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueSceneScheduleBulkDeleteResult>(response.Value);
        Assert.Equal(2, result.RequestedCount);
        Assert.Equal(2, result.DeletedCount);
        Assert.Equal(1, result.RemainingCount);
        Assert.Equal(new[] { "bulk-delete-api-one", "bulk-delete-api-two" }, result.Schedules.Select(schedule => schedule.Id));
        Assert.Single(configuration.SceneSchedules);
        Assert.Equal("bulk-delete-api-untouched", configuration.SceneSchedules[0].Id);
        Assert.Single(configuration.PersistedSceneScheduleHistory);
    }

    [Fact]
    public void SceneSchedules_BulkDeleteActionRefusesPartialDeletionWhenCueIsMissing()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Bulk guarded delete API scene" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "bulk-delete-api-existing",
                    Name = "Bulk delete API existing",
                    PresetName = "Bulk guarded delete API scene",
                    TimeOfDay = "06:30"
                },
                new()
                {
                    Id = "bulk-delete-api-safe",
                    Name = "Bulk delete API safe",
                    PresetName = "Bulk guarded delete API scene",
                    TimeOfDay = "07:30"
                }
            }
        });
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(hostedServices: new[] { service });

        var action = controller.DeleteSceneSchedulesBulk(new HueSceneScheduleBulkDeleteRequest
        {
            ScheduleIds = new List<string> { "bulk-delete-api-existing", "missing-delete-api-cue" }
        });

        var response = Assert.IsType<NotFoundObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status404NotFound, response.StatusCode);
        var result = Assert.IsType<HueSceneScheduleBulkDeleteResult>(response.Value);
        Assert.Equal(0, result.DeletedCount);
        Assert.Contains("missing-delete-api-cue", result.Message, StringComparison.Ordinal);
        Assert.Equal(2, configuration.SceneSchedules.Count);
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
                    TargetAllEnabledMappings = true,
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
        Assert.True(run.TargetAllEnabledMappings);
        var serialized = System.Text.Json.JsonSerializer.Serialize(document);
        Assert.Contains("\"targetAllEnabledMappings\":true", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("AppKey", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ClientKey", serialized, StringComparison.OrdinalIgnoreCase);

        var cleared = controller.ClearSceneScheduleHistory();
        var clearedResponse = Assert.IsType<OkObjectResult>(cleared.Result);
        var clearResult = Assert.IsType<HueSceneScheduleHistoryClearResult>(clearedResponse.Value);
        Assert.Equal(1, clearResult.ClearedCount);
        Assert.Empty(configuration.PersistedSceneScheduleHistory);
    }

    [Fact]
    public void SceneScheduleReadRoutes_DoNotPersistLazyHistoryRepairs()
    {
        var serializer = new Mock<IXmlSerializer>();
        var persistedHistory = new List<HueSceneScheduleHistoryEntry>
        {
            new()
            {
                ScheduleId = "stale-cue",
                ScheduleName = "Stale cue",
                Succeeded = true,
                RunAtUtc = DateTime.UtcNow
            }
        };
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            PersistSceneScheduleHistory = false,
            PersistedSceneScheduleHistory = persistedHistory
        }, serializer.Object);
        var sceneService = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(hostedServices: new IHostedService[] { sceneService });

        var statusAction = controller.GetSceneScheduleStatus();
        Assert.IsType<OkObjectResult>(statusAction.Result);
        var historyAction = controller.GetSceneScheduleHistory();
        var historyResponse = Assert.IsType<OkObjectResult>(historyAction.Result);
        var history = Assert.IsType<HueSceneScheduleHistoryResult>(historyResponse.Value);

        Assert.Empty(history.Runs);
        Assert.Same(persistedHistory, configuration.PersistedSceneScheduleHistory);
        Assert.Single(configuration.PersistedSceneScheduleHistory);
        serializer.Verify(
            xml => xml.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public void SceneScheduleHistory_FiltersOutcomeAndExportWithoutSecrets()
    {
        InstallConfiguration(new PluginConfiguration
        {
            PersistSceneScheduleHistory = true,
            PersistedSceneScheduleHistory = new List<HueSceneScheduleHistoryEntry>
            {
                new()
                {
                    ScheduleId = "success-cue",
                    ScheduleName = "Successful cue",
                    Succeeded = true,
                    RunAtUtc = DateTime.UtcNow.AddMinutes(-1)
                },
                new()
                {
                    ScheduleId = "failed-cue",
                    ScheduleName = "Failed cue",
                    Succeeded = false,
                    Skipped = false,
                    RunAtUtc = DateTime.UtcNow.AddMinutes(-2)
                },
                new()
                {
                    ScheduleId = "skipped-cue",
                    ScheduleName = "Skipped cue",
                    Succeeded = false,
                    Skipped = true,
                    RunAtUtc = DateTime.UtcNow.AddMinutes(-3)
                },
                new()
                {
                    ScheduleId = "recovered-cue",
                    ScheduleName = "Recovered cue",
                    Succeeded = true,
                    WasCatchUp = true,
                    RunAtUtc = DateTime.UtcNow.AddMinutes(-4)
                }
            }
        });
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(hostedServices: new IHostedService[] { service });

        var failedAction = controller.GetSceneScheduleHistory(outcome: " Failed ");
        var failedResponse = Assert.IsType<OkObjectResult>(failedAction.Result);
        var failedDocument = Assert.IsType<HueSceneScheduleHistoryResult>(failedResponse.Value);
        Assert.Equal("Failed", failedDocument.OutcomeFilter);
        var failedRun = Assert.Single(failedDocument.Runs);
        Assert.Equal("failed-cue", failedRun.ScheduleId);
        Assert.False(failedRun.Succeeded);
        Assert.False(failedRun.Skipped);
        Assert.False(failedRun.TargetAllEnabledMappings);

        var skippedAction = controller.GetSceneScheduleHistory(outcome: "Skipped");
        var skippedResponse = Assert.IsType<OkObjectResult>(skippedAction.Result);
        var skippedDocument = Assert.IsType<HueSceneScheduleHistoryResult>(skippedResponse.Value);
        Assert.Single(skippedDocument.Runs);
        Assert.True(skippedDocument.Runs[0].Skipped);

        var recoveredAction = controller.ExportSceneScheduleHistory(outcome: "Recovered");
        var recoveredResponse = Assert.IsType<OkObjectResult>(recoveredAction.Result);
        var recoveredDocument = Assert.IsType<HueSceneScheduleHistoryResult>(recoveredResponse.Value);
        Assert.Equal("Recovered", recoveredDocument.OutcomeFilter);
        var recoveredRun = Assert.Single(recoveredDocument.Runs);
        Assert.Equal("recovered-cue", recoveredRun.ScheduleId);
        Assert.True(recoveredRun.WasCatchUp);

        var focusedAction = controller.GetSceneScheduleHistory(
            scheduleId: " failed-cue ",
            outcome: " Failed ");
        var focusedResponse = Assert.IsType<OkObjectResult>(focusedAction.Result);
        var focusedDocument = Assert.IsType<HueSceneScheduleHistoryResult>(focusedResponse.Value);
        Assert.Equal("failed-cue", focusedDocument.ScheduleIdFilter);
        Assert.Equal("Failed", focusedDocument.OutcomeFilter);
        var focusedRun = Assert.Single(focusedDocument.Runs);
        Assert.Equal("failed-cue", focusedRun.ScheduleId);
        Assert.False(focusedRun.Succeeded);
        var serialized = System.Text.Json.JsonSerializer.Serialize(recoveredDocument);
        Assert.DoesNotContain("AppKey", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ClientKey", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CsvExports_PreserveFiltersEscapingAndCredentialFreeHeaders()
    {
        var cueTime = DateTime.Now.AddMinutes(10).ToString("HH:mm", CultureInfo.InvariantCulture);
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            PersistSceneScheduleHistory = true,
            PersistedSceneScheduleHistory = new List<HueSceneScheduleHistoryEntry>
            {
                new()
                {
                    ScheduleId = "csv-cue",
                    ScheduleName = "CSV, \"Cue\"",
                    PresetName = "CSV scene",
                    TargetLabel = "Living Room",
                    BrightnessPercent = 48,
                    Red = 41,
                    Green = 42,
                    Blue = 43,
                    TargetAllEnabledMappings = true,
                    TargetUserIds = new List<string> { "mapping-user" },
                    TargetRoutes = new List<HueSceneScheduleTargetRoute>
                    {
                        new() { UserId = "mapping-user", DeviceId = "living-room-tv" }
                    },
                    IncludeDefaultTarget = true,
                    Succeeded = true,
                    Message = "Message, with \"quotes\"",
                    CleanupWarning = "=FORMULA()",
                    RunAtUtc = DateTime.UtcNow,
                    RunCount = 2
                }
            },
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "CSV scene", DurationSeconds = 12 },
                new() { Name = "Short CSV scene", DurationSeconds = 4 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "csv-cue",
                    Name = "CSV, \"Cue\"",
                    PresetName = "CSV scene",
                    TimeOfDay = cueTime,
                    TimeZoneId = TimeZoneInfo.Local.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    BrightnessPercent = 47,
                    Red = 51,
                    Green = 52,
                    Blue = 53,
                    TargetUserIds = new List<string> { "mapping-user" },
                    TargetRoutes = new List<HueSceneScheduleTargetRoute>
                    {
                        new() { UserId = "mapping-user", DeviceId = "living-room-tv" }
                    },
                    IncludeDefaultTarget = true,
                    Enabled = true
                },
                new()
                {
                    Id = "csv-short-cue",
                    Name = "CSV short cue",
                    PresetName = "Short CSV scene",
                    TimeOfDay = cueTime,
                    TimeZoneId = TimeZoneInfo.Local.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    Enabled = true
                }
            }
        });
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(hostedServices: new IHostedService[] { service });

        static string ReadCsv(FileContentResult result)
            => Encoding.UTF8.GetString(result.FileContents).TrimStart('\uFEFF');

        var occurrenceFile = Assert.IsType<FileContentResult>(controller.ExportSceneScheduleOccurrencesCsv(10, 7, "csv-cue"));
        Assert.Equal("text/csv; charset=utf-8", occurrenceFile.ContentType);
        Assert.Equal("jellyfin-hue-scene-schedule-occurrences.csv", occurrenceFile.FileDownloadName);
        var occurrenceCsv = ReadCsv(occurrenceFile);
        Assert.StartsWith("\"scheduleId\",\"scheduleName\"", occurrenceCsv, StringComparison.Ordinal);
        Assert.Contains("\"red\",\"green\",\"blue\"", occurrenceCsv, StringComparison.Ordinal);
        Assert.Contains("\"brightnessPercent\"", occurrenceCsv, StringComparison.Ordinal);
        Assert.Contains("\"47\"", occurrenceCsv, StringComparison.Ordinal);
        Assert.Contains("\"51\"", occurrenceCsv, StringComparison.Ordinal);
        Assert.Contains("\"52\"", occurrenceCsv, StringComparison.Ordinal);
        Assert.Contains("\"53\"", occurrenceCsv, StringComparison.Ordinal);
        Assert.Contains("\"targetUserIds\",\"targetRoutes\",\"includeDefaultTarget\"", occurrenceCsv, StringComparison.Ordinal);
        Assert.Contains("\"[\"\"mapping-user\"\"]\"", occurrenceCsv, StringComparison.Ordinal);
        Assert.Contains("\"[{\"\"userId\"\":\"\"mapping-user\"\",\"\"deviceId\"\":\"\"living-room-tv\"\"}]\"", occurrenceCsv, StringComparison.Ordinal);
        Assert.Contains("\"True\"", occurrenceCsv, StringComparison.Ordinal);
        Assert.Contains("\"csv-cue\",\"CSV, \"\"Cue\"\"\"", occurrenceCsv, StringComparison.Ordinal);
        Assert.DoesNotContain("AppKey", occurrenceCsv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ClientKey", occurrenceCsv, StringComparison.OrdinalIgnoreCase);

        var conflictFile = Assert.IsType<FileContentResult>(controller.ExportSceneScheduleConflictsCsv(10, 7));
        var conflictCsv = ReadCsv(conflictFile);
        Assert.StartsWith("\"firstScheduleId\",\"firstScheduleName\"", conflictCsv, StringComparison.Ordinal);
        Assert.Contains("\"overlapSeconds\"", conflictCsv, StringComparison.Ordinal);
        Assert.Contains("csv-cue", conflictCsv, StringComparison.Ordinal);

        var historyFile = Assert.IsType<FileContentResult>(controller.ExportSceneScheduleHistoryCsv(10, "csv-cue", "Succeeded"));
        var historyCsv = ReadCsv(historyFile);
        Assert.StartsWith("\"scheduleId\",\"scheduleName\"", historyCsv, StringComparison.Ordinal);
        Assert.Contains("\"red\",\"green\",\"blue\"", historyCsv, StringComparison.Ordinal);
        Assert.Contains("\"brightnessPercent\"", historyCsv, StringComparison.Ordinal);
        Assert.Contains("\"48\"", historyCsv, StringComparison.Ordinal);
        Assert.Contains("\"41\"", historyCsv, StringComparison.Ordinal);
        Assert.Contains("\"42\"", historyCsv, StringComparison.Ordinal);
        Assert.Contains("\"43\"", historyCsv, StringComparison.Ordinal);
        Assert.Contains("\"targetAllEnabledMappings\",\"targetUserIds\",\"targetRoutes\",\"includeDefaultTarget\"", historyCsv, StringComparison.Ordinal);
        Assert.Contains("\"True\",\"[\"\"mapping-user\"\"]\"", historyCsv, StringComparison.Ordinal);
        Assert.Contains("\"[\"\"mapping-user\"\"]\"", historyCsv, StringComparison.Ordinal);
        Assert.Contains("\"[{\"\"userId\"\":\"\"mapping-user\"\",\"\"deviceId\"\":\"\"living-room-tv\"\"}]\"", historyCsv, StringComparison.Ordinal);
        Assert.Contains("\"True\"", historyCsv, StringComparison.Ordinal);
        Assert.Contains("\"Message, with \"\"quotes\"\"\"", historyCsv, StringComparison.Ordinal);
        Assert.Contains("\"csv-cue\",\"CSV, \"\"Cue\"\"\"", historyCsv, StringComparison.Ordinal);
        Assert.Contains("\"'=FORMULA()\"", historyCsv, StringComparison.Ordinal);

        var sessionFile = Assert.IsType<FileContentResult>(controller.ExportSessionHistoryCsv(10));
        var sessionCsv = ReadCsv(sessionFile);
        Assert.StartsWith("\"outcome\",\"item\",\"userId\"", sessionCsv, StringComparison.Ordinal);
        Assert.DoesNotContain("csv-cue", sessionCsv, StringComparison.Ordinal);
        Assert.DoesNotContain("AppKey", sessionCsv, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, configuration.SceneSchedules.Count);
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
    public void DeleteColorPreset_ProtectsPlaylistReferencesAndReportsAllDependencies()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Accent" } },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new() { Id = "playlist-1", Name = "Accent sequence", PresetNames = new List<string> { "Accent" } }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "cue-1", Name = "Accent cue", PresetName = "Accent" }
            }
        });
        var controller = CreateController();

        var action = controller.DeleteColorPreset(" accent ");

        var response = Assert.IsType<ConflictObjectResult>(action);
        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        var message = Assert.IsType<string>(response.Value);
        Assert.Contains("1 playlist(s)", message, StringComparison.Ordinal);
        Assert.Contains("1 scheduled cue(s)", message, StringComparison.Ordinal);
        Assert.Single(configuration.ColorPresets);

        Assert.IsType<OkObjectResult>(controller.DeleteSceneSchedule("cue-1"));
        Assert.IsType<OkObjectResult>(controller.DeleteScenePlaylist("Accent sequence"));
        Assert.IsType<OkObjectResult>(controller.DeleteColorPreset("Accent"));
        Assert.Empty(configuration.ColorPresets);
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
    public async Task SceneSchedulesBulkRun_RunsSelectedCuesInOrderAndKeepsResultsCredentialFree()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "bulk-run-app-secret",
            HueClientKey = "bulk-run-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Warm", Red = 25, Green = 50, Blue = 75, DurationSeconds = 1 },
                new() { Name = "Cool", Red = 220, Green = 180, Blue = 140, DurationSeconds = 1 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "bulk-run-warm",
                    Name = "Warm cue",
                    PresetName = "Warm",
                    TimeOfDay = "20:00",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0
                },
                new()
                {
                    Id = "bulk-run-cool",
                    Name = "Cool cue",
                    PresetName = "Cool",
                    TimeOfDay = "20:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0
                }
            }
        });
        SetupHttpResponse(
            HttpStatusCode.OK,
            "{\"data\":[{\"channels\":[{\"channel_id\":0}]}]}");
        var streamTester = new Mock<IHueStreamTester>();
        streamTester
            .Setup(tester => tester.PreviewAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<JsonElement>(), It.IsAny<IReadOnlySet<int>?>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new HueStreamProbeResult { Succeeded = true, Message = "Bulk cue completed." });
        var service = new HueSceneAutomationService(
            streamTester.Object,
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(streamTester.Object, hostedServices: new[] { service });

        var action = await controller.RunSceneSchedulesBulk(new HueSceneScheduleBulkRunRequest
        {
            ScheduleIds = new List<string> { "bulk-run-cool", "bulk-run-warm", "bulk-run-cool" }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueSceneScheduleBulkRunResult>(response.Value);
        Assert.Equal(2, result.RequestedCount);
        Assert.Equal(2, result.CompletedCount);
        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);
        Assert.False(result.Canceled);
        Assert.Equal(new[] { "Cool cue", "Warm cue" }, result.Results.Select(run => run.ScheduleName));
        Assert.Equal(new[] { 220, 25 }, streamTester.Invocations
            .Where(invocation => invocation.Method.Name == nameof(IHueStreamTester.PreviewAsync))
            .Select(invocation => (int)invocation.Arguments[6]!)
            .ToArray());
        Assert.All(configuration.SceneSchedules, schedule => Assert.Equal(1, schedule.RunCount));
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("bulk-run-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("bulk-run-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SceneSchedulesBulkRun_PreflightsEveryCueBeforeBridgeActivity()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "bulk-preflight-app-secret",
            HueClientKey = "bulk-preflight-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset> { new() { Name = "Valid", DurationSeconds = 1 } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "bulk-preflight-invalid",
                    Name = "Invalid cue",
                    PresetName = "Missing",
                    TimeOfDay = "20:00",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0
                }
            }
        });
        var controller = CreateController(Mock.Of<IHueStreamTester>());

        var missing = await controller.RunSceneSchedulesBulk(new HueSceneScheduleBulkRunRequest
        {
            ScheduleIds = new List<string> { "bulk-preflight-invalid", "not-present" }
        });
        var missingResponse = Assert.IsType<NotFoundObjectResult>(missing.Result);
        var missingResult = Assert.IsType<HueSceneScheduleBulkRunResult>(missingResponse.Value);
        Assert.Equal(new[] { "not-present" }, missingResult.MissingScheduleIds);

        var invalid = await controller.RunSceneSchedulesBulk(new HueSceneScheduleBulkRunRequest
        {
            ScheduleIds = new List<string> { "bulk-preflight-invalid" }
        });
        var invalidResponse = Assert.IsType<BadRequestObjectResult>(invalid.Result);
        var invalidResult = Assert.IsType<HueSceneScheduleBulkRunResult>(invalidResponse.Value);
        Assert.NotEmpty(invalidResult.ValidationErrors);
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public void SceneSchedulesBulkCancel_ReturnsCredentialFreeNoActiveSummary()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "bulk-cancel-one", Name = "First cue", PresetName = "Scene" },
                new() { Id = "bulk-cancel-two", Name = "Second cue", PresetName = "Scene" }
            }
        });
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(hostedServices: new[] { service });

        var action = controller.CancelSceneSchedulesBulk(new HueSceneScheduleBulkCancelRequest
        {
            ScheduleIds = new List<string> { "bulk-cancel-one", "bulk-cancel-two", "bulk-cancel-one" }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueSceneScheduleBulkCancelResult>(response.Value);
        Assert.Equal(2, result.RequestedCount);
        Assert.Equal(0, result.CanceledCount);
        Assert.Empty(result.CanceledScheduleIds);
        Assert.Contains("no manually", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, configuration.SceneSchedules.Count);
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
        Assert.Equal(PluginConfiguration.PlaybackMediaFilterAllVideo, status.ConfiguredPlaybackMediaFilter);
        Assert.Equal("Unavailable", status.State);
        Assert.Null(status.ActiveBridgeIp);
        Assert.Null(status.ActiveEntertainmentAreaId);
        Assert.Null(status.ActiveDeviceId);
        Assert.Null(status.ActiveDeviceName);
        Assert.Null(status.ActiveDeviceRouteMatched);
        Assert.Null(status.ActiveTargetFps);
        Assert.Null(status.ActiveAudioSensitivityPercent);
        Assert.Null(status.ActiveAudioLowFrequencyHz);
        Assert.Null(status.ActiveAudioMidFrequencyHz);
        Assert.Null(status.ActiveAudioHighFrequencyHz);
        Assert.Null(status.ActiveAudioLowGainPercent);
        Assert.Null(status.ActiveAudioMidGainPercent);
        Assert.Null(status.ActiveAudioHighGainPercent);
        Assert.Null(status.ActiveAudioResponseSmoothingPercent);
        Assert.Null(status.ActiveAudioBandSpreadPercent);
        Assert.Null(status.ActiveAudioBeatPulsePercent);
        Assert.Null(status.ActiveAudioBeatPulseThresholdPercent);
        Assert.Null(status.ActiveAudioColorPalette);
        Assert.Null(status.ActiveAudioSpatialMode);
        Assert.Null(status.ActiveAudioChannelMode);
        Assert.Null(status.ActiveFrameResolution);
        Assert.Null(status.ActiveVideoScalingMode);
        Assert.Null(status.ActiveVideoDeinterlaceMode);
        Assert.Null(status.ActiveSamplingBreadthPercent);
        Assert.Null(status.ActiveSamplingMode);
        Assert.Null(status.ActiveSpatialOrientation);
        Assert.Null(status.ActiveColorSmoothingPercent);
        Assert.Null(status.ActiveBrightnessBoost);
        Assert.Null(status.ActiveRedGain);
        Assert.Null(status.ActiveGreenGain);
        Assert.Null(status.ActiveBlueGain);
        Assert.Null(status.ActiveColorSaturation);
        Assert.Null(status.ActiveHueShiftDegrees);
        Assert.Null(status.ActiveOutputBrightnessPercent);
        Assert.Null(status.ActiveGammaCorrection);
        Assert.Null(status.ActiveContrastPercent);
        Assert.Null(status.ActiveColorTemperatureKelvin);
        Assert.Null(status.ActiveBlackoutThreshold);
        Assert.Null(status.ActiveBlackoutBehavior);
        Assert.Null(status.ActiveColorChangeThreshold);
        Assert.Null(status.ActiveUseGpu);
        Assert.Null(status.ActiveCustomFfmpegFlagsConfigured);
        Assert.Null(status.ActiveFfmpegStallTimeoutSeconds);
        Assert.Null(status.ActiveNetworkRetryAttempts);
        Assert.Null(status.ActiveChannelIds);
        Assert.Null(status.ActiveRestoreLightState);
        Assert.Null(status.ActivePauseBehavior);
        Assert.Null(status.ActivePauseBrightnessPercent);
        Assert.Null(status.EffectiveFps);
        Assert.Equal(0, status.PacketsSent);
        Assert.Equal(0, status.PacketsSkippedByThreshold);
        Assert.Equal(0, status.PacketSendFailures);
        Assert.Equal(0, status.ReconnectAttempts);
        Assert.Equal(0, status.SeekRestartCount);
        Assert.Null(status.LastSeekPositionSeconds);
        Assert.Null(status.PlaybackPositionSeconds);
        Assert.Null(status.PlaybackDurationSeconds);
        Assert.Null(status.PlaybackProgressPercent);
        Assert.Null(status.PlaybackIsPaused);
        Assert.Null(status.PlaybackObservedAtUtc);
        Assert.Null(status.LastError);
        Assert.Null(status.CleanupWarning);
        Assert.Null(status.LastSession);
        Assert.False(status.CanStopSync);
    }

    [Fact]
    public async Task GetStatus_ProjectsPauseTelemetryIntoSupportBundleRuntime()
    {
        InstallConfiguration(new PluginConfiguration());
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory
            .Setup(factory => factory.CreateLogger(It.IsAny<string>()))
            .Returns(Mock.Of<ILogger>());
        var service = new HueSyncService(
            Mock.Of<ISessionManager>(),
            Mock.Of<ILogger<HueSyncService>>(),
            loggerFactory.Object,
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<IMediaEncoder>());
        SetPrivateField(service, "_activePauseBehavior", PluginConfiguration.PauseBehaviorDimToCinemaLevel);
        SetPrivateField(service, "_activePauseBrightnessPercent", 25);
        SetPrivateField(service, "_currentPlaySessionId", "timeline-session");
        SetPrivateField(service, "_currentItemName", "Feature film");
        SetPrivateField(service, "_currentPlaybackPositionTicks", TimeSpan.FromSeconds(90).Ticks);
        SetPrivateField(service, "_currentPlaybackDurationTicks", TimeSpan.FromMinutes(10).Ticks);
        SetPrivateField(service, "_currentPlaybackIsPaused", true);
        var playbackObservedAtUtc = DateTime.UtcNow.AddSeconds(-2);
        SetPrivateField(service, "_currentPlaybackObservedAtUtc", playbackObservedAtUtc);
        using var syncCts = new CancellationTokenSource();
        var syncStartedAtUtc = DateTime.UtcNow.AddMinutes(-3);
        SetPrivateField(service, "_syncCts", syncCts);
        SetPrivateField(service, "_syncStartTime", syncStartedAtUtc);
        var environmentProbe = new Mock<IHueEnvironmentProbe>();
        environmentProbe
            .Setup(probe => probe.CheckAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HueEnvironmentProbeResult
            {
                Ffmpeg = new HueToolStatus { Available = true, Version = "ffmpeg test" },
                OpenSsl = new HueToolStatus { Available = true, Version = "openssl test" }
            });
        var controller = CreateController(
            environmentProbe: environmentProbe.Object,
            hostedServices: new IHostedService[] { service });

        var statusAction = controller.GetStatus();
        var statusResponse = Assert.IsType<OkObjectResult>(statusAction.Result);
        var status = Assert.IsType<HueSyncStatus>(statusResponse.Value);
        Assert.True(status.ServiceAvailable);
        Assert.Equal(PluginConfiguration.PauseBehaviorDimToCinemaLevel, status.ActivePauseBehavior);
        Assert.Equal(25, status.ActivePauseBrightnessPercent);
        Assert.Equal(90, status.PlaybackPositionSeconds);
        Assert.Equal(600, status.PlaybackDurationSeconds);
        Assert.Equal(15, status.PlaybackProgressPercent);
        Assert.True(status.PlaybackIsPaused);
        Assert.Equal(playbackObservedAtUtc, status.PlaybackObservedAtUtc);
        Assert.Equal(syncStartedAtUtc, status.SyncStartedAtUtc);

        var bundleAction = await controller.ExportSupportBundle();
        var bundleResponse = Assert.IsType<OkObjectResult>(bundleAction.Result);
        var bundle = Assert.IsType<HueSupportBundle>(bundleResponse.Value);
        Assert.Equal(PluginConfiguration.PauseBehaviorDimToCinemaLevel, bundle.Runtime.ActivePauseBehavior);
        Assert.Equal(25, bundle.Runtime.ActivePauseBrightnessPercent);
        Assert.Equal(90, bundle.Runtime.PlaybackPositionSeconds);
        Assert.Equal(600, bundle.Runtime.PlaybackDurationSeconds);
        Assert.Equal(15, bundle.Runtime.PlaybackProgressPercent);
        Assert.True(bundle.Runtime.PlaybackIsPaused);
        Assert.Equal(playbackObservedAtUtc, bundle.Runtime.PlaybackObservedAtUtc);
        Assert.Equal(syncStartedAtUtc, bundle.Runtime.SyncStartedAtUtc);
        var serialized = JsonSerializer.Serialize(bundle);
        Assert.Contains("\"ActivePauseBehavior\":\"DimToCinemaLevel\"", serialized, StringComparison.Ordinal);
        Assert.Contains("\"ActivePauseBrightnessPercent\":25", serialized, StringComparison.Ordinal);
        Assert.Contains("\"PlaybackObservedAtUtc\"", serialized, StringComparison.Ordinal);
        Assert.Contains("\"SyncStartedAtUtc\"", serialized, StringComparison.Ordinal);
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
                    BrightnessPercent = 46,
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
        Assert.Equal(46, schedule.BrightnessPercent);
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
                new() { Name = "Evening", Effect = PluginConfiguration.ColorPresetEffectPulse, EffectSpeedPercent = 175, Red = 10, Green = 20, Blue = 30, TransitionSeconds = 2, TransitionOutSeconds = 3 }
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
                    BrightnessPercent = 44,
                    Red = 101,
                    Green = 102,
                    Blue = 103,
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
        Assert.Contains(result.Occurrences, occurrence => occurrence.ScheduleId == "cue-1" && occurrence.BrightnessPercent == 44);
        Assert.Contains(result.Occurrences, occurrence => occurrence.ScheduleId == "cue-1" && occurrence.Red == 101 && occurrence.Green == 102 && occurrence.Blue == 103);
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
    public void GetSceneScheduleOccurrences_PreservesTargetSelectionMetadataWithoutSecrets()
    {
        var cueTime = DateTime.Now.AddMinutes(10).ToString("HH:mm", CultureInfo.InvariantCulture);
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "occurrence-target-app-secret",
            HueClientKey = "occurrence-target-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Target metadata scene", DurationSeconds = 5 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "all-targets-cue",
                    Name = "All targets cue",
                    PresetName = "Target metadata scene",
                    TargetAllEnabledMappings = true,
                    TimeOfDay = cueTime,
                    TimeZoneId = TimeZoneInfo.Local.Id,
                    DaysOfWeekMask = 127
                },
                new()
                {
                    Id = "selected-targets-cue",
                    Name = "Selected targets cue",
                    PresetName = "Target metadata scene",
                    TargetUserIds = new List<string> { "mapping-user" },
                    TargetRoutes = new List<HueSceneScheduleTargetRoute>
                    {
                        new() { UserId = "mapping-user", DeviceId = "living-room-tv" }
                    },
                    IncludeDefaultTarget = true,
                    TimeOfDay = cueTime,
                    TimeZoneId = TimeZoneInfo.Local.Id,
                    DaysOfWeekMask = 127
                }
            }
        });
        var controller = CreateController();

        var allAction = controller.GetSceneScheduleOccurrences(limit: 1, days: 7, scheduleId: "all-targets-cue");
        var allResponse = Assert.IsType<OkObjectResult>(allAction.Result);
        var allOccurrence = Assert.Single(Assert.IsType<HueSceneScheduleOccurrencesResult>(allResponse.Value).Occurrences);
        Assert.True(allOccurrence.TargetAllEnabledMappings);
        Assert.Empty(allOccurrence.TargetUserIds);
        Assert.Empty(allOccurrence.TargetRoutes);
        Assert.False(allOccurrence.IncludeDefaultTarget);

        var selectedAction = controller.GetSceneScheduleOccurrences(limit: 1, days: 7, scheduleId: "selected-targets-cue");
        var selectedResponse = Assert.IsType<OkObjectResult>(selectedAction.Result);
        var selectedOccurrence = Assert.Single(Assert.IsType<HueSceneScheduleOccurrencesResult>(selectedResponse.Value).Occurrences);
        Assert.False(selectedOccurrence.TargetAllEnabledMappings);
        Assert.Equal(new[] { "mapping-user" }, selectedOccurrence.TargetUserIds);
        var selectedRoute = Assert.Single(selectedOccurrence.TargetRoutes);
        Assert.Equal("mapping-user", selectedRoute.UserId);
        Assert.Equal("living-room-tv", selectedRoute.DeviceId);
        Assert.True(selectedOccurrence.IncludeDefaultTarget);

        var serialized = JsonSerializer.Serialize(new[] { allOccurrence, selectedOccurrence });
        Assert.DoesNotContain("occurrence-target-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("occurrence-target-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSceneScheduleOccurrences_ExpandsPlaylistStepPlanAndExportsIt()
    {
        var cueTime = DateTime.Now.AddMinutes(10).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "playlist-occurrence-app-secret",
            HueClientKey = "playlist-occurrence-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Warm", Effect = PluginConfiguration.ColorPresetEffectPulse, Red = 240, Green = 120, Blue = 40, EffectSpeedPercent = 150, BrightnessPercent = 80, DurationSeconds = 12, TransitionSeconds = 4, TransitionOutSeconds = 3 },
                new() { Name = "Cool", Effect = PluginConfiguration.ColorPresetEffectRainbow, Red = 20, Green = 30, Blue = 90, EffectSpeedPercent = 275, BrightnessPercent = 60, DurationSeconds = 8, TransitionSeconds = 2, TransitionOutSeconds = 2 }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "occurrence-playlist",
                    Name = "Occurrence sequence",
                    PresetNames = new List<string> { "Warm", "Cool" },
                    StepDurationSeconds = new List<int> { 3, 0 },
                    StepRed = new List<int?> { 200, null },
                    StepGreen = new List<int?> { 100, null },
                    StepBlue = new List<int?> { 50, null },
                    StepBrightnessPercent = new List<int?> { 25, null },
                    StepEffects = new List<string?> { PluginConfiguration.ColorPresetEffectStarlight, null },
                    StepEffectSpeedPercent = new List<int?> { 225, null },
                    RepeatCount = 2,
                    PlaybackOrder = PluginConfiguration.ScenePlaylistOrderSequential
                }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "playlist-occurrence-cue",
                    Name = "Playlist occurrence cue",
                    PlaylistName = "Occurrence sequence",
                    TimeOfDay = cueTime,
                    TimeZoneId = TimeZoneInfo.Local.Id,
                    DaysOfWeekMask = 127
                }
            }
        });
        var controller = CreateController();

        var action = controller.GetSceneScheduleOccurrences(limit: 1, days: 7);
        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueSceneScheduleOccurrencesResult>(response.Value);
        var occurrence = Assert.Single(result.Occurrences);
        Assert.Equal("playlist-occurrence-cue", occurrence.ScheduleId);
        Assert.Equal(22, occurrence.PlaylistTotalDurationSeconds);
        Assert.Equal(new[] { "Warm", "Cool", "Warm", "Cool" }, occurrence.PlaylistSteps.Select(step => step.PresetName));
        Assert.Equal(new[] { 200, 20, 200, 20 }, occurrence.PlaylistSteps.Select(step => step.Red));
        Assert.Equal(new[] { 100, 30, 100, 30 }, occurrence.PlaylistSteps.Select(step => step.Green));
        Assert.Equal(new[] { 50, 90, 50, 90 }, occurrence.PlaylistSteps.Select(step => step.Blue));
        Assert.Equal(new[] { 25, 60, 25, 60 }, occurrence.PlaylistSteps.Select(step => step.BrightnessPercent));
        Assert.Equal(
            new[]
            {
                PluginConfiguration.ColorPresetEffectStarlight,
                PluginConfiguration.ColorPresetEffectRainbow,
                PluginConfiguration.ColorPresetEffectStarlight,
                PluginConfiguration.ColorPresetEffectRainbow
            },
            occurrence.PlaylistSteps.Select(step => step.Effect));
        Assert.Equal(new[] { 225, 275, 225, 275 }, occurrence.PlaylistSteps.Select(step => step.EffectSpeedPercent));
        Assert.Equal(new[] { 3, 8, 3, 8 }, occurrence.PlaylistSteps.Select(step => step.DurationSeconds));
        Assert.Equal(new[] { 0, 3, 11, 14 }, occurrence.PlaylistSteps.Select(step => step.StartOffsetSeconds));
        Assert.DoesNotContain("playlist-occurrence-app-secret", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.DoesNotContain("playlist-occurrence-client-secret", JsonSerializer.Serialize(result), StringComparison.Ordinal);

        var csv = Assert.IsType<FileContentResult>(controller.ExportSceneScheduleOccurrencesCsv(1, 7, "playlist-occurrence-cue"));
        var csvText = Encoding.UTF8.GetString(csv.FileContents).TrimStart('\uFEFF');
        Assert.Contains("\"playlistSteps\"", csvText, StringComparison.Ordinal);
        Assert.Contains("Warm", csvText, StringComparison.Ordinal);
        var normalizedCsvText = csvText.Replace("\"\"", "\"", StringComparison.Ordinal);
        Assert.Contains("\"red\":200", normalizedCsvText, StringComparison.Ordinal);
        Assert.Contains("\"green\":100", normalizedCsvText, StringComparison.Ordinal);
        Assert.Contains("\"blue\":50", normalizedCsvText, StringComparison.Ordinal);
        Assert.Contains("\"effect\":\"Starlight\"", normalizedCsvText, StringComparison.Ordinal);
        Assert.Contains("\"effect\":\"Rainbow\"", normalizedCsvText, StringComparison.Ordinal);
        Assert.Contains("\"effectSpeedPercent\":225", normalizedCsvText, StringComparison.Ordinal);
        Assert.Contains("\"effectSpeedPercent\":275", normalizedCsvText, StringComparison.Ordinal);
        Assert.DoesNotContain("playlist-occurrence-app-secret", csvText, StringComparison.Ordinal);

        var calendar = Assert.IsType<FileContentResult>(controller.GetSceneScheduleCalendar(1, 7, "playlist-occurrence-cue"));
        var calendarText = Encoding.UTF8.GetString(calendar.FileContents);
        Assert.Contains("X-HUE-PLAYLIST-STEP-PLAN:", calendarText, StringComparison.Ordinal);
        Assert.Contains("Warm", calendarText, StringComparison.Ordinal);
        var unfoldedCalendarText = calendarText.Replace("\r\n ", string.Empty, StringComparison.Ordinal);
        Assert.Contains("\"red\":200", unfoldedCalendarText, StringComparison.Ordinal);
        Assert.Contains("\"green\":100", unfoldedCalendarText, StringComparison.Ordinal);
        Assert.Contains("\"blue\":50", unfoldedCalendarText, StringComparison.Ordinal);
        Assert.Contains("\"effect\":\"Starlight\"", unfoldedCalendarText, StringComparison.Ordinal);
        Assert.Contains("\"effect\":\"Rainbow\"", unfoldedCalendarText, StringComparison.Ordinal);
        Assert.Contains("\"effectSpeedPercent\":225", unfoldedCalendarText, StringComparison.Ordinal);
        Assert.Contains("\"effectSpeedPercent\":275", unfoldedCalendarText, StringComparison.Ordinal);
        Assert.DoesNotContain("playlist-occurrence-client-secret", calendarText, StringComparison.Ordinal);
        Assert.Single(configuration.SceneSchedules);
    }

    [Fact]
    public void GetSceneScheduleConflicts_ReturnsBoundedDurationAwareReportWithoutSecrets()
    {
        var cueTime = DateTime.UtcNow.AddMinutes(10).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "conflict-api-app-secret",
            HueClientKey = "conflict-api-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Long scene", DurationSeconds = 12 },
                new() { Name = "Short scene", DurationSeconds = 4 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "api-long-cue",
                    Name = "API long cue",
                    PresetName = "Long scene",
                    TimeOfDay = cueTime,
                    TimeZoneId = TimeZoneInfo.Local.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    Priority = 90,
                    MaxRuns = 1,
                    Enabled = true
                },
                new()
                {
                    Id = "api-short-cue",
                    Name = "API short cue",
                    PresetName = "Short scene",
                    TimeOfDay = cueTime,
                    TimeZoneId = TimeZoneInfo.Local.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    Priority = 10,
                    MaxRuns = 1,
                    Enabled = true
                }
            }
        });

        var action = CreateController().GetSceneScheduleConflicts(limit: 7, days: 9);

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueSceneScheduleConflictsResult>(response.Value);
        Assert.False(result.ServiceAvailable);
        Assert.Equal(7, result.Limit);
        Assert.Equal(9, result.HorizonDays);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("api-long-cue", conflict.FirstScheduleId);
        Assert.Equal("api-short-cue", conflict.SecondScheduleId);
        Assert.Equal(4, conflict.OverlapSeconds);
        Assert.Contains("higher priority", conflict.ResolutionHint, StringComparison.OrdinalIgnoreCase);
        var serialized = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("conflict-api-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("conflict-api-client-secret", serialized, StringComparison.Ordinal);

        var filteredAction = CreateController().GetSceneScheduleConflicts(
            limit: 7,
            days: 9,
            scheduleId: "api-short-cue");
        var filteredResponse = Assert.IsType<OkObjectResult>(filteredAction.Result);
        var filteredResult = Assert.IsType<HueSceneScheduleConflictsResult>(filteredResponse.Value);
        Assert.Equal("api-short-cue", filteredResult.ScheduleIdFilter);
        var filteredConflict = Assert.Single(filteredResult.Conflicts);
        Assert.Contains(
            new[] { filteredConflict.FirstScheduleId, filteredConflict.SecondScheduleId },
            scheduleId => string.Equals(scheduleId, "api-short-cue", StringComparison.OrdinalIgnoreCase));
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
                new() { Name = "Evening", Effect = PluginConfiguration.ColorPresetEffectRainbow, EffectSpeedPercent = 225, Red = 10, Green = 20, Blue = 30, DurationSeconds = 8, TransitionSeconds = 2, TransitionOutSeconds = 3 }
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
                    BrightnessPercent = 55,
                    Red = 101,
                    Green = 102,
                    Blue = 103,
                    TargetUserIds = new List<string> { "mapping-user" },
                    TargetRoutes = new List<HueSceneScheduleTargetRoute>
                    {
                        new() { UserId = "mapping-user", DeviceId = "living-room-tv" }
                    },
                    IncludeDefaultTarget = true,
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
        Assert.Contains("X-HUE-BRIGHTNESS-PERCENT:55\r\n", calendar, StringComparison.Ordinal);
        Assert.Contains("X-HUE-RED:101\r\n", calendar, StringComparison.Ordinal);
        Assert.Contains("X-HUE-GREEN:102\r\n", calendar, StringComparison.Ordinal);
        Assert.Contains("X-HUE-BLUE:103\r\n", calendar, StringComparison.Ordinal);
        Assert.Contains("X-HUE-PRIORITY:64\r\n", calendar, StringComparison.Ordinal);
        Assert.Contains("X-HUE-TARGET-ALL-ENABLED-MAPPINGS:False\r\n", calendar, StringComparison.Ordinal);
        var unfoldedCalendar = calendar.Replace("\r\n ", string.Empty, StringComparison.Ordinal);
        Assert.Contains("X-HUE-TARGET-USER-IDS:[\"mapping-user\"]\r\n", unfoldedCalendar, StringComparison.Ordinal);
        Assert.Contains("X-HUE-TARGET-ROUTES:[{\"userId\":\"mapping-user\"\\,\"deviceId\":\"living-room-tv\"}]\r\n", unfoldedCalendar, StringComparison.Ordinal);
        Assert.Contains("X-HUE-INCLUDE-DEFAULT-TARGET:True\r\n", calendar, StringComparison.Ordinal);
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
    public void GetSceneScheduleCalendar_FoldsMultibyteMetadataAtUtf8Boundaries()
    {
        var cueTime = DateTime.Now.AddMinutes(10).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var scheduleName = new string('é', 64);
        var targetUserId = "用户😀";
        var targetDeviceId = "客厅电视🌈";
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "unicode-calendar-app-secret",
            HueClientKey = "unicode-calendar-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Evening", DurationSeconds = 8 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "unicode-calendar-cue",
                    Name = scheduleName,
                    PresetName = "Evening",
                    TimeOfDay = cueTime,
                    TimeZoneId = TimeZoneInfo.Local.Id,
                    TargetUserIds = new List<string> { targetUserId },
                    TargetRoutes = new List<HueSceneScheduleTargetRoute>
                    {
                        new() { UserId = targetUserId, DeviceId = targetDeviceId }
                    },
                    IncludeDefaultTarget = true,
                    DaysOfWeekMask = 127
                }
            }
        });

        var action = CreateController().GetSceneScheduleCalendar(limit: 1, days: 7);

        var response = Assert.IsType<FileContentResult>(action);
        var calendar = Encoding.UTF8.GetString(response.FileContents);
        var physicalLines = calendar
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains(
            physicalLines,
            line => line.StartsWith(" ", StringComparison.Ordinal));
        foreach (var line in physicalLines)
        {
            Assert.InRange(Encoding.UTF8.GetByteCount(line), 1, 75);
            for (var index = 0; index < line.Length; index++)
            {
                if (char.IsSurrogate(line[index]))
                {
                    Assert.True(
                        char.IsHighSurrogate(line[index]) &&
                        index + 1 < line.Length &&
                        char.IsLowSurrogate(line[index + 1]),
                        "A Unicode scalar must not be split across physical lines.");
                    index++;
                }
            }

            if (line.StartsWith(" ", StringComparison.Ordinal))
                continue;

            var propertySeparator = line.IndexOf(':');
            Assert.True(propertySeparator > 0, $"Non-continuation line is not a property: {line}");
            Assert.All(
                line[..propertySeparator],
                character => Assert.True(
                    char.IsUpper(character) || char.IsDigit(character) || character == '-',
                    $"Non-continuation line has an invalid property name: {line}"));
        }

        var unfoldedCalendar = calendar.Replace("\r\n ", string.Empty, StringComparison.Ordinal);
        Assert.Contains($"SUMMARY:{scheduleName}\r\n", unfoldedCalendar, StringComparison.Ordinal);
        Assert.Contains(
            $"Target: Default bridge + {targetUserId} / {targetDeviceId}",
            unfoldedCalendar,
            StringComparison.Ordinal);
        Assert.Contains("X-HUE-TARGET-ROUTES:", unfoldedCalendar, StringComparison.Ordinal);
        Assert.DoesNotContain("unicode-calendar-app-secret", calendar, StringComparison.Ordinal);
        Assert.DoesNotContain("unicode-calendar-client-secret", calendar, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialFreeConfigurationReadRoutes_RejectActiveConfigurationMutation()
    {
        InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Scene" } },
            ScenePlaylists = new List<HueScenePlaylist> { new() { Name = "Playlist" } },
            SceneSchedules = new List<HueSceneSchedule> { new() { Id = "cue-1", Name = "Cue" } },
            UserMappings = new List<UserBridgeMapping> { new() { UserId = "user-1", UserName = "Viewer" } }
        });

        var gate = new HueBridgeLifecycleGate();
        using var mutation = gate.TryEnterConfigurationMutation();
        Assert.NotNull(mutation);

        var controller = CreateController(bridgeLifecycleGate: gate);

        AssertConfigurationReadConflict(controller.GetColorPresets());
        AssertConfigurationReadConflict(controller.GetColorPresetDependencies("Scene"));
        AssertConfigurationReadConflict(controller.GetScenePlaylists());
        AssertConfigurationReadConflict(controller.GetScenePlaylistDependencies("Playlist"));
        AssertConfigurationReadConflict(controller.GetSceneSchedules());
        AssertConfigurationReadConflict(controller.GetSceneScheduleStatus());
        AssertConfigurationReadConflict(controller.GetSceneScheduleConflicts());
        AssertConfigurationReadConflict(controller.GetSceneScheduleOccurrences());
        AssertConflict(controller.GetSceneScheduleCalendar());
        AssertConfigurationReadConflict(controller.GetSceneScheduleHistory());
        AssertConfigurationReadConflict(controller.ExportSceneScheduleHistory());
        AssertConflict(controller.ExportSceneScheduleConflictsCsv());
        AssertConflict(controller.ExportSceneScheduleOccurrencesCsv());
        AssertConflict(controller.ExportSceneScheduleHistoryCsv());
        AssertConfigurationReadConflict(controller.GetStatus());
        AssertConfigurationReadConflict(controller.GetUserMappings());
        AssertConfigurationReadConflict(controller.GetUserMappingReconciliation());
        AssertConfigurationReadConflict(controller.GetUserMappingDependencies("user-1"));
    }

    [Fact]
    public void CredentialFreeConfigurationReadRoutes_RejectActiveSchedulerEvaluation()
    {
        InstallConfiguration(new PluginConfiguration());

        var gate = new HueBridgeLifecycleGate();
        using var evaluation = gate.TryEnterSchedulerEvaluation();
        Assert.NotNull(evaluation);

        var action = CreateController(bridgeLifecycleGate: gate).GetSceneScheduleOccurrences();

        AssertConfigurationReadConflict(action);
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
            EntertainmentAreaId = "area-1",
            PlaybackMediaFilter = PluginConfiguration.PlaybackMediaFilterEpisodes
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
                AudioCapture = new HueToolStatus
                {
                    Available = true,
                    ExecutablePath = "/usr/bin/ffmpeg",
                    Version = "PCM s16le 8000 Hz stereo"
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
        Assert.Equal(PluginConfiguration.PlaybackMediaFilterEpisodes, diagnostics.PlaybackMediaFilter);
        Assert.True(diagnostics.DefaultBridgeConfigured);
        Assert.True(diagnostics.Ffmpeg.Available);
        Assert.True(diagnostics.AudioCapture.Available);
        Assert.False(diagnostics.AudioCaptureRequired);
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
    public async Task Diagnostics_RequiresAudioCaptureForAudioPlaybackScope()
    {
        InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "app-secret",
            HueClientKey = "client-secret",
            EntertainmentAreaId = "area-1",
            PlaybackMediaFilter = PluginConfiguration.PlaybackMediaFilterAudio
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
                AudioCapture = new HueToolStatus
                {
                    Available = false,
                    ExecutablePath = "/usr/bin/ffmpeg",
                    Message = "The FFmpeg audio capture probe timed out."
                },
                OpenSsl = new HueToolStatus
                {
                    Available = true,
                    ExecutablePath = "/usr/bin/openssl",
                    Version = "OpenSSL 3.0"
                }
            });
        var controller = CreateController(null, new HueBridgeLifecycleGate(), probe.Object);

        var action = await controller.GetDiagnostics();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var diagnostics = Assert.IsType<HueDiagnosticsResult>(response.Value);
        Assert.True(diagnostics.AudioCaptureRequired);
        Assert.False(diagnostics.AudioCapture.Available);
        Assert.False(diagnostics.CanStartPlayback);
    }

    [Fact]
    public async Task Diagnostics_RejectsActiveConfigurationMutationAfterEnvironmentProbe()
    {
        InstallConfiguration(new PluginConfiguration());

        var probe = new Mock<IHueEnvironmentProbe>();
        probe
            .Setup(environment => environment.CheckAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HueEnvironmentProbeResult());

        var gate = new HueBridgeLifecycleGate();
        using var mutation = gate.TryEnterConfigurationMutation();
        Assert.NotNull(mutation);

        var action = await CreateController(
            bridgeLifecycleGate: gate,
            environmentProbe: probe.Object).GetDiagnostics();

        AssertConfigurationReadConflict(action);
        probe.Verify(
            environment => environment.CheckAsync(It.IsAny<CancellationToken>()),
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
    public async Task ExportSupportBundle_CombinesDiagnosticsAndNeverSerializesCredentials()
    {
        InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "support-app-secret",
            HueClientKey = "support-client-secret",
            EntertainmentAreaId = "area-1",
            CustomFfmpegFlags = "-hwaccel_device support-global-ffmpeg-secret",
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "support-mapping-user",
                    UserName = "Support Mapping",
                    CustomFfmpegFlagsOverride = "-hwaccel_device support-mapping-ffmpeg-secret",
                    SyncEnabled = false
                }
            }
        });
        SetupHttpResponse(HttpStatusCode.OK, "{\"data\":[]}");
        var probe = new Mock<IHueEnvironmentProbe>();
        probe
            .Setup(environment => environment.CheckAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HueEnvironmentProbeResult
            {
                Ffmpeg = new HueToolStatus { Available = true, Version = "ffmpeg test" },
                OpenSsl = new HueToolStatus { Available = true, Version = "openssl test" }
            });

        var action = await CreateController(environmentProbe: probe.Object).ExportSupportBundle();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var bundle = Assert.IsType<HueSupportBundle>(response.Value);
        Assert.Equal(HueSupportBundle.CurrentSchemaVersion, bundle.SchemaVersion);
        Assert.NotEqual(default, bundle.GeneratedAtUtc);
        Assert.True(bundle.Diagnostics.ConfigurationValid);
        Assert.Equal(1, bundle.TargetDiagnostics.TargetCount);
        Assert.False(bundle.TargetDiagnostics.AllTargetsReady);
        Assert.False(bundle.Configuration.CredentialsIncluded);
        Assert.True(bundle.Configuration.Configuration.HasAppKey);
        Assert.True(bundle.Configuration.Configuration.HasClientKey);
        Assert.Empty(bundle.Configuration.Configuration.HueAppKey);
        Assert.Empty(bundle.Configuration.Configuration.HueClientKey);
        Assert.True(bundle.Configuration.Configuration.CustomFfmpegFlagsConfigured);
        var supportMapping = Assert.Single(bundle.Configuration.UserMappings);
        Assert.True(supportMapping.CustomFfmpegFlagsConfigured);
        Assert.Null(supportMapping.CustomFfmpegFlagsOverride);

        var serialized = JsonSerializer.Serialize(bundle);
        Assert.DoesNotContain("support-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("support-client-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("support-global-ffmpeg-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("support-mapping-ffmpeg-secret", serialized, StringComparison.Ordinal);
        probe.Verify(environment => environment.CheckAsync(It.Is<CancellationToken>(token => token.CanBeCanceled)), Times.Once);
    }

    [Fact]
    public async Task TargetDiagnostics_BlocksEnabledAndDisabledDuplicateBeforeBridgeActivity()
    {
        InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    MappingId = "duplicate-enabled",
                    UserId = "duplicate-user",
                    UserName = "Enabled row",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.111",
                    HueAppKey = "enabled-app-secret",
                    HueClientKey = "enabled-client-secret",
                    EntertainmentAreaId = "area-enabled"
                },
                new()
                {
                    MappingId = "duplicate-disabled",
                    UserId = "duplicate-user",
                    UserName = "Disabled row",
                    SyncEnabled = false
                }
            }
        });

        var action = await CreateController().GetTargetDiagnostics();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var diagnostics = Assert.IsType<HueTargetDiagnosticsResult>(response.Value);
        var group = Assert.Single(diagnostics.DuplicateMappingGroups);
        Assert.Equal("duplicate-user", group.UserId);
        Assert.Equal(2, group.MappingCount);
        Assert.Equal(1, group.EnabledMappingCount);
        Assert.Equal(2, group.MappingIds.Count);
        var target = Assert.Single(diagnostics.Targets);
        Assert.False(diagnostics.AllTargetsReady);
        Assert.False(target.Ready);
        Assert.Contains("multiple mapping rows", target.Status, StringComparison.OrdinalIgnoreCase);
        _httpHandlerMock.VerifyNoOtherCalls();

        var serialized = JsonSerializer.Serialize(diagnostics);
        Assert.DoesNotContain("enabled-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("enabled-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TargetDiagnostics_BlocksTwoEnabledDuplicatesBeforeBridgeActivity()
    {
        InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    MappingId = "duplicate-first",
                    UserId = "duplicate-user",
                    UserName = "First room",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.111",
                    HueAppKey = "first-app-secret",
                    HueClientKey = "first-client-secret",
                    EntertainmentAreaId = "area-first"
                },
                new()
                {
                    MappingId = "duplicate-second",
                    UserId = "duplicate-user",
                    UserName = "Second room",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.112",
                    HueAppKey = "second-app-secret",
                    HueClientKey = "second-client-secret",
                    EntertainmentAreaId = "area-second"
                }
            }
        });

        var action = await CreateController().GetTargetDiagnostics();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var diagnostics = Assert.IsType<HueTargetDiagnosticsResult>(response.Value);
        var group = Assert.Single(diagnostics.DuplicateMappingGroups);
        Assert.Equal(2, group.MappingCount);
        Assert.Equal(2, group.EnabledMappingCount);
        Assert.Equal(2, diagnostics.Targets.Count);
        Assert.Equal(0, diagnostics.ReadyTargetCount);
        Assert.False(diagnostics.AllTargetsReady);
        Assert.All(diagnostics.Targets, target =>
        {
            Assert.False(target.Ready);
            Assert.Contains("multiple mapping rows", target.Status, StringComparison.OrdinalIgnoreCase);
        });
        _httpHandlerMock.VerifyNoOtherCalls();

        var serialized = JsonSerializer.Serialize(diagnostics);
        Assert.DoesNotContain("first-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("first-client-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("second-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("second-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TargetDiagnostics_ReportsAllDisabledDuplicateGroupWithoutBridgeActivity()
    {
        InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    MappingId = "duplicate-disabled-a",
                    UserId = "duplicate-user",
                    UserName = "Disabled A",
                    SyncEnabled = false
                },
                new()
                {
                    MappingId = "duplicate-disabled-b",
                    UserId = "duplicate-user",
                    UserName = "Disabled B",
                    SyncEnabled = false
                }
            }
        });

        var action = await CreateController().GetTargetDiagnostics();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var diagnostics = Assert.IsType<HueTargetDiagnosticsResult>(response.Value);
        var group = Assert.Single(diagnostics.DuplicateMappingGroups);
        Assert.Equal(2, group.MappingCount);
        Assert.Equal(0, group.EnabledMappingCount);
        Assert.Empty(diagnostics.Targets);
        Assert.True(diagnostics.HasConfiguredTargets);
        Assert.False(diagnostics.AllTargetsReady);
        Assert.Equal(0, diagnostics.ReadyTargetCount);
        _httpHandlerMock.VerifyNoOtherCalls();
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
        var inherited = Assert.Single(diagnostics.Targets, target => target.UserId == "user-inherited");
        Assert.True(inherited.InheritsDefaultBridge);
        Assert.Equal("Global Room", inherited.EntertainmentAreaName);
        var custom = Assert.Single(diagnostics.Targets, target => target.UserId == "user-custom");
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
    public async Task TargetDiagnostics_ValidatesExplicitDeviceTargetWithoutSecrets()
    {
        InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = true,
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "device-only-user",
                    UserName = "Device Viewer",
                    SyncEnabled = true,
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new()
                        {
                            DeviceId = "living-room-tv",
                            DeviceName = "Living Room TV",
                            HueBridgeIp = "192.168.1.100",
                            HueAppKey = "device-app-secret",
                            HueClientKey = "device-client-secret",
                            EntertainmentAreaId = "area-device",
                            EntertainmentAreaName = "Device Room",
                            ChannelIdsOverride = "2, 4"
                        }
                    }
                }
            }
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
                    ? "{\"data\":[{\"channels\":[{\"channel_id\":2},{\"channel_id\":4}]}]}"
                    : "{\"data\":[{\"id\":\"area-device\",\"metadata\":{\"name\":\"Device Room\"}}]}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            });

        var action = await CreateController().GetTargetDiagnostics();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var diagnostics = Assert.IsType<HueTargetDiagnosticsResult>(response.Value);
        var target = Assert.Single(diagnostics.Targets);
        Assert.Equal(1, diagnostics.TargetCount);
        Assert.Equal(1, diagnostics.ReadyTargetCount);
        Assert.True(diagnostics.AllTargetsReady);
        Assert.Equal("UserDevice", target.Scope);
        Assert.Equal("device-only-user", target.UserId);
        Assert.Equal("Device Viewer", target.UserName);
        Assert.Equal("living-room-tv", target.DeviceId);
        Assert.Equal("Living Room TV", target.DeviceName);
        Assert.Equal("Device Room", target.EntertainmentAreaName);
        Assert.Equal(2, target.SelectedChannelCount);
        Assert.True(target.Ready);

        var serialized = JsonSerializer.Serialize(diagnostics);
        Assert.DoesNotContain("device-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("device-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnostics_RecognizesDeviceOnlyTargetAsCustomTarget()
    {
        InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = true,
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "device-only-user",
                    SyncEnabled = true,
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new()
                        {
                            DeviceId = "living-room-tv",
                            HueBridgeIp = "192.168.1.100",
                            HueAppKey = "device-app-secret",
                            HueClientKey = "device-client-secret",
                            EntertainmentAreaId = "area-device"
                        }
                    }
                }
            }
        });
        var probe = new Mock<IHueEnvironmentProbe>();
        probe
            .Setup(environment => environment.CheckAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HueEnvironmentProbeResult
            {
                Ffmpeg = new HueToolStatus { Available = true },
                OpenSsl = new HueToolStatus { Available = true }
            });

        var action = await CreateController(environmentProbe: probe.Object).GetDiagnostics();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var diagnostics = Assert.IsType<HueDiagnosticsResult>(response.Value);
        Assert.True(diagnostics.ConfigurationValid);
        Assert.False(diagnostics.DefaultBridgeConfigured);
        Assert.True(diagnostics.CustomUserTargetConfigured);
        Assert.Equal(1, diagnostics.EnabledUserMappingCount);
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
    public async Task TargetDiagnostics_ReturnsConflictWhilePlaybackOwnsBridgeLifecycle()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "diagnostic-app-secret",
            HueClientKey = "diagnostic-client-secret",
            EntertainmentAreaId = "area-1"
        });
        var lifecycleGate = new HueBridgeLifecycleGate();
        using var playbackLease = lifecycleGate.TryEnterPlayback("192.168.1.100|area-1");
        Assert.NotNull(playbackLease);

        var action = await CreateController(bridgeLifecycleGate: lifecycleGate).GetTargetDiagnostics();

        var conflict = Assert.IsType<ConflictObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Contains("playback", Assert.IsType<string>(conflict.Value), StringComparison.OrdinalIgnoreCase);
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExportSupportBundle_ReturnsConflictWhileDiagnosticOwnsBridgeLifecycle()
    {
        InstallConfiguration(new PluginConfiguration());
        var lifecycleGate = new HueBridgeLifecycleGate();
        using var diagnosticLease = lifecycleGate.TryEnterDiagnostic("192.168.1.100|area-1");
        Assert.NotNull(diagnosticLease);

        var action = await CreateController(bridgeLifecycleGate: lifecycleGate).ExportSupportBundle();

        var conflict = Assert.IsType<ConflictObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Contains("diagnostic", Assert.IsType<string>(conflict.Value), StringComparison.OrdinalIgnoreCase);
        _httpHandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExportSupportBundle_PropagatesConfigurationReadConflict()
    {
        InstallConfiguration(new PluginConfiguration());
        var probe = new Mock<IHueEnvironmentProbe>();
        probe
            .Setup(environment => environment.CheckAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HueEnvironmentProbeResult());

        var lifecycleGate = new HueBridgeLifecycleGate();
        using var evaluation = lifecycleGate.TryEnterSchedulerEvaluation();
        Assert.NotNull(evaluation);

        var action = await CreateController(
            bridgeLifecycleGate: lifecycleGate,
            environmentProbe: probe.Object).ExportSupportBundle();

        var conflict = Assert.IsType<ConflictObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Contains("configuration", Assert.IsType<string>(conflict.Value), StringComparison.OrdinalIgnoreCase);
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
                    PlaybackMediaFilterOverride = PluginConfiguration.PlaybackMediaFilterEpisodes,
                    AudioSensitivityPercentOverride = 275,
                    AudioNoiseGatePercentOverride = 16,
                    AudioLowFrequencyHzOverride = 60,
                    AudioMidFrequencyHzOverride = 700,
                    AudioHighFrequencyHzOverride = 2400,
                    AudioLowGainPercentOverride = 45,
                    AudioMidGainPercentOverride = 125,
                    AudioHighGainPercentOverride = 175,
                    AudioResponseSmoothingPercentOverride = 55,
                    AudioBandSpreadPercentOverride = 35,
                    AudioBeatPulsePercentOverride = 65,
                    AudioBeatPulseDecayPercentOverride = 75,
                    AudioBeatPulseThresholdPercentOverride = 85,
                    BrightnessBoostOverride = 150,
                    RedGainOverride = 120,
                    GreenGainOverride = 90,
                    BlueGainOverride = 110,
                    ColorSaturationOverride = 0,
                    HueShiftDegreesOverride = -45,
                    OutputBrightnessPercentOverride = 75,
                    GammaCorrectionOverride = 1.35,
                    ContrastPercentOverride = 135,
                    ColorTemperatureKelvinOverride = 4200,
                    BlackoutThresholdOverride = 30,
                    BlackoutBehaviorOverride = PluginConfiguration.BlackoutBehaviorKeepLastColors,
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
                    SpatialOrientationOverride = PluginConfiguration.SpatialOrientationMirrorHorizontal,
                    ColorSmoothingPercentOverride = 40,
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new()
                        {
                            DeviceId = "living-room-player",
                            DeviceName = "Living Room Player",
                            HueBridgeIp = "192.168.1.101",
                            HueAppKey = "nested-app-secret",
                            HueClientKey = "nested-client-secret",
                            EntertainmentAreaId = "area-device",
                            EntertainmentAreaName = "Device Room",
                            ChannelIdsOverride = "2, 4"
                        }
                    }
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
        var deviceTarget = Assert.Single(mapping.DeviceTargets);
        Assert.Equal("living-room-player", deviceTarget.DeviceId);
        Assert.True(deviceTarget.HasAppKey);
        Assert.True(deviceTarget.HasClientKey);
        Assert.DoesNotContain("nested-app-secret", JsonSerializer.Serialize(mapping), StringComparison.Ordinal);
        Assert.DoesNotContain("nested-client-secret", JsonSerializer.Serialize(mapping), StringComparison.Ordinal);
        Assert.Equal((bool?)false, mapping.UseCinemaModeOverride);
        Assert.Equal((int?)10, mapping.BrightnessDimLevelOverride);
        Assert.Equal(PluginConfiguration.PauseBehaviorRestoreLightState, mapping.PauseBehaviorOverride);
        Assert.Equal((bool?)true, mapping.RestoreLightStateOverride);
        Assert.Equal(PluginConfiguration.PlaybackMediaFilterEpisodes, mapping.PlaybackMediaFilterOverride);
        Assert.Equal((int?)275, mapping.AudioSensitivityPercentOverride);
        Assert.Equal((int?)16, mapping.AudioNoiseGatePercentOverride);
        Assert.Equal((int?)60, mapping.AudioLowFrequencyHzOverride);
        Assert.Equal((int?)700, mapping.AudioMidFrequencyHzOverride);
        Assert.Equal((int?)2400, mapping.AudioHighFrequencyHzOverride);
        Assert.Equal((int?)45, mapping.AudioLowGainPercentOverride);
        Assert.Equal((int?)125, mapping.AudioMidGainPercentOverride);
        Assert.Equal((int?)175, mapping.AudioHighGainPercentOverride);
        Assert.Equal((int?)55, mapping.AudioResponseSmoothingPercentOverride);
        Assert.Equal((int?)35, mapping.AudioBandSpreadPercentOverride);
        Assert.Equal((int?)65, mapping.AudioBeatPulsePercentOverride);
        Assert.Equal((int?)75, mapping.AudioBeatPulseDecayPercentOverride);
        Assert.Equal((int?)85, mapping.AudioBeatPulseThresholdPercentOverride);
        Assert.Equal((int?)150, mapping.BrightnessBoostOverride);
        Assert.Equal((int?)120, mapping.RedGainOverride);
        Assert.Equal((int?)90, mapping.GreenGainOverride);
        Assert.Equal((int?)110, mapping.BlueGainOverride);
        Assert.Equal((int?)0, mapping.ColorSaturationOverride);
        Assert.Equal((int?)-45, mapping.HueShiftDegreesOverride);
        Assert.Equal((int?)75, mapping.OutputBrightnessPercentOverride);
        Assert.Equal((double?)1.35, mapping.GammaCorrectionOverride);
        Assert.Equal((int?)135, mapping.ContrastPercentOverride);
        Assert.Equal((int?)4200, mapping.ColorTemperatureKelvinOverride);
        Assert.Equal((int?)30, mapping.BlackoutThresholdOverride);
        Assert.Equal(PluginConfiguration.BlackoutBehaviorKeepLastColors, mapping.BlackoutBehaviorOverride);
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
        Assert.Equal(PluginConfiguration.SpatialOrientationMirrorHorizontal, mapping.SpatialOrientationOverride);
        Assert.Equal((int?)40, mapping.ColorSmoothingPercentOverride);
        var serialized = System.Text.Json.JsonSerializer.Serialize(mapping);
        Assert.DoesNotContain("mapping-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("mapping-client-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("HueAppKey", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HueClientKey", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetUserMappings_NormalizesLegacyGuidRepresentation()
    {
        const string canonicalUserId = "dddddddd-dddd-dddd-dddd-dddddddddddd";
        InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "{DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD}",
                    UserName = "Legacy viewer"
                }
            }
        });

        var action = CreateController().GetUserMappings();

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var mapping = Assert.Single(Assert.IsAssignableFrom<IEnumerable<UserBridgeMappingSummary>>(response.Value));
        Assert.Equal(canonicalUserId, mapping.UserId);
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
    public void GetUserMappingDependencies_ReturnsCredentialFreeCueDetails()
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
                    HueAppKey = "mapping-dependency-app-secret",
                    HueClientKey = "mapping-dependency-client-secret"
                }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "mapping-cue-disabled",
                    Name = "Disabled mapping cue",
                    TargetUserId = " USER-1 ",
                    Enabled = false
                },
                new()
                {
                    Id = "mapping-cue-enabled",
                    Name = "Enabled mapping cue",
                    TargetUserId = "user-1",
                    Enabled = true
                },
                new()
                {
                    Id = "mapping-cue-selected",
                    Name = "Selected mapping cue",
                    TargetUserIds = new List<string> { " USER-1 " },
                    Enabled = true
                },
                new()
                {
                    Id = "mapping-cue-device-route",
                    Name = "Nested device cue",
                    TargetRoutes = new List<HueSceneScheduleTargetRoute>
                    {
                        new() { UserId = "user-1", DeviceId = "living-room-tv" }
                    },
                    Enabled = true
                },
                new()
                {
                    Id = "other-mapping-cue",
                    Name = "Other mapping cue",
                    TargetUserId = "user-2",
                    Enabled = true
                }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "mapping-playlist-selected",
                    Name = "Kitchen playlist",
                    PresetNames = new List<string> { "Welcome" },
                    TargetUserIds = new List<string> { " USER-1 " }
                }
            }
        });
        var controller = CreateController();

        var action = controller.GetUserMappingDependencies(" USER-1 ");

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueUserMappingDependenciesResult>(response.Value);
        Assert.Equal("user-1", result.UserId);
        Assert.Equal("Viewer", result.UserName);
        Assert.True(result.SyncEnabled);
        Assert.False(result.CanDisable);
        Assert.False(result.CanDelete);
        Assert.Equal(4, result.ScheduledCueCount);
        Assert.Equal(1, result.ScenePlaylistCount);
        Assert.Equal("Kitchen playlist", Assert.Single(result.ScenePlaylists).Name);
        Assert.Equal(new[] { "Disabled mapping cue", "Enabled mapping cue", "Nested device cue", "Selected mapping cue" }, result.ScheduledCues.Select(cue => cue.Name));
        Assert.False(result.ScheduledCues[0].Enabled);
        Assert.True(result.ScheduledCues[1].Enabled);
        Assert.True(result.ScheduledCues[2].Enabled);
        Assert.Equal(new[] { "mapping-cue-disabled", "mapping-cue-enabled", "mapping-cue-device-route", "mapping-cue-selected" }, result.ScheduledCues.Select(cue => cue.Id));
        Assert.Empty(configuration.UserMappings[0].MappingId);

        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("mapping-dependency-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("mapping-dependency-client-secret", serialized, StringComparison.Ordinal);

        configuration.SceneSchedules.Clear();
        configuration.ScenePlaylists.Clear();
        var noReferences = controller.GetUserMappingDependencies("user-1");
        var noReferencesResponse = Assert.IsType<OkObjectResult>(noReferences.Result);
        var noReferencesResult = Assert.IsType<HueUserMappingDependenciesResult>(noReferencesResponse.Value);
        Assert.True(noReferencesResult.CanDisable);
        Assert.True(noReferencesResult.CanDelete);
        Assert.Equal(0, noReferencesResult.ScheduledCueCount);
        Assert.Empty(noReferencesResult.ScheduledCues);
        Assert.Equal(0, noReferencesResult.ScenePlaylistCount);
        Assert.Empty(noReferencesResult.ScenePlaylists);
    }

    [Fact]
    public void UserMappings_BulkDeleteRemovesSelectedMappingsByUserId()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-one",
                    UserName = "One",
                    SyncEnabled = true,
                    HueAppKey = "bulk-mapping-app-secret",
                    HueClientKey = "bulk-mapping-client-secret",
                    HueBridgeIp = "192.168.1.101",
                    EntertainmentAreaId = "area-one"
                },
                new() { UserId = "user-two", UserName = "Two", SyncEnabled = false },
                new() { UserId = "user-keep", UserName = "Keep", SyncEnabled = false }
            }
        });

        var action = CreateController().DeleteUserMappingsBulk(new HueUserMappingBulkDeleteRequest
        {
            UserIds = new List<string> { " USER-ONE ", "USER-TWO" }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueUserMappingBulkDeleteResult>(response.Value);
        Assert.Equal(2, result.RequestedCount);
        Assert.Equal(2, result.DeletedCount);
        Assert.Equal(1, result.RemainingCount);
        Assert.Equal(new[] { "user-one", "user-two" }, result.Mappings.Select(mapping => mapping.UserId));
        Assert.Equal("user-keep", Assert.Single(configuration.UserMappings).UserId);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("bulk-mapping-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("bulk-mapping-client-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("HueAppKey", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HueClientKey", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UserMappings_BulkDeleteRejectsNullOrBlankIdWithoutMutation()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = "user-one", UserName = "One", SyncEnabled = false },
                new() { UserId = "user-keep", UserName = "Keep", SyncEnabled = false }
            }
        });

        var action = CreateController().DeleteUserMappingsBulk(new HueUserMappingBulkDeleteRequest
        {
            UserIds = new List<string> { " user-one ", null!, " " }
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Equal(new[] { "user-one", "user-keep" }, configuration.UserMappings.Select(mapping => mapping.UserId));
    }

    [Fact]
    public void UserMappings_BulkDeleteRefusesDependenciesAndMissingIdsAtomically()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = "user-dependent", UserName = "Dependent", SyncEnabled = true },
                new() { UserId = "user-free", UserName = "Free", SyncEnabled = false },
                new() { UserId = "user-keep", UserName = "Keep", SyncEnabled = false }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "mapping-bulk-cue", Name = "Dependent cue", TargetUserId = " USER-DEPENDENT " }
            }
        });
        var controller = CreateController();

        var blocked = controller.DeleteUserMappingsBulk(new HueUserMappingBulkDeleteRequest
        {
            UserIds = new List<string> { "user-dependent", "user-free" }
        });

        var blockedResponse = Assert.IsType<ConflictObjectResult>(blocked.Result);
        var blockedResult = Assert.IsType<HueUserMappingBulkDeleteResult>(blockedResponse.Value);
        Assert.Equal(2, blockedResult.RequestedCount);
        var dependency = Assert.Single(blockedResult.BlockedMappings);
        Assert.Equal("user-dependent", dependency.UserId);
        Assert.Equal(1, dependency.ScheduledCueCount);
        Assert.Equal(3, configuration.UserMappings.Count);

        var missing = controller.DeleteUserMappingsBulk(new HueUserMappingBulkDeleteRequest
        {
            UserIds = new List<string> { "user-free", "missing-user" }
        });

        var missingResponse = Assert.IsType<NotFoundObjectResult>(missing.Result);
        var missingResult = Assert.IsType<HueUserMappingBulkDeleteResult>(missingResponse.Value);
        Assert.Equal(2, missingResult.RequestedCount);
        Assert.Equal(new[] { "missing-user" }, missingResult.MissingUserIds);
        Assert.Equal(3, configuration.UserMappings.Count);
    }

    [Fact]
    public void UserMappings_BulkDeletePersistenceFailureRestoresCollection()
    {
        var serializer = new Mock<IXmlSerializer>();
        serializer
            .Setup(xml => xml.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("bulk mapping persistence failed"));
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = "user-remove", UserName = "Remove", SyncEnabled = false },
                new() { UserId = "user-keep", UserName = "Keep", SyncEnabled = false }
            }
        }, serializer.Object);

        var action = CreateController().DeleteUserMappingsBulk(new HueUserMappingBulkDeleteRequest
        {
            UserIds = new List<string> { "user-remove" }
        });

        var response = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, response.StatusCode);
        Assert.Equal(new[] { "user-remove", "user-keep" }, configuration.UserMappings.Select(mapping => mapping.UserId));
    }

    [Fact]
    public void UserMappings_BulkEnabledChangesSelectedStatesAndScrubsDisabledTargets()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-enabled",
                    UserName = "Enabled",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "bulk-enabled-app-secret",
                    HueClientKey = "bulk-enabled-client-secret",
                    EntertainmentAreaId = "area-enabled",
                    EntertainmentAreaName = "Enabled Room"
                },
                new() { UserId = "user-disabled", UserName = "Disabled", SyncEnabled = false },
                new() { UserId = "user-keep", UserName = "Keep", SyncEnabled = true }
            }
        });
        var controller = CreateController();

        var disable = controller.SetUserMappingsEnabledBulk(new HueUserMappingBulkEnabledRequest
        {
            UserIds = new List<string> { " USER-ENABLED ", "USER-DISABLED", "user-enabled" },
            SyncEnabled = false
        });

        var disableResponse = Assert.IsType<OkObjectResult>(disable.Result);
        var disableResult = Assert.IsType<HueUserMappingBulkEnabledResult>(disableResponse.Value);
        Assert.False(disableResult.SyncEnabled);
        Assert.Equal(2, disableResult.RequestedCount);
        Assert.Equal(2, disableResult.UpdatedCount);
        Assert.Equal(new[] { "user-enabled", "user-disabled" }, disableResult.Mappings.Select(mapping => mapping.UserId));
        var disabledMapping = configuration.UserMappings.Single(mapping => mapping.UserId == "user-enabled");
        Assert.False(disabledMapping.SyncEnabled);
        Assert.Empty(disabledMapping.HueBridgeIp);
        Assert.Empty(disabledMapping.HueAppKey);
        Assert.Empty(disabledMapping.HueClientKey);
        Assert.Empty(disabledMapping.EntertainmentAreaId);
        Assert.Empty(disabledMapping.EntertainmentAreaName);
        Assert.True(configuration.UserMappings.Single(mapping => mapping.UserId == "user-keep").SyncEnabled);

        var serialized = JsonSerializer.Serialize(disableResult);
        Assert.DoesNotContain("bulk-enabled-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("bulk-enabled-client-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("HueAppKey", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HueClientKey", serialized, StringComparison.OrdinalIgnoreCase);

        var enable = controller.SetUserMappingsEnabledBulk(new HueUserMappingBulkEnabledRequest
        {
            UserIds = new List<string> { " user-disabled " },
            SyncEnabled = true
        });

        var enableResponse = Assert.IsType<OkObjectResult>(enable.Result);
        var enableResult = Assert.IsType<HueUserMappingBulkEnabledResult>(enableResponse.Value);
        Assert.True(enableResult.SyncEnabled);
        Assert.Equal(1, enableResult.UpdatedCount);
        Assert.True(configuration.UserMappings.Single(mapping => mapping.UserId == "user-disabled").SyncEnabled);
    }

    [Fact]
    public void UserMappings_BulkEnabledWithStableRowIdChangesOnlySelectedDuplicate()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    MappingId = "mapping-enabled-a",
                    UserId = "user-enabled-duplicate",
                    UserName = "First",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.121",
                    HueAppKey = "enabled-a-app-secret",
                    HueClientKey = "enabled-a-client-secret",
                    EntertainmentAreaId = "area-enabled-a"
                },
                new()
                {
                    MappingId = "mapping-enabled-b",
                    UserId = "user-enabled-duplicate",
                    UserName = "Second",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.122",
                    HueAppKey = "enabled-b-app-secret",
                    HueClientKey = "enabled-b-client-secret",
                    EntertainmentAreaId = "area-enabled-b"
                }
            }
        });

        var action = CreateController().SetUserMappingsEnabledBulk(new HueUserMappingBulkEnabledRequest
        {
            MappingIds = new List<string> { "mapping-enabled-b" },
            SyncEnabled = false
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueUserMappingBulkEnabledResult>(response.Value);
        Assert.Equal(1, result.RequestedCount);
        Assert.Equal(1, result.UpdatedCount);
        var first = Assert.Single(configuration.UserMappings, mapping => mapping.MappingId == "mapping-enabled-a");
        Assert.True(first.SyncEnabled);
        Assert.Equal("enabled-a-app-secret", first.HueAppKey);
        Assert.Equal("area-enabled-a", first.EntertainmentAreaId);
        var second = Assert.Single(configuration.UserMappings, mapping => mapping.MappingId == "mapping-enabled-b");
        Assert.False(second.SyncEnabled);
        Assert.Empty(second.HueAppKey);
        Assert.Empty(second.HueClientKey);
        Assert.Empty(second.EntertainmentAreaId);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("enabled-b-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("enabled-b-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void UserMappings_BulkEnabledRejectsAmbiguousLegacyUserIdWithoutMutation()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new() { MappingId = "mapping-enabled-a", UserId = "user-enabled-duplicate", UserName = "First", SyncEnabled = true, HueAppKey = "enabled-a-app-secret" },
                new() { MappingId = "mapping-enabled-b", UserId = "user-enabled-duplicate", UserName = "Second", SyncEnabled = true, HueAppKey = "enabled-b-app-secret" }
            }
        });

        var action = CreateController().SetUserMappingsEnabledBulk(new HueUserMappingBulkEnabledRequest
        {
            UserIds = new List<string> { "user-enabled-duplicate" },
            SyncEnabled = false
        });

        var response = Assert.IsType<ConflictObjectResult>(action.Result);
        var result = Assert.IsType<HueUserMappingBulkEnabledResult>(response.Value);
        Assert.Equal(new[] { "user-enabled-duplicate" }, result.AmbiguousUserIds);
        Assert.All(configuration.UserMappings, mapping => Assert.True(mapping.SyncEnabled));
        Assert.Equal(new[] { "enabled-a-app-secret", "enabled-b-app-secret" }, configuration.UserMappings.Select(mapping => mapping.HueAppKey));
    }

    [Fact]
    public void UserMappings_BulkEnabledRefusesDependenciesAndMissingIdsAtomically()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = "user-dependent", UserName = "Dependent", SyncEnabled = true },
                new() { UserId = "user-free", UserName = "Free", SyncEnabled = true },
                new() { UserId = "user-keep", UserName = "Keep", SyncEnabled = false }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "mapping-enabled-cue", Name = "Dependent cue", TargetUserId = " USER-DEPENDENT " }
            }
        });
        var controller = CreateController();

        var blocked = controller.SetUserMappingsEnabledBulk(new HueUserMappingBulkEnabledRequest
        {
            UserIds = new List<string> { "user-dependent", "user-free" },
            SyncEnabled = false
        });

        var blockedResponse = Assert.IsType<ConflictObjectResult>(blocked.Result);
        var blockedResult = Assert.IsType<HueUserMappingBulkEnabledResult>(blockedResponse.Value);
        Assert.Equal(2, blockedResult.RequestedCount);
        var dependency = Assert.Single(blockedResult.BlockedMappings);
        Assert.Equal("user-dependent", dependency.UserId);
        Assert.Equal(1, dependency.ScheduledCueCount);
        Assert.True(configuration.UserMappings.Single(mapping => mapping.UserId == "user-dependent").SyncEnabled);
        Assert.True(configuration.UserMappings.Single(mapping => mapping.UserId == "user-free").SyncEnabled);

        var missing = controller.SetUserMappingsEnabledBulk(new HueUserMappingBulkEnabledRequest
        {
            UserIds = new List<string> { "user-free", "missing-user" },
            SyncEnabled = false
        });

        var missingResponse = Assert.IsType<NotFoundObjectResult>(missing.Result);
        var missingResult = Assert.IsType<HueUserMappingBulkEnabledResult>(missingResponse.Value);
        Assert.Equal(2, missingResult.RequestedCount);
        Assert.Equal(new[] { "missing-user" }, missingResult.MissingUserIds);
        Assert.True(configuration.UserMappings.Single(mapping => mapping.UserId == "user-free").SyncEnabled);
    }

    [Fact]
    public void UserMappings_BulkEnabledRefusesIncompleteCustomTargetWithoutMutation()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-invalid",
                    UserName = "Invalid target",
                    SyncEnabled = false,
                    HueBridgeIp = "192.168.1.102",
                    HueAppKey = "only-app-key",
                    EntertainmentAreaId = "area-invalid"
                }
            }
        });

        var action = CreateController().SetUserMappingsEnabledBulk(new HueUserMappingBulkEnabledRequest
        {
            UserIds = new List<string> { "user-invalid" },
            SyncEnabled = true
        });

        var response = Assert.IsType<ConflictObjectResult>(action.Result);
        var result = Assert.IsType<HueUserMappingBulkEnabledResult>(response.Value);
        Assert.Equal(new[] { "user-invalid" }, result.InvalidUserIds);
        Assert.False(configuration.UserMappings[0].SyncEnabled);
        Assert.Equal("only-app-key", configuration.UserMappings[0].HueAppKey);
    }

    [Fact]
    public void UserMappings_BulkEnabledPersistenceFailureRestoresEverySelectedMapping()
    {
        var serializer = new Mock<IXmlSerializer>();
        serializer
            .Setup(xml => xml.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("bulk mapping enabled persistence failed"));
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-one",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "user-one-app-secret",
                    HueClientKey = "user-one-client-secret",
                    EntertainmentAreaId = "area-one",
                    EntertainmentAreaName = "Room One",
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new()
                        {
                            DeviceId = "player-one",
                            HueBridgeIp = "192.168.1.111",
                            HueAppKey = "device-one-app-secret",
                            HueClientKey = "device-one-client-secret",
                            EntertainmentAreaId = "device-area-one"
                        }
                    }
                },
                new()
                {
                    UserId = "user-two",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.102",
                    HueAppKey = "user-two-app-secret",
                    HueClientKey = "user-two-client-secret",
                    EntertainmentAreaId = "area-two",
                    EntertainmentAreaName = "Room Two"
                }
            }
        }, serializer.Object);

        var action = CreateController().SetUserMappingsEnabledBulk(new HueUserMappingBulkEnabledRequest
        {
            UserIds = new List<string> { "user-one", "user-two" },
            SyncEnabled = false
        });

        var response = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, response.StatusCode);
        Assert.All(configuration.UserMappings, mapping => Assert.True(mapping.SyncEnabled));
        Assert.Equal("user-one-app-secret", configuration.UserMappings[0].HueAppKey);
        Assert.Equal("user-two-client-secret", configuration.UserMappings[1].HueClientKey);
        Assert.Equal(new[] { "area-one", "area-two" }, configuration.UserMappings.Select(mapping => mapping.EntertainmentAreaId));
        var restoredDeviceTarget = Assert.Single(configuration.UserMappings[0].DeviceTargets);
        Assert.Equal("device-one-app-secret", restoredDeviceTarget.HueAppKey);
        Assert.Equal("device-one-client-secret", restoredDeviceTarget.HueClientKey);
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
            PlaybackMediaFilter = PluginConfiguration.PlaybackMediaFilterMovies,
            AudioSensitivityPercent = 180,
            AudioNoiseGatePercent = 12,
            AudioLowFrequencyHz = 70,
            AudioMidFrequencyHz = 600,
            AudioHighFrequencyHz = 2200,
            AudioLowGainPercent = 75,
            AudioMidGainPercent = 115,
            AudioHighGainPercent = 165,
            AudioResponseSmoothingPercent = 35,
            AudioBandSpreadPercent = 18,
            AudioBeatPulsePercent = 42,
            AudioBeatPulseDecayPercent = 55,
            AudioBeatPulseThresholdPercent = 27,
            AudioColorPalette = PluginConfiguration.AudioColorPaletteWarm,
            AudioSpatialMode = PluginConfiguration.AudioSpatialModeMirror,
            AudioChannelMode = PluginConfiguration.AudioChannelModeLeft,
            FrameResolution = PluginConfiguration.FrameResolutionHigh,
            VideoScalingMode = PluginConfiguration.VideoScalingModeFit,
            VideoDeinterlaceMode = PluginConfiguration.VideoDeinterlaceModeAuto,
            SamplingBreadthPercent = 25,
            SamplingMode = PluginConfiguration.SamplingModeCenterWeighted,
            SpatialOrientation = PluginConfiguration.SpatialOrientationMirrorVertical,
            ColorSmoothingPercent = 65,
            HueShiftDegrees = 45,
            OutputBrightnessPercent = 75,
            GammaCorrection = 1.2,
            ContrastPercent = 120,
            ColorTemperatureKelvin = 5600,
            BlackoutBehavior = PluginConfiguration.BlackoutBehaviorKeepLastColors,
            RedGain = 120,
            GreenGain = 90,
            BlueGain = 110,
            NetworkRetryAttempts = 6,
            PauseBehavior = PluginConfiguration.PauseBehaviorRestoreLightState,
            PersistSessionHistory = true,
            SessionHistoryRetentionCount = 12,
            SceneScheduleHistoryRetentionCount = 73,
            SceneAutomationEnabled = false,
            SceneAutomationCatchUpMinutes = 37,
            SceneAutomationPlaybackPolicy = PluginConfiguration.SceneAutomationPlaybackPolicyDefer,
            SceneAutomationPlaybackScope = PluginConfiguration.SceneAutomationPlaybackScopeMatchingTarget,
            SceneAutomationDeferMinutes = 49,
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
        Assert.Equal(PluginConfiguration.PlaybackMediaFilterMovies, settings.PlaybackMediaFilter);
        Assert.Equal(180, settings.AudioSensitivityPercent);
        Assert.Equal(12, settings.AudioNoiseGatePercent);
        Assert.Equal(70, settings.AudioLowFrequencyHz);
        Assert.Equal(600, settings.AudioMidFrequencyHz);
        Assert.Equal(2200, settings.AudioHighFrequencyHz);
        Assert.Equal(75, settings.AudioLowGainPercent);
        Assert.Equal(115, settings.AudioMidGainPercent);
        Assert.Equal(165, settings.AudioHighGainPercent);
        Assert.Equal(35, settings.AudioResponseSmoothingPercent);
        Assert.Equal(18, settings.AudioBandSpreadPercent);
        Assert.Equal(42, settings.AudioBeatPulsePercent);
        Assert.Equal(55, settings.AudioBeatPulseDecayPercent);
        Assert.Equal(27, settings.AudioBeatPulseThresholdPercent);
        Assert.Equal(PluginConfiguration.AudioColorPaletteWarm, settings.AudioColorPalette);
        Assert.Equal(PluginConfiguration.AudioSpatialModeMirror, settings.AudioSpatialMode);
        Assert.Equal(PluginConfiguration.AudioChannelModeLeft, settings.AudioChannelMode);
        Assert.Equal(PluginConfiguration.FrameResolutionHigh, settings.FrameResolution);
        Assert.Equal(PluginConfiguration.VideoScalingModeFit, settings.VideoScalingMode);
        Assert.Equal(PluginConfiguration.VideoDeinterlaceModeAuto, settings.VideoDeinterlaceMode);
        Assert.Equal(25, settings.SamplingBreadthPercent);
        Assert.Equal(PluginConfiguration.SamplingModeCenterWeighted, settings.SamplingMode);
        Assert.Equal(PluginConfiguration.SpatialOrientationMirrorVertical, settings.SpatialOrientation);
        Assert.Equal(65, settings.ColorSmoothingPercent);
        Assert.Equal(45, settings.HueShiftDegrees);
        Assert.Equal(75, settings.OutputBrightnessPercent);
        Assert.Equal(1.2, settings.GammaCorrection);
        Assert.Equal(120, settings.ContrastPercent);
        Assert.Equal(5600, settings.ColorTemperatureKelvin);
        Assert.Equal(PluginConfiguration.BlackoutBehaviorKeepLastColors, settings.BlackoutBehavior);
        Assert.Equal(120, settings.RedGain);
        Assert.Equal(90, settings.GreenGain);
        Assert.Equal(110, settings.BlueGain);
        Assert.Equal(6, settings.NetworkRetryAttempts);
        Assert.Equal(PluginConfiguration.PauseBehaviorRestoreLightState, settings.PauseBehavior);
        Assert.True(settings.PersistSessionHistory);
        Assert.Equal(12, settings.SessionHistoryRetentionCount);
        Assert.Equal(73, settings.SceneScheduleHistoryRetentionCount);
        Assert.Equal(false, settings.SceneAutomationEnabled);
        Assert.Equal(37, settings.SceneAutomationCatchUpMinutes);
        Assert.Equal(PluginConfiguration.SceneAutomationPlaybackPolicyDefer, settings.SceneAutomationPlaybackPolicy);
        Assert.Equal(PluginConfiguration.SceneAutomationPlaybackScopeMatchingTarget, settings.SceneAutomationPlaybackScope);
        Assert.Equal(49, settings.SceneAutomationDeferMinutes);
        var serialized = System.Text.Json.JsonSerializer.Serialize(settings);
        Assert.DoesNotContain("default-app-key", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("default-client-key", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("mapping-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("UserMappings", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetConfiguration_RejectsActiveConfigurationMutation()
    {
        InstallConfiguration(new PluginConfiguration());
        var gate = new HueBridgeLifecycleGate();
        using var mutation = gate.TryEnterConfigurationMutation();
        Assert.NotNull(mutation);

        var action = CreateController(bridgeLifecycleGate: gate).GetConfiguration();

        var response = Assert.IsType<ConflictObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        Assert.Null(action.Value);
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
            SceneAutomationPlaybackScope = PluginConfiguration.SceneAutomationPlaybackScopeMatchingTarget,
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
                    PlaybackMediaFilterOverride = PluginConfiguration.PlaybackMediaFilterMovies,
                    AudioSensitivityPercentOverride = 245,
                    AudioNoiseGatePercentOverride = 14,
                    AudioLowFrequencyHzOverride = 60,
                    AudioMidFrequencyHzOverride = 700,
                    AudioHighFrequencyHzOverride = 2400,
                    AudioLowGainPercentOverride = 55,
                    AudioMidGainPercentOverride = 130,
                    AudioHighGainPercentOverride = 180,
                    AudioResponseSmoothingPercentOverride = 45,
                    AudioBandSpreadPercentOverride = 35,
                    AudioBeatPulsePercentOverride = 65,
                    AudioBeatPulseDecayPercentOverride = 75,
                    AudioBeatPulseThresholdPercentOverride = 85,
                    AudioColorPaletteOverride = PluginConfiguration.AudioColorPaletteBand,
                    AudioSpatialModeOverride = PluginConfiguration.AudioSpatialModeUniform,
                    AudioChannelModeOverride = PluginConfiguration.AudioChannelModeStereo,
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
                    PlaybackPolicy = PluginConfiguration.SceneAutomationPlaybackPolicyDefer,
                    TimeOfDay = "08:15",
                    StartDate = "2026-08-01",
                    EndDate = "2026-12-31",
                    ExcludedDates = new List<string> { "2026-12-24" },
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceMonthly,
                    DayOfMonth = 31,
                    DurationSeconds = 11,
                    BrightnessPercent = 62,
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
        Assert.Equal(PluginConfiguration.PlaybackMediaFilterMovies, mapping.PlaybackMediaFilterOverride);
        Assert.Equal(245, mapping.AudioSensitivityPercentOverride);
        Assert.Equal(14, mapping.AudioNoiseGatePercentOverride);
        Assert.Equal(60, mapping.AudioLowFrequencyHzOverride);
        Assert.Equal(700, mapping.AudioMidFrequencyHzOverride);
        Assert.Equal(2400, mapping.AudioHighFrequencyHzOverride);
        Assert.Equal(55, mapping.AudioLowGainPercentOverride);
        Assert.Equal(130, mapping.AudioMidGainPercentOverride);
        Assert.Equal(180, mapping.AudioHighGainPercentOverride);
        Assert.Equal(45, mapping.AudioResponseSmoothingPercentOverride);
        Assert.Equal(35, mapping.AudioBandSpreadPercentOverride);
        Assert.Equal(65, mapping.AudioBeatPulsePercentOverride);
        Assert.Equal(75, mapping.AudioBeatPulseDecayPercentOverride);
        Assert.Equal(85, mapping.AudioBeatPulseThresholdPercentOverride);
        Assert.Equal(PluginConfiguration.AudioColorPaletteBand, mapping.AudioColorPaletteOverride);
        Assert.Equal(PluginConfiguration.AudioSpatialModeUniform, mapping.AudioSpatialModeOverride);
        Assert.Equal(PluginConfiguration.AudioChannelModeStereo, mapping.AudioChannelModeOverride);
        Assert.Single(document.ColorPresets);
        Assert.Single(document.SceneSchedules);
        Assert.Equal("Morning cue", document.SceneSchedules[0].Name);
        Assert.Equal(55, document.SceneSchedules[0].Priority);
        Assert.Equal(PluginConfiguration.SceneAutomationPlaybackPolicyDefer, document.SceneSchedules[0].PlaybackPolicy);
        Assert.Equal(PluginConfiguration.SceneAutomationPlaybackPolicyDefer, document.SceneSchedules[0].EffectivePlaybackPolicy);
        Assert.Equal("2026-08-01", document.SceneSchedules[0].StartDate);
        Assert.Equal("2026-12-31", document.SceneSchedules[0].EndDate);
        Assert.Equal(new[] { "2026-12-24" }, document.SceneSchedules[0].ExcludedDates);
        Assert.Equal(PluginConfiguration.SceneScheduleRecurrenceMonthly, document.SceneSchedules[0].Recurrence);
        Assert.Equal(31, document.SceneSchedules[0].DayOfMonth);
        Assert.Equal(11, document.SceneSchedules[0].DurationSeconds);
        Assert.Equal(62, document.SceneSchedules[0].BrightnessPercent);
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
        Assert.Equal(PluginConfiguration.SceneAutomationPlaybackScopeMatchingTarget, document.Configuration.SceneAutomationPlaybackScope);

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
    public void ExportConfiguration_RejectsActiveConfigurationMutation()
    {
        InstallConfiguration(new PluginConfiguration());
        var gate = new HueBridgeLifecycleGate();
        using var mutation = gate.TryEnterConfigurationMutation();
        Assert.NotNull(mutation);

        var action = CreateController(bridgeLifecycleGate: gate).ExportConfiguration();

        var response = Assert.IsType<ConflictObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        Assert.Null(action.Value);
    }

    [Fact]
    public void ValidateConfigurationImport_ReturnsCredentialSafePlanWithoutChangingConfiguration()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = false,
            HueAppKey = "existing-app-secret",
            HueClientKey = "existing-client-secret",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Existing" }
            }
        });
        var controller = CreateController();

        var action = controller.ValidateConfigurationImport(new HueConfigurationImportRequest
        {
            Configuration = HuePluginConfigurationSettings.From(configuration),
            ColorPresets = new List<HueColorPresetRequest>
            {
                new() { Name = "Imported", Red = 20, Green = 30, Blue = 40 }
            }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueConfigurationImportValidationResult>(response.Value);
        Assert.True(result.Valid);
        Assert.True(result.CanImport);
        Assert.Equal(1, result.ColorPresetsImported);
        Assert.Equal(1, result.TotalColorPresets);
        Assert.True(result.GlobalAppKeyPreserved);
        Assert.True(result.GlobalClientKeyPreserved);
        Assert.Equal("Existing", Assert.Single(configuration.ColorPresets).Name);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("existing-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("existing-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateConfigurationImport_RejectsActiveConfigurationMutation()
    {
        var configuration = InstallConfiguration(new PluginConfiguration());
        var gate = new HueBridgeLifecycleGate();
        using var mutation = gate.TryEnterConfigurationMutation();
        Assert.NotNull(mutation);

        var action = CreateController(bridgeLifecycleGate: gate)
            .ValidateConfigurationImport(CreateConfigurationImportRequest(configuration));

        var response = Assert.IsType<ConflictObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        Assert.Null(action.Value);
    }

    [Fact]
    public void ValidateConfigurationImport_AcceptsMaximumUserMappingCollection()
    {
        var configuration = InstallConfiguration(new PluginConfiguration { SyncEnabled = false });
        var request = new HueConfigurationImportRequest
        {
            Configuration = HuePluginConfigurationSettings.From(configuration),
            UserMappings = Enumerable.Range(0, PluginConfiguration.MaxUserMappings)
                .Select(_ => new UserBridgeMappingImport
                {
                    UserId = Guid.NewGuid().ToString("D"),
                    SyncEnabled = false
                })
                .ToList()
        };

        var action = CreateController().ValidateConfigurationImport(request);

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueConfigurationImportValidationResult>(response.Value);
        Assert.True(result.Valid);
        Assert.True(result.CanImport);
        Assert.Equal(PluginConfiguration.MaxUserMappings, result.MappingsImported);
        Assert.Equal(PluginConfiguration.MaxUserMappings, result.TotalMappings);
        Assert.Empty(configuration.UserMappings);
    }

    [Fact]
    public void ConfigurationImport_RejectsOversizedUserMappingCollectionBeforePlanning()
    {
        var existingMapping = new UserBridgeMapping
        {
            UserId = "99999999-9999-9999-9999-999999999999",
            UserName = "Existing viewer",
            SyncEnabled = false
        };
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = false,
            UserMappings = new List<UserBridgeMapping> { existingMapping }
        });
        var request = new HueConfigurationImportRequest
        {
            Configuration = HuePluginConfigurationSettings.From(configuration),
            UserMappings = Enumerable.Range(0, PluginConfiguration.MaxUserMappings + 1)
                .Select(_ => new UserBridgeMappingImport
                {
                    UserId = Guid.NewGuid().ToString("D"),
                    SyncEnabled = false
                })
                .ToList()
        };
        var controller = CreateController();

        var validation = controller.ValidateConfigurationImport(request);

        var validationResponse = Assert.IsType<OkObjectResult>(validation.Result);
        var validationResult = Assert.IsType<HueConfigurationImportValidationResult>(validationResponse.Value);
        Assert.False(validationResult.Valid);
        Assert.False(validationResult.CanImport);
        Assert.Contains(
            $"No more than {PluginConfiguration.MaxUserMappings} user mappings may be imported.",
            validationResult.ValidationErrors);
        Assert.Same(existingMapping, Assert.Single(configuration.UserMappings));

        var import = controller.ImportConfiguration(request);

        var importResponse = Assert.IsType<BadRequestObjectResult>(import.Result);
        Assert.Contains(
            $"No more than {PluginConfiguration.MaxUserMappings} user mappings may be imported.",
            JsonSerializer.Serialize(importResponse.Value));
        Assert.Same(existingMapping, Assert.Single(configuration.UserMappings));
    }

    [Fact]
    public void ConfigurationImport_AcceptsMaximumColorPresetCollection()
    {
        var configuration = InstallConfiguration(new PluginConfiguration { SyncEnabled = false });
        var request = CreateConfigurationImportRequest(configuration);
        request.ColorPresets = Enumerable.Range(0, PluginConfiguration.MaxColorPresets)
            .Select(index => new HueColorPresetRequest
            {
                Name = $"Imported scene {index}",
                Red = index,
                Green = index + 50,
                Blue = index + 100
            })
            .ToList();

        AssertConfigurationImportAccepted(
            configuration,
            request,
            expectedPresets: PluginConfiguration.MaxColorPresets,
            expectedPlaylists: 0,
            expectedSchedules: 0);
    }

    [Fact]
    public void ConfigurationImport_AcceptsMaximumScenePlaylistCollection()
    {
        var configuration = InstallConfiguration(new PluginConfiguration { SyncEnabled = false });
        var request = CreateConfigurationImportRequest(configuration);
        request.ColorPresets = new List<HueColorPresetRequest>
        {
            new() { Name = "Imported scene" }
        };
        request.ScenePlaylists = Enumerable.Range(0, PluginConfiguration.MaxScenePlaylists)
            .Select(index => new HueScenePlaylistRequest
            {
                Id = $"imported-playlist-{index}",
                Name = $"Imported playlist {index}",
                PresetNames = new List<string> { "Imported scene" }
            })
            .ToList();

        AssertConfigurationImportAccepted(
            configuration,
            request,
            expectedPresets: 1,
            expectedPlaylists: PluginConfiguration.MaxScenePlaylists,
            expectedSchedules: 0);
    }

    [Fact]
    public void ConfigurationImport_AcceptsMaximumSceneScheduleCollection()
    {
        var configuration = InstallConfiguration(new PluginConfiguration { SyncEnabled = false });
        var request = CreateConfigurationImportRequest(configuration);
        request.ColorPresets = new List<HueColorPresetRequest>
        {
            new() { Name = "Imported scene" }
        };
        request.SceneSchedules = Enumerable.Range(0, PluginConfiguration.MaxSceneSchedules)
            .Select(index => new HueSceneScheduleRequest
            {
                Id = $"imported-schedule-{index}",
                Name = $"Imported schedule {index}",
                PresetName = "Imported scene",
                TimeOfDay = "20:00",
                TimeZoneId = TimeZoneInfo.Utc.Id,
                Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                DaysOfWeekMask = 0,
                Enabled = false
            })
            .ToList();

        AssertConfigurationImportAccepted(
            configuration,
            request,
            expectedPresets: 1,
            expectedPlaylists: 0,
            expectedSchedules: PluginConfiguration.MaxSceneSchedules);
    }

    [Fact]
    public void ConfigurationImport_AcceptsMaximumPlaylistStepAndTargetCollections()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "global-app-key",
            HueClientKey = "global-client-key",
            EntertainmentAreaId = "area-1",
            UserMappings = Enumerable.Range(0, PluginConfiguration.MaxSceneScheduleTargetMappings)
                .Select(index => new UserBridgeMapping
                {
                    UserId = Guid.NewGuid().ToString("D"),
                    UserName = $"Viewer {index}",
                    SyncEnabled = true
                })
                .ToList()
        });
        var sceneNames = Enumerable.Range(0, PluginConfiguration.MaxScenePlaylistItems)
            .Select(index => $"Scene {index}")
            .ToList();
        var request = CreateConfigurationImportRequest(configuration);
        request.ReplaceMappings = false;
        request.ColorPresets = sceneNames
            .Select(name => new HueColorPresetRequest { Name = name })
            .ToList();
        request.ScenePlaylists = new List<HueScenePlaylistRequest>
        {
            new()
            {
                Id = "maximum-playlist",
                Name = "Maximum playlist",
                PresetNames = sceneNames,
                StepDurationSeconds = Enumerable.Repeat(0, PluginConfiguration.MaxScenePlaylistItems).ToList(),
                StepRed = Enumerable.Repeat<int?>(null, PluginConfiguration.MaxScenePlaylistItems).ToList(),
                StepGreen = Enumerable.Repeat<int?>(null, PluginConfiguration.MaxScenePlaylistItems).ToList(),
                StepBlue = Enumerable.Repeat<int?>(null, PluginConfiguration.MaxScenePlaylistItems).ToList(),
                StepBrightnessPercent = Enumerable.Repeat<int?>(null, PluginConfiguration.MaxScenePlaylistItems).ToList(),
                StepEffects = Enumerable.Repeat<string?>(null, PluginConfiguration.MaxScenePlaylistItems).ToList(),
                StepEffectSpeedPercent = Enumerable.Repeat<int?>(null, PluginConfiguration.MaxScenePlaylistItems).ToList(),
                StepTransitionSeconds = Enumerable.Repeat<int?>(null, PluginConfiguration.MaxScenePlaylistItems).ToList(),
                StepTransitionOutSeconds = Enumerable.Repeat<int?>(null, PluginConfiguration.MaxScenePlaylistItems).ToList(),
                StepTransitionCurves = Enumerable.Repeat<string?>(null, PluginConfiguration.MaxScenePlaylistItems).ToList(),
                TargetUserIds = configuration.UserMappings.Select(mapping => mapping.UserId).ToList()
            }
        };

        AssertConfigurationImportAccepted(
            configuration,
            request,
            expectedPresets: PluginConfiguration.MaxScenePlaylistItems,
            expectedPlaylists: 1,
            expectedSchedules: 0);
    }

    [Fact]
    public void ConfigurationImport_RejectsOversizedTopLevelCollectionsBeforePlanning()
    {
        var configuration = InstallConfiguration(new PluginConfiguration { SyncEnabled = false });

        var presetRequest = CreateConfigurationImportRequest(configuration);
        presetRequest.ColorPresets = Enumerable.Range(0, PluginConfiguration.MaxColorPresets + 1)
            .Select(index => new HueColorPresetRequest { Name = $"Oversized scene {index}" })
            .ToList();
        AssertConfigurationImportCapacityRejected(
            configuration,
            presetRequest,
            $"Imported color preset collection may contain no more than {PluginConfiguration.MaxColorPresets} items.");

        var playlistRequest = CreateConfigurationImportRequest(configuration);
        playlistRequest.ScenePlaylists = Enumerable.Range(0, PluginConfiguration.MaxScenePlaylists + 1)
            .Select(index => new HueScenePlaylistRequest { Id = $"oversized-playlist-{index}" })
            .ToList();
        AssertConfigurationImportCapacityRejected(
            configuration,
            playlistRequest,
            $"Imported scene playlist collection may contain no more than {PluginConfiguration.MaxScenePlaylists} items.");

        var scheduleRequest = CreateConfigurationImportRequest(configuration);
        scheduleRequest.SceneSchedules = Enumerable.Range(0, PluginConfiguration.MaxSceneSchedules + 1)
            .Select(index => new HueSceneScheduleRequest { Id = $"oversized-schedule-{index}" })
            .ToList();
        AssertConfigurationImportCapacityRejected(
            configuration,
            scheduleRequest,
            $"Imported scene schedule collection may contain no more than {PluginConfiguration.MaxSceneSchedules} items.");
    }

    [Fact]
    public void ConfigurationImport_RejectsOversizedPlaylistNestedCollectionsBeforePlanning()
    {
        var configuration = InstallConfiguration(new PluginConfiguration { SyncEnabled = false });
        var oversizedPlaylist = new HueScenePlaylistRequest
        {
            PresetNames = Enumerable.Repeat("Scene", PluginConfiguration.MaxScenePlaylistItems + 1).ToList(),
            StepDurationSeconds = Enumerable.Repeat(0, PluginConfiguration.MaxScenePlaylistItems + 1).ToList(),
            StepRed = Enumerable.Repeat<int?>(null, PluginConfiguration.MaxScenePlaylistItems + 1).ToList(),
            StepGreen = Enumerable.Repeat<int?>(null, PluginConfiguration.MaxScenePlaylistItems + 1).ToList(),
            StepBlue = Enumerable.Repeat<int?>(null, PluginConfiguration.MaxScenePlaylistItems + 1).ToList(),
            StepBrightnessPercent = Enumerable.Repeat<int?>(null, PluginConfiguration.MaxScenePlaylistItems + 1).ToList(),
            StepEffects = Enumerable.Repeat<string?>(null, PluginConfiguration.MaxScenePlaylistItems + 1).ToList(),
            StepEffectSpeedPercent = Enumerable.Repeat<int?>(null, PluginConfiguration.MaxScenePlaylistItems + 1).ToList(),
            StepTransitionSeconds = Enumerable.Repeat<int?>(null, PluginConfiguration.MaxScenePlaylistItems + 1).ToList(),
            StepTransitionOutSeconds = Enumerable.Repeat<int?>(null, PluginConfiguration.MaxScenePlaylistItems + 1).ToList(),
            StepTransitionCurves = Enumerable.Repeat<string?>(null, PluginConfiguration.MaxScenePlaylistItems + 1).ToList(),
            TargetUserIds = Enumerable.Range(0, PluginConfiguration.MaxSceneScheduleTargetMappings + 1)
                .Select(_ => Guid.NewGuid().ToString("D"))
                .ToList()
        };
        var request = CreateConfigurationImportRequest(configuration);
        request.ScenePlaylists = new List<HueScenePlaylistRequest> { oversizedPlaylist };

        AssertConfigurationImportCapacityRejected(
            configuration,
            request,
            $"Imported scene playlist 1 saved scenes may contain no more than {PluginConfiguration.MaxScenePlaylistItems} items.",
            $"Imported scene playlist 1 step durations may contain no more than {PluginConfiguration.MaxScenePlaylistItems} items.",
            $"Imported scene playlist 1 step red overrides may contain no more than {PluginConfiguration.MaxScenePlaylistItems} items.",
            $"Imported scene playlist 1 step green overrides may contain no more than {PluginConfiguration.MaxScenePlaylistItems} items.",
            $"Imported scene playlist 1 step blue overrides may contain no more than {PluginConfiguration.MaxScenePlaylistItems} items.",
            $"Imported scene playlist 1 step brightness overrides may contain no more than {PluginConfiguration.MaxScenePlaylistItems} items.",
            $"Imported scene playlist 1 step effects may contain no more than {PluginConfiguration.MaxScenePlaylistItems} items.",
            $"Imported scene playlist 1 step effect-speed overrides may contain no more than {PluginConfiguration.MaxScenePlaylistItems} items.",
            $"Imported scene playlist 1 step transition overrides may contain no more than {PluginConfiguration.MaxScenePlaylistItems} items.",
            $"Imported scene playlist 1 step fade-out overrides may contain no more than {PluginConfiguration.MaxScenePlaylistItems} items.",
            $"Imported scene playlist 1 step transition curves may contain no more than {PluginConfiguration.MaxScenePlaylistItems} items.",
            $"Imported scene playlist 1 target user IDs may contain no more than {PluginConfiguration.MaxSceneScheduleTargetMappings} items.");
    }

    [Fact]
    public void ConfigurationImport_RejectsOversizedScheduleTargetAndExcludedDateCollectionsBeforePlanning()
    {
        var configuration = InstallConfiguration(new PluginConfiguration { SyncEnabled = false });
        var request = CreateConfigurationImportRequest(configuration);
        request.SceneSchedules = new List<HueSceneScheduleRequest>
        {
            new()
            {
                TargetUserIds = Enumerable.Range(0, PluginConfiguration.MaxSceneScheduleTargetMappings)
                    .Select(_ => Guid.NewGuid().ToString("D"))
                    .ToList(),
                TargetRoutes = new List<HueSceneScheduleTargetRoute>
                {
                    new() { UserId = Guid.NewGuid().ToString("D"), DeviceId = "device-1" }
                },
                ExcludedDates = Enumerable.Range(0, PluginConfiguration.MaxSceneScheduleExcludedDates + 1)
                    .Select(index => $"2026-01-{(index % 28) + 1:00}")
                    .ToList()
            }
        };

        AssertConfigurationImportCapacityRejected(
            configuration,
            request,
            $"Imported scene schedule 1 target selection may contain no more than {PluginConfiguration.MaxSceneScheduleTargetMappings} target routes.",
            $"Imported scene schedule 1 excluded dates may contain no more than {PluginConfiguration.MaxSceneScheduleExcludedDates} items.");
    }

    [Fact]
    public void ConfigurationImport_RejectsOversizedMappingDeviceCollectionsBeforePlanning()
    {
        var configuration = InstallConfiguration(new PluginConfiguration { SyncEnabled = false });
        var request = CreateConfigurationImportRequest(configuration);
        request.UserMappings = new List<UserBridgeMappingImport>
        {
            new()
            {
                UserId = Guid.NewGuid().ToString("D"),
                DeviceTargets = Enumerable.Range(0, PluginConfiguration.MaxDeviceTargetsPerUser + 1)
                    .Select(index => new UserDeviceBridgeTargetSummary { DeviceId = $"device-{index}" })
                    .ToList(),
                DeviceTargetCredentials = Enumerable.Range(0, PluginConfiguration.MaxDeviceTargetsPerUser + 1)
                    .Select(index => new UserDeviceBridgeTargetImport { DeviceId = $"device-{index}" })
                    .ToList()
            }
        };

        AssertConfigurationImportCapacityRejected(
            configuration,
            request,
            $"Imported user mapping 1 device targets may contain no more than {PluginConfiguration.MaxDeviceTargetsPerUser} items.",
            $"Imported user mapping 1 device-target credentials may contain no more than {PluginConfiguration.MaxDeviceTargetsPerUser} items.");
    }

    [Fact]
    public void ConfigurationImport_ReportsAndRejectsActiveBridgeLifecycle()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = false,
            HueAppKey = "active-lifecycle-app-secret",
            HueClientKey = "active-lifecycle-client-secret"
        });
        var gate = new HueBridgeLifecycleGate();
        using var diagnostic = gate.TryEnterDiagnostic("192.168.1.100|area-1");
        Assert.NotNull(diagnostic);
        var controller = CreateController(bridgeLifecycleGate: gate);
        var request = new HueConfigurationImportRequest
        {
            Configuration = HuePluginConfigurationSettings.From(configuration)
        };

        var validation = controller.ValidateConfigurationImport(request);
        var validationResponse = Assert.IsType<OkObjectResult>(validation.Result);
        var validationResult = Assert.IsType<HueConfigurationImportValidationResult>(validationResponse.Value);
        Assert.True(validationResult.Valid);
        Assert.False(validationResult.CanImport);
        Assert.True(validationResult.ActiveDiagnostic);
        Assert.Contains("diagnostic", validationResult.Message, StringComparison.OrdinalIgnoreCase);

        var import = controller.ImportConfiguration(request);
        var conflict = Assert.IsType<ConflictObjectResult>(import.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Contains("diagnostic", Assert.IsType<string>(conflict.Value), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("active-lifecycle-app-secret", configuration.HueAppKey);
    }

    [Fact]
    public void ConfigurationImport_ReportsAndRejectsActivePlaybackLifecycle()
    {
        var configuration = InstallConfiguration(new PluginConfiguration { SyncEnabled = false });
        var gate = new HueBridgeLifecycleGate();
        using var playback = gate.TryEnterPlayback("192.168.1.100|area-1");
        Assert.NotNull(playback);
        var controller = CreateController(bridgeLifecycleGate: gate);
        var request = new HueConfigurationImportRequest
        {
            Configuration = HuePluginConfigurationSettings.From(configuration)
        };

        var validation = controller.ValidateConfigurationImport(request);
        var validationResponse = Assert.IsType<OkObjectResult>(validation.Result);
        var validationResult = Assert.IsType<HueConfigurationImportValidationResult>(validationResponse.Value);
        Assert.False(validationResult.CanImport);
        Assert.True(validationResult.ActivePlayback);

        var import = controller.ImportConfiguration(request);
        var conflict = Assert.IsType<ConflictObjectResult>(import.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Contains("playback", Assert.IsType<string>(conflict.Value), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfigurationImport_ReportsAndRejectsSchedulerEvaluation()
    {
        var configuration = InstallConfiguration(new PluginConfiguration { SyncEnabled = false });
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        SetPrivateField(service, "_schedulerEvaluationCount", 1);
        try
        {
            var controller = CreateController(hostedServices: new[] { service });
            var request = new HueConfigurationImportRequest
            {
                Configuration = HuePluginConfigurationSettings.From(configuration)
            };

            var validation = controller.ValidateConfigurationImport(request);
            var validationResponse = Assert.IsType<OkObjectResult>(validation.Result);
            var validationResult = Assert.IsType<HueConfigurationImportValidationResult>(validationResponse.Value);
            Assert.False(validationResult.CanImport);
            Assert.True(validationResult.ActiveScheduleEvaluation);
            Assert.Contains("evaluation", validationResult.Message, StringComparison.OrdinalIgnoreCase);

            var import = controller.ImportConfiguration(request);
            var conflict = Assert.IsType<ConflictObjectResult>(import.Result);
            Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
            Assert.Contains("evaluation", Assert.IsType<string>(conflict.Value), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SetPrivateField(service, "_schedulerEvaluationCount", 0);
        }
    }

    [Fact]
    public async Task ConfigurationImport_RefusesActiveCueAndPreservesConfiguration()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "active-import-app-key",
            HueClientKey = "active-import-client-key",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Active import scene", DurationSeconds = 8 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "active-import-cue",
                    Name = "Active import cue",
                    PresetName = "Active import scene",
                    MaxRuns = 3
                }
            }
        });
        SetupHttpResponse(
            HttpStatusCode.OK,
            "{\"data\":[{\"channels\":[{\"channel_id\":0}]}]}");
        var streamTester = new BlockingPreviewStreamTester();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(_httpClient, _loggerMock.Object),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var controller = CreateController(hostedServices: new[] { service });
        var importRequest = new HueConfigurationImportRequest
        {
            Configuration = HuePluginConfigurationSettings.From(configuration),
            ReplaceMappings = false,
            ReplaceColorPresets = false,
            ReplaceScenePlaylists = false,
            ReplaceSceneSchedules = true
        };
        var runTask = service.RunScheduleAsync("active-import-cue");

        await streamTester.PreviewStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var validation = controller.ValidateConfigurationImport(importRequest);

            var validationResponse = Assert.IsType<OkObjectResult>(validation.Result);
            var validationResult = Assert.IsType<HueConfigurationImportValidationResult>(validationResponse.Value);
            Assert.True(validationResult.Valid);
            Assert.False(validationResult.CanImport);
            Assert.True(validationResult.ActiveScheduledCue);
            Assert.False(validationResult.ActiveScheduleEvaluation);
            Assert.Contains("scheduled scene cues", validationResult.Message, StringComparison.OrdinalIgnoreCase);

            var action = controller.ImportConfiguration(importRequest);

            var response = Assert.IsType<ConflictObjectResult>(action.Result);
            Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
            Assert.Contains("scheduled scene cue", Assert.IsType<string>(response.Value), StringComparison.OrdinalIgnoreCase);
            var unchanged = Assert.Single(configuration.SceneSchedules);
            Assert.Equal("active-import-cue", unchanged.Id);
            Assert.Equal("Active import cue", unchanged.Name);
            Assert.Equal("Active import scene", unchanged.PresetName);
        }
        finally
        {
            streamTester.ReleasePreview.TrySetResult(true);
            await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var inactiveImport = controller.ImportConfiguration(importRequest);
        Assert.IsType<OkObjectResult>(inactiveImport.Result);
        Assert.Empty(configuration.SceneSchedules);
    }

    [Fact]
    public void ValidateConfigurationImport_ReturnsNormalizedObjectDiffWithoutCredentials()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = false,
            HueAppKey = "old-app-secret",
            HueClientKey = "old-client-secret",
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = "11111111-1111-1111-1111-111111111111", UserName = "Keep mapping", SyncEnabled = false },
                new() { UserId = "22222222-2222-2222-2222-222222222222", UserName = "Change mapping", SyncEnabled = false },
                new() { UserId = "33333333-3333-3333-3333-333333333333", UserName = "Remove mapping", SyncEnabled = false }
            },
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Keep", Red = 10, Green = 20, Blue = 30 },
                new() { Name = "Change", Red = 40, Green = 50, Blue = 60 },
                new() { Name = "Remove", Red = 70, Green = 80, Blue = 90 }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new() { Id = "playlist-keep", Name = "Keep playlist", PresetNames = new List<string> { "Keep" } },
                new() { Id = "playlist-change", Name = "Change playlist", PresetNames = new List<string> { "Change" }, StepEffectSpeedPercent = new List<int?> { 150 } },
                new() { Id = "playlist-remove", Name = "Remove playlist", PresetNames = new List<string> { "Remove" } }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "schedule-keep", Name = "Keep cue", PresetName = "Keep", RunDate = "2030-01-01" },
                new() { Id = "schedule-change", Name = "Change cue", PresetName = "Change", RunDate = "2030-01-01" },
                new() { Id = "schedule-remove", Name = "Remove cue", PresetName = "Remove", RunDate = "2030-01-01" }
            }
        });
        var settings = HuePluginConfigurationSettings.From(configuration);
        settings.HueAppKey = "new-app-secret";
        settings.HueClientKey = "new-client-secret";

        var action = CreateController().ValidateConfigurationImport(new HueConfigurationImportRequest
        {
            Configuration = settings,
            UserMappings = new List<UserBridgeMappingImport>
            {
                new() { UserId = "11111111-1111-1111-1111-111111111111", UserName = "Keep mapping", SyncEnabled = false },
                new() { UserId = "22222222-2222-2222-2222-222222222222", UserName = "Changed mapping", SyncEnabled = false },
                new() { UserId = "44444444-4444-4444-4444-444444444444", UserName = "Add mapping", SyncEnabled = false }
            },
            ColorPresets = new List<HueColorPresetRequest>
            {
                new() { Name = "Keep", Red = 10, Green = 20, Blue = 30 },
                new() { Name = "Change", Red = 41, Green = 50, Blue = 60 },
                new() { Name = "Add", Red = 100, Green = 110, Blue = 120 }
            },
            ScenePlaylists = new List<HueScenePlaylistRequest>
            {
                new() { Id = "playlist-keep", Name = "Keep playlist", PresetNames = new List<string> { "Keep" } },
                new() { Id = "playlist-change", Name = "Change playlist", PresetNames = new List<string> { "Change" }, StepEffectSpeedPercent = new List<int?> { 175 } },
                new() { Id = "playlist-add", Name = "Add playlist", PresetNames = new List<string> { "Add" } }
            },
            SceneSchedules = new List<HueSceneScheduleRequest>
            {
                new() { Id = "schedule-keep", Name = "Keep cue", PresetName = "Keep", RunDate = "2030-01-01" },
                new() { Id = "schedule-change", Name = "Changed cue", PresetName = "Change", RunDate = "2030-01-01" },
                new() { Id = "schedule-add", Name = "Add cue", PresetName = "Add", RunDate = "2030-01-01" }
            }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueConfigurationImportValidationResult>(response.Value);
        Assert.True(result.Valid);
        Assert.True(result.CanImport);
        Assert.True(result.Diff.HasChanges);
        Assert.False(result.Diff.GlobalSettingsChanged);
        Assert.True(result.Diff.GlobalAppKeyChanged);
        Assert.True(result.Diff.GlobalClientKeyChanged);
        Assert.Equal(1, result.Diff.Mappings.Added);
        Assert.Equal(1, result.Diff.Mappings.Removed);
        Assert.Equal(1, result.Diff.Mappings.Changed);
        Assert.Equal(1, result.Diff.Mappings.Unchanged);
        Assert.Equal(1, result.Diff.ColorPresets.Added);
        Assert.Equal(1, result.Diff.ColorPresets.Removed);
        Assert.Equal(1, result.Diff.ColorPresets.Changed);
        Assert.Equal(1, result.Diff.ColorPresets.Unchanged);
        Assert.Equal(1, result.Diff.ScenePlaylists.Added);
        Assert.Equal(1, result.Diff.ScenePlaylists.Removed);
        Assert.Equal(1, result.Diff.ScenePlaylists.Changed);
        Assert.Equal(1, result.Diff.ScenePlaylists.Unchanged);
        Assert.Equal(1, result.Diff.SceneSchedules.Added);
        Assert.Equal(1, result.Diff.SceneSchedules.Removed);
        Assert.Equal(1, result.Diff.SceneSchedules.Changed);
        Assert.Equal(1, result.Diff.SceneSchedules.Unchanged);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("old-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("old-client-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("new-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("new-client-secret", serialized, StringComparison.Ordinal);
        Assert.Equal("Keep", Assert.Single(configuration.ColorPresets, preset => preset.Name == "Keep").Name);
    }

    [Fact]
    public void ValidateConfigurationImport_ReportsErrorsWithoutChangingConfiguration()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = false,
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Existing" }
            }
        });

        var action = CreateController().ValidateConfigurationImport(new HueConfigurationImportRequest
        {
            Configuration = HuePluginConfigurationSettings.From(configuration),
            ColorPresets = new List<HueColorPresetRequest>
            {
                new() { Name = string.Empty }
            }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueConfigurationImportValidationResult>(response.Value);
        Assert.False(result.Valid);
        Assert.False(result.CanImport);
        Assert.NotEmpty(result.ValidationErrors);
        Assert.Equal("Existing", Assert.Single(configuration.ColorPresets).Name);
    }

    [Fact]
    public void ValidateConfigurationImport_IgnoresNullExistingDeviceTargets()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = false,
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "55555555-5555-5555-5555-555555555555",
                    SyncEnabled = false,
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        null!,
                        new()
                        {
                            DeviceId = "living-room-tv",
                            HueBridgeIp = "192.168.1.102",
                            HueAppKey = "old-device-app",
                            HueClientKey = "old-device-client",
                            EntertainmentAreaId = "old-device-area"
                        }
                    }
                }
            }
        });

        var action = CreateController().ValidateConfigurationImport(new HueConfigurationImportRequest
        {
            Configuration = HuePluginConfigurationSettings.From(configuration),
            UserMappings = new List<UserBridgeMappingImport>
            {
                new()
                {
                    UserId = "55555555-5555-5555-5555-555555555555",
                    SyncEnabled = false,
                    DeviceTargets = new List<UserDeviceBridgeTargetSummary>
                    {
                        new()
                        {
                            DeviceId = "living-room-tv",
                            HueBridgeIp = "192.168.1.102",
                            EntertainmentAreaId = "new-device-area"
                        }
                    },
                    DeviceTargetCredentials = new List<UserDeviceBridgeTargetImport>
                    {
                        new()
                        {
                            DeviceId = "living-room-tv",
                            HueAppKey = "new-device-app",
                            HueClientKey = "new-device-client"
                        }
                    }
                }
            }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueConfigurationImportValidationResult>(response.Value);
        Assert.True(result.Valid);
        Assert.True(result.CanImport);
        Assert.Equal("old-device-app", configuration.UserMappings[0].DeviceTargets[1].HueAppKey);
        Assert.Null(configuration.UserMappings[0].DeviceTargets[0]);
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
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = "user-1", UserName = "Viewer", SyncEnabled = true }
            },
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
                    StepDurationSeconds = new List<int> { 2, 0 },
                    StepRed = new List<int?> { 220, null },
                    StepGreen = new List<int?> { 110, null },
                    StepBlue = new List<int?> { 35, null },
                    StepBrightnessPercent = new List<int?> { 35, null },
                    StepEffects = new List<string?> { PluginConfiguration.ColorPresetEffectFire, null },
                    StepEffectSpeedPercent = new List<int?> { 175, null },
                    StepTransitionSeconds = new List<int?> { 2, null },
                    StepTransitionOutSeconds = new List<int?> { null, 1 },
                    StepTransitionCurves = new List<string?> { "EaseInOut", null },
                    RepeatCount = 2,
                    PlaybackOrder = PluginConfiguration.ScenePlaylistOrderShuffle,
                    TargetUserIds = new List<string> { "user-1" },
                    IncludeDefaultTarget = true
                }
            }
        };
        var exported = HueConfigurationExportDocument.From(sourceConfiguration);
        var exportedPlaylist = Assert.Single(exported.ScenePlaylists);
        Assert.Equal("playlist-portable", exportedPlaylist.Id);
        Assert.Equal(new[] { "Sunrise", "Midnight" }, exportedPlaylist.PresetNames);
        Assert.Equal(new[] { 2, 0 }, exportedPlaylist.StepDurationSeconds);
        Assert.Equal(new int?[] { 220, null }, exportedPlaylist.StepRed);
        Assert.Equal(new int?[] { 110, null }, exportedPlaylist.StepGreen);
        Assert.Equal(new int?[] { 35, null }, exportedPlaylist.StepBlue);
        Assert.Equal(new int?[] { 35, null }, exportedPlaylist.StepBrightnessPercent);
        Assert.Equal(new[] { PluginConfiguration.ColorPresetEffectFire, null }, exportedPlaylist.StepEffects);
        Assert.Equal(new int?[] { 175, null }, exportedPlaylist.StepEffectSpeedPercent);
        Assert.Equal(new int?[] { 2, null }, exportedPlaylist.StepTransitionSeconds);
        Assert.Equal(new int?[] { null, 1 }, exportedPlaylist.StepTransitionOutSeconds);
        Assert.Equal(new[] { "EaseInOut", null }, exportedPlaylist.StepTransitionCurves);
        Assert.Equal(2, exportedPlaylist.RepeatCount);
        Assert.Equal(PluginConfiguration.ScenePlaylistOrderShuffle, exportedPlaylist.PlaybackOrder);
        Assert.Equal(new[] { "user-1" }, exportedPlaylist.TargetUserIds);
        Assert.True(exportedPlaylist.IncludeDefaultTarget);
        Assert.False(exportedPlaylist.TargetAllEnabledMappings);
        Assert.Equal(14, exportedPlaylist.TotalDurationSeconds);
        var serialized = JsonSerializer.Serialize(exported);
        Assert.DoesNotContain("source-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("source-client-secret", serialized, StringComparison.Ordinal);

        var destination = InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "destination-app-secret",
            HueClientKey = "destination-client-secret",
            EntertainmentAreaId = "destination-area",
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = "user-1", UserName = "Viewer", SyncEnabled = true }
            }
        });
        var action = CreateController().ImportConfiguration(new HueConfigurationImportRequest
        {
            SchemaVersion = exported.SchemaVersion,
            Configuration = exported.Configuration,
            ReplaceMappings = false,
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
                    StepDurationSeconds = exportedPlaylist.StepDurationSeconds.ToList(),
                    StepRed = exportedPlaylist.StepRed.ToList(),
                    StepGreen = exportedPlaylist.StepGreen.ToList(),
                    StepBlue = exportedPlaylist.StepBlue.ToList(),
                    StepBrightnessPercent = exportedPlaylist.StepBrightnessPercent.ToList(),
                    StepEffects = exportedPlaylist.StepEffects.ToList(),
                    StepEffectSpeedPercent = exportedPlaylist.StepEffectSpeedPercent.ToList(),
                    StepTransitionSeconds = exportedPlaylist.StepTransitionSeconds.ToList(),
                    StepTransitionOutSeconds = exportedPlaylist.StepTransitionOutSeconds.ToList(),
                    StepTransitionCurves = exportedPlaylist.StepTransitionCurves.ToList(),
                    RepeatCount = exportedPlaylist.RepeatCount,
                    PlaybackOrder = exportedPlaylist.PlaybackOrder,
                    TargetUserIds = exportedPlaylist.TargetUserIds.ToList(),
                    IncludeDefaultTarget = exportedPlaylist.IncludeDefaultTarget
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
        Assert.Equal(new[] { 2, 0 }, imported.StepDurationSeconds);
        Assert.Equal(new int?[] { 220, null }, imported.StepRed);
        Assert.Equal(new int?[] { 110, null }, imported.StepGreen);
        Assert.Equal(new int?[] { 35, null }, imported.StepBlue);
        Assert.Equal(new int?[] { 35, null }, imported.StepBrightnessPercent);
        Assert.Equal(new[] { PluginConfiguration.ColorPresetEffectFire, null }, imported.StepEffects);
        Assert.Equal(new int?[] { 175, null }, imported.StepEffectSpeedPercent);
        Assert.Equal(new int?[] { 2, null }, imported.StepTransitionSeconds);
        Assert.Equal(new int?[] { null, 1 }, imported.StepTransitionOutSeconds);
        Assert.Equal(new[] { "EaseInOut", null }, imported.StepTransitionCurves);
        Assert.Equal(2, imported.RepeatCount);
        Assert.Equal(PluginConfiguration.ScenePlaylistOrderShuffle, imported.PlaybackOrder);
        Assert.Equal(new[] { "user-1" }, imported.TargetUserIds);
        Assert.True(imported.IncludeDefaultTarget);
        Assert.False(imported.TargetAllEnabledMappings);
        var serializedResult = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("destination-app-secret", serializedResult, StringComparison.Ordinal);
        Assert.DoesNotContain("destination-client-secret", serializedResult, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationImport_RenamedPlaylistMigratesRetainedCueReference()
    {
        var destination = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Warm", DurationSeconds = 2 }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "portable-rename",
                    Name = "Old sequence",
                    PresetNames = new List<string> { "Warm" },
                    StepEffects = new List<string?> { PluginConfiguration.ColorPresetEffectStarlight }
                }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "retained-cue",
                    Name = "Retained playlist cue",
                    PlaylistName = "Old sequence",
                    TimeZoneId = TimeZoneInfo.Utc.Id
                }
            }
        });

        var action = CreateController().ImportConfiguration(new HueConfigurationImportRequest
        {
            Configuration = HuePluginConfigurationSettings.From(destination),
            ReplaceMappings = false,
            ReplaceColorPresets = false,
            ReplaceScenePlaylists = false,
            ReplaceSceneSchedules = false,
            ScenePlaylists = new List<HueScenePlaylistRequest>
            {
                new()
                {
                    Id = "portable-rename",
                    Name = "New sequence",
                    PresetNames = new List<string> { "Warm" }
                }
            }
        });

        Assert.IsType<OkObjectResult>(action.Result);
        var importedPlaylist = Assert.Single(destination.ScenePlaylists);
        Assert.Equal("New sequence", importedPlaylist.Name);
        Assert.Equal(new[] { PluginConfiguration.ColorPresetEffectStarlight }, importedPlaylist.StepEffects);
        Assert.Equal("New sequence", Assert.Single(destination.SceneSchedules).PlaylistName);
    }

    [Fact]
    public void ConfigurationExportAndImport_PreservesPlaylistBackedCueAndZeroDurationOverride()
    {
        var source = new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.111",
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
                    TimeZoneIanaId = exportedCue.TimeZoneIanaId,
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
        Assert.Equal(exportedCue.TimeZoneIanaId, importedCue.TimeZoneId);
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
                    BrightnessPercent = 39,
                    DaysOfWeekMask = 0
                }
            }
        });

        var exported = HueConfigurationExportDocument.From(configuration);
        var exportedCue = Assert.Single(exported.SceneSchedules);
        Assert.Equal("2026-12-24", exportedCue.RunDate);
        Assert.Equal(39, exportedCue.BrightnessPercent);
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
                    BrightnessPercent = exportedCue.BrightnessPercent,
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
        Assert.Equal(exportedCue.BrightnessPercent, importedCue.BrightnessPercent);
        Assert.Equal(exportedCue.Priority, importedCue.Priority);
        Assert.Equal(exportedCue.MaxRuns, importedCue.MaxRuns);
        Assert.Equal(exportedCue.RunCount, importedCue.RunCount);
        Assert.Equal(0, importedCue.DaysOfWeekMask);
        Assert.Equal(2, Assert.Single(configuration.ColorPresets).TransitionSeconds);
        Assert.Equal(1, Assert.Single(configuration.ColorPresets).TransitionOutSeconds);
        Assert.Equal(275, Assert.Single(configuration.ColorPresets).EffectSpeedPercent);
    }

    [Fact]
    public void ConfigurationImport_NormalizesWindowsTimeZoneAndRejectsUnmappableValue()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Portable" } }
        });
        var controller = CreateController();

        var import = controller.ImportConfiguration(new HueConfigurationImportRequest
        {
            Configuration = HuePluginConfigurationSettings.From(configuration),
            ReplaceColorPresets = false,
            SceneSchedules = new List<HueSceneScheduleRequest>
            {
                new()
                {
                    Id = "windows-zone-cue",
                    Name = "Windows zone cue",
                    PresetName = "Portable",
                    TimeZoneId = "Eastern Standard Time",
                    TimeOfDay = "08:00"
                }
            }
        });

        Assert.IsType<OkObjectResult>(import.Result);
        Assert.Equal("America/New_York", Assert.Single(configuration.SceneSchedules).TimeZoneId);

        var invalid = controller.ImportConfiguration(new HueConfigurationImportRequest
        {
            Configuration = HuePluginConfigurationSettings.From(configuration),
            ReplaceColorPresets = false,
            SceneSchedules = new List<HueSceneScheduleRequest>
            {
                new()
                {
                    Id = "invalid-zone-cue",
                    Name = "Invalid zone cue",
                    PresetName = "Portable",
                    TimeZoneId = "Definitely/Not-A-Real-Time-Zone",
                    TimeOfDay = "08:00"
                }
            }
        });

        var invalidResponse = Assert.IsType<BadRequestObjectResult>(invalid.Result);
        Assert.Contains(
            "portable IANA",
            JsonSerializer.Serialize(invalidResponse.Value),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(configuration.SceneSchedules, schedule => schedule.Id == "invalid-zone-cue");
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
        const string importedUserId = "66666666-6666-6666-6666-666666666666";
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
                    UserId = importedUserId,
                    UserName = "Viewer",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "mapping-app-secret",
                    HueClientKey = "mapping-client-secret",
                    EntertainmentAreaId = "area-2",
                    PlaybackMediaFilterOverride = PluginConfiguration.PlaybackMediaFilterMovies,
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
                    PlaybackMediaFilterOverride = summary.PlaybackMediaFilterOverride,
                    AudioSensitivityPercentOverride = 310,
                    AudioLowFrequencyHzOverride = 60,
                    AudioMidFrequencyHzOverride = 700,
                    AudioHighFrequencyHzOverride = 2400,
                    AudioLowGainPercentOverride = 45,
                    AudioMidGainPercentOverride = 135,
                    AudioHighGainPercentOverride = 185,
                    AudioResponseSmoothingPercentOverride = 65,
                    AudioBandSpreadPercentOverride = 35,
                    AudioBeatPulsePercentOverride = 65,
                    AudioBeatPulseThresholdPercentOverride = 70,
                    AudioColorPaletteOverride = PluginConfiguration.AudioColorPaletteCool,
                    AudioSpatialModeOverride = PluginConfiguration.AudioSpatialModeMirror,
                    AudioChannelModeOverride = PluginConfiguration.AudioChannelModeMono,
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
        Assert.Equal(PluginConfiguration.PlaybackMediaFilterMovies, mapping.PlaybackMediaFilterOverride);
        Assert.Equal(310, mapping.AudioSensitivityPercentOverride);
        Assert.Equal(60, mapping.AudioLowFrequencyHzOverride);
        Assert.Equal(700, mapping.AudioMidFrequencyHzOverride);
        Assert.Equal(2400, mapping.AudioHighFrequencyHzOverride);
        Assert.Equal(45, mapping.AudioLowGainPercentOverride);
        Assert.Equal(135, mapping.AudioMidGainPercentOverride);
        Assert.Equal(185, mapping.AudioHighGainPercentOverride);
        Assert.Equal(65, mapping.AudioResponseSmoothingPercentOverride);
        Assert.Equal(35, mapping.AudioBandSpreadPercentOverride);
        Assert.Equal(65, mapping.AudioBeatPulsePercentOverride);
        Assert.Equal(70, mapping.AudioBeatPulseThresholdPercentOverride);
        Assert.Equal(PluginConfiguration.AudioColorPaletteCool, mapping.AudioColorPaletteOverride);
        Assert.Equal(PluginConfiguration.AudioSpatialModeMirror, mapping.AudioSpatialModeOverride);
        Assert.Equal(PluginConfiguration.AudioChannelModeMono, mapping.AudioChannelModeOverride);
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
        const string importedUserId = "77777777-7777-7777-7777-777777777777";
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.99",
            HueAppKey = "old-global-app-secret",
            HueClientKey = "old-global-client-secret",
            EntertainmentAreaId = "old-global-area"
        });

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
                    UserId = importedUserId,
                    UserName = "Migrated Viewer",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "migrated-mapping-app-secret",
                    HueClientKey = "migrated-mapping-client-secret",
                    EntertainmentAreaId = "mapping-area",
                    DeviceTargets = new List<UserDeviceBridgeTargetSummary>
                    {
                        new()
                        {
                            DeviceId = "living-room-tv",
                            DeviceName = "Living Room TV",
                            HueBridgeIp = "192.168.1.102",
                            EntertainmentAreaId = "device-area"
                        }
                    },
                    DeviceTargetCredentials = new List<UserDeviceBridgeTargetImport>
                    {
                        new()
                        {
                            DeviceId = "living-room-tv",
                            HueAppKey = "migrated-device-app-secret",
                            HueClientKey = "migrated-device-client-secret"
                        }
                    }
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
        var deviceTarget = Assert.Single(mapping.DeviceTargets);
        Assert.Equal("living-room-tv", deviceTarget.DeviceId);
        Assert.Equal("migrated-device-app-secret", deviceTarget.HueAppKey);
        Assert.Equal("migrated-device-client-secret", deviceTarget.HueClientKey);

        var serializedResult = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("migrated-global-app-secret", serializedResult, StringComparison.Ordinal);
        Assert.DoesNotContain("migrated-global-client-secret", serializedResult, StringComparison.Ordinal);
        Assert.DoesNotContain("migrated-mapping-app-secret", serializedResult, StringComparison.Ordinal);
        Assert.DoesNotContain("migrated-mapping-client-secret", serializedResult, StringComparison.Ordinal);
        Assert.DoesNotContain("migrated-device-app-secret", serializedResult, StringComparison.Ordinal);
        Assert.DoesNotContain("migrated-device-client-secret", serializedResult, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportConfiguration_RejectsIncompleteNewTargetWithoutChangingConfiguration()
    {
        const string importedUserId = "88888888-8888-8888-8888-888888888888";
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
                    UserId = importedUserId,
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
    public void ImportConfiguration_RejectsChangedGlobalTargetWhenCredentialsAreOmittedWithoutMutation()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = false,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "stored-global-app",
            HueClientKey = "stored-global-client",
            EntertainmentAreaId = "area-1"
        });

        var action = CreateController().ImportConfiguration(new HueConfigurationImportRequest
        {
            Configuration = new HuePluginConfigurationSettings
            {
                SyncEnabled = false,
                HueBridgeIp = "192.168.1.101",
                EntertainmentAreaId = "area-2"
            }
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("changes the global bridge target", JsonSerializer.Serialize(response.Value), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("App Key", JsonSerializer.Serialize(response.Value), StringComparison.Ordinal);
        Assert.Contains("Client Key", JsonSerializer.Serialize(response.Value), StringComparison.Ordinal);
        Assert.Equal("192.168.1.100", configuration.HueBridgeIp);
        Assert.Equal("stored-global-app", configuration.HueAppKey);
        Assert.Equal("stored-global-client", configuration.HueClientKey);
        Assert.Equal("area-1", configuration.EntertainmentAreaId);
    }

    [Fact]
    public void ConfigurationImport_RejectsMalformedUserMappingIdWithoutChangingConfiguration()
    {
        const string existingUserId = "99999999-9999-9999-9999-999999999999";
        var existingMapping = new UserBridgeMapping
        {
            UserId = existingUserId,
            UserName = "Existing viewer",
            SyncEnabled = false
        };
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            HueAppKey = "existing-global-app",
            HueClientKey = "existing-global-client",
            UserMappings = new List<UserBridgeMapping> { existingMapping }
        });

        var action = CreateController().ImportConfiguration(new HueConfigurationImportRequest
        {
            Configuration = new HuePluginConfigurationSettings
            {
                HueAppKey = "replacement-global-app",
                HueClientKey = "replacement-global-client"
            },
            UserMappings = new List<UserBridgeMappingImport>
            {
                new()
                {
                    UserId = "not-a-jellyfin-user-id",
                    UserName = "Rejected viewer",
                    SyncEnabled = false
                }
            }
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Contains("valid Jellyfin user ID", JsonSerializer.Serialize(response.Value), StringComparison.OrdinalIgnoreCase);
        Assert.Same(existingMapping, Assert.Single(configuration.UserMappings));
        Assert.Equal(existingUserId, configuration.UserMappings[0].UserId);
        Assert.Equal("existing-global-app", configuration.HueAppKey);
        Assert.Equal("existing-global-client", configuration.HueClientKey);
    }

    [Fact]
    public void ConfigurationImport_NormalizesBraceUserMappingIdAndPreservesMatchingCredentials()
    {
        const string canonicalUserId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = canonicalUserId,
                    UserName = "Existing viewer",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.120",
                    HueAppKey = "stored-mapping-app",
                    HueClientKey = "stored-mapping-client",
                    EntertainmentAreaId = "stored-area"
                }
            }
        });

        var action = CreateController().ImportConfiguration(new HueConfigurationImportRequest
        {
            Configuration = HuePluginConfigurationSettings.From(configuration),
            UserMappings = new List<UserBridgeMappingImport>
            {
                new()
                {
                    UserId = "{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}",
                    UserName = "Imported viewer",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.120",
                    EntertainmentAreaId = "imported-area"
                }
            }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueConfigurationImportResult>(response.Value);
        Assert.Equal(1, result.MappingCredentialPairsPreserved);
        var imported = Assert.Single(configuration.UserMappings);
        Assert.Equal(canonicalUserId, imported.UserId);
        Assert.Equal("stored-mapping-app", imported.HueAppKey);
        Assert.Equal("stored-mapping-client", imported.HueClientKey);
        Assert.Equal("imported-area", imported.EntertainmentAreaId);
    }

    [Fact]
    public void ConfigurationImport_NormalizesBraceAndNFormatScheduleRouteUserIds()
    {
        const string canonicalUserId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Welcome" } },
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = canonicalUserId,
                    UserName = "Imported viewer",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.120",
                    HueAppKey = "stored-mapping-app",
                    HueClientKey = "stored-mapping-client",
                    EntertainmentAreaId = "stored-area",
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new()
                        {
                            DeviceId = "living-room-tv",
                            HueBridgeIp = "192.168.1.121",
                            HueAppKey = "stored-device-app",
                            HueClientKey = "stored-device-client",
                            EntertainmentAreaId = "living-room-area"
                        },
                        new()
                        {
                            DeviceId = "bedroom-tv",
                            HueBridgeIp = "192.168.1.122",
                            HueAppKey = "stored-bedroom-app",
                            HueClientKey = "stored-bedroom-client",
                            EntertainmentAreaId = "bedroom-area"
                        }
                    }
                }
            }
        });

        var action = CreateController().ImportConfiguration(new HueConfigurationImportRequest
        {
            Configuration = HuePluginConfigurationSettings.From(configuration),
            ReplaceMappings = false,
            ReplaceColorPresets = false,
            ReplaceSceneSchedules = true,
            SceneSchedules = new List<HueSceneScheduleRequest>
            {
                new()
                {
                    Id = "guid-route-schedule",
                    Name = "GUID route schedule",
                    PresetName = "Welcome",
                    TimeOfDay = "20:00",
                    DaysOfWeekMask = 127,
                    TargetRoutes = new List<HueSceneScheduleTargetRoute>
                    {
                        new() { UserId = "{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}", DeviceId = "living-room-tv" },
                        new() { UserId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", DeviceId = "bedroom-tv" }
                    }
                }
            }
        });

        Assert.IsType<OkObjectResult>(action.Result);
        var imported = Assert.Single(configuration.SceneSchedules);
        Assert.Equal(
            new[] { canonicalUserId, canonicalUserId },
            imported.TargetRoutes.Select(route => route.UserId));
        Assert.Equal(
            new[] { "living-room-tv", "bedroom-tv" },
            imported.TargetRoutes.Select(route => route.DeviceId));
    }

    [Fact]
    public void ConfigurationImport_RejectsDuplicateUserMappingsAfterGuidNormalization()
    {
        var configuration = InstallConfiguration(new PluginConfiguration());

        var action = CreateController().ImportConfiguration(new HueConfigurationImportRequest
        {
            Configuration = HuePluginConfigurationSettings.From(configuration),
            UserMappings = new List<UserBridgeMappingImport>
            {
                new() { UserId = "{BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB}", SyncEnabled = false },
                new() { UserId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", SyncEnabled = false }
            }
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("duplicates another imported user mapping", JsonSerializer.Serialize(response.Value), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(configuration.UserMappings);
    }

    [Fact]
    public void ConfigurationImport_ReplaceMappings_UsesStableMappingIdForCredentialPreservation()
    {
        const string userId = "cccccccc-cccc-cccc-cccc-cccccccccccc";
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    MappingId = "row-a",
                    UserId = userId,
                    UserName = "First duplicate",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "row-a-app-secret",
                    HueClientKey = "row-a-client-secret",
                    EntertainmentAreaId = "area-a"
                },
                new()
                {
                    MappingId = "row-b",
                    UserId = userId,
                    UserName = "Second duplicate",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.102",
                    HueAppKey = "row-b-app-secret",
                    HueClientKey = "row-b-client-secret",
                    EntertainmentAreaId = "area-b"
                }
            }
        });

        var action = CreateController().ImportConfiguration(new HueConfigurationImportRequest
        {
            Configuration = HuePluginConfigurationSettings.From(configuration),
            UserMappings = new List<UserBridgeMappingImport>
            {
                new()
                {
                    MappingId = "row-b",
                    UserId = userId,
                    UserName = "Updated second duplicate",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.102",
                    EntertainmentAreaId = "area-b-updated"
                }
            }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueConfigurationImportResult>(response.Value);
        Assert.Equal(1, result.MappingCredentialPairsPreserved);
        var imported = Assert.Single(configuration.UserMappings);
        Assert.Equal("row-b", imported.MappingId);
        Assert.Equal("Updated second duplicate", imported.UserName);
        Assert.Equal("row-b-app-secret", imported.HueAppKey);
        Assert.Equal("row-b-client-secret", imported.HueClientKey);
        Assert.Equal("area-b-updated", imported.EntertainmentAreaId);
    }

    [Fact]
    public void ConfigurationImport_PartialMerge_ReplacesOnlyExactStableMappingRow()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    MappingId = "row-a",
                    UserId = "dddddddd-dddd-dddd-dddd-dddddddddddd",
                    UserName = "First viewer",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.103",
                    HueAppKey = "row-a-app-secret",
                    HueClientKey = "row-a-client-secret",
                    EntertainmentAreaId = "area-a"
                },
                new()
                {
                    MappingId = "row-b",
                    UserId = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
                    UserName = "Second viewer",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.104",
                    HueAppKey = "row-b-app-secret",
                    HueClientKey = "row-b-client-secret",
                    EntertainmentAreaId = "area-b"
                }
            }
        });

        var action = CreateController().ImportConfiguration(new HueConfigurationImportRequest
        {
            Configuration = HuePluginConfigurationSettings.From(configuration),
            ReplaceMappings = false,
            UserMappings = new List<UserBridgeMappingImport>
            {
                new()
                {
                    MappingId = "row-b",
                    UserId = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
                    UserName = "Renamed second viewer",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.104",
                    EntertainmentAreaId = "area-b-updated"
                }
            }
        });

        Assert.IsType<OkObjectResult>(action.Result);
        Assert.Equal(2, configuration.UserMappings.Count);
        var first = Assert.Single(configuration.UserMappings, mapping => mapping.MappingId == "row-a");
        var second = Assert.Single(configuration.UserMappings, mapping => mapping.MappingId == "row-b");
        Assert.Equal("First viewer", first.UserName);
        Assert.Equal("row-a-app-secret", first.HueAppKey);
        Assert.Equal("row-a-client-secret", first.HueClientKey);
        Assert.Equal("Renamed second viewer", second.UserName);
        Assert.Equal("row-b-app-secret", second.HueAppKey);
        Assert.Equal("row-b-client-secret", second.HueClientKey);
        Assert.Equal("area-b-updated", second.EntertainmentAreaId);
    }

    [Fact]
    public void ConfigurationImport_UsesUniqueUserFallbackButKeepsDestinationStableMappingId()
    {
        const string userId = "abababab-abab-abab-abab-abababababab";
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    MappingId = "destination-row",
                    UserId = userId,
                    UserName = "Existing viewer",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.105",
                    HueAppKey = "destination-app-secret",
                    HueClientKey = "destination-client-secret",
                    EntertainmentAreaId = "destination-area"
                }
            }
        });

        var action = CreateController().ImportConfiguration(new HueConfigurationImportRequest
        {
            Configuration = HuePluginConfigurationSettings.From(configuration),
            ReplaceMappings = false,
            UserMappings = new List<UserBridgeMappingImport>
            {
                new()
                {
                    MappingId = "source-server-row",
                    UserId = userId,
                    UserName = "Migrated viewer",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.105",
                    EntertainmentAreaId = "migrated-area"
                }
            }
        });

        Assert.IsType<OkObjectResult>(action.Result);
        var imported = Assert.Single(configuration.UserMappings);
        Assert.Equal("destination-row", imported.MappingId);
        Assert.Equal("Migrated viewer", imported.UserName);
        Assert.Equal("destination-app-secret", imported.HueAppKey);
        Assert.Equal("destination-client-secret", imported.HueClientKey);
        Assert.Equal("migrated-area", imported.EntertainmentAreaId);
    }

    [Fact]
    public void ConfigurationImport_RejectsIdlessImportAgainstDuplicateRowsWithoutMutation()
    {
        const string userId = "ffffffff-ffff-ffff-ffff-ffffffffffff";
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    MappingId = "row-a",
                    UserId = userId,
                    UserName = "First duplicate",
                    SyncEnabled = false,
                    HueAppKey = "row-a-app-secret"
                },
                new()
                {
                    MappingId = "row-b",
                    UserId = userId,
                    UserName = "Second duplicate",
                    SyncEnabled = false,
                    HueAppKey = "row-b-app-secret"
                }
            }
        });

        var action = CreateController().ImportConfiguration(new HueConfigurationImportRequest
        {
            Configuration = HuePluginConfigurationSettings.From(configuration),
            ReplaceMappings = false,
            UserMappings = new List<UserBridgeMappingImport>
            {
                new()
                {
                    UserId = userId,
                    UserName = "Ambiguous import",
                    SyncEnabled = false
                }
            }
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        var serialized = JsonSerializer.Serialize(response.Value);
        Assert.Contains("multiple existing mapping rows", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, configuration.UserMappings.Count);
        Assert.Equal("row-a-app-secret", configuration.UserMappings[0].HueAppKey);
        Assert.Equal("row-b-app-secret", configuration.UserMappings[1].HueAppKey);
    }

    [Fact]
    public void ConfigurationImport_InvalidDocument_DoesNotMutateLegacyMappingIdentityOrCredentials()
    {
        var legacy = new UserBridgeMapping
        {
            MappingId = string.Empty,
            UserId = "99999999-9999-9999-9999-999999999999",
            UserName = "Legacy viewer",
            SyncEnabled = false,
            HueBridgeIp = "192.168.1.120",
            HueAppKey = "legacy-app-secret",
            HueClientKey = "legacy-client-secret",
            EntertainmentAreaId = "legacy-area",
            DeviceTargets = new List<UserDeviceBridgeTarget>
            {
                new()
                {
                    DeviceId = "living-room-tv",
                    DeviceName = "Living Room TV",
                    HueBridgeIp = "192.168.1.121",
                    HueAppKey = "legacy-device-app",
                    HueClientKey = "legacy-device-client",
                    EntertainmentAreaId = "living-room-area",
                    ChannelIdsOverride = "1, 3"
                }
            }
        };
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping> { legacy }
        });

        var action = CreateController().ImportConfiguration(new HueConfigurationImportRequest
        {
            Configuration = HuePluginConfigurationSettings.From(configuration),
            UserMappings = new List<UserBridgeMappingImport>
            {
                new() { UserId = "not-a-jellyfin-user-id", SyncEnabled = false }
            }
        });

        Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Empty(legacy.MappingId);
        Assert.Equal("legacy-app-secret", legacy.HueAppKey);
        Assert.Equal("legacy-client-secret", legacy.HueClientKey);
        var device = Assert.Single(legacy.DeviceTargets);
        Assert.Equal("living-room-tv", device.DeviceId);
        Assert.Equal("legacy-device-app", device.HueAppKey);
        Assert.Equal("legacy-device-client", device.HueClientKey);
        Assert.Equal("1, 3", device.ChannelIdsOverride);
        Assert.Same(legacy, Assert.Single(configuration.UserMappings));
    }

    [Fact]
    public void ConfigurationImport_InvalidDocument_DoesNotNormalizeDuplicateLegacyMappingIds()
    {
        var first = new UserBridgeMapping
        {
            MappingId = "legacy-duplicate",
            UserId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            UserName = "First legacy row",
            HueAppKey = "first-app-secret"
        };
        var second = new UserBridgeMapping
        {
            MappingId = "legacy-duplicate",
            UserId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            UserName = "Second legacy row",
            HueAppKey = "second-app-secret"
        };
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping> { first, second }
        });

        var action = CreateController().ImportConfiguration(new HueConfigurationImportRequest
        {
            Configuration = HuePluginConfigurationSettings.From(configuration),
            UserMappings = new List<UserBridgeMappingImport>
            {
                new() { UserId = "not-a-jellyfin-user-id", SyncEnabled = false }
            }
        });

        Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal("legacy-duplicate", first.MappingId);
        Assert.Equal("legacy-duplicate", second.MappingId);
        Assert.Equal("first-app-secret", first.HueAppKey);
        Assert.Equal("second-app-secret", second.HueAppKey);
        Assert.Same(first, configuration.UserMappings[0]);
        Assert.Same(second, configuration.UserMappings[1]);
    }

    [Fact]
    public void ImportConfiguration_WhenPersistenceFailsRestoresRetainedHistory()
    {
        var serializer = new Mock<IXmlSerializer>();
        serializer
            .Setup(xml => xml.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("import persistence failed"));
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            PersistSessionHistory = true,
            PersistSceneScheduleHistory = true,
            PersistedSessionHistory = new List<HueSessionHistoryEntry>
            {
                new() { Item = "Private title" }
            },
            PersistedSceneScheduleHistory = new List<HueSceneScheduleHistoryEntry>
            {
                new() { ScheduleId = "private-cue", ScheduleName = "Private cue" }
            }
        }, serializer.Object);

        var action = CreateController().ImportConfiguration(new HueConfigurationImportRequest
        {
            Configuration = new HuePluginConfigurationSettings
            {
                PersistSessionHistory = false,
                PersistSceneScheduleHistory = false
            }
        });

        var response = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, response.StatusCode);
        Assert.True(configuration.PersistSessionHistory);
        Assert.True(configuration.PersistSceneScheduleHistory);
        Assert.Equal("Private title", Assert.Single(configuration.PersistedSessionHistory).Item);
        Assert.Equal("private-cue", Assert.Single(configuration.PersistedSceneScheduleHistory).ScheduleId);
    }

    [Fact]
    public void ImportConfiguration_WhenPersistenceFails_DoesNotMutateLegacyMappingRows()
    {
        var serializer = new Mock<IXmlSerializer>();
        serializer
            .Setup(xml => xml.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("import persistence failed"));
        var legacy = new UserBridgeMapping
        {
            MappingId = string.Empty,
            UserId = "cccccccc-cccc-cccc-cccc-cccccccccccc",
            UserName = "Legacy viewer",
            HueAppKey = "legacy-app-secret",
            HueClientKey = "legacy-client-secret",
            DeviceTargets = new List<UserDeviceBridgeTarget>
            {
                new()
                {
                    DeviceId = "tv",
                    HueAppKey = "legacy-device-app",
                    HueClientKey = "legacy-device-client"
                }
            }
        };
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping> { legacy }
        }, serializer.Object);

        var action = CreateController().ImportConfiguration(new HueConfigurationImportRequest
        {
            Configuration = new HuePluginConfigurationSettings(),
            UserMappings = new List<UserBridgeMappingImport>()
        });

        var response = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, response.StatusCode);
        Assert.Empty(legacy.MappingId);
        Assert.Equal("legacy-app-secret", legacy.HueAppKey);
        Assert.Equal("legacy-client-secret", legacy.HueClientKey);
        var device = Assert.Single(legacy.DeviceTargets);
        Assert.Equal("legacy-device-app", device.HueAppKey);
        Assert.Equal("legacy-device-client", device.HueClientKey);
        Assert.Same(legacy, Assert.Single(configuration.UserMappings));
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
            PlaybackMediaFilter = PluginConfiguration.PlaybackMediaFilterEpisodes,
            AudioSensitivityPercent = 220,
            AudioLowFrequencyHz = 70,
            AudioMidFrequencyHz = 600,
            AudioHighFrequencyHz = 2200,
            AudioLowGainPercent = 70,
            AudioMidGainPercent = 120,
            AudioHighGainPercent = 180,
            AudioResponseSmoothingPercent = 30,
            AudioBandSpreadPercent = 18,
            AudioBeatPulsePercent = 42,
            AudioBeatPulseThresholdPercent = 28,
            AudioColorPalette = PluginConfiguration.AudioColorPaletteCool,
            AudioSpatialMode = PluginConfiguration.AudioSpatialModeUniform,
            AudioChannelMode = PluginConfiguration.AudioChannelModeRight,
            TargetFps = 30,
            FrameResolution = PluginConfiguration.FrameResolutionLow,
            VideoScalingMode = PluginConfiguration.VideoScalingModeCrop,
            VideoDeinterlaceMode = PluginConfiguration.VideoDeinterlaceModeOn,
            SamplingBreadthPercent = 25,
            SamplingMode = PluginConfiguration.SamplingModeCenterPixel,
            SpatialOrientation = PluginConfiguration.SpatialOrientationRotate180,
            ColorSmoothingPercent = 40,
            HueShiftDegrees = -30,
            OutputBrightnessPercent = 60,
            GammaCorrection = 1.15,
            ContrastPercent = 115,
            ColorTemperatureKelvin = 3200,
            RedGain = 115,
            GreenGain = 95,
            BlueGain = 105,
            NetworkRetryAttempts = 4,
            PauseBehavior = PluginConfiguration.PauseBehaviorRestoreLightState,
            PersistSessionHistory = true,
            PersistSceneScheduleHistory = true,
            SessionHistoryRetentionCount = 9,
            SceneScheduleHistoryRetentionCount = 64,
            SceneAutomationEnabled = false,
            SceneAutomationCatchUpMinutes = 18,
            SceneAutomationPlaybackPolicy = PluginConfiguration.SceneAutomationPlaybackPolicyDefer,
            SceneAutomationPlaybackScope = PluginConfiguration.SceneAutomationPlaybackScopeMatchingTarget,
            SceneAutomationDeferMinutes = 42
        });

        Assert.IsType<OkObjectResult>(action.Result);
        Assert.Equal("4, 8", configuration.ChannelIds);
        Assert.Equal(PluginConfiguration.PlaybackMediaFilterEpisodes, configuration.PlaybackMediaFilter);
        Assert.Equal(220, configuration.AudioSensitivityPercent);
        Assert.Equal(70, configuration.AudioLowFrequencyHz);
        Assert.Equal(600, configuration.AudioMidFrequencyHz);
        Assert.Equal(2200, configuration.AudioHighFrequencyHz);
        Assert.Equal(70, configuration.AudioLowGainPercent);
        Assert.Equal(120, configuration.AudioMidGainPercent);
        Assert.Equal(180, configuration.AudioHighGainPercent);
        Assert.Equal(30, configuration.AudioResponseSmoothingPercent);
        Assert.Equal(18, configuration.AudioBandSpreadPercent);
        Assert.Equal(42, configuration.AudioBeatPulsePercent);
        Assert.Equal(28, configuration.AudioBeatPulseThresholdPercent);
        Assert.Equal(PluginConfiguration.AudioColorPaletteCool, configuration.AudioColorPalette);
        Assert.Equal(PluginConfiguration.AudioSpatialModeUniform, configuration.AudioSpatialMode);
        Assert.Equal(PluginConfiguration.AudioChannelModeRight, configuration.AudioChannelMode);
        Assert.Equal(30, configuration.TargetFps);
        Assert.Equal(PluginConfiguration.FrameResolutionLow, configuration.FrameResolution);
        Assert.Equal(PluginConfiguration.VideoScalingModeCrop, configuration.VideoScalingMode);
        Assert.Equal(PluginConfiguration.VideoDeinterlaceModeOn, configuration.VideoDeinterlaceMode);
        Assert.Equal(25, configuration.SamplingBreadthPercent);
        Assert.Equal(PluginConfiguration.SamplingModeCenterPixel, configuration.SamplingMode);
        Assert.Equal(PluginConfiguration.SpatialOrientationRotate180, configuration.SpatialOrientation);
        Assert.Equal(40, configuration.ColorSmoothingPercent);
        Assert.Equal(-30, configuration.HueShiftDegrees);
        Assert.Equal(60, configuration.OutputBrightnessPercent);
        Assert.Equal(1.15, configuration.GammaCorrection);
        Assert.Equal(115, configuration.ContrastPercent);
        Assert.Equal(3200, configuration.ColorTemperatureKelvin);
        Assert.Equal(115, configuration.RedGain);
        Assert.Equal(95, configuration.GreenGain);
        Assert.Equal(105, configuration.BlueGain);
        Assert.Equal(4, configuration.NetworkRetryAttempts);
        Assert.Equal(PluginConfiguration.PauseBehaviorRestoreLightState, configuration.PauseBehavior);
        Assert.True(configuration.PersistSessionHistory);
        Assert.True(configuration.PersistSceneScheduleHistory);
        Assert.Equal(9, configuration.SessionHistoryRetentionCount);
        Assert.Equal(64, configuration.SceneScheduleHistoryRetentionCount);
        Assert.False(configuration.SceneAutomationEnabled);
        Assert.Equal(18, configuration.SceneAutomationCatchUpMinutes);
        Assert.Equal(PluginConfiguration.SceneAutomationPlaybackPolicyDefer, configuration.SceneAutomationPlaybackPolicy);
        Assert.Equal(PluginConfiguration.SceneAutomationPlaybackScopeMatchingTarget, configuration.SceneAutomationPlaybackScope);
        Assert.Equal(42, configuration.SceneAutomationDeferMinutes);
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
            HueClientKey = "stored-client-key",
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "mapping-1",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.110",
                    HueAppKey = "mapping-app-key",
                    HueClientKey = "mapping-client-key",
                    EntertainmentAreaId = "mapping-area",
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new()
                        {
                            DeviceId = "player-1",
                            HueBridgeIp = "192.168.1.111",
                            HueAppKey = "device-app-key",
                            HueClientKey = "device-client-key",
                            EntertainmentAreaId = "device-area"
                        }
                    }
                }
            }
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
            HueAppKey = "submitted-app-key",
            HueClientKey = "submitted-client-key",
            ClearStoredCredentials = true
        });

        Assert.IsType<OkObjectResult>(clearAction.Result);
        Assert.Empty(configuration.HueAppKey);
        Assert.Empty(configuration.HueClientKey);
        Assert.Equal("mapping-app-key", configuration.UserMappings[0].HueAppKey);
        Assert.Equal("mapping-client-key", configuration.UserMappings[0].HueClientKey);
        Assert.Equal("device-app-key", configuration.UserMappings[0].DeviceTargets[0].HueAppKey);
        Assert.Equal("device-client-key", configuration.UserMappings[0].DeviceTargets[0].HueClientKey);
    }

    [Fact]
    public void SaveConfiguration_RejectsChangedGlobalTargetWhenCredentialsAreOmittedWithoutMutation()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = false,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "stored-global-app",
            HueClientKey = "stored-global-client",
            EntertainmentAreaId = "area-1"
        });

        var action = CreateController().SaveConfiguration(new HuePluginConfigurationSettings
        {
            SyncEnabled = false,
            HueBridgeIp = "192.168.1.101",
            EntertainmentAreaId = "area-2"
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("changes the global bridge target", JsonSerializer.Serialize(response.Value), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("App Key", JsonSerializer.Serialize(response.Value), StringComparison.Ordinal);
        Assert.Contains("Client Key", JsonSerializer.Serialize(response.Value), StringComparison.Ordinal);
        Assert.Equal("192.168.1.100", configuration.HueBridgeIp);
        Assert.Equal("stored-global-app", configuration.HueAppKey);
        Assert.Equal("stored-global-client", configuration.HueClientKey);
        Assert.Equal("area-1", configuration.EntertainmentAreaId);
    }

    [Fact]
    public void SaveConfiguration_AcceptsChangedGlobalTargetWithReplacementCredentials()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = false,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "stored-global-app",
            HueClientKey = "stored-global-client",
            EntertainmentAreaId = "area-1"
        });

        var action = CreateController().SaveConfiguration(new HuePluginConfigurationSettings
        {
            SyncEnabled = false,
            HueBridgeIp = "192.168.1.101",
            HueAppKey = "replacement-global-app",
            HueClientKey = "replacement-global-client",
            EntertainmentAreaId = "area-2"
        });

        Assert.IsType<OkObjectResult>(action.Result);
        Assert.Equal("192.168.1.101", configuration.HueBridgeIp);
        Assert.Equal("replacement-global-app", configuration.HueAppKey);
        Assert.Equal("replacement-global-client", configuration.HueClientKey);
        Assert.Equal("area-2", configuration.EntertainmentAreaId);
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
    public void SaveConfiguration_InvalidPlaybackDeferSettingsReturnsBadRequestWithoutSaving()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SceneAutomationPlaybackPolicy = PluginConfiguration.SceneAutomationPlaybackPolicySkip,
            SceneAutomationDeferMinutes = 12
        });

        var action = CreateController().SaveConfiguration(new HuePluginConfigurationSettings
        {
            SceneAutomationPlaybackPolicy = "Queue",
            SceneAutomationPlaybackScope = "GlobalOnly",
            SceneAutomationDeferMinutes = PluginConfiguration.MaxSceneAutomationDeferMinutes + 1
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Equal(PluginConfiguration.SceneAutomationPlaybackPolicySkip, configuration.SceneAutomationPlaybackPolicy);
        Assert.Equal(PluginConfiguration.SceneAutomationPlaybackScopeAnyTarget, configuration.SceneAutomationPlaybackScope);
        Assert.Equal(12, configuration.SceneAutomationDeferMinutes);
    }

    [Fact]
    public void SaveConfiguration_InvalidHistoryRetentionReturnsBadRequestWithoutSaving()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SessionHistoryRetentionCount = 12,
            SceneScheduleHistoryRetentionCount = 73
        });

        var action = CreateController().SaveConfiguration(new HuePluginConfigurationSettings
        {
            SessionHistoryRetentionCount = PluginConfiguration.MaxSessionHistoryRetentionCount + 1,
            SceneScheduleHistoryRetentionCount = PluginConfiguration.MinSceneScheduleHistoryRetentionCount - 1
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Equal(12, configuration.SessionHistoryRetentionCount);
        Assert.Equal(73, configuration.SceneScheduleHistoryRetentionCount);
    }

    [Fact]
    public void SaveConfiguration_WhenPersistenceFails_RestoresSettingsAndHistory()
    {
        var serializer = new Mock<IXmlSerializer>();
        serializer
            .Setup(xml => xml.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("serializer path contains a private implementation detail"));
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "stored-app-key",
            HueClientKey = "stored-client-key",
            SceneAutomationCatchUpMinutes = 12,
            PersistSessionHistory = true,
            PersistSceneScheduleHistory = true,
            PersistedSessionHistory = new List<HueSessionHistoryEntry>
            {
                new() { Item = "Private title" }
            },
            PersistedSceneScheduleHistory = new List<HueSceneScheduleHistoryEntry>
            {
                new() { ScheduleId = "private-cue", ScheduleName = "Private cue" }
            }
        }, serializer.Object);

        var action = CreateController().SaveConfiguration(new HuePluginConfigurationSettings
        {
            SyncEnabled = false,
            HueBridgeIp = "192.168.1.101",
            HueAppKey = "replacement-app-key",
            HueClientKey = "replacement-client-key",
            PersistSessionHistory = false,
            PersistSceneScheduleHistory = false,
            SceneAutomationCatchUpMinutes = 18
        });

        var response = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, response.StatusCode);
        Assert.Equal("Configuration could not be saved.", response.Value);
        Assert.True(configuration.SyncEnabled);
        Assert.Equal("192.168.1.100", configuration.HueBridgeIp);
        Assert.Equal("stored-app-key", configuration.HueAppKey);
        Assert.Equal("stored-client-key", configuration.HueClientKey);
        Assert.Equal(12, configuration.SceneAutomationCatchUpMinutes);
        Assert.True(configuration.PersistSessionHistory);
        Assert.True(configuration.PersistSceneScheduleHistory);
        Assert.Equal("Private title", Assert.Single(configuration.PersistedSessionHistory).Item);
        Assert.Equal("private-cue", Assert.Single(configuration.PersistedSceneScheduleHistory).ScheduleId);
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
            PlaybackMediaFilterOverride = " episodes ",
            AudioSensitivityPercentOverride = 260,
            AudioLowFrequencyHzOverride = 60,
            AudioMidFrequencyHzOverride = 700,
            AudioHighFrequencyHzOverride = 2400,
            AudioBandSpreadPercentOverride = 35,
            AudioBeatPulsePercentOverride = 65,
            AudioBeatPulseThresholdPercentOverride = 50,
            AudioColorPaletteOverride = PluginConfiguration.AudioColorPaletteBand,
            AudioSpatialModeOverride = PluginConfiguration.AudioSpatialModeSpatial,
            AudioChannelModeOverride = PluginConfiguration.AudioChannelModeLeft,
            BrightnessBoostOverride = 125,
            RedGainOverride = 115,
            GreenGainOverride = 95,
            BlueGainOverride = 105,
            ColorSaturationOverride = 80,
            HueShiftDegreesOverride = 30,
            OutputBrightnessPercentOverride = 65,
            GammaCorrectionOverride = 0.85,
            ContrastPercentOverride = 85,
            ColorTemperatureKelvinOverride = 7200,
            BlackoutThresholdOverride = 20,
            BlackoutBehaviorOverride = " keeplastcolors ",
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
            SpatialOrientationOverride = PluginConfiguration.SpatialOrientationRotate180,
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
        Assert.Equal(PluginConfiguration.PlaybackMediaFilterEpisodes, mapping.PlaybackMediaFilterOverride);
        Assert.Equal((int?)260, mapping.AudioSensitivityPercentOverride);
        Assert.Equal((int?)60, mapping.AudioLowFrequencyHzOverride);
        Assert.Equal((int?)700, mapping.AudioMidFrequencyHzOverride);
        Assert.Equal((int?)2400, mapping.AudioHighFrequencyHzOverride);
        Assert.Equal((int?)35, mapping.AudioBandSpreadPercentOverride);
        Assert.Equal((int?)65, mapping.AudioBeatPulsePercentOverride);
        Assert.Equal((int?)50, mapping.AudioBeatPulseThresholdPercentOverride);
        Assert.Equal(PluginConfiguration.AudioColorPaletteBand, mapping.AudioColorPaletteOverride);
        Assert.Equal(PluginConfiguration.AudioSpatialModeSpatial, mapping.AudioSpatialModeOverride);
        Assert.Equal(PluginConfiguration.AudioChannelModeLeft, mapping.AudioChannelModeOverride);
        Assert.Equal((int?)125, mapping.BrightnessBoostOverride);
        Assert.Equal((int?)115, mapping.RedGainOverride);
        Assert.Equal((int?)95, mapping.GreenGainOverride);
        Assert.Equal((int?)105, mapping.BlueGainOverride);
        Assert.Equal((int?)80, mapping.ColorSaturationOverride);
        Assert.Equal((int?)30, mapping.HueShiftDegreesOverride);
        Assert.Equal((int?)65, mapping.OutputBrightnessPercentOverride);
        Assert.Equal((double?)0.85, mapping.GammaCorrectionOverride);
        Assert.Equal((int?)85, mapping.ContrastPercentOverride);
        Assert.Equal((int?)7200, mapping.ColorTemperatureKelvinOverride);
        Assert.Equal((int?)20, mapping.BlackoutThresholdOverride);
        Assert.Equal(PluginConfiguration.BlackoutBehaviorKeepLastColors, mapping.BlackoutBehaviorOverride);
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
        Assert.Equal(PluginConfiguration.SpatialOrientationRotate180, mapping.SpatialOrientationOverride);
        Assert.Equal((int?)35, mapping.ColorSmoothingPercentOverride);
    }

    [Fact]
    public void SaveUserMapping_TrimsIdentityAndReplacesWhitespacePaddedExistingMapping()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = " user-1 ",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.100",
                    HueAppKey = "stored-app-key",
                    HueClientKey = "stored-client-key",
                    EntertainmentAreaId = "area-1"
                }
            }
        });

        var action = CreateController().SaveUserMapping(new UserBridgeMapping
        {
            UserId = " user-1 ",
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.101",
            EntertainmentAreaId = "area-2"
        });

        Assert.IsType<OkObjectResult>(action);
        var mapping = Assert.Single(configuration.UserMappings);
        Assert.Equal("user-1", mapping.UserId);
        Assert.Equal("stored-app-key", mapping.HueAppKey);
        Assert.Equal("stored-client-key", mapping.HueClientKey);
        Assert.Equal("192.168.1.101", mapping.HueBridgeIp);
    }

    [Fact]
    public void SaveUserMapping_RejectsAmbiguousDuplicateWithoutStableRowId()
    {
        var userId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    MappingId = "duplicate-row-one",
                    UserId = userId.ToString("D"),
                    UserName = "First duplicate",
                    SyncEnabled = false,
                    HueAppKey = "first-secret"
                },
                new()
                {
                    MappingId = "duplicate-row-two",
                    UserId = "{" + userId.ToString("D") + "}",
                    UserName = "Second duplicate",
                    SyncEnabled = false,
                    HueAppKey = "second-secret"
                }
            }
        });

        var action = CreateController().SaveUserMapping(new UserBridgeMapping
        {
            UserId = userId.ToString("D"),
            UserName = "Ambiguous edit",
            SyncEnabled = false
        });

        var response = Assert.IsType<ConflictObjectResult>(action);
        Assert.Contains("multiple mapping rows", response.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, configuration.UserMappings.Count);
        Assert.Equal("First duplicate", configuration.UserMappings[0].UserName);
        Assert.Equal("Second duplicate", configuration.UserMappings[1].UserName);
        Assert.Equal("first-secret", configuration.UserMappings[0].HueAppKey);
        Assert.Equal("second-secret", configuration.UserMappings[1].HueAppKey);
    }

    [Fact]
    public void SaveUserMapping_WithStableRowIdEditsOnlySelectedDuplicate()
    {
        var userId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new() { MappingId = "duplicate-row-one", UserId = userId.ToString("D"), UserName = "First duplicate", SyncEnabled = false },
                new() { MappingId = "duplicate-row-two", UserId = userId.ToString("D"), UserName = "Second duplicate", SyncEnabled = false }
            }
        });

        var action = CreateController().SaveUserMapping(new UserBridgeMapping
        {
            MappingId = "duplicate-row-two",
            UserId = userId.ToString("D"),
            UserName = "Selected duplicate",
            SyncEnabled = false
        });

        Assert.IsType<OkObjectResult>(action);
        Assert.Equal(2, configuration.UserMappings.Count);
        Assert.Equal("First duplicate", configuration.UserMappings[0].UserName);
        Assert.Equal("Selected duplicate", configuration.UserMappings[1].UserName);
        Assert.Equal("duplicate-row-two", configuration.UserMappings[1].MappingId);
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
            PlaybackMediaFilterOverride = "Trailers",
            TargetFpsOverride = 0,
            FfmpegStallTimeoutSecondsOverride = 0,
            NetworkRetryAttemptsOverride = 11,
            ChannelIdsOverride = "1, bad"
        });

        var response = Assert.IsType<BadRequestObjectResult>(action);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        var validationBody = System.Text.Json.JsonSerializer.Serialize(response.Value);
        Assert.Contains("target FPS override must be between 1 and 60", validationBody, StringComparison.Ordinal);
        Assert.Contains("playback media scope override must be AllVideo, Movies, Episodes, OtherVideo, Audio, or AllMedia", validationBody, StringComparison.Ordinal);
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

    [Fact]
    public void SaveUserMapping_UpdatesLegacyBraceGuidMappingWithoutDuplicatingOrDroppingCredentials()
    {
        const string canonicalUserId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "{BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB}",
                    UserName = "Old viewer",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.125",
                    HueAppKey = "stored-legacy-app",
                    HueClientKey = "stored-legacy-client",
                    EntertainmentAreaId = "legacy-area"
                }
            }
        });

        var action = CreateController().SaveUserMapping(new UserBridgeMapping
        {
            UserId = canonicalUserId,
            UserName = "Updated viewer",
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.125",
            EntertainmentAreaId = "updated-area"
        });

        Assert.IsType<OkObjectResult>(action);
        var updated = Assert.Single(configuration.UserMappings);
        Assert.Equal(canonicalUserId, updated.UserId);
        Assert.Equal("Updated viewer", updated.UserName);
        Assert.Equal("updated-area", updated.EntertainmentAreaId);
        Assert.Equal("stored-legacy-app", updated.HueAppKey);
        Assert.Equal("stored-legacy-client", updated.HueClientKey);
    }

    [Fact]
    public void UserMappingLifecycle_RejectsSavedPlaylistTargetDependency()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = "user-playlist", UserName = "Playlist room", SyncEnabled = true }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "playlist-dependent",
                    Name = "Dependent playlist",
                    PresetNames = new List<string> { "Welcome" },
                    TargetUserIds = new List<string> { "user-playlist" }
                }
            }
        });
        var controller = CreateController();

        var disable = controller.SaveUserMapping(new UserBridgeMapping
        {
            UserId = "user-playlist",
            UserName = "Playlist room",
            SyncEnabled = false
        });

        var disableResponse = Assert.IsType<ConflictObjectResult>(disable);
        Assert.Contains("saved playlist", disableResponse.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(configuration.UserMappings[0].SyncEnabled);

        var delete = controller.DeleteUserMapping("user-playlist");

        var deleteResponse = Assert.IsType<ConflictObjectResult>(delete);
        Assert.Contains("saved playlist", deleteResponse.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Single(configuration.UserMappings);
    }

    [Fact]
    public void UserMappingLifecycle_ProtectsScheduledDeviceRouteDependencies()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-device",
                    UserName = "Device room",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "device-app-secret",
                    HueClientKey = "device-client-secret",
                    EntertainmentAreaId = "device-area",
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new()
                        {
                            DeviceId = "living-room-tv",
                            HueBridgeIp = "192.168.1.102",
                            HueAppKey = "tv-app-secret",
                            HueClientKey = "tv-client-secret",
                            EntertainmentAreaId = "tv-area"
                        }
                    }
                }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "device-route-cue",
                    Name = "Device route cue",
                    TargetRoutes = new List<HueSceneScheduleTargetRoute>
                    {
                        new() { UserId = "user-device", DeviceId = "living-room-tv" }
                    }
                }
            }
        });
        var controller = CreateController();

        var disable = controller.SaveUserMapping(new UserBridgeMapping
        {
            UserId = "user-device",
            UserName = "Device room",
            SyncEnabled = false
        });
        var disableResponse = Assert.IsType<ConflictObjectResult>(disable);
        Assert.Contains("scheduled cue", disableResponse.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(configuration.UserMappings[0].SyncEnabled);

        var delete = controller.DeleteUserMapping("user-device");
        var deleteResponse = Assert.IsType<ConflictObjectResult>(delete);
        Assert.Contains("scheduled cue", deleteResponse.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Single(configuration.UserMappings);

        var removeDevice = controller.SaveUserMapping(new UserBridgeMapping
        {
            UserId = "user-device",
            UserName = "Device room",
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.101",
            EntertainmentAreaId = "device-area",
            DeviceTargets = new List<UserDeviceBridgeTarget>()
        });
        var removeDeviceResponse = Assert.IsType<ConflictObjectResult>(removeDevice);
        Assert.Contains("device targets", removeDeviceResponse.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("living-room-tv", Assert.Single(configuration.UserMappings[0].DeviceTargets).DeviceId);
    }

    [Fact]
    public void DeleteUserMapping_UnreferencedMappingIsRemoved()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = "user-delete", UserName = "Viewer", SyncEnabled = false }
            }
        });

        var action = CreateController().DeleteUserMapping(" USER-DELETE ");

        Assert.IsType<OkObjectResult>(action);
        Assert.Empty(configuration.UserMappings);
    }

    [Fact]
    public void DeleteUserMapping_WithStableRowIdDeletesOnlySelectedDuplicate()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    MappingId = "mapping-delete-a",
                    UserId = "user-duplicate",
                    UserName = "First",
                    SyncEnabled = false,
                    HueAppKey = "delete-a-app-secret",
                    HueClientKey = "delete-a-client-secret"
                },
                new()
                {
                    MappingId = "mapping-delete-b",
                    UserId = "user-duplicate",
                    UserName = "Second",
                    SyncEnabled = false,
                    HueAppKey = "delete-b-app-secret",
                    HueClientKey = "delete-b-client-secret"
                }
            }
        });

        var action = CreateController().DeleteUserMapping("user-duplicate", "mapping-delete-a");

        Assert.IsType<OkObjectResult>(action);
        var remaining = Assert.Single(configuration.UserMappings);
        Assert.Equal("mapping-delete-b", remaining.MappingId);
        Assert.Equal("Second", remaining.UserName);
        Assert.Equal("delete-b-app-secret", remaining.HueAppKey);
        Assert.Equal("delete-b-client-secret", remaining.HueClientKey);
    }

    [Fact]
    public void DeleteUserMapping_RejectsAmbiguousLegacyUserIdWithoutMutation()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new() { MappingId = "mapping-delete-a", UserId = "user-duplicate", UserName = "First", SyncEnabled = false, HueAppKey = "delete-a-app-secret" },
                new() { MappingId = "mapping-delete-b", UserId = "user-duplicate", UserName = "Second", SyncEnabled = false, HueAppKey = "delete-b-app-secret" }
            }
        });

        var action = CreateController().DeleteUserMapping("user-duplicate");

        var response = Assert.IsType<ConflictObjectResult>(action);
        Assert.Contains("mappingId", response.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "mapping-delete-a", "mapping-delete-b" }, configuration.UserMappings.Select(mapping => mapping.MappingId));
        Assert.Equal(new[] { "delete-a-app-secret", "delete-b-app-secret" }, configuration.UserMappings.Select(mapping => mapping.HueAppKey));
    }

    [Fact]
    public void UserMappings_BulkDeleteWithStableRowIdsPreservesSiblingDuplicate()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new() { MappingId = "mapping-bulk-a", UserId = "user-bulk-duplicate", UserName = "First", SyncEnabled = false, HueAppKey = "bulk-a-app-secret" },
                new() { MappingId = "mapping-bulk-b", UserId = "user-bulk-duplicate", UserName = "Second", SyncEnabled = false, HueAppKey = "bulk-b-app-secret" },
                new() { MappingId = "mapping-bulk-keep", UserId = "user-bulk-keep", UserName = "Keep", SyncEnabled = false }
            }
        });

        var action = CreateController().DeleteUserMappingsBulk(new HueUserMappingBulkDeleteRequest
        {
            MappingIds = new List<string> { "mapping-bulk-a" }
        });

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueUserMappingBulkDeleteResult>(response.Value);
        Assert.Equal(1, result.RequestedCount);
        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(new[] { "mapping-bulk-a" }, result.Mappings.Select(mapping => mapping.MappingId));
        Assert.Equal(new[] { "mapping-bulk-b", "mapping-bulk-keep" }, configuration.UserMappings.Select(mapping => mapping.MappingId));
        Assert.Equal("bulk-b-app-secret", configuration.UserMappings[0].HueAppKey);
    }

    [Fact]
    public void UserMappings_BulkDeleteRejectsAmbiguousLegacyUserIdWithoutMutation()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new() { MappingId = "mapping-bulk-a", UserId = "user-bulk-duplicate", UserName = "First", SyncEnabled = false },
                new() { MappingId = "mapping-bulk-b", UserId = "user-bulk-duplicate", UserName = "Second", SyncEnabled = false }
            }
        });

        var action = CreateController().DeleteUserMappingsBulk(new HueUserMappingBulkDeleteRequest
        {
            UserIds = new List<string> { "user-bulk-duplicate" }
        });

        var response = Assert.IsType<ConflictObjectResult>(action.Result);
        var result = Assert.IsType<HueUserMappingBulkDeleteResult>(response.Value);
        Assert.Equal(new[] { "user-bulk-duplicate" }, result.AmbiguousUserIds);
        Assert.Equal(new[] { "mapping-bulk-a", "mapping-bulk-b" }, configuration.UserMappings.Select(mapping => mapping.MappingId));
    }

    [Fact]
    public void UserMappingDependencies_WithStableRowIdSelectsExactDuplicate()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new() { MappingId = "mapping-dependency-a", UserId = "user-dependency-duplicate", UserName = "First", SyncEnabled = false },
                new() { MappingId = "mapping-dependency-b", UserId = "user-dependency-duplicate", UserName = "Second", SyncEnabled = false }
            }
        });

        var action = CreateController().GetUserMappingDependencies("user-dependency-duplicate", "mapping-dependency-b");

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<HueUserMappingDependenciesResult>(response.Value);
        Assert.Equal("mapping-dependency-b", result.MappingId);
        Assert.Equal("Second", result.UserName);
        Assert.Equal(0, result.ScheduledCueCount);
        Assert.True(result.CanDelete);
        Assert.Equal(2, configuration.UserMappings.Count);
    }

    [Fact]
    public void DeleteUserMapping_RemovesLegacyBraceGuidWhenCalledWithCanonicalId()
    {
        const string canonicalUserId = "cccccccc-cccc-cccc-cccc-cccccccccccc";
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "{CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC}",
                    UserName = "Legacy viewer",
                    SyncEnabled = false
                }
            }
        });

        var action = CreateController().DeleteUserMapping(canonicalUserId);

        Assert.IsType<OkObjectResult>(action);
        Assert.Empty(configuration.UserMappings);
    }

    [Fact]
    public void SaveSceneSchedule_RejectsNullTargetRouteWithoutMutation()
    {
        var existingSchedule = new HueSceneSchedule
        {
            Id = "existing-schedule",
            Name = "Existing cue",
            PresetName = "Evening"
        };
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = false,
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            SceneSchedules = new List<HueSceneSchedule> { existingSchedule }
        });

        var action = CreateController().SaveSceneSchedule(new HueSceneScheduleRequest
        {
            Id = existingSchedule.Id,
            Name = "Updated cue",
            PresetName = "Evening",
            TargetRoutes = new List<HueSceneScheduleTargetRoute> { null! }
        });

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains(
            "Scene schedule 1 selected device route 1 requires both a user mapping ID and device ID",
            JsonSerializer.Serialize(response.Value),
            StringComparison.Ordinal);
        Assert.Same(existingSchedule, Assert.Single(configuration.SceneSchedules));
        Assert.Equal("Existing cue", configuration.SceneSchedules[0].Name);
    }

    [Fact]
    public void ConfigurationImport_RejectsNullScheduleTargetRouteWithoutMutation()
    {
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } }
        });
        var request = CreateConfigurationImportRequest(configuration);
        request.SceneSchedules = new List<HueSceneScheduleRequest>
        {
            new()
            {
                Id = "imported-schedule",
                Name = "Imported cue",
                PresetName = "Evening",
                TargetRoutes = new List<HueSceneScheduleTargetRoute> { null! }
            }
        };

        var controller = CreateController();
        var validation = controller.ValidateConfigurationImport(request);
        var validationResponse = Assert.IsType<OkObjectResult>(validation.Result);
        var validationResult = Assert.IsType<HueConfigurationImportValidationResult>(validationResponse.Value);
        Assert.False(validationResult.Valid);
        Assert.Contains(
            "Scene schedule 1 selected device route 1 requires both a user mapping ID and device ID",
            validationResult.ValidationErrors);

        var import = controller.ImportConfiguration(request);
        var importResponse = Assert.IsType<BadRequestObjectResult>(import.Result);
        Assert.Contains(
            "Scene schedule 1 selected device route 1 requires both a user mapping ID and device ID",
            JsonSerializer.Serialize(importResponse.Value),
            StringComparison.Ordinal);
        Assert.Empty(configuration.SceneSchedules);
    }

    [Fact]
    public void ConfigurationImport_PartialScheduleMergePreservesEnabledAndSkipStateWhenOmitted()
    {
        var existingSchedule = new HueSceneSchedule
        {
            Id = "partial-state-schedule",
            Name = "Paused cue",
            PresetName = "Evening",
            Enabled = false,
            SkipNextOccurrence = true
        };
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = false,
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            SceneSchedules = new List<HueSceneSchedule> { existingSchedule }
        });
        var request = CreateConfigurationImportRequest(configuration);
        request.ReplaceSceneSchedules = false;
        request.ReplaceColorPresets = false;
        request.SceneSchedules = new List<HueSceneScheduleRequest>
        {
            new()
            {
                Id = existingSchedule.Id,
                Name = "Renamed paused cue",
                PresetName = "Evening"
            }
        };

        var action = CreateController().ImportConfiguration(request);

        Assert.IsType<OkObjectResult>(action.Result);
        var imported = Assert.Single(configuration.SceneSchedules);
        Assert.Equal("Renamed paused cue", imported.Name);
        Assert.False(imported.Enabled);
        Assert.True(imported.SkipNextOccurrence);
    }

    [Fact]
    public void ConfigurationImport_PartialScheduleMergeAllowsExplicitEnabledAndSkipStateOverrides()
    {
        var existingSchedule = new HueSceneSchedule
        {
            Id = "explicit-state-schedule",
            Name = "Paused cue",
            PresetName = "Evening",
            Enabled = false,
            SkipNextOccurrence = true
        };
        var configuration = InstallConfiguration(new PluginConfiguration
        {
            SyncEnabled = false,
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            SceneSchedules = new List<HueSceneSchedule> { existingSchedule }
        });
        var request = CreateConfigurationImportRequest(configuration);
        request.ReplaceSceneSchedules = false;
        request.ReplaceColorPresets = false;
        request.SceneSchedules = new List<HueSceneScheduleRequest>
        {
            new()
            {
                Id = existingSchedule.Id,
                Name = "Resumed cue",
                PresetName = "Evening",
                Enabled = true,
                SkipNextOccurrence = false
            }
        };

        var action = CreateController().ImportConfiguration(request);

        Assert.IsType<OkObjectResult>(action.Result);
        var imported = Assert.Single(configuration.SceneSchedules);
        Assert.True(imported.Enabled);
        Assert.False(imported.SkipNextOccurrence);
    }

    private sealed class NestedCancellationPlaylistStreamTester : IHueStreamTester, IHuePlaylistStreamTester
    {
        public int PlaylistCallCount { get; private set; }

        public Task<HueStreamProbeResult> TestAsync(
            string bridgeIp,
            string appKey,
            string clientKey,
            string areaId,
            JsonElement areaConfiguration,
            IReadOnlySet<int>? channelIds = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The continuous-playlist test double must not use the legacy probe path.");

        public Task<HueStreamProbeResult> PreviewAsync(
            string bridgeIp,
            string appKey,
            string clientKey,
            string areaId,
            JsonElement areaConfiguration,
            IReadOnlySet<int>? channelIds,
            int red,
            int green,
            int blue,
            int brightnessPercent,
            int durationSeconds,
            CancellationToken cancellationToken = default,
            int transitionSeconds = PluginConfiguration.MinColorPresetTransitionSeconds,
            int transitionOutSeconds = PluginConfiguration.MinColorPresetTransitionOutSeconds,
            string effect = PluginConfiguration.ColorPresetEffectSolid,
            int effectSpeedPercent = PluginConfiguration.DefaultColorPresetEffectSpeedPercent)
            => throw new InvalidOperationException("The continuous-playlist test double must not use the legacy preview path.");

        public bool CancelActiveDiagnostic() => false;

        public Task<HuePlaylistStreamProbeResult> PreviewPlaylistAsync(
            string bridgeIp,
            string appKey,
            string clientKey,
            string areaId,
            JsonElement areaConfiguration,
            IReadOnlySet<int>? channelIds,
            IReadOnlyList<HuePlaylistPreviewStep> steps,
            CancellationToken cancellationToken = default)
        {
            PlaylistCallCount++;
            return Task.FromResult(new HuePlaylistStreamProbeResult
            {
                Succeeded = false,
                Message = "The continuous playlist preview ended during bridge cleanup.",
                Steps = new[]
                {
                    new HuePlaylistPreviewStepResult
                    {
                        Index = steps[0].Index,
                        Succeeded = false,
                        Message = "The playlist step was canceled during cleanup."
                    }
                }
            });
        }

        public Task<HuePlaylistStreamProbeResult> PreviewPlaylistAsyncForTarget(
            string bridgeIp,
            string appKey,
            string clientKey,
            string areaId,
            JsonElement areaConfiguration,
            IReadOnlySet<int>? channelIds,
            IReadOnlyList<HuePlaylistPreviewStep> steps,
            CancellationToken cancellationToken = default)
            => PreviewPlaylistAsync(
                bridgeIp,
                appKey,
                clientKey,
                areaId,
                areaConfiguration,
                channelIds,
                steps,
                cancellationToken);
    }

    private sealed class BlockingPreviewStreamTester : IHueStreamTester
    {
        public TaskCompletionSource<bool> PreviewStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleasePreview { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<HueStreamProbeResult> TestAsync(
            string bridgeIp,
            string appKey,
            string clientKey,
            string areaId,
            JsonElement areaConfiguration,
            IReadOnlySet<int>? channelIds = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new HueStreamProbeResult
            {
                Succeeded = false,
                Message = "Not used by this test."
            });

        public async Task<HueStreamProbeResult> PreviewAsync(
            string bridgeIp,
            string appKey,
            string clientKey,
            string areaId,
            JsonElement areaConfiguration,
            IReadOnlySet<int>? channelIds,
            int red,
            int green,
            int blue,
            int brightnessPercent,
            int durationSeconds,
            CancellationToken cancellationToken = default,
            int transitionSeconds = PluginConfiguration.MinColorPresetTransitionSeconds,
            int transitionOutSeconds = PluginConfiguration.MinColorPresetTransitionOutSeconds,
            string effect = PluginConfiguration.ColorPresetEffectSolid,
            int effectSpeedPercent = PluginConfiguration.DefaultColorPresetEffectSpeedPercent)
        {
            PreviewStarted.TrySetResult(true);
            await ReleasePreview.Task.WaitAsync(cancellationToken);
            return new HueStreamProbeResult
            {
                Succeeded = true,
                Message = "Preview completed."
            };
        }

        public bool CancelActiveDiagnostic() => false;
    }

    private HueApiController CreateController(
        IHueStreamTester? streamTester = null,
        HueBridgeLifecycleGate? bridgeLifecycleGate = null,
        IHueEnvironmentProbe? environmentProbe = null,
        HueDiagnosticsCancellationGate? diagnosticsCancellationGate = null,
        IEnumerable<IHostedService>? hostedServices = null,
        ISessionManager? sessionManager = null,
        IUserManager? userManager = null)
    {
        var client = new HueClient(_httpClient, _loggerMock.Object);
        return new HueApiController(
            client,
            hostedServices ?? Array.Empty<IHostedService>(),
            streamTester,
            bridgeLifecycleGate,
            environmentProbe,
            diagnosticsCancellationGate,
            sessionManager,
            userManager: userManager);
    }

    private static void AssertConflict(IActionResult action)
    {
        var response = Assert.IsType<ConflictObjectResult>(action);
        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
    }

    private static void AssertConfigurationReadConflict<T>(ActionResult<T> action)
        where T : class
    {
        Assert.Null(action.Value);
        Assert.NotNull(action.Result);
        AssertConflict(action.Result!);
    }

    private static HueConfigurationImportRequest CreateConfigurationImportRequest(
        PluginConfiguration configuration)
        => new()
        {
            Configuration = HuePluginConfigurationSettings.From(configuration)
        };

    private void AssertConfigurationImportAccepted(
        PluginConfiguration configuration,
        HueConfigurationImportRequest request,
        int expectedPresets,
        int expectedPlaylists,
        int expectedSchedules)
    {
        var controller = CreateController();
        var validation = controller.ValidateConfigurationImport(request);
        var validationResponse = Assert.IsType<OkObjectResult>(validation.Result);
        var validationResult = Assert.IsType<HueConfigurationImportValidationResult>(validationResponse.Value);
        Assert.True(validationResult.Valid);
        Assert.True(validationResult.CanImport);
        Assert.Equal(expectedPresets, validationResult.TotalColorPresets);
        Assert.Equal(expectedPlaylists, validationResult.TotalScenePlaylists);
        Assert.Equal(expectedSchedules, validationResult.TotalSceneSchedules);

        var import = controller.ImportConfiguration(request);
        var importResponse = Assert.IsType<OkObjectResult>(import.Result);
        var importResult = Assert.IsType<HueConfigurationImportResult>(importResponse.Value);
        Assert.Equal(expectedPresets, importResult.TotalColorPresets);
        Assert.Equal(expectedPlaylists, importResult.TotalScenePlaylists);
        Assert.Equal(expectedSchedules, importResult.TotalSceneSchedules);
        Assert.Equal(expectedPresets, configuration.ColorPresets.Count);
        Assert.Equal(expectedPlaylists, configuration.ScenePlaylists.Count);
        Assert.Equal(expectedSchedules, configuration.SceneSchedules.Count);
    }

    private void AssertConfigurationImportCapacityRejected(
        PluginConfiguration configuration,
        HueConfigurationImportRequest request,
        params string[] expectedErrors)
    {
        var previousMappings = configuration.UserMappings;
        var previousPresets = configuration.ColorPresets;
        var previousPlaylists = configuration.ScenePlaylists;
        var previousSchedules = configuration.SceneSchedules;
        var controller = CreateController();

        var validation = controller.ValidateConfigurationImport(request);
        var validationResponse = Assert.IsType<OkObjectResult>(validation.Result);
        var validationResult = Assert.IsType<HueConfigurationImportValidationResult>(validationResponse.Value);
        Assert.False(validationResult.Valid);
        Assert.False(validationResult.CanImport);
        foreach (var expectedError in expectedErrors)
        {
            Assert.Contains(expectedError, validationResult.ValidationErrors);
        }

        var import = controller.ImportConfiguration(request);
        var importResponse = Assert.IsType<BadRequestObjectResult>(import.Result);
        var serializedImportResponse = JsonSerializer.Serialize(importResponse.Value);
        foreach (var expectedError in expectedErrors)
        {
            Assert.Contains(expectedError, serializedImportResponse, StringComparison.Ordinal);
        }

        Assert.Same(previousMappings, configuration.UserMappings);
        Assert.Same(previousPresets, configuration.ColorPresets);
        Assert.Same(previousPlaylists, configuration.ScenePlaylists);
        Assert.Same(previousSchedules, configuration.SceneSchedules);
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        target.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);
    }

    private static PluginConfiguration InstallConfiguration(
        PluginConfiguration configuration,
        IXmlSerializer? xmlSerializer = null)
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

        var plugin = new Plugin(applicationPaths.Object, xmlSerializer ?? Mock.Of<IXmlSerializer>());
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
