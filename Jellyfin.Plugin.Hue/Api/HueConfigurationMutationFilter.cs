using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.Hue.Service;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.Hue.Api;

/// <summary>
/// Serializes administrator configuration writers with configuration import and the
/// scheduler's evaluation barrier. Read-only, bridge-lifecycle, and cue-run actions are
/// intentionally excluded so a configuration write never holds the bridge gate across a
/// long-running preview or playback operation.
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

        var lease = _bridgeLifecycleGate.TryEnterConfigurationMutation();
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
}
