using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Jellyfin.Plugin.Hue.Service;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Hue;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Hue bridges use a locally issued/self-signed certificate. Keep that exception
        // scoped to private bridge addresses so discovery.meethue.com and any other public
        // HTTPS endpoint still use the platform certificate trust store.
        // Note: AddHttpClient<T>() registers T as transient by default, using the configured handler
        serviceCollection.AddHttpClient<Hue.HueClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HueBridgeCertificateValidation.ValidateServerCertificate
            });
        serviceCollection.AddHostedService<HueSyncService>();
    }
}

internal static class HueBridgeCertificateValidation
{
    internal static bool ValidateServerCertificate(
        HttpRequestMessage? request,
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        if (sslPolicyErrors == SslPolicyErrors.None)
        {
            return true;
        }

        // Never bypass a missing certificate or other policy failures. A Hue bridge may
        // legitimately have name/chain errors because it uses a local certificate.
        const SslPolicyErrors allowedLocalErrors =
            SslPolicyErrors.RemoteCertificateNameMismatch |
            SslPolicyErrors.RemoteCertificateChainErrors;
        if ((sslPolicyErrors & ~allowedLocalErrors) != 0 || request?.RequestUri is not { } requestUri)
        {
            return false;
        }

        return IsLocalBridgeHost(requestUri.Host);
    }

    internal static bool IsLocalBridgeHost(string host)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            return IsLocalAddress(address);
        }

        // mDNS names are a supported local-bridge configuration and are not public DNS
        // names. Any other hostname must present a normally trusted certificate.
        return host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return IsLocalAddress(address.MapToIPv4());
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 169 && bytes[1] == 254);
        }

        // IPv6 unique-local (fc00::/7) and link-local (fe80::/10) addresses.
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
               ((bytes[0] & 0xfe) == 0xfc ||
                (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80));
    }
}
