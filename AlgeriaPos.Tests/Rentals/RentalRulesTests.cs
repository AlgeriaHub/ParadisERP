using System.Globalization;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Application.Rentals;

namespace AlgeriaPos.Tests.Rentals;

/// <summary>
/// What may be raised, and by whom. These are the checks a screen runs before saving; the
/// arithmetic in <see cref="RentalSettlementTests"/> assumes they have already passed.
/// </summary>
public class RentalRulesTests
{
    private static DateOnly Day(string iso) => DateOnly.Parse(iso, CultureInfo.InvariantCulture);

    private static RentalLine Line(Guid? id = null) =>
        new(id ?? Guid.NewGuid(), Qty: 100, DurationDays: 30,
            RentUnitPrice: 100, DamageUnitPrice: 2000, OverdueUnitPrice: 150);

    [Fact]
    public void A_contract_locks_as_soon_as_the_first_issue_note_is_raised()
    {
        var line = Line();
        Assert.False(RentalRules.IsLocked([]));
        Assert.True(RentalRules.IsLocked([new IssueBatch(Guid.NewGuid(), line.Id, Day("2026-03-01"), 10)]));
    }

    [Fact]
    public void A_line_needs_prices_that_can_produce_honest_money()
    {
        RentalRules.Validate(Line());

        Assert.Throws<ArgumentException>(() => RentalRules.Validate(Line() with { Qty = 0 }));
        Assert.Throws<ArgumentException>(() => RentalRules.Validate(Line() with { DurationDays = 0 }));
        Assert.Throws<ArgumentException>(() => RentalRules.Validate(Line() with { DamageUnitPrice = -1 }));

        // An overdue price below the rental price would make being late the cheaper option.
        Assert.Throws<ArgumentException>(() => RentalRules.Validate(Line() with { OverdueUnitPrice = 50 }));
    }

    [Fact]
    public void An_accessory_rents_at_zero_and_must_carry_a_compensation_price()
    {
        var parent = Guid.NewGuid();
        var couplers = new RentalLine(Guid.NewGuid(), 500, 1, 0, DamageUnitPrice: 500, OverdueUnitPrice: 0, ParentLineId: parent);
        RentalRules.Validate(couplers);

        // Charging rent for a coupler is not the mistake; forgetting what it costs when it is
        // lost is, because that one cannot be recovered after the goods are gone.
        Assert.Throws<ArgumentException>(() => RentalRules.Validate(couplers with { DamageUnitPrice = 0 }));
        Assert.Throws<ArgumentException>(() =>
            RentalRules.Validate(couplers with { RentUnitPrice = 100, OverdueUnitPrice = 150 }));
    }

    [Fact]
    public void SignatoryProblem_is_silent_once_locked()
    {
        // The rule bites at the first note, the one that locks; a second note never asks.
        Assert.Null(RentalRules.SignatoryProblem(locked: true, required: true, name: null));
        Assert.Null(RentalRules.SignatoryProblem(locked: true, required: true, name: ""));
    }

    [Fact]
    public void SignatoryProblem_is_silent_when_the_contract_predates_the_rule()
    {
        Assert.Null(RentalRules.SignatoryProblem(locked: false, required: false, name: null));
    }

    [Fact]
    public void SignatoryProblem_names_the_missing_name()
    {
        Assert.Equal("signatoryRequired", RentalRules.SignatoryProblem(locked: false, required: true, name: null));
        Assert.Equal("signatoryRequired", RentalRules.SignatoryProblem(locked: false, required: true, name: "   "));
        Assert.Null(RentalRules.SignatoryProblem(locked: false, required: true, name: "Karim Ben Ali"));
    }

    [Fact]
    public void SignatoryProblem_does_not_ask_for_the_id_number()
    {
        // The owner's requirement is that the paper keeps the name. The number's absence is said
        // on screen in words; blocking on it would stop goods a company already agreed to hire.
        Assert.Null(RentalRules.SignatoryProblem(locked: false, required: true, name: "Karim Ben Ali"));
        Assert.Equal(3, typeof(RentalRules).GetMethod(nameof(RentalRules.SignatoryProblem))!.GetParameters().Length);
    }

    [Fact]
    public void Issues_never_exceed_the_contracted_quantity()
    {
        var line = Line();
        Assert.Null(RentalRules.IssueProblem(line, alreadyIssued: 60, qty: 40));
        Assert.Equal("exceedsContracted", RentalRules.IssueProblem(line, 60, 41));
        Assert.Equal("quantityRequired", RentalRules.IssueProblem(line, 0, 0));
    }

    [Fact]
    public void A_return_note_is_checked_against_what_is_still_out()
    {
        var line = Line();
        var note = new ReturnEntry(Guid.NewGuid(), line.Id, Day("2026-03-11"), QtyOk: 40);

        Assert.Null(RentalRules.ReturnProblem(note, issuedQty: 60, alreadySettled: 20));
        Assert.Equal("exceedsIssued", RentalRules.ReturnProblem(note, 60, 21));
        Assert.Equal("quantityRequired", RentalRules.ReturnProblem(note with { QtyOk = 0 }, 60, 0));
        Assert.Equal("negativeQuantity", RentalRules.ReturnProblem(note with { QtyMissing = -1 }, 60, 0));

        // A repair charged against nothing damaged is a typo in the wrong box.
        Assert.Equal("maintenanceNeedsDamagedUnits",
            RentalRules.ReturnProblem(note with { MaintenanceValue = 500 }, 60, 0));
        Assert.Null(RentalRules.ReturnProblem(
            note with { QtyOk = 35, QtyPartialDamage = 5, MaintenanceValue = 500 }, 60, 20));
    }

    [Fact]
    public void An_extension_signed_before_the_period_ends_needs_nobodys_permission()
    {
        var periodEnds = Day("2026-03-31");
        var extension = new RentalExtension(Guid.NewGuid(), null, 10, 3000, RaisedOn: Day("2026-03-25"));

        Assert.Null(RentalRules.ExtensionProblem(extension, periodEnds, isManager: false));
        // Signed on the last day of the period it is still an extension, not a consequence.
        Assert.Null(RentalRules.ExtensionProblem(extension with { RaisedOn = periodEnds }, periodEnds, false));
    }

    [Fact]
    public void A_retroactive_extension_is_a_managers_decision_and_is_written_down()
    {
        // Left open to everyone, every overdue quietly becomes an extension and the overdue line
        // stops meaning anything.
        var periodEnds = Day("2026-03-31");
        var late = new RentalExtension(Guid.NewGuid(), null, 10, 3000, RaisedOn: Day("2026-04-03"));

        Assert.True(RentalRules.IsRetroactive(late, periodEnds));
        Assert.Equal("retroactiveNeedsManager", RentalRules.ExtensionProblem(late, periodEnds, isManager: false));
        Assert.Equal("retroactiveNeedsReason", RentalRules.ExtensionProblem(late, periodEnds, isManager: true));
        Assert.Null(RentalRules.ExtensionProblem(
            late with { Reason = "Site flooded; agreed verbally on the 30th." }, periodEnds, true));

        Assert.Equal("daysRequired", RentalRules.ExtensionProblem(late with { ExtraDays = 0 }, periodEnds, true));
    }

    [Fact]
    public void An_extension_is_measured_against_the_batch_that_expires_first()
    {
        var line = Guid.NewGuid();
        var batches = new IssueBatch[]
        {
            new(Guid.NewGuid(), line, Day("2026-03-10"), 40),
            new(Guid.NewGuid(), line, Day("2026-03-01"), 60)
        };

        // The first batch out is the one about to expire, so that is the deadline to beat.
        Assert.Equal(Day("2026-03-31"), RentalRules.SoonestPeriodEnd(batches, entitledDays: 30));
        Assert.Null(RentalRules.SoonestPeriodEnd([], 30));
    }

    [Fact]
    public void The_overdue_multiplier_stays_between_one_and_ten()
    {
        RentalRules.Validate(new RentalSettings());
        Assert.Throws<ArgumentException>(() => RentalRules.Validate(new RentalSettings(OverdueMultiplier: 0.9m)));
        Assert.Throws<ArgumentException>(() => RentalRules.Validate(new RentalSettings(OverdueMultiplier: 11m)));
    }

    [Fact]
    public void The_admins_multiplier_is_a_floor_for_a_signed_overdue_price()
    {
        // Blank derives the floor itself; at or above it is a negotiation; below it is signing
        // away the admin's decision, and every contract would quietly go out at 1.0×.
        Assert.Null(RentalRules.OverduePriceProblem(null, floor: 4500));
        Assert.Null(RentalRules.OverduePriceProblem(4500, floor: 4500));
        Assert.Null(RentalRules.OverduePriceProblem(5000, floor: 4500));
        Assert.Equal("overdueBelowFloor", RentalRules.OverduePriceProblem(4499.99m, floor: 4500));
        // An accessory rents at zero, so its floor is zero and nothing is ever refused there.
        Assert.Null(RentalRules.OverduePriceProblem(0, floor: 0));
    }

    [Fact]
    public void The_deposit_is_owed_back_at_close_and_only_then()
    {
        var off = new RentalSettings();
        var on = off with { DepositEnabled = true };

        // Nothing owed back while goods are out; the whole figure once the contract closes.
        Assert.Equal(0m, RentalDeposit.ToReturn(on, 50_000, isClosed: false));
        Assert.Equal(50_000m, RentalDeposit.ToReturn(on, 50_000, isClosed: true));
        // Off hides the figure in both directions — and a negative stored figure is never a debt.
        Assert.Equal(0m, RentalDeposit.ToReturn(off, 50_000, isClosed: true));
        Assert.Equal(0m, RentalDeposit.ToReturn(on, -1, isClosed: true));
    }

    [Fact]
    public void The_deposit_is_built_and_switched_off_and_nothing_computes_with_it()
    {
        var off = new RentalSettings();
        var on = off with { DepositEnabled = true };

        Assert.False(off.DepositEnabled);
        Assert.False(RentalDeposit.IsVisible(off));
        Assert.Equal(0m, RentalDeposit.Held(off, contractDeposit: 50_000));
        Assert.Equal(50_000m, RentalDeposit.Held(on, 50_000));

        // And a deposit sitting on the contract changes nothing a customer is charged: it is a
        // liability held on their behalf, never a payment against what they owe.
        var line = Line() with { Qty = 10 };
        var settlement = RentalSettlement.Settle(
            new RentalContractInput(
                [line],
                [new IssueBatch(Guid.NewGuid(), line.Id, Day("2026-03-01"), 10)],
                [new ReturnEntry(Guid.NewGuid(), line.Id, Day("2026-03-31"), QtyOk: 10)],
                []),
            new RegionalSettings { CurrencyDecimals = 2 });

        Assert.Equal(30_000m, settlement.ChargeTotal);
        Assert.Equal(0m, settlement.UnusedRent);
    }
}
