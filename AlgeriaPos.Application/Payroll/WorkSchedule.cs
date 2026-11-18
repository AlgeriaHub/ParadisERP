using System.Globalization;

namespace AlgeriaPos.Application.Payroll;

/// <summary>
/// The company's working day and the dated periods that replace it (owner, 2026-10-10: "the
/// working day is 08:00 to 17:00, an admin setting, and keep Ramadan in mind").
///
/// <para>
/// Times are held as <c>HH:mm</c> text rather than <see cref="TimeOnly"/>: they travel inside the
/// payroll settings JSON and come from an <c>&lt;input type="time"&gt;</c>, and a format the
/// browser sends and the screen shows as is leaves nothing for a serialiser to reinterpret.
/// <see cref="WorkScheduleRules"/> parses them once, after <see cref="WorkScheduleRules.NormalizeAndValidate"/>
/// has refused anything it cannot read.
/// </para>
/// <para>
/// The defaults are the owner's own figures, and they are data: the admin edits them on the payroll
/// settings screen. Friday as the rest day is answer 60. A day never crosses midnight here — the
/// company works days; a night shift would be its own question.
/// </para>
/// </summary>
public class WorkSchedule
{
    public string Start { get; set; } = "08:00";
    public string End { get; set; } = "17:00";

    /// <summary>Unpaid break inside the day, in minutes. 08:00–17:00 less an hour is the 8-hour day of answer 58.</summary>
    public int BreakMinutes { get; set; } = 60;

    /// <summary>
    /// Minutes after the start before an arrival reads as late. 0 is the schedule exactly as stated;
    /// a company that tolerates a few minutes says so here rather than in the code.
    /// </summary>
    public int LateGraceMinutes { get; set; }

    /// <summary>The weekly rest days (<see cref="DayOfWeek"/> numbers, Sunday = 0). Friday by default (answer 60).</summary>
    public List<DayOfWeek> RestDays { get; set; } = [DayOfWeek.Friday];

    /// <summary>Dated periods with their own hours — Ramadan, entered by the admin each year because it moves.</summary>
    public List<WorkSchedulePeriod> Periods { get; set; } = [];
}

/// <summary>A dated stretch of days worked on other hours than the normal schedule.</summary>
public class WorkSchedulePeriod
{
    /// <summary>What the admin calls it ("Ramadan 2027"). Free text: the period is the admin's, not a rule in code.</summary>
    public string Name { get; set; } = "";

    /// <summary>First day, inclusive.</summary>
    public DateOnly From { get; set; }

    /// <summary>Last day, inclusive.</summary>
    public DateOnly To { get; set; }

    public string Start { get; set; } = "08:00";
    public string End { get; set; } = "14:00";
    public int BreakMinutes { get; set; }
}

/// <summary>The hours that apply to one date, resolved from <see cref="WorkSchedule"/>.</summary>
/// <param name="PeriodName">The special period the date falls in; null on a normal day.</param>
public sealed record DaySchedule(
    DateOnly Date, bool IsRestDay, TimeOnly Start, TimeOnly End, int BreakMinutes, string? PeriodName)
{
    /// <summary>The paid hours of the day: the span less the break.</summary>
    public decimal PaidHours => Math.Max(0m, (decimal)(End - Start).TotalHours - BreakMinutes / 60m);
}

/// <summary>
/// What one person's day looked like against its schedule. Every figure is a fact to show the
/// supervisor, never a decision: lateness deducts nothing by itself, and time after the end is
/// <i>possible</i> overtime that a supervisor confirms (answer 58) — nothing here creates any.
/// </summary>
/// <param name="LateMinutes">Minutes after the start (and its grace) that the first punch came; 0 on time.</param>
/// <param name="EarlyLeaveMinutes">Minutes before the end that the last exit came; 0 when it did not.</param>
/// <param name="MissingCheckOut">An entry with no exit after it — the foreman's question (docs/07 §5.5).</param>
/// <param name="PossibleOvertimeMinutes">
/// Minutes after the end of the day; on a rest day, every minute worked. For the supervisor to
/// confirm or not.
/// </param>
public sealed record DayReview(
    int LateMinutes, int EarlyLeaveMinutes, bool MissingCheckOut, int PossibleOvertimeMinutes, bool WorkedOnRestDay)
{
    public bool HasFlag => LateMinutes > 0 || EarlyLeaveMinutes > 0 || MissingCheckOut || PossibleOvertimeMinutes > 0 || WorkedOnRestDay;
}

public static class WorkScheduleRules
{
    private const string TimeFormat = "HH:mm";

    /// <summary>
    /// Tidies the schedule and refuses the shapes that would misread a whole month: a time that is
    /// not a time, an end before its start, a break as long as the day, two periods claiming the
    /// same date.
    /// </summary>
    public static void NormalizeAndValidate(WorkSchedule s)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.Start = Time(s.Start, "The working day's start");
        s.End = Time(s.End, "The working day's end");
        Span(s.Start, s.End, s.BreakMinutes, "The working day");
        if (s.LateGraceMinutes is < 0 or > 240) Fail("The grace before an arrival is late must be between 0 and 240 minutes.");

        s.RestDays = (s.RestDays ?? []).Distinct().OrderBy(d => d).ToList();
        if (s.RestDays.Any(d => !Enum.IsDefined(d))) Fail("A rest day is a day of the week.");
        if (s.RestDays.Count >= 7) Fail("At least one day of the week must be a working day.");

        s.Periods ??= [];
        foreach (var p in s.Periods)
        {
            p.Name = (p.Name ?? "").Trim();
            if (p.Name.Length == 0) Fail("Name each special period (for example, Ramadan).");
            if (p.Name.Length > 60) Fail("A special period's name is 60 characters at most.");
            if (p.To < p.From) Fail($"The period \"{p.Name}\" ends before it starts.");
            if (p.To.DayNumber - p.From.DayNumber > 366) Fail($"The period \"{p.Name}\" is longer than a year.");
            p.Start = Time(p.Start, $"The start of \"{p.Name}\"");
            p.End = Time(p.End, $"The end of \"{p.Name}\"");
            Span(p.Start, p.End, p.BreakMinutes, $"The period \"{p.Name}\"");
        }

        s.Periods = s.Periods.OrderBy(p => p.From).ToList();
        for (var i = 1; i < s.Periods.Count; i++)
            if (s.Periods[i].From <= s.Periods[i - 1].To)
                Fail($"The periods \"{s.Periods[i - 1].Name}\" and \"{s.Periods[i].Name}\" overlap; a day has one schedule.");
    }

    /// <summary>The hours that apply on <paramref name="date"/>: a special period's when one covers it, else the normal day.</summary>
    public static DaySchedule ForDay(WorkSchedule? schedule, DateOnly date)
    {
        schedule ??= new WorkSchedule();
        var rest = (schedule.RestDays ?? []).Contains(date.DayOfWeek);
        var period = (schedule.Periods ?? []).FirstOrDefault(p => p.From <= date && date <= p.To);
        return period is null
            ? new DaySchedule(date, rest, Parse(schedule.Start), Parse(schedule.End), schedule.BreakMinutes, null)
            : new DaySchedule(date, rest, Parse(period.Start), Parse(period.End), period.BreakMinutes, period.Name);
    }

    /// <summary>
    /// The hours that make a full day on <paramref name="date"/>, for turning hours worked into a
    /// fraction of a day: a special period's paid hours inside one (a six-hour Ramadan day worked
    /// in full is a full day), and the company's <see cref="PayrollSettings.HoursPerDay"/> on every
    /// other day — the figure every run before the schedule existed computed with, so a normal day
    /// reads exactly as it did.
    /// </summary>
    public static decimal FullDayHours(PayrollSettings settings, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var day = ForDay(settings.Schedule, date);
        return day.PeriodName is not null && day.PaidHours > 0 ? day.PaidHours : settings.HoursPerDay;
    }

    /// <summary>
    /// One day's punches read against its schedule.
    /// </summary>
    /// <param name="firstIn">The first entry of the day, local time; null when the day has none.</param>
    /// <param name="lastOut">The last exit of the day, local time; null when no exit closed it.</param>
    /// <param name="workedMinutes">Paired time worked that day, for a rest day's possible overtime.</param>
    /// <param name="openEntry">An entry with no exit after it.</param>
    public static DayReview Review(
        DaySchedule day, int lateGraceMinutes, TimeOnly? firstIn, TimeOnly? lastOut, int workedMinutes, bool openEntry)
    {
        ArgumentNullException.ThrowIfNull(day);
        var missingOut = openEntry || (firstIn is not null && lastOut is null);

        if (day.IsRestDay)
        {
            var worked = firstIn is not null || lastOut is not null || workedMinutes > 0;
            return new DayReview(0, 0, missingOut, Math.Max(0, workedMinutes), worked);
        }

        // Compared before subtracting: TimeOnly subtraction wraps round midnight, so an arrival
        // before the start would otherwise read as twenty-three hours late.
        var late = firstIn is TimeOnly arrived && arrived > day.Start
            ? Math.Max(0, (int)(arrived - day.Start).TotalMinutes - Math.Max(0, lateGraceMinutes))
            : 0;

        int early = 0, overtime = 0;
        if (lastOut is TimeOnly left)
        {
            if (left < day.End) early = (int)(day.End - left).TotalMinutes;
            else if (left > day.End) overtime = (int)(left - day.End).TotalMinutes;
        }

        return new DayReview(late, early, missingOut, overtime, false);
    }

    private static TimeOnly Parse(string? value) =>
        TimeOnly.TryParseExact(value, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t
            : TimeOnly.TryParse(value, CultureInfo.InvariantCulture, out t) ? t : new TimeOnly(0, 0);

    /// <summary>Reads <c>H:mm</c>, <c>HH:mm</c> or <c>HH:mm:ss</c> and writes it back as <c>HH:mm</c>.</summary>
    private static string Time(string? value, string what)
    {
        var text = (value ?? "").Trim();
        if (!TimeOnly.TryParse(text, CultureInfo.InvariantCulture, out var t))
            Fail($"{what} is not a time (HH:mm).");
        return t.ToString(TimeFormat, CultureInfo.InvariantCulture);
    }

    private static void Span(string start, string end, int breakMinutes, string what)
    {
        var s = Parse(start);
        var e = Parse(end);
        if (e <= s) Fail($"{what} must end after it starts, on the same day.");
        if (breakMinutes < 0) Fail($"{what}: the break cannot be negative.");
        if (breakMinutes >= (e - s).TotalMinutes) Fail($"{what}: the break must be shorter than the day.");
    }

    private static void Fail(string message) => throw new ArgumentException(message);
}
