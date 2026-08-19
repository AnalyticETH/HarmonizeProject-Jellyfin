using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Hue.Hue;

/// <summary>
/// Converts the Hue color representations returned by the bridge into a compact RGB
/// sample that can be used to seed a saved scene or an administrator preview.
/// </summary>
public static class HueColorMath
{
    /// <summary>
    /// Represents an averaged current-light sample. Brightness is kept separate from
    /// chromaticity so a scene can retain the room's color and output level independently.
    /// </summary>
    public readonly record struct RgbSample(
        int Red,
        int Green,
        int Blue,
        int BrightnessPercent,
        int SampledLightCount);

    /// <summary>
    /// Averages the currently-on lights while retaining the average output brightness of
    /// the complete captured set. Off lights contribute zero brightness but do not muddy
    /// the room's chromaticity sample.
    /// </summary>
    public static bool TryAverageLightStates(
        IEnumerable<HueClient.LightState>? states,
        out RgbSample sample)
    {
        sample = default;
        if (states == null)
            return false;

        var capturedStates = states.ToArray();
        if (capturedStates.Length == 0)
            return false;

        var redTotal = 0d;
        var greenTotal = 0d;
        var blueTotal = 0d;
        var sampledLightCount = 0;
        var brightnessTotal = 0d;
        foreach (var state in capturedStates)
        {
            var brightness = state.IsOn
                ? Math.Clamp(state.Brightness, 0, 100)
                : 0;
            brightnessTotal += brightness;
            if (!state.IsOn || !TryConvertToRgb(state, out var red, out var green, out var blue))
                continue;

            redTotal += red;
            greenTotal += green;
            blueTotal += blue;
            sampledLightCount++;
        }

        var brightnessPercent = (int)Math.Round(
            brightnessTotal / capturedStates.Length,
            MidpointRounding.AwayFromZero);
        if (sampledLightCount == 0)
        {
            sample = new RgbSample(0, 0, 0, brightnessPercent, 0);
            return true;
        }

        sample = new RgbSample(
            ToByte(redTotal / sampledLightCount),
            ToByte(greenTotal / sampledLightCount),
            ToByte(blueTotal / sampledLightCount),
            brightnessPercent,
            sampledLightCount);
        return true;
    }

    /// <summary>
    /// Converts one bridge light state to sRGB. Hue color-temperature-only lights use
    /// their valid mirek value; color-capable lights use their xy chromaticity.
    /// </summary>
    public static bool TryConvertToRgb(
        HueClient.LightState state,
        out double red,
        out double green,
        out double blue)
    {
        if (state.HasColor && TryConvertXyToRgb(state.X, state.Y, out red, out green, out blue))
            return true;

        if (state.Mirek is { } mirek && TryConvertMirekToRgb(mirek, out red, out green, out blue))
            return true;

        red = 0;
        green = 0;
        blue = 0;
        return false;
    }

    /// <summary>
    /// Converts Hue xy chromaticity to normalized sRGB. The result deliberately omits
    /// brightness because the bridge state exposes that independently.
    /// </summary>
    public static bool TryConvertXyToRgb(
        double x,
        double y,
        out double red,
        out double green,
        out double blue)
    {
        red = 0;
        green = 0;
        blue = 0;
        if (double.IsNaN(x) || double.IsInfinity(x) ||
            double.IsNaN(y) || double.IsInfinity(y) ||
            x <= 0 || y <= 0 || x + y >= 1)
        {
            return false;
        }

        // Use unit luminance; brightness is carried separately by RgbSample.
        var z = 1d - x - y;
        var X = x / y;
        var Y = 1d;
        var Z = z / y;

        var linearRed = (3.2406d * X) - (1.5372d * Y) - (0.4986d * Z);
        var linearGreen = (-0.9689d * X) + (1.8758d * Y) + (0.0415d * Z);
        var linearBlue = (0.0557d * X) - (0.2040d * Y) + (1.0570d * Z);

        // Colors outside the sRGB gamut are clipped and then normalized so unusual
        // Hue gamut coordinates still seed a useful, vivid preview color.
        linearRed = Math.Max(0, linearRed);
        linearGreen = Math.Max(0, linearGreen);
        linearBlue = Math.Max(0, linearBlue);
        var maximum = Math.Max(linearRed, Math.Max(linearGreen, linearBlue));
        if (maximum <= double.Epsilon)
            return false;

        red = GammaEncode(linearRed / maximum);
        green = GammaEncode(linearGreen / maximum);
        blue = GammaEncode(linearBlue / maximum);
        return true;
    }

    /// <summary>
    /// Converts a Hue mirek color-temperature value to normalized sRGB.
    /// </summary>
    public static bool TryConvertMirekToRgb(
        int mirek,
        out double red,
        out double green,
        out double blue)
    {
        red = 0;
        green = 0;
        blue = 0;
        if (mirek <= 0)
            return false;

        var kelvin = Math.Clamp(1_000_000d / mirek, 1_000d, 40_000d);
        var temperature = kelvin / 100d;
        red = temperature <= 66d
            ? 255d
            : 329.698727446d * Math.Pow(temperature - 60d, -0.1332047592d);
        green = temperature <= 66d
            ? 99.4708025861d * Math.Log(temperature) - 161.1195681661d
            : 288.1221695283d * Math.Pow(temperature - 60d, -0.0755148492d);
        blue = temperature >= 66d
            ? 255d
            : temperature <= 19d
                ? 0d
                : 138.5177312231d * Math.Log(temperature - 10d) - 305.0447927307d;

        red = Math.Clamp(red / 255d, 0d, 1d);
        green = Math.Clamp(green / 255d, 0d, 1d);
        blue = Math.Clamp(blue / 255d, 0d, 1d);
        return true;
    }

    private static double GammaEncode(double value)
        => value <= 0.0031308d
            ? 12.92d * value
            : (1.055d * Math.Pow(value, 1d / 2.4d)) - 0.055d;

    private static int ToByte(double value)
        => Math.Clamp(
            (int)Math.Round(Math.Clamp(value, 0d, 1d) * 255d, MidpointRounding.AwayFromZero),
            0,
            255);
}
