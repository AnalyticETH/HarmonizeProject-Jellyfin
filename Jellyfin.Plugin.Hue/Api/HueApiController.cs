using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Mime;
using System.Text.Json.Serialization;
using System.Threading;
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
        private readonly HueBridgeLifecycleGate _bridgeLifecycleGate;
        private readonly IHueEnvironmentProbe _environmentProbe;

        public HueApiController(HueClient hueClient, IEnumerable<Microsoft.Extensions.Hosting.IHostedService> hostedServices)
            : this(hueClient, hostedServices, null, null, null)
        {
        }

        [ActivatorUtilitiesConstructor]
        public HueApiController(
            HueClient hueClient,
            IEnumerable<Microsoft.Extensions.Hosting.IHostedService> hostedServices,
            IHueStreamTester? streamTester,
            HueBridgeLifecycleGate? bridgeLifecycleGate = null,
            IHueEnvironmentProbe? environmentProbe = null)
        {
            _hueClient = hueClient;
            _syncService = hostedServices.OfType<Service.HueSyncService>().FirstOrDefault();
            _streamTester = streamTester;
            _bridgeLifecycleGate = bridgeLifecycleGate ?? new HueBridgeLifecycleGate();
            _environmentProbe = environmentProbe ?? new HueEnvironmentProbe();
        }

        /// <summary>
        /// Resolves credentials omitted by the configuration page only when the requested
        /// bridge is the configured global target. This lets the page keep global keys out
        /// of its JSON state without allowing a blank key to authorize an arbitrary host.
        /// </summary>
        private static bool TryResolveGlobalCredentials(
            string? requestedBridgeIp,
            string? requestedAppKey,
            string? requestedClientKey,
            bool allowStoredClientKey,
            out string bridgeIp,
            out string appKey,
            out string clientKey)
        {
            var config = Plugin.Instance?.Configuration;
            bridgeIp = requestedBridgeIp?.Trim() ?? string.Empty;
            appKey = requestedAppKey?.Trim() ?? string.Empty;
            clientKey = requestedClientKey?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(bridgeIp))
                bridgeIp = config?.HueBridgeIp?.Trim() ?? string.Empty;

            if (config == null || !IsSameBridgeTarget(bridgeIp, config.HueBridgeIp))
                return !string.IsNullOrWhiteSpace(bridgeIp) && !string.IsNullOrWhiteSpace(appKey);

            if (string.IsNullOrWhiteSpace(appKey))
                appKey = config.HueAppKey?.Trim() ?? string.Empty;

            if (allowStoredClientKey && string.IsNullOrWhiteSpace(clientKey))
                clientKey = config.HueClientKey?.Trim() ?? string.Empty;

            return !string.IsNullOrWhiteSpace(bridgeIp) && !string.IsNullOrWhiteSpace(appKey);
        }

        private static bool IsSameBridgeTarget(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            var leftHost = left.Trim();
            var rightHost = right.Trim();
            if (IPAddress.TryParse(leftHost, out var leftAddress) &&
                IPAddress.TryParse(rightHost, out var rightAddress))
            {
                return leftAddress.Equals(rightAddress);
            }

            return string.Equals(
                leftHost.TrimEnd('.'),
                rightHost.TrimEnd('.'),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves credentials omitted by the administrator page from the selected
        /// user mapping or, when no custom mapping owns the target, from the global
        /// bridge configuration. Mapping credentials are used only when the supplied
        /// user ID and bridge address match the persisted custom target exactly.
        /// </summary>
        private static bool TryResolveCredentials(
            string? requestedBridgeIp,
            string? requestedAppKey,
            string? requestedClientKey,
            string? userId,
            bool allowStoredClientKey,
            out string bridgeIp,
            out string appKey,
            out string clientKey)
        {
            var config = Plugin.Instance?.Configuration;
            bridgeIp = requestedBridgeIp?.Trim() ?? string.Empty;
            appKey = requestedAppKey?.Trim() ?? string.Empty;
            clientKey = requestedClientKey?.Trim() ?? string.Empty;

            var mapping = config?.UserMappings?.FirstOrDefault(candidate =>
                candidate != null &&
                !string.IsNullOrWhiteSpace(userId) &&
                string.Equals(candidate.UserId?.Trim(), userId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (mapping != null &&
                !string.IsNullOrWhiteSpace(mapping.HueBridgeIp) &&
                IsSameBridgeTarget(bridgeIp, mapping.HueBridgeIp))
            {
                if (string.IsNullOrWhiteSpace(appKey))
                    appKey = mapping.HueAppKey?.Trim() ?? string.Empty;

                if (allowStoredClientKey && string.IsNullOrWhiteSpace(clientKey))
                    clientKey = mapping.HueClientKey?.Trim() ?? string.Empty;

                return !string.IsNullOrWhiteSpace(bridgeIp) && !string.IsNullOrWhiteSpace(appKey);
            }

            var resolved = TryResolveGlobalCredentials(
                bridgeIp,
                appKey,
                clientKey,
                allowStoredClientKey,
                out var resolvedBridgeIp,
                out var resolvedAppKey,
                out var resolvedClientKey);
            bridgeIp = resolvedBridgeIp;
            appKey = resolvedAppKey;
            clientKey = resolvedClientKey;
            return resolved;
        }

        [HttpPost("Register")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<HueRegistrationResult>> RegisterBridge(
            [FromBody] HueRegistrationRequest? request,
            CancellationToken cancellationToken = default)
        {
            if (request == null || !HueBridgeCertificateValidation.IsValidBridgeAddress(request.IpAddress))
            {
                return BadRequest("A valid private bridge IP address or .local host name is required.");
            }

            var result = await _hueClient.RegisterWithBridge(request.IpAddress.Trim(), cancellationToken);
            if (result == null)
            {
                return BadRequest("Failed to register. Did you press the Link Button?");
            }

            return Ok(result);
        }

        /// <summary>
        /// Discovers Hue Bridges reported on the local network. The legacy singular
        /// route remains first-result compatible while also returning every candidate.
        /// </summary>
        [HttpGet("DiscoverBridge")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        public async Task<ActionResult<HueBridgeDiscoveryResult>> DiscoverBridge(CancellationToken cancellationToken = default)
        {
            return await DiscoverBridgesCore(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Discovers every private Hue Bridge visible to the Jellyfin server. This is
        /// useful when different per-user mappings target different rooms or bridges.
        /// </summary>
        [HttpGet("DiscoverBridges")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        public async Task<ActionResult<HueBridgeDiscoveryResult>> DiscoverBridges(CancellationToken cancellationToken = default)
        {
            return await DiscoverBridgesCore(cancellationToken).ConfigureAwait(false);
        }

        private async Task<ActionResult<HueBridgeDiscoveryResult>> DiscoverBridgesCore(CancellationToken cancellationToken)
        {
            var ipAddresses = (await _hueClient.DiscoverBridgeIps(cancellationToken).ConfigureAwait(false))
                .Where(address => !string.IsNullOrWhiteSpace(address))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (ipAddresses.Length == 0)
            {
                return StatusCode(StatusCodes.Status502BadGateway, "No Hue Bridge was found on the local network.");
            }

            return Ok(new HueBridgeDiscoveryResult
            {
                IpAddress = ipAddresses[0],
                IpAddresses = ipAddresses
            });
        }

        [HttpGet("EntertainmentAreas")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        public async Task<ActionResult<IEnumerable<HueClient.EntertainmentArea>>> GetEntertainmentAreas(
            [FromQuery(Name = "ip")] string? bridgeIp,
            [FromQuery(Name = "appKey")] string? appKey,
            [FromQuery(Name = "userId")] string? userId,
            CancellationToken cancellationToken = default)
        {
            return await LoadEntertainmentAreas(bridgeIp, appKey, userId, cancellationToken);
        }

        /// <summary>
        /// Loads entertainment areas using a request body so app keys do not appear in URLs or access logs.
        /// A userId may select a matching persisted custom mapping when the key is omitted.
        /// </summary>
        [HttpPost("EntertainmentAreas")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        public async Task<ActionResult<IEnumerable<HueClient.EntertainmentArea>>> PostEntertainmentAreas(
            [FromBody] HueEntertainmentAreasRequest? request,
            CancellationToken cancellationToken = default)
        {
            if (request == null ||
                !HueBridgeCertificateValidation.IsValidBridgeAddress(request.IpAddress) ||
                !TryResolveCredentials(
                    request.IpAddress,
                    request.AppKey,
                    null,
                    request.UserId,
                    allowStoredClientKey: false,
                    out _,
                    out _,
                    out _))
            {
                return BadRequest("Bridge IP and app key are required before loading entertainment areas.");
            }

            return await LoadEntertainmentAreas(request.IpAddress, request.AppKey, request.UserId, cancellationToken);
        }

        /// <summary>
        /// Loads the channel IDs exposed by one entertainment area so an administrator can
        /// build a per-user channel profile without inspecting the bridge API manually.
        /// A matching userId allows the stored custom mapping key to remain server-side.
        /// </summary>
        [HttpPost("EntertainmentChannels")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        public async Task<ActionResult<IEnumerable<HueEntertainmentChannel>>> PostEntertainmentChannels(
            [FromBody] HueEntertainmentChannelsRequest? request,
            CancellationToken cancellationToken = default)
        {
            if (request == null ||
                !HueBridgeCertificateValidation.IsValidBridgeAddress(request.IpAddress) ||
                string.IsNullOrWhiteSpace(request.EntertainmentAreaId))
            {
                return BadRequest("A valid bridge address, app key, and entertainment area ID are required.");
            }

            if (!TryResolveCredentials(
                    request.IpAddress,
                    request.AppKey,
                    null,
                    request.UserId,
                    allowStoredClientKey: false,
                    out var bridgeIp,
                    out var appKey,
                    out _))
            {
                return BadRequest("A valid bridge address and app key are required.");
            }

            var areaConfiguration = await _hueClient.GetEntertainmentConfiguration(
                bridgeIp,
                appKey,
                request.EntertainmentAreaId.Trim(),
                cancellationToken);
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
            string? appKey,
            string? userId,
            CancellationToken cancellationToken)
        {
            if (!TryResolveCredentials(
                    bridgeIp,
                    appKey,
                    null,
                    userId,
                    allowStoredClientKey: false,
                    out var resolvedBridgeIp,
                    out var resolvedAppKey,
                    out _)
                || !HueBridgeCertificateValidation.IsValidBridgeAddress(resolvedBridgeIp))
            {
                return BadRequest("Bridge IP and app key are required before loading entertainment areas.");
            }

            var areas = await _hueClient.GetEntertainmentAreas(resolvedBridgeIp, resolvedAppKey, cancellationToken);
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
            [FromBody] HueConnectionTestRequest? request,
            CancellationToken cancellationToken = default)
        {
            if (request == null ||
                !HueBridgeCertificateValidation.IsValidBridgeAddress(request.IpAddress))
            {
                return BadRequest("A valid private bridge address and app key are required.");
            }

            if (!TryResolveCredentials(
                    request.IpAddress,
                    request.AppKey,
                    request.ClientKey,
                    request.UserId,
                    allowStoredClientKey: string.IsNullOrWhiteSpace(request.AppKey),
                    out var bridgeIp,
                    out var appKey,
                    out var clientKey))
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

            var areas = await _hueClient.GetEntertainmentAreas(bridgeIp, appKey, cancellationToken);
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

            var areaConfiguration = await _hueClient.GetEntertainmentConfiguration(
                bridgeIp,
                appKey,
                areaId,
                cancellationToken);
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
            if (!string.IsNullOrWhiteSpace(clientKey) && _streamTester != null)
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
                            clientKey,
                            areaId,
                            areaConfiguration.Value,
                            selectedChannelIds,
                            cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
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
                    result.CleanupWarning = streamProbe.CleanupWarning;
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
            [FromBody] HuePreviewRequest? request,
            CancellationToken cancellationToken = default)
        {
            if (request == null ||
                !HueBridgeCertificateValidation.IsValidBridgeAddress(request.IpAddress) ||
                string.IsNullOrWhiteSpace(request.EntertainmentAreaId))
            {
                return BadRequest("A valid bridge address, app key, client key, and entertainment area ID are required.");
            }

            if (!TryResolveCredentials(
                    request.IpAddress,
                    request.AppKey,
                    request.ClientKey,
                    request.UserId,
                    allowStoredClientKey: true,
                    out var bridgeIp,
                    out var appKey,
                    out var clientKey)
                || string.IsNullOrWhiteSpace(clientKey))
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

            var areaId = request.EntertainmentAreaId.Trim();
            var areaConfiguration = await _hueClient.GetEntertainmentConfiguration(
                bridgeIp,
                appKey,
                areaId,
                cancellationToken);
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
                    request.DurationSeconds,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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
                CleanupWarning = streamPreview.CleanupWarning,
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
                CleanupWarning = runtime?.CleanupWarning,
                ActiveUserId = runtime?.ActiveUserId,
                ActiveUserName = runtime?.ActiveUserName,
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
                EffectiveFps = runtime?.EffectiveFps,
                PacketsSent = runtime?.PacketsSent ?? 0,
                PacketsSkippedByThreshold = runtime?.PacketsSkippedByThreshold ?? 0,
                PacketSendFailures = runtime?.PacketSendFailures ?? 0,
                ReconnectAttempts = runtime?.ReconnectAttempts ?? 0,
                CanStopSync = runtime?.CanStopSync ?? false,
                IsFfmpegHealthy = runtime?.IsFfmpegHealthy ?? false,
                IsDtlsHealthy = runtime?.IsDtlsHealthy ?? false,
                SyncDurationSeconds = runtime?.SyncDurationSeconds,
                SyncStartedAtUtc = runtime?.SyncStartedAtUtc
            };

            return Ok(status);
        }

        /// <summary>
        /// Reports local playback prerequisites and sanitized bridge lifecycle state without
        /// contacting or mutating a Hue bridge.
        /// </summary>
        [HttpGet("Diagnostics")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<HueDiagnosticsResult>> GetDiagnostics(
            CancellationToken cancellationToken = default)
        {
            var environment = await _environmentProbe.CheckAsync(cancellationToken).ConfigureAwait(false);
            var config = Plugin.Instance?.Configuration;
            var configurationErrors = new List<string>();
            if (config == null)
            {
                configurationErrors.Add("Plugin configuration is not available.");
            }
            else
            {
                try
                {
                    configurationErrors.AddRange(config.Validate());
                }
                catch
                {
                    configurationErrors.Add("Plugin configuration could not be validated safely.");
                }
            }

            var runtime = _syncService?.GetRuntimeStatus();
            var hasDefaultTarget = config != null &&
                !string.IsNullOrWhiteSpace(config.HueBridgeIp) &&
                !string.IsNullOrWhiteSpace(config.HueAppKey) &&
                !string.IsNullOrWhiteSpace(config.HueClientKey) &&
                !string.IsNullOrWhiteSpace(config.EntertainmentAreaId);
            var hasCustomUserTarget = config?.UserMappings?.Any(mapping =>
                mapping != null &&
                mapping.SyncEnabled &&
                !string.IsNullOrWhiteSpace(mapping.HueBridgeIp) &&
                !string.IsNullOrWhiteSpace(mapping.HueAppKey) &&
                !string.IsNullOrWhiteSpace(mapping.HueClientKey) &&
                !string.IsNullOrWhiteSpace(mapping.EntertainmentAreaId)) == true;
            var playbackActive = _bridgeLifecycleGate.IsPlaybackActive;
            var diagnosticActive = _bridgeLifecycleGate.IsDiagnosticActive;
            var configurationValid = config != null && configurationErrors.Count == 0;
            var serviceAvailable = _syncService != null;

            return Ok(new HueDiagnosticsResult
            {
                PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString(),
                ConfigurationValid = configurationValid,
                ConfigurationErrors = configurationErrors,
                SyncEnabled = config?.SyncEnabled ?? false,
                DefaultBridgeConfigured = hasDefaultTarget,
                EnabledUserMappingCount = config?.UserMappings?.Count(mapping => mapping != null && mapping.SyncEnabled) ?? 0,
                CustomUserTargetConfigured = hasCustomUserTarget,
                ServiceAvailable = serviceAvailable,
                Ffmpeg = environment.Ffmpeg,
                OpenSsl = environment.OpenSsl,
                PlaybackLifecycleActive = playbackActive,
                DiagnosticLifecycleActive = diagnosticActive,
                BridgeLifecycleState = playbackActive
                    ? "Playback"
                    : diagnosticActive
                        ? "Diagnostic"
                        : "Idle",
                CanRunDiagnostics = serviceAvailable && configurationValid &&
                    (hasDefaultTarget || hasCustomUserTarget) &&
                    environment.OpenSsl.Available && !playbackActive && !diagnosticActive,
                CanStartPlayback = serviceAvailable && configurationValid && (config?.SyncEnabled ?? false) &&
                    (hasDefaultTarget || hasCustomUserTarget) &&
                    environment.Ffmpeg.Available && environment.OpenSsl.Available &&
                    !playbackActive && !diagnosticActive,
                RuntimeState = runtime?.State ?? "Unavailable",
                RuntimeMessage = runtime?.Message,
                LastError = runtime?.LastError,
                CleanupWarning = runtime?.CleanupWarning,
                CheckedAtUtc = DateTime.UtcNow
            });
        }

        /// <summary>
        /// Validates every saved playback target without mutating the bridge. This is
        /// intentionally separate from the local prerequisite report above: a server can
        /// have working FFmpeg/OpenSSL binaries while one of several mapped bridges has
        /// stale credentials, a missing area, or no controllable channels.
        /// </summary>
        [HttpGet("TargetDiagnostics")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<HueTargetDiagnosticsResult>> GetTargetDiagnostics(
            CancellationToken cancellationToken = default)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return NotFound("Plugin configuration not available.");
            }

            var targets = EnumerateConfiguredTargets(config).ToArray();
            var areaRequests = new Dictionary<string, Task<List<HueClient.EntertainmentArea>?>>(StringComparer.Ordinal);
            var configurationRequests = new Dictionary<string, Task<System.Text.Json.JsonElement?>>(StringComparer.Ordinal);
            var results = new List<HueTargetDiagnostic>(targets.Length);

            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await ValidateTargetAsync(
                    target,
                    areaRequests,
                    configurationRequests,
                    cancellationToken).ConfigureAwait(false));
            }

            var readyCount = results.Count(result => result.Ready);
            return Ok(new HueTargetDiagnosticsResult
            {
                HasConfiguredTargets = results.Count > 0,
                AllTargetsReady = results.Count > 0 && readyCount == results.Count,
                TargetCount = results.Count,
                ReadyTargetCount = readyCount,
                Targets = results,
                CheckedAtUtc = DateTime.UtcNow
            });
        }

        private async Task<HueTargetDiagnostic> ValidateTargetAsync(
            HueTarget target,
            IDictionary<string, Task<List<HueClient.EntertainmentArea>?>> areaRequests,
            IDictionary<string, Task<System.Text.Json.JsonElement?>> configurationRequests,
            CancellationToken cancellationToken)
        {
            var result = new HueTargetDiagnostic
            {
                Scope = target.Scope,
                UserId = target.UserId,
                UserName = target.UserName,
                SyncEnabled = target.SyncEnabled,
                InheritsDefaultBridge = target.InheritsDefaultBridge,
                BridgeIp = target.BridgeIp,
                EntertainmentAreaId = target.AreaId,
                EntertainmentAreaName = target.AreaName,
                HasAppKey = !string.IsNullOrWhiteSpace(target.AppKey),
                HasClientKey = !string.IsNullOrWhiteSpace(target.ClientKey),
                ConfigurationValid = true
            };

            if (!HueBridgeCertificateValidation.IsValidBridgeAddress(target.BridgeIp))
            {
                return result with
                {
                    ConfigurationValid = false,
                    Status = "Bridge address is missing or invalid."
                };
            }

            if (string.IsNullOrWhiteSpace(target.AppKey))
            {
                return result with
                {
                    ConfigurationValid = false,
                    Status = "App Key is missing."
                };
            }

            if (string.IsNullOrWhiteSpace(target.AreaId))
            {
                return result with
                {
                    ConfigurationValid = false,
                    Status = "Entertainment area is not selected."
                };
            }

            var areaCacheKey = target.BridgeIp.Trim() + "\n" + target.AppKey.Trim();
            if (!areaRequests.TryGetValue(areaCacheKey, out var areasTask))
            {
                areasTask = _hueClient.GetEntertainmentAreas(
                    target.BridgeIp.Trim(),
                    target.AppKey.Trim(),
                    cancellationToken);
                areaRequests[areaCacheKey] = areasTask;
            }

            var areas = await areasTask.ConfigureAwait(false);
            if (areas == null)
            {
                return result with
                {
                    ConfigurationValid = !string.IsNullOrWhiteSpace(target.ClientKey),
                    Status = "Bridge could not be reached with the saved App Key."
                };
            }

            result = result with
            {
                BridgeReachable = true,
                AreaCount = areas.Count
            };

            var selectedArea = areas.FirstOrDefault(area =>
                string.Equals(area.Id, target.AreaId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (selectedArea == null)
            {
                return result with
                {
                    ConfigurationValid = false,
                    Status = string.IsNullOrWhiteSpace(target.ClientKey)
                        ? "Bridge is reachable, but the selected area was not found and the Client Key is missing."
                        : "Bridge is reachable, but the selected area was not found."
                };
            }

            var configurationCacheKey = areaCacheKey + "\n" + target.AreaId.Trim();
            if (!configurationRequests.TryGetValue(configurationCacheKey, out var configurationTask))
            {
                configurationTask = _hueClient.GetEntertainmentConfiguration(
                    target.BridgeIp.Trim(),
                    target.AppKey.Trim(),
                    target.AreaId.Trim(),
                    cancellationToken);
                configurationRequests[configurationCacheKey] = configurationTask;
            }

            var areaConfiguration = await configurationTask.ConfigureAwait(false);
            if (areaConfiguration == null)
            {
                return result with
                {
                    ConfigurationValid = false,
                    AreaFound = true,
                    EntertainmentAreaName = selectedArea.Name,
                    Status = "Bridge is reachable, but the selected area configuration could not be loaded."
                };
            }

            var channelCount = GetValidChannelIds(areaConfiguration.Value).Count;
            var hasClientKey = !string.IsNullOrWhiteSpace(target.ClientKey);
            return result with
            {
                ConfigurationValid = hasClientKey && channelCount > 0,
                AreaFound = true,
                EntertainmentAreaName = selectedArea.Name,
                ChannelCount = channelCount,
                Ready = hasClientKey && channelCount > 0,
                Status = !hasClientKey
                    ? "Bridge and area are reachable, but the Client Key is missing."
                    : channelCount > 0
                        ? "Ready for playback."
                        : "The selected area has no controllable channels."
            };
        }

        private static IEnumerable<HueTarget> EnumerateConfiguredTargets(PluginConfiguration config)
        {
            var hasGlobalTarget = !string.IsNullOrWhiteSpace(config.HueBridgeIp) ||
                !string.IsNullOrWhiteSpace(config.HueAppKey) ||
                !string.IsNullOrWhiteSpace(config.HueClientKey) ||
                !string.IsNullOrWhiteSpace(config.EntertainmentAreaId);
            if (hasGlobalTarget)
            {
                yield return new HueTarget(
                    Scope: "Default",
                    UserId: null,
                    UserName: null,
                    SyncEnabled: config.SyncEnabled,
                    InheritsDefaultBridge: false,
                    BridgeIp: config.HueBridgeIp?.Trim() ?? string.Empty,
                    AppKey: config.HueAppKey?.Trim() ?? string.Empty,
                    ClientKey: config.HueClientKey?.Trim() ?? string.Empty,
                    AreaId: config.EntertainmentAreaId?.Trim() ?? string.Empty,
                    AreaName: null);
            }

            foreach (var mapping in config.UserMappings ?? new List<UserBridgeMapping>())
            {
                if (mapping == null || !mapping.SyncEnabled)
                    continue;

                var inheritsDefaultBridge = string.IsNullOrWhiteSpace(mapping.HueBridgeIp);
                yield return new HueTarget(
                    Scope: "User",
                    UserId: mapping.UserId?.Trim(),
                    UserName: string.IsNullOrWhiteSpace(mapping.UserName) ? null : mapping.UserName.Trim(),
                    SyncEnabled: true,
                    InheritsDefaultBridge: inheritsDefaultBridge,
                    BridgeIp: inheritsDefaultBridge ? config.HueBridgeIp?.Trim() ?? string.Empty : mapping.HueBridgeIp.Trim(),
                    AppKey: inheritsDefaultBridge ? config.HueAppKey?.Trim() ?? string.Empty : mapping.HueAppKey?.Trim() ?? string.Empty,
                    ClientKey: inheritsDefaultBridge ? config.HueClientKey?.Trim() ?? string.Empty : mapping.HueClientKey?.Trim() ?? string.Empty,
                    AreaId: inheritsDefaultBridge ? config.EntertainmentAreaId?.Trim() ?? string.Empty : mapping.EntertainmentAreaId?.Trim() ?? string.Empty,
                    AreaName: string.IsNullOrWhiteSpace(mapping.EntertainmentAreaName) ? null : mapping.EntertainmentAreaName.Trim());
            }
        }

        private sealed record HueTarget(
            string Scope,
            string? UserId,
            string? UserName,
            bool SyncEnabled,
            bool InheritsDefaultBridge,
            string BridgeIp,
            string AppKey,
            string ClientKey,
            string AreaId,
            string? AreaName);

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
            var previousAppKey = config.HueAppKey;
            var previousClientKey = config.HueClientKey;
            settings.ApplyTo(config);
            var validationErrors = config.Validate();
            if (validationErrors.Count > 0)
            {
                previousSettings.ApplyTo(config);
                config.HueAppKey = previousAppKey;
                config.HueClientKey = previousClientKey;
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

            var inheritsDefaultBridge = string.IsNullOrWhiteSpace(mapping.HueBridgeIp);

            // The edit form deliberately leaves secret fields blank. Preserve an
            // existing credential only while the mapping continues to target its
            // own bridge. Clearing the bridge address is an explicit request to
            // inherit the global bridge, so stale custom credentials must not return.
            if (mapping.SyncEnabled && !inheritsDefaultBridge && existingMapping != null)
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

            if (mapping.SyncEnabled && !inheritsDefaultBridge &&
                !HueBridgeCertificateValidation.IsValidBridgeAddress(mapping.HueBridgeIp))
            {
                return BadRequest("A valid private bridge IP address or .local host name is required.");
            }

            if (mapping.SyncEnabled && !inheritsDefaultBridge &&
                (string.IsNullOrWhiteSpace(mapping.HueAppKey) ||
                 string.IsNullOrWhiteSpace(mapping.HueClientKey) ||
                 string.IsNullOrWhiteSpace(mapping.EntertainmentAreaId)))
            {
                return BadRequest("Bridge credentials and entertainment area ID are required.");
            }

            if (mapping.SyncEnabled && inheritsDefaultBridge &&
                (!string.IsNullOrWhiteSpace(mapping.HueAppKey) ||
                 !string.IsNullOrWhiteSpace(mapping.HueClientKey) ||
                 !string.IsNullOrWhiteSpace(mapping.EntertainmentAreaId)))
            {
                return BadRequest("Leave mapping bridge credentials and entertainment area blank to inherit the global bridge settings, or provide a complete custom bridge target.");
            }

            if (mapping.SyncEnabled && inheritsDefaultBridge)
            {
                // Keep the persisted representation unambiguous: blank bridge
                // mappings inherit every global target field and retain no secrets.
                mapping.HueBridgeIp = string.Empty;
                mapping.HueAppKey = string.Empty;
                mapping.HueClientKey = string.Empty;
                mapping.EntertainmentAreaId = string.Empty;
                mapping.EntertainmentAreaName = string.Empty;
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
    /// and the global bridge secrets so the generic settings flow cannot round-trip credentials
    /// through a browser. Blank secret fields preserve the stored values; set ClearStoredCredentials
    /// explicitly when both global credentials must be removed.
    /// </summary>
    public sealed class HuePluginConfigurationSettings
    {
        public bool SyncEnabled { get; set; }
        public string HueBridgeIp { get; set; } = string.Empty;
        public string HueAppKey { get; set; } = string.Empty;
        public string HueClientKey { get; set; } = string.Empty;
        public bool HasAppKey { get; set; }
        public bool HasClientKey { get; set; }
        public bool ClearStoredCredentials { get; set; }
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
                HueAppKey = string.Empty,
                HueClientKey = string.Empty,
                HasAppKey = !string.IsNullOrWhiteSpace(config.HueAppKey),
                HasClientKey = !string.IsNullOrWhiteSpace(config.HueClientKey),
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
            if (ClearStoredCredentials)
            {
                config.HueAppKey = string.Empty;
                config.HueClientKey = string.Empty;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(HueAppKey))
                    config.HueAppKey = HueAppKey.Trim();
                if (!string.IsNullOrWhiteSpace(HueClientKey))
                    config.HueClientKey = HueClientKey.Trim();
            }
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
        public bool InheritsDefaultBridge { get; set; }
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
                InheritsDefaultBridge = string.IsNullOrWhiteSpace(mapping.HueBridgeIp),
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
        [JsonPropertyName("userId")]
        public string? UserId { get; set; }

        [JsonPropertyName("ipAddress")]
        public string IpAddress { get; set; } = string.Empty;

        [JsonPropertyName("appKey")]
        public string AppKey { get; set; } = string.Empty;
    }

    public class HueEntertainmentChannelsRequest
    {
        [JsonPropertyName("userId")]
        public string? UserId { get; set; }

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
        [JsonPropertyName("userId")]
        public string? UserId { get; set; }

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
        [JsonPropertyName("userId")]
        public string? UserId { get; set; }

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

        [JsonPropertyName("cleanupWarning")]
        public string? CleanupWarning { get; set; }

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

        [JsonPropertyName("cleanupWarning")]
        public string? CleanupWarning { get; set; }

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

        /// <summary>
        /// Every distinct private bridge address found during this discovery pass. The
        /// singular <see cref="IpAddress"/> property remains the first result for older
        /// configuration-page clients.
        /// </summary>
        [JsonPropertyName("ipAddresses")]
        public IReadOnlyList<string> IpAddresses { get; set; } = Array.Empty<string>();
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
        public string? CleanupWarning { get; set; }
        public string? ActiveUserId { get; set; }
        public string? ActiveUserName { get; set; }
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
        public double? EffectiveFps { get; set; }
        public long PacketsSent { get; set; }
        public long PacketsSkippedByThreshold { get; set; }
        public long PacketSendFailures { get; set; }
        public int ReconnectAttempts { get; set; }
        public bool CanStopSync { get; set; }
        public bool IsFfmpegHealthy { get; set; }
        public bool IsDtlsHealthy { get; set; }
        public double? SyncDurationSeconds { get; set; }
        public DateTime? SyncStartedAtUtc { get; set; }
    }

    /// <summary>
    /// Sanitized setup and runtime diagnostics. No bridge credentials are included.
    /// </summary>
    public sealed class HueDiagnosticsResult
    {
        public string? PluginVersion { get; init; }
        public bool ConfigurationValid { get; init; }
        public IReadOnlyList<string> ConfigurationErrors { get; init; } = Array.Empty<string>();
        public bool SyncEnabled { get; init; }
        public bool DefaultBridgeConfigured { get; init; }
        public int EnabledUserMappingCount { get; init; }
        public bool CustomUserTargetConfigured { get; init; }
        public bool ServiceAvailable { get; init; }
        public HueToolStatus Ffmpeg { get; init; } = new();
        public HueToolStatus OpenSsl { get; init; } = new();
        public bool PlaybackLifecycleActive { get; init; }
        public bool DiagnosticLifecycleActive { get; init; }
        public string BridgeLifecycleState { get; init; } = "Idle";
        public bool CanRunDiagnostics { get; init; }
        public bool CanStartPlayback { get; init; }
        public string RuntimeState { get; init; } = "Unavailable";
        public string? RuntimeMessage { get; init; }
        public string? LastError { get; init; }
        public string? CleanupWarning { get; init; }
        public DateTime CheckedAtUtc { get; init; }
    }

    /// <summary>
    /// Sanitized validation results for all configured default and per-user targets.
    /// Bridge credentials are represented only by presence flags.
    /// </summary>
    public sealed class HueTargetDiagnosticsResult
    {
        public bool HasConfiguredTargets { get; init; }
        public bool AllTargetsReady { get; init; }
        public int TargetCount { get; init; }
        public int ReadyTargetCount { get; init; }
        public IReadOnlyList<HueTargetDiagnostic> Targets { get; init; } = Array.Empty<HueTargetDiagnostic>();
        public DateTime CheckedAtUtc { get; init; }
    }

    /// <summary>
    /// A single non-mutating bridge/area validation result. This type intentionally has
    /// no App Key, Client Key, or other bridge secret fields.
    /// </summary>
    public sealed record HueTargetDiagnostic
    {
        public string Scope { get; init; } = string.Empty;
        public string? UserId { get; init; }
        public string? UserName { get; init; }
        public bool SyncEnabled { get; init; }
        public bool InheritsDefaultBridge { get; init; }
        public string BridgeIp { get; init; } = string.Empty;
        public string EntertainmentAreaId { get; init; } = string.Empty;
        public string? EntertainmentAreaName { get; init; }
        public bool HasAppKey { get; init; }
        public bool HasClientKey { get; init; }
        public bool ConfigurationValid { get; init; }
        public bool BridgeReachable { get; init; }
        public int AreaCount { get; init; }
        public bool AreaFound { get; init; }
        public int ChannelCount { get; init; }
        public bool Ready { get; init; }
        public string Status { get; init; } = string.Empty;
    }

}
