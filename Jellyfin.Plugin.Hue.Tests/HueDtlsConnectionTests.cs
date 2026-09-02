using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Hue.Configuration;
using Jellyfin.Plugin.Hue.Hue;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

public sealed class HueDtlsConnectionTests
{
    private const string TestAppKey = "hue-loopback-app-key";
    private static readonly byte[] TestPsk =
    [
        0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77,
        0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff
    ];
    private static string TestClientKey => Convert.ToHexString(TestPsk);
    private const int TestDatagramLimit = 16 * 1024;

    [Fact]
    public async Task ConnectAsync_CompletesHuePskHandshake_SendsPayload_AndCloses()
    {
        var psk = (byte[])TestPsk.Clone();
        await using var server = TestDtlsServer.Start(TestAppKey, psk);

        var connection = await HueDtlsConnection.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, server.Port),
            TestAppKey,
            TestClientKey,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            Assert.True(connection.IsHealthy);
            Assert.Equal(ProtocolVersion.DTLSv12, server.NegotiatedVersion);
            Assert.Equal(CipherSuite.TLS_PSK_WITH_AES_128_GCM_SHA256, server.SelectedCipherSuite);
            Assert.True(server.IdentityMatched);

            var payload = "Hue DTLS loopback payload"u8.ToArray();
            connection.Send(payload, 0, payload.Length);

            var received = await server.ReceivePayloadAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(payload, received);
        }
        finally
        {
            connection.Close();
        }

        Assert.False(connection.IsHealthy);
    }

    [Fact]
    public async Task ConnectAsync_CancellationDuringUnresponsiveHandshake_IsBounded()
    {
        using var server = SilentUdpServer.Start();
        using var cancellationSource = new CancellationTokenSource();
        var connectionTask = HueDtlsConnection.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, server.Port),
            TestAppKey,
            TestClientKey,
            cancellationSource.Token);

        try
        {
            await server.FirstDatagram.WaitAsync(TimeSpan.FromSeconds(2));

            var stopwatch = Stopwatch.StartNew();
            cancellationSource.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connectionTask);

            stopwatch.Stop();
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                $"Cancellation took {stopwatch.Elapsed.TotalMilliseconds:0} ms.");
            Assert.True(connectionTask.IsCompleted);
        }
        finally
        {
            cancellationSource.Cancel();
            try
            {
                await connectionTask;
            }
            catch
            {
                // Preserve the assertion failure while ensuring the handshake worker is observed.
            }
        }
    }

    [Fact]
    public void ParseClientKey_RejectsOversizedInputBeforeHexDecoding()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            HueDtlsConnection.ParseClientKey(
                new string('a', PluginConfiguration.MaxHueCredentialLength + 1)));

        Assert.Contains(
            $"{PluginConfiguration.MaxHueCredentialLength} characters or fewer",
            exception.Message);
    }

    [Fact]
    public void ParseClientKey_RejectsControlCharacters()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            HueDtlsConnection.ParseClientKey("00112233\n44556677"));

        Assert.Contains("control characters", exception.Message);
    }

    private sealed class TestDtlsServer : IAsyncDisposable
    {
        private readonly Socket _socket;
        private readonly TestDatagramTransport _datagramTransport;
        private readonly HuePskTestServer _tlsServer;
        private readonly Task<DtlsTransport> _handshakeTask;
        private DtlsTransport? _dtlsTransport;

        private TestDtlsServer(string identity, byte[] psk)
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _datagramTransport = new TestDatagramTransport(_socket);
            _tlsServer = new HuePskTestServer(
                new BcTlsCrypto(new SecureRandom()),
                new FixedPskIdentityManager(identity, psk));
            _handshakeTask = Task.Run(HandshakeAsync);
        }

        public int Port => ((IPEndPoint)_socket.LocalEndPoint!).Port;

        public ProtocolVersion? NegotiatedVersion => _tlsServer.NegotiatedVersion;

        public int SelectedCipherSuite => _tlsServer.SelectedCipherSuite;

        public bool IdentityMatched => _tlsServer.IdentityMatched;

        public static TestDtlsServer Start(string identity, byte[] psk) => new(identity, psk);

        public async Task<byte[]> ReceivePayloadAsync(TimeSpan timeout)
        {
            var dtlsTransport = await _handshakeTask.WaitAsync(timeout).ConfigureAwait(false);
            _dtlsTransport = dtlsTransport;

            var buffer = new byte[TestDatagramLimit];
            var received = await Task.Run(
                () => dtlsTransport.Receive(buffer, 0, buffer.Length, checked((int)timeout.TotalMilliseconds)))
                .WaitAsync(timeout)
                .ConfigureAwait(false);
            return buffer[..received];
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _dtlsTransport?.Close();
            }
            catch (IOException)
            {
            }

            _datagramTransport.Close();
            try
            {
                await _handshakeTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch
            {
                // The server may observe the client close or a test teardown socket close.
            }
        }

        private DtlsTransport HandshakeAsync()
        {
            var protocol = new DtlsServerProtocol();
            var dtlsTransport = protocol.Accept(_tlsServer, _datagramTransport);
            _dtlsTransport = dtlsTransport;
            return dtlsTransport;
        }
    }

    private sealed class HuePskTestServer : PskTlsServer
    {
        public HuePskTestServer(
            Org.BouncyCastle.Tls.Crypto.TlsCrypto crypto,
            TlsPskIdentityManager identityManager)
            : base(crypto, identityManager)
        {
        }

        public ProtocolVersion? NegotiatedVersion { get; private set; }

        public int SelectedCipherSuite { get; private set; }

        public bool IdentityMatched => ((FixedPskIdentityManager)PskIdentityManager).IdentityMatched;

        private TlsPskIdentityManager PskIdentityManager => GetPskIdentityManager();

        protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.DTLSv12.Only();

        protected override int[] GetSupportedCipherSuites() =>
            TlsUtilities.GetSupportedCipherSuites(
                Crypto,
                new[] { CipherSuite.TLS_PSK_WITH_AES_128_GCM_SHA256 });

        public override ProtocolVersion GetServerVersion()
        {
            var version = base.GetServerVersion();
            NegotiatedVersion = version;
            return version;
        }

        public override int GetSelectedCipherSuite()
        {
            var cipherSuite = base.GetSelectedCipherSuite();
            SelectedCipherSuite = cipherSuite;
            return cipherSuite;
        }

        public override int GetHandshakeTimeoutMillis() => 5000;
    }

    private sealed class FixedPskIdentityManager : TlsPskIdentityManager
    {
        private readonly byte[] _identity;
        private readonly byte[] _psk;

        public FixedPskIdentityManager(string identity, byte[] psk)
        {
            _identity = System.Text.Encoding.UTF8.GetBytes(identity);
            _psk = (byte[])psk.Clone();
        }

        public bool IdentityMatched { get; private set; }

        public byte[] GetHint() => null!;

        public byte[] GetPsk(byte[] identity)
        {
            IdentityMatched = identity.AsSpan().SequenceEqual(_identity);
            if (!IdentityMatched)
            {
                throw new InvalidOperationException("Unexpected DTLS PSK identity.");
            }

            return (byte[])_psk.Clone();
        }
    }

    private sealed class TestDatagramTransport : DatagramTransport, IDisposable
    {
        private readonly Socket _socket;
        private EndPoint? _remoteEndpoint;
        private int _closed;

        public TestDatagramTransport(Socket socket)
        {
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        }

        public int GetReceiveLimit() => TestDatagramLimit;

        public int GetSendLimit() => TestDatagramLimit;

        public int Receive(byte[] buffer, int offset, int length, int waitMillis)
        {
            ValidateBuffer(buffer, offset, length);
            if (!WaitForData(waitMillis))
            {
                return -1;
            }

            try
            {
                EndPoint remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);
                var received = _socket.ReceiveFrom(buffer, offset, length, SocketFlags.None, ref remoteEndpoint);
                _remoteEndpoint = remoteEndpoint;
                return received;
            }
            catch (ObjectDisposedException ex)
            {
                throw new IOException("The test DTLS UDP transport is closed.", ex);
            }
            catch (SocketException ex)
            {
                throw new IOException("The test DTLS UDP transport could not receive a datagram.", ex);
            }
        }

        public int Receive(Span<byte> buffer, int waitMillis)
        {
            if (!WaitForData(waitMillis))
            {
                return -1;
            }

            try
            {
                EndPoint remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);
                var received = _socket.ReceiveFrom(buffer, SocketFlags.None, ref remoteEndpoint);
                _remoteEndpoint = remoteEndpoint;
                return received;
            }
            catch (ObjectDisposedException ex)
            {
                throw new IOException("The test DTLS UDP transport is closed.", ex);
            }
            catch (SocketException ex)
            {
                throw new IOException("The test DTLS UDP transport could not receive a datagram.", ex);
            }
        }

        public void Send(byte[] buffer, int offset, int length)
        {
            ValidateBuffer(buffer, offset, length);
            SendCore(() => _socket.SendTo(buffer, offset, length, SocketFlags.None, _remoteEndpoint!));
        }

        public void Send(ReadOnlySpan<byte> buffer)
        {
            if (_remoteEndpoint == null)
            {
                throw new IOException("The test DTLS UDP transport has no peer endpoint.");
            }

            try
            {
                _socket.SendTo(buffer, SocketFlags.None, _remoteEndpoint);
            }
            catch (ObjectDisposedException ex)
            {
                throw new IOException("The test DTLS UDP transport is closed.", ex);
            }
            catch (SocketException ex)
            {
                throw new IOException("The test DTLS UDP transport could not send a datagram.", ex);
            }
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            try
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                _socket.Dispose();
            }
        }

        public void Dispose() => Close();

        private bool WaitForData(int waitMillis)
        {
            if (Volatile.Read(ref _closed) != 0)
            {
                throw new IOException("The test DTLS UDP transport is closed.");
            }

            ArgumentOutOfRangeException.ThrowIfNegative(waitMillis);
            if (waitMillis == 0)
            {
                return true;
            }

            try
            {
                return _socket.Poll(checked(waitMillis * 1000), SelectMode.SelectRead);
            }
            catch (ObjectDisposedException ex)
            {
                throw new IOException("The test DTLS UDP transport is closed.", ex);
            }
            catch (SocketException ex)
            {
                throw new IOException("The test DTLS UDP transport could not poll for a datagram.", ex);
            }
        }

        private void SendCore(Func<int> send)
        {
            if (_remoteEndpoint == null)
            {
                throw new IOException("The test DTLS UDP transport has no peer endpoint.");
            }

            try
            {
                send();
            }
            catch (ObjectDisposedException ex)
            {
                throw new IOException("The test DTLS UDP transport is closed.", ex);
            }
            catch (SocketException ex)
            {
                throw new IOException("The test DTLS UDP transport could not send a datagram.", ex);
            }
        }

        private static void ValidateBuffer(byte[] buffer, int offset, int length)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            if (offset > buffer.Length - length)
            {
                throw new ArgumentException("The buffer segment is outside the supplied array.");
            }
        }
    }

    private sealed class SilentUdpServer : IDisposable
    {
        private readonly Socket _socket;
        private readonly Task _receiveTask;
        private int _closed;

        private SilentUdpServer()
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            FirstDatagramSource = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            _receiveTask = Task.Run(ReceiveFirstDatagramAsync);
        }

        public int Port => ((IPEndPoint)_socket.LocalEndPoint!).Port;

        private TaskCompletionSource<int> FirstDatagramSource { get; }

        public Task<int> FirstDatagram => FirstDatagramSource.Task;

        public static SilentUdpServer Start() => new();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            _socket.Dispose();
            try
            {
                _receiveTask.Wait(TimeSpan.FromSeconds(1));
            }
            catch
            {
            }
        }

        private void ReceiveFirstDatagramAsync()
        {
            try
            {
                var buffer = new byte[TestDatagramLimit];
                EndPoint remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);
                var received = _socket.ReceiveFrom(buffer, SocketFlags.None, ref remoteEndpoint);
                FirstDatagramSource.TrySetResult(received);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
            {
                FirstDatagramSource.TrySetException(ex);
            }
        }
    }
}
