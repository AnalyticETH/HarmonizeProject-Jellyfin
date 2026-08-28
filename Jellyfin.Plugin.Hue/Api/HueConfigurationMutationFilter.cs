using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Hue.Configuration;
using Jellyfin.Plugin.Hue.Service;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.Hue.Api;

/// <summary>
/// Serializes administrator configuration writers with configuration import and the
/// scheduler's evaluation barrier. Read-only, bridge-lifecycle, and cue-run actions are
/// intentionally excluded so a configuration write never holds the bridge gate across a
/// long-running preview or playback operation. A guarded policy-disable write may overlap
/// existing playback long enough to persist the disable and await targeted cleanup.
/// </summary>
public sealed class HueConfigurationMutationFilter : IAsyncActionFilter
{
    private readonly HueBridgeLifecycleGate _bridgeLifecycleGate;

    public HueConfigurationMutationFilter(HueBridgeLifecycleGate bridgeLifecycleGate)
    {
        _bridgeLifecycleGate = bridgeLifecycleGate ?? throw new ArgumentNullException(nameof(bridgeLifecycleGate));
    }

    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        if (!IsConfigurationWriter(context.HttpContext.Request.Method, context.HttpContext.Request.Path.Value))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var lease = _bridgeLifecycleGate.TryEnterConfigurationMutation(
            allowActivePlayback: IsDisablingPlaybackPolicy(context));
        if (lease == null)
        {
            context.Result = new ConflictObjectResult(
                "Configuration change cannot proceed while another Hue lifecycle or configuration mutation is active.");
            return;
        }

        using (lease)
        {
            await next().ConfigureAwait(false);
        }
    }

    private static bool IsConfigurationWriter(string method, string? path)
    {
        if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var route = path?.Trim('/') ?? string.Empty;
        var segments = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 ||
            !segments[0].Equals("HueSync", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (segments.Length == 2 &&
            (segments[1].Equals("Configuration", StringComparison.OrdinalIgnoreCase) ||
             segments[1].Equals("History", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (segments.Length == 3 &&
            segments[1].Equals("BridgeCertificate", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("Trust", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (segments[1].Equals("SceneSchedules", StringComparison.OrdinalIgnoreCase) &&
            segments.Length == 3 &&
            segments[2].Equals("History", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (segments[1].Equals("Configuration", StringComparison.OrdinalIgnoreCase))
            return false;

        if (segments[1].Equals("UserMappings", StringComparison.OrdinalIgnoreCase))
            return true;

        if (segments[1].Equals("ColorPresets", StringComparison.OrdinalIgnoreCase))
        {
            // Preview is an action segment, not a reserved substring in a user-selected
            // scene name. DELETE /ColorPresets/{name} remains a writer even when the name
            // itself is "Preview" or contains that word.
            return !string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) ||
                !(segments.Length == 3 &&
                  segments[2].Equals("BulkPreview", StringComparison.OrdinalIgnoreCase)) &&
                !(segments.Length == 4 &&
                  segments[3].Equals("Preview", StringComparison.OrdinalIgnoreCase));
        }

        if (segments[1].Equals("ScenePlaylists", StringComparison.OrdinalIgnoreCase))
        {
            return !string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) ||
                !(segments.Length == 3 &&
                  segments[2].Equals("BulkPreview", StringComparison.OrdinalIgnoreCase)) &&
                !(segments.Length == 4 &&
                  segments[3].Equals("Preview", StringComparison.OrdinalIgnoreCase));
        }

        if (segments[1].Equals("SceneSchedules", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) &&
                segments.Length == 3 &&
                (segments[2].Equals("BulkRun", StringComparison.OrdinalIgnoreCase) ||
                 segments[2].Equals("BulkCancel", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            return !(string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) &&
                segments.Length == 4 &&
                (segments[3].Equals("Run", StringComparison.OrdinalIgnoreCase) ||
                 segments[3].Equals("Cancel", StringComparison.OrdinalIgnoreCase)));
        }

        return false;
    }

    private static bool IsDisablingPlaybackPolicy(ActionExecutingContext context)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration == null || !configuration.SyncEnabled)
            return false;

        var route = context.HttpContext.Request.Path.Value?.Trim('/') ?? string.Empty;
        var segments = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 ||
            !segments[0].Equals("HueSync", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (segments.Length == 2 &&
            segments[1].Equals("Configuration", StringComparison.OrdinalIgnoreCase) &&
            context.ActionArguments.TryGetValue("settings", out var settingsArgument) &&
            settingsArgument is HuePluginConfigurationSettings settings)
        {
            return !settings.SyncEnabled;
        }

        if (segments.Length == 3 &&
            segments[1].Equals("Configuration", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("Import", StringComparison.OrdinalIgnoreCase) &&
            context.ActionArguments.TryGetValue("request", out var importArgument) &&
            importArgument is HueConfigurationImportRequest import)
        {
            if (import.Configuration?.SyncEnabled == false)
                return true;

            return (import.UserMappings ?? new List<UserBridgeMappingImport>()).Any(mapping =>
                mapping != null &&
                !mapping.SyncEnabled &&
                TryParsePlaybackUserId(mapping.UserId, out var userId) &&
                configuration.IsSyncEnabledForUser(userId));
        }

        if (segments.Length == 2 &&
            segments[1].Equals("UserMappings", StringComparison.OrdinalIgnoreCase) &&
            context.ActionArguments.TryGetValue("request", out var mappingArgument) &&
            mappingArgument is HueUserMappingRequest mappingRequest)
        {
            if (mappingRequest.Values == null ||
                !TryGetValue(mappingRequest.Values, "SyncEnabled", out var syncEnabled) ||
                syncEnabled.ValueKind != JsonValueKind.False ||
                !TryGetValue(mappingRequest.Values, "UserId", out var userIdValue) ||
                userIdValue.ValueKind != JsonValueKind.String ||
                !TryParsePlaybackUserId(userIdValue.GetString(), out var userId))
            {
                return false;
            }

            return configuration.IsSyncEnabledForUser(userId);
        }

        if (segments.Length == 3 &&
            segments[1].Equals("UserMappings", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("BulkEnabled", StringComparison.OrdinalIgnoreCase) &&
            context.ActionArguments.TryGetValue("request", out var bulkArgument) &&
            bulkArgument is HueUserMappingBulkEnabledRequest bulkRequest &&
            !bulkRequest.SyncEnabled)
        {
            var requestedMappingIds = (bulkRequest.MappingIds ?? new List<string>())
                .Where(mappingId => !string.IsNullOrWhiteSpace(mappingId))
                .Select(mappingId => mappingId.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var requestedUserIds = (bulkRequest.UserIds ?? new List<string>())
                .Where(userId => !string.IsNullOrWhiteSpace(userId))
                .Select(userId => userId.Trim())
                .ToArray();

            return (configuration.UserMappings ?? new List<UserBridgeMapping>())
                .Where(mapping => mapping != null &&
                    ((requestedMappingIds.Count > 0 &&
                      requestedMappingIds.Contains(mapping.MappingId?.Trim() ?? string.Empty)) ||
                     (requestedMappingIds.Count == 0 &&
                      requestedUserIds.Any(userId =>
                          PluginConfiguration.AreSameJellyfinUserId(mapping.UserId, userId)))))
                .Any(mapping =>
                    TryParsePlaybackUserId(mapping.UserId, out var userId) &&
                    configuration.IsSyncEnabledForUser(userId));
        }

        return false;
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

    private static bool TryParsePlaybackUserId(string? value, out Guid userId)
        => Guid.TryParse(value?.Trim(), out userId) && userId != Guid.Empty;
}
