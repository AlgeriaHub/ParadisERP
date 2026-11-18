namespace AlgeriaPos.Application.Rentals;

/// <summary>
/// Which days a delivery and a return use up, by the hour of the day they happen (answers 5 and
/// 6 of <c>docs/owner-answers.txt</c>, 2026-09-30).
///
/// <para>
/// The owner's rule, in his words: "the contract from 27 to 29 counts two days only, because the
/// day the goods come back is not counted (if they come back in the morning, and as the branch
/// manager decides)"; and "the contract starts according to the delivery hour". So the day is
/// split at one local hour — <see cref="RentalSettings.DayCutoffHour"/>, the company's choice:
/// </para>
/// <list type="bullet">
/// <item>goods <b>delivered</b> at or after that hour start their period the next day — the
/// afternoon of the delivery day is not a day the customer could use;</item>
/// <item>goods <b>returned</b> at or after that hour have their return day counted — the
/// customer used the morning — unless the branch manager decides otherwise on the note, in
/// writing; and a morning return is not counted unless he decides it is.</item>
/// </list>
/// <para>
/// With no hour set (null) nothing changes from the rule the engine has always had: the delivery
/// day counts, the return day never does, and <see cref="RentalPricing.DaysOut"/> is still the
/// return date minus the start date. Worked through with a cutoff of 12:00: goods out on the 26th
/// at 15:00 start on the 27th; back on the 29th at 09:00 is 27 and 28, two days; back on the
/// 29th at 16:00 is 27, 28 and 29, three — or two, if the manager waives the afternoon and says why.
/// </para>
/// <para>
/// Nothing here reads a clock or a database. The front office mirrors it in <c>core/rentals.ts</c>
/// so a return note previews the same figure the server will charge.
/// </para>
/// </summary>
public static class RentalDays
{
    /// <summary>The identity papers a signatory may present, as the owner's contract lists them.</summary>
    public static readonly IReadOnlyList<string> IdDocumentTypes = ["nationalCard", "drivingLicence", "passport"];

    /// <summary>The wall-clock time of a UTC instant in the company's zone.</summary>
    public static DateTime LocalTime(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);

    /// <summary>Whether a local time falls at or after the cutoff hour. Always false with no cutoff.</summary>
    public static bool IsAfterCutoff(DateTime localTime, int? cutoffHour) =>
        cutoffHour is int hour && localTime.Hour >= hour;

    /// <summary>
    /// The first day of a batch's period: the local delivery day, or the day after it when the
    /// goods left at or after the cutoff hour.
    /// </summary>
    public static DateOnly PeriodStartsOn(DateTime localIssuedAt, int? cutoffHour)
    {
        var day = DateOnly.FromDateTime(localIssuedAt);
        return IsAfterCutoff(localIssuedAt, cutoffHour) ? day.AddDays(1) : day;
    }

    /// <summary>
    /// Whether the return day is charged when nobody decides otherwise: yes for goods back at or
    /// after the cutoff hour, no before it — and never with no cutoff, which is today's rule.
    /// </summary>
    public static bool CountsReturnDayByDefault(DateTime localReturnedAt, int? cutoffHour) =>
        IsAfterCutoff(localReturnedAt, cutoffHour);

    /// <summary>
    /// The day charged up to, exclusive: the return day itself when it is not counted, the day
    /// after when it is. <see cref="RentalPricing.DaysOut"/> subtracts the start day from it.
    /// </summary>
    public static DateOnly ChargedUntil(DateOnly returnedOn, bool countReturnDay) =>
        countReturnDay ? returnedOn.AddDays(1) : returnedOn;

    /// <summary>
    /// Why the manager's decision on the return day cannot be saved, or null. A decision the same
    /// as the rule needs nothing; one that departs from it is written down, because it moves money
    /// the paper would otherwise not explain.
    /// </summary>
    public static string? ReturnDayProblem(bool? decision, bool byDefault, string? reason) =>
        decision is bool decided && decided != byDefault && string.IsNullOrWhiteSpace(reason)
            ? "returnDayNeedsReason"
            : null;

    /// <summary>A cutoff is an hour of the day, or none at all.</summary>
    public static string? CutoffProblem(int? cutoffHour) =>
        cutoffHour is int hour && (hour < 0 || hour > 23) ? "cutoffOutOfRange" : null;
}
