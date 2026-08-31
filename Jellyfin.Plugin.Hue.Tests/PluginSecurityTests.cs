using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Serialization;
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
    public void PluginConfigurationXmlSerializerRoundTripsCertificatePins()
    {
        var configuration = new PluginConfiguration
        {
            HueBridgeCertificatePins = new Dictionary<string, string>
            {
                ["192.168.1.100"] = new string('a', 64),
                ["hue-bridge.local"] = new string('b', 64)
            }
        };
        var serializer = new XmlSerializer(typeof(PluginConfiguration));

        string xml;
        using (var writer = new StringWriter())
        {
            serializer.Serialize(writer, configuration);
            xml = writer.ToString();
        }
        Assert.Contains("<HueBridgeCertificatePins>", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("HueBridgeCertificatePinEntries", xml, StringComparison.Ordinal);

        using var reader = new StringReader(xml);
        var roundTripped = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));

        Assert.Equal(configuration.HueBridgeCertificatePins, roundTripped.HueBridgeCertificatePins);
        Assert.Equal(StringComparer.OrdinalIgnoreCase, roundTripped.HueBridgeCertificatePins.Comparer);
        Assert.Equal(
            new string('b', 64),
            roundTripped.HueBridgeCertificatePins["HUE-BRIDGE.LOCAL"]);
        Assert.DoesNotContain("HueBridgeCertificatePinEntries", JsonSerializer.Serialize(configuration), StringComparison.Ordinal);
    }

    [Fact]
    public void PluginConfigurationXmlAdapterRejectsBlankHostsAndConflictingPins()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        const string xml = """
            <PluginConfiguration>
              <HueBridgeCertificatePins>
                <Pin><Host> </Host><Fingerprint>aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa</Fingerprint></Pin>
                <Pin><Host>hue-bridge.local</Host><Fingerprint>aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa</Fingerprint></Pin>
                <Pin><Host> HUE-BRIDGE.LOCAL </Host><Fingerprint>bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb</Fingerprint></Pin>
              </HueBridgeCertificatePins>
            </PluginConfiguration>
            """;

        using var reader = new StringReader(xml);
        var configuration = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));

        Assert.Single(configuration.HueBridgeCertificatePins);
        Assert.DoesNotContain(string.Empty, configuration.HueBridgeCertificatePins.Keys);
        Assert.Equal(string.Empty, configuration.HueBridgeCertificatePins["hue-bridge.local"]);
        Assert.Null(HueBridgeCertificateValidation.GetConfiguredCertificateFingerprint(
            configuration,
            "hue-bridge.local"));
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
