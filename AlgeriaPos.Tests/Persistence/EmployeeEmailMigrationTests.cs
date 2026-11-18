using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlgeriaPos.Tests.Persistence;

/// <summary>
/// The upgrade a company that already keeps a staff file performs for `EmployeeEmail` (owner,
/// 2026-10-05: an optional e-mail address on the employee) — the shape of
/// <c>PayrollAndDocumentHistoryMigrationTests</c>. One nullable column and nothing else: every person
/// already on file comes through unchanged and with no address, and a legacy Daily row stays Daily
/// (no migration converts anyone's pay basis).
/// </summary>
public class EmployeeEmailMigrationTests
{
    /// <summary>The release before this one.</summary>
    private const string Before = "UserEmployeeAndLoginId";

    [Fact]
    public async Task People_already_on_file_come_through_unchanged_and_with_no_address()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);

        await db.Database.GetService<IMigrator>().MigrateAsync(Before);

        // Both rows are written in the old shape with raw SQL, because the current model names columns
        // the old tables do not have (the branch's Code came with BranchCodes, a later release).
        // Parameters, never formatted text.
        var branch = new Branch { NameFr = "Oran", IsMain = true, IsOpenForOperation = true };
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Branches (Id, Version, UpdatedAtUtc, IsDeleted, NameAr, NameFr, NameEn, Address, AddressAr, AddressFr, AddressEn, Phone, IsMain, IsWarehouse, AllowNegativeStock, IsActive, IsOpenForOperation, FiscalIds) " +
            "VALUES ({0}, 1, {1}, 0, '', 'Oran', '', '', '', '', '', '', 1, 0, 0, 1, 1, '{{}}')",
            branch.Id, now);

        var employeeId = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Employees (Id, Number, FullName, BranchId, HiredOn, PayBasis, BaseRate, PayoutMethod, IsActive, CreatedAtUtc, UpdatedAtUtc, Phone) " +
            "VALUES ({0}, 'EMP-000001', 'Karim Haddad', {1}, {2}, 0, {3}, 0, 1, {4}, {4}, '0550 12 34 56')",
            employeeId, branch.Id, new DateOnly(2024, 3, 1), 1_500m, now);

        // The upgrade itself. A statement that is not valid SQL on this provider stops here.
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        var upgraded = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == employeeId);
        Assert.Null(upgraded.Email);
        Assert.Equal("Karim Haddad", upgraded.FullName);
        Assert.Equal("0550 12 34 56", upgraded.Phone);
        Assert.Equal(PayBasis.Daily, upgraded.PayBasis);
        Assert.Equal(1_500m, upgraded.BaseRate);

        // And the new column takes an address.
        var tracked = await db.Employees.SingleAsync(e => e.Id == employeeId);
        tracked.Email = "karim.haddad@exemple.dz";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal("karim.haddad@exemple.dz", (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == employeeId)).Email);
    }
}
