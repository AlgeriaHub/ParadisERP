using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ParadiERP.Tests.Hosting;

/// <summary>
/// docs/07 §6.0. Behind Caddy the API is spoken to over http, so <c>Request.Scheme</c> is http
/// unless the forwarded headers are honoured — and the day SSO is switched on, the OIDC redirect
/// URI is built from that scheme. It is off by default because on a directly exposed API those
/// headers are whatever the caller typed. <c>/health</c> reports the scheme for exactly this
/// reason: it is how an operator checks the proxy without enabling SSO first.
/// </summary>
[Collection(HostCollection.Name)]
public class ForwardedHeadersTests
{
    [Fact]
    public async Task A_forwarded_scheme_is_ignored_while_no_proxy_is_declared()
    {
        using var host = new PosHost();
        using var client = host.CreateClient();

        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        Assert.Equal("http", await SchemeAsync(client));
    }

    [Fact]
    public async Task A_forwarded_scheme_is_honoured_once_the_proxy_is_declared()
    {
        using var host = new PosHost(new Dictionary<string, string?> { ["Proxy__Enabled"] = "true" });
        using var client = host.CreateClient();

        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        Assert.Equal("https", await SchemeAsync(client));
    }

    [Fact]
    public void With_no_trusted_network_named_the_middleware_checks_no_address_at_all()
    {
        using var host = new PosHost(new Dictionary<string, string?> { ["Proxy__Enabled"] = "true" });

        var options = host.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        // Both empty is what makes the middleware skip the address check. Left at the framework
        // default — loopback only — the headers were parsed and then silently thrown away,
        // because a proxy in another container reaches the API from the compose bridge and
        // never from 127.0.0.1: Proxy__Enabled read as switched on and changed nothing. Found
        // by asking the running image, which is why the assertion is on the options and the
        // proof is in docs/08.
        Assert.Empty(options.KnownNetworks);
        Assert.Empty(options.KnownProxies);
    }

    [Fact]
    public void A_named_trusted_network_narrows_the_check_to_exactly_what_was_named()
    {
        using var host = new PosHost(new Dictionary<string, string?>
        {
            ["Proxy__Enabled"] = "true",
            ["Proxy__TrustedNetworks__0"] = "172.16.0.0/12",
            ["Proxy__TrustedNetworks__1"] = "10.1.2.3",
        });

        var options = host.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        var network = Assert.Single(options.KnownNetworks);
        Assert.Equal("172.16.0.0", network.Prefix.ToString());
        Assert.Equal(12, network.PrefixLength);
        Assert.Equal("10.1.2.3", Assert.Single(options.KnownProxies).ToString());
    }

    // Not async: the host refuses in its constructor, so there is nothing to await here.
    [Fact]
    public void A_trusted_network_that_is_neither_an_address_nor_a_range_refuses_to_start()
    {
        var refusal = Assert.ThrowsAny<Exception>(() => new PosHost(new Dictionary<string, string?>
        {
            ["Proxy__Enabled"] = "true",
            ["Proxy__TrustedNetworks__0"] = "caddy",
        }));

        var message = string.Join(" | ", Unwrap(refusal));
        Assert.Contains("Proxy:TrustedNetworks", message);
    }

    private static async Task<string?> SchemeAsync(HttpClient client)
    {
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("scheme").GetString();
    }

    private static IEnumerable<string> Unwrap(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException) yield return current.Message;
    }
}
