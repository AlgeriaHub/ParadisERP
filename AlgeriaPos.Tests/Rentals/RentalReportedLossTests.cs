using AlgeriaPos.Application.Regional;
using AlgeriaPos.Application.Rentals;

namespace AlgeriaPos.Tests.Rentals;

/// <summary>
/// Rent on a unit that never comes back stops the day the customer reports it gone, not the
/// day the loss is finally settled.
///
/// <para>
/// The owner's reasoning: reporting is what lets the company write the unit off, charge for it
/// and stop waiting for it. A customer who says nothing leaves the company unable to re-hire a
/// unit it does not know is gone — so the meter keeps running, and telling them late is never
/// the cheaper option.
/// </para>
/// </summary>
public class RentalReportedLossTests
{
    private static readonly Guid LineId = Guid.NewGuid();
    private static readonly Guid IssueId = Guid.NewGuid();
    private static readonly DateOnly IssuedOn = new(2026, 1, 1);

    private static RegionalSettings Regional() => RegionalPresets.Create("DZ")!;

    /// <summary>
    /// One scaffold hired for 30 days at 3000 a month — 100 a day — with overdue frozen at
    /// double and 5000 to replace it.
    /// </summary>
    private static RentalContractInput Contract(ReturnEntry settlement) => new(
        [new RentalLine(LineId, Qty: 1, DurationDays: 30, RentUnitPrice: 100,
            DamageUnitPrice: 5000m, OverdueUnitPrice: 200)],
        [new IssueBatch(IssueId, LineId, IssuedOn, Qty: 1)],
        [settlement],
        []);

    private static ContractSettlement Settle(ReturnEntry settlement) =>
        RentalSettlement.Settle(Contract(settlement), Regional());

    private static ReturnEntry Lost(DateOnly settledOn, DateOnly? reportedOn = null) =>
        new(Guid.NewGuid(), LineId, settledOn, QtyMissing: 1, ReportedOn: reportedOn);

    private static decimal Hire(ContractSettlement result) =>
        result.Charges.Where(c => c.Kind is RentalChargeKind.Rent or RentalChargeKind.Overdue).Sum(c => c.Amount);

    private static decimal Compensation(ContractSettlement result) =>
        result.Charges.Where(c => c.Kind is RentalChargeKind.Missing or RentalChargeKind.Damage).Sum(c => c.Amount);

    [Fact]
    public void Reporting_the_loss_stops_the_rent_that_day()
    {
        // Lost on the 11th and said so; the paperwork catches up on the 31st.
        var result = Settle(Lost(new DateOnly(2026, 1, 31), reportedOn: new DateOnly(2026, 1, 11)));

        // Ten days on site at 100, not thirty.
        Assert.Equal(1000m, Hire(result));
        // And the asset is charged regardless — the hire and the asset are separate things.
        Assert.Equal(5000m, Compensation(result));
    }

    [Fact]
    public void Saying_nothing_keeps_the_meter_running_to_the_settlement()
    {
        var result = Settle(Lost(new DateOnly(2026, 1, 31)));

        // Thirty days, because nobody knew it was gone and it could not be re-hired.
        Assert.Equal(3000m, Hire(result));
        Assert.Equal(5000m, Compensation(result));
    }

    [Fact]
    public void Reporting_late_is_never_cheaper_than_reporting_early()
    {
        var settledOn = new DateOnly(2026, 1, 31);
        var early = Settle(Lost(settledOn, reportedOn: new DateOnly(2026, 1, 6)));
        var late = Settle(Lost(settledOn, reportedOn: new DateOnly(2026, 1, 26)));

        // If this ever inverted, the incentive would be to keep quiet — which is the whole
        // reason the report date exists.
        Assert.True(Hire(early) < Hire(late));
        Assert.Equal(Compensation(early), Compensation(late));
    }

    [Fact]
    public void A_report_that_outlives_the_period_still_pays_the_overdue_rate()
    {
        // Hired for 30 days and reported gone well after: the days past the period are priced
        // as overdue, exactly as they would be for a unit that came back late.
        var result = Settle(Lost(new DateOnly(2026, 3, 1), reportedOn: new DateOnly(2026, 2, 15)));

        var rent = result.Charges.Where(c => c.Kind == RentalChargeKind.Rent).Sum(c => c.Amount);
        var overdue = result.Charges.Where(c => c.Kind == RentalChargeKind.Overdue).Sum(c => c.Amount);

        Assert.Equal(3000m, rent);                 // the 30 agreed days at 100
        Assert.True(overdue > 0);                  // and the rest above the rental rate
        Assert.Equal(3000m + overdue, Hire(result));
    }

    [Fact]
    public void A_report_the_arithmetic_cannot_use_is_ignored_rather_than_trusted()
    {
        var settledOn = new DateOnly(2026, 1, 31);

        // Dated after the settlement, or before the goods went out: both are data-entry
        // errors, and neither may quietly shorten or lengthen the hire.
        foreach (var nonsense in new[] { new DateOnly(2026, 2, 20), new DateOnly(2025, 12, 1) })
        {
            Assert.Equal(3000m, Hire(Settle(Lost(settledOn, reportedOn: nonsense))));
        }
    }

    [Fact]
    public void A_reported_wreck_that_comes_back_stops_rent_on_the_report_day()
    {
        // Reported destroyed on the 11th, the wreck handed back for scrap on the 31st. The owner's
        // decision (docs/07 §3.4, 2026-09-24): a unit reported destroyed is no more usable than one
        // reported lost, so the meter stops on the report day — and the asset is charged in full.
        var result = Settle(new ReturnEntry(
            Guid.NewGuid(), LineId, ReturnedOn: new DateOnly(2026, 1, 31),
            QtyTotalDamage: 1, ReportedOn: new DateOnly(2026, 1, 11)));

        Assert.Equal(1000m, Hire(result));
        Assert.Equal(5000m, Compensation(result));
    }

    [Fact]
    public void A_report_on_one_line_does_not_shorten_another_lines_hire_on_the_same_note()
    {
        // Two scaffolds on two lines, out together; one note settles both. The first is reported
        // lost on the 11th; the second simply comes back on the 31st. The report date is the
        // first line's fact, and the second line is charged to the day its unit actually returned.
        var otherLine = Guid.NewGuid();
        var otherIssue = Guid.NewGuid();
        var noteId = Guid.NewGuid();
        var input = new RentalContractInput(
            [
                new RentalLine(LineId, Qty: 1, DurationDays: 30, RentUnitPrice: 100, DamageUnitPrice: 5000m, OverdueUnitPrice: 200),
                new RentalLine(otherLine, Qty: 1, DurationDays: 30, RentUnitPrice: 100, DamageUnitPrice: 5000m, OverdueUnitPrice: 200)
            ],
            [new IssueBatch(IssueId, LineId, IssuedOn, Qty: 1), new IssueBatch(otherIssue, otherLine, IssuedOn, Qty: 1)],
            [
                new ReturnEntry(noteId, LineId, new DateOnly(2026, 1, 31), QtyMissing: 1, ReportedOn: new DateOnly(2026, 1, 11)),
                new ReturnEntry(noteId, otherLine, new DateOnly(2026, 1, 31), QtyOk: 1)
            ],
            []);

        var result = RentalSettlement.Settle(input, Regional());
        var lost = result.Lines.Single(l => l.LineId == LineId);
        var returned = result.Lines.Single(l => l.LineId == otherLine);

        Assert.Equal(1000m, lost.Total(RentalChargeKind.Rent));       // ten days, to the report
        Assert.Equal(5000m, lost.Total(RentalChargeKind.Missing));
        Assert.Equal(3000m, returned.Total(RentalChargeKind.Rent));   // thirty days, to the return
        Assert.Equal(0m, returned.Total(RentalChargeKind.Missing));
        Assert.True(result.IsClosed);
    }

    [Fact]
    public void A_unit_that_walks_back_in_is_unaffected_by_a_report_date()
    {
        // Reporting damage on something that then comes back does not shorten its hire: the
        // customer had the use of it right up to the day they returned it.
        var result = Settle(new ReturnEntry(
            Guid.NewGuid(), LineId, ReturnedOn: new DateOnly(2026, 1, 21),
            QtyOk: 1, ReportedOn: new DateOnly(2026, 1, 11)));

        Assert.Equal(2000m, Hire(result));
        Assert.Equal(0m, Compensation(result));
    }
}
