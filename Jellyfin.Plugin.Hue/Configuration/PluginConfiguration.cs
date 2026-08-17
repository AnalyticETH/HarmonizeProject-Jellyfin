using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Hue.Configuration
{
    /// <summary>
    /// Per-user bridge and entertainment area mapping
    /// </summary>
    public class UserBridgeMapping
    {
        public string UserId { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty; // For display purposes
        public string HueBridgeIp { get; set; } = string.Empty;
        public string HueAppKey { get; set; } = string.Empty;
        public string HueClientKey { get; set; } = string.Empty;
        public string EntertainmentAreaId { get; set; } = string.Empty;
        public string EntertainmentAreaName { get; set; } = string.Empty; // For display purposes
    }

    /// <summary>
    /// Configuration for the Philips Hue Sync plugin
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        private const int MinTargetFps = 1;
        private const int MaxTargetFps = 60;
        private const int MinBrightnessDimLevel = 0;
        private const int MaxBrightnessDimLevel = 100;
        private const int MinBrightnessBoost = 50;
        private const int MaxBrightnessBoost = 200;
        private const int MinColorSaturation = 0;
        private const int MaxColorSaturation = 200;
        private const int MinByteSetting = 0;
        private const int MaxByteSetting = 255;
        private const int MinNetworkRetryAttempts = 0;
        private const int MaxNetworkRetryAttempts = 10;

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
        public int TargetFps { get; set; } = 20;
        public bool UseGpu { get; set; } = true;
        public string CustomFfmpegFlags { get; set; } = string.Empty; // e.g. -hwaccel auto

        // New advanced settings
        public bool RestoreLightState { get; set; } = true; // Save and restore light state before sync
        public int BrightnessBoost { get; set; } = 100; // Brightness multiplier (50-200%)
        public int ColorSaturation { get; set; } = 100; // Color saturation adjustment (0-200%)
        public int BlackoutThreshold { get; set; } = 15; // Average brightness below which lights are set to black (0-255)
        public int ColorChangeThreshold { get; set; } = 10; // Minimum color change to trigger update (0-255)
        public int NetworkRetryAttempts { get; set; } = 3; // Number of retry attempts for network operations

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

        public PluginConfiguration()
        {
            // Defaults
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
                bool hasUserMappings = UserMappings?.Exists(m => !string.IsNullOrWhiteSpace(m.HueBridgeIp)) == true;
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

                if (BrightnessDimLevel < MinBrightnessDimLevel || BrightnessDimLevel > MaxBrightnessDimLevel)
                    errors.Add("Brightness dim level must be between 0 and 100");

                if (BrightnessBoost < MinBrightnessBoost || BrightnessBoost > MaxBrightnessBoost)
                    errors.Add("Brightness boost must be between 50 and 200");

                if (ColorSaturation < MinColorSaturation || ColorSaturation > MaxColorSaturation)
                    errors.Add("Color saturation must be between 0 and 200");

                if (BlackoutThreshold < MinByteSetting || BlackoutThreshold > MaxByteSetting)
                    errors.Add("Blackout threshold must be between 0 and 255");

                if (ColorChangeThreshold < MinByteSetting || ColorChangeThreshold > MaxByteSetting)
                    errors.Add("Color change threshold must be between 0 and 255");

                if (NetworkRetryAttempts < MinNetworkRetryAttempts || NetworkRetryAttempts > MaxNetworkRetryAttempts)
                    errors.Add("Network retry attempts must be between 0 and 10");

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

                if (string.IsNullOrWhiteSpace(mapping.UserId))
                {
                    errors.Add($"{label} requires a user ID");
                }
                else if (!seenUserIds.Add(mapping.UserId.Trim()))
                {
                    errors.Add($"{label} duplicates another user mapping");
                }

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
