using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadisERP.API.Endpoints;
using ParadisERP.Application.Customers;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Tests.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ParadisERP.Tests.Customers;

/// <summary>
/// A person's name in two parts (owner, 2026-10-06: "الاسم يكون له 2 مدخل، واحد للاسم والثاني
/// اللقب"), first name first — the employee form's rule. The full name every list, receipt and
/// contract reads is composed from the parts on every save; a company keeps its one legal name; an
/// older client that sends a person's name whole still saves, filed as the family name the way the
/// migration filed everyone already on file.
/// </summary>
[Collection(HostCollection.Name)]
public class CustomerNamePartsTests
{
    // ---------- the rule ----------

    [Fact]
    public void A_persons_full_name_is_the_first_name_then_the_family_name()
    {
        var names = CustomerRules.Names(CustomerKind.Person, "whatever the screen sent", "  Karim ", " Haddad  ");
        Assert.Equal(("Karim", "Haddad", "Karim Haddad"), names);
    }

    [Theory]
    [InlineData("Karim", null)]
    [InlineData("Karim", "   ")]
    [InlineData(null, "Haddad")]
    [InlineData("", "Haddad")]
    public void A_person_sent_in_parts_needs_both(string? first, string? family) =>
        Assert.Null(CustomerRules.Names(CustomerKind.Person, "Karim Haddad", first, family));

    [Fact]
    public void A_person_sent_whole_is_filed_as_the_family_name()
    {
        Assert.Equal((null, "Karim Haddad", "Karim Haddad"),
            CustomerRules.Names(CustomerKind.Person, " Karim Haddad ", null, null));
    }

    [Theory]
    [InlineData(CustomerKind.Company)]
    [InlineData(CustomerKind.Unspecified)]
    public void A_company_or_an_undecided_row_has_one_name_and_no_parts(CustomerKind kind)
    {
        // Parts left over from a form that was a person a moment ago are not the name.
        Assert.Equal((null, null, "SARL El Injaz"),
            CustomerRules.Names(kind, " SARL El Injaz ", "Karim", "Haddad"));
    }

    // ---------- through the API ----------

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

    private static object Person(string? firstName, string? familyName, string fullName = "") => new
    {
        fullName, firstName, familyName, phone = (string?)null, address = (string?)null, taxId = (string?)null,
        email = (string?)null, birthDate = (DateTime?)null, creditLimit = 0m, kind = "Person",
    };

    [Fact]
    public async Task A_person_is_saved_in_parts_and_an_edit_recomposes_the_full_name()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);

        var created = await client.PostAsJsonAsync("/api/catalog/customers", Person(" Karim ", " Haddad ", "ignored"));
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var row = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Karim Haddad", row.GetProperty("fullName").GetString());
        Assert.Equal("Karim", row.GetProperty("firstName").GetString());
        Assert.Equal("Haddad", row.GetProperty("familyName").GetString());
        var id = row.GetProperty("id").GetGuid();

        var edited = await client.PutAsJsonAsync($"/api/catalog/customers/{id}", Person("Karim", "Haddad-Benali"));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(host.ConnectionString).Options);
        var stored = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == id);
        Assert.Equal(("Karim", "Haddad-Benali", "Karim Haddad-Benali"), (stored.FirstName, stored.FamilyName, stored.FullName));
    }

    [Fact]
    public async Task A_person_with_a_blank_part_is_refused_with_a_code_and_nothing_is_saved()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);

        var refused = await client.PostAsJsonAsync("/api/catalog/customers", Person("Karim", "  ", "Karim"));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal(CustomerRules.PersonNameRequired, body.RootElement.GetProperty("code").GetString());
        Assert.Equal(CatalogEndpoints.PersonNameRequiredMessage, body.RootElement.GetProperty("detail").GetString());
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(host.ConnectionString).Options);
        Assert.False(await db.Customers.AnyAsync());
    }

    // ---------- the upgrade ----------

    /// <summary>The release before this one.</summary>
    private const string Before = "EmployeeNameParts";

    [Fact]
    public async Task People_on_file_keep_their_whole_name_as_the_family_name_and_companies_get_no_parts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);

        await db.Database.GetService<IMigrator>().MigrateAsync(Before);

        // Rows in the old shape, with raw SQL; parameters, never formatted text.
        var person = Guid.NewGuid();
        var company = Guid.NewGuid();
        var now = DateTime.UtcNow;
        const string insert =
            "INSERT INTO Customers (Id, Version, UpdatedAtUtc, IsDeleted, Code, FullName, CreditLimit, Balance, LoyaltyPoints, Kind) " +
            "VALUES ({0}, 1, {1}, 0, {2}, {3}, {4}, {4}, 0, {5})";
        await db.Database.ExecuteSqlRawAsync(insert, person, now, "C001", "Karim Haddad", 0m, (int)CustomerKind.Person);
        await db.Database.ExecuteSqlRawAsync(insert, company, now, "C002", "SARL El Injaz", 0m, (int)CustomerKind.Company);

        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        var p = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == person);
        Assert.Equal((null, "Karim Haddad", "Karim Haddad"), (p.FirstName, p.FamilyName, p.FullName));
        var c = await db.Customers.AsNoTracking().SingleAsync(x => x.Id == company);
        Assert.Equal((null, null, "SARL El Injaz"), (c.FirstName, c.FamilyName, c.FullName));
    }
}
