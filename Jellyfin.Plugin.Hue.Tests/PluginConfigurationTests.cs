using Jellyfin.Plugin.Hue.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

public class PluginConfigurationTests
{
    [Fact]
    public void PlaybackMediaFilter_DefaultsToAllVideoAndNormalizesCase()
    {
        var config = new PluginConfiguration();

        Assert.Equal(PluginConfiguration.PlaybackMediaFilterAllVideo, config.PlaybackMediaFilter);
        Assert.True(PluginConfiguration.TryNormalizePlaybackMediaFilter(" episodes ", out var normalized));
        Assert.Equal(PluginConfiguration.PlaybackMediaFilterEpisodes, normalized);
        Assert.Empty(config.Validate());
    }

    [Fact]
    public void PlaybackMediaFilter_RejectsUnknownValues()
    {
        var config = new PluginConfiguration { PlaybackMediaFilter = "Trailers" };

        Assert.False(PluginConfiguration.TryNormalizePlaybackMediaFilter(config.PlaybackMediaFilter, out _));
        Assert.Contains("Playback media scope must be AllVideo, Movies, Episodes, OtherVideo, Audio, or AllMedia", config.Validate());
    }

    [Fact]
    public void PlaybackMediaFilter_PerUserOverrideInheritsOrReplacesGlobalScope()
    {
        var userId = Guid.NewGuid();
        var config = new PluginConfiguration
        {
            PlaybackMediaFilter = PluginConfiguration.PlaybackMediaFilterMovies,
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = userId.ToString() }
            }
        };

        Assert.Null(config.GetPlaybackMediaFilterOverrideForUser(userId));
        Assert.Equal(PluginConfiguration.PlaybackMediaFilterMovies, config.GetPlaybackMediaFilterForUser(userId));

        config.UserMappings[0].PlaybackMediaFilterOverride = " episodes ";

        Assert.Equal(PluginConfiguration.PlaybackMediaFilterEpisodes, config.GetPlaybackMediaFilterOverrideForUser(userId));
        Assert.Equal(PluginConfiguration.PlaybackMediaFilterEpisodes, config.GetPlaybackMediaFilterForUser(userId));
        Assert.Empty(config.Validate());
    }

    [Fact]
    public void PlaybackMediaFilter_PerUserOverrideRejectsUnknownValues()
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "app-key",
            HueClientKey = "client-key",
            EntertainmentAreaId = "area-1",
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = "user-1", PlaybackMediaFilterOverride = "Trailers" }
            }
        };

        Assert.Contains("User mapping 1 playback media scope override must be AllVideo, Movies, Episodes, OtherVideo, Audio, or AllMedia", config.Validate());
    }

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
                    TransitionSeconds = 3,
                    TransitionOutSeconds = 2
                }
            }
        };

        Assert.Empty(config.ValidateColorPresets());
        Assert.Empty(config.Validate());
    }

    [Fact]
    public void ValidateColorPresets_AllowsSupportedEffectsAndNormalizesLegacyBlank()
    {
        foreach (var effect in new[]
        {
            PluginConfiguration.ColorPresetEffectSolid,
            PluginConfiguration.ColorPresetEffectPulse,
            PluginConfiguration.ColorPresetEffectRainbow,
            PluginConfiguration.ColorPresetEffectCandle,
            PluginConfiguration.ColorPresetEffectTemperature,
            PluginConfiguration.ColorPresetEffectAurora,
            " rainbow "
        })
        {
            Assert.Empty(PluginConfiguration.ValidateColorPreset(new HueColorPreset
            {
                Name = effect,
                Effect = effect
            }));
        }

        Assert.True(PluginConfiguration.TryNormalizeColorPresetEffect(string.Empty, out var normalized));
        Assert.Equal(PluginConfiguration.ColorPresetEffectSolid, normalized);
    }

    [Fact]
    public void ValidateColorPresets_RejectsInvalidValuesAndDuplicateNames()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Accent", Red = 256, DurationSeconds = 0, TransitionSeconds = 31, TransitionOutSeconds = 31 },
                new() { Name = " accent " }
            }
        };

        var errors = config.ValidateColorPresets();

        Assert.Contains("Color preset 1 RGB values must be between 0 and 255", errors);
        Assert.Contains("Color preset 1 duration must be between 1 and 30 seconds", errors);
        Assert.Contains("Color preset 1 transition must be between 0 and 30 seconds", errors);
        Assert.Contains("Color preset 1 fade-out must be between 0 and 30 seconds", errors);
        Assert.Contains("Color preset 2 duplicates another color preset name", errors);
    }

    [Fact]
    public void ValidateColorPreset_RejectsUnknownEffect()
    {
        var errors = PluginConfiguration.ValidateColorPreset(new HueColorPreset
        {
            Name = "Unknown effect",
            Effect = "Strobe"
        });

        Assert.Contains("Color preset effect must be one of Solid, Pulse, Rainbow, Candle, Temperature, Aurora", errors);
    }

    [Fact]
    public void ValidateColorPreset_RejectsEffectSpeedOutsideBounds()
    {
        var tooSlow = PluginConfiguration.ValidateColorPreset(new HueColorPreset
        {
            Name = "Too slow",
            Effect = PluginConfiguration.ColorPresetEffectPulse,
            EffectSpeedPercent = PluginConfiguration.MinColorPresetEffectSpeedPercent - 1
        });
        var tooFast = PluginConfiguration.ValidateColorPreset(new HueColorPreset
        {
            Name = "Too fast",
            Effect = PluginConfiguration.ColorPresetEffectRainbow,
            EffectSpeedPercent = PluginConfiguration.MaxColorPresetEffectSpeedPercent + 1
        });

        Assert.Contains("Color preset effect speed must be between 25 and 400 percent", tooSlow);
        Assert.Contains("Color preset effect speed must be between 25 and 400 percent", tooFast);
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

        Assert.Contains("Color preset transition cannot exceed the scene duration", errors);
    }

    [Fact]
    public void ValidateColorPresets_RejectsCombinedTransitionsLongerThanScene()
    {
        var errors = PluginConfiguration.ValidateColorPreset(new HueColorPreset
        {
            Name = "Bookend fades",
            DurationSeconds = 5,
            TransitionSeconds = 3,
            TransitionOutSeconds = 3
        });

        Assert.Contains("Color preset fade-in and fade-out cannot exceed the scene duration together", errors);
    }

    [Fact]
    public void ValidateScenePlaylists_AllowsOrderedScenesAndEnabledMappingTarget()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Welcome" },
                new() { Name = "Movie Night", DurationSeconds = 8 }
            },
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = "user-1", UserName = "Living Room", SyncEnabled = true }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "playlist-1",
                    Name = "Arrival sequence",
                    PresetNames = new List<string> { "Welcome", "Movie Night", "Welcome" },
                    TargetUserId = "user-1"
                }
            }
        };

        Assert.Empty(config.ValidateScenePlaylists());
        Assert.Empty(config.Validate());
    }

    [Fact]
    public void ValidateScenePlaylists_AllowsBoundedRepeatsAndRejectsUnsafeTotals()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset>
            {
                new() { Name = "Warm", DurationSeconds = 20 },
                new() { Name = "Cool", DurationSeconds = 20 }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "playlist-repeat",
                    Name = "Repeated sequence",
                    PresetNames = new List<string> { "Warm", "Cool" },
                    RepeatCount = 5
                }
            }
        };

        Assert.Empty(config.ValidateScenePlaylists());

        config.ScenePlaylists[0].RepeatCount = PluginConfiguration.MaxScenePlaylistRepeatCount + 1;
        Assert.Contains("Scene playlist 1 repeat count must be between 1 and 10", config.ValidateScenePlaylists());

        config.ScenePlaylists[0].RepeatCount = 10;
        config.ColorPresets.Add(new HueColorPreset { Name = "Finale", DurationSeconds = 30 });
        config.ScenePlaylists[0].PresetNames.Add("Finale");
        Assert.Contains("Scene playlist 1 repeated duration cannot exceed 600 seconds", config.ValidateScenePlaylists());
    }

    [Fact]
    public void ValidateScenePlaylists_RejectsMissingScenesInvalidTargetAndDuplicateIdentity()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Welcome" } },
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = "disabled", SyncEnabled = false }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "playlist-1",
                    Name = "Sequence",
                    PresetNames = new List<string> { "Welcome", "Missing" },
                    TargetUserId = "disabled",
                    TargetAllEnabledMappings = true
                },
                new()
                {
                    Id = "PLAYLIST-1",
                    Name = "sequence",
                    PresetNames = new List<string> { "Welcome" }
                }
            }
        };

        var errors = config.ValidateScenePlaylists();

        Assert.Contains("Scene playlist 1 references a saved scene that does not exist: Missing", errors);
        Assert.Contains("Scene playlist 1 cannot select all enabled targets and a specific user mapping together", errors);
        Assert.Contains("Scene playlist 1 references a disabled user mapping", errors);
        Assert.Contains("Scene playlist 2 duplicates another scene playlist ID", errors);
        Assert.Contains("Scene playlist 2 duplicates another scene playlist name", errors);
    }

    [Fact]
    public void ValidateScenePlaylists_AllowsSelectedTargetsAndRejectsInvalidCombinations()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Welcome" } },
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "global-app",
            EntertainmentAreaId = "global-area",
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = "user-1", UserName = "Kitchen", SyncEnabled = true },
                new() { UserId = "user-2", UserName = "Bedroom", SyncEnabled = true },
                new() { UserId = "disabled", UserName = "Disabled", SyncEnabled = false }
            },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new()
                {
                    Id = "selected-playlist",
                    Name = "Selected sequence",
                    PresetNames = new List<string> { "Welcome" },
                    TargetUserIds = new List<string> { " user-1 ", "USER-2" },
                    IncludeDefaultTarget = true
                }
            }
        };

        Assert.Empty(config.ValidateScenePlaylists());

        config.ScenePlaylists[0].TargetUserIds.Add("user-1");
        var duplicateErrors = config.ValidateScenePlaylists();
        Assert.Contains("Scene playlist 1 selects user mapping user-1 more than once", duplicateErrors);

        config.ScenePlaylists[0].TargetUserIds = new List<string> { "disabled" };
        var disabledErrors = config.ValidateScenePlaylists();
        Assert.Contains("Scene playlist 1 references a disabled selected user mapping: disabled", disabledErrors);

        config.ScenePlaylists[0].TargetUserIds = new List<string> { "user-1" };
        config.ScenePlaylists[0].TargetAllEnabledMappings = true;
        var mixedErrors = config.ValidateScenePlaylists();
        Assert.Contains("Scene playlist 1 cannot combine all enabled targets with a specific or selected target", mixedErrors);
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
    public void ValidateSceneSchedules_AllowsSavedPlaylistAndRejectsScenePlaylistAmbiguity()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Warm" } },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new() { Id = "playlist-1", Name = "Evening sequence", PresetNames = new List<string> { "Warm" } }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "playlist-cue", Name = "Playlist cue", PlaylistName = " evening sequence ", TimeZoneId = TimeZoneInfo.Utc.Id },
            }
        };

        Assert.Empty(config.ValidateSceneSchedules());

        config.SceneSchedules.Add(new HueSceneSchedule
        {
            Id = "ambiguous",
            Name = "Ambiguous cue",
            PresetName = "Warm",
            PlaylistName = "Evening sequence"
        });
        Assert.Contains(
            "Scene schedule 2 cannot reference both a saved scene and a playlist",
            config.ValidateSceneSchedules());
    }

    [Fact]
    public void ValidateSceneSchedules_RejectsPlaylistDurationOverrideAndMissingContent()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Warm" } },
            ScenePlaylists = new List<HueScenePlaylist>
            {
                new() { Id = "playlist-1", Name = "Evening sequence", PresetNames = new List<string> { "Warm" } }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new() { Id = "bad-playlist", Name = "Bad playlist", PlaylistName = "Evening sequence", DurationSeconds = 5 },
                new() { Id = "missing-content", Name = "Missing content" }
            }
        };

        var errors = config.ValidateSceneSchedules();
        Assert.Contains("Scene schedule 1 playlist duration override must be 0; each saved scene keeps its own duration", errors);
        Assert.Contains("Scene schedule 2 requires a saved scene or playlist", errors);
    }

    [Fact]
    public void ValidateSceneSchedules_AllowsBroadcastTarget()
    {
        var config = new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "app-key",
            HueClientKey = "client-key",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-1",
                    UserName = "Kitchen",
                    SyncEnabled = true,
                    HueBridgeIp = "192.168.1.101",
                    HueAppKey = "mapping-app",
                    HueClientKey = "mapping-client",
                    EntertainmentAreaId = "area-2"
                }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "broadcast",
                    Name = "House welcome",
                    PresetName = "Evening",
                    TargetAllEnabledMappings = true
                }
            }
        };

        Assert.Empty(config.ValidateSceneSchedules());
    }

    [Fact]
    public void ValidateSceneSchedules_RejectsBroadcastTargetWithSpecificMapping()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = "user-1", SyncEnabled = true }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "conflict",
                    Name = "Conflicting cue",
                    PresetName = "Evening",
                    TargetAllEnabledMappings = true,
                    TargetUserId = "user-1"
                }
            }
        };

        Assert.Contains(
            "Scene schedule 1 cannot select all enabled targets and a specific user mapping together",
            config.ValidateSceneSchedules());
    }

    [Fact]
    public void ValidateSceneSchedules_AllowsSelectedTargetsAndRejectsInvalidCombinations()
    {
        var config = new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "app-key",
            HueClientKey = "client-key",
            EntertainmentAreaId = "area-1",
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = "user-1", UserName = "Kitchen", SyncEnabled = true },
                new() { UserId = "user-2", UserName = "Disabled", SyncEnabled = false }
            },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "selected",
                    Name = "Selected cue",
                    PresetName = "Evening",
                    TargetUserIds = new List<string> { "user-1" },
                    IncludeDefaultTarget = true
                }
            }
        };

        Assert.Empty(config.ValidateSceneSchedules());

        config.SceneSchedules[0].TargetUserIds = new List<string> { "user-1", "user-1" };
        var duplicateErrors = config.ValidateSceneSchedules();
        Assert.Contains(duplicateErrors, error => error.Contains("selects user mapping user-1 more than once", StringComparison.Ordinal));

        config.SceneSchedules[0].TargetUserIds = new List<string> { "user-2" };
        var disabledErrors = config.ValidateSceneSchedules();
        Assert.Contains(disabledErrors, error => error.Contains("references a disabled selected user mapping: user-2", StringComparison.Ordinal));

        config.SceneSchedules[0].TargetUserIds = new List<string> { "user-1" };
        config.SceneSchedules[0].TargetAllEnabledMappings = true;
        var mixedErrors = config.ValidateSceneSchedules();
        Assert.Contains(mixedErrors, error => error.Contains("cannot combine all enabled targets with a specific or selected target", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateSceneSchedules_RejectsPriorityOutsideBounds()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "too-high",
                    Name = "Too high",
                    PresetName = "Evening",
                    Priority = PluginConfiguration.MaxSceneSchedulePriority + 1
                },
                new()
                {
                    Id = "too-low",
                    Name = "Too low",
                    PresetName = "Evening",
                    Priority = PluginConfiguration.MinSceneSchedulePriority - 1
                }
            }
        };

        var errors = config.ValidateSceneSchedules();

        Assert.Contains($"Scene schedule 1 priority must be between {PluginConfiguration.MinSceneSchedulePriority} and {PluginConfiguration.MaxSceneSchedulePriority}", errors);
        Assert.Contains($"Scene schedule 2 priority must be between {PluginConfiguration.MinSceneSchedulePriority} and {PluginConfiguration.MaxSceneSchedulePriority}", errors);
    }

    [Fact]
    public void ValidateSceneSchedules_AllowsPerCuePlaybackPolicyAndRejectsUnknownValue()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "defer-cue",
                    Name = "Defer cue",
                    PresetName = "Evening",
                    PlaybackPolicy = PluginConfiguration.SceneAutomationPlaybackPolicyDefer
                }
            }
        };

        Assert.Empty(config.ValidateSceneSchedules());
        Assert.True(PluginConfiguration.TryNormalizeSceneAutomationSchedulePlaybackPolicy(
            " inherit ",
            out var normalized));
        Assert.Equal(PluginConfiguration.SceneAutomationPlaybackPolicyInherit, normalized);

        config.SceneSchedules[0].PlaybackPolicy = "Queue";
        Assert.Contains(
            "Scene schedule 1 playback policy must be Inherit, Skip, or Defer",
            config.ValidateSceneSchedules());
        Assert.False(PluginConfiguration.TryNormalizeSceneAutomationSchedulePlaybackPolicy("Queue", out _));
    }

    [Fact]
    public void Validate_RejectsSceneAutomationCatchUpWindowOutsideBounds()
    {
        var tooLarge = new PluginConfiguration
        {
            SceneAutomationCatchUpMinutes = PluginConfiguration.MaxSceneAutomationCatchUpMinutes + 1
        };
        var tooSmall = new PluginConfiguration
        {
            SceneAutomationCatchUpMinutes = PluginConfiguration.MinSceneAutomationCatchUpMinutes - 1
        };

        Assert.Contains("Scene automation catch-up window must be between 0 and 120 minutes", tooLarge.Validate());
        Assert.Contains("Scene automation catch-up window must be between 0 and 120 minutes", tooSmall.Validate());
    }

    [Fact]
    public void Validate_RejectsInvalidSceneAutomationPlaybackPolicyAndDeferWindow()
    {
        var invalid = new PluginConfiguration
        {
            SceneAutomationPlaybackPolicy = "Queue",
            SceneAutomationPlaybackScope = "GlobalOnly",
            SceneAutomationDeferMinutes = PluginConfiguration.MaxSceneAutomationDeferMinutes + 1
        };

        var errors = invalid.Validate();

        Assert.Contains("Scene automation playback policy must be Skip or Defer", errors);
        Assert.Contains("Scene automation playback scope must be AnyTarget or MatchingTarget", errors);
        Assert.Contains("Scene automation defer window must be between 1 and 120 minutes", errors);
        Assert.True(PluginConfiguration.TryNormalizeSceneAutomationPlaybackPolicy(
            " defer ",
            out var normalized));
        Assert.Equal(PluginConfiguration.SceneAutomationPlaybackPolicyDefer, normalized);
        Assert.False(PluginConfiguration.TryNormalizeSceneAutomationPlaybackPolicy("Queue", out _));
        Assert.True(PluginConfiguration.TryNormalizeSceneAutomationPlaybackScope(
            " matchingtarget ",
            out var normalizedScope));
        Assert.Equal(PluginConfiguration.SceneAutomationPlaybackScopeMatchingTarget, normalizedScope);
        Assert.False(PluginConfiguration.TryNormalizeSceneAutomationPlaybackScope("GlobalOnly", out _));
    }

    [Fact]
    public void HistoryRetention_DefaultsToLegacyWindowsAndRejectsOutOfRangeValues()
    {
        var defaults = new PluginConfiguration();

        Assert.Equal(PluginConfiguration.DefaultSessionHistoryRetentionCount, defaults.SessionHistoryRetentionCount);
        Assert.Equal(PluginConfiguration.DefaultSceneScheduleHistoryRetentionCount, defaults.SceneScheduleHistoryRetentionCount);
        Assert.Equal(defaults.SessionHistoryRetentionCount, defaults.GetSessionHistoryRetentionCount());
        Assert.Equal(defaults.SceneScheduleHistoryRetentionCount, defaults.GetSceneScheduleHistoryRetentionCount());

        var invalid = new PluginConfiguration
        {
            SessionHistoryRetentionCount = PluginConfiguration.MinSessionHistoryRetentionCount - 1,
            SceneScheduleHistoryRetentionCount = PluginConfiguration.MaxSceneScheduleHistoryRetentionCount + 1
        };

        var errors = invalid.Validate();
        Assert.Contains("Session history retention must be between 1 and 25 entries", errors);
        Assert.Contains("Scheduled-scene history retention must be between 1 and 100 entries", errors);
        Assert.Equal(PluginConfiguration.DefaultSessionHistoryRetentionCount, invalid.GetSessionHistoryRetentionCount());
        Assert.Equal(PluginConfiguration.DefaultSceneScheduleHistoryRetentionCount, invalid.GetSceneScheduleHistoryRetentionCount());
    }

    [Fact]
    public void HistoryRetention_UsesConfiguredWindowAndTrimsInvalidRuntimeValuesSafely()
    {
        var config = new PluginConfiguration
        {
            SessionHistoryRetentionCount = 7,
            SceneScheduleHistoryRetentionCount = 42
        };

        Assert.Equal(7, config.GetSessionHistoryRetentionCount());
        Assert.Equal(42, config.GetSceneScheduleHistoryRetentionCount());

        config.SessionHistoryRetentionCount = PluginConfiguration.MaxSessionHistoryRetentionCount + 1;
        config.SceneScheduleHistoryRetentionCount = PluginConfiguration.MinSceneScheduleHistoryRetentionCount - 1;

        Assert.Equal(PluginConfiguration.DefaultSessionHistoryRetentionCount, config.GetSessionHistoryRetentionCount());
        Assert.Equal(PluginConfiguration.DefaultSceneScheduleHistoryRetentionCount, config.GetSceneScheduleHistoryRetentionCount());
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
    public void ValidateSceneSchedules_AllowsFiniteExecutionLimitAndPersistedCount()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "finite-cue",
                    Name = "Finite cue",
                    PresetName = "Evening",
                    MaxRuns = 3,
                    RunCount = 2,
                    DaysOfWeekMask = 127
                }
            }
        };

        Assert.Empty(config.ValidateSceneSchedules());
    }

    [Fact]
    public void ValidateSceneSchedules_RejectsInvalidFiniteExecutionLimit()
    {
        var config = new PluginConfiguration
        {
            ColorPresets = new List<HueColorPreset> { new() { Name = "Evening" } },
            SceneSchedules = new List<HueSceneSchedule>
            {
                new()
                {
                    Id = "negative-limit",
                    Name = "Negative limit",
                    PresetName = "Evening",
                    MaxRuns = -1
                },
                new()
                {
                    Id = "oversized-count",
                    Name = "Oversized count",
                    PresetName = "Evening",
                    RunCount = PluginConfiguration.MaxSceneScheduleRuns + 1
                }
            }
        };

        var errors = config.ValidateSceneSchedules();

        Assert.Contains(errors, error => error.Contains("maximum runs", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("run count", StringComparison.Ordinal));
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
            AudioSensitivityPercent = PluginConfiguration.DefaultAudioSensitivityPercent,
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
    [InlineData(24)]
    [InlineData(401)]
    public void Validate_WhenAudioSensitivityOutOfRange_ReturnsError(int sensitivity)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            AudioSensitivityPercent = sensitivity
        };

        Assert.Contains("Audio sensitivity must be between 25 and 400 percent", config.Validate());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Validate_WhenAudioNoiseGateOutOfRange_ReturnsError(int gate)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            AudioNoiseGatePercent = gate
        };

        Assert.Contains("Audio noise gate must be between 0 and 100 percent", config.Validate());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(201)]
    public void Validate_WhenAudioBandGainOutOfRange_ReturnsError(int gain)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            AudioLowGainPercent = gain
        };

        Assert.Contains("Audio band gains must be between 0 and 200 percent", config.Validate());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(91)]
    public void Validate_WhenAudioResponseSmoothingOutOfRange_ReturnsError(int smoothing)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            AudioResponseSmoothingPercent = smoothing
        };

        Assert.Contains("Audio response smoothing must be between 0 and 90 percent", config.Validate());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Validate_WhenAudioBandSpreadOutOfRange_ReturnsError(int spread)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            AudioBandSpreadPercent = spread
        };

        Assert.Contains("Audio band spread must be between 0 and 100 percent", config.Validate());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Validate_WhenAudioBeatPulseOutOfRange_ReturnsError(int pulse)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            AudioBeatPulsePercent = pulse
        };

        Assert.Contains("Audio beat pulse must be between 0 and 100 percent", config.Validate());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Validate_WhenAudioBeatPulseDecayOutOfRange_ReturnsError(int decay)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            AudioBeatPulseDecayPercent = decay
        };

        Assert.Contains("Audio beat pulse decay must be between 0 and 100 percent", config.Validate());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Validate_WhenAudioBeatPulseThresholdOutOfRange_ReturnsError(int threshold)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            AudioBeatPulseThresholdPercent = threshold
        };

        Assert.Contains("Audio beat pulse threshold must be between 0 and 100 percent", config.Validate());
    }

    [Fact]
    public void Validate_WhenAudioColorPaletteIsInvalid_ReturnsError()
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            AudioColorPalette = "InvalidPalette"
        };

        Assert.Contains("Audio color palette must be Spectrum, Band, Warm, Cool, or Monochrome", config.Validate());
    }

    [Fact]
    public void Validate_WhenAudioSpatialModeIsInvalid_ReturnsError()
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            AudioSpatialMode = "InvalidMode"
        };

        Assert.Contains("Audio spatial mode must be Spatial, Uniform, or Mirror", config.Validate());
    }

    [Fact]
    public void Validate_WhenAudioChannelModeIsInvalid_ReturnsError()
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            AudioChannelMode = "InvalidMode"
        };

        Assert.Contains("Audio channel mode must be Mono, Stereo, Left, or Right", config.Validate());
    }

    [Theory]
    [InlineData(19, 420, 1600)]
    [InlineData(90, 420, 3901)]
    public void Validate_WhenAudioFrequencyOutOfRange_ReturnsError(int low, int mid, int high)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            AudioLowFrequencyHz = low,
            AudioMidFrequencyHz = mid,
            AudioHighFrequencyHz = high
        };

        Assert.Contains("Audio frequencies must be between 20 and 3900 Hz", config.Validate());
    }

    [Theory]
    [InlineData(500, 400, 1600)]
    [InlineData(90, 1600, 1500)]
    public void Validate_WhenAudioFrequenciesAreNotOrdered_ReturnsError(int low, int mid, int high)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            AudioLowFrequencyHz = low,
            AudioMidFrequencyHz = mid,
            AudioHighFrequencyHz = high
        };

        Assert.Contains("Audio frequencies must be strictly ordered low < mid < high", config.Validate());
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
    public void Validate_WhenCustomFfmpegFlagsHaveUnterminatedQuote_ReturnsActionableError()
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            CustomFfmpegFlags = "-filter_threads \"1"
        };

        var errors = config.Validate();

        Assert.Contains(
            "Custom FFmpeg flags are invalid: FFmpeg custom flags contain an unterminated quote.",
            errors);
    }

    [Fact]
    public void ValidateExecutionOverrides_WhenCustomFfmpegFlagsHaveUnterminatedQuote_ReturnsActionableError()
    {
        var errors = PluginConfiguration.ValidateExecutionOverrides(
            new UserBridgeMapping
            {
                CustomFfmpegFlagsOverride = "-vf \"scale=160:90"
            },
            "Bedroom mapping");

        Assert.Contains(
            "Bedroom mapping custom FFmpeg flags are invalid: FFmpeg custom flags contain an unterminated quote.",
            errors);
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
        Assert.Equal(PluginConfiguration.DefaultAudioSensitivityPercent, config.AudioSensitivityPercent);
        Assert.Equal(PluginConfiguration.DefaultAudioNoiseGatePercent, config.AudioNoiseGatePercent);
        Assert.Equal(PluginConfiguration.DefaultAudioBandGainPercent, config.AudioLowGainPercent);
        Assert.Equal(PluginConfiguration.DefaultAudioBandGainPercent, config.AudioMidGainPercent);
        Assert.Equal(PluginConfiguration.DefaultAudioBandGainPercent, config.AudioHighGainPercent);
        Assert.Equal(PluginConfiguration.DefaultAudioResponseSmoothingPercent, config.AudioResponseSmoothingPercent);
        Assert.Equal(PluginConfiguration.DefaultAudioLowFrequencyHz, config.AudioLowFrequencyHz);
        Assert.Equal(PluginConfiguration.DefaultAudioMidFrequencyHz, config.AudioMidFrequencyHz);
        Assert.Equal(PluginConfiguration.DefaultAudioHighFrequencyHz, config.AudioHighFrequencyHz);
        Assert.Equal(PluginConfiguration.DefaultAudioBandSpreadPercent, config.AudioBandSpreadPercent);
        Assert.Equal(PluginConfiguration.DefaultAudioBeatPulsePercent, config.AudioBeatPulsePercent);
        Assert.Equal(PluginConfiguration.DefaultAudioBeatPulseDecayPercent, config.AudioBeatPulseDecayPercent);
        Assert.Equal(PluginConfiguration.DefaultAudioBeatPulseThresholdPercent, config.AudioBeatPulseThresholdPercent);
        Assert.Equal(PluginConfiguration.AudioColorPaletteSpectrum, config.AudioColorPalette);
        Assert.Equal(PluginConfiguration.AudioSpatialModeSpatial, config.AudioSpatialMode);
        Assert.Equal(PluginConfiguration.AudioChannelModeMono, config.AudioChannelMode);
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
    public void GetAudioSensitivityPercentForUser_UsesOverrideAndGlobalFallback()
    {
        var userId = System.Guid.NewGuid();
        var config = new PluginConfiguration
        {
            AudioSensitivityPercent = 175,
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = userId.ToString().ToUpperInvariant(),
                    AudioSensitivityPercentOverride = 325
                }
            }
        };

        Assert.Equal(325, config.GetAudioSensitivityPercentForUser(userId));
        Assert.Equal(175, config.GetAudioSensitivityPercentForUser(System.Guid.NewGuid()));
    }

    [Fact]
    public void GetAudioNoiseGatePercentForUser_UsesOverrideAndClampsInvalidValues()
    {
        var userId = System.Guid.NewGuid();
        var config = new PluginConfiguration
        {
            AudioNoiseGatePercent = 20,
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = userId.ToString().ToUpperInvariant(),
                    AudioNoiseGatePercentOverride = 60
                }
            }
        };

        Assert.Equal(60, config.GetAudioNoiseGatePercentForUser(userId));
        Assert.Equal(20, config.GetAudioNoiseGatePercentForUser(System.Guid.NewGuid()));

        config.UserMappings[0].AudioNoiseGatePercentOverride = 999;
        Assert.Equal(PluginConfiguration.MaxAudioNoiseGatePercent, config.GetAudioNoiseGatePercentForUser(userId));

        config.UserMappings[0].AudioNoiseGatePercentOverride = -1;
        Assert.Equal(PluginConfiguration.MinAudioNoiseGatePercent, config.GetAudioNoiseGatePercentForUser(userId));
    }

    [Fact]
    public void GetAudioBandGainsForUser_UsesOverrideAndClampsInvalidValues()
    {
        var userId = System.Guid.NewGuid();
        var config = new PluginConfiguration
        {
            AudioLowGainPercent = 80,
            AudioMidGainPercent = 110,
            AudioHighGainPercent = 140,
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = userId.ToString(),
                    AudioLowGainPercentOverride = 40,
                    AudioMidGainPercentOverride = 160,
                    AudioHighGainPercentOverride = 200
                }
            }
        };

        Assert.Equal((40, 160, 200), config.GetAudioBandGainsForUser(userId));
        Assert.Equal((80, 110, 140), config.GetAudioBandGainsForUser(System.Guid.NewGuid()));

        config.UserMappings[0].AudioLowGainPercentOverride = -1;
        config.UserMappings[0].AudioMidGainPercentOverride = 999;
        Assert.Equal(
            (PluginConfiguration.MinAudioBandGainPercent,
                PluginConfiguration.MaxAudioBandGainPercent,
                PluginConfiguration.MaxAudioBandGainPercent),
            config.GetAudioBandGainsForUser(userId));
    }

    [Fact]
    public void GetAudioResponseSmoothingPercentForUser_UsesOverrideAndClampsInvalidValues()
    {
        var userId = System.Guid.NewGuid();
        var config = new PluginConfiguration
        {
            AudioResponseSmoothingPercent = 20,
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = userId.ToString().ToUpperInvariant(),
                    AudioResponseSmoothingPercentOverride = 65
                }
            }
        };

        Assert.Equal(65, config.GetAudioResponseSmoothingPercentForUser(userId));
        Assert.Equal(20, config.GetAudioResponseSmoothingPercentForUser(System.Guid.NewGuid()));

        config.UserMappings[0].AudioResponseSmoothingPercentOverride = 999;
        Assert.Equal(PluginConfiguration.MaxAudioResponseSmoothingPercent, config.GetAudioResponseSmoothingPercentForUser(userId));

        config.UserMappings[0].AudioResponseSmoothingPercentOverride = -1;
        Assert.Equal(PluginConfiguration.MinAudioResponseSmoothingPercent, config.GetAudioResponseSmoothingPercentForUser(userId));
    }

    [Fact]
    public void GetAudioBandSpreadPercentForUser_UsesOverrideAndClampsInvalidValues()
    {
        var userId = System.Guid.NewGuid();
        var config = new PluginConfiguration
        {
            AudioBandSpreadPercent = 15,
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = userId.ToString().ToUpperInvariant(),
                    AudioBandSpreadPercentOverride = 45
                }
            }
        };

        Assert.Equal(45, config.GetAudioBandSpreadPercentForUser(userId));
        Assert.Equal(15, config.GetAudioBandSpreadPercentForUser(System.Guid.NewGuid()));

        config.UserMappings[0].AudioBandSpreadPercentOverride = 999;
        Assert.Equal(PluginConfiguration.MaxAudioBandSpreadPercent, config.GetAudioBandSpreadPercentForUser(userId));

        config.UserMappings[0].AudioBandSpreadPercentOverride = -1;
        Assert.Equal(PluginConfiguration.MinAudioBandSpreadPercent, config.GetAudioBandSpreadPercentForUser(userId));
    }

    [Fact]
    public void GetAudioBeatPulsePercentForUser_UsesOverrideAndClampsInvalidValues()
    {
        var userId = System.Guid.NewGuid();
        var config = new PluginConfiguration
        {
            AudioBeatPulsePercent = 20,
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = userId.ToString().ToUpperInvariant(),
                    AudioBeatPulsePercentOverride = 60
                }
            }
        };

        Assert.Equal(60, config.GetAudioBeatPulsePercentForUser(userId));
        Assert.Equal(20, config.GetAudioBeatPulsePercentForUser(System.Guid.NewGuid()));

        config.UserMappings[0].AudioBeatPulsePercentOverride = 999;
        Assert.Equal(PluginConfiguration.MaxAudioBeatPulsePercent, config.GetAudioBeatPulsePercentForUser(userId));

        config.UserMappings[0].AudioBeatPulsePercentOverride = -1;
        Assert.Equal(PluginConfiguration.MinAudioBeatPulsePercent, config.GetAudioBeatPulsePercentForUser(userId));
    }

    [Fact]
    public void GetAudioBeatPulseDecayPercentForUser_UsesOverrideAndClampsInvalidValues()
    {
        var userId = System.Guid.NewGuid();
        var config = new PluginConfiguration
        {
            AudioBeatPulseDecayPercent = 20,
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = userId.ToString().ToUpperInvariant(),
                    AudioBeatPulseDecayPercentOverride = 60
                }
            }
        };

        Assert.Equal(60, config.GetAudioBeatPulseDecayPercentForUser(userId));
        Assert.Equal(20, config.GetAudioBeatPulseDecayPercentForUser(System.Guid.NewGuid()));

        config.UserMappings[0].AudioBeatPulseDecayPercentOverride = 999;
        Assert.Equal(PluginConfiguration.MaxAudioBeatPulseDecayPercent, config.GetAudioBeatPulseDecayPercentForUser(userId));

        config.UserMappings[0].AudioBeatPulseDecayPercentOverride = -1;
        Assert.Equal(PluginConfiguration.MinAudioBeatPulseDecayPercent, config.GetAudioBeatPulseDecayPercentForUser(userId));
    }

    [Fact]
    public void GetAudioBeatPulseThresholdPercentForUser_UsesOverrideAndClampsInvalidValues()
    {
        var userId = System.Guid.NewGuid();
        var config = new PluginConfiguration
        {
            AudioBeatPulseThresholdPercent = 20,
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = userId.ToString().ToUpperInvariant(),
                    AudioBeatPulseThresholdPercentOverride = 60
                }
            }
        };

        Assert.Equal(60, config.GetAudioBeatPulseThresholdPercentForUser(userId));
        Assert.Equal(20, config.GetAudioBeatPulseThresholdPercentForUser(System.Guid.NewGuid()));

        config.UserMappings[0].AudioBeatPulseThresholdPercentOverride = 999;
        Assert.Equal(PluginConfiguration.MaxAudioBeatPulseThresholdPercent, config.GetAudioBeatPulseThresholdPercentForUser(userId));

        config.UserMappings[0].AudioBeatPulseThresholdPercentOverride = -1;
        Assert.Equal(PluginConfiguration.MinAudioBeatPulseThresholdPercent, config.GetAudioBeatPulseThresholdPercentForUser(userId));
    }

    [Fact]
    public void GetAudioColorPaletteForUser_UsesOverrideAndFallsBackSafely()
    {
        var userId = System.Guid.NewGuid();
        var config = new PluginConfiguration
        {
            AudioColorPalette = PluginConfiguration.AudioColorPaletteCool,
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = userId.ToString().ToUpperInvariant(),
                    AudioColorPaletteOverride = PluginConfiguration.AudioColorPaletteMonochrome
                }
            }
        };

        Assert.Equal(PluginConfiguration.AudioColorPaletteMonochrome, config.GetAudioColorPaletteForUser(userId));
        Assert.Equal(PluginConfiguration.AudioColorPaletteCool, config.GetAudioColorPaletteForUser(System.Guid.NewGuid()));

        config.UserMappings[0].AudioColorPaletteOverride = "invalid";
        Assert.Equal(PluginConfiguration.AudioColorPaletteCool, config.GetAudioColorPaletteForUser(userId));

        config.UserMappings.Clear();
        config.AudioColorPalette = "invalid";
        Assert.Equal(PluginConfiguration.AudioColorPaletteSpectrum, config.GetAudioColorPaletteForUser(userId));
    }

    [Fact]
    public void GetAudioSpatialModeForUser_UsesOverrideAndFallsBackSafely()
    {
        var userId = System.Guid.NewGuid();
        var config = new PluginConfiguration
        {
            AudioSpatialMode = PluginConfiguration.AudioSpatialModeMirror,
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = userId.ToString().ToUpperInvariant(),
                    AudioSpatialModeOverride = PluginConfiguration.AudioSpatialModeUniform
                }
            }
        };

        Assert.Equal(PluginConfiguration.AudioSpatialModeUniform, config.GetAudioSpatialModeForUser(userId));
        Assert.Equal(PluginConfiguration.AudioSpatialModeMirror, config.GetAudioSpatialModeForUser(System.Guid.NewGuid()));

        config.UserMappings[0].AudioSpatialModeOverride = "invalid";
        Assert.Equal(PluginConfiguration.AudioSpatialModeMirror, config.GetAudioSpatialModeForUser(userId));

        config.UserMappings.Clear();
        config.AudioSpatialMode = "invalid";
        Assert.Equal(PluginConfiguration.AudioSpatialModeSpatial, config.GetAudioSpatialModeForUser(userId));
    }

    [Fact]
    public void GetAudioChannelModeForUser_UsesOverrideAndFallsBackSafely()
    {
        var userId = System.Guid.NewGuid();
        var config = new PluginConfiguration
        {
            AudioChannelMode = PluginConfiguration.AudioChannelModeStereo,
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = userId.ToString().ToUpperInvariant(),
                    AudioChannelModeOverride = PluginConfiguration.AudioChannelModeMono
                }
            }
        };

        Assert.Equal(PluginConfiguration.AudioChannelModeMono, config.GetAudioChannelModeForUser(userId));
        Assert.Equal(PluginConfiguration.AudioChannelModeStereo, config.GetAudioChannelModeForUser(System.Guid.NewGuid()));

        config.UserMappings[0].AudioChannelModeOverride = "invalid";
        Assert.Equal(PluginConfiguration.AudioChannelModeStereo, config.GetAudioChannelModeForUser(userId));

        config.UserMappings[0].AudioChannelModeOverride = "left";
        Assert.Equal(PluginConfiguration.AudioChannelModeLeft, config.GetAudioChannelModeForUser(userId));

        config.UserMappings[0].AudioChannelModeOverride = "RIGHT";
        Assert.Equal(PluginConfiguration.AudioChannelModeRight, config.GetAudioChannelModeForUser(userId));

        config.UserMappings.Clear();
        config.AudioChannelMode = "invalid";
        Assert.Equal(PluginConfiguration.AudioChannelModeMono, config.GetAudioChannelModeForUser(userId));
    }

    [Fact]
    public void ValidatePerformanceOverrides_WhenAudioChannelModeIsInvalid_ReturnsError()
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = "user-1",
                    AudioChannelModeOverride = "InvalidMode"
                }
            }
        };

        Assert.Contains(
            "User mapping 1 audio channel mode override must be Mono, Stereo, Left, or Right",
            config.Validate());
    }

    [Fact]
    public void ValidatePerformanceOverrides_WhenAudioSpatialModeIsInvalid_ReturnsError()
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = "user-1",
                    AudioSpatialModeOverride = "InvalidMode"
                }
            }
        };

        Assert.Contains(
            "User mapping 1 audio spatial mode override must be Spatial, Uniform, or Mirror",
            config.Validate());
    }

    [Fact]
    public void GetAudioFrequenciesForUser_UsesOverrideAndGlobalFallback()
    {
        var userId = System.Guid.NewGuid();
        var config = new PluginConfiguration
        {
            AudioLowFrequencyHz = 80,
            AudioMidFrequencyHz = 500,
            AudioHighFrequencyHz = 1800,
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = userId.ToString().ToUpperInvariant(),
                    AudioLowFrequencyHzOverride = 60,
                    AudioMidFrequencyHzOverride = 700,
                    AudioHighFrequencyHzOverride = 2400
                }
            }
        };

        Assert.Equal((60, 700, 2400), config.GetAudioFrequenciesForUser(userId));
        Assert.Equal((80, 500, 1800), config.GetAudioFrequenciesForUser(System.Guid.NewGuid()));

        config.UserMappings[0].AudioMidFrequencyHzOverride = 50;
        Assert.Equal(
            (PluginConfiguration.DefaultAudioLowFrequencyHz,
                PluginConfiguration.DefaultAudioMidFrequencyHz,
                PluginConfiguration.DefaultAudioHighFrequencyHz),
            config.GetAudioFrequenciesForUser(userId));
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
                    AudioSensitivityPercentOverride = 401,
                    AudioLowFrequencyHzOverride = 2000,
                    AudioMidFrequencyHzOverride = 100,
                    AudioHighFrequencyHzOverride = 4000,
                    AudioLowGainPercentOverride = -1,
                    AudioMidGainPercentOverride = 201,
                    AudioHighGainPercentOverride = 201,
                    AudioResponseSmoothingPercentOverride = 91,
                    AudioBandSpreadPercentOverride = 101,
                    AudioBeatPulsePercentOverride = 101,
                    AudioBeatPulseThresholdPercentOverride = 101,
                    AudioColorPaletteOverride = "InvalidPalette",
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
        Assert.Contains("User mapping 1 audio sensitivity override must be between 25 and 400 percent", errors);
        Assert.Contains("User mapping 1 audio high frequency override must be between 20 and 3900 Hz", errors);
        Assert.Contains("User mapping 1 audio low gain override must be between 0 and 200 percent", errors);
        Assert.Contains("User mapping 1 audio response smoothing override must be between 0 and 90 percent", errors);
        Assert.Contains("User mapping 1 audio mid gain override must be between 0 and 200 percent", errors);
        Assert.Contains("User mapping 1 audio high gain override must be between 0 and 200 percent", errors);
        Assert.Contains("User mapping 1 audio band spread override must be between 0 and 100 percent", errors);
        Assert.Contains("User mapping 1 audio beat pulse override must be between 0 and 100 percent", errors);
        Assert.Contains("User mapping 1 audio beat pulse threshold override must be between 0 and 100 percent", errors);
        Assert.Contains("User mapping 1 audio color palette override must be Spectrum, Band, Warm, Cool, or Monochrome", errors);
        Assert.Contains("User mapping 1 audio frequency overrides must be strictly ordered low < mid < high", errors);
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
