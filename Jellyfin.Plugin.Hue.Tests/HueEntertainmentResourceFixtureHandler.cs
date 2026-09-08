using System.Net;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.Hue.Tests;

internal sealed class HueEntertainmentResourceFixtureHandler : DelegatingHandler
{
    public HueEntertainmentResourceFixtureHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        const string resourcePrefix = "/clip/v2/resource/entertainment/";
        var resourcePath = request.RequestUri?.AbsolutePath;
        if (request.Method == HttpMethod.Get &&
            resourcePath?.StartsWith(resourcePrefix + "ent-", StringComparison.Ordinal) == true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Existing state tests use ent-<light ID>; resolver-specific tests use unrelated IDs.
            var entertainmentId = Uri.UnescapeDataString(resourcePath[resourcePrefix.Length..]);
            var lightId = entertainmentId["ent-".Length..];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    errors = Array.Empty<object>(),
                    data = new[]
                    {
                        new
                        {
                            id = entertainmentId,
                            type = "entertainment",
                            renderer_reference = new { rid = lightId, rtype = "light" }
                        }
                    }
                }), Encoding.UTF8, "application/json")
            });
        }

        return base.SendAsync(request, cancellationToken);
    }
}
