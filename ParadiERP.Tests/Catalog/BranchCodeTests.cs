using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadiERP.Application.Catalog;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Tests.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ParadiERP.Tests.Catalog;

/// <summary>
/// Every location's two-digit code (owner, 2026-10-05: "give every branch a code of 2 digits, not
/// starting with zero"): 10–99, given by the server, one past the highest ever given, fixed for life.
/// </summary>
public class BranchCodeRuleTests
{
    [Fact]
    public void Codes_are_three_digits_unique_and_exhaust_only_after_all_900_are_used()
    {
        var used = new HashSet<string>();
        for (var i = 0; i < 900; i++)
        {
            var code = BranchCodes.Next(used);
            Assert.True(BranchCodes.IsWellFormed(code));
            Assert.True(used.Add(code!));
        }
        Assert.Null(BranchCodes.Next(used));
        used.Remove("785");
        Assert.Equal("785", BranchCodes.Next(used));
    }

    [Theory]
    [InlineData("100", true)]
    [InlineData("999", true)]
    [InlineData("361", true)]
    [InlineData("09", false)]
    [InlineData("1", false)]
    [InlineData("10", false)]
    [InlineData("099", false)]
    [InlineData("1000", false)]
    [InlineData("1x", false)]
    [InlineData(null, false)]
    public void A_code_is_two_digits_and_never_starts_with_zero(string? code, bool wellFormed) =>
        Assert.Equal(wellFormed, BranchCodes.IsWellFormed(code));
}

/// <summary>The BranchCodes migration, run on a SQLite database that already holds locations.</summary>
public class BranchCodesMigrationTests
{
    /// <summary>The release before this one.</summary>
    private const string Before = "EmployeeEmail";

    [Fact]
    public async Task Existing_locations_are_numbered_from_10_in_creation_order_and_a_new_one_takes_the_next()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await db.Database.GetService<IMigrator>().MigrateAsync(Before);

        // Written in the old shape, in an order that is not the expected one, so the test sees the
        // migration choose: main branch, main warehouse, the other live ones by French name, deleted last.
        var (oran, warehouse, main, gone, blida) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var now = DateTime.UtcNow;
        foreach (var (id, name, isMain, isWarehouse, isDeleted) in new[]
                 {
                     (oran, "Oran", 0, 0, 0), (warehouse, "Entrepôt central", 1, 1, 0), (main, "Alger-Centre", 1, 0, 0),
                     (gone, "Ancien dépôt", 0, 1, 1), (blida, "Blida", 0, 0, 0),
                 })
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Branches (Id, Version, UpdatedAtUtc, IsDeleted, NameAr, NameFr, NameEn, Address, AddressAr, AddressFr, AddressEn, Phone, IsMain, IsWarehouse, AllowNegativeStock, IsActive, IsOpenForOperation, FiscalIds) " +
                "VALUES ({0}, 1, {1}, {2}, '', {3}, '', '', '', '', '', '', {4}, {5}, 0, 1, 1, '{{}}')",
                id, now, isDeleted, name, isMain, isWarehouse);

        // The upgrade itself. A statement that is not valid SQL on this provider stops here.
        await db.Database.GetService<IMigrator>().MigrateAsync("BranchCodes");

        var codes = await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.Code);
        Assert.Equal("10", codes[main]);
        Assert.Equal("11", codes[warehouse]);
        Assert.Equal("12", codes[blida]);
        Assert.Equal("13", codes[oran]);
        Assert.Equal("14", codes[gone]);

        // The next location saved takes 15 — past the deleted one's, which is never handed out again.
        var made = new Branch { NameFr = "Annaba", IsActive = true };
        db.Branches.Add(made);
        await db.SaveChangesAsync();
        Assert.True(BranchCodes.IsWellFormed(made.Code));

        // And the database itself refuses a second location with a code already given.
        db.ChangeTracker.Clear();
        db.Branches.Add(new Branch { NameFr = "Doublon", Code = "12" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}

/// <summary>The codes through the real HTTP pipeline.</summary>
[Collection(HostCollection.Name)]
public class BranchCodeEndpointTests
{
    private static async Task<HttpClient> AdminAsync(PosHost host)
    {
        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { username = PosHost.BootstrapUsername, password = PosHost.BootstrapPassword, deviceCode = "C01" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.RootElement.GetProperty("token").GetString());
        return client;
    }

    private static PosDbContext Db(PosHost host) =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(host.ConnectionString).Options);

    private static object Location(string name, bool warehouse = false, string? code = null) => new
    {
        nameAr = name, nameFr = name, nameEn = name, address = "", phone = "", code,
        isMain = false, isWarehouse = warehouse, allowNegativeStock = false, isActive = true,
    };

    private static async Task<JsonElement> CreatedAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Every_location_is_given_the_next_code_once_and_keeps_it()
    {
        using var host = new PosHost();
        using var admin = await AdminAsync(host);

        // A new company's two places, numbered when it was first started.
        var rows = (await admin.GetFromJsonAsync<JsonElement>("/api/catalog/branches")).EnumerateArray().ToList();
        Assert.All(rows, b => Assert.True(BranchCodes.IsWellFormed(b.GetProperty("code").GetString())));

        // A code in the body is ignored, on create and on edit.
        var oran = await CreatedAsync(await admin.PostAsJsonAsync("/api/catalog/branches", Location("Oran", code: "42")));
        Assert.True(BranchCodes.IsWellFormed(oran.GetProperty("code").GetString()));
        var depot = await CreatedAsync(await admin.PostAsJsonAsync("/api/catalog/branches", Location("Dépôt Skikda", warehouse: true)));
        Assert.True(BranchCodes.IsWellFormed(depot.GetProperty("code").GetString()));

        var edited = await admin.PutAsJsonAsync($"/api/catalog/branches/{oran.GetProperty("id").GetGuid()}", Location("Oran-Est", code: "77"));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(oran.GetProperty("code").GetString(), (await edited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // A row deleted before branches stopped being deletable (2026-10-06) keeps its code taken.
        await using (var legacy = Db(host))
        {
            var depotId = depot.GetProperty("id").GetGuid();
            (await legacy.Branches.SingleAsync(b => b.Id == depotId)).IsDeleted = true;
            await legacy.SaveChangesAsync();
        }
        var blida = await CreatedAsync(await admin.PostAsJsonAsync("/api/catalog/branches", Location("Blida")));
        Assert.True(BranchCodes.IsWellFormed(blida.GetProperty("code").GetString()));

        await using var db = Db(host);
        var codes = await db.Branches.AsNoTracking().Select(b => b.Code).ToListAsync();
        Assert.Equal(5, codes.Distinct().Count());
    }

    [Fact]
    public async Task The_901st_location_is_refused_with_a_code_and_nothing_is_saved()
    {
        using var host = new PosHost();
        using var admin = await AdminAsync(host);
        await using (var db = Db(host))
        {
            var used = await db.Branches.Select(b => b.Code).ToListAsync();
            foreach (var code in Enumerable.Range(100, 900).Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)).Except(used))
                db.Branches.Add(new Branch { NameFr = "Occupied " + code, Code = code, IsActive = true });
            await db.SaveChangesAsync();
        }

        var refused = await admin.PostAsJsonAsync("/api/catalog/branches", Location("Une de trop"));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal(BranchCodes.ExhaustedCode, body.RootElement.GetProperty("code").GetString());
        Assert.Equal(BranchCodes.ExhaustedMessage, body.RootElement.GetProperty("detail").GetString());

        await using var after = Db(host);
        Assert.False(await after.Branches.AnyAsync(b => b.NameFr == "Une de trop"));
    }
}
