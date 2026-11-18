using ParadisERP.Application.Manufacturing;
using ParadisERP.Domain;
using static ParadisERP.Application.Manufacturing.CostReconciliation;

namespace ParadisERP.Tests.Manufacturing;

/// <summary>
/// B2's reconciliation rules, pure: no database, the same figures on every provider (docs/manufacturing/costing-b2.md §4).
/// </summary>
public sealed class CostReconciliationTests
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    private static StoredElement[] Sheet(decimal m = 0m, decimal k = 0m, decimal a = 0m, decimal l = 0m, decimal o = 0m, decimal e = 0m, decimal r = 0m) =>
    [
        new(Materials, m, "reference_cost"), new(Consumables, k, "none"), new(Accessories, a, "none"), new(Labour, l, "provisional"),
        new(Overhead, o, "not_set"), new(Expenses, e, "provisional"), new(Recovery, r, "none"),
    ];

    [Fact]
    public void Money_rounds_half_away_from_zero_at_three_decimals_and_units_at_six()
    {
        Assert.Equal((0.001m, -0.001m, 0.000m, 1.000001m), (Money(0.0005m), Money(-0.0005m), Money(0.00049m), Unit(1.0000005m)));
        Assert.Equal(12.35m, Percent(12.345m));
    }

    [Fact]
    public void Elements_add_up_to_the_original_with_an_explicit_residual_and_recovery_reduces_the_cost()
    {
        // C = M + K + A + L + O + E − R; each stored element was rounded alone, the total once.
        var (lines, residual) = ReconcileElements(1_000.001m, Sheet(m: 900m, l: 100.0005m, e: 10m, r: 9.999m), new Dictionary<string, decimal> { [Labour] = 5m });
        Assert.Equal(-9.999m, lines.Single(x => x.Element == Recovery).Original);
        Assert.Equal(1_000.001m - (900m + 100.0005m + 10m - 9.999m), residual);
        Assert.Equal(1_000.001m + 5m, lines.Sum(x => x.Adjusted) + residual);
        Assert.Equal(105.0005m, lines.Single(x => x.Element == Labour).Adjusted);
    }

    [Theory]
    [InlineData("expense", Expenses)]
    [InlineData(" Expenses ", Expenses)]
    [InlineData("LABOR", Labour)]
    [InlineData("scrap_recovery", Recovery)]
    [InlineData("material", Materials)]
    [InlineData("freight-fix", null)]
    [InlineData("", null)]
    public void A_kind_names_an_element_or_stays_unclassified(string kind, string? element) => Assert.Equal(element, ElementOf(kind));

    [Fact]
    public void Late_costs_are_recognized_unclassified_or_uncertain_and_the_totals_keep_them_apart()
    {
        var sheet = Guid.NewGuid();
        var otherSheet = Guid.NewGuid();
        var at = new DateTime(2026, 10, 29, 10, 0, 0);
        var rows = new List<LateInput>
        {
            new(Guid.NewGuid(), sheet, "expense", "PV-1", 10m, at),
            new(Guid.NewGuid(), sheet, "expenses", " pv-1", 10m, at),          // same element, same source but for case and space
            new(Guid.NewGuid(), sheet, "labour", "DOC 9", -4m, at),
            new(Guid.NewGuid(), sheet, "overhead", "doc  9", 3m, at),          // the same document, another element: shared
            new(Guid.NewGuid(), sheet, "expense", "voucher:PV-0042", 7m, at),  // names a voucher frozen in the sheet
            new(Guid.NewGuid(), sheet, "bonus", "B-1", 2m, at),
            new(Guid.NewGuid(), otherSheet, "expense", "DOC 9", 1m, at),
        };
        var frozen = new Dictionary<Guid, IReadOnlySet<string>> { [sheet] = new HashSet<string> { "PV-0042" } };
        var assessed = AssessLate(rows, id => frozen.GetValueOrDefault(id) ?? None);
        string T(int i) => assessed[rows[i].Id].Treatment;
        Assert.Equal(new[] { TreatmentUncertain, TreatmentUncertain, TreatmentRecognized, TreatmentRecognized, TreatmentUncertain, TreatmentUnclassified, TreatmentRecognized },
            Enumerable.Range(0, rows.Count).Select(T));
        Assert.Contains(FlagDuplicateSource, assessed[rows[0].Id].Flags);
        Assert.Contains(FlagSharedSource, assessed[rows[2].Id].Flags);
        Assert.Contains(FlagSeveralOrders, assessed[rows[6].Id].Flags);
        Assert.Contains(FlagOverlapsOriginal, assessed[rows[4].Id].Flags);
        Assert.Null(assessed[rows[5].Id].Element);

        var totals = Totals(rows.Select(r => (r.Amount, assessed[r.Id].Treatment)));
        Assert.Equal(new LateTotals(29m, 0m, 2m, 27m), totals);
    }

    [Fact]
    public void Releases_follow_the_engine_order_the_last_carries_the_remainder_and_nothing_late_is_allocated()
    {
        var at = new DateTime(2026, 10, 21, 9, 0, 0);
        StoredRelease R(string number, DateTime when, decimal qty, decimal value) => new(Guid.NewGuid(), number, when, qty, value, value / qty, 400m);
        var summary = Releases(1_000m, 100m, [R("OR-2", at, 30m, 310m), R("OR-1", at, 20m, 190m), R("OR-0", at.AddHours(-1), 50m, 500m)], 25m);
        Assert.Equal(new[] { "OR-0", "OR-1", "OR-2" }, summary.Lines.Select(l => l.Number));
        Assert.Equal(new[] { false, false, true }, summary.Lines.Select(l => l.CarriesRemainder));
        Assert.Equal((100m, 0m, 1_000m, 0m, 25m), (summary.ReleasedQty, summary.UnreleasedAcceptedQty, summary.ReleasedValue, summary.KeptInCharges, summary.LateNotAllocated));
        Assert.Equal(12_000m, summary.Lines[2].CatalogueReferenceValue);

        var none = Releases(1_000m, 100m, [], 0m);
        Assert.Equal((1_000m, 100m), (none.KeptInCharges, none.UnreleasedAcceptedQty));
        Assert.Contains(FlagUnreleasedOutput, none.Flags);
        var negative = Releases(100m, 90m, [R("OR-1", at, 80m, 120m), R("OR-2", at.AddHours(1), 20m, -20m)], 0m);
        Assert.Equal(new[] { FlagOverReleased, FlagFinalReleaseNegative }, negative.Flags);
    }

    [Fact]
    public void Spoilage_is_absorbed_by_B1s_rule_and_an_unrecorded_recovery_is_said_not_shown_as_zero()
    {
        var scrap = Scrap(1_000m, 98m, 2m, 0m, ProductionCostEngine.ElementNotRecorded, null, CostingCapture.ReasonNotCaptured);
        Assert.Equal((2.00m, 20m, (decimal?)null, "not_recorded", CostingCapture.ReasonNotCaptured),
            (scrap.RejectionRatePercent, scrap.SpoilageAbsorbed, scrap.MaterialScrapValue, scrap.RecoveryStatus, scrap.Unavailable));
        Assert.Equal(Money((1_000m + 50m) * 2m / 100m - 50m), Scrap(1_000m, 98m, 2m, 50m, "recorded", null, null).SpoilageAbsorbed);
        var nothing = Scrap(0m, 0m, 0m, 0m, "none", null, null);
        Assert.Equal(((decimal?)null, (decimal?)null), (nothing.RejectionRatePercent, nothing.SpoilageAbsorbed));
    }

    [Fact]
    public void Variances_replay_the_engine_add_up_with_a_rounding_line_and_take_late_effects_only_on_group_lines()
    {
        var input = new CostingInput(
            PlannedBaseQty: 3m, AcceptedBaseQty: 2m, RejectedBaseQty: 1m, Completed: true, StandardHours: 1m, StandardRate: 100m / 3m,
            Materials: [new CostingMaterial(Guid.NewGuid(), "W", ManufacturingRole.RawMaterial, "kg", 3m, 10m / 3m, 3m, 0m, 2m, 1m, 10m, 0m)],
            Bookings: [new CostingBooking(Guid.NewGuid(), Guid.Empty, new DateOnly(2026, 10, 20), 2026, 10, 1m)],
            Rates: new Dictionary<(Guid, int, int), LabourRate>(), Expenses: [], Releases: [], Policy: CostingPolicy.Default);
        var withRate = input with { Rates = new Dictionary<(Guid, int, int), LabourRate> { [(input.Bookings[0].EmployeeId, 2026, 10)] = new(100m / 7m, LabourRateKind.Standard) } };
        var sheet = ProductionCostEngine.Compute(withRate);
        var figures = Variances(sheet, null, new Dictionary<string, decimal> { [Materials] = 1m, [Consumables] = 2m, [Labour] = -3m }, labourSplit: true);
        var groups = figures.Lines.Where(l => !l.Detail && l.Component is not ("rounding" or "total")).ToList();
        var total = figures.Lines.Single(l => l.Component == "total");
        Assert.Equal(Money(sheet.Variances!.Total), total.Original);
        Assert.Equal(total.Original, groups.Sum(g => g.Original) + figures.Lines.Single(l => l.Component == "rounding").Original);
        Assert.Equal(3m, figures.Lines.Single(l => l.Component == "materials").LateRecognized);
        Assert.Equal(0m, total.LateRecognized);
        Assert.All(figures.Lines.Where(l => l.Detail), l => Assert.Null(l.LateRecognized));

        var hidden = Variances(sheet, null, new Dictionary<string, decimal>(), labourSplit: false);
        Assert.DoesNotContain(hidden.Lines, l => l.Component is "labour_rate" or "labour_efficiency");
        Assert.False(Variances(null, CostingCapture.ReasonNotCaptured, new Dictionary<string, decimal>(), true).Available);
    }

    [Fact]
    public void A_capture_is_read_only_under_its_own_method_version_and_otherwise_says_why()
    {
        Assert.Equal(CostingCapture.ReasonNotCaptured, CostingCapture.Parse("", ProductionCostEngine.Version, ManufacturingCostSourceState.Complete).Reason);
        Assert.Equal(CostingCapture.ReasonNotCaptured, CostingCapture.Parse("{}", ProductionCostEngine.Version, ManufacturingCostSourceState.IncompleteLegacy).Reason);
        Assert.Equal(CostingCapture.ReasonVersion, CostingCapture.Parse("{}", "B0.9", ManufacturingCostSourceState.Complete).Reason);
        Assert.Equal(CostingCapture.ReasonUnreadable, CostingCapture.Parse("{not json", ProductionCostEngine.Version, ManufacturingCostSourceState.Complete).Reason);
        Assert.Equal(CostingCapture.ReasonUnreadable, CostingCapture.Parse("{\"Materials\":[]}", ProductionCostEngine.Version, ManufacturingCostSourceState.Complete).Reason);
    }

    [Fact]
    public void Checks_pass_fail_or_do_not_apply_and_a_residual_beyond_rounding_fails()
    {
        var at = new DateTime(2026, 10, 21);
        SheetRows Rows(decimal original, decimal? unit, StoredElement[] elements, StoredRelease[] releases) => new(
            original, 4m, 0m, unit, "complete", "", null, elements, [], [], [], (0m, 0m), releases);
        var sound = Checks(Rows(10m, 2.5m, Sheet(m: 10m), [new(Guid.NewGuid(), "OR-1", at, 4m, 10m, 2.5m, null)]), null, CostingCapture.ReasonNotCaptured, null, []);
        Assert.Equal(Pass, sound.Single(c => c.Code == CheckElementIdentity).Result);
        Assert.Equal(Pass, sound.Single(c => c.Code == CheckReleaseSum).Result);
        Assert.Equal(Pass, sound.Single(c => c.Code == CheckUnitCost).Result);
        Assert.Equal(NotApplicable, sound.Single(c => c.Code == CheckLateSources).Result);
        Assert.Equal(NotApplicable, sound.Single(c => c.Code == CheckSourceReplay).Result);

        var broken = Checks(Rows(10m, 3m, Sheet(m: 9.99m), [new(Guid.NewGuid(), "OR-1", at, 4m, 9m, 2.25m, null)]), null, CostingCapture.ReasonNotCaptured, null,
            [new LateAssessment(Guid.NewGuid(), Expenses, TreatmentUncertain, [FlagDuplicateSource])]);
        Assert.All(new[] { CheckElementIdentity, CheckReleaseSum, CheckUnitCost, CheckLateSources },
            code => Assert.Equal(Fail, broken.Single(c => c.Code == code).Result));
        Assert.Equal(NotApplicable, Checks(Rows(10m, 2.5m, Sheet(m: 10m), []), null, null, null, []).Single(c => c.Code == CheckReleaseSum).Result);
    }

    [Fact]
    public void Drift_is_live_minus_adjusted_element_by_element()
    {
        var input = new CostingInput(10m, 10m, 0m, true, 0m, null,
            [new CostingMaterial(Guid.NewGuid(), "W", ManufacturingRole.RawMaterial, "kg", 10m, 1m, 10m, 0m, 10m, 0m, 12m, 0m)],
            [], new Dictionary<(Guid, int, int), LabourRate>(), [new CostingExpense(Guid.NewGuid(), 5m, new DateTime(2026, 10, 1))], [], CostingPolicy.Default);
        var live = ProductionCostEngine.Compute(input);
        var (lines, _) = ReconcileElements(14m, Sheet(m: 12m, e: 2m), new Dictionary<string, decimal> { [Expenses] = 1m });
        var (total, unexplained, elements) = Drift(live, lines, 15m);
        Assert.Equal((17m, 2m), (total, unexplained));
        Assert.Equal(2m, elements.Single(e => e.Element == Expenses).Unexplained);
        Assert.Equal(0m, elements.Single(e => e.Element == Materials).Unexplained);
    }
}
