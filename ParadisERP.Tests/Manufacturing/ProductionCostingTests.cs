using System.Text.Json;
using ParadisERP.Application.Manufacturing;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Manufacturing;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Tests.Security;
using Microsoft.EntityFrameworkCore;
using static ParadisERP.Tests.Manufacturing.MfgHarness;
using static ParadisERP.Tests.Manufacturing.ProductionWorld;

namespace ParadisERP.Tests.Manufacturing;

/// <summary>
/// Manufacturing costing, Phase A (docs/manufacturing/costing-plan.md): labour rates read from payroll,
/// the order's cost sheet through its route, who sees which figures, the reports, and that none of it
/// writes anything.
/// </summary>
public class ProductionCostingTests
{
    private const string Costing = "/api/manufacturing/orders/{id:guid}/costing";

    // ---------- labour rates ----------

    private static LabourRates Rates(PosDbContext db) =>
        new(db, new ParadisERP.Infrastructure.Payroll.PayrollSettingsStore(db, new Algeria()), new Algeria());

    private static PayrollRun Run(Guid branch, PayrollRunStatus status, decimal levies = 0m) => new()
    {
        Number = $"RUN-{Guid.NewGuid():N}"[..12], BranchId = branch, PeriodYear = 2026, PeriodMonth = 10, Status = status, EmployerLevies = levies,
    };

    private static PayrollRunLine Line(Guid employee, decimal gross, decimal days, decimal socialEmployer = 0m, decimal overtimeHours = 0m) => new()
    {
        EmployeeId = employee, Gross = gross, DaysPresent = days, SocialEmployer = socialEmployer, AttendanceSource = AttendanceSource.Recorded,
        Elements =
        [
            new PayrollRunLineElement { Key = "base", Kind = PayrollElementKind.Base, Qty = days, Amount = gross, InSocialBase = true },
            .. overtimeHours > 0
                ? new[] { new PayrollRunLineElement { Key = "ot-weekday", Kind = PayrollElementKind.Overtime, Qty = overtimeHours, Amount = 0m } }
                : [],
        ],
    };

    [Fact]
    public async Task Before_the_month_s_run_is_approved_a_rate_is_the_payroll_engine_s_full_month_over_working_hours()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        // A draft run for October does not make the month actual.
        var draft = Run(TillHarness.Algiers, PayrollRunStatus.Draft);
        draft.Lines.Add(Line(w.Karim, 99_999m, 1m));
        s.Db.PayrollRuns.Add(draft);
        await s.Db.SaveChangesAsync();

        var rates = await Rates(s.Db).ForAsync([(w.Karim, 2026, 10), (w.Samir, 2026, 10)], default);

        // 52,000 a month, statutory deductions off as on new runs, no levy in the Algeria preset: 52,000 ÷ (26 × 8).
        Assert.Equal((LabourRateKind.Standard, 250m, LabourRates.ReasonNotApproved),
            (rates[(w.Karim, 2026, 10)].Kind, rates[(w.Karim, 2026, 10)].PerHour, rates[(w.Karim, 2026, 10)].Reason));
        Assert.Equal(Math.Round(48_000m / 208m, 6), Math.Round(rates[(w.Samir, 2026, 10)].PerHour!.Value, 6));
    }

    [Fact]
    public async Task Once_approved_a_rate_is_the_payslips_employer_cost_over_the_hours_they_paid_across_branches()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        // Karim changed branch in October: paid by two branches' runs, both counted. A reversed run is not.
        var algiers = Run(TillHarness.Algiers, PayrollRunStatus.Approved);
        algiers.Lines.Add(Line(w.Karim, 40_000m, 15m, overtimeHours: 10m));
        var other = Run(MainWarehouse, PayrollRunStatus.Paid);
        other.Lines.Add(Line(w.Karim, 20_000m, 7m, socialEmployer: 1_000m));
        var reversed = Run(WarehouseA, PayrollRunStatus.Reversed);
        reversed.Lines.Add(Line(w.Karim, 500_000m, 1m));
        s.Db.PayrollRuns.AddRange(algiers, other, reversed);
        await s.Db.SaveChangesAsync();

        var rate = (await Rates(s.Db).ForAsync([(w.Karim, 2026, 10)], default))[(w.Karim, 2026, 10)];

        // (40,000 + 20,000 + 1,000) ÷ ((15 + 7) days × 8 h + 10 overtime hours) = 61,000 ÷ 186.
        Assert.Equal((LabourRateKind.Actual, 61_000m, 186m, false), (rate.Kind, rate.EmployerCost, rate.PaidHours, rate.WithoutEmployerSocial));
        Assert.Equal(61_000m / 186m, rate.PerHour);
    }

    [Fact]
    public async Task A_run_s_levies_are_shared_by_each_line_s_social_base_and_no_employer_social_is_said()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var run = Run(TillHarness.Algiers, PayrollRunStatus.Approved, levies: 1_000m);
        run.Lines.Add(Line(w.Karim, 60_000m, 20m));
        run.Lines.Add(Line(w.Samir, 40_000m, 20m));
        s.Db.PayrollRuns.Add(run);
        await s.Db.SaveChangesAsync();

        var rates = await Rates(s.Db).ForAsync([(w.Karim, 2026, 10), (w.Samir, 2026, 10)], default);

        Assert.Equal(60_600m, rates[(w.Karim, 2026, 10)].EmployerCost);
        Assert.Equal(40_400m, rates[(w.Samir, 2026, 10)].EmployerCost);
        Assert.True(rates[(w.Karim, 2026, 10)].WithoutEmployerSocial);
    }

    // ---------- the cost sheet ----------

    /// <summary>An order of 100 kg on line A with 105 kg of wire issued and Karim's eight hours booked on its first stage on 20 October.</summary>
    internal static async Task<(JsonElement Order, Guid Id)> WorkedOrderAsync(PosDbContext db, ProductionWorld w)
    {
        var order = await w.CreateOrderAsync(db, w.LineA, 100);
        var id = OrderId(order);
        await OkAsync(db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/movements", Movement("Issue", MaterialId(order), 105), Of(id));
        var (logId, rowId) = await RecordAsync(db, w, "2026-10-20", id, Stage(order, 0).GetProperty("id").GetGuid(), completed: 0m);
        await OkAsync(db, w.AdminPrincipal, "POST", "/api/manufacturing/logs/{id:guid}/rows/{rowId:guid}/bookings",
            JsonSerializer.Serialize(new { employeeId = w.Karim, hours = 8m, overrideReason = (string?)null }), Of(logId), ("rowId", rowId.ToString()));
        return (order, id);
    }

    /// <summary>A line A daily record for the day with one row on the order's stage; its id and the row's.</summary>
    private static async Task<(Guid Log, Guid Row)> RecordAsync(PosDbContext db, ProductionWorld w, string day, Guid order, Guid stage, decimal completed)
    {
        var log = await OkAsync(db, w.AdminPrincipal, "POST", "/api/manufacturing/logs", JsonSerializer.Serialize(new { lineId = w.LineA, day }));
        var logId = log.GetProperty("log").GetProperty("id").GetGuid();
        var saved = await OkAsync(db, w.AdminPrincipal, "PUT", "/api/manufacturing/logs/{id:guid}", JsonSerializer.Serialize(new
        {
            notes = (string?)null,
            rows = new[] { new { id = (Guid?)null, orderId = order, stageId = stage, category = (string?)null, specification = (string?)null,
                plannedQty = 100m, completedQty = completed, rejectedQty = 0m, rejectReason = (string?)null, notes = (string?)null } },
        }), Of(logId));
        return (logId, saved.GetProperty("rows")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task The_cost_sheet_values_what_was_issued_and_worked_and_says_what_is_provisional()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var (_, id) = await WorkedOrderAsync(s.Db, w);

        var sheet = await OkAsync(s.Db, w.AdminPrincipal, "GET", Costing, "", Of(id));

        // 105 kg at the wire's reference cost of 150; 8 hours at Karim's standard 250 (October not approved).
        Assert.Equal((15_750m, 2_000m, 17_750m), (Dec(sheet, "actual", "materials"), Dec(sheet, "actual", "labour"), Dec(sheet, "actual", "total")));
        Assert.Equal((15_750m, 17_750m), (sheet.GetProperty("atLineValue").GetDecimal(), sheet.GetProperty("workInProgress").GetDecimal()));
        Assert.Equal(15_750m, Dec(sheet, "estimate", "materials"));
        // Standard hours: cut 60 min × 2 workers + form 30 min × 1, per batch of 100 kg — 2.5 h; the crew's average standard rate.
        Assert.Equal(2.5m, sheet.GetProperty("standardHours").GetDecimal());
        Assert.Equal(Math.Round((250m + 48_000m / 208m) / 2m, 6), sheet.GetProperty("standardRate").GetDecimal());
        var labour = sheet.GetProperty("labour")[0];
        Assert.Equal(("Standard", 8m, 250m, 2_000m), (labour.GetProperty("basis").GetString(), labour.GetProperty("hours").GetDecimal(),
            labour.GetProperty("rate").GetDecimal(), labour.GetProperty("cost").GetDecimal()));
        var notes = sheet.GetProperty("notes").EnumerateArray().Select(n => n.GetProperty("code").GetString()).ToList();
        Assert.Contains(ProductionCostEngine.NoteReferenceCost, notes);
        Assert.Contains(ProductionCostEngine.NoteStandardRate, notes);
        Assert.Contains(ProductionCostEngine.NoteNoOverhead, notes);
        Assert.True(sheet.GetProperty("perEmployeeMoneyShown").GetBoolean());
        // Provisional, and each element says why: material at reference cost, labour at the standard rate, no overhead rate.
        var statuses = sheet.GetProperty("statuses");
        Assert.Equal(("provisional", "reference_cost", "provisional", "not_set", "none"),
            (sheet.GetProperty("costStatus").GetString(), statuses.GetProperty("materials").GetString(), statuses.GetProperty("labour").GetString(),
             statuses.GetProperty("overhead").GetString(), statuses.GetProperty("expenses").GetString()));
        // The standard rate leaves out the employer social contribution new runs do not compute: said.
        Assert.True(sheet.GetProperty("employerSocialMissing").GetBoolean());
        Assert.True(labour.GetProperty("withoutEmployerSocial").GetBoolean());
        // Phase A's method is the unsigned default: no sheet can read approved, and the sheet says why.
        Assert.Contains(ProductionCostEngine.NotePolicyNotApproved, notes);
    }

    [Fact]
    public async Task An_expense_is_backed_only_once_its_voucher_is_approved_and_the_unsigned_method_keeps_the_sheet_provisional()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var (_, id) = await WorkedOrderAsync(s.Db, w);
        var voucher = new PaymentVoucher { Number = "PV-000801", BranchId = TillHarness.Algiers, Kind = PaymentVoucherKind.Expense, Amount = 12_000m,
            MethodKey = "cash", Status = PaymentVoucherStatus.Pending };
        s.Db.PaymentVouchers.Add(voucher);
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/expenses", JsonSerializer.Serialize(
            new { description = "Galvanising, outside", amount = 12_000m, paymentVoucherId = voucher.Id, clientRequestId = "req-cost-exp-1" }), Of(id));
        static bool Has(JsonElement sheet, string code) => sheet.GetProperty("notes").EnumerateArray().Any(n => n.GetProperty("code").GetString() == code);

        // A pending voucher: the expense is a typed figure, provisional, its amount said.
        var pending = await OkAsync(s.Db, w.AdminPrincipal, "GET", Costing, "", Of(id));
        Assert.Equal("provisional", pending.GetProperty("statuses").GetProperty("expenses").GetString());
        Assert.Equal(12_000m, pending.GetProperty("notes").EnumerateArray()
            .Single(n => n.GetProperty("code").GetString() == ProductionCostEngine.NoteUnsettledExpense).GetProperty("value").GetDecimal());

        // Approved: the expense is recorded — and the sheet is still provisional, because the method is not signed.
        await s.Db.PaymentVouchers.Where(v => v.Id == voucher.Id).ExecuteUpdateAsync(u => u.SetProperty(v => v.Status, PaymentVoucherStatus.Approved));
        var approved = await OkAsync(s.Db, w.AdminPrincipal, "GET", Costing, "", Of(id));
        Assert.Equal(("recorded", "provisional"), (approved.GetProperty("statuses").GetProperty("expenses").GetString(),
            approved.GetProperty("costStatus").GetString()));
        Assert.False(Has(approved, ProductionCostEngine.NoteUnsettledExpense));
        Assert.True(Has(approved, ProductionCostEngine.NotePolicyNotApproved));
        Assert.Equal(PaymentVoucherStatus.Approved, (await s.Db.PaymentVouchers.AsNoTracking().SingleAsync(v => v.Id == voucher.Id)).Status);
    }

    [Fact]
    public async Task Costs_need_their_own_permission_an_employee_s_pay_needs_payroll_and_another_line_s_order_is_not_found()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var (_, id) = await WorkedOrderAsync(s.Db, w);

        // A production manager of line A without manufacturing.costs.view: refused.
        var withoutCosts = Capabilities.Manufacturing.Where(k => k != PermissionCatalog.ManufacturingCostsView).ToArray();
        var (plain, plainPrincipal) = await ManagerAsync(s.Db, null, withoutCosts);
        s.Db.ProductionLineMembers.Add(new ProductionLineMember { LineId = w.LineA, UserId = plain.Id, Role = ProductionLineMemberRole.Manager });
        // With it but without payroll.manage: the order's materials, never its labour money, an employee's rate or pay.
        var (costs, costsPrincipal) = await ManagerAsync(s.Db, null, AllGrants);
        s.Db.ProductionLineMembers.Add(new ProductionLineMember { LineId = w.LineA, UserId = costs.Id, Role = ProductionLineMemberRole.Manager });
        // A manager of line B, with every grant: line A's order is not found.
        var (other, otherPrincipal) = await ManagerAsync(s.Db, null, AllGrants);
        s.Db.ProductionLineMembers.Add(new ProductionLineMember { LineId = w.LineB, UserId = other.Id, Role = ProductionLineMemberRole.Manager });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        await RefusedAsync(s.Db, plainPrincipal, 403, "forbidden", "GET", Costing, "", Of(id));
        await RefusedAsync(s.Db, otherPrincipal, 404, "not_found", "GET", Costing, "", Of(id));
        var sheet = await OkAsync(s.Db, costsPrincipal, "GET", Costing, "", Of(id));
        Assert.False(sheet.GetProperty("perEmployeeMoneyShown").GetBoolean());
        Assert.True(sheet.GetProperty("orderMoneyWithheld").GetBoolean());
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null),
            (sheet.GetProperty("actual").GetProperty("labour").ValueKind, sheet.GetProperty("actual").GetProperty("total").ValueKind));
        // Karim alone worked the order: the hours stay out too.
        var labour = sheet.GetProperty("labour")[0];
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null),
            (labour.GetProperty("rate").ValueKind, labour.GetProperty("cost").ValueKind, labour.GetProperty("hours").ValueKind));
        Assert.Equal(JsonValueKind.Null, sheet.GetProperty("standardRate").ValueKind);
    }

    // ---------- labour money for one order needs pay-rate access (owner, 2026-11-02 and -03; costing-b2.md §8) ----------

    /// <summary>
    /// An order of 100 kg on line A: 105 kg of wire issued, Karim's 8 hours on the cut stage (and Samir's 4 when
    /// asked, his attendance declared per month), then 40 kg made at the output stage, approved and released, so the sheet has its variances.
    /// </summary>
    private static async Task<Guid> ReleasedOrderAsync(PosDbContext db, ProductionWorld w, bool samirToo)
    {
        var order = await w.CreateOrderAsync(db, w.LineA, 100);
        var id = OrderId(order);
        await OkAsync(db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/movements", Movement("Issue", MaterialId(order), 105), Of(id));
        var (cutLog, cutRow) = await RecordAsync(db, w, "2026-10-20", id, Stage(order, 0).GetProperty("id").GetGuid(), completed: 0m);
        const string Bookings = "/api/manufacturing/logs/{id:guid}/rows/{rowId:guid}/bookings";
        await OkAsync(db, w.AdminPrincipal, "POST", Bookings,
            JsonSerializer.Serialize(new { employeeId = w.Karim, hours = 8m, overrideReason = (string?)null }), Of(cutLog), ("rowId", cutRow.ToString()));
        if (samirToo)
            await OkAsync(db, w.AdminPrincipal, "POST", Bookings,
                JsonSerializer.Serialize(new { employeeId = w.Samir, hours = 4m, overrideReason = "Attendance declared per month" }), Of(cutLog), ("rowId", cutRow.ToString()));
        var (formLog, _) = await RecordAsync(db, w, "2026-10-21", id, Stage(order, 1).GetProperty("id").GetGuid(), completed: 40m);
        await OkAsync(db, w.AdminPrincipal, "POST", "/api/manufacturing/logs/{id:guid}/submit", "", Of(formLog));
        var (_, manager) = await AdminAsync(db);
        await OkAsync(db, manager, "POST", "/api/manufacturing/logs/{id:guid}/approve", "", Of(formLog));
        await OkAsync(db, manager, "POST", "/api/manufacturing/orders/{id:guid}/releases", Release(40), Of(id));
        return id;
    }

    /// <summary>A manager of line A with every manufacturing grant, the rates key included, but not payroll.manage.</summary>
    private static async Task<System.Security.Claims.ClaimsPrincipal> CostsReaderAsync(PosDbContext db, ProductionWorld w)
    {
        var (user, principal) = await ManagerAsync(db, null, AllGrants);
        db.ProductionLineMembers.Add(new ProductionLineMember { LineId = w.LineA, UserId = user.Id, Role = ProductionLineMemberRole.Manager });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return principal;
    }

    private static decimal? Num(JsonElement e, string name) =>
        e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetDecimal();

    private static decimal? NoteValue(JsonElement sheet, string code) =>
        sheet.GetProperty("notes").EnumerateArray().Single(n => n.GetProperty("code").GetString() == code) is var n
        && n.GetProperty("value").ValueKind == JsonValueKind.Number ? n.GetProperty("value").GetDecimal() : null;

    [Fact]
    public async Task Without_the_rate_keys_an_order_s_labour_money_and_every_figure_that_gives_it_back_stay_out()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var id = await ReleasedOrderAsync(s.Db, w, samirToo: false);
        var reader = await CostsReaderAsync(s.Db, w);

        var full = await OkAsync(s.Db, w.AdminPrincipal, "GET", Costing, "", Of(id));
        var shown = await OkAsync(s.Db, reader, "GET", Costing, "", Of(id));

        // The Admin: every figure, the split included.
        Assert.Equal((8m, false, false), (full.GetProperty("labourHours").GetDecimal(),
            full.GetProperty("labourHoursWithheld").GetBoolean(), full.GetProperty("labourEstimateWithheld").GetBoolean()));
        Assert.Equal(8m, NoteValue(full, ProductionCostEngine.NoteStandardRate));
        var fullVariances = full.GetProperty("variances");
        var efficiency = fullVariances.GetProperty("labourEfficiency").GetDecimal();
        var rate = fullVariances.GetProperty("labourRate").GetDecimal();
        Assert.Equal(efficiency + rate, fullVariances.GetProperty("labour").GetDecimal());

        // The Admin sees the order's money whole.
        Assert.False(full.GetProperty("orderMoneyWithheld").GetBoolean());
        Assert.NotNull(Num(full, "releasedValue"));
        Assert.NotNull(Num(full.GetProperty("releases")[0], "value"));

        // Without payroll.manage: the order's labour, and every figure it comes back from by subtraction, stay out.
        Assert.True(shown.GetProperty("orderMoneyWithheld").GetBoolean());
        var actual = shown.GetProperty("actual");
        Assert.Equal((null, null), (Num(actual, "labour"), Num(actual, "total")));
        foreach (var figure in new[] { "actualUnitCost", "releasedValue", "workInProgress", "keptInCharges", "spoilage", "estimatedUnitCost" })
            Assert.True(shown.GetProperty(figure).ValueKind == JsonValueKind.Null, figure);
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null),
            (shown.GetProperty("releases")[0].GetProperty("value").ValueKind, shown.GetProperty("releases")[0].GetProperty("unitValue").ValueKind));
        // What is not labour stays: the materials by element and by line, and what is at the line.
        Assert.Equal(Dec(full, "actual", "materials"), Dec(shown, "actual", "materials"));
        Assert.Equal(Dec(full, "actual", "expenses"), Dec(shown, "actual", "expenses"));
        Assert.Equal(full.GetProperty("atLineValue").GetDecimal(), shown.GetProperty("atLineValue").GetDecimal());
        Assert.Equal(full.GetProperty("materials")[0].GetProperty("netValue").GetDecimal(), shown.GetProperty("materials")[0].GetProperty("netValue").GetDecimal());
        // Karim alone worked it: the hours go too — on the sheet, on the line and in the note.
        Assert.Equal((JsonValueKind.Null, true), (shown.GetProperty("labourHours").ValueKind, shown.GetProperty("labourHoursWithheld").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, shown.GetProperty("labour")[0].GetProperty("hours").ValueKind);
        Assert.Null(NoteValue(shown, ProductionCostEngine.NoteStandardRate));
        // No labour variance, split or whole, and no total; the material variances stay.
        var variances = shown.GetProperty("variances");
        Assert.Equal((null, null, null, null), (Num(variances, "labourEfficiency"), Num(variances, "labourRate"), Num(variances, "labour"), Num(variances, "total")));
        Assert.Equal(fullVariances.GetProperty("materialUsage").GetDecimal(), Num(variances, "materialUsage"));
        // The estimate's labour over the standard hours is the crew's rate, however many it averages.
        Assert.True(shown.GetProperty("labourEstimateWithheld").GetBoolean());
        Assert.Equal((null, null), (Num(shown.GetProperty("estimate"), "labour"), Num(shown.GetProperty("estimate"), "total")));
        Assert.Equal(Dec(full, "estimate", "materials"), Dec(shown, "estimate", "materials"));
    }

    [Fact]
    public async Task Without_the_rate_keys_an_order_two_employees_worked_shows_their_hours()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var id = await ReleasedOrderAsync(s.Db, w, samirToo: true);
        var reader = await CostsReaderAsync(s.Db, w);

        var shown = await OkAsync(s.Db, reader, "GET", Costing, "", Of(id));

        Assert.Equal((12m, false), (shown.GetProperty("labourHours").GetDecimal(), shown.GetProperty("labourHoursWithheld").GetBoolean()));
        Assert.Equal(new[] { 4m, 8m }, shown.GetProperty("labour").EnumerateArray().Select(l => l.GetProperty("hours").GetDecimal()).Order());
        Assert.Equal(12m, NoteValue(shown, ProductionCostEngine.NoteStandardRate));
        Assert.Null(Num(shown.GetProperty("variances"), "labourEfficiency"));
    }

    [Fact]
    public async Task Without_the_rate_keys_a_crew_rate_resting_on_one_employee_keeps_the_labour_estimate_too()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        // Samir leaves line A's crew: its standard rate is Karim's alone.
        await s.Db.ProductionLineEmployees.Where(e => e.LineId == w.LineA && e.EmployeeId == w.Samir)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.EndedOn, (DateOnly?)new DateOnly(2026, 9, 30)));
        var id = await ReleasedOrderAsync(s.Db, w, samirToo: false);
        var reader = await CostsReaderAsync(s.Db, w);

        var full = await OkAsync(s.Db, w.AdminPrincipal, "GET", Costing, "", Of(id));
        var shown = await OkAsync(s.Db, reader, "GET", Costing, "", Of(id));

        // The Admin: 2.5 standard hours at Karim's 250.
        Assert.Equal((625m, false), (Dec(full, "estimate", "labour"), full.GetProperty("labourEstimateWithheld").GetBoolean()));
        Assert.NotNull(Num(full.GetProperty("variances"), "labour"));

        // Without payroll.manage: the labour estimate, and every figure it can be read back from, stay out.
        Assert.True(shown.GetProperty("labourEstimateWithheld").GetBoolean());
        var estimate = shown.GetProperty("estimate");
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null),
            (estimate.GetProperty("labour").ValueKind, estimate.GetProperty("total").ValueKind, shown.GetProperty("estimatedUnitCost").ValueKind));
        Assert.Equal(Dec(full, "estimate", "materials"), Dec(shown, "estimate", "materials"));
        var variances = shown.GetProperty("variances");
        Assert.Equal((null, null, null, null), (Num(variances, "labourEfficiency"), Num(variances, "labourRate"),
            Num(variances, "labour"), Num(variances, "total")));
        Assert.Equal(full.GetProperty("variances").GetProperty("materialUsage").GetDecimal(), Num(variances, "materialUsage"));
        // And the order's own total, which gives the labour back.
        Assert.Null(Num(shown.GetProperty("actual"), "total"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_worker_s_rate_and_pay_need_both_the_rates_key_and_payroll_manage_on_every_costing_read(bool ratesView, bool payrollManage)
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var (_, id) = await WorkedOrderAsync(s.Db, w);
        var grants = AllGrants.Where(k => k != PermissionCatalog.ManufacturingCostsRatesView)
            .Concat(ratesView ? [PermissionCatalog.ManufacturingCostsRatesView] : [])
            .Concat(payrollManage ? [PermissionCatalog.PayrollManage] : []).ToArray();
        var (user, principal) = await ManagerAsync(s.Db, null, grants);
        s.Db.ProductionLineMembers.Add(new ProductionLineMember { LineId = w.LineA, UserId = user.Id, Role = ProductionLineMemberRole.Manager });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        var shown = ratesView && payrollManage;

        var sheet = await OkAsync(s.Db, principal, "GET", Costing, "", Of(id));
        Assert.Equal(shown, sheet.GetProperty("perEmployeeMoneyShown").GetBoolean());
        var labour = sheet.GetProperty("labour")[0];
        Assert.Equal(shown, labour.GetProperty("rate").ValueKind == JsonValueKind.Number);
        Assert.Equal(shown, labour.GetProperty("cost").ValueKind == JsonValueKind.Number);
        Assert.Equal(shown, sheet.GetProperty("standardRate").ValueKind == JsonValueKind.Number);

        var absorption = await OkAsync(s.Db, principal, "GET", "/api/manufacturing/reports/labour-absorption", "?year=2026&month=10");
        Assert.Equal(shown, absorption.GetProperty("moneyShown").GetBoolean());
        Assert.Equal(shown, absorption.GetProperty("rows")[0].GetProperty("rate").ValueKind == JsonValueKind.Number);
    }

    [Fact]
    public async Task With_costing_switched_off_nothing_is_computed()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var (_, id) = await WorkedOrderAsync(s.Db, w);
        MfgHarness.Costing = false;
        try
        {
            await RefusedAsync(s.Db, w.AdminPrincipal, 409, "costing_not_enabled", "GET", Costing, "", Of(id));
            await RefusedAsync(s.Db, w.AdminPrincipal, 409, "costing_not_enabled", "GET", "/api/manufacturing/reports/work-in-progress");
            var status = await OkAsync(s.Db, w.AdminPrincipal, "GET", "/api/manufacturing/operations/status");
            Assert.False(status.GetProperty("costingEnabled").GetBoolean());
            // The awaiting-posting report no longer values releases at all.
            var pending = await OkAsync(s.Db, w.AdminPrincipal, "GET", "/api/manufacturing/reports/pending-movements");
            Assert.False(pending.GetProperty("releasesValued").GetBoolean());
        }
        finally { MfgHarness.Costing = true; }
    }

    [Fact]
    public async Task Costing_writes_nothing_anywhere()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var (order, id) = await WorkedOrderAsync(s.Db, w);
        var approved = Run(TillHarness.Algiers, PayrollRunStatus.Approved);
        approved.Lines.Add(Line(w.Karim, 52_000m, 22m));
        s.Db.PayrollRuns.Add(approved);
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        var before = await FingerprintAsync(s.Db);
        await OkAsync(s.Db, w.AdminPrincipal, "GET", Costing, "", Of(id));
        await OkAsync(s.Db, w.AdminPrincipal, "GET", "/api/manufacturing/reports/work-in-progress");
        await OkAsync(s.Db, w.AdminPrincipal, "GET", "/api/manufacturing/reports/labour-absorption", "?year=2026&month=10");
        await OkAsync(s.Db, w.AdminPrincipal, "GET", "/api/manufacturing/reports/pending-movements");
        await OkAsync(s.Db, w.AdminPrincipal, "GET", "/api/manufacturing/reports/material-flow");
        Assert.Equal(before, await FingerprintAsync(s.Db));
    }

    private static async Task<string> FingerprintAsync(PosDbContext db) => string.Join("|",
        string.Join(",", (await db.Products.AsNoTracking().Select(p => new { p.Id, p.CostPrice }).ToListAsync()).OrderBy(p => p.Id).Select(p => $"{p.Id}:{p.CostPrice}")),
        await db.StockMovements.CountAsync(), await db.LedgerEntries.CountAsync(), await db.PayrollRuns.CountAsync(),
        await db.PayrollRunLines.CountAsync(), await db.LaborBookings.CountAsync(), await db.MaterialMovements.CountAsync(),
        await db.OutputReleases.CountAsync(), await db.AttendanceDays.CountAsync());

    // ---------- reports ----------

    [Fact]
    public async Task Work_in_progress_lists_live_orders_and_releases_are_valued_only_for_those_shown_costs()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var (order, id) = await WorkedOrderAsync(s.Db, w);
        // 40 kg approved at the output stage the next day, then released.
        var (logId, _) = await RecordAsync(s.Db, w, "2026-10-21", id, Stage(order, 1).GetProperty("id").GetGuid(), completed: 40m);
        await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/logs/{id:guid}/submit", "", Of(logId));
        var (_, manager) = await AdminAsync(s.Db);
        await OkAsync(s.Db, manager, "POST", "/api/manufacturing/logs/{id:guid}/approve", "", Of(logId));
        await OkAsync(s.Db, manager, "POST", "/api/manufacturing/orders/{id:guid}/releases", Release(40), Of(id));

        var wip = await OkAsync(s.Db, w.AdminPrincipal, "GET", "/api/manufacturing/reports/work-in-progress");
        var row = wip.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("orderId").GetGuid() == id);
        var sheet = await OkAsync(s.Db, w.AdminPrincipal, "GET", Costing, "", Of(id));
        Assert.Equal(sheet.GetProperty("actual").GetProperty("total").GetDecimal(), row.GetProperty("costToDate").GetDecimal());
        Assert.Equal(sheet.GetProperty("releasedValue").GetDecimal(), row.GetProperty("releasedValue").GetDecimal());
        Assert.True(row.GetProperty("releasedValue").GetDecimal() > 0);
        Assert.True(row.GetProperty("provisional").GetBoolean());
        // Shown at three decimals, the releases add up to the released value, and with it to cost to date − WIP.
        Assert.Equal(sheet.GetProperty("releases").EnumerateArray().Sum(r => r.GetProperty("value").GetDecimal()),
            sheet.GetProperty("releasedValue").GetDecimal());
        Assert.Equal(sheet.GetProperty("actual").GetProperty("total").GetDecimal() - sheet.GetProperty("releasedValue").GetDecimal(),
            sheet.GetProperty("workInProgress").GetDecimal());

        // The awaiting-posting report: the release at the preview's value for the Admin; not valued for a holder of
        // manufacturing.reports.view alone — never at the finished product's catalogue cost (400 × 40).
        var pending = await OkAsync(s.Db, w.AdminPrincipal, "GET", "/api/manufacturing/reports/pending-movements");
        var release = pending.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("document").GetString() == "release");
        Assert.Equal(sheet.GetProperty("releases")[0].GetProperty("value").GetDecimal(), release.GetProperty("value").GetDecimal());
        Assert.NotEqual(16_000m, release.GetProperty("value").GetDecimal());
        var (reader, readerPrincipal) = await ManagerAsync(s.Db, null, PermissionCatalog.ManufacturingView, PermissionCatalog.ManufacturingReportsView);
        s.Db.ProductionLineMembers.Add(new ProductionLineMember { LineId = w.LineA, UserId = reader.Id, Role = ProductionLineMemberRole.Supervisor });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        var unvalued = await OkAsync(s.Db, readerPrincipal, "GET", "/api/manufacturing/reports/pending-movements");
        Assert.False(unvalued.GetProperty("releasesValued").GetBoolean());
        Assert.Equal(JsonValueKind.Null, unvalued.GetProperty("releasedValue").ValueKind);
        Assert.Equal(JsonValueKind.Null, unvalued.GetProperty("rows").EnumerateArray()
            .Single(r => r.GetProperty("document").GetString() == "release").GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task Labour_absorption_shows_hours_to_everyone_shown_costs_and_money_only_with_payroll()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        await WorkedOrderAsync(s.Db, w);
        var approved = Run(TillHarness.Algiers, PayrollRunStatus.Approved);
        approved.Lines.Add(Line(w.Karim, 41_600m, 20m));
        s.Db.PayrollRuns.Add(approved);
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        var report = await OkAsync(s.Db, w.AdminPrincipal, "GET", "/api/manufacturing/reports/labour-absorption", "?year=2026&month=10");
        var karim = report.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("employeeId").GetGuid() == w.Karim);
        // 41,600 over 20 days × 8 h = 260 an hour; 8 hours absorbed by the order, the rest stays in the wage charges.
        Assert.Equal(("Actual", 8m, 160m, 41_600m, 260m, 2_080m, 39_520m),
            (karim.GetProperty("basis").GetString(), karim.GetProperty("orderHours").GetDecimal(), karim.GetProperty("paidHours").GetDecimal(),
             karim.GetProperty("employerCost").GetDecimal(), karim.GetProperty("rate").GetDecimal(),
             karim.GetProperty("absorbedCost").GetDecimal(), karim.GetProperty("unabsorbedCost").GetDecimal()));

        var (manager, principal) = await ManagerAsync(s.Db, null, AllGrants);
        s.Db.ProductionLineMembers.Add(new ProductionLineMember { LineId = w.LineA, UserId = manager.Id, Role = ProductionLineMemberRole.Manager });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        var hoursOnly = await OkAsync(s.Db, principal, "GET", "/api/manufacturing/reports/labour-absorption", "?year=2026&month=10");
        Assert.False(hoursOnly.GetProperty("moneyShown").GetBoolean());
        var row = hoursOnly.GetProperty("rows")[0];
        // The hours payroll paid go with the money: over the month's pay they are the rate.
        Assert.Equal((8m, JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null), (row.GetProperty("orderHours").GetDecimal(),
            row.GetProperty("paidHours").ValueKind, row.GetProperty("employerCost").ValueKind, row.GetProperty("absorbedCost").ValueKind));
        await RefusedAsync(s.Db, w.AdminPrincipal, 400, "invalid_period", "GET", "/api/manufacturing/reports/labour-absorption", "?year=2026&month=13");

        // No month asked: the company's month today, on the server's clock in Algiers — never the browser's.
        var algiers = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ParadisERP.Infrastructure.Catalog.StoreDocumentHistory.Zone(new Algeria()));
        var current = await OkAsync(s.Db, w.AdminPrincipal, "GET", "/api/manufacturing/reports/labour-absorption");
        Assert.Equal((algiers.Year, algiers.Month), (current.GetProperty("year").GetInt32(), current.GetProperty("month").GetInt32()));
    }

    [Fact]
    public async Task A_month_payroll_has_not_approved_has_no_employer_cost_yet_never_zero()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        await WorkedOrderAsync(s.Db, w);

        var report = await OkAsync(s.Db, w.AdminPrincipal, "GET", "/api/manufacturing/reports/labour-absorption", "?year=2026&month=10");
        // Absorbed at the standard rate, but what payroll cost — and so what stayed in the charges — is not known.
        Assert.Equal((2_000m, JsonValueKind.Null, JsonValueKind.Null), (report.GetProperty("absorbedCost").GetDecimal(),
            report.GetProperty("employerCost").ValueKind, report.GetProperty("unabsorbedCost").ValueKind));
        Assert.Equal("Standard", report.GetProperty("rows")[0].GetProperty("basis").GetString());
    }

    [Fact]
    public async Task Material_flow_ties_documents_to_stock_and_names_what_does_not_match()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var (order, id) = await WorkedOrderAsync(s.Db, w);

        // The world's opening stock was written without a movement: the line's balance differs by exactly that.
        var report = await OkAsync(s.Db, w.AdminPrincipal, "GET", "/api/manufacturing/reports/material-flow");
        var issues = report.GetProperty("issues").EnumerateArray().ToList();
        var balance = Assert.Single(issues, i => i.GetProperty("kind").GetString() == "stock_balance_mismatch"
            && i.GetProperty("locationId").GetGuid() == WarehouseA);
        Assert.Equal((-105m, 195m), (balance.GetProperty("expected").GetDecimal(), balance.GetProperty("actual").GetDecimal()));
        Assert.DoesNotContain(issues, i => i.GetProperty("kind").GetString() is "stock_movement_orphan" or "document_without_stock_movement"
            or "stock_movement_quantity" || i.GetProperty("kind").GetString()!.StartsWith("order_material_"));

        // A counter edited behind the documents' back, and a manufacturing stock movement no document names.
        var material = MaterialId(order);
        await s.Db.ProductionOrderMaterials.Where(m => m.Id == material).ExecuteUpdateAsync(u => u.SetProperty(m => m.IssuedBaseQty, 110m));
        s.Db.StockMovements.Add(new StockMovement { ProductId = w.Wire, BranchId = WarehouseA, QtyDelta = -1m, Reason = ProductionMovements.IssueReason, DocumentNumber = "MI-999999" });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        issues = (await OkAsync(s.Db, w.AdminPrincipal, "GET", "/api/manufacturing/reports/material-flow")).GetProperty("issues").EnumerateArray().ToList();
        var counter = Assert.Single(issues, i => i.GetProperty("kind").GetString() == "order_material_issued");
        Assert.Equal((105m, 110m, id), (counter.GetProperty("expected").GetDecimal(), counter.GetProperty("actual").GetDecimal(), counter.GetProperty("orderId").GetGuid()));
        var orphan = Assert.Single(issues, i => i.GetProperty("kind").GetString() == "stock_movement_orphan");
        Assert.Equal("MI-999999", orphan.GetProperty("reference").GetString());
    }

    // ---------- lines' sums stand in for an order's labour money (owner, 2026-11-03) ----------

    private const string Wip = "/api/manufacturing/reports/work-in-progress";
    private const string Absorption = "/api/manufacturing/reports/labour-absorption";

    /// <summary>A second live order on line A, worked by Samir alone on another day (his attendance is declared per month).</summary>
    private static async Task<Guid> SamirOrderAsync(PosDbContext db, ProductionWorld w)
    {
        var order = await w.CreateOrderAsync(db, w.LineA, 50);
        var id = OrderId(order);
        await OkAsync(db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/movements", Movement("Issue", MaterialId(order), 10), Of(id));
        var (log, row) = await RecordAsync(db, w, "2026-10-19", id, Stage(order, 0).GetProperty("id").GetGuid(), completed: 0m);
        await OkAsync(db, w.AdminPrincipal, "POST", "/api/manufacturing/logs/{id:guid}/rows/{rowId:guid}/bookings",
            JsonSerializer.Serialize(new { employeeId = w.Samir, hours = 6m, overrideReason = "Attendance declared per month" }), Of(log), ("rowId", row.ToString()));
        return id;
    }

    private static JsonElement LineOf(JsonElement report, Guid line) =>
        report.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("lineId").GetGuid() == line);

    [Fact]
    public async Task Work_in_progress_without_the_rate_keys_gives_a_line_s_sums_only_from_two_orders_and_two_workers()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var first = await ReleasedOrderAsync(s.Db, w, samirToo: true);    // Karim and Samir
        var reader = await CostsReaderAsync(s.Db, w);

        // One live order on line A: the line's sums would be that order's, and the report's total that line's.
        var one = await OkAsync(s.Db, reader, "GET", Wip);
        Assert.False(one.GetProperty("moneyShown").GetBoolean());
        var row = one.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("orderId").GetGuid() == first);
        Assert.Equal((null, null, null), (Num(row, "costToDate"), Num(row, "releasedValue"), Num(row, "workInProgress")));
        Assert.NotNull(Num(row, "atLineValue"));
        var lineA = LineOf(one, w.LineA);
        Assert.Equal((1, 2, true), (lineA.GetProperty("orders").GetInt32(), lineA.GetProperty("workers").GetInt32(), lineA.GetProperty("withheld").GetBoolean()));
        Assert.Equal((null, null), (Num(lineA, "costToDate"), Num(one, "costToDate")));

        // A second order, Samir's: two orders and two workers, and the line's sums add its orders up.
        await SamirOrderAsync(s.Db, w);
        var two = await OkAsync(s.Db, reader, "GET", Wip);
        var full = await OkAsync(s.Db, w.AdminPrincipal, "GET", Wip);
        lineA = LineOf(two, w.LineA);
        Assert.Equal((2, 2, false), (lineA.GetProperty("orders").GetInt32(), lineA.GetProperty("workers").GetInt32(), lineA.GetProperty("withheld").GetBoolean()));
        var adminRows = full.GetProperty("rows").EnumerateArray().Where(r => r.GetProperty("lineId").GetGuid() == w.LineA).ToList();
        Assert.Equal(adminRows.Sum(r => r.GetProperty("costToDate").GetDecimal()), Num(lineA, "costToDate"));
        Assert.Equal(adminRows.Sum(r => r.GetProperty("workInProgress").GetDecimal()), Num(lineA, "workInProgress"));
        Assert.All(two.GetProperty("rows").EnumerateArray(), r => Assert.Null(Num(r, "costToDate")));
        Assert.Equal(Num(full, "costToDate"), Num(two, "costToDate"));

        // The Admin sees every order's own figures, and every line's sums.
        Assert.True(full.GetProperty("moneyShown").GetBoolean());
        Assert.All(adminRows, r => Assert.NotNull(Num(r, "costToDate")));
        Assert.False(LineOf(full, w.LineA).GetProperty("withheld").GetBoolean());

        // The awaiting-posting report does not value a release for a reader of costs without the rate keys either:
        // its value is the order's cost less the other releases.
        var pending = await OkAsync(s.Db, reader, "GET", "/api/manufacturing/reports/pending-movements");
        Assert.False(pending.GetProperty("releasesValued").GetBoolean());
        Assert.Equal(JsonValueKind.Null, pending.GetProperty("rows").EnumerateArray()
            .Single(r => r.GetProperty("document").GetString() == "release").GetProperty("value").ValueKind);
        Assert.True((await OkAsync(s.Db, w.AdminPrincipal, "GET", "/api/manufacturing/reports/pending-movements")).GetProperty("releasesValued").GetBoolean());
    }

    [Fact]
    public async Task The_labour_report_gives_each_line_s_labour_for_the_month_from_two_orders_and_two_workers()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        await ReleasedOrderAsync(s.Db, w, samirToo: true);
        var reader = await CostsReaderAsync(s.Db, w);

        var one = await OkAsync(s.Db, reader, "GET", Absorption, "?year=2026&month=10");
        var lineA = LineOf(one, w.LineA);
        Assert.Equal((1, 2, 12m, true), (lineA.GetProperty("orders").GetInt32(), lineA.GetProperty("workers").GetInt32(),
            lineA.GetProperty("orderHours").GetDecimal(), lineA.GetProperty("withheld").GetBoolean()));
        Assert.Null(Num(lineA, "absorbedCost"));
        // The Admin sees it whatever the count, and it is the hours at the month's rates.
        var admin = LineOf(await OkAsync(s.Db, w.AdminPrincipal, "GET", Absorption, "?year=2026&month=10"), w.LineA);
        Assert.False(admin.GetProperty("withheld").GetBoolean());
        Assert.True(Num(admin, "absorbedCost") > 0m);

        await SamirOrderAsync(s.Db, w);
        var two = LineOf(await OkAsync(s.Db, reader, "GET", Absorption, "?year=2026&month=10"), w.LineA);
        var adminTwo = LineOf(await OkAsync(s.Db, w.AdminPrincipal, "GET", Absorption, "?year=2026&month=10"), w.LineA);
        Assert.Equal((2, 2, 18m, false), (two.GetProperty("orders").GetInt32(), two.GetProperty("workers").GetInt32(),
            two.GetProperty("orderHours").GetDecimal(), two.GetProperty("withheld").GetBoolean()));
        Assert.Equal(Num(adminTwo, "absorbedCost"), Num(two, "absorbedCost"));
        // Each employee's money stays the rate holders' alone.
        Assert.All((await OkAsync(s.Db, reader, "GET", Absorption, "?year=2026&month=10")).GetProperty("rows").EnumerateArray(),
            r => Assert.Null(Num(r, "absorbedCost")));
    }

    private static decimal Dec(JsonElement sheet, string group, string element) => sheet.GetProperty(group).GetProperty(element).GetDecimal();
}
