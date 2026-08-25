using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Hue.Configuration;
using Jellyfin.Plugin.Hue.Hue;
using Jellyfin.Plugin.Hue.Service;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
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
    [ServiceFilter(typeof(HueConfigurationMutationFilter))]
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
        private readonly ISessionManager? _sessionManager;

        private const int PlaybackDeviceActivityWindowSeconds = 86400;
        private const int MaxPlaybackDeviceResults = 256;

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
            ISessionManager? sessionManager = null,
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
            _sessionManager = sessionManager;
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

        private static List<string> ValidateGlobalCredentialTransition(
            PluginConfiguration existingConfiguration,
            HuePluginConfigurationSettings incomingSettings,
            string operation)
        {
            var errors = new List<string>();
            var bothTargetsUnset = string.IsNullOrWhiteSpace(existingConfiguration.HueBridgeIp) &&
                string.IsNullOrWhiteSpace(incomingSettings.HueBridgeIp);
            if (bothTargetsUnset ||
                IsSameBridgeTarget(existingConfiguration.HueBridgeIp, incomingSettings.HueBridgeIp) ||
                incomingSettings.ClearStoredCredentials)
            {
                return errors;
            }

            if (string.IsNullOrWhiteSpace(incomingSettings.HueAppKey) &&
                !string.IsNullOrWhiteSpace(existingConfiguration.HueAppKey))
            {
                errors.Add(
                    $"{operation} changes the global bridge target but omits the stored Hue App Key. Provide a replacement App Key or set ClearStoredCredentials=true.");
            }

            if (string.IsNullOrWhiteSpace(incomingSettings.HueClientKey) &&
                !string.IsNullOrWhiteSpace(existingConfiguration.HueClientKey))
            {
                errors.Add(
                    $"{operation} changes the global bridge target but omits the stored Hue Client Key. Provide a replacement Client Key or set ClearStoredCredentials=true.");
            }

            return errors;
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
            => TryResolveCredentials(
                requestedBridgeIp,
                requestedAppKey,
                requestedClientKey,
                userId,
                null,
                allowStoredClientKey,
                out bridgeIp,
                out appKey,
                out clientKey);

        /// <summary>
        /// Resolves credentials for an optional explicit playback-device route. When a
        /// device ID is supplied, stored keys are considered only for the exact,
        /// case-sensitive device route belonging to the selected user and bridge. This
        /// prevents a stale or guessed device ID from falling back to another target's
        /// persisted credentials.
        /// </summary>
        private static bool TryResolveCredentials(
            string? requestedBridgeIp,
            string? requestedAppKey,
            string? requestedClientKey,
            string? userId,
            string? deviceId,
            bool allowStoredClientKey,
            out string bridgeIp,
            out string appKey,
            out string clientKey)
        {
            var config = Plugin.Instance?.Configuration;
            bridgeIp = requestedBridgeIp?.Trim() ?? string.Empty;
            appKey = requestedAppKey?.Trim() ?? string.Empty;
            clientKey = requestedClientKey?.Trim() ?? string.Empty;

            var normalizedDeviceId = deviceId?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(normalizedDeviceId))
            {
                var deviceMapping = config?.UserMappings?.FirstOrDefault(candidate =>
                    candidate != null &&
                    !string.IsNullOrWhiteSpace(userId) &&
                    PluginConfiguration.AreSameJellyfinUserId(candidate.UserId, userId));
                var deviceTarget = deviceMapping?.DeviceTargets?.FirstOrDefault(candidate =>
                    candidate != null &&
                    string.Equals(candidate.DeviceId?.Trim(), normalizedDeviceId, StringComparison.Ordinal));

                if (deviceTarget != null &&
                    !string.IsNullOrWhiteSpace(deviceTarget.HueBridgeIp) &&
                    IsSameBridgeTarget(bridgeIp, deviceTarget.HueBridgeIp))
                {
                    if (string.IsNullOrWhiteSpace(appKey))
                        appKey = deviceTarget.HueAppKey?.Trim() ?? string.Empty;

                    if (allowStoredClientKey && string.IsNullOrWhiteSpace(clientKey))
                        clientKey = deviceTarget.HueClientKey?.Trim() ?? string.Empty;
                }

                // An explicit device route must never fall through to the global or
                // outer-user mapping when its stored route is missing or mismatched.
                return !string.IsNullOrWhiteSpace(bridgeIp) && !string.IsNullOrWhiteSpace(appKey);
            }

            var mapping = config?.UserMappings?.FirstOrDefault(candidate =>
                candidate != null &&
                !string.IsNullOrWhiteSpace(userId) &&
                PluginConfiguration.AreSameJellyfinUserId(candidate.UserId, userId));
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

        /// <summary>
        /// Returns credential-free playback-device identities observed by Jellyfin.
        /// Device IDs are the exact, case-sensitive values consumed by automatic route
        /// matching; keys and playback titles are intentionally never returned.
        /// </summary>
        [HttpGet("PlaybackDevices")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public ActionResult<IEnumerable<HuePlaybackDeviceSummary>> GetPlaybackDevices(
            [FromQuery] string? userId = null)
        {
            if (!string.IsNullOrWhiteSpace(userId) && !Guid.TryParse(userId.Trim(), out _))
                return BadRequest("userId must be a valid Jellyfin user ID.");

            if (_sessionManager == null)
                return Ok(Array.Empty<HuePlaybackDeviceSummary>());

            IReadOnlyList<SessionInfoDto> sessions;
            try
            {
                sessions = _sessionManager.GetSessions(
                    Guid.Empty,
                    null,
                    PlaybackDeviceActivityWindowSeconds,
                    null,
                    false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Unable to enumerate Jellyfin playback sessions for Hue device discovery.");
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Playback device discovery is temporarily unavailable.");
            }

            var normalizedUserId = PluginConfiguration.NormalizeJellyfinUserId(userId);
            var devices = sessions
                .Where(session => session != null &&
                    !string.IsNullOrWhiteSpace(session.DeviceId) &&
                    (string.IsNullOrWhiteSpace(normalizedUserId) ||
                     PluginConfiguration.AreSameJellyfinUserId(session.UserId.ToString(), normalizedUserId)))
                .Select(session => new HuePlaybackDeviceSummary
                {
                    UserId = session.UserId.ToString(),
                    UserName = session.UserName ?? string.Empty,
                    DeviceId = session.DeviceId!.Trim(),
                    DeviceName = string.IsNullOrWhiteSpace(session.DeviceName)
                        ? session.DeviceId!.Trim()
                        : session.DeviceName.Trim(),
                    Client = session.Client ?? string.Empty,
                    DeviceType = session.DeviceType ?? string.Empty,
                    ApplicationVersion = session.ApplicationVersion ?? string.Empty,
                    IsActive = session.IsActive,
                    LastActivityDate = session.LastActivityDate
                })
                .GroupBy(
                    device => (device.UserId, device.DeviceId),
                    new PlaybackDeviceRouteKeyComparer())
                .Select(group => group
                    .OrderByDescending(device => device.IsActive)
                    .ThenByDescending(device => device.LastActivityDate)
                    .First())
                .OrderBy(device => device.UserName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(device => device.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(device => device.DeviceId, StringComparer.Ordinal)
                .Take(MaxPlaybackDeviceResults)
                .ToArray();

            return Ok(devices);
        }

        private sealed class PlaybackDeviceRouteKeyComparer : IEqualityComparer<(string UserId, string DeviceId)>
        {
            public bool Equals((string UserId, string DeviceId) left, (string UserId, string DeviceId) right)
                => PluginConfiguration.AreSameJellyfinUserId(left.UserId, right.UserId) &&
                   string.Equals(left.DeviceId, right.DeviceId, StringComparison.Ordinal);

            public int GetHashCode((string UserId, string DeviceId) value)
                => HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(
                        PluginConfiguration.NormalizeJellyfinUserId(value.UserId)),
                    StringComparer.Ordinal.GetHashCode(value.DeviceId));
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
                    request.DeviceId,
                    allowStoredClientKey: false,
                    out _,
                    out _,
                    out _))
            {
                return BadRequest("Bridge IP and app key are required before loading entertainment areas.");
            }

            return await LoadEntertainmentAreas(
                request.IpAddress,
                request.AppKey,
                request.UserId,
                request.DeviceId,
                cancellationToken);
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
                    request.DeviceId,
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
            string? deviceId,
            CancellationToken cancellationToken)
        {
            if (!TryResolveCredentials(
                    bridgeIp,
                    appKey,
                    null,
                    userId,
                    deviceId,
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
                TransitionOutSeconds = preset.TransitionOutSeconds,
                TransitionCurve = HueSceneAutomationService.GetEffectiveTransitionCurve(preset)
            };
        }

        internal static HueScenePlaylistResult ToScenePlaylistResult(
            HueScenePlaylist playlist,
            PluginConfiguration config)
        {
            var targetUserId = PluginConfiguration.NormalizeJellyfinUserId(playlist.TargetUserId);
            var targetUserIds = (playlist.TargetUserIds ?? new List<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(PluginConfiguration.NormalizeJellyfinUserId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var mapping = string.IsNullOrWhiteSpace(targetUserId)
                ? null
                : config.UserMappings?.FirstOrDefault(candidate =>
                    candidate != null &&
                    PluginConfiguration.AreSameJellyfinUserId(candidate.UserId, targetUserId));
            var targetLabel = playlist.TargetAllEnabledMappings
                ? "All enabled targets"
                : playlist.IncludeDefaultTarget || targetUserIds.Length > 0
                    ? playlist.IncludeDefaultTarget
                        ? targetUserIds.Length == 0
                            ? "Default bridge target"
                            : $"Default bridge + {targetUserIds.Length} selected target(s)"
                        : $"{targetUserIds.Length} selected target(s)"
                    : string.IsNullOrWhiteSpace(targetUserId)
                        ? "Default bridge target"
                        : mapping == null
                            ? "Missing user mapping"
                            : string.IsNullOrWhiteSpace(mapping.UserName)
                                ? $"User mapping {mapping.UserId?.Trim() ?? targetUserId}"
                                : mapping.UserName.Trim();
            var presetNames = (playlist.PresetNames ?? new List<string>())
                .Select(name => name?.Trim() ?? string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();
            var sourcePresetNames = playlist.PresetNames ?? new List<string>();
            var totalDuration = sourcePresetNames
                .Select((name, index) => new
                {
                    Index = index,
                    Preset = config.ColorPresets?.FirstOrDefault(preset =>
                        preset != null &&
                        string.Equals(preset.Name?.Trim(), name?.Trim(), StringComparison.OrdinalIgnoreCase))
                })
                .Where(item => item.Preset != null)
                .Select(item => PluginConfiguration.GetEffectiveScenePlaylistStepDurationSeconds(
                    playlist,
                    item.Index,
                    item.Preset!))
                .Sum() * Math.Clamp(
                    playlist.RepeatCount,
                    PluginConfiguration.MinScenePlaylistRepeatCount,
                    PluginConfiguration.MaxScenePlaylistRepeatCount);
            totalDuration = Math.Min(totalDuration, PluginConfiguration.MaxScenePlaylistTotalDurationSeconds);
            return new HueScenePlaylistResult
            {
                Id = playlist.Id?.Trim() ?? string.Empty,
                Name = playlist.Name?.Trim() ?? string.Empty,
                PresetNames = presetNames,
                StepDurationSeconds = (playlist.StepDurationSeconds ?? new List<int>()).ToArray(),
                StepRed = (playlist.StepRed ?? new List<int?>()).ToArray(),
                StepGreen = (playlist.StepGreen ?? new List<int?>()).ToArray(),
                StepBlue = (playlist.StepBlue ?? new List<int?>()).ToArray(),
                StepBrightnessPercent = (playlist.StepBrightnessPercent ?? new List<int?>()).ToArray(),
                StepEffects = (playlist.StepEffects ?? new List<string?>())
                    .Select(effect => string.IsNullOrWhiteSpace(effect)
                        ? null
                        : PluginConfiguration.TryNormalizeColorPresetEffect(effect, out var normalizedEffect)
                            ? normalizedEffect
                            : effect.Trim())
                    .ToArray(),
                StepEffectSpeedPercent = (playlist.StepEffectSpeedPercent ?? new List<int?>()).ToArray(),
                StepTransitionSeconds = (playlist.StepTransitionSeconds ?? new List<int?>()).ToArray(),
                StepTransitionOutSeconds = (playlist.StepTransitionOutSeconds ?? new List<int?>()).ToArray(),
                StepTransitionCurves = (playlist.StepTransitionCurves ?? new List<string?>())
                    .Select(curve => string.IsNullOrWhiteSpace(curve)
                        ? null
                        : PluginConfiguration.TryNormalizeColorPresetTransitionCurve(curve, out var normalizedCurve)
                            ? normalizedCurve
                            : curve.Trim())
                    .ToArray(),
                RepeatCount = Math.Clamp(
                    playlist.RepeatCount,
                    PluginConfiguration.MinScenePlaylistRepeatCount,
                    PluginConfiguration.MaxScenePlaylistRepeatCount),
                PlaybackOrder = PluginConfiguration.TryNormalizeScenePlaylistOrder(
                    playlist.PlaybackOrder,
                    out var normalizedPlaybackOrder)
                    ? normalizedPlaybackOrder
                    : PluginConfiguration.ScenePlaylistOrderSequential,
                TargetUserId = playlist.TargetAllEnabledMappings || playlist.IncludeDefaultTarget || targetUserIds.Length > 0
                    ? string.Empty
                    : targetUserId,
                TargetAllEnabledMappings = playlist.TargetAllEnabledMappings,
                TargetUserIds = targetUserIds,
                IncludeDefaultTarget = playlist.IncludeDefaultTarget,
                TargetLabel = targetLabel,
                TotalDurationSeconds = totalDuration
            };
        }

        private static HueColorPreset CloneColorPreset(HueColorPreset preset)
        {
            return new HueColorPreset
            {
                Name = preset.Name,
                Effect = preset.Effect,
                EffectSpeedPercent = preset.EffectSpeedPercent,
                Red = preset.Red,
                Green = preset.Green,
                Blue = preset.Blue,
                BrightnessPercent = preset.BrightnessPercent,
                DurationSeconds = preset.DurationSeconds,
                TransitionSeconds = preset.TransitionSeconds,
                TransitionOutSeconds = preset.TransitionOutSeconds,
                TransitionCurve = preset.TransitionCurve
            };
        }

        private static HueScenePlaylist CloneScenePlaylist(HueScenePlaylist playlist)
        {
            return new HueScenePlaylist
            {
                Id = playlist.Id,
                Name = playlist.Name,
                PresetNames = (playlist.PresetNames ?? new List<string>()).ToList(),
                StepDurationSeconds = (playlist.StepDurationSeconds ?? new List<int>()).ToList(),
                StepRed = (playlist.StepRed ?? new List<int?>()).ToList(),
                StepGreen = (playlist.StepGreen ?? new List<int?>()).ToList(),
                StepBlue = (playlist.StepBlue ?? new List<int?>()).ToList(),
                StepBrightnessPercent = (playlist.StepBrightnessPercent ?? new List<int?>()).ToList(),
                StepEffects = (playlist.StepEffects ?? new List<string?>()).ToList(),
                StepEffectSpeedPercent = (playlist.StepEffectSpeedPercent ?? new List<int?>()).ToList(),
                StepTransitionSeconds = (playlist.StepTransitionSeconds ?? new List<int?>()).ToList(),
                StepTransitionOutSeconds = (playlist.StepTransitionOutSeconds ?? new List<int?>()).ToList(),
                StepTransitionCurves = (playlist.StepTransitionCurves ?? new List<string?>()).ToList(),
                RepeatCount = playlist.RepeatCount,
                PlaybackOrder = playlist.PlaybackOrder,
                TargetUserId = PluginConfiguration.NormalizeJellyfinUserId(playlist.TargetUserId),
                TargetUserIds = (playlist.TargetUserIds ?? new List<string>())
                    .Select(PluginConfiguration.NormalizeJellyfinUserId)
                    .ToList(),
                IncludeDefaultTarget = playlist.IncludeDefaultTarget,
                TargetAllEnabledMappings = playlist.TargetAllEnabledMappings
            };
        }

        private static string BuildDuplicateScenePlaylistName(
            IEnumerable<HueScenePlaylist> playlists,
            string? sourceName)
        {
            var existingNames = new HashSet<string>(
                playlists
                    .Where(playlist => playlist != null)
                    .Select(playlist => playlist.Name?.Trim() ?? string.Empty)
                    .Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);
            var baseName = string.IsNullOrWhiteSpace(sourceName) ? "Saved playlist" : sourceName.Trim();
            for (var copyNumber = 1; copyNumber <= PluginConfiguration.MaxScenePlaylists + 1; copyNumber++)
            {
                var suffix = copyNumber == 1 ? " (Copy)" : $" (Copy {copyNumber})";
                var availableBaseLength = Math.Max(1, PluginConfiguration.MaxScenePlaylistNameLength - suffix.Length);
                var truncatedBase = baseName.Length > availableBaseLength
                    ? baseName[..availableBaseLength].TrimEnd()
                    : baseName;
                var candidate = truncatedBase + suffix;
                if (!existingNames.Contains(candidate))
                    return candidate;
            }

            return $"Playlist copy {Guid.NewGuid():N}"[..PluginConfiguration.MaxScenePlaylistNameLength];
        }

        private static string BuildDuplicateColorPresetName(
            IEnumerable<HueColorPreset> presets,
            string? sourceName)
        {
            var existingNames = new HashSet<string>(
                presets
                    .Where(preset => preset != null)
                    .Select(preset => preset.Name?.Trim() ?? string.Empty)
                    .Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);
            var baseName = string.IsNullOrWhiteSpace(sourceName)
                ? "Saved scene"
                : sourceName.Trim();

            for (var copyNumber = 1; copyNumber <= PluginConfiguration.MaxColorPresets + 1; copyNumber++)
            {
                var suffix = copyNumber == 1
                    ? " (Copy)"
                    : $" (Copy {copyNumber})";
                var availableBaseLength = Math.Max(
                    1,
                    PluginConfiguration.MaxColorPresetNameLength - suffix.Length);
                var truncatedBase = baseName.Length > availableBaseLength
                    ? baseName[..availableBaseLength].TrimEnd()
                    : baseName;
                var candidate = truncatedBase + suffix;
                if (!existingNames.Contains(candidate))
                    return candidate;
            }

            // The preset collection is bounded, so the loop above always returns. Keep
            // a bounded unique fallback for malformed legacy configurations.
            return $"Scene copy {Guid.NewGuid():N}"[..PluginConfiguration.MaxColorPresetNameLength];
        }

        internal static HueSceneScheduleResult ToSceneScheduleResult(
            HueSceneSchedule schedule,
            PluginConfiguration config)
        {
            var targetUserId = PluginConfiguration.NormalizeJellyfinUserId(schedule.TargetUserId);
            var targetLabel = HueSceneAutomationService.ResolveTargetLabel(config, schedule);
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
            var playlist = config.ScenePlaylists?.FirstOrDefault(candidate =>
                candidate != null &&
                string.Equals(candidate.Name?.Trim(), schedule.PlaylistName?.Trim(), StringComparison.OrdinalIgnoreCase));
            var isPlaylist = !string.IsNullOrWhiteSpace(schedule.PlaylistName);
            var effect = isPlaylist
                ? PluginConfiguration.SceneScheduleEffectPlaylist
                : PluginConfiguration.TryNormalizeColorPresetEffect(preset?.Effect, out var normalizedEffect)
                    ? normalizedEffect
                    : PluginConfiguration.ColorPresetEffectSolid;
            var playlistTotalDuration = isPlaylist
                ? HueSceneAutomationService.GetPlaylistTotalDurationSeconds(config, playlist)
                : 0;

            return new HueSceneScheduleResult
            {
                Id = schedule.Id,
                Name = schedule.Name,
                PresetName = schedule.PresetName,
                PlaylistName = schedule.PlaylistName,
                Priority = schedule.Priority,
                PlaybackPolicy = HueSceneAutomationService.NormalizeSchedulePlaybackPolicy(schedule.PlaybackPolicy),
                EffectivePlaybackPolicy = HueSceneAutomationService.GetEffectivePlaybackPolicy(config, schedule),
                Effect = effect,
                EffectSpeedPercent = isPlaylist || preset == null
                    ? PluginConfiguration.DefaultColorPresetEffectSpeedPercent
                    : PluginConfiguration.ClampColorPresetEffectSpeedPercent(preset.EffectSpeedPercent),
                BrightnessPercent = isPlaylist ? null : schedule.BrightnessPercent,
                Red = isPlaylist ? null : schedule.Red,
                Green = isPlaylist ? null : schedule.Green,
                Blue = isPlaylist ? null : schedule.Blue,
                TransitionCurve = isPlaylist
                    ? PluginConfiguration.ColorPresetTransitionCurveLinear
                    : HueSceneAutomationService.GetEffectiveTransitionCurve(preset),
                PlaylistStepCount = playlist?.PresetNames?.Count ?? 0,
                PlaylistRepeatCount = playlist == null
                    ? PluginConfiguration.DefaultScenePlaylistRepeatCount
                    : Math.Clamp(
                        playlist.RepeatCount,
                        PluginConfiguration.MinScenePlaylistRepeatCount,
                        PluginConfiguration.MaxScenePlaylistRepeatCount),
                PlaylistPlaybackOrder = playlist == null || !PluginConfiguration.TryNormalizeScenePlaylistOrder(
                    playlist.PlaybackOrder,
                    out var playlistPlaybackOrder)
                    ? PluginConfiguration.ScenePlaylistOrderSequential
                    : playlistPlaybackOrder,
                PlaylistTotalDurationSeconds = playlistTotalDuration,
                TargetUserId = targetUserId,
                TargetAllEnabledMappings = schedule.TargetAllEnabledMappings,
                TargetUserIds = schedule.TargetUserIds?.Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(PluginConfiguration.NormalizeJellyfinUserId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                    ?? Array.Empty<string>(),
                TargetRoutes = schedule.TargetRoutes?.Where(route => route != null)
                    .Select(route => new HueSceneScheduleTargetRoute
                    {
                        UserId = PluginConfiguration.NormalizeJellyfinUserId(route.UserId),
                        DeviceId = route.DeviceId?.Trim() ?? string.Empty
                    }).ToArray() ?? Array.Empty<HueSceneScheduleTargetRoute>(),
                IncludeDefaultTarget = schedule.IncludeDefaultTarget,
                TargetLabel = targetLabel,
                TimeOfDay = schedule.TimeOfDay,
                TimeMode = PluginConfiguration.TryNormalizeSceneScheduleTimeMode(
                    schedule.TimeMode,
                    out var normalizedTimeMode)
                    ? normalizedTimeMode
                    : PluginConfiguration.SceneScheduleTimeModeFixed,
                SolarOffsetMinutes = Math.Clamp(
                    schedule.SolarOffsetMinutes,
                    PluginConfiguration.MinSceneScheduleSolarOffsetMinutes,
                    PluginConfiguration.MaxSceneScheduleSolarOffsetMinutes),
                SolarLatitude = schedule.SolarLatitude,
                SolarLongitude = schedule.SolarLongitude,
                TimeZoneId = schedule.TimeZoneId?.Trim() ?? string.Empty,
                TimeZoneIanaId = PluginConfiguration.TryGetPortableSceneScheduleTimeZoneId(
                    schedule.TimeZoneId,
                    out var portableTimeZoneId)
                    ? portableTimeZoneId
                    : string.Empty,
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
                // Keep the persisted schedule override round-trippable. Playlist total
                // duration is exposed separately because playlist cues must keep this at 0.
                DurationSeconds = schedule.DurationSeconds,
                MaxRuns = schedule.MaxRuns,
                RunCount = schedule.RunCount,
                RunDate = schedule.RunDate?.Trim() ?? string.Empty,
                StartDate = schedule.StartDate?.Trim() ?? string.Empty,
                EndDate = schedule.EndDate?.Trim() ?? string.Empty,
                ExcludedDates = excludedDates,
                DaysOfWeekMask = schedule.DaysOfWeekMask,
                Enabled = schedule.Enabled,
                SkipNextOccurrence = schedule.SkipNextOccurrence,
                TransitionSeconds = isPlaylist ? 0 : HueSceneAutomationService.GetEffectiveTransitionSeconds(schedule, preset),
                TransitionOutSeconds = isPlaylist ? 0 : HueSceneAutomationService.GetEffectiveTransitionOutSeconds(schedule, preset)
            };
        }

        private static HueSceneSchedule CloneSceneSchedule(HueSceneSchedule schedule)
        {
            return new HueSceneSchedule
            {
                Id = schedule.Id,
                Name = schedule.Name,
                PresetName = schedule.PresetName,
                PlaylistName = schedule.PlaylistName,
                Priority = schedule.Priority,
                PlaybackPolicy = schedule.PlaybackPolicy,
                TargetUserId = PluginConfiguration.NormalizeJellyfinUserId(schedule.TargetUserId),
                TargetUserIds = schedule.TargetUserIds?
                    .Select(PluginConfiguration.NormalizeJellyfinUserId)
                    .ToList() ?? new List<string>(),
                TargetRoutes = schedule.TargetRoutes?.Where(route => route != null)
                    .Select(route => new HueSceneScheduleTargetRoute
                    {
                        UserId = PluginConfiguration.NormalizeJellyfinUserId(route.UserId),
                        DeviceId = route.DeviceId?.Trim() ?? string.Empty
                    }).ToList() ?? new List<HueSceneScheduleTargetRoute>(),
                IncludeDefaultTarget = schedule.IncludeDefaultTarget,
                TargetAllEnabledMappings = schedule.TargetAllEnabledMappings,
                TimeOfDay = schedule.TimeOfDay,
                TimeMode = schedule.TimeMode,
                SolarOffsetMinutes = schedule.SolarOffsetMinutes,
                SolarLatitude = schedule.SolarLatitude,
                SolarLongitude = schedule.SolarLongitude,
                TimeZoneId = schedule.TimeZoneId,
                Recurrence = schedule.Recurrence,
                RecurrenceInterval = schedule.RecurrenceInterval,
                DayOfMonth = schedule.DayOfMonth,
                MonthOfYear = schedule.MonthOfYear,
                WeekOfMonth = schedule.WeekOfMonth,
                DayOfWeek = schedule.DayOfWeek,
                DurationSeconds = schedule.DurationSeconds,
                BrightnessPercent = schedule.BrightnessPercent,
                Red = schedule.Red,
                Green = schedule.Green,
                Blue = schedule.Blue,
                MaxRuns = schedule.MaxRuns,
                RunCount = schedule.RunCount,
                RunDate = schedule.RunDate,
                StartDate = schedule.StartDate,
                EndDate = schedule.EndDate,
                ExcludedDates = schedule.ExcludedDates?.ToList() ?? new List<string>(),
                DaysOfWeekMask = schedule.DaysOfWeekMask,
                Enabled = schedule.Enabled,
                SkipNextOccurrence = schedule.SkipNextOccurrence
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
                    request.DeviceId,
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
        /// Captures the current color and brightness of one persisted target without
        /// exposing bridge credentials to the configuration page. The selected target's
        /// saved channel profile is honored so a capture can be used as a reliable seed
        /// for a scene that will later run on the same room.
        /// </summary>
        [HttpPost("Preview/CaptureCurrentColor")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        public async Task<ActionResult<HueCurrentLightColorResult>> CaptureCurrentColor(
            [FromBody] HueCurrentLightColorRequest? request,
            CancellationToken cancellationToken = default)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return BadRequest("Plugin configuration is unavailable.");

            var targetUserId = request?.TargetUserId?.Trim() ?? string.Empty;
            var targetDeviceId = request?.TargetDeviceId?.Trim() ?? string.Empty;
            var target = ResolveSingleCaptureTarget(config, targetUserId, targetDeviceId);
            if (target == null)
            {
                return BadRequest(string.IsNullOrWhiteSpace(targetUserId) && string.IsNullOrWhiteSpace(targetDeviceId)
                    ? "The default bridge target is not configured."
                    : !string.IsNullOrWhiteSpace(targetDeviceId)
                        ? "The selected user device target is not configured or enabled."
                    : "The selected user target is not configured or enabled.");
            }

            using var diagnosticsOperation = _diagnosticsCancellationGate.Begin(cancellationToken);
            var diagnosticsCancellationToken = diagnosticsOperation.Token;
            using var lifecycleLease = _bridgeLifecycleGate.TryEnterDiagnostic();
            if (lifecycleLease == null)
                return Conflict("Another Hue playback or diagnostic operation is already running.");

            var attempt = await CaptureCurrentColorAsync(target, diagnosticsCancellationToken).ConfigureAwait(false);
            if (attempt.FailureStatusCode is { } statusCode)
                return StatusCode(statusCode, attempt.FailureResponse ?? attempt.FailureMessage);

            return Ok(attempt.Result);
        }

        /// <summary>
        /// Captures current RGB/brightness samples from the default bridge, every
        /// distinct enabled target, or a selected user/device target subset. Each target is reported
        /// independently so one stale mapping cannot hide usable samples from the other
        /// rooms; the aggregate sample is a convenient seed for the scene editor.
        /// </summary>
        [HttpPost("Preview/CaptureCurrentColors")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<HueCurrentLightColorBatchResult>> CaptureCurrentColors(
            [FromBody] HueCurrentLightColorBatchRequest? request,
            CancellationToken cancellationToken = default)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return BadRequest("Plugin configuration is unavailable.");

            if (!TryResolveCaptureTargets(config, request, out var targets, out var selectionError))
                return BadRequest(selectionError);

            using var diagnosticsOperation = _diagnosticsCancellationGate.Begin(cancellationToken);
            var diagnosticsCancellationToken = diagnosticsOperation.Token;
            using var lifecycleLease = _bridgeLifecycleGate.TryEnterDiagnostic();
            if (lifecycleLease == null)
                return Conflict("Another Hue playback or diagnostic operation is already running.");

            var captures = new List<HueCurrentLightColorResult>(targets.Count);
            foreach (var target in targets)
            {
                diagnosticsCancellationToken.ThrowIfCancellationRequested();
                var attempt = await CaptureCurrentColorAsync(target, diagnosticsCancellationToken).ConfigureAwait(false);
                captures.Add(attempt.Result ?? BuildCaptureFailureResult(target, attempt.FailureMessage));
            }

            var successfulCaptures = captures.Where(capture => capture.Succeeded).ToArray();
            var aggregate = AggregateCurrentColorSamples(successfulCaptures);
            var successfulCount = successfulCaptures.Length;
            var targetCount = captures.Count;
            return Ok(new HueCurrentLightColorBatchResult
            {
                Succeeded = successfulCount == targetCount,
                Message = successfulCount == targetCount
                    ? $"Captured current light colors from {successfulCount} target(s)."
                    : successfulCount == 0
                        ? "No selected target returned a usable current-light sample."
                        : $"Captured current light colors from {successfulCount} of {targetCount} target(s); review the per-target results.",
                TargetAllEnabledMappings = request?.TargetAllEnabledMappings == true,
                TargetUserIds = request?.TargetUserIds?
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray() ?? Array.Empty<string>(),
                TargetRoutes = request?.TargetRoutes?
                    .Where(route => route != null && !string.IsNullOrWhiteSpace(route.UserId))
                    .Select(route => new HueCurrentLightColorTargetRoute
                    {
                        UserId = route.UserId.Trim(),
                        DeviceId = string.IsNullOrWhiteSpace(route.DeviceId) ? null : route.DeviceId.Trim()
                    })
                    .ToArray() ?? Array.Empty<HueCurrentLightColorTargetRoute>(),
                IncludeDefaultTarget = request?.IncludeDefaultTarget == true,
                AttemptedTargetCount = targetCount,
                SuccessfulTargetCount = successfulCount,
                Captures = captures,
                Red = aggregate.Red,
                Green = aggregate.Green,
                Blue = aggregate.Blue,
                BrightnessPercent = aggregate.BrightnessPercent,
                SampledLightCount = aggregate.SampledLightCount,
                CapturedAtUtc = DateTime.UtcNow
            });
        }

        /// <summary>
        /// Displays a bounded scene-effect preview through the configured entertainment
        /// area. When targetAllEnabledMappings is enabled, the same preview runs
        /// sequentially on each distinct enabled configured target; selected target IDs
        /// can instead fan out to a deliberate subset and optionally include the default
        /// bridge. The stream tester captures and restores the selected lights so this
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
            var broadcast = request?.TargetAllEnabledMappings == true;
            if (ContainsBlankTargetUserId(request?.TargetUserIds))
                return BadRequest("Selected preview target IDs must contain user mapping IDs.");

            var selectedTargetUserIds = request?.TargetUserIds?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToList();
            var selectedTargetRoutes = NormalizeSceneAutomationTargetRoutes(request?.TargetRoutes);
            if (TryGetInvalidSceneAutomationTargetRouteError(selectedTargetRoutes) is { } selectedTargetRouteError)
                return BadRequest(selectedTargetRouteError);
            var includeDefaultTarget = request?.IncludeDefaultTarget == true;
            var hasSelectedTargetOverride = includeDefaultTarget ||
                (selectedTargetUserIds?.Count > 0) ||
                selectedTargetRoutes.Count > 0;
            var multiTarget = broadcast || hasSelectedTargetOverride;
            if (request != null && !string.IsNullOrWhiteSpace(request.DeviceId) &&
                (string.IsNullOrWhiteSpace(request.UserId) || multiTarget))
            {
                return BadRequest("A deviceId requires one specific user mapping and cannot be combined with broadcast or selected targets.");
            }
            if (request != null && !string.IsNullOrWhiteSpace(request.UserId) && multiTarget)
            {
                return BadRequest(
                    "A raw preview cannot combine a specific user mapping with broadcast or selected targets.");
            }
            if (request == null ||
                (!multiTarget &&
                 (!HueBridgeCertificateValidation.IsValidBridgeAddress(request.IpAddress) ||
                  string.IsNullOrWhiteSpace(request.EntertainmentAreaId))))
            {
                return BadRequest("A valid bridge address, app key, client key, and entertainment area ID are required.");
            }

            var bridgeIp = string.Empty;
            var appKey = string.Empty;
            var clientKey = string.Empty;
            if (!multiTarget &&
                (!TryResolveCredentials(
                    request.IpAddress,
                    request.AppKey,
                    request.ClientKey,
                    request.UserId,
                    request.DeviceId,
                    allowStoredClientKey: true,
                    out bridgeIp,
                    out appKey,
                    out clientKey)
                 || string.IsNullOrWhiteSpace(clientKey)))
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
                return BadRequest($"Preview effect must be one of {PluginConfiguration.ColorPresetEffectSolid}, {PluginConfiguration.ColorPresetEffectPulse}, {PluginConfiguration.ColorPresetEffectRainbow}, {PluginConfiguration.ColorPresetEffectCandle}, {PluginConfiguration.ColorPresetEffectTemperature}, {PluginConfiguration.ColorPresetEffectAurora}, {PluginConfiguration.ColorPresetEffectFire}, {PluginConfiguration.ColorPresetEffectOcean}, {PluginConfiguration.ColorPresetEffectLightning}, or {PluginConfiguration.ColorPresetEffectStarlight}.");
            }

            if (!PluginConfiguration.TryNormalizeColorPresetTransitionCurve(request.TransitionCurve, out var transitionCurve))
            {
                return BadRequest($"Preview transition curve must be one of {PluginConfiguration.ColorPresetTransitionCurveLinear}, {PluginConfiguration.ColorPresetTransitionCurveSmoothStep}, {PluginConfiguration.ColorPresetTransitionCurveEaseIn}, {PluginConfiguration.ColorPresetTransitionCurveEaseOut}, or {PluginConfiguration.ColorPresetTransitionCurveEaseInOut}.");
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

            if (broadcast && hasSelectedTargetOverride)
                return BadRequest("A broadcast preview cannot also select a specific or selected target.");

            if (_streamTester == null)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Hue preview service is not available.");
            }

            if (_syncService?.IsSyncing == true)
            {
                return Conflict("Stop active playback before running a Hue scene preview.");
            }

            if (multiTarget)
            {
                if (!string.IsNullOrWhiteSpace(request.ChannelIds))
                {
                    return BadRequest("All-target previews use each configured target's channel profile; omit channelIds.");
                }

                if (_sceneAutomationService == null)
                {
                    return StatusCode(StatusCodes.Status503ServiceUnavailable, "Scene automation service is not available.");
                }

                var config = Plugin.Instance?.Configuration;
                if (config == null)
                {
                    return BadRequest("Scene automation configuration is unavailable.");
                }

                var previewSchedule = new HueSceneSchedule
                {
                    Id = "administrator-preview",
                    Name = "Administrator preview",
                    PresetName = "Administrator preview",
                    TargetUserIds = selectedTargetUserIds ?? new List<string>(),
                    IncludeDefaultTarget = includeDefaultTarget,
                    TargetAllEnabledMappings = broadcast && !hasSelectedTargetOverride,
                    DurationSeconds = request.DurationSeconds
                };
                if (!HueSceneAutomationService.TryResolveTargets(
                        config,
                        previewSchedule,
                        out _,
                        out var targetError,
                        selectedTargetRoutes))
                {
                    return BadRequest(targetError);
                }

                var previewPreset = new HueColorPreset
                {
                    Name = "Administrator preview",
                    Effect = effect,
                    EffectSpeedPercent = request.EffectSpeedPercent,
                    Red = request.Red,
                    Green = request.Green,
                    Blue = request.Blue,
                    BrightnessPercent = request.BrightnessPercent,
                    DurationSeconds = request.DurationSeconds,
                    TransitionSeconds = request.TransitionSeconds,
                    TransitionOutSeconds = request.TransitionOutSeconds,
                    TransitionCurve = transitionCurve
                };
                var broadcastResult = await _sceneAutomationService.RunPreviewAsync(
                    previewSchedule,
                    previewPreset,
                    selectedTargetRoutes,
                    cancellationToken).ConfigureAwait(false);
                return Ok(BuildPreviewResult(
                    broadcastResult,
                    previewSchedule,
                    previewPreset,
                    selectedTargetRoutes));
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
                var curveTester = _streamTester as IHueTransitionCurveStreamTester;
                streamPreview = curveTester != null
                    ? await curveTester.PreviewAsyncWithTransitionCurve(
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
                        request.EffectSpeedPercent,
                        transitionCurve)
                    : await _streamTester.PreviewAsync(
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
                TransitionCurve = transitionCurve,
                TargetAllEnabledMappings = false,
                TargetUserIds = string.IsNullOrWhiteSpace(request.UserId)
                    ? Array.Empty<string>()
                    : new[] { request.UserId.Trim() },
                TargetRoutes = string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.DeviceId)
                    ? Array.Empty<HueCurrentLightColorTargetRoute>()
                    : new[]
                    {
                        new HueCurrentLightColorTargetRoute
                        {
                            UserId = request.UserId.Trim(),
                            DeviceId = request.DeviceId.Trim()
                        }
                    },
                TargetResults = new[]
                {
                    new HuePreviewTargetResult
                    {
                        TargetLabel = string.IsNullOrWhiteSpace(request.UserId)
                            ? "Selected bridge target"
                            : request.UserId.Trim(),
                        Succeeded = streamPreview.Succeeded,
                        Message = streamPreview.Message,
                        CleanupWarning = streamPreview.CleanupWarning,
                        AvailableChannelCount = availableChannelIds.Count,
                        SelectedChannelCount = requestedChannelIds?.Count ?? availableChannelIds.Count
                    }
                },
                AvailableChannelCount = availableChannelIds.Count,
                SelectedChannelCount = requestedChannelIds?.Count ?? availableChannelIds.Count
            });
        }

        /// <summary>
        /// Displays one saved scene through server-side target resolution. The request
        /// carries only the saved scene name and optional target mode; bridge credentials
        /// and channel profiles remain in the persisted Jellyfin configuration. A selected
        /// target list may contain enabled user mappings and optionally the default bridge.
        /// </summary>
        [HttpPost("ColorPresets/{name}/Preview")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<ActionResult<HuePreviewResult>> PreviewColorPreset(
            string name,
            [FromBody] HueSavedColorPresetPreviewRequest? request,
            CancellationToken cancellationToken = default)
        {
            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
                return NotFound("Plugin configuration not available.");

            if (string.IsNullOrWhiteSpace(name))
                return NotFound("Color preset not found.");

            config.ColorPresets ??= new List<HueColorPreset>();
            var preset = config.ColorPresets.FirstOrDefault(candidate =>
                candidate != null &&
                string.Equals(candidate.Name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (preset == null)
                return NotFound("Color preset not found.");

            var validationErrors = PluginConfiguration.ValidateColorPreset(preset);
            if (validationErrors.Count > 0)
            {
                return BadRequest(new
                {
                    message = "The saved scene is invalid.",
                    errors = validationErrors
                });
            }

            request ??= new HueSavedColorPresetPreviewRequest();
            if (ContainsBlankTargetUserId(request.TargetUserIds))
                return BadRequest("Selected saved-scene target IDs must contain user mapping IDs.");

            var targetUserId = request.TargetUserId?.Trim() ?? string.Empty;
            var targetUserIds = request.TargetUserIds?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToList();
            var targetRoutes = NormalizeSceneAutomationTargetRoutes(request.TargetRoutes);
            if (TryGetInvalidSceneAutomationTargetRouteError(targetRoutes) is { } targetRouteError)
                return BadRequest(targetRouteError);
            var includeDefaultTarget = request.IncludeDefaultTarget == true;
            var hasSelectedTargetOverride = includeDefaultTarget ||
                (targetUserIds?.Count > 0) ||
                targetRoutes.Count > 0;
            if (request.TargetAllEnabledMappings && (!string.IsNullOrWhiteSpace(targetUserId) || hasSelectedTargetOverride))
                return BadRequest("A saved-scene preview cannot select all enabled targets and a specific user mapping together.");
            if (!string.IsNullOrWhiteSpace(targetUserId) && hasSelectedTargetOverride)
                return BadRequest("A saved-scene preview cannot combine a specific user mapping with selected targets.");

            if (_streamTester == null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Hue preview service is not available.");

            if (_sceneAutomationService == null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Scene automation service is not available.");

            if (_syncService?.IsSyncing == true)
                return Conflict("Stop active playback before running a Hue scene preview.");

            var previewSchedule = new HueSceneSchedule
            {
                Id = "saved-scene-preview",
                Name = preset.Name?.Trim() ?? name.Trim(),
                PresetName = preset.Name?.Trim() ?? name.Trim(),
                TargetUserId = hasSelectedTargetOverride || request.TargetAllEnabledMappings ? string.Empty : targetUserId,
                TargetUserIds = targetUserIds ?? new List<string>(),
                IncludeDefaultTarget = includeDefaultTarget,
                TargetAllEnabledMappings = request.TargetAllEnabledMappings && !hasSelectedTargetOverride,
                DurationSeconds = 0
            };
            if (!HueSceneAutomationService.TryResolveTargets(
                    config,
                    previewSchedule,
                    out _,
                    out var targetError,
                    targetRoutes))
                return BadRequest(targetError);

            var previewPreset = CloneColorPreset(preset);
            PluginConfiguration.TryNormalizeColorPresetEffect(previewPreset.Effect, out var normalizedEffect);
            previewPreset.Effect = normalizedEffect;
            var previewResult = await _sceneAutomationService.RunPreviewAsync(
                previewSchedule,
                previewPreset,
                targetRoutes,
                cancellationToken).ConfigureAwait(false);
            return Ok(BuildPreviewResult(
                previewResult,
                previewSchedule,
                previewPreset,
                targetRoutes));
        }

        /// <summary>
        /// Previews several saved scenes sequentially through the restorative preview
        /// lifecycle. Every selected scene, target, and validation rule is preflighted
        /// before the first bridge call; a runtime failure is reported per scene while
        /// later scenes continue, and cancellation stops the remaining sequence safely.
        /// </summary>
        [HttpPost("ColorPresets/BulkPreview")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<ActionResult<HueColorPresetBulkPreviewResult>> PreviewColorPresetsBulk(
            [FromBody] HueColorPresetBulkPreviewRequest? request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
                return BadRequest("A saved-scene selection is required.");

            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
                return NotFound("Plugin configuration not available.");

            var presetNames = (request.PresetNames ?? new List<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (presetNames.Length == 0)
                return BadRequest("Select at least one saved scene.");
            if (presetNames.Length > PluginConfiguration.MaxColorPresets)
            {
                return BadRequest(
                    $"Select no more than {PluginConfiguration.MaxColorPresets} saved scenes at once.");
            }

            config.ColorPresets ??= new List<HueColorPreset>();
            var selectedPresets = presetNames
                .Select(name => config.ColorPresets.FirstOrDefault(preset =>
                    preset != null &&
                    string.Equals(preset.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            var missingNames = presetNames
                .Where((_, index) => selectedPresets[index] == null)
                .ToArray();
            if (missingNames.Length > 0)
            {
                return NotFound(new HueColorPresetBulkPreviewResult
                {
                    RequestedCount = presetNames.Length,
                    MissingNames = missingNames,
                    Message = $"The requested saved scene(s) were not found: {string.Join(", ", missingNames)}."
                });
            }

            var validationErrors = selectedPresets
                .Cast<HueColorPreset>()
                .SelectMany(preset => PluginConfiguration.ValidateColorPreset(
                    preset,
                    $"Saved scene '{preset.Name?.Trim() ?? string.Empty}'"))
                .ToArray();
            if (validationErrors.Length > 0)
            {
                return BadRequest(new HueColorPresetBulkPreviewResult
                {
                    RequestedCount = presetNames.Length,
                    ValidationErrors = validationErrors,
                    Message = "One or more selected saved scenes are invalid; no preview was started."
                });
            }

            if (ContainsBlankTargetUserId(request.TargetUserIds))
                return BadRequest("Selected bulk saved-scene target IDs must contain user mapping IDs.");

            var targetUserId = request.TargetUserId?.Trim() ?? string.Empty;
            var targetUserIds = request.TargetUserIds?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToList();
            var targetRoutes = NormalizeSceneAutomationTargetRoutes(request.TargetRoutes);
            if (TryGetInvalidSceneAutomationTargetRouteError(targetRoutes) is { } targetRouteError)
                return BadRequest(targetRouteError);
            var includeDefaultTarget = request.IncludeDefaultTarget == true;
            var hasSelectedTargetOverride = includeDefaultTarget ||
                (targetUserIds?.Count > 0) ||
                targetRoutes.Count > 0;
            if (request.TargetAllEnabledMappings && (!string.IsNullOrWhiteSpace(targetUserId) || hasSelectedTargetOverride))
            {
                return BadRequest(
                    "A bulk saved-scene preview cannot select all enabled targets and a specific user mapping together.");
            }
            if (!string.IsNullOrWhiteSpace(targetUserId) && hasSelectedTargetOverride)
            {
                return BadRequest(
                    "A bulk saved-scene preview cannot combine a specific user mapping with selected targets.");
            }

            if (_streamTester == null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Hue preview service is not available.");
            if (_sceneAutomationService == null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Scene automation service is not available.");
            if (_syncService?.IsSyncing == true)
                return Conflict("Stop active playback before running a bulk Hue scene preview.");

            var targetSchedule = new HueSceneSchedule
            {
                Id = "bulk-saved-scene-preview",
                Name = "Bulk saved-scene preview",
                TargetUserId = hasSelectedTargetOverride || request.TargetAllEnabledMappings ? string.Empty : targetUserId,
                TargetUserIds = targetUserIds ?? new List<string>(),
                IncludeDefaultTarget = includeDefaultTarget,
                TargetAllEnabledMappings = request.TargetAllEnabledMappings && !hasSelectedTargetOverride
            };
            if (!HueSceneAutomationService.TryResolveTargets(
                    config,
                    targetSchedule,
                    out _,
                    out var targetError,
                    targetRoutes))
                return BadRequest(targetError);

            var previews = new List<HueColorPresetBulkPreviewItem>(selectedPresets.Length);
            var canceled = false;
            foreach (var source in selectedPresets.Cast<HueColorPreset>())
            {
                var previewSchedule = new HueSceneSchedule
                {
                    Id = "bulk-saved-scene-preview",
                    Name = source.Name?.Trim() ?? string.Empty,
                    PresetName = source.Name?.Trim() ?? string.Empty,
                    TargetUserId = targetSchedule.TargetUserId,
                    TargetUserIds = targetSchedule.TargetUserIds.ToList(),
                    IncludeDefaultTarget = targetSchedule.IncludeDefaultTarget,
                    TargetAllEnabledMappings = targetSchedule.TargetAllEnabledMappings,
                    DurationSeconds = 0
                };
                var previewPreset = CloneColorPreset(source);
                PluginConfiguration.TryNormalizeColorPresetEffect(previewPreset.Effect, out var normalizedEffect);
                previewPreset.Effect = normalizedEffect;
                try
                {
                    var run = await _sceneAutomationService.RunPreviewAsync(
                        previewSchedule,
                        previewPreset,
                        targetRoutes,
                        cancellationToken).ConfigureAwait(false);
                    previews.Add(new HueColorPresetBulkPreviewItem
                    {
                        Name = previewPreset.Name?.Trim() ?? string.Empty,
                        Preview = BuildPreviewResult(
                            run,
                            previewSchedule,
                            previewPreset,
                            targetRoutes)
                    });
                    if (!run.Succeeded && run.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase))
                    {
                        canceled = true;
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Bulk saved-scene preview failed for {0}", source.Name);
                    previews.Add(new HueColorPresetBulkPreviewItem
                    {
                        Name = source.Name?.Trim() ?? string.Empty,
                        Preview = BuildPreviewResult(
                            new HueSceneAutomationRunResult
                            {
                                TargetAllEnabledMappings = previewSchedule.TargetAllEnabledMappings,
                                Succeeded = false,
                                Message = "The saved-scene preview failed unexpectedly."
                            },
                            previewSchedule,
                            previewPreset,
                            targetRoutes)
                    });
                }
            }

            var succeededCount = previews.Count(item => item.Preview.Succeeded);
            var failedCount = previews.Count - succeededCount;
            return Ok(new HueColorPresetBulkPreviewResult
            {
                RequestedCount = presetNames.Length,
                CompletedCount = previews.Count,
                SucceededCount = succeededCount,
                FailedCount = failedCount,
                Canceled = canceled,
                Message = canceled
                    ? $"Bulk saved-scene preview canceled after {previews.Count} of {presetNames.Length} scene(s)."
                    : failedCount == 0
                        ? $"Previewed {previews.Count} saved scene(s) successfully."
                        : $"Previewed {previews.Count} saved scene(s); {failedCount} failed.",
                Previews = previews
            });
        }

        private static HuePreviewResult BuildPreviewResult(
            HueSceneAutomationRunResult run,
            HueSceneSchedule schedule,
            HueColorPreset preset,
            IReadOnlyList<HueSceneAutomationTargetRoute>? targetRoutes = null)
        {
            PluginConfiguration.TryNormalizeColorPresetEffect(preset.Effect, out var effect);
            var targetResults = run.TargetResults
                .Select(target => new HuePreviewTargetResult
                {
                    TargetLabel = target.TargetLabel,
                    Succeeded = target.Succeeded,
                    Message = target.Message,
                    CleanupWarning = target.CleanupWarning,
                    AvailableChannelCount = target.AvailableChannelCount,
                    SelectedChannelCount = target.SelectedChannelCount
                })
                .ToArray();
            return new HuePreviewResult
            {
                Succeeded = run.Succeeded,
                Message = run.Message,
                CleanupWarning = run.CleanupWarning,
                TargetAllEnabledMappings = schedule.TargetAllEnabledMappings,
                TargetUserIds = schedule.TargetUserIds?.Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                    ?? Array.Empty<string>(),
                TargetRoutes = targetRoutes?
                    .Where(route => route != null &&
                        !string.IsNullOrWhiteSpace(route.UserId) &&
                        !string.IsNullOrWhiteSpace(route.DeviceId))
                    .Select(route => new HueCurrentLightColorTargetRoute
                    {
                        UserId = route.UserId.Trim(),
                        DeviceId = route.DeviceId!.Trim()
                    })
                    .ToArray()
                    ?? Array.Empty<HueCurrentLightColorTargetRoute>(),
                IncludeDefaultTarget = schedule.IncludeDefaultTarget,
                TargetResults = targetResults,
                Red = preset.Red,
                Green = preset.Green,
                Blue = preset.Blue,
                Effect = effect,
                EffectSpeedPercent = PluginConfiguration.ClampColorPresetEffectSpeedPercent(preset.EffectSpeedPercent),
                TransitionCurve = HueSceneAutomationService.GetEffectiveTransitionCurve(preset),
                BrightnessPercent = preset.BrightnessPercent,
                DurationSeconds = HueSceneAutomationService.GetEffectiveDurationSeconds(schedule, preset),
                TransitionSeconds = HueSceneAutomationService.GetEffectiveTransitionSeconds(schedule, preset),
                TransitionOutSeconds = HueSceneAutomationService.GetEffectiveTransitionOutSeconds(schedule, preset),
                AvailableChannelCount = targetResults.Sum(target => target.AvailableChannelCount),
                SelectedChannelCount = targetResults.Sum(target => target.SelectedChannelCount)
            };
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
        /// Lists the credential-free playlist and scheduled-cue dependency graph for one
        /// saved scene so administrators can review dependencies before changing or
        /// deleting it. This includes cues that execute a dependent playlist. Bridge
        /// credentials and target details are never included.
        /// </summary>
        [HttpGet("ColorPresets/{name}/Dependencies")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<HueColorPresetDependenciesResult> GetColorPresetDependencies(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return NotFound("Color preset not found.");

            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return NotFound("Plugin configuration not available.");

            var normalizedName = name.Trim();
            var source = config.ColorPresets?.FirstOrDefault(preset =>
                preset != null &&
                string.Equals(preset.Name?.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase));
            if (source == null)
                return NotFound("Color preset not found.");

            return Ok(BuildColorPresetDependenciesResult(source, config));
        }

        private static HueColorPresetDependenciesResult BuildColorPresetDependenciesResult(
            HueColorPreset source,
            PluginConfiguration config)
        {
            var normalizedName = source.Name?.Trim() ?? string.Empty;
            var playlists = (config.ScenePlaylists ?? new List<HueScenePlaylist>())
                .Where(playlist => playlist != null &&
                    playlist.PresetNames?.Any(presetName =>
                        string.Equals(presetName?.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase)) == true)
                .Select(playlist => new HueColorPresetPlaylistDependencyResult
                {
                    Id = playlist.Id?.Trim() ?? string.Empty,
                    Name = playlist.Name?.Trim() ?? string.Empty,
                    ReferenceCount = playlist.PresetNames?.Count(presetName =>
                        string.Equals(presetName?.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase)) ?? 0
                })
                .OrderBy(playlist => playlist.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(playlist => playlist.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var dependentPlaylistNames = new HashSet<string>(
                playlists
                    .Select(playlist => playlist.Name)
                    .Where(playlistName => !string.IsNullOrWhiteSpace(playlistName)),
                StringComparer.OrdinalIgnoreCase);
            var directSchedules = (config.SceneSchedules ?? new List<HueSceneSchedule>())
                .Where(schedule => schedule != null &&
                    string.Equals(schedule.PresetName?.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase))
                .Select(schedule => new HueColorPresetScheduleDependencyResult
                {
                    Id = schedule.Id?.Trim() ?? string.Empty,
                    Name = schedule.Name?.Trim() ?? string.Empty,
                    Enabled = schedule.Enabled,
                    ReferenceType = "DirectScene"
                });
            var playlistSchedules = (config.SceneSchedules ?? new List<HueSceneSchedule>())
                .Where(schedule => schedule != null &&
                    !string.Equals(schedule.PresetName?.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase) &&
                    dependentPlaylistNames.Contains(schedule.PlaylistName?.Trim() ?? string.Empty))
                .Select(schedule => new HueColorPresetScheduleDependencyResult
                {
                    Id = schedule.Id?.Trim() ?? string.Empty,
                    Name = schedule.Name?.Trim() ?? string.Empty,
                    Enabled = schedule.Enabled,
                    ReferenceType = "Playlist",
                    PlaylistName = schedule.PlaylistName?.Trim() ?? string.Empty
                });
            var schedules = playlistSchedules
                .Concat(directSchedules)
                .OrderBy(schedule => schedule.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(schedule => schedule.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new HueColorPresetDependenciesResult
            {
                Name = source.Name?.Trim() ?? normalizedName,
                CanDelete = playlists.Length == 0 && schedules.Length == 0,
                PlaylistCount = playlists.Length,
                ScheduledCueCount = schedules.Length,
                Playlists = playlists,
                ScheduledCues = schedules
            };
        }

        /// <summary>
        /// Saves or updates a reusable preview scene by case-insensitive name.
        /// </summary>
        [HttpPost("ColorPresets")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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
            var previousPresets = config.ColorPresets;
            var candidatePresets = previousPresets.ToList();
            var existingIndex = candidatePresets.FindIndex(existing =>
                existing != null &&
                string.Equals(existing.Name?.Trim(), preset.Name, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                candidatePresets[existingIndex] = preset;
            }
            else
            {
                if (candidatePresets.Count >= PluginConfiguration.MaxColorPresets)
                {
                    return BadRequest(new
                    {
                        message = $"No more than {PluginConfiguration.MaxColorPresets} color presets may be saved.",
                        errors = new[] { $"No more than {PluginConfiguration.MaxColorPresets} color presets may be saved" }
                    });
                }

                candidatePresets.Add(preset);
            }

            config.ColorPresets = candidatePresets;
            try
            {
                plugin.SaveConfiguration();
            }
            catch (Exception ex)
            {
                config.ColorPresets = previousPresets;
                _logger?.LogError(ex, "Could not persist Hue color preset {0}", preset.Name);
                return StatusCode(StatusCodes.Status500InternalServerError, "The color preset could not be saved.");
            }

            return Ok(ToColorPresetResult(preset));
        }

        /// <summary>
        /// Renames one reusable preview scene while migrating every saved-playlist and
        /// direct scheduled-cue reference that uses the old name. The complete candidate
        /// scene configuration is validated before it replaces the current configuration.
        /// </summary>
        [HttpPost("ColorPresets/{name}/Rename")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public ActionResult<HueColorPresetResult> RenameColorPreset(
            string name,
            [FromBody] HueColorPresetRenameRequest? request)
        {
            if (string.IsNullOrWhiteSpace(name))
                return NotFound("Color preset not found.");

            if (request == null || string.IsNullOrWhiteSpace(request.NewName))
                return BadRequest("A new color preset name is required.");

            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
                return NotFound("Plugin configuration not available.");

            var sourceName = name.Trim();
            var targetName = request.NewName.Trim();
            config.ColorPresets ??= new List<HueColorPreset>();
            var source = config.ColorPresets.FirstOrDefault(preset =>
                preset != null &&
                string.Equals(preset.Name?.Trim(), sourceName, StringComparison.OrdinalIgnoreCase));
            if (source == null)
                return NotFound("Color preset not found.");

            if (string.Equals(source.Name?.Trim(), targetName, StringComparison.Ordinal))
                return BadRequest("The new color preset name must differ from the current name.");

            var collision = config.ColorPresets.Any(preset =>
                preset != null &&
                !ReferenceEquals(preset, source) &&
                string.Equals(preset.Name?.Trim(), targetName, StringComparison.OrdinalIgnoreCase));
            if (collision)
                return Conflict("A color preset with the new name already exists.");

            var previousPresets = config.ColorPresets;
            var previousPlaylists = config.ScenePlaylists ?? new List<HueScenePlaylist>();
            var previousSchedules = config.SceneSchedules ?? new List<HueSceneSchedule>();
            var candidatePresets = previousPresets
                .Where(preset => preset != null)
                .Select(CloneColorPreset)
                .ToList();
            var candidateSource = candidatePresets.First(preset =>
                string.Equals(preset.Name?.Trim(), sourceName, StringComparison.OrdinalIgnoreCase));
            candidateSource.Name = targetName;

            var candidatePlaylists = previousPlaylists
                .Where(playlist => playlist != null)
                .Select(playlist =>
                {
                    var clone = CloneScenePlaylist(playlist);
                    clone.PresetNames = (clone.PresetNames ?? new List<string>())
                        .Select(presetName => presetName != null && string.Equals(
                                presetName.Trim(),
                                sourceName,
                                StringComparison.OrdinalIgnoreCase)
                            ? targetName
                            : presetName!)
                        .ToList();
                    return clone;
                })
                .ToList();
            var candidateSchedules = previousSchedules
                .Where(schedule => schedule != null)
                .Select(schedule =>
                {
                    var clone = CloneSceneSchedule(schedule);
                    if (string.Equals(
                            clone.PresetName?.Trim(),
                            sourceName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        clone.PresetName = targetName;
                    }

                    return clone;
                })
                .ToList();

            var validationConfiguration = new PluginConfiguration
            {
                ColorPresets = candidatePresets,
                ScenePlaylists = candidatePlaylists,
                SceneSchedules = candidateSchedules,
                UserMappings = config.UserMappings ?? new List<UserBridgeMapping>()
            };
            var validationErrors = validationConfiguration.ValidateColorPresets();
            validationErrors.AddRange(validationConfiguration.ValidateScenePlaylists());
            validationErrors.AddRange(validationConfiguration.ValidateSceneSchedules());
            if (validationErrors.Count > 0)
            {
                return BadRequest(new
                {
                    message = "The renamed color preset configuration is invalid.",
                    errors = validationErrors
                });
            }

            config.ColorPresets = candidatePresets;
            config.ScenePlaylists = candidatePlaylists;
            config.SceneSchedules = candidateSchedules;
            try
            {
                plugin.SaveConfiguration();
            }
            catch (Exception ex)
            {
                config.ColorPresets = previousPresets;
                config.ScenePlaylists = previousPlaylists;
                config.SceneSchedules = previousSchedules;
                _logger?.LogError(ex, "Could not persist renamed Hue color preset {0}", sourceName);
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    "The color preset rename could not be saved.");
            }

            return Ok(ToColorPresetResult(candidateSource));
        }

        /// <summary>
        /// Creates a safe copy of one reusable preview scene. The copy keeps all visual
        /// and transition metadata, receives a bounded unique name, and can be edited
        /// independently without changing the source scene or its scheduled cues.
        /// </summary>
        [HttpPost("ColorPresets/{name}/Duplicate")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public ActionResult<HueColorPresetResult> DuplicateColorPreset(string name)
        {
            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
                return NotFound("Plugin configuration not available.");

            if (string.IsNullOrWhiteSpace(name))
                return NotFound("Color preset not found.");

            config.ColorPresets ??= new List<HueColorPreset>();
            var source = config.ColorPresets.FirstOrDefault(preset =>
                preset != null &&
                string.Equals(preset.Name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (source == null)
                return NotFound("Color preset not found.");

            if (config.ColorPresets.Count >= PluginConfiguration.MaxColorPresets)
            {
                return Conflict(
                    $"No more than {PluginConfiguration.MaxColorPresets} color presets may be saved.");
            }

            var duplicate = CloneColorPreset(source);
            duplicate.Name = BuildDuplicateColorPresetName(config.ColorPresets, source.Name);

            var previousPresets = config.ColorPresets;
            var candidatePresets = previousPresets
                .Where(preset => preset != null)
                .Select(CloneColorPreset)
                .ToList();
            candidatePresets.Add(duplicate);
            config.ColorPresets = candidatePresets;
            var validationErrors = config.ValidateColorPresets();
            if (validationErrors.Count > 0)
            {
                config.ColorPresets = previousPresets;
                return BadRequest(new
                {
                    message = "The color preset copy is invalid.",
                    errors = validationErrors
                });
            }

            try
            {
                plugin.SaveConfiguration();
            }
            catch (Exception ex)
            {
                config.ColorPresets = previousPresets;
                _logger?.LogError(ex, "Could not persist duplicate Hue color preset {0}", source.Name);
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    "The color preset copy could not be saved.");
            }

            return Ok(ToColorPresetResult(duplicate));
        }

        /// <summary>
        /// Creates independent copies of several reusable preview scenes in one atomic
        /// administrator operation. Every source is resolved before capacity, validation,
        /// or persistence is attempted, and the original scenes remain unchanged.
        /// </summary>
        [HttpPost("ColorPresets/BulkDuplicate")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public ActionResult<HueColorPresetBulkDuplicateResult> DuplicateColorPresetsBulk(
            [FromBody] HueColorPresetBulkDuplicateRequest? request)
        {
            if (request == null)
                return BadRequest("A saved-scene selection is required.");

            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
                return NotFound("Plugin configuration not available.");

            var presetNames = (request.PresetNames ?? new List<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (presetNames.Length == 0)
                return BadRequest("Select at least one saved scene.");
            if (presetNames.Length > PluginConfiguration.MaxColorPresets)
            {
                return BadRequest(
                    $"Select no more than {PluginConfiguration.MaxColorPresets} saved scenes at once.");
            }

            config.ColorPresets ??= new List<HueColorPreset>();
            var selectedPresets = presetNames
                .Select(name => config.ColorPresets.FirstOrDefault(preset =>
                    preset != null &&
                    string.Equals(preset.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            var missingNames = presetNames
                .Where((_, index) => selectedPresets[index] == null)
                .ToArray();
            if (missingNames.Length > 0)
            {
                return NotFound(new HueColorPresetBulkDuplicateResult
                {
                    RequestedCount = presetNames.Length,
                    MissingNames = missingNames,
                    Message = $"The requested saved scene(s) were not found: {string.Join(", ", missingNames)}."
                });
            }

            var availableCapacity = Math.Max(0, PluginConfiguration.MaxColorPresets - config.ColorPresets.Count);
            if (presetNames.Length > availableCapacity)
            {
                return Conflict(new HueColorPresetBulkDuplicateResult
                {
                    RequestedCount = presetNames.Length,
                    AvailableCapacity = availableCapacity,
                    Message = $"Only {availableCapacity} saved-scene slot(s) remain; no copies were created."
                });
            }

            var presets = selectedPresets.Cast<HueColorPreset>().ToArray();
            var previousPresets = config.ColorPresets;
            var candidatePresets = previousPresets
                .Where(preset => preset != null)
                .Select(CloneColorPreset)
                .ToList();
            var duplicates = new List<HueColorPreset>(presets.Length);
            foreach (var source in presets)
            {
                var duplicate = CloneColorPreset(source);
                duplicate.Name = BuildDuplicateColorPresetName(candidatePresets, source.Name);
                candidatePresets.Add(duplicate);
                duplicates.Add(duplicate);
            }

            config.ColorPresets = candidatePresets;
            var validationErrors = config.ValidateColorPresets();
            if (validationErrors.Count > 0)
            {
                config.ColorPresets = previousPresets;
                return BadRequest(new HueColorPresetBulkDuplicateResult
                {
                    RequestedCount = presetNames.Length,
                    ValidationErrors = validationErrors,
                    Message = "The selected saved-scene copies are invalid; no scenes were created."
                });
            }

            try
            {
                plugin.SaveConfiguration();
            }
            catch (Exception ex)
            {
                config.ColorPresets = previousPresets;
                _logger?.LogError(ex, "Could not persist bulk duplication of Hue color presets");
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    "The selected saved-scene copies could not be saved; no changes were retained.");
            }

            return Ok(new HueColorPresetBulkDuplicateResult
            {
                RequestedCount = presetNames.Length,
                DuplicatedCount = duplicates.Count,
                RemainingCount = config.ColorPresets.Count,
                AvailableCapacity = Math.Max(0, PluginConfiguration.MaxColorPresets - config.ColorPresets.Count),
                Message = $"Created {duplicates.Count} independent saved-scene copy(ies) atomically.",
                Presets = duplicates.Select(ToColorPresetResult).ToArray()
            });
        }

        /// <summary>
        /// Deletes one reusable preview scene by name. A scene that is referenced by a
        /// saved playlist or scheduled cue is retained until those references are removed
        /// or changed, preventing an otherwise valid configuration from being broken.
        /// </summary>
        [HttpDelete("ColorPresets/{name}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public ActionResult DeleteColorPreset(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return NotFound("Color preset not found.");

            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
                return NotFound("Plugin configuration not available.");

            var normalizedName = name.Trim();
            var referencedPlaylistCount = config.ScenePlaylists?.Count(playlist =>
                playlist != null &&
                playlist.PresetNames?.Any(presetName =>
                    string.Equals(presetName?.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase)) == true) ?? 0;
            var referencedScheduleCount = config.SceneSchedules?.Count(schedule =>
                schedule != null &&
                string.Equals(schedule.PresetName?.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase)) ?? 0;
            if (referencedPlaylistCount > 0 || referencedScheduleCount > 0)
            {
                var dependencies = new List<string>();
                if (referencedPlaylistCount > 0)
                    dependencies.Add($"{referencedPlaylistCount} playlist(s)");
                if (referencedScheduleCount > 0)
                    dependencies.Add($"{referencedScheduleCount} scheduled cue(s)");
                return Conflict(
                    $"The saved scene is referenced by {string.Join(" and ", dependencies)}. Delete or update those references first.");
            }

            config.ColorPresets ??= new List<HueColorPreset>();
            var previousPresets = config.ColorPresets;
            var candidatePresets = previousPresets.ToList();
            var removed = candidatePresets.RemoveAll(existing =>
                existing != null &&
                string.Equals(existing.Name?.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
                return NotFound("Color preset not found.");

            config.ColorPresets = candidatePresets;
            try
            {
                plugin.SaveConfiguration();
            }
            catch (Exception ex)
            {
                config.ColorPresets = previousPresets;
                _logger?.LogError(ex, "Could not persist deleted Hue color preset {0}", normalizedName);
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    "The color preset deletion could not be saved.");
            }

            return Ok(new { message = "Color preset deleted successfully." });
        }

        /// <summary>
        /// Deletes several saved scenes by normalized name in one administrator operation.
        /// Every selected scene is resolved and checked for playlist, direct-cue, and
        /// playlist-backed cue references before mutation; any missing name, dependency,
        /// or persistence failure leaves the complete scene collection unchanged.
        /// </summary>
        [HttpPost("ColorPresets/BulkDelete")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public ActionResult<HueColorPresetBulkDeleteResult> DeleteColorPresetsBulk(
            [FromBody] HueColorPresetBulkDeleteRequest? request)
        {
            if (request == null)
                return BadRequest("A saved-scene selection is required.");

            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
                return NotFound("Plugin configuration not available.");

            var presetNames = (request.PresetNames ?? new List<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (presetNames.Length == 0)
                return BadRequest("Select at least one saved scene.");
            if (presetNames.Length > PluginConfiguration.MaxColorPresets)
            {
                return BadRequest(
                    $"Select no more than {PluginConfiguration.MaxColorPresets} saved scenes at once.");
            }

            config.ColorPresets ??= new List<HueColorPreset>();
            var selectedPresets = presetNames
                .Select(name => config.ColorPresets.FirstOrDefault(preset =>
                    preset != null &&
                    string.Equals(preset.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            var missingNames = presetNames
                .Where((_, index) => selectedPresets[index] == null)
                .ToArray();
            if (missingNames.Length > 0)
            {
                return NotFound(new HueColorPresetBulkDeleteResult
                {
                    RequestedCount = presetNames.Length,
                    MissingNames = missingNames,
                    Message = $"The requested saved scene(s) were not found: {string.Join(", ", missingNames)}."
                });
            }

            var presets = selectedPresets
                .Where(preset => preset != null)
                .Cast<HueColorPreset>()
                .ToArray();
            var blockedPresets = presets
                .Select(preset => BuildColorPresetDependenciesResult(preset, config))
                .Where(dependencies => !dependencies.CanDelete)
                .ToArray();
            if (blockedPresets.Length > 0)
            {
                return Conflict(new HueColorPresetBulkDeleteResult
                {
                    RequestedCount = presetNames.Length,
                    Message = "One or more selected saved scenes are still referenced. Delete or update those references first; no scenes were deleted.",
                    BlockedPresets = blockedPresets
                });
            }

            var previousPresets = config.ColorPresets;
            var selectedNames = new HashSet<string>(presetNames, StringComparer.OrdinalIgnoreCase);
            var candidatePresets = previousPresets
                .Where(preset => preset == null || !selectedNames.Contains(preset.Name?.Trim() ?? string.Empty))
                .ToList();
            var deletedCount = previousPresets.Count - candidatePresets.Count;
            var deletedResults = presets
                .Select(ToColorPresetResult)
                .ToArray();
            config.ColorPresets = candidatePresets;
            try
            {
                plugin.SaveConfiguration();
            }
            catch (Exception ex)
            {
                config.ColorPresets = previousPresets;
                _logger?.LogError(ex, "Could not persist bulk deletion of Hue color presets");
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    "The selected saved scenes could not be deleted; no changes were retained.");
            }

            return Ok(new HueColorPresetBulkDeleteResult
            {
                RequestedCount = presetNames.Length,
                DeletedCount = deletedCount,
                RemainingCount = config.ColorPresets.Count,
                Message = $"Deleted {deletedCount} saved scene(s); playlist and scheduled-cue references were checked atomically.",
                Presets = deletedResults
            });
        }

        /// <summary>
        /// Lists ordered saved-scene playlists without returning bridge credentials.
        /// </summary>
        [HttpGet("ScenePlaylists")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<IEnumerable<HueScenePlaylistResult>> GetScenePlaylists()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return NotFound("Plugin configuration not available.");

            config.ScenePlaylists ??= new List<HueScenePlaylist>();
            return Ok(config.ScenePlaylists
                .Where(playlist => playlist != null)
                .OrderBy(playlist => playlist.Name, StringComparer.OrdinalIgnoreCase)
                .Select(playlist => ToScenePlaylistResult(playlist, config)));
        }

        /// <summary>
        /// Inspects the scheduled-cue references for one saved-scene playlist without
        /// returning bridge credentials or target details. The result is suitable for
        /// checking whether deletion can proceed before changing the playlist.
        /// </summary>
        [HttpGet("ScenePlaylists/{name}/Dependencies")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<HueScenePlaylistDependenciesResult> GetScenePlaylistDependencies(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return NotFound("Scene playlist not found.");

            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return NotFound("Plugin configuration not available.");

            var normalizedName = name.Trim();
            var source = config.ScenePlaylists?.FirstOrDefault(playlist =>
                playlist != null &&
                string.Equals(playlist.Name?.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase));
            if (source == null)
                return NotFound("Scene playlist not found.");

            var schedules = (config.SceneSchedules ?? new List<HueSceneSchedule>())
                .Where(schedule => schedule != null &&
                    string.Equals(schedule.PlaylistName?.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase))
                .Select(schedule => new HueScenePlaylistScheduleDependencyResult
                {
                    Id = schedule.Id?.Trim() ?? string.Empty,
                    Name = schedule.Name?.Trim() ?? string.Empty,
                    Enabled = schedule.Enabled
                })
                .OrderBy(schedule => schedule.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(schedule => schedule.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Ok(new HueScenePlaylistDependenciesResult
            {
                Id = source.Id?.Trim() ?? string.Empty,
                Name = source.Name?.Trim() ?? normalizedName,
                CanDelete = schedules.Length == 0,
                ScheduledCueCount = schedules.Length,
                ScheduledCues = schedules
            });
        }

        /// <summary>
        /// Saves or updates an ordered saved-scene playlist. Only scene references, optional
        /// bounded per-step duration, RGB channel, brightness, effect, effect-speed, transition,
        /// and transition-curve overrides, repeat count, playback order, and target mode
        /// are persisted; credentials remain in the server configuration.
        /// </summary>
        [HttpPost("ScenePlaylists")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<HueScenePlaylistResult> SaveScenePlaylist(
            [FromBody] HueScenePlaylistRequest? request)
        {
            if (request == null)
                return BadRequest("A scene playlist is required.");

            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
                return NotFound("Plugin configuration not available.");

            var playlist = request.ToConfigurationPlaylist();
            if (string.IsNullOrWhiteSpace(playlist.Id))
                playlist.Id = Guid.NewGuid().ToString("N");
            config.ScenePlaylists ??= new List<HueScenePlaylist>();
            var existingIndex = config.ScenePlaylists.FindIndex(existing =>
                existing != null &&
                string.Equals(existing.Id?.Trim(), playlist.Id.Trim(), StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0 && request.StepDurationSeconds == null)
            {
                playlist.StepDurationSeconds = config.ScenePlaylists[existingIndex]?.StepDurationSeconds?.ToList()
                    ?? new List<int>();
            }
            if (existingIndex >= 0 && request.StepRed == null)
            {
                playlist.StepRed = config.ScenePlaylists[existingIndex]?.StepRed?.ToList()
                    ?? new List<int?>();
            }
            if (existingIndex >= 0 && request.StepGreen == null)
            {
                playlist.StepGreen = config.ScenePlaylists[existingIndex]?.StepGreen?.ToList()
                    ?? new List<int?>();
            }
            if (existingIndex >= 0 && request.StepBlue == null)
            {
                playlist.StepBlue = config.ScenePlaylists[existingIndex]?.StepBlue?.ToList()
                    ?? new List<int?>();
            }
            if (existingIndex >= 0 && request.StepBrightnessPercent == null)
            {
                playlist.StepBrightnessPercent = config.ScenePlaylists[existingIndex]?.StepBrightnessPercent?.ToList()
                    ?? new List<int?>();
            }
            if (existingIndex >= 0 && request.StepEffects == null)
            {
                playlist.StepEffects = config.ScenePlaylists[existingIndex]?.StepEffects?.ToList()
                    ?? new List<string?>();
            }
            if (existingIndex >= 0 && request.StepEffectSpeedPercent == null)
            {
                playlist.StepEffectSpeedPercent = config.ScenePlaylists[existingIndex]?.StepEffectSpeedPercent?.ToList()
                    ?? new List<int?>();
            }
            if (existingIndex >= 0 && request.StepTransitionSeconds == null)
            {
                playlist.StepTransitionSeconds = config.ScenePlaylists[existingIndex]?.StepTransitionSeconds?.ToList()
                    ?? new List<int?>();
            }
            if (existingIndex >= 0 && request.StepTransitionOutSeconds == null)
            {
                playlist.StepTransitionOutSeconds = config.ScenePlaylists[existingIndex]?.StepTransitionOutSeconds?.ToList()
                    ?? new List<int?>();
            }
            if (existingIndex >= 0 && request.StepTransitionCurves == null)
            {
                playlist.StepTransitionCurves = config.ScenePlaylists[existingIndex]?.StepTransitionCurves?.ToList()
                    ?? new List<string?>();
            }
            var previousPlaylistName = existingIndex >= 0
                ? config.ScenePlaylists[existingIndex]?.Name?.Trim() ?? string.Empty
                : string.Empty;
            var candidatePlaylists = config.ScenePlaylists
                .Where(existing => existing != null)
                .Select(CloneScenePlaylist)
                .ToList();
            if (existingIndex >= 0)
            {
                var candidateIndex = candidatePlaylists.FindIndex(existing =>
                    string.Equals(existing.Id?.Trim(), playlist.Id.Trim(), StringComparison.OrdinalIgnoreCase));
                candidatePlaylists[candidateIndex] = playlist;
            }
            else
            {
                if (candidatePlaylists.Count >= PluginConfiguration.MaxScenePlaylists)
                {
                    return BadRequest(new
                    {
                        message = $"No more than {PluginConfiguration.MaxScenePlaylists} scene playlists may be saved.",
                        errors = new[] { $"No more than {PluginConfiguration.MaxScenePlaylists} scene playlists may be saved" }
                    });
                }

                candidatePlaylists.Add(playlist);
            }

            // Scheduled cues reference playlists by their credential-free name. When an
            // existing playlist is renamed, carry that reference change into the same
            // configuration save so cues never become silently unresolvable.
            var previousSchedules = (config.SceneSchedules ?? new List<HueSceneSchedule>()).ToList();
            var candidateSchedules = previousSchedules
                .Where(schedule => schedule != null)
                .Select(CloneSceneSchedule)
                .ToList();
            var normalizedPlaylistName = playlist.Name?.Trim() ?? string.Empty;
            if (existingIndex >= 0 &&
                !string.IsNullOrWhiteSpace(previousPlaylistName) &&
                !string.Equals(previousPlaylistName, normalizedPlaylistName, StringComparison.Ordinal))
            {
                foreach (var schedule in candidateSchedules.Where(schedule =>
                             !string.IsNullOrWhiteSpace(schedule.PlaylistName) &&
                             string.Equals(schedule.PlaylistName.Trim(), previousPlaylistName, StringComparison.OrdinalIgnoreCase)))
                {
                    schedule.PlaylistName = normalizedPlaylistName;
                }
            }

            var validationConfiguration = new PluginConfiguration
            {
                ColorPresets = config.ColorPresets ?? new List<HueColorPreset>(),
                UserMappings = config.UserMappings ?? new List<UserBridgeMapping>(),
                ScenePlaylists = candidatePlaylists,
                SceneSchedules = candidateSchedules
            };
            var validationErrors = validationConfiguration.ValidateScenePlaylists();
            validationErrors.AddRange(validationConfiguration.ValidateSceneSchedules());
            if (validationErrors.Count > 0)
            {
                return BadRequest(new
                {
                    message = "Scene playlist is invalid.",
                    errors = validationErrors
                });
            }

            var previousPlaylists = config.ScenePlaylists.ToList();
            var previousConfiguredSchedules = config.SceneSchedules ?? new List<HueSceneSchedule>();
            config.ScenePlaylists = candidatePlaylists;
            config.SceneSchedules = candidateSchedules;
            try
            {
                plugin.SaveConfiguration();
            }
            catch (Exception ex)
            {
                config.ScenePlaylists = previousPlaylists;
                config.SceneSchedules = previousConfiguredSchedules;
                _logger?.LogError(ex, "Could not persist Hue scene playlist {0}", playlist.Name);
                return StatusCode(StatusCodes.Status500InternalServerError, "The scene playlist could not be saved.");
            }

            return Ok(ToScenePlaylistResult(playlist, config));
        }

        /// <summary>
        /// Renames one saved-scene playlist while migrating every scheduled-cue
        /// reference that uses the old credential-free name. The complete candidate
        /// playlist and cue configuration is validated before persistence.
        /// </summary>
        [HttpPost("ScenePlaylists/{name}/Rename")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public ActionResult<HueScenePlaylistResult> RenameScenePlaylist(
            string name,
            [FromBody] HueScenePlaylistRenameRequest? request)
        {
            if (string.IsNullOrWhiteSpace(name))
                return NotFound("Scene playlist not found.");

            if (request == null || string.IsNullOrWhiteSpace(request.NewName))
                return BadRequest("A new scene playlist name is required.");

            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
                return NotFound("Plugin configuration not available.");

            var sourceName = name.Trim();
            var targetName = request.NewName.Trim();
            config.ScenePlaylists ??= new List<HueScenePlaylist>();
            var source = config.ScenePlaylists.FirstOrDefault(playlist =>
                playlist != null &&
                string.Equals(playlist.Name?.Trim(), sourceName, StringComparison.OrdinalIgnoreCase));
            if (source == null)
                return NotFound("Scene playlist not found.");

            if (string.Equals(source.Name?.Trim(), targetName, StringComparison.Ordinal))
                return BadRequest("The new scene playlist name must differ from the current name.");

            var collision = config.ScenePlaylists.Any(playlist =>
                playlist != null &&
                !ReferenceEquals(playlist, source) &&
                string.Equals(playlist.Name?.Trim(), targetName, StringComparison.OrdinalIgnoreCase));
            if (collision)
                return Conflict("A scene playlist with the new name already exists.");

            var previousPlaylists = config.ScenePlaylists;
            var previousSchedules = config.SceneSchedules ?? new List<HueSceneSchedule>();
            var candidatePlaylists = previousPlaylists
                .Where(playlist => playlist != null)
                .Select(CloneScenePlaylist)
                .ToList();
            var candidateSource = candidatePlaylists.First(playlist =>
                string.Equals(playlist.Name?.Trim(), sourceName, StringComparison.OrdinalIgnoreCase));
            candidateSource.Name = targetName;

            var candidateSchedules = previousSchedules
                .Where(schedule => schedule != null)
                .Select(schedule =>
                {
                    var clone = CloneSceneSchedule(schedule);
                    if (string.Equals(
                            clone.PlaylistName?.Trim(),
                            sourceName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        clone.PlaylistName = targetName;
                    }

                    return clone;
                })
                .ToList();

            var validationConfiguration = new PluginConfiguration
            {
                ColorPresets = config.ColorPresets ?? new List<HueColorPreset>(),
                UserMappings = config.UserMappings ?? new List<UserBridgeMapping>(),
                ScenePlaylists = candidatePlaylists,
                SceneSchedules = candidateSchedules
            };
            var validationErrors = validationConfiguration.ValidateScenePlaylists();
            validationErrors.AddRange(validationConfiguration.ValidateSceneSchedules());
            if (validationErrors.Count > 0)
            {
                return BadRequest(new
                {
                    message = "The renamed scene playlist configuration is invalid.",
                    errors = validationErrors
                });
            }

            config.ScenePlaylists = candidatePlaylists;
            config.SceneSchedules = candidateSchedules;
            try
            {
                plugin.SaveConfiguration();
            }
            catch (Exception ex)
            {
                config.ScenePlaylists = previousPlaylists;
                config.SceneSchedules = previousSchedules;
                _logger?.LogError(ex, "Could not persist renamed Hue scene playlist {0}", sourceName);
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    "The scene playlist rename could not be saved.");
            }

            return Ok(ToScenePlaylistResult(candidateSource, config));
        }

        /// <summary>
        /// Previews one saved-scene playlist sequentially. The optional request can select
        /// a different credential-free target mode for this run without changing the saved
        /// playlist; all target credentials and channel profiles stay server-side.
        /// </summary>
        [HttpPost("ScenePlaylists/{name}/Preview")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<ActionResult<HueScenePlaylistRunResult>> PreviewScenePlaylist(
            string name,
            [FromBody] HueScenePlaylistPreviewRequest? request,
            CancellationToken cancellationToken = default)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return NotFound("Plugin configuration not available.");

            if (string.IsNullOrWhiteSpace(name))
                return NotFound("Scene playlist not found.");

            config.ScenePlaylists ??= new List<HueScenePlaylist>();
            var source = config.ScenePlaylists.FirstOrDefault(playlist =>
                playlist != null &&
                string.Equals(playlist.Name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (source == null)
                return NotFound("Scene playlist not found.");

            var playlist = CloneScenePlaylist(source);
            request ??= new HueScenePlaylistPreviewRequest();
            if (ContainsBlankTargetUserId(request.TargetUserIds))
                return BadRequest("Selected scene playlist target IDs must contain user mapping IDs.");

            var targetUserId = request.TargetUserId?.Trim() ?? string.Empty;
            var targetUserIds = request.TargetUserIds?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var targetRoutes = NormalizeSceneAutomationTargetRoutes(request.TargetRoutes);
            if (TryGetInvalidSceneAutomationTargetRouteError(targetRoutes) is { } targetRouteError)
                return BadRequest(targetRouteError);
            var includeDefaultTarget = request.IncludeDefaultTarget == true;
            var hasSelectedTargetOverride = includeDefaultTarget ||
                (targetUserIds?.Count > 0) ||
                targetRoutes.Count > 0;
            if ((request.TargetAllEnabledMappings == true || !string.IsNullOrWhiteSpace(targetUserId)) &&
                hasSelectedTargetOverride)
            {
                return BadRequest("A scene playlist preview cannot combine legacy and selected target modes.");
            }
            if (request.TargetAllEnabledMappings == true && !string.IsNullOrWhiteSpace(targetUserId))
                return BadRequest("A scene playlist preview cannot select all enabled targets and a specific user mapping together.");
            if (hasSelectedTargetOverride)
            {
                playlist.TargetAllEnabledMappings = false;
                playlist.TargetUserId = string.Empty;
                playlist.TargetUserIds = targetUserIds ?? new List<string>();
                playlist.IncludeDefaultTarget = includeDefaultTarget;
            }
            else if (request.TargetAllEnabledMappings.HasValue)
            {
                playlist.TargetAllEnabledMappings = request.TargetAllEnabledMappings.Value;
                playlist.TargetUserId = request.TargetAllEnabledMappings.Value ? string.Empty : targetUserId;
                playlist.TargetUserIds = new List<string>();
                playlist.IncludeDefaultTarget = false;
            }
            else if (!string.IsNullOrWhiteSpace(targetUserId))
            {
                playlist.TargetAllEnabledMappings = false;
                playlist.TargetUserId = targetUserId;
                playlist.TargetUserIds = new List<string>();
                playlist.IncludeDefaultTarget = false;
            }

            var validationErrors = PluginConfiguration.ValidateScenePlaylist(playlist, config);
            if (validationErrors.Count > 0)
            {
                return BadRequest(new
                {
                    message = "The scene playlist is invalid.",
                    errors = validationErrors
                });
            }

            var targetSchedule = new HueSceneSchedule
            {
                Id = "scene-playlist-preview",
                Name = playlist.Name?.Trim() ?? string.Empty,
                TargetUserId = playlist.TargetAllEnabledMappings ||
                    playlist.IncludeDefaultTarget ||
                    (playlist.TargetUserIds?.Count ?? 0) > 0
                    ? string.Empty
                    : playlist.TargetUserId?.Trim() ?? string.Empty,
                TargetUserIds = playlist.TargetUserIds?.ToList() ?? new List<string>(),
                IncludeDefaultTarget = playlist.IncludeDefaultTarget,
                TargetAllEnabledMappings = playlist.TargetAllEnabledMappings
            };
            if (!HueSceneAutomationService.TryResolveTargets(
                    config,
                    targetSchedule,
                    out _,
                    out var targetError,
                    hasSelectedTargetOverride ? targetRoutes : null))
            {
                return BadRequest(targetError);
            }

            if (_streamTester == null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Hue preview service is not available.");
            if (_sceneAutomationService == null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Scene automation service is not available.");
            if (_syncService?.IsSyncing == true)
                return Conflict("Stop active playback before running a Hue scene playlist.");

            var result = await _sceneAutomationService.RunPlaylistPreviewAsync(
                playlist,
                cancellationToken,
                hasSelectedTargetOverride ? targetUserIds ?? new List<string>() : null,
                hasSelectedTargetOverride && includeDefaultTarget,
                targetRoutesOverride: hasSelectedTargetOverride ? targetRoutes : null).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// Previews several saved-scene playlists sequentially. Every selected playlist,
        /// referenced scene, target override, and validation rule is preflighted before the
        /// first bridge call; a runtime failure is retained per playlist while later
        /// playlists continue, and cancellation stops the remaining sequence safely.
        /// </summary>
        [HttpPost("ScenePlaylists/BulkPreview")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<ActionResult<HueScenePlaylistBulkPreviewResult>> PreviewScenePlaylistsBulk(
            [FromBody] HueScenePlaylistBulkPreviewRequest? request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
                return BadRequest("A playlist selection is required.");

            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
                return NotFound("Plugin configuration not available.");

            var playlistIds = (request.PlaylistIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (playlistIds.Length == 0)
                return BadRequest("Select at least one saved playlist.");
            if (playlistIds.Length > PluginConfiguration.MaxScenePlaylists)
            {
                return BadRequest(
                    $"Select no more than {PluginConfiguration.MaxScenePlaylists} saved playlists at once.");
            }

            config.ScenePlaylists ??= new List<HueScenePlaylist>();
            var selectedPlaylists = playlistIds
                .Select(id => config.ScenePlaylists.FirstOrDefault(playlist =>
                    playlist != null &&
                    string.Equals(playlist.Id?.Trim(), id, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            var missingIds = playlistIds
                .Where((_, index) => selectedPlaylists[index] == null)
                .ToArray();
            if (missingIds.Length > 0)
            {
                return NotFound(new HueScenePlaylistBulkPreviewResult
                {
                    RequestedCount = playlistIds.Length,
                    MissingIds = missingIds,
                    Message = $"The requested scene playlist(s) were not found: {string.Join(", ", missingIds)}."
                });
            }

            var playlists = selectedPlaylists
                .Cast<HueScenePlaylist>()
                .Select(CloneScenePlaylist)
                .ToArray();
            if (ContainsBlankTargetUserId(request.TargetUserIds))
                return BadRequest("Selected bulk scene playlist target IDs must contain user mapping IDs.");

            var targetUserId = request.TargetUserId?.Trim() ?? string.Empty;
            var targetUserIds = request.TargetUserIds?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var targetRoutes = NormalizeSceneAutomationTargetRoutes(request.TargetRoutes);
            if (TryGetInvalidSceneAutomationTargetRouteError(targetRoutes) is { } targetRouteError)
                return BadRequest(targetRouteError);
            var includeDefaultTarget = request.IncludeDefaultTarget == true;
            var selectedTargetOverride = includeDefaultTarget ||
                (targetUserIds?.Count > 0) ||
                targetRoutes.Count > 0;
            if ((request.TargetAllEnabledMappings == true || !string.IsNullOrWhiteSpace(targetUserId)) &&
                selectedTargetOverride)
            {
                return BadRequest(
                    "A bulk scene playlist preview cannot combine legacy and selected target modes.");
            }
            if (request.TargetAllEnabledMappings == true && !string.IsNullOrWhiteSpace(targetUserId))
            {
                return BadRequest(
                    "A bulk scene playlist preview cannot select all enabled targets and a specific user mapping together.");
            }

            foreach (var playlist in playlists)
            {
                if (!request.TargetAllEnabledMappings.HasValue && string.IsNullOrWhiteSpace(targetUserId) &&
                    !selectedTargetOverride)
                    continue;

                if (request.TargetAllEnabledMappings.HasValue)
                {
                    playlist.TargetAllEnabledMappings = request.TargetAllEnabledMappings.Value;
                    playlist.TargetUserId = request.TargetAllEnabledMappings.Value ? string.Empty : targetUserId;
                    playlist.TargetUserIds = new List<string>();
                    playlist.IncludeDefaultTarget = false;
                }
                else
                {
                    playlist.TargetAllEnabledMappings = false;
                    playlist.TargetUserId = targetUserId;
                    playlist.TargetUserIds = new List<string>();
                    playlist.IncludeDefaultTarget = false;
                }
                if (selectedTargetOverride)
                {
                    playlist.TargetAllEnabledMappings = false;
                    playlist.TargetUserId = string.Empty;
                    playlist.TargetUserIds = targetUserIds ?? new List<string>();
                    playlist.IncludeDefaultTarget = includeDefaultTarget;
                }
            }

            var validationErrors = playlists
                .SelectMany(playlist => PluginConfiguration.ValidateScenePlaylist(
                    playlist,
                    config,
                    $"Scene playlist '{playlist.Name?.Trim() ?? string.Empty}'"))
                .ToList();
            foreach (var playlist in playlists)
            {
                var playlistHasSelectedTargets = playlist.IncludeDefaultTarget ||
                    (playlist.TargetUserIds?.Count ?? 0) > 0;
                var hasSelectedTargets = selectedTargetOverride || playlistHasSelectedTargets;
                var targetSchedule = new HueSceneSchedule
                {
                    Id = "bulk-scene-playlist-preview",
                    Name = playlist.Name?.Trim() ?? string.Empty,
                    TargetUserId = hasSelectedTargets || playlist.TargetAllEnabledMappings
                        ? string.Empty
                        : playlist.TargetUserId?.Trim() ?? string.Empty,
                    TargetUserIds = selectedTargetOverride
                        ? targetUserIds ?? new List<string>()
                        : playlist.TargetUserIds?.ToList() ?? new List<string>(),
                    IncludeDefaultTarget = selectedTargetOverride
                        ? includeDefaultTarget
                        : playlist.IncludeDefaultTarget,
                    TargetAllEnabledMappings = !hasSelectedTargets && playlist.TargetAllEnabledMappings
                };
                if (!HueSceneAutomationService.TryResolveTargets(
                        config,
                        targetSchedule,
                        out _,
                        out var targetError,
                        selectedTargetOverride ? targetRoutes : null))
                {
                    validationErrors.Add($"Scene playlist '{playlist.Name?.Trim() ?? string.Empty}' target: {targetError}");
                }
            }

            if (validationErrors.Count > 0)
            {
                return BadRequest(new HueScenePlaylistBulkPreviewResult
                {
                    RequestedCount = playlistIds.Length,
                    ValidationErrors = validationErrors,
                    Message = "One or more selected scene playlists are invalid; no preview was started."
                });
            }

            if (_streamTester == null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Hue preview service is not available.");
            if (_sceneAutomationService == null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Scene automation service is not available.");
            if (_syncService?.IsSyncing == true)
                return Conflict("Stop active playback before running a bulk Hue scene playlist preview.");

            var results = new List<HueScenePlaylistRunResult>(playlists.Length);
            var canceled = false;
            foreach (var playlist in playlists)
            {
                try
                {
                    var result = await _sceneAutomationService.RunPlaylistPreviewAsync(
                        playlist,
                        cancellationToken,
                        selectedTargetOverride ? targetUserIds ?? new List<string>() : null,
                        selectedTargetOverride && includeDefaultTarget,
                        targetRoutesOverride: selectedTargetOverride ? targetRoutes : null).ConfigureAwait(false);
                    results.Add(result);
                    if (!result.Succeeded && HueSceneAutomationService.IndicatesCancellation(result))
                    {
                        canceled = true;
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Bulk scene playlist preview failed for {0}", playlist.Name);
                    results.Add(new HueScenePlaylistRunResult
                    {
                        PlaylistId = playlist.Id?.Trim() ?? string.Empty,
                        PlaylistName = playlist.Name?.Trim() ?? string.Empty,
                        RepeatCount = Math.Clamp(
                            playlist.RepeatCount,
                            PluginConfiguration.MinScenePlaylistRepeatCount,
                            PluginConfiguration.MaxScenePlaylistRepeatCount),
                        PlaybackOrder = PluginConfiguration.TryNormalizeScenePlaylistOrder(
                            playlist.PlaybackOrder,
                            out var failedPlaybackOrder)
                            ? failedPlaybackOrder
                            : PluginConfiguration.ScenePlaylistOrderSequential,
                        TargetAllEnabledMappings = playlist.TargetAllEnabledMappings,
                        TargetUserIds = selectedTargetOverride
                            ? targetUserIds ?? new List<string>()
                            : playlist.TargetUserIds?.Where(value => !string.IsNullOrWhiteSpace(value))
                                .Select(value => value.Trim())
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToArray() ?? Array.Empty<string>(),
                        TargetRoutes = selectedTargetOverride
                            ? targetRoutes
                            : Array.Empty<HueSceneAutomationTargetRoute>(),
                        IncludeDefaultTarget = selectedTargetOverride
                            ? includeDefaultTarget
                            : playlist.IncludeDefaultTarget,
                        Succeeded = false,
                        Message = "The scene playlist preview failed unexpectedly.",
                        RunAtUtc = DateTime.UtcNow
                    });
                }
            }

            var succeededCount = results.Count(result => result.Succeeded);
            var failedCount = results.Count - succeededCount;
            return Ok(new HueScenePlaylistBulkPreviewResult
            {
                RequestedCount = playlistIds.Length,
                CompletedCount = results.Count,
                SucceededCount = succeededCount,
                FailedCount = failedCount,
                Canceled = canceled,
                Message = canceled
                    ? $"Bulk scene playlist preview canceled after {results.Count} of {playlistIds.Length} playlist(s)."
                    : failedCount == 0
                        ? $"Previewed {results.Count} scene playlist(s) successfully."
                        : $"Previewed {results.Count} scene playlist(s); {failedCount} failed.",
                Results = results
            });
        }

        /// <summary>
        /// Creates a safe independent copy of a saved-scene playlist.
        /// </summary>
        [HttpPost("ScenePlaylists/{name}/Duplicate")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public ActionResult<HueScenePlaylistResult> DuplicateScenePlaylist(string name)
        {
            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
                return NotFound("Plugin configuration not available.");
            if (string.IsNullOrWhiteSpace(name))
                return NotFound("Scene playlist not found.");

            config.ScenePlaylists ??= new List<HueScenePlaylist>();
            var source = config.ScenePlaylists.FirstOrDefault(playlist =>
                playlist != null &&
                string.Equals(playlist.Name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (source == null)
                return NotFound("Scene playlist not found.");
            if (config.ScenePlaylists.Count >= PluginConfiguration.MaxScenePlaylists)
                return Conflict($"No more than {PluginConfiguration.MaxScenePlaylists} scene playlists may be saved.");

            var duplicate = CloneScenePlaylist(source);
            duplicate.Id = Guid.NewGuid().ToString("N");
            duplicate.Name = BuildDuplicateScenePlaylistName(config.ScenePlaylists, source.Name);
            var previousPlaylists = config.ScenePlaylists;
            var candidatePlaylists = previousPlaylists
                .Where(playlist => playlist != null)
                .Select(CloneScenePlaylist)
                .ToList();
            candidatePlaylists.Add(duplicate);
            var validationConfiguration = new PluginConfiguration
            {
                ColorPresets = config.ColorPresets ?? new List<HueColorPreset>(),
                UserMappings = config.UserMappings ?? new List<UserBridgeMapping>(),
                ScenePlaylists = candidatePlaylists
            };
            var validationErrors = validationConfiguration.ValidateScenePlaylists();
            if (validationErrors.Count > 0)
                return BadRequest(new { message = "The scene playlist copy is invalid.", errors = validationErrors });

            config.ScenePlaylists = candidatePlaylists;
            try
            {
                plugin.SaveConfiguration();
            }
            catch (Exception ex)
            {
                config.ScenePlaylists = previousPlaylists;
                _logger?.LogError(ex, "Could not persist duplicate Hue scene playlist {0}", source.Name);
                return StatusCode(StatusCodes.Status500InternalServerError, "The scene playlist copy could not be saved.");
            }

            return Ok(ToScenePlaylistResult(duplicate, config));
        }

        /// <summary>
        /// Creates independent copies of several saved-scene playlists in one atomic
        /// administrator operation. Every source ID is resolved before capacity,
        /// validation, or persistence is attempted, and scheduled-cue references to the
        /// originals remain unchanged.
        /// </summary>
        [HttpPost("ScenePlaylists/BulkDuplicate")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public ActionResult<HueScenePlaylistBulkDuplicateResult> DuplicateScenePlaylistsBulk(
            [FromBody] HueScenePlaylistBulkDuplicateRequest? request)
        {
            if (request == null)
                return BadRequest("A playlist selection is required.");

            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
                return NotFound("Plugin configuration not available.");

            var playlistIds = (request.PlaylistIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (playlistIds.Length == 0)
                return BadRequest("Select at least one saved playlist.");
            if (playlistIds.Length > PluginConfiguration.MaxScenePlaylists)
            {
                return BadRequest(
                    $"Select no more than {PluginConfiguration.MaxScenePlaylists} saved playlists at once.");
            }

            config.ScenePlaylists ??= new List<HueScenePlaylist>();
            var selectedPlaylists = playlistIds
                .Select(id => config.ScenePlaylists.FirstOrDefault(playlist =>
                    playlist != null &&
                    string.Equals(playlist.Id?.Trim(), id, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            var missingIds = playlistIds
                .Where((_, index) => selectedPlaylists[index] == null)
                .ToArray();
            if (missingIds.Length > 0)
            {
                return NotFound(new HueScenePlaylistBulkDuplicateResult
                {
                    RequestedCount = playlistIds.Length,
                    MissingIds = missingIds,
                    Message = $"The requested scene playlist(s) were not found: {string.Join(", ", missingIds)}."
                });
            }

            var availableCapacity = Math.Max(0, PluginConfiguration.MaxScenePlaylists - config.ScenePlaylists.Count);
            if (playlistIds.Length > availableCapacity)
            {
                return Conflict(new HueScenePlaylistBulkDuplicateResult
                {
                    RequestedCount = playlistIds.Length,
                    AvailableCapacity = availableCapacity,
                    Message = $"Only {availableCapacity} playlist slot(s) remain; no copies were created."
                });
            }

            var playlists = selectedPlaylists.Cast<HueScenePlaylist>().ToArray();
            var previousPlaylists = config.ScenePlaylists;
            var candidatePlaylists = previousPlaylists
                .Where(playlist => playlist != null)
                .Select(CloneScenePlaylist)
                .ToList();
            var duplicates = new List<HueScenePlaylist>(playlists.Length);
            foreach (var source in playlists)
            {
                var duplicate = CloneScenePlaylist(source);
                duplicate.Id = Guid.NewGuid().ToString("N");
                duplicate.Name = BuildDuplicateScenePlaylistName(candidatePlaylists, source.Name);
                candidatePlaylists.Add(duplicate);
                duplicates.Add(duplicate);
            }

            var validationConfiguration = new PluginConfiguration
            {
                ColorPresets = config.ColorPresets ?? new List<HueColorPreset>(),
                UserMappings = config.UserMappings ?? new List<UserBridgeMapping>(),
                ScenePlaylists = candidatePlaylists
            };
            var validationErrors = validationConfiguration.ValidateScenePlaylists();
            if (validationErrors.Count > 0)
            {
                return BadRequest(new HueScenePlaylistBulkDuplicateResult
                {
                    RequestedCount = playlistIds.Length,
                    ValidationErrors = validationErrors,
                    Message = "The selected playlist copies are invalid; no playlists were created."
                });
            }

            config.ScenePlaylists = candidatePlaylists;
            try
            {
                plugin.SaveConfiguration();
            }
            catch (Exception ex)
            {
                config.ScenePlaylists = previousPlaylists;
                _logger?.LogError(ex, "Could not persist bulk duplication of Hue scene playlists");
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    "The selected playlist copies could not be saved; no changes were retained.");
            }

            return Ok(new HueScenePlaylistBulkDuplicateResult
            {
                RequestedCount = playlistIds.Length,
                DuplicatedCount = duplicates.Count,
                RemainingCount = config.ScenePlaylists.Count,
                AvailableCapacity = Math.Max(0, PluginConfiguration.MaxScenePlaylists - config.ScenePlaylists.Count),
                Message = $"Created {duplicates.Count} independent playlist copy(ies) atomically.",
                Playlists = duplicates.Select(playlist => ToScenePlaylistResult(playlist, config)).ToArray()
            });
        }

        /// <summary>
        /// Deletes one saved-scene playlist by name. A playlist that is referenced by a
        /// scheduled cue is retained until those cues are deleted or changed, preventing
        /// a successful deletion from leaving an otherwise valid configuration broken.
        /// </summary>
        [HttpDelete("ScenePlaylists/{name}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public ActionResult DeleteScenePlaylist(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return NotFound("Scene playlist not found.");

            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
                return NotFound("Plugin configuration not available.");

            config.ScenePlaylists ??= new List<HueScenePlaylist>();
            var normalizedName = name.Trim();
            var referencedScheduleCount = config.SceneSchedules?.Count(schedule =>
                schedule != null &&
                string.Equals(schedule.PlaylistName?.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase)) ?? 0;
            if (referencedScheduleCount > 0)
            {
                return Conflict(
                    $"The saved playlist is used by {referencedScheduleCount} scheduled cue(s). Delete or update those cues first.");
            }

            var previousPlaylists = config.ScenePlaylists.ToList();
            var removed = config.ScenePlaylists.RemoveAll(playlist =>
                playlist != null &&
                string.Equals(playlist.Name?.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
                return NotFound("Scene playlist not found.");

            try
            {
                plugin.SaveConfiguration();
            }
            catch (Exception ex)
            {
                config.ScenePlaylists = previousPlaylists;
                _logger?.LogError(ex, "Could not persist deletion of Hue scene playlist {0}", name);
                return StatusCode(StatusCodes.Status500InternalServerError, "The scene playlist could not be deleted.");
            }

            return Ok(new { message = "Scene playlist deleted successfully." });
        }

        /// <summary>
        /// Deletes several saved-scene playlists by stable ID in one administrator
        /// operation. Every selected playlist is resolved and checked for scheduled-cue
        /// references before mutation; any missing ID, dependency, or persistence failure
        /// leaves the complete playlist collection unchanged.
        /// </summary>
        [HttpPost("ScenePlaylists/BulkDelete")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public ActionResult<HueScenePlaylistBulkDeleteResult> DeleteScenePlaylistsBulk(
            [FromBody] HueScenePlaylistBulkDeleteRequest? request)
        {
            if (request == null)
                return BadRequest("A playlist selection is required.");

            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
                return NotFound("Plugin configuration not available.");

            var playlistIds = (request.PlaylistIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (playlistIds.Length == 0)
                return BadRequest("Select at least one scene playlist.");
            if (playlistIds.Length > PluginConfiguration.MaxScenePlaylists)
            {
                return BadRequest(
                    $"Select no more than {PluginConfiguration.MaxScenePlaylists} scene playlists at once.");
            }

            config.ScenePlaylists ??= new List<HueScenePlaylist>();
            var selectedPlaylists = playlistIds
                .Select(id => config.ScenePlaylists.FirstOrDefault(playlist =>
                    playlist != null &&
                    string.Equals(playlist.Id?.Trim(), id, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            var missingIds = playlistIds
                .Where((_, index) => selectedPlaylists[index] == null)
                .ToArray();
            if (missingIds.Length > 0)
            {
                return NotFound(new HueScenePlaylistBulkDeleteResult
                {
                    RequestedCount = playlistIds.Length,
                    Message = $"The requested scene playlist(s) were not found: {string.Join(", ", missingIds)}."
                });
            }

            var playlists = selectedPlaylists
                .Where(playlist => playlist != null)
                .Cast<HueScenePlaylist>()
                .ToArray();
            var blockedPlaylists = playlists
                .Select(playlist =>
                {
                    var normalizedName = playlist.Name?.Trim() ?? string.Empty;
                    var schedules = (config.SceneSchedules ?? new List<HueSceneSchedule>())
                        .Where(schedule => schedule != null &&
                            string.Equals(schedule.PlaylistName?.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase))
                        .Select(schedule => new HueScenePlaylistScheduleDependencyResult
                        {
                            Id = schedule.Id?.Trim() ?? string.Empty,
                            Name = schedule.Name?.Trim() ?? string.Empty,
                            Enabled = schedule.Enabled
                        })
                        .OrderBy(schedule => schedule.Name, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(schedule => schedule.Id, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    return schedules.Length == 0
                        ? null
                        : new HueScenePlaylistDependenciesResult
                        {
                            Id = playlist.Id?.Trim() ?? string.Empty,
                            Name = normalizedName,
                            CanDelete = false,
                            ScheduledCueCount = schedules.Length,
                            ScheduledCues = schedules
                        };
                })
                .Where(dependencies => dependencies != null)
                .Cast<HueScenePlaylistDependenciesResult>()
                .ToArray();
            if (blockedPlaylists.Length > 0)
            {
                return Conflict(new HueScenePlaylistBulkDeleteResult
                {
                    RequestedCount = playlists.Length,
                    Message = "One or more selected scene playlists are used by scheduled cues. Delete or update those cues first; no playlists were deleted.",
                    BlockedPlaylists = blockedPlaylists
                });
            }

            var previousPlaylists = config.ScenePlaylists;
            var selectedIds = new HashSet<string>(playlistIds, StringComparer.OrdinalIgnoreCase);
            var candidatePlaylists = previousPlaylists
                .Where(playlist => playlist == null || !selectedIds.Contains(playlist.Id?.Trim() ?? string.Empty))
                .ToList();
            var deletedCount = previousPlaylists.Count - candidatePlaylists.Count;
            var deletedResults = playlists
                .Select(playlist => ToScenePlaylistResult(playlist, config))
                .ToArray();
            config.ScenePlaylists = candidatePlaylists;
            try
            {
                plugin.SaveConfiguration();
            }
            catch (Exception ex)
            {
                config.ScenePlaylists = previousPlaylists;
                _logger?.LogError(ex, "Could not persist bulk deletion of Hue scene playlists");
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    "The selected scene playlists could not be deleted; no changes were retained.");
            }

            return Ok(new HueScenePlaylistBulkDeleteResult
            {
                RequestedCount = playlists.Length,
                DeletedCount = deletedCount,
                RemainingCount = config.ScenePlaylists.Count,
                Message = $"Deleted {deletedCount} scene playlist(s); scheduled-cue references were checked atomically.",
                Playlists = deletedResults
            });
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
                    BaseUtcOffsetMinutes = (int)zone.BaseUtcOffset.TotalMinutes,
                    TimeZoneIanaId = PluginConfiguration.TryGetPortableSceneScheduleTimeZoneId(
                        zone.Id,
                        out var portableTimeZoneId)
                        ? portableTimeZoneId
                        : string.Empty
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
        /// Returns bounded, credential-free overlaps between upcoming enabled cue
        /// execution windows. The calculation uses each cue's configured time zone,
        /// recurrence, exclusions, skip state, finite-run limit, and effective scene or
        /// playlist duration without contacting a bridge.
        /// </summary>
        [HttpGet("SceneSchedules/Conflicts")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<HueSceneScheduleConflictsResult> GetSceneScheduleConflicts(
            [FromQuery(Name = "limit")] int limit = HueSceneAutomationService.DefaultConflictLimit,
            [FromQuery(Name = "days")] int days = HueSceneAutomationService.DefaultConflictHorizonDays,
            [FromQuery(Name = "scheduleId")] string? scheduleId = null)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return NotFound("Plugin configuration not available.");

            var boundedLimit = Math.Clamp(limit, 1, HueSceneAutomationService.MaxConflictLimit);
            var boundedDays = Math.Clamp(days, 1, HueSceneAutomationService.MaxConflictHorizonDays);
            var normalizedScheduleId = string.IsNullOrWhiteSpace(scheduleId) ? null : scheduleId.Trim();
            var serverLocalNow = DateTime.Now;
            return Ok(new HueSceneScheduleConflictsResult
            {
                ServiceAvailable = _sceneAutomationService != null,
                GeneratedAtUtc = DateTime.UtcNow,
                ServerLocalNow = DateTime.SpecifyKind(serverLocalNow, DateTimeKind.Unspecified),
                ServerTimeZoneId = TimeZoneInfo.Local.Id,
                Limit = boundedLimit,
                HorizonDays = boundedDays,
                ScheduleIdFilter = normalizedScheduleId,
                Conflicts = HueSceneAutomationService.GetUpcomingConflicts(
                    config,
                    serverLocalNow,
                    boundedLimit,
                    boundedDays,
                    normalizedScheduleId)
            });
        }

        /// <summary>
        /// Downloads the same bounded, credential-free conflict report as CSV. The
        /// active horizon and cue filter are preserved so spreadsheet audits match the
        /// administrator JSON view exactly.
        /// </summary>
        [HttpGet("SceneSchedules/Conflicts/ExportCsv")]
        [Produces("text/csv")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult ExportSceneScheduleConflictsCsv(
            [FromQuery(Name = "limit")] int limit = HueSceneAutomationService.DefaultConflictLimit,
            [FromQuery(Name = "days")] int days = HueSceneAutomationService.DefaultConflictHorizonDays,
            [FromQuery(Name = "scheduleId")] string? scheduleId = null)
        {
            if (Plugin.Instance?.Configuration == null)
                return NotFound("Plugin configuration not available.");

            var report = ReadActionValue(GetSceneScheduleConflicts(limit, days, scheduleId));
            if (report == null)
                return NotFound("Schedule conflict report is not available.");

            var builder = new StringBuilder();
            AppendCsvRow(
                builder,
                "firstScheduleId",
                "firstScheduleName",
                "firstTargetLabel",
                "firstOccurrenceUtc",
                "firstOccurrenceLocal",
                "firstDurationSeconds",
                "firstPriority",
                "secondScheduleId",
                "secondScheduleName",
                "secondTargetLabel",
                "secondOccurrenceUtc",
                "secondOccurrenceLocal",
                "secondDurationSeconds",
                "secondPriority",
                "overlapSeconds",
                "resolutionHint");
            foreach (var conflict in report.Conflicts)
            {
                AppendCsvRow(
                    builder,
                    conflict.FirstScheduleId,
                    conflict.FirstScheduleName,
                    conflict.FirstTargetLabel,
                    conflict.FirstOccurrenceUtc,
                    conflict.FirstOccurrenceLocal,
                    conflict.FirstDurationSeconds,
                    conflict.FirstPriority,
                    conflict.SecondScheduleId,
                    conflict.SecondScheduleName,
                    conflict.SecondTargetLabel,
                    conflict.SecondOccurrenceUtc,
                    conflict.SecondOccurrenceLocal,
                    conflict.SecondDurationSeconds,
                    conflict.SecondPriority,
                    conflict.OverlapSeconds,
                    conflict.ResolutionHint);
            }

            return CsvFile(builder, "jellyfin-hue-scene-schedule-conflicts.csv");
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
        /// Downloads the same bounded, credential-free upcoming-occurrence report as CSV.
        /// Dates retain their explicit local/UTC columns, exact target-selection JSON, and
        /// the active cue filter and report horizon are applied server-side.
        /// </summary>
        [HttpGet("SceneSchedules/Occurrences/ExportCsv")]
        [Produces("text/csv")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult ExportSceneScheduleOccurrencesCsv(
            [FromQuery(Name = "limit")] int limit = HueSceneAutomationService.MaxUpcomingOccurrencesPerSchedule,
            [FromQuery(Name = "days")] int days = HueSceneAutomationService.DefaultUpcomingHorizonDays,
            [FromQuery(Name = "scheduleId")] string? scheduleId = null)
        {
            if (Plugin.Instance?.Configuration == null)
                return NotFound("Plugin configuration not available.");

            var report = ReadActionValue(GetSceneScheduleOccurrences(limit, days, scheduleId));
            if (report == null)
                return NotFound("Schedule occurrence report is not available.");

            var builder = new StringBuilder();
            AppendCsvRow(
                builder,
                "scheduleId",
                "scheduleName",
                "presetName",
                "playlistName",
                "playlistStepCount",
                "playlistRepeatCount",
                "playlistPlaybackOrder",
                "playlistTotalDurationSeconds",
                "playlistSteps",
                "priority",
                "effect",
                "effectSpeedPercent",
                "brightnessPercent",
                "red",
                "green",
                "blue",
                "recurrence",
                "recurrenceInterval",
                "timeMode",
                "solarOffsetMinutes",
                "solarLatitude",
                "solarLongitude",
                "durationSeconds",
                "transitionSeconds",
                "transitionOutSeconds",
                "transitionCurve",
                "targetLabel",
                "targetAllEnabledMappings",
                "targetUserIds",
                "targetRoutes",
                "includeDefaultTarget",
                "timeZoneId",
                "timeZoneDisplayName",
                "localTime",
                "utcTime");
            foreach (var occurrence in report.Occurrences)
            {
                AppendCsvRow(
                    builder,
                    occurrence.ScheduleId,
                    occurrence.ScheduleName,
                    occurrence.PresetName,
                    occurrence.PlaylistName,
                    occurrence.PlaylistStepCount,
                    occurrence.PlaylistRepeatCount,
                    occurrence.PlaylistPlaybackOrder,
                    occurrence.PlaylistTotalDurationSeconds,
                    JsonSerializer.Serialize(occurrence.PlaylistSteps),
                    occurrence.Priority,
                    occurrence.Effect,
                    occurrence.EffectSpeedPercent,
                    occurrence.BrightnessPercent,
                    occurrence.Red,
                    occurrence.Green,
                    occurrence.Blue,
                    occurrence.Recurrence,
                    occurrence.RecurrenceInterval,
                    occurrence.TimeMode,
                    occurrence.SolarOffsetMinutes,
                    occurrence.SolarLatitude,
                    occurrence.SolarLongitude,
                    occurrence.DurationSeconds,
                    occurrence.TransitionSeconds,
                    occurrence.TransitionOutSeconds,
                    occurrence.TransitionCurve,
                    occurrence.TargetLabel,
                    occurrence.TargetAllEnabledMappings,
                    JsonSerializer.Serialize(occurrence.TargetUserIds ?? Array.Empty<string>()),
                    JsonSerializer.Serialize((occurrence.TargetRoutes ?? Array.Empty<HueSceneScheduleTargetRoute>())
                        .Select(route => new HueSceneAutomationTargetRoute
                        {
                            UserId = route.UserId,
                            DeviceId = route.DeviceId
                        }).ToArray()),
                    occurrence.IncludeDefaultTarget,
                    occurrence.TimeZoneId,
                    occurrence.TimeZoneDisplayName,
                    occurrence.LocalTime,
                    occurrence.UtcTime);
            }

            return CsvFile(builder, "jellyfin-hue-scene-schedule-occurrences.csv");
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
                    var playlist = config.ScenePlaylists?.FirstOrDefault(candidate =>
                        candidate != null &&
                        string.Equals(candidate.Name?.Trim(), schedule.PlaylistName?.Trim(), StringComparison.OrdinalIgnoreCase));
                    var isPlaylist = !string.IsNullOrWhiteSpace(schedule.PlaylistName);
                    var effectiveDuration = isPlaylist
                        ? HueSceneAutomationService.GetPlaylistTotalDurationSeconds(config, playlist)
                        : HueSceneAutomationService.GetEffectiveDurationSeconds(schedule, preset);
                    var effectiveTransition = isPlaylist ? 0 : HueSceneAutomationService.GetEffectiveTransitionSeconds(schedule, preset);
                    var effectiveTransitionOut = isPlaylist ? 0 : HueSceneAutomationService.GetEffectiveTransitionOutSeconds(schedule, preset);
                    var effect = isPlaylist
                        ? PluginConfiguration.SceneScheduleEffectPlaylist
                        : PluginConfiguration.TryNormalizeColorPresetEffect(preset?.Effect, out var normalizedEffect)
                            ? normalizedEffect
                            : PluginConfiguration.ColorPresetEffectSolid;
                    var effectiveRed = !isPlaylist && preset != null
                        ? Math.Clamp(schedule.Red ?? preset.Red, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue)
                        : 0;
                    var effectiveGreen = !isPlaylist && preset != null
                        ? Math.Clamp(schedule.Green ?? preset.Green, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue)
                        : 0;
                    var effectiveBlue = !isPlaylist && preset != null
                        ? Math.Clamp(schedule.Blue ?? preset.Blue, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue)
                        : 0;
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
                                : PluginConfiguration.ClampColorPresetEffectSpeedPercent(preset.EffectSpeedPercent),
                            durationSeconds: effectiveDuration,
                            transitionCurve: isPlaylist
                                ? PluginConfiguration.ColorPresetTransitionCurveLinear
                                : HueSceneAutomationService.GetEffectiveTransitionCurve(preset),
                            brightnessOverride: isPlaylist || preset == null
                                ? null
                                : Math.Clamp(
                                    schedule.BrightnessPercent ?? preset.BrightnessPercent,
                                    PluginConfiguration.MinScenePlaylistStepBrightnessPercent,
                                    PluginConfiguration.MaxScenePlaylistStepBrightnessPercent),
                            redOverride: isPlaylist ? null : effectiveRed,
                            greenOverride: isPlaylist ? null : effectiveGreen,
                            blueOverride: isPlaylist ? null : effectiveBlue)
                        .Select(occurrence => new HueSceneScheduleOccurrenceResult
                        {
                            ScheduleId = occurrence.ScheduleId,
                            ScheduleName = occurrence.ScheduleName,
                            PresetName = occurrence.PresetName,
                            PlaylistName = occurrence.PlaylistName,
                            PlaylistStepCount = playlist?.PresetNames?.Count ?? 0,
                            PlaylistRepeatCount = playlist == null
                                ? PluginConfiguration.DefaultScenePlaylistRepeatCount
                                : Math.Clamp(
                                    playlist.RepeatCount,
                                    PluginConfiguration.MinScenePlaylistRepeatCount,
                                    PluginConfiguration.MaxScenePlaylistRepeatCount),
                            PlaylistPlaybackOrder = playlist == null || !PluginConfiguration.TryNormalizeScenePlaylistOrder(
                                playlist.PlaybackOrder,
                                out var occurrencePlaybackOrder)
                                ? PluginConfiguration.ScenePlaylistOrderSequential
                                : occurrencePlaybackOrder,
                            PlaylistTotalDurationSeconds = isPlaylist ? effectiveDuration : 0,
                            PlaylistSteps = isPlaylist
                                ? HueSceneAutomationService.BuildPlaylistScheduleSteps(config, playlist, occurrence.UtcTime)
                                : Array.Empty<HueScenePlaylistScheduleStep>(),
                            Priority = occurrence.Priority,
                            Effect = effect,
                            EffectSpeedPercent = isPlaylist || preset == null
                                ? PluginConfiguration.DefaultColorPresetEffectSpeedPercent
                                : PluginConfiguration.ClampColorPresetEffectSpeedPercent(preset.EffectSpeedPercent),
                            BrightnessPercent = occurrence.BrightnessPercent,
                            Red = isPlaylist || preset == null
                                ? 0
                                : Math.Clamp(
                                    schedule.Red ?? preset.Red,
                                    PluginConfiguration.MinScenePlaylistStepColorValue,
                                    PluginConfiguration.MaxScenePlaylistStepColorValue),
                            Green = isPlaylist || preset == null
                                ? 0
                                : Math.Clamp(
                                    schedule.Green ?? preset.Green,
                                    PluginConfiguration.MinScenePlaylistStepColorValue,
                                    PluginConfiguration.MaxScenePlaylistStepColorValue),
                            Blue = isPlaylist || preset == null
                                ? 0
                                : Math.Clamp(
                                    schedule.Blue ?? preset.Blue,
                                    PluginConfiguration.MinScenePlaylistStepColorValue,
                                    PluginConfiguration.MaxScenePlaylistStepColorValue),
                            TransitionCurve = isPlaylist
                                ? PluginConfiguration.ColorPresetTransitionCurveLinear
                                : HueSceneAutomationService.GetEffectiveTransitionCurve(preset),
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
                            TargetAllEnabledMappings = occurrence.TargetAllEnabledMappings,
                            TargetUserIds = occurrence.TargetUserIds?.ToArray() ?? Array.Empty<string>(),
                            TargetRoutes = occurrence.TargetRoutes?.Where(route => route != null)
                                .Select(route => new HueSceneScheduleTargetRoute
                                {
                                    UserId = route.UserId?.Trim() ?? string.Empty,
                                    DeviceId = route.DeviceId?.Trim() ?? string.Empty
                                }).ToArray() ?? Array.Empty<HueSceneScheduleTargetRoute>(),
                            IncludeDefaultTarget = occurrence.IncludeDefaultTarget,
                            TimeMode = occurrence.TimeMode,
                            SolarOffsetMinutes = occurrence.SolarOffsetMinutes,
                            SolarLatitude = occurrence.SolarLatitude,
                            SolarLongitude = occurrence.SolarLongitude,
                            TargetLabel = ToSceneScheduleResult(schedule, config).TargetLabel,
                            TimeZoneId = occurrence.TimeZoneId,
                            TimeZoneIanaId = occurrence.TimeZoneIanaId,
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
                    PluginConfiguration.MaxScenePlaylistTotalDurationSeconds);

                AppendIcsLine(builder, "BEGIN", "VEVENT");
                AppendIcsLine(builder, "UID", BuildIcsUid(occurrence.ScheduleId, utcStart));
                AppendIcsLine(builder, "DTSTAMP", FormatIcsUtc(generatedAtUtc));
                AppendIcsLine(builder, "DTSTART", FormatIcsUtc(utcStart));
                AppendIcsLine(builder, "DTEND", FormatIcsUtc(utcStart.AddSeconds(durationSeconds)));
                AppendIcsLine(builder, "SUMMARY", occurrence.ScheduleName);
                AppendIcsLine(
                    builder,
                    "DESCRIPTION",
                    $"{(string.IsNullOrWhiteSpace(occurrence.PlaylistName) ? $"Scene: {occurrence.PresetName}" : $"Playlist: {occurrence.PlaylistName}")}; Target: {occurrence.TargetLabel}; Time zone: {occurrence.TimeZoneDisplayName}; Timing: {FormatSceneScheduleTiming(occurrence)}");
                AppendIcsLine(builder, "X-HUE-TARGET-ALL-ENABLED-MAPPINGS", occurrence.TargetAllEnabledMappings.ToString());
                AppendIcsLine(builder, "X-HUE-TARGET-USER-IDS", JsonSerializer.Serialize(occurrence.TargetUserIds ?? Array.Empty<string>()));
                AppendIcsLine(builder, "X-HUE-TARGET-ROUTES", JsonSerializer.Serialize((occurrence.TargetRoutes ?? Array.Empty<HueSceneScheduleTargetRoute>())
                    .Select(route => new HueSceneAutomationTargetRoute
                    {
                        UserId = route.UserId,
                        DeviceId = route.DeviceId
                    }).ToArray()));
                AppendIcsLine(builder, "X-HUE-INCLUDE-DEFAULT-TARGET", occurrence.IncludeDefaultTarget.ToString());
                AppendIcsLine(builder, "X-HUE-TIMEZONE", occurrence.TimeZoneId);
                AppendIcsLine(builder, "X-HUE-TIME-MODE", occurrence.TimeMode);
                AppendIcsLine(builder, "X-HUE-SOLAR-OFFSET-MINUTES", occurrence.SolarOffsetMinutes.ToString(CultureInfo.InvariantCulture));
                if (occurrence.SolarLatitude.HasValue)
                    AppendIcsLine(builder, "X-HUE-SOLAR-LATITUDE", occurrence.SolarLatitude.Value.ToString("0.####", CultureInfo.InvariantCulture));
                if (occurrence.SolarLongitude.HasValue)
                    AppendIcsLine(builder, "X-HUE-SOLAR-LONGITUDE", occurrence.SolarLongitude.Value.ToString("0.####", CultureInfo.InvariantCulture));
                AppendIcsLine(builder, "X-HUE-RECURRENCE", occurrence.Recurrence);
                AppendIcsLine(builder, "X-HUE-RECURRENCE-INTERVAL", occurrence.RecurrenceInterval.ToString(CultureInfo.InvariantCulture));
                AppendIcsLine(builder, "X-HUE-PRIORITY", occurrence.Priority.ToString(CultureInfo.InvariantCulture));
                AppendIcsLine(builder, "X-HUE-EFFECT", occurrence.Effect);
                AppendIcsLine(builder, "X-HUE-PLAYLIST-STEPS", occurrence.PlaylistStepCount.ToString(CultureInfo.InvariantCulture));
                AppendIcsLine(builder, "X-HUE-PLAYLIST-REPEATS", occurrence.PlaylistRepeatCount.ToString(CultureInfo.InvariantCulture));
                AppendIcsLine(builder, "X-HUE-PLAYLIST-ORDER", occurrence.PlaylistPlaybackOrder);
                AppendIcsLine(builder, "X-HUE-PLAYLIST-STEP-PLAN", JsonSerializer.Serialize(occurrence.PlaylistSteps));
                AppendIcsLine(builder, "X-HUE-EFFECT-SPEED-PERCENT", occurrence.EffectSpeedPercent.ToString(CultureInfo.InvariantCulture));
                AppendIcsLine(builder, "X-HUE-BRIGHTNESS-PERCENT", occurrence.BrightnessPercent.ToString(CultureInfo.InvariantCulture));
                AppendIcsLine(builder, "X-HUE-RED", occurrence.Red.ToString(CultureInfo.InvariantCulture));
                AppendIcsLine(builder, "X-HUE-GREEN", occurrence.Green.ToString(CultureInfo.InvariantCulture));
                AppendIcsLine(builder, "X-HUE-BLUE", occurrence.Blue.ToString(CultureInfo.InvariantCulture));
                AppendIcsLine(builder, "X-HUE-TRANSITION-SECONDS", occurrence.TransitionSeconds.ToString(CultureInfo.InvariantCulture));
                AppendIcsLine(builder, "X-HUE-TRANSITION-OUT-SECONDS", occurrence.TransitionOutSeconds.ToString(CultureInfo.InvariantCulture));
                AppendIcsLine(builder, "X-HUE-TRANSITION-CURVE", occurrence.TransitionCurve);
                AppendIcsLine(builder, "STATUS", "CONFIRMED");
                AppendIcsLine(builder, "TRANSP", "TRANSPARENT");
                AppendIcsLine(builder, "END", "VEVENT");
            }

            AppendIcsLine(builder, "END", "VCALENDAR");
            return builder.ToString();
        }

        private static string FormatSceneScheduleTiming(HueSceneScheduleOccurrenceResult occurrence)
        {
            var offset = occurrence.SolarOffsetMinutes > 0
                ? $"+{occurrence.SolarOffsetMinutes}m"
                : $"{occurrence.SolarOffsetMinutes}m";
            if (string.Equals(occurrence.TimeMode, PluginConfiguration.SceneScheduleTimeModeFixed, StringComparison.OrdinalIgnoreCase))
                return $"Fixed {occurrence.LocalTime.ToString("HH:mm", CultureInfo.InvariantCulture)}";

            var coordinates = occurrence.SolarLatitude.HasValue && occurrence.SolarLongitude.HasValue
                ? $" ({occurrence.SolarLatitude.Value.ToString("0.####", CultureInfo.InvariantCulture)},{occurrence.SolarLongitude.Value.ToString("0.####", CultureInfo.InvariantCulture)})"
                : string.Empty;
            return $"{occurrence.TimeMode} {offset}{coordinates}";
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
            var offset = 0;
            var continuation = false;
            do
            {
                const int maxOctets = 75;
                var prefix = continuation ? " " : string.Empty;
                var availableOctets = maxOctets - Encoding.UTF8.GetByteCount(prefix);
                var chunkLength = GetIcsChunkLength(line, offset, availableOctets);

                builder.Append(prefix);
                builder.Append(line, offset, chunkLength).Append("\r\n");
                offset += chunkLength;
                continuation = true;
            } while (offset < line.Length);
        }

        private static int GetIcsChunkLength(string value, int startIndex, int maxOctets)
        {
            var characterCount = 0;
            var octetCount = 0;
            while (startIndex + characterCount < value.Length)
            {
                var scalarCharacterCount = 1;
                if (char.IsHighSurrogate(value[startIndex + characterCount]) &&
                    startIndex + characterCount + 1 < value.Length &&
                    char.IsLowSurrogate(value[startIndex + characterCount + 1]))
                {
                    scalarCharacterCount = 2;
                }

                var scalarOctetCount = Encoding.UTF8.GetByteCount(
                    value.AsSpan(startIndex + characterCount, scalarCharacterCount));
                if (octetCount + scalarOctetCount > maxOctets)
                    break;

                characterCount += scalarCharacterCount;
                octetCount += scalarOctetCount;
            }

            return characterCount;
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

        private FileContentResult CsvFile(StringBuilder builder, string fileName)
        {
            var content = Encoding.UTF8.GetBytes("\uFEFF" + builder.ToString());
            return File(content, "text/csv; charset=utf-8", fileName);
        }

        private static void AppendCsvRow(StringBuilder builder, params object?[] values)
        {
            for (var index = 0; index < values.Length; index++)
            {
                if (index > 0)
                    builder.Append(',');

                builder.Append(EscapeCsvValue(values[index]));
            }

            builder.Append("\r\n");
        }

        private static string EscapeCsvValue(object? value)
        {
            var text = value switch
            {
                null => string.Empty,
                DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
                _ => value.ToString() ?? string.Empty
            };

            // Keep administrator labels safe when opened in spreadsheet applications:
            // a leading formula marker is treated as text rather than executable data.
            if (value is string &&
                text.Length > 0 &&
                (text[0] == '=' || text[0] == '+' || text[0] == '-' || text[0] == '@' || text[0] == '\t'))
            {
                text = "'" + text;
            }

            return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        /// <summary>
        /// Returns bounded sanitized run history for scheduled scene cues. Bridge
        /// credentials and connection details are never retained or serialized. The
        /// optional outcome filter accepts Succeeded, Failed, Skipped, Recovered, or
        /// Deferred; restored-deferred runs also expose their restart recovery state and
        /// recovered/deferred runs also match their underlying outcome.
        /// </summary>
        [HttpGet("SceneSchedules/History")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<HueSceneScheduleHistoryResult> GetSceneScheduleHistory(
            [FromQuery(Name = "limit")] int limit = HueSceneAutomationService.MaxSceneScheduleHistoryCount,
            [FromQuery(Name = "scheduleId")] string? scheduleId = null,
            [FromQuery(Name = "outcome")] string? outcome = null)
        {
            var boundedLimit = Math.Clamp(limit, 1, HueSceneAutomationService.MaxSceneScheduleHistoryCount);
            var normalizedScheduleId = string.IsNullOrWhiteSpace(scheduleId) ? null : scheduleId.Trim();
            var normalizedOutcome = string.IsNullOrWhiteSpace(outcome) ? null : outcome.Trim();
            return Ok(new HueSceneScheduleHistoryResult
            {
                ServiceAvailable = _sceneAutomationService != null,
                PersistenceEnabled = Plugin.Instance?.Configuration.PersistSceneScheduleHistory ?? false,
                Limit = boundedLimit,
                ScheduleIdFilter = normalizedScheduleId,
                OutcomeFilter = normalizedOutcome,
                GeneratedAtUtc = DateTime.UtcNow,
                Runs = _sceneAutomationService?.GetHistory(boundedLimit, normalizedScheduleId, normalizedOutcome)
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
            [FromQuery(Name = "scheduleId")] string? scheduleId = null,
            [FromQuery(Name = "outcome")] string? outcome = null)
        {
            return GetSceneScheduleHistory(limit, scheduleId, outcome);
        }

        /// <summary>
        /// Downloads the same bounded, credential-free scheduled-cue history as CSV.
        /// Nested target and playlist telemetry is represented by bounded counts and
        /// credential-free target-selection JSON so each run remains one spreadsheet row
        /// without serializing credentials or tokens.
        /// </summary>
        [HttpGet("SceneSchedules/History/ExportCsv")]
        [Produces("text/csv")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult ExportSceneScheduleHistoryCsv(
            [FromQuery(Name = "limit")] int limit = HueSceneAutomationService.MaxSceneScheduleHistoryCount,
            [FromQuery(Name = "scheduleId")] string? scheduleId = null,
            [FromQuery(Name = "outcome")] string? outcome = null)
        {
            if (Plugin.Instance?.Configuration == null)
                return NotFound("Plugin configuration not available.");

            var report = ReadActionValue(GetSceneScheduleHistory(limit, scheduleId, outcome));
            var builder = new StringBuilder();
            AppendCsvRow(
                builder,
                "scheduleId",
                "scheduleName",
                "presetName",
                "playlistName",
                "playlistRepeatCount",
                "playlistPlaybackOrder",
                "effect",
                "effectSpeedPercent",
                "brightnessPercent",
                "red",
                "green",
                "blue",
                "targetLabel",
                "targetAllEnabledMappings",
                "targetUserIds",
                "targetRoutes",
                "includeDefaultTarget",
                "succeeded",
                "skipped",
                "wasCatchUp",
                "wasDeferred",
                "wasDeferredRestored",
                "runAtUtc",
                "runCount",
                "targetResultCount",
                "playlistStepCount",
                "message",
                "cleanupWarning");
            foreach (var run in report?.Runs ?? Array.Empty<HueSceneAutomationRunResult>())
            {
                AppendCsvRow(
                    builder,
                    run.ScheduleId,
                    run.ScheduleName,
                    run.PresetName,
                    run.PlaylistName,
                    run.PlaylistRepeatCount,
                    run.PlaylistPlaybackOrder,
                    run.Effect,
                    run.EffectSpeedPercent,
                    run.BrightnessPercent,
                    run.Red,
                    run.Green,
                    run.Blue,
                    run.TargetLabel,
                    run.TargetAllEnabledMappings,
                    JsonSerializer.Serialize(run.TargetUserIds ?? Array.Empty<string>()),
                    JsonSerializer.Serialize(run.TargetRoutes ?? Array.Empty<HueSceneAutomationTargetRoute>()),
                    run.IncludeDefaultTarget,
                    run.Succeeded,
                    run.Skipped,
                    run.WasCatchUp,
                    run.WasDeferred,
                    run.WasDeferredRestored,
                    run.RunAtUtc,
                    run.RunCount,
                    run.TargetResults?.Count ?? 0,
                    run.PlaylistSteps?.Count ?? 0,
                    run.Message,
                    run.CleanupWarning);
            }

            return CsvFile(builder, "jellyfin-hue-scene-schedule-history.csv");
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
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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
            if (PluginConfiguration.TryNormalizeSceneScheduleTimeMode(schedule.TimeMode, out var normalizedTimeMode))
                schedule.TimeMode = normalizedTimeMode;
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
            {
                schedule.Enabled = false;
                schedule.SkipNextOccurrence = false;
            }

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
                if (!request.Priority.HasValue)
                    schedule.Priority = candidateSchedules[existingIndex].Priority;
                if (request.PlaybackPolicy == null)
                    schedule.PlaybackPolicy = candidateSchedules[existingIndex].PlaybackPolicy;
                if (!request.RedSpecified)
                    schedule.Red = candidateSchedules[existingIndex].Red;
                if (!request.GreenSpecified)
                    schedule.Green = candidateSchedules[existingIndex].Green;
                if (!request.BlueSpecified)
                    schedule.Blue = candidateSchedules[existingIndex].Blue;
                if (!request.BrightnessSpecified)
                    schedule.BrightnessPercent = candidateSchedules[existingIndex].BrightnessPercent;
                if (!request.DurationSpecified)
                    schedule.DurationSeconds = candidateSchedules[existingIndex].DurationSeconds;
                if (!request.TargetAllEnabledMappings.HasValue)
                    schedule.TargetAllEnabledMappings = candidateSchedules[existingIndex].TargetAllEnabledMappings;
                if (request.TargetUserIds == null)
                {
                    schedule.TargetUserIds = request.TargetAllEnabledMappings == true ||
                        !string.IsNullOrWhiteSpace(request.TargetUserId)
                        ? new List<string>()
                        : candidateSchedules[existingIndex].TargetUserIds?.ToList() ?? new List<string>();
                }
                if (request.TargetRoutes == null)
                {
                    schedule.TargetRoutes = request.TargetAllEnabledMappings == true ||
                        !string.IsNullOrWhiteSpace(request.TargetUserId) ||
                        request.TargetUserIds != null
                        ? new List<HueSceneScheduleTargetRoute>()
                        : candidateSchedules[existingIndex].TargetRoutes?.Where(route => route != null)
                            .Select(route => new HueSceneScheduleTargetRoute
                            {
                                UserId = route.UserId?.Trim() ?? string.Empty,
                                DeviceId = route.DeviceId?.Trim() ?? string.Empty
                            }).ToList() ?? new List<HueSceneScheduleTargetRoute>();
                }
                if (!request.IncludeDefaultTarget.HasValue)
                {
                    schedule.IncludeDefaultTarget = request.TargetAllEnabledMappings == true ||
                        !string.IsNullOrWhiteSpace(request.TargetUserId)
                        ? false
                        : candidateSchedules[existingIndex].IncludeDefaultTarget;
                }
                if (!request.SkipNextOccurrence.HasValue)
                    schedule.SkipNextOccurrence = candidateSchedules[existingIndex].SkipNextOccurrence;
                candidateSchedules[existingIndex] = schedule;
            }
            else
                candidateSchedules.Add(schedule);

            if (schedule.MaxRuns > 0 && schedule.RunCount >= schedule.MaxRuns)
            {
                schedule.Enabled = false;
                schedule.SkipNextOccurrence = false;
            }

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

            if (existingIndex >= 0 && _sceneAutomationService != null)
            {
                if (!_sceneAutomationService.TryReplaceScheduleConfiguration(
                        schedule.Id,
                        candidateSchedules,
                        out var blockedByActiveRun,
                        out var message))
                {
                    return blockedByActiveRun
                        ? Conflict(message)
                        : StatusCode(StatusCodes.Status500InternalServerError, message);
                }

                return Ok(ToSceneScheduleResult(schedule, config));
            }

            config.SceneSchedules = candidateSchedules;
            try
            {
                plugin.SaveConfiguration();
            }
            catch (Exception ex)
            {
                config.SceneSchedules = previousSchedules;
                _logger?.LogError(ex, "Could not persist Hue scene schedule {0}", schedule.Name);
                return StatusCode(StatusCodes.Status500InternalServerError, "The scene schedule could not be saved.");
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
            duplicate.SkipNextOccurrence = false;

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
        /// Creates disabled, independently editable copies of several scene cues in one
        /// administrator operation. Every selected ID is resolved before any copy is
        /// created; copies receive fresh IDs, unique bounded names, reset counters, and
        /// cleared Skip Next markers. Capacity, validation, and persistence failures leave
        /// the complete original schedule collection unchanged.
        /// </summary>
        [HttpPost("SceneSchedules/BulkDuplicate")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public ActionResult<HueSceneScheduleBulkDuplicateResult> DuplicateSceneSchedulesBulk(
            [FromBody] HueSceneScheduleBulkDuplicateRequest? request)
        {
            if (request == null)
                return BadRequest("A scheduled-cue selection is required.");

            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
                return NotFound("Plugin configuration not available.");

            var scheduleIds = (request.ScheduleIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (scheduleIds.Length == 0)
                return BadRequest("Select at least one scheduled cue.");
            if (scheduleIds.Length > PluginConfiguration.MaxSceneSchedules)
            {
                return BadRequest(
                    $"Select no more than {PluginConfiguration.MaxSceneSchedules} scheduled cues at once.");
            }

            config.SceneSchedules ??= new List<HueSceneSchedule>();
            var selectedSchedules = scheduleIds
                .Select(id => config.SceneSchedules.FirstOrDefault(schedule =>
                    schedule != null &&
                    string.Equals(schedule.Id?.Trim(), id, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            var missingIds = scheduleIds
                .Where((_, index) => selectedSchedules[index] == null)
                .ToArray();
            if (missingIds.Length > 0)
            {
                return NotFound(new HueSceneScheduleBulkDuplicateResult
                {
                    RequestedCount = scheduleIds.Length,
                    MissingScheduleIds = missingIds,
                    Message = $"The requested scene schedule(s) were not found: {string.Join(", ", missingIds)}."
                });
            }

            var schedules = selectedSchedules
                .Where(schedule => schedule != null)
                .Cast<HueSceneSchedule>()
                .ToArray();
            var availableCapacity = Math.Max(0, PluginConfiguration.MaxSceneSchedules - config.SceneSchedules.Count);
            if (schedules.Length > availableCapacity)
            {
                return Conflict(new HueSceneScheduleBulkDuplicateResult
                {
                    RequestedCount = schedules.Length,
                    AvailableCapacity = availableCapacity,
                    Message = $"Only {availableCapacity} scheduled-cue slot(s) remain; no copies were created."
                });
            }

            var previousSchedules = config.SceneSchedules;
            var candidateSchedules = previousSchedules
                .Where(schedule => schedule != null)
                .Select(CloneSceneSchedule)
                .ToList();
            var duplicates = new List<HueSceneSchedule>(schedules.Length);
            foreach (var source in schedules)
            {
                var duplicate = CloneSceneSchedule(source);
                duplicate.Id = Guid.NewGuid().ToString("N");
                duplicate.Name = BuildDuplicateSceneScheduleName(candidateSchedules, source.Name);
                duplicate.RunCount = 0;
                duplicate.Enabled = false;
                duplicate.SkipNextOccurrence = false;
                candidateSchedules.Add(duplicate);
                duplicates.Add(duplicate);
            }

            config.SceneSchedules = candidateSchedules;
            var validationErrors = config.ValidateSceneSchedules();
            if (validationErrors.Count > 0)
            {
                config.SceneSchedules = previousSchedules;
                return BadRequest(new HueSceneScheduleBulkDuplicateResult
                {
                    RequestedCount = schedules.Length,
                    DuplicatedCount = 0,
                    Message = "The selected scene-cue copies are invalid; no copies were created.",
                    ValidationErrors = validationErrors
                });
            }

            try
            {
                plugin.SaveConfiguration();
            }
            catch (Exception ex)
            {
                config.SceneSchedules = previousSchedules;
                _logger?.LogError(ex, "Could not persist bulk duplicate of Hue scene schedules");
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    "The selected scene-cue copies could not be saved; no copies were retained.");
            }

            return Ok(new HueSceneScheduleBulkDuplicateResult
            {
                RequestedCount = schedules.Length,
                DuplicatedCount = duplicates.Count,
                Message = duplicates.Count == 1
                    ? "Created one disabled scheduled-cue copy with a fresh counter."
                    : $"Created {duplicates.Count} disabled scheduled-cue copies with fresh counters.",
                Schedules = duplicates.Select(schedule => ToSceneScheduleResult(schedule, config)).ToArray()
            });
        }

        /// <summary>
        /// Deletes one scene cue by its stable ID.
        /// </summary>
        [HttpDelete("SceneSchedules/{id}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public ActionResult DeleteSceneSchedule(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return NotFound("Scene schedule not found.");

            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
                return NotFound("Plugin configuration not available.");

            config.SceneSchedules ??= new List<HueSceneSchedule>();
            var previousSchedules = config.SceneSchedules.ToList();
            var candidateSchedules = previousSchedules.ToList();
            var normalizedId = id.Trim();
            var removed = candidateSchedules.RemoveAll(schedule =>
                schedule != null &&
                string.Equals(schedule.Id?.Trim(), normalizedId, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
                return NotFound("Scene schedule not found.");

            if (_sceneAutomationService != null)
            {
                if (!_sceneAutomationService.TryDeleteSchedules(new[] { normalizedId }, out var message))
                    return Conflict(message);

                return Ok(new { message });
            }

            config.SceneSchedules = candidateSchedules;
            try
            {
                plugin.SaveConfiguration();
            }
            catch (Exception ex)
            {
                config.SceneSchedules = previousSchedules;
                _logger?.LogError(ex, "Could not persist deletion of Hue scene schedule {0}", normalizedId);
                return StatusCode(StatusCodes.Status500InternalServerError, "The scene schedule could not be deleted.");
            }

            return Ok(new { message = "Scene schedule deleted successfully." });
        }

        /// <summary>
        /// Runs several saved scene cues immediately through the same serialized,
        /// restorative lifecycle used by an individual administrator Run Now action.
        /// Every selected cue, saved-scene or playlist reference, and target is
        /// preflighted before the first bridge call. Runtime failures are retained per
        /// cue while later cues continue; cancellation stops the remaining sequence.
        /// </summary>
        [HttpPost("SceneSchedules/BulkRun")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<ActionResult<HueSceneScheduleBulkRunResult>> RunSceneSchedulesBulk(
            [FromBody] HueSceneScheduleBulkRunRequest? request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
                return BadRequest("A scheduled-cue selection is required.");

            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
                return NotFound("Plugin configuration not available.");

            var scheduleIds = (request.ScheduleIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (scheduleIds.Length == 0)
                return BadRequest("Select at least one scheduled cue.");
            if (scheduleIds.Length > PluginConfiguration.MaxSceneSchedules)
            {
                return BadRequest(
                    $"Select no more than {PluginConfiguration.MaxSceneSchedules} scheduled cues at once.");
            }

            config.SceneSchedules ??= new List<HueSceneSchedule>();
            var selectedSchedules = scheduleIds
                .Select(id => config.SceneSchedules.FirstOrDefault(schedule =>
                    schedule != null &&
                    string.Equals(schedule.Id?.Trim(), id, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            var missingIds = scheduleIds
                .Where((_, index) => selectedSchedules[index] == null)
                .ToArray();
            if (missingIds.Length > 0)
            {
                return NotFound(new HueSceneScheduleBulkRunResult
                {
                    RequestedCount = scheduleIds.Length,
                    MissingScheduleIds = missingIds,
                    Message = $"The requested scene schedule(s) were not found: {string.Join(", ", missingIds)}."
                });
            }

            var schedules = selectedSchedules.Cast<HueSceneSchedule>().ToArray();
            var validationErrors = new List<string>();
            foreach (var schedule in schedules)
            {
                var scheduleName = schedule.Name?.Trim() ?? schedule.Id?.Trim() ?? string.Empty;
                validationErrors.AddRange(PluginConfiguration.ValidateSceneSchedule(
                    schedule,
                    config,
                    $"Scheduled cue '{scheduleName}'"));

                if (schedule.MaxRuns > 0 && schedule.RunCount >= schedule.MaxRuns)
                {
                    validationErrors.Add(
                        $"Scheduled cue '{scheduleName}' has reached its maximum of {schedule.MaxRuns} executions.");
                }

                var targetSchedule = new HueSceneSchedule
                {
                    Id = schedule.Id?.Trim() ?? string.Empty,
                    Name = schedule.Name?.Trim() ?? string.Empty,
                    TargetUserId = schedule.TargetAllEnabledMappings || schedule.IncludeDefaultTarget ||
                        (schedule.TargetUserIds?.Count ?? 0) > 0
                        ? string.Empty
                        : schedule.TargetUserId?.Trim() ?? string.Empty,
                    TargetUserIds = schedule.TargetUserIds?.ToList() ?? new List<string>(),
                    TargetRoutes = schedule.TargetRoutes?.Where(route => route != null)
                        .Select(route => new HueSceneScheduleTargetRoute
                        {
                            UserId = route.UserId?.Trim() ?? string.Empty,
                            DeviceId = route.DeviceId?.Trim() ?? string.Empty
                        }).ToList() ?? new List<HueSceneScheduleTargetRoute>(),
                    IncludeDefaultTarget = schedule.IncludeDefaultTarget,
                    TargetAllEnabledMappings = schedule.TargetAllEnabledMappings
                };
                if (!HueSceneAutomationService.TryResolveTargets(config, targetSchedule, out _, out var targetError))
                {
                    validationErrors.Add($"Scheduled cue '{scheduleName}' target: {targetError}");
                }

                if (!string.IsNullOrWhiteSpace(schedule.PlaylistName))
                {
                    var playlist = config.ScenePlaylists?.FirstOrDefault(candidate =>
                        candidate != null &&
                        string.Equals(candidate.Name?.Trim(), schedule.PlaylistName.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (playlist == null)
                    {
                        validationErrors.Add(
                            $"Scheduled cue '{scheduleName}' references a saved playlist that does not exist.");
                    }
                    else
                    {
                        var scheduledPlaylist = CloneScenePlaylist(playlist);
                        scheduledPlaylist.TargetUserId = schedule.TargetAllEnabledMappings || schedule.IncludeDefaultTarget ||
                            (schedule.TargetUserIds?.Count ?? 0) > 0
                            ? string.Empty
                            : schedule.TargetUserId?.Trim() ?? string.Empty;
                        scheduledPlaylist.TargetUserIds = schedule.TargetUserIds?.ToList() ?? new List<string>();
                        scheduledPlaylist.IncludeDefaultTarget = schedule.IncludeDefaultTarget;
                        scheduledPlaylist.TargetAllEnabledMappings = schedule.TargetAllEnabledMappings;
                        validationErrors.AddRange(PluginConfiguration.ValidateScenePlaylist(
                            scheduledPlaylist,
                            config,
                            $"Scheduled cue '{scheduleName}' playlist"));
                    }
                }
                else if (!string.IsNullOrWhiteSpace(schedule.PresetName) &&
                         !(config.ColorPresets ?? new List<HueColorPreset>()).Any(candidate =>
                             candidate != null &&
                             string.Equals(candidate.Name?.Trim(), schedule.PresetName.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    validationErrors.Add(
                        $"Scheduled cue '{scheduleName}' references a saved scene that does not exist.");
                }
            }

            if (validationErrors.Count > 0)
            {
                return BadRequest(new HueSceneScheduleBulkRunResult
                {
                    RequestedCount = schedules.Length,
                    ValidationErrors = validationErrors,
                    Message = "One or more selected scheduled cues are invalid or exhausted; no cue was started."
                });
            }

            if (_sceneAutomationService == null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Scene automation service is not available.");

            var results = new List<HueSceneAutomationRunResult>(schedules.Length);
            var canceled = false;
            foreach (var schedule in schedules)
            {
                try
                {
                    var result = await _sceneAutomationService.RunScheduleAsync(
                        schedule.Id?.Trim() ?? string.Empty,
                        cancellationToken).ConfigureAwait(false);
                    results.Add(result);
                    if (!result.Succeeded && result.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase))
                    {
                        canceled = true;
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Bulk scene schedule run failed for {0}", schedule.Name);
                    results.Add(new HueSceneAutomationRunResult
                    {
                        ScheduleId = schedule.Id?.Trim() ?? string.Empty,
                        ScheduleName = schedule.Name?.Trim() ?? string.Empty,
                        PresetName = schedule.PresetName?.Trim() ?? string.Empty,
                        PlaylistName = schedule.PlaylistName?.Trim() ?? string.Empty,
                        TargetAllEnabledMappings = schedule.TargetAllEnabledMappings,
                        Succeeded = false,
                        Message = "The scheduled scene cue failed unexpectedly.",
                        RunAtUtc = DateTime.UtcNow
                    });
                }
            }

            var succeededCount = results.Count(result => result.Succeeded);
            var failedCount = results.Count - succeededCount;
            return Ok(new HueSceneScheduleBulkRunResult
            {
                RequestedCount = schedules.Length,
                CompletedCount = results.Count,
                SucceededCount = succeededCount,
                FailedCount = failedCount,
                Canceled = canceled,
                Message = canceled
                    ? $"Bulk scheduled-cue run canceled after {results.Count} of {schedules.Length} cue(s)."
                    : failedCount == 0
                        ? $"Ran {results.Count} scheduled cue(s) successfully."
                        : $"Ran {results.Count} scheduled cue(s); {failedCount} failed.",
                Results = results
            });
        }

        /// <summary>
        /// Requests cancellation for every active manually started cue in a selected
        /// batch. The bridge cleanup lifecycle remains owned by each running cue.
        /// </summary>
        [HttpPost("SceneSchedules/BulkCancel")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public ActionResult<HueSceneScheduleBulkCancelResult> CancelSceneSchedulesBulk(
            [FromBody] HueSceneScheduleBulkCancelRequest? request)
        {
            if (request == null)
                return BadRequest("A scheduled-cue selection is required.");

            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return NotFound("Plugin configuration not available.");
            if (_sceneAutomationService == null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Scene automation service is not available.");

            var scheduleIds = (request.ScheduleIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (scheduleIds.Length == 0)
                return BadRequest("Select at least one scheduled cue.");
            if (scheduleIds.Length > PluginConfiguration.MaxSceneSchedules)
            {
                return BadRequest(
                    $"Select no more than {PluginConfiguration.MaxSceneSchedules} scheduled cues at once.");
            }

            config.SceneSchedules ??= new List<HueSceneSchedule>();
            var selectedSchedules = scheduleIds
                .Select(id => config.SceneSchedules.FirstOrDefault(schedule =>
                    schedule != null &&
                    string.Equals(schedule.Id?.Trim(), id, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            var missingIds = scheduleIds
                .Where((_, index) => selectedSchedules[index] == null)
                .ToArray();
            if (missingIds.Length > 0)
            {
                return NotFound(new HueSceneScheduleBulkCancelResult
                {
                    RequestedCount = scheduleIds.Length,
                    MissingScheduleIds = missingIds,
                    Message = $"The requested scene schedule(s) were not found: {string.Join(", ", missingIds)}."
                });
            }

            var canceledIds = selectedSchedules
                .Cast<HueSceneSchedule>()
                .Where(schedule => _sceneAutomationService.CancelSchedule(schedule.Id))
                .Select(schedule => schedule.Id?.Trim() ?? string.Empty)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToArray();
            return Ok(new HueSceneScheduleBulkCancelResult
            {
                RequestedCount = scheduleIds.Length,
                CanceledCount = canceledIds.Length,
                CanceledScheduleIds = canceledIds,
                Message = canceledIds.Length == 0
                    ? "No manually started scene run is active for the selected cues."
                    : $"Cancellation requested for {canceledIds.Length} selected scene cue(s); bridge state will be restored before they end."
            });
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
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public ActionResult<HueSceneScheduleResult> ResetSceneScheduleRunCount(string id)
        {
            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
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
                var previousRunCount = schedule.RunCount;
                var previousEnabled = schedule.Enabled;
                var previousSkip = schedule.SkipNextOccurrence;
                schedule.RunCount = 0;
                schedule.Enabled = true;
                schedule.SkipNextOccurrence = false;
                try
                {
                    plugin.SaveConfiguration();
                }
                catch (Exception ex)
                {
                    schedule.RunCount = previousRunCount;
                    schedule.Enabled = previousEnabled;
                    schedule.SkipNextOccurrence = previousSkip;
                    _logger?.LogError(ex, "Could not persist reset for Hue scene schedule {0}", schedule.Name);
                    return StatusCode(
                        StatusCodes.Status500InternalServerError,
                        "The scene schedule counter could not be reset.");
                }
            }

            return Ok(ToSceneScheduleResult(schedule, config));
        }

        /// <summary>
        /// Resets the persisted execution counters for several scheduled cues in one
        /// administrator operation. The selected cues are normalized and resolved before
        /// mutation; active, missing, or persistence-blocked cues leave the full selection
        /// unchanged. Each successful reset also re-enables the cue and clears Skip Next.
        /// </summary>
        [HttpPost("SceneSchedules/BulkResetRunCount")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public ActionResult<HueSceneScheduleBulkResetRunCountResult> ResetSceneSchedulesRunCountBulk(
            [FromBody] HueSceneScheduleBulkResetRunCountRequest? request)
        {
            if (request == null)
                return BadRequest("A scheduled-cue selection is required.");

            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
                return NotFound("Plugin configuration not available.");

            var scheduleIds = (request.ScheduleIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (scheduleIds.Length == 0)
                return BadRequest("Select at least one scheduled cue.");
            if (scheduleIds.Length > PluginConfiguration.MaxSceneSchedules)
            {
                return BadRequest(
                    $"Select no more than {PluginConfiguration.MaxSceneSchedules} scheduled cues at once.");
            }

            config.SceneSchedules ??= new List<HueSceneSchedule>();
            var selectedSchedules = scheduleIds
                .Select(id => config.SceneSchedules.FirstOrDefault(schedule =>
                    schedule != null &&
                    string.Equals(schedule.Id?.Trim(), id, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            var missingIds = scheduleIds
                .Where((_, index) => selectedSchedules[index] == null)
                .ToArray();
            if (missingIds.Length > 0)
            {
                return NotFound(new HueSceneScheduleBulkResetRunCountResult
                {
                    RequestedCount = scheduleIds.Length,
                    MissingScheduleIds = missingIds,
                    Message = $"The requested scene schedule(s) were not found: {string.Join(", ", missingIds)}."
                });
            }

            var schedules = selectedSchedules
                .Where(schedule => schedule != null)
                .Cast<HueSceneSchedule>()
                .ToArray();
            if (_sceneAutomationService != null)
            {
                if (!_sceneAutomationService.TryResetSchedulesRunCount(scheduleIds, out var message))
                {
                    return Conflict(new HueSceneScheduleBulkResetRunCountResult
                    {
                        RequestedCount = schedules.Length,
                        Message = message
                    });
                }
            }
            else
            {
                var previousState = schedules.ToDictionary(
                    schedule => schedule.Id?.Trim() ?? string.Empty,
                    schedule => (schedule.RunCount, schedule.Enabled, schedule.SkipNextOccurrence),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var schedule in schedules)
                {
                    schedule.RunCount = 0;
                    schedule.Enabled = true;
                    schedule.SkipNextOccurrence = false;
                }

                try
                {
                    plugin.SaveConfiguration();
                }
                catch (Exception ex)
                {
                    foreach (var schedule in schedules)
                    {
                        var key = schedule.Id?.Trim() ?? string.Empty;
                        if (previousState.TryGetValue(key, out var previous))
                        {
                            schedule.RunCount = previous.RunCount;
                            schedule.Enabled = previous.Enabled;
                            schedule.SkipNextOccurrence = previous.SkipNextOccurrence;
                        }
                    }

                    _logger?.LogError(ex, "Could not persist bulk reset for Hue scene schedules");
                    return StatusCode(
                        StatusCodes.Status500InternalServerError,
                        new HueSceneScheduleBulkResetRunCountResult
                        {
                            RequestedCount = schedules.Length,
                            Message = "The selected scene schedule counters could not be reset; no changes were retained."
                        });
                }
            }

            return Ok(new HueSceneScheduleBulkResetRunCountResult
            {
                RequestedCount = schedules.Length,
                ResetCount = schedules.Length,
                Message = schedules.Length == 1
                    ? "The scene schedule execution counter was reset and the cue was re-enabled."
                    : $"Reset execution counters and re-enabled {schedules.Length} scheduled cue(s).",
                Schedules = schedules.Select(schedule => ToSceneScheduleResult(schedule, config)).ToArray()
            });
        }

        /// <summary>
        /// Enables or disables one scene cue without changing its timing, target, or
        /// scene definition. Enabling an exhausted finite cue requires ResetRunCount.
        /// </summary>
        [HttpPost("SceneSchedules/{id}/Enabled")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public ActionResult<HueSceneScheduleResult> SetSceneScheduleEnabled(
            string id,
            [FromBody] HueSceneScheduleEnabledRequest? request)
        {
            if (request == null)
                return BadRequest("An enabled value is required.");

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
                if (!_sceneAutomationService.TrySetScheduleEnabled(id, request.Enabled, out var message))
                    return Conflict(message);
            }
            else
            {
                if (request.Enabled && schedule.MaxRuns > 0 && schedule.RunCount >= schedule.MaxRuns)
                {
                    return Conflict(
                        "The scene schedule has reached its execution limit. Reset its run counter before enabling it.");
                }

                var previousEnabled = schedule.Enabled;
                schedule.Enabled = request.Enabled;
                try
                {
                    Plugin.Instance?.SaveConfiguration();
                }
                catch (Exception ex)
                {
                    schedule.Enabled = previousEnabled;
                    _logger?.LogError(ex, "Could not persist enabled state for Hue scene schedule {0}", schedule.Name);
                    return StatusCode(
                        StatusCodes.Status500InternalServerError,
                        "The scene schedule enabled state could not be saved.");
                }
            }

            return Ok(ToSceneScheduleResult(schedule, config));
        }

        /// <summary>
        /// Enables or disables several scene cues in one administrator operation. The
        /// selected IDs are normalized and deduplicated, and the automation service
        /// validates every cue before persisting any change so active or exhausted cues
        /// cannot produce a partial bulk update.
        /// </summary>
        [HttpPost("SceneSchedules/BulkEnabled")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public ActionResult<HueSceneScheduleBulkEnabledResult> SetSceneSchedulesEnabledBulk(
            [FromBody] HueSceneScheduleBulkEnabledRequest? request)
        {
            if (request == null)
                return BadRequest("A cue selection and enabled value are required.");

            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return NotFound("Plugin configuration not available.");

            var scheduleIds = (request.ScheduleIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (scheduleIds.Length == 0)
                return BadRequest("Select at least one scheduled cue.");
            if (scheduleIds.Length > PluginConfiguration.MaxSceneSchedules)
            {
                return BadRequest(
                    $"Select no more than {PluginConfiguration.MaxSceneSchedules} scheduled cues at once.");
            }

            config.SceneSchedules ??= new List<HueSceneSchedule>();
            var selectedSchedules = scheduleIds
                .Select(id => config.SceneSchedules.FirstOrDefault(schedule =>
                    schedule != null &&
                    string.Equals(schedule.Id?.Trim(), id, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            var missingIds = scheduleIds
                .Where((_, index) => selectedSchedules[index] == null)
                .ToArray();
            if (missingIds.Length > 0)
            {
                return NotFound(new HueSceneScheduleBulkEnabledResult
                {
                    Enabled = request.Enabled,
                    RequestedCount = scheduleIds.Length,
                    Message = $"The requested scene schedule(s) were not found: {string.Join(", ", missingIds)}."
                });
            }

            var schedules = selectedSchedules
                .Where(schedule => schedule != null)
                .Cast<HueSceneSchedule>()
                .ToArray();
            if (_sceneAutomationService != null)
            {
                if (!_sceneAutomationService.TrySetSchedulesEnabled(scheduleIds, request.Enabled, out var message))
                {
                    return Conflict(new HueSceneScheduleBulkEnabledResult
                    {
                        Enabled = request.Enabled,
                        RequestedCount = schedules.Length,
                        Message = message
                    });
                }
            }
            else
            {
                if (request.Enabled)
                {
                    var exhausted = schedules
                        .Where(schedule => schedule.MaxRuns > 0 && schedule.RunCount >= schedule.MaxRuns)
                        .Select(schedule => schedule.Name)
                        .ToArray();
                    if (exhausted.Length > 0)
                    {
                        return Conflict(new HueSceneScheduleBulkEnabledResult
                        {
                            Enabled = true,
                            RequestedCount = schedules.Length,
                            Message = $"The selected cue(s) reached their execution limit; reset their run counters first: {string.Join(", ", exhausted)}."
                        });
                    }
                }

                var previousEnabled = schedules.ToDictionary(
                    schedule => schedule.Id?.Trim() ?? string.Empty,
                    schedule => schedule.Enabled,
                    StringComparer.OrdinalIgnoreCase);
                foreach (var schedule in schedules)
                {
                    schedule.Enabled = request.Enabled;
                }

                try
                {
                    Plugin.Instance?.SaveConfiguration();
                }
                catch (Exception ex)
                {
                    foreach (var schedule in schedules)
                    {
                        var key = schedule.Id?.Trim() ?? string.Empty;
                        if (previousEnabled.TryGetValue(key, out var wasEnabled))
                            schedule.Enabled = wasEnabled;
                    }

                    _logger?.LogError(ex, "Could not persist bulk enabled state for Hue scene schedules");
                    return StatusCode(
                        StatusCodes.Status500InternalServerError,
                        "The selected scene schedules could not be saved; no changes were retained.");
                }
            }

            return Ok(new HueSceneScheduleBulkEnabledResult
            {
                Enabled = request.Enabled,
                RequestedCount = schedules.Length,
                UpdatedCount = schedules.Length,
                Message = request.Enabled
                    ? $"Enabled {schedules.Length} scheduled cue(s) without changing their schedules."
                    : $"Disabled {schedules.Length} scheduled cue(s) without changing their schedules.",
                Schedules = schedules.Select(schedule => ToSceneScheduleResult(schedule, config)).ToArray()
            });
        }

        /// <summary>
        /// Marks or clears the next automatic occurrence for several scene cues in one
        /// administrator operation. The automation service validates every cue before
        /// persisting any marker, so a disabled, exhausted, futureless, or active cue
        /// cannot produce a partial update. Manual Run Now remains available.
        /// </summary>
        [HttpPost("SceneSchedules/BulkSkipNext")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public ActionResult<HueSceneScheduleBulkSkipNextResult> SetSceneSchedulesSkipNextBulk(
            [FromBody] HueSceneScheduleBulkSkipNextRequest? request)
        {
            if (request == null)
                return BadRequest("A cue selection and skip value are required.");

            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return NotFound("Plugin configuration not available.");

            var scheduleIds = (request.ScheduleIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (scheduleIds.Length == 0)
                return BadRequest("Select at least one scheduled cue.");
            if (scheduleIds.Length > PluginConfiguration.MaxSceneSchedules)
            {
                return BadRequest(
                    $"Select no more than {PluginConfiguration.MaxSceneSchedules} scheduled cues at once.");
            }

            config.SceneSchedules ??= new List<HueSceneSchedule>();
            var selectedSchedules = scheduleIds
                .Select(id => config.SceneSchedules.FirstOrDefault(schedule =>
                    schedule != null &&
                    string.Equals(schedule.Id?.Trim(), id, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            var missingIds = scheduleIds
                .Where((_, index) => selectedSchedules[index] == null)
                .ToArray();
            if (missingIds.Length > 0)
            {
                return NotFound(new HueSceneScheduleBulkSkipNextResult
                {
                    SkipNextOccurrence = request.SkipNextOccurrence,
                    RequestedCount = scheduleIds.Length,
                    Message = $"The requested scene schedule(s) were not found: {string.Join(", ", missingIds)}."
                });
            }

            var schedules = selectedSchedules
                .Where(schedule => schedule != null)
                .Cast<HueSceneSchedule>()
                .ToArray();
            if (_sceneAutomationService != null)
            {
                if (!_sceneAutomationService.TrySetSchedulesSkipNextOccurrence(
                        scheduleIds,
                        request.SkipNextOccurrence,
                        out var message))
                {
                    return Conflict(new HueSceneScheduleBulkSkipNextResult
                    {
                        SkipNextOccurrence = request.SkipNextOccurrence,
                        RequestedCount = schedules.Length,
                        Message = message
                    });
                }
            }
            else
            {
                // Keep the controller usable in the lightweight test/degraded host path
                // where the hosted automation service is not registered.
                var previousSkip = schedules.ToDictionary(
                    schedule => schedule.Id?.Trim() ?? string.Empty,
                    schedule => schedule.SkipNextOccurrence,
                    StringComparer.OrdinalIgnoreCase);
                var blocked = schedules
                    .Where(schedule => request.SkipNextOccurrence &&
                        !schedule.SkipNextOccurrence &&
                        !schedule.Enabled)
                    .Select(schedule => $"{schedule.Name}: it must be enabled before its next occurrence can be skipped")
                    .ToArray();
                if (request.SkipNextOccurrence)
                {
                    blocked = blocked
                        .Concat(schedules
                            .Where(schedule => request.SkipNextOccurrence &&
                                !schedule.SkipNextOccurrence &&
                                schedule.Enabled &&
                                schedule.MaxRuns > 0 &&
                                schedule.RunCount >= schedule.MaxRuns)
                            .Select(schedule => $"{schedule.Name}: it reached its execution limit; reset its run counter first"))
                        .Concat(schedules
                            .Where(schedule => request.SkipNextOccurrence &&
                                !schedule.SkipNextOccurrence &&
                                schedule.Enabled &&
                                !(schedule.MaxRuns > 0 && schedule.RunCount >= schedule.MaxRuns) &&
                                HueSceneAutomationService.GetNextRunUtc(schedule, DateTime.Now) == null)
                            .Select(schedule => $"{schedule.Name}: it has no upcoming automatic occurrence to skip"))
                        .ToArray();
                }

                if (blocked.Length > 0)
                {
                    return Conflict(new HueSceneScheduleBulkSkipNextResult
                    {
                        SkipNextOccurrence = request.SkipNextOccurrence,
                        RequestedCount = schedules.Length,
                        Message = string.Join("; ", blocked) + "."
                    });
                }

                foreach (var schedule in schedules)
                    schedule.SkipNextOccurrence = request.SkipNextOccurrence;

                try
                {
                    Plugin.Instance?.SaveConfiguration();
                }
                catch (Exception ex)
                {
                    foreach (var schedule in schedules)
                    {
                        var key = schedule.Id?.Trim() ?? string.Empty;
                        if (previousSkip.TryGetValue(key, out var wasSkipped))
                            schedule.SkipNextOccurrence = wasSkipped;
                    }

                    _logger?.LogError(ex, "Could not persist bulk skipped occurrence state for Hue scene schedules");
                    return StatusCode(
                        StatusCodes.Status500InternalServerError,
                        "The selected skipped-occurrence state could not be saved; no changes were retained.");
                }
            }

            return Ok(new HueSceneScheduleBulkSkipNextResult
            {
                SkipNextOccurrence = request.SkipNextOccurrence,
                RequestedCount = schedules.Length,
                UpdatedCount = schedules.Length,
                Message = request.SkipNextOccurrence
                    ? $"Marked the next automatic occurrence for {schedules.Length} scheduled cue(s) to be skipped."
                    : $"Cleared the pending skipped occurrence for {schedules.Length} scheduled cue(s).",
                Schedules = schedules.Select(schedule => ToSceneScheduleResult(schedule, config)).ToArray()
            });
        }

        /// <summary>
        /// Deletes several scene cues in one administrator operation. All IDs are resolved
        /// before mutation, and the automation service refuses the complete request when a
        /// selected cue is active or persistence fails. Retained history remains available.
        /// </summary>
        [HttpPost("SceneSchedules/BulkDelete")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public ActionResult<HueSceneScheduleBulkDeleteResult> DeleteSceneSchedulesBulk(
            [FromBody] HueSceneScheduleBulkDeleteRequest? request)
        {
            if (request == null)
                return BadRequest("A cue selection is required.");

            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return NotFound("Plugin configuration not available.");

            var scheduleIds = (request.ScheduleIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (scheduleIds.Length == 0)
                return BadRequest("Select at least one scheduled cue.");
            if (scheduleIds.Length > PluginConfiguration.MaxSceneSchedules)
            {
                return BadRequest(
                    $"Select no more than {PluginConfiguration.MaxSceneSchedules} scheduled cues at once.");
            }

            config.SceneSchedules ??= new List<HueSceneSchedule>();
            var selectedSchedules = scheduleIds
                .Select(id => config.SceneSchedules.FirstOrDefault(schedule =>
                    schedule != null &&
                    string.Equals(schedule.Id?.Trim(), id, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            var missingIds = scheduleIds
                .Where((_, index) => selectedSchedules[index] == null)
                .ToArray();
            if (missingIds.Length > 0)
            {
                return NotFound(new HueSceneScheduleBulkDeleteResult
                {
                    RequestedCount = scheduleIds.Length,
                    Message = $"The requested scene schedule(s) were not found: {string.Join(", ", missingIds)}."
                });
            }

            var schedules = selectedSchedules
                .Where(schedule => schedule != null)
                .Cast<HueSceneSchedule>()
                .ToArray();
            if (_sceneAutomationService != null)
            {
                if (!_sceneAutomationService.TryDeleteSchedules(scheduleIds, out var message))
                {
                    return Conflict(new HueSceneScheduleBulkDeleteResult
                    {
                        RequestedCount = schedules.Length,
                        Message = message
                    });
                }
            }
            else
            {
                // Keep the controller usable in the lightweight test/degraded host path
                // where the hosted automation service is not registered.
                var previousSchedules = config.SceneSchedules;
                var selectedIds = new HashSet<string>(scheduleIds, StringComparer.OrdinalIgnoreCase);
                config.SceneSchedules = previousSchedules
                    .Where(schedule => schedule == null || !selectedIds.Contains(schedule.Id?.Trim() ?? string.Empty))
                    .ToList();
                try
                {
                    Plugin.Instance?.SaveConfiguration();
                }
                catch (Exception ex)
                {
                    config.SceneSchedules = previousSchedules;
                    _logger?.LogError(ex, "Could not persist bulk deletion of Hue scene schedules");
                    return StatusCode(
                        StatusCodes.Status500InternalServerError,
                        "The selected scene schedules could not be deleted; no changes were retained.");
                }
            }

            return Ok(new HueSceneScheduleBulkDeleteResult
            {
                RequestedCount = schedules.Length,
                DeletedCount = schedules.Length,
                RemainingCount = config.SceneSchedules?.Count ?? 0,
                Message = $"Deleted {schedules.Length} scheduled cue(s); retained cue history was preserved.",
                Schedules = schedules.Select(schedule => ToSceneScheduleResult(schedule, config)).ToArray()
            });
        }

        /// <summary>
        /// Skips the next eligible automatic occurrence of one scene cue without changing
        /// its recurrence definition. Manual Run Now remains available; one-time cues are
        /// disabled after their skipped occurrence.
        /// </summary>
        [HttpPost("SceneSchedules/{id}/SkipNext")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public ActionResult<HueSceneScheduleResult> SkipNextSceneSchedule(string id)
        {
            return SetSceneScheduleSkipNextOccurrence(id, true);
        }

        /// <summary>
        /// Clears a pending skip so the next eligible automatic occurrence runs normally.
        /// </summary>
        [HttpDelete("SceneSchedules/{id}/SkipNext")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public ActionResult<HueSceneScheduleResult> ClearSkippedSceneSchedule(string id)
        {
            return SetSceneScheduleSkipNextOccurrence(id, false);
        }

        private ActionResult<HueSceneScheduleResult> SetSceneScheduleSkipNextOccurrence(string id, bool skip)
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
                if (!_sceneAutomationService.TrySetScheduleSkipNextOccurrence(id, skip, out var message))
                    return Conflict(message);
            }
            else
            {
                if (skip && schedule.SkipNextOccurrence)
                    return Ok(ToSceneScheduleResult(schedule, config));

                if (skip && !schedule.Enabled)
                    return Conflict("The scene schedule must be enabled before its next occurrence can be skipped.");

                if (skip && schedule.MaxRuns > 0 && schedule.RunCount >= schedule.MaxRuns)
                {
                    return Conflict(
                        "The scene schedule has reached its execution limit. Reset its run counter before marking an occurrence to skip.");
                }

                if (skip && HueSceneAutomationService.GetNextRunUtc(schedule, DateTime.Now) == null)
                    return Conflict("The scene schedule has no upcoming automatic occurrence to skip.");

                var previousSkip = schedule.SkipNextOccurrence;
                schedule.SkipNextOccurrence = skip;
                try
                {
                    Plugin.Instance?.SaveConfiguration();
                }
                catch (Exception ex)
                {
                    schedule.SkipNextOccurrence = previousSkip;
                    _logger?.LogError(ex, "Could not persist skipped occurrence state for Hue scene schedule {0}", schedule.Name);
                    return StatusCode(
                        StatusCodes.Status500InternalServerError,
                        "The skipped occurrence state could not be saved.");
                }
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
                ConfiguredPlaybackMediaFilter = config?.PlaybackMediaFilter ?? PluginConfiguration.PlaybackMediaFilterAllVideo,
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
                ActiveDeviceId = runtime?.ActiveDeviceId,
                ActiveDeviceName = runtime?.ActiveDeviceName,
                ActiveDeviceRouteMatched = runtime?.ActiveDeviceRouteMatched,
                ActivePlaybackMediaFilter = runtime?.ActivePlaybackMediaFilter,
                ActiveBridgeIp = runtime?.ActiveBridgeIp,
                ActiveEntertainmentAreaId = runtime?.ActiveEntertainmentAreaId,
                ActiveTargetFps = runtime?.ActiveTargetFps,
                ActiveAudioSensitivityPercent = runtime?.ActiveAudioSensitivityPercent,
                ActiveAudioNoiseGatePercent = runtime?.ActiveAudioNoiseGatePercent,
                ActiveAudioLowFrequencyHz = runtime?.ActiveAudioLowFrequencyHz,
                ActiveAudioMidFrequencyHz = runtime?.ActiveAudioMidFrequencyHz,
                ActiveAudioHighFrequencyHz = runtime?.ActiveAudioHighFrequencyHz,
                ActiveAudioLowGainPercent = runtime?.ActiveAudioLowGainPercent,
                ActiveAudioMidGainPercent = runtime?.ActiveAudioMidGainPercent,
                ActiveAudioHighGainPercent = runtime?.ActiveAudioHighGainPercent,
                ActiveAudioResponseSmoothingPercent = runtime?.ActiveAudioResponseSmoothingPercent,
                ActiveAudioBandSpreadPercent = runtime?.ActiveAudioBandSpreadPercent,
                ActiveAudioBeatPulsePercent = runtime?.ActiveAudioBeatPulsePercent,
                ActiveAudioBeatPulseDecayPercent = runtime?.ActiveAudioBeatPulseDecayPercent,
                ActiveAudioBeatPulseThresholdPercent = runtime?.ActiveAudioBeatPulseThresholdPercent,
                ActiveAudioColorPalette = runtime?.ActiveAudioColorPalette,
                ActiveAudioSpatialMode = runtime?.ActiveAudioSpatialMode,
                ActiveAudioChannelMode = runtime?.ActiveAudioChannelMode,
                ActiveFrameResolution = runtime?.ActiveFrameResolution,
                ActiveVideoScalingMode = runtime?.ActiveVideoScalingMode,
                ActiveVideoDeinterlaceMode = runtime?.ActiveVideoDeinterlaceMode,
                ActiveSamplingBreadthPercent = runtime?.ActiveSamplingBreadthPercent,
                ActiveSamplingMode = runtime?.ActiveSamplingMode,
                ActiveSpatialOrientation = runtime?.ActiveSpatialOrientation,
                ActiveColorSmoothingPercent = runtime?.ActiveColorSmoothingPercent,
                ActiveBrightnessBoost = runtime?.ActiveBrightnessBoost,
                ActiveRedGain = runtime?.ActiveRedGain,
                ActiveGreenGain = runtime?.ActiveGreenGain,
                ActiveBlueGain = runtime?.ActiveBlueGain,
                ActiveColorSaturation = runtime?.ActiveColorSaturation,
                ActiveHueShiftDegrees = runtime?.ActiveHueShiftDegrees,
                ActiveOutputBrightnessPercent = runtime?.ActiveOutputBrightnessPercent,
                ActiveGammaCorrection = runtime?.ActiveGammaCorrection,
                ActiveContrastPercent = runtime?.ActiveContrastPercent,
                ActiveColorTemperatureKelvin = runtime?.ActiveColorTemperatureKelvin,
                ActiveBlackoutThreshold = runtime?.ActiveBlackoutThreshold,
                ActiveBlackoutBehavior = runtime?.ActiveBlackoutBehavior,
                ActiveColorChangeThreshold = runtime?.ActiveColorChangeThreshold,
                ActiveUseGpu = runtime?.ActiveUseGpu,
                ActiveCustomFfmpegFlagsConfigured = runtime?.ActiveCustomFfmpegFlagsConfigured,
                ActiveFfmpegStallTimeoutSeconds = runtime?.ActiveFfmpegStallTimeoutSeconds,
                ActiveNetworkRetryAttempts = runtime?.ActiveNetworkRetryAttempts,
                ActiveChannelIds = runtime?.ActiveChannelIds,
                ActiveRestoreLightState = runtime?.ActiveRestoreLightState,
                ActivePauseBehavior = runtime?.ActivePauseBehavior,
                ActivePauseBrightnessPercent = runtime?.ActivePauseBrightnessPercent,
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
        /// Downloads the bounded, credential-free completed playback-session history as
        /// CSV. Private labels and target addresses follow the existing administrator
        /// JSON export, while bridge credentials and playback tokens remain absent.
        /// </summary>
        [HttpGet("History/ExportCsv")]
        [Produces("text/csv")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult ExportSessionHistoryCsv(
            [FromQuery(Name = "limit")] int limit = HueSyncService.MaxSessionHistoryCount,
            [FromQuery(Name = "outcome")] string? outcome = null)
        {
            var report = BuildSessionHistoryResult(limit, outcome);
            var builder = new StringBuilder();
            AppendCsvRow(
                builder,
                "outcome",
                "item",
                "userId",
                "userName",
                "deviceId",
                "deviceName",
                "deviceRouteMatched",
                "bridgeIp",
                "entertainmentAreaId",
                "startedAtUtc",
                "endedAtUtc",
                "durationSeconds",
                "effectiveFps",
                "framesProcessed",
                "packetsSent",
                "packetsSkippedByThreshold",
                "packetSendFailures",
                "reconnectAttempts",
                "seekRestartCount",
                "lastSeekPositionSeconds",
                "error",
                "cleanupWarning");
            foreach (var session in report.Sessions)
            {
                AppendCsvRow(
                    builder,
                    session.Outcome,
                    session.Item,
                    session.UserId,
                    session.UserName,
                    session.DeviceId,
                    session.DeviceName,
                    session.DeviceRouteMatched,
                    session.BridgeIp,
                    session.EntertainmentAreaId,
                    session.StartedAtUtc,
                    session.EndedAtUtc,
                    session.DurationSeconds,
                    session.EffectiveFps,
                    session.FramesProcessed,
                    session.PacketsSent,
                    session.PacketsSkippedByThreshold,
                    session.PacketSendFailures,
                    session.ReconnectAttempts,
                    session.SeekRestartCount,
                    session.LastSeekPositionSeconds,
                    session.Error,
                    session.CleanupWarning);
            }

            return CsvFile(builder, "jellyfin-hue-session-history.csv");
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
            static bool HasCompleteTarget(
                string? bridgeIp,
                string? appKey,
                string? clientKey,
                string? entertainmentAreaId)
                => !string.IsNullOrWhiteSpace(bridgeIp) &&
                   !string.IsNullOrWhiteSpace(appKey) &&
                   !string.IsNullOrWhiteSpace(clientKey) &&
                   !string.IsNullOrWhiteSpace(entertainmentAreaId);

            var hasDefaultTarget = config != null && HasCompleteTarget(
                config.HueBridgeIp,
                config.HueAppKey,
                config.HueClientKey,
                config.EntertainmentAreaId);
            var hasCustomUserTarget = config?.UserMappings?.Any(mapping =>
                mapping != null &&
                mapping.SyncEnabled &&
                (HasCompleteTarget(
                     mapping.HueBridgeIp,
                     mapping.HueAppKey,
                     mapping.HueClientKey,
                     mapping.EntertainmentAreaId) ||
                 mapping.DeviceTargets?.Any(target =>
                     target != null && HasCompleteTarget(
                         target.HueBridgeIp,
                         target.HueAppKey,
                         target.HueClientKey,
                         target.EntertainmentAreaId)) == true)) == true;
            var playbackActive = _bridgeLifecycleGate.IsPlaybackActive;
            var diagnosticActive = _bridgeLifecycleGate.IsDiagnosticActive;
            var configurationValid = config != null && configurationErrors.Count == 0;
            var serviceAvailable = _syncService != null;
            var audioCaptureRequired = config != null && RequiresAudioCapture(config);

            return Ok(new HueDiagnosticsResult
            {
                PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString(),
                ConfigurationValid = configurationValid,
                ConfigurationErrors = configurationErrors,
                SyncEnabled = config?.SyncEnabled ?? false,
                PlaybackMediaFilter = config?.PlaybackMediaFilter ?? PluginConfiguration.PlaybackMediaFilterAllVideo,
                DefaultBridgeConfigured = hasDefaultTarget,
                EnabledUserMappingCount = config?.UserMappings?.Count(mapping => mapping != null && mapping.SyncEnabled) ?? 0,
                CustomUserTargetConfigured = hasCustomUserTarget,
                ServiceAvailable = serviceAvailable,
                Ffmpeg = environment.Ffmpeg,
                AudioCapture = environment.AudioCapture,
                AudioCaptureRequired = audioCaptureRequired,
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
                    (!audioCaptureRequired || environment.AudioCapture.Available) &&
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
        /// Collects a consolidated, credential-safe administrator support document.
        /// The bundle combines local prerequisites, saved-target validation, runtime
        /// telemetry, playback and scene-cue history, scheduler status, and a
        /// support-specific configuration export. It never contains bridge keys,
        /// playback tokens, or custom FFmpeg flag values; labels and media metadata
        /// may still be private, so administrators should review the file before sharing it.
        /// </summary>
        [HttpGet("Diagnostics/SupportBundle")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<HueSupportBundle>> ExportSupportBundle(
            CancellationToken cancellationToken = default)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return NotFound("Plugin configuration not available.");
            }

            var diagnosticsAction = await GetDiagnostics(cancellationToken).ConfigureAwait(false);
            var diagnostics = ReadActionValue(diagnosticsAction) ?? new HueDiagnosticsResult
            {
                PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString(),
                ConfigurationValid = false,
                ConfigurationErrors = new[] { "Diagnostics did not return a result." },
                CheckedAtUtc = DateTime.UtcNow
            };

            cancellationToken.ThrowIfCancellationRequested();
            var targetDiagnosticsAction = await GetTargetDiagnostics(cancellationToken).ConfigureAwait(false);
            var targetDiagnostics = ReadActionValue(targetDiagnosticsAction) ?? new HueTargetDiagnosticsResult
            {
                CheckedAtUtc = DateTime.UtcNow
            };

            var generatedAtUtc = DateTime.UtcNow;
            var sessionHistory = BuildSessionHistoryResult(HueSyncService.MaxSessionHistoryCount, null);
            var scheduleHistory = new HueSceneScheduleHistoryResult
            {
                ServiceAvailable = _sceneAutomationService != null,
                PersistenceEnabled = config.PersistSceneScheduleHistory,
                Limit = HueSceneAutomationService.MaxSceneScheduleHistoryCount,
                GeneratedAtUtc = generatedAtUtc,
                Runs = _sceneAutomationService?.GetHistory(HueSceneAutomationService.MaxSceneScheduleHistoryCount)
                    ?? Array.Empty<HueSceneAutomationRunResult>()
            };
            var runtime = ReadActionValue(GetStatus()) ?? new HueSyncStatus
            {
                ServiceAvailable = false,
                State = "Unavailable",
                StatusMessage = "Sync service is not available."
            };

            return Ok(new HueSupportBundle
            {
                SchemaVersion = HueSupportBundle.CurrentSchemaVersion,
                GeneratedAtUtc = generatedAtUtc,
                PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? string.Empty,
                Diagnostics = diagnostics,
                TargetDiagnostics = targetDiagnostics,
                Runtime = runtime,
                SessionHistory = sessionHistory,
                SceneAutomation = _sceneAutomationService?.GetStatus() ?? new HueSceneAutomationStatus
                {
                    ServiceAvailable = false,
                    GeneratedAtUtc = generatedAtUtc,
                    ServerLocalNow = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified),
                    ServerTimeZoneId = TimeZoneInfo.Local.Id
                },
                SceneScheduleHistory = scheduleHistory,
                Configuration = HueConfigurationExportDocument.ForSupport(config)
            });
        }

        private static T? ReadActionValue<T>(ActionResult<T> action)
            where T : class
        {
            return action.Value ?? (action.Result as ObjectResult)?.Value as T;
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
                DeviceId = target.DeviceId,
                DeviceName = target.DeviceName,
                SyncEnabled = target.SyncEnabled,
                InheritsDefaultBridge = target.InheritsDefaultBridge,
                BridgeIp = target.BridgeIp,
                EntertainmentAreaId = target.AreaId,
                EntertainmentAreaName = target.AreaName,
                HasAppKey = !string.IsNullOrWhiteSpace(target.AppKey),
                HasClientKey = !string.IsNullOrWhiteSpace(target.ClientKey),
                ChannelProfileValid = true,
                ConfigurationValid = true
            };

            if (!PluginConfiguration.TryParseChannelIds(target.ChannelIds, out var requestedChannelIds))
            {
                return result with
                {
                    ConfigurationValid = false,
                    ChannelProfileValid = false,
                    Status = "The saved channel profile is invalid."
                };
            }

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

            var availableChannelIds = GetValidChannelIds(areaConfiguration.Value);
            var channelCount = availableChannelIds.Count;
            var missingChannelIds = requestedChannelIds
                .Where(channelId => !availableChannelIds.Contains(channelId))
                .OrderBy(channelId => channelId)
                .ToArray();
            var channelProfileValid = missingChannelIds.Length == 0;
            var selectedChannelCount = requestedChannelIds.Count == 0
                ? channelCount
                : requestedChannelIds.Count;
            var hasClientKey = !string.IsNullOrWhiteSpace(target.ClientKey);
            return result with
            {
                ConfigurationValid = hasClientKey && channelCount > 0 && channelProfileValid,
                AreaFound = true,
                EntertainmentAreaName = selectedArea.Name,
                ChannelCount = channelCount,
                SelectedChannelCount = selectedChannelCount,
                ChannelProfileValid = channelProfileValid,
                MissingChannelIds = missingChannelIds.Length == 0
                    ? null
                    : string.Join(", ", missingChannelIds),
                Ready = hasClientKey && channelCount > 0 && channelProfileValid,
                Status = !hasClientKey
                    ? "Bridge and area are reachable, but the Client Key is missing."
                    : channelCount == 0
                        ? "The selected area has no controllable channels."
                        : !channelProfileValid
                            ? $"The saved channel profile references IDs not present in this area: {string.Join(", ", missingChannelIds)}."
                            : "Ready for playback."
            };
        }

        private async Task<HueCurrentLightColorCaptureAttempt> CaptureCurrentColorAsync(
            HueTarget target,
            CancellationToken cancellationToken)
        {
            var targetLabel = BuildCaptureTargetLabel(target);
            if (!HueBridgeCertificateValidation.IsValidBridgeAddress(target.BridgeIp))
                return HueCurrentLightColorCaptureAttempt.Failure(
                    StatusCodes.Status400BadRequest,
                    "The selected target has an invalid bridge address.");

            if (string.IsNullOrWhiteSpace(target.AppKey))
                return HueCurrentLightColorCaptureAttempt.Failure(
                    StatusCodes.Status400BadRequest,
                    "The selected target is missing an App Key.");

            if (string.IsNullOrWhiteSpace(target.AreaId))
                return HueCurrentLightColorCaptureAttempt.Failure(
                    StatusCodes.Status400BadRequest,
                    "The selected target has no entertainment area configured.");

            if (!PluginConfiguration.TryParseChannelIds(target.ChannelIds, out var requestedChannelIds))
                return HueCurrentLightColorCaptureAttempt.Failure(
                    StatusCodes.Status400BadRequest,
                    "The selected target has an invalid channel profile.");

            var areas = await _hueClient.GetEntertainmentAreas(
                target.BridgeIp,
                target.AppKey,
                cancellationToken).ConfigureAwait(false);
            if (areas == null)
            {
                return HueCurrentLightColorCaptureAttempt.Failure(
                    StatusCodes.Status502BadGateway,
                    "Could not contact the selected Hue bridge.");
            }

            var selectedArea = areas.FirstOrDefault(area =>
                string.Equals(area.Id, target.AreaId, StringComparison.OrdinalIgnoreCase));
            if (selectedArea == null)
            {
                return HueCurrentLightColorCaptureAttempt.Failure(
                    StatusCodes.Status400BadRequest,
                    "The selected target's entertainment area was not found.");
            }

            var areaConfiguration = await _hueClient.GetEntertainmentConfiguration(
                target.BridgeIp,
                target.AppKey,
                target.AreaId,
                cancellationToken).ConfigureAwait(false);
            if (areaConfiguration == null)
            {
                return HueCurrentLightColorCaptureAttempt.Failure(
                    StatusCodes.Status502BadGateway,
                    "Could not load the selected entertainment area configuration.");
            }

            var availableChannelIds = GetValidChannelIds(areaConfiguration.Value);
            if (availableChannelIds.Count == 0)
            {
                return HueCurrentLightColorCaptureAttempt.Failure(
                    StatusCodes.Status400BadRequest,
                    "The selected entertainment area has no controllable channels.");
            }

            var missingChannelIds = requestedChannelIds
                .Where(channelId => !availableChannelIds.Contains(channelId))
                .OrderBy(channelId => channelId)
                .ToArray();
            if (missingChannelIds.Length > 0)
            {
                return HueCurrentLightColorCaptureAttempt.Failure(
                    StatusCodes.Status400BadRequest,
                    "The selected target's channel profile references IDs not present in this entertainment area.",
                    new
                    {
                        message = "The selected target's channel profile references IDs not present in this entertainment area.",
                        missingChannelIds = string.Join(", ", missingChannelIds)
                    });
            }

            var capture = await _hueClient.GetLightStatesWithResult(
                target.BridgeIp,
                target.AppKey,
                areaConfiguration.Value,
                requestedChannelIds.Count == 0 ? null : requestedChannelIds,
                cancellationToken).ConfigureAwait(false);
            if (!HueColorMath.TryAverageLightStates(capture.States, out var sample))
            {
                return HueCurrentLightColorCaptureAttempt.Success(new HueCurrentLightColorResult
                {
                    Succeeded = false,
                    Message = "The selected entertainment area did not return any light states.",
                    TargetLabel = targetLabel,
                    TargetUserId = target.UserId,
                    TargetDeviceId = target.DeviceId,
                    TargetDeviceName = target.DeviceName,
                    AttemptedLightCount = capture.AttemptedCount,
                    CapturedLightCount = capture.CapturedCount,
                    SampledLightCount = 0,
                    ChannelProfileCount = requestedChannelIds.Count == 0 ? availableChannelIds.Count : requestedChannelIds.Count
                });
            }

            var hasUsableColor = sample.SampledLightCount > 0 || sample.BrightnessPercent == 0;
            var succeeded = capture.Succeeded && hasUsableColor;
            var message = !capture.Succeeded
                ? $"Captured {capture.CapturedCount} of {capture.AttemptedCount} light state(s); the sample is incomplete."
                : !hasUsableColor
                    ? "The selected lights are on, but the bridge returned no usable color data."
                    : sample.SampledLightCount == 0
                        ? "All selected lights are off; the preview was set to black at 0% brightness."
                        : $"Captured the current color from {sample.SampledLightCount} light(s) in {selectedArea.Name}.";

            return HueCurrentLightColorCaptureAttempt.Success(new HueCurrentLightColorResult
            {
                Succeeded = succeeded,
                Message = message,
                TargetLabel = targetLabel,
                TargetUserId = target.UserId,
                TargetDeviceId = target.DeviceId,
                TargetDeviceName = target.DeviceName,
                Red = sample.Red,
                Green = sample.Green,
                Blue = sample.Blue,
                BrightnessPercent = sample.BrightnessPercent,
                CapturedLightCount = capture.CapturedCount,
                AttemptedLightCount = capture.AttemptedCount,
                SampledLightCount = sample.SampledLightCount,
                ChannelProfileCount = requestedChannelIds.Count == 0 ? availableChannelIds.Count : requestedChannelIds.Count
            });
        }

        private static HueCurrentLightColorResult BuildCaptureFailureResult(
            HueTarget target,
            string? message)
            => new()
            {
                Succeeded = false,
                Message = message ?? "The selected target could not be captured.",
                TargetLabel = BuildCaptureTargetLabel(target),
                TargetUserId = target.UserId,
                TargetDeviceId = target.DeviceId,
                TargetDeviceName = target.DeviceName
            };

        private static HueCurrentLightColorAggregate AggregateCurrentColorSamples(
            IReadOnlyList<HueCurrentLightColorResult> captures)
        {
            if (captures.Count == 0)
                return default;

            var brightness = (int)Math.Round(
                captures.Average(capture => Math.Clamp(capture.BrightnessPercent, 0, 100)),
                MidpointRounding.AwayFromZero);
            var colorSamples = captures
                .Where(capture => capture.SampledLightCount > 0)
                .ToArray();
            var sampledLightCount = captures.Sum(capture => Math.Max(0, capture.SampledLightCount));
            if (colorSamples.Length == 0)
            {
                return new HueCurrentLightColorAggregate(0, 0, 0, brightness, sampledLightCount);
            }

            var weight = colorSamples.Sum(capture => capture.SampledLightCount);
            var red = colorSamples.Sum(capture => (double)capture.Red * capture.SampledLightCount) / weight;
            var green = colorSamples.Sum(capture => (double)capture.Green * capture.SampledLightCount) / weight;
            var blue = colorSamples.Sum(capture => (double)capture.Blue * capture.SampledLightCount) / weight;
            return new HueCurrentLightColorAggregate(
                (int)Math.Round(red, MidpointRounding.AwayFromZero),
                (int)Math.Round(green, MidpointRounding.AwayFromZero),
                (int)Math.Round(blue, MidpointRounding.AwayFromZero),
                brightness,
                sampledLightCount);
        }

        private static HueTarget? ResolveSingleCaptureTarget(
            PluginConfiguration config,
            string targetUserId,
            string targetDeviceId = "")
        {
            var normalizedUserId = PluginConfiguration.NormalizeJellyfinUserId(targetUserId);
            var normalizedDeviceId = targetDeviceId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedUserId))
            {
                return string.IsNullOrWhiteSpace(normalizedDeviceId)
                    ? EnumerateConfiguredTargets(config).FirstOrDefault(candidate => candidate.Scope == "Default")
                    : null;
            }

            return EnumerateConfiguredTargets(config).FirstOrDefault(candidate =>
                PluginConfiguration.AreSameJellyfinUserId(candidate.UserId, normalizedUserId) &&
                (string.IsNullOrWhiteSpace(normalizedDeviceId)
                    ? candidate.Scope == "User"
                    : candidate.Scope == "UserDevice" &&
                      string.Equals(candidate.DeviceId, normalizedDeviceId, StringComparison.Ordinal)));
        }

        private static IReadOnlyList<HueSceneAutomationTargetRoute> NormalizeSceneAutomationTargetRoutes(
            IEnumerable<HueCurrentLightColorTargetRoute>? routes)
            => routes?
                .Select(route => route == null
                    ? new HueSceneAutomationTargetRoute()
                    : new HueSceneAutomationTargetRoute
                    {
                        UserId = PluginConfiguration.NormalizeJellyfinUserId(route.UserId),
                        DeviceId = string.IsNullOrWhiteSpace(route.DeviceId) ? null : route.DeviceId.Trim()
                    })
                .ToArray() ?? Array.Empty<HueSceneAutomationTargetRoute>();

        private static string? TryGetInvalidSceneAutomationTargetRouteError(
            IReadOnlyList<HueSceneAutomationTargetRoute> routes)
            => routes.Any(route => route == null ||
                string.IsNullOrWhiteSpace(route.UserId) ||
                string.IsNullOrWhiteSpace(route.DeviceId))
                ? "Selected scene preview target routes must contain both a user mapping ID and a device ID."
                : null;

        private static bool ContainsBlankTargetUserId(IEnumerable<string>? targetUserIds)
            => targetUserIds?.Any(value => string.IsNullOrWhiteSpace(value)) == true;

        private static bool TryResolveCaptureTargets(
            PluginConfiguration config,
            HueCurrentLightColorBatchRequest? request,
            out IReadOnlyList<HueTarget> targets,
            out string error)
        {
            targets = Array.Empty<HueTarget>();
            error = string.Empty;
            var targetAll = request?.TargetAllEnabledMappings == true;
            var includeDefault = request?.IncludeDefaultTarget == true;
            if (request?.TargetUserIds?.Any(value => string.IsNullOrWhiteSpace(value)) == true)
            {
                error = "Selected current-light capture target IDs must contain user mapping IDs.";
                return false;
            }

            var selectedUserIds = request?.TargetUserIds?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToArray() ?? Array.Empty<string>();
            if (request?.TargetRoutes?.Any(route =>
                    route == null || string.IsNullOrWhiteSpace(route.UserId)) == true)
            {
                error = "Selected current-light capture target routes must contain user mapping IDs.";
                return false;
            }

            var selectedRoutes = request?.TargetRoutes?
                .Select(route => new HueCurrentLightColorTargetRoute
                {
                    UserId = route.UserId.Trim(),
                    DeviceId = string.IsNullOrWhiteSpace(route.DeviceId) ? null : route.DeviceId.Trim()
                })
                .ToArray() ?? Array.Empty<HueCurrentLightColorTargetRoute>();
            if (selectedUserIds.Length + selectedRoutes.Length > PluginConfiguration.MaxSceneScheduleTargetMappings)
            {
                error = $"Current-light capture cannot select more than {PluginConfiguration.MaxSceneScheduleTargetMappings} target routes.";
                return false;
            }

            if (targetAll && (includeDefault || selectedUserIds.Length > 0 || selectedRoutes.Length > 0))
            {
                error = "A broadcast current-light capture cannot also select specific targets.";
                return false;
            }

            var resolved = new List<HueTarget>();
            if (targetAll)
            {
                resolved.AddRange(EnumerateConfiguredTargets(config));
            }
            else if (!includeDefault && selectedUserIds.Length == 0 && selectedRoutes.Length == 0)
            {
                var defaultTarget = ResolveSingleCaptureTarget(config, string.Empty);
                if (defaultTarget == null)
                {
                    error = "The default bridge target is not configured.";
                    return false;
                }

                resolved.Add(defaultTarget);
            }
            else
            {
                if (includeDefault)
                {
                    var defaultTarget = ResolveSingleCaptureTarget(config, string.Empty);
                    if (defaultTarget == null)
                    {
                        error = "The default bridge target is not configured.";
                        return false;
                    }

                    resolved.Add(defaultTarget);
                }

                var seenUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var selectedUserId in selectedUserIds)
                {
                    if (!seenUserIds.Add(selectedUserId))
                    {
                        error = $"Selected current-light capture targets contain user mapping '{selectedUserId}' more than once.";
                        return false;
                    }

                    var target = ResolveSingleCaptureTarget(config, selectedUserId);
                    if (target == null)
                    {
                        error = $"The selected user target '{selectedUserId}' is not configured or enabled.";
                        return false;
                    }

                    resolved.Add(target);
                }

                var seenRoutes = new HashSet<(string UserId, string DeviceId)>(new CaptureRouteKeyComparer());
                foreach (var selectedRoute in selectedRoutes)
                {
                    var routeKey = (selectedRoute.UserId, selectedRoute.DeviceId ?? string.Empty);
                    if (!seenRoutes.Add(routeKey))
                    {
                        error = $"Selected current-light capture targets contain route '{selectedRoute.UserId}/{selectedRoute.DeviceId ?? "user"}' more than once.";
                        return false;
                    }

                    var target = ResolveSingleCaptureTarget(config, selectedRoute.UserId, selectedRoute.DeviceId ?? string.Empty);
                    if (target == null)
                    {
                        error = string.IsNullOrWhiteSpace(selectedRoute.DeviceId)
                            ? $"The selected user target '{selectedRoute.UserId}' is not configured or enabled."
                            : $"The selected device target '{selectedRoute.UserId}/{selectedRoute.DeviceId}' is not configured or enabled.";
                        return false;
                    }

                    resolved.Add(target);
                }
            }

            var deduplicated = new List<HueTarget>(resolved.Count);
            var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in resolved)
            {
                if (seenTargets.Add(BuildCaptureTargetIdentity(target)))
                    deduplicated.Add(target);
            }

            if (deduplicated.Count == 0)
            {
                error = targetAll
                    ? "No enabled bridge targets are configured."
                    : "At least one current-light capture target must be selected.";
                return false;
            }

            targets = deduplicated;
            return true;
        }

        private static string BuildCaptureTargetIdentity(HueTarget target)
        {
            var channelProfile = PluginConfiguration.TryParseChannelIds(target.ChannelIds, out var channelIds)
                ? string.Join(",", channelIds.OrderBy(channelId => channelId))
                : target.ChannelIds.Trim();
            return string.Join(
                "|",
                target.BridgeIp.Trim().TrimEnd('.'),
                target.AreaId.Trim(),
                channelProfile);
        }

        private static bool RequiresAudioCapture(PluginConfiguration config)
        {
            static bool IsAudioScope(string? scope)
                => string.Equals(scope, PluginConfiguration.PlaybackMediaFilterAudio, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(scope, PluginConfiguration.PlaybackMediaFilterAllMedia, StringComparison.OrdinalIgnoreCase);

            return IsAudioScope(config.PlaybackMediaFilter) ||
                config.UserMappings?.Any(mapping =>
                    mapping != null &&
                    mapping.SyncEnabled &&
                    IsAudioScope(mapping.PlaybackMediaFilterOverride)) == true;
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
                    DeviceId: null,
                    DeviceName: null,
                    SyncEnabled: config.SyncEnabled,
                    InheritsDefaultBridge: false,
                    BridgeIp: config.HueBridgeIp?.Trim() ?? string.Empty,
                    AppKey: config.HueAppKey?.Trim() ?? string.Empty,
                    ClientKey: config.HueClientKey?.Trim() ?? string.Empty,
                    AreaId: config.EntertainmentAreaId?.Trim() ?? string.Empty,
                    AreaName: null,
                    ChannelIds: config.ChannelIds?.Trim() ?? string.Empty);
            }

            foreach (var mapping in config.UserMappings ?? new List<UserBridgeMapping>())
            {
                if (mapping == null || !mapping.SyncEnabled)
                    continue;

                var inheritsDefaultBridge = string.IsNullOrWhiteSpace(mapping.HueBridgeIp);
                var deviceTargets = (mapping.DeviceTargets ?? new List<UserDeviceBridgeTarget>())
                    .Where(target => target != null)
                    .ToArray();
                var includeBaseTarget = !inheritsDefaultBridge || hasGlobalTarget || deviceTargets.Length == 0;
                if (includeBaseTarget)
                {
                    yield return new HueTarget(
                        Scope: "User",
                        UserId: mapping.UserId?.Trim(),
                        UserName: string.IsNullOrWhiteSpace(mapping.UserName) ? null : mapping.UserName.Trim(),
                        DeviceId: null,
                        DeviceName: null,
                        SyncEnabled: true,
                        InheritsDefaultBridge: inheritsDefaultBridge,
                        BridgeIp: inheritsDefaultBridge ? config.HueBridgeIp?.Trim() ?? string.Empty : mapping.HueBridgeIp.Trim(),
                        AppKey: inheritsDefaultBridge ? config.HueAppKey?.Trim() ?? string.Empty : mapping.HueAppKey?.Trim() ?? string.Empty,
                        ClientKey: inheritsDefaultBridge ? config.HueClientKey?.Trim() ?? string.Empty : mapping.HueClientKey?.Trim() ?? string.Empty,
                        AreaId: inheritsDefaultBridge ? config.EntertainmentAreaId?.Trim() ?? string.Empty : mapping.EntertainmentAreaId?.Trim() ?? string.Empty,
                        AreaName: string.IsNullOrWhiteSpace(mapping.EntertainmentAreaName) ? null : mapping.EntertainmentAreaName.Trim(),
                        ChannelIds: inheritsDefaultBridge || string.IsNullOrWhiteSpace(mapping.ChannelIdsOverride)
                            ? config.ChannelIds?.Trim() ?? string.Empty
                            : mapping.ChannelIdsOverride.Trim());
                }

                foreach (var deviceTarget in deviceTargets)
                {
                    var inheritedChannelIds = string.IsNullOrWhiteSpace(mapping.ChannelIdsOverride)
                        ? config.ChannelIds?.Trim() ?? string.Empty
                        : mapping.ChannelIdsOverride.Trim();
                    yield return new HueTarget(
                        Scope: "UserDevice",
                        UserId: mapping.UserId?.Trim(),
                        UserName: string.IsNullOrWhiteSpace(mapping.UserName) ? null : mapping.UserName.Trim(),
                        DeviceId: string.IsNullOrWhiteSpace(deviceTarget.DeviceId) ? null : deviceTarget.DeviceId.Trim(),
                        DeviceName: string.IsNullOrWhiteSpace(deviceTarget.DeviceName) ? null : deviceTarget.DeviceName.Trim(),
                        SyncEnabled: true,
                        InheritsDefaultBridge: false,
                        BridgeIp: deviceTarget.HueBridgeIp?.Trim() ?? string.Empty,
                        AppKey: deviceTarget.HueAppKey?.Trim() ?? string.Empty,
                        ClientKey: deviceTarget.HueClientKey?.Trim() ?? string.Empty,
                        AreaId: deviceTarget.EntertainmentAreaId?.Trim() ?? string.Empty,
                        AreaName: string.IsNullOrWhiteSpace(deviceTarget.EntertainmentAreaName) ? null : deviceTarget.EntertainmentAreaName.Trim(),
                        ChannelIds: string.IsNullOrWhiteSpace(deviceTarget.ChannelIdsOverride)
                            ? inheritedChannelIds
                            : deviceTarget.ChannelIdsOverride.Trim());
                }
            }
        }

        private static string BuildCaptureTargetLabel(HueTarget target)
        {
            if (target.Scope == "Default")
                return "Default bridge";

            var userLabel = !string.IsNullOrWhiteSpace(target.UserName)
                ? target.UserName
                : string.IsNullOrWhiteSpace(target.UserId)
                    ? "Selected user target"
                    : target.UserId;

            if (target.Scope == "UserDevice")
            {
                var deviceLabel = !string.IsNullOrWhiteSpace(target.DeviceName)
                    ? target.DeviceName
                    : string.IsNullOrWhiteSpace(target.DeviceId)
                        ? "Device route"
                        : target.DeviceId;
                return userLabel + " / " + deviceLabel;
            }

            return userLabel;
        }

        private sealed class CaptureRouteKeyComparer : IEqualityComparer<(string UserId, string DeviceId)>
        {
            public bool Equals(
                (string UserId, string DeviceId) left,
                (string UserId, string DeviceId) right)
                => PluginConfiguration.AreSameJellyfinUserId(left.UserId, right.UserId) &&
                   string.Equals(left.DeviceId, right.DeviceId, StringComparison.Ordinal);

            public int GetHashCode((string UserId, string DeviceId) value)
                => HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(
                        PluginConfiguration.NormalizeJellyfinUserId(value.UserId)),
                    StringComparer.Ordinal.GetHashCode(value.DeviceId));
        }

        private sealed record HueTarget(
            string Scope,
            string? UserId,
            string? UserName,
            string? DeviceId,
            string? DeviceName,
            bool SyncEnabled,
            bool InheritsDefaultBridge,
            string BridgeIp,
            string AppKey,
            string ClientKey,
            string AreaId,
            string? AreaName,
            string ChannelIds);

        private sealed record HueCurrentLightColorCaptureAttempt(
            HueCurrentLightColorResult? Result,
            int? FailureStatusCode,
            string? FailureMessage,
            object? FailureResponse)
        {
            public static HueCurrentLightColorCaptureAttempt Success(HueCurrentLightColorResult result)
                => new(result, null, null, null);

            public static HueCurrentLightColorCaptureAttempt Failure(
                int statusCode,
                string message,
                object? response = null)
                => new(null, statusCode, message, response);
        }

        private readonly record struct HueCurrentLightColorAggregate(
            int Red,
            int Green,
            int Blue,
            int BrightnessPercent,
            int SampledLightCount);

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
        /// Validates a configuration import without changing the live configuration or
        /// contacting a Hue bridge. The same normalization, dependency, and complete
        /// configuration checks used by the atomic import are applied to an isolated
        /// candidate, so administrators can preflight a migration before confirming it.
        /// </summary>
        [HttpPost("Configuration/ValidateImport")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<HueConfigurationImportValidationResult> ValidateConfigurationImport(
            [FromBody] HueConfigurationImportRequest? request)
        {
            if (request == null || request.Configuration == null)
                return BadRequest("A configuration export document is required.");

            if (request.SchemaVersion != HueConfigurationExportDocument.CurrentSchemaVersion)
            {
                return BadRequest($"Unsupported configuration schema version {request.SchemaVersion}. Expected {HueConfigurationExportDocument.CurrentSchemaVersion}.");
            }

            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return NotFound("Plugin configuration not available.");

            var plan = BuildConfigurationImportPlan(config, request);
            var activePlayback = _syncService?.HasActivePlaybackSessions == true ||
                _bridgeLifecycleGate.IsPlaybackActive;
            var activeDiagnostic = _bridgeLifecycleGate.IsDiagnosticActive;
            var activeConfigurationMutation = _bridgeLifecycleGate.IsConfigurationMutationActive;
            var activeScheduledCue = _sceneAutomationService?.HasActiveScheduleRuns == true;
            var activeScheduleEvaluation = _sceneAutomationService?.HasActiveScheduleEvaluation == true;
            var activeScheduleLifecycle = _sceneAutomationService?.HasActiveScheduleLifecycle == true;
            var valid = plan.ValidationErrors.Count == 0;
            return Ok(new HueConfigurationImportValidationResult
            {
                Valid = valid,
                CanImport = valid && !activePlayback && !activeDiagnostic &&
                    !activeConfigurationMutation && !activeScheduledCue &&
                    !activeScheduleEvaluation && !activeScheduleLifecycle,
                ActivePlayback = activePlayback,
                ActiveDiagnostic = activeDiagnostic,
                ActiveConfigurationMutation = activeConfigurationMutation,
                ActiveScheduledCue = activeScheduledCue,
                ActiveScheduleEvaluation = activeScheduleEvaluation,
                ActiveScheduleLifecycle = activeScheduleLifecycle,
                SchemaVersion = request.SchemaVersion,
                ValidationErrors = plan.ValidationErrors,
                MappingsImported = plan.ImportedMappingCount,
                ColorPresetsImported = plan.ImportedPresetCount,
                ScenePlaylistsImported = plan.ImportedPlaylistCount,
                SceneSchedulesImported = plan.ImportedScheduleCount,
                TotalMappings = plan.CandidateMappings.Count,
                TotalColorPresets = plan.CandidatePresets.Count,
                TotalScenePlaylists = plan.CandidatePlaylists.Count,
                TotalSceneSchedules = plan.CandidateSchedules.Count,
                GlobalAppKeyPreserved = plan.GlobalAppKeyPreserved,
                GlobalClientKeyPreserved = plan.GlobalClientKeyPreserved,
                MappingCredentialPairsPreserved = plan.MappingCredentialPairsPreserved,
                Diff = plan.Diff,
                Message = BuildConfigurationImportValidationMessage(
                    valid,
                    activePlayback,
                    activeDiagnostic,
                    activeConfigurationMutation,
                    activeScheduledCue,
                    activeScheduleEvaluation,
                    activeScheduleLifecycle)
            });
        }

        private static string BuildConfigurationImportValidationMessage(
            bool valid,
            bool activePlayback,
            bool activeDiagnostic,
            bool activeConfigurationMutation,
            bool activeScheduledCue,
            bool activeScheduleEvaluation,
            bool activeScheduleLifecycle)
        {
            if (!valid)
                return "Configuration import is invalid. No changes were applied.";

            var blockers = new List<string>();
            if (activePlayback)
                blockers.Add("active Hue playback");
            if (activeDiagnostic)
                blockers.Add("an administrator diagnostic");
            if (activeConfigurationMutation)
                blockers.Add("another configuration import");
            if (activeScheduledCue)
                blockers.Add("active scheduled scene cues");
            if (activeScheduleEvaluation)
                blockers.Add("scheduled scene evaluation");
            else if (activeScheduleLifecycle && !activeScheduledCue)
                blockers.Add("scheduled scene lifecycle");

            return blockers.Count == 0
                ? "Configuration is valid and ready to import."
                : $"Configuration is valid, but {string.Join(", ", blockers)} must finish before import.";
        }

        /// <summary>
        /// Imports global settings, per-user profiles, color scenes, scene playlists, and
        /// scheduled scene cues atomically. Blank
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

            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
                return NotFound("Plugin configuration not available.");

            IDisposable? importLease = null;
            if (_sceneAutomationService != null)
            {
                if (!_sceneAutomationService.TryAcquireConfigurationMutation(
                        out importLease,
                        out var activeScheduleMessage))
                {
                    return Conflict(activeScheduleMessage);
                }
            }
            else
            {
                importLease = _bridgeLifecycleGate.TryEnterConfigurationMutation();
                if (importLease == null)
                {
                    return Conflict("Configuration import cannot proceed while Hue playback or an administrator diagnostic is active.");
                }
            }

            try
            {
                // Keep the existing runtime-state check for defensive compatibility with
                // test hosts or integrations that expose a playback service without using
                // the shared lifecycle gate. The composite lease above closes the normal
                // check-then-start race for the real hosted services.
                if (_syncService?.HasActivePlaybackSessions == true)
                {
                    return Conflict("Stop all active Hue playback sessions before importing configuration.");
                }

                var plan = BuildConfigurationImportPlan(config, request);
                if (plan.ValidationErrors.Count > 0)
                {
                    return BadRequest(new { message = "Configuration import is invalid.", errors = plan.ValidationErrors });
                }

                return ApplyConfigurationImport(plugin, config, request, plan);
            }
            finally
            {
                importLease?.Dispose();
            }
        }

        private ActionResult<HueConfigurationImportResult> ApplyConfigurationImport(
            Plugin plugin,
            PluginConfiguration config,
            HueConfigurationImportRequest request,
            HueConfigurationImportPlan plan)
        {
            var previousSettings = HuePluginConfigurationSettings.From(config);
            var previousAppKey = config.HueAppKey;
            var previousClientKey = config.HueClientKey;
            var previousMappings = config.UserMappings ?? new List<UserBridgeMapping>();
            var previousPresets = config.ColorPresets ?? new List<HueColorPreset>();
            var previousPlaylists = config.ScenePlaylists ?? new List<HueScenePlaylist>();
            var previousSchedules = config.SceneSchedules ?? new List<HueSceneSchedule>();
            var previousPersistedSessionHistory = (config.PersistedSessionHistory ?? new List<HueSessionHistoryEntry>()).ToList();
            var previousPersistedSceneScheduleHistory = (config.PersistedSceneScheduleHistory ?? new List<HueSceneScheduleHistoryEntry>()).ToList();

            request.Configuration!.ApplyTo(config);
            config.UserMappings = plan.CandidateMappings;
            config.ColorPresets = plan.CandidatePresets;
            config.ScenePlaylists = plan.CandidatePlaylists;
            config.SceneSchedules = plan.CandidateSchedules;

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
                config.ScenePlaylists = previousPlaylists;
                config.SceneSchedules = previousSchedules;
                config.PersistedSessionHistory = previousPersistedSessionHistory;
                config.PersistedSceneScheduleHistory = previousPersistedSceneScheduleHistory;
                // Keep the response credential-free while retaining the exception in the
                // server log for the administrator's normal Jellyfin diagnostics.
                _logger?.LogError(ex, "Could not persist imported Hue configuration");
                return StatusCode(StatusCodes.Status500InternalServerError, "Configuration could not be saved.");
            }

            return Ok(new HueConfigurationImportResult
            {
                MappingsImported = plan.ImportedMappingCount,
                ColorPresetsImported = plan.ImportedPresetCount,
                ScenePlaylistsImported = plan.ImportedPlaylistCount,
                SceneSchedulesImported = plan.ImportedScheduleCount,
                TotalMappings = plan.CandidateMappings.Count,
                TotalColorPresets = plan.CandidatePresets.Count,
                TotalScenePlaylists = plan.CandidatePlaylists.Count,
                TotalSceneSchedules = plan.CandidateSchedules.Count,
                GlobalAppKeyPreserved = plan.GlobalAppKeyPreserved,
                GlobalClientKeyPreserved = plan.GlobalClientKeyPreserved,
                MappingCredentialPairsPreserved = plan.MappingCredentialPairsPreserved,
                Message = "Configuration imported. Stored credentials were preserved when the imported document omitted them."
            });
        }

        private static HueConfigurationImportPlan BuildConfigurationImportPlan(
            PluginConfiguration config,
            HueConfigurationImportRequest request)
        {
            var existingMappings = (config.UserMappings ?? new List<UserBridgeMapping>())
                .Where(mapping => mapping != null)
                .ToList();
            var existingPresets = (config.ColorPresets ?? new List<HueColorPreset>())
                .Where(preset => preset != null)
                .ToList();
            var existingPlaylists = (config.ScenePlaylists ?? new List<HueScenePlaylist>())
                .Where(playlist => playlist != null)
                .Select(CloneScenePlaylist)
                .ToList();
            var existingSchedules = (config.SceneSchedules ?? new List<HueSceneSchedule>())
                .Where(schedule => schedule != null)
                .Select(CloneSceneSchedule)
                .ToList();
            var importedMappings = request.UserMappings ?? new List<UserBridgeMappingImport>();
            var importedPresets = request.ColorPresets ?? new List<HueColorPresetRequest>();
            var importedPlaylists = request.ScenePlaylists ?? new List<HueScenePlaylistRequest>();
            var importedSchedules = request.SceneSchedules ?? new List<HueSceneScheduleRequest>();
            var validationErrors = new List<string>();
            validationErrors.AddRange(ValidateGlobalCredentialTransition(
                config,
                request.Configuration!,
                "Configuration import"));
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

                var sourceUserId = source.UserId?.Trim() ?? string.Empty;
                var hasValidUserId = Guid.TryParse(sourceUserId, out var parsedUserId);
                var normalizedUserId = hasValidUserId
                    ? parsedUserId.ToString("D")
                    : string.Empty;
                var existing = hasValidUserId
                    ? existingMappings.FirstOrDefault(candidate =>
                        PluginConfiguration.AreSameJellyfinUserId(candidate.UserId, normalizedUserId))
                    : null;
                var imported = ToImportedMapping(source, existing, out var preservedCredentialPair);
                if (hasValidUserId)
                {
                    // Jellyfin's public user APIs use canonical D-format IDs. Normalize
                    // every accepted import before merge/diff/validation so brace/N-format
                    // exports match an existing mapping and remain runtime-addressable.
                    imported.UserId = normalizedUserId;
                }
                mappingCredentialPairsPreserved += preservedCredentialPair ? 1 : 0;
                importedMappingValues.Add(imported);

                var label = string.IsNullOrWhiteSpace(imported.UserName)
                    ? $"User mapping {index + 1}"
                    : $"User mapping for '{imported.UserName}'";
                if (!hasValidUserId)
                    validationErrors.Add($"{label} user ID must be a valid Jellyfin user ID.");
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

            var candidatePlaylists = request.ReplaceScenePlaylists
                ? new List<HueScenePlaylist>()
                : existingPlaylists.Select(CloneScenePlaylist).ToList();
            var playlistRenameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var playlistRequest in importedPlaylists)
            {
                var playlist = playlistRequest?.ToConfigurationPlaylist() ?? new HueScenePlaylist();
                if (string.IsNullOrWhiteSpace(playlist.Id))
                    playlist.Id = Guid.NewGuid().ToString("N");

                var existingPlaylist = existingPlaylists.FirstOrDefault(existing =>
                    string.Equals(existing.Id?.Trim(), playlist.Id.Trim(), StringComparison.OrdinalIgnoreCase));
                if (playlistRequest?.StepDurationSeconds == null && existingPlaylist != null)
                {
                    playlist.StepDurationSeconds = existingPlaylist.StepDurationSeconds?.ToList()
                        ?? new List<int>();
                }
                if (playlistRequest?.StepRed == null && existingPlaylist != null)
                {
                    playlist.StepRed = existingPlaylist.StepRed?.ToList()
                        ?? new List<int?>();
                }
                if (playlistRequest?.StepGreen == null && existingPlaylist != null)
                {
                    playlist.StepGreen = existingPlaylist.StepGreen?.ToList()
                        ?? new List<int?>();
                }
                if (playlistRequest?.StepBlue == null && existingPlaylist != null)
                {
                    playlist.StepBlue = existingPlaylist.StepBlue?.ToList()
                        ?? new List<int?>();
                }
                if (playlistRequest?.StepBrightnessPercent == null && existingPlaylist != null)
                {
                    playlist.StepBrightnessPercent = existingPlaylist.StepBrightnessPercent?.ToList()
                        ?? new List<int?>();
                }
                if (playlistRequest?.StepEffects == null && existingPlaylist != null)
                {
                    playlist.StepEffects = existingPlaylist.StepEffects?.ToList()
                        ?? new List<string?>();
                }
                if (playlistRequest?.StepEffectSpeedPercent == null && existingPlaylist != null)
                {
                    playlist.StepEffectSpeedPercent = existingPlaylist.StepEffectSpeedPercent?.ToList()
                        ?? new List<int?>();
                }
                if (playlistRequest?.StepTransitionSeconds == null && existingPlaylist != null)
                {
                    playlist.StepTransitionSeconds = existingPlaylist.StepTransitionSeconds?.ToList()
                        ?? new List<int?>();
                }
                if (playlistRequest?.StepTransitionOutSeconds == null && existingPlaylist != null)
                {
                    playlist.StepTransitionOutSeconds = existingPlaylist.StepTransitionOutSeconds?.ToList()
                        ?? new List<int?>();
                }
                if (playlistRequest?.StepTransitionCurves == null && existingPlaylist != null)
                {
                    playlist.StepTransitionCurves = existingPlaylist.StepTransitionCurves?.ToList()
                        ?? new List<string?>();
                }
                var existingPlaylistName = existingPlaylist?.Name?.Trim() ?? string.Empty;
                var importedPlaylistName = playlist.Name?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(existingPlaylistName) &&
                    !string.IsNullOrWhiteSpace(importedPlaylistName) &&
                    !string.Equals(existingPlaylistName, importedPlaylistName, StringComparison.Ordinal))
                {
                    playlistRenameMap[existingPlaylistName] = importedPlaylistName;
                }

                var existingIndex = candidatePlaylists.FindIndex(existing =>
                    string.Equals(existing.Id?.Trim(), playlist.Id.Trim(), StringComparison.OrdinalIgnoreCase));
                if (existingIndex >= 0)
                    candidatePlaylists[existingIndex] = playlist;
                else
                    candidatePlaylists.Add(playlist);
            }

            var playlistValidationConfiguration = new PluginConfiguration
            {
                ColorPresets = candidatePresets,
                UserMappings = candidateMappings,
                ScenePlaylists = candidatePlaylists
            };
            validationErrors.AddRange(playlistValidationConfiguration.ValidateScenePlaylists());

            var candidateSchedules = request.ReplaceSceneSchedules
                ? new List<HueSceneSchedule>()
                : existingSchedules.Select(CloneSceneSchedule).ToList();
            foreach (var schedule in candidateSchedules)
            {
                var existingPlaylistName = schedule.PlaylistName?.Trim() ?? string.Empty;
                if (playlistRenameMap.TryGetValue(existingPlaylistName, out var importedPlaylistName))
                    schedule.PlaylistName = importedPlaylistName;
            }
            foreach (var scheduleRequest in importedSchedules)
            {
                var schedule = scheduleRequest?.ToConfigurationSchedule() ?? new HueSceneSchedule();
                var importedSchedulePlaylistName = schedule.PlaylistName?.Trim() ?? string.Empty;
                if (playlistRenameMap.TryGetValue(importedSchedulePlaylistName, out var migratedPlaylistName))
                    schedule.PlaylistName = migratedPlaylistName;
                if (string.IsNullOrWhiteSpace(schedule.Id))
                    schedule.Id = Guid.NewGuid().ToString("N");
                if (PluginConfiguration.TryNormalizeSceneScheduleTime(schedule.TimeOfDay, out var normalizedTime))
                    schedule.TimeOfDay = normalizedTime;
                if (PluginConfiguration.TryNormalizeSceneScheduleTimeMode(schedule.TimeMode, out var normalizedTimeMode))
                    schedule.TimeMode = normalizedTimeMode;
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
                if (!string.IsNullOrWhiteSpace(schedule.TimeZoneId))
                {
                    if (PluginConfiguration.TryGetPortableSceneScheduleTimeZoneId(
                            schedule.TimeZoneId,
                            out var portableTimeZoneId))
                    {
                        schedule.TimeZoneId = portableTimeZoneId;
                    }
                    else
                    {
                        var scheduleLabel = string.IsNullOrWhiteSpace(schedule.Name)
                            ? "Imported scene schedule"
                            : $"Imported scene schedule '{schedule.Name.Trim()}'";
                        validationErrors.Add(
                            $"{scheduleLabel} time zone '{schedule.TimeZoneId.Trim()}' cannot be mapped to a portable IANA identifier");
                    }
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
                    if (scheduleRequest != null && !scheduleRequest.Priority.HasValue)
                        schedule.Priority = candidateSchedules[existingIndex].Priority;
                    if (scheduleRequest != null && scheduleRequest.PlaybackPolicy == null)
                        schedule.PlaybackPolicy = candidateSchedules[existingIndex].PlaybackPolicy;
                    if (scheduleRequest != null && !scheduleRequest.RedSpecified)
                        schedule.Red = candidateSchedules[existingIndex].Red;
                    if (scheduleRequest != null && !scheduleRequest.GreenSpecified)
                        schedule.Green = candidateSchedules[existingIndex].Green;
                    if (scheduleRequest != null && !scheduleRequest.BlueSpecified)
                        schedule.Blue = candidateSchedules[existingIndex].Blue;
                    if (scheduleRequest != null && !scheduleRequest.BrightnessSpecified)
                        schedule.BrightnessPercent = candidateSchedules[existingIndex].BrightnessPercent;
                    if (scheduleRequest != null && !scheduleRequest.DurationSpecified)
                        schedule.DurationSeconds = candidateSchedules[existingIndex].DurationSeconds;
                    if (scheduleRequest != null && !scheduleRequest.TargetAllEnabledMappings.HasValue)
                        schedule.TargetAllEnabledMappings = candidateSchedules[existingIndex].TargetAllEnabledMappings;
                    if (scheduleRequest?.TargetUserIds == null)
                    {
                        schedule.TargetUserIds = scheduleRequest?.TargetAllEnabledMappings == true ||
                            !string.IsNullOrWhiteSpace(scheduleRequest?.TargetUserId)
                            ? new List<string>()
                            : candidateSchedules[existingIndex].TargetUserIds?.ToList() ?? new List<string>();
                    }
                    if (scheduleRequest?.TargetRoutes == null)
                    {
                        schedule.TargetRoutes = scheduleRequest?.TargetAllEnabledMappings == true ||
                            !string.IsNullOrWhiteSpace(scheduleRequest?.TargetUserId) ||
                            scheduleRequest?.TargetUserIds != null
                            ? new List<HueSceneScheduleTargetRoute>()
                            : candidateSchedules[existingIndex].TargetRoutes?.Where(route => route != null)
                                .Select(route => new HueSceneScheduleTargetRoute
                                {
                                    UserId = route.UserId?.Trim() ?? string.Empty,
                                    DeviceId = route.DeviceId?.Trim() ?? string.Empty
                                }).ToList() ?? new List<HueSceneScheduleTargetRoute>();
                    }
                    if (scheduleRequest?.IncludeDefaultTarget == null)
                    {
                        schedule.IncludeDefaultTarget = scheduleRequest?.TargetAllEnabledMappings == true ||
                            !string.IsNullOrWhiteSpace(scheduleRequest?.TargetUserId)
                            ? false
                            : candidateSchedules[existingIndex].IncludeDefaultTarget;
                    }
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
                ScenePlaylists = candidatePlaylists,
                UserMappings = candidateMappings,
                SceneSchedules = candidateSchedules
            };
            validationErrors.AddRange(scheduleValidationConfiguration.ValidateSceneSchedules());

            var candidateConfiguration = new PluginConfiguration();
            var baselineSettings = HuePluginConfigurationSettings.From(config);
            baselineSettings.ApplyTo(candidateConfiguration);
            candidateConfiguration.HueAppKey = config.HueAppKey;
            candidateConfiguration.HueClientKey = config.HueClientKey;
            request.Configuration!.ApplyTo(candidateConfiguration);
            candidateConfiguration.UserMappings = candidateMappings;
            candidateConfiguration.ColorPresets = candidatePresets;
            candidateConfiguration.ScenePlaylists = candidatePlaylists;
            candidateConfiguration.SceneSchedules = candidateSchedules;
            if (validationErrors.Count == 0)
                validationErrors.AddRange(candidateConfiguration.Validate());

            var diff = BuildConfigurationImportDiff(
                config,
                candidateConfiguration,
                existingMappings,
                candidateMappings,
                existingPresets,
                candidatePresets,
                existingPlaylists,
                candidatePlaylists,
                existingSchedules,
                candidateSchedules);

            return new HueConfigurationImportPlan
            {
                CandidateConfiguration = candidateConfiguration,
                CandidateMappings = candidateMappings,
                CandidatePresets = candidatePresets,
                CandidatePlaylists = candidatePlaylists,
                CandidateSchedules = candidateSchedules,
                ImportedMappingCount = importedMappingValues.Count,
                ImportedPresetCount = importedPresets.Count,
                ImportedPlaylistCount = importedPlaylists.Count,
                ImportedScheduleCount = importedSchedules.Count,
                GlobalAppKeyPreserved = string.IsNullOrWhiteSpace(request.Configuration.HueAppKey) &&
                    !request.Configuration.ClearStoredCredentials &&
                    !string.IsNullOrWhiteSpace(config.HueAppKey),
                GlobalClientKeyPreserved = string.IsNullOrWhiteSpace(request.Configuration.HueClientKey) &&
                    !request.Configuration.ClearStoredCredentials &&
                    !string.IsNullOrWhiteSpace(config.HueClientKey),
                MappingCredentialPairsPreserved = mappingCredentialPairsPreserved,
                Diff = diff,
                ValidationErrors = validationErrors
            };
        }

        private static HueConfigurationImportDiff BuildConfigurationImportDiff(
            PluginConfiguration existingConfiguration,
            PluginConfiguration candidateConfiguration,
            IReadOnlyList<UserBridgeMapping> existingMappings,
            IReadOnlyList<UserBridgeMapping> candidateMappings,
            IReadOnlyList<HueColorPreset> existingPresets,
            IReadOnlyList<HueColorPreset> candidatePresets,
            IReadOnlyList<HueScenePlaylist> existingPlaylists,
            IReadOnlyList<HueScenePlaylist> candidatePlaylists,
            IReadOnlyList<HueSceneSchedule> existingSchedules,
            IReadOnlyList<HueSceneSchedule> candidateSchedules)
        {
            var globalSettingsChanged = !AreEquivalentConfigurationSettings(
                HuePluginConfigurationSettings.From(existingConfiguration),
                HuePluginConfigurationSettings.From(candidateConfiguration));
            var globalAppKeyChanged = !string.Equals(
                existingConfiguration.HueAppKey,
                candidateConfiguration.HueAppKey,
                StringComparison.Ordinal);
            var globalClientKeyChanged = !string.Equals(
                existingConfiguration.HueClientKey,
                candidateConfiguration.HueClientKey,
                StringComparison.Ordinal);
            var mappings = CompareImportCollection(
                existingMappings,
                candidateMappings,
                mapping => PluginConfiguration.NormalizeJellyfinUserId(mapping.UserId),
                AreEquivalentMapping);
            var presets = CompareImportCollection(
                existingPresets,
                candidatePresets,
                preset => preset.Name,
                AreEquivalentPreset);
            var playlists = CompareImportCollection(
                existingPlaylists,
                candidatePlaylists,
                playlist => playlist.Id,
                AreEquivalentPlaylist);
            var schedules = CompareImportCollection(
                existingSchedules,
                candidateSchedules,
                schedule => schedule.Id,
                AreEquivalentSchedule);

            return new HueConfigurationImportDiff
            {
                HasChanges = globalSettingsChanged || globalAppKeyChanged || globalClientKeyChanged ||
                    mappings.HasChanges || presets.HasChanges || playlists.HasChanges || schedules.HasChanges,
                GlobalSettingsChanged = globalSettingsChanged,
                GlobalAppKeyChanged = globalAppKeyChanged,
                GlobalClientKeyChanged = globalClientKeyChanged,
                Mappings = mappings,
                ColorPresets = presets,
                ScenePlaylists = playlists,
                SceneSchedules = schedules
            };
        }

        private static HueConfigurationImportCollectionDiff CompareImportCollection<T>(
            IEnumerable<T> existingItems,
            IEnumerable<T> candidateItems,
            Func<T, string?> keySelector,
            Func<T, T, bool> equivalent)
        {
            var existing = existingItems.ToList();
            var matched = new bool[existing.Count];
            var added = 0;
            var changed = 0;
            var unchanged = 0;

            foreach (var candidate in candidateItems)
            {
                var candidateKey = keySelector(candidate)?.Trim() ?? string.Empty;
                var existingIndex = -1;
                for (var index = 0; index < existing.Count; index++)
                {
                    if (matched[index])
                        continue;

                    var existingKey = keySelector(existing[index])?.Trim() ?? string.Empty;
                    if (string.Equals(existingKey, candidateKey, StringComparison.OrdinalIgnoreCase))
                    {
                        existingIndex = index;
                        break;
                    }
                }

                if (existingIndex < 0)
                {
                    added++;
                    continue;
                }

                matched[existingIndex] = true;
                if (equivalent(existing[existingIndex], candidate))
                    unchanged++;
                else
                    changed++;
            }

            return new HueConfigurationImportCollectionDiff
            {
                Added = added,
                Removed = matched.Count(wasMatched => !wasMatched),
                Changed = changed,
                Unchanged = unchanged
            };
        }

        private static bool AreEquivalentConfigurationSettings(
            HuePluginConfigurationSettings left,
            HuePluginConfigurationSettings right)
        {
            left.HasAppKey = false;
            left.HasClientKey = false;
            right.HasAppKey = false;
            right.HasClientKey = false;
            return string.Equals(
                JsonSerializer.Serialize(left),
                JsonSerializer.Serialize(right),
                StringComparison.Ordinal);
        }

        private static bool AreEquivalentMapping(UserBridgeMapping left, UserBridgeMapping right)
        {
            if (!string.Equals(left.HueAppKey, right.HueAppKey, StringComparison.Ordinal) ||
                !string.Equals(left.HueClientKey, right.HueClientKey, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(
                JsonSerializer.Serialize(UserBridgeMappingSummary.From(left)),
                JsonSerializer.Serialize(UserBridgeMappingSummary.From(right)),
                StringComparison.Ordinal))
            {
                return false;
            }

            var leftTargets = left.DeviceTargets ?? new List<UserDeviceBridgeTarget>();
            var rightTargets = right.DeviceTargets ?? new List<UserDeviceBridgeTarget>();
            if (leftTargets.Count != rightTargets.Count)
                return false;

            for (var index = 0; index < leftTargets.Count; index++)
            {
                var leftTarget = leftTargets[index];
                var rightTarget = rightTargets[index];
                if (leftTarget == null || rightTarget == null)
                {
                    if (leftTarget != null || rightTarget != null)
                        return false;
                    continue;
                }

                if (!string.Equals(leftTarget.HueAppKey, rightTarget.HueAppKey, StringComparison.Ordinal) ||
                    !string.Equals(leftTarget.HueClientKey, rightTarget.HueClientKey, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool AreEquivalentPreset(HueColorPreset left, HueColorPreset right) =>
            string.Equals(JsonSerializer.Serialize(left), JsonSerializer.Serialize(right), StringComparison.Ordinal);

        private static bool AreEquivalentPlaylist(HueScenePlaylist left, HueScenePlaylist right) =>
            string.Equals(JsonSerializer.Serialize(left), JsonSerializer.Serialize(right), StringComparison.Ordinal);

        private static bool AreEquivalentSchedule(HueSceneSchedule left, HueSceneSchedule right) =>
            string.Equals(JsonSerializer.Serialize(left), JsonSerializer.Serialize(right), StringComparison.Ordinal);

        private static List<UserBridgeMapping> MergeMappings(
            IEnumerable<UserBridgeMapping> existingMappings,
            IEnumerable<UserBridgeMapping> importedMappings)
        {
            var merged = new List<UserBridgeMapping>(existingMappings);
            foreach (var imported in importedMappings)
            {
                merged.RemoveAll(existing =>
                    PluginConfiguration.AreSameJellyfinUserId(existing.UserId, imported.UserId));
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
                DeviceTargets = (source.DeviceTargets ?? Array.Empty<UserDeviceBridgeTargetSummary>())
                    .Where(target => target != null)
                    .Select(target => new UserDeviceBridgeTarget
                    {
                        DeviceId = target.DeviceId?.Trim() ?? string.Empty,
                        DeviceName = target.DeviceName?.Trim() ?? string.Empty,
                        HueBridgeIp = target.HueBridgeIp?.Trim() ?? string.Empty,
                        EntertainmentAreaId = target.EntertainmentAreaId?.Trim() ?? string.Empty,
                        EntertainmentAreaName = target.EntertainmentAreaName?.Trim() ?? string.Empty,
                        ChannelIdsOverride = target.ChannelIdsOverride?.Trim()
                    })
                    .ToList(),
                UseCinemaModeOverride = source.UseCinemaModeOverride,
                BrightnessDimLevelOverride = source.BrightnessDimLevelOverride,
                PauseBehaviorOverride = source.PauseBehaviorOverride?.Trim(),
                RestoreLightStateOverride = source.RestoreLightStateOverride,
                PlaybackMediaFilterOverride = PluginConfiguration.NormalizeOptionalPlaybackMediaFilter(source.PlaybackMediaFilterOverride),
                BrightnessBoostOverride = source.BrightnessBoostOverride,
                RedGainOverride = source.RedGainOverride,
                GreenGainOverride = source.GreenGainOverride,
                BlueGainOverride = source.BlueGainOverride,
                ColorSaturationOverride = source.ColorSaturationOverride,
                HueShiftDegreesOverride = source.HueShiftDegreesOverride,
                OutputBrightnessPercentOverride = source.OutputBrightnessPercentOverride,
                GammaCorrectionOverride = source.GammaCorrectionOverride,
                ContrastPercentOverride = source.ContrastPercentOverride,
                ColorTemperatureKelvinOverride = source.ColorTemperatureKelvinOverride,
                BlackoutThresholdOverride = source.BlackoutThresholdOverride,
                BlackoutBehaviorOverride = PluginConfiguration.NormalizeOptionalBlackoutBehavior(source.BlackoutBehaviorOverride),
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
                SpatialOrientationOverride = PluginConfiguration.NormalizeOptionalSpatialOrientation(source.SpatialOrientationOverride),
                ColorSmoothingPercentOverride = source.ColorSmoothingPercentOverride,
                AudioSensitivityPercentOverride = source.AudioSensitivityPercentOverride,
                AudioNoiseGatePercentOverride = source.AudioNoiseGatePercentOverride,
                AudioLowFrequencyHzOverride = source.AudioLowFrequencyHzOverride,
                AudioMidFrequencyHzOverride = source.AudioMidFrequencyHzOverride,
                AudioHighFrequencyHzOverride = source.AudioHighFrequencyHzOverride,
                AudioLowGainPercentOverride = source.AudioLowGainPercentOverride,
                AudioMidGainPercentOverride = source.AudioMidGainPercentOverride,
                AudioHighGainPercentOverride = source.AudioHighGainPercentOverride,
                AudioResponseSmoothingPercentOverride = source.AudioResponseSmoothingPercentOverride,
                AudioBandSpreadPercentOverride = source.AudioBandSpreadPercentOverride,
                AudioBeatPulsePercentOverride = source.AudioBeatPulsePercentOverride,
                AudioBeatPulseDecayPercentOverride = source.AudioBeatPulseDecayPercentOverride,
                AudioBeatPulseThresholdPercentOverride = source.AudioBeatPulseThresholdPercentOverride,
                AudioColorPaletteOverride = PluginConfiguration.NormalizeOptionalAudioColorPalette(source.AudioColorPaletteOverride),
                AudioSpatialModeOverride = PluginConfiguration.NormalizeOptionalAudioSpatialMode(source.AudioSpatialModeOverride),
                AudioChannelModeOverride = PluginConfiguration.NormalizeOptionalAudioChannelMode(source.AudioChannelModeOverride)
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

            var explicitDeviceCredentials = (source.DeviceTargetCredentials ?? new List<UserDeviceBridgeTargetImport>())
                .Where(target => target != null)
                .GroupBy(target => target.DeviceId?.Trim() ?? string.Empty, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            foreach (var deviceTarget in mapping.DeviceTargets ?? new List<UserDeviceBridgeTarget>())
            {
                if (explicitDeviceCredentials.TryGetValue(deviceTarget.DeviceId?.Trim() ?? string.Empty, out var replacement))
                {
                    if (!string.IsNullOrWhiteSpace(replacement.DeviceName))
                        deviceTarget.DeviceName = replacement.DeviceName.Trim();
                    if (!string.IsNullOrWhiteSpace(replacement.HueBridgeIp))
                        deviceTarget.HueBridgeIp = replacement.HueBridgeIp.Trim();
                    if (!string.IsNullOrWhiteSpace(replacement.HueAppKey))
                        deviceTarget.HueAppKey = replacement.HueAppKey.Trim();
                    if (!string.IsNullOrWhiteSpace(replacement.HueClientKey))
                        deviceTarget.HueClientKey = replacement.HueClientKey.Trim();
                    if (!string.IsNullOrWhiteSpace(replacement.EntertainmentAreaId))
                        deviceTarget.EntertainmentAreaId = replacement.EntertainmentAreaId.Trim();
                    if (!string.IsNullOrWhiteSpace(replacement.EntertainmentAreaName))
                        deviceTarget.EntertainmentAreaName = replacement.EntertainmentAreaName.Trim();
                    if (!string.IsNullOrWhiteSpace(replacement.ChannelIdsOverride))
                        deviceTarget.ChannelIdsOverride = replacement.ChannelIdsOverride.Trim();
                }

                var existingDeviceTarget = existing?.DeviceTargets?.FirstOrDefault(candidate =>
                    candidate != null &&
                    string.Equals(candidate.DeviceId?.Trim(), deviceTarget.DeviceId?.Trim(), StringComparison.Ordinal));
                if (existingDeviceTarget != null &&
                    IsSameBridgeTarget(deviceTarget.HueBridgeIp, existingDeviceTarget.HueBridgeIp))
                {
                    if (string.IsNullOrWhiteSpace(deviceTarget.HueAppKey))
                        deviceTarget.HueAppKey = existingDeviceTarget.HueAppKey;
                    if (string.IsNullOrWhiteSpace(deviceTarget.HueClientKey))
                        deviceTarget.HueClientKey = existingDeviceTarget.HueClientKey;
                }
            }

            if (!mapping.SyncEnabled || string.IsNullOrWhiteSpace(mapping.HueBridgeIp))
            {
                mapping.HueBridgeIp = string.Empty;
                mapping.HueAppKey = string.Empty;
                mapping.HueClientKey = string.Empty;
                mapping.EntertainmentAreaId = string.Empty;
                mapping.EntertainmentAreaName = string.Empty;
                if (!mapping.SyncEnabled)
                    mapping.DeviceTargets = new List<UserDeviceBridgeTarget>();
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
            errors.AddRange(PluginConfiguration.ValidateDeviceTargets(mapping, label));

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
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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

            var credentialTransitionErrors = ValidateGlobalCredentialTransition(
                config,
                settings,
                "Configuration save");
            if (credentialTransitionErrors.Count > 0)
            {
                return BadRequest(new
                {
                    message = "Configuration is invalid.",
                    errors = credentialTransitionErrors
                });
            }

            var previousSettings = HuePluginConfigurationSettings.From(config);
            var previousAppKey = config.HueAppKey;
            var previousClientKey = config.HueClientKey;
            var previousPersistedSessionHistory = (config.PersistedSessionHistory ?? new List<HueSessionHistoryEntry>()).ToList();
            var previousPersistedSceneScheduleHistory = (config.PersistedSceneScheduleHistory ?? new List<HueSceneScheduleHistoryEntry>()).ToList();
            settings.ApplyTo(config);
            var validationErrors = config.Validate();
            if (validationErrors.Count > 0)
            {
                previousSettings.ApplyTo(config);
                config.HueAppKey = previousAppKey;
                config.HueClientKey = previousClientKey;
                config.PersistedSessionHistory = previousPersistedSessionHistory;
                config.PersistedSceneScheduleHistory = previousPersistedSceneScheduleHistory;
                return BadRequest(new
                {
                    message = "Configuration is invalid.",
                    errors = validationErrors
                });
            }

            try
            {
                plugin.SaveConfiguration();
            }
            catch (Exception ex)
            {
                previousSettings.ApplyTo(config);
                config.HueAppKey = previousAppKey;
                config.HueClientKey = previousClientKey;
                config.PersistedSessionHistory = previousPersistedSessionHistory;
                config.PersistedSceneScheduleHistory = previousPersistedSceneScheduleHistory;
                _logger?.LogError(ex, "Could not persist Hue plugin configuration");
                return StatusCode(StatusCodes.Status500InternalServerError, "Configuration could not be saved.");
            }

            _syncService?.RefreshSessionHistoryPersistence();
            _sceneAutomationService?.RefreshSceneScheduleHistoryPersistence();
            return Ok(HuePluginConfigurationSettings.From(config));
        }

        /// <summary>
        /// Gets all user-to-bridge mappings without returning stored credentials. Optional
        /// per-user playback media scope, playback, color-threshold, performance, execution,
        /// channel, and restoration profile values are included because they are not secret.
        /// </summary>
        [HttpGet("UserMappings")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<IEnumerable<UserBridgeMappingSummary>> GetUserMappings()
        {
            var config = Plugin.Instance?.Configuration;
            var mappings = config?.UserMappings?
                .Where(mapping => mapping != null)
                .Select(UserBridgeMappingSummary.From)
                ?? Enumerable.Empty<UserBridgeMappingSummary>();
            return Ok(mappings);
        }

        /// <summary>
        /// Inspects scheduled-cue references to one user mapping without returning bridge
        /// credentials or target details. The result lets an administrator understand why
        /// disabling or deleting a mapping may be blocked before changing it.
        /// </summary>
        [HttpGet("UserMappings/{userId}/Dependencies")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<HueUserMappingDependenciesResult> GetUserMappingDependencies(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
                return NotFound("User mapping not found.");

            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return NotFound("Plugin configuration not available.");

            var normalizedUserId = userId.Trim();
            var mapping = config.UserMappings?.FirstOrDefault(candidate =>
                candidate != null &&
                PluginConfiguration.AreSameJellyfinUserId(candidate.UserId, normalizedUserId));
            if (mapping == null)
                return NotFound("Mapping not found for the specified user.");

            return Ok(BuildUserMappingDependenciesResult(mapping, config));
        }

        private static HueUserMappingDependenciesResult BuildUserMappingDependenciesResult(
            UserBridgeMapping mapping,
            PluginConfiguration config)
        {
            var normalizedUserId = mapping.UserId?.Trim() ?? string.Empty;
            var schedules = (config.SceneSchedules ?? new List<HueSceneSchedule>())
                .Where(schedule => schedule != null && ScheduleReferencesUserMapping(schedule, normalizedUserId))
                .Select(schedule => new HueUserMappingScheduleDependencyResult
                {
                    Id = schedule.Id?.Trim() ?? string.Empty,
                    Name = schedule.Name?.Trim() ?? string.Empty,
                    Enabled = schedule.Enabled
                })
                .OrderBy(schedule => schedule.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(schedule => schedule.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var playlists = (config.ScenePlaylists ?? new List<HueScenePlaylist>())
                .Where(playlist => playlist != null &&
                    (PluginConfiguration.AreSameJellyfinUserId(playlist.TargetUserId, normalizedUserId) ||
                     (playlist.TargetUserIds ?? new List<string>()).Any(targetUserId =>
                         PluginConfiguration.AreSameJellyfinUserId(targetUserId, normalizedUserId))))
                .Select(playlist => new HueUserMappingPlaylistDependencyResult
                {
                    Id = playlist.Id?.Trim() ?? string.Empty,
                    Name = playlist.Name?.Trim() ?? string.Empty
                })
                .OrderBy(playlist => playlist.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(playlist => playlist.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new HueUserMappingDependenciesResult
            {
                UserId = mapping.UserId?.Trim() ?? normalizedUserId,
                UserName = mapping.UserName?.Trim() ?? string.Empty,
                SyncEnabled = mapping.SyncEnabled,
                CanDisable = !mapping.SyncEnabled || (schedules.Length == 0 && playlists.Length == 0),
                CanDelete = schedules.Length == 0 && playlists.Length == 0,
                ScheduledCueCount = schedules.Length,
                ScheduledCues = schedules,
                ScenePlaylistCount = playlists.Length,
                ScenePlaylists = playlists
            };
        }

        private static bool ScheduleReferencesUserMapping(HueSceneSchedule schedule, string userId)
            => PluginConfiguration.AreSameJellyfinUserId(schedule.TargetUserId, userId) ||
               (schedule.TargetUserIds ?? new List<string>()).Any(targetUserId =>
                   PluginConfiguration.AreSameJellyfinUserId(targetUserId, userId)) ||
               (schedule.TargetRoutes ?? new List<HueSceneScheduleTargetRoute>()).Any(route =>
                   route != null && PluginConfiguration.AreSameJellyfinUserId(route.UserId, userId));

        private static string? GetUserMappingEnableValidationError(UserBridgeMapping mapping)
        {
            var label = string.IsNullOrWhiteSpace(mapping.UserName)
                ? mapping.UserId?.Trim() ?? "selected mapping"
                : mapping.UserName.Trim();
            var bridgeIp = mapping.HueBridgeIp?.Trim() ?? string.Empty;
            var appKey = mapping.HueAppKey?.Trim() ?? string.Empty;
            var clientKey = mapping.HueClientKey?.Trim() ?? string.Empty;
            var areaId = mapping.EntertainmentAreaId?.Trim() ?? string.Empty;
            var deviceTargetError = PluginConfiguration.ValidateDeviceTargets(mapping, label).FirstOrDefault();
            if (deviceTargetError != null)
                return deviceTargetError;

            if (string.IsNullOrWhiteSpace(bridgeIp))
            {
                if (!string.IsNullOrWhiteSpace(appKey) ||
                    !string.IsNullOrWhiteSpace(clientKey) ||
                    !string.IsNullOrWhiteSpace(areaId))
                {
                    return $"{label}: clear the custom bridge address, credentials, and entertainment area to inherit the global target.";
                }

                return null;
            }

            if (!HueBridgeCertificateValidation.IsValidBridgeAddress(bridgeIp))
                return $"{label}: the custom bridge address is invalid.";

            if (string.IsNullOrWhiteSpace(appKey) ||
                string.IsNullOrWhiteSpace(clientKey) ||
                string.IsNullOrWhiteSpace(areaId))
            {
                return $"{label}: a custom bridge target requires an address, App Key, Client Key, and entertainment area ID.";
            }

            return null;
        }

        private static void ClearDisabledUserMappingTarget(UserBridgeMapping mapping)
        {
            mapping.HueBridgeIp = string.Empty;
            mapping.HueAppKey = string.Empty;
            mapping.HueClientKey = string.Empty;
            mapping.EntertainmentAreaId = string.Empty;
            mapping.EntertainmentAreaName = string.Empty;
            mapping.DeviceTargets = new List<UserDeviceBridgeTarget>();
        }

        private static List<UserDeviceBridgeTarget> CloneDeviceTargets(IEnumerable<UserDeviceBridgeTarget>? targets)
        {
            return (targets ?? Array.Empty<UserDeviceBridgeTarget>())
                .Where(target => target != null)
                .Select(target => new UserDeviceBridgeTarget
                {
                    DeviceId = target.DeviceId,
                    DeviceName = target.DeviceName,
                    HueBridgeIp = target.HueBridgeIp,
                    HueAppKey = target.HueAppKey,
                    HueClientKey = target.HueClientKey,
                    EntertainmentAreaId = target.EntertainmentAreaId,
                    EntertainmentAreaName = target.EntertainmentAreaName,
                    ChannelIdsOverride = target.ChannelIdsOverride
                })
                .ToList();
        }

        private static void PreserveDeviceTargetCredentials(
            UserBridgeMapping mapping,
            UserBridgeMapping? existingMapping)
        {
            mapping.DeviceTargets ??= new List<UserDeviceBridgeTarget>();
            if (existingMapping?.DeviceTargets == null)
                return;

            foreach (var target in mapping.DeviceTargets)
            {
                if (target == null)
                    continue;

                var existingTarget = existingMapping.DeviceTargets.FirstOrDefault(candidate =>
                    candidate != null &&
                    string.Equals(candidate.DeviceId?.Trim(), target.DeviceId?.Trim(), StringComparison.Ordinal) &&
                    IsSameBridgeTarget(candidate.HueBridgeIp, target.HueBridgeIp));
                if (existingTarget == null)
                    continue;

                if (string.IsNullOrWhiteSpace(target.HueAppKey))
                    target.HueAppKey = existingTarget.HueAppKey;
                if (string.IsNullOrWhiteSpace(target.HueClientKey))
                    target.HueClientKey = existingTarget.HueClientKey;
            }
        }

        /// <summary>
        /// Saves or updates a user-to-bridge mapping. A mapping can opt a user out of
        /// synchronization without storing bridge credentials and can override playback media scope, playback behavior, color processing and scene thresholds,
        /// capture-performance, execution, channel selection, or light-restoration settings.
        /// </summary>
        [HttpPost("UserMappings")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public ActionResult SaveUserMapping([FromBody] HueUserMappingRequest? request)
        {
            if (request == null)
            {
                return BadRequest("Mapping is required.");
            }

            var mapping = request.ToConfigurationMapping();
            if (!Guid.TryParse(mapping.UserId?.Trim(), out var parsedUserId))
            {
                return BadRequest("userId must be a valid Jellyfin user ID.");
            }

            // Jellyfin resolves user IDs using the canonical D-format text. Normalize
            // every accepted GUID before persistence so brace/N-format inputs remain
            // addressable by the runtime's Guid-based mapping lookup.
            mapping.UserId = parsedUserId.ToString("D");
            return SaveUserMappingCore(mapping);
        }

        // Keep the strongly typed helper available to the existing in-process callers
        // while the HTTP action uses a write-only request contract. UserBridgeMapping
        // intentionally ignores bridge keys during JSON deserialization so that its
        // summaries and generic configuration payloads cannot leak them; binding that
        // type directly here would therefore make manually entered mapping keys vanish.
        [NonAction]
        internal ActionResult SaveUserMapping(UserBridgeMapping mapping)
            => SaveUserMappingCore(mapping);

        private ActionResult SaveUserMappingCore(UserBridgeMapping mapping)
        {
            if (mapping == null)
            {
                return BadRequest("Mapping is required.");
            }

            if (string.IsNullOrWhiteSpace(mapping.UserId))
            {
                return BadRequest("User ID is required.");
            }

            mapping.UserId = PluginConfiguration.NormalizeJellyfinUserId(mapping.UserId);

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

            mapping.PlaybackMediaFilterOverride = PluginConfiguration.NormalizeOptionalPlaybackMediaFilter(mapping.PlaybackMediaFilterOverride);
            mapping.BlackoutBehaviorOverride = PluginConfiguration.NormalizeOptionalBlackoutBehavior(mapping.BlackoutBehaviorOverride);

            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
            {
                return BadRequest("Plugin configuration not available.");
            }

            var scheduledCueCount = config.SceneSchedules?.Count(schedule =>
                schedule != null && ScheduleReferencesUserMapping(schedule, mapping.UserId.Trim())) ?? 0;
            var scenePlaylistCount = config.ScenePlaylists?.Count(playlist =>
                playlist != null &&
                (PluginConfiguration.AreSameJellyfinUserId(playlist.TargetUserId, mapping.UserId) ||
                 (playlist.TargetUserIds ?? new List<string>()).Any(targetUserId =>
                     PluginConfiguration.AreSameJellyfinUserId(targetUserId, mapping.UserId)))) ?? 0;
            if ((scheduledCueCount > 0 || scenePlaylistCount > 0) && !mapping.SyncEnabled)
            {
                return Conflict($"This user mapping is used by {scheduledCueCount} scheduled cue(s) and {scenePlaylistCount} saved playlist(s). Delete or update those targets before disabling the mapping.");
            }

            config.UserMappings ??= new List<UserBridgeMapping>();
            var existingMapping = config.UserMappings.FirstOrDefault(existing =>
                existing != null &&
                PluginConfiguration.AreSameJellyfinUserId(existing.UserId, mapping.UserId));

            if (mapping.SyncEnabled && existingMapping != null)
            {
                var requestedDeviceIds = new HashSet<string>(
                    (mapping.DeviceTargets ?? new List<UserDeviceBridgeTarget>())
                        .Where(target => target != null && !string.IsNullOrWhiteSpace(target.DeviceId))
                        .Select(target => target.DeviceId.Trim()),
                    StringComparer.Ordinal);
                var removedRouteSchedules = (config.SceneSchedules ?? new List<HueSceneSchedule>())
                    .Where(schedule => schedule != null &&
                        (schedule.TargetRoutes ?? new List<HueSceneScheduleTargetRoute>()).Any(route =>
                            route != null &&
                            PluginConfiguration.AreSameJellyfinUserId(route.UserId, mapping.UserId) &&
                            !requestedDeviceIds.Contains(route.DeviceId?.Trim() ?? string.Empty)))
                    .Select(schedule => schedule.Name?.Trim() ?? schedule.Id?.Trim() ?? "unnamed cue")
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (removedRouteSchedules.Length > 0)
                {
                    return Conflict(
                        $"This mapping's device targets are used by scheduled cue(s): {string.Join(", ", removedRouteSchedules)}. Keep those device routes or update the scheduled cues first.");
                }
            }

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

            if (!mapping.SyncEnabled)
            {
                // A disabled mapping is only a per-user opt-out. Scrub nested
                // routes before validation so stale or incomplete credentials
                // cannot block the opt-out or remain persisted.
                ClearDisabledUserMappingTarget(mapping);
            }
            else
            {
                PreserveDeviceTargetCredentials(mapping, existingMapping);
                var deviceTargetErrors = PluginConfiguration.ValidateDeviceTargets(mapping, overrideLabel);
                if (deviceTargetErrors.Count > 0)
                {
                    return BadRequest(new
                    {
                        message = "Automatic playback device targets are invalid.",
                        errors = deviceTargetErrors
                    });
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

            var previousMappings = config.UserMappings.ToList();
            var candidateMappings = previousMappings
                .Where(existing => existing != null)
                .ToList();
            candidateMappings.RemoveAll(existing =>
                PluginConfiguration.AreSameJellyfinUserId(existing.UserId, mapping.UserId));
            candidateMappings.Add(mapping);
            config.UserMappings = candidateMappings;

            try
            {
                plugin.SaveConfiguration();
            }
            catch (Exception ex)
            {
                config.UserMappings = previousMappings;
                _logger?.LogError(ex, "Could not persist Hue user mapping {0}", mapping.UserId);
                return StatusCode(StatusCodes.Status500InternalServerError, "The user mapping could not be saved.");
            }

            return Ok(new { message = "Mapping saved successfully." });
        }

        /// <summary>
        /// Deletes a user-to-bridge mapping
        /// </summary>
        [HttpDelete("UserMappings/{userId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public ActionResult DeleteUserMapping(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return BadRequest("User ID is required.");
            }

            var normalizedUserId = userId.Trim();

            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
            {
                return NotFound("Plugin configuration not available.");
            }

            var scheduledCueCount = config.SceneSchedules?.Count(schedule =>
                schedule != null && ScheduleReferencesUserMapping(schedule, normalizedUserId)) ?? 0;
            var scenePlaylistCount = config.ScenePlaylists?.Count(playlist =>
                playlist != null &&
                (PluginConfiguration.AreSameJellyfinUserId(playlist.TargetUserId, normalizedUserId) ||
                 (playlist.TargetUserIds ?? new List<string>()).Any(targetUserId =>
                     PluginConfiguration.AreSameJellyfinUserId(targetUserId, normalizedUserId)))) ?? 0;
            if (scheduledCueCount > 0 || scenePlaylistCount > 0)
            {
                return Conflict($"This user mapping is used by {scheduledCueCount} scheduled cue(s) and {scenePlaylistCount} saved playlist(s). Delete or update those targets first.");
            }

            config.UserMappings ??= new List<UserBridgeMapping>();
            var previousMappings = config.UserMappings.ToList();
            var candidateMappings = previousMappings
                .Where(mapping => mapping != null)
                .ToList();
            var removed = candidateMappings.RemoveAll(mapping =>
                PluginConfiguration.AreSameJellyfinUserId(mapping.UserId, normalizedUserId));
            if (removed == 0)
            {
                return NotFound("Mapping not found for the specified user.");
            }

            config.UserMappings = candidateMappings;
            try
            {
                plugin.SaveConfiguration();
            }
            catch (Exception ex)
            {
                config.UserMappings = previousMappings;
                _logger?.LogError(ex, "Could not persist deletion of Hue user mapping {0}", normalizedUserId);
                return StatusCode(StatusCodes.Status500InternalServerError, "The user mapping could not be deleted.");
            }

            return Ok(new { message = "Mapping deleted successfully." });
        }

        /// <summary>
        /// Deletes several per-user bridge mappings by user ID in one administrator
        /// operation. Every selected mapping is resolved and checked for scheduled-cue or
        /// saved-playlist target references before mutation; any missing ID, dependency, or persistence failure
        /// leaves the complete mapping collection unchanged.
        /// </summary>
        [HttpPost("UserMappings/BulkDelete")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public ActionResult<HueUserMappingBulkDeleteResult> DeleteUserMappingsBulk(
            [FromBody] HueUserMappingBulkDeleteRequest? request)
        {
            if (request == null)
                return BadRequest("A user-mapping selection is required.");
            if (request.UserIds?.Any(userId => string.IsNullOrWhiteSpace(userId)) == true)
                return BadRequest("Selected user mapping IDs must not be blank.");

            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
                return NotFound("Plugin configuration not available.");

            var userIds = (request.UserIds ?? new List<string>())
                .Where(userId => !string.IsNullOrWhiteSpace(userId))
                .Select(userId => userId.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (userIds.Length == 0)
                return BadRequest("Select at least one user mapping.");
            if (userIds.Length > PluginConfiguration.MaxBulkUserMappingDeletes)
            {
                return BadRequest(
                    $"Select no more than {PluginConfiguration.MaxBulkUserMappingDeletes} user mappings at once.");
            }

            config.UserMappings ??= new List<UserBridgeMapping>();
            var selectedMappings = userIds
                .Select(userId => config.UserMappings.FirstOrDefault(mapping =>
                    mapping != null &&
                    PluginConfiguration.AreSameJellyfinUserId(mapping.UserId, userId)))
                .ToArray();
            var missingUserIds = userIds
                .Where((_, index) => selectedMappings[index] == null)
                .ToArray();
            if (missingUserIds.Length > 0)
            {
                return NotFound(new HueUserMappingBulkDeleteResult
                {
                    RequestedCount = userIds.Length,
                    MissingUserIds = missingUserIds,
                    Message = $"The requested user mapping(s) were not found: {string.Join(", ", missingUserIds)}."
                });
            }

            var mappings = selectedMappings
                .Where(mapping => mapping != null)
                .Cast<UserBridgeMapping>()
                .ToArray();
            var blockedMappings = mappings
                .Select(mapping => BuildUserMappingDependenciesResult(mapping, config))
                .Where(dependencies => !dependencies.CanDelete)
                .ToArray();
            if (blockedMappings.Length > 0)
            {
                return Conflict(new HueUserMappingBulkDeleteResult
                {
                    RequestedCount = userIds.Length,
                    Message = "One or more selected user mappings are used by scheduled cues or saved playlists. Delete or update those targets first; no mappings were deleted.",
                    BlockedMappings = blockedMappings
                });
            }

            var previousMappings = config.UserMappings;
            var candidateMappings = previousMappings
                .Where(mapping => mapping == null || !userIds.Any(userId =>
                    PluginConfiguration.AreSameJellyfinUserId(mapping.UserId, userId)))
                .ToList();
            var deletedCount = previousMappings.Count - candidateMappings.Count;
            var deletedResults = previousMappings
                .Where(mapping => mapping != null && userIds.Any(userId =>
                    PluginConfiguration.AreSameJellyfinUserId(mapping.UserId, userId)))
                .Select(UserBridgeMappingSummary.From)
                .ToArray();
            config.UserMappings = candidateMappings;
            try
            {
                plugin.SaveConfiguration();
            }
            catch (Exception ex)
            {
                config.UserMappings = previousMappings;
                _logger?.LogError(ex, "Could not persist bulk deletion of Hue user mappings");
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    "The selected user mappings could not be deleted; no changes were retained.");
            }

            return Ok(new HueUserMappingBulkDeleteResult
            {
                RequestedCount = userIds.Length,
                DeletedCount = deletedCount,
                RemainingCount = config.UserMappings.Count,
                Message = $"Deleted {deletedCount} user mapping(s); scheduled-cue references were checked atomically.",
                Mappings = deletedResults
            });
        }

        /// <summary>
        /// Enables or disables several per-user bridge mappings in one administrator
        /// operation. Every selected mapping is resolved and validated before mutation;
        /// scheduled-cue or saved-playlist references block disabling, and disabling scrubs the custom
        /// bridge target exactly like the single-mapping save workflow. A persistence
        /// failure restores every selected mapping's prior state.
        /// </summary>
        [HttpPost("UserMappings/BulkEnabled")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public ActionResult<HueUserMappingBulkEnabledResult> SetUserMappingsEnabledBulk(
            [FromBody] HueUserMappingBulkEnabledRequest? request)
        {
            if (request == null)
                return BadRequest("A user-mapping selection and sync-enabled value are required.");

            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null)
                return NotFound("Plugin configuration not available.");

            var userIds = (request.UserIds ?? new List<string>())
                .Where(userId => !string.IsNullOrWhiteSpace(userId))
                .Select(userId => userId.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (userIds.Length == 0)
                return BadRequest("Select at least one user mapping.");
            if (userIds.Length > PluginConfiguration.MaxBulkUserMappingUpdates)
            {
                return BadRequest(
                    $"Select no more than {PluginConfiguration.MaxBulkUserMappingUpdates} user mappings at once.");
            }

            config.UserMappings ??= new List<UserBridgeMapping>();
            var selectedMappings = userIds
                .Select(userId => config.UserMappings.FirstOrDefault(mapping =>
                    mapping != null &&
                    PluginConfiguration.AreSameJellyfinUserId(mapping.UserId, userId)))
                .ToArray();
            var missingUserIds = userIds
                .Where((_, index) => selectedMappings[index] == null)
                .ToArray();
            if (missingUserIds.Length > 0)
            {
                return NotFound(new HueUserMappingBulkEnabledResult
                {
                    SyncEnabled = request.SyncEnabled,
                    RequestedCount = userIds.Length,
                    MissingUserIds = missingUserIds,
                    Message = $"The requested user mapping(s) were not found: {string.Join(", ", missingUserIds)}."
                });
            }

            var mappings = selectedMappings
                .Where(mapping => mapping != null)
                .Cast<UserBridgeMapping>()
                .ToArray();
            if (!request.SyncEnabled)
            {
                var blockedMappings = mappings
                    .Select(mapping => BuildUserMappingDependenciesResult(mapping, config))
                    .Where(dependencies => !dependencies.CanDisable)
                    .ToArray();
                if (blockedMappings.Length > 0)
                {
                    return Conflict(new HueUserMappingBulkEnabledResult
                    {
                        SyncEnabled = false,
                        RequestedCount = userIds.Length,
                        Message = "One or more selected user mappings are used by scheduled cues or saved playlists. Delete or update those targets first; no mappings were disabled.",
                        BlockedMappings = blockedMappings
                    });
                }
            }
            else
            {
                var validationErrors = mappings
                    .Select(mapping => new
                    {
                        Mapping = mapping,
                        Error = GetUserMappingEnableValidationError(mapping)
                    })
                    .Where(result => result.Error != null)
                    .ToArray();
                if (validationErrors.Length > 0)
                {
                    return Conflict(new HueUserMappingBulkEnabledResult
                    {
                        SyncEnabled = true,
                        RequestedCount = userIds.Length,
                        InvalidUserIds = validationErrors
                            .Select(result => result.Mapping.UserId?.Trim() ?? string.Empty)
                            .ToArray(),
                        Message = string.Join(" ", validationErrors.Select(result => result.Error)) + " No mappings were enabled."
                    });
                }
            }

            var previousStates = mappings.ToDictionary(
                mapping => mapping,
                mapping => (
                    SyncEnabled: mapping.SyncEnabled,
                    HueBridgeIp: mapping.HueBridgeIp,
                    HueAppKey: mapping.HueAppKey,
                    HueClientKey: mapping.HueClientKey,
                    EntertainmentAreaId: mapping.EntertainmentAreaId,
                    EntertainmentAreaName: mapping.EntertainmentAreaName,
                    DeviceTargets: CloneDeviceTargets(mapping.DeviceTargets)));

            foreach (var mapping in mappings)
            {
                mapping.SyncEnabled = request.SyncEnabled;
                if (!request.SyncEnabled)
                    ClearDisabledUserMappingTarget(mapping);
            }

            try
            {
                plugin.SaveConfiguration();
            }
            catch (Exception ex)
            {
                foreach (var mapping in mappings)
                {
                    if (!previousStates.TryGetValue(mapping, out var previous))
                        continue;

                    mapping.SyncEnabled = previous.SyncEnabled;
                    mapping.HueBridgeIp = previous.HueBridgeIp;
                    mapping.HueAppKey = previous.HueAppKey;
                    mapping.HueClientKey = previous.HueClientKey;
                    mapping.EntertainmentAreaId = previous.EntertainmentAreaId;
                    mapping.EntertainmentAreaName = previous.EntertainmentAreaName;
                    mapping.DeviceTargets = CloneDeviceTargets(previous.DeviceTargets);
                }

                _logger?.LogError(ex, "Could not persist bulk enabled state for Hue user mappings");
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    "The selected user-mapping enabled state could not be saved; no changes were retained.");
            }

            return Ok(new HueUserMappingBulkEnabledResult
            {
                SyncEnabled = request.SyncEnabled,
                RequestedCount = mappings.Length,
                UpdatedCount = mappings.Length,
                Message = request.SyncEnabled
                    ? $"Enabled {mappings.Length} user mapping(s) without changing their profiles."
                    : $"Disabled {mappings.Length} user mapping(s) and cleared their custom bridge targets.",
                Mappings = mappings.Select(UserBridgeMappingSummary.From).ToArray()
            });
        }

        private sealed class HueConfigurationImportPlan
        {
            public PluginConfiguration CandidateConfiguration { get; init; } = new();
            public List<UserBridgeMapping> CandidateMappings { get; init; } = new();
            public List<HueColorPreset> CandidatePresets { get; init; } = new();
            public List<HueScenePlaylist> CandidatePlaylists { get; init; } = new();
            public List<HueSceneSchedule> CandidateSchedules { get; init; } = new();
            public int ImportedMappingCount { get; init; }
            public int ImportedPresetCount { get; init; }
            public int ImportedPlaylistCount { get; init; }
            public int ImportedScheduleCount { get; init; }
            public bool GlobalAppKeyPreserved { get; init; }
            public bool GlobalClientKeyPreserved { get; init; }
            public int MappingCredentialPairsPreserved { get; init; }
            public HueConfigurationImportDiff Diff { get; init; } = new();
            public List<string> ValidationErrors { get; init; } = new();
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
        public string PlaybackMediaFilter { get; set; } = PluginConfiguration.PlaybackMediaFilterAllVideo;
        public string HueBridgeIp { get; set; } = string.Empty;
        public string HueAppKey { get; set; } = string.Empty;
        public string HueClientKey { get; set; } = string.Empty;
        public bool HasAppKey { get; set; }
        public bool HasClientKey { get; set; }
        public bool ClearStoredCredentials { get; set; }
        public bool? PersistSessionHistory { get; set; }
        public int? SessionHistoryRetentionCount { get; set; } = PluginConfiguration.DefaultSessionHistoryRetentionCount;
        public bool? PersistSceneScheduleHistory { get; set; }
        public int? SceneScheduleHistoryRetentionCount { get; set; } = PluginConfiguration.DefaultSceneScheduleHistoryRetentionCount;
        public bool? SceneAutomationEnabled { get; set; }
        public int SceneAutomationCatchUpMinutes { get; set; }
        public string? SceneAutomationPlaybackPolicy { get; set; }
        public string? SceneAutomationPlaybackScope { get; set; }
        public int? SceneAutomationDeferMinutes { get; set; }
        public string EntertainmentAreaId { get; set; } = string.Empty;
        public string ChannelIds { get; set; } = string.Empty;
        public bool UseCinemaMode { get; set; } = true;
        public int BrightnessDimLevel { get; set; } = 30;
        public string PauseBehavior { get; set; } = PluginConfiguration.PauseBehaviorKeepLastColors;
        public int AudioSensitivityPercent { get; set; } = PluginConfiguration.DefaultAudioSensitivityPercent;
        public int AudioNoiseGatePercent { get; set; } = PluginConfiguration.DefaultAudioNoiseGatePercent;
        public int AudioLowFrequencyHz { get; set; } = PluginConfiguration.DefaultAudioLowFrequencyHz;
        public int AudioMidFrequencyHz { get; set; } = PluginConfiguration.DefaultAudioMidFrequencyHz;
        public int AudioHighFrequencyHz { get; set; } = PluginConfiguration.DefaultAudioHighFrequencyHz;
        public int AudioLowGainPercent { get; set; } = PluginConfiguration.DefaultAudioBandGainPercent;
        public int AudioMidGainPercent { get; set; } = PluginConfiguration.DefaultAudioBandGainPercent;
        public int AudioHighGainPercent { get; set; } = PluginConfiguration.DefaultAudioBandGainPercent;
        public int AudioResponseSmoothingPercent { get; set; } = PluginConfiguration.DefaultAudioResponseSmoothingPercent;
        public int AudioBandSpreadPercent { get; set; } = PluginConfiguration.DefaultAudioBandSpreadPercent;
        public int AudioBeatPulsePercent { get; set; } = PluginConfiguration.DefaultAudioBeatPulsePercent;
        public int AudioBeatPulseDecayPercent { get; set; } = PluginConfiguration.DefaultAudioBeatPulseDecayPercent;
        public int AudioBeatPulseThresholdPercent { get; set; } = PluginConfiguration.DefaultAudioBeatPulseThresholdPercent;
        public string AudioColorPalette { get; set; } = PluginConfiguration.AudioColorPaletteSpectrum;
        public string AudioSpatialMode { get; set; } = PluginConfiguration.AudioSpatialModeSpatial;
        public string AudioChannelMode { get; set; } = PluginConfiguration.AudioChannelModeMono;
        public int TargetFps { get; set; } = 20;
        public string FrameResolution { get; set; } = PluginConfiguration.FrameResolutionStandard;
        public string VideoScalingMode { get; set; } = PluginConfiguration.VideoScalingModeStretch;
        public string VideoDeinterlaceMode { get; set; } = PluginConfiguration.VideoDeinterlaceModeOff;
        public int SamplingBreadthPercent { get; set; } = 15;
        public string SamplingMode { get; set; } = PluginConfiguration.SamplingModeAverage;
        public string SpatialOrientation { get; set; } = PluginConfiguration.SpatialOrientationNormal;
        public int ColorSmoothingPercent { get; set; } = 0;
        public bool UseGpu { get; set; } = true;
        public string CustomFfmpegFlags { get; set; } = string.Empty;
        public bool CustomFfmpegFlagsConfigured { get; set; }
        public int FfmpegStallTimeoutSeconds { get; set; } = 5;
        public bool RestoreLightState { get; set; } = true;
        public int BrightnessBoost { get; set; } = 100;
        public int RedGain { get; set; } = 100;
        public int GreenGain { get; set; } = 100;
        public int BlueGain { get; set; } = 100;
        public int ColorSaturation { get; set; } = 100;
        public int HueShiftDegrees { get; set; } = 0;
        public int OutputBrightnessPercent { get; set; } = 100;
        public double GammaCorrection { get; set; } = PluginConfiguration.DefaultGammaCorrection;
        public int ContrastPercent { get; set; } = PluginConfiguration.DefaultContrastPercent;
        public int ColorTemperatureKelvin { get; set; } = PluginConfiguration.DefaultColorTemperatureKelvin;
        public int BlackoutThreshold { get; set; } = 15;
        public string BlackoutBehavior { get; set; } = PluginConfiguration.BlackoutBehaviorBlackout;
        public int ColorChangeThreshold { get; set; } = 10;
        public int NetworkRetryAttempts { get; set; } = 3;

        public static HuePluginConfigurationSettings From(PluginConfiguration config)
        {
            return new HuePluginConfigurationSettings
            {
                SyncEnabled = config.SyncEnabled,
                PlaybackMediaFilter = config.PlaybackMediaFilter,
                HueBridgeIp = config.HueBridgeIp,
                HueAppKey = string.Empty,
                HueClientKey = string.Empty,
                HasAppKey = !string.IsNullOrWhiteSpace(config.HueAppKey),
                HasClientKey = !string.IsNullOrWhiteSpace(config.HueClientKey),
                PersistSessionHistory = config.PersistSessionHistory,
                SessionHistoryRetentionCount = config.SessionHistoryRetentionCount,
                PersistSceneScheduleHistory = config.PersistSceneScheduleHistory,
                SceneScheduleHistoryRetentionCount = config.SceneScheduleHistoryRetentionCount,
                SceneAutomationEnabled = config.SceneAutomationEnabled,
                SceneAutomationCatchUpMinutes = config.SceneAutomationCatchUpMinutes,
                SceneAutomationPlaybackPolicy = PluginConfiguration.TryNormalizeSceneAutomationPlaybackPolicy(
                    config.SceneAutomationPlaybackPolicy,
                    out var normalizedPlaybackPolicy)
                    ? normalizedPlaybackPolicy
                    : PluginConfiguration.SceneAutomationPlaybackPolicySkip,
                SceneAutomationPlaybackScope = PluginConfiguration.TryNormalizeSceneAutomationPlaybackScope(
                    config.SceneAutomationPlaybackScope,
                    out var normalizedPlaybackScope)
                    ? normalizedPlaybackScope
                    : PluginConfiguration.SceneAutomationPlaybackScopeAnyTarget,
                SceneAutomationDeferMinutes = Math.Clamp(
                    config.SceneAutomationDeferMinutes,
                    PluginConfiguration.MinSceneAutomationDeferMinutes,
                    PluginConfiguration.MaxSceneAutomationDeferMinutes),
                EntertainmentAreaId = config.EntertainmentAreaId,
                ChannelIds = config.ChannelIds,
                UseCinemaMode = config.UseCinemaMode,
                BrightnessDimLevel = config.BrightnessDimLevel,
                PauseBehavior = config.PauseBehavior,
                AudioSensitivityPercent = config.AudioSensitivityPercent,
                AudioNoiseGatePercent = config.AudioNoiseGatePercent,
                AudioLowFrequencyHz = config.AudioLowFrequencyHz,
                AudioMidFrequencyHz = config.AudioMidFrequencyHz,
                AudioHighFrequencyHz = config.AudioHighFrequencyHz,
                AudioLowGainPercent = config.AudioLowGainPercent,
                AudioMidGainPercent = config.AudioMidGainPercent,
                AudioHighGainPercent = config.AudioHighGainPercent,
                AudioResponseSmoothingPercent = config.AudioResponseSmoothingPercent,
                AudioBandSpreadPercent = config.AudioBandSpreadPercent,
                AudioBeatPulsePercent = config.AudioBeatPulsePercent,
                AudioBeatPulseDecayPercent = config.AudioBeatPulseDecayPercent,
                AudioBeatPulseThresholdPercent = config.AudioBeatPulseThresholdPercent,
                AudioColorPalette = config.AudioColorPalette,
                AudioSpatialMode = config.AudioSpatialMode,
                AudioChannelMode = config.AudioChannelMode,
                TargetFps = config.TargetFps,
                FrameResolution = config.FrameResolution,
                VideoScalingMode = config.VideoScalingMode,
                VideoDeinterlaceMode = config.VideoDeinterlaceMode,
                SamplingBreadthPercent = config.SamplingBreadthPercent,
                SamplingMode = config.SamplingMode,
                SpatialOrientation = PluginConfiguration.TryNormalizeSpatialOrientation(
                    config.SpatialOrientation,
                    out var normalizedSpatialOrientation)
                    ? normalizedSpatialOrientation
                    : PluginConfiguration.SpatialOrientationNormal,
                ColorSmoothingPercent = config.ColorSmoothingPercent,
                UseGpu = config.UseGpu,
                CustomFfmpegFlags = config.CustomFfmpegFlags,
                CustomFfmpegFlagsConfigured = !string.IsNullOrWhiteSpace(config.CustomFfmpegFlags),
                FfmpegStallTimeoutSeconds = config.FfmpegStallTimeoutSeconds,
                RestoreLightState = config.RestoreLightState,
                BrightnessBoost = config.BrightnessBoost,
                RedGain = config.RedGain,
                GreenGain = config.GreenGain,
                BlueGain = config.BlueGain,
                ColorSaturation = config.ColorSaturation,
                HueShiftDegrees = config.HueShiftDegrees,
                OutputBrightnessPercent = config.OutputBrightnessPercent,
                GammaCorrection = double.IsFinite(config.GammaCorrection) &&
                    config.GammaCorrection >= PluginConfiguration.MinGammaCorrection &&
                    config.GammaCorrection <= PluginConfiguration.MaxGammaCorrection
                    ? config.GammaCorrection
                    : PluginConfiguration.DefaultGammaCorrection,
                ContrastPercent = Math.Clamp(
                    config.ContrastPercent,
                    PluginConfiguration.MinContrastPercent,
                    PluginConfiguration.MaxContrastPercent),
                ColorTemperatureKelvin = Math.Clamp(
                    config.ColorTemperatureKelvin,
                    PluginConfiguration.MinColorTemperatureKelvin,
                    PluginConfiguration.MaxColorTemperatureKelvin),
                BlackoutThreshold = config.BlackoutThreshold,
                BlackoutBehavior = PluginConfiguration.TryNormalizeBlackoutBehavior(config.BlackoutBehavior, out var normalizedBlackoutBehavior)
                    ? normalizedBlackoutBehavior
                    : PluginConfiguration.BlackoutBehaviorBlackout,
                ColorChangeThreshold = config.ColorChangeThreshold,
                NetworkRetryAttempts = config.NetworkRetryAttempts
            };
        }

        public void ApplyTo(PluginConfiguration config)
        {
            config.SyncEnabled = SyncEnabled;
            config.PlaybackMediaFilter = PlaybackMediaFilter?.Trim() ?? PluginConfiguration.PlaybackMediaFilterAllVideo;
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
            if (SessionHistoryRetentionCount.HasValue)
                config.SessionHistoryRetentionCount = SessionHistoryRetentionCount.Value;
            if (PersistSceneScheduleHistory.HasValue)
            {
                config.PersistSceneScheduleHistory = PersistSceneScheduleHistory.Value;
                if (!config.PersistSceneScheduleHistory)
                    config.PersistedSceneScheduleHistory?.Clear();
            }
            if (SceneScheduleHistoryRetentionCount.HasValue)
                config.SceneScheduleHistoryRetentionCount = SceneScheduleHistoryRetentionCount.Value;
            if (SceneAutomationEnabled.HasValue)
                config.SceneAutomationEnabled = SceneAutomationEnabled.Value;
            config.SceneAutomationCatchUpMinutes = SceneAutomationCatchUpMinutes;
            if (!string.IsNullOrWhiteSpace(SceneAutomationPlaybackPolicy))
                config.SceneAutomationPlaybackPolicy = SceneAutomationPlaybackPolicy.Trim();
            if (!string.IsNullOrWhiteSpace(SceneAutomationPlaybackScope))
                config.SceneAutomationPlaybackScope = SceneAutomationPlaybackScope.Trim();
            if (SceneAutomationDeferMinutes.HasValue)
                config.SceneAutomationDeferMinutes = SceneAutomationDeferMinutes.Value;
            config.EntertainmentAreaId = EntertainmentAreaId?.Trim() ?? string.Empty;
            config.ChannelIds = ChannelIds?.Trim() ?? string.Empty;
            config.UseCinemaMode = UseCinemaMode;
            config.BrightnessDimLevel = BrightnessDimLevel;
            config.PauseBehavior = PauseBehavior?.Trim() ?? PluginConfiguration.PauseBehaviorKeepLastColors;
            config.AudioSensitivityPercent = AudioSensitivityPercent;
            config.AudioNoiseGatePercent = AudioNoiseGatePercent;
            config.AudioLowFrequencyHz = AudioLowFrequencyHz;
            config.AudioMidFrequencyHz = AudioMidFrequencyHz;
            config.AudioHighFrequencyHz = AudioHighFrequencyHz;
            config.AudioLowGainPercent = AudioLowGainPercent;
            config.AudioMidGainPercent = AudioMidGainPercent;
            config.AudioHighGainPercent = AudioHighGainPercent;
            config.AudioResponseSmoothingPercent = AudioResponseSmoothingPercent;
            config.AudioBandSpreadPercent = AudioBandSpreadPercent;
            config.AudioBeatPulsePercent = AudioBeatPulsePercent;
            config.AudioBeatPulseDecayPercent = AudioBeatPulseDecayPercent;
            config.AudioBeatPulseThresholdPercent = AudioBeatPulseThresholdPercent;
            config.AudioColorPalette = AudioColorPalette?.Trim() ?? PluginConfiguration.AudioColorPaletteSpectrum;
            config.AudioSpatialMode = AudioSpatialMode?.Trim() ?? PluginConfiguration.AudioSpatialModeSpatial;
            config.AudioChannelMode = AudioChannelMode?.Trim() ?? PluginConfiguration.AudioChannelModeMono;
            config.TargetFps = TargetFps;
            config.FrameResolution = FrameResolution?.Trim() ?? PluginConfiguration.FrameResolutionStandard;
            config.VideoScalingMode = VideoScalingMode?.Trim() ?? PluginConfiguration.VideoScalingModeStretch;
            config.VideoDeinterlaceMode = VideoDeinterlaceMode?.Trim() ?? PluginConfiguration.VideoDeinterlaceModeOff;
            config.SamplingBreadthPercent = SamplingBreadthPercent;
            config.SamplingMode = SamplingMode?.Trim() ?? PluginConfiguration.SamplingModeAverage;
            config.SpatialOrientation = SpatialOrientation?.Trim() ?? PluginConfiguration.SpatialOrientationNormal;
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
            config.GammaCorrection = GammaCorrection;
            config.ContrastPercent = ContrastPercent;
            config.ColorTemperatureKelvin = ColorTemperatureKelvin;
            config.BlackoutThreshold = BlackoutThreshold;
            config.BlackoutBehavior = BlackoutBehavior?.Trim() ?? PluginConfiguration.BlackoutBehaviorBlackout;
            config.ColorChangeThreshold = ColorChangeThreshold;
            config.NetworkRetryAttempts = NetworkRetryAttempts;
        }
    }

    /// <summary>Credential-safe representation of one automatic playback device target.</summary>
    public sealed class UserDeviceBridgeTargetSummary
    {
        public string DeviceId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string HueBridgeIp { get; set; } = string.Empty;
        public string EntertainmentAreaId { get; set; } = string.Empty;
        public string EntertainmentAreaName { get; set; } = string.Empty;
        public bool HasAppKey { get; set; }
        public bool HasClientKey { get; set; }
        public string? ChannelIdsOverride { get; set; }

        public static UserDeviceBridgeTargetSummary From(UserDeviceBridgeTarget target)
        {
            return new UserDeviceBridgeTargetSummary
            {
                DeviceId = target.DeviceId,
                DeviceName = target.DeviceName,
                HueBridgeIp = target.HueBridgeIp,
                EntertainmentAreaId = target.EntertainmentAreaId,
                EntertainmentAreaName = target.EntertainmentAreaName,
                HasAppKey = !string.IsNullOrWhiteSpace(target.HueAppKey),
                HasClientKey = !string.IsNullOrWhiteSpace(target.HueClientKey),
                ChannelIdsOverride = target.ChannelIdsOverride
            };
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
        public IReadOnlyList<UserDeviceBridgeTargetSummary> DeviceTargets { get; set; } = Array.Empty<UserDeviceBridgeTargetSummary>();
        public bool? UseCinemaModeOverride { get; set; }
        public int? BrightnessDimLevelOverride { get; set; }
        public string? PauseBehaviorOverride { get; set; }
        public bool? RestoreLightStateOverride { get; set; }
        public string? PlaybackMediaFilterOverride { get; set; }
        public int? AudioSensitivityPercentOverride { get; set; }
        public int? AudioNoiseGatePercentOverride { get; set; }
        public int? AudioLowFrequencyHzOverride { get; set; }
        public int? AudioMidFrequencyHzOverride { get; set; }
        public int? AudioHighFrequencyHzOverride { get; set; }
        public int? AudioLowGainPercentOverride { get; set; }
        public int? AudioMidGainPercentOverride { get; set; }
        public int? AudioHighGainPercentOverride { get; set; }
        public int? AudioResponseSmoothingPercentOverride { get; set; }
        public int? AudioBandSpreadPercentOverride { get; set; }
        public int? AudioBeatPulsePercentOverride { get; set; }
        public int? AudioBeatPulseDecayPercentOverride { get; set; }
        public int? AudioBeatPulseThresholdPercentOverride { get; set; }
        public string? AudioColorPaletteOverride { get; set; }
        public string? AudioSpatialModeOverride { get; set; }
        public string? AudioChannelModeOverride { get; set; }
        public int? BrightnessBoostOverride { get; set; }
        public int? RedGainOverride { get; set; }
        public int? GreenGainOverride { get; set; }
        public int? BlueGainOverride { get; set; }
        public int? ColorSaturationOverride { get; set; }
        public int? HueShiftDegreesOverride { get; set; }
        public int? OutputBrightnessPercentOverride { get; set; }
        public double? GammaCorrectionOverride { get; set; }
        public int? ContrastPercentOverride { get; set; }
        public int? ColorTemperatureKelvinOverride { get; set; }
        public int? BlackoutThresholdOverride { get; set; }
        public string? BlackoutBehaviorOverride { get; set; }
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
        public string? SpatialOrientationOverride { get; set; }
        public int? ColorSmoothingPercentOverride { get; set; }
        public bool CustomFfmpegFlagsConfigured { get; set; }

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
                DeviceTargets = (mapping.DeviceTargets ?? new List<UserDeviceBridgeTarget>())
                    .Where(target => target != null)
                    .Select(UserDeviceBridgeTargetSummary.From)
                    .ToArray(),
                UseCinemaModeOverride = mapping.UseCinemaModeOverride,
                BrightnessDimLevelOverride = mapping.BrightnessDimLevelOverride,
                PauseBehaviorOverride = mapping.PauseBehaviorOverride,
                RestoreLightStateOverride = mapping.RestoreLightStateOverride,
                PlaybackMediaFilterOverride = mapping.PlaybackMediaFilterOverride,
                AudioSensitivityPercentOverride = mapping.AudioSensitivityPercentOverride,
                AudioNoiseGatePercentOverride = mapping.AudioNoiseGatePercentOverride,
                AudioLowFrequencyHzOverride = mapping.AudioLowFrequencyHzOverride,
                AudioMidFrequencyHzOverride = mapping.AudioMidFrequencyHzOverride,
                AudioHighFrequencyHzOverride = mapping.AudioHighFrequencyHzOverride,
                AudioLowGainPercentOverride = mapping.AudioLowGainPercentOverride,
                AudioMidGainPercentOverride = mapping.AudioMidGainPercentOverride,
                AudioHighGainPercentOverride = mapping.AudioHighGainPercentOverride,
                AudioResponseSmoothingPercentOverride = mapping.AudioResponseSmoothingPercentOverride,
                AudioBandSpreadPercentOverride = mapping.AudioBandSpreadPercentOverride,
                AudioBeatPulsePercentOverride = mapping.AudioBeatPulsePercentOverride,
                AudioBeatPulseDecayPercentOverride = mapping.AudioBeatPulseDecayPercentOverride,
                AudioBeatPulseThresholdPercentOverride = mapping.AudioBeatPulseThresholdPercentOverride,
                AudioColorPaletteOverride = mapping.AudioColorPaletteOverride,
                AudioSpatialModeOverride = mapping.AudioSpatialModeOverride,
                AudioChannelModeOverride = mapping.AudioChannelModeOverride,
                BrightnessBoostOverride = mapping.BrightnessBoostOverride,
                RedGainOverride = mapping.RedGainOverride,
                GreenGainOverride = mapping.GreenGainOverride,
                BlueGainOverride = mapping.BlueGainOverride,
                ColorSaturationOverride = mapping.ColorSaturationOverride,
                HueShiftDegreesOverride = mapping.HueShiftDegreesOverride,
                OutputBrightnessPercentOverride = mapping.OutputBrightnessPercentOverride,
                GammaCorrectionOverride = mapping.GammaCorrectionOverride,
                ContrastPercentOverride = mapping.ContrastPercentOverride,
                ColorTemperatureKelvinOverride = mapping.ColorTemperatureKelvinOverride,
                BlackoutThresholdOverride = mapping.BlackoutThresholdOverride,
                BlackoutBehaviorOverride = mapping.BlackoutBehaviorOverride,
                ColorChangeThresholdOverride = mapping.ColorChangeThresholdOverride,
                UseGpuOverride = mapping.UseGpuOverride,
                CustomFfmpegFlagsOverride = mapping.CustomFfmpegFlagsOverride,
                CustomFfmpegFlagsConfigured = !string.IsNullOrWhiteSpace(mapping.CustomFfmpegFlagsOverride),
                FfmpegStallTimeoutSecondsOverride = mapping.FfmpegStallTimeoutSecondsOverride,
                NetworkRetryAttemptsOverride = mapping.NetworkRetryAttemptsOverride,
                ChannelIdsOverride = mapping.ChannelIdsOverride,
                TargetFpsOverride = mapping.TargetFpsOverride,
                FrameResolutionOverride = mapping.FrameResolutionOverride,
                VideoScalingModeOverride = mapping.VideoScalingModeOverride,
                VideoDeinterlaceModeOverride = mapping.VideoDeinterlaceModeOverride,
                SamplingBreadthPercentOverride = mapping.SamplingBreadthPercentOverride,
                SamplingModeOverride = mapping.SamplingModeOverride,
                SpatialOrientationOverride = mapping.SpatialOrientationOverride,
                ColorSmoothingPercentOverride = mapping.ColorSmoothingPercentOverride
            };
        }

        public static UserBridgeMappingSummary ForSupport(UserBridgeMapping mapping)
        {
            var summary = From(mapping);
            summary.CustomFfmpegFlagsOverride = null;
            return summary;
        }
    }

    /// <summary>
    /// Credential-free dependency summary for one per-user bridge mapping.
    /// </summary>
    public sealed class HueUserMappingDependenciesResult
    {
        [JsonPropertyName("userId")]
        public string UserId { get; init; } = string.Empty;

        [JsonPropertyName("userName")]
        public string UserName { get; init; } = string.Empty;

        [JsonPropertyName("syncEnabled")]
        public bool SyncEnabled { get; init; }

        [JsonPropertyName("canDisable")]
        public bool CanDisable { get; init; }

        [JsonPropertyName("canDelete")]
        public bool CanDelete { get; init; }

        [JsonPropertyName("scheduledCueCount")]
        public int ScheduledCueCount { get; init; }

        [JsonPropertyName("scheduledCues")]
        public IReadOnlyList<HueUserMappingScheduleDependencyResult> ScheduledCues { get; init; } =
            Array.Empty<HueUserMappingScheduleDependencyResult>();

        [JsonPropertyName("scenePlaylistCount")]
        public int ScenePlaylistCount { get; init; }

        [JsonPropertyName("scenePlaylists")]
        public IReadOnlyList<HueUserMappingPlaylistDependencyResult> ScenePlaylists { get; init; } =
            Array.Empty<HueUserMappingPlaylistDependencyResult>();
    }

    /// <summary>
    /// One credential-free scheduled-cue reference to a per-user bridge mapping.
    /// </summary>
    public sealed class HueUserMappingScheduleDependencyResult
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("enabled")]
        public bool Enabled { get; init; }
    }

    /// <summary>
    /// One credential-free saved-playlist reference to a per-user bridge mapping.
    /// </summary>
    public sealed class HueUserMappingPlaylistDependencyResult
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;
    }

    /// <summary>
    /// Request shape for atomically deleting several per-user bridge mappings by user ID.
    /// </summary>
    public sealed class HueUserMappingBulkDeleteRequest
    {
        [JsonPropertyName("userIds")]
        public List<string> UserIds { get; set; } = new();
    }

    /// <summary>
    /// Credential-free result for an atomic per-user mapping deletion operation.
    /// </summary>
    public sealed class HueUserMappingBulkDeleteResult
    {
        [JsonPropertyName("requestedCount")]
        public int RequestedCount { get; set; }

        [JsonPropertyName("deletedCount")]
        public int DeletedCount { get; set; }

        [JsonPropertyName("remainingCount")]
        public int RemainingCount { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("mappings")]
        public IReadOnlyList<UserBridgeMappingSummary> Mappings { get; set; } = Array.Empty<UserBridgeMappingSummary>();

        [JsonPropertyName("missingUserIds")]
        public IReadOnlyList<string> MissingUserIds { get; set; } = Array.Empty<string>();

        [JsonPropertyName("blockedMappings")]
        public IReadOnlyList<HueUserMappingDependenciesResult> BlockedMappings { get; set; } =
            Array.Empty<HueUserMappingDependenciesResult>();
    }

    /// <summary>
    /// Request shape for atomically enabling or disabling several per-user bridge mappings.
    /// </summary>
    public sealed class HueUserMappingBulkEnabledRequest
    {
        [JsonPropertyName("userIds")]
        public List<string> UserIds { get; set; } = new();

        [JsonPropertyName("syncEnabled")]
        public bool SyncEnabled { get; set; }
    }

    /// <summary>
    /// Credential-free result for an atomic per-user mapping enabled-state operation.
    /// </summary>
    public sealed class HueUserMappingBulkEnabledResult
    {
        [JsonPropertyName("syncEnabled")]
        public bool SyncEnabled { get; set; }

        [JsonPropertyName("requestedCount")]
        public int RequestedCount { get; set; }

        [JsonPropertyName("updatedCount")]
        public int UpdatedCount { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("mappings")]
        public IReadOnlyList<UserBridgeMappingSummary> Mappings { get; set; } = Array.Empty<UserBridgeMappingSummary>();

        [JsonPropertyName("missingUserIds")]
        public IReadOnlyList<string> MissingUserIds { get; set; } = Array.Empty<string>();

        [JsonPropertyName("invalidUserIds")]
        public IReadOnlyList<string> InvalidUserIds { get; set; } = Array.Empty<string>();

        [JsonPropertyName("blockedMappings")]
        public IReadOnlyList<HueUserMappingDependenciesResult> BlockedMappings { get; set; } =
            Array.Empty<HueUserMappingDependenciesResult>();
    }

    /// <summary>
    /// Import-only mapping shape. Export documents use <see cref="UserBridgeMappingSummary"/>
    /// so stored credentials are never serialized, while an administrator may explicitly
    /// provide replacement keys in an import request. UserId must be a Jellyfin user GUID;
    /// accepted brace/N-format values are normalized to canonical D-format text before
    /// merge, duplicate detection, credential preservation, and persistence.
    /// </summary>
    public sealed class UserBridgeMappingImport : UserBridgeMappingSummary
    {
        public string HueAppKey { get; set; } = string.Empty;
        public string HueClientKey { get; set; } = string.Empty;
        /// <summary>
        /// Optional replacement credentials for exported device targets. Export documents
        /// never populate this collection; same-server imports preserve matching stored keys.
        /// </summary>
        public List<UserDeviceBridgeTargetImport> DeviceTargetCredentials { get; set; } = new();
    }

    /// <summary>Explicit replacement credentials for one imported device target.</summary>
    public sealed class UserDeviceBridgeTargetImport
    {
        public string DeviceId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string HueBridgeIp { get; set; } = string.Empty;
        public string HueAppKey { get; set; } = string.Empty;
        public string HueClientKey { get; set; } = string.Empty;
        public string EntertainmentAreaId { get; set; } = string.Empty;
        public string EntertainmentAreaName { get; set; } = string.Empty;
        public string? ChannelIdsOverride { get; set; }
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
        public string CredentialNote { get; set; } = "Bridge credential values, including nested automatic playback device-route keys, are omitted. Custom FFmpeg flags are retained for migration and may contain sensitive paths or URLs; review before sharing. Re-enter replacement keys in the import document's DeviceTargetCredentials when importing to a new server; existing matching keys are preserved.";
        public HuePluginConfigurationSettings Configuration { get; set; } = new();
        public IReadOnlyList<UserBridgeMappingSummary> UserMappings { get; set; } = Array.Empty<UserBridgeMappingSummary>();
        public IReadOnlyList<HueColorPresetResult> ColorPresets { get; set; } = Array.Empty<HueColorPresetResult>();
        public IReadOnlyList<HueScenePlaylistResult> ScenePlaylists { get; set; } = Array.Empty<HueScenePlaylistResult>();
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
                ScenePlaylists = (config.ScenePlaylists ?? new List<HueScenePlaylist>())
                    .Where(playlist => playlist != null)
                    .Select(playlist => HueApiController.ToScenePlaylistResult(playlist, config))
                    .ToArray(),
                SceneSchedules = (config.SceneSchedules ?? new List<HueSceneSchedule>())
                    .Where(schedule => schedule != null)
                    .Select(schedule => HueApiController.ToSceneScheduleResult(schedule, config))
                    .ToArray()
            };
        }

        public static HueConfigurationExportDocument ForSupport(PluginConfiguration config)
        {
            var export = From(config);
            export.CredentialNote =
                "Credential values and custom FFmpeg flags are omitted from support bundles. Review private labels and metadata before sharing.";
            export.Configuration.CustomFfmpegFlags = string.Empty;
            export.Configuration.CustomFfmpegFlagsConfigured = !string.IsNullOrWhiteSpace(config.CustomFfmpegFlags);
            export.UserMappings = (config.UserMappings ?? new List<UserBridgeMapping>())
                .Where(mapping => mapping != null)
                .Select(UserBridgeMappingSummary.ForSupport)
                .ToArray();
            return export;
        }
    }

    /// <summary>
    /// Request shape accepted by the configuration import endpoint. It is intentionally
    /// compatible with the export document while allowing explicit replacement keys.
    /// Imported mapping IDs are validated as Jellyfin user GUIDs and normalized to
    /// canonical D-format text before the candidate is merged or persisted.
    /// </summary>
    public sealed class HueConfigurationImportRequest
    {
        public int SchemaVersion { get; set; } = HueConfigurationExportDocument.CurrentSchemaVersion;
        public HuePluginConfigurationSettings? Configuration { get; set; }
        public List<UserBridgeMappingImport> UserMappings { get; set; } = new();
        public List<HueColorPresetRequest> ColorPresets { get; set; } = new();
        public List<HueScenePlaylistRequest> ScenePlaylists { get; set; } = new();
        public List<HueSceneScheduleRequest> SceneSchedules { get; set; } = new();
        public bool ReplaceMappings { get; set; } = true;
        public bool ReplaceColorPresets { get; set; } = true;
        public bool ReplaceScenePlaylists { get; set; } = true;
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
        public int ScenePlaylistsImported { get; set; }
        public int SceneSchedulesImported { get; set; }
        public int TotalMappings { get; set; }
        public int TotalColorPresets { get; set; }
        public int TotalScenePlaylists { get; set; }
        public int TotalSceneSchedules { get; set; }
        public bool GlobalAppKeyPreserved { get; set; }
        public bool GlobalClientKeyPreserved { get; set; }
        public int MappingCredentialPairsPreserved { get; set; }
    }

    /// <summary>
    /// Credential-safe change summary for an import candidate. Counts are calculated after
    /// the same normalization, merge, credential-preservation, and reference migration rules
    /// used by the atomic import endpoint; bridge key values are never returned.
    /// </summary>
    public sealed class HueConfigurationImportDiff
    {
        public bool HasChanges { get; set; }
        public bool GlobalSettingsChanged { get; set; }
        public bool GlobalAppKeyChanged { get; set; }
        public bool GlobalClientKeyChanged { get; set; }
        public HueConfigurationImportCollectionDiff Mappings { get; set; } = new();
        public HueConfigurationImportCollectionDiff ColorPresets { get; set; } = new();
        public HueConfigurationImportCollectionDiff ScenePlaylists { get; set; } = new();
        public HueConfigurationImportCollectionDiff SceneSchedules { get; set; } = new();
    }

    /// <summary>
    /// Added, removed, changed, and unchanged counts for one imported object collection.
    /// </summary>
    public sealed class HueConfigurationImportCollectionDiff
    {
        public int Added { get; set; }
        public int Removed { get; set; }
        public int Changed { get; set; }
        public int Unchanged { get; set; }

        public bool HasChanges => Added > 0 || Removed > 0 || Changed > 0;
    }

    /// <summary>
    /// Credential-safe preflight report for a configuration import. It never contains
    /// bridge keys or persisted telemetry and does not mutate the live configuration.
    /// </summary>
    public sealed class HueConfigurationImportValidationResult
    {
        public string Message { get; set; } = string.Empty;
        public bool Valid { get; set; }
        public bool CanImport { get; set; }
        public bool ActivePlayback { get; set; }
        public bool ActiveDiagnostic { get; set; }
        public bool ActiveConfigurationMutation { get; set; }
        public bool ActiveScheduledCue { get; set; }
        public bool ActiveScheduleEvaluation { get; set; }
        public bool ActiveScheduleLifecycle { get; set; }
        public int SchemaVersion { get; set; }
        public IReadOnlyList<string> ValidationErrors { get; set; } = Array.Empty<string>();
        public int MappingsImported { get; set; }
        public int ColorPresetsImported { get; set; }
        public int ScenePlaylistsImported { get; set; }
        public int SceneSchedulesImported { get; set; }
        public int TotalMappings { get; set; }
        public int TotalColorPresets { get; set; }
        public int TotalScenePlaylists { get; set; }
        public int TotalSceneSchedules { get; set; }
        public bool GlobalAppKeyPreserved { get; set; }
        public bool GlobalClientKeyPreserved { get; set; }
        public int MappingCredentialPairsPreserved { get; set; }
        public HueConfigurationImportDiff Diff { get; set; } = new();
    }

    /// <summary>
    /// Write-only request contract for a per-user mapping. The persisted mapping types
    /// mark bridge credentials with <see cref="JsonIgnoreAttribute"/> so their generic
    /// JSON representation is safe to return; using those types directly for this POST
    /// would also discard credentials sent by the configuration page. Unknown fields are
    /// retained here so the non-secret mapping profile stays in one canonical model.
    /// </summary>
    public sealed class HueUserMappingRequest
    {
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Values { get; set; }

        internal UserBridgeMapping ToConfigurationMapping()
        {
            var values = Values ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            var mapping = JsonSerializer.Deserialize<UserBridgeMapping>(
                JsonSerializer.Serialize(values),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new UserBridgeMapping();

            mapping.HueAppKey = ReadString(values, "HueAppKey") ?? string.Empty;
            mapping.HueClientKey = ReadString(values, "HueClientKey") ?? string.Empty;

            if (TryGetValue(values, "DeviceTargets", out var deviceTargets) &&
                deviceTargets.ValueKind == JsonValueKind.Array)
            {
                mapping.DeviceTargets = new List<UserDeviceBridgeTarget>();
                foreach (var deviceTargetElement in deviceTargets.EnumerateArray())
                {
                    if (deviceTargetElement.ValueKind == JsonValueKind.Null)
                    {
                        mapping.DeviceTargets.Add(null!);
                        continue;
                    }

                    var deviceTarget = JsonSerializer.Deserialize<UserDeviceBridgeTarget>(
                        deviceTargetElement.GetRawText(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new UserDeviceBridgeTarget();
                    deviceTarget.HueAppKey = ReadString(deviceTargetElement, "HueAppKey") ?? string.Empty;
                    deviceTarget.HueClientKey = ReadString(deviceTargetElement, "HueClientKey") ?? string.Empty;
                    mapping.DeviceTargets.Add(deviceTarget);
                }
            }

            return mapping;
        }

        private static string? ReadString(
            IReadOnlyDictionary<string, JsonElement> values,
            string propertyName)
        {
            return TryGetValue(values, propertyName, out var value)
                ? ReadString(value)
                : null;
        }

        private static string? ReadString(JsonElement value)
            => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                ? null
                : value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : value.ToString();

        private static string? ReadString(JsonElement value, string propertyName)
        {
            if (value.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var property in value.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    return ReadString(property.Value);
            }

            return null;
        }

        private static bool TryGetValue(
            IReadOnlyDictionary<string, JsonElement> values,
            string propertyName,
            out JsonElement value)
        {
            foreach (var pair in values)
            {
                if (string.Equals(pair.Key, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }

            value = default;
            return false;
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

        [JsonPropertyName("deviceId")]
        public string? DeviceId { get; set; }

        [JsonPropertyName("ipAddress")]
        public string IpAddress { get; set; } = string.Empty;

        [JsonPropertyName("appKey")]
        public string AppKey { get; set; } = string.Empty;
    }

    public class HueEntertainmentChannelsRequest
    {
        [JsonPropertyName("userId")]
        public string? UserId { get; set; }

        [JsonPropertyName("deviceId")]
        public string? DeviceId { get; set; }

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

        [JsonPropertyName("deviceId")]
        public string? DeviceId { get; set; }

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

    /// <summary>
    /// Credential-free playback-device identity returned to the elevated configuration
    /// page. The exact device ID is safe to persist as a route key; bridge keys and
    /// playback item metadata are deliberately excluded.
    /// </summary>
    public sealed class HuePlaybackDeviceSummary
    {
        [JsonPropertyName("userId")]
        public string UserId { get; init; } = string.Empty;

        [JsonPropertyName("userName")]
        public string UserName { get; init; } = string.Empty;

        [JsonPropertyName("deviceId")]
        public string DeviceId { get; init; } = string.Empty;

        [JsonPropertyName("deviceName")]
        public string DeviceName { get; init; } = string.Empty;

        [JsonPropertyName("client")]
        public string Client { get; init; } = string.Empty;

        [JsonPropertyName("deviceType")]
        public string DeviceType { get; init; } = string.Empty;

        [JsonPropertyName("applicationVersion")]
        public string ApplicationVersion { get; init; } = string.Empty;

        [JsonPropertyName("isActive")]
        public bool IsActive { get; init; }

        [JsonPropertyName("lastActivityDate")]
        public DateTime LastActivityDate { get; init; }
    }

    /// <summary>
    /// Selects one persisted target for a credential-free current-light capture.
    /// An empty user ID means the configured default bridge target.
    /// </summary>
    public sealed class HueCurrentLightColorRequest
    {
        [JsonPropertyName("targetUserId")]
        public string? TargetUserId { get; set; }

        [JsonPropertyName("targetDeviceId")]
        public string? TargetDeviceId { get; set; }
    }

    /// <summary>
    /// Identifies either a user-level target or one explicit device route belonging
    /// to that user. Device IDs are persisted route identifiers, never credentials.
    /// </summary>
    public sealed class HueCurrentLightColorTargetRoute
    {
        [JsonPropertyName("userId")]
        public string UserId { get; set; } = string.Empty;

        [JsonPropertyName("deviceId")]
        public string? DeviceId { get; set; }
    }

    /// <summary>
    /// Selects multiple persisted targets for credential-free current-light capture.
    /// All-target selection is exclusive; otherwise the default bridge, enabled user
    /// mapping IDs, and explicit per-user device routes can be selected explicitly.
    /// </summary>
    public sealed class HueCurrentLightColorBatchRequest
    {
        [JsonPropertyName("targetAllEnabledMappings")]
        public bool TargetAllEnabledMappings { get; set; }

        [JsonPropertyName("targetUserIds")]
        public List<string>? TargetUserIds { get; set; }

        [JsonPropertyName("targetRoutes")]
        public List<HueCurrentLightColorTargetRoute>? TargetRoutes { get; set; }

        [JsonPropertyName("includeDefaultTarget")]
        public bool? IncludeDefaultTarget { get; set; }
    }

    public class HuePreviewRequest
    {
        [JsonPropertyName("userId")]
        public string? UserId { get; set; }

        [JsonPropertyName("deviceId")]
        public string? DeviceId { get; set; }

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

        [JsonPropertyName("transitionCurve")]
        public string TransitionCurve { get; set; } = PluginConfiguration.ColorPresetTransitionCurveLinear;

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

        [JsonPropertyName("targetAllEnabledMappings")]
        public bool TargetAllEnabledMappings { get; set; }

        [JsonPropertyName("targetUserIds")]
        public List<string>? TargetUserIds { get; set; }

        [JsonPropertyName("targetRoutes")]
        public List<HueCurrentLightColorTargetRoute>? TargetRoutes { get; set; }

        [JsonPropertyName("includeDefaultTarget")]
        public bool? IncludeDefaultTarget { get; set; }
    }

    /// <summary>
    /// Credential-free request for previewing a saved scene against the default target,
    /// one enabled user mapping, a selected subset of enabled mappings (optionally including
    /// the default bridge), or every distinct enabled target.
    /// </summary>
    public sealed class HueSavedColorPresetPreviewRequest
    {
        [JsonPropertyName("targetUserId")]
        public string? TargetUserId { get; set; }

        [JsonPropertyName("targetAllEnabledMappings")]
        public bool TargetAllEnabledMappings { get; set; }

        [JsonPropertyName("targetUserIds")]
        public List<string>? TargetUserIds { get; set; }

        [JsonPropertyName("targetRoutes")]
        public List<HueCurrentLightColorTargetRoute>? TargetRoutes { get; set; }

        [JsonPropertyName("includeDefaultTarget")]
        public bool? IncludeDefaultTarget { get; set; }
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

    /// <summary>
    /// Credential-safe current-light color capture. Counts make partial bridge reads
    /// visible to the administrator without returning light IDs or bridge secrets.
    /// </summary>
    public sealed class HueCurrentLightColorResult
    {
        [JsonPropertyName("succeeded")]
        public bool Succeeded { get; init; }

        [JsonPropertyName("message")]
        public string Message { get; init; } = string.Empty;

        [JsonPropertyName("targetLabel")]
        public string TargetLabel { get; init; } = string.Empty;

        [JsonPropertyName("targetUserId")]
        public string? TargetUserId { get; init; }

        [JsonPropertyName("targetDeviceId")]
        public string? TargetDeviceId { get; init; }

        [JsonPropertyName("targetDeviceName")]
        public string? TargetDeviceName { get; init; }

        [JsonPropertyName("red")]
        public int Red { get; init; }

        [JsonPropertyName("green")]
        public int Green { get; init; }

        [JsonPropertyName("blue")]
        public int Blue { get; init; }

        [JsonPropertyName("brightnessPercent")]
        public int BrightnessPercent { get; init; }

        [JsonPropertyName("capturedLightCount")]
        public int CapturedLightCount { get; init; }

        [JsonPropertyName("attemptedLightCount")]
        public int AttemptedLightCount { get; init; }

        [JsonPropertyName("sampledLightCount")]
        public int SampledLightCount { get; init; }

        [JsonPropertyName("channelProfileCount")]
        public int ChannelProfileCount { get; init; }
    }

    /// <summary>
    /// Aggregate and per-target results for a multi-room current-light capture. The
    /// aggregate RGB/brightness fields are weighted from successful light samples and
    /// can seed the administrator scene editor without returning bridge credentials.
    /// </summary>
    public sealed class HueCurrentLightColorBatchResult
    {
        [JsonPropertyName("succeeded")]
        public bool Succeeded { get; init; }

        [JsonPropertyName("message")]
        public string Message { get; init; } = string.Empty;

        [JsonPropertyName("targetAllEnabledMappings")]
        public bool TargetAllEnabledMappings { get; init; }

        [JsonPropertyName("targetUserIds")]
        public IReadOnlyList<string> TargetUserIds { get; init; } = Array.Empty<string>();

        [JsonPropertyName("targetRoutes")]
        public IReadOnlyList<HueCurrentLightColorTargetRoute> TargetRoutes { get; init; } = Array.Empty<HueCurrentLightColorTargetRoute>();

        [JsonPropertyName("includeDefaultTarget")]
        public bool IncludeDefaultTarget { get; init; }

        [JsonPropertyName("attemptedTargetCount")]
        public int AttemptedTargetCount { get; init; }

        [JsonPropertyName("successfulTargetCount")]
        public int SuccessfulTargetCount { get; init; }

        [JsonPropertyName("red")]
        public int Red { get; init; }

        [JsonPropertyName("green")]
        public int Green { get; init; }

        [JsonPropertyName("blue")]
        public int Blue { get; init; }

        [JsonPropertyName("brightnessPercent")]
        public int BrightnessPercent { get; init; }

        [JsonPropertyName("sampledLightCount")]
        public int SampledLightCount { get; init; }

        [JsonPropertyName("captures")]
        public IReadOnlyList<HueCurrentLightColorResult> Captures { get; init; } = Array.Empty<HueCurrentLightColorResult>();

        [JsonPropertyName("capturedAtUtc")]
        public DateTime CapturedAtUtc { get; init; }
    }

    public class HuePreviewResult
    {
        [JsonPropertyName("succeeded")]
        public bool Succeeded { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("cleanupWarning")]
        public string? CleanupWarning { get; set; }

        [JsonPropertyName("targetAllEnabledMappings")]
        public bool TargetAllEnabledMappings { get; set; }

        [JsonPropertyName("targetUserIds")]
        public IReadOnlyList<string> TargetUserIds { get; set; } = Array.Empty<string>();

        [JsonPropertyName("targetRoutes")]
        public IReadOnlyList<HueCurrentLightColorTargetRoute> TargetRoutes { get; set; } = Array.Empty<HueCurrentLightColorTargetRoute>();

        [JsonPropertyName("includeDefaultTarget")]
        public bool IncludeDefaultTarget { get; set; }

        [JsonPropertyName("targetResults")]
        public IReadOnlyList<HuePreviewTargetResult> TargetResults { get; set; } = Array.Empty<HuePreviewTargetResult>();

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

        [JsonPropertyName("transitionCurve")]
        public string TransitionCurve { get; set; } = PluginConfiguration.ColorPresetTransitionCurveLinear;

        [JsonPropertyName("availableChannelCount")]
        public int AvailableChannelCount { get; set; }

        [JsonPropertyName("selectedChannelCount")]
        public int SelectedChannelCount { get; set; }
    }

    /// <summary>
    /// Request shape for previewing several saved scenes sequentially without exposing
    /// bridge credentials or changing persisted configuration.
    /// </summary>
    public sealed class HueColorPresetBulkPreviewRequest
    {
        [JsonPropertyName("presetNames")]
        public List<string> PresetNames { get; set; } = new();

        [JsonPropertyName("targetUserId")]
        public string? TargetUserId { get; set; }

        [JsonPropertyName("targetAllEnabledMappings")]
        public bool TargetAllEnabledMappings { get; set; }

        [JsonPropertyName("targetUserIds")]
        public List<string>? TargetUserIds { get; set; }

        [JsonPropertyName("targetRoutes")]
        public List<HueCurrentLightColorTargetRoute>? TargetRoutes { get; set; }

        [JsonPropertyName("includeDefaultTarget")]
        public bool? IncludeDefaultTarget { get; set; }
    }

    /// <summary>
    /// Credential-free aggregate result for a sequential saved-scene preview operation.
    /// </summary>
    public sealed class HueColorPresetBulkPreviewResult
    {
        [JsonPropertyName("requestedCount")]
        public int RequestedCount { get; set; }

        [JsonPropertyName("completedCount")]
        public int CompletedCount { get; set; }

        [JsonPropertyName("succeededCount")]
        public int SucceededCount { get; set; }

        [JsonPropertyName("failedCount")]
        public int FailedCount { get; set; }

        [JsonPropertyName("canceled")]
        public bool Canceled { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("previews")]
        public IReadOnlyList<HueColorPresetBulkPreviewItem> Previews { get; set; } = Array.Empty<HueColorPresetBulkPreviewItem>();

        [JsonPropertyName("missingNames")]
        public IReadOnlyList<string> MissingNames { get; set; } = Array.Empty<string>();

        [JsonPropertyName("validationErrors")]
        public IReadOnlyList<string> ValidationErrors { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// One saved-scene preview outcome in a bulk operation.
    /// </summary>
    public sealed class HueColorPresetBulkPreviewItem
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("preview")]
        public HuePreviewResult Preview { get; set; } = new();
    }

    /// <summary>
    /// Credential-free outcome for one immediate preview target.
    /// </summary>
    public sealed class HuePreviewTargetResult
    {
        [JsonPropertyName("targetLabel")]
        public string TargetLabel { get; set; } = string.Empty;

        [JsonPropertyName("succeeded")]
        public bool Succeeded { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("cleanupWarning")]
        public string? CleanupWarning { get; set; }

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

        [JsonPropertyName("transitionCurve")]
        public string TransitionCurve { get; set; } = PluginConfiguration.ColorPresetTransitionCurveLinear;

        public HueColorPreset ToConfigurationPreset()
        {
            var normalizedEffect = PluginConfiguration.TryNormalizeColorPresetEffect(Effect, out var effect)
                ? effect
                : Effect?.Trim() ?? string.Empty;
            var normalizedTransitionCurve = PluginConfiguration.TryNormalizeColorPresetTransitionCurve(TransitionCurve, out var transitionCurve)
                ? transitionCurve
                : TransitionCurve?.Trim() ?? string.Empty;
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
                TransitionOutSeconds = TransitionOutSeconds,
                TransitionCurve = normalizedTransitionCurve
            };
        }
    }

    /// <summary>
    /// Credential-free request to rename a saved scene and migrate its references.
    /// </summary>
    public sealed class HueColorPresetRenameRequest
    {
        [JsonPropertyName("newName")]
        public string NewName { get; set; } = string.Empty;
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

        [JsonPropertyName("transitionCurve")]
        public string TransitionCurve { get; set; } = PluginConfiguration.ColorPresetTransitionCurveLinear;
    }

    /// <summary>
    /// Request shape for atomically duplicating several saved scenes by normalized name.
    /// </summary>
    public sealed class HueColorPresetBulkDuplicateRequest
    {
        [JsonPropertyName("presetNames")]
        public List<string> PresetNames { get; set; } = new();
    }

    /// <summary>
    /// Credential-free result for an atomic saved-scene duplication operation.
    /// </summary>
    public sealed class HueColorPresetBulkDuplicateResult
    {
        [JsonPropertyName("requestedCount")]
        public int RequestedCount { get; set; }

        [JsonPropertyName("duplicatedCount")]
        public int DuplicatedCount { get; set; }

        [JsonPropertyName("remainingCount")]
        public int RemainingCount { get; set; }

        [JsonPropertyName("availableCapacity")]
        public int AvailableCapacity { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("presets")]
        public IReadOnlyList<HueColorPresetResult> Presets { get; set; } = Array.Empty<HueColorPresetResult>();

        [JsonPropertyName("missingNames")]
        public IReadOnlyList<string> MissingNames { get; set; } = Array.Empty<string>();

        [JsonPropertyName("validationErrors")]
        public IReadOnlyList<string> ValidationErrors { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Request shape for atomically deleting several saved scenes by normalized name.
    /// </summary>
    public sealed class HueColorPresetBulkDeleteRequest
    {
        [JsonPropertyName("presetNames")]
        public List<string> PresetNames { get; set; } = new();
    }

    /// <summary>
    /// Credential-free result for an atomic saved-scene deletion operation.
    /// </summary>
    public sealed class HueColorPresetBulkDeleteResult
    {
        [JsonPropertyName("requestedCount")]
        public int RequestedCount { get; set; }

        [JsonPropertyName("deletedCount")]
        public int DeletedCount { get; set; }

        [JsonPropertyName("remainingCount")]
        public int RemainingCount { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("presets")]
        public IReadOnlyList<HueColorPresetResult> Presets { get; set; } = Array.Empty<HueColorPresetResult>();

        [JsonPropertyName("missingNames")]
        public IReadOnlyList<string> MissingNames { get; set; } = Array.Empty<string>();

        [JsonPropertyName("blockedPresets")]
        public IReadOnlyList<HueColorPresetDependenciesResult> BlockedPresets { get; set; } =
            Array.Empty<HueColorPresetDependenciesResult>();
    }

    /// <summary>
    /// Credential-free dependency summary for one saved scene.
    /// </summary>
    public sealed class HueColorPresetDependenciesResult
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("canDelete")]
        public bool CanDelete { get; init; }

        [JsonPropertyName("playlistCount")]
        public int PlaylistCount { get; init; }

        [JsonPropertyName("scheduledCueCount")]
        public int ScheduledCueCount { get; init; }

        [JsonPropertyName("playlists")]
        public IReadOnlyList<HueColorPresetPlaylistDependencyResult> Playlists { get; init; } =
            Array.Empty<HueColorPresetPlaylistDependencyResult>();

        [JsonPropertyName("scheduledCues")]
        public IReadOnlyList<HueColorPresetScheduleDependencyResult> ScheduledCues { get; init; } =
            Array.Empty<HueColorPresetScheduleDependencyResult>();
    }

    /// <summary>
    /// One saved-playlist reference to a scene, including repeated step count.
    /// </summary>
    public sealed class HueColorPresetPlaylistDependencyResult
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("referenceCount")]
        public int ReferenceCount { get; init; }
    }

    /// <summary>
    /// One direct or playlist-backed scheduled-cue reference to a scene.
    /// </summary>
    public sealed class HueColorPresetScheduleDependencyResult
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("enabled")]
        public bool Enabled { get; init; }

        [JsonPropertyName("referenceType")]
        public string ReferenceType { get; init; } = string.Empty;

        [JsonPropertyName("playlistName")]
        public string PlaylistName { get; init; } = string.Empty;
    }

    /// <summary>
    /// Credential-free saved-scene playlist metadata returned by administrator APIs,
    /// including its bounded per-step duration, RGB color, brightness, effect, effect-speed, fade-in, fade-out, and transition-curve overrides,
    /// repeat count, and selected target mode.
    /// </summary>
    public sealed class HueScenePlaylistResult
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("presetNames")]
        public IReadOnlyList<string> PresetNames { get; set; } = Array.Empty<string>();

        [JsonPropertyName("stepDurationSeconds")]
        public IReadOnlyList<int> StepDurationSeconds { get; set; } = Array.Empty<int>();

        [JsonPropertyName("stepRed")]
        public IReadOnlyList<int?> StepRed { get; set; } = Array.Empty<int?>();

        [JsonPropertyName("stepGreen")]
        public IReadOnlyList<int?> StepGreen { get; set; } = Array.Empty<int?>();

        [JsonPropertyName("stepBlue")]
        public IReadOnlyList<int?> StepBlue { get; set; } = Array.Empty<int?>();

        [JsonPropertyName("stepBrightnessPercent")]
        public IReadOnlyList<int?> StepBrightnessPercent { get; set; } = Array.Empty<int?>();

        [JsonPropertyName("stepEffects")]
        public IReadOnlyList<string?> StepEffects { get; set; } = Array.Empty<string?>();

        [JsonPropertyName("stepEffectSpeedPercent")]
        public IReadOnlyList<int?> StepEffectSpeedPercent { get; set; } = Array.Empty<int?>();

        [JsonPropertyName("stepTransitionSeconds")]
        public IReadOnlyList<int?> StepTransitionSeconds { get; set; } = Array.Empty<int?>();

        [JsonPropertyName("stepTransitionOutSeconds")]
        public IReadOnlyList<int?> StepTransitionOutSeconds { get; set; } = Array.Empty<int?>();

        [JsonPropertyName("stepTransitionCurves")]
        public IReadOnlyList<string?> StepTransitionCurves { get; set; } = Array.Empty<string?>();

        [JsonPropertyName("repeatCount")]
        public int RepeatCount { get; set; } = PluginConfiguration.DefaultScenePlaylistRepeatCount;

        [JsonPropertyName("playbackOrder")]
        public string PlaybackOrder { get; set; } = PluginConfiguration.ScenePlaylistOrderSequential;

        [JsonPropertyName("targetUserId")]
        public string TargetUserId { get; set; } = string.Empty;

        [JsonPropertyName("targetAllEnabledMappings")]
        public bool TargetAllEnabledMappings { get; set; }

        [JsonPropertyName("targetUserIds")]
        public IReadOnlyList<string> TargetUserIds { get; set; } = Array.Empty<string>();

        [JsonPropertyName("includeDefaultTarget")]
        public bool IncludeDefaultTarget { get; set; }

        [JsonPropertyName("targetLabel")]
        public string TargetLabel { get; set; } = string.Empty;

        [JsonPropertyName("totalDurationSeconds")]
        public int TotalDurationSeconds { get; set; }
    }

    /// <summary>
    /// Credential-free dependency summary for one saved-scene playlist.
    /// </summary>
    public sealed class HueScenePlaylistDependenciesResult
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("canDelete")]
        public bool CanDelete { get; init; }

        [JsonPropertyName("scheduledCueCount")]
        public int ScheduledCueCount { get; init; }

        [JsonPropertyName("scheduledCues")]
        public IReadOnlyList<HueScenePlaylistScheduleDependencyResult> ScheduledCues { get; init; } =
            Array.Empty<HueScenePlaylistScheduleDependencyResult>();
    }

    /// <summary>
    /// Request shape for atomically duplicating several saved-scene playlists by stable ID.
    /// </summary>
    public sealed class HueScenePlaylistBulkDuplicateRequest
    {
        [JsonPropertyName("playlistIds")]
        public List<string> PlaylistIds { get; set; } = new();
    }

    /// <summary>
    /// Credential-free result for an atomic saved-scene playlist duplication operation.
    /// </summary>
    public sealed class HueScenePlaylistBulkDuplicateResult
    {
        [JsonPropertyName("requestedCount")]
        public int RequestedCount { get; set; }

        [JsonPropertyName("duplicatedCount")]
        public int DuplicatedCount { get; set; }

        [JsonPropertyName("remainingCount")]
        public int RemainingCount { get; set; }

        [JsonPropertyName("availableCapacity")]
        public int AvailableCapacity { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("playlists")]
        public IReadOnlyList<HueScenePlaylistResult> Playlists { get; set; } = Array.Empty<HueScenePlaylistResult>();

        [JsonPropertyName("missingIds")]
        public IReadOnlyList<string> MissingIds { get; set; } = Array.Empty<string>();

        [JsonPropertyName("validationErrors")]
        public IReadOnlyList<string> ValidationErrors { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Request shape for atomically deleting several saved-scene playlists by stable ID.
    /// </summary>
    public sealed class HueScenePlaylistBulkDeleteRequest
    {
        [JsonPropertyName("playlistIds")]
        public List<string> PlaylistIds { get; set; } = new();
    }

    /// <summary>
    /// Credential-free result for an atomic saved-scene playlist deletion operation.
    /// </summary>
    public sealed class HueScenePlaylistBulkDeleteResult
    {
        [JsonPropertyName("requestedCount")]
        public int RequestedCount { get; set; }

        [JsonPropertyName("deletedCount")]
        public int DeletedCount { get; set; }

        [JsonPropertyName("remainingCount")]
        public int RemainingCount { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("playlists")]
        public IReadOnlyList<HueScenePlaylistResult> Playlists { get; set; } = Array.Empty<HueScenePlaylistResult>();

        [JsonPropertyName("blockedPlaylists")]
        public IReadOnlyList<HueScenePlaylistDependenciesResult> BlockedPlaylists { get; set; } = Array.Empty<HueScenePlaylistDependenciesResult>();
    }

    /// <summary>
    /// One credential-free scheduled-cue reference to a saved-scene playlist.
    /// </summary>
    public sealed class HueScenePlaylistScheduleDependencyResult
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("enabled")]
        public bool Enabled { get; init; }
    }

    /// <summary>
    /// Request shape for saving a credential-free scene playlist, its bounded repeat count,
    /// playback order, optional per-step duration, RGB color, brightness, effect, effect-speed, fade-in, fade-out, and transition-curve overrides,
    /// and either a legacy target mode or a selected mapping subset.
    /// </summary>
    public sealed class HueScenePlaylistRequest
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("presetNames")]
        public List<string> PresetNames { get; set; } = new();

        [JsonPropertyName("stepDurationSeconds")]
        public List<int>? StepDurationSeconds { get; set; }

        [JsonPropertyName("stepRed")]
        public List<int?>? StepRed { get; set; }

        [JsonPropertyName("stepGreen")]
        public List<int?>? StepGreen { get; set; }

        [JsonPropertyName("stepBlue")]
        public List<int?>? StepBlue { get; set; }

        [JsonPropertyName("stepBrightnessPercent")]
        public List<int?>? StepBrightnessPercent { get; set; }

        [JsonPropertyName("stepEffects")]
        public List<string?>? StepEffects { get; set; }

        [JsonPropertyName("stepEffectSpeedPercent")]
        public List<int?>? StepEffectSpeedPercent { get; set; }

        [JsonPropertyName("stepTransitionSeconds")]
        public List<int?>? StepTransitionSeconds { get; set; }

        [JsonPropertyName("stepTransitionOutSeconds")]
        public List<int?>? StepTransitionOutSeconds { get; set; }

        [JsonPropertyName("stepTransitionCurves")]
        public List<string?>? StepTransitionCurves { get; set; }

        [JsonPropertyName("repeatCount")]
        public int RepeatCount { get; set; } = PluginConfiguration.DefaultScenePlaylistRepeatCount;

        [JsonPropertyName("playbackOrder")]
        public string PlaybackOrder { get; set; } = PluginConfiguration.ScenePlaylistOrderSequential;

        [JsonPropertyName("targetUserId")]
        public string TargetUserId { get; set; } = string.Empty;

        [JsonPropertyName("targetUserIds")]
        public List<string> TargetUserIds { get; set; } = new();

        [JsonPropertyName("includeDefaultTarget")]
        public bool IncludeDefaultTarget { get; set; }

        [JsonPropertyName("targetAllEnabledMappings")]
        public bool TargetAllEnabledMappings { get; set; }

        public HueScenePlaylist ToConfigurationPlaylist()
        {
            PluginConfiguration.TryNormalizeScenePlaylistOrder(PlaybackOrder, out var normalizedPlaybackOrder);
            return new HueScenePlaylist
            {
                Id = Id?.Trim() ?? string.Empty,
                Name = Name?.Trim() ?? string.Empty,
                PresetNames = (PresetNames ?? new List<string>())
                    .Select(name => name?.Trim() ?? string.Empty)
                    .ToList(),
                StepDurationSeconds = (StepDurationSeconds ?? new List<int>()).ToList(),
                StepRed = (StepRed ?? new List<int?>()).ToList(),
                StepGreen = (StepGreen ?? new List<int?>()).ToList(),
                StepBlue = (StepBlue ?? new List<int?>()).ToList(),
                StepBrightnessPercent = (StepBrightnessPercent ?? new List<int?>()).ToList(),
                StepEffects = (StepEffects ?? new List<string?>())
                    .Select(value =>
                    {
                        if (string.IsNullOrWhiteSpace(value))
                            return null;
                        return PluginConfiguration.TryNormalizeColorPresetEffect(value, out var normalizedEffect)
                            ? normalizedEffect
                            : value.Trim();
                    })
                    .ToList(),
                StepEffectSpeedPercent = (StepEffectSpeedPercent ?? new List<int?>()).ToList(),
                StepTransitionSeconds = (StepTransitionSeconds ?? new List<int?>()).ToList(),
                StepTransitionOutSeconds = (StepTransitionOutSeconds ?? new List<int?>()).ToList(),
                StepTransitionCurves = (StepTransitionCurves ?? new List<string?>())
                    .Select(value =>
                    {
                        if (string.IsNullOrWhiteSpace(value))
                            return null;
                        return PluginConfiguration.TryNormalizeColorPresetTransitionCurve(value, out var normalizedCurve)
                            ? normalizedCurve
                            : value.Trim();
                    })
                    .ToList(),
                RepeatCount = RepeatCount,
                PlaybackOrder = normalizedPlaybackOrder,
                TargetUserId = TargetAllEnabledMappings || IncludeDefaultTarget || (TargetUserIds?.Count ?? 0) > 0
                    ? string.Empty
                    : PluginConfiguration.NormalizeJellyfinUserId(TargetUserId),
                TargetUserIds = (TargetUserIds ?? new List<string>())
                    .Select(PluginConfiguration.NormalizeJellyfinUserId)
                    .ToList(),
                IncludeDefaultTarget = IncludeDefaultTarget,
                TargetAllEnabledMappings = TargetAllEnabledMappings
            };
        }
    }

    /// <summary>
    /// Request shape for renaming a saved-scene playlist. The response remains
    /// credential-free; scheduled-cue references are migrated server-side.
    /// </summary>
    public sealed class HueScenePlaylistRenameRequest
    {
        [JsonPropertyName("newName")]
        public string NewName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Optional per-run target override for a saved-scene playlist preview. The persisted
    /// playlist remains unchanged.
    /// </summary>
    public sealed class HueScenePlaylistPreviewRequest
    {
        [JsonPropertyName("targetUserId")]
        public string? TargetUserId { get; set; }

        [JsonPropertyName("targetAllEnabledMappings")]
        public bool? TargetAllEnabledMappings { get; set; }

        [JsonPropertyName("targetUserIds")]
        public List<string>? TargetUserIds { get; set; }

        [JsonPropertyName("targetRoutes")]
        public List<HueCurrentLightColorTargetRoute>? TargetRoutes { get; set; }

        [JsonPropertyName("includeDefaultTarget")]
        public bool? IncludeDefaultTarget { get; set; }
    }

    /// <summary>
    /// Request shape for previewing several saved-scene playlists sequentially. Nullable
    /// target mode preserves each saved playlist's target when omitted, matching the
    /// individual preview route.
    /// </summary>
    public sealed class HueScenePlaylistBulkPreviewRequest
    {
        [JsonPropertyName("playlistIds")]
        public List<string> PlaylistIds { get; set; } = new();

        [JsonPropertyName("targetUserId")]
        public string? TargetUserId { get; set; }

        [JsonPropertyName("targetAllEnabledMappings")]
        public bool? TargetAllEnabledMappings { get; set; }

        [JsonPropertyName("targetUserIds")]
        public List<string>? TargetUserIds { get; set; }

        [JsonPropertyName("targetRoutes")]
        public List<HueCurrentLightColorTargetRoute>? TargetRoutes { get; set; }

        [JsonPropertyName("includeDefaultTarget")]
        public bool? IncludeDefaultTarget { get; set; }
    }

    /// <summary>
    /// Credential-free aggregate result for a sequential saved-scene playlist preview.
    /// </summary>
    public sealed class HueScenePlaylistBulkPreviewResult
    {
        [JsonPropertyName("requestedCount")]
        public int RequestedCount { get; set; }

        [JsonPropertyName("completedCount")]
        public int CompletedCount { get; set; }

        [JsonPropertyName("succeededCount")]
        public int SucceededCount { get; set; }

        [JsonPropertyName("failedCount")]
        public int FailedCount { get; set; }

        [JsonPropertyName("canceled")]
        public bool Canceled { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("results")]
        public IReadOnlyList<HueScenePlaylistRunResult> Results { get; set; } = Array.Empty<HueScenePlaylistRunResult>();

        [JsonPropertyName("missingIds")]
        public IReadOnlyList<string> MissingIds { get; set; } = Array.Empty<string>();

        [JsonPropertyName("validationErrors")]
        public IReadOnlyList<string> ValidationErrors { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Request shape for one saved-scene cue. TargetUserId is blank for the global bridge
    /// target or when TargetAllEnabledMappings is true; the latter fans out to the global
    /// target and every distinct enabled custom mapping target.
    /// target; runDate selects a one-time cue, otherwise daily, weekly, monthly-day,
    /// monthly-weekday, or yearly date rules in the selected cue timezone apply. RecurrenceInterval
    /// controls the number of calendar units between runs and requires startDate when greater than one.
    /// Priority is bounded from 0 through 100; higher values run first when automatic cues are due together,
    /// and omitted priority preserves an existing cue's value during ordinary edits.
    /// DurationSeconds is zero
    /// to inherit the saved scene's
    /// duration or a bounded per-cue override; the saved scene's optional fade-in and fade-out
    /// are inherited and clamped to that effective duration. MaxRuns is zero for unlimited
    /// execution or a bounded number of attempts; RunCount is optional so normal edits preserve
    /// the persisted finite-cue counter. BrightnessPercent is null to inherit the saved scene
    /// brightness or a bounded 0-100 override for direct scene cues; playlist cues keep their
    /// per-step brightness. Bridge credentials are intentionally not accepted.
    /// </summary>
    public sealed class HueSceneScheduleRequest
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("presetName")]
        public string PresetName { get; set; } = string.Empty;

        [JsonPropertyName("playlistName")]
        public string PlaylistName { get; set; } = string.Empty;

        [JsonPropertyName("priority")]
        public int? Priority { get; set; }

        [JsonPropertyName("playbackPolicy")]
        public string? PlaybackPolicy { get; set; }

        [JsonPropertyName("targetUserId")]
        public string TargetUserId { get; set; } = string.Empty;

        [JsonPropertyName("targetAllEnabledMappings")]
        public bool? TargetAllEnabledMappings { get; set; }

        [JsonPropertyName("targetUserIds")]
        public List<string>? TargetUserIds { get; set; }

        [JsonPropertyName("targetRoutes")]
        public List<HueSceneScheduleTargetRoute>? TargetRoutes { get; set; }

        [JsonPropertyName("includeDefaultTarget")]
        public bool? IncludeDefaultTarget { get; set; }

        [JsonPropertyName("timeOfDay")]
        public string TimeOfDay { get; set; } = "20:00";

        [JsonPropertyName("timeMode")]
        public string TimeMode { get; set; } = PluginConfiguration.SceneScheduleTimeModeFixed;

        [JsonPropertyName("solarOffsetMinutes")]
        public int SolarOffsetMinutes { get; set; }

        [JsonPropertyName("solarLatitude")]
        public double? SolarLatitude { get; set; }

        [JsonPropertyName("solarLongitude")]
        public double? SolarLongitude { get; set; }

        [JsonPropertyName("timeZoneId")]
        public string TimeZoneId { get; set; } = string.Empty;

        [JsonPropertyName("timeZoneIanaId")]
        public string? TimeZoneIanaId { get; set; }

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

        private int _durationSeconds;
        private bool _durationSpecified;

        [JsonPropertyName("durationSeconds")]
        public int DurationSeconds
        {
            get => _durationSeconds;
            set
            {
                _durationSpecified = true;
                _durationSeconds = value;
            }
        }

        [JsonIgnore]
        public bool DurationSpecified => _durationSpecified;

        private int? _brightnessPercent;
        private int? _red;
        private int? _green;
        private int? _blue;
        private bool _brightnessSpecified;
        private bool _redSpecified;
        private bool _greenSpecified;
        private bool _blueSpecified;

        [JsonPropertyName("brightnessPercent")]
        public int? BrightnessPercent
        {
            get => _brightnessPercent;
            set
            {
                _brightnessSpecified = true;
                _brightnessPercent = value;
            }
        }

        [JsonIgnore]
        public bool BrightnessSpecified => _brightnessSpecified;

        [JsonPropertyName("red")]
        public int? Red
        {
            get => _red;
            set
            {
                _redSpecified = true;
                _red = value;
            }
        }

        [JsonIgnore]
        public bool RedSpecified => _redSpecified;

        [JsonPropertyName("green")]
        public int? Green
        {
            get => _green;
            set
            {
                _greenSpecified = true;
                _green = value;
            }
        }

        [JsonIgnore]
        public bool GreenSpecified => _greenSpecified;

        [JsonPropertyName("blue")]
        public int? Blue
        {
            get => _blue;
            set
            {
                _blueSpecified = true;
                _blue = value;
            }
        }

        [JsonIgnore]
        public bool BlueSpecified => _blueSpecified;

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

        [JsonPropertyName("skipNextOccurrence")]
        public bool? SkipNextOccurrence { get; set; }

        public HueSceneSchedule ToConfigurationSchedule()
        {
            return new HueSceneSchedule
            {
                Id = Id?.Trim() ?? string.Empty,
                Name = Name?.Trim() ?? string.Empty,
                PresetName = PresetName?.Trim() ?? string.Empty,
                PlaylistName = PlaylistName?.Trim() ?? string.Empty,
                Priority = Priority ?? PluginConfiguration.MinSceneSchedulePriority,
                PlaybackPolicy = string.IsNullOrWhiteSpace(PlaybackPolicy)
                    ? PluginConfiguration.SceneAutomationPlaybackPolicyInherit
                    : PlaybackPolicy.Trim(),
                TargetUserId = PluginConfiguration.NormalizeJellyfinUserId(TargetUserId),
                TargetUserIds = (TargetUserIds ?? new List<string>())
                    .Select(PluginConfiguration.NormalizeJellyfinUserId)
                    .ToList(),
                TargetRoutes = (TargetRoutes ?? new List<HueSceneScheduleTargetRoute>())
                    .Where(route => route != null)
                    .Select(route => new HueSceneScheduleTargetRoute
                    {
                        UserId = PluginConfiguration.NormalizeJellyfinUserId(route.UserId),
                        DeviceId = route.DeviceId?.Trim() ?? string.Empty
                    })
                    .ToList(),
                IncludeDefaultTarget = IncludeDefaultTarget ?? false,
                TargetAllEnabledMappings = TargetAllEnabledMappings ?? false,
                TimeOfDay = TimeOfDay?.Trim() ?? string.Empty,
                TimeMode = TimeMode?.Trim() ?? string.Empty,
                SolarOffsetMinutes = SolarOffsetMinutes,
                SolarLatitude = SolarLatitude,
                SolarLongitude = SolarLongitude,
                TimeZoneId = string.IsNullOrWhiteSpace(TimeZoneIanaId)
                    ? TimeZoneId?.Trim() ?? string.Empty
                    : TimeZoneIanaId?.Trim() ?? string.Empty,
                Recurrence = Recurrence?.Trim() ?? string.Empty,
                RecurrenceInterval = RecurrenceInterval,
                DayOfMonth = DayOfMonth,
                MonthOfYear = MonthOfYear,
                WeekOfMonth = WeekOfMonth,
                DayOfWeek = DayOfWeek,
                DurationSeconds = DurationSeconds,
                BrightnessPercent = BrightnessPercent,
                Red = Red,
                Green = Green,
                Blue = Blue,
                MaxRuns = MaxRuns ?? 0,
                RunCount = RunCount ?? 0,
                RunDate = RunDate?.Trim() ?? string.Empty,
                StartDate = StartDate?.Trim() ?? string.Empty,
                EndDate = EndDate?.Trim() ?? string.Empty,
                ExcludedDates = (ExcludedDates ?? new List<string>())
                    .Select(value => value?.Trim() ?? string.Empty)
                    .ToList(),
                DaysOfWeekMask = DaysOfWeekMask,
                Enabled = Enabled,
                SkipNextOccurrence = SkipNextOccurrence ?? false
            };
        }
    }

    /// <summary>
    /// Request shape for changing only a scene cue's enabled state. All schedule
    /// definition fields remain unchanged and no bridge credentials are accepted.
    /// </summary>
    public sealed class HueSceneScheduleEnabledRequest
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }
    }

    /// <summary>
    /// Request shape for running several saved scene cues sequentially through their
    /// restorative administrator Run Now lifecycle.
    /// </summary>
    public sealed class HueSceneScheduleBulkRunRequest
    {
        [JsonPropertyName("scheduleIds")]
        public List<string> ScheduleIds { get; set; } = new();
    }

    /// <summary>
    /// Credential-free aggregate result for a sequential scheduled-cue run.
    /// </summary>
    public sealed class HueSceneScheduleBulkRunResult
    {
        [JsonPropertyName("requestedCount")]
        public int RequestedCount { get; set; }

        [JsonPropertyName("completedCount")]
        public int CompletedCount { get; set; }

        [JsonPropertyName("succeededCount")]
        public int SucceededCount { get; set; }

        [JsonPropertyName("failedCount")]
        public int FailedCount { get; set; }

        [JsonPropertyName("canceled")]
        public bool Canceled { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("results")]
        public IReadOnlyList<HueSceneAutomationRunResult> Results { get; set; } = Array.Empty<HueSceneAutomationRunResult>();

        [JsonPropertyName("missingScheduleIds")]
        public IReadOnlyList<string> MissingScheduleIds { get; set; } = Array.Empty<string>();

        [JsonPropertyName("validationErrors")]
        public IReadOnlyList<string> ValidationErrors { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Request shape for requesting cancellation of several manually started cues.
    /// </summary>
    public sealed class HueSceneScheduleBulkCancelRequest
    {
        [JsonPropertyName("scheduleIds")]
        public List<string> ScheduleIds { get; set; } = new();
    }

    /// <summary>
    /// Credential-free result from a bulk scheduled-cue cancellation request.
    /// </summary>
    public sealed class HueSceneScheduleBulkCancelResult
    {
        [JsonPropertyName("requestedCount")]
        public int RequestedCount { get; set; }

        [JsonPropertyName("canceledCount")]
        public int CanceledCount { get; set; }

        [JsonPropertyName("canceledScheduleIds")]
        public IReadOnlyList<string> CanceledScheduleIds { get; set; } = Array.Empty<string>();

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("missingScheduleIds")]
        public IReadOnlyList<string> MissingScheduleIds { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Request shape for atomically creating disabled copies of several scene cues by ID.
    /// </summary>
    public sealed class HueSceneScheduleBulkDuplicateRequest
    {
        [JsonPropertyName("scheduleIds")]
        public List<string> ScheduleIds { get; set; } = new();
    }

    /// <summary>
    /// Credential-free result for an atomic bulk scheduled-cue duplication operation.
    /// </summary>
    public sealed class HueSceneScheduleBulkDuplicateResult
    {
        [JsonPropertyName("requestedCount")]
        public int RequestedCount { get; set; }

        [JsonPropertyName("duplicatedCount")]
        public int DuplicatedCount { get; set; }

        [JsonPropertyName("availableCapacity")]
        public int AvailableCapacity { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("missingScheduleIds")]
        public IReadOnlyList<string> MissingScheduleIds { get; set; } = Array.Empty<string>();

        [JsonPropertyName("validationErrors")]
        public IReadOnlyList<string> ValidationErrors { get; set; } = Array.Empty<string>();

        [JsonPropertyName("schedules")]
        public IReadOnlyList<HueSceneScheduleResult> Schedules { get; set; } = Array.Empty<HueSceneScheduleResult>();
    }

    /// <summary>
    /// Request shape for atomically resetting the execution counters of several scene cues.
    /// Schedule definitions, target values, and bridge credentials are never accepted or
    /// changed by this action.
    /// </summary>
    public sealed class HueSceneScheduleBulkResetRunCountRequest
    {
        [JsonPropertyName("scheduleIds")]
        public List<string> ScheduleIds { get; set; } = new();
    }

    /// <summary>
    /// Credential-free result for an atomic bulk scheduled-cue counter reset.
    /// </summary>
    public sealed class HueSceneScheduleBulkResetRunCountResult
    {
        [JsonPropertyName("requestedCount")]
        public int RequestedCount { get; set; }

        [JsonPropertyName("resetCount")]
        public int ResetCount { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("missingScheduleIds")]
        public IReadOnlyList<string> MissingScheduleIds { get; set; } = Array.Empty<string>();

        [JsonPropertyName("schedules")]
        public IReadOnlyList<HueSceneScheduleResult> Schedules { get; set; } = Array.Empty<HueSceneScheduleResult>();
    }

    /// <summary>
    /// Request shape for changing only the enabled state of several scene cues. Schedule
    /// definitions, counters, and bridge credentials are never accepted or changed.
    /// </summary>
    public sealed class HueSceneScheduleBulkEnabledRequest
    {
        [JsonPropertyName("scheduleIds")]
        public List<string> ScheduleIds { get; set; } = new();

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }
    }

    /// <summary>
    /// Credential-free result for an atomic bulk cue enabled-state operation.
    /// </summary>
    public sealed class HueSceneScheduleBulkEnabledResult
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("requestedCount")]
        public int RequestedCount { get; set; }

        [JsonPropertyName("updatedCount")]
        public int UpdatedCount { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("schedules")]
        public IReadOnlyList<HueSceneScheduleResult> Schedules { get; set; } = Array.Empty<HueSceneScheduleResult>();
    }

    /// <summary>
    /// Request shape for atomically marking or clearing the next automatic occurrence of
    /// several scene cues. Schedule definitions, counters, and bridge credentials are
    /// never accepted or changed.
    /// </summary>
    public sealed class HueSceneScheduleBulkSkipNextRequest
    {
        [JsonPropertyName("scheduleIds")]
        public List<string> ScheduleIds { get; set; } = new();

        [JsonPropertyName("skipNextOccurrence")]
        public bool SkipNextOccurrence { get; set; }
    }

    /// <summary>
    /// Credential-free result for an atomic bulk skipped-occurrence operation.
    /// </summary>
    public sealed class HueSceneScheduleBulkSkipNextResult
    {
        [JsonPropertyName("skipNextOccurrence")]
        public bool SkipNextOccurrence { get; set; }

        [JsonPropertyName("requestedCount")]
        public int RequestedCount { get; set; }

        [JsonPropertyName("updatedCount")]
        public int UpdatedCount { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("schedules")]
        public IReadOnlyList<HueSceneScheduleResult> Schedules { get; set; } = Array.Empty<HueSceneScheduleResult>();
    }

    /// <summary>
    /// Request shape for atomically deleting several scene cues by stable ID.
    /// </summary>
    public sealed class HueSceneScheduleBulkDeleteRequest
    {
        [JsonPropertyName("scheduleIds")]
        public List<string> ScheduleIds { get; set; } = new();
    }

    /// <summary>
    /// Credential-free result for an atomic bulk scene-cue deletion.
    /// </summary>
    public sealed class HueSceneScheduleBulkDeleteResult
    {
        [JsonPropertyName("requestedCount")]
        public int RequestedCount { get; set; }

        [JsonPropertyName("deletedCount")]
        public int DeletedCount { get; set; }

        [JsonPropertyName("remainingCount")]
        public int RemainingCount { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("schedules")]
        public IReadOnlyList<HueSceneScheduleResult> Schedules { get; set; } = Array.Empty<HueSceneScheduleResult>();
    }

    /// <summary>
    /// Credential-free scene cue returned by the administrator API, including the effective
    /// saved-scene fade-in/fade-out, optional per-cue duration override, daily, weekly, monthly-day, monthly-weekday, or yearly recurrence,
    /// bounded recurrence intervals, finite execution limits, one-time date, inclusive bounds,
    /// and deterministic execution priority,
    /// and normalized excluded calendar dates. TargetAllEnabledMappings exposes the optional
    /// sequential fan-out mode without returning any bridge credentials.
    /// </summary>
    public sealed class HueSceneScheduleResult
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("presetName")]
        public string PresetName { get; set; } = string.Empty;

        [JsonPropertyName("playlistName")]
        public string PlaylistName { get; set; } = string.Empty;

        [JsonPropertyName("playlistStepCount")]
        public int PlaylistStepCount { get; set; }

        [JsonPropertyName("playlistRepeatCount")]
        public int PlaylistRepeatCount { get; set; } = PluginConfiguration.DefaultScenePlaylistRepeatCount;

        [JsonPropertyName("playlistPlaybackOrder")]
        public string PlaylistPlaybackOrder { get; set; } = PluginConfiguration.ScenePlaylistOrderSequential;

        [JsonPropertyName("playlistTotalDurationSeconds")]
        public int PlaylistTotalDurationSeconds { get; set; }

        [JsonPropertyName("priority")]
        public int Priority { get; set; }

        [JsonPropertyName("playbackPolicy")]
        public string PlaybackPolicy { get; set; } = PluginConfiguration.SceneAutomationPlaybackPolicyInherit;

        [JsonPropertyName("effectivePlaybackPolicy")]
        public string EffectivePlaybackPolicy { get; set; } = PluginConfiguration.SceneAutomationPlaybackPolicySkip;

        [JsonPropertyName("effect")]
        public string Effect { get; set; } = PluginConfiguration.ColorPresetEffectSolid;

        [JsonPropertyName("effectSpeedPercent")]
        public int EffectSpeedPercent { get; set; } = PluginConfiguration.DefaultColorPresetEffectSpeedPercent;

        [JsonPropertyName("brightnessPercent")]
        public int? BrightnessPercent { get; set; }

        [JsonPropertyName("red")]
        public int? Red { get; set; }

        [JsonPropertyName("green")]
        public int? Green { get; set; }

        [JsonPropertyName("blue")]
        public int? Blue { get; set; }

        [JsonPropertyName("transitionCurve")]
        public string TransitionCurve { get; set; } = PluginConfiguration.ColorPresetTransitionCurveLinear;

        [JsonPropertyName("targetUserId")]
        public string TargetUserId { get; set; } = string.Empty;

        [JsonPropertyName("targetAllEnabledMappings")]
        public bool TargetAllEnabledMappings { get; set; }

        [JsonPropertyName("targetUserIds")]
        public IReadOnlyList<string> TargetUserIds { get; set; } = Array.Empty<string>();

        [JsonPropertyName("targetRoutes")]
        public IReadOnlyList<HueSceneScheduleTargetRoute> TargetRoutes { get; set; } = Array.Empty<HueSceneScheduleTargetRoute>();

        [JsonPropertyName("includeDefaultTarget")]
        public bool IncludeDefaultTarget { get; set; }

        [JsonPropertyName("targetLabel")]
        public string TargetLabel { get; set; } = string.Empty;

        [JsonPropertyName("timeOfDay")]
        public string TimeOfDay { get; set; } = string.Empty;

        [JsonPropertyName("timeMode")]
        public string TimeMode { get; set; } = PluginConfiguration.SceneScheduleTimeModeFixed;

        [JsonPropertyName("solarOffsetMinutes")]
        public int SolarOffsetMinutes { get; set; }

        [JsonPropertyName("solarLatitude")]
        public double? SolarLatitude { get; set; }

        [JsonPropertyName("solarLongitude")]
        public double? SolarLongitude { get; set; }

        [JsonPropertyName("timeZoneId")]
        public string TimeZoneId { get; set; } = string.Empty;

        [JsonPropertyName("timeZoneIanaId")]
        public string TimeZoneIanaId { get; set; } = string.Empty;

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

        [JsonPropertyName("skipNextOccurrence")]
        public bool SkipNextOccurrence { get; set; }
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

        [JsonPropertyName("playlistName")]
        public string PlaylistName { get; set; } = string.Empty;

        [JsonPropertyName("playlistStepCount")]
        public int PlaylistStepCount { get; set; }

        [JsonPropertyName("playlistRepeatCount")]
        public int PlaylistRepeatCount { get; set; } = PluginConfiguration.DefaultScenePlaylistRepeatCount;

        [JsonPropertyName("playlistPlaybackOrder")]
        public string PlaylistPlaybackOrder { get; set; } = PluginConfiguration.ScenePlaylistOrderSequential;

        [JsonPropertyName("playlistTotalDurationSeconds")]
        public int PlaylistTotalDurationSeconds { get; set; }

        [JsonPropertyName("playlistSteps")]
        public IReadOnlyList<HueScenePlaylistScheduleStep> PlaylistSteps { get; set; } = Array.Empty<HueScenePlaylistScheduleStep>();

        [JsonPropertyName("priority")]
        public int Priority { get; set; }

        [JsonPropertyName("effect")]
        public string Effect { get; set; } = PluginConfiguration.ColorPresetEffectSolid;

        [JsonPropertyName("effectSpeedPercent")]
        public int EffectSpeedPercent { get; set; } = PluginConfiguration.DefaultColorPresetEffectSpeedPercent;

        [JsonPropertyName("brightnessPercent")]
        public int BrightnessPercent { get; set; }

        [JsonPropertyName("red")]
        public int Red { get; set; }

        [JsonPropertyName("green")]
        public int Green { get; set; }

        [JsonPropertyName("blue")]
        public int Blue { get; set; }

        [JsonPropertyName("transitionCurve")]
        public string TransitionCurve { get; set; } = PluginConfiguration.ColorPresetTransitionCurveLinear;

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

        [JsonPropertyName("targetAllEnabledMappings")]
        public bool TargetAllEnabledMappings { get; set; }

        [JsonPropertyName("targetUserIds")]
        public IReadOnlyList<string> TargetUserIds { get; set; } = Array.Empty<string>();

        [JsonPropertyName("targetRoutes")]
        public IReadOnlyList<HueSceneScheduleTargetRoute> TargetRoutes { get; set; } = Array.Empty<HueSceneScheduleTargetRoute>();

        [JsonPropertyName("includeDefaultTarget")]
        public bool IncludeDefaultTarget { get; set; }

        [JsonPropertyName("timeMode")]
        public string TimeMode { get; set; } = PluginConfiguration.SceneScheduleTimeModeFixed;

        [JsonPropertyName("solarOffsetMinutes")]
        public int SolarOffsetMinutes { get; set; }

        [JsonPropertyName("solarLatitude")]
        public double? SolarLatitude { get; set; }

        [JsonPropertyName("solarLongitude")]
        public double? SolarLongitude { get; set; }

        [JsonPropertyName("timeZoneId")]
        public string TimeZoneId { get; set; } = string.Empty;

        [JsonPropertyName("timeZoneIanaId")]
        public string TimeZoneIanaId { get; set; } = string.Empty;

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
    /// Bounded credential-free overlap report for upcoming scheduled cues.
    /// </summary>
    public sealed class HueSceneScheduleConflictsResult
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

        [JsonPropertyName("conflicts")]
        public IReadOnlyList<HueSceneScheduleConflict> Conflicts { get; set; } = Array.Empty<HueSceneScheduleConflict>();
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

        [JsonPropertyName("timeZoneIanaId")]
        public string TimeZoneIanaId { get; set; } = string.Empty;
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
        public string ConfiguredPlaybackMediaFilter { get; set; } = PluginConfiguration.PlaybackMediaFilterAllVideo;
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
        public string? ActiveDeviceId { get; set; }
        public string? ActiveDeviceName { get; set; }
        public bool? ActiveDeviceRouteMatched { get; set; }
        public string? ActivePlaybackMediaFilter { get; set; }
        public string? ActiveBridgeIp { get; set; }
        public string? ActiveEntertainmentAreaId { get; set; }
        public int? ActiveTargetFps { get; set; }
        public int? ActiveAudioSensitivityPercent { get; set; }
        public int? ActiveAudioNoiseGatePercent { get; set; }
        public int? ActiveAudioLowFrequencyHz { get; set; }
        public int? ActiveAudioMidFrequencyHz { get; set; }
        public int? ActiveAudioHighFrequencyHz { get; set; }
        public int? ActiveAudioLowGainPercent { get; set; }
        public int? ActiveAudioMidGainPercent { get; set; }
        public int? ActiveAudioHighGainPercent { get; set; }
        public int? ActiveAudioResponseSmoothingPercent { get; set; }
        public int? ActiveAudioBandSpreadPercent { get; set; }
        public int? ActiveAudioBeatPulsePercent { get; set; }
        public int? ActiveAudioBeatPulseDecayPercent { get; set; }
        public int? ActiveAudioBeatPulseThresholdPercent { get; set; }
        public string? ActiveAudioColorPalette { get; set; }
        public string? ActiveAudioSpatialMode { get; set; }
        public string? ActiveAudioChannelMode { get; set; }
        public string? ActiveFrameResolution { get; set; }
        public string? ActiveVideoScalingMode { get; set; }
        public string? ActiveVideoDeinterlaceMode { get; set; }
        public int? ActiveSamplingBreadthPercent { get; set; }
        public string? ActiveSamplingMode { get; set; }
        public string? ActiveSpatialOrientation { get; set; }
        public int? ActiveColorSmoothingPercent { get; set; }
        public int? ActiveBrightnessBoost { get; set; }
        public int? ActiveRedGain { get; set; }
        public int? ActiveGreenGain { get; set; }
        public int? ActiveBlueGain { get; set; }
        public int? ActiveColorSaturation { get; set; }
        public int? ActiveHueShiftDegrees { get; set; }
        public int? ActiveOutputBrightnessPercent { get; set; }
        public double? ActiveGammaCorrection { get; set; }
        public int? ActiveContrastPercent { get; set; }
        public int? ActiveColorTemperatureKelvin { get; set; }
        public int? ActiveBlackoutThreshold { get; set; }
        public string? ActiveBlackoutBehavior { get; set; }
        public int? ActiveColorChangeThreshold { get; set; }
        public bool? ActiveUseGpu { get; set; }
        public bool? ActiveCustomFfmpegFlagsConfigured { get; set; }
        public int? ActiveFfmpegStallTimeoutSeconds { get; set; }
        public int? ActiveNetworkRetryAttempts { get; set; }
        public string? ActiveChannelIds { get; set; }
        public bool? ActiveRestoreLightState { get; set; }
        public string? ActivePauseBehavior { get; set; }
        public int? ActivePauseBrightnessPercent { get; set; }
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
        public string? OutcomeFilter { get; init; }
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
        public string PlaybackMediaFilter { get; init; } = PluginConfiguration.PlaybackMediaFilterAllVideo;
        public bool DefaultBridgeConfigured { get; init; }
        public int EnabledUserMappingCount { get; init; }
        public bool CustomUserTargetConfigured { get; init; }
        public bool ServiceAvailable { get; init; }
        public HueToolStatus Ffmpeg { get; init; } = new();
        public HueToolStatus AudioCapture { get; init; } = new();
        public bool AudioCaptureRequired { get; init; }
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
    /// Consolidated, credential-safe administrator support document. The nested
    /// diagnostics and history types are already redacted for API consumers; this
    /// envelope makes it possible to collect the same evidence in one download.
    /// </summary>
    public sealed class HueSupportBundle
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; init; } = CurrentSchemaVersion;
        public DateTime GeneratedAtUtc { get; init; }
        public string PluginVersion { get; init; } = string.Empty;
        public HueDiagnosticsResult Diagnostics { get; init; } = new();
        public HueTargetDiagnosticsResult TargetDiagnostics { get; init; } = new();
        public HueSyncStatus Runtime { get; init; } = new();
        public HueSessionHistoryResult SessionHistory { get; init; } = new();
        public HueSceneAutomationStatus SceneAutomation { get; init; } = new();
        public HueSceneScheduleHistoryResult SceneScheduleHistory { get; init; } = new();
        public HueConfigurationExportDocument Configuration { get; init; } = new();
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
        public string? DeviceId { get; init; }
        public string? DeviceName { get; init; }
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
        public int SelectedChannelCount { get; init; }
        public bool ChannelProfileValid { get; init; } = true;
        public string? MissingChannelIds { get; init; }
        public bool Ready { get; init; }
        public string Status { get; init; } = string.Empty;
    }

}
