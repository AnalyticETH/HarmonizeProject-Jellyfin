using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Mime;
using System.Text;
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
using Microsoft.Extensions.Logging;

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
        private readonly HueSceneAutomationService? _sceneAutomationService;
        private readonly IHueStreamTester? _streamTester;
        private readonly HueBridgeLifecycleGate _bridgeLifecycleGate;
        private readonly HueDiagnosticsCancellationGate _diagnosticsCancellationGate;
        private readonly IHueEnvironmentProbe _environmentProbe;
        private readonly ILogger<HueApiController>? _logger;

        public HueApiController(HueClient hueClient, IEnumerable<Microsoft.Extensions.Hosting.IHostedService> hostedServices)
            : this(hueClient, hostedServices, null, null, null, null, null)
        {
        }

        [ActivatorUtilitiesConstructor]
        public HueApiController(
            HueClient hueClient,
            IEnumerable<Microsoft.Extensions.Hosting.IHostedService> hostedServices,
            IHueStreamTester? streamTester,
            HueBridgeLifecycleGate? bridgeLifecycleGate = null,
            IHueEnvironmentProbe? environmentProbe = null,
            HueDiagnosticsCancellationGate? diagnosticsCancellationGate = null,
            ILogger<HueApiController>? logger = null)
        {
            _hueClient = hueClient;
            _syncService = hostedServices.OfType<Service.HueSyncService>().FirstOrDefault();
            _sceneAutomationService = hostedServices.OfType<HueSceneAutomationService>().FirstOrDefault();
            _streamTester = streamTester;
            _bridgeLifecycleGate = bridgeLifecycleGate ?? new HueBridgeLifecycleGate();
            _diagnosticsCancellationGate = diagnosticsCancellationGate ?? new HueDiagnosticsCancellationGate();
            _environmentProbe = environmentProbe ?? new HueEnvironmentProbe();
            _logger = logger;
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

        internal static HueColorPresetResult ToColorPresetResult(HueColorPreset preset)
        {
            PluginConfiguration.TryNormalizeColorPresetEffect(preset.Effect, out var effect);
            return new HueColorPresetResult
            {
                Name = preset.Name,
                Effect = effect,
                EffectSpeedPercent = PluginConfiguration.ClampColorPresetEffectSpeedPercent(preset.EffectSpeedPercent),
                Red = preset.Red,
                Green = preset.Green,
                Blue = preset.Blue,
                BrightnessPercent = preset.BrightnessPercent,
                DurationSeconds = preset.DurationSeconds,
                TransitionSeconds = preset.TransitionSeconds,
                TransitionOutSeconds = preset.TransitionOutSeconds
            };
        }

        internal static HueSceneScheduleResult ToSceneScheduleResult(
            HueSceneSchedule schedule,
            PluginConfiguration config)
        {
            var targetUserId = schedule.TargetUserId?.Trim() ?? string.Empty;
            var mapping = string.IsNullOrWhiteSpace(targetUserId)
                ? null
                : config.UserMappings?.FirstOrDefault(candidate =>
                    candidate != null &&
                    string.Equals(candidate.UserId?.Trim(), targetUserId, StringComparison.OrdinalIgnoreCase));
            var targetLabel = string.IsNullOrWhiteSpace(targetUserId)
                ? "Default bridge target"
                : mapping == null
                    ? "Missing user mapping"
                    : string.IsNullOrWhiteSpace(mapping.UserName)
                        ? $"User mapping {mapping.UserId.Trim()}"
                        : mapping.UserName.Trim();
            IReadOnlyList<string> excludedDates =
                PluginConfiguration.TryNormalizeSceneScheduleExcludedDates(
                    schedule.ExcludedDates,
                    out var normalizedExcludedDates)
                    ? normalizedExcludedDates
                    : (schedule.ExcludedDates ?? new List<string>())
                        .Select(value => value?.Trim() ?? string.Empty)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
            var preset = config.ColorPresets?.FirstOrDefault(candidate =>
                candidate != null &&
                string.Equals(candidate.Name?.Trim(), schedule.PresetName?.Trim(), StringComparison.OrdinalIgnoreCase));
            PluginConfiguration.TryNormalizeColorPresetEffect(preset?.Effect, out var effect);

            return new HueSceneScheduleResult
            {
                Id = schedule.Id,
                Name = schedule.Name,
                PresetName = schedule.PresetName,
                Effect = effect,
                EffectSpeedPercent = preset == null
                    ? PluginConfiguration.DefaultColorPresetEffectSpeedPercent
                    : PluginConfiguration.ClampColorPresetEffectSpeedPercent(preset.EffectSpeedPercent),
                TargetUserId = targetUserId,
                TargetLabel = targetLabel,
                TimeOfDay = schedule.TimeOfDay,
                TimeZoneId = schedule.TimeZoneId?.Trim() ?? string.Empty,
                Recurrence = PluginConfiguration.TryNormalizeSceneScheduleRecurrence(
                    schedule.Recurrence,
                    out var normalizedRecurrence)
                    ? normalizedRecurrence
                    : schedule.Recurrence?.Trim() ?? string.Empty,
                RecurrenceInterval = schedule.RecurrenceInterval,
                DayOfMonth = schedule.DayOfMonth,
                MonthOfYear = schedule.MonthOfYear,
                WeekOfMonth = schedule.WeekOfMonth,
                DayOfWeek = schedule.DayOfWeek,
                DurationSeconds = schedule.DurationSeconds,
                MaxRuns = schedule.MaxRuns,
                RunCount = schedule.RunCount,
                RunDate = schedule.RunDate?.Trim() ?? string.Empty,
                StartDate = schedule.StartDate?.Trim() ?? string.Empty,
                EndDate = schedule.EndDate?.Trim() ?? string.Empty,
                ExcludedDates = excludedDates,
                DaysOfWeekMask = schedule.DaysOfWeekMask,
                Enabled = schedule.Enabled,
                TransitionSeconds = HueSceneAutomationService.GetEffectiveTransitionSeconds(schedule, preset),
                TransitionOutSeconds = HueSceneAutomationService.GetEffectiveTransitionOutSeconds(schedule, preset)
            };
        }

        private static HueSceneSchedule CloneSceneSchedule(HueSceneSchedule schedule)
        {
            return new HueSceneSchedule
            {
                Id = schedule.Id,
                Name = schedule.Name,
                PresetName = schedule.PresetName,
                TargetUserId = schedule.TargetUserId,
                TimeOfDay = schedule.TimeOfDay,
                TimeZoneId = schedule.TimeZoneId,
                Recurrence = schedule.Recurrence,
                RecurrenceInterval = schedule.RecurrenceInterval,
                DayOfMonth = schedule.DayOfMonth,
                MonthOfYear = schedule.MonthOfYear,
                WeekOfMonth = schedule.WeekOfMonth,
                DayOfWeek = schedule.DayOfWeek,
                DurationSeconds = schedule.DurationSeconds,
                MaxRuns = schedule.MaxRuns,
                RunCount = schedule.RunCount,
                RunDate = schedule.RunDate,
                StartDate = schedule.StartDate,
                EndDate = schedule.EndDate,
                ExcludedDates = schedule.ExcludedDates?.ToList() ?? new List<string>(),
                DaysOfWeekMask = schedule.DaysOfWeekMask,
                Enabled = schedule.Enabled
            };
        }

        private static string BuildDuplicateSceneScheduleName(
            IEnumerable<HueSceneSchedule> schedules,
            string? sourceName)
        {
            var existingNames = new HashSet<string>(
                schedules
                    .Where(schedule => schedule != null)
                    .Select(schedule => schedule.Name?.Trim() ?? string.Empty)
                    .Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);
            var baseName = string.IsNullOrWhiteSpace(sourceName)
                ? "Scheduled cue"
                : sourceName.Trim();

            for (var copyNumber = 1; copyNumber <= PluginConfiguration.MaxSceneSchedules + 1; copyNumber++)
            {
                var suffix = copyNumber == 1
                    ? " (Copy)"
                    : $" (Copy {copyNumber})";
                var availableBaseLength = Math.Max(
                    1,
                    PluginConfiguration.MaxSceneScheduleNameLength - suffix.Length);
                var truncatedBase = baseName.Length > availableBaseLength
                    ? baseName[..availableBaseLength].TrimEnd()
                    : baseName;
                var candidate = truncatedBase + suffix;
                if (!existingNames.Contains(candidate))
                    return candidate;
            }

            // The schedule collection is bounded, so the loop above always returns. Keep
            // a bounded unique fallback for malformed legacy configurations with an
            // unexpectedly large collection.
            return $"Cue copy {Guid.NewGuid():N}"[..PluginConfiguration.MaxSceneScheduleNameLength];
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
        /// Displays a bounded scene-effect preview through the configured entertainment
        /// area. The stream tester captures and restores the selected lights so this
        /// diagnostic never leaves a manual scene behind.
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

            if (!PluginConfiguration.TryNormalizeColorPresetEffect(request.Effect, out var effect))
            {
                return BadRequest($"Preview effect must be one of {PluginConfiguration.ColorPresetEffectSolid}, {PluginConfiguration.ColorPresetEffectPulse}, {PluginConfiguration.ColorPresetEffectRainbow}, or {PluginConfiguration.ColorPresetEffectCandle}.");
            }

            if (request.EffectSpeedPercent < PluginConfiguration.MinColorPresetEffectSpeedPercent ||
                request.EffectSpeedPercent > PluginConfiguration.MaxColorPresetEffectSpeedPercent)
            {
                return BadRequest($"Preview effect speed must be between {PluginConfiguration.MinColorPresetEffectSpeedPercent} and {PluginConfiguration.MaxColorPresetEffectSpeedPercent} percent.");
            }

            if (request.DurationSeconds < HueStreamTester.MinPreviewDurationSeconds ||
                request.DurationSeconds > HueStreamTester.MaxPreviewDurationSeconds)
            {
                return BadRequest($"Preview duration must be between {HueStreamTester.MinPreviewDurationSeconds} and {HueStreamTester.MaxPreviewDurationSeconds} seconds.");
            }

            if (request.TransitionSeconds < PluginConfiguration.MinColorPresetTransitionSeconds ||
                request.TransitionSeconds > PluginConfiguration.MaxColorPresetTransitionSeconds)
            {
                return BadRequest($"Preview transition must be between {PluginConfiguration.MinColorPresetTransitionSeconds} and {PluginConfiguration.MaxColorPresetTransitionSeconds} seconds.");
            }

            if (request.TransitionSeconds > request.DurationSeconds)
                return BadRequest("Preview transition cannot exceed the preview duration.");

            if (request.TransitionOutSeconds < PluginConfiguration.MinColorPresetTransitionOutSeconds ||
                request.TransitionOutSeconds > PluginConfiguration.MaxColorPresetTransitionOutSeconds)
            {
                return BadRequest($"Preview fade-out must be between {PluginConfiguration.MinColorPresetTransitionOutSeconds} and {PluginConfiguration.MaxColorPresetTransitionOutSeconds} seconds.");
            }

            if (request.TransitionSeconds + request.TransitionOutSeconds > request.DurationSeconds)
                return BadRequest("Preview fade-in and fade-out cannot exceed the preview duration together.");

            if (_streamTester == null)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Hue preview service is not available.");
            }

            if (_syncService?.IsSyncing == true)
            {
                return Conflict("Stop active playback before running a Hue scene preview.");
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
                    cancellationToken,
                    request.TransitionSeconds,
                    request.TransitionOutSeconds,
                    effect,
                    request.EffectSpeedPercent);
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
                    Message = $"The {effect.ToLowerInvariant()} preview failed unexpectedly. Check the server log."
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
                Effect = effect,
                EffectSpeedPercent = request.EffectSpeedPercent,
                BrightnessPercent = request.BrightnessPercent,
                DurationSeconds = request.DurationSeconds,
                TransitionSeconds = request.TransitionSeconds,
                TransitionOutSeconds = request.TransitionOutSeconds,
                AvailableChannelCount = availableChannelIds.Count,
                SelectedChannelCount = requestedChannelIds?.Count ?? availableChannelIds.Count
            });
        }

        /// <summary>
        /// Requests cancellation of the active administrator preview or diagnostic. The
        /// active lifecycle still deactivates the entertainment area and restores captured
        /// light state before completing.
        /// </summary>
        [HttpPost("Preview/Cancel")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public ActionResult<HuePreviewCancellationResult> CancelPreview()
        {
            if (_streamTester == null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Hue preview service is not available.");

            var canceled = _streamTester.CancelActiveDiagnostic();
            return Ok(new HuePreviewCancellationResult
            {
                Canceled = canceled,
                Message = canceled
                    ? "Cancellation requested; the preview will restore the bridge state before ending."
                    : "No active Hue preview or diagnostic is running."
            });
        }

        /// <summary>
        /// Lists reusable credential-free preview scenes. Presets contain only visual
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
        /// Saves or updates a reusable preview scene by case-insensitive name.
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
        /// Deletes one reusable preview scene by name.
        /// </summary>
        [HttpDelete("ColorPresets/{name}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public ActionResult DeleteColorPreset(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return NotFound("Color preset not found.");

            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return NotFound("Plugin configuration not available.");

            var referencedScheduleCount = config.SceneSchedules?.Count(schedule =>
                schedule != null &&
                string.Equals(schedule.PresetName?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)) ?? 0;
            if (referencedScheduleCount > 0)
            {
                return Conflict($"The saved scene is used by {referencedScheduleCount} scheduled cue(s). Delete or update those cues first.");
            }

            config.ColorPresets ??= new List<HueColorPreset>();
            var removed = config.ColorPresets.RemoveAll(existing =>
                existing != null &&
                string.Equals(existing.Name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
                return NotFound("Color preset not found.");

            Plugin.Instance?.SaveConfiguration();
            return Ok(new { message = "Color preset deleted successfully." });
        }

        /// <summary>
        /// Lists scene cues without returning bridge credentials. Target user
        /// IDs are retained so the configuration page can address a mapping, while the
        /// human-readable target label is derived from the current mapping.
        /// </summary>
        [HttpGet("SceneSchedules")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<IEnumerable<HueSceneScheduleResult>> GetSceneSchedules()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return NotFound("Plugin configuration not available.");

            return Ok((config.SceneSchedules ?? new List<HueSceneSchedule>())
                .Where(schedule => schedule != null)
                .OrderBy(schedule => schedule.TimeOfDay, StringComparer.Ordinal)
                .ThenBy(schedule => schedule.Name, StringComparer.OrdinalIgnoreCase)
                .Select(schedule => ToSceneScheduleResult(schedule, config)));
        }

        /// <summary>
        /// Lists the system time zones available to scene cues. IDs are the
        /// exact values accepted by <see cref="TimeZoneInfo.FindSystemTimeZoneById"/>.
        /// </summary>
        [HttpGet("SceneSchedules/TimeZones")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<IEnumerable<HueSceneScheduleTimeZoneResult>> GetSceneScheduleTimeZones()
        {
            var zones = TimeZoneInfo.GetSystemTimeZones()
                .OrderBy(zone => zone.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(zone => zone.Id, StringComparer.OrdinalIgnoreCase)
                .Select(zone => new HueSceneScheduleTimeZoneResult
                {
                    Id = zone.Id,
                    DisplayName = zone.DisplayName,
                    BaseUtcOffsetMinutes = (int)zone.BaseUtcOffset.TotalMinutes
                })
                .ToArray();
            return Ok(zones);
        }

        /// <summary>
        /// Returns next-run and last-run telemetry for scheduled scene cues without
        /// exposing bridge credentials or target connection details.
        /// </summary>
        [HttpGet("SceneSchedules/Status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<HueSceneAutomationStatus> GetSceneScheduleStatus()
        {
            if (_sceneAutomationService == null)
            {
                return Ok(new HueSceneAutomationStatus
                {
                    ServiceAvailable = false,
                    AutomationEnabled = Plugin.Instance?.Configuration?.SceneAutomationEnabled ?? true,
                    GeneratedAtUtc = DateTime.UtcNow,
                    ServerLocalNow = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified),
                    ServerTimeZoneId = TimeZoneInfo.Local.Id,
                    Schedules = Array.Empty<HueSceneScheduleRuntimeStatus>()
                });
            }

            return Ok(_sceneAutomationService.GetStatus());
        }

        /// <summary>
        /// Returns a bounded, credential-free preview of upcoming cue occurrences. The
        /// calculation uses each cue's timezone, date window, exclusions, daily, weekly,
        /// monthly-day, monthly-weekday, or yearly recurrence, and DST rules without contacting the bridge.
        /// </summary>
        [HttpGet("SceneSchedules/Occurrences")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<HueSceneScheduleOccurrencesResult> GetSceneScheduleOccurrences(
            [FromQuery(Name = "limit")] int limit = HueSceneAutomationService.MaxUpcomingOccurrencesPerSchedule,
            [FromQuery(Name = "days")] int days = HueSceneAutomationService.DefaultUpcomingHorizonDays,
            [FromQuery(Name = "scheduleId")] string? scheduleId = null)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return NotFound("Plugin configuration not available.");

            var boundedLimit = Math.Clamp(limit, 1, HueSceneAutomationService.MaxUpcomingOccurrencesPerSchedule);
            var boundedDays = Math.Clamp(days, 1, HueSceneAutomationService.MaxUpcomingHorizonDays);
            var normalizedScheduleId = string.IsNullOrWhiteSpace(scheduleId) ? null : scheduleId.Trim();
            var serverLocalNow = DateTime.Now;
            var occurrences = BuildUpcomingSceneScheduleOccurrences(
                config,
                serverLocalNow,
                boundedLimit,
                boundedDays,
                normalizedScheduleId);

            return Ok(new HueSceneScheduleOccurrencesResult
            {
                ServiceAvailable = _sceneAutomationService != null,
                GeneratedAtUtc = DateTime.UtcNow,
                ServerLocalNow = DateTime.SpecifyKind(serverLocalNow, DateTimeKind.Unspecified),
                ServerTimeZoneId = TimeZoneInfo.Local.Id,
                Limit = boundedLimit,
                HorizonDays = boundedDays,
                ScheduleIdFilter = normalizedScheduleId,
                Occurrences = occurrences
            });
        }

        /// <summary>
        /// Returns the same bounded, credential-free cue preview as the JSON endpoint
        /// in iCalendar format for calendar clients. Events use UTC instants while the
        /// cue's configured timezone is retained as metadata, so DST behavior cannot
        /// drift between the preview and an imported calendar.
        /// </summary>
        [HttpGet("SceneSchedules/Calendar")]
        [Produces("text/calendar")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult GetSceneScheduleCalendar(
            [FromQuery(Name = "limit")] int limit = HueSceneAutomationService.MaxUpcomingOccurrencesPerSchedule,
            [FromQuery(Name = "days")] int days = HueSceneAutomationService.DefaultUpcomingHorizonDays,
            [FromQuery(Name = "scheduleId")] string? scheduleId = null)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return NotFound("Plugin configuration not available.");

            var boundedLimit = Math.Clamp(limit, 1, HueSceneAutomationService.MaxUpcomingOccurrencesPerSchedule);
            var boundedDays = Math.Clamp(days, 1, HueSceneAutomationService.MaxUpcomingHorizonDays);
            var normalizedScheduleId = string.IsNullOrWhiteSpace(scheduleId) ? null : scheduleId.Trim();
            var generatedAtUtc = DateTime.UtcNow;
            var occurrences = BuildUpcomingSceneScheduleOccurrences(
                config,
                DateTime.Now,
                boundedLimit,
                boundedDays,
                normalizedScheduleId);
            var calendar = BuildSceneScheduleCalendar(occurrences, generatedAtUtc, boundedDays);
            return File(
                Encoding.UTF8.GetBytes(calendar),
                "text/calendar; charset=utf-8",
                "jellyfin-hue-scene-cues.ics");
        }

        private static IReadOnlyList<HueSceneScheduleOccurrenceResult> BuildUpcomingSceneScheduleOccurrences(
            PluginConfiguration config,
            DateTime serverLocalNow,
            int boundedLimit,
            int boundedDays,
            string? normalizedScheduleId)
        {
            var schedules = (config.SceneSchedules ?? new List<HueSceneSchedule>())
                .Where(schedule => schedule != null)
                .Where(schedule => string.IsNullOrWhiteSpace(normalizedScheduleId) ||
                                   string.Equals(schedule.Id?.Trim(), normalizedScheduleId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(schedule => schedule.TimeOfDay, StringComparer.Ordinal)
                .ThenBy(schedule => schedule.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return schedules
                .SelectMany(schedule =>
                {
                    var preset = config.ColorPresets?.FirstOrDefault(candidate =>
                        candidate != null &&
                        string.Equals(candidate.Name?.Trim(), schedule.PresetName?.Trim(), StringComparison.OrdinalIgnoreCase));
                    var effectiveDuration = HueSceneAutomationService.GetEffectiveDurationSeconds(schedule, preset);
                    var effectiveTransition = HueSceneAutomationService.GetEffectiveTransitionSeconds(schedule, preset);
                    var effectiveTransitionOut = HueSceneAutomationService.GetEffectiveTransitionOutSeconds(schedule, preset);
                    PluginConfiguration.TryNormalizeColorPresetEffect(preset?.Effect, out var effect);
                    return HueSceneAutomationService.GetUpcomingOccurrences(
                            schedule,
                            serverLocalNow,
                            Math.Min(boundedLimit, HueSceneAutomationService.MaxUpcomingOccurrencesPerSchedule),
                            boundedDays,
                            includeFutureStartBeyondHorizon: false,
                            transitionSeconds: effectiveTransition,
                            transitionOutSeconds: effectiveTransitionOut,
                            effectSpeedPercent: preset == null
                                ? PluginConfiguration.DefaultColorPresetEffectSpeedPercent
                                : PluginConfiguration.ClampColorPresetEffectSpeedPercent(preset.EffectSpeedPercent))
                        .Select(occurrence => new HueSceneScheduleOccurrenceResult
                        {
                            ScheduleId = occurrence.ScheduleId,
                            ScheduleName = occurrence.ScheduleName,
                            PresetName = occurrence.PresetName,
                            Effect = effect,
                            EffectSpeedPercent = preset == null
                                ? PluginConfiguration.DefaultColorPresetEffectSpeedPercent
                                : PluginConfiguration.ClampColorPresetEffectSpeedPercent(preset.EffectSpeedPercent),
                            Recurrence = PluginConfiguration.TryNormalizeSceneScheduleRecurrence(
                                schedule.Recurrence,
                                out var normalizedRecurrence)
                                ? normalizedRecurrence
                                : schedule.Recurrence?.Trim() ?? string.Empty,
                            RecurrenceInterval = schedule.RecurrenceInterval,
                            DayOfMonth = schedule.DayOfMonth,
                            MonthOfYear = schedule.MonthOfYear,
                            WeekOfMonth = schedule.WeekOfMonth,
                            DayOfWeek = schedule.DayOfWeek,
                            DurationSeconds = effectiveDuration,
                            TransitionSeconds = occurrence.TransitionSeconds,
                            TransitionOutSeconds = occurrence.TransitionOutSeconds,
                            TargetLabel = ToSceneScheduleResult(schedule, config).TargetLabel,
                            TimeZoneId = occurrence.TimeZoneId,
                            TimeZoneDisplayName = occurrence.TimeZoneDisplayName,
                            LocalTime = occurrence.LocalTime,
                            UtcTime = occurrence.UtcTime
                        });
                })
                .OrderBy(occurrence => occurrence.UtcTime)
                .ThenBy(occurrence => occurrence.ScheduleName, StringComparer.OrdinalIgnoreCase)
                .Take(boundedLimit)
                .ToArray();
        }

        private static string BuildSceneScheduleCalendar(
            IReadOnlyList<HueSceneScheduleOccurrenceResult> occurrences,
            DateTime generatedAtUtc,
            int horizonDays)
        {
            var builder = new StringBuilder();
            AppendIcsLine(builder, "BEGIN", "VCALENDAR");
            AppendIcsLine(builder, "VERSION", "2.0");
            AppendIcsLine(builder, "PRODID", "-//MCP Capital LLC//Jellyfin Hue Sync//EN");
            AppendIcsLine(builder, "CALSCALE", "GREGORIAN");
            AppendIcsLine(builder, "METHOD", "PUBLISH");
            AppendIcsLine(builder, "X-WR-CALNAME", "Jellyfin Hue Scene Cues");
            AppendIcsLine(builder, "X-WR-CALDESC", $"Upcoming credential-free Hue scene cues for the next {horizonDays} days.");

            foreach (var occurrence in occurrences)
            {
                var utcStart = DateTime.SpecifyKind(occurrence.UtcTime, DateTimeKind.Utc);
                var durationSeconds = occurrence.DurationSeconds;
                durationSeconds = Math.Clamp(
                    durationSeconds,
                    PluginConfiguration.MinPreviewDurationSeconds,
                    PluginConfiguration.MaxPreviewDurationSeconds);

                AppendIcsLine(builder, "BEGIN", "VEVENT");
                AppendIcsLine(builder, "UID", BuildIcsUid(occurrence.ScheduleId, utcStart));
                AppendIcsLine(builder, "DTSTAMP", FormatIcsUtc(generatedAtUtc));
                AppendIcsLine(builder, "DTSTART", FormatIcsUtc(utcStart));
                AppendIcsLine(builder, "DTEND", FormatIcsUtc(utcStart.AddSeconds(durationSeconds)));
                AppendIcsLine(builder, "SUMMARY", occurrence.ScheduleName);
                AppendIcsLine(
                    builder,
                    "DESCRIPTION",
                    $"Scene: {occurrence.PresetName}; Target: {occurrence.TargetLabel}; Time zone: {occurrence.TimeZoneDisplayName}");
                AppendIcsLine(builder, "X-HUE-TIMEZONE", occurrence.TimeZoneId);
                AppendIcsLine(builder, "X-HUE-RECURRENCE", occurrence.Recurrence);
                AppendIcsLine(builder, "X-HUE-RECURRENCE-INTERVAL", occurrence.RecurrenceInterval.ToString(CultureInfo.InvariantCulture));
                AppendIcsLine(builder, "X-HUE-EFFECT", occurrence.Effect);
                AppendIcsLine(builder, "X-HUE-EFFECT-SPEED-PERCENT", occurrence.EffectSpeedPercent.ToString(CultureInfo.InvariantCulture));
                AppendIcsLine(builder, "X-HUE-TRANSITION-SECONDS", occurrence.TransitionSeconds.ToString(CultureInfo.InvariantCulture));
                AppendIcsLine(builder, "X-HUE-TRANSITION-OUT-SECONDS", occurrence.TransitionOutSeconds.ToString(CultureInfo.InvariantCulture));
                AppendIcsLine(builder, "STATUS", "CONFIRMED");
                AppendIcsLine(builder, "TRANSP", "TRANSPARENT");
                AppendIcsLine(builder, "END", "VEVENT");
            }

            AppendIcsLine(builder, "END", "VCALENDAR");
            return builder.ToString();
        }

        private static string BuildIcsUid(string? scheduleId, DateTime utcStart)
        {
            var encodedId = Convert.ToBase64String(Encoding.UTF8.GetBytes(scheduleId?.Trim() ?? string.Empty))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            return $"{(string.IsNullOrWhiteSpace(encodedId) ? "cue" : encodedId)}-{utcStart.Ticks}@jellyfin-hue";
        }

        private static string FormatIcsUtc(DateTime value)
        {
            return DateTime.SpecifyKind(value, DateTimeKind.Utc)
                .ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        }

        private static void AppendIcsLine(StringBuilder builder, string name, string? value)
        {
            var line = $"{name}:{EscapeIcsText(value)}";
            while (line.Length > 75)
            {
                builder.Append(line, 0, 75).Append("\r\n");
                line = " " + line[75..];
            }

            builder.Append(line).Append("\r\n");
        }

        private static string EscapeIcsText(string? value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace(";", "\\;", StringComparison.Ordinal)
                .Replace(",", "\\,", StringComparison.Ordinal)
                .Replace("\r\n", "\\n", StringComparison.Ordinal)
                .Replace("\r", "\\n", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);
        }

        /// <summary>
        /// Returns bounded sanitized run history for scheduled scene cues. Bridge
        /// credentials and connection details are never retained or serialized.
        /// </summary>
        [HttpGet("SceneSchedules/History")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<HueSceneScheduleHistoryResult> GetSceneScheduleHistory(
            [FromQuery(Name = "limit")] int limit = HueSceneAutomationService.MaxSceneScheduleHistoryCount,
            [FromQuery(Name = "scheduleId")] string? scheduleId = null)
        {
            var boundedLimit = Math.Clamp(limit, 1, HueSceneAutomationService.MaxSceneScheduleHistoryCount);
            var normalizedScheduleId = string.IsNullOrWhiteSpace(scheduleId) ? null : scheduleId.Trim();
            return Ok(new HueSceneScheduleHistoryResult
            {
                ServiceAvailable = _sceneAutomationService != null,
                PersistenceEnabled = Plugin.Instance?.Configuration.PersistSceneScheduleHistory ?? false,
                Limit = boundedLimit,
                ScheduleIdFilter = normalizedScheduleId,
                GeneratedAtUtc = DateTime.UtcNow,
                Runs = _sceneAutomationService?.GetHistory(boundedLimit, normalizedScheduleId)
                    ?? Array.Empty<HueSceneAutomationRunResult>()
            });
        }

        /// <summary>
        /// Returns the same sanitized cue history document used by the administrator
        /// export action. Exporting does not add retention or expose credentials.
        /// </summary>
        [HttpGet("SceneSchedules/History/Export")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<HueSceneScheduleHistoryResult> ExportSceneScheduleHistory(
            [FromQuery(Name = "limit")] int limit = HueSceneAutomationService.MaxSceneScheduleHistoryCount,
            [FromQuery(Name = "scheduleId")] string? scheduleId = null)
        {
            return GetSceneScheduleHistory(limit, scheduleId);
        }

        /// <summary>
        /// Clears retained scheduled-scene run history without stopping an active cue.
        /// </summary>
        [HttpDelete("SceneSchedules/History")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<HueSceneScheduleHistoryClearResult> ClearSceneScheduleHistory()
        {
            return Ok(new HueSceneScheduleHistoryClearResult
            {
                ServiceAvailable = _sceneAutomationService != null,
                ClearedCount = _sceneAutomationService?.ClearHistory() ?? 0,
                ClearedAtUtc = DateTime.UtcNow
            });
        }

        /// <summary>
        /// Saves or updates a scene cue. The cue references an existing saved scene and
        /// a global or per-user target; a populated run date makes it one-time, while a
        /// blank run date uses the requested daily, weekly, monthly-day, monthly-weekday, or yearly recurrence.
        /// Bridge credentials
        /// are never accepted.
        /// </summary>
        [HttpPost("SceneSchedules")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<HueSceneScheduleResult> SaveSceneSchedule(
            [FromBody] HueSceneScheduleRequest? request)
        {
            if (request == null)
                return BadRequest("Scene schedule is required.");

            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
                return NotFound("Plugin configuration not available.");

            var schedule = request.ToConfigurationSchedule();
            if (string.IsNullOrWhiteSpace(schedule.Id))
                schedule.Id = Guid.NewGuid().ToString("N");
            if (PluginConfiguration.TryNormalizeSceneScheduleTime(schedule.TimeOfDay, out var normalizedTime))
                schedule.TimeOfDay = normalizedTime;
            if (PluginConfiguration.TryNormalizeSceneScheduleRecurrence(schedule.Recurrence, out var normalizedRecurrence))
                schedule.Recurrence = normalizedRecurrence;
            if (PluginConfiguration.TryNormalizeSceneScheduleDate(schedule.StartDate, out var normalizedStartDate))
                schedule.StartDate = normalizedStartDate;
            if (PluginConfiguration.TryNormalizeSceneScheduleDate(schedule.EndDate, out var normalizedEndDate))
                schedule.EndDate = normalizedEndDate;
            if (PluginConfiguration.TryNormalizeSceneScheduleDate(schedule.RunDate, out var normalizedRunDate))
                schedule.RunDate = normalizedRunDate;
            if (PluginConfiguration.TryNormalizeSceneScheduleExcludedDates(
                    schedule.ExcludedDates,
                    out var normalizedExcludedDates))
            {
                schedule.ExcludedDates = normalizedExcludedDates;
            }
            if (schedule.MaxRuns > 0 && schedule.RunCount >= schedule.MaxRuns)
                schedule.Enabled = false;

            var previousSchedules = config.SceneSchedules ?? new List<HueSceneSchedule>();
            var candidateSchedules = previousSchedules
                .Where(existing => existing != null)
                .Select(CloneSceneSchedule)
                .ToList();
            var existingIndex = candidateSchedules.FindIndex(existing =>
                string.Equals(existing.Id?.Trim(), schedule.Id.Trim(), StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                if (!request.MaxRuns.HasValue)
                    schedule.MaxRuns = candidateSchedules[existingIndex].MaxRuns;
                if (!request.RunCount.HasValue)
                    schedule.RunCount = candidateSchedules[existingIndex].RunCount;
                candidateSchedules[existingIndex] = schedule;
            }
            else
                candidateSchedules.Add(schedule);

            if (schedule.MaxRuns > 0 && schedule.RunCount >= schedule.MaxRuns)
                schedule.Enabled = false;

            config.SceneSchedules = candidateSchedules;
            var validationErrors = config.ValidateSceneSchedules();
            config.SceneSchedules = previousSchedules;
            if (validationErrors.Count > 0)
            {
                return BadRequest(new
                {
                    message = "Scene schedule is invalid.",
                    errors = validationErrors
                });
            }

            config.SceneSchedules = candidateSchedules;
            try
            {
                plugin.SaveConfiguration();
            }
            catch
            {
                config.SceneSchedules = previousSchedules;
                throw;
            }

            return Ok(ToSceneScheduleResult(schedule, config));
        }

        /// <summary>
        /// Creates a safe, disabled copy of one scene cue. The copy preserves its
        /// timing, target, recurrence, effect, and finite limit, but receives a new ID,
        /// a unique name, and a fresh execution counter so it can be edited independently
        /// without duplicating an already-consumed cue or firing unexpectedly.
        /// </summary>
        [HttpPost("SceneSchedules/{id}/Duplicate")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public ActionResult<HueSceneScheduleResult> DuplicateSceneSchedule(string id)
        {
            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
                return NotFound("Plugin configuration not available.");

            config.SceneSchedules ??= new List<HueSceneSchedule>();
            var source = config.SceneSchedules.FirstOrDefault(schedule =>
                schedule != null &&
                string.Equals(schedule.Id?.Trim(), id?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (source == null)
                return NotFound("Scene schedule not found.");

            if (config.SceneSchedules.Count >= PluginConfiguration.MaxSceneSchedules)
            {
                return Conflict(
                    $"No more than {PluginConfiguration.MaxSceneSchedules} scene schedules may be saved.");
            }

            var duplicate = CloneSceneSchedule(source);
            duplicate.Id = Guid.NewGuid().ToString("N");
            duplicate.Name = BuildDuplicateSceneScheduleName(config.SceneSchedules, source.Name);
            duplicate.RunCount = 0;
            duplicate.Enabled = false;

            var previousSchedules = config.SceneSchedules;
            var candidateSchedules = previousSchedules
                .Where(schedule => schedule != null)
                .Select(CloneSceneSchedule)
                .ToList();
            candidateSchedules.Add(duplicate);
            config.SceneSchedules = candidateSchedules;
            var validationErrors = config.ValidateSceneSchedules();
            if (validationErrors.Count > 0)
            {
                config.SceneSchedules = previousSchedules;
                return BadRequest(new
                {
                    message = "The scene cue copy is invalid.",
                    errors = validationErrors
                });
            }

            try
            {
                plugin.SaveConfiguration();
            }
            catch (Exception ex)
            {
                config.SceneSchedules = previousSchedules;
                _logger?.LogError(ex, "Could not persist duplicate Hue scene schedule {0}", source.Name);
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    "The scene cue copy could not be saved.");
            }

            return Ok(ToSceneScheduleResult(duplicate, config));
        }

        /// <summary>
        /// Deletes one scene cue by its stable ID.
        /// </summary>
        [HttpDelete("SceneSchedules/{id}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult DeleteSceneSchedule(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return NotFound("Scene schedule not found.");

            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return NotFound("Plugin configuration not available.");

            config.SceneSchedules ??= new List<HueSceneSchedule>();
            var removed = config.SceneSchedules.RemoveAll(schedule =>
                schedule != null &&
                string.Equals(schedule.Id?.Trim(), id.Trim(), StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
                return NotFound("Scene schedule not found.");

            Plugin.Instance?.SaveConfiguration();
            return Ok(new { message = "Scene schedule deleted successfully." });
        }

        /// <summary>
        /// Runs a saved scene cue immediately through the same serialized, restorative
        /// preview lifecycle used by the administrator preview button.
        /// </summary>
        [HttpPost("SceneSchedules/{id}/Run")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<ActionResult<HueSceneAutomationRunResult>> RunSceneSchedule(
            string id,
            CancellationToken cancellationToken = default)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return NotFound("Plugin configuration not available.");

            var scheduleExists = config.SceneSchedules?.Any(schedule =>
                schedule != null &&
                string.Equals(schedule.Id?.Trim(), id?.Trim(), StringComparison.OrdinalIgnoreCase)) == true;
            if (!scheduleExists)
            {
                return NotFound("Scene schedule not found.");
            }

            if (_sceneAutomationService == null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Scene automation service is not available.");

            return Ok(await _sceneAutomationService.RunScheduleAsync(id, cancellationToken).ConfigureAwait(false));
        }

        /// <summary>
        /// Requests cancellation of a manually started scene cue. The active cue continues
        /// through the normal bridge deactivation and light-restoration cleanup lifecycle.
        /// </summary>
        [HttpPost("SceneSchedules/{id}/Cancel")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public ActionResult<HueSceneScheduleCancellationResult> CancelSceneSchedule(string id)
        {
            if (Plugin.Instance?.Configuration == null)
                return NotFound("Plugin configuration not available.");

            if (_sceneAutomationService == null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Scene automation service is not available.");

            var scheduleExists = Plugin.Instance.Configuration.SceneSchedules?.Any(schedule =>
                schedule != null &&
                string.Equals(schedule.Id?.Trim(), id?.Trim(), StringComparison.OrdinalIgnoreCase)) == true;
            if (!scheduleExists)
                return NotFound("Scene schedule not found.");

            var canceled = _sceneAutomationService.CancelSchedule(id);
            return Ok(new HueSceneScheduleCancellationResult
            {
                Canceled = canceled,
                Message = canceled
                    ? "Cancellation requested; the scene will restore the bridge state before ending."
                    : "No manually started scene run is active for this cue."
            });
        }

        /// <summary>
        /// Resets a finite cue's persisted execution counter and re-enables the cue. Retained
        /// history remains available as an audit trail; an active cue must finish first.
        /// </summary>
        [HttpPost("SceneSchedules/{id}/ResetRunCount")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public ActionResult<HueSceneScheduleResult> ResetSceneScheduleRunCount(string id)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return NotFound("Plugin configuration not available.");

            var schedule = config.SceneSchedules?.FirstOrDefault(candidate =>
                candidate != null &&
                string.Equals(candidate.Id?.Trim(), id?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (schedule == null)
                return NotFound("Scene schedule not found.");

            if (_sceneAutomationService != null)
            {
                if (!_sceneAutomationService.TryResetScheduleRunCount(id, out var message))
                    return Conflict(message);
            }
            else
            {
                schedule.RunCount = 0;
                schedule.Enabled = true;
                Plugin.Instance?.SaveConfiguration();
            }

            return Ok(ToSceneScheduleResult(schedule, config));
        }

        [HttpGet("Status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<HueSyncStatus> GetStatus()
        {
            var config = Plugin.Instance?.Configuration;
            var runtime = _syncService?.GetRuntimeStatus();
            var sessions = _syncService?.GetPlaybackRuntimeStatuses() ?? Array.Empty<HueRuntimeStatus>();
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
                SeekRestartCount = runtime?.SeekRestartCount ?? 0,
                LastSeekPositionSeconds = runtime?.LastSeekPositionSeconds,
                CanStopSync = runtime?.CanStopSync ?? false,
                IsFfmpegHealthy = runtime?.IsFfmpegHealthy ?? false,
                IsDtlsHealthy = runtime?.IsDtlsHealthy ?? false,
                SyncDurationSeconds = runtime?.SyncDurationSeconds,
                SyncStartedAtUtc = runtime?.SyncStartedAtUtc,
                LastSession = runtime?.LastSession,
                Sessions = sessions
            };

            return Ok(status);
        }

        /// <summary>
        /// Returns a bounded, newest-first history of completed Hue playback sessions.
        /// Summaries contain aggregate telemetry and target labels only; bridge keys and
        /// Jellyfin playback tokens are never retained or serialized. An optional outcome
        /// filter narrows the result for administrator diagnostics.
        /// </summary>
        [HttpGet("History")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<HueSessionHistoryResult> GetSessionHistory(
            [FromQuery(Name = "limit")] int limit = 20,
            [FromQuery(Name = "outcome")] string? outcome = null)
        {
            return Ok(BuildSessionHistoryResult(limit, outcome));
        }

        /// <summary>
        /// Returns the same sanitized history document used by the administrator export
        /// action. Exporting does not add retention or expose credentials.
        /// </summary>
        [HttpGet("History/Export")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<HueSessionHistoryResult> ExportSessionHistory(
            [FromQuery(Name = "limit")] int limit = HueSyncService.MaxSessionHistoryCount,
            [FromQuery(Name = "outcome")] string? outcome = null)
        {
            return Ok(BuildSessionHistoryResult(limit, outcome));
        }

        /// <summary>
        /// Clears retained completed-session summaries without stopping active playback.
        /// </summary>
        [HttpDelete("History")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<HueSessionHistoryClearResult> ClearSessionHistory()
        {
            return Ok(new HueSessionHistoryClearResult
            {
                ServiceAvailable = _syncService != null,
                ClearedCount = _syncService?.ClearSessionHistory() ?? 0,
                ClearedAtUtc = DateTime.UtcNow
            });
        }

        private HueSessionHistoryResult BuildSessionHistoryResult(int limit, string? outcome)
        {
            var boundedLimit = Math.Clamp(limit, 1, HueSyncService.MaxSessionHistoryCount);
            var normalizedOutcome = string.IsNullOrWhiteSpace(outcome) ? null : outcome.Trim();
            return new HueSessionHistoryResult
            {
                ServiceAvailable = _syncService != null,
                Limit = boundedLimit,
                OutcomeFilter = normalizedOutcome,
                GeneratedAtUtc = DateTime.UtcNow,
                Sessions = _syncService?.GetSessionHistory(boundedLimit, normalizedOutcome) ?? Array.Empty<HueSessionSummary>()
            };
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
            using var diagnosticsOperation = _diagnosticsCancellationGate.Begin(cancellationToken);
            var diagnosticsCancellationToken = diagnosticsOperation.Token;
            var environment = await _environmentProbe.CheckAsync(diagnosticsCancellationToken).ConfigureAwait(false);
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
                    !diagnosticActive,
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
            using var diagnosticsOperation = _diagnosticsCancellationGate.Begin(cancellationToken);
            var diagnosticsCancellationToken = diagnosticsOperation.Token;
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
                diagnosticsCancellationToken.ThrowIfCancellationRequested();
                results.Add(await ValidateTargetAsync(
                    target,
                    areaRequests,
                    configurationRequests,
                    diagnosticsCancellationToken).ConfigureAwait(false));
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

        /// <summary>
        /// Requests cancellation for active non-mutating administrator diagnostics. The
        /// diagnostic request owns its normal disposal and returns no bridge credentials.
        /// </summary>
        [HttpPost("Diagnostics/Cancel")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<HueDiagnosticsCancellationResult> CancelDiagnostics()
        {
            var canceledCount = _diagnosticsCancellationGate.CancelActive();
            return Ok(new HueDiagnosticsCancellationResult
            {
                Canceled = canceledCount > 0,
                CanceledCount = canceledCount,
                Message = canceledCount > 0
                    ? $"Cancellation requested for {canceledCount} active diagnostic operation(s)."
                    : "No active administrator diagnostics were found."
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
        public async Task<ActionResult> StopSync(
            [FromQuery(Name = "playSessionId")] string? playSessionId = null)
        {
            if (_syncService == null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Hue sync service is not available.");

            if (!await _syncService.StopCurrentSyncAsync(playSessionId))
                return Conflict("There is no active playback sync session to stop.");

            return Ok(new
            {
                message = "Hue sync stopped; playback continues.",
                playSessionId
            });
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
        /// Exports a credential-safe configuration document. Global and per-user secrets
        /// are represented only by presence flags; administrators can re-enter replacement
        /// keys in an import document when moving the configuration to another server.
        /// </summary>
        [HttpGet("Configuration/Export")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<HueConfigurationExportDocument> ExportConfiguration()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return NotFound("Plugin configuration not available.");

            return Ok(HueConfigurationExportDocument.From(config));
        }

        /// <summary>
        /// Imports global settings, per-user profiles, color scenes, and scheduled scene
        /// cues atomically. Blank
        /// global or mapping keys preserve credentials already stored for the same target;
        /// secrets included explicitly in an import are accepted but never echoed back.
        /// </summary>
        [HttpPost("Configuration/Import")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public ActionResult<HueConfigurationImportResult> ImportConfiguration(
            [FromBody] HueConfigurationImportRequest? request)
        {
            if (request == null || request.Configuration == null)
                return BadRequest("A configuration export document is required.");

            if (request.SchemaVersion != HueConfigurationExportDocument.CurrentSchemaVersion)
            {
                return BadRequest($"Unsupported configuration schema version {request.SchemaVersion}. Expected {HueConfigurationExportDocument.CurrentSchemaVersion}.");
            }

            if (_syncService?.HasActivePlaybackSessions == true)
            {
                return Conflict("Stop all active Hue playback sessions before importing configuration.");
            }

            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
                return NotFound("Plugin configuration not available.");

            var existingMappings = (config.UserMappings ?? new List<UserBridgeMapping>())
                .Where(mapping => mapping != null)
                .ToList();
            var existingPresets = (config.ColorPresets ?? new List<HueColorPreset>())
                .Where(preset => preset != null)
                .ToList();
            var existingSchedules = (config.SceneSchedules ?? new List<HueSceneSchedule>())
                .Where(schedule => schedule != null)
                .Select(CloneSceneSchedule)
                .ToList();
            var importedMappings = request.UserMappings ?? new List<UserBridgeMappingImport>();
            var importedPresets = request.ColorPresets ?? new List<HueColorPresetRequest>();
            var importedSchedules = request.SceneSchedules ?? new List<HueSceneScheduleRequest>();
            var validationErrors = new List<string>();
            var seenUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mappingCredentialPairsPreserved = 0;

            var importedMappingValues = new List<UserBridgeMapping>();
            for (var index = 0; index < importedMappings.Count; index++)
            {
                var source = importedMappings[index];
                if (source == null)
                {
                    validationErrors.Add($"User mapping {index + 1} is required.");
                    continue;
                }

                var existing = existingMappings.FirstOrDefault(candidate =>
                    string.Equals(candidate.UserId?.Trim(), source.UserId?.Trim(), StringComparison.OrdinalIgnoreCase));
                var imported = ToImportedMapping(source, existing, out var preservedCredentialPair);
                mappingCredentialPairsPreserved += preservedCredentialPair ? 1 : 0;
                importedMappingValues.Add(imported);

                var label = string.IsNullOrWhiteSpace(imported.UserName)
                    ? $"User mapping {index + 1}"
                    : $"User mapping for '{imported.UserName}'";
                validationErrors.AddRange(ValidateImportedMapping(imported, label));
                if (!string.IsNullOrWhiteSpace(imported.UserId) && !seenUserIds.Add(imported.UserId.Trim()))
                    validationErrors.Add($"{label} duplicates another imported user mapping.");
            }

            var candidateMappings = request.ReplaceMappings
                ? importedMappingValues
                : MergeMappings(existingMappings, importedMappingValues);

            var candidatePresets = request.ReplaceColorPresets
                ? new List<HueColorPreset>()
                : new List<HueColorPreset>(existingPresets);
            var seenPresetNames = new HashSet<string>(
                candidatePresets
                    .Where(preset => preset != null && !string.IsNullOrWhiteSpace(preset.Name))
                    .Select(preset => preset.Name.Trim()),
                StringComparer.OrdinalIgnoreCase);
            foreach (var presetRequest in importedPresets)
            {
                var preset = presetRequest?.ToConfigurationPreset();
                validationErrors.AddRange(PluginConfiguration.ValidateColorPreset(
                    preset,
                    string.IsNullOrWhiteSpace(preset?.Name) ? "Imported color preset" : $"Imported color preset '{preset.Name.Trim()}'"));
                if (preset == null)
                    continue;

                var existingIndex = candidatePresets.FindIndex(existing =>
                    string.Equals(existing?.Name?.Trim(), preset.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (existingIndex >= 0)
                {
                    candidatePresets[existingIndex] = preset;
                }
                else if (!seenPresetNames.Add(preset.Name.Trim()))
                {
                    validationErrors.Add($"Imported color preset '{preset.Name.Trim()}' duplicates another preset name.");
                }
                else
                {
                    candidatePresets.Add(preset);
                }
            }

            if (candidatePresets.Count > PluginConfiguration.MaxColorPresets)
            {
                validationErrors.Add($"No more than {PluginConfiguration.MaxColorPresets} color presets may be saved.");
            }

            var candidateSchedules = request.ReplaceSceneSchedules
                ? new List<HueSceneSchedule>()
                : existingSchedules.Select(CloneSceneSchedule).ToList();
            foreach (var scheduleRequest in importedSchedules)
            {
                var schedule = scheduleRequest?.ToConfigurationSchedule() ?? new HueSceneSchedule();
                if (string.IsNullOrWhiteSpace(schedule.Id))
                    schedule.Id = Guid.NewGuid().ToString("N");
                if (PluginConfiguration.TryNormalizeSceneScheduleTime(schedule.TimeOfDay, out var normalizedTime))
                    schedule.TimeOfDay = normalizedTime;
                if (PluginConfiguration.TryNormalizeSceneScheduleRecurrence(schedule.Recurrence, out var normalizedRecurrence))
                    schedule.Recurrence = normalizedRecurrence;
                if (PluginConfiguration.TryNormalizeSceneScheduleDate(schedule.StartDate, out var normalizedStartDate))
                    schedule.StartDate = normalizedStartDate;
                if (PluginConfiguration.TryNormalizeSceneScheduleDate(schedule.EndDate, out var normalizedEndDate))
                    schedule.EndDate = normalizedEndDate;
                if (PluginConfiguration.TryNormalizeSceneScheduleDate(schedule.RunDate, out var normalizedRunDate))
                    schedule.RunDate = normalizedRunDate;
                if (PluginConfiguration.TryNormalizeSceneScheduleExcludedDates(
                        schedule.ExcludedDates,
                        out var normalizedExcludedDates))
                {
                    schedule.ExcludedDates = normalizedExcludedDates;
                }
                if (schedule.MaxRuns > 0 && schedule.RunCount >= schedule.MaxRuns)
                    schedule.Enabled = false;

                var existingIndex = candidateSchedules.FindIndex(existing =>
                    string.Equals(existing.Id?.Trim(), schedule.Id.Trim(), StringComparison.OrdinalIgnoreCase));
                if (existingIndex >= 0)
                {
                    if (scheduleRequest != null && !scheduleRequest.MaxRuns.HasValue)
                        schedule.MaxRuns = candidateSchedules[existingIndex].MaxRuns;
                    if (scheduleRequest != null && !scheduleRequest.RunCount.HasValue)
                        schedule.RunCount = candidateSchedules[existingIndex].RunCount;
                    candidateSchedules[existingIndex] = schedule;
                }
                else
                    candidateSchedules.Add(schedule);

                if (schedule.MaxRuns > 0 && schedule.RunCount >= schedule.MaxRuns)
                    schedule.Enabled = false;
            }

            var scheduleValidationConfiguration = new PluginConfiguration
            {
                ColorPresets = candidatePresets,
                UserMappings = candidateMappings,
                SceneSchedules = candidateSchedules
            };
            validationErrors.AddRange(scheduleValidationConfiguration.ValidateSceneSchedules());

            if (validationErrors.Count > 0)
                return BadRequest(new { message = "Configuration import is invalid.", errors = validationErrors });

            var previousSettings = HuePluginConfigurationSettings.From(config);
            var previousAppKey = config.HueAppKey;
            var previousClientKey = config.HueClientKey;
            var previousMappings = config.UserMappings ?? new List<UserBridgeMapping>();
            var previousPresets = config.ColorPresets ?? new List<HueColorPreset>();
            var previousSchedules = config.SceneSchedules ?? new List<HueSceneSchedule>();
            var globalAppKeyPreserved = string.IsNullOrWhiteSpace(request.Configuration.HueAppKey) &&
                !request.Configuration.ClearStoredCredentials &&
                !string.IsNullOrWhiteSpace(previousAppKey);
            var globalClientKeyPreserved = string.IsNullOrWhiteSpace(request.Configuration.HueClientKey) &&
                !request.Configuration.ClearStoredCredentials &&
                !string.IsNullOrWhiteSpace(previousClientKey);

            request.Configuration.ApplyTo(config);
            config.UserMappings = candidateMappings;
            config.ColorPresets = candidatePresets;
            config.SceneSchedules = candidateSchedules;
            var completeValidationErrors = config.Validate();
            if (completeValidationErrors.Count > 0)
            {
                previousSettings.ApplyTo(config);
                config.HueAppKey = previousAppKey;
                config.HueClientKey = previousClientKey;
                config.UserMappings = previousMappings;
                config.ColorPresets = previousPresets;
                config.SceneSchedules = previousSchedules;
                return BadRequest(new
                {
                    message = "Configuration import is invalid.",
                    errors = completeValidationErrors
                });
            }

            try
            {
                plugin.SaveConfiguration();
                _syncService?.RefreshSessionHistoryPersistence();
                _sceneAutomationService?.RefreshSceneScheduleHistoryPersistence();
            }
            catch (Exception ex)
            {
                previousSettings.ApplyTo(config);
                config.HueAppKey = previousAppKey;
                config.HueClientKey = previousClientKey;
                config.UserMappings = previousMappings;
                config.ColorPresets = previousPresets;
                config.SceneSchedules = previousSchedules;
                // Keep the response credential-free while retaining the exception in the
                // server log for the administrator's normal Jellyfin diagnostics.
                _logger?.LogError(ex, "Could not persist imported Hue configuration");
                return StatusCode(StatusCodes.Status500InternalServerError, "Configuration could not be saved.");
            }

            return Ok(new HueConfigurationImportResult
            {
                MappingsImported = importedMappingValues.Count,
                ColorPresetsImported = importedPresets.Count,
                SceneSchedulesImported = importedSchedules.Count,
                TotalMappings = candidateMappings.Count,
                TotalColorPresets = candidatePresets.Count,
                TotalSceneSchedules = candidateSchedules.Count,
                GlobalAppKeyPreserved = globalAppKeyPreserved,
                GlobalClientKeyPreserved = globalClientKeyPreserved,
                MappingCredentialPairsPreserved = mappingCredentialPairsPreserved,
                Message = "Configuration imported. Stored credentials were preserved when the imported document omitted them."
            });
        }

        private static List<UserBridgeMapping> MergeMappings(
            IEnumerable<UserBridgeMapping> existingMappings,
            IEnumerable<UserBridgeMapping> importedMappings)
        {
            var merged = new List<UserBridgeMapping>(existingMappings);
            foreach (var imported in importedMappings)
            {
                merged.RemoveAll(existing =>
                    string.Equals(existing.UserId?.Trim(), imported.UserId?.Trim(), StringComparison.OrdinalIgnoreCase));
                merged.Add(imported);
            }

            return merged;
        }

        private static UserBridgeMapping ToImportedMapping(
            UserBridgeMappingImport source,
            UserBridgeMapping? existing,
            out bool preservedCredentialPair)
        {
            var mapping = new UserBridgeMapping
            {
                UserId = source.UserId?.Trim() ?? string.Empty,
                UserName = source.UserName?.Trim() ?? string.Empty,
                SyncEnabled = source.SyncEnabled,
                HueBridgeIp = source.HueBridgeIp?.Trim() ?? string.Empty,
                HueAppKey = source.HueAppKey?.Trim() ?? string.Empty,
                HueClientKey = source.HueClientKey?.Trim() ?? string.Empty,
                EntertainmentAreaId = source.EntertainmentAreaId?.Trim() ?? string.Empty,
                EntertainmentAreaName = source.EntertainmentAreaName?.Trim() ?? string.Empty,
                UseCinemaModeOverride = source.UseCinemaModeOverride,
                BrightnessDimLevelOverride = source.BrightnessDimLevelOverride,
                PauseBehaviorOverride = source.PauseBehaviorOverride?.Trim(),
                RestoreLightStateOverride = source.RestoreLightStateOverride,
                BrightnessBoostOverride = source.BrightnessBoostOverride,
                RedGainOverride = source.RedGainOverride,
                GreenGainOverride = source.GreenGainOverride,
                BlueGainOverride = source.BlueGainOverride,
                ColorSaturationOverride = source.ColorSaturationOverride,
                HueShiftDegreesOverride = source.HueShiftDegreesOverride,
                OutputBrightnessPercentOverride = source.OutputBrightnessPercentOverride,
                BlackoutThresholdOverride = source.BlackoutThresholdOverride,
                ColorChangeThresholdOverride = source.ColorChangeThresholdOverride,
                UseGpuOverride = source.UseGpuOverride,
                CustomFfmpegFlagsOverride = source.CustomFfmpegFlagsOverride,
                FfmpegStallTimeoutSecondsOverride = source.FfmpegStallTimeoutSecondsOverride,
                NetworkRetryAttemptsOverride = source.NetworkRetryAttemptsOverride,
                ChannelIdsOverride = source.ChannelIdsOverride,
                TargetFpsOverride = source.TargetFpsOverride,
                FrameResolutionOverride = source.FrameResolutionOverride,
                VideoScalingModeOverride = source.VideoScalingModeOverride,
                VideoDeinterlaceModeOverride = source.VideoDeinterlaceModeOverride,
                SamplingBreadthPercentOverride = source.SamplingBreadthPercentOverride,
                SamplingModeOverride = source.SamplingModeOverride,
                ColorSmoothingPercentOverride = source.ColorSmoothingPercentOverride
            };

            preservedCredentialPair = false;
            var customTargetMatchesExisting = existing != null &&
                !string.IsNullOrWhiteSpace(mapping.HueBridgeIp) &&
                IsSameBridgeTarget(mapping.HueBridgeIp, existing.HueBridgeIp);
            if (mapping.SyncEnabled && customTargetMatchesExisting)
            {
                var existingMapping = existing!;
                if (string.IsNullOrWhiteSpace(mapping.HueAppKey) && !string.IsNullOrWhiteSpace(existingMapping.HueAppKey))
                    mapping.HueAppKey = existingMapping.HueAppKey;
                if (string.IsNullOrWhiteSpace(mapping.HueClientKey) && !string.IsNullOrWhiteSpace(existingMapping.HueClientKey))
                    mapping.HueClientKey = existingMapping.HueClientKey;
                preservedCredentialPair = string.IsNullOrWhiteSpace(source.HueAppKey) &&
                    string.IsNullOrWhiteSpace(source.HueClientKey) &&
                    (!string.IsNullOrWhiteSpace(existingMapping.HueAppKey) || !string.IsNullOrWhiteSpace(existingMapping.HueClientKey));
            }

            if (!mapping.SyncEnabled || string.IsNullOrWhiteSpace(mapping.HueBridgeIp))
            {
                mapping.HueBridgeIp = string.Empty;
                mapping.HueAppKey = string.Empty;
                mapping.HueClientKey = string.Empty;
                mapping.EntertainmentAreaId = string.Empty;
                mapping.EntertainmentAreaName = string.Empty;
            }

            return mapping;
        }

        private static List<string> ValidateImportedMapping(UserBridgeMapping mapping, string label)
        {
            var errors = new List<string>();
            errors.AddRange(PluginConfiguration.ValidatePlaybackOverrides(mapping, label));
            errors.AddRange(PluginConfiguration.ValidateColorOverrides(mapping, label));
            errors.AddRange(PluginConfiguration.ValidatePerformanceOverrides(mapping, label));
            errors.AddRange(PluginConfiguration.ValidateExecutionOverrides(mapping, label));
            errors.AddRange(PluginConfiguration.ValidateChannelOverrides(mapping, label));

            if (string.IsNullOrWhiteSpace(mapping.UserId))
                errors.Add($"{label} requires a user ID.");
            if (!mapping.SyncEnabled || string.IsNullOrWhiteSpace(mapping.HueBridgeIp))
                return errors;

            if (!HueBridgeCertificateValidation.IsValidBridgeAddress(mapping.HueBridgeIp))
                errors.Add($"{label} bridge address must be a valid private IP address or .local host name.");
            if (string.IsNullOrWhiteSpace(mapping.HueAppKey))
                errors.Add($"{label} requires a Hue App Key. Provide it in the import document; matching stored credentials are preserved automatically.");
            if (string.IsNullOrWhiteSpace(mapping.HueClientKey))
                errors.Add($"{label} requires a Hue Client Key. Provide it in the import document; matching stored credentials are preserved automatically.");
            if (string.IsNullOrWhiteSpace(mapping.EntertainmentAreaId))
                errors.Add($"{label} requires an Entertainment Area ID.");

            return errors;
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
            _syncService?.RefreshSessionHistoryPersistence();
            _sceneAutomationService?.RefreshSceneScheduleHistoryPersistence();
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
        [ProducesResponseType(StatusCodes.Status409Conflict)]
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

            var scheduledCueCount = config.SceneSchedules?.Count(schedule =>
                schedule != null &&
                string.Equals(schedule.TargetUserId?.Trim(), mapping.UserId.Trim(), StringComparison.OrdinalIgnoreCase)) ?? 0;
            if (scheduledCueCount > 0 && !mapping.SyncEnabled)
            {
                return Conflict($"This user mapping is used by {scheduledCueCount} scheduled cue(s). Delete or update those cues before disabling the mapping.");
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
        [ProducesResponseType(StatusCodes.Status409Conflict)]
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

            var scheduledCueCount = config.SceneSchedules?.Count(schedule =>
                schedule != null &&
                string.Equals(schedule.TargetUserId?.Trim(), userId.Trim(), StringComparison.OrdinalIgnoreCase)) ?? 0;
            if (scheduledCueCount > 0)
            {
                return Conflict($"This user mapping is used by {scheduledCueCount} scheduled cue(s). Delete or update those cues first.");
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
        public bool? PersistSessionHistory { get; set; }
        public bool? PersistSceneScheduleHistory { get; set; }
        public bool? SceneAutomationEnabled { get; set; }
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
                PersistSessionHistory = config.PersistSessionHistory,
                PersistSceneScheduleHistory = config.PersistSceneScheduleHistory,
                SceneAutomationEnabled = config.SceneAutomationEnabled,
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
            if (PersistSessionHistory.HasValue)
            {
                config.PersistSessionHistory = PersistSessionHistory.Value;
                if (!config.PersistSessionHistory)
                    config.PersistedSessionHistory?.Clear();
            }
            if (PersistSceneScheduleHistory.HasValue)
            {
                config.PersistSceneScheduleHistory = PersistSceneScheduleHistory.Value;
                if (!config.PersistSceneScheduleHistory)
                    config.PersistedSceneScheduleHistory?.Clear();
            }
            if (SceneAutomationEnabled.HasValue)
                config.SceneAutomationEnabled = SceneAutomationEnabled.Value;
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
    public class UserBridgeMappingSummary
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

    /// <summary>
    /// Import-only mapping shape. Export documents use <see cref="UserBridgeMappingSummary"/>
    /// so stored credentials are never serialized, while an administrator may explicitly
    /// provide replacement keys in an import request.
    /// </summary>
    public sealed class UserBridgeMappingImport : UserBridgeMappingSummary
    {
        public string HueAppKey { get; set; } = string.Empty;
        public string HueClientKey { get; set; } = string.Empty;
    }

    /// <summary>
    /// Credential-safe backup document for settings, profiles, color scenes, and
    /// scheduled scene cues.
    /// </summary>
    public sealed class HueConfigurationExportDocument
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string PluginVersion { get; set; } = string.Empty;
        public DateTime ExportedAtUtc { get; set; }
        public bool CredentialsIncluded { get; set; }
        public string CredentialNote { get; set; } = "Credential values are omitted. Re-enter replacement keys when importing to a new server; existing matching keys are preserved.";
        public HuePluginConfigurationSettings Configuration { get; set; } = new();
        public IReadOnlyList<UserBridgeMappingSummary> UserMappings { get; set; } = Array.Empty<UserBridgeMappingSummary>();
        public IReadOnlyList<HueColorPresetResult> ColorPresets { get; set; } = Array.Empty<HueColorPresetResult>();
        public IReadOnlyList<HueSceneScheduleResult> SceneSchedules { get; set; } = Array.Empty<HueSceneScheduleResult>();

        public static HueConfigurationExportDocument From(PluginConfiguration config)
        {
            return new HueConfigurationExportDocument
            {
                SchemaVersion = CurrentSchemaVersion,
                PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? string.Empty,
                ExportedAtUtc = DateTime.UtcNow,
                CredentialsIncluded = false,
                Configuration = HuePluginConfigurationSettings.From(config),
                UserMappings = (config.UserMappings ?? new List<UserBridgeMapping>())
                    .Where(mapping => mapping != null)
                    .Select(UserBridgeMappingSummary.From)
                    .ToArray(),
                ColorPresets = (config.ColorPresets ?? new List<HueColorPreset>())
                    .Where(preset => preset != null)
                    .Select(HueApiController.ToColorPresetResult)
                    .ToArray(),
                SceneSchedules = (config.SceneSchedules ?? new List<HueSceneSchedule>())
                    .Where(schedule => schedule != null)
                    .Select(schedule => HueApiController.ToSceneScheduleResult(schedule, config))
                    .ToArray()
            };
        }
    }

    /// <summary>
    /// Request shape accepted by the configuration import endpoint. It is intentionally
    /// compatible with the export document while allowing explicit replacement keys.
    /// </summary>
    public sealed class HueConfigurationImportRequest
    {
        public int SchemaVersion { get; set; } = HueConfigurationExportDocument.CurrentSchemaVersion;
        public HuePluginConfigurationSettings? Configuration { get; set; }
        public List<UserBridgeMappingImport> UserMappings { get; set; } = new();
        public List<HueColorPresetRequest> ColorPresets { get; set; } = new();
        public List<HueSceneScheduleRequest> SceneSchedules { get; set; } = new();
        public bool ReplaceMappings { get; set; } = true;
        public bool ReplaceColorPresets { get; set; } = true;
        public bool ReplaceSceneSchedules { get; set; } = true;
    }

    /// <summary>
    /// Sanitized result returned after a successful configuration import.
    /// </summary>
    public sealed class HueConfigurationImportResult
    {
        public string Message { get; set; } = string.Empty;
        public int MappingsImported { get; set; }
        public int ColorPresetsImported { get; set; }
        public int SceneSchedulesImported { get; set; }
        public int TotalMappings { get; set; }
        public int TotalColorPresets { get; set; }
        public int TotalSceneSchedules { get; set; }
        public bool GlobalAppKeyPreserved { get; set; }
        public bool GlobalClientKeyPreserved { get; set; }
        public int MappingCredentialPairsPreserved { get; set; }
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

        [JsonPropertyName("effect")]
        public string Effect { get; set; } = PluginConfiguration.ColorPresetEffectSolid;

        [JsonPropertyName("effectSpeedPercent")]
        public int EffectSpeedPercent { get; set; } = PluginConfiguration.DefaultColorPresetEffectSpeedPercent;

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

        [JsonPropertyName("transitionSeconds")]
        public int TransitionSeconds { get; set; }

        [JsonPropertyName("transitionOutSeconds")]
        public int TransitionOutSeconds { get; set; }
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

        [JsonPropertyName("effect")]
        public string Effect { get; set; } = PluginConfiguration.ColorPresetEffectSolid;

        [JsonPropertyName("effectSpeedPercent")]
        public int EffectSpeedPercent { get; set; } = PluginConfiguration.DefaultColorPresetEffectSpeedPercent;

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

        [JsonPropertyName("transitionSeconds")]
        public int TransitionSeconds { get; set; }

        [JsonPropertyName("transitionOutSeconds")]
        public int TransitionOutSeconds { get; set; }

        [JsonPropertyName("availableChannelCount")]
        public int AvailableChannelCount { get; set; }

        [JsonPropertyName("selectedChannelCount")]
        public int SelectedChannelCount { get; set; }
    }

    /// <summary>
    /// Sanitized result from requesting cancellation of an administrator preview.
    /// </summary>
    public sealed class HuePreviewCancellationResult
    {
        public bool Canceled { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    public class HueColorPresetRequest
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("effect")]
        public string Effect { get; set; } = PluginConfiguration.ColorPresetEffectSolid;

        [JsonPropertyName("effectSpeedPercent")]
        public int EffectSpeedPercent { get; set; } = PluginConfiguration.DefaultColorPresetEffectSpeedPercent;

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

        [JsonPropertyName("transitionSeconds")]
        public int TransitionSeconds { get; set; }

        [JsonPropertyName("transitionOutSeconds")]
        public int TransitionOutSeconds { get; set; }

        public HueColorPreset ToConfigurationPreset()
        {
            var normalizedEffect = PluginConfiguration.TryNormalizeColorPresetEffect(Effect, out var effect)
                ? effect
                : Effect?.Trim() ?? string.Empty;
            return new HueColorPreset
            {
                Name = Name?.Trim() ?? string.Empty,
                Effect = normalizedEffect,
                EffectSpeedPercent = EffectSpeedPercent,
                Red = Red,
                Green = Green,
                Blue = Blue,
                BrightnessPercent = BrightnessPercent,
                DurationSeconds = DurationSeconds,
                TransitionSeconds = TransitionSeconds,
                TransitionOutSeconds = TransitionOutSeconds
            };
        }
    }

    public class HueColorPresetResult
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("effect")]
        public string Effect { get; set; } = PluginConfiguration.ColorPresetEffectSolid;

        [JsonPropertyName("effectSpeedPercent")]
        public int EffectSpeedPercent { get; set; } = PluginConfiguration.DefaultColorPresetEffectSpeedPercent;

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

        [JsonPropertyName("transitionSeconds")]
        public int TransitionSeconds { get; set; }

        [JsonPropertyName("transitionOutSeconds")]
        public int TransitionOutSeconds { get; set; }
    }

    /// <summary>
    /// Request shape for one saved-scene cue. TargetUserId is blank for the global bridge
    /// target; runDate selects a one-time cue, otherwise daily, weekly, monthly-day,
    /// monthly-weekday, or yearly date rules in the selected cue timezone apply. RecurrenceInterval
    /// controls the number of calendar units between runs and requires startDate when greater than one.
    /// DurationSeconds is zero
    /// to inherit the saved scene's
    /// duration or a bounded per-cue override; the saved scene's optional fade-in and fade-out
    /// are inherited and clamped to that effective duration. MaxRuns is zero for unlimited
    /// execution or a bounded number of attempts; RunCount is optional so normal edits preserve
    /// the persisted finite-cue counter. Bridge credentials are intentionally not accepted.
    /// </summary>
    public sealed class HueSceneScheduleRequest
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("presetName")]
        public string PresetName { get; set; } = string.Empty;

        [JsonPropertyName("targetUserId")]
        public string TargetUserId { get; set; } = string.Empty;

        [JsonPropertyName("timeOfDay")]
        public string TimeOfDay { get; set; } = "20:00";

        [JsonPropertyName("timeZoneId")]
        public string TimeZoneId { get; set; } = string.Empty;

        [JsonPropertyName("recurrence")]
        public string Recurrence { get; set; } = PluginConfiguration.SceneScheduleRecurrenceWeekly;

        [JsonPropertyName("recurrenceInterval")]
        public int RecurrenceInterval { get; set; } = PluginConfiguration.MinSceneScheduleRecurrenceInterval;

        [JsonPropertyName("dayOfMonth")]
        public int DayOfMonth { get; set; }

        [JsonPropertyName("monthOfYear")]
        public int MonthOfYear { get; set; }

        [JsonPropertyName("weekOfMonth")]
        public int WeekOfMonth { get; set; }

        [JsonPropertyName("dayOfWeek")]
        public int DayOfWeek { get; set; } = -1;

        [JsonPropertyName("durationSeconds")]
        public int DurationSeconds { get; set; }

        [JsonPropertyName("maxRuns")]
        public int? MaxRuns { get; set; }

        [JsonPropertyName("runCount")]
        public int? RunCount { get; set; }

        [JsonPropertyName("runDate")]
        public string RunDate { get; set; } = string.Empty;

        [JsonPropertyName("startDate")]
        public string StartDate { get; set; } = string.Empty;

        [JsonPropertyName("endDate")]
        public string EndDate { get; set; } = string.Empty;

        [JsonPropertyName("excludedDates")]
        public List<string> ExcludedDates { get; set; } = new();

        [JsonPropertyName("daysOfWeekMask")]
        public int DaysOfWeekMask { get; set; } = PluginConfiguration.AllSceneScheduleDaysMask;

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        public HueSceneSchedule ToConfigurationSchedule()
        {
            return new HueSceneSchedule
            {
                Id = Id?.Trim() ?? string.Empty,
                Name = Name?.Trim() ?? string.Empty,
                PresetName = PresetName?.Trim() ?? string.Empty,
                TargetUserId = TargetUserId?.Trim() ?? string.Empty,
                TimeOfDay = TimeOfDay?.Trim() ?? string.Empty,
                TimeZoneId = TimeZoneId?.Trim() ?? string.Empty,
                Recurrence = Recurrence?.Trim() ?? string.Empty,
                RecurrenceInterval = RecurrenceInterval,
                DayOfMonth = DayOfMonth,
                MonthOfYear = MonthOfYear,
                WeekOfMonth = WeekOfMonth,
                DayOfWeek = DayOfWeek,
                DurationSeconds = DurationSeconds,
                MaxRuns = MaxRuns ?? 0,
                RunCount = RunCount ?? 0,
                RunDate = RunDate?.Trim() ?? string.Empty,
                StartDate = StartDate?.Trim() ?? string.Empty,
                EndDate = EndDate?.Trim() ?? string.Empty,
                ExcludedDates = (ExcludedDates ?? new List<string>())
                    .Select(value => value?.Trim() ?? string.Empty)
                    .ToList(),
                DaysOfWeekMask = DaysOfWeekMask,
                Enabled = Enabled
            };
        }
    }

    /// <summary>
    /// Credential-free scene cue returned by the administrator API, including the effective
    /// saved-scene fade-in/fade-out, optional per-cue duration override, daily, weekly, monthly-day, monthly-weekday, or yearly recurrence,
    /// bounded recurrence intervals, finite execution limits, one-time date, inclusive bounds,
    /// and normalized excluded calendar dates.
    /// </summary>
    public sealed class HueSceneScheduleResult
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("presetName")]
        public string PresetName { get; set; } = string.Empty;

        [JsonPropertyName("effect")]
        public string Effect { get; set; } = PluginConfiguration.ColorPresetEffectSolid;

        [JsonPropertyName("effectSpeedPercent")]
        public int EffectSpeedPercent { get; set; } = PluginConfiguration.DefaultColorPresetEffectSpeedPercent;

        [JsonPropertyName("targetUserId")]
        public string TargetUserId { get; set; } = string.Empty;

        [JsonPropertyName("targetLabel")]
        public string TargetLabel { get; set; } = string.Empty;

        [JsonPropertyName("timeOfDay")]
        public string TimeOfDay { get; set; } = string.Empty;

        [JsonPropertyName("timeZoneId")]
        public string TimeZoneId { get; set; } = string.Empty;

        [JsonPropertyName("recurrence")]
        public string Recurrence { get; set; } = PluginConfiguration.SceneScheduleRecurrenceWeekly;

        [JsonPropertyName("recurrenceInterval")]
        public int RecurrenceInterval { get; set; } = PluginConfiguration.MinSceneScheduleRecurrenceInterval;

        [JsonPropertyName("dayOfMonth")]
        public int DayOfMonth { get; set; }

        [JsonPropertyName("monthOfYear")]
        public int MonthOfYear { get; set; }

        [JsonPropertyName("weekOfMonth")]
        public int WeekOfMonth { get; set; }

        [JsonPropertyName("dayOfWeek")]
        public int DayOfWeek { get; set; } = -1;

        [JsonPropertyName("durationSeconds")]
        public int DurationSeconds { get; set; }

        [JsonPropertyName("maxRuns")]
        public int MaxRuns { get; set; }

        [JsonPropertyName("runCount")]
        public int RunCount { get; set; }

        [JsonPropertyName("transitionSeconds")]
        public int TransitionSeconds { get; set; }

        [JsonPropertyName("transitionOutSeconds")]
        public int TransitionOutSeconds { get; set; }

        [JsonPropertyName("runDate")]
        public string RunDate { get; set; } = string.Empty;

        [JsonPropertyName("startDate")]
        public string StartDate { get; set; } = string.Empty;

        [JsonPropertyName("endDate")]
        public string EndDate { get; set; } = string.Empty;

        [JsonPropertyName("excludedDates")]
        public IReadOnlyList<string> ExcludedDates { get; set; } = Array.Empty<string>();

        [JsonPropertyName("daysOfWeekMask")]
        public int DaysOfWeekMask { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }
    }

    /// <summary>
    /// One credential-free upcoming scene-cue occurrence returned by the preview API.
    /// </summary>
    public sealed class HueSceneScheduleOccurrenceResult
    {
        [JsonPropertyName("scheduleId")]
        public string ScheduleId { get; set; } = string.Empty;

        [JsonPropertyName("scheduleName")]
        public string ScheduleName { get; set; } = string.Empty;

        [JsonPropertyName("presetName")]
        public string PresetName { get; set; } = string.Empty;

        [JsonPropertyName("effect")]
        public string Effect { get; set; } = PluginConfiguration.ColorPresetEffectSolid;

        [JsonPropertyName("effectSpeedPercent")]
        public int EffectSpeedPercent { get; set; } = PluginConfiguration.DefaultColorPresetEffectSpeedPercent;

        [JsonPropertyName("recurrence")]
        public string Recurrence { get; set; } = PluginConfiguration.SceneScheduleRecurrenceWeekly;

        [JsonPropertyName("recurrenceInterval")]
        public int RecurrenceInterval { get; set; } = PluginConfiguration.MinSceneScheduleRecurrenceInterval;

        [JsonPropertyName("dayOfMonth")]
        public int DayOfMonth { get; set; }

        [JsonPropertyName("monthOfYear")]
        public int MonthOfYear { get; set; }

        [JsonPropertyName("weekOfMonth")]
        public int WeekOfMonth { get; set; }

        [JsonPropertyName("dayOfWeek")]
        public int DayOfWeek { get; set; } = -1;

        [JsonPropertyName("durationSeconds")]
        public int DurationSeconds { get; set; }

        [JsonPropertyName("transitionSeconds")]
        public int TransitionSeconds { get; set; }

        [JsonPropertyName("transitionOutSeconds")]
        public int TransitionOutSeconds { get; set; }

        [JsonPropertyName("targetLabel")]
        public string TargetLabel { get; set; } = string.Empty;

        [JsonPropertyName("timeZoneId")]
        public string TimeZoneId { get; set; } = string.Empty;

        [JsonPropertyName("timeZoneDisplayName")]
        public string TimeZoneDisplayName { get; set; } = string.Empty;

        [JsonPropertyName("localTime")]
        public DateTime LocalTime { get; set; }

        [JsonPropertyName("utcTime")]
        public DateTime UtcTime { get; set; }
    }

    /// <summary>
    /// Bounded credential-free upcoming-cue preview returned by the administrator API.
    /// </summary>
    public sealed class HueSceneScheduleOccurrencesResult
    {
        [JsonPropertyName("serviceAvailable")]
        public bool ServiceAvailable { get; set; }

        [JsonPropertyName("generatedAtUtc")]
        public DateTime GeneratedAtUtc { get; set; }

        [JsonPropertyName("serverLocalNow")]
        public DateTime ServerLocalNow { get; set; }

        [JsonPropertyName("serverTimeZoneId")]
        public string ServerTimeZoneId { get; set; } = string.Empty;

        [JsonPropertyName("limit")]
        public int Limit { get; set; }

        [JsonPropertyName("horizonDays")]
        public int HorizonDays { get; set; }

        [JsonPropertyName("scheduleIdFilter")]
        public string? ScheduleIdFilter { get; set; }

        [JsonPropertyName("occurrences")]
        public IReadOnlyList<HueSceneScheduleOccurrenceResult> Occurrences { get; set; } = Array.Empty<HueSceneScheduleOccurrenceResult>();
    }

    /// <summary>
    /// Sanitized system time-zone choice for scene cues.
    /// </summary>
    public sealed class HueSceneScheduleTimeZoneResult
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("baseUtcOffsetMinutes")]
        public int BaseUtcOffsetMinutes { get; set; }
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
        public int SeekRestartCount { get; set; }
        public double? LastSeekPositionSeconds { get; set; }
        public bool CanStopSync { get; set; }
        public bool IsFfmpegHealthy { get; set; }
        public bool IsDtlsHealthy { get; set; }
        public double? SyncDurationSeconds { get; set; }
        public DateTime? SyncStartedAtUtc { get; set; }
        public HueSessionSummary? LastSession { get; set; }
        /// <summary>
        /// Sanitized active playback sessions. Existing top-level fields continue to
        /// describe the primary session for backward compatibility.
        /// </summary>
        public IReadOnlyList<HueRuntimeStatus> Sessions { get; set; } = Array.Empty<HueRuntimeStatus>();
    }

    /// <summary>
    /// Bounded administrator-facing history response for completed playback sessions.
    /// </summary>
    public sealed class HueSessionHistoryResult
    {
        public bool ServiceAvailable { get; init; }
        public int Limit { get; init; }
        public string? OutcomeFilter { get; init; }
        public DateTime GeneratedAtUtc { get; init; }
        public IReadOnlyList<HueSessionSummary> Sessions { get; init; } = Array.Empty<HueSessionSummary>();
    }

    /// <summary>
    /// Sanitized result from clearing completed-session history.
    /// </summary>
    public sealed class HueSessionHistoryClearResult
    {
        public bool ServiceAvailable { get; init; }
        public int ClearedCount { get; init; }
        public DateTime ClearedAtUtc { get; init; }
    }

    /// <summary>
    /// Bounded administrator-facing history response for completed scheduled-scene cues.
    /// </summary>
    public sealed class HueSceneScheduleHistoryResult
    {
        public bool ServiceAvailable { get; init; }
        public bool PersistenceEnabled { get; init; }
        public int Limit { get; init; }
        public string? ScheduleIdFilter { get; init; }
        public DateTime GeneratedAtUtc { get; init; }
        public IReadOnlyList<HueSceneAutomationRunResult> Runs { get; init; } = Array.Empty<HueSceneAutomationRunResult>();
    }

    /// <summary>
    /// Sanitized result from requesting cancellation of a manually started scene cue.
    /// </summary>
    public sealed class HueSceneScheduleCancellationResult
    {
        public bool Canceled { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// Sanitized result from requesting cancellation of active non-mutating diagnostics.
    /// </summary>
    public sealed class HueDiagnosticsCancellationResult
    {
        public bool Canceled { get; init; }
        public int CanceledCount { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// Sanitized result from clearing scheduled-scene cue history.
    /// </summary>
    public sealed class HueSceneScheduleHistoryClearResult
    {
        public bool ServiceAvailable { get; init; }
        public int ClearedCount { get; init; }
        public DateTime ClearedAtUtc { get; init; }
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
