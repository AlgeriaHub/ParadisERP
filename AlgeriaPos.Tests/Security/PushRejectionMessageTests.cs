using System.Text.RegularExpressions;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlgeriaPos.Tests.Security;

/// <summary>
/// A till never receives the database's own words (docs/07 §2.4). It gets a stable code and a
/// safe sentence; the exception goes to the server log under a reference the till also shows,
/// so an operator can join the two.
/// </summary>
public class PushRejectionMessageTests
{
    private sealed class CapturingLogger : ILogger<SyncService>
    {
        public readonly List<(LogLevel Level, string Message, Exception? Exception)> Entries = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }

    private static SalePush CashSale(string reference, long localSeq) =>
        TillHarness.Sale(reference, localSeq, payments: TillHarness.Tender("cash", 1000m));

    /// <summary>The words a provider puts in its messages and a till must never see.</summary>
    private static readonly string[] DatabaseWords = ["SQLite", "sqlite", "table", "constraint", "UNIQUE", "SqlServer", "Cannot insert duplicate key"];

    [Fact]
    public async Task A_rejected_sale_never_carries_the_database_s_own_words()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var sync = TillHarness.NewService(db);

        // Something the till could never have caused and the server cannot recover from
        // per-sale: the ledger table is gone. The provider's sentence names it; the till's must not.
        await db.Database.ExecuteSqlRawAsync("DROP TABLE LedgerEntries");
        var response = await sync.PushAsync(new PushRequest("C01", [CashSale("T-0001", 1)]), "C01", CancellationToken.None);

        Assert.Equal(1, response.Rejected);
        var item = Assert.Single(response.Results);
        Assert.Equal("rejected", item.Status);
        Assert.Equal(PushRejection.ServerError, item.Code);
        Assert.NotNull(item.Message);
        foreach (var word in DatabaseWords)
            Assert.DoesNotContain(word, item.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("LedgerEntries", item.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_receipt_number_already_used_on_the_till_is_recorded_not_refused()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var sync = TillHarness.NewService(db);

        // Two different sales, same terminal, same receipt number. Since a cashier's till can be
        // open on two computers (2026-10-05), that is no longer a till's mistake it can fix — and
        // the sale is the money taken (CLAUDE.md, "never block the sale"). It is recorded, under a
        // number that keeps the printed one, and the push says so in words.
        await sync.PushAsync(new PushRequest("C01", [CashSale("T-0001", 1)]), "C01", CancellationToken.None);
        var second = CashSale("T-0001", 7);
        var response = await sync.PushAsync(new PushRequest("C01", [second]), "C01", CancellationToken.None);

        var item = Assert.Single(response.Results);
        Assert.Equal("accepted", item.Status);
        Assert.Null(item.Code);
        Assert.Contains("T-0001/R7", item.Message, StringComparison.Ordinal);
        foreach (var word in DatabaseWords)
            Assert.DoesNotContain(word, item.Message!, StringComparison.Ordinal);

        // The sequence was free, so it is kept; only the printed number moved.
        var stored = await db.Sales.AsNoTracking().SingleAsync(s => s.Id == second.Id);
        Assert.Equal(7, stored.LocalSeq);
        Assert.Equal("T-0001/R7", stored.Reference);
    }

    [Fact]
    public void The_duplicate_key_codes_are_the_providers_own_not_a_substring_of_their_prose()
    {
        // SQLite: SQLITE_CONSTRAINT (19) with the UNIQUE extended code. A message that merely
        // mentions "Reference" — the old test — is not a duplicate.
        var unique = new DbUpdateException("x", new SqliteException("UNIQUE constraint failed: Sales.DeviceCode, Sales.Reference", 19, 2067));
        var foreignKey = new DbUpdateException("x", new SqliteException("FOREIGN KEY constraint failed", 19, 787));
        var prose = new DbUpdateException("x", new InvalidOperationException("Reference and LocalSeq are mentioned here"));

        Assert.Equal(PushRejection.DuplicateReceiptNumber, PushRejection.Classify(unique).Code);
        Assert.Equal(PushRejection.ServerError, PushRejection.Classify(foreignKey).Code);
        Assert.Equal(PushRejection.ServerError, PushRejection.Classify(prose).Code);

        // SQL Server: SqlException cannot be constructed from outside the driver, so the two
        // numbers the classifier compares SqlException.Number with — 2601 and 2627, the server's
        // duplicate-key errors — are checked against a live server by provider-smoke.cjs instead.

        Assert.Equal(PushRejection.ConcurrentUpdate, PushRejection.Classify(new DbUpdateConcurrencyException("x")).Code);
    }

    [Fact]
    public async Task A_rejection_is_logged_in_full_under_the_reference_the_till_was_given()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var logger = new CapturingLogger();
        var sync = TillHarness.NewService(db, logger);
        var sale = CashSale("T-0001", 1);

        await db.Database.ExecuteSqlRawAsync("DROP TABLE LedgerEntries");
        var response = await sync.PushAsync(new PushRequest("C01", [sale]), "C01", CancellationToken.None);

        var item = Assert.Single(response.Results);
        var reference = Regex.Match(item.Message!, @"Reference (\S+)\.").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(reference), "the till's message carries no reference to quote to the operator");

        // The log line names the sale, the terminal and the reference, and carries the exception
        // — the provider's sentence lives there, and only there.
        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(reference, entry.Message, StringComparison.Ordinal);
        Assert.Contains(sale.Id.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.Contains("C01", entry.Message, StringComparison.Ordinal);
        Assert.NotNull(entry.Exception);
        Assert.Contains("LedgerEntries", entry.Exception.GetBaseException().Message, StringComparison.Ordinal);

        // Ids only: a SalePush carries payment references and a customer id, and the log line
        // itself carries none of them — the sale is named by its id.
        Assert.DoesNotContain(sale.Reference, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejection_carries_a_stable_code_the_till_can_translate()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var sync = TillHarness.NewService(db);
        string[] known = [PushRejection.DuplicateReceiptNumber, PushRejection.ConcurrentUpdate, PushRejection.ServerError];
        var first = CashSale("T-0001", 1);

        var accepted = await sync.PushAsync(new PushRequest("C01", [first]), "C01", CancellationToken.None);
        // A reused receipt number is recorded, not refused (A_receipt_number_already_used_on_the_till_is_recorded_not_refused).
        var reused = await sync.PushAsync(new PushRequest("C01", [CashSale("T-0001", 2)]), "C01", CancellationToken.None);
        Assert.Equal("accepted", reused.Results[0].Status);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE LedgerEntries");
        var broken = await sync.PushAsync(new PushRequest("C01", [CashSale("T-0002", 3)]), "C01", CancellationToken.None);

        foreach (var item in broken.Results)
        {
            Assert.Equal("rejected", item.Status);
            Assert.Contains(item.Code, known);
            // The sentence still travels, so a till running an older bundle keeps showing one.
            Assert.False(string.IsNullOrWhiteSpace(item.Message));
        }

        // An accepted or already-recorded sale carries no code: neither is a rejection.
        Assert.Equal("accepted", accepted.Results[0].Status);
        Assert.Null(accepted.Results[0].Code);
        var again = await sync.PushAsync(new PushRequest("C01", [first]), "C01", CancellationToken.None);
        Assert.Equal("duplicate", again.Results[0].Status);
        Assert.Null(again.Results[0].Code);
    }
}
