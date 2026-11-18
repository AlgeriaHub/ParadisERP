using AlgeriaPos.Application.Regional;

namespace AlgeriaPos.Application.Rentals;

/// <summary>
/// The day-and-money arithmetic of a rental, one function per rule the owner stated. Nothing
/// here reads a database or a clock; everything is passed in, so the same figures come out on
/// the server, in the back office and in a test.
/// </summary>
public static class RentalPricing
{
    /// <summary>The supplied price is for one unit for one day, without intermediate rounding.</summary>
    public static decimal DailyRate(decimal dailyUnitRate) => dailyUnitRate;

    /// <summary>
    /// Days charged for a batch: return date − issue note date, minimum one.
    ///
    /// <para>
    /// The period starts at the issue note, not at the contract: goods sitting in the warehouse
    /// because the site was not ready are not hired. And goods taken out and brought back the
    /// same afternoon are a day's hire, not a free one — the day they went out is used up
    /// either way.
    /// </para>
    /// </summary>
    public static int DaysOut(DateOnly issuedOn, DateOnly returnedOn) =>
        Math.Max(1, returnedOn.DayNumber - issuedOn.DayNumber);

    /// <summary>
    /// The last day of a period: the issue date plus the days agreed.
    ///
    /// <para>
    /// A batch issued on the 1st for 30 days and returned on the 31st has been out exactly its
    /// 30 days and owes no overdue. Returned on the 1st of the next month, it is one day over.
    /// </para>
    /// </summary>
    public static DateOnly PeriodEndsOn(DateOnly issuedOn, int entitledDays) =>
        issuedOn.AddDays(Math.Max(0, entitledDays));

    /// <summary>
    /// The overdue price, frozen onto the line at signing: the rental price times the admin's
    /// multiplier (or the item's own override). Rounded, because it is a price printed on a
    /// document the customer signs.
    /// </summary>
    public static decimal OverdueUnitPrice(RegionalSettings regional, decimal rentUnitPrice, decimal multiplier) =>
        regional.RoundMoney(rentUnitPrice * Math.Max(0, multiplier));

    /// <summary>Quantity × rental days × daily unit rate, rounded once to currency precision.</summary>
    public static decimal Amount(RegionalSettings regional, decimal dailyUnitRate, int days, decimal qty) =>
        regional.RoundMoney(qty * days * dailyUnitRate);

    /// <summary>
    /// Compensation for units that will not come back, at the total-damage price frozen on the
    /// line. Used for both damage and loss: the company has lost the same asset either way.
    /// </summary>
    public static decimal DamageAmount(RegionalSettings regional, decimal damageUnitPrice, decimal qty) =>
        regional.RoundMoney(damageUnitPrice * qty);

    /// <summary>
    /// The date a note falls on, in the company's own time zone.
    ///
    /// <para>
    /// Notes are stored in UTC and the whole calculation counts <b>days</b>. A return note taken
    /// at 00:30 in Algiers is stored on the previous UTC day, and counting from the UTC date
    /// would charge the customer a day they did not have — every time, on the early shift.
    /// </para>
    /// </summary>
    public static DateOnly LocalDay(DateTime utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
            utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));
}

/// <summary>
/// A stretch of days at one price: the agreed period, then whatever extensions were signed,
/// then the open-ended overdue tail.
/// </summary>
/// <param name="ExtensionId">Set when these days were bought by an extension, so the money can be traced to the document that created it.</param>
public sealed record RentalSpan(int Days, decimal UnitPrice, RentalChargeKind Kind, Guid? ExtensionId = null);

/// <summary>
/// A rental period is a chain of priced stretches of days, and the days a batch was actually
/// out are poured into that chain in order: the agreed period first, then each extension in the
/// order it was signed, and only then overdue.
///
/// <para>
/// This is what makes "an extension is a decision, overdue is a consequence" arithmetic rather
/// than a slogan. The same ten days cost the rental rate when they were agreed in advance and
/// the multiplied rate when they were not.
/// </para>
/// </summary>
public static class RentalPeriod
{
    /// <summary>
    /// The days a batch is entitled to, in the order they are consumed: the line's own duration
    /// at the rental price, then each applicable extension at its own signed price.
    /// </summary>
    public static IReadOnlyList<RentalSpan> Entitlement(
        decimal rentUnitPrice, int durationDays, IEnumerable<RentalExtension> extensions)
    {
        var spans = new List<RentalSpan> { new(Math.Max(0, durationDays), rentUnitPrice, RentalChargeKind.Rent) };
        spans.AddRange(extensions
            .OrderBy(e => e.RaisedOn)
            .Where(e => e.ExtraDays > 0)
            .Select(e => new RentalSpan(e.ExtraDays, e.UnitPrice, RentalChargeKind.Rent, e.Id)));
        return spans;
    }

    /// <summary>Every day the batch is entitled to before overdue begins.</summary>
    public static int EntitledDays(IEnumerable<RentalSpan> entitlement) => entitlement.Sum(s => s.Days);

    /// <summary>
    /// Pours <paramref name="daysOut"/> into the entitlement, and whatever will not fit becomes
    /// overdue at the frozen overdue price. Stretches that take no days are dropped, so a batch
    /// back inside its period produces one rent span and nothing else.
    /// </summary>
    public static IReadOnlyList<RentalSpan> Apportion(
        int daysOut, IReadOnlyList<RentalSpan> entitlement, decimal overdueUnitPrice)
    {
        var used = new List<RentalSpan>();
        var left = Math.Max(0, daysOut);
        foreach (var span in entitlement)
        {
            if (left <= 0) break;
            var take = Math.Min(left, span.Days);
            if (take > 0) used.Add(span with { Days = take });
            left -= take;
        }
        if (left > 0) used.Add(new RentalSpan(left, overdueUnitPrice, RentalChargeKind.Overdue));
        return used;
    }

    /// <summary>
    /// Days of the agreed period the customer never used. The contract is invoiced for the full
    /// period when the goods go out, so this is what an early return gives back.
    /// </summary>
    public static int UnusedDays(int daysOut, int durationDays) => Math.Max(0, durationDays - Math.Max(0, daysOut));

    /// <summary>
    /// The days a batch is charged for when it comes back: the days it was out, but never fewer
    /// than the company's minimum hire — or the agreed period, when that is shorter than the
    /// minimum (owner, docs/owner-notes.md, early return decision B). Only the days beyond this
    /// are given back as unused rent. A minimum of 0 is the rule the engine has always had.
    /// </summary>
    public static int ChargedDays(int daysOut, int durationDays, int minimumDays) =>
        Math.Max(Math.Max(0, daysOut), Math.Min(Math.Max(0, minimumDays), Math.Max(0, durationDays)));
}
