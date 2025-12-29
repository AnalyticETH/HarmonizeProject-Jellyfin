using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Hue.Configuration
{
    /// <summary>
    /// Configuration for the Philips Hue Sync plugin
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        public bool SyncEnabled { get; set; } = false;
        public string HueBridgeIp { get; set; } = string.Empty;
        public string HueAppKey { get; set; } = string.Empty;
        public string HueClientKey { get; set; } = string.Empty; // For DTLS
        public string EntertainmentAreaId { get; set; } = string.Empty;
        public bool UseCinemaMode { get; set; } = true; // Dimming behavior
        public int BrightnessDimLevel { get; set; } = 30;
        public int TargetFps { get; set; } = 20;
        public bool UseGpu { get; set; } = true;
        public string CustomFfmpegFlags { get; set; } = string.Empty; // e.g. -hwaccel auto

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
            }

            return errors;
        }

        /// <summary>
        /// Checks if the configuration is valid
        /// </summary>
        public bool IsValid() => Validate().Count == 0;
    }
}
