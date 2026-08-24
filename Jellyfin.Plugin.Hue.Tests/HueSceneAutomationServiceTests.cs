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
    public void SolarCalculator_ProducesTimezoneAwareSunriseAndSunset()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");
        var date = new DateTime(2026, 6, 21);

        Assert.True(HueSolarCalculator.TryGetEventLocal(
            date,
            zone,
            40.7128,
            -74.0060,
            sunrise: true,
            offsetMinutes: 0,
            out var sunrise,
            out var sunriseUtc));
        Assert.True(HueSolarCalculator.TryGetEventLocal(
            date,
            zone,
            40.7128,
            -74.0060,
            sunrise: false,
            offsetMinutes: 0,
            out var sunset,
            out var sunsetUtc));

        Assert.Equal(new DateTime(2026, 6, 21), sunrise.Date);
        Assert.Equal(new DateTime(2026, 6, 21), sunset.Date);
        Assert.InRange(sunrise.Hour, 5, 6);
        Assert.InRange(sunset.Hour, 20, 21);
        Assert.Equal(DateTimeKind.Utc, sunriseUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, sunsetUtc.Kind);
        Assert.True(sunriseUtc < sunsetUtc);

        Assert.True(HueSolarCalculator.TryGetCivilTwilightLocal(
            date,
            zone,
            40.7128,
            -74.0060,
            dawn: true,
            offsetMinutes: 0,
            out var civilDawn,
            out var civilDawnUtc));
        Assert.True(HueSolarCalculator.TryGetCivilTwilightLocal(
            date,
            zone,
            40.7128,
            -74.0060,
            dawn: false,
            offsetMinutes: 0,
            out var civilDusk,
            out var civilDuskUtc));
        Assert.Equal(date, civilDawn.Date);
        Assert.Equal(date, civilDusk.Date);
        Assert.True(civilDawn < sunrise);
        Assert.True(civilDusk > sunset);
        Assert.Equal(DateTimeKind.Utc, civilDawnUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, civilDuskUtc.Kind);

        Assert.True(HueSolarCalculator.TryGetNauticalTwilightLocal(
            date,
            zone,
            40.7128,
            -74.0060,
            dawn: true,
            offsetMinutes: 0,
            out var nauticalDawn,
            out var nauticalDawnUtc));
        Assert.True(HueSolarCalculator.TryGetNauticalTwilightLocal(
            date,
            zone,
            40.7128,
            -74.0060,
            dawn: false,
            offsetMinutes: 0,
            out var nauticalDusk,
            out var nauticalDuskUtc));
        Assert.True(HueSolarCalculator.TryGetAstronomicalTwilightLocal(
            date,
            zone,
            40.7128,
            -74.0060,
            dawn: true,
            offsetMinutes: 0,
            out var astronomicalDawn,
            out var astronomicalDawnUtc));
        Assert.True(HueSolarCalculator.TryGetAstronomicalTwilightLocal(
            date,
            zone,
            40.7128,
            -74.0060,
            dawn: false,
            offsetMinutes: 0,
            out var astronomicalDusk,
            out var astronomicalDuskUtc));
        Assert.True(astronomicalDawn < nauticalDawn);
        Assert.True(nauticalDawn < civilDawn);
        Assert.True(civilDusk < nauticalDusk);
        Assert.True(nauticalDusk < astronomicalDusk);
        Assert.Equal(DateTimeKind.Utc, nauticalDawnUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, nauticalDuskUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, astronomicalDawnUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, astronomicalDuskUtc.Kind);

        Assert.True(HueSolarCalculator.TryGetEventLocal(
            date,
            zone,
            40.7128,
            -74.0060,
            sunrise: true,
            offsetMinutes: -30,
            out var earlierSunrise,
            out _));
        Assert.Equal(sunrise.AddMinutes(-30), earlierSunrise);

        Assert.True(HueSolarCalculator.TryGetSolarNoonLocal(
            date,
            zone,
            40.7128,
            -74.0060,
            offsetMinutes: 0,
            out var solarNoon,
            out var solarNoonUtc));
        Assert.Equal(date, solarNoon.Date);
        Assert.InRange(solarNoon.Hour, 12, 13);
        Assert.Equal(DateTimeKind.Utc, solarNoonUtc.Kind);
        Assert.True(solarNoonUtc > sunriseUtc && solarNoonUtc < sunsetUtc);
    }

    [Fact]
    public void SolarSchedule_UsesEventTimeForDueAndUpcomingOccurrence()
    {
        var schedule = new HueSceneSchedule
        {
            Id = "solar-daily",
            Name = "Solar daily",
            Enabled = true,
            PresetName = "Scene",
            TimeMode = PluginConfiguration.SceneScheduleTimeModeSunrise,
            SolarLatitude = 0,
            SolarLongitude = 0,
            TimeZoneId = TimeZoneInfo.Utc.Id,
            Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
            StartDate = "2026-08-17",
            DaysOfWeekMask = 0
        };

        var beforeSunrise = new DateTime(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc);
        var occurrences = HueSceneAutomationService.GetUpcomingOccurrences(
            schedule,
            beforeSunrise,
            maxOccurrences: 2,
            horizonDays: 3);

        Assert.Equal(2, occurrences.Count);
        Assert.All(occurrences, occurrence =>
        {
            Assert.Equal(PluginConfiguration.SceneScheduleTimeModeSunrise, occurrence.TimeMode);
            Assert.Equal(0, occurrence.SolarOffsetMinutes);
            Assert.Equal(0, occurrence.SolarLatitude);
            Assert.Equal(0, occurrence.SolarLongitude);
            Assert.Equal(6, occurrence.LocalTime.Hour);
            Assert.True(HueSceneAutomationService.IsDue(
                schedule,
                DateTime.SpecifyKind(occurrence.LocalTime.AddSeconds(30), DateTimeKind.Utc)));
        });
        Assert.True(occurrences[0].UtcTime < occurrences[1].UtcTime);
    }

    [Fact]
    public void UpcomingOccurrences_ExposeClampedDirectRgbOverrides()
    {
        var schedule = new HueSceneSchedule
        {
            Id = "rgb-occurrence",
            Name = "RGB occurrence",
            Enabled = true,
            PresetName = "Scene",
            TimeOfDay = "07:05",
            TimeZoneId = TimeZoneInfo.Utc.Id,
            Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
            StartDate = "2026-08-17",
            DaysOfWeekMask = 0
        };

        var occurrences = HueSceneAutomationService.GetUpcomingOccurrences(
            schedule,
            new DateTime(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc),
            maxOccurrences: 1,
            horizonDays: 2,
            redOverride: 300,
            greenOverride: -10,
            blueOverride: 42);

        var occurrence = Assert.Single(occurrences);
        Assert.Equal(255, occurrence.Red);
        Assert.Equal(0, occurrence.Green);
        Assert.Equal(42, occurrence.Blue);
    }

    [Fact]
    public void SolarNoonSchedule_UsesNoonTimeForDueAndUpcomingOccurrence()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");
        var schedule = new HueSceneSchedule
        {
            Id = "solar-noon-daily",
            Name = "Solar noon daily",
            Enabled = true,
            PresetName = "Scene",
            TimeMode = PluginConfiguration.SceneScheduleTimeModeSolarNoon,
            SolarLatitude = 40.7128,
            SolarLongitude = -74.0060,
            TimeZoneId = zone.Id,
            Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
            StartDate = "2026-06-21",
            DaysOfWeekMask = 0
        };

        Assert.True(HueSolarCalculator.TryGetSolarNoonLocal(
            new DateTime(2026, 6, 21),
            zone,
            40.7128,
            -74.0060,
            offsetMinutes: 0,
            out var solarNoon,
            out _));

        var occurrences = HueSceneAutomationService.GetUpcomingOccurrences(
            schedule,
            new DateTime(2026, 6, 21, 0, 0, 0, DateTimeKind.Utc),
            maxOccurrences: 1,
            horizonDays: 2);
        var occurrence = Assert.Single(occurrences);
        Assert.Equal(PluginConfiguration.SceneScheduleTimeModeSolarNoon, occurrence.TimeMode);
        Assert.Equal(solarNoon, occurrence.LocalTime);
        Assert.True(HueSceneAutomationService.IsDue(
            schedule,
            DateTime.SpecifyKind(occurrence.UtcTime.AddSeconds(30), DateTimeKind.Utc)));
    }

    [Fact]
    public void CivilTwilightSchedule_UsesDawnTimeForDueAndUpcomingOccurrence()
    {
        var schedule = new HueSceneSchedule
        {
            Id = "civil-dawn-daily",
            Name = "Civil dawn daily",
            Enabled = true,
            PresetName = "Scene",
            TimeMode = PluginConfiguration.SceneScheduleTimeModeCivilDawn,
            SolarLatitude = 40.7128,
            SolarLongitude = -74.0060,
            TimeZoneId = TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York").Id,
            Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
            StartDate = "2026-06-21",
            DaysOfWeekMask = 0
        };

        var zone = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");
        Assert.True(HueSolarCalculator.TryGetCivilTwilightLocal(
            new DateTime(2026, 6, 21),
            zone,
            40.7128,
            -74.0060,
            dawn: true,
            offsetMinutes: 0,
            out var civilDawn,
            out _));

        var occurrences = HueSceneAutomationService.GetUpcomingOccurrences(
            schedule,
            new DateTime(2026, 6, 21, 0, 0, 0, DateTimeKind.Utc),
            maxOccurrences: 1,
            horizonDays: 2);
        var occurrence = Assert.Single(occurrences);
        Assert.Equal(PluginConfiguration.SceneScheduleTimeModeCivilDawn, occurrence.TimeMode);
        Assert.Equal(civilDawn, occurrence.LocalTime);
        Assert.True(HueSceneAutomationService.IsDue(
            schedule,
            DateTime.SpecifyKind(occurrence.UtcTime.AddSeconds(30), DateTimeKind.Utc)));
    }

    [Fact]
    public void AstronomicalTwilightSchedule_UsesDuskTimeForDueAndUpcomingOccurrence()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");
        var schedule = new HueSceneSchedule
        {
            Id = "astronomical-dusk-daily",
            Name = "Astronomical dusk daily",
            Enabled = true,
            PresetName = "Scene",
            TimeMode = PluginConfiguration.SceneScheduleTimeModeAstronomicalDusk,
            SolarLatitude = 40.7128,
            SolarLongitude = -74.0060,
            TimeZoneId = zone.Id,
            Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
            StartDate = "2026-06-21",
            DaysOfWeekMask = 0
        };

        Assert.True(HueSolarCalculator.TryGetAstronomicalTwilightLocal(
            new DateTime(2026, 6, 21),
            zone,
            40.7128,
            -74.0060,
            dawn: false,
            offsetMinutes: 0,
            out var astronomicalDusk,
            out _));

        var occurrences = HueSceneAutomationService.GetUpcomingOccurrences(
            schedule,
            new DateTime(2026, 6, 21, 0, 0, 0, DateTimeKind.Utc),
            maxOccurrences: 1,
            horizonDays: 2);
        var occurrence = Assert.Single(occurrences);
        Assert.Equal(PluginConfiguration.SceneScheduleTimeModeAstronomicalDusk, occurrence.TimeMode);
        Assert.Equal(astronomicalDusk, occurrence.LocalTime);
        Assert.True(HueSceneAutomationService.IsDue(
            schedule,
            DateTime.SpecifyKind(occurrence.UtcTime.AddSeconds(30), DateTimeKind.Utc)));
    }

    [Fact]
    public void SolarSchedule_RejectsPolarNoEventWithoutManufacturingOccurrence()
    {
        var schedule = new HueSceneSchedule
        {
            Id = "polar-solar",
            Name = "Polar solar",
            Enabled = true,
            PresetName = "Scene",
            TimeMode = PluginConfiguration.SceneScheduleTimeModeSunrise,
            SolarLatitude = 90,
            SolarLongitude = 0,
            TimeZoneId = TimeZoneInfo.Utc.Id,
            Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
            StartDate = "2026-06-17",
            DaysOfWeekMask = 0
        };

        Assert.Empty(HueSceneAutomationService.GetUpcomingOccurrences(
            schedule,
            new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc),
            maxOccurrences: 3,
            horizonDays: 3));
    }

    [Fact]
    public void SolarNoonSchedule_RemainsAvailableDuringPolarDay()
    {
        var schedule = new HueSceneSchedule
        {
            Id = "polar-noon",
            Name = "Polar noon",
            Enabled = true,
            PresetName = "Scene",
            TimeMode = PluginConfiguration.SceneScheduleTimeModeSolarNoon,
            SolarLatitude = 90,
            SolarLongitude = 0,
            TimeZoneId = TimeZoneInfo.Utc.Id,
            Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
            StartDate = "2026-06-17",
            DaysOfWeekMask = 0
        };

        Assert.True(HueSolarCalculator.TryGetSolarNoonLocal(
            new DateTime(2026, 6, 17),
            TimeZoneInfo.Utc,
            90,
            0,
            offsetMinutes: 0,
            out var solarNoon,
            out _));

        var occurrences = HueSceneAutomationService.GetUpcomingOccurrences(
            schedule,
            new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc),
            maxOccurrences: 1,
            horizonDays: 1);
        var occurrence = Assert.Single(occurrences);
        Assert.Equal(solarNoon, occurrence.LocalTime);
    }

    [Fact]
    public void SolarSchedule_AllowsOffsetAcrossLocalMidnight()
    {
        var date = new DateTime(2026, 6, 21);
        var zone = TimeZoneInfo.Utc;
        Assert.True(HueSolarCalculator.TryGetEventLocal(
            date,
            zone,
            0,
            0,
            sunrise: false,
            offsetMinutes: PluginConfiguration.MaxSceneScheduleSolarOffsetMinutes,
            out var shiftedLocal,
            out var shiftedUtc));
        Assert.Equal(date.AddDays(1), shiftedLocal.Date);
        Assert.Equal(DateTimeKind.Utc, shiftedUtc.Kind);

        var schedule = new HueSceneSchedule
        {
            Id = "solar-midnight",
            Name = "Solar midnight",
            Enabled = true,
            PresetName = "Scene",
            TimeMode = PluginConfiguration.SceneScheduleTimeModeSunset,
            SolarOffsetMinutes = PluginConfiguration.MaxSceneScheduleSolarOffsetMinutes,
            SolarLatitude = 0,
            SolarLongitude = 0,
            TimeZoneId = TimeZoneInfo.Utc.Id,
            Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
            StartDate = date.ToString("yyyy-MM-dd"),
            DaysOfWeekMask = 0
        };

        var occurrences = HueSceneAutomationService.GetUpcomingOccurrences(
            schedule,
            new DateTime(2026, 6, 21, 0, 0, 0, DateTimeKind.Utc),
            maxOccurrences: 1,
            horizonDays: 2);
        var occurrence = Assert.Single(occurrences);
        Assert.Equal(date.AddDays(1), occurrence.LocalTime.Date);

        var shiftedDateOccurrences = HueSceneAutomationService.GetUpcomingOccurrences(
            schedule,
            new DateTime(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc),
            maxOccurrences: 1,
            horizonDays: 2);
        var shiftedDateOccurrence = Assert.Single(shiftedDateOccurrences);
        Assert.Equal(occurrence.UtcTime, shiftedDateOccurrence.UtcTime);

        Assert.True(HueSceneAutomationService.IsDue(
            schedule,
            DateTime.SpecifyKind(occurrence.LocalTime.AddSeconds(30), DateTimeKind.Utc)));
        var missed = HueSceneAutomationService.GetMostRecentMissedOccurrence(
            schedule,
            DateTime.SpecifyKind(occurrence.LocalTime.AddMinutes(1), DateTimeKind.Utc),
            catchUpMinutes: 10);
        Assert.NotNull(missed);
        Assert.Equal(occurrence.UtcTime, missed!.UtcTime);
    }

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
    public void DailyRecurrence_RunsEveryCalendarDateWithoutWeekdayMask()
    {
        var schedule = new HueSceneSchedule
        {
            Enabled = true,
            TimeOfDay = "07:05",
            TimeZoneId = TimeZoneInfo.Utc.Id,
            Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
            DaysOfWeekMask = 0,
            StartDate = "2026-08-17",
            EndDate = "2026-08-20",
            ExcludedDates = new List<string> { "2026-08-18" }
        };
        var beforeFirstCue = new DateTime(2026, 8, 17, 6, 0, 0, DateTimeKind.Utc);

        Assert.True(HueSceneAutomationService.IsDue(
            schedule,
            new DateTime(2026, 8, 17, 7, 5, 30, DateTimeKind.Utc)));
        Assert.False(HueSceneAutomationService.IsDue(
            schedule,
            new DateTime(2026, 8, 18, 7, 5, 30, DateTimeKind.Utc)));

        var occurrences = HueSceneAutomationService.GetUpcomingOccurrences(
            schedule,
            beforeFirstCue,
            maxOccurrences: 5,
            horizonDays: 5);

        Assert.Equal(3, occurrences.Count);
        Assert.Equal(new DateTime(2026, 8, 17, 7, 5, 0), occurrences[0].LocalTime);
        Assert.Equal(new DateTime(2026, 8, 19, 7, 5, 0), occurrences[1].LocalTime);
        Assert.Equal(new DateTime(2026, 8, 20, 7, 5, 0), occurrences[2].LocalTime);
    }

    [Fact]
    public void RecurrenceInterval_UsesStartDateAnchorAcrossCalendarUnits()
    {
        var daily = new HueSceneSchedule
        {
            Enabled = true,
            TimeOfDay = "07:05",
            TimeZoneId = TimeZoneInfo.Utc.Id,
            Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
            RecurrenceInterval = 2,
            StartDate = "2026-08-17",
            DaysOfWeekMask = 0
        };

        Assert.True(HueSceneAutomationService.IsDue(
            daily,
            new DateTime(2026, 8, 17, 7, 5, 30, DateTimeKind.Utc)));
        Assert.False(HueSceneAutomationService.IsDue(
            daily,
            new DateTime(2026, 8, 18, 7, 5, 30, DateTimeKind.Utc)));
        Assert.True(HueSceneAutomationService.IsDue(
            daily,
            new DateTime(2026, 8, 19, 7, 5, 30, DateTimeKind.Utc)));

        var dailyOccurrences = HueSceneAutomationService.GetUpcomingOccurrences(
            daily,
            new DateTime(2026, 8, 16, 6, 0, 0, DateTimeKind.Utc),
            maxOccurrences: 3,
            horizonDays: 6);
        Assert.Equal(
            new[]
            {
                new DateTime(2026, 8, 17, 7, 5, 0),
                new DateTime(2026, 8, 19, 7, 5, 0),
                new DateTime(2026, 8, 21, 7, 5, 0)
            },
            dailyOccurrences.Select(occurrence => occurrence.LocalTime));
        Assert.All(dailyOccurrences, occurrence => Assert.Equal(2, occurrence.RecurrenceInterval));

        var weekly = new HueSceneSchedule
        {
            Enabled = true,
            TimeOfDay = "07:05",
            TimeZoneId = TimeZoneInfo.Utc.Id,
            Recurrence = PluginConfiguration.SceneScheduleRecurrenceWeekly,
            RecurrenceInterval = 2,
            StartDate = "2026-08-17",
            DaysOfWeekMask = 1 << (int)DayOfWeek.Monday
        };
        Assert.True(HueSceneAutomationService.IsDue(
            weekly,
            new DateTime(2026, 8, 17, 7, 5, 30, DateTimeKind.Utc)));
        Assert.False(HueSceneAutomationService.IsDue(
            weekly,
            new DateTime(2026, 8, 24, 7, 5, 30, DateTimeKind.Utc)));
        Assert.True(HueSceneAutomationService.IsDue(
            weekly,
            new DateTime(2026, 8, 31, 7, 5, 30, DateTimeKind.Utc)));

        var monthly = new HueSceneSchedule
        {
            Enabled = true,
            TimeOfDay = "07:05",
            TimeZoneId = TimeZoneInfo.Utc.Id,
            Recurrence = PluginConfiguration.SceneScheduleRecurrenceMonthly,
            RecurrenceInterval = 2,
            DayOfMonth = 31,
            StartDate = "2026-01-01",
            DaysOfWeekMask = 0
        };
        Assert.True(HueSceneAutomationService.IsDue(
            monthly,
            new DateTime(2026, 1, 31, 7, 5, 30, DateTimeKind.Utc)));
        Assert.False(HueSceneAutomationService.IsDue(
            monthly,
            new DateTime(2026, 2, 28, 7, 5, 30, DateTimeKind.Utc)));
        Assert.True(HueSceneAutomationService.IsDue(
            monthly,
            new DateTime(2026, 3, 31, 7, 5, 30, DateTimeKind.Utc)));

        var monthlyWeekday = new HueSceneSchedule
        {
            Enabled = true,
            TimeOfDay = "07:05",
            TimeZoneId = TimeZoneInfo.Utc.Id,
            Recurrence = PluginConfiguration.SceneScheduleRecurrenceMonthlyWeekday,
            RecurrenceInterval = 2,
            StartDate = "2026-01-01",
            WeekOfMonth = 1,
            DayOfWeek = (int)DayOfWeek.Monday
        };
        Assert.True(HueSceneAutomationService.IsDue(
            monthlyWeekday,
            new DateTime(2026, 1, 5, 7, 5, 30, DateTimeKind.Utc)));
        Assert.False(HueSceneAutomationService.IsDue(
            monthlyWeekday,
            new DateTime(2026, 2, 2, 7, 5, 30, DateTimeKind.Utc)));
        Assert.True(HueSceneAutomationService.IsDue(
            monthlyWeekday,
            new DateTime(2026, 3, 2, 7, 5, 30, DateTimeKind.Utc)));

        var yearly = new HueSceneSchedule
        {
            Enabled = true,
            TimeOfDay = "07:05",
            TimeZoneId = TimeZoneInfo.Utc.Id,
            Recurrence = PluginConfiguration.SceneScheduleRecurrenceYearly,
            RecurrenceInterval = 2,
            MonthOfYear = 12,
            DayOfMonth = 31,
            StartDate = "2026-01-01",
            DaysOfWeekMask = 0
        };
        Assert.True(HueSceneAutomationService.IsDue(
            yearly,
            new DateTime(2026, 12, 31, 7, 5, 30, DateTimeKind.Utc)));
        Assert.False(HueSceneAutomationService.IsDue(
            yearly,
            new DateTime(2027, 12, 31, 7, 5, 30, DateTimeKind.Utc)));
        Assert.True(HueSceneAutomationService.IsDue(
            yearly,
            new DateTime(2028, 12, 31, 7, 5, 30, DateTimeKind.Utc)));
    }

    [Fact]
    public void GetUpcomingConflicts_ReportsDurationAwareCollisionsWithoutSecrets()
    {
        var configuration = new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "conflict-app-secret",
            HueClientKey = "conflict-client-secret",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Long scene", DurationSeconds = 10 },
                new() { Name = "Short scene", DurationSeconds = 5 },
                new() { Name = "Later scene", DurationSeconds = 5 }
            },
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = "user-1", UserName = "Living Room", SyncEnabled = true }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "long-cue",
                    Name = "Long cue",
                    PresetName = "Long scene",
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    Priority = 80,
                    Enabled = true
                },
                new()
                {
                    Id = "short-cue",
                    Name = "Short cue",
                    PresetName = "Short scene",
                    TargetUserId = "user-1",
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    Priority = 20,
                    Enabled = true
                },
                new()
                {
                    Id = "later-cue",
                    Name = "Later cue",
                    PresetName = "Later scene",
                    TimeOfDay = "07:20",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    Enabled = true
                }
            }
        };

        var conflicts = HueSceneAutomationService.GetUpcomingConflicts(
            configuration,
            new DateTime(2026, 8, 18, 6, 0, 0, DateTimeKind.Utc),
            maxConflicts: 10,
            horizonDays: 1);

        var conflict = Assert.Single(conflicts);
        Assert.Equal("long-cue", conflict.FirstScheduleId);
        Assert.Equal("Long cue", conflict.FirstScheduleName);
        Assert.Equal("Default bridge target", conflict.FirstTargetLabel);
        Assert.Equal("short-cue", conflict.SecondScheduleId);
        Assert.Equal("Living Room", conflict.SecondTargetLabel);
        Assert.Equal(10, conflict.FirstDurationSeconds);
        Assert.Equal(5, conflict.SecondDurationSeconds);
        Assert.Equal(5, conflict.OverlapSeconds);
        Assert.Equal(80, conflict.FirstPriority);
        Assert.Equal(20, conflict.SecondPriority);
        Assert.Contains("higher priority", conflict.ResolutionHint, StringComparison.OrdinalIgnoreCase);

        var serialized = JsonSerializer.Serialize(conflicts);
        Assert.DoesNotContain("conflict-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("conflict-client-secret", serialized, StringComparison.Ordinal);

        var filteredConflicts = HueSceneAutomationService.GetUpcomingConflicts(
            configuration,
            new DateTime(2026, 8, 18, 6, 0, 0, DateTimeKind.Utc),
            maxConflicts: 10,
            horizonDays: 1,
            scheduleId: "short-cue");

        var filteredConflict = Assert.Single(filteredConflicts);
        Assert.Contains(
            new[] { filteredConflict.FirstScheduleId, filteredConflict.SecondScheduleId },
            scheduleId => string.Equals(scheduleId, "short-cue", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Long cue", filteredConflict.FirstScheduleName + filteredConflict.SecondScheduleName, StringComparison.Ordinal);
        Assert.Empty(HueSceneAutomationService.GetUpcomingConflicts(
            configuration,
            new DateTime(2026, 8, 18, 6, 0, 0, DateTimeKind.Utc),
            maxConflicts: 10,
            horizonDays: 1,
            scheduleId: "missing-cue"));
    }

    [Fact]
    public void MonthlyWeekdayRecurrence_MatchesFirstAndLastWeekday()
    {
        var firstMonday = new HueSceneSchedule
        {
            Enabled = true,
            TimeOfDay = "07:05",
            TimeZoneId = TimeZoneInfo.Utc.Id,
            Recurrence = PluginConfiguration.SceneScheduleRecurrenceMonthlyWeekday,
            WeekOfMonth = 1,
            DayOfWeek = (int)DayOfWeek.Monday,
            DaysOfWeekMask = 0
        };

        Assert.True(HueSceneAutomationService.IsDue(
            firstMonday,
            new DateTime(2026, 8, 3, 7, 5, 30, DateTimeKind.Utc)));
        Assert.False(HueSceneAutomationService.IsDue(
            firstMonday,
            new DateTime(2026, 8, 10, 7, 5, 30, DateTimeKind.Utc)));

        var firstOccurrences = HueSceneAutomationService.GetUpcomingOccurrences(
            firstMonday,
            new DateTime(2026, 8, 1, 6, 0, 0, DateTimeKind.Utc),
            maxOccurrences: 2,
            horizonDays: 45);
        Assert.Equal(2, firstOccurrences.Count);
        Assert.Equal(new DateTime(2026, 8, 3, 7, 5, 0), firstOccurrences[0].LocalTime);
        Assert.Equal(new DateTime(2026, 9, 7, 7, 5, 0), firstOccurrences[1].LocalTime);

        var lastFriday = new HueSceneSchedule
        {
            Enabled = true,
            TimeOfDay = "07:05",
            TimeZoneId = TimeZoneInfo.Utc.Id,
            Recurrence = PluginConfiguration.SceneScheduleRecurrenceMonthlyWeekday,
            WeekOfMonth = PluginConfiguration.SceneScheduleLastWeekOfMonth,
            DayOfWeek = (int)DayOfWeek.Friday,
            DaysOfWeekMask = 0
        };
        var lastOccurrences = HueSceneAutomationService.GetUpcomingOccurrences(
            lastFriday,
            new DateTime(2026, 8, 1, 6, 0, 0, DateTimeKind.Utc),
            maxOccurrences: 2,
            horizonDays: 60);
        Assert.Equal(2, lastOccurrences.Count);
        Assert.Equal(new DateTime(2026, 8, 28, 7, 5, 0), lastOccurrences[0].LocalTime);
        Assert.Equal(new DateTime(2026, 9, 25, 7, 5, 0), lastOccurrences[1].LocalTime);
    }

    [Fact]
    public void YearlyRecurrence_MatchesSelectedMonthAndClampsShortMonths()
    {
        var yearEnd = new HueSceneSchedule
        {
            Enabled = true,
            TimeOfDay = "07:05",
            TimeZoneId = TimeZoneInfo.Utc.Id,
            Recurrence = PluginConfiguration.SceneScheduleRecurrenceYearly,
            MonthOfYear = 12,
            DayOfMonth = 31,
            DaysOfWeekMask = 0
        };

        Assert.True(HueSceneAutomationService.IsDue(
            yearEnd,
            new DateTime(2026, 12, 31, 7, 5, 30, DateTimeKind.Utc)));
        Assert.False(HueSceneAutomationService.IsDue(
            yearEnd,
            new DateTime(2026, 11, 30, 7, 5, 30, DateTimeKind.Utc)));

        var nextYear = HueSceneAutomationService.GetUpcomingOccurrences(
            yearEnd,
            new DateTime(2027, 1, 1, 6, 0, 0, DateTimeKind.Utc),
            maxOccurrences: 1,
            horizonDays: 366);
        Assert.Single(nextYear);
        Assert.Equal(new DateTime(2027, 12, 31, 7, 5, 0), nextYear[0].LocalTime);
        Assert.Equal(12, nextYear[0].MonthOfYear);

        var februaryThirtyFirst = new HueSceneSchedule
        {
            Enabled = true,
            TimeOfDay = "07:05",
            TimeZoneId = TimeZoneInfo.Utc.Id,
            Recurrence = PluginConfiguration.SceneScheduleRecurrenceYearly,
            MonthOfYear = 2,
            DayOfMonth = 31,
            DaysOfWeekMask = 0
        };

        Assert.True(HueSceneAutomationService.IsDue(
            februaryThirtyFirst,
            new DateTime(2026, 2, 28, 7, 5, 30, DateTimeKind.Utc)));
        Assert.False(HueSceneAutomationService.IsDue(
            februaryThirtyFirst,
            new DateTime(2026, 2, 27, 7, 5, 30, DateTimeKind.Utc)));
        Assert.True(HueSceneAutomationService.IsDue(
            februaryThirtyFirst,
            new DateTime(2028, 2, 29, 7, 5, 30, DateTimeKind.Utc)));
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
            EndDate = "2026-09-07",
            ExcludedDates = new List<string> { "2026-08-24" },
            DaysOfWeekMask = 1 << (int)DayOfWeek.Monday
        };

        var beforeStartUtc = new DateTime(2026, 8, 17, 7, 5, 30, DateTimeKind.Utc);
        var insideWindowUtc = new DateTime(2026, 8, 24, 7, 5, 30, DateTimeKind.Utc);
        var afterEndUtc = new DateTime(2026, 9, 14, 7, 5, 30, DateTimeKind.Utc);

        Assert.False(HueSceneAutomationService.IsDue(
            schedule,
            TimeZoneInfo.ConvertTimeFromUtc(beforeStartUtc, TimeZoneInfo.Local)));
        Assert.False(HueSceneAutomationService.IsDue(
            schedule,
            TimeZoneInfo.ConvertTimeFromUtc(insideWindowUtc, TimeZoneInfo.Local)));
        var nextAllowedUtc = new DateTime(2026, 8, 31, 7, 5, 30, DateTimeKind.Utc);
        Assert.True(HueSceneAutomationService.IsDue(
            schedule,
            TimeZoneInfo.ConvertTimeFromUtc(nextAllowedUtc, TimeZoneInfo.Local)));
        Assert.False(HueSceneAutomationService.IsDue(
            schedule,
            TimeZoneInfo.ConvertTimeFromUtc(afterEndUtc, TimeZoneInfo.Local)));

        var nextBeforeStart = HueSceneAutomationService.GetNextRunUtc(
            schedule,
            TimeZoneInfo.ConvertTimeFromUtc(beforeStartUtc.AddMinutes(-1), TimeZoneInfo.Local));
        Assert.Equal(new DateTime(2026, 8, 31, 7, 5, 0, DateTimeKind.Utc), nextBeforeStart);

        var nextAfterEnd = HueSceneAutomationService.GetNextRunUtc(
            schedule,
            TimeZoneInfo.ConvertTimeFromUtc(new DateTime(2026, 9, 8, 7, 0, 0, DateTimeKind.Utc), TimeZoneInfo.Local));
        Assert.Null(nextAfterEnd);

        var occurrences = HueSceneAutomationService.GetUpcomingOccurrences(
            schedule,
            TimeZoneInfo.ConvertTimeFromUtc(beforeStartUtc.AddMinutes(-1), TimeZoneInfo.Local),
            maxOccurrences: 3,
            horizonDays: 31);
        Assert.Equal(2, occurrences.Count);
        Assert.Equal(new DateTime(2026, 8, 31, 7, 5, 0), occurrences[0].LocalTime);
        Assert.Equal(new DateTime(2026, 8, 31, 7, 5, 0, DateTimeKind.Utc), occurrences[0].UtcTime);
        Assert.Equal(new DateTime(2026, 9, 7, 7, 5, 0, DateTimeKind.Utc), occurrences[1].UtcTime);

        var futureSchedule = new HueSceneSchedule
        {
            Enabled = true,
            TimeOfDay = "07:05",
            TimeZoneId = TimeZoneInfo.Utc.Id,
            StartDate = "2099-01-01",
            DaysOfWeekMask = 127
        };
        Assert.Empty(HueSceneAutomationService.GetUpcomingOccurrences(
            futureSchedule,
            TimeZoneInfo.ConvertTimeFromUtc(beforeStartUtc, TimeZoneInfo.Local),
            maxOccurrences: 3,
            horizonDays: 31,
            includeFutureStartBeyondHorizon: false));
        Assert.NotEmpty(HueSceneAutomationService.GetUpcomingOccurrences(
            futureSchedule,
            TimeZoneInfo.ConvertTimeFromUtc(beforeStartUtc, TimeZoneInfo.Local),
            maxOccurrences: 1,
            horizonDays: 31));
    }

    [Fact]
    public void GetEffectiveDurationSeconds_UsesCueOverrideAndFallsBackToSavedScene()
    {
        var preset = new HueColorPreset { Name = "Evening", DurationSeconds = 6 };

        Assert.Equal(
            12,
            HueSceneAutomationService.GetEffectiveDurationSeconds(
                new HueSceneSchedule { DurationSeconds = 12 },
                preset));
        Assert.Equal(
            6,
            HueSceneAutomationService.GetEffectiveDurationSeconds(
                new HueSceneSchedule { DurationSeconds = 0 },
                preset));
    }

    [Fact]
    public void GetEffectiveTransitionSeconds_ClampsToEffectiveCueDuration()
    {
        var preset = new HueColorPreset
        {
            Name = "Evening",
            DurationSeconds = 8,
            TransitionSeconds = 6
        };

        Assert.Equal(
            6,
            HueSceneAutomationService.GetEffectiveTransitionSeconds(
                new HueSceneSchedule { DurationSeconds = 0 },
                preset));
        Assert.Equal(
            3,
            HueSceneAutomationService.GetEffectiveTransitionSeconds(
                new HueSceneSchedule { DurationSeconds = 3 },
                preset));
    }

    [Fact]
    public void GetEffectiveTransitionOutSeconds_ClampsToRemainingCueDuration()
    {
        var preset = new HueColorPreset
        {
            Name = "Evening",
            DurationSeconds = 8,
            TransitionSeconds = 3,
            TransitionOutSeconds = 5
        };

        Assert.Equal(
            5,
            HueSceneAutomationService.GetEffectiveTransitionOutSeconds(
                new HueSceneSchedule { DurationSeconds = 0 },
                preset));
        Assert.Equal(
            2,
            HueSceneAutomationService.GetEffectiveTransitionOutSeconds(
                new HueSceneSchedule { DurationSeconds = 5 },
                preset));
    }

    [Fact]
    public void MonthlyCue_ClampsDayThirtyOneToShortMonths()
    {
        var schedule = new HueSceneSchedule
        {
            Enabled = true,
            TimeOfDay = "07:05",
            TimeZoneId = TimeZoneInfo.Utc.Id,
            Recurrence = PluginConfiguration.SceneScheduleRecurrenceMonthly,
            DayOfMonth = 31,
            DaysOfWeekMask = 0
        };
        var beforeJanuaryCueUtc = new DateTime(2026, 1, 30, 7, 0, 0, DateTimeKind.Utc);
        var occurrences = HueSceneAutomationService.GetUpcomingOccurrences(
            schedule,
            TimeZoneInfo.ConvertTimeFromUtc(beforeJanuaryCueUtc, TimeZoneInfo.Local),
            maxOccurrences: 3,
            horizonDays: 75);

        Assert.Equal(
            new[]
            {
                new DateTime(2026, 1, 31, 7, 5, 0, DateTimeKind.Utc),
                new DateTime(2026, 2, 28, 7, 5, 0, DateTimeKind.Utc),
                new DateTime(2026, 3, 31, 7, 5, 0, DateTimeKind.Utc)
            },
            occurrences.Select(occurrence => occurrence.UtcTime));
        Assert.Equal(
            new DateTime(2026, 1, 31, 7, 5, 0, DateTimeKind.Utc),
            HueSceneAutomationService.GetNextRunUtc(
                schedule,
                TimeZoneInfo.ConvertTimeFromUtc(
                    new DateTime(2026, 1, 1, 7, 0, 0, DateTimeKind.Utc),
                    TimeZoneInfo.Local)));
        Assert.True(HueSceneAutomationService.IsDue(
            schedule,
            TimeZoneInfo.ConvertTimeFromUtc(
                new DateTime(2026, 2, 28, 7, 5, 30, DateTimeKind.Utc),
                TimeZoneInfo.Local)));
        Assert.False(HueSceneAutomationService.IsDue(
            schedule,
            TimeZoneInfo.ConvertTimeFromUtc(
                new DateTime(2026, 2, 27, 7, 5, 30, DateTimeKind.Utc),
                TimeZoneInfo.Local)));
    }

    [Fact]
    public void OneTimeCue_RunsOnlyOnConfiguredDateAndIgnoresWeekdayMask()
    {
        var schedule = new HueSceneSchedule
        {
            Enabled = true,
            TimeOfDay = "07:05",
            TimeZoneId = TimeZoneInfo.Utc.Id,
            RunDate = "2026-08-18",
            DaysOfWeekMask = 0
        };

        var beforeRunUtc = new DateTime(2026, 8, 17, 7, 0, 0, DateTimeKind.Utc);
        var dueUtc = new DateTime(2026, 8, 18, 7, 5, 30, DateTimeKind.Utc);
        var afterRunUtc = new DateTime(2026, 8, 19, 7, 0, 0, DateTimeKind.Utc);

        var occurrences = HueSceneAutomationService.GetUpcomingOccurrences(
            schedule,
            TimeZoneInfo.ConvertTimeFromUtc(beforeRunUtc, TimeZoneInfo.Local),
            maxOccurrences: 5,
            horizonDays: 31);

        var occurrence = Assert.Single(occurrences);
        Assert.Equal(new DateTime(2026, 8, 18, 7, 5, 0), occurrence.LocalTime);
        Assert.Equal(new DateTime(2026, 8, 18, 7, 5, 0, DateTimeKind.Utc), occurrence.UtcTime);
        Assert.False(HueSceneAutomationService.IsDue(
            schedule,
            TimeZoneInfo.ConvertTimeFromUtc(new DateTime(2026, 8, 17, 7, 5, 30, DateTimeKind.Utc), TimeZoneInfo.Local)));
        Assert.True(HueSceneAutomationService.IsDue(
            schedule,
            TimeZoneInfo.ConvertTimeFromUtc(dueUtc, TimeZoneInfo.Local)));
        Assert.False(HueSceneAutomationService.IsDue(
            schedule,
            TimeZoneInfo.ConvertTimeFromUtc(afterRunUtc, TimeZoneInfo.Local)));
        Assert.Null(HueSceneAutomationService.GetNextRunUtc(
            schedule,
            TimeZoneInfo.ConvertTimeFromUtc(afterRunUtc, TimeZoneInfo.Local)));
    }

    [Fact]
    public async Task OneTimeCue_BackgroundRuntimePreservesDateThroughScheduleClone()
    {
        var configuration = new PluginConfiguration
        {
            SceneAutomationEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "one-time-app-secret",
            HueClientKey = "one-time-client-secret",
            EntertainmentAreaId = "area-1",
            PersistSceneScheduleHistory = true,
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Evening", Red = 10, Green = 20, Blue = 30, BrightnessPercent = 80, DurationSeconds = 1 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "one-time-runtime",
                    Name = "One-time runtime cue",
                    PresetName = "Evening",
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    RunDate = "2026-08-18",
                    DurationSeconds = 7,
                    Red = 101,
                    Green = 102,
                    Blue = 103,
                    DaysOfWeekMask = 0
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new Mock<IHueStreamTester>();
        streamTester
            .Setup(tester => tester.PreviewAsync(
                "192.168.1.100",
                "one-time-app-secret",
                "one-time-client-secret",
                "area-1",
                It.IsAny<JsonElement>(),
                null,
                101,
                102,
                103,
                80,
                7,
                It.IsAny<CancellationToken>(),
                0,
                0,
                PluginConfiguration.ColorPresetEffectSolid,
                PluginConfiguration.DefaultColorPresetEffectSpeedPercent))
            .ReturnsAsync(new HueStreamProbeResult { Succeeded = true, Message = "Displayed one-time scene." });
        var service = new HueSceneAutomationService(
            streamTester.Object,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        var dueUtc = new DateTime(2026, 8, 18, 7, 5, 30, DateTimeKind.Utc);
        await service.RunDueSchedulesAsync(
            TimeZoneInfo.ConvertTimeFromUtc(dueUtc, TimeZoneInfo.Local),
            CancellationToken.None);

        streamTester.VerifyAll();
        var runtime = Assert.Single(service.GetStatus().Schedules);
        Assert.Equal("2026-08-18", runtime.RunDate);
        Assert.False(runtime.Enabled);
        Assert.Equal(7, runtime.DurationSeconds);
        Assert.Equal(101, runtime.Red);
        Assert.Equal(102, runtime.Green);
        Assert.Equal(103, runtime.Blue);
        Assert.Equal(1, runtime.RunCount);
        Assert.True(runtime.LastSucceeded);
        var history = Assert.Single(service.GetHistory());
        Assert.Equal(101, history.Red);
        Assert.Equal(102, history.Green);
        Assert.Equal(103, history.Blue);
        await service.RunDueSchedulesAsync(
            TimeZoneInfo.ConvertTimeFromUtc(dueUtc, TimeZoneInfo.Local),
            CancellationToken.None);
        Assert.Single(streamTester.Invocations);
    }

    [Fact]
    public async Task RunDueSchedules_HigherPriorityCueRunsFirstAndEqualPriorityKeepsSavedOrder()
    {
        var configuration = new PluginConfiguration
        {
            SceneAutomationEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "priority-app-secret",
            HueClientKey = "priority-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Low", Red = 10, Green = 20, Blue = 30, BrightnessPercent = 80, DurationSeconds = 1 },
                new() { Name = "High", Red = 200, Green = 120, Blue = 60, BrightnessPercent = 80, DurationSeconds = 1 },
                new() { Name = "Equal", Red = 90, Green = 80, Blue = 70, BrightnessPercent = 80, DurationSeconds = 1 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "low-priority",
                    Name = "Low priority",
                    PresetName = "Low",
                    Priority = 10,
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0
                },
                new()
                {
                    Id = "high-priority",
                    Name = "High priority",
                    PresetName = "High",
                    Priority = 90,
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0
                },
                new()
                {
                    Id = "equal-priority",
                    Name = "Equal priority",
                    PresetName = "Equal",
                    Priority = 10,
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new RecordingStreamTester();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        await service.RunDueSchedulesAsync(
            new DateTime(2026, 8, 18, 7, 5, 30, DateTimeKind.Utc),
            CancellationToken.None);

        Assert.Equal(new[] { 200, 10, 90 }, streamTester.Reds);
        Assert.All(configuration.SceneSchedules, schedule => Assert.Equal(1, schedule.RunCount));
    }

    [Fact]
    public async Task RunDueSchedules_RecoversLaterCueAfterLongCueWithoutConfiguredCatchUp()
    {
        var configuration = new PluginConfiguration
        {
            SceneAutomationEnabled = true,
            SceneAutomationCatchUpMinutes = PluginConfiguration.MinSceneAutomationCatchUpMinutes,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "overlap-recovery-app-secret",
            HueClientKey = "overlap-recovery-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "First overlap scene", Red = 10, Green = 20, Blue = 30, DurationSeconds = 1 },
                new() { Name = "Recovered overlap scene", Red = 200, Green = 180, Blue = 160, DurationSeconds = 1 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "overlap-first-cue",
                    Name = "First overlap cue",
                    PresetName = "First overlap scene",
                    Priority = 100,
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0
                },
                new()
                {
                    Id = "overlap-recovered-cue",
                    Name = "Recovered overlap cue",
                    PresetName = "Recovered overlap scene",
                    Priority = 10,
                    TimeOfDay = "07:06",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    RunDate = "2026-08-18",
                    DaysOfWeekMask = 0
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new OverlapRecoveryStreamTester();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        var schedulerRun = service.RunDueSchedulesAsync(
            new DateTime(2026, 8, 18, 7, 5, 59, DateTimeKind.Utc),
            CancellationToken.None);
        await streamTester.FirstPreviewStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        streamTester.ReleaseFirstPreview.TrySetResult(true);
        await schedulerRun.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { 10, 200 }, streamTester.Reds);
        Assert.Equal(1, Assert.Single(configuration.SceneSchedules, schedule => schedule.Id == "overlap-first-cue").RunCount);
        var recoveredSchedule = Assert.Single(configuration.SceneSchedules, schedule => schedule.Id == "overlap-recovered-cue");
        Assert.Equal(1, recoveredSchedule.RunCount);
        Assert.False(recoveredSchedule.Enabled);

        var recoveredStatus = Assert.Single(
            service.GetStatus().Schedules,
            schedule => schedule.ScheduleId == "overlap-recovered-cue");
        Assert.True(recoveredStatus.LastSucceeded);

        await service.RunDueSchedulesAsync(
            new DateTime(2026, 8, 18, 7, 6, 2, DateTimeKind.Utc),
            CancellationToken.None);
        Assert.Equal(new[] { 10, 200 }, streamTester.Reds);
    }

    [Fact]
    public async Task RunPlaylistPreview_RunsSavedScenesInOrderAndAggregatesTargetOutcome()
    {
        var configuration = new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "playlist-app-secret",
            HueClientKey = "playlist-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Warm", Red = 25, Green = 50, Blue = 75, BrightnessPercent = 80, DurationSeconds = 1 },
                new() { Name = "Cool", Red = 220, Green = 180, Blue = 140, BrightnessPercent = 70, DurationSeconds = 2 }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "playlist-1",
                    Name = "Evening sequence",
                    PresetNames = new List<string> { "Warm", "Cool" }
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new RecordingStreamTester();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        var result = await service.RunPlaylistPreviewAsync(
            configuration.ScenePlaylists[0],
            CancellationToken.None,
            Array.Empty<string>());

        Assert.True(result.Succeeded);
        Assert.Equal("playlist-1", result.PlaylistId);
        Assert.Equal("Evening sequence", result.PlaylistName);
        Assert.Equal(PluginConfiguration.ScenePlaylistOrderSequential, result.PlaybackOrder);
        Assert.Equal(new[] { 25, 220 }, streamTester.Reds);
        Assert.Equal(new[] { 25, 220 }, result.Steps.Select(step => step.Red));
        Assert.Equal(new[] { 50, 180 }, result.Steps.Select(step => step.Green));
        Assert.Equal(new[] { 75, 140 }, result.Steps.Select(step => step.Blue));
        Assert.Equal(new[] { "Warm", "Cool" }, result.Steps.Select(step => step.PresetName));
        Assert.Equal(new[] { 1, 2 }, result.Steps.Select(step => step.OriginalIndex));
        Assert.All(result.Steps, step => Assert.True(step.Succeeded));
        var target = Assert.Single(result.TargetResults);
        Assert.Equal("Default bridge target", target.TargetLabel);
        Assert.True(target.Succeeded);
        Assert.Equal(2, target.CompletedStepCount);
        Assert.Equal(2, target.TotalStepCount);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("playlist-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("playlist-client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunPlaylistPreview_UsesPerStepDurationOverridesAndReportsEffectiveTiming()
    {
        var configuration = new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "playlist-duration-app-secret",
            HueClientKey = "playlist-duration-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Warm", Red = 25, Green = 50, Blue = 75, DurationSeconds = 12, TransitionSeconds = 2, TransitionOutSeconds = 2, TransitionCurve = PluginConfiguration.ColorPresetTransitionCurveEaseIn },
                new() { Name = "Cool", Red = 220, Green = 180, Blue = 140, DurationSeconds = 8, EffectSpeedPercent = 275 }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "playlist-duration",
                    Name = "Timed sequence",
                    PresetNames = new List<string> { "Warm", "Cool" },
                    StepDurationSeconds = new List<int> { 3, 0 },
                    StepRed = new List<int?> { 200, null },
                    StepGreen = new List<int?> { 100, null },
                    StepBlue = new List<int?> { 50, null },
                    StepTransitionSeconds = new List<int?> { 1, null },
                    StepTransitionOutSeconds = new List<int?> { null, 1 },
                    StepTransitionCurves = new List<string?> { PluginConfiguration.ColorPresetTransitionCurveEaseInOut, null },
                    StepEffectSpeedPercent = new List<int?> { 225, null }
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new RecordingStreamTester();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        var result = await service.RunPlaylistPreviewAsync(configuration.ScenePlaylists[0]);

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { 3, 8 }, streamTester.Durations);
        Assert.Equal(new[] { 200, 220 }, streamTester.Reds);
        Assert.Equal(new[] { 200, 220 }, result.Steps.Select(step => step.Red));
        Assert.Equal(new[] { 100, 180 }, result.Steps.Select(step => step.Green));
        Assert.Equal(new[] { 50, 140 }, result.Steps.Select(step => step.Blue));
        Assert.Equal(new[] { 1, 0 }, streamTester.TransitionSeconds);
        Assert.Equal(new[] { 2, 1 }, streamTester.TransitionOutSeconds);
        Assert.Equal(new[] { "EaseInOut", "Linear" }, streamTester.TransitionCurves);
        Assert.Equal(new[] { 225, 275 }, streamTester.EffectSpeeds);
        Assert.Equal(new[] { 225, 275 }, result.Steps.Select(step => step.EffectSpeedPercent));
        Assert.Equal(new[] { 3, 8 }, result.Steps.Select(step => step.DurationSeconds));
        Assert.Equal(new[] { 1, 0 }, result.Steps.Select(step => step.TransitionSeconds));
        Assert.Equal(new[] { 2, 1 }, result.Steps.Select(step => step.TransitionOutSeconds));
        Assert.Equal(new[] { 0, 3 }, result.Steps.Select(step => step.StartOffsetSeconds));
        Assert.Equal(0, result.Steps[1].TransitionSeconds);
        Assert.Equal(11, HueSceneAutomationService.GetPlaylistTotalDurationSeconds(
            configuration,
            configuration.ScenePlaylists[0]));
    }

    [Fact]
    public async Task RunPlaylistPreview_ContinuousCapabilityRunsOncePerTargetWithFullEffectivePlan()
    {
        var configuration = new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "continuous-app-secret",
            HueClientKey = "continuous-client-secret",
            EntertainmentAreaId = "default-area",
            ColorPresets = new List<HueColorPreset>
            {
                new()
                {
                    Name = "Warm",
                    Effect = PluginConfiguration.ColorPresetEffectPulse,
                    EffectSpeedPercent = 150,
                    Red = 11,
                    Green = 22,
                    Blue = 33,
                    BrightnessPercent = 80,
                    DurationSeconds = 12,
                    TransitionSeconds = 4,
                    TransitionOutSeconds = 3,
                    TransitionCurve = PluginConfiguration.ColorPresetTransitionCurveEaseIn
                },
                new()
                {
                    Name = "Cool",
                    Effect = PluginConfiguration.ColorPresetEffectRainbow,
                    EffectSpeedPercent = 275,
                    Red = 210,
                    Green = 180,
                    Blue = 140,
                    BrightnessPercent = 60,
                    DurationSeconds = 8,
                    TransitionSeconds = 2,
                    TransitionOutSeconds = 2,
                    TransitionCurve = PluginConfiguration.ColorPresetTransitionCurveLinear
                }
            },
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
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "continuous-plan",
                    Name = "Continuous plan",
                    PresetNames = new List<string> { "Warm", "Cool" },
                    StepDurationSeconds = new List<int> { 3, 0 },
                    StepRed = new List<int?> { 101, null },
                    StepGreen = new List<int?> { 102, null },
                    StepBlue = new List<int?> { 103, null },
                    StepBrightnessPercent = new List<int?> { 25, null },
                    StepEffects = new List<string?> { PluginConfiguration.ColorPresetEffectLightning, null },
                    StepEffectSpeedPercent = new List<int?> { 175, null },
                    StepTransitionSeconds = new List<int?> { 1, null },
                    StepTransitionOutSeconds = new List<int?> { null, 1 },
                    StepTransitionCurves = new List<string?>
                    {
                        PluginConfiguration.ColorPresetTransitionCurveEaseInOut,
                        null
                    },
                    RepeatCount = 2,
                    PlaybackOrder = PluginConfiguration.ScenePlaylistOrderSequential,
                    TargetUserIds = new List<string> { "user-1" },
                    IncludeDefaultTarget = true
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new ContinuousPlaylistStreamTester();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        var result = await service.RunPlaylistPreviewAsync(
            configuration.ScenePlaylists[0],
            runAtUtcOverride: new DateTime(2026, 8, 23, 20, 0, 0, DateTimeKind.Utc));

        Assert.True(result.Succeeded);
        Assert.Equal(0, streamTester.LegacyPreviewCallCount);
        Assert.Empty(streamTester.TargetScopedPlaylistInvocations);
        Assert.Equal(2, streamTester.PlaylistInvocations.Count);
        Assert.Equal(
            new[] { "192.168.1.100", "192.168.1.101" },
            streamTester.PlaylistInvocations.Select(invocation => invocation.BridgeIp));
        Assert.All(streamTester.PlaylistInvocations, invocation =>
        {
            Assert.Equal(new[] { 1, 2, 3, 4 }, invocation.Steps.Select(step => step.Index));
            Assert.Equal(new[] { 101, 210, 101, 210 }, invocation.Steps.Select(step => step.Red));
            Assert.Equal(new[] { 102, 180, 102, 180 }, invocation.Steps.Select(step => step.Green));
            Assert.Equal(new[] { 103, 140, 103, 140 }, invocation.Steps.Select(step => step.Blue));
            Assert.Equal(new[] { 25, 60, 25, 60 }, invocation.Steps.Select(step => step.BrightnessPercent));
            Assert.Equal(new[] { 3, 8, 3, 8 }, invocation.Steps.Select(step => step.DurationSeconds));
            Assert.Equal(new[] { 1, 2, 1, 2 }, invocation.Steps.Select(step => step.TransitionSeconds));
            Assert.Equal(new[] { 2, 1, 2, 1 }, invocation.Steps.Select(step => step.TransitionOutSeconds));
            Assert.Equal(
                new[] { "Lightning", "Rainbow", "Lightning", "Rainbow" },
                invocation.Steps.Select(step => step.Effect));
            Assert.Equal(new[] { 175, 275, 175, 275 }, invocation.Steps.Select(step => step.EffectSpeedPercent));
            Assert.Equal(
                new[] { "EaseInOut", "Linear", "EaseInOut", "Linear" },
                invocation.Steps.Select(step => step.TransitionCurve));
        });
        Assert.Equal(new[] { "Warm", "Cool", "Warm", "Cool" }, result.Steps.Select(step => step.PresetName));
        Assert.Equal(new[] { 1, 2, 1, 2 }, result.Steps.Select(step => step.OriginalIndex));
        Assert.Equal(new[] { 1, 1, 2, 2 }, result.Steps.Select(step => step.RepeatIndex));
        Assert.Equal(new[] { 101, 210, 101, 210 }, result.Steps.Select(step => step.Red));
        Assert.Equal(new[] { 102, 180, 102, 180 }, result.Steps.Select(step => step.Green));
        Assert.Equal(new[] { 103, 140, 103, 140 }, result.Steps.Select(step => step.Blue));
        Assert.Equal(new[] { 0, 3, 11, 14 }, result.Steps.Select(step => step.StartOffsetSeconds));
        Assert.Equal(new[] { "Lightning", "Rainbow", "Lightning", "Rainbow" }, result.Steps.Select(step => step.Effect));
        Assert.Equal(2, result.TargetResults.Count);
        Assert.All(result.TargetResults, target =>
        {
            Assert.True(target.Succeeded);
            Assert.Equal(4, target.CompletedStepCount);
            Assert.Equal(4, target.TotalStepCount);
        });
    }

    [Fact]
    public async Task RunPlaylistPreview_ContinuousCapabilitySelectsTargetScopedEntryPoint()
    {
        var configuration = CreateContinuousPlaylistConfiguration("target-scoped-playlist", "Target-scoped playlist", 2);
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new ContinuousPlaylistStreamTester();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        var result = await service.RunPlaylistPreviewAsync(
            configuration.ScenePlaylists[0],
            targetScopedPlayback: true);

        Assert.True(result.Succeeded);
        Assert.Equal(0, streamTester.LegacyPreviewCallCount);
        Assert.Empty(streamTester.PlaylistInvocations);
        var invocation = Assert.Single(streamTester.TargetScopedPlaylistInvocations);
        Assert.Equal("192.168.1.100", invocation.BridgeIp);
        Assert.Equal("area-1", invocation.AreaId);
        Assert.Equal(new[] { 1, 2 }, invocation.Steps.Select(step => step.Index));
    }

    [Fact]
    public async Task RunPlaylistPreview_ContinuousPartialFailurePreservesStepAndCleanupTelemetry()
    {
        var configuration = CreateContinuousPlaylistConfiguration("partial-playlist", "Partial playlist", 3);
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new ContinuousPlaylistStreamTester();
        streamTester.EnqueueResult(new HuePlaylistStreamProbeResult
        {
            Succeeded = false,
            Message = "Completed 1 of 3 playlist steps before the continuous stream failed.",
            CleanupWarning = "The bridge restore was incomplete.",
            Steps = new HuePlaylistPreviewStepResult[]
            {
                new() { Index = 1, Succeeded = true, Message = "Displayed playlist step 1." },
                new() { Index = 2, Succeeded = false, Message = "The playlist frame could not be sent." }
            }
        });
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        var result = await service.RunPlaylistPreviewAsync(configuration.ScenePlaylists[0]);

        Assert.False(result.Succeeded);
        Assert.Equal(0, streamTester.LegacyPreviewCallCount);
        Assert.Single(streamTester.PlaylistInvocations);
        Assert.Equal(3, result.Steps.Count);
        Assert.True(result.Steps[0].Succeeded);
        Assert.False(result.Steps[1].Succeeded);
        Assert.Contains("frame could not be sent", result.Steps[1].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restore was incomplete", result.Steps[1].CleanupWarning, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.Steps[2].Succeeded);
        Assert.Contains("ended before this step", result.Steps[2].Message, StringComparison.OrdinalIgnoreCase);
        var target = Assert.Single(result.TargetResults);
        Assert.False(target.Succeeded);
        Assert.Equal(2, target.CompletedStepCount);
        Assert.Equal(3, target.TotalStepCount);
        Assert.Equal("The bridge restore was incomplete.", target.CleanupWarning);
        Assert.Contains("Completed 1 of 3", target.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunPlaylistPreview_ContinuousCancellationStopsBeforeTheNextTargetAndPreservesTelemetry()
    {
        var configuration = CreateContinuousPlaylistConfiguration("canceled-playlist", "Canceled playlist", 2);
        configuration.UserMappings = new List<UserBridgeMapping>
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
        };
        configuration.ScenePlaylists[0].TargetUserIds = new List<string> { "user-1" };
        configuration.ScenePlaylists[0].IncludeDefaultTarget = true;
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new ContinuousPlaylistStreamTester();
        streamTester.EnqueueResult(new HuePlaylistStreamProbeResult
        {
            Succeeded = false,
            Message = "The continuous playlist preview was canceled after 0 of 2 step(s); the bridge is being restored.",
            CleanupWarning = "The canceled target was restored with warnings.",
            Steps = new HuePlaylistPreviewStepResult[]
            {
                new() { Index = 1, Succeeded = false, Message = "The playlist step was canceled." }
            }
        });
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        var result = await service.RunPlaylistPreviewAsync(configuration.ScenePlaylists[0]);

        Assert.False(result.Succeeded);
        Assert.Equal(0, streamTester.LegacyPreviewCallCount);
        var invocation = Assert.Single(streamTester.PlaylistInvocations);
        Assert.Equal("192.168.1.100", invocation.BridgeIp);
        Assert.Equal(2, result.TargetResults.Count);
        Assert.Equal(1, result.TargetResults[0].CompletedStepCount);
        Assert.Contains("canceled after 0 of 2", result.TargetResults[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("The canceled target was restored with warnings.", result.TargetResults[0].CleanupWarning);
        Assert.Equal(0, result.TargetResults[1].CompletedStepCount);
        Assert.Contains("canceled before this target started", result.TargetResults[1].Message, StringComparison.OrdinalIgnoreCase);
        Assert.All(result.Steps, step => Assert.False(step.Succeeded));
        Assert.Contains("playlist step was canceled", result.Steps[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ended before this step", result.Steps[1].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPlaylistScheduleSteps_ExpandsEffectiveOverridesAndOffsets()
    {
        var configuration = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset>
            {
                new()
                {
                    Name = "Warm",
                    BrightnessPercent = 80,
                    DurationSeconds = 12,
                    TransitionSeconds = 4,
                    TransitionOutSeconds = 3
                },
                new()
                {
                    Name = "Cool",
                    BrightnessPercent = 60,
                    EffectSpeedPercent = 275,
                    DurationSeconds = 8,
                    TransitionSeconds = 2,
                    TransitionOutSeconds = 2
                }
            }
        };
        var playlist = new HueScenePlaylist
        {
            Id = "scheduled-plan",
            Name = "Scheduled plan",
            PresetNames = new List<string> { "Warm", "Cool" },
            StepDurationSeconds = new List<int> { 3, 0 },
            StepBrightnessPercent = new List<int?> { 25, null },
            StepEffectSpeedPercent = new List<int?> { 175, null },
            StepTransitionSeconds = new List<int?> { 1, null },
            StepTransitionOutSeconds = new List<int?> { null, 1 },
            RepeatCount = 2,
            PlaybackOrder = PluginConfiguration.ScenePlaylistOrderSequential
        };

        var steps = HueSceneAutomationService.BuildPlaylistScheduleSteps(
            configuration,
            playlist,
            new DateTime(2026, 8, 23, 20, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new[] { "Warm", "Cool", "Warm", "Cool" }, steps.Select(step => step.PresetName));
        Assert.Equal(new[] { 25, 60, 25, 60 }, steps.Select(step => step.BrightnessPercent));
        Assert.Equal(new[] { 175, 275, 175, 275 }, steps.Select(step => step.EffectSpeedPercent));
        Assert.Equal(new[] { 3, 8, 3, 8 }, steps.Select(step => step.DurationSeconds));
        Assert.Equal(new[] { 0, 3, 11, 14 }, steps.Select(step => step.StartOffsetSeconds));
        Assert.Equal(new[] { 1, 1, 2, 2 }, steps.Select(step => step.RepeatIndex));
        Assert.Equal(new[] { 1, 2, 1, 2 }, steps.Select(step => step.OriginalIndex));
        Assert.Equal(new[] { 1, 2, 1, 2 }, steps.Select(step => step.TransitionSeconds));
        Assert.Equal(new[] { 2, 1, 2, 1 }, steps.Select(step => step.TransitionOutSeconds));
        playlist.StepTransitionCurves = new List<string?> { "EaseInOut", null };
        var curveSteps = HueSceneAutomationService.BuildPlaylistScheduleSteps(
            configuration,
            playlist,
            new DateTime(2026, 8, 23, 20, 0, 0));
        Assert.Equal(new[] { "EaseInOut", "Linear", "EaseInOut", "Linear" }, curveSteps.Select(step => step.TransitionCurve));

        playlist.PlaybackOrder = PluginConfiguration.ScenePlaylistOrderShuffle;
        var shuffled = HueSceneAutomationService.BuildPlaylistScheduleSteps(
            configuration,
            playlist,
            new DateTime(2026, 8, 23, 20, 0, 0, DateTimeKind.Utc));
        var shuffledRetry = HueSceneAutomationService.BuildPlaylistScheduleSteps(
            configuration,
            playlist,
            new DateTime(2026, 8, 23, 23, 0, 0, DateTimeKind.Utc));
        Assert.Equal(shuffled.Select(step => step.OriginalIndex), shuffledRetry.Select(step => step.OriginalIndex));
        Assert.Equal(new[] { 1, 2 }, shuffled.Take(2).Select(step => step.OriginalIndex).OrderBy(index => index));
        Assert.Equal(new[] { 1, 2 }, shuffled.Skip(2).Select(step => step.OriginalIndex).OrderBy(index => index));
    }

    [Fact]
    public void BuildPlaylistPass_ShuffleIsStablePerPlaylistDateAndPass()
    {
        var presets = new[]
        {
            new HueColorPreset { Name = "One" },
            new HueColorPreset { Name = "Two" },
            new HueColorPreset { Name = "Three" },
            new HueColorPreset { Name = "Four" }
        };
        var runAtUtc = new DateTime(2026, 8, 23, 14, 30, 0, DateTimeKind.Utc);

        var first = HueSceneAutomationService.BuildPlaylistPass(
            presets,
            PluginConfiguration.ScenePlaylistOrderShuffle,
            "stable-playlist",
            1,
            runAtUtc);
        var retry = HueSceneAutomationService.BuildPlaylistPass(
            presets,
            "shuffle",
            "stable-playlist",
            1,
            runAtUtc.AddHours(4));

        Assert.Equal(first.Select(step => step.OriginalIndex), retry.Select(step => step.OriginalIndex));
        Assert.Equal(new[] { 1, 2, 3, 4 }, first.Select(step => step.OriginalIndex).OrderBy(index => index));
        Assert.False(first.Select(step => step.OriginalIndex).SequenceEqual(new[] { 1, 2, 3, 4 }));

        var nextPass = HueSceneAutomationService.BuildPlaylistPass(
            presets,
            PluginConfiguration.ScenePlaylistOrderShuffle,
            "stable-playlist",
            2,
            runAtUtc);
        Assert.Equal(new[] { 1, 2, 3, 4 }, nextPass.Select(step => step.OriginalIndex).OrderBy(index => index));
    }

    [Fact]
    public async Task RunPlaylistPreview_RepeatsTheSequenceAndReportsPassTelemetry()
    {
        var configuration = new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "repeat-app-secret",
            HueClientKey = "repeat-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Warm", Red = 25, Green = 50, Blue = 75, BrightnessPercent = 80, DurationSeconds = 1 },
                new() { Name = "Cool", Red = 220, Green = 180, Blue = 140, BrightnessPercent = 70, DurationSeconds = 2 }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "playlist-repeat",
                    Name = "Repeated sequence",
                    PresetNames = new List<string> { "Warm", "Cool" },
                    RepeatCount = 2
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new RecordingStreamTester();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        var result = await service.RunPlaylistPreviewAsync(configuration.ScenePlaylists[0]);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.RepeatCount);
        Assert.Equal(PluginConfiguration.ScenePlaylistOrderSequential, result.PlaybackOrder);
        Assert.Equal(new[] { 25, 220, 25, 220 }, streamTester.Reds);
        Assert.Equal(new[] { 1, 1, 2, 2 }, result.Steps.Select(step => step.RepeatIndex));
        Assert.Equal(new[] { "Warm", "Cool", "Warm", "Cool" }, result.Steps.Select(step => step.PresetName));
        var target = Assert.Single(result.TargetResults);
        Assert.Equal(4, target.CompletedStepCount);
        Assert.Equal(4, target.TotalStepCount);
        Assert.Contains("2 passes", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunScheduleAsync_RunsScheduledPlaylistInOrderAndReturnsPlaylistTelemetry()
    {
        var configuration = new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "scheduled-playlist-app-secret",
            HueClientKey = "scheduled-playlist-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "First", Red = 11, Green = 20, Blue = 30, BrightnessPercent = 80, DurationSeconds = 1 },
                new() { Name = "Second", Red = 222, Green = 180, Blue = 140, BrightnessPercent = 70, DurationSeconds = 2, EffectSpeedPercent = 260 }
            },
            PersistSceneScheduleHistory = true,
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "scheduled-playlist",
                    Name = "Scheduled sequence",
                    PresetNames = new List<string> { "First", "Second" },
                    StepBrightnessPercent = new List<int?> { 30, null },
                    StepEffectSpeedPercent = new List<int?> { 180, null },
                    StepTransitionSeconds = new List<int?> { 1, null },
                    StepTransitionOutSeconds = new List<int?> { null, 1 },
                    StepTransitionCurves = new List<string?> { "EaseInOut", null },
                    RepeatCount = 2
                }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "scheduled-playlist-cue",
                    Name = "Scheduled playlist cue",
                    PlaylistName = "Scheduled sequence",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new RecordingStreamTester();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        var result = await service.RunScheduleAsync("scheduled-playlist-cue");

        Assert.True(result.Succeeded);
        Assert.Equal(PluginConfiguration.SceneScheduleEffectPlaylist, result.Effect);
        Assert.Equal("Scheduled sequence", result.PlaylistName);
        Assert.Equal(2, result.PlaylistRepeatCount);
        Assert.Equal(PluginConfiguration.ScenePlaylistOrderSequential, result.PlaylistPlaybackOrder);
        Assert.Equal(new[] { "First", "Second", "First", "Second" }, result.PlaylistSteps.Select(step => step.PresetName));
        Assert.Equal(new[] { 30, 70, 30, 70 }, result.PlaylistSteps.Select(step => step.BrightnessPercent));
        Assert.Equal(new[] { 180, 260, 180, 260 }, result.PlaylistSteps.Select(step => step.EffectSpeedPercent));
        Assert.Equal(new[] { 0, 1, 3, 4 }, result.PlaylistSteps.Select(step => step.StartOffsetSeconds));
        Assert.Equal(new[] { 1, 0, 1, 0 }, result.PlaylistSteps.Select(step => step.TransitionSeconds));
        Assert.Equal(new[] { 0, 1, 0, 1 }, result.PlaylistSteps.Select(step => step.TransitionOutSeconds));
        Assert.Equal(new[] { "EaseInOut", "Linear", "EaseInOut", "Linear" }, result.PlaylistSteps.Select(step => step.TransitionCurve));
        Assert.Equal(new[] { 11, 222, 11, 222 }, streamTester.Reds);
        Assert.Equal(new[] { 30, 70, 30, 70 }, streamTester.Brightnesses);
        Assert.Equal(new[] { 180, 260, 180, 260 }, streamTester.EffectSpeeds);
        Assert.Equal(new[] { 1, 0, 1, 0 }, streamTester.TransitionSeconds);
        Assert.Equal(new[] { 0, 1, 0, 1 }, streamTester.TransitionOutSeconds);
        Assert.Equal(1, configuration.SceneSchedules[0].RunCount);
        var history = Assert.Single(service.GetHistory());
        Assert.Equal(new[] { 30, 70, 30, 70 }, history.PlaylistSteps.Select(step => step.BrightnessPercent));
        Assert.Equal(new[] { 180, 260, 180, 260 }, history.PlaylistSteps.Select(step => step.EffectSpeedPercent));
        Assert.Equal(new[] { 0, 1, 3, 4 }, history.PlaylistSteps.Select(step => step.StartOffsetSeconds));
        Assert.Equal(new[] { 1, 0, 1, 0 }, history.PlaylistSteps.Select(step => step.TransitionSeconds));
        Assert.Equal(new[] { "EaseInOut", "Linear", "EaseInOut", "Linear" }, history.PlaylistSteps.Select(step => step.TransitionCurve));
        var persistedHistory = Assert.Single(configuration.PersistedSceneScheduleHistory);
        Assert.Equal(new[] { 180, 260, 180, 260 }, persistedHistory.PlaylistSteps.Select(step => step.EffectSpeedPercent));
        var runtime = Assert.Single(service.GetStatus().Schedules);
        Assert.Equal("Scheduled sequence", runtime.PlaylistName);
        Assert.Equal(2, runtime.PlaylistStepCount);
        Assert.Equal(2, runtime.PlaylistRepeatCount);
        Assert.Equal(PluginConfiguration.ScenePlaylistOrderSequential, runtime.PlaylistPlaybackOrder);
        Assert.Equal(6, runtime.PlaylistTotalDurationSeconds);
    }

    [Fact]
    public async Task RunScheduleAsync_ContinuousPlaylistCloneRetainsStepEffects()
    {
        var configuration = new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "scheduled-effects-app-secret",
            HueClientKey = "scheduled-effects-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new()
                {
                    Name = "First",
                    Effect = PluginConfiguration.ColorPresetEffectPulse,
                    Red = 11,
                    Green = 20,
                    Blue = 30,
                    DurationSeconds = 1
                },
                new()
                {
                    Name = "Second",
                    Effect = PluginConfiguration.ColorPresetEffectRainbow,
                    Red = 222,
                    Green = 180,
                    Blue = 140,
                    DurationSeconds = 1
                }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "scheduled-effects-playlist",
                    Name = "Scheduled effects",
                    PresetNames = new List<string> { "First", "Second" },
                    StepEffects = new List<string?>
                    {
                        PluginConfiguration.ColorPresetEffectLightning,
                        null
                    }
                }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "scheduled-effects-cue",
                    Name = "Scheduled effects cue",
                    PlaylistName = "Scheduled effects",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new ContinuousPlaylistStreamTester();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        var result = await service.RunScheduleAsync("scheduled-effects-cue");

        Assert.True(result.Succeeded);
        Assert.Equal(0, streamTester.LegacyPreviewCallCount);
        var invocation = Assert.Single(streamTester.PlaylistInvocations);
        Assert.Equal(new[] { "Lightning", "Rainbow" }, invocation.Steps.Select(step => step.Effect));
        Assert.Equal(new[] { "Lightning", "Rainbow" }, result.PlaylistSteps.Select(step => step.Effect));
        Assert.Equal(
            new string?[] { PluginConfiguration.ColorPresetEffectLightning, null },
            configuration.ScenePlaylists[0].StepEffects);
    }

    [Fact]
    public async Task RunDueSchedules_UsesOccurrenceDateForScheduledPlaylistShuffle()
    {
        var configuration = new PluginConfiguration
        {
            SceneAutomationEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "scheduled-shuffle-app-secret",
            HueClientKey = "scheduled-shuffle-client-secret",
            EntertainmentAreaId = "area-1",
            PersistSceneScheduleHistory = true,
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "One", Red = 10, Green = 20, Blue = 30, DurationSeconds = 1 },
                new() { Name = "Two", Red = 40, Green = 50, Blue = 60, DurationSeconds = 1 },
                new() { Name = "Three", Red = 70, Green = 80, Blue = 90, DurationSeconds = 1 },
                new() { Name = "Four", Red = 100, Green = 110, Blue = 120, DurationSeconds = 1 }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "scheduled-shuffle",
                    Name = "Scheduled shuffle",
                    PresetNames = new List<string> { "One", "Two", "Three", "Four" },
                    PlaybackOrder = PluginConfiguration.ScenePlaylistOrderShuffle
                }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "scheduled-shuffle-cue",
                    Name = "Scheduled shuffle cue",
                    PlaylistName = "Scheduled shuffle",
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0
                }
            }
        };
        InstallConfiguration(configuration);

        var playlist = configuration.ScenePlaylists[0];
        var today = DateTime.UtcNow.Date;
        var todayOrder = HueSceneAutomationService.BuildPlaylistScheduleSteps(
                configuration,
                playlist,
                today)
            .Select(step => step.OriginalIndex)
            .ToArray();
        var occurrenceDate = Enumerable.Range(1, 366)
            .Select(daysAgo => today.AddDays(-daysAgo))
            .First(date => !HueSceneAutomationService.BuildPlaylistScheduleSteps(
                    configuration,
                    playlist,
                    date)
                .Select(step => step.OriginalIndex)
                .SequenceEqual(todayOrder));
        var occurrenceUtc = occurrenceDate.AddHours(7).AddMinutes(5);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new RecordingStreamTester();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        await service.RunDueSchedulesAsync(
            occurrenceUtc.AddSeconds(30),
            CancellationToken.None);

        var history = Assert.Single(service.GetHistory());
        var expected = HueSceneAutomationService.BuildPlaylistScheduleSteps(
                configuration,
                playlist,
                occurrenceUtc)
            .Select(step => step.OriginalIndex);
        Assert.Equal(expected, history.PlaylistSteps.Select(step => step.OriginalIndex));
        Assert.Equal(1, configuration.SceneSchedules[0].RunCount);
    }

    [Fact]
    public async Task RunDueSchedules_DisablesFiniteCueAtPersistedExecutionLimit()
    {
        var configuration = new PluginConfiguration
        {
            SceneAutomationEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "finite-app-secret",
            HueClientKey = "finite-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Finite scene", Red = 10, Green = 20, Blue = 30, BrightnessPercent = 80, DurationSeconds = 1 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "finite-runtime",
                    Name = "Finite runtime cue",
                    PresetName = "Finite scene",
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    MaxRuns = 1,
                    DaysOfWeekMask = 0
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new Mock<IHueStreamTester>();
        streamTester
            .Setup(tester => tester.PreviewAsync(
                "192.168.1.100",
                "finite-app-secret",
                "finite-client-secret",
                "area-1",
                It.IsAny<JsonElement>(),
                null,
                10,
                20,
                30,
                80,
                1,
                It.IsAny<CancellationToken>(),
                0,
                0,
                PluginConfiguration.ColorPresetEffectSolid,
                PluginConfiguration.DefaultColorPresetEffectSpeedPercent))
            .ReturnsAsync(new HueStreamProbeResult { Succeeded = true, Message = "Displayed finite scene." });
        var service = new HueSceneAutomationService(
            streamTester.Object,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        var dueUtc = new DateTime(2026, 8, 18, 7, 5, 30, DateTimeKind.Utc);
        await service.RunDueSchedulesAsync(dueUtc, CancellationToken.None);
        await service.RunDueSchedulesAsync(dueUtc, CancellationToken.None);

        streamTester.Verify(tester => tester.PreviewAsync(
            "192.168.1.100",
            "finite-app-secret",
            "finite-client-secret",
            "area-1",
            It.IsAny<JsonElement>(),
            null,
            10,
            20,
            30,
            80,
            1,
            It.IsAny<CancellationToken>(),
            0,
            0,
            PluginConfiguration.ColorPresetEffectSolid,
            PluginConfiguration.DefaultColorPresetEffectSpeedPercent), Times.Once);
        var saved = Assert.Single(configuration.SceneSchedules);
        Assert.Equal(1, saved.RunCount);
        Assert.False(saved.Enabled);
        var runtime = Assert.Single(service.GetStatus().Schedules);
        Assert.Equal(1, runtime.MaxRuns);
        Assert.Equal(1, runtime.RunCount);
        Assert.Equal(0, runtime.RemainingRuns);
    }

    [Fact]
    public void GetMostRecentMissedOccurrence_UsesWindowAndReturnsNewestOccurrenceOnly()
    {
        var schedule = new HueSceneSchedule
        {
            Id = "catch-up-preview",
            Enabled = true,
            TimeOfDay = "07:05",
            TimeZoneId = TimeZoneInfo.Utc.Id,
            Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
            DaysOfWeekMask = 0
        };
        var now = new DateTime(2026, 8, 18, 7, 20, 0, DateTimeKind.Utc);

        var recent = HueSceneAutomationService.GetMostRecentMissedOccurrence(schedule, now, 20);
        Assert.NotNull(recent);
        Assert.Equal(new DateTime(2026, 8, 18, 7, 5, 0, DateTimeKind.Utc), recent!.UtcTime);
        Assert.Null(HueSceneAutomationService.GetMostRecentMissedOccurrence(schedule, now, 10));
    }

    [Fact]
    public async Task RunDueSchedules_RecoversRecentMissedCueAndMarksTelemetry()
    {
        var configuration = new PluginConfiguration
        {
            SceneAutomationEnabled = true,
            SceneAutomationCatchUpMinutes = 10,
            PersistSceneScheduleHistory = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "catch-up-app-secret",
            HueClientKey = "catch-up-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Catch-up scene", Red = 10, Green = 20, Blue = 30, BrightnessPercent = 80, DurationSeconds = 1 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "catch-up-runtime-cue",
                    Name = "Catch-up runtime cue",
                    PresetName = "Catch-up scene",
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    Enabled = true
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new Mock<IHueStreamTester>();
        streamTester
            .Setup(tester => tester.PreviewAsync(
                "192.168.1.100",
                "catch-up-app-secret",
                "catch-up-client-secret",
                "area-1",
                It.IsAny<JsonElement>(),
                null,
                10,
                20,
                30,
                80,
                1,
                It.IsAny<CancellationToken>(),
                0,
                0,
                PluginConfiguration.ColorPresetEffectSolid,
                PluginConfiguration.DefaultColorPresetEffectSpeedPercent))
            .ReturnsAsync(new HueStreamProbeResult { Succeeded = true, Message = "Recovered scene." });
        var service = new HueSceneAutomationService(
            streamTester.Object,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        var afterMissedCueUtc = new DateTime(2026, 8, 18, 7, 8, 30, DateTimeKind.Utc);
        await service.RunDueSchedulesAsync(afterMissedCueUtc, CancellationToken.None);
        await service.RunDueSchedulesAsync(afterMissedCueUtc, CancellationToken.None);

        streamTester.VerifyAll();
        var saved = Assert.Single(configuration.SceneSchedules);
        Assert.Equal(1, saved.RunCount);
        Assert.Equal(10, service.GetStatus().CatchUpMinutes);
        var status = Assert.Single(service.GetStatus().Schedules);
        Assert.True(status.LastWasCatchUp);
        Assert.True(status.LastSucceeded);
        var history = Assert.Single(service.GetHistory());
        Assert.True(history.WasCatchUp);
        Assert.True(history.Succeeded);
        Assert.Single(service.GetHistory(outcome: "Recovered"));
        Assert.Single(service.GetHistory(outcome: "Succeeded"));
        Assert.Empty(service.GetHistory(outcome: "Failed"));
    }

    [Fact]
    public async Task RunDueSchedules_DoesNotRecoverCueOutsideConfiguredWindow()
    {
        var configuration = new PluginConfiguration
        {
            SceneAutomationEnabled = true,
            SceneAutomationCatchUpMinutes = 10,
            ColorPresets = new List<HueColorPreset> { new() { Name = "No catch-up scene" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "outside-catch-up-window",
                    Name = "Outside catch-up window",
                    PresetName = "No catch-up scene",
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    Enabled = true
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new Mock<IHueStreamTester>();
        var service = new HueSceneAutomationService(
            streamTester.Object,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        await service.RunDueSchedulesAsync(
            new DateTime(2026, 8, 18, 7, 20, 0, DateTimeKind.Utc),
            CancellationToken.None);

        streamTester.VerifyNoOtherCalls();
        Assert.Equal(0, Assert.Single(configuration.SceneSchedules).RunCount);
        Assert.Empty(service.GetHistory());
    }

    [Fact]
    public async Task RunDueSchedules_DeferPolicyQueuesCueUntilPlaybackEndsWithoutConsumingRun()
    {
        var configuration = new PluginConfiguration
        {
            SceneAutomationEnabled = true,
            SceneAutomationPlaybackPolicy = PluginConfiguration.SceneAutomationPlaybackPolicyDefer,
            SceneAutomationDeferMinutes = 10,
            PersistSceneScheduleHistory = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "defer-app-secret",
            HueClientKey = "defer-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Deferred scene", Red = 10, Green = 20, Blue = 30, BrightnessPercent = 80, DurationSeconds = 1 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "deferred-cue",
                    Name = "Deferred cue",
                    PresetName = "Deferred scene",
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    Enabled = true
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new RecordingStreamTester();
        var lifecycleGate = new HueBridgeLifecycleGate();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>(),
            lifecycleGate);
        var dueUtc = new DateTime(2026, 8, 18, 7, 5, 30, DateTimeKind.Utc);

        using (var playbackLease = lifecycleGate.TryEnterPlayback("deferred-target"))
        {
            Assert.NotNull(playbackLease);
            await service.RunDueSchedulesAsync(dueUtc, CancellationToken.None);
            Assert.Empty(streamTester.Reds);
            var pendingStatus = service.GetStatus();
            Assert.Equal(PluginConfiguration.SceneAutomationPlaybackPolicyDefer, pendingStatus.PlaybackPolicy);
            Assert.Equal(10, pendingStatus.DeferMinutes);
            Assert.True(pendingStatus.PlaybackActive);
            var pending = Assert.Single(pendingStatus.Schedules);
            Assert.True(pending.DeferredPending);
            Assert.False(pending.DeferredRestored);
            Assert.Equal(0, pending.RunCount);
            Assert.Contains("Waiting for active playback", pending.LastMessage, StringComparison.Ordinal);
            var persistedDeferred = Assert.Single(configuration.PersistedSceneAutomationDeferredRuns);
            Assert.Equal("deferred-cue", persistedDeferred.ScheduleId);
            Assert.Equal(dueUtc.AddSeconds(-30), persistedDeferred.OccurrenceSlot);
        }

        await service.RunDueSchedulesAsync(dueUtc.AddMinutes(1), CancellationToken.None);

        Assert.Equal(new[] { 10 }, streamTester.Reds);
        Assert.Equal(1, configuration.SceneSchedules[0].RunCount);
        var status = Assert.Single(service.GetStatus().Schedules);
        Assert.False(status.DeferredPending);
        Assert.True(status.LastWasDeferred);
        Assert.True(status.LastSucceeded);
        var history = Assert.Single(service.GetHistory());
        Assert.True(history.WasDeferred);
        Assert.False(history.WasDeferredRestored);
        Assert.True(history.Succeeded);
        Assert.Single(service.GetHistory(outcome: "Deferred"));
        Assert.Empty(service.GetHistory(outcome: "Failed"));
        Assert.Empty(configuration.PersistedSceneAutomationDeferredRuns);
    }

    [Fact]
    public async Task RunDueSchedules_PerCueDeferOverrideQueuesWhenGlobalPolicySkips()
    {
        var configuration = new PluginConfiguration
        {
            SceneAutomationEnabled = true,
            SceneAutomationPlaybackPolicy = PluginConfiguration.SceneAutomationPlaybackPolicySkip,
            SceneAutomationDeferMinutes = 10,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "per-cue-defer-app-secret",
            HueClientKey = "per-cue-defer-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Per-cue deferred scene", Red = 70, Green = 80, Blue = 90, BrightnessPercent = 80, DurationSeconds = 1 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "per-cue-defer",
                    Name = "Per-cue defer",
                    PresetName = "Per-cue deferred scene",
                    PlaybackPolicy = PluginConfiguration.SceneAutomationPlaybackPolicyDefer,
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    Enabled = true
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new RecordingStreamTester();
        var lifecycleGate = new HueBridgeLifecycleGate();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>(),
            lifecycleGate);
        var dueUtc = new DateTime(2026, 8, 18, 7, 5, 30, DateTimeKind.Utc);

        using (var playbackLease = lifecycleGate.TryEnterPlayback("per-cue-defer-target"))
        {
            Assert.NotNull(playbackLease);
            await service.RunDueSchedulesAsync(dueUtc, CancellationToken.None);
            Assert.Empty(streamTester.Reds);
            var status = Assert.Single(service.GetStatus().Schedules);
            Assert.Equal(PluginConfiguration.SceneAutomationPlaybackPolicyDefer, status.PlaybackPolicy);
            Assert.Equal(PluginConfiguration.SceneAutomationPlaybackPolicyDefer, status.PlaybackPolicyOverride);
            Assert.True(status.DeferredPending);
        }

        await service.RunDueSchedulesAsync(dueUtc.AddMinutes(1), CancellationToken.None);

        Assert.Equal(new[] { 70 }, streamTester.Reds);
        Assert.Empty(configuration.PersistedSceneAutomationDeferredRuns);
    }

    [Fact]
    public async Task RunDueSchedules_PerCueSkipOverrideRunsWhenGlobalPolicyDefers()
    {
        var configuration = new PluginConfiguration
        {
            SceneAutomationEnabled = true,
            SceneAutomationPlaybackPolicy = PluginConfiguration.SceneAutomationPlaybackPolicyDefer,
            SceneAutomationDeferMinutes = 10,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "per-cue-skip-app-secret",
            HueClientKey = "per-cue-skip-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Per-cue skipped scene", Red = 170, Green = 180, Blue = 190, BrightnessPercent = 80, DurationSeconds = 1 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "per-cue-skip",
                    Name = "Per-cue skip",
                    PresetName = "Per-cue skipped scene",
                    PlaybackPolicy = PluginConfiguration.SceneAutomationPlaybackPolicySkip,
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    Enabled = true
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new RecordingStreamTester();
        var lifecycleGate = new HueBridgeLifecycleGate();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>(),
            lifecycleGate);
        var dueUtc = new DateTime(2026, 8, 18, 7, 5, 30, DateTimeKind.Utc);

        using var playbackLease = lifecycleGate.TryEnterPlayback("per-cue-skip-target");
        Assert.NotNull(playbackLease);
        await service.RunDueSchedulesAsync(dueUtc, CancellationToken.None);

        Assert.Equal(new[] { 170 }, streamTester.Reds);
        Assert.Empty(configuration.PersistedSceneAutomationDeferredRuns);
        var status = Assert.Single(service.GetStatus().Schedules);
        Assert.Equal(PluginConfiguration.SceneAutomationPlaybackPolicySkip, status.PlaybackPolicy);
        Assert.Equal(PluginConfiguration.SceneAutomationPlaybackPolicySkip, status.PlaybackPolicyOverride);
        Assert.False(status.DeferredPending);
    }

    [Fact]
    public async Task RunDueSchedules_MatchingTargetScopeAllowsIndependentRoomDuringPlayback()
    {
        var configuration = new PluginConfiguration
        {
            SceneAutomationEnabled = true,
            SceneAutomationPlaybackPolicy = PluginConfiguration.SceneAutomationPlaybackPolicyDefer,
            SceneAutomationPlaybackScope = PluginConfiguration.SceneAutomationPlaybackScopeMatchingTarget,
            SceneAutomationDeferMinutes = 10,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "matching-scope-app-secret",
            HueClientKey = "matching-scope-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Matching-scope scene", Red = 40, Green = 50, Blue = 60, BrightnessPercent = 80, DurationSeconds = 1 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "matching-scope-cue",
                    Name = "Matching-scope cue",
                    PresetName = "Matching-scope scene",
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    Enabled = true
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new RecordingStreamTester();
        var lifecycleGate = new HueBridgeLifecycleGate();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>(),
            lifecycleGate);
        var dueUtc = new DateTime(2026, 8, 18, 7, 5, 30, DateTimeKind.Utc);

        using (var otherRoomPlayback = lifecycleGate.TryEnterPlayback(
                   HueSyncService.GetPlaybackResourceKey("192.168.1.200", "area-2")))
        {
            Assert.NotNull(otherRoomPlayback);
            await service.RunDueSchedulesAsync(dueUtc, CancellationToken.None);
        }

        Assert.Equal(new[] { 40 }, streamTester.Reds);
        Assert.Equal(1, configuration.SceneSchedules[0].RunCount);
        Assert.Equal(
            PluginConfiguration.SceneAutomationPlaybackScopeMatchingTarget,
            service.GetStatus().PlaybackConflictScope);
        Assert.Empty(configuration.PersistedSceneAutomationDeferredRuns);
    }

    [Fact]
    public async Task RunDueSchedules_MatchingTargetScopeDefersWhenCueTargetIsPlaying()
    {
        var configuration = new PluginConfiguration
        {
            SceneAutomationEnabled = true,
            SceneAutomationPlaybackPolicy = PluginConfiguration.SceneAutomationPlaybackPolicyDefer,
            SceneAutomationPlaybackScope = PluginConfiguration.SceneAutomationPlaybackScopeMatchingTarget,
            SceneAutomationDeferMinutes = 10,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "matching-target-app-secret",
            HueClientKey = "matching-target-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Matching-target scene", Red = 70, Green = 80, Blue = 90, BrightnessPercent = 80, DurationSeconds = 1 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "matching-target-cue",
                    Name = "Matching-target cue",
                    PresetName = "Matching-target scene",
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    Enabled = true
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new RecordingStreamTester();
        var lifecycleGate = new HueBridgeLifecycleGate();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>(),
            lifecycleGate);
        var dueUtc = new DateTime(2026, 8, 18, 7, 5, 30, DateTimeKind.Utc);

        using var matchingPlayback = lifecycleGate.TryEnterPlayback(
            HueSyncService.GetPlaybackResourceKey("192.168.1.100", "area-1"));
        Assert.NotNull(matchingPlayback);
        await service.RunDueSchedulesAsync(dueUtc, CancellationToken.None);

        Assert.Empty(streamTester.Reds);
        Assert.Equal(0, configuration.SceneSchedules[0].RunCount);
        Assert.True(Assert.Single(service.GetStatus().Schedules).DeferredPending);
        Assert.Single(configuration.PersistedSceneAutomationDeferredRuns);
    }

    [Fact]
    public async Task RunDueSchedules_RestoresPersistedDeferredCueAfterRestart()
    {
        var dueUtc = new DateTime(2026, 8, 18, 7, 5, 0, DateTimeKind.Utc);
        var configuration = new PluginConfiguration
        {
            SceneAutomationEnabled = true,
            SceneAutomationPlaybackPolicy = PluginConfiguration.SceneAutomationPlaybackPolicyDefer,
            SceneAutomationDeferMinutes = 10,
            PersistSceneScheduleHistory = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "restored-app-secret",
            HueClientKey = "restored-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Restored scene", Red = 40, Green = 50, Blue = 60, BrightnessPercent = 70, DurationSeconds = 1 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "restored-deferred-cue",
                    Name = "Restored deferred cue",
                    PresetName = "Restored scene",
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    Enabled = true
                }
            },
            PersistedSceneAutomationDeferredRuns = new List<HueSceneDeferredRunEntry>
            {
                new()
                {
                    ScheduleId = "restored-deferred-cue",
                    OccurrenceSlot = dueUtc,
                    DeferredAtLocal = dueUtc.AddSeconds(30)
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new RecordingStreamTester();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        var restoredStatus = Assert.Single(service.GetStatus().Schedules);
        Assert.True(restoredStatus.DeferredPending);
        Assert.True(restoredStatus.DeferredRestored);
        Assert.Contains("Restored after scheduler restart", restoredStatus.LastMessage, StringComparison.Ordinal);

        await service.RunDueSchedulesAsync(dueUtc.AddMinutes(1), CancellationToken.None);

        Assert.Equal(new[] { 40 }, streamTester.Reds);
        Assert.Empty(configuration.PersistedSceneAutomationDeferredRuns);
        var status = Assert.Single(service.GetStatus().Schedules);
        Assert.False(status.DeferredPending);
        Assert.True(status.LastWasDeferred);
        Assert.True(status.LastWasDeferredRestored);
        var history = Assert.Single(service.GetHistory());
        Assert.True(history.WasDeferred);
        Assert.True(history.WasDeferredRestored);
    }

    [Fact]
    public async Task RunDueSchedules_DeferredCueExpiresAsSkippedWhenPlaybackStaysActive()
    {
        var configuration = new PluginConfiguration
        {
            SceneAutomationEnabled = true,
            SceneAutomationPlaybackPolicy = PluginConfiguration.SceneAutomationPlaybackPolicyDefer,
            SceneAutomationDeferMinutes = 1,
            PersistSceneScheduleHistory = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "defer-expiry-app-secret",
            HueClientKey = "defer-expiry-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Expiring scene", DurationSeconds = 1 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "expiring-cue",
                    Name = "Expiring cue",
                    PresetName = "Expiring scene",
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    Enabled = true
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new RecordingStreamTester();
        var lifecycleGate = new HueBridgeLifecycleGate();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>(),
            lifecycleGate);
        var dueUtc = new DateTime(2026, 8, 18, 7, 5, 30, DateTimeKind.Utc);

        using var playbackLease = lifecycleGate.TryEnterPlayback("expiry-target");
        Assert.NotNull(playbackLease);
        await service.RunDueSchedulesAsync(dueUtc, CancellationToken.None);
        await service.RunDueSchedulesAsync(dueUtc.AddMinutes(2), CancellationToken.None);

        Assert.Empty(streamTester.Reds);
        var history = Assert.Single(service.GetHistory());
        Assert.True(history.Skipped);
        Assert.True(history.WasDeferred);
        Assert.Contains("defer window", history.Message, StringComparison.OrdinalIgnoreCase);
        var status = Assert.Single(service.GetStatus().Schedules);
        Assert.False(status.DeferredPending);
        Assert.True(status.LastSkipped);
        Assert.Equal(0, status.RunCount);
    }

    [Fact]
    public async Task RunDueSchedules_RecoversPendingSkipWithoutPlayingTheMissedCue()
    {
        var configuration = new PluginConfiguration
        {
            SceneAutomationEnabled = true,
            SceneAutomationCatchUpMinutes = 10,
            PersistSceneScheduleHistory = true,
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "catch-up-skip-cue",
                    Name = "Catch-up skip cue",
                    PresetName = "No scene required for skip",
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    SkipNextOccurrence = true,
                    Enabled = true
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new Mock<IHueStreamTester>();
        var service = new HueSceneAutomationService(
            streamTester.Object,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        await service.RunDueSchedulesAsync(
            new DateTime(2026, 8, 18, 7, 8, 30, DateTimeKind.Utc),
            CancellationToken.None);

        streamTester.VerifyNoOtherCalls();
        Assert.False(configuration.SceneSchedules[0].SkipNextOccurrence);
        var history = Assert.Single(service.GetHistory());
        Assert.True(history.Skipped);
        Assert.True(history.WasCatchUp);
        Assert.True(Assert.Single(service.GetStatus().Schedules).LastWasCatchUp);
    }

    [Fact]
    public void ResetScheduleRunCount_ClearsPersistedCounterAndReenablesCue()
    {
        var configuration = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Finite scene" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "resettable-cue",
                    Name = "Resettable cue",
                    PresetName = "Finite scene",
                    MaxRuns = 3,
                    RunCount = 3,
                    Enabled = false
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        Assert.True(service.TryResetScheduleRunCount(" resettable-cue ", out var message));
        Assert.Contains("reset", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, configuration.SceneSchedules[0].RunCount);
        Assert.True(configuration.SceneSchedules[0].Enabled);
        var runtime = Assert.Single(service.GetStatus().Schedules);
        Assert.Equal(0, runtime.RunCount);
        Assert.Equal(3, runtime.RemainingRuns);
        Assert.Null(runtime.LastSucceeded);
    }

    [Fact]
    public void ResetSchedulesRunCount_PersistenceFailureRestoresEverySelectedCue()
    {
        var serializer = new Mock<IXmlSerializer>();
        serializer
            .Setup(xml => xml.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("bulk reset persistence failure"));
        var configuration = new PluginConfiguration
        {
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "bulk-reset-service-one",
                    Name = "Bulk reset service one",
                    RunCount = 3,
                    Enabled = false,
                    SkipNextOccurrence = true
                },
                new()
                {
                    Id = "bulk-reset-service-two",
                    Name = "Bulk reset service two",
                    RunCount = 2,
                    Enabled = false,
                    SkipNextOccurrence = false
                }
            }
        };
        InstallConfiguration(configuration, serializer.Object);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        Assert.False(service.TryResetSchedulesRunCount(
            new[] { " bulk-reset-service-one ", "bulk-reset-service-two" },
            out var message));
        Assert.Contains("no changes", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, configuration.SceneSchedules[0].RunCount);
        Assert.False(configuration.SceneSchedules[0].Enabled);
        Assert.True(configuration.SceneSchedules[0].SkipNextOccurrence);
        Assert.Equal(2, configuration.SceneSchedules[1].RunCount);
        Assert.False(configuration.SceneSchedules[1].Enabled);
        Assert.False(configuration.SceneSchedules[1].SkipNextOccurrence);
    }

    [Fact]
    public void SetScheduleEnabled_TogglesCueWithoutChangingItsDefinition()
    {
        var configuration = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Toggle scene" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "toggle-cue",
                    Name = "Toggle cue",
                    PresetName = "Toggle scene",
                    TimeOfDay = "06:45",
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    StartDate = "2026-08-01",
                    DurationSeconds = 9,
                    MaxRuns = 4,
                    RunCount = 1,
                    Enabled = true
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        Assert.True(service.TrySetScheduleEnabled(" toggle-cue ", false, out var disableMessage));
        Assert.Contains("disabled", disableMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(configuration.SceneSchedules[0].Enabled);
        Assert.Equal("06:45", configuration.SceneSchedules[0].TimeOfDay);
        Assert.Equal(PluginConfiguration.SceneScheduleRecurrenceDaily, configuration.SceneSchedules[0].Recurrence);
        Assert.Equal(1, configuration.SceneSchedules[0].RunCount);

        Assert.True(service.TrySetScheduleEnabled("toggle-cue", true, out var enableMessage));
        Assert.Contains("enabled", enableMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(configuration.SceneSchedules[0].Enabled);
        Assert.Equal(9, configuration.SceneSchedules[0].DurationSeconds);
        Assert.Equal(4, configuration.SceneSchedules[0].MaxRuns);
    }

    [Fact]
    public void SetScheduleEnabled_RefusesEnablingAnExhaustedCue()
    {
        var configuration = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Exhausted scene" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "exhausted-toggle-cue",
                    Name = "Exhausted toggle cue",
                    PresetName = "Exhausted scene",
                    MaxRuns = 2,
                    RunCount = 2,
                    Enabled = false
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        Assert.False(service.TrySetScheduleEnabled("exhausted-toggle-cue", true, out var message));
        Assert.Contains("reset", message, StringComparison.OrdinalIgnoreCase);
        Assert.False(configuration.SceneSchedules[0].Enabled);
        Assert.Equal(2, configuration.SceneSchedules[0].RunCount);
    }

    [Fact]
    public void SetSchedulesEnabled_UpdatesSelectedCuesAtomically()
    {
        var configuration = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Bulk scene" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "bulk-one",
                    Name = "Bulk one",
                    PresetName = "Bulk scene",
                    TimeOfDay = "06:45",
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DurationSeconds = 9,
                    Enabled = true
                },
                new()
                {
                    Id = "bulk-two",
                    Name = "Bulk two",
                    PresetName = "Bulk scene",
                    TimeOfDay = "07:15",
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceWeekly,
                    DurationSeconds = 12,
                    Enabled = true
                },
                new()
                {
                    Id = "bulk-three",
                    Name = "Bulk three",
                    PresetName = "Bulk scene",
                    TimeOfDay = "08:15",
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceYearly,
                    DurationSeconds = 15,
                    Enabled = false
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        Assert.True(service.TrySetSchedulesEnabled(
            new[] { " bulk-one ", "bulk-two", "bulk-one" },
            false,
            out var message));
        Assert.Contains("2", message, StringComparison.Ordinal);
        Assert.False(configuration.SceneSchedules[0].Enabled);
        Assert.False(configuration.SceneSchedules[1].Enabled);
        Assert.False(configuration.SceneSchedules[2].Enabled);
        Assert.Equal("06:45", configuration.SceneSchedules[0].TimeOfDay);
        Assert.Equal(PluginConfiguration.SceneScheduleRecurrenceWeekly, configuration.SceneSchedules[1].Recurrence);
        Assert.Equal(15, configuration.SceneSchedules[2].DurationSeconds);
    }

    [Fact]
    public void SetSchedulesEnabled_RefusesEntireSelectionWhenOneCueIsExhausted()
    {
        var configuration = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Bulk guarded scene" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "bulk-ready",
                    Name = "Bulk ready",
                    PresetName = "Bulk guarded scene",
                    Enabled = false
                },
                new()
                {
                    Id = "bulk-exhausted",
                    Name = "Bulk exhausted",
                    PresetName = "Bulk guarded scene",
                    MaxRuns = 2,
                    RunCount = 2,
                    Enabled = false
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        Assert.False(service.TrySetSchedulesEnabled(
            new[] { "bulk-ready", "bulk-exhausted" },
            true,
            out var message));
        Assert.Contains("Bulk exhausted", message, StringComparison.Ordinal);
        Assert.Contains("reset", message, StringComparison.OrdinalIgnoreCase);
        Assert.False(configuration.SceneSchedules[0].Enabled);
        Assert.False(configuration.SceneSchedules[1].Enabled);
    }

    [Fact]
    public void SetSchedulesSkipNextOccurrence_UpdatesSelectedCuesAtomicallyAndCanClearThem()
    {
        var configuration = new PluginConfiguration
        {
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "bulk-skip-one",
                    Name = "Bulk skip one",
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    Enabled = true
                },
                new()
                {
                    Id = "bulk-skip-two",
                    Name = "Bulk skip two",
                    TimeOfDay = "08:15",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    Enabled = true
                },
                new()
                {
                    Id = "bulk-skip-untouched",
                    Name = "Bulk skip untouched",
                    TimeOfDay = "09:15",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    Enabled = true
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        Assert.True(service.TrySetSchedulesSkipNextOccurrence(
            new[] { " bulk-skip-one ", "bulk-skip-two", "bulk-skip-one" },
            true,
            out var skipMessage));
        Assert.Contains("2", skipMessage, StringComparison.Ordinal);
        Assert.True(configuration.SceneSchedules[0].SkipNextOccurrence);
        Assert.True(configuration.SceneSchedules[1].SkipNextOccurrence);
        Assert.False(configuration.SceneSchedules[2].SkipNextOccurrence);

        Assert.True(service.TrySetSchedulesSkipNextOccurrence(
            new[] { "bulk-skip-one", "bulk-skip-two" },
            false,
            out var clearMessage));
        Assert.Contains("cleared", clearMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(configuration.SceneSchedules[0].SkipNextOccurrence);
        Assert.False(configuration.SceneSchedules[1].SkipNextOccurrence);
    }

    [Fact]
    public void SetSchedulesSkipNextOccurrence_RefusesEntireSelectionWhenOneCueCannotBeSkipped()
    {
        var configuration = new PluginConfiguration
        {
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "bulk-skip-ready",
                    Name = "Bulk skip ready",
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    Enabled = true
                },
                new()
                {
                    Id = "bulk-skip-disabled",
                    Name = "Bulk skip disabled",
                    TimeOfDay = "08:15",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    Enabled = false
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        Assert.False(service.TrySetSchedulesSkipNextOccurrence(
            new[] { "bulk-skip-ready", "bulk-skip-disabled" },
            true,
            out var message));
        Assert.Contains("Bulk skip disabled", message, StringComparison.Ordinal);
        Assert.Contains("enabled", message, StringComparison.OrdinalIgnoreCase);
        Assert.False(configuration.SceneSchedules[0].SkipNextOccurrence);
        Assert.False(configuration.SceneSchedules[1].SkipNextOccurrence);
    }

    [Fact]
    public void DeleteSchedules_RemovesSelectedCuesAtomicallyAndPreservesOtherDefinitions()
    {
        var configuration = new PluginConfiguration
        {
            PersistSceneScheduleHistory = true,
            PersistedSceneScheduleHistory = new List<HueSceneScheduleHistoryEntry>
            {
                new() { ScheduleId = "bulk-delete-one", ScheduleName = "Bulk delete one" }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "bulk-delete-one", Name = "Bulk delete one", TimeOfDay = "06:45" },
                new() { Id = "bulk-delete-two", Name = "Bulk delete two", TimeOfDay = "07:15" },
                new() { Id = "bulk-delete-untouched", Name = "Bulk delete untouched", TimeOfDay = "08:15" }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        Assert.True(service.TryDeleteSchedules(
            new[] { " bulk-delete-one ", "bulk-delete-two", "bulk-delete-one" },
            out var message));
        Assert.Contains("Deleted 2", message, StringComparison.Ordinal);
        Assert.Single(configuration.SceneSchedules);
        Assert.Equal("bulk-delete-untouched", configuration.SceneSchedules[0].Id);
        Assert.Single(configuration.PersistedSceneScheduleHistory);
        Assert.Equal("bulk-delete-one", configuration.PersistedSceneScheduleHistory[0].ScheduleId);
    }

    [Fact]
    public void DeleteSchedules_RefusesEntireSelectionWhenOneCueIsMissing()
    {
        var configuration = new PluginConfiguration
        {
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "bulk-delete-existing", Name = "Bulk delete existing", TimeOfDay = "06:45" },
                new() { Id = "bulk-delete-safe", Name = "Bulk delete safe", TimeOfDay = "07:15" }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        Assert.False(service.TryDeleteSchedules(
            new[] { "bulk-delete-existing", "missing-delete-cue" },
            out var message));
        Assert.Contains("missing-delete-cue", message, StringComparison.Ordinal);
        Assert.Equal(2, configuration.SceneSchedules.Count);
        Assert.Equal("bulk-delete-existing", configuration.SceneSchedules[0].Id);
        Assert.Equal("bulk-delete-safe", configuration.SceneSchedules[1].Id);
    }

    [Fact]
    public void SetScheduleSkipNextOccurrence_ChangesOnlyPendingAutomaticOccurrence()
    {
        var configuration = new PluginConfiguration
        {
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "skip-next-cue",
                    Name = "Skip next cue",
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    Enabled = true
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        Assert.True(service.TrySetScheduleSkipNextOccurrence("skip-next-cue", true, out var skipMessage));
        Assert.Contains("skip", skipMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(configuration.SceneSchedules[0].SkipNextOccurrence);

        var skippedPreview = HueSceneAutomationService.GetUpcomingOccurrences(
            configuration.SceneSchedules[0],
            new DateTime(2026, 8, 17, 6, 0, 0, DateTimeKind.Utc),
            maxOccurrences: 2,
            horizonDays: 3);
        Assert.Equal(
            new[]
            {
                new DateTime(2026, 8, 18, 7, 5, 0),
                new DateTime(2026, 8, 19, 7, 5, 0)
            },
            skippedPreview.Select(occurrence => occurrence.LocalTime));

        Assert.True(service.TrySetScheduleSkipNextOccurrence("skip-next-cue", false, out var clearMessage));
        Assert.Contains("restored", clearMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(configuration.SceneSchedules[0].SkipNextOccurrence);
        var restoredPreview = HueSceneAutomationService.GetUpcomingOccurrences(
            configuration.SceneSchedules[0],
            new DateTime(2026, 8, 17, 6, 0, 0, DateTimeKind.Utc),
            maxOccurrences: 1,
            horizonDays: 1);
        Assert.Equal(new DateTime(2026, 8, 17, 7, 5, 0), Assert.Single(restoredPreview).LocalTime);
    }

    [Fact]
    public async Task RunDueSchedules_SkipsAutomaticOccurrenceAndKeepsFutureRecurrence()
    {
        var configuration = new PluginConfiguration
        {
            SceneAutomationEnabled = true,
            PersistSceneScheduleHistory = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "skip-app-secret",
            HueClientKey = "skip-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Skip scene", Red = 10, Green = 20, Blue = 30, BrightnessPercent = 80, DurationSeconds = 1 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "skip-runtime-cue",
                    Name = "Skip runtime cue",
                    PresetName = "Skip scene",
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    BrightnessPercent = 42,
                    SkipNextOccurrence = true,
                    Enabled = true
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new Mock<IHueStreamTester>();
        streamTester
            .Setup(tester => tester.PreviewAsync(
                "192.168.1.100",
                "skip-app-secret",
                "skip-client-secret",
                "area-1",
                It.IsAny<JsonElement>(),
                null,
                10,
                20,
                30,
                80,
                1,
                It.IsAny<CancellationToken>(),
                0,
                0,
                PluginConfiguration.ColorPresetEffectSolid,
                PluginConfiguration.DefaultColorPresetEffectSpeedPercent))
            .ReturnsAsync(new HueStreamProbeResult { Succeeded = true, Message = "Displayed future scene." });
        var service = new HueSceneAutomationService(
            streamTester.Object,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        var firstDueUtc = new DateTime(2026, 8, 18, 7, 5, 30, DateTimeKind.Utc);
        await service.RunDueSchedulesAsync(firstDueUtc, CancellationToken.None);

        streamTester.VerifyNoOtherCalls();
        Assert.False(configuration.SceneSchedules[0].SkipNextOccurrence);
        Assert.True(configuration.SceneSchedules[0].Enabled);
        Assert.Equal(0, configuration.SceneSchedules[0].RunCount);
        var skippedHistory = Assert.Single(service.GetHistory());
        Assert.True(skippedHistory.Skipped);
        Assert.False(skippedHistory.Succeeded);
        Assert.Equal(42, skippedHistory.BrightnessPercent);
        Assert.Equal(0, skippedHistory.RunCount);
        Assert.Single(service.GetHistory(outcome: "Skipped"));
        Assert.Empty(service.GetHistory(outcome: "Failed"));
        var skippedStatus = Assert.Single(service.GetStatus().Schedules);
        Assert.True(skippedStatus.LastSkipped);
        Assert.False(skippedStatus.LastSucceeded);

        await service.RunDueSchedulesAsync(
            new DateTime(2026, 8, 19, 7, 5, 30, DateTimeKind.Utc),
            CancellationToken.None);

        streamTester.Verify(tester => tester.PreviewAsync(
            "192.168.1.100",
            "skip-app-secret",
            "skip-client-secret",
            "area-1",
            It.IsAny<JsonElement>(),
            null,
            10,
            20,
            30,
            42,
            1,
            It.IsAny<CancellationToken>(),
            0,
            0,
            PluginConfiguration.ColorPresetEffectSolid,
            PluginConfiguration.DefaultColorPresetEffectSpeedPercent), Times.Once);
        Assert.Equal(1, configuration.SceneSchedules[0].RunCount);
        Assert.False(Assert.Single(service.GetStatus().Schedules).LastSkipped);
    }

    [Fact]
    public async Task RunDueSchedules_SkipsOneTimeCueAndDisablesIt()
    {
        var configuration = new PluginConfiguration
        {
            SceneAutomationEnabled = true,
            PersistSceneScheduleHistory = true,
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "skip-one-time-cue",
                    Name = "Skip one-time cue",
                    PresetName = "Missing scene is not needed for skip",
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    RunDate = "2026-08-18",
                    DaysOfWeekMask = 0,
                    SkipNextOccurrence = true,
                    Enabled = true
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        await service.RunDueSchedulesAsync(
            new DateTime(2026, 8, 18, 7, 5, 30, DateTimeKind.Utc),
            CancellationToken.None);

        var saved = Assert.Single(configuration.SceneSchedules);
        Assert.False(saved.Enabled);
        Assert.False(saved.SkipNextOccurrence);
        var history = Assert.Single(service.GetHistory());
        Assert.True(history.Skipped);
        Assert.Contains("one-time", history.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunDueSchedules_WhenSkipPersistenceFails_DoesNotRunCue()
    {
        var serializer = new Mock<IXmlSerializer>();
        serializer
            .Setup(xml => xml.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("simulated scheduler persistence failure"));
        var configuration = new PluginConfiguration
        {
            SceneAutomationEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "skip-failure-app-secret",
            HueClientKey = "skip-failure-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Skip failure scene", DurationSeconds = 1 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "skip-persistence-failure",
                    Name = "Skip persistence failure",
                    PresetName = "Skip failure scene",
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0,
                    SkipNextOccurrence = true,
                    Enabled = true
                }
            }
        };
        InstallConfiguration(configuration, serializer.Object);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new Mock<IHueStreamTester>();
        var service = new HueSceneAutomationService(
            streamTester.Object,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        await service.RunDueSchedulesAsync(
            new DateTime(2026, 8, 18, 7, 5, 30, DateTimeKind.Utc),
            CancellationToken.None);

        streamTester.VerifyNoOtherCalls();
        Assert.True(configuration.SceneSchedules[0].SkipNextOccurrence);
        Assert.True(configuration.SceneSchedules[0].Enabled);
        Assert.Empty(service.GetHistory());
    }

    [Fact]
    public async Task RunDueSchedules_WhenOneTimeCompletionPersistenceFails_RestoresEnabledState()
    {
        var serializer = new Mock<IXmlSerializer>();
        serializer
            .Setup(xml => xml.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("simulated one-time completion persistence failure"));
        var configuration = new PluginConfiguration
        {
            SceneAutomationEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "one-time-failure-app-secret",
            HueClientKey = "one-time-failure-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "One-time failure scene", DurationSeconds = 1 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "one-time-persistence-failure",
                    Name = "One-time persistence failure",
                    PresetName = "One-time failure scene",
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    RunDate = "2026-08-18",
                    DaysOfWeekMask = 0,
                    Enabled = true
                }
            }
        };
        InstallConfiguration(configuration, serializer.Object);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var service = new HueSceneAutomationService(
            new RecordingStreamTester(),
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        await service.RunDueSchedulesAsync(
            new DateTime(2026, 8, 18, 7, 5, 30, DateTimeKind.Utc),
            CancellationToken.None);

        Assert.True(configuration.SceneSchedules[0].Enabled);
        Assert.Equal(1, configuration.SceneSchedules[0].RunCount);
        Assert.True(Assert.Single(service.GetHistory()).Succeeded);
    }

    [Fact]
    public async Task ResetScheduleRunCount_RefusesActiveCue()
    {
        InstallConfiguration(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "reset-app-secret",
            HueClientKey = "reset-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset> { new() { Name = "Reset scene", DurationSeconds = 8 } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "active-reset-cue", Name = "Active reset cue", PresetName = "Reset scene", MaxRuns = 3 }
            }
        });

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new BlockingStreamTester();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var runTask = service.RunScheduleAsync("active-reset-cue");
        await streamTester.PreviewStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(service.TryResetScheduleRunCount("active-reset-cue", out var message));
        Assert.Contains("running", message, StringComparison.OrdinalIgnoreCase);
        Assert.False(service.TrySetScheduleEnabled("active-reset-cue", false, out var enabledMessage));
        Assert.Contains("running", enabledMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(service.TrySetScheduleSkipNextOccurrence("active-reset-cue", true, out var skipMessage));
        Assert.Contains("running", skipMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(service.CancelSchedule("active-reset-cue"));
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AutomaticCueRun_BlocksOverlappingManualRunForSameCue()
    {
        var configuration = new PluginConfiguration
        {
            SceneAutomationEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "overlap-app-secret",
            HueClientKey = "overlap-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Overlap scene", DurationSeconds = 8 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "overlap-cue",
                    Name = "Overlap cue",
                    PresetName = "Overlap scene",
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    DaysOfWeekMask = 0
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new BlockingStreamTester();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        using var cancellationSource = new CancellationTokenSource();
        var automaticTask = service.RunDueSchedulesAsync(
            new DateTime(2026, 8, 19, 7, 5, 30, DateTimeKind.Utc),
            cancellationSource.Token);
        await streamTester.PreviewStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var manualResult = await service.RunScheduleAsync("overlap-cue");

        Assert.False(manualResult.Succeeded);
        Assert.Contains("already running", manualResult.Message, StringComparison.OrdinalIgnoreCase);
        cancellationSource.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => automaticTask);
    }

    [Fact]
    public async Task ResetSchedulesRunCount_RefusesActiveCueWithoutPartialMutation()
    {
        var configuration = new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "bulk-reset-app-secret",
            HueClientKey = "bulk-reset-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset> { new() { Name = "Bulk reset active scene", DurationSeconds = 8 } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "bulk-reset-active-cue",
                    Name = "Bulk reset active cue",
                    PresetName = "Bulk reset active scene",
                    RunCount = 1,
                    Enabled = true
                },
                new()
                {
                    Id = "bulk-reset-idle-cue",
                    Name = "Bulk reset idle cue",
                    PresetName = "Bulk reset active scene",
                    RunCount = 3,
                    Enabled = false,
                    SkipNextOccurrence = true
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new BlockingStreamTester();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());
        var runTask = service.RunScheduleAsync("bulk-reset-active-cue");
        await streamTester.PreviewStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(service.TryResetSchedulesRunCount(
            new[] { "bulk-reset-active-cue", "bulk-reset-idle-cue" },
            out var message));
        Assert.Contains("running", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, configuration.SceneSchedules[0].RunCount);
        Assert.True(configuration.SceneSchedules[0].Enabled);
        Assert.Equal(3, configuration.SceneSchedules[1].RunCount);
        Assert.False(configuration.SceneSchedules[1].Enabled);
        Assert.True(configuration.SceneSchedules[1].SkipNextOccurrence);

        Assert.True(service.CancelSchedule("bulk-reset-active-cue"));
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
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
        var invalidExcluded = HueSceneAutomationService.EvaluateReadiness(
            config,
            new HueSceneSchedule
            {
                Enabled = true,
                PresetName = "Evening",
                ExcludedDates = new List<string> { "2026-02-30" }
            });
        var invalidRunDate = HueSceneAutomationService.EvaluateReadiness(
            config,
            new HueSceneSchedule { Enabled = true, PresetName = "Evening", RunDate = "2026-02-30", DaysOfWeekMask = 0 });
        var invalidDuration = HueSceneAutomationService.EvaluateReadiness(
            config,
            new HueSceneSchedule { Enabled = true, PresetName = "Evening", DurationSeconds = 31 });
        var invalidMonthly = HueSceneAutomationService.EvaluateReadiness(
            config,
            new HueSceneSchedule
            {
                Enabled = true,
                PresetName = "Evening",
                Recurrence = PluginConfiguration.SceneScheduleRecurrenceMonthly,
                DayOfMonth = 0
            });
        var invalidMonthlyWeekday = HueSceneAutomationService.EvaluateReadiness(
            config,
            new HueSceneSchedule
            {
                Enabled = true,
                PresetName = "Evening",
                Recurrence = PluginConfiguration.SceneScheduleRecurrenceMonthlyWeekday,
                WeekOfMonth = 0,
                DayOfWeek = -1
            });
        var invalidYearly = HueSceneAutomationService.EvaluateReadiness(
            config,
            new HueSceneSchedule
            {
                Enabled = true,
                PresetName = "Evening",
                Recurrence = PluginConfiguration.SceneScheduleRecurrenceYearly,
                MonthOfYear = 0,
                DayOfMonth = 31
            });
        var invalidInterval = HueSceneAutomationService.EvaluateReadiness(
            config,
            new HueSceneSchedule
            {
                Enabled = true,
                PresetName = "Evening",
                RecurrenceInterval = 2
            });

        Assert.False(invalidStart.Ready);
        Assert.Contains("start date", invalidStart.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(reversed.Ready);
        Assert.Contains("before", reversed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(invalidExcluded.Ready);
        Assert.Contains("excluded", invalidExcluded.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(invalidRunDate.Ready);
        Assert.Contains("run date", invalidRunDate.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(invalidDuration.Ready);
        Assert.Contains("duration", invalidDuration.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(invalidMonthly.Ready);
        Assert.Contains("monthly", invalidMonthly.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(invalidMonthlyWeekday.Ready);
        Assert.Contains("monthly-weekday", invalidMonthlyWeekday.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(invalidYearly.Ready);
        Assert.Contains("yearly", invalidYearly.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(invalidInterval.Ready);
        Assert.Contains("interval", invalidInterval.Message, StringComparison.OrdinalIgnoreCase);
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
    public void TryResolveTargets_BroadcastIncludesDistinctEnabledTargetsAndDeduplicatesInheritedMappings()
    {
        var config = new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "global-app-secret",
            HueClientKey = "global-client-secret",
            EntertainmentAreaId = "global-area",
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = "inherited", UserName = "Inherited", SyncEnabled = true },
                new()
                {
                    UserId = "user-1",
                    UserName = "Kitchen",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "mapping-app-secret",
                    HueClientKey = "mapping-client-secret",
                    EntertainmentAreaId = "mapping-area"
                },
                new()
                {
                    UserId = "disabled",
                    UserName = "Disabled",
                    SyncEnabled = false,
                    HueBridgeIp = "192.168.1.102",
                    HueAppKey = "disabled-app",
                    HueClientKey = "disabled-client",
                    EntertainmentAreaId = "disabled-area"
                }
            }
        };

        var schedule = new HueSceneSchedule { TargetAllEnabledMappings = true };

        Assert.True(HueSceneAutomationService.TryResolveTargets(config, schedule, out var targets, out var error));
        Assert.Empty(error);
        Assert.Equal(new[] { "Default bridge target", "Kitchen" }, targets.Select(target => target.TargetLabel));
        Assert.DoesNotContain(JsonSerializer.Serialize(schedule), "mapping-app-secret", StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolveTargets_SelectedTargetsIncludeOnlyRequestedMappingsAndOptionalDefault()
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
                    UserName = "Kitchen",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "mapping-app-secret",
                    HueClientKey = "mapping-client-secret",
                    EntertainmentAreaId = "mapping-area"
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
        };

        var selected = new HueSceneSchedule
        {
            TargetUserIds = new List<string> { "user-1" }
        };
        Assert.True(HueSceneAutomationService.TryResolveTargets(config, selected, out var selectedTargets, out var selectedError));
        Assert.Empty(selectedError);
        Assert.Equal(new[] { "Kitchen" }, selectedTargets.Select(target => target.TargetLabel));

        selected.IncludeDefaultTarget = true;
        Assert.True(HueSceneAutomationService.TryResolveTargets(config, selected, out var mixedTargets, out var mixedError));
        Assert.Empty(mixedError);
        Assert.Equal(new[] { "Default bridge target", "Kitchen" }, mixedTargets.Select(target => target.TargetLabel));
        Assert.DoesNotContain(mixedTargets, target => target.TargetLabel == "Office");

        var serialized = JsonSerializer.Serialize(selected);
        Assert.DoesNotContain("mapping-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("office-app-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolveTargets_ExplicitDeviceRouteUsesNestedCredentialsAndChannelProfile()
    {
        var config = new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "global-app-secret",
            HueClientKey = "global-client-secret",
            EntertainmentAreaId = "global-area",
            ChannelIds = "1,2",
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
                    ChannelIdsOverride = "3,4",
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new()
                        {
                            DeviceId = "device-bedroom",
                            DeviceName = "Bedroom TV",
                            HueBridgeIp = "192.168.1.102",
                            HueAppKey = "device-app-secret",
                            HueClientKey = "device-client-secret",
                            EntertainmentAreaId = "device-area",
                            ChannelIdsOverride = "7,8"
                        }
                    }
                }
            }
        };

        var schedule = new HueSceneSchedule();
        var routes = new[]
        {
            new HueSceneAutomationTargetRoute { UserId = " user-1 ", DeviceId = " device-bedroom " }
        };

        Assert.True(HueSceneAutomationService.TryResolveTargets(config, schedule, out var targets, out var error, routes));
        Assert.Empty(error);
        var target = Assert.Single(targets);
        Assert.Equal("Living Room / Bedroom TV", target.TargetLabel);
        Assert.Equal("192.168.1.102", target.BridgeIp);
        Assert.Equal("device-area", target.EntertainmentAreaId);
        Assert.Equal(new[] { 7, 8 }, target.ChannelIds!.OrderBy(id => id));

        Assert.False(HueSceneAutomationService.TryResolveTargets(
            config,
            schedule,
            out _,
            out var duplicateError,
            new[]
            {
                routes[0],
                new HueSceneAutomationTargetRoute { UserId = "user-1", DeviceId = "device-bedroom" }
            }));
        Assert.Contains("more than once", duplicateError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunPlaylistPreview_ReportsExplicitDeviceRoutesWithoutCredentials()
    {
        var configuration = new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "global-app-secret",
            HueClientKey = "global-client-secret",
            EntertainmentAreaId = "global-area",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Device scene", Red = 20, Green = 40, Blue = 60, DurationSeconds = 1 }
            },
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-device",
                    UserName = "Device room",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "mapping-app-secret",
                    HueClientKey = "mapping-client-secret",
                    EntertainmentAreaId = "mapping-area",
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new()
                        {
                            DeviceId = "device-panel",
                            DeviceName = "Wall panel",
                            HueBridgeIp = "192.168.1.102",
                            HueAppKey = "device-app-secret",
                            HueClientKey = "device-client-secret",
                            EntertainmentAreaId = "device-area"
                        }
                    }
                }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "device-playlist",
                    Name = "Device playlist",
                    PresetNames = new List<string> { "Device scene" }
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new RecordingStreamTester();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        var result = await service.RunPlaylistPreviewAsync(
            configuration.ScenePlaylists[0],
            targetRoutesOverride: new[]
            {
                new HueSceneAutomationTargetRoute { UserId = " user-device ", DeviceId = " device-panel " }
            });

        Assert.True(result.Succeeded);
        var route = Assert.Single(result.TargetRoutes);
        Assert.Equal("user-device", route.UserId);
        Assert.Equal("device-panel", route.DeviceId);
        Assert.Equal("Device room / Wall panel", result.TargetLabel);
        Assert.Equal(new[] { 20 }, streamTester.Reds);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("global-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("mapping-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("device-app-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunPlaylistPreview_UsesPersistedSelectedTargetsAndPreservesTargetTelemetry()
    {
        var configuration = new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "global-app-secret",
            HueClientKey = "global-client-secret",
            EntertainmentAreaId = "global-area",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Welcome", Red = 20, Green = 30, Blue = 40, DurationSeconds = 1 }
            },
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
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "selected-playlist",
                    Name = "Selected sequence",
                    PresetNames = new List<string> { "Welcome" },
                    TargetUserIds = new List<string> { "user-1" },
                    IncludeDefaultTarget = true
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new RecordingStreamTester();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        var result = await service.RunPlaylistPreviewAsync(configuration.ScenePlaylists[0]);

        Assert.True(result.Succeeded);
        Assert.Equal("Default bridge + 1 selected target(s)", result.TargetLabel);
        Assert.Equal(new[] { "user-1" }, result.TargetUserIds);
        Assert.True(result.IncludeDefaultTarget);
        Assert.Equal(new[] { "Default bridge target", "Kitchen" }, result.TargetResults.Select(target => target.TargetLabel));
        Assert.DoesNotContain(result.TargetResults, target => target.TargetLabel == "Office");
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("global-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("mapping-app-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("office-app-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunPlaylistPreview_ContinuousModePreservesDistinctTargetsThatShareADisplayLabel()
    {
        var configuration = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Warm", Red = 25, Green = 50, Blue = 75, DurationSeconds = 1 },
                new() { Name = "Cool", Red = 220, Green = 180, Blue = 140, DurationSeconds = 1 }
            },
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-1",
                    UserName = "Living Room",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "first-app-secret",
                    HueClientKey = "first-client-secret",
                    EntertainmentAreaId = "first-area"
                },
                new()
                {
                    UserId = "user-2",
                    UserName = "Living Room",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.102",
                    HueAppKey = "second-app-secret",
                    HueClientKey = "second-client-secret",
                    EntertainmentAreaId = "second-area"
                }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "same-label-playlist",
                    Name = "Shared room sequence",
                    PresetNames = new List<string> { "Warm", "Cool" },
                    TargetUserIds = new List<string> { "user-1", "user-2" }
                }
            }
        };
        InstallConfiguration(configuration);

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new ContinuousPlaylistStreamTester();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        var result = await service.RunPlaylistPreviewAsync(configuration.ScenePlaylists[0]);

        Assert.True(result.Succeeded);
        Assert.Equal(0, streamTester.LegacyPreviewCallCount);
        Assert.Equal(2, streamTester.PlaylistInvocations.Count);
        Assert.Equal(
            new[] { "192.168.1.101", "192.168.1.102" },
            streamTester.PlaylistInvocations.Select(invocation => invocation.BridgeIp));
        Assert.Equal(2, result.TargetResults.Count);
        Assert.All(result.TargetResults, target =>
        {
            Assert.Equal("Living Room", target.TargetLabel);
            Assert.True(target.Succeeded);
            Assert.Equal(2, target.CompletedStepCount);
            Assert.Equal(2, target.TotalStepCount);
        });
    }

    [Fact]
    public async Task RunSchedule_ResolvesPresetAndReturnsSanitizedResult()
    {
        InstallConfiguration(new PluginConfiguration
        {
            SceneAutomationEnabled = false,
            PersistSceneScheduleHistory = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "app-secret",
            HueClientKey = "client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Evening", Effect = PluginConfiguration.ColorPresetEffectPulse, EffectSpeedPercent = 150, Red = 12, Green = 34, Blue = 56, BrightnessPercent = 75, DurationSeconds = 8, TransitionSeconds = 2, TransitionOutSeconds = 1 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "cue-1", Name = "Evening cue", PresetName = "evening", BrightnessPercent = 33 }
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
                33,
                8,
                It.IsAny<CancellationToken>(),
                2,
                1,
                PluginConfiguration.ColorPresetEffectPulse,
                150))
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
        Assert.Equal(PluginConfiguration.ColorPresetEffectPulse, result.Effect);
        Assert.Equal(150, result.EffectSpeedPercent);
        Assert.Equal(33, result.BrightnessPercent);
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
        Assert.False(status.AutomationEnabled);
        Assert.Equal("cue-1", runtime.ScheduleId);
        Assert.Equal(2, runtime.TransitionSeconds);
        Assert.Equal(1, runtime.TransitionOutSeconds);
        Assert.Equal(PluginConfiguration.ColorPresetEffectPulse, runtime.Effect);
        Assert.Equal(33, runtime.BrightnessPercent);
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
    public async Task RunSchedule_BroadcastRunsSequentiallyAndReportsEachTarget()
    {
        InstallConfiguration(new PluginConfiguration
        {
            SceneAutomationEnabled = false,
            PersistSceneScheduleHistory = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "global-app-secret",
            HueClientKey = "global-client-secret",
            EntertainmentAreaId = "global-area",
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening", Red = 20, Green = 30, Blue = 40 } },
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
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "broadcast-cue",
                    Name = "Whole home welcome",
                    PresetName = "Evening",
                    TargetAllEnabledMappings = true
                }
            }
        });

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new Mock<IHueStreamTester>();
        streamTester
            .Setup(tester => tester.PreviewAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<JsonElement>(),
                It.IsAny<IReadOnlySet<int>?>(),
                20,
                30,
                40,
                100,
                5,
                It.IsAny<CancellationToken>(),
                0,
                0,
                PluginConfiguration.ColorPresetEffectSolid,
                100))
            .Returns((string bridgeIp, string appKey, string clientKey, string areaId, JsonElement areaConfiguration,
                IReadOnlySet<int>? channelIds, int red, int green, int blue, int brightnessPercent, int durationSeconds,
                CancellationToken cancellationToken, int transitionSeconds, int transitionOutSeconds, string effect,
                int effectSpeedPercent) => Task.FromResult(new HueStreamProbeResult
                {
                    Succeeded = !string.Equals(bridgeIp, "192.168.1.101", StringComparison.Ordinal),
                    Message = string.Equals(bridgeIp, "192.168.1.101", StringComparison.Ordinal)
                        ? "Kitchen was unavailable."
                        : "Displayed scheduled scene."
                }));

        var service = new HueSceneAutomationService(
            streamTester.Object,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        var result = await service.RunScheduleAsync("broadcast-cue");

        Assert.False(result.Succeeded);
        Assert.Equal("All enabled targets", result.TargetLabel);
        Assert.Equal(2, result.TargetResults.Count);
        Assert.Equal("Default bridge target", result.TargetResults[0].TargetLabel);
        Assert.True(result.TargetResults[0].Succeeded);
        Assert.Equal("Kitchen", result.TargetResults[1].TargetLabel);
        Assert.False(result.TargetResults[1].Succeeded);
        Assert.Contains("Kitchen", result.Message, StringComparison.Ordinal);
        Assert.Equal(2, service.GetHistory().Single().TargetResults.Count);
        var runtime = Assert.Single(service.GetStatus().Schedules);
        Assert.Equal(2, runtime.LastTargetResults.Count);
        Assert.False(runtime.LastTargetResults[1].Succeeded);
        streamTester.Verify(tester => tester.PreviewAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<JsonElement>(),
            It.IsAny<IReadOnlySet<int>?>(), 20, 30, 40, 100, 5, It.IsAny<CancellationToken>(), 0, 0,
            PluginConfiguration.ColorPresetEffectSolid, 100), Times.Exactly(2));
    }

    [Fact]
    public async Task RunSchedule_CancelScheduleStopsManualRunAndReleasesCancellationSlot()
    {
        InstallConfiguration(new PluginConfiguration
        {
            SceneAutomationEnabled = false,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "app-secret",
            HueClientKey = "client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Cue", DurationSeconds = 8 }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "cue-1", Name = "Cue", PresetName = "Cue" }
            }
        });

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var streamTester = new BlockingStreamTester();
        var service = new HueSceneAutomationService(
            streamTester,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        var runTask = service.RunScheduleAsync("cue-1");
        await streamTester.PreviewStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(service.CancelSchedule(" cue-1 "));
        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Succeeded);
        Assert.Contains("canceled", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(service.CancelSchedule("cue-1"));
        var runtime = Assert.Single(service.GetStatus().Schedules);
        Assert.False(runtime.IsRunning);
        Assert.Equal(1, runtime.RunCount);
        var history = Assert.Single(service.GetHistory());
        Assert.Contains("canceled", history.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PausedAutomation_SkipsDueCueWithoutClaimingOrRunningIt()
    {
        var now = DateTime.Now;
        var streamTester = new Mock<IHueStreamTester>();
        InstallConfiguration(new PluginConfiguration
        {
            SceneAutomationEnabled = false,
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "paused-cue",
                    Name = "Paused cue",
                    Enabled = true,
                    TimeOfDay = now.ToString("HH:mm"),
                    DaysOfWeekMask = 1 << (int)now.DayOfWeek
                }
            }
        });

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var service = new HueSceneAutomationService(
            streamTester.Object,
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        await service.RunDueSchedulesAsync(now, CancellationToken.None);

        streamTester.VerifyNoOtherCalls();
        var status = service.GetStatus();
        Assert.False(status.AutomationEnabled);
        var runtime = Assert.Single(status.Schedules);
        Assert.Equal(0, runtime.RunCount);
        Assert.Null(runtime.LastSucceeded);
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
                    WasCatchUp = true,
                    Message = "The bridge was unavailable.",
                    CleanupWarning = "Cleanup warning",
                    BrightnessPercent = 42,
                    PlaylistSteps = new List<HueScenePlaylistStepHistoryEntry>
                    {
                        new()
                        {
                            Index = 1,
                            RepeatIndex = 2,
                            OriginalIndex = 2,
                            PresetName = "Accent",
                            EffectSpeedPercent = 275,
                            BrightnessPercent = 37,
                            StartOffsetSeconds = 9,
                            DurationSeconds = 4,
                            TransitionSeconds = 2,
                            TransitionOutSeconds = 1,
                            Succeeded = true,
                            Message = "Restored step telemetry."
                        }
                    },
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
        Assert.True(history.WasCatchUp);
        Assert.Equal(42, history.BrightnessPercent);
        var restoredStep = Assert.Single(history.PlaylistSteps);
        Assert.Equal(37, restoredStep.BrightnessPercent);
        Assert.Equal(275, restoredStep.EffectSpeedPercent);
        Assert.Equal("Accent", restoredStep.PresetName);
        Assert.Equal(9, restoredStep.StartOffsetSeconds);
        Assert.Equal(2, restoredStep.TransitionSeconds);
        Assert.Equal(1, restoredStep.TransitionOutSeconds);
        Assert.True(runtime.LastWasCatchUp);
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

    [Fact]
    public void PersistedHistory_UsesConfiguredRetentionAndRefreshTrimsLoadedEntries()
    {
        InstallConfiguration(new PluginConfiguration
        {
            PersistSceneScheduleHistory = true,
            SceneScheduleHistoryRetentionCount = 3,
            PersistedSceneScheduleHistory = Enumerable.Range(0, 5)
                .Select(index => new HueSceneScheduleHistoryEntry
                {
                    ScheduleId = "cue-" + index,
                    ScheduleName = "Cue " + index,
                    Message = "Run " + index,
                    RunAtUtc = DateTime.UtcNow.AddMinutes(-index)
                })
                .ToList()
        });

        using var httpClient = new HttpClient(new AreaConfigurationHandler());
        var service = new HueSceneAutomationService(
            Mock.Of<IHueStreamTester>(),
            new HueClient(httpClient, Mock.Of<ILogger<HueClient>>()),
            Mock.Of<ILogger<HueSceneAutomationService>>());

        Assert.Equal(3, service.GetHistory().Count);
        Assert.Equal(3, Plugin.Instance!.Configuration.PersistedSceneScheduleHistory.Count);

        Plugin.Instance.Configuration.SceneScheduleHistoryRetentionCount = 1;
        service.RefreshSceneScheduleHistoryPersistence();

        Assert.Single(service.GetHistory());
        Assert.Single(Plugin.Instance.Configuration.PersistedSceneScheduleHistory);
        Assert.Equal("cue-0", service.GetHistory()[0].ScheduleId);
    }

    private static PluginConfiguration CreateContinuousPlaylistConfiguration(
        string playlistId,
        string playlistName,
        int stepCount)
    {
        var presets = Enumerable.Range(1, stepCount)
            .Select(index => new HueColorPreset
            {
                Name = $"Scene {index}",
                Red = index * 20,
                Green = index * 20 + 1,
                Blue = index * 20 + 2,
                DurationSeconds = 1
            })
            .ToList();
        return new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "continuous-app-secret",
            HueClientKey = "continuous-client-secret",
            EntertainmentAreaId = "area-1",
            ColorPresets = presets,
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = playlistId,
                    Name = playlistName,
                    PresetNames = presets.Select(preset => preset.Name).ToList()
                }
            }
        };
    }

    private static void InstallConfiguration(
        PluginConfiguration configuration,
        IXmlSerializer? xmlSerializer = null)
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

        var plugin = new Jellyfin.Plugin.Hue.Plugin(applicationPaths.Object, xmlSerializer ?? Mock.Of<IXmlSerializer>());
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

    private sealed class ContinuousPlaylistStreamTester : IHueStreamTester, IHuePlaylistStreamTester
    {
        private readonly Queue<HuePlaylistStreamProbeResult> _queuedResults = new();

        public int LegacyPreviewCallCount { get; private set; }

        public List<(string BridgeIp, string AreaId, IReadOnlyList<HuePlaylistPreviewStep> Steps)>
            PlaylistInvocations
        { get; } = new();

        public List<(string BridgeIp, string AreaId, IReadOnlyList<HuePlaylistPreviewStep> Steps)>
            TargetScopedPlaylistInvocations
        { get; } = new();

        public void EnqueueResult(HuePlaylistStreamProbeResult result)
            => _queuedResults.Enqueue(result);

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
        {
            LegacyPreviewCallCount++;
            return Task.FromResult(new HueStreamProbeResult
            {
                Succeeded = true,
                Message = "Unexpected legacy preview."
            });
        }

        public Task<HuePlaylistStreamProbeResult> PreviewPlaylistAsync(
            string bridgeIp,
            string appKey,
            string clientKey,
            string areaId,
            JsonElement areaConfiguration,
            IReadOnlySet<int>? channelIds,
            IReadOnlyList<HuePlaylistPreviewStep> steps,
            CancellationToken cancellationToken = default)
            => RecordPlaylistInvocation(
                PlaylistInvocations,
                bridgeIp,
                areaId,
                steps);

        public Task<HuePlaylistStreamProbeResult> PreviewPlaylistAsyncForTarget(
            string bridgeIp,
            string appKey,
            string clientKey,
            string areaId,
            JsonElement areaConfiguration,
            IReadOnlySet<int>? channelIds,
            IReadOnlyList<HuePlaylistPreviewStep> steps,
            CancellationToken cancellationToken = default)
            => RecordPlaylistInvocation(
                TargetScopedPlaylistInvocations,
                bridgeIp,
                areaId,
                steps);

        public bool CancelActiveDiagnostic() => false;

        private Task<HuePlaylistStreamProbeResult> RecordPlaylistInvocation(
            List<(string BridgeIp, string AreaId, IReadOnlyList<HuePlaylistPreviewStep> Steps)> invocations,
            string bridgeIp,
            string areaId,
            IReadOnlyList<HuePlaylistPreviewStep> steps)
        {
            invocations.Add((bridgeIp, areaId, steps.ToArray()));
            if (_queuedResults.Count > 0)
                return Task.FromResult(_queuedResults.Dequeue());

            return Task.FromResult(new HuePlaylistStreamProbeResult
            {
                Succeeded = true,
                Message = $"Displayed {steps.Count} playlist step(s) in one continuous stream.",
                Steps = steps
                    .Select(step => new HuePlaylistPreviewStepResult
                    {
                        Index = step.Index,
                        Succeeded = true,
                        Message = $"Displayed playlist step {step.Index}."
                    })
                    .ToArray()
            });
        }
    }

    private sealed class BlockingStreamTester : IHueStreamTester
    {
        public TaskCompletionSource<bool> PreviewStarted { get; } =
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
        {
            PreviewStarted.TrySetResult(true);
            return WaitForCancellationAsync(cancellationToken);
        }

        public bool CancelActiveDiagnostic() => false;

        private static async Task<HueStreamProbeResult> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HueStreamProbeResult
            {
                Succeeded = true,
                Message = "Unexpected completion."
            };
        }
    }

    private sealed class OverlapRecoveryStreamTester : IHueStreamTester
    {
        private int _previewCount;

        public TaskCompletionSource<bool> FirstPreviewStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseFirstPreview { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<int> Reds { get; } = new();

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
            Reds.Add(red);
            if (Interlocked.Increment(ref _previewCount) == 1)
            {
                FirstPreviewStarted.TrySetResult(true);
                await ReleaseFirstPreview.Task.WaitAsync(cancellationToken);
            }

            return new HueStreamProbeResult
            {
                Succeeded = true,
                Message = "Displayed overlap-recovery scene."
            };
        }

        public bool CancelActiveDiagnostic() => false;
    }

    private sealed class RecordingStreamTester : IHueStreamTester, IHueTransitionCurveStreamTester
    {
        public List<int> Reds { get; } = new();
        public List<int> Brightnesses { get; } = new();
        public List<int> Durations { get; } = new();
        public List<int> TransitionSeconds { get; } = new();
        public List<int> TransitionOutSeconds { get; } = new();
        public List<int> EffectSpeeds { get; } = new();
        public List<string> TransitionCurves { get; } = new();

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
        {
            Reds.Add(red);
            Brightnesses.Add(brightnessPercent);
            Durations.Add(durationSeconds);
            TransitionSeconds.Add(transitionSeconds);
            TransitionOutSeconds.Add(transitionOutSeconds);
            EffectSpeeds.Add(effectSpeedPercent);
            return Task.FromResult(new HueStreamProbeResult
            {
                Succeeded = true,
                Message = "Displayed scheduled scene."
            });
        }

        public Task<HueStreamProbeResult> PreviewAsyncWithTransitionCurve(
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
            int effectSpeedPercent = PluginConfiguration.DefaultColorPresetEffectSpeedPercent,
            string transitionCurve = PluginConfiguration.ColorPresetTransitionCurveLinear)
        {
            Reds.Add(red);
            Brightnesses.Add(brightnessPercent);
            Durations.Add(durationSeconds);
            TransitionSeconds.Add(transitionSeconds);
            TransitionOutSeconds.Add(transitionOutSeconds);
            EffectSpeeds.Add(effectSpeedPercent);
            TransitionCurves.Add(transitionCurve);
            return Task.FromResult(new HueStreamProbeResult
            {
                Succeeded = true,
                Message = "Displayed scheduled scene."
            });
        }

        public Task<HueStreamProbeResult> PreviewAsyncForTargetWithTransitionCurve(
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
            int effectSpeedPercent = PluginConfiguration.DefaultColorPresetEffectSpeedPercent,
            string transitionCurve = PluginConfiguration.ColorPresetTransitionCurveLinear)
            => PreviewAsyncWithTransitionCurve(
                bridgeIp,
                appKey,
                clientKey,
                areaId,
                areaConfiguration,
                channelIds,
                red,
                green,
                blue,
                brightnessPercent,
                durationSeconds,
                cancellationToken,
                transitionSeconds,
                transitionOutSeconds,
                effect,
                effectSpeedPercent,
                transitionCurve);

        public bool CancelActiveDiagnostic() => false;
    }
}
