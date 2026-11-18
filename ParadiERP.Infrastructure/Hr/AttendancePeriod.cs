namespace ParadiERP.Infrastructure.Hr;

/// <summary>
/// A payroll period: one calendar month, in the company's time zone, held as two ints.
///
/// <para>
/// Not a <c>DateTime</c>, and deliberately so (docs/07 §5.5). Africa/Algiers is an hour ahead of
/// UTC all year, so midnight on the first of a month is 23:00 on the last day of the previous one
/// in UTC: a period stored as an instant puts a day of work — and sometimes a whole run — in the
/// wrong month, and every uniqueness rule in this module hangs off the pair. The conversion
/// happens once, here, and only ever in the direction instant → local month.
/// </para>
/// </summary>
public readonly record struct AttendancePeriod(int Year, int Month)
{
    /// <summary>Wide enough for a back-dated correction and narrow enough to catch a typed 202.</summary>
    public static bool IsValid(int year, int month) => year is >= 2000 and <= 2100 && month is >= 1 and <= 12;

    public int DayCount => DateTime.DaysInMonth(Year, Month);

    public DateOnly FirstDay => new(Year, Month, 1);
    public DateOnly LastDay => new(Year, Month, DayCount);

    public bool Contains(DateOnly date) => date.Year == Year && date.Month == Month;

    /// <summary>The month an instant falls in, read in the company's zone.</summary>
    public static AttendancePeriod Of(DateTime utc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        return new AttendancePeriod(local.Year, local.Month);
    }

    /// <summary>The local working day an instant belongs to — the day a shift <i>started</i> in.</summary>
    public static DateOnly LocalDay(DateTime utc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));
    }

    /// <summary>
    /// The instant this local midnight was. Used to bound a clock-event query, never to store a
    /// period: a query may be a minute generous, a stored period may not be a day wrong.
    /// </summary>
    public static DateTime StartUtc(DateOnly localDay, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var midnight = DateTime.SpecifyKind(localDay.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);

        // A midnight that does not exist — the hour a zone skips at a DST start — would throw.
        // The window it bounds is a query, so the safe answer is the instant just before it.
        try { return TimeZoneInfo.ConvertTimeToUtc(midnight, zone); }
        catch (ArgumentException) { return TimeZoneInfo.ConvertTimeToUtc(midnight.AddHours(1), zone).AddHours(-1); }
    }

    /// <summary>The company's zone, or UTC when the machine does not know the IANA name.</summary>
    public static TimeZoneInfo Zone(string? ianaName)
    {
        if (string.IsNullOrWhiteSpace(ianaName)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(ianaName); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    public override string ToString() => $"{Year:D4}-{Month:D2}";
}
