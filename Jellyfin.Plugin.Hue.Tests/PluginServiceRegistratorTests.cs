using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Hue;
using Jellyfin.Plugin.Hue.Service;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaEncoding;
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
        var environmentDescriptor = Assert.Single(services, service => service.ServiceType == typeof(IHueEnvironmentProbe));
        Assert.Equal(ServiceLifetime.Singleton, environmentDescriptor.Lifetime);
        var diagnosticsCancellationDescriptor = Assert.Single(services, service => service.ServiceType == typeof(HueDiagnosticsCancellationGate));
        Assert.Equal(ServiceLifetime.Singleton, diagnosticsCancellationDescriptor.Lifetime);
        var localDiscoveryDescriptor = Assert.Single(services, service => service.ServiceType == typeof(IHueBridgeLocalDiscovery));
        Assert.Equal(ServiceLifetime.Singleton, localDiscoveryDescriptor.Lifetime);

        using var provider = services.AddLogging().BuildServiceProvider();
        var first = provider.GetRequiredService<IHueStreamTester>();
        var second = provider.GetRequiredService<IHueStreamTester>();
        Assert.Same(first, second);
        var firstProbe = provider.GetRequiredService<IHueEnvironmentProbe>();
        var secondProbe = provider.GetRequiredService<IHueEnvironmentProbe>();
        Assert.Same(firstProbe, secondProbe);
        var firstDiagnosticsCancellation = provider.GetRequiredService<HueDiagnosticsCancellationGate>();
        var secondDiagnosticsCancellation = provider.GetRequiredService<HueDiagnosticsCancellationGate>();
        Assert.Same(firstDiagnosticsCancellation, secondDiagnosticsCancellation);
        var firstLocalDiscovery = provider.GetRequiredService<IHueBridgeLocalDiscovery>();
        var secondLocalDiscovery = provider.GetRequiredService<IHueBridgeLocalDiscovery>();
        Assert.Same(firstLocalDiscovery, secondLocalDiscovery);
    }

    [Fact]
    public void CreateHueHttpClientHandler_DisablesRedirects()
    {
        using var handler = PluginServiceRegistrator.CreateHueHttpClientHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.NotNull(handler.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public async Task RegisterServices_UsesConfiguredMediaEncoderForEnvironmentProbe()
    {
        var services = new ServiceCollection();
        var processPath = Environment.ProcessPath;
        Assert.False(string.IsNullOrWhiteSpace(processPath));
        var mediaEncoder = Mock.Of<IMediaEncoder>(encoder => encoder.EncoderPath == processPath);
        services.AddSingleton(mediaEncoder);

        new PluginServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>());

        using var provider = services.AddLogging().BuildServiceProvider();
        var probe = Assert.IsType<HueEnvironmentProbe>(provider.GetRequiredService<IHueEnvironmentProbe>());
        var result = await probe.CheckAsync();

        Assert.Equal(Path.GetFullPath(processPath!), result.Ffmpeg.ExecutablePath);
    }

    [Theory]
    [InlineData("192.168.1.100")]
    [InlineData("10.0.0.15")]
    [InlineData("172.16.20.4")]
    [InlineData("169.254.1.20")]
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
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("ff02::1")]
    public void IsLocalBridgeHost_PublicOrNonRoutableAddressesAreRejected(string host)
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

    [Theory]
    [InlineData("192.168.1.100")]
    [InlineData("10.0.0.15")]
    [InlineData("172.16.20.4")]
    [InlineData("169.254.1.20")]
    [InlineData("fc00::1234")]
    [InlineData("fe80::1234")]
    public async Task ResolveLocalBridgeAddress_LiteralPrivateAddressesAreReturned(string address)
    {
        var resolved = await HueBridgeCertificateValidation.ResolveLocalBridgeAddressAsync(
            address,
            CancellationToken.None);

        Assert.Equal(IPAddress.Parse(address), resolved);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("2001:db8::10")]
    public async Task ResolveLocalBridgeAddress_LiteralPublicOrNonRoutableAddressesAreRejected(string address)
    {
        await Assert.ThrowsAsync<SocketException>(() => HueBridgeCertificateValidation.ResolveLocalBridgeAddressAsync(
            address,
            CancellationToken.None));
    }

    [Fact]
    public void SelectLocalBridgeAddress_RejectsAnyPublicAnswerBeforeCredentialUse()
    {
        var exception = Assert.Throws<SocketException>(() => HueBridgeCertificateValidation.SelectLocalBridgeAddress(
        [
            IPAddress.Parse("192.168.1.100"),
            IPAddress.Parse("203.0.113.10")
        ]));

        Assert.Equal(SocketError.AddressNotAvailable, exception.SocketErrorCode);
    }

    [Fact]
    public void SelectLocalBridgeAddress_AllowsPrivateAndLinkLocalAnswers()
    {
        var resolved = HueBridgeCertificateValidation.SelectLocalBridgeAddress(
        [
            IPAddress.Parse("fe80::1234"),
            IPAddress.Parse("192.168.1.100")
        ]);

        Assert.Equal(IPAddress.Parse("fe80::1234"), resolved);
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
