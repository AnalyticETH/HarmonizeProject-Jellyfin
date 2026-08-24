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
    private readonly HashSet<string> _diagnosticResources = new(StringComparer.OrdinalIgnoreCase);
    private bool _unscopedPlaybackActive;
    private bool _unscopedDiagnosticActive;
    private bool _configurationMutationActive;

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
    /// Gets whether playback currently owns the supplied bridge/area resource. An
    /// unscoped playback lease conflicts with every resource; a null key returns the
    /// process-wide state exposed by <see cref="IsPlaybackActive"/>.
    /// </summary>
    public bool IsPlaybackActiveForResource(string? resourceKey)
    {
        lock (_sync)
        {
            return resourceKey == null
                ? IsPlaybackActiveLocked()
                : _unscopedPlaybackActive || _playbackResources.Contains(resourceKey);
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
                return IsDiagnosticActiveLocked();
            }
        }
    }

    /// <summary>
    /// Gets whether a configuration mutation currently owns the lifecycle gate. While
    /// held, no new playback or diagnostic lifecycle may reserve a bridge resource.
    /// </summary>
    public bool IsConfigurationMutationActive
    {
        get
        {
            lock (_sync)
            {
                return _configurationMutationActive;
            }
        }
    }

    /// <summary>
    /// Attempts to reserve the process-wide lifecycle gate for an atomic configuration
    /// mutation. The reservation succeeds only when no playback or diagnostic lifecycle
    /// is active, and remains held until the returned lease is disposed. This closes the
    /// check-then-start race where playback could begin after an import's active check.
    /// </summary>
    public IDisposable? TryEnterConfigurationMutation()
    {
        lock (_sync)
        {
            if (_configurationMutationActive ||
                IsPlaybackActiveLocked() ||
                IsDiagnosticActiveLocked())
            {
                return null;
            }

            _configurationMutationActive = true;
            return new LifecycleLease(this, LifecycleKind.ConfigurationMutation, resourceKey: null);
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
            if (_configurationMutationActive)
                return null;

            var blockedByDiagnostic = resourceKey == null
                ? IsDiagnosticActiveLocked()
                : _unscopedDiagnosticActive || _diagnosticResources.Contains(resourceKey);
            var blockedByPlayback = resourceKey == null
                ? IsPlaybackActiveLocked()
                : _unscopedPlaybackActive || _playbackResources.Contains(resourceKey);
            if (blockedByDiagnostic || blockedByPlayback)
                return null;

            if (resourceKey == null)
                _unscopedPlaybackActive = true;
            else
                _playbackResources.Add(resourceKey);

            return new LifecycleLease(this, LifecycleKind.Playback, resourceKey);
        }
    }

    /// <summary>
    /// Attempts to reserve the bridge for one diagnostic probe or preview until the
    /// returned lease is disposed.
    /// </summary>
    public IDisposable? TryEnterDiagnostic()
        => TryEnterDiagnostic(resourceKey: null);

    /// <summary>
    /// Attempts to reserve one bridge/entertainment-area target for a diagnostic or
    /// restorative preview until the returned lease is disposed. Scoped diagnostics
    /// may run beside playback on a different target, while the legacy unscoped
    /// overload continues to reserve the entire bridge process.
    /// </summary>
    public IDisposable? TryEnterDiagnostic(string? resourceKey)
    {
        lock (_sync)
        {
            if (_configurationMutationActive)
                return null;

            var blockedByPlayback = resourceKey == null
                ? IsPlaybackActiveLocked()
                : _unscopedPlaybackActive || _playbackResources.Contains(resourceKey);
            var blockedByDiagnostic = resourceKey == null
                ? IsDiagnosticActiveLocked()
                : _unscopedDiagnosticActive || _diagnosticResources.Contains(resourceKey);
            if (blockedByPlayback || blockedByDiagnostic)
                return null;

            if (resourceKey == null)
                _unscopedDiagnosticActive = true;
            else
                _diagnosticResources.Add(resourceKey);

            return new LifecycleLease(this, LifecycleKind.Diagnostic, resourceKey);
        }
    }

    private bool IsPlaybackActiveLocked() =>
        _unscopedPlaybackActive || _playbackResources.Count > 0;

    private bool IsDiagnosticActiveLocked() =>
        _unscopedDiagnosticActive || _diagnosticResources.Count > 0;

    private void Exit(LifecycleKind kind, string? resourceKey)
    {
        lock (_sync)
        {
            if (kind == LifecycleKind.ConfigurationMutation)
            {
                _configurationMutationActive = false;
            }
            else if (kind == LifecycleKind.Playback)
            {
                if (resourceKey == null)
                    _unscopedPlaybackActive = false;
                else
                    _playbackResources.Remove(resourceKey);
            }
            else
            {
                if (resourceKey == null)
                    _unscopedDiagnosticActive = false;
                else
                    _diagnosticResources.Remove(resourceKey);
            }
        }
    }

    private sealed class LifecycleLease : IDisposable
    {
        private HueBridgeLifecycleGate? _owner;
        private readonly LifecycleKind _kind;
        private readonly string? _resourceKey;

        public LifecycleLease(HueBridgeLifecycleGate owner, LifecycleKind kind, string? resourceKey)
        {
            _owner = owner;
            _kind = kind;
            _resourceKey = resourceKey;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Exit(_kind, _resourceKey);
        }
    }

    private enum LifecycleKind
    {
        Playback,
        Diagnostic,
        ConfigurationMutation
    }
}
