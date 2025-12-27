using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Hue.Hue
{
    public class HueClient
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<HueClient> _logger;

        public HueClient(ILogger<HueClient> logger)
        {
            _httpClient = new HttpClient();
            _logger = logger;
            // Ignore SSL errors for local Hue Bridge (self-signed)
            var handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
            _httpClient = new HttpClient(handler);
        }

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
    }
}
