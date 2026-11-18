using System.Text.Json;
using ParadisERP.Application.Contracts;
using ParadisERP.Application.Manufacturing;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Manufacturing;
using Microsoft.EntityFrameworkCore;
using static ParadisERP.Tests.Manufacturing.MfgHarness;
using static ParadisERP.Tests.Manufacturing.ProductionWorld;
using S = ParadisERP.Domain.ManufacturingAccountSlots;

namespace ParadisERP.Tests.Manufacturing;

/// <summary>
/// Phase C2, the practice ledger (owner, 2026-11-09: "yes start C2"): one month of the entries the admin's chosen model
/// would make, computed on read and posted nowhere.
/// </summary>
public class PracticeLedgerTests
{
    private const string Route = "/api/manufacturing/accounting/practice-ledger";
    private static readonly Guid Branch = Guid.NewGuid();

    private static PracticeLedgerChoices Choices(
        ManufacturingAccountingModel model = ManufacturingAccountingModel.ByNature, MonthEndWorkInProgress monthEnd = MonthEndWorkInProgress.Monthly,
        LateCostTreatment late = LateCostTreatment.StayInCharges) => new(model, monthEnd, late, VarianceTreatment.ManagementOnly);

    /// <summary>
    /// The plan's worked example (§4.4) in one month: wire and supplies issued, 4 kg of wire returned, release 1 made, the
    /// order still open at the month's end with 17,750 of work in progress.
    /// </summary>
    private static PracticeOrder Worked(bool closed = false, decimal? release = 22_070m) => new(
        Guid.NewGuid(), "MO-000001", Branch,
        OpenAtStart: false, OpenAtEnd: !closed, ClosedDay: closed ? new DateOnly(2026, 10, 25) : null,
        Start: PracticeCostPoint.Zero,
        End: new PracticeCostPoint(34_900m, 5_300m, 1_400m, 12_000m, release ?? 0m),
        Documents:
        [
            new("MI-000001", new DateOnly(2026, 10, 20), false,
                [new(PracticeMaterialGroup.Raw, 31_500m), new(PracticeMaterialGroup.Supplies, 4_000m)]),
            new("MR-000001", new DateOnly(2026, 10, 21), true, [new(PracticeMaterialGroup.Raw, 600m)]),
        ],
        Releases: release is decimal r ? [new PracticeRelease("OR-000001", new DateOnly(2026, 10, 20), r)] : []);

    private static decimal Net(PracticeLedgerResult r, string slot) =>
        r.Entries.SelectMany(e => e.Lines).Where(l => l.Slot == slot).Sum(l => l.Debit - l.Credit);

    [Fact]
    public void By_nature_issues_go_to_consumption_releases_to_production_stocked_and_open_orders_are_carried_at_month_end()
    {
        var r = PracticeLedger.Build(Choices(), 2026, 10, [Worked()]);

        // §4.4 rows 1–3, 6 and 8: the wire and supplies charged, the return taken back, release 1, the month-end 17,750.
        Assert.Equal(30_900m, Net(r, S.RawMaterialConsumption));
        Assert.Equal(-30_900m, Net(r, S.RawMaterialStock));
        Assert.Equal((4_000m, -4_000m), (Net(r, S.SuppliesConsumption), Net(r, S.SuppliesStock)));
        Assert.Equal(22_070m, Net(r, S.FinishedGoodsStock));
        var wip = Assert.Single(r.Entries, e => e.Kind == PracticeLedger.KindMonthEndWip);
        Assert.Equal((new DateOnly(2026, 10, 31), 31_530m), (wip.Day, Net(r, S.WorkInProgress)));
        // Production stocked takes the release and the month-end value; labour, overhead and expenses stay in their charges.
        Assert.Equal(-(22_070m + 31_530m), Net(r, S.ProductionStocked));
        Assert.DoesNotContain(r.Entries, e => e.Kind is PracticeLedger.KindLabour or PracticeLedger.KindOverhead or PracticeLedger.KindExpenses);
        Assert.All(r.Checks, c => Assert.True(c.Passed, c.Code));
        Assert.Contains(PracticeLedger.NotePracticeOnly, r.Notes);
        Assert.Contains(PracticeLedger.NoteOpenReleasesLive, r.Notes);
    }

    [Fact]
    public void Last_month_s_work_in_progress_comes_back_on_the_1st_and_year_end_only_carries_it_in_December_alone()
    {
        var carried = Worked() with { OpenAtStart = true, Start = new PracticeCostPoint(10_000m, 0m, 0m, 0m, 0m) };

        var monthly = PracticeLedger.Build(Choices(), 2026, 10, [carried]);
        var back = Assert.Single(monthly.Entries, e => e.Kind == PracticeLedger.KindWipReversal);
        Assert.Equal(new DateOnly(2026, 10, 1), back.Day);
        Assert.Equal(new PracticeLine(S.ProductionStocked, 10_000m, 0m), back.Lines[0]);
        Assert.Equal(31_530m - 10_000m, Net(monthly, S.WorkInProgress));

        var yearEnd = Choices(monthEnd: MonthEndWorkInProgress.YearEndOnly);
        Assert.DoesNotContain(PracticeLedger.Build(yearEnd, 2026, 10, [carried]).Entries,
            e => e.Kind is PracticeLedger.KindMonthEndWip or PracticeLedger.KindWipReversal);
        Assert.Single(PracticeLedger.Build(yearEnd, 2026, 12, [carried]).Entries, e => e.Kind == PracticeLedger.KindMonthEndWip);
        Assert.Single(PracticeLedger.Build(yearEnd, 2027, 1, [carried]).Entries, e => e.Kind == PracticeLedger.KindWipReversal);
        Assert.All(PracticeLedger.Build(yearEnd, 2026, 10, [carried]).Checks, c => Assert.True(c.Passed, c.Code));
    }

    [Fact]
    public void Perpetual_work_in_progress_takes_every_cost_as_it_happens_and_a_closed_order_s_remainder_goes_to_the_variance()
    {
        var w = Choices(ManufacturingAccountingModel.WorkInProgress);

        var open = PracticeLedger.Build(w, 2026, 10, [Worked()]);
        // Materials, labour, overhead and expenses in; release 1 out: 34,900 + 18,700 − 22,070.
        Assert.Equal(34_900m + 5_300m + 1_400m + 12_000m - 22_070m, Net(open, S.WorkInProgress));
        Assert.Equal((-5_300m, -1_400m, -12_000m),
            (Net(open, S.LabourAbsorbed), Net(open, S.OverheadAbsorbed), Net(open, S.ExpensesAbsorbed)));
        Assert.Equal(0m, Net(open, S.RawMaterialConsumption));
        Assert.DoesNotContain(open.Entries, e => e.Kind is PracticeLedger.KindMonthEndWip or PracticeLedger.KindCloseResidual);
        Assert.All(open.Checks, c => Assert.True(c.Passed, c.Code));

        // Closed with nothing released: the whole cost leaves work in progress for the variance account, on the closing day.
        var closed = PracticeLedger.Build(w, 2026, 10, [Worked(closed: true, release: null)]);
        var residual = Assert.Single(closed.Entries, e => e.Kind == PracticeLedger.KindCloseResidual);
        Assert.Equal((new DateOnly(2026, 10, 25), 53_600m), (residual.Day, Net(closed, S.Variance)));
        Assert.Equal(0m, Net(closed, S.WorkInProgress));
        Assert.All(closed.Checks, c => Assert.True(c.Passed, c.Code));
    }

    [Fact]
    public void A_cost_after_the_close_is_listed_and_goes_to_the_variance_only_under_the_perpetual_model_with_that_choice()
    {
        var after = Worked(closed: true, release: 53_600m) with
        {
            LateFrom = new PracticeCostPoint(34_900m, 5_300m, 1_400m, 12_000m, 53_600m),
            LateTo = new PracticeCostPoint(34_900m, 5_364m, 1_400m, 12_020m, 53_600m),
        };

        foreach (var model in new[] { ManufacturingAccountingModel.ByNature, ManufacturingAccountingModel.WorkInProgress })
        {
            var stay = PracticeLedger.Build(Choices(model), 2026, 10, [after]);
            Assert.Equal(new[] { ("labour", 64m), ("expenses", 20m) }, stay.LateCosts.Select(l => (l.Element, l.Amount)));
            Assert.DoesNotContain(stay.Entries, e => e.Kind == PracticeLedger.KindLateCost);
        }

        var toVariance = PracticeLedger.Build(Choices(ManufacturingAccountingModel.WorkInProgress, late: LateCostTreatment.VarianceAccount), 2026, 10, [after]);
        Assert.Equal((84m, -64m, -20m), (Net(toVariance, S.Variance), Net(toVariance, S.LabourAbsorbed) + 5_300m, Net(toVariance, S.ExpensesAbsorbed) + 12_000m));
        Assert.All(toVariance.Checks, c => Assert.True(c.Passed, c.Code));
        // By nature they are already in their charges: the choice is said to have nothing to move.
        Assert.Contains(PracticeLedger.NoteLateCostsByNature,
            PracticeLedger.Build(Choices(late: LateCostTreatment.VarianceAccount), 2026, 10, [after]).Notes);
    }

    [Fact]
    public async Task The_month_is_built_from_the_orders_events_followed_into_next_month_and_its_accounts_come_from_the_map()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var (_, id) = await ProductionCostingTests.WorkedOrderAsync(s.Db, w);
        // The order and its 105 kg issue in October, beside Karim's 8 hours of 20 October.
        var october = new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc);
        await s.Db.ProductionOrders.Where(o => o.Id == id).ExecuteUpdateAsync(u => u.SetProperty(o => o.CreatedAtUtc, october));
        await s.Db.MaterialMovements.Where(m => m.OrderId == id).ExecuteUpdateAsync(u => u.SetProperty(m => m.AtUtc, october));

        var oct = await OkAsync(s.Db, w.AdminPrincipal, "GET", Route, "?year=2026&month=10");
        Assert.Equal(("ByNature", false), (oct.GetProperty("model").GetString(), oct.GetProperty("monthToDate").GetBoolean()));
        // 105 kg at 150 issued; 15,750 + 8 h at Karim's standard 250 carried at the month's end.
        var issue = Entry(oct, PracticeLedger.KindIssue);
        Assert.Equal(("2026-10-20", 15_750m), (issue.GetProperty("day").GetString(), Side(issue, S.RawMaterialConsumption, "debit")));
        var carried = Entry(oct, PracticeLedger.KindMonthEndWip);
        Assert.Equal(("2026-10-31", 17_750m), (carried.GetProperty("day").GetString(), Side(carried, S.WorkInProgress, "debit")));
        Assert.Equal(oct.GetProperty("totalDebit").GetDecimal(), oct.GetProperty("totalCredit").GetDecimal());
        // Nothing mapped yet: every check of the events passes, and the map's says which slots name no account.
        var checks = oct.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("code").GetString()!, c => c.GetProperty("passed").GetBoolean());
        Assert.Equal(new[] { true, true, true, true, false }, new[] { "entries_balance", "materials_tie", "releases_tie", "wip_tie", "accounts_mapped" }.Select(c => checks[c]));
        Assert.Equal(new[] { S.RawMaterialStock, S.RawMaterialConsumption, S.WorkInProgress, S.ProductionStocked },
            oct.GetProperty("unmappedSlots").EnumerateArray().Select(x => x.GetString()));

        // November: October's value comes back on the 1st and is carried again at the end.
        var nov = await OkAsync(s.Db, w.AdminPrincipal, "GET", Route, "?year=2026&month=11");
        var back = Entry(nov, PracticeLedger.KindWipReversal);
        Assert.Equal(("2026-11-01", 17_750m), (back.GetProperty("day").GetString(), Side(back, S.ProductionStocked, "debit")));
        Assert.Equal(17_750m, Side(Entry(nov, PracticeLedger.KindMonthEndWip), S.WorkInProgress, "debit"));

        // An account named in the map is the entry's.
        var stock = await s.Db.LedgerAccounts.AsNoTracking().FirstAsync(a => a.IsActive);
        var (_, error) = await new ManufacturingCostingChoices(s.Db).UpdateAccountMapAsync(
            new UpdateManufacturingAccountMapRequest([new(S.RawMaterialStock, stock.Id)]), "admin");
        Assert.Null(error);
        var mapped = Entry(await OkAsync(s.Db, w.AdminPrincipal, "GET", Route, "?year=2026&month=10"), PracticeLedger.KindIssue);
        Assert.Equal(stock.Code, mapped.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("slot").GetString() == S.RawMaterialStock)
            .GetProperty("accountCode").GetString());

        // The perpetual model: the issue and the hours go to work in progress as they happen, nothing is carried.
        var settings = await new ManufacturingCostingChoices(s.Db).UpdateSettingsAsync(new UpdateManufacturingCostingSettingsRequest(
            "WorkInProgress", 0m, "StandardMaterialPlusConversion", "None", 0m, "Monthly", "StayInCharges", "ManagementOnly"), "admin");
        Assert.Null(settings.Error);
        var perpetual = await OkAsync(s.Db, w.AdminPrincipal, "GET", Route, "?year=2026&month=10");
        Assert.Equal(2_000m, Side(Entry(perpetual, PracticeLedger.KindLabour), S.LabourAbsorbed, "credit"));
        Assert.Equal(15_750m, Side(Entry(perpetual, PracticeLedger.KindIssue), S.WorkInProgress, "debit"));
        Assert.DoesNotContain(perpetual.GetProperty("entries").EnumerateArray(), e => e.GetProperty("kind").GetString() == PracticeLedger.KindMonthEndWip);
        var wip = perpetual.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("code").GetString() == "wip_tie");
        Assert.Equal((17_750m, true), (wip.GetProperty("actual").GetDecimal(), wip.GetProperty("passed").GetBoolean()));
    }

    [Fact]
    public async Task Every_entry_is_one_order_s_labour_so_the_month_needs_pay_rate_access_and_a_month_that_has_begun()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var (_, costsOnly) = await ManagerAsync(s.Db, WarehouseA, AllGrants);
        await RefusedAsync(s.Db, costsOnly, 403, CostPrivacy.WithheldCode, "GET", Route, "?year=2026&month=10");
        await RefusedAsync(s.Db, w.AdminPrincipal, 400, "month_in_future", "GET", Route, "?year=2100&month=12");
        await RefusedAsync(s.Db, w.AdminPrincipal, 400, "invalid_period", "GET", Route, "?year=2026&month=13");
    }

    private static JsonElement Entry(JsonElement ledger, string kind) =>
        ledger.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("kind").GetString() == kind);

    private static decimal Side(JsonElement entry, string slot, string side) =>
        entry.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("slot").GetString() == slot).GetProperty(side).GetDecimal();
}
