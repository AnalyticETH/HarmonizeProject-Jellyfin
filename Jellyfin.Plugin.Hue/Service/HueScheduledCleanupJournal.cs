using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.Hue.Configuration;
using Jellyfin.Plugin.Hue.Hue;

namespace Jellyfin.Plugin.Hue.Service;

/// <summary>
/// Carries the non-secret target reference used while one scheduled preview owns a
/// captured light-state snapshot. The journal never stores bridge credentials.
/// </summary>
internal sealed class HueScheduledCleanupScope
{
    public string CleanupId { get; init; } = string.Empty;
    public string ScheduleId { get; init; } = string.Empty;
    public string TargetUserId { get; init; } = string.Empty;
    public string TargetDeviceId { get; init; } = string.Empty;
    public string BridgeIp { get; init; } = string.Empty;
    public string EntertainmentAreaId { get; init; } = string.Empty;
    public IReadOnlySet<int>? ChannelIds { get; init; }
}

/// <summary>
/// Durable, credential-free cleanup journal for scheduled Hue previews. A snapshot is
/// written before a scheduled preview activates the bridge and is removed only after
/// deactivation and light restoration both complete. Failed recovery attempts remain
/// persisted with bounded exponential backoff.
/// </summary>
public sealed class HueScheduledCleanupJournal
{
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HueBridgeLifecycleGate _bridgeLifecycleGate;
    private readonly AsyncLocal<HueScheduledCleanupScope?> _currentScope = new();
    private readonly object _sync = new();

    public HueScheduledCleanupJournal(HueBridgeLifecycleGate bridgeLifecycleGate)
    {
        _bridgeLifecycleGate = bridgeLifecycleGate ?? throw new ArgumentNullException(nameof(bridgeLifecycleGate));
    }

    /// <summary>
    /// Associates a scheduled preview with a cleanup record for the current async flow.
    /// Nested scopes restore the previous scope when disposed.
    /// </summary>
    internal IDisposable BeginScope(HueScheduledCleanupScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var previous = _currentScope.Value;
        _currentScope.Value = scope;
        return new ScopeLease(this, previous);
    }

    /// <summary>
    /// Persists the captured light states before bridge activation. Returning false is a
    /// fail-closed signal: callers must not mutate the bridge when no durable recovery
    /// record exists.
    /// </summary>
    internal bool Capture(IReadOnlyList<HueClient.LightState> states)
    {
        var scope = _currentScope.Value;
        if (scope == null)
            return true;

        if (!TrySerializeStates(states, out var serializedStates))
            return false;

        var config = Plugin.Instance?.Configuration;
        if (config == null ||
            string.IsNullOrWhiteSpace(scope.CleanupId) ||
            string.IsNullOrWhiteSpace(scope.ScheduleId) ||
            string.IsNullOrWhiteSpace(scope.BridgeIp) ||
            string.IsNullOrWhiteSpace(scope.EntertainmentAreaId))
        {
            return false;
        }

        var capturedAtUtc = DateTime.UtcNow;
        var entry = new HueSceneAutomationPendingCleanupEntry
        {
            CleanupId = scope.CleanupId.Trim(),
            ScheduleId = scope.ScheduleId.Trim(),
            TargetUserId = PluginConfiguration.NormalizeJellyfinUserId(scope.TargetUserId),
            TargetDeviceId = scope.TargetDeviceId?.Trim() ?? string.Empty,
            BridgeIp = scope.BridgeIp.Trim(),
            EntertainmentAreaId = scope.EntertainmentAreaId.Trim(),
            ChannelIds = NormalizeChannelIds(scope.ChannelIds),
            LightStatesJson = serializedStates,
            CapturedAtUtc = capturedAtUtc,
            AttemptCount = 0,
            LastAttemptAtUtc = null,
            NextAttemptAtUtc = capturedAtUtc,
            LastError = null
        };

        lock (_bridgeLifecycleGate.HistorySynchronization)
        {
            lock (_sync)
            {
                var previousEntries = CloneEntries(config.PersistedSceneAutomationPendingCleanups);
                var entries = previousEntries
                    .Where(candidate => !string.Equals(candidate.CleanupId, entry.CleanupId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (entries.Count >= PluginConfiguration.MaxSceneAutomationPendingCleanups)
                    return false;

                entries.Add(entry);
                config.PersistedSceneAutomationPendingCleanups = entries;
                if (SaveConfiguration())
                    return true;

                config.PersistedSceneAutomationPendingCleanups = previousEntries;
                return false;
            }
        }
    }

    /// <summary>
    /// Completes the current scheduled cleanup. A null warning removes its durable
    /// snapshot; a warning retains it and records the initial recovery error.
    /// </summary>
    internal bool Complete(string? cleanupWarning)
    {
        var scope = _currentScope.Value;
        if (scope == null || string.IsNullOrWhiteSpace(scope.CleanupId))
            return true;

        var config = Plugin.Instance?.Configuration;
        if (config == null)
            return false;

        lock (_bridgeLifecycleGate.HistorySynchronization)
        {
            lock (_sync)
            {
                var previousEntries = CloneEntries(config.PersistedSceneAutomationPendingCleanups);
                var entries = CloneEntries(previousEntries);
                var key = scope.CleanupId.Trim();
                var index = entries.FindIndex(entry =>
                    string.Equals(entry.CleanupId, key, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                    return true;

                if (string.IsNullOrWhiteSpace(cleanupWarning))
                {
                    entries.RemoveAll(entry =>
                        string.Equals(entry.CleanupId, key, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    entries[index].LastError = NormalizeError(cleanupWarning);
                    entries[index].NextAttemptAtUtc = DateTime.UtcNow;
                }

                config.PersistedSceneAutomationPendingCleanups = entries;
                if (SaveConfiguration())
                    return true;

                config.PersistedSceneAutomationPendingCleanups = previousEntries;
                return false;
            }
        }
    }

    /// <summary>Returns a detached, credential-free snapshot for status and recovery.</summary>
    internal IReadOnlyList<HueSceneAutomationPendingCleanupEntry> Snapshot()
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
            return Array.Empty<HueSceneAutomationPendingCleanupEntry>();

        lock (_bridgeLifecycleGate.HistorySynchronization)
        {
            lock (_sync)
            {
                return CloneEntries(config.PersistedSceneAutomationPendingCleanups);
            }
        }
    }

    /// <summary>Removes a snapshot only after a recovery attempt fully succeeds.</summary>
    internal bool Remove(string cleanupId)
    {
        var key = cleanupId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var config = Plugin.Instance?.Configuration;
        if (config == null)
            return false;

        lock (_bridgeLifecycleGate.HistorySynchronization)
        {
            lock (_sync)
            {
                var previousEntries = CloneEntries(config.PersistedSceneAutomationPendingCleanups);
                var entries = previousEntries
                    .Where(entry => !string.Equals(entry.CleanupId, key, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (entries.Count == previousEntries.Count)
                    return true;

                config.PersistedSceneAutomationPendingCleanups = entries;
                if (SaveConfiguration())
                    return true;

                config.PersistedSceneAutomationPendingCleanups = previousEntries;
                return false;
            }
        }
    }

    /// <summary>
    /// Records a failed recovery attempt and applies a bounded exponential retry delay.
    /// The snapshot remains available even if this metadata write fails.
    /// </summary>
    internal bool RecordFailure(string cleanupId, string? error, DateTime nowUtc)
    {
        var key = cleanupId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var config = Plugin.Instance?.Configuration;
        if (config == null)
            return false;

        var normalizedNow = NormalizeUtc(nowUtc);
        lock (_bridgeLifecycleGate.HistorySynchronization)
        {
            lock (_sync)
            {
                var previousEntries = CloneEntries(config.PersistedSceneAutomationPendingCleanups);
                var entries = CloneEntries(previousEntries);
                var entry = entries.FirstOrDefault(candidate =>
                    string.Equals(candidate.CleanupId, key, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                    return true;

                entry.AttemptCount = Math.Min(
                    PluginConfiguration.MaxSceneAutomationPendingCleanupAttempts,
                    Math.Max(0, entry.AttemptCount) + 1);
                entry.LastAttemptAtUtc = normalizedNow;
                entry.NextAttemptAtUtc = normalizedNow + GetRetryDelay(entry.AttemptCount);
                entry.LastError = NormalizeError(error);
                config.PersistedSceneAutomationPendingCleanups = entries;
                if (SaveConfiguration())
                    return true;

                config.PersistedSceneAutomationPendingCleanups = previousEntries;
                return false;
            }
        }
    }

    /// <summary>Deserializes and validates one credential-free captured state list.</summary>
    internal static bool TryDeserializeStates(
        HueSceneAutomationPendingCleanupEntry entry,
        out List<HueClient.LightState> states,
        out string error)
    {
        states = new List<HueClient.LightState>();
        error = string.Empty;
        if (entry == null || string.IsNullOrWhiteSpace(entry.LightStatesJson))
        {
            error = "The cleanup snapshot is empty.";
            return false;
        }

        if (entry.LightStatesJson.Length > PluginConfiguration.MaxSceneAutomationPendingCleanupJsonLength)
        {
            error = "The cleanup snapshot exceeds the configured safety limit.";
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<PersistedLightState>>(
                entry.LightStatesJson,
                StateJsonOptions);
            if (parsed == null || parsed.Count == 0 || parsed.Count > PluginConfiguration.MaxSceneAutomationPendingCleanupLightStates)
            {
                error = "The cleanup snapshot contains no valid light states.";
                return false;
            }

            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            states = new List<HueClient.LightState>(parsed.Count);
            foreach (var persistedState in parsed)
            {
                if (!IsValidLightState(persistedState, seenIds))
                {
                    error = "The cleanup snapshot contains an invalid or duplicate light state.";
                    return false;
                }

                states.Add(new HueClient.LightState(
                    persistedState.Id!.Trim(),
                    persistedState.IsOn,
                    persistedState.Brightness,
                    persistedState.X,
                    persistedState.Y,
                    persistedState.Mirek,
                    persistedState.HasColor,
                    persistedState.Snapshot));
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException or OverflowException)
        {
            error = "The cleanup snapshot could not be decoded safely.";
            return false;
        }
    }

    private static bool TrySerializeStates(
        IReadOnlyList<HueClient.LightState>? states,
        out string serialized)
    {
        serialized = string.Empty;
        if (states == null ||
            states.Count == 0 ||
            states.Count > PluginConfiguration.MaxSceneAutomationPendingCleanupLightStates)
        {
            return false;
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var persistedStates = new List<PersistedLightState>(states.Count);
        foreach (var state in states)
        {
            if (state == null)
                return false;

            var persistedState = new PersistedLightState
            {
                Id = state.Id,
                IsOn = state.IsOn,
                Brightness = state.Brightness,
                X = state.X,
                Y = state.Y,
                Mirek = state.Mirek,
                HasColor = state.HasColor,
                Snapshot = state.Snapshot
            };
            if (!IsValidLightState(persistedState, seenIds))
            {
                return false;
            }

            persistedStates.Add(persistedState);
        }

        try
        {
            serialized = JsonSerializer.Serialize(persistedStates, StateJsonOptions);
            return serialized.Length <= PluginConfiguration.MaxSceneAutomationPendingCleanupJsonLength;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException or OverflowException)
        {
            serialized = string.Empty;
            return false;
        }
    }

    private static bool IsValidCoordinate(double value)
        => double.IsFinite(value) && value >= 0 && value <= 1;

    private static bool IsValidLightState(
        PersistedLightState? state,
        ISet<string> seenIds)
    {
        if (state == null ||
            string.IsNullOrWhiteSpace(state.Id))
        {
            return false;
        }

        var normalizedId = state.Id.Trim();
        return normalizedId.Length <= 128 &&
               seenIds.Add(normalizedId) &&
               state.Brightness is >= 0 and <= 100 &&
               (!state.Mirek.HasValue || state.Mirek.Value is >= 153 and <= 500) &&
               IsValidCoordinate(state.X) &&
               IsValidCoordinate(state.Y);
    }

    private static string NormalizeChannelIds(IReadOnlySet<int>? channelIds)
        => channelIds == null
            ? string.Empty
            : string.Join(",", channelIds
                .Where(channelId => channelId is >= 0 and <= ushort.MaxValue)
                .OrderBy(channelId => channelId));

    private static TimeSpan GetRetryDelay(int attemptCount)
    {
        var exponent = Math.Clamp(attemptCount - 1, 0, 10);
        var seconds = Math.Min(MaxRetryDelay.TotalSeconds, 60 * Math.Pow(2, exponent));
        return TimeSpan.FromSeconds(seconds);
    }

    private static string? NormalizeError(string? error)
    {
        var normalized = error?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;
        return normalized.Length <= PluginConfiguration.MaxSceneAutomationPendingCleanupErrorLength
            ? normalized
            : normalized[..PluginConfiguration.MaxSceneAutomationPendingCleanupErrorLength];
    }

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? value
            : value.Kind == DateTimeKind.Local
                ? value.ToUniversalTime()
                : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static List<HueSceneAutomationPendingCleanupEntry> CloneEntries(
        IEnumerable<HueSceneAutomationPendingCleanupEntry>? source)
        => (source ?? Enumerable.Empty<HueSceneAutomationPendingCleanupEntry>())
            .Where(entry => entry != null)
            .Select(entry => new HueSceneAutomationPendingCleanupEntry
            {
                CleanupId = entry.CleanupId?.Trim() ?? string.Empty,
                ScheduleId = entry.ScheduleId?.Trim() ?? string.Empty,
                TargetUserId = PluginConfiguration.NormalizeJellyfinUserId(entry.TargetUserId),
                TargetDeviceId = entry.TargetDeviceId?.Trim() ?? string.Empty,
                BridgeIp = entry.BridgeIp?.Trim() ?? string.Empty,
                EntertainmentAreaId = entry.EntertainmentAreaId?.Trim() ?? string.Empty,
                ChannelIds = entry.ChannelIds?.Trim() ?? string.Empty,
                LightStatesJson = entry.LightStatesJson ?? string.Empty,
                CapturedAtUtc = NormalizeUtc(entry.CapturedAtUtc),
                AttemptCount = Math.Clamp(
                    entry.AttemptCount,
                    0,
                    PluginConfiguration.MaxSceneAutomationPendingCleanupAttempts),
                LastAttemptAtUtc = entry.LastAttemptAtUtc.HasValue
                    ? NormalizeUtc(entry.LastAttemptAtUtc.Value)
                    : null,
                NextAttemptAtUtc = entry.NextAttemptAtUtc.HasValue
                    ? NormalizeUtc(entry.NextAttemptAtUtc.Value)
                    : null,
                LastError = NormalizeError(entry.LastError)
            })
            .Take(PluginConfiguration.MaxSceneAutomationPendingCleanups)
            .ToList();

    private static bool SaveConfiguration()
    {
        try
        {
            if (Plugin.Instance == null)
                return false;

            Plugin.Instance.SaveConfiguration();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class PersistedLightState
    {
        public string? Id { get; set; }
        public bool IsOn { get; set; }
        public int Brightness { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public int? Mirek { get; set; }
        public bool HasColor { get; set; } = true;
        public HueClient.LightStateSnapshot? Snapshot { get; set; }
    }

    private sealed class ScopeLease : IDisposable
    {
        private HueScheduledCleanupJournal? _owner;
        private readonly HueScheduledCleanupScope? _previous;

        public ScopeLease(HueScheduledCleanupJournal owner, HueScheduledCleanupScope? previous)
        {
            _owner = owner;
            _previous = previous;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner != null)
                owner._currentScope.Value = _previous;
        }
    }
}
