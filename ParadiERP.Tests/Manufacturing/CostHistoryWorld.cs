using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ParadiERP.Tests.Manufacturing.MfgHarness;
using static ParadiERP.Tests.Manufacturing.ProductionWorld;

namespace ParadiERP.Tests.Manufacturing;

/// <summary>
/// The B2 scenarios, shared by the SQLite and SQL Server tests: an order worked, released in two parts and
/// completed through the real routes, then frozen by B1 — so every B2 figure rests on rows the application wrote.
/// </summary>
internal static class CostHistoryWorld
{
    public const string Reconciliation = "/api/manufacturing/orders/{id:guid}/cost-reconciliation";
    public const string History = "/api/manufacturing/reports/cost-history";
    public const string LateCosts = "/api/manufacturing/reports/late-costs";
    public const string Variances = "/api/manufacturing/reports/cost-variances";
    public const string Releases = "/api/manufacturing/reports/release-reconciliation";
    public const string Scrap = "/api/manufacturing/reports/scrap-costs";
    public const string Gaps = "/api/manufacturing/reports/cost-gaps";
    public const string Snapshot = "/api/manufacturing/orders/{id:guid}/cost-snapshot";
    public const string Late = "/api/manufacturing/orders/{id:guid}/cost-snapshot/late-costs";

    public static readonly string[] Registers = [History, LateCosts, Variances, Releases, Scrap, Gaps];

    /// <summary>
    /// 100 kg of nails on line A: 105 kg of wire issued at 150; on 20 October (plus <paramref name="dayOffset"/>) 100 kg cut and 98 formed with 2
    /// rejected, Karim booked 8 h (and Samir 4 h unless <paramref name="twoWorkers"/> is false); 60 kg released;
    /// 101 kg used, 3 kg offcut, 1 kg returned; an unbacked expense; the last 38 kg released; completion approved by
    /// a second admin. Returns the order id.
    /// </summary>
    public static async Task<Guid> WorkedAndCompletedAsync(
        PosDbContext db, ProductionWorld w, bool twoWorkers = true, decimal expense = 500m, int dayOffset = 0)
    {
        // One daily record per line and day: each order of a test works on its own day, Karim present for it.
        var day = Day.AddDays(dayOffset);
        if (!await db.AttendanceDays.AnyAsync(a => a.EmployeeId == w.Karim && a.WorkDate == day))
        {
            db.AttendanceDays.Add(new AttendanceDay { EmployeeId = w.Karim, WorkDate = day, Status = AttendanceDayStatus.Present, HoursWorked = 8m });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
        var (_, manager) = await AdminAsync(db);
        var order = await w.CreateOrderAsync(db, w.LineA, 100);
        var id = OrderId(order);
        var material = MaterialId(order);
        var (cut, form) = (Stage(order, 0).GetProperty("id").GetGuid(), Stage(order, 1).GetProperty("id").GetGuid());
        await OkAsync(db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/movements", Movement("Issue", material, 105), Of(id));

        var log = await OkAsync(db, w.AdminPrincipal, "POST", "/api/manufacturing/logs", $$"""{"lineId":"{{w.LineA}}","day":"{{day:yyyy-MM-dd}}"}""");
        var logId = log.GetProperty("log").GetProperty("id").GetGuid();
        var saved = await OkAsync(db, w.AdminPrincipal, "PUT", "/api/manufacturing/logs/{id:guid}",
            Rows(Row(id, cut, 100), Row(id, form, 98, rejected: 2, reason: "Bent")), Of(logId));
        var cutRow = saved.GetProperty("rows")[0].GetProperty("id").GetGuid();
        const string bookings = "/api/manufacturing/logs/{id:guid}/rows/{rowId:guid}/bookings";
        await OkAsync(db, w.AdminPrincipal, "POST", bookings, Book(w.Karim, 8m), Of(logId), ("rowId", cutRow.ToString()));
        // Samir declares his attendance per month: his day carries no hours, so his booking is an audited override.
        if (twoWorkers) await OkAsync(db, w.AdminPrincipal, "POST", bookings, Book(w.Samir, 4m, "Declared month"), Of(logId), ("rowId", cutRow.ToString()));
        await OkAsync(db, w.AdminPrincipal, "POST", "/api/manufacturing/logs/{id:guid}/submit", "", Of(logId));
        await OkAsync(db, manager, "POST", "/api/manufacturing/logs/{id:guid}/approve", "", Of(logId));

        await OkAsync(db, manager, "POST", "/api/manufacturing/orders/{id:guid}/releases", Release(60), Of(id));
        await OkAsync(db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/consumptions", Consume(material, 101, row: cutRow), Of(id));
        await OkAsync(db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/consumptions", Consume(material, 3, "Offcut"), Of(id));
        await OkAsync(db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/movements", Movement("Return", material, 1), Of(id));
        if (expense > 0m) await ExpenseAsync(db, w, id, expense);
        await OkAsync(db, manager, "POST", "/api/manufacturing/orders/{id:guid}/releases", Release(38), Of(id));
        await OkAsync(db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/request-completion", "", Of(id));
        await OkAsync(db, manager, "POST", "/api/manufacturing/orders/{id:guid}/approve-completion", "", Of(id));
        return id;
    }

    /// <summary>An order completed with nothing issued, worked or released (its cost is zero), as B1's tests make one.</summary>
    public static async Task<Guid> EmptyCompletedAsync(PosDbContext db, ProductionWorld w)
    {
        var id = OrderId(await w.CreateOrderAsync(db, w.LineA, 100m));
        var row = await db.ProductionOrders.SingleAsync(x => x.Id == id);
        row.Status = ProductionOrderStatus.Completed;
        row.AcceptedBaseQty = 100m;
        row.CompletedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return id;
    }

    public static Task<JsonElement> ExpenseAsync(PosDbContext db, ProductionWorld w, Guid id, decimal amount, Guid? voucher = null) =>
        OkAsync(db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/expenses", JsonSerializer.Serialize(new
        {
            description = "Freight", amount, paymentVoucherId = voucher, clientRequestId = Guid.NewGuid().ToString("N"),
        }), Of(id));

    public static Task<JsonElement> FreezeAsync(PosDbContext db, ProductionWorld w, Guid id, bool reconstruct = false) =>
        OkAsync(db, w.AdminPrincipal, "POST", Snapshot,
            JsonSerializer.Serialize(new { requestId = $"b2-{Guid.NewGuid():N}", reconstruct }), Of(id));

    public static Task<JsonElement> LateCostAsync(PosDbContext db, ProductionWorld w, Guid id, string kind, string source, decimal amount, string reason = "Late document") =>
        OkAsync(db, w.AdminPrincipal, "POST", Late, JsonSerializer.Serialize(new { kind, sourceReference = source, amount, reason }), Of(id));

    /// <summary>A late cost written straight to the table at a chosen record time (append-only allows inserts).</summary>
    public static async Task InsertLateAsync(PosDbContext db, Guid orderId, string kind, string source, decimal amount, DateTime recordedAtUtc)
    {
        var sheet = await db.ManufacturingCostSheets.AsNoTracking().SingleAsync(s => s.OrderId == orderId);
        db.ManufacturingLateCosts.Add(new ManufacturingLateCost
            { SheetId = sheet.Id, Kind = kind, SourceReference = source, Amount = amount, Reason = "test", RecordedAtUtc = recordedAtUtc });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    public static decimal Dec(JsonElement e, params string[] path)
    {
        foreach (var p in path) e = e.GetProperty(p);
        return e.GetDecimal();
    }

    public static JsonElement Find(JsonElement array, string property, string value) =>
        array.EnumerateArray().Single(x => x.GetProperty(property).GetString() == value);

    public static string Result(JsonElement report, string check) => Find(report.GetProperty("checks"), "code", check).GetProperty("result").GetString()!;

    /// <summary>Every B1 history row and everything costing could touch, serialized: equal before and after means nothing was written.</summary>
    public static async Task<string> FingerprintAsync(PosDbContext db)
    {
        var parts = new List<object>
        {
            (await db.Products.AsNoTracking().Select(p => new { p.Id, p.CostPrice }).ToListAsync()).OrderBy(x => x.Id),
            (await db.ManufacturingCostSheets.AsNoTracking().ToListAsync()).OrderBy(x => x.Id).Select(x => new { x.Id, x.TotalCost, x.UnitCost, x.SourceHash, x.SourceSnapshotJson, x.Kind, x.SourceState }),
            (await db.ManufacturingCostElementLines.AsNoTracking().ToListAsync()).OrderBy(x => x.Id).Select(x => new { x.Id, x.TotalCost, x.Status }),
            (await db.ManufacturingMaterialCostSnapshots.AsNoTracking().ToListAsync()).OrderBy(x => x.Id).Select(x => new { x.Id, x.TotalCost }),
            (await db.ManufacturingLabourRateSnapshots.AsNoTracking().ToListAsync()).OrderBy(x => x.Id).Select(x => new { x.Id, x.Rate, x.TotalCost }),
            (await db.ManufacturingExpenseAllocationSnapshots.AsNoTracking().ToListAsync()).OrderBy(x => x.Id).Select(x => new { x.Id, x.Amount }),
            (await db.ManufacturingScrapRecoverySnapshots.AsNoTracking().ToListAsync()).OrderBy(x => x.Id).Select(x => new { x.Id, x.RecoveryValue }),
            (await db.ManufacturingReleaseValueSnapshots.AsNoTracking().ToListAsync()).OrderBy(x => x.Id).Select(x => new { x.Id, x.Value }),
            (await db.ManufacturingLateCosts.AsNoTracking().ToListAsync()).OrderBy(x => x.Id).Select(x => new { x.Id, x.Amount, x.Kind, x.SourceReference }),
            (await db.PaymentVouchers.AsNoTracking().Select(v => new { v.Id, v.Status, v.Amount }).ToListAsync()).OrderBy(x => x.Id),
            new[]
            {
                await db.StockMovements.CountAsync(), await db.LedgerEntries.CountAsync(), await db.PayrollRuns.CountAsync(),
                await db.PayrollRunLines.CountAsync(), await db.LaborBookings.CountAsync(), await db.MaterialMovements.CountAsync(),
                await db.OutputReleases.CountAsync(), await db.ProductionOrderExpenses.CountAsync(), await db.ProductStocks.CountAsync(),
                await db.ProductionOrders.CountAsync(),
            },
        };
        return JsonSerializer.Serialize(parts);
    }

    /// <summary>The text of every worksheet of an xlsx, for asserting what an export holds.</summary>
    public static string WorkbookText(byte[] xlsx)
    {
        using var zip = new ZipArchive(new MemoryStream(xlsx), ZipArchiveMode.Read);
        var text = new StringBuilder();
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(entry.Open());
            text.Append(reader.ReadToEnd());
        }
        return text.ToString();
    }

    private static string Row(Guid order, Guid stage, decimal completed, decimal rejected = 0, string? reason = null) =>
        JsonSerializer.Serialize(new
        {
            id = (Guid?)null, orderId = order, stageId = stage, category = (string?)null, specification = (string?)null,
            plannedQty = 100m, completedQty = completed, rejectedQty = rejected, rejectReason = reason, notes = (string?)null,
        });

    private static string Rows(params string[] rows) => $$"""{"notes":null,"rows":[{{string.Join(",", rows)}}]}""";

    private static string Book(Guid employee, decimal hours, string? overrideReason = null) => JsonSerializer.Serialize(new { employeeId = employee, hours, overrideReason });
}
