using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadisERP.Tests.Hosting;

namespace ParadisERP.Tests.Catalog;

/// <summary>
/// A branch's stock sheet, read and loaded through the real routes: the preview writes nothing,
/// the import creates what the catalogue lacks and sets — never adds — the branch's quantities.
/// </summary>
[Collection(HostCollection.Name)]
public class OpeningImportEndpointTests
{
    private const string Preview = "/api/catalog/stock/opening/import/preview";
    private const string Import = "/api/catalog/stock/opening/import";

    [Fact]
    public async Task The_preview_reads_the_sheet_matches_what_exists_and_writes_nothing()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);
        var tube = await CreateProductAsync(client, "TUBE-6", "تيب 6.00 م");
        var before = await ProductCountAsync(client);

        var preview = await PreviewAsync(client, TestWorkbook.StockSheet(
            ("تيب 6,00 م", 18, 0), ("ارجل جالفا", 972, 1897), ("ارجل  جالفا", 1, 0)));

        Assert.Equal("29-09", preview.GetProperty("sheetName").GetString());
        Assert.Equal("J", preview.GetProperty("nameColumn").GetString());
        Assert.Equal(11, preview.GetProperty("quantityColumn").GetInt32());
        var rows = preview.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(3, rows.Count);

        // Another spelling of a product already in the catalogue is that product.
        Assert.Equal("existing", rows[0].GetProperty("match").GetString());
        Assert.Equal(tube, rows[0].GetProperty("productId").GetGuid());
        Assert.Equal("TUBE-6", rows[0].GetProperty("sku").GetString());
        Assert.Equal("new", rows[1].GetProperty("match").GetString());
        // The second spelling of the same article on one sheet is pointed at, not merged.
        Assert.Equal(7, rows[2].GetProperty("duplicateOfRow").GetInt32());

        Assert.Equal(before, await ProductCountAsync(client));
    }

    [Fact]
    public async Task The_preview_refuses_a_file_that_is_not_a_workbook_in_words()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);

        var response = await client.PostAsync(Preview, Form("stock.xls", [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(".xlsx", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_import_creates_the_missing_articles_and_sets_the_branch_s_count()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);
        var branch = await CreateBranchAsync(client, "أولاد موسى");
        var tube = await CreateProductAsync(client, "TUBE-6", "تيب 6.00 م");

        var result = await ImportAsync(client, branch,
            [new { name = "تيب 6,00 م", qty = 18m }, new { name = "ارجل جالفا", qty = 972m }, new { name = "بوطو 0.30* 0.30 *3.00 م", qty = 14.5m }]);

        Assert.Equal(3, result.GetProperty("lines").GetInt32());
        Assert.Equal(1, result.GetProperty("matched").GetInt32());
        Assert.Equal(2, result.GetProperty("created").GetInt32());
        var created = result.GetProperty("createdProducts").EnumerateArray().ToList();
        Assert.Equal(["ART-0001", "ART-0002"], created.Select(c => c.GetProperty("sku").GetString()));

        var legs = created[0].GetProperty("id").GetGuid();
        var product = await client.GetFromJsonAsync<JsonElement>($"/api/catalog/products?q=ART-0001");
        var stored = product.EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == legs);
        Assert.Equal("ارجل جالفا", stored.GetProperty("nameAr").GetString());
        // Only what the sheet says: no invented French or English name.
        Assert.Equal("", stored.GetProperty("nameFr").GetString());
        Assert.True(stored.GetProperty("isRentable").GetBoolean());

        Assert.Equal(972m, await AvailableAsync(client, legs, branch));
        Assert.Equal(18m, await AvailableAsync(client, tube, branch));
        Assert.Equal(14.5m, await AvailableAsync(client, created[1].GetProperty("id").GetGuid(), branch));
    }

    [Fact]
    public async Task Loading_the_sheet_again_restates_the_count_instead_of_adding_to_it()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);
        var branch = await CreateBranchAsync(client, "أولاد موسى");

        var first = await ImportAsync(client, branch, [new { name = "كرو", qty = 2443m }]);
        var second = await ImportAsync(client, branch, [new { name = "كرو ", qty = 2400m }]);

        var id = first.GetProperty("createdProducts")[0].GetProperty("id").GetGuid();
        Assert.Equal(0, second.GetProperty("created").GetInt32());
        Assert.Equal(2400m, await AvailableAsync(client, id, branch));
    }

    [Fact]
    public async Task A_negative_count_or_the_same_article_twice_is_refused_and_nothing_is_written()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);
        var branch = await CreateBranchAsync(client, "أولاد موسى");
        var before = await ProductCountAsync(client);

        var negative = await client.PostAsJsonAsync(Import, Request(branch,
            [new { name = "ارجل جالفا", qty = 972m }, new { name = "كولي ثابت", qty = -192m }]));
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        Assert.Contains("كولي ثابت", await negative.Content.ReadAsStringAsync());

        var twice = await client.PostAsJsonAsync(Import, Request(branch,
            [new { name = "بانو 0,24*1,00 م", qty = 1m }, new { name = "بانو 0.24* 1.00 م", qty = 2m }]));
        Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);
        Assert.Contains("same article", await twice.Content.ReadAsStringAsync());

        Assert.Equal(before, await ProductCountAsync(client));
    }

    [Fact]
    public async Task A_branch_already_trading_takes_no_opening_balance_from_a_sheet_either()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);
        var branch = await CreateBranchAsync(client, "سكيكدة");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/catalog/stock/branches/{branch}/open", new { })).StatusCode);

        var refused = await client.PostAsJsonAsync(Import, Request(branch, [new { name = "كرو", qty = 1m }]));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("already open for operation", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task New_codes_continue_after_the_highest_one_with_the_prefix()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);
        var branch = await CreateBranchAsync(client, "وهران");
        await CreateProductAsync(client, "OM-0041", "سلم المونيوم 3.00 م");

        var result = await ImportAsync(client, branch, [new { name = "عجلات", qty = 12m }], skuPrefix: "OM-");

        Assert.Equal("OM-0042", result.GetProperty("createdProducts")[0].GetProperty("sku").GetString());

        var badPrefix = await client.PostAsJsonAsync(Import, Request(branch, [new { name = "كليبس", qty = 0m }], "أصناف/"));
        Assert.Equal(HttpStatusCode.BadRequest, badPrefix.StatusCode);
    }

    // ---------- plumbing ----------

    private static object Request(Guid branch, object[] lines, string? skuPrefix = null) =>
        new { branchId = branch, lines, note = "mosa.xlsx", newProductsRentable = true, skuPrefix };

    private static async Task<JsonElement> ImportAsync(HttpClient client, Guid branch, object[] lines, string? skuPrefix = null)
    {
        var response = await client.PostAsJsonAsync(Import, Request(branch, lines, skuPrefix));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> PreviewAsync(HttpClient client, byte[] workbook)
    {
        var response = await client.PostAsync(Preview, Form("mosa.xlsx", workbook));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static MultipartFormDataContent Form(string fileName, byte[] bytes)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        return new MultipartFormDataContent { { file, "file", fileName } };
    }

    private static async Task<decimal> AvailableAsync(HttpClient client, Guid product, Guid branch)
    {
        var rows = await client.GetFromJsonAsync<JsonElement>($"/api/catalog/stock/availability/{product}");
        return rows.EnumerateArray().Single(r => r.GetProperty("branchId").GetGuid() == branch).GetProperty("available").GetDecimal();
    }

    private static async Task<int> ProductCountAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/catalog/products")).GetArrayLength();

    private static async Task<Guid> CreateBranchAsync(HttpClient client, string nameAr)
    {
        var created = await client.PostAsJsonAsync("/api/catalog/branches", new
        {
            nameAr, nameFr = "", nameEn = "", address = "", phone = "", isMain = false, allowNegativeStock = false, isActive = true,
        });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateProductAsync(HttpClient client, string sku, string nameAr)
    {
        var created = await client.PostAsJsonAsync("/api/catalog/products", new
        {
            sku, barcode = (string?)null, nameAr, nameFr = "", nameEn = "", categoryId = (Guid?)null,
            price = 0m, costPrice = 0m, taxRate = 19m, unit = "pcs", trackStock = true, isActive = true, isRentable = true,
        });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

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
}
