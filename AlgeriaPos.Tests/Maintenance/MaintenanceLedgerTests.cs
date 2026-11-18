using System.Security.Claims;
using System.Text.Json;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Maintenance;
using AlgeriaPos.Application.Manufacturing;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Maintenance;
using AlgeriaPos.Infrastructure.Manufacturing;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Tests.Manufacturing;
using Microsoft.EntityFrameworkCore;
using static AlgeriaPos.Tests.Maintenance.MaintenanceWorld;
using Engine = AlgeriaPos.Application.Payroll;
using S = AlgeriaPos.Domain.MaintenanceAccountSlots;

namespace AlgeriaPos.Tests.Maintenance;

/// <summary>
/// Phase M6 (docs/maintenance/plan.md §5, §8 row M6): the maintenance section of the practice ledger — parts issued and returned
/// against the stock they left, labour moved into jobs (MS-33), open customer jobs' work in progress (MS-36), customer jobs'
/// invoices for the tie-out — computed on read and written nowhere; the account map's maintenance slots (MS-31), never guessed;
/// a closed month posted through the month poster shared with manufacturing (MS-39), refused, reversed and posted again; parts
/// posted at issue (MS-32); and labour entries only for a reader who sees pay rates.
/// The parts: welding rods carried at 40 (spare parts, so supplies' stock) and bolts at 10 (plain merchandise, so the stock account).
/// </summary>
public class MaintenanceLedgerTests
{
    private const string Ledger = "/api/maintenance/practice-ledger";
    private const string Map = "/api/maintenance/account-map";

    private static readonly Guid Rod = Guid.Parse("a6a6a6a6-0000-0000-0000-000000000001");
    private static readonly Guid Bolt = Guid.Parse("a6a6a6a6-0000-0000-0000-000000000002");
    private static readonly Guid Karim = Guid.Parse("a6a6a6a6-0000-0000-0000-0000000000e1");
    private static readonly DateTime September = new(2026, 9, 15, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime August = new(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc);
    private static string Month(int year, int month) => JsonSerializer.Serialize(new { year, month });

    private sealed class Algeria : IRegionalSettingsProvider
    {
        public RegionalSettings Get() => new() { CountryCode = "DZ", CurrencyCode = "DZD", CurrencyDecimals = 2, TimeZone = "Africa/Algiers" };
    }

    // ---------- the builder, pure ----------

    private static MaintenancePracticeJob Job(string number, bool customer = false, IReadOnlyList<MaintenancePracticeDoc>? docs = null,
        decimal labour = 0m, decimal overhead = 0m, bool openAtStart = false, bool openAtEnd = false, decimal start = 0m, decimal end = 0m) =>
        new(Guid.NewGuid(), number, Algiers, customer, docs ?? [], labour, overhead, null, openAtStart, openAtEnd, start, end);

    [Fact]
    public void Every_entry_balances_parts_tie_to_their_stock_and_each_setting_adds_only_its_own_entries()
    {
        var issue = new MaintenancePracticeDoc("MJI-000001", new DateOnly(2026, 9, 3), false,
            [new(MaintenanceStockGroup.Merchandise, 100m), new(MaintenanceStockGroup.Supplies, 200m)]);
        var back = new MaintenancePracticeDoc("MJR-000001", new DateOnly(2026, 9, 5), true, [new(MaintenanceStockGroup.Supplies, 80m)]);
        var custIssue = new MaintenancePracticeDoc("MJI-000002", new DateOnly(2026, 9, 6), false, [new(MaintenanceStockGroup.RawMaterial, 50m)]);
        IReadOnlyList<MaintenancePracticeJob> jobs =
        [
            Job("MJ-000001", docs: [issue, back], labour: 2_700m, overhead: 300m),
            Job("MJ-000002", customer: true, docs: [custIssue], labour: 900m, openAtStart: true, openAtEnd: true, start: 400m, end: 1_350m),
        ];

        // The defaults: parts only — Dr own repairs / Cr the stock each part left; a customer job's to customer parts.
        var plain = MaintenancePracticeLedger.Build(new(MaintenanceLabourBooks.ReportOnly, MaintenanceOpenJobs.LeftInExpensesListed), 2026, 9, jobs, []);
        Assert.Equal(["parts_issue", "parts_return", "parts_issue"], plain.Entries.Select(e => e.Kind));
        Assert.All(plain.Entries, e => Assert.Equal(e.Lines.Sum(l => l.Debit), e.Lines.Sum(l => l.Credit)));
        Assert.Equal(300m, plain.Entries[0].Lines.Where(l => l.Slot == S.OwnParts).Sum(l => l.Debit));
        Assert.Equal((100m, 200m), (Credit(plain.Entries[0], S.MerchandiseStock), Credit(plain.Entries[0], ManufacturingAccountSlots.SuppliesStock)));
        Assert.Equal(80m, Credit(plain.Entries[1], S.OwnParts));
        Assert.Equal((50m, 50m), (plain.Entries[2].Lines.Single(l => l.Slot == S.CustomerParts).Debit, Credit(plain.Entries[2], ManufacturingAccountSlots.RawMaterialStock)));
        Assert.All(plain.Checks, c => Assert.True(c.Passed, c.Code));
        Assert.Equal(270m, plain.Checks.Single(c => c.Code == MaintenancePracticeLedger.CheckParts).Expected);
        Assert.Contains(MaintenancePracticeLedger.NoteLabourReportOnly, plain.Notes);
        Assert.Contains(MaintenancePracticeLedger.NoteOpenJobsListed, plain.Notes);

        // MS-33 "moved": the month's labour and overhead per job, own equipment to own repairs, a customer's to its service cost.
        var moved = MaintenancePracticeLedger.Build(new(MaintenanceLabourBooks.MovedIntoJobs, MaintenanceOpenJobs.LeftInExpensesListed), 2026, 9, jobs, []);
        var labour = moved.Entries.Where(e => e.Kind == MaintenancePracticeLedger.KindLabour).ToList();
        Assert.Equal((2_700m, 900m), (labour[0].Lines.Single(l => l.Slot == S.OwnParts).Debit, labour[1].Lines.Single(l => l.Slot == S.ServiceCost).Debit));
        Assert.Equal(3_600m, labour.Sum(e => Credit(e, S.LabourAbsorbed)));
        Assert.Equal(300m, Credit(moved.Entries.Single(e => e.Kind == MaintenancePracticeLedger.KindOverhead), S.OverheadAbsorbed));
        Assert.All(moved.Checks, c => Assert.True(c.Passed, c.Code));

        // MS-36 every month: the open customer job's cost to date comes back on the 1st and is carried at the end.
        var wip = MaintenancePracticeLedger.Build(new(MaintenanceLabourBooks.ReportOnly, MaintenanceOpenJobs.WorkInProgressMonthly), 2026, 9, jobs, []);
        var reversal = wip.Entries.Single(e => e.Kind == MaintenancePracticeLedger.KindWipReversal);
        var carried = wip.Entries.Single(e => e.Kind == MaintenancePracticeLedger.KindMonthEndWip);
        Assert.Equal((new DateOnly(2026, 9, 1), 400m), (reversal.Day, Credit(reversal, S.WorkInProgress)));
        Assert.Equal((new DateOnly(2026, 9, 30), 1_350m), (carried.Day, carried.Lines.Single(l => l.Slot == S.WorkInProgress).Debit));
        var tie = wip.Checks.Single(c => c.Code == MaintenancePracticeLedger.CheckWorkInProgress);
        Assert.Equal((true, 950m), (tie.Passed, tie.Actual));
        // Own equipment never carries work in progress; at year end only December's end carries and only January's 1st reverses.
        var yearEnd = new MaintenancePracticeChoices(MaintenanceLabourBooks.ReportOnly, MaintenanceOpenJobs.WorkInProgressAtYearEnd);
        Assert.DoesNotContain(MaintenancePracticeLedger.Build(yearEnd, 2026, 9, jobs, []).Entries, e => e.Kind.Contains("wip"));
        Assert.Equal(["month_end_wip"], MaintenancePracticeLedger.Build(yearEnd, 2026, 12, jobs, []).Entries.Where(e => e.Kind.Contains("wip")).Select(e => e.Kind));
        Assert.Equal(["wip_reversal"], MaintenancePracticeLedger.Build(yearEnd, 2027, 1, jobs, []).Entries.Where(e => e.Kind.Contains("wip")).Select(e => e.Kind));

        // A customer job's invoice names its job, once: counted for the tie-out; one naming no customer job is "to review".
        var invoices = MaintenancePracticeLedger.Build(new(MaintenanceLabourBooks.ReportOnly, MaintenanceOpenJobs.LeftInExpensesListed), 2026, 9, jobs,
            [new("FA-1", new DateOnly(2026, 9, 9), Guid.NewGuid(), "MJ-000002", true, 5_000m), new("FA-2", new DateOnly(2026, 9, 9), null, null, false, 100m)]);
        var invoiceCheck = invoices.Checks.Single(c => c.Code == MaintenancePracticeLedger.CheckInvoices);
        Assert.Equal((false, 2m, 1m), (invoiceCheck.Passed, invoiceCheck.Expected, invoiceCheck.Actual));
    }

    private static decimal Credit(PracticeEntry e, string slot) => e.Lines.Where(l => l.Slot == slot).Sum(l => l.Credit);

    // ---------- a company ----------

    private static async Task<MaintenanceWorld> WorldAsync(Action<MaintenanceSettings>? more = null)
    {
        var w = await NewAsync(On(more), inMaintenance: 10);
        w.Db.Products.AddRange(
            new Product { Id = Rod, Sku = "ROD-25", NameFr = "Baguette", NameAr = "قضيب", NameEn = "Rod", CostPrice = 40m, TrackStock = true,
                ManufacturingRole = ManufacturingRole.SparePart },
            new Product { Id = Bolt, Sku = "BLT-10", NameFr = "Boulon", NameAr = "برغي", NameEn = "Bolt", CostPrice = 10m, TrackStock = true });
        w.Db.ProductStocks.AddRange(
            new ProductStock { ProductId = Rod, BranchId = Algiers, StockQty = 50m },
            new ProductStock { ProductId = Bolt, BranchId = Algiers, StockQty = 100m });
        w.Db.Employees.Add(new Employee { Id = Karim, Number = "E-001", FullName = "Karim Benali", BranchId = Algiers, HiredOn = new DateOnly(2025, 1, 1), BaseRate = 52_000m });
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        return w;
    }

    private static MaintenanceJobParts Parts(PosDbContext db) => new(db, new Algeria());

    private static MaintenancePartsRequest Move(string kind, params (Guid Product, decimal Qty)[] lines) =>
        new(Guid.NewGuid().ToString("N"), kind, lines.Select(l => new MaintenancePartLineRequest(l.Product, l.Qty)).ToList(), null);

    private static async Task MoveAsync(MaintenanceWorld w, Guid job, string kind, params (Guid Product, decimal Qty)[] lines)
    {
        var moved = await Parts(w.Db).MoveAsync(job, Move(kind, lines), Chef);
        Assert.True(moved.Ok, moved.Error);
        w.Db.ChangeTracker.Clear();
    }

    /// <summary>An internal job opened on 1 September: five rods and ten bolts issued, two rods back, all on 15 September.</summary>
    private static async Task<Guid> SeptemberJobAsync(MaintenanceWorld w)
    {
        var job = await w.OpenAsync(Internal(1));
        await MoveAsync(w, job.Id, "Issue", (Rod, 5), (Bolt, 10));
        await MoveAsync(w, job.Id, "Return", (Rod, 2));
        await w.Db.MaintenanceJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(u => u.SetProperty(j => j.CreatedAtUtc, September.AddDays(-14)));
        await w.Db.MaintenanceJobParts.Where(p => p.JobId == job.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.AtUtc, September));
        return job.Id;
    }

    private static async Task SettingsAsync(MaintenanceWorld w, Action<MaintenanceSettings> change)
    {
        var row = await w.Db.MaintenanceSettings.SingleAsync();
        change(row);
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
    }

    private static Task<ClaimsPrincipal> AdminAsync(MaintenanceWorld w) => w.CallerAsync(UserRole.Admin, Algiers, Chef);

    private static async Task<JsonElement> OkAsync(MaintenanceWorld w, ClaimsPrincipal caller, string method, string pattern, string json = "")
    {
        var (status, body) = await w.CallAsync(caller, method, pattern, json);
        w.Db.ChangeTracker.Clear();
        Assert.True(status == 200, $"{method} {pattern}: {status} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task RefusedAsync(MaintenanceWorld w, ClaimsPrincipal caller, int status, string code, string method, string pattern, string json = "")
    {
        var (got, body) = await w.CallAsync(caller, method, pattern, json);
        w.Db.ChangeTracker.Clear();
        Assert.True(got == status, $"{method} {pattern}: {got} {body}");
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    /// <summary>Two active accounts of the chart, for the slots a test maps.</summary>
    private static async Task<(LedgerAccount Repairs, LedgerAccount Supplies)> AccountsAsync(PosDbContext db)
    {
        var repairs = await db.LedgerAccounts.AsNoTracking().SingleAsync(a => a.Code == LedgerAccounts.MaintenanceExpense);
        var other = await db.LedgerAccounts.AsNoTracking().Where(a => a.IsActive && a.Code != LedgerAccounts.MaintenanceExpense && a.Code != LedgerAccounts.Inventory)
            .OrderBy(a => a.Code).FirstAsync();
        return (repairs, other);
    }

    private static async Task MapAsync(MaintenanceWorld w, Guid? ownParts, Guid? supplies)
    {
        Assert.Null((await new MaintenanceAccountMap(w.Db).UpdateAsync(new([new(S.OwnParts, ownParts)]), "comptable")).Error);
        Assert.Null((await new ManufacturingCostingChoices(w.Db).UpdateAccountMapAsync(
            new([new(ManufacturingAccountSlots.SuppliesStock, supplies)]), "comptable")).Error);
        w.Db.ChangeTracker.Clear();
    }

    /// <summary>The books as they stand: every entry, every line's account and amounts (SQLite sums no decimals, so read and added here).</summary>
    private static async Task<string> BooksAsync(PosDbContext db)
    {
        var lines = await db.LedgerEntryLines.AsNoTracking().OrderBy(l => l.Id)
            .Select(l => new { l.Id, l.EntryId, l.AccountId, l.Debit, l.Credit }).ToListAsync();
        return $"{await db.LedgerEntries.CountAsync()}|" + string.Join(';', lines.Select(l => $"{l.Id}:{l.EntryId}:{l.AccountId}:{l.Debit}:{l.Credit}"));
    }

    // ---------- the practice ledger on read ----------

    [Fact]
    public async Task A_month_s_parts_are_entries_on_read_with_unmapped_slots_shown_and_the_books_identical_before_and_after_the_read()
    {
        await using var w = await WorldAsync();
        var job = await SeptemberJobAsync(w);
        var admin = await AdminAsync(w);
        var before = await BooksAsync(w.Db);

        var month = await OkAsync(w, admin, "GET", Ledger, "?year=2026&month=9");
        Assert.Equal(before, await BooksAsync(w.Db));

        var entries = month.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(["parts_issue", "parts_return"], entries.Select(e => e.GetProperty("kind").GetString()));
        Assert.All(entries, e => Assert.Equal(job, e.GetProperty("orderId").GetGuid()));
        // Five rods (200, supplies) and ten bolts (100, merchandise: the stock account, never mapped) out; two rods (80) back.
        Assert.Equal((380m, 380m), (month.GetProperty("totalDebit").GetDecimal(), month.GetProperty("totalCredit").GetDecimal()));
        var merchandise = entries[0].GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("slot").GetString() == S.MerchandiseStock);
        Assert.Equal((LedgerAccounts.Inventory, 100m), (merchandise.GetProperty("accountCode").GetString(), merchandise.GetProperty("credit").GetDecimal()));
        // Never guessed: own repairs and supplies' stock are shown "not mapped", and the mapping check is to review.
        Assert.Equal(new HashSet<string?> { S.OwnParts, ManufacturingAccountSlots.SuppliesStock },
            month.GetProperty("unmappedSlots").EnumerateArray().Select(s => s.GetString()).ToHashSet());
        var checks = month.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("code").GetString()!, c => c.GetProperty("passed").GetBoolean());
        Assert.Equal((true, true, true, false), (checks["entries_balance"], checks["parts_tie"], checks["invoices_tie"], checks["accounts_mapped"]));
        Assert.Contains("labour_report_only", month.GetProperty("notes").EnumerateArray().Select(n => n.GetString()));
        Assert.Contains("policy_not_confirmed", month.GetProperty("notes").EnumerateArray().Select(n => n.GetString()));

        // August saw nothing; a month to come is refused.
        Assert.Empty((await OkAsync(w, admin, "GET", Ledger, "?year=2026&month=8")).GetProperty("entries").EnumerateArray());
        await RefusedAsync(w, admin, 400, "month_in_future", "GET", Ledger, "?year=2099&month=1");
        Assert.Equal(before, await BooksAsync(w.Db));

        // Readers: reports.accounting or maintenance.costs.view, without pay rates while no labour is in the entries.
        var (_, accountant) = await MfgHarness.ManagerAsync(w.Db, Algiers, PermissionCatalog.ReportsAccounting);
        await OkAsync(w, accountant, "GET", Ledger, "?year=2026&month=9");
        var (_, costs) = await MfgHarness.ManagerAsync(w.Db, Algiers, PermissionCatalog.MaintenanceCostsView);
        await OkAsync(w, costs, "GET", Ledger, "?year=2026&month=9");
        var cashier = await w.CallerAsync(UserRole.Cashier, Algiers, Second);
        await RefusedAsync(w, cashier, 403, "forbidden", "GET", Ledger, "?year=2026&month=9");

        // The module off: 409, as every job route; the account map stays reachable beside the settings.
        await SettingsAsync(w, s => s.Module = MaintenanceSwitch.Off);
        await RefusedAsync(w, admin, 409, MaintenanceJobs.OffCode, "GET", Ledger, "?year=2026&month=9");
        await OkAsync(w, admin, "GET", Map);
    }

    [Fact]
    public async Task The_account_map_s_maintenance_slots_are_seeded_empty_mapped_under_their_own_route_and_manufacturing_s_list_is_unchanged()
    {
        await using var w = await WorldAsync();
        var admin = await AdminAsync(w);
        var (repairs, _) = await AccountsAsync(w.Db);

        var map = (await OkAsync(w, admin, "GET", Map)).EnumerateArray().ToList();
        Assert.Equal([.. S.All, .. S.SharedStock], map.Select(m => m.GetProperty("slot").GetString()));
        Assert.All(map, m => Assert.Equal(JsonValueKind.Null, m.GetProperty("ledgerAccountId").ValueKind));
        // The hint is today's account for the same event, never used until the accountant names one.
        Assert.Equal(LedgerAccounts.MaintenanceExpense, map[0].GetProperty("hintCode").GetString());
        Assert.True(map.Single(m => m.GetProperty("slot").GetString() == ManufacturingAccountSlots.SuppliesStock).GetProperty("shared").GetBoolean());

        var saved = await OkAsync(w, admin, "PUT", Map, JsonSerializer.Serialize(new { lines = new[] { new { slot = S.OwnParts, ledgerAccountId = repairs.Id } } }));
        Assert.Equal(LedgerAccounts.MaintenanceExpense, saved[0].GetProperty("accountCode").GetString());
        // Only maintenance's slots here; the stock slots are mapped in Costing settings.
        await RefusedAsync(w, admin, 400, "invalid_account_map", "PUT", Map,
            JsonSerializer.Serialize(new { lines = new[] { new { slot = ManufacturingAccountSlots.SuppliesStock, ledgerAccountId = repairs.Id } } }));
        // Manufacturing's map lists its own slots only, as before.
        Assert.Equal(ManufacturingAccountSlots.All, (await new ManufacturingCostingChoices(w.Db).AccountMapAsync()).Select(m => m.Slot));
    }

    // ---------- posting a month ----------

    [Fact]
    public async Task A_closed_month_posts_once_balanced_is_refused_twice_reversed_by_mirror_entries_and_posted_again()
    {
        await using var w = await WorldAsync();
        await SeptemberJobAsync(w);
        var admin = await AdminAsync(w);
        var (repairs, supplies) = await AccountsAsync(w.Db);
        var september = Month(2026, 9);
        var manufacturing = await w.Db.LedgerEntries.CountAsync(e => e.SourceType == ManufacturingLedgerPostings.SourceType);

        // Nothing posts until the accountant chooses the first month (MS-39), nor while a slot the month uses is unmapped.
        await RefusedAsync(w, admin, 409, "posting_off", "POST", Ledger + "/post", september);
        await SettingsAsync(w, s => s.PostingFromMonth = new DateOnly(2026, 9, 1));
        await RefusedAsync(w, admin, 409, "checks_not_passed", "POST", Ledger + "/post", september);
        await MapAsync(w, repairs.Id, supplies.Id);

        var posted = await OkAsync(w, admin, "POST", Ledger + "/post", september);
        var posting = posted.GetProperty("postings").EnumerateArray().Single();
        Assert.Equal((2, 380m), (posting.GetProperty("entries").GetInt32(), posting.GetProperty("totalDebit").GetDecimal()));
        var entries = await w.Db.LedgerEntries.Include(e => e.Lines).AsNoTracking()
            .Where(e => e.SourceType == MaintenanceLedgerPostings.SourceType).ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(e.Lines.Sum(l => l.Debit), e.Lines.Sum(l => l.Credit)));
        Assert.Equal(300m - 80m, entries.SelectMany(e => e.Lines).Where(l => l.AccountId == repairs.Id).Sum(l => l.Debit - l.Credit));
        Assert.Equal(-120m, entries.SelectMany(e => e.Lines).Where(l => l.AccountId == supplies.Id).Sum(l => l.Debit - l.Credit));
        Assert.Contains(entries, e => e.SourceId == MaintenanceLedgerPostings.EntryId(2026, 9, 1, 0));

        // Refused twice: posted already, and a month before the first; a branch-held accountant cannot post the whole month.
        await RefusedAsync(w, admin, 409, "already_posted", "POST", Ledger + "/post", september);
        await RefusedAsync(w, admin, 409, "before_posting_start", "POST", Ledger + "/post", Month(2026, 8));
        var (_, branchHeld) = await MfgHarness.ManagerAsync(w.Db, Algiers, PermissionCatalog.AccountingManage, PermissionCatalog.ReportsAccounting);
        await RefusedAsync(w, branchHeld, 403, "post_needs_every_branch", "POST", Ledger + "/post", september);

        // October (empty) follows; September cannot be reversed while October stands.
        await OkAsync(w, admin, "POST", Ledger + "/post", Month(2026, 10));
        await RefusedAsync(w, admin, 409, "later_month_posted", "POST", Ledger + "/reverse", september);
        await OkAsync(w, admin, "POST", Ledger + "/reverse", Month(2026, 10));

        // Reversed by mirror entries, never deleted; then the month posts again under its next posting.
        var reversed = await OkAsync(w, admin, "POST", Ledger + "/reverse", september);
        Assert.NotEqual(JsonValueKind.Null, reversed.GetProperty("postings")[0].GetProperty("reversedAtUtc").ValueKind);
        Assert.Equal(2, await w.Db.LedgerEntries.CountAsync(e => e.SourceType == MaintenanceLedgerPostings.SourceType && e.IsReversal));
        var again = await OkAsync(w, admin, "POST", Ledger + "/post", september);
        Assert.Equal(2, again.GetProperty("postings")[1].GetProperty("generation").GetInt32());
        Assert.Equal(4, await w.Db.LedgerEntries.CountAsync(e => e.SourceType == MaintenanceLedgerPostings.SourceType && !e.IsReversal));
        var all = await w.Db.LedgerEntries.Include(e => e.Lines).AsNoTracking().Where(e => e.SourceType == MaintenanceLedgerPostings.SourceType).ToListAsync();
        // Posted, reversed, posted again: the repair account holds the month once.
        Assert.Equal(220m, all.SelectMany(e => e.Lines).Where(l => l.AccountId == repairs.Id).Sum(l => l.Debit - l.Credit));
        // Manufacturing's postings are its own, untouched.
        Assert.Equal(manufacturing, await w.Db.LedgerEntries.CountAsync(e => e.SourceType == ManufacturingLedgerPostings.SourceType));
        Assert.Empty(await w.Db.ManufacturingMonthPostings.ToListAsync());
    }

    // ---------- labour (MS-33) and its privacy ----------

    /// <summary>Karim's October approved: 20 days of 8 h for an employer cost of 72,000 — 450 an hour.</summary>
    private static async Task PayOctoberAsync(PosDbContext db)
    {
        var settings = new Engine.PayrollSettings { WorkingDaysPerMonth = 26m, HoursPerDay = 8m };
        var run = new PayrollRun
        {
            Number = $"RUN-{Guid.NewGuid():N}"[..12], BranchId = Algiers, PeriodYear = 2026, PeriodMonth = 10, Status = PayrollRunStatus.Approved,
            SettingsSnapshotJson = JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };
        run.Lines.Add(new PayrollRunLine { EmployeeId = Karim, Gross = 60_000m, SocialEmployer = 12_000m, DaysPresent = 20m, AttendanceSource = AttendanceSource.Recorded });
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Labour_moved_into_jobs_is_the_month_s_increase_per_job_and_needs_pay_rate_access_unless_valued_at_the_workshop_rate()
    {
        await using var w = await WorldAsync(s => s.LabourInBooks = MaintenanceLabourBooks.MovedIntoJobs);
        var job = await w.OpenAsync(Internal(1));
        await w.Db.MaintenanceJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(u => u.SetProperty(j => j.CreatedAtUtc, September));
        w.Db.LaborBookings.Add(new LaborBooking
        {
            EmployeeId = Karim, Day = new DateOnly(2026, 10, 20), OrderKind = LaborOrderKind.Maintenance, OrderId = job.Id, Hours = 6m,
            PayrollBranchId = Algiers, PayrollYear = 2026, PayrollMonth = 10, AttendanceHours = 8m, UserName = "chef",
        });
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        await PayOctoberAsync(w.Db);

        // Labour at payroll rates in the entries: a reader without pay-rate access is refused the month, as manufacturing's.
        var (_, accountant) = await MfgHarness.ManagerAsync(w.Db, Algiers, PermissionCatalog.ReportsAccounting, PermissionCatalog.PayrollManage);
        await RefusedAsync(w, accountant, 403, CostPrivacy.WithheldCode, "GET", Ledger, "?year=2026&month=10");
        var (_, rates) = await MfgHarness.ManagerAsync(w.Db, Algiers, PermissionCatalog.ReportsAccounting, PermissionCatalog.PayrollManage,
            PermissionCatalog.ManufacturingCostsRatesView);
        var october = await OkAsync(w, rates, "GET", Ledger, "?year=2026&month=10");
        var labour = october.GetProperty("entries").EnumerateArray().Single();
        Assert.Equal(("labour_moved", new DateOnly(2026, 10, 31)), (labour.GetProperty("kind").GetString(), DateOnly.Parse(labour.GetProperty("day").GetString()!)));
        var lines = labour.GetProperty("lines").EnumerateArray().ToList();
        // 6 h at 450: Dr own repairs / Cr labour absorbed, 2,700; September had none of it, November none more.
        Assert.Equal((S.OwnParts, 2_700m, S.LabourAbsorbed, 2_700m),
            (lines[0].GetProperty("slot").GetString(), lines[0].GetProperty("debit").GetDecimal(), lines[1].GetProperty("slot").GetString(), lines[1].GetProperty("credit").GetDecimal()));
        Assert.Empty((await OkAsync(w, rates, "GET", Ledger, "?year=2026&month=9")).GetProperty("entries").EnumerateArray());

        // MS-12 one workshop rate gives nobody's pay back: the accountant without pay rates reads it, at that rate.
        await SettingsAsync(w, s => { s.LabourRate = MaintenanceLabourRate.WorkshopRate; s.WorkshopRatePerHour = 500m; });
        var workshop = await OkAsync(w, accountant, "GET", Ledger, "?year=2026&month=10");
        Assert.Equal(3_000m, workshop.GetProperty("totalDebit").GetDecimal());
        // MS-33 back to "report only": no labour entry, and no rate access needed.
        await SettingsAsync(w, s => { s.LabourRate = MaintenanceLabourRate.PayrollDerived; s.WorkshopRatePerHour = 0m; s.LabourInBooks = MaintenanceLabourBooks.ReportOnly; });
        Assert.Empty((await OkAsync(w, accountant, "GET", Ledger, "?year=2026&month=10")).GetProperty("entries").EnumerateArray());
    }

    // ---------- open customer jobs (MS-36) and invoices ----------

    [Fact]
    public async Task An_open_customer_job_s_cost_to_date_is_carried_in_work_in_progress_reversed_on_the_first_and_its_invoice_ties_out()
    {
        await using var w = await WorldAsync(s => s.OpenJobsAtPeriodEnd = MaintenanceOpenJobs.WorkInProgressMonthly);
        var admin = await AdminAsync(w);
        // Customer jobs are Phase M5's; one written as M5 will write it, open since 1 September.
        var job = new MaintenanceJob
        {
            Number = "MJ-900001", BranchId = Algiers, Kind = MaintenanceJobKind.Customer, Status = MaintenanceJobStatus.Open, ProductId = Scaffold,
            Qty = 1m, ClientRequestId = "customer-1", CreatedAtUtc = September.AddDays(-14), OpenedAtUtc = September.AddDays(-14), OpenedBy = "chef",
        };
        w.Db.MaintenanceJobs.Add(job);
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        await MoveAsync(w, job.Id, "Issue", (Rod, 5));
        await w.Db.MaintenanceJobParts.Where(p => p.JobId == job.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.AtUtc, September));

        var sept = await OkAsync(w, admin, "GET", Ledger, "?year=2026&month=9");
        var kinds = sept.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("kind").GetString()).ToList();
        Assert.Equal(["parts_issue", "month_end_wip"], kinds);
        var issue = sept.GetProperty("entries")[0].GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal((S.CustomerParts, 200m), (issue[0].GetProperty("slot").GetString(), issue[0].GetProperty("debit").GetDecimal()));
        var wip = sept.GetProperty("entries")[1].GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal((S.WorkInProgress, 200m, S.ServiceCost), (wip[0].GetProperty("slot").GetString(), wip[0].GetProperty("debit").GetDecimal(), wip[1].GetProperty("slot").GetString()));
        Assert.All(sept.GetProperty("checks").EnumerateArray().Where(c => c.GetProperty("code").GetString() != "accounts_mapped"),
            c => Assert.True(c.GetProperty("passed").GetBoolean(), c.GetProperty("code").GetString()));

        // Completed and invoiced in October: September's carry comes back on the 1st, nothing carried at the end; the invoice is listed.
        await w.Db.MaintenanceJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(u => u
            .SetProperty(j => j.Status, MaintenanceJobStatus.Invoiced).SetProperty(j => j.CompletedAtUtc, new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc)));
        w.Db.Invoices.Add(new Invoice
        {
            Number = "FA-2026-0001", CustomerId = Contractor, BranchId = Algiers, IssuedAtUtc = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc),
            DueDateUtc = new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc), SourceType = MaintenancePracticeLedgerReport.InvoiceSourceType,
            SourceId = job.Id, SubTotalHt = 5_000m, GrandTotalTtc = 5_000m,
        });
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        var oct = await OkAsync(w, admin, "GET", Ledger, "?year=2026&month=10");
        var reversal = oct.GetProperty("entries").EnumerateArray().Single();
        Assert.Equal(("wip_reversal", new DateOnly(2026, 10, 1)), (reversal.GetProperty("kind").GetString(), DateOnly.Parse(reversal.GetProperty("day").GetString()!)));
        var invoice = oct.GetProperty("invoices").EnumerateArray().Single();
        Assert.Equal(("MJ-900001", true, 5_000m), (invoice.GetProperty("jobNumber").GetString(), invoice.GetProperty("jobMatched").GetBoolean(), invoice.GetProperty("netAmount").GetDecimal()));
        var tie = oct.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("code").GetString() == "wip_tie");
        Assert.True(tie.GetProperty("passed").GetBoolean());

        // The default (left in expenses, listed): no work-in-progress entry, the open job listed with its cost to date.
        await w.Db.MaintenanceJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(u => u
            .SetProperty(j => j.Status, MaintenanceJobStatus.Open).SetProperty(j => j.CompletedAtUtc, (DateTime?)null));
        await SettingsAsync(w, s => s.OpenJobsAtPeriodEnd = MaintenanceOpenJobs.LeftInExpensesListed);
        var listed = await OkAsync(w, admin, "GET", Ledger, "?year=2026&month=9");
        Assert.Equal(["parts_issue"], listed.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("kind").GetString()));
        var open = listed.GetProperty("openCustomerJobs").EnumerateArray().Single();
        Assert.Equal(("MJ-900001", 200m), (open.GetProperty("jobNumber").GetString(), open.GetProperty("costToDate").GetDecimal()));
    }

    // ---------- MS-32 at issue ----------

    [Fact]
    public async Task Parts_at_issue_wait_for_the_own_repairs_account_then_post_one_balanced_entry_per_document_and_leave_the_practice_ledger()
    {
        await using var w = await WorldAsync();
        var store = new MaintenanceSettingsStore(w.Db);
        var current = await store.SettingsAsync();
        var ask = new UpdateMaintenanceSettingsRequest(current.Module, current.UnitsLeaveMaintenance, current.JobsFromReturnNotes,
            current.BookableEmployees, current.JobCloser, current.CustomerJobs, current.CostIncludes, current.OverheadPerLabourHour,
            current.LabourRate, current.WorkshopRatePerHour, current.BurdenAndVat, current.EmployerBurdenPercent, current.ExpenseVatPercent,
            current.CustomerPrice, current.LabourChargePerHour, current.MarkupPercent, current.ReturnRecharge, current.PriceBelowCost,
            current.RechargeSharing, current.OwnRepairs, "AtIssue", current.LabourInBooks, current.CustomerIncome,
            current.Advances, current.OpenJobsAtPeriodEnd, current.Rework, current.RepairHistory, current.PostingFromMonth, false);

        // Never guessed: "at issue" waits for the account of own equipment repairs.
        var refused = await store.UpdateAsync(ask, _ => true, "comptable");
        Assert.Equal((409, MaintenanceSettingsStore.AccountsNotMappedCode), (refused.Status, refused.Code));
        var (repairs, supplies) = await AccountsAsync(w.Db);
        await MapAsync(w, repairs.Id, supplies.Id);
        Assert.Null((await store.UpdateAsync(ask, _ => true, "comptable")).Error);
        w.Db.ChangeTracker.Clear();

        var job = await w.OpenAsync(Internal(1));
        var before = await w.Db.LedgerEntries.CountAsync();
        await MoveAsync(w, job.Id, "Issue", (Rod, 5), (Bolt, 10));
        await MoveAsync(w, job.Id, "Return", (Rod, 2));
        var posted = await w.Db.LedgerEntries.Include(e => e.Lines).AsNoTracking()
            .Where(e => e.SourceType == MaintenancePartsAtIssue.SourceType).ToListAsync();
        Assert.Equal(before + 2, await w.Db.LedgerEntries.CountAsync());
        Assert.Equal(2, posted.Count);
        Assert.All(posted, e => Assert.Equal(e.Lines.Sum(l => l.Debit), e.Lines.Sum(l => l.Credit)));
        var documents = await w.Db.MaintenanceJobParts.AsNoTracking().Select(p => p.DocumentId).Distinct().ToListAsync();
        Assert.All(posted, e => Assert.Contains(e.SourceId, documents));
        var stock = await w.Db.LedgerAccounts.AsNoTracking().SingleAsync(a => a.Code == LedgerAccounts.Inventory);
        var lines = posted.SelectMany(e => e.Lines).ToList();
        // Dr own repairs 300 − 80; Cr supplies 200 − 80; Cr the stock account 100 for the bolts.
        Assert.Equal((220m, -120m, -100m), (Net(lines, repairs.Id), Net(lines, supplies.Id), Net(lines, stock.Id)));

        // Nothing of them in the practice ledger: listed as posted at issue.
        var admin = await AdminAsync(w);
        var month = await OkAsync(w, admin, "GET", Ledger);
        Assert.Empty(month.GetProperty("entries").EnumerateArray());
        Assert.Equal(2, month.GetProperty("postedAtIssue").GetArrayLength());

        // A slot cleared since: the next document posts nothing and waits in the practice ledger, shown "not mapped".
        await MapAsync(w, null, null);
        await MoveAsync(w, job.Id, "Issue", (Rod, 1));
        Assert.Equal(before + 2, await w.Db.LedgerEntries.CountAsync());
        var waiting = await OkAsync(w, admin, "GET", Ledger);
        Assert.Equal(["parts_issue"], waiting.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("kind").GetString()));
        Assert.Contains(S.OwnParts, waiting.GetProperty("unmappedSlots").EnumerateArray().Select(s => s.GetString()));
    }

    private static decimal Net(IEnumerable<LedgerEntryLine> lines, Guid account) => lines.Where(l => l.AccountId == account).Sum(l => l.Debit - l.Credit);
}
