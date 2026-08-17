using System.Net;
using System.Net.Http;
using System.Net.Security;
using Jellyfin.Plugin.Hue;
using Jellyfin.Plugin.Hue.Service;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

public class PluginServiceRegistratorTests
{
    [Fact]
    public void RegisterServices_UsesSingletonDiagnosticTester()
    {
        var services = new ServiceCollection();
        var registrator = new PluginServiceRegistrator();

        registrator.RegisterServices(services, Mock.Of<IServerApplicationHost>());

        var descriptor = Assert.Single(services, service => service.ServiceType == typeof(IHueStreamTester));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);

        using var provider = services.AddLogging().BuildServiceProvider();
        var first = provider.GetRequiredService<IHueStreamTester>();
        var second = provider.GetRequiredService<IHueStreamTester>();
        Assert.Same(first, second);
    }

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

    [Theory]
    [InlineData("192.168.1.100")]
    [InlineData("hue-bridge.local")]
    public void IsValidBridgeAddress_AcceptsLocalBridgeTargets(string address)
    {
        Assert.True(HueBridgeCertificateValidation.IsValidBridgeAddress(address));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("hue.example.com")]
    [InlineData("https://192.168.1.100")]
    public void IsValidBridgeAddress_RejectsPublicOrUrlTargets(string address)
    {
        Assert.False(HueBridgeCertificateValidation.IsValidBridgeAddress(address));
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
