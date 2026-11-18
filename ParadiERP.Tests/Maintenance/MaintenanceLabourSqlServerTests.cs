using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Tests.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Tests.Maintenance;

/// <summary>
/// Phase M3's migration on SQL Server, where the columns are altered in place and the unique (EmployeeId, LogRowId)
/// index is rebuilt with a NOT NULL filter: a database with production hours keeps every row. Runs only where
/// <c>ALGERIAPOS_TEST_SQLSERVER</c> names a server.
/// </summary>
public sealed class MaintenanceLabourSqlServerTests
{
    [SqlServerFact]
    public async Task The_migration_keeps_every_booking_and_change_row_on_SQL_Server()
    {
        await using var database = new SqlServerTestDatabase();
        var options = new DbContextOptionsBuilder<PosDbContext>();
        PosDbProviders.Configure(options, PosDbProviders.SqlServer, database.ConnectionString);
        await using var db = new PosDbContext(options.Options);
        await MaintenanceLabourMigration.SeedBeforeAsync(db, MaintenanceLabourMigration.SqlServerBefore);

        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        await using (var connection = new SqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT CONCAT(i.is_unique, ':', i.filter_definition) FROM sys.indexes i WHERE i.object_id = OBJECT_ID(N'dbo.LaborBookings') " +
                "AND i.name = N'IX_LaborBookings_EmployeeId_LogRowId'", connection);
            Assert.Equal("1:([LogRowId] IS NOT NULL)", (string?)await command.ExecuteScalarAsync());
        }
        await MaintenanceLabourMigration.AssertKeptAsync(db);
    }
}
