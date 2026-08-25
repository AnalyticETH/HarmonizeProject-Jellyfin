using System;
using System.Threading;

namespace Jellyfin.Plugin.Hue.Service;

/// <summary>
/// Bounds bridge cleanup without linking it to the request or playback cancellation token.
/// Cleanup must survive caller cancellation long enough to restore the bridge, but it must
/// also release the lifecycle lease when a bridge is unreachable.
/// </summary>
internal static class HueCleanupBudget
{
    internal const int TimeoutSeconds = 30;

    internal static CancellationTokenSource CreateCancellationSource()
        => new(TimeSpan.FromSeconds(TimeoutSeconds));
}
