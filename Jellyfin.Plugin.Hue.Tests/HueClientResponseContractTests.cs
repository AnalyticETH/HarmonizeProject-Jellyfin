using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Hue.Hue;
using Jellyfin.Plugin.Hue.Service;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

public sealed class HueClientResponseContractTests
{
    private const string ResourceId = "test-resource";
    private const string SecretSentinel = "private-bridge-response-sentinel";

    public static IEnumerable<object[]> InvalidMutations()
    {
        foreach (var operation in new[] { "start", "stop", "brightness", "restore" })
        {
            var type = operation is "start" or "stop" ? "entertainment_configuration" : "light";
            var success = MutationBody(type);
            foreach (var invalid in new[]
            {
                "", "{}", "[]", "null", "invalid JSON",
                "{\"errors\":[],\"data\":[]}",
                "{\"errors\":[],\"data\":{}}",
                "{\"errors\":[],\"data\":[null]}",
                success.Replace("\"errors\":[],", "", StringComparison.Ordinal),
                success.Replace("\"errors\":[]", "\"errors\":null", StringComparison.Ordinal),
                success.Replace("\"errors\":[]", "\"errors\":{}", StringComparison.Ordinal),
                success.Replace("\"errors\":[]", "\"errors\":[{\"description\":\"" + SecretSentinel + "\"}]", StringComparison.Ordinal),
                success.Replace(ResourceId, "other-resource", StringComparison.Ordinal),
                success.Replace(type, "device", StringComparison.Ordinal),
                success.Replace("\"rid\":\"" + ResourceId + "\"", "\"rid\":null", StringComparison.Ordinal)
            })
            {
                yield return new object[] { operation, invalid };
            }
        }
    }

    [Theory]
    [MemberData(nameof(InvalidMutations))]
    public async Task MutationRequiresErrorFreeAcknowledgementOfRequestedResource(string operation, string body)
    {
        var logger = new Mock<ILogger<HueClient>>();
        using var handler = new ResponseHandler((_, _) => Task.FromResult(JsonResponse(body)));
        using var http = new HttpClient(handler);
        var client = new HueClient(http, logger.Object) { RetryAttempts = 1 };

        Assert.False(await Mutate(client, operation));

        Assert.Equal(1, handler.RequestCount);
        Assert.DoesNotContain(logger.Invocations, invocation =>
            invocation.Arguments.Any(argument => argument?.ToString()?.Contains(SecretSentinel, StringComparison.Ordinal) == true));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("brightness")]
    [InlineData("restore")]
    public async Task MutationAcceptsTypedAcknowledgement(string operation)
    {
        var type = operation is "start" or "stop" ? "entertainment_configuration" : "light";
        using var handler = new ResponseHandler((_, _) => Task.FromResult(JsonResponse(MutationBody(type))));
        using var http = new HttpClient(handler);
        var client = new HueClient(http, NullLogger<HueClient>.Instance) { RetryAttempts = 0 };

        Assert.True(await Mutate(client, operation));
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialMutationFailureDoesNotCountRejectedLightAsRestoredOrUpdated(bool brightness)
    {
        using var handler = new ResponseHandler((request, _) => Task.FromResult(JsonResponse(
            request.RequestUri!.AbsolutePath.EndsWith("/rejected", StringComparison.Ordinal)
                ? "{\"errors\":[{\"description\":\"rejected\"}],\"data\":[]}"
                : MutationBody("light"))));
        using var http = new HttpClient(handler);
        var client = new HueClient(http, NullLogger<HueClient>.Instance) { RetryAttempts = 0 };
        var states = new List<HueClient.LightState> { new("rejected", true, 50, 0.3, 0.3), new(ResourceId, false, 25, 0.3, 0.3) };

        if (brightness)
        {
            var result = await client.SetLightBrightnessWithResult("192.168.1.100", "fixture-app", states, 25);
            Assert.False(result.Succeeded);
            Assert.Equal(2, result.AttemptedCount);
            Assert.Equal(1, result.UpdatedCount);
            Assert.Equal(1, result.FailedCount);
        }
        else
        {
            var result = await client.RestoreLightStatesWithResult("192.168.1.100", "fixture-app", states);
            Assert.False(result.Succeeded);
            Assert.Equal(2, result.AttemptedCount);
            Assert.Equal(1, result.RestoredCount);
            Assert.Equal(1, result.FailedCount);
        }

        Assert.Equal(2, handler.RequestCount);
    }

    [Theory]
    [InlineData("areas", false)]
    [InlineData("areas", true)]
    [InlineData("configuration", false)]
    [InlineData("register", false)]
    [InlineData("probe", false)]
    [InlineData("start", false)]
    [InlineData("stop", false)]
    [InlineData("brightness", false)]
    [InlineData("restore", false)]
    public async Task StalledResponseBodyIsBoundedAndDisposed(string operation, bool ignoreCancellation)
    {
        using var stream = new StallingStream(ignoreCancellation);
        using var handler = new ResponseHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        }));
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(150) };
        var client = new HueClient(http, NullLogger<HueClient>.Instance) { RetryAttempts = 0 };

        var result = await Invoke(client, operation).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result);
        await stream.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task StalledCloudBodyReachesLocalDiscovery()
    {
        using var stream = new StallingStream();
        using var handler = new ResponseHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        }));
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(150) };
        var local = new Mock<IHueBridgeLocalDiscovery>();
        local.Setup(discovery => discovery.DiscoverAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "192.168.1.120" });
        var client = new HueClient(http, NullLogger<HueClient>.Instance, local.Object);

        var result = await client.DiscoverBridgeIps().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("192.168.1.120", Assert.Single(result));
        await stream.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        local.Verify(discovery => discovery.DiscoverAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("areas")]
    [InlineData("configuration")]
    [InlineData("register")]
    [InlineData("probe")]
    [InlineData("discovery")]
    [InlineData("start")]
    public async Task CallerCancellationDuringBodyReadPropagatesWithoutRetryOrFallback(string operation)
    {
        using var stream = new StallingStream();
        using var handler = new ResponseHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        }));
        using var http = new HttpClient(handler);
        var local = new Mock<IHueBridgeLocalDiscovery>(MockBehavior.Strict);
        var client = new HueClient(http, NullLogger<HueClient>.Instance, local.Object) { RetryAttempts = 3 };
        using var cancellation = new CancellationTokenSource();
        var pending = Invoke(client, operation, cancellation.Token);
        await stream.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await stream.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, handler.RequestCount);
        local.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BodyTimeoutRetriesWithFreshDeadlineAndLeavesSharedTransportUnchanged()
    {
        using var stream = new StallingStream();
        var requests = 0;
        using var handler = new ResponseHandler((_, _) => Task.FromResult(++requests == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }
            : JsonResponse("{\"data\":[]}")));
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(150) };
        var primary = new HueClient(http, NullLogger<HueClient>.Instance) { RetryAttempts = 1 };
        var client = primary.CreatePlaybackClient();

        var areas = await client.GetEntertainmentAreas("192.168.1.100", "fixture-app").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(areas);
        Assert.Empty(areas);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(TimeSpan.FromMilliseconds(150), http.Timeout);
        await stream.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("areas")]
    [InlineData("discovery")]
    public async Task HeadersAndBodyShareOneDeadline(string operation)
    {
        var phaseDelay = TimeSpan.FromMilliseconds(600);
        var timeout = TimeSpan.FromSeconds(1);
        var body = operation == "areas"
            ? "{\"data\":[]}"
            : "[{\"internalipaddress\":\"192.168.1.120\"}]";
        using var stream = new ObservedReadStream(Encoding.UTF8.GetBytes(body), phaseDelay);
        using var handler = new ResponseHandler(async (_, token) =>
        {
            await Task.Delay(phaseDelay, token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        });
        using var http = new HttpClient(handler) { Timeout = timeout };
        var client = new HueClient(http, NullLogger<HueClient>.Instance) { RetryAttempts = 0 };

        var pending = Invoke(client, operation);
        await stream.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5)));

        await stream.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, stream.ReadCount);
        Assert.Equal(0, stream.BytesRead);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(timeout, http.Timeout);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ResponseReturnedAfterDeadlineOrCallerCancellationIsDisposed(bool callerCancellation, bool failDisposal)
    {
        var logger = new Mock<ILogger<HueClient>>();
        var responseReady = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stream = new ObservedReadStream(Encoding.UTF8.GetBytes("{\"data\":[]}"), failDisposal: failDisposal);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        using var handler = new ResponseHandler((_, _) =>
        {
            requestEntered.TrySetResult(true);
            return responseReady.Task;
        });
        using var http = new HttpClient(handler)
        {
            Timeout = callerCancellation ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(150)
        };
        var client = new HueClient(http, logger.Object) { RetryAttempts = 0 };
        using var cancellation = new CancellationTokenSource();

        var pending = client.GetEntertainmentAreas("192.168.1.100", "fixture-app", cancellation.Token);
        await requestEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (callerCancellation)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        else
        {
            Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        Assert.False(stream.Disposed.Task.IsCompleted);
        responseReady.SetResult(response);
        await stream.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, handler.RequestCount);
        Assert.DoesNotContain(logger.Invocations, invocation =>
            invocation.Arguments.Any(argument => argument?.ToString()?.Contains(SecretSentinel, StringComparison.Ordinal) == true));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task StreamReturnedAfterDeadlineOrCallerCancellationIsDisposed(bool callerCancellation, bool failDisposal)
    {
        var logger = new Mock<ILogger<HueClient>>();
        using var stream = new ObservedReadStream(Encoding.UTF8.GetBytes("{\"data\":[]}"), failDisposal: failDisposal);
        using var content = new DeferredStreamContent();
        using var handler = new ResponseHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
        }));
        using var http = new HttpClient(handler)
        {
            Timeout = callerCancellation ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(150)
        };
        var client = new HueClient(http, logger.Object) { RetryAttempts = 0 };
        using var cancellation = new CancellationTokenSource();

        var pending = client.GetEntertainmentAreas("192.168.1.100", "fixture-app", cancellation.Token);
        await content.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (callerCancellation)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        else
        {
            Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        await content.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stream.Disposed.Task.IsCompleted);
        content.StreamReady.SetResult(stream);
        await stream.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, stream.ReadCount);
        Assert.Equal(1, handler.RequestCount);
        Assert.DoesNotContain(logger.Invocations, invocation =>
            invocation.Arguments.Any(argument => argument?.ToString()?.Contains(SecretSentinel, StringComparison.Ordinal) == true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateStreamCleanupObservesAcquisitionAndDisposalFailures(bool failAcquisition)
    {
        using var stream = new ObservedReadStream(Array.Empty<byte>(), failDisposal: !failAcquisition);
        var pendingStream = failAcquisition
            ? Task.FromException<Stream>(new InvalidDataException(SecretSentinel))
            : Task.FromResult<Stream>(stream);
        var method = typeof(HueClient).GetMethod("DisposeLateResponseStreamAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var cleanup = Assert.IsAssignableFrom<Task>(method.Invoke(null, new object[] { pendingStream }));
        await cleanup.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(cleanup.IsCompletedSuccessfully);
        if (!failAcquisition)
            await stream.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("brightness")]
    [InlineData("restore")]
    public async Task MutationBodyRetainsStreamingSizeBound(string operation)
    {
        var type = operation is "start" or "stop" ? "entertainment_configuration" : "light";
        var body = MutationBody(type) + new string(' ', HueClient.MaxResponseBodyBytes);
        using var stream = new ObservedReadStream(Encoding.UTF8.GetBytes(body));
        using var content = new StreamContent(stream);
        Assert.Null(content.Headers.ContentLength);
        using var handler = new ResponseHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
        }));
        using var http = new HttpClient(handler);
        var client = new HueClient(http, NullLogger<HueClient>.Instance) { RetryAttempts = 1 };

        Assert.False(await Mutate(client, operation));
        Assert.Equal(HueClient.MaxResponseBodyBytes + 1, stream.BytesRead);
        Assert.True(stream.ReadCount > 0);
        Assert.Equal(1, handler.RequestCount);
    }

    private static async Task<bool> Invoke(HueClient client, string operation, CancellationToken token = default) => operation switch
    {
        "areas" => await client.GetEntertainmentAreas("192.168.1.100", "fixture-app", token) != null,
        "configuration" => await client.GetEntertainmentConfiguration("192.168.1.100", "fixture-app", ResourceId, token) != null,
        "register" => await client.RegisterWithBridge("192.168.1.100", token) != null,
        "probe" => await client.GetBridgeCertificateFingerprint("192.168.1.100", token) != null,
        "discovery" => (await client.DiscoverBridgeIps(token)).Count > 0,
        _ => await Mutate(client, operation, token)
    };

    private static async Task<bool> Mutate(HueClient client, string operation, CancellationToken token = default)
    {
        var states = new List<HueClient.LightState> { new(ResourceId, true, 50, 0.3, 0.3) };
        return operation switch
        {
            "start" => await client.StartEntertainmentArea("192.168.1.100", "fixture-app", ResourceId, token),
            "stop" => await client.StopEntertainmentAreaWithResult("192.168.1.100", "fixture-app", ResourceId, token),
            "brightness" => (await client.SetLightBrightnessWithResult("192.168.1.100", "fixture-app", states, 25, token)).Succeeded,
            "restore" => (await client.RestoreLightStatesWithResult("192.168.1.100", "fixture-app", states, token)).Succeeded,
            _ => throw new ArgumentException("Unknown fixture operation.", nameof(operation))
        };
    }

    private static string MutationBody(string type) => JsonSerializer.Serialize(new
    {
        errors = Array.Empty<object>(),
        data = new[] { new { rid = ResourceId, rtype = type } }
    });

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class ResponseHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return respond(request, cancellationToken);
        }
    }

    private sealed class ObservedReadStream(byte[] bytes, TimeSpan firstReadDelay = default, bool failDisposal = false) : MemoryStream(bytes)
    {
        private bool _disposalFailureThrown;
        public int ReadCount { get; private set; }
        public int BytesRead { get; private set; }
        public TaskCompletionSource<bool> ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            ReadEntered.TrySetResult(true);
            // Only the first read waits; a second delay at EOF would let a separate
            // body timeout incorrectly satisfy the shared-deadline regression.
            if (ReadCount == 1 && firstReadDelay > TimeSpan.Zero)
                await Task.Delay(firstReadDelay, cancellationToken);
            var count = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += count;
            return count;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            var throwFailure = failDisposal && !_disposalFailureThrown;
            _disposalFailureThrown = true;
            Disposed.TrySetResult(true);
            if (throwFailure)
                throw new IOException(SecretSentinel);
        }
    }

    private sealed class DeferredStreamContent : HttpContent
    {
        public TaskCompletionSource<Stream> StreamReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            ReadEntered.TrySetResult(true);
            return StreamReady.Task;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("The fixture must be consumed as a stream.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Disposed.TrySetResult(true);
        }
    }

    private sealed class StallingStream(bool ignoreCancellation = false) : MemoryStream
    {
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanSeek => false;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadEntered.TrySetResult(true);
            return new ValueTask<int>(ignoreCancellation ? _read.Task : _read.Task.WaitAsync(cancellationToken));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            _read.TrySetResult(0);
            Disposed.TrySetResult(true);
        }
    }
}
