using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Hue.Service;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Hue.Hue
{
    /// <summary>
    /// Client for interacting with Philips Hue Bridge API v2
    /// </summary>
    public class HueClient
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<HueClient> _logger;
        private readonly IHueBridgeLocalDiscovery? _localDiscovery;
        private const int DefaultRetryAttempts = 3;
        private const int MaxRetryAttempts = 10;
        private const int RetryDelayMs = 1000;
        // Hue REST and discovery payloads are normally measured in kilobytes. Keep
        // enough headroom for installations with many resources while preventing a
        // compromised bridge or discovery endpoint from forcing an unbounded buffer.
        internal const int MaxResponseBodyBytes = 1024 * 1024;
        /// <summary>
        /// Maximum number of entertainment areas accepted from one bridge response.
        /// Keep area discovery bounded before materializing every returned resource.
        /// </summary>
        internal const int MaxEntertainmentAreas = 256;
        private const int MaxLightStateTokenLength = 128;
        /// <summary>
        /// Maximum number of unique light resources captured from one entertainment
        /// area. Keep capture fan-out bounded before any bridge request is issued so a
        /// bounded response cannot turn into an unbounded sequence of REST calls.
        /// </summary>
        internal const int MaxLightStateRequests = 256;
        private const int MaxGradientPoints = 5;
        private const long MaxTimedEffectDurationMilliseconds = 21_600_000;

        /// <summary>
        /// Number of retry attempts for network operations. Defaults to 3; set from plugin configuration.
        /// </summary>
        public int RetryAttempts
        {
            get => _retryAttempts;
            set => _retryAttempts = Math.Clamp(value, 0, MaxRetryAttempts);
        }

        private int _retryAttempts = DefaultRetryAttempts;

        /// <summary>
        /// Creates a lightweight playback client that reuses the safe HttpClient transport
        /// but keeps retry policy state isolated from another concurrent playback worker.
        /// </summary>
        internal HueClient CreatePlaybackClient()
        {
            return new HueClient(_httpClient, _logger, _localDiscovery)
            {
                RetryAttempts = RetryAttempts
            };
        }

        public HueClient(
            HttpClient httpClient,
            ILogger<HueClient> logger,
            IHueBridgeLocalDiscovery? localDiscovery = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _localDiscovery = localDiscovery;
        }

        /// <summary>
        /// Executes an HTTP operation with retry logic and exponential backoff
        /// </summary>
        private async Task<T?> ExecuteWithRetry<T>(
            Func<Task<T>> operation,
            int? maxRetries = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(operation);

            // Configuration normally validates this range, but persisted settings can
            // outlive older versions and callers can invoke the client directly. Keep
            // malformed values from turning one bridge failure into an unbounded retry
            // loop (or overflowing the exponential backoff calculation).
            var retries = Math.Clamp(maxRetries ?? RetryAttempts, 0, MaxRetryAttempts);
            for (int attempt = 0; attempt <= retries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // WaitAsync enforces the caller's deadline even when a custom
                    // HttpMessageHandler does not observe cancellation itself.
                    return await operation().WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (attempt < retries && IsRetriableException(ex))
                {
                    var delay = RetryDelayMs * (int)Math.Pow(2, attempt);
                    _logger.LogWarning(ex, "Network operation failed (attempt {0}/{1}), retrying in {2}ms", attempt + 1, retries + 1, delay);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Network operation failed after {0} attempts", attempt + 1);
                    throw;
                }
            }
            return default;
        }

        /// <summary>
        /// Determines if an exception is retriable
        /// </summary>
        private bool IsRetriableException(Exception ex)
        {
            if (ex is TaskCanceledException or TimeoutException)
                return true;

            if (ex is HttpRequestException requestException)
            {
                // A missing status code means the request never reached the bridge
                // (DNS, connection reset, TLS, etc.). HTTP client/server failures that
                // are commonly transient should be retried; authentication and input
                // errors should fail immediately.
                if (!requestException.StatusCode.HasValue)
                    return true;

                var statusCode = (int)requestException.StatusCode.Value;
                return statusCode == (int)HttpStatusCode.RequestTimeout ||
                       statusCode == 429 ||
                       statusCode >= 500;
            }

            return false;
        }

        private static string FormatBridgeHost(string bridgeIp)
        {
            var host = bridgeIp.Trim();
            if (!Jellyfin.Plugin.Hue.HueBridgeCertificateValidation.IsValidBridgeAddress(host))
            {
                throw new ArgumentException("Bridge address must be a private IP address or .local host name.", nameof(bridgeIp));
            }

            var parseableHost = host.Length >= 2 &&
                                host[0] == '[' &&
                                host[^1] == ']'
                ? host[1..^1]
                : host;
            if (IPAddress.TryParse(parseableHost, out var address) &&
                address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                // RFC 6874 requires the percent separator in an IPv6 zone identifier
                // to be percent-encoded inside a URI. Without this, Uri treats the
                // raw "%42" scope as an escape and silently drops the interface ID,
                // turning a usable link-local bridge address into an unroutable one.
                var uriHost = parseableHost.Replace("%", "%25", StringComparison.Ordinal);
                return $"[{uriHost}]";
            }

            return host;
        }

        internal static string GetBridgeIdentityHost(Uri requestUri)
        {
            ArgumentNullException.ThrowIfNull(requestUri);
            var host = requestUri.DnsSafeHost;
            // Uri preserves RFC 6874 zone identifiers in DnsSafeHost as "%25". The
            // certificate-pin registry stores the administrator's bridge spelling,
            // where the zone separator is a single percent, so normalize only that
            // delimiter before comparing persisted identities.
            var zoneMarker = host.IndexOf("%25", StringComparison.OrdinalIgnoreCase);
            if (zoneMarker < 0)
                return host;

            var normalized = host[..zoneMarker] + "%" + host[(zoneMarker + 3)..];
            return IPAddress.TryParse(normalized, out _)
                ? normalized
                : host;
        }

        private static string BuildBridgeUrl(string scheme, string bridgeIp, string path)
        {
            var host = FormatBridgeHost(bridgeIp);
            return $"{scheme}://{host}/{path.TrimStart('/')}";
        }

        /// <summary>
        /// Sends one bridge request after resolving a .local host to a vetted private
        /// address. The request URI is rewritten to that exact address so the HTTP stack
        /// cannot perform a second, potentially different DNS/mDNS lookup after the
        /// private-address check. Cloud discovery intentionally bypasses this helper and
        /// continues to use the normal public HTTPS trust policy.
        /// </summary>
        private async Task<HttpResponseMessage> SendBridgeRequestAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.RequestUri is not { } requestUri)
            {
                throw new InvalidOperationException("Hue bridge requests require an absolute URI.");
            }

            IPAddress? resolvedAddress = null;
            if (requestUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                requestUri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            {
                resolvedAddress = await HueBridgeCertificateValidation
                    .ResolveLocalBridgeAddressAsync(requestUri.Host, cancellationToken)
                    .ConfigureAwait(false);
                var builder = new UriBuilder(requestUri)
                {
                    Host = FormatBridgeHost(resolvedAddress.ToString())
                };
                request.RequestUri = builder.Uri;
            }

            // Carry the configured pin into the TLS callback even when a .local name
            // was rewritten to its vetted private IP between validation and connect.
            // The fingerprint is public metadata; bridge credentials remain in the
            // existing Hue application-key header and are never logged.
            var configuredFingerprint = HueBridgeCertificateValidation
                .GetConfiguredCertificateFingerprint(GetBridgeIdentityHost(requestUri), resolvedAddress);
            if (!string.IsNullOrWhiteSpace(configuredFingerprint))
            {
                request.Headers.TryAddWithoutValidation(
                    HueBridgeCertificateValidation.CertificateFingerprintHeader,
                    configuredFingerprint);
            }
            else if (resolvedAddress != null && !request.Headers.Contains(
                         HueBridgeCertificateValidation.CertificateProbeHeader))
            {
                // Credential-bearing requests must not let the certificate callback
                // recover an exact IP pin after alias resolution has found no single
                // trusted identity (including conflicting alias/IP pins).
                request.Options.Set(
                    HueBridgeCertificateValidation.CertificatePinResolutionFailedOption,
                    true);
            }

            return await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Reads a Hue response body with a finite decoded-byte limit. The limit is
        /// enforced both from the advertised length and while streaming so chunked or
        /// dishonest responses cannot grow an in-memory buffer without bound.
        /// </summary>
        private static async Task<string> ReadResponseBodyAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(response);

            var content = response.Content;
            if (content.Headers.ContentLength > MaxResponseBodyBytes)
            {
                throw new InvalidDataException("Hue response body exceeds the maximum allowed size.");
            }

            await using var responseStream = await content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var body = new MemoryStream();
            var buffer = new byte[81920];

            while (true)
            {
                var remaining = MaxResponseBodyBytes - checked((int)body.Length);
                var bytesToRead = Math.Min(buffer.Length, remaining + 1);
                var bytesRead = await responseStream
                    .ReadAsync(buffer.AsMemory(0, bytesToRead), cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                if (bytesRead > remaining)
                {
                    throw new InvalidDataException("Hue response body exceeds the maximum allowed size.");
                }

                body.Write(buffer, 0, bytesRead);
            }

            return Encoding.UTF8.GetString(body.GetBuffer(), 0, checked((int)body.Length));
        }

        /// <summary>
        /// Performs a credential-free TLS handshake and returns the server certificate
        /// fingerprint. The caller must present the result to an administrator and use
        /// the explicit trust endpoint before bridge credentials can be sent.
        /// </summary>
        public async Task<string?> GetBridgeCertificateFingerprint(
            string bridgeIp,
            CancellationToken cancellationToken = default)
        {
            if (!HueBridgeCertificateValidation.IsValidBridgeAddress(bridgeIp))
                return null;

            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    BuildBridgeUrl("https", bridgeIp, "/api/config"));
                request.Headers.TryAddWithoutValidation(
                    HueBridgeCertificateValidation.CertificateProbeHeader,
                    "1");
                using var response = await SendBridgeRequestAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(
                    await ReadResponseBodyAsync(response, cancellationToken).ConfigureAwait(false));
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("bridgeid", out var bridgeId) ||
                    bridgeId.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(bridgeId.GetString()))
                {
                    _logger.LogWarning("Hue bridge certificate probe returned an unexpected configuration response");
                    return null;
                }

                return request.Options.TryGetValue(
                    HueBridgeCertificateValidation.CertificateFingerprintOption,
                    out var fingerprint)
                    ? fingerprint
                    : null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to retrieve the Hue bridge certificate fingerprint");
                return null;
            }
        }

        /// <summary>
        /// Discovers every private Hue Bridge visible to the server. Cloud discovery is
        /// combined with local mDNS results so multi-room installations can choose a
        /// bridge for each per-user mapping instead of losing every result after the first.
        /// </summary>
        /// <param name="cancellationToken">Cancels the discovery request.</param>
        /// <returns>Distinct private bridge addresses in discovery order.</returns>
        public async Task<IReadOnlyList<string>> DiscoverBridgeIps(CancellationToken cancellationToken = default)
        {
            var addresses = new List<string>();
            var seenAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddAddress(string? addressText)
            {
                if (!IPAddress.TryParse(addressText, out var address) ||
                    !Jellyfin.Plugin.Hue.HueBridgeCertificateValidation.IsValidBridgeAddress(address.ToString()))
                {
                    return;
                }

                var normalized = address.ToString();
                if (seenAddresses.Add(normalized))
                    addresses.Add(normalized);
            }

            // Prefer the official cloud discovery endpoint for the fastest result, then
            // supplement it with local mDNS. Cloud discovery can return only the bridges
            // registered to the account, while local discovery can find bridges that are
            // offline from the cloud or have not been published there yet.
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://discovery.meethue.com/");
                using var response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(
                    await ReadResponseBodyAsync(response, cancellationToken).ConfigureAwait(false));
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var bridge in doc.RootElement.EnumerateArray())
                    {
                        if (!bridge.TryGetProperty("internalipaddress", out var addressProperty) ||
                            addressProperty.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        AddAddress(addressProperty.GetString());
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cloud Hue bridge discovery failed; trying local mDNS");
            }

            if (_localDiscovery != null)
            {
                try
                {
                    var localAddresses = await _localDiscovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
                    foreach (var localAddress in localAddresses)
                        AddAddress(localAddress);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Local mDNS Hue bridge discovery failed");
                }
            }

            return addresses;
        }

        /// <summary>
        /// Discovers a Hue Bridge and returns the first result for compatibility with
        /// existing callers. Use <see cref="DiscoverBridgeIps"/> when all bridges are
        /// needed for multi-room configuration.
        /// </summary>
        public async Task<string> DiscoverBridgeIp(CancellationToken cancellationToken = default)
        {
            var addresses = await DiscoverBridgeIps(cancellationToken).ConfigureAwait(false);
            return addresses.FirstOrDefault() ?? string.Empty;
        }

        /// <summary>
        /// Registers this application with the Hue Bridge to obtain credentials
        /// </summary>
        /// <param name="ip">The IP address of the Hue Bridge</param>
        /// <param name="cancellationToken">Cancels the registration request.</param>
        /// <returns>Registration result with username and client key, or null if failed</returns>
        /// <remarks>The physical link button must be pressed on the bridge before calling this method</remarks>
        public async Task<Api.HueRegistrationResult?> RegisterWithBridge(
            string ip,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await ExecuteWithRetry(async () =>
                {
                    using var content = new StringContent("{\"devicetype\":\"jellyfin_hue#server\", \"generateclientkey\":true}", System.Text.Encoding.UTF8, "application/json");
                    // Hue bridge firmware now requires the local API to be accessed over TLS.
                    // The bridge certificate is handled by PluginServiceRegistrator for local
                    // bridge addresses only; public discovery traffic keeps normal validation.
                    using var request = new HttpRequestMessage(HttpMethod.Post, BuildBridgeUrl("https", ip, "/api"))
                    {
                        Content = content
                    };
                    using var response = await SendBridgeRequestAsync(request, cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        response.EnsureSuccessStatusCode();
                    }

                    var json = await ReadResponseBodyAsync(response, cancellationToken).ConfigureAwait(false);

                    // Response: [{"success":{"username":"...","clientkey":"..."}}] OR [{"error":...}]
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                    {
                        var first = doc.RootElement[0];
                        if (first.TryGetProperty("success", out var success) &&
                            success.TryGetProperty("username", out var username) &&
                            success.TryGetProperty("clientkey", out var clientKey))
                        {
                            var usernameValue = username.GetString() ?? string.Empty;
                            var clientKeyValue = clientKey.GetString() ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(usernameValue) && !string.IsNullOrWhiteSpace(clientKeyValue))
                            {
                                return new Api.HueRegistrationResult
                                {
                                    Username = usernameValue,
                                    ClientKey = clientKeyValue
                                };
                            }
                        }
                    }

                    // Never log the bridge response: registration failures may include
                    // credential-shaped fields (or other private bridge metadata).
                    _logger.LogWarning("Registration failed: Hue bridge returned an unsuccessful response.");
                    return null;
                }, maxRetries: 0, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception registering with Hue bridge");
                return null;
            }
        }

        /// <summary>
        /// Retrieves the configuration for a specific entertainment area
        /// </summary>
        /// <param name="bridgeIp">The IP address of the Hue Bridge</param>
        /// <param name="appKey">The application key for authentication</param>
        /// <param name="areaId">The ID of the entertainment area</param>
        /// <param name="cancellationToken">Cancels the configuration request.</param>
        /// <returns>JSON element containing the area configuration, or null if failed</returns>
        public async Task<JsonElement?> GetEntertainmentConfiguration(
            string bridgeIp,
            string appKey,
            string areaId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await ExecuteWithRetry(async () =>
                {
                    var url = BuildBridgeUrl("https", bridgeIp, $"/clip/v2/resource/entertainment_configuration/{Uri.EscapeDataString(areaId)}");
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("hue-application-key", appKey);

                    using var response = await SendBridgeRequestAsync(request, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    var json = await ReadResponseBodyAsync(response, cancellationToken).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    // Expected: { "data": [ { "id": "...", "channels": [ ... ] } ] }.
                    // Older bridge responses may omit the resource id, so preserve the
                    // existing first-entry fallback only when no returned entry identifies
                    // itself. If ids are present, select the requested resource explicitly;
                    // using a different area's channel layout could route playback to the
                    // wrong target while still appearing to start successfully.
                    if (!doc.RootElement.TryGetProperty("data", out var data) ||
                        data.ValueKind != JsonValueKind.Array ||
                        data.GetArrayLength() == 0)
                    {
                        _logger.LogWarning("Entertainment configuration response did not contain an area for {0}", areaId);
                        return (JsonElement?)null;
                    }

                    var hasResourceIds = false;
                    JsonElement? matchingConfiguration = null;
                    foreach (var candidate in data.EnumerateArray())
                    {
                        // Preserve the legacy first-entry fallback only when every
                        // returned resource truly omits its identity. A present but
                        // malformed id is not equivalent to an older response without
                        // ids: accepting it could apply another area's channel layout
                        // to the requested target.
                        if (!candidate.TryGetProperty("id", out var idProperty))
                        {
                            continue;
                        }

                        hasResourceIds = true;
                        if (idProperty.ValueKind != JsonValueKind.String ||
                            string.IsNullOrWhiteSpace(idProperty.GetString()))
                        {
                            _logger.LogWarning("Entertainment configuration response contained a malformed area identifier");
                            return (JsonElement?)null;
                        }

                        if (string.Equals(
                            idProperty.GetString()?.Trim(),
                            areaId.Trim(),
                            StringComparison.OrdinalIgnoreCase))
                        {
                            if (matchingConfiguration.HasValue)
                            {
                                // Multiple entries for the requested resource are ambiguous;
                                // do not select a channel layout based on response ordering.
                                _logger.LogWarning("Entertainment configuration response contained duplicate area identifiers");
                                return (JsonElement?)null;
                            }

                            matchingConfiguration = candidate;
                        }
                    }

                    if (matchingConfiguration.HasValue)
                    {
                        // Clone the element so the JsonDocument can be safely disposed.
                        return (JsonElement?)matchingConfiguration.Value.Clone();
                    }

                    if (hasResourceIds)
                    {
                        _logger.LogWarning("Entertainment configuration response did not contain requested area {0}", areaId);
                        return (JsonElement?)null;
                    }

                    // Clone the legacy response so the JsonDocument can be safely disposed.
                    return (JsonElement?)data[0].Clone();
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to load entertainment configuration for area {0}", areaId);
                return null;
            }
        }

        /// <summary>
        /// Activates a Hue Entertainment Area for streaming.
        /// This MUST be called before opening the DTLS tunnel — the bridge will silently
        /// reject all packets from a DTLS session if the area is not in "active" streaming mode.
        /// The bridge automatically deactivates the area after ~10 seconds of inactivity or
        /// when StopEntertainmentArea is called.
        /// </summary>
        /// <param name="bridgeIp">IP address of the Hue Bridge</param>
        /// <param name="appKey">Application key for authentication</param>
        /// <param name="areaId">Entertainment area UUID</param>
        /// <param name="cancellationToken">Cancels activation without changing cleanup behavior.</param>
        /// <returns>True if activation succeeded</returns>
        public async Task<bool> StartEntertainmentArea(
            string bridgeIp,
            string appKey,
            string areaId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Use retry for transient errors — a failure here aborts the entire sync session
                var result = await ExecuteWithRetry(async () =>
                {
                    var url = BuildBridgeUrl("https", bridgeIp, $"/clip/v2/resource/entertainment_configuration/{Uri.EscapeDataString(areaId)}");
                    using var request = new HttpRequestMessage(HttpMethod.Put, url);
                    request.Headers.Add("hue-application-key", appKey);
                    request.Content = new StringContent("{\"action\":\"start\"}", System.Text.Encoding.UTF8, "application/json");

                    using var response = await SendBridgeRequestAsync(request, cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        if (IsRetriableStatusCode(response.StatusCode))
                        {
                            throw new HttpRequestException(
                                $"Hue bridge returned HTTP {(int)response.StatusCode} while starting area {areaId}.",
                                null,
                                response.StatusCode);
                        }

                        _logger.LogError("Failed to start entertainment area {0}: HTTP {1}", areaId, (int)response.StatusCode);
                        return false;
                    }

                    _logger.LogInformation("Entertainment area {0} activated for streaming", areaId);
                    return true;
                }, cancellationToken: cancellationToken).ConfigureAwait(false);

                return result == true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception activating entertainment area {0} after retries", areaId);
                return false;
            }
        }

        /// <summary>
        /// Deactivates a Hue Entertainment Area after streaming ends.
        /// This returns lights to normal Hue control.
        /// </summary>
        /// <param name="bridgeIp">The IP address of the Hue Bridge.</param>
        /// <param name="appKey">The application key for authentication.</param>
        /// <param name="areaId">The entertainment area ID.</param>
        /// <param name="cancellationToken">Bounds cleanup without changing its best-effort result contract.</param>
        public async Task StopEntertainmentArea(
            string bridgeIp,
            string appKey,
            string areaId,
            CancellationToken cancellationToken = default)
        {
            await StopEntertainmentAreaWithResult(bridgeIp, appKey, areaId, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> StopEntertainmentAreaWithResult(
            string bridgeIp,
            string appKey,
            string areaId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await ExecuteWithRetry(async () =>
                {
                    var url = BuildBridgeUrl("https", bridgeIp, $"/clip/v2/resource/entertainment_configuration/{Uri.EscapeDataString(areaId)}");
                    using var request = new HttpRequestMessage(HttpMethod.Put, url);
                    request.Headers.Add("hue-application-key", appKey);
                    request.Content = new StringContent("{\"action\":\"stop\"}", System.Text.Encoding.UTF8, "application/json");

                    using var response = await SendBridgeRequestAsync(request, cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        if (IsRetriableStatusCode(response.StatusCode))
                        {
                            throw new HttpRequestException(
                                $"Hue bridge returned HTTP {(int)response.StatusCode} while stopping area {areaId}.",
                                null,
                                response.StatusCode);
                        }

                        _logger.LogWarning("Failed to stop entertainment area {0}: HTTP {1}", areaId, (int)response.StatusCode);
                        return false;
                    }

                    _logger.LogInformation("Entertainment area {0} deactivated", areaId);
                    return true;
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
                return result == true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception deactivating entertainment area {0}", areaId);
                return false;
            }
        }

        private static bool IsRetriableStatusCode(HttpStatusCode statusCode)
        {
            var code = (int)statusCode;
            return code == (int)HttpStatusCode.RequestTimeout || code == 429 || code >= 500;
        }

        public record EntertainmentArea(
            [property: System.Text.Json.Serialization.JsonPropertyName("id")] string Id,
            [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name);

        /// <summary>
        /// Retrieves all entertainment areas configured on the bridge
        /// </summary>
        /// <param name="bridgeIp">The IP address of the Hue Bridge</param>
        /// <param name="appKey">The application key for authentication</param>
        /// <param name="cancellationToken">Cancels the areas request.</param>
        /// <returns>List of entertainment areas, or null if failed</returns>
        public async Task<List<EntertainmentArea>?> GetEntertainmentAreas(
            string bridgeIp,
            string appKey,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await ExecuteWithRetry(async () =>
                {
                    var url = BuildBridgeUrl("https", bridgeIp, "/clip/v2/resource/entertainment_configuration");
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("hue-application-key", appKey);

                    using var response = await SendBridgeRequestAsync(request, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    var json = await ReadResponseBodyAsync(response, cancellationToken).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);

                    var results = new List<EntertainmentArea>();
                    if (doc.RootElement.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
                    {
                        if (dataElement.GetArrayLength() > MaxEntertainmentAreas)
                        {
                            _logger.LogWarning(
                                "Hue bridge returned more than the maximum allowed entertainment areas ({0})",
                                MaxEntertainmentAreas);
                            return null;
                        }

                        foreach (var area in dataElement.EnumerateArray())
                        {
                            var id = area.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                                ? idProp.GetString() ?? string.Empty
                                : string.Empty;
                            var name = area.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object &&
                                       meta.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                                ? nameProp.GetString() ?? string.Empty
                                : string.Empty;

                            if (!string.IsNullOrEmpty(id))
                            {
                                results.Add(new EntertainmentArea(id, string.IsNullOrEmpty(name) ? id : name));
                            }
                        }
                    }

                    return results;
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to load Hue entertainment areas");
                return null;
            }
        }

        /// <summary>
        /// A restorable Hue light state. Hue exposes both color and color-temperature
        /// resources for many lights; only the mode that was valid when the state was
        /// captured is sent back during restoration.
        /// </summary>
        public record LightState(
            string Id,
            bool IsOn,
            int Brightness,
            double X,
            double Y,
            int? Mirek = null,
            bool HasColor = true,
            LightStateSnapshot? Snapshot = null)
        {
            // Preserve the pre-snapshot constructor signature for binary consumers
            // that create LightState instances outside the plugin assembly.
            public LightState(
                string id,
                bool isOn,
                int brightness,
                double x,
                double y,
                int? mirek,
                bool hasColor)
                : this(id, isOn, brightness, x, y, mirek, hasColor, null)
            {
            }
        }

        /// <summary>
        /// Optional Hue v2 light-state resources that can be safely restored. The DTO
        /// deliberately contains only writable values; bridge capability and status
        /// metadata are never retained for replay.
        /// </summary>
        public sealed record LightStateSnapshot(
            LightGradientSnapshot? Gradient = null,
            LightEffectsSnapshot? Effects = null,
            LightEffectsV2Snapshot? EffectsV2 = null,
            LightTimedEffectsSnapshot? TimedEffects = null,
            LightAlertSnapshot? Alert = null);

        /// <summary>Gradient points represented by the Hue v2 color.xy shape.</summary>
        public sealed record LightGradientSnapshot(
            IReadOnlyList<LightGradientPoint> Points,
            string? Mode = null);

        /// <summary>A single gradient or effect parameter color point.</summary>
        public sealed record LightGradientPoint(double X, double Y);

        /// <summary>The writable legacy effects effect value.</summary>
        public sealed record LightEffectsSnapshot(string Effect);

        /// <summary>The writable effects_v2 action and supported parameters.</summary>
        public sealed record LightEffectsV2Snapshot(
            string Effect,
            LightEffectParameters? Parameters = null);

        /// <summary>Supported writable effects_v2 parameters.</summary>
        public sealed record LightEffectParameters(
            LightGradientPoint? Color = null,
            int? Mirek = null,
            double? Speed = null);

        /// <summary>The writable timed-effects effect and optional duration.</summary>
        public sealed record LightTimedEffectsSnapshot(
            string Effect,
            long? Duration = null);

        /// <summary>The writable alert action.</summary>
        public sealed record LightAlertSnapshot(string Action);

        /// <summary>
        /// Summarizes a light-state restoration attempt without exposing bridge credentials
        /// or individual light identifiers.
        /// </summary>
        public sealed class LightStateRestoreResult
        {
            public int AttemptedCount { get; init; }
            public int RestoredCount { get; init; }
            public int FailedCount { get; init; }
            public bool Succeeded => FailedCount == 0;
        }

        /// <summary>
        /// Summarizes a bounded brightness update used by pause-time dimming. The
        /// original color/temperature state is intentionally not changed by this
        /// operation, so playback can resume without a visible color reset.
        /// </summary>
        public sealed class LightStateBrightnessResult
        {
            public int AttemptedCount { get; init; }
            public int UpdatedCount { get; init; }
            public int FailedCount { get; init; }
            public bool Succeeded => FailedCount == 0;
        }

        /// <summary>
        /// Summarizes a light-state capture attempt without exposing bridge credentials
        /// or individual light identifiers.
        /// </summary>
        public sealed class LightStateCaptureResult
        {
            public List<LightState> States { get; init; } = new();
            public int AttemptedCount { get; init; }
            public int FailedCount { get; init; }
            public int CapturedCount => States.Count;
            public bool Succeeded => FailedCount == 0;
        }

        private static LightStateSnapshot? ParseLightStateSnapshot(JsonElement light)
        {
            LightGradientSnapshot? gradient = null;
            if (light.TryGetProperty("gradient", out var gradientElement))
                gradient = ParseGradientSnapshot(gradientElement);

            LightEffectsSnapshot? effects = null;
            if (light.TryGetProperty("effects", out var effectsElement))
                effects = ParseEffectsSnapshot(effectsElement);

            LightEffectsV2Snapshot? effectsV2 = null;
            if (light.TryGetProperty("effects_v2", out var effectsV2Element))
                effectsV2 = ParseEffectsV2Snapshot(effectsV2Element);

            LightTimedEffectsSnapshot? timedEffects = null;
            if (light.TryGetProperty("timed_effects", out var timedEffectsElement))
                timedEffects = ParseTimedEffectsSnapshot(timedEffectsElement);

            LightAlertSnapshot? alert = null;
            if (light.TryGetProperty("alert", out var alertElement))
                alert = ParseAlertSnapshot(alertElement);

            if (gradient == null && effects == null && effectsV2 == null && timedEffects == null && alert == null)
                return null;

            return new LightStateSnapshot(gradient, effects, effectsV2, timedEffects, alert);
        }

        private static LightGradientSnapshot? ParseGradientSnapshot(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("points", out var pointsElement) ||
                pointsElement.ValueKind != JsonValueKind.Array ||
                pointsElement.GetArrayLength() < 2 ||
                pointsElement.GetArrayLength() > MaxGradientPoints)
            {
                return null;
            }

            var points = new List<LightGradientPoint>(pointsElement.GetArrayLength());
            foreach (var pointElement in pointsElement.EnumerateArray())
            {
                var colorElement = pointElement;
                if (pointElement.ValueKind == JsonValueKind.Object &&
                    pointElement.TryGetProperty("color", out var wrappedColorElement))
                {
                    // Hue GET responses expose each point through a color feature,
                    // while the writable PUT shape uses the point's xy member directly.
                    colorElement = wrappedColorElement;
                }

                if (pointElement.ValueKind != JsonValueKind.Object ||
                    colorElement.ValueKind != JsonValueKind.Object ||
                    !colorElement.TryGetProperty("xy", out var xyElement) ||
                    !TryReadXyPoint(xyElement, out var point))
                {
                    // A partial gradient is not safely restorable: omit the complete
                    // optional resource while retaining the required light state.
                    return null;
                }

                points.Add(point!);
            }

            string? mode = null;
            if (element.TryGetProperty("mode", out var modeElement) &&
                modeElement.ValueKind == JsonValueKind.String)
            {
                TryNormalizeSafeToken(modeElement.GetString(), out mode);
            }

            return new LightGradientSnapshot(points, mode);
        }

        private static LightEffectsSnapshot? ParseEffectsSnapshot(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object || !TryReadEffectToken(element, out var effect))
                return null;

            return new LightEffectsSnapshot(effect);
        }

        private static LightEffectsV2Snapshot? ParseEffectsV2Snapshot(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return null;

            var effect = string.Empty;
            if (element.TryGetProperty("action", out var actionElement) &&
                actionElement.ValueKind == JsonValueKind.Object)
            {
                TryGetSafeToken(actionElement, "effect", out effect);
            }

            if (string.IsNullOrEmpty(effect))
                TryReadEffectToken(element, out effect);

            if (string.IsNullOrEmpty(effect))
                return null;

            LightEffectParameters? parameters = null;
            if (element.TryGetProperty("parameters", out var parametersElement))
            {
                parameters = ParseEffectParameters(parametersElement);
            }
            else if (element.TryGetProperty("action", out actionElement) &&
                     actionElement.ValueKind == JsonValueKind.Object &&
                     actionElement.TryGetProperty("parameters", out parametersElement))
            {
                parameters = ParseEffectParameters(parametersElement);
            }
            else if (element.TryGetProperty("status", out var statusElement) &&
                     statusElement.ValueKind == JsonValueKind.Object &&
                     statusElement.TryGetProperty("parameters", out parametersElement))
            {
                parameters = ParseEffectParameters(parametersElement);
            }

            return new LightEffectsV2Snapshot(effect, parameters);
        }

        private static LightEffectParameters? ParseEffectParameters(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return null;

            LightGradientPoint? color = null;
            if (element.TryGetProperty("color", out var colorElement) &&
                colorElement.ValueKind == JsonValueKind.Object &&
                colorElement.TryGetProperty("xy", out var xyElement) &&
                TryReadXyPoint(xyElement, out var point))
            {
                color = point;
            }

            int? mirek = null;
            if (element.TryGetProperty("color_temperature", out var colorTemperatureElement) &&
                colorTemperatureElement.ValueKind == JsonValueKind.Object &&
                TryReadMirek(colorTemperatureElement, out var mirekValue))
            {
                mirek = mirekValue;
            }

            double? speed = null;
            if (TryGetFiniteNumber(element, "speed", out var speedValue) &&
                speedValue >= 0 && speedValue <= 1)
            {
                speed = speedValue;
            }

            return color == null && mirek == null && speed == null
                ? null
                : new LightEffectParameters(color, mirek, speed);
        }

        private static LightTimedEffectsSnapshot? ParseTimedEffectsSnapshot(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object || !TryReadEffectToken(element, out var effect))
                return null;

            long? duration = null;
            if (TryGetFiniteNumber(element, "duration", out var durationValue) &&
                durationValue >= 0 &&
                durationValue <= MaxTimedEffectDurationMilliseconds &&
                durationValue == Math.Truncate(durationValue))
            {
                duration = (long)durationValue;
            }

            return new LightTimedEffectsSnapshot(effect, duration);
        }

        private static LightAlertSnapshot? ParseAlertSnapshot(JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Object && TryGetSafeToken(element, "action", out var action)
                ? new LightAlertSnapshot(action)
                : null;
        }

        private static bool TryReadEffectToken(JsonElement element, out string effect)
        {
            effect = string.Empty;
            if (TryGetSafeToken(element, "effect", out effect))
                return true;

            if (TryGetSafeToken(element, "status", out effect))
                return true;

            if (element.TryGetProperty("status", out var statusElement) &&
                statusElement.ValueKind == JsonValueKind.Object &&
                TryGetSafeToken(statusElement, "effect", out effect))
            {
                return true;
            }

            return false;
        }

        private static bool TryGetSafeToken(JsonElement element, string propertyName, out string value)
        {
            value = string.Empty;
            return element.ValueKind == JsonValueKind.Object &&
                   element.TryGetProperty(propertyName, out var valueElement) &&
                   valueElement.ValueKind == JsonValueKind.String &&
                   TryNormalizeSafeToken(valueElement.GetString(), out value);
        }

        private static bool TryNormalizeSafeToken(string? rawValue, out string value)
        {
            value = string.Empty;
            if (string.IsNullOrWhiteSpace(rawValue))
                return false;

            var normalized = rawValue.Trim();
            if (normalized.Length > MaxLightStateTokenLength)
                return false;

            foreach (var character in normalized)
            {
                if (char.IsControl(character))
                    return false;
            }

            value = normalized;
            return true;
        }

        private static bool TryReadXyPoint(JsonElement element, out LightGradientPoint? point)
        {
            point = null;
            if (element.ValueKind != JsonValueKind.Object ||
                !TryGetFiniteNumber(element, "x", out var x) ||
                !TryGetFiniteNumber(element, "y", out var y) ||
                x < 0 || x > 1 || y < 0 || y > 1)
            {
                return false;
            }

            point = new LightGradientPoint(x, y);
            return true;
        }

        private static bool TryReadMirek(JsonElement element, out int mirek)
        {
            mirek = 0;
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("mirek", out var mirekElement) ||
                mirekElement.ValueKind != JsonValueKind.Number ||
                !mirekElement.TryGetInt32(out mirek) ||
                mirek < 153 || mirek > 500)
            {
                mirek = 0;
                return false;
            }

            return true;
        }

        private static bool TryGetFiniteNumber(JsonElement element, string propertyName, out double value)
        {
            value = 0;
            return element.ValueKind == JsonValueKind.Object &&
                   element.TryGetProperty(propertyName, out var numberElement) &&
                   numberElement.ValueKind == JsonValueKind.Number &&
                   numberElement.TryGetDouble(out value) &&
                   double.IsFinite(value);
        }

        /// <summary>
        /// Gets the current state of lights in an entertainment area for restoration later.
        /// When channelIds is supplied, only lights belonging to those channel IDs are read.
        /// </summary>
        /// <param name="bridgeIp">The IP address of the Hue Bridge</param>
        /// <param name="appKey">The application key for authentication</param>
        /// <param name="areaConfig">The entertainment area configuration</param>
        /// <param name="channelIds">Optional entertainment channel IDs to capture.</param>
        /// <param name="cancellationToken">Cancels the capture request without changing cleanup behavior.</param>
        /// <returns>The light states that were captured. Use GetLightStatesWithResult when
        /// the caller must distinguish a complete capture from a partial one.</returns>
        public async Task<List<LightState>> GetLightStates(
            string bridgeIp,
            string appKey,
            JsonElement areaConfig,
            IReadOnlySet<int>? channelIds = null,
            CancellationToken cancellationToken = default)
        {
            var result = await GetLightStatesWithResult(
                bridgeIp,
                appKey,
                areaConfig,
                channelIds,
                cancellationToken).ConfigureAwait(false);
            return result.States;
        }

        /// <summary>
        /// Gets the current state of each unique light in an entertainment area with the
        /// configured retry policy and reports partial capture failures explicitly.
        /// </summary>
        /// <param name="bridgeIp">The IP address of the Hue Bridge</param>
        /// <param name="appKey">The application key for authentication</param>
        /// <param name="areaConfig">The entertainment area configuration</param>
        /// <param name="channelIds">Optional entertainment channel IDs to capture.</param>
        /// <param name="cancellationToken">Cancels the capture request without changing cleanup behavior.</param>
        /// <returns>A capture summary. States can be partial when one or more light requests fail.</returns>
        public async Task<LightStateCaptureResult> GetLightStatesWithResult(
            string bridgeIp,
            string appKey,
            JsonElement areaConfig,
            IReadOnlySet<int>? channelIds = null,
            CancellationToken cancellationToken = default)
        {
            var lightIds = new List<string>();
            var seenLightIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (areaConfig.TryGetProperty("channels", out var channels) &&
                channels.ValueKind == JsonValueKind.Array)
            {
                foreach (var channel in channels.EnumerateArray())
                {
                    if (channelIds != null &&
                        (!channel.TryGetProperty("channel_id", out var channelIdProperty) ||
                         !channelIdProperty.TryGetInt32(out var channelId) ||
                         !channelIds.Contains(channelId)))
                    {
                        continue;
                    }

                    if (!channel.TryGetProperty("members", out var members) || members.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var member in members.EnumerateArray())
                    {
                        if (!member.TryGetProperty("service", out var service) || service.ValueKind != JsonValueKind.Object ||
                            !service.TryGetProperty("rid", out var ridProp) || ridProp.ValueKind != JsonValueKind.String)
                            continue;

                        var rawLightId = ridProp.GetString();
                        if (string.IsNullOrWhiteSpace(rawLightId))
                            continue;

                        if (!TryNormalizeSafeToken(rawLightId, out var lightId))
                        {
                            _logger.LogWarning("Hue entertainment configuration contained an invalid light resource identifier");
                            return new LightStateCaptureResult
                            {
                                FailedCount = 1
                            };
                        }

                        if (!seenLightIds.Add(lightId))
                            continue;

                        if (lightIds.Count >= MaxLightStateRequests)
                        {
                            _logger.LogWarning(
                                "Hue entertainment configuration contains more than {0} unique light resources",
                                MaxLightStateRequests);
                            return new LightStateCaptureResult
                            {
                                FailedCount = 1
                            };
                        }

                        lightIds.Add(lightId);
                    }
                }
            }

            var states = new List<LightState>();
            var failedCount = 0;
            foreach (var lightId in lightIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var state = await ExecuteWithRetry(async () =>
                    {
                        var url = BuildBridgeUrl("https", bridgeIp, $"/clip/v2/resource/light/{Uri.EscapeDataString(lightId)}");
                        using var request = new HttpRequestMessage(HttpMethod.Get, url);
                        request.Headers.Add("hue-application-key", appKey);

                        using var response = await SendBridgeRequestAsync(request, cancellationToken).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();

                        var json = await ReadResponseBodyAsync(response, cancellationToken).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(json);
                        if (!doc.RootElement.TryGetProperty("data", out var data) ||
                            data.ValueKind != JsonValueKind.Array ||
                            data.GetArrayLength() == 0)
                        {
                            throw new InvalidOperationException("Hue light response did not contain state data.");
                        }

                        var light = data[0];
                        if (light.ValueKind != JsonValueKind.Object ||
                            (light.TryGetProperty("id", out var lightIdProperty) &&
                             (lightIdProperty.ValueKind != JsonValueKind.String ||
                              !string.Equals(
                                  lightIdProperty.GetString()?.Trim(),
                                  lightId,
                                  StringComparison.OrdinalIgnoreCase))))
                        {
                            // A response for another light must never be associated with
                            // the requested resource. Preserve compatibility with older
                            // bridge responses that omit the optional id field, but fail
                            // closed whenever an explicit id is malformed or mismatched.
                            throw new InvalidOperationException(
                                $"Hue light response did not identify requested light {lightId}.");
                        }

                        if (!light.TryGetProperty("on", out var on) ||
                            !on.TryGetProperty("on", out var onValue) ||
                            !light.TryGetProperty("dimming", out var dimming) ||
                            !dimming.TryGetProperty("brightness", out var brightnessValue))
                        {
                            throw new InvalidOperationException("Hue light response did not contain required state fields.");
                        }

                        var isOn = onValue.GetBoolean();
                        var brightness = Math.Clamp((int)brightnessValue.GetDouble(), 0, 100);

                        double x = 0;
                        double y = 0;
                        var hasColor = false;
                        if (light.TryGetProperty("color", out var color) &&
                            color.TryGetProperty("xy", out var xy) &&
                            xy.TryGetProperty("x", out var xValue) &&
                            xy.TryGetProperty("y", out var yValue) &&
                            xValue.ValueKind == JsonValueKind.Number &&
                            yValue.ValueKind == JsonValueKind.Number)
                        {
                            x = xValue.GetDouble();
                            y = yValue.GetDouble();
                            hasColor = true;
                        }

                        int? mirek = null;
                        if (light.TryGetProperty("color_temperature", out var colorTemperature) &&
                            colorTemperature.ValueKind == JsonValueKind.Object &&
                            colorTemperature.TryGetProperty("mirek", out var mirekValue) &&
                            mirekValue.ValueKind == JsonValueKind.Number &&
                            mirekValue.TryGetInt32(out var mirekNumber))
                        {
                            var mirekIsValid = !colorTemperature.TryGetProperty("mirek_valid", out var validValue) ||
                                                (validValue.ValueKind == JsonValueKind.True && validValue.GetBoolean());
                            if (mirekIsValid)
                                mirek = mirekNumber;
                        }

                        var snapshot = ParseLightStateSnapshot(light);
                        return new LightState(lightId, isOn, brightness, x, y, mirek, hasColor, snapshot);
                    }, cancellationToken: cancellationToken).ConfigureAwait(false);

                    if (state != null)
                        states.Add(state);
                    else
                        failedCount++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get state for light {0}", lightId);
                    failedCount++;
                }
            }

            return new LightStateCaptureResult
            {
                States = states,
                AttemptedCount = lightIds.Count,
                FailedCount = failedCount
            };
        }

        /// <summary>
        /// Restores the saved state of lights in an entertainment area
        /// </summary>
        /// <param name="bridgeIp">The IP address of the Hue Bridge</param>
        /// <param name="appKey">The application key for authentication</param>
        /// <param name="lightStates">The saved light states to restore</param>
        /// <param name="cancellationToken">Bounds cleanup without changing its best-effort result contract.</param>
        public async Task RestoreLightStates(
            string bridgeIp,
            string appKey,
            List<LightState> lightStates,
            CancellationToken cancellationToken = default)
        {
            await RestoreLightStatesWithResult(bridgeIp, appKey, lightStates, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Changes only the dimming level of captured lights. This is used when playback
        /// is paused with the DimToCinemaLevel policy; omitting color and temperature
        /// fields preserves the most recently streamed colors while paused.
        /// </summary>
        public async Task<LightStateBrightnessResult> SetLightBrightnessWithResult(
            string bridgeIp,
            string appKey,
            IReadOnlyList<LightState> lightStates,
            int brightnessPercent,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(lightStates);

            var brightness = Math.Clamp(brightnessPercent, 0, 100);
            var updatedCount = 0;
            var failedCount = 0;

            for (var index = 0; index < lightStates.Count; index++)
            {
                var state = lightStates[index];
                try
                {
                    var updated = await ExecuteWithRetry(async () =>
                    {
                        var url = BuildBridgeUrl("https", bridgeIp, $"/clip/v2/resource/light/{Uri.EscapeDataString(state.Id)}");
                        using var request = new HttpRequestMessage(HttpMethod.Put, url);
                        request.Headers.Add("hue-application-key", appKey);
                        var payload = new
                        {
                            on = new { on = state.IsOn },
                            dimming = new { brightness }
                        };
                        request.Content = new StringContent(
                            JsonSerializer.Serialize(payload),
                            System.Text.Encoding.UTF8,
                            "application/json");

                        using var response = await SendBridgeRequestAsync(request, cancellationToken).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();
                        return true;
                    }, cancellationToken: cancellationToken).ConfigureAwait(false);

                    if (updated == true)
                        updatedCount++;
                    else
                        failedCount++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    failedCount += lightStates.Count - index;
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to set pause brightness for light {0}", state.Id);
                    failedCount++;
                }
            }

            return new LightStateBrightnessResult
            {
                AttemptedCount = lightStates.Count,
                UpdatedCount = updatedCount,
                FailedCount = failedCount
            };
        }

        private static Dictionary<string, object?> BuildLightStatePayload(LightState state)
        {
            var payload = new Dictionary<string, object?>
            {
                ["on"] = new Dictionary<string, object?> { ["on"] = state.IsOn },
                ["dimming"] = new Dictionary<string, object?> { ["brightness"] = state.Brightness }
            };

            if (state.Mirek.HasValue)
            {
                payload["color_temperature"] = new Dictionary<string, object?>
                {
                    ["mirek"] = state.Mirek.Value
                };
            }
            else if (state.HasColor)
            {
                payload["color"] = new Dictionary<string, object?>
                {
                    ["xy"] = new Dictionary<string, object?>
                    {
                        ["x"] = state.X,
                        ["y"] = state.Y
                    }
                };
            }

            AddSnapshotPayload(payload, state.Snapshot);
            return payload;
        }

        private static void AddSnapshotPayload(
            Dictionary<string, object?> payload,
            LightStateSnapshot? snapshot)
        {
            if (snapshot == null)
                return;

            if (snapshot.Gradient != null && TryBuildGradientPayload(snapshot.Gradient, out var gradientPayload))
                payload["gradient"] = gradientPayload;

            // The bridge exposes both effects resources for compatibility, but their
            // PUT actions represent one effect state. Prefer the newer v2 resource and
            // avoid sending conflicting legacy/v2 actions in the same update. Timed
            // effects are likewise mutually exclusive with normal effects.
            Dictionary<string, object?>? timedEffectsPayload = null;
            if (snapshot.TimedEffects != null &&
                !IsNoEffect(snapshot.TimedEffects.Effect) &&
                TryBuildTimedEffectsPayload(snapshot.TimedEffects, out var validTimedEffectsPayload))
            {
                timedEffectsPayload = validTimedEffectsPayload;
            }

            Dictionary<string, object?>? effectsV2Payload = null;
            if (snapshot.EffectsV2 != null &&
                TryBuildEffectsV2Payload(snapshot.EffectsV2, out var validEffectsV2Payload))
            {
                effectsV2Payload = validEffectsV2Payload;
            }

            Dictionary<string, object?>? effectsPayload = null;
            if (snapshot.Effects != null &&
                TryBuildEffectsPayload(snapshot.Effects, out var validEffectsPayload))
            {
                effectsPayload = validEffectsPayload;
            }

            if (timedEffectsPayload != null)
            {
                payload["timed_effects"] = timedEffectsPayload;
            }
            else if (effectsV2Payload != null &&
                     ((snapshot.EffectsV2 != null && !IsNoEffect(snapshot.EffectsV2.Effect)) || effectsPayload == null))
            {
                payload["effects_v2"] = effectsV2Payload;
            }
            else if (effectsPayload != null)
            {
                payload["effects"] = effectsPayload;
            }

            if (snapshot.Alert != null && TryBuildAlertPayload(snapshot.Alert, out var alertPayload))
                payload["alert"] = alertPayload;
        }

        private static bool TryBuildGradientPayload(
            LightGradientSnapshot snapshot,
            out Dictionary<string, object?> payload)
        {
            payload = new Dictionary<string, object?>();
            if (snapshot.Points == null ||
                snapshot.Points.Count < 2 ||
                snapshot.Points.Count > MaxGradientPoints)
            {
                return false;
            }

            var points = new List<object>(snapshot.Points.Count);
            foreach (var point in snapshot.Points)
            {
                if (point == null || !IsValidXy(point.X, point.Y))
                    return false;

                points.Add(new Dictionary<string, object?>
                {
                    ["xy"] = new Dictionary<string, object?>
                    {
                        ["x"] = point.X,
                        ["y"] = point.Y
                    }
                });
            }

            payload["points"] = points;
            if (TryNormalizeSafeToken(snapshot.Mode, out var mode))
                payload["mode"] = mode;

            return true;
        }

        private static bool TryBuildEffectsPayload(
            LightEffectsSnapshot snapshot,
            out Dictionary<string, object?> payload)
        {
            payload = new Dictionary<string, object?>();
            if (!TryNormalizeSafeToken(snapshot.Effect, out var effect))
                return false;

            payload["effect"] = effect;
            return true;
        }

        private static bool TryBuildEffectsV2Payload(
            LightEffectsV2Snapshot snapshot,
            out Dictionary<string, object?> payload)
        {
            payload = new Dictionary<string, object?>();
            if (!TryNormalizeSafeToken(snapshot.Effect, out var effect))
                return false;

            var action = new Dictionary<string, object?>
            {
                ["effect"] = effect
            };
            if (snapshot.Parameters != null && TryBuildEffectParametersPayload(snapshot.Parameters, out var parametersPayload))
                action["parameters"] = parametersPayload;

            payload["action"] = action;
            return true;
        }

        private static bool TryBuildEffectParametersPayload(
            LightEffectParameters parameters,
            out Dictionary<string, object?> payload)
        {
            payload = new Dictionary<string, object?>();

            if (parameters.Color != null && IsValidXy(parameters.Color.X, parameters.Color.Y))
            {
                payload["color"] = new Dictionary<string, object?>
                {
                    ["xy"] = new Dictionary<string, object?>
                    {
                        ["x"] = parameters.Color.X,
                        ["y"] = parameters.Color.Y
                    }
                };
            }

            if (parameters.Mirek is >= 153 and <= 500)
            {
                payload["color_temperature"] = new Dictionary<string, object?>
                {
                    ["mirek"] = parameters.Mirek.Value
                };
            }

            if (parameters.Speed is double speed && double.IsFinite(speed) && speed >= 0 && speed <= 1)
                payload["speed"] = speed;

            return payload.Count > 0;
        }

        private static bool TryBuildTimedEffectsPayload(
            LightTimedEffectsSnapshot snapshot,
            out Dictionary<string, object?> payload)
        {
            payload = new Dictionary<string, object?>();
            if (!TryNormalizeSafeToken(snapshot.Effect, out var effect))
                return false;

            payload["effect"] = effect;
            if (snapshot.Duration is long duration &&
                duration >= 0 &&
                duration <= MaxTimedEffectDurationMilliseconds)
            {
                payload["duration"] = duration;
            }

            return true;
        }

        private static bool TryBuildAlertPayload(
            LightAlertSnapshot snapshot,
            out Dictionary<string, object?> payload)
        {
            payload = new Dictionary<string, object?>();
            if (!TryNormalizeSafeToken(snapshot.Action, out var action))
                return false;

            payload["action"] = action;
            return true;
        }

        private static bool IsValidXy(double x, double y)
        {
            return double.IsFinite(x) && double.IsFinite(y) &&
                   x >= 0 && x <= 1 && y >= 0 && y <= 1;
        }

        private static bool IsNoEffect(string effect)
        {
            return string.Equals(effect, "no_effect", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(effect, "none", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Restores each saved light state independently with the configured retry policy and
        /// returns an aggregate result so callers can distinguish complete from partial cleanup.
        /// </summary>
        /// <param name="bridgeIp">The IP address of the Hue Bridge</param>
        /// <param name="appKey">The application key for authentication</param>
        /// <param name="lightStates">The saved light states to restore</param>
        /// <param name="cancellationToken">Bounds cleanup without changing its best-effort result contract.</param>
        public async Task<LightStateRestoreResult> RestoreLightStatesWithResult(
            string bridgeIp,
            string appKey,
            List<LightState> lightStates,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(lightStates);

            var restoredCount = 0;
            var failedCount = 0;

            // Restore each light independently — failures on one light don't block others —
            // while still applying the configured retry policy to transient failures.
            for (var index = 0; index < lightStates.Count; index++)
            {
                var state = lightStates[index];
                try
                {
                    var restored = await ExecuteWithRetry(async () =>
                    {
                        var url = BuildBridgeUrl("https", bridgeIp, $"/clip/v2/resource/light/{Uri.EscapeDataString(state.Id)}");
                        using var request = new HttpRequestMessage(HttpMethod.Put, url);
                        request.Headers.Add("hue-application-key", appKey);

                        var payload = BuildLightStatePayload(state);

                        var json = JsonSerializer.Serialize(payload);
                        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                        using var response = await SendBridgeRequestAsync(request, cancellationToken).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();
                        return true;
                    }, cancellationToken: cancellationToken).ConfigureAwait(false);

                    if (restored == true)
                        restoredCount++;
                    else
                        failedCount++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    failedCount += lightStates.Count - index;
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to restore state for light {0}", state.Id);
                    failedCount++;
                }
            }

            return new LightStateRestoreResult
            {
                AttemptedCount = lightStates.Count,
                RestoredCount = restoredCount,
                FailedCount = failedCount
            };
        }
    }
}
