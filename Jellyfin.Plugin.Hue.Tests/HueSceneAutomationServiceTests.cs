using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Hue.Configuration;
using Jellyfin.Plugin.Hue.Hue;
using Jellyfin.Plugin.Hue.Service;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

[Collection("PluginState")]
public sealed class HueSceneAutomationServiceTests
{
    [Fact]
    public void IsDue_UsesSelectedLocalDayAndMinute()
    {
        var schedule = new HueSceneSchedule
        {
            Enabled = true,
            TimeOfDay = "07:05",
            DaysOfWeekMask = 1 << (int)DayOfWeek.Monday
        };

        Assert.True(HueSceneAutomationService.IsDue(schedule, new DateTime(2026, 8, 17, 7, 5, 30)));
        Assert.False(HueSceneAutomationService.IsDue(schedule, new DateTime(2026, 8, 18, 7, 5, 30)));
        Assert.False(HueSceneAutomationService.IsDue(schedule, new DateTime(2026, 8, 17, 7, 6, 0)));
    }

    [Fact]
    public void GetNextRunLocal_UsesServerLocalScheduleAndSkipsElapsedOccurrence()
    {
        var schedule = new HueSceneSchedule
        {
            Enabled = true,
            TimeOfDay = "07:05",
            DaysOfWeekMask = (1 << (int)DayOfWeek.Monday) | (1 << (int)DayOfWeek.Wednesday)
        };

        var beforeCue = HueSceneAutomationService.GetNextRunLocal(
            schedule,
            new DateTime(2026, 8, 17, 6, 59, 0));
        var afterCue = HueSceneAutomationService.GetNextRunLocal(
            schedule,
            new DateTime(2026, 8, 17, 7, 5, 30));

        Assert.Equal(new DateTime(2026, 8, 17, 7, 5, 0), beforeCue);
        Assert.Equal(new DateTime(2026, 8, 19, 7, 5, 0), afterCue);
        Assert.Null(HueSceneAutomationService.GetNextRunLocal(
            new HueSceneSchedule { Enabled = false, TimeOfDay = "07:05", DaysOfWeekMask = 127 },
            new DateTime(2026, 8, 17, 6, 59, 0)));
    }

    [Fact]
    public void TimeZoneAwareSchedule_UsesUtcInstantAndSelectedZoneWallClock()
    {
        var schedule = new HueSceneSchedule
        {
            Enabled = true,
            TimeOfDay = "07:05",
            TimeZoneId = TimeZoneInfo.Utc.Id,
            DaysOfWeekMask = 1 << (int)DayOfWeek.Monday
        };
        var dueUtc = new DateTime(2026, 8, 17, 7, 5, 30, DateTimeKind.Utc);
        var serverLocalNow = TimeZoneInfo.ConvertTimeFromUtc(dueUtc, TimeZoneInfo.Local);

        Assert.True(HueSceneAutomationService.IsDue(schedule, serverLocalNow));

        var nextServerLocal = TimeZoneInfo.ConvertTimeFromUtc(
            new DateTime(2026, 8, 17, 6, 59, 0, DateTimeKind.Utc),
            TimeZoneInfo.Local);
        var nextLocal = HueSceneAutomationService.GetNextRunLocal(schedule, nextServerLocal);
        var nextUtc = HueSceneAutomationService.GetNextRunUtc(schedule, nextServerLocal);
        Assert.Equal(new DateTime(2026, 8, 17, 7, 5, 0), nextLocal);
        Assert.Equal(new DateTime(2026, 8, 17, 7, 5, 0, DateTimeKind.Utc), nextUtc);
    }

    [Fact]
    public void TimeZoneAwareSchedule_SkipsInvalidSpringForwardWallClock()
    {
        var zone = TimeZoneInfo.GetSystemTimeZones()
            .FirstOrDefault(candidate => candidate.Id.Equals("America/New_York", StringComparison.OrdinalIgnoreCase));
        if (zone == null)
            return;

        var schedule = new HueSceneSchedule
        {
            Enabled = true,
            TimeOfDay = "02:30",
            TimeZoneId = zone.Id,
            DaysOfWeekMask = 1 << (int)DayOfWeek.Sunday
        };
        var serverLocalNow = TimeZoneInfo.ConvertTimeFromUtc(
            new DateTime(2026, 3, 8, 6, 0, 0, DateTimeKind.Utc),
            TimeZoneInfo.Local);

        var nextLocal = HueSceneAutomationService.GetNextRunLocal(schedule, serverLocalNow);
        Assert.NotEqual(new DateTime(2026, 3, 8, 2, 30, 0), nextLocal);
    }

    [Fact]
    public void DateWindow_ClampsNextRunAndRejectsOutsideDates()
    {
        var schedule = new HueSceneSchedule
        {
            Enabled = true,
            TimeOfDay = "07:05",
            TimeZoneId = TimeZoneInfo.Utc.Id,
            StartDate = "2026-08-24",
            EndDate = "2026-08-31",
            DaysOfWeekMask = 1 << (int)DayOfWeek.Monday
        };

        var beforeStartUtc = new DateTime(2026, 8, 17, 7, 5, 30, DateTimeKind.Utc);
        var insideWindowUtc = new DateTime(2026, 8, 24, 7, 5, 30, DateTimeKind.Utc);
        var afterEndUtc = new DateTime(2026, 9, 7, 7, 5, 30, DateTimeKind.Utc);

        Assert.False(HueSceneAutomationService.IsDue(
            schedule,
            TimeZoneInfo.ConvertTimeFromUtc(beforeStartUtc, TimeZoneInfo.Local)));
        Assert.True(HueSceneAutomationService.IsDue(
            schedule,
            TimeZoneInfo.ConvertTimeFromUtc(insideWindowUtc, TimeZoneInfo.Local)));
        Assert.False(HueSceneAutomationService.IsDue(
            schedule,
            TimeZoneInfo.ConvertTimeFromUtc(afterEndUtc, TimeZoneInfo.Local)));

        var nextBeforeStart = HueSceneAutomationService.GetNextRunUtc(
            schedule,
            TimeZoneInfo.ConvertTimeFromUtc(beforeStartUtc.AddMinutes(-1), TimeZoneInfo.Local));
        Assert.Equal(new DateTime(2026, 8, 24, 7, 5, 0, DateTimeKind.Utc), nextBeforeStart);

        var nextAfterEnd = HueSceneAutomationService.GetNextRunUtc(
            schedule,
            TimeZoneInfo.ConvertTimeFromUtc(new DateTime(2026, 9, 1, 7, 0, 0, DateTimeKind.Utc), TimeZoneInfo.Local));
        Assert.Null(nextAfterEnd);
    }

    [Fact]
    public void EvaluateReadiness_RejectsInvalidDateWindowWithoutCredentials()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } }
        };

        var invalidStart = HueSceneAutomationService.EvaluateReadiness(
            config,
            new HueSceneSchedule { Enabled = true, PresetName = "Evening", StartDate = "2026-02-30" });
        var reversed = HueSceneAutomationService.EvaluateReadiness(
            config,
            new HueSceneSchedule
            {
                Enabled = true,
                PresetName = "Evening",
                StartDate = "2026-09-01",
                EndDate = "2026-08-01"
            });

        Assert.False(invalidStart.Ready);
        Assert.Contains("start date", invalidStart.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(reversed.Ready);
        Assert.Contains("before", reversed.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateReadiness_ReportsDisabledAndMissingTargetWithoutCredentials()
    {
        var config = new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "app-secret",
            HueClientKey = "client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } }
        };

        var disabled = HueSceneAutomationService.EvaluateReadiness(
            config,
            new HueSceneSchedule { Enabled = false, PresetName = "Evening" });
        var missingTarget = HueSceneAutomationService.EvaluateReadiness(
            config,
            new HueSceneSchedule { Enabled = true, PresetName = "Evening", TargetUserId = "missing-user" });
        var missingTimeZone = HueSceneAutomationService.EvaluateReadiness(
            config,
            new HueSceneSchedule { Enabled = true, PresetName = "Evening", TimeZoneId = "Missing/Zone" });

        Assert.False(disabled.Ready);
        Assert.Equal("Disabled.", disabled.Message);
        Assert.False(missingTarget.Ready);
        Assert.Contains("mapping", missingTarget.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("app-secret", missingTarget.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("client-secret", missingTarget.Message, StringComparison.Ordinal);
        Assert.False(missingTimeZone.Ready);
        Assert.Contains("time zone", missingTimeZone.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryResolveTarget_UsesCustomMappingWithoutChangingCredentialFreeSchedule()
    {
        var config = new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "global-app-secret",
            HueClientKey = "global-client-secret",
            EntertainmentAreaId = "global-area",
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-1",
                    UserName = "Living Room",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "mapping-app-secret",
                    HueClientKey = "mapping-client-secret",
                    EntertainmentAreaId = "mapping-area",
                    ChannelIdsOverride = "2, 9"
                }
            }
        };
        var schedule = new HueSceneSchedule { TargetUserId = "user-1" };

        Assert.True(HueSceneAutomationService.TryResolveTarget(config, schedule, out var target, out var error));
        Assert.Empty(error);
        Assert.Equal("Living Room", target.TargetLabel);
        Assert.Equal("192.168.1.101", target.BridgeIp);
        Assert.Equal("mapping-area", target.EntertainmentAreaId);
        Assert.Equal(new[] { 2, 9 }, target.ChannelIds!.OrderBy(id => id));
        var serialized = JsonSerializer.Serialize(schedule);
        Assert.DoesNotContain("mapping-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("mapping-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunSchedule_ResolvesPresetAndReturnsSanitizedResult()
    {
        InstallConfiguration(new PluginConfiguration
        {
            PersistSceneScheduleHistory = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "app-secret",
            HueClientKey = "client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Evening", Red = 12, Green = 34, Blue = 56, BrightnessPercent = 75, DurationSeconds = 8 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "cue-1", Name = "Evening cue", PresetName = "evening" }
            }
        });

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var hueClient = new HueClient(httpClient, Mock.Of<ILogger<HueClient>>());
        var streamTester = new Mock<IHueStreamTester>();
        streamTester
            .Setup(tester => tester.PreviewAsync(
                "192.168.1.100",
                "app-secret",
                "client-secret",
                "area-1",
                It.IsAny<JsonElement>(),
                null,
                12,
                34,
                56,
                75,
                8,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HueStreamProbeResult
            {
                Succeeded = true,
                Message = "Displayed scheduled scene."
            });

        var service = new HueSceneAutomationService(
            streamTester.Object,
            hueClient,
            Mock.Of<ILogger<HueSceneAutomationService>>());

        var result = await service.RunScheduleAsync("cue-1");

        Assert.True(result.Succeeded);
        Assert.Equal("Evening cue", result.ScheduleName);
        Assert.Equal("Evening", result.PresetName);
        Assert.Equal("Default bridge target", result.TargetLabel);
        Assert.Equal(1, result.RunCount);
        var persisted = Assert.Single(Plugin.Instance!.Configuration.PersistedSceneScheduleHistory);
        Assert.Equal("cue-1", persisted.ScheduleId);
        Assert.Equal(1, persisted.RunCount);
        var history = Assert.Single(service.GetHistory());
        Assert.Equal(result.Message, history.Message);
        streamTester.VerifyAll();
        var status = service.GetStatus();
        var runtime = Assert.Single(status.Schedules);
        Assert.True(status.ServiceAvailable);
        Assert.Equal("cue-1", runtime.ScheduleId);
        Assert.Equal(string.Empty, runtime.TimeZoneId);
        Assert.Contains("Server local", runtime.TimeZoneDisplayName, StringComparison.Ordinal);
        Assert.NotNull(runtime.NextRunUtc);
        Assert.Equal(1, runtime.RunCount);
        Assert.True(runtime.LastSucceeded == true);
        Assert.False(runtime.IsRunning);
        Assert.True(runtime.Ready);
        Assert.Contains("Ready", runtime.ReadinessMessage, StringComparison.Ordinal);
        Assert.Equal("Displayed scheduled scene.", runtime.LastMessage);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("client-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("app-secret", JsonSerializer.Serialize(status), StringComparison.Ordinal);
        Assert.DoesNotContain("client-secret", JsonSerializer.Serialize(status), StringComparison.Ordinal);
    }

    [Fact]
    public void PersistedHistory_LoadsIntoStatusAndCanBeClearedWithoutCredentials()
    {
        InstallConfiguration(new PluginConfiguration
        {
            PersistSceneScheduleHistory = true,
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "cue-1", Name = "Evening cue", PresetName = "Evening" }
            },
            PersistedSceneScheduleHistory = new List<HueSceneScheduleHistoryEntry>
            {
                new()
                {
                    ScheduleId = "cue-1",
                    ScheduleName = "Evening cue",
                    PresetName = "Evening",
                    TargetLabel = "Living Room",
                    Succeeded = false,
                    Message = "The bridge was unavailable.",
                    CleanupWarning = "Cleanup warning",
                    RunAtUtc = DateTime.UtcNow.AddMinutes(-5),
                    RunCount = 7
                }
            }
        });

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        var status = service.GetStatus();
        var runtime = Assert.Single(status.Schedules);
        Assert.Equal(7, runtime.RunCount);
        Assert.False(runtime.LastSucceeded);
        Assert.Equal("The bridge was unavailable.", runtime.LastMessage);
        var history = Assert.Single(service.GetHistory());
        Assert.Equal("Living Room", history.TargetLabel);
        Assert.Equal(7, history.RunCount);
        var serialized = JsonSerializer.Serialize(history);
        Assert.DoesNotContain("AppKey", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ClientKey", serialized, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, service.ClearHistory());
        Assert.Empty(service.GetHistory());
        Assert.Empty(Plugin.Instance!.Configuration.PersistedSceneScheduleHistory);
        runtime = Assert.Single(service.GetStatus().Schedules);
        Assert.Equal(0, runtime.RunCount);
        Assert.Null(runtime.LastSucceeded);
    }

    private static void InstallConfiguration(PluginConfiguration configuration)
    {
        var pluginDataPath = Path.Combine(Path.GetTempPath(), "jellyfin-hue-schedule-test-" + Guid.NewGuid().ToString("N"));
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

        var plugin = new Jellyfin.Plugin.Hue.Plugin(applicationPaths.Object, Mock.Of<IXmlSerializer>());
        var configurationField = plugin.GetType().BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!;
        configurationField.SetValue(plugin, configuration);
    }

    private sealed class AreaConfigurationHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"channels\":[{\"channel_id\":0}]}]}",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
