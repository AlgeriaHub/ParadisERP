using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadisERP.Application.Receivables;
using ParadisERP.Tests.Hosting;

namespace ParadisERP.Tests.Security;

/// <summary>
/// FR-2B (the owner, 2026-10-03) on the till's cheques and transfers: a bounce says why, in the
/// note the settle route always stored. The reason is asked before anything is looked up, so the
/// rule holds whatever the payment; collecting is unchanged and needs no note.
/// </summary>
[Collection(HostCollection.Name)]
public class BounceReasonTests
{
    [Fact]
    public async Task The_till_s_settle_route_refuses_a_bounce_with_no_reason_and_lets_collecting_through()
    {
        using var host = new PosHost();
        using var admin = host.CreateClient();
        var login = await admin.PostAsJsonAsync("/api/auth/login",
            new { username = PosHost.BootstrapUsername, password = PosHost.BootstrapPassword, deviceCode = "C01" });
        using (var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync()))
            admin.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", body.RootElement.GetProperty("token").GetString());

        var unknown = Guid.NewGuid();
        foreach (var note in new string?[] { null, "", "   " })
        {
            var refused = await admin.PostAsJsonAsync($"/api/treasury/pending-payments/{unknown}/settle", new { status = "Bounced", note });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains(ReceivableRules.BounceNeedsReason, await refused.Content.ReadAsStringAsync());
        }

        // With a reason, or when collecting, the route goes on to find the payment, as before.
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PostAsJsonAsync($"/api/treasury/pending-payments/{unknown}/settle", new { status = "Bounced", note = "Provision insuffisante" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PostAsJsonAsync($"/api/treasury/pending-payments/{unknown}/settle", new { status = "Confirmed", note = (string?)null })).StatusCode);
    }
}
