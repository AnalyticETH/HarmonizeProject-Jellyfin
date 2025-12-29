using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
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

        public HueClient(HttpClient httpClient, ILogger<HueClient> logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _httpClient.Timeout = TimeSpan.FromSeconds(10);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Executes an HTTP operation with retry logic and exponential backoff
        /// </summary>
        private async Task<T?> ExecuteWithRetry<T>(Func<Task<T>> operation, int maxRetries = DefaultRetryAttempts)
        {
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    return await operation();
                }
                catch (Exception ex) when (attempt < maxRetries && IsRetriableException(ex))
                {
                    var delay = RetryDelayMs * (int)Math.Pow(2, attempt);
                    _logger.LogWarning(ex, "Network operation failed (attempt {0}/{1}), retrying in {2}ms", attempt + 1, maxRetries + 1, delay);
                    await Task.Delay(delay);
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
            return ex is HttpRequestException or TaskCanceledException or TimeoutException;
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
                if (doc.RootElement.GetArrayLength() > 0)
                {
                    return doc.RootElement[0].GetProperty("internalipaddress").GetString() ?? "";
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
            return await ExecuteWithRetry(async () =>
            {
                var content = new StringContent("{\"devicetype\":\"jellyfin_hue#server\", \"generateclientkey\":true}", System.Text.Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync($"https://{ip}/api", content);
                var json = await response.Content.ReadAsStringAsync();

                // Response: [{"success":{"username":"...","clientkey":"..."}}] OR [{"error":...}]
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    var first = doc.RootElement[0];
                    if (first.TryGetProperty("success", out var success))
                    {
                        return new Api.HueRegistrationResult
                        {
                            Username = success.GetProperty("username").GetString() ?? "",
                            ClientKey = success.GetProperty("clientkey").GetString() ?? ""
                        };
                    }
                }
                _logger.LogWarning("Registration failed: {0}", json);
                return null;
            }) ?? null;
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
            return await ExecuteWithRetry(async () =>
            {
                var url = $"https://{bridgeIp}/clip/v2/resource/entertainment_configuration/{areaId}";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("hue-application-key", appKey);

                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);
                // Expected: { "data": [ { "channels": [ ... ] } ] }
                return (JsonElement?)doc.RootElement.GetProperty("data")[0];
            }) ?? null;
        }

        public record EntertainmentArea(string Id, string Name);

        /// <summary>
        /// Retrieves all entertainment areas configured on the bridge
        /// </summary>
        /// <param name="bridgeIp">The IP address of the Hue Bridge</param>
        /// <param name="appKey">The application key for authentication</param>
        /// <returns>List of entertainment areas, or null if failed</returns>
        public async Task<List<EntertainmentArea>?> GetEntertainmentAreas(string bridgeIp, string appKey)
        {
            return await ExecuteWithRetry(async () =>
            {
                var url = $"https://{bridgeIp}/clip/v2/resource/entertainment_configuration";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("hue-application-key", appKey);

                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);

                var results = new List<EntertainmentArea>();
                if (doc.RootElement.TryGetProperty("data", out var dataElement))
                {
                    foreach (var area in dataElement.EnumerateArray())
                    {
                        var id = area.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? string.Empty : string.Empty;
                        var name = area.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("name", out var nameProp)
                            ? nameProp.GetString() ?? string.Empty
                            : string.Empty;

                        if (!string.IsNullOrEmpty(id))
                        {
                            results.Add(new EntertainmentArea(id, string.IsNullOrEmpty(name) ? id : name));
                        }
                    }
                }

                return results;
            }) ?? null;
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

                if (!areaConfig.TryGetProperty("channels", out var channels))
                    return states;

                foreach (var channel in channels.EnumerateArray())
                {
                    if (!channel.TryGetProperty("members", out var members))
                        continue;

                    foreach (var member in members.EnumerateArray())
                    {
                        if (!member.TryGetProperty("service", out var service) ||
                            !service.TryGetProperty("rid", out var ridProp))
                            continue;

                        var lightId = ridProp.GetString();
                        if (string.IsNullOrEmpty(lightId))
                            continue;

                        try
                        {
                            var url = $"https://{bridgeIp}/clip/v2/resource/light/{lightId}";
                            var request = new HttpRequestMessage(HttpMethod.Get, url);
                            request.Headers.Add("hue-application-key", appKey);

                            var response = await _httpClient.SendAsync(request);
                            response.EnsureSuccessStatusCode();

                            var json = await response.Content.ReadAsStringAsync();
                            var doc = JsonDocument.Parse(json);

                            if (doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
                            {
                                var light = data[0];
                                var isOn = light.GetProperty("on").GetProperty("on").GetBoolean();
                                var brightness = light.GetProperty("dimming").GetProperty("brightness").GetDouble();

                                double x = 0, y = 0;
                                if (light.TryGetProperty("color", out var color) &&
                                    color.TryGetProperty("xy", out var xy))
                                {
                                    x = xy.GetProperty("x").GetDouble();
                                    y = xy.GetProperty("y").GetDouble();
                                }

                                states.Add(new LightState(lightId, isOn, (int)brightness, x, y));
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
            await ExecuteWithRetry(async () =>
            {
                foreach (var state in lightStates)
                {
                    try
                    {
                        var url = $"https://{bridgeIp}/clip/v2/resource/light/{state.Id}";
                        var request = new HttpRequestMessage(HttpMethod.Put, url);
                        request.Headers.Add("hue-application-key", appKey);

                        var payload = new
                        {
                            on = new { on = state.IsOn },
                            dimming = new { brightness = state.Brightness },
                            color = new { xy = new { x = state.X, y = state.Y } }
                        };

                        var json = JsonSerializer.Serialize(payload);
                        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                        var response = await _httpClient.SendAsync(request);
                        response.EnsureSuccessStatusCode();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to restore state for light {0}", state.Id);
                    }
                }
                return Task.CompletedTask;
            });
        }
    }
}
