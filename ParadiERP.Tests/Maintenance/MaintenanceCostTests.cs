using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ParadiERP.Application.Contracts;
using ParadiERP.Application.Maintenance;
using ParadiERP.Application.Manufacturing;
using ParadiERP.Application.Regional;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Ledger;
using ParadiERP.Infrastructure.Maintenance;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Tests.Manufacturing;
using ParadiERP.Tests.Security;
using Microsoft.EntityFrameworkCore;
using static ParadiERP.Tests.Maintenance.MaintenanceWorld;
using Engine = ParadiERP.Application.Payroll;

namespace ParadiERP.Tests.Maintenance;

/// <summary>
/// Phase M4 (docs/maintenance/plan.md §3.1, §6.2, §8 row M4): a job's cost through the manufacturing cost engine, unchanged —
/// parts at their frozen values, labour at payroll's rates (standard until the month is approved, actual after) or one
/// workshop rate, outside work, overhead per labour hour — the settings MS-10 to MS-13 and MS-40, labour privacy, and the
/// recharge-against-cost and cost-per-product reports.
/// </summary>
public class MaintenanceCostTests
{
    private const string Cost = "/api/maintenance/jobs/{id:guid}/cost";
    private const string Recharges = "/api/maintenance/reports/recharges";
    private const string Products = "/api/maintenance/reports/products";

    private static readonly Guid Sheet = Guid.Parse("a4a4a4a4-0000-0000-0000-000000000001");
    private static readonly Guid Karim = Guid.Parse("a4a4a4a4-0000-0000-0000-0000000000e1");
    private static readonly Guid Samir = Guid.Parse("a4a4a4a4-0000-0000-0000-0000000000e2");
    private static readonly DateOnly Day = new(2026, 10, 20);

    private sealed class Algeria : IRegionalSettingsProvider
    {
        public RegionalSettings Get() => new() { CountryCode = "DZ", CurrencyCode = "DZD", CurrencyDecimals = 2, TimeZone = "Africa/Algiers" };
    }

    // ---------- the adapter, pure ----------

    /// <summary>MNT-04's job: parts 3,200 (four sheets at 800), 6 h at 450, painting 4,000 by an approved voucher.</summary>
    private static MaintenanceCostInput Mnt04(MaintenanceCostSettings settings, LabourRate? rate = null, bool parts = true) => new(
        Qty: 1m, Completed: true,
        Parts: parts ? [new MaintenanceCostPart(Sheet, "PRT-01", ManufacturingRole.SparePart, "pcs", 4m, 0m, 3_200m, 0m)] : [],
        Bookings: [new CostingBooking(Karim, Guid.NewGuid(), Day, 2026, 10, 6m)],
        Rates: new Dictionary<(Guid, int, int), LabourRate> { [(Karim, 2026, 10)] = rate ?? new LabourRate(450m, LabourRateKind.Actual, null, 72_000m, 160m) },
        Expenses: [new CostingExpense(Guid.NewGuid(), 4_000m, DateTime.UtcNow, Settled: true)],
        Settings: settings);

    [Fact]
    public void The_MNT_04_worked_example_costs_9_900_and_11_385_with_overhead_at_15_percent()
    {
        var plain = MaintenanceCostAdapter.Compute(Mnt04(MaintenanceCostSettings.Default));

        Assert.Equal((3_200m, 2_700m, 4_000m, 0m, 9_900m), (plain.Parts, plain.Labour, plain.OutsideWork, plain.Overhead, plain.Total));
        // MS-10's default leaves overhead out of the cost: it says so, it is not a rate "not set".
        Assert.Equal((MaintenanceCostAdapter.ElementExcluded, ProductionCostEngine.ElementApproved), (plain.Statuses.Overhead, plain.Statuses.Labour));
        Assert.DoesNotContain(plain.Notes, n => n.Code is ProductionCostEngine.NoteNoOverhead or ProductionCostEngine.NoteNoLabourEstimate
            or ProductionCostEngine.NoteNotFrozen);

        // MS-11 is per labour hour: 15 % of 9,900 is 1,485 over the job's 6 h, 247.5 an hour.
        var overhead = MaintenanceCostAdapter.Compute(Mnt04(MaintenanceCostSettings.Default with { OverheadIncluded = true, OverheadPerLabourHour = 247.5m }));
        Assert.Equal((1_485m, 11_385m), (overhead.Overhead, overhead.Total));
        Assert.Equal(ProductionCostEngine.ElementApproved, overhead.Statuses.Overhead);

        // MS-10 "parts and outside work only": the hours stay a wage cost, said in a note with their number.
        var noLabour = MaintenanceCostAdapter.Compute(Mnt04(MaintenanceCostSettings.Default with { LabourIncluded = false, OverheadIncluded = true, OverheadPerLabourHour = 247.5m }));
        Assert.Equal((0m, 0m, 7_200m, 6m), (noLabour.Labour, noLabour.Overhead, noLabour.Total, noLabour.LabourHours));
        Assert.Contains(noLabour.Notes, n => n.Code == MaintenanceCostAdapter.NoteLabourExcluded && n.Value == 6m);
        Assert.Equal(MaintenanceCostAdapter.ElementExcluded, noLabour.Statuses.Labour);

        // MS-12 "one workshop rate": every hour at it, whatever payroll says, and no burden on top.
        var workshop = MaintenanceCostAdapter.Compute(Mnt04(MaintenanceCostSettings.Default with { WorkshopRatePerHour = 500m, EmployerBurdenPercent = 20m },
            new LabourRate(300m, LabourRateKind.Standard, WithoutEmployerSocial: true)));
        Assert.Equal((3_000m, 10_200m), (workshop.Labour, workshop.Total));
        Assert.Contains(workshop.Notes, n => n.Code == MaintenanceCostAdapter.NoteWorkshopRate && n.Value == 500m);
    }

    [Fact]
    public void A_job_s_cost_is_final_only_once_the_accountant_confirmed_and_every_element_is_final_and_the_engine_is_unchanged()
    {
        // The engine's version is the one every B1 sheet was frozen under: the adapter never changes what it computes.
        Assert.Equal("B1.0", ProductionCostEngine.Version);
        var input = Mnt04(MaintenanceCostSettings.Default, parts: false);
        Assert.Equal(ProductionCostEngine.Compute(MaintenanceCostAdapter.InputFor(input)).Actual.Total, MaintenanceCostAdapter.Compute(input).Total);

        var unconfirmed = MaintenanceCostAdapter.Compute(input);
        Assert.Equal(ProductionCostEngine.StatusProvisional, unconfirmed.Status);
        Assert.Contains(unconfirmed.Notes, n => n.Code == ProductionCostEngine.NotePolicyNotApproved);

        var confirmed = MaintenanceCostAdapter.Compute(input with { Settings = MaintenanceCostSettings.Default with { Approved = true } });
        Assert.Equal(ProductionCostEngine.StatusApproved, confirmed.Status);
        // No parts: no reference-cost note.
        Assert.DoesNotContain(confirmed.Notes, n => n.Code == ProductionCostEngine.NoteReferenceCost);

        // A standard rate (payroll not approved) keeps it provisional, confirmed or not.
        var standard = MaintenanceCostAdapter.Compute(Mnt04(MaintenanceCostSettings.Default with { Approved = true },
            new LabourRate(300m, LabourRateKind.Standard, LabourRates()), parts: false));
        Assert.Equal((ProductionCostEngine.StatusProvisional, ProductionCostEngine.ElementProvisional), (standard.Status, standard.Statuses.Labour));
        // Parts at the reference cost do as well, as on a production sheet, until the valuation method is signed.
        Assert.Equal(ProductionCostEngine.StatusProvisional,
            MaintenanceCostAdapter.Compute(Mnt04(MaintenanceCostSettings.Default with { Approved = true })).Status);

        static string LabourRates() => ParadiERP.Infrastructure.Manufacturing.LabourRates.ReasonNotApproved;
    }

    // ---------- a company ----------

    /// <summary>The maintenance world (module on, twenty scaffolds in maintenance), sheets at 800 on the shelf, Karim and Samir of Algiers.</summary>
    private static async Task<MaintenanceWorld> WorldAsync(Action<MaintenanceSettings>? more = null)
    {
        var w = await NewAsync(On(more), inMaintenance: 20);
        w.Db.Products.Add(new Product { Id = Sheet, Sku = "PRT-01", NameFr = "Tôle", NameAr = "صفيحة", NameEn = "Sheet", CostPrice = 800m,
            TrackStock = true, ManufacturingRole = ManufacturingRole.SparePart });
        w.Db.ProductStocks.Add(new ProductStock { ProductId = Sheet, BranchId = Algiers, StockQty = 40m });
        w.Db.Employees.AddRange(
            new Employee { Id = Karim, Number = "E-001", FullName = "Karim Benali", BranchId = Algiers, HiredOn = new DateOnly(2025, 1, 1), BaseRate = 52_000m },
            new Employee { Id = Samir, Number = "E-002", FullName = "Samir Haddad", BranchId = Algiers, HiredOn = new DateOnly(2025, 1, 1), BaseRate = 48_000m });
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        return w;
    }

    /// <summary>An internal job of two scaffolds with MNT-04's parts and painting, and Karim's <paramref name="hours"/> when <paramref name="booked"/>.</summary>
    private static async Task<Guid> JobAsync(MaintenanceWorld w, bool booked = true, decimal hours = 6m, decimal sheets = 4m, decimal painting = 4_000m,
        CreateMaintenanceJobRequest? request = null)
    {
        var job = await w.OpenAsync(request ?? Internal(2));
        var parts = new MaintenanceJobParts(w.Db, new Algeria());
        if (sheets > 0)
            Assert.True((await parts.MoveAsync(job.Id, new MaintenancePartsRequest(Guid.NewGuid().ToString("N"), "Issue",
                [new MaintenancePartLineRequest(Sheet, sheets)], null), Chef)).Ok);
        if (painting > 0)
            Assert.True((await parts.AddExpenseAsync(job.Id, new MaintenanceJobExpenseRequest(Guid.NewGuid().ToString("N"), "Peinture", painting, null),
                Chef, true)).Ok);
        w.Db.ChangeTracker.Clear();
        if (booked) await BookAsync(w, job.Id, Karim, hours);
        return job.Id;
    }

    private static async Task BookAsync(MaintenanceWorld w, Guid job, Guid employee, decimal hours)
    {
        w.Db.LaborBookings.Add(new LaborBooking
        {
            EmployeeId = employee, Day = Day, OrderKind = LaborOrderKind.Maintenance, OrderId = job, Hours = hours,
            PayrollBranchId = Algiers, PayrollYear = 2026, PayrollMonth = 10, AttendanceHours = 8m, UserName = "chef",
        });
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
    }

    /// <summary>Karim's October approved: 20 days of 8 h for an employer cost of 72,000 (gross + social) — 450 an hour.</summary>
    private static async Task PayOctoberAsync(PosDbContext db, decimal social = 12_000m)
    {
        var settings = new Engine.PayrollSettings { WorkingDaysPerMonth = 26m, HoursPerDay = 8m };
        var run = new PayrollRun
        {
            Number = $"RUN-{Guid.NewGuid():N}"[..12], BranchId = Algiers, PeriodYear = 2026, PeriodMonth = 10, Status = PayrollRunStatus.Approved,
            SettingsSnapshotJson = JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };
        run.Lines.Add(new PayrollRunLine
        {
            EmployeeId = Karim, Gross = 72_000m - social, SocialEmployer = social, DaysPresent = 20m, AttendanceSource = AttendanceSource.Recorded,
        });
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task SettingsAsync(MaintenanceWorld w, Action<MaintenanceSettings> change)
    {
        var row = await w.Db.MaintenanceSettings.SingleAsync();
        change(row);
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
    }

    private static async Task<JsonElement> GetAsync(MaintenanceWorld w, ClaimsPrincipal caller, string pattern, string query = "", Guid? id = null)
    {
        var (status, body) = id is Guid job
            ? await w.CallAsync(caller, "GET", pattern, query, ("id", job.ToString()))
            : await w.CallAsync(caller, "GET", pattern, query);
        w.Db.ChangeTracker.Clear();
        Assert.True(status == 200, $"GET {pattern}: {status} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static decimal? Num(JsonElement e, string name) =>
        e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetDecimal();

    private static Task<ClaimsPrincipal> AdminAsync(MaintenanceWorld w) => w.CallerAsync(UserRole.Admin, Algiers, Chef);

    /// <summary>A maintenance manager at Algiers with these keys besides reading jobs and their costs.</summary>
    private static async Task<ClaimsPrincipal> ManagerAsync(MaintenanceWorld w, params string[] more) =>
        (await MfgHarness.ManagerAsync(w.Db, Algiers, [PermissionCatalog.MaintenanceView, PermissionCatalog.MaintenanceCostsView, .. more])).Principal;

    // ---------- payroll's rates ----------

    [Fact]
    public async Task Labour_is_at_the_standard_rate_until_the_month_s_payroll_is_approved_then_at_the_actual_rate()
    {
        await using var w = await WorldAsync();
        var job = await JobAsync(w);
        var admin = await AdminAsync(w);

        var before = await GetAsync(w, admin, Cost, id: job);
        var line = before.GetProperty("labourLines")[0];
        var standardRate = line.GetProperty("rate").GetDecimal();
        Assert.Equal(("Standard", "payroll_not_approved", 6m), (line.GetProperty("rateKind").GetString(), line.GetProperty("reason").GetString(),
            line.GetProperty("hours").GetDecimal()));
        Assert.True(standardRate > 0m);
        Assert.Equal(Math.Round(6m * standardRate, 2, MidpointRounding.AwayFromZero), Num(before, "labour"));
        Assert.Equal(("provisional", "provisional"), (before.GetProperty("status").GetString(), before.GetProperty("statuses").GetProperty("labour").GetString()));
        Assert.Contains(before.GetProperty("notes").EnumerateArray(), n => n.GetProperty("code").GetString() == "labour_standard_rate"
            && n.GetProperty("value").GetDecimal() == 6m);

        await PayOctoberAsync(w.Db);
        var after = await GetAsync(w, admin, Cost, id: job);
        line = after.GetProperty("labourLines")[0];
        Assert.Equal(("Actual", 450m, 2_700m), (line.GetProperty("rateKind").GetString(), line.GetProperty("rate").GetDecimal(), line.GetProperty("cost").GetDecimal()));
        // MNT-04: 3,200 + 6 h × 450 + 4,000.
        Assert.Equal((3_200m, 2_700m, 4_000m, 0m, 9_900m), (Num(after, "parts"), Num(after, "labour"), Num(after, "outsideWork"), Num(after, "overhead"), Num(after, "total")));
        Assert.Equal("approved", after.GetProperty("statuses").GetProperty("labour").GetString());
        // The painting has no voucher yet, and the accountant has not confirmed: still provisional, and said why.
        Assert.Equal("provisional", after.GetProperty("status").GetString());
        var codes = after.GetProperty("notes").EnumerateArray().Select(n => n.GetProperty("code").GetString()).ToList();
        Assert.Contains("expense_unsettled", codes);
        Assert.Contains("costing_policy_not_approved", codes);
        Assert.DoesNotContain("labour_standard_rate", codes);

        // MS-10 with overhead, 247.5 an hour (15 % of 9,900 over 6 h): 11,385.
        await SettingsAsync(w, s => { s.CostIncludes = MaintenanceCostScope.PartsLabourOutsideWorkOverhead; s.OverheadPerLabourHour = 247.5m; });
        var withOverhead = await GetAsync(w, admin, Cost, id: job);
        Assert.Equal((1_485m, 11_385m), (Num(withOverhead, "overhead"), Num(withOverhead, "total")));
    }

    [Fact]
    public async Task The_employer_burden_and_the_expense_VAT_follow_C_06_and_C_07_unless_MS_13_gives_the_job_its_own()
    {
        await using var w = await WorldAsync();
        // Payslips without employer social (as new runs compute none): 72,000 gross, 450 an hour, the burden stands in for it.
        await PayOctoberAsync(w.Db, social: 0m);
        var job = await JobAsync(w, painting: 4_760m);
        var admin = await AdminAsync(w);

        var plain = await GetAsync(w, admin, Cost, id: job);
        Assert.Equal((2_700m, 4_760m), (Num(plain, "labour"), Num(plain, "outsideWork")));
        Assert.True(plain.GetProperty("labourLines")[0].GetProperty("withoutEmployerSocial").GetBoolean());
        Assert.Contains(plain.GetProperty("notes").EnumerateArray(), n => n.GetProperty("code").GetString() == "labour_without_employer_social");

        // Manufacturing › Costing settings: a 20 % burden in reports (C-06) and expenses before 19 % recoverable VAT (C-07).
        w.Db.ManufacturingCostingSettings.Add(new ManufacturingCostingSettings
        {
            EmployerBurdenUse = EmployerBurdenUse.ReportsOnly, EmployerBurdenPercent = 20m,
            ExpenseBasis = ExpenseBasis.BeforeRecoverableVat, ExpenseVatPercent = 19m,
        });
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        var followed = await GetAsync(w, admin, Cost, id: job);
        Assert.Equal((3_240m, 4_000m, 10_440m), (Num(followed, "labour"), Num(followed, "outsideWork"), Num(followed, "total")));
        Assert.Equal((20m, 19m), (Num(followed, "employerBurdenPercent"), Num(followed, "expenseVatPercent")));
        var painting = followed.GetProperty("outsideWorkLines")[0];
        Assert.Equal((4_760m, 4_000m), (painting.GetProperty("recorded").GetDecimal(), painting.GetProperty("counted").GetDecimal()));
        Assert.Contains(followed.GetProperty("notes").EnumerateArray(), n => n.GetProperty("code").GetString() == "labour_employer_burden"
            && n.GetProperty("value").GetDecimal() == 20m);

        // MS-13 "own values": 10 % burden, no VAT taken out.
        await SettingsAsync(w, s => { s.BurdenAndVat = MaintenanceBurdenSource.OwnValues; s.EmployerBurdenPercent = 10m; s.ExpenseVatPercent = 0m; });
        var own = await GetAsync(w, admin, Cost, id: job);
        Assert.Equal((2_970m, 4_760m), (Num(own, "labour"), Num(own, "outsideWork")));
        Assert.Equal("OwnValues", own.GetProperty("burdenAndVat").GetString());
    }

    // ---------- privacy ----------

    [Fact]
    public async Task Without_pay_rate_access_a_job_shows_hours_parts_and_outside_work_but_no_labour_money_or_total_unless_the_workshop_rate_is_used()
    {
        await using var w = await WorldAsync();
        await PayOctoberAsync(w.Db);
        var job = await JobAsync(w);

        var reader = await ManagerAsync(w);
        var payrollOnly = await ManagerAsync(w, PermissionCatalog.PayrollManage);
        var rates = await ManagerAsync(w, PermissionCatalog.PayrollManage, PermissionCatalog.ManufacturingCostsRatesView);

        foreach (var withheldFrom in new[] { reader, payrollOnly })
        {
            var hidden = await GetAsync(w, withheldFrom, Cost, id: job);
            Assert.False(hidden.GetProperty("labourMoneyShown").GetBoolean());
            Assert.Equal(((decimal?)null, (decimal?)null), (Num(hidden, "labour"), Num(hidden, "total")));
            Assert.Equal((3_200m, 4_000m, 0m, 6m), (Num(hidden, "parts"), Num(hidden, "outsideWork"), Num(hidden, "overhead"), Num(hidden, "labourHours")));
            var line = hidden.GetProperty("labourLines")[0];
            Assert.Equal((6m, JsonValueKind.Null, JsonValueKind.Null), (line.GetProperty("hours").GetDecimal(),
                line.GetProperty("rate").ValueKind, line.GetProperty("cost").ValueKind));
            Assert.DoesNotContain("2700", hidden.GetRawText());
            Assert.DoesNotContain("9900", hidden.GetRawText());
        }

        var full = await GetAsync(w, rates, Cost, id: job);
        Assert.True(full.GetProperty("labourMoneyShown").GetBoolean());
        Assert.Equal((2_700m, 9_900m), (Num(full, "labour"), Num(full, "total")));

        // MS-12 "one workshop rate": a published rate gives no one's pay back, so the reader sees the whole cost.
        await SettingsAsync(w, s => { s.LabourRate = MaintenanceLabourRate.WorkshopRate; s.WorkshopRatePerHour = 500m; });
        var workshop = await GetAsync(w, reader, Cost, id: job);
        Assert.True(workshop.GetProperty("labourMoneyShown").GetBoolean());
        Assert.Equal((3_000m, 10_200m, 500m), (Num(workshop, "labour"), Num(workshop, "total"), Num(workshop, "workshopRatePerHour")));
        Assert.Equal(("Workshop", 500m), (workshop.GetProperty("labourLines")[0].GetProperty("rateKind").GetString(),
            workshop.GetProperty("labourLines")[0].GetProperty("rate").GetDecimal()));

        // MS-10 "parts and outside work only": no labour money in the cost at all, nothing to withhold.
        await SettingsAsync(w, s => { s.LabourRate = MaintenanceLabourRate.PayrollDerived; s.WorkshopRatePerHour = 0m; s.CostIncludes = MaintenanceCostScope.PartsAndOutsideWorkOnly; });
        var noLabour = await GetAsync(w, reader, Cost, id: job);
        Assert.Equal((0m, 7_200m, 6m, "excluded"), (Num(noLabour, "labour"), Num(noLabour, "total"), Num(noLabour, "labourHours"),
            noLabour.GetProperty("statuses").GetProperty("labour").GetString()));
    }

    [Fact]
    public async Task A_product_s_sum_is_shown_without_rate_access_only_from_two_jobs_with_labour_and_two_workers()
    {
        await using var w = await WorldAsync();
        await PayOctoberAsync(w.Db);
        var reader = await ManagerAsync(w);
        var admin = await AdminAsync(w);

        // One job with parts and painting only: its total has no labour in it, so it is shown.
        var first = await JobAsync(w, booked: false);
        var partsOnly = (await GetAsync(w, reader, Products)).GetProperty("rows")[0];
        Assert.Equal((7_200m, false), (Num(partsOnly, "total"), partsOnly.GetProperty("withheld").GetBoolean()));

        // Karim's 6 h on it: one job, one worker — the product's labour and total would be his.
        await BookAsync(w, first, Karim, 6m);
        var one = await GetAsync(w, reader, Products);
        var row = one.GetProperty("rows")[0];
        Assert.Equal(((decimal?)null, (decimal?)null, true, 3_200m, 4_000m, 6m),
            (Num(row, "labour"), Num(row, "total"), row.GetProperty("withheld").GetBoolean(), Num(row, "parts"), Num(row, "outsideWork"), Num(row, "labourHours")));
        Assert.Equal(((decimal?)null, true), (Num(one, "total"), one.GetProperty("totalsWithheld").GetBoolean()));

        // A second job by Karim alone is two jobs and still one worker.
        var second = await JobAsync(w, booked: false, sheets: 0m, painting: 0m);
        await BookAsync(w, second, Karim, 2m);
        Assert.True((await GetAsync(w, reader, Products)).GetProperty("rows")[0].GetProperty("withheld").GetBoolean());

        // Samir on the second job too: two jobs with labour, two workers — shown, the same figure the Admin sees.
        await BookAsync(w, second, Samir, 2m);
        var two = await GetAsync(w, reader, Products);
        var full = await GetAsync(w, admin, Products);
        row = two.GetProperty("rows")[0];
        Assert.False(row.GetProperty("withheld").GetBoolean());
        Assert.Equal((2, 2), (row.GetProperty("jobs").GetInt32(), row.GetProperty("workers").GetInt32()));
        Assert.Equal(Num(full.GetProperty("rows")[0], "total"), Num(row, "total"));
        Assert.Equal(Num(full, "total"), Num(two, "total"));
        Assert.False(two.GetProperty("totalsWithheld").GetBoolean());
        // Each job on its own still withholds its labour money and total.
        Assert.Null(Num(await GetAsync(w, reader, Cost, id: second), "total"));
    }

    // ---------- recharge against cost ----------

    [Fact]
    public async Task Recharged_jobs_set_their_share_of_the_recharge_against_their_cost_as_MS_26_says()
    {
        await using var w = await WorldAsync();
        // REN-11: eight frames back damaged, 16,000 charged to the customer at return; repaired by a welder for 11,000.
        var (_, _, lineId, _) = await w.ReturnDamagedAsync(8m, 16_000m);
        var a = await JobAsync(w, booked: false, sheets: 0m, painting: 7_000m, request: Recharged(lineId, 5m));
        var b = await JobAsync(w, booked: false, sheets: 0m, painting: 4_000m, request: Recharged(lineId, 3m));
        await JobAsync(w, booked: false, sheets: 0m, painting: 1_000m); // an internal job: not in the report
        var admin = await AdminAsync(w);

        var byUnits = await GetAsync(w, admin, Recharges);
        Assert.Equal("ByUnits", byUnits.GetProperty("rechargeSharing").GetString());
        var rows = byUnits.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("jobId").GetGuid());
        Assert.Equal(2, rows.Count);
        Assert.Equal((16_000m, 10_000m, 7_000m, 3_000m), (Num(rows[a], "lineRecharge"), Num(rows[a], "recharge"), Num(rows[a], "total"), Num(rows[a], "difference")));
        Assert.Equal((6_000m, 4_000m, 2_000m), (Num(rows[b], "recharge"), Num(rows[b], "total"), Num(rows[b], "difference")));
        // The REN-11 figure no report showed: 16,000 − 11,000 = 5,000.
        Assert.Equal((16_000m, 11_000m, 5_000m), (Num(byUnits, "recharge"), Num(byUnits, "total"), Num(byUnits, "difference")));
        var cost = await GetAsync(w, admin, Cost, id: a);
        Assert.Equal((10_000m, 3_000m), (Num(cost, "recharge"), Num(cost, "rechargeLessCost")));

        await SettingsAsync(w, s => s.RechargeSharing = MaintenanceRechargeShare.AllToFirstJob);
        var first = (await GetAsync(w, admin, Recharges)).GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("jobId").GetGuid());
        Assert.Equal((16_000m, 0m, 9_000m, -4_000m), (Num(first[a], "recharge"), Num(first[b], "recharge"), Num(first[a], "difference"), Num(first[b], "difference")));

        // Karim's hours on job A: without rate access its cost and difference go, and the totals with them (one job, one worker).
        await PayOctoberAsync(w.Db);
        await BookAsync(w, a, Karim, 2m);
        var reader = await ManagerAsync(w);
        var hidden = await GetAsync(w, reader, Recharges);
        var hiddenRows = hidden.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("jobId").GetGuid());
        Assert.Equal(((decimal?)null, (decimal?)null, true, 16_000m), (Num(hiddenRows[a], "total"), Num(hiddenRows[a], "difference"),
            hiddenRows[a].GetProperty("withheld").GetBoolean(), Num(hiddenRows[a], "recharge")));
        Assert.Equal((4_000m, -4_000m), (Num(hiddenRows[b], "total"), Num(hiddenRows[b], "difference")));
        Assert.Equal(((decimal?)null, (decimal?)null, true, 16_000m), (Num(hidden, "total"), Num(hidden, "difference"),
            hidden.GetProperty("totalsWithheld").GetBoolean(), Num(hidden, "recharge")));
        Assert.Null(Num(await GetAsync(w, reader, Cost, id: a), "rechargeLessCost"));
        // The Admin sees it: 7,000 + 2 h × 450.
        Assert.Equal(7_900m, Num((await GetAsync(w, admin, Recharges)).GetProperty("rows").EnumerateArray()
            .Single(r => r.GetProperty("jobId").GetGuid() == a), "total"));
    }

    // ---------- the routes ----------

    [Fact]
    public async Task Another_branch_s_job_is_not_found_a_reversed_period_is_refused_and_the_module_off_answers_maintenance_off()
    {
        await using var w = await WorldAsync();
        var job = await JobAsync(w, booked: false);
        var oran = await w.CallerAsync(UserRole.Manager, Oran, Second);
        var admin = await AdminAsync(w);

        Assert.Equal(404, (await w.CallAsync(oran, "GET", Cost, "", ("id", job.ToString()))).Status);
        Assert.Equal(404, (await w.CallAsync(admin, "GET", Cost, "", ("id", Guid.NewGuid().ToString()))).Status);
        // A branch-held reader's report reads their own branch only, whatever branchId says.
        var oranReport = await GetAsync(w, oran, Products, $"?branchId={Algiers}");
        Assert.Equal(0, oranReport.GetProperty("rows").GetArrayLength());

        var (status, body) = await w.CallAsync(admin, "GET", Recharges, "?from=2026-10-31&to=2026-10-01");
        Assert.Equal((400, MaintenanceCosting.InvalidPeriodCode), (status, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString()));
        // A period that holds the job, and one that does not.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.Equal(1, (await GetAsync(w, admin, Products, $"?from={today.AddDays(-1):yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}")).GetProperty("rows").GetArrayLength());
        Assert.Equal(0, (await GetAsync(w, admin, Products, "?from=2020-01-01&to=2020-12-31")).GetProperty("rows").GetArrayLength());

        await SettingsAsync(w, s => s.Module = MaintenanceSwitch.Off);
        foreach (var pattern in new[] { Recharges, Products })
            Assert.Equal((409, MaintenanceJobs.OffCode), await CodeAsync(w.CallAsync(admin, "GET", pattern)));
        Assert.Equal((409, MaintenanceJobs.OffCode), await CodeAsync(w.CallAsync(admin, "GET", Cost, "", ("id", job.ToString()))));

        static async Task<(int, string?)> CodeAsync(Task<(int Status, string Body)> call)
        {
            var (s, b) = await call;
            return (s, JsonDocument.Parse(b).RootElement.GetProperty("code").GetString());
        }
    }

    // ---------- manufacturing untouched ----------

    [Fact]
    public async Task Costing_a_job_leaves_production_s_frozen_B1_sheet_its_hash_and_its_live_cost_exactly_as_they_were()
    {
        await using var s = await MfgHarness.NewAsync();
        var pw = await ProductionWorld.SetupAsync(s.Db);
        s.Db.MaintenanceSettings.Add(new MaintenanceSettings { Module = MaintenanceSwitch.On });
        var pipes = await s.Db.ProductStocks.SingleAsync(x => x.ProductId == TillHarness.Pipe && x.BranchId == TillHarness.Algiers);
        pipes.MaintenanceQty = 2m;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        // A production order Karim worked 2 h on, completed and frozen (B1).
        var order = await pw.CreateOrderAsync(s.Db, pw.LineA, 100m);
        var orderId = ProductionWorld.OrderId(order);
        var log = await MfgHarness.OkAsync(s.Db, pw.AdminPrincipal, "POST", "/api/manufacturing/logs", JsonSerializer.Serialize(new { lineId = pw.LineA, day = "2026-10-20" }));
        var logId = log.GetProperty("log").GetProperty("id").GetGuid();
        var saved = await MfgHarness.OkAsync(s.Db, pw.AdminPrincipal, "PUT", "/api/manufacturing/logs/{id:guid}", JsonSerializer.Serialize(new
        {
            notes = (string?)null,
            rows = new[] { new { id = (Guid?)null, orderId, stageId = ProductionWorld.Stage(order, 0).GetProperty("id").GetGuid(), category = (string?)null,
                specification = (string?)null, plannedQty = 100m, completedQty = 0m, rejectedQty = 0m, rejectReason = (string?)null, notes = (string?)null } },
        }), ProductionWorld.Of(logId));
        await MfgHarness.OkAsync(s.Db, pw.AdminPrincipal, "POST", "/api/manufacturing/logs/{id:guid}/rows/{rowId:guid}/bookings",
            JsonSerializer.Serialize(new { employeeId = pw.Karim, hours = 2m, overrideReason = (string?)null }),
            ProductionWorld.Of(logId), ("rowId", saved.GetProperty("rows")[0].GetProperty("id").GetGuid().ToString()));
        var row = await s.Db.ProductionOrders.SingleAsync(x => x.Id == orderId);
        row.Status = ProductionOrderStatus.Completed;
        row.AcceptedBaseQty = 100m;
        row.CompletedAtUtc = DateTime.UtcNow;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        const string Snapshot = "/api/manufacturing/orders/{id:guid}/cost-snapshot";
        var frozen = await MfgHarness.OkAsync(s.Db, pw.AdminPrincipal, "POST", Snapshot, "{\"requestId\":\"m4-b1\"}", ProductionWorld.Of(orderId));
        var live = await MfgHarness.OkAsync(s.Db, pw.AdminPrincipal, "GET", "/api/manufacturing/orders/{id:guid}/costing", "", ProductionWorld.Of(orderId));

        // A maintenance job with Karim's 3 h the same day, costed.
        var job = (await new MaintenanceJobs(s.Db, new LedgerPoster(s.Db), MaintenanceWorld.Regional).CreateAsync(new CreateMaintenanceJobRequest(Guid.NewGuid().ToString("N"),
            TillHarness.Algiers, "Internal", TillHarness.Pipe, 2m, null, null, "Soudure"), new MaintenanceActor(pw.Admin, "admin"))).Value!.Id;
        s.Db.LaborBookings.Add(new LaborBooking
        {
            EmployeeId = pw.Karim, Day = Day, OrderKind = LaborOrderKind.Maintenance, OrderId = job, Hours = 3m,
            PayrollBranchId = TillHarness.Algiers, PayrollYear = 2026, PayrollMonth = 10, AttendanceHours = 8m,
        });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        var (status, body) = await BranchLockTests.InvokeAsync(s.Db, a => ParadiERP.API.Endpoints.MaintenanceEndpoints.MapMaintenanceEndpoints(a),
            "GET", Cost, pw.AdminPrincipal, "", ("id", job.ToString()));
        s.Db.ChangeTracker.Clear();
        Assert.True(status == 200, body);
        Assert.Equal(3m, JsonDocument.Parse(body).RootElement.GetProperty("labourHours").GetDecimal());

        // The frozen sheet, its hash and the live production figure are what they were.
        var again = await MfgHarness.OkAsync(s.Db, pw.AdminPrincipal, "GET", Snapshot, "", ProductionWorld.Of(orderId));
        Assert.Equal((frozen.GetProperty("sourceHash").GetString(), frozen.GetProperty("totalCost").GetDecimal()),
            (again.GetProperty("sourceHash").GetString(), again.GetProperty("totalCost").GetDecimal()));
        var stored = await s.Db.ManufacturingCostSheets.AsNoTracking().SingleAsync(x => x.OrderId == orderId);
        Assert.Equal(stored.SourceHash, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stored.SourceSnapshotJson!))));
        Assert.Equal(ProductionCostEngine.Version, stored.CalculationVersion);
        var liveAgain = await MfgHarness.OkAsync(s.Db, pw.AdminPrincipal, "GET", "/api/manufacturing/orders/{id:guid}/costing", "", ProductionWorld.Of(orderId));
        Assert.Equal(live.GetProperty("actual").GetRawText(), liveAgain.GetProperty("actual").GetRawText());
        Assert.Equal(2m, liveAgain.GetProperty("labourHours").GetDecimal());
    }
}
