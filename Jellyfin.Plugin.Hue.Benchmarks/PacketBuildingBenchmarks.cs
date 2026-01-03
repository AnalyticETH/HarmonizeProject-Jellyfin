using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Jellyfin.Plugin.Hue.Benchmarks;

/// <summary>
/// Benchmarks for HueStream packet building operations.
/// Packet building happens every frame (20-60 Hz), so allocation and speed matter.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
[RankColumn]
public class PacketBuildingBenchmarks
{
    private Dictionary<int, byte[]> _channelColors8 = null!;
    private Dictionary<int, byte[]> _channelColors16 = null!;
    private byte[] _reuseableBuffer = null!;
    private const string AreaId = "entertainment-area-12345678";

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(42);

        // 8 channels (typical setup)
        _channelColors8 = new Dictionary<int, byte[]>();
        for (int i = 0; i < 8; i++)
        {
            var rgb = new byte[3];
            random.NextBytes(rgb);
            _channelColors8[i] = rgb;
        }

        // 16 channels (large setup)
        _channelColors16 = new Dictionary<int, byte[]>();
        for (int i = 0; i < 16; i++)
        {
            var rgb = new byte[3];
            random.NextBytes(rgb);
            _channelColors16[i] = rgb;
        }

        // Pre-allocated buffer for reuse
        _reuseableBuffer = new byte[256];
    }

    #region Packet Building Benchmarks

    [Benchmark(Baseline = true, Description = "Build Packet - 8 Channels (MemoryStream)")]
    public byte[] BuildPacket_MemoryStream_8Channels()
    {
        return BuildPacketWithMemoryStream(AreaId, _channelColors8);
    }

    [Benchmark(Description = "Build Packet - 8 Channels (Span/ArrayPool)")]
    public int BuildPacket_Span_8Channels()
    {
        return BuildPacketWithSpan(AreaId, _channelColors8, _reuseableBuffer);
    }

    [Benchmark(Description = "Build Packet - 16 Channels (MemoryStream)")]
    public byte[] BuildPacket_MemoryStream_16Channels()
    {
        return BuildPacketWithMemoryStream(AreaId, _channelColors16);
    }

    [Benchmark(Description = "Build Packet - 16 Channels (Span/ArrayPool)")]
    public int BuildPacket_Span_16Channels()
    {
        return BuildPacketWithSpan(AreaId, _channelColors16, _reuseableBuffer);
    }

    #endregion

    #region Color Encoding Benchmarks

    [Benchmark(Description = "Encode RGB to 16-bit - Simple")]
    public void EncodeRgb16Bit_Simple()
    {
        foreach (var (_, rgb) in _channelColors8)
        {
            byte r = (byte)(rgb[0] / 2);
            byte g = (byte)(rgb[1] / 2);
            byte b = (byte)(rgb[2] / 2);
            // Simulate writing duplicated bytes
            _ = new byte[] { r, r, g, g, b, b };
        }
    }

    [Benchmark(Description = "Encode RGB to 16-bit - Optimized")]
    public void EncodeRgb16Bit_Optimized()
    {
        Span<byte> buffer = stackalloc byte[6];
        foreach (var (_, rgb) in _channelColors8)
        {
            EncodeRgbTo16Bit(rgb, buffer);
        }
    }

    #endregion

    #region String Encoding Benchmarks

    [Benchmark(Description = "Encode AreaId - GetBytes")]
    public byte[] EncodeAreaId_GetBytes()
    {
        return Encoding.ASCII.GetBytes(AreaId);
    }

    [Benchmark(Description = "Encode AreaId - Span")]
    public int EncodeAreaId_Span()
    {
        Span<byte> buffer = stackalloc byte[64];
        return Encoding.ASCII.GetBytes(AreaId, buffer);
    }

    #endregion

    #region Full Frame Processing Simulation

    [Benchmark(Description = "Process Full Frame - 8 Channels @ 60 FPS")]
    public void ProcessFullFrame_8Channels()
    {
        // Simulate 60 frames
        for (int frame = 0; frame < 60; frame++)
        {
            _ = BuildPacketWithSpan(AreaId, _channelColors8, _reuseableBuffer);
        }
    }

    #endregion

    #region Helper Methods

    private static byte[] BuildPacketWithMemoryStream(string areaId, Dictionary<int, byte[]> channelColors)
    {
        using var ms = new MemoryStream();

        // Header
        ms.Write("HueStream"u8);

        // Version
        ms.Write(new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

        // Area ID
        ms.Write(Encoding.ASCII.GetBytes(areaId));
        ms.WriteByte(0x00);

        // Channel data
        foreach (var (channelId, rgb) in channelColors.OrderBy(c => c.Key))
        {
            ms.WriteByte((byte)channelId);
            byte r = (byte)(rgb[0] / 2);
            byte g = (byte)(rgb[1] / 2);
            byte b = (byte)(rgb[2] / 2);
            ms.Write(new byte[] { r, r, g, g, b, b });
        }

        return ms.ToArray();
    }

    private static int BuildPacketWithSpan(string areaId, Dictionary<int, byte[]> channelColors, byte[] buffer)
    {
        var span = buffer.AsSpan();
        int offset = 0;

        // Header: "HueStream"
        "HueStream"u8.CopyTo(span[offset..]);
        offset += 9;

        // Version
        span[offset++] = 0x02;
        span[offset++] = 0x00;
        span[offset++] = 0x00;
        span[offset++] = 0x00;
        span[offset++] = 0x00;
        span[offset++] = 0x00;
        span[offset++] = 0x00;

        // Area ID
        offset += Encoding.ASCII.GetBytes(areaId, span[offset..]);
        span[offset++] = 0x00;

        // Channel data
        foreach (var (channelId, rgb) in channelColors.OrderBy(c => c.Key))
        {
            span[offset++] = (byte)channelId;
            byte r = (byte)(rgb[0] / 2);
            byte g = (byte)(rgb[1] / 2);
            byte b = (byte)(rgb[2] / 2);
            span[offset++] = r;
            span[offset++] = r;
            span[offset++] = g;
            span[offset++] = g;
            span[offset++] = b;
            span[offset++] = b;
        }

        return offset;
    }

    private static void EncodeRgbTo16Bit(byte[] rgb, Span<byte> output)
    {
        byte r = (byte)(rgb[0] >> 1); // Divide by 2 using bit shift
        byte g = (byte)(rgb[1] >> 1);
        byte b = (byte)(rgb[2] >> 1);

        output[0] = r;
        output[1] = r;
        output[2] = g;
        output[3] = g;
        output[4] = b;
        output[5] = b;
    }

    #endregion
}
