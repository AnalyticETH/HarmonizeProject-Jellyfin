using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
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
    private const int MaxPendingCleanupRecoveriesPerPass = 4;
    private readonly IHueStreamTester _streamTester;
    private readonly HueClient _hueClient;
    private readonly ILogger<HueSceneAutomationService> _logger;
    private readonly HueBridgeLifecycleGate _bridgeLifecycleGate;
    private readonly HueScheduledCleanupJournal? _scheduledCleanupJournal;
    private readonly Func<string, CancellationToken, Task<IPAddress>> _bridgeAddressResolver;
    private readonly object _runSlotLock = new();
    private readonly Dictionary<string, DateTime> _lastRunSlots = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _deferredRunLock = new();
    private readonly Dictionary<string, HueSceneDeferredRun> _deferredRuns = new(StringComparer.OrdinalIgnoreCase);
    private bool _deferredRunsLoaded;
    private readonly object _manualRunCancellationLock = new();
    private readonly Dictionary<string, CancellationTokenSource> _manualRunCancellations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TaskCompletionSource<bool>> _manualRunCompletions = new(StringComparer.OrdinalIgnoreCase);
    private bool _isStopping;
    private readonly object _runtimeStateLock = new();
    private readonly Dictionary<string, HueSceneScheduleRuntimeState> _runtimeStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _oneTimeCompletionPersistencePending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _oneTimeCancellationPersistencePending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _automaticOccurrenceClaimLock = new();
    private readonly Dictionary<string, HueSceneAutomationOccurrenceClaim> _automaticOccurrenceClaims = new(StringComparer.OrdinalIgnoreCase);
    private bool _automaticOccurrenceClaimsLoaded;
    private bool _automaticOccurrenceClaimsPersistencePending;
    private int _schedulerEvaluationCount;
    private int _scheduleLifecycleCount;
    private readonly object _historyLock = new();
    private readonly List<HueSceneAutomationRunResult> _runHistory = new();
    private bool _historyLoaded;
    private bool _historyPersistencePending;
    private bool _deferredRunsPersistencePending;

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
        : this(streamTester, hueClient, logger, bridgeLifecycleGate, scheduledCleanupJournal: null)
    {
    }

    public HueSceneAutomationService(
        IHueStreamTester streamTester,
        HueClient hueClient,
        ILogger<HueSceneAutomationService> logger,
        HueBridgeLifecycleGate? bridgeLifecycleGate,
        HueScheduledCleanupJournal? scheduledCleanupJournal,
        Func<string, CancellationToken, Task<IPAddress>>? bridgeAddressResolver = null)
    {
        _streamTester = streamTester ?? throw new ArgumentNullException(nameof(streamTester));
        _hueClient = hueClient ?? throw new ArgumentNullException(nameof(hueClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _bridgeLifecycleGate = bridgeLifecycleGate ?? new HueBridgeLifecycleGate();
        _scheduledCleanupJournal = scheduledCleanupJournal;
        _bridgeAddressResolver = bridgeAddressResolver ?? HueBridgeCertificateValidation.ResolveLocalBridgeAddressAsync;
    }

    private static string GetAmbiguousSceneScheduleMessage(IEnumerable<string> scheduleIds)
        => $"The requested scene schedule ID(s) are ambiguous because duplicate persisted cues match: {string.Join(", ", scheduleIds)}.";

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
    /// Reports whether any scheduled cue currently owns an active run. The check is
    /// synchronized with the runtime state used by scheduled cue mutations and imports.
    /// </summary>
    public bool HasActiveScheduleRuns
    {
        get
        {
            lock (_runtimeStateLock)
            {
                return _runtimeStates.Values.Any(state => state.ActiveRuns > 0);
            }
        }
    }

    /// <summary>
    /// Reports whether an automatic scheduler pass has captured configuration and is
    /// still evaluating, running, or finalizing scheduled cues. Configuration mutation
    /// uses the same runtime-state lock and must not interleave with this window.
    /// </summary>
    internal bool HasActiveScheduleEvaluation
    {
        get
        {
            lock (_runtimeStateLock)
            {
                return _schedulerEvaluationCount > 0;
            }
        }
    }

    /// <summary>
    /// Reports whether a scheduled-cue lifecycle is still mutating runtime state or
    /// persisting its final result. This remains active through post-run persistence.
    /// </summary>
    internal bool HasActiveScheduleLifecycle
    {
        get
        {
            lock (_runtimeStateLock)
            {
                return _scheduleLifecycleCount > 0;
            }
        }
    }

    /// <summary>
    /// Reports whether a completed one-time cue still needs its disabled state persisted.
    /// This is intentionally in-memory repair state; the completed cue remains disabled
    /// while the next scheduler pass retries the configuration write.
    /// </summary>
    internal bool HasPendingOneTimeCompletionPersistence
    {
        get
        {
            lock (_runtimeStateLock)
            {
                return _oneTimeCompletionPersistencePending.Count > 0;
            }
        }
    }

    /// <summary>
    /// Reports whether a canceled one-time cue still needs its restored enabled state
    /// persisted. The cue remains eligible in memory while the next scheduler pass retries
    /// this repair; a restart still honors the durable pre-run claim until that write wins.
    /// </summary>
    internal bool HasPendingOneTimeCancellationPersistence
    {
        get
        {
            lock (_runtimeStateLock)
            {
                return _oneTimeCancellationPersistencePending.Count > 0;
            }
        }
    }

    /// <summary>
    /// Reserves the runtime-state lock for a configuration import. The returned lease
    /// prevents a cue from starting until the caller finishes its configuration transaction.
    /// </summary>
    public bool TryAcquireConfigurationMutation(
        out IDisposable? lease,
        out string message)
        => TryAcquireConfigurationMutation(
            allowActivePlayback: false,
            out lease,
            out message);

    /// <summary>
    /// Reserves the runtime-state lock for a configuration mutation. A policy-disable
    /// import may overlap active playback so the caller can persist the disable and
    /// then stop the affected playback session through its normal cleanup path.
    /// </summary>
    public bool TryAcquireConfigurationMutation(
        bool allowActivePlayback,
        out IDisposable? lease,
        out string message)
    {
        lease = null;
        message = string.Empty;

        // Reserve the process-wide bridge lifecycle gate before checking playback or
        // diagnostics. The lease prevents a new bridge lifecycle from starting after
        // the point-in-time check and remains held through the configuration transaction.
        var bridgeLifecycleLease = _bridgeLifecycleGate.TryEnterConfigurationMutation(allowActivePlayback);
        if (bridgeLifecycleLease == null)
        {
            message = _bridgeLifecycleGate.IsSchedulerEvaluationActive && HasActiveScheduleRuns
                ? "Configuration import cannot proceed while a scheduled scene cue is running."
                : _bridgeLifecycleGate.IsSchedulerEvaluationActive
                    ? "Configuration import cannot proceed while scheduled scene evaluation is in progress."
                : "Configuration import cannot proceed while Hue playback or an administrator diagnostic is active.";
            return false;
        }

        var runtimeStateLockHeld = false;
        try
        {
            Monitor.Enter(_runtimeStateLock);
            runtimeStateLockHeld = true;
            var activeScheduleRun = _runtimeStates.Values.Any(state => state.ActiveRuns > 0);
            var activeScheduleEvaluation = _schedulerEvaluationCount > 0;
            var activeScheduleLifecycle = _scheduleLifecycleCount > 0;
            if (activeScheduleEvaluation || activeScheduleLifecycle || activeScheduleRun)
            {
                Monitor.Exit(_runtimeStateLock);
                runtimeStateLockHeld = false;
                bridgeLifecycleLease.Dispose();
                message = activeScheduleRun
                    ? "Configuration import cannot proceed while a scheduled scene cue is running."
                    : activeScheduleEvaluation
                        ? "Configuration import cannot proceed while scheduled scene evaluation is in progress."
                        : "Configuration import cannot proceed while a scheduled scene lifecycle is in progress.";
                return false;
            }

            lease = new ConfigurationMutationLease(this, bridgeLifecycleLease);
            return true;
        }
        catch
        {
            if (runtimeStateLockHeld)
                Monitor.Exit(_runtimeStateLock);
            bridgeLifecycleLease.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Attempts to reserve the same configuration barrier for a read that may lazily
    /// repair persisted scheduler state. A contended read remains non-mutating and lets
    /// the scheduler or the completed configuration transaction perform the repair later.
    /// </summary>
    private IDisposable? TryAcquireReadRepairLease()
    {
        return TryAcquireConfigurationMutation(out var lease, out _)
            ? lease
            : null;
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
        if (!PluginConfiguration.TryResolveUniqueSceneSchedules(
                configuredSchedules,
                keys,
                out var schedules,
                out var missingKeys,
                out var ambiguousKeys))
        {
            message = ambiguousKeys.Length > 0
                ? GetAmbiguousSceneScheduleMessage(ambiguousKeys)
                : $"The requested scene schedule(s) were not found: {string.Join(", ", missingKeys)}.";
            return false;
        }
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
        if (!PluginConfiguration.TryResolveUniqueSceneSchedule(
                config?.SceneSchedules,
                key,
                out var schedule,
                out var ambiguous))
        {
            message = ambiguous
                ? GetAmbiguousSceneScheduleMessage(new[] { key })
                : "The requested scene schedule was not found.";
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
        if (!PluginConfiguration.TryResolveUniqueSceneSchedules(
                configuredSchedules,
                keys,
                out var schedules,
                out var missingKeys,
                out var ambiguousKeys))
        {
            message = ambiguousKeys.Length > 0
                ? GetAmbiguousSceneScheduleMessage(ambiguousKeys)
                : $"The requested scene schedule(s) were not found: {string.Join(", ", missingKeys)}.";
            return false;
        }
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
    /// partially updated bulk operation behind. A still-live deferred occurrence counts
    /// as eligible even when a one-time cue has no future preview. Manual Run Now remains
    /// unaffected.
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
        if (!PluginConfiguration.TryResolveUniqueSceneSchedules(
                configuredSchedules,
                keys,
                out var schedules,
                out var missingKeys,
                out var ambiguousKeys))
        {
            message = ambiguousKeys.Length > 0
                ? GetAmbiguousSceneScheduleMessage(ambiguousKeys)
                : $"The requested scene schedule(s) were not found: {string.Join(", ", missingKeys)}.";
            return false;
        }
        var previousSkip = schedules.ToDictionary(
            schedule => schedule.Id?.Trim() ?? string.Empty,
            schedule => schedule.SkipNextOccurrence,
            StringComparer.OrdinalIgnoreCase);

        // A deferred occurrence is durable scheduler state, so load it before validating
        // a skip request. One-time cues have no future preview after their occurrence has
        // entered the defer window, but the administrator must still be able to cancel it.
        EnsureDeferredRunsLoaded(persistRepairs: false);

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

                if (GetNextRunUtc(schedule, DateTime.Now) == null &&
                    !HasUnexpiredDeferredRun(schedule, DateTime.Now))
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
    /// Replaces an existing scene cue as one configuration transaction. The runtime-state
    /// lock is held through the active-run check and persistence so a cue cannot start
    /// between the check and the replacement.
    /// </summary>
    public bool TryReplaceScheduleConfiguration(
        string scheduleId,
        IReadOnlyList<HueSceneSchedule> candidateSchedules,
        out bool blockedByActiveRun,
        out string message)
    {
        blockedByActiveRun = false;
        message = string.Empty;

        var plugin = Plugin.Instance;
        var config = plugin?.Configuration;
        var key = scheduleId?.Trim() ?? string.Empty;
        if (plugin == null || config == null)
        {
            message = "Plugin configuration is not available.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            message = "The scene schedule ID is required.";
            return false;
        }

        if (!PluginConfiguration.TryResolveUniqueSceneSchedule(
                config.SceneSchedules,
                key,
                out _,
                out var ambiguous))
        {
            message = ambiguous
                ? GetAmbiguousSceneScheduleMessage(new[] { key })
                : "The requested scene schedule was not found.";
            return false;
        }

        lock (_runtimeStateLock)
        {
            if (_runtimeStates.TryGetValue(key, out var state) && state.ActiveRuns > 0)
            {
                blockedByActiveRun = true;
                message = "The scene schedule cannot be changed while it is running.";
                return false;
            }

            var previousSchedules = config.SceneSchedules;
            config.SceneSchedules = candidateSchedules.ToList();
            try
            {
                plugin.SaveConfiguration();
                return true;
            }
            catch (Exception ex)
            {
                config.SceneSchedules = previousSchedules;
                _logger.LogWarning(ex, "Could not persist replacement of Hue scene schedule {0}", key);
                message = "The scene schedule could not be saved.";
                return false;
            }
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
        if (!PluginConfiguration.TryResolveUniqueSceneSchedules(
                previousSchedules,
                keys,
                out var schedules,
                out var missingKeys,
                out var ambiguousKeys))
        {
            message = ambiguousKeys.Length > 0
                ? GetAmbiguousSceneScheduleMessage(ambiguousKeys)
                : $"The requested scene schedule(s) were not found: {string.Join(", ", missingKeys)}.";
            return false;
        }
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
    /// state with no upcoming event to consume. A still-live deferred occurrence is also
    /// eligible, including a one-time cue whose original occurrence has already elapsed.
    /// </summary>
    public bool TrySetScheduleSkipNextOccurrence(string scheduleId, bool skip, out string message)
    {
        message = string.Empty;
        var config = Plugin.Instance?.Configuration;
        var key = scheduleId?.Trim() ?? string.Empty;
        if (!PluginConfiguration.TryResolveUniqueSceneSchedule(
                config?.SceneSchedules,
                key,
                out var schedule,
                out var ambiguous))
        {
            message = ambiguous
                ? GetAmbiguousSceneScheduleMessage(new[] { key })
                : "The requested scene schedule was not found.";
            return false;
        }

        var previousSkip = schedule.SkipNextOccurrence;
        // A deferred occurrence is durable scheduler state, so load it before validating
        // a skip request. One-time cues have no future preview after their occurrence has
        // entered the defer window, but the administrator must still be able to cancel it.
        EnsureDeferredRunsLoaded(persistRepairs: false);
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

                if (GetNextRunUtc(schedule, DateTime.Now) == null &&
                    !HasUnexpiredDeferredRun(schedule, DateTime.Now))
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
        string? outcome = null,
        bool persistRepairs = true)
    {
        var readRepairLease = persistRepairs ? TryAcquireReadRepairLease() : null;
        try
        {
            EnsureHistoryLoaded(persistRepairs && readRepairLease != null);
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
        finally
        {
            readRepairLease?.Dispose();
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
        lock (_bridgeLifecycleGate.HistorySynchronization)
        {
            EnsureHistoryLoaded();
            // Deferred occurrences are durable scheduler state, not history. Ensure the
            // authoritative persisted/runtime set has been loaded before resetting the
            // presentation fields below so a pending cue remains visible after this call.
            EnsureDeferredRunsLoaded();
            var config = Plugin.Instance?.Configuration;

            lock (_runtimeStateLock)
            {
                foreach (var entry in _runtimeStates)
                {
                    var configuredSchedule = PluginConfiguration.TryResolveUniqueSceneSchedule(
                        config?.SceneSchedules,
                        entry.Key,
                        out var resolvedSchedule,
                        out _)
                        ? resolvedSchedule
                        : null;
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
                    // A pending deferred occurrence is owned by _deferredRuns and must
                    // survive a history-only clear. Keep its occurrence/expiry metadata
                    // and waiting message visible; completed cues still clear their
                    // transient deferred fields along with the rest of their telemetry.
                    if (!state.DeferredPending)
                    {
                        state.DeferredOccurrenceSlot = null;
                        state.DeferredAtLocal = null;
                        state.DeferredUntilLocal = null;
                        state.DeferredRestored = false;
                        state.LastMessage = null;
                    }
                    state.LastCleanupWarning = null;
                }
            }

            int clearedCount;
            lock (_historyLock)
            {
                clearedCount = _runHistory.Count;
                _runHistory.Clear();
            }

            PersistSceneScheduleHistory();
            return clearedCount;
        }
    }

    /// <summary>
    /// Reconciles persisted cue history with the current administrator setting. This is
    /// called after configuration changes so enabling retention captures the current
    /// in-memory window immediately and disabling retention removes stored entries.
    /// </summary>
    public void RefreshSceneScheduleHistoryPersistence()
    {
        lock (_bridgeLifecycleGate.HistorySynchronization)
        {
            EnsureHistoryLoaded();
            lock (_historyLock)
            {
                TrimRunHistoryLocked();
            }

            PersistSceneScheduleHistory();
        }
    }

    /// <summary>
    /// Returns sanitized runtime telemetry for the configured cues. Credentials and
    /// bridge connection details never enter this snapshot; target labels are derived
    /// from the current mapping names only.
    /// </summary>
    public HueSceneAutomationStatus GetStatus(bool persistRepairs = true)
    {
        var readRepairLease = persistRepairs ? TryAcquireReadRepairLease() : null;
        try
        {
            return GetStatusSnapshot(persistRepairs && readRepairLease != null);
        }
        finally
        {
            readRepairLease?.Dispose();
        }
    }

    private HueSceneAutomationStatus GetStatusSnapshot(bool persistRepairs)
    {
        EnsureHistoryLoaded(persistRepairs);
        EnsureDeferredRunsLoaded(persistRepairs);
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

        PruneDeferredRuns(schedules, config, persistRepairs);

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
                BrightnessPercent = isPlaylist || preset == null
                    ? null
                    : Math.Clamp(
                        schedule.BrightnessPercent ?? preset.BrightnessPercent,
                        PluginConfiguration.MinScenePlaylistStepBrightnessPercent,
                        PluginConfiguration.MaxScenePlaylistStepBrightnessPercent),
                Red = isPlaylist || preset == null
                    ? 0
                    : Math.Clamp(schedule.Red ?? preset.Red, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue),
                Green = isPlaylist || preset == null
                    ? 0
                    : Math.Clamp(schedule.Green ?? preset.Green, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue),
                Blue = isPlaylist || preset == null
                    ? 0
                    : Math.Clamp(schedule.Blue ?? preset.Blue, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue),
                PlaylistStepCount = playlist?.PresetNames?.Count ?? 0,
                PlaylistRepeatCount = playlist == null
                    ? PluginConfiguration.DefaultScenePlaylistRepeatCount
                    : Math.Clamp(
                        playlist.RepeatCount,
                        PluginConfiguration.MinScenePlaylistRepeatCount,
                        PluginConfiguration.MaxScenePlaylistRepeatCount),
                PlaylistPlaybackOrder = playlist == null || !PluginConfiguration.TryNormalizeScenePlaylistOrder(
                    playlist.PlaybackOrder,
                    out var runtimePlaybackOrder)
                    ? PluginConfiguration.ScenePlaylistOrderSequential
                    : runtimePlaybackOrder,
                PlaylistTotalDurationSeconds = isPlaylist
                    ? GetPlaylistTotalDurationSeconds(config, playlist)
                    : 0,
                DurationSeconds = isPlaylist
                    ? GetPlaylistTotalDurationSeconds(config, playlist)
                    : GetEffectiveDurationSeconds(schedule, preset),
                TransitionSeconds = isPlaylist ? 0 : GetEffectiveTransitionSeconds(schedule, preset),
                TransitionOutSeconds = isPlaylist ? 0 : GetEffectiveTransitionOutSeconds(schedule, preset),
                TransitionCurve = isPlaylist ? PluginConfiguration.ColorPresetTransitionCurveLinear : GetEffectiveTransitionCurve(preset),
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
                    .Select(PluginConfiguration.NormalizeJellyfinUserId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                    ?? Array.Empty<string>(),
                TargetRoutes = GetScheduleTargetRoutes(schedule),
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
                TimeZoneIanaId = PluginConfiguration.TryGetPortableSceneScheduleTimeZoneId(
                    schedule.TimeZoneId,
                    out var portableTimeZoneId)
                    ? portableTimeZoneId
                    : string.Empty,
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

        var pendingCleanupStatuses = _scheduledCleanupJournal?.Snapshot()
            .OrderBy(entry => entry.NextAttemptAtUtc ?? DateTime.MinValue)
            .ThenBy(entry => entry.CapturedAtUtc)
            .Select(entry => new HueSceneAutomationPendingCleanupStatus
            {
                CleanupId = entry.CleanupId,
                ScheduleId = entry.ScheduleId,
                TargetUserId = entry.TargetUserId,
                TargetDeviceId = entry.TargetDeviceId,
                CapturedAtUtc = entry.CapturedAtUtc,
                AttemptCount = entry.AttemptCount,
                LastAttemptAtUtc = entry.LastAttemptAtUtc,
                NextAttemptAtUtc = entry.NextAttemptAtUtc,
                LastError = entry.LastError
            })
            .ToArray() ?? Array.Empty<HueSceneAutomationPendingCleanupStatus>();

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
            PendingCleanupCount = pendingCleanupStatuses.Length,
            PendingCleanups = pendingCleanupStatuses,
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
            HueSyncService.GetPlaybackResourceKey(config, target.BridgeIp, target.EntertainmentAreaId)));
    }

    internal static int GetEffectiveTransitionSeconds(HueSceneSchedule schedule, HueColorPreset? preset)
    {
        return GetEffectiveTransitionSeconds(schedule, preset, null);
    }

    internal static int GetEffectiveTransitionSeconds(
        HueSceneSchedule schedule,
        HueColorPreset? preset,
        int? transitionOverrideSeconds)
    {
        var transition = transitionOverrideSeconds ?? preset?.TransitionSeconds ?? PluginConfiguration.MinColorPresetTransitionSeconds;
        return Math.Clamp(
            transition,
            PluginConfiguration.MinColorPresetTransitionSeconds,
            Math.Min(GetEffectiveDurationSeconds(schedule, preset), PluginConfiguration.MaxColorPresetTransitionSeconds));
    }

    internal static int GetEffectiveTransitionOutSeconds(HueSceneSchedule schedule, HueColorPreset? preset)
    {
        return GetEffectiveTransitionOutSeconds(schedule, preset, null, null);
    }

    internal static int GetEffectiveTransitionOutSeconds(
        HueSceneSchedule schedule,
        HueColorPreset? preset,
        int? transitionOutOverrideSeconds,
        int? transitionOverrideSeconds)
    {
        var duration = GetEffectiveDurationSeconds(schedule, preset);
        var transitionIn = GetEffectiveTransitionSeconds(schedule, preset, transitionOverrideSeconds);
        var transitionOut = transitionOutOverrideSeconds ?? preset?.TransitionOutSeconds ?? PluginConfiguration.MinColorPresetTransitionOutSeconds;
        return Math.Clamp(
            transitionOut,
            PluginConfiguration.MinColorPresetTransitionOutSeconds,
            Math.Min(
                duration - transitionIn,
                PluginConfiguration.MaxColorPresetTransitionOutSeconds));
    }

    internal static string GetEffectiveTransitionCurve(HueColorPreset? preset)
    {
        return PluginConfiguration.TryNormalizeColorPresetTransitionCurve(
            preset?.TransitionCurve,
            out var normalizedCurve)
            ? normalizedCurve
            : PluginConfiguration.ColorPresetTransitionCurveLinear;
    }

    /// <summary>
    /// Returns the total hold time represented by a saved-scene playlist, including each
    /// configured repeat pass. Each step inherits its scene duration unless the playlist
    /// provides a bounded per-step override; the total is used for schedule status,
    /// occurrence, and calendar metadata.
    /// </summary>
    internal static int GetPlaylistTotalDurationSeconds(
        PluginConfiguration? config,
        HueScenePlaylist? playlist)
    {
        if (playlist == null)
            return PluginConfiguration.MinPreviewDurationSeconds;

        var total = (playlist.PresetNames ?? new List<string>())
            .Select((name, index) => new
            {
                Index = index,
                Preset = config?.ColorPresets?.FirstOrDefault(preset =>
                    preset != null &&
                    string.Equals(preset.Name?.Trim(), name?.Trim(), StringComparison.OrdinalIgnoreCase))
            })
            .Select(item => item.Preset == null
                ? PluginConfiguration.MinPreviewDurationSeconds
                : PluginConfiguration.GetEffectiveScenePlaylistStepDurationSeconds(
                    playlist,
                    item.Index,
                    item.Preset))
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
    /// Expands a saved playlist into the exact credential-free step plan that a future
    /// scheduled run will use. Shuffle ordering is seeded from the occurrence date and
    /// repeat pass, matching preview and scheduler execution; offsets are cumulative hold
    /// seconds from the cue start and do not include bridge cleanup time.
    /// </summary>
    internal static IReadOnlyList<HueScenePlaylistScheduleStep> BuildPlaylistScheduleSteps(
        PluginConfiguration? config,
        HueScenePlaylist? playlist,
        DateTime runAtUtc)
    {
        if (config == null || playlist == null)
            return Array.Empty<HueScenePlaylistScheduleStep>();

        var presets = (playlist.PresetNames ?? new List<string>())
            .Select(name => config.ColorPresets?.FirstOrDefault(candidate =>
                candidate != null &&
                string.Equals(candidate.Name?.Trim(), name?.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (presets.Length == 0 || presets.Any(preset => preset == null))
            return Array.Empty<HueScenePlaylistScheduleStep>();

        return BuildPlaylistScheduleSteps(
            presets.Select(preset => preset!).ToArray(),
            playlist,
            runAtUtc);
    }

    internal static IReadOnlyList<HueScenePlaylistScheduleStep> BuildPlaylistScheduleSteps(
        IReadOnlyList<HueColorPreset>? presets,
        HueScenePlaylist? playlist,
        DateTime runAtUtc)
    {
        if (playlist == null || presets == null || presets.Count == 0 || presets.Any(preset => preset == null))
            return Array.Empty<HueScenePlaylistScheduleStep>();

        PluginConfiguration.TryNormalizeScenePlaylistOrder(
            playlist.PlaybackOrder,
            out var playbackOrder);
        var repeatCount = Math.Clamp(
            playlist.RepeatCount,
            PluginConfiguration.MinScenePlaylistRepeatCount,
            PluginConfiguration.MaxScenePlaylistRepeatCount);
        var steps = new List<HueScenePlaylistScheduleStep>(presets.Count * repeatCount);
        var startOffsetSeconds = 0;
        var normalizedRunAtUtc = DateTime.SpecifyKind(runAtUtc, DateTimeKind.Utc);
        for (var repeatIndex = 1; repeatIndex <= repeatCount; repeatIndex++)
        {
            var pass = BuildPlaylistPass(
                presets,
                playbackOrder,
                playlist.Id,
                repeatIndex,
                normalizedRunAtUtc);
            foreach (var (preset, originalIndex) in pass)
            {
                var durationSeconds = PluginConfiguration.GetEffectiveScenePlaylistStepDurationSeconds(
                    playlist,
                    originalIndex - 1,
                    preset);
                var transitionSecondsOverride = PluginConfiguration.GetEffectiveScenePlaylistStepTransitionSeconds(
                    playlist,
                    originalIndex - 1,
                    preset);
                var transitionOutSecondsOverride = PluginConfiguration.GetEffectiveScenePlaylistStepTransitionOutSeconds(
                    playlist,
                    originalIndex - 1,
                    preset);
                var effectSpeedPercent = PluginConfiguration.GetEffectiveScenePlaylistStepEffectSpeedPercent(
                    playlist,
                    originalIndex - 1,
                    preset);
                var effect = PluginConfiguration.GetEffectiveScenePlaylistStepEffect(
                    playlist,
                    originalIndex - 1,
                    preset);
                var red = PluginConfiguration.GetEffectiveScenePlaylistStepRed(
                    playlist,
                    originalIndex - 1,
                    preset);
                var green = PluginConfiguration.GetEffectiveScenePlaylistStepGreen(
                    playlist,
                    originalIndex - 1,
                    preset);
                var blue = PluginConfiguration.GetEffectiveScenePlaylistStepBlue(
                    playlist,
                    originalIndex - 1,
                    preset);
                var stepSchedule = new HueSceneSchedule { DurationSeconds = durationSeconds };
                steps.Add(new HueScenePlaylistScheduleStep
                {
                    Index = steps.Count + 1,
                    RepeatIndex = repeatIndex,
                    OriginalIndex = originalIndex,
                    PresetName = preset.Name?.Trim() ?? string.Empty,
                    Effect = effect,
                    EffectSpeedPercent = effectSpeedPercent,
                    TransitionCurve = PluginConfiguration.GetEffectiveScenePlaylistStepTransitionCurve(
                        playlist,
                        originalIndex - 1,
                        preset),
                    Red = red,
                    Green = green,
                    Blue = blue,
                    BrightnessPercent = PluginConfiguration.GetEffectiveScenePlaylistStepBrightnessPercent(
                        playlist,
                        originalIndex - 1,
                        preset),
                    DurationSeconds = durationSeconds,
                    TransitionSeconds = GetEffectiveTransitionSeconds(stepSchedule, preset, transitionSecondsOverride),
                    TransitionOutSeconds = GetEffectiveTransitionOutSeconds(
                        stepSchedule,
                        preset,
                        transitionOutSecondsOverride,
                        transitionSecondsOverride),
                    StartOffsetSeconds = startOffsetSeconds
                });
                startOffsetSeconds += durationSeconds;
            }
        }

        return steps;
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
        int durationSeconds = -1,
        string transitionCurve = PluginConfiguration.ColorPresetTransitionCurveLinear,
        int? brightnessOverride = null,
        int? redOverride = null,
        int? greenOverride = null,
        int? blueOverride = null,
        int? horizonLimitDays = null)
    {
        var occurrences = new List<HueSceneScheduleOccurrence>();
        var boundedOccurrences = Math.Clamp(
            maxOccurrences,
            1,
            MaxUpcomingOccurrencesPerSchedule);
        var boundedHorizon = Math.Clamp(
            horizonDays,
            1,
            Math.Max(1, horizonLimitDays ?? MaxUpcomingHorizonDays));

        if (schedule == null || !schedule.Enabled ||
            IsRunLimitReached(schedule) ||
            !PluginConfiguration.TryNormalizeSceneScheduleTimeMode(schedule.TimeMode, out var normalizedTimeMode) ||
            !PluginConfiguration.TryResolveSceneScheduleTimeZone(schedule.TimeZoneId, out var timeZone) ||
            (schedule.DurationSeconds != 0 &&
             (schedule.DurationSeconds < PluginConfiguration.MinPreviewDurationSeconds ||
              schedule.DurationSeconds > PluginConfiguration.MaxPreviewDurationSeconds)) ||
            !TryGetScheduleLocalNow(schedule, serverLocalNow, out var scheduleNow, out var serverUtcNow) ||
            !TryGetScheduleDateBounds(schedule, out var startDate, out var endDate) ||
            !PluginConfiguration.TryNormalizeSceneScheduleExcludedDates(schedule.ExcludedDates, out var normalizedExcludedDates) ||
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
        var solarBaseLookbackDays = PluginConfiguration.IsSceneScheduleSolarTimeMode(normalizedTimeMode)
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

        var excludedDateSet = new HashSet<string>(normalizedExcludedDates, StringComparer.Ordinal);
        var skipNextOccurrence = schedule.SkipNextOccurrence;
        for (var dayOffset = 0; dayOffset < boundedHorizon + solarBaseLookbackDays; dayOffset++)
        {
            if (runDate.HasValue && dayOffset > 0)
                break;

            // Legacy configurations can contain a boundary date such as
            // 9999-12-31. Do not let the look-ahead step overflow DateTime when a
            // pending skip marker asks for the following occurrence.
            if (dayOffset > 0 && firstCandidateDate > DateTime.MaxValue.AddDays(-dayOffset))
                break;

            var candidateDate = firstCandidateDate.AddDays(dayOffset);
            if (endDate.HasValue && candidateDate > endDate.Value)
                break;
            if (!IsScheduleDateAllowed(
                    candidateDate,
                    runDate,
                    startDate,
                    endDate,
                    excludedDateSet))
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
                    .Select(PluginConfiguration.NormalizeJellyfinUserId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                    ?? Array.Empty<string>(),
                TargetRoutes = GetScheduleTargetRoutes(schedule),
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
                BrightnessPercent = brightnessOverride.HasValue
                    ? Math.Clamp(
                        brightnessOverride.Value,
                        PluginConfiguration.MinScenePlaylistStepBrightnessPercent,
                        PluginConfiguration.MaxScenePlaylistStepBrightnessPercent)
                    : 0,
                Red = redOverride.HasValue
                    ? Math.Clamp(redOverride.Value, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue)
                    : 0,
                Green = greenOverride.HasValue
                    ? Math.Clamp(greenOverride.Value, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue)
                    : 0,
                Blue = blueOverride.HasValue
                    ? Math.Clamp(blueOverride.Value, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue)
                    : 0,
                TransitionCurve = PluginConfiguration.TryNormalizeColorPresetTransitionCurve(
                    transitionCurve,
                    out var normalizedTransitionCurve)
                    ? normalizedTransitionCurve
                    : PluginConfiguration.ColorPresetTransitionCurveLinear,
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
                TimeZoneIanaId = PluginConfiguration.TryGetPortableSceneScheduleTimeZoneId(
                    schedule.TimeZoneId,
                    out var portableTimeZoneId)
                    ? portableTimeZoneId
                    : string.Empty,
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
                         durationSeconds,
                         isPlaylist ? PluginConfiguration.ColorPresetTransitionCurveLinear : GetEffectiveTransitionCurve(preset)))
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
        // Capture the scheduler side of the configuration barrier before resolving
        // the schedule. A configuration mutation must not commit after this lookup
        // and before the tracked run owns its lifecycle state.
        using var schedulerEvaluation = BeginSchedulerEvaluation();
        if (schedulerEvaluation == null)
        {
            return Failure(
                scheduleId,
                "The scheduled scene could not start while configuration is changing.");
        }

        var config = Plugin.Instance?.Configuration;
        if (!PluginConfiguration.TryResolveUniqueSceneSchedule(
                config?.SceneSchedules,
                scheduleId,
                out var schedule,
                out var ambiguous))
        {
            return Failure(
                scheduleId,
                ambiguous
                    ? GetAmbiguousSceneScheduleMessage(new[] { scheduleId?.Trim() ?? string.Empty })
                    : "The requested scene schedule was not found.");
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
        var runCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_manualRunCancellationLock)
        {
            if (_isStopping)
            {
                return Failure(
                    scheduleId,
                    "The scene automation service is stopping.",
                    schedule);
            }

            if (_manualRunCancellations.ContainsKey(key))
            {
                return Failure(
                    scheduleId,
                    "The requested scene schedule is already running.",
                    schedule);
            }

            _manualRunCancellations[key] = runCancellation;
            _manualRunCompletions[key] = runCompletion;
        }

        try
        {
            try
            {
                var result = await RunScheduleTrackedAsync(
                    config!,
                    schedule,
                    runCancellation.Token,
                    schedulerBarrierHeld: true).ConfigureAwait(false);
                if (result.Succeeded)
                    DisableCompletedOneTimeSchedule(config!, schedule);

                return result;
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
                    runCompletion.TrySetResult(true);
                    _manualRunCancellations.Remove(key);
                    if (_manualRunCompletions.TryGetValue(key, out var completion) &&
                        ReferenceEquals(completion, runCompletion))
                    {
                        _manualRunCompletions.Remove(key);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Runs an unsaved administrator preview against the resolved target collection.
    /// This keeps immediate all-target previews on the same sequential, restorative
    /// lifecycle as scheduled cues without creating a schedule or retaining run history.
    /// </summary>
    public Task<HueSceneAutomationRunResult> RunPreviewAsync(
        HueSceneSchedule schedule,
        HueColorPreset preset,
        CancellationToken cancellationToken = default,
        bool targetScopedPlayback = false,
        int? brightnessPercentOverride = null,
        int? transitionSecondsOverride = null,
        int? transitionOutSecondsOverride = null,
        string? transitionCurveOverride = null,
        int? effectSpeedPercentOverride = null,
        int? redOverride = null,
        int? greenOverride = null,
        int? blueOverride = null)
        => RunPreviewWithEffectAsync(
            schedule,
            preset,
            cancellationToken,
            targetScopedPlayback,
            brightnessPercentOverride,
            transitionSecondsOverride,
            transitionOutSecondsOverride,
            transitionCurveOverride,
            effectSpeedPercentOverride,
            redOverride,
            greenOverride,
            blueOverride,
            effectOverride: null,
            targetRoutesOverride: null,
            resolvedTargetsOverride: null);

    /// <summary>
    /// Runs an administrator preview against credential-free explicit user/device routes.
    /// The route identifiers are resolved against the current server configuration; no
    /// bridge credentials are accepted or persisted by this API.
    /// </summary>
    public Task<HueSceneAutomationRunResult> RunPreviewAsync(
        HueSceneSchedule schedule,
        HueColorPreset preset,
        IReadOnlyList<HueSceneAutomationTargetRoute>? targetRoutesOverride,
        CancellationToken cancellationToken = default,
        bool targetScopedPlayback = false,
        int? brightnessPercentOverride = null,
        int? transitionSecondsOverride = null,
        int? transitionOutSecondsOverride = null,
        string? transitionCurveOverride = null,
        int? effectSpeedPercentOverride = null,
        int? redOverride = null,
        int? greenOverride = null,
        int? blueOverride = null)
        => RunPreviewWithEffectAsync(
            schedule,
            preset,
            cancellationToken,
            targetScopedPlayback,
            brightnessPercentOverride,
            transitionSecondsOverride,
            transitionOutSecondsOverride,
            transitionCurveOverride,
            effectSpeedPercentOverride,
            redOverride,
            greenOverride,
            blueOverride,
            effectOverride: null,
            targetRoutesOverride: targetRoutesOverride,
            resolvedTargetsOverride: null);

    /// <summary>
    /// Runs an administrator preview using a detached target snapshot captured while
    /// the configuration read lease was held. The resolved target descriptions contain
    /// only the immutable values needed for bridge I/O, so configuration writers may
    /// proceed while the preview is running without redirecting later operations.
    /// </summary>
    internal Task<HueSceneAutomationRunResult> RunPreviewWithSnapshotAsync(
        HueSceneSchedule schedule,
        HueColorPreset preset,
        IReadOnlyList<HueSceneAutomationTargetDescription> resolvedTargets,
        IReadOnlyList<HueSceneAutomationTargetRoute>? targetRoutesOverride,
        CancellationToken cancellationToken = default)
        => RunPreviewWithEffectAsync(
            schedule,
            preset,
            cancellationToken,
            targetScopedPlayback: false,
            brightnessPercentOverride: null,
            transitionSecondsOverride: null,
            transitionOutSecondsOverride: null,
            transitionCurveOverride: null,
            effectSpeedPercentOverride: null,
            redOverride: null,
            greenOverride: null,
            blueOverride: null,
            effectOverride: null,
            targetRoutesOverride: targetRoutesOverride,
            resolvedTargetsOverride: resolvedTargets);

    private async Task<HueSceneAutomationRunResult> RunPreviewWithEffectAsync(
        HueSceneSchedule schedule,
        HueColorPreset preset,
        CancellationToken cancellationToken,
        bool targetScopedPlayback,
        int? brightnessPercentOverride,
        int? transitionSecondsOverride,
        int? transitionOutSecondsOverride,
        string? transitionCurveOverride,
        int? effectSpeedPercentOverride,
        int? redOverride,
        int? greenOverride,
        int? blueOverride,
        string? effectOverride,
        IReadOnlyList<HueSceneAutomationTargetRoute>? targetRoutesOverride,
        IReadOnlyList<HueSceneAutomationTargetDescription>? resolvedTargetsOverride,
        bool durableCleanup = false)
    {
        var normalizedTargetRoutesOverride = NormalizeTargetRoutes(targetRoutesOverride);
        var config = Plugin.Instance?.Configuration;
        if (config == null && resolvedTargetsOverride == null)
            return Failure(
                schedule?.Id,
                "Scene automation configuration is unavailable.",
                schedule,
                targetRoutes: normalizedTargetRoutesOverride);

        if (schedule == null || preset == null)
            return Failure(
                schedule?.Id,
                "The preview configuration is unavailable.",
                schedule,
                targetRoutes: normalizedTargetRoutesOverride);

        IReadOnlyList<HueSceneAutomationTargetDescription> targets;
        if (resolvedTargetsOverride != null)
        {
            targets = resolvedTargetsOverride
                .Where(target => target != null)
                .ToArray();
            if (targets.Count == 0)
            {
                return Failure(
                    schedule.Id,
                    "The preview target snapshot is unavailable.",
                    schedule,
                    targetRoutes: normalizedTargetRoutesOverride);
            }
        }
        else if (!TryResolveTargets(config!, schedule, out targets, out var targetError, normalizedTargetRoutesOverride))
        {
            return Failure(schedule.Id, targetError, schedule, targetRoutes: normalizedTargetRoutesOverride);
        }

        var effect = !string.IsNullOrWhiteSpace(effectOverride) &&
            PluginConfiguration.TryNormalizeColorPresetEffect(effectOverride, out var normalizedEffectOverride)
            ? normalizedEffectOverride
            : PluginConfiguration.TryNormalizeColorPresetEffect(preset.Effect, out var normalizedPresetEffect)
                ? normalizedPresetEffect
                : PluginConfiguration.ColorPresetEffectSolid;
        var targetResults = new List<HueSceneScheduleTargetResult>();
        foreach (var target in targets)
        {
            var targetResult = await RunScheduleTargetAsync(
                schedule,
                preset,
                target,
                cancellationToken,
                targetScopedPlayback,
                brightnessPercentOverride,
                transitionSecondsOverride,
                transitionOutSecondsOverride,
                transitionCurveOverride,
                effectSpeedPercentOverride,
                effect,
                redOverride,
                greenOverride,
                blueOverride,
                durableCleanup: durableCleanup).ConfigureAwait(false);
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
            EffectSpeedPercent = effectSpeedPercentOverride.HasValue
                ? Math.Clamp(
                    effectSpeedPercentOverride.Value,
                    PluginConfiguration.MinScenePlaylistStepEffectSpeedPercent,
                    PluginConfiguration.MaxScenePlaylistStepEffectSpeedPercent)
                : PluginConfiguration.ClampColorPresetEffectSpeedPercent(preset.EffectSpeedPercent),
            BrightnessPercent = brightnessPercentOverride.HasValue
                ? Math.Clamp(
                    brightnessPercentOverride.Value,
                    PluginConfiguration.MinScenePlaylistStepBrightnessPercent,
                    PluginConfiguration.MaxScenePlaylistStepBrightnessPercent)
                : Math.Clamp(
                    preset.BrightnessPercent,
                    PluginConfiguration.MinScenePlaylistStepBrightnessPercent,
                    PluginConfiguration.MaxScenePlaylistStepBrightnessPercent),
            Red = redOverride.HasValue
                ? Math.Clamp(redOverride.Value, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue)
                : Math.Clamp(preset.Red, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue),
            Green = greenOverride.HasValue
                ? Math.Clamp(greenOverride.Value, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue)
                : Math.Clamp(preset.Green, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue),
            Blue = blueOverride.HasValue
                ? Math.Clamp(blueOverride.Value, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue)
                : Math.Clamp(preset.Blue, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue),
            TransitionCurve = !string.IsNullOrWhiteSpace(transitionCurveOverride) &&
                PluginConfiguration.TryNormalizeColorPresetTransitionCurve(
                    transitionCurveOverride,
                    out var normalizedTransitionCurve)
                ? normalizedTransitionCurve
                : GetEffectiveTransitionCurve(preset),
            TargetLabel = ResolveTargetLabel(config, schedule, targetRoutesOverride, targets),
            TargetAllEnabledMappings = schedule.TargetAllEnabledMappings,
            TargetUserIds = schedule.TargetUserIds?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(PluginConfiguration.NormalizeJellyfinUserId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                ?? Array.Empty<string>(),
            TargetRoutes = normalizedTargetRoutesOverride.Count > 0
                ? normalizedTargetRoutesOverride
                : GetScheduleTargetRoutes(schedule),
            IncludeDefaultTarget = schedule.IncludeDefaultTarget,
            Succeeded = targetResults.Count > 0 && succeededCount == targetResults.Count,
            Message = BuildAggregateRunMessage(targetResults, succeededCount),
            CleanupWarning = string.IsNullOrWhiteSpace(cleanupWarning) ? null : cleanupWarning,
            TargetResults = targetResults,
            BlockedByPlayback = targetResults.Count > 0 &&
                succeededCount == 0 &&
                targetResults.All(result => result.BlockedByPlayback),
            RunAtUtc = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Runs a saved-scene playlist through the same restorative preview lifecycle as an
    /// individual scene. All playlist references and targets are preflighted before the
    /// first bridge call; each repeated pass remains independently observable and
    /// cancellable. Shuffle order is stable for a playlist/date/pass so retries do not
    /// silently produce a different sequence within the same day.
    /// </summary>
    public Task<HueScenePlaylistRunResult> RunPlaylistPreviewAsync(
        HueScenePlaylist playlist,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? targetUserIdsOverride = null,
        bool includeDefaultTargetOverride = false,
        bool targetScopedPlayback = false,
        DateTime? runAtUtcOverride = null,
        IReadOnlyList<HueSceneAutomationTargetRoute>? targetRoutesOverride = null)
        => RunPlaylistPreviewCoreAsync(
            playlist,
            cancellationToken,
            targetUserIdsOverride,
            includeDefaultTargetOverride,
            targetScopedPlayback,
            runAtUtcOverride,
            targetRoutesOverride,
            resolvedTargetsOverride: null,
            resolvedPresetsOverride: null,
            durableCleanup: false,
            scheduleId: null);

    /// <summary>
    /// Runs a scheduled playlist with a durable cleanup journal scope. This overload is
    /// intentionally internal so the long-standing public preview signature remains
    /// binary compatible for existing plugin consumers.
    /// </summary>
    internal Task<HueScenePlaylistRunResult> RunPlaylistPreviewDurableAsync(
        HueScenePlaylist playlist,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? targetUserIdsOverride,
        bool includeDefaultTargetOverride,
        bool targetScopedPlayback,
        DateTime? runAtUtcOverride,
        IReadOnlyList<HueSceneAutomationTargetRoute>? targetRoutesOverride,
        bool durableCleanup,
        string? scheduleId)
        => RunPlaylistPreviewCoreAsync(
            playlist,
            cancellationToken,
            targetUserIdsOverride,
            includeDefaultTargetOverride,
            targetScopedPlayback,
            runAtUtcOverride,
            targetRoutesOverride,
            resolvedTargetsOverride: null,
            resolvedPresetsOverride: null,
            durableCleanup: durableCleanup,
            scheduleId: scheduleId);

    /// <summary>
    /// Runs a saved-scene playlist from detached target and preset snapshots captured
    /// while the configuration read lease was held. The snapshots keep a long-running
    /// preview stable after configuration writers are allowed to continue.
    /// </summary>
    internal Task<HueScenePlaylistRunResult> RunPlaylistPreviewWithSnapshotAsync(
        HueScenePlaylist playlist,
        IReadOnlyList<HueColorPreset> resolvedPresets,
        IReadOnlyList<HueSceneAutomationTargetDescription> resolvedTargets,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? targetUserIdsOverride = null,
        bool includeDefaultTargetOverride = false,
        bool targetScopedPlayback = false,
        DateTime? runAtUtcOverride = null,
        IReadOnlyList<HueSceneAutomationTargetRoute>? targetRoutesOverride = null)
        => RunPlaylistPreviewCoreAsync(
            playlist,
            cancellationToken,
            targetUserIdsOverride,
            includeDefaultTargetOverride,
            targetScopedPlayback,
            runAtUtcOverride,
            targetRoutesOverride,
            resolvedTargets,
            resolvedPresets,
            durableCleanup: false,
            scheduleId: null);

    private async Task<HueScenePlaylistRunResult> RunPlaylistPreviewCoreAsync(
        HueScenePlaylist playlist,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? targetUserIdsOverride = null,
        bool includeDefaultTargetOverride = false,
        bool targetScopedPlayback = false,
        DateTime? runAtUtcOverride = null,
        IReadOnlyList<HueSceneAutomationTargetRoute>? targetRoutesOverride = null,
        IReadOnlyList<HueSceneAutomationTargetDescription>? resolvedTargetsOverride = null,
        IReadOnlyList<HueColorPreset>? resolvedPresetsOverride = null,
        bool durableCleanup = false,
        string? scheduleId = null)
    {
        var normalizedTargetRoutesOverride = NormalizeTargetRoutes(targetRoutesOverride);
        var savedTargetRoutes = GetPlaylistTargetRoutes(playlist);
        var normalizedTargetUserIdsOverride = targetUserIdsOverride?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(PluginConfiguration.NormalizeJellyfinUserId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var hasTargetOverride = includeDefaultTargetOverride ||
            (normalizedTargetUserIdsOverride?.Count > 0) ||
            normalizedTargetRoutesOverride.Count > 0;
        var effectiveTargetRoutes = hasTargetOverride
            ? normalizedTargetRoutesOverride
            : savedTargetRoutes;
        var config = Plugin.Instance?.Configuration;
        if (config == null && (resolvedTargetsOverride == null || resolvedPresetsOverride == null))
            return PlaylistFailure(playlist, "Scene playlist configuration is unavailable.", effectiveTargetRoutes);

        if (playlist == null)
            return PlaylistFailure(null, "The scene playlist is unavailable.", effectiveTargetRoutes);

        IReadOnlyList<HueColorPreset> resolvedPresets;
        if (resolvedPresetsOverride != null)
        {
            var detachedValidationErrors = ValidateDetachedPlaylist(playlist, resolvedPresetsOverride);
            if (detachedValidationErrors.Count > 0)
            {
                return PlaylistFailure(
                    playlist,
                    string.Join(" ", detachedValidationErrors),
                    effectiveTargetRoutes);
            }

            resolvedPresets = resolvedPresetsOverride.ToArray();
        }
        else
        {
            var validationErrors = PluginConfiguration.ValidateScenePlaylist(playlist, config!);
            if (validationErrors.Count > 0)
                return PlaylistFailure(playlist, string.Join(" ", validationErrors), effectiveTargetRoutes);

            var presets = (playlist.PresetNames ?? new List<string>())
                .Select(name => config!.ColorPresets?.FirstOrDefault(candidate =>
                    candidate != null &&
                    string.Equals(candidate.Name?.Trim(), name?.Trim(), StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (presets.Any(preset => preset == null))
                return PlaylistFailure(
                    playlist,
                    "The scene playlist references a saved scene that no longer exists.",
                    effectiveTargetRoutes);
            resolvedPresets = presets.Select(preset => preset!).ToArray();
        }

        var effectiveTargetUserIds = hasTargetOverride
            ? normalizedTargetUserIdsOverride ?? new List<string>()
            : (playlist.TargetUserIds ?? new List<string>())
                .Select(PluginConfiguration.NormalizeJellyfinUserId)
                .ToList();
        var effectiveIncludeDefaultTarget = hasTargetOverride
            ? includeDefaultTargetOverride
            : playlist.IncludeDefaultTarget;
        var targetSchedule = new HueSceneSchedule
        {
            Id = scheduleId?.Trim() ?? "scene-playlist-preview",
            Name = playlist.Name?.Trim() ?? string.Empty,
            PresetName = resolvedPresets[0].Name?.Trim() ?? string.Empty,
            TargetUserId = hasTargetOverride || effectiveIncludeDefaultTarget || effectiveTargetUserIds.Count > 0 || effectiveTargetRoutes.Count > 0
                ? string.Empty
                : playlist.TargetAllEnabledMappings ? string.Empty : PluginConfiguration.NormalizeJellyfinUserId(playlist.TargetUserId),
            TargetUserIds = effectiveTargetUserIds,
            TargetRoutes = effectiveTargetRoutes.Select(route => new HueSceneScheduleTargetRoute
            {
                UserId = route.UserId,
                DeviceId = route.DeviceId ?? string.Empty
            }).ToList(),
            IncludeDefaultTarget = effectiveIncludeDefaultTarget,
            TargetAllEnabledMappings = !hasTargetOverride && !effectiveIncludeDefaultTarget &&
                effectiveTargetUserIds.Count == 0 &&
                effectiveTargetRoutes.Count == 0 &&
                playlist.TargetAllEnabledMappings
        };
        IReadOnlyList<HueSceneAutomationTargetDescription> resolvedTargets;
        if (resolvedTargetsOverride != null)
        {
            resolvedTargets = resolvedTargetsOverride
                .Where(target => target != null)
                .ToArray();
            if (resolvedTargets.Count == 0)
            {
                return PlaylistFailure(
                    playlist,
                    "The playlist target snapshot is unavailable.",
                    effectiveTargetRoutes);
            }
        }
        else if (!TryResolveTargets(
                     config!,
                     targetSchedule,
                     out resolvedTargets,
                     out var targetError,
                     effectiveTargetRoutes))
        {
            return PlaylistFailure(playlist, targetError, effectiveTargetRoutes);
        }

        var repeatCount = Math.Clamp(
            playlist.RepeatCount,
            PluginConfiguration.MinScenePlaylistRepeatCount,
            PluginConfiguration.MaxScenePlaylistRepeatCount);
        PluginConfiguration.TryNormalizeScenePlaylistOrder(
            playlist.PlaybackOrder,
            out var playbackOrder);
        var executionStartedAtUtc = DateTime.UtcNow;
        var runAtUtc = NormalizeRunAtUtc(runAtUtcOverride) ?? executionStartedAtUtc;
        var plannedSteps = BuildPlaylistScheduleSteps(resolvedPresets, playlist, runAtUtc);
        var totalStepCount = plannedSteps.Count;
        if (_streamTester is IHuePlaylistStreamTester playlistStreamTester)
        {
            return await RunContinuousPlaylistPreviewAsync(
                config,
                playlist,
                targetSchedule,
                effectiveTargetRoutes,
                resolvedTargets,
                resolvedPresets,
                plannedSteps,
                playlistStreamTester,
                repeatCount,
                playbackOrder,
                executionStartedAtUtc,
                targetScopedPlayback,
                cancellationToken,
                durableCleanup).ConfigureAwait(false);
        }

        var steps = new List<HueScenePlaylistStepResult>();
        foreach (var plannedStep in plannedSteps)
        {
            var preset = resolvedPresets[plannedStep.OriginalIndex - 1];
            var schedule = new HueSceneSchedule
            {
                Id = string.IsNullOrWhiteSpace(scheduleId)
                    ? $"scene-playlist-preview-{plannedStep.RepeatIndex}-{plannedStep.Index}"
                    : scheduleId.Trim(),
                Name = playlist.Name?.Trim() ?? string.Empty,
                PresetName = plannedStep.PresetName,
                DurationSeconds = plannedStep.DurationSeconds,
                TargetUserId = targetSchedule.TargetUserId,
                TargetUserIds = targetSchedule.TargetUserIds.ToList(),
                TargetRoutes = targetSchedule.TargetRoutes?.Select(route => new HueSceneScheduleTargetRoute
                {
                    UserId = route.UserId,
                    DeviceId = route.DeviceId
                }).ToList() ?? new List<HueSceneScheduleTargetRoute>(),
                IncludeDefaultTarget = targetSchedule.IncludeDefaultTarget,
                TargetAllEnabledMappings = targetSchedule.TargetAllEnabledMappings
            };
            var run = await RunPreviewWithEffectAsync(
                schedule,
                preset,
                cancellationToken,
                targetScopedPlayback,
                plannedStep.BrightnessPercent,
                plannedStep.TransitionSeconds,
                plannedStep.TransitionOutSeconds,
                plannedStep.TransitionCurve,
                plannedStep.EffectSpeedPercent,
                plannedStep.Red,
                plannedStep.Green,
                plannedStep.Blue,
                plannedStep.Effect,
                effectiveTargetRoutes,
                resolvedTargetsOverride: resolvedTargets,
                durableCleanup: durableCleanup).ConfigureAwait(false);
            steps.Add(new HueScenePlaylistStepResult
            {
                Index = plannedStep.Index,
                RepeatIndex = plannedStep.RepeatIndex,
                OriginalIndex = plannedStep.OriginalIndex,
                PresetName = plannedStep.PresetName,
                Effect = plannedStep.Effect,
                EffectSpeedPercent = plannedStep.EffectSpeedPercent,
                TransitionCurve = plannedStep.TransitionCurve,
                Red = plannedStep.Red,
                Green = plannedStep.Green,
                Blue = plannedStep.Blue,
                BrightnessPercent = plannedStep.BrightnessPercent,
                DurationSeconds = plannedStep.DurationSeconds,
                TransitionSeconds = plannedStep.TransitionSeconds,
                TransitionOutSeconds = plannedStep.TransitionOutSeconds,
                StartOffsetSeconds = plannedStep.StartOffsetSeconds,
                Succeeded = run.Succeeded,
                Message = run.Message,
                CleanupWarning = run.CleanupWarning,
                TargetResults = run.TargetResults
            });
            if (!run.Succeeded &&
                (IndicatesCancellation(run.Message) ||
                 (cancellationToken.IsCancellationRequested &&
                  IndicatesCancellationAfterTokenRequest(run.Message))))
                break;
        }

        var succeededCount = steps.Count(step => step.Succeeded);
        var cleanupWarning = string.Join(
            " ",
            steps
                .Where(step => !string.IsNullOrWhiteSpace(step.CleanupWarning))
                .Select(step => $"{step.PresetName}: {step.CleanupWarning!.Trim()}"));
        var targetResults = Enumerable.Range(0, resolvedTargets.Count)
            .Select(targetIndex =>
            {
                var entries = steps
                    .Where(step => step.TargetResults.Count > targetIndex)
                    .Select(step => step.TargetResults[targetIndex])
                    .ToArray();
                var failed = entries.Where(entry => !entry.Succeeded).ToArray();
                return new HueScenePlaylistTargetResult
                {
                    TargetLabel = resolvedTargets[targetIndex].TargetLabel,
                    Succeeded = entries.Length == totalStepCount && entries.All(entry => entry.Succeeded),
                    CompletedStepCount = entries.Length,
                    TotalStepCount = totalStepCount,
                    Message = failed.Length == 0
                        ? $"Completed {entries.Length} of {totalStepCount} playlist step(s)."
                        : string.Join(" ", failed.Select(entry => entry.Message).Where(message => !string.IsNullOrWhiteSpace(message))),
                    CleanupWarning = string.Join(" ", entries
                        .Where(entry => !string.IsNullOrWhiteSpace(entry.CleanupWarning))
                        .Select(entry => entry.CleanupWarning!.Trim())),
                    BlockedByPlayback = entries.Length > 0 &&
                        entries.All(entry => entry.BlockedByPlayback),
                    AvailableChannelCount = entries.Select(entry => entry.AvailableChannelCount).DefaultIfEmpty().Max(),
                    SelectedChannelCount = entries.Select(entry => entry.SelectedChannelCount).DefaultIfEmpty().Max()
                };
            })
            .ToArray();
        var message = steps.Count == totalStepCount && succeededCount == totalStepCount
            ? repeatCount == 1
                ? $"Played playlist '{playlist.Name?.Trim()}' with {resolvedPresets.Count} saved scene(s)."
                : $"Played playlist '{playlist.Name?.Trim()}' with {resolvedPresets.Count} saved scene(s) for {repeatCount} passes."
            : steps.Count == 0
                ? "The scene playlist did not contain any runnable steps."
                : $"Played {succeededCount} of {totalStepCount} playlist step(s).";
        return new HueScenePlaylistRunResult
        {
            PlaylistId = playlist.Id?.Trim() ?? string.Empty,
            PlaylistName = playlist.Name?.Trim() ?? string.Empty,
            RepeatCount = repeatCount,
            PlaybackOrder = playbackOrder,
            TargetLabel = ResolveTargetLabel(config, targetSchedule, effectiveTargetRoutes, resolvedTargets),
            TargetAllEnabledMappings = targetSchedule.TargetAllEnabledMappings,
            TargetUserIds = targetSchedule.TargetUserIds?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(PluginConfiguration.NormalizeJellyfinUserId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                ?? Array.Empty<string>(),
            TargetRoutes = effectiveTargetRoutes,
            IncludeDefaultTarget = targetSchedule.IncludeDefaultTarget,
            Succeeded = steps.Count == totalStepCount && succeededCount == totalStepCount,
            Message = message,
            CleanupWarning = string.IsNullOrWhiteSpace(cleanupWarning) ? null : cleanupWarning,
            Steps = steps,
            TargetResults = targetResults,
            BlockedByPlayback = targetResults.Length > 0 &&
                !targetResults.Any(result => result.Succeeded) &&
                targetResults.All(result => result.BlockedByPlayback),
            RunAtUtc = executionStartedAtUtc
        };
    }

    private async Task<HueScenePlaylistRunResult> RunContinuousPlaylistPreviewAsync(
        PluginConfiguration? config,
        HueScenePlaylist playlist,
        HueSceneSchedule targetSchedule,
        IReadOnlyList<HueSceneAutomationTargetRoute> targetRoutes,
        IReadOnlyList<HueSceneAutomationTargetDescription> resolvedTargets,
        IReadOnlyList<HueColorPreset> resolvedPresets,
        IReadOnlyList<HueScenePlaylistScheduleStep> plannedSteps,
        IHuePlaylistStreamTester playlistStreamTester,
        int repeatCount,
        string playbackOrder,
        DateTime executionStartedAtUtc,
        bool targetScopedPlayback,
        CancellationToken cancellationToken,
        bool durableCleanup)
    {
        var streamSteps = plannedSteps
            .Select(step =>
            {
                return new HuePlaylistPreviewStep
                {
                    Index = step.Index,
                    Red = step.Red,
                    Green = step.Green,
                    Blue = step.Blue,
                    BrightnessPercent = step.BrightnessPercent,
                    DurationSeconds = step.DurationSeconds,
                    TransitionSeconds = step.TransitionSeconds,
                    TransitionOutSeconds = step.TransitionOutSeconds,
                    Effect = step.Effect,
                    EffectSpeedPercent = step.EffectSpeedPercent,
                    TransitionCurve = step.TransitionCurve
                };
            })
            .ToArray();
        var targetExecutions = new List<(
            HueScenePlaylistTargetResult Result,
            IReadOnlyDictionary<int, HueSceneScheduleTargetResult> StepResults)>(resolvedTargets.Count);
        var canceled = false;
        foreach (var target in resolvedTargets)
        {
            if (canceled || cancellationToken.IsCancellationRequested)
            {
                targetExecutions.Add(ContinuousPlaylistTargetFailure(
                    target,
                    plannedSteps.Count,
                    "The continuous playlist preview was canceled before this target started."));
                continue;
            }

            var execution = await RunContinuousPlaylistTargetAsync(
                playlist.Name,
                target,
                streamSteps,
                playlistStreamTester,
                targetScopedPlayback,
                cancellationToken,
                durableCleanup,
                targetSchedule.Id).ConfigureAwait(false);
            targetExecutions.Add(execution);
            canceled = !execution.Result.Succeeded &&
                (IndicatesCancellation(execution.Result.Message) ||
                 execution.StepResults.Values.Any(result => IndicatesCancellation(result.Message)));
        }

        var steps = plannedSteps
            .Select(plannedStep =>
            {
                var stepTargetResults = targetExecutions
                    .Select(execution => execution.StepResults.TryGetValue(plannedStep.Index, out var result)
                        ? result
                        : new HueSceneScheduleTargetResult
                        {
                            TargetLabel = execution.Result.TargetLabel,
                            Succeeded = false,
                            Message = execution.Result.CompletedStepCount == 0
                                ? execution.Result.Message
                                : "The continuous playlist stream ended before this step.",
                            BlockedByPlayback = execution.Result.BlockedByPlayback,
                            AvailableChannelCount = execution.Result.AvailableChannelCount,
                            SelectedChannelCount = execution.Result.SelectedChannelCount
                        })
                    .ToArray();
                var succeededTargetCount = stepTargetResults.Count(result => result.Succeeded);
                var stepCleanupWarning = string.Join(
                    " ",
                    stepTargetResults
                        .Where(result => !string.IsNullOrWhiteSpace(result.CleanupWarning))
                        .Select(result => $"{result.TargetLabel}: {result.CleanupWarning!.Trim()}"));
                return new HueScenePlaylistStepResult
                {
                    Index = plannedStep.Index,
                    RepeatIndex = plannedStep.RepeatIndex,
                    OriginalIndex = plannedStep.OriginalIndex,
                    PresetName = plannedStep.PresetName,
                    Effect = plannedStep.Effect,
                    EffectSpeedPercent = plannedStep.EffectSpeedPercent,
                    TransitionCurve = plannedStep.TransitionCurve,
                    Red = plannedStep.Red,
                    Green = plannedStep.Green,
                    Blue = plannedStep.Blue,
                    BrightnessPercent = plannedStep.BrightnessPercent,
                    DurationSeconds = plannedStep.DurationSeconds,
                    TransitionSeconds = plannedStep.TransitionSeconds,
                    TransitionOutSeconds = plannedStep.TransitionOutSeconds,
                    StartOffsetSeconds = plannedStep.StartOffsetSeconds,
                    Succeeded = stepTargetResults.Length > 0 &&
                        succeededTargetCount == stepTargetResults.Length,
                    Message = BuildAggregateRunMessage(stepTargetResults, succeededTargetCount),
                    CleanupWarning = string.IsNullOrWhiteSpace(stepCleanupWarning)
                        ? null
                        : stepCleanupWarning,
                    TargetResults = stepTargetResults
                };
            })
            .ToArray();
        var targetResults = targetExecutions.Select(execution => execution.Result).ToArray();
        var succeededStepCount = steps.Count(step => step.Succeeded);
        var succeeded = steps.Length == plannedSteps.Count &&
            succeededStepCount == steps.Length &&
            targetResults.Length == resolvedTargets.Count &&
            targetResults.All(result => result.Succeeded);
        var wasCanceled = canceled ||
            cancellationToken.IsCancellationRequested ||
            targetResults.Any(result => IndicatesCancellation(result.Message)) ||
            steps.Any(step =>
                IndicatesCancellation(step.Message) ||
                step.TargetResults.Any(result => IndicatesCancellation(result.Message)));
        var cleanupWarning = string.Join(
            " ",
            targetResults
                .Where(result => !string.IsNullOrWhiteSpace(result.CleanupWarning))
                .Select(result => $"{result.TargetLabel}: {result.CleanupWarning!.Trim()}"));
        var message = succeeded
            ? repeatCount == 1
                ? $"Played playlist '{playlist.Name?.Trim()}' with {resolvedPresets.Count} saved scene(s) in continuous streams."
                : $"Played playlist '{playlist.Name?.Trim()}' with {resolvedPresets.Count} saved scene(s) for {repeatCount} passes in continuous streams."
            : wasCanceled
                ? $"The continuous playlist preview was canceled after {succeededStepCount} of {plannedSteps.Count} playlist step(s) completed successfully across all targets."
                : steps.Length == 0
                    ? "The scene playlist did not contain any runnable steps."
                    : $"Played {succeededStepCount} of {plannedSteps.Count} playlist step(s) successfully across all targets.";
        return new HueScenePlaylistRunResult
        {
            PlaylistId = playlist.Id?.Trim() ?? string.Empty,
            PlaylistName = playlist.Name?.Trim() ?? string.Empty,
            RepeatCount = repeatCount,
            PlaybackOrder = playbackOrder,
            TargetLabel = ResolveTargetLabel(config, targetSchedule, targetRoutes, resolvedTargets),
            TargetAllEnabledMappings = targetSchedule.TargetAllEnabledMappings,
            TargetUserIds = targetSchedule.TargetUserIds?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(PluginConfiguration.NormalizeJellyfinUserId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                ?? Array.Empty<string>(),
            TargetRoutes = targetRoutes,
            IncludeDefaultTarget = targetSchedule.IncludeDefaultTarget,
            Succeeded = succeeded,
            Message = message,
            CleanupWarning = string.IsNullOrWhiteSpace(cleanupWarning) ? null : cleanupWarning,
            Steps = steps,
            TargetResults = targetResults,
            BlockedByPlayback = targetResults.Length > 0 &&
                !targetResults.Any(result => result.Succeeded) &&
                targetResults.All(result => result.BlockedByPlayback),
            RunAtUtc = executionStartedAtUtc
        };
    }

    private async Task<(
        HueScenePlaylistTargetResult Result,
        IReadOnlyDictionary<int, HueSceneScheduleTargetResult> StepResults)> RunContinuousPlaylistTargetAsync(
        string? playlistName,
        HueSceneAutomationTargetDescription target,
        IReadOnlyList<HuePlaylistPreviewStep> steps,
        IHuePlaylistStreamTester playlistStreamTester,
        bool targetScopedPlayback,
        CancellationToken cancellationToken,
        bool durableCleanup,
        string? scheduleId)
    {
        try
        {
            // Keep retry state isolated per scheduled target. The hosted service and
            // stream tester are singletons, so mutating their shared clients here would
            // let concurrent rooms overwrite one another's network policy.
            var targetHueClient = _hueClient.CreatePlaybackClient();
            targetHueClient.RetryAttempts = target.RetryAttempts;
            var areaConfiguration = await targetHueClient.GetEntertainmentConfiguration(
                target.BridgeIp,
                target.AppKey,
                target.EntertainmentAreaId,
                cancellationToken).ConfigureAwait(false);
            if (areaConfiguration == null)
            {
                return ContinuousPlaylistTargetFailure(
                    target,
                    steps.Count,
                    "The Hue bridge did not return the configured entertainment area.");
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
                    return ContinuousPlaylistTargetFailure(
                        target,
                        steps.Count,
                        $"The channel profile references IDs not present in this entertainment area: {string.Join(", ", missingChannelIds)}.",
                        availableChannelIds.Count,
                        selectedChannelCount);
                }
            }

            var targetPlaylistTester = playlistStreamTester;
            if (playlistStreamTester is IHueRetryAwareStreamTester retryAwareTester &&
                retryAwareTester.CreateForRetryAttempts(target.RetryAttempts) is IHuePlaylistStreamTester retryAwarePlaylistTester)
            {
                targetPlaylistTester = retryAwarePlaylistTester;
            }

            using var cleanupScope = durableCleanup && _scheduledCleanupJournal != null
                ? _scheduledCleanupJournal.BeginScope(new HueScheduledCleanupScope
                {
                    CleanupId = Guid.NewGuid().ToString("N"),
                    ScheduleId = scheduleId?.Trim() ?? string.Empty,
                    TargetUserId = target.TargetUserId,
                    TargetDeviceId = target.TargetDeviceId,
                    BridgeIp = target.BridgeIp,
                    EntertainmentAreaId = target.EntertainmentAreaId,
                    ChannelIds = target.ChannelIds
                })
                : null;
            var probe = targetScopedPlayback
                ? await targetPlaylistTester.PreviewPlaylistAsyncForTarget(
                    target.BridgeIp,
                    target.AppKey,
                    target.ClientKey,
                    target.EntertainmentAreaId,
                    areaConfiguration.Value,
                    target.ChannelIds,
                    steps,
                    cancellationToken).ConfigureAwait(false)
                : await targetPlaylistTester.PreviewPlaylistAsync(
                    target.BridgeIp,
                    target.AppKey,
                    target.ClientKey,
                    target.EntertainmentAreaId,
                    areaConfiguration.Value,
                    target.ChannelIds,
                    steps,
                    cancellationToken).ConfigureAwait(false);
            var plannedIndexes = steps.Select(step => step.Index).ToHashSet();
            var probeSteps = probe.Steps
                .Where(step => step != null && plannedIndexes.Contains(step.Index))
                .GroupBy(step => step.Index)
                .ToDictionary(group => group.Key, group => group.Last());
            var lastCompletedIndex = probeSteps.Keys.DefaultIfEmpty(-1).Max();
            var stepResults = probeSteps.ToDictionary(
                pair => pair.Key,
                pair => new HueSceneScheduleTargetResult
                {
                    TargetLabel = target.TargetLabel,
                    Succeeded = pair.Value.Succeeded,
                    Message = pair.Value.Message,
                    BlockedByPlayback = probe.BlockedByPlayback,
                    CleanupWarning = pair.Key == lastCompletedIndex ? probe.CleanupWarning : null,
                    AvailableChannelCount = availableChannelIds.Count,
                    SelectedChannelCount = selectedChannelCount
                });
            var targetSucceeded = probe.Succeeded &&
                stepResults.Count == steps.Count &&
                stepResults.Values.All(result => result.Succeeded);
            return (
                new HueScenePlaylistTargetResult
                {
                    TargetLabel = target.TargetLabel,
                    Succeeded = targetSucceeded,
                    CompletedStepCount = stepResults.Count,
                    TotalStepCount = steps.Count,
                    Message = probe.Message,
                    CleanupWarning = probe.CleanupWarning,
                    BlockedByPlayback = probe.BlockedByPlayback,
                    AvailableChannelCount = availableChannelIds.Count,
                    SelectedChannelCount = selectedChannelCount
                },
                stepResults);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ContinuousPlaylistTargetFailure(
                target,
                steps.Count,
                "The continuous playlist preview was canceled; the bridge is being restored.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Hue scene playlist {0} failed for target {1}",
                playlistName,
                target.TargetLabel);
            return ContinuousPlaylistTargetFailure(
                target,
                steps.Count,
                "The continuous playlist preview failed for this target.");
        }
    }

    private static (
        HueScenePlaylistTargetResult Result,
        IReadOnlyDictionary<int, HueSceneScheduleTargetResult> StepResults) ContinuousPlaylistTargetFailure(
        HueSceneAutomationTargetDescription target,
        int totalStepCount,
        string message,
        int availableChannelCount = 0,
        int selectedChannelCount = 0)
        => (
            new HueScenePlaylistTargetResult
            {
                TargetLabel = target.TargetLabel,
                Succeeded = false,
                CompletedStepCount = 0,
                TotalStepCount = totalStepCount,
                Message = message,
                BlockedByPlayback = false,
                AvailableChannelCount = availableChannelCount,
                SelectedChannelCount = selectedChannelCount
            },
            new Dictionary<int, HueSceneScheduleTargetResult>());

    private static bool IndicatesCancellation(string? message)
        => !string.IsNullOrWhiteSpace(message) &&
            (message.Contains("canceled", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("cancelled", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("cancellation", StringComparison.OrdinalIgnoreCase));

    private static bool IndicatesCancellationAfterTokenRequest(string? message)
        => IndicatesCancellation(message) ||
            (!string.IsNullOrWhiteSpace(message) &&
             (message.Contains("stream stopped", StringComparison.OrdinalIgnoreCase) ||
              message.Contains("could not be sent", StringComparison.OrdinalIgnoreCase)));

    internal static bool IndicatesCancellation(HueScenePlaylistRunResult? result)
        => result != null &&
            (IndicatesCancellation(result.Message) ||
             (result.TargetResults?.Any(target => IndicatesCancellation(target.Message)) ?? false) ||
             (result.Steps?.Any(step =>
                 IndicatesCancellation(step.Message) ||
                 (step.TargetResults?.Any(target => IndicatesCancellation(target.Message)) ?? false)) ?? false));

    private static DateTime? NormalizeRunAtUtc(DateTime? value)
    {
        if (!value.HasValue)
            return null;

        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };
    }

    /// <summary>
    /// Builds one playlist pass while retaining each scene's configured position for
    /// credential-free telemetry. Shuffle uses a stable FNV-1a-derived seed from the
    /// playlist identity, UTC date, and pass number, avoiding nondeterministic retries.
    /// </summary>
    internal static IReadOnlyList<(HueColorPreset Preset, int OriginalIndex)> BuildPlaylistPass(
        IReadOnlyList<HueColorPreset> presets,
        string? playbackOrder,
        string? playlistId,
        int repeatIndex,
        DateTime runAtUtc)
    {
        ArgumentNullException.ThrowIfNull(presets);
        var pass = presets
            .Select((preset, index) => (Preset: preset, OriginalIndex: index + 1))
            .ToList();
        if (!PluginConfiguration.TryNormalizeScenePlaylistOrder(playbackOrder, out var normalizedOrder) ||
            !string.Equals(normalizedOrder, PluginConfiguration.ScenePlaylistOrderShuffle, StringComparison.Ordinal) ||
            pass.Count < 2)
        {
            return pass;
        }

        var hash = 2166136261u;
        foreach (var character in playlistId?.Trim() ?? string.Empty)
        {
            hash ^= character;
            hash *= 16777619u;
        }

        hash ^= unchecked((uint)repeatIndex);
        hash *= 16777619u;
        hash ^= unchecked((uint)runAtUtc.Date.Ticks);
        hash *= 16777619u;
        hash ^= unchecked((uint)(runAtUtc.Date.Ticks >> 32));
        var random = new Random(unchecked((int)(hash & 0x7fffffff)));
        for (var index = pass.Count - 1; index > 0; index--)
        {
            var swapIndex = random.Next(index + 1);
            (pass[index], pass[swapIndex]) = (pass[swapIndex], pass[index]);
        }

        return pass;
    }

    private static HueScenePlaylistRunResult PlaylistFailure(
        HueScenePlaylist? playlist,
        string message,
        IReadOnlyList<HueSceneAutomationTargetRoute>? targetRoutes = null)
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
            PlaybackOrder = playlist != null && PluginConfiguration.TryNormalizeScenePlaylistOrder(
                playlist.PlaybackOrder,
                out var playbackOrder)
                ? playbackOrder
                : PluginConfiguration.ScenePlaylistOrderSequential,
            TargetAllEnabledMappings = playlist?.TargetAllEnabledMappings == true,
            TargetUserIds = playlist?.TargetUserIds?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(PluginConfiguration.NormalizeJellyfinUserId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                ?? Array.Empty<string>(),
            TargetRoutes = NormalizeTargetRoutes(targetRoutes),
            IncludeDefaultTarget = playlist?.IncludeDefaultTarget == true,
            Succeeded = false,
            Message = message,
            RunAtUtc = DateTime.UtcNow
        };
    }

    private static List<string> ValidateDetachedPlaylist(
        HueScenePlaylist playlist,
        IReadOnlyList<HueColorPreset> resolvedPresets)
    {
        var errors = PluginConfiguration.ValidateScenePlaylist(playlist, configuration: null);
        var presetNames = playlist.PresetNames ?? new List<string>();
        if (resolvedPresets == null || resolvedPresets.Count != presetNames.Count)
        {
            errors.Add("The scene playlist saved-scene snapshot does not match its saved scenes.");
            return errors;
        }

        for (var index = 0; index < presetNames.Count; index++)
        {
            var preset = resolvedPresets[index];
            var expectedName = presetNames[index]?.Trim() ?? string.Empty;
            if (preset == null ||
                !string.Equals(preset.Name?.Trim(), expectedName, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"The scene playlist saved-scene snapshot does not match scene {index + 1}.");
                continue;
            }

            errors.AddRange(PluginConfiguration.ValidateColorPreset(
                preset,
                $"Saved scene '{expectedName}'"));
        }

        if (errors.Count == 0 &&
            playlist.RepeatCount >= PluginConfiguration.MinScenePlaylistRepeatCount &&
            playlist.RepeatCount <= PluginConfiguration.MaxScenePlaylistRepeatCount)
        {
            var totalDuration = resolvedPresets
                .Select((preset, index) => PluginConfiguration.GetEffectiveScenePlaylistStepDurationSeconds(
                    playlist,
                    index,
                    preset))
                .Sum() * playlist.RepeatCount;
            if (totalDuration > PluginConfiguration.MaxScenePlaylistTotalDurationSeconds)
            {
                errors.Add(
                    $"Scene playlist repeated duration cannot exceed {PluginConfiguration.MaxScenePlaylistTotalDurationSeconds} seconds");
            }
        }

        return errors;
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

        if (Plugin.Instance?.Configuration is { } config &&
            !PluginConfiguration.TryResolveUniqueSceneSchedule(
                config.SceneSchedules,
                key,
                out _,
                out _))
        {
            return false;
        }

        CancellationTokenSource? cancellation;
        lock (_manualRunCancellationLock)
        {
            if (!_manualRunCancellations.TryGetValue(key, out cancellation) || cancellation == null)
                return false;
        }

        try
        {
            // Cancellation callbacks can perform bridge cleanup; do not invoke them
            // while holding the map lock, and let all callbacks run before reporting
            // any aggregate callback exception to the caller.
            cancellation.Cancel(throwOnFirstException: false);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hue scene schedule {0} cancellation reported an exception", key);
        }

        return true;
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

        if (PluginConfiguration.IsSceneScheduleSolarTimeMode(timeMode))
        {
            if (!PluginConfiguration.AreValidSceneScheduleSolarCoordinates(
                    schedule.SolarLatitude,
                    schedule.SolarLongitude))
            {
                return false;
            }

            var dawn = PluginConfiguration.IsSceneScheduleDawnTimeMode(timeMode);
            var offsetMinutes = Math.Clamp(
                schedule.SolarOffsetMinutes,
                PluginConfiguration.MinSceneScheduleSolarOffsetMinutes,
                PluginConfiguration.MaxSceneScheduleSolarOffsetMinutes);
            if (string.Equals(timeMode, PluginConfiguration.SceneScheduleTimeModeSolarNoon, StringComparison.Ordinal))
            {
                return HueSolarCalculator.TryGetSolarNoonLocal(
                    scheduleDate,
                    timeZone,
                    schedule.SolarLatitude!.Value,
                    schedule.SolarLongitude!.Value,
                    offsetMinutes,
                    out localTime,
                    out utcTime);
            }

            if (string.Equals(timeMode, PluginConfiguration.SceneScheduleTimeModeCivilDawn, StringComparison.Ordinal) ||
                string.Equals(timeMode, PluginConfiguration.SceneScheduleTimeModeCivilDusk, StringComparison.Ordinal))
            {
                return HueSolarCalculator.TryGetCivilTwilightLocal(
                    scheduleDate,
                    timeZone,
                    schedule.SolarLatitude!.Value,
                    schedule.SolarLongitude!.Value,
                    dawn,
                    offsetMinutes,
                    out localTime,
                    out utcTime);
            }

            if (string.Equals(timeMode, PluginConfiguration.SceneScheduleTimeModeNauticalDawn, StringComparison.Ordinal) ||
                string.Equals(timeMode, PluginConfiguration.SceneScheduleTimeModeNauticalDusk, StringComparison.Ordinal))
            {
                return HueSolarCalculator.TryGetNauticalTwilightLocal(
                    scheduleDate,
                    timeZone,
                    schedule.SolarLatitude!.Value,
                    schedule.SolarLongitude!.Value,
                    dawn,
                    offsetMinutes,
                    out localTime,
                    out utcTime);
            }

            if (string.Equals(timeMode, PluginConfiguration.SceneScheduleTimeModeAstronomicalDawn, StringComparison.Ordinal) ||
                string.Equals(timeMode, PluginConfiguration.SceneScheduleTimeModeAstronomicalDusk, StringComparison.Ordinal))
            {
                return HueSolarCalculator.TryGetAstronomicalTwilightLocal(
                    scheduleDate,
                    timeZone,
                    schedule.SolarLatitude!.Value,
                    schedule.SolarLongitude!.Value,
                    dawn,
                    offsetMinutes,
                    out localTime,
                    out utcTime);
            }

            return HueSolarCalculator.TryGetEventLocal(
                scheduleDate,
                timeZone,
                schedule.SolarLatitude!.Value,
                schedule.SolarLongitude!.Value,
                dawn,
                offsetMinutes,
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
        DateTime serverUtcNow;
        if (schedule == null || !schedule.Enabled ||
            IsRunLimitReached(schedule) ||
            !PluginConfiguration.TryNormalizeSceneScheduleTimeMode(schedule.TimeMode, out var normalizedTimeMode) ||
            !TryGetScheduleLocalNow(schedule, localNow, out var scheduleNow, out serverUtcNow) ||
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
            return false;

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
                    out var expectedUtc))
            {
                continue;
            }

            // Match the resolved instant as well as the local wall-clock minute. A
            // fall-back transition can contain the same local minute twice; the
            // occurrence calculator resolves that ambiguity deterministically (to
            // the standard-time instant), so due evaluation must use the same UTC
            // minute or the cue could run an hour early and then run again after a
            // restart when the in-memory slot guard is lost.
            if (expectedLocal.Date == scheduleNow.Date &&
                scheduleNow.Hour == expectedLocal.Hour &&
                scheduleNow.Minute == expectedLocal.Minute &&
                expectedUtc.Year == serverUtcNow.Year &&
                expectedUtc.Month == serverUtcNow.Month &&
                expectedUtc.Day == serverUtcNow.Day &&
                expectedUtc.Hour == serverUtcNow.Hour &&
                expectedUtc.Minute == serverUtcNow.Minute)
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
            // Compute the Monday/Sunday-aligned week distance arithmetically. Using
            // AddDays to move the anchor to the start of its week throws when a valid
            // year-one anchor falls on Monday through Saturday and would move before
            // DateTime.MinValue.
            var elapsedWeekDays = (candidateDate - anchorDate).Days +
                                  (int)anchorDate.DayOfWeek -
                                  (int)candidateDate.DayOfWeek;
            return (elapsedWeekDays / 7) % interval == 0;
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

        return IsScheduleDateAllowed(
            scheduleDate,
            runDate,
            startDate,
            endDate,
            new HashSet<string>(excludedDates, StringComparer.Ordinal));
    }

    private static bool IsScheduleDateAllowed(
        DateTime scheduleDate,
        DateTime? runDate,
        DateTime? startDate,
        DateTime? endDate,
        ISet<string> excludedDates)
    {
        if (runDate.HasValue && scheduleDate.Date != runDate.Value)
            return false;

        var date = scheduleDate.Date;
        if ((startDate.HasValue && date < startDate.Value) ||
            (endDate.HasValue && date > endDate.Value))
        {
            return false;
        }

        var normalizedDate = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return !excludedDates.Contains(normalizedDate);
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
        var searchHorizonDays = GetNextRunSearchHorizonDays(schedule);
        var occurrence = GetUpcomingOccurrences(
            schedule,
            serverLocalNow,
            1,
            searchHorizonDays,
            includeFutureStartBeyondHorizon: true,
            horizonLimitDays: searchHorizonDays).FirstOrDefault();
        if (occurrence == null)
            return null;

        nextRunUtc = occurrence.UtcTime;
        return occurrence.LocalTime;
    }

    private static int GetNextRunSearchHorizonDays(HueSceneSchedule? schedule)
    {
        if (schedule == null ||
            !PluginConfiguration.TryNormalizeSceneScheduleRecurrence(schedule.Recurrence, out var recurrence))
        {
            return MaxUpcomingHorizonDays;
        }

        var interval = Math.Clamp(
            schedule.RecurrenceInterval,
            PluginConfiguration.MinSceneScheduleRecurrenceInterval,
            PluginConfiguration.MaxSceneScheduleRecurrenceInterval);
        var requiredOccurrences = schedule.SkipNextOccurrence ? 2 : 1;
        var recurrenceHorizon = recurrence switch
        {
            PluginConfiguration.SceneScheduleRecurrenceDaily => (interval * requiredOccurrences) + 1,
            PluginConfiguration.SceneScheduleRecurrenceWeekly => (interval * 7 * requiredOccurrences) + 7,
            PluginConfiguration.SceneScheduleRecurrenceMonthly => (interval * 31 * requiredOccurrences) + 32,
            PluginConfiguration.SceneScheduleRecurrenceMonthlyWeekday => (interval * 31 * requiredOccurrences) + 32,
            PluginConfiguration.SceneScheduleRecurrenceYearly => (interval * 366 * requiredOccurrences) + 367,
            _ => MaxUpcomingHorizonDays
        };

        // Preserve the existing 366-day behavior for ordinary schedules and provide
        // enough room for the next anchored interval when a cue uses Every > 1. A
        // pending skip marker needs room for the following occurrence as well. The
        // public occurrence/conflict/export endpoints continue to use their own cap.
        return Math.Max(MaxUpcomingHorizonDays, recurrenceHorizon);
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

    private static int GetInProcessCatchUpMinutes(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
            return PluginConfiguration.MinSceneAutomationCatchUpMinutes;

        return Math.Clamp(
            (int)Math.Ceiling(elapsed.TotalMinutes),
            PluginConfiguration.MinSceneAutomationCatchUpMinutes,
            PluginConfiguration.MaxSceneAutomationCatchUpMinutes);
    }

    private static DateTime ConvertServerLocalNowToUtc(DateTime serverLocalNow)
    {
        try
        {
            return serverLocalNow.Kind switch
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
            // Preserve progress for a synthetic invalid local wall-clock value supplied
            // by a caller or test; the platform's normal conversion remains the fallback.
            return DateTime.SpecifyKind(serverLocalNow, DateTimeKind.Unspecified).ToUniversalTime();
        }
    }

    private static DateTime ConvertUtcToServerLocal(DateTime utcNow)
    {
        var normalizedUtc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        return DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(normalizedUtc, TimeZoneInfo.Local),
            DateTimeKind.Local);
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

        serverUtcNow = ConvertServerLocalNowToUtc(serverLocalNow);

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

        if (schedule.BrightnessPercent.HasValue &&
            (schedule.BrightnessPercent.Value < PluginConfiguration.MinScenePlaylistStepBrightnessPercent ||
             schedule.BrightnessPercent.Value > PluginConfiguration.MaxScenePlaylistStepBrightnessPercent))
        {
            return new HueSceneScheduleReadiness(false, "The cue brightness override is invalid.");
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
            if (schedule.BrightnessPercent.HasValue)
                return new HueSceneScheduleReadiness(false, "Playlist cues must inherit brightness from their saved steps.");

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
                    StepDurationSeconds = playlist.StepDurationSeconds?.ToList() ?? new List<int>(),
                    StepBrightnessPercent = playlist.StepBrightnessPercent?.ToList() ?? new List<int?>(),
                    StepRed = playlist.StepRed?.ToList() ?? new List<int?>(),
                    StepGreen = playlist.StepGreen?.ToList() ?? new List<int?>(),
                    StepBlue = playlist.StepBlue?.ToList() ?? new List<int?>(),
                    StepEffects = playlist.StepEffects?.ToList() ?? new List<string?>(),
                    StepEffectSpeedPercent = playlist.StepEffectSpeedPercent?.ToList() ?? new List<int?>(),
                    StepTransitionSeconds = playlist.StepTransitionSeconds?.ToList() ?? new List<int?>(),
                    StepTransitionOutSeconds = playlist.StepTransitionOutSeconds?.ToList() ?? new List<int?>(),
                    StepTransitionCurves = playlist.StepTransitionCurves?.ToList() ?? new List<string?>(),
                    RepeatCount = playlist.RepeatCount,
                    PlaybackOrder = playlist.PlaybackOrder
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
            (schedule.TargetUserIds?.Any(target => !string.IsNullOrWhiteSpace(target)) ?? false) ||
            (schedule.TargetRoutes?.Count > 0))
        {
            description = new HueSceneAutomationTargetDescription();
            error = "The schedule selects multiple targets; resolve the target collection instead.";
            return false;
        }

        return TryResolveSingleTarget(config, schedule, schedule.TargetUserId, null, out description, out error);
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
        out string error,
        IReadOnlyList<HueSceneAutomationTargetRoute>? targetRoutes = null)
    {
        var effectiveTargetRoutes = targetRoutes ?? GetScheduleTargetRoutes(schedule);
        return TryResolveTargetsCore(
            config,
            schedule?.TargetUserId,
            schedule?.TargetAllEnabledMappings ?? false,
            schedule?.TargetUserIds,
            schedule?.IncludeDefaultTarget ?? false,
            effectiveTargetRoutes,
            out targets,
            out error);
    }

    private static IReadOnlyList<HueSceneAutomationTargetRoute> GetScheduleTargetRoutes(HueSceneSchedule? schedule)
    {
        return schedule?.TargetRoutes?
            .Select(route => route == null
                ? new HueSceneAutomationTargetRoute()
                : new HueSceneAutomationTargetRoute
                {
                    UserId = PluginConfiguration.NormalizeJellyfinUserId(route.UserId),
                    DeviceId = string.IsNullOrWhiteSpace(route.DeviceId) ? null : route.DeviceId.Trim()
                })
            .ToArray() ?? Array.Empty<HueSceneAutomationTargetRoute>();
    }

    private static IReadOnlyList<HueSceneAutomationTargetRoute> GetPlaylistTargetRoutes(HueScenePlaylist? playlist)
    {
        return playlist?.TargetRoutes?
            .Select(route => route == null
                ? new HueSceneAutomationTargetRoute()
                : new HueSceneAutomationTargetRoute
                {
                    UserId = PluginConfiguration.NormalizeJellyfinUserId(route.UserId),
                    DeviceId = string.IsNullOrWhiteSpace(route.DeviceId) ? null : route.DeviceId.Trim()
                })
            .ToArray() ?? Array.Empty<HueSceneAutomationTargetRoute>();
    }

    private static bool TryResolveTargetsCore(
        PluginConfiguration config,
        string? targetUserIdValue,
        bool targetAllEnabledMappings,
        IReadOnlyList<string>? targetUserIds,
        bool includeDefaultTarget,
        IReadOnlyList<HueSceneAutomationTargetRoute>? targetRoutes,
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

        var targetUserId = PluginConfiguration.NormalizeJellyfinUserId(targetUserIdValue);
        if (targetRoutes?.Any(route => route == null ||
                string.IsNullOrWhiteSpace(route.UserId) ||
                string.IsNullOrWhiteSpace(route.DeviceId)) == true)
        {
            error = "Selected scene cue target routes must contain both a user mapping ID and a device ID.";
            return false;
        }

        var selectedUserIds = (targetUserIds ?? Array.Empty<string>())
            .Select(PluginConfiguration.NormalizeJellyfinUserId)
            .ToArray();
        var selectedRoutes = NormalizeTargetRoutes(targetRoutes);
        if (selectedUserIds.Length + selectedRoutes.Count > PluginConfiguration.MaxSceneScheduleTargetMappings)
        {
            error = $"Scene cue target selection cannot contain more than {PluginConfiguration.MaxSceneScheduleTargetMappings} target routes.";
            return false;
        }
        var hasSelectedTargets = includeDefaultTarget ||
            selectedUserIds.Any(value => !string.IsNullOrWhiteSpace(value)) ||
            selectedRoutes.Count > 0;

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
            if (!TryResolveSingleTarget(config, new HueSceneSchedule(), targetUserId, null, out var target, out error))
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
                if (!TryResolveSingleTarget(config, new HueSceneSchedule(), string.Empty, null, out var selectedGlobalTarget, out var selectedGlobalError))
                {
                    error = $"The default bridge target is not ready for selected targets: {selectedGlobalError}";
                    return false;
                }

                resolved.Add(selectedGlobalTarget);
                seenTargets.Add(GetTargetIdentity(config, selectedGlobalTarget));
            }

            var seenUserIds = new List<string>();
            foreach (var selectedUserId in selectedUserIds)
            {
                if (string.IsNullOrWhiteSpace(selectedUserId))
                {
                    error = "Selected scene cue targets must contain user mapping IDs.";
                    return false;
                }

                if (seenUserIds.Any(existing =>
                        PluginConfiguration.AreSameJellyfinUserId(existing, selectedUserId)))
                {
                    error = $"Selected scene cue targets contain user mapping '{selectedUserId}' more than once.";
                    return false;
                }
                seenUserIds.Add(selectedUserId);

                if (!TryResolveSingleTarget(config, new HueSceneSchedule(), selectedUserId, null, out var selectedTarget, out var selectedError))
                {
                    var matchingMappings = (config.UserMappings ?? new List<UserBridgeMapping>())
                        .Where(candidate => candidate != null &&
                            PluginConfiguration.AreSameJellyfinUserId(candidate.UserId, selectedUserId))
                        .Cast<UserBridgeMapping>()
                        .ToArray();
                    var mappingLabel = matchingMappings.Length > 1
                        ? $"ambiguous user mapping {selectedUserId}"
                        : matchingMappings.Length == 0
                        ? selectedUserId
                        : GetMappingLabel(matchingMappings[0]);
                    error = $"Target '{mappingLabel}' is not ready for selected targets: {selectedError}";
                    return false;
                }

                if (seenTargets.Add(GetTargetIdentity(config, selectedTarget)))
                    resolved.Add(selectedTarget);
            }

            var seenRoutes = new HashSet<(string UserId, string DeviceId)>(new SceneTargetRouteKeyComparer());
            foreach (var selectedRoute in selectedRoutes)
            {
                var routeKey = (selectedRoute.UserId, selectedRoute.DeviceId ?? string.Empty);
                if (!seenRoutes.Add(routeKey))
                {
                    error = $"Selected scene cue targets contain route '{selectedRoute.UserId}/{selectedRoute.DeviceId ?? "user"}' more than once.";
                    return false;
                }

                if (!TryResolveSingleTarget(
                        config,
                        new HueSceneSchedule(),
                        selectedRoute.UserId,
                        selectedRoute.DeviceId,
                        out var selectedRouteTarget,
                        out var selectedRouteError))
                {
                    error = string.IsNullOrWhiteSpace(selectedRoute.DeviceId)
                        ? $"Target '{selectedRoute.UserId}' is not ready for selected targets: {selectedRouteError}"
                        : $"Target '{selectedRoute.UserId}/{selectedRoute.DeviceId}' is not ready for selected targets: {selectedRouteError}";
                    return false;
                }

                if (seenTargets.Add(GetTargetIdentity(config, selectedRouteTarget)))
                    resolved.Add(selectedRouteTarget);
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
        var globalResolved = TryResolveSingleTarget(config, new HueSceneSchedule(), string.Empty, null, out var globalTarget, out var globalError);
        if (globalResolved)
        {
            resolved.Add(globalTarget);
            seenTargets.Add(GetTargetIdentity(config, globalTarget));
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

            if (!TryResolveSingleTarget(config, new HueSceneSchedule(), mapping.UserId, null, out var mappingTarget, out var mappingError))
            {
                error = $"Target '{GetMappingLabel(mapping)}' is not ready for broadcast: {mappingError}";
                return false;
            }

            if (seenTargets.Add(GetTargetIdentity(config, mappingTarget)))
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
        string? requestedTargetDeviceId,
        out HueSceneAutomationTargetDescription description,
        out string error,
        bool requireClientKey = true)
    {
        description = new HueSceneAutomationTargetDescription();
        error = string.Empty;
        if (config == null || schedule == null)
        {
            error = "Scene automation configuration is unavailable.";
            return false;
        }

        var targetUserId = PluginConfiguration.NormalizeJellyfinUserId(requestedTargetUserId);
        var targetDeviceId = requestedTargetDeviceId?.Trim() ?? string.Empty;
        var bridgeIp = config.HueBridgeIp?.Trim() ?? string.Empty;
        var appKey = config.HueAppKey?.Trim() ?? string.Empty;
        var clientKey = config.HueClientKey?.Trim() ?? string.Empty;
        var areaId = config.EntertainmentAreaId?.Trim() ?? string.Empty;
        var channelIds = config.ChannelIds?.Trim() ?? string.Empty;
        // Configuration validation bounds this value, but scene automation may run
        // against a legacy or hand-edited persisted file. Keep the resolved target
        // consistent with HueClient's bounded retry policy and prevent malformed
        // settings from extending a scheduled bridge operation indefinitely.
        var retryAttempts = Math.Clamp(config.NetworkRetryAttempts, 0, 10);
        string targetLabel = "Default bridge target";

        if (!string.IsNullOrWhiteSpace(targetUserId))
        {
            var matchingMappings = (config.UserMappings ?? new List<UserBridgeMapping>())
                .Where(candidate => candidate != null &&
                    PluginConfiguration.AreSameJellyfinUserId(candidate.UserId, targetUserId))
                .Cast<UserBridgeMapping>()
                .ToArray();
            if (matchingMappings.Length == 0)
            {
                error = "The selected user mapping no longer exists.";
                return false;
            }
            if (matchingMappings.Length > 1)
            {
                error = "The selected Jellyfin user has multiple mapping rows. Resolve the duplicate mappings before running scene automation.";
                return false;
            }

            var mapping = matchingMappings[0];

            if (!mapping.SyncEnabled)
            {
                error = "The selected user mapping is disabled.";
                return false;
            }

            targetLabel = string.IsNullOrWhiteSpace(mapping.UserName)
                ? $"User mapping {mapping.UserId?.Trim() ?? targetUserId}"
                : mapping.UserName.Trim();

            if (!string.IsNullOrWhiteSpace(targetDeviceId))
            {
                if (PluginConfiguration.HasAmbiguousDeviceTarget(mapping, targetDeviceId))
                {
                    error = $"The selected device route '{targetDeviceId}' is ambiguous because the user mapping contains duplicate device targets. Resolve duplicate device targets before running scene automation.";
                    return false;
                }

                var deviceTarget = mapping.DeviceTargets?.FirstOrDefault(candidate =>
                    candidate != null &&
                    string.Equals(candidate.DeviceId?.Trim(), targetDeviceId, StringComparison.Ordinal));
                if (deviceTarget == null)
                {
                    error = "The selected device route no longer exists.";
                    return false;
                }

                var deviceLabel = string.IsNullOrWhiteSpace(deviceTarget.DeviceName)
                    ? targetDeviceId
                    : deviceTarget.DeviceName.Trim();
                targetLabel += $" / {deviceLabel}";
                bridgeIp = deviceTarget.HueBridgeIp?.Trim() ?? string.Empty;
                appKey = deviceTarget.HueAppKey?.Trim() ?? string.Empty;
                clientKey = deviceTarget.HueClientKey?.Trim() ?? string.Empty;
                areaId = deviceTarget.EntertainmentAreaId?.Trim() ?? string.Empty;
                channelIds = string.IsNullOrWhiteSpace(deviceTarget.ChannelIdsOverride)
                    ? string.IsNullOrWhiteSpace(mapping.ChannelIdsOverride)
                        ? config.ChannelIds?.Trim() ?? string.Empty
                        : mapping.ChannelIdsOverride.Trim()
                    : deviceTarget.ChannelIdsOverride.Trim();
            }
            // Blank mapping targets intentionally inherit every global target field.
            else if (!string.IsNullOrWhiteSpace(mapping.HueBridgeIp))
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
                retryAttempts = Math.Clamp(mapping.NetworkRetryAttemptsOverride.Value, 0, 10);
        }

        if (!Jellyfin.Plugin.Hue.HueBridgeCertificateValidation.IsValidBridgeAddress(bridgeIp))
        {
            error = "The scene target requires a valid private bridge IP address or .local host name.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(appKey) ||
            (requireClientKey && string.IsNullOrWhiteSpace(clientKey)))
        {
            error = requireClientKey
                ? "The scene target requires both a Hue App Key and Client Key."
                : "The scene target requires a Hue App Key for cleanup recovery.";
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
            TargetUserId = targetUserId,
            TargetDeviceId = targetDeviceId,
            BridgeIp = bridgeIp,
            AppKey = appKey,
            ClientKey = clientKey,
            EntertainmentAreaId = areaId,
            ChannelIds = parsedChannelIds.Count == 0 ? null : parsedChannelIds,
            RetryAttempts = retryAttempts
        };
        return true;
    }

    private static string GetTargetIdentity(
        PluginConfiguration config,
        HueSceneAutomationTargetDescription target)
    {
        var channelProfile = target.ChannelIds == null
            ? string.Empty
            : string.Join(",", target.ChannelIds.OrderBy(channelId => channelId));
        return $"{HueSyncService.GetPlaybackResourceKey(config, target.BridgeIp, target.EntertainmentAreaId)}|" +
               channelProfile;
    }

    private static IReadOnlyList<HueSceneAutomationTargetRoute> NormalizeTargetRoutes(
        IReadOnlyList<HueSceneAutomationTargetRoute>? routes)
        => routes?
            .Select(route => route == null
                ? new HueSceneAutomationTargetRoute()
                : new HueSceneAutomationTargetRoute
                {
                    UserId = PluginConfiguration.NormalizeJellyfinUserId(route.UserId),
                    DeviceId = string.IsNullOrWhiteSpace(route.DeviceId) ? null : route.DeviceId.Trim()
                })
            .ToArray() ?? Array.Empty<HueSceneAutomationTargetRoute>();

    private sealed class SceneTargetRouteKeyComparer : IEqualityComparer<(string UserId, string DeviceId)>
    {
        public bool Equals(
            (string UserId, string DeviceId) left,
            (string UserId, string DeviceId) right)
            => PluginConfiguration.AreSameJellyfinUserId(left.UserId, right.UserId) &&
               string.Equals(left.DeviceId, right.DeviceId, StringComparison.Ordinal);

        public int GetHashCode((string UserId, string DeviceId) value)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(
                    PluginConfiguration.NormalizeJellyfinUserId(value.UserId)),
                StringComparer.Ordinal.GetHashCode(value.DeviceId));
    }

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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource[] activeCancellations;
        Task[] activeCompletions;
        lock (_manualRunCancellationLock)
        {
            _isStopping = true;
            activeCancellations = _manualRunCancellations.Values.ToArray();
            activeCompletions = _manualRunCompletions.Values
                .Select(completion => completion.Task)
                .ToArray();
        }

        foreach (var cancellation in activeCancellations)
        {
            try
            {
                // Cancellation callbacks can include bridge cleanup and must not run
                // while the ownership lock is held. Continue cancelling every source
                // even when one callback reports an exception.
                cancellation.Cancel(throwOnFirstException: false);
            }
            catch (ObjectDisposedException)
            {
                // The run completed between the snapshot and cancellation request.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hue scene manual run cancellation reported an exception during service shutdown");
            }
        }

        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Stream cleanup owns its independent bounded budget. Do not abandon an
            // active manual run merely because the host's StopAsync token was canceled.
            await Task.WhenAll(activeCompletions).ConfigureAwait(false);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Check once on startup so a service restart during a configured minute still
            // honors the cue, while the run-slot guard prevents duplicate polling triggers.
            stoppingToken.ThrowIfCancellationRequested();
            await RunDueSchedulesAsync(DateTime.Now, stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(PollInterval);
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

    private async Task RetryPendingScheduledCleanupsAsync(
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        if (_scheduledCleanupJournal == null)
            return;

        var nowUtc = DateTime.UtcNow;
        var pendingEntries = _scheduledCleanupJournal
            .Snapshot()
            .Where(entry => !entry.NextAttemptAtUtc.HasValue ||
                NormalizeUtcInstant(entry.NextAttemptAtUtc.Value) <= nowUtc)
            .OrderBy(entry => entry.NextAttemptAtUtc ?? DateTime.MinValue)
            .ThenBy(entry => entry.CapturedAtUtc)
            .ToArray();

        // Snapshot() already applies the journal's global pending-entry bound. Keep the
        // per-pass bridge-work bound, but do not let a coordination skip consume it: a
        // playback-held entry can remain due indefinitely, so scan past it until four
        // recoveries have actually acquired a diagnostic lease.
        var recoveryCount = 0;
        foreach (var entry in pendingEntries)
        {
            if (recoveryCount >= MaxPendingCleanupRecoveriesPerPass)
                break;

            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolvePendingCleanupTarget(config, entry, out var target, out var targetError))
            {
                _scheduledCleanupJournal.RecordFailure(entry.CleanupId, targetError, nowUtc);
                continue;
            }

            if (!await IsSamePendingCleanupBridgeAsync(
                    config,
                    target.BridgeIp,
                    entry.BridgeIp,
                    cancellationToken).ConfigureAwait(false) ||
                !string.Equals(
                    target.EntertainmentAreaId,
                    entry.EntertainmentAreaId?.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                _scheduledCleanupJournal.RecordFailure(
                    entry.CleanupId,
                    "The configured cleanup target changed since the snapshot was captured.",
                    nowUtc);
                continue;
            }

            if (!HueScheduledCleanupJournal.TryDeserializeStates(entry, out var savedLightStates, out var snapshotError))
            {
                _scheduledCleanupJournal.RecordFailure(entry.CleanupId, snapshotError, nowUtc);
                continue;
            }

            // Cleanup mutates the same bridge/area resource as live playback and previews.
            // Keep channel-profile identity for target de-duplication, but use the
            // canonical bridge/area key for lifecycle arbitration so a recovery cannot
            // overlap an active stream that selected a different channel profile.
            var resourceKey = HueSyncService.GetPlaybackResourceKey(
                config,
                target.BridgeIp,
                target.EntertainmentAreaId);
            using var diagnosticLease = _bridgeLifecycleGate.TryEnterDiagnostic(
                resourceKey,
                out _);
            if (diagnosticLease == null)
            {
                // Playback or another diagnostic currently owns this exact resource. Do
                // not count a coordination skip as a failed bridge attempt; the existing
                // due timestamp lets the next scheduler pass retry without extending the
                // bounded backoff window.
                continue;
            }

            recoveryCount++;

            var warnings = new List<string>();
            var targetHueClient = _hueClient.CreatePlaybackClient();
            targetHueClient.RetryAttempts = target.RetryAttempts;
            using var cleanupCancellation = HueCleanupBudget.CreateCancellationSource();
            var cleanupToken = cleanupCancellation.Token;
            try
            {
                if (!await targetHueClient.StopEntertainmentAreaWithResult(
                        target.BridgeIp,
                        target.AppKey,
                        target.EntertainmentAreaId,
                        cleanupToken).ConfigureAwait(false))
                {
                    warnings.Add("The entertainment area could not be deactivated within the cleanup deadline.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not deactivate Hue entertainment area while recovering scheduled cleanup for {0}",
                    entry.CleanupId);
                warnings.Add("The entertainment area could not be deactivated during cleanup recovery.");
            }

            try
            {
                var restoreResult = await targetHueClient.RestoreLightStatesWithResult(
                    target.BridgeIp,
                    target.AppKey,
                    savedLightStates,
                    cleanupToken).ConfigureAwait(false);
                if (!restoreResult.Succeeded)
                {
                    warnings.Add(
                        $"Light restoration was incomplete: restored {restoreResult.RestoredCount} of {restoreResult.AttemptedCount} light(s); {restoreResult.FailedCount} failed or exceeded the cleanup deadline.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not restore Hue light state while recovering scheduled cleanup for {0}",
                    entry.CleanupId);
                warnings.Add("Saved light state could not be restored during cleanup recovery.");
            }

            if (warnings.Count == 0)
            {
                if (!_scheduledCleanupJournal.Remove(entry.CleanupId))
                {
                    _logger.LogWarning(
                        "Scheduled Hue cleanup for {0} succeeded but its durable record could not be cleared; it will be retried safely.",
                        entry.CleanupId);
                }
                else
                {
                    _logger.LogInformation(
                        "Recovered scheduled Hue cleanup for {0} after {1} persisted attempt(s).",
                        entry.CleanupId,
                        entry.AttemptCount);
                }
            }
            else
            {
                var warning = string.Join(" ", warnings);
                _scheduledCleanupJournal.RecordFailure(entry.CleanupId, warning, nowUtc);
                _logger.LogWarning(
                    "Scheduled Hue cleanup recovery for {0} remains pending: {1}",
                    entry.CleanupId,
                    warning);
            }
        }
    }

    private static bool TryResolvePendingCleanupTarget(
        PluginConfiguration config,
        HueSceneAutomationPendingCleanupEntry entry,
        out HueSceneAutomationTargetDescription target,
        out string error)
    {
        target = new HueSceneAutomationTargetDescription();
        error = string.Empty;
        var requestedDeviceId = string.IsNullOrWhiteSpace(entry.TargetDeviceId)
            ? null
            : entry.TargetDeviceId.Trim();
        return TryResolveSingleTarget(
            config,
            new HueSceneSchedule(),
            entry.TargetUserId,
            requestedDeviceId,
            out target,
            out error,
            requireClientKey: false);
    }

    private async Task<bool> IsSamePendingCleanupBridgeAsync(
        PluginConfiguration config,
        string targetBridgeHost,
        string? capturedBridgeHost,
        CancellationToken cancellationToken)
    {
        if (HueBridgeCertificateValidation.IsSameBridgeHost(targetBridgeHost, capturedBridgeHost))
        {
            return true;
        }

        // An explicit pin on each persisted spelling is already a physical-identity
        // proof. Resolve the hosts only when one side lacks a direct pin; this keeps
        // tests and restart recovery deterministic when a stale .local name is gone,
        // while conflicting direct pins still fail closed before any bridge request.
        var hasTargetPin = HasExplicitPendingCleanupCertificatePin(config, targetBridgeHost);
        var hasCapturedPin = HasExplicitPendingCleanupCertificatePin(config, capturedBridgeHost);
        if (hasTargetPin && hasCapturedPin)
        {
            var targetFingerprint = HueBridgeCertificateValidation.GetConfiguredCertificateFingerprint(
                config,
                targetBridgeHost);
            var capturedFingerprint = HueBridgeCertificateValidation.GetConfiguredCertificateFingerprint(
                config,
                capturedBridgeHost);
            return !string.IsNullOrWhiteSpace(targetFingerprint) &&
                   string.Equals(targetFingerprint, capturedFingerprint, StringComparison.OrdinalIgnoreCase);
        }

        var targetAddress = await TryResolvePendingCleanupBridgeAddressAsync(
                targetBridgeHost,
                cancellationToken)
            .ConfigureAwait(false);
        var capturedAddress = await TryResolvePendingCleanupBridgeAddressAsync(
                capturedBridgeHost,
                cancellationToken)
            .ConfigureAwait(false);

        if (targetAddress != null && capturedAddress != null && targetAddress.Equals(capturedAddress))
        {
            // A matching vetted address proves the physical route. If any configured
            // pin explicitly refers to either spelling/address, nevertheless require
            // both views to resolve one unambiguous certificate identity; this keeps a
            // conflicting alias pair from being treated as equivalent merely because
            // one side has an exact pin.
            return HasConsistentPendingCleanupCertificateIdentity(
                config,
                targetBridgeHost,
                capturedBridgeHost,
                targetAddress,
                capturedAddress);
        }

        if (targetAddress != null && capturedAddress != null)
        {
            // Different vetted addresses are unrelated unless the explicit-pin proof
            // above matched. Do not fall back to a sole local pin for an unknown IP.
            return false;
        }

        if (string.IsNullOrWhiteSpace(capturedBridgeHost))
            return false;

        // Address resolution may be unavailable after a restart or a host migration.
        // The existing asynchronous pin resolver can still prove equivalence when both
        // persisted spellings carry the same explicit certificate identity. It never
        // treats a sole unrelated local pin as an alias, and it remains outside the
        // synchronous playback/resource-key path.
        var equivalentPinHosts = await HueBridgeCertificateValidation
            .FindEquivalentCertificatePinHostsAsync(
                config,
                targetBridgeHost,
                targetAddress,
                cancellationToken)
            .ConfigureAwait(false);
        return equivalentPinHosts.Any(candidate =>
            HueBridgeCertificateValidation.IsSameBridgeHost(candidate, capturedBridgeHost));
    }

    private async Task<IPAddress?> TryResolvePendingCleanupBridgeAddressAsync(
        string? bridgeHost,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bridgeHost))
            return null;

        try
        {
            var resolvedAddress = await _bridgeAddressResolver(bridgeHost, cancellationToken).ConfigureAwait(false);
            // Keep the recovery proof fail-closed even when a test or host-provided
            // resolver is injected: only the same private/link-local/unique-local
            // boundary accepted by the production resolver may establish identity.
            return HueBridgeCertificateValidation.IsLocalAddress(resolvedAddress)
                ? resolvedAddress
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // An unavailable alias cannot prove target identity by spelling alone. The
            // caller may still use explicit matching certificate pins as a safe proof.
            return null;
        }
    }

    private static bool HasExplicitPendingCleanupCertificatePin(
        PluginConfiguration config,
        string? bridgeHost)
        => !string.IsNullOrWhiteSpace(bridgeHost) &&
           (config.HueBridgeCertificatePins ?? new Dictionary<string, string>())
               .Keys
               .Any(pinHost => HueBridgeCertificateValidation.IsSameBridgeHost(pinHost, bridgeHost));

    private static bool HasConsistentPendingCleanupCertificateIdentity(
        PluginConfiguration config,
        string targetBridgeHost,
        string? capturedBridgeHost,
        IPAddress targetAddress,
        IPAddress capturedAddress)
    {
        if (string.IsNullOrWhiteSpace(capturedBridgeHost))
            return false;

        var targetFingerprint = HueBridgeCertificateValidation.GetConfiguredCertificateFingerprint(
            config,
            targetBridgeHost,
            targetAddress);
        var capturedFingerprint = HueBridgeCertificateValidation.GetConfiguredCertificateFingerprint(
            config,
            capturedBridgeHost,
            capturedAddress);
        var hasRelevantPin = (config.HueBridgeCertificatePins ?? new Dictionary<string, string>())
            .Keys
            .Any(pinHost =>
                HueBridgeCertificateValidation.IsSameBridgeHost(pinHost, targetBridgeHost) ||
                HueBridgeCertificateValidation.IsSameBridgeHost(pinHost, capturedBridgeHost) ||
                HueBridgeCertificateValidation.IsSameBridgeHost(pinHost, targetAddress.ToString()) ||
                HueBridgeCertificateValidation.IsSameBridgeHost(pinHost, capturedAddress.ToString()));

        if (!hasRelevantPin)
            return true;

        return !string.IsNullOrWhiteSpace(targetFingerprint) &&
               string.Equals(targetFingerprint, capturedFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    internal async Task RunDueSchedulesAsync(DateTime localNow, CancellationToken cancellationToken)
    {
        using var schedulerEvaluation = BeginSchedulerEvaluation();
        if (schedulerEvaluation == null)
            return;

        // Scheduler passes are also persistence-repair opportunities. This retries
        // finite run-state writes that failed after the previous pass completed.
        EnsureHistoryLoaded();
        EnsureDeferredRunsLoaded();
        EnsureAutomaticOccurrenceClaimsLoaded();
        var config = Plugin.Instance?.Configuration;
        if (config == null)
            return;

        // A successful one-time cue must remain disabled even when its first completion
        // write failed. Repair that write before evaluating due schedules so a retry never
        // needs to replay bridge activity.
        await RetryPendingScheduledCleanupsAsync(config, cancellationToken).ConfigureAwait(false);
        RetryPendingOneTimeCancellations(config);
        RetryPendingOneTimeCompletions(config);
        if (!config.SceneAutomationEnabled)
            return;

        // Keep the logical scheduler clock anchored to the caller's observation while
        // allowing a long restorative cue to advance that clock for schedules that have
        // not been evaluated yet. This recovers in-process overlaps without changing the
        // configured restart catch-up window or depending on the host wall clock's date.
        var schedulerStartedAtUtc = DateTime.UtcNow;
        var logicalStartUtc = ConvertServerLocalNowToUtc(localNow);

        var configuredSchedules = config.SceneSchedules ?? new List<HueSceneSchedule>();
        var schedules = configuredSchedules
            .Where(schedule => schedule != null)
            // A malformed persisted configuration can contain case-variant duplicate IDs.
            // Do not let scheduler collection order choose one cue or merge both into the
            // same runtime/occurrence state key; the administrator can repair the config
            // through the normal validation path.
            .Where(schedule => PluginConfiguration.TryResolveUniqueSceneSchedule(
                configuredSchedules,
                schedule.Id,
                out _,
                out _))
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
            ClearAllAutomaticOccurrenceClaims();
            return;
        }

        PruneAutomaticOccurrenceClaims(schedules, persist: true);

        var catchUpMinutes = Math.Clamp(
            config.SceneAutomationCatchUpMinutes,
            PluginConfiguration.MinSceneAutomationCatchUpMinutes,
            PluginConfiguration.MaxSceneAutomationCatchUpMinutes);
        var deferMinutes = Math.Clamp(
            config.SceneAutomationDeferMinutes,
            PluginConfiguration.MinSceneAutomationDeferMinutes,
            PluginConfiguration.MaxSceneAutomationDeferMinutes);
        var playbackScope = GetPlaybackConflictScope(config);

        PruneDeferredRuns(schedules, config, persist: true);

        foreach (var schedule in schedules)
        {
            var elapsed = DateTime.UtcNow - schedulerStartedAtUtc;
            if (elapsed < TimeSpan.Zero)
                elapsed = TimeSpan.Zero;
            // Advance an absolute UTC instant, then convert it back to server local
            // time for schedule matching. Adding elapsed time to a Local DateTime can
            // manufacture invalid/ambiguous wall-clock values across DST transitions.
            var evaluationNow = ConvertUtcToServerLocal(logicalStartUtc + elapsed);
            var inProcessCatchUpMinutes = GetInProcessCatchUpMinutes(elapsed);
            var deferDuringPlayback = string.Equals(
                GetEffectivePlaybackPolicy(config, schedule),
                PluginConfiguration.SceneAutomationPlaybackPolicyDefer,
                StringComparison.OrdinalIgnoreCase);
            HueSceneDeferredRun? deferredRun = null;
            var deferredExpired = false;
            var hasDeferredRun = deferDuringPlayback && TryGetDeferredRun(
                schedule,
                evaluationNow,
                deferMinutes,
                out deferredRun,
                out deferredExpired);
            if (deferredExpired)
            {
                if (TryClaimRunSlot(schedule.Id, deferredRun!.OccurrenceSlot))
                {
                    // An expired deferred one-time cue is still consuming its only
                    // occurrence. Claim and persist the disabled state before recording
                    // that skip so a restart cannot restore an enabled cue and consume the
                    // same deferred occurrence again. Recurring cues keep their existing
                    // skip-recording behavior.
                    if (!string.IsNullOrWhiteSpace(schedule.RunDate) &&
                        !TryClaimAutomaticOneTimeSchedule(config, schedule))
                    {
                        ReleaseRunSlot(schedule.Id, deferredRun!.OccurrenceSlot);
                        continue;
                    }

                    RecordSkippedOccurrence(
                        config,
                        schedule,
                        CreateDeferredExpiredResult(
                            config,
                            schedule,
                            deferMinutes,
                            deferredRun!.Restored));
                    RemoveDeferredRun(schedule.Id);
                }

                continue;
            }

            var isDue = hasDeferredRun || IsDue(schedule, evaluationNow);
            var effectiveCatchUpMinutes = Math.Max(catchUpMinutes, inProcessCatchUpMinutes);
            var recoveredOccurrence = hasDeferredRun || isDue
                ? null
                : GetMostRecentMissedOccurrence(schedule, evaluationNow, effectiveCatchUpMinutes);
            if (catchUpMinutes == PluginConfiguration.MinSceneAutomationCatchUpMinutes &&
                recoveredOccurrence != null &&
                recoveredOccurrence.UtcTime <= logicalStartUtc)
            {
                // With restart catch-up disabled, only recover an occurrence that elapsed
                // after this scheduler pass began. Older occurrences remain intentionally
                // skipped, preserving the existing zero-window behavior.
                recoveredOccurrence = null;
            }
            if (!isDue && recoveredOccurrence == null)
                continue;

            var slot = hasDeferredRun
                ? deferredRun!.OccurrenceSlot
                : isDue
                    ? GetScheduleRunSlot(schedule, evaluationNow)
                    : recoveredOccurrence!.UtcTime;
            var wasCatchUp = !hasDeferredRun && !isDue;
            var wasDeferredRestored = hasDeferredRun && deferredRun!.Restored;

            // The in-memory run-slot guard only protects this process. A durable claim
            // is authoritative across scheduler restarts; if a deferred entry survived
            // a completed run's cleanup write, discard that stale work item rather than
            // replaying bridge activity for an already-claimed occurrence.
            if (HasAutomaticOccurrenceClaim(schedule, slot))
            {
                if (hasDeferredRun)
                    RemoveDeferredRun(schedule.Id);
                continue;
            }

            // A pending administrator skip always wins over a deferred occurrence. It is
            // safe to consume it while playback is active because no bridge mutation occurs.
            var playbackActiveForSchedule = IsPlaybackActiveForSchedule(config, schedule, playbackScope);
            if (deferDuringPlayback && playbackActiveForSchedule)
            {
                var skippedOccurrenceClaimed = false;
                if (ShouldClaimAutomaticOccurrence(schedule) && schedule.SkipNextOccurrence)
                {
                    // Claim a skipped recurring occurrence before clearing the
                    // administrator marker. If persistence fails, leave the marker
                    // untouched so a later pass can retry without bridge activity.
                    if (!TryClaimAutomaticOccurrence(config, schedule, slot))
                    {
                        ReleaseRunSlot(schedule.Id, slot);
                        continue;
                    }

                    skippedOccurrenceClaimed = true;
                }

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
                    else if (skippedOccurrenceClaimed)
                    {
                        ReleaseAutomaticOccurrenceClaim(config, schedule, slot);
                    }

                    continue;
                }

                // If clearing a skip marker failed, leave both the marker and any pending
                // deferred occurrence intact so a later poll can retry safely.
                if (blockedSkipPersistenceFailed)
                {
                    if (skippedOccurrenceClaimed)
                        ReleaseAutomaticOccurrenceClaim(config, schedule, slot);
                    continue;
                }

                if (skippedOccurrenceClaimed)
                    ReleaseAutomaticOccurrenceClaim(config, schedule, slot);

                if (!hasDeferredRun)
                {
                    QueueDeferredRun(schedule, slot, evaluationNow, deferMinutes);
                }
                continue;
            }

            if (!TryClaimRunSlot(schedule.Id, slot))
                continue;

            var automaticOccurrenceClaimed = false;
            if (ShouldClaimAutomaticOccurrence(schedule))
            {
                // Persist the recurring occurrence claim before entering any target
                // lifecycle. A failed write is fail-closed: release only the local
                // slot and retry on a later scheduler pass without contacting Hue.
                if (!TryClaimAutomaticOccurrence(config, schedule, slot))
                {
                    ReleaseRunSlot(schedule.Id, slot);
                    continue;
                }

                automaticOccurrenceClaimed = true;
            }

            if (TryConsumeSkippedOccurrence(config, schedule, out var skippedResult, out var skipPersistenceFailed))
            {
                if (skippedResult != null)
                {
                    skippedResult.WasCatchUp = wasCatchUp;
                    skippedResult.WasDeferred = hasDeferredRun;
                    skippedResult.WasDeferredRestored = wasDeferredRestored;
                }
                if (hasDeferredRun)
                    RemoveDeferredRun(schedule.Id);
                RecordSkippedOccurrence(config, schedule, skippedResult!);
                continue;
            }

            // A pending skip is an explicit administrator instruction. If clearing it
            // could not be persisted, do not fall through and run the cue anyway. The
            // marker remains intact so the same occurrence can retry safely. Release the
            // slot claimed above; otherwise the slot guard would suppress that retry.
            if (skipPersistenceFailed)
            {
                if (automaticOccurrenceClaimed)
                    ReleaseAutomaticOccurrenceClaim(config, schedule, slot);
                ReleaseRunSlot(schedule.Id, slot);
                continue;
            }

            // A one-time cue must have a durable claim before any bridge lifecycle can
            // begin. The in-memory occurrence slot protects only this process; persisting
            // the disabled state first is what prevents a restart from replaying the same
            // RunDate when post-run history or cleanup writes fail. If the claim cannot be
            // saved, leave the cue untouched and retry the occurrence later without
            // contacting the bridge.
            var automaticOneTimeClaimed = false;
            if (!string.IsNullOrWhiteSpace(schedule.RunDate))
            {
                if (!TryClaimAutomaticOneTimeSchedule(config, schedule))
                {
                    ReleaseRunSlot(schedule.Id, slot);
                    continue;
                }

                automaticOneTimeClaimed = true;
            }

            var result = await RunScheduleTrackedAsync(
                config,
                schedule,
                cancellationToken,
                automaticRun: true,
                automaticOneTimeClaimed: automaticOneTimeClaimed,
                wasCatchUp: wasCatchUp,
                wasDeferred: hasDeferredRun,
                wasDeferredRestored: wasDeferredRestored,
                targetScopedPlayback: string.Equals(
                    playbackScope,
                    PluginConfiguration.SceneAutomationPlaybackScopeMatchingTarget,
                    StringComparison.OrdinalIgnoreCase),
                runAtUtcOverride: slot,
                schedulerBarrierHeld: true).ConfigureAwait(false);
            var canceledDuringAutomaticRun = cancellationToken.IsCancellationRequested &&
                !result.Succeeded;
            var deferredPlaybackBlocked = deferDuringPlayback &&
                result.BlockedByPlayback &&
                !result.Succeeded &&
                !canceledDuringAutomaticRun;
            if (deferredPlaybackBlocked)
            {
                // Playback won the target lifecycle race after the point-in-time
                // scheduler check. The diagnostic made no bridge mutation, so release
                // the in-memory claim and retain the exact occurrence for the defer
                // window instead of counting or consuming a failed attempt.
                ReleaseRunSlot(schedule.Id, slot);
                if (automaticOneTimeClaimed)
                    RestoreAutomaticOneTimeScheduleAfterPlaybackConflict(config, schedule);
                if (!hasDeferredRun)
                    QueueDeferredRun(schedule, slot, evaluationNow, deferMinutes);
                _logger.LogInformation(
                    "Hue scene schedule {0} was deferred because playback became active before its target lifecycle started",
                    schedule.Name);
                continue;
            }

            if (hasDeferredRun && !canceledDuringAutomaticRun)
                RemoveDeferredRun(schedule.Id);

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

    private bool TryClaimAutomaticOneTimeSchedule(
        PluginConfiguration config,
        HueSceneSchedule schedule)
    {
        var key = schedule.Id?.Trim() ?? string.Empty;
        if (!PluginConfiguration.TryResolveUniqueSceneSchedule(
                config.SceneSchedules,
                key,
                out var configuredSchedule,
                out _)
            || !configuredSchedule.Enabled)
            return false;

        var previousEnabled = configuredSchedule.Enabled;
        var previousSkipNextOccurrence = configuredSchedule.SkipNextOccurrence;
        lock (_runtimeStateLock)
        {
            // A manual run may have acquired this cue after the scheduler cloned its
            // configuration. Do not consume the automatic occurrence in that case; the
            // manual lifecycle owns the cue and the scheduler will retry after it ends.
            if (_runtimeStates.TryGetValue(key, out var state) && state.ActiveRuns > 0)
                return false;

            configuredSchedule.Enabled = false;
            configuredSchedule.SkipNextOccurrence = false;
            try
            {
                Plugin.Instance?.SaveConfiguration();
                return true;
            }
            catch (Exception ex)
            {
                configuredSchedule.Enabled = previousEnabled;
                configuredSchedule.SkipNextOccurrence = previousSkipNextOccurrence;
                _logger.LogWarning(
                    ex,
                    "One-time Hue scene schedule {0} could not be claimed before its automatic run",
                    schedule.Name);
                return false;
            }
        }
    }

    private void RestoreAutomaticOneTimeScheduleAfterPlaybackConflict(
        PluginConfiguration config,
        HueSceneSchedule schedule)
    {
        var key = schedule.Id?.Trim() ?? string.Empty;
        if (!PluginConfiguration.TryResolveUniqueSceneSchedule(
                config.SceneSchedules,
                key,
                out var configuredSchedule,
                out _))
            return;

        // The one-time claim was persisted before the preview started. A playback
        // conflict is the one failure mode where no bridge mutation occurred, so make
        // the cue eligible again before QueueDeferredRun persists the exact occurrence.
        // Keeping this in-memory lets that single queue write commit both changes.
        lock (_runtimeStateLock)
        {
            configuredSchedule.Enabled = true;
            configuredSchedule.SkipNextOccurrence = false;
        }
    }

    private void RestoreAutomaticOneTimeScheduleAfterCancellation(
        PluginConfiguration config,
        HueSceneSchedule schedule)
    {
        var key = schedule.Id?.Trim() ?? string.Empty;
        if (!PluginConfiguration.TryResolveUniqueSceneSchedule(
                config.SceneSchedules,
                key,
                out var configuredSchedule,
                out _))
            return;

        // The one-time claim is persisted before bridge work begins. A host cancellation
        // means the automatic run did not complete, so restore the durable eligibility
        // gate before the scheduler lifecycle ends. This also keeps a deferred occurrence
        // discoverable after a restart. If this repair write fails, retain the eligible
        // in-memory state and retry it before the next scheduler evaluation. The durable
        // pre-run claim remains disabled until that repair succeeds, so an intervening
        // restart fails closed instead of replaying an ambiguous run.
        lock (_runtimeStateLock)
        {
            configuredSchedule.Enabled = true;
            configuredSchedule.SkipNextOccurrence = false;
            try
            {
                Plugin.Instance?.SaveConfiguration();
                ClearOneTimeCancellationPersistencePending(new[] { configuredSchedule.Id ?? string.Empty });
            }
            catch (Exception ex)
            {
                MarkOneTimeCancellationPersistencePending(configuredSchedule.Id);
                _logger.LogWarning(
                    ex,
                    "Canceled one-time Hue scene schedule {0} could not persist its restored enabled state",
                    schedule.Name);
            }
        }
    }

    private void PruneDeferredRuns(
        IReadOnlyList<HueSceneSchedule> schedules,
        PluginConfiguration? config,
        bool persist)
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

        if (staleIds.Length > 0 && !persist)
        {
            lock (_deferredRunLock)
            {
                _deferredRunsPersistencePending = true;
            }
        }
        else if (staleIds.Length > 0 || (persist && HasDeferredRunsPersistencePending()))
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
        if (!PluginConfiguration.TryResolveUniqueSceneSchedule(
                config.SceneSchedules,
                key,
                out var configuredSchedule,
                out _)
            || !configuredSchedule.SkipNextOccurrence)
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
        lock (_bridgeLifecycleGate.HistorySynchronization)
        {
            RecordSkippedOccurrenceCore(config, schedule, result);
        }
    }

    private void RecordSkippedOccurrenceCore(
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
            BrightnessPercent = GetEffectiveScheduleBrightness(config, schedule),
            Red = isPlaylist || preset == null
                ? 0
                : Math.Clamp(
                    schedule.Red ?? preset.Red,
                    PluginConfiguration.MinScenePlaylistStepColorValue,
                    PluginConfiguration.MaxScenePlaylistStepColorValue),
            Green = isPlaylist || preset == null
                ? 0
                : Math.Clamp(
                    schedule.Green ?? preset.Green,
                    PluginConfiguration.MinScenePlaylistStepColorValue,
                    PluginConfiguration.MaxScenePlaylistStepColorValue),
            Blue = isPlaylist || preset == null
                ? 0
                : Math.Clamp(
                    schedule.Blue ?? preset.Blue,
                    PluginConfiguration.MinScenePlaylistStepColorValue,
                    PluginConfiguration.MaxScenePlaylistStepColorValue),
            TargetLabel = ResolveTargetLabel(config, schedule),
            TargetAllEnabledMappings = schedule.TargetAllEnabledMappings,
            TargetUserIds = schedule.TargetUserIds?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(PluginConfiguration.NormalizeJellyfinUserId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                ?? Array.Empty<string>(),
            TargetRoutes = GetScheduleTargetRoutes(schedule),
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

        if (!PluginConfiguration.TryResolveUniqueSceneSchedule(
                config.SceneSchedules,
                schedule.Id,
                out var configuredSchedule,
                out _)
            || !configuredSchedule.Enabled)
            return;

        var previousSkipNextOccurrence = configuredSchedule.SkipNextOccurrence;
        var persistenceFailed = false;
        lock (_runtimeStateLock)
        {
            configuredSchedule.Enabled = false;
            configuredSchedule.SkipNextOccurrence = false;
            try
            {
                Plugin.Instance?.SaveConfiguration();
            }
            catch (Exception ex)
            {
                // The cue already ran successfully. Keep it disabled in memory while a
                // later scheduler pass retries the durable state transition; restoring
                // Enabled here would permit a restart or another evaluation to replay it.
                configuredSchedule.Enabled = false;
                configuredSchedule.SkipNextOccurrence = previousSkipNextOccurrence;
                persistenceFailed = true;
                _logger.LogWarning(ex, "One-time Hue scene schedule {0} ran but could not persist its completed state", schedule.Name);
            }
        }

        if (persistenceFailed)
            MarkOneTimeCompletionPersistencePending(configuredSchedule.Id);
    }

    private void RetryPendingOneTimeCancellations(PluginConfiguration config)
    {
        string[] pendingIds;
        lock (_runtimeStateLock)
        {
            pendingIds = _oneTimeCancellationPersistencePending.ToArray();
        }

        if (pendingIds.Length == 0)
            return;

        var pendingSchedules = new List<HueSceneSchedule>(pendingIds.Length);
        var staleIds = new List<string>();
        foreach (var pendingId in pendingIds)
        {
            if (!PluginConfiguration.TryResolveUniqueSceneSchedule(
                    config.SceneSchedules,
                    pendingId,
                    out var configuredSchedule,
                    out _)
                || string.IsNullOrWhiteSpace(configuredSchedule.RunDate)
                || !configuredSchedule.Enabled)
            {
                // A deleted, converted, or deliberately disabled cue no longer has a
                // cancellation restoration to repair. Respect that administrator state.
                staleIds.Add(pendingId);
                continue;
            }

            pendingSchedules.Add(configuredSchedule);
        }

        if (staleIds.Count > 0)
            ClearOneTimeCancellationPersistencePending(staleIds);
        if (pendingSchedules.Count == 0)
            return;

        try
        {
            lock (_runtimeStateLock)
            {
                foreach (var schedule in pendingSchedules)
                {
                    schedule.Enabled = true;
                    schedule.SkipNextOccurrence = false;
                }

                Plugin.Instance?.SaveConfiguration();
            }

            ClearOneTimeCancellationPersistencePending(pendingSchedules
                .Select(schedule => schedule.Id?.Trim() ?? string.Empty)
                .Where(id => !string.IsNullOrWhiteSpace(id)));
        }
        catch (Exception ex)
        {
            foreach (var schedule in pendingSchedules)
            {
                _logger.LogWarning(
                    ex,
                    "Canceled one-time Hue scene schedule {0} could not persist its restored enabled state",
                    schedule.Name);
            }
        }
    }

    private void RetryPendingOneTimeCompletions(PluginConfiguration config)
    {
        string[] pendingIds;
        lock (_runtimeStateLock)
        {
            pendingIds = _oneTimeCompletionPersistencePending.ToArray();
        }

        if (pendingIds.Length == 0)
            return;

        var pendingSchedules = new List<HueSceneSchedule>(pendingIds.Length);
        var staleIds = new List<string>();
        foreach (var pendingId in pendingIds)
        {
            if (!PluginConfiguration.TryResolveUniqueSceneSchedule(
                    config.SceneSchedules,
                    pendingId,
                    out var configuredSchedule,
                    out _)
                || string.IsNullOrWhiteSpace(configuredSchedule.RunDate))
            {
                // A deleted or deliberately converted recurring cue no longer has a
                // one-time completion transition to repair.
                staleIds.Add(pendingId);
                continue;
            }

            pendingSchedules.Add(configuredSchedule);
        }

        if (staleIds.Count > 0)
            ClearOneTimeCompletionPersistencePending(staleIds);
        if (pendingSchedules.Count == 0)
            return;

        try
        {
            lock (_runtimeStateLock)
            {
                foreach (var schedule in pendingSchedules)
                {
                    schedule.Enabled = false;
                    schedule.SkipNextOccurrence = false;
                }

                Plugin.Instance?.SaveConfiguration();
            }

            ClearOneTimeCompletionPersistencePending(pendingSchedules
                .Select(schedule => schedule.Id?.Trim() ?? string.Empty)
                .Where(id => !string.IsNullOrWhiteSpace(id)));
        }
        catch (Exception ex)
        {
            foreach (var schedule in pendingSchedules)
            {
                // Keep the same diagnostic contract as the initial failed write. The
                // marker remains set and the next scheduler pass retries again.
                _logger.LogWarning(ex, "One-time Hue scene schedule {0} ran but could not persist its completed state", schedule.Name);
            }
        }
    }

    private void MarkOneTimeCompletionPersistencePending(string? scheduleId)
    {
        var key = scheduleId?.Trim();
        if (string.IsNullOrWhiteSpace(key))
            return;

        lock (_runtimeStateLock)
        {
            _oneTimeCompletionPersistencePending.Add(key);
        }
    }

    private void MarkOneTimeCancellationPersistencePending(string? scheduleId)
    {
        var key = scheduleId?.Trim();
        if (string.IsNullOrWhiteSpace(key))
            return;

        lock (_runtimeStateLock)
        {
            _oneTimeCancellationPersistencePending.Add(key);
        }
    }

    private void ClearOneTimeCancellationPersistencePending(IEnumerable<string> scheduleIds)
    {
        lock (_runtimeStateLock)
        {
            foreach (var scheduleId in scheduleIds)
            {
                var key = scheduleId?.Trim();
                if (!string.IsNullOrWhiteSpace(key))
                    _oneTimeCancellationPersistencePending.Remove(key);
            }
        }
    }

    private void ClearOneTimeCompletionPersistencePending(IEnumerable<string> scheduleIds)
    {
        lock (_runtimeStateLock)
        {
            foreach (var scheduleId in scheduleIds)
            {
                var key = scheduleId?.Trim();
                if (!string.IsNullOrWhiteSpace(key))
                    _oneTimeCompletionPersistencePending.Remove(key);
            }
        }
    }

    private async Task<HueSceneAutomationRunResult> RunScheduleCoreAsync(
        PluginConfiguration config,
        HueSceneSchedule schedule,
        CancellationToken cancellationToken,
        bool targetScopedPlayback = false,
        DateTime? runAtUtcOverride = null,
        bool durableCleanup = false)
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
                StepDurationSeconds = playlist.StepDurationSeconds?.ToList() ?? new List<int>(),
                StepBrightnessPercent = playlist.StepBrightnessPercent?.ToList() ?? new List<int?>(),
                StepRed = playlist.StepRed?.ToList() ?? new List<int?>(),
                StepGreen = playlist.StepGreen?.ToList() ?? new List<int?>(),
                StepBlue = playlist.StepBlue?.ToList() ?? new List<int?>(),
                StepEffects = playlist.StepEffects?.ToList() ?? new List<string?>(),
                StepEffectSpeedPercent = playlist.StepEffectSpeedPercent?.ToList() ?? new List<int?>(),
                StepTransitionSeconds = playlist.StepTransitionSeconds?.ToList() ?? new List<int?>(),
                StepTransitionOutSeconds = playlist.StepTransitionOutSeconds?.ToList() ?? new List<int?>(),
                StepTransitionCurves = playlist.StepTransitionCurves?.ToList() ?? new List<string?>(),
                RepeatCount = playlist.RepeatCount,
                PlaybackOrder = playlist.PlaybackOrder,
                TargetUserId = schedule.TargetAllEnabledMappings
                    || schedule.IncludeDefaultTarget
                    || (schedule.TargetUserIds?.Count ?? 0) > 0
                    ? string.Empty
                    : PluginConfiguration.NormalizeJellyfinUserId(schedule.TargetUserId),
                TargetAllEnabledMappings = schedule.TargetAllEnabledMappings
            };
            var selectedTargetIds = schedule.IncludeDefaultTarget || (schedule.TargetUserIds?.Count ?? 0) > 0
                ? schedule.TargetUserIds?.ToList() ?? new List<string>()
                : null;
            var playlistRun = await RunPlaylistPreviewDurableAsync(
                scheduledPlaylist,
                cancellationToken,
                selectedTargetIds,
                schedule.IncludeDefaultTarget,
                targetScopedPlayback,
                runAtUtcOverride,
                GetScheduleTargetRoutes(schedule),
                durableCleanup,
                schedule.Id).ConfigureAwait(false);
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
                targetScopedPlayback,
                brightnessPercentOverride: schedule.BrightnessPercent,
                redOverride: schedule.Red,
                greenOverride: schedule.Green,
                blueOverride: schedule.Blue,
                durableCleanup: durableCleanup).ConfigureAwait(false);
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
            BrightnessPercent = Math.Clamp(
                schedule.BrightnessPercent ?? preset.BrightnessPercent,
                PluginConfiguration.MinScenePlaylistStepBrightnessPercent,
                PluginConfiguration.MaxScenePlaylistStepBrightnessPercent),
            Red = schedule.Red.HasValue
                ? Math.Clamp(schedule.Red.Value, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue)
                : Math.Clamp(preset.Red, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue),
            Green = schedule.Green.HasValue
                ? Math.Clamp(schedule.Green.Value, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue)
                : Math.Clamp(preset.Green, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue),
            Blue = schedule.Blue.HasValue
                ? Math.Clamp(schedule.Blue.Value, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue)
                : Math.Clamp(preset.Blue, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue),
            TargetLabel = ResolveTargetLabel(config, schedule),
            TargetAllEnabledMappings = schedule.TargetAllEnabledMappings,
            TargetUserIds = schedule.TargetUserIds?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(PluginConfiguration.NormalizeJellyfinUserId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                ?? Array.Empty<string>(),
            TargetRoutes = GetScheduleTargetRoutes(schedule),
            IncludeDefaultTarget = schedule.IncludeDefaultTarget,
            Succeeded = allSucceeded,
            Message = message,
            CleanupWarning = string.IsNullOrWhiteSpace(cleanupWarning) ? null : cleanupWarning,
            TargetResults = targetResults,
            BlockedByPlayback = targetResults.Count > 0 &&
                !targetResults.Any(result => result.Succeeded) &&
                targetResults.All(result => result.BlockedByPlayback),
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
            BlockedByPlayback = target.BlockedByPlayback,
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
            PlaylistPlaybackOrder = playlistRun.PlaybackOrder,
            Effect = PluginConfiguration.SceneScheduleEffectPlaylist,
            EffectSpeedPercent = PluginConfiguration.DefaultColorPresetEffectSpeedPercent,
            BrightnessPercent = null,
            TargetLabel = string.IsNullOrWhiteSpace(playlistRun.TargetLabel)
                ? ResolveTargetLabel(config, schedule)
                : playlistRun.TargetLabel,
            TargetAllEnabledMappings = schedule.TargetAllEnabledMappings,
            TargetUserIds = schedule.TargetUserIds?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(PluginConfiguration.NormalizeJellyfinUserId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                ?? Array.Empty<string>(),
            TargetRoutes = GetScheduleTargetRoutes(schedule),
            IncludeDefaultTarget = schedule.IncludeDefaultTarget,
            Succeeded = playlistRun.Succeeded,
            Message = playlistRun.Message,
            CleanupWarning = playlistRun.CleanupWarning,
            TargetResults = targetResults,
            PlaylistSteps = playlistRun.Steps ?? Array.Empty<HueScenePlaylistStepResult>(),
            BlockedByPlayback = targetResults.Length > 0 &&
                !targetResults.Any(result => result.Succeeded) &&
                targetResults.All(result => result.BlockedByPlayback),
            RunAtUtc = playlistRun.RunAtUtc
        };
    }

    private async Task<HueSceneScheduleTargetResult> RunScheduleTargetAsync(
        HueSceneSchedule schedule,
        HueColorPreset preset,
        HueSceneAutomationTargetDescription target,
        CancellationToken cancellationToken,
        bool targetScopedPlayback = false,
        int? brightnessPercentOverride = null,
        int? transitionSecondsOverride = null,
        int? transitionOutSecondsOverride = null,
        string? transitionCurveOverride = null,
        int? effectSpeedPercentOverride = null,
        string? effectOverride = null,
        int? redOverride = null,
        int? greenOverride = null,
        int? blueOverride = null,
        bool durableCleanup = false)
    {
        try
        {
            // Keep retry state isolated per scheduled target. The hosted service and
            // stream tester are singletons, so mutating their shared clients here would
            // let concurrent rooms overwrite one another's network policy.
            var targetHueClient = _hueClient.CreatePlaybackClient();
            targetHueClient.RetryAttempts = target.RetryAttempts;
            var areaConfiguration = await targetHueClient.GetEntertainmentConfiguration(
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

            var targetStreamTester = _streamTester is IHueRetryAwareStreamTester retryAwareTester
                ? retryAwareTester.CreateForRetryAttempts(target.RetryAttempts)
                : _streamTester;
            var scopedTester = targetScopedPlayback
                ? targetStreamTester as IHueTargetScopedStreamTester
                : null;
            var curveTester = targetStreamTester as IHueTransitionCurveStreamTester;
            var transitionCurve = !string.IsNullOrWhiteSpace(transitionCurveOverride) &&
                PluginConfiguration.TryNormalizeColorPresetTransitionCurve(
                    transitionCurveOverride,
                    out var normalizedTransitionCurve)
                ? normalizedTransitionCurve
                : GetEffectiveTransitionCurve(preset);
            var brightnessPercent = brightnessPercentOverride.HasValue
                ? Math.Clamp(
                    brightnessPercentOverride.Value,
                    PluginConfiguration.MinScenePlaylistStepBrightnessPercent,
                    PluginConfiguration.MaxScenePlaylistStepBrightnessPercent)
                : Math.Clamp(
                    preset.BrightnessPercent,
                    PluginConfiguration.MinScenePlaylistStepBrightnessPercent,
                    PluginConfiguration.MaxScenePlaylistStepBrightnessPercent);
            var red = redOverride.HasValue
                ? Math.Clamp(
                    redOverride.Value,
                    PluginConfiguration.MinScenePlaylistStepColorValue,
                    PluginConfiguration.MaxScenePlaylistStepColorValue)
                : Math.Clamp(
                    preset.Red,
                    PluginConfiguration.MinScenePlaylistStepColorValue,
                    PluginConfiguration.MaxScenePlaylistStepColorValue);
            var green = greenOverride.HasValue
                ? Math.Clamp(
                    greenOverride.Value,
                    PluginConfiguration.MinScenePlaylistStepColorValue,
                    PluginConfiguration.MaxScenePlaylistStepColorValue)
                : Math.Clamp(
                    preset.Green,
                    PluginConfiguration.MinScenePlaylistStepColorValue,
                    PluginConfiguration.MaxScenePlaylistStepColorValue);
            var blue = blueOverride.HasValue
                ? Math.Clamp(
                    blueOverride.Value,
                    PluginConfiguration.MinScenePlaylistStepColorValue,
                    PluginConfiguration.MaxScenePlaylistStepColorValue)
                : Math.Clamp(
                    preset.Blue,
                    PluginConfiguration.MinScenePlaylistStepColorValue,
                    PluginConfiguration.MaxScenePlaylistStepColorValue);
            var effectSpeedPercent = effectSpeedPercentOverride.HasValue
                ? Math.Clamp(
                    effectSpeedPercentOverride.Value,
                    PluginConfiguration.MinScenePlaylistStepEffectSpeedPercent,
                    PluginConfiguration.MaxScenePlaylistStepEffectSpeedPercent)
                : PluginConfiguration.ClampColorPresetEffectSpeedPercent(preset.EffectSpeedPercent);
            var effect = !string.IsNullOrWhiteSpace(effectOverride) &&
                PluginConfiguration.TryNormalizeColorPresetEffect(effectOverride, out var normalizedEffectOverride)
                ? normalizedEffectOverride
                : PluginConfiguration.TryNormalizeColorPresetEffect(preset.Effect, out var normalizedPresetEffect)
                    ? normalizedPresetEffect
                    : PluginConfiguration.ColorPresetEffectSolid;
            using var cleanupScope = durableCleanup && _scheduledCleanupJournal != null
                ? _scheduledCleanupJournal.BeginScope(new HueScheduledCleanupScope
                {
                    CleanupId = Guid.NewGuid().ToString("N"),
                    ScheduleId = schedule.Id?.Trim() ?? string.Empty,
                    TargetUserId = target.TargetUserId,
                    TargetDeviceId = target.TargetDeviceId,
                    BridgeIp = target.BridgeIp,
                    EntertainmentAreaId = target.EntertainmentAreaId,
                    ChannelIds = target.ChannelIds
                })
                : null;
            var preview = scopedTester != null && curveTester != null
                ? await curveTester.PreviewAsyncForTargetWithTransitionCurve(
                    target.BridgeIp,
                    target.AppKey,
                    target.ClientKey,
                    target.EntertainmentAreaId,
                    areaConfiguration.Value,
                    target.ChannelIds,
                    red,
                    green,
                    blue,
                    brightnessPercent,
                    GetEffectiveDurationSeconds(schedule, preset),
                    cancellationToken,
                    GetEffectiveTransitionSeconds(schedule, preset, transitionSecondsOverride),
                    GetEffectiveTransitionOutSeconds(schedule, preset, transitionOutSecondsOverride, transitionSecondsOverride),
                    effect,
                    effectSpeedPercent,
                    transitionCurve).ConfigureAwait(false)
                : scopedTester != null
                ? await scopedTester.PreviewAsyncForTarget(
                    target.BridgeIp,
                    target.AppKey,
                    target.ClientKey,
                    target.EntertainmentAreaId,
                    areaConfiguration.Value,
                    target.ChannelIds,
                    red,
                    green,
                    blue,
                    brightnessPercent,
                    GetEffectiveDurationSeconds(schedule, preset),
                    cancellationToken,
                    GetEffectiveTransitionSeconds(schedule, preset, transitionSecondsOverride),
                    GetEffectiveTransitionOutSeconds(schedule, preset, transitionOutSecondsOverride, transitionSecondsOverride),
                    effect,
                    effectSpeedPercent).ConfigureAwait(false)
                : curveTester != null
                ? await curveTester.PreviewAsyncWithTransitionCurve(
                    target.BridgeIp,
                    target.AppKey,
                    target.ClientKey,
                    target.EntertainmentAreaId,
                    areaConfiguration.Value,
                    target.ChannelIds,
                    red,
                    green,
                    blue,
                    brightnessPercent,
                    GetEffectiveDurationSeconds(schedule, preset),
                    cancellationToken,
                    GetEffectiveTransitionSeconds(schedule, preset, transitionSecondsOverride),
                    GetEffectiveTransitionOutSeconds(schedule, preset, transitionOutSecondsOverride, transitionSecondsOverride),
                    effect,
                    effectSpeedPercent,
                    transitionCurve).ConfigureAwait(false)
                : await targetStreamTester.PreviewAsync(
                    target.BridgeIp,
                    target.AppKey,
                    target.ClientKey,
                    target.EntertainmentAreaId,
                    areaConfiguration.Value,
                    target.ChannelIds,
                    red,
                    green,
                    blue,
                    brightnessPercent,
                    GetEffectiveDurationSeconds(schedule, preset),
                    cancellationToken,
                    GetEffectiveTransitionSeconds(schedule, preset, transitionSecondsOverride),
                    GetEffectiveTransitionOutSeconds(schedule, preset, transitionOutSecondsOverride, transitionSecondsOverride),
                    effect,
                    effectSpeedPercent).ConfigureAwait(false);
            return new HueSceneScheduleTargetResult
            {
                TargetLabel = target.TargetLabel,
                Succeeded = preview.Succeeded,
                Message = preview.Message,
                CleanupWarning = preview.CleanupWarning,
                BlockedByPlayback = preview.BlockedByPlayback,
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
        bool automaticRun = false,
        bool automaticOneTimeClaimed = false,
        bool wasCatchUp = false,
        bool wasDeferred = false,
        bool wasDeferredRestored = false,
        bool targetScopedPlayback = false,
        DateTime? runAtUtcOverride = null,
        bool schedulerBarrierHeld = false)
    {
        var manualSchedulerLease = schedulerBarrierHeld
            ? null
            : _bridgeLifecycleGate.TryEnterSchedulerEvaluation();
        if (!schedulerBarrierHeld && manualSchedulerLease == null)
        {
            var blocked = Failure(
                schedule.Id,
                "The scheduled scene could not start while configuration is changing.",
                schedule);
            blocked.RunCount = Math.Max(0, schedule.RunCount);
            return blocked;
        }

        using var schedulerLifecycle = manualSchedulerLease;
        using var scheduleLifecycle = BeginScheduleLifecycle();
        if (!TryBeginRun(schedule, out var currentRunCount, out var alreadyRunning))
        {
            // The scheduler claims an occurrence slot before entering this method so
            // overlapping scheduler evaluations cannot start the same cue twice. A
            // manual run may already own the cue, however: scheduler evaluations are
            // allowed to continue beside playback. Do not let that rejected attempt
            // consume the occurrence slot, or the next poll will permanently suppress
            // the occurrence after the manual run completes.
            if (automaticRun && runAtUtcOverride.HasValue)
            {
                ReleaseRunSlot(schedule.Id, runAtUtcOverride.Value);
                ReleaseAutomaticOccurrenceClaim(config, schedule, runAtUtcOverride.Value);
            }

            // The one-time claim is taken immediately before entering this method. If a
            // concurrent manual run or finite-run guard rejects the lifecycle, no bridge
            // work started and the claim must not strand the cue disabled.
            if (automaticOneTimeClaimed)
                RestoreAutomaticOneTimeScheduleAfterCancellation(config, schedule);

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
        var runCompleted = false;
        try
        {
            result = await RunScheduleCoreAsync(
                config,
                schedule,
                cancellationToken,
                targetScopedPlayback,
                runAtUtcOverride,
                durableCleanup: automaticRun).ConfigureAwait(false);
            runCompleted = true;
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
            // A host-stopped automatic cue must remain eligible for the next scheduler
            // instance; manual cancellation keeps its existing attempted-run accounting.
            var automaticCancellationBeforeCompletion = automaticRun &&
                cancellationToken.IsCancellationRequested &&
                (!runCompleted || result is { Succeeded: false });
            var automaticPlaybackConflictBeforeCompletion = automaticRun &&
                !automaticCancellationBeforeCompletion &&
                result is { Succeeded: false, BlockedByPlayback: true };
            if ((automaticCancellationBeforeCompletion || automaticPlaybackConflictBeforeCompletion) &&
                runAtUtcOverride.HasValue)
            {
                ReleaseRunSlot(schedule.Id, runAtUtcOverride.Value);
                if (automaticRun)
                    ReleaseAutomaticOccurrenceClaim(config, schedule, runAtUtcOverride.Value);
            }

            if (automaticCancellationBeforeCompletion && automaticOneTimeClaimed)
                RestoreAutomaticOneTimeScheduleAfterCancellation(config, schedule);

            CompleteRun(
                config,
                schedule,
                result,
                countRun: !automaticCancellationBeforeCompletion && !automaticPlaybackConflictBeforeCompletion,
                preserveDeferred: (automaticCancellationBeforeCompletion && wasDeferred) ||
                    automaticPlaybackConflictBeforeCompletion);
        }
    }

    private HueSceneScheduleRuntimeState GetRuntimeState(string? scheduleId)
    {
        var key = scheduleId?.Trim() ?? string.Empty;
        lock (_runtimeStateLock)
        {
            if (_runtimeStates.TryGetValue(key, out var state))
                return state.Clone();

            var configuredRunCount = PluginConfiguration.TryResolveUniqueSceneSchedule(
                    Plugin.Instance?.Configuration?.SceneSchedules,
                    key,
                    out var configuredSchedule,
                    out _)
                ? configuredSchedule.RunCount
                : 0;
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

    private IDisposable? BeginSchedulerEvaluation()
    {
        var bridgeLifecycleLease = _bridgeLifecycleGate.TryEnterSchedulerEvaluation();
        if (bridgeLifecycleLease == null)
            return null;

        lock (_runtimeStateLock)
        {
            _schedulerEvaluationCount++;
        }

        return new SchedulerEvaluationLease(this, bridgeLifecycleLease);
    }

    private IDisposable BeginScheduleLifecycle()
    {
        lock (_runtimeStateLock)
        {
            _scheduleLifecycleCount++;
        }

        return new ScheduleLifecycleLease(this);
    }

    private void EndSchedulerEvaluation()
    {
        lock (_runtimeStateLock)
        {
            _schedulerEvaluationCount = Math.Max(0, _schedulerEvaluationCount - 1);
        }
    }

    private void EndScheduleLifecycle()
    {
        lock (_runtimeStateLock)
        {
            _scheduleLifecycleCount = Math.Max(0, _scheduleLifecycleCount - 1);
        }
    }

    private sealed class SchedulerEvaluationLease : IDisposable
    {
        private HueSceneAutomationService? _owner;
        private readonly IDisposable _bridgeLifecycleLease;

        public SchedulerEvaluationLease(
            HueSceneAutomationService owner,
            IDisposable bridgeLifecycleLease)
        {
            _owner = owner;
            _bridgeLifecycleLease = bridgeLifecycleLease;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner == null)
                return;

            try
            {
                owner.EndSchedulerEvaluation();
            }
            finally
            {
                _bridgeLifecycleLease.Dispose();
            }
        }
    }

    private sealed class ScheduleLifecycleLease : IDisposable
    {
        private HueSceneAutomationService? _owner;

        public ScheduleLifecycleLease(HueSceneAutomationService owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.EndScheduleLifecycle();
        }
    }

    private void ReleaseConfigurationMutation(IDisposable bridgeLifecycleLease)
    {
        try
        {
            Monitor.Exit(_runtimeStateLock);
        }
        finally
        {
            bridgeLifecycleLease.Dispose();
        }
    }

    private sealed class ConfigurationMutationLease : IDisposable
    {
        private HueSceneAutomationService? _owner;
        private readonly IDisposable _bridgeLifecycleLease;

        public ConfigurationMutationLease(
            HueSceneAutomationService owner,
            IDisposable bridgeLifecycleLease)
        {
            _owner = owner;
            _bridgeLifecycleLease = bridgeLifecycleLease;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseConfigurationMutation(_bridgeLifecycleLease);
        }
    }

    private void CompleteRun(
        PluginConfiguration config,
        HueSceneSchedule schedule,
        HueSceneAutomationRunResult? result,
        bool countRun,
        bool preserveDeferred)
    {
        lock (_bridgeLifecycleGate.HistorySynchronization)
        {
            CompleteRunCore(config, schedule, result, countRun, preserveDeferred);
        }
    }

    private void CompleteRunCore(
        PluginConfiguration config,
        HueSceneSchedule schedule,
        HueSceneAutomationRunResult? result,
        bool countRun,
        bool preserveDeferred)
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
            if (countRun)
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
            if (!preserveDeferred)
            {
                state.DeferredPending = false;
                state.DeferredOccurrenceSlot = null;
                state.DeferredAtLocal = null;
                state.DeferredUntilLocal = null;
                state.DeferredRestored = false;
            }
            if (result != null)
                result.RunCount = state.RunCount;

            if (PluginConfiguration.TryResolveUniqueSceneSchedule(
                    config.SceneSchedules,
                    key,
                    out var configuredSchedule,
                    out _))
            {
                configuredSchedule.RunCount = state.RunCount;
                if (countRun && configuredSchedule.MaxRuns > 0 && state.RunCount >= configuredSchedule.MaxRuns)
                    configuredSchedule.Enabled = false;
                shouldPersistRunState = countRun && configuredSchedule.MaxRuns > 0;
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
            // The in-memory finite-run state is authoritative, but the failed write
            // must remain dirty so the next scheduler/read-repair pass retries it even
            // when history retention is disabled and there is no history payload to save.
            MarkHistoryPersistencePending();
            _logger.LogWarning(
                ex,
                "Hue scene schedule {0} completed but its finite run count could not be persisted",
                scheduleName);
        }
    }

    private void EnsureHistoryLoaded(bool persistRepairs = true)
    {
        lock (_bridgeLifecycleGate.HistorySynchronization)
        {
            EnsureHistoryLoadedCore(persistRepairs);
        }
    }

    private void EnsureHistoryLoadedCore(bool persistRepairs)
    {
        bool historyLoaded;
        bool persistencePending;
        lock (_historyLock)
        {
            historyLoaded = _historyLoaded;
            persistencePending = _historyPersistencePending;
        }

        if (historyLoaded)
        {
            if (persistRepairs && persistencePending)
                PersistPendingSceneScheduleHistoryRepair();
            return;
        }

        var config = Plugin.Instance?.Configuration;
        var persistedEntries = new List<HueSceneScheduleHistoryEntry>();
        var shouldSave = false;
        if (config != null)
        {
            if (persistRepairs)
                config.PersistedSceneScheduleHistory ??= new List<HueSceneScheduleHistoryEntry>();
            var configuredEntries = config.PersistedSceneScheduleHistory ??
                new List<HueSceneScheduleHistoryEntry>();
            if (!config.PersistSceneScheduleHistory)
            {
                shouldSave = configuredEntries.Count > 0;
            }
            else
            {
                persistedEntries = configuredEntries
                    .Where(entry => entry != null)
                    .Take(config.GetSceneScheduleHistoryRetentionCount())
                    .Select(CloneHistoryEntry)
                    .ToList();
                shouldSave = persistedEntries.Count != configuredEntries.Count;
            }
        }

        var loadedResults = persistedEntries
            .Select(ToRunResult)
            .ToArray();
        lock (_historyLock)
        {
            if (_historyLoaded)
            {
                if (!persistRepairs && shouldSave)
                    _historyPersistencePending = true;
                return;
            }

            if (shouldSave)
            {
                if (persistRepairs && config != null)
                {
                    config.PersistedSceneScheduleHistory = persistedEntries
                        .Select(CloneHistoryEntry)
                        .ToList();
                }
                else
                {
                    _historyPersistencePending = true;
                }
            }

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

                    var configuredRunCount = PluginConfiguration.TryResolveUniqueSceneSchedule(
                            Plugin.Instance?.Configuration?.SceneSchedules,
                            group.Key,
                            out var configuredSchedule,
                            out _)
                        ? configuredSchedule.RunCount
                        : 0;
                    // The persisted schedule counter and each history row's RunCount are
                    // authoritative. Do not infer executions from history-row count: an
                    // automatic cancellation is intentionally retained in history with
                    // the prior RunCount so a restart can retry it without consuming a run.
                    var persistedHistoryRunCount = group.Max(result => result.RunCount);
                    state.RunCount = Math.Max(configuredRunCount, persistedHistoryRunCount);
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

        if (shouldSave && persistRepairs && SavePersistedSceneScheduleHistoryConfiguration())
        {
            lock (_historyLock)
            {
                _historyPersistencePending = false;
            }
        }
        else if (shouldSave && persistRepairs)
        {
            MarkHistoryPersistencePending();
        }
    }

    private void PersistPendingSceneScheduleHistoryRepair()
    {
        lock (_bridgeLifecycleGate.HistorySynchronization)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return;

            List<HueSceneScheduleHistoryEntry> entries;
            lock (_historyLock)
            {
                entries = config.PersistSceneScheduleHistory
                    ? _runHistory
                        .Take(config.GetSceneScheduleHistoryRetentionCount())
                        .Select(ToHistoryEntry)
                        .ToList()
                    : new List<HueSceneScheduleHistoryEntry>();
            }

            // A failed initial repair already copied the normalized list into the live
            // configuration object, so comparing against that list cannot establish that
            // the XML file was updated. Persist the authoritative in-memory history whenever
            // the dirty flag is set, even when the configuration appears equal.
            config.PersistedSceneScheduleHistory = entries
                .Select(CloneHistoryEntry)
                .ToList();
            if (SavePersistedSceneScheduleHistoryConfiguration())
            {
                lock (_historyLock)
                {
                    _historyPersistencePending = false;
                }
            }
            else
            {
                MarkHistoryPersistencePending();
            }
        }
    }

    private void PersistSceneScheduleHistory()
    {
        lock (_bridgeLifecycleGate.HistorySynchronization)
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
                {
                    lock (_historyLock)
                    {
                        if (!_historyPersistencePending)
                            return;
                    }
                }

                config.PersistedSceneScheduleHistory.Clear();
            }

            if (SavePersistedSceneScheduleHistoryConfiguration())
            {
                lock (_historyLock)
                {
                    _historyPersistencePending = false;
                }
            }
            else
            {
                MarkHistoryPersistencePending();
            }
        }
    }

    private void TrimRunHistoryLocked()
    {
        var retentionCount = Plugin.Instance?.Configuration?.GetSceneScheduleHistoryRetentionCount()
            ?? PluginConfiguration.DefaultSceneScheduleHistoryRetentionCount;
        if (_runHistory.Count > retentionCount)
            _runHistory.RemoveRange(retentionCount, _runHistory.Count - retentionCount);
    }

    private bool SavePersistedSceneScheduleHistoryConfiguration()
    {
        try
        {
            Plugin.Instance?.SaveConfiguration();
            return true;
        }
        catch (Exception ex)
        {
            // Persistence is diagnostic-only and must never interrupt a cue run.
            _logger.LogWarning(ex, "Could not persist Hue scheduled-scene history");
            return false;
        }
    }

    private void MarkHistoryPersistencePending()
    {
        lock (_historyLock)
        {
            _historyPersistencePending = true;
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
            PlaylistPlaybackOrder = result.PlaylistPlaybackOrder,
            Effect = result.Effect,
            EffectSpeedPercent = result.EffectSpeedPercent,
            TransitionCurve = result.TransitionCurve,
            BrightnessPercent = result.BrightnessPercent,
            Red = result.Red,
            Green = result.Green,
            Blue = result.Blue,
            TargetLabel = result.TargetLabel,
            TargetAllEnabledMappings = result.TargetAllEnabledMappings,
            TargetUserIds = result.TargetUserIds?
                .Select(PluginConfiguration.NormalizeJellyfinUserId)
                .ToList() ?? new List<string>(),
            TargetRoutes = result.TargetRoutes?.Where(route => route != null)
                .Select(route => new HueSceneScheduleTargetRoute
                {
                    UserId = PluginConfiguration.NormalizeJellyfinUserId(route.UserId),
                    DeviceId = route.DeviceId?.Trim() ?? string.Empty
                }).ToList() ?? new List<HueSceneScheduleTargetRoute>(),
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
            PlaylistSteps = result.PlaylistSteps?.Select(ToHistoryPlaylistStep).ToList()
                ?? new List<HueScenePlaylistStepHistoryEntry>(),
            RunAtUtc = result.RunAtUtc,
            RunCount = result.RunCount
        };
    }

    private static HueScenePlaylistStepHistoryEntry ToHistoryPlaylistStep(HueScenePlaylistStepResult source)
    {
        return new HueScenePlaylistStepHistoryEntry
        {
            Index = source.Index,
            RepeatIndex = source.RepeatIndex,
            OriginalIndex = source.OriginalIndex,
            PresetName = source.PresetName,
            Effect = source.Effect,
            EffectSpeedPercent = source.EffectSpeedPercent,
            TransitionCurve = source.TransitionCurve,
            BrightnessPercent = source.BrightnessPercent,
            Red = source.Red,
            Green = source.Green,
            Blue = source.Blue,
            StartOffsetSeconds = source.StartOffsetSeconds,
            DurationSeconds = source.DurationSeconds,
            TransitionSeconds = source.TransitionSeconds,
            TransitionOutSeconds = source.TransitionOutSeconds,
            Succeeded = source.Succeeded,
            Message = source.Message,
            CleanupWarning = source.CleanupWarning,
            TargetResults = source.TargetResults?.Select(CloneTargetResult).ToList()
                ?? new List<HueSceneScheduleTargetResult>()
        };
    }

    private static HueScenePlaylistStepHistoryEntry CloneHistoryPlaylistStep(HueScenePlaylistStepHistoryEntry source)
    {
        return new HueScenePlaylistStepHistoryEntry
        {
            Index = Math.Max(0, source.Index),
            RepeatIndex = Math.Max(PluginConfiguration.MinScenePlaylistRepeatCount, source.RepeatIndex),
            OriginalIndex = Math.Max(0, source.OriginalIndex),
            PresetName = source.PresetName?.Trim() ?? string.Empty,
            Effect = PluginConfiguration.TryNormalizeColorPresetEffect(source.Effect, out var effect)
                ? effect
                : PluginConfiguration.ColorPresetEffectSolid,
            EffectSpeedPercent = PluginConfiguration.ClampColorPresetEffectSpeedPercent(source.EffectSpeedPercent),
            TransitionCurve = PluginConfiguration.TryNormalizeColorPresetTransitionCurve(
                source.TransitionCurve,
                out var transitionCurve)
                ? transitionCurve
                : PluginConfiguration.ColorPresetTransitionCurveLinear,
            BrightnessPercent = source.BrightnessPercent.HasValue
                ? Math.Clamp(
                    source.BrightnessPercent.Value,
                    PluginConfiguration.MinScenePlaylistStepBrightnessPercent,
                    PluginConfiguration.MaxScenePlaylistStepBrightnessPercent)
                : null,
            Red = Math.Clamp(
                source.Red,
                PluginConfiguration.MinScenePlaylistStepColorValue,
                PluginConfiguration.MaxScenePlaylistStepColorValue),
            Green = Math.Clamp(
                source.Green,
                PluginConfiguration.MinScenePlaylistStepColorValue,
                PluginConfiguration.MaxScenePlaylistStepColorValue),
            Blue = Math.Clamp(
                source.Blue,
                PluginConfiguration.MinScenePlaylistStepColorValue,
                PluginConfiguration.MaxScenePlaylistStepColorValue),
            StartOffsetSeconds = Math.Max(0, source.StartOffsetSeconds),
            DurationSeconds = Math.Clamp(
                source.DurationSeconds,
                PluginConfiguration.MinPreviewDurationSeconds,
                PluginConfiguration.MaxPreviewDurationSeconds),
            TransitionSeconds = Math.Clamp(
                source.TransitionSeconds,
                PluginConfiguration.MinColorPresetTransitionSeconds,
                PluginConfiguration.MaxColorPresetTransitionSeconds),
            TransitionOutSeconds = Math.Clamp(
                source.TransitionOutSeconds,
                PluginConfiguration.MinColorPresetTransitionOutSeconds,
                PluginConfiguration.MaxColorPresetTransitionOutSeconds),
            Succeeded = source.Succeeded,
            Message = source.Message?.Trim() ?? string.Empty,
            CleanupWarning = source.CleanupWarning?.Trim(),
            TargetResults = source.TargetResults?.Where(target => target != null)
                .Select(CloneTargetResult)
                .ToList() ?? new List<HueSceneScheduleTargetResult>()
        };
    }

    private static HueScenePlaylistStepResult ToPlaylistStepResult(HueScenePlaylistStepHistoryEntry source)
    {
        return new HueScenePlaylistStepResult
        {
            Index = source.Index,
            RepeatIndex = source.RepeatIndex,
            OriginalIndex = source.OriginalIndex,
            PresetName = source.PresetName?.Trim() ?? string.Empty,
            Effect = PluginConfiguration.TryNormalizeColorPresetEffect(source.Effect, out var effect)
                ? effect
                : PluginConfiguration.ColorPresetEffectSolid,
            EffectSpeedPercent = PluginConfiguration.ClampColorPresetEffectSpeedPercent(source.EffectSpeedPercent),
            TransitionCurve = PluginConfiguration.TryNormalizeColorPresetTransitionCurve(
                source.TransitionCurve,
                out var transitionCurve)
                ? transitionCurve
                : PluginConfiguration.ColorPresetTransitionCurveLinear,
            BrightnessPercent = source.BrightnessPercent.HasValue
                ? Math.Clamp(
                    source.BrightnessPercent.Value,
                    PluginConfiguration.MinScenePlaylistStepBrightnessPercent,
                    PluginConfiguration.MaxScenePlaylistStepBrightnessPercent)
                : 0,
            Red = Math.Clamp(
                source.Red,
                PluginConfiguration.MinScenePlaylistStepColorValue,
                PluginConfiguration.MaxScenePlaylistStepColorValue),
            Green = Math.Clamp(
                source.Green,
                PluginConfiguration.MinScenePlaylistStepColorValue,
                PluginConfiguration.MaxScenePlaylistStepColorValue),
            Blue = Math.Clamp(
                source.Blue,
                PluginConfiguration.MinScenePlaylistStepColorValue,
                PluginConfiguration.MaxScenePlaylistStepColorValue),
            StartOffsetSeconds = Math.Max(0, source.StartOffsetSeconds),
            DurationSeconds = Math.Clamp(
                source.DurationSeconds,
                PluginConfiguration.MinPreviewDurationSeconds,
                PluginConfiguration.MaxPreviewDurationSeconds),
            TransitionSeconds = Math.Clamp(
                source.TransitionSeconds,
                PluginConfiguration.MinColorPresetTransitionSeconds,
                PluginConfiguration.MaxColorPresetTransitionSeconds),
            TransitionOutSeconds = Math.Clamp(
                source.TransitionOutSeconds,
                PluginConfiguration.MinColorPresetTransitionOutSeconds,
                PluginConfiguration.MaxColorPresetTransitionOutSeconds),
            Succeeded = source.Succeeded,
            Message = source.Message?.Trim() ?? string.Empty,
            CleanupWarning = source.CleanupWarning?.Trim(),
            TargetResults = source.TargetResults?.Where(target => target != null)
                .Select(CloneTargetResult).ToArray() ?? Array.Empty<HueSceneScheduleTargetResult>()
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
            PlaylistPlaybackOrder = PluginConfiguration.TryNormalizeScenePlaylistOrder(
                source.PlaylistPlaybackOrder,
                out var sourcePlaybackOrder)
                ? sourcePlaybackOrder
                : PluginConfiguration.ScenePlaylistOrderSequential,
            Effect = PluginConfiguration.TryNormalizeColorPresetEffect(source.Effect, out var effect)
                ? effect
                : string.Equals(source.Effect?.Trim(), PluginConfiguration.SceneScheduleEffectPlaylist, StringComparison.OrdinalIgnoreCase)
                    ? PluginConfiguration.SceneScheduleEffectPlaylist
                : PluginConfiguration.ColorPresetEffectSolid,
            EffectSpeedPercent = PluginConfiguration.ClampColorPresetEffectSpeedPercent(source.EffectSpeedPercent),
            BrightnessPercent = source.BrightnessPercent.HasValue
                ? Math.Clamp(
                    source.BrightnessPercent.Value,
                    PluginConfiguration.MinScenePlaylistStepBrightnessPercent,
                    PluginConfiguration.MaxScenePlaylistStepBrightnessPercent)
                : null,
            Red = Math.Clamp(source.Red ?? 0, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue),
            Green = Math.Clamp(source.Green ?? 0, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue),
            Blue = Math.Clamp(source.Blue ?? 0, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue),
            TransitionCurve = PluginConfiguration.TryNormalizeColorPresetTransitionCurve(
                source.TransitionCurve,
                out var sourceTransitionCurve)
                ? sourceTransitionCurve
                : PluginConfiguration.ColorPresetTransitionCurveLinear,
            TargetLabel = source.TargetLabel?.Trim(),
            TargetAllEnabledMappings = source.TargetAllEnabledMappings,
            TargetUserIds = source.TargetUserIds?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(PluginConfiguration.NormalizeJellyfinUserId).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                ?? new List<string>(),
            TargetRoutes = source.TargetRoutes?.Where(route => route != null)
                .Select(route => new HueSceneScheduleTargetRoute
                {
                    UserId = PluginConfiguration.NormalizeJellyfinUserId(route.UserId),
                    DeviceId = route.DeviceId?.Trim() ?? string.Empty
                }).ToList() ?? new List<HueSceneScheduleTargetRoute>(),
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
            PlaylistSteps = source.PlaylistSteps?.Where(step => step != null)
                .Select(CloneHistoryPlaylistStep)
                .ToList() ?? new List<HueScenePlaylistStepHistoryEntry>(),
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
            PlaylistPlaybackOrder = PluginConfiguration.TryNormalizeScenePlaylistOrder(
                entry.PlaylistPlaybackOrder,
                out var entryPlaybackOrder)
                ? entryPlaybackOrder
                : PluginConfiguration.ScenePlaylistOrderSequential,
            Effect = string.Equals(entry.Effect?.Trim(), PluginConfiguration.SceneScheduleEffectPlaylist, StringComparison.OrdinalIgnoreCase)
                ? PluginConfiguration.SceneScheduleEffectPlaylist
                : PluginConfiguration.TryNormalizeColorPresetEffect(entry.Effect, out var effect)
                    ? effect
                    : PluginConfiguration.ColorPresetEffectSolid,
            EffectSpeedPercent = PluginConfiguration.ClampColorPresetEffectSpeedPercent(entry.EffectSpeedPercent),
            BrightnessPercent = entry.BrightnessPercent.HasValue
                ? Math.Clamp(
                    entry.BrightnessPercent.Value,
                    PluginConfiguration.MinScenePlaylistStepBrightnessPercent,
                    PluginConfiguration.MaxScenePlaylistStepBrightnessPercent)
                : null,
            Red = Math.Clamp(entry.Red ?? 0, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue),
            Green = Math.Clamp(entry.Green ?? 0, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue),
            Blue = Math.Clamp(entry.Blue ?? 0, PluginConfiguration.MinScenePlaylistStepColorValue, PluginConfiguration.MaxScenePlaylistStepColorValue),
            TransitionCurve = PluginConfiguration.TryNormalizeColorPresetTransitionCurve(
                entry.TransitionCurve,
                out var entryTransitionCurve)
                ? entryTransitionCurve
                : PluginConfiguration.ColorPresetTransitionCurveLinear,
            TargetLabel = entry.TargetLabel?.Trim(),
            TargetAllEnabledMappings = entry.TargetAllEnabledMappings,
            TargetUserIds = entry.TargetUserIds?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(PluginConfiguration.NormalizeJellyfinUserId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                ?? Array.Empty<string>(),
            TargetRoutes = entry.TargetRoutes?.Where(route => route != null)
                .Select(route => new HueSceneAutomationTargetRoute
                {
                    UserId = PluginConfiguration.NormalizeJellyfinUserId(route.UserId),
                    DeviceId = string.IsNullOrWhiteSpace(route.DeviceId) ? null : route.DeviceId.Trim()
                }).ToArray() ?? Array.Empty<HueSceneAutomationTargetRoute>(),
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
            PlaylistSteps = entry.PlaylistSteps?.Where(step => step != null)
                .Select(ToPlaylistStepResult)
                .ToArray() ?? Array.Empty<HueScenePlaylistStepResult>(),
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
            PlaylistPlaybackOrder = source.PlaylistPlaybackOrder,
            Effect = source.Effect,
            EffectSpeedPercent = source.EffectSpeedPercent,
            BrightnessPercent = source.BrightnessPercent,
            Red = source.Red,
            Green = source.Green,
            Blue = source.Blue,
            TransitionCurve = source.TransitionCurve,
            TargetLabel = source.TargetLabel,
            TargetAllEnabledMappings = source.TargetAllEnabledMappings,
            TargetUserIds = source.TargetUserIds?
                .Select(PluginConfiguration.NormalizeJellyfinUserId)
                .ToArray() ?? Array.Empty<string>(),
            TargetRoutes = source.TargetRoutes?.Where(route => route != null)
                .Select(route => new HueSceneAutomationTargetRoute
                {
                    UserId = PluginConfiguration.NormalizeJellyfinUserId(route.UserId),
                    DeviceId = string.IsNullOrWhiteSpace(route.DeviceId) ? null : route.DeviceId.Trim()
                }).ToArray() ?? Array.Empty<HueSceneAutomationTargetRoute>(),
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
            RunCount = source.RunCount,
            BlockedByPlayback = source.BlockedByPlayback
        };
    }

    internal static string ResolveTargetLabel(
        PluginConfiguration? config,
        HueSceneSchedule schedule,
        IReadOnlyList<HueSceneAutomationTargetRoute>? targetRoutes = null,
        IReadOnlyList<HueSceneAutomationTargetDescription>? resolvedTargets = null)
    {
        if (schedule.TargetAllEnabledMappings)
            return "All enabled targets";

        var selectedUserTargetCount = schedule.TargetUserIds?.Count(value => !string.IsNullOrWhiteSpace(value)) ?? 0;
        var effectiveTargetRoutes = targetRoutes ?? GetScheduleTargetRoutes(schedule);
        var selectedRouteLabels = effectiveTargetRoutes
            .Where(route => route != null && !string.IsNullOrWhiteSpace(route.UserId))
            .Select(route => string.IsNullOrWhiteSpace(route.DeviceId)
                ? route.UserId.Trim()
                : $"{route.UserId.Trim()} / {route.DeviceId.Trim()}")
            .ToArray();
        var selectedRouteCount = effectiveTargetRoutes.Count;
        var selectedTargetCount = selectedUserTargetCount + selectedRouteCount;
        if (schedule.IncludeDefaultTarget || selectedTargetCount > 0)
        {
            if (selectedRouteLabels.Length > 0)
            {
                var routeLabel = string.Join(", ", selectedRouteLabels);
                if (selectedUserTargetCount > 0)
                    routeLabel += $" + {selectedUserTargetCount} selected mapping(s)";
                return schedule.IncludeDefaultTarget
                    ? $"Default bridge + {routeLabel}"
                    : routeLabel;
            }

            return schedule.IncludeDefaultTarget
                ? selectedTargetCount == 0
                    ? "Default bridge target"
                    : $"Default bridge + {selectedTargetCount} selected target(s)"
                : $"{selectedTargetCount} selected target(s)";
        }

        var targetUserId = PluginConfiguration.NormalizeJellyfinUserId(schedule.TargetUserId);
        if (string.IsNullOrWhiteSpace(targetUserId))
        {
            return resolvedTargets is { Count: 1 } &&
                !string.IsNullOrWhiteSpace(resolvedTargets[0].TargetLabel)
                ? resolvedTargets[0].TargetLabel
                : "Default bridge target";
        }

        if (resolvedTargets is { Count: 1 } &&
            !string.IsNullOrWhiteSpace(resolvedTargets[0].TargetLabel))
        {
            return resolvedTargets[0].TargetLabel;
        }

        var matchingMappings = (config?.UserMappings ?? new List<UserBridgeMapping>())
            .Where(candidate => candidate != null &&
                PluginConfiguration.AreSameJellyfinUserId(candidate.UserId, targetUserId))
            .Cast<UserBridgeMapping>()
            .ToArray();
        if (matchingMappings.Length == 0)
            return "Missing user mapping";
        if (matchingMappings.Length > 1)
            return "Ambiguous user mapping";

        var mapping = matchingMappings[0];
        return string.IsNullOrWhiteSpace(mapping.UserName)
            ? $"User mapping {mapping.UserId?.Trim() ?? targetUserId}"
            : mapping.UserName.Trim();
    }

    private bool TryGetDeferredRun(
        HueSceneSchedule? schedule,
        DateTime localNow,
        int deferMinutes,
        out HueSceneDeferredRun? deferredRun,
        out bool expired)
    {
        deferredRun = null;
        expired = false;
        if (schedule == null)
            return false;

        var key = schedule?.Id?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var stale = false;
        lock (_deferredRunLock)
        {
            if (!_deferredRuns.TryGetValue(key, out var current))
                return false;

            // A deferred occurrence belongs to the exact schedule definition that
            // produced its UTC slot. Imports and edits can retain the same stable ID
            // while changing the time zone, recurrence, exclusion window, or solar
            // settings; never replay that old slot against the new definition.
            if (!IsDeferredOccurrenceCurrent(schedule, current.OccurrenceSlot))
            {
                _deferredRuns.Remove(key);
                stale = true;
            }
            else if (ConvertServerLocalNowToUtc(localNow) - current.DeferredAtUtc >= TimeSpan.FromMinutes(deferMinutes))
            {
                deferredRun = current;
                expired = true;
            }
            else
            {
                deferredRun = current;
            }
        }

        if (stale)
        {
            ClearDeferredRuntimeState(key);
            PersistDeferredRuns();
            _logger.LogInformation(
                "Discarded stale deferred Hue scene occurrence for schedule {0} after its timing definition changed",
                key);
            return false;
        }

        // Keep an expired entry until the caller records its skipped outcome. This lets
        // one-time cues durably claim their disabled state first; if that claim fails,
        // the deferred occurrence remains available for a safe retry instead of falling
        // through to ordinary catch-up evaluation.
        return !expired;
    }

    private bool HasUnexpiredDeferredRun(HueSceneSchedule? schedule, DateTime localNow)
    {
        var config = Plugin.Instance?.Configuration;
        var deferMinutes = Math.Clamp(
            config?.SceneAutomationDeferMinutes ?? PluginConfiguration.DefaultSceneAutomationDeferMinutes,
            PluginConfiguration.MinSceneAutomationDeferMinutes,
            PluginConfiguration.MaxSceneAutomationDeferMinutes);
        var key = schedule?.Id?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key) ||
            !string.Equals(
                GetEffectivePlaybackPolicy(config, schedule),
                PluginConfiguration.SceneAutomationPlaybackPolicyDefer,
                StringComparison.OrdinalIgnoreCase))
            return false;

        lock (_deferredRunLock)
        {
            if (!_deferredRuns.TryGetValue(key, out var deferredRun) ||
                !IsDeferredOccurrenceCurrent(schedule, deferredRun.OccurrenceSlot))
            {
                return false;
            }

            // Keep this check side-effect free: the scheduler remains responsible for
            // pruning stale/expired entries and recording their skipped outcome. A marker
            // can be set while the persisted defer window is still open, even when the
            // one-time cue no longer appears in the future-occurrence preview.
            var elapsed = ConvertServerLocalNowToUtc(localNow) - deferredRun.DeferredAtUtc;
            return elapsed < TimeSpan.FromMinutes(deferMinutes);
        }
    }

    internal static bool HasUnexpiredPersistedDeferredRun(
        PluginConfiguration? config,
        HueSceneSchedule? schedule,
        DateTime localNow)
    {
        var deferMinutes = Math.Clamp(
            config?.SceneAutomationDeferMinutes ?? PluginConfiguration.DefaultSceneAutomationDeferMinutes,
            PluginConfiguration.MinSceneAutomationDeferMinutes,
            PluginConfiguration.MaxSceneAutomationDeferMinutes);
        var key = schedule?.Id?.Trim() ?? string.Empty;
        if (config == null ||
            string.IsNullOrWhiteSpace(key) ||
            !string.Equals(
                GetEffectivePlaybackPolicy(config, schedule),
                PluginConfiguration.SceneAutomationPlaybackPolicyDefer,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var deferredRun = config.PersistedSceneAutomationDeferredRuns?
            .FirstOrDefault(entry => entry != null &&
                string.Equals(entry.ScheduleId?.Trim(), key, StringComparison.OrdinalIgnoreCase));
        if (deferredRun == null ||
            !IsDeferredOccurrenceCurrent(schedule, deferredRun.OccurrenceSlot))
        {
            return false;
        }

        var deferredAtUtc = deferredRun.DeferredAtUtc is { } persistedUtc && persistedUtc != default
            ? NormalizeUtcInstant(persistedUtc)
            : ConvertServerLocalNowToUtc(deferredRun.DeferredAtLocal);
        return ConvertServerLocalNowToUtc(localNow) - deferredAtUtc < TimeSpan.FromMinutes(deferMinutes);
    }

    private static bool IsDeferredOccurrenceCurrent(
        HueSceneSchedule? schedule,
        DateTime occurrenceSlot)
    {
        if (schedule == null || occurrenceSlot == default)
            return false;

        var normalizedSlot = DateTime.SpecifyKind(occurrenceSlot, DateTimeKind.Utc);
        DateTime serverLocalBeforeOccurrence;
        try
        {
            // The occurrence preview deliberately excludes instants at or before its
            // supplied clock. Start one second before the persisted slot so the exact
            // deferred occurrence is eligible even when solar calculations retain
            // sub-minute UTC precision.
            serverLocalBeforeOccurrence = TimeZoneInfo.ConvertTimeFromUtc(
                normalizedSlot.AddSeconds(-1),
                TimeZoneInfo.Local);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var candidate = CloneSchedule(schedule);
        // The administrator skip marker is consumed separately by the scheduler. It
        // must not make an otherwise valid deferred slot look stale before that marker
        // can be recorded as the explicit skipped outcome.
        candidate.SkipNextOccurrence = false;
        return GetUpcomingOccurrences(
                candidate,
                serverLocalBeforeOccurrence,
                maxOccurrences: MaxUpcomingOccurrencesPerSchedule,
                horizonDays: 3,
                includeFutureStartBeyondHorizon: true)
            .Any(occurrence => DateTime.SpecifyKind(occurrence.UtcTime, DateTimeKind.Utc) == normalizedSlot);
    }

    /// <summary>
    /// Loads deferred occurrences once from the credential-free runtime state stored in
    /// plugin configuration. Invalid, duplicate, disabled, and deleted-cue entries are
    /// discarded before they can be replayed after a restart.
    /// </summary>
    private void EnsureDeferredRunsLoaded(bool persistRepairs = true)
    {
        lock (_bridgeLifecycleGate.HistorySynchronization)
        {
            EnsureDeferredRunsLoadedCore(persistRepairs);
        }
    }

    private void EnsureDeferredRunsLoadedCore(bool persistRepairs)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
            return;

        bool deferredRunsLoaded;
        bool persistencePending;
        lock (_deferredRunLock)
        {
            deferredRunsLoaded = _deferredRunsLoaded;
            persistencePending = _deferredRunsPersistencePending;
        }

        if (deferredRunsLoaded)
        {
            if (persistRepairs && persistencePending)
                PersistPendingDeferredRunsRepair();
            return;
        }

        if (persistRepairs)
            config.PersistedSceneAutomationDeferredRuns ??= new List<HueSceneDeferredRunEntry>();
        var configuredEntries = config.PersistedSceneAutomationDeferredRuns ??
            new List<HueSceneDeferredRunEntry>();
        var normalizedEntries = NormalizeDeferredRunEntries(config, configuredEntries, out var shouldSave);
        var loaded = normalizedEntries.ToDictionary(
            entry => entry.ScheduleId.Trim(),
            entry => new HueSceneDeferredRun(
                entry.OccurrenceSlot,
                entry.DeferredAtLocal,
                ResolveDeferredAtUtc(entry),
                restored: true),
            StringComparer.OrdinalIgnoreCase);

        lock (_deferredRunLock)
        {
            if (_deferredRunsLoaded)
            {
                if (!persistRepairs && shouldSave)
                    _deferredRunsPersistencePending = true;
                return;
            }

            if (shouldSave)
            {
                if (persistRepairs)
                {
                    config.PersistedSceneAutomationDeferredRuns = normalizedEntries
                        .Select(CloneDeferredRunEntry)
                        .ToList();
                }
                else
                {
                    _deferredRunsPersistencePending = true;
                }
            }

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
                restored: true,
                deferredAtUtc: entry.Value.DeferredAtUtc);
        }

        if (shouldSave && persistRepairs && SavePersistedDeferredRunsConfiguration())
        {
            lock (_deferredRunLock)
            {
                _deferredRunsPersistencePending = false;
            }
        }
        else if (shouldSave && persistRepairs)
        {
            MarkDeferredRunsPersistencePending();
        }

        if (loaded.Count > 0)
        {
            _logger.LogInformation(
                "Restored {0} deferred Hue scene occurrence(s) after scheduler startup",
                loaded.Count);
        }
    }

    private List<HueSceneDeferredRunEntry> NormalizeDeferredRunEntries(
        PluginConfiguration config,
        IReadOnlyList<HueSceneDeferredRunEntry> configuredEntries,
        out bool shouldSave)
    {
        var enabledScheduleIds = (config.SceneSchedules ?? new List<HueSceneSchedule>())
            .Where(schedule => schedule != null && schedule.Enabled &&
                string.Equals(
                    GetEffectivePlaybackPolicy(config, schedule),
                    PluginConfiguration.SceneAutomationPlaybackPolicyDefer,
                    StringComparison.OrdinalIgnoreCase))
            .Select(schedule => schedule.Id?.Trim() ?? string.Empty)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalizedEntries = configuredEntries
            .Where(entry => entry != null &&
                !string.IsNullOrWhiteSpace(entry.ScheduleId) &&
                enabledScheduleIds.Contains(entry.ScheduleId.Trim()) &&
                entry.OccurrenceSlot != default &&
                (entry.DeferredAtLocal != default ||
                 (entry.DeferredAtUtc.HasValue && entry.DeferredAtUtc.Value != default)))
            .GroupBy(entry => entry.ScheduleId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(entry => entry.ScheduleId, StringComparer.OrdinalIgnoreCase)
            .Select(entry =>
            {
                var normalized = CloneDeferredRunEntry(entry);
                normalized.DeferredAtUtc = ResolveDeferredAtUtc(entry);
                if (normalized.DeferredAtLocal == default)
                {
                    normalized.DeferredAtLocal = TimeZoneInfo.ConvertTimeFromUtc(
                        normalized.DeferredAtUtc.Value,
                        TimeZoneInfo.Local);
                }

                return normalized;
            })
            .ToList();
        shouldSave = normalizedEntries.Count != configuredEntries.Count ||
            normalizedEntries.Where((entry, index) =>
                !string.Equals(
                    entry.ScheduleId.Trim(),
                    configuredEntries[index]?.ScheduleId?.Trim(),
                    StringComparison.OrdinalIgnoreCase) ||
                entry.OccurrenceSlot != configuredEntries[index]?.OccurrenceSlot ||
                entry.DeferredAtLocal != configuredEntries[index]?.DeferredAtLocal ||
                entry.DeferredAtUtc != configuredEntries[index]?.DeferredAtUtc)
                .Any();
        return normalizedEntries;
    }

    private static DateTime ResolveDeferredAtUtc(HueSceneDeferredRunEntry entry)
    {
        if (entry.DeferredAtUtc is { } persistedUtc && persistedUtc != default)
        {
            return NormalizeUtcInstant(persistedUtc);
        }

        return ConvertServerLocalNowToUtc(entry.DeferredAtLocal);
    }

    private static DateTime NormalizeUtcInstant(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private void PersistPendingDeferredRunsRepair()
    {
        lock (_bridgeLifecycleGate.HistorySynchronization)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
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
                        DeferredAtLocal = entry.Value.DeferredAtLocal,
                        DeferredAtUtc = entry.Value.DeferredAtUtc
                    })
                    .ToList();
            }

            // A failed normal write already copied the attempted snapshot into the live
            // configuration object, so comparing against that list cannot tell whether the
            // XML file was updated. Persist the authoritative runtime snapshot whenever the
            // dirty flag is set, even when it appears equal to the in-memory configuration.
            config.PersistedSceneAutomationDeferredRuns = entries;
            if (SavePersistedDeferredRunsConfiguration())
            {
                lock (_deferredRunLock)
                {
                    _deferredRunsPersistencePending = false;
                }
            }
            else
            {
                MarkDeferredRunsPersistencePending();
            }
        }
    }

    private void QueueDeferredRun(
        HueSceneSchedule schedule,
        DateTime occurrenceSlot,
        DateTime localNow,
        int deferMinutes)
    {
        lock (_bridgeLifecycleGate.HistorySynchronization)
        {
            QueueDeferredRunCore(schedule, occurrenceSlot, localNow, deferMinutes);
        }
    }

    private void QueueDeferredRunCore(
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
                _deferredRuns[key] = new HueSceneDeferredRun(
                    occurrenceSlot,
                    localNow,
                    ConvertServerLocalNowToUtc(localNow),
                    restored: false);
                queued = true;
            }
        }

        MarkDeferredRuntimeState(
            key,
            occurrenceSlot,
            localNow,
            deferMinutes,
            restored: false,
            deferredAtUtc: ConvertServerLocalNowToUtc(localNow));
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
        lock (_bridgeLifecycleGate.HistorySynchronization)
        {
            RemoveDeferredRunCore(scheduleId, persist);
        }
    }

    private void RemoveDeferredRunCore(string scheduleId, bool persist)
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
        bool restored,
        DateTime? deferredAtUtc = null)
    {
        lock (_bridgeLifecycleGate.HistorySynchronization)
        {
            MarkDeferredRuntimeStateCore(
                scheduleId,
                occurrenceSlot,
                deferredAtLocal,
                deferMinutes,
                restored,
                deferredAtUtc);
        }
    }

    private void MarkDeferredRuntimeStateCore(
        string scheduleId,
        DateTime occurrenceSlot,
        DateTime deferredAtLocal,
        int deferMinutes,
        bool restored,
        DateTime? deferredAtUtc = null)
    {
        var effectiveDeferredAtUtc = deferredAtUtc.HasValue
            ? NormalizeUtcInstant(deferredAtUtc.Value)
            : ConvertServerLocalNowToUtc(deferredAtLocal);
        var deferredUntilUtc = effectiveDeferredAtUtc.AddMinutes(deferMinutes);
        var deferredUntilLocal = TimeZoneInfo.ConvertTimeFromUtc(
            deferredUntilUtc,
            TimeZoneInfo.Local);
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
            state.DeferredUntilLocal = deferredUntilLocal;
            state.DeferredRestored = restored;
            state.LastMessage = restored
                ? "Restored after scheduler restart; waiting for active playback to finish before running this automatic cue."
                : "Waiting for active playback to finish before running this automatic cue.";
        }
    }

    private void ClearDeferredRuntimeState(string scheduleId)
    {
        lock (_bridgeLifecycleGate.HistorySynchronization)
        {
            ClearDeferredRuntimeStateCore(scheduleId);
        }
    }

    private void ClearDeferredRuntimeStateCore(string scheduleId)
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
        lock (_bridgeLifecycleGate.HistorySynchronization)
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
                        DeferredAtLocal = entry.Value.DeferredAtLocal,
                        DeferredAtUtc = entry.Value.DeferredAtUtc
                    })
                    .ToList();
            }

            config.PersistedSceneAutomationDeferredRuns = entries;
            if (SavePersistedDeferredRunsConfiguration())
            {
                lock (_deferredRunLock)
                {
                    _deferredRunsPersistencePending = false;
                }
            }
            else
            {
                MarkDeferredRunsPersistencePending();
            }
        }
    }

    private void MarkDeferredRunsPersistencePending()
    {
        lock (_deferredRunLock)
        {
            _deferredRunsPersistencePending = true;
        }
    }

    private bool SavePersistedDeferredRunsConfiguration()
    {
        try
        {
            Plugin.Instance?.SaveConfiguration();
            return true;
        }
        catch (Exception ex)
        {
            // Runtime persistence must never interrupt a cue or make the scheduler fail.
            _logger.LogWarning(ex, "Could not persist deferred Hue scene occurrences");
            return false;
        }
    }

    private static HueSceneDeferredRunEntry CloneDeferredRunEntry(HueSceneDeferredRunEntry source)
    {
        return new HueSceneDeferredRunEntry
        {
            ScheduleId = source.ScheduleId?.Trim() ?? string.Empty,
            OccurrenceSlot = source.OccurrenceSlot,
            DeferredAtLocal = source.DeferredAtLocal,
            DeferredAtUtc = source.DeferredAtUtc
        };
    }

    private bool HasDeferredRunsPersistencePending()
    {
        lock (_deferredRunLock)
        {
            return _deferredRunsPersistencePending;
        }
    }

    private static bool ShouldClaimAutomaticOccurrence(HueSceneSchedule? schedule)
        => schedule != null && string.IsNullOrWhiteSpace(schedule.RunDate);

    private static string GetOccurrenceDefinitionKey(HueSceneSchedule schedule)
    {
        var timeMode = PluginConfiguration.TryNormalizeSceneScheduleTimeMode(
            schedule.TimeMode,
            out var normalizedTimeMode)
            ? normalizedTimeMode
            : schedule.TimeMode?.Trim() ?? string.Empty;
        var recurrence = PluginConfiguration.TryNormalizeSceneScheduleRecurrence(
            schedule.Recurrence,
            out var normalizedRecurrence)
            ? normalizedRecurrence
            : schedule.Recurrence?.Trim() ?? string.Empty;
        var timeOfDay = PluginConfiguration.TryNormalizeSceneScheduleTime(
            schedule.TimeOfDay,
            out var normalizedTimeOfDay)
            ? normalizedTimeOfDay
            : schedule.TimeOfDay?.Trim() ?? string.Empty;
        var excludedDates = PluginConfiguration.TryNormalizeSceneScheduleExcludedDates(
            schedule.ExcludedDates,
            out var normalizedExcludedDates)
            ? normalizedExcludedDates
            : (schedule.ExcludedDates ?? new List<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();

        // Unit-separate each field so a credential-free schedule edit cannot create an
        // accidental collision. These are all timing fields; target and scene edits do
        // not change the identity of an already-consumed occurrence.
        return string.Join(
            '\u001f',
            timeMode,
            timeOfDay,
            schedule.TimeZoneId?.Trim() ?? string.Empty,
            recurrence,
            schedule.RecurrenceInterval.ToString(CultureInfo.InvariantCulture),
            schedule.DayOfMonth.ToString(CultureInfo.InvariantCulture),
            schedule.MonthOfYear.ToString(CultureInfo.InvariantCulture),
            schedule.WeekOfMonth.ToString(CultureInfo.InvariantCulture),
            schedule.DayOfWeek.ToString(CultureInfo.InvariantCulture),
            schedule.DaysOfWeekMask.ToString(CultureInfo.InvariantCulture),
            schedule.StartDate?.Trim() ?? string.Empty,
            schedule.EndDate?.Trim() ?? string.Empty,
            schedule.SolarOffsetMinutes.ToString(CultureInfo.InvariantCulture),
            schedule.SolarLatitude?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
            schedule.SolarLongitude?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
            string.Join(',', normalizedExcludedDates));
    }

    private bool HasAutomaticOccurrenceClaim(
        HueSceneSchedule schedule,
        DateTime occurrenceSlot)
    {
        if (!ShouldClaimAutomaticOccurrence(schedule) ||
            string.IsNullOrWhiteSpace(schedule.Id) ||
            occurrenceSlot == default)
        {
            return false;
        }

        var key = schedule.Id.Trim();
        var normalizedSlot = NormalizeUtcInstant(occurrenceSlot);
        var definitionKey = GetOccurrenceDefinitionKey(schedule);
        lock (_automaticOccurrenceClaimLock)
        {
            return _automaticOccurrenceClaims.TryGetValue(key, out var claim) &&
                claim.OccurrenceSlot == normalizedSlot &&
                string.Equals(claim.OccurrenceDefinitionKey, definitionKey, StringComparison.OrdinalIgnoreCase);
        }
    }

    private bool TryClaimAutomaticOccurrence(
        PluginConfiguration config,
        HueSceneSchedule schedule,
        DateTime occurrenceSlot)
    {
        if (!ShouldClaimAutomaticOccurrence(schedule) ||
            string.IsNullOrWhiteSpace(schedule.Id) ||
            occurrenceSlot == default)
        {
            return false;
        }

        var key = schedule.Id.Trim();
        var normalizedSlot = NormalizeUtcInstant(occurrenceSlot);
        var definitionKey = GetOccurrenceDefinitionKey(schedule);
        lock (_bridgeLifecycleGate.HistorySynchronization)
        {
            lock (_automaticOccurrenceClaimLock)
            {
                if (_automaticOccurrenceClaims.TryGetValue(key, out var existing) &&
                    existing.OccurrenceSlot == normalizedSlot &&
                    string.Equals(existing.OccurrenceDefinitionKey, definitionKey, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var previousClaim = _automaticOccurrenceClaims.TryGetValue(key, out existing)
                    ? existing
                    : null;
                var previousEntries = (config.PersistedSceneAutomationOccurrenceClaims ?? new List<HueSceneAutomationOccurrenceClaimEntry>())
                    .Select(CloneAutomaticOccurrenceClaimEntry)
                    .ToList();
                _automaticOccurrenceClaims[key] = new HueSceneAutomationOccurrenceClaim(
                    normalizedSlot,
                    definitionKey);
                config.PersistedSceneAutomationOccurrenceClaims = SnapshotAutomaticOccurrenceClaims();
                if (SaveAutomaticOccurrenceClaimsConfiguration())
                {
                    _automaticOccurrenceClaimsPersistencePending = false;
                    return true;
                }

                if (previousClaim == null)
                    _automaticOccurrenceClaims.Remove(key);
                else
                    _automaticOccurrenceClaims[key] = previousClaim;
                config.PersistedSceneAutomationOccurrenceClaims = previousEntries;
                return false;
            }
        }
    }

    private bool ReleaseAutomaticOccurrenceClaim(
        PluginConfiguration config,
        HueSceneSchedule schedule,
        DateTime occurrenceSlot)
    {
        if (!ShouldClaimAutomaticOccurrence(schedule) ||
            string.IsNullOrWhiteSpace(schedule.Id) ||
            occurrenceSlot == default)
        {
            return true;
        }

        var key = schedule.Id.Trim();
        var normalizedSlot = NormalizeUtcInstant(occurrenceSlot);
        lock (_bridgeLifecycleGate.HistorySynchronization)
        {
            lock (_automaticOccurrenceClaimLock)
            {
                var definitionKey = GetOccurrenceDefinitionKey(schedule);
                if (!_automaticOccurrenceClaims.TryGetValue(key, out var existing) ||
                    existing.OccurrenceSlot != normalizedSlot ||
                    !string.Equals(
                        existing.OccurrenceDefinitionKey,
                        definitionKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var previousEntries = (config.PersistedSceneAutomationOccurrenceClaims ?? new List<HueSceneAutomationOccurrenceClaimEntry>())
                    .Select(CloneAutomaticOccurrenceClaimEntry)
                    .ToList();
                _automaticOccurrenceClaims.Remove(key);
                config.PersistedSceneAutomationOccurrenceClaims = SnapshotAutomaticOccurrenceClaims();
                if (SaveAutomaticOccurrenceClaimsConfiguration())
                {
                    _automaticOccurrenceClaimsPersistencePending = false;
                    return true;
                }

                // Keep a failed release in memory and on the in-memory configuration so
                // a persistence outage fails closed instead of replaying a possibly
                // partially-mutated bridge lifecycle after cancellation.
                _automaticOccurrenceClaims[key] = existing;
                config.PersistedSceneAutomationOccurrenceClaims = previousEntries;
                return false;
            }
        }
    }

    private void EnsureAutomaticOccurrenceClaimsLoaded(bool persistRepairs = true)
    {
        lock (_bridgeLifecycleGate.HistorySynchronization)
        {
            EnsureAutomaticOccurrenceClaimsLoadedCore(persistRepairs);
        }
    }

    private void EnsureAutomaticOccurrenceClaimsLoadedCore(bool persistRepairs)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
            return;

        bool loaded;
        bool persistencePending;
        lock (_automaticOccurrenceClaimLock)
        {
            loaded = _automaticOccurrenceClaimsLoaded;
            persistencePending = _automaticOccurrenceClaimsPersistencePending;
        }

        if (loaded)
        {
            if (persistRepairs && persistencePending)
                PersistPendingAutomaticOccurrenceClaimsRepair();
            return;
        }

        if (persistRepairs)
            config.PersistedSceneAutomationOccurrenceClaims ??= new List<HueSceneAutomationOccurrenceClaimEntry>();
        var configuredEntries = config.PersistedSceneAutomationOccurrenceClaims ??
            new List<HueSceneAutomationOccurrenceClaimEntry>();
        var normalizedEntries = NormalizeAutomaticOccurrenceClaimEntries(
            config,
            configuredEntries,
            out var shouldSave);
        var loadedClaims = normalizedEntries.ToDictionary(
            entry => entry.ScheduleId.Trim(),
            entry => new HueSceneAutomationOccurrenceClaim(
                NormalizeUtcInstant(entry.OccurrenceSlot),
                entry.OccurrenceDefinitionKey.Trim()),
            StringComparer.OrdinalIgnoreCase);

        lock (_automaticOccurrenceClaimLock)
        {
            if (_automaticOccurrenceClaimsLoaded)
            {
                if (!persistRepairs && shouldSave)
                    _automaticOccurrenceClaimsPersistencePending = true;
                return;
            }

            if (shouldSave)
            {
                if (persistRepairs)
                    config.PersistedSceneAutomationOccurrenceClaims = normalizedEntries
                        .Select(CloneAutomaticOccurrenceClaimEntry)
                        .ToList();
                else
                    _automaticOccurrenceClaimsPersistencePending = true;
            }

            _automaticOccurrenceClaims.Clear();
            foreach (var entry in loadedClaims)
                _automaticOccurrenceClaims[entry.Key] = entry.Value;
            _automaticOccurrenceClaimsLoaded = true;
        }

        if (shouldSave && persistRepairs)
        {
            if (SaveAutomaticOccurrenceClaimsConfiguration())
            {
                lock (_automaticOccurrenceClaimLock)
                {
                    _automaticOccurrenceClaimsPersistencePending = false;
                }
            }
            else
            {
                MarkAutomaticOccurrenceClaimsPersistencePending();
            }
        }
    }

    private List<HueSceneAutomationOccurrenceClaimEntry> NormalizeAutomaticOccurrenceClaimEntries(
        PluginConfiguration config,
        IReadOnlyList<HueSceneAutomationOccurrenceClaimEntry> configuredEntries,
        out bool shouldSave)
    {
        var schedules = (config.SceneSchedules ?? new List<HueSceneSchedule>())
            .Where(schedule => schedule != null && !string.IsNullOrWhiteSpace(schedule.Id))
            .GroupBy(schedule => schedule.Id.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToDictionary(
                schedule => schedule.Id.Trim(),
                schedule => schedule,
                StringComparer.OrdinalIgnoreCase);
        var candidates = new List<HueSceneAutomationOccurrenceClaimEntry>();
        foreach (var entry in configuredEntries ?? Array.Empty<HueSceneAutomationOccurrenceClaimEntry>())
        {
            if (entry == null ||
                string.IsNullOrWhiteSpace(entry.ScheduleId) ||
                entry.OccurrenceSlot == default ||
                !schedules.TryGetValue(entry.ScheduleId.Trim(), out var schedule) ||
                !ShouldClaimAutomaticOccurrence(schedule))
            {
                continue;
            }

            var definitionKey = GetOccurrenceDefinitionKey(schedule);
            var persistedDefinitionKey = entry.OccurrenceDefinitionKey?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(persistedDefinitionKey) &&
                !string.Equals(persistedDefinitionKey, definitionKey, StringComparison.OrdinalIgnoreCase))
            {
                // The schedule kept its ID but its timing changed; the old claim belongs
                // to the old occurrence definition and must not suppress the new cue.
                continue;
            }

            candidates.Add(new HueSceneAutomationOccurrenceClaimEntry
            {
                ScheduleId = schedule.Id.Trim(),
                OccurrenceSlot = NormalizeUtcInstant(entry.OccurrenceSlot),
                OccurrenceDefinitionKey = definitionKey
            });
        }

        var normalizedEntries = candidates
            .GroupBy(entry => entry.ScheduleId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(entry => entry.OccurrenceSlot)
                .First())
            .OrderBy(entry => entry.ScheduleId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sourceEntries = (configuredEntries ?? Array.Empty<HueSceneAutomationOccurrenceClaimEntry>()).ToArray();
        shouldSave = normalizedEntries.Count != sourceEntries.Length ||
            normalizedEntries.Where((entry, index) => !AutomaticOccurrenceClaimEntriesEqual(entry, sourceEntries[index])).Any();
        return normalizedEntries;
    }

    private static bool AutomaticOccurrenceClaimEntriesEqual(
        HueSceneAutomationOccurrenceClaimEntry left,
        HueSceneAutomationOccurrenceClaimEntry? right)
    {
        return right != null &&
            string.Equals(left.ScheduleId?.Trim(), right.ScheduleId?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            NormalizeUtcInstant(left.OccurrenceSlot) == NormalizeUtcInstant(right.OccurrenceSlot) &&
            string.Equals(left.OccurrenceDefinitionKey?.Trim(), right.OccurrenceDefinitionKey?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private void PruneAutomaticOccurrenceClaims(
        IReadOnlyList<HueSceneSchedule> schedules,
        bool persist)
    {
        var definitions = schedules
            .Where(schedule => schedule != null && ShouldClaimAutomaticOccurrence(schedule) && !string.IsNullOrWhiteSpace(schedule.Id))
            .GroupBy(schedule => schedule.Id.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToDictionary(
                schedule => schedule.Id.Trim(),
                schedule => GetOccurrenceDefinitionKey(schedule),
                StringComparer.OrdinalIgnoreCase);
        lock (_bridgeLifecycleGate.HistorySynchronization)
        {
            lock (_automaticOccurrenceClaimLock)
            {
                var retained = _automaticOccurrenceClaims
                    .Where(entry => definitions.TryGetValue(entry.Key, out var definition) &&
                        string.Equals(entry.Value.OccurrenceDefinitionKey, definition, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
                if (retained.Count == _automaticOccurrenceClaims.Count)
                    return;

                var previousClaims = _automaticOccurrenceClaims.ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value,
                    StringComparer.OrdinalIgnoreCase);
                var previousEntries = (Plugin.Instance?.Configuration?.PersistedSceneAutomationOccurrenceClaims ??
                    new List<HueSceneAutomationOccurrenceClaimEntry>())
                    .Select(CloneAutomaticOccurrenceClaimEntry)
                    .ToList();
                _automaticOccurrenceClaims.Clear();
                foreach (var entry in retained)
                    _automaticOccurrenceClaims[entry.Key] = entry.Value;
                var config = Plugin.Instance?.Configuration;
                if (config == null)
                    return;
                config.PersistedSceneAutomationOccurrenceClaims = SnapshotAutomaticOccurrenceClaims();
                if (!persist || SaveAutomaticOccurrenceClaimsConfiguration())
                {
                    _automaticOccurrenceClaimsPersistencePending = !persist;
                    return;
                }

                _automaticOccurrenceClaims.Clear();
                foreach (var entry in previousClaims)
                    _automaticOccurrenceClaims[entry.Key] = entry.Value;
                config.PersistedSceneAutomationOccurrenceClaims = previousEntries;
            }
        }
    }

    private void ClearAllAutomaticOccurrenceClaims()
        => PruneAutomaticOccurrenceClaims(Array.Empty<HueSceneSchedule>(), persist: true);

    private List<HueSceneAutomationOccurrenceClaimEntry> SnapshotAutomaticOccurrenceClaims()
    {
        return _automaticOccurrenceClaims
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new HueSceneAutomationOccurrenceClaimEntry
            {
                ScheduleId = entry.Key,
                OccurrenceSlot = entry.Value.OccurrenceSlot,
                OccurrenceDefinitionKey = entry.Value.OccurrenceDefinitionKey
            })
            .ToList();
    }

    private void PersistPendingAutomaticOccurrenceClaimsRepair()
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
            return;

        lock (_automaticOccurrenceClaimLock)
        {
            config.PersistedSceneAutomationOccurrenceClaims = SnapshotAutomaticOccurrenceClaims();
            if (SaveAutomaticOccurrenceClaimsConfiguration())
                _automaticOccurrenceClaimsPersistencePending = false;
            else
                MarkAutomaticOccurrenceClaimsPersistencePending();
        }
    }

    private void MarkAutomaticOccurrenceClaimsPersistencePending()
    {
        lock (_automaticOccurrenceClaimLock)
        {
            _automaticOccurrenceClaimsPersistencePending = true;
        }
    }

    private bool SaveAutomaticOccurrenceClaimsConfiguration()
    {
        try
        {
            Plugin.Instance?.SaveConfiguration();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist Hue automatic scene occurrence claims");
            return false;
        }
    }

    private static HueSceneAutomationOccurrenceClaimEntry CloneAutomaticOccurrenceClaimEntry(
        HueSceneAutomationOccurrenceClaimEntry source)
    {
        return new HueSceneAutomationOccurrenceClaimEntry
        {
            ScheduleId = source.ScheduleId?.Trim() ?? string.Empty,
            OccurrenceSlot = NormalizeUtcInstant(source.OccurrenceSlot),
            OccurrenceDefinitionKey = source.OccurrenceDefinitionKey?.Trim() ?? string.Empty
        };
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

    private void ReleaseRunSlot(string scheduleId, DateTime slot)
    {
        lock (_runSlotLock)
        {
            if (_lastRunSlots.TryGetValue(scheduleId, out var claimedSlot) && claimedSlot == slot)
                _lastRunSlots.Remove(scheduleId);
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
            TargetUserId = PluginConfiguration.NormalizeJellyfinUserId(source.TargetUserId),
            TargetUserIds = source.TargetUserIds?
                .Select(PluginConfiguration.NormalizeJellyfinUserId)
                .ToList() ?? new List<string>(),
            TargetRoutes = source.TargetRoutes?.Where(route => route != null)
                .Select(route => new HueSceneScheduleTargetRoute
                {
                    UserId = PluginConfiguration.NormalizeJellyfinUserId(route.UserId),
                    DeviceId = route.DeviceId?.Trim() ?? string.Empty
                }).ToList() ?? new List<HueSceneScheduleTargetRoute>(),
            IncludeDefaultTarget = source.IncludeDefaultTarget,
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
            BrightnessPercent = source.BrightnessPercent,
            Red = source.Red,
            Green = source.Green,
            Blue = source.Blue,
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
            BlockedByPlayback = source.BlockedByPlayback,
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
            OriginalIndex = source.OriginalIndex,
            PresetName = source.PresetName,
            Effect = source.Effect,
            EffectSpeedPercent = source.EffectSpeedPercent,
            TransitionCurve = source.TransitionCurve,
            BrightnessPercent = source.BrightnessPercent,
            Red = source.Red,
            Green = source.Green,
            Blue = source.Blue,
            StartOffsetSeconds = source.StartOffsetSeconds,
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
        string? targetLabel = null,
        IReadOnlyList<HueSceneAutomationTargetRoute>? targetRoutes = null)
    {
        return new HueSceneAutomationRunResult
        {
            ScheduleId = scheduleId ?? string.Empty,
            ScheduleName = schedule?.Name?.Trim() ?? string.Empty,
            PresetName = schedule?.PresetName?.Trim() ?? string.Empty,
            PlaylistName = schedule?.PlaylistName?.Trim() ?? string.Empty,
            BrightnessPercent = GetEffectiveScheduleBrightness(Plugin.Instance?.Configuration, schedule),
            Red = schedule?.Red ?? 0,
            Green = schedule?.Green ?? 0,
            Blue = schedule?.Blue ?? 0,
            TargetLabel = targetLabel,
            TargetAllEnabledMappings = schedule?.TargetAllEnabledMappings ?? false,
            TargetUserIds = schedule?.TargetUserIds?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(PluginConfiguration.NormalizeJellyfinUserId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                ?? Array.Empty<string>(),
            TargetRoutes = targetRoutes ?? GetScheduleTargetRoutes(schedule),
            IncludeDefaultTarget = schedule?.IncludeDefaultTarget ?? false,
            Succeeded = false,
            Message = message,
            RunAtUtc = DateTime.UtcNow
        };
    }

    private static int? GetEffectiveScheduleBrightness(
        PluginConfiguration? config,
        HueSceneSchedule? schedule)
    {
        if (config == null || schedule == null || !string.IsNullOrWhiteSpace(schedule.PlaylistName))
            return null;

        var preset = config.ColorPresets?.FirstOrDefault(candidate =>
            candidate != null &&
            string.Equals(candidate.Name?.Trim(), schedule.PresetName?.Trim(), StringComparison.OrdinalIgnoreCase));
        return preset == null
            ? null
            : Math.Clamp(
                schedule.BrightnessPercent ?? preset.BrightnessPercent,
                PluginConfiguration.MinScenePlaylistStepBrightnessPercent,
                PluginConfiguration.MaxScenePlaylistStepBrightnessPercent);
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
                BlockedByPlayback = target.BlockedByPlayback,
                AvailableChannelCount = target.AvailableChannelCount,
                SelectedChannelCount = target.SelectedChannelCount
            }).ToArray() ?? Array.Empty<HueSceneScheduleTargetResult>()
        };
    }
}

internal sealed class HueSceneDeferredRun
{
    public HueSceneDeferredRun(
        DateTime occurrenceSlot,
        DateTime deferredAtLocal,
        DateTime deferredAtUtc,
        bool restored)
    {
        OccurrenceSlot = occurrenceSlot;
        DeferredAtLocal = deferredAtLocal;
        DeferredAtUtc = deferredAtUtc;
        Restored = restored;
    }

    public DateTime OccurrenceSlot { get; }
    public DateTime DeferredAtLocal { get; }
    public DateTime DeferredAtUtc { get; }
    public bool Restored { get; }
}

internal sealed class HueSceneAutomationOccurrenceClaim
{
    public HueSceneAutomationOccurrenceClaim(
        DateTime occurrenceSlot,
        string occurrenceDefinitionKey)
    {
        OccurrenceSlot = occurrenceSlot;
        OccurrenceDefinitionKey = occurrenceDefinitionKey;
    }

    public DateTime OccurrenceSlot { get; }
    public string OccurrenceDefinitionKey { get; }
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
public sealed class HueSceneAutomationTargetRoute
{
    [JsonPropertyName("userId")]
    public string UserId { get; init; } = string.Empty;

    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; init; }
}

internal sealed class HueSceneAutomationTargetDescription
{
    public string TargetLabel { get; init; } = string.Empty;
    public string TargetUserId { get; init; } = string.Empty;
    public string TargetDeviceId { get; init; } = string.Empty;
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

    [JsonPropertyName("playlistPlaybackOrder")]
    public string PlaylistPlaybackOrder { get; init; } = PluginConfiguration.ScenePlaylistOrderSequential;

    [JsonPropertyName("effect")]
    public string Effect { get; init; } = PluginConfiguration.ColorPresetEffectSolid;

    [JsonPropertyName("effectSpeedPercent")]
    public int EffectSpeedPercent { get; init; } = PluginConfiguration.DefaultColorPresetEffectSpeedPercent;

    [JsonPropertyName("brightnessPercent")]
    public int? BrightnessPercent { get; init; }

    [JsonPropertyName("red")]
    public int Red { get; init; }

    [JsonPropertyName("green")]
    public int Green { get; init; }

    [JsonPropertyName("blue")]
    public int Blue { get; init; }

    [JsonPropertyName("transitionCurve")]
    public string TransitionCurve { get; init; } = PluginConfiguration.ColorPresetTransitionCurveLinear;

    [JsonPropertyName("targetLabel")]
    public string? TargetLabel { get; init; }

    [JsonPropertyName("targetAllEnabledMappings")]
    public bool TargetAllEnabledMappings { get; init; }

    [JsonPropertyName("targetUserIds")]
    public IReadOnlyList<string> TargetUserIds { get; init; } = Array.Empty<string>();

    [JsonPropertyName("targetRoutes")]
    public IReadOnlyList<HueSceneAutomationTargetRoute> TargetRoutes { get; init; } = Array.Empty<HueSceneAutomationTargetRoute>();

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

    // Internal scheduler arbitration signal; omitted from serialized API/history data.
    internal bool BlockedByPlayback { get; init; }
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

    [JsonPropertyName("playbackOrder")]
    public string PlaybackOrder { get; init; } = PluginConfiguration.ScenePlaylistOrderSequential;

    [JsonPropertyName("targetLabel")]
    public string? TargetLabel { get; init; }

    [JsonPropertyName("targetAllEnabledMappings")]
    public bool TargetAllEnabledMappings { get; init; }

    [JsonPropertyName("targetUserIds")]
    public IReadOnlyList<string> TargetUserIds { get; init; } = Array.Empty<string>();

    [JsonPropertyName("targetRoutes")]
    public IReadOnlyList<HueSceneAutomationTargetRoute> TargetRoutes { get; init; } = Array.Empty<HueSceneAutomationTargetRoute>();

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

    // Internal scheduler arbitration signal; omitted from serialized API/history data.
    internal bool BlockedByPlayback { get; init; }
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

    [JsonPropertyName("originalIndex")]
    public int OriginalIndex { get; init; }

    [JsonPropertyName("presetName")]
    public string PresetName { get; init; } = string.Empty;

    [JsonPropertyName("effect")]
    public string Effect { get; init; } = PluginConfiguration.ColorPresetEffectSolid;

    [JsonPropertyName("effectSpeedPercent")]
    public int EffectSpeedPercent { get; init; } = PluginConfiguration.DefaultColorPresetEffectSpeedPercent;

    [JsonPropertyName("transitionCurve")]
    public string TransitionCurve { get; init; } = PluginConfiguration.ColorPresetTransitionCurveLinear;

    [JsonPropertyName("red")]
    public int Red { get; init; }

    [JsonPropertyName("green")]
    public int Green { get; init; }

    [JsonPropertyName("blue")]
    public int Blue { get; init; }

    [JsonPropertyName("brightnessPercent")]
    public int BrightnessPercent { get; init; }

    [JsonPropertyName("startOffsetSeconds")]
    public int StartOffsetSeconds { get; init; }

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

    // Internal scheduler arbitration signal; omitted from serialized API/history data.
    internal bool BlockedByPlayback { get; init; }
}

/// <summary>
/// Credential-free effective plan for one step in an upcoming scheduled playlist run.
/// </summary>
public sealed class HueScenePlaylistScheduleStep
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("repeatIndex")]
    public int RepeatIndex { get; init; } = PluginConfiguration.DefaultScenePlaylistRepeatCount;

    [JsonPropertyName("originalIndex")]
    public int OriginalIndex { get; init; }

    [JsonPropertyName("presetName")]
    public string PresetName { get; init; } = string.Empty;

    [JsonPropertyName("effect")]
    public string Effect { get; init; } = PluginConfiguration.ColorPresetEffectSolid;

    [JsonPropertyName("effectSpeedPercent")]
    public int EffectSpeedPercent { get; init; } = PluginConfiguration.DefaultColorPresetEffectSpeedPercent;

    [JsonPropertyName("transitionCurve")]
    public string TransitionCurve { get; init; } = PluginConfiguration.ColorPresetTransitionCurveLinear;

    [JsonPropertyName("red")]
    public int Red { get; init; }

    [JsonPropertyName("green")]
    public int Green { get; init; }

    [JsonPropertyName("blue")]
    public int Blue { get; init; }

    [JsonPropertyName("brightnessPercent")]
    public int BrightnessPercent { get; init; }

    [JsonPropertyName("durationSeconds")]
    public int DurationSeconds { get; init; }

    [JsonPropertyName("transitionSeconds")]
    public int TransitionSeconds { get; init; }

    [JsonPropertyName("transitionOutSeconds")]
    public int TransitionOutSeconds { get; init; }

    [JsonPropertyName("startOffsetSeconds")]
    public int StartOffsetSeconds { get; init; }
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

    [JsonPropertyName("playlistPlaybackOrder")]
    public string PlaylistPlaybackOrder { get; init; } = PluginConfiguration.ScenePlaylistOrderSequential;

    [JsonPropertyName("priority")]
    public int Priority { get; init; }

    [JsonPropertyName("targetAllEnabledMappings")]
    public bool TargetAllEnabledMappings { get; init; }

    [JsonPropertyName("targetUserIds")]
    public IReadOnlyList<string> TargetUserIds { get; init; } = Array.Empty<string>();

    [JsonPropertyName("targetRoutes")]
    public IReadOnlyList<HueSceneAutomationTargetRoute> TargetRoutes { get; init; } = Array.Empty<HueSceneAutomationTargetRoute>();

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

    [JsonPropertyName("brightnessPercent")]
    public int BrightnessPercent { get; init; }

    [JsonPropertyName("red")]
    public int Red { get; init; }

    [JsonPropertyName("green")]
    public int Green { get; init; }

    [JsonPropertyName("blue")]
    public int Blue { get; init; }

    [JsonPropertyName("transitionCurve")]
    public string TransitionCurve { get; init; } = PluginConfiguration.ColorPresetTransitionCurveLinear;

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

    [JsonPropertyName("timeZoneIanaId")]
    public string TimeZoneIanaId { get; init; } = string.Empty;

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

    [JsonPropertyName("brightnessPercent")]
    public int? BrightnessPercent { get; init; }

    [JsonPropertyName("red")]
    public int Red { get; init; }

    [JsonPropertyName("green")]
    public int Green { get; init; }

    [JsonPropertyName("blue")]
    public int Blue { get; init; }

    [JsonPropertyName("transitionCurve")]
    public string TransitionCurve { get; init; } = PluginConfiguration.ColorPresetTransitionCurveLinear;

    [JsonPropertyName("durationSeconds")]
    public int DurationSeconds { get; init; }

    [JsonPropertyName("playlistStepCount")]
    public int PlaylistStepCount { get; init; }

    [JsonPropertyName("playlistRepeatCount")]
    public int PlaylistRepeatCount { get; init; } = PluginConfiguration.DefaultScenePlaylistRepeatCount;

    [JsonPropertyName("playlistPlaybackOrder")]
    public string PlaylistPlaybackOrder { get; init; } = PluginConfiguration.ScenePlaylistOrderSequential;

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

    [JsonPropertyName("targetRoutes")]
    public IReadOnlyList<HueSceneAutomationTargetRoute> TargetRoutes { get; init; } = Array.Empty<HueSceneAutomationTargetRoute>();

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

    [JsonPropertyName("timeZoneIanaId")]
    public string TimeZoneIanaId { get; init; } = string.Empty;

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
/// Sanitized administrator-facing status for one pending scheduled cleanup.
/// </summary>
public sealed class HueSceneAutomationPendingCleanupStatus
{
    [JsonPropertyName("cleanupId")]
    public string CleanupId { get; init; } = string.Empty;

    [JsonPropertyName("scheduleId")]
    public string ScheduleId { get; init; } = string.Empty;

    [JsonPropertyName("targetUserId")]
    public string TargetUserId { get; init; } = string.Empty;

    [JsonPropertyName("targetDeviceId")]
    public string TargetDeviceId { get; init; } = string.Empty;

    [JsonPropertyName("capturedAtUtc")]
    public DateTime CapturedAtUtc { get; init; }

    [JsonPropertyName("attemptCount")]
    public int AttemptCount { get; init; }

    [JsonPropertyName("lastAttemptAtUtc")]
    public DateTime? LastAttemptAtUtc { get; init; }

    [JsonPropertyName("nextAttemptAtUtc")]
    public DateTime? NextAttemptAtUtc { get; init; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; init; }
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

    [JsonPropertyName("pendingCleanupCount")]
    public int PendingCleanupCount { get; init; }

    [JsonPropertyName("pendingCleanups")]
    public IReadOnlyList<HueSceneAutomationPendingCleanupStatus> PendingCleanups { get; init; } = Array.Empty<HueSceneAutomationPendingCleanupStatus>();

    [JsonPropertyName("conflicts")]
    public IReadOnlyList<HueSceneScheduleConflict> Conflicts { get; init; } = Array.Empty<HueSceneScheduleConflict>();
}
