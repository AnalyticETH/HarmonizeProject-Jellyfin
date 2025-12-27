using System;
using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using System.Threading.Tasks;
using Jellyfin.Plugin.Hue.Hue;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http; // Ensure this is available

namespace Jellyfin.Plugin.Hue.Api
{
    [ApiController]
    [Route("HueSync")] 
    [Authorize] // Require admin auth usually
    [Produces(MediaTypeNames.Application.Json)]
    public class HueApiController : ControllerBase
    {
        private readonly HueClient _hueClient;

        public HueApiController(HueClient hueClient)
        {
            _hueClient = hueClient;
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
}
