using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Jellyfin.Plugin.Hue.Configuration;
using Jellyfin.Plugin.Hue.Hue;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Hue.Api
{
    [ApiController]
    [Route("HueSync")]
    [Authorize(Policy = "RequiresElevation")]
    [Produces(MediaTypeNames.Application.Json)]
    public class HueApiController : ControllerBase
    {
        private readonly HueClient _hueClient;
        private readonly Service.HueSyncService? _syncService;

        public HueApiController(HueClient hueClient, IEnumerable<Microsoft.Extensions.Hosting.IHostedService> hostedServices)
        {
            _hueClient = hueClient;
            _syncService = hostedServices.OfType<Service.HueSyncService>().FirstOrDefault();
        }

        [HttpPost("Register")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<HueRegistrationResult>> RegisterBridge([FromBody] HueRegistrationRequest request)
        {
            if (string.IsNullOrEmpty(request.IpAddress))
            {
                return BadRequest("IP Address is required.");
            }

            var result = await _hueClient.RegisterWithBridge(request.IpAddress);
            if (result == null)
            {
                return BadRequest("Failed to register. Did you press the Link Button?");
            }

            return Ok(result);
        }

        [HttpGet("EntertainmentAreas")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<IEnumerable<HueClient.EntertainmentArea>>> GetEntertainmentAreas(
            [FromQuery(Name = "ip")] string? bridgeIp,
            [FromQuery(Name = "appKey")] string? appKey)
        {
            bridgeIp ??= Plugin.Instance?.Configuration?.HueBridgeIp;
            appKey ??= Plugin.Instance?.Configuration?.HueAppKey;

            if (string.IsNullOrWhiteSpace(bridgeIp) || string.IsNullOrWhiteSpace(appKey))
            {
                return BadRequest("Bridge IP and app key are required before loading entertainment areas.");
            }

            var areas = await _hueClient.GetEntertainmentAreas(bridgeIp, appKey);
            if (areas == null)
            {
                return StatusCode(StatusCodes.Status502BadGateway, "Could not contact the Hue bridge.");
            }

            return Ok(areas);
        }

        [HttpGet("Status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<HueSyncStatus> GetStatus()
        {
            var config = Plugin.Instance?.Configuration;
            var status = new HueSyncStatus
            {
                IsEnabled = config?.SyncEnabled ?? false,
                IsSyncing = _syncService?.IsSyncing ?? false,
                CurrentItem = _syncService?.CurrentItemName,
                BridgeIp = config?.HueBridgeIp,
                EntertainmentAreaId = config?.EntertainmentAreaId
            };

            return Ok(status);
        }

        /// <summary>
        /// Gets all user-to-bridge mappings
        /// </summary>
        [HttpGet("UserMappings")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<IEnumerable<UserBridgeMapping>> GetUserMappings()
        {
            var config = Plugin.Instance?.Configuration;
            return Ok(config?.UserMappings ?? new List<UserBridgeMapping>());
        }

        /// <summary>
        /// Saves or updates a user-to-bridge mapping
        /// </summary>
        [HttpPost("UserMappings")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public ActionResult SaveUserMapping([FromBody] UserBridgeMapping mapping)
        {
            if (string.IsNullOrWhiteSpace(mapping.UserId))
            {
                return BadRequest("User ID is required.");
            }

            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return BadRequest("Plugin configuration not available.");
            }

            // Remove existing mapping for this user if exists
            config.UserMappings.RemoveAll(m => m.UserId == mapping.UserId);

            // Add the new/updated mapping
            config.UserMappings.Add(mapping);

            Plugin.Instance?.SaveConfiguration();

            return Ok(new { message = "Mapping saved successfully." });
        }

        /// <summary>
        /// Deletes a user-to-bridge mapping
        /// </summary>
        [HttpDelete("UserMappings/{userId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult DeleteUserMapping(string userId)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return NotFound("Plugin configuration not available.");
            }

            var removed = config.UserMappings.RemoveAll(m => m.UserId == userId);
            if (removed == 0)
            {
                return NotFound("Mapping not found for the specified user.");
            }

            Plugin.Instance?.SaveConfiguration();

            return Ok(new { message = "Mapping deleted successfully." });
        }
    }

    public class HueRegistrationRequest
    {
        public string IpAddress { get; set; } = string.Empty;
    }

    public class HueRegistrationResult
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("clientKey")]
        public string ClientKey { get; set; } = string.Empty;
    }

    public class HueSyncStatus
    {
        public bool IsEnabled { get; set; }
        public bool IsSyncing { get; set; }
        public string? CurrentItem { get; set; }
        public string? BridgeIp { get; set; }
        public string? EntertainmentAreaId { get; set; }
    }

}
