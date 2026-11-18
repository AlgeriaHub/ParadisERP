using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AlgeriaPos.Tests.Hosting;

/// <summary>
/// A till opened over plain http cannot sell or work offline, so the deployed site sends http on
/// to https for good (Https:Redirect). Development and every other host stay on http.
/// </summary>
[Collection(HostCollection.Name)]
public class HttpsRedirectTests
{
    [Fact]
    public async Task With_the_redirect_on_plain_http_is_sent_on_to_https_permanently()
    {
        using var host = new PosHost(new Dictionary<string, string?> { ["Https__Redirect"] = "true" });
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/auth/ping");

        Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
        Assert.Equal("https://localhost/api/auth/ping", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Without_it_http_is_served_as_it_is()
    {
        using var host = new PosHost();
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/auth/ping");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
