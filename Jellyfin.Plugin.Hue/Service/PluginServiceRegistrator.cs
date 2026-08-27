using System;
using System.Linq;
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
        // Hue bridges use a locally issued/self-signed certificate. Keep the exception
        // scoped to private bridge addresses and require an explicit SHA-256 pin so
        // discovery.meethue.com and any other public HTTPS endpoint still use the
        // platform certificate trust store.
        // Note: AddHttpClient<T>() registers T as transient by default, using the configured handler
        serviceCollection.AddHttpClient<Hue.HueClient>()
            .ConfigureHttpClient(httpClient => httpClient.Timeout = TimeSpan.FromSeconds(10))
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
    internal const string CertificateFingerprintHeader = "x-hue-certificate-sha256";
    internal const string CertificateProbeHeader = "x-hue-certificate-probe";
    internal static readonly HttpRequestOptionsKey<string> CertificateFingerprintOption =
        new("HueBridgeCertificateFingerprint");

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
        if (request?.RequestUri is not { } requestUri)
            return false;

        var isLocalBridge = IsLocalBridgeHost(requestUri.Host);
        if (!isLocalBridge)
        {
            // Public discovery traffic must use the platform trust store and never
            // inherits the local self-signed exception.
            return sslPolicyErrors == SslPolicyErrors.None;
        }

        if (certificate == null)
            return false;

        var actualFingerprint = ComputeCertificateFingerprint(certificate);
        request.Options.Set(CertificateFingerprintOption, actualFingerprint);

        // An explicit certificate probe is credential-free and is used only by the
        // elevated re-pinning flow. It still rejects missing certificates and policy
        // failures other than the expected local name/chain errors.
        const SslPolicyErrors allowedLocalErrors =
            SslPolicyErrors.RemoteCertificateNameMismatch |
            SslPolicyErrors.RemoteCertificateChainErrors;
        if ((sslPolicyErrors & ~allowedLocalErrors) != 0)
        {
            return false;
        }

        if (request.Headers.Contains(CertificateProbeHeader))
            return true;

        var expected = request.Headers.TryGetValues(CertificateFingerprintHeader, out var values)
            ? values.FirstOrDefault()
            : GetConfiguredCertificateFingerprint(requestUri.Host);
        return TryNormalizeCertificateFingerprint(expected, out var normalizedExpected) &&
               string.Equals(actualFingerprint, normalizedExpected, StringComparison.OrdinalIgnoreCase);
    }

    internal static string ComputeCertificateFingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(certificate.RawData))
            .ToLowerInvariant();
    }

    internal static bool TryNormalizeCertificateFingerprint(
        string? value,
        out string normalized)
    {
        return Configuration.PluginConfiguration.TryNormalizeCertificateFingerprint(value, out normalized);
    }

    /// <summary>
    /// Returns one configured pin for a bridge host. Conflicting pins across targets
    /// fail closed by returning null, forcing an explicit administrator re-pin.
    /// </summary>
    internal static string? GetConfiguredCertificateFingerprint(string? bridgeHost)
    {
        return GetConfiguredCertificateFingerprint(Plugin.Instance?.Configuration, bridgeHost);
    }

    /// <summary>
    /// Resolves a configured bridge pin from an explicit configuration snapshot.
    /// Target resolution and report generation can operate on a candidate snapshot
    /// before it is assigned to <see cref="Plugin.Instance"/>, so they must not
    /// consult a potentially different live configuration when deriving identity.
    /// </summary>
    internal static string? GetConfiguredCertificateFingerprint(
        Configuration.PluginConfiguration? config,
        string? bridgeHost)
    {
        if (config?.HueBridgeCertificatePins == null || string.IsNullOrWhiteSpace(bridgeHost))
            return null;

        var matches = new List<string>();
        foreach (var pair in config.HueBridgeCertificatePins)
        {
            if (!IsSameBridgeHost(pair.Key, bridgeHost))
                continue;

            // A malformed alias must not be silently ignored when another alias has a
            // valid pin. Force an explicit administrator re-pin instead of guessing
            // which persisted identity should win.
            if (!TryNormalizeCertificateFingerprint(pair.Value, out var normalized))
                return null;

            matches.Add(normalized);
        }

        var distinctMatches = matches.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return distinctMatches.Length == 1 ? distinctMatches[0] : null;
    }

    internal static bool IsSameBridgeHost(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        var leftHost = left.Trim().Trim('[', ']');
        var rightHost = right.Trim().Trim('[', ']');
        if (IPAddress.TryParse(leftHost, out var leftAddress) &&
            IPAddress.TryParse(rightHost, out var rightAddress))
        {
            return leftAddress.Equals(rightAddress);
        }

        return string.Equals(
            leftHost.TrimEnd('.'),
            rightHost.TrimEnd('.'),
            StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsLocalBridgeHost(string host)
    {
        var normalizedHost = host.Trim();
        if (normalizedHost.Length >= 2 &&
            normalizedHost[0] == '[' &&
            normalizedHost[^1] == ']')
        {
            normalizedHost = normalizedHost[1..^1];
        }

        if (IPAddress.TryParse(normalizedHost, out var address))
        {
            return IsLocalAddress(address);
        }

        // mDNS names are a supported local-bridge configuration and are not public DNS
        // names. Any other hostname must present a normally trusted certificate.
        return normalizedHost.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
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
