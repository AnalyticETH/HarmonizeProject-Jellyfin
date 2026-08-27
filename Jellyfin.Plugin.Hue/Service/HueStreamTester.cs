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
/// Optional capability used by scheduled cues to isolate the retry policy for each
/// target. The public tester contract remains unchanged for older extensions and test
/// doubles; the built-in implementation creates a lightweight tester over a cloned
/// HueClient so concurrent rooms cannot overwrite one another's retry state.
/// </summary>
internal interface IHueRetryAwareStreamTester
{
    IHueStreamTester CreateForRetryAttempts(int retryAttempts);
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
/// Optional continuous-playlist preview capability. Implementations keep the complete
/// ordered step plan inside one save/activate/DTLS/restore lifecycle for a target. The
/// original <see cref="IHueStreamTester"/> contract remains unchanged so older test
/// doubles and extensions can continue to use per-step previews as a fallback.
/// </summary>
internal interface IHuePlaylistStreamTester
{
    Task<HuePlaylistStreamProbeResult> PreviewPlaylistAsync(
        string bridgeIp,
        string appKey,
        string clientKey,
        string areaId,
        JsonElement areaConfiguration,
        IReadOnlySet<int>? channelIds,
        IReadOnlyList<HuePlaylistPreviewStep> steps,
        CancellationToken cancellationToken = default);

    Task<HuePlaylistStreamProbeResult> PreviewPlaylistAsyncForTarget(
        string bridgeIp,
        string appKey,
        string clientKey,
        string areaId,
        JsonElement areaConfiguration,
        IReadOnlySet<int>? channelIds,
        IReadOnlyList<HuePlaylistPreviewStep> steps,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Credential-free, fully effective input for one step in a continuous playlist stream.
/// Callers resolve saved-scene inheritance, repeat order, and shuffle order before invoking
/// the stream tester so the complete plan can be validated before any bridge mutation.
/// </summary>
public sealed class HuePlaylistPreviewStep
{
    public int Index { get; init; }
    public int Red { get; init; }
    public int Green { get; init; }
    public int Blue { get; init; }
    public int BrightnessPercent { get; init; }
    public int DurationSeconds { get; init; }
    public int TransitionSeconds { get; init; }
    public int TransitionOutSeconds { get; init; }
    public string Effect { get; init; } = PluginConfiguration.ColorPresetEffectSolid;
    public int EffectSpeedPercent { get; init; } = PluginConfiguration.DefaultColorPresetEffectSpeedPercent;
    public string TransitionCurve { get; init; } = PluginConfiguration.ColorPresetTransitionCurveLinear;
}

/// <summary>
/// Credential-free outcome for one attempted continuous-playlist step.
/// </summary>
public sealed class HuePlaylistPreviewStepResult
{
    public int Index { get; init; }
    public bool Succeeded { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Aggregate result for a continuous playlist stream. Cleanup warnings apply to the
/// target lifecycle as a whole; completed step outcomes remain available when a later
/// step fails or cancellation stops the sequence.
/// </summary>
public sealed class HuePlaylistStreamProbeResult
{
    public bool Succeeded { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? CleanupWarning { get; init; }
    public IReadOnlyList<HuePlaylistPreviewStepResult> Steps { get; init; } =
        Array.Empty<HuePlaylistPreviewStepResult>();
}

/// <summary>
/// Testable DTLS surface used only by continuous playlist previews. Production wraps
/// <see cref="HueStreamer"/>; keeping this small avoids changing playback's public stream
/// contract while allowing lifecycle-count regression tests without opening a bridge stream.
/// </summary>
internal interface IHuePreviewStream
{
    Func<CancellationToken, Task<bool>>? OnBeforeReconnectWithCancellation { set; }

    Task StartStreamAsync(
        string bridgeIp,
        string appKey,
        string clientKey,
        CancellationToken cancellationToken);

    bool IsHealthy();

    Task<bool> SendColors(
        string areaId,
        Dictionary<int, byte[]> channelColors,
        CancellationToken cancellationToken);

    void StopStream();
}

internal interface IHuePreviewStreamFactory
{
    IHuePreviewStream Create();
}

internal sealed class HuePreviewStreamFactory : IHuePreviewStreamFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public HuePreviewStreamFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public IHuePreviewStream Create()
        => new HuePreviewStreamAdapter(
            new HueStreamer(_loggerFactory.CreateLogger<HueStreamer>()));
}

internal sealed class HuePreviewStreamAdapter : IHuePreviewStream
{
    private readonly HueStreamer _streamer;

    public HuePreviewStreamAdapter(HueStreamer streamer)
    {
        _streamer = streamer ?? throw new ArgumentNullException(nameof(streamer));
    }

    public Func<CancellationToken, Task<bool>>? OnBeforeReconnectWithCancellation
    {
        set => _streamer.OnBeforeReconnectWithCancellation = value;
    }

    public Task StartStreamAsync(
        string bridgeIp,
        string appKey,
        string clientKey,
        CancellationToken cancellationToken)
        => _streamer.StartStreamAsync(bridgeIp, appKey, clientKey, cancellationToken);

    public bool IsHealthy() => _streamer.IsHealthy();

    public Task<bool> SendColors(
        string areaId,
        Dictionary<int, byte[]> channelColors,
        CancellationToken cancellationToken)
        => _streamer.SendColors(
            areaId,
            channelColors,
            cancellationToken: cancellationToken);

    public void StopStream() => _streamer.StopStream();
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
public sealed class HueStreamTester :
    IHueStreamTester,
    IHueTargetScopedStreamTester,
    IHueTransitionCurveStreamTester,
    IHuePlaylistStreamTester,
    IHueRetryAwareStreamTester
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
    private readonly IHuePreviewStreamFactory _previewStreamFactory;
    private readonly object _activeOperationLock = new();
    private readonly HashSet<CancellationTokenSource> _activeOperationCancellations = new();

    public HueStreamTester(
        HueClient hueClient,
        ILoggerFactory loggerFactory,
        ILogger<HueStreamTester> logger,
        HueBridgeLifecycleGate? bridgeLifecycleGate = null)
        : this(
            hueClient,
            loggerFactory,
            logger,
            bridgeLifecycleGate,
            new HuePreviewStreamFactory(loggerFactory))
    {
    }

    internal HueStreamTester(
        HueClient hueClient,
        ILoggerFactory loggerFactory,
        ILogger<HueStreamTester> logger,
        HueBridgeLifecycleGate? bridgeLifecycleGate,
        IHuePreviewStreamFactory previewStreamFactory)
    {
        _hueClient = hueClient ?? throw new ArgumentNullException(nameof(hueClient));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _bridgeLifecycleGate = bridgeLifecycleGate ?? new HueBridgeLifecycleGate();
        _previewStreamFactory = previewStreamFactory ?? throw new ArgumentNullException(nameof(previewStreamFactory));
    }

    /// <summary>
    /// Creates an isolated tester for one scheduled target. The underlying transport is
    /// still shared by HttpClient, but retry policy and diagnostic operation state are
    /// local to the returned instance.
    /// </summary>
    public IHueStreamTester CreateForRetryAttempts(int retryAttempts)
    {
        var client = _hueClient.CreatePlaybackClient();
        client.RetryAttempts = retryAttempts;
        return new HueStreamTester(
            client,
            _loggerFactory,
            _logger,
            _bridgeLifecycleGate,
            _previewStreamFactory);
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
                    probeResult = Failure("The DTLS stream did not become healthy. Check the client key and managed DTLS transport.");
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
                probeResult = Failure("The DTLS stream probe failed. Check the client key and managed DTLS diagnostics.");
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

    /// <summary>
    /// Displays every pre-resolved playlist step through one target lifecycle. All input
    /// is validated before light state is captured, then the target is activated and its
    /// DTLS stream is opened once. State is restored once after the sequence completes,
    /// fails, or is canceled.
    /// </summary>
    public Task<HuePlaylistStreamProbeResult> PreviewPlaylistAsync(
        string bridgeIp,
        string appKey,
        string clientKey,
        string areaId,
        JsonElement areaConfiguration,
        IReadOnlySet<int>? channelIds,
        IReadOnlyList<HuePlaylistPreviewStep> steps,
        CancellationToken cancellationToken = default)
        => RunPlaylistPreviewAsync(
            bridgeIp,
            appKey,
            clientKey,
            areaId,
            areaConfiguration,
            channelIds,
            steps,
            cancellationToken,
            resourceKey: null);

    /// <summary>
    /// Runs a continuous playlist while reserving only the selected bridge/area target.
    /// This mirrors target-scoped single previews and retains the lifecycle lease for the
    /// complete ordered sequence.
    /// </summary>
    public Task<HuePlaylistStreamProbeResult> PreviewPlaylistAsyncForTarget(
        string bridgeIp,
        string appKey,
        string clientKey,
        string areaId,
        JsonElement areaConfiguration,
        IReadOnlySet<int>? channelIds,
        IReadOnlyList<HuePlaylistPreviewStep> steps,
        CancellationToken cancellationToken = default)
        => RunPlaylistPreviewAsync(
            bridgeIp,
            appKey,
            clientKey,
            areaId,
            areaConfiguration,
            channelIds,
            steps,
            cancellationToken,
            HueSyncService.GetPlaybackResourceKey(bridgeIp, areaId));

    private Task<HuePlaylistStreamProbeResult> RunPlaylistPreviewAsync(
        string bridgeIp,
        string appKey,
        string clientKey,
        string areaId,
        JsonElement areaConfiguration,
        IReadOnlySet<int>? channelIds,
        IReadOnlyList<HuePlaylistPreviewStep> steps,
        CancellationToken cancellationToken,
        string? resourceKey)
        => RunSerializedPlaylistAsync(
            operationCancellation => PreviewPlaylistCoreAsync(
                bridgeIp,
                appKey,
                clientKey,
                areaId,
                areaConfiguration,
                channelIds,
                steps,
                operationCancellation),
            cancellationToken,
            resourceKey);

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
                    previewResult = Failure("The DTLS stream did not become healthy. Check the client key and managed DTLS transport.");
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
                previewResult = Failure($"The {effect} preview failed. Check the client key and managed DTLS diagnostics.");
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

    private async Task<HuePlaylistStreamProbeResult> PreviewPlaylistCoreAsync(
        string bridgeIp,
        string appKey,
        string clientKey,
        string areaId,
        JsonElement areaConfiguration,
        IReadOnlySet<int>? channelIds,
        IReadOnlyList<HuePlaylistPreviewStep> steps,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return PlaylistFailure("The continuous playlist preview request was canceled.");

        if (string.IsNullOrWhiteSpace(bridgeIp) ||
            string.IsNullOrWhiteSpace(appKey) ||
            string.IsNullOrWhiteSpace(clientKey) ||
            string.IsNullOrWhiteSpace(areaId))
        {
            return PlaylistFailure("Bridge address, app key, client key, and entertainment area are required.");
        }

        if (!TryPreparePlaylistSteps(
                areaConfiguration,
                channelIds,
                steps,
                out var preparedSteps,
                out var preparationFailure))
        {
            return preparationFailure;
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
                return PlaylistFailure(
                    captureResult.AttemptedCount == 0
                        ? "The continuous playlist preview could not capture any light state safely."
                        : $"The continuous playlist preview could not capture all light states safely ({captureResult.CapturedCount} of {captureResult.AttemptedCount} captured; {captureResult.FailedCount} failed).");
            }

            savedLightStates = captureResult.States;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PlaylistFailure("The continuous playlist preview request was canceled before activation.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save light state before continuous Hue playlist preview for area {0}", areaId);
            return PlaylistFailure("The continuous playlist preview could not save the current light state safely.");
        }

        if (cancellationToken.IsCancellationRequested)
            return PlaylistFailure("The continuous playlist preview request was canceled before activation.");

        bool activated;
        try
        {
            activated = await _hueClient.StartEntertainmentArea(
                bridgeIp,
                appKey,
                areaId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await AddPlaylistCleanupResultAsync(
                PlaylistFailure("The continuous playlist preview request was canceled during activation; the bridge is being restored."),
                bridgeIp,
                appKey,
                areaId,
                savedLightStates).ConfigureAwait(false);
        }

        if (!activated)
        {
            return await AddPlaylistCleanupResultAsync(
                PlaylistFailure("The Hue bridge could not activate the entertainment area for continuous playlist preview; the bridge is being restored."),
                bridgeIp,
                appKey,
                areaId,
                savedLightStates).ConfigureAwait(false);
        }

        var completedSteps = new List<HuePlaylistPreviewStepResult>(preparedSteps.Count);
        var playlistResult = PlaylistFailure("The continuous playlist preview did not complete.");
        PreparedPlaylistPreviewStep? activeStep = null;
        IHuePreviewStream? stream = null;
        try
        {
            await Task.Delay(EntertainmentAreaActivationDelayMs, cancellationToken).ConfigureAwait(false);

            stream = _previewStreamFactory.Create();
            stream.OnBeforeReconnectWithCancellation = reconnectToken =>
                _hueClient.StartEntertainmentArea(bridgeIp, appKey, areaId, reconnectToken);
            await stream.StartStreamAsync(
                bridgeIp,
                appKey,
                clientKey,
                cancellationToken).ConfigureAwait(false);
            if (!stream.IsHealthy())
            {
                playlistResult = PlaylistFailure(
                    "The DTLS stream did not become healthy. Check the client key and managed DTLS transport.");
            }
            else
            {
                foreach (var preparedStep in preparedSteps)
                {
                    activeStep = preparedStep;
                    var stepResult = await RenderPlaylistStepAsync(
                        stream,
                        areaId,
                        preparedStep,
                        cancellationToken).ConfigureAwait(false);
                    completedSteps.Add(stepResult);
                    if (!stepResult.Succeeded)
                    {
                        playlistResult = PlaylistFailure(
                            $"Completed {completedSteps.Count(result => result.Succeeded)} of {preparedSteps.Count} playlist step(s) before the continuous stream failed. {stepResult.Message}",
                            completedSteps);
                        break;
                    }

                    activeStep = null;
                }

                if (completedSteps.Count == preparedSteps.Count && completedSteps.All(step => step.Succeeded))
                {
                    playlistResult = new HuePlaylistStreamProbeResult
                    {
                        Succeeded = true,
                        Message = $"Displayed {completedSteps.Count} playlist step(s) in one continuous DTLS session.",
                        Steps = completedSteps.ToArray()
                    };
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (activeStep != null && completedSteps.All(result => result.Index != activeStep.Index))
            {
                completedSteps.Add(new HuePlaylistPreviewStepResult
                {
                    Index = activeStep.Index,
                    Succeeded = false,
                    Message = "The playlist step was canceled."
                });
            }

            playlistResult = PlaylistFailure(
                $"The continuous playlist preview was canceled after {completedSteps.Count(result => result.Succeeded)} of {preparedSteps.Count} step(s); the bridge is being restored.",
                completedSteps);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Continuous Hue playlist preview failed for entertainment area {0}", areaId);
            if (activeStep != null && completedSteps.All(result => result.Index != activeStep.Index))
            {
                completedSteps.Add(new HuePlaylistPreviewStepResult
                {
                    Index = activeStep.Index,
                    Succeeded = false,
                    Message = "The playlist step failed unexpectedly."
                });
            }

            playlistResult = PlaylistFailure(
                "The continuous playlist preview failed. Check the client key and managed DTLS diagnostics.",
                completedSteps);
        }
        finally
        {
            if (stream != null)
            {
                try
                {
                    stream.StopStream();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not stop the continuous Hue playlist DTLS stream for area {0}", areaId);
                    playlistResult = AddPlaylistCleanupWarning(
                        playlistResult,
                        "The DTLS stream could not be stopped cleanly.");
                }
            }

            playlistResult = await AddPlaylistCleanupResultAsync(
                playlistResult,
                bridgeIp,
                appKey,
                areaId,
                savedLightStates).ConfigureAwait(false);
        }

        if (cancellationToken.IsCancellationRequested &&
            !playlistResult.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase))
        {
            playlistResult = new HuePlaylistStreamProbeResult
            {
                Succeeded = false,
                Message = $"The continuous playlist preview was canceled during cleanup after {playlistResult.Steps.Count(step => step.Succeeded)} of {preparedSteps.Count} step(s). {playlistResult.Message}",
                CleanupWarning = playlistResult.CleanupWarning,
                Steps = playlistResult.Steps
            };
        }

        return playlistResult;
    }

    private static bool TryPreparePlaylistSteps(
        JsonElement areaConfiguration,
        IReadOnlySet<int>? channelIds,
        IReadOnlyList<HuePlaylistPreviewStep>? steps,
        out IReadOnlyList<PreparedPlaylistPreviewStep> preparedSteps,
        out HuePlaylistStreamProbeResult failure)
    {
        var prepared = new List<PreparedPlaylistPreviewStep>();
        preparedSteps = prepared;
        failure = PlaylistFailure("The continuous playlist preview plan is invalid.");
        var maxSteps = PluginConfiguration.MaxScenePlaylistItems *
            PluginConfiguration.MaxScenePlaylistRepeatCount;
        if (steps == null || steps.Count < 1 || steps.Count > maxSteps)
        {
            failure = PlaylistFailure(
                $"A continuous playlist preview must contain between 1 and {maxSteps} expanded steps.");
            return false;
        }

        var usedIndexes = new HashSet<int>();
        var totalDurationSeconds = 0;
        for (var position = 0; position < steps.Count; position++)
        {
            var step = steps[position];
            var index = step?.Index > 0 ? step.Index : position + 1;
            if (step == null)
            {
                failure = PlaylistStepFailure(index, "Playlist step configuration is required.");
                return false;
            }

            if (!usedIndexes.Add(index))
            {
                failure = PlaylistStepFailure(index, "Playlist step indexes must be unique.");
                return false;
            }

            if (!PluginConfiguration.TryNormalizeColorPresetEffect(step.Effect, out var effect))
            {
                failure = PlaylistStepFailure(
                    index,
                    "Playlist step effect must be Solid, Pulse, Rainbow, Candle, Temperature, Aurora, Fire, Ocean, Lightning, or Starlight.");
                return false;
            }

            if (!PluginConfiguration.TryNormalizeColorPresetTransitionCurve(
                    step.TransitionCurve,
                    out var transitionCurve))
            {
                failure = PlaylistStepFailure(
                    index,
                    "Playlist step transition curve must be Linear, SmoothStep, EaseIn, EaseOut, or EaseInOut.");
                return false;
            }

            if (step.EffectSpeedPercent < PluginConfiguration.MinColorPresetEffectSpeedPercent ||
                step.EffectSpeedPercent > PluginConfiguration.MaxColorPresetEffectSpeedPercent)
            {
                failure = PlaylistStepFailure(
                    index,
                    $"Playlist step effect speed must be between {PluginConfiguration.MinColorPresetEffectSpeedPercent} and {PluginConfiguration.MaxColorPresetEffectSpeedPercent} percent.");
                return false;
            }

            if (step.DurationSeconds < MinPreviewDurationSeconds ||
                step.DurationSeconds > MaxPreviewDurationSeconds)
            {
                failure = PlaylistStepFailure(
                    index,
                    $"Playlist step duration must be between {MinPreviewDurationSeconds} and {MaxPreviewDurationSeconds} seconds.");
                return false;
            }

            if (step.TransitionSeconds < PluginConfiguration.MinColorPresetTransitionSeconds ||
                step.TransitionSeconds > PluginConfiguration.MaxColorPresetTransitionSeconds)
            {
                failure = PlaylistStepFailure(
                    index,
                    $"Playlist step fade-in must be between {PluginConfiguration.MinColorPresetTransitionSeconds} and {PluginConfiguration.MaxColorPresetTransitionSeconds} seconds.");
                return false;
            }

            if (step.TransitionOutSeconds < PluginConfiguration.MinColorPresetTransitionOutSeconds ||
                step.TransitionOutSeconds > PluginConfiguration.MaxColorPresetTransitionOutSeconds)
            {
                failure = PlaylistStepFailure(
                    index,
                    $"Playlist step fade-out must be between {PluginConfiguration.MinColorPresetTransitionOutSeconds} and {PluginConfiguration.MaxColorPresetTransitionOutSeconds} seconds.");
                return false;
            }

            if (step.TransitionSeconds + step.TransitionOutSeconds > step.DurationSeconds)
            {
                failure = PlaylistStepFailure(
                    index,
                    "Playlist step fade-in and fade-out cannot exceed its duration together.");
                return false;
            }

            if (!TryBuildSolidColors(
                    areaConfiguration,
                    channelIds,
                    step.Red,
                    step.Green,
                    step.Blue,
                    step.BrightnessPercent,
                    out var channelColors))
            {
                failure = PlaylistStepFailure(
                    index,
                    channelIds == null
                        ? "The selected entertainment area has no valid controllable channels or the playlist step color is invalid."
                        : "The selected channel profile has no valid controllable channels in this entertainment area or the playlist step color is invalid.");
                return false;
            }

            totalDurationSeconds += step.DurationSeconds;
            prepared.Add(new PreparedPlaylistPreviewStep(
                index,
                step.DurationSeconds,
                step.TransitionSeconds,
                step.TransitionOutSeconds,
                effect,
                step.EffectSpeedPercent,
                transitionCurve,
                channelColors));
        }

        if (totalDurationSeconds > PluginConfiguration.MaxScenePlaylistTotalDurationSeconds)
        {
            failure = PlaylistFailure(
                $"A continuous playlist preview cannot exceed {PluginConfiguration.MaxScenePlaylistTotalDurationSeconds} seconds in total.");
            return false;
        }

        preparedSteps = prepared;
        return true;
    }

    private static async Task<HuePlaylistPreviewStepResult> RenderPlaylistStepAsync(
        IHuePreviewStream stream,
        string areaId,
        PreparedPlaylistPreviewStep step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var previewStartedAt = DateTime.UtcNow;
        var previewEndsAt = previewStartedAt.AddSeconds(step.DurationSeconds);
        var transitionEndsAt = previewStartedAt.AddSeconds(step.TransitionSeconds);
        var transitionOutStartsAt = previewEndsAt.Subtract(TimeSpan.FromSeconds(step.TransitionOutSeconds));
        var animated = !string.Equals(
            step.Effect,
            PluginConfiguration.ColorPresetEffectSolid,
            StringComparison.OrdinalIgnoreCase);
        var frame = BuildEffectColors(
            step.ChannelColors,
            step.Effect,
            0,
            step.DurationSeconds,
            step.EffectSpeedPercent);
        if (step.TransitionSeconds > PluginConfiguration.MinColorPresetTransitionSeconds)
            frame = BuildTransitionColors(frame, 0, step.TransitionCurve);

        if (!await SendPlaylistColorsAsync(
                stream,
                areaId,
                frame,
                cancellationToken).ConfigureAwait(false))
        {
            return PlaylistRenderFailure(
                step.Index,
                "The DTLS stream opened, but the playlist step color could not be sent.");
        }

        while (step.TransitionSeconds > PluginConfiguration.MinColorPresetTransitionSeconds)
        {
            var remaining = transitionEndsAt - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;

            await Task.Delay(
                remaining > TimeSpan.FromMilliseconds(PreviewTransitionRefreshIntervalMs)
                    ? TimeSpan.FromMilliseconds(PreviewTransitionRefreshIntervalMs)
                    : remaining,
                cancellationToken).ConfigureAwait(false);

            var progress = Math.Clamp(
                (DateTime.UtcNow - (transitionEndsAt - TimeSpan.FromSeconds(step.TransitionSeconds))).TotalSeconds /
                step.TransitionSeconds,
                0d,
                1d);
            var elapsedSeconds = (DateTime.UtcNow - previewStartedAt).TotalSeconds;
            frame = BuildTransitionColors(
                BuildEffectColors(
                    step.ChannelColors,
                    step.Effect,
                    elapsedSeconds,
                    step.DurationSeconds,
                    step.EffectSpeedPercent),
                progress,
                step.TransitionCurve);
            if (!await SendPlaylistColorsAsync(
                    stream,
                    areaId,
                    frame,
                    cancellationToken).ConfigureAwait(false))
            {
                return PlaylistRenderFailure(
                    step.Index,
                    "The DTLS stream stopped while fading in the playlist step.");
            }
        }

        if (step.TransitionSeconds > PluginConfiguration.MinColorPresetTransitionSeconds &&
            !await SendPlaylistColorsAsync(
                stream,
                areaId,
                BuildEffectColors(
                    step.ChannelColors,
                    step.Effect,
                    (DateTime.UtcNow - previewStartedAt).TotalSeconds,
                    step.DurationSeconds,
                    step.EffectSpeedPercent),
                cancellationToken).ConfigureAwait(false))
        {
            return PlaylistRenderFailure(
                step.Index,
                "The DTLS stream stopped while completing the playlist step fade-in.");
        }

        while (true)
        {
            var now = DateTime.UtcNow;
            if (now >= previewEndsAt)
                break;

            if (step.TransitionOutSeconds > PluginConfiguration.MinColorPresetTransitionOutSeconds &&
                now >= transitionOutStartsAt)
            {
                var remainingFadeOut = previewEndsAt - now;
                await Task.Delay(
                    remainingFadeOut > TimeSpan.FromMilliseconds(PreviewTransitionRefreshIntervalMs)
                        ? TimeSpan.FromMilliseconds(PreviewTransitionRefreshIntervalMs)
                        : remainingFadeOut,
                    cancellationToken).ConfigureAwait(false);

                var fadeOutProgress = Math.Clamp(
                    (DateTime.UtcNow - transitionOutStartsAt).TotalSeconds /
                    step.TransitionOutSeconds,
                    0d,
                    1d);
                frame = BuildTransitionColors(
                    BuildEffectColors(
                        step.ChannelColors,
                        step.Effect,
                        (DateTime.UtcNow - previewStartedAt).TotalSeconds,
                        step.DurationSeconds,
                        step.EffectSpeedPercent),
                    1d - fadeOutProgress,
                    step.TransitionCurve);
                if (!await SendPlaylistColorsAsync(
                        stream,
                        areaId,
                        frame,
                        cancellationToken).ConfigureAwait(false))
                {
                    return PlaylistRenderFailure(
                        step.Index,
                        "The DTLS stream stopped while fading out the playlist step.");
                }

                continue;
            }

            var remainingHold = previewEndsAt - now;
            if (step.TransitionOutSeconds > PluginConfiguration.MinColorPresetTransitionOutSeconds)
            {
                remainingHold = TimeSpan.FromTicks(
                    Math.Min(remainingHold.Ticks, (transitionOutStartsAt - now).Ticks));
            }

            if (remainingHold <= TimeSpan.Zero)
                continue;

            var refreshInterval = animated
                ? TimeSpan.FromMilliseconds(PreviewTransitionRefreshIntervalMs)
                : TimeSpan.FromSeconds(1);
            await Task.Delay(
                remainingHold > refreshInterval ? refreshInterval : remainingHold,
                cancellationToken).ConfigureAwait(false);
            if (DateTime.UtcNow < transitionOutStartsAt &&
                DateTime.UtcNow < previewEndsAt &&
                !await SendPlaylistColorsAsync(
                    stream,
                    areaId,
                    BuildEffectColors(
                        step.ChannelColors,
                        step.Effect,
                        (DateTime.UtcNow - previewStartedAt).TotalSeconds,
                        step.DurationSeconds,
                        step.EffectSpeedPercent),
                    cancellationToken).ConfigureAwait(false))
            {
                return PlaylistRenderFailure(
                    step.Index,
                    "The DTLS stream stopped while holding the playlist step.");
            }
        }

        if (step.TransitionOutSeconds > PluginConfiguration.MinColorPresetTransitionOutSeconds &&
            !await SendPlaylistColorsAsync(
                stream,
                areaId,
                BuildTransitionColors(
                    BuildEffectColors(
                        step.ChannelColors,
                        step.Effect,
                        step.DurationSeconds,
                        step.DurationSeconds,
                        step.EffectSpeedPercent),
                    0,
                    step.TransitionCurve),
                cancellationToken).ConfigureAwait(false))
        {
            return PlaylistRenderFailure(
                step.Index,
                "The DTLS stream stopped while completing the playlist step fade-out.");
        }

        var transitionMessage = step.TransitionSeconds > PluginConfiguration.MinColorPresetTransitionSeconds &&
            step.TransitionOutSeconds > PluginConfiguration.MinColorPresetTransitionOutSeconds
            ? $" with a {step.TransitionSeconds}-second {step.TransitionCurve} fade-in and a {step.TransitionOutSeconds}-second {step.TransitionCurve} fade-out."
            : step.TransitionSeconds > PluginConfiguration.MinColorPresetTransitionSeconds
                ? $" with a {step.TransitionSeconds}-second {step.TransitionCurve} fade-in."
                : step.TransitionOutSeconds > PluginConfiguration.MinColorPresetTransitionOutSeconds
                    ? $" with a {step.TransitionOutSeconds}-second {step.TransitionCurve} fade-out."
                    : ".";
        var effectDescription = string.Equals(
            step.Effect,
            PluginConfiguration.ColorPresetEffectSolid,
            StringComparison.OrdinalIgnoreCase)
            ? "solid color"
            : $"{step.Effect.ToLowerInvariant()} effect";
        return new HuePlaylistPreviewStepResult
        {
            Index = step.Index,
            Succeeded = true,
            Message = $"Displayed the {effectDescription} playlist step for {step.DurationSeconds} seconds across {step.ChannelColors.Count} channel(s){transitionMessage}"
        };
    }

    private static async Task<bool> SendPlaylistColorsAsync(
        IHuePreviewStream stream,
        string areaId,
        Dictionary<int, byte[]> colors,
        CancellationToken cancellationToken)
    {
        var sent = await stream.SendColors(
            areaId,
            colors,
            cancellationToken).ConfigureAwait(false);
        if (!sent)
            cancellationToken.ThrowIfCancellationRequested();

        return sent;
    }

    private static HuePlaylistPreviewStepResult PlaylistRenderFailure(int index, string message)
        => new()
        {
            Index = index,
            Succeeded = false,
            Message = message
        };

    private static HuePlaylistStreamProbeResult PlaylistStepFailure(int index, string message)
        => PlaylistFailure(
            $"Playlist step {index}: {message}",
            new[]
            {
                new HuePlaylistPreviewStepResult
                {
                    Index = index,
                    Succeeded = false,
                    Message = message
                }
            });

    private static HuePlaylistStreamProbeResult PlaylistFailure(
        string message,
        IReadOnlyList<HuePlaylistPreviewStepResult>? steps = null)
        => new()
        {
            Succeeded = false,
            Message = message,
            Steps = steps?.ToArray() ?? Array.Empty<HuePlaylistPreviewStepResult>()
        };

    private async Task<HuePlaylistStreamProbeResult> AddPlaylistCleanupResultAsync(
        HuePlaylistStreamProbeResult playlistResult,
        string bridgeIp,
        string appKey,
        string areaId,
        List<HueClient.LightState> savedLightStates)
    {
        var cleanupResult = await AddCleanupResultAsync(
            new HueStreamProbeResult
            {
                Succeeded = playlistResult.Succeeded,
                Message = playlistResult.Message
            },
            bridgeIp,
            appKey,
            areaId,
            savedLightStates).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(cleanupResult.CleanupWarning))
            return playlistResult;

        return AddPlaylistCleanupWarning(
            playlistResult,
            cleanupResult.CleanupWarning!);
    }

    private static HuePlaylistStreamProbeResult AddPlaylistCleanupWarning(
        HuePlaylistStreamProbeResult result,
        string warning)
    {
        var warnings = new[] { result.CleanupWarning, warning }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var cleanupWarning = string.Join(" ", warnings);
        var message = result.Message;
        if (!string.IsNullOrWhiteSpace(warning) &&
            !message.Contains(warning, StringComparison.Ordinal))
        {
            message = $"{message} Cleanup warning: {warning.Trim()}";
        }

        return new HuePlaylistStreamProbeResult
        {
            Succeeded = false,
            Message = message,
            CleanupWarning = cleanupWarning,
            Steps = result.Steps
        };
    }

    private async Task<HuePlaylistStreamProbeResult> RunSerializedPlaylistAsync(
        Func<CancellationToken, Task<HuePlaylistStreamProbeResult>> operation,
        CancellationToken requestCancellation,
        string? resourceKey)
    {
        var lifecycleLease = _bridgeLifecycleGate.TryEnterDiagnostic(resourceKey);
        if (lifecycleLease == null)
            return PlaylistFailure(DiagnosticBusyMessage);

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation);
        lock (_activeOperationLock)
        {
            _activeOperationCancellations.Add(operationCancellation);
        }

        try
        {
            return await operation(operationCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_activeOperationLock)
            {
                _activeOperationCancellations.Remove(operationCancellation);
            }

            lifecycleLease.Dispose();
        }
    }

    private sealed class PreparedPlaylistPreviewStep
    {
        public PreparedPlaylistPreviewStep(
            int index,
            int durationSeconds,
            int transitionSeconds,
            int transitionOutSeconds,
            string effect,
            int effectSpeedPercent,
            string transitionCurve,
            Dictionary<int, byte[]> channelColors)
        {
            Index = index;
            DurationSeconds = durationSeconds;
            TransitionSeconds = transitionSeconds;
            TransitionOutSeconds = transitionOutSeconds;
            Effect = effect;
            EffectSpeedPercent = effectSpeedPercent;
            TransitionCurve = transitionCurve;
            ChannelColors = channelColors;
        }

        public int Index { get; }
        public int DurationSeconds { get; }
        public int TransitionSeconds { get; }
        public int TransitionOutSeconds { get; }
        public string Effect { get; }
        public int EffectSpeedPercent { get; }
        public string TransitionCurve { get; }
        public Dictionary<int, byte[]> ChannelColors { get; }
    }

    public bool CancelActiveDiagnostic()
    {
        CancellationTokenSource[] activeOperations;
        lock (_activeOperationLock)
        {
            if (_activeOperationCancellations.Count == 0)
                return false;

            // Snapshot under the lock, but invoke cancellation callbacks outside it. A
            // callback may synchronously finish the operation and attempt to unregister
            // its source from the same set.
            activeOperations = _activeOperationCancellations.ToArray();
        }

        foreach (var operation in activeOperations)
        {
            try
            {
                operation.Cancel(throwOnFirstException: false);
            }
            catch (ObjectDisposedException)
            {
                // The operation completed between the snapshot and cancellation. Its
                // finally block already released the source and lifecycle lease.
            }
            catch (Exception ex)
            {
                // CancellationTokenSource.Cancel(false) still reports callback failures
                // after invoking every callback. Keep canceling the remaining operations
                // so one misbehaving callback cannot leave another target running.
                _logger.LogWarning(ex, "A Hue diagnostic cancellation callback failed");
            }
        }

        return true;
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
            _activeOperationCancellations.Add(operationCancellation);
        }

        try
        {
            return await operation(operationCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_activeOperationLock)
            {
                _activeOperationCancellations.Remove(operationCancellation);
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
        // Cleanup deliberately uses an independent budget. A canceled request must not
        // abort bridge restoration, but an unreachable bridge must not hold the diagnostic
        // lifecycle lease indefinitely.
        using var cleanupCancellation = HueCleanupBudget.CreateCancellationSource();
        var cleanupToken = cleanupCancellation.Token;
        if (!await _hueClient.StopEntertainmentAreaWithResult(
                bridgeIp,
                appKey,
                areaId,
                cleanupToken).ConfigureAwait(false))
        {
            warnings.Add("The entertainment area could not be deactivated within the cleanup deadline.");
        }

        if (savedLightStates.Count > 0)
        {
            try
            {
                var restoreResult = await _hueClient.RestoreLightStatesWithResult(
                    bridgeIp,
                    appKey,
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
