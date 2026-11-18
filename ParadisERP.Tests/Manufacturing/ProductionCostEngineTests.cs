using ParadisERP.Application.Manufacturing;
using ParadisERP.Domain;

namespace ParadisERP.Tests.Manufacturing;

/// <summary>
/// The costing engine on its own (costing plan §2): the worked example of §2.12 to the decimal, the
/// variance identity, the release rule and the notes that say what is not final.
/// </summary>
public class ProductionCostEngineTests
{
    private static readonly Guid Karim = Guid.NewGuid(), Samir = Guid.NewGuid(), Cut = Guid.NewGuid(), Form = Guid.NewGuid();
    private static readonly DateOnly Day1 = new(2026, 10, 20), Day2 = new(2026, 10, 21);
    private static readonly DateTime EndOfDay1 = new(2026, 10, 20, 17, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2Noon = new(2026, 10, 21, 11, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime EndOfDay2 = new(2026, 10, 21, 17, 0, 0, DateTimeKind.Utc);

    private static readonly CostingMaterial[] ExampleMaterials =
    [
        // Wire: 210 kg issued at 150, 4 kg returned at the issue average; 206 kg consumed (200 used, 6 of offcuts).
        new(Guid.NewGuid(), "WIRE", ManufacturingRole.RawMaterial, "kg", 210m, 150m, 210m, 4m, 206m, 0m, 31_500m, 600m),
        new(Guid.NewGuid(), "DISC", ManufacturingRole.Consumable, "pcs", 10m, 300m, 10m, 0m, 10m, 0m, 3_000m, 0m),
        new(Guid.NewGuid(), "BOX", ManufacturingRole.Accessory, "pcs", 20m, 50m, 20m, 0m, 20m, 0m, 1_000m, 0m),
    ];

    private static readonly CostingBooking[] ExampleBookings =
    [
        new(Karim, Cut, Day1, 2026, 10, 8m),
        new(Samir, Form, Day1, 2026, 10, 1m),
        new(Samir, Form, Day2, 2026, 10, 5m),
    ];

    private static CostingInput Example(
        decimal karimRate, decimal samirRate, LabourRateKind kind, bool completed, decimal recovery = 120m,
        IReadOnlyList<CostingRelease>? releases = null, decimal accepted = 196m, decimal rejected = 4m) =>
        new(
            PlannedBaseQty: 200m, AcceptedBaseQty: accepted, RejectedBaseQty: rejected, Completed: completed,
            StandardHours: 16m, StandardRate: 380m,
            Materials: ExampleMaterials, Bookings: ExampleBookings,
            Rates: new Dictionary<(Guid, int, int), LabourRate>
            {
                [(Karim, 2026, 10)] = new(karimRate, kind),
                [(Samir, 2026, 10)] = new(samirRate, kind),
            },
            Expenses: [new CostingExpense(Guid.NewGuid(), 12_000m, Day2Noon, Settled: true)],
            Releases: releases ??
            [
                new CostingRelease(Guid.NewGuid(), "OR-000001", 100m, EndOfDay1, Day1, GoodToDate: 100m),
                new CostingRelease(Guid.NewGuid(), "OR-000002", 96m, EndOfDay2, Day2, GoodToDate: 196m),
            ],
            Policy: new CostingPolicy(OverheadPerLabourHour: 100m, Approved: true),
            RecoveryValue: recovery);

    [Fact]
    public void The_worked_example_gives_the_plan_s_figures()
    {
        var sheet = ProductionCostEngine.Compute(Example(400m, 350m, LabourRateKind.Actual, completed: true));

        Assert.Equal((31_500m, 3_000m, 1_000m, 6_080m, 1_600m, 0m, 0m, 43_180m),
            (sheet.Estimate.Materials, sheet.Estimate.Consumables, sheet.Estimate.Accessories, sheet.Estimate.Labour,
             sheet.Estimate.Overhead, sheet.Estimate.Expenses, sheet.Estimate.Recovery, sheet.Estimate.Total));
        Assert.Equal(215.9m, sheet.EstimatedUnitCost);

        Assert.Equal((30_900m, 3_000m, 1_000m, 5_300m, 1_400m, 12_000m, 120m, 53_480m),
            (sheet.Actual.Materials, sheet.Actual.Consumables, sheet.Actual.Accessories, sheet.Actual.Labour,
             sheet.Actual.Overhead, sheet.Actual.Expenses, sheet.Actual.Recovery, sheet.Actual.Total));
        Assert.Equal(272.857m, Math.Round(sheet.ActualUnitCost!.Value, 3));
        Assert.Equal(14m, sheet.LabourHours);

        // Variances against the estimate scaled to 196 kg: they sum to the total, which is final − 215.90 × 196.
        var v = sheet.Variances!;
        Assert.Equal((110m, 0m, -638.4m, -20m, -168m, 12_000m, -120m, 11_163.6m),
            (v.MaterialUsage, v.MaterialPrice, v.LabourEfficiency, v.LabourRate, v.Overhead, v.Expenses, v.Recovery, v.Total));
        Assert.Equal(sheet.Actual.Total - sheet.EstimatedUnitCost!.Value * 196m, v.Total);
        Assert.Equal(952m, sheet.Spoilage);

        // Release 1 at the rates known now (actual): 177.50 of standard material + (3,550 + 900) ÷ 100 of conversion;
        // the last release of a completed order carries the remainder, so releases add up to the cost.
        Assert.Equal((22_200m, 222m, false), (sheet.Releases[0].Value, sheet.Releases[0].UnitValue, sheet.Releases[0].CarriesRemainder));
        Assert.Equal((31_280m, true), (sheet.Releases[1].Value, sheet.Releases[1].CarriesRemainder));
        Assert.Equal(sheet.Actual.Total, sheet.ReleasedValue);
        Assert.Equal((0m, 0m), (sheet.WorkInProgress, sheet.KeptInCharges));
        Assert.Contains(sheet.Notes, n => n.Code == ProductionCostEngine.NoteNotFrozen);
        Assert.Contains(sheet.Notes, n => n.Code == ProductionCostEngine.NoteReferenceCost);
    }

    [Fact]
    public void Before_payroll_is_approved_release_one_is_the_plan_s_22070_and_the_hours_are_said_to_be_provisional()
    {
        var sheet = ProductionCostEngine.Compute(Example(380m, 380m, LabourRateKind.Standard, completed: false, recovery: 0m));

        Assert.Equal((22_070m, 220.7m), (sheet.Releases[0].Value, sheet.Releases[0].UnitValue));
        // An open order's second release follows the rule too: conversion to date 5,320 + 1,400 + 12,000, less the
        // 4,320 the first carried, over the 96 good units not yet released.
        Assert.Equal((31_440m, 327.5m, false), (sheet.Releases[1].Value, sheet.Releases[1].UnitValue, sheet.Releases[1].CarriesRemainder));
        Assert.Equal(53_620m, sheet.Actual.Total);
        Assert.Equal(sheet.Actual.Total - sheet.ReleasedValue, sheet.WorkInProgress);
        Assert.Contains(sheet.Notes, n => n.Code == ProductionCostEngine.NoteStandardRate && n.Value == 14m);
        Assert.DoesNotContain(sheet.Notes, n => n.Code == ProductionCostEngine.NoteNotFrozen);
    }

    [Fact]
    public void Material_still_at_the_line_is_work_in_progress_and_consumption_takes_what_is_left()
    {
        CostingMaterial[] wire = [new(Guid.NewGuid(), "WIRE", ManufacturingRole.RawMaterial, "kg", 210m, 150m,
            IssuedBaseQty: 210m, ReturnedBaseQty: 0m, ConsumedBaseQty: 100m, ScrappedBaseQty: 2m, IssuedValue: 31_500m, ReturnedValue: 0m)];
        var sheet = ProductionCostEngine.Compute(Example(400m, 350m, LabourRateKind.Actual, completed: false, recovery: 0m, releases: [], accepted: 0m, rejected: 0m)
            with { Materials = wire, Bookings = [], Expenses = [] });

        var m = sheet.Materials.Single();
        Assert.Equal((108m, 16_200m, 300m, 15_000m), (m.AtLineBaseQty, m.AtLineValue, m.ScrapValue, m.ConsumedValue));
        Assert.Equal(m.NetValue, m.ConsumedValue + m.ScrapValue + m.AtLineValue);
        Assert.Equal((16_200m, 31_500m), (sheet.AtLineValue, sheet.WorkInProgress));
        Assert.Null(sheet.ActualUnitCost);
        Assert.Null(sheet.Variances);
    }

    [Fact]
    public void A_completed_order_with_nothing_released_keeps_its_cost_in_the_charges()
    {
        var sheet = ProductionCostEngine.Compute(Example(400m, 350m, LabourRateKind.Actual, completed: true, releases: [], accepted: 0m, rejected: 200m));

        Assert.Empty(sheet.Releases);
        Assert.Equal((0m, 0m, sheet.Actual.Total), (sheet.ReleasedValue, sheet.WorkInProgress, sheet.KeptInCharges));
        Assert.Null(sheet.ActualUnitCost);
        // Everything rejected: the spoilage is the whole cost.
        Assert.Equal(sheet.Actual.Total, sheet.Spoilage);
    }

    [Fact]
    public void Hours_without_a_rate_count_but_cost_nothing_and_are_said_to_be_unvalued()
    {
        var input = Example(400m, 350m, LabourRateKind.Actual, completed: false) with
        {
            Rates = new Dictionary<(Guid, int, int), LabourRate> { [(Karim, 2026, 10)] = new(400m, LabourRateKind.Actual) },
            StandardRate = null,
        };
        var sheet = ProductionCostEngine.Compute(input);

        Assert.Equal((14m, 3_200m), (sheet.LabourHours, sheet.Actual.Labour));
        var samir = sheet.Labour.Single(l => l.EmployeeId == Samir);
        Assert.Equal((LabourRateKind.None, 0m, (decimal?)null), (samir.Kind, samir.Cost, samir.Rate));
        Assert.Contains(sheet.Notes, n => n.Code == ProductionCostEngine.NoteUnvaluedLabour && n.Value == 6m);
        Assert.Contains(sheet.Notes, n => n.Code == ProductionCostEngine.NoteNoLabourEstimate);
        Assert.Equal(0m, sheet.Estimate.Labour);
    }

    [Fact]
    public void Material_issued_at_no_cost_and_payslips_without_employer_social_are_said()
    {
        CostingMaterial[] free = [new(Guid.NewGuid(), "WIRE", ManufacturingRole.RawMaterial, "kg", 10m, 0m, 10m, 0m, 0m, 0m, 0m, 0m)];
        var input = Example(400m, 350m, LabourRateKind.Actual, completed: false) with
        {
            Materials = free,
            Rates = new Dictionary<(Guid, int, int), LabourRate>
            {
                [(Karim, 2026, 10)] = new(400m, LabourRateKind.Actual, WithoutEmployerSocial: true),
                [(Samir, 2026, 10)] = new(350m, LabourRateKind.Actual),
            },
            Policy = CostingPolicy.Default,
        };
        var sheet = ProductionCostEngine.Compute(input);

        Assert.Contains(sheet.Notes, n => n.Code == ProductionCostEngine.NoteUnvaluedMaterial);
        Assert.Contains(sheet.Notes, n => n.Code == ProductionCostEngine.NoteNoEmployerSocial);
        Assert.Contains(sheet.Notes, n => n.Code == ProductionCostEngine.NoteNoOverhead);
        Assert.Equal(0m, sheet.Actual.Overhead);
    }

    [Fact]
    public void A_final_release_that_would_go_below_zero_is_said()
    {
        // The first release carried more than the order turned out to cost: the remainder is negative and flagged.
        var input = Example(400m, 350m, LabourRateKind.Actual, completed: true, recovery: 50_000m);
        var sheet = ProductionCostEngine.Compute(input);

        Assert.True(sheet.Releases[^1].Value < 0);
        Assert.Contains(sheet.Notes, n => n.Code == ProductionCostEngine.NoteFinalReleaseNegative);
        Assert.Equal(sheet.Actual.Total, sheet.ReleasedValue);
    }

    [Fact]
    public void A_completed_order_stays_provisional_while_any_input_is_and_each_element_says_what_it_rests_on()
    {
        // Payroll approved, an overhead rate, the expense and the recovery recorded: still provisional, because the
        // materials are at their reference cost until the valuation method is signed.
        var sheet = ProductionCostEngine.Compute(Example(400m, 350m, LabourRateKind.Actual, completed: true));
        Assert.Equal(ProductionCostEngine.StatusProvisional, sheet.Status);
        Assert.Equal(new CostElementStatuses("reference_cost", "reference_cost", "reference_cost", "approved", "approved", "recorded", "recorded"),
            sheet.Statuses);

        // Before payroll is approved, with no overhead rate and rejects whose recovery is not recorded: each said.
        var open = ProductionCostEngine.Compute(Example(380m, 380m, LabourRateKind.Standard, completed: false, recovery: 0m) with
        {
            Policy = CostingPolicy.Default,
        });
        Assert.Equal(("provisional", "not_set", "not_recorded"), (open.Statuses.Labour, open.Statuses.Overhead, open.Statuses.Recovery));

        // Hours without a rate and a material issued at no cost are incomplete, not zero.
        CostingMaterial[] free = [new(Guid.NewGuid(), "WIRE", ManufacturingRole.RawMaterial, "kg", 10m, 0m, 10m, 0m, 0m, 0m, 0m, 0m)];
        var incomplete = ProductionCostEngine.Compute(Example(400m, 350m, LabourRateKind.Actual, completed: false) with
        {
            Materials = free,
            Rates = new Dictionary<(Guid, int, int), LabourRate> { [(Karim, 2026, 10)] = new(400m, LabourRateKind.Actual) },
        });
        Assert.Equal(("incomplete", "incomplete"), (incomplete.Statuses.Materials, incomplete.Statuses.Labour));
    }

    [Fact]
    public void Only_a_sheet_whose_every_input_is_approved_reads_approved_and_missing_employer_social_is_said()
    {
        // No material, payroll approved, an approved overhead rate, nothing rejected: approved.
        var approved = ProductionCostEngine.Compute(Example(400m, 350m, LabourRateKind.Actual, completed: true, recovery: 0m, rejected: 0m) with
        {
            Materials = [],
            Expenses = [],
        });
        Assert.Equal(ProductionCostEngine.StatusApproved, approved.Status);
        Assert.False(approved.EmployerSocialMissing);

        // The same with payslips that carried no employer social contribution: the flag and the note, never a zero line.
        var withoutSocial = ProductionCostEngine.Compute(Example(400m, 350m, LabourRateKind.Actual, completed: true, recovery: 0m, rejected: 0m) with
        {
            Materials = [],
            Rates = new Dictionary<(Guid, int, int), LabourRate>
            {
                [(Karim, 2026, 10)] = new(400m, LabourRateKind.Actual, WithoutEmployerSocial: true),
                [(Samir, 2026, 10)] = new(350m, LabourRateKind.Standard, WithoutEmployerSocial: true),
            },
        });
        Assert.True(withoutSocial.EmployerSocialMissing);
        Assert.Contains(withoutSocial.Notes, n => n.Code == ProductionCostEngine.NoteNoEmployerSocial);
        Assert.Equal(ProductionCostEngine.StatusProvisional, withoutSocial.Status);

        // Approved payroll that left the employer share out is still not an approved labour cost (C-06 unsigned).
        var approvedWithoutSocial = ProductionCostEngine.Compute(Example(400m, 350m, LabourRateKind.Actual, completed: true, recovery: 0m, rejected: 0m) with
        {
            Materials = [],
            Expenses = [],
            Rates = new Dictionary<(Guid, int, int), LabourRate>
            {
                [(Karim, 2026, 10)] = new(400m, LabourRateKind.Actual, WithoutEmployerSocial: true),
                [(Samir, 2026, 10)] = new(350m, LabourRateKind.Actual),
            },
        });
        Assert.Equal((ProductionCostEngine.ElementProvisional, ProductionCostEngine.StatusProvisional),
            (approvedWithoutSocial.Statuses.Labour, approvedWithoutSocial.Status));
    }

    [Fact]
    public void No_sheet_reads_approved_before_the_costing_method_is_signed_or_while_an_expense_is_unbacked()
    {
        // Every input approved, but the method is the unsigned default of Phase A (C-01, C-05 to C-08): provisional, and said.
        var everything = Example(400m, 350m, LabourRateKind.Actual, completed: true, recovery: 0m, rejected: 0m) with { Materials = [] };
        Assert.Equal(ProductionCostEngine.StatusApproved, ProductionCostEngine.Compute(everything).Status);
        var unsigned = ProductionCostEngine.Compute(everything with { Policy = everything.Policy with { Approved = false } });
        Assert.Equal(ProductionCostEngine.StatusProvisional, unsigned.Status);
        Assert.Contains(unsigned.Notes, n => n.Code == ProductionCostEngine.NotePolicyNotApproved);
        Assert.Contains(ProductionCostEngine.Compute(everything with { Policy = CostingPolicy.Default }).Notes,
            n => n.Code == ProductionCostEngine.NotePolicyNotApproved);

        // An expense with no voucher, or one not yet approved or paid, is typed, not backed: provisional, with its amount.
        var unbacked = ProductionCostEngine.Compute(everything with
        {
            Expenses = [new CostingExpense(Guid.NewGuid(), 12_000m, Day2Noon, Settled: true), new CostingExpense(Guid.NewGuid(), 500m, Day2Noon)],
        });
        Assert.Equal((ProductionCostEngine.ElementProvisional, ProductionCostEngine.StatusProvisional), (unbacked.Statuses.Expenses, unbacked.Status));
        Assert.Contains(unbacked.Notes, n => n.Code == ProductionCostEngine.NoteUnsettledExpense && n.Value == 500m);
        Assert.Equal(12_500m, unbacked.Actual.Expenses);
    }

    [Fact]
    public void The_same_inputs_give_the_same_sheet()
    {
        var input = Example(400m, 350m, LabourRateKind.Actual, completed: true);
        var once = ProductionCostEngine.Compute(input);
        var twice = ProductionCostEngine.Compute(input);
        Assert.Equal(once.Actual, twice.Actual);
        Assert.Equal(once.Releases, twice.Releases);
        Assert.Equal(once.Variances, twice.Variances);
    }
}
