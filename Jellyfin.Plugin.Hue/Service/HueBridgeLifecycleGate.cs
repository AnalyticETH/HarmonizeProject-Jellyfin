using System;
using System.Collections.Generic;
using System.Threading;

namespace Jellyfin.Plugin.Hue.Service;

/// <summary>
/// Coordinates bridge-mutating playback and diagnostic lifecycles. A single gate is
/// shared by the hosted playback service and the API diagnostic tester so a request
/// cannot pass a point-in-time <c>IsSyncing</c> check and then race playback startup.
/// Distinct bridge/entertainment-area resources may be owned by independent playback
/// sessions at the same time.
/// </summary>
public sealed class HueBridgeLifecycleGate
{
    private readonly object _sync = new();
    private readonly HashSet<string> _playbackResources = new(StringComparer.OrdinalIgnoreCase);
    private bool _unscopedPlaybackActive;
    private bool _diagnosticActive;

    /// <summary>
    /// Gets whether a playback lifecycle currently owns at least one bridge resource.
    /// </summary>
    public bool IsPlaybackActive
    {
        get
        {
            lock (_sync)
            {
                return IsPlaybackActiveLocked();
            }
        }
    }

    /// <summary>
    /// Gets whether a diagnostic lifecycle currently owns the bridge.
    /// </summary>
    public bool IsDiagnosticActive
    {
        get
        {
            lock (_sync)
            {
                return _diagnosticActive;
            }
        }
    }

    /// <summary>
    /// Attempts to reserve the bridge for playback until the returned lease is disposed.
    /// This legacy overload reserves the entire process, preserving the behavior expected
    /// by callers that do not identify a bridge target.
    /// </summary>
    public IDisposable? TryEnterPlayback() => TryEnterPlayback(resourceKey: null);

    /// <summary>
    /// Attempts to reserve one bridge/entertainment-area target for playback until the
    /// returned lease is disposed. Distinct targets may stream concurrently, while a
    /// second lifecycle for the same target is rejected.
    /// </summary>
    public IDisposable? TryEnterPlayback(string? resourceKey)
    {
        lock (_sync)
        {
            if (_diagnosticActive ||
                (resourceKey == null
                    ? IsPlaybackActiveLocked()
                    : _unscopedPlaybackActive || _playbackResources.Contains(resourceKey)))
                return null;

            if (resourceKey == null)
                _unscopedPlaybackActive = true;
            else
                _playbackResources.Add(resourceKey);

            return new LifecycleLease(this, isPlayback: true, resourceKey: resourceKey);
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
            if (IsPlaybackActiveLocked() || _diagnosticActive)
                return null;

            _diagnosticActive = true;
            return new LifecycleLease(this, isPlayback: false, resourceKey: null);
        }
    }

    private bool IsPlaybackActiveLocked() =>
        _unscopedPlaybackActive || _playbackResources.Count > 0;

    private void Exit(bool isPlayback, string? resourceKey)
    {
        lock (_sync)
        {
            if (isPlayback)
            {
                if (resourceKey == null)
                    _unscopedPlaybackActive = false;
                else
                    _playbackResources.Remove(resourceKey);
            }
            else
                _diagnosticActive = false;
        }
    }

    private sealed class LifecycleLease : IDisposable
    {
        private HueBridgeLifecycleGate? _owner;
        private readonly bool _isPlayback;
        private readonly string? _resourceKey;

        public LifecycleLease(HueBridgeLifecycleGate owner, bool isPlayback, string? resourceKey)
        {
            _owner = owner;
            _isPlayback = isPlayback;
            _resourceKey = resourceKey;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Exit(_isPlayback, _resourceKey);
        }
    }
}
