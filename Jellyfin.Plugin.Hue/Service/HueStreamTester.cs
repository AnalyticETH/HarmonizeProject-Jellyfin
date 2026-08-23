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
        int transitionOutSeconds = PluginConfiguration.MinColorPresetTransitionOutSeconds,
        string effect = PluginConfiguration.ColorPresetEffectSolid,
        int effectSpeedPercent = PluginConfiguration.DefaultColorPresetEffectSpeedPercent);

    /// <summary>
    /// Cancels the active diagnostic or preview, if one is running. Cleanup continues
    /// through the normal state-restoring lifecycle.
    /// </summary>
    bool CancelActiveDiagnostic();
}

/// <summary>
/// Optional target-scoped preview capability used by scheduled cues when the
/// administrator enables matching-target playback conflict arbitration. Keeping this
/// separate from <see cref="IHueStreamTester"/> preserves compatibility with existing
/// test and extension implementations that only provide the original preview contract.
/// </summary>
internal interface IHueTargetScopedStreamTester
{
    Task<HueStreamProbeResult> PreviewAsyncForTarget(
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
        int transitionOutSeconds = PluginConfiguration.MinColorPresetTransitionOutSeconds,
        string effect = PluginConfiguration.ColorPresetEffectSolid,
        int effectSpeedPercent = PluginConfiguration.DefaultColorPresetEffectSpeedPercent);
}

/// <summary>
/// Optional transition-curve preview capability. Keeping this additive preserves the
/// original stream-tester contract for extensions and test doubles that predate easing
/// curves; callers fall back to the original linear preview when it is unavailable.
/// </summary>
internal interface IHueTransitionCurveStreamTester
{
    Task<HueStreamProbeResult> PreviewAsyncWithTransitionCurve(
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
        int transitionOutSeconds = PluginConfiguration.MinColorPresetTransitionOutSeconds,
        string effect = PluginConfiguration.ColorPresetEffectSolid,
        int effectSpeedPercent = PluginConfiguration.DefaultColorPresetEffectSpeedPercent,
        string transitionCurve = PluginConfiguration.ColorPresetTransitionCurveLinear);

    Task<HueStreamProbeResult> PreviewAsyncForTargetWithTransitionCurve(
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
        int transitionOutSeconds = PluginConfiguration.MinColorPresetTransitionOutSeconds,
        string effect = PluginConfiguration.ColorPresetEffectSolid,
        int effectSpeedPercent = PluginConfiguration.DefaultColorPresetEffectSpeedPercent,
        string transitionCurve = PluginConfiguration.ColorPresetTransitionCurveLinear);
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
public sealed class HueStreamTester : IHueStreamTester, IHueTargetScopedStreamTester, IHueTransitionCurveStreamTester
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
    /// Displays a bounded preview through the same save/activate/DTLS/restore
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
        int transitionOutSeconds = PluginConfiguration.MinColorPresetTransitionOutSeconds,
        string effect = PluginConfiguration.ColorPresetEffectSolid,
        int effectSpeedPercent = PluginConfiguration.DefaultColorPresetEffectSpeedPercent)
        => RunPreviewAsync(
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
            effect,
            effectSpeedPercent,
            PluginConfiguration.ColorPresetTransitionCurveLinear,
            cancellationToken,
            resourceKey: null);

    public Task<HueStreamProbeResult> PreviewAsyncWithTransitionCurve(
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
        int transitionOutSeconds = PluginConfiguration.MinColorPresetTransitionOutSeconds,
        string effect = PluginConfiguration.ColorPresetEffectSolid,
        int effectSpeedPercent = PluginConfiguration.DefaultColorPresetEffectSpeedPercent,
        string transitionCurve = PluginConfiguration.ColorPresetTransitionCurveLinear)
        => RunPreviewAsync(
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
            effect,
            effectSpeedPercent,
            transitionCurve,
            cancellationToken,
            resourceKey: null);

    /// <summary>
    /// Runs a restorative preview while reserving only the selected bridge/area
    /// resource. This allows an automatic cue for an independent room to proceed while
    /// another room has active playback.
    /// </summary>
    public Task<HueStreamProbeResult> PreviewAsyncForTarget(
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
        int transitionOutSeconds = PluginConfiguration.MinColorPresetTransitionOutSeconds,
        string effect = PluginConfiguration.ColorPresetEffectSolid,
        int effectSpeedPercent = PluginConfiguration.DefaultColorPresetEffectSpeedPercent)
        => RunPreviewAsync(
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
            effect,
            effectSpeedPercent,
            PluginConfiguration.ColorPresetTransitionCurveLinear,
            cancellationToken,
            HueSyncService.GetPlaybackResourceKey(bridgeIp, areaId));

    public Task<HueStreamProbeResult> PreviewAsyncForTargetWithTransitionCurve(
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
        int transitionOutSeconds = PluginConfiguration.MinColorPresetTransitionOutSeconds,
        string effect = PluginConfiguration.ColorPresetEffectSolid,
        int effectSpeedPercent = PluginConfiguration.DefaultColorPresetEffectSpeedPercent,
        string transitionCurve = PluginConfiguration.ColorPresetTransitionCurveLinear)
        => RunPreviewAsync(
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
            effect,
            effectSpeedPercent,
            transitionCurve,
            cancellationToken,
            HueSyncService.GetPlaybackResourceKey(bridgeIp, areaId));

    private Task<HueStreamProbeResult> RunPreviewAsync(
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
        string effect,
        int effectSpeedPercent,
        string transitionCurve,
        CancellationToken cancellationToken,
        string? resourceKey)
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
            effect,
            effectSpeedPercent,
            transitionCurve,
            operationCancellation), cancellationToken, resourceKey);

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
        string effect,
        int effectSpeedPercent,
        string transitionCurve,
        CancellationToken cancellationToken)
    {
        if (!PluginConfiguration.TryNormalizeColorPresetEffect(effect, out var normalizedEffect))
            return Failure("Preview effect must be Solid, Pulse, Rainbow, Candle, Temperature, Aurora, Fire, Ocean, Lightning, or Starlight.");

        effect = normalizedEffect;
        if (!PluginConfiguration.TryNormalizeColorPresetTransitionCurve(transitionCurve, out var normalizedTransitionCurve))
            return Failure("Preview transition curve must be Linear, SmoothStep, EaseIn, EaseOut, or EaseInOut.");

        transitionCurve = normalizedTransitionCurve;
        if (cancellationToken.IsCancellationRequested)
            return Failure($"The {effect.ToLowerInvariant()} preview request was canceled.");

        if (effectSpeedPercent < PluginConfiguration.MinColorPresetEffectSpeedPercent ||
            effectSpeedPercent > PluginConfiguration.MaxColorPresetEffectSpeedPercent)
        {
            return Failure($"Preview effect speed must be between {PluginConfiguration.MinColorPresetEffectSpeedPercent} and {PluginConfiguration.MaxColorPresetEffectSpeedPercent} percent.");
        }

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
            return Failure($"The {effect.ToLowerInvariant()} preview request was canceled before activation.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save light state before Hue color preview for area {0}", areaId);
            return Failure("The preview could not save the current light state safely.");
        }

        if (cancellationToken.IsCancellationRequested)
            return Failure($"The {effect.ToLowerInvariant()} preview request was canceled before activation.");

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
                Failure($"The {effect.ToLowerInvariant()} preview request was canceled during activation; the bridge is being restored."),
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
                Failure($"The Hue bridge could not activate the entertainment area for {effect.ToLowerInvariant()} preview; the bridge is being restored."),
                bridgeIp,
                appKey,
                areaId,
                savedLightStates).ConfigureAwait(false);
        }

        var previewResult = Failure($"The {effect.ToLowerInvariant()} preview did not complete.");
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
                    var animated = !string.Equals(
                        effect,
                        PluginConfiguration.ColorPresetEffectSolid,
                        StringComparison.OrdinalIgnoreCase);
                    var frame = BuildEffectColors(
                        channelColors,
                        effect,
                        0,
                        durationSeconds,
                        effectSpeedPercent);
                    if (transitionSeconds > PluginConfiguration.MinColorPresetTransitionSeconds)
                        frame = BuildTransitionColors(frame, 0, transitionCurve);
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
                            var elapsedSeconds = (DateTime.UtcNow - previewStartedAt).TotalSeconds;
                            frame = BuildTransitionColors(
                                BuildEffectColors(channelColors, effect, elapsedSeconds, durationSeconds, effectSpeedPercent),
                                progress,
                                transitionCurve);
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
                                BuildEffectColors(
                                    channelColors,
                                    effect,
                                    (DateTime.UtcNow - previewStartedAt).TotalSeconds,
                                    durationSeconds,
                                    effectSpeedPercent),
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
                                frame = BuildTransitionColors(
                                    BuildEffectColors(
                                        channelColors,
                                        effect,
                                        (DateTime.UtcNow - previewStartedAt).TotalSeconds,
                                        durationSeconds,
                                        effectSpeedPercent),
                                    1d - fadeOutProgress,
                                    transitionCurve);
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
                            // inactivity. Refresh static scenes once per second and animated
                            // scenes every 100ms so a longer preview remains visible for its
                            // full requested duration, stopping the hold refresh when the
                            // fade-out window begins.
                            var remainingHold = previewEndsAt - now;
                            if (transitionOutSeconds > PluginConfiguration.MinColorPresetTransitionOutSeconds)
                                remainingHold = TimeSpan.FromTicks(Math.Min(remainingHold.Ticks, (transitionOutStartsAt - now).Ticks));
                            if (remainingHold <= TimeSpan.Zero)
                                continue;

                            var refreshInterval = animated
                                ? TimeSpan.FromMilliseconds(PreviewTransitionRefreshIntervalMs)
                                : TimeSpan.FromSeconds(1);
                            await Task.Delay(
                                remainingHold > refreshInterval ? refreshInterval : remainingHold,
                                cancellationToken)
                                .ConfigureAwait(false);
                            if (DateTime.UtcNow < transitionOutStartsAt &&
                                DateTime.UtcNow < previewEndsAt &&
                                !await streamer.SendColors(
                                    areaId,
                                    BuildEffectColors(
                                        channelColors,
                                        effect,
                                        (DateTime.UtcNow - previewStartedAt).TotalSeconds,
                                        durationSeconds,
                                        effectSpeedPercent),
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
                                BuildTransitionColors(
                                    BuildEffectColors(channelColors, effect, durationSeconds, durationSeconds, effectSpeedPercent),
                                    0,
                                    transitionCurve),
                                cancellationToken: cancellationToken).ConfigureAwait(false))
                        {
                            transitionFailed = true;
                            previewResult = Failure("The DTLS stream stopped while completing the preview fade-out.");
                        }

                        if (!transitionFailed)
                        {
                            var transitionMessage = transitionSeconds > PluginConfiguration.MinColorPresetTransitionSeconds &&
                                transitionOutSeconds > PluginConfiguration.MinColorPresetTransitionOutSeconds
                                ? $" with a {transitionSeconds}-second {transitionCurve} fade-in and a {transitionOutSeconds}-second {transitionCurve} fade-out."
                                : transitionSeconds > PluginConfiguration.MinColorPresetTransitionSeconds
                                    ? $" with a {transitionSeconds}-second {transitionCurve} fade-in."
                                    : transitionOutSeconds > PluginConfiguration.MinColorPresetTransitionOutSeconds
                                        ? $" with a {transitionOutSeconds}-second {transitionCurve} fade-out."
                                        : ".";
                            var effectDescription = string.Equals(
                                effect,
                                PluginConfiguration.ColorPresetEffectSolid,
                                StringComparison.OrdinalIgnoreCase)
                                ? "solid color"
                                : $"{effect.ToLowerInvariant()} effect";
                            previewResult = new HueStreamProbeResult
                            {
                                Succeeded = true,
                                Message = $"Displayed the {effectDescription} preview for {durationSeconds} seconds across {channelColors.Count} channel(s){transitionMessage}"
                            };
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                previewResult = Failure($"The {effect.ToLowerInvariant()} preview request was canceled; the bridge is being restored.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hue {0} preview failed for entertainment area {1}", effect, areaId);
                previewResult = Failure($"The {effect} preview failed. Check the client key and OpenSSL diagnostics.");
            }
            finally
            {
                streamer.StopStream();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            previewResult = Failure($"The {effect.ToLowerInvariant()} preview request was canceled; the bridge is being restored.");
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
        CancellationToken requestCancellation,
        string? resourceKey = null)
    {
        var lifecycleLease = _bridgeLifecycleGate.TryEnterDiagnostic(resourceKey);
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
        => BuildTransitionColors(
            targetColors,
            progress,
            PluginConfiguration.ColorPresetTransitionCurveLinear);

    internal static Dictionary<int, byte[]> BuildTransitionColors(
        IReadOnlyDictionary<int, byte[]> targetColors,
        double progress,
        string transitionCurve)
    {
        ArgumentNullException.ThrowIfNull(targetColors);
        var boundedProgress = Math.Clamp(progress, 0d, 1d);
        if (!PluginConfiguration.TryNormalizeColorPresetTransitionCurve(transitionCurve, out var normalizedCurve))
            throw new ArgumentException("Transition curve must be Linear, SmoothStep, EaseIn, EaseOut, or EaseInOut.", nameof(transitionCurve));

        boundedProgress = ApplyTransitionCurve(boundedProgress, normalizedCurve);
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

    internal static double ApplyTransitionCurve(double progress, string transitionCurve)
    {
        var boundedProgress = Math.Clamp(progress, 0d, 1d);
        if (!PluginConfiguration.TryNormalizeColorPresetTransitionCurve(transitionCurve, out var normalizedCurve))
            throw new ArgumentException("Transition curve must be Linear, SmoothStep, EaseIn, EaseOut, or EaseInOut.", nameof(transitionCurve));

        return normalizedCurve switch
        {
            PluginConfiguration.ColorPresetTransitionCurveSmoothStep => boundedProgress * boundedProgress * (3d - (2d * boundedProgress)),
            PluginConfiguration.ColorPresetTransitionCurveEaseIn => boundedProgress * boundedProgress,
            PluginConfiguration.ColorPresetTransitionCurveEaseOut => 1d - Math.Pow(1d - boundedProgress, 2d),
            PluginConfiguration.ColorPresetTransitionCurveEaseInOut => boundedProgress < 0.5d
                ? 2d * boundedProgress * boundedProgress
                : 1d - (Math.Pow((-2d * boundedProgress) + 2d, 2d) / 2d),
            _ => boundedProgress
        };
    }

    /// <summary>
    /// Builds one deterministic frame for a saved-scene effect. Every effect remains
    /// bounded to the selected RGB16 target and is safe to call at the preview refresh
    /// cadence. Solid returns a clone so callers can freely apply fade transitions.
    /// </summary>
    internal static Dictionary<int, byte[]> BuildEffectColors(
        IReadOnlyDictionary<int, byte[]> targetColors,
        string effect,
        double elapsedSeconds,
        double durationSeconds,
        int effectSpeedPercent = PluginConfiguration.DefaultColorPresetEffectSpeedPercent)
    {
        ArgumentNullException.ThrowIfNull(targetColors);
        if (!PluginConfiguration.TryNormalizeColorPresetEffect(effect, out var normalizedEffect))
            throw new ArgumentException("Effect must be Solid, Pulse, Rainbow, Candle, Temperature, Aurora, Fire, Ocean, Lightning, or Starlight.", nameof(effect));

        var elapsed = Math.Max(0d, elapsedSeconds);
        var duration = Math.Max(1d, durationSeconds);
        var speedMultiplier = Math.Clamp(
            effectSpeedPercent,
            PluginConfiguration.MinColorPresetEffectSpeedPercent,
            PluginConfiguration.MaxColorPresetEffectSpeedPercent) / 100d;
        var colors = new Dictionary<int, byte[]>(targetColors.Count);
        foreach (var (channelId, target) in targetColors)
        {
            if (target == null || target.Length != 6)
                throw new ArgumentException("Each Hue effect color must contain six RGB16 bytes.", nameof(targetColors));

            if (string.Equals(normalizedEffect, PluginConfiguration.ColorPresetEffectSolid, StringComparison.Ordinal))
            {
                colors[channelId] = (byte[])target.Clone();
                continue;
            }

            if (string.Equals(normalizedEffect, PluginConfiguration.ColorPresetEffectPulse, StringComparison.Ordinal))
            {
                var pulsePeriod = 2.4d / speedMultiplier;
                var phase = (elapsed % pulsePeriod) / pulsePeriod;
                var wave = 0.5d + 0.5d * Math.Sin((phase * 2d * Math.PI) - (Math.PI / 2d));
                var multiplier = 0.2d + (0.8d * wave);
                colors[channelId] = ScaleFrame(target, multiplier);
                continue;
            }

            if (string.Equals(normalizedEffect, PluginConfiguration.ColorPresetEffectCandle, StringComparison.Ordinal))
            {
                var candleRed = Math.Clamp(target[0] / 127d, 0d, 1d);
                var candleGreen = Math.Clamp(target[2] / 127d, 0d, 1d);
                var candleBlue = Math.Clamp(target[4] / 127d, 0d, 1d);
                var candleValue = Math.Clamp(Math.Max(candleRed, Math.Max(candleGreen, candleBlue)), 0d, 1d);
                const double warmMix = 0.45d;
                // A candle remains deterministic and bounded while shifting the selected
                // seed toward a warm amber palette. Each channel receives a different
                // phase so a multi-light area flickers naturally instead of in lockstep.
                candleRed = Math.Max(candleRed, candleValue * 0.95d);
                candleGreen = (candleGreen * (1d - warmMix)) + (candleValue * 0.62d * warmMix);
                candleBlue = (candleBlue * (1d - warmMix)) + (candleValue * 0.14d * warmMix);
                var flicker = 0.62d +
                    (0.28d * (0.5d + 0.5d * Math.Sin((elapsed * 5.5d * speedMultiplier) + (channelId * 0.731d)))) +
                    (0.10d * (0.5d + 0.5d * Math.Sin((elapsed * 13d * speedMultiplier) + (channelId * 1.17d) + 0.9d)));
                colors[channelId] = new[]
                {
                    ToRgb16Byte(candleRed * flicker), ToRgb16Byte(candleRed * flicker),
                    ToRgb16Byte(candleGreen * flicker), ToRgb16Byte(candleGreen * flicker),
                    ToRgb16Byte(candleBlue * flicker), ToRgb16Byte(candleBlue * flicker)
                };
                continue;
            }

            if (string.Equals(normalizedEffect, PluginConfiguration.ColorPresetEffectTemperature, StringComparison.Ordinal))
            {
                // Sweep through a warm candle-like 2200 K white to a cool 6500 K
                // daylight white and back. The seed frame supplies the output level,
                // while HueColorMath keeps the conversion consistent with captured
                // mirek states. Channel phase prevents a multi-channel room from
                // changing in lockstep while remaining deterministic.
                var temperaturePeriod = 8d / speedMultiplier;
                var temperaturePhase = ((elapsed + (channelId * 0.11d)) % temperaturePeriod) / temperaturePeriod;
                var temperatureWave = 0.5d - (0.5d * Math.Cos(temperaturePhase * 2d * Math.PI));
                var kelvin = 2200d + (4300d * temperatureWave);
                var mirek = (int)Math.Round(1_000_000d / kelvin, MidpointRounding.AwayFromZero);
                if (!HueColorMath.TryConvertMirekToRgb(mirek, out var temperatureRed, out var temperatureGreen, out var temperatureBlue))
                    throw new InvalidOperationException("Could not convert the Temperature effect color.");

                var intensity = Math.Clamp(
                    Math.Max(target[0], Math.Max(target[2], target[4])) / 127d,
                    0d,
                    1d);
                colors[channelId] = new[]
                {
                    ToRgb16Byte(temperatureRed * intensity), ToRgb16Byte(temperatureRed * intensity),
                    ToRgb16Byte(temperatureGreen * intensity), ToRgb16Byte(temperatureGreen * intensity),
                    ToRgb16Byte(temperatureBlue * intensity), ToRgb16Byte(temperatureBlue * intensity)
                };
                continue;
            }

            if (string.Equals(normalizedEffect, PluginConfiguration.ColorPresetEffectAurora, StringComparison.Ordinal))
            {
                // Drift through the green, cyan, blue, and violet band used by an
                // aurora. The seed frame controls output intensity while independent
                // channel phases create a calm multi-light wave instead of lockstep
                // color changes. The result is deterministic and bounded like every
                // other saved-scene effect.
                var auroraPeriod = 10d / speedMultiplier;
                var auroraPhase = ((elapsed + (channelId * 0.17d)) % auroraPeriod) / auroraPeriod;
                var auroraWave = 0.5d + (0.5d * Math.Sin(auroraPhase * 2d * Math.PI));
                var auroraHue = 70d + (180d * auroraWave);
                var auroraSaturation = 0.72d + (0.18d * (0.5d + (0.5d * Math.Sin((auroraPhase * 4d * Math.PI) + (channelId * 0.41d)))));
                var seedValue = Math.Clamp(
                    Math.Max(target[0], Math.Max(target[2], target[4])) / 127d,
                    0d,
                    1d);
                var auroraValue = seedValue * (0.72d + (0.28d * (0.5d + (0.5d * Math.Sin((auroraPhase * 2d * Math.PI) + 0.6d)))));
                var (auroraRed, auroraGreen, auroraBlue) = HsvToRgb(
                    auroraHue,
                    Math.Clamp(auroraSaturation, 0d, 1d),
                    Math.Clamp(auroraValue, 0d, 1d));
                colors[channelId] = new[]
                {
                    ToRgb16Byte(auroraRed), ToRgb16Byte(auroraRed),
                    ToRgb16Byte(auroraGreen), ToRgb16Byte(auroraGreen),
                    ToRgb16Byte(auroraBlue), ToRgb16Byte(auroraBlue)
                };
                continue;
            }

            if (string.Equals(normalizedEffect, PluginConfiguration.ColorPresetEffectFire, StringComparison.Ordinal))
            {
                // Fire uses a saturated red/amber/yellow palette with two bounded
                // flicker frequencies. The seed frame controls intensity while the
                // channel phase keeps a multi-light room from flickering in lockstep.
                var firePeriod = 3.6d / speedMultiplier;
                var firePhase = ((elapsed + (channelId * 0.23d)) % firePeriod) / firePeriod;
                var fireWave = 0.5d + (0.5d * Math.Sin(firePhase * 2d * Math.PI));
                var fireFlicker = 0.58d +
                    (0.27d * fireWave) +
                    (0.15d * (0.5d + (0.5d * Math.Sin((firePhase * 6d * Math.PI) + (channelId * 0.77d)))));
                var fireHue = 4d + (42d * fireWave);
                var fireSaturation = 0.86d + (0.10d * (0.5d + (0.5d * Math.Sin((firePhase * 4d * Math.PI) + 0.4d))));
                var fireSeedValue = Math.Clamp(
                    Math.Max(target[0], Math.Max(target[2], target[4])) / 127d,
                    0d,
                    1d);
                var (fireRed, fireGreen, fireBlue) = HsvToRgb(
                    fireHue,
                    Math.Clamp(fireSaturation, 0d, 1d),
                    Math.Clamp(fireSeedValue * fireFlicker, 0d, 1d));
                colors[channelId] = new[]
                {
                    ToRgb16Byte(fireRed), ToRgb16Byte(fireRed),
                    ToRgb16Byte(fireGreen), ToRgb16Byte(fireGreen),
                    ToRgb16Byte(fireBlue), ToRgb16Byte(fireBlue)
                };
                continue;
            }

            if (string.Equals(normalizedEffect, PluginConfiguration.ColorPresetEffectOcean, StringComparison.Ordinal))
            {
                // Ocean rolls through deep blue and cyan with a slow wave. The seed
                // frame controls output level and independent phase keeps the room
                // spatially alive while remaining deterministic and bounded.
                var oceanPeriod = 12d / speedMultiplier;
                var oceanPhase = ((elapsed + (channelId * 0.19d)) % oceanPeriod) / oceanPeriod;
                var oceanWave = 0.5d + (0.5d * Math.Sin(oceanPhase * 2d * Math.PI));
                var oceanHue = 184d + (34d * oceanWave);
                var oceanSaturation = 0.76d + (0.16d * (0.5d + (0.5d * Math.Sin((oceanPhase * 2d * Math.PI) + 0.8d))));
                var oceanSeedValue = Math.Clamp(
                    Math.Max(target[0], Math.Max(target[2], target[4])) / 127d,
                    0d,
                    1d);
                var oceanValue = oceanSeedValue * (0.70d + (0.30d * oceanWave));
                var (oceanRed, oceanGreen, oceanBlue) = HsvToRgb(
                    oceanHue,
                    Math.Clamp(oceanSaturation, 0d, 1d),
                    Math.Clamp(oceanValue, 0d, 1d));
                colors[channelId] = new[]
                {
                    ToRgb16Byte(oceanRed), ToRgb16Byte(oceanRed),
                    ToRgb16Byte(oceanGreen), ToRgb16Byte(oceanGreen),
                    ToRgb16Byte(oceanBlue), ToRgb16Byte(oceanBlue)
                };
                continue;
            }

            if (string.Equals(normalizedEffect, PluginConfiguration.ColorPresetEffectLightning, StringComparison.Ordinal))
            {
                // Lightning builds a dark electric-blue baseline with two narrow,
                // independent flash envelopes. The seed frame controls intensity,
                // while channel phase keeps a multi-light room from flashing in lockstep.
                var lightningPeriod = 6.5d / speedMultiplier;
                var lightningPhase = ((elapsed + (channelId * 0.31d)) % lightningPeriod) / lightningPeriod;
                var primaryFlash = Math.Pow(
                    Math.Max(0d, Math.Sin((lightningPhase * 2d * Math.PI) + 0.65d)),
                    18d);
                var secondaryFlash = Math.Pow(
                    Math.Max(0d, Math.Sin((lightningPhase * 4d * Math.PI) + (channelId * 0.53d) + 1.2d)),
                    28d);
                var flash = Math.Clamp(0.06d + (0.68d * primaryFlash) + (0.26d * secondaryFlash), 0d, 1d);
                var lightningHue = 210d + (30d * (0.5d + (0.5d * Math.Sin((lightningPhase * 2d * Math.PI) + 0.3d))));
                var lightningSaturation = 0.92d - (0.82d * flash);
                var lightningSeedValue = Math.Clamp(
                    Math.Max(target[0], Math.Max(target[2], target[4])) / 127d,
                    0d,
                    1d);
                var lightningValue = lightningSeedValue * (0.08d + (0.92d * flash));
                var (lightningRed, lightningGreen, lightningBlue) = HsvToRgb(
                    lightningHue,
                    Math.Clamp(lightningSaturation, 0d, 1d),
                    Math.Clamp(lightningValue, 0d, 1d));
                colors[channelId] = new[]
                {
                    ToRgb16Byte(lightningRed), ToRgb16Byte(lightningRed),
                    ToRgb16Byte(lightningGreen), ToRgb16Byte(lightningGreen),
                    ToRgb16Byte(lightningBlue), ToRgb16Byte(lightningBlue)
                };
                continue;
            }

            if (string.Equals(normalizedEffect, PluginConfiguration.ColorPresetEffectStarlight, StringComparison.Ordinal))
            {
                // Starlight twinkles from deep blue to cool white. The seed frame
                // controls intensity, while a sharp glint envelope and per-channel
                // phase make each light shimmer independently without randomness.
                var starlightPeriod = 7.5d / speedMultiplier;
                var starlightPhase = ((elapsed + (channelId * 0.27d)) % starlightPeriod) / starlightPeriod;
                var starWave = 0.5d + (0.5d * Math.Sin(starlightPhase * 2d * Math.PI));
                var glint = Math.Pow(starWave, 6d);
                var drift = 0.5d + (0.5d * Math.Sin((starlightPhase * 4d * Math.PI) + (channelId * 0.61d)));
                var starlightHue = 205d + (25d * drift);
                var starlightSaturation = 0.68d - (0.58d * glint);
                var starlightSeedValue = Math.Clamp(
                    Math.Max(target[0], Math.Max(target[2], target[4])) / 127d,
                    0d,
                    1d);
                var starlightValue = starlightSeedValue * (0.18d + (0.82d * glint));
                var (starlightRed, starlightGreen, starlightBlue) = HsvToRgb(
                    starlightHue,
                    Math.Clamp(starlightSaturation, 0d, 1d),
                    Math.Clamp(starlightValue, 0d, 1d));
                colors[channelId] = new[]
                {
                    ToRgb16Byte(starlightRed), ToRgb16Byte(starlightRed),
                    ToRgb16Byte(starlightGreen), ToRgb16Byte(starlightGreen),
                    ToRgb16Byte(starlightBlue), ToRgb16Byte(starlightBlue)
                };
                continue;
            }

            var red = target[0] / 127d;
            var green = target[2] / 127d;
            var blue = target[4] / 127d;
            var value = Math.Clamp(Math.Max(red, Math.Max(green, blue)), 0d, 1d);
            var baseHue = RgbToHue(red, green, blue);
            var hue = (baseHue + ((elapsed / duration) * 360d * speedMultiplier)) % 360d;
            var (rainbowRed, rainbowGreen, rainbowBlue) = HsvToRgb(hue, 1d, value);
            colors[channelId] = new[]
            {
                ToRgb16Byte(rainbowRed), ToRgb16Byte(rainbowRed),
                ToRgb16Byte(rainbowGreen), ToRgb16Byte(rainbowGreen),
                ToRgb16Byte(rainbowBlue), ToRgb16Byte(rainbowBlue)
            };
        }

        return colors;
    }

    private static byte[] ScaleFrame(IReadOnlyList<byte> target, double multiplier)
    {
        var frame = new byte[target.Count];
        for (var index = 0; index < target.Count; index++)
        {
            frame[index] = (byte)Math.Clamp(
                (int)Math.Round(target[index] * multiplier, MidpointRounding.AwayFromZero),
                byte.MinValue,
                byte.MaxValue);
        }

        return frame;
    }

    private static double RgbToHue(double red, double green, double blue)
    {
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var delta = max - min;
        if (delta <= double.Epsilon)
            return 0d;

        var hue = max == red
            ? 60d * (((green - blue) / delta) % 6d)
            : max == green
                ? 60d * (((blue - red) / delta) + 2d)
                : 60d * (((red - green) / delta) + 4d);
        return hue < 0d ? hue + 360d : hue;
    }

    private static (double Red, double Green, double Blue) HsvToRgb(
        double hue,
        double saturation,
        double value)
    {
        var chroma = value * saturation;
        var intermediate = chroma * (1d - Math.Abs(((hue / 60d) % 2d) - 1d));
        var match = value - chroma;
        var (red, green, blue) = hue switch
        {
            < 60d => (chroma, intermediate, 0d),
            < 120d => (intermediate, chroma, 0d),
            < 180d => (0d, chroma, intermediate),
            < 240d => (0d, intermediate, chroma),
            < 300d => (intermediate, 0d, chroma),
            _ => (chroma, 0d, intermediate)
        };
        return (red + match, green + match, blue + match);
    }

    private static byte ToRgb16Byte(double component)
        => (byte)Math.Clamp(
            (int)Math.Round(Math.Clamp(component, 0d, 1d) * 127d, MidpointRounding.AwayFromZero),
            byte.MinValue,
            byte.MaxValue);

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
