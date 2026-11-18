using ParadisERP.Domain;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Infrastructure.Seeding;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Ledger;

/// <summary>
/// Putting right the cash every branch was recorded as holding, for the sales taken while the
/// till netted change off twice.
///
/// <para>
/// The correction runs at startup, so the thing that matters most is that a second startup
/// changes nothing. A correction applied twice is worse than one never applied: the first is a
/// visible shortfall, the second is a surplus nobody can explain.
/// </para>
/// </summary>
public class ChangeRestatementTests
{
    private static readonly Guid Branch = Guid.NewGuid();
    private static readonly Guid OtherBranch = Guid.NewGuid();

    private static async Task<PosDbContext> FreshAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.SyncCounters.Add(new SyncCounter { Id = 1, Value = 0 });
        db.Branches.Add(new Branch { Id = Branch, NameFr = "Alger-Centre" });
        db.Branches.Add(new Branch { Id = OtherBranch, NameFr = "Oran" });
        db.CashBoxes.Add(new CashBox { BranchId = Branch, NameFr = "Caisse Alger" });
        await db.SaveChangesAsync();
        // The chart comes from the same startup path a real company gets it from.
        await CoreDataSeeder.EnsureAsync(db);
        return db;
    }

    /// <summary>A completed cash sale that gave change — the shape the defect touched.</summary>
    private static void AddCashSale(PosDbContext db, Guid branchId, decimal tendered, decimal change)
    {
        var id = Guid.NewGuid();
        db.Sales.Add(new Sale
        {
            Id = id,
            Reference = "C01-" + Guid.NewGuid().ToString("N")[..6],
            DeviceCode = "C01",
            BranchId = branchId,
            LocalSeq = Random.Shared.NextInt64(1, long.MaxValue),
            SoldAtUtc = DateTime.UtcNow,
            ReceivedAtUtc = DateTime.UtcNow,
            GrandTotalTtc = tendered - change,
            PaidAmount = tendered,
            ChangeAmount = change,
            PaymentMethod = PaymentMethod.Cash,
            Status = SaleStatus.Completed,
            Payments =
            [
                new SalePayment
                {
                    Id = Guid.NewGuid(), SaleId = id, MethodKey = "cash",
                    Behavior = PaymentBehavior.Cash, Status = PaymentStatus.Confirmed,
                    Amount = tendered - change
                }
            ]
        });
    }

    private static async Task<decimal> AccountBalanceAsync(PosDbContext db, string code)
    {
        var rows = await db.LedgerEntryLines.AsNoTracking()
            .Join(db.LedgerAccounts.AsNoTracking(), l => l.AccountId, a => a.Id, (l, a) => new { a.Code, l.Debit, l.Credit })
            .Where(x => x.Code == code)
            .ToListAsync();
        return rows.Sum(x => x.Debit - x.Credit);
    }

    [Fact]
    public async Task The_shortfall_restated_is_exactly_the_change_that_was_handed_out()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await FreshAsync(connection);

        AddCashSale(db, Branch, tendered: 2000m, change: 800m);
        AddCashSale(db, Branch, tendered: 5000m, change: 250m);
        await db.SaveChangesAsync();

        var corrected = await ChangeRestatement.ApplyAsync(db, new LedgerPoster(db));

        Assert.Single(corrected);
        Assert.Equal(1050m, corrected[0].Amount);
        // Cash up, suspense down: the money was always in the drawer, the record was not.
        Assert.Equal(1050m, await AccountBalanceAsync(db, LedgerAccounts.Cash));
        Assert.Equal(-1050m, await AccountBalanceAsync(db, LedgerAccounts.Suspense));
    }

    [Fact]
    public async Task Running_it_a_second_time_changes_nothing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await FreshAsync(connection);

        AddCashSale(db, Branch, tendered: 2000m, change: 800m);
        await db.SaveChangesAsync();

        await ChangeRestatement.ApplyAsync(db, new LedgerPoster(db));
        var again = await ChangeRestatement.ApplyAsync(db, new LedgerPoster(db));

        // The whole safety of running this at startup rests on this: a correction applied
        // twice leaves a surplus nobody can explain, which is worse than the shortfall.
        Assert.Empty(again);
        Assert.Equal(800m, await AccountBalanceAsync(db, LedgerAccounts.Cash));
        Assert.Single(await db.CashMovements.ToListAsync());
    }

    [Fact]
    public async Task Each_branch_is_corrected_for_its_own_drawer_only()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await FreshAsync(connection);

        AddCashSale(db, Branch, tendered: 1000m, change: 300m);
        AddCashSale(db, OtherBranch, tendered: 1000m, change: 120m);
        await db.SaveChangesAsync();

        var corrected = await ChangeRestatement.ApplyAsync(db, new LedgerPoster(db));

        Assert.Equal(2, corrected.Count);
        Assert.Equal(300m, corrected.Single(c => c.BranchId == Branch).Amount);
        Assert.Equal(120m, corrected.Single(c => c.BranchId == OtherBranch).Amount);
    }

    [Fact]
    public async Task A_company_with_nothing_to_correct_writes_nothing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await FreshAsync(connection);

        // A sale that gave no change was never affected, and neither was a card sale.
        AddCashSale(db, Branch, tendered: 1000m, change: 0m);
        await db.SaveChangesAsync();

        Assert.Empty(await ChangeRestatement.ApplyAsync(db, new LedgerPoster(db)));
        Assert.Empty(await db.CashMovements.ToListAsync());
    }

    [Fact]
    public async Task A_sale_that_took_no_cash_is_left_alone()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await FreshAsync(connection);

        // Change recorded against a sale with no cash line cannot have come out of a drawer,
        // so correcting for it would invent money rather than restore it.
        var id = Guid.NewGuid();
        db.Sales.Add(new Sale
        {
            Id = id, Reference = "C01-999999", DeviceCode = "C01", BranchId = Branch, LocalSeq = 999999,
            SoldAtUtc = DateTime.UtcNow, ReceivedAtUtc = DateTime.UtcNow,
            GrandTotalTtc = 500m, PaidAmount = 500m, ChangeAmount = 100m,
            PaymentMethod = PaymentMethod.Card, Status = SaleStatus.Completed,
            Payments =
            [
                new SalePayment
                {
                    Id = Guid.NewGuid(), SaleId = id, MethodKey = "card",
                    Behavior = PaymentBehavior.Card, Status = PaymentStatus.Confirmed, Amount = 500m
                }
            ]
        });
        await db.SaveChangesAsync();

        Assert.Empty(await ChangeRestatement.ApplyAsync(db, new LedgerPoster(db)));
    }

    [Fact]
    public async Task The_correction_is_traceable_and_says_what_it_is()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await FreshAsync(connection);

        AddCashSale(db, Branch, tendered: 1000m, change: 400m);
        await db.SaveChangesAsync();
        await ChangeRestatement.ApplyAsync(db, new LedgerPoster(db));

        // A correction nobody can identify later is indistinguishable from an error.
        var entry = await db.LedgerEntries.AsNoTracking()
            .SingleAsync(e => e.SourceType == ChangeRestatement.Source);
        Assert.Contains("twice", entry.Description);
        Assert.Equal(Branch, entry.BranchId);

        var movement = await db.CashMovements.AsNoTracking().SingleAsync();
        Assert.Equal(CashMovementKind.Adjustment, movement.Kind);
        Assert.Equal(ChangeRestatement.Source, movement.SourceType);
    }
}
