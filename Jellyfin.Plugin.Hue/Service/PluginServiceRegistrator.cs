using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Hue.Api;
using Jellyfin.Plugin.Hue.Service;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaEncoding;
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
            .ConfigurePrimaryHttpMessageHandler(CreateHueHttpClientHandler);
        serviceCollection.AddSingleton<HueBridgeLifecycleGate>();
        serviceCollection.AddScoped<HueConfigurationMutationFilter>();
        serviceCollection.AddSingleton<HueDiagnosticsCancellationGate>();
        serviceCollection.AddSingleton<IHueBridgeLocalDiscovery, HueBridgeMdnsDiscovery>();
        // The tester owns the process-wide diagnostic lifecycle gate. A singleton keeps
        // concurrent API requests from creating separate gates and touching the bridge at
        // the same time.
        serviceCollection.AddSingleton<IHueStreamTester, HueStreamTester>();
        serviceCollection.AddSingleton<IHueEnvironmentProbe>(serviceProvider =>
            new HueEnvironmentProbe(mediaEncoder: serviceProvider.GetService<IMediaEncoder>()));
        serviceCollection.AddHostedService<HueSyncService>();
        serviceCollection.AddHostedService<HueSceneAutomationService>();
    }

    internal static HttpClientHandler CreateHueHttpClientHandler()
    {
        return new HttpClientHandler
        {
            // Hue bridge requests are scoped to the configured private/local host.
            // Following a redirect could silently move a credential-bearing request
            // to an attacker-controlled or public endpoint, so redirects fail closed.
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = HueBridgeCertificateValidation.ValidateServerCertificate
        };
    }
}

internal static class HueBridgeCertificateValidation
{
    internal static bool IsValidBridgeAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var host = value.Trim();
        return host.IndexOfAny(new[] { '/', '\\', '?', '#' }) < 0 &&
               IsLocalBridgeHost(host);
    }

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

    /// <summary>
    /// Resolves a configured bridge host and returns an address that is permitted by
    /// the same private/link-local policy used for literal bridge addresses.  The
    /// caller must connect to the returned address rather than resolving the hostname
    /// again, otherwise a DNS/mDNS answer could change between validation and connect.
    /// </summary>
    internal static async Task<IPAddress> ResolveLocalBridgeAddressAsync(
        string value,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var host = value.Trim().Trim('[', ']');
        if (IPAddress.TryParse(host, out var address))
        {
            if (IsLocalAddress(address))
            {
                return address;
            }

            throw new SocketException((int)SocketError.AddressNotAvailable);
        }

        if (!IsValidBridgeAddress(host))
        {
            throw new ArgumentException("Bridge address must be a private IP address or .local host name.", nameof(value));
        }

        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        return SelectLocalBridgeAddress(addresses);
    }

    /// <summary>
    /// Selects a resolved bridge address only when every DNS/mDNS answer is within the
    /// private/link-local/unique-local boundary. Rejecting mixed answers avoids silently
    /// accepting a poisoned public answer alongside an otherwise valid local address.
    /// </summary>
    internal static IPAddress SelectLocalBridgeAddress(IPAddress[] addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Length == 0 || Array.Exists(addresses, candidate => !IsLocalAddress(candidate)))
        {
            throw new SocketException((int)SocketError.AddressNotAvailable);
        }

        return Array.Find(addresses, IsLocalAddress)
            ?? throw new SocketException((int)SocketError.AddressNotAvailable);
    }

    internal static bool IsLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6Multicast)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return IsLocalAddress(address.MapToIPv4());
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            if (bytes[0] >= 224 || IsAllZero(bytes) || IsAllOnes(bytes))
                return false;

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

    private static bool IsAllZero(byte[] bytes)
    {
        foreach (var value in bytes)
        {
            if (value != 0)
                return false;
        }

        return true;
    }

    private static bool IsAllOnes(byte[] bytes)
    {
        foreach (var value in bytes)
        {
            if (value != byte.MaxValue)
                return false;
        }

        return true;
    }
}
