using Jellyfin.Plugin.Hue.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

public class PluginConfigurationTests
{
    [Fact]
    public void ValidateColorPresets_AllowsValidReusableScene()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset>
            {
                new()
                {
                    Name = "Movie Night",
                    Red = 220,
                    Green = 90,
                    Blue = 35,
                    BrightnessPercent = 75,
                    DurationSeconds = 8,
                    TransitionSeconds = 3
                }
            }
        };

        Assert.Empty(config.ValidateColorPresets());
        Assert.Empty(config.Validate());
    }

    [Fact]
    public void ValidateColorPresets_RejectsInvalidValuesAndDuplicateNames()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Accent", Red = 256, DurationSeconds = 0, TransitionSeconds = 31 },
                new() { Name = " accent " }
            }
        };

        var errors = config.ValidateColorPresets();

        Assert.Contains("Color preset 1 RGB values must be between 0 and 255", errors);
        Assert.Contains("Color preset 1 duration must be between 1 and 30 seconds", errors);
        Assert.Contains("Color preset 1 transition must be between 0 and 30 seconds", errors);
        Assert.Contains("Color preset 2 duplicates another color preset name", errors);
    }

    [Fact]
    public void ValidateColorPresets_RejectsTransitionLongerThanScene()
    {
        var errors = PluginConfiguration.ValidateColorPreset(new HueColorPreset
        {
            Name = "Slow fade",
            DurationSeconds = 4,
            TransitionSeconds = 5
        });

        Assert.Contains("transition cannot exceed the scene duration", errors);
    }

    [Fact]
    public void ValidateColorPresets_RejectsTooManyScenes()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = Enumerable.Range(0, PluginConfiguration.MaxColorPresets + 1)
                .Select(index => new HueColorPreset { Name = $"Scene {index}" })
                .ToList()
        };

        Assert.Contains($"No more than {PluginConfiguration.MaxColorPresets} color presets may be saved", config.ValidateColorPresets());
    }

    [Fact]
    public void ValidateSceneSchedules_AllowsSavedSceneAndNormalizesTime()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "cue-1",
                    Name = "Evening welcome",
                    PresetName = " evening ",
                    TimeOfDay = "07:05",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    DaysOfWeekMask = 1 | 32,
                    ExcludedDates = new List<string> { "2026-12-25" }
                }
            }
        };

        Assert.Empty(config.ValidateSceneSchedules());
        Assert.True(PluginConfiguration.TryNormalizeSceneScheduleTime("07:05", out var normalized));
        Assert.Equal("07:05", normalized);
        Assert.True(PluginConfiguration.TryResolveSceneScheduleTimeZone(TimeZoneInfo.Utc.Id, out var timeZone));
        Assert.Equal(TimeZoneInfo.Utc.Id, timeZone.Id);
    }

    [Fact]
    public void ValidateSceneSchedules_AllowsAndNormalizesInclusiveDateWindow()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "cue-window",
                    Name = "Limited welcome",
                    PresetName = "Evening",
                    TimeOfDay = "07:05",
                    StartDate = " 2026-08-01 ",
                    EndDate = "2026-08-31",
                    DaysOfWeekMask = 127
                }
            }
        };

        Assert.Empty(config.ValidateSceneSchedules());
        Assert.True(PluginConfiguration.TryNormalizeSceneScheduleDate(" 2026-08-01 ", out var normalizedStart));
        Assert.Equal("2026-08-01", normalizedStart);
        Assert.True(PluginConfiguration.TryNormalizeSceneScheduleDate(string.Empty, out var normalizedBlank));
        Assert.Equal(string.Empty, normalizedBlank);
        Assert.True(PluginConfiguration.TryNormalizeSceneScheduleExcludedDates(
            new[] { "2026-12-31", " 2026-12-24 ", "2026-12-31" },
            out var normalizedExcluded));
        Assert.Equal(new[] { "2026-12-24", "2026-12-31" }, normalizedExcluded);
    }

    [Fact]
    public void ValidateSceneSchedules_AllowsOneTimeRunDateWithoutWeekdayMask()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "cue-once",
                    Name = "Movie night",
                    PresetName = "Evening",
                    TimeOfDay = "21:15",
                    TimeZoneId = TimeZoneInfo.Utc.Id,
                    RunDate = " 2026-12-24 ",
                    DaysOfWeekMask = 0
                }
            }
        };

        Assert.Empty(config.ValidateSceneSchedules());
        Assert.True(PluginConfiguration.TryNormalizeSceneScheduleDate(config.SceneSchedules[0].RunDate, out var normalized));
        Assert.Equal("2026-12-24", normalized);
    }

    [Fact]
    public void ValidateSceneSchedules_AllowsDurationOverrideAndSceneDefault()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "cue-duration",
                    Name = "Longer welcome",
                    PresetName = "Evening",
                    TimeOfDay = "20:00",
                    DurationSeconds = 12,
                    DaysOfWeekMask = 127
                },
                new()
                {
                    Id = "cue-default-duration",
                    Name = "Scene default welcome",
                    PresetName = "Evening",
                    TimeOfDay = "20:15",
                    DurationSeconds = 0,
                    DaysOfWeekMask = 127
                }
            }
        };

        Assert.Empty(config.ValidateSceneSchedules());
    }

    [Fact]
    public void ValidateSceneSchedules_AllowsMonthlyDayAndClampedShortMonths()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "cue-monthly",
                    Name = "Month end welcome",
                    PresetName = "Evening",
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceMonthly,
                    DayOfMonth = 31,
                    TimeOfDay = "20:00",
                    DaysOfWeekMask = 0
                }
            }
        };

        Assert.Empty(config.ValidateSceneSchedules());
    }

    [Fact]
    public void ValidateSceneSchedules_AllowsDailyRecurrenceWithoutWeekdayMask()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Everyday" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "cue-daily",
                    Name = "Everyday welcome",
                    PresetName = "Everyday",
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    TimeOfDay = "07:05",
                    DaysOfWeekMask = 0
                }
            }
        };

        Assert.Empty(config.ValidateSceneSchedules());
        Assert.True(PluginConfiguration.TryNormalizeSceneScheduleRecurrence(" daily ", out var normalized));
        Assert.Equal(PluginConfiguration.SceneScheduleRecurrenceDaily, normalized);
    }

    [Fact]
    public void ValidateSceneSchedules_AllowsMonthlyWeekdayRecurrence()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Weekday" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "cue-monthly-weekday",
                    Name = "Last Friday",
                    PresetName = "Weekday",
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceMonthlyWeekday,
                    WeekOfMonth = PluginConfiguration.SceneScheduleLastWeekOfMonth,
                    DayOfWeek = (int)System.DayOfWeek.Friday,
                    TimeOfDay = "18:00",
                    DaysOfWeekMask = 0
                }
            }
        };

        Assert.Empty(config.ValidateSceneSchedules());
        Assert.True(PluginConfiguration.TryNormalizeSceneScheduleRecurrence(
            " monthlyweekday ",
            out var normalized));
        Assert.Equal(PluginConfiguration.SceneScheduleRecurrenceMonthlyWeekday, normalized);
    }

    [Fact]
    public void ValidateSceneSchedules_AllowsYearlyRecurrenceAndNormalizesMonth()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Annual" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "cue-yearly",
                    Name = "Holiday welcome",
                    PresetName = "Annual",
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceYearly,
                    MonthOfYear = 12,
                    DayOfMonth = 31,
                    TimeOfDay = "23:00",
                    DaysOfWeekMask = 0
                }
            }
        };

        Assert.Empty(config.ValidateSceneSchedules());
        Assert.True(PluginConfiguration.TryNormalizeSceneScheduleRecurrence(
            " yearly ",
            out var normalized));
        Assert.Equal(PluginConfiguration.SceneScheduleRecurrenceYearly, normalized);
    }

    [Fact]
    public void ValidateSceneSchedules_AllowsBoundedRecurrenceIntervalWithAnchor()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Every Other Day" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "cue-interval",
                    Name = "Every other day",
                    PresetName = "Every Other Day",
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    RecurrenceInterval = 2,
                    StartDate = "2026-08-17",
                    TimeOfDay = "07:05",
                    DaysOfWeekMask = 0
                }
            }
        };

        Assert.Empty(config.ValidateSceneSchedules());
    }

    [Fact]
    public void ValidateSceneSchedules_RejectsUnanchoredOrOutOfRangeInterval()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "cue-unanchored",
                    Name = "Unanchored interval",
                    PresetName = "Evening",
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    RecurrenceInterval = 2,
                    TimeOfDay = "20:00",
                    DaysOfWeekMask = 0
                },
                new()
                {
                    Id = "cue-too-large",
                    Name = "Too-large interval",
                    PresetName = "Evening",
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceDaily,
                    RecurrenceInterval = PluginConfiguration.MaxSceneScheduleRecurrenceInterval + 1,
                    StartDate = "2026-08-17",
                    TimeOfDay = "20:00",
                    DaysOfWeekMask = 0
                }
            }
        };

        var errors = config.ValidateSceneSchedules();

        Assert.Contains(errors, error => error.Contains("require a start date anchor", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("recurrence interval must be between", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateSceneSchedules_RejectsInvalidMonthlyRecurrence()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "cue-monthly-invalid",
                    Name = "Invalid monthly cue",
                    PresetName = "Evening",
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceMonthly,
                    DayOfMonth = 0,
                    TimeOfDay = "20:00",
                    DaysOfWeekMask = 0
                },
                new()
                {
                    Id = "cue-recurrence-invalid",
                    Name = "Invalid recurrence cue",
                    PresetName = "Evening",
                    Recurrence = "Hourly",
                    DayOfMonth = 1,
                    TimeOfDay = "20:00",
                    DaysOfWeekMask = 127
                },
                new()
                {
                    Id = "cue-yearly-invalid",
                    Name = "Invalid yearly cue",
                    PresetName = "Evening",
                    Recurrence = PluginConfiguration.SceneScheduleRecurrenceYearly,
                    MonthOfYear = 13,
                    DayOfMonth = 0,
                    TimeOfDay = "20:00",
                    DaysOfWeekMask = 0
                }
            }
        };

        var errors = config.ValidateSceneSchedules();

        Assert.Contains(errors, error => error.Contains("monthly recurrence requires", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("yearly recurrence requires a month", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("yearly recurrence requires a day", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("recurrence must be Daily, Weekly, Monthly, MonthlyWeekday, or Yearly", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateSceneSchedules_RejectsDurationOverrideOutsideSupportedRange()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "cue-short-duration",
                    Name = "Too short",
                    PresetName = "Evening",
                    DurationSeconds = -1,
                    DaysOfWeekMask = 127
                },
                new()
                {
                    Id = "cue-long-duration",
                    Name = "Too long",
                    PresetName = "Evening",
                    DurationSeconds = PluginConfiguration.MaxPreviewDurationSeconds + 1,
                    DaysOfWeekMask = 127
                }
            }
        };

        var errors = config.ValidateSceneSchedules();

        Assert.Equal(2, errors.Count(error => error.Contains("duration override", StringComparison.Ordinal)));
    }

    [Fact]
    public void ValidateSceneSchedules_RejectsOneTimeDateWithRecurringDateRules()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "cue-once-conflict",
                    Name = "Conflicting cue",
                    PresetName = "Evening",
                    RunDate = "2026-12-24",
                    StartDate = "2026-12-01",
                    ExcludedDates = new List<string> { "2026-12-25" },
                    DaysOfWeekMask = 127
                }
            }
        };

        var errors = config.ValidateSceneSchedules();

        Assert.Contains(errors, error => error.Contains("one-time run date cannot be combined with a start or end date", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("one-time run date cannot be combined with excluded dates", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateSceneSchedules_RejectsInvalidAndReversedDateWindows()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "cue-invalid-date",
                    Name = "Invalid date",
                    PresetName = "Evening",
                    StartDate = "2026-02-30",
                    ExcludedDates = new List<string> { "2026-13-01" },
                    DaysOfWeekMask = 127
                },
                new()
                {
                    Id = "cue-reversed-date",
                    Name = "Reversed date",
                    PresetName = "Evening",
                    StartDate = "2026-09-01",
                    EndDate = "2026-08-01",
                    ExcludedDates = new List<string> { "2026-12-01", "2026-12-01" },
                    DaysOfWeekMask = 127
                }
            }
        };

        var errors = config.ValidateSceneSchedules();

        Assert.Contains("Scene schedule 1 start date must use yyyy-MM-dd format", errors);
        Assert.Contains("Scene schedule 1 excluded date 1 must use yyyy-MM-dd format", errors);
        Assert.Contains("Scene schedule 2 end date must be on or after the start date", errors);
        Assert.Contains("Scene schedule 2 excludes the date 2026-12-01 more than once", errors);
    }

    [Fact]
    public void ValidateSceneSchedules_RejectsTooManyExcludedDates()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "cue-too-many-exclusions",
                    Name = "Too many exclusions",
                    PresetName = "Evening",
                    ExcludedDates = Enumerable.Range(0, PluginConfiguration.MaxSceneScheduleExcludedDates + 1)
                        .Select(offset => new DateTime(2026, 1, 1).AddDays(offset).ToString("yyyy-MM-dd"))
                        .ToList(),
                    DaysOfWeekMask = 127
                }
            }
        };

        Assert.Contains(
            $"Scene schedule 1 may exclude no more than {PluginConfiguration.MaxSceneScheduleExcludedDates} dates",
            config.ValidateSceneSchedules());
    }

    [Fact]
    public void ValidateSceneSchedules_RejectsInvalidReferencesAndDuplicates()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "duplicate", Name = "Cue", PresetName = "Missing", TimeOfDay = "25:00", TimeZoneId = "Missing/Zone", DaysOfWeekMask = 0 },
                new() { Id = "duplicate", Name = " cue ", PresetName = "Evening", TimeOfDay = "20:00", DaysOfWeekMask = 127 }
            }
        };

        var errors = config.ValidateSceneSchedules();

        Assert.Contains("Scene schedule 1 references a saved scene that does not exist", errors);
        Assert.Contains("Scene schedule 1 time must use 24-hour HH:mm format", errors);
        Assert.Contains("Scene schedule 1 time zone is not available on this server", errors);
        Assert.Contains("Scene schedule 1 must select at least one day of the week", errors);
        Assert.Contains("Scene schedule 2 duplicates another scene schedule ID", errors);
        Assert.Contains("Scene schedule 2 duplicates another scene schedule name", errors);
    }

    [Fact]
    public void ValidateSceneSchedules_RejectsTooManyCues()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = Enumerable.Range(0, PluginConfiguration.MaxSceneSchedules + 1)
                .Select(index => new HueColorPreset { Name = $"Scene {index}" })
                .ToList(),
            SceneSchedules = Enumerable.Range(0, PluginConfiguration.MaxSceneSchedules + 1)
                .Select(index => new HueSceneSchedule
                {
                    Id = $"cue-{index}",
                    Name = $"Cue {index}",
                    PresetName = $"Scene {index}"
                })
                .ToList()
        };

        Assert.Contains($"No more than {PluginConfiguration.MaxSceneSchedules} scene schedules may be saved", config.ValidateSceneSchedules());
    }

    [Fact]
    public void Validate_WhenSyncDisabled_ReturnsNoErrors()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = false
            // All other fields can be invalid
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Empty(errors);
        Assert.True(config.IsValid());
    }

    [Fact]
    public void Validate_WhenSyncEnabledWithValidConfig_ReturnsNoErrors()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-app-key",
            HueClientKey = "test-client-key",
            EntertainmentAreaId = "test-area-id",
            TargetFps = 20,
            SamplingBreadthPercent = 15,
            BrightnessDimLevel = 30,
            BrightnessBoost = 100,
            ColorSaturation = 100,
            BlackoutThreshold = 15,
            ColorChangeThreshold = 10,
            NetworkRetryAttempts = 3,
            FfmpegStallTimeoutSeconds = 5
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Empty(errors);
        Assert.True(config.IsValid());
    }

    [Fact]
    public void Validate_WhenHueBridgeIpMissing_ReturnsError()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id"
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Contains("Hue Bridge IP is required when sync is enabled", errors);
        Assert.False(config.IsValid());
    }

    [Fact]
    public void Validate_WhenHueBridgeIpInvalid_ReturnsError()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "invalid-ip-address",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id"
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Contains("Hue Bridge address must be a valid private IP address or .local host name", errors);
    }

    [Fact]
    public void Validate_WhenHueAppKeyMissing_ReturnsError()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id"
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Contains("Hue App Key is required. Use the 'Link Bridge' button to generate credentials", errors);
    }

    [Fact]
    public void Validate_WhenHueClientKeyMissing_ReturnsError()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "",
            EntertainmentAreaId = "test-id"
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Contains("Hue Client Key is required for streaming. Use the 'Link Bridge' button to generate credentials", errors);
    }

    [Fact]
    public void Validate_WhenEntertainmentAreaIdMissing_ReturnsError()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = ""
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Contains("Entertainment Area ID is required. Select an area from the dropdown or enter manually", errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(61)]
    [InlineData(100)]
    public void Validate_WhenTargetFpsOutOfRange_ReturnsError(int fps)
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            TargetFps = fps
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Contains("Target FPS must be between 1 and 60", errors);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(60)]
    public void Validate_WhenTargetFpsValid_NoError(int fps)
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            TargetFps = fps
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.DoesNotContain("Target FPS", errors.ToString());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Validate_WhenBrightnessDimLevelOutOfRange_ReturnsError(int level)
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            BrightnessDimLevel = level
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Contains("Brightness dim level must be between 0 and 100", errors);
    }

    [Theory]
    [InlineData(49)]
    [InlineData(201)]
    public void Validate_WhenBrightnessBoostOutOfRange_ReturnsError(int boost)
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            BrightnessBoost = boost
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Contains("Brightness boost must be between 50 and 200", errors);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(201)]
    public void Validate_WhenColorSaturationOutOfRange_ReturnsError(int saturation)
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            ColorSaturation = saturation
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Contains("Color saturation must be between 0 and 200", errors);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Validate_WhenOutputBrightnessIsOutOfRange_ReturnsError(int outputBrightnessPercent)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            OutputBrightnessPercent = outputBrightnessPercent
        };

        var errors = config.Validate();

        Assert.Contains("Output brightness must be between 0 and 100 percent", errors);
    }

    [Theory]
    [InlineData(-181)]
    [InlineData(181)]
    public void Validate_WhenHueShiftIsOutOfRange_ReturnsError(int hueShiftDegrees)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            HueShiftDegrees = hueShiftDegrees
        };

        var errors = config.Validate();

        Assert.Contains("Hue shift must be between -180 and 180 degrees", errors);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public void Validate_WhenBlackoutThresholdOutOfRange_ReturnsError(int threshold)
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            BlackoutThreshold = threshold
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Contains("Blackout threshold must be between 0 and 255", errors);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public void Validate_WhenColorChangeThresholdOutOfRange_ReturnsError(int threshold)
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            ColorChangeThreshold = threshold
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Contains("Color change threshold must be between 0 and 255", errors);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public void Validate_WhenNetworkRetryAttemptsOutOfRange_ReturnsError(int attempts)
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            NetworkRetryAttempts = attempts
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Contains("Network retry attempts must be between 0 and 10", errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void Validate_WhenFfmpegStallTimeoutOutOfRange_ReturnsError(int timeout)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            FfmpegStallTimeoutSeconds = timeout
        };

        var errors = config.Validate();

        Assert.Contains("FFmpeg stall timeout must be between 1 and 60 seconds", errors);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(60)]
    public void Validate_WhenFfmpegStallTimeoutIsValid_ReturnsNoStallError(int timeout)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            FfmpegStallTimeoutSeconds = timeout
        };

        var errors = config.Validate();

        Assert.DoesNotContain("FFmpeg stall timeout must be between 1 and 60 seconds", errors);
    }

    [Fact]
    public void Validate_WithMultipleErrors_ReturnsAllErrors()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "",
            HueAppKey = "",
            HueClientKey = "",
            EntertainmentAreaId = "",
            TargetFps = 0,
            BrightnessDimLevel = -1,
            BrightnessBoost = 300,
            ColorSaturation = -50
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.NotEmpty(errors);
        Assert.True(errors.Count >= 5); // At least 5 validation errors
    }

    [Fact]
    public void Constructor_SetsDefaultValues()
    {
        // Arrange & Act
        var config = new PluginConfiguration();

        // Assert
        Assert.False(config.SyncEnabled);
        Assert.True(config.UseCinemaMode);
        Assert.True(config.RestoreLightState);
        Assert.Equal(30, config.BrightnessDimLevel);
        Assert.Equal(20, config.TargetFps);
        Assert.Equal(PluginConfiguration.FrameResolutionStandard, config.FrameResolution);
        Assert.Equal(PluginConfiguration.VideoScalingModeStretch, config.VideoScalingMode);
        Assert.Equal(PluginConfiguration.VideoDeinterlaceModeOff, config.VideoDeinterlaceMode);
        Assert.Equal(15, config.SamplingBreadthPercent);
        Assert.Equal(PluginConfiguration.SamplingModeAverage, config.SamplingMode);
        Assert.Equal(0, config.ColorSmoothingPercent);
        Assert.True(config.UseGpu);
        Assert.Equal(100, config.BrightnessBoost);
        Assert.Equal(100, config.RedGain);
        Assert.Equal(100, config.GreenGain);
        Assert.Equal(100, config.BlueGain);
        Assert.Equal(100, config.ColorSaturation);
        Assert.Equal(0, config.HueShiftDegrees);
        Assert.Equal(100, config.OutputBrightnessPercent);
        Assert.Equal(15, config.BlackoutThreshold);
        Assert.Equal(10, config.ColorChangeThreshold);
        Assert.Equal(3, config.NetworkRetryAttempts);
        Assert.Equal(5, config.FfmpegStallTimeoutSeconds);
        Assert.Equal(PluginConfiguration.PauseBehaviorKeepLastColors, config.PauseBehavior);
        Assert.True(new UserBridgeMapping().SyncEnabled);
    }

    [Fact]
    public void GetFrameDimensions_ReturnsConfiguredResolutionPresets()
    {
        Assert.Equal((80, 45), PluginConfiguration.GetFrameDimensions(PluginConfiguration.FrameResolutionLow));
        Assert.Equal((160, 90), PluginConfiguration.GetFrameDimensions(PluginConfiguration.FrameResolutionStandard));
        Assert.Equal((320, 180), PluginConfiguration.GetFrameDimensions(PluginConfiguration.FrameResolutionHigh));
        Assert.Equal((160, 90), PluginConfiguration.GetFrameDimensions("unknown"));
    }

    [Theory]
    [InlineData("Unsupported")]
    [InlineData("")]
    public void Validate_WhenFrameResolutionIsInvalid_ReturnsError(string frameResolution)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            FrameResolution = frameResolution
        };

        var errors = config.Validate();

        Assert.Contains("Frame resolution must be 80x45, 160x90, or 320x180", errors);
    }

    [Theory]
    [InlineData(PluginConfiguration.FrameResolutionLow)]
    [InlineData(PluginConfiguration.FrameResolutionStandard)]
    [InlineData(PluginConfiguration.FrameResolutionHigh)]
    [InlineData("320X180")]
    public void Validate_WhenFrameResolutionIsValid_ReturnsNoResolutionError(string frameResolution)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            FrameResolution = frameResolution
        };

        var errors = config.Validate();

        Assert.DoesNotContain("Frame resolution must be 80x45, 160x90, or 320x180", errors);
    }

    [Theory]
    [InlineData("Unsupported")]
    [InlineData("")]
    public void Validate_WhenVideoScalingModeIsInvalid_ReturnsError(string videoScalingMode)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            VideoScalingMode = videoScalingMode
        };

        var errors = config.Validate();

        Assert.Contains("Video scaling mode must be Stretch, Fit, or Crop", errors);
    }

    [Theory]
    [InlineData(PluginConfiguration.VideoScalingModeStretch)]
    [InlineData(PluginConfiguration.VideoScalingModeFit)]
    [InlineData(PluginConfiguration.VideoScalingModeCrop)]
    [InlineData("fit")]
    public void Validate_WhenVideoScalingModeIsValid_ReturnsNoScalingError(string videoScalingMode)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            VideoScalingMode = videoScalingMode
        };

        var errors = config.Validate();

        Assert.DoesNotContain("Video scaling mode must be Stretch, Fit, or Crop", errors);
    }

    [Theory]
    [InlineData("Unsupported")]
    [InlineData("")]
    public void Validate_WhenVideoDeinterlaceModeIsInvalid_ReturnsError(string videoDeinterlaceMode)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            VideoDeinterlaceMode = videoDeinterlaceMode
        };

        var errors = config.Validate();

        Assert.Contains("Video deinterlace mode must be Off, Auto, or On", errors);
    }

    [Theory]
    [InlineData(PluginConfiguration.VideoDeinterlaceModeOff)]
    [InlineData(PluginConfiguration.VideoDeinterlaceModeAuto)]
    [InlineData(PluginConfiguration.VideoDeinterlaceModeOn)]
    [InlineData("auto")]
    public void Validate_WhenVideoDeinterlaceModeIsValid_ReturnsNoDeinterlaceError(string videoDeinterlaceMode)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            VideoDeinterlaceMode = videoDeinterlaceMode
        };

        var errors = config.Validate();

        Assert.DoesNotContain("Video deinterlace mode must be Off, Auto, or On", errors);
    }

    [Theory]
    [InlineData("Unsupported")]
    [InlineData("")]
    public void Validate_WhenPauseBehaviorIsInvalid_ReturnsError(string pauseBehavior)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            PauseBehavior = pauseBehavior
        };

        var errors = config.Validate();

        Assert.Contains("Pause behavior must be KeepLastColors or RestoreLightState", errors);
    }

    [Theory]
    [InlineData(PluginConfiguration.PauseBehaviorKeepLastColors)]
    [InlineData(PluginConfiguration.PauseBehaviorRestoreLightState)]
    [InlineData("restorelightstate")]
    public void Validate_WhenPauseBehaviorIsValid_ReturnsNoPauseError(string pauseBehavior)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            PauseBehavior = pauseBehavior
        };

        var errors = config.Validate();

        Assert.DoesNotContain("Pause behavior must be KeepLastColors or RestoreLightState", errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Validate_WhenSamplingBreadthIsOutOfRange_ReturnsError(int samplingBreadthPercent)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            SamplingBreadthPercent = samplingBreadthPercent
        };

        var errors = config.Validate();

        Assert.Contains("Sampling breadth must be between 1 and 50 percent", errors);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(50)]
    public void Validate_WhenSamplingBreadthIsValid_ReturnsNoSamplingError(int samplingBreadthPercent)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            SamplingBreadthPercent = samplingBreadthPercent
        };

        var errors = config.Validate();

        Assert.DoesNotContain("Sampling breadth must be between 1 and 50 percent", errors);
    }

    [Theory]
    [InlineData("Unsupported")]
    [InlineData("")]
    public void Validate_WhenSamplingModeIsInvalid_ReturnsError(string samplingMode)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            SamplingMode = samplingMode
        };

        var errors = config.Validate();

        Assert.Contains("Sampling mode must be Average, CenterWeighted, or CenterPixel", errors);
    }

    [Theory]
    [InlineData(PluginConfiguration.SamplingModeAverage)]
    [InlineData(PluginConfiguration.SamplingModeCenterWeighted)]
    [InlineData(PluginConfiguration.SamplingModeCenterPixel)]
    [InlineData("centerweighted")]
    public void Validate_WhenSamplingModeIsValid_ReturnsNoSamplingModeError(string samplingMode)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            SamplingMode = samplingMode
        };

        var errors = config.Validate();

        Assert.DoesNotContain("Sampling mode must be Average, CenterWeighted, or CenterPixel", errors);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(91)]
    public void Validate_WhenColorSmoothingIsOutOfRange_ReturnsError(int colorSmoothingPercent)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            ColorSmoothingPercent = colorSmoothingPercent
        };

        var errors = config.Validate();

        Assert.Contains("Color smoothing must be between 0 and 90 percent", errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(90)]
    public void Validate_WhenColorSmoothingIsValid_ReturnsNoSmoothingError(int colorSmoothingPercent)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            ColorSmoothingPercent = colorSmoothingPercent
        };

        var errors = config.Validate();

        Assert.DoesNotContain("Color smoothing must be between 0 and 90 percent", errors);
    }

    [Fact]
    public void Validate_WhenUserMappingsExist_SkipsDefaultBridgeValidation()
    {
        // Arrange — sync enabled, empty default fields, but a valid user mapping
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "",
            HueAppKey = "",
            HueClientKey = "",
            EntertainmentAreaId = "",
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = "user-1",
                    HueBridgeIp = "192.168.1.100",
                    HueAppKey = "app-key",
                    HueClientKey = "client-key",
                    EntertainmentAreaId = "area-1"
                }
            }
        };

        // Act
        var errors = config.Validate();

        // Assert — no bridge-related errors since user mappings cover it
        Assert.DoesNotContain("Hue Bridge IP is required", errors);
        Assert.DoesNotContain("Hue App Key is required", errors);
        Assert.DoesNotContain("Hue Client Key is required", errors);
        Assert.DoesNotContain("Entertainment Area ID is required", errors);
        Assert.True(config.IsValid());
    }

    [Fact]
    public void Validate_WhenUserMappingsEmptyIp_StillRequiresDefaultBridge()
    {
        // Arrange — user mapping exists but has empty bridge IP
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "",
            HueAppKey = "",
            HueClientKey = "",
            EntertainmentAreaId = "",
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = "user-1",
                    HueBridgeIp = "", // empty — doesn't count
                }
            }
        };

        // Act
        var errors = config.Validate();

        // Assert — should still require default bridge fields
        Assert.Contains("Hue Bridge IP is required when sync is enabled", errors);
    }

    [Fact]
    public void Validate_WhenUserMappingDisablesSync_AllowsMissingBridgeCredentials()
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "default-key",
            HueClientKey = "default-client",
            EntertainmentAreaId = "default-area",
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = "user-1",
                    SyncEnabled = false,
                    HueBridgeIp = "not-a-bridge",
                    HueAppKey = "",
                    HueClientKey = "",
                    EntertainmentAreaId = ""
                }
            }
        };

        var errors = config.Validate();

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_WhenOnlyDisabledUserMappingsExist_StillRequiresDefaultBridge()
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = "user-1",
                    SyncEnabled = false
                }
            }
        };

        var errors = config.Validate();

        Assert.Contains("Hue Bridge IP is required when sync is enabled", errors);
    }

    [Fact]
    public void GetBridgeConfigForUser_WithMatchingMapping_ReturnsUserConfig()
    {
        // Arrange
        var userId = System.Guid.NewGuid();
        var config = new PluginConfiguration
        {
            HueBridgeIp = "10.0.0.1",
            HueAppKey = "default-key",
            HueClientKey = "default-client",
            EntertainmentAreaId = "default-area",
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = userId.ToString(),
                    HueBridgeIp = "192.168.1.200",
                    HueAppKey = "user-key",
                    HueClientKey = "user-client",
                    EntertainmentAreaId = "user-area"
                }
            }
        };

        // Act
        var (bridgeIp, appKey, clientKey, areaId) = config.GetBridgeConfigForUser(userId);

        // Assert
        Assert.Equal("192.168.1.200", bridgeIp);
        Assert.Equal("user-key", appKey);
        Assert.Equal("user-client", clientKey);
        Assert.Equal("user-area", areaId);
    }

    [Fact]
    public void GetBridgeConfigForUser_MatchesMappingCaseInsensitively()
    {
        var userId = System.Guid.NewGuid();
        var config = new PluginConfiguration
        {
            HueBridgeIp = "10.0.0.1",
            HueAppKey = "default-key",
            HueClientKey = "default-client",
            EntertainmentAreaId = "default-area",
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = userId.ToString().ToUpperInvariant(),
                    HueBridgeIp = "192.168.1.200",
                    HueAppKey = "user-key",
                    HueClientKey = "user-client",
                    EntertainmentAreaId = "user-area"
                }
            }
        };

        var result = config.GetBridgeConfigForUser(userId);

        Assert.Equal("192.168.1.200", result.BridgeIp);
        Assert.Equal("user-key", result.AppKey);
    }

    [Fact]
    public void Validate_WhenUserMappingIsIncomplete_ReturnsMappingErrors()
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = "user-1",
                    HueBridgeIp = "not-an-ip",
                    HueAppKey = "",
                    HueClientKey = "",
                    EntertainmentAreaId = ""
                }
            }
        };

        var errors = config.Validate();

        Assert.Contains("User mapping 1 bridge address must be a valid private IP address or .local host name", errors);
        Assert.Contains("User mapping 1 requires a Hue App Key", errors);
        Assert.Contains("User mapping 1 requires a Hue Client Key", errors);
        Assert.Contains("User mapping 1 requires an Entertainment Area ID", errors);
    }

    [Fact]
    public void GetBridgeConfigForUser_WithNoMatchingMapping_ReturnsDefaultConfig()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            HueBridgeIp = "10.0.0.1",
            HueAppKey = "default-key",
            HueClientKey = "default-client",
            EntertainmentAreaId = "default-area",
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = "some-other-user",
                    HueBridgeIp = "192.168.1.200",
                    HueAppKey = "user-key",
                    HueClientKey = "user-client",
                    EntertainmentAreaId = "user-area"
                }
            }
        };

        // Act
        var (bridgeIp, appKey, clientKey, areaId) = config.GetBridgeConfigForUser(System.Guid.NewGuid());

        // Assert — falls back to default
        Assert.Equal("10.0.0.1", bridgeIp);
        Assert.Equal("default-key", appKey);
        Assert.Equal("default-client", clientKey);
        Assert.Equal("default-area", areaId);
    }

    [Fact]
    public void GetBridgeConfigForUser_WithEmptyBridgeIpMapping_ReturnsDefaultConfig()
    {
        // Arrange — mapping exists for user but has empty bridge IP
        var userId = System.Guid.NewGuid();
        var config = new PluginConfiguration
        {
            HueBridgeIp = "10.0.0.1",
            HueAppKey = "default-key",
            HueClientKey = "default-client",
            EntertainmentAreaId = "default-area",
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = userId.ToString(),
                    HueBridgeIp = "",
                }
            }
        };

        // Act
        var (bridgeIp, appKey, clientKey, areaId) = config.GetBridgeConfigForUser(userId);

        // Assert — falls back to default because mapping has empty bridge IP
        Assert.Equal("10.0.0.1", bridgeIp);
        Assert.Equal("default-key", appKey);
    }

    [Fact]
    public void IsSyncEnabledForUser_UsesMappingOptOutAndDefaultsUnmappedUsersToEnabled()
    {
        var disabledUser = System.Guid.NewGuid();
        var config = new PluginConfiguration
        {
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = disabledUser.ToString().ToUpperInvariant(),
                    SyncEnabled = false
                }
            }
        };

        Assert.False(config.IsSyncEnabledForUser(disabledUser));
        Assert.True(config.IsSyncEnabledForUser(System.Guid.NewGuid()));
    }

    [Fact]
    public void GetColorProcessingOverridesForUser_UsesMatchingMappingAndLeavesBlankValuesForGlobalSettings()
    {
        var userId = System.Guid.NewGuid();
        var config = new PluginConfiguration
        {
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = userId.ToString().ToUpperInvariant(),
                    BrightnessBoostOverride = 150,
                    RedGainOverride = 120,
                    GreenGainOverride = 90,
                    BlueGainOverride = 110,
                    ColorSaturationOverride = 0,
                    HueShiftDegreesOverride = -45,
                    OutputBrightnessPercentOverride = 75,
                    BlackoutThresholdOverride = 0,
                    ColorChangeThresholdOverride = 255
                }
            }
        };

        var overrides = config.GetColorProcessingOverridesForUser(userId);
        var unmappedOverrides = config.GetColorProcessingOverridesForUser(System.Guid.NewGuid());

        Assert.Equal((int?)150, overrides.BrightnessBoost);
        var channelGainOverrides = config.GetColorChannelGainOverridesForUser(userId);
        var unmappedChannelGainOverrides = config.GetColorChannelGainOverridesForUser(System.Guid.NewGuid());
        Assert.Equal((int?)120, channelGainOverrides.RedGain);
        Assert.Equal((int?)90, channelGainOverrides.GreenGain);
        Assert.Equal((int?)110, channelGainOverrides.BlueGain);
        Assert.Null(unmappedChannelGainOverrides.RedGain);
        Assert.Null(unmappedChannelGainOverrides.GreenGain);
        Assert.Null(unmappedChannelGainOverrides.BlueGain);
        Assert.Equal((int?)0, overrides.ColorSaturation);
        Assert.Equal((int?)-45, overrides.HueShiftDegrees);
        Assert.Equal((int?)75, overrides.OutputBrightnessPercent);
        var thresholdOverrides = config.GetColorThresholdOverridesForUser(userId);
        var unmappedThresholdOverrides = config.GetColorThresholdOverridesForUser(System.Guid.NewGuid());
        Assert.Equal((int?)0, thresholdOverrides.BlackoutThreshold);
        Assert.Equal((int?)255, thresholdOverrides.ColorChangeThreshold);
        Assert.Null(unmappedThresholdOverrides.BlackoutThreshold);
        Assert.Null(unmappedThresholdOverrides.ColorChangeThreshold);
        Assert.Null(unmappedOverrides.BrightnessBoost);
        Assert.Null(unmappedOverrides.ColorSaturation);
        Assert.Null(unmappedOverrides.HueShiftDegrees);
        Assert.Null(unmappedOverrides.OutputBrightnessPercent);
    }

    [Fact]
    public void GetPerformanceOverridesForUser_UsesMatchingMappingAndLeavesBlankValuesForGlobalSettings()
    {
        var userId = System.Guid.NewGuid();
        var config = new PluginConfiguration
        {
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = userId.ToString().ToUpperInvariant(),
                    TargetFpsOverride = 30,
                    FrameResolutionOverride = " 320x180 ",
                    VideoScalingModeOverride = "fit",
                    VideoDeinterlaceModeOverride = "Auto",
                    SamplingBreadthPercentOverride = 25,
                    SamplingModeOverride = "CenterWeighted",
                    ColorSmoothingPercentOverride = 40
                }
            }
        };

        var overrides = config.GetPerformanceOverridesForUser(userId);
        var unmapped = config.GetPerformanceOverridesForUser(System.Guid.NewGuid());

        Assert.Equal((int?)30, overrides.TargetFps);
        Assert.Equal("320x180", overrides.FrameResolution);
        Assert.Equal("fit", overrides.VideoScalingMode);
        Assert.Equal("Auto", overrides.VideoDeinterlaceMode);
        Assert.Equal((int?)25, overrides.SamplingBreadthPercent);
        Assert.Equal("CenterWeighted", overrides.SamplingMode);
        Assert.Equal((int?)40, overrides.ColorSmoothingPercent);
        Assert.Null(unmapped.TargetFps);
        Assert.Null(unmapped.FrameResolution);
        Assert.Null(unmapped.VideoScalingMode);
        Assert.Null(unmapped.VideoDeinterlaceMode);
        Assert.Null(unmapped.SamplingBreadthPercent);
        Assert.Null(unmapped.SamplingMode);
        Assert.Null(unmapped.ColorSmoothingPercent);
    }

    [Fact]
    public void GetExecutionOverridesForUser_UsesMatchingMappingAndNormalizesOptionalValues()
    {
        var userId = System.Guid.NewGuid();
        var config = new PluginConfiguration
        {
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = userId.ToString().ToUpperInvariant(),
                    UseGpuOverride = false,
                    CustomFfmpegFlagsOverride = "  -hwaccel vaapi  ",
                    FfmpegStallTimeoutSecondsOverride = 12,
                    NetworkRetryAttemptsOverride = 1
                }
            }
        };

        var overrides = config.GetExecutionOverridesForUser(userId);
        var unmapped = config.GetExecutionOverridesForUser(System.Guid.NewGuid());

        Assert.Equal((bool?)false, overrides.UseGpu);
        Assert.Equal("-hwaccel vaapi", overrides.CustomFfmpegFlags);
        Assert.Equal((int?)12, overrides.FfmpegStallTimeoutSeconds);
        Assert.Equal((int?)1, overrides.NetworkRetryAttempts);
        Assert.Null(unmapped.UseGpu);
        Assert.Null(unmapped.CustomFfmpegFlags);
        Assert.Null(unmapped.FfmpegStallTimeoutSeconds);
        Assert.Null(unmapped.NetworkRetryAttempts);
    }

    [Fact]
    public void GetChannelIdsOverrideForUser_ParsesDelimitedIdsAndLeavesUnmappedUsersGlobal()
    {
        var userId = System.Guid.NewGuid();
        var config = new PluginConfiguration
        {
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = userId.ToString().ToUpperInvariant(),
                    ChannelIdsOverride = " 12, 4;12 65535 "
                }
            }
        };

        var channelIds = config.GetChannelIdsOverrideForUser(userId);
        var unmapped = config.GetChannelIdsOverrideForUser(System.Guid.NewGuid());

        Assert.NotNull(channelIds);
        Assert.Equal(3, channelIds!.Count);
        Assert.Contains(4, channelIds);
        Assert.Contains(12, channelIds);
        Assert.Contains(65535, channelIds);
        Assert.Null(unmapped);
    }

    [Fact]
    public void GetChannelIdsForUser_InheritsGlobalSelectionUnlessUserOverridesIt()
    {
        var userId = System.Guid.NewGuid();
        var inheritedUserId = System.Guid.NewGuid();
        var configuration = new PluginConfiguration
        {
            ChannelIds = "7, 3, 7",
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new()
                {
                    UserId = userId.ToString(),
                    ChannelIdsOverride = "12, 4"
                },
                new()
                {
                    UserId = inheritedUserId.ToString()
                }
            }
        };

        var explicitSelection = configuration.GetChannelIdsForUser(userId);
        var inheritedSelection = configuration.GetChannelIdsForUser(inheritedUserId);
        var unmappedSelection = configuration.GetChannelIdsForUser(System.Guid.NewGuid());

        Assert.Equal(new[] { 4, 12 }, explicitSelection!.OrderBy(channelId => channelId));
        Assert.Equal(new[] { 3, 7 }, inheritedSelection!.OrderBy(channelId => channelId));
        Assert.Equal(new[] { 3, 7 }, unmappedSelection!.OrderBy(channelId => channelId));
    }

    [Fact]
    public void Validate_WhenGlobalChannelSelectionIsMalformed_ReturnsChannelProfileError()
    {
        var configuration = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "default-app-key",
            HueClientKey = "default-client-key",
            EntertainmentAreaId = "default-area",
            ChannelIds = "1, nope, 70000"
        };

        var errors = configuration.Validate();

        Assert.Contains("Global channel IDs must be a comma-separated list of IDs from 0 to 65535", errors);
    }

    [Fact]
    public void Validate_WhenUserChannelSelectionIsMalformed_ReturnsChannelProfileError()
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "default-app-key",
            HueClientKey = "default-client-key",
            EntertainmentAreaId = "default-area",
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = "user-1",
                    ChannelIdsOverride = "1, nope, 70000"
                }
            }
        };

        var errors = config.Validate();

        Assert.Contains("User mapping 1 channel IDs override must be a comma-separated list of IDs from 0 to 65535", errors);
    }

    [Fact]
    public void GetPlaybackOverridesForUser_UsesMatchingMappingAndLeavesBlankValuesForGlobalSettings()
    {
        var userId = System.Guid.NewGuid();
        var config = new PluginConfiguration
        {
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = userId.ToString().ToUpperInvariant(),
                    UseCinemaModeOverride = false,
                    BrightnessDimLevelOverride = 10,
                    PauseBehaviorOverride = PluginConfiguration.PauseBehaviorRestoreLightState,
                    RestoreLightStateOverride = false
                }
            }
        };

        var overrides = config.GetPlaybackOverridesForUser(userId);
        var pauseBehaviorOverride = config.GetPauseBehaviorOverrideForUser(userId);
        var unmappedOverrides = config.GetPlaybackOverridesForUser(System.Guid.NewGuid());
        var unmappedPauseBehaviorOverride = config.GetPauseBehaviorOverrideForUser(System.Guid.NewGuid());

        Assert.Equal((bool?)false, overrides.UseCinemaMode);
        Assert.Equal((int?)10, overrides.BrightnessDimLevel);
        Assert.Equal((bool?)false, overrides.RestoreLightState);
        Assert.Equal(PluginConfiguration.PauseBehaviorRestoreLightState, pauseBehaviorOverride);
        Assert.Null(unmappedOverrides.UseCinemaMode);
        Assert.Null(unmappedOverrides.BrightnessDimLevel);
        Assert.Null(unmappedOverrides.RestoreLightState);
        Assert.Null(unmappedPauseBehaviorOverride);
    }

    [Fact]
    public void Validate_WhenUserColorProfileOverridesAreOutOfRange_ReturnsProfileErrors()
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "default-app-key",
            HueClientKey = "default-client-key",
            EntertainmentAreaId = "default-area",
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = "user-1",
                    BrightnessBoostOverride = 49,
                    RedGainOverride = 49,
                    GreenGainOverride = 201,
                    BlueGainOverride = 0,
                    ColorSaturationOverride = 201,
                    HueShiftDegreesOverride = 181,
                    OutputBrightnessPercentOverride = -1,
                    BlackoutThresholdOverride = -1,
                    ColorChangeThresholdOverride = 256
                }
            }
        };

        var errors = config.Validate();

        Assert.Contains("User mapping 1 brightness boost override must be between 50 and 200", errors);
        Assert.Contains("User mapping 1 red gain override must be between 50 and 200", errors);
        Assert.Contains("User mapping 1 green gain override must be between 50 and 200", errors);
        Assert.Contains("User mapping 1 blue gain override must be between 50 and 200", errors);
        Assert.Contains("User mapping 1 color saturation override must be between 0 and 200", errors);
        Assert.Contains("User mapping 1 hue shift override must be between -180 and 180 degrees", errors);
        Assert.Contains("User mapping 1 output brightness override must be between 0 and 100 percent", errors);
        Assert.Contains("User mapping 1 blackout threshold override must be between 0 and 255", errors);
        Assert.Contains("User mapping 1 color change threshold override must be between 0 and 255", errors);
    }

    [Fact]
    public void Validate_WhenUserPerformanceOverridesAreInvalid_ReturnsPerformanceErrors()
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "default-app-key",
            HueClientKey = "default-client-key",
            EntertainmentAreaId = "default-area",
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = "user-1",
                    TargetFpsOverride = 0,
                    FrameResolutionOverride = "640x360",
                    VideoScalingModeOverride = "InvalidScaling",
                    VideoDeinterlaceModeOverride = "InvalidDeinterlace",
                    SamplingBreadthPercentOverride = 51,
                    SamplingModeOverride = "InvalidSampling",
                    ColorSmoothingPercentOverride = 91
                }
            }
        };

        var errors = config.Validate();

        Assert.Contains("User mapping 1 target FPS override must be between 1 and 60", errors);
        Assert.Contains("User mapping 1 frame resolution override must be 80x45, 160x90, or 320x180", errors);
        Assert.Contains("User mapping 1 video scaling override must be Stretch, Fit, or Crop", errors);
        Assert.Contains("User mapping 1 video deinterlace override must be Off, Auto, or On", errors);
        Assert.Contains("User mapping 1 sampling breadth override must be between 1 and 50 percent", errors);
        Assert.Contains("User mapping 1 sampling mode override must be Average, CenterWeighted, or CenterPixel", errors);
        Assert.Contains("User mapping 1 color smoothing override must be between 0 and 90 percent", errors);
    }

    [Fact]
    public void Validate_WhenUserExecutionOverridesAreInvalid_ReturnsExecutionErrors()
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "default-app-key",
            HueClientKey = "default-client-key",
            EntertainmentAreaId = "default-area",
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = "user-1",
                    FfmpegStallTimeoutSecondsOverride = 0,
                    NetworkRetryAttemptsOverride = 11
                }
            }
        };

        var errors = config.Validate();

        Assert.Contains("User mapping 1 FFmpeg stall timeout override must be between 1 and 60 seconds", errors);
        Assert.Contains("User mapping 1 network retry attempts override must be between 0 and 10", errors);
    }

    [Fact]
    public void Validate_WhenUserPlaybackOverridesAreOutOfRange_ReturnsProfileErrors()
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "default-app-key",
            HueClientKey = "default-client-key",
            EntertainmentAreaId = "default-area",
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = "user-1",
                    BrightnessDimLevelOverride = 101,
                    PauseBehaviorOverride = "InvalidPauseBehavior"
                }
            }
        };

        var errors = config.Validate();

        Assert.Contains("User mapping 1 brightness dim level override must be between 0 and 100", errors);
        Assert.Contains("User mapping 1 pause behavior override must be KeepLastColors or RestoreLightState", errors);
    }

    [Fact]
    public void Validate_WhenGlobalColorChannelGainsAreOutOfRange_ReturnsGainErrors()
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "default-app-key",
            HueClientKey = "default-client-key",
            EntertainmentAreaId = "default-area",
            RedGain = 49,
            GreenGain = 201,
            BlueGain = 0
        };

        var errors = config.Validate();

        Assert.Contains("Red gain must be between 50 and 200", errors);
        Assert.Contains("Green gain must be between 50 and 200", errors);
        Assert.Contains("Blue gain must be between 50 and 200", errors);
    }
}
