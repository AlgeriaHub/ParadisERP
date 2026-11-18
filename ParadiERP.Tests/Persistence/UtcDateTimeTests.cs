using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadiERP.API.Endpoints;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Platform;
using ParadiERP.Tests.Hosting;
using ParadiERP.Tests.Security;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Tests.Persistence;

/// <summary>
/// Times read back from the database say they are UTC (2026-10-13). Neither provider keeps a
/// DateTime's kind, so every one came back Unspecified, went out without its Z, and a browser read it
/// as its own clock: a dispatch at 23:12 in Algiers read 20:12 on a computer in Riyadh. The prints
/// were right because they patched it themselves; the screens were not.
/// </summary>
public class UtcDateTimeTests
{
    /// <summary>What the property's converter makes of a value read from the column.</summary>
    private static bool ReadsAsUtc(Microsoft.EntityFrameworkCore.Metadata.IReadOnlyProperty property) =>
        property.GetValueConverter() is { } converter
        && converter.ConvertFromProvider(new DateTime(2026, 10, 13, 22, 12, 0)) is DateTime read
        && read.Kind == DateTimeKind.Utc && read == new DateTime(2026, 10, 13, 22, 12, 0);

    [Fact]
    public void Every_moment_in_the_model_reads_back_as_utc_and_the_calendar_days_stay_dates()
    {
        // The context the migration tools build; only its model is read, nothing is opened.
        using var db = PosDesignTimeFactory.Create(PosDbProviders.Sqlite);
        var dates = db.Model.GetEntityTypes().SelectMany(t => t.GetDeclaredProperties()).Where(UtcDateTimes.IsDateTime).ToList();
        Assert.True(dates.Count > 50, $"only {dates.Count} DateTime properties found");

        // A DateTime is a moment or a calendar day, decided once. One that is neither would come back
        // without a zone again, so it is refused here until someone says which it is.
        var undecided = dates.Where(p => !UtcDateTimes.IsInstant(p) && !UtcDateTimes.CalendarDays.Contains(UtcDateTimes.NameOf(p)))
            .Select(UtcDateTimes.NameOf).ToList();
        Assert.True(undecided.Count == 0,
            "Name each moment …Utc, or list it as a calendar day in UtcDateTimes.CalendarDays: " + string.Join(", ", undecided));

        Assert.All(dates.Where(UtcDateTimes.IsInstant), p => Assert.True(ReadsAsUtc(p), UtcDateTimes.NameOf(p)));
        Assert.All(dates.Where(p => !UtcDateTimes.IsInstant(p)), p => Assert.Null(p.GetValueConverter()));

        // Both lists name properties that exist, so neither can rot into a spelling nobody has.
        var names = dates.Select(UtcDateTimes.NameOf).ToHashSet();
        Assert.Subset(names, UtcDateTimes.CalendarDays.ToHashSet());
        Assert.Subset(names, UtcDateTimes.InstantsNamedOtherwise.ToHashSet());

        // The one the defect was found on reads as UTC; a birthday stays the date it is.
        Assert.True(UtcDateTimes.IsInstant(dates.Single(p => UtcDateTimes.NameOf(p) == "StockTransfer.DispatchedAtUtc")));
        Assert.False(UtcDateTimes.IsInstant(dates.Single(p => UtcDateTimes.NameOf(p) == "Customer.BirthDate")));
    }

    [Fact]
    public void The_portal_reads_its_times_back_as_utc_too()
    {
        using var platform = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite("Data Source=:memory:").Options);
        var dates = platform.Model.GetEntityTypes().SelectMany(t => t.GetDeclaredProperties()).Where(UtcDateTimes.IsDateTime).ToList();
        Assert.NotEmpty(dates);
        Assert.All(dates, p => Assert.True(ReadsAsUtc(p), UtcDateTimes.NameOf(p)));
    }

    [Fact]
    public void Neither_migration_set_has_anything_to_add()
    {
        // The converter changes how a value is read, not the column: no migration in either set.
        foreach (var provider in new[] { PosDbProviders.Sqlite, PosDbProviders.SqlServer })
        {
            using var db = PosDesignTimeFactory.Create(provider);
            Assert.False(db.Database.HasPendingModelChanges(), $"{provider}: the model differs from its migration snapshot");
        }
    }

    [Fact]
    public async Task A_moment_is_stored_exactly_as_before_and_read_back_as_utc()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var at = new DateTime(2026, 10, 13, 22, 12, 0);

        // Whatever kind a value carries, it goes in exactly as it did: the converter only reads.
        db.IntegrationOutbox.Add(new IntegrationOutbox
        {
            CreatedAtUtc = DateTime.SpecifyKind(at, DateTimeKind.Utc),
            NextAttemptAtUtc = DateTime.SpecifyKind(at, DateTimeKind.Unspecified),
            SentAtUtc = DateTime.SpecifyKind(at, DateTimeKind.Local),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await using (var raw = connection.CreateCommand())
        {
            raw.CommandText = "SELECT CreatedAtUtc, NextAttemptAtUtc, SentAtUtc FROM IntegrationOutbox";
            await using var reader = await raw.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            for (var i = 0; i < 3; i++)
            {
                var text = reader.GetString(i);
                Assert.StartsWith("2026-10-13 22:12:00", text);
                Assert.DoesNotContain("Z", text);
            }
        }

        var row = await db.IntegrationOutbox.AsNoTracking().SingleAsync();
        Assert.Equal(DateTimeKind.Utc, row.CreatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, row.NextAttemptAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, row.SentAtUtc!.Value.Kind);
        Assert.Equal(at, row.CreatedAtUtc);
        Assert.Equal("\"2026-10-13T22:12:00Z\"", JsonSerializer.Serialize(row.CreatedAtUtc));

        // A projection, an aggregate and a filter read the column through the same converter.
        var projected = await db.IntegrationOutbox.AsNoTracking().Select(o => new { o.CreatedAtUtc, o.SentAtUtc }).SingleAsync();
        Assert.Equal(DateTimeKind.Utc, projected.CreatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, projected.SentAtUtc!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, (await db.IntegrationOutbox.MaxAsync(o => o.CreatedAtUtc)).Kind);
        var from = new DateTime(2026, 10, 13, 22, 0, 0, DateTimeKind.Utc);
        Assert.Equal(1, await db.IntegrationOutbox.CountAsync(o => o.CreatedAtUtc >= from && o.CreatedAtUtc < from.AddHours(1)));
        Assert.Equal(0, await db.IntegrationOutbox.CountAsync(o => o.CreatedAtUtc >= from.AddHours(1)));
    }

    [Fact]
    public async Task A_calendar_day_comes_back_the_date_it_was()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        db.Customers.Add(new Customer { Code = "C-UTC", FullName = "Benali", BirthDate = new DateTime(1990, 5, 1) });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var born = (await db.Customers.AsNoTracking().SingleAsync(c => c.Code == "C-UTC")).BirthDate!.Value;
        Assert.Equal(DateTimeKind.Unspecified, born.Kind);
        Assert.Equal("\"1990-05-01T00:00:00\"", JsonSerializer.Serialize(born));
    }

    [Fact]
    public async Task A_transfer_read_back_through_its_route_states_its_times_in_utc()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        (await db.Products.SingleAsync(p => p.Id == TillHarness.Pipe)).CostPrice = 600m;
        await db.SaveChangesAsync();
        var admin = (await BranchLockTests.UserAsync(db, UserRole.Admin, TillHarness.Algiers)).Principal;
        const string route = "/api/catalog/stock/transfers";

        var (status, body) = await BranchLockTests.InvokeAsync(db, app => app.MapStockTransferEndpoints(), "POST", route, admin,
            $$"""{"fromBranchId":"{{TillHarness.Algiers}}","toBranchId":"{{TillHarness.Oran}}","lines":[{"productId":"{{TillHarness.Pipe}}","qty":2}]}""");
        Assert.True(status == 201, $"{status}: {body}");
        var id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();

        // What the screen opens later: a fresh read, nothing left in memory from the dispatch.
        db.ChangeTracker.Clear();
        var (read, json) = await BranchLockTests.InvokeAsync(db, app => app.MapStockTransferEndpoints(), "GET", route + "/{id:guid}", admin, "",
            ("id", id.ToString()));
        Assert.True(read == 200, $"{read}: {json}");
        var transfer = JsonDocument.Parse(json).RootElement;

        var dispatched = transfer.GetProperty("dispatchedAtUtc").GetString()!;
        Assert.EndsWith("Z", dispatched);
        var stored = (await db.StockTransfers.AsNoTracking().SingleAsync(t => t.Id == id)).DispatchedAtUtc;
        Assert.Equal(stored, DateTime.Parse(dispatched, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal));
        var events = transfer.GetProperty("events").EnumerateArray().ToList();
        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.EndsWith("Z", e.GetProperty("atUtc").GetString()));
    }
}

/// <summary>The same, through the real <c>Program</c>: the till pushes a sale, the back office reads it.</summary>
[Collection(HostCollection.Name)]
public class UtcDateTimeHostTests
{
    private const string Password = "Essai@2026";

    [Fact]
    public async Task A_sale_read_back_over_http_states_its_time_in_utc()
    {
        using var host = new PosHost();
        await AssertSaleReadsBackInUtcAsync(host);
    }

    [SqlServerFact]
    public async Task On_SQL_Server_a_sale_read_back_over_http_states_its_time_in_utc()
    {
        await using var database = new SqlServerTestDatabase();
        using var host = new PosHost(new Dictionary<string, string?>
        {
            ["Database__Provider"] = "SqlServer",
            ["ConnectionStrings__Default"] = database.ConnectionString,
        });
        await AssertSaleReadsBackInUtcAsync(host);
    }

    /// <summary>A sale rung up at 23:12 in Algiers — 22:12 UTC, the evening before — read back by the office.</summary>
    private static async Task AssertSaleReadsBackInUtcAsync(PosHost host)
    {
        var (admin, _) = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        using var adminClient = admin;
        var branch = await TestBranches.SellingAsync(admin, "Alger");
        await TestUsers.CreateAsync(host, admin, "caisse.utc", "Cashier", branch, Password, pin: "1234");
        var (till, login) = await SignedInAsync(host, host.SignInName("caisse.utc"), Password);
        using var tillClient = till;
        var device = login.GetProperty("deviceCode").GetString()!;
        var cashier = login.GetProperty("user").GetProperty("id").GetGuid();

        var soldAt = DateTime.UtcNow.Date.AddDays(-1).AddHours(22).AddMinutes(12);
        var expected = soldAt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "Z";
        var id = Guid.NewGuid();
        var push = await till.PostAsJsonAsync("/api/sync/push", new { deviceCode = device, sales = new[] { Sale(id, device, cashier, expected) } });
        var pushed = await push.Content.ReadAsStringAsync();
        Assert.True(JsonDocument.Parse(pushed).RootElement.GetProperty("accepted").GetInt32() == 1, pushed);

        var sale = await admin.GetFromJsonAsync<JsonElement>($"/api/sales/{id}");
        Assert.Equal(expected, sale.GetProperty("soldAtUtc").GetString());
        Assert.EndsWith("Z", sale.GetProperty("receivedAtUtc").GetString());

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/sales?branchId={branch}");
        var row = list.EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == id);
        Assert.Equal(expected, row.GetProperty("soldAtUtc").GetString());
    }

    private static object Sale(Guid id, string device, Guid cashier, string soldAtUtc) => new
    {
        id, reference = $"{device}-000001", localSeq = 1L, soldAtUtc, customerId = (Guid?)null,
        cashierUserId = cashier, cashierName = "Essai", subTotalHt = 10m, taxTotal = 0m, discountTotal = 0m,
        grandTotalTtc = 10m, roundingAdjustment = 0m, paidAmount = 10m, changeAmount = 0m,
        paymentMethod = "Cash", status = "Completed", refundOfSaleId = (Guid?)null, note = (string?)null, taxMode = "Untaxed",
        lines = new[] { new { id = Guid.NewGuid(), productId = Guid.NewGuid(), sku = "X", nameSnapshot = "X", qty = 1m,
            unitPrice = 10m, taxRate = 0m, lineDiscount = 0m, lineTotalTtc = 10m, lineTaxAmount = 0m } },
        payments = new[] { new { id = Guid.NewGuid(), method = "Cash", amount = 10m, reference = (string?)null } },
    };

    private static async Task<(HttpClient Client, JsonElement Body)> SignedInAsync(PosHost host, string username, string password)
    {
        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password, deviceCode = (string?)null });
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
        var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.Clone();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        return (client, body);
    }
}
