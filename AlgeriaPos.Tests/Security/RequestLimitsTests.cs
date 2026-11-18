using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AlgeriaPos.API.Security;
using AlgeriaPos.Tests.Hosting;
using Microsoft.AspNetCore.RateLimiting;

namespace AlgeriaPos.Tests.Security;

/// <summary>
/// "No API may be open to a script that submits requests without end" (owner, security review,
/// 2026-10-10). The site has no proxy in front of it, so the limits are the app's own: per address
/// on every call, tighter on sign-in, per session on the signed-in routes, and a body size on every
/// request. Each limit is proven here with small values; the shipped values are generous enough
/// that a shop never meets them.
/// </summary>
[Collection(HostCollection.Name)]
public class RequestLimitsTests
{
    private static Dictionary<string, string?> Limits(params (string Key, string Value)[] values)
    {
        var settings = new Dictionary<string, string?> { ["RateLimits__Enabled"] = "true" };
        foreach (var (key, value) in values) settings["RateLimits__" + key] = value;
        return settings;
    }

    private static object WrongLogin => new { username = "admin", password = "not-the-password", deviceCode = "C01" };

    [Fact]
    public async Task Sign_in_attempts_from_one_address_are_cut_off_and_told_when_to_come_back()
    {
        using var host = new PosHost(Limits(("LoginPerMinute", "3")));
        using var client = host.CreateClient();

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", WrongLogin)).StatusCode);

        var refused = await client.PostAsJsonAsync("/api/auth/login", WrongLogin);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.True(refused.Headers.RetryAfter is not null, "a refused call says when to try again");
        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal(RequestLimits.RateLimitedCode, body.RootElement.GetProperty("code").GetString());
        Assert.True(body.RootElement.GetProperty("retryAfterSeconds").GetInt32() >= 1);
    }

    [Fact]
    public async Task Every_call_is_limited_per_address_even_anonymous_ones()
    {
        using var host = new PosHost(Limits(("PerAddressBurst", "5"), ("PerAddressPerSecond", "1")));
        using var client = host.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++) statuses.Add((await client.GetAsync("/api/auth/ping")).StatusCode);

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        Assert.Equal(HttpStatusCode.OK, statuses[0]);
    }

    [Fact]
    public async Task A_shop_working_normally_is_never_refused_at_the_shipped_values()
    {
        // The shipped limits with nothing overridden: a burst a back-office page makes, and a few
        // sign-ins at the start of a shift, all pass.
        using var host = new PosHost(new Dictionary<string, string?> { ["RateLimits__Enabled"] = "true" });
        using var client = host.CreateClient();

        for (var i = 0; i < 60; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/ping")).StatusCode);
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { username = PosHost.BootstrapUsername, password = PosHost.BootstrapPassword, deviceCode = "C01" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Switched_off_there_is_no_limit_at_all()
    {
        using var host = new PosHost(new Dictionary<string, string?>
        {
            ["RateLimits__Enabled"] = "false", ["RateLimits__PerAddressBurst"] = "1", ["RateLimits__PerAddressPerSecond"] = "1",
        });
        using var client = host.CreateClient();

        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/ping")).StatusCode);
    }

    [Fact]
    public async Task A_request_bigger_than_its_route_allows_is_refused_before_it_is_read()
    {
        // Body limits apply whether or not the rate limits are on.
        using var host = new PosHost();
        using var client = host.CreateClient();

        var huge = "{\"username\":\"" + new string('a', 3 * 1024 * 1024) + "\",\"password\":\"x\"}";
        var response = await client.PostAsync("/api/auth/login", new StringContent(huge, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task A_username_or_password_longer_than_any_account_can_have_is_refused_without_a_lookup()
    {
        using var host = new PosHost();
        using var client = host.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { username = new string('a', 65), password = "x", deviceCode = "C01" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("credentialsTooLong", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void Every_api_route_has_a_rate_limit_and_a_body_size()
    {
        var missing = EndpointAuthorizationTests.Inventory()
            .Where(r => r.Pattern.StartsWith("/api", StringComparison.Ordinal) && !r.Pattern.StartsWith("/api/platform", StringComparison.Ordinal))
            .Where(r => r.Endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>() is null
                || r.Endpoint.Metadata.GetMetadata<RequestLimits.BodyLimit>() is null)
            .Select(r => r.Pattern)
            .ToList();

        Assert.Empty(missing);
    }

    [Theory]
    [InlineData("POST", "/api/auth/login", RequestLimits.Login)]
    [InlineData("POST", "/api/auth/password", RequestLimits.Login)]
    [InlineData("POST", "/api/auth/two-factor/enable", RequestLimits.Login)]
    [InlineData("POST", "/api/auth/two-factor/disable", RequestLimits.Login)]
    [InlineData("POST", "/api/sync/push", RequestLimits.SyncPush)]
    [InlineData("GET", "/api/sync/pull", RequestLimits.SyncPull)]
    [InlineData("POST", "/api/sales/{id:guid}/send-receipt", RequestLimits.Receipts)]
    [InlineData("POST", "/api/catalog/customers/{id:guid}/attachments", RequestLimits.Uploads)]
    [InlineData("POST", "/api/rentals/contracts/{id:guid}/attachments", RequestLimits.Uploads)]
    [InlineData("POST", "/api/catalog/stock/opening/import/preview", RequestLimits.Uploads)]
    [InlineData("GET", "/api/catalog/customers/{id:guid}/attachments", RequestLimits.Session)]
    [InlineData("GET", "/api/catalog/products", RequestLimits.Session)]
    public void Each_kind_of_route_gets_its_own_limit(string method, string pattern, string policy)
    {
        var options = new RequestLimits.Options();
        var (chosen, body) = RequestLimits.Classify([method], pattern, options);

        Assert.Equal(policy, chosen);
        Assert.True(body >= options.DefaultBodyBytes);
        if (policy == RequestLimits.Uploads) Assert.Equal(options.UploadBodyBytes, body);
    }
}
