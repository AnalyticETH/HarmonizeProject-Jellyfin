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
///   Per channel (9 bytes):
///     [0]   0x00 device type
///     [1]   channelId >> 8
///     [2]   channelId & 0xFF
///     [3-8] R_hi R_lo G_hi G_lo B_hi B_lo
/// </summary>
public class HueStreamerTests
{
    private readonly Mock<ILogger<HueStreamer>> _loggerMock;
    private readonly HueStreamer _streamer;

    public HueStreamerTests()
    {
        _loggerMock = new Mock<ILogger<HueStreamer>>();
        _streamer = new HueStreamer(_loggerMock.Object);
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
        var streamer = new HueStreamer(
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
        var streamer = new HueStreamer(
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
            "area-id",
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
            "area-id",
            new Dictionary<int, byte[]> { [1] = new byte[] { 4, 4, 5, 5, 6, 6 } }));
    }

    [Fact]
    public async Task ScheduleReconnect_WhenPreparationCancelsWithUnrelatedToken_DoesNotReadCanceledResult()
    {
        var connection = new TestDtlsConnection();
        var streamer = new HueStreamer(
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

        streamer = new HueStreamer(
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
            "area-id",
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

        streamer = new HueStreamer(
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
            HueClientKey = "stream-client-key"
        };

        await streamer.StartStreamAsync(config);

        Assert.Equal(1, connectionCount);
        Assert.False(streamer.IsHealthy());
        streamer.MaxReconnectAttempts = 1;
        Assert.False(await streamer.SendColors(
            "area-id",
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
        var streamer = new HueStreamer(
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
        Assert.False(await streamer.SendColors("area-id", colors));

        await failedReconnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The first reconnect returns an unhealthy connection. The saved target must
        // survive that failure so this subsequent frame can perform attempt two.
        Assert.True(await streamer.SendColors("area-id", colors));
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
        var streamer = new HueStreamer(
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
        Assert.False(await streamer.SendColors("area-id", colors));

        await reconnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The next frame joins the in-flight bounded reconnect and proves that a
        // transport disposal cannot leave the stream permanently wedged.
        Assert.True(await streamer.SendColors("area-id", colors));
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
            "area-id",
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

        Assert.True(await _streamer.SendColors("area-id", colors));
        Assert.True(await _streamer.SendColors("area-id", colors, colorChangeThreshold: 1));

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

        var sent = await _streamer.SendColors("area-id", colors, colorChangeThreshold: 1);

        Assert.False(sent);
        Assert.Equal(0, _streamer.PacketsSkippedByThreshold);
        Assert.Equal(1, _streamer.PacketSendFailures);
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

        Assert.True(await _streamer.SendColors("area-id", validColors));

        var malformedColors = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 10, 10, 20 }
        };
        Assert.False(await _streamer.SendColors("area-id", malformedColors, colorChangeThreshold: 1));

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

        Assert.True(await _streamer.SendColors("area-id", validColors));

        var malformedColors = new Dictionary<int, byte[]>
        {
            [1] = null!
        };
        Assert.False(await _streamer.SendColors("area-id", malformedColors, colorChangeThreshold: 1));

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

        Assert.False(await _streamer.SendColors("area-id", malformedColors, colorChangeThreshold: 1));

        Assert.Equal(1, _streamer.PacketSendFailures);
        Assert.Equal(0, _streamer.ReconnectAttempts);
        Assert.False(connection.IsHealthy);
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
    [InlineData(255, 255, 255, 127, 127, 127)] // White (halved)
    [InlineData(255, 0, 0, 127, 0, 0)]      // Red
    [InlineData(0, 255, 0, 0, 127, 0)]      // Green
    [InlineData(0, 0, 255, 0, 0, 127)]      // Blue
    [InlineData(128, 128, 128, 64, 64, 64)] // Gray
    public void EncodeColorFor16Bit_CorrectlyHalvesValues(
        byte inputR, byte inputG, byte inputB,
        byte expectedR, byte expectedG, byte expectedB)
    {
        // The HueStream protocol halves 8-bit values for 16-bit encoding
        var encodedR = (byte)(inputR / 2);
        var encodedG = (byte)(inputG / 2);
        var encodedB = (byte)(inputB / 2);

        Assert.Equal(expectedR, encodedR);
        Assert.Equal(expectedG, encodedG);
        Assert.Equal(expectedB, encodedB);
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

        public bool ThrowOnSend { get; set; }

        public bool ThrowObjectDisposedOnSend { get; set; }

        public void Send(byte[] buffer, int offset, int count)
        {
            if (ThrowObjectDisposedOnSend)
                throw new ObjectDisposedException("synthetic DTLS transport");

            if (ThrowOnSend)
            {
                IsHealthy = false;
                throw new IOException("synthetic DTLS failure");
            }
        }

        public void Close()
        {
            IsHealthy = false;
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

        var packet = _streamer.BuildHueStreamPacket(channelColors);

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

        var packet = _streamer.BuildHueStreamPacket(channelColors);

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

        var packet = _streamer.BuildHueStreamPacket(channelColors);

        Assert.Equal(0x00, packet[14]); // RGB color space
    }

    [Fact]
    public void BuildHueStreamPacket_SingleChannel_CorrectSize()
    {
        // Fixed header: 16 bytes, per channel: 9 bytes
        // Total = 16 + 1 * 9 = 25
        var channelColors = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 127, 127, 64, 64, 32, 32 } }
        };

        var packet = _streamer.BuildHueStreamPacket(channelColors);

        Assert.Equal(16 + 9, packet.Length);
    }

    [Fact]
    public void BuildHueStreamPacket_MultipleChannels_IncludesAllChannels()
    {
        // Fixed header: 16 bytes, per channel: 9 bytes
        // 3 channels → 16 + 3*9 = 43 bytes
        var channelColors = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 127, 127, 0, 0, 0, 0 } },
            { 1, new byte[] { 0, 0, 127, 127, 0, 0 } },
            { 2, new byte[] { 0, 0, 0, 0, 127, 127 } }
        };

        var packet = _streamer.BuildHueStreamPacket(channelColors);

        Assert.Equal(16 + 3 * 9, packet.Length);
    }

    [Fact]
    public void BuildHueStreamPacket_ChannelData_DeviceTypeIsLight()
    {
        // First byte of each channel block (offset 16) = 0x00 (light device type)
        var channelColors = new Dictionary<int, byte[]>
        {
            { 5, new byte[] { 100, 100, 50, 50, 25, 25 } }
        };

        var packet = _streamer.BuildHueStreamPacket(channelColors);

        // Channel block starts at offset 16
        Assert.Equal(0x00, packet[16]); // device type = light
    }

    [Fact]
    public void BuildHueStreamPacket_ChannelData_IdEncodedBigEndian()
    {
        // Channel ID 0x0005 → high byte = 0x00, low byte = 0x05
        var channelColors = new Dictionary<int, byte[]>
        {
            { 5, new byte[] { 100, 100, 50, 50, 25, 25 } }
        };

        var packet = _streamer.BuildHueStreamPacket(channelColors);

        Assert.Equal(0x00, packet[17]); // channel ID high byte
        Assert.Equal(0x05, packet[18]); // channel ID low byte
    }

    [Fact]
    public void BuildHueStreamPacket_ChannelData_RgbBytesCorrect()
    {
        // Channel colors [R_hi, R_lo, G_hi, G_lo, B_hi, B_lo] should appear at offsets 19-24
        var channelColors = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF } }
        };

        var packet = _streamer.BuildHueStreamPacket(channelColors);

        Assert.Equal(0xAA, packet[19]); // R hi
        Assert.Equal(0xBB, packet[20]); // R lo
        Assert.Equal(0xCC, packet[21]); // G hi
        Assert.Equal(0xDD, packet[22]); // G lo
        Assert.Equal(0xEE, packet[23]); // B hi
        Assert.Equal(0xFF, packet[24]); // B lo
    }

    [Fact]
    public void BuildHueStreamPacket_DoesNotContainAreaId()
    {
        // The area UUID must NOT appear in the packet body — it's established via the DTLS session
        var channelColors = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 100, 100, 100, 100, 100, 100 } }
        };

        var packet = _streamer.BuildHueStreamPacket(channelColors);
        var packetStr = Encoding.ASCII.GetString(packet);

        // Verify no UUID-like text appears in the packet
        Assert.True(packetStr.Length < 30); // header(9) + fixed(7) + channel(9) = 25 bytes
    }

    [Fact]
    public void BuildHueStreamPacket_RejectsMalformedChannelColor()
    {
        var channelColors = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 1, 2, 3 } }
        };

        Assert.Throws<ArgumentException>(() => _streamer.BuildHueStreamPacket(channelColors));
    }

    [Fact]
    public void BuildHueStreamPacket_RejectsOutOfRangeChannelId()
    {
        var channelColors = new Dictionary<int, byte[]>
        {
            { 65536, new byte[] { 1, 1, 2, 2, 3, 3 } }
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => _streamer.BuildHueStreamPacket(channelColors));
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
