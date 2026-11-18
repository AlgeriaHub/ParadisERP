using System.Net;

namespace AlgeriaPos.Tests.Hosting;

/// <summary>
/// docs/07 §6.0, backlog item 24. A till that cached <c>index.html</c> or <c>ngsw.json</c> keeps
/// running yesterday's bundle against today's API, and the cure is clearing browser storage on
/// a machine in another town. Everything else Angular emits is content-hashed and may be held.
/// </summary>
[Collection(HostCollection.Name)]
public class StaticFileCacheTests
{
    [Theory]
    [InlineData("/index.html")]
    [InlineData("/")]                 // UseDefaultFiles rewrites to index.html
    [InlineData("/register")]         // the SPA fallback serves the same file
    [InlineData("/ngsw.json")]
    public async Task The_two_files_that_announce_a_new_version_are_never_cached(string path)
    {
        using var host = new PosHost();
        using var client = host.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-cache", string.Join(", ", response.Headers.CacheControl?.ToString() ?? ""));
    }

    [Fact]
    public async Task A_hashed_bundle_is_left_cacheable()
    {
        using var host = new PosHost();
        using var client = host.CreateClient();

        var response = await client.GetAsync("/main-TEST0001.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl is null || !response.Headers.CacheControl.NoCache);
    }
}
