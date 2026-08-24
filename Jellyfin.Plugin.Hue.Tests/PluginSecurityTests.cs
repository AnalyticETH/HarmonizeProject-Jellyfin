using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Jellyfin.Plugin.Hue.Configuration;
using MediaBrowser.Common.Plugins;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

public sealed class PluginSecurityTests
{
    [Fact]
    public void GenericPluginConfigurationJsonNeverContainsBridgeCredentials()
    {
        var configuration = new PluginConfiguration
        {
            HueAppKey = "global-app-secret",
            HueClientKey = "global-client-secret",
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = "user-1",
                    HueAppKey = "mapping-app-secret",
                    HueClientKey = "mapping-client-secret",
                    DeviceTargets = new List<UserDeviceBridgeTarget>
                    {
                        new()
                        {
                            DeviceId = "living-room-tv",
                            HueAppKey = "device-app-secret",
                            HueClientKey = "device-client-secret"
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(configuration);

        Assert.DoesNotContain("global-app-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("global-client-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("mapping-app-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("mapping-client-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("device-app-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("device-client-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("hueAppKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hueClientKey", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenericPluginConfigurationUpdateIsRejected()
    {
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));

        var configPlugin = (IHasPluginConfiguration)plugin;
        var exception = Assert.Throws<InvalidOperationException>(() =>
            configPlugin.UpdateConfiguration(new PluginConfiguration()));

        Assert.Contains("HueSync", exception.Message, StringComparison.Ordinal);
    }
}
