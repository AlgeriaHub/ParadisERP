using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AlgeriaPos.Application.Catalog;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Catalog;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Tests.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlgeriaPos.Tests.Customers;

/// <summary>
/// The code shape on its own (owner, 2026-10-05): prefix + branch code + the company's YYMM + the
/// sequence of that branch and month — Cust-7852610001 for the first person registered at branch 785
/// in October 2026, Comp-7852610001 for the first company there, Emp-7852610001 for the first employee,
/// Cust-7852611001 for the first person of November.
/// </summary>
public class CodeSeriesTests
{
    private static readonly TimeZoneInfo Algiers =
        TimeZoneInfo.CreateCustomTimeZone("test/Algiers", TimeSpan.FromHours(1), "Algiers", "Algiers");
    private static readonly TimeZoneInfo West =
        TimeZoneInfo.CreateCustomTimeZone("test/West", TimeSpan.FromHours(-3), "West", "West");

    [Fact]
    public void The_code_is_prefix_branch_month_and_three_digits_that_grow_past_999()
    {
        var stem = CodeSeries.Stem(CodeSeries.PersonPrefix, "785", "2610");
        Assert.Equal("Cust-7852610", stem);
        Assert.Equal("Cust-7852610001", CodeSeries.Format(stem, 1));
        Assert.Equal("Cust-7852610002", CodeSeries.Format(stem, 2));
        Assert.Equal("Cust-7852610999", CodeSeries.Format(stem, 999));
        Assert.Equal("Cust-78526101000", CodeSeries.Format(stem, 1000));
        Assert.Equal("Comp-7852610001", CodeSeries.Format(CodeSeries.Stem(CodeSeries.CompanyPrefix, "785", "2610"), 1));
        Assert.Equal("Emp-7852610001", CodeSeries.Format(CodeSeries.Stem(CodeSeries.EmployeePrefix, "785", "2610"), 1));
        // One dash only, after the prefix.
        Assert.Single(CodeSeries.Format(stem, 1), '-');
    }

    [Theory]
    [InlineData("09")]
    [InlineData("1")]
    [InlineData("1000")]
    [InlineData("1a")]
    [InlineData("")]
    public void A_stem_needs_a_real_branch_code(string branchCode) =>
        Assert.Throws<ArgumentException>(() => CodeSeries.Stem(CodeSeries.PersonPrefix, branchCode, "2610"));

    [Fact]
    public void A_person_and_a_company_have_their_own_series_and_an_unspecified_kind_has_none()
    {
        Assert.Equal("Cust-", CodeSeries.CustomerPrefix(CustomerKind.Person));
        Assert.Equal("Comp-", CodeSeries.CustomerPrefix(CustomerKind.Company));
        Assert.Throws<ArgumentOutOfRangeException>(() => CodeSeries.CustomerPrefix(CustomerKind.Unspecified));
    }

    [Fact]
    public void The_month_is_the_company_s_own_not_UTC_s()
    {
        // 23:30 UTC on 31 October is already 00:30 on 1 November in Algiers.
        var lateOctoberUtc = new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.Zero);
        Assert.Equal("2611", CodeSeries.Month(lateOctoberUtc, Algiers));
        Assert.Equal("2610", CodeSeries.Month(lateOctoberUtc, TimeZoneInfo.Utc));
        // And the other way round west of Greenwich: still October there when UTC is in November.
        Assert.Equal("2610", CodeSeries.Month(new DateTimeOffset(2026, 11, 1, 2, 0, 0, TimeSpan.Zero), West));
        Assert.Equal("2701", CodeSeries.Month(new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero), Algiers));
    }

    [Fact]
    public void Only_codes_of_exactly_this_prefix_branch_and_month_move_the_counter()
    {
        const string stem = "Cust-7852610";
        Assert.Equal(1, CodeSeries.NextSequence([], stem));
        Assert.Equal(1, CodeSeries.NextSequence(
        [
            "C001", "C002", "EMP-000001", // legacy codes
            "Cust-2610001",               // a code without a branch
            "Cust-785-2610001",           // a second dash
            "Cust-3612610005",            // another branch
            "Cust-112610005",             // a two-digit branch code from before 2026-10-19
            "Cust-7852609042",            // another month
            "Comp-7852610007",            // the other series
            "cust-7852610050",            // not the prefix, exactly
            "Cust-785261005",             // a two-digit tail
            "Cust-78526100077",           // a leading zero on a four-digit tail: never written by Format
            "Cust-7852610x01", "Cust-7852610 01", null, "",
        ], stem));
        Assert.Equal(4, CodeSeries.NextSequence(["Cust-7852610003", "Cust-7852610001", "C003"], stem));
        Assert.Equal(1000, CodeSeries.NextSequence(["Cust-7852610999"], stem));
        Assert.Equal(1001, CodeSeries.NextSequence(["Cust-7852610999", "Cust-78526101000"], stem));
        Assert.Null(CodeSeries.SequenceOf("Cust-78526101234567890", stem));
    }
}

/// <summary>
/// The customer code through the real HTTP pipeline (owner, 2026-10-05): drawn by the server when
/// Save is clicked, from the branch the customer is registered at, never typed and never changed.
/// The clock is the host's <see cref="TimeProvider"/>, replaced here; the time zone is the
/// company's (the Algeria profile's Africa/Algiers). A new company's main branch is location 10 and
/// its main warehouse 11.
/// </summary>
[Collection(HostCollection.Name)]
public class CustomerCodeEndpointTests
{
    private const string Password = "Essai@2026";
    private static readonly DateTimeOffset MidOctober = new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);

    private static PosHost HostAt(TestClock clock) => new(services: s => s.AddSingleton<TimeProvider>(clock));

    private static async Task<HttpClient> SignedInAsync(PosHost host, string? username = null, string? password = null)
    {
        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = username ?? PosHost.BootstrapUsername, password = password ?? PosHost.BootstrapPassword,
            deviceCode = username is null ? "C01" : null,
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.RootElement.GetProperty("token").GetString());
        return client;
    }

    private static PosDbContext Db(PosHost host) =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(host.ConnectionString).Options);

    private static object Person(string? code = "TYPED-1", string name = "Karim Ben Ali", Guid? branchId = null) => new
    {
        code, fullName = name, phone = (string?)null, address = (string?)null, taxId = (string?)null,
        email = (string?)null, birthDate = (DateTime?)null, creditLimit = 0m, kind = "Person", branchId,
    };

    private static object Company(string? code = "TYPED-2", bool withRepresentative = true, Guid? branchId = null) => new
    {
        code, fullName = "SARL El Injaz Travaux", phone = (string?)null, address = (string?)null, taxId = "000116001234567",
        email = (string?)null, birthDate = (DateTime?)null, creditLimit = 0m, kind = "Company", branchId,
        representative = withRepresentative ? new { fullName = "Nadia Bouzid" } : null,
    };

    private static async Task<(Guid Id, string Code)> CreatedAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetGuid(), body.GetProperty("code").GetString()!);
    }

    private static async Task<string> CreateAsync(HttpClient client, object body) =>
        (await CreatedAsync(await client.PostAsJsonAsync("/api/catalog/customers", body))).Code;

    private static async Task SeedAsync(PosHost host, params Customer[] rows)
    {
        await using var db = Db(host);
        db.Customers.AddRange(rows);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Persons_and_companies_are_numbered_in_their_own_series_and_a_typed_code_is_ignored()
    {
        var clock = new TestClock(MidOctober);
        using var host = HostAt(clock);
        using var started = host.CreateClient();
        await using var branchDb = Db(host);
        var mainCode = await branchDb.Branches.Where(b => b.IsMain && !b.IsWarehouse).Select(b => b.Code).SingleAsync();
        using var client = await SignedInAsync(host);

        Assert.Equal($"Cust-{mainCode}2610001", await CreateAsync(client, Person("MON-CODE")));
        // No code at all is just as good: the field is optional and never read.
        Assert.Equal($"Cust-{mainCode}2610002", await CreateAsync(client, new { fullName = "Amel Cherif", kind = "Person", creditLimit = 0m }));
        Assert.Equal($"Comp-{mainCode}2610001", await CreateAsync(client, Company()));
        Assert.Equal($"Cust-{mainCode}2610003", await CreateAsync(client, Person($"Cust-{mainCode}2610001")));
        Assert.Equal($"Comp-{mainCode}2610002", await CreateAsync(client, Company($"Comp-{mainCode}2610001")));

        await using var db = Db(host);
        Assert.False(await db.Customers.AnyAsync(c => c.Code == "MON-CODE"));
        Assert.Equal(5, await db.Customers.CountAsync());
    }

    [Fact]
    public async Task Every_month_starts_again_at_001_and_the_company_s_time_zone_decides_the_month()
    {
        // 22:30 UTC on 31 October is 23:30 in Algiers: still October.
        var clock = new TestClock(new DateTimeOffset(2026, 10, 31, 22, 30, 0, TimeSpan.Zero));
        using var host = HostAt(clock);
        using var started = host.CreateClient();
        await using var branchDb = Db(host);
        var mainCode = await branchDb.Branches.Where(b => b.IsMain && !b.IsWarehouse).Select(b => b.Code).SingleAsync();
        using var client = await SignedInAsync(host);
        Assert.Equal($"Cust-{mainCode}2610001", await CreateAsync(client, Person()));

        // An hour later UTC still says October, but it is 00:30 on 1 November in Algiers.
        clock.Now = new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.Zero);
        Assert.Equal($"Cust-{mainCode}2611001", await CreateAsync(client, Person()));
        Assert.Equal($"Comp-{mainCode}2611001", await CreateAsync(client, Company()));

        clock.Now = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal($"Cust-{mainCode}2611002", await CreateAsync(client, Person()));
    }

    [Fact]
    public async Task Past_999_the_sequence_simply_grows()
    {
        var clock = new TestClock(MidOctober);
        using var host = HostAt(clock);
        using var started = host.CreateClient();
        await using var branchDb = Db(host);
        var mainCode = await branchDb.Branches.Where(b => b.IsMain && !b.IsWarehouse).Select(b => b.Code).SingleAsync();
        await SeedAsync(host, new Customer { Code = $"Cust-{mainCode}2610999", FullName = "Le 999e", Kind = CustomerKind.Person });
        using var client = await SignedInAsync(host);

        Assert.Equal($"Cust-{mainCode}26101000", await CreateAsync(client, Person()));
        Assert.Equal($"Cust-{mainCode}26101001", await CreateAsync(client, Person()));
    }

    [Fact]
    public async Task Legacy_codes_do_not_disturb_the_counter_and_a_deleted_customer_s_number_is_not_reused()
    {
        var clock = new TestClock(MidOctober);
        using var host = HostAt(clock);
        using var started = host.CreateClient();
        await using var branchDb = Db(host);
        var mainCode = await branchDb.Branches.Where(b => b.IsMain && !b.IsWarehouse).Select(b => b.Code).SingleAsync();
        await SeedAsync(host,
            new Customer { Code = "C001", FullName = "Client de passage", Kind = CustomerKind.Person },
            new Customer { Code = "C002", FullName = "Entreprise Ben Ali BTP" },
            new Customer { Code = "Cust-2610001", FullName = "Sans agence", Kind = CustomerKind.Person },
            new Customer { Code = $"Cust-{mainCode}2609042", FullName = "Septembre", Kind = CustomerKind.Person },
            new Customer { Code = $"Cust-{mainCode}26100077", FullName = "Sosie", Kind = CustomerKind.Person },
            new Customer { Code = $"Comp-{mainCode}2610007", FullName = "Déjà là", Kind = CustomerKind.Company });
        using var client = await SignedInAsync(host);

        var (first, firstCode) = await CreatedAsync(await client.PostAsJsonAsync("/api/catalog/customers", Person()));
        Assert.Equal($"Cust-{mainCode}2610001", firstCode);
        Assert.Equal($"Comp-{mainCode}2610008", await CreateAsync(client, Company()));

        // Deleted is a tombstone, and its number stays taken.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/catalog/customers/{first}")).StatusCode);
        Assert.Equal($"Cust-{mainCode}2610002", await CreateAsync(client, Person()));
    }

    [Fact]
    public async Task An_edit_keeps_the_stored_code_whatever_the_body_says_and_whatever_the_kind_becomes()
    {
        var clock = new TestClock(MidOctober);
        using var host = HostAt(clock);
        using var started = host.CreateClient();
        await using var branchDb = Db(host);
        var mainCode = await branchDb.Branches.Where(b => b.IsMain && !b.IsWarehouse).Select(b => b.Code).SingleAsync();
        var legacy = new Customer { Code = "C001", FullName = "Client de passage", Kind = CustomerKind.Person };
        await SeedAsync(host, legacy);
        using var client = await SignedInAsync(host);
        var (company, code) = await CreatedAsync(await client.PostAsJsonAsync("/api/catalog/customers", Company()));
        Assert.Equal($"Comp-{mainCode}2610001", code);

        var edited = await client.PutAsJsonAsync($"/api/catalog/customers/{company}", Company("C001", withRepresentative: false));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal($"Comp-{mainCode}2610001", (await edited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // Made a person later, it keeps the code it was drawn as a company.
        var turned = await client.PutAsJsonAsync($"/api/catalog/customers/{company}", Person($"Cust-{mainCode}2610001"));
        Assert.True(turned.StatusCode == HttpStatusCode.OK, await turned.Content.ReadAsStringAsync());
        Assert.Equal($"Comp-{mainCode}2610001", (await turned.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // A customer from before the rule keeps its code too: statements and invoices carry it.
        var kept = await client.PutAsJsonAsync($"/api/catalog/customers/{legacy.Id}", Person($"Cust-{mainCode}2610999", "Client de passage"));
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);

        await using (var db = Db(host))
        {
            Assert.Equal("C001", (await db.Customers.AsNoTracking().SingleAsync(c => c.Id == legacy.Id)).Code);
            Assert.Equal($"Comp-{mainCode}2610001", (await db.Customers.AsNoTracking().SingleAsync(c => c.Id == company)).Code);
        }
        // Nothing the edits sent moved the person counter.
        Assert.Equal($"Cust-{mainCode}2610001", await CreateAsync(client, Person()));
    }

    [Fact]
    public async Task Each_branch_draws_its_own_numbers_and_only_the_admin_chooses_the_branch()
    {
        var clock = new TestClock(MidOctober);
        using var host = HostAt(clock);
        using var started = host.CreateClient();
        await using var branchDb = Db(host);
        var mainCode = await branchDb.Branches.Where(b => b.IsMain && !b.IsWarehouse).Select(b => b.Code).SingleAsync();
        using var admin = await SignedInAsync(host);
        var oran = await TestBranches.SellingAsync(admin, "Oran");
        var oranCode = (await admin.GetFromJsonAsync<JsonElement>("/api/catalog/branches"))
            .EnumerateArray().Single(b => b.GetProperty("id").GetGuid() == oran).GetProperty("code").GetString();
        Assert.True(BranchCodes.IsWellFormed(oranCode));

        // The admin's working branch, when named; the main branch (10) when not, or when it is no location.
        Assert.Equal($"Cust-{oranCode}2610001", await CreateAsync(admin, Person(branchId: oran)));
        Assert.Equal($"Cust-{mainCode}2610001", await CreateAsync(admin, Person()));
        Assert.Equal($"Cust-{mainCode}2610002", await CreateAsync(admin, Person(branchId: Guid.NewGuid())));
        Assert.Equal($"Cust-{oranCode}2610002", await CreateAsync(admin, Person(branchId: oran)));
        Assert.Equal($"Comp-{oranCode}2610001", await CreateAsync(admin, Company(branchId: oran)));

        // A branch-locked manager registers at their own branch, whatever the request names.
        var main = (await admin.GetFromJsonAsync<JsonElement>("/api/catalog/branches"))
            .EnumerateArray().Single(b => b.GetProperty("code").GetString() == mainCode).GetProperty("id").GetGuid();
        var (_, loginId) = await TestUsers.CreateAsync(admin, "Gérant Oran", "Manager", oran, Password);
        using var manager = await SignedInAsync(host, loginId, Password);
        Assert.Equal($"Cust-{oranCode}2610003", await CreateAsync(manager, Person(branchId: main)));
    }

    [Fact]
    public async Task A_drawn_code_that_is_already_taken_trips_the_index_and_is_drawn_again()
    {
        var clock = new TestClock(MidOctober);
        using var host = HostAt(clock);
        using var started = host.CreateClient();
        await using var branchDb = Db(host);
        var mainCode = await branchDb.Branches.Where(b => b.IsMain && !b.IsWarehouse).Select(b => b.Code).SingleAsync();
        using var client = await SignedInAsync(host);
        Assert.Equal($"Cust-{mainCode}2610001", await CreateAsync(client, Person()));

        // Force the draw onto the number the first customer holds, as a write outside the lock would.
        var forced = new System.Collections.Concurrent.ConcurrentQueue<int>([1]);
        var previous = CodeSeriesDraw.Draw;
        CodeSeriesDraw.Draw = next => forced.TryDequeue(out var taken) ? taken : next;
        try
        {
            Assert.Equal($"Cust-{mainCode}2610002", await CreateAsync(client, Person()));
            Assert.True(forced.IsEmpty);
        }
        finally
        {
            CodeSeriesDraw.Draw = previous;
        }

        await using var db = Db(host);
        Assert.Equal(2, await db.Customers.CountAsync());
    }

    [Fact]
    public async Task Customers_saved_at_the_same_moment_each_get_their_own_consecutive_code()
    {
        var clock = new TestClock(MidOctober);
        using var host = HostAt(clock);
        using var started = host.CreateClient();
        await using var branchDb = Db(host);
        var mainCode = await branchDb.Branches.Where(b => b.IsMain && !b.IsWarehouse).Select(b => b.Code).SingleAsync();
        using var client = await SignedInAsync(host);
        var oran = await TestBranches.SellingAsync(client, "Oran");
        var oranCode = await branchDb.Branches.Where(b => b.Id == oran).Select(b => b.Code).SingleAsync();

        // Several administrators with the create form open, all clicking Save at once, at two branches.
        var requests = Enumerable.Range(0, 10).Select(i => client.PostAsJsonAsync("/api/catalog/customers", Person("SAME", $"Client {i}")))
            .Concat(Enumerable.Range(0, 4).Select(_ => client.PostAsJsonAsync("/api/catalog/customers", Company("SAME"))))
            .Concat(Enumerable.Range(0, 5).Select(i => client.PostAsJsonAsync("/api/catalog/customers", Person("SAME", $"Oranais {i}", oran))));
        var responses = await Task.WhenAll(requests);

        var codes = new List<string>();
        foreach (var response in responses) codes.Add((await CreatedAsync(response)).Code);
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 10).Select(n => $"Cust-{mainCode}2610{n:D3}"), codes.Where(c => c.StartsWith($"Cust-{mainCode}")).Order());
        Assert.Equal(Enumerable.Range(1, 4).Select(n => $"Comp-{mainCode}2610{n:D3}"), codes.Where(c => c.StartsWith($"Comp-{mainCode}")).Order());
        Assert.Equal(Enumerable.Range(1, 5).Select(n => $"Cust-{oranCode}2610{n:D3}"), codes.Where(c => c.StartsWith($"Cust-{oranCode}")).Order());
    }
}
