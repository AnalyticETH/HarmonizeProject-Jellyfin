using System.Net;
using System.Net.Http;
using System.Net.Security;
using Jellyfin.Plugin.Hue;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

public class PluginServiceRegistratorTests
{
    [Theory]
    [InlineData("192.168.1.100")]
    [InlineData("10.0.0.15")]
    [InlineData("172.16.20.4")]
    [InlineData("169.254.1.20")]
    [InlineData("127.0.0.1")]
    [InlineData("fc00::1234")]
    [InlineData("fe80::1234")]
    [InlineData("hue-bridge.local")]
    public void IsLocalBridgeHost_PrivateAddressesAreAllowed(string host)
    {
        Assert.True(HueBridgeCertificateValidation.IsLocalBridgeHost(host));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("203.0.113.10")]
    [InlineData("discovery.meethue.com")]
    [InlineData("hue.example.com")]
    public void IsLocalBridgeHost_PublicAddressesAreRejected(string host)
    {
        Assert.False(HueBridgeCertificateValidation.IsLocalBridgeHost(host));
    }

    [Fact]
    public void ValidateServerCertificate_TrustedCertificateIsAccepted()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://discovery.meethue.com/");

        Assert.True(HueBridgeCertificateValidation.ValidateServerCertificate(
            request,
            certificate: null,
            chain: null,
            SslPolicyErrors.None));
    }

    [Fact]
    public void ValidateServerCertificate_LocalBridgeCertificateErrorsAreScoped()
    {
        using var bridgeRequest = new HttpRequestMessage(HttpMethod.Get, "https://192.168.1.100/api");
        using var publicRequest = new HttpRequestMessage(HttpMethod.Get, "https://discovery.meethue.com/");
        const SslPolicyErrors bridgeCertificateErrors =
            SslPolicyErrors.RemoteCertificateNameMismatch |
            SslPolicyErrors.RemoteCertificateChainErrors;

        Assert.True(HueBridgeCertificateValidation.ValidateServerCertificate(
            bridgeRequest, null, null, bridgeCertificateErrors));
        Assert.False(HueBridgeCertificateValidation.ValidateServerCertificate(
            publicRequest, null, null, bridgeCertificateErrors));
    }

    [Fact]
    public void ValidateServerCertificate_MissingCertificateIsRejected()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://192.168.1.100/api");

        Assert.False(HueBridgeCertificateValidation.ValidateServerCertificate(
            request,
            certificate: null,
            chain: null,
            SslPolicyErrors.RemoteCertificateNotAvailable));
    }
}
