using System.Globalization;
using ParadiERP.Application.Contracts;
using ParadiERP.Application.Regional;
using ParadiERP.Application.Rentals;
using Microsoft.EntityFrameworkCore;
using static ParadiERP.Tests.Rentals.RentalTestPaper;

namespace ParadiERP.Tests.Rentals;

/// <summary>
/// The minimum hire (owner, early return decision B). The agreed period is invoiced at issue; a
/// line back before the minimum is charged the minimum — or its period, when that is shorter —
/// and only the days beyond are given back as unused rent. A minimum of 0 is the old rule.
///
/// <para>
/// Ten units at 100 a unit a day for 30 days: 30,000 invoiced when they go out. Out on the 1st,
/// back on the 6th is five days (the return day does not count).
/// </para>
/// </summary>
public class RentalMinimumDaysTests
{
    private static readonly RegionalSettings Money = new() { CurrencyDecimals = 2 };

    private static DateOnly Day(string iso) => DateOnly.Parse(iso, CultureInfo.InvariantCulture);

    private static RentalLine Scaffolding(int durationDays = 30, decimal qty = 10) =>
        new(Guid.NewGuid(), qty, durationDays, RentUnitPrice: 100, DamageUnitPrice: 2000, OverdueUnitPrice: 150);

    private static ReturnEntry Back(Guid lineId, string on, decimal ok) =>
        new(Guid.NewGuid(), lineId, Day(on), ok, 0, 0, 0, 0);

    private static ContractSettlement Settle(RentalLine line, int minimumDays, params ReturnEntry[] returns) =>
        RentalSettlement.Settle(
            new RentalContractInput(
                [line], [new IssueBatch(Guid.NewGuid(), line.Id, Day("2026-03-01"), line.Qty)], returns, [],
                minimumDays),
            Money);

    [Theory]
    [InlineData(5, 30, 0, 5)]
    [InlineData(5, 30, 15, 15)]
    [InlineData(15, 30, 15, 15)]
    [InlineData(20, 30, 15, 20)]
    [InlineData(3, 7, 15, 7)]    // the period is shorter than the minimum: the period is the most
    [InlineData(10, 7, 15, 10)]  // late days are still days out
    [InlineData(0, 30, -4, 0)]
    public void Charged_days_are_the_days_out_but_never_below_the_minimum_or_above_the_period_for_it(
        int daysOut, int duration, int minimum, int expected) =>
        Assert.Equal(expected, RentalPeriod.ChargedDays(daysOut, duration, minimum));

    [Fact]
    public void A_minimum_of_zero_changes_nothing()
    {
        var line = Scaffolding();
        var back = Back(line.Id, "2026-03-06", 10);
        var without = RentalSettlement.Settle(
            new RentalContractInput(
                [line], [new IssueBatch(Guid.NewGuid(), line.Id, Day("2026-03-01"), 10)], [back], []),
            Money);
        var withZero = Settle(line, 0, back);

        Assert.Equal(5, withZero.Charges.Single().Days);
        Assert.Equal(5_000m, withZero.Total(RentalChargeKind.Rent));
        Assert.Equal(25_000m, withZero.UnusedRent);
        Assert.Equal(without.Total(RentalChargeKind.Rent), withZero.Total(RentalChargeKind.Rent));
        Assert.Equal(without.UnusedRent, withZero.UnusedRent);
    }

    [Theory]
    [InlineData("2026-03-06", 15, 15_000, 15_000)] // day 5: charged the minimum
    [InlineData("2026-03-16", 15, 15_000, 15_000)] // day 15: exactly the minimum
    [InlineData("2026-03-21", 20, 20_000, 10_000)] // day 20: past the minimum, the days used
    [InlineData("2026-03-31", 30, 30_000, 0)]      // the whole period
    public void A_fifteen_day_minimum_on_a_thirty_day_period(string returnedOn, int days, decimal rent, decimal unused)
    {
        var line = Scaffolding();
        var settlement = Settle(line, 15, Back(line.Id, returnedOn, 10));

        var charge = settlement.Charges.Single();
        Assert.Equal(RentalChargeKind.Rent, charge.Kind);
        Assert.Equal(days, charge.Days);
        Assert.Equal(rent, settlement.Total(RentalChargeKind.Rent));
        Assert.Equal(unused, settlement.UnusedRent);
        // Charged and given back always add up to what was invoiced at issue.
        Assert.Equal(30_000m, settlement.Total(RentalChargeKind.Rent) + settlement.UnusedRent);
    }

    [Fact]
    public void A_period_shorter_than_the_minimum_is_charged_in_full_and_nothing_comes_back()
    {
        var line = Scaffolding(durationDays: 7);
        var settlement = Settle(line, 15, Back(line.Id, "2026-03-04", 10));

        Assert.Equal(7, settlement.Charges.Single().Days);
        Assert.Equal(7_000m, settlement.Total(RentalChargeKind.Rent));
        Assert.Equal(0m, settlement.UnusedRent);
        Assert.Equal(0m, settlement.Total(RentalChargeKind.Overdue));
    }

    [Fact]
    public void A_period_shorter_than_the_minimum_still_runs_late_exactly_as_before()
    {
        var line = Scaffolding(durationDays: 7);
        var withMinimum = Settle(line, 15, Back(line.Id, "2026-03-11", 10));
        var withoutMinimum = Settle(line, 0, Back(line.Id, "2026-03-11", 10));

        Assert.Equal(7_000m, withMinimum.Total(RentalChargeKind.Rent));
        Assert.Equal(4_500m, withMinimum.Total(RentalChargeKind.Overdue)); // 3 days × 10 × 150
        Assert.Equal(withoutMinimum.Total(RentalChargeKind.Overdue), withMinimum.Total(RentalChargeKind.Overdue));
        Assert.Equal(0m, withMinimum.UnusedRent);
    }

    [Fact]
    public void Each_part_returned_is_held_to_the_minimum_on_its_own_days()
    {
        // Four back on day 5, three on day 20, the last three on day 30.
        var line = Scaffolding();
        var settlement = Settle(line, 15,
            Back(line.Id, "2026-03-06", 4),
            Back(line.Id, "2026-03-21", 3),
            Back(line.Id, "2026-03-31", 3));

        Assert.Equal(
            [(4m, 15, 6_000m), (3m, 20, 6_000m), (3m, 30, 9_000m)],
            settlement.Charges.Select(c => (c.Qty, c.Days, c.Amount)));
        // 4 × 15 days and 3 × 10 days were invoiced and not used; the last three used it all.
        Assert.Equal(9_000m, settlement.UnusedRent);
        Assert.Equal(30_000m, settlement.Total(RentalChargeKind.Rent) + settlement.UnusedRent);
        Assert.True(settlement.IsClosed);
    }

    [Fact]
    public void Partial_returns_without_a_minimum_give_back_every_unused_day()
    {
        var line = Scaffolding();
        var settlement = Settle(line, 0,
            Back(line.Id, "2026-03-06", 4),
            Back(line.Id, "2026-03-21", 3),
            Back(line.Id, "2026-03-31", 3));

        Assert.Equal([5, 20, 30], settlement.Charges.Select(c => c.Days));
        Assert.Equal(13_000m, settlement.UnusedRent); // 4 × 25 + 3 × 10 days at 100
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(367)]
    public void The_minimum_is_a_number_of_days_an_admin_can_mean(int minimum) =>
        Assert.Throws<ArgumentException>(() => RentalRules.Validate(new RentalSettings(MinimumDays: minimum)));
}

public partial class RentalDocumentTests
{
    [Fact]
    public async Task The_minimum_hire_is_frozen_on_the_contract_when_it_is_raised()
    {
        await using var h = await NewAsync();
        Assert.True((await h.Rentals.UpdateSettingsAsync(new UpdateRentalSettingsRequest(1.5m, false, MinimumDays: 15))).Ok);
        Assert.Equal(15, (await h.Rentals.SettingsAsync()).MinimumDays);

        var contract = (await h.Rentals.CreateContractAsync(Contract(qty: 1, days: 30), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;
        Assert.True((await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 1)], Utc("2026-09-01T09:00:00"))), null, "storeman")).Ok);

        // A later change of the setting reaches new contracts only.
        Assert.True((await h.Rentals.UpdateSettingsAsync(new UpdateRentalSettingsRequest(1.5m, false))).Ok);
        var back = await h.Rentals.ReturnAsync(contract.Id,
            Driven(new ReturnNoteRequest(Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 1)], Utc("2026-09-06T09:00:00"))), null, "clerk");
        Assert.True(back.Ok, back.Message);

        var settled = (await h.Rentals.SettleAsync(contract.Id))!;
        Assert.Equal(15, settled.Charges.Single(c => c.Kind == RentalChargeKind.Rent).Days);
        Assert.Equal(15, (await h.Db.RentalContracts.AsNoTracking().SingleAsync(c => c.Id == contract.Id)).MinimumRentalDays);

        var later = (await h.Rentals.CreateContractAsync(Contract(qty: 1, days: 30), "admin")).Value!;
        Assert.Equal(0, (await h.Db.RentalContracts.AsNoTracking().SingleAsync(c => c.Id == later.Id)).MinimumRentalDays);
    }

    [Fact]
    public async Task A_minimum_hire_out_of_range_is_refused()
    {
        await using var h = await NewAsync();
        Assert.False((await h.Rentals.UpdateSettingsAsync(new UpdateRentalSettingsRequest(1.5m, false, MinimumDays: -1))).Ok);
    }
}
