
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Hue.Configuration
{
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
        public string CustomFfmpegFlags { get; set; } = string.Empty; // e.g. -hwaccel auto

        public PluginConfiguration()
        {
            // Defaults
        }
    }
}
