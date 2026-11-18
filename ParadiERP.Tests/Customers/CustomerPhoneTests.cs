using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadiERP.Application.Regional;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Customers;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Tests.Hosting;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Tests.Customers;

/// <summary>
/// "Phone must be saved as mobile format" and "make the country key required if we use messages
/// in the future" (owner, 2026-11-17): a customer's phone and its representative's are stored as a
/// mobile with the calling code of the company's country (+213 in the Algeria preset); anything
/// else is refused with a stable code. A phone saved before the rule is rewritten at start-up when
/// it reads as a mobile, kept as typed otherwise, and never a reason to refuse another change.
/// </summary>
[Collection(HostCollection.Name)]
public class CustomerPhoneTests
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

    private static object Person(string? phone, string fullName = "Karim Ben Ali") => new
    {
        code = (string?)null, fullName, phone, address = (string?)null, taxId = (string?)null,
        email = (string?)null, birthDate = (DateTime?)null, creditLimit = 0m, kind = "Person",
    };

    private static object Company(string? phone, string? representativePhone) => new
    {
        code = (string?)null, fullName = "SARL El Injaz Travaux", phone, address = (string?)null, taxId = (string?)null,
        email = (string?)null, birthDate = (DateTime?)null, creditLimit = 0m, kind = "Company",
        representative = new { fullName = "Nadia Bouzid", phone = representativePhone },
    };

    private static async Task<Guid> CreatedIdAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task AssertNotMobileAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(PhoneNumbers.NotMobile, body.RootElement.GetProperty("code").GetString());
        Assert.Equal(field, body.RootElement.GetProperty("field").GetString());
    }

    private static async Task<Customer> StoredAsync(PosHost host, Guid id)
    {
        await using var db = Db(host);
        return await db.Customers.AsNoTracking().SingleAsync(c => c.Id == id);
    }

    [Fact]
    public async Task A_mobile_typed_any_way_is_stored_with_the_country_code()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);

        var id = await CreatedIdAsync(await client.PostAsJsonAsync("/api/catalog/customers", Person("06.58.41.77.30")));
        Assert.Equal("+213658417730", (await StoredAsync(host, id)).Phone);

        var edited = await client.PutAsJsonAsync($"/api/catalog/customers/{id}", Person("0770 51 82 08"));
        Assert.True(edited.StatusCode == HttpStatusCode.OK, await edited.Content.ReadAsStringAsync());
        Assert.Equal("+213770518208", (await StoredAsync(host, id)).Phone);
    }

    [Fact]
    public async Task A_landline_is_refused_with_its_code_and_nothing_is_saved()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);

        await AssertNotMobileAsync(await client.PostAsJsonAsync("/api/catalog/customers", Person("021 23 45 67")), "phone");
        await using var db = Db(host);
        Assert.False(await db.Customers.AnyAsync(c => c.FullName == "Karim Ben Ali"));
    }

    [Fact]
    public async Task A_phone_from_before_the_rule_is_kept_when_another_field_changes_and_refused_when_changed()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var id = await CreatedIdAsync(await client.PostAsJsonAsync("/api/catalog/customers", Person(null)));
        await using (var db = Db(host))
        {
            var row = await db.Customers.SingleAsync(c => c.Id == id);
            row.Phone = "021 23 45 67";
            await db.SaveChangesAsync();
        }

        var renamed = await client.PutAsJsonAsync($"/api/catalog/customers/{id}", Person("021 23 45 67", "Karim Benali"));
        Assert.True(renamed.StatusCode == HttpStatusCode.OK, await renamed.Content.ReadAsStringAsync());
        var kept = await StoredAsync(host, id);
        Assert.Equal("Karim Benali", kept.FullName);
        Assert.Equal("021 23 45 67", kept.Phone);

        await AssertNotMobileAsync(await client.PutAsJsonAsync($"/api/catalog/customers/{id}", Person("021 23 45 68")), "phone");
        Assert.Equal("021 23 45 67", (await StoredAsync(host, id)).Phone);
    }

    [Fact]
    public async Task The_representatives_phone_follows_the_same_rule()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);

        await AssertNotMobileAsync(
            await client.PostAsJsonAsync("/api/catalog/customers", Company(null, "021 23 45 67")), "representativePhone");

        var id = await CreatedIdAsync(await client.PostAsJsonAsync("/api/catalog/customers", Company(null, "0661 00 00 00")));
        await using (var db = Db(host))
            Assert.Equal("+213661000000", (await db.CustomerRepresentatives.SingleAsync(r => r.CustomerId == id)).Phone);

        await AssertNotMobileAsync(await client.PostAsJsonAsync($"/api/catalog/customers/{id}/representatives",
            new { fullName = "Samir Haddad", phone = "023 92 45 10" }), "representativePhone");
        var changed = await client.PostAsJsonAsync($"/api/catalog/customers/{id}/representatives",
            new { fullName = "Samir Haddad", phone = "05.50.12.34.56" });
        Assert.True(changed.StatusCode == HttpStatusCode.OK, await changed.Content.ReadAsStringAsync());
        await using (var db = Db(host))
            Assert.Equal("+213550123456",
                (await db.CustomerRepresentatives.SingleAsync(r => r.CustomerId == id && r.ToUtc == null)).Phone);
    }

    private sealed class Algeria : IRegionalSettingsProvider
    {
        public RegionalSettings Get() => RegionalPresets.Create("DZ")!;
    }

    [Fact]
    public async Task The_startup_correction_rewrites_what_reads_as_a_mobile_and_keeps_the_rest()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var mobile = await CreatedIdAsync(await client.PostAsJsonAsync("/api/catalog/customers", Person(null, "Mobile")));
        var landline = await CreatedIdAsync(await client.PostAsJsonAsync("/api/catalog/customers", Person(null, "Landline")));
        var done = await CreatedIdAsync(await client.PostAsJsonAsync("/api/catalog/customers", Person("0555123456", "Done")));
        long versionBefore;
        await using (var db = Db(host))
        {
            (await db.Customers.SingleAsync(c => c.Id == mobile)).Phone = "06.58.41.77.30";
            (await db.Customers.SingleAsync(c => c.Id == landline)).Phone = "021 23 45 67";
            await db.SaveChangesAsync();
            versionBefore = (await db.Customers.SingleAsync(c => c.Id == mobile)).Version;
        }

        await using (var db = Db(host))
            Assert.Equal(1, await new CustomerPhoneCorrection(db, new Algeria()).ApplyAsync());

        var rewritten = await StoredAsync(host, mobile);
        Assert.Equal("+213658417730", rewritten.Phone);
        Assert.True(rewritten.Version > versionBefore, "the tills pull a rewritten customer");
        Assert.Equal("021 23 45 67", (await StoredAsync(host, landline)).Phone);
        Assert.Equal("+213555123456", (await StoredAsync(host, done)).Phone);

        // A second start finds nothing left that it can read.
        await using (var db = Db(host))
            Assert.Equal(0, await new CustomerPhoneCorrection(db, new Algeria()).ApplyAsync());
    }
}
