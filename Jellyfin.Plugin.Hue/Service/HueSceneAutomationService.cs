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

        return await RunScheduleCoreAsync(config!, schedule, cancellationToken).ConfigureAwait(false);
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
                ? $"User mapping {mapping.UserId.Trim()}"
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

            var result = await RunScheduleCoreAsync(config, schedule, cancellationToken).ConfigureAwait(false);
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
}
