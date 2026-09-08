using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Hue.Hue;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

public sealed class HueClientResourceResolutionTests
{
    private const string EntertainmentId = "62f81354-20c5-4f87-b187-2b301c7da177";
    private const string OtherEntertainmentId = "3b4bfb16-46f5-4cda-a5d1-091b883de4bb";
    private const string LightId = "de134f43-9064-4854-aa4a-9dc168066b62";
    private const string EntertainmentPath = "/clip/v2/resource/entertainment/" + EntertainmentId;
    private const string OtherEntertainmentPath = "/clip/v2/resource/entertainment/" + OtherEntertainmentId;
    private const string LightPath = "/clip/v2/resource/light/" + LightId;

    [Fact]
    public async Task CaptureResolvesEntertainmentServiceToDifferentLightResource()
    {
        using var area = Area(EntertainmentId);
        using var handler = new ResolutionHandler(request => request.RequestUri!.AbsolutePath switch
        {
            EntertainmentPath => JsonResponse(EntertainmentResource(EntertainmentId, LightId)),
            LightPath => JsonResponse(LightResource()),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        using var http = new HttpClient(handler);
        var client = new HueClient(http, NullLogger<HueClient>.Instance) { RetryAttempts = 0 };

        var result = await client.GetLightStatesWithResult("192.168.1.100", "fixture-app", area.RootElement);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.AttemptedCount);
        Assert.Equal(LightId, Assert.Single(result.States).Id);
        Assert.Equal(new[] { EntertainmentPath, LightPath }, handler.Paths);
        Assert.All(handler.AppKeys, key => Assert.Equal("fixture-app", key));
    }

    [Fact]
    public async Task CaptureDeduplicatesServicesAndSharedRendererLights()
    {
        using var area = Area(EntertainmentId, EntertainmentId, OtherEntertainmentId);
        using var handler = new ResolutionHandler(request => request.RequestUri!.AbsolutePath switch
        {
            EntertainmentPath => JsonResponse(EntertainmentResource(EntertainmentId, LightId)),
            OtherEntertainmentPath => JsonResponse(EntertainmentResource(OtherEntertainmentId, LightId)),
            LightPath => JsonResponse(LightResource()),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        using var http = new HttpClient(handler);
        var client = new HueClient(http, NullLogger<HueClient>.Instance) { RetryAttempts = 0 };

        var result = await client.GetLightStatesWithResult("192.168.1.100", "fixture-app", area.RootElement);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.AttemptedCount);
        Assert.Single(result.States);
        Assert.Equal(new[] { EntertainmentPath, OtherEntertainmentPath, LightPath }, handler.Paths);
    }

    [Fact]
    public async Task CaptureHandoff_ReusesOriginalStateThroughAnotherEntertainmentService()
    {
        var original = new HueClient.LightState(LightId, true, 41, 0.1, 0.2);
        var originals = new Dictionary<string, HueClient.LightState>(StringComparer.OrdinalIgnoreCase) { [LightId] = original };
        using var area = Area(OtherEntertainmentId);
        using var handler = new ResolutionHandler(request => request.RequestUri!.AbsolutePath switch
        {
            OtherEntertainmentPath => JsonResponse(EntertainmentResource(OtherEntertainmentId, LightId)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        using var http = new HttpClient(handler);
        var client = new HueClient(http, NullLogger<HueClient>.Instance) { RetryAttempts = 0 };

        var result = await client.GetLightStatesWithResult("192.168.1.100", "fixture-app", area.RootElement, null, originals, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.AttemptedCount);
        Assert.Same(original, Assert.Single(result.States));
        Assert.Equal(new[] { OtherEntertainmentPath }, handler.Paths);
    }

    [Theory]
    [InlineData(HueClient.MaxLightStateRequests - 1, true)]
    [InlineData(HueClient.MaxLightStateRequests, false)]
    public async Task CaptureHandoff_BoundsCombinedOriginalAndNewSnapshotBeforeStateReads(int originalCount, bool expectedSuccess)
    {
        var originals = Enumerable.Range(0, originalCount).ToDictionary(
            index => "original-" + index,
            index => new HueClient.LightState("original-" + index, true, 41, 0.1, 0.2),
            StringComparer.OrdinalIgnoreCase);
        using var area = Area(EntertainmentId);
        using var handler = new ResolutionHandler(request => request.RequestUri!.AbsolutePath switch
        {
            EntertainmentPath => JsonResponse(EntertainmentResource(EntertainmentId, LightId)),
            LightPath => JsonResponse(LightResource()),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        using var http = new HttpClient(handler);
        var client = new HueClient(http, NullLogger<HueClient>.Instance) { RetryAttempts = 0 };

        var result = await client.GetLightStatesWithResult("192.168.1.100", "fixture-app", area.RootElement, null, originals, CancellationToken.None);

        Assert.Equal(expectedSuccess, result.Succeeded);
        Assert.Equal(expectedSuccess ? new[] { EntertainmentPath, LightPath } : new[] { EntertainmentPath }, handler.Paths);
        Assert.Equal(expectedSuccess ? 1 : 0, result.CapturedCount);
        Assert.Equal(originalCount, originals.Count);
    }

    [Fact]
    public async Task CaptureResolvesOnlySelectedChannels()
    {
        using var area = Area(OtherEntertainmentId, EntertainmentId);
        using var handler = new ResolutionHandler(request => request.RequestUri!.AbsolutePath switch
        {
            EntertainmentPath => JsonResponse(EntertainmentResource(EntertainmentId, LightId)),
            LightPath => JsonResponse(LightResource()),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        using var http = new HttpClient(handler);
        var client = new HueClient(http, NullLogger<HueClient>.Instance) { RetryAttempts = 0 };

        var result = await client.GetLightStatesWithResult(
            "192.168.1.100", "fixture-app", area.RootElement, new HashSet<int> { 1 });

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { EntertainmentPath, LightPath }, handler.Paths);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("light")]
    [InlineData("device")]
    [InlineData("Entertainment")]
    public async Task CaptureRejectsMissingOrUnexpectedMemberTypeBeforeRequests(string? resourceType)
    {
        using var area = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            channels = new[] { new { channel_id = 0, members = new[] { new { service = new { rid = EntertainmentId, rtype = resourceType } } } } }
        }));
        using var handler = new ResolutionHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);
        var client = new HueClient(http, NullLogger<HueClient>.Instance) { RetryAttempts = 0 };

        var result = await client.GetLightStatesWithResult("192.168.1.100", "fixture-app", area.RootElement);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.AttemptedCount);
        Assert.Empty(handler.Paths);
    }

    [Theory]
    [InlineData("missing-data")]
    [InlineData("empty-data")]
    [InlineData("multiple-resources")]
    [InlineData("scalar-resource")]
    [InlineData("wrong-id")]
    [InlineData("missing-id")]
    [InlineData("wrong-type")]
    [InlineData("missing-renderer")]
    [InlineData("wrong-renderer-type")]
    [InlineData("empty-renderer-id")]
    [InlineData("overlong-renderer-id")]
    [InlineData("control-renderer-id")]
    [InlineData("error-envelope")]
    public async Task CaptureRejectsUnresolvedRendererBeforeReadingAnyLight(string fault)
    {
        var document = JsonNode.Parse(EntertainmentResource(EntertainmentId, LightId))!;
        var resource = document["data"]![0]!;
        switch (fault)
        {
            case "missing-data":
                document.AsObject().Remove("data");
                break;
            case "empty-data":
                document["data"] = new JsonArray();
                break;
            case "multiple-resources":
                document["data"]!.AsArray().Add(resource.DeepClone());
                break;
            case "scalar-resource":
                document["data"]![0] = 42;
                break;
            case "wrong-id":
                resource["id"] = OtherEntertainmentId;
                break;
            case "missing-id":
                resource.AsObject().Remove("id");
                break;
            case "wrong-type":
                resource["type"] = "light";
                break;
            case "missing-renderer":
                resource.AsObject().Remove("renderer_reference");
                break;
            case "wrong-renderer-type":
                resource["renderer_reference"]!["rtype"] = "device";
                break;
            case "empty-renderer-id":
                resource["renderer_reference"]!["rid"] = " ";
                break;
            case "overlong-renderer-id":
                resource["renderer_reference"]!["rid"] = new string('x', 129);
                break;
            case "control-renderer-id":
                resource["renderer_reference"]!["rid"] = "invalid\u0001";
                break;
            case "error-envelope":
                document["errors"]!.AsArray().Add(new JsonObject { ["description"] = "resource unavailable" });
                break;
        }
        using var area = Area(EntertainmentId);
        using var handler = new ResolutionHandler(_ => JsonResponse(document.ToJsonString()));
        using var http = new HttpClient(handler);
        var client = new HueClient(http, NullLogger<HueClient>.Instance) { RetryAttempts = 0 };

        var result = await client.GetLightStatesWithResult("192.168.1.100", "fixture-app", area.RootElement);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.AttemptedCount);
        Assert.Empty(result.States);
        Assert.Equal(new[] { EntertainmentPath }, handler.Paths);
    }

    [Fact]
    public async Task CaptureRejectsExcessiveServiceCountBeforeRequests()
    {
        using var area = Area(Enumerable.Range(0, HueClient.MaxLightStateRequests + 1).Select(index => "service-" + index).ToArray());
        using var handler = new ResolutionHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);
        var client = new HueClient(http, NullLogger<HueClient>.Instance) { RetryAttempts = 0 };

        var result = await client.GetLightStatesWithResult("192.168.1.100", "fixture-app", area.RootElement);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.AttemptedCount);
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task CaptureRetriesTransientResolutionFailure()
    {
        var attempts = 0;
        using var area = Area(EntertainmentId);
        using var handler = new ResolutionHandler(request => request.RequestUri!.AbsolutePath switch
        {
            EntertainmentPath when ++attempts == 1 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            EntertainmentPath => JsonResponse(EntertainmentResource(EntertainmentId, LightId)),
            LightPath => JsonResponse(LightResource()),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        using var http = new HttpClient(handler);
        var client = new HueClient(http, NullLogger<HueClient>.Instance) { RetryAttempts = 1 };

        var result = await client.GetLightStatesWithResult("192.168.1.100", "fixture-app", area.RootElement);

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { EntertainmentPath, EntertainmentPath, LightPath }, handler.Paths);
    }

    [Fact]
    public async Task CaptureResolutionFailurePreventsPartialLightReads()
    {
        using var area = Area(EntertainmentId, OtherEntertainmentId);
        using var handler = new ResolutionHandler(request => request.RequestUri!.AbsolutePath == EntertainmentPath
            ? JsonResponse(EntertainmentResource(EntertainmentId, LightId))
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);
        var client = new HueClient(http, NullLogger<HueClient>.Instance) { RetryAttempts = 0 };

        var result = await client.GetLightStatesWithResult("192.168.1.100", "fixture-app", area.RootElement);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.AttemptedCount);
        Assert.Empty(result.States);
        Assert.Equal(new[] { EntertainmentPath, OtherEntertainmentPath }, handler.Paths);
    }

    [Fact]
    public async Task CapturePropagatesCancellationDuringResolution()
    {
        using var cancellation = new CancellationTokenSource();
        using var area = Area(EntertainmentId);
        using var handler = new ResolutionHandler(_ =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler);
        var client = new HueClient(http, NullLogger<HueClient>.Instance) { RetryAttempts = 0 };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetLightStatesWithResult(
            "192.168.1.100", "fixture-app", area.RootElement, cancellationToken: cancellation.Token));
        Assert.Equal(new[] { EntertainmentPath }, handler.Paths);
    }

    private static JsonDocument Area(params string[] entertainmentIds) => JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        channels = entertainmentIds.Select((id, index) => new
        {
            channel_id = index,
            members = new[] { new { service = new { rid = id, rtype = "entertainment" }, index = 0 } }
        })
    }));

    private static string EntertainmentResource(string id, string lightId) => JsonSerializer.Serialize(new
    {
        errors = Array.Empty<object>(),
        data = new[] { new { id, type = "entertainment", renderer_reference = new { rid = lightId, rtype = "light" } } }
    });

    private static string LightResource() => JsonSerializer.Serialize(new
    {
        errors = Array.Empty<object>(),
        data = new[] { new { id = LightId, type = "light", on = new { on = true }, dimming = new { brightness = 50 } } }
    });

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class ResolutionHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Paths { get; } = new();
        public List<string> AppKeys { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            AppKeys.Add(Assert.Single(request.Headers.GetValues("hue-application-key")));
            return Task.FromResult(respond(request));
        }
    }
}
