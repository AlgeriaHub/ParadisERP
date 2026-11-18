using ParadiERP.Application.Catalog;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Rentals;
using ParadiERP.Tests.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ParadiERP.Tests.Rentals;

public class BranchContractNumberMigrationTests
{
    private const string SqliteBefore = "20261013164628_SameDayRentalUsage";
    private const string SqlServerBefore = "20261013164817_SameDayRentalUsage";

    [Fact]
    public async Task Sqlite_upgrade_preserves_sequences_and_continues_each_branch_independently()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await VerifyUpgrade(db, SqliteBefore);
    }

    [SqlServerFact]
    public async Task SqlServer_upgrade_preserves_sequences_and_continues_each_branch_independently()
    {
        await using var database = new SqlServerTestDatabase();
        var options = new DbContextOptionsBuilder<PosDbContext>();
        PosDbProviders.Configure(options, PosDbProviders.SqlServer, database.ConnectionString);
        await using var db = new PosDbContext(options.Options);
        await VerifyUpgrade(db, SqlServerBefore);
    }

    private static async Task VerifyUpgrade(PosDbContext db, string before)
    {
        await db.Database.GetService<IMigrator>().MigrateAsync(before);
        var a = new Branch { Code = "10", NameFr = "Branch A", IsMain = true };
        var b = new Branch { Code = "13", NameFr = "Branch B" };
        var deleted = new Branch { Code = "99", NameFr = "Deleted branch", IsDeleted = true };
        var customer = new Customer { Code = "legacy-customer", FullName = "Synthetic customer" };
        db.Branches.AddRange(a, b, deleted);
        db.Customers.Add(customer);
        if (!await db.SyncCounters.AnyAsync()) db.SyncCounters.Add(new SyncCounter { Id = 1 });
        await db.SaveChangesAsync();
        // Raw SQL, in the columns this older schema has: today's model carries columns it does not yet (the contract's
        // TaxMode, 2026-11-03), and saving through the model would write them.
        var first = new RentalContract { Number = "CTR-000027", BranchId = a.Id, CustomerId = customer.Id };
        var second = new RentalContract { Number = "CTR-000028", BranchId = b.Id, CustomerId = customer.Id, Status = RentalContractStatus.Cancelled };
        foreach (var contract in new[] { first, second })
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO RentalContracts (Id, Number, CustomerId, BranchId, Status, SignedAtUtc, CreatedAtUtc, DepositAmount, " +
                "SignatoryRequired, SignedByAgent, MinimumRentalDays) VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {5}, {6}, {7}, {7}, {8})",
                contract.Id, contract.Number, contract.CustomerId, contract.BranchId, (int)contract.Status,
                contract.SignedAtUtc, 0m, false, 0);
        var oldVersion = await db.SyncCounters.Select(c => c.Value).SingleAsync();
        db.ChangeTracker.Clear();

        await db.Database.MigrateAsync();
        var branches = await db.Branches.AsNoTracking().ToListAsync();
        Assert.All(branches, branch => Assert.True(BranchCodes.IsWellFormed(branch.Code)));
        Assert.Equal(3, branches.Select(branch => branch.Code).Distinct().Count());
        Assert.All(branches, branch => Assert.True(branch.Version > oldVersion));
        Assert.Equal(3, branches.Select(branch => branch.Version).Distinct().Count());
        var codeA = branches.Single(x => x.Id == a.Id).Code;
        var codeB = branches.Single(x => x.Id == b.Id).Code;
        Assert.Equal("CTR-" + codeA + "000027", (await db.RentalContracts.FindAsync(first.Id))!.Number);
        Assert.Equal("CTR-" + codeB + "000028", (await db.RentalContracts.FindAsync(second.Id))!.Number);
        Assert.Equal("legacy-customer", (await db.Customers.FindAsync(customer.Id))!.Code);
        await using var tx = await db.BeginWriteScopeAsync();
        Assert.Equal("CTR-" + codeA + "000028", await ContractNumbers.NextAsync(db, a.Id));
        Assert.Equal("CTR-" + codeB + "000029", await ContractNumbers.NextAsync(db, b.Id));
        await tx.CommitAsync();
        await db.Database.MigrateAsync();
        Assert.Equal("CTR-" + codeB + "000028", (await db.RentalContracts.FindAsync(second.Id))!.Number);
    }
}

public partial class RentalDocumentTests
{
    [Fact]
    public async Task New_contracts_use_their_branch_code_and_independent_sequences()
    {
        await using var h = await NewAsync();
        var a = await h.Db.Branches.Where(b => b.Id == Algiers).Select(b => b.Code).SingleAsync();
        var b = await h.Db.Branches.Where(b => b.Id == Oran).Select(b => b.Code).SingleAsync();
        var first = await h.Rentals.CreateContractAsync(Contract(), "admin");
        var other = await h.Rentals.CreateContractAsync(Contract() with { BranchId = Oran }, "admin");
        var next = await h.Rentals.CreateContractAsync(Contract(), "admin");
        Assert.True(first.Ok, first.Message);
        Assert.True(other.Ok, other.Message);
        Assert.True(next.Ok, next.Message);
        Assert.Equal("CTR-" + a + "000001", first.Value!.Number);
        Assert.Equal("CTR-" + b + "000001", other.Value!.Number);
        Assert.Equal("CTR-" + a + "000002", next.Value!.Number);
    }
}
