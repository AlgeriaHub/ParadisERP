using ParadiERP.Application.Receivables;

namespace ParadiERP.Tests.Receivables;

/// <summary>
/// The boundaries of the ageing bands, and what an invoice still owes.
///
/// <para>
/// Both are pure, and both are worth their own tests because both decide money. An off-by-one
/// at thirty days moves a contractor between two columns the owner reads very differently, and
/// <see cref="InvoiceStanding.Outstanding"/> is where "a pending cheque does not reduce a debt"
/// is actually enforced.
/// </para>
/// </summary>
public class DebtAgeingTests
{
    private static readonly DateTime Today = new(2026, 9, 23, 14, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, AgeingBucket.Current)]      // due today: due, not late
    [InlineData(-5, AgeingBucket.Current)]     // due next week
    [InlineData(1, AgeingBucket.Days1To30)]
    [InlineData(30, AgeingBucket.Days1To30)]
    [InlineData(31, AgeingBucket.Days31To60)]
    [InlineData(60, AgeingBucket.Days31To60)]
    [InlineData(61, AgeingBucket.Days61To90)]
    [InlineData(90, AgeingBucket.Days61To90)]
    [InlineData(91, AgeingBucket.Over90)]
    [InlineData(400, AgeingBucket.Over90)]
    public void Each_band_starts_and_ends_where_it_says(int daysLate, AgeingBucket expected) =>
        Assert.Equal(expected, DebtAgeing.BucketFor(Today.AddDays(-daysLate), Today));

    [Fact]
    public void An_invoice_due_today_is_not_overdue_whatever_the_clock_says()
    {
        // Compared by date, not by instant. Counting the clock would push a third of every
        // day's invoices one band along depending on when somebody ran the report.
        var dueThisMorning = new DateTime(2026, 9, 23, 9, 0, 0, DateTimeKind.Utc);

        Assert.Equal(0, DebtAgeing.DaysOverdue(dueThisMorning, Today));
        Assert.Equal(AgeingBucket.Current, DebtAgeing.BucketFor(dueThisMorning, Today));
    }

    [Fact]
    public void A_pending_cheque_is_spoken_for_and_is_not_paid()
    {
        var standing = new InvoiceStanding(Total: 100m, Prepaid: 0m, Credited: 0m, Received: 0m, Claimed: 100m);

        // ⚠ §5.4. The whole invoice is written against a cheque, and the whole invoice is owed.
        Assert.Equal(100m, standing.Outstanding);
        Assert.Equal(0m, standing.Allocatable);
        Assert.Equal("Open", standing.Status);
        Assert.False(standing.IsSettled);
    }

    [Fact]
    public void What_was_settled_before_the_invoice_existed_is_not_owed_and_not_allocatable()
    {
        var standing = new InvoiceStanding(Total: 100m, Prepaid: 30m, Credited: 0m, Received: 20m, Claimed: 0m);

        Assert.Equal(50m, standing.Outstanding);
        Assert.Equal(50m, standing.Allocatable);
        Assert.Equal("PartlyPaid", standing.Status);
    }

    [Fact]
    public void An_invoice_credited_past_what_was_still_owed_becomes_money_the_company_owes()
    {
        // The customer paid cash for the lot and was then credited in full.
        var standing = new InvoiceStanding(Total: 100m, Prepaid: 100m, Credited: 100m, Received: 0m, Claimed: 0m);

        Assert.Equal(-100m, standing.Outstanding);
        Assert.Equal("Credited", standing.Status);
    }
}
