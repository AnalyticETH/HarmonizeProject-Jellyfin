using System;
using System.Threading;

namespace Jellyfin.Plugin.Hue.Service;

/// <summary>
/// Bounds bridge cleanup with a short deadline. Callers may additionally link a host
/// shutdown token when waiting for cleanup must not delay application termination.
/// A bridge that is unreachable still releases the lifecycle lease at the deadline.
/// </summary>
internal static class HueCleanupBudget
{
    internal const int TimeoutSeconds = 30;

    internal static CancellationTokenSource CreateCancellationSource()
        => new(TimeSpan.FromSeconds(TimeoutSeconds));
}
