using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
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
        private const int DefaultRetryAttempts = 3;
        private const int RetryDelayMs = 1000;

        /// <summary>
        /// Number of retry attempts for network operations. Defaults to 3; set from plugin configuration.
        /// </summary>
        public int RetryAttempts { get; set; } = DefaultRetryAttempts;

        public HueClient(HttpClient httpClient, ILogger<HueClient> logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _httpClient.Timeout = TimeSpan.FromSeconds(10);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Executes an HTTP operation with retry logic and exponential backoff
        /// </summary>
        private async Task<T?> ExecuteWithRetry<T>(Func<Task<T>> operation, int? maxRetries = null)
        {
            ArgumentNullException.ThrowIfNull(operation);

            var retries = Math.Max(0, maxRetries ?? RetryAttempts);
            for (int attempt = 0; attempt <= retries; attempt++)
            {
                try
                {
                    return await operation().ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt < retries && IsRetriableException(ex))
                {
                    var delay = RetryDelayMs * (int)Math.Pow(2, attempt);
                    _logger.LogWarning(ex, "Network operation failed (attempt {0}/{1}), retrying in {2}ms", attempt + 1, retries + 1, delay);
                    await Task.Delay(delay).ConfigureAwait(false);
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

            return IPAddress.TryParse(host, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? $"[{host}]"
                : host;
        }

        private static string BuildBridgeUrl(string scheme, string bridgeIp, string path)
        {
            var host = FormatBridgeHost(bridgeIp);
            return $"{scheme}://{host}/{path.TrimStart('/')}";
        }

        /// <summary>
        /// Discovers the IP address of a Hue Bridge on the local network using the meethue.com discovery service
        /// </summary>
        /// <returns>The IP address of the bridge, or empty string if not found</returns>
        public async Task<string> DiscoverBridgeIp()
        {
            // Simple discovery via meethue.com or mDNS (simplified for now)
            try
            {
                var response = await _httpClient.GetStringAsync("https://discovery.meethue.com/");
                using var doc = JsonDocument.Parse(response);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var bridge in doc.RootElement.EnumerateArray())
                    {
                        if (!bridge.TryGetProperty("internalipaddress", out var addressProperty) ||
                            addressProperty.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var addressText = addressProperty.GetString();
                        if (IPAddress.TryParse(addressText, out var address))
                        {
                            return address.ToString();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error discovering bridge");
            }
            return "";
        }

        /// <summary>
        /// Registers this application with the Hue Bridge to obtain credentials
        /// </summary>
        /// <param name="ip">The IP address of the Hue Bridge</param>
        /// <returns>Registration result with username and client key, or null if failed</returns>
        /// <remarks>The physical link button must be pressed on the bridge before calling this method</remarks>
        public async Task<Api.HueRegistrationResult?> RegisterWithBridge(string ip)
        {
            try
            {
                return await ExecuteWithRetry(async () =>
                {
                    using var content = new StringContent("{\"devicetype\":\"jellyfin_hue#server\", \"generateclientkey\":true}", System.Text.Encoding.UTF8, "application/json");
                    // Hue bridge firmware now requires the local API to be accessed over TLS.
                    // The bridge certificate is handled by PluginServiceRegistrator for local
                    // bridge addresses only; public discovery traffic keeps normal validation.
                    using var response = await _httpClient.PostAsync(BuildBridgeUrl("https", ip, "/api"), content).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        response.EnsureSuccessStatusCode();
                    }

                    var json = await response.Content.ReadAsStringAsync();

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

                    _logger.LogWarning("Registration failed: {0}", json);
                    return null;
                }).ConfigureAwait(false);
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
        /// <returns>JSON element containing the area configuration, or null if failed</returns>
        public async Task<JsonElement?> GetEntertainmentConfiguration(string bridgeIp, string appKey, string areaId)
        {
            try
            {
                return await ExecuteWithRetry(async () =>
                {
                    var url = BuildBridgeUrl("https", bridgeIp, $"/clip/v2/resource/entertainment_configuration/{Uri.EscapeDataString(areaId)}");
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("hue-application-key", appKey);

                    using var response = await _httpClient.SendAsync(request);
                    response.EnsureSuccessStatusCode();

                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    // Expected: { "data": [ { "channels": [ ... ] } ] }
                    if (!doc.RootElement.TryGetProperty("data", out var data) ||
                        data.ValueKind != JsonValueKind.Array ||
                        data.GetArrayLength() == 0)
                    {
                        _logger.LogWarning("Entertainment configuration response did not contain an area for {0}", areaId);
                        return (JsonElement?)null;
                    }

                    // Clone the element so the JsonDocument can be safely disposed
                    return (JsonElement?)data[0].Clone();
                }).ConfigureAwait(false);
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
        /// <returns>True if activation succeeded</returns>
        public async Task<bool> StartEntertainmentArea(string bridgeIp, string appKey, string areaId)
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

                    using var response = await _httpClient.SendAsync(request);
                    if (!response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        if (IsRetriableStatusCode(response.StatusCode))
                        {
                            throw new HttpRequestException(
                                $"Hue bridge returned HTTP {(int)response.StatusCode} while starting area {areaId}: {body}",
                                null,
                                response.StatusCode);
                        }

                        _logger.LogError("Failed to start entertainment area {0}: HTTP {1} — {2}", areaId, (int)response.StatusCode, body);
                        return false;
                    }

                    _logger.LogInformation("Entertainment area {0} activated for streaming", areaId);
                    return true;
                }).ConfigureAwait(false);

                return result == true;
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
        public async Task StopEntertainmentArea(string bridgeIp, string appKey, string areaId)
        {
            try
            {
                await ExecuteWithRetry(async () =>
                {
                    var url = BuildBridgeUrl("https", bridgeIp, $"/clip/v2/resource/entertainment_configuration/{Uri.EscapeDataString(areaId)}");
                    using var request = new HttpRequestMessage(HttpMethod.Put, url);
                    request.Headers.Add("hue-application-key", appKey);
                    request.Content = new StringContent("{\"action\":\"stop\"}", System.Text.Encoding.UTF8, "application/json");

                    using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (IsRetriableStatusCode(response.StatusCode))
                        {
                            throw new HttpRequestException(
                                $"Hue bridge returned HTTP {(int)response.StatusCode} while stopping area {areaId}: {body}",
                                null,
                                response.StatusCode);
                        }

                        _logger.LogWarning("Failed to stop entertainment area {0}: HTTP {1} — {2}", areaId, (int)response.StatusCode, body);
                        return false;
                    }

                    _logger.LogInformation("Entertainment area {0} deactivated", areaId);
                    return true;
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception deactivating entertainment area {0}", areaId);
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
        /// <returns>List of entertainment areas, or null if failed</returns>
        public async Task<List<EntertainmentArea>?> GetEntertainmentAreas(string bridgeIp, string appKey)
        {
            try
            {
                return await ExecuteWithRetry(async () =>
                {
                    var url = BuildBridgeUrl("https", bridgeIp, "/clip/v2/resource/entertainment_configuration");
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("hue-application-key", appKey);

                    using var response = await _httpClient.SendAsync(request);
                    response.EnsureSuccessStatusCode();

                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);

                    var results = new List<EntertainmentArea>();
                    if (doc.RootElement.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
                    {
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
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to load Hue entertainment areas");
                return null;
            }
        }

        public record LightState(string Id, bool IsOn, int Brightness, double X, double Y);

        /// <summary>
        /// Gets the current state of all lights in an entertainment area for restoration later
        /// </summary>
        /// <param name="bridgeIp">The IP address of the Hue Bridge</param>
        /// <param name="appKey">The application key for authentication</param>
        /// <param name="areaConfig">The entertainment area configuration</param>
        /// <returns>List of light states, or null if failed</returns>
        public async Task<List<LightState>?> GetLightStates(string bridgeIp, string appKey, JsonElement areaConfig)
        {
            return await ExecuteWithRetry(async () =>
            {
                var states = new List<LightState>();

                if (!areaConfig.TryGetProperty("channels", out var channels) || channels.ValueKind != JsonValueKind.Array)
                    return states;

                foreach (var channel in channels.EnumerateArray())
                {
                    if (!channel.TryGetProperty("members", out var members) || members.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var member in members.EnumerateArray())
                    {
                        if (!member.TryGetProperty("service", out var service) || service.ValueKind != JsonValueKind.Object ||
                            !service.TryGetProperty("rid", out var ridProp) || ridProp.ValueKind != JsonValueKind.String)
                            continue;

                        var lightId = ridProp.GetString();
                        if (string.IsNullOrEmpty(lightId))
                            continue;

                        try
                        {
                            var url = BuildBridgeUrl("https", bridgeIp, $"/clip/v2/resource/light/{Uri.EscapeDataString(lightId)}");
                            using var request = new HttpRequestMessage(HttpMethod.Get, url);
                            request.Headers.Add("hue-application-key", appKey);

                            using var response = await _httpClient.SendAsync(request);
                            response.EnsureSuccessStatusCode();

                            var json = await response.Content.ReadAsStringAsync();
                            using var doc = JsonDocument.Parse(json);

                            if (doc.RootElement.TryGetProperty("data", out var data) &&
                                data.ValueKind == JsonValueKind.Array &&
                                data.GetArrayLength() > 0)
                            {
                                var light = data[0];
                                if (!light.TryGetProperty("on", out var on) || !on.TryGetProperty("on", out var onValue) ||
                                    !light.TryGetProperty("dimming", out var dimming) ||
                                    !dimming.TryGetProperty("brightness", out var brightnessValue))
                                {
                                    continue;
                                }

                                var isOn = onValue.GetBoolean();
                                var brightness = Math.Clamp((int)brightnessValue.GetDouble(), 0, 100);

                                double x = 0, y = 0;
                                if (light.TryGetProperty("color", out var color) &&
                                    color.TryGetProperty("xy", out var xy))
                                {
                                    x = xy.GetProperty("x").GetDouble();
                                    y = xy.GetProperty("y").GetDouble();
                                }

                                states.Add(new LightState(lightId, isOn, brightness, x, y));
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to get state for light {0}", lightId);
                        }
                    }
                }

                return states;
            }) ?? null;
        }

        /// <summary>
        /// Restores the saved state of lights in an entertainment area
        /// </summary>
        /// <param name="bridgeIp">The IP address of the Hue Bridge</param>
        /// <param name="appKey">The application key for authentication</param>
        /// <param name="lightStates">The saved light states to restore</param>
        public async Task RestoreLightStates(string bridgeIp, string appKey, List<LightState> lightStates)
        {
            ArgumentNullException.ThrowIfNull(lightStates);

            // Restore each light independently — failures on one light don't block others.
            // ExecuteWithRetry is intentionally not used here: the per-light try/catch means
            // no exception would ever escape to trigger a retry anyway.
            foreach (var state in lightStates)
            {
                try
                {
                    var url = BuildBridgeUrl("https", bridgeIp, $"/clip/v2/resource/light/{Uri.EscapeDataString(state.Id)}");
                    using var request = new HttpRequestMessage(HttpMethod.Put, url);
                    request.Headers.Add("hue-application-key", appKey);

                    var payload = new
                    {
                        on = new { on = state.IsOn },
                        dimming = new { brightness = state.Brightness },
                        color = new { xy = new { x = state.X, y = state.Y } }
                    };

                    var json = JsonSerializer.Serialize(payload);
                    request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                    using var response = await _httpClient.SendAsync(request);
                    response.EnsureSuccessStatusCode();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to restore state for light {0}", state.Id);
                }
            }
        }
    }
}
