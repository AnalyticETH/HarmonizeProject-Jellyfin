using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Hue.Service;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

public sealed class HueBridgeMdnsDiscoveryTests
{
    [Fact]
    public void BuildQuery_RequestsHueService()
    {
        var query = HueBridgeMdnsDiscovery.BuildQuery();

        Assert.Equal(0, query[0]);
        Assert.Equal(1, query[5]);
        Assert.Equal(4, query[12]);
        Assert.Equal((byte)'_', query[13]);
        Assert.Equal((byte)'h', query[14]);
        Assert.Equal((byte)'u', query[15]);
        Assert.Equal((byte)'e', query[16]);
        Assert.Equal(12, query[^4] << 8 | query[^3]);
        Assert.Equal(1, query[^2] << 8 | query[^1]);
    }

    [Fact]
    public void BuildQuery_WhenUsingFallbackSocketRequestsUnicastResponse()
    {
        var query = HueBridgeMdnsDiscovery.BuildQuery(requestUnicastResponse: true);

        Assert.Equal(0x80, query[^2]);
        Assert.Equal(0x01, query[^1]);
    }

    [Fact]
    public void ParseResponse_ResolvesPrivateAddressFromPtrSrvAndARecords()
    {
        var response = BuildResponse("192.168.1.50");

        var addresses = HueBridgeMdnsDiscovery.ParseResponse(response);

        Assert.Contains("192.168.1.50", addresses);
    }

    [Fact]
    public void ParseResponse_ResolvesUniqueLocalAddressFromPtrSrvAndAaaaRecords()
    {
        var response = BuildResponse("fd12:3456:789a::50");

        var addresses = HueBridgeMdnsDiscovery.ParseResponse(response);

        Assert.Contains("fd12:3456:789a::50", addresses);
    }

    [Fact]
    public void ParseResponse_ResolvesLinkLocalAddressFromPtrSrvAndAaaaRecords()
    {
        var response = BuildResponse("fe80::50");

        var addresses = HueBridgeMdnsDiscovery.ParseResponse(response);

        Assert.Contains("fe80::50", addresses);
    }

    [Fact]
    public void ParseResponse_WhenReplyScopeIsKnown_PreservesLinkLocalInterfaceScope()
    {
        var response = BuildResponse("fe80::50");

        var addresses = HueBridgeMdnsDiscovery.ParseResponse(response, linkLocalScopeId: 42);

        Assert.Contains("fe80::50%42", addresses);
    }

    [Fact]
    public void ParseResponse_WhenReplyScopeIsKnown_DoesNotScopeUniqueLocalAddress()
    {
        var response = BuildResponse("fd12:3456:789a::50");

        var addresses = HueBridgeMdnsDiscovery.ParseResponse(response, linkLocalScopeId: 42);

        Assert.Contains("fd12:3456:789a::50", addresses);
        Assert.DoesNotContain(addresses, address => address.Contains('%', StringComparison.Ordinal));
    }

    [Fact]
    public void ParseResponse_RejectsGlobalIpv6Addresses()
    {
        var response = BuildResponse("2001:db8::50");

        var addresses = HueBridgeMdnsDiscovery.ParseResponse(response);

        Assert.Empty(addresses);
    }

    [Fact]
    public void CreateIpv6MulticastEndpoint_UsesPerInterfaceScope()
    {
        var endpoint = HueBridgeMdnsDiscovery.CreateIpv6MulticastEndpoint(42);

        Assert.Equal(System.Net.Sockets.AddressFamily.InterNetworkV6, endpoint.AddressFamily);
        Assert.Equal("ff02::fb", endpoint.Address.ToString().Split('%')[0]);
        Assert.Equal(42, endpoint.Address.ScopeId);
        Assert.Equal(5353, endpoint.Port);
    }

    [Fact]
    public void ParseResponse_RejectsPublicAddresses()
    {
        var response = BuildResponse("8.8.8.8");

        var addresses = HueBridgeMdnsDiscovery.ParseResponse(response);

        Assert.Empty(addresses);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-dns-packet")]
    public void ParseResponse_MalformedPacketsReturnNoAddresses(string? value)
    {
        var response = value == null ? null : System.Text.Encoding.ASCII.GetBytes(value);

        var addresses = HueBridgeMdnsDiscovery.ParseResponse(response);

        Assert.Empty(addresses);
    }

    [Fact]
    public void ParseResponse_RejectsSrvNameThatRunsPastRdata()
    {
        var response = BuildResponseWithTruncatedSrvTarget("192.168.1.50");

        var addresses = HueBridgeMdnsDiscovery.ParseResponse(response);

        Assert.Empty(addresses);
    }

    [Fact]
    public void ParseResponse_RejectsPtrNameThatRunsPastRdata()
    {
        var response = BuildResponseWithTruncatedPtrTarget("192.168.1.50");

        var addresses = HueBridgeMdnsDiscovery.ParseResponse(response);

        Assert.Empty(addresses);
    }

    [Fact]
    public void ParseResponse_RejectsForwardCompressionPointer()
    {
        var response = BuildResponseWithForwardSrvPointer("192.168.1.50");

        var addresses = HueBridgeMdnsDiscovery.ParseResponse(response);

        Assert.Empty(addresses);
    }

    [Fact]
    public async Task DiscoverAsync_WhenCanceledBeforeSocketUseHonorsCancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new HueBridgeMdnsDiscovery().DiscoverAsync(cancellationSource.Token));
    }

    private static byte[] BuildResponse(string address)
    {
        var response = new List<byte>();
        AppendUInt16(response, 0);
        AppendUInt16(response, 0x8400); // response + authoritative
        AppendUInt16(response, 1); // question count
        AppendUInt16(response, 2); // answer count
        AppendUInt16(response, 0); // authority count
        AppendUInt16(response, 1); // additional count

        AppendName(response, HueBridgeMdnsDiscovery.ServiceType);
        AppendUInt16(response, 12); // PTR
        AppendUInt16(response, 1); // IN

        AppendUInt16(response, 0xc00c); // pointer to _hue._tcp.local
        AppendUInt16(response, 12); // PTR
        AppendUInt16(response, 1); // IN
        AppendUInt32(response, 120);
        var instance = EncodeName("Hue Bridge._hue._tcp.local");
        AppendUInt16(response, (ushort)instance.Length);
        response.AddRange(instance);

        AppendName(response, "Hue Bridge._hue._tcp.local");
        AppendUInt16(response, 33); // SRV
        AppendUInt16(response, 1); // IN
        AppendUInt32(response, 120);
        var host = EncodeName("hue-bridge.local");
        AppendUInt16(response, (ushort)(6 + host.Length));
        AppendUInt16(response, 0); // priority
        AppendUInt16(response, 0); // weight
        AppendUInt16(response, 443);
        response.AddRange(host);

        AppendName(response, "hue-bridge.local");
        var parsedAddress = IPAddress.Parse(address);
        AppendUInt16(
            response,
            parsedAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 28 : 1);
        AppendUInt16(response, 1); // IN
        AppendUInt32(response, 120);
        var addressBytes = parsedAddress.GetAddressBytes();
        AppendUInt16(response, addressBytes.Length);
        response.AddRange(addressBytes);

        return response.ToArray();
    }

    private static byte[] BuildResponseWithTruncatedSrvTarget(string address)
    {
        var response = new List<byte>();
        AppendUInt16(response, 0);
        AppendUInt16(response, 0x8400); // response + authoritative
        AppendUInt16(response, 1); // question count
        AppendUInt16(response, 2); // answer count
        AppendUInt16(response, 0); // authority count
        AppendUInt16(response, 1); // additional count

        AppendName(response, HueBridgeMdnsDiscovery.ServiceType);
        AppendUInt16(response, 12); // PTR
        AppendUInt16(response, 1); // IN

        AppendUInt16(response, 0xc00c); // pointer to _hue._tcp.local
        AppendUInt16(response, 12); // PTR
        AppendUInt16(response, 1); // IN
        AppendUInt32(response, 120);
        var instance = EncodeName("Hue Bridge._hue._tcp.local");
        AppendUInt16(response, (ushort)instance.Length);
        response.AddRange(instance);

        AppendName(response, "Hue Bridge._hue._tcp.local");
        AppendUInt16(response, 33); // SRV
        AppendUInt16(response, 1); // IN
        AppendUInt32(response, 120);
        AppendUInt16(response, 6); // priority, weight, and port only; target is missing
        AppendUInt16(response, 0); // priority
        AppendUInt16(response, 0); // weight
        AppendUInt16(response, 443); // port

        AppendName(response, "hue-bridge.local");
        var parsedAddress = IPAddress.Parse(address);
        AppendUInt16(
            response,
            parsedAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 28 : 1);
        AppendUInt16(response, 1); // IN
        AppendUInt32(response, 120);
        var addressBytes = parsedAddress.GetAddressBytes();
        AppendUInt16(response, addressBytes.Length);
        response.AddRange(addressBytes);

        return response.ToArray();
    }

    private static byte[] BuildResponseWithTruncatedPtrTarget(string address)
    {
        var response = new List<byte>();
        AppendUInt16(response, 0);
        AppendUInt16(response, 0x8400); // response + authoritative
        AppendUInt16(response, 1); // question count
        AppendUInt16(response, 2); // answer count
        AppendUInt16(response, 0); // authority count
        AppendUInt16(response, 1); // additional count

        AppendName(response, HueBridgeMdnsDiscovery.ServiceType);
        AppendUInt16(response, 12); // PTR
        AppendUInt16(response, 1); // IN

        AppendUInt16(response, 0xc00c); // pointer to _hue._tcp.local
        AppendUInt16(response, 12); // PTR
        AppendUInt16(response, 1); // IN
        AppendUInt32(response, 120);
        AppendUInt16(response, 1); // truncated compression pointer
        response.Add(0xc0);

        // The first label is exactly 12 bytes so the following record's owner
        // supplies the missing low pointer byte in the unbounded parser.
        const string instanceName = "Hue Bridge 1._hue._tcp.local";
        AppendName(response, instanceName);
        AppendUInt16(response, 33); // SRV
        AppendUInt16(response, 1); // IN
        AppendUInt32(response, 120);
        var host = EncodeName("hue-bridge.local");
        AppendUInt16(response, (ushort)(6 + host.Length));
        AppendUInt16(response, 0); // priority
        AppendUInt16(response, 0); // weight
        AppendUInt16(response, 443);
        response.AddRange(host);

        AppendName(response, "hue-bridge.local");
        var parsedAddress = IPAddress.Parse(address);
        AppendUInt16(
            response,
            parsedAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 28 : 1);
        AppendUInt16(response, 1); // IN
        AppendUInt32(response, 120);
        var addressBytes = parsedAddress.GetAddressBytes();
        AppendUInt16(response, addressBytes.Length);
        response.AddRange(addressBytes);

        return response.ToArray();
    }

    private static byte[] BuildResponseWithForwardSrvPointer(string address)
    {
        var response = new List<byte>();
        AppendUInt16(response, 0);
        AppendUInt16(response, 0x8400); // response + authoritative
        AppendUInt16(response, 1); // question count
        AppendUInt16(response, 2); // answer count
        AppendUInt16(response, 0); // authority count
        AppendUInt16(response, 1); // additional count

        AppendName(response, HueBridgeMdnsDiscovery.ServiceType);
        AppendUInt16(response, 12); // PTR
        AppendUInt16(response, 1); // IN

        AppendUInt16(response, 0xc00c); // pointer to _hue._tcp.local
        AppendUInt16(response, 12); // PTR
        AppendUInt16(response, 1); // IN
        AppendUInt32(response, 120);
        var instance = EncodeName("Hue Bridge._hue._tcp.local");
        AppendUInt16(response, (ushort)instance.Length);
        response.AddRange(instance);

        AppendName(response, "Hue Bridge._hue._tcp.local");
        AppendUInt16(response, 33); // SRV
        AppendUInt16(response, 1); // IN
        AppendUInt32(response, 120);
        AppendUInt16(response, 8); // six SRV fields plus a compressed target
        AppendUInt16(response, 0); // priority
        AppendUInt16(response, 0); // weight
        AppendUInt16(response, 443); // port
        var pointerOffset = response.Count;
        response.Add(0xc0);
        response.Add(0); // patched to the later host owner below

        var hostOffset = response.Count;
        response[pointerOffset] = (byte)(0xc0 | (hostOffset >> 8));
        response[pointerOffset + 1] = (byte)hostOffset;
        AppendName(response, "hue-bridge.local");
        var parsedAddress = IPAddress.Parse(address);
        AppendUInt16(
            response,
            parsedAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 28 : 1);
        AppendUInt16(response, 1); // IN
        AppendUInt32(response, 120);
        var addressBytes = parsedAddress.GetAddressBytes();
        AppendUInt16(response, addressBytes.Length);
        response.AddRange(addressBytes);

        return response.ToArray();
    }

    private static byte[] EncodeName(string name)
    {
        var bytes = new List<byte>();
        AppendName(bytes, name);
        return bytes.ToArray();
    }

    private static void AppendName(List<byte> target, string name)
    {
        foreach (var label in name.Split('.'))
        {
            target.Add((byte)label.Length);
            target.AddRange(System.Text.Encoding.UTF8.GetBytes(label));
        }

        target.Add(0);
    }

    private static void AppendUInt16(List<byte> target, int value)
    {
        target.Add((byte)(value >> 8));
        target.Add((byte)value);
    }

    private static void AppendUInt32(List<byte> target, int value)
    {
        target.Add((byte)(value >> 24));
        target.Add((byte)(value >> 16));
        target.Add((byte)(value >> 8));
        target.Add((byte)value);
    }
}
