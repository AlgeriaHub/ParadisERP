using System.Text.Json;
using System.Text.Json.Nodes;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Seeding;
using ParadiERP.Tests.Hosting;
using ParadiERP.Tests.Security;
using Microsoft.EntityFrameworkCore;
using static ParadiERP.Tests.Manufacturing.CostHistoryWorld;
using static ParadiERP.Tests.Manufacturing.MfgHarness;
using static ParadiERP.Tests.Manufacturing.ProductionWorld;

namespace ParadiERP.Tests.Manufacturing;

/// <summary>
/// B2 on SQL Server: the same scenario gives the same figures as on SQLite (money is added in memory, never by the
/// provider), SQL Server's case-insensitive unique key keeps one late cost where SQLite keeps two, decimals and
/// company-day boundaries survive the round trip, and nothing is written.
/// </summary>
public sealed class ProductionCostHistorySqlServerTests
{
    [SqlServerFact]
    public async Task The_reconciliation_of_one_scenario_is_identical_on_sql_server_and_sqlite()
    {
        string onSqlite;
        await using (var s = await NewAsync())
            onSqlite = await ScenarioAsync(s.Db);

        await using var database = new SqlServerTestDatabase();
        await using var db = await OpenAsync(database.ConnectionString);
        var onSqlServer = await ScenarioAsync(db);

        Assert.Equal(onSqlite, onSqlServer);
    }

    [SqlServerFact]
    public async Task A_case_variant_late_source_is_one_record_on_sql_server_and_two_flagged_records_on_sqlite()
    {
        await using var database = new SqlServerTestDatabase();
        await using var db = await OpenAsync(database.ConnectionString);
        var w = await SetupAsync(db);
        var id = await WorkedAndCompletedAsync(db, w);
        await FreezeAsync(db, w, id);
        var first = await LateCostAsync(db, w, id, "expense", "PV-777", 15m);
        var second = await LateCostAsync(db, w, id, "expense", "pv-777", 15m);
        // The unique key ignores case under SQL Server's collation: B1 answers with the record already there.
        Assert.Equal(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
        var report = await OkAsync(db, w.AdminPrincipal, "GET", LateCosts);
        var row = report.GetProperty("rows").EnumerateArray().Single();
        Assert.Equal(("recognized", 15m), (row.GetProperty("treatment").GetString(), Dec(row, "recognizedEffect")));

        await using var s = await NewAsync();
        var lite = await SetupAsync(s.Db);
        var liteId = await WorkedAndCompletedAsync(s.Db, lite);
        await FreezeAsync(s.Db, lite, liteId);
        await LateCostAsync(s.Db, lite, liteId, "expense", "PV-777", 15m);
        await LateCostAsync(s.Db, lite, liteId, "expense", "pv-777", 15m);
        var liteReport = await OkAsync(s.Db, lite.AdminPrincipal, "GET", LateCosts);
        Assert.Equal(2, liteReport.GetProperty("rows").GetArrayLength());
        Assert.All(liteReport.GetProperty("rows").EnumerateArray(), r => Assert.Equal("uncertain", r.GetProperty("treatment").GetString()));
        Assert.Equal((0m, 30m), (Dec(liteReport, "recognized"), Dec(liteReport, "uncertain")));
    }

    [SqlServerFact]
    public async Task Decimals_and_company_day_boundaries_survive_sql_server_and_no_route_writes()
    {
        await using var database = new SqlServerTestDatabase();
        await using var db = await OpenAsync(database.ConnectionString);
        var w = await SetupAsync(db);
        var id = await WorkedAndCompletedAsync(db, w);
        await FreezeAsync(db, w, id);
        var zone = ParadiERP.Infrastructure.Catalog.StoreDocumentHistory.Zone(new Algeria());
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, zone));
        var lastMoment = today.ToDateTime(new TimeOnly(22, 59, 59), DateTimeKind.Utc); // 23:59:59 in Algiers
        await InsertLateAsync(db, id, "expense", "BIG", 1_234_567.891m, lastMoment);
        await InsertLateAsync(db, id, "materials", "SMALL", -0.001m, lastMoment);
        await InsertLateAsync(db, id, "expense", "NEXT-DAY", 7m, lastMoment.AddSeconds(1));

        var before = await FingerprintAsync(db);
        var r = await OkAsync(db, w.AdminPrincipal, "GET", Reconciliation, $"?asOf={today:yyyy-MM-dd}&live=true", Of(id));
        Assert.Equal(1_234_567.890m, Dec(r, "figures", "lateRecognized"));
        Assert.Equal(Dec(r, "figures", "original") + 1_234_567.890m, Dec(r, "figures", "adjustedProvisional"));
        var sheet = await db.ManufacturingCostSheets.AsNoTracking().SingleAsync(s => s.OrderId == id);
        Assert.Equal(sheet.UnitCost, r.GetProperty("figures").GetProperty("originalUnitCost").GetDecimal());
        foreach (var route in Registers)
        {
            await OkAsync(db, w.AdminPrincipal, "GET", route, route == History ? "?live=true" : "");
            Assert.Equal(200, (await CallRawAsync(db, w.AdminPrincipal, "GET", route, "?format=xlsx&lang=ar")).Status);
        }
        var late = await OkAsync(db, w.AdminPrincipal, "GET", LateCosts, $"?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        Assert.Equal(2, late.GetProperty("rows").GetArrayLength());
        Assert.Equal(before, await FingerprintAsync(db));
    }

    /// <summary>The scenario's reconciliation with every provider-independent figure, as canonical JSON.</summary>
    private static async Task<string> ScenarioAsync(PosDbContext db)
    {
        var w = await SetupAsync(db);
        var id = await WorkedAndCompletedAsync(db, w);
        await FreezeAsync(db, w, id);
        await LateCostAsync(db, w, id, "expense", "PV-900", 120m);
        await LateCostAsync(db, w, id, "labour", "PAY-2026-10", -30.0005m);
        await LateCostAsync(db, w, id, "freight-fix", "INV-77", 50m);
        await LateCostAsync(db, w, id, "expenses", "pv-900 ", 120m);
        var r = await OkAsync(db, w.AdminPrincipal, "GET", Reconciliation, "?live=true", Of(id));
        var node = JsonNode.Parse(r.GetRawText())!.AsObject();
        static JsonNode? Strip(JsonNode? n, params string[] drop)
        {
            if (n is JsonObject o) { foreach (var d in drop) o.Remove(d); foreach (var kv in o.ToList()) Strip(kv.Value, drop); }
            if (n is JsonArray a) foreach (var item in a) Strip(item, drop);
            return n;
        }
        var kept = new JsonObject
        {
            ["figures"] = node["figures"]?.DeepClone(), ["elements"] = node["elements"]?.DeepClone(), ["roundingResidual"] = node["roundingResidual"]?.DeepClone(),
            ["lateCosts"] = Strip(node["lateCosts"]?.DeepClone(), "id", "orderId", "orderNumber", "recordedAtUtc"),
            ["releases"] = Strip(node["releases"]?.DeepClone(), "releaseId", "number", "atUtc"),
            ["scrap"] = node["scrap"]?.DeepClone(), ["variances"] = node["variances"]?.DeepClone(), ["labour"] = node["labour"]?.DeepClone(),
            ["checks"] = node["checks"]?.DeepClone(), ["gaps"] = node["gaps"]?.DeepClone(), ["live"] = node["live"]?.DeepClone(),
        };
        // Late costs in a stable order: their record times differ by run, their contents do not.
        var lates = kept["lateCosts"]!.AsArray().Select(x => x!.ToJsonString()).Order(StringComparer.Ordinal).ToList();
        kept["lateCosts"] = new JsonArray(lates.Select(x => JsonNode.Parse(x)).ToArray());
        return Canonical(kept)!.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    /// <summary>
    /// Numbers by value, not by scale: SQL Server returns a decimal(18,3) as 50.000 where SQLite's text column returns
    /// 50.0 — the same figure.
    /// </summary>
    private static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, Canonical(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(Canonical).ToArray()),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number =>
            JsonValue.Create(decimal.Parse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture)
                .ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture)),
        _ => node?.DeepClone(),
    };

    private static async Task<PosDbContext> OpenAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<PosDbContext>();
        PosDbProviders.Configure(options, PosDbProviders.SqlServer, connectionString);
        var db = new PosDbContext(options.Options);
        await db.Database.MigrateAsync();
        // The SQLite harness's baseline, as B1's SQL Server tests seed it.
        db.SyncCounters.Add(new SyncCounter { Id = 1, Value = 0 });
        db.Branches.AddRange(
            new Branch { Id = TillHarness.Algiers, NameFr = "Alger-Centre", IsMain = true, IsOpenForOperation = true },
            new Branch { Id = TillHarness.Oran, NameFr = "Oran", IsOpenForOperation = true },
            new Branch { Id = WarehouseA, NameFr = "Atelier A", IsWarehouse = true, IsOpenForOperation = true },
            new Branch { Id = WarehouseB, NameFr = "Atelier B", IsWarehouse = true, IsOpenForOperation = true },
            new Branch { Id = MainWarehouse, NameFr = "Dépôt central", IsWarehouse = true, IsMain = true, IsOpenForOperation = true });
        db.Devices.AddRange(new Device { Code = "C01", BranchId = TillHarness.Algiers }, new Device { Code = "C02", BranchId = TillHarness.Oran });
        db.Products.Add(new Product { Id = TillHarness.Pipe, Sku = "PIPE-90", NameFr = "Tube 90", Price = 1000m, TrackStock = true });
        db.ProductStocks.AddRange(
            new ProductStock { ProductId = TillHarness.Pipe, BranchId = TillHarness.Algiers, StockQty = 10 },
            new ProductStock { ProductId = TillHarness.Pipe, BranchId = TillHarness.Oran, StockQty = 10 });
        await db.SaveChangesAsync();
        await CoreDataSeeder.EnsureAsync(db);
        db.ChangeTracker.Clear();
        return db;
    }
}
