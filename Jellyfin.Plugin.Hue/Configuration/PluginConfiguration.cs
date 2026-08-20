using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Hue.Video;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Hue.Configuration
{
    /// <summary>
    /// Per-user bridge, entertainment area, and optional playback, color, performance, channel, and restoration profile mapping
    /// </summary>
    public class UserBridgeMapping
    {
        public string UserId { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty; // For display purposes
        // Missing values in older saved configurations deserialize to true, preserving
        // the existing behavior for every mapping created before per-user opt-out support.
        public bool SyncEnabled { get; set; } = true;
        public string HueBridgeIp { get; set; } = string.Empty;
        public string HueAppKey { get; set; } = string.Empty;
        public string HueClientKey { get; set; } = string.Empty;
        public string EntertainmentAreaId { get; set; } = string.Empty;
        public string EntertainmentAreaName { get; set; } = string.Empty; // For display purposes

        // Optional per-user cinema-mode overrides. Null values inherit the global setting.
        public bool? UseCinemaModeOverride { get; set; }
        public int? BrightnessDimLevelOverride { get; set; }
        public string? PauseBehaviorOverride { get; set; }
        public bool? RestoreLightStateOverride { get; set; }
        public string? PlaybackMediaFilterOverride { get; set; }

        // Optional per-user color profile overrides. Null values inherit the global setting.
        public int? BrightnessBoostOverride { get; set; }
        public int? ColorSaturationOverride { get; set; }
        public int? HueShiftDegreesOverride { get; set; }
        public int? OutputBrightnessPercentOverride { get; set; }
        public int? BlackoutThresholdOverride { get; set; }
        public int? ColorChangeThresholdOverride { get; set; }
        public int? RedGainOverride { get; set; }
        public int? GreenGainOverride { get; set; }
        public int? BlueGainOverride { get; set; }

        // Optional per-user playback-performance overrides. Null values inherit the global setting.
        public int? AudioSensitivityPercentOverride { get; set; }
        public int? AudioNoiseGatePercentOverride { get; set; }
        public int? AudioLowFrequencyHzOverride { get; set; }
        public int? AudioMidFrequencyHzOverride { get; set; }
        public int? AudioHighFrequencyHzOverride { get; set; }
        public int? AudioLowGainPercentOverride { get; set; }
        public int? AudioMidGainPercentOverride { get; set; }
        public int? AudioHighGainPercentOverride { get; set; }
        public int? AudioResponseSmoothingPercentOverride { get; set; }
        public int? AudioBandSpreadPercentOverride { get; set; }
        public int? AudioBeatPulsePercentOverride { get; set; }
        public int? AudioBeatPulseDecayPercentOverride { get; set; }
        public int? AudioBeatPulseThresholdPercentOverride { get; set; }
        public string? AudioColorPaletteOverride { get; set; }
        public string? AudioSpatialModeOverride { get; set; }
        public string? AudioChannelModeOverride { get; set; }
        public int? TargetFpsOverride { get; set; }
        public string? FrameResolutionOverride { get; set; }
        public string? VideoScalingModeOverride { get; set; }
        public string? VideoDeinterlaceModeOverride { get; set; }
        public int? SamplingBreadthPercentOverride { get; set; }
        public string? SamplingModeOverride { get; set; }
        public int? ColorSmoothingPercentOverride { get; set; }

        // Optional per-user execution and reliability overrides. Null values inherit the global setting.
        public bool? UseGpuOverride { get; set; }
        public string? CustomFfmpegFlagsOverride { get; set; }
        public int? FfmpegStallTimeoutSecondsOverride { get; set; }
        public int? NetworkRetryAttemptsOverride { get; set; }
        public string? ChannelIdsOverride { get; set; }
    }

    /// <summary>
    /// A reusable preview scene. Presets intentionally contain no bridge credentials or
    /// target information; they can be applied to the default target or any per-user
    /// mapping from the administrator configuration page. TransitionSeconds and
    /// TransitionOutSeconds optionally fade the scene in and out within the configured
    /// duration. Effect selects the bounded frame pattern used during the hold, and
    /// EffectSpeedPercent controls the animation rate for non-solid effects.
    /// </summary>
    public class HueColorPreset
    {
        public string Name { get; set; } = string.Empty;
        /// <summary>
        /// Bounded scene effect. Missing values in older configurations deserialize to
        /// <see cref="PluginConfiguration.ColorPresetEffectSolid"/>.
        /// </summary>
        public string Effect { get; set; } = PluginConfiguration.ColorPresetEffectSolid;
        /// <summary>
        /// Animation speed for Pulse, Rainbow, Candle, Temperature, and Aurora effects. Missing values in
        /// older configurations deserialize to the neutral 100 percent rate.
        /// </summary>
        public int EffectSpeedPercent { get; set; } = PluginConfiguration.DefaultColorPresetEffectSpeedPercent;
        public int Red { get; set; } = 255;
        public int Green { get; set; } = 255;
        public int Blue { get; set; } = 255;
        public int BrightnessPercent { get; set; } = 100;
        public int DurationSeconds { get; set; } = 5;
        /// <summary>
        /// Optional fade-in duration in seconds within the scene duration. Zero preserves
        /// the original instantaneous preview behavior; the value may not exceed the duration.
        /// </summary>
        public int TransitionSeconds { get; set; }
        /// <summary>
        /// Optional fade-out duration in seconds within the scene duration. Zero preserves
        /// the original immediate end behavior; the combined fade-in and fade-out may not
        /// exceed the duration.
        /// </summary>
        public int TransitionOutSeconds { get; set; }
    }

    /// <summary>
    /// A credential-free ordered collection of saved scenes. Playlists retain only scene
    /// names, a bounded repeat count, and an optional target mode; bridge credentials and
    /// channel profiles are resolved from the current server configuration when the
    /// playlist is previewed. A selected-target playlist can fan out to a deliberate
    /// subset of enabled user mappings and optionally the global bridge without storing
    /// any credential-bearing target data.
    /// </summary>
    public sealed class HueScenePlaylist
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public List<string> PresetNames { get; set; } = new List<string>();
        /// <summary>
        /// Number of times the ordered scene sequence is played. Missing values in
        /// legacy configurations preserve the original single-pass behavior.
        /// </summary>
        public int RepeatCount { get; set; } = PluginConfiguration.DefaultScenePlaylistRepeatCount;
        public string TargetUserId { get; set; } = string.Empty;
        /// <summary>
        /// Optional explicit user-mapping targets for a selected-target playlist. When
        /// populated, the playlist runs only on these enabled mappings, optionally
        /// including the global target when <see cref="IncludeDefaultTarget"/> is true.
        /// </summary>
        public List<string> TargetUserIds { get; set; } = new List<string>();
        /// <summary>
        /// Includes the configured global bridge in an explicit selected-target playlist.
        /// </summary>
        public bool IncludeDefaultTarget { get; set; }
        public bool TargetAllEnabledMappings { get; set; }
    }

    /// <summary>
    /// A credential-free cue that displays one saved color scene at a selected time-zone
    /// wall-clock time. It can run once on RunDate or recur daily, weekly, monthly-day,
    /// monthly-weekday, or yearly with
    /// optional date bounds and exclusions. Recurring cues can optionally run every N
    /// calendar days, weeks, months, or years; intervals greater than one use StartDate
    /// as the cadence anchor. A cue can optionally override the saved scene's hold duration
    /// for this event only (single-scene cues; playlists retain each scene's saved duration)
    /// or stop after a bounded number of executions.
    /// The target is resolved from the global bridge, a persisted user mapping, or a bounded
    /// selected mapping subset when the cue runs; credentials are never stored here.
    /// When multiple cues are due together, higher-priority cues run first. Equal priorities
    /// retain their saved configuration order.
    /// </summary>
    public sealed class HueSceneSchedule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public string PresetName { get; set; } = string.Empty;
        /// <summary>
        /// Optional ordered saved-scene playlist. Exactly one of PresetName and PlaylistName
        /// must be populated so existing single-scene cues remain backward compatible.
        /// </summary>
        public string PlaylistName { get; set; } = string.Empty;
        /// <summary>
        /// Relative execution priority when multiple cues are due together. Zero preserves the
        /// default ordering; higher values run first, up to the configured maximum.
        /// </summary>
        public int Priority { get; set; }
        public string TargetUserId { get; set; } = string.Empty;
        /// <summary>
        /// Optional explicit user-mapping targets for a selected-target cue. When populated,
        /// the cue runs only on these enabled mappings, optionally including the global target
        /// when <see cref="IncludeDefaultTarget"/> is true. This remains empty for legacy
        /// default, single-mapping, and all-enabled target modes.
        /// </summary>
        public List<string> TargetUserIds { get; set; } = new List<string>();
        /// <summary>
        /// Includes the configured global bridge in an explicit selected-target cue.
        /// </summary>
        public bool IncludeDefaultTarget { get; set; }
        /// <summary>
        /// When true, the cue fans out sequentially to the global target and every enabled
        /// user mapping with a distinct bridge/area target. TargetUserId must remain blank.
        /// Existing schedules default to false so their single-target behavior is unchanged.
        /// </summary>
        public bool TargetAllEnabledMappings { get; set; }
        public string TimeOfDay { get; set; } = "20:00";
        /// <summary>
        /// Optional system time-zone ID for this cue. Blank preserves the original
        /// server-local behavior and is resolved from <see cref="TimeZoneInfo.Local"/>.
        /// </summary>
        public string TimeZoneId { get; set; } = string.Empty;
        /// <summary>
        /// Recurrence mode for recurring cues. Blank and <see cref="PluginConfiguration.SceneScheduleRecurrenceWeekly"/>
        /// preserve the original weekday-mask behavior; <see cref="PluginConfiguration.SceneScheduleRecurrenceDaily"/>
        /// runs on every calendar date; <see cref="PluginConfiguration.SceneScheduleRecurrenceMonthly"/>
        /// uses <see cref="DayOfMonth"/> and clamps days beyond a month's length to its final day;
        /// <see cref="PluginConfiguration.SceneScheduleRecurrenceMonthlyWeekday"/> uses
        /// <see cref="WeekOfMonth"/> and <see cref="DayOfWeek"/> for patterns such as first Monday
        /// or last Friday; <see cref="PluginConfiguration.SceneScheduleRecurrenceYearly"/> uses
        /// <see cref="MonthOfYear"/> and <see cref="DayOfMonth"/> for an annual calendar date.
        /// One-time cues ignore this value.
        /// </summary>
        public string Recurrence { get; set; } = PluginConfiguration.SceneScheduleRecurrenceWeekly;
        /// <summary>
        /// Number of recurrence units between runs. One preserves the original cadence.
        /// Values greater than one require <see cref="StartDate"/> so the cadence remains
        /// deterministic when configurations move between servers.
        /// </summary>
        public int RecurrenceInterval { get; set; } = 1;
        /// <summary>
        /// Calendar day for monthly recurrence, from 1 through 31. Values above a month's
        /// length run on that month's final calendar day. Yearly recurrence uses the same
        /// clamping behavior within its selected month. Zero is unused by weekly and one-time cues.
        /// </summary>
        public int DayOfMonth { get; set; }
        /// <summary>
        /// Calendar month for yearly recurrence, from 1 (January) through 12 (December).
        /// Zero is unused by other recurrence modes.
        /// </summary>
        public int MonthOfYear { get; set; }
        /// <summary>
        /// Ordinal week for monthly-weekday recurrence. Values 1 through 5 select the first
        /// through fifth matching weekday; -1 selects the last matching weekday. Zero is unused
        /// by other recurrence modes.
        /// </summary>
        public int WeekOfMonth { get; set; }
        /// <summary>
        /// Sunday=0 through Saturday=6 for monthly-weekday recurrence. -1 is unused by other
        /// recurrence modes.
        /// </summary>
        public int DayOfWeek { get; set; } = -1;
        /// <summary>
        /// Optional per-cue hold duration in seconds. Zero inherits the selected saved
        /// scene's duration; a non-zero value overrides it for this cue only.
        /// </summary>
        public int DurationSeconds { get; set; }
        /// <summary>
        /// Maximum number of executions for this cue. Zero means unlimited. The persisted
        /// <see cref="RunCount"/> is incremented for every completed execution, including
        /// failed or canceled attempts, so a finite cue cannot retry forever after a broken
        /// target. One-time cues still disable themselves after their first successful run.
        /// </summary>
        public int MaxRuns { get; set; }
        /// <summary>
        /// Persisted number of completed executions for finite cues. This is retained in the
        /// schedule definition so a limit remains effective after a Jellyfin restart.
        /// </summary>
        public int RunCount { get; set; }
        /// <summary>
        /// Optional one-time calendar date in the cue's selected time zone, formatted as
        /// yyyy-MM-dd. When set, the cue runs once on this date and ignores its weekday
        /// mask; blank preserves the recurring schedule behavior.
        /// </summary>
        public string RunDate { get; set; } = string.Empty;
        /// <summary>
        /// Optional inclusive first calendar date in the cue's time zone, formatted as
        /// yyyy-MM-dd. Blank means the cue has no lower date bound.
        /// </summary>
        public string StartDate { get; set; } = string.Empty;
        /// <summary>
        /// Optional inclusive last calendar date in the cue's time zone, formatted as
        /// yyyy-MM-dd. Blank means the cue has no upper date bound.
        /// </summary>
        public string EndDate { get; set; } = string.Empty;
        /// <summary>
        /// Optional calendar dates on which this cue must not run, formatted as yyyy-MM-dd
        /// in the cue's time zone. Values are normalized, sorted, and bounded during API saves.
        /// </summary>
        public List<string> ExcludedDates { get; set; } = new List<string>();
        public int DaysOfWeekMask { get; set; } = 127;
        public bool Enabled { get; set; } = true;
        /// <summary>
        /// When true, the next eligible automatic occurrence is skipped and the flag is
        /// cleared atomically. Manual Run Now actions are never suppressed. One-time cues
        /// are disabled after their skipped occurrence because they have no later occurrence.
        /// </summary>
        public bool SkipNextOccurrence { get; set; }
    }

    /// <summary>
    /// Credential-free completed-session telemetry stored only when an administrator opts
    /// into retaining history across Jellyfin restarts. This shape deliberately contains no
    /// bridge keys or Jellyfin playback tokens.
    /// </summary>
    public sealed class HueSessionHistoryEntry
    {
        public string Outcome { get; set; } = "Stopped";
        public string? Item { get; set; }
        public string? UserId { get; set; }
        public string? UserName { get; set; }
        public string? BridgeIp { get; set; }
        public string? EntertainmentAreaId { get; set; }
        public DateTime? StartedAtUtc { get; set; }
        public DateTime? EndedAtUtc { get; set; }
        public double? DurationSeconds { get; set; }
        public double? EffectiveFps { get; set; }
        public long FramesProcessed { get; set; }
        public long PacketsSent { get; set; }
        public long PacketsSkippedByThreshold { get; set; }
        public long PacketSendFailures { get; set; }
        public int ReconnectAttempts { get; set; }
        public int SeekRestartCount { get; set; }
        public double? LastSeekPositionSeconds { get; set; }
        public string? Error { get; set; }
        public string? CleanupWarning { get; set; }
    }

    /// <summary>
    /// Credential-free scheduled-scene run telemetry stored only when an administrator
    /// opts into retaining cue history across Jellyfin restarts. This shape deliberately
    /// contains no bridge keys, client keys, or playback tokens.
    /// </summary>
    public sealed class HueSceneScheduleHistoryEntry
    {
        public string ScheduleId { get; set; } = string.Empty;
        public string ScheduleName { get; set; } = string.Empty;
        public string PresetName { get; set; } = string.Empty;
        public string PlaylistName { get; set; } = string.Empty;
        public int PlaylistRepeatCount { get; set; } = PluginConfiguration.DefaultScenePlaylistRepeatCount;
        public string Effect { get; set; } = PluginConfiguration.ColorPresetEffectSolid;
        public int EffectSpeedPercent { get; set; } = PluginConfiguration.DefaultColorPresetEffectSpeedPercent;
        public string? TargetLabel { get; set; }
        public List<string> TargetUserIds { get; set; } = new List<string>();
        public bool IncludeDefaultTarget { get; set; }
        public bool Succeeded { get; set; }
        public bool Skipped { get; set; }
        public bool WasCatchUp { get; set; }
        public bool WasDeferred { get; set; }
        public bool WasDeferredRestored { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? CleanupWarning { get; set; }
        public List<HueSceneScheduleTargetResult> TargetResults { get; set; } = new List<HueSceneScheduleTargetResult>();
        public DateTime RunAtUtc { get; set; }
        public int RunCount { get; set; }
    }

    /// <summary>
    /// Credential-free state for one automatic scene occurrence that is waiting for
    /// active playback to finish. Deferred occurrences are runtime state rather than
    /// backup content, but are persisted in the plugin configuration so a Jellyfin
    /// restart does not silently discard a cue that is still inside its wait window.
    /// </summary>
    public sealed class HueSceneDeferredRunEntry
    {
        public string ScheduleId { get; set; } = string.Empty;
        public DateTime OccurrenceSlot { get; set; }
        public DateTime DeferredAtLocal { get; set; }
    }

    /// <summary>
    /// Credential-free outcome for one target in a scheduled-scene run, including the
    /// validated entertainment-channel counts used by immediate and scheduled previews.
    /// </summary>
    public sealed class HueSceneScheduleTargetResult
    {
        [JsonPropertyName("targetLabel")]
        public string TargetLabel { get; set; } = string.Empty;

        [JsonPropertyName("succeeded")]
        public bool Succeeded { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("cleanupWarning")]
        public string? CleanupWarning { get; set; }

        [JsonPropertyName("availableChannelCount")]
        public int AvailableChannelCount { get; set; }

        [JsonPropertyName("selectedChannelCount")]
        public int SelectedChannelCount { get; set; }
    }

    /// <summary>
    /// Configuration for the Philips Hue Sync plugin
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        public const string PauseBehaviorKeepLastColors = "KeepLastColors";
        public const string PauseBehaviorRestoreLightState = "RestoreLightState";
        public const string SamplingModeAverage = "Average";
        public const string SamplingModeCenterWeighted = "CenterWeighted";
        public const string SamplingModeCenterPixel = "CenterPixel";
        public const string FrameResolutionLow = "80x45";
        public const string FrameResolutionStandard = "160x90";
        public const string FrameResolutionHigh = "320x180";
        public const string VideoScalingModeStretch = "Stretch";
        public const string VideoScalingModeFit = "Fit";
        public const string VideoScalingModeCrop = "Crop";
        public const string VideoDeinterlaceModeOff = "Off";
        public const string VideoDeinterlaceModeAuto = "Auto";
        public const string VideoDeinterlaceModeOn = "On";
        public const string PlaybackMediaFilterAllVideo = "AllVideo";
        public const string PlaybackMediaFilterMovies = "Movies";
        public const string PlaybackMediaFilterEpisodes = "Episodes";
        public const string PlaybackMediaFilterOtherVideo = "OtherVideo";
        public const string PlaybackMediaFilterAudio = "Audio";
        public const string PlaybackMediaFilterAllMedia = "AllMedia";
        public const string AudioColorPaletteSpectrum = "Spectrum";
        public const string AudioColorPaletteBand = "Band";
        public const string AudioColorPaletteWarm = "Warm";
        public const string AudioColorPaletteCool = "Cool";
        public const string AudioColorPaletteMonochrome = "Monochrome";
        public const string AudioSpatialModeSpatial = "Spatial";
        public const string AudioSpatialModeUniform = "Uniform";
        public const string AudioSpatialModeMirror = "Mirror";
        public const string AudioChannelModeMono = "Mono";
        public const string AudioChannelModeStereo = "Stereo";
        public const string AudioChannelModeLeft = "Left";
        public const string AudioChannelModeRight = "Right";

        private const int MinTargetFps = 1;
        private const int MaxTargetFps = 60;
        private const int MinBrightnessDimLevel = 0;
        private const int MaxBrightnessDimLevel = 100;
        private const int MinBrightnessBoost = 50;
        private const int MaxBrightnessBoost = 200;
        private const int MinColorChannelGain = 50;
        private const int MaxColorChannelGain = 200;
        private const int MinColorSaturation = 0;
        private const int MaxColorSaturation = 200;
        private const int MinHueShiftDegrees = -180;
        private const int MaxHueShiftDegrees = 180;
        private const int MinOutputBrightnessPercent = 0;
        private const int MaxOutputBrightnessPercent = 100;
        private const int MinByteSetting = 0;
        private const int MaxByteSetting = 255;
        private const int MinNetworkRetryAttempts = 0;
        private const int MaxNetworkRetryAttempts = 10;
        private const int MinFfmpegStallTimeoutSeconds = 1;
        private const int MaxFfmpegStallTimeoutSeconds = 60;
        private const int MinSamplingBreadthPercent = 1;
        private const int MaxSamplingBreadthPercent = 50;
        private const int MinColorSmoothingPercent = 0;
        private const int MaxColorSmoothingPercent = 90;
        public const int MinAudioSensitivityPercent = 25;
        public const int MaxAudioSensitivityPercent = 400;
        public const int DefaultAudioSensitivityPercent = 100;
        public const int MinAudioNoiseGatePercent = 0;
        public const int MaxAudioNoiseGatePercent = 100;
        public const int DefaultAudioNoiseGatePercent = 0;
        public const int MinAudioFrequencyHz = 20;
        public const int MaxAudioFrequencyHz = 3900;
        public const int DefaultAudioLowFrequencyHz = 90;
        public const int DefaultAudioMidFrequencyHz = 420;
        public const int DefaultAudioHighFrequencyHz = 1600;
        public const int MinAudioBandGainPercent = 0;
        public const int MaxAudioBandGainPercent = 200;
        public const int DefaultAudioBandGainPercent = 100;
        public const int MinAudioResponseSmoothingPercent = 0;
        public const int MaxAudioResponseSmoothingPercent = 90;
        public const int DefaultAudioResponseSmoothingPercent = 0;
        public const int MinAudioBandSpreadPercent = 0;
        public const int MaxAudioBandSpreadPercent = 100;
        public const int DefaultAudioBandSpreadPercent = 0;
        public const int MinAudioBeatPulsePercent = 0;
        public const int MaxAudioBeatPulsePercent = 100;
        public const int DefaultAudioBeatPulsePercent = 0;
        public const int MinAudioBeatPulseDecayPercent = 0;
        public const int MaxAudioBeatPulseDecayPercent = 100;
        public const int DefaultAudioBeatPulseDecayPercent = 0;
        public const int MinAudioBeatPulseThresholdPercent = 0;
        public const int MaxAudioBeatPulseThresholdPercent = 100;
        public const int DefaultAudioBeatPulseThresholdPercent = 0;
        public const string ColorPresetEffectSolid = "Solid";
        public const string ColorPresetEffectPulse = "Pulse";
        public const string ColorPresetEffectRainbow = "Rainbow";
        public const string ColorPresetEffectCandle = "Candle";
        public const string ColorPresetEffectTemperature = "Temperature";
        public const string ColorPresetEffectAurora = "Aurora";
        public const string SceneScheduleEffectPlaylist = "Playlist";
        public const string SceneAutomationPlaybackPolicySkip = "Skip";
        public const string SceneAutomationPlaybackPolicyDefer = "Defer";
        public const int MinPreviewDurationSeconds = 1;
        public const int MaxPreviewDurationSeconds = 30;
        public const int MinColorPresetTransitionSeconds = 0;
        public const int MaxColorPresetTransitionSeconds = MaxPreviewDurationSeconds;
        public const int MinColorPresetTransitionOutSeconds = 0;
        public const int MaxColorPresetTransitionOutSeconds = MaxPreviewDurationSeconds;
        public const int MinColorPresetEffectSpeedPercent = 25;
        public const int MaxColorPresetEffectSpeedPercent = 400;
        public const int DefaultColorPresetEffectSpeedPercent = 100;
        public const int MaxColorPresets = 50;
        public const int MaxColorPresetNameLength = 64;
        public const int MaxBulkUserMappingDeletes = 50;
        public const int MaxBulkUserMappingUpdates = 50;
        public const int MaxScenePlaylists = 50;
        public const int MaxScenePlaylistItems = 20;
        public const int MaxScenePlaylistTotalDurationSeconds = MaxScenePlaylistItems * MaxPreviewDurationSeconds;
        public const int MinScenePlaylistRepeatCount = 1;
        public const int MaxScenePlaylistRepeatCount = 10;
        public const int DefaultScenePlaylistRepeatCount = MinScenePlaylistRepeatCount;
        public const int MaxScenePlaylistNameLength = 64;
        public const int MaxSceneSchedules = 50;
        public const int MaxSceneScheduleTargetMappings = 50;
        public const int MaxSceneScheduleNameLength = 64;
        public const int MinSceneSchedulePriority = 0;
        public const int MaxSceneSchedulePriority = 100;
        public const int MaxSceneScheduleExcludedDates = 100;
        public const int AllSceneScheduleDaysMask = 127;
        public const string SceneScheduleRecurrenceWeekly = "Weekly";
        public const string SceneScheduleRecurrenceDaily = "Daily";
        public const string SceneScheduleRecurrenceMonthly = "Monthly";
        public const string SceneScheduleRecurrenceMonthlyWeekday = "MonthlyWeekday";
        public const string SceneScheduleRecurrenceYearly = "Yearly";
        public const int SceneScheduleLastWeekOfMonth = -1;
        public const int MinSceneScheduleWeekOfMonth = 1;
        public const int MaxSceneScheduleWeekOfMonth = 5;
        public const int MinSceneScheduleMonthOfYear = 1;
        public const int MaxSceneScheduleMonthOfYear = 12;
        public const int MinSceneScheduleRecurrenceInterval = 1;
        public const int MaxSceneScheduleRecurrenceInterval = 365;
        public const int MaxSceneScheduleRuns = 365;
        public const int MaxSessionHistoryCount = 25;
        public const int MaxSceneScheduleHistoryCount = 100;
        public const int MinSessionHistoryRetentionCount = 1;
        public const int MaxSessionHistoryRetentionCount = MaxSessionHistoryCount;
        public const int DefaultSessionHistoryRetentionCount = MaxSessionHistoryCount;
        public const int MinSceneScheduleHistoryRetentionCount = 1;
        public const int MaxSceneScheduleHistoryRetentionCount = MaxSceneScheduleHistoryCount;
        public const int DefaultSceneScheduleHistoryRetentionCount = MaxSceneScheduleHistoryCount;
        public const int MinSceneAutomationCatchUpMinutes = 0;
        public const int MaxSceneAutomationCatchUpMinutes = 120;
        public const int MinSceneAutomationDeferMinutes = 1;
        public const int MaxSceneAutomationDeferMinutes = 120;
        public const int DefaultSceneAutomationDeferMinutes = 15;

        private static readonly string[] ColorPresetEffects =
        {
            ColorPresetEffectSolid,
            ColorPresetEffectPulse,
            ColorPresetEffectRainbow,
            ColorPresetEffectCandle,
            ColorPresetEffectTemperature,
            ColorPresetEffectAurora
        };

        private static readonly string[] PlaybackMediaFilters =
        {
            PlaybackMediaFilterAllVideo,
            PlaybackMediaFilterMovies,
            PlaybackMediaFilterEpisodes,
            PlaybackMediaFilterOtherVideo,
            PlaybackMediaFilterAudio,
            PlaybackMediaFilterAllMedia
        };

        private static readonly string[] SceneAutomationPlaybackPolicies =
        {
            SceneAutomationPlaybackPolicySkip,
            SceneAutomationPlaybackPolicyDefer
        };

        private static readonly string[] AudioColorPalettes =
        {
            AudioColorPaletteSpectrum,
            AudioColorPaletteBand,
            AudioColorPaletteWarm,
            AudioColorPaletteCool,
            AudioColorPaletteMonochrome
        };

        private static readonly string[] AudioSpatialModes =
        {
            AudioSpatialModeSpatial,
            AudioSpatialModeUniform,
            AudioSpatialModeMirror
        };

        private static readonly string[] AudioChannelModes =
        {
            AudioChannelModeMono,
            AudioChannelModeStereo,
            AudioChannelModeLeft,
            AudioChannelModeRight
        };

        /// <summary>
        /// Returns the canonical spelling for a supported saved-scene effect. Blank
        /// values are treated as the legacy solid-color behavior.
        /// </summary>
        public static bool TryNormalizeColorPresetEffect(string? value, out string normalized)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                normalized = ColorPresetEffectSolid;
                return true;
            }

            var match = ColorPresetEffects.FirstOrDefault(effect =>
                string.Equals(effect, value.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                normalized = ColorPresetEffectSolid;
                return false;
            }

            normalized = match;
            return true;
        }

        /// <summary>
        /// Returns the canonical spelling for the configured playback media scope.
        /// Blank values preserve the legacy behavior of synchronizing every video item.
        /// </summary>
        public static bool TryNormalizePlaybackMediaFilter(string? value, out string normalized)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                normalized = PlaybackMediaFilterAllVideo;
                return true;
            }

            var match = PlaybackMediaFilters.FirstOrDefault(filter =>
                string.Equals(filter, value.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                normalized = PlaybackMediaFilterAllVideo;
                return false;
            }

            normalized = match;
            return true;
        }

        /// <summary>
        /// Returns the canonical policy used when an automatic scene cue collides with
        /// active Jellyfin playback. Blank values preserve the original skip behavior.
        /// </summary>
        public static bool TryNormalizeSceneAutomationPlaybackPolicy(string? value, out string normalized)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                normalized = SceneAutomationPlaybackPolicySkip;
                return true;
            }

            var match = SceneAutomationPlaybackPolicies.FirstOrDefault(policy =>
                string.Equals(policy, value.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                normalized = SceneAutomationPlaybackPolicySkip;
                return false;
            }

            normalized = match;
            return true;
        }

        /// <summary>
        /// Normalizes an optional per-user playback scope while preserving invalid text
        /// for configuration validation feedback. Blank values become null so inheritance
        /// remains explicit in persisted mapping profiles.
        /// </summary>
        public static string? NormalizeOptionalPlaybackMediaFilter(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return TryNormalizePlaybackMediaFilter(value, out var normalized)
                ? normalized
                : value.Trim();
        }

        /// <summary>
        /// Returns the canonical spelling for a supported audio visualizer palette.
        /// Blank values preserve the default Spectrum behavior.
        /// </summary>
        public static bool TryNormalizeAudioColorPalette(string? value, out string normalized)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                normalized = AudioColorPaletteSpectrum;
                return true;
            }

            var match = AudioColorPalettes.FirstOrDefault(palette =>
                string.Equals(palette, value.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                normalized = AudioColorPaletteSpectrum;
                return false;
            }

            normalized = match;
            return true;
        }

        /// <summary>
        /// Normalizes an optional per-user audio palette while preserving invalid text
        /// for configuration validation feedback. Blank values become null so inheritance
        /// remains explicit in persisted mapping profiles.
        /// </summary>
        public static string? NormalizeOptionalAudioColorPalette(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return TryNormalizeAudioColorPalette(value, out var normalized)
                ? normalized
                : value.Trim();
        }

        /// <summary>
        /// Returns the canonical spelling for a supported audio spatial routing mode.
        /// Blank values preserve the default Spatial behavior.
        /// </summary>
        public static bool TryNormalizeAudioSpatialMode(string? value, out string normalized)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                normalized = AudioSpatialModeSpatial;
                return true;
            }

            var match = AudioSpatialModes.FirstOrDefault(mode =>
                string.Equals(mode, value.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                normalized = AudioSpatialModeSpatial;
                return false;
            }

            normalized = match;
            return true;
        }

        /// <summary>
        /// Normalizes an optional per-user audio spatial mode while preserving invalid text
        /// for configuration validation feedback. Blank values become null so inheritance
        /// remains explicit in persisted mapping profiles.
        /// </summary>
        public static string? NormalizeOptionalAudioSpatialMode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return TryNormalizeAudioSpatialMode(value, out var normalized)
                ? normalized
                : value.Trim();
        }

        /// <summary>
        /// Returns the canonical spelling for a supported audio source-channel mode.
        /// Mono preserves the legacy mixed behavior; Left and Right isolate one source
        /// channel when Spatial routing is active.
        /// </summary>
        public static bool TryNormalizeAudioChannelMode(string? value, out string normalized)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                normalized = AudioChannelModeMono;
                return true;
            }

            var match = AudioChannelModes.FirstOrDefault(mode =>
                string.Equals(mode, value.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                normalized = AudioChannelModeMono;
                return false;
            }

            normalized = match;
            return true;
        }

        /// <summary>
        /// Normalizes an optional per-user audio source-channel mode while preserving
        /// invalid text for configuration validation feedback. Blank values become null
        /// so inheritance remains explicit in persisted mapping profiles.
        /// </summary>
        public static string? NormalizeOptionalAudioChannelMode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return TryNormalizeAudioChannelMode(value, out var normalized)
                ? normalized
                : value.Trim();
        }

        /// <summary>
        /// Clamps persisted or telemetry-only effect speed values to the supported range.
        /// Request and configuration validation still rejects out-of-range user input.
        /// </summary>
        public static int ClampColorPresetEffectSpeedPercent(int value)
            => Math.Clamp(value, MinColorPresetEffectSpeedPercent, MaxColorPresetEffectSpeedPercent);

        public bool SyncEnabled { get; set; } = false;
        /// <summary>
        /// Limits which Jellyfin video item types can start Hue synchronization. Existing
        /// sessions still receive their normal progress and stop lifecycle events when this
        /// setting changes, so cleanup remains safe during an administrator edit.
        /// </summary>
        public string PlaybackMediaFilter { get; set; } = PlaybackMediaFilterAllVideo;

        // Default/fallback bridge settings (used when no user mapping exists)
        public string HueBridgeIp { get; set; } = string.Empty;
        public string HueAppKey { get; set; } = string.Empty;
        public string HueClientKey { get; set; } = string.Empty; // For DTLS
        public string EntertainmentAreaId { get; set; } = string.Empty;
        /// <summary>
        /// Optional global entertainment channel selection. Blank means every channel in the area.
        /// Per-user channel profiles override this selection when populated.
        /// </summary>
        public string ChannelIds { get; set; } = string.Empty;

        // Per-user bridge mappings
        public List<UserBridgeMapping> UserMappings { get; set; } = new List<UserBridgeMapping>();

        /// <summary>
        /// Named visual scenes available to the administrator preview controls.
        /// Presets are global, carry only bounded visual metadata, and do not contain
        /// bridge credentials or channel targets.
        /// </summary>
        public List<HueColorPreset> ColorPresets { get; set; } = new List<HueColorPreset>();

        /// <summary>
        /// Ordered, credential-free collections of saved scenes that can be previewed
        /// sequentially against the default target, one mapping, or all enabled targets.
        /// </summary>
        public List<HueScenePlaylist> ScenePlaylists { get; set; } = new List<HueScenePlaylist>();

        /// <summary>
        /// Recurring visual cues that reference the credential-free color presets above.
        /// Targets are resolved from the current global or per-user bridge mapping when
        /// a cue runs, so this collection never contains bridge secrets.
        /// </summary>
        public List<HueSceneSchedule> SceneSchedules { get; set; } = new List<HueSceneSchedule>();

        /// <summary>
        /// Controls whether configured scene cues run automatically. Existing
        /// configurations remain enabled by default; manual Run Now requests are
        /// still allowed while recurring automation is paused.
        /// </summary>
        public bool SceneAutomationEnabled { get; set; } = true;

        /// <summary>
        /// Bounded grace period for recovering the most recent automatic cue occurrence
        /// after a scheduler restart or short Jellyfin outage. Zero preserves the
        /// original exact-minute behavior; when enabled, at most one missed occurrence
        /// is recovered per cue and older missed occurrences are not replayed in a burst.
        /// </summary>
        public int SceneAutomationCatchUpMinutes { get; set; }

        /// <summary>
        /// Controls automatic scene cues that become due while playback owns the Hue
        /// bridge. Skip preserves the historical behavior; Defer retries one occurrence
        /// after playback ends for the bounded defer window.
        /// </summary>
        public string SceneAutomationPlaybackPolicy { get; set; } = SceneAutomationPlaybackPolicySkip;

        /// <summary>
        /// Maximum wall-clock wait for a deferred automatic cue before it is recorded as
        /// skipped. This setting is used only when the playback policy is Defer.
        /// </summary>
        public int SceneAutomationDeferMinutes { get; set; } = DefaultSceneAutomationDeferMinutes;

        /// <summary>
        /// Retains the bounded, sanitized scheduled-scene run history in plugin
        /// configuration. Disabled by default because cue and target labels may be
        /// private metadata.
        /// </summary>
        public bool PersistSceneScheduleHistory { get; set; } = false;

        /// <summary>
        /// Maximum number of sanitized scheduled-scene entries retained in memory and,
        /// when persistence is enabled, across Jellyfin restarts. Older configurations
        /// default to the historical 100-entry window.
        /// </summary>
        public int SceneScheduleHistoryRetentionCount { get; set; } = DefaultSceneScheduleHistoryRetentionCount;

        /// <summary>
        /// Newest-first sanitized scheduled-scene entries used only when
        /// <see cref="PersistSceneScheduleHistory"/> is enabled. These entries are
        /// intentionally excluded from configuration exports.
        /// </summary>
        public List<HueSceneScheduleHistoryEntry> PersistedSceneScheduleHistory { get; set; } = new List<HueSceneScheduleHistoryEntry>();

        /// <summary>
        /// Deferred automatic scene occurrences waiting for playback to finish. Entries
        /// contain only schedule identity and server-local timestamps; credentials and
        /// target secrets are never stored here. The scheduler removes entries after
        /// completion, expiration, disablement, or deletion.
        /// </summary>
        public List<HueSceneDeferredRunEntry> PersistedSceneAutomationDeferredRuns { get; set; } = new List<HueSceneDeferredRunEntry>();

        /// <summary>
        /// Retains the bounded, sanitized completed-session history in plugin configuration.
        /// Disabled by default because item and user labels may be private metadata.
        /// </summary>
        public bool PersistSessionHistory { get; set; } = false;

        /// <summary>
        /// Maximum number of sanitized completed-session entries retained in memory and,
        /// when persistence is enabled, across Jellyfin restarts. Older configurations
        /// default to the historical 25-entry window.
        /// </summary>
        public int SessionHistoryRetentionCount { get; set; } = DefaultSessionHistoryRetentionCount;

        /// <summary>
        /// Newest-first sanitized session entries used only when <see cref="PersistSessionHistory"/>
        /// is enabled. These entries are intentionally excluded from configuration exports.
        /// </summary>
        public List<HueSessionHistoryEntry> PersistedSessionHistory { get; set; } = new List<HueSessionHistoryEntry>();

        public bool UseCinemaMode { get; set; } = true; // Dimming behavior
        public int BrightnessDimLevel { get; set; } = 30;
        public string PauseBehavior { get; set; } = PauseBehaviorKeepLastColors;
        /// <summary>
        /// Scales the audio-reactive loudness envelope without changing the final
        /// brightness policy applied to video or audio colors.
        /// </summary>
        public int AudioSensitivityPercent { get; set; } = DefaultAudioSensitivityPercent;
        /// <summary>
        /// Suppresses audio analysis windows whose mixed RMS level is below this
        /// normalized full-scale threshold. Zero preserves the original behavior.
        /// </summary>
        public int AudioNoiseGatePercent { get; set; } = DefaultAudioNoiseGatePercent;
        /// <summary>
        /// Center frequencies used by the dependency-free low/mid/high audio analyzer.
        /// The defaults preserve the original visualizer behavior; each value may be
        /// overridden per user while the effective profile remains strictly ordered.
        /// </summary>
        public int AudioLowFrequencyHz { get; set; } = DefaultAudioLowFrequencyHz;
        public int AudioMidFrequencyHz { get; set; } = DefaultAudioMidFrequencyHz;
        public int AudioHighFrequencyHz { get; set; } = DefaultAudioHighFrequencyHz;
        /// <summary>
        /// Scales each low/mid/high audio band independently before palette rendering.
        /// Neutral 100% values preserve the original analyzer balance; zero mutes a band
        /// and values through 200% compensate for room or source imbalance.
        /// </summary>
        public int AudioLowGainPercent { get; set; } = DefaultAudioBandGainPercent;
        public int AudioMidGainPercent { get; set; } = DefaultAudioBandGainPercent;
        public int AudioHighGainPercent { get; set; } = DefaultAudioBandGainPercent;
        /// <summary>
        /// Blends each decoded audio analysis window with the previous window before
        /// palette rendering. Zero preserves the original immediate response; higher
        /// values reduce spectral flicker while retaining a bounded, deterministic tail.
        /// </summary>
        public int AudioResponseSmoothingPercent { get; set; } = DefaultAudioResponseSmoothingPercent;
        /// <summary>
        /// Expands each audio center into a bounded frequency band. Zero preserves the
        /// original single-center DFT behavior; higher values average nearby frequencies
        /// so real-world content between configured centers remains reactive.
        /// </summary>
        public int AudioBandSpreadPercent { get; set; } = DefaultAudioBandSpreadPercent;
        /// <summary>
        /// Adds an opt-in transient brightness response to rising audio energy. Zero
        /// preserves the steady loudness envelope used by existing installations.
        /// </summary>
        public int AudioBeatPulsePercent { get; set; } = DefaultAudioBeatPulsePercent;
        /// <summary>
        /// Controls how much of an audio beat pulse carries into subsequent frames.
        /// Zero preserves the original instantaneous transient; higher values add a
        /// bounded visible release tail without allowing a pulse to remain permanent.
        /// </summary>
        public int AudioBeatPulseDecayPercent { get; set; } = DefaultAudioBeatPulseDecayPercent;
        /// <summary>
        /// Requires a minimum normalized rise in audio energy before a beat pulse
        /// attacks. Zero preserves the original response to every upward change.
        /// </summary>
        public int AudioBeatPulseThresholdPercent { get; set; } = DefaultAudioBeatPulseThresholdPercent;
        /// <summary>
        /// Selects the palette used by audio-reactive playback. Spectrum preserves the
        /// original drifting hue behavior; the other palettes provide explicit band,
        /// warm, cool, or monochrome presentation choices.
        /// </summary>
        public string AudioColorPalette { get; set; } = AudioColorPaletteSpectrum;
        /// <summary>
        /// Selects how low/mid/high audio energy is routed across entertainment channels.
        /// Spatial preserves the original position-aware mapping, Uniform sends the same
        /// mixed response to every channel, and Mirror produces a symmetric room pattern.
        /// </summary>
        public string AudioSpatialMode { get; set; } = AudioSpatialModeSpatial;
        /// <summary>
        /// Selects whether stereo PCM is mixed to mono (legacy default) or its left/right
        /// source energy is preserved for physical channel placement during audio playback.
        /// </summary>
        public string AudioChannelMode { get; set; } = AudioChannelModeMono;
        public int TargetFps { get; set; } = 20;
        public string FrameResolution { get; set; } = FrameResolutionStandard;
        public string VideoScalingMode { get; set; } = VideoScalingModeStretch;
        public string VideoDeinterlaceMode { get; set; } = VideoDeinterlaceModeOff;
        public int SamplingBreadthPercent { get; set; } = 15;
        public string SamplingMode { get; set; } = SamplingModeAverage;
        public int ColorSmoothingPercent { get; set; } = 0;
        public bool UseGpu { get; set; } = true;
        public string CustomFfmpegFlags { get; set; } = string.Empty; // e.g. -hwaccel auto
        public int FfmpegStallTimeoutSeconds { get; set; } = 5;

        // New advanced settings
        public bool RestoreLightState { get; set; } = true; // Save and restore light state before sync
        public int BrightnessBoost { get; set; } = 100; // Brightness multiplier (50-200%)
        public int RedGain { get; set; } = 100; // Red channel white-balance gain (50-200%)
        public int GreenGain { get; set; } = 100; // Green channel white-balance gain (50-200%)
        public int BlueGain { get; set; } = 100; // Blue channel white-balance gain (50-200%)
        public int ColorSaturation { get; set; } = 100; // Color saturation adjustment (0-200%)
        public int HueShiftDegrees { get; set; } = 0; // Global hue rotation (-180 to 180 degrees)
        public int OutputBrightnessPercent { get; set; } = 100; // Final output brightness ceiling (0-100%)
        public int BlackoutThreshold { get; set; } = 15; // Average brightness below which lights are set to black (0-255)
        public int ColorChangeThreshold { get; set; } = 10; // Minimum color change to trigger update (0-255)
        public int NetworkRetryAttempts { get; set; } = 3; // Number of retry attempts for Hue REST and DTLS recovery

        /// <summary>
        /// Gets the bridge configuration for a specific user, or falls back to default
        /// </summary>
        public (string BridgeIp, string AppKey, string ClientKey, string AreaId) GetBridgeConfigForUser(Guid userId)
        {
            var userIdText = userId.ToString();
            var mapping = UserMappings?.Find(m => string.Equals(m.UserId?.Trim(), userIdText, StringComparison.OrdinalIgnoreCase));
            if (mapping != null && !string.IsNullOrWhiteSpace(mapping.HueBridgeIp))
            {
                return (mapping.HueBridgeIp, mapping.HueAppKey, mapping.HueClientKey, mapping.EntertainmentAreaId);
            }
            // Fall back to default
            return (HueBridgeIp, HueAppKey, HueClientKey, EntertainmentAreaId);
        }

        /// <summary>
        /// Gets whether synchronization is enabled for a user. Users without a mapping
        /// retain the default synchronization behavior.
        /// </summary>
        public bool IsSyncEnabledForUser(Guid userId)
        {
            var userIdText = userId.ToString();
            var mapping = UserMappings?.Find(m => string.Equals(m.UserId?.Trim(), userIdText, StringComparison.OrdinalIgnoreCase));
            return mapping?.SyncEnabled ?? true;
        }

        /// <summary>
        /// Gets the optional per-user playback media scope override. A blank value means
        /// the global playback scope is inherited.
        /// </summary>
        public string? GetPlaybackMediaFilterOverrideForUser(Guid userId)
        {
            var userIdText = userId.ToString();
            var mapping = UserMappings?.Find(m => string.Equals(m.UserId?.Trim(), userIdText, StringComparison.OrdinalIgnoreCase));
            return NormalizeOptionalPlaybackMediaFilter(mapping?.PlaybackMediaFilterOverride);
        }

        /// <summary>
        /// Gets the effective audio-reactive sensitivity for a user. A missing mapping
        /// or blank override inherits the global setting; the result is clamped for
        /// runtime safety while configuration validation reports invalid persisted values.
        /// </summary>
        public int GetAudioSensitivityPercentForUser(Guid userId)
        {
            var userIdText = userId.ToString();
            var mapping = UserMappings?.Find(m => string.Equals(m.UserId?.Trim(), userIdText, StringComparison.OrdinalIgnoreCase));
            return Math.Clamp(
                mapping?.AudioSensitivityPercentOverride ?? AudioSensitivityPercent,
                MinAudioSensitivityPercent,
                MaxAudioSensitivityPercent);
        }

        /// <summary>
        /// Gets the effective audio noise gate for a user. A missing override inherits
        /// the global setting; invalid hand-edited values are clamped for runtime safety.
        /// </summary>
        public int GetAudioNoiseGatePercentForUser(Guid userId)
        {
            var userIdText = userId.ToString();
            var mapping = UserMappings?.Find(m => string.Equals(m.UserId?.Trim(), userIdText, StringComparison.OrdinalIgnoreCase));
            return Math.Clamp(
                mapping?.AudioNoiseGatePercentOverride ?? AudioNoiseGatePercent,
                MinAudioNoiseGatePercent,
                MaxAudioNoiseGatePercent);
        }

        /// <summary>
        /// Gets the effective low/mid/high audio band gains for a user. Missing overrides
        /// inherit the global profile; invalid hand-edited values are clamped for runtime
        /// continuity while configuration validation reports the bad values.
        /// </summary>
        public (int LowGainPercent, int MidGainPercent, int HighGainPercent) GetAudioBandGainsForUser(Guid userId)
        {
            var userIdText = userId.ToString();
            var mapping = UserMappings?.Find(m => string.Equals(m.UserId?.Trim(), userIdText, StringComparison.OrdinalIgnoreCase));
            return (
                Math.Clamp(mapping?.AudioLowGainPercentOverride ?? AudioLowGainPercent, MinAudioBandGainPercent, MaxAudioBandGainPercent),
                Math.Clamp(mapping?.AudioMidGainPercentOverride ?? AudioMidGainPercent, MinAudioBandGainPercent, MaxAudioBandGainPercent),
                Math.Clamp(mapping?.AudioHighGainPercentOverride ?? AudioHighGainPercent, MinAudioBandGainPercent, MaxAudioBandGainPercent));
        }

        /// <summary>
        /// Gets the effective temporal smoothing applied to audio analysis windows for a
        /// user. Missing overrides inherit the global setting; invalid hand-edited values
        /// are clamped for runtime continuity while validation reports the bad value.
        /// </summary>
        public int GetAudioResponseSmoothingPercentForUser(Guid userId)
        {
            var userIdText = userId.ToString();
            var mapping = UserMappings?.Find(m => string.Equals(m.UserId?.Trim(), userIdText, StringComparison.OrdinalIgnoreCase));
            return Math.Clamp(
                mapping?.AudioResponseSmoothingPercentOverride ?? AudioResponseSmoothingPercent,
                MinAudioResponseSmoothingPercent,
                MaxAudioResponseSmoothingPercent);
        }

        /// <summary>
        /// Gets the effective audio-band spread for a user. A missing override inherits the
        /// global setting; invalid hand-edited values are clamped for runtime continuity.
        /// </summary>
        public int GetAudioBandSpreadPercentForUser(Guid userId)
        {
            var userIdText = userId.ToString();
            var mapping = UserMappings?.Find(m => string.Equals(m.UserId?.Trim(), userIdText, StringComparison.OrdinalIgnoreCase));
            return Math.Clamp(
                mapping?.AudioBandSpreadPercentOverride ?? AudioBandSpreadPercent,
                MinAudioBandSpreadPercent,
                MaxAudioBandSpreadPercent);
        }

        /// <summary>
        /// Gets the effective audio beat-pulse response for a user. A missing override
        /// inherits the global setting; invalid hand-edited values are clamped for
        /// runtime continuity.
        /// </summary>
        public int GetAudioBeatPulsePercentForUser(Guid userId)
        {
            var userIdText = userId.ToString();
            var mapping = UserMappings?.Find(m => string.Equals(m.UserId?.Trim(), userIdText, StringComparison.OrdinalIgnoreCase));
            return Math.Clamp(
                mapping?.AudioBeatPulsePercentOverride ?? AudioBeatPulsePercent,
                MinAudioBeatPulsePercent,
                MaxAudioBeatPulsePercent);
        }

        /// <summary>
        /// Gets the effective audio beat-pulse release for a user. A missing override
        /// inherits the global setting; invalid hand-edited values are clamped for
        /// runtime continuity.
        /// </summary>
        public int GetAudioBeatPulseDecayPercentForUser(Guid userId)
        {
            var userIdText = userId.ToString();
            var mapping = UserMappings?.Find(m => string.Equals(m.UserId?.Trim(), userIdText, StringComparison.OrdinalIgnoreCase));
            return Math.Clamp(
                mapping?.AudioBeatPulseDecayPercentOverride ?? AudioBeatPulseDecayPercent,
                MinAudioBeatPulseDecayPercent,
                MaxAudioBeatPulseDecayPercent);
        }

        /// <summary>
        /// Gets the effective minimum onset threshold for a user's beat pulse. A missing
        /// override inherits the global setting; invalid hand-edited values are clamped
        /// for runtime continuity while validation reports the bad value.
        /// </summary>
        public int GetAudioBeatPulseThresholdPercentForUser(Guid userId)
        {
            var userIdText = userId.ToString();
            var mapping = UserMappings?.Find(m => string.Equals(m.UserId?.Trim(), userIdText, StringComparison.OrdinalIgnoreCase));
            return Math.Clamp(
                mapping?.AudioBeatPulseThresholdPercentOverride ?? AudioBeatPulseThresholdPercent,
                MinAudioBeatPulseThresholdPercent,
                MaxAudioBeatPulseThresholdPercent);
        }

        /// <summary>
        /// Gets the effective audio visualizer palette for a user. A missing or blank
        /// override inherits the global palette; invalid hand-edited values also fall
        /// back to the global palette for runtime continuity while validation reports
        /// the bad value.
        /// </summary>
        public string GetAudioColorPaletteForUser(Guid userId)
        {
            var userIdText = userId.ToString();
            var mapping = UserMappings?.Find(m => string.Equals(m.UserId?.Trim(), userIdText, StringComparison.OrdinalIgnoreCase));
            var overridePalette = NormalizeOptionalAudioColorPalette(mapping?.AudioColorPaletteOverride);
            if (overridePalette != null && TryNormalizeAudioColorPalette(overridePalette, out var normalizedOverride))
                return normalizedOverride;

            return TryNormalizeAudioColorPalette(AudioColorPalette, out var normalizedGlobal)
                ? normalizedGlobal
                : AudioColorPaletteSpectrum;
        }

        /// <summary>
        /// Gets the effective audio spatial routing mode for a user. A missing or blank
        /// override inherits the global mode; invalid hand-edited values fall back to the
        /// global mode for runtime continuity while validation reports the bad value.
        /// </summary>
        public string GetAudioSpatialModeForUser(Guid userId)
        {
            var userIdText = userId.ToString();
            var mapping = UserMappings?.Find(m => string.Equals(m.UserId?.Trim(), userIdText, StringComparison.OrdinalIgnoreCase));
            var overrideMode = NormalizeOptionalAudioSpatialMode(mapping?.AudioSpatialModeOverride);
            if (overrideMode != null && TryNormalizeAudioSpatialMode(overrideMode, out var normalizedOverride))
                return normalizedOverride;

            return TryNormalizeAudioSpatialMode(AudioSpatialMode, out var normalizedGlobal)
                ? normalizedGlobal
                : AudioSpatialModeSpatial;
        }

        /// <summary>
        /// Gets the effective audio source-channel mode for a user. Blank overrides inherit
        /// the global mode; invalid hand-edited values fall back safely while validation
        /// reports the bad value.
        /// </summary>
        public string GetAudioChannelModeForUser(Guid userId)
        {
            var userIdText = userId.ToString();
            var mapping = UserMappings?.Find(m => string.Equals(m.UserId?.Trim(), userIdText, StringComparison.OrdinalIgnoreCase));
            var overrideMode = NormalizeOptionalAudioChannelMode(mapping?.AudioChannelModeOverride);
            if (overrideMode != null && TryNormalizeAudioChannelMode(overrideMode, out var normalizedOverride))
                return normalizedOverride;

            return TryNormalizeAudioChannelMode(AudioChannelMode, out var normalizedGlobal)
                ? normalizedGlobal
                : AudioChannelModeMono;
        }

        /// <summary>
        /// Gets the effective low/mid/high audio analysis center frequencies for a user.
        /// Missing overrides inherit the global profile; malformed persisted values fall
        /// back to the safe default profile for runtime continuity.
        /// </summary>
        public (int LowFrequencyHz, int MidFrequencyHz, int HighFrequencyHz) GetAudioFrequenciesForUser(Guid userId)
        {
            var userIdText = userId.ToString();
            var mapping = UserMappings?.Find(m => string.Equals(m.UserId?.Trim(), userIdText, StringComparison.OrdinalIgnoreCase));
            return NormalizeAudioFrequencyProfile(
                mapping?.AudioLowFrequencyHzOverride ?? AudioLowFrequencyHz,
                mapping?.AudioMidFrequencyHzOverride ?? AudioMidFrequencyHz,
                mapping?.AudioHighFrequencyHzOverride ?? AudioHighFrequencyHz);
        }

        /// <summary>
        /// Normalizes an audio frequency profile for runtime use. Configuration validation
        /// reports invalid input, while this helper keeps a legacy or hand-edited file from
        /// producing aliased or unordered spectral bands during playback.
        /// </summary>
        public static (int LowFrequencyHz, int MidFrequencyHz, int HighFrequencyHz) NormalizeAudioFrequencyProfile(
            int lowFrequencyHz,
            int midFrequencyHz,
            int highFrequencyHz)
        {
            if (lowFrequencyHz < MinAudioFrequencyHz || lowFrequencyHz > MaxAudioFrequencyHz ||
                midFrequencyHz < MinAudioFrequencyHz || midFrequencyHz > MaxAudioFrequencyHz ||
                highFrequencyHz < MinAudioFrequencyHz || highFrequencyHz > MaxAudioFrequencyHz ||
                lowFrequencyHz >= midFrequencyHz || midFrequencyHz >= highFrequencyHz)
            {
                return (DefaultAudioLowFrequencyHz, DefaultAudioMidFrequencyHz, DefaultAudioHighFrequencyHz);
            }

            return (lowFrequencyHz, midFrequencyHz, highFrequencyHz);
        }

        /// <summary>
        /// Gets the effective playback media scope for a user, falling back to the global
        /// setting when the mapping does not override it. Invalid legacy values are kept
        /// fail-open for playback; normal configuration validation still reports them.
        /// </summary>
        public string GetPlaybackMediaFilterForUser(Guid userId)
        {
            var overrideFilter = GetPlaybackMediaFilterOverrideForUser(userId);
            if (overrideFilter != null && TryNormalizePlaybackMediaFilter(overrideFilter, out var normalizedOverride))
                return normalizedOverride;

            return TryNormalizePlaybackMediaFilter(PlaybackMediaFilter, out var normalizedGlobal)
                ? normalizedGlobal
                : PlaybackMediaFilterAllVideo;
        }

        /// <summary>
        /// Gets optional per-user playback and restoration overrides. Null values mean the global
        /// plugin setting should be used for that component.
        /// </summary>
        public (
            bool? UseCinemaMode,
            int? BrightnessDimLevel,
            bool? RestoreLightState) GetPlaybackOverridesForUser(Guid userId)
        {
            var userIdText = userId.ToString();
            var mapping = UserMappings?.Find(m => string.Equals(m.UserId?.Trim(), userIdText, StringComparison.OrdinalIgnoreCase));
            return mapping == null
                ? (null, null, null)
                : (mapping.UseCinemaModeOverride, mapping.BrightnessDimLevelOverride, mapping.RestoreLightStateOverride);
        }

        /// <summary>
        /// Gets an optional per-user pause behavior override. A blank value means the global
        /// plugin setting should be used.
        /// </summary>
        public string? GetPauseBehaviorOverrideForUser(Guid userId)
        {
            var userIdText = userId.ToString();
            var mapping = UserMappings?.Find(m => string.Equals(m.UserId?.Trim(), userIdText, StringComparison.OrdinalIgnoreCase));
            var pauseBehavior = mapping?.PauseBehaviorOverride?.Trim();
            return string.IsNullOrWhiteSpace(pauseBehavior) ? null : pauseBehavior;
        }

        /// <summary>
        /// Gets optional per-user color-processing overrides. Null values mean the global
        /// plugin setting should be used for that component.
        /// </summary>
        public (int? BrightnessBoost, int? ColorSaturation, int? HueShiftDegrees, int? OutputBrightnessPercent)
            GetColorProcessingOverridesForUser(Guid userId)
        {
            var userIdText = userId.ToString();
            var mapping = UserMappings?.Find(m => string.Equals(m.UserId?.Trim(), userIdText, StringComparison.OrdinalIgnoreCase));
            return mapping == null
                ? (null, null, null, null)
                : (
                    mapping.BrightnessBoostOverride,
                    mapping.ColorSaturationOverride,
                    mapping.HueShiftDegreesOverride,
                    mapping.OutputBrightnessPercentOverride);
        }

        /// <summary>
        /// Gets optional per-user RGB channel gains. Null values mean the global channel
        /// gain should be used for that component.
        /// </summary>
        public (int? RedGain, int? GreenGain, int? BlueGain) GetColorChannelGainOverridesForUser(Guid userId)
        {
            var userIdText = userId.ToString();
            var mapping = UserMappings?.Find(m => string.Equals(m.UserId?.Trim(), userIdText, StringComparison.OrdinalIgnoreCase));
            return mapping == null
                ? (null, null, null)
                : (mapping.RedGainOverride, mapping.GreenGainOverride, mapping.BlueGainOverride);
        }

        /// <summary>
        /// Gets optional per-user scene threshold overrides. Null values mean the global
        /// blackout or color-change threshold should be used.
        /// </summary>
        public (int? BlackoutThreshold, int? ColorChangeThreshold) GetColorThresholdOverridesForUser(Guid userId)
        {
            var userIdText = userId.ToString();
            var mapping = UserMappings?.Find(m => string.Equals(m.UserId?.Trim(), userIdText, StringComparison.OrdinalIgnoreCase));
            return mapping == null
                ? (null, null)
                : (mapping.BlackoutThresholdOverride, mapping.ColorChangeThresholdOverride);
        }

        /// <summary>
        /// Gets optional per-user playback-performance overrides. Null values mean the global
        /// capture or processing setting should be used for that component.
        /// </summary>
        public (
            int? TargetFps,
            string? FrameResolution,
            string? VideoScalingMode,
            string? VideoDeinterlaceMode,
            int? SamplingBreadthPercent,
            string? SamplingMode,
            int? ColorSmoothingPercent) GetPerformanceOverridesForUser(Guid userId)
        {
            var userIdText = userId.ToString();
            var mapping = UserMappings?.Find(m => string.Equals(m.UserId?.Trim(), userIdText, StringComparison.OrdinalIgnoreCase));
            return mapping == null
                ? (null, null, null, null, null, null, null)
                : (
                    mapping.TargetFpsOverride,
                    NormalizeOptionalOverride(mapping.FrameResolutionOverride),
                    NormalizeOptionalOverride(mapping.VideoScalingModeOverride),
                    NormalizeOptionalOverride(mapping.VideoDeinterlaceModeOverride),
                    mapping.SamplingBreadthPercentOverride,
                    NormalizeOptionalOverride(mapping.SamplingModeOverride),
                    mapping.ColorSmoothingPercentOverride);
        }

        /// <summary>
        /// Gets optional per-user FFmpeg and network execution overrides. Null values mean the
        /// global execution setting should be used for that component.
        /// </summary>
        public (
            bool? UseGpu,
            string? CustomFfmpegFlags,
            int? FfmpegStallTimeoutSeconds,
            int? NetworkRetryAttempts) GetExecutionOverridesForUser(Guid userId)
        {
            var userIdText = userId.ToString();
            var mapping = UserMappings?.Find(m => string.Equals(m.UserId?.Trim(), userIdText, StringComparison.OrdinalIgnoreCase));
            return mapping == null
                ? (null, null, null, null)
                : (
                    mapping.UseGpuOverride,
                    NormalizeOptionalOverride(mapping.CustomFfmpegFlagsOverride),
                    mapping.FfmpegStallTimeoutSecondsOverride,
                    mapping.NetworkRetryAttemptsOverride);
        }

        /// <summary>
        /// Gets optional per-user entertainment channel IDs. A null result means the mapping
        /// inherits the global channel selection.
        /// </summary>
        public IReadOnlySet<int>? GetChannelIdsOverrideForUser(Guid userId)
        {
            var userIdText = userId.ToString();
            var mapping = UserMappings?.Find(m => string.Equals(m.UserId?.Trim(), userIdText, StringComparison.OrdinalIgnoreCase));
            if (mapping == null || !TryParseChannelIds(mapping.ChannelIdsOverride, out var channelIds) || channelIds.Count == 0)
                return null;

            return channelIds;
        }

        /// <summary>
        /// Gets the optional global entertainment channel selection. A null result means every
        /// channel in the selected area should be used.
        /// </summary>
        public IReadOnlySet<int>? GetGlobalChannelIds()
        {
            if (!TryParseChannelIds(ChannelIds, out var channelIds) || channelIds.Count == 0)
                return null;

            return channelIds;
        }

        /// <summary>
        /// Gets the effective entertainment channel selection for a user. A populated per-user
        /// profile wins; otherwise the global profile is inherited.
        /// </summary>
        public IReadOnlySet<int>? GetChannelIdsForUser(Guid userId)
            => GetChannelIdsOverrideForUser(userId) ?? GetGlobalChannelIds();

        private static string? NormalizeOptionalOverride(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        /// <summary>
        /// Validates optional per-user color profile overrides without exposing bridge credentials.
        /// </summary>
        public static List<string> ValidateColorOverrides(UserBridgeMapping mapping, string label = "User mapping")
        {
            var errors = new List<string>();

            if (mapping.BrightnessBoostOverride.HasValue &&
                (mapping.BrightnessBoostOverride.Value < MinBrightnessBoost ||
                 mapping.BrightnessBoostOverride.Value > MaxBrightnessBoost))
            {
                errors.Add($"{label} brightness boost override must be between 50 and 200");
            }

            if (mapping.ColorSaturationOverride.HasValue &&
                (mapping.ColorSaturationOverride.Value < MinColorSaturation ||
                 mapping.ColorSaturationOverride.Value > MaxColorSaturation))
            {
                errors.Add($"{label} color saturation override must be between 0 and 200");
            }

            if (mapping.HueShiftDegreesOverride.HasValue &&
                (mapping.HueShiftDegreesOverride.Value < MinHueShiftDegrees ||
                 mapping.HueShiftDegreesOverride.Value > MaxHueShiftDegrees))
            {
                errors.Add($"{label} hue shift override must be between -180 and 180 degrees");
            }

            if (mapping.OutputBrightnessPercentOverride.HasValue &&
                (mapping.OutputBrightnessPercentOverride.Value < MinOutputBrightnessPercent ||
                 mapping.OutputBrightnessPercentOverride.Value > MaxOutputBrightnessPercent))
            {
                errors.Add($"{label} output brightness override must be between 0 and 100 percent");
            }

            if (mapping.BlackoutThresholdOverride.HasValue &&
                (mapping.BlackoutThresholdOverride.Value < MinByteSetting ||
                 mapping.BlackoutThresholdOverride.Value > MaxByteSetting))
            {
                errors.Add($"{label} blackout threshold override must be between 0 and 255");
            }

            if (mapping.ColorChangeThresholdOverride.HasValue &&
                (mapping.ColorChangeThresholdOverride.Value < MinByteSetting ||
                 mapping.ColorChangeThresholdOverride.Value > MaxByteSetting))
            {
                errors.Add($"{label} color change threshold override must be between 0 and 255");
            }

            if (mapping.RedGainOverride.HasValue &&
                (mapping.RedGainOverride.Value < MinColorChannelGain ||
                 mapping.RedGainOverride.Value > MaxColorChannelGain))
            {
                errors.Add($"{label} red gain override must be between 50 and 200");
            }

            if (mapping.GreenGainOverride.HasValue &&
                (mapping.GreenGainOverride.Value < MinColorChannelGain ||
                 mapping.GreenGainOverride.Value > MaxColorChannelGain))
            {
                errors.Add($"{label} green gain override must be between 50 and 200");
            }

            if (mapping.BlueGainOverride.HasValue &&
                (mapping.BlueGainOverride.Value < MinColorChannelGain ||
                 mapping.BlueGainOverride.Value > MaxColorChannelGain))
            {
                errors.Add($"{label} blue gain override must be between 50 and 200");
            }

            return errors;
        }

        /// <summary>
        /// Validates optional per-user cinema-mode overrides.
        /// </summary>
        public static List<string> ValidatePlaybackOverrides(UserBridgeMapping mapping, string label = "User mapping")
        {
            var errors = new List<string>();

            if (mapping.BrightnessDimLevelOverride.HasValue &&
                (mapping.BrightnessDimLevelOverride.Value < MinBrightnessDimLevel ||
                 mapping.BrightnessDimLevelOverride.Value > MaxBrightnessDimLevel))
            {
                errors.Add($"{label} brightness dim level override must be between 0 and 100");
            }

            var pauseBehaviorOverride = mapping.PauseBehaviorOverride?.Trim();
            if (!string.IsNullOrWhiteSpace(pauseBehaviorOverride) &&
                !string.Equals(pauseBehaviorOverride, PauseBehaviorKeepLastColors, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(pauseBehaviorOverride, PauseBehaviorRestoreLightState, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{label} pause behavior override must be KeepLastColors or RestoreLightState");
            }

            var playbackMediaFilterOverride = mapping.PlaybackMediaFilterOverride?.Trim();
            if (!string.IsNullOrWhiteSpace(playbackMediaFilterOverride) &&
                !TryNormalizePlaybackMediaFilter(playbackMediaFilterOverride, out _))
            {
                errors.Add($"{label} playback media scope override must be AllVideo, Movies, Episodes, OtherVideo, Audio, or AllMedia");
            }

            return errors;
        }

        /// <summary>
        /// Validates optional per-user playback-performance overrides.
        /// </summary>
        public static List<string> ValidatePerformanceOverrides(UserBridgeMapping mapping, string label = "User mapping")
        {
            var errors = new List<string>();

            if (mapping.AudioSensitivityPercentOverride.HasValue &&
                (mapping.AudioSensitivityPercentOverride.Value < MinAudioSensitivityPercent ||
                 mapping.AudioSensitivityPercentOverride.Value > MaxAudioSensitivityPercent))
            {
                errors.Add($"{label} audio sensitivity override must be between {MinAudioSensitivityPercent} and {MaxAudioSensitivityPercent} percent");
            }

            if (mapping.AudioNoiseGatePercentOverride.HasValue &&
                (mapping.AudioNoiseGatePercentOverride.Value < MinAudioNoiseGatePercent ||
                 mapping.AudioNoiseGatePercentOverride.Value > MaxAudioNoiseGatePercent))
            {
                errors.Add($"{label} audio noise gate override must be between {MinAudioNoiseGatePercent} and {MaxAudioNoiseGatePercent} percent");
            }

            ValidateAudioFrequencyOverride(mapping.AudioLowFrequencyHzOverride, $"{label} audio low frequency override", errors);
            ValidateAudioFrequencyOverride(mapping.AudioMidFrequencyHzOverride, $"{label} audio mid frequency override", errors);
            ValidateAudioFrequencyOverride(mapping.AudioHighFrequencyHzOverride, $"{label} audio high frequency override", errors);
            ValidateAudioBandGainOverride(mapping.AudioLowGainPercentOverride, $"{label} audio low gain override", errors);
            ValidateAudioBandGainOverride(mapping.AudioMidGainPercentOverride, $"{label} audio mid gain override", errors);
            ValidateAudioBandGainOverride(mapping.AudioHighGainPercentOverride, $"{label} audio high gain override", errors);
            if (mapping.AudioResponseSmoothingPercentOverride.HasValue &&
                (mapping.AudioResponseSmoothingPercentOverride.Value < MinAudioResponseSmoothingPercent ||
                 mapping.AudioResponseSmoothingPercentOverride.Value > MaxAudioResponseSmoothingPercent))
            {
                errors.Add($"{label} audio response smoothing override must be between {MinAudioResponseSmoothingPercent} and {MaxAudioResponseSmoothingPercent} percent");
            }
            if (mapping.AudioBandSpreadPercentOverride.HasValue &&
                (mapping.AudioBandSpreadPercentOverride.Value < MinAudioBandSpreadPercent ||
                 mapping.AudioBandSpreadPercentOverride.Value > MaxAudioBandSpreadPercent))
            {
                errors.Add($"{label} audio band spread override must be between {MinAudioBandSpreadPercent} and {MaxAudioBandSpreadPercent} percent");
            }
            if (mapping.AudioBeatPulsePercentOverride.HasValue &&
                (mapping.AudioBeatPulsePercentOverride.Value < MinAudioBeatPulsePercent ||
                 mapping.AudioBeatPulsePercentOverride.Value > MaxAudioBeatPulsePercent))
            {
                errors.Add($"{label} audio beat pulse override must be between {MinAudioBeatPulsePercent} and {MaxAudioBeatPulsePercent} percent");
            }
            if (mapping.AudioBeatPulseDecayPercentOverride.HasValue &&
                (mapping.AudioBeatPulseDecayPercentOverride.Value < MinAudioBeatPulseDecayPercent ||
                 mapping.AudioBeatPulseDecayPercentOverride.Value > MaxAudioBeatPulseDecayPercent))
            {
                errors.Add($"{label} audio beat pulse decay override must be between {MinAudioBeatPulseDecayPercent} and {MaxAudioBeatPulseDecayPercent} percent");
            }
            if (mapping.AudioBeatPulseThresholdPercentOverride.HasValue &&
                (mapping.AudioBeatPulseThresholdPercentOverride.Value < MinAudioBeatPulseThresholdPercent ||
                 mapping.AudioBeatPulseThresholdPercentOverride.Value > MaxAudioBeatPulseThresholdPercent))
            {
                errors.Add($"{label} audio beat pulse threshold override must be between {MinAudioBeatPulseThresholdPercent} and {MaxAudioBeatPulseThresholdPercent} percent");
            }
            var audioColorPaletteOverride = mapping.AudioColorPaletteOverride?.Trim();
            if (!string.IsNullOrWhiteSpace(audioColorPaletteOverride) &&
                !TryNormalizeAudioColorPalette(audioColorPaletteOverride, out _))
            {
                errors.Add($"{label} audio color palette override must be Spectrum, Band, Warm, Cool, or Monochrome");
            }
            var audioSpatialModeOverride = mapping.AudioSpatialModeOverride?.Trim();
            if (!string.IsNullOrWhiteSpace(audioSpatialModeOverride) &&
                !TryNormalizeAudioSpatialMode(audioSpatialModeOverride, out _))
            {
                errors.Add($"{label} audio spatial mode override must be Spatial, Uniform, or Mirror");
            }
            var audioChannelModeOverride = mapping.AudioChannelModeOverride?.Trim();
            if (!string.IsNullOrWhiteSpace(audioChannelModeOverride) &&
                !TryNormalizeAudioChannelMode(audioChannelModeOverride, out _))
            {
                errors.Add($"{label} audio channel mode override must be Mono, Stereo, Left, or Right");
            }
            if (mapping.AudioLowFrequencyHzOverride.HasValue &&
                mapping.AudioMidFrequencyHzOverride.HasValue &&
                mapping.AudioHighFrequencyHzOverride.HasValue &&
                (mapping.AudioLowFrequencyHzOverride.Value >= mapping.AudioMidFrequencyHzOverride.Value ||
                 mapping.AudioMidFrequencyHzOverride.Value >= mapping.AudioHighFrequencyHzOverride.Value))
            {
                errors.Add($"{label} audio frequency overrides must be strictly ordered low < mid < high");
            }

            if (mapping.TargetFpsOverride.HasValue &&
                (mapping.TargetFpsOverride.Value < MinTargetFps || mapping.TargetFpsOverride.Value > MaxTargetFps))
            {
                errors.Add($"{label} target FPS override must be between 1 and 60");
            }

            var frameResolution = mapping.FrameResolutionOverride?.Trim();
            if (!string.IsNullOrWhiteSpace(frameResolution) &&
                !string.Equals(frameResolution, FrameResolutionLow, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(frameResolution, FrameResolutionStandard, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(frameResolution, FrameResolutionHigh, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{label} frame resolution override must be 80x45, 160x90, or 320x180");
            }

            var scalingMode = mapping.VideoScalingModeOverride?.Trim();
            if (!string.IsNullOrWhiteSpace(scalingMode) &&
                !string.Equals(scalingMode, VideoScalingModeStretch, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(scalingMode, VideoScalingModeFit, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(scalingMode, VideoScalingModeCrop, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{label} video scaling override must be Stretch, Fit, or Crop");
            }

            var deinterlaceMode = mapping.VideoDeinterlaceModeOverride?.Trim();
            if (!string.IsNullOrWhiteSpace(deinterlaceMode) &&
                !string.Equals(deinterlaceMode, VideoDeinterlaceModeOff, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(deinterlaceMode, VideoDeinterlaceModeAuto, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(deinterlaceMode, VideoDeinterlaceModeOn, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{label} video deinterlace override must be Off, Auto, or On");
            }

            if (mapping.SamplingBreadthPercentOverride.HasValue &&
                (mapping.SamplingBreadthPercentOverride.Value < MinSamplingBreadthPercent ||
                 mapping.SamplingBreadthPercentOverride.Value > MaxSamplingBreadthPercent))
            {
                errors.Add($"{label} sampling breadth override must be between 1 and 50 percent");
            }

            var samplingMode = mapping.SamplingModeOverride?.Trim();
            if (!string.IsNullOrWhiteSpace(samplingMode) &&
                !string.Equals(samplingMode, SamplingModeAverage, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(samplingMode, SamplingModeCenterWeighted, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(samplingMode, SamplingModeCenterPixel, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{label} sampling mode override must be Average, CenterWeighted, or CenterPixel");
            }

            if (mapping.ColorSmoothingPercentOverride.HasValue &&
                (mapping.ColorSmoothingPercentOverride.Value < MinColorSmoothingPercent ||
                 mapping.ColorSmoothingPercentOverride.Value > MaxColorSmoothingPercent))
            {
                errors.Add($"{label} color smoothing override must be between 0 and 90 percent");
            }

            return errors;
        }

        private static void ValidateAudioFrequencyOverride(int? value, string label, List<string> errors)
        {
            if (value.HasValue && (value.Value < MinAudioFrequencyHz || value.Value > MaxAudioFrequencyHz))
                errors.Add($"{label} must be between {MinAudioFrequencyHz} and {MaxAudioFrequencyHz} Hz");
        }

        private static void ValidateAudioBandGainOverride(int? value, string label, List<string> errors)
        {
            if (value.HasValue && (value.Value < MinAudioBandGainPercent || value.Value > MaxAudioBandGainPercent))
                errors.Add($"{label} must be between {MinAudioBandGainPercent} and {MaxAudioBandGainPercent} percent");
        }

        /// <summary>
        /// Validates optional per-user FFmpeg and network execution overrides.
        /// </summary>
        public static List<string> ValidateExecutionOverrides(UserBridgeMapping mapping, string label = "User mapping")
        {
            var errors = new List<string>();

            if (!string.IsNullOrWhiteSpace(mapping.CustomFfmpegFlagsOverride))
            {
                try
                {
                    _ = FfmpegStreamer.ParseCustomArguments(mapping.CustomFfmpegFlagsOverride);
                }
                catch (FormatException ex)
                {
                    errors.Add($"{label} custom FFmpeg flags are invalid: {ex.Message}");
                }
            }

            if (mapping.FfmpegStallTimeoutSecondsOverride.HasValue &&
                (mapping.FfmpegStallTimeoutSecondsOverride.Value < MinFfmpegStallTimeoutSeconds ||
                 mapping.FfmpegStallTimeoutSecondsOverride.Value > MaxFfmpegStallTimeoutSeconds))
            {
                errors.Add($"{label} FFmpeg stall timeout override must be between 1 and 60 seconds");
            }

            if (mapping.NetworkRetryAttemptsOverride.HasValue &&
                (mapping.NetworkRetryAttemptsOverride.Value < MinNetworkRetryAttempts ||
                 mapping.NetworkRetryAttemptsOverride.Value > MaxNetworkRetryAttempts))
            {
                errors.Add($"{label} network retry attempts override must be between 0 and 10");
            }

            return errors;
        }

        /// <summary>
        /// Parses an optional comma-, semicolon-, or whitespace-separated channel ID list.
        /// </summary>
        public static bool TryParseChannelIds(string? value, out HashSet<int> channelIds)
        {
            channelIds = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(value))
                return true;

            var tokens = value.Split(
                new[] { ',', ';', ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (var token in tokens)
            {
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var channelId) ||
                    channelId < ushort.MinValue ||
                    channelId > ushort.MaxValue)
                {
                    channelIds.Clear();
                    return false;
                }

                channelIds.Add(channelId);
            }

            return channelIds.Count > 0;
        }

        /// <summary>
        /// Validates an optional per-user channel selection override.
        /// </summary>
        public static List<string> ValidateChannelOverrides(UserBridgeMapping mapping, string label = "User mapping")
        {
            var errors = new List<string>();
            if (!TryParseChannelIds(mapping.ChannelIdsOverride, out _))
                errors.Add($"{label} channel IDs override must be a comma-separated list of IDs from 0 to 65535");

            return errors;
        }

        /// <summary>
        /// Validates the optional global entertainment channel selection.
        /// </summary>
        public static List<string> ValidateGlobalChannelIds(string? channelIds, string label = "Global")
        {
            var errors = new List<string>();
            if (!TryParseChannelIds(channelIds, out _))
                errors.Add($"{label} channel IDs must be a comma-separated list of IDs from 0 to 65535");

            return errors;
        }

        /// <summary>
        /// Validates one reusable scene-effect preview preset.
        /// </summary>
        public static List<string> ValidateColorPreset(HueColorPreset? preset, string label = "Color preset")
        {
            var errors = new List<string>();
            if (preset == null)
            {
                errors.Add($"{label} is required");
                return errors;
            }

            var name = preset.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                errors.Add($"{label} name is required");
            else if (name.Length > MaxColorPresetNameLength)
                errors.Add($"{label} name must be {MaxColorPresetNameLength} characters or fewer");
            else if (name.Any(char.IsControl))
                errors.Add($"{label} name must not contain control characters");
            else if (name.IndexOfAny(new[] { '/', '\\', '?', '#' }) >= 0)
                errors.Add($"{label} name must not contain path or URL separator characters");

            if (!TryNormalizeColorPresetEffect(preset.Effect, out _))
                errors.Add($"{label} effect must be one of {string.Join(", ", ColorPresetEffects)}");

            if (preset.EffectSpeedPercent < MinColorPresetEffectSpeedPercent ||
                preset.EffectSpeedPercent > MaxColorPresetEffectSpeedPercent)
            {
                errors.Add($"{label} effect speed must be between {MinColorPresetEffectSpeedPercent} and {MaxColorPresetEffectSpeedPercent} percent");
            }

            if (preset.Red < MinByteSetting || preset.Red > MaxByteSetting ||
                preset.Green < MinByteSetting || preset.Green > MaxByteSetting ||
                preset.Blue < MinByteSetting || preset.Blue > MaxByteSetting)
            {
                errors.Add($"{label} RGB values must be between 0 and 255");
            }

            if (preset.BrightnessPercent < MinOutputBrightnessPercent ||
                preset.BrightnessPercent > MaxOutputBrightnessPercent)
            {
                errors.Add($"{label} brightness must be between 0 and 100 percent");
            }

            if (preset.DurationSeconds < MinPreviewDurationSeconds ||
                preset.DurationSeconds > MaxPreviewDurationSeconds)
            {
                errors.Add($"{label} duration must be between {MinPreviewDurationSeconds} and {MaxPreviewDurationSeconds} seconds");
            }

            if (preset.TransitionSeconds < MinColorPresetTransitionSeconds ||
                preset.TransitionSeconds > MaxColorPresetTransitionSeconds)
            {
                errors.Add($"{label} transition must be between {MinColorPresetTransitionSeconds} and {MaxColorPresetTransitionSeconds} seconds");
            }
            else if (preset.TransitionSeconds > preset.DurationSeconds)
            {
                errors.Add($"{label} transition cannot exceed the scene duration");
            }

            if (preset.TransitionOutSeconds < MinColorPresetTransitionOutSeconds ||
                preset.TransitionOutSeconds > MaxColorPresetTransitionOutSeconds)
            {
                errors.Add($"{label} fade-out must be between {MinColorPresetTransitionOutSeconds} and {MaxColorPresetTransitionOutSeconds} seconds");
            }
            else if (preset.TransitionSeconds + preset.TransitionOutSeconds > preset.DurationSeconds)
            {
                errors.Add($"{label} fade-in and fade-out cannot exceed the scene duration together");
            }

            return errors;
        }

        /// <summary>
        /// Validates one ordered saved-scene playlist. Playlist items reference existing
        /// color presets by name so a playlist remains credential-free and portable.
        /// </summary>
        public static List<string> ValidateScenePlaylist(
            HueScenePlaylist? playlist,
            PluginConfiguration? configuration = null,
            string label = "Scene playlist")
        {
            var errors = new List<string>();
            if (playlist == null)
            {
                errors.Add($"{label} is required");
                return errors;
            }

            if (string.IsNullOrWhiteSpace(playlist.Id))
                errors.Add($"{label} requires an ID");

            var name = playlist.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                errors.Add($"{label} name is required");
            else if (name.Length > MaxScenePlaylistNameLength)
                errors.Add($"{label} name must be {MaxScenePlaylistNameLength} characters or fewer");
            else if (name.Any(char.IsControl))
                errors.Add($"{label} name must not contain control characters");
            else if (name.IndexOfAny(new[] { '/', '\\', '?', '#' }) >= 0)
                errors.Add($"{label} name must not contain path or URL separator characters");

            var presetNames = playlist.PresetNames ?? new List<string>();
            if (presetNames.Count < 1 || presetNames.Count > MaxScenePlaylistItems)
            {
                errors.Add($"{label} must contain between 1 and {MaxScenePlaylistItems} saved scenes");
            }

            if (playlist.RepeatCount < MinScenePlaylistRepeatCount ||
                playlist.RepeatCount > MaxScenePlaylistRepeatCount)
            {
                errors.Add($"{label} repeat count must be between {MinScenePlaylistRepeatCount} and {MaxScenePlaylistRepeatCount}");
            }

            for (var index = 0; index < presetNames.Count; index++)
            {
                var presetName = presetNames[index]?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(presetName))
                {
                    errors.Add($"{label} scene {index + 1} is required");
                    continue;
                }

                if (configuration != null &&
                    !(configuration.ColorPresets ?? new List<HueColorPreset>()).Any(preset =>
                        preset != null &&
                        string.Equals(preset.Name?.Trim(), presetName, StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add($"{label} references a saved scene that does not exist: {presetName}");
                }
            }

            if (configuration != null &&
                playlist.RepeatCount >= MinScenePlaylistRepeatCount &&
                playlist.RepeatCount <= MaxScenePlaylistRepeatCount)
            {
                var presets = presetNames
                    .Select(presetName => (configuration.ColorPresets ?? new List<HueColorPreset>())
                        .FirstOrDefault(preset =>
                            preset != null &&
                            string.Equals(preset.Name?.Trim(), presetName?.Trim(), StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                if (presets.All(preset => preset != null))
                {
                    var totalDuration = presets
                        .Select(preset => Math.Clamp(
                            preset!.DurationSeconds,
                            MinPreviewDurationSeconds,
                            MaxPreviewDurationSeconds))
                        .Sum() * playlist.RepeatCount;
                    if (totalDuration > MaxScenePlaylistTotalDurationSeconds)
                    {
                        errors.Add($"{label} repeated duration cannot exceed {MaxScenePlaylistTotalDurationSeconds} seconds");
                    }
                }
            }

            var targetUserId = playlist.TargetUserId?.Trim() ?? string.Empty;
            var targetUserIds = playlist.TargetUserIds ?? new List<string>();
            if (targetUserIds.Count > MaxSceneScheduleTargetMappings)
                errors.Add($"{label} may select no more than {MaxSceneScheduleTargetMappings} user mappings");

            var seenTargetUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < targetUserIds.Count; index++)
            {
                var selectedUserId = targetUserIds[index]?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(selectedUserId))
                {
                    errors.Add($"{label} selected user mapping {index + 1} is required");
                    continue;
                }

                if (!seenTargetUserIds.Add(selectedUserId))
                {
                    errors.Add($"{label} selects user mapping {selectedUserId} more than once");
                    continue;
                }

                var mapping = configuration?.UserMappings?.FirstOrDefault(candidate =>
                    candidate != null &&
                    string.Equals(candidate.UserId?.Trim(), selectedUserId, StringComparison.OrdinalIgnoreCase));
                if (mapping == null)
                    errors.Add($"{label} references a selected user mapping that does not exist: {selectedUserId}");
                else if (!mapping.SyncEnabled)
                    errors.Add($"{label} references a disabled selected user mapping: {selectedUserId}");
            }

            var hasSelectedTargets = playlist.IncludeDefaultTarget || targetUserIds.Count > 0;
            if (playlist.TargetAllEnabledMappings && !string.IsNullOrWhiteSpace(targetUserId) && !hasSelectedTargets)
                errors.Add($"{label} cannot select all enabled targets and a specific user mapping together");
            else if (playlist.TargetAllEnabledMappings && hasSelectedTargets)
                errors.Add($"{label} cannot combine all enabled targets with a specific or selected target");

            if (!string.IsNullOrWhiteSpace(targetUserId) && hasSelectedTargets)
                errors.Add($"{label} cannot combine a specific user mapping with selected targets");

            if (!string.IsNullOrWhiteSpace(targetUserId))
            {
                var mapping = configuration?.UserMappings?.FirstOrDefault(candidate =>
                    candidate != null &&
                    string.Equals(candidate.UserId?.Trim(), targetUserId, StringComparison.OrdinalIgnoreCase));
                if (mapping == null)
                    errors.Add($"{label} references a user mapping that does not exist");
                else if (!mapping.SyncEnabled)
                    errors.Add($"{label} references a disabled user mapping");
            }

            return errors;
        }

        /// <summary>
        /// Validates the complete playlist collection, including bounded size and unique
        /// IDs/names used by the administrator page and API routes.
        /// </summary>
        public List<string> ValidateScenePlaylists()
        {
            var errors = new List<string>();
            if (ScenePlaylists == null)
                return errors;

            if (ScenePlaylists.Count > MaxScenePlaylists)
                errors.Add($"No more than {MaxScenePlaylists} scene playlists may be saved");

            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < ScenePlaylists.Count; index++)
            {
                var label = $"Scene playlist {index + 1}";
                var playlist = ScenePlaylists[index];
                errors.AddRange(ValidateScenePlaylist(playlist, this, label));
                var id = playlist?.Id?.Trim();
                if (!string.IsNullOrWhiteSpace(id) && !seenIds.Add(id))
                    errors.Add($"{label} duplicates another scene playlist ID");
                var name = playlist?.Name?.Trim();
                if (!string.IsNullOrWhiteSpace(name) && !seenNames.Add(name))
                    errors.Add($"{label} duplicates another scene playlist name");
            }

            return errors;
        }

        /// <summary>
        /// Validates the complete reusable preset collection, including names that must
        /// be unique so the UI can address a preset deterministically.
        /// </summary>
        public List<string> ValidateColorPresets()
        {
            var errors = new List<string>();
            if (ColorPresets == null)
                return errors;

            if (ColorPresets.Count > MaxColorPresets)
                errors.Add($"No more than {MaxColorPresets} color presets may be saved");

            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < ColorPresets.Count; index++)
            {
                var label = $"Color preset {index + 1}";
                errors.AddRange(ValidateColorPreset(ColorPresets[index], label));
                var name = ColorPresets[index]?.Name?.Trim();
                if (!string.IsNullOrWhiteSpace(name) && !seenNames.Add(name))
                    errors.Add($"{label} duplicates another color preset name");
            }

            return errors;
        }

        /// <summary>
        /// Validates one scene cue. Times use the selected cue timezone and are stored in
        /// 24-hour HH:mm form. A populated RunDate makes the cue one-time and ignores
        /// recurrence fields; blank RunDate uses either the recurring Sunday=1 through
        /// Saturday=64 bit-mask behavior, every calendar day, a monthly calendar day, a monthly
        /// ordinal weekday, or a yearly calendar date. RecurrenceInterval controls the number
        /// of calendar units between recurring runs and values above one require StartDate as
        /// the cadence anchor.
        /// </summary>
        public static List<string> ValidateSceneSchedule(
            HueSceneSchedule? schedule,
            PluginConfiguration? configuration = null,
            string label = "Scene schedule")
        {
            var errors = new List<string>();
            if (schedule == null)
            {
                errors.Add($"{label} is required");
                return errors;
            }

            if (string.IsNullOrWhiteSpace(schedule.Id))
                errors.Add($"{label} requires an ID");

            var name = schedule.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                errors.Add($"{label} name is required");
            else if (name.Length > MaxSceneScheduleNameLength)
                errors.Add($"{label} name must be {MaxSceneScheduleNameLength} characters or fewer");
            else if (name.Any(char.IsControl))
                errors.Add($"{label} name must not contain control characters");
            else if (name.IndexOfAny(new[] { '/', '\\', '?', '#' }) >= 0)
                errors.Add($"{label} name must not contain path or URL separator characters");

            var presetName = schedule.PresetName?.Trim() ?? string.Empty;
            var playlistName = schedule.PlaylistName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(presetName) && string.IsNullOrWhiteSpace(playlistName))
                errors.Add($"{label} requires a saved scene or playlist");
            else if (!string.IsNullOrWhiteSpace(presetName) && !string.IsNullOrWhiteSpace(playlistName))
                errors.Add($"{label} cannot reference both a saved scene and a playlist");
            else if (!string.IsNullOrWhiteSpace(presetName) && configuration?.ColorPresets != null &&
                     !configuration.ColorPresets.Any(preset =>
                         preset != null &&
                         string.Equals(preset.Name?.Trim(), presetName, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"{label} references a saved scene that does not exist");
            }
            else if (!string.IsNullOrWhiteSpace(playlistName) && configuration?.ScenePlaylists != null &&
                     !configuration.ScenePlaylists.Any(playlist =>
                         playlist != null &&
                         string.Equals(playlist.Name?.Trim(), playlistName, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"{label} references a saved playlist that does not exist");
            }

            if (schedule.Priority < MinSceneSchedulePriority ||
                schedule.Priority > MaxSceneSchedulePriority)
            {
                errors.Add($"{label} priority must be between {MinSceneSchedulePriority} and {MaxSceneSchedulePriority}");
            }

            if (!TryNormalizeSceneScheduleTime(schedule.TimeOfDay, out _))
                errors.Add($"{label} time must use 24-hour HH:mm format");

            if (!TryResolveSceneScheduleTimeZone(schedule.TimeZoneId, out _))
                errors.Add($"{label} time zone is not available on this server");

            if (!TryNormalizeSceneScheduleRecurrence(schedule.Recurrence, out var normalizedRecurrence))
                errors.Add($"{label} recurrence must be Daily, Weekly, Monthly, MonthlyWeekday, or Yearly");

            if (schedule.RecurrenceInterval < MinSceneScheduleRecurrenceInterval ||
                schedule.RecurrenceInterval > MaxSceneScheduleRecurrenceInterval)
            {
                errors.Add($"{label} recurrence interval must be between {MinSceneScheduleRecurrenceInterval} and {MaxSceneScheduleRecurrenceInterval}");
            }

            if (schedule.DayOfMonth < 0 || schedule.DayOfMonth > 31)
                errors.Add($"{label} day of month must be between 1 and 31 for monthly recurrence");

            if (!string.IsNullOrWhiteSpace(playlistName))
            {
                if (schedule.DurationSeconds != 0)
                    errors.Add($"{label} playlist duration override must be 0; each saved scene keeps its own duration");
            }
            else if (schedule.DurationSeconds != 0 &&
                     (schedule.DurationSeconds < MinPreviewDurationSeconds ||
                      schedule.DurationSeconds > MaxPreviewDurationSeconds))
            {
                errors.Add($"{label} duration override must be 0 (inherit scene duration) or between {MinPreviewDurationSeconds} and {MaxPreviewDurationSeconds} seconds");
            }

            if (schedule.MaxRuns < 0 || schedule.MaxRuns > MaxSceneScheduleRuns)
                errors.Add($"{label} maximum runs must be 0 (unlimited) or between 1 and {MaxSceneScheduleRuns}");

            if (schedule.RunCount < 0 || schedule.RunCount > MaxSceneScheduleRuns)
                errors.Add($"{label} run count must be between 0 and {MaxSceneScheduleRuns}");

            if (!TryNormalizeSceneScheduleDate(schedule.RunDate, out var normalizedRunDate))
                errors.Add($"{label} run date must use yyyy-MM-dd format");

            if (!TryNormalizeSceneScheduleDate(schedule.StartDate, out var normalizedStartDate))
                errors.Add($"{label} start date must use yyyy-MM-dd format");

            if (!TryNormalizeSceneScheduleDate(schedule.EndDate, out var normalizedEndDate))
                errors.Add($"{label} end date must use yyyy-MM-dd format");

            if (!string.IsNullOrWhiteSpace(normalizedStartDate) &&
                !string.IsNullOrWhiteSpace(normalizedEndDate) &&
                string.CompareOrdinal(normalizedStartDate, normalizedEndDate) > 0)
            {
                errors.Add($"{label} end date must be on or after the start date");
            }

            if (string.IsNullOrWhiteSpace(normalizedRunDate) &&
                schedule.RecurrenceInterval > MinSceneScheduleRecurrenceInterval &&
                string.IsNullOrWhiteSpace(normalizedStartDate))
            {
                errors.Add($"{label} recurrence intervals greater than {MinSceneScheduleRecurrenceInterval} require a start date anchor");
            }

            var excludedDates = schedule.ExcludedDates ?? new List<string>();
            if (!string.IsNullOrWhiteSpace(normalizedRunDate))
            {
                if (!string.IsNullOrWhiteSpace(normalizedStartDate) ||
                    !string.IsNullOrWhiteSpace(normalizedEndDate))
                {
                    errors.Add($"{label} one-time run date cannot be combined with a start or end date");
                }

                if (excludedDates.Any(value => !string.IsNullOrWhiteSpace(value)))
                    errors.Add($"{label} one-time run date cannot be combined with excluded dates");
            }

            if (excludedDates.Count > MaxSceneScheduleExcludedDates)
                errors.Add($"{label} may exclude no more than {MaxSceneScheduleExcludedDates} dates");

            var seenExcludedDates = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < excludedDates.Count; index++)
            {
                var value = excludedDates[index]?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                if (!TryNormalizeSceneScheduleDate(value, out var normalizedExcludedDate))
                {
                    errors.Add($"{label} excluded date {index + 1} must use yyyy-MM-dd format");
                }
                else if (!seenExcludedDates.Add(normalizedExcludedDate))
                {
                    errors.Add($"{label} excludes the date {normalizedExcludedDate} more than once");
                }
            }

            if (string.IsNullOrWhiteSpace(normalizedRunDate))
            {
                if (string.Equals(normalizedRecurrence, SceneScheduleRecurrenceMonthly, StringComparison.Ordinal))
                {
                    if (schedule.DayOfMonth < 1 || schedule.DayOfMonth > 31)
                        errors.Add($"{label} monthly recurrence requires a day of month from 1 to 31");
                }
                else if (string.Equals(normalizedRecurrence, SceneScheduleRecurrenceMonthlyWeekday, StringComparison.Ordinal))
                {
                    if (schedule.WeekOfMonth != SceneScheduleLastWeekOfMonth &&
                        (schedule.WeekOfMonth < MinSceneScheduleWeekOfMonth || schedule.WeekOfMonth > MaxSceneScheduleWeekOfMonth))
                    {
                        errors.Add($"{label} monthly-weekday recurrence requires a week of month from 1 to {MaxSceneScheduleWeekOfMonth} or {SceneScheduleLastWeekOfMonth}");
                    }

                    if (schedule.DayOfWeek < (int)System.DayOfWeek.Sunday ||
                        schedule.DayOfWeek > (int)System.DayOfWeek.Saturday)
                    {
                        errors.Add($"{label} monthly-weekday recurrence requires a day of week from Sunday through Saturday");
                    }
                }
                else if (string.Equals(normalizedRecurrence, SceneScheduleRecurrenceYearly, StringComparison.Ordinal))
                {
                    if (schedule.MonthOfYear < MinSceneScheduleMonthOfYear ||
                        schedule.MonthOfYear > MaxSceneScheduleMonthOfYear)
                    {
                        errors.Add($"{label} yearly recurrence requires a month from {MinSceneScheduleMonthOfYear} to {MaxSceneScheduleMonthOfYear}");
                    }

                    if (schedule.DayOfMonth < 1 || schedule.DayOfMonth > 31)
                        errors.Add($"{label} yearly recurrence requires a day of month from 1 to 31");
                }
                else if (string.Equals(normalizedRecurrence, SceneScheduleRecurrenceWeekly, StringComparison.Ordinal) &&
                         (schedule.DaysOfWeekMask < 1 || schedule.DaysOfWeekMask > AllSceneScheduleDaysMask))
                {
                    errors.Add($"{label} must select at least one day of the week");
                }
            }

            var targetUserId = schedule.TargetUserId?.Trim() ?? string.Empty;
            var targetUserIds = schedule.TargetUserIds ?? new List<string>();
            if (targetUserIds.Count > MaxSceneScheduleTargetMappings)
            {
                errors.Add($"{label} may select no more than {MaxSceneScheduleTargetMappings} user mappings");
            }

            var seenTargetUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < targetUserIds.Count; index++)
            {
                var selectedUserId = targetUserIds[index]?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(selectedUserId))
                {
                    errors.Add($"{label} selected user mapping {index + 1} is required");
                    continue;
                }

                if (!seenTargetUserIds.Add(selectedUserId))
                {
                    errors.Add($"{label} selects user mapping {selectedUserId} more than once");
                    continue;
                }

                var mapping = configuration?.UserMappings?.FirstOrDefault(candidate =>
                    candidate != null &&
                    string.Equals(candidate.UserId?.Trim(), selectedUserId, StringComparison.OrdinalIgnoreCase));
                if (mapping == null)
                    errors.Add($"{label} references a selected user mapping that does not exist: {selectedUserId}");
                else if (!mapping.SyncEnabled)
                    errors.Add($"{label} references a disabled selected user mapping: {selectedUserId}");
            }

            var hasSelectedTargets = schedule.IncludeDefaultTarget || targetUserIds.Count > 0;
            if (schedule.TargetAllEnabledMappings && !string.IsNullOrWhiteSpace(targetUserId) && !hasSelectedTargets)
            {
                errors.Add($"{label} cannot select all enabled targets and a specific user mapping together");
            }
            else if (schedule.TargetAllEnabledMappings && hasSelectedTargets)
            {
                errors.Add($"{label} cannot combine all enabled targets with a specific or selected target");
            }

            if (!string.IsNullOrWhiteSpace(targetUserId) && hasSelectedTargets)
                errors.Add($"{label} cannot combine a specific user mapping with selected targets");

            if (!string.IsNullOrWhiteSpace(targetUserId))
            {
                var mapping = configuration?.UserMappings?.FirstOrDefault(candidate =>
                    candidate != null &&
                    string.Equals(candidate.UserId?.Trim(), targetUserId, StringComparison.OrdinalIgnoreCase));
                if (mapping == null)
                    errors.Add($"{label} references a user mapping that does not exist");
                else if (!mapping.SyncEnabled)
                    errors.Add($"{label} references a disabled user mapping");
            }

            return errors;
        }

        /// <summary>
        /// Validates the complete recurring scene-cue collection, including unique
        /// IDs and names so the administrator page can address cues deterministically.
        /// </summary>
        public List<string> ValidateSceneSchedules()
        {
            var errors = new List<string>();
            if (SceneSchedules == null)
                return errors;

            if (SceneSchedules.Count > MaxSceneSchedules)
                errors.Add($"No more than {MaxSceneSchedules} scene schedules may be saved");

            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < SceneSchedules.Count; index++)
            {
                var label = $"Scene schedule {index + 1}";
                var schedule = SceneSchedules[index];
                errors.AddRange(ValidateSceneSchedule(schedule, this, label));
                var id = schedule?.Id?.Trim();
                if (!string.IsNullOrWhiteSpace(id) && !seenIds.Add(id))
                    errors.Add($"{label} duplicates another scene schedule ID");
                var name = schedule?.Name?.Trim();
                if (!string.IsNullOrWhiteSpace(name) && !seenNames.Add(name))
                    errors.Add($"{label} duplicates another scene schedule name");
            }

            return errors;
        }

        /// <summary>
        /// Normalizes a schedule time to a stable 24-hour HH:mm representation.
        /// </summary>
        public static bool TryNormalizeSceneScheduleTime(string? value, out string normalized)
        {
            normalized = string.Empty;
            if (!TimeSpan.TryParseExact(
                    value?.Trim(),
                    @"hh\:mm",
                    CultureInfo.InvariantCulture,
                    out var parsed) ||
                parsed < TimeSpan.Zero ||
                parsed >= TimeSpan.FromDays(1))
            {
                return false;
            }

            normalized = parsed.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>
        /// Normalizes a schedule recurrence mode. Blank values retain the original weekly
        /// weekday behavior for configurations written before daily/monthly recurrence existed.
        /// </summary>
        public static bool TryNormalizeSceneScheduleRecurrence(string? value, out string normalized)
        {
            var trimmed = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed) ||
                string.Equals(trimmed, SceneScheduleRecurrenceWeekly, StringComparison.OrdinalIgnoreCase))
            {
                normalized = SceneScheduleRecurrenceWeekly;
                return true;
            }

            if (string.Equals(trimmed, SceneScheduleRecurrenceDaily, StringComparison.OrdinalIgnoreCase))
            {
                normalized = SceneScheduleRecurrenceDaily;
                return true;
            }

            if (string.Equals(trimmed, SceneScheduleRecurrenceMonthly, StringComparison.OrdinalIgnoreCase))
            {
                normalized = SceneScheduleRecurrenceMonthly;
                return true;
            }

            if (string.Equals(trimmed, SceneScheduleRecurrenceMonthlyWeekday, StringComparison.OrdinalIgnoreCase))
            {
                normalized = SceneScheduleRecurrenceMonthlyWeekday;
                return true;
            }

            if (string.Equals(trimmed, SceneScheduleRecurrenceYearly, StringComparison.OrdinalIgnoreCase))
            {
                normalized = SceneScheduleRecurrenceYearly;
                return true;
            }

            normalized = string.Empty;
            return false;
        }

        /// <summary>
        /// Resolves an optional schedule time-zone ID. An empty ID intentionally maps to
        /// the server's local zone for backward compatibility with existing cues.
        /// </summary>
        public static bool TryResolveSceneScheduleTimeZone(string? value, out TimeZoneInfo timeZone)
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                timeZone = TimeZoneInfo.Local;
                return true;
            }

            try
            {
                timeZone = TimeZoneInfo.FindSystemTimeZoneById(normalized);
                return true;
            }
            catch (TimeZoneNotFoundException)
            {
                timeZone = TimeZoneInfo.Local;
                return false;
            }
            catch (InvalidTimeZoneException)
            {
                timeZone = TimeZoneInfo.Local;
                return false;
            }
            catch (ArgumentException)
            {
                timeZone = TimeZoneInfo.Local;
                return false;
            }
        }

        /// <summary>
        /// Normalizes an optional inclusive schedule date to yyyy-MM-dd. Blank values
        /// intentionally remain blank for backwards-compatible unbounded cues.
        /// </summary>
        public static bool TryNormalizeSceneScheduleDate(string? value, out string normalized)
        {
            normalized = string.Empty;
            var trimmed = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
                return true;

            if (!DateTime.TryParseExact(
                    trimmed,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed))
            {
                return false;
            }

            normalized = parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>
        /// Normalizes an optional excluded-date collection to unique sorted yyyy-MM-dd
        /// values. Blank entries are ignored so text-based administrator clients can send
        /// a trailing separator safely; invalid values or an oversized collection fail.
        /// </summary>
        public static bool TryNormalizeSceneScheduleExcludedDates(
            IEnumerable<string>? values,
            out List<string> normalized)
        {
            normalized = new List<string>();
            if (values == null)
                return true;

            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                if (!TryNormalizeSceneScheduleDate(value, out var normalizedDate))
                {
                    normalized.Clear();
                    return false;
                }

                if (!normalized.Contains(normalizedDate, StringComparer.Ordinal))
                    normalized.Add(normalizedDate);
            }

            if (normalized.Count > MaxSceneScheduleExcludedDates)
            {
                normalized.Clear();
                return false;
            }

            normalized.Sort(StringComparer.Ordinal);
            return true;
        }

        public PluginConfiguration()
        {
            // Defaults
        }

        /// <summary>
        /// Returns the runtime-safe completed-session retention window. Invalid
        /// hand-edited values fall back to the historical maximum while configuration
        /// validation reports the problem to the administrator.
        /// </summary>
        public int GetSessionHistoryRetentionCount()
        {
            if (SessionHistoryRetentionCount < MinSessionHistoryRetentionCount ||
                SessionHistoryRetentionCount > MaxSessionHistoryRetentionCount)
            {
                return DefaultSessionHistoryRetentionCount;
            }

            return SessionHistoryRetentionCount;
        }

        /// <summary>
        /// Returns the runtime-safe scheduled-scene retention window. Invalid
        /// hand-edited values fall back to the historical maximum while configuration
        /// validation reports the problem to the administrator.
        /// </summary>
        public int GetSceneScheduleHistoryRetentionCount()
        {
            if (SceneScheduleHistoryRetentionCount < MinSceneScheduleHistoryRetentionCount ||
                SceneScheduleHistoryRetentionCount > MaxSceneScheduleHistoryRetentionCount)
            {
                return DefaultSceneScheduleHistoryRetentionCount;
            }

            return SceneScheduleHistoryRetentionCount;
        }

        /// <summary>
        /// Gets the RGB frame dimensions used by FFmpeg and the sampling loop.
        /// Unknown values fall back to the standard resolution so older or manually
        /// edited configurations remain safe to load.
        /// </summary>
        public static (int Width, int Height) GetFrameDimensions(string? frameResolution)
        {
            if (string.Equals(frameResolution, FrameResolutionLow, StringComparison.OrdinalIgnoreCase))
                return (80, 45);

            if (string.Equals(frameResolution, FrameResolutionHigh, StringComparison.OrdinalIgnoreCase))
                return (320, 180);

            return (160, 90);
        }

        /// <summary>
        /// Validates the configuration and returns a list of validation errors
        /// </summary>
        public List<string> Validate()
        {
            var errors = new List<string>();

            errors.AddRange(ValidateColorPresets());
            errors.AddRange(ValidateScenePlaylists());
            errors.AddRange(ValidateSceneSchedules());

            if (!TryNormalizePlaybackMediaFilter(PlaybackMediaFilter, out _))
                errors.Add("Playback media scope must be AllVideo, Movies, Episodes, OtherVideo, Audio, or AllMedia");

            if (SceneAutomationCatchUpMinutes < MinSceneAutomationCatchUpMinutes ||
                SceneAutomationCatchUpMinutes > MaxSceneAutomationCatchUpMinutes)
            {
                errors.Add($"Scene automation catch-up window must be between {MinSceneAutomationCatchUpMinutes} and {MaxSceneAutomationCatchUpMinutes} minutes");
            }

            if (!TryNormalizeSceneAutomationPlaybackPolicy(SceneAutomationPlaybackPolicy, out _))
                errors.Add("Scene automation playback policy must be Skip or Defer");

            if (SceneAutomationDeferMinutes < MinSceneAutomationDeferMinutes ||
                SceneAutomationDeferMinutes > MaxSceneAutomationDeferMinutes)
            {
                errors.Add($"Scene automation defer window must be between {MinSceneAutomationDeferMinutes} and {MaxSceneAutomationDeferMinutes} minutes");
            }

            if (SessionHistoryRetentionCount < MinSessionHistoryRetentionCount ||
                SessionHistoryRetentionCount > MaxSessionHistoryRetentionCount)
            {
                errors.Add($"Session history retention must be between {MinSessionHistoryRetentionCount} and {MaxSessionHistoryRetentionCount} entries");
            }

            if (SceneScheduleHistoryRetentionCount < MinSceneScheduleHistoryRetentionCount ||
                SceneScheduleHistoryRetentionCount > MaxSceneScheduleHistoryRetentionCount)
            {
                errors.Add($"Scheduled-scene history retention must be between {MinSceneScheduleHistoryRetentionCount} and {MaxSceneScheduleHistoryRetentionCount} entries");
            }

            if (SyncEnabled)
            {
                // Default bridge fields are only required if no per-user mappings exist
                bool hasUserMappings = UserMappings?.Exists(m => m.SyncEnabled && !string.IsNullOrWhiteSpace(m.HueBridgeIp)) == true;
                if (!hasUserMappings)
                {
                    if (string.IsNullOrWhiteSpace(HueBridgeIp))
                        errors.Add("Hue Bridge IP is required when sync is enabled");
                    else if (!Jellyfin.Plugin.Hue.HueBridgeCertificateValidation.IsValidBridgeAddress(HueBridgeIp))
                        errors.Add("Hue Bridge address must be a valid private IP address or .local host name");

                    if (string.IsNullOrWhiteSpace(HueAppKey))
                        errors.Add("Hue App Key is required. Use the 'Link Bridge' button to generate credentials");

                    if (string.IsNullOrWhiteSpace(HueClientKey))
                        errors.Add("Hue Client Key is required for streaming. Use the 'Link Bridge' button to generate credentials");

                    if (string.IsNullOrWhiteSpace(EntertainmentAreaId))
                        errors.Add("Entertainment Area ID is required. Select an area from the dropdown or enter manually");
                }

                if (TargetFps < MinTargetFps || TargetFps > MaxTargetFps)
                    errors.Add("Target FPS must be between 1 and 60");

                if (AudioSensitivityPercent < MinAudioSensitivityPercent ||
                    AudioSensitivityPercent > MaxAudioSensitivityPercent)
                    errors.Add($"Audio sensitivity must be between {MinAudioSensitivityPercent} and {MaxAudioSensitivityPercent} percent");

                if (AudioNoiseGatePercent < MinAudioNoiseGatePercent || AudioNoiseGatePercent > MaxAudioNoiseGatePercent)
                    errors.Add($"Audio noise gate must be between {MinAudioNoiseGatePercent} and {MaxAudioNoiseGatePercent} percent");

                if (AudioLowFrequencyHz < MinAudioFrequencyHz || AudioLowFrequencyHz > MaxAudioFrequencyHz ||
                    AudioMidFrequencyHz < MinAudioFrequencyHz || AudioMidFrequencyHz > MaxAudioFrequencyHz ||
                    AudioHighFrequencyHz < MinAudioFrequencyHz || AudioHighFrequencyHz > MaxAudioFrequencyHz)
                {
                    errors.Add($"Audio frequencies must be between {MinAudioFrequencyHz} and {MaxAudioFrequencyHz} Hz");
                }
                else if (AudioLowFrequencyHz >= AudioMidFrequencyHz || AudioMidFrequencyHz >= AudioHighFrequencyHz)
                {
                    errors.Add("Audio frequencies must be strictly ordered low < mid < high");
                }

                if (AudioLowGainPercent < MinAudioBandGainPercent || AudioLowGainPercent > MaxAudioBandGainPercent ||
                    AudioMidGainPercent < MinAudioBandGainPercent || AudioMidGainPercent > MaxAudioBandGainPercent ||
                    AudioHighGainPercent < MinAudioBandGainPercent || AudioHighGainPercent > MaxAudioBandGainPercent)
                {
                    errors.Add($"Audio band gains must be between {MinAudioBandGainPercent} and {MaxAudioBandGainPercent} percent");
                }

                if (AudioResponseSmoothingPercent < MinAudioResponseSmoothingPercent ||
                    AudioResponseSmoothingPercent > MaxAudioResponseSmoothingPercent)
                {
                    errors.Add($"Audio response smoothing must be between {MinAudioResponseSmoothingPercent} and {MaxAudioResponseSmoothingPercent} percent");
                }

                if (AudioBandSpreadPercent < MinAudioBandSpreadPercent || AudioBandSpreadPercent > MaxAudioBandSpreadPercent)
                    errors.Add($"Audio band spread must be between {MinAudioBandSpreadPercent} and {MaxAudioBandSpreadPercent} percent");

                if (AudioBeatPulsePercent < MinAudioBeatPulsePercent || AudioBeatPulsePercent > MaxAudioBeatPulsePercent)
                    errors.Add($"Audio beat pulse must be between {MinAudioBeatPulsePercent} and {MaxAudioBeatPulsePercent} percent");

                if (AudioBeatPulseDecayPercent < MinAudioBeatPulseDecayPercent || AudioBeatPulseDecayPercent > MaxAudioBeatPulseDecayPercent)
                    errors.Add($"Audio beat pulse decay must be between {MinAudioBeatPulseDecayPercent} and {MaxAudioBeatPulseDecayPercent} percent");

                if (AudioBeatPulseThresholdPercent < MinAudioBeatPulseThresholdPercent ||
                    AudioBeatPulseThresholdPercent > MaxAudioBeatPulseThresholdPercent)
                    errors.Add($"Audio beat pulse threshold must be between {MinAudioBeatPulseThresholdPercent} and {MaxAudioBeatPulseThresholdPercent} percent");

                if (!TryNormalizeAudioColorPalette(AudioColorPalette, out _))
                    errors.Add("Audio color palette must be Spectrum, Band, Warm, Cool, or Monochrome");

                if (!TryNormalizeAudioSpatialMode(AudioSpatialMode, out _))
                    errors.Add("Audio spatial mode must be Spatial, Uniform, or Mirror");

                if (!TryNormalizeAudioChannelMode(AudioChannelMode, out _))
                    errors.Add("Audio channel mode must be Mono, Stereo, Left, or Right");

                if (!string.Equals(FrameResolution, FrameResolutionLow, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(FrameResolution, FrameResolutionStandard, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(FrameResolution, FrameResolutionHigh, StringComparison.OrdinalIgnoreCase))
                    errors.Add("Frame resolution must be 80x45, 160x90, or 320x180");

                if (!string.Equals(VideoScalingMode, VideoScalingModeStretch, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(VideoScalingMode, VideoScalingModeFit, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(VideoScalingMode, VideoScalingModeCrop, StringComparison.OrdinalIgnoreCase))
                    errors.Add("Video scaling mode must be Stretch, Fit, or Crop");

                if (!string.Equals(VideoDeinterlaceMode, VideoDeinterlaceModeOff, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(VideoDeinterlaceMode, VideoDeinterlaceModeAuto, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(VideoDeinterlaceMode, VideoDeinterlaceModeOn, StringComparison.OrdinalIgnoreCase))
                    errors.Add("Video deinterlace mode must be Off, Auto, or On");

                if (!string.Equals(PauseBehavior, PauseBehaviorKeepLastColors, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(PauseBehavior, PauseBehaviorRestoreLightState, StringComparison.OrdinalIgnoreCase))
                    errors.Add("Pause behavior must be KeepLastColors or RestoreLightState");

                if (SamplingBreadthPercent < MinSamplingBreadthPercent ||
                    SamplingBreadthPercent > MaxSamplingBreadthPercent)
                    errors.Add("Sampling breadth must be between 1 and 50 percent");

                if (!string.Equals(SamplingMode, SamplingModeAverage, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(SamplingMode, SamplingModeCenterWeighted, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(SamplingMode, SamplingModeCenterPixel, StringComparison.OrdinalIgnoreCase))
                    errors.Add("Sampling mode must be Average, CenterWeighted, or CenterPixel");

                if (ColorSmoothingPercent < MinColorSmoothingPercent ||
                    ColorSmoothingPercent > MaxColorSmoothingPercent)
                    errors.Add("Color smoothing must be between 0 and 90 percent");

                if (BrightnessDimLevel < MinBrightnessDimLevel || BrightnessDimLevel > MaxBrightnessDimLevel)
                    errors.Add("Brightness dim level must be between 0 and 100");

                if (BrightnessBoost < MinBrightnessBoost || BrightnessBoost > MaxBrightnessBoost)
                    errors.Add("Brightness boost must be between 50 and 200");

                if (RedGain < MinColorChannelGain || RedGain > MaxColorChannelGain)
                    errors.Add("Red gain must be between 50 and 200");

                if (GreenGain < MinColorChannelGain || GreenGain > MaxColorChannelGain)
                    errors.Add("Green gain must be between 50 and 200");

                if (BlueGain < MinColorChannelGain || BlueGain > MaxColorChannelGain)
                    errors.Add("Blue gain must be between 50 and 200");

                if (ColorSaturation < MinColorSaturation || ColorSaturation > MaxColorSaturation)
                    errors.Add("Color saturation must be between 0 and 200");

                if (HueShiftDegrees < MinHueShiftDegrees || HueShiftDegrees > MaxHueShiftDegrees)
                    errors.Add("Hue shift must be between -180 and 180 degrees");

                if (OutputBrightnessPercent < MinOutputBrightnessPercent ||
                    OutputBrightnessPercent > MaxOutputBrightnessPercent)
                    errors.Add("Output brightness must be between 0 and 100 percent");

                if (BlackoutThreshold < MinByteSetting || BlackoutThreshold > MaxByteSetting)
                    errors.Add("Blackout threshold must be between 0 and 255");

                if (ColorChangeThreshold < MinByteSetting || ColorChangeThreshold > MaxByteSetting)
                    errors.Add("Color change threshold must be between 0 and 255");

                if (NetworkRetryAttempts < MinNetworkRetryAttempts || NetworkRetryAttempts > MaxNetworkRetryAttempts)
                    errors.Add("Network retry attempts must be between 0 and 10");

                if (FfmpegStallTimeoutSeconds < MinFfmpegStallTimeoutSeconds ||
                    FfmpegStallTimeoutSeconds > MaxFfmpegStallTimeoutSeconds)
                    errors.Add("FFmpeg stall timeout must be between 1 and 60 seconds");

                if (!string.IsNullOrWhiteSpace(CustomFfmpegFlags))
                {
                    try
                    {
                        _ = FfmpegStreamer.ParseCustomArguments(CustomFfmpegFlags);
                    }
                    catch (FormatException ex)
                    {
                        errors.Add($"Custom FFmpeg flags are invalid: {ex.Message}");
                    }
                }

                errors.AddRange(ValidateGlobalChannelIds(ChannelIds));
                ValidateUserMappings(errors);
            }

            return errors;
        }

        /// <summary>
        /// Checks if the configuration is valid
        /// </summary>
        public bool IsValid() => Validate().Count == 0;

        private void ValidateUserMappings(List<string> errors)
        {
            if (UserMappings == null)
                return;

            var seenUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < UserMappings.Count; index++)
            {
                var mapping = UserMappings[index];
                var label = string.IsNullOrWhiteSpace(mapping.UserName)
                    ? $"User mapping {index + 1}"
                    : $"User mapping for '{mapping.UserName.Trim()}'";

                errors.AddRange(ValidatePlaybackOverrides(mapping, label));
                errors.AddRange(ValidateColorOverrides(mapping, label));
                errors.AddRange(ValidatePerformanceOverrides(mapping, label));
                errors.AddRange(ValidateExecutionOverrides(mapping, label));
                errors.AddRange(ValidateChannelOverrides(mapping, label));

                var effectiveAudioFrequencies = NormalizeAudioFrequencyProfile(
                    mapping.AudioLowFrequencyHzOverride ?? AudioLowFrequencyHz,
                    mapping.AudioMidFrequencyHzOverride ?? AudioMidFrequencyHz,
                    mapping.AudioHighFrequencyHzOverride ?? AudioHighFrequencyHz);
                var requestedAudioFrequencies = (
                    LowFrequencyHz: mapping.AudioLowFrequencyHzOverride ?? AudioLowFrequencyHz,
                    MidFrequencyHz: mapping.AudioMidFrequencyHzOverride ?? AudioMidFrequencyHz,
                    HighFrequencyHz: mapping.AudioHighFrequencyHzOverride ?? AudioHighFrequencyHz);
                if (requestedAudioFrequencies != effectiveAudioFrequencies)
                {
                    errors.Add($"{label} effective audio frequencies must be strictly ordered low < mid < high and within {MinAudioFrequencyHz}-{MaxAudioFrequencyHz} Hz");
                }

                if (string.IsNullOrWhiteSpace(mapping.UserId))
                {
                    errors.Add($"{label} requires a user ID");
                }
                else if (!seenUserIds.Add(mapping.UserId.Trim()))
                {
                    errors.Add($"{label} duplicates another user mapping");
                }

                if (!mapping.SyncEnabled)
                    continue;

                // An empty bridge IP represents an intentionally incomplete mapping and
                // falls back to the default bridge. If an address is supplied, the
                // remaining credentials must be complete and the address must be valid.
                if (string.IsNullOrWhiteSpace(mapping.HueBridgeIp))
                    continue;

                if (!Jellyfin.Plugin.Hue.HueBridgeCertificateValidation.IsValidBridgeAddress(mapping.HueBridgeIp))
                    errors.Add($"{label} bridge address must be a valid private IP address or .local host name");

                if (string.IsNullOrWhiteSpace(mapping.HueAppKey))
                    errors.Add($"{label} requires a Hue App Key");

                if (string.IsNullOrWhiteSpace(mapping.HueClientKey))
                    errors.Add($"{label} requires a Hue Client Key");

                if (string.IsNullOrWhiteSpace(mapping.EntertainmentAreaId))
                    errors.Add($"{label} requires an Entertainment Area ID");
            }
        }
    }
}
