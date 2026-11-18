using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadiERP.Application.Regional;
using ParadiERP.Tests.Hosting;
using ParadiERP.Tests.Security;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Tests.Catalog;

/// <summary>
/// Each branch's own commercial register, NIF, NIS and article number (owner, 2026-09-30, answer
/// 2b: every branch has its own and prints them on its documents). Read exactly as the company's
/// are — the profile's keys, trimmed, blanks dropped, 40 characters at most — but none is
/// required, because a blank one prints the company's. A client that sends no dictionary at all
/// leaves the stored ones alone.
/// </summary>
[Collection(HostCollection.Name)]
public class BranchFiscalIdsTests
{
    // ---------- the rule, pure ----------

    private static RegionalSettings Algeria() => RegionalPresets.Create("DZ")!;

    [Fact]
    public void Values_are_trimmed_and_blanks_dropped()
    {
        Assert.Null(FiscalIdValues.TryNormalize(Algeria(),
            new Dictionary<string, string> { ["rc"] = " 21/06-0729922B23 ", ["nif"] = "  ", ["nis"] = "" }, null, out var clean));
        Assert.Equal(new Dictionary<string, string> { ["rc"] = "21/06-0729922B23" }, clean);
    }

    [Fact]
    public void A_key_the_profile_does_not_define_or_an_over_long_value_is_refused()
    {
        Assert.Contains("'ice'", FiscalIdValues.TryNormalize(Algeria(), new Dictionary<string, string> { ["ice"] = "1" }, null, out _));
        Assert.Contains("40", FiscalIdValues.TryNormalize(Algeria(), new Dictionary<string, string> { ["nif"] = new string('9', 41) }, null, out _));
    }

    [Fact]
    public void A_value_under_a_key_the_operator_removed_from_the_profile_is_kept()
    {
        Assert.Null(FiscalIdValues.TryNormalize(Algeria(), new Dictionary<string, string> { ["nif"] = "1" },
            new Dictionary<string, string> { ["nif"] = "0", ["old"] = "kept" }, out var clean));
        Assert.Equal(new Dictionary<string, string> { ["nif"] = "1", ["old"] = "kept" }, clean);
    }

    // ---------- the endpoint ----------

    private static async Task<HttpClient> SignedInAsync(PosHost host)
    {
        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = PosHost.BootstrapUsername, password = PosHost.BootstrapPassword, deviceCode = "C01",
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.RootElement.GetProperty("token").GetString());
        return client;
    }

    private static object Branch(Dictionary<string, string>? fiscalIds) => new
    {
        nameAr = "سكيكدة", nameFr = "Skikda", nameEn = "Skikda", address = "Skikda", phone = "",
        isMain = false, allowNegativeStock = false, isActive = true, fiscalIds,
    };

    private static async Task<Dictionary<string, string>> StoredAsync(HttpClient client, Guid id)
    {
        var rows = await client.GetFromJsonAsync<JsonElement>("/api/catalog/branches");
        var row = rows.EnumerateArray().Single(b => b.GetProperty("id").GetGuid() == id);
        return row.GetProperty("fiscalIds").Deserialize<Dictionary<string, string>>()!;
    }

    [Fact]
    public async Task A_branch_stores_its_own_identifiers_and_a_request_without_them_keeps_them()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);

        var created = await client.PostAsJsonAsync("/api/catalog/branches",
            Branch(new() { ["rc"] = " 21/06-0729922B23 ", ["nif"] = "", ["ai"] = "21060123456" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(new Dictionary<string, string> { ["rc"] = "21/06-0729922B23", ["ai"] = "21060123456" }, await StoredAsync(client, id));

        // An older screen that knows nothing of the field does not wipe them.
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/catalog/branches/{id}", Branch(null))).StatusCode);
        Assert.Equal(2, (await StoredAsync(client, id)).Count);

        // An empty set is a deliberate clear: the branch prints the company's again.
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/catalog/branches/{id}", Branch(new()))).StatusCode);
        Assert.Empty(await StoredAsync(client, id));
    }

    [Fact]
    public async Task A_new_branch_has_its_cash_box_at_once_not_after_a_restart()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);

        var created = await client.PostAsJsonAsync("/api/catalog/branches", Branch(null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var boxes = await client.GetFromJsonAsync<JsonElement>($"/api/treasury/cash-boxes?branchId={id}");
        var own = boxes.EnumerateArray().Where(b => b.GetProperty("branchId").GetGuid() == id).ToList();
        Assert.Single(own);
        Assert.False(own[0].GetProperty("isMain").GetBoolean());
        Assert.Equal("Caisse Skikda", own[0].GetProperty("nameFr").GetString());
    }

    [Fact]
    public async Task A_branch_identifier_the_profile_does_not_define_is_refused_in_words()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);

        var refused = await client.PostAsJsonAsync("/api/catalog/branches", Branch(new() { ["siret"] = "123" }));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("siret", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_till_pulls_each_branch_s_identifiers_for_its_receipt()
    {
        // Owner, 2026-10-20: every paper printed at a branch carries that branch's identity — the
        // 80 mm receipt too, and a till prints it offline from its synced copy of the branches.
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var oran = await db.Branches.SingleAsync(b => b.Id == TillHarness.Oran);
        oran.FiscalIds = new Dictionary<string, string> { ["rc"] = "31/00-0729922B23" };
        await db.SaveWithVersionsAsync(default, oran);

        var pull = await TillHarness.NewService(db).PullAsync(0, "C02", CancellationToken.None);

        Assert.Equal(new Dictionary<string, string> { ["rc"] = "31/00-0729922B23" },
            pull.Branches.Single(b => b.Id == TillHarness.Oran).FiscalIds);
    }
}
