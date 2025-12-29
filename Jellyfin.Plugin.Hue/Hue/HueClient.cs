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

        public HueClient(ILogger<HueClient> logger)
        {
            _logger = logger;
            // Ignore SSL errors for local Hue Bridge (self-signed)
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(10) // Add timeout for bridge communication
            };
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
            try
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling Register API");
            }
            return null;
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
                var url = $"https://{bridgeIp}/clip/v2/resource/entertainment_configuration/{areaId}";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("hue-application-key", appKey);
                
                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();
                
                var json = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);
                // Expected: { "data": [ { "channels": [ ... ] } ] }
                return doc.RootElement.GetProperty("data")[0];
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching entertainment configuration");
                return null;
            }
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
            try
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching entertainment areas");
                return null;
            }
        }
    }
}
