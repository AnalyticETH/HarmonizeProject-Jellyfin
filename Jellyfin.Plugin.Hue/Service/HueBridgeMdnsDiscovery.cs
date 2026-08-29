using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Hue.Service;

/// <summary>
/// Discovers Hue bridge addresses on the local link without contacting the Hue cloud.
/// </summary>
public interface IHueBridgeLocalDiscovery
{
    Task<IReadOnlyList<string>> DiscoverAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Performs a bounded DNS-SD query for the Hue bridge mDNS service. The implementation
/// intentionally uses only framework networking APIs so the plugin package does not need
/// to ship another runtime assembly alongside Jellyfin's plugin DLL.
/// </summary>
public sealed class HueBridgeMdnsDiscovery : IHueBridgeLocalDiscovery
{
    internal const string ServiceType = "_hue._tcp.local";

    private const ushort DnsTypeA = 1;
    private const ushort DnsTypePtr = 12;
    private const ushort DnsTypeSrv = 33;
    private const ushort DnsTypeAaaa = 28;
    private const ushort DnsClassIn = 1;
    // mDNS repurposes the top class bit for cache-flush/unicast-response flags.
    // The remaining 15 bits are the DNS class and must be Internet (IN) for the
    // service query handled here.
    private const ushort DnsClassValueMask = 0x7fff;
    private const int DnsHeaderLength = 12;
    private const int MdnsPort = 5353;
    private const int MaxRecords = 256;
    private static readonly IPAddress MdnsAddress = IPAddress.Parse("224.0.0.251");
    private static readonly IPAddress MdnsIpv6Address = IPAddress.Parse("ff02::fb");
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1.5);

    private readonly TimeSpan _timeout;

    public HueBridgeMdnsDiscovery(TimeSpan? timeout = null)
    {
        _timeout = timeout is { } value && value > TimeSpan.Zero
            ? value
            : DefaultTimeout;
    }

    public async Task<IReadOnlyList<string>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        try
        {
            // Keep IPv4 and IPv6 discovery independent. A host can have no IPv6
            // multicast-capable interface (or a firewall can reject the IPv6 socket)
            // without making the existing IPv4 discovery fail.
            var results = await Task.WhenAll(
                    DiscoverIpv4Async(timeoutSource.Token, cancellationToken),
                    DiscoverIpv6Async(timeoutSource.Token, cancellationToken))
                .ConfigureAwait(false);

            return results
                .SelectMany(addresses => addresses)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Array.Empty<string>();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<string>();
        }
    }

    private async Task<IReadOnlyList<string>> DiscoverIpv4Async(
        CancellationToken timeoutCancellationToken,
        CancellationToken callerCancellationToken)
    {
        UdpClient? client = null;
        try
        {
            client = CreateClient(out var requestedUnicastResponse);
            var query = BuildQuery(requestedUnicastResponse);
            var endpoint = new IPEndPoint(MdnsAddress, MdnsPort);
            await client.SendAsync(query, endpoint).AsTask().WaitAsync(timeoutCancellationToken).ConfigureAwait(false);

            return await ReceiveAddressesAsync(client, timeoutCancellationToken, callerCancellationToken)
                .ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return Array.Empty<string>();
        }
        catch (ObjectDisposedException)
        {
            return Array.Empty<string>();
        }
        catch (OperationCanceledException) when (!callerCancellationToken.IsCancellationRequested)
        {
            return Array.Empty<string>();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<string>();
        }
        finally
        {
            client?.Dispose();
        }
    }

    private async Task<IReadOnlyList<string>> DiscoverIpv6Async(
        CancellationToken timeoutCancellationToken,
        CancellationToken callerCancellationToken)
    {
        IReadOnlyList<int> interfaceIndexes;
        try
        {
            interfaceIndexes = GetIpv6MulticastInterfaceIndexes();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<string>();
        }
        catch (SocketException)
        {
            return Array.Empty<string>();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<string>();
        }

        if (interfaceIndexes.Count == 0)
            return Array.Empty<string>();

        UdpClient? client = null;
        try
        {
            client = CreateIpv6Client(interfaceIndexes, out var requestedUnicastResponse);
            var query = BuildQuery(requestedUnicastResponse);
            var sentQuery = false;
            foreach (var interfaceIndex in interfaceIndexes)
            {
                try
                {
                    // Link-local multicast requires a scope ID. Sending once per
                    // interface also avoids relying on the process-wide default route.
                    var endpoint = CreateIpv6MulticastEndpoint(interfaceIndex);
                    await client.SendAsync(query, endpoint).AsTask().WaitAsync(timeoutCancellationToken)
                        .ConfigureAwait(false);
                    sentQuery = true;
                }
                catch (SocketException)
                {
                    // One interface can disappear between enumeration and send. Keep
                    // querying the remaining interfaces rather than dropping IPv6
                    // discovery altogether.
                }
            }

            return sentQuery
                ? await ReceiveAddressesAsync(client, timeoutCancellationToken, callerCancellationToken)
                    .ConfigureAwait(false)
                : Array.Empty<string>();
        }
        catch (SocketException)
        {
            return Array.Empty<string>();
        }
        catch (ObjectDisposedException)
        {
            return Array.Empty<string>();
        }
        catch (OperationCanceledException) when (!callerCancellationToken.IsCancellationRequested)
        {
            return Array.Empty<string>();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<string>();
        }
        finally
        {
            client?.Dispose();
        }
    }

    private static async Task<IReadOnlyList<string>> ReceiveAddressesAsync(
        UdpClient client,
        CancellationToken timeoutCancellationToken,
        CancellationToken callerCancellationToken)
    {
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!timeoutCancellationToken.IsCancellationRequested)
        {
            try
            {
                var response = await client.ReceiveAsync().WaitAsync(timeoutCancellationToken).ConfigureAwait(false);
                var linkLocalScopeId = GetLinkLocalScopeId(response.RemoteEndPoint);
                foreach (var address in ParseResponse(response.Buffer, linkLocalScopeId))
                    addresses.Add(address);
            }
            catch (OperationCanceledException) when (!callerCancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        return addresses.ToArray();
    }

    /// <summary>
    /// Builds a DNS-SD PTR query for the Hue service. The unicast-response bit is used
    /// only when binding the standard mDNS port was unavailable, which lets a running
    /// Avahi/Bonjour daemon coexist with the fallback socket.
    /// </summary>
    internal static byte[] BuildQuery(bool requestUnicastResponse = false)
    {
        var query = new List<byte>(64);
        AppendUInt16(query, 0); // transaction ID
        AppendUInt16(query, 0); // standard query flags
        AppendUInt16(query, 1); // one question
        AppendUInt16(query, 0); // answer count
        AppendUInt16(query, 0); // authority count
        AppendUInt16(query, 0); // additional count
        AppendDnsName(query, ServiceType);
        AppendUInt16(query, DnsTypePtr);
        AppendUInt16(query, (ushort)(DnsClassIn | (requestUnicastResponse ? 0x8000 : 0)));
        return query.ToArray();
    }

    /// <summary>
    /// Extracts private/local addresses from a DNS-SD response. Keeping this parser
    /// internal makes malformed or compressed responses testable without requiring a
    /// multicast-capable CI runner.
    /// </summary>
    internal static IReadOnlyList<string> ParseResponse(byte[]? message, long linkLocalScopeId = 0)
    {
        if (message == null || message.Length < DnsHeaderLength)
            return Array.Empty<string>();

        if (linkLocalScopeId < 0 || linkLocalScopeId > uint.MaxValue)
            return Array.Empty<string>();

        var offset = 0;
        if (!TryReadUInt16(message, ref offset, out _) ||
            !TryReadUInt16(message, ref offset, out var flags) ||
            !TryReadUInt16(message, ref offset, out var questionCount) ||
            !TryReadUInt16(message, ref offset, out var answerCount) ||
            !TryReadUInt16(message, ref offset, out var authorityCount) ||
            !TryReadUInt16(message, ref offset, out var additionalCount))
        {
            return Array.Empty<string>();
        }

        if ((flags & 0x8000) == 0)
            return Array.Empty<string>();

        for (var index = 0; index < questionCount; index++)
        {
            if (!TryReadDnsName(message, ref offset, out _) ||
                !TryReadUInt16(message, ref offset, out _) ||
                !TryReadUInt16(message, ref offset, out var questionClass) ||
                (questionClass & DnsClassValueMask) != DnsClassIn)
                return Array.Empty<string>();
        }

        var recordCount = (long)answerCount + authorityCount + additionalCount;
        if (recordCount > MaxRecords)
            return Array.Empty<string>();

        var records = new List<MdnsRecord>((int)recordCount);
        for (var index = 0; index < recordCount; index++)
        {
            if (!TryReadRecord(message, ref offset, out var record))
                return Array.Empty<string>();

            records.Add(record);
        }

        var serviceInstances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records.Where(record => record.Type == DnsTypePtr &&
                                                         string.Equals(record.Name, ServiceType, StringComparison.OrdinalIgnoreCase)))
        {
            if (!string.IsNullOrWhiteSpace(record.Target))
                serviceInstances.Add(record.Target);
        }

        var serviceHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records.Where(record => record.Type == DnsTypeSrv))
        {
            if ((serviceInstances.Contains(record.Name) || IsHueServiceInstance(record.Name)) &&
                !string.IsNullOrWhiteSpace(record.Target))
            {
                serviceHosts.Add(record.Target);
            }
        }

        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records.Where(record =>
                     (record.Type == DnsTypeA || record.Type == DnsTypeAaaa) &&
                     record.Address != null &&
                     serviceHosts.Contains(record.Name)))
        {
            var scopedAddress = AddLinkLocalScope(record.Address!, linkLocalScopeId);
            var address = scopedAddress.ToString();
            if (Jellyfin.Plugin.Hue.HueBridgeCertificateValidation.IsValidBridgeAddress(address))
                addresses.Add(address);
        }

        return addresses.ToArray();
    }

    private static long GetLinkLocalScopeId(IPEndPoint endpoint)
    {
        var address = endpoint.Address;
        return address.AddressFamily == AddressFamily.InterNetworkV6 &&
               address.IsIPv6LinkLocal &&
               address.ScopeId > 0
            ? address.ScopeId
            : 0;
    }

    private static IPAddress AddLinkLocalScope(IPAddress address, long linkLocalScopeId)
    {
        if (!address.IsIPv6LinkLocal || address.ScopeId != 0 || linkLocalScopeId == 0)
            return address;

        return new IPAddress(address.GetAddressBytes(), linkLocalScopeId);
    }

    private static UdpClient CreateClient(out bool requestedUnicastResponse)
    {
        UdpClient? client = null;
        try
        {
            client = new UdpClient(AddressFamily.InterNetwork)
            {
                ExclusiveAddressUse = false
            };
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
            client.JoinMulticastGroup(MdnsAddress);
            requestedUnicastResponse = false;
            return client;
        }
        catch (SocketException)
        {
            client?.Dispose();
            requestedUnicastResponse = true;
            return new UdpClient(0);
        }
        catch (InvalidOperationException)
        {
            client?.Dispose();
            requestedUnicastResponse = true;
            return new UdpClient(0);
        }
    }

    private static UdpClient CreateIpv6Client(
        IReadOnlyList<int> interfaceIndexes,
        out bool requestedUnicastResponse)
    {
        UdpClient? client = null;
        try
        {
            client = new UdpClient(AddressFamily.InterNetworkV6)
            {
                ExclusiveAddressUse = false
            };
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, MdnsPort));
            var joinedInterface = false;
            foreach (var interfaceIndex in interfaceIndexes)
            {
                try
                {
                    client.JoinMulticastGroup(interfaceIndex, MdnsIpv6Address);
                    joinedInterface = true;
                }
                catch (SocketException)
                {
                    // Continue with interfaces that are still available.
                }
                catch (InvalidOperationException)
                {
                    // Continue with interfaces that are still available.
                }
            }

            if (!joinedInterface)
            {
                client.Dispose();
                client = null;
                requestedUnicastResponse = true;
                return new UdpClient(AddressFamily.InterNetworkV6);
            }

            requestedUnicastResponse = false;
            return client;
        }
        catch (SocketException)
        {
            client?.Dispose();
            requestedUnicastResponse = true;
            return new UdpClient(AddressFamily.InterNetworkV6);
        }
        catch (InvalidOperationException)
        {
            client?.Dispose();
            requestedUnicastResponse = true;
            return new UdpClient(AddressFamily.InterNetworkV6);
        }
    }

    private static IReadOnlyList<int> GetIpv6MulticastInterfaceIndexes()
    {
        var indexes = new HashSet<int>();
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                !networkInterface.SupportsMulticast)
            {
                continue;
            }

            try
            {
                var properties = networkInterface.GetIPProperties();
                if (!properties.UnicastAddresses.Any(address =>
                        address.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                        !IPAddress.IsLoopback(address.Address) &&
                        !address.Address.Equals(IPAddress.IPv6Any)))
                {
                    continue;
                }

                var ipv6Properties = properties.GetIPv6Properties();
                if (ipv6Properties is { Index: > 0 })
                    indexes.Add(ipv6Properties.Index);
            }
            catch (NetworkInformationException)
            {
                // Interface state can change while it is being inspected. A failed
                // interface must not prevent discovery on the rest of the host.
            }
            catch (SocketException)
            {
                // Some platforms surface a disappearing interface as a socket error.
            }
        }

        return indexes.ToArray();
    }

    internal static IPEndPoint CreateIpv6MulticastEndpoint(int interfaceIndex)
    {
        if (interfaceIndex <= 0)
            throw new ArgumentOutOfRangeException(nameof(interfaceIndex));

        return new IPEndPoint(
            new IPAddress(MdnsIpv6Address.GetAddressBytes(), interfaceIndex),
            MdnsPort);
    }

    private static bool IsHueServiceInstance(string name)
        => name.EndsWith("." + ServiceType, StringComparison.OrdinalIgnoreCase);

    private static bool TryReadRecord(byte[] message, ref int offset, out MdnsRecord record)
    {
        record = new MdnsRecord(string.Empty, 0, null, null);
        if (!TryReadDnsName(message, ref offset, out var name) ||
            !TryReadUInt16(message, ref offset, out var type) ||
            !TryReadUInt16(message, ref offset, out var recordClass) ||
            (recordClass & DnsClassValueMask) != DnsClassIn ||
            !TryReadUInt32(message, ref offset, out _) ||
            !TryReadUInt16(message, ref offset, out var dataLength))
        {
            return false;
        }

        var dataOffset = offset;
        if (!TrySkip(message, ref offset, dataLength))
            return false;

        string? target = null;
        IPAddress? address = null;
        var dataEnd = offset;
        var dataCursor = dataOffset;
        if (type == DnsTypePtr)
        {
            // PTR names are encoded inside the record's RDATA. Never let a
            // malformed length make the parser consume the next record while
            // resolving an instance name.
            if (!TryReadDnsName(message, ref dataCursor, out target, dataEnd) ||
                dataCursor != dataEnd)
            {
                return false;
            }
        }
        else if (type == DnsTypeSrv)
        {
            if (dataLength < 6)
                return false;

            dataCursor += 6;
            // SRV priority, weight, and port occupy the first six bytes; the
            // target name must stay wholly inside the remaining RDATA bytes.
            if (!TryReadDnsName(message, ref dataCursor, out target, dataEnd) ||
                dataCursor != dataEnd)
            {
                return false;
            }
        }
        else if (type == DnsTypeA && dataLength == 4)
        {
            address = new IPAddress(message.AsSpan(dataOffset, 4));
        }
        else if (type == DnsTypeAaaa && dataLength == 16)
        {
            address = new IPAddress(message.AsSpan(dataOffset, 16));
        }

        record = new MdnsRecord(NormalizeName(name), type, NormalizeName(target), address);
        return true;
    }

    private static bool TryReadDnsName(
        byte[] message,
        ref int offset,
        out string name,
        int maxOffset = -1)
    {
        if (maxOffset < 0)
            maxOffset = message.Length;

        if (offset < 0 || maxOffset > message.Length || offset > maxOffset)
        {
            name = string.Empty;
            return false;
        }

        var labels = new List<string>();
        var cursor = offset;
        var jumped = false;
        var jumps = 0;
        var encodedNameLength = 0;

        while (true)
        {
            // Before a compression pointer, every byte belongs to this name's
            // encoded RDATA. Once jumped, the pointer target may refer to an
            // earlier name elsewhere in the DNS message.
            if (cursor >= message.Length || (!jumped && cursor >= maxOffset))
            {
                name = string.Empty;
                return false;
            }

            var length = message[cursor++];
            if (length == 0)
            {
                if (!jumped)
                    offset = cursor;

                name = string.Join('.', labels);
                return true;
            }

            if ((length & 0xc0) == 0xc0)
            {
                if (cursor >= message.Length || (!jumped && cursor >= maxOffset) || ++jumps > 20)
                {
                    name = string.Empty;
                    return false;
                }

                var pointerOffset = cursor - 1;
                var pointer = ((length & 0x3f) << 8) | message[cursor++];
                // RFC 1035 compression pointers reference an earlier name. A
                // forward pointer could otherwise make a malformed record read
                // into a later answer and manufacture a service/host association.
                if (pointer >= message.Length || pointer >= pointerOffset)
                {
                    name = string.Empty;
                    return false;
                }

                if (!jumped)
                {
                    offset = cursor;
                    jumped = true;
                }

                cursor = pointer;
                continue;
            }

            if ((length & 0xc0) != 0 || length > 63 ||
                length > message.Length - cursor ||
                (!jumped && length > maxOffset - cursor) ||
                encodedNameLength > 255 - length - 1)
            {
                name = string.Empty;
                return false;
            }

            labels.Add(Encoding.UTF8.GetString(message, cursor, length));
            cursor += length;
            encodedNameLength += length + 1;
        }
    }

    private static string NormalizeName(string? name)
        => (name ?? string.Empty).Trim().TrimEnd('.');

    private static bool TryReadUInt16(byte[] message, ref int offset, out ushort value)
    {
        if (offset + 2 > message.Length)
        {
            value = 0;
            return false;
        }

        value = (ushort)((message[offset] << 8) | message[offset + 1]);
        offset += 2;
        return true;
    }

    private static bool TryReadUInt32(byte[] message, ref int offset, out uint value)
    {
        if (offset + 4 > message.Length)
        {
            value = 0;
            return false;
        }

        value = ((uint)message[offset] << 24) |
                ((uint)message[offset + 1] << 16) |
                ((uint)message[offset + 2] << 8) |
                message[offset + 3];
        offset += 4;
        return true;
    }

    private static bool TrySkip(byte[] message, ref int offset, int length)
    {
        if (length < 0 || offset + length > message.Length)
            return false;

        offset += length;
        return true;
    }

    private static void AppendDnsName(List<byte> target, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length is 0 or > 63)
                throw new ArgumentException("DNS labels must be between 1 and 63 bytes.", nameof(name));

            target.Add((byte)bytes.Length);
            target.AddRange(bytes);
        }

        target.Add(0);
    }

    private static void AppendUInt16(List<byte> target, ushort value)
    {
        target.Add((byte)(value >> 8));
        target.Add((byte)value);
    }

    private sealed record MdnsRecord(string Name, ushort Type, string? Target, IPAddress? Address);
}
