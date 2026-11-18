using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadisERP.Application.Customers;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Tests.Hosting;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Customers;

/// <summary>
/// A customer's opening balance (owner, 2026-10-10: the company goes live today, and customers who
/// already owe money — or are owed it — start with that balance). Entered once, at registration or
/// later by whoever holds <c>customers.opening_balance</c>; posted against opening-balance equity,
/// never revenue; corrected only by a correction that says why.
/// </summary>
[Collection(HostCollection.Name)]
public class CustomerOpeningBalanceTests
{
    private const string Password = "Essai@2026";

    private static async Task<(HttpClient Client, JsonElement Login)> SignedInAsync(PosHost host, string? username = null, string? password = null)
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
        return (client, body);
    }

    private static PosDbContext Db(PosHost host) =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(host.ConnectionString).Options);

    private static object Person(string name, object? openingBalance = null) => new
    {
        fullName = name, firstName = name, familyName = "Test", phone = (string?)null, address = (string?)null,
        taxId = (string?)null, email = (string?)null, birthDate = (DateTime?)null, creditLimit = 0m, kind = "Person",
        openingBalance,
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> CreateAsync(HttpClient client, string name, object? openingBalance = null) =>
        (await JsonAsync(await client.PostAsJsonAsync("/api/catalog/customers", Person(name, openingBalance)), HttpStatusCode.Created))
        .GetProperty("id").GetGuid();

    private static async Task<JsonElement> StatementAsync(HttpClient client, Guid id) =>
        await JsonAsync(await client.GetAsync(
            $"/api/receivables/statement?customerId={id}&from={DateTime.UtcNow.AddDays(-300):O}&to={DateTime.UtcNow.AddDays(2):O}"),
            HttpStatusCode.OK);

    /// <summary>A day near today (UTC calendar), so the statement window always holds it.</summary>
    private static string Day(int offset) => DateTime.UtcNow.AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<Guid> MainBranchAsync(HttpClient admin)
    {
        var branches = await admin.GetFromJsonAsync<JsonElement>("/api/catalog/branches");
        return branches.EnumerateArray().Single(b => b.GetProperty("isMain").GetBoolean() && !b.GetProperty("isWarehouse").GetBoolean())
            .GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task A_debt_entered_at_registration_moves_the_balance_and_posts_against_opening_equity_never_revenue()
    {
        using var host = new PosHost();
        var (client, _) = await SignedInAsync(host);

        var created = await JsonAsync(await client.PostAsJsonAsync("/api/catalog/customers",
            Person("Karim", new { amount = 50_000m, direction = OpeningBalanceRules.OwesUs, on = Day(-3) })), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();

        Assert.Equal(50_000m, created.GetProperty("balance").GetDecimal());
        Assert.Equal(50_000m, created.GetProperty("openingBalance").GetDecimal());
        Assert.Equal(Day(-3), created.GetProperty("openingBalanceOn").GetString());
        Assert.NotEqual(JsonValueKind.Null, created.GetProperty("openingBalanceSetAtUtc").ValueKind);

        await using var db = Db(host);
        var entry = await db.LedgerEntries.AsNoTracking().Include(e => e.Lines)
            .SingleAsync(e => e.SourceType == LedgerSources.CustomerOpening && e.SourceId == id);
        var accounts = await db.LedgerAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Code);
        var receivable = Assert.Single(entry.Lines, l => accounts[l.AccountId] == LedgerAccounts.Customers);
        Assert.Equal(50_000m, receivable.Debit);
        Assert.Equal(id, receivable.CustomerId);
        var equity = Assert.Single(entry.Lines, l => accounts[l.AccountId] == LedgerAccounts.OpeningBalance);
        Assert.Equal(50_000m, equity.Credit);
        Assert.DoesNotContain(entry.Lines, l => accounts[l.AccountId].StartsWith('7'));

        // The statement is account 411 for this customer: it closes on the opening balance.
        var statement = await StatementAsync(client, id);
        Assert.Equal(50_000m, statement.GetProperty("closingBalance").GetDecimal());
        Assert.Equal(0m, statement.GetProperty("balanceDifference").GetDecimal());
    }

    [Fact]
    public async Task What_the_company_owes_the_customer_is_a_credit()
    {
        using var host = new PosHost();
        var (client, _) = await SignedInAsync(host);

        var id = await CreateAsync(client, "Nadia", new { amount = 1_200m, direction = OpeningBalanceRules.WeOwe, on = (string?)null });

        await using var db = Db(host);
        Assert.Equal(-1_200m, (await db.Customers.AsNoTracking().SingleAsync(c => c.Id == id)).Balance);
        Assert.Equal(-1_200m, (await StatementAsync(client, id)).GetProperty("closingBalance").GetDecimal());
    }

    [Fact]
    public async Task Zero_at_registration_is_nothing_owed_and_leaves_it_settable_once_later_and_never_twice()
    {
        using var host = new PosHost();
        var (client, login) = await SignedInAsync(host);
        Assert.Contains("customers.opening_balance",
            login.GetProperty("user").GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));

        var id = await CreateAsync(client, "Samir", new { amount = 0m, direction = OpeningBalanceRules.OwesUs, on = (string?)null });
        await using (var db = Db(host))
            Assert.Null((await db.Customers.AsNoTracking().SingleAsync(c => c.Id == id)).OpeningBalanceSetAtUtc);

        var set = await JsonAsync(await client.PostAsJsonAsync($"/api/catalog/customers/{id}/opening-balance",
            new { amount = 7_500m, direction = OpeningBalanceRules.OwesUs }), HttpStatusCode.OK);
        Assert.Equal(7_500m, set.GetProperty("balance").GetDecimal());

        var again = await client.PostAsJsonAsync($"/api/catalog/customers/{id}/opening-balance",
            new { amount = 9_000m, direction = OpeningBalanceRules.OwesUs });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        await using var check = Db(host);
        Assert.Equal(7_500m, (await check.Customers.AsNoTracking().SingleAsync(c => c.Id == id)).Balance);
        Assert.Equal(1, await check.LedgerEntries.CountAsync(e => e.SourceType == LedgerSources.CustomerOpening && e.SourceId == id));
    }

    [Fact]
    public async Task One_entered_at_registration_cannot_be_set_again_afterwards()
    {
        using var host = new PosHost();
        var (client, _) = await SignedInAsync(host);
        var id = await CreateAsync(client, "Yacine", new { amount = 100m, direction = OpeningBalanceRules.OwesUs, on = (string?)null });

        var response = await client.PostAsJsonAsync($"/api/catalog/customers/{id}/opening-balance",
            new { amount = 200m, direction = OpeningBalanceRules.OwesUs });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_manager_registers_with_a_balance_but_may_not_set_or_correct_one_later()
    {
        using var host = new PosHost();
        var (admin, _) = await SignedInAsync(host);
        await TestUsers.CreateAsync(host, admin, "gerant", UserRole.Manager.ToString(), await MainBranchAsync(admin), Password);
        var (manager, login) = await SignedInAsync(host, "gerant", Password);
        Assert.DoesNotContain("customers.opening_balance",
            login.GetProperty("user").GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));

        var withBalance = await CreateAsync(manager, "Lyes", new { amount = 3_000m, direction = OpeningBalanceRules.OwesUs, on = (string?)null });
        var without = await CreateAsync(manager, "Rania");

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync($"/api/catalog/customers/{without}/opening-balance",
            new { amount = 10m, direction = OpeningBalanceRules.OwesUs })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync($"/api/catalog/customers/{withBalance}/opening-corrections",
            new { amount = 10m, direction = OpeningBalanceRules.WeOwe, reason = "Erreur de saisie" })).StatusCode);
    }

    [Fact]
    public async Task A_wrong_balance_is_corrected_with_a_reason_and_the_statement_keeps_both()
    {
        using var host = new PosHost();
        var (client, _) = await SignedInAsync(host);
        var empty = await CreateAsync(client, "Amine");
        var id = await CreateAsync(client, "Sofiane", new { amount = 10_000m, direction = OpeningBalanceRules.OwesUs, on = (string?)null });

        // Nothing to correct on a customer whose opening balance was never set.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/catalog/customers/{empty}/opening-corrections",
            new { amount = 10m, direction = OpeningBalanceRules.WeOwe, reason = "x" })).StatusCode);
        // And no correction without its reason.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/catalog/customers/{id}/opening-corrections",
            new { amount = 1_000m, direction = OpeningBalanceRules.WeOwe, reason = "  " })).StatusCode);

        var correction = await JsonAsync(await client.PostAsJsonAsync($"/api/catalog/customers/{id}/opening-corrections",
            new { amount = 1_000m, direction = OpeningBalanceRules.WeOwe, reason = "Facture 2025-118 déjà réglée" }), HttpStatusCode.Created);
        Assert.Equal(-1_000m, correction.GetProperty("amount").GetDecimal());
        Assert.StartsWith("OBC-", correction.GetProperty("number").GetString());

        await using var db = Db(host);
        var customer = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == id);
        Assert.Equal(9_000m, customer.Balance);
        Assert.Equal(10_000m, customer.OpeningBalance);   // the opening balance itself never changes

        var statement = await StatementAsync(client, id);
        Assert.Equal(9_000m, statement.GetProperty("closingBalance").GetDecimal());
        var sources = statement.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("sourceType").GetString()).ToList();
        Assert.Contains(LedgerSources.CustomerOpening, sources);
        Assert.Contains(LedgerSources.CustomerOpeningCorrection, sources);

        var listed = await JsonAsync(await client.GetAsync($"/api/catalog/customers/{id}/opening-corrections"), HttpStatusCode.OK);
        Assert.Equal("Facture 2025-118 déjà réglée", Assert.Single(listed.EnumerateArray()).GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData(-5, OpeningBalanceRules.OwesUs, null)]
    [InlineData(5, "credit", null)]
    [InlineData(5, OpeningBalanceRules.OwesUs, "2999-01-01")]
    public async Task A_negative_amount_an_unknown_direction_or_a_future_date_is_refused_and_nothing_is_created(
        decimal amount, string direction, string? on)
    {
        using var host = new PosHost();
        var (client, _) = await SignedInAsync(host);
        await using var db = Db(host);
        var before = await db.Customers.CountAsync();

        var response = await client.PostAsJsonAsync("/api/catalog/customers", Person("Refus", new { amount, direction, on }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await db.Customers.CountAsync());
    }

    [Fact]
    public void The_rules_decide_the_sign_once_and_refuse_a_second_setting()
    {
        var today = new DateOnly(2026, 10, 10);
        Assert.Equal(1, OpeningBalanceRules.Sign(OpeningBalanceRules.OwesUs));
        Assert.Equal(-1, OpeningBalanceRules.Sign(OpeningBalanceRules.WeOwe));
        Assert.Null(OpeningBalanceRules.Sign("debit"));
        Assert.Null(OpeningBalanceRules.Problem(1m, OpeningBalanceRules.OwesUs, today, today));
        Assert.NotNull(OpeningBalanceRules.Problem(0m, OpeningBalanceRules.OwesUs, null, today));
        Assert.NotNull(OpeningBalanceRules.Problem(OpeningBalanceRules.MaxAmount + 1, OpeningBalanceRules.OwesUs, null, today));
        Assert.Equal(OpeningRefusal.AlreadySet, OpeningBalanceRules.MaySet(alreadySet: true, atRegistration: true, holdsPermission: true));
        Assert.Equal(OpeningRefusal.Forbidden, OpeningBalanceRules.MaySet(alreadySet: false, atRegistration: false, holdsPermission: false));
        Assert.Null(OpeningBalanceRules.MaySet(alreadySet: false, atRegistration: true, holdsPermission: false));
        Assert.NotNull(OpeningBalanceRules.ReasonProblem(new string('x', OpeningBalanceRules.MaxReasonLength + 1)));
    }
}
