using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ParadiERP.Tests.Persistence;

/// <summary>
/// The upgrade for `EmployeeNameParts` (owner, PinSay #301: first name and family name apart) — the
/// shape of <c>EmployeeEmailMigrationTests</c>. Everyone already on file keeps the name they print
/// under: the whole name becomes the family name, the first name stays empty, and so the first-name-
/// first full name is the one they had.
/// </summary>
public class EmployeeNamePartsMigrationTests
{
    /// <summary>The release before this one.</summary>
    private const string Before = "ContractWordings";

    [Fact]
    public async Task People_on_file_keep_their_whole_name_as_the_family_name()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);

        await db.Database.GetService<IMigrator>().MigrateAsync(Before);

        // The branch table is the same shape on both sides of this release, so the model can write it;
        // the employee is written in the old shape with raw SQL. Parameters, never formatted text.
        var branch = new Branch { NameFr = "Oran", IsMain = true, IsOpenForOperation = true };
        db.Branches.Add(branch);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var employeeId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Employees (Id, Number, FullName, BranchId, HiredOn, PayBasis, BaseRate, PayoutMethod, IsActive, CreatedAtUtc, UpdatedAtUtc) " +
            "VALUES ({0}, 'EMP-000001', 'Karim Haddad', {1}, {2}, 1, {3}, 0, 1, {4}, {4})",
            employeeId, branch.Id, new DateOnly(2024, 3, 1), 52_000m, now);

        // The upgrade itself. A statement that is not valid SQL on this provider stops here.
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        var upgraded = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == employeeId);
        Assert.Null(upgraded.FirstName);
        Assert.Equal("Karim Haddad", upgraded.FamilyName);
        Assert.Equal("Karim Haddad", upgraded.FullName);
    }
}
