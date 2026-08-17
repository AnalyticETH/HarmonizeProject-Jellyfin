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
/// Runs recurring, credential-free color-scene cues. Each cue reuses the existing
/// non-destructive preview lifecycle, so the selected lights are captured, displayed for
/// the saved scene duration, deactivated, and restored automatically.
/// </summary>
public sealed class HueSceneAutomationService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private readonly IHueStreamTester _streamTester;
    private readonly HueClient _hueClient;
    private readonly ILogger<HueSceneAutomationService> _logger;
    private readonly object _runSlotLock = new();
    private readonly Dictionary<string, DateTime> _lastRunSlots = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _runtimeStateLock = new();
    private readonly Dictionary<string, HueSceneScheduleRuntimeState> _runtimeStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _historyLock = new();
    private readonly List<HueSceneAutomationRunResult> _runHistory = new();
    private bool _historyLoaded;

    public const int MaxSceneScheduleHistoryCount = PluginConfiguration.MaxSceneScheduleHistoryCount;

    public HueSceneAutomationService(
        IHueStreamTester streamTester,
        HueClient hueClient,
        ILogger<HueSceneAutomationService> logger)
    {
        _streamTester = streamTester ?? throw new ArgumentNullException(nameof(streamTester));
        _hueClient = hueClient ?? throw new ArgumentNullException(nameof(hueClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
    /// Returns the newest sanitized scheduled-scene run summaries. The optional
    /// schedule filter is matched against the stable cue ID and never against secrets.
    /// </summary>
    public IReadOnlyList<HueSceneAutomationRunResult> GetHistory(
        int limit = MaxSceneScheduleHistoryCount,
        string? scheduleId = null)
    {
        EnsureHistoryLoaded();
        var boundedLimit = Math.Clamp(limit, 1, MaxSceneScheduleHistoryCount);
        var normalizedScheduleId = scheduleId?.Trim();
        lock (_historyLock)
        {
            return _runHistory
                .Where(result => string.IsNullOrWhiteSpace(normalizedScheduleId) ||
                                 string.Equals(result.ScheduleId, normalizedScheduleId, StringComparison.OrdinalIgnoreCase))
                .Take(boundedLimit)
                .Select(CloneRunResult)
                .ToArray();
        }
    }

    /// <summary>
    /// Clears retained scheduled-scene history without stopping an active cue. Runtime
    /// counters and last-run pointers are reset while an in-flight run remains marked
    /// as active until its normal completion.
    /// </summary>
    public int ClearHistory()
    {
        EnsureHistoryLoaded();
        int clearedCount;
        lock (_historyLock)
        {
            clearedCount = _runHistory.Count;
            _runHistory.Clear();
        }

        lock (_runtimeStateLock)
        {
            foreach (var state in _runtimeStates.Values)
            {
                state.RunCount = 0;
                state.LastRunAtUtc = null;
                state.LastSucceeded = null;
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
        var localNow = DateTime.Now;
        var config = Plugin.Instance?.Configuration;
        var schedules = config?.SceneSchedules?
            .Where(schedule => schedule != null)
            .Select(CloneSchedule)
            .OrderBy(schedule => schedule.TimeOfDay, StringComparer.Ordinal)
            .ThenBy(schedule => schedule.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<HueSceneSchedule>();

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
            return new HueSceneScheduleRuntimeStatus
            {
                ScheduleId = schedule.Id?.Trim() ?? string.Empty,
                ScheduleName = schedule.Name?.Trim() ?? string.Empty,
                PresetName = schedule.PresetName?.Trim() ?? string.Empty,
                TargetLabel = ResolveTargetLabel(config, schedule),
                TimeOfDay = schedule.TimeOfDay?.Trim() ?? string.Empty,
                DaysOfWeekMask = schedule.DaysOfWeekMask,
                Enabled = schedule.Enabled,
                Ready = readiness.Ready,
                ReadinessMessage = readiness.Message,
                NextRunLocal = GetNextRunLocal(schedule, localNow),
                LastRunAtUtc = runtime.LastRunAtUtc,
                LastSucceeded = runtime.LastSucceeded,
                LastMessage = runtime.LastMessage,
                LastCleanupWarning = runtime.LastCleanupWarning,
                RunCount = runtime.RunCount,
                IsRunning = runtime.ActiveRuns > 0
            };
        }).ToArray();

        return new HueSceneAutomationStatus
        {
            ServiceAvailable = true,
            GeneratedAtUtc = DateTime.UtcNow,
            ServerLocalNow = DateTime.SpecifyKind(localNow, DateTimeKind.Unspecified),
            Schedules = statuses
        };
    }

    /// <summary>
    /// Calculates the next server-local occurrence after the supplied local time.
    /// The return value intentionally has an unspecified kind so clients do not mistake
    /// it for a UTC timestamp; the companion status field names the server-local basis.
    /// </summary>
    internal static DateTime? GetNextRunLocal(HueSceneSchedule schedule, DateTime localNow)
    {
        if (schedule == null || !schedule.Enabled ||
            (schedule.DaysOfWeekMask & PluginConfiguration.AllSceneScheduleDaysMask) == 0 ||
            !PluginConfiguration.TryNormalizeSceneScheduleTime(schedule.TimeOfDay, out var normalized))
        {
            return null;
        }

        var expectedTime = TimeSpan.Parse(normalized, System.Globalization.CultureInfo.InvariantCulture);
        var unspecifiedNow = DateTime.SpecifyKind(localNow, DateTimeKind.Unspecified);
        for (var dayOffset = 0; dayOffset <= 7; dayOffset++)
        {
            var candidateDate = unspecifiedNow.Date.AddDays(dayOffset);
            var dayBit = 1 << (int)candidateDate.DayOfWeek;
            if ((schedule.DaysOfWeekMask & dayBit) == 0)
                continue;

            var candidate = candidateDate.Add(expectedTime);
            if (candidate > unspecifiedNow)
                return DateTime.SpecifyKind(candidate, DateTimeKind.Unspecified);
        }

        return null;
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

        return await RunScheduleTrackedAsync(config!, schedule, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Determines whether a schedule is due in the supplied server-local minute.
    /// Sunday is bit 0 and Saturday is bit 6 in <see cref="HueSceneSchedule.DaysOfWeekMask"/>.
    /// </summary>
    internal static bool IsDue(HueSceneSchedule schedule, DateTime localNow)
    {
        if (schedule == null || !schedule.Enabled ||
            !PluginConfiguration.TryNormalizeSceneScheduleTime(schedule.TimeOfDay, out var normalized))
        {
            return false;
        }

        var expectedTime = TimeSpan.Parse(normalized, System.Globalization.CultureInfo.InvariantCulture);
        var dayBit = 1 << (int)localNow.DayOfWeek;
        return (schedule.DaysOfWeekMask & dayBit) != 0 &&
               localNow.Hour == expectedTime.Hours &&
               localNow.Minute == expectedTime.Minutes;
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

        if (config == null)
            return new HueSceneScheduleReadiness(false, "Plugin configuration is unavailable.");

        if (!PluginConfiguration.TryNormalizeSceneScheduleTime(schedule.TimeOfDay, out _))
            return new HueSceneScheduleReadiness(false, "The scheduled time is invalid.");

        if (schedule.DaysOfWeekMask < 1 || schedule.DaysOfWeekMask > PluginConfiguration.AllSceneScheduleDaysMask)
            return new HueSceneScheduleReadiness(false, "At least one valid day must be selected.");

        var preset = config.ColorPresets?.FirstOrDefault(candidate =>
            candidate != null &&
            string.Equals(candidate.Name?.Trim(), schedule.PresetName?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (preset == null)
            return new HueSceneScheduleReadiness(false, "The saved scene no longer exists.");

        if (!TryResolveTarget(config, schedule, out _, out var targetError))
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
        description = new HueSceneAutomationTargetDescription();
        error = string.Empty;
        if (config == null || schedule == null)
        {
            error = "Scene automation configuration is unavailable.";
            return false;
        }

        var targetUserId = schedule.TargetUserId?.Trim() ?? string.Empty;
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

    private async Task RunDueSchedulesAsync(DateTime localNow, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var schedules = config?.SceneSchedules?
            .Where(schedule => schedule != null)
            .Select(CloneSchedule)
            .ToArray();
        if (config == null || schedules == null || schedules.Length == 0)
            return;

        var slot = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, localNow.Minute, 0, DateTimeKind.Unspecified);
        foreach (var schedule in schedules)
        {
            if (!IsDue(schedule, localNow) || !TryClaimRunSlot(schedule.Id, slot))
                continue;

            var result = await RunScheduleTrackedAsync(config, schedule, cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                _logger.LogInformation(
                    "Hue scene schedule {0} displayed preset {1} for target {2}",
                    schedule.Name,
                    schedule.PresetName,
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

    private async Task<HueSceneAutomationRunResult> RunScheduleCoreAsync(
        PluginConfiguration config,
        HueSceneSchedule schedule,
        CancellationToken cancellationToken)
    {
        var preset = config.ColorPresets?.FirstOrDefault(candidate =>
            candidate != null &&
            string.Equals(candidate.Name?.Trim(), schedule.PresetName?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (preset == null)
        {
            return Failure(schedule.Id, "The scene schedule references a saved scene that no longer exists.", schedule);
        }

        if (!TryResolveTarget(config, schedule, out var target, out var targetError))
            return Failure(schedule.Id, targetError, schedule);

        _hueClient.RetryAttempts = target.RetryAttempts;

        JsonElement? areaConfiguration;
        try
        {
            areaConfiguration = await _hueClient.GetEntertainmentConfiguration(
                target.BridgeIp,
                target.AppKey,
                target.EntertainmentAreaId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        if (areaConfiguration == null)
        {
            return Failure(
                schedule.Id,
                "The Hue bridge did not return the configured entertainment area.",
                schedule,
                target.TargetLabel);
        }

        HueStreamProbeResult preview;
        try
        {
            preview = await _streamTester.PreviewAsync(
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
                preset.DurationSeconds,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hue scene schedule {0} failed while displaying preset", schedule.Name);
            return Failure(schedule.Id, "The scheduled scene preview failed unexpectedly.", schedule, target.TargetLabel);
        }

        return new HueSceneAutomationRunResult
        {
            ScheduleId = schedule.Id,
            ScheduleName = schedule.Name?.Trim() ?? string.Empty,
            PresetName = preset.Name?.Trim() ?? string.Empty,
            TargetLabel = target.TargetLabel,
            Succeeded = preview.Succeeded,
            Message = preview.Message,
            CleanupWarning = preview.CleanupWarning,
            RunAtUtc = DateTime.UtcNow
        };
    }

    private async Task<HueSceneAutomationRunResult> RunScheduleTrackedAsync(
        PluginConfiguration config,
        HueSceneSchedule schedule,
        CancellationToken cancellationToken)
    {
        BeginRun(schedule.Id);
        HueSceneAutomationRunResult? result = null;
        try
        {
            result = await RunScheduleCoreAsync(config, schedule, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = Failure(schedule.Id, "The scene cue run was canceled.", schedule);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hue scene schedule {0} failed unexpectedly", schedule.Name);
            result = Failure(schedule.Id, "The scheduled scene could not be completed.", schedule);
            return result;
        }
        finally
        {
            CompleteRun(schedule.Id, result);
        }
    }

    private HueSceneScheduleRuntimeState GetRuntimeState(string? scheduleId)
    {
        var key = scheduleId?.Trim() ?? string.Empty;
        lock (_runtimeStateLock)
        {
            return _runtimeStates.TryGetValue(key, out var state)
                ? state.Clone()
                : new HueSceneScheduleRuntimeState();
        }
    }

    private void BeginRun(string? scheduleId)
    {
        var key = scheduleId?.Trim() ?? string.Empty;
        lock (_runtimeStateLock)
        {
            if (!_runtimeStates.TryGetValue(key, out var state))
            {
                state = new HueSceneScheduleRuntimeState();
                _runtimeStates[key] = state;
            }

            state.ActiveRuns++;
        }
    }

    private void CompleteRun(string? scheduleId, HueSceneAutomationRunResult? result)
    {
        EnsureHistoryLoaded();
        var key = scheduleId?.Trim() ?? string.Empty;
        lock (_runtimeStateLock)
        {
            if (!_runtimeStates.TryGetValue(key, out var state))
            {
                state = new HueSceneScheduleRuntimeState();
                _runtimeStates[key] = state;
            }

            state.ActiveRuns = Math.Max(0, state.ActiveRuns - 1);
            state.RunCount++;
            state.LastRunAtUtc = result?.RunAtUtc ?? DateTime.UtcNow;
            state.LastSucceeded = result?.Succeeded ?? false;
            state.LastMessage = result?.Message ?? "The scheduled scene ended without a result.";
            state.LastCleanupWarning = result?.CleanupWarning;
            if (result != null)
                result.RunCount = state.RunCount;
        }

        if (result != null)
        {
            lock (_historyLock)
            {
                _runHistory.Insert(0, CloneRunResult(result));
                if (_runHistory.Count > MaxSceneScheduleHistoryCount)
                    _runHistory.RemoveRange(MaxSceneScheduleHistoryCount, _runHistory.Count - MaxSceneScheduleHistoryCount);
            }
        }

        PersistSceneScheduleHistory();
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
                    .Take(MaxSceneScheduleHistoryCount)
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

                    state.RunCount = Math.Max(latest.RunCount, group.Count());
                    state.LastRunAtUtc = latest.RunAtUtc;
                    state.LastSucceeded = latest.Succeeded;
                    state.LastMessage = latest.Message;
                    state.LastCleanupWarning = latest.CleanupWarning;
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
                .Take(MaxSceneScheduleHistoryCount)
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
            TargetLabel = result.TargetLabel,
            Succeeded = result.Succeeded,
            Message = result.Message,
            CleanupWarning = result.CleanupWarning,
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
            TargetLabel = source.TargetLabel?.Trim(),
            Succeeded = source.Succeeded,
            Message = source.Message?.Trim() ?? string.Empty,
            CleanupWarning = source.CleanupWarning?.Trim(),
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
            TargetLabel = entry.TargetLabel?.Trim(),
            Succeeded = entry.Succeeded,
            Message = entry.Message?.Trim() ?? string.Empty,
            CleanupWarning = entry.CleanupWarning?.Trim(),
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
            TargetLabel = source.TargetLabel,
            Succeeded = source.Succeeded,
            Message = source.Message,
            CleanupWarning = source.CleanupWarning,
            RunAtUtc = source.RunAtUtc,
            RunCount = source.RunCount
        };
    }

    internal static string ResolveTargetLabel(PluginConfiguration? config, HueSceneSchedule schedule)
    {
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

    private static HueSceneSchedule CloneSchedule(HueSceneSchedule source)
    {
        return new HueSceneSchedule
        {
            Id = source.Id,
            Name = source.Name,
            PresetName = source.PresetName,
            TargetUserId = source.TargetUserId,
            TimeOfDay = source.TimeOfDay,
            DaysOfWeekMask = source.DaysOfWeekMask,
            Enabled = source.Enabled
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
            TargetLabel = targetLabel,
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
    public string? LastMessage { get; set; }
    public string? LastCleanupWarning { get; set; }

    public HueSceneScheduleRuntimeState Clone()
    {
        return new HueSceneScheduleRuntimeState
        {
            ActiveRuns = ActiveRuns,
            RunCount = RunCount,
            LastRunAtUtc = LastRunAtUtc,
            LastSucceeded = LastSucceeded,
            LastMessage = LastMessage,
            LastCleanupWarning = LastCleanupWarning
        };
    }
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
/// Sanitized result returned by an immediate or recurring scene cue run.
/// </summary>
public sealed class HueSceneAutomationRunResult
{
    [JsonPropertyName("scheduleId")]
    public string ScheduleId { get; init; } = string.Empty;

    [JsonPropertyName("scheduleName")]
    public string ScheduleName { get; init; } = string.Empty;

    [JsonPropertyName("presetName")]
    public string PresetName { get; init; } = string.Empty;

    [JsonPropertyName("targetLabel")]
    public string? TargetLabel { get; init; }

    [JsonPropertyName("succeeded")]
    public bool Succeeded { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("cleanupWarning")]
    public string? CleanupWarning { get; init; }

    [JsonPropertyName("runAtUtc")]
    public DateTime RunAtUtc { get; init; }

    [JsonPropertyName("runCount")]
    public int RunCount { get; internal set; }
}

/// <summary>
/// Sanitized status for one configured recurring scene cue.
/// </summary>
public sealed class HueSceneScheduleRuntimeStatus
{
    [JsonPropertyName("scheduleId")]
    public string ScheduleId { get; init; } = string.Empty;

    [JsonPropertyName("scheduleName")]
    public string ScheduleName { get; init; } = string.Empty;

    [JsonPropertyName("presetName")]
    public string PresetName { get; init; } = string.Empty;

    [JsonPropertyName("targetLabel")]
    public string TargetLabel { get; init; } = string.Empty;

    [JsonPropertyName("timeOfDay")]
    public string TimeOfDay { get; init; } = string.Empty;

    [JsonPropertyName("daysOfWeekMask")]
    public int DaysOfWeekMask { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("ready")]
    public bool Ready { get; init; }

    [JsonPropertyName("readinessMessage")]
    public string ReadinessMessage { get; init; } = string.Empty;

    [JsonPropertyName("nextRunLocal")]
    public DateTime? NextRunLocal { get; init; }

    [JsonPropertyName("lastRunAtUtc")]
    public DateTime? LastRunAtUtc { get; init; }

    [JsonPropertyName("lastSucceeded")]
    public bool? LastSucceeded { get; init; }

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
/// Sanitized administrator-facing status for the recurring scene automation service.
/// </summary>
public sealed class HueSceneAutomationStatus
{
    [JsonPropertyName("serviceAvailable")]
    public bool ServiceAvailable { get; init; }

    [JsonPropertyName("generatedAtUtc")]
    public DateTime GeneratedAtUtc { get; init; }

    [JsonPropertyName("serverLocalNow")]
    public DateTime ServerLocalNow { get; init; }

    [JsonPropertyName("schedules")]
    public IReadOnlyList<HueSceneScheduleRuntimeStatus> Schedules { get; init; } = Array.Empty<HueSceneScheduleRuntimeStatus>();
}
