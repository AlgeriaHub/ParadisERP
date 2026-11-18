using System.Text.Json;
using ParadisERP.Application.Contracts;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.FixedAssets;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Maintenance;
using ParadisERP.Infrastructure.Manufacturing;
using ParadisERP.Infrastructure.Payroll;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Tests.Manufacturing;
using ParadisERP.Tests.Security;
using Microsoft.EntityFrameworkCore;
using static ParadisERP.Tests.Maintenance.MaintenanceWorld;
using S = ParadisERP.Domain.MaintenanceAccountSlots;

namespace ParadisERP.Tests.Maintenance;

/// <summary>
/// Phase M7d (docs/maintenance/plan.md "M7d — as built"): MS-30 capitalised repairs and MS-38 per-piece history. The company:
/// hire scaffolds in the REN-12 register since three months ago (110 at Algiers, 1,200 each, 60 months), ten in maintenance;
/// welding rods (spare parts, 40) and bolts (merchandise, 10); Karim at a workshop rate of 450 an hour.
/// The repair: 5 rods issued before the practice ledger's first month, 10 bolts this month, 6 h of labour, 500 of outside work
/// on a paid voucher (account 6181) and 300 with none — M4's cost 3,800.
/// </summary>
public class MaintenanceCapitalisationTests
{
    private const string Ledger = "/api/maintenance/practice-ledger";
    private const string History = "/api/maintenance/history";

    private static readonly Guid Rod = Guid.Parse("a7a7a7a7-0000-0000-0000-000000000001");
    private static readonly Guid Bolt = Guid.Parse("a7a7a7a7-0000-0000-0000-000000000002");
    private static readonly Guid Karim = Guid.Parse("a7a7a7a7-0000-0000-0000-0000000000e1");
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Algiers");

    private sealed class Dz : IRegionalSettingsProvider
    {
        public RegionalSettings Get() => new() { CountryCode = "DZ", CurrencyCode = "DZD", CurrencyDecimals = 2, TimeZone = "Africa/Algiers" };
    }

    private static DateOnly ThisMonth()
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone);
        return new DateOnly(local.Year, local.Month, 1);
    }

    private static DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone));

    private static DateTime Noon(DateOnly day) => TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(12, 0)), Zone);

    // ---------- the register's arithmetic: a layer of cost only ----------

    [Fact]
    public void A_capitalised_repair_adds_cost_but_no_unit_is_depreciated_from_the_month_after_and_leaves_pro_rata()
    {
        var product = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var january = new DateOnly(2027, 1, 1);
        var engine = new FixedAssetEngine(january, new FixedAssetPolicy(60, new Dictionary<Guid, int>(), 0m));
        FixedAssetMovement Row(FixedAssetMovementKind kind, decimal qty, decimal cost, decimal depreciation, DateOnly month, DateOnly? start) => new()
        {
            ProductId = product, BranchId = branch, Kind = kind, Qty = qty, Cost = cost, Depreciation = depreciation, StartMonth = start,
            AtUtc = month.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc).AddDays(9),
        };
        engine.Apply(Row(FixedAssetMovementKind.Opening, 100m, 120_000m, 0m, january, january), january);
        // In March, a repair of 6,000 over the 2 units repaired.
        var march = new DateOnly(2027, 3, 1);
        engine.Apply(Row(FixedAssetMovementKind.CapitalisedRepair, 2m, 6_000m, 0m, march, march.AddMonths(1)), march);
        var pool = engine.Find(product, branch)!;
        Assert.Equal((100m, 126_000m), (pool.Qty, pool.Cost));

        engine.DepreciateThrough(march);
        Assert.Equal(2_000m, engine.Charges[^1].Amount);
        engine.DepreciateThrough(march.AddMonths(1));
        Assert.Equal(2_100m, engine.Charges[^1].Amount);

        // Half the units leave in May: half of everything, the repair's share with them.
        var may = new DateOnly(2027, 5, 1);
        engine.DepreciateThrough(may.AddMonths(-1));
        var (cost, depreciation) = engine.Share(product, branch, 50m);
        // Through April: three months at 2,000 and April's 2,100.
        Assert.Equal((63_000m, 4_050m), (cost, depreciation));
        engine.Apply(Row(FixedAssetMovementKind.Disposal, 50m, cost, depreciation, may, null), may);
        Assert.Equal((50m, 63_000m), (pool.Qty, pool.Cost));
        engine.DepreciateThrough(may);
        Assert.Equal(1_050m, engine.Charges[^1].Amount);

        // All the rest leaves: the repair's layer goes with the last unit.
        var june = new DateOnly(2027, 6, 1);
        engine.DepreciateThrough(june.AddMonths(-1));
        var rest = engine.Share(product, branch, 50m);
        engine.Apply(Row(FixedAssetMovementKind.Disposal, 50m, rest.Cost, rest.Depreciation, june, null), june);
        Assert.Empty(pool.Layers);
        Assert.Equal((0m, 0m, 0m), (pool.Qty, pool.Cost, pool.Depreciation));
    }

    // ---------- a company ----------

    private static async Task<MaintenanceWorld> WorldAsync(Action<MaintenanceSettings>? more = null)
    {
        var w = await NewAsync(On(s =>
        {
            s.LabourRate = MaintenanceLabourRate.WorkshopRate;
            s.WorkshopRatePerHour = 450m;
            s.BurdenAndVat = MaintenanceBurdenSource.OwnValues;
            s.PostingFromMonth = ThisMonth().AddMonths(-1);
            more?.Invoke(s);
        }), inMaintenance: 10);
        var db = w.Db;
        db.Products.AddRange(
            new Product { Id = Rod, Sku = "ROD-25", NameFr = "Baguette", NameAr = "قضيب", NameEn = "Rod", CostPrice = 40m, TrackStock = true,
                ManufacturingRole = ManufacturingRole.SparePart },
            new Product { Id = Bolt, Sku = "BLT-10", NameFr = "Boulon", NameAr = "برغي", NameEn = "Bolt", CostPrice = 10m, TrackStock = true });
        db.ProductStocks.AddRange(
            new ProductStock { ProductId = Rod, BranchId = Algiers, StockQty = 50m },
            new ProductStock { ProductId = Bolt, BranchId = Algiers, StockQty = 100m });
        db.Employees.Add(new Employee { Id = Karim, Number = "E-001", FullName = "Karim Benali", BranchId = Algiers, HiredOn = new DateOnly(2025, 1, 1), BaseRate = 52_000m });
        foreach (var (code, type) in new[]
        {
            ("215", LedgerAccountType.Asset), ("2815", LedgerAccountType.Asset), ("681", LedgerAccountType.Expense), ("652", LedgerAccountType.Expense),
            ("731", LedgerAccountType.Income), ("322", LedgerAccountType.Asset), ("6181", LedgerAccountType.Expense),
        })
            if (!await db.LedgerAccounts.AnyAsync(a => a.Code == code))
                db.LedgerAccounts.Add(new LedgerAccount { Code = code, Type = type, NameFr = code, NameAr = code, NameEn = code });
        await db.SaveChangesAsync();

        // The register: switched over three months ago, the scaffolds' pool at Algiers opened with the 110 held.
        var switchMonth = ThisMonth().AddMonths(-3);
        db.FixedAssetSettings.Add(new FixedAssetSettings
        {
            Treatment = RentalEquipmentTreatment.FixedAssets, DefaultUsefulLifeMonths = 60, SwitchMonth = switchMonth,
            SwitchedAtUtc = Noon(switchMonth.AddDays(9)), SwitchedBy = "comptable", TimeZoneId = "Africa/Algiers",
        });
        db.FixedAssetMovements.Add(new FixedAssetMovement
        {
            ProductId = Scaffold, BranchId = Algiers, Kind = FixedAssetMovementKind.Opening, Qty = 110m, Cost = 132_000m,
            StartMonth = switchMonth, AtUtc = Noon(switchMonth.AddDays(9)),
        });
        await db.SaveChangesAsync();
        await MapAsync(db, new()
        {
            [FixedAssetAccountSlots.Asset] = "215", [FixedAssetAccountSlots.AccumulatedDepreciation] = "2815",
            [FixedAssetAccountSlots.DepreciationExpense] = "681", [FixedAssetAccountSlots.Disposal] = "652",
            [S.OwnParts] = LedgerAccounts.MaintenanceExpense, [S.Capitalised] = "731", [ManufacturingAccountSlots.SuppliesStock] = "322",
        });
        db.ChangeTracker.Clear();
        return w;
    }

    private static async Task MapAsync(PosDbContext db, Dictionary<string, string?> slots)
    {
        var accounts = await db.LedgerAccounts.AsNoTracking().ToDictionaryAsync(a => a.Code, a => a.Id);
        foreach (var (slot, code) in slots)
        {
            var row = await db.ManufacturingAccountMappings.FirstOrDefaultAsync(m => m.Slot == slot);
            if (row is null) db.ManufacturingAccountMappings.Add(row = new ManufacturingAccountMapping { Slot = slot });
            row.LedgerAccountId = code is null ? null : accounts[code];
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static MaintenanceJobParts Parts(PosDbContext db) => new(db, new Dz());

    private static MaintenanceCosting Costing(PosDbContext db) =>
        new(db, new LabourRates(db, new PayrollSettingsStore(db, new Dz()), new Dz()), new Dz());

    private static async Task IssueAsync(MaintenanceWorld w, Guid job, Guid product, decimal qty)
    {
        var moved = await Parts(w.Db).MoveAsync(job,
            new MaintenancePartsRequest(Guid.NewGuid().ToString("N"), "Issue", [new MaintenancePartLineRequest(product, qty)], null), Chef);
        Assert.True(moved.Ok, moved.Error);
        w.Db.ChangeTracker.Clear();
    }

    /// <summary>The repair of two scaffolds: rods before the first month posted, bolts now, 6 h, 500 paid outside work, 300 unbacked.</summary>
    private static async Task<MaintenanceJobDto> RepairAsync(MaintenanceWorld w, CreateMaintenanceJobRequest? request = null)
    {
        var job = await w.OpenAsync(request ?? Internal(2));
        await IssueAsync(w, job.Id, Rod, 5);
        // The rods went out two months ago: before the first month the practice ledger posts (MS-39), so never in the books.
        await w.Db.MaintenanceJobParts.Where(p => p.JobId == job.Id && p.ProductId == Rod)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.AtUtc, Noon(ThisMonth().AddMonths(-2).AddDays(4))));
        await IssueAsync(w, job.Id, Bolt, 10);
        var today = Today();
        w.Db.LaborBookings.Add(new LaborBooking
        {
            EmployeeId = Karim, Day = today, OrderKind = LaborOrderKind.Maintenance, OrderId = job.Id, Hours = 6m,
            PayrollBranchId = Algiers, PayrollYear = today.Year, PayrollMonth = today.Month, AttendanceHours = 8m,
        });
        var voucher = new PaymentVoucher { Number = $"PV-{Random.Shared.Next(100_000, 999_999)}", BranchId = Algiers, Kind = PaymentVoucherKind.Expense, Amount = 500m,
            MethodKey = "cash", Status = PaymentVoucherStatus.Paid, ExpenseCategory = "repair", ExpenseAccountCode = "6181" };
        w.Db.PaymentVouchers.Add(voucher);
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        var parts = Parts(w.Db);
        Assert.True((await parts.AddExpenseAsync(job.Id, new MaintenanceJobExpenseRequest(Guid.NewGuid().ToString("N"), "Soudeur", 500m, voucher.Id), Chef, true)).Ok);
        Assert.True((await parts.AddExpenseAsync(job.Id, new MaintenanceJobExpenseRequest(Guid.NewGuid().ToString("N"), "Peinture", 300m, null), Chef, true)).Ok);
        w.Db.ChangeTracker.Clear();
        return job;
    }

    private static async Task<MaintenanceJobDto> CompleteAsync(MaintenanceWorld w, Guid job, decimal repaired, decimal scrapped = 0m)
    {
        var done = await w.Jobs.CompleteAsync(job, new CompleteMaintenanceJobRequest(repaired, scrapped, null), Second);
        Assert.True(done.Ok, done.Error);
        w.Db.ChangeTracker.Clear();
        return done.Value!;
    }

    private static async Task<JsonElement> OkAsync(MaintenanceWorld w, System.Security.Claims.ClaimsPrincipal caller, string method, string pattern, string json = "")
    {
        var (status, body) = await w.CallAsync(caller, method, pattern, json);
        w.Db.ChangeTracker.Clear();
        Assert.True(status == 200, $"{method} {pattern}: {status} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static decimal Line(JsonElement entry, string slot, string side) =>
        entry.GetProperty("lines").EnumerateArray().Where(l => l.GetProperty("slot").GetString() == slot).Sum(l => l.GetProperty(side).GetDecimal());

    // ---------- MS-30 ----------

    [Fact]
    public async Task By_default_a_repair_above_any_amount_stays_an_expense_and_nothing_is_written_to_the_register()
    {
        await using var w = await WorldAsync();
        var job = await RepairAsync(w);
        var done = await CompleteAsync(w, job.Id, 2);

        Assert.DoesNotContain(done.Events, e => e.Kind == nameof(MaintenanceJobEventKind.Capitalisation));
        var row = await w.Db.MaintenanceJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        Assert.Equal((null, null, null), (row.CapitalisedAtUtc, row.CapitalisedAmount, row.CapitalisedMovementId));
        Assert.Equal(0, await w.Db.MaintenanceJobCapitalisedLines.CountAsync());
        Assert.Equal([FixedAssetMovementKind.Opening], await w.Db.FixedAssetMovements.Select(m => m.Kind).ToListAsync());

        // The practice ledger: the bolts' issue only, Dr own repairs, as M6 built it.
        var month = await OkAsync(w, await w.CallerAsync(UserRole.Admin, Algiers, Chef), "GET", Ledger, $"?year={ThisMonth().Year}&month={ThisMonth().Month}");
        Assert.Equal(["parts_issue"], month.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("kind").GetString()));
        Assert.Null((await Costing(w.Db).JobAsync(job.Id, _ => true)).Value!.Capitalised);
    }

    [Fact]
    public async Task Above_the_amount_the_repair_joins_the_pool_at_completion_is_depreciated_from_the_next_month_and_its_entry_balances_without_counting_twice()
    {
        await using var w = await WorldAsync(s => (s.OwnRepairs, s.CapitaliseAboveAmount) = (MaintenanceOwnRepairs.CapitalisedAboveAmount, 1_000m));
        var job = await RepairAsync(w, Internal(2) with { ItemReference = "SC-7" });
        Assert.Equal(3_800m, (await Costing(w.Db).JobAsync(job.Id, _ => true)).Value!.Total);
        var done = await CompleteAsync(w, job.Id, 2);
        Assert.Contains(done.Events, e => e.Kind == nameof(MaintenanceJobEventKind.Capitalisation) && e.Detail!.StartsWith("capitalised 3500"));

        // What the pool received: the rods from stock (never in the books), the bolts back from own repairs, the hours as own work
        // capitalised (MS-33 "report only": the wages stay in 631/635), the paid outside work back from its voucher's account.
        // The 300 of outside work with no voucher stays an expense.
        var row = await w.Db.MaintenanceJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        Assert.Equal(3_500m, row.CapitalisedAmount);
        var lines = (await w.Db.MaintenanceJobCapitalisedLines.AsNoTracking().Where(l => l.JobId == job.Id).ToListAsync())
            .ToDictionary(l => (l.Element, l.Slot), l => l.Amount);
        Assert.Equal(new Dictionary<(string, string), decimal>
        {
            [("parts", ManufacturingAccountSlots.SuppliesStock)] = 200m, [("parts", S.OwnParts)] = 100m,
            [("labour", S.Capitalised)] = 2_700m, [("outside_work", S.Account("6181"))] = 500m,
        }, lines);

        // The register: a capitalised repair of 3,500 over the two units, no unit added, depreciated from the month after.
        var movement = await w.Db.FixedAssetMovements.AsNoTracking().SingleAsync(m => m.Id == row.CapitalisedMovementId);
        Assert.Equal((FixedAssetMovementKind.CapitalisedRepair, 2m, 3_500m, ThisMonth().AddMonths(1), job.Number, "SC-7"),
            (movement.Kind, movement.Qty, movement.Cost, movement.StartMonth, movement.DocumentNumber, movement.Reference));
        var settings = await FixedAssetData.CurrentAsync(w.Db);
        var engine = FixedAssetData.Replay(settings, await FixedAssetData.PolicyAsync(w.Db, settings),
            await FixedAssetData.RowsAsync(w.Db, null, null), ThisMonth().AddMonths(1));
        var pool = engine.Find(Scaffold, Algiers)!;
        Assert.Equal((110m, 135_500m), (pool.Qty, pool.Cost));
        Assert.Equal(2_200m, engine.Charges.Single(c => c.Month == ThisMonth()).Amount);
        Assert.Equal(2_258.333m, engine.Charges.Single(c => c.Month == ThisMonth().AddMonths(1)).Amount);

        // The job's cost card and the cumulative report: 3,500 capitalised, 300 expensed.
        Assert.Equal(3_500m, (await Costing(w.Db).JobAsync(job.Id, _ => true)).Value!.Capitalised);
        var products = (await Costing(w.Db).ProductsAsync(null, null, null, _ => true)).Value!;
        var scaffolds = products.Rows.Single(r => r.ProductId == Scaffold);
        Assert.Equal((3_800m, 3_500m, 300m), (scaffolds.Total, scaffolds.Capitalised, scaffolds.Expensed));
        Assert.Equal((3_500m, 300m), (products.Capitalised, products.Expensed));

        // The practice ledger of the month: Dr the asset 3,500 / Cr where each element was charged. Own repairs nets to nothing
        // (the bolts Dr 100 at issue, Cr 100 here), every entry balances and the register ties to the asset debit.
        var admin = await w.CallerAsync(UserRole.Admin, Algiers, Chef);
        var month = await OkAsync(w, admin, "GET", Ledger, $"?year={ThisMonth().Year}&month={ThisMonth().Month}");
        var entries = month.GetProperty("entries").EnumerateArray().ToList();
        var capitalised = entries.Single(e => e.GetProperty("kind").GetString() == "capitalised_repair");
        Assert.Equal((3_500m, 3_500m), (Line(capitalised, "fixed_assets.asset", "debit"), capitalised.GetProperty("lines").EnumerateArray().Sum(l => l.GetProperty("credit").GetDecimal())));
        Assert.Equal((2_700m, 500m, 200m, 100m), (Line(capitalised, S.Capitalised, "credit"), Line(capitalised, S.Account("6181"), "credit"),
            Line(capitalised, ManufacturingAccountSlots.SuppliesStock, "credit"), Line(capitalised, S.OwnParts, "credit")));
        Assert.Equal(0m, entries.Sum(e => Line(e, S.OwnParts, "debit") - Line(e, S.OwnParts, "credit")));
        var checks = month.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("code").GetString()!, c => c.GetProperty("passed").GetBoolean());
        Assert.All(checks, c => Assert.True(c.Value, c.Key));
        Assert.True(checks.ContainsKey("capitalised_tie"));

        // Moved to last month (the first month posted), the month posts balanced: the asset 3,500, nothing left in own repairs.
        var last = ThisMonth().AddMonths(-1);
        var lastDay = Noon(last.AddDays(19));
        await w.Db.MaintenanceJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(u => u
            .SetProperty(j => j.CapitalisedAtUtc, lastDay).SetProperty(j => j.CompletedAtUtc, lastDay).SetProperty(j => j.CreatedAtUtc, lastDay.AddDays(-5)));
        await w.Db.MaintenanceJobParts.Where(p => p.JobId == job.Id && p.ProductId == Bolt).ExecuteUpdateAsync(u => u.SetProperty(p => p.AtUtc, lastDay.AddDays(-1)));
        await w.Db.FixedAssetMovements.Where(m => m.Id == movement.Id).ExecuteUpdateAsync(u => u.SetProperty(m => m.AtUtc, lastDay));
        await OkAsync(w, admin, "POST", Ledger + "/post", JsonSerializer.Serialize(new { year = last.Year, month = last.Month }));
        var posted = await w.Db.LedgerEntryLines.AsNoTracking().Include(l => l.Account).Include(l => l.Entry)
            .Where(l => l.Entry!.SourceType == MaintenanceLedgerPostings.SourceType).ToListAsync();
        var net = posted.GroupBy(l => l.Account!.Code).ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));
        Assert.Equal(3_500m, net["215"]);
        Assert.Equal((-2_700m, -500m, -200m, -100m), (net["731"], net["6181"], net["322"], net[LedgerAccounts.Inventory]));
        Assert.Equal(0m, net.GetValueOrDefault(LedgerAccounts.MaintenanceExpense));
        Assert.Equal(0m, net.Values.Sum());

        // The pool's history shows the capitalised repair, posted with that month.
        var history = (await new FixedAssetRegisterReport(w.Db, TimeProvider.System).HistoryAsync(Scaffold, Algiers)).Value!;
        var repair = history.Lines.Single(l => l.Kind == "CapitalisedRepair");
        Assert.Equal((3_500m, job.Number, "SC-7", true), (repair.Cost, repair.DocumentNumber, repair.Reference, repair.Posted));
    }

    [Fact]
    public async Task At_or_below_the_amount_or_on_a_customer_s_equipment_the_repair_stays_an_expense()
    {
        await using var w = await WorldAsync(s =>
        {
            (s.OwnRepairs, s.CapitaliseAboveAmount) = (MaintenanceOwnRepairs.CapitalisedAboveAmount, 3_800m);
            s.CustomerJobs = MaintenanceSwitch.On;
        });
        var job = await RepairAsync(w);
        var done = await CompleteAsync(w, job.Id, 2);
        Assert.Contains(done.Events, e => e.Kind == nameof(MaintenanceJobEventKind.Capitalisation) && e.Detail!.Contains("kept an expense: cost 3800 not above 3800"));
        Assert.Null((await w.Db.MaintenanceJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id)).CapitalisedAtUtc);

        // A customer's equipment is never the company's to capitalise, whatever it cost.
        var customer = await w.OpenAsync(new CreateMaintenanceJobRequest(Guid.NewGuid().ToString("N"), Algiers, "Customer", Scaffold, 1, null, null, "Moteur",
            CustomerId: Contractor, Equipment: "Bétonnière du client"));
        await IssueAsync(w, customer.Id, Rod, 40);
        await w.Db.MaintenanceSettings.ExecuteUpdateAsync(u => u.SetProperty(s => s.CapitaliseAboveAmount, 1m));
        var customerDone = await CompleteAsync(w, customer.Id, 1);
        Assert.Contains(customerDone.Events, e => e.Detail == "kept an expense: not the company's equipment");

        // A job that only found its units beyond repair improved nothing.
        var wreck = await RepairAsync(w);
        var wrecked = await CompleteAsync(w, wreck.Id, 0, 2);
        Assert.Contains(wrecked.Events, e => e.Detail == "kept an expense: no unit repaired");

        Assert.Equal(0, await w.Db.MaintenanceJobCapitalisedLines.CountAsync());
        Assert.DoesNotContain(await w.Db.FixedAssetMovements.Select(m => m.Kind).ToListAsync(), k => k == FixedAssetMovementKind.CapitalisedRepair);
    }

    [Fact]
    public async Task Capitalising_is_refused_while_the_register_is_off_the_ledger_does_not_post_or_a_slot_is_unmapped()
    {
        // No register at all.
        var (connection, db) = await TillHarness.NewDbAsync();
        await using (connection)
        await using (db)
        {
            var store = new MaintenanceSettingsStore(db);
            var ask = Ask(await store.SettingsAsync()) with { OwnRepairs = "CapitalisedAboveAmount", CapitaliseAboveAmount = 50_000m, PostingFromMonth = "2026-10" };
            var off = await store.UpdateAsync(ask, _ => true, "comptable");
            Assert.Equal((409, MaintenanceSettingsStore.RegisterNotActiveCode), (off.Status, off.Code));
            Assert.False((await store.SettingsAsync()).AssetRegisterActive);
        }

        await using var w = await WorldAsync();
        var s = new MaintenanceSettingsStore(w.Db);
        var current = Ask(await s.SettingsAsync());
        Assert.True((await s.SettingsAsync()).AssetRegisterActive);
        var wanted = current with { OwnRepairs = "CapitalisedAboveAmount", CapitaliseAboveAmount = 50_000m };

        Assert.Equal((400, MaintenanceSettingsStore.InvalidCode), Code(await s.UpdateAsync(wanted with { CapitaliseAboveAmount = 0m }, _ => true, "comptable")));
        Assert.Equal((409, MaintenanceSettingsStore.PostingOffCode), Code(await s.UpdateAsync(wanted with { PostingFromMonth = null }, _ => true, "comptable")));
        await MapAsync(w.Db, new() { [S.Capitalised] = null });
        Assert.Equal((409, MaintenanceSettingsStore.AccountsNotMappedCode), Code(await s.UpdateAsync(wanted, _ => true, "comptable")));
        // With labour out of the cost, own work capitalised is never used, so not asked for.
        Assert.Null((await s.UpdateAsync(wanted with { CostIncludes = "PartsAndOutsideWorkOnly", LabourRate = "PayrollDerived", WorkshopRatePerHour = 0m }, _ => true, "comptable")).Error);
        await MapAsync(w.Db, new() { [S.Capitalised] = "731" });
        var saved = await s.UpdateAsync(wanted, _ => true, "comptable");
        Assert.Null(saved.Error);
        Assert.Equal(("CapitalisedAboveAmount", 50_000m), (saved.Saved!.OwnRepairs, saved.Saved.CapitaliseAboveAmount));
        // The accountant's half: a maintenance.settings holder cannot change it.
        Assert.Equal(403, (await s.UpdateAsync(Ask(saved.Saved) with { CapitaliseAboveAmount = 60_000m },
            k => k == PermissionCatalog.MaintenanceSettings, "chef")).Status);
        // While on, its accounts stay named; back to an expense, the amount is not kept.
        var (_, error) = await new MaintenanceAccountMap(w.Db).UpdateAsync(new([new(S.Capitalised, null)]), "comptable");
        Assert.NotNull(error);
        Assert.Equal(0m, (await s.UpdateAsync(Ask(saved.Saved) with { OwnRepairs = "AlwaysExpense" }, _ => true, "comptable")).Saved!.CapitaliseAboveAmount);
    }

    private static (int, string?) Code(MaintenanceSettingsResult r) => (r.Status, r.Code);

    private static UpdateMaintenanceSettingsRequest Ask(MaintenanceSettingsDto d) => new(
        d.Module, d.UnitsLeaveMaintenance, d.JobsFromReturnNotes, d.BookableEmployees, d.JobCloser, d.CustomerJobs,
        d.CostIncludes, d.OverheadPerLabourHour, d.LabourRate, d.WorkshopRatePerHour, d.BurdenAndVat, d.EmployerBurdenPercent,
        d.ExpenseVatPercent, d.CustomerPrice, d.LabourChargePerHour, d.MarkupPercent, d.ReturnRecharge, d.PriceBelowCost,
        d.RechargeSharing, d.OwnRepairs, d.PartsPosting, d.LabourInBooks, d.CustomerIncome, d.Advances, d.OpenJobsAtPeriodEnd,
        d.Rework, d.RepairHistory, d.PostingFromMonth, d.AccountantApproved, d.GuaranteeMonths, d.ProvisionPercent,
        d.CapitaliseAboveAmount, d.PerPieceAboveValue);

    // ---------- MS-38 ----------

    private static CreateMaintenanceJobRequest Piece(decimal qty, string? reference) =>
        new(Guid.NewGuid().ToString("N"), Algiers, "Internal", Scaffold, qty, null, reference, "Soudure");

    [Fact]
    public async Task Per_piece_a_type_above_the_value_names_one_piece_per_job_and_its_history_lists_every_job_on_that_piece()
    {
        // The default: history per type, a reference optional, any quantity.
        await using (var plain = await WorldAsync())
            Assert.True((await plain.Jobs.CreateAsync(Piece(3, null), Chef)).Ok);

        // Per piece above 1,000: the scaffolds (1,200) are tracked one by one; rods (40) would not be.
        await using var w = await WorldAsync(s => (s.RepairHistory, s.PerPieceAboveValue) = (MaintenanceHistory.PerPiece, 1_000m));
        var noReference = await w.Jobs.CreateAsync(Piece(1, null), Chef);
        Assert.Equal((400, MaintenanceJobs.PieceReferenceRequiredCode), (noReference.Status, noReference.Code));
        var two = await w.Jobs.CreateAsync(Piece(2, "SC-7"), Chef);
        Assert.Equal((400, MaintenanceJobs.OnePiecePerJobCode), (two.Status, two.Code));

        var first = await w.OpenAsync(Piece(1, "SC-7"));
        await IssueAsync(w, first.Id, Bolt, 4);
        await CompleteAsync(w, first.Id, 1);
        var second = await w.OpenAsync(Piece(1, " sc-7 "));
        var other = await w.OpenAsync(Piece(1, "SC-8"));
        w.Db.LaborBookings.Add(new LaborBooking
        {
            EmployeeId = Karim, Day = Today(), OrderKind = LaborOrderKind.Maintenance, OrderId = second.Id, Hours = 2m,
            PayrollBranchId = Algiers, PayrollYear = Today().Year, PayrollMonth = Today().Month, AttendanceHours = 8m,
        });
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();

        var admin = await w.CallerAsync(UserRole.Admin, Algiers, Chef);
        var piece = await OkAsync(w, admin, "GET", History, $"?productId={Scaffold}&reference=sc-7");
        Assert.True(piece.GetProperty("trackedPerPiece").GetBoolean());
        Assert.Equal(("PerPiece", 1_200m, 1_000m), (piece.GetProperty("mode").GetString(), piece.GetProperty("unitValue").GetDecimal(), piece.GetProperty("perPieceAboveValue").GetDecimal()));
        var jobs = piece.GetProperty("jobs").EnumerateArray().ToList();
        Assert.Equal([first.Number, second.Number], jobs.Select(j => j.GetProperty("number").GetString()));
        Assert.Equal((40m, 0m), (jobs[0].GetProperty("parts").GetDecimal(), jobs[0].GetProperty("labourHours").GetDecimal()));
        Assert.Equal((40m, 900m), (jobs[0].GetProperty("cost").GetDecimal(), jobs[1].GetProperty("cost").GetDecimal()));
        Assert.Equal("BLT-10", jobs[0].GetProperty("partLines")[0].GetProperty("sku").GetString());
        Assert.Equal(["SC-7", "SC-8"], piece.GetProperty("pieces").EnumerateArray().Select(p => p.GetProperty("reference").GetString()));
        Assert.Equal(2, piece.GetProperty("pieces")[0].GetProperty("jobs").GetInt32());
        Assert.Equal(940m, piece.GetProperty("cost").GetDecimal());
        // The whole type without a reference: every job.
        Assert.Equal(3, (await OkAsync(w, admin, "GET", History, $"?productId={Scaffold}")).GetProperty("jobs").GetArrayLength());

        // A reader of jobs without maintenance.costs.view sees dates, parts and hours, never the cost.
        var (_, viewer) = await MfgHarness.ManagerAsync(w.Db, Algiers, PermissionCatalog.MaintenanceView);
        var seen = await OkAsync(w, viewer, "GET", History, $"?productId={Scaffold}&reference=SC-7");
        Assert.False(seen.GetProperty("costsShown").GetBoolean());
        Assert.All(seen.GetProperty("jobs").EnumerateArray(), j => Assert.Equal(JsonValueKind.Null, j.GetProperty("cost").ValueKind));
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("cost").ValueKind);
        Assert.Equal(40m, seen.GetProperty("jobs")[0].GetProperty("parts").GetDecimal());

        // A draft a return note opened for several damaged pieces waits until it is split: completing it is refused.
        var draft = new MaintenanceJob
        {
            Number = "MJ-900001", BranchId = Algiers, Kind = MaintenanceJobKind.Internal, Status = MaintenanceJobStatus.Open, ProductId = Scaffold,
            Qty = 2, ClientRequestId = "draft-two",
        };
        w.Db.MaintenanceJobs.Add(draft);
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        var refused = await w.Jobs.CompleteAsync(draft.Id, new CompleteMaintenanceJobRequest(2, 0, null), Second);
        Assert.Equal((409, MaintenanceJobs.PieceReferenceRequiredCode), (refused.Status, refused.Code));
    }
}
