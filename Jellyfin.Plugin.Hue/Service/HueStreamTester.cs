using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
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
        IReadOnlySet<int>? channelIds = null);

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
        int durationSeconds);
}

/// <summary>
/// Result of a DTLS stream probe. No bridge credentials are included.
/// </summary>
public sealed class HueStreamProbeResult
{
    public bool Succeeded { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Opens the same activate/DTLS/send/deactivate lifecycle used by playback, using a
/// very low-intensity probe color and the selected area's real channel IDs.
/// </summary>
public sealed class HueStreamTester : IHueStreamTester
{
    private const int EntertainmentAreaActivationDelayMs = 200;
    private const byte ProbeColor = 1;
    internal const int MinPreviewDurationSeconds = 1;
    internal const int MaxPreviewDurationSeconds = 30;

    private readonly HueClient _hueClient;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<HueStreamTester> _logger;

    public HueStreamTester(HueClient hueClient, ILoggerFactory loggerFactory, ILogger<HueStreamTester> logger)
    {
        _hueClient = hueClient ?? throw new ArgumentNullException(nameof(hueClient));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<HueStreamProbeResult> TestAsync(
        string bridgeIp,
        string appKey,
        string clientKey,
        string areaId,
        JsonElement areaConfiguration,
        IReadOnlySet<int>? channelIds = null)
    {
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

        List<HueClient.LightState>? savedLightStates;
        try
        {
            savedLightStates = await _hueClient.GetLightStates(
                bridgeIp,
                appKey,
                areaConfiguration,
                channelIds).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save light state before Hue DTLS stream probe for area {0}", areaId);
            return Failure("The probe could not save the current light state safely.");
        }

        if (savedLightStates == null)
        {
            return Failure("The probe could not read the current light state safely.");
        }

        var activated = await _hueClient.StartEntertainmentArea(bridgeIp, appKey, areaId).ConfigureAwait(false);
        if (!activated)
        {
            return Failure("The Hue bridge could not activate the entertainment area.");
        }

        try
        {
            await Task.Delay(EntertainmentAreaActivationDelayMs).ConfigureAwait(false);

            var streamer = new HueStreamer(_loggerFactory.CreateLogger<HueStreamer>());
            try
            {
                await streamer.StartStreamAsync(bridgeIp, appKey, clientKey).ConfigureAwait(false);
                if (!streamer.IsHealthy())
                {
                    return Failure("The DTLS stream did not become healthy. Check the client key and OpenSSL installation.");
                }

                var sent = await streamer.SendColors(areaId, channelColors).ConfigureAwait(false);
                if (!sent)
                {
                    return Failure("The DTLS stream opened, but the probe packet could not be sent.");
                }

                return new HueStreamProbeResult
                {
                    Succeeded = true,
                    Message = "DTLS stream opened and a low-intensity probe packet was sent."
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hue DTLS stream probe failed for entertainment area {0}", areaId);
                return Failure("The DTLS stream probe failed. Check the client key and OpenSSL diagnostics.");
            }
            finally
            {
                streamer.StopStream();
            }
        }
        finally
        {
            await _hueClient.StopEntertainmentArea(bridgeIp, appKey, areaId).ConfigureAwait(false);
            if (savedLightStates.Count > 0)
            {
                await _hueClient.RestoreLightStates(bridgeIp, appKey, savedLightStates).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Displays a bounded solid-color preview through the same save/activate/DTLS/restore
    /// lifecycle used by playback. This is intentionally separate from TestAsync so a
    /// connection check can remain low intensity and non-disruptive.
    /// </summary>
    public async Task<HueStreamProbeResult> PreviewAsync(
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
        int durationSeconds)
    {
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

        List<HueClient.LightState>? savedLightStates;
        try
        {
            savedLightStates = await _hueClient.GetLightStates(
                bridgeIp,
                appKey,
                areaConfiguration,
                channelIds).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save light state before Hue color preview for area {0}", areaId);
            return Failure("The preview could not save the current light state safely.");
        }

        if (savedLightStates == null)
        {
            return Failure("The preview could not read the current light state safely.");
        }

        var activated = await _hueClient.StartEntertainmentArea(bridgeIp, appKey, areaId).ConfigureAwait(false);
        if (!activated)
        {
            return Failure("The Hue bridge could not activate the entertainment area for preview.");
        }

        try
        {
            await Task.Delay(EntertainmentAreaActivationDelayMs).ConfigureAwait(false);

            var streamer = new HueStreamer(_loggerFactory.CreateLogger<HueStreamer>());
            try
            {
                await streamer.StartStreamAsync(bridgeIp, appKey, clientKey).ConfigureAwait(false);
                if (!streamer.IsHealthy())
                {
                    return Failure("The DTLS stream did not become healthy. Check the client key and OpenSSL installation.");
                }

                var sent = await streamer.SendColors(areaId, channelColors).ConfigureAwait(false);
                if (!sent)
                {
                    return Failure("The DTLS stream opened, but the preview color could not be sent.");
                }

                // Hue bridges deactivate an entertainment area after a period of
                // inactivity. Refresh the static packet once per second so a longer
                // preview remains visible for its full requested duration.
                var previewEndsAt = DateTime.UtcNow.AddSeconds(durationSeconds);
                while (true)
                {
                    var remaining = previewEndsAt - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                        break;

                    await Task.Delay(
                        remaining > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remaining)
                        .ConfigureAwait(false);
                    if (DateTime.UtcNow < previewEndsAt &&
                        !await streamer.SendColors(areaId, channelColors).ConfigureAwait(false))
                    {
                        return Failure("The DTLS stream stopped while holding the preview color.");
                    }
                }

                return new HueStreamProbeResult
                {
                    Succeeded = true,
                    Message = $"Displayed the solid color preview for {durationSeconds} seconds across {channelColors.Count} channel(s)."
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hue solid color preview failed for entertainment area {0}", areaId);
                return Failure("The solid color preview failed. Check the client key and OpenSSL diagnostics.");
            }
            finally
            {
                streamer.StopStream();
            }
        }
        finally
        {
            await _hueClient.StopEntertainmentArea(bridgeIp, appKey, areaId).ConfigureAwait(false);
            if (savedLightStates.Count > 0)
            {
                await _hueClient.RestoreLightStates(bridgeIp, appKey, savedLightStates).ConfigureAwait(false);
            }
        }
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
