using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Manufacturing;

/// <summary>
/// The one labour ceiling (amendment 7; docs/maintenance/plan.md §2.5): a person's hours booked on a day — on production
/// records and maintenance jobs alike — never pass what their attendance shows that day (<see cref="AttendanceCapacity"/>),
/// unless someone allowed to override says why. Production bookings (<see cref="ProductionLogs"/>) and maintenance
/// bookings (<c>MaintenanceLabour</c>) both read the day's total here, under the version lock their write already holds,
/// so two bookings on two screens cannot both take the last hour.
/// </summary>
public static class LabourCeiling
{
    public const string ExceedsCode = "exceeds_attendance";

    /// <summary>Every hour booked for the person that day, on every line and order kind. Summed in memory: SQLite stores decimal as text.</summary>
    public static async Task<decimal> BookedAsync(PosDbContext db, Guid employeeId, DateOnly day, CancellationToken ct) =>
        (await db.LaborBookings.AsNoTracking().Where(b => b.EmployeeId == employeeId && b.Day == day)
            .Select(b => b.Hours).ToListAsync(ct)).Sum();

    /// <summary>Why the hours do not fit: a declared-per-month attendance shows no day at all, otherwise the day's hours and those already booked.</summary>
    public static string Exceeds(DayAttendance day, decimal booked) =>
        day.Hours is null
            ? "This person's attendance is declared per month, so the day shows no hours; booking needs an override with a reason."
            : $"The day's attendance shows {(day.Hours ?? 0m):0.##} h and {booked:0.##} h are already booked.";
}
