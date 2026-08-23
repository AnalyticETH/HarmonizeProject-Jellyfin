using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Hue.Configuration;
using Jellyfin.Plugin.Hue.Hue;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Hue.Service;

/// <summary>
/// Runs credential-free scheduled color-scene cues. Each cue reuses the existing
/// non-destructive preview lifecycle, so the selected lights are captured, displayed for
/// the saved scene duration, deactivated, and restored automatically.
/// </summary>
public sealed class HueSceneAutomationService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private readonly IHueStreamTester _streamTester;
    private readonly HueClient _hueClient;
    private readonly ILogger<HueSceneAutomationService> _logger;
    private readonly HueBridgeLifecycleGate _bridgeLifecycleGate;
    private readonly object _runSlotLock = new();
    private readonly Dictionary<string, DateTime> _lastRunSlots = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _deferredRunLock = new();
    private readonly Dictionary<string, HueSceneDeferredRun> _deferredRuns = new(StringComparer.OrdinalIgnoreCase);
    private bool _deferredRunsLoaded;
    private readonly object _manualRunCancellationLock = new();
    private readonly Dictionary<string, CancellationTokenSource> _manualRunCancellations = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _runtimeStateLock = new();
    private readonly Dictionary<string, HueSceneScheduleRuntimeState> _runtimeStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _historyLock = new();
    private readonly List<HueSceneAutomationRunResult> _runHistory = new();
    private bool _historyLoaded;

    public const int MaxSceneScheduleHistoryCount = PluginConfiguration.MaxSceneScheduleHistoryCount;
    public const int DefaultUpcomingOccurrencesPerSchedule = 5;
    public const int MaxUpcomingOccurrencesPerSchedule = 50;
    public const int DefaultUpcomingHorizonDays = 31;
    public const int MaxUpcomingHorizonDays = 366;
    public const int DefaultConflictLimit = 50;
    public const int MaxConflictLimit = 200;
    public const int DefaultConflictHorizonDays = 31;
    public const int MaxConflictHorizonDays = 366;

    public HueSceneAutomationService(
        IHueStreamTester streamTester,
        HueClient hueClient,
        ILogger<HueSceneAutomationService> logger,
        HueBridgeLifecycleGate? bridgeLifecycleGate = null)
    {
        _streamTester = streamTester ?? throw new ArgumentNullException(nameof(streamTester));
        _hueClient = hueClient ?? throw new ArgumentNullException(nameof(hueClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _bridgeLifecycleGate = bridgeLifecycleGate ?? new HueBridgeLifecycleGate();
    }

    /// <summary>
    /// Returns credential-free configured schedules for the administrator API.
    /// </summary>
    public IReadOnlyList<HueSceneSchedule> GetSchedules()
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.SceneSchedules == null)
            return Array.Empty<HueSceneSchedule>();

        return config.SceneSchedules
            .Where(schedule => schedule != null)
            .Select(CloneSchedule)
            .OrderBy(schedule => schedule.TimeOfDay, StringComparer.Ordinal)
            .ThenBy(schedule => schedule.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Resets a cue's persisted execution counter and re-enables it. The operation refuses
    /// to mutate an active cue so a reset cannot race with a running bridge lifecycle.
    /// Retained history is intentionally preserved as an audit trail; only the live counter
    /// and last-run pointers are reset.
    /// </summary>
    public bool TryResetScheduleRunCount(string scheduleId, out string message)
        => TryResetSchedulesRunCount(new[] { scheduleId }, out message);

    /// <summary>
    /// Resets several cues' persisted execution counters as one configuration transaction.
    /// Every selected cue is resolved and checked before any state changes are made, so an
    /// active or missing cue cannot leave a partial reset behind. Resetting also re-enables
    /// each cue and clears a pending Skip Next marker; retained history remains an audit trail.
    /// </summary>
    public bool TryResetSchedulesRunCount(
        IEnumerable<string>? scheduleIds,
        out string message)
    {
        message = string.Empty;
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            message = "Plugin configuration is not available.";
            return false;
        }

        var keys = (scheduleIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (keys.Length == 0)
        {
            message = "Select at least one scene schedule.";
            return false;
        }

        if (keys.Length > PluginConfiguration.MaxSceneSchedules)
        {
            message = $"Select no more than {PluginConfiguration.MaxSceneSchedules} scene schedules at once.";
            return false;
        }

        var configuredSchedules = config.SceneSchedules ?? new List<HueSceneSchedule>();
        var selectedSchedules = keys
            .Select(key => configuredSchedules.FirstOrDefault(candidate =>
                candidate != null &&
                string.Equals(candidate.Id?.Trim(), key, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var missingKeys = keys
            .Where((_, index) => selectedSchedules[index] == null)
            .ToArray();
        if (missingKeys.Length > 0)
        {
            message = $"The requested scene schedule(s) were not found: {string.Join(", ", missingKeys)}.";
            return false;
        }

        var schedules = selectedSchedules
            .Where(schedule => schedule != null)
            .Cast<HueSceneSchedule>()
            .ToArray();
        var previousScheduleStates = schedules.ToDictionary(
            schedule => schedule.Id?.Trim() ?? string.Empty,
            schedule => (schedule.RunCount, schedule.Enabled, schedule.SkipNextOccurrence),
            StringComparer.OrdinalIgnoreCase);
        var previousRuntimeStates = new Dictionary<string, HueSceneScheduleRuntimeState?>(StringComparer.OrdinalIgnoreCase);

        lock (_runtimeStateLock)
        {
            var blocked = new List<string>();
            foreach (var schedule in schedules)
            {
                var key = schedule.Id?.Trim() ?? string.Empty;
                previousRuntimeStates[key] = _runtimeStates.TryGetValue(key, out var state)
                    ? state.Clone()
                    : null;
                if (state != null && state.ActiveRuns > 0)
                {
                    blocked.Add($"{schedule.Name}: it is currently running");
                }
            }

            if (blocked.Count > 0)
            {
                message = string.Join("; ", blocked) + ".";
                return false;
            }

            foreach (var schedule in schedules)
            {
                var key = schedule.Id?.Trim() ?? string.Empty;
                if (_runtimeStates.TryGetValue(key, out var state))
                {
                    state.RunCount = 0;
                    state.LastRunAtUtc = null;
                    state.LastSucceeded = null;
                    state.LastSkipped = false;
                    state.LastWasCatchUp = false;
                    state.LastWasDeferred = false;
                    state.LastWasDeferredRestored = false;
                    state.DeferredPending = false;
                    state.DeferredOccurrenceSlot = null;
                    state.DeferredAtLocal = null;
                    state.DeferredUntilLocal = null;
                    state.DeferredRestored = false;
                    state.LastMessage = null;
                    state.LastCleanupWarning = null;
                    state.LastTargetResults = Array.Empty<HueSceneScheduleTargetResult>();
                }

                schedule.RunCount = 0;
                schedule.Enabled = true;
                schedule.SkipNextOccurrence = false;
            }

            try
            {
                Plugin.Instance?.SaveConfiguration();
                message = schedules.Length == 1
                    ? "The scene schedule execution counter was reset and the cue was re-enabled."
                    : $"Reset execution counters and re-enabled {schedules.Length} scheduled cue(s).";
                return true;
            }
            catch (Exception ex)
            {
                foreach (var schedule in schedules)
                {
                    var key = schedule.Id?.Trim() ?? string.Empty;
                    if (previousScheduleStates.TryGetValue(key, out var previousSchedule))
                    {
                        schedule.RunCount = previousSchedule.RunCount;
                        schedule.Enabled = previousSchedule.Enabled;
                        schedule.SkipNextOccurrence = previousSchedule.SkipNextOccurrence;
                    }

                    if (previousRuntimeStates.TryGetValue(key, out var previousState))
                    {
                        if (previousState == null)
                            _runtimeStates.Remove(key);
                        else
                            _runtimeStates[key] = previousState.Clone();
                    }
                }

                _logger.LogWarning(ex, "Could not persist reset for Hue scene schedules");
                message = "The selected scene schedule counters could not be reset because the configuration could not be saved; no changes were retained.";
                return false;
            }
        }
    }

    /// <summary>
    /// Enables or disables one cue without changing its schedule definition. The action
    /// refuses to race an active bridge lifecycle and will not bypass an exhausted finite
    /// execution limit; administrators must reset that counter explicitly first.
    /// </summary>
    public bool TrySetScheduleEnabled(string scheduleId, bool enabled, out string message)
    {
        message = string.Empty;
        var config = Plugin.Instance?.Configuration;
        var key = scheduleId?.Trim() ?? string.Empty;
        var schedule = config?.SceneSchedules?.FirstOrDefault(candidate =>
            candidate != null &&
            string.Equals(candidate.Id?.Trim(), key, StringComparison.OrdinalIgnoreCase));
        if (schedule == null)
        {
            message = "The requested scene schedule was not found.";
            return false;
        }

        var previousEnabled = schedule.Enabled;
        lock (_runtimeStateLock)
        {
            if (_runtimeStates.TryGetValue(key, out var state) && state.ActiveRuns > 0)
            {
                message = "The scene schedule cannot be enabled or disabled while it is running.";
                return false;
            }

            var currentRunCount = _runtimeStates.TryGetValue(key, out state)
                ? state.RunCount
                : Math.Max(0, schedule.RunCount);
            if (enabled && schedule.MaxRuns > 0 && currentRunCount >= schedule.MaxRuns)
            {
                message = "The scene schedule has reached its execution limit. Reset its run counter before enabling it.";
                return false;
            }

            schedule.Enabled = enabled;
        }

        try
        {
            Plugin.Instance?.SaveConfiguration();
            message = enabled
                ? "The scene schedule was enabled without changing its timing or scene."
                : "The scene schedule was disabled without changing its timing or scene.";
            return true;
        }
        catch (Exception ex)
        {
            lock (_runtimeStateLock)
            {
                schedule.Enabled = previousEnabled;
            }

            _logger.LogWarning(ex, "Could not persist enabled state for Hue scene schedule {0}", schedule.Name);
            message = "The scene schedule enabled state could not be saved.";
            return false;
        }
    }

    /// <summary>
    /// Enables or disables several cues as one persistence transaction. Every selected
    /// cue is validated before any state changes are made, so an active cue or an
    /// exhausted finite cue cannot leave a partially updated bulk operation behind.
    /// </summary>
    public bool TrySetSchedulesEnabled(
        IEnumerable<string>? scheduleIds,
        bool enabled,
        out string message)
    {
        message = string.Empty;
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            message = "Plugin configuration is not available.";
            return false;
        }

        var keys = (scheduleIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (keys.Length == 0)
        {
            message = "Select at least one scene schedule.";
            return false;
        }

        var configuredSchedules = config.SceneSchedules ?? new List<HueSceneSchedule>();
        var selectedSchedules = keys
            .Select(key => configuredSchedules.FirstOrDefault(schedule =>
                schedule != null &&
                string.Equals(schedule.Id?.Trim(), key, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var missingKeys = keys
            .Where((_, index) => selectedSchedules[index] == null)
            .ToArray();
        if (missingKeys.Length > 0)
        {
            message = $"The requested scene schedule(s) were not found: {string.Join(", ", missingKeys)}.";
            return false;
        }

        var schedules = selectedSchedules
            .Where(schedule => schedule != null)
            .Cast<HueSceneSchedule>()
            .ToArray();
        var previousEnabled = schedules.ToDictionary(
            schedule => schedule.Id?.Trim() ?? string.Empty,
            schedule => schedule.Enabled,
            StringComparer.OrdinalIgnoreCase);

        lock (_runtimeStateLock)
        {
            var blocked = new List<string>();
            foreach (var schedule in schedules)
            {
                var key = schedule.Id?.Trim() ?? string.Empty;
                if (_runtimeStates.TryGetValue(key, out var state) && state.ActiveRuns > 0)
                {
                    blocked.Add($"{schedule.Name}: it is currently running");
                    continue;
                }

                var currentRunCount = _runtimeStates.TryGetValue(key, out state)
                    ? state.RunCount
                    : Math.Max(0, schedule.RunCount);
                if (enabled && schedule.MaxRuns > 0 && currentRunCount >= schedule.MaxRuns)
                {
                    blocked.Add($"{schedule.Name}: it reached its execution limit; reset its run counter first");
                }
            }

            if (blocked.Count > 0)
            {
                message = string.Join("; ", blocked) + ".";
                return false;
            }

            foreach (var schedule in schedules)
            {
                schedule.Enabled = enabled;
            }
        }

        try
        {
            Plugin.Instance?.SaveConfiguration();
            message = enabled
                ? $"Enabled {schedules.Length} scheduled cue(s) without changing their schedules."
                : $"Disabled {schedules.Length} scheduled cue(s) without changing their schedules.";
            return true;
        }
        catch (Exception ex)
        {
            lock (_runtimeStateLock)
            {
                foreach (var schedule in schedules)
                {
                    var key = schedule.Id?.Trim() ?? string.Empty;
                    if (previousEnabled.TryGetValue(key, out var wasEnabled))
                    {
                        schedule.Enabled = wasEnabled;
                    }
                }
            }

            _logger.LogWarning(ex, "Could not persist bulk enabled state for Hue scene schedules");
            message = "The selected scene schedules could not be saved; no changes were retained.";
            return false;
        }
    }

    /// <summary>
    /// Marks or clears the next automatic occurrence for several cues as one persistence
    /// transaction. Every selected cue is validated before any skip marker changes are
    /// made, so disabled, exhausted, futureless, active, or missing cues cannot leave a
    /// partially updated bulk operation behind. Manual Run Now remains unaffected.
    /// </summary>
    public bool TrySetSchedulesSkipNextOccurrence(
        IEnumerable<string>? scheduleIds,
        bool skip,
        out string message)
    {
        message = string.Empty;
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            message = "Plugin configuration is not available.";
            return false;
        }

        var keys = (scheduleIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (keys.Length == 0)
        {
            message = "Select at least one scene schedule.";
            return false;
        }

        if (keys.Length > PluginConfiguration.MaxSceneSchedules)
        {
            message = $"Select no more than {PluginConfiguration.MaxSceneSchedules} scene schedules at once.";
            return false;
        }

        var configuredSchedules = config.SceneSchedules ?? new List<HueSceneSchedule>();
        var selectedSchedules = keys
            .Select(key => configuredSchedules.FirstOrDefault(schedule =>
                schedule != null &&
                string.Equals(schedule.Id?.Trim(), key, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var missingKeys = keys
            .Where((_, index) => selectedSchedules[index] == null)
            .ToArray();
        if (missingKeys.Length > 0)
        {
            message = $"The requested scene schedule(s) were not found: {string.Join(", ", missingKeys)}.";
            return false;
        }

        var schedules = selectedSchedules
            .Where(schedule => schedule != null)
            .Cast<HueSceneSchedule>()
            .ToArray();
        var previousSkip = schedules.ToDictionary(
            schedule => schedule.Id?.Trim() ?? string.Empty,
            schedule => schedule.SkipNextOccurrence,
            StringComparer.OrdinalIgnoreCase);

        lock (_runtimeStateLock)
        {
            var blocked = new List<string>();
            foreach (var schedule in schedules)
            {
                var key = schedule.Id?.Trim() ?? string.Empty;
                if (_runtimeStates.TryGetValue(key, out var state) && state.ActiveRuns > 0)
                {
                    blocked.Add($"{schedule.Name}: it is currently running");
                    continue;
                }

                // Clearing an existing marker is always safe, and re-applying an existing
                // marker is idempotent. Only a new marker needs occurrence eligibility.
                if (!skip || schedule.SkipNextOccurrence)
                    continue;

                if (!schedule.Enabled)
                {
                    blocked.Add($"{schedule.Name}: it must be enabled before its next occurrence can be skipped");
                    continue;
                }

                var currentRunCount = _runtimeStates.TryGetValue(key, out state)
                    ? state.RunCount
                    : Math.Max(0, schedule.RunCount);
                if (schedule.MaxRuns > 0 && currentRunCount >= schedule.MaxRuns)
                {
                    blocked.Add($"{schedule.Name}: it reached its execution limit; reset its run counter first");
                    continue;
                }

                if (GetNextRunUtc(schedule, DateTime.Now) == null)
                    blocked.Add($"{schedule.Name}: it has no upcoming automatic occurrence to skip");
            }

            if (blocked.Count > 0)
            {
                message = string.Join("; ", blocked) + ".";
                return false;
            }

            foreach (var schedule in schedules)
                schedule.SkipNextOccurrence = skip;
        }

        try
        {
            Plugin.Instance?.SaveConfiguration();
            message = skip
                ? $"Marked the next automatic occurrence for {schedules.Length} scheduled cue(s) to be skipped."
                : $"Cleared the pending skipped occurrence for {schedules.Length} scheduled cue(s).";
            return true;
        }
        catch (Exception ex)
        {
            lock (_runtimeStateLock)
            {
                foreach (var schedule in schedules)
                {
                    var key = schedule.Id?.Trim() ?? string.Empty;
                    if (previousSkip.TryGetValue(key, out var wasSkipped))
                        schedule.SkipNextOccurrence = wasSkipped;
                }
            }

            _logger.LogWarning(ex, "Could not persist bulk skipped occurrence state for Hue scene schedules");
            message = "The selected skipped-occurrence state could not be saved; no changes were retained.";
            return false;
        }
    }

    /// <summary>
    /// Deletes several scene cues as one persistence transaction. Every selected cue is
    /// resolved and checked before the collection changes, and an active cue blocks the
    /// complete operation so a running restorative lifecycle cannot lose its definition.
    /// Retained run history is intentionally preserved as an audit trail.
    /// </summary>
    public bool TryDeleteSchedules(
        IEnumerable<string>? scheduleIds,
        out string message)
    {
        message = string.Empty;
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            message = "Plugin configuration is not available.";
            return false;
        }

        var keys = (scheduleIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (keys.Length == 0)
        {
            message = "Select at least one scene schedule.";
            return false;
        }

        if (keys.Length > PluginConfiguration.MaxSceneSchedules)
        {
            message = $"Select no more than {PluginConfiguration.MaxSceneSchedules} scene schedules at once.";
            return false;
        }

        var previousSchedules = config.SceneSchedules ?? new List<HueSceneSchedule>();
        var selectedSchedules = keys
            .Select(key => previousSchedules.FirstOrDefault(schedule =>
                schedule != null &&
                string.Equals(schedule.Id?.Trim(), key, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var missingKeys = keys
            .Where((_, index) => selectedSchedules[index] == null)
            .ToArray();
        if (missingKeys.Length > 0)
        {
            message = $"The requested scene schedule(s) were not found: {string.Join(", ", missingKeys)}.";
            return false;
        }

        var schedules = selectedSchedules
            .Where(schedule => schedule != null)
            .Cast<HueSceneSchedule>()
            .ToArray();
        lock (_runtimeStateLock)
        {
            var activeSchedules = schedules
                .Where(schedule =>
                {
                    var key = schedule.Id?.Trim() ?? string.Empty;
                    return _runtimeStates.TryGetValue(key, out var state) && state.ActiveRuns > 0;
                })
                .Select(schedule => schedule.Name)
                .ToArray();
            if (activeSchedules.Length > 0)
            {
                message = $"The selected scene schedule(s) are currently running and cannot be deleted: {string.Join(", ", activeSchedules)}.";
                return false;
            }

            var selectedIds = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
            config.SceneSchedules = previousSchedules
                .Where(schedule => schedule == null || !selectedIds.Contains(schedule.Id?.Trim() ?? string.Empty))
                .ToList();
        }

        try
        {
            Plugin.Instance?.SaveConfiguration();
            lock (_runtimeStateLock)
            {
                foreach (var schedule in schedules)
                    _runtimeStates.Remove(schedule.Id?.Trim() ?? string.Empty);
            }

            message = $"Deleted {schedules.Length} scheduled cue(s); retained cue history was preserved.";
            return true;
        }
        catch (Exception ex)
        {
            lock (_runtimeStateLock)
            {
                config.SceneSchedules = previousSchedules;
            }

            _logger.LogWarning(ex, "Could not persist bulk deletion of Hue scene schedules");
            message = "The selected scene schedules could not be deleted; no changes were retained.";
            return false;
        }
    }

    /// <summary>
    /// Marks or clears one upcoming automatic occurrence without changing the cue's
    /// recurrence definition. The next occurrence is consumed by the scheduler only;
    /// manual Run Now remains available. Active, exhausted, or otherwise idle cues are
    /// rejected when a new skip is requested so an administrator cannot create a silent
    /// state with no upcoming event to consume.
    /// </summary>
    public bool TrySetScheduleSkipNextOccurrence(string scheduleId, bool skip, out string message)
    {
        message = string.Empty;
        var config = Plugin.Instance?.Configuration;
        var key = scheduleId?.Trim() ?? string.Empty;
        var schedule = config?.SceneSchedules?.FirstOrDefault(candidate =>
            candidate != null &&
            string.Equals(candidate.Id?.Trim(), key, StringComparison.OrdinalIgnoreCase));
        if (schedule == null)
        {
            message = "The requested scene schedule was not found.";
            return false;
        }

        var previousSkip = schedule.SkipNextOccurrence;
        lock (_runtimeStateLock)
        {
            if (_runtimeStates.TryGetValue(key, out var state) && state.ActiveRuns > 0)
            {
                message = "The scene schedule cannot change its skipped occurrence while it is running.";
                return false;
            }

            if (skip)
            {
                if (schedule.SkipNextOccurrence)
                {
                    message = "The next automatic scene occurrence is already marked to be skipped.";
                    return true;
                }

                if (!schedule.Enabled)
                {
                    message = "The scene schedule must be enabled before its next occurrence can be skipped.";
                    return false;
                }

                var currentRunCount = _runtimeStates.TryGetValue(key, out state)
                    ? state.RunCount
                    : Math.Max(0, schedule.RunCount);
                if (schedule.MaxRuns > 0 && currentRunCount >= schedule.MaxRuns)
                {
                    message = "The scene schedule has reached its execution limit. Reset its run counter before marking an occurrence to skip.";
                    return false;
                }

                if (GetNextRunUtc(schedule, DateTime.Now) == null)
                {
                    message = "The scene schedule has no upcoming automatic occurrence to skip.";
                    return false;
                }
            }

            schedule.SkipNextOccurrence = skip;
        }

        try
        {
            Plugin.Instance?.SaveConfiguration();
            message = skip
                ? "The next automatic scene occurrence was marked to be skipped."
                : "The pending skipped scene occurrence was restored.";
            return true;
        }
        catch (Exception ex)
        {
            lock (_runtimeStateLock)
            {
                schedule.SkipNextOccurrence = previousSkip;
            }

            _logger.LogWarning(ex, "Could not persist skipped occurrence state for Hue scene schedule {0}", schedule.Name);
            message = "The skipped occurrence state could not be saved.";
            return false;
        }
    }

    /// <summary>
    /// Returns the newest sanitized scheduled-scene run summaries. The optional
    /// schedule filter is matched against the stable cue ID and never against secrets.
    /// </summary>
    public IReadOnlyList<HueSceneAutomationRunResult> GetHistory(
        int limit = MaxSceneScheduleHistoryCount,
        string? scheduleId = null,
        string? outcome = null)
    {
        EnsureHistoryLoaded();
        var boundedLimit = Math.Clamp(limit, 1, MaxSceneScheduleHistoryCount);
        var normalizedScheduleId = scheduleId?.Trim();
        var normalizedOutcome = outcome?.Trim();
        lock (_historyLock)
        {
            return _runHistory
                .Where(result => string.IsNullOrWhiteSpace(normalizedScheduleId) ||
                                 string.Equals(result.ScheduleId, normalizedScheduleId, StringComparison.OrdinalIgnoreCase))
                .Where(result => MatchesHistoryOutcome(result, normalizedOutcome))
                .Take(boundedLimit)
                .Select(CloneRunResult)
                .ToArray();
        }
    }

    /// <summary>
    /// Applies the credential-free outcome vocabulary used by the administrator history
    /// API. Recovered and deferred runs retain their underlying succeeded/failed/skipped
    /// outcome while also matching their corresponding filters.
    /// </summary>
    internal static bool MatchesHistoryOutcome(
        HueSceneAutomationRunResult result,
        string? outcome)
    {
        if (result == null || string.IsNullOrWhiteSpace(outcome))
            return result != null;

        return outcome.Trim().ToLowerInvariant() switch
        {
            "succeeded" => result.Succeeded && !result.Skipped,
            "failed" => !result.Succeeded && !result.Skipped,
            "skipped" => result.Skipped,
            "recovered" => result.WasCatchUp,
            "deferred" => result.WasDeferred,
            _ => false
        };
    }

    /// <summary>
    /// Clears retained scheduled-scene history without stopping an active cue. Last-run
    /// pointers are reset while an in-flight run remains marked as active until its normal
    /// completion; persisted finite execution counters remain intact so clearing telemetry
    /// cannot bypass a configured run limit.
    /// </summary>
    public int ClearHistory()
    {
        EnsureHistoryLoaded();
        var config = Plugin.Instance?.Configuration;
        int clearedCount;
        lock (_historyLock)
        {
            clearedCount = _runHistory.Count;
            _runHistory.Clear();
        }

        lock (_runtimeStateLock)
        {
            foreach (var entry in _runtimeStates)
            {
                var configuredSchedule = config?.SceneSchedules?.FirstOrDefault(schedule =>
                    schedule != null &&
                    string.Equals(schedule.Id?.Trim(), entry.Key, StringComparison.OrdinalIgnoreCase));
                var state = entry.Value;
                state.RunCount = 0;
                if (configuredSchedule?.MaxRuns > 0)
                    state.RunCount = Math.Max(0, configuredSchedule.RunCount);
                state.LastRunAtUtc = null;
                state.LastSucceeded = null;
                state.LastSkipped = false;
                state.LastWasCatchUp = false;
                state.LastWasDeferred = false;
                state.LastWasDeferredRestored = false;
                state.DeferredPending = false;
                state.DeferredOccurrenceSlot = null;
                state.DeferredAtLocal = null;
                state.DeferredUntilLocal = null;
                state.DeferredRestored = false;
                state.LastMessage = null;
                state.LastCleanupWarning = null;
            }
        }

        PersistSceneScheduleHistory();
        return clearedCount;
    }

    /// <summary>
    /// Reconciles persisted cue history with the current administrator setting. This is
    /// called after configuration changes so enabling retention captures the current
    /// in-memory window immediately and disabling retention removes stored entries.
    /// </summary>
    public void RefreshSceneScheduleHistoryPersistence()
    {
        EnsureHistoryLoaded();
        lock (_historyLock)
        {
            TrimRunHistoryLocked();
        }

        PersistSceneScheduleHistory();
    }

    /// <summary>
    /// Returns sanitized runtime telemetry for the configured cues. Credentials and
    /// bridge connection details never enter this snapshot; target labels are derived
    /// from the current mapping names only.
    /// </summary>
    public HueSceneAutomationStatus GetStatus()
    {
        EnsureHistoryLoaded();
        EnsureDeferredRunsLoaded();
        var localNow = DateTime.Now;
        var config = Plugin.Instance?.Configuration;
        var playbackPolicy = PluginConfiguration.TryNormalizeSceneAutomationPlaybackPolicy(
            config?.SceneAutomationPlaybackPolicy,
            out var normalizedPlaybackPolicy)
            ? normalizedPlaybackPolicy
            : PluginConfiguration.SceneAutomationPlaybackPolicySkip;
        var playbackScope = GetPlaybackConflictScope(config);
        var deferMinutes = Math.Clamp(
            config?.SceneAutomationDeferMinutes ?? PluginConfiguration.DefaultSceneAutomationDeferMinutes,
            PluginConfiguration.MinSceneAutomationDeferMinutes,
            PluginConfiguration.MaxSceneAutomationDeferMinutes);
        var schedules = config?.SceneSchedules?
            .Where(schedule => schedule != null)
            .Select(CloneSchedule)
            .OrderBy(schedule => schedule.TimeOfDay, StringComparer.Ordinal)
            .ThenBy(schedule => schedule.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<HueSceneSchedule>();

        PruneDeferredRuns(schedules, config);

        var configuredIds = schedules
            .Select(schedule => schedule.Id?.Trim() ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_runtimeStateLock)
        {
            foreach (var staleId in _runtimeStates
                         .Where(entry => entry.Value.ActiveRuns == 0 && !configuredIds.Contains(entry.Key))
                         .Select(entry => entry.Key)
                         .ToArray())
            {
                _runtimeStates.Remove(staleId);
            }
        }

        var statuses = schedules.Select(schedule =>
        {
            var runtime = GetRuntimeState(schedule.Id);
            var readiness = EvaluateReadiness(config, schedule);
            var preset = config?.ColorPresets?.FirstOrDefault(candidate =>
                candidate != null &&
                string.Equals(candidate.Name?.Trim(), schedule.PresetName?.Trim(), StringComparison.OrdinalIgnoreCase));
            var playlist = config?.ScenePlaylists?.FirstOrDefault(candidate =>
                candidate != null &&
                string.Equals(candidate.Name?.Trim(), schedule.PlaylistName?.Trim(), StringComparison.OrdinalIgnoreCase));
            var isPlaylist = !string.IsNullOrWhiteSpace(schedule.PlaylistName);
            var effect = isPlaylist
                ? PluginConfiguration.SceneScheduleEffectPlaylist
                : PluginConfiguration.TryNormalizeColorPresetEffect(preset?.Effect, out var normalizedEffect)
                    ? normalizedEffect
                    : PluginConfiguration.ColorPresetEffectSolid;
            PluginConfiguration.TryNormalizeSceneScheduleRecurrence(schedule.Recurrence, out var recurrence);
            var timeZone = PluginConfiguration.TryResolveSceneScheduleTimeZone(schedule.TimeZoneId, out var resolvedTimeZone)
                ? resolvedTimeZone
                : TimeZoneInfo.Local;
            return new HueSceneScheduleRuntimeStatus
            {
                ScheduleId = schedule.Id?.Trim() ?? string.Empty,
                ScheduleName = schedule.Name?.Trim() ?? string.Empty,
                PresetName = schedule.PresetName?.Trim() ?? string.Empty,
                PlaylistName = schedule.PlaylistName?.Trim() ?? string.Empty,
                Priority = schedule.Priority,
                PlaybackPolicy = GetEffectivePlaybackPolicy(config, schedule),
                PlaybackPolicyOverride = NormalizeSchedulePlaybackPolicy(schedule.PlaybackPolicy),
                Effect = effect,
                EffectSpeedPercent = isPlaylist || preset == null
                    ? PluginConfiguration.DefaultColorPresetEffectSpeedPercent
                    : PluginConfiguration.ClampColorPresetEffectSpeedPercent(preset.EffectSpeedPercent),
                PlaylistStepCount = playlist?.PresetNames?.Count ?? 0,
                PlaylistRepeatCount = playlist == null
                    ? PluginConfiguration.DefaultScenePlaylistRepeatCount
                    : Math.Clamp(
                        playlist.RepeatCount,
                        PluginConfiguration.MinScenePlaylistRepeatCount,
                        PluginConfiguration.MaxScenePlaylistRepeatCount),
                PlaylistTotalDurationSeconds = isPlaylist
                    ? GetPlaylistTotalDurationSeconds(config, playlist)
                    : 0,
                DurationSeconds = isPlaylist
                    ? GetPlaylistTotalDurationSeconds(config, playlist)
                    : GetEffectiveDurationSeconds(schedule, preset),
                TransitionSeconds = isPlaylist ? 0 : GetEffectiveTransitionSeconds(schedule, preset),
                TransitionOutSeconds = isPlaylist ? 0 : GetEffectiveTransitionOutSeconds(schedule, preset),
                Recurrence = recurrence,
                RecurrenceInterval = schedule.RecurrenceInterval,
                MaxRuns = schedule.MaxRuns,
                RunCount = runtime.RunCount,
                RemainingRuns = schedule.MaxRuns > 0
                    ? Math.Max(0, schedule.MaxRuns - runtime.RunCount)
                    : null,
                DayOfMonth = schedule.DayOfMonth,
                MonthOfYear = schedule.MonthOfYear,
                WeekOfMonth = schedule.WeekOfMonth,
                DayOfWeek = schedule.DayOfWeek,
                TargetAllEnabledMappings = schedule.TargetAllEnabledMappings,
                TargetUserIds = schedule.TargetUserIds?.Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                    ?? Array.Empty<string>(),
                IncludeDefaultTarget = schedule.IncludeDefaultTarget,
                TargetLabel = ResolveTargetLabel(config, schedule),
                TimeOfDay = schedule.TimeOfDay?.Trim() ?? string.Empty,
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
                TimeZoneDisplayName = string.IsNullOrWhiteSpace(schedule.TimeZoneId)
                    ? $"Server local ({timeZone.DisplayName})"
                    : timeZone.DisplayName,
                RunDate = schedule.RunDate?.Trim() ?? string.Empty,
                StartDate = schedule.StartDate?.Trim() ?? string.Empty,
                EndDate = schedule.EndDate?.Trim() ?? string.Empty,
                ExcludedDates = PluginConfiguration.TryNormalizeSceneScheduleExcludedDates(
                    schedule.ExcludedDates,
                    out var excludedDates)
                    ? excludedDates
                    : Array.Empty<string>(),
                DaysOfWeekMask = schedule.DaysOfWeekMask,
                Enabled = schedule.Enabled,
                SkipNextOccurrence = schedule.SkipNextOccurrence,
                Ready = readiness.Ready,
                ReadinessMessage = readiness.Message,
                NextRunLocal = GetNextRunLocal(schedule, localNow),
                NextRunUtc = GetNextRunUtc(schedule, localNow),
                LastRunAtUtc = runtime.LastRunAtUtc,
                LastSucceeded = runtime.LastSucceeded,
                LastSkipped = runtime.LastSkipped,
                LastWasCatchUp = runtime.LastWasCatchUp,
                LastWasDeferred = runtime.LastWasDeferred,
                LastWasDeferredRestored = runtime.LastWasDeferredRestored,
                DeferredPending = runtime.DeferredPending,
                DeferredRestored = runtime.DeferredRestored,
                DeferredOccurrenceSlot = runtime.DeferredOccurrenceSlot,
                DeferredUntilLocal = runtime.DeferredUntilLocal,
                LastMessage = runtime.LastMessage,
                LastCleanupWarning = runtime.LastCleanupWarning,
                LastTargetResults = runtime.LastTargetResults?.Select(CloneTargetResult).ToArray()
                    ?? Array.Empty<HueSceneScheduleTargetResult>(),
                IsRunning = runtime.ActiveRuns > 0
            };
        }).ToArray();

        return new HueSceneAutomationStatus
        {
            ServiceAvailable = true,
            AutomationEnabled = config?.SceneAutomationEnabled ?? true,
            CatchUpMinutes = Math.Clamp(
                config?.SceneAutomationCatchUpMinutes ?? PluginConfiguration.MinSceneAutomationCatchUpMinutes,
                PluginConfiguration.MinSceneAutomationCatchUpMinutes,
                PluginConfiguration.MaxSceneAutomationCatchUpMinutes),
            PlaybackPolicy = playbackPolicy,
            PlaybackConflictScope = playbackScope,
            DeferMinutes = deferMinutes,
            PlaybackActive = _bridgeLifecycleGate.IsPlaybackActive,
            GeneratedAtUtc = DateTime.UtcNow,
            ServerLocalNow = DateTime.SpecifyKind(localNow, DateTimeKind.Unspecified),
            ServerTimeZoneId = TimeZoneInfo.Local.Id,
            Schedules = statuses,
            Conflicts = GetUpcomingConflicts(
                config,
                localNow,
                DefaultConflictLimit,
                DefaultConflictHorizonDays)
        };
    }

    /// <summary>
    /// Calculates the next occurrence in the cue's configured time zone after the
    /// supplied server-local time. The return value intentionally has an unspecified
    /// kind; use <see cref="GetNextRunUtc"/> when an absolute instant is required.
    /// </summary>
    internal static DateTime? GetNextRunLocal(HueSceneSchedule schedule, DateTime localNow)
    {
        return GetNextRun(schedule, localNow, out _);
    }

    /// <summary>
    /// Calculates the absolute UTC instant corresponding to the next cue occurrence.
    /// </summary>
    internal static DateTime? GetNextRunUtc(HueSceneSchedule schedule, DateTime localNow)
    {
        _ = GetNextRun(schedule, localNow, out var nextRunUtc);
        return nextRunUtc;
    }

    /// <summary>
    /// Resolves the hold duration used by a scheduled cue. A zero schedule value keeps
    /// the saved scene's duration so existing configurations remain unchanged.
    /// </summary>
    internal static int GetEffectiveDurationSeconds(HueSceneSchedule schedule, HueColorPreset? preset)
    {
        var duration = schedule?.DurationSeconds > 0
            ? schedule.DurationSeconds
            : preset?.DurationSeconds ?? PluginConfiguration.MinPreviewDurationSeconds;
        return Math.Clamp(
            duration,
            PluginConfiguration.MinPreviewDurationSeconds,
            PluginConfiguration.MaxPreviewDurationSeconds);
    }

    /// <summary>
    /// Resolves the playback policy that applies to one cue. Blank and legacy values
    /// inherit the global setting; malformed optional values safely fall back to the
    /// global policy and are surfaced by configuration validation.
    /// </summary>
    internal static string GetEffectivePlaybackPolicy(
        PluginConfiguration? config,
        HueSceneSchedule? schedule)
    {
        var globalPolicy = PluginConfiguration.TryNormalizeSceneAutomationPlaybackPolicy(
            config?.SceneAutomationPlaybackPolicy,
            out var normalizedGlobal)
            ? normalizedGlobal
            : PluginConfiguration.SceneAutomationPlaybackPolicySkip;
        var schedulePolicy = PluginConfiguration.TryNormalizeSceneAutomationSchedulePlaybackPolicy(
            schedule?.PlaybackPolicy,
            out var normalizedSchedule)
            ? normalizedSchedule
            : PluginConfiguration.SceneAutomationPlaybackPolicyInherit;
        return string.Equals(
            normalizedSchedule,
            PluginConfiguration.SceneAutomationPlaybackPolicyInherit,
            StringComparison.OrdinalIgnoreCase)
            ? globalPolicy
            : normalizedSchedule;
    }

    internal static string NormalizeSchedulePlaybackPolicy(string? value)
    {
        return PluginConfiguration.TryNormalizeSceneAutomationSchedulePlaybackPolicy(
            value,
            out var normalized)
            ? normalized
            : PluginConfiguration.SceneAutomationPlaybackPolicyInherit;
    }

    internal static string GetPlaybackConflictScope(PluginConfiguration? config)
    {
        return PluginConfiguration.TryNormalizeSceneAutomationPlaybackScope(
            config?.SceneAutomationPlaybackScope,
            out var normalized)
            ? normalized
            : PluginConfiguration.SceneAutomationPlaybackScopeAnyTarget;
    }

    private bool IsPlaybackActiveForSchedule(
        PluginConfiguration config,
        HueSceneSchedule schedule,
        string playbackScope)
    {
        if (!string.Equals(
                playbackScope,
                PluginConfiguration.SceneAutomationPlaybackScopeMatchingTarget,
                StringComparison.OrdinalIgnoreCase))
        {
            return _bridgeLifecycleGate.IsPlaybackActive;
        }

        // Target resolution is credential-safe and deduplicates physical bridge/area
        // resources. If a cue is not resolvable, conservatively retain the historical
        // process-wide block; the subsequent readiness/run path will report the actual
        // configuration error without risking a bridge race.
        if (!TryResolveTargets(config, schedule, out var targets, out _))
            return _bridgeLifecycleGate.IsPlaybackActive;

        return targets.Any(target => _bridgeLifecycleGate.IsPlaybackActiveForResource(
            HueSyncService.GetPlaybackResourceKey(target.BridgeIp, target.EntertainmentAreaId)));
    }

    internal static int GetEffectiveTransitionSeconds(HueSceneSchedule schedule, HueColorPreset? preset)
    {
        var transition = preset?.TransitionSeconds ?? PluginConfiguration.MinColorPresetTransitionSeconds;
        return Math.Clamp(
            transition,
            PluginConfiguration.MinColorPresetTransitionSeconds,
            Math.Min(GetEffectiveDurationSeconds(schedule, preset), PluginConfiguration.MaxColorPresetTransitionSeconds));
    }

    internal static int GetEffectiveTransitionOutSeconds(HueSceneSchedule schedule, HueColorPreset? preset)
    {
        var duration = GetEffectiveDurationSeconds(schedule, preset);
        var transitionIn = GetEffectiveTransitionSeconds(schedule, preset);
        var transitionOut = preset?.TransitionOutSeconds ?? PluginConfiguration.MinColorPresetTransitionOutSeconds;
        return Math.Clamp(
            transitionOut,
            PluginConfiguration.MinColorPresetTransitionOutSeconds,
            Math.Min(
                duration - transitionIn,
                PluginConfiguration.MaxColorPresetTransitionOutSeconds));
    }

    /// <summary>
    /// Returns the total hold time represented by a saved-scene playlist, including each
    /// configured repeat pass. Each scene keeps its own saved duration; the total is used
    /// for schedule status, occurrence, and calendar metadata.
    /// </summary>
    internal static int GetPlaylistTotalDurationSeconds(
        PluginConfiguration? config,
        HueScenePlaylist? playlist)
    {
        if (playlist == null)
            return PluginConfiguration.MinPreviewDurationSeconds;

        var total = (playlist.PresetNames ?? new List<string>())
            .Select(name => config?.ColorPresets?.FirstOrDefault(preset =>
                preset != null &&
                string.Equals(preset.Name?.Trim(), name?.Trim(), StringComparison.OrdinalIgnoreCase)))
            .Select(preset => preset == null
                ? PluginConfiguration.MinPreviewDurationSeconds
                : Math.Clamp(
                    preset.DurationSeconds,
                    PluginConfiguration.MinPreviewDurationSeconds,
                    PluginConfiguration.MaxPreviewDurationSeconds))
            .Sum() * Math.Clamp(
                playlist.RepeatCount,
                PluginConfiguration.MinScenePlaylistRepeatCount,
                PluginConfiguration.MaxScenePlaylistRepeatCount);
        return Math.Clamp(
            total,
            PluginConfiguration.MinPreviewDurationSeconds,
            PluginConfiguration.MaxScenePlaylistTotalDurationSeconds);
    }

    /// <summary>
    /// Calculates a bounded preview of future cue occurrences. Calendar dates are
    /// evaluated in the cue's selected time zone, so one-time dates, date windows,
    /// exclusions, daily/weekly/monthly-day/monthly-weekday/yearly recurrence, bounded
    /// recurrence intervals, DST gaps, and weekday masks use the same
    /// rules as the hosted scheduler.
    /// </summary>
    internal static IReadOnlyList<HueSceneScheduleOccurrence> GetUpcomingOccurrences(
        HueSceneSchedule schedule,
        DateTime serverLocalNow,
        int maxOccurrences = DefaultUpcomingOccurrencesPerSchedule,
        int horizonDays = DefaultUpcomingHorizonDays,
        bool includeFutureStartBeyondHorizon = true,
        int transitionSeconds = PluginConfiguration.MinColorPresetTransitionSeconds,
        int transitionOutSeconds = PluginConfiguration.MinColorPresetTransitionOutSeconds,
        int effectSpeedPercent = PluginConfiguration.DefaultColorPresetEffectSpeedPercent,
        int durationSeconds = -1)
    {
        var occurrences = new List<HueSceneScheduleOccurrence>();
        var boundedOccurrences = Math.Clamp(
            maxOccurrences,
            1,
            MaxUpcomingOccurrencesPerSchedule);
        var boundedHorizon = Math.Clamp(
            horizonDays,
            1,
            MaxUpcomingHorizonDays);

        if (schedule == null || !schedule.Enabled ||
            IsRunLimitReached(schedule) ||
            !PluginConfiguration.TryNormalizeSceneScheduleTimeMode(schedule.TimeMode, out var normalizedTimeMode) ||
            !PluginConfiguration.TryResolveSceneScheduleTimeZone(schedule.TimeZoneId, out var timeZone) ||
            (schedule.DurationSeconds != 0 &&
             (schedule.DurationSeconds < PluginConfiguration.MinPreviewDurationSeconds ||
              schedule.DurationSeconds > PluginConfiguration.MaxPreviewDurationSeconds)) ||
            !TryGetScheduleLocalNow(schedule, serverLocalNow, out var scheduleNow, out var serverUtcNow) ||
            !TryGetScheduleDateBounds(schedule, out var startDate, out var endDate) ||
            !PluginConfiguration.TryNormalizeSceneScheduleExcludedDates(schedule.ExcludedDates, out _) ||
            !TryGetScheduleRunDate(schedule, out var runDate) ||
            !PluginConfiguration.TryNormalizeSceneScheduleRecurrence(schedule.Recurrence, out var normalizedRecurrence))
        {
            return occurrences;
        }

        if (string.Equals(normalizedTimeMode, PluginConfiguration.SceneScheduleTimeModeFixed, StringComparison.Ordinal) &&
            !PluginConfiguration.TryNormalizeSceneScheduleTime(schedule.TimeOfDay, out _))
        {
            return occurrences;
        }

        if (!string.Equals(normalizedTimeMode, PluginConfiguration.SceneScheduleTimeModeFixed, StringComparison.Ordinal) &&
            !PluginConfiguration.AreValidSceneScheduleSolarCoordinates(schedule.SolarLatitude, schedule.SolarLongitude))
        {
            return occurrences;
        }

        if (!runDate.HasValue &&
            ((string.Equals(normalizedRecurrence, PluginConfiguration.SceneScheduleRecurrenceMonthly, StringComparison.Ordinal) &&
              (schedule.DayOfMonth < 1 || schedule.DayOfMonth > 31)) ||
             (string.Equals(normalizedRecurrence, PluginConfiguration.SceneScheduleRecurrenceMonthlyWeekday, StringComparison.Ordinal) &&
              !IsMonthlyWeekdayConfigurationValid(schedule)) ||
             (string.Equals(normalizedRecurrence, PluginConfiguration.SceneScheduleRecurrenceYearly, StringComparison.Ordinal) &&
              !IsYearlyConfigurationValid(schedule)) ||
             (string.Equals(normalizedRecurrence, PluginConfiguration.SceneScheduleRecurrenceWeekly, StringComparison.Ordinal) &&
              (schedule.DaysOfWeekMask & PluginConfiguration.AllSceneScheduleDaysMask) == 0) ||
             !IsRecurrenceIntervalConfigurationValid(schedule, runDate)))
            return occurrences;

        // Solar offsets can move an event into the following local date. Include the
        // preceding base date so a preview requested after midnight still finds the
        // shifted event before evaluating the next base solar date.
        var solarBaseLookbackDays = string.Equals(
                normalizedTimeMode,
                PluginConfiguration.SceneScheduleTimeModeSunrise,
                StringComparison.Ordinal) ||
            string.Equals(
                normalizedTimeMode,
                PluginConfiguration.SceneScheduleTimeModeSunset,
                StringComparison.Ordinal)
            ? 1
            : 0;
        var firstCandidateDate = scheduleNow.Date.AddDays(-solarBaseLookbackDays);
        if (runDate.HasValue)
        {
            if (runDate.Value < firstCandidateDate)
                return occurrences;

            if (!includeFutureStartBeyondHorizon &&
                (runDate.Value - firstCandidateDate).TotalDays >= boundedHorizon)
            {
                return occurrences;
            }

            firstCandidateDate = runDate.Value;
        }
        else if (startDate.HasValue && firstCandidateDate < startDate.Value)
        {
            if (!includeFutureStartBeyondHorizon &&
                (startDate.Value - firstCandidateDate).TotalDays >= boundedHorizon)
            {
                return occurrences;
            }

            firstCandidateDate = startDate.Value;
        }

        var skipNextOccurrence = schedule.SkipNextOccurrence;
        for (var dayOffset = 0; dayOffset < boundedHorizon + solarBaseLookbackDays; dayOffset++)
        {
            if (runDate.HasValue && dayOffset > 0)
                break;

            var candidateDate = firstCandidateDate.AddDays(dayOffset);
            if (endDate.HasValue && candidateDate > endDate.Value)
                break;
            if (!IsScheduleDateAllowed(schedule, candidateDate))
                continue;

            if (!runDate.HasValue && !IsScheduleRecurrenceDate(schedule, normalizedRecurrence, candidateDate))
                continue;

            if (!TryGetScheduleOccurrenceTimes(
                    schedule,
                    candidateDate,
                    timeZone,
                    out var candidateLocal,
                    out var candidateUtc))
            {
                // A spring-forward fixed time or a polar-day/night solar event has no
                // occurrence on this local date. Skipping it keeps scheduler and preview
                // behavior identical and avoids manufacturing an unsafe instant.
                continue;
            }

            if (candidateUtc <= serverUtcNow)
                continue;

            if (skipNextOccurrence)
            {
                skipNextOccurrence = false;
                continue;
            }

            occurrences.Add(new HueSceneScheduleOccurrence
            {
                ScheduleId = schedule.Id?.Trim() ?? string.Empty,
                ScheduleName = schedule.Name?.Trim() ?? string.Empty,
                PresetName = schedule.PresetName?.Trim() ?? string.Empty,
                PlaylistName = schedule.PlaylistName?.Trim() ?? string.Empty,
                Priority = schedule.Priority,
                TargetAllEnabledMappings = schedule.TargetAllEnabledMappings,
                TargetUserIds = schedule.TargetUserIds?.Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                    ?? Array.Empty<string>(),
                IncludeDefaultTarget = schedule.IncludeDefaultTarget,
                TimeMode = PluginConfiguration.TryNormalizeSceneScheduleTimeMode(
                    schedule.TimeMode,
                    out var occurrenceTimeMode)
                    ? occurrenceTimeMode
                    : PluginConfiguration.SceneScheduleTimeModeFixed,
                SolarOffsetMinutes = Math.Clamp(
                    schedule.SolarOffsetMinutes,
                    PluginConfiguration.MinSceneScheduleSolarOffsetMinutes,
                    PluginConfiguration.MaxSceneScheduleSolarOffsetMinutes),
                SolarLatitude = schedule.SolarLatitude,
                SolarLongitude = schedule.SolarLongitude,
                Effect = PluginConfiguration.ColorPresetEffectSolid,
                EffectSpeedPercent = PluginConfiguration.ClampColorPresetEffectSpeedPercent(effectSpeedPercent),
                DurationSeconds = durationSeconds >= 0 ? durationSeconds : schedule.DurationSeconds,
                TransitionSeconds = Math.Clamp(
                    transitionSeconds,
                    PluginConfiguration.MinColorPresetTransitionSeconds,
                    PluginConfiguration.MaxColorPresetTransitionSeconds),
                TransitionOutSeconds = Math.Clamp(
                    transitionOutSeconds,
                    PluginConfiguration.MinColorPresetTransitionOutSeconds,
                    PluginConfiguration.MaxColorPresetTransitionOutSeconds),
                RecurrenceInterval = schedule.RecurrenceInterval,
                MonthOfYear = schedule.MonthOfYear,
                WeekOfMonth = schedule.WeekOfMonth,
                DayOfWeek = schedule.DayOfWeek,
                TimeZoneId = schedule.TimeZoneId?.Trim() ?? string.Empty,
                TimeZoneDisplayName = string.IsNullOrWhiteSpace(schedule.TimeZoneId)
                    ? $"Server local ({timeZone.DisplayName})"
                    : timeZone.DisplayName,
                LocalTime = candidateLocal,
                UtcTime = DateTime.SpecifyKind(candidateUtc, DateTimeKind.Utc)
            });

            if (occurrences.Count >= boundedOccurrences ||
                (schedule.MaxRuns > 0 && occurrences.Count >= Math.Max(0, schedule.MaxRuns - schedule.RunCount)))
                break;
        }

        return occurrences;
    }

    /// <summary>
    /// Finds bounded upcoming execution windows that overlap across enabled cues. The
    /// scheduler serializes restorative bridge lifecycles, so any overlap can delay the
    /// later cue even when the cues target different mappings. The calculation reuses the
    /// same time-zone, DST, recurrence, exclusion, skip, and finite-run rules as the
    /// occurrence preview and never contacts a bridge. When a schedule ID is supplied,
    /// the result contains only conflicts involving that cue while retaining the other
    /// cue in each pair for actionable context.
    /// </summary>
    internal static IReadOnlyList<HueSceneScheduleConflict> GetUpcomingConflicts(
        PluginConfiguration? config,
        DateTime serverLocalNow,
        int maxConflicts = DefaultConflictLimit,
        int horizonDays = DefaultConflictHorizonDays,
        string? scheduleId = null)
    {
        if (config == null)
            return Array.Empty<HueSceneScheduleConflict>();

        var boundedLimit = Math.Clamp(maxConflicts, 1, MaxConflictLimit);
        var boundedHorizon = Math.Clamp(horizonDays, 1, MaxConflictHorizonDays);
        var normalizedScheduleId = string.IsNullOrWhiteSpace(scheduleId) ? null : scheduleId.Trim();
        var windows = new List<HueSceneScheduleConflictWindow>();
        var schedules = (config.SceneSchedules ?? new List<HueSceneSchedule>())
            .Where(schedule => schedule != null && schedule.Enabled)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(normalizedScheduleId) &&
            !schedules.Any(schedule => string.Equals(
                schedule.Id?.Trim(),
                normalizedScheduleId,
                StringComparison.OrdinalIgnoreCase)))
        {
            return Array.Empty<HueSceneScheduleConflict>();
        }

        foreach (var schedule in schedules)
        {
            var preset = config.ColorPresets?.FirstOrDefault(candidate =>
                candidate != null &&
                string.Equals(candidate.Name?.Trim(), schedule.PresetName?.Trim(), StringComparison.OrdinalIgnoreCase));
            var playlist = config.ScenePlaylists?.FirstOrDefault(candidate =>
                candidate != null &&
                string.Equals(candidate.Name?.Trim(), schedule.PlaylistName?.Trim(), StringComparison.OrdinalIgnoreCase));
            var isPlaylist = !string.IsNullOrWhiteSpace(schedule.PlaylistName);
            var durationSeconds = isPlaylist
                ? GetPlaylistTotalDurationSeconds(config, playlist)
                : GetEffectiveDurationSeconds(schedule, preset);
            var transitionSeconds = isPlaylist ? 0 : GetEffectiveTransitionSeconds(schedule, preset);
            var transitionOutSeconds = isPlaylist ? 0 : GetEffectiveTransitionOutSeconds(schedule, preset);
            var effectSpeedPercent = preset == null
                ? PluginConfiguration.DefaultColorPresetEffectSpeedPercent
                : PluginConfiguration.ClampColorPresetEffectSpeedPercent(preset.EffectSpeedPercent);

            foreach (var occurrence in GetUpcomingOccurrences(
                         schedule,
                         serverLocalNow,
                         MaxUpcomingOccurrencesPerSchedule,
                         boundedHorizon,
                         includeFutureStartBeyondHorizon: false,
                         transitionSeconds,
                         transitionOutSeconds,
                         effectSpeedPercent,
                         durationSeconds))
            {
                var startUtc = DateTime.SpecifyKind(occurrence.UtcTime, DateTimeKind.Utc);
                var endUtc = startUtc.AddSeconds(Math.Max(
                    PluginConfiguration.MinPreviewDurationSeconds,
                    occurrence.DurationSeconds));
                windows.Add(new HueSceneScheduleConflictWindow(
                    occurrence,
                    endUtc,
                    ResolveTargetLabel(config, schedule)));
            }
        }

        if (windows.Count < 2)
            return Array.Empty<HueSceneScheduleConflict>();

        var conflicts = new List<HueSceneScheduleConflict>();
        for (var leftIndex = 0; leftIndex < windows.Count; leftIndex++)
        {
            var left = windows[leftIndex];
            for (var rightIndex = leftIndex + 1; rightIndex < windows.Count; rightIndex++)
            {
                var right = windows[rightIndex];
                if (string.Equals(
                        left.Occurrence.ScheduleId,
                        right.Occurrence.ScheduleId,
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(left.Occurrence.ScheduleId))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(normalizedScheduleId) &&
                    !string.Equals(
                        left.Occurrence.ScheduleId,
                        normalizedScheduleId,
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(
                        right.Occurrence.ScheduleId,
                        normalizedScheduleId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (left.Occurrence.UtcTime >= right.EndUtc || right.Occurrence.UtcTime >= left.EndUtc)
                    continue;

                var first = left.Occurrence.UtcTime <= right.Occurrence.UtcTime ? left : right;
                var second = ReferenceEquals(first, left) ? right : left;
                var overlapStart = first.Occurrence.UtcTime >= second.Occurrence.UtcTime
                    ? first.Occurrence.UtcTime
                    : second.Occurrence.UtcTime;
                var overlapEnd = first.EndUtc <= second.EndUtc ? first.EndUtc : second.EndUtc;
                var overlapSeconds = Math.Max(
                    1,
                    (int)Math.Ceiling((overlapEnd - overlapStart).TotalSeconds));
                var priorityMessage = first.Occurrence.Priority == second.Occurrence.Priority
                    ? "Equal priority; saved configuration order decides which cue starts first."
                    : first.Occurrence.Priority > second.Occurrence.Priority
                        ? $"{first.Occurrence.ScheduleName} has higher priority and starts first when both cues are due together."
                        : $"{second.Occurrence.ScheduleName} has higher priority and starts first when both cues are due together.";

                conflicts.Add(new HueSceneScheduleConflict
                {
                    FirstScheduleId = first.Occurrence.ScheduleId,
                    FirstScheduleName = first.Occurrence.ScheduleName,
                    FirstTargetLabel = first.TargetLabel,
                    FirstOccurrenceUtc = DateTime.SpecifyKind(first.Occurrence.UtcTime, DateTimeKind.Utc),
                    FirstOccurrenceLocal = first.Occurrence.LocalTime,
                    FirstDurationSeconds = Math.Max(
                        PluginConfiguration.MinPreviewDurationSeconds,
                        first.Occurrence.DurationSeconds),
                    FirstPriority = first.Occurrence.Priority,
                    SecondScheduleId = second.Occurrence.ScheduleId,
                    SecondScheduleName = second.Occurrence.ScheduleName,
                    SecondTargetLabel = second.TargetLabel,
                    SecondOccurrenceUtc = DateTime.SpecifyKind(second.Occurrence.UtcTime, DateTimeKind.Utc),
                    SecondOccurrenceLocal = second.Occurrence.LocalTime,
                    SecondDurationSeconds = Math.Max(
                        PluginConfiguration.MinPreviewDurationSeconds,
                        second.Occurrence.DurationSeconds),
                    SecondPriority = second.Occurrence.Priority,
                    OverlapSeconds = overlapSeconds,
                    ResolutionHint = priorityMessage + " The scheduler serializes restorative cue lifecycles, so the later cue may be delayed."
                });
            }
        }

        return conflicts
            .OrderBy(conflict => conflict.FirstOccurrenceUtc)
            .ThenByDescending(conflict => Math.Max(conflict.FirstPriority, conflict.SecondPriority))
            .ThenBy(conflict => conflict.FirstScheduleName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(conflict => conflict.SecondScheduleName, StringComparer.OrdinalIgnoreCase)
            .Take(boundedLimit)
            .ToArray();
    }

    /// <summary>
    /// Runs one configured scene cue immediately. This is also the operation used by
    /// the recurring loop after it has matched the local day and minute.
    /// </summary>
    public async Task<HueSceneAutomationRunResult> RunScheduleAsync(
        string scheduleId,
        CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        var schedule = config?.SceneSchedules?.FirstOrDefault(candidate =>
            candidate != null &&
            string.Equals(candidate.Id?.Trim(), scheduleId?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (schedule == null)
        {
            return Failure(
                scheduleId,
                "The requested scene schedule was not found.");
        }

        var key = schedule.Id?.Trim() ?? string.Empty;
        if (IsRunLimitReached(schedule))
        {
            var exhausted = Failure(
                schedule.Id,
                $"The scene cue has reached its maximum of {schedule.MaxRuns} executions.",
                schedule);
            exhausted.RunCount = schedule.RunCount;
            return exhausted;
        }

        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_manualRunCancellationLock)
        {
            if (_manualRunCancellations.ContainsKey(key))
            {
                return Failure(
                    scheduleId,
                    "The requested scene schedule is already running.",
                    schedule);
            }

            _manualRunCancellations[key] = runCancellation;
        }

        try
        {
            try
            {
                return await RunScheduleTrackedAsync(config!, schedule, runCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                runCancellation.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                var canceled = Failure(schedule.Id, "The scene cue run was canceled.", schedule);
                canceled.RunCount = GetRuntimeState(schedule.Id).RunCount;
                return canceled;
            }
        }
        finally
        {
            lock (_manualRunCancellationLock)
            {
                if (_manualRunCancellations.TryGetValue(key, out var active) &&
                    ReferenceEquals(active, runCancellation))
                {
                    _manualRunCancellations.Remove(key);
                }
            }
        }
    }

    /// <summary>
    /// Runs an unsaved administrator preview against the resolved target collection.
    /// This keeps immediate all-target previews on the same sequential, restorative
    /// lifecycle as scheduled cues without creating a schedule or retaining run history.
    /// </summary>
    public async Task<HueSceneAutomationRunResult> RunPreviewAsync(
        HueSceneSchedule schedule,
        HueColorPreset preset,
        CancellationToken cancellationToken = default,
        bool targetScopedPlayback = false)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
            return Failure(schedule?.Id, "Scene automation configuration is unavailable.", schedule);

        if (schedule == null || preset == null)
            return Failure(schedule?.Id, "The preview configuration is unavailable.", schedule);

        if (!TryResolveTargets(config, schedule, out var targets, out var targetError))
            return Failure(schedule.Id, targetError, schedule);

        PluginConfiguration.TryNormalizeColorPresetEffect(preset.Effect, out var effect);
        var targetResults = new List<HueSceneScheduleTargetResult>();
        foreach (var target in targets)
        {
            var targetResult = await RunScheduleTargetAsync(
                schedule,
                preset,
                target,
                cancellationToken,
                targetScopedPlayback).ConfigureAwait(false);
            targetResults.Add(targetResult);
            if (!targetResult.Succeeded &&
                targetResult.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        var succeededCount = targetResults.Count(result => result.Succeeded);
        var cleanupWarning = string.Join(
            " ",
            targetResults
                .Where(result => !string.IsNullOrWhiteSpace(result.CleanupWarning))
                .Select(result => $"{result.TargetLabel}: {result.CleanupWarning!.Trim()}"));
        return new HueSceneAutomationRunResult
        {
            ScheduleId = schedule.Id?.Trim() ?? string.Empty,
            ScheduleName = schedule.Name?.Trim() ?? string.Empty,
            PresetName = preset.Name?.Trim() ?? string.Empty,
            Effect = effect,
            EffectSpeedPercent = PluginConfiguration.ClampColorPresetEffectSpeedPercent(preset.EffectSpeedPercent),
            TargetLabel = ResolveTargetLabel(config, schedule),
            TargetUserIds = schedule.TargetUserIds?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                ?? Array.Empty<string>(),
            IncludeDefaultTarget = schedule.IncludeDefaultTarget,
            Succeeded = targetResults.Count > 0 && succeededCount == targetResults.Count,
            Message = BuildAggregateRunMessage(targetResults, succeededCount),
            CleanupWarning = string.IsNullOrWhiteSpace(cleanupWarning) ? null : cleanupWarning,
            TargetResults = targetResults,
            RunAtUtc = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Runs an ordered saved-scene playlist through the same restorative preview lifecycle
    /// as an individual scene. All playlist references and targets are preflighted before
    /// the first bridge call; each repeated pass remains independently observable and
    /// cancellable.
    /// </summary>
    public async Task<HueScenePlaylistRunResult> RunPlaylistPreviewAsync(
        HueScenePlaylist playlist,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? targetUserIdsOverride = null,
        bool includeDefaultTargetOverride = false,
        bool targetScopedPlayback = false)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
            return PlaylistFailure(playlist, "Scene playlist configuration is unavailable.");

        if (playlist == null)
            return PlaylistFailure(null, "The scene playlist is unavailable.");

        var validationErrors = PluginConfiguration.ValidateScenePlaylist(playlist, config);
        if (validationErrors.Count > 0)
            return PlaylistFailure(playlist, string.Join(" ", validationErrors));

        var presets = (playlist.PresetNames ?? new List<string>())
            .Select(name => config.ColorPresets?.FirstOrDefault(candidate =>
                candidate != null &&
                string.Equals(candidate.Name?.Trim(), name?.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (presets.Any(preset => preset == null))
            return PlaylistFailure(playlist, "The scene playlist references a saved scene that no longer exists.");

        var normalizedTargetUserIdsOverride = targetUserIdsOverride?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var hasTargetOverride = includeDefaultTargetOverride || (normalizedTargetUserIdsOverride?.Count > 0);
        var effectiveTargetUserIds = hasTargetOverride
            ? normalizedTargetUserIdsOverride ?? new List<string>()
            : (playlist.TargetUserIds ?? new List<string>())
                .Select(value => value?.Trim() ?? string.Empty)
                .ToList();
        var effectiveIncludeDefaultTarget = hasTargetOverride
            ? includeDefaultTargetOverride
            : playlist.IncludeDefaultTarget;
        var targetSchedule = new HueSceneSchedule
        {
            Id = "scene-playlist-preview",
            Name = playlist.Name?.Trim() ?? string.Empty,
            PresetName = presets[0]!.Name?.Trim() ?? string.Empty,
            TargetUserId = hasTargetOverride || effectiveIncludeDefaultTarget || effectiveTargetUserIds.Count > 0
                ? string.Empty
                : playlist.TargetAllEnabledMappings ? string.Empty : playlist.TargetUserId?.Trim() ?? string.Empty,
            TargetUserIds = effectiveTargetUserIds,
            IncludeDefaultTarget = effectiveIncludeDefaultTarget,
            TargetAllEnabledMappings = !hasTargetOverride && !effectiveIncludeDefaultTarget &&
                effectiveTargetUserIds.Count == 0 &&
                playlist.TargetAllEnabledMappings
        };
        if (!TryResolveTargets(config, targetSchedule, out _, out var targetError))
            return PlaylistFailure(playlist, targetError);

        var repeatCount = Math.Clamp(
            playlist.RepeatCount,
            PluginConfiguration.MinScenePlaylistRepeatCount,
            PluginConfiguration.MaxScenePlaylistRepeatCount);
        var totalStepCount = presets.Count * repeatCount;
        var steps = new List<HueScenePlaylistStepResult>();
        var canceled = false;
        for (var repeatIndex = 1; repeatIndex <= repeatCount && !canceled; repeatIndex++)
        {
            for (var index = 0; index < presets.Count; index++)
            {
                var preset = presets[index]!;
                var schedule = new HueSceneSchedule
                {
                    Id = $"scene-playlist-preview-{repeatIndex}-{index + 1}",
                    Name = playlist.Name?.Trim() ?? string.Empty,
                    PresetName = preset.Name?.Trim() ?? string.Empty,
                    TargetUserId = targetSchedule.TargetUserId,
                    TargetUserIds = targetSchedule.TargetUserIds.ToList(),
                    IncludeDefaultTarget = targetSchedule.IncludeDefaultTarget,
                    TargetAllEnabledMappings = targetSchedule.TargetAllEnabledMappings
                };
                var run = await RunPreviewAsync(
                    schedule,
                    preset,
                    cancellationToken,
                    targetScopedPlayback).ConfigureAwait(false);
                PluginConfiguration.TryNormalizeColorPresetEffect(preset.Effect, out var effect);
                steps.Add(new HueScenePlaylistStepResult
                {
                    Index = steps.Count + 1,
                    RepeatIndex = repeatIndex,
                    PresetName = preset.Name?.Trim() ?? string.Empty,
                    Effect = effect,
                    EffectSpeedPercent = PluginConfiguration.ClampColorPresetEffectSpeedPercent(preset.EffectSpeedPercent),
                    DurationSeconds = HueSceneAutomationService.GetEffectiveDurationSeconds(schedule, preset),
                    TransitionSeconds = HueSceneAutomationService.GetEffectiveTransitionSeconds(schedule, preset),
                    TransitionOutSeconds = HueSceneAutomationService.GetEffectiveTransitionOutSeconds(schedule, preset),
                    Succeeded = run.Succeeded,
                    Message = run.Message,
                    CleanupWarning = run.CleanupWarning,
                    TargetResults = run.TargetResults
                });
                if (!run.Succeeded && run.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase))
                {
                    canceled = true;
                    break;
                }
            }
        }

        var succeededCount = steps.Count(step => step.Succeeded);
        var cleanupWarning = string.Join(
            " ",
            steps
                .Where(step => !string.IsNullOrWhiteSpace(step.CleanupWarning))
                .Select(step => $"{step.PresetName}: {step.CleanupWarning!.Trim()}"));
        var targetResults = steps
            .SelectMany(step => step.TargetResults)
            .GroupBy(target => target.TargetLabel, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var entries = group.ToArray();
                var failed = entries.Where(entry => !entry.Succeeded).ToArray();
                return new HueScenePlaylistTargetResult
                {
                    TargetLabel = group.Key,
                    Succeeded = entries.Length == totalStepCount && entries.All(entry => entry.Succeeded),
                    CompletedStepCount = entries.Length,
                    TotalStepCount = totalStepCount,
                    Message = failed.Length == 0
                        ? $"Completed {entries.Length} of {totalStepCount} playlist step(s)."
                        : string.Join(" ", failed.Select(entry => entry.Message).Where(message => !string.IsNullOrWhiteSpace(message))),
                    CleanupWarning = string.Join(" ", entries
                        .Where(entry => !string.IsNullOrWhiteSpace(entry.CleanupWarning))
                        .Select(entry => entry.CleanupWarning!.Trim())),
                    AvailableChannelCount = entries.Select(entry => entry.AvailableChannelCount).DefaultIfEmpty().Max(),
                    SelectedChannelCount = entries.Select(entry => entry.SelectedChannelCount).DefaultIfEmpty().Max()
                };
            })
            .ToArray();
        var message = steps.Count == totalStepCount && succeededCount == totalStepCount
            ? repeatCount == 1
                ? $"Played playlist '{playlist.Name?.Trim()}' with {presets.Count} saved scene(s)."
                : $"Played playlist '{playlist.Name?.Trim()}' with {presets.Count} saved scene(s) for {repeatCount} passes."
            : steps.Count == 0
                ? "The scene playlist did not contain any runnable steps."
                : $"Played {succeededCount} of {totalStepCount} playlist step(s).";
        return new HueScenePlaylistRunResult
        {
            PlaylistId = playlist.Id?.Trim() ?? string.Empty,
            PlaylistName = playlist.Name?.Trim() ?? string.Empty,
            RepeatCount = repeatCount,
            TargetLabel = ResolveTargetLabel(config, targetSchedule),
            TargetAllEnabledMappings = targetSchedule.TargetAllEnabledMappings,
            TargetUserIds = targetSchedule.TargetUserIds?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                ?? Array.Empty<string>(),
            IncludeDefaultTarget = targetSchedule.IncludeDefaultTarget,
            Succeeded = steps.Count == totalStepCount && succeededCount == totalStepCount,
            Message = message,
            CleanupWarning = string.IsNullOrWhiteSpace(cleanupWarning) ? null : cleanupWarning,
            Steps = steps,
            TargetResults = targetResults,
            RunAtUtc = DateTime.UtcNow
        };
    }

    private static HueScenePlaylistRunResult PlaylistFailure(
        HueScenePlaylist? playlist,
        string message)
    {
        return new HueScenePlaylistRunResult
        {
            PlaylistId = playlist?.Id?.Trim() ?? string.Empty,
            PlaylistName = playlist?.Name?.Trim() ?? string.Empty,
            RepeatCount = playlist == null
                ? PluginConfiguration.DefaultScenePlaylistRepeatCount
                : Math.Clamp(
                    playlist.RepeatCount,
                    PluginConfiguration.MinScenePlaylistRepeatCount,
                    PluginConfiguration.MaxScenePlaylistRepeatCount),
            TargetAllEnabledMappings = playlist?.TargetAllEnabledMappings == true,
            TargetUserIds = playlist?.TargetUserIds?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                ?? Array.Empty<string>(),
            IncludeDefaultTarget = playlist?.IncludeDefaultTarget == true,
            Succeeded = false,
            Message = message,
            RunAtUtc = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Requests cancellation of a manually started cue. The active stream tester and
    /// bridge cleanup lifecycle receive the cancellation through the linked run token.
    /// </summary>
    public bool CancelSchedule(string scheduleId)
    {
        var key = scheduleId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        lock (_manualRunCancellationLock)
        {
            if (!_manualRunCancellations.TryGetValue(key, out var cancellation))
                return false;

            cancellation.Cancel();
            return true;
        }
    }

    private static bool TryGetScheduleOccurrenceTimes(
        HueSceneSchedule schedule,
        DateTime scheduleDate,
        TimeZoneInfo timeZone,
        out DateTime localTime,
        out DateTime utcTime)
    {
        localTime = default;
        utcTime = default;
        if (schedule == null || timeZone == null ||
            !PluginConfiguration.TryNormalizeSceneScheduleTimeMode(schedule.TimeMode, out var timeMode))
        {
            return false;
        }

        if (string.Equals(timeMode, PluginConfiguration.SceneScheduleTimeModeSunrise, StringComparison.Ordinal) ||
            string.Equals(timeMode, PluginConfiguration.SceneScheduleTimeModeSunset, StringComparison.Ordinal))
        {
            if (!PluginConfiguration.AreValidSceneScheduleSolarCoordinates(
                    schedule.SolarLatitude,
                    schedule.SolarLongitude))
            {
                return false;
            }

            return HueSolarCalculator.TryGetEventLocal(
                scheduleDate,
                timeZone,
                schedule.SolarLatitude!.Value,
                schedule.SolarLongitude!.Value,
                string.Equals(timeMode, PluginConfiguration.SceneScheduleTimeModeSunrise, StringComparison.Ordinal),
                Math.Clamp(
                    schedule.SolarOffsetMinutes,
                    PluginConfiguration.MinSceneScheduleSolarOffsetMinutes,
                    PluginConfiguration.MaxSceneScheduleSolarOffsetMinutes),
                out localTime,
                out utcTime);
        }

        if (!PluginConfiguration.TryNormalizeSceneScheduleTime(schedule.TimeOfDay, out var normalizedTime))
            return false;

        var parsedTime = TimeSpan.Parse(normalizedTime, System.Globalization.CultureInfo.InvariantCulture);
        localTime = DateTime.SpecifyKind(scheduleDate.Date.Add(parsedTime), DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(localTime))
            return false;

        try
        {
            utcTime = DateTime.SpecifyKind(
                TimeZoneInfo.ConvertTimeToUtc(localTime, timeZone),
                DateTimeKind.Utc);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryGetScheduleOccurrenceForLocalDate(
        HueSceneSchedule schedule,
        DateTime localDate,
        TimeZoneInfo timeZone,
        out DateTime localTime,
        out DateTime utcTime)
    {
        localTime = default;
        utcTime = default;
        for (var dayOffset = -1; dayOffset <= 1; dayOffset++)
        {
            if (!TryGetScheduleOccurrenceTimes(
                    schedule,
                    localDate.Date.AddDays(dayOffset),
                    timeZone,
                    out var candidateLocal,
                    out var candidateUtc) ||
                candidateLocal.Date != localDate.Date)
            {
                continue;
            }

            localTime = candidateLocal;
            utcTime = candidateUtc;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Determines whether a schedule is due in the supplied server-local minute after
    /// converting that instant into the cue's configured time zone. One-time cues match
    /// their RunDate; recurring cues use every calendar day, Sunday=1 through Saturday=64
    /// bits, a monthly calendar day, a monthly ordinal weekday, or a yearly calendar date.
    /// </summary>
    internal static bool IsDue(HueSceneSchedule schedule, DateTime localNow)
    {
        if (schedule == null || !schedule.Enabled ||
            IsRunLimitReached(schedule) ||
            !PluginConfiguration.TryNormalizeSceneScheduleTimeMode(schedule.TimeMode, out var normalizedTimeMode) ||
            !TryGetScheduleLocalNow(schedule, localNow, out var scheduleNow, out _) ||
            !TryGetScheduleRunDate(schedule, out var runDate))
            return false;

        if (string.Equals(normalizedTimeMode, PluginConfiguration.SceneScheduleTimeModeFixed, StringComparison.Ordinal) &&
            !PluginConfiguration.TryNormalizeSceneScheduleTime(schedule.TimeOfDay, out _))
        {
            return false;
        }

        if (!runDate.HasValue &&
            (!PluginConfiguration.TryNormalizeSceneScheduleRecurrence(schedule.Recurrence, out var recurrence) ||
             (string.Equals(recurrence, PluginConfiguration.SceneScheduleRecurrenceMonthly, StringComparison.Ordinal) &&
              (schedule.DayOfMonth < 1 || schedule.DayOfMonth > 31)) ||
             (string.Equals(recurrence, PluginConfiguration.SceneScheduleRecurrenceMonthlyWeekday, StringComparison.Ordinal) &&
              !IsMonthlyWeekdayConfigurationValid(schedule)) ||
             (string.Equals(recurrence, PluginConfiguration.SceneScheduleRecurrenceYearly, StringComparison.Ordinal) &&
              !IsYearlyConfigurationValid(schedule)) ||
             (string.Equals(recurrence, PluginConfiguration.SceneScheduleRecurrenceWeekly, StringComparison.Ordinal) &&
              (schedule.DaysOfWeekMask & PluginConfiguration.AllSceneScheduleDaysMask) == 0) ||
             !IsRecurrenceIntervalConfigurationValid(schedule, runDate)))
            return false;

        if (!PluginConfiguration.TryNormalizeSceneScheduleRecurrence(schedule.Recurrence, out var normalizedRecurrence))
            return false;

        if (!PluginConfiguration.TryResolveSceneScheduleTimeZone(schedule.TimeZoneId, out var timeZone))
            timeZone = TimeZoneInfo.Local;

        // Solar offsets can move an event across local midnight. Recurrence and date
        // windows belong to the unshifted solar date, so inspect the adjacent base dates
        // while matching the actual shifted local instant against the current minute.
        for (var dayOffset = -1; dayOffset <= 1; dayOffset++)
        {
            var candidateDate = scheduleNow.Date.AddDays(dayOffset);
            if (runDate.HasValue && candidateDate != runDate.Value)
                continue;
            if (!IsScheduleDateAllowed(schedule, candidateDate) ||
                (!runDate.HasValue && !IsScheduleRecurrenceDate(schedule, normalizedRecurrence, candidateDate)) ||
                !TryGetScheduleOccurrenceTimes(
                    schedule,
                    candidateDate,
                    timeZone,
                    out var expectedLocal,
                    out _))
            {
                continue;
            }

            if (expectedLocal.Date == scheduleNow.Date &&
                scheduleNow.Hour == expectedLocal.Hour &&
                scheduleNow.Minute == expectedLocal.Minute)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRunLimitReached(HueSceneSchedule? schedule)
        => schedule != null && schedule.MaxRuns > 0 && schedule.RunCount >= schedule.MaxRuns;

    private static bool IsScheduleRecurrenceDate(
        HueSceneSchedule schedule,
        string recurrence,
        DateTime scheduleDate)
    {
        if (string.Equals(recurrence, PluginConfiguration.SceneScheduleRecurrenceDaily, StringComparison.Ordinal))
            return IsScheduleIntervalDateAllowed(schedule, recurrence, scheduleDate);

        if (string.Equals(recurrence, PluginConfiguration.SceneScheduleRecurrenceMonthlyWeekday, StringComparison.Ordinal))
        {
            if (!IsMonthlyWeekdayConfigurationValid(schedule) ||
                (int)scheduleDate.DayOfWeek != schedule.DayOfWeek)
            {
                return false;
            }

            if (schedule.WeekOfMonth == PluginConfiguration.SceneScheduleLastWeekOfMonth)
            {
                return scheduleDate.Day + 7 > DateTime.DaysInMonth(scheduleDate.Year, scheduleDate.Month) &&
                       IsScheduleIntervalDateAllowed(schedule, recurrence, scheduleDate);
            }

            return ((scheduleDate.Day - 1) / 7) + 1 == schedule.WeekOfMonth &&
                   IsScheduleIntervalDateAllowed(schedule, recurrence, scheduleDate);
        }

        if (string.Equals(recurrence, PluginConfiguration.SceneScheduleRecurrenceYearly, StringComparison.Ordinal))
        {
            if (!IsYearlyConfigurationValid(schedule) || scheduleDate.Month != schedule.MonthOfYear)
                return false;

            return scheduleDate.Day == Math.Min(
                schedule.DayOfMonth,
                DateTime.DaysInMonth(scheduleDate.Year, scheduleDate.Month)) &&
                IsScheduleIntervalDateAllowed(schedule, recurrence, scheduleDate);
        }

        if (string.Equals(recurrence, PluginConfiguration.SceneScheduleRecurrenceMonthly, StringComparison.Ordinal))
        {
            if (schedule.DayOfMonth < 1 || schedule.DayOfMonth > 31)
                return false;

            return scheduleDate.Day == Math.Min(
                schedule.DayOfMonth,
                DateTime.DaysInMonth(scheduleDate.Year, scheduleDate.Month)) &&
                IsScheduleIntervalDateAllowed(schedule, recurrence, scheduleDate);
        }

        return (schedule.DaysOfWeekMask & (1 << (int)scheduleDate.DayOfWeek)) != 0 &&
               IsScheduleIntervalDateAllowed(schedule, recurrence, scheduleDate);
    }

    private static bool IsMonthlyWeekdayConfigurationValid(HueSceneSchedule schedule)
    {
        return (schedule.WeekOfMonth == PluginConfiguration.SceneScheduleLastWeekOfMonth ||
                (schedule.WeekOfMonth >= PluginConfiguration.MinSceneScheduleWeekOfMonth &&
                 schedule.WeekOfMonth <= PluginConfiguration.MaxSceneScheduleWeekOfMonth)) &&
               schedule.DayOfWeek >= (int)DayOfWeek.Sunday &&
               schedule.DayOfWeek <= (int)DayOfWeek.Saturday;
    }

    private static bool IsYearlyConfigurationValid(HueSceneSchedule schedule)
    {
        return schedule.MonthOfYear >= PluginConfiguration.MinSceneScheduleMonthOfYear &&
               schedule.MonthOfYear <= PluginConfiguration.MaxSceneScheduleMonthOfYear &&
               schedule.DayOfMonth >= 1 &&
               schedule.DayOfMonth <= 31;
    }

    private static bool IsRecurrenceIntervalConfigurationValid(
        HueSceneSchedule schedule,
        DateTime? runDate)
    {
        if (runDate.HasValue)
            return true;

        return schedule.RecurrenceInterval >= PluginConfiguration.MinSceneScheduleRecurrenceInterval &&
               schedule.RecurrenceInterval <= PluginConfiguration.MaxSceneScheduleRecurrenceInterval &&
               (schedule.RecurrenceInterval == PluginConfiguration.MinSceneScheduleRecurrenceInterval ||
                TryParseScheduleDate(schedule.StartDate, out _));
    }

    private static bool IsScheduleIntervalDateAllowed(
        HueSceneSchedule schedule,
        string recurrence,
        DateTime scheduleDate)
    {
        var interval = schedule.RecurrenceInterval;
        if (interval == PluginConfiguration.MinSceneScheduleRecurrenceInterval)
            return true;

        if (!TryParseScheduleDate(schedule.StartDate, out var anchorDate))
            return false;

        var candidateDate = scheduleDate.Date;
        anchorDate = anchorDate.Date;
        if (candidateDate < anchorDate)
            return false;

        if (string.Equals(recurrence, PluginConfiguration.SceneScheduleRecurrenceDaily, StringComparison.Ordinal))
            return (candidateDate - anchorDate).Days % interval == 0;

        if (string.Equals(recurrence, PluginConfiguration.SceneScheduleRecurrenceWeekly, StringComparison.Ordinal))
        {
            var anchorWeek = anchorDate.AddDays(-(int)anchorDate.DayOfWeek);
            var candidateWeek = candidateDate.AddDays(-(int)candidateDate.DayOfWeek);
            return ((candidateWeek - anchorWeek).Days / 7) % interval == 0;
        }

        if (string.Equals(recurrence, PluginConfiguration.SceneScheduleRecurrenceMonthly, StringComparison.Ordinal) ||
            string.Equals(recurrence, PluginConfiguration.SceneScheduleRecurrenceMonthlyWeekday, StringComparison.Ordinal))
        {
            var monthDelta = (candidateDate.Year - anchorDate.Year) * 12 +
                             candidateDate.Month - anchorDate.Month;
            return monthDelta >= 0 && monthDelta % interval == 0;
        }

        if (string.Equals(recurrence, PluginConfiguration.SceneScheduleRecurrenceYearly, StringComparison.Ordinal))
            return (candidateDate.Year - anchorDate.Year) % interval == 0;

        return true;
    }

    private static bool IsScheduleDateAllowed(HueSceneSchedule schedule, DateTime scheduleDate)
    {
        if (!TryGetScheduleRunDate(schedule, out var runDate))
            return false;

        if (runDate.HasValue && scheduleDate.Date != runDate.Value)
            return false;

        if (!TryGetScheduleDateBounds(schedule, out var startDate, out var endDate))
            return false;

        var date = scheduleDate.Date;
        if ((startDate.HasValue && date < startDate.Value) ||
            (endDate.HasValue && date > endDate.Value))
        {
            return false;
        }

        if (!PluginConfiguration.TryNormalizeSceneScheduleExcludedDates(
                schedule.ExcludedDates,
                out var excludedDates))
        {
            return false;
        }

        var normalizedDate = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return !excludedDates.Contains(normalizedDate, StringComparer.Ordinal);
    }

    private static bool TryGetScheduleDateBounds(
        HueSceneSchedule schedule,
        out DateTime? startDate,
        out DateTime? endDate)
    {
        startDate = null;
        endDate = null;
        if (!PluginConfiguration.TryNormalizeSceneScheduleDate(schedule?.StartDate, out var normalizedStart) ||
            !PluginConfiguration.TryNormalizeSceneScheduleDate(schedule?.EndDate, out var normalizedEnd))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(normalizedStart))
            startDate = DateTime.ParseExact(normalizedStart, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(normalizedEnd))
            endDate = DateTime.ParseExact(normalizedEnd, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        return !startDate.HasValue || !endDate.HasValue || startDate.Value <= endDate.Value;
    }

    private static bool TryParseScheduleDate(string? value, out DateTime date)
    {
        date = default;
        if (!PluginConfiguration.TryNormalizeSceneScheduleDate(value, out var normalized) ||
            string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        date = DateTime.ParseExact(normalized, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryGetScheduleRunDate(HueSceneSchedule? schedule, out DateTime? runDate)
    {
        runDate = null;
        if (!PluginConfiguration.TryNormalizeSceneScheduleDate(schedule?.RunDate, out var normalized))
            return false;

        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        runDate = DateTime.ParseExact(
            normalized,
            "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private static DateTime? GetNextRun(
        HueSceneSchedule schedule,
        DateTime serverLocalNow,
        out DateTime? nextRunUtc)
    {
        nextRunUtc = null;
        // Daily and weekly cues always find a match within one or seven days, while monthly/yearly
        // dates can be more than a week away. Use the bounded public horizon so every valid cue
        // reports its next run instead of appearing idle for part of the month.
        var occurrence = GetUpcomingOccurrences(
            schedule,
            serverLocalNow,
            1,
            MaxUpcomingHorizonDays).FirstOrDefault();
        if (occurrence == null)
            return null;

        nextRunUtc = occurrence.UtcTime;
        return occurrence.LocalTime;
    }

    /// <summary>
    /// Finds the most recent automatic occurrence that elapsed within the configured
    /// recovery window. The pending-skip flag is ignored while locating the occurrence;
    /// the scheduler consumes that flag separately so a recovered occurrence can still
    /// be skipped atomically. Only the newest missed occurrence is returned, preventing
    /// a long-enough outage from replaying a burst of old cues.
    /// </summary>
    internal static HueSceneScheduleOccurrence? GetMostRecentMissedOccurrence(
        HueSceneSchedule schedule,
        DateTime serverLocalNow,
        int catchUpMinutes)
    {
        if (schedule == null || catchUpMinutes <= PluginConfiguration.MinSceneAutomationCatchUpMinutes)
            return null;

        if (!TryGetScheduleLocalNow(schedule, serverLocalNow, out _, out var serverUtcNow))
            return null;

        var lookbackUtc = serverUtcNow.AddMinutes(-Math.Min(
            catchUpMinutes,
            PluginConfiguration.MaxSceneAutomationCatchUpMinutes));
        var lookbackServerLocal = TimeZoneInfo.ConvertTimeFromUtc(lookbackUtc, TimeZoneInfo.Local);
        var candidateSchedule = CloneSchedule(schedule);
        candidateSchedule.SkipNextOccurrence = false;
        var horizonDays = Math.Clamp(
            (int)Math.Ceiling(catchUpMinutes / 1440d) + 2,
            1,
            MaxUpcomingHorizonDays);

        // Include the preceding base solar date because a permitted offset can move
        // its actual local occurrence into the current calendar date.
        var occurrenceSearchLocal = lookbackServerLocal.AddDays(-1);
        return GetUpcomingOccurrences(
                candidateSchedule,
                occurrenceSearchLocal,
                MaxUpcomingOccurrencesPerSchedule,
                horizonDays)
            .Where(occurrence => occurrence.UtcTime > lookbackUtc && occurrence.UtcTime <= serverUtcNow)
            .OrderByDescending(occurrence => occurrence.UtcTime)
            .FirstOrDefault();
    }

    private static bool TryGetScheduleLocalNow(
        HueSceneSchedule schedule,
        DateTime serverLocalNow,
        out DateTime scheduleLocalNow,
        out DateTime serverUtcNow)
    {
        return TryGetScheduleLocalNow(
            schedule,
            serverLocalNow,
            out scheduleLocalNow,
            out serverUtcNow,
            out _);
    }

    private static bool TryGetScheduleLocalNow(
        HueSceneSchedule schedule,
        DateTime serverLocalNow,
        out DateTime scheduleLocalNow,
        out DateTime serverUtcNow,
        out TimeZoneInfo timeZone)
    {
        scheduleLocalNow = DateTime.SpecifyKind(serverLocalNow, DateTimeKind.Unspecified);
        serverUtcNow = DateTime.SpecifyKind(serverLocalNow, DateTimeKind.Utc);
        if (!PluginConfiguration.TryResolveSceneScheduleTimeZone(schedule?.TimeZoneId, out timeZone))
            return false;

        try
        {
            serverUtcNow = serverLocalNow.Kind switch
            {
                DateTimeKind.Utc => serverLocalNow,
                DateTimeKind.Local => serverLocalNow.ToUniversalTime(),
                _ => TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(serverLocalNow, DateTimeKind.Unspecified),
                    TimeZoneInfo.Local)
            };
        }
        catch (ArgumentException)
        {
            // DateTime.Now cannot normally be an invalid local wall-clock value, but a
            // caller can supply one in tests or a custom host. Preserve progress with
            // the platform's normal unspecified-to-UTC conversion in that edge case.
            serverUtcNow = DateTime.SpecifyKind(serverLocalNow, DateTimeKind.Unspecified).ToUniversalTime();
        }

        scheduleLocalNow = DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(serverUtcNow, timeZone),
            DateTimeKind.Unspecified);
        return true;
    }

    private static DateTime GetScheduleRunSlot(HueSceneSchedule schedule, DateTime serverLocalNow)
    {
        if (TryGetScheduleLocalNow(schedule, serverLocalNow, out var scheduleLocalNow, out _) &&
            PluginConfiguration.TryResolveSceneScheduleTimeZone(schedule.TimeZoneId, out var timeZone) &&
            TryGetScheduleOccurrenceForLocalDate(schedule, scheduleLocalNow.Date, timeZone, out _, out var occurrenceUtc))
        {
            // Stable UTC slots prevent duplicate polling runs when a solar event falls
            // near a local DST transition or when the host crosses a minute boundary.
            return occurrenceUtc;
        }

        if (TryGetScheduleLocalNow(schedule, serverLocalNow, out scheduleLocalNow, out _))
        {
            return new DateTime(
                scheduleLocalNow.Year,
                scheduleLocalNow.Month,
                scheduleLocalNow.Day,
                scheduleLocalNow.Hour,
                scheduleLocalNow.Minute,
                0,
                DateTimeKind.Unspecified);
        }

        return new DateTime(
            serverLocalNow.Year,
            serverLocalNow.Month,
            serverLocalNow.Day,
            serverLocalNow.Hour,
            serverLocalNow.Minute,
            0,
            DateTimeKind.Unspecified);
    }

    /// <summary>
    /// Evaluates whether a cue can run with the current saved configuration without
    /// contacting the bridge. The result intentionally contains only fixed diagnostic
    /// text and never includes credentials or connection details.
    /// </summary>
    internal static HueSceneScheduleReadiness EvaluateReadiness(
        PluginConfiguration? config,
        HueSceneSchedule schedule)
    {
        if (schedule == null)
            return new HueSceneScheduleReadiness(false, "Schedule is unavailable.");

        if (!schedule.Enabled)
            return new HueSceneScheduleReadiness(false, "Disabled.");

        if (IsRunLimitReached(schedule))
        {
            return new HueSceneScheduleReadiness(
                false,
                $"Run limit reached ({schedule.RunCount} of {schedule.MaxRuns} executions).");
        }

        if (config == null)
            return new HueSceneScheduleReadiness(false, "Plugin configuration is unavailable.");

        if (!PluginConfiguration.TryNormalizeSceneScheduleTimeMode(schedule.TimeMode, out var normalizedTimeMode))
            return new HueSceneScheduleReadiness(false, "The schedule time mode is invalid.");

        if (string.Equals(normalizedTimeMode, PluginConfiguration.SceneScheduleTimeModeFixed, StringComparison.Ordinal))
        {
            if (!PluginConfiguration.TryNormalizeSceneScheduleTime(schedule.TimeOfDay, out _))
                return new HueSceneScheduleReadiness(false, "The fixed scheduled time is invalid.");
        }
        else
        {
            if (schedule.SolarOffsetMinutes < PluginConfiguration.MinSceneScheduleSolarOffsetMinutes ||
                schedule.SolarOffsetMinutes > PluginConfiguration.MaxSceneScheduleSolarOffsetMinutes)
            {
                return new HueSceneScheduleReadiness(false, "The solar offset is outside the supported range.");
            }

            if (!PluginConfiguration.AreValidSceneScheduleSolarCoordinates(
                    schedule.SolarLatitude,
                    schedule.SolarLongitude))
            {
                return new HueSceneScheduleReadiness(false, "Solar cues require valid latitude and longitude coordinates.");
            }
        }

        if (!PluginConfiguration.TryResolveSceneScheduleTimeZone(schedule.TimeZoneId, out _))
            return new HueSceneScheduleReadiness(false, "The scheduled time zone is not available on this server.");

        if (!PluginConfiguration.TryNormalizeSceneScheduleRecurrence(schedule.Recurrence, out var normalizedRecurrence))
            return new HueSceneScheduleReadiness(false, "The schedule recurrence is invalid.");

        if (schedule.DayOfMonth < 0 || schedule.DayOfMonth > 31)
            return new HueSceneScheduleReadiness(false, "The schedule day of month is invalid.");

        if (schedule.DurationSeconds != 0 &&
            (schedule.DurationSeconds < PluginConfiguration.MinPreviewDurationSeconds ||
             schedule.DurationSeconds > PluginConfiguration.MaxPreviewDurationSeconds))
        {
            return new HueSceneScheduleReadiness(false, "The cue duration override is invalid.");
        }

        if (!PluginConfiguration.TryNormalizeSceneAutomationSchedulePlaybackPolicy(
                schedule.PlaybackPolicy,
                out _))
        {
            return new HueSceneScheduleReadiness(false, "The cue playback policy is invalid.");
        }

        if (!PluginConfiguration.TryNormalizeSceneScheduleDate(schedule.RunDate, out var normalizedRunDate))
            return new HueSceneScheduleReadiness(false, "The one-time run date is invalid.");

        if (!PluginConfiguration.TryNormalizeSceneScheduleDate(schedule.StartDate, out var normalizedStartDate))
            return new HueSceneScheduleReadiness(false, "The schedule start date is invalid.");

        if (!PluginConfiguration.TryNormalizeSceneScheduleDate(schedule.EndDate, out _))
            return new HueSceneScheduleReadiness(false, "The schedule end date is invalid.");

        if (TryParseScheduleDate(schedule.StartDate, out var startDate) &&
            TryParseScheduleDate(schedule.EndDate, out var endDate) &&
            endDate < startDate)
        {
            return new HueSceneScheduleReadiness(false, "The schedule end date is before the start date.");
        }

        if (schedule.RecurrenceInterval < PluginConfiguration.MinSceneScheduleRecurrenceInterval ||
            schedule.RecurrenceInterval > PluginConfiguration.MaxSceneScheduleRecurrenceInterval)
        {
            return new HueSceneScheduleReadiness(false, "The recurrence interval is outside the supported range.");
        }

        if (string.IsNullOrWhiteSpace(normalizedRunDate) &&
            schedule.RecurrenceInterval > PluginConfiguration.MinSceneScheduleRecurrenceInterval &&
            string.IsNullOrWhiteSpace(normalizedStartDate))
        {
            return new HueSceneScheduleReadiness(false, "A recurrence interval greater than one requires a start date anchor.");
        }

        if (!string.IsNullOrWhiteSpace(normalizedRunDate))
        {
            if (!string.IsNullOrWhiteSpace(schedule.StartDate) ||
                !string.IsNullOrWhiteSpace(schedule.EndDate))
            {
                return new HueSceneScheduleReadiness(false, "A one-time cue cannot also have a start or end date.");
            }

            if (schedule.ExcludedDates?.Any(value => !string.IsNullOrWhiteSpace(value)) == true)
                return new HueSceneScheduleReadiness(false, "A one-time cue cannot also have excluded dates.");
        }

        if (!PluginConfiguration.TryNormalizeSceneScheduleExcludedDates(
                schedule.ExcludedDates,
                out _))
        {
            return new HueSceneScheduleReadiness(false, "One or more schedule excluded dates are invalid or exceed the limit.");
        }

        if (string.IsNullOrWhiteSpace(normalizedRunDate))
        {
            if (string.Equals(normalizedRecurrence, PluginConfiguration.SceneScheduleRecurrenceMonthly, StringComparison.Ordinal))
            {
                if (schedule.DayOfMonth < 1 || schedule.DayOfMonth > 31)
                    return new HueSceneScheduleReadiness(false, "A monthly cue requires a day of month from 1 to 31.");
            }
            else if (string.Equals(normalizedRecurrence, PluginConfiguration.SceneScheduleRecurrenceMonthlyWeekday, StringComparison.Ordinal))
            {
                if (!IsMonthlyWeekdayConfigurationValid(schedule))
                {
                    return new HueSceneScheduleReadiness(false, "A monthly-weekday cue requires a valid ordinal week and weekday.");
                }
            }
            else if (string.Equals(normalizedRecurrence, PluginConfiguration.SceneScheduleRecurrenceYearly, StringComparison.Ordinal))
            {
                if (!IsYearlyConfigurationValid(schedule))
                {
                    return new HueSceneScheduleReadiness(false, "A yearly cue requires a valid month and day of month.");
                }
            }
            else if (string.Equals(normalizedRecurrence, PluginConfiguration.SceneScheduleRecurrenceWeekly, StringComparison.Ordinal) &&
                     (schedule.DaysOfWeekMask < 1 || schedule.DaysOfWeekMask > PluginConfiguration.AllSceneScheduleDaysMask))
            {
                return new HueSceneScheduleReadiness(false, "At least one valid day must be selected.");
            }
        }

        if (!string.IsNullOrWhiteSpace(schedule.PlaylistName))
        {
            var playlist = config.ScenePlaylists?.FirstOrDefault(candidate =>
                candidate != null &&
                string.Equals(candidate.Name?.Trim(), schedule.PlaylistName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (playlist == null)
                return new HueSceneScheduleReadiness(false, "The saved playlist no longer exists.");

            var playlistValidation = PluginConfiguration.ValidateScenePlaylist(
                new HueScenePlaylist
                {
                    Id = playlist.Id,
                    Name = playlist.Name,
                    PresetNames = playlist.PresetNames?.ToList() ?? new List<string>(),
                    RepeatCount = playlist.RepeatCount
                },
                config,
                "Saved playlist");
            if (playlistValidation.Count > 0)
                return new HueSceneScheduleReadiness(false, string.Join(" ", playlistValidation));
        }
        else
        {
            var preset = config.ColorPresets?.FirstOrDefault(candidate =>
                candidate != null &&
                string.Equals(candidate.Name?.Trim(), schedule.PresetName?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (preset == null)
                return new HueSceneScheduleReadiness(false, "The saved scene no longer exists.");
        }

        if (!TryResolveTargets(config, schedule, out _, out var targetError))
            return new HueSceneScheduleReadiness(false, targetError);

        return new HueSceneScheduleReadiness(true, "Ready; bridge reachability is checked when the cue runs.");
    }

    /// <summary>
    /// Testable, credential-free description of how a schedule target resolves.
    /// </summary>
    internal static bool TryResolveTarget(
        PluginConfiguration config,
        HueSceneSchedule schedule,
        out HueSceneAutomationTargetDescription description,
        out string error)
    {
        if (schedule == null)
        {
            description = new HueSceneAutomationTargetDescription();
            error = "Scene automation configuration is unavailable.";
            return false;
        }

        if (schedule.TargetAllEnabledMappings)
        {
            description = new HueSceneAutomationTargetDescription();
            error = "The schedule selects all enabled targets; resolve the target collection instead.";
            return false;
        }

        if (schedule.IncludeDefaultTarget ||
            (schedule.TargetUserIds?.Any(target => !string.IsNullOrWhiteSpace(target)) ?? false))
        {
            description = new HueSceneAutomationTargetDescription();
            error = "The schedule selects multiple targets; resolve the target collection instead.";
            return false;
        }

        return TryResolveSingleTarget(config, schedule, schedule.TargetUserId, out description, out error);
    }

    /// <summary>
    /// Resolves the credential-bearing targets for a cue without exposing those credentials
    /// outside this service. Broadcast cues include the valid global target followed by each
    /// distinct enabled custom mapping target; inherited mappings are intentionally deduplicated.
    /// </summary>
    internal static bool TryResolveTargets(
        PluginConfiguration config,
        HueSceneSchedule schedule,
        out IReadOnlyList<HueSceneAutomationTargetDescription> targets,
        out string error)
    {
        return TryResolveTargetsCore(
            config,
            schedule?.TargetUserId,
            schedule?.TargetAllEnabledMappings ?? false,
            schedule?.TargetUserIds,
            schedule?.IncludeDefaultTarget ?? false,
            out targets,
            out error);
    }

    private static bool TryResolveTargetsCore(
        PluginConfiguration config,
        string? targetUserIdValue,
        bool targetAllEnabledMappings,
        IReadOnlyList<string>? targetUserIds,
        bool includeDefaultTarget,
        out IReadOnlyList<HueSceneAutomationTargetDescription> targets,
        out string error)
    {
        targets = Array.Empty<HueSceneAutomationTargetDescription>();
        error = string.Empty;
        if (config == null)
        {
            error = "Scene automation configuration is unavailable.";
            return false;
        }

        var targetUserId = targetUserIdValue?.Trim() ?? string.Empty;
        var selectedUserIds = (targetUserIds ?? Array.Empty<string>())
            .Select(value => value?.Trim() ?? string.Empty)
            .ToArray();
        if (selectedUserIds.Length > PluginConfiguration.MaxSceneScheduleTargetMappings)
        {
            error = $"Scene cue target selection cannot contain more than {PluginConfiguration.MaxSceneScheduleTargetMappings} user mappings.";
            return false;
        }
        var hasSelectedTargets = includeDefaultTarget || selectedUserIds.Any(value => !string.IsNullOrWhiteSpace(value));

        if (targetAllEnabledMappings && (!string.IsNullOrWhiteSpace(targetUserId) || hasSelectedTargets))
        {
            error = "A broadcast scene cue cannot also select a specific or selected target.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(targetUserId) && hasSelectedTargets)
        {
            error = "A scene cue cannot combine a specific user mapping with selected targets.";
            return false;
        }

        if (!targetAllEnabledMappings && !hasSelectedTargets)
        {
            if (!TryResolveSingleTarget(config, new HueSceneSchedule(), targetUserId, out var target, out error))
                return false;

            targets = new[] { target };
            return true;
        }

        var resolved = new List<HueSceneAutomationTargetDescription>();
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!targetAllEnabledMappings)
        {
            if (includeDefaultTarget)
            {
                if (!TryResolveSingleTarget(config, new HueSceneSchedule(), string.Empty, out var selectedGlobalTarget, out var selectedGlobalError))
                {
                    error = $"The default bridge target is not ready for selected targets: {selectedGlobalError}";
                    return false;
                }

                resolved.Add(selectedGlobalTarget);
                seenTargets.Add(GetTargetIdentity(selectedGlobalTarget));
            }

            var seenUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var selectedUserId in selectedUserIds)
            {
                if (string.IsNullOrWhiteSpace(selectedUserId))
                {
                    error = "Selected scene cue targets must contain user mapping IDs.";
                    return false;
                }

                if (!seenUserIds.Add(selectedUserId))
                {
                    error = $"Selected scene cue targets contain user mapping '{selectedUserId}' more than once.";
                    return false;
                }

                if (!TryResolveSingleTarget(config, new HueSceneSchedule(), selectedUserId, out var selectedTarget, out var selectedError))
                {
                    var mapping = config.UserMappings?.FirstOrDefault(candidate =>
                        candidate != null &&
                        string.Equals(candidate.UserId?.Trim(), selectedUserId, StringComparison.OrdinalIgnoreCase));
                    var mappingLabel = mapping == null
                        ? selectedUserId
                        : GetMappingLabel(mapping);
                    error = $"Target '{mappingLabel}' is not ready for selected targets: {selectedError}";
                    return false;
                }

                if (seenTargets.Add(GetTargetIdentity(selectedTarget)))
                    resolved.Add(selectedTarget);
            }

            if (resolved.Count == 0)
            {
                error = "Selected scene cues require at least one selected target.";
                return false;
            }

            targets = resolved;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(targetUserId))
        {
            error = "A broadcast scene cue cannot also select a specific user mapping.";
            return false;
        }

        var globalConfigured = !string.IsNullOrWhiteSpace(config.HueBridgeIp) ||
                               !string.IsNullOrWhiteSpace(config.HueAppKey) ||
                               !string.IsNullOrWhiteSpace(config.HueClientKey) ||
                               !string.IsNullOrWhiteSpace(config.EntertainmentAreaId) ||
                               !string.IsNullOrWhiteSpace(config.ChannelIds);
        var globalResolved = TryResolveSingleTarget(config, new HueSceneSchedule(), string.Empty, out var globalTarget, out var globalError);
        if (globalResolved)
        {
            resolved.Add(globalTarget);
            seenTargets.Add(GetTargetIdentity(globalTarget));
        }
        else if (globalConfigured)
        {
            error = $"The default bridge target is not ready for broadcast: {globalError}";
            return false;
        }

        foreach (var mapping in config.UserMappings?.Where(candidate => candidate != null && candidate.SyncEnabled) ?? Enumerable.Empty<UserBridgeMapping>())
        {
            if (string.IsNullOrWhiteSpace(mapping.HueBridgeIp))
            {
                if (!globalResolved)
                {
                    error = $"Enabled mapping '{GetMappingLabel(mapping)}' inherits the default target, which is not configured.";
                    return false;
                }

                // An inherited mapping is the same physical target as the global entry.
                continue;
            }

            if (!TryResolveSingleTarget(config, new HueSceneSchedule(), mapping.UserId, out var mappingTarget, out var mappingError))
            {
                error = $"Target '{GetMappingLabel(mapping)}' is not ready for broadcast: {mappingError}";
                return false;
            }

            if (seenTargets.Add(GetTargetIdentity(mappingTarget)))
                resolved.Add(mappingTarget);
        }

        if (resolved.Count == 0)
        {
            error = "Broadcast scene cues require at least one configured enabled target.";
            return false;
        }

        targets = resolved;
        return true;
    }

    private static bool TryResolveSingleTarget(
        PluginConfiguration config,
        HueSceneSchedule schedule,
        string? requestedTargetUserId,
        out HueSceneAutomationTargetDescription description,
        out string error)
    {
        description = new HueSceneAutomationTargetDescription();
        error = string.Empty;
        if (config == null || schedule == null)
        {
            error = "Scene automation configuration is unavailable.";
            return false;
        }

        var targetUserId = requestedTargetUserId?.Trim() ?? string.Empty;
        var bridgeIp = config.HueBridgeIp?.Trim() ?? string.Empty;
        var appKey = config.HueAppKey?.Trim() ?? string.Empty;
        var clientKey = config.HueClientKey?.Trim() ?? string.Empty;
        var areaId = config.EntertainmentAreaId?.Trim() ?? string.Empty;
        var channelIds = config.ChannelIds?.Trim() ?? string.Empty;
        var retryAttempts = config.NetworkRetryAttempts;
        string targetLabel = "Default bridge target";

        if (!string.IsNullOrWhiteSpace(targetUserId))
        {
            var mapping = config.UserMappings?.FirstOrDefault(candidate =>
                candidate != null &&
                string.Equals(candidate.UserId?.Trim(), targetUserId, StringComparison.OrdinalIgnoreCase));
            if (mapping == null)
            {
                error = "The selected user mapping no longer exists.";
                return false;
            }

            if (!mapping.SyncEnabled)
            {
                error = "The selected user mapping is disabled.";
                return false;
            }

            targetLabel = string.IsNullOrWhiteSpace(mapping.UserName)
                ? $"User mapping {mapping.UserId?.Trim() ?? targetUserId}"
                : mapping.UserName.Trim();

            // Blank mapping targets intentionally inherit every global target field.
            if (!string.IsNullOrWhiteSpace(mapping.HueBridgeIp))
            {
                bridgeIp = mapping.HueBridgeIp.Trim();
                appKey = mapping.HueAppKey?.Trim() ?? string.Empty;
                clientKey = mapping.HueClientKey?.Trim() ?? string.Empty;
                areaId = mapping.EntertainmentAreaId?.Trim() ?? string.Empty;
                channelIds = string.IsNullOrWhiteSpace(mapping.ChannelIdsOverride)
                    ? config.ChannelIds?.Trim() ?? string.Empty
                    : mapping.ChannelIdsOverride.Trim();
            }
            else if (!string.IsNullOrWhiteSpace(mapping.ChannelIdsOverride))
            {
                channelIds = mapping.ChannelIdsOverride.Trim();
            }

            if (mapping.NetworkRetryAttemptsOverride.HasValue)
                retryAttempts = mapping.NetworkRetryAttemptsOverride.Value;
        }

        if (!Jellyfin.Plugin.Hue.HueBridgeCertificateValidation.IsValidBridgeAddress(bridgeIp))
        {
            error = "The scene target requires a valid private bridge IP address or .local host name.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(appKey) || string.IsNullOrWhiteSpace(clientKey))
        {
            error = "The scene target requires both a Hue App Key and Client Key.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(areaId))
        {
            error = "The scene target requires an entertainment area.";
            return false;
        }

        if (!PluginConfiguration.TryParseChannelIds(channelIds, out var parsedChannelIds))
        {
            error = "The scene target channel profile is invalid.";
            return false;
        }

        description = new HueSceneAutomationTargetDescription
        {
            TargetLabel = targetLabel,
            BridgeIp = bridgeIp,
            AppKey = appKey,
            ClientKey = clientKey,
            EntertainmentAreaId = areaId,
            ChannelIds = parsedChannelIds.Count == 0 ? null : parsedChannelIds,
            RetryAttempts = retryAttempts
        };
        return true;
    }

    private static string GetTargetIdentity(HueSceneAutomationTargetDescription target)
        => $"{target.BridgeIp.Trim().TrimEnd('.').ToLowerInvariant()}|{target.EntertainmentAreaId.Trim().ToLowerInvariant()}";

    private static HashSet<int> GetValidChannelIds(JsonElement areaConfiguration)
    {
        var channelIds = new HashSet<int>();
        if (!areaConfiguration.TryGetProperty("channels", out var channels) ||
            channels.ValueKind != JsonValueKind.Array)
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

    private static string GetMappingLabel(UserBridgeMapping mapping)
        => string.IsNullOrWhiteSpace(mapping.UserName)
            ? $"User mapping {mapping.UserId?.Trim() ?? "unknown"}"
            : mapping.UserName.Trim();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Check once on startup so a service restart during a configured minute still
        // honors the cue, while the run-slot guard prevents duplicate polling triggers.
        await RunDueSchedulesAsync(DateTime.Now, stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunDueSchedulesAsync(DateTime.Now, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal hosted-service shutdown.
        }
    }

    internal async Task RunDueSchedulesAsync(DateTime localNow, CancellationToken cancellationToken)
    {
        EnsureDeferredRunsLoaded();
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.SceneAutomationEnabled)
            return;

        var schedules = config.SceneSchedules?
            .Where(schedule => schedule != null)
            .Select((schedule, index) => new
            {
                Schedule = CloneSchedule(schedule),
                Index = index
            })
            .OrderByDescending(entry => entry.Schedule.Priority)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Schedule)
            .ToArray();
        if (schedules == null || schedules.Length == 0)
        {
            ClearAllDeferredRuns();
            return;
        }

        var catchUpMinutes = Math.Clamp(
            config.SceneAutomationCatchUpMinutes,
            PluginConfiguration.MinSceneAutomationCatchUpMinutes,
            PluginConfiguration.MaxSceneAutomationCatchUpMinutes);
        var deferMinutes = Math.Clamp(
            config.SceneAutomationDeferMinutes,
            PluginConfiguration.MinSceneAutomationDeferMinutes,
            PluginConfiguration.MaxSceneAutomationDeferMinutes);
        var playbackScope = GetPlaybackConflictScope(config);

        PruneDeferredRuns(schedules, config);

        foreach (var schedule in schedules)
        {
            var deferDuringPlayback = string.Equals(
                GetEffectivePlaybackPolicy(config, schedule),
                PluginConfiguration.SceneAutomationPlaybackPolicyDefer,
                StringComparison.OrdinalIgnoreCase);
            HueSceneDeferredRun? deferredRun = null;
            var deferredExpired = false;
            var hasDeferredRun = deferDuringPlayback && TryGetDeferredRun(
                schedule.Id,
                localNow,
                deferMinutes,
                out deferredRun,
                out deferredExpired);
            if (deferredExpired)
            {
                if (TryClaimRunSlot(schedule.Id, deferredRun!.OccurrenceSlot))
                {
                    RecordSkippedOccurrence(
                        config,
                        schedule,
                        CreateDeferredExpiredResult(
                            config,
                            schedule,
                            deferMinutes,
                            deferredRun!.Restored));
                    if (!string.IsNullOrWhiteSpace(schedule.RunDate))
                        DisableCompletedOneTimeSchedule(config, schedule);
                }

                continue;
            }

            var isDue = hasDeferredRun || IsDue(schedule, localNow);
            var recoveredOccurrence = hasDeferredRun || isDue
                ? null
                : GetMostRecentMissedOccurrence(schedule, localNow, catchUpMinutes);
            if (!isDue && recoveredOccurrence == null)
                continue;

            var slot = hasDeferredRun
                ? deferredRun!.OccurrenceSlot
                : isDue
                    ? GetScheduleRunSlot(schedule, localNow)
                    : recoveredOccurrence!.UtcTime;
            var wasCatchUp = !hasDeferredRun && !isDue;
            var wasDeferredRestored = hasDeferredRun && deferredRun!.Restored;

            // A pending administrator skip always wins over a deferred occurrence. It is
            // safe to consume it while playback is active because no bridge mutation occurs.
            var playbackActiveForSchedule = IsPlaybackActiveForSchedule(config, schedule, playbackScope);
            if (deferDuringPlayback && playbackActiveForSchedule)
            {
                if (TryConsumeSkippedOccurrence(config, schedule, out var blockedSkippedResult, out var blockedSkipPersistenceFailed))
                {
                    if (TryClaimRunSlot(schedule.Id, slot))
                    {
                        blockedSkippedResult!.WasCatchUp = wasCatchUp;
                        blockedSkippedResult.WasDeferred = hasDeferredRun;
                        blockedSkippedResult.WasDeferredRestored = wasDeferredRestored;
                        RemoveDeferredRun(schedule.Id);
                        RecordSkippedOccurrence(config, schedule, blockedSkippedResult);
                    }

                    continue;
                }

                // If clearing a skip marker failed, leave both the marker and any pending
                // deferred occurrence intact so a later poll can retry safely.
                if (blockedSkipPersistenceFailed)
                    continue;

                if (!hasDeferredRun)
                {
                    QueueDeferredRun(schedule, slot, localNow, deferMinutes);
                }
                continue;
            }

            if (!TryClaimRunSlot(schedule.Id, slot))
                continue;

            if (hasDeferredRun)
                RemoveDeferredRun(schedule.Id);

            if (TryConsumeSkippedOccurrence(config, schedule, out var skippedResult, out var skipPersistenceFailed))
            {
                if (skippedResult != null)
                {
                    skippedResult.WasCatchUp = wasCatchUp;
                    skippedResult.WasDeferred = hasDeferredRun;
                    skippedResult.WasDeferredRestored = wasDeferredRestored;
                }
                RecordSkippedOccurrence(config, schedule, skippedResult!);
                continue;
            }

            // A pending skip is an explicit administrator instruction. If clearing it
            // could not be persisted, do not fall through and run the cue anyway. The
            // marker remains intact so the next eligible occurrence can retry safely.
            if (skipPersistenceFailed)
                continue;

            var result = await RunScheduleTrackedAsync(
                config,
                schedule,
                cancellationToken,
                wasCatchUp: wasCatchUp,
                wasDeferred: hasDeferredRun,
                wasDeferredRestored: wasDeferredRestored,
                targetScopedPlayback: string.Equals(
                    playbackScope,
                    PluginConfiguration.SceneAutomationPlaybackScopeMatchingTarget,
                    StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);
            if (result.Succeeded)
            {
                DisableCompletedOneTimeSchedule(config, schedule);
                _logger.LogInformation(
                    "Hue scene schedule {0} displayed {1} for target {2}",
                    schedule.Name,
                    string.IsNullOrWhiteSpace(schedule.PlaylistName)
                        ? $"preset {schedule.PresetName}"
                        : $"playlist {schedule.PlaylistName}",
                    result.TargetLabel);
            }
            else
            {
                _logger.LogWarning(
                    "Hue scene schedule {0} could not run: {1}",
                    schedule.Name,
                    result.Message);
            }
        }
    }

    private void PruneDeferredRuns(
        IReadOnlyList<HueSceneSchedule> schedules,
        PluginConfiguration? config)
    {
        HashSet<string> configuredIds = schedules
            .Where(schedule => schedule.Enabled &&
                string.Equals(
                    GetEffectivePlaybackPolicy(config, schedule),
                    PluginConfiguration.SceneAutomationPlaybackPolicyDefer,
                    StringComparison.OrdinalIgnoreCase))
            .Select(schedule => schedule.Id?.Trim() ?? string.Empty)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] staleIds;
        lock (_deferredRunLock)
        {
            staleIds = _deferredRuns.Keys
                .Where(id => !configuredIds.Contains(id))
                .ToArray();
        }

        foreach (var staleId in staleIds)
            RemoveDeferredRun(staleId, persist: false);

        if (staleIds.Length > 0)
            PersistDeferredRuns();
    }

    private void ClearAllDeferredRuns()
    {
        string[] ids;
        lock (_deferredRunLock)
        {
            ids = _deferredRuns.Keys.ToArray();
        }

        foreach (var id in ids)
            RemoveDeferredRun(id, persist: false);

        if (ids.Length > 0)
            PersistDeferredRuns();
    }

    private bool TryConsumeSkippedOccurrence(
        PluginConfiguration config,
        HueSceneSchedule schedule,
        out HueSceneAutomationRunResult? result,
        out bool persistenceFailed)
    {
        result = null;
        persistenceFailed = false;
        var key = schedule.Id?.Trim() ?? string.Empty;
        var configuredSchedule = config.SceneSchedules?.FirstOrDefault(candidate =>
            candidate != null &&
            string.Equals(candidate.Id?.Trim(), key, StringComparison.OrdinalIgnoreCase));
        if (configuredSchedule == null || !configuredSchedule.SkipNextOccurrence)
            return false;

        var previousEnabled = configuredSchedule.Enabled;
        lock (_runtimeStateLock)
        {
            if (_runtimeStates.TryGetValue(key, out var state) && state.ActiveRuns > 0)
                return false;

            configuredSchedule.SkipNextOccurrence = false;
            if (!string.IsNullOrWhiteSpace(configuredSchedule.RunDate))
                configuredSchedule.Enabled = false;
        }

        try
        {
            Plugin.Instance?.SaveConfiguration();
            result = CreateSkippedOccurrenceResult(config, schedule);
            return true;
        }
        catch (Exception ex)
        {
            persistenceFailed = true;
            lock (_runtimeStateLock)
            {
                configuredSchedule.SkipNextOccurrence = true;
                configuredSchedule.Enabled = previousEnabled;
            }

            _logger.LogWarning(ex, "Could not persist skipped occurrence for Hue scene schedule {0}", schedule.Name);
            return false;
        }
    }

    private void RecordSkippedOccurrence(
        PluginConfiguration config,
        HueSceneSchedule schedule,
        HueSceneAutomationRunResult result)
    {
        EnsureHistoryLoaded();
        var key = schedule.Id?.Trim() ?? string.Empty;
        lock (_runtimeStateLock)
        {
            if (!_runtimeStates.TryGetValue(key, out var state))
            {
                state = new HueSceneScheduleRuntimeState
                {
                    RunCount = Math.Max(0, schedule.RunCount)
                };
                _runtimeStates[key] = state;
            }

            state.LastRunAtUtc = result.RunAtUtc;
            state.LastSucceeded = false;
            state.LastSkipped = true;
            state.LastWasCatchUp = result.WasCatchUp;
            state.LastWasDeferred = result.WasDeferred;
            state.LastWasDeferredRestored = result.WasDeferredRestored;
            state.LastMessage = result.Message;
            state.LastCleanupWarning = null;
            state.LastTargetResults = Array.Empty<HueSceneScheduleTargetResult>();
            state.DeferredPending = false;
            state.DeferredOccurrenceSlot = null;
            state.DeferredAtLocal = null;
            state.DeferredUntilLocal = null;
            state.DeferredRestored = false;
            result.RunCount = state.RunCount;
        }

        lock (_historyLock)
        {
            _runHistory.Insert(0, CloneRunResult(result));
            TrimRunHistoryLocked();
        }

        PersistSceneScheduleHistory();
    }

    private static HueSceneAutomationRunResult CreateSkippedOccurrenceResult(
        PluginConfiguration config,
        HueSceneSchedule schedule,
        string? message = null,
        bool wasDeferred = false,
        bool wasDeferredRestored = false)
    {
        var isPlaylist = !string.IsNullOrWhiteSpace(schedule.PlaylistName);
        var preset = config.ColorPresets?.FirstOrDefault(candidate =>
            candidate != null &&
            string.Equals(candidate.Name?.Trim(), schedule.PresetName?.Trim(), StringComparison.OrdinalIgnoreCase));
        PluginConfiguration.TryNormalizeColorPresetEffect(preset?.Effect, out var effect);
        return new HueSceneAutomationRunResult
        {
            ScheduleId = schedule.Id,
            ScheduleName = schedule.Name?.Trim() ?? string.Empty,
            PresetName = schedule.PresetName?.Trim() ?? string.Empty,
            PlaylistName = schedule.PlaylistName?.Trim() ?? string.Empty,
            Effect = isPlaylist ? PluginConfiguration.SceneScheduleEffectPlaylist : effect,
            EffectSpeedPercent = isPlaylist || preset == null
                ? PluginConfiguration.DefaultColorPresetEffectSpeedPercent
                : PluginConfiguration.ClampColorPresetEffectSpeedPercent(preset.EffectSpeedPercent),
            TargetLabel = ResolveTargetLabel(config, schedule),
            TargetUserIds = schedule.TargetUserIds?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                ?? Array.Empty<string>(),
            IncludeDefaultTarget = schedule.IncludeDefaultTarget,
            Succeeded = false,
            Skipped = true,
            WasDeferred = wasDeferred,
            WasDeferredRestored = wasDeferredRestored,
            Message = message ?? (string.IsNullOrWhiteSpace(schedule.RunDate)
                ? "The next automatic scene occurrence was skipped by the administrator."
                : "The one-time scene occurrence was skipped by the administrator and the cue was disabled."),
            RunAtUtc = DateTime.UtcNow
        };
    }

    private void DisableCompletedOneTimeSchedule(PluginConfiguration config, HueSceneSchedule schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule.RunDate))
            return;

        var configuredSchedule = config.SceneSchedules?.FirstOrDefault(candidate =>
            candidate != null &&
            string.Equals(candidate.Id?.Trim(), schedule.Id?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (configuredSchedule == null || !configuredSchedule.Enabled)
            return;

        var previousEnabled = configuredSchedule.Enabled;
        configuredSchedule.Enabled = false;
        try
        {
            Plugin.Instance?.SaveConfiguration();
        }
        catch (Exception ex)
        {
            configuredSchedule.Enabled = previousEnabled;
            _logger.LogWarning(ex, "One-time Hue scene schedule {0} ran but could not persist its completed state", schedule.Name);
        }
    }

    private async Task<HueSceneAutomationRunResult> RunScheduleCoreAsync(
        PluginConfiguration config,
        HueSceneSchedule schedule,
        CancellationToken cancellationToken,
        bool targetScopedPlayback = false)
    {
        if (!string.IsNullOrWhiteSpace(schedule.PlaylistName))
        {
            var playlist = config.ScenePlaylists?.FirstOrDefault(candidate =>
                candidate != null &&
                string.Equals(candidate.Name?.Trim(), schedule.PlaylistName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (playlist == null)
                return Failure(schedule.Id, "The scene schedule references a saved playlist that no longer exists.", schedule);

            // A schedule owns its target selection. Clone the credential-free playlist so
            // its saved target preference is never mutated by a scheduled run.
            var scheduledPlaylist = new HueScenePlaylist
            {
                Id = playlist.Id,
                Name = playlist.Name,
                PresetNames = playlist.PresetNames?.ToList() ?? new List<string>(),
                RepeatCount = playlist.RepeatCount,
                TargetUserId = schedule.TargetAllEnabledMappings
                    || schedule.IncludeDefaultTarget
                    || (schedule.TargetUserIds?.Count ?? 0) > 0
                    ? string.Empty
                    : schedule.TargetUserId?.Trim() ?? string.Empty,
                TargetAllEnabledMappings = schedule.TargetAllEnabledMappings
            };
            var selectedTargetIds = schedule.IncludeDefaultTarget || (schedule.TargetUserIds?.Count ?? 0) > 0
                ? schedule.TargetUserIds?.ToList() ?? new List<string>()
                : null;
            var playlistRun = await RunPlaylistPreviewAsync(
                scheduledPlaylist,
                cancellationToken,
                selectedTargetIds,
                schedule.IncludeDefaultTarget,
                targetScopedPlayback).ConfigureAwait(false);
            return BuildPlaylistScheduleRunResult(config, schedule, playlistRun);
        }

        var preset = config.ColorPresets?.FirstOrDefault(candidate =>
            candidate != null &&
            string.Equals(candidate.Name?.Trim(), schedule.PresetName?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (preset == null)
        {
            return Failure(schedule.Id, "The scene schedule references a saved scene that no longer exists.", schedule);
        }

        if (!TryResolveTargets(config, schedule, out var targets, out var targetError))
            return Failure(schedule.Id, targetError, schedule);

        PluginConfiguration.TryNormalizeColorPresetEffect(preset.Effect, out var effect);
        var targetResults = new List<HueSceneScheduleTargetResult>();
        foreach (var target in targets)
        {
            var targetResult = await RunScheduleTargetAsync(
                schedule,
                preset,
                target,
                cancellationToken,
                targetScopedPlayback).ConfigureAwait(false);
            targetResults.Add(targetResult);
        }

        var succeededCount = targetResults.Count(result => result.Succeeded);
        var allSucceeded = targetResults.Count > 0 && succeededCount == targetResults.Count;
        var message = BuildAggregateRunMessage(targetResults, succeededCount);
        var cleanupWarning = string.Join(
            " ",
            targetResults
                .Where(result => !string.IsNullOrWhiteSpace(result.CleanupWarning))
                .Select(result => $"{result.TargetLabel}: {result.CleanupWarning!.Trim()}"));
        return new HueSceneAutomationRunResult
        {
            ScheduleId = schedule.Id,
            ScheduleName = schedule.Name?.Trim() ?? string.Empty,
            PresetName = preset.Name?.Trim() ?? string.Empty,
            Effect = effect,
            EffectSpeedPercent = PluginConfiguration.ClampColorPresetEffectSpeedPercent(preset.EffectSpeedPercent),
            TargetLabel = ResolveTargetLabel(config, schedule),
            TargetUserIds = schedule.TargetUserIds?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                ?? Array.Empty<string>(),
            IncludeDefaultTarget = schedule.IncludeDefaultTarget,
            Succeeded = allSucceeded,
            Message = message,
            CleanupWarning = string.IsNullOrWhiteSpace(cleanupWarning) ? null : cleanupWarning,
            TargetResults = targetResults,
            RunAtUtc = DateTime.UtcNow
        };
    }

    private static HueSceneAutomationRunResult BuildPlaylistScheduleRunResult(
        PluginConfiguration config,
        HueSceneSchedule schedule,
        HueScenePlaylistRunResult playlistRun)
    {
        var targetResults = playlistRun.TargetResults?.Select(target => new HueSceneScheduleTargetResult
        {
            TargetLabel = target.TargetLabel,
            Succeeded = target.Succeeded,
            Message = target.Message,
            CleanupWarning = target.CleanupWarning,
            AvailableChannelCount = target.AvailableChannelCount,
            SelectedChannelCount = target.SelectedChannelCount
        }).ToArray() ?? Array.Empty<HueSceneScheduleTargetResult>();
        return new HueSceneAutomationRunResult
        {
            ScheduleId = schedule.Id?.Trim() ?? string.Empty,
            ScheduleName = schedule.Name?.Trim() ?? string.Empty,
            PresetName = string.Empty,
            PlaylistName = schedule.PlaylistName?.Trim() ?? playlistRun.PlaylistName?.Trim() ?? string.Empty,
            PlaylistRepeatCount = playlistRun.RepeatCount,
            Effect = PluginConfiguration.SceneScheduleEffectPlaylist,
            EffectSpeedPercent = PluginConfiguration.DefaultColorPresetEffectSpeedPercent,
            TargetLabel = string.IsNullOrWhiteSpace(playlistRun.TargetLabel)
                ? ResolveTargetLabel(config, schedule)
                : playlistRun.TargetLabel,
            TargetUserIds = schedule.TargetUserIds?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                ?? Array.Empty<string>(),
            IncludeDefaultTarget = schedule.IncludeDefaultTarget,
            Succeeded = playlistRun.Succeeded,
            Message = playlistRun.Message,
            CleanupWarning = playlistRun.CleanupWarning,
            TargetResults = targetResults,
            PlaylistSteps = playlistRun.Steps ?? Array.Empty<HueScenePlaylistStepResult>(),
            RunAtUtc = playlistRun.RunAtUtc
        };
    }

    private async Task<HueSceneScheduleTargetResult> RunScheduleTargetAsync(
        HueSceneSchedule schedule,
        HueColorPreset preset,
        HueSceneAutomationTargetDescription target,
        CancellationToken cancellationToken,
        bool targetScopedPlayback = false)
    {
        try
        {
            _hueClient.RetryAttempts = target.RetryAttempts;
            var areaConfiguration = await _hueClient.GetEntertainmentConfiguration(
                target.BridgeIp,
                target.AppKey,
                target.EntertainmentAreaId,
                cancellationToken).ConfigureAwait(false);
            if (areaConfiguration == null)
            {
                return new HueSceneScheduleTargetResult
                {
                    TargetLabel = target.TargetLabel,
                    Succeeded = false,
                    Message = "The Hue bridge did not return the configured entertainment area."
                };
            }

            var availableChannelIds = GetValidChannelIds(areaConfiguration.Value);
            var selectedChannelCount = target.ChannelIds?.Count ?? availableChannelIds.Count;
            if (target.ChannelIds != null)
            {
                var missingChannelIds = target.ChannelIds
                    .Where(channelId => !availableChannelIds.Contains(channelId))
                    .OrderBy(channelId => channelId)
                    .ToArray();
                if (missingChannelIds.Length > 0)
                {
                    return new HueSceneScheduleTargetResult
                    {
                        TargetLabel = target.TargetLabel,
                        Succeeded = false,
                        Message = $"The channel profile references IDs not present in this entertainment area: {string.Join(", ", missingChannelIds)}.",
                        AvailableChannelCount = availableChannelIds.Count,
                        SelectedChannelCount = selectedChannelCount
                    };
                }
            }

            var scopedTester = targetScopedPlayback
                ? _streamTester as IHueTargetScopedStreamTester
                : null;
            var preview = scopedTester != null
                ? await scopedTester.PreviewAsyncForTarget(
                    target.BridgeIp,
                    target.AppKey,
                    target.ClientKey,
                    target.EntertainmentAreaId,
                    areaConfiguration.Value,
                    target.ChannelIds,
                    preset.Red,
                    preset.Green,
                    preset.Blue,
                    preset.BrightnessPercent,
                    GetEffectiveDurationSeconds(schedule, preset),
                    cancellationToken,
                    GetEffectiveTransitionSeconds(schedule, preset),
                    GetEffectiveTransitionOutSeconds(schedule, preset),
                    preset.Effect,
                    PluginConfiguration.ClampColorPresetEffectSpeedPercent(preset.EffectSpeedPercent)).ConfigureAwait(false)
                : await _streamTester.PreviewAsync(
                    target.BridgeIp,
                    target.AppKey,
                    target.ClientKey,
                    target.EntertainmentAreaId,
                    areaConfiguration.Value,
                    target.ChannelIds,
                    preset.Red,
                    preset.Green,
                    preset.Blue,
                    preset.BrightnessPercent,
                    GetEffectiveDurationSeconds(schedule, preset),
                    cancellationToken,
                    GetEffectiveTransitionSeconds(schedule, preset),
                    GetEffectiveTransitionOutSeconds(schedule, preset),
                    preset.Effect,
                    PluginConfiguration.ClampColorPresetEffectSpeedPercent(preset.EffectSpeedPercent)).ConfigureAwait(false);
            return new HueSceneScheduleTargetResult
            {
                TargetLabel = target.TargetLabel,
                Succeeded = preview.Succeeded,
                Message = preview.Message,
                CleanupWarning = preview.CleanupWarning,
                AvailableChannelCount = availableChannelIds.Count,
                SelectedChannelCount = selectedChannelCount
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hue scene schedule {0} failed for target {1}", schedule.Name, target.TargetLabel);
            return new HueSceneScheduleTargetResult
            {
                TargetLabel = target.TargetLabel,
                Succeeded = false,
                Message = "The scene preview failed unexpectedly."
            };
        }
    }

    private static string BuildAggregateRunMessage(
        IReadOnlyList<HueSceneScheduleTargetResult> targetResults,
        int succeededCount)
    {
        if (targetResults.Count == 1)
            return targetResults[0].Message?.Trim() ?? string.Empty;

        var total = targetResults.Count;
        if (succeededCount == total)
            return $"Displayed scheduled scene on all {total} targets.";

        var failures = string.Join(
            "; ",
            targetResults
                .Where(result => !result.Succeeded)
                .Select(result => $"{result.TargetLabel}: {result.Message}"));
        return succeededCount == 0
            ? $"No targets completed the scheduled scene. {failures}"
            : $"Displayed scheduled scene on {succeededCount} of {total} targets. Failed targets: {failures}";
    }

    private async Task<HueSceneAutomationRunResult> RunScheduleTrackedAsync(
        PluginConfiguration config,
        HueSceneSchedule schedule,
        CancellationToken cancellationToken,
        bool wasCatchUp = false,
        bool wasDeferred = false,
        bool wasDeferredRestored = false,
        bool targetScopedPlayback = false)
    {
        if (!TryBeginRun(schedule, out var currentRunCount, out var alreadyRunning))
        {
            var exhausted = Failure(
                schedule.Id,
                alreadyRunning
                    ? "The scene cue is already running."
                    : $"The scene cue has reached its maximum of {schedule.MaxRuns} executions.",
                schedule);
            exhausted.RunCount = currentRunCount;
            return exhausted;
        }

        HueSceneAutomationRunResult? result = null;
        try
        {
            result = await RunScheduleCoreAsync(
                config,
                schedule,
                cancellationToken,
                targetScopedPlayback).ConfigureAwait(false);
            result.WasCatchUp = wasCatchUp;
            result.WasDeferred = wasDeferred;
            result.WasDeferredRestored = wasDeferredRestored;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = Failure(schedule.Id, "The scene cue run was canceled.", schedule);
            result.WasCatchUp = wasCatchUp;
            result.WasDeferred = wasDeferred;
            result.WasDeferredRestored = wasDeferredRestored;
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hue scene schedule {0} failed unexpectedly", schedule.Name);
            result = Failure(schedule.Id, "The scheduled scene could not be completed.", schedule);
            result.WasCatchUp = wasCatchUp;
            result.WasDeferred = wasDeferred;
            result.WasDeferredRestored = wasDeferredRestored;
            return result;
        }
        finally
        {
            CompleteRun(config, schedule, result);
        }
    }

    private HueSceneScheduleRuntimeState GetRuntimeState(string? scheduleId)
    {
        var key = scheduleId?.Trim() ?? string.Empty;
        lock (_runtimeStateLock)
        {
            if (_runtimeStates.TryGetValue(key, out var state))
                return state.Clone();

            var configuredRunCount = Plugin.Instance?.Configuration?.SceneSchedules?
                .FirstOrDefault(candidate =>
                    candidate != null &&
                    string.Equals(candidate.Id?.Trim(), key, StringComparison.OrdinalIgnoreCase))?
                .RunCount ?? 0;
            return new HueSceneScheduleRuntimeState
            {
                RunCount = Math.Max(0, configuredRunCount)
            };
        }
    }

    private bool TryBeginRun(
        HueSceneSchedule schedule,
        out int currentRunCount,
        out bool alreadyRunning)
    {
        var key = schedule.Id?.Trim() ?? string.Empty;
        lock (_runtimeStateLock)
        {
            if (!_runtimeStates.TryGetValue(key, out var state))
            {
                state = new HueSceneScheduleRuntimeState
                {
                    RunCount = Math.Max(0, schedule.RunCount)
                };
                _runtimeStates[key] = state;
            }

            currentRunCount = state.RunCount;
            alreadyRunning = state.ActiveRuns > 0;
            if (alreadyRunning)
                return false;

            if (schedule.MaxRuns > 0 && state.RunCount + state.ActiveRuns >= schedule.MaxRuns)
                return false;

            state.ActiveRuns++;
            return true;
        }
    }

    private void CompleteRun(
        PluginConfiguration config,
        HueSceneSchedule schedule,
        HueSceneAutomationRunResult? result)
    {
        EnsureHistoryLoaded();
        var key = schedule.Id?.Trim() ?? string.Empty;
        var shouldPersistRunState = false;
        lock (_runtimeStateLock)
        {
            if (!_runtimeStates.TryGetValue(key, out var state))
            {
                state = new HueSceneScheduleRuntimeState
                {
                    RunCount = Math.Max(0, schedule.RunCount)
                };
                _runtimeStates[key] = state;
            }

            state.ActiveRuns = Math.Max(0, state.ActiveRuns - 1);
            state.RunCount++;
            state.LastRunAtUtc = result?.RunAtUtc ?? DateTime.UtcNow;
            state.LastSucceeded = result?.Succeeded ?? false;
            state.LastSkipped = result?.Skipped ?? false;
            state.LastWasCatchUp = result?.WasCatchUp ?? false;
            state.LastWasDeferred = result?.WasDeferred ?? false;
            state.LastWasDeferredRestored = result?.WasDeferredRestored ?? false;
            state.LastMessage = result?.Message ?? "The scheduled scene ended without a result.";
            state.LastCleanupWarning = result?.CleanupWarning;
            state.LastTargetResults = result?.TargetResults?.Select(CloneTargetResult).ToArray()
                ?? Array.Empty<HueSceneScheduleTargetResult>();
            state.DeferredPending = false;
            state.DeferredOccurrenceSlot = null;
            state.DeferredAtLocal = null;
            state.DeferredUntilLocal = null;
            state.DeferredRestored = false;
            if (result != null)
                result.RunCount = state.RunCount;

            var configuredSchedule = config.SceneSchedules?.FirstOrDefault(candidate =>
                candidate != null &&
                string.Equals(candidate.Id?.Trim(), key, StringComparison.OrdinalIgnoreCase));
            if (configuredSchedule != null)
            {
                configuredSchedule.RunCount = state.RunCount;
                if (configuredSchedule.MaxRuns > 0 && state.RunCount >= configuredSchedule.MaxRuns)
                    configuredSchedule.Enabled = false;
                shouldPersistRunState = configuredSchedule.MaxRuns > 0;
            }
        }

        if (result != null)
        {
            lock (_historyLock)
            {
                _runHistory.Insert(0, CloneRunResult(result));
                TrimRunHistoryLocked();
            }
        }

        if (shouldPersistRunState)
            PersistFiniteScheduleRunState(schedule.Name);

        PersistSceneScheduleHistory();
    }

    private void PersistFiniteScheduleRunState(string? scheduleName)
    {
        try
        {
            Plugin.Instance?.SaveConfiguration();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Hue scene schedule {0} completed but its finite run count could not be persisted",
                scheduleName);
        }
    }

    private void EnsureHistoryLoaded()
    {
        lock (_historyLock)
        {
            if (_historyLoaded)
                return;
        }

        var config = Plugin.Instance?.Configuration;
        var persistedEntries = new List<HueSceneScheduleHistoryEntry>();
        var shouldSave = false;
        if (config != null)
        {
            config.PersistedSceneScheduleHistory ??= new List<HueSceneScheduleHistoryEntry>();
            if (!config.PersistSceneScheduleHistory)
            {
                if (config.PersistedSceneScheduleHistory.Count > 0)
                {
                    config.PersistedSceneScheduleHistory.Clear();
                    shouldSave = true;
                }
            }
            else
            {
                persistedEntries = config.PersistedSceneScheduleHistory
                    .Where(entry => entry != null)
                    .Take(config.GetSceneScheduleHistoryRetentionCount())
                    .Select(CloneHistoryEntry)
                    .ToList();
                if (persistedEntries.Count != config.PersistedSceneScheduleHistory.Count)
                {
                    config.PersistedSceneScheduleHistory = persistedEntries
                        .Select(CloneHistoryEntry)
                        .ToList();
                    shouldSave = true;
                }
            }
        }

        var loadedResults = persistedEntries
            .Select(ToRunResult)
            .ToArray();
        lock (_historyLock)
        {
            if (_historyLoaded)
                return;

            _runHistory.Clear();
            _runHistory.AddRange(loadedResults);
            _historyLoaded = true;
        }

        if (loadedResults.Length > 0)
        {
            lock (_runtimeStateLock)
            {
                foreach (var group in loadedResults
                             .Where(result => !string.IsNullOrWhiteSpace(result.ScheduleId))
                             .GroupBy(result => result.ScheduleId, StringComparer.OrdinalIgnoreCase))
                {
                    var latest = group.First();
                    if (!_runtimeStates.TryGetValue(group.Key, out var state))
                    {
                        state = new HueSceneScheduleRuntimeState();
                        _runtimeStates[group.Key] = state;
                    }

                    var configuredRunCount = Plugin.Instance?.Configuration?.SceneSchedules?
                        .FirstOrDefault(candidate =>
                            candidate != null &&
                            string.Equals(candidate.Id?.Trim(), group.Key, StringComparison.OrdinalIgnoreCase))?
                        .RunCount ?? 0;
                    state.RunCount = Math.Max(configuredRunCount, Math.Max(latest.RunCount, group.Count()));
                    state.LastRunAtUtc = latest.RunAtUtc;
                    state.LastSucceeded = latest.Succeeded;
                    state.LastSkipped = latest.Skipped;
                    state.LastWasCatchUp = latest.WasCatchUp;
                    state.LastWasDeferred = latest.WasDeferred;
                    state.LastWasDeferredRestored = latest.WasDeferredRestored;
                    state.LastMessage = latest.Message;
                    state.LastCleanupWarning = latest.CleanupWarning;
                    state.LastTargetResults = latest.TargetResults?.Select(CloneTargetResult).ToArray()
                        ?? Array.Empty<HueSceneScheduleTargetResult>();
                }
            }
        }

        if (shouldSave)
            SavePersistedSceneScheduleHistoryConfiguration();
    }

    private void PersistSceneScheduleHistory()
    {
        var plugin = Plugin.Instance;
        var config = plugin?.Configuration;
        if (plugin == null || config == null)
            return;

        List<HueSceneScheduleHistoryEntry> entries;
        lock (_historyLock)
        {
            entries = _runHistory
                .Take(config.GetSceneScheduleHistoryRetentionCount())
                .Select(ToHistoryEntry)
                .ToList();
        }

        config.PersistedSceneScheduleHistory ??= new List<HueSceneScheduleHistoryEntry>();
        if (config.PersistSceneScheduleHistory)
        {
            config.PersistedSceneScheduleHistory = entries;
        }
        else
        {
            if (config.PersistedSceneScheduleHistory.Count == 0)
                return;

            config.PersistedSceneScheduleHistory.Clear();
        }

        SavePersistedSceneScheduleHistoryConfiguration();
    }

    private void TrimRunHistoryLocked()
    {
        var retentionCount = Plugin.Instance?.Configuration?.GetSceneScheduleHistoryRetentionCount()
            ?? PluginConfiguration.DefaultSceneScheduleHistoryRetentionCount;
        if (_runHistory.Count > retentionCount)
            _runHistory.RemoveRange(retentionCount, _runHistory.Count - retentionCount);
    }

    private void SavePersistedSceneScheduleHistoryConfiguration()
    {
        try
        {
            Plugin.Instance?.SaveConfiguration();
        }
        catch (Exception ex)
        {
            // Persistence is diagnostic-only and must never interrupt a cue run.
            _logger.LogWarning(ex, "Could not persist Hue scheduled-scene history");
        }
    }

    private static HueSceneScheduleHistoryEntry ToHistoryEntry(HueSceneAutomationRunResult result)
    {
        return new HueSceneScheduleHistoryEntry
        {
            ScheduleId = result.ScheduleId,
            ScheduleName = result.ScheduleName,
            PresetName = result.PresetName,
            PlaylistName = result.PlaylistName,
            PlaylistRepeatCount = result.PlaylistRepeatCount,
            Effect = result.Effect,
            EffectSpeedPercent = result.EffectSpeedPercent,
            TargetLabel = result.TargetLabel,
            TargetUserIds = result.TargetUserIds?.ToList() ?? new List<string>(),
            IncludeDefaultTarget = result.IncludeDefaultTarget,
            Succeeded = result.Succeeded,
            Skipped = result.Skipped,
            WasCatchUp = result.WasCatchUp,
            WasDeferred = result.WasDeferred,
            WasDeferredRestored = result.WasDeferredRestored,
            Message = result.Message,
            CleanupWarning = result.CleanupWarning,
            TargetResults = result.TargetResults?.Select(CloneTargetResult).ToList()
                ?? new List<HueSceneScheduleTargetResult>(),
            RunAtUtc = result.RunAtUtc,
            RunCount = result.RunCount
        };
    }

    private static HueSceneScheduleHistoryEntry CloneHistoryEntry(HueSceneScheduleHistoryEntry source)
    {
        return new HueSceneScheduleHistoryEntry
        {
            ScheduleId = source.ScheduleId?.Trim() ?? string.Empty,
            ScheduleName = source.ScheduleName?.Trim() ?? string.Empty,
            PresetName = source.PresetName?.Trim() ?? string.Empty,
            PlaylistName = source.PlaylistName?.Trim() ?? string.Empty,
            PlaylistRepeatCount = Math.Max(
                PluginConfiguration.MinScenePlaylistRepeatCount,
                source.PlaylistRepeatCount),
            Effect = PluginConfiguration.TryNormalizeColorPresetEffect(source.Effect, out var effect)
                ? effect
                : string.Equals(source.Effect?.Trim(), PluginConfiguration.SceneScheduleEffectPlaylist, StringComparison.OrdinalIgnoreCase)
                    ? PluginConfiguration.SceneScheduleEffectPlaylist
                : PluginConfiguration.ColorPresetEffectSolid,
            EffectSpeedPercent = PluginConfiguration.ClampColorPresetEffectSpeedPercent(source.EffectSpeedPercent),
            TargetLabel = source.TargetLabel?.Trim(),
            TargetUserIds = source.TargetUserIds?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                ?? new List<string>(),
            IncludeDefaultTarget = source.IncludeDefaultTarget,
            Succeeded = source.Succeeded,
            Skipped = source.Skipped,
            WasCatchUp = source.WasCatchUp,
            WasDeferred = source.WasDeferred,
            WasDeferredRestored = source.WasDeferredRestored,
            Message = source.Message?.Trim() ?? string.Empty,
            CleanupWarning = source.CleanupWarning?.Trim(),
            TargetResults = source.TargetResults?.Where(target => target != null)
                .Select(CloneTargetResult)
                .ToList() ?? new List<HueSceneScheduleTargetResult>(),
            RunAtUtc = source.RunAtUtc,
            RunCount = Math.Max(0, source.RunCount)
        };
    }

    private static HueSceneAutomationRunResult ToRunResult(HueSceneScheduleHistoryEntry entry)
    {
        return new HueSceneAutomationRunResult
        {
            ScheduleId = entry.ScheduleId?.Trim() ?? string.Empty,
            ScheduleName = entry.ScheduleName?.Trim() ?? string.Empty,
            PresetName = entry.PresetName?.Trim() ?? string.Empty,
            PlaylistName = entry.PlaylistName?.Trim() ?? string.Empty,
            PlaylistRepeatCount = Math.Max(
                PluginConfiguration.MinScenePlaylistRepeatCount,
                entry.PlaylistRepeatCount),
            Effect = string.Equals(entry.Effect?.Trim(), PluginConfiguration.SceneScheduleEffectPlaylist, StringComparison.OrdinalIgnoreCase)
                ? PluginConfiguration.SceneScheduleEffectPlaylist
                : PluginConfiguration.TryNormalizeColorPresetEffect(entry.Effect, out var effect)
                    ? effect
                    : PluginConfiguration.ColorPresetEffectSolid,
            EffectSpeedPercent = PluginConfiguration.ClampColorPresetEffectSpeedPercent(entry.EffectSpeedPercent),
            TargetLabel = entry.TargetLabel?.Trim(),
            TargetUserIds = entry.TargetUserIds?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                ?? Array.Empty<string>(),
            IncludeDefaultTarget = entry.IncludeDefaultTarget,
            Succeeded = entry.Succeeded,
            Skipped = entry.Skipped,
            WasCatchUp = entry.WasCatchUp,
            WasDeferred = entry.WasDeferred,
            WasDeferredRestored = entry.WasDeferredRestored,
            Message = entry.Message?.Trim() ?? string.Empty,
            CleanupWarning = entry.CleanupWarning?.Trim(),
            TargetResults = entry.TargetResults?.Where(target => target != null)
                .Select(CloneTargetResult)
                .ToArray() ?? Array.Empty<HueSceneScheduleTargetResult>(),
            RunAtUtc = entry.RunAtUtc,
            RunCount = Math.Max(0, entry.RunCount)
        };
    }

    private static HueSceneAutomationRunResult CloneRunResult(HueSceneAutomationRunResult source)
    {
        return new HueSceneAutomationRunResult
        {
            ScheduleId = source.ScheduleId,
            ScheduleName = source.ScheduleName,
            PresetName = source.PresetName,
            PlaylistName = source.PlaylistName,
            PlaylistRepeatCount = source.PlaylistRepeatCount,
            Effect = source.Effect,
            EffectSpeedPercent = source.EffectSpeedPercent,
            TargetLabel = source.TargetLabel,
            TargetUserIds = source.TargetUserIds?.ToArray() ?? Array.Empty<string>(),
            IncludeDefaultTarget = source.IncludeDefaultTarget,
            Succeeded = source.Succeeded,
            Skipped = source.Skipped,
            WasCatchUp = source.WasCatchUp,
            WasDeferred = source.WasDeferred,
            WasDeferredRestored = source.WasDeferredRestored,
            Message = source.Message,
            CleanupWarning = source.CleanupWarning,
            TargetResults = source.TargetResults?.Select(CloneTargetResult).ToArray()
                ?? Array.Empty<HueSceneScheduleTargetResult>(),
            PlaylistSteps = source.PlaylistSteps?.Select(ClonePlaylistStepResult).ToArray()
                ?? Array.Empty<HueScenePlaylistStepResult>(),
            RunAtUtc = source.RunAtUtc,
            RunCount = source.RunCount
        };
    }

    internal static string ResolveTargetLabel(PluginConfiguration? config, HueSceneSchedule schedule)
    {
        if (schedule.TargetAllEnabledMappings)
            return "All enabled targets";

        var selectedTargetCount = schedule.TargetUserIds?.Count(value => !string.IsNullOrWhiteSpace(value)) ?? 0;
        if (schedule.IncludeDefaultTarget || selectedTargetCount > 0)
        {
            return schedule.IncludeDefaultTarget
                ? selectedTargetCount == 0
                    ? "Default bridge target"
                    : $"Default bridge + {selectedTargetCount} selected target(s)"
                : $"{selectedTargetCount} selected target(s)";
        }

        var targetUserId = schedule.TargetUserId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(targetUserId))
            return "Default bridge target";

        var mapping = config?.UserMappings?.FirstOrDefault(candidate =>
            candidate != null &&
            string.Equals(candidate.UserId?.Trim(), targetUserId, StringComparison.OrdinalIgnoreCase));
        if (mapping == null)
            return "Missing user mapping";

        return string.IsNullOrWhiteSpace(mapping.UserName)
            ? $"User mapping {mapping.UserId?.Trim() ?? targetUserId}"
            : mapping.UserName.Trim();
    }

    private bool TryGetDeferredRun(
        string scheduleId,
        DateTime localNow,
        int deferMinutes,
        out HueSceneDeferredRun? deferredRun,
        out bool expired)
    {
        deferredRun = null;
        expired = false;
        var key = scheduleId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        lock (_deferredRunLock)
        {
            if (!_deferredRuns.TryGetValue(key, out var current))
                return false;

            if (localNow - current.DeferredAtLocal >= TimeSpan.FromMinutes(deferMinutes))
            {
                _deferredRuns.Remove(key);
                deferredRun = current;
                expired = true;
            }
            else
            {
                deferredRun = current;
            }
        }

        if (expired)
        {
            ClearDeferredRuntimeState(key);
            PersistDeferredRuns();
        }

        return !expired;
    }

    /// <summary>
    /// Loads deferred occurrences once from the credential-free runtime state stored in
    /// plugin configuration. Invalid, duplicate, disabled, and deleted-cue entries are
    /// discarded before they can be replayed after a restart.
    /// </summary>
    private void EnsureDeferredRunsLoaded()
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
            return;

        Dictionary<string, HueSceneDeferredRun> loaded;
        bool shouldSave;
        lock (_deferredRunLock)
        {
            if (_deferredRunsLoaded)
                return;

            config.PersistedSceneAutomationDeferredRuns ??= new List<HueSceneDeferredRunEntry>();
            var enabledScheduleIds = (config.SceneSchedules ?? new List<HueSceneSchedule>())
                .Where(schedule => schedule != null && schedule.Enabled &&
                    string.Equals(
                        GetEffectivePlaybackPolicy(config, schedule),
                        PluginConfiguration.SceneAutomationPlaybackPolicyDefer,
                        StringComparison.OrdinalIgnoreCase))
                .Select(schedule => schedule.Id?.Trim() ?? string.Empty)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var normalizedEntries = config.PersistedSceneAutomationDeferredRuns
                .Where(entry => entry != null &&
                    !string.IsNullOrWhiteSpace(entry.ScheduleId) &&
                    enabledScheduleIds.Contains(entry.ScheduleId.Trim()) &&
                    entry.OccurrenceSlot != default &&
                    entry.DeferredAtLocal != default)
                .GroupBy(entry => entry.ScheduleId.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(entry => entry.ScheduleId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            shouldSave = normalizedEntries.Count != config.PersistedSceneAutomationDeferredRuns.Count ||
                normalizedEntries.Where((entry, index) =>
                    !string.Equals(
                        entry.ScheduleId.Trim(),
                        config.PersistedSceneAutomationDeferredRuns[index]?.ScheduleId?.Trim(),
                        StringComparison.OrdinalIgnoreCase) ||
                    entry.OccurrenceSlot != config.PersistedSceneAutomationDeferredRuns[index]?.OccurrenceSlot ||
                    entry.DeferredAtLocal != config.PersistedSceneAutomationDeferredRuns[index]?.DeferredAtLocal)
                    .Any();
            if (shouldSave)
            {
                config.PersistedSceneAutomationDeferredRuns = normalizedEntries
                    .Select(entry => new HueSceneDeferredRunEntry
                    {
                        ScheduleId = entry.ScheduleId.Trim(),
                        OccurrenceSlot = entry.OccurrenceSlot,
                        DeferredAtLocal = entry.DeferredAtLocal
                    })
                    .ToList();
            }

            loaded = normalizedEntries.ToDictionary(
                entry => entry.ScheduleId.Trim(),
                entry => new HueSceneDeferredRun(
                    entry.OccurrenceSlot,
                    entry.DeferredAtLocal,
                    restored: true),
                StringComparer.OrdinalIgnoreCase);
            _deferredRuns.Clear();
            foreach (var entry in loaded)
                _deferredRuns[entry.Key] = entry.Value;
            _deferredRunsLoaded = true;
        }

        var deferMinutes = Math.Clamp(
            config.SceneAutomationDeferMinutes,
            PluginConfiguration.MinSceneAutomationDeferMinutes,
            PluginConfiguration.MaxSceneAutomationDeferMinutes);
        foreach (var entry in loaded)
        {
            MarkDeferredRuntimeState(
                entry.Key,
                entry.Value.OccurrenceSlot,
                entry.Value.DeferredAtLocal,
                deferMinutes,
                restored: true);
        }

        if (shouldSave)
            SavePersistedDeferredRunsConfiguration();

        if (loaded.Count > 0)
        {
            _logger.LogInformation(
                "Restored {0} deferred Hue scene occurrence(s) after scheduler startup",
                loaded.Count);
        }
    }

    private void QueueDeferredRun(
        HueSceneSchedule schedule,
        DateTime occurrenceSlot,
        DateTime localNow,
        int deferMinutes)
    {
        var key = schedule.Id?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
            return;

        var queued = false;
        lock (_deferredRunLock)
        {
            if (!_deferredRuns.ContainsKey(key))
            {
                _deferredRuns[key] = new HueSceneDeferredRun(occurrenceSlot, localNow, restored: false);
                queued = true;
            }
        }

        MarkDeferredRuntimeState(key, occurrenceSlot, localNow, deferMinutes, restored: false);
        if (queued)
        {
            PersistDeferredRuns();
            _logger.LogInformation(
                "Deferred Hue scene schedule {0} until playback is idle (window {1} minutes)",
                schedule.Name,
                deferMinutes);
        }
    }

    private void RemoveDeferredRun(string scheduleId, bool persist = true)
    {
        var key = scheduleId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
            return;

        var removed = false;
        lock (_deferredRunLock)
        {
            removed = _deferredRuns.Remove(key);
        }

        ClearDeferredRuntimeState(key);
        if (removed && persist)
            PersistDeferredRuns();
    }

    private void MarkDeferredRuntimeState(
        string scheduleId,
        DateTime occurrenceSlot,
        DateTime deferredAtLocal,
        int deferMinutes,
        bool restored)
    {
        lock (_runtimeStateLock)
        {
            if (!_runtimeStates.TryGetValue(scheduleId, out var state))
            {
                state = new HueSceneScheduleRuntimeState();
                _runtimeStates[scheduleId] = state;
            }

            state.DeferredPending = true;
            state.DeferredOccurrenceSlot = occurrenceSlot;
            state.DeferredAtLocal = deferredAtLocal;
            state.DeferredUntilLocal = deferredAtLocal.AddMinutes(deferMinutes);
            state.DeferredRestored = restored;
            state.LastMessage = restored
                ? "Restored after scheduler restart; waiting for active playback to finish before running this automatic cue."
                : "Waiting for active playback to finish before running this automatic cue.";
        }
    }

    private void ClearDeferredRuntimeState(string scheduleId)
    {
        lock (_runtimeStateLock)
        {
            if (!_runtimeStates.TryGetValue(scheduleId, out var state))
                return;

            state.DeferredPending = false;
            state.DeferredOccurrenceSlot = null;
            state.DeferredAtLocal = null;
            state.DeferredUntilLocal = null;
            state.DeferredRestored = false;
        }
    }

    private static HueSceneAutomationRunResult CreateDeferredExpiredResult(
        PluginConfiguration config,
        HueSceneSchedule schedule,
        int deferMinutes,
        bool restored)
        => CreateSkippedOccurrenceResult(
            config,
            schedule,
            $"The automatic scene occurrence was skipped because playback remained active beyond the {deferMinutes}-minute defer window.",
            wasDeferred: true,
            wasDeferredRestored: restored);

    private void PersistDeferredRuns()
    {
        var plugin = Plugin.Instance;
        var config = plugin?.Configuration;
        if (plugin == null || config == null)
            return;

        List<HueSceneDeferredRunEntry> entries;
        lock (_deferredRunLock)
        {
            entries = _deferredRuns
                .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new HueSceneDeferredRunEntry
                {
                    ScheduleId = entry.Key,
                    OccurrenceSlot = entry.Value.OccurrenceSlot,
                    DeferredAtLocal = entry.Value.DeferredAtLocal
                })
                .ToList();
        }

        config.PersistedSceneAutomationDeferredRuns = entries;
        SavePersistedDeferredRunsConfiguration();
    }

    private void SavePersistedDeferredRunsConfiguration()
    {
        try
        {
            Plugin.Instance?.SaveConfiguration();
        }
        catch (Exception ex)
        {
            // Runtime persistence must never interrupt a cue or make the scheduler fail.
            _logger.LogWarning(ex, "Could not persist deferred Hue scene occurrences");
        }
    }

    private bool TryClaimRunSlot(string scheduleId, DateTime slot)
    {
        lock (_runSlotLock)
        {
            var staleBefore = slot.AddDays(-2);
            foreach (var stale in _lastRunSlots
                         .Where(entry => entry.Value < staleBefore)
                         .Select(entry => entry.Key)
                         .ToArray())
            {
                _lastRunSlots.Remove(stale);
            }

            if (_lastRunSlots.TryGetValue(scheduleId, out var previous) && previous == slot)
                return false;

            _lastRunSlots[scheduleId] = slot;
            return true;
        }
    }

    private sealed class HueSceneScheduleConflictWindow
    {
        public HueSceneScheduleConflictWindow(
            HueSceneScheduleOccurrence occurrence,
            DateTime endUtc,
            string targetLabel)
        {
            Occurrence = occurrence;
            EndUtc = endUtc;
            TargetLabel = targetLabel;
        }

        public HueSceneScheduleOccurrence Occurrence { get; }
        public DateTime EndUtc { get; }
        public string TargetLabel { get; }
    }

    private static HueSceneSchedule CloneSchedule(HueSceneSchedule source)
    {
        return new HueSceneSchedule
        {
            Id = source.Id,
            Name = source.Name,
            PresetName = source.PresetName,
            PlaylistName = source.PlaylistName,
            Priority = source.Priority,
            PlaybackPolicy = source.PlaybackPolicy,
            TargetUserId = source.TargetUserId,
            TargetAllEnabledMappings = source.TargetAllEnabledMappings,
            TimeOfDay = source.TimeOfDay,
            TimeMode = source.TimeMode,
            SolarOffsetMinutes = source.SolarOffsetMinutes,
            SolarLatitude = source.SolarLatitude,
            SolarLongitude = source.SolarLongitude,
            TimeZoneId = source.TimeZoneId,
            Recurrence = source.Recurrence,
            RecurrenceInterval = source.RecurrenceInterval,
            DayOfMonth = source.DayOfMonth,
            MonthOfYear = source.MonthOfYear,
            WeekOfMonth = source.WeekOfMonth,
            DayOfWeek = source.DayOfWeek,
            DurationSeconds = source.DurationSeconds,
            MaxRuns = source.MaxRuns,
            RunCount = source.RunCount,
            RunDate = source.RunDate,
            StartDate = source.StartDate,
            EndDate = source.EndDate,
            ExcludedDates = source.ExcludedDates?.ToList() ?? new List<string>(),
            DaysOfWeekMask = source.DaysOfWeekMask,
            Enabled = source.Enabled,
            SkipNextOccurrence = source.SkipNextOccurrence
        };
    }

    private static HueSceneScheduleTargetResult CloneTargetResult(HueSceneScheduleTargetResult source)
    {
        return new HueSceneScheduleTargetResult
        {
            TargetLabel = source.TargetLabel?.Trim() ?? string.Empty,
            Succeeded = source.Succeeded,
            Message = source.Message?.Trim() ?? string.Empty,
            CleanupWarning = source.CleanupWarning?.Trim(),
            AvailableChannelCount = source.AvailableChannelCount,
            SelectedChannelCount = source.SelectedChannelCount
        };
    }

    private static HueScenePlaylistStepResult ClonePlaylistStepResult(HueScenePlaylistStepResult source)
    {
        return new HueScenePlaylistStepResult
        {
            Index = source.Index,
            RepeatIndex = source.RepeatIndex,
            PresetName = source.PresetName,
            Effect = source.Effect,
            EffectSpeedPercent = source.EffectSpeedPercent,
            DurationSeconds = source.DurationSeconds,
            TransitionSeconds = source.TransitionSeconds,
            TransitionOutSeconds = source.TransitionOutSeconds,
            Succeeded = source.Succeeded,
            Message = source.Message,
            CleanupWarning = source.CleanupWarning,
            TargetResults = source.TargetResults?.Select(CloneTargetResult).ToArray()
                ?? Array.Empty<HueSceneScheduleTargetResult>()
        };
    }

    private static HueSceneAutomationRunResult Failure(
        string? scheduleId,
        string message,
        HueSceneSchedule? schedule = null,
        string? targetLabel = null)
    {
        return new HueSceneAutomationRunResult
        {
            ScheduleId = scheduleId ?? string.Empty,
            ScheduleName = schedule?.Name?.Trim() ?? string.Empty,
            PresetName = schedule?.PresetName?.Trim() ?? string.Empty,
            PlaylistName = schedule?.PlaylistName?.Trim() ?? string.Empty,
            TargetLabel = targetLabel,
            TargetUserIds = schedule?.TargetUserIds?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                ?? Array.Empty<string>(),
            IncludeDefaultTarget = schedule?.IncludeDefaultTarget ?? false,
            Succeeded = false,
            Message = message,
            RunAtUtc = DateTime.UtcNow
        };
    }
}

internal sealed class HueSceneScheduleRuntimeState
{
    public int ActiveRuns { get; set; }
    public int RunCount { get; set; }
    public DateTime? LastRunAtUtc { get; set; }
    public bool? LastSucceeded { get; set; }
    public bool LastSkipped { get; set; }
    public bool LastWasCatchUp { get; set; }
    public bool LastWasDeferred { get; set; }
    public bool LastWasDeferredRestored { get; set; }
    public bool DeferredPending { get; set; }
    public bool DeferredRestored { get; set; }
    public DateTime? DeferredOccurrenceSlot { get; set; }
    public DateTime? DeferredAtLocal { get; set; }
    public DateTime? DeferredUntilLocal { get; set; }
    public string? LastMessage { get; set; }
    public string? LastCleanupWarning { get; set; }
    public IReadOnlyList<HueSceneScheduleTargetResult> LastTargetResults { get; set; } = Array.Empty<HueSceneScheduleTargetResult>();

    public HueSceneScheduleRuntimeState Clone()
    {
        return new HueSceneScheduleRuntimeState
        {
            ActiveRuns = ActiveRuns,
            RunCount = RunCount,
            LastRunAtUtc = LastRunAtUtc,
            LastSucceeded = LastSucceeded,
            LastSkipped = LastSkipped,
            LastWasCatchUp = LastWasCatchUp,
            LastWasDeferred = LastWasDeferred,
            LastWasDeferredRestored = LastWasDeferredRestored,
            DeferredPending = DeferredPending,
            DeferredRestored = DeferredRestored,
            DeferredOccurrenceSlot = DeferredOccurrenceSlot,
            DeferredAtLocal = DeferredAtLocal,
            DeferredUntilLocal = DeferredUntilLocal,
            LastMessage = LastMessage,
            LastCleanupWarning = LastCleanupWarning,
            LastTargetResults = LastTargetResults?.Select(target => new HueSceneScheduleTargetResult
            {
                TargetLabel = target.TargetLabel,
                Succeeded = target.Succeeded,
                Message = target.Message,
                CleanupWarning = target.CleanupWarning,
                AvailableChannelCount = target.AvailableChannelCount,
                SelectedChannelCount = target.SelectedChannelCount
            }).ToArray() ?? Array.Empty<HueSceneScheduleTargetResult>()
        };
    }
}

internal sealed class HueSceneDeferredRun
{
    public HueSceneDeferredRun(DateTime occurrenceSlot, DateTime deferredAtLocal, bool restored)
    {
        OccurrenceSlot = occurrenceSlot;
        DeferredAtLocal = deferredAtLocal;
        Restored = restored;
    }

    public DateTime OccurrenceSlot { get; }
    public DateTime DeferredAtLocal { get; }
    public bool Restored { get; }
}

internal sealed class HueSceneScheduleReadiness
{
    public HueSceneScheduleReadiness(bool ready, string message)
    {
        Ready = ready;
        Message = message;
    }

    public bool Ready { get; }
    public string Message { get; }
}

/// <summary>
/// Internal resolved target. The credential fields never leave the service and are not
/// part of any API result.
/// </summary>
internal sealed class HueSceneAutomationTargetDescription
{
    public string TargetLabel { get; init; } = string.Empty;
    public string BridgeIp { get; init; } = string.Empty;
    public string AppKey { get; init; } = string.Empty;
    public string ClientKey { get; init; } = string.Empty;
    public string EntertainmentAreaId { get; init; } = string.Empty;
    public IReadOnlySet<int>? ChannelIds { get; init; }
    public int RetryAttempts { get; init; }
}

/// <summary>
/// Sanitized result returned by an immediate or scheduled scene cue run.
/// </summary>
public sealed class HueSceneAutomationRunResult
{
    [JsonPropertyName("scheduleId")]
    public string ScheduleId { get; init; } = string.Empty;

    [JsonPropertyName("scheduleName")]
    public string ScheduleName { get; init; } = string.Empty;

    [JsonPropertyName("presetName")]
    public string PresetName { get; init; } = string.Empty;

    [JsonPropertyName("playlistName")]
    public string PlaylistName { get; init; } = string.Empty;

    [JsonPropertyName("playlistRepeatCount")]
    public int PlaylistRepeatCount { get; init; } = PluginConfiguration.DefaultScenePlaylistRepeatCount;

    [JsonPropertyName("effect")]
    public string Effect { get; init; } = PluginConfiguration.ColorPresetEffectSolid;

    [JsonPropertyName("effectSpeedPercent")]
    public int EffectSpeedPercent { get; init; } = PluginConfiguration.DefaultColorPresetEffectSpeedPercent;

    [JsonPropertyName("targetLabel")]
    public string? TargetLabel { get; init; }

    [JsonPropertyName("targetUserIds")]
    public IReadOnlyList<string> TargetUserIds { get; init; } = Array.Empty<string>();

    [JsonPropertyName("includeDefaultTarget")]
    public bool IncludeDefaultTarget { get; init; }

    [JsonPropertyName("succeeded")]
    public bool Succeeded { get; init; }

    [JsonPropertyName("skipped")]
    public bool Skipped { get; init; }

    [JsonPropertyName("wasCatchUp")]
    public bool WasCatchUp { get; set; }

    [JsonPropertyName("wasDeferred")]
    public bool WasDeferred { get; set; }

    [JsonPropertyName("wasDeferredRestored")]
    public bool WasDeferredRestored { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("cleanupWarning")]
    public string? CleanupWarning { get; init; }

    [JsonPropertyName("targetResults")]
    public IReadOnlyList<HueSceneScheduleTargetResult> TargetResults { get; init; } = Array.Empty<HueSceneScheduleTargetResult>();

    [JsonPropertyName("playlistSteps")]
    public IReadOnlyList<HueScenePlaylistStepResult> PlaylistSteps { get; init; } = Array.Empty<HueScenePlaylistStepResult>();

    [JsonPropertyName("runAtUtc")]
    public DateTime RunAtUtc { get; init; }

    [JsonPropertyName("runCount")]
    public int RunCount { get; internal set; }
}

/// <summary>
/// Sanitized result returned by an ordered saved-scene playlist preview.
/// </summary>
public sealed class HueScenePlaylistRunResult
{
    [JsonPropertyName("playlistId")]
    public string PlaylistId { get; init; } = string.Empty;

    [JsonPropertyName("playlistName")]
    public string PlaylistName { get; init; } = string.Empty;

    [JsonPropertyName("playlistRepeatCount")]
    public int PlaylistRepeatCount { get; init; } = PluginConfiguration.DefaultScenePlaylistRepeatCount;

    [JsonPropertyName("repeatCount")]
    public int RepeatCount { get; init; } = PluginConfiguration.DefaultScenePlaylistRepeatCount;

    [JsonPropertyName("targetLabel")]
    public string? TargetLabel { get; init; }

    [JsonPropertyName("targetAllEnabledMappings")]
    public bool TargetAllEnabledMappings { get; init; }

    [JsonPropertyName("targetUserIds")]
    public IReadOnlyList<string> TargetUserIds { get; init; } = Array.Empty<string>();

    [JsonPropertyName("includeDefaultTarget")]
    public bool IncludeDefaultTarget { get; init; }

    [JsonPropertyName("succeeded")]
    public bool Succeeded { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("cleanupWarning")]
    public string? CleanupWarning { get; init; }

    [JsonPropertyName("steps")]
    public IReadOnlyList<HueScenePlaylistStepResult> Steps { get; init; } = Array.Empty<HueScenePlaylistStepResult>();

    [JsonPropertyName("targetResults")]
    public IReadOnlyList<HueScenePlaylistTargetResult> TargetResults { get; init; } = Array.Empty<HueScenePlaylistTargetResult>();

    [JsonPropertyName("runAtUtc")]
    public DateTime RunAtUtc { get; init; }
}

/// <summary>
/// Sanitized result for one saved-scene playlist step.
/// </summary>
public sealed class HueScenePlaylistStepResult
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("repeatIndex")]
    public int RepeatIndex { get; init; } = PluginConfiguration.DefaultScenePlaylistRepeatCount;

    [JsonPropertyName("presetName")]
    public string PresetName { get; init; } = string.Empty;

    [JsonPropertyName("effect")]
    public string Effect { get; init; } = PluginConfiguration.ColorPresetEffectSolid;

    [JsonPropertyName("effectSpeedPercent")]
    public int EffectSpeedPercent { get; init; } = PluginConfiguration.DefaultColorPresetEffectSpeedPercent;

    [JsonPropertyName("durationSeconds")]
    public int DurationSeconds { get; init; }

    [JsonPropertyName("transitionSeconds")]
    public int TransitionSeconds { get; init; }

    [JsonPropertyName("transitionOutSeconds")]
    public int TransitionOutSeconds { get; init; }

    [JsonPropertyName("succeeded")]
    public bool Succeeded { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("cleanupWarning")]
    public string? CleanupWarning { get; init; }

    [JsonPropertyName("targetResults")]
    public IReadOnlyList<HueSceneScheduleTargetResult> TargetResults { get; init; } = Array.Empty<HueSceneScheduleTargetResult>();
}

/// <summary>
/// Aggregate credential-free outcome for one playlist target across all steps.
/// </summary>
public sealed class HueScenePlaylistTargetResult
{
    [JsonPropertyName("targetLabel")]
    public string TargetLabel { get; init; } = string.Empty;

    [JsonPropertyName("succeeded")]
    public bool Succeeded { get; init; }

    [JsonPropertyName("completedStepCount")]
    public int CompletedStepCount { get; init; }

    [JsonPropertyName("totalStepCount")]
    public int TotalStepCount { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("cleanupWarning")]
    public string? CleanupWarning { get; init; }

    [JsonPropertyName("availableChannelCount")]
    public int AvailableChannelCount { get; init; }

    [JsonPropertyName("selectedChannelCount")]
    public int SelectedChannelCount { get; init; }
}

/// <summary>
/// Credential-free upcoming occurrence for one scene cue.
/// </summary>
public sealed class HueSceneScheduleOccurrence
{
    [JsonPropertyName("scheduleId")]
    public string ScheduleId { get; init; } = string.Empty;

    [JsonPropertyName("scheduleName")]
    public string ScheduleName { get; init; } = string.Empty;

    [JsonPropertyName("presetName")]
    public string PresetName { get; init; } = string.Empty;

    [JsonPropertyName("playlistName")]
    public string PlaylistName { get; init; } = string.Empty;

    [JsonPropertyName("playlistRepeatCount")]
    public int PlaylistRepeatCount { get; init; } = PluginConfiguration.DefaultScenePlaylistRepeatCount;

    [JsonPropertyName("priority")]
    public int Priority { get; init; }

    [JsonPropertyName("targetAllEnabledMappings")]
    public bool TargetAllEnabledMappings { get; init; }

    [JsonPropertyName("targetUserIds")]
    public IReadOnlyList<string> TargetUserIds { get; init; } = Array.Empty<string>();

    [JsonPropertyName("includeDefaultTarget")]
    public bool IncludeDefaultTarget { get; init; }

    [JsonPropertyName("timeMode")]
    public string TimeMode { get; init; } = PluginConfiguration.SceneScheduleTimeModeFixed;

    [JsonPropertyName("solarOffsetMinutes")]
    public int SolarOffsetMinutes { get; init; }

    [JsonPropertyName("solarLatitude")]
    public double? SolarLatitude { get; init; }

    [JsonPropertyName("solarLongitude")]
    public double? SolarLongitude { get; init; }

    [JsonPropertyName("effect")]
    public string Effect { get; init; } = PluginConfiguration.ColorPresetEffectSolid;

    [JsonPropertyName("effectSpeedPercent")]
    public int EffectSpeedPercent { get; init; } = PluginConfiguration.DefaultColorPresetEffectSpeedPercent;

    [JsonPropertyName("durationSeconds")]
    public int DurationSeconds { get; init; }

    [JsonPropertyName("playlistStepCount")]
    public int PlaylistStepCount { get; init; }

    [JsonPropertyName("playlistTotalDurationSeconds")]
    public int PlaylistTotalDurationSeconds { get; init; }

    [JsonPropertyName("transitionSeconds")]
    public int TransitionSeconds { get; init; }

    [JsonPropertyName("transitionOutSeconds")]
    public int TransitionOutSeconds { get; init; }

    [JsonPropertyName("recurrenceInterval")]
    public int RecurrenceInterval { get; init; } = PluginConfiguration.MinSceneScheduleRecurrenceInterval;

    [JsonPropertyName("monthOfYear")]
    public int MonthOfYear { get; init; }

    [JsonPropertyName("weekOfMonth")]
    public int WeekOfMonth { get; init; }

    [JsonPropertyName("dayOfWeek")]
    public int DayOfWeek { get; init; } = -1;

    [JsonPropertyName("timeZoneId")]
    public string TimeZoneId { get; init; } = string.Empty;

    [JsonPropertyName("timeZoneDisplayName")]
    public string TimeZoneDisplayName { get; init; } = string.Empty;

    [JsonPropertyName("localTime")]
    public DateTime LocalTime { get; init; }

    [JsonPropertyName("utcTime")]
    public DateTime UtcTime { get; init; }
}

/// <summary>
/// Credential-free overlap between two upcoming scheduled-cue execution windows.
/// </summary>
public sealed class HueSceneScheduleConflict
{
    [JsonPropertyName("firstScheduleId")]
    public string FirstScheduleId { get; init; } = string.Empty;

    [JsonPropertyName("firstScheduleName")]
    public string FirstScheduleName { get; init; } = string.Empty;

    [JsonPropertyName("firstTargetLabel")]
    public string FirstTargetLabel { get; init; } = string.Empty;

    [JsonPropertyName("firstOccurrenceUtc")]
    public DateTime FirstOccurrenceUtc { get; init; }

    [JsonPropertyName("firstOccurrenceLocal")]
    public DateTime FirstOccurrenceLocal { get; init; }

    [JsonPropertyName("firstDurationSeconds")]
    public int FirstDurationSeconds { get; init; }

    [JsonPropertyName("firstPriority")]
    public int FirstPriority { get; init; }

    [JsonPropertyName("secondScheduleId")]
    public string SecondScheduleId { get; init; } = string.Empty;

    [JsonPropertyName("secondScheduleName")]
    public string SecondScheduleName { get; init; } = string.Empty;

    [JsonPropertyName("secondTargetLabel")]
    public string SecondTargetLabel { get; init; } = string.Empty;

    [JsonPropertyName("secondOccurrenceUtc")]
    public DateTime SecondOccurrenceUtc { get; init; }

    [JsonPropertyName("secondOccurrenceLocal")]
    public DateTime SecondOccurrenceLocal { get; init; }

    [JsonPropertyName("secondDurationSeconds")]
    public int SecondDurationSeconds { get; init; }

    [JsonPropertyName("secondPriority")]
    public int SecondPriority { get; init; }

    [JsonPropertyName("overlapSeconds")]
    public int OverlapSeconds { get; init; }

    [JsonPropertyName("resolutionHint")]
    public string ResolutionHint { get; init; } = string.Empty;
}

/// <summary>
/// Sanitized status for one configured scene cue.
/// </summary>
public sealed class HueSceneScheduleRuntimeStatus
{
    [JsonPropertyName("scheduleId")]
    public string ScheduleId { get; init; } = string.Empty;

    [JsonPropertyName("scheduleName")]
    public string ScheduleName { get; init; } = string.Empty;

    [JsonPropertyName("presetName")]
    public string PresetName { get; init; } = string.Empty;

    [JsonPropertyName("playlistName")]
    public string PlaylistName { get; init; } = string.Empty;

    [JsonPropertyName("priority")]
    public int Priority { get; init; }

    [JsonPropertyName("playbackPolicy")]
    public string PlaybackPolicy { get; init; } = PluginConfiguration.SceneAutomationPlaybackPolicySkip;

    [JsonPropertyName("playbackPolicyOverride")]
    public string PlaybackPolicyOverride { get; init; } = PluginConfiguration.SceneAutomationPlaybackPolicyInherit;

    [JsonPropertyName("effect")]
    public string Effect { get; init; } = PluginConfiguration.ColorPresetEffectSolid;

    [JsonPropertyName("effectSpeedPercent")]
    public int EffectSpeedPercent { get; init; } = PluginConfiguration.DefaultColorPresetEffectSpeedPercent;

    [JsonPropertyName("durationSeconds")]
    public int DurationSeconds { get; init; }

    [JsonPropertyName("playlistStepCount")]
    public int PlaylistStepCount { get; init; }

    [JsonPropertyName("playlistRepeatCount")]
    public int PlaylistRepeatCount { get; init; } = PluginConfiguration.DefaultScenePlaylistRepeatCount;

    [JsonPropertyName("playlistTotalDurationSeconds")]
    public int PlaylistTotalDurationSeconds { get; init; }

    [JsonPropertyName("transitionSeconds")]
    public int TransitionSeconds { get; init; }

    [JsonPropertyName("transitionOutSeconds")]
    public int TransitionOutSeconds { get; init; }

    [JsonPropertyName("recurrence")]
    public string Recurrence { get; init; } = PluginConfiguration.SceneScheduleRecurrenceWeekly;

    [JsonPropertyName("recurrenceInterval")]
    public int RecurrenceInterval { get; init; } = PluginConfiguration.MinSceneScheduleRecurrenceInterval;

    [JsonPropertyName("maxRuns")]
    public int MaxRuns { get; init; }

    [JsonPropertyName("remainingRuns")]
    public int? RemainingRuns { get; init; }

    [JsonPropertyName("dayOfMonth")]
    public int DayOfMonth { get; init; }

    [JsonPropertyName("monthOfYear")]
    public int MonthOfYear { get; init; }

    [JsonPropertyName("weekOfMonth")]
    public int WeekOfMonth { get; init; }

    [JsonPropertyName("dayOfWeek")]
    public int DayOfWeek { get; init; } = -1;

    [JsonPropertyName("targetLabel")]
    public string TargetLabel { get; init; } = string.Empty;

    [JsonPropertyName("targetAllEnabledMappings")]
    public bool TargetAllEnabledMappings { get; init; }

    [JsonPropertyName("targetUserIds")]
    public IReadOnlyList<string> TargetUserIds { get; init; } = Array.Empty<string>();

    [JsonPropertyName("includeDefaultTarget")]
    public bool IncludeDefaultTarget { get; init; }

    [JsonPropertyName("lastTargetResults")]
    public IReadOnlyList<HueSceneScheduleTargetResult> LastTargetResults { get; init; } = Array.Empty<HueSceneScheduleTargetResult>();

    [JsonPropertyName("timeOfDay")]
    public string TimeOfDay { get; init; } = string.Empty;

    [JsonPropertyName("timeMode")]
    public string TimeMode { get; init; } = PluginConfiguration.SceneScheduleTimeModeFixed;

    [JsonPropertyName("solarOffsetMinutes")]
    public int SolarOffsetMinutes { get; init; }

    [JsonPropertyName("solarLatitude")]
    public double? SolarLatitude { get; init; }

    [JsonPropertyName("solarLongitude")]
    public double? SolarLongitude { get; init; }

    [JsonPropertyName("timeZoneId")]
    public string TimeZoneId { get; init; } = string.Empty;

    [JsonPropertyName("timeZoneDisplayName")]
    public string TimeZoneDisplayName { get; init; } = string.Empty;

    [JsonPropertyName("runDate")]
    public string RunDate { get; init; } = string.Empty;

    [JsonPropertyName("startDate")]
    public string StartDate { get; init; } = string.Empty;

    [JsonPropertyName("endDate")]
    public string EndDate { get; init; } = string.Empty;

    [JsonPropertyName("excludedDates")]
    public IReadOnlyList<string> ExcludedDates { get; init; } = Array.Empty<string>();

    [JsonPropertyName("daysOfWeekMask")]
    public int DaysOfWeekMask { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("skipNextOccurrence")]
    public bool SkipNextOccurrence { get; init; }

    [JsonPropertyName("ready")]
    public bool Ready { get; init; }

    [JsonPropertyName("readinessMessage")]
    public string ReadinessMessage { get; init; } = string.Empty;

    [JsonPropertyName("nextRunLocal")]
    public DateTime? NextRunLocal { get; init; }

    [JsonPropertyName("nextRunUtc")]
    public DateTime? NextRunUtc { get; init; }

    [JsonPropertyName("lastRunAtUtc")]
    public DateTime? LastRunAtUtc { get; init; }

    [JsonPropertyName("lastSucceeded")]
    public bool? LastSucceeded { get; init; }

    [JsonPropertyName("lastSkipped")]
    public bool LastSkipped { get; init; }

    [JsonPropertyName("lastWasCatchUp")]
    public bool LastWasCatchUp { get; init; }

    [JsonPropertyName("lastWasDeferred")]
    public bool LastWasDeferred { get; init; }

    [JsonPropertyName("lastWasDeferredRestored")]
    public bool LastWasDeferredRestored { get; init; }

    [JsonPropertyName("deferredPending")]
    public bool DeferredPending { get; init; }

    [JsonPropertyName("deferredRestored")]
    public bool DeferredRestored { get; init; }

    [JsonPropertyName("deferredOccurrenceSlot")]
    public DateTime? DeferredOccurrenceSlot { get; init; }

    [JsonPropertyName("deferredUntilLocal")]
    public DateTime? DeferredUntilLocal { get; init; }

    [JsonPropertyName("lastMessage")]
    public string? LastMessage { get; init; }

    [JsonPropertyName("lastCleanupWarning")]
    public string? LastCleanupWarning { get; init; }

    [JsonPropertyName("runCount")]
    public int RunCount { get; init; }

    [JsonPropertyName("isRunning")]
    public bool IsRunning { get; init; }
}

/// <summary>
/// Sanitized administrator-facing status for the scheduled scene automation service.
/// </summary>
public sealed class HueSceneAutomationStatus
{
    [JsonPropertyName("serviceAvailable")]
    public bool ServiceAvailable { get; init; }

    [JsonPropertyName("automationEnabled")]
    public bool AutomationEnabled { get; init; } = true;

    [JsonPropertyName("catchUpMinutes")]
    public int CatchUpMinutes { get; init; }

    [JsonPropertyName("playbackPolicy")]
    public string PlaybackPolicy { get; init; } = PluginConfiguration.SceneAutomationPlaybackPolicySkip;

    [JsonPropertyName("playbackConflictScope")]
    public string PlaybackConflictScope { get; init; } = PluginConfiguration.SceneAutomationPlaybackScopeAnyTarget;

    [JsonPropertyName("deferMinutes")]
    public int DeferMinutes { get; init; } = PluginConfiguration.DefaultSceneAutomationDeferMinutes;

    [JsonPropertyName("playbackActive")]
    public bool PlaybackActive { get; init; }

    [JsonPropertyName("generatedAtUtc")]
    public DateTime GeneratedAtUtc { get; init; }

    [JsonPropertyName("serverLocalNow")]
    public DateTime ServerLocalNow { get; init; }

    [JsonPropertyName("serverTimeZoneId")]
    public string ServerTimeZoneId { get; init; } = string.Empty;

    [JsonPropertyName("schedules")]
    public IReadOnlyList<HueSceneScheduleRuntimeStatus> Schedules { get; init; } = Array.Empty<HueSceneScheduleRuntimeStatus>();

    [JsonPropertyName("conflicts")]
    public IReadOnlyList<HueSceneScheduleConflict> Conflicts { get; init; } = Array.Empty<HueSceneScheduleConflict>();
}
