using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadisERP.Application.Customers;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Tests.Hosting;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Suppliers;

/// <summary>
/// A basic suppliers module (owner, 2026-10-10). Before it, receiving posted one lump to 401 and a
/// voucher named its payee as free text, so no supplier had a balance. Now a supplier has a card,
/// an opening balance under the customers' rules, deliveries and payments that move what the
/// company owes them, and a statement that is account 401 for them.
/// </summary>
[Collection(HostCollection.Name)]
public class SupplierTests
{
    private const string Password = "Essai@2026";

    private static async Task<HttpClient> SignedInAsync(PosHost host, string? username = null, string? password = null)
    {
        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = username is null ? PosHost.BootstrapUsername : host.SignInName(username),
            password = password ?? PosHost.BootstrapPassword,
            deviceCode = "C01",
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        return client;
    }

    private static PosDbContext Db(PosHost host) =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(host.ConnectionString).Options);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> CreateAsync(HttpClient client, string name, object? openingBalance = null) =>
        (await JsonAsync(await client.PostAsJsonAsync("/api/suppliers", new { name, phone = "0550 00 00 00", openingBalance }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static async Task<JsonElement> StatementAsync(HttpClient client, Guid id) =>
        await JsonAsync(await client.GetAsync(
            $"/api/suppliers/{id}/statement?from={DateTime.UtcNow.AddDays(-300):O}&to={DateTime.UtcNow.AddDays(2):O}"),
            HttpStatusCode.OK);

    private static async Task<decimal> BalanceAsync(PosHost host, Guid id)
    {
        await using var db = Db(host);
        return (await db.Suppliers.AsNoTracking().SingleAsync(s => s.Id == id)).Balance;
    }

    /// <summary>A product to receive: the test company is seeded with none.</summary>
    private static async Task<Guid> ProductAsync(PosHost host)
    {
        await using var db = Db(host);
        var product = new Product { Sku = "TUBE-48", NameFr = "Tube 48", Price = 1_000m, CostPrice = 500m };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<Guid> MainBranchAsync(HttpClient admin)
    {
        var branches = await admin.GetFromJsonAsync<JsonElement>("/api/catalog/branches");
        return branches.EnumerateArray().Single(b => b.GetProperty("isMain").GetBoolean() && !b.GetProperty("isWarehouse").GetBoolean())
            .GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task A_supplier_is_registered_with_a_drawn_code_and_what_the_company_owes_them()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);

        var created = await JsonAsync(await client.PostAsJsonAsync("/api/suppliers", new
        {
            name = "  SARL Echafaudages du Sahel ", address = "Zone industrielle, Blida",
            openingBalance = new { amount = 30_000m, direction = OpeningBalanceRules.WeOwe, on = (string?)null },
        }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();

        Assert.Equal("SARL Echafaudages du Sahel", created.GetProperty("name").GetString());
        Assert.StartsWith("FRN-", created.GetProperty("code").GetString());
        Assert.Equal(30_000m, created.GetProperty("balance").GetDecimal());
        Assert.Equal(30_000m, created.GetProperty("openingBalance").GetDecimal());

        await using var db = Db(host);
        var accounts = await db.LedgerAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Code);
        var entry = await db.LedgerEntries.AsNoTracking().Include(e => e.Lines)
            .SingleAsync(e => e.SourceType == LedgerSources.SupplierOpening && e.SourceId == id);
        var payable = Assert.Single(entry.Lines, l => accounts[l.AccountId] == LedgerAccounts.Suppliers);
        Assert.Equal(30_000m, payable.Credit);
        Assert.Equal(id, payable.SupplierId);
        Assert.Equal(30_000m, Assert.Single(entry.Lines, l => accounts[l.AccountId] == LedgerAccounts.OpeningBalance).Debit);

        var statement = await StatementAsync(client, id);
        Assert.Equal(30_000m, statement.GetProperty("closingBalance").GetDecimal());
        Assert.Equal(0m, statement.GetProperty("balanceDifference").GetDecimal());
    }

    [Fact]
    public async Task A_delivery_adds_to_what_is_owed_and_a_supplier_payment_takes_it_off_both_on_the_statement()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);
        var supplier = await CreateAsync(client, "Acier Plus");
        var product = await ProductAsync(host);

        var received = await client.PostAsJsonAsync("/api/catalog/stock/receiving", new
        {
            lines = new[] { new { productId = product, qty = 10m, unitCost = 500m, assetUnitValue = (decimal?)null } },
            reference = "BL-7781", note = (string?)null, supplierId = supplier,
        });
        Assert.True(received.IsSuccessStatusCode, await received.Content.ReadAsStringAsync());
        Assert.Equal(5_000m, await BalanceAsync(host, supplier));

        var raised = await JsonAsync(await client.PostAsJsonAsync("/api/payment-vouchers", new
        {
            branchId = await MainBranchAsync(client), kind = (int)PaymentVoucherKind.SupplierPayment, amount = 3_000m,
            methodKey = "cash", supplierId = supplier,
        }), HttpStatusCode.Created);
        var voucher = raised.TryGetProperty("voucher", out var v) ? v : raised;
        var voucherId = voucher.GetProperty("id").GetGuid();
        Assert.Equal("Acier Plus", voucher.GetProperty("payeeName").GetString());
        Assert.Equal(supplier, voucher.GetProperty("supplierId").GetGuid());
        // Raised moves nothing; paid does.
        Assert.Equal(5_000m, await BalanceAsync(host, supplier));

        Assert.True((await client.PostAsJsonAsync($"/api/payment-vouchers/{voucherId}/approve", new { note = (string?)null })).IsSuccessStatusCode);
        var paid = await client.PostAsJsonAsync($"/api/payment-vouchers/{voucherId}/pay", new { note = (string?)null });
        Assert.True(paid.IsSuccessStatusCode, await paid.Content.ReadAsStringAsync());
        Assert.Equal(2_000m, await BalanceAsync(host, supplier));

        var statement = await StatementAsync(client, supplier);
        Assert.Equal(2_000m, statement.GetProperty("closingBalance").GetDecimal());
        Assert.Equal(0m, statement.GetProperty("balanceDifference").GetDecimal());
        var rows = statement.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(["BL-7781", voucher.GetProperty("number").GetString()],
            rows.Select(r => r.GetProperty("documentNumber").GetString()));
    }

    [Fact]
    public async Task A_delivery_naming_no_supplier_posts_as_before_and_an_unknown_one_is_refused()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);
        var product = await ProductAsync(host);
        var lines = new[] { new { productId = product, qty = 1m, unitCost = 100m, assetUnitValue = (decimal?)null } };

        Assert.True((await client.PostAsJsonAsync("/api/catalog/stock/receiving", new { lines, reference = "X", note = (string?)null }))
            .IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/catalog/stock/receiving",
            new { lines, reference = "Y", note = (string?)null, supplierId = Guid.NewGuid() })).StatusCode);
    }

    [Fact]
    public async Task The_opening_balance_follows_the_customers_rules_once_then_corrections_with_a_reason()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host);
        var id = await CreateAsync(admin, "Bois & Coffrage");

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/suppliers/{id}/opening-corrections",
            new { amount = 10m, direction = OpeningBalanceRules.OwesUs, reason = "x" })).StatusCode);

        var set = await JsonAsync(await admin.PostAsJsonAsync($"/api/suppliers/{id}/opening-balance",
            new { amount = 12_000m, direction = OpeningBalanceRules.WeOwe }), HttpStatusCode.OK);
        Assert.Equal(12_000m, set.GetProperty("balance").GetDecimal());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/suppliers/{id}/opening-balance",
            new { amount = 1m, direction = OpeningBalanceRules.WeOwe })).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/suppliers/{id}/opening-corrections",
            new { amount = 2_000m, direction = OpeningBalanceRules.OwesUs, reason = " " })).StatusCode);
        var correction = await JsonAsync(await admin.PostAsJsonAsync($"/api/suppliers/{id}/opening-corrections",
            new { amount = 2_000m, direction = OpeningBalanceRules.OwesUs, reason = "Avoir fournisseur oublié" }), HttpStatusCode.Created);
        Assert.Equal(-2_000m, correction.GetProperty("amount").GetDecimal());
        Assert.StartsWith("SOC-", correction.GetProperty("number").GetString());
        Assert.Equal(10_000m, await BalanceAsync(host, id));

        await TestUsers.CreateAsync(host, admin, "gerant", UserRole.Manager.ToString(), await MainBranchAsync(admin), Password);
        using var manager = await SignedInAsync(host, "gerant", Password);
        var other = await CreateAsync(manager, "Peinture Est");   // a manager keeps the list…
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync($"/api/suppliers/{other}/opening-balance",
            new { amount = 1m, direction = OpeningBalanceRules.WeOwe })).StatusCode);   // …but not this
    }

    [Fact]
    public async Task An_edit_never_touches_the_balance_or_the_code_and_a_delete_only_hides_it()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);
        var id = await CreateAsync(client, "Quincaillerie", new { amount = 500m, direction = OpeningBalanceRules.WeOwe, on = (string?)null });

        var edited = await JsonAsync(await client.PutAsJsonAsync($"/api/suppliers/{id}", new
        {
            name = "Quincaillerie Centrale", openingBalance = new { amount = 99_999m, direction = OpeningBalanceRules.WeOwe },
        }), HttpStatusCode.OK);
        Assert.Equal("Quincaillerie Centrale", edited.GetProperty("name").GetString());
        Assert.Equal(500m, edited.GetProperty("balance").GetDecimal());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/suppliers/{id}", new { name = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/suppliers/{id}")).StatusCode);
        var list = await client.GetFromJsonAsync<JsonElement>("/api/suppliers");
        Assert.DoesNotContain(list.EnumerateArray(), s => s.GetProperty("id").GetGuid() == id);
        await using var db = Db(host);
        Assert.True((await db.Suppliers.AsNoTracking().SingleAsync(s => s.Id == id)).IsDeleted);
    }
}
