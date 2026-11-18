namespace ParadiERP.Application.Contracts;

// ---------- Phase M3: labour on jobs (docs/maintenance/plan.md §2.5) ----------

/// <summary>
/// Hours one employee worked on a job on one day: a <c>LaborBooking</c> of kind Maintenance. The attendance the day showed
/// when booked, and the override (who, why) that allowed more. Hours only — no rate and no money (cost is Phase M4).
/// </summary>
public record MaintenanceBookingDto(
    Guid Id, Guid EmployeeId, string EmployeeNumber, string EmployeeName, DateOnly Day, decimal Hours, decimal OvertimeHours,
    decimal AttendanceHours, string? OverrideReason, string? OverriddenBy, string? UserName, DateTime AtUtc);

/// <summary>
/// A job's hours: every booking, day by day and person by person, and their total; and the company's today on the
/// server's clock, the day a new booking starts from.
/// </summary>
public record MaintenanceJobHoursDto(
    Guid JobId, string JobStatus, decimal TotalHours, IReadOnlyList<MaintenanceBookingDto> Bookings, DateOnly Today);

/// <summary>
/// Someone who may be booked on the job that day (MS-04: an employee of the job's branch, employed on the day): the hours
/// their attendance shows (null when it is declared per month), how it was read, what is already booked that day on every
/// production record and job, whether they already have hours on this job that day, and whether their payroll month is
/// closed.
/// </summary>
public record MaintenanceLabourCandidateDto(
    Guid EmployeeId, string Number, string FullName, decimal? AttendanceHours, string AttendanceSource, decimal BookedHours,
    bool BookedOnJob, bool PayrollLocked);

/// <summary>
/// Books one employee's hours on an open job for one day (<c>maintenance.jobs.manage</c>). <c>OvertimeHours</c> (C-05):
/// how many of them were overtime, 0 to <c>Hours</c>. Past what the day's attendance leaves, <c>OverrideReason</c> is
/// required and the caller needs <c>maintenance.labour.override</c>.
/// </summary>
public record BookMaintenanceHoursRequest(Guid EmployeeId, DateOnly Day, decimal Hours, decimal OvertimeHours = 0m, string? OverrideReason = null);
