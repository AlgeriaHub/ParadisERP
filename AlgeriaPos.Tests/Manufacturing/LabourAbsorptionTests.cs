using System.Text.Json;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Manufacturing;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Manufacturing;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Tests.Security;
using Microsoft.EntityFrameworkCore;
using static AlgeriaPos.Tests.Manufacturing.MfgHarness;
using static AlgeriaPos.Tests.Manufacturing.ProductionWorld;
using Engine = AlgeriaPos.Application.Payroll;

namespace AlgeriaPos.Tests.Manufacturing;

/// <summary>
/// C-05, labour absorption (owner, 2026-11-09: every accounting decision is a setting the accountant sets in the panel,
/// default today's behaviour). A booking marks how many of its hours were overtime; under "premium to overtime hours"
/// payroll's overtime premium is kept to those hours, the normal hours costed without it.
/// </summary>
public class LabourAbsorptionTests
{
    private static readonly Guid Karim = Guid.NewGuid(), CutStage = Guid.NewGuid();
    private static readonly DateOnly Day1 = new(2026, 10, 20);
    private static readonly DateTime EndOfDay1 = new(2026, 10, 20, 17, 0, 0, DateTimeKind.Utc);

    private static readonly CostingPolicy Blended = new(0m);
    private static readonly CostingPolicy Premium = new(0m, LabourAbsorption: LabourAbsorption.PremiumToOvertime);

    /// <summary>
    /// October paid 20 days (160 h at 100) and 10 overtime hours at 150: 17,500 over 170 hours. The premium is the 50 above
    /// the normal 100 on each overtime hour, 500.
    /// </summary>
    private static LabourRate October(decimal marked = 2m, bool withoutSocial = false) => new(
        17_500m / 170m, LabourRateKind.Actual, null, 17_500m, 170m, withoutSocial,
        OvertimePremium: 500m, PaidOvertimeHours: 10m, MarkedOvertimeHours: marked);

    private static CostingInput Order(CostingPolicy policy, LabourRate rate, decimal hours = 8m, decimal overtime = 2m,
        IReadOnlyList<CostingRelease>? releases = null) => new(
        PlannedBaseQty: 100m, AcceptedBaseQty: 0m, RejectedBaseQty: 0m, Completed: false, StandardHours: 0m, StandardRate: null,
        Materials: [], Bookings: [new CostingBooking(Karim, CutStage, Day1, 2026, 10, hours, overtime)],
        Rates: new Dictionary<(Guid, int, int), LabourRate> { [(Karim, 2026, 10)] = rate },
        Expenses: [], Releases: releases ?? [], Policy: policy);

    // ---------- the engine ----------

    [Fact]
    public void Under_the_blended_rate_a_marked_hour_costs_what_every_hour_costs_exactly_as_before()
    {
        var marked = ProductionCostEngine.Compute(Order(Blended, October()));
        var unmarked = ProductionCostEngine.Compute(Order(Blended, October(), overtime: 0m));

        Assert.Equal(8m * (17_500m / 170m), marked.Actual.Labour);
        Assert.Equal(unmarked.Actual.Labour, marked.Actual.Labour);
        Assert.Equal(marked.Labour[0].Rate, marked.Labour[0].OvertimeRate);
        Assert.DoesNotContain(marked.Notes, n => n.Code.StartsWith("labour_overtime", StringComparison.Ordinal));
    }

    [Fact]
    public void Under_premium_to_overtime_normal_hours_lose_the_premium_and_marked_hours_carry_it()
    {
        var sheet = ProductionCostEngine.Compute(Order(Premium, October()));

        // Normal: (17,500 − 500) ÷ 170 = 100; overtime: 100 + 500 ÷ max(10 paid, 2 marked) = 150. 6 × 100 + 2 × 150.
        Assert.Equal(900m, sheet.Actual.Labour);
        Assert.Equal((100m, 150m, 2m), (sheet.Labour[0].Rate, sheet.Labour[0].OvertimeRate, sheet.Labour[0].OvertimeHours));
        // Eight overtime hours were paid and not marked: their 400 of premium stays in the wage charges, said.
        Assert.Contains(sheet.Notes, n => n.Code == ProductionCostEngine.NoteOvertimePremiumInCharges && n.Value is null);
    }

    [Fact]
    public void With_every_paid_hour_booked_and_every_overtime_hour_marked_the_month_is_absorbed_to_the_dinar()
    {
        var sheet = ProductionCostEngine.Compute(Order(Premium, October(marked: 10m), hours: 170m, overtime: 10m));

        Assert.Equal(17_500m, sheet.Actual.Labour);
        Assert.DoesNotContain(sheet.Notes, n => n.Code == ProductionCostEngine.NoteOvertimePremiumInCharges);
    }

    [Fact]
    public void Marking_more_hours_than_payroll_paid_spreads_the_premium_thinner_never_twice()
    {
        // 20 hours marked in the month against 10 paid: 500 ÷ 20 = 25 a marked hour, so the month absorbs 500 at most.
        var sheet = ProductionCostEngine.Compute(Order(Premium, October(marked: 20m), overtime: 8m));

        Assert.Equal(8m * 125m, sheet.Actual.Labour);
        Assert.Equal(500m, 20m * (sheet.Labour[0].OvertimeRate!.Value - sheet.Labour[0].Rate!.Value));
    }

    [Fact]
    public void Hours_marked_in_a_month_payroll_paid_no_overtime_carry_no_premium_and_say_so()
    {
        var noOvertime = new LabourRate(100m, LabourRateKind.Actual, null, 16_000m, 160m, MarkedOvertimeHours: 2m);
        var standard = new LabourRate(250m, LabourRateKind.Standard, LabourRates.ReasonNotApproved, MarkedOvertimeHours: 2m);

        var paidNone = ProductionCostEngine.Compute(Order(Premium, noOvertime));
        var notApproved = ProductionCostEngine.Compute(Order(Premium, standard));

        Assert.Equal(800m, paidNone.Actual.Labour);
        Assert.Equal(2_000m, notApproved.Actual.Labour);
        Assert.All(new[] { paidNone, notApproved }, s =>
            Assert.Contains(s.Notes, n => n.Code == ProductionCostEngine.NoteOvertimeWithoutPremium && n.Value == 2m));
        Assert.DoesNotContain(paidNone.Notes, n => n.Code == ProductionCostEngine.NoteOvertimePremiumInCharges);
    }

    [Fact]
    public void The_employer_burden_is_added_to_the_premium_too()
    {
        var sheet = ProductionCostEngine.Compute(Order(Premium with { EmployerBurdenPercent = 20m }, October(withoutSocial: true)));

        // 6 × 120 + 2 × 180.
        Assert.Equal(1_080m, sheet.Actual.Labour);
    }

    [Fact]
    public void A_partial_release_carries_the_overtime_rate_of_the_hours_booked_by_its_day()
    {
        CostingRelease[] release = [new(Guid.NewGuid(), "OR-000001", 50m, EndOfDay1, Day1, GoodToDate: 50m)];

        var premium = ProductionCostEngine.Compute(Order(Premium, October(), releases: release));
        var blended = ProductionCostEngine.Compute(Order(Blended, October(), releases: release));

        Assert.Equal(900m, premium.Releases[0].Value);
        Assert.Equal(blended.Actual.Labour, blended.Releases[0].Value);
    }

    // ---------- the record, payroll, the panel and the reports ----------

    private static async Task<(Guid Order, Guid Log, Guid Row)> OrderWithRowAsync(PosDbContext db, ProductionWorld w, Guid? line = null, decimal kg = 100m)
    {
        var order = await w.CreateOrderAsync(db, line ?? w.LineA, kg);
        var id = OrderId(order);
        var log = await OkAsync(db, w.AdminPrincipal, "POST", "/api/manufacturing/logs", JsonSerializer.Serialize(new { lineId = line ?? w.LineA, day = "2026-10-20" }));
        var logId = log.GetProperty("log").GetProperty("id").GetGuid();
        var saved = await OkAsync(db, w.AdminPrincipal, "PUT", "/api/manufacturing/logs/{id:guid}", JsonSerializer.Serialize(new
        {
            notes = (string?)null,
            rows = new[] { new { id = (Guid?)null, orderId = id, stageId = Stage(order, 0).GetProperty("id").GetGuid(), category = (string?)null,
                specification = (string?)null, plannedQty = 100m, completedQty = 0m, rejectedQty = 0m, rejectReason = (string?)null, notes = (string?)null } },
        }), Of(logId));
        return (id, logId, saved.GetProperty("rows")[0].GetProperty("id").GetGuid());
    }

    private static Task<JsonElement> BookAsync(PosDbContext db, ProductionWorld w, Guid log, Guid row, decimal hours, decimal overtime) =>
        OkAsync(db, w.AdminPrincipal, "POST", "/api/manufacturing/logs/{id:guid}/rows/{rowId:guid}/bookings",
            JsonSerializer.Serialize(new { employeeId = w.Karim, hours, overrideReason = (string?)null, overtimeHours = overtime }),
            Of(log), ("rowId", row.ToString()));

    /// <summary>Karim's approved October: 20 days at 800 (100 an hour) and 10 weekday overtime hours at 150, under a 1.5 band.</summary>
    private static async Task PayOctoberAsync(PosDbContext db, Guid karim)
    {
        var settings = new Engine.PayrollSettings
        {
            WorkingDaysPerMonth = 26m, HoursPerDay = 8m,
            OvertimeRules = [new Engine.OvertimeRuleDef { Key = "hs-jour", Multiplier = 1.5m }],
        };
        var run = new PayrollRun
        {
            Number = $"RUN-{Guid.NewGuid():N}"[..12], BranchId = TillHarness.Algiers, PeriodYear = 2026, PeriodMonth = 10,
            Status = PayrollRunStatus.Approved,
            SettingsSnapshotJson = JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };
        run.Lines.Add(new PayrollRunLine
        {
            EmployeeId = karim, Gross = 17_500m, DaysPresent = 20m, AttendanceSource = AttendanceSource.Recorded,
            Elements =
            [
                new PayrollRunLineElement { Key = "base", Kind = PayrollElementKind.Base, Qty = 20m, Amount = 16_000m, InSocialBase = true },
                new PayrollRunLineElement { Key = "hs-jour", Kind = PayrollElementKind.Overtime, Qty = 10m, Rate = 150m, Amount = 1_500m, InSocialBase = true },
            ],
        });
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static UpdateManufacturingCostingSettingsRequest Choose(string labourAbsorption) =>
        new("ByNature", 0m, "StandardMaterialPlusConversion", "None", 0m, "Monthly", "StayInCharges", "ManagementOnly",
            LabourAbsorption: labourAbsorption);

    [Fact]
    public async Task A_booking_records_its_overtime_hours_between_zero_and_its_hours()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var (_, log, row) = await OrderWithRowAsync(s.Db, w);

        await RefusedAsync(s.Db, w.AdminPrincipal, 400, "invalid_overtime_hours", "POST", "/api/manufacturing/logs/{id:guid}/rows/{rowId:guid}/bookings",
            JsonSerializer.Serialize(new { employeeId = w.Karim, hours = 8m, overrideReason = (string?)null, overtimeHours = 9m }),
            Of(log), ("rowId", row.ToString()));
        await RefusedAsync(s.Db, w.AdminPrincipal, 400, "invalid_overtime_hours", "POST", "/api/manufacturing/logs/{id:guid}/rows/{rowId:guid}/bookings",
            JsonSerializer.Serialize(new { employeeId = w.Karim, hours = 8m, overrideReason = (string?)null, overtimeHours = -1m }),
            Of(log), ("rowId", row.ToString()));

        var booked = await BookAsync(s.Db, w, log, row, 8m, 2m);
        Assert.Equal(2m, booked.GetProperty("rows")[0].GetProperty("bookings")[0].GetProperty("overtimeHours").GetDecimal());
        var stored = await s.Db.LaborBookings.AsNoTracking().SingleAsync();
        var change = await s.Db.LaborBookingChanges.AsNoTracking().SingleAsync();
        Assert.Equal((8m, 2m, 2m), (stored.Hours, stored.OvertimeHours, change.OvertimeHours));
    }

    [Fact]
    public async Task An_approved_month_carries_its_overtime_premium_paid_hours_and_the_hours_bookings_marked()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var (_, log, row) = await OrderWithRowAsync(s.Db, w);
        await BookAsync(s.Db, w, log, row, 8m, 2m);
        await PayOctoberAsync(s.Db, w.Karim);

        var rate = (await new LabourRates(s.Db, new AlgeriaPos.Infrastructure.Payroll.PayrollSettingsStore(s.Db, new Algeria()), new Algeria())
            .ForAsync([(w.Karim, 2026, 10)], default))[(w.Karim, 2026, 10)];

        // 1,500 at a 1.5 band: 1,000 is the normal pay of the ten hours, 500 the premium. No employer social on new runs.
        Assert.Equal((LabourRateKind.Actual, 17_500m, 170m, 500m, 10m, 2m),
            (rate.Kind, rate.EmployerCost, rate.PaidHours, rate.OvertimePremium, rate.PaidOvertimeHours, rate.MarkedOvertimeHours));
        Assert.Equal(17_500m / 170m, rate.PerHour);
    }

    [Fact]
    public async Task The_panel_s_choice_is_checked_saved_and_followed_by_the_cost_sheet_and_the_absorption_report()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var (id, log, row) = await OrderWithRowAsync(s.Db, w);
        await BookAsync(s.Db, w, log, row, 8m, 2m);
        await PayOctoberAsync(s.Db, w.Karim);
        var choices = new ManufacturingCostingChoices(s.Db);

        Assert.Equal("BlendedActualRate", (await choices.SettingsAsync()).LabourAbsorption);
        Assert.NotNull((await choices.UpdateSettingsAsync(Choose("Sometimes"), "admin")).Error);

        // The default: every hour at the blended 17,500 ÷ 170, the marked ones too.
        var blended = await OkAsync(s.Db, w.AdminPrincipal, "GET", "/api/manufacturing/orders/{id:guid}/costing", "", Of(id));
        Assert.Equal(Math.Round(8m * 17_500m / 170m, 3), blended.GetProperty("actual").GetProperty("labour").GetDecimal());

        var (saved, error) = await choices.UpdateSettingsAsync(Choose("PremiumToOvertime"), "admin");
        Assert.Null(error);
        Assert.Equal("PremiumToOvertime", saved!.LabourAbsorption);
        s.Db.ChangeTracker.Clear();

        var sheet = await OkAsync(s.Db, w.AdminPrincipal, "GET", "/api/manufacturing/orders/{id:guid}/costing", "", Of(id));
        var karim = sheet.GetProperty("labour")[0];
        Assert.Equal(900m, sheet.GetProperty("actual").GetProperty("labour").GetDecimal());
        Assert.Equal((2m, 100m, 150m), (karim.GetProperty("overtimeHours").GetDecimal(), karim.GetProperty("rate").GetDecimal(),
            karim.GetProperty("overtimeRate").GetDecimal()));
        Assert.Contains(sheet.GetProperty("notes").EnumerateArray(),
            n => n.GetProperty("code").GetString() == ProductionCostEngine.NoteOvertimePremiumInCharges);

        var report = await OkAsync(s.Db, w.AdminPrincipal, "GET", "/api/manufacturing/reports/labour-absorption", "?year=2026&month=10");
        var line = report.GetProperty("rows")[0];
        // The orders absorbed 900; of the 500 premium, the two marked hours carried 100 and 400 stays in the wage charges.
        Assert.Equal((900m, 16_600m, 2m, 10m, 500m, 400m),
            (line.GetProperty("absorbedCost").GetDecimal(), line.GetProperty("unabsorbedCost").GetDecimal(),
             line.GetProperty("overtimeHours").GetDecimal(), line.GetProperty("paidOvertimeHours").GetDecimal(),
             line.GetProperty("overtimePremium").GetDecimal(), line.GetProperty("premiumInCharges").GetDecimal()));
        Assert.Equal(("PremiumToOvertime", 400m), (report.GetProperty("labourAbsorption").GetString(), report.GetProperty("premiumInCharges").GetDecimal()));

        // Labour privacy: without the rate keys the hours stay, the money does not.
        var (manager, principal) = await ManagerAsync(s.Db, null, AllGrants);
        s.Db.ProductionLineMembers.Add(new ProductionLineMember { LineId = w.LineA, UserId = manager.Id, Role = ProductionLineMemberRole.Manager });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        var hoursOnly = (await OkAsync(s.Db, principal, "GET", "/api/manufacturing/reports/labour-absorption", "?year=2026&month=10")).GetProperty("rows")[0];
        Assert.Equal((2m, JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null),
            (hoursOnly.GetProperty("overtimeHours").GetDecimal(), hoursOnly.GetProperty("paidOvertimeHours").ValueKind,
             hoursOnly.GetProperty("overtimePremium").ValueKind, hoursOnly.GetProperty("premiumInCharges").ValueKind));
        var withheld = await OkAsync(s.Db, principal, "GET", "/api/manufacturing/orders/{id:guid}/costing", "", Of(id));
        // One employee worked the order: without the rate keys his hours are withheld too, overtime ones included.
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (withheld.GetProperty("labour")[0].GetProperty("overtimeHours").ValueKind,
            withheld.GetProperty("labour")[0].GetProperty("overtimeRate").ValueKind));
    }

    [Fact]
    public async Task A_frozen_sheet_records_the_choice_only_when_it_is_not_the_default_and_replays_it()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var (id, log, row) = await OrderWithRowAsync(s.Db, w);
        await BookAsync(s.Db, w, log, row, 7.5m, 0m);
        // A second order on line B, half an hour marked overtime; booked before the month is approved, which closes it.
        var (second, log2, row2) = await OrderWithRowAsync(s.Db, w, w.LineB, kg: 1m);
        await BookAsync(s.Db, w, log2, row2, 0.5m, 0.5m);
        await PayOctoberAsync(s.Db, w.Karim);
        await CompleteAsync(s.Db, id);

        // The defaults with nothing marked: no new field in the capture, so it serializes as a sheet frozen before C-05.
        await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/cost-snapshot", "{\"requestId\":\"c05-default\"}", Of(id));
        var plain = (await s.Db.ManufacturingCostSheets.AsNoTracking().SingleAsync(x => x.OrderId == id)).SourceSnapshotJson!;
        Assert.DoesNotContain("LabourAbsorption", plain);
        Assert.DoesNotContain("Overtime", plain);

        // Under the premium choice, the second order: the choice, the marked hours and the month's overtime are captured and replayed.
        await new ManufacturingCostingChoices(s.Db).UpdateSettingsAsync(Choose("PremiumToOvertime"), "admin");
        await CompleteAsync(s.Db, second);
        await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/cost-snapshot", "{\"requestId\":\"c05-premium\"}", Of(second));
        var frozen = await s.Db.ManufacturingCostSheets.AsNoTracking().SingleAsync(x => x.OrderId == second);
        var (source, reason) = CostingCapture.Parse(frozen.SourceSnapshotJson, frozen.CalculationVersion, frozen.SourceState);

        Assert.Null(reason);
        Assert.Equal(LabourAbsorption.PremiumToOvertime, source!.Input.Policy.LabourAbsorption);
        Assert.Equal(0.5m, source.Input.Bookings.Single().OvertimeHours);
        var rate = source.Input.Rates[(w.Karim, 2026, 10)];
        Assert.Equal((500m, 10m, 0.5m), (rate.OvertimePremium, rate.PaidOvertimeHours, rate.MarkedOvertimeHours));
        // Half an hour at 100 + 50: the replay gives the frozen labour back.
        Assert.Equal(75m, ProductionCostEngine.Compute(source.Input).Actual.Labour);
        Assert.Equal(75m, (await s.Db.ManufacturingLabourRateSnapshots.AsNoTracking().SingleAsync(x => x.SheetId == frozen.Id)).TotalCost);
    }

    private static async Task CompleteAsync(PosDbContext db, Guid id)
    {
        var order = await db.ProductionOrders.SingleAsync(x => x.Id == id);
        order.Status = ProductionOrderStatus.Completed;
        order.AcceptedBaseQty = 100m;
        order.CompletedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}
