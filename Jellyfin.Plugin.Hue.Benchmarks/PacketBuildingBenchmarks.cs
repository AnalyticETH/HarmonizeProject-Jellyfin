using System.Text;
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
///   16-byte fixed header + 36-byte area UUID + 7 bytes per channel.
/// The Span prototype uses pre-encoded, validated inputs and a reusable buffer;
/// it does not include the production serializer's validation or allocation costs.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
[RankColumn]
public class PacketBuildingBenchmarks
{
    private const string EntertainmentAreaId = "9a334b22-b148-4540-ae52-39f17826b9bc";
    private const int HeaderBytes = 52;
    private const int ChannelBytes = 7;
    private Dictionary<int, byte[]> _channelColors8 = null!;
    private Dictionary<int, byte[]> _channelColors16 = null!;
    private byte[] _areaIdBytes = null!;
    private byte[] _reuseableBuffer = null!;
    private byte _prototypeSequenceNumber;
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

        _areaIdBytes = Encoding.ASCII.GetBytes(EntertainmentAreaId);
        _reuseableBuffer = new byte[HeaderBytes + 16 * ChannelBytes];
        foreach (var colors in new[] { _channelColors8, _channelColors16 })
        {
            var expected = _streamer.BuildHueStreamPacket(EntertainmentAreaId, colors);
            var length = BuildPacketWithSpan(_areaIdBytes, colors, _reuseableBuffer, expected[11]);
            if (expected.Length != length || !expected.AsSpan().SequenceEqual(_reuseableBuffer.AsSpan(0, length)))
                throw new InvalidOperationException("The packet prototype must match the production serializer byte for byte.");
        }
    }

    #region Packet Building Benchmarks

    [Benchmark(Baseline = true, Description = "Build Packet - 8 Channels (HueStreamer)")]
    public byte[] BuildPacket_HueStreamer_8Channels()
    {
        return _streamer.BuildHueStreamPacket(EntertainmentAreaId, _channelColors8);
    }

    [Benchmark(Description = "Prototype Packet - 8 Channels (trusted Span/pre-alloc)")]
    public int BuildPacket_Span_8Channels()
    {
        return BuildPacketWithSpan(_areaIdBytes, _channelColors8, _reuseableBuffer, unchecked(_prototypeSequenceNumber++));
    }

    [Benchmark(Description = "Build Packet - 16 Channels (HueStreamer)")]
    public byte[] BuildPacket_HueStreamer_16Channels()
    {
        return _streamer.BuildHueStreamPacket(EntertainmentAreaId, _channelColors16);
    }

    [Benchmark(Description = "Prototype Packet - 16 Channels (trusted Span/pre-alloc)")]
    public int BuildPacket_Span_16Channels()
    {
        return BuildPacketWithSpan(_areaIdBytes, _channelColors16, _reuseableBuffer, unchecked(_prototypeSequenceNumber++));
    }

    #endregion

    #region Prototype Packet Batch

    [Benchmark(Description = "Prototype Packet Batch - 60 Packets, 8 Channels")]
    public int BuildPrototypeBatch_8Channels()
    {
        var bytesWritten = 0;
        for (int frame = 0; frame < 60; frame++)
        {
            bytesWritten += BuildPacketWithSpan(_areaIdBytes, _channelColors8, _reuseableBuffer, unchecked(_prototypeSequenceNumber++));
        }
        return bytesWritten;
    }

    #endregion

    #region Helper Methods — v2 Hue Entertainment API packet format

    /// <summary>
    /// Zero-allocation prototype for valid byte-sized channel IDs and RGB16 values.
    /// Header: "HueStream"(9) + version(2) + seqNo(1) + reserved(2) + colorSpace(1) + reserved(1) = 16 bytes
    /// Area: canonical hyphenated UUID in ASCII = 36 bytes
    /// Per channel: channelId(1) + R_hi(1) + R_lo(1) + G_hi(1) + G_lo(1) + B_hi(1) + B_lo(1) = 7 bytes
    /// </summary>
    private static int BuildPacketWithSpan(ReadOnlySpan<byte> areaId, Dictionary<int, byte[]> channelColors, byte[] buffer, byte sequenceNumber)
    {
        var span = buffer.AsSpan();
        int offset = 0;

        // Header: "HueStream" (9 bytes)
        "HueStream"u8.CopyTo(span[offset..]);
        offset += 9;

        // Version 2.0
        span[offset++] = 0x02; // major
        span[offset++] = 0x00; // minor

        span[offset++] = sequenceNumber;

        // Reserved (2 bytes)
        span[offset++] = 0x00;
        span[offset++] = 0x00;

        // Color space: RGB = 0x00
        span[offset++] = 0x00;

        // Reserved (1 byte)
        span[offset++] = 0x00;

        areaId.CopyTo(span[offset..]);
        offset += areaId.Length;

        // Channel data
        foreach (var (channelId, rgb16) in channelColors)
        {
            span[offset++] = (byte)channelId;
            rgb16.AsSpan(0, 6).CopyTo(span[offset..]);  // R_hi R_lo G_hi G_lo B_hi B_lo
            offset += 6;
        }

        return offset;
    }

    #endregion
}
