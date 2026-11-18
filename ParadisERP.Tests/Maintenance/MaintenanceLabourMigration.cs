using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ParadisERP.Tests.Maintenance;

/// <summary>
/// The upgrade an installation with production hours performs at Phase M3 (migration <c>MaintenanceLabour</c>): a
/// database at the release before it (<c>MaintenanceJobs</c>) holding a line, a day's record, three bookings and their
/// change rows, written in the old shape with raw SQL; then the real migrations; then every row read back unchanged.
/// SQLite rebuilds both tables to make the columns nullable, SQL Server alters them in place — the same assertions run on
/// each (<see cref="MaintenanceLabourTests"/>, <see cref="MaintenanceLabourSqlServerTests"/>).
/// </summary>
internal static class MaintenanceLabourMigration
{
    public const string SqliteBefore = "MaintenanceJobs";
    public const string SqlServerBefore = "MaintenanceJobs";

    public static readonly Guid Branch = Guid.Parse("b3000000-0000-4000-8000-000000000001");
    public static readonly Guid Karim = Guid.Parse("b3000000-0000-4000-8000-000000000002");
    public static readonly Guid Samir = Guid.Parse("b3000000-0000-4000-8000-000000000003");
    public static readonly Guid Line = Guid.Parse("b3000000-0000-4000-8000-000000000004");
    public static readonly Guid Log = Guid.Parse("b3000000-0000-4000-8000-000000000005");
    public static readonly Guid RowA = Guid.Parse("b3000000-0000-4000-8000-000000000006");
    public static readonly Guid RowB = Guid.Parse("b3000000-0000-4000-8000-000000000007");
    public static readonly Guid Order = Guid.Parse("b3000000-0000-4000-8000-000000000008");

    /// <summary>The three bookings: id, employee, row, hours, overtime, override reason.</summary>
    public static readonly (Guid Id, Guid Employee, Guid Row, decimal Hours, decimal Overtime, string? Reason)[] Bookings =
    [
        (Guid.Parse("b3000000-0000-4000-8000-0000000000a1"), Karim, RowA, 5m, 0m, null),
        (Guid.Parse("b3000000-0000-4000-8000-0000000000a2"), Samir, RowA, 2.5m, 0m, "Declared attendance"),
        (Guid.Parse("b3000000-0000-4000-8000-0000000000a3"), Karim, RowB, 3m, 1m, null),
    ];

    public static readonly Guid RemovedBooking = Guid.Parse("b3000000-0000-4000-8000-0000000000a4");
    public static readonly DateOnly Day = new(2026, 10, 20);

    public static async Task SeedBeforeAsync(PosDbContext db, string before)
    {
        await db.Database.GetService<IMigrator>().MigrateAsync(before);
        Assert.DoesNotContain(await db.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("_MaintenanceLabour", StringComparison.Ordinal));

        var now = new DateTime(2026, 10, 20, 15, 0, 0, DateTimeKind.Utc);
        // Bound as parameters, never formatted into the text: only the provider knows how it spells a Guid or a date.
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Branches\" (\"Id\", \"Address\", \"AddressAr\", \"AddressEn\", \"AddressFr\", \"AllowNegativeStock\", \"FiscalIds\", \"IsActive\", " +
            "\"IsDeleted\", \"IsMain\", \"IsOpenForOperation\", \"NameAr\", \"NameEn\", \"NameFr\", \"Phone\", \"UpdatedAtUtc\", \"Version\", \"IsWarehouse\", \"Code\") " +
            "VALUES ({0}, '', '', '', '', 0, '{{}}', 1, 0, 1, 1, '', '', 'Alger', '', {1}, 1, 0, 'ALG')", Branch, now);
        foreach (var (id, number, name) in new[] { (Karim, "E-001", "Karim Benali"), (Samir, "E-002", "Samir Haddad") })
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"Employees\" (\"Id\", \"Number\", \"FullName\", \"BranchId\", \"HiredOn\", \"PayBasis\", \"BaseRate\", \"PayoutMethod\", \"IsActive\", " +
                "\"CreatedAtUtc\", \"UpdatedAtUtc\") VALUES ({0}, {1}, {2}, {3}, {4}, 0, {5}, 0, 1, {6}, {6})",
                id, number, name, Branch, new DateOnly(2025, 1, 1), 50_000m, now);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"ProductionLines\" (\"Id\", \"Code\", \"NameAr\", \"NameFr\", \"NameEn\", \"LocationId\", \"IsActive\", \"CreatedAtUtc\", \"UpdatedAtUtc\") " +
            "VALUES ({0}, 'LA', '', 'Ligne A', '', {1}, 1, {2}, {2})", Line, Branch, now);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"ProductionLogs\" (\"Id\", \"Number\", \"LineId\", \"Day\", \"Status\", \"CreatedAtUtc\") VALUES ({0}, 'PL-000001', {1}, {2}, 0, {3})",
            Log, Line, Day, now);
        foreach (var (row, sequence) in new[] { (RowA, 1), (RowB, 2) })
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"ProductionLogRows\" (\"Id\", \"LogId\", \"Sequence\", \"PlannedQty\", \"CompletedQty\", \"RejectedQty\") VALUES ({0}, {1}, {2}, {3}, {3}, {3})",
                row, Log, sequence, 0m);
        foreach (var b in Bookings)
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"LaborBookings\" (\"Id\", \"EmployeeId\", \"Day\", \"OrderKind\", \"OrderId\", \"LineId\", \"LogRowId\", \"Hours\", \"PayrollBranchId\", " +
                "\"PayrollYear\", \"PayrollMonth\", \"AttendanceHours\", \"UserName\", \"AtUtc\", \"OvertimeHours\") " +
                "VALUES ({0}, {1}, {2}, 0, {3}, {4}, {5}, {6}, {7}, 2026, 10, {8}, 'chef', {9}, {10})",
                b.Id, b.Employee, Day, Order, Line, b.Row, b.Hours, Branch, b.Reason is null ? 8m : 0m, now, b.Overtime);
            if (b.Reason is string reason)
                await db.Database.ExecuteSqlRawAsync("UPDATE \"LaborBookings\" SET \"OverrideReason\" = {0} WHERE \"Id\" = {1}", reason, b.Id);
            await ChangeAsync(db, b.Id, b.Employee, b.Row, LaborBookingAction.Created, b.Hours, b.Overtime, now);
        }
        // A booking made and removed: only its two change rows are left.
        await ChangeAsync(db, RemovedBooking, Samir, RowB, LaborBookingAction.Created, 1m, 0m, now);
        await ChangeAsync(db, RemovedBooking, Samir, RowB, LaborBookingAction.Removed, 1m, 0m, now.AddMinutes(5));
    }

    private static Task<int> ChangeAsync(PosDbContext db, Guid booking, Guid employee, Guid row, LaborBookingAction action, decimal hours,
        decimal overtime, DateTime at) =>
        db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"LaborBookingChanges\" (\"Id\", \"BookingId\", \"EmployeeId\", \"Day\", \"LogRowId\", \"Action\", \"Hours\", \"UserName\", \"AtUtc\", \"OvertimeHours\") " +
            "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, 'chef', {7}, {8})",
            Guid.NewGuid(), booking, employee, Day, row, (int)action, hours, at, overtime);

    /// <summary>Every row as it was; the change rows read as production; the unique row index still holds; a booking with no row now fits.</summary>
    public static async Task AssertKeptAsync(PosDbContext db)
    {
        var bookings = await db.LaborBookings.AsNoTracking().OrderBy(b => b.Id).ToListAsync();
        Assert.Equal(Bookings.Length, bookings.Count);
        foreach (var (expected, actual) in Bookings.OrderBy(b => b.Id).Zip(bookings))
        {
            Assert.Equal((expected.Id, expected.Employee, Day, LaborOrderKind.Production, (Guid?)Order, (Guid?)Line, (Guid?)expected.Row),
                (actual.Id, actual.EmployeeId, actual.Day, actual.OrderKind, actual.OrderId, actual.LineId, actual.LogRowId));
            Assert.Equal((expected.Hours, expected.Overtime, expected.Reason, Branch, 2026, 10, expected.Reason is null ? 8m : 0m, "chef"),
                (actual.Hours, actual.OvertimeHours, actual.OverrideReason, actual.PayrollBranchId, actual.PayrollYear, actual.PayrollMonth,
                    actual.AttendanceHours, actual.UserName));
        }
        var changes = await db.LaborBookingChanges.AsNoTracking().ToListAsync();
        Assert.Equal(Bookings.Length + 2, changes.Count);
        Assert.All(changes, c => Assert.Equal((LaborOrderKind.Production, (Guid?)null, Day), (c.OrderKind, c.OrderId, c.Day)));
        Assert.Equal(Bookings.Select(b => (b.Id, (Guid?)b.Row, b.Hours, b.Overtime)).Order(),
            changes.Where(c => c.BookingId != RemovedBooking).Select(c => (c.BookingId, c.LogRowId, c.Hours, c.OvertimeHours)).Order());
        Assert.Equal([LaborBookingAction.Created, LaborBookingAction.Removed],
            changes.Where(c => c.BookingId == RemovedBooking).OrderBy(c => c.AtUtc).Select(c => c.Action));

        // The same person on the same row is still refused by the index.
        db.LaborBookings.Add(new LaborBooking
        {
            EmployeeId = Karim, Day = Day, LineId = Line, LogRowId = RowA, OrderId = Order, Hours = 1m, PayrollBranchId = Branch,
            PayrollYear = 2026, PayrollMonth = 10,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // Two maintenance bookings of one person, with no row, fit beside the production ones.
        foreach (var day in new[] { 21, 22 })
            db.LaborBookings.Add(new LaborBooking
            {
                EmployeeId = Karim, Day = new DateOnly(2026, 10, day), OrderKind = LaborOrderKind.Maintenance, OrderId = Guid.NewGuid(), Hours = 1m,
                PayrollBranchId = Branch, PayrollYear = 2026, PayrollMonth = 10,
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.LaborBookings.CountAsync(b => b.LogRowId == null && b.LineId == null));
    }
}
