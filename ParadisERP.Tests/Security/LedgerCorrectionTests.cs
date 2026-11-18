using ParadisERP.Domain;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Infrastructure.Seeding;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Security;

/// <summary>
/// A reversal is the only correction the ledger has, which makes it the only way to move a
/// posted figure — so the question worth asking of it is whether it can be made to happen
/// twice.
///
/// <para>
/// Nothing else in the ledger can: every other posting is refused a second time on its source
/// document, and a reversal is exempt from that check precisely because it shares the source of
/// the entry it cancels. The guard that replaces it lived only in the route. It lives beside the
/// posting now, so it holds wherever a reversal is raised — a replayed correction is not a
/// correction, it is the same amount moved twice, and in a report the second one looks exactly
/// as legitimate as the first.
/// </para>
/// </summary>
public class LedgerCorrectionTests
{
    private static readonly Guid Branch = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static async Task<(SqliteConnection, PosDbContext)> NewDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.SyncCounters.Add(new SyncCounter { Id = 1, Value = 0 });
        db.Branches.Add(new Branch { Id = Branch, NameFr = "Alger-Centre", IsMain = true });
        await db.SaveChangesAsync();
        await CoreDataSeeder.EnsureAsync(db);
        return (connection, db);
    }

    private static PostingDraft Remittance(Guid movementId) =>
        TreasuryPosting.Remittance(movementId, Branch, Branch, "Oran", "Coffre", 250_000m, "REM-1", "Manager");

    [Fact]
    public async Task The_same_entry_cannot_be_reversed_twice()
    {
        var (connection, db) = await NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var poster = new LedgerPoster(db);

        var original = await poster.PostAsync(Remittance(Guid.NewGuid()));
        await db.SaveChangesAsync();
        await poster.ReverseAsync(original!, "Counted wrong", "Manager");
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => poster.ReverseAsync(original!, "Counted wrong", "Manager"));

        // One original, one correction, and 250 000 that moved once.
        Assert.Equal(2, await db.LedgerEntries.CountAsync());
        var lines = await db.LedgerEntryLines.AsNoTracking().ToListAsync();
        Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
        Assert.Equal(500_000m, lines.Sum(l => l.Debit));
    }

    [Fact]
    public async Task A_reversal_is_not_itself_a_document_that_can_be_reversed()
    {
        var (connection, db) = await NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var poster = new LedgerPoster(db);

        var original = await poster.PostAsync(Remittance(Guid.NewGuid()));
        await db.SaveChangesAsync();
        var reversal = await poster.ReverseAsync(original!, "Counted wrong", "Manager");
        await db.SaveChangesAsync();

        // Reversing a reversal would reinstate the original figure by a route that leaves no
        // document behind saying so. The way back is to post the document again.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => poster.ReverseAsync(reversal, "Changed my mind", "Manager"));
        Assert.Equal(2, await db.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task A_document_that_arrives_again_after_being_reversed_still_posts_only_once()
    {
        var (connection, db) = await NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var poster = new LedgerPoster(db);
        var movementId = Guid.NewGuid();

        var original = await poster.PostAsync(Remittance(movementId));
        await db.SaveChangesAsync();
        await poster.ReverseAsync(original!, "Counted wrong", "Manager");
        await db.SaveChangesAsync();

        // A till re-pushing its outbox is ordinary. A correction the back office has already
        // made must not be undone by one, which is what re-posting the source would amount to.
        Assert.Null(await poster.PostAsync(Remittance(movementId)));
        Assert.Equal(2, await db.LedgerEntries.CountAsync());
    }
}
