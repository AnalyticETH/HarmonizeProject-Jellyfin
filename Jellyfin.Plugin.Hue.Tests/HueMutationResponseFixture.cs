using System.Net;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.Hue.Tests;

internal static class HueMutationResponseFixture
{
    public static HttpResponseMessage Success(HttpRequestMessage request)
    {
        var parts = request.RequestUri!.AbsolutePath.Split('/');
        if (request.Method != HttpMethod.Put || parts.Length != 6 ||
            parts[4] is not ("light" or "entertainment_configuration"))
        {
            throw new InvalidOperationException("Mutation fixtures require a light or entertainment configuration PUT.");
        }

        return Success(parts[4], Uri.UnescapeDataString(parts[5]));
    }

    public static HttpResponseMessage Success(string resourceType, string resourceId) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            errors = Array.Empty<object>(),
            data = new[] { new { rid = resourceId, rtype = resourceType } }
        }), Encoding.UTF8, "application/json")
    };
}
