using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlgeriaPos.Tests.Customers;

/// <summary>
/// The upgrade an installation that is already trading actually performs, with rows in it.
///
/// <para>
/// Two defaults carry the whole release for existing data: a customer that predates the kind
/// is <see cref="CustomerKind.Unspecified"/> — never guessed — and a contract that predates the
/// signatory rule is exempt from it. Both are column defaults rather than backfills, and a
/// column default is exactly the kind of thing that is right in the migration file and wrong in
/// the database if anyone reorders the enum or hand-edits the scaffold. So the real migrations
/// run against a real database holding a row of each, and the row is read back.
/// </para>
/// </summary>
public class CustomerKindMigrationTests
{
    /// <summary>The release before this one.</summary>
    private const string Before = "InvoicesAndStatements";

    [Fact]
    public async Task The_release_migrates_a_database_at_InvoicesAndStatements_and_leaves_every_customer_unspecified_and_every_contract_exempt()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);

        await db.Database.GetService<IMigrator>().MigrateAsync(Before);

        // Rows in the old shape, written with raw SQL because the current model would insist on
        // columns the old table does not have.
        var customerId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        // Bound as parameters, never formatted into the text: SQLite compares Guid and date
        // columns as strings, and only the provider knows the exact spelling it will query with.
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Customers (Id, Version, UpdatedAtUtc, IsDeleted, Code, FullName, CreditLimit, Balance, LoyaltyPoints) " +
            "VALUES ({0}, 1, {1}, 0, 'C001', 'Ben Ali BTP', {2}, {2}, 0)",
            customerId, now, 0m);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Branches (Id, Version, UpdatedAtUtc, IsDeleted, NameAr, NameFr, NameEn, Address, Phone, IsMain, AllowNegativeStock, IsActive, IsOpenForOperation) " +
            "VALUES ({0}, 1, {1}, 0, '', 'Alger-Centre', '', '', '', 1, 0, 1, 1)",
            branchId, now);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO RentalContracts (Id, Number, CustomerId, BranchId, Status, SignedAtUtc, CreatedAtUtc, DepositAmount, CreatedBy) " +
            "VALUES ({0}, 'CTR-000001', {1}, {2}, 0, {3}, {3}, {4}, 'admin')",
            contractId, customerId, branchId, now, 0m);

        // The upgrade an existing installation performs. If a statement is not valid SQL on this
        // provider, or a default is missing, it stops here.
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        var customer = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == customerId);
        Assert.Equal(CustomerKind.Unspecified, customer.Kind);
        Assert.Null(customer.IdDocumentNumber);

        var contract = await db.RentalContracts.AsNoTracking().SingleAsync(c => c.Id == contractId);
        Assert.False(contract.SignatoryRequired);
        Assert.Null(contract.SignatoryName);
        Assert.Null(contract.SignatoryIdNumber);

        // Nobody was invented to sign for anyone.
        Assert.Empty(await db.CustomerRepresentatives.ToListAsync());
    }
}
