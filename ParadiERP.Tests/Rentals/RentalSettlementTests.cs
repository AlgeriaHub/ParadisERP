using System.Globalization;
using ParadiERP.Application.Regional;
using ParadiERP.Application.Rentals;

namespace ParadiERP.Tests.Rentals;

/// <summary>
/// The money on a rental contract, case by case.
///
/// <para>
/// The scaffolding line below is the shape the owner described: 100 a unit per
/// day; an overdue price of 4,500 (1.5×), so 150 a day; and 2,000 a unit if it does not come
/// back. Every figure asserted here was worked out from those three.
/// </para>
/// </summary>
public class RentalSettlementTests
{
    private static RegionalSettings Money(int decimals = 2) => new() { CurrencyDecimals = decimals };

    private static DateOnly Day(string iso) => DateOnly.Parse(iso, CultureInfo.InvariantCulture);

    private static RentalLine Scaffolding(Guid id, decimal qty = 100, int durationDays = 30) =>
        new(id, qty, durationDays, RentUnitPrice: 100, DamageUnitPrice: 2000, OverdueUnitPrice: 150);

    private static IssueBatch Issued(Guid lineId, string on, decimal qty) =>
        new(Guid.NewGuid(), lineId, Day(on), qty);

    private static ReturnEntry Back(
        Guid lineId, string on, decimal ok = 0, decimal partialDamage = 0,
        decimal maintenance = 0, decimal totalDamage = 0, decimal missing = 0) =>
        new(Guid.NewGuid(), lineId, Day(on), ok, partialDamage, maintenance, totalDamage, missing);

    [Fact]
    public void Fifo_decides_how_many_days_each_returned_unit_is_charged_for()
    {
        // One line, out in two batches nine days apart, all brought back in one go. Nothing on a
        // returned pipe says which note it left on, so the oldest batch closes first.
        var line = Scaffolding(Guid.NewGuid());
        var first = Issued(line.Id, "2026-03-01", 60);
        var second = Issued(line.Id, "2026-03-10", 40);

        var settlement = RentalSettlement.Settle(
            new RentalContractInput([line], [first, second], [Back(line.Id, "2026-03-20", ok: 70)], []),
            Money());

        var rent = settlement.Charges.Where(c => c.Kind == RentalChargeKind.Rent).ToList();
        Assert.Equal(
            [(first.IssueNoteId, 60m, 19), (second.IssueNoteId, 10m, 10)],
            rent.Select(c => (c.IssueNoteId, c.Qty, c.Days)));

        // 60 units for 19 days and 10 units for 10 days, at 100 a unit a day.
        Assert.Equal(114_000m, rent[0].Amount);
        Assert.Equal(10_000m, rent[1].Amount);

        // Thirty units of the second batch are still with the customer, so the line stays open.
        Assert.Equal(30m, settlement.StillOutQty);
        Assert.False(settlement.IsClosed);
    }

    [Fact]
    public void Out_and_back_the_same_day_is_a_days_hire()
    {
        var line = Scaffolding(Guid.NewGuid(), qty: 5);
        var settlement = RentalSettlement.Settle(
            new RentalContractInput(
                [line], [Issued(line.Id, "2026-03-01", 5)], [Back(line.Id, "2026-03-01", ok: 5)], []),
            Money());

        Assert.Equal(1, settlement.Charges.Single().Days);
        Assert.Equal(500m, settlement.Total(RentalChargeKind.Rent));
        Assert.True(settlement.IsClosed);
    }

    [Fact]
    public void An_early_return_gives_back_the_unused_period_as_a_voucher_or_off_the_debt()
    {
        // Ten units taken for a month, back after ten days: twenty days were paid for and not used.
        var line = Scaffolding(Guid.NewGuid(), qty: 10);
        var settlement = RentalSettlement.Settle(
            new RentalContractInput(
                [line], [Issued(line.Id, "2026-03-01", 10)], [Back(line.Id, "2026-03-11", ok: 10)], []),
            Money());

        Assert.Equal(30_000m, settlement.Lines[0].IssuedPeriodRent);
        Assert.Equal(10_000m, settlement.Total(RentalChargeKind.Rent));
        Assert.Equal(20_000m, settlement.UnusedRent);

        // The customer paid on the day: money goes back out.
        var paid = RentalSettlement.Relieve(settlement.UnusedRent, outstandingBalance: 0);
        Assert.Equal(new RentalRelief(DebtReduction: 0m, PaymentVoucher: 20_000m), paid);

        // A contractor on account who has not paid: the debt comes down and no cash moves.
        var onAccount = RentalSettlement.Relieve(settlement.UnusedRent, outstandingBalance: 50_000);
        Assert.Equal(new RentalRelief(20_000m, 0m), onAccount);

        // Part paid: square the account first, and only the rest leaves the till.
        Assert.Equal(new RentalRelief(5_000m, 15_000m), RentalSettlement.Relieve(20_000m, 5_000m));
    }

    [Fact]
    public void An_extension_and_the_same_days_overdue_are_not_the_same_money()
    {
        // Forty days out on a thirty-day period, the ten extra days handled two ways.
        var line = Scaffolding(Guid.NewGuid(), qty: 1);
        var issue = Issued(line.Id, "2026-03-01", 1);
        var back = Back(line.Id, "2026-04-10", ok: 1);

        var late = RentalSettlement.Settle(new RentalContractInput([line], [issue], [back], []), Money());
        Assert.Equal(3_000m, late.Total(RentalChargeKind.Rent));      // 30 days at 100
        Assert.Equal(1_500m, late.Total(RentalChargeKind.Overdue));   // 10 days at 150
        Assert.Equal(4_500m, late.ChargeTotal);

        // The same ten days, signed for on the 25th — before the period ended on the 31st.
        var extension = new RentalExtension(Guid.NewGuid(), line.Id, 10, UnitPrice: 100, RaisedOn: Day("2026-03-25"));
        Assert.False(RentalRules.IsRetroactive(extension, RentalPricing.PeriodEndsOn(issue.IssuedOn, 30)));

        var extended = RentalSettlement.Settle(
            new RentalContractInput([line], [issue], [back], [extension]), Money());

        Assert.Equal(4_000m, extended.Total(RentalChargeKind.Rent));  // 30 days at 100, then 10 more at 100
        Assert.Equal(0m, extended.Total(RentalChargeKind.Overdue));
        Assert.Equal(4_000m, extended.ChargeTotal);

        // If these two came to the same figure, nobody would ever sign an extension again.
        Assert.True(late.ChargeTotal > extended.ChargeTotal);

        // And the money is traceable to the document that created it.
        Assert.Equal(extension.Id, extended.Charges.Last().ExtensionId);
    }

    [Fact]
    public void An_extension_that_is_signed_and_not_used_costs_nothing()
    {
        // Extension days are charged as they are consumed, so returning inside the original
        // period leaves nothing to charge and nothing to refund on the extension itself.
        var line = Scaffolding(Guid.NewGuid(), qty: 1);
        var extension = new RentalExtension(Guid.NewGuid(), line.Id, 10, 3000, Day("2026-03-25"));

        var settlement = RentalSettlement.Settle(
            new RentalContractInput(
                [line], [Issued(line.Id, "2026-03-01", 1)], [Back(line.Id, "2026-03-21", ok: 1)], [extension]),
            Money());

        Assert.Equal(2_000m, settlement.ChargeTotal);          // 20 days at 100
        Assert.Equal(1_000m, settlement.UnusedRent);           // the 10 unused days of the agreed period
        Assert.DoesNotContain(settlement.Charges, c => c.ExtensionId is not null);
    }

    [Fact]
    public void A_partial_return_leaves_the_rest_out_until_a_later_note_closes_the_line()
    {
        var line = Scaffolding(Guid.NewGuid());
        var settlement = RentalSettlement.Settle(
            new RentalContractInput(
                [line],
                [Issued(line.Id, "2026-03-01", 100)],
                [Back(line.Id, "2026-03-15", ok: 40), Back(line.Id, "2026-03-31", ok: 60)],
                []),
            Money());

        var rent = settlement.Charges.Where(c => c.Kind == RentalChargeKind.Rent).ToList();
        Assert.Equal(56_000m, rent[0].Amount);    // 40 units, 14 days
        Assert.Equal(180_000m, rent[1].Amount);   // 60 units, the full 30 days

        // The forty that came back early give back their sixteen unused days; the rest used the
        // whole period, so there is nothing more to give back.
        Assert.Equal(64_000m, settlement.UnusedRent);
        Assert.Equal(300_000m, settlement.Lines[0].IssuedPeriodRent);
        Assert.Equal(settlement.Lines[0].IssuedPeriodRent, settlement.Total(RentalChargeKind.Rent) + settlement.UnusedRent);
        Assert.True(settlement.IsClosed);
    }

    [Fact]
    public void A_return_short_by_forty_units_is_charged_at_the_price_signed_on_the_contract()
    {
        var line = Scaffolding(Guid.NewGuid());
        var settlement = RentalSettlement.Settle(
            new RentalContractInput(
                [line],
                [Issued(line.Id, "2026-03-01", 100)],
                [Back(line.Id, "2026-03-11", ok: 60, missing: 40)],
                []),
            Money());

        // 40 x 2,000 — the total-damage price frozen at signing, not today's price and not cost.
        Assert.Equal(80_000m, settlement.Total(RentalChargeKind.Missing));
        Assert.Equal(2_000m, settlement.Charges.Single(c => c.Kind == RentalChargeKind.Missing).UnitPrice);

        // The forty that are gone were on site for those ten days like everything else, so the
        // hire is charged on all hundred units. The hire and the asset are two different things.
        Assert.Equal(100_000m, settlement.Total(RentalChargeKind.Rent));
        Assert.Equal(100m, settlement.Charges.First(c => c.Kind == RentalChargeKind.Rent).Qty);

        // Issued = returned + settled as lost, so the line is clear and the contract closes.
        Assert.Equal(0m, settlement.StillOutQty);
        Assert.True(settlement.IsClosed);
    }

    [Fact]
    public void An_accessory_rents_at_zero_and_is_still_charged_for_what_goes_missing()
    {
        // A scaffold goes out with couplers. Nobody hires the couplers — but forty short is
        // forty charged.
        var scaffold = Scaffolding(Guid.NewGuid(), qty: 20, durationDays: 45);
        var couplers = new RentalLine(
            Guid.NewGuid(), Qty: 500, DurationDays: 1, RentUnitPrice: 0,
            DamageUnitPrice: 500, OverdueUnitPrice: 0, ParentLineId: scaffold.Id);

        RentalRules.Validate(couplers);

        // Its own duration is meaningless: a coupler cannot expire before the scaffold it is
        // bolted to, so it takes its parent's forty-five days.
        Assert.Equal(45, RentalSettlement.DurationOf(couplers, [scaffold, couplers]));

        var settlement = RentalSettlement.Settle(
            new RentalContractInput(
                [scaffold, couplers],
                [Issued(couplers.Id, "2026-03-01", 500)],
                [Back(couplers.Id, "2026-04-20", ok: 460, missing: 40)],
                // An extension raised on the scaffold reaches the couplers as days, never as price.
                [new RentalExtension(Guid.NewGuid(), scaffold.Id, 10, 3000, Day("2026-04-10"))]),
            Money());

        Assert.Equal(20_000m, settlement.Total(RentalChargeKind.Missing));
        Assert.Equal(0m, settlement.Total(RentalChargeKind.Rent));
        Assert.Equal(0m, settlement.Total(RentalChargeKind.Overdue));

        // "No value" applies to the rent line only: there is no zero-rent line at all.
        Assert.Equal([RentalChargeKind.Missing], settlement.Charges.Select(c => c.Kind));
        Assert.Equal(0m, settlement.UnusedRent);
    }

    [Fact]
    public void Damage_and_maintenance_are_charged_separately_from_the_hire()
    {
        var line = Scaffolding(Guid.NewGuid(), qty: 20);
        var settlement = RentalSettlement.Settle(
            new RentalContractInput(
                [line],
                [Issued(line.Id, "2026-03-01", 20)],
                [Back(line.Id, "2026-03-11", ok: 12, partialDamage: 5, maintenance: 1250, totalDamage: 3)],
                []),
            Money());

        Assert.Equal(1_250m, settlement.Total(RentalChargeKind.Maintenance));   // what the repair costs
        Assert.Equal(6_000m, settlement.Total(RentalChargeKind.Damage));        // 3 x 2,000, beyond repair
        Assert.Equal(20_000m, settlement.Total(RentalChargeKind.Rent));         // all 20 units, 10 days

        // The five damaged-but-usable units came back: the line is clear.
        Assert.True(settlement.IsClosed);
    }

    [Fact]
    public void The_figures_add_up_to_what_the_customer_is_shown()
    {
        // The daily rate is 100/30 (3.3333...). Pricing the used days and the unused days separately
        // and rounding each gives 6.67 + 16.67 = 23.34 against an invoice of 23.33 — a centime
        // out of nowhere, on every line, every month.
        var line = new RentalLine(Guid.NewGuid(), Qty: 1, DurationDays: 7,
            RentUnitPrice: 100m / 30m, DamageUnitPrice: 0, OverdueUnitPrice: 5);

        var settlement = RentalSettlement.Settle(
            new RentalContractInput(
                [line], [Issued(line.Id, "2026-03-01", 1)], [Back(line.Id, "2026-03-03", ok: 1)], []),
            Money());

        Assert.Equal(23.33m, settlement.Lines[0].IssuedPeriodRent);
        Assert.Equal(6.67m, settlement.Total(RentalChargeKind.Rent));
        Assert.Equal(16.66m, settlement.UnusedRent);
        Assert.Equal(settlement.Lines[0].IssuedPeriodRent, settlement.ChargeTotal + settlement.UnusedRent);
    }

    [Theory]
    [InlineData(0, 2567, 4433)]
    [InlineData(2, 2566.67, 4433.33)]
    [InlineData(3, 2566.667, 4433.333)]
    public void What_is_charged_and_what_is_given_back_reconcile_in_any_currency(
        int decimals, decimal earned, decimal unused)
    {
        // Currency decimals are a country setting, not always two.
        var line = new RentalLine(Guid.NewGuid(), Qty: 7, DurationDays: 30,
            RentUnitPrice: 1000m / 30m, DamageUnitPrice: 0, OverdueUnitPrice: 50);

        var settlement = RentalSettlement.Settle(
            new RentalContractInput(
                [line], [Issued(line.Id, "2026-03-01", 7)], [Back(line.Id, "2026-03-12", ok: 7)], []),
            Money(decimals));

        Assert.Equal(earned, settlement.Total(RentalChargeKind.Rent));
        Assert.Equal(unused, settlement.UnusedRent);
        Assert.Equal(7_000m, settlement.Lines[0].IssuedPeriodRent);
        Assert.Equal(7_000m, settlement.ChargeTotal + settlement.UnusedRent);
    }

    [Fact]
    public void A_contract_closes_on_what_went_out_not_on_what_was_contracted()
    {
        var issuedLine = Scaffolding(Guid.NewGuid(), qty: 100);
        var neverIssued = Scaffolding(Guid.NewGuid(), qty: 50);
        var issue = Issued(issuedLine.Id, "2026-03-01", 60);

        // 60 of a contracted 100 went out, and 60 came back. The 40 never issued have no bearing.
        var closed = RentalSettlement.Settle(
            new RentalContractInput(
                [issuedLine, neverIssued], [issue], [Back(issuedLine.Id, "2026-03-20", ok: 60)], []),
            Money());
        Assert.True(closed.IsClosed);

        var open = RentalSettlement.Settle(
            new RentalContractInput(
                [issuedLine, neverIssued], [issue], [Back(issuedLine.Id, "2026-03-20", ok: 59)], []),
            Money());
        Assert.False(open.IsClosed);

        // A contract that has issued nothing has not finished — it has not started.
        var untouched = RentalSettlement.Settle(new RentalContractInput([issuedLine, neverIssued]), Money());
        Assert.False(untouched.IsClosed);
    }

    [Fact]
    public void A_return_bigger_than_what_went_out_is_reported_rather_than_absorbed()
    {
        var line = Scaffolding(Guid.NewGuid(), qty: 100);
        var note = Back(line.Id, "2026-03-11", ok: 12);

        // The screen should have refused it...
        Assert.Equal("exceedsIssued", RentalRules.ReturnProblem(note, issuedQty: 10, alreadySettled: 0));

        // ...and if it is saved anyway, the two units that were never issued are visible rather
        // than charged to the oldest batch as if they had been.
        var settlement = RentalSettlement.Settle(
            new RentalContractInput([line], [Issued(line.Id, "2026-03-01", 10)], [note], []), Money());

        Assert.Equal(2m, settlement.Lines[0].OverReturnedQty);
        Assert.Equal(10m, settlement.Lines[0].SettledQty);
        Assert.Equal(10m, settlement.Charges.First(c => c.Kind == RentalChargeKind.Rent).Qty);
    }

    [Fact]
    public void Overdue_runs_from_each_batchs_own_issue_date()
    {
        // The same return date: the older batch is overdue, the newer one is not. This is why
        // the unit of calculation is the batch and not the contract.
        var line = Scaffolding(Guid.NewGuid());
        var older = Issued(line.Id, "2026-03-01", 10);
        var newer = Issued(line.Id, "2026-03-20", 10);

        var settlement = RentalSettlement.Settle(
            new RentalContractInput([line], [older, newer], [Back(line.Id, "2026-04-05", ok: 20)], []),
            Money());

        var overdue = settlement.Charges.Where(c => c.Kind == RentalChargeKind.Overdue).ToList();
        Assert.Equal(older.IssueNoteId, overdue.Single().IssueNoteId);
        Assert.Equal(5, overdue.Single().Days);            // 35 days out on a 30-day period
        Assert.Equal(7_500m, overdue.Single().Amount);     // 5 days x 10 units x 150 a day
        Assert.Equal(16, settlement.Charges.First(c => c.IssueNoteId == newer.IssueNoteId).Days);
    }
}
