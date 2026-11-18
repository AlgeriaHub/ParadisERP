using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Infrastructure.Payroll;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Engine = AlgeriaPos.Application.Payroll;

namespace AlgeriaPos.Infrastructure.Hr;

/// <summary>
/// A month of punches read against the working day (owner, 2026-10-10): who came late, who left
/// early, whose entry has no exit, and who stayed after the end.
///
/// <para>
/// <b>It reads and it never writes.</b> Each figure is for the supervisor: lateness deducts nothing
/// by itself, and time after the end is <i>possible</i> overtime — overtime is still what the
/// supervisor records (answer 58). The punches are paired exactly as the run pairs them
/// (<see cref="AttendanceSummarizer.Pair"/>), so this page and the payslip can never disagree about
/// which entry an exit closed.
/// </para>
/// </summary>
public sealed class AttendanceReview(
    PosDbContext db, PayrollSettingsStore settings, IRegionalSettingsProvider regional)
{
    public async Task<HrResult<AttendanceReviewDto>> ReadAsync(
        Guid? employeeId, Guid? branchId, int year, int month, bool flaggedOnly, CancellationToken ct = default)
    {
        if (!AttendancePeriod.IsValid(year, month))
            return HrResult<AttendanceReviewDto>.Invalid(HrProblems.PeriodInvalid, "That is not a month.");
        if (employeeId is null && branchId is null)
            return HrResult<AttendanceReviewDto>.Invalid(HrProblems.EmployeeRequired, "Name an employee or a branch.");

        var period = new AttendancePeriod(year, month);
        var zone = AttendancePeriod.Zone(regional.Get().TimeZone);
        var schedule = (await settings.ReadAsync(ct)).Settings.Schedule ?? new Engine.WorkSchedule();

        // A day either side, as the clock log reads it: a shift that crosses the month boundary
        // pairs, and only then is the month cut out.
        var from = AttendancePeriod.StartUtc(period.FirstDay.AddDays(-1), zone);
        var to = AttendancePeriod.StartUtc(period.LastDay.AddDays(2), zone);

        var query = db.ClockEvents.AsNoTracking().Where(e => e.AtUtc >= from && e.AtUtc < to);
        if (employeeId is Guid one) query = query.Where(e => e.EmployeeId == one);
        else if (branchId is Guid branch)
            query = query.Where(e => db.Employees.Any(x => x.Id == e.EmployeeId && x.BranchId == branch));

        var punches = await query.OrderBy(e => e.AtUtc).ToListAsync(ct);
        var ids = punches.Select(p => p.EmployeeId).Distinct().ToList();
        var staff = ids.Count == 0
            ? []
            : await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);

        var days = new List<AttendanceReviewDayDto>();
        foreach (var person in punches.GroupBy(p => p.EmployeeId))
        {
            var pairing = AttendanceSummarizer.Pair(
                person.Select(e => new ClockPunch(e.Id, Utc(e.AtUtc), e.Kind)), zone);

            var shiftsByDay = pairing.Shifts.GroupBy(s => s.LocalDay).ToDictionary(g => g.Key, g => g.ToList());
            var openByDay = pairing.Unpaired
                .Where(p => p.Kind == Domain.ClockEventKind.In)
                .GroupBy(p => AttendancePeriod.LocalDay(p.AtUtc, zone))
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var date in shiftsByDay.Keys.Union(openByDay.Keys).Where(period.Contains).OrderBy(d => d))
            {
                var shifts = shiftsByDay.GetValueOrDefault(date) ?? [];
                var open = openByDay.GetValueOrDefault(date) ?? [];

                var entries = shifts.Select(s => s.InUtc).Concat(open.Select(p => p.AtUtc)).ToList();
                TimeOnly? firstIn = entries.Count == 0 ? null : Local(entries.Min(), zone);
                // An exit after midnight still closes the day it started in; read against this day's
                // end it is the last minute of it, not an early leave.
                TimeOnly? lastOut = shifts.Count == 0
                    ? null
                    : AttendancePeriod.LocalDay(shifts.Max(s => s.OutUtc), zone) > date
                        ? new TimeOnly(23, 59)
                        : Local(shifts.Max(s => s.OutUtc), zone);
                var worked = (int)Math.Round(shifts.Sum(s => s.Hours) * 60m);

                var day = Engine.WorkScheduleRules.ForDay(schedule, date);
                var review = Engine.WorkScheduleRules.Review(
                    day, schedule.LateGraceMinutes, firstIn, lastOut, worked, open.Count > 0);
                if (flaggedOnly && !review.HasFlag) continue;

                days.Add(new AttendanceReviewDayDto(
                    person.Key, date, day.IsRestDay, day.PeriodName,
                    Hm(day.Start), Hm(day.End), day.PaidHours,
                    firstIn is TimeOnly i ? Hm(i) : null,
                    lastOut is TimeOnly o ? Hm(o) : null,
                    Math.Round(worked / 60m, 2),
                    review.LateMinutes, review.EarlyLeaveMinutes, review.MissingCheckOut,
                    review.PossibleOvertimeMinutes, review.WorkedOnRestDay));
            }
        }

        var people = days.Select(d => d.EmployeeId).Distinct()
            .Where(staff.ContainsKey)
            .Select(id => new ClockPersonDto(id, staff[id].Number, staff[id].FullName))
            .OrderBy(p => p.EmployeeNumber, StringComparer.Ordinal)
            .ToList();
        var order = people.Select((p, i) => (p.EmployeeId, i)).ToDictionary(x => x.EmployeeId, x => x.i);

        return HrResult<AttendanceReviewDto>.Success(new AttendanceReviewDto(
            year, month,
            days.OrderBy(d => d.WorkDate).ThenBy(d => order.GetValueOrDefault(d.EmployeeId, int.MaxValue)).ToList(),
            people));
    }

    private static TimeOnly Local(DateTime utc, TimeZoneInfo zone) =>
        TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Utc(utc), zone));

    private static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string Hm(TimeOnly t) => t.ToString("HH:mm");
}
