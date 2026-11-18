using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Tests.Hosting;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Customers;

/// <summary>
/// "I need to paginate in backend, not frontend" (owner, 2026-11-17): the customers list is asked a
/// page at a time — ten rows to begin with, 10/20/50/100 — and its search and filters run on the
/// server over every customer, never over the page on screen.
/// </summary>
[Collection(HostCollection.Name)]
public class CustomerPageTests
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

    private static PosDbContext Db(PosHost host) =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(host.ConnectionString).Options);

    /// <summary>25 people named P01…P25, a few with balances, points and phones, and two companies.</summary>
    private static async Task SeedAsync(PosHost host)
    {
        await using var db = Db(host);
        db.Customers.RemoveRange(db.Customers);
        for (var i = 1; i <= 25; i++)
        {
            db.Customers.Add(new Customer
            {
                Code = $"Cust-T-{i:00}", FullName = $"P{i:00}", Kind = CustomerKind.Person,
                Balance = i switch { 1 => 500m, 2 => -20m, 3 => 2000m, _ => 0m },
                CreditLimit = i == 3 ? 1000m : 0m,
                LoyaltyPoints = i <= 4 ? 10 : 0,
                Phone = i switch { 5 => "+213658417730", 6 => "021 23 45 67", 7 => null, _ => $"+2135500000{i:00}" },
                Email = i == 8 ? "p08@mail.dz" : null,
            });
        }
        db.Customers.Add(new Customer { Code = "Comp-T-01", FullName = "Z Company without a representative", Kind = CustomerKind.Company });
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> PageAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/catalog/customers/page{query}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string[] Names(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("fullName").GetString()!).ToArray();

    [Fact]
    public async Task Ten_rows_to_begin_with_and_the_total_of_every_row()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        await SeedAsync(host);

        var first = await PageAsync(client, "");
        Assert.Equal(26, first.GetProperty("total").GetInt32());
        Assert.Equal(10, first.GetProperty("take").GetInt32());
        Assert.Equal(["P01", "P02", "P03", "P04", "P05", "P06", "P07", "P08", "P09", "P10"], Names(first));

        var third = await PageAsync(client, "?skip=20&take=10");
        Assert.Equal(["P21", "P22", "P23", "P24", "P25", "Z Company without a representative"], Names(third));

        // A page past the end, after a filter shrank the list, is answered with the last page.
        var past = await PageAsync(client, "?skip=90&take=20");
        Assert.Equal(20, past.GetProperty("skip").GetInt32());
        Assert.Equal(6, past.GetProperty("items").GetArrayLength());

        // Never more than a hundred rows at once.
        Assert.Equal(100, (await PageAsync(client, "?take=5000")).GetProperty("take").GetInt32());
    }

    [Fact]
    public async Task The_filters_run_on_the_server_over_every_customer()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        await SeedAsync(host);

        Assert.Equal(["P01", "P03"], Names(await PageAsync(client, "?balance=owes")));
        Assert.Equal(["P03"], Names(await PageAsync(client, "?balance=overLimit")));
        Assert.Equal(["P02"], Names(await PageAsync(client, "?balance=inCredit")));
        Assert.Equal(4, (await PageAsync(client, "?points=has")).GetProperty("total").GetInt32());
        Assert.Equal(["Z Company without a representative"], Names(await PageAsync(client, "?kind=Company")));
        Assert.Equal(["Z Company without a representative"], Names(await PageAsync(client, "?missing=representative")));
        Assert.Contains("P07", Names(await PageAsync(client, "?missing=phone")));
        Assert.Equal(25, (await PageAsync(client, "?missing=email")).GetProperty("total").GetInt32());
        Assert.Equal(["P06"], Names(await PageAsync(client, "?missing=phoneToFix")));
        // Filters combine.
        Assert.Equal(["P01", "P03"], Names(await PageAsync(client, "?balance=owes&points=has")));
    }

    [Fact]
    public async Task The_search_finds_a_phone_however_it_is_typed()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        await SeedAsync(host);

        Assert.Equal(["P05"], Names(await PageAsync(client, "?q=0658417730")));
        Assert.Equal(["P05"], Names(await PageAsync(client, "?q=" + Uri.EscapeDataString("06.58.41.77"))));
        Assert.Equal(["P05"], Names(await PageAsync(client, "?q=213658")));
        Assert.Equal(["P12"], Names(await PageAsync(client, "?q=P12")));
        Assert.Equal(["P08"], Names(await PageAsync(client, "?q=p08@mail")));
    }

    [Fact]
    public async Task One_customer_by_id_for_the_open_editor()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        await SeedAsync(host);
        Guid id;
        await using (var db = Db(host)) id = (await db.Customers.SingleAsync(c => c.FullName == "P17")).Id;

        var page = await PageAsync(client, $"?id={id}&take=1");
        Assert.Equal(["P17"], Names(page));
    }
}
