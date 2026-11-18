using AlgeriaPos.Domain;
using Engine = AlgeriaPos.Application.Payroll;

namespace AlgeriaPos.Infrastructure.Hr;

/// <summary>One band's hours, as captured.</summary>
public sealed record AttendanceOvertimeEntry(string RuleKey, decimal Hours);

/// <summary>One recorded day, as captured. The shape <see cref="AttendanceSummarizer"/> reduces.</summary>
public sealed record AttendanceDayEntry(
    DateOnly WorkDate,
    AttendanceDayStatus Status,
    decimal? HoursWorked = null,
    IReadOnlyList<AttendanceOvertimeEntry>? Overtime = null);

/// <summary>A raw punch, with the identity of the row it came from so an unpaired one can be named.</summary>
public sealed record ClockPunch(Guid Id, DateTime AtUtc, ClockEventKind Kind);

/// <summary>An In matched to an Out. <paramref name="LocalDay"/> is the day the shift <i>started</i> in.</summary>
public sealed record ClockShift(DateOnly LocalDay, DateTime InUtc, DateTime OutUtc, decimal Hours);

/// <param name="Unpaired">
/// Punches with no partner. They are handed back rather than mended: a missing clock-out is a
/// question for the foreman, and a system that closed the shift at midnight — or at the next
/// punch — would be paying a figure nobody entered.
/// </param>
public sealed record ClockPairing(IReadOnlyList<ClockShift> Shifts, IReadOnlyList<ClockPunch> Unpaired);

/// <summary>
/// The one place both capture modes become the shape the payroll engine reads (docs/07 §5.5).
///
/// <para>
/// Pure: no entities, no database, no clock. If the engine read a declaration and a set of day
/// rows directly, every rule would be written twice and the daily basis — which cannot run
/// without days — would take its number from whichever path happened to be filled.
/// </para>
/// <para>
/// <b>What it deliberately does not decide.</b> A weekly rest day and a public holiday count as
/// neither worked nor absent: for a monthly employee that is a day that changes nothing, and for
/// a daily worker whether a public holiday is paid is a statutory question this module has no
/// answer for. Counting it as present would invent a payment; counting it as absence would invent
/// a deduction. It stays out, and the sheet still shows it.
/// </para>
/// <para>
/// It lives in Infrastructure rather than in <c>Application/Payroll</c> — where docs/07 §5.5
/// sketched it — because this stage owns these files; it takes no dependency on the database and
/// could move without a change to its callers.
/// </para>
/// </summary>
public static class AttendanceSummarizer
{
    /// <summary>A foreman's period totals.</summary>
    public static Engine.AttendanceSummary FromDeclaration(
        decimal daysPresent,
        decimal paidLeaveDays,
        decimal unpaidAbsenceDays,
        IEnumerable<AttendanceOvertimeEntry>? overtime = null) =>
        new(NonNegative(daysPresent),
            NonNegative(paidLeaveDays),
            NonNegative(unpaidAbsenceDays),
            ByRule(overtime),
            Engine.AttendanceSource.Declared);

    /// <summary>
    /// Day rows added up.
    /// </summary>
    /// <param name="hoursPerDay">
    /// The company's working day. A day row with hours on it is that fraction of a day, capped at
    /// one: the hours beyond a full day are overtime, which is captured per band, and paying them
    /// twice is the mistake this cap exists to stop.
    /// </param>
    public static Engine.AttendanceSummary FromDays(IEnumerable<AttendanceDayEntry> days, decimal hoursPerDay) =>
        FromDays(days, _ => hoursPerDay);

    /// <summary>
    /// Day rows added up, with a full day's hours that may differ by date: a day inside a special
    /// period such as Ramadan is full at that period's paid hours (owner, 2026-10-10;
    /// <c>WorkScheduleRules.FullDayHours</c>).
    /// </summary>
    public static Engine.AttendanceSummary FromDays(IEnumerable<AttendanceDayEntry> days, Func<DateOnly, decimal> fullDayHours)
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(fullDayHours);

        decimal present = 0, paidLeave = 0, unpaid = 0;
        var overtime = new List<AttendanceOvertimeEntry>();

        foreach (var day in days)
        {
            switch (day.Status)
            {
                case AttendanceDayStatus.Present:
                    present += DayFraction(day.HoursWorked, fullDayHours(day.WorkDate));
                    break;
                case AttendanceDayStatus.PaidLeave:
                    paidLeave += 1m;
                    break;
                case AttendanceDayStatus.Absent:
                case AttendanceDayStatus.UnpaidLeave:
                    unpaid += 1m;
                    break;
                // WeeklyRest and PublicHoliday: neither worked nor absent. See the class remarks.
                default:
                    break;
            }

            // Overtime is counted whatever the day's status was: hours worked on a rest day are
            // exactly the band a company defines a rest-day rule for.
            if (day.Overtime is not null) overtime.AddRange(day.Overtime);
        }

        return new Engine.AttendanceSummary(
            present, paidLeave, unpaid, ByRule(overtime), Engine.AttendanceSource.Recorded);
    }

    /// <summary>
    /// Paired clock shifts added up, for a Recorded employee whose month was punched rather than
    /// ticked. No overtime is derived: which band an hour belongs to is a rule about nights,
    /// rest days and holidays that the punches do not carry, and guessing one would pay a
    /// multiplier nobody applied.
    /// </summary>
    public static Engine.AttendanceSummary FromShifts(IEnumerable<ClockShift> shifts, decimal hoursPerDay) =>
        FromShifts(shifts, _ => hoursPerDay);

    /// <summary>Paired shifts added up, with a full day's hours by date, as <see cref="FromDays(IEnumerable{AttendanceDayEntry}, Func{DateOnly, decimal})"/>.</summary>
    public static Engine.AttendanceSummary FromShifts(IEnumerable<ClockShift> shifts, Func<DateOnly, decimal> fullDayHours)
    {
        ArgumentNullException.ThrowIfNull(shifts);
        ArgumentNullException.ThrowIfNull(fullDayHours);

        var present = shifts
            .GroupBy(s => s.LocalDay)
            .Sum(day => DayFraction(day.Sum(s => s.Hours), fullDayHours(day.Key)));

        return new Engine.AttendanceSummary(
            present, 0m, 0m, new Dictionary<string, decimal>(), Engine.AttendanceSource.Recorded);
    }

    /// <summary>
    /// Punches in, shifts out — and every punch that never found a partner, named.
    ///
    /// <para>
    /// The rule is the simplest one that cannot invent time: read in order, hold at most one open
    /// In, and close it on the next Out. A second In while one is open leaves the first unpaired;
    /// an Out with nothing open is unpaired itself. A shift belongs to the local day its In fell
    /// in, so a night shift ending at 02:00 counts against the evening it started.
    /// </para>
    /// </summary>
    public static ClockPairing Pair(IEnumerable<ClockPunch> punches, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(punches);
        ArgumentNullException.ThrowIfNull(zone);

        var shifts = new List<ClockShift>();
        var unpaired = new List<ClockPunch>();
        ClockPunch? open = null;

        foreach (var punch in punches.OrderBy(p => p.AtUtc).ThenBy(p => p.Kind))
        {
            if (punch.Kind == ClockEventKind.In)
            {
                if (open is not null) unpaired.Add(open);
                open = punch;
                continue;
            }

            if (open is null) { unpaired.Add(punch); continue; }

            var hours = (decimal)(punch.AtUtc - open.AtUtc).TotalHours;
            shifts.Add(new ClockShift(
                AttendancePeriod.LocalDay(open.AtUtc, zone), open.AtUtc, punch.AtUtc, NonNegative(hours)));
            open = null;
        }

        if (open is not null) unpaired.Add(open);

        return new ClockPairing(shifts, unpaired);
    }

    private static decimal DayFraction(decimal? hours, decimal hoursPerDay)
    {
        if (hours is not decimal worked) return 1m;
        if (hoursPerDay <= 0) return worked > 0 ? 1m : 0m;
        return Math.Clamp(worked / hoursPerDay, 0m, 1m);
    }

    /// <summary>Hours per band, summed, with anything unnamed or negative left out rather than guessed at.</summary>
    private static IReadOnlyDictionary<string, decimal> ByRule(IEnumerable<AttendanceOvertimeEntry>? entries)
    {
        var byRule = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (entries is null) return byRule;

        foreach (var entry in entries)
        {
            var key = (entry.RuleKey ?? "").Trim().ToLowerInvariant();
            if (key.Length == 0 || entry.Hours <= 0) continue;
            byRule[key] = byRule.TryGetValue(key, out var already) ? already + entry.Hours : entry.Hours;
        }
        return byRule;
    }

    private static decimal NonNegative(decimal value) => value < 0 ? 0 : value;
}

/// <summary>
/// Which capture mode a person is in. One function, called by the sheet, the day grid and the
/// run, so those three can never disagree about whether a month is declared or recorded.
///
/// <para>
/// There is no per-branch default: docs/07 §5.4 says one would be a nullable column on
/// <c>Branches</c> in a migration of its own, and Q14 is unanswered. Adding the branch here later
/// is one parameter and one line.
/// </para>
/// </summary>
public static class AttendanceModes
{
    /// <summary>The employee's override, or the company's default.</summary>
    public static AttendanceMode Resolve(AttendanceMode? employeeOverride, Engine.AttendanceMode companyDefault) =>
        // The two enums mirror each other name for name and value for value (HrEntities.cs), and
        // HrMirrorTests fails the moment they stop doing so.
        employeeOverride ?? (AttendanceMode)(int)companyDefault;
}
