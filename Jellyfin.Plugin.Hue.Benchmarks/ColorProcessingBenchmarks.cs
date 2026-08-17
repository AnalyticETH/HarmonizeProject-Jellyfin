using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Jellyfin.Plugin.Hue.Benchmarks;

/// <summary>
/// Benchmarks for color processing operations used in real-time video sync.
/// These operations are called at 20-60 FPS, so performance is critical.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
[RankColumn]
public class ColorProcessingBenchmarks
{
    private byte[] _frameBuffer = null!;
    private readonly Random _random = new(42);

    [GlobalSetup]
    public void Setup()
    {
        // Simulate a 160x90 RGB frame buffer (used for color sampling)
        _frameBuffer = new byte[160 * 90 * 3];
        _random.NextBytes(_frameBuffer);
    }

    #region RGB to HSL Conversion Benchmarks

    [Benchmark(Description = "RGB to HSL - Single Color")]
    public (double H, double S, double L) RgbToHsl_Single()
    {
        return RgbToHsl(128, 64, 200);
    }

    [Benchmark(Description = "RGB to HSL - 100 Colors")]
    public void RgbToHsl_Batch()
    {
        for (int i = 0; i < 100; i++)
        {
            _ = RgbToHsl(
                _frameBuffer[i * 3],
                _frameBuffer[i * 3 + 1],
                _frameBuffer[i * 3 + 2]);
        }
    }

    #endregion

    #region HSL to RGB Conversion Benchmarks

    [Benchmark(Description = "HSL to RGB - Single Color")]
    public (byte R, byte G, byte B) HslToRgb_Single()
    {
        return HslToRgb(0.75, 0.6, 0.5);
    }

    [Benchmark(Description = "HSL to RGB - 100 Colors")]
    public void HslToRgb_Batch()
    {
        for (int i = 0; i < 100; i++)
        {
            double h = i / 100.0;
            _ = HslToRgb(h, 0.6, 0.5);
        }
    }

    #endregion

    #region Color Sampling Benchmarks

    [Benchmark(Description = "Sample Average RGB from Region")]
    public (byte R, byte G, byte B) SampleAverageRgb()
    {
        return SampleRegionAverage(_frameBuffer, 160, 90, 40, 20, 20, 15);
    }

    [Benchmark(Description = "Sample 8 Light Positions")]
    public void Sample8LightPositions()
    {
        // Simulate sampling for 8 lights in typical entertainment area
        var positions = new (int x, int y)[]
        {
            (20, 45), (40, 45), (60, 45), (80, 45),  // Left to center
            (100, 45), (120, 45), (140, 45), (80, 20) // Center to right + top
        };

        foreach (var (x, y) in positions)
        {
            _ = SampleRegionAverage(_frameBuffer, 160, 90, x, y, 12, 9);
        }
    }

    #endregion

    #region Brightness, Saturation, and Hue Adjustment Benchmarks

    [Benchmark(Description = "Apply Brightness Boost")]
    public (byte R, byte G, byte B) ApplyBrightnessBoost()
    {
        return ApplyBrightness(100, 80, 60, 1.5);
    }

    [Benchmark(Description = "Apply Saturation Boost")]
    public (byte R, byte G, byte B) ApplySaturationBoost()
    {
        return ApplySaturation(100, 80, 60, 1.3);
    }

    [Benchmark(Description = "Apply Hue Shift")]
    public (byte R, byte G, byte B) ApplyHueShift()
    {
        var (h, s, l) = RgbToHsl(100, 80, 60);
        return HslToRgb(NormalizeHue(h + 45.0 / 360.0), s, l);
    }

    [Benchmark(Description = "Full Color Pipeline (Sample + Brightness + Saturation + Hue)")]
    public (byte R, byte G, byte B) FullColorPipeline()
    {
        // Sample
        var (r, g, b) = SampleRegionAverage(_frameBuffer, 160, 90, 80, 45, 12, 9);

        // Brightness boost
        (r, g, b) = ApplyBrightness(r, g, b, 1.3);

        // Saturation boost
        (r, g, b) = ApplySaturation(r, g, b, 1.2);

        // Hue shift
        var (h, s, l) = RgbToHsl(r, g, b);
        (r, g, b) = HslToRgb(NormalizeHue(h + 45.0 / 360.0), s, l);

        return (r, g, b);
    }

    #endregion

    #region Color Change Detection Benchmarks

    private byte[] _previousColors = null!;
    private byte[] _currentColors = null!;

    [IterationSetup(Target = nameof(DetectColorChange_8Channels))]
    public void SetupColorChangeDetection()
    {
        _previousColors = new byte[8 * 3];
        _currentColors = new byte[8 * 3];
        _random.NextBytes(_previousColors);
        _random.NextBytes(_currentColors);
    }

    [Benchmark(Description = "Detect Color Change - 8 Channels")]
    public bool DetectColorChange_8Channels()
    {
        return HasSignificantColorChange(_currentColors, _previousColors, 8, threshold: 10);
    }

    #endregion

    #region Helper Methods (Production implementations)

    private static (double H, double S, double L) RgbToHsl(byte r, byte g, byte b)
    {
        double rd = r / 255.0;
        double gd = g / 255.0;
        double bd = b / 255.0;

        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double delta = max - min;

        double h = 0, s = 0, l = (max + min) / 2;

        if (delta > 0)
        {
            s = l < 0.5 ? delta / (max + min) : delta / (2 - max - min);

            if (max == rd)
                h = ((gd - bd) / delta + (gd < bd ? 6 : 0)) / 6;
            else if (max == gd)
                h = ((bd - rd) / delta + 2) / 6;
            else
                h = ((rd - gd) / delta + 4) / 6;
        }

        return (h, s, l);
    }

    private static (byte R, byte G, byte B) HslToRgb(double h, double s, double l)
    {
        double r, g, b;

        if (s == 0)
        {
            r = g = b = l;
        }
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            r = HueToRgb(p, q, h + 1.0 / 3);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0 / 3);
        }

        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0)
            t += 1;
        if (t > 1)
            t -= 1;
        if (t < 1.0 / 6)
            return p + (q - p) * 6 * t;
        if (t < 1.0 / 2)
            return q;
        if (t < 2.0 / 3)
            return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    private static (byte R, byte G, byte B) SampleRegionAverage(
        byte[] buffer, int width, int height, int centerX, int centerY, int regionWidth, int regionHeight)
    {
        int startX = Math.Max(0, centerX - regionWidth / 2);
        int startY = Math.Max(0, centerY - regionHeight / 2);
        int endX = Math.Min(width, startX + regionWidth);
        int endY = Math.Min(height, startY + regionHeight);

        long sumR = 0, sumG = 0, sumB = 0;
        int count = 0;

        for (int y = startY; y < endY; y++)
        {
            for (int x = startX; x < endX; x++)
            {
                int idx = (y * width + x) * 3;
                sumR += buffer[idx];
                sumG += buffer[idx + 1];
                sumB += buffer[idx + 2];
                count++;
            }
        }

        if (count == 0)
            return (0, 0, 0);

        return ((byte)(sumR / count), (byte)(sumG / count), (byte)(sumB / count));
    }

    private static (byte R, byte G, byte B) ApplyBrightness(byte r, byte g, byte b, double factor)
    {
        return (
            (byte)Math.Min(255, r * factor),
            (byte)Math.Min(255, g * factor),
            (byte)Math.Min(255, b * factor)
        );
    }

    private static (byte R, byte G, byte B) ApplySaturation(byte r, byte g, byte b, double factor)
    {
        var (h, s, l) = RgbToHsl(r, g, b);
        s = Math.Min(1.0, s * factor);
        return HslToRgb(h, s, l);
    }

    private static double NormalizeHue(double hue)
    {
        hue %= 1.0;
        return hue < 0 ? hue + 1.0 : hue;
    }

    private static bool HasSignificantColorChange(byte[] current, byte[] previous, int channelCount, int threshold)
    {
        for (int c = 0; c < channelCount; c++)
        {
            int idx = c * 3;
            if (Math.Abs(current[idx] - previous[idx]) > threshold ||
                Math.Abs(current[idx + 1] - previous[idx + 1]) > threshold ||
                Math.Abs(current[idx + 2] - previous[idx + 2]) > threshold)
            {
                return true;
            }
        }
        return false;
    }

    #endregion
}
