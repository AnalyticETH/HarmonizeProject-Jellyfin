using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace Jellyfin.Plugin.Hue.Hue;

/// <summary>
/// Fixed DTLS application limits for the Hue transport contract.
/// </summary>
/// <remarks>
/// HuePskTlsClient is intentionally locked to DTLS 1.2 with AES-128-GCM. Bouncy
/// Castle subtracts the DTLS record header, explicit AEAD nonce, and authentication
/// tag from the datagram transport limit when calculating the plaintext send limit.
/// Keep these values in sync if the configured protocol or cipher suite changes.
/// </remarks>
internal static class HueDtlsLimits
{
    internal const int DatagramBytes = 16 * 1024;
    internal const int DtlsRecordHeaderBytes = 13;
    internal const int AeadExplicitNonceBytes = 8;
    internal const int AeadAuthenticationTagBytes = 16;
    internal const int ApplicationPayloadBytes =
        DatagramBytes -
        DtlsRecordHeaderBytes -
        AeadExplicitNonceBytes -
        AeadAuthenticationTagBytes;
}

/// <summary>
/// Small send/health/close abstraction around the managed Hue DTLS session.
/// Keeping the streaming surface independent from Bouncy Castle makes lifecycle
/// tests deterministic without launching a credential-bearing child process.
/// </summary>
internal interface IHueDtlsConnection
{
    bool IsHealthy { get; }

    void Send(byte[] buffer, int offset, int count);

    void Close();
}

/// <summary>
/// A managed DTLS 1.2 PSK connection to a Hue entertainment bridge.
/// </summary>
internal sealed class HueDtlsConnection : IHueDtlsConnection
{
    private const int BridgePort = 2100;
    private const int HandshakeTimeoutMilliseconds = 5000;

    private readonly DtlsTransport _dtlsTransport;
    private readonly HueDatagramTransport _datagramTransport;
    private int _closed;

    private HueDtlsConnection(DtlsTransport dtlsTransport, HueDatagramTransport datagramTransport)
    {
        _dtlsTransport = dtlsTransport;
        _datagramTransport = datagramTransport;
    }

    public bool IsHealthy =>
        Volatile.Read(ref _closed) == 0 && _datagramTransport.IsOpen;

    public static async Task<HueDtlsConnection> ConnectAsync(
        string bridgeIp,
        string appKey,
        string clientKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgeIp);
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientKey);
        cancellationToken.ThrowIfCancellationRequested();

        var address = await ResolveBridgeAddressAsync(bridgeIp, cancellationToken).ConfigureAwait(false);
        return await ConnectAsync(
            new IPEndPoint(address, BridgePort),
            appKey,
            clientKey,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<HueDtlsConnection> ConnectAsync(
        IPEndPoint endpoint,
        string appKey,
        string clientKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientKey);
        cancellationToken.ThrowIfCancellationRequested();

        var address = endpoint.Address;
        var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

        HueDatagramTransport? datagramTransport = null;
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            datagramTransport = new HueDatagramTransport(socket);

            using var cancellationRegistration = cancellationToken.Register(
                static state => ((HueDatagramTransport)state!).Close(),
                datagramTransport);

            var psk = ParseClientKey(clientKey);
            var tlsClient = new HuePskTlsClient(
                new BcTlsCrypto(new SecureRandom()),
                appKey,
                psk,
                HandshakeTimeoutMilliseconds);
            var protocol = new DtlsClientProtocol();
            var handshakeTask = Task.Run(
                () => protocol.Connect(tlsClient, datagramTransport),
                CancellationToken.None);

            DtlsTransport dtlsTransport;
            try
            {
                dtlsTransport = await handshakeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // DtlsClientProtocol.Connect is synchronous. Closing the UDP adapter
                // interrupts its bounded receive poll, then we observe the worker before
                // disposing the socket so a canceled startup cannot leak a task.
                datagramTransport.Close();
                try
                {
                    await handshakeTask.WaitAsync(TimeSpan.FromSeconds(HandshakeTimeoutMilliseconds / 1000 + 1))
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original cancellation or handshake failure.
                }

                throw;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                dtlsTransport.Close();
                datagramTransport.Close();
                throw new OperationCanceledException(cancellationToken);
            }

            return new HueDtlsConnection(dtlsTransport, datagramTransport);
        }
        catch
        {
            datagramTransport?.Close();
            socket.Dispose();
            throw;
        }
    }

    public void Send(byte[] buffer, int offset, int count)
    {
        if (!IsHealthy)
        {
            throw new IOException("The Hue DTLS transport is closed.");
        }

        _dtlsTransport.Send(buffer, offset, count);
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        try
        {
            _dtlsTransport.Close();
        }
        catch (IOException)
        {
            // The bridge may already have gone away; closing the socket is still required.
        }
        finally
        {
            _datagramTransport.Close();
        }
    }

    private static async Task<IPAddress> ResolveBridgeAddressAsync(
        string bridgeIp,
        CancellationToken cancellationToken)
    {
        // Resolve and validate once, then connect the UDP socket to this exact address.
        // Re-resolving a .local name here would allow a DNS/mDNS answer to change after
        // the private-address check and could send the DTLS PSK to an unintended peer.
        return await global::Jellyfin.Plugin.Hue.HueBridgeCertificateValidation
            .ResolveLocalBridgeAddressAsync(bridgeIp, cancellationToken)
            .ConfigureAwait(false);
    }

    private static byte[] ParseClientKey(string clientKey)
    {
        var normalized = clientKey.Replace("-", string.Empty, StringComparison.Ordinal).Trim();
        if (normalized.Length == 0 || normalized.Length % 2 != 0)
        {
            throw new ArgumentException("The Hue Client Key must contain an even number of hexadecimal characters.", nameof(clientKey));
        }

        try
        {
            return Convert.FromHexString(normalized);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The Hue Client Key must contain only hexadecimal characters.", nameof(clientKey), ex);
        }
    }
}

/// <summary>
/// Connected UDP adapter required by Bouncy Castle's synchronous DTLS protocol.
/// Closing the socket interrupts Receive so startup cancellation remains bounded.
/// </summary>
internal sealed class HueDatagramTransport : DatagramTransport, IDisposable
{
    private readonly Socket _socket;
    private int _closed;

    public HueDatagramTransport(Socket socket)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
    }

    public bool IsOpen => Volatile.Read(ref _closed) == 0;

    public int GetReceiveLimit() => HueDtlsLimits.DatagramBytes;

    public int GetSendLimit() => HueDtlsLimits.DatagramBytes;

    public int Receive(byte[] buf, int off, int len, int waitMillis)
    {
        ArgumentNullException.ThrowIfNull(buf);
        ArgumentOutOfRangeException.ThrowIfNegative(off);
        ArgumentOutOfRangeException.ThrowIfNegative(len);
        if (off > buf.Length - len)
        {
            throw new ArgumentException("The receive buffer segment is outside the supplied array.", nameof(buf));
        }

        try
        {
            if (!WaitForData(waitMillis))
            {
                return -1;
            }

            return _socket.Receive(buf, off, len, SocketFlags.None);
        }
        catch (ObjectDisposedException ex)
        {
            throw new IOException("The Hue UDP transport is closed.", ex);
        }
        catch (SocketException ex)
        {
            throw new IOException("The Hue UDP transport could not receive a datagram.", ex);
        }
    }

    public int Receive(Span<byte> buffer, int waitMillis)
    {
        try
        {
            if (!WaitForData(waitMillis))
            {
                return -1;
            }

            return _socket.Receive(buffer, SocketFlags.None);
        }
        catch (ObjectDisposedException ex)
        {
            throw new IOException("The Hue UDP transport is closed.", ex);
        }
        catch (SocketException ex)
        {
            throw new IOException("The Hue UDP transport could not receive a datagram.", ex);
        }
    }

    public void Send(byte[] buf, int off, int len)
    {
        ArgumentNullException.ThrowIfNull(buf);
        ArgumentOutOfRangeException.ThrowIfNegative(off);
        ArgumentOutOfRangeException.ThrowIfNegative(len);
        if (off > buf.Length - len)
        {
            throw new ArgumentException("The send buffer segment is outside the supplied array.", nameof(buf));
        }

        try
        {
            _socket.Send(buf, off, len, SocketFlags.None);
        }
        catch (ObjectDisposedException ex)
        {
            throw new IOException("The Hue UDP transport is closed.", ex);
        }
        catch (SocketException ex)
        {
            throw new IOException("The Hue UDP transport could not send a datagram.", ex);
        }
    }

    public void Send(ReadOnlySpan<byte> buffer)
    {
        try
        {
            _socket.Send(buffer, SocketFlags.None);
        }
        catch (ObjectDisposedException ex)
        {
            throw new IOException("The Hue UDP transport is closed.", ex);
        }
        catch (SocketException ex)
        {
            throw new IOException("The Hue UDP transport could not send a datagram.", ex);
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
        if (!IsOpen)
        {
            throw new IOException("The Hue UDP transport is closed.");
        }

        if (waitMillis < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(waitMillis));
        }

        if (waitMillis == 0)
        {
            return true;
        }

        try
        {
            return _socket.Poll(waitMillis * 1000, SelectMode.SelectRead);
        }
        catch (ObjectDisposedException ex)
        {
            throw new IOException("The Hue UDP transport is closed.", ex);
        }
        catch (SocketException ex)
        {
            throw new IOException("The Hue UDP transport could not poll for a datagram.", ex);
        }
    }
}

/// <summary>
/// Hue requires the plain PSK AES-GCM DTLS 1.2 suite rather than the DHE/ECDHE
/// PSK suites offered by Bouncy Castle's default <see cref="PskTlsClient"/>.
/// </summary>
internal sealed class HuePskTlsClient : PskTlsClient
{
    internal const int HueCipherSuite = CipherSuite.TLS_PSK_WITH_AES_128_GCM_SHA256;

    private readonly int _handshakeTimeoutMilliseconds;

    public HuePskTlsClient(
        Org.BouncyCastle.Tls.Crypto.TlsCrypto crypto,
        string identity,
        byte[] psk,
        int handshakeTimeoutMilliseconds)
        : base(crypto, new BasicTlsPskIdentity(identity, psk))
    {
        _handshakeTimeoutMilliseconds = handshakeTimeoutMilliseconds;
    }

    internal static ProtocolVersion[] HueProtocolVersions() => ProtocolVersion.DTLSv12.Only();

    protected override ProtocolVersion[] GetSupportedVersions() => HueProtocolVersions();

    protected override int[] GetSupportedCipherSuites() =>
        TlsUtilities.GetSupportedCipherSuites(
            Crypto,
            new[] { HueCipherSuite });

    public override int GetHandshakeTimeoutMillis() => _handshakeTimeoutMilliseconds;
}
