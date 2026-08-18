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
    private readonly object _runSlotLock = new();
    private readonly Dictionary<string, DateTime> _lastRunSlots = new(StringComparer.OrdinalIgnoreCase);
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
            var preset = config?.ColorPresets?.FirstOrDefault(candidate =>
                candidate != null &&
                string.Equals(candidate.Name?.Trim(), schedule.PresetName?.Trim(), StringComparison.OrdinalIgnoreCase));
            PluginConfiguration.TryNormalizeSceneScheduleRecurrence(schedule.Recurrence, out var recurrence);
            var timeZone = PluginConfiguration.TryResolveSceneScheduleTimeZone(schedule.TimeZoneId, out var resolvedTimeZone)
                ? resolvedTimeZone
                : TimeZoneInfo.Local;
            return new HueSceneScheduleRuntimeStatus
            {
                ScheduleId = schedule.Id?.Trim() ?? string.Empty,
                ScheduleName = schedule.Name?.Trim() ?? string.Empty,
                PresetName = schedule.PresetName?.Trim() ?? string.Empty,
                DurationSeconds = GetEffectiveDurationSeconds(schedule, preset),
                TransitionSeconds = GetEffectiveTransitionSeconds(schedule, preset),
                TransitionOutSeconds = GetEffectiveTransitionOutSeconds(schedule, preset),
                Recurrence = recurrence,
                RecurrenceInterval = schedule.RecurrenceInterval,
                DayOfMonth = schedule.DayOfMonth,
                MonthOfYear = schedule.MonthOfYear,
                WeekOfMonth = schedule.WeekOfMonth,
                DayOfWeek = schedule.DayOfWeek,
                TargetLabel = ResolveTargetLabel(config, schedule),
                TimeOfDay = schedule.TimeOfDay?.Trim() ?? string.Empty,
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
                Ready = readiness.Ready,
                ReadinessMessage = readiness.Message,
                NextRunLocal = GetNextRunLocal(schedule, localNow),
                NextRunUtc = GetNextRunUtc(schedule, localNow),
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
            AutomationEnabled = config?.SceneAutomationEnabled ?? true,
            GeneratedAtUtc = DateTime.UtcNow,
            ServerLocalNow = DateTime.SpecifyKind(localNow, DateTimeKind.Unspecified),
            ServerTimeZoneId = TimeZoneInfo.Local.Id,
            Schedules = statuses
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
        int transitionOutSeconds = PluginConfiguration.MinColorPresetTransitionOutSeconds)
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
            !PluginConfiguration.TryNormalizeSceneScheduleTime(schedule.TimeOfDay, out var normalized) ||
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

        var expectedTime = TimeSpan.Parse(normalized, System.Globalization.CultureInfo.InvariantCulture);
        var firstCandidateDate = scheduleNow.Date;
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

        for (var dayOffset = 0; dayOffset < boundedHorizon; dayOffset++)
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

            var candidateLocal = DateTime.SpecifyKind(candidateDate.Add(expectedTime), DateTimeKind.Unspecified);
            // A spring-forward transition can remove a local wall-clock time. Skipping
            // that occurrence keeps previews identical to the hosted scheduler.
            if (timeZone.IsInvalidTime(candidateLocal))
                continue;

            DateTime candidateUtc;
            try
            {
                candidateUtc = TimeZoneInfo.ConvertTimeToUtc(candidateLocal, timeZone);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (candidateUtc <= serverUtcNow)
                continue;

            occurrences.Add(new HueSceneScheduleOccurrence
            {
                ScheduleId = schedule.Id?.Trim() ?? string.Empty,
                ScheduleName = schedule.Name?.Trim() ?? string.Empty,
                PresetName = schedule.PresetName?.Trim() ?? string.Empty,
                DurationSeconds = schedule.DurationSeconds,
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

            if (occurrences.Count >= boundedOccurrences)
                break;
        }

        return occurrences;
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
    /// Determines whether a schedule is due in the supplied server-local minute after
    /// converting that instant into the cue's configured time zone. One-time cues match
    /// their RunDate; recurring cues use every calendar day, Sunday=1 through Saturday=64
    /// bits, a monthly calendar day, a monthly ordinal weekday, or a yearly calendar date.
    /// </summary>
    internal static bool IsDue(HueSceneSchedule schedule, DateTime localNow)
    {
        if (schedule == null || !schedule.Enabled ||
            !PluginConfiguration.TryNormalizeSceneScheduleTime(schedule.TimeOfDay, out var normalized) ||
            !TryGetScheduleLocalNow(schedule, localNow, out var scheduleNow, out _) ||
            !TryGetScheduleRunDate(schedule, out var runDate))
            return false;

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

        if (!IsScheduleDateAllowed(schedule, scheduleNow.Date))
            return false;

        var expectedTime = TimeSpan.Parse(normalized, System.Globalization.CultureInfo.InvariantCulture);
        return (runDate.HasValue || IsScheduleRecurrenceDate(schedule, normalizedRecurrence, scheduleNow.Date)) &&
               scheduleNow.Hour == expectedTime.Hours &&
               scheduleNow.Minute == expectedTime.Minutes;
    }

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
        if (TryGetScheduleLocalNow(schedule, serverLocalNow, out var scheduleLocalNow, out _))
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

        if (config == null)
            return new HueSceneScheduleReadiness(false, "Plugin configuration is unavailable.");

        if (!PluginConfiguration.TryNormalizeSceneScheduleTime(schedule.TimeOfDay, out _))
            return new HueSceneScheduleReadiness(false, "The scheduled time is invalid.");

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

    internal async Task RunDueSchedulesAsync(DateTime localNow, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.SceneAutomationEnabled)
            return;

        var schedules = config.SceneSchedules?
            .Where(schedule => schedule != null)
            .Select(CloneSchedule)
            .ToArray();
        if (schedules == null || schedules.Length == 0)
            return;

        foreach (var schedule in schedules)
        {
            var slot = GetScheduleRunSlot(schedule, localNow);
            if (!IsDue(schedule, localNow) || !TryClaimRunSlot(schedule.Id, slot))
                continue;

            var result = await RunScheduleTrackedAsync(config, schedule, cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                DisableCompletedOneTimeSchedule(config, schedule);
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

    private void DisableCompletedOneTimeSchedule(PluginConfiguration config, HueSceneSchedule schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule.RunDate))
            return;

        var configuredSchedule = config.SceneSchedules?.FirstOrDefault(candidate =>
            candidate != null &&
            string.Equals(candidate.Id?.Trim(), schedule.Id?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (configuredSchedule == null || !configuredSchedule.Enabled)
            return;

        configuredSchedule.Enabled = false;
        try
        {
            Plugin.Instance?.SaveConfiguration();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "One-time Hue scene schedule {0} ran but could not persist its completed state", schedule.Name);
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
                GetEffectiveDurationSeconds(schedule, preset),
                cancellationToken,
                GetEffectiveTransitionSeconds(schedule, preset),
                GetEffectiveTransitionOutSeconds(schedule, preset)).ConfigureAwait(false);
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
            TimeZoneId = source.TimeZoneId,
            Recurrence = source.Recurrence,
            RecurrenceInterval = source.RecurrenceInterval,
            DayOfMonth = source.DayOfMonth,
            MonthOfYear = source.MonthOfYear,
            WeekOfMonth = source.WeekOfMonth,
            DayOfWeek = source.DayOfWeek,
            DurationSeconds = source.DurationSeconds,
            RunDate = source.RunDate,
            StartDate = source.StartDate,
            EndDate = source.EndDate,
            ExcludedDates = source.ExcludedDates?.ToList() ?? new List<string>(),
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

    [JsonPropertyName("durationSeconds")]
    public int DurationSeconds { get; init; }

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

    [JsonPropertyName("durationSeconds")]
    public int DurationSeconds { get; init; }

    [JsonPropertyName("transitionSeconds")]
    public int TransitionSeconds { get; init; }

    [JsonPropertyName("transitionOutSeconds")]
    public int TransitionOutSeconds { get; init; }

    [JsonPropertyName("recurrence")]
    public string Recurrence { get; init; } = PluginConfiguration.SceneScheduleRecurrenceWeekly;

    [JsonPropertyName("recurrenceInterval")]
    public int RecurrenceInterval { get; init; } = PluginConfiguration.MinSceneScheduleRecurrenceInterval;

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

    [JsonPropertyName("timeOfDay")]
    public string TimeOfDay { get; init; } = string.Empty;

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

    [JsonPropertyName("generatedAtUtc")]
    public DateTime GeneratedAtUtc { get; init; }

    [JsonPropertyName("serverLocalNow")]
    public DateTime ServerLocalNow { get; init; }

    [JsonPropertyName("serverTimeZoneId")]
    public string ServerTimeZoneId { get; init; } = string.Empty;

    [JsonPropertyName("schedules")]
    public IReadOnlyList<HueSceneScheduleRuntimeStatus> Schedules { get; init; } = Array.Empty<HueSceneScheduleRuntimeStatus>();
}
