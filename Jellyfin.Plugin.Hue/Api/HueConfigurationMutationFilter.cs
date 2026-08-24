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

        var route = path?.TrimEnd('/') ?? string.Empty;
        if (route.Equals("/HueSync/Configuration", StringComparison.OrdinalIgnoreCase) ||
            route.Equals("/HueSync/History", StringComparison.OrdinalIgnoreCase) ||
            route.Equals("/HueSync/SceneSchedules/History", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (route.StartsWith("/HueSync/Configuration/", StringComparison.OrdinalIgnoreCase))
            return false;

        if (route.StartsWith("/HueSync/UserMappings", StringComparison.OrdinalIgnoreCase))
            return true;

        if (route.StartsWith("/HueSync/ColorPresets", StringComparison.OrdinalIgnoreCase))
        {
            return !route.Contains("/Preview", StringComparison.OrdinalIgnoreCase);
        }

        if (route.StartsWith("/HueSync/ScenePlaylists", StringComparison.OrdinalIgnoreCase))
        {
            return !route.Contains("/Preview", StringComparison.OrdinalIgnoreCase);
        }

        if (route.StartsWith("/HueSync/SceneSchedules", StringComparison.OrdinalIgnoreCase))
        {
            return !route.Contains("/Run", StringComparison.OrdinalIgnoreCase) &&
                !route.Contains("/Cancel", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
