using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Hue.Configuration;
using Jellyfin.Plugin.Hue.Hue;
using Jellyfin.Plugin.Hue.Service;
using Microsoft.Extensions.Logging;
using Moq;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

/// <summary>
/// Tests for HueStreamer binary protocol and color encoding.
/// Tests verify the Hue Entertainment API v2 packet format:
///   [0-8]   "HueStream" (9 bytes ASCII)
///   [9]     0x02  major version
///   [10]    0x00  minor version
///   [11]    seqNo (wrapping 0-255)
///   [12-13] 0x00 0x00 reserved
///   [14]    0x00  color space (RGB)
///   [15]    0x00  reserved
///   [16-51] configuration UUID as ASCII
///   Per channel (7 bytes): channel ID, R_hi R_lo G_hi G_lo B_hi B_lo
/// </summary>
public class HueStreamerTests : IDisposable
{
    private const string AreaId = "00112233-4455-6677-8899-aabbccddeeff";
    private readonly Mock<ILogger<HueStreamer>> _loggerMock;
    private readonly HueStreamer _streamer;
    private readonly List<HueStreamer> _streamers = new();

    public HueStreamerTests()
    {
        _loggerMock = new Mock<ILogger<HueStreamer>>();
        _streamer = CreateStreamer(_loggerMock.Object);
    }

    public void Dispose()
    {
        foreach (var streamer in _streamers)
            streamer.StopStream();
    }

    [Fact]
    public async Task V2Packet_SendIncludesConfigurationUuidAndByteAddressedRgb16()
    {
        var connection = new TestDtlsConnection();
        var streamer = CreateStreamer(_loggerMock.Object, (_, _, _, _) => Task.FromResult<IHueDtlsConnection>(connection));
        await streamer.StartStreamAsync("192.168.1.100", "app-key", "client-key");

        Assert.True(await streamer.SendColors(AreaId, new Dictionary<int, byte[]>
        {
            [255] = new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9a, 0xbc }
        }));

        // Derived asymmetric fixture; UUID bytes are text, and RGB16 is big-endian.
        var expected = Convert.FromHexString("48756553747265616d0200000000000030303131323233332d343435352d363637372d383839392d616162626363646465656666ff123456789abc");
        Assert.Equal(expected, Assert.Single(connection.SentPackets));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("area-id")]
    [InlineData("00112233445566778899aabbccddeeff")]
    [InlineData("{00112233-4455-6677-8899-aabbccddeeff}")]
    public async Task V2Packet_InvalidAreaFailsBeforeSendingOrReconnecting(string? areaId)
    {
        var connection = new TestDtlsConnection();
        var connections = 0;
        var streamer = CreateStreamer(_loggerMock.Object, (_, _, _, _) =>
        {
            connections++;
            return Task.FromResult<IHueDtlsConnection>(connection);
        });
        await streamer.StartStreamAsync("192.168.1.100", "app-key", "client-key");

        Assert.False(await streamer.SendColors(areaId!, CreateChannelColors(1)));
        Assert.Empty(connection.SentPackets);
        Assert.Equal(1, connections);
        Assert.Equal(0, streamer.ReconnectAttempts);
    }

    [Fact]
    public async Task V2Packet_CannotChangeAreaInsideLiveStream()
    {
        var clock = new ManualTimeProvider();
        var connection = new TestDtlsConnection();
        var streamer = CreateStreamer(_loggerMock.Object, (_, _, _, _) => Task.FromResult<IHueDtlsConnection>(connection), clock);
        await streamer.StartStreamAsync("192.168.1.100", "app-key", "client-key");
        Assert.True(await streamer.SendColors(AreaId, CreateChannelColors(1)));

        Assert.False(await streamer.SendColors("1a8d99cc-967b-44f2-9202-43f976c0fa6b", CreateChannelColors(1), colorChangeThreshold: 255));
        Assert.Single(connection.SentPackets);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(2, connection.SentPackets.Count);
        Assert.All(connection.SentPackets, packet => Assert.Equal(AreaId, Encoding.ASCII.GetString(packet, 16, 36)));
    }

    [Fact]
    public async Task V2Packet_RejectsChannel256InsteadOfTruncatingItsAddress()
    {
        var connection = new TestDtlsConnection();
        var streamer = CreateStreamer(_loggerMock.Object, (_, _, _, _) => Task.FromResult<IHueDtlsConnection>(connection));
        await streamer.StartStreamAsync("192.168.1.100", "app-key", "client-key");

        Assert.False(await streamer.SendColors(AreaId, new Dictionary<int, byte[]> { [256] = new byte[6] }));
        Assert.Empty(connection.SentPackets);
        Assert.Equal(0, streamer.ReconnectAttempts);
    }

    [Fact]
    public void V2Packet_MatchesPublishedHyperionExample()
    {
        // Source-comment fixture, not a physical capture; provenance is in docs/HUE_STREAM_PROTOCOL.md.
        SetPrivateField(_streamer, "_sequenceNumber", (byte)7);
        var packet = _streamer.BuildHueStreamPacket("1a8d99cc-967b-44f2-9202-43f976c0fa6b", new Dictionary<int, byte[]>
        {
            [0] = new byte[] { 255, 255, 0, 0, 0, 0 },
            [1] = new byte[] { 0, 0, 255, 255, 0, 0 },
            [2] = new byte[] { 0, 0, 0, 0, 255, 255 },
            [3] = new byte[] { 255, 255, 255, 255, 255, 255 }
        });
        var expected = Convert.FromHexString("48756553747265616d0200070000000031613864393963632d393637622d343466322d393230322d34336639373663306661366200ffff00000000010000ffff00000200000000ffff03ffffffffffff");

        Assert.Equal(expected, packet);
    }

    [Fact]
    public void V2Packet_PreservesByteSequenceRollover()
    {
        SetPrivateField(_streamer, "_sequenceNumber", (byte)254);
        var sequences = Enumerable.Range(0, 3).Select(_ => _streamer.BuildHueStreamPacket(AreaId, CreateChannelColors(1))[11]).ToArray();

        Assert.Equal(new byte[] { 254, 255, 0 }, sequences);
    }

    [Fact]
    public async Task V2Packet_StandaloneBuilderDoesNotChangeStreamAreaAndStopClearsBinding()
    {
        var clock = new ManualTimeProvider();
        var connection = new TestDtlsConnection();
        var streamer = CreateStreamer(_loggerMock.Object, (_, _, _, _) => Task.FromResult<IHueDtlsConnection>(connection), clock);
        var colors = CreateChannelColors(1);
        Assert.Throws<InvalidOperationException>(() => streamer.BuildHueStreamPacket(colors));
        await streamer.StartStreamAsync("192.168.1.100", "app-key", "client-key");
        Assert.True(await streamer.SendColors(AreaId.ToUpperInvariant(), colors));

        var independentPacket = streamer.BuildHueStreamPacket("1a8d99cc-967b-44f2-9202-43f976c0fa6b", colors);
        Assert.Equal("1a8d99cc-967b-44f2-9202-43f976c0fa6b", Encoding.ASCII.GetString(independentPacket, 16, 36));
        Assert.Equal(AreaId, Encoding.ASCII.GetString(streamer.BuildHueStreamPacket(colors), 16, 36));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(2, connection.SentPackets.Count);
        Assert.All(connection.SentPackets, packet => Assert.Equal(AreaId, Encoding.ASCII.GetString(packet, 16, 36)));

        streamer.StopStream();
        Assert.Throws<InvalidOperationException>(() => streamer.BuildHueStreamPacket(colors));
    }

    [Fact]
    public async Task V2Packet_ConfigurationStartBindsAreaBeforeHandshakeCompletes()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamer = CreateStreamer(_loggerMock.Object, async (_, _, _, token) =>
        {
            entered.TrySetResult(true);
            await release.Task.WaitAsync(token);
            return new TestDtlsConnection();
        });
        var startup = streamer.StartStreamAsync(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "app-key",
            HueClientKey = "client-key",
            EntertainmentAreaId = AreaId
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(await streamer.SendColors("1a8d99cc-967b-44f2-9202-43f976c0fa6b", CreateChannelColors(1)).WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal(AreaId, Encoding.ASCII.GetString(streamer.BuildHueStreamPacket(CreateChannelColors(1)), 16, 36));
        }
        finally
        {
            release.TrySetResult(true);
            await startup.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("area-id")]
    [InlineData("00112233445566778899aabbccddeeff")]
    [InlineData("{00112233-4455-6677-8899-aabbccddeeff}")]
    public async Task V2Packet_ConfigurationStartRejectsInvalidAreaBeforeTransportWork(string? areaId)
    {
        var connections = 0;
        var streamer = CreateStreamer(_loggerMock.Object, (_, _, _, _) =>
        {
            connections++;
            return Task.FromResult<IHueDtlsConnection>(new TestDtlsConnection());
        });

        await Assert.ThrowsAsync<ArgumentException>(() => streamer.StartStreamAsync(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "app-key",
            HueClientKey = "client-key",
            EntertainmentAreaId = areaId!
        }));

        Assert.Equal(0, connections);
        Assert.False(streamer.IsHealthy());
        Assert.Throws<InvalidOperationException>(() => streamer.BuildHueStreamPacket(CreateChannelColors(1)));
    }

    [Fact]
    public async Task V2Packet_InvalidConfigurationStartPreservesAnExistingAreaBinding()
    {
        var connection = new TestDtlsConnection();
        var connections = 0;
        var streamer = CreateStreamer(_loggerMock.Object, (_, _, _, _) =>
        {
            connections++;
            return Task.FromResult<IHueDtlsConnection>(connection);
        });
        await streamer.StartStreamAsync("192.168.1.100", "app-key", "client-key");
        Assert.True(await streamer.SendColors(AreaId, CreateChannelColors(1)));

        await Assert.ThrowsAsync<ArgumentException>(() => streamer.StartStreamAsync(new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.101",
            HueAppKey = "new-app",
            HueClientKey = "new-client",
            EntertainmentAreaId = "invalid-area"
        }));

        Assert.Equal(1, connections);
        Assert.True(streamer.IsHealthy());
        Assert.Equal(AreaId, Encoding.ASCII.GetString(streamer.BuildHueStreamPacket(CreateChannelColors(1)), 16, 36));
    }

    [Fact]
    public async Task V2Packet_ReconnectCallbackCannotMutateTheOwnedFrame()
    {
        var clock = new ManualTimeProvider();
        var initialConnection = new TestDtlsConnection();
        var replacementConnection = new TestDtlsConnection();
        var connections = 0;
        var streamer = CreateStreamer(_loggerMock.Object,
            (_, _, _, _) => Task.FromResult<IHueDtlsConnection>(++connections == 1 ? initialConnection : replacementConnection), clock);
        await streamer.StartStreamAsync("192.168.1.100", "app-key", "client-key");
        var rgb = new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9a, 0xbc };
        var expected = (byte[])rgb.Clone();
        var callerColors = new Dictionary<int, byte[]> { [255] = rgb };
        streamer.OnBeforeReconnectWithCancellation = _ =>
        {
            rgb[0] = 99;
            callerColors.Clear();
            callerColors[256] = new byte[] { 1, 2, 3, 4, 5, 6 };
            return Task.FromResult(true);
        };
        initialConnection.IsHealthy = false;

        Assert.True(await streamer.SendColors(AreaId, callerColors).WaitAsync(TimeSpan.FromSeconds(5)));
        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(2, connections);
        Assert.Equal(2, replacementConnection.SentPackets.Count);
        Assert.All(replacementConnection.SentPackets, packet =>
        {
            Assert.Equal(255, packet[52]);
            Assert.Equal(expected, packet[53..]);
        });
        Assert.True(callerColors.ContainsKey(256));
    }

    [Fact]
    public async Task V2Packet_TransportCallbackCannotChangeTheCachedKeepAliveFrame()
    {
        var clock = new ManualTimeProvider();
        var connection = new TestDtlsConnection();
        var streamer = CreateStreamer(_loggerMock.Object,
            (_, _, _, _) => Task.FromResult<IHueDtlsConnection>(connection), clock);
        await streamer.StartStreamAsync("192.168.1.100", "app-key", "client-key");
        var rgb = new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9a, 0xbc };
        var expected = (byte[])rgb.Clone();
        var callerColors = new Dictionary<int, byte[]> { [255] = rgb };
        connection.OnSend = () =>
        {
            rgb[0] = 99;
            callerColors.Clear();
            callerColors[256] = new byte[] { 1, 2, 3, 4, 5, 6 };
        };

        Assert.True(await streamer.SendColors(AreaId, callerColors));
        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(2, connection.SentPackets.Count);
        Assert.All(connection.SentPackets, packet =>
        {
            Assert.Equal(255, packet[52]);
            Assert.Equal(expected, packet[53..]);
        });
        Assert.Equal(0, streamer.PacketSendFailures);
    }

    [Theory]
    [InlineData(256, 6)]
    [InlineData(-1, 6)]
    [InlineData(0, 5)]
    [InlineData(0, 7)]
    public void V2Packet_InternalSerializerCannotBypassFrameValidation(int channelId, int colorLength)
    {
        var method = typeof(HueStreamer).GetMethod("BuildHueStreamPacketCore",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var colors = new Dictionary<int, byte[]> { [channelId] = new byte[colorLength] };

        var exception = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            method.Invoke(_streamer, new object[] { Guid.Parse(AreaId), colors }));

        Assert.IsAssignableFrom<ArgumentException>(exception.InnerException);
        Assert.Equal((byte)0, GetPrivateField<byte>(_streamer, "_sequenceNumber"));
    }

    [Fact]
    public void V2Packet_StandaloneBuilderAllocatesOnlyItsOutputBuffer()
    {
        var colors = CreateChannelColors(HueStreamer.MaxHueStreamChannels);
        _streamer.BuildHueStreamPacket(AreaId, colors);
        var before = GC.GetAllocatedBytesForCurrentThread();

        var packet = _streamer.BuildHueStreamPacket(AreaId, colors);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated <= packet.Length + 64, $"Allocated {allocated} bytes for a {packet.Length}-byte packet.");
    }

    private HueStreamer CreateStreamer(
        ILogger<HueStreamer> logger,
        Func<string, string, string, CancellationToken, Task<IHueDtlsConnection>>? connect = null,
        TimeProvider? timeProvider = null)
    {
        var streamer = new HueStreamer(logger, connect, timeProvider);
        _streamers.Add(streamer);
        return streamer;
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(10, 10)]
    [InlineData(11, 10)]
    public void MaxReconnectAttempts_ClampsConfiguredValue(int configured, int expected)
    {
        _streamer.MaxReconnectAttempts = configured;

        Assert.Equal(expected, _streamer.MaxReconnectAttempts);
    }

    [Fact]
    public async Task StartStreamAsync_WhenCanceledBeforeStartupDoesNotOpenTransport()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _streamer.StartStreamAsync(
                "192.168.1.100",
                "app-key",
                "00112233445566778899aabbccddeeff",
                cancellationSource.Token));
    }

    [Fact]
    public async Task StartStreamAsync_WhenHandshakeIsCanceledDoesNotLeaveABackgroundTask()
    {
        var streamer = CreateStreamer(
            _loggerMock.Object,
            async (_, _, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new TestDtlsConnection();
            });
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            streamer.StartStreamAsync(
                "127.0.0.1",
                "app-key",
                "00112233445566778899aabbccddeeff",
                cancellationSource.Token));

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.False(streamer.IsHealthy());
    }

    [Fact]
    public async Task StartStreamAsync_WhenCanceledReconnectOverlapsReplacementStart_PreservesReplacementStream()
    {
        var firstConnection = new TestDtlsConnection();
        var reconnectStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReconnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectionNumber = 0;
        var streamer = CreateStreamer(
            _loggerMock.Object,
            async (_, _, _, cancellationToken) =>
            {
                var number = Interlocked.Increment(ref connectionNumber);
                if (number == 1)
                {
                    return firstConnection;
                }

                if (number == 2)
                {
                    reconnectStarted.TrySetResult(true);
                    await releaseReconnect.Task.WaitAsync(cancellationToken);
                    return new TestDtlsConnection();
                }

                return new TestDtlsConnection();
            });

        await streamer.StartStreamAsync(
            "192.168.1.100",
            "app-key",
            "00112233445566778899aabbccddeeff");

        firstConnection.ThrowOnSend = true;
        Assert.False(await streamer.SendColors(
            AreaId,
            new Dictionary<int, byte[]> { [1] = new byte[] { 1, 1, 2, 2, 3, 3 } }));

        await reconnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var replacementStart = streamer.StartStreamAsync(
            "192.168.1.101",
            "replacement-app-key",
            "00112233445566778899aabbccddeeff");

        await Task.Delay(100);
        Assert.False(replacementStart.IsCompleted);

        releaseReconnect.TrySetResult(true);
        await replacementStart;

        Assert.True(streamer.IsHealthy());
        Assert.True(await streamer.SendColors(
            AreaId,
            new Dictionary<int, byte[]> { [1] = new byte[] { 4, 4, 5, 5, 6, 6 } }));
    }

    [Fact]
    public async Task ScheduleReconnect_WhenPreparationCancelsWithUnrelatedToken_DoesNotReadCanceledResult()
    {
        var connection = new TestDtlsConnection();
        var streamer = CreateStreamer(
            _loggerMock.Object,
            (_, _, _, _) => Task.FromResult<IHueDtlsConnection>(connection));

        await streamer.StartStreamAsync(
            "192.168.1.100",
            "app-key",
            "00112233445566778899aabbccddeeff");

        connection.IsHealthy = false;
        using var unrelatedCancellation = new CancellationTokenSource();
        unrelatedCancellation.Cancel();
        streamer.OnBeforeReconnectWithCancellation = _ =>
            Task.FromCanceled<bool>(unrelatedCancellation.Token);

        var scheduleReconnect = typeof(HueStreamer).GetMethod(
            "ScheduleReconnect",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var scheduledReconnect = (Task)scheduleReconnect.Invoke(
            streamer,
            new object[] { CancellationToken.None })!;

        await scheduledReconnect.WaitAsync(TimeSpan.FromSeconds(5));
        streamer.StopStream();
    }

    [Fact]
    public async Task StartStreamAsync_WhenStopRacesLifecycleCapture_DoesNotResurrectStream()
    {
        var logger = new CallbackLogger<HueStreamer>();
        HueStreamer? streamer = null;
        var stopCount = 0;
        var connectionCount = 0;
        logger.OnMessage = message =>
        {
            if (message == "DTLS stream stopped" && Interlocked.Exchange(ref stopCount, 1) == 0)
                streamer!.StopStream();
        };

        streamer = CreateStreamer(
            logger,
            (_, _, _, _) =>
            {
                Interlocked.Increment(ref connectionCount);
                return Task.FromResult<IHueDtlsConnection>(new TestDtlsConnection());
            });

        await streamer.StartStreamAsync(
            "192.168.1.100",
            "app-key",
            "00112233445566778899aabbccddeeff");

        Assert.Equal(0, connectionCount);
        Assert.False(streamer.IsHealthy());
        Assert.False(await streamer.SendColors(
            AreaId,
            new Dictionary<int, byte[]> { [1] = new byte[] { 1, 1, 2, 2, 3, 3 } }));
        Assert.Equal(0, streamer.ReconnectAttempts);
    }

    [Fact]
    public async Task StartStreamAsync_WithConfig_DoesNotRetainReconnectTargetAfterStopWinsAtCompletion()
    {
        var logger = new CallbackLogger<HueStreamer>();
        HueStreamer? streamer = null;
        var stopCount = 0;
        var connectionCount = 0;
        logger.OnMessage = message =>
        {
            if (message == "Managed DTLS tunnel started to 192.168.1.100:2100" &&
                Interlocked.Exchange(ref stopCount, 1) == 0)
            {
                streamer!.StopStream();
            }
        };

        streamer = CreateStreamer(
            logger,
            (_, _, _, _) =>
            {
                Interlocked.Increment(ref connectionCount);
                return Task.FromResult<IHueDtlsConnection>(new TestDtlsConnection());
            });

        var config = new PluginConfiguration
        {
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "app-key",
            HueClientKey = "stream-client-key",
            EntertainmentAreaId = AreaId
        };

        await streamer.StartStreamAsync(config);

        Assert.Equal(1, connectionCount);
        Assert.False(streamer.IsHealthy());
        streamer.MaxReconnectAttempts = 1;
        Assert.False(await streamer.SendColors(
            AreaId,
            new Dictionary<int, byte[]> { [1] = new byte[] { 1, 1, 2, 2, 3, 3 } }));
        Assert.Equal(1, connectionCount);
        Assert.Equal(0, streamer.ReconnectAttempts);
    }

    [Fact]
    public void StopStream_DisposesRetiredLifecycleCancellationSource()
    {
        var retiredLifecycle = GetPrivateField<CancellationTokenSource>(_streamer, "_streamLifecycleCts");

        _streamer.StopStream();

        Assert.Throws<ObjectDisposedException>(() => retiredLifecycle.Token.Register(static () => { }).Dispose());
    }

    [Fact]
    public async Task SendColors_WhenReconnectStartupFails_RetainsTargetForLaterRetry()
    {
        var firstConnection = new TestDtlsConnection();
        var failedReconnectStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectionNumber = 0;
        var streamer = CreateStreamer(
            _loggerMock.Object,
            (_, _, _, _) =>
            {
                var number = Interlocked.Increment(ref connectionNumber);
                if (number == 1)
                    return Task.FromResult<IHueDtlsConnection>(firstConnection);

                if (number == 2)
                {
                    failedReconnectStarted.TrySetResult(true);
                    return Task.FromResult<IHueDtlsConnection>(new TestDtlsConnection { IsHealthy = false });
                }

                return Task.FromResult<IHueDtlsConnection>(new TestDtlsConnection());
            });

        await streamer.StartStreamAsync(
            "192.168.1.100",
            "app-key",
            "00112233445566778899aabbccddeeff");

        firstConnection.ThrowOnSend = true;
        var colors = new Dictionary<int, byte[]> { [1] = new byte[] { 1, 1, 2, 2, 3, 3 } };
        Assert.False(await streamer.SendColors(AreaId, colors));

        await failedReconnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The first reconnect returns an unhealthy connection. The saved target must
        // survive that failure so this subsequent frame can perform attempt two.
        Assert.True(await streamer.SendColors(AreaId, colors));
        Assert.Equal(3, connectionNumber);
        Assert.Equal(2, streamer.ReconnectAttempts);
        Assert.True(streamer.IsHealthy());

        streamer.StopStream();
    }

    [Fact]
    public async Task SendColors_WhenTransportIsDisposed_TriggersReconnect()
    {
        var firstConnection = new TestDtlsConnection { ThrowObjectDisposedOnSend = true };
        var reconnectStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectionNumber = 0;
        var streamer = CreateStreamer(
            _loggerMock.Object,
            (_, _, _, _) =>
            {
                var number = Interlocked.Increment(ref connectionNumber);
                if (number == 1)
                    return Task.FromResult<IHueDtlsConnection>(firstConnection);

                reconnectStarted.TrySetResult(true);
                return Task.FromResult<IHueDtlsConnection>(new TestDtlsConnection());
            });

        await streamer.StartStreamAsync(
            "192.168.1.100",
            "app-key",
            "00112233445566778899aabbccddeeff");

        var colors = new Dictionary<int, byte[]> { [1] = new byte[] { 1, 1, 2, 2, 3, 3 } };
        Assert.False(await streamer.SendColors(AreaId, colors));

        await reconnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The next frame joins the in-flight bounded reconnect and proves that a
        // transport disposal cannot leave the stream permanently wedged.
        Assert.True(await streamer.SendColors(AreaId, colors));
        Assert.Equal(2, connectionNumber);
        Assert.Equal(1, streamer.ReconnectAttempts);

        streamer.StopStream();
    }

    [Fact]
    public async Task SendColors_WhenCanceledBeforeWriteReturnsFalse()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        var sent = await _streamer.SendColors(
            AreaId,
            new Dictionary<int, byte[]> { [1] = new byte[] { 1, 1, 2, 2, 3, 3 } },
            cancellationToken: cancellationSource.Token);

        Assert.False(sent);
    }

    [Fact]
    public async Task SendColors_ReportsPacketTelemetryAndThresholdSkips()
    {
        var connection = new TestDtlsConnection();
        SetPrivateField(_streamer, "_dtlsConnection", connection);
        var colors = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 10, 10, 20, 20, 30, 30 }
        };

        Assert.True(await _streamer.SendColors(AreaId, colors));
        Assert.True(await _streamer.SendColors(AreaId, colors, colorChangeThreshold: 1));

        Assert.Equal(1, _streamer.PacketsSent);
        Assert.Equal(1, _streamer.PacketsSkippedByThreshold);
        Assert.Equal(0, _streamer.PacketSendFailures);
        Assert.Equal(0, _streamer.ReconnectAttempts);
    }

    [Fact]
    public async Task SendColors_WhenThresholdWouldSkipStillReconnectsAnUnhealthyStream()
    {
        var colors = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 10, 10, 20, 20, 30, 30 }
        };

        // Keep the prior colors identical so the threshold would suppress the send if
        // health were checked after threshold filtering.
        SetPrivateField(_streamer, "_dtlsConnection", new TestDtlsConnection { IsHealthy = false });
        SetPrivateField(_streamer, "_lastSentColors", new Dictionary<int, byte[]>
        {
            [1] = (byte[])colors[1].Clone()
        });

        var sent = await _streamer.SendColors(AreaId, colors, colorChangeThreshold: 1);

        Assert.False(sent);
        Assert.Equal(0, _streamer.PacketsSkippedByThreshold);
        Assert.Equal(1, _streamer.PacketSendFailures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeepAlive_ResendsStaticOrBlackFramesBeyondTheBridgeIdleWindow(bool black)
    {
        var clock = new ManualTimeProvider();
        var connection = new TestDtlsConnection();
        var streamer = CreateStreamer(
            _loggerMock.Object,
            (_, _, _, _) => Task.FromResult<IHueDtlsConnection>(connection),
            clock);
        await streamer.StartStreamAsync("192.168.1.100", "app-key", "client-key");
        var colors = black
            ? new Dictionary<int, byte[]> { [1] = new byte[6] }
            : CreateChannelColors(1);

        Assert.True(await streamer.SendColors(AreaId, colors));
        for (var second = 0; second < 30; second++)
        {
            Assert.True(await streamer.SendColors(AreaId, colors, colorChangeThreshold: 1));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(7, streamer.PacketsSent);
        Assert.Equal(30, streamer.PacketsSkippedByThreshold);
        Assert.Equal(0, streamer.PacketSendFailures);
        var originalPacket = connection.SentPackets[0];
        for (var index = 1; index < connection.SentPackets.Count; index++)
        {
            var packet = connection.SentPackets[index];
            Assert.Equal((byte)index, packet[11]);
            Assert.Equal(originalPacket[16..], packet[16..]);
        }
    }

    [Fact]
    public async Task KeepAlive_WhenProducerPreservesLastColors_UsesAnOwnedSnapshotWithoutNewFrames()
    {
        var clock = new ManualTimeProvider();
        var connection = new TestDtlsConnection();
        var streamer = CreateStreamer(
            _loggerMock.Object,
            (_, _, _, _) => Task.FromResult<IHueDtlsConnection>(connection),
            clock);
        await streamer.StartStreamAsync("192.168.1.100", "app-key", "client-key");
        var colors = CreateChannelColors(1);
        Assert.True(await streamer.SendColors(AreaId, colors));
        var sentColors = connection.SentPackets[0][16..];
        colors[0][0] = 99;
        colors.Clear();

        // Video and audio KeepLastColors branches make no further SendColors calls.
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(7, streamer.PacketsSent);
        Assert.All(connection.SentPackets, packet => Assert.Equal(sentColors, packet[16..]));
        Assert.Equal(0, streamer.PacketsSkippedByThreshold);
    }

    [Fact]
    public async Task KeepAlive_DoesNotInventColorsBeforeTheFirstSuccessfulFrame()
    {
        var clock = new ManualTimeProvider();
        var connection = new TestDtlsConnection();
        var streamer = CreateStreamer(
            _loggerMock.Object,
            (_, _, _, _) => Task.FromResult<IHueDtlsConnection>(connection),
            clock);
        await streamer.StartStreamAsync("192.168.1.100", "app-key", "client-key");

        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Empty(connection.SentPackets);
        Assert.Equal(0, streamer.ReconnectAttempts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task SendColors_AfterInitialIdleWindow_ReactivatesBeforeTheFirstFrameOrFailsClosed(int retryAttempts)
    {
        var clock = new ManualTimeProvider();
        var initialConnection = new TestDtlsConnection();
        var replacementConnection = new TestDtlsConnection();
        var connections = 0;
        var activations = 0;
        var streamer = CreateStreamer(
            _loggerMock.Object,
            (_, _, _, _) =>
            {
                connections++;
                if (connections == 1)
                    return Task.FromResult<IHueDtlsConnection>(initialConnection);

                Assert.Equal(1, activations);
                Assert.Empty(initialConnection.SentPackets);
                return Task.FromResult<IHueDtlsConnection>(replacementConnection);
            },
            clock);
        streamer.MaxReconnectAttempts = retryAttempts;
        streamer.OnBeforeReconnectWithCancellation = _ =>
        {
            activations++;
            return Task.FromResult(true);
        };
        await streamer.StartStreamAsync("192.168.1.100", "app-key", "client-key");

        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.True(initialConnection.IsHealthy);
        Assert.False(streamer.IsHealthy());
        Assert.Empty(initialConnection.SentPackets);
        var colors = CreateChannelColors(1);
        Assert.Equal(retryAttempts > 0, await streamer.SendColors(AreaId, colors));

        Assert.Empty(initialConnection.SentPackets);
        Assert.Equal(retryAttempts, streamer.ReconnectAttempts);
        Assert.Equal(retryAttempts, activations);
        Assert.Equal(retryAttempts + 1, connections);
        if (retryAttempts > 0)
        {
            Assert.Equal(colors[0], Assert.Single(replacementConnection.SentPackets)[53..]);
            Assert.True(streamer.IsHealthy());
        }
        else
        {
            Assert.Empty(replacementConnection.SentPackets);
            Assert.Equal(1, streamer.PacketSendFailures);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task KeepAlive_AfterSuspendedTimer_ReactivatesBeforeResendingOrFailsClosed(int retryAttempts)
    {
        var clock = new ManualTimeProvider();
        var initialConnection = new TestDtlsConnection();
        var replacementConnection = new TestDtlsConnection();
        var connections = 0;
        var activations = 0;
        var streamer = CreateStreamer(
            _loggerMock.Object,
            (_, _, _, _) => Task.FromResult<IHueDtlsConnection>(
                ++connections == 1 ? initialConnection : replacementConnection),
            clock);
        streamer.MaxReconnectAttempts = retryAttempts;
        streamer.OnBeforeReconnectWithCancellation = _ =>
        {
            activations++;
            Assert.Single(initialConnection.SentPackets);
            return Task.FromResult(true);
        };
        await streamer.StartStreamAsync("192.168.1.100", "app-key", "client-key");
        Assert.True(await streamer.SendColors(AreaId, CreateChannelColors(1)));

        clock.ElapseWithoutCallbacks(TimeSpan.FromSeconds(30));
        Assert.True(initialConnection.IsHealthy);
        Assert.False(streamer.IsHealthy());
        clock.Timers[0].InvokeQueuedCallback();
        await GetPrivateField<Task>(streamer, "_keepAliveTask").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(initialConnection.SentPackets);
        Assert.Equal(retryAttempts, streamer.ReconnectAttempts);
        Assert.Equal(retryAttempts, activations);
        Assert.Equal(retryAttempts + 1, connections);
        if (retryAttempts > 0)
        {
            Assert.Equal(initialConnection.SentPackets[0][16..], Assert.Single(replacementConnection.SentPackets)[16..]);
            Assert.True(streamer.IsHealthy());
        }
        else
        {
            Assert.Empty(replacementConnection.SentPackets);
        }
    }

    [Theory]
    [InlineData("startup")]
    [InlineData("frame")]
    [InlineData("stop")]
    public async Task SendColors_WhenFirstFrameReactivationIsCanceled_DoesNotWriteOrInstallATransport(string cancelOwner)
    {
        var clock = new ManualTimeProvider();
        var connection = new TestDtlsConnection();
        var connections = 0;
        var activationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamer = CreateStreamer(
            _loggerMock.Object,
            (_, _, _, _) =>
            {
                connections++;
                return Task.FromResult<IHueDtlsConnection>(connection);
            },
            clock);
        using var startupCancellation = new CancellationTokenSource();
        using var frameCancellation = new CancellationTokenSource();
        streamer.OnBeforeReconnectWithCancellation = async token =>
        {
            activationStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        };
        await streamer.StartStreamAsync("192.168.1.100", "app-key", "client-key", startupCancellation.Token);
        clock.Advance(TimeSpan.FromSeconds(30));

        var send = streamer.SendColors(AreaId, CreateChannelColors(1), cancellationToken: frameCancellation.Token);
        await activationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelOwner == "startup")
            startupCancellation.Cancel();
        else if (cancelOwner == "frame")
            frameCancellation.Cancel();
        else
            streamer.StopStream();

        Assert.False(await send.WaitAsync(TimeSpan.FromSeconds(2)));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1, connections);
        Assert.Empty(connection.SentPackets);
        Assert.False(streamer.IsHealthy());
    }

    [Fact]
    public async Task KeepAlive_DoesNotDuplicateRegularTrafficAndUsesTheLatestSuccessfulColors()
    {
        var clock = new ManualTimeProvider();
        var connection = new TestDtlsConnection();
        var streamer = CreateStreamer(
            _loggerMock.Object,
            (_, _, _, _) => Task.FromResult<IHueDtlsConnection>(connection),
            clock);
        await streamer.StartStreamAsync("192.168.1.100", "app-key", "client-key");
        for (byte second = 0; second < 20; second++)
        {
            Assert.True(await streamer.SendColors(
                AreaId,
                new Dictionary<int, byte[]> { [1] = new byte[] { second, 0, 2, 0, 3, 0 } }));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(20, streamer.PacketsSent);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(20, streamer.PacketsSent);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(21, streamer.PacketsSent);
        Assert.Equal(connection.SentPackets[19][16..], connection.SentPackets[20][16..]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeepAlive_WhenStartupOrFrameTokenIsCanceled_DoesNotSendOrReconnect(bool cancelStartup)
    {
        var clock = new ManualTimeProvider();
        var connection = new TestDtlsConnection();
        var streamer = CreateStreamer(
            _loggerMock.Object,
            (_, _, _, _) => Task.FromResult<IHueDtlsConnection>(connection),
            clock);
        using var cancellation = new CancellationTokenSource();
        await streamer.StartStreamAsync(
            "192.168.1.100", "app-key", "client-key",
            cancelStartup ? cancellation.Token : CancellationToken.None);
        Assert.True(await streamer.SendColors(
            AreaId, CreateChannelColors(1),
            cancellationToken: cancelStartup ? CancellationToken.None : cancellation.Token));

        cancellation.Cancel();
        clock.Advance(TimeSpan.FromSeconds(15));
        connection.IsHealthy = false;
        clock.Advance(TimeSpan.FromSeconds(15));

        Assert.Equal(1, streamer.PacketsSent);
        Assert.Equal(0, streamer.ReconnectAttempts);
    }

    [Fact]
    public async Task KeepAlive_WhenStoppedOrReplaced_RejectsAlreadyQueuedTimerCallbacks()
    {
        var clock = new ManualTimeProvider();
        var firstConnection = new TestDtlsConnection();
        var secondConnection = new TestDtlsConnection();
        var connectionCount = 0;
        var streamer = CreateStreamer(
            _loggerMock.Object,
            (_, _, _, _) => Task.FromResult<IHueDtlsConnection>(
                ++connectionCount == 1 ? firstConnection : secondConnection),
            clock);
        await streamer.StartStreamAsync("192.168.1.100", "app-key", "client-key");
        Assert.True(await streamer.SendColors(AreaId, CreateChannelColors(1)));
        var retiredTimer = clock.Timers[0];

        streamer.StopStream();
        clock.Advance(TimeSpan.FromSeconds(30));
        retiredTimer.InvokeQueuedCallback();
        Assert.True(retiredTimer.IsDisposed);
        Assert.Single(firstConnection.SentPackets);
        Assert.Equal(0, streamer.ReconnectAttempts);

        await streamer.StartStreamAsync("192.168.1.101", "new-app-key", "new-client-key");
        clock.Advance(TimeSpan.FromSeconds(1));
        retiredTimer.InvokeQueuedCallback();
        Assert.Empty(secondConnection.SentPackets);
        Assert.True(await streamer.SendColors(
            "1a8d99cc-967b-44f2-9202-43f976c0fa6b", new Dictionary<int, byte[]> { [2] = new byte[] { 9, 9, 8, 8, 7, 7 } }));
        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(2, secondConnection.SentPackets.Count);
        Assert.Equal(secondConnection.SentPackets[0][16..], secondConnection.SentPackets[1][16..]);
        Assert.Single(firstConnection.SentPackets);
    }

    [Fact]
    public async Task KeepAlive_WhenConnectionFails_ReconnectsAndResendsWithoutNewProducerFrames()
    {
        var clock = new ManualTimeProvider();
        var firstConnection = new TestDtlsConnection();
        var replacementConnection = new TestDtlsConnection();
        var connectionCount = 0;
        var streamer = CreateStreamer(
            _loggerMock.Object,
            (_, _, _, _) => Task.FromResult<IHueDtlsConnection>(
                ++connectionCount == 1 ? firstConnection : replacementConnection),
            clock);
        await streamer.StartStreamAsync("192.168.1.100", "app-key", "client-key");
        Assert.True(await streamer.SendColors(AreaId, CreateChannelColors(1)));
        firstConnection.ThrowOnSend = true;

        clock.Advance(TimeSpan.FromSeconds(5));
        await GetPrivateField<Task>(streamer, "_keepAliveTask").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, streamer.ReconnectAttempts);
        Assert.Equal(1, streamer.PacketSendFailures);
        Assert.Single(replacementConnection.SentPackets);
        Assert.Equal(firstConnection.SentPackets[0][16..], replacementConnection.SentPackets[0][16..]);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(3, replacementConnection.SentPackets.Count);
        Assert.Equal(1, clock.Timers.Count(timer => !timer.IsDisposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeepAlive_WhenRetryIsCanceled_DoesNotOpenAnotherConnection(bool stopStream)
    {
        var clock = new ManualTimeProvider();
        var connection = new TestDtlsConnection();
        var connectionCount = 0;
        var streamer = CreateStreamer(
            _loggerMock.Object,
            (_, _, _, _) =>
            {
                connectionCount++;
                return Task.FromResult<IHueDtlsConnection>(connection);
            },
            clock);
        using var cancellation = new CancellationTokenSource();
        await streamer.StartStreamAsync("192.168.1.100", "app-key", "client-key");
        Assert.True(await streamer.SendColors(
            AreaId, CreateChannelColors(1), cancellationToken: cancellation.Token));
        connection.IsHealthy = false;

        clock.Advance(TimeSpan.FromSeconds(5));
        var keepAlive = GetPrivateField<Task>(streamer, "_keepAliveTask");
        if (stopStream)
            streamer.StopStream();
        else
            cancellation.Cancel();
        await keepAlive.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(1, connectionCount);
        Assert.Single(connection.SentPackets);
        Assert.Equal(1, streamer.ReconnectAttempts);
    }

    [Fact]
    public async Task KeepAlive_WhenReconnectBudgetIsExhausted_DoesNotStartUnboundedRetries()
    {
        var clock = new ManualTimeProvider();
        var connection = new TestDtlsConnection();
        var streamer = CreateStreamer(
            _loggerMock.Object,
            (_, _, _, _) => Task.FromResult<IHueDtlsConnection>(connection),
            clock);
        streamer.MaxReconnectAttempts = 1;
        streamer.OnBeforeReconnect = () => Task.FromResult(false);
        await streamer.StartStreamAsync("192.168.1.100", "app-key", "client-key");
        Assert.True(await streamer.SendColors(AreaId, CreateChannelColors(1)));
        connection.IsHealthy = false;

        clock.Advance(TimeSpan.FromSeconds(5));
        await GetPrivateField<Task>(streamer, "_keepAliveTask").WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(60));

        Assert.Equal(1, streamer.ReconnectAttempts);
        Assert.Single(connection.SentPackets);
    }

    [Fact]
    public async Task SendColors_WhenShortColorFollowsValidSend_FailsClosedWithoutReconnect()
    {
        var connection = new TestDtlsConnection();
        SetPrivateField(_streamer, "_dtlsConnection", connection);
        var validColors = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 10, 10, 20, 20, 30, 30 }
        };

        Assert.True(await _streamer.SendColors(AreaId, validColors));

        var malformedColors = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 10, 10, 20 }
        };
        Assert.False(await _streamer.SendColors(AreaId, malformedColors, colorChangeThreshold: 1));

        Assert.Equal(1, _streamer.PacketsSent);
        Assert.Equal(1, _streamer.PacketSendFailures);
        Assert.Equal(0, _streamer.ReconnectAttempts);
        Assert.True(connection.IsHealthy);
    }

    [Fact]
    public async Task SendColors_WhenNullColorFollowsValidSend_FailsClosedWithoutReconnect()
    {
        var connection = new TestDtlsConnection();
        SetPrivateField(_streamer, "_dtlsConnection", connection);
        var validColors = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 10, 10, 20, 20, 30, 30 }
        };

        Assert.True(await _streamer.SendColors(AreaId, validColors));

        var malformedColors = new Dictionary<int, byte[]>
        {
            [1] = null!
        };
        Assert.False(await _streamer.SendColors(AreaId, malformedColors, colorChangeThreshold: 1));

        Assert.Equal(1, _streamer.PacketsSent);
        Assert.Equal(1, _streamer.PacketSendFailures);
        Assert.Equal(0, _streamer.ReconnectAttempts);
        Assert.True(connection.IsHealthy);
    }

    [Fact]
    public async Task SendColors_WhenMalformedFrameTargetsUnhealthyStream_DoesNotReconnect()
    {
        var connection = new TestDtlsConnection { IsHealthy = false };
        SetPrivateField(_streamer, "_dtlsConnection", connection);

        var malformedColors = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 1, 2, 3 }
        };

        Assert.False(await _streamer.SendColors(AreaId, malformedColors, colorChangeThreshold: 1));

        Assert.Equal(1, _streamer.PacketSendFailures);
        Assert.Equal(0, _streamer.ReconnectAttempts);
        Assert.False(connection.IsHealthy);
    }

    [Fact]
    public async Task SendColors_WhenFrameExceedsDtlsPacketBudget_FailsBeforeTransportSendOrReconnect()
    {
        var connection = new TestDtlsConnection();
        SetPrivateField(_streamer, "_dtlsConnection", connection);
        var oversizedColors = CreateChannelColors(HueStreamer.MaxHueStreamChannels + 1);

        Assert.False(await _streamer.SendColors(AreaId, oversizedColors));

        Assert.Equal(0, connection.SendCount);
        Assert.Equal(1, _streamer.PacketSendFailures);
        Assert.Equal(0, _streamer.ReconnectAttempts);
        Assert.True(connection.IsHealthy);
    }

    [Fact]
    public void HuePskTlsClient_UsesHueDtls12PskContract()
    {
        var client = new HuePskTlsClient(
            new Org.BouncyCastle.Tls.Crypto.Impl.BC.BcTlsCrypto(new SecureRandom()),
            "app-key",
            new byte[16],
            handshakeTimeoutMilliseconds: 5000);

        Assert.Equal(ProtocolVersion.DTLSv12, Assert.Single(HuePskTlsClient.HueProtocolVersions()));
        Assert.Equal(CipherSuite.TLS_PSK_WITH_AES_128_GCM_SHA256, HuePskTlsClient.HueCipherSuite);
        Assert.Equal(5000, client.GetHandshakeTimeoutMillis());
    }

    [Fact]
    public void HueDatagramTransport_RoundTripsConnectedUdpDatagrams()
    {
        using var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sender.Connect((IPEndPoint)receiver.LocalEndPoint!);
        using var transport = new HueDatagramTransport(sender);
        Assert.InRange(sender.SendTimeout, 1, 1000);

        var payload = "Hue DTLS test"u8.ToArray();
        receiver.SendTo(payload, sender.LocalEndPoint!);

        var buffer = new byte[64];
        var received = transport.Receive(buffer, 0, buffer.Length, 1000);

        Assert.Equal(payload.Length, received);
        Assert.Equal(payload, buffer[..received]);
        Assert.True(transport.IsOpen);
        transport.Close();
        Assert.False(transport.IsOpen);
    }

    #region Color Encoding Tests

    [Theory]
    [InlineData("192.168.1.100", "192.168.1.100:2100")]
    [InlineData("2001:db8::10", "[2001:db8::10]:2100")]
    public void FormatBridgeEndpoint_AddsPortAndBracketsIpv6(string address, string expected)
    {
        Assert.Equal(expected, HueStreamer.FormatBridgeEndpoint(address));
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0, 0)]         // Black
    [InlineData(255, 255, 255, 255, 255, 255)] // White
    [InlineData(255, 0, 0, 255, 0, 0)]      // Red
    [InlineData(0, 255, 0, 0, 255, 0)]      // Green
    [InlineData(0, 0, 255, 0, 0, 255)]      // Blue
    [InlineData(128, 128, 128, 128, 128, 128)] // Gray
    [InlineData(1, 1, 1, 1, 1, 1)]
    public void EncodeColorFor16Bit_UsesFullRangeProductionEncoder(
        byte inputR, byte inputG, byte inputB,
        byte expectedR, byte expectedG, byte expectedB)
    {
        var encoded = HueSyncService.EncodeRgb16(inputR, inputG, inputB);
        Assert.Equal(new byte[] { expectedR, expectedR, expectedG, expectedG, expectedB, expectedB }, encoded);
    }

    [Fact]
    public void HueStreamPacket_HasCorrectHeader()
    {
        // Arrange
        var expectedHeader = "HueStream"u8.ToArray();

        // Act & Assert
        Assert.Equal(9, expectedHeader.Length);
        Assert.Equal((byte)'H', expectedHeader[0]);
        Assert.Equal((byte)'u', expectedHeader[1]);
        Assert.Equal((byte)'e', expectedHeader[2]);
        Assert.Equal((byte)'S', expectedHeader[3]);
        Assert.Equal((byte)'t', expectedHeader[4]);
        Assert.Equal((byte)'r', expectedHeader[5]);
        Assert.Equal((byte)'e', expectedHeader[6]);
        Assert.Equal((byte)'a', expectedHeader[7]);
        Assert.Equal((byte)'m', expectedHeader[8]);
    }

    [Fact]
    public void ChannelColorData_Has6BytesPerChannel()
    {
        // Each channel has: R(2 bytes) + G(2 bytes) + B(2 bytes) = 6 bytes
        // Format: [R_hi, R_lo, G_hi, G_lo, B_hi, B_lo]
        byte r = 127, g = 64, b = 32;
        byte[] channelData = [r, r, g, g, b, b];

        Assert.Equal(6, channelData.Length);
        Assert.Equal(channelData[0], channelData[1]); // R duplicated
        Assert.Equal(channelData[2], channelData[3]); // G duplicated
        Assert.Equal(channelData[4], channelData[5]); // B duplicated
    }

    #endregion

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        target.GetType().GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(target, value);
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        return (T)target.GetType().GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(target)!;
    }

    private static Dictionary<int, byte[]> CreateChannelColors(int count)
    {
        var colors = new Dictionary<int, byte[]>(count);
        for (var channelId = 0; channelId < count; channelId++)
            colors[channelId] = new byte[] { 1, 1, 2, 2, 3, 3 };

        return colors;
    }

    private sealed class CallbackLogger<T> : ILogger<T>
    {
        public Action<string>? OnMessage { get; set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            OnMessage?.Invoke(formatter(state, exception));
        }
    }

    private sealed class TestDtlsConnection : IHueDtlsConnection
    {
        public bool IsHealthy { get; set; } = true;

        public int SendCount { get; private set; }

        public List<byte[]> SentPackets { get; } = new();

        public bool ThrowOnSend { get; set; }

        public bool ThrowObjectDisposedOnSend { get; set; }

        public Action? OnSend { get; set; }

        public void Send(byte[] buffer, int offset, int count)
        {
            SendCount++;
            if (ThrowObjectDisposedOnSend)
                throw new ObjectDisposedException("synthetic DTLS transport");

            if (ThrowOnSend)
            {
                IsHealthy = false;
                throw new IOException("synthetic DTLS failure");
            }

            OnSend?.Invoke();
            SentPackets.Add(buffer.AsSpan(offset, count).ToArray());
        }

        public void Close()
        {
            IsHealthy = false;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public List<ManualTimer> Timers { get; } = new();

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            Timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            var target = _timestamp + elapsed.Ticks;
            while (true)
            {
                var nextTimer = Timers
                    .Where(timer => !timer.IsDisposed && timer.NextTick <= target)
                    .MinBy(timer => timer.NextTick);
                if (nextTimer == null)
                    break;

                _timestamp = nextTimer.NextTick;
                nextTimer.Fire();
            }

            _timestamp = target;
        }

        public void ElapseWithoutCallbacks(TimeSpan elapsed)
        {
            _timestamp += elapsed.Ticks;
            foreach (var timer in Timers)
                timer.SkipElapsedTicks();
        }

        public sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider _clock;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private TimeSpan _period;

            public ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state)
            {
                _clock = clock;
                _callback = callback;
                _state = state;
            }

            public long NextTick { get; private set; }

            public bool IsDisposed { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (IsDisposed)
                    return false;

                NextTick = dueTime == Timeout.InfiniteTimeSpan
                    ? long.MaxValue
                    : _clock.GetTimestamp() + dueTime.Ticks;
                _period = period;
                return true;
            }

            public void Fire()
            {
                NextTick = _period > TimeSpan.Zero
                    ? NextTick + _period.Ticks
                    : long.MaxValue;
                _callback(_state);
            }

            public void InvokeQueuedCallback() => _callback(_state);

            public void SkipElapsedTicks()
            {
                while (_period > TimeSpan.Zero && NextTick < _clock.GetTimestamp())
                    NextTick += _period.Ticks;
            }

            public void Dispose() => IsDisposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }


    #region Color Change Detection Tests

    [Fact]
    public void HasSignificantColorChange_IdenticalColors_ReturnsFalse()
    {
        var current = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 100, 100, 100 } },
            { 1, new byte[] { 200, 200, 200 } }
        };
        var previous = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 100, 100, 100 } },
            { 1, new byte[] { 200, 200, 200 } }
        };

        var hasChange = HasSignificantColorChange(current, previous, threshold: 10);

        Assert.False(hasChange);
    }

    [Fact]
    public void HasSignificantColorChange_LargeChange_ReturnsTrue()
    {
        var current = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 100, 100, 100 } }
        };
        var previous = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 200, 200, 200 } }
        };

        var hasChange = HasSignificantColorChange(current, previous, threshold: 10);

        Assert.True(hasChange);
    }

    [Fact]
    public void HasSignificantColorChange_SmallChange_BelowThreshold_ReturnsFalse()
    {
        var current = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 100, 100, 100 } }
        };
        var previous = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 105, 103, 102 } }
        };

        var hasChange = HasSignificantColorChange(current, previous, threshold: 10);

        Assert.False(hasChange);
    }

    [Fact]
    public void HasSignificantColorChange_SmallChange_AboveThreshold_ReturnsTrue()
    {
        var current = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 100, 100, 100 } }
        };
        var previous = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 115, 100, 100 } } // R differs by 15
        };

        var hasChange = HasSignificantColorChange(current, previous, threshold: 10);

        Assert.True(hasChange);
    }

    [Fact]
    public void HasSignificantColorChange_NewChannel_ReturnsTrue()
    {
        var current = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 100, 100, 100 } },
            { 1, new byte[] { 200, 200, 200 } } // New channel
        };
        var previous = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 100, 100, 100 } }
        };

        var hasChange = HasSignificantColorChange(current, previous, threshold: 10);

        Assert.True(hasChange);
    }

    [Fact]
    public void HasSignificantColorChange_EmptyPrevious_ReturnsTrue()
    {
        var current = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 100, 100, 100 } }
        };
        var previous = new Dictionary<int, byte[]>();

        var hasChange = HasSignificantColorChange(current, previous, threshold: 10);

        Assert.True(hasChange);
    }

    [Fact]
    public void HasSignificantColorChange_ZeroThreshold_AnyChange_ReturnsTrue()
    {
        var current = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 100, 100, 100 } }
        };
        var previous = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 101, 100, 100 } } // Differs by 1
        };

        var hasChange = HasSignificantColorChange(current, previous, threshold: 0);

        Assert.True(hasChange);
    }

    #endregion

    #region Packet Construction Tests — Hue Entertainment API v2 format

    [Fact]
    public void BuildHueStreamPacket_StartsWithCorrectHeader()
    {
        // The packet must begin with the 9-byte ASCII magic "HueStream"
        var channelColors = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 127, 127, 64, 64, 32, 32 } }
        };

        var packet = _streamer.BuildHueStreamPacket(AreaId, channelColors);

        Assert.Equal((byte)'H', packet[0]);
        Assert.Equal((byte)'u', packet[1]);
        Assert.Equal((byte)'e', packet[2]);
        Assert.Equal((byte)'S', packet[3]);
        Assert.Equal((byte)'t', packet[4]);
        Assert.Equal((byte)'r', packet[5]);
        Assert.Equal((byte)'e', packet[6]);
        Assert.Equal((byte)'a', packet[7]);
        Assert.Equal((byte)'m', packet[8]);
    }

    [Fact]
    public void BuildHueStreamPacket_VersionBytesAreCorrect()
    {
        // Bytes [9] = 0x02 (major), [10] = 0x00 (minor)
        var channelColors = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 127, 127, 64, 64, 32, 32 } }
        };

        var packet = _streamer.BuildHueStreamPacket(AreaId, channelColors);

        Assert.Equal(0x02, packet[9]);  // major version
        Assert.Equal(0x00, packet[10]); // minor version
    }

    [Fact]
    public void BuildHueStreamPacket_ColorSpaceIsRgb()
    {
        // Byte [14] = 0x00 means RGB color space
        var channelColors = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 127, 127, 64, 64, 32, 32 } }
        };

        var packet = _streamer.BuildHueStreamPacket(AreaId, channelColors);

        Assert.Equal(0x00, packet[14]); // RGB color space
    }

    [Fact]
    public void BuildHueStreamPacket_SingleChannel_CorrectSize()
    {
        // Fixed prefix and UUID: 52 bytes, per channel: 7 bytes.
        var channelColors = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 127, 127, 64, 64, 32, 32 } }
        };

        var packet = _streamer.BuildHueStreamPacket(AreaId, channelColors);

        Assert.Equal(52 + 7, packet.Length);
    }

    [Fact]
    public void BuildHueStreamPacket_MultipleChannels_IncludesAllChannels()
    {
        // Three channels follow the 16-byte prefix and 36-byte UUID.
        var channelColors = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 127, 127, 0, 0, 0, 0 } },
            { 1, new byte[] { 0, 0, 127, 127, 0, 0 } },
            { 2, new byte[] { 0, 0, 0, 0, 127, 127 } }
        };

        var packet = _streamer.BuildHueStreamPacket(AreaId, channelColors);

        Assert.Equal(52 + 3 * 7, packet.Length);
    }

    [Fact]
    public void BuildHueStreamPacket_ChannelData_StartsWithChannelIdNotDeviceType()
    {
        var channelColors = new Dictionary<int, byte[]>
        {
            { 5, new byte[] { 100, 100, 50, 50, 25, 25 } }
        };

        var packet = _streamer.BuildHueStreamPacket(AreaId, channelColors);

        Assert.Equal(5, packet[52]);
    }

    [Fact]
    public void BuildHueStreamPacket_ChannelData_IdOccupiesOneByte()
    {
        var channelColors = new Dictionary<int, byte[]>
        {
            { 5, new byte[] { 100, 100, 50, 50, 25, 25 } }
        };

        var packet = _streamer.BuildHueStreamPacket(AreaId, channelColors);

        Assert.Equal(0x05, packet[52]);
        Assert.Equal(100, packet[53]);
    }

    [Fact]
    public void BuildHueStreamPacket_ChannelData_RgbBytesCorrect()
    {
        // Color bytes follow the UUID and one-byte channel ID.
        var channelColors = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF } }
        };

        var packet = _streamer.BuildHueStreamPacket(AreaId, channelColors);

        Assert.Equal(0xAA, packet[53]); // R hi
        Assert.Equal(0xBB, packet[54]); // R lo
        Assert.Equal(0xCC, packet[55]); // G hi
        Assert.Equal(0xDD, packet[56]); // G lo
        Assert.Equal(0xEE, packet[57]); // B hi
        Assert.Equal(0xFF, packet[58]); // B lo
    }

    [Fact]
    public void BuildHueStreamPacket_ContainsCanonicalAreaIdWithoutTerminator()
    {
        var channelColors = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 100, 100, 100, 100, 100, 100 } }
        };

        var packet = _streamer.BuildHueStreamPacket(AreaId, channelColors);
        Assert.Equal(AreaId, Encoding.ASCII.GetString(packet, 16, 36));
        Assert.Equal(59, packet.Length);
    }

    [Fact]
    public void BuildHueStreamPacket_RejectsMalformedChannelColor()
    {
        var channelColors = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 1, 2, 3 } }
        };

        Assert.Throws<ArgumentException>(() => _streamer.BuildHueStreamPacket(AreaId, channelColors));
    }

    [Fact]
    public void BuildHueStreamPacket_RejectsOutOfRangeChannelId()
    {
        var channelColors = new Dictionary<int, byte[]>
        {
            { 65536, new byte[] { 1, 1, 2, 2, 3, 3 } }
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => _streamer.BuildHueStreamPacket(AreaId, channelColors));
    }

    [Fact]
    public void BuildHueStreamPacket_AcceptsMaximumDtlsApplicationPayloadChannelCount()
    {
        var channelColors = CreateChannelColors(HueStreamer.MaxHueStreamChannels);

        var packet = _streamer.BuildHueStreamPacket(AreaId, channelColors);

        Assert.Equal(
            HueStreamer.HueStreamPacketHeaderBytes +
            HueStreamer.MaxHueStreamChannels * HueStreamer.HueStreamChannelBytes,
            packet.Length);
        Assert.InRange(packet.Length, 0, HueStreamer.MaxHueStreamPacketBytes);
    }

    [Fact]
    public void BuildHueStreamPacket_RejectsFirstChannelBeyondDtlsApplicationPayloadBudget()
    {
        var channelColors = CreateChannelColors(HueStreamer.MaxHueStreamChannels + 1);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            _streamer.BuildHueStreamPacket(AreaId, channelColors));

        Assert.Equal("channelColors", exception.ParamName);
        Assert.Equal(HueStreamer.MaxHueStreamChannels + 1, exception.ActualValue);
    }


    #endregion

    #region Coordinate Mapping Tests

    [Theory]
    [InlineData(-1.0, 160, 0)]   // Far left -> X = 0
    [InlineData(1.0, 160, 159)]  // Far right -> X = 159
    [InlineData(0.0, 160, 79)]   // Center -> X = 79
    public void MapHueCoordinateToPixelX_CorrectMapping(
        double hueX, int width, int expectedPixelX)
    {
        var pixelX = (int)(((hueX + 1.0) / 2.0) * (width - 1));
        Assert.Equal(expectedPixelX, pixelX);
    }

    [Theory]
    [InlineData(1.0, 90, 0)]    // Top -> Y = 0
    [InlineData(-1.0, 90, 89)]  // Bottom -> Y = 89
    [InlineData(0.0, 90, 44)]   // Center -> Y = 44
    public void MapHueCoordinateToPixelY_CorrectMapping(
        double hueZ, int height, int expectedPixelY)
    {
        var pixelY = (int)(((1.0 - hueZ) / 2.0) * (height - 1));
        Assert.Equal(expectedPixelY, pixelY);
    }

    #endregion

    #region Helper Methods (Simulating HueStreamer logic for change detection)

    private static bool HasSignificantColorChange(
        Dictionary<int, byte[]> current,
        Dictionary<int, byte[]> previous,
        int threshold)
    {
        if (previous.Count == 0 && current.Count > 0)
            return true;

        foreach (var (channelId, colors) in current)
        {
            if (!previous.TryGetValue(channelId, out var prevColors))
                return true;

            for (int i = 0; i < Math.Min(colors.Length, prevColors.Length); i++)
            {
                if (Math.Abs(colors[i] - prevColors[i]) > threshold)
                    return true;
            }
        }

        return false;
    }

    #endregion
}
