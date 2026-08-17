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

        /// <summary>
        /// Tests bridge reachability and, when supplied, verifies an entertainment area. A client key
        /// additionally opts into a short non-destructive DTLS stream probe.
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
                            areaConfiguration.Value);
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
        /// per-user playback and color profile values are included because they are not secret.
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
        /// synchronization without storing bridge credentials and can override playback or color processing.
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
            var overrideErrors = new List<string>(playbackOverrideErrors);
            overrideErrors.AddRange(colorOverrideErrors);
            if (overrideErrors.Count > 0)
            {
                return BadRequest(new
                {
                    message = playbackOverrideErrors.Count > 0
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
            config.ColorSaturation = ColorSaturation;
            config.HueShiftDegrees = HueShiftDegrees;
            config.OutputBrightnessPercent = OutputBrightnessPercent;
            config.BlackoutThreshold = BlackoutThreshold;
            config.ColorChangeThreshold = ColorChangeThreshold;
            config.NetworkRetryAttempts = NetworkRetryAttempts;
        }
    }

    /// <summary>
    /// Non-secret representation of a per-user bridge mapping, playback profile, and color profile.
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
        public int? BrightnessBoostOverride { get; set; }
        public int? ColorSaturationOverride { get; set; }
        public int? HueShiftDegreesOverride { get; set; }
        public int? OutputBrightnessPercentOverride { get; set; }

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
                BrightnessBoostOverride = mapping.BrightnessBoostOverride,
                ColorSaturationOverride = mapping.ColorSaturationOverride,
                HueShiftDegreesOverride = mapping.HueShiftDegreesOverride,
                OutputBrightnessPercentOverride = mapping.OutputBrightnessPercentOverride
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

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
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
        public long FramesProcessed { get; set; }
        public bool CanStopSync { get; set; }
        public bool IsFfmpegHealthy { get; set; }
        public bool IsDtlsHealthy { get; set; }
        public double? SyncDurationSeconds { get; set; }
        public DateTime? SyncStartedAtUtc { get; set; }
    }

}
