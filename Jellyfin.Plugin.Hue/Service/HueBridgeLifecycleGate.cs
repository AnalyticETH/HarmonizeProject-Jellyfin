using System;
using System.Threading;

namespace Jellyfin.Plugin.Hue.Service;

/// <summary>
/// Coordinates bridge-mutating playback and diagnostic lifecycles. A single gate is
/// shared by the hosted playback service and the API diagnostic tester so a request
/// cannot pass a point-in-time <c>IsSyncing</c> check and then race playback startup.
/// </summary>
public sealed class HueBridgeLifecycleGate
{
    private readonly object _sync = new();
    private bool _playbackActive;
    private bool _diagnosticActive;

    /// <summary>
    /// Attempts to reserve the bridge for playback until the returned lease is disposed.
    /// </summary>
    public IDisposable? TryEnterPlayback()
    {
        lock (_sync)
        {
            if (_playbackActive || _diagnosticActive)
                return null;

            _playbackActive = true;
            return new LifecycleLease(this, isPlayback: true);
        }
    }

    /// <summary>
    /// Attempts to reserve the bridge for one diagnostic probe or preview until the
    /// returned lease is disposed.
    /// </summary>
    public IDisposable? TryEnterDiagnostic()
    {
        lock (_sync)
        {
            if (_playbackActive || _diagnosticActive)
                return null;

            _diagnosticActive = true;
            return new LifecycleLease(this, isPlayback: false);
        }
    }

    private void Exit(bool isPlayback)
    {
        lock (_sync)
        {
            if (isPlayback)
                _playbackActive = false;
            else
                _diagnosticActive = false;
        }
    }

    private sealed class LifecycleLease : IDisposable
    {
        private HueBridgeLifecycleGate? _owner;
        private readonly bool _isPlayback;

        public LifecycleLease(HueBridgeLifecycleGate owner, bool isPlayback)
        {
            _owner = owner;
            _isPlayback = isPlayback;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Exit(_isPlayback);
        }
    }
}
