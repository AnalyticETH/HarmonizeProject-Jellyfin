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
        public int BlackoutThreshold { get; set; } = 15; // Average brightness below which sync is skipped (0-255)
        public int ColorChangeThreshold { get; set; } = 10; // Minimum color change to trigger update (0-255)
        public int NetworkRetryAttempts { get; set; } = 3; // Number of retry attempts for network operations

        /// <summary>
        /// Gets the bridge configuration for a specific user, or falls back to default
        /// </summary>
        public (string BridgeIp, string AppKey, string ClientKey, string AreaId) GetBridgeConfigForUser(Guid userId)
        {
            var mapping = UserMappings.Find(m => m.UserId == userId.ToString());
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
                if (string.IsNullOrWhiteSpace(HueBridgeIp))
                    errors.Add("Hue Bridge IP is required when sync is enabled");
                else if (!System.Net.IPAddress.TryParse(HueBridgeIp, out _))
                    errors.Add("Hue Bridge IP must be a valid IP address");

                if (string.IsNullOrWhiteSpace(HueAppKey))
                    errors.Add("Hue App Key is required. Use the 'Link Bridge' button to generate credentials");

                if (string.IsNullOrWhiteSpace(HueClientKey))
                    errors.Add("Hue Client Key is required for streaming. Use the 'Link Bridge' button to generate credentials");

                if (string.IsNullOrWhiteSpace(EntertainmentAreaId))
                    errors.Add("Entertainment Area ID is required. Select an area from the dropdown or enter manually");

                if (TargetFps < 1 || TargetFps > 60)
                    errors.Add("Target FPS must be between 1 and 60");

                if (BrightnessDimLevel < 0 || BrightnessDimLevel > 100)
                    errors.Add("Brightness dim level must be between 0 and 100");

                if (BrightnessBoost < 50 || BrightnessBoost > 200)
                    errors.Add("Brightness boost must be between 50 and 200");

                if (ColorSaturation < 0 || ColorSaturation > 200)
                    errors.Add("Color saturation must be between 0 and 200");

                if (BlackoutThreshold < 0 || BlackoutThreshold > 255)
                    errors.Add("Blackout threshold must be between 0 and 255");

                if (ColorChangeThreshold < 0 || ColorChangeThreshold > 255)
                    errors.Add("Color change threshold must be between 0 and 255");

                if (NetworkRetryAttempts < 0 || NetworkRetryAttempts > 10)
                    errors.Add("Network retry attempts must be between 0 and 10");
            }

            return errors;
        }

        /// <summary>
        /// Checks if the configuration is valid
        /// </summary>
        public bool IsValid() => Validate().Count == 0;
    }
}
