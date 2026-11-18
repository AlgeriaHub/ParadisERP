using ParadisERP.Application.Contracts;
using ParadisERP.Application.Manufacturing;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Manufacturing;
using Microsoft.EntityFrameworkCore;
using static ParadisERP.Tests.Manufacturing.MfgHarness;
using static ParadisERP.Tests.Manufacturing.ProductionWorld;

namespace ParadisERP.Tests.Manufacturing;

/// <summary>
/// Phase C1 (owner, 2026-11-08: "for the things that need a decision, make it settings and the admin can choose"):
/// the accountant's open costing choices as settings, read by the engine; the account map, seeded empty.
/// </summary>
public class CostingChoicesTests
{
    private static readonly Guid Karim = Guid.NewGuid(), Samir = Guid.NewGuid(), Stage = Guid.NewGuid();
    private static readonly DateOnly Day1 = new(2026, 10, 20), Day2 = new(2026, 10, 21);
    private static readonly DateTime EndOfDay1 = new(2026, 10, 20, 17, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2Noon = new(2026, 10, 21, 11, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime EndOfDay2 = new(2026, 10, 21, 17, 0, 0, DateTimeKind.Utc);

    /// <summary>The costing plan's worked example (§2.12), under a chosen policy.</summary>
    private static CostingInput Example(CostingPolicy policy, bool withoutEmployerSocial = false) => new(
        PlannedBaseQty: 200m, AcceptedBaseQty: 196m, RejectedBaseQty: 4m, Completed: true, StandardHours: 16m, StandardRate: 380m,
        Materials:
        [
            new(Guid.NewGuid(), "WIRE", ManufacturingRole.RawMaterial, "kg", 210m, 150m, 210m, 4m, 206m, 0m, 31_500m, 600m),
            new(Guid.NewGuid(), "DISC", ManufacturingRole.Consumable, "pcs", 10m, 300m, 10m, 0m, 10m, 0m, 3_000m, 0m),
            new(Guid.NewGuid(), "BOX", ManufacturingRole.Accessory, "pcs", 20m, 50m, 20m, 0m, 20m, 0m, 1_000m, 0m),
        ],
        Bookings: [new(Karim, Stage, Day1, 2026, 10, 8m), new(Samir, Stage, Day1, 2026, 10, 1m), new(Samir, Stage, Day2, 2026, 10, 5m)],
        Rates: new Dictionary<(Guid, int, int), LabourRate>
        {
            [(Karim, 2026, 10)] = new(400m, LabourRateKind.Actual, WithoutEmployerSocial: withoutEmployerSocial),
            [(Samir, 2026, 10)] = new(350m, LabourRateKind.Actual, WithoutEmployerSocial: withoutEmployerSocial),
        },
        Expenses: [new CostingExpense(Guid.NewGuid(), 12_000m, Day2Noon, Settled: true)],
        Releases:
        [
            new CostingRelease(Guid.NewGuid(), "OR-000001", 100m, EndOfDay1, Day1, GoodToDate: 100m),
            new CostingRelease(Guid.NewGuid(), "OR-000002", 96m, EndOfDay2, Day2, GoodToDate: 196m),
        ],
        Policy: policy, RecoveryValue: 120m);

    [Fact]
    public void Each_release_rule_values_the_first_release_its_own_way_and_the_last_carries_the_rest()
    {
        // §2.9 (suggested): 177.50 of standard material + (4,000 + 450) ÷ 100 of conversion.
        var standard = ProductionCostEngine.Compute(Example(new CostingPolicy(100m))).Releases;
        // The estimate: 215.90 a kg.
        var estimated = ProductionCostEngine.Compute(Example(new CostingPolicy(100m, ReleaseRule: ReleaseValuationRule.EstimatedUnitCost))).Releases;
        // Cost to date: every material issued (34,900) + the day's conversion (4,450), over the 100 kg.
        var toDate = ProductionCostEngine.Compute(Example(new CostingPolicy(100m, ReleaseRule: ReleaseValuationRule.CostToDate))).Releases;

        Assert.Equal(22_200m, standard[0].Value);
        Assert.Equal(21_590m, estimated[0].Value);
        Assert.Equal(39_350m, toDate[0].Value);
        // Whatever the rule, the releases of a completed order add up to its cost, 53,480.
        Assert.All(new[] { standard, estimated, toDate }, r => Assert.Equal(53_480m, r.Sum(x => x.Value)));
        Assert.All(new[] { standard, estimated, toDate }, r => Assert.True(r[1].CarriesRemainder));
    }

    [Fact]
    public void The_employer_burden_is_added_only_to_payslips_without_an_employer_share_and_said()
    {
        var policy = new CostingPolicy(100m, EmployerBurdenPercent: 20m);

        var without = ProductionCostEngine.Compute(Example(policy, withoutEmployerSocial: true));
        var with = ProductionCostEngine.Compute(Example(policy, withoutEmployerSocial: false));

        // 8 h × 480 + 6 h × 420, against payroll's 8 × 400 + 6 × 350.
        Assert.Equal(6_360m, without.Actual.Labour);
        Assert.Equal(5_300m, with.Actual.Labour);
        Assert.Contains(without.Notes, n => n.Code == ProductionCostEngine.NoteEmployerBurden && n.Value == 20m);
        // Never approved, whatever the admin chose.
        Assert.Equal(ProductionCostEngine.StatusProvisional, without.Status);
    }

    [Fact]
    public void The_defaults_change_no_figure()
    {
        var before = ProductionCostEngine.Compute(Example(new CostingPolicy(100m)));
        var after = ProductionCostEngine.Compute(Example(new CostingPolicy(100m, false, ReleaseValuationRule.StandardMaterialPlusConversion, 0m)));
        Assert.Equal(before.Actual, after.Actual);
        Assert.Equal(before.Releases.Select(r => r.Value), after.Releases.Select(r => r.Value));
    }

    [Fact]
    public async Task The_admin_s_choices_are_checked_saved_and_followed_by_the_cost_preview()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var choices = new ManufacturingCostingChoices(s.Db);

        // Nothing saved: the suggested answers.
        var defaults = await choices.SettingsAsync();
        Assert.Equal(("ByNature", 0m, "StandardMaterialPlusConversion", "None", "Monthly", "StayInCharges", "ManagementOnly"),
            (defaults.Model, defaults.OverheadPerLabourHour, defaults.ReleaseRule, defaults.EmployerBurdenUse,
             defaults.MonthEndWorkInProgress, defaults.LateCosts, defaults.Variances));

        UpdateManufacturingCostingSettingsRequest Ask(decimal overhead, string burdenUse, decimal burden, string model = "ByNature") =>
            new(model, overhead, "StandardMaterialPlusConversion", burdenUse, burden, "Monthly", "StayInCharges", "ManagementOnly");
        Assert.NotNull((await choices.UpdateSettingsAsync(Ask(10m, "None", 0m, model: "Planet"), "admin")).Error);
        Assert.NotNull((await choices.UpdateSettingsAsync(Ask(-1m, "None", 0m), "admin")).Error);
        Assert.NotNull((await choices.UpdateSettingsAsync(Ask(10m, "ReportsOnly", 0m), "admin")).Error);

        var (saved, error) = await choices.UpdateSettingsAsync(Ask(50m, "ReportsOnly", 10m), "admin");
        Assert.Null(error);
        Assert.Equal((50m, "ReportsOnly", 10m, "admin"), (saved!.OverheadPerLabourHour, saved.EmployerBurdenUse, saved.EmployerBurdenPercent, saved.UpdatedBy));

        var (_, id) = await ProductionCostingTests.WorkedOrderAsync(s.Db, w);
        var sheet = await OkAsync(s.Db, w.AdminPrincipal, "GET", "/api/manufacturing/orders/{id:guid}/costing", "", Of(id));
        // 8 hours: overhead 8 × 50; labour at Karim's standard 250 plus the 10 % burden the standard rate's missing employer share takes.
        Assert.Equal(400m, sheet.GetProperty("actual").GetProperty("overhead").GetDecimal());
        Assert.Equal(2_200m, sheet.GetProperty("actual").GetProperty("labour").GetDecimal());
        Assert.Equal(50m, sheet.GetProperty("overheadPerLabourHour").GetDecimal());
    }

    [Fact]
    public async Task The_account_map_starts_empty_and_names_only_active_accounts_of_the_chart()
    {
        await using var s = await NewAsync();
        var choices = new ManufacturingCostingChoices(s.Db);

        var empty = await choices.AccountMapAsync();
        Assert.Equal(ManufacturingAccountSlots.All, empty.Select(x => x.Slot));
        Assert.All(empty, x => Assert.Null(x.LedgerAccountId));

        var account = await s.Db.LedgerAccounts.AsNoTracking().FirstAsync(a => a.IsActive);
        Assert.NotNull((await choices.UpdateAccountMapAsync(new([new("nowhere", account.Id)]), "admin")).Error);
        Assert.NotNull((await choices.UpdateAccountMapAsync(new([new(ManufacturingAccountSlots.WorkInProgress, Guid.NewGuid())]), "admin")).Error);

        var (saved, error) = await choices.UpdateAccountMapAsync(new([new(ManufacturingAccountSlots.WorkInProgress, account.Id)]), "admin");
        Assert.Null(error);
        var wip = saved!.Single(x => x.Slot == ManufacturingAccountSlots.WorkInProgress);
        Assert.Equal((account.Id, account.Code, "admin"), (wip.LedgerAccountId, wip.AccountCode, wip.UpdatedBy));
        Assert.All(saved.Where(x => x.Slot != ManufacturingAccountSlots.WorkInProgress), x => Assert.Null(x.LedgerAccountId));

        // Cleared again: back to unmapped.
        (saved, _) = await choices.UpdateAccountMapAsync(new([new(ManufacturingAccountSlots.WorkInProgress, null)]), "admin");
        Assert.Null(saved!.Single(x => x.Slot == ManufacturingAccountSlots.WorkInProgress).LedgerAccountId);
    }
}
