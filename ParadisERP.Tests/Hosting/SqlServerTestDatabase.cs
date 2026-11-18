using Microsoft.Data.SqlClient;

namespace ParadisERP.Tests.Hosting;

/// <summary>
/// A throwaway SQL Server database for the tests that only SQL Server can answer.
/// <para>
/// The suite runs on SQLite, which lets one writer in at a time: two transactions that read a
/// figure, change it in memory and write it back can never interleave there, so a lost update is
/// invisible to every SQLite test. Production is SQL Server, where they can. A test about two
/// writers at once has to run where two writers can actually meet.
/// </para>
/// <para>
/// Set <c>ALGERIAPOS_TEST_SQLSERVER</c> to a connection string without a database
/// (<c>Server=127.0.0.1,14333;User Id=sa;Password=…;TrustServerCertificate=True</c>, the container in
/// <c>deploy/sqlserver</c>). Without it these tests are reported as skipped, never as passed.
/// </para>
/// </summary>
public sealed class SqlServerTestDatabase : IAsyncDisposable
{
    public const string Variable = "ALGERIAPOS_TEST_SQLSERVER";

    private readonly string server;

    /// <summary>The database's own connection string. The API creates and migrates it on start-up.</summary>
    public string ConnectionString { get; }

    public SqlServerTestDatabase()
    {
        server = Environment.GetEnvironmentVariable(Variable)
                 ?? throw new InvalidOperationException($"{Variable} is not set; mark the test [SqlServerFact].");
        ConnectionString = new SqlConnectionStringBuilder(server)
        {
            InitialCatalog = "algeriapos_test_" + Guid.NewGuid().ToString("n"),
        }.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        var name = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;
        // This database's pool only. ClearAllPools is process-wide: the SQL Server tests outside the host
        // collection ran it while a till fleet's host was signing in on its own database, and from 2026-11-02 that
        // fleet's first sign-in hung on CI, every time in the first fleet of the run. The drop below disconnects
        // whatever is still connected here anyway.
        await using (var own = new SqlConnection(ConnectionString)) SqlConnection.ClearPool(own);
        await using var master = new SqlConnection(new SqlConnectionStringBuilder(server) { InitialCatalog = "master" }.ConnectionString);
        await master.OpenAsync();
        await using var drop = master.CreateCommand();
        drop.CommandText = $"""
            IF DB_ID(N'{name}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{name}];
            END
            """;
        await drop.ExecuteNonQueryAsync();
    }
}

/// <summary>A fact that needs SQL Server: skipped, and saying why, when <see cref="SqlServerTestDatabase.Variable"/> is unset.</summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlServerTestDatabase.Variable)))
            Skip = $"Needs SQL Server: set {SqlServerTestDatabase.Variable} (see SqlServerTestDatabase).";
    }
}
