using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ParadiERP.Tests.Receivables;

/// <summary>
/// The upgrade an installation that is already trading actually performs.
///
/// <para>
/// <c>LedgerEntryLine.CustomerId</c> arrives nullable, and a nullable column added to a table
/// with rows in it is null on every one of them. Left there, account 411 would carry a
/// contractor's whole trading history with nobody's name against any of it: every statement
/// would open at zero, the ageing report would show only what had been billed since the upgrade,
/// and <b>nothing anywhere would fail</b>. Four bugs of exactly this shape have been found in
/// this repository in a week, which is why this is a test and not a hope.
/// </para>
/// <para>
/// The two facts are checked separately because they need opposite setups. That the migration
/// runs — backfill included — needs a database at the previous release; that the backfill finds
/// the right customer needs rows that have none, which is easier to arrange after the column
/// exists than before it. The SQL is the same object in both, and the two server providers run
/// it for real through <c>tests/e2e/provider-smoke.cjs</c>.
/// </para>
/// </summary>
public class LedgerCustomerBackfillTests
{
    /// <summary>The release before this one: everything of phases 1 to 4 and none of phase 5.</summary>
    private const string Before = "RentalTax";

    private static readonly Guid Contractor = Guid.NewGuid();
    private static readonly Guid Branch = Guid.NewGuid();

    [Fact]
    public async Task The_release_migrates_a_database_that_is_already_at_the_previous_one()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = New(connection);

        await db.Database.GetService<IMigrator>().MigrateAsync(Before);
        // The upgrade an existing installation performs, backfill included: if any of those
        // statements is not valid SQL on this provider, the upgrade stops here.
        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Empty(await db.Invoices.ToListAsync());
    }

    [Fact]
    public async Task The_backfill_puts_every_customer_back_on_the_lines_that_were_theirs()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = New(connection);
        await db.Database.MigrateAsync();

        var customers = Account(LedgerAccounts.Customers, LedgerAccountType.Asset);
        var notes = Account(LedgerAccounts.NotesReceivable, LedgerAccountType.Asset);
        var income = Account(LedgerAccounts.Sales, LedgerAccountType.Income);
        db.LedgerAccounts.AddRange(customers, notes, income);

        db.Customers.Add(new Customer { Id = Contractor, Code = "C001", FullName = "Ben Ali BTP" });
        db.Branches.Add(new Branch { Id = Branch, NameFr = "Alger-Centre", IsMain = true });

        var sale = Sale(Contractor, "T-0001", 1);
        var walkIn = Sale(null, "T-0002", 2);
        db.Sales.AddRange(sale, walkIn);

        var cheque = new SalePayment
        {
            Id = Guid.NewGuid(), SaleId = sale.Id, Method = PaymentMethod.Card, Amount = 40_000m,
            MethodKey = "cheque", Behavior = PaymentBehavior.Generic, Status = PaymentStatus.Confirmed
        };
        db.SalePayments.Add(cheque);

        var product = new Product { Sku = "ECH-01", NameFr = "Échafaudage", IsRentable = true };
        var contract = new RentalContract
        {
            Number = "CTR-000001", CustomerId = Contractor, BranchId = Branch, SignedAtUtc = DateTime.UtcNow
        };
        contract.Lines.Add(new RentalContractLine
        {
            ContractId = contract.Id, ProductId = product.Id, Sku = product.Sku, NameSnapshot = product.NameFr,
            Qty = 10, DurationDays = 30, RentUnitPrice = 3_000m, DamageUnitPrice = 2_000m, OverdueUnitPrice = 4_500m
        });
        var issueNote = new IssueNote
        {
            Number = "ISS-000001", ContractId = contract.Id, BranchId = Branch, IssuedAtUtc = DateTime.UtcNow
        };
        db.Products.Add(product);
        db.RentalContracts.Add(contract);
        db.IssueNotes.Add(issueNote);

        // As the previous release wrote them: every line with no customer on it. A sale on
        // account, the cheque taken on it and later confirmed, goods out on hire, and a walk-in
        // whose tenders did not add up. The first three are somebody's; the last is not.
        db.LedgerEntries.AddRange(
            Entry(LedgerSources.Sale, sale.Id, Line(customers, 60_000m, 0m), Line(income, 0m, 60_000m)),
            Entry(LedgerSources.Sale, sale.Id, Line(notes, 40_000m, 0m), Line(income, 0m, 40_000m)),
            Entry(LedgerSources.PaymentSettlement, cheque.Id, Line(customers, 40_000m, 0m), Line(notes, 0m, 40_000m)),
            Entry(LedgerSources.RentalIssue, issueNote.Id, Line(customers, 90_000m, 0m), Line(income, 0m, 90_000m)),
            Entry(LedgerSources.Sale, walkIn.Id, Line(customers, 5_000m, 0m), Line(income, 0m, 5_000m)));

        await db.SaveChangesAsync();
        Assert.All(await db.LedgerEntryLines.ToListAsync(), l => Assert.Null(l.CustomerId));

        foreach (var statement in LedgerCustomerBackfill.Statements(LedgerCustomerBackfill.Sqlite))
            await db.Database.ExecuteSqlRawAsync(statement);

        var lines = await db.LedgerEntryLines.AsNoTracking()
            .Join(db.LedgerAccounts.AsNoTracking(), l => l.AccountId, a => a.Id,
                (l, a) => new { a.Code, l.CustomerId, l.Debit, l.Credit })
            .ToListAsync();

        decimal Owed(string code) =>
            lines.Where(x => x.Code == code && x.CustomerId == Contractor).Sum(x => x.Debit - x.Credit);

        // 60 000 of trade debt and 90 000 of hire, plus the 40 000 the settlement moved back on
        // to the trade account — and both halves of the cheque itself attributed.
        Assert.Equal(190_000m, Owed(LedgerAccounts.Customers));
        Assert.Equal(0m, Owed(LedgerAccounts.NotesReceivable));
        Assert.Equal(2, lines.Count(x => x.Code == LedgerAccounts.NotesReceivable && x.CustomerId == Contractor));

        // A sale with no customer had nobody to bill, and inventing one would be worse than the
        // gap it filled.
        Assert.Null(lines.Single(x => x.Code == LedgerAccounts.Customers && x.Debit == 5_000m).CustomerId);
        // Income is not a third-party account and is left alone.
        Assert.All(lines.Where(x => x.Code == LedgerAccounts.Sales), x => Assert.Null(x.CustomerId));

        // Run twice, nothing moves: each statement touches only rows still unattributed, which
        // is also what stops the four of them overwriting one another.
        foreach (var statement in LedgerCustomerBackfill.Statements(LedgerCustomerBackfill.Sqlite))
            await db.Database.ExecuteSqlRawAsync(statement);
        Assert.Equal(190_000m, await TradeDebtAsync(db));
    }

    private static async Task<decimal> TradeDebtAsync(PosDbContext db)
    {
        var account = await db.LedgerAccounts.AsNoTracking().FirstAsync(a => a.Code == LedgerAccounts.Customers);
        var rows = await db.LedgerEntryLines.AsNoTracking()
            .Where(l => l.AccountId == account.Id && l.CustomerId == Contractor)
            .Select(l => new { l.Debit, l.Credit })
            .ToListAsync();
        return rows.Sum(r => r.Debit - r.Credit);
    }

    private static PosDbContext New(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);

    private static LedgerAccount Account(string code, LedgerAccountType type) =>
        new() { Code = code, NameFr = code, Type = type, IsSystem = true };

    private static Sale Sale(Guid? customerId, string reference, long seq) => new()
    {
        Id = Guid.NewGuid(), Reference = reference, DeviceCode = "C01", BranchId = Branch, LocalSeq = seq,
        SoldAtUtc = DateTime.UtcNow, ReceivedAtUtc = DateTime.UtcNow,
        CustomerId = customerId, CashierName = "Cashier", GrandTotalTtc = 100_000m
    };

    private static LedgerEntry Entry(string sourceType, Guid sourceId, params LedgerEntryLine[] lines)
    {
        var entry = new LedgerEntry
        {
            EntryDate = DateTime.UtcNow, BranchId = Branch, SourceType = sourceType, SourceId = sourceId
        };
        foreach (var line in lines)
        {
            line.EntryId = entry.Id;
            line.BranchId = Branch;
            entry.Lines.Add(line);
        }
        return entry;
    }

    private static LedgerEntryLine Line(LedgerAccount account, decimal debit, decimal credit) =>
        new() { AccountId = account.Id, Debit = debit, Credit = credit };
}
