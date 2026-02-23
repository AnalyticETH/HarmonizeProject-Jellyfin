using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Jellyfin.Plugin.Hue.Hue;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Hue.Benchmarks;

/// <summary>
/// Benchmarks for HueStream packet building operations.
/// Packet building happens every frame (20-60 Hz), so allocation and speed matter.
///
/// Hue Entertainment API v2 packet format:
///   16-byte fixed header + 9 bytes per channel
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
[RankColumn]
public class PacketBuildingBenchmarks
{
    private Dictionary<int, byte[]> _channelColors8 = null!;
    private Dictionary<int, byte[]> _channelColors16 = null!;
    private byte[] _reuseableBuffer = null!;
    private HueStreamer _streamer = null!;

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(42);

        _streamer = new HueStreamer(NullLogger<HueStreamer>.Instance);

        // 8 channels (typical setup) — each channel needs 6 bytes [R_hi, R_lo, G_hi, G_lo, B_hi, B_lo]
        _channelColors8 = new Dictionary<int, byte[]>();
        for (int i = 0; i < 8; i++)
        {
            var rgb = new byte[6];
            random.NextBytes(rgb);
            _channelColors8[i] = rgb;
        }

        // 16 channels (large setup)
        _channelColors16 = new Dictionary<int, byte[]>();
        for (int i = 0; i < 16; i++)
        {
            var rgb = new byte[6];
            random.NextBytes(rgb);
            _channelColors16[i] = rgb;
        }

        // Pre-allocated buffer (16 header + 16*9 channels = 160 bytes max)
        _reuseableBuffer = new byte[256];
    }

    #region Packet Building Benchmarks

    [Benchmark(Baseline = true, Description = "Build Packet - 8 Channels (HueStreamer)")]
    public byte[] BuildPacket_HueStreamer_8Channels()
    {
        return _streamer.BuildHueStreamPacket(_channelColors8);
    }

    [Benchmark(Description = "Build Packet - 8 Channels (Span/pre-alloc)")]
    public int BuildPacket_Span_8Channels()
    {
        return BuildPacketWithSpan(_channelColors8, _reuseableBuffer);
    }

    [Benchmark(Description = "Build Packet - 16 Channels (HueStreamer)")]
    public byte[] BuildPacket_HueStreamer_16Channels()
    {
        return _streamer.BuildHueStreamPacket(_channelColors16);
    }

    [Benchmark(Description = "Build Packet - 16 Channels (Span/pre-alloc)")]
    public int BuildPacket_Span_16Channels()
    {
        return BuildPacketWithSpan(_channelColors16, _reuseableBuffer);
    }

    #endregion

    #region Color Encoding Benchmarks

    [Benchmark(Description = "Encode RGB to 16-bit - Simple (divide by 2)")]
    public void EncodeRgb16Bit_Simple()
    {
        foreach (var (_, rgb) in _channelColors8)
        {
            byte r = (byte)(rgb[0] / 2);
            byte g = (byte)(rgb[2] / 2);
            byte b = (byte)(rgb[4] / 2);
            _ = new byte[] { r, r, g, g, b, b };
        }
    }

    [Benchmark(Description = "Encode RGB to 16-bit - Bit shift")]
    public void EncodeRgb16Bit_BitShift()
    {
        Span<byte> buffer = stackalloc byte[6];
        foreach (var (_, rgb) in _channelColors8)
        {
            EncodeRgbTo16Bit(rgb, buffer);
        }
    }

    #endregion

    #region Full Frame Processing Simulation

    [Benchmark(Description = "Process Full Frame - 8 Channels @ 60 FPS")]
    public void ProcessFullFrame_8Channels()
    {
        // Simulate 60 frames
        for (int frame = 0; frame < 60; frame++)
        {
            _ = BuildPacketWithSpan(_channelColors8, _reuseableBuffer);
        }
    }

    #endregion

    #region Helper Methods — v2 Hue Entertainment API packet format

    /// <summary>
    /// Zero-allocation Span-based packet builder using the correct v2 format.
    /// Header: "HueStream"(9) + version(2) + seqNo(1) + reserved(2) + colorSpace(1) + reserved(1) = 16 bytes
    /// Per channel: deviceType(1) + id_hi(1) + id_lo(1) + R_hi(1) + R_lo(1) + G_hi(1) + G_lo(1) + B_hi(1) + B_lo(1) = 9 bytes
    /// </summary>
    private static int BuildPacketWithSpan(Dictionary<int, byte[]> channelColors, byte[] buffer)
    {
        var span = buffer.AsSpan();
        int offset = 0;

        // Header: "HueStream" (9 bytes)
        "HueStream"u8.CopyTo(span[offset..]);
        offset += 9;

        // Version 2.0
        span[offset++] = 0x02; // major
        span[offset++] = 0x00; // minor

        // Sequence number (static 0 here — in production this wraps)
        span[offset++] = 0x00;

        // Reserved (2 bytes)
        span[offset++] = 0x00;
        span[offset++] = 0x00;

        // Color space: RGB = 0x00
        span[offset++] = 0x00;

        // Reserved (1 byte)
        span[offset++] = 0x00;

        // Channel data
        foreach (var (channelId, rgb16) in channelColors)
        {
            span[offset++] = 0x00;                       // device type: light
            span[offset++] = (byte)(channelId >> 8);    // channel ID high byte
            span[offset++] = (byte)(channelId & 0xFF);  // channel ID low byte
            rgb16.AsSpan(0, 6).CopyTo(span[offset..]);  // R_hi R_lo G_hi G_lo B_hi B_lo
            offset += 6;
        }

        return offset;
    }

    private static void EncodeRgbTo16Bit(byte[] rgb, Span<byte> output)
    {
        byte r = (byte)(rgb[0] >> 1); // Divide by 2 using bit shift
        byte g = (byte)(rgb[2] >> 1);
        byte b = (byte)(rgb[4] >> 1);

        output[0] = r;
        output[1] = r;
        output[2] = g;
        output[3] = g;
        output[4] = b;
        output[5] = b;
    }

    #endregion
}
