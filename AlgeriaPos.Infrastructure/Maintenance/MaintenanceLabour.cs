using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Hr;
using AlgeriaPos.Infrastructure.Manufacturing;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Infrastructure.Maintenance;

/// <summary>
/// Labour on maintenance jobs, Phase M3 (docs/maintenance/plan.md §2.5): hours go into the <c>LaborBookings</c> table
/// production already uses, with <see cref="LaborOrderKind.Maintenance"/>, <c>OrderId</c> = the job, and no line, record
/// row or stage. Hours only: no rate and no money (a job's cost is Phase M4).
/// <list type="bullet">
/// <item><b>One ceiling</b> (amendment 7): a person's hours on a day, across production records and jobs together, never
/// pass what their attendance shows (<see cref="AttendanceCapacity"/>, payroll's precedence) — unless the caller holds
/// <c>maintenance.labour.override</c> and says why, which is kept on the booking. The total is read through
/// <see cref="LabourCeiling"/> under the version lock, the lock production bookings take too.</item>
/// <item>Never in a payroll month a run holds closed for the person's branch (<c>payroll_month_locked</c>), to book or to
/// remove.</item>
/// <item>One booking per person, job and day, on an open job, for a day that has come.</item>
/// <item>Who may be booked (MS-04): any employee of the job's branch employed that day; under "only the workshop list",
/// only those of them on the job's branch's list (<see cref="MaintenanceWorkshop"/>), else 400
/// <c>employee_not_on_workshop_list</c>.</item>
/// <item>Each booking and removal is kept in <c>LaborBookingChanges</c>, naming the job; nothing in payroll or attendance
/// is written.</item>
/// </list>
/// Branch scope and the module switch are the API's: another branch's job answers 404, and every route answers 409
/// <c>maintenance_off</c> while MS-01 is off.
/// </summary>
public sealed class MaintenanceLabour(
    PosDbContext db, AttendanceCapacity attendance, AttendanceSheet sheet, IRegionalSettingsProvider regional, TimeProvider time)
{
    public const string AlreadyBookedCode = "already_booked";
    public const string PayrollLockedCode = "payroll_month_locked";
    public const string OverrideForbiddenCode = "override_forbidden";
    public const string NotOpenCode = "job_not_open";
    public const string NotOfBranchCode = "employee_not_of_branch";

    // ---------- reading ----------

    /// <summary>The job's hours, by day (newest first) and person.</summary>
    public async Task<MaintenanceResult<MaintenanceJobHoursDto>> HoursAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await db.MaintenanceJobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => new { j.Id, j.Status }).FirstOrDefaultAsync(ct);
        if (job is null) return NotFound<MaintenanceJobHoursDto>();
        var bookings = await db.LaborBookings.AsNoTracking().Include(b => b.Employee)
            .Where(b => b.OrderKind == LaborOrderKind.Maintenance && b.OrderId == jobId).ToListAsync(ct);
        var rows = bookings
            .OrderByDescending(b => b.Day).ThenBy(b => b.Employee?.FullName, StringComparer.Ordinal).ThenBy(b => b.AtUtc)
            .Select(b => new MaintenanceBookingDto(b.Id, b.EmployeeId, b.Employee?.Number ?? "", b.Employee?.FullName ?? "", b.Day,
                b.Hours, b.OvertimeHours, b.AttendanceHours, b.OverrideReason, b.OverriddenBy, b.UserName, b.AtUtc))
            .ToList();
        return MaintenanceResult<MaintenanceJobHoursDto>.Done(
            new MaintenanceJobHoursDto(job.Id, job.Status.ToString(), rows.Sum(b => b.Hours), rows, Production.Today(regional, time)));
    }

    /// <summary>
    /// The people who may be booked on the job on <paramref name="day"/> (the company's today when none): MS-04's
    /// employees of the job's branch, employed that day — only those on the branch's workshop list under "only the workshop
    /// list" — with the day's attendance and what is booked already.
    /// </summary>
    public async Task<MaintenanceResult<List<MaintenanceLabourCandidateDto>>> CandidatesAsync(Guid jobId, DateOnly? day, CancellationToken ct = default)
    {
        var job = await db.MaintenanceJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return NotFound<List<MaintenanceLabourCandidateDto>>();
        var on = day ?? Production.Today(regional, time);
        var settings = await MaintenanceSettingsStore.CurrentAsync(db, ct);
        var query = db.Employees.AsNoTracking()
            .Where(e => e.BranchId == job.BranchId && e.HiredOn <= on && (e.LeftOn == null || e.LeftOn >= on));
        if (settings.BookableEmployees == MaintenanceBookable.WorkshopListOnly)
        {
            var listed = MaintenanceWorkshop.OnListOf(db, job.BranchId);
            query = query.Where(e => listed.Contains(e.Id));
        }
        var employees = await query.OrderBy(e => e.FullName).ToListAsync(ct);
        var ids = employees.Select(e => e.Id).ToList();
        var days = await attendance.ForDayAsync(employees, on, ct);
        var booked = (await db.LaborBookings.AsNoTracking().Where(b => ids.Contains(b.EmployeeId) && b.Day == on)
                .Select(b => new { b.EmployeeId, b.Hours, b.OrderKind, b.OrderId }).ToListAsync(ct));
        var locked = (await sheet.LockOfAsync(job.BranchId, new AttendancePeriod(on.Year, on.Month), ct)).Locked;
        return MaintenanceResult<List<MaintenanceLabourCandidateDto>>.Done(employees.Select(e => new MaintenanceLabourCandidateDto(
            e.Id, e.Number, e.FullName, days[e.Id].Hours, days[e.Id].Source,
            booked.Where(b => b.EmployeeId == e.Id).Sum(b => b.Hours),
            booked.Any(b => b.EmployeeId == e.Id && b.OrderKind == LaborOrderKind.Maintenance && b.OrderId == jobId),
            locked)).ToList());
    }

    // ---------- writing ----------

    /// <summary>
    /// Books one employee's hours on an open job for a day. Refused: a job not open, a day still to come, hours outside
    /// (0, 24], overtime outside [0, hours], someone MS-04 does not allow or not employed that day, a closed payroll month,
    /// a second booking of the person on the job that day, and hours past the day's attendance across every booking —
    /// the last unless <paramref name="mayOverride"/> and a reason is given.
    /// </summary>
    public async Task<MaintenanceResult<MaintenanceJobHoursDto>> BookAsync(
        Guid jobId, BookMaintenanceHoursRequest r, bool mayOverride, MaintenanceActor actor, CancellationToken ct = default)
    {
        if (r.Hours <= 0 || r.Hours > 24)
            return Invalid<MaintenanceJobHoursDto>("invalid_hours", "Hours are above zero and at most 24.");
        if (r.OvertimeHours < 0 || r.OvertimeHours > r.Hours)
            return Invalid<MaintenanceJobHoursDto>("invalid_overtime_hours", "Overtime hours are between zero and the hours booked.");
        if (r.Day > Production.Today(regional, time))
            return Invalid<MaintenanceJobHoursDto>("future_day", "Hours are booked for a day that has come.");

        await using (var scope = await db.BeginWriteScopeAsync(ct))
        {
            // The version lock first: the job, the person's day and the payroll month are read under it.
            var job = await db.LockedAsync<MaintenanceJob>(jobId, ct);
            if (job is null) return NotFound<MaintenanceJobHoursDto>();
            if (job.Status != MaintenanceJobStatus.Open)
                return Conflict(NotOpenCode, job.Status == MaintenanceJobStatus.Draft
                    ? "Take the draft job before booking hours on it."
                    : "Hours are booked on an open job; this one is closed.");

            var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == r.EmployeeId, ct);
            if (employee is null) return NotFound<MaintenanceJobHoursDto>("Employee");
            if (employee.BranchId != job.BranchId)
                return Invalid<MaintenanceJobHoursDto>(NotOfBranchCode, "Only an employee of the job's branch is booked on it (MS-04).");
            if (employee.HiredOn > r.Day || (employee.LeftOn is DateOnly left && left < r.Day))
                return Invalid<MaintenanceJobHoursDto>("employee_not_employed", "This person was not employed on that day.");
            // MS-04 "only the workshop list": the person is on the list of the job's branch (and, above, still of that branch).
            var settings = await MaintenanceSettingsStore.CurrentAsync(db, ct);
            if (settings.BookableEmployees == MaintenanceBookable.WorkshopListOnly
                && !await MaintenanceWorkshop.OnListOf(db, job.BranchId).AnyAsync(id => id == employee.Id, ct))
                return Invalid<MaintenanceJobHoursDto>(MaintenanceWorkshop.NotOnListCode,
                    "Only someone on the branch's workshop list is booked on its jobs (MS-04): add them in Maintenance › Workshop.");
            if ((await sheet.LockOfAsync(employee.BranchId, new AttendancePeriod(r.Day.Year, r.Day.Month), ct)).Locked)
                return Conflict(PayrollLockedCode, "That month's payroll is closed for this person's branch; its hours no longer change.");
            if (await db.LaborBookings.AnyAsync(b => b.OrderKind == LaborOrderKind.Maintenance && b.OrderId == job.Id
                    && b.EmployeeId == employee.Id && b.Day == r.Day, ct))
                return Conflict(AlreadyBookedCode, "This person already has hours on this job that day: remove them and book the day's total.");

            var day = (await attendance.ForDayAsync([employee], r.Day, ct))[employee.Id];
            // Every booking that day, on every production record and job: one hour is never allocated twice.
            var booked = await LabourCeiling.BookedAsync(db, employee.Id, r.Day, ct);
            var capacity = day.Hours ?? 0m;
            var reason = Clean(r.OverrideReason, 400);
            var overridden = false;
            if (booked + r.Hours > capacity)
            {
                if (reason is null) return Conflict(LabourCeiling.ExceedsCode, LabourCeiling.Exceeds(day, booked));
                if (!mayOverride)
                    return MaintenanceResult<MaintenanceJobHoursDto>.Fail(403, OverrideForbiddenCode,
                        "Booking beyond attendance needs the maintenance labour override permission.");
                overridden = true;
            }

            var booking = new LaborBooking
            {
                EmployeeId = employee.Id, Day = r.Day, OrderKind = LaborOrderKind.Maintenance, OrderId = job.Id,
                StageId = null, LineId = null, LogRowId = null, Hours = r.Hours, OvertimeHours = r.OvertimeHours,
                PayrollBranchId = employee.BranchId, PayrollYear = r.Day.Year, PayrollMonth = r.Day.Month, AttendanceHours = capacity,
                OverrideReason = overridden ? reason : null, OverriddenByUserId = overridden ? actor.UserId : null,
                OverriddenBy = overridden ? actor.Name : null, UserId = actor.UserId, UserName = actor.Name,
            };
            db.LaborBookings.Add(booking);
            db.LaborBookingChanges.Add(Change(booking, LaborBookingAction.Created, actor));
            job.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
        }
        return await HoursAsync(jobId, ct);
    }

    /// <summary>Removes a booking of an open job, unless its payroll month is closed. The removal is kept.</summary>
    public async Task<MaintenanceResult<MaintenanceJobHoursDto>> UnbookAsync(
        Guid jobId, Guid bookingId, MaintenanceActor actor, CancellationToken ct = default)
    {
        await using (var scope = await db.BeginWriteScopeAsync(ct))
        {
            var job = await db.LockedAsync<MaintenanceJob>(jobId, ct);
            if (job is null) return NotFound<MaintenanceJobHoursDto>();
            if (job.Status != MaintenanceJobStatus.Open)
                return Conflict(NotOpenCode, "Hours change only on an open job.");
            var booking = await db.LaborBookings.FirstOrDefaultAsync(b => b.Id == bookingId
                && b.OrderKind == LaborOrderKind.Maintenance && b.OrderId == job.Id, ct);
            if (booking is null) return NotFound<MaintenanceJobHoursDto>("Booking");
            if ((await sheet.LockOfAsync(booking.PayrollBranchId, new AttendancePeriod(booking.PayrollYear, booking.PayrollMonth), ct)).Locked)
                return Conflict(PayrollLockedCode, "That month's payroll is closed; its hours no longer change.");
            db.LaborBookingChanges.Add(Change(booking, LaborBookingAction.Removed, actor));
            db.LaborBookings.Remove(booking);
            job.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
        }
        return await HoursAsync(jobId, ct);
    }

    // ---------- helpers ----------

    private static LaborBookingChange Change(LaborBooking b, LaborBookingAction action, MaintenanceActor actor) => new()
    {
        BookingId = b.Id, EmployeeId = b.EmployeeId, Day = b.Day, LogRowId = null, OrderKind = b.OrderKind, OrderId = b.OrderId,
        Action = action, Hours = b.Hours, OvertimeHours = b.OvertimeHours, OverrideReason = b.OverrideReason,
        UserId = actor.UserId, UserName = actor.Name,
    };

    private static string? Clean(string? text, int max)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length <= max ? t : t[..max];
    }

    private static MaintenanceResult<T> NotFound<T>(string what = "Maintenance job") =>
        MaintenanceResult<T>.Fail(404, "not_found", $"{what} not found.");

    private static MaintenanceResult<T> Invalid<T>(string code, string error) => MaintenanceResult<T>.Fail(400, code, error);

    private static MaintenanceResult<MaintenanceJobHoursDto> Conflict(string code, string error) =>
        MaintenanceResult<MaintenanceJobHoursDto>.Fail(409, code, error);
}
