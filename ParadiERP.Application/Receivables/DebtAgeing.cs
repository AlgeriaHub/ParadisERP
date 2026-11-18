namespace ParadiERP.Application.Receivables;

/// <summary>
/// How long a debt has been overdue, in the bands the business chases in.
///
/// <para>
/// <see cref="Current"/> is not "nothing owed" — it is money owed that is not yet late. Merging
/// it into the first overdue band, which is the easy mistake, would put every invoice raised
/// this morning on the list of customers to telephone.
/// </para>
/// </summary>
public enum AgeingBucket
{
    Current = 0,
    Days1To30 = 1,
    Days31To60 = 2,
    Days61To90 = 3,
    Over90 = 4
}

/// <summary>
/// Which band a debt falls in, and nothing else. Pure so the rule can be checked without a
/// database — the boundaries are the whole report, and an off-by-one at 30 days moves real
/// money between two columns the owner reads differently.
/// </summary>
public static class DebtAgeing
{
    /// <summary>
    /// Days past due, counted in whole days and never negative.
    ///
    /// <para>
    /// Compared by date and not by instant: an invoice due today at 09:00, looked at today at
    /// 17:00, is not eight hours overdue — it is due today. Counting the clock would push a
    /// third of every day's invoices one band along depending on when the report was run.
    /// </para>
    /// </summary>
    public static int DaysOverdue(DateTime dueDateUtc, DateTime asOfUtc) =>
        Math.Max(0, (int)(asOfUtc.Date - dueDateUtc.Date).TotalDays);

    public static AgeingBucket BucketFor(DateTime dueDateUtc, DateTime asOfUtc) =>
        DaysOverdue(dueDateUtc, asOfUtc) switch
        {
            0 => AgeingBucket.Current,
            <= 30 => AgeingBucket.Days1To30,
            <= 60 => AgeingBucket.Days31To60,
            <= 90 => AgeingBucket.Days61To90,
            _ => AgeingBucket.Over90
        };

    /// <summary>The bands in the order a report prints them, so no caller invents its own.</summary>
    public static IReadOnlyList<AgeingBucket> Buckets { get; } =
    [
        AgeingBucket.Current, AgeingBucket.Days1To30, AgeingBucket.Days31To60,
        AgeingBucket.Days61To90, AgeingBucket.Over90
    ];
}
