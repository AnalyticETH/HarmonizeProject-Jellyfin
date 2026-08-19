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
                10,
                20,
                30,
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
        Assert.Equal(1, runtime.RunCount);
        Assert.True(runtime.LastSucceeded);
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

        var result = await service.RunPlaylistPreviewAsync(configuration.ScenePlaylists[0]);

        Assert.True(result.Succeeded);
        Assert.Equal("playlist-1", result.PlaylistId);
        Assert.Equal("Evening sequence", result.PlaylistName);
        Assert.Equal(new[] { 25, 220 }, streamTester.Reds);
        Assert.Equal(new[] { "Warm", "Cool" }, result.Steps.Select(step => step.PresetName));
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
                new() { Name = "Second", Red = 222, Green = 180, Blue = 140, BrightnessPercent = 70, DurationSeconds = 2 }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new() { Id = "scheduled-playlist", Name = "Scheduled sequence", PresetNames = new List<string> { "First", "Second" } }
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
        Assert.Equal(new[] { "First", "Second" }, result.PlaylistSteps.Select(step => step.PresetName));
        Assert.Equal(new[] { 11, 222 }, streamTester.Reds);
        Assert.Equal(1, configuration.SceneSchedules[0].RunCount);
        var runtime = Assert.Single(service.GetStatus().Schedules);
        Assert.Equal("Scheduled sequence", runtime.PlaylistName);
        Assert.Equal(2, runtime.PlaylistStepCount);
        Assert.Equal(3, runtime.PlaylistTotalDurationSeconds);
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
        Assert.Equal(0, skippedHistory.RunCount);
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
            80,
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

    private sealed class RecordingStreamTester : IHueStreamTester
    {
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
            return Task.FromResult(new HueStreamProbeResult
            {
                Succeeded = true,
                Message = "Displayed scheduled scene."
            });
        }

        public bool CancelActiveDiagnostic() => false;
    }
}
