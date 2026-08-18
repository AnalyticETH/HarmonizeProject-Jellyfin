using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Hue.Configuration;
using Jellyfin.Plugin.Hue.Hue;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Hue.Service;

/// <summary>
/// Performs a short, non-destructive DTLS probe against a configured Hue entertainment area.
/// </summary>
public interface IHueStreamTester
{
    Task<HueStreamProbeResult> TestAsync(
        string bridgeIp,
        string appKey,
        string clientKey,
        string areaId,
        JsonElement areaConfiguration,
        IReadOnlySet<int>? channelIds = null,
        CancellationToken cancellationToken = default);

    Task<HueStreamProbeResult> PreviewAsync(
        string bridgeIp,
        string appKey,
        string clientKey,
        string areaId,
        JsonElement areaConfiguration,
        IReadOnlySet<int>? channelIds,
        int red,
        int green,
        int blue,
        int brightnessPercent,
        int durationSeconds,
        CancellationToken cancellationToken = default,
        int transitionSeconds = PluginConfiguration.MinColorPresetTransitionSeconds,
        int transitionOutSeconds = PluginConfiguration.MinColorPresetTransitionOutSeconds);

    /// <summary>
    /// Cancels the active diagnostic or preview, if one is running. Cleanup continues
    /// through the normal state-restoring lifecycle.
    /// </summary>
    bool CancelActiveDiagnostic();
}

/// <summary>
/// Result of a DTLS stream probe. No bridge credentials are included.
/// </summary>
public sealed class HueStreamProbeResult
{
    public bool Succeeded { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? CleanupWarning { get; init; }
}

/// <summary>
/// Opens the same activate/DTLS/send/deactivate lifecycle used by playback, using a
/// very low-intensity probe color and the selected area's real channel IDs.
/// </summary>
public sealed class HueStreamTester : IHueStreamTester
{
    private const int EntertainmentAreaActivationDelayMs = 200;
    private const int PreviewTransitionRefreshIntervalMs = 100;
    private const byte ProbeColor = 1;
    private const string DiagnosticBusyMessage = "Another Hue diagnostic is already running. Wait for it to finish before starting another probe or preview.";
    internal const int MinPreviewDurationSeconds = PluginConfiguration.MinPreviewDurationSeconds;
    internal const int MaxPreviewDurationSeconds = PluginConfiguration.MaxPreviewDurationSeconds;

    private readonly HueClient _hueClient;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<HueStreamTester> _logger;
    private readonly HueBridgeLifecycleGate _bridgeLifecycleGate;
    private readonly object _activeOperationLock = new();
    private CancellationTokenSource? _activeOperationCancellation;

    public HueStreamTester(
        HueClient hueClient,
        ILoggerFactory loggerFactory,
        ILogger<HueStreamTester> logger,
        HueBridgeLifecycleGate? bridgeLifecycleGate = null)
    {
        _hueClient = hueClient ?? throw new ArgumentNullException(nameof(hueClient));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _bridgeLifecycleGate = bridgeLifecycleGate ?? new HueBridgeLifecycleGate();
    }

    public Task<HueStreamProbeResult> TestAsync(
        string bridgeIp,
        string appKey,
        string clientKey,
        string areaId,
        JsonElement areaConfiguration,
        IReadOnlySet<int>? channelIds = null,
        CancellationToken cancellationToken = default)
        => RunSerializedAsync(operationCancellation => TestCoreAsync(
            bridgeIp,
            appKey,
            clientKey,
            areaId,
            areaConfiguration,
            channelIds,
            operationCancellation), cancellationToken);

    private async Task<HueStreamProbeResult> TestCoreAsync(
        string bridgeIp,
        string appKey,
        string clientKey,
        string areaId,
        JsonElement areaConfiguration,
        IReadOnlySet<int>? channelIds,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Failure("The DTLS stream probe request was canceled.");

        if (string.IsNullOrWhiteSpace(bridgeIp) ||
            string.IsNullOrWhiteSpace(appKey) ||
            string.IsNullOrWhiteSpace(clientKey) ||
            string.IsNullOrWhiteSpace(areaId))
        {
            return Failure("Bridge address, app key, client key, and entertainment area are required.");
        }

        if (!TryBuildProbeColors(areaConfiguration, channelIds, out var channelColors))
        {
            return Failure(channelIds == null
                ? "The selected entertainment area has no valid controllable channels."
                : "The selected channel profile has no valid controllable channels in this entertainment area.");
        }

        List<HueClient.LightState> savedLightStates;
        try
        {
            var captureResult = await _hueClient.GetLightStatesWithResult(
                bridgeIp,
                appKey,
                areaConfiguration,
                channelIds,
                cancellationToken).ConfigureAwait(false);
            if (!captureResult.Succeeded || captureResult.AttemptedCount == 0)
            {
                return Failure(
                    captureResult.AttemptedCount == 0
                        ? "The probe could not capture any light state safely."
                        : $"The probe could not capture all light states safely ({captureResult.CapturedCount} of {captureResult.AttemptedCount} captured; {captureResult.FailedCount} failed).");
            }

            savedLightStates = captureResult.States;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure("The DTLS stream probe request was canceled before activation.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save light state before Hue DTLS stream probe for area {0}", areaId);
            return Failure("The probe could not save the current light state safely.");
        }

        if (cancellationToken.IsCancellationRequested)
            return Failure("The DTLS stream probe request was canceled before activation.");

        bool activated;
        try
        {
            // A canceled activation request may have reached the bridge before the
            // response was aborted. Always run the normal safety cleanup if activation
            // throws cancellation so the area is deactivated and saved light state is restored.
            activated = await _hueClient.StartEntertainmentArea(
                bridgeIp,
                appKey,
                areaId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await AddCleanupResultAsync(
                Failure("The DTLS stream probe request was canceled during activation; the bridge is being restored."),
                bridgeIp,
                appKey,
                areaId,
                savedLightStates).ConfigureAwait(false);
        }
        if (!activated)
        {
            // A failed response does not prove that the bridge ignored the request:
            // activation may have reached the bridge before a timeout or transport
            // error. Run the same non-cancellable cleanup used after a successful
            // activation so diagnostics never leave a captured target in an unknown
            // state.
            return await AddCleanupResultAsync(
                Failure("The Hue bridge could not activate the entertainment area; the bridge is being restored."),
                bridgeIp,
                appKey,
                areaId,
                savedLightStates).ConfigureAwait(false);
        }

        var probeResult = Failure("The DTLS stream probe did not complete.");
        try
        {
            await Task.Delay(EntertainmentAreaActivationDelayMs, cancellationToken).ConfigureAwait(false);

            var streamer = new HueStreamer(_loggerFactory.CreateLogger<HueStreamer>());
            streamer.OnBeforeReconnectWithCancellation = reconnectToken =>
                _hueClient.StartEntertainmentArea(bridgeIp, appKey, areaId, reconnectToken);
            try
            {
                await streamer.StartStreamAsync(bridgeIp, appKey, clientKey, cancellationToken).ConfigureAwait(false);
                if (!streamer.IsHealthy())
                {
                    probeResult = Failure("The DTLS stream did not become healthy. Check the client key and OpenSSL installation.");
                }
                else
                {
                    var sent = await streamer.SendColors(
                        areaId,
                        channelColors,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    probeResult = sent
                        ? new HueStreamProbeResult
                        {
                            Succeeded = true,
                            Message = "DTLS stream opened and a low-intensity probe packet was sent."
                        }
                        : Failure("The DTLS stream opened, but the probe packet could not be sent.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                probeResult = Failure("The DTLS stream probe request was canceled; the bridge is being restored.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hue DTLS stream probe failed for entertainment area {0}", areaId);
                probeResult = Failure("The DTLS stream probe failed. Check the client key and OpenSSL diagnostics.");
            }
            finally
            {
                streamer.StopStream();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            probeResult = Failure("The DTLS stream probe request was canceled; the bridge is being restored.");
        }
        finally
        {
            probeResult = await AddCleanupResultAsync(
                probeResult,
                bridgeIp,
                appKey,
                areaId,
                savedLightStates).ConfigureAwait(false);
        }

        return probeResult;
    }

    /// <summary>
    /// Displays a bounded solid-color preview through the same save/activate/DTLS/restore
    /// lifecycle used by playback. This is intentionally separate from TestAsync so a
    /// connection check can remain low intensity and non-disruptive.
    /// </summary>
    public Task<HueStreamProbeResult> PreviewAsync(
        string bridgeIp,
        string appKey,
        string clientKey,
        string areaId,
        JsonElement areaConfiguration,
        IReadOnlySet<int>? channelIds,
        int red,
        int green,
        int blue,
        int brightnessPercent,
        int durationSeconds,
        CancellationToken cancellationToken = default,
        int transitionSeconds = PluginConfiguration.MinColorPresetTransitionSeconds,
        int transitionOutSeconds = PluginConfiguration.MinColorPresetTransitionOutSeconds)
        => RunSerializedAsync(operationCancellation => PreviewCoreAsync(
            bridgeIp,
            appKey,
            clientKey,
            areaId,
            areaConfiguration,
            channelIds,
            red,
            green,
            blue,
            brightnessPercent,
            durationSeconds,
            transitionSeconds,
            transitionOutSeconds,
            operationCancellation), cancellationToken);

    private async Task<HueStreamProbeResult> PreviewCoreAsync(
        string bridgeIp,
        string appKey,
        string clientKey,
        string areaId,
        JsonElement areaConfiguration,
        IReadOnlySet<int>? channelIds,
        int red,
        int green,
        int blue,
        int brightnessPercent,
        int durationSeconds,
        int transitionSeconds,
        int transitionOutSeconds,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Failure("The solid color preview request was canceled.");

        if (string.IsNullOrWhiteSpace(bridgeIp) ||
            string.IsNullOrWhiteSpace(appKey) ||
            string.IsNullOrWhiteSpace(clientKey) ||
            string.IsNullOrWhiteSpace(areaId))
        {
            return Failure("Bridge address, app key, client key, and entertainment area are required.");
        }

        if (durationSeconds < MinPreviewDurationSeconds || durationSeconds > MaxPreviewDurationSeconds)
        {
            return Failure($"Preview duration must be between {MinPreviewDurationSeconds} and {MaxPreviewDurationSeconds} seconds.");
        }

        if (transitionSeconds < PluginConfiguration.MinColorPresetTransitionSeconds ||
            transitionSeconds > PluginConfiguration.MaxColorPresetTransitionSeconds)
        {
            return Failure($"Preview transition must be between {PluginConfiguration.MinColorPresetTransitionSeconds} and {PluginConfiguration.MaxColorPresetTransitionSeconds} seconds.");
        }

        if (transitionSeconds > durationSeconds)
            return Failure("Preview transition cannot exceed the preview duration.");

        if (transitionOutSeconds < PluginConfiguration.MinColorPresetTransitionOutSeconds ||
            transitionOutSeconds > PluginConfiguration.MaxColorPresetTransitionOutSeconds)
        {
            return Failure($"Preview fade-out must be between {PluginConfiguration.MinColorPresetTransitionOutSeconds} and {PluginConfiguration.MaxColorPresetTransitionOutSeconds} seconds.");
        }

        if (transitionSeconds + transitionOutSeconds > durationSeconds)
            return Failure("Preview fade-in and fade-out cannot exceed the preview duration together.");

        if (!TryBuildSolidColors(
                areaConfiguration,
                channelIds,
                red,
                green,
                blue,
                brightnessPercent,
                out var channelColors))
        {
            return Failure(channelIds == null
                ? "The selected entertainment area has no valid controllable channels or the preview color is invalid."
                : "The selected channel profile has no valid controllable channels in this entertainment area or the preview color is invalid.");
        }

        List<HueClient.LightState> savedLightStates;
        try
        {
            var captureResult = await _hueClient.GetLightStatesWithResult(
                bridgeIp,
                appKey,
                areaConfiguration,
                channelIds,
                cancellationToken).ConfigureAwait(false);
            if (!captureResult.Succeeded || captureResult.AttemptedCount == 0)
            {
                return Failure(
                    captureResult.AttemptedCount == 0
                        ? "The preview could not capture any light state safely."
                        : $"The preview could not capture all light states safely ({captureResult.CapturedCount} of {captureResult.AttemptedCount} captured; {captureResult.FailedCount} failed).");
            }

            savedLightStates = captureResult.States;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure("The solid color preview request was canceled before activation.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save light state before Hue color preview for area {0}", areaId);
            return Failure("The preview could not save the current light state safely.");
        }

        if (cancellationToken.IsCancellationRequested)
            return Failure("The solid color preview request was canceled before activation.");

        bool activated;
        try
        {
            // A canceled activation request may have reached the bridge before the
            // response was aborted. Always run the normal safety cleanup if activation
            // throws cancellation so the area is deactivated and saved light state is restored.
            activated = await _hueClient.StartEntertainmentArea(
                bridgeIp,
                appKey,
                areaId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await AddCleanupResultAsync(
                Failure("The solid color preview request was canceled during activation; the bridge is being restored."),
                bridgeIp,
                appKey,
                areaId,
                savedLightStates).ConfigureAwait(false);
        }
        if (!activated)
        {
            // Treat an unsuccessful activation response as ambiguous. The request may
            // have reached the bridge even if its response was lost, so always run the
            // safety cleanup before returning to the configuration page.
            return await AddCleanupResultAsync(
                Failure("The Hue bridge could not activate the entertainment area for preview; the bridge is being restored."),
                bridgeIp,
                appKey,
                areaId,
                savedLightStates).ConfigureAwait(false);
        }

        var previewResult = Failure("The solid color preview did not complete.");
        try
        {
            await Task.Delay(EntertainmentAreaActivationDelayMs, cancellationToken).ConfigureAwait(false);

            var streamer = new HueStreamer(_loggerFactory.CreateLogger<HueStreamer>());
            streamer.OnBeforeReconnectWithCancellation = reconnectToken =>
                _hueClient.StartEntertainmentArea(bridgeIp, appKey, areaId, reconnectToken);
            try
            {
                await streamer.StartStreamAsync(bridgeIp, appKey, clientKey, cancellationToken).ConfigureAwait(false);
                if (!streamer.IsHealthy())
                {
                    previewResult = Failure("The DTLS stream did not become healthy. Check the client key and OpenSSL installation.");
                }
                else
                {
                    var previewStartedAt = DateTime.UtcNow;
                    var previewEndsAt = previewStartedAt.AddSeconds(durationSeconds);
                    var transitionEndsAt = previewStartedAt.AddSeconds(transitionSeconds);
                    var transitionOutStartsAt = previewEndsAt.Subtract(TimeSpan.FromSeconds(transitionOutSeconds));
                    var frame = transitionSeconds > PluginConfiguration.MinColorPresetTransitionSeconds
                        ? BuildTransitionColors(channelColors, 0)
                        : channelColors;
                    var sent = await streamer.SendColors(
                        areaId,
                        frame,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (!sent)
                    {
                        previewResult = Failure("The DTLS stream opened, but the preview color could not be sent.");
                    }
                    else
                    {
                        var transitionFailed = false;
                        while (transitionSeconds > PluginConfiguration.MinColorPresetTransitionSeconds)
                        {
                            var remaining = transitionEndsAt - DateTime.UtcNow;
                            if (remaining <= TimeSpan.Zero)
                                break;

                            await Task.Delay(
                                remaining > TimeSpan.FromMilliseconds(PreviewTransitionRefreshIntervalMs)
                                    ? TimeSpan.FromMilliseconds(PreviewTransitionRefreshIntervalMs)
                                    : remaining,
                                cancellationToken)
                                .ConfigureAwait(false);

                            var progress = Math.Clamp(
                                (DateTime.UtcNow - (transitionEndsAt - TimeSpan.FromSeconds(transitionSeconds))).TotalSeconds /
                                transitionSeconds,
                                0d,
                                1d);
                            frame = BuildTransitionColors(channelColors, progress);
                            if (!await streamer.SendColors(
                                    areaId,
                                    frame,
                                    cancellationToken: cancellationToken).ConfigureAwait(false))
                            {
                                transitionFailed = true;
                                previewResult = Failure("The DTLS stream stopped while fading in the preview color.");
                                break;
                            }
                        }

                        if (!transitionFailed && transitionSeconds > PluginConfiguration.MinColorPresetTransitionSeconds &&
                            !await streamer.SendColors(
                                areaId,
                                channelColors,
                                cancellationToken: cancellationToken).ConfigureAwait(false))
                        {
                            transitionFailed = true;
                            previewResult = Failure("The DTLS stream stopped while completing the preview transition.");
                        }

                        while (!transitionFailed)
                        {
                            var now = DateTime.UtcNow;
                            if (now >= previewEndsAt)
                                break;

                            if (transitionOutSeconds > PluginConfiguration.MinColorPresetTransitionOutSeconds &&
                                now >= transitionOutStartsAt)
                            {
                                var remainingFadeOut = previewEndsAt - now;
                                await Task.Delay(
                                    remainingFadeOut > TimeSpan.FromMilliseconds(PreviewTransitionRefreshIntervalMs)
                                        ? TimeSpan.FromMilliseconds(PreviewTransitionRefreshIntervalMs)
                                        : remainingFadeOut,
                                    cancellationToken)
                                    .ConfigureAwait(false);

                                var fadeOutProgress = Math.Clamp(
                                    (DateTime.UtcNow - transitionOutStartsAt).TotalSeconds /
                                    transitionOutSeconds,
                                    0d,
                                    1d);
                                frame = BuildTransitionColors(channelColors, 1d - fadeOutProgress);
                                if (!await streamer.SendColors(
                                        areaId,
                                        frame,
                                        cancellationToken: cancellationToken).ConfigureAwait(false))
                                {
                                    transitionFailed = true;
                                    previewResult = Failure("The DTLS stream stopped while fading out the preview color.");
                                    break;
                                }

                                continue;
                            }

                            // Hue bridges deactivate an entertainment area after a period of
                            // inactivity. Refresh the static packet once per second so a longer
                            // preview remains visible for its full requested duration, stopping
                            // the hold refresh when the fade-out window begins.
                            var remainingHold = previewEndsAt - now;
                            if (transitionOutSeconds > PluginConfiguration.MinColorPresetTransitionOutSeconds)
                                remainingHold = TimeSpan.FromTicks(Math.Min(remainingHold.Ticks, (transitionOutStartsAt - now).Ticks));
                            if (remainingHold <= TimeSpan.Zero)
                                continue;

                            await Task.Delay(
                                remainingHold > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remainingHold,
                                cancellationToken)
                                .ConfigureAwait(false);
                            if (DateTime.UtcNow < transitionOutStartsAt &&
                                DateTime.UtcNow < previewEndsAt &&
                                !await streamer.SendColors(
                                    areaId,
                                    channelColors,
                                    cancellationToken: cancellationToken).ConfigureAwait(false))
                            {
                                previewResult = Failure("The DTLS stream stopped while holding the preview color.");
                                transitionFailed = true;
                                break;
                            }
                        }

                        if (!transitionFailed && transitionOutSeconds > PluginConfiguration.MinColorPresetTransitionOutSeconds &&
                            !await streamer.SendColors(
                                areaId,
                                BuildTransitionColors(channelColors, 0),
                                cancellationToken: cancellationToken).ConfigureAwait(false))
                        {
                            transitionFailed = true;
                            previewResult = Failure("The DTLS stream stopped while completing the preview fade-out.");
                        }

                        if (!transitionFailed)
                        {
                            var transitionMessage = transitionSeconds > PluginConfiguration.MinColorPresetTransitionSeconds &&
                                transitionOutSeconds > PluginConfiguration.MinColorPresetTransitionOutSeconds
                                ? $" with a {transitionSeconds}-second fade-in and a {transitionOutSeconds}-second fade-out."
                                : transitionSeconds > PluginConfiguration.MinColorPresetTransitionSeconds
                                    ? $" with a {transitionSeconds}-second fade-in."
                                    : transitionOutSeconds > PluginConfiguration.MinColorPresetTransitionOutSeconds
                                        ? $" with a {transitionOutSeconds}-second fade-out."
                                        : ".";
                            previewResult = new HueStreamProbeResult
                            {
                                Succeeded = true,
                                Message = $"Displayed the solid color preview for {durationSeconds} seconds across {channelColors.Count} channel(s){transitionMessage}"
                            };
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                previewResult = Failure("The solid color preview request was canceled; the bridge is being restored.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hue solid color preview failed for entertainment area {0}", areaId);
                previewResult = Failure("The solid color preview failed. Check the client key and OpenSSL diagnostics.");
            }
            finally
            {
                streamer.StopStream();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            previewResult = Failure("The solid color preview request was canceled; the bridge is being restored.");
        }
        finally
        {
            previewResult = await AddCleanupResultAsync(
                previewResult,
                bridgeIp,
                appKey,
                areaId,
                savedLightStates).ConfigureAwait(false);
        }

        return previewResult;
    }

    public bool CancelActiveDiagnostic()
    {
        lock (_activeOperationLock)
        {
            if (_activeOperationCancellation == null)
                return false;

            _activeOperationCancellation.Cancel();
            return true;
        }
    }

    private async Task<HueStreamProbeResult> RunSerializedAsync(
        Func<CancellationToken, Task<HueStreamProbeResult>> operation,
        CancellationToken requestCancellation)
    {
        var lifecycleLease = _bridgeLifecycleGate.TryEnterDiagnostic();
        if (lifecycleLease == null)
            return Failure(DiagnosticBusyMessage);

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation);
        lock (_activeOperationLock)
        {
            _activeOperationCancellation = operationCancellation;
        }

        try
        {
            return await operation(operationCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_activeOperationLock)
            {
                if (ReferenceEquals(_activeOperationCancellation, operationCancellation))
                    _activeOperationCancellation = null;
            }

            lifecycleLease.Dispose();
        }
    }

    private async Task<HueStreamProbeResult> AddCleanupResultAsync(
        HueStreamProbeResult probeResult,
        string bridgeIp,
        string appKey,
        string areaId,
        List<HueClient.LightState> savedLightStates)
    {
        var warnings = new List<string>();
        if (!await _hueClient.StopEntertainmentAreaWithResult(bridgeIp, appKey, areaId).ConfigureAwait(false))
            warnings.Add("The entertainment area could not be deactivated.");

        if (savedLightStates.Count > 0)
        {
            try
            {
                var restoreResult = await _hueClient.RestoreLightStatesWithResult(
                    bridgeIp,
                    appKey,
                    savedLightStates).ConfigureAwait(false);
                if (!restoreResult.Succeeded)
                {
                    warnings.Add(
                        $"Light restoration was incomplete: restored {restoreResult.RestoredCount} of {restoreResult.AttemptedCount} light(s); {restoreResult.FailedCount} failed.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not restore light state after Hue stream probe cleanup for area {0}", areaId);
                warnings.Add("Saved light state could not be restored.");
            }
        }

        if (warnings.Count == 0)
            return probeResult;

        var cleanupWarning = string.Join(" ", warnings);
        _logger.LogWarning("Hue stream probe cleanup warning for area {0}: {1}", areaId, cleanupWarning);
        return new HueStreamProbeResult
        {
            Succeeded = false,
            Message = $"{probeResult.Message} Cleanup warning: {cleanupWarning}",
            CleanupWarning = cleanupWarning
        };
    }

    internal static Dictionary<int, byte[]> BuildTransitionColors(
        IReadOnlyDictionary<int, byte[]> targetColors,
        double progress)
    {
        ArgumentNullException.ThrowIfNull(targetColors);
        var boundedProgress = Math.Clamp(progress, 0d, 1d);
        var colors = new Dictionary<int, byte[]>(targetColors.Count);
        foreach (var (channelId, target) in targetColors)
        {
            if (target == null || target.Length != 6)
                throw new ArgumentException("Each Hue transition color must contain six RGB16 bytes.", nameof(targetColors));

            var frame = new byte[target.Length];
            for (var index = 0; index < target.Length; index++)
            {
                frame[index] = (byte)Math.Clamp(
                    (int)Math.Round(target[index] * boundedProgress, MidpointRounding.AwayFromZero),
                    byte.MinValue,
                    byte.MaxValue);
            }

            colors[channelId] = frame;
        }

        return colors;
    }

    internal static bool TryBuildProbeColors(JsonElement areaConfiguration, out Dictionary<int, byte[]> channelColors)
        => TryBuildProbeColors(areaConfiguration, null, out channelColors);

    internal static bool TryBuildProbeColors(
        JsonElement areaConfiguration,
        IReadOnlySet<int>? channelIds,
        out Dictionary<int, byte[]> channelColors)
    {
        channelColors = new Dictionary<int, byte[]>();
        if (!areaConfiguration.TryGetProperty("channels", out var channels) ||
            channels.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var availableChannelIds = new HashSet<int>();
        foreach (var channel in channels.EnumerateArray())
        {
            if (!channel.TryGetProperty("channel_id", out var channelIdProperty) ||
                channelIdProperty.ValueKind != JsonValueKind.Number ||
                !channelIdProperty.TryGetInt32(out var channelId) ||
                channelId < 0 ||
                channelId > ushort.MaxValue)
            {
                channelColors.Clear();
                return false;
            }

            availableChannelIds.Add(channelId);
            if (channelIds != null && !channelIds.Contains(channelId))
                continue;

            channelColors[channelId] = new[]
            {
                ProbeColor, ProbeColor,
                ProbeColor, ProbeColor,
                ProbeColor, ProbeColor
            };
        }

        return channelColors.Count > 0 &&
            (channelIds == null || channelIds.All(availableChannelIds.Contains));
    }

    internal static bool TryBuildSolidColors(
        JsonElement areaConfiguration,
        IReadOnlySet<int>? channelIds,
        int red,
        int green,
        int blue,
        int brightnessPercent,
        out Dictionary<int, byte[]> channelColors)
    {
        channelColors = new Dictionary<int, byte[]>();
        if (red < 0 || red > 255 ||
            green < 0 || green > 255 ||
            blue < 0 || blue > 255 ||
            brightnessPercent < 0 || brightnessPercent > 100)
        {
            return false;
        }

        if (!areaConfiguration.TryGetProperty("channels", out var channels) ||
            channels.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var availableChannelIds = new HashSet<int>();
        var red16 = ToHueColorComponent(red, brightnessPercent);
        var green16 = ToHueColorComponent(green, brightnessPercent);
        var blue16 = ToHueColorComponent(blue, brightnessPercent);
        foreach (var channel in channels.EnumerateArray())
        {
            if (!channel.TryGetProperty("channel_id", out var channelIdProperty) ||
                channelIdProperty.ValueKind != JsonValueKind.Number ||
                !channelIdProperty.TryGetInt32(out var channelId) ||
                channelId < 0 ||
                channelId > ushort.MaxValue)
            {
                channelColors.Clear();
                return false;
            }

            availableChannelIds.Add(channelId);
            if (channelIds != null && !channelIds.Contains(channelId))
                continue;

            channelColors[channelId] = new[]
            {
                red16, red16,
                green16, green16,
                blue16, blue16
            };
        }

        return channelColors.Count > 0 &&
            (channelIds == null || channelIds.All(availableChannelIds.Contains));
    }

    internal static byte ToHueColorComponent(int component, int brightnessPercent)
    {
        if (component < 0 || component > 255 || brightnessPercent < 0 || brightnessPercent > 100)
            throw new ArgumentOutOfRangeException();

        // Hue's entertainment protocol uses the high byte of each 16-bit RGB
        // component. Match the runtime's divide-by-two conversion while applying
        // the preview brightness ceiling first.
        return (byte)Math.Clamp((int)(component * brightnessPercent / 100d / 2d), 0, 127);
    }

    private static HueStreamProbeResult Failure(string message) => new()
    {
        Succeeded = false,
        Message = message
    };
}
