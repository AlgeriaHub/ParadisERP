using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadiERP.Domain;
using ParadiERP.Tests.Hosting;

namespace ParadiERP.Tests.Stock;

/// <summary>
/// A stock movement's reason is thirty-two characters, and the two providers disagree about
/// what happens when it is longer: MySQL refuses the insert, SQLite stores it. So production
/// answered 500 on an ordinary stocktake comment while dev, CI and every test here passed.
///
/// <para>These pin both halves of the answer: where the caller's words <i>are</i> the label the
/// endpoint says 400 and names the limit, and where they decorate a prefix the system chose the
/// label is cut so the prefix — the part a stock history is read by — always survives.</para>
/// </summary>
public class StockReasonTests
{
    [Fact]
    public void A_label_keeps_its_prefix_when_the_operator_s_words_do_not_fit()
    {
        var label = StockMovement.Label("receiving", "BL-2026-0041 from Ciments de l'Ouest");

        Assert.Equal(StockMovement.ReasonMaxLength, label.Length);
        Assert.StartsWith("receiving: BL-2026-0041", label);
    }

    [Fact]
    public void A_label_with_nothing_added_is_just_the_prefix()
    {
        Assert.Equal("transfer", StockMovement.Label("transfer", null));
        Assert.Equal("transfer", StockMovement.Label("transfer", "   "));
        Assert.Equal("transfer: Oran", StockMovement.Label("transfer", "  Oran  "));
    }
}

/// <summary>
/// The same rule over HTTP, because the guard is in the endpoint and nowhere else. It runs on
/// SQLite, which would have accepted the over-long value — which is exactly the point: the
/// refusal has to come from the code, not from the column.
/// </summary>
[Collection(HostCollection.Name)]
public class StockAdjustReasonEndpointTests
{
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

    [Fact]
    public async Task An_over_long_reason_is_refused_with_the_limit_in_words_rather_than_a_database_error()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);

        var branches = await client.GetFromJsonAsync<JsonElement>("/api/catalog/branches");
        var branchId = branches[0].GetProperty("id").GetGuid();

        var created = await client.PostAsJsonAsync("/api/catalog/products", new
        {
            sku = "TUBE-50", barcode = (string?)null, nameAr = "أنبوب", nameFr = "Tube 50", nameEn = "Tube 50",
            categoryId = (Guid?)null, price = 2000m, costPrice = 1200m, taxRate = 19m, unit = "u",
            trackStock = true, isActive = true,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var productId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Thirty-three characters: an ordinary stocktake comment, one past the column.
        var tooLong = new string('c', StockMovement.ReasonMaxLength + 1);
        var refused = await client.PostAsJsonAsync($"/api/catalog/products/{productId}/stock", new
        {
            branchId, qtyDelta = 3m, reason = tooLong, reorderLevel = (decimal?)null,
        });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var message = await refused.Content.ReadAsStringAsync();
        Assert.Contains(StockMovement.ReasonMaxLength.ToString(), message);

        // And the longest one that does fit still goes through, so the boundary is where it says.
        var accepted = await client.PostAsJsonAsync($"/api/catalog/products/{productId}/stock", new
        {
            branchId, qtyDelta = 3m, reason = new string('c', StockMovement.ReasonMaxLength), reorderLevel = (decimal?)null,
        });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }
}
