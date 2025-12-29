using System;
using System.Net.Mime;
using System.Threading.Tasks;
using Jellyfin.Plugin.Hue.Hue;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Hue.Api
{
    [ApiController]
    [Route("HueSync")]
    [Authorize] // Require admin auth usually
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
    }

    public class HueRegistrationRequest
    {
        public string IpAddress { get; set; } = string.Empty;
    }

    public class HueRegistrationResult
    {
        public string Username { get; set; } = string.Empty;
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
