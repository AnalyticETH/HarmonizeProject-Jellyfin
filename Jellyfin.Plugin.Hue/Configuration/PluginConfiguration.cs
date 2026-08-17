using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Hue.Configuration
{
    /// <summary>
    /// Per-user bridge, entertainment area, and optional playback/color profile mapping
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

        // Optional per-user color profile overrides. Null values inherit the global setting.
        public int? BrightnessBoostOverride { get; set; }
        public int? ColorSaturationOverride { get; set; }
        public int? HueShiftDegreesOverride { get; set; }
        public int? OutputBrightnessPercentOverride { get; set; }
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

        private const int MinTargetFps = 1;
        private const int MaxTargetFps = 60;
        private const int MinBrightnessDimLevel = 0;
        private const int MaxBrightnessDimLevel = 100;
        private const int MinBrightnessBoost = 50;
        private const int MaxBrightnessBoost = 200;
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

        public bool SyncEnabled { get; set; } = false;

        // Default/fallback bridge settings (used when no user mapping exists)
        public string HueBridgeIp { get; set; } = string.Empty;
        public string HueAppKey { get; set; } = string.Empty;
        public string HueClientKey { get; set; } = string.Empty; // For DTLS
        public string EntertainmentAreaId { get; set; } = string.Empty;

        // Per-user bridge mappings
        public List<UserBridgeMapping> UserMappings { get; set; } = new List<UserBridgeMapping>();

        public bool UseCinemaMode { get; set; } = true; // Dimming behavior
        public int BrightnessDimLevel { get; set; } = 30;
        public string PauseBehavior { get; set; } = PauseBehaviorKeepLastColors;
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
        /// Gets optional per-user cinema-mode overrides. Null values mean the global plugin
        /// setting should be used for that component.
        /// </summary>
        public (bool? UseCinemaMode, int? BrightnessDimLevel) GetPlaybackOverridesForUser(Guid userId)
        {
            var userIdText = userId.ToString();
            var mapping = UserMappings?.Find(m => string.Equals(m.UserId?.Trim(), userIdText, StringComparison.OrdinalIgnoreCase));
            return mapping == null
                ? (null, null)
                : (mapping.UseCinemaModeOverride, mapping.BrightnessDimLevelOverride);
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

            return errors;
        }

        public PluginConfiguration()
        {
            // Defaults
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
