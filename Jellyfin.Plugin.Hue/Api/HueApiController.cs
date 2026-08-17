using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Mime;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Jellyfin.Plugin.Hue.Configuration;
using Jellyfin.Plugin.Hue.Hue;
using Jellyfin.Plugin.Hue.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

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
        private readonly IHueStreamTester? _streamTester;

        public HueApiController(HueClient hueClient, IEnumerable<Microsoft.Extensions.Hosting.IHostedService> hostedServices)
            : this(hueClient, hostedServices, null)
        {
        }

        [ActivatorUtilitiesConstructor]
        public HueApiController(
            HueClient hueClient,
            IEnumerable<Microsoft.Extensions.Hosting.IHostedService> hostedServices,
            IHueStreamTester? streamTester)
        {
            _hueClient = hueClient;
            _syncService = hostedServices.OfType<Service.HueSyncService>().FirstOrDefault();
            _streamTester = streamTester;
        }

        [HttpPost("Register")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<HueRegistrationResult>> RegisterBridge([FromBody] HueRegistrationRequest? request)
        {
            if (request == null || !HueBridgeCertificateValidation.IsValidBridgeAddress(request.IpAddress))
            {
                return BadRequest("A valid private bridge IP address or .local host name is required.");
            }

            var result = await _hueClient.RegisterWithBridge(request.IpAddress.Trim());
            if (result == null)
            {
                return BadRequest("Failed to register. Did you press the Link Button?");
            }

            return Ok(result);
        }

        /// <summary>
        /// Discovers the first Hue Bridge reported on the local network.
        /// </summary>
        [HttpGet("DiscoverBridge")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        public async Task<ActionResult<HueBridgeDiscoveryResult>> DiscoverBridge()
        {
            var ipAddress = await _hueClient.DiscoverBridgeIp();
            if (string.IsNullOrWhiteSpace(ipAddress))
            {
                return StatusCode(StatusCodes.Status502BadGateway, "No Hue Bridge was found on the local network.");
            }

            return Ok(new HueBridgeDiscoveryResult { IpAddress = ipAddress });
        }

        [HttpGet("EntertainmentAreas")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        public async Task<ActionResult<IEnumerable<HueClient.EntertainmentArea>>> GetEntertainmentAreas(
            [FromQuery(Name = "ip")] string? bridgeIp,
            [FromQuery(Name = "appKey")] string? appKey)
        {
            return await LoadEntertainmentAreas(bridgeIp, appKey);
        }

        /// <summary>
        /// Loads entertainment areas using a request body so app keys do not appear in URLs or access logs.
        /// </summary>
        [HttpPost("EntertainmentAreas")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        public async Task<ActionResult<IEnumerable<HueClient.EntertainmentArea>>> PostEntertainmentAreas(
            [FromBody] HueEntertainmentAreasRequest? request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.IpAddress) || string.IsNullOrWhiteSpace(request.AppKey))
            {
                return BadRequest("Bridge IP and app key are required before loading entertainment areas.");
            }

            return await LoadEntertainmentAreas(request.IpAddress, request.AppKey);
        }

        /// <summary>
        /// Loads the channel IDs exposed by one entertainment area so an administrator can
        /// build a per-user channel profile without inspecting the bridge API manually.
        /// </summary>
        [HttpPost("EntertainmentChannels")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        public async Task<ActionResult<IEnumerable<HueEntertainmentChannel>>> PostEntertainmentChannels(
            [FromBody] HueEntertainmentChannelsRequest? request)
        {
            if (request == null ||
                !HueBridgeCertificateValidation.IsValidBridgeAddress(request.IpAddress) ||
                string.IsNullOrWhiteSpace(request.AppKey) ||
                string.IsNullOrWhiteSpace(request.EntertainmentAreaId))
            {
                return BadRequest("A valid bridge address, app key, and entertainment area ID are required.");
            }

            var areaConfiguration = await _hueClient.GetEntertainmentConfiguration(
                request.IpAddress.Trim(),
                request.AppKey.Trim(),
                request.EntertainmentAreaId.Trim());
            if (areaConfiguration == null)
            {
                return StatusCode(StatusCodes.Status502BadGateway, "Could not load entertainment channels from the Hue bridge.");
            }

            if (!areaConfiguration.Value.TryGetProperty("channels", out var channels) ||
                channels.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return Ok(Array.Empty<HueEntertainmentChannel>());
            }

            var result = new List<HueEntertainmentChannel>();
            foreach (var channel in channels.EnumerateArray())
            {
                if (!channel.TryGetProperty("channel_id", out var channelIdProperty) ||
                    !channelIdProperty.TryGetInt32(out var channelId) ||
                    channelId < ushort.MinValue ||
                    channelId > ushort.MaxValue)
                {
                    continue;
                }

                var memberCount = channel.TryGetProperty("members", out var members) &&
                    members.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? members.GetArrayLength()
                    : 0;
                result.Add(new HueEntertainmentChannel
                {
                    ChannelId = channelId,
                    MemberCount = memberCount
                });
            }

            return Ok(result.OrderBy(channel => channel.ChannelId));
        }

        private async Task<ActionResult<IEnumerable<HueClient.EntertainmentArea>>> LoadEntertainmentAreas(
            string? bridgeIp,
            string? appKey)
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

        private static HashSet<int> GetValidChannelIds(System.Text.Json.JsonElement areaConfiguration)
        {
            var channelIds = new HashSet<int>();
            if (!areaConfiguration.TryGetProperty("channels", out var channels) ||
                channels.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return channelIds;
            }

            foreach (var channel in channels.EnumerateArray())
            {
                if (channel.TryGetProperty("channel_id", out var channelIdProperty) &&
                    channelIdProperty.TryGetInt32(out var channelId) &&
                    channelId >= ushort.MinValue &&
                    channelId <= ushort.MaxValue)
                {
                    channelIds.Add(channelId);
                }
            }

            return channelIds;
        }

        private static HueColorPresetResult ToColorPresetResult(HueColorPreset preset)
        {
            return new HueColorPresetResult
            {
                Name = preset.Name,
                Red = preset.Red,
                Green = preset.Green,
                Blue = preset.Blue,
                BrightnessPercent = preset.BrightnessPercent,
                DurationSeconds = preset.DurationSeconds
            };
        }

        /// <summary>
        /// Tests bridge reachability and, when supplied, verifies an entertainment area. A client key
        /// additionally opts into a short non-destructive DTLS stream probe. A channelIds profile can
        /// validate and limit that probe to the channels selected for a per-user mapping.
        /// </summary>
        [HttpPost("TestConnection")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        public async Task<ActionResult<HueConnectionTestResult>> TestConnection(
            [FromBody] HueConnectionTestRequest? request)
        {
            if (request == null ||
                !HueBridgeCertificateValidation.IsValidBridgeAddress(request.IpAddress) ||
                string.IsNullOrWhiteSpace(request.AppKey))
            {
                return BadRequest("A valid private bridge address and app key are required.");
            }

            HashSet<int>? requestedChannelIds = null;
            if (!string.IsNullOrWhiteSpace(request.ChannelIds))
            {
                if (!PluginConfiguration.TryParseChannelIds(request.ChannelIds, out var parsedChannelIds) ||
                    parsedChannelIds.Count == 0)
                {
                    return BadRequest("channelIds must be a comma-separated list of IDs from 0 to 65535.");
                }

                requestedChannelIds = parsedChannelIds;
            }

            var bridgeIp = request.IpAddress.Trim();
            var appKey = request.AppKey.Trim();
            var areas = await _hueClient.GetEntertainmentAreas(bridgeIp, appKey);
            if (areas == null)
            {
                return StatusCode(StatusCodes.Status502BadGateway, "Could not contact the Hue bridge with the supplied credentials.");
            }

            var areaId = request.EntertainmentAreaId?.Trim();
            var result = new HueConnectionTestResult
            {
                IsReachable = true,
                AreaCount = areas.Count,
                AreaId = string.IsNullOrWhiteSpace(areaId) ? null : areaId
            };

            if (string.IsNullOrWhiteSpace(areaId))
            {
                result.Message = $"Bridge reachable. Found {areas.Count} entertainment area(s).";
                return Ok(result);
            }

            var selectedArea = areas.FirstOrDefault(area => string.Equals(area.Id, areaId, StringComparison.OrdinalIgnoreCase));
            if (selectedArea == null)
            {
                result.AreaFound = false;
                result.Message = "Bridge reachable, but the selected entertainment area was not found.";
                return Ok(result);
            }

            var areaConfiguration = await _hueClient.GetEntertainmentConfiguration(bridgeIp, appKey, areaId);
            if (areaConfiguration == null)
            {
                result.AreaFound = false;
                result.Message = "Bridge reachable, but the selected entertainment area could not be loaded.";
                return Ok(result);
            }

            if (!areaConfiguration.Value.TryGetProperty("channels", out var channels) ||
                channels.ValueKind != System.Text.Json.JsonValueKind.Array ||
                channels.GetArrayLength() == 0)
            {
                result.AreaFound = false;
                result.Message = "Bridge reachable, but the selected entertainment area has no controllable channels.";
                return Ok(result);
            }

            result.AreaFound = true;
            result.AreaName = selectedArea.Name;
            var selectedChannelIds = requestedChannelIds;
            if (selectedChannelIds != null)
            {
                var availableChannelIds = GetValidChannelIds(areaConfiguration.Value);
                var missingChannelIds = selectedChannelIds
                    .Where(channelId => !availableChannelIds.Contains(channelId))
                    .OrderBy(channelId => channelId)
                    .ToArray();
                result.AvailableChannelCount = availableChannelIds.Count;
                result.SelectedChannelCount = selectedChannelIds.Count;
                result.ChannelProfileValid = missingChannelIds.Length == 0;
                result.MissingChannelIds = missingChannelIds.Length == 0
                    ? null
                    : string.Join(", ", missingChannelIds);
                if (missingChannelIds.Length > 0)
                {
                    result.StreamTested = false;
                    result.StreamMessage = $"The channel profile references IDs not present in this entertainment area: {result.MissingChannelIds}.";
                    result.Message = $"Bridge reachable and area '{selectedArea.Name}' is configured, but the channel profile is invalid. {result.StreamMessage}";
                    return Ok(result);
                }
            }
            if (!string.IsNullOrWhiteSpace(request.ClientKey) && _streamTester != null)
            {
                if (_syncService?.IsSyncing == true)
                {
                    result.StreamTested = false;
                    result.StreamMessage = "Stop active playback before running a DTLS stream probe.";
                    result.Message = $"Bridge reachable and area '{selectedArea.Name}' is configured, but the stream probe was skipped because playback sync is active.";
                }
                else
                {
                    result.StreamTested = true;
                    HueStreamProbeResult streamProbe;
                    try
                    {
                        streamProbe = await _streamTester.TestAsync(
                            bridgeIp,
                            appKey,
                            request.ClientKey.Trim(),
                            areaId,
                            areaConfiguration.Value,
                            selectedChannelIds);
                    }
                    catch
                    {
                        streamProbe = new HueStreamProbeResult
                        {
                            Succeeded = false,
                            Message = "The DTLS stream probe failed unexpectedly. Check the server log."
                        };
                    }
                    result.StreamReady = streamProbe.Succeeded;
                    result.StreamMessage = streamProbe.Message;
                    result.Message = streamProbe.Succeeded
                        ? $"Bridge reachable. Entertainment area '{selectedArea.Name}' and DTLS stream are ready."
                        : $"Bridge reachable and area '{selectedArea.Name}' is configured, but the DTLS stream probe failed: {streamProbe.Message}";
                }
            }
            else
            {
                result.Message = $"Bridge reachable. Entertainment area '{selectedArea.Name}' is ready.";
            }
            return Ok(result);
        }

        /// <summary>
        /// Displays a bounded solid-color preview through the configured entertainment
        /// area. The stream tester captures and restores the selected lights so this
        /// diagnostic never leaves a manual color behind.
        /// </summary>
        [HttpPost("Preview")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<ActionResult<HuePreviewResult>> Preview(
            [FromBody] HuePreviewRequest? request)
        {
            if (request == null ||
                !HueBridgeCertificateValidation.IsValidBridgeAddress(request.IpAddress) ||
                string.IsNullOrWhiteSpace(request.AppKey) ||
                string.IsNullOrWhiteSpace(request.ClientKey) ||
                string.IsNullOrWhiteSpace(request.EntertainmentAreaId))
            {
                return BadRequest("A valid bridge address, app key, client key, and entertainment area ID are required.");
            }

            if (request.Red < 0 || request.Red > 255 ||
                request.Green < 0 || request.Green > 255 ||
                request.Blue < 0 || request.Blue > 255)
            {
                return BadRequest("Preview RGB values must be between 0 and 255.");
            }

            if (request.BrightnessPercent < 0 || request.BrightnessPercent > 100)
            {
                return BadRequest("Preview brightness must be between 0 and 100 percent.");
            }

            if (request.DurationSeconds < HueStreamTester.MinPreviewDurationSeconds ||
                request.DurationSeconds > HueStreamTester.MaxPreviewDurationSeconds)
            {
                return BadRequest($"Preview duration must be between {HueStreamTester.MinPreviewDurationSeconds} and {HueStreamTester.MaxPreviewDurationSeconds} seconds.");
            }

            if (_streamTester == null)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Hue preview service is not available.");
            }

            if (_syncService?.IsSyncing == true)
            {
                return Conflict("Stop active playback before running a solid color preview.");
            }

            HashSet<int>? requestedChannelIds = null;
            if (!string.IsNullOrWhiteSpace(request.ChannelIds))
            {
                if (!PluginConfiguration.TryParseChannelIds(request.ChannelIds, out var parsedChannelIds) ||
                    parsedChannelIds.Count == 0)
                {
                    return BadRequest("channelIds must be a comma-separated list of IDs from 0 to 65535.");
                }

                requestedChannelIds = parsedChannelIds;
            }

            var bridgeIp = request.IpAddress.Trim();
            var appKey = request.AppKey.Trim();
            var clientKey = request.ClientKey.Trim();
            var areaId = request.EntertainmentAreaId.Trim();
            var areaConfiguration = await _hueClient.GetEntertainmentConfiguration(bridgeIp, appKey, areaId);
            if (areaConfiguration == null)
            {
                return StatusCode(StatusCodes.Status502BadGateway, "Could not load the selected entertainment area from the Hue bridge.");
            }

            var availableChannelIds = GetValidChannelIds(areaConfiguration.Value);
            if (availableChannelIds.Count == 0)
            {
                return BadRequest("The selected entertainment area has no controllable channels.");
            }

            if (requestedChannelIds != null)
            {
                var missingChannelIds = requestedChannelIds
                    .Where(channelId => !availableChannelIds.Contains(channelId))
                    .OrderBy(channelId => channelId)
                    .ToArray();
                if (missingChannelIds.Length > 0)
                {
                    return BadRequest(new
                    {
                        message = "The channel profile references IDs not present in this entertainment area.",
                        missingChannelIds = string.Join(", ", missingChannelIds)
                    });
                }
            }

            HueStreamProbeResult streamPreview;
            try
            {
                streamPreview = await _streamTester.PreviewAsync(
                    bridgeIp,
                    appKey,
                    clientKey,
                    areaId,
                    areaConfiguration.Value,
                    requestedChannelIds,
                    request.Red,
                    request.Green,
                    request.Blue,
                    request.BrightnessPercent,
                    request.DurationSeconds);
            }
            catch
            {
                streamPreview = new HueStreamProbeResult
                {
                    Succeeded = false,
                    Message = "The solid color preview failed unexpectedly. Check the server log."
                };
            }

            return Ok(new HuePreviewResult
            {
                Succeeded = streamPreview.Succeeded,
                Message = streamPreview.Message,
                Red = request.Red,
                Green = request.Green,
                Blue = request.Blue,
                BrightnessPercent = request.BrightnessPercent,
                DurationSeconds = request.DurationSeconds,
                AvailableChannelCount = availableChannelIds.Count,
                SelectedChannelCount = requestedChannelIds?.Count ?? availableChannelIds.Count
            });
        }

        /// <summary>
        /// Lists reusable solid-color preview scenes. Presets contain only visual
        /// values and never include bridge credentials or per-user targets.
        /// </summary>
        [HttpGet("ColorPresets")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<IEnumerable<HueColorPresetResult>> GetColorPresets()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return NotFound("Plugin configuration not available.");

            var presets = config.ColorPresets ?? new List<HueColorPreset>();
            return Ok(presets
                .Where(preset => preset != null)
                .OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase)
                .Select(ToColorPresetResult));
        }

        /// <summary>
        /// Saves or updates a reusable solid-color preview scene by case-insensitive name.
        /// </summary>
        [HttpPost("ColorPresets")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<HueColorPresetResult> SaveColorPreset(
            [FromBody] HueColorPresetRequest? request)
        {
            if (request == null)
                return BadRequest("Color preset is required.");

            var preset = request.ToConfigurationPreset();
            var validationErrors = PluginConfiguration.ValidateColorPreset(preset);
            if (validationErrors.Count > 0)
            {
                return BadRequest(new
                {
                    message = "Color preset is invalid.",
                    errors = validationErrors
                });
            }

            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
                return NotFound("Plugin configuration not available.");

            config.ColorPresets ??= new List<HueColorPreset>();
            var existingIndex = config.ColorPresets.FindIndex(existing =>
                existing != null &&
                string.Equals(existing.Name?.Trim(), preset.Name, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                config.ColorPresets[existingIndex] = preset;
            }
            else
            {
                if (config.ColorPresets.Count >= PluginConfiguration.MaxColorPresets)
                {
                    return BadRequest(new
                    {
                        message = $"No more than {PluginConfiguration.MaxColorPresets} color presets may be saved.",
                        errors = new[] { $"No more than {PluginConfiguration.MaxColorPresets} color presets may be saved" }
                    });
                }

                config.ColorPresets.Add(preset);
            }

            plugin.SaveConfiguration();
            return Ok(ToColorPresetResult(preset));
        }

        /// <summary>
        /// Deletes one reusable solid-color preview scene by name.
        /// </summary>
        [HttpDelete("ColorPresets/{name}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult DeleteColorPreset(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return NotFound("Color preset not found.");

            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return NotFound("Plugin configuration not available.");

            config.ColorPresets ??= new List<HueColorPreset>();
            var removed = config.ColorPresets.RemoveAll(existing =>
                existing != null &&
                string.Equals(existing.Name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
                return NotFound("Color preset not found.");

            Plugin.Instance?.SaveConfiguration();
            return Ok(new { message = "Color preset deleted successfully." });
        }

        [HttpGet("Status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<HueSyncStatus> GetStatus()
        {
            var config = Plugin.Instance?.Configuration;
            var runtime = _syncService?.GetRuntimeStatus();
            var status = new HueSyncStatus
            {
                IsEnabled = config?.SyncEnabled ?? false,
                ServiceAvailable = _syncService != null,
                IsSyncing = runtime?.IsSyncing ?? false,
                CurrentItem = runtime?.CurrentItem,
                BridgeIp = config?.HueBridgeIp,
                EntertainmentAreaId = config?.EntertainmentAreaId,
                State = runtime?.State ?? "Unavailable",
                StatusMessage = runtime?.Message ?? "Sync service is not available.",
                LastError = runtime?.LastError,
                ActiveBridgeIp = runtime?.ActiveBridgeIp,
                ActiveEntertainmentAreaId = runtime?.ActiveEntertainmentAreaId,
                ActiveTargetFps = runtime?.ActiveTargetFps,
                ActiveFrameResolution = runtime?.ActiveFrameResolution,
                ActiveVideoScalingMode = runtime?.ActiveVideoScalingMode,
                ActiveVideoDeinterlaceMode = runtime?.ActiveVideoDeinterlaceMode,
                ActiveSamplingBreadthPercent = runtime?.ActiveSamplingBreadthPercent,
                ActiveSamplingMode = runtime?.ActiveSamplingMode,
                ActiveColorSmoothingPercent = runtime?.ActiveColorSmoothingPercent,
                ActiveBrightnessBoost = runtime?.ActiveBrightnessBoost,
                ActiveRedGain = runtime?.ActiveRedGain,
                ActiveGreenGain = runtime?.ActiveGreenGain,
                ActiveBlueGain = runtime?.ActiveBlueGain,
                ActiveColorSaturation = runtime?.ActiveColorSaturation,
                ActiveHueShiftDegrees = runtime?.ActiveHueShiftDegrees,
                ActiveOutputBrightnessPercent = runtime?.ActiveOutputBrightnessPercent,
                ActiveBlackoutThreshold = runtime?.ActiveBlackoutThreshold,
                ActiveColorChangeThreshold = runtime?.ActiveColorChangeThreshold,
                ActiveUseGpu = runtime?.ActiveUseGpu,
                ActiveCustomFfmpegFlagsConfigured = runtime?.ActiveCustomFfmpegFlagsConfigured,
                ActiveFfmpegStallTimeoutSeconds = runtime?.ActiveFfmpegStallTimeoutSeconds,
                ActiveNetworkRetryAttempts = runtime?.ActiveNetworkRetryAttempts,
                ActiveChannelIds = runtime?.ActiveChannelIds,
                ActiveRestoreLightState = runtime?.ActiveRestoreLightState,
                FramesProcessed = runtime?.FramesProcessed ?? 0,
                CanStopSync = runtime?.CanStopSync ?? false,
                IsFfmpegHealthy = runtime?.IsFfmpegHealthy ?? false,
                IsDtlsHealthy = runtime?.IsDtlsHealthy ?? false,
                SyncDurationSeconds = runtime?.SyncDurationSeconds,
                SyncStartedAtUtc = runtime?.SyncStartedAtUtc
            };

            return Ok(status);
        }

        /// <summary>
        /// Stops Hue output for the current playback session without stopping Jellyfin playback.
        /// </summary>
        [HttpPost("Stop")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<ActionResult> StopSync()
        {
            if (_syncService == null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Hue sync service is not available.");

            if (!await _syncService.StopCurrentSyncAsync())
                return Conflict("There is no active playback sync session to stop.");

            return Ok(new { message = "Hue sync stopped; playback continues." });
        }

        /// <summary>
        /// Gets the settings used by the configuration page without serializing the
        /// per-user mappings. Mapping credentials are managed only through the
        /// dedicated mapping endpoints below.
        /// </summary>
        [HttpGet("Configuration")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<HuePluginConfigurationSettings> GetConfiguration()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return NotFound("Plugin configuration not available.");
            }

            return Ok(HuePluginConfigurationSettings.From(config));
        }

        /// <summary>
        /// Updates the settings used by the configuration page while leaving
        /// per-user mappings (and their stored credentials) untouched.
        /// </summary>
        [HttpPost("Configuration")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<HuePluginConfigurationSettings> SaveConfiguration(
            [FromBody] HuePluginConfigurationSettings? settings)
        {
            if (settings == null)
            {
                return BadRequest("Configuration is required.");
            }

            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
            {
                return NotFound("Plugin configuration not available.");
            }

            var previousSettings = HuePluginConfigurationSettings.From(config);
            settings.ApplyTo(config);
            var validationErrors = config.Validate();
            if (validationErrors.Count > 0)
            {
                previousSettings.ApplyTo(config);
                return BadRequest(new
                {
                    message = "Configuration is invalid.",
                    errors = validationErrors
                });
            }

            plugin.SaveConfiguration();
            return Ok(HuePluginConfigurationSettings.From(config));
        }

        /// <summary>
        /// Gets all user-to-bridge mappings without returning stored credentials. Optional
        /// per-user playback, color-threshold, performance, execution, channel, and restoration profile values are included because they are not secret.
        /// </summary>
        [HttpGet("UserMappings")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<IEnumerable<UserBridgeMappingSummary>> GetUserMappings()
        {
            var config = Plugin.Instance?.Configuration;
            var mappings = config?.UserMappings?
                .Select(UserBridgeMappingSummary.From)
                ?? Enumerable.Empty<UserBridgeMappingSummary>();
            return Ok(mappings);
        }

        /// <summary>
        /// Saves or updates a user-to-bridge mapping. A mapping can opt a user out of
        /// synchronization without storing bridge credentials and can override playback, color processing and scene thresholds,
        /// capture-performance, execution, channel selection, or light-restoration settings.
        /// </summary>
        [HttpPost("UserMappings")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public ActionResult SaveUserMapping([FromBody] UserBridgeMapping mapping)
        {
            if (mapping == null)
            {
                return BadRequest("Mapping is required.");
            }

            if (string.IsNullOrWhiteSpace(mapping.UserId))
            {
                return BadRequest("User ID is required.");
            }

            var overrideLabel = string.IsNullOrWhiteSpace(mapping.UserName)
                ? "User mapping"
                : $"User mapping for '{mapping.UserName.Trim()}'";
            var playbackOverrideErrors = PluginConfiguration.ValidatePlaybackOverrides(mapping, overrideLabel);
            var colorOverrideErrors = PluginConfiguration.ValidateColorOverrides(mapping, overrideLabel);
            var performanceOverrideErrors = PluginConfiguration.ValidatePerformanceOverrides(mapping, overrideLabel);
            var executionOverrideErrors = PluginConfiguration.ValidateExecutionOverrides(mapping, overrideLabel);
            var channelOverrideErrors = PluginConfiguration.ValidateChannelOverrides(mapping, overrideLabel);
            var overrideErrors = new List<string>(playbackOverrideErrors);
            overrideErrors.AddRange(colorOverrideErrors);
            overrideErrors.AddRange(performanceOverrideErrors);
            overrideErrors.AddRange(executionOverrideErrors);
            overrideErrors.AddRange(channelOverrideErrors);
            if (overrideErrors.Count > 0)
            {
                return BadRequest(new
                {
                    message = channelOverrideErrors.Count > 0
                        ? "User channel profile is invalid."
                        : executionOverrideErrors.Count > 0
                            ? "User execution profile is invalid."
                            : performanceOverrideErrors.Count > 0
                                ? "User performance profile is invalid."
                                : playbackOverrideErrors.Count > 0
                                    ? "User profile overrides are invalid."
                                    : "User color profile is invalid.",
                    errors = overrideErrors
                });
            }

            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
            {
                return BadRequest("Plugin configuration not available.");
            }

            config.UserMappings ??= new List<UserBridgeMapping>();
            var existingMapping = config.UserMappings.FirstOrDefault(existing =>
                string.Equals(existing.UserId, mapping.UserId, StringComparison.OrdinalIgnoreCase));

            // The edit form deliberately leaves secret fields blank. Preserve an
            // existing credential unless the caller supplied a replacement value.
            if (mapping.SyncEnabled && existingMapping != null)
            {
                if (string.IsNullOrWhiteSpace(mapping.HueAppKey))
                {
                    mapping.HueAppKey = existingMapping.HueAppKey;
                }

                if (string.IsNullOrWhiteSpace(mapping.HueClientKey))
                {
                    mapping.HueClientKey = existingMapping.HueClientKey;
                }
            }

            if (mapping.SyncEnabled && !HueBridgeCertificateValidation.IsValidBridgeAddress(mapping.HueBridgeIp))
            {
                return BadRequest("A valid private bridge IP address or .local host name is required.");
            }

            if (mapping.SyncEnabled &&
                (string.IsNullOrWhiteSpace(mapping.HueAppKey) ||
                 string.IsNullOrWhiteSpace(mapping.HueClientKey) ||
                 string.IsNullOrWhiteSpace(mapping.EntertainmentAreaId)))
            {
                return BadRequest("Bridge credentials and entertainment area ID are required.");
            }

            if (!mapping.SyncEnabled)
            {
                // A disabled mapping is only a per-user opt-out. Do not retain stale
                // bridge credentials or an area that will never be used.
                mapping.HueBridgeIp = string.Empty;
                mapping.HueAppKey = string.Empty;
                mapping.HueClientKey = string.Empty;
                mapping.EntertainmentAreaId = string.Empty;
                mapping.EntertainmentAreaName = string.Empty;
            }

            // Remove existing mapping for this user if exists
            config.UserMappings.RemoveAll(m => string.Equals(m.UserId, mapping.UserId, StringComparison.OrdinalIgnoreCase));

            // Add the new/updated mapping
            config.UserMappings.Add(mapping);

            plugin.SaveConfiguration();

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
            if (string.IsNullOrWhiteSpace(userId))
            {
                return BadRequest("User ID is required.");
            }

            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return NotFound("Plugin configuration not available.");
            }

            config.UserMappings ??= new List<UserBridgeMapping>();
            var removed = config.UserMappings.RemoveAll(m => string.Equals(m.UserId, userId, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                return NotFound("Mapping not found for the specified user.");
            }

            Plugin.Instance?.SaveConfiguration();

            return Ok(new { message = "Mapping deleted successfully." });
        }

    }

    /// <summary>
    /// Configuration-page settings. This intentionally excludes PluginConfiguration.UserMappings
    /// so the generic settings flow cannot round-trip per-user bridge credentials through a browser.
    /// </summary>
    public sealed class HuePluginConfigurationSettings
    {
        public bool SyncEnabled { get; set; }
        public string HueBridgeIp { get; set; } = string.Empty;
        public string HueAppKey { get; set; } = string.Empty;
        public string HueClientKey { get; set; } = string.Empty;
        public string EntertainmentAreaId { get; set; } = string.Empty;
        public string ChannelIds { get; set; } = string.Empty;
        public bool UseCinemaMode { get; set; } = true;
        public int BrightnessDimLevel { get; set; } = 30;
        public string PauseBehavior { get; set; } = PluginConfiguration.PauseBehaviorKeepLastColors;
        public int TargetFps { get; set; } = 20;
        public string FrameResolution { get; set; } = PluginConfiguration.FrameResolutionStandard;
        public string VideoScalingMode { get; set; } = PluginConfiguration.VideoScalingModeStretch;
        public string VideoDeinterlaceMode { get; set; } = PluginConfiguration.VideoDeinterlaceModeOff;
        public int SamplingBreadthPercent { get; set; } = 15;
        public string SamplingMode { get; set; } = PluginConfiguration.SamplingModeAverage;
        public int ColorSmoothingPercent { get; set; } = 0;
        public bool UseGpu { get; set; } = true;
        public string CustomFfmpegFlags { get; set; } = string.Empty;
        public int FfmpegStallTimeoutSeconds { get; set; } = 5;
        public bool RestoreLightState { get; set; } = true;
        public int BrightnessBoost { get; set; } = 100;
        public int RedGain { get; set; } = 100;
        public int GreenGain { get; set; } = 100;
        public int BlueGain { get; set; } = 100;
        public int ColorSaturation { get; set; } = 100;
        public int HueShiftDegrees { get; set; } = 0;
        public int OutputBrightnessPercent { get; set; } = 100;
        public int BlackoutThreshold { get; set; } = 15;
        public int ColorChangeThreshold { get; set; } = 10;
        public int NetworkRetryAttempts { get; set; } = 3;

        public static HuePluginConfigurationSettings From(PluginConfiguration config)
        {
            return new HuePluginConfigurationSettings
            {
                SyncEnabled = config.SyncEnabled,
                HueBridgeIp = config.HueBridgeIp,
                HueAppKey = config.HueAppKey,
                HueClientKey = config.HueClientKey,
                EntertainmentAreaId = config.EntertainmentAreaId,
                ChannelIds = config.ChannelIds,
                UseCinemaMode = config.UseCinemaMode,
                BrightnessDimLevel = config.BrightnessDimLevel,
                PauseBehavior = config.PauseBehavior,
                TargetFps = config.TargetFps,
                FrameResolution = config.FrameResolution,
                VideoScalingMode = config.VideoScalingMode,
                VideoDeinterlaceMode = config.VideoDeinterlaceMode,
                SamplingBreadthPercent = config.SamplingBreadthPercent,
                SamplingMode = config.SamplingMode,
                ColorSmoothingPercent = config.ColorSmoothingPercent,
                UseGpu = config.UseGpu,
                CustomFfmpegFlags = config.CustomFfmpegFlags,
                FfmpegStallTimeoutSeconds = config.FfmpegStallTimeoutSeconds,
                RestoreLightState = config.RestoreLightState,
                BrightnessBoost = config.BrightnessBoost,
                RedGain = config.RedGain,
                GreenGain = config.GreenGain,
                BlueGain = config.BlueGain,
                ColorSaturation = config.ColorSaturation,
                HueShiftDegrees = config.HueShiftDegrees,
                OutputBrightnessPercent = config.OutputBrightnessPercent,
                BlackoutThreshold = config.BlackoutThreshold,
                ColorChangeThreshold = config.ColorChangeThreshold,
                NetworkRetryAttempts = config.NetworkRetryAttempts
            };
        }

        public void ApplyTo(PluginConfiguration config)
        {
            config.SyncEnabled = SyncEnabled;
            config.HueBridgeIp = HueBridgeIp?.Trim() ?? string.Empty;
            config.HueAppKey = HueAppKey?.Trim() ?? string.Empty;
            config.HueClientKey = HueClientKey?.Trim() ?? string.Empty;
            config.EntertainmentAreaId = EntertainmentAreaId?.Trim() ?? string.Empty;
            config.ChannelIds = ChannelIds?.Trim() ?? string.Empty;
            config.UseCinemaMode = UseCinemaMode;
            config.BrightnessDimLevel = BrightnessDimLevel;
            config.PauseBehavior = PauseBehavior?.Trim() ?? PluginConfiguration.PauseBehaviorKeepLastColors;
            config.TargetFps = TargetFps;
            config.FrameResolution = FrameResolution?.Trim() ?? PluginConfiguration.FrameResolutionStandard;
            config.VideoScalingMode = VideoScalingMode?.Trim() ?? PluginConfiguration.VideoScalingModeStretch;
            config.VideoDeinterlaceMode = VideoDeinterlaceMode?.Trim() ?? PluginConfiguration.VideoDeinterlaceModeOff;
            config.SamplingBreadthPercent = SamplingBreadthPercent;
            config.SamplingMode = SamplingMode?.Trim() ?? PluginConfiguration.SamplingModeAverage;
            config.ColorSmoothingPercent = ColorSmoothingPercent;
            config.UseGpu = UseGpu;
            config.CustomFfmpegFlags = CustomFfmpegFlags ?? string.Empty;
            config.FfmpegStallTimeoutSeconds = FfmpegStallTimeoutSeconds;
            config.RestoreLightState = RestoreLightState;
            config.BrightnessBoost = BrightnessBoost;
            config.RedGain = RedGain;
            config.GreenGain = GreenGain;
            config.BlueGain = BlueGain;
            config.ColorSaturation = ColorSaturation;
            config.HueShiftDegrees = HueShiftDegrees;
            config.OutputBrightnessPercent = OutputBrightnessPercent;
            config.BlackoutThreshold = BlackoutThreshold;
            config.ColorChangeThreshold = ColorChangeThreshold;
            config.NetworkRetryAttempts = NetworkRetryAttempts;
        }
    }

    /// <summary>
    /// Non-secret representation of a per-user bridge mapping, playback, color, performance,
    /// execution, channel, and restoration profiles.
    /// </summary>
    public sealed class UserBridgeMappingSummary
    {
        public string UserId { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public bool SyncEnabled { get; set; }
        public string HueBridgeIp { get; set; } = string.Empty;
        public string EntertainmentAreaId { get; set; } = string.Empty;
        public string EntertainmentAreaName { get; set; } = string.Empty;
        public bool HasAppKey { get; set; }
        public bool HasClientKey { get; set; }
        public bool? UseCinemaModeOverride { get; set; }
        public int? BrightnessDimLevelOverride { get; set; }
        public string? PauseBehaviorOverride { get; set; }
        public bool? RestoreLightStateOverride { get; set; }
        public int? BrightnessBoostOverride { get; set; }
        public int? RedGainOverride { get; set; }
        public int? GreenGainOverride { get; set; }
        public int? BlueGainOverride { get; set; }
        public int? ColorSaturationOverride { get; set; }
        public int? HueShiftDegreesOverride { get; set; }
        public int? OutputBrightnessPercentOverride { get; set; }
        public int? BlackoutThresholdOverride { get; set; }
        public int? ColorChangeThresholdOverride { get; set; }
        public bool? UseGpuOverride { get; set; }
        public string? CustomFfmpegFlagsOverride { get; set; }
        public int? FfmpegStallTimeoutSecondsOverride { get; set; }
        public int? NetworkRetryAttemptsOverride { get; set; }
        public string? ChannelIdsOverride { get; set; }
        public int? TargetFpsOverride { get; set; }
        public string? FrameResolutionOverride { get; set; }
        public string? VideoScalingModeOverride { get; set; }
        public string? VideoDeinterlaceModeOverride { get; set; }
        public int? SamplingBreadthPercentOverride { get; set; }
        public string? SamplingModeOverride { get; set; }
        public int? ColorSmoothingPercentOverride { get; set; }

        public static UserBridgeMappingSummary From(UserBridgeMapping mapping)
        {
            return new UserBridgeMappingSummary
            {
                UserId = mapping.UserId,
                UserName = mapping.UserName,
                SyncEnabled = mapping.SyncEnabled,
                HueBridgeIp = mapping.HueBridgeIp,
                EntertainmentAreaId = mapping.EntertainmentAreaId,
                EntertainmentAreaName = mapping.EntertainmentAreaName,
                HasAppKey = !string.IsNullOrWhiteSpace(mapping.HueAppKey),
                HasClientKey = !string.IsNullOrWhiteSpace(mapping.HueClientKey),
                UseCinemaModeOverride = mapping.UseCinemaModeOverride,
                BrightnessDimLevelOverride = mapping.BrightnessDimLevelOverride,
                PauseBehaviorOverride = mapping.PauseBehaviorOverride,
                RestoreLightStateOverride = mapping.RestoreLightStateOverride,
                BrightnessBoostOverride = mapping.BrightnessBoostOverride,
                RedGainOverride = mapping.RedGainOverride,
                GreenGainOverride = mapping.GreenGainOverride,
                BlueGainOverride = mapping.BlueGainOverride,
                ColorSaturationOverride = mapping.ColorSaturationOverride,
                HueShiftDegreesOverride = mapping.HueShiftDegreesOverride,
                OutputBrightnessPercentOverride = mapping.OutputBrightnessPercentOverride,
                BlackoutThresholdOverride = mapping.BlackoutThresholdOverride,
                ColorChangeThresholdOverride = mapping.ColorChangeThresholdOverride,
                UseGpuOverride = mapping.UseGpuOverride,
                CustomFfmpegFlagsOverride = mapping.CustomFfmpegFlagsOverride,
                FfmpegStallTimeoutSecondsOverride = mapping.FfmpegStallTimeoutSecondsOverride,
                NetworkRetryAttemptsOverride = mapping.NetworkRetryAttemptsOverride,
                ChannelIdsOverride = mapping.ChannelIdsOverride,
                TargetFpsOverride = mapping.TargetFpsOverride,
                FrameResolutionOverride = mapping.FrameResolutionOverride,
                VideoScalingModeOverride = mapping.VideoScalingModeOverride,
                VideoDeinterlaceModeOverride = mapping.VideoDeinterlaceModeOverride,
                SamplingBreadthPercentOverride = mapping.SamplingBreadthPercentOverride,
                SamplingModeOverride = mapping.SamplingModeOverride,
                ColorSmoothingPercentOverride = mapping.ColorSmoothingPercentOverride
            };
        }
    }

    public class HueRegistrationRequest
    {
        public string IpAddress { get; set; } = string.Empty;
    }

    public class HueEntertainmentAreasRequest
    {
        [JsonPropertyName("ipAddress")]
        public string IpAddress { get; set; } = string.Empty;

        [JsonPropertyName("appKey")]
        public string AppKey { get; set; } = string.Empty;
    }

    public class HueEntertainmentChannelsRequest
    {
        [JsonPropertyName("ipAddress")]
        public string IpAddress { get; set; } = string.Empty;

        [JsonPropertyName("appKey")]
        public string AppKey { get; set; } = string.Empty;

        [JsonPropertyName("entertainmentAreaId")]
        public string EntertainmentAreaId { get; set; } = string.Empty;
    }

    public class HueEntertainmentChannel
    {
        [JsonPropertyName("channelId")]
        public int ChannelId { get; set; }

        [JsonPropertyName("memberCount")]
        public int MemberCount { get; set; }
    }

    public class HueConnectionTestRequest
    {
        [JsonPropertyName("ipAddress")]
        public string IpAddress { get; set; } = string.Empty;

        [JsonPropertyName("appKey")]
        public string AppKey { get; set; } = string.Empty;

        [JsonPropertyName("clientKey")]
        public string? ClientKey { get; set; }

        [JsonPropertyName("entertainmentAreaId")]
        public string? EntertainmentAreaId { get; set; }

        [JsonPropertyName("channelIds")]
        public string? ChannelIds { get; set; }
    }

    public class HuePreviewRequest
    {
        [JsonPropertyName("ipAddress")]
        public string IpAddress { get; set; } = string.Empty;

        [JsonPropertyName("appKey")]
        public string AppKey { get; set; } = string.Empty;

        [JsonPropertyName("clientKey")]
        public string ClientKey { get; set; } = string.Empty;

        [JsonPropertyName("entertainmentAreaId")]
        public string EntertainmentAreaId { get; set; } = string.Empty;

        [JsonPropertyName("channelIds")]
        public string? ChannelIds { get; set; }

        [JsonPropertyName("red")]
        public int Red { get; set; } = 255;

        [JsonPropertyName("green")]
        public int Green { get; set; } = 255;

        [JsonPropertyName("blue")]
        public int Blue { get; set; } = 255;

        [JsonPropertyName("brightnessPercent")]
        public int BrightnessPercent { get; set; } = 100;

        [JsonPropertyName("durationSeconds")]
        public int DurationSeconds { get; set; } = 5;
    }

    public class HueConnectionTestResult
    {
        [JsonPropertyName("isReachable")]
        public bool IsReachable { get; set; }

        [JsonPropertyName("areaCount")]
        public int AreaCount { get; set; }

        [JsonPropertyName("areaId")]
        public string? AreaId { get; set; }

        [JsonPropertyName("areaFound")]
        public bool? AreaFound { get; set; }

        [JsonPropertyName("areaName")]
        public string? AreaName { get; set; }

        [JsonPropertyName("streamTested")]
        public bool? StreamTested { get; set; }

        [JsonPropertyName("streamReady")]
        public bool? StreamReady { get; set; }

        [JsonPropertyName("streamMessage")]
        public string? StreamMessage { get; set; }

        [JsonPropertyName("availableChannelCount")]
        public int? AvailableChannelCount { get; set; }

        [JsonPropertyName("selectedChannelCount")]
        public int? SelectedChannelCount { get; set; }

        [JsonPropertyName("channelProfileValid")]
        public bool? ChannelProfileValid { get; set; }

        [JsonPropertyName("missingChannelIds")]
        public string? MissingChannelIds { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    public class HuePreviewResult
    {
        [JsonPropertyName("succeeded")]
        public bool Succeeded { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("red")]
        public int Red { get; set; }

        [JsonPropertyName("green")]
        public int Green { get; set; }

        [JsonPropertyName("blue")]
        public int Blue { get; set; }

        [JsonPropertyName("brightnessPercent")]
        public int BrightnessPercent { get; set; }

        [JsonPropertyName("durationSeconds")]
        public int DurationSeconds { get; set; }

        [JsonPropertyName("availableChannelCount")]
        public int AvailableChannelCount { get; set; }

        [JsonPropertyName("selectedChannelCount")]
        public int SelectedChannelCount { get; set; }
    }

    public class HueColorPresetRequest
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("red")]
        public int Red { get; set; }

        [JsonPropertyName("green")]
        public int Green { get; set; }

        [JsonPropertyName("blue")]
        public int Blue { get; set; }

        [JsonPropertyName("brightnessPercent")]
        public int BrightnessPercent { get; set; } = 100;

        [JsonPropertyName("durationSeconds")]
        public int DurationSeconds { get; set; } = 5;

        public HueColorPreset ToConfigurationPreset()
        {
            return new HueColorPreset
            {
                Name = Name?.Trim() ?? string.Empty,
                Red = Red,
                Green = Green,
                Blue = Blue,
                BrightnessPercent = BrightnessPercent,
                DurationSeconds = DurationSeconds
            };
        }
    }

    public class HueColorPresetResult
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("red")]
        public int Red { get; set; }

        [JsonPropertyName("green")]
        public int Green { get; set; }

        [JsonPropertyName("blue")]
        public int Blue { get; set; }

        [JsonPropertyName("brightnessPercent")]
        public int BrightnessPercent { get; set; }

        [JsonPropertyName("durationSeconds")]
        public int DurationSeconds { get; set; }
    }

    public class HueRegistrationResult
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("clientKey")]
        public string ClientKey { get; set; } = string.Empty;
    }

    public class HueBridgeDiscoveryResult
    {
        [JsonPropertyName("ipAddress")]
        public string IpAddress { get; set; } = string.Empty;
    }

    public class HueSyncStatus
    {
        public bool IsEnabled { get; set; }
        public bool ServiceAvailable { get; set; }
        public bool IsSyncing { get; set; }
        public string? CurrentItem { get; set; }
        public string? BridgeIp { get; set; }
        public string? EntertainmentAreaId { get; set; }
        public string State { get; set; } = "Unavailable";
        public string StatusMessage { get; set; } = string.Empty;
        public string? LastError { get; set; }
        public string? ActiveBridgeIp { get; set; }
        public string? ActiveEntertainmentAreaId { get; set; }
        public int? ActiveTargetFps { get; set; }
        public string? ActiveFrameResolution { get; set; }
        public string? ActiveVideoScalingMode { get; set; }
        public string? ActiveVideoDeinterlaceMode { get; set; }
        public int? ActiveSamplingBreadthPercent { get; set; }
        public string? ActiveSamplingMode { get; set; }
        public int? ActiveColorSmoothingPercent { get; set; }
        public int? ActiveBrightnessBoost { get; set; }
        public int? ActiveRedGain { get; set; }
        public int? ActiveGreenGain { get; set; }
        public int? ActiveBlueGain { get; set; }
        public int? ActiveColorSaturation { get; set; }
        public int? ActiveHueShiftDegrees { get; set; }
        public int? ActiveOutputBrightnessPercent { get; set; }
        public int? ActiveBlackoutThreshold { get; set; }
        public int? ActiveColorChangeThreshold { get; set; }
        public bool? ActiveUseGpu { get; set; }
        public bool? ActiveCustomFfmpegFlagsConfigured { get; set; }
        public int? ActiveFfmpegStallTimeoutSeconds { get; set; }
        public int? ActiveNetworkRetryAttempts { get; set; }
        public string? ActiveChannelIds { get; set; }
        public bool? ActiveRestoreLightState { get; set; }
        public long FramesProcessed { get; set; }
        public bool CanStopSync { get; set; }
        public bool IsFfmpegHealthy { get; set; }
        public bool IsDtlsHealthy { get; set; }
        public double? SyncDurationSeconds { get; set; }
        public DateTime? SyncStartedAtUtc { get; set; }
    }

}
