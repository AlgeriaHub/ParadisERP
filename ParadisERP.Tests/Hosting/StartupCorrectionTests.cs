using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ParadisERP.Tests.Hosting;

/// <summary>
/// A start-up correction that refuses a record is logged and leaves the books as they were; it
/// never keeps the site down. The daily-pricing restatement throws on any document it cannot
/// trace, and before this guard one such record stopped the API booting on every restart.
/// </summary>
[Collection(HostCollection.Name)]
public class StartupCorrectionTests
{
    [Fact]
    public async Task A_restatement_that_refuses_a_record_is_logged_and_the_site_still_starts()
    {
        var root = Path.Combine(Path.GetTempPath(), "algeriapos-host", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path.Combine(root, "database"));
        var connection = "Data Source=" + Path.Combine(root, "database", "pos.db");

        // A queued contract that no longer exists: the restatement refuses it outright.
        var missing = Guid.NewGuid();
        await using (var db = Open(connection))
        {
            await db.Database.MigrateAsync();
            db.RentalDailyPricingCorrections.Add(new RentalDailyPricingCorrection { ContractId = missing });
            await db.SaveChangesAsync();
        }
        SqliteConnection.ClearAllPools();

        using var host = new PosHost(root: root);
        using var client = host.CreateClient();
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

        // Still queued, untouched: the next start tries again, and nothing half-done was kept.
        await using var after = Open(connection);
        var audit = await after.RentalDailyPricingCorrections.AsNoTracking().SingleAsync(a => a.ContractId == missing);
        Assert.Null(audit.CompletedAtUtc);
        Assert.Null(audit.BeforeJson);
    }

    [Fact]
    public async Task What_a_failed_correction_changed_in_memory_is_not_saved_by_the_next_step()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var branch = new Branch { NameFr = "Alger" };
        db.Branches.Add(branch);
        await db.SaveChangesAsync();

        var logger = new ListLogger();
        var result = await StartupCorrection.RunAsync<int>(db, logger, "test", "default", async () =>
        {
            (await db.Branches.SingleAsync()).NameFr = "half a correction";
            throw new InvalidOperationException("cannot trace record 42");
        });

        Assert.Equal(0, result);
        Assert.Contains(logger.Errors, e => e.Contains("'test'") && e.Contains("default"));

        // The step after it saves; the abandoned change must not ride along.
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal("Alger", (await db.Branches.SingleAsync()).NameFr);
    }

    private static PosDbContext Open(string connection) =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);

    private sealed class ListLogger : ILogger
    {
        public List<string> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) Errors.Add(formatter(state, exception));
        }
    }
}
