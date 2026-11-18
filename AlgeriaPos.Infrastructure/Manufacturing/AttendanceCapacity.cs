using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Catalog;
using AlgeriaPos.Infrastructure.Hr;
using AlgeriaPos.Infrastructure.Payroll;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Engine = AlgeriaPos.Application.Payroll;

namespace AlgeriaPos.Infrastructure.Manufacturing;

/// <summary>
/// The hours an employee's attendance shows for one day — the ceiling for the hours booked to orders that
/// day (owner, 2026-10-26, decision 2). Read only: no attendance or payroll row is written here.
///
/// <para>
/// The sources are payroll's own, with payroll's precedence (AttendanceSheet): a day row someone ticked
/// wins over clock punches, and punches are paired the way payroll pairs them, a shift belonging to the
/// day it started. Payroll reads a month from day rows only once the employee has one anywhere in that
/// calendar month, so on such a month a day without a row shows no hours, whatever the clock holds
/// (costing plan F6, step A0): an hour is never booked on a day payroll did not pay. A present day without hours is the company's full day for that date; overtime recorded
/// that day — on the day row or as an overtime entry — adds its hours, a band counted in days adding a full
/// day. An employee whose attendance is declared per month has no day to read: <see cref="Hours"/> is null
/// and nothing can be booked for them without the controlled override.
/// </para>
/// </summary>
public sealed record DayAttendance(decimal? Hours, string Source)
{
    public const string Recorded = "recorded";
    public const string Clock = "clock";
    public const string Declared = "declared";
    public const string NoRecord = "none";
}

public sealed class AttendanceCapacity(PosDbContext db, PayrollSettingsStore settings, IRegionalSettingsProvider regional)
{
    public async Task<Dictionary<Guid, DayAttendance>> ForDayAsync(IReadOnlyCollection<Employee> employees, DateOnly day, CancellationToken ct)
    {
        var profile = (await settings.ReadAsync(ct)).Settings;
        var zone = StoreDocumentHistory.Zone(regional);
        var fullDay = Engine.WorkScheduleRules.FullDayHours(profile, day);
        var unitOf = profile.OvertimeRules.ToDictionary(r => r.Key, r => r.Unit, StringComparer.OrdinalIgnoreCase);
        decimal Overtime(string key, decimal amount) =>
            unitOf.GetValueOrDefault(key, Engine.OvertimeUnit.Hours) == Engine.OvertimeUnit.Days ? amount * fullDay : amount;

        var ids = employees.Select(e => e.Id).ToList();
        var rows = await db.AttendanceDays.AsNoTracking().Include(d => d.Overtime)
            .Where(d => ids.Contains(d.EmployeeId) && d.WorkDate == day).ToListAsync(ct);
        // Who has a day row anywhere in the month: payroll reads their whole month from day rows (AttendanceSheet).
        var month = new AttendancePeriod(day.Year, day.Month);
        var (first, last) = (month.FirstDay, month.LastDay);
        var onDayRows = (await db.AttendanceDays.AsNoTracking()
            .Where(d => ids.Contains(d.EmployeeId) && d.WorkDate >= first && d.WorkDate <= last)
            .Select(d => d.EmployeeId).Distinct().ToListAsync(ct)).ToHashSet();
        var entries = await db.OvertimeEntries.AsNoTracking()
            .Where(o => ids.Contains(o.EmployeeId) && o.WorkDate == day).ToListAsync(ct);
        // The day either side, so a shift crossing midnight pairs as payroll pairs it.
        var from = TimeZoneInfo.ConvertTimeToUtc(day.AddDays(-1).ToDateTime(TimeOnly.MinValue), zone);
        var to = TimeZoneInfo.ConvertTimeToUtc(day.AddDays(2).ToDateTime(TimeOnly.MinValue), zone);
        var punches = await db.ClockEvents.AsNoTracking()
            .Where(e => ids.Contains(e.EmployeeId) && e.AtUtc >= from && e.AtUtc < to).ToListAsync(ct);

        var answer = new Dictionary<Guid, DayAttendance>();
        foreach (var employee in employees)
        {
            if (AttendanceModes.Resolve(employee.AttendanceModeOverride, profile.DefaultAttendanceMode) == AttendanceMode.Declared)
            {
                answer[employee.Id] = new DayAttendance(null, DayAttendance.Declared);
                continue;
            }
            var extra = entries.Where(o => o.EmployeeId == employee.Id).Sum(o => Overtime(o.RuleKey, o.Hours));
            var row = rows.FirstOrDefault(d => d.EmployeeId == employee.Id);
            if (row is not null)
            {
                var worked = row.Status == AttendanceDayStatus.Present ? row.HoursWorked ?? fullDay : 0m;
                answer[employee.Id] = new DayAttendance(worked + row.Overtime.Sum(o => Overtime(o.RuleKey, o.Hours)) + extra, DayAttendance.Recorded);
                continue;
            }
            if (onDayRows.Contains(employee.Id))
            {
                // A month read from day rows, and no row this day: payroll paid no hours for it, punches or not.
                answer[employee.Id] = new DayAttendance(extra, DayAttendance.NoRecord);
                continue;
            }
            var shifts = AttendanceSummarizer.Pair(
                    punches.Where(p => p.EmployeeId == employee.Id)
                        .Select(p => new ClockPunch(p.Id, DateTime.SpecifyKind(p.AtUtc, DateTimeKind.Utc), p.Kind)), zone)
                .Shifts.Where(s => s.LocalDay == day).ToList();
            answer[employee.Id] = shifts.Count > 0
                ? new DayAttendance(shifts.Sum(s => s.Hours) + extra, DayAttendance.Clock)
                : new DayAttendance(extra, DayAttendance.NoRecord);
        }
        return answer;
    }
}
