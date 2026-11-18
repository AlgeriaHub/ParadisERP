using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Persistence;

/// <summary>
/// The one place a company database is wired to a provider. EF Core migrations are
/// provider-specific (column types, identity, indexes), so SQL Server keeps its own migration set
/// in its own assembly; SQLite's lives next to <see cref="PosDbContext"/>.
/// The provider is chosen per company at runtime (see TenantOptions), which is why this is an
/// assembly switch on the options rather than a separate DbContext type per provider.
/// </summary>
public static class PosDbProviders
{
    // Two providers, deliberately: SQL Server in production, SQLite for development, CI and the
    // tests. One deployment serves one company (another company gets its own copy), so a third
    // provider would earn nothing and cost one more migration set on every schema change. SQL
    // Server replaced MySQL on 2026-10-04, when the owner chose SmarterASP hosting.
    public const string Sqlite = "Sqlite";
    public const string SqlServer = "SqlServer";

    public const string SqlServerMigrationsAssembly = "ParadisERP.Migrations.SqlServer";

    /// <summary>Canonical provider name, or null when it isn't one this app supports.</summary>
    public static string? Normalize(string? provider) => provider?.Trim().ToLowerInvariant() switch
    {
        "sqlite" => Sqlite,
        "sqlserver" or "mssql" => SqlServer,
        _ => null
    };

    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string provider, string connectionString)
    {
        switch (Normalize(provider))
        {
            case SqlServer:
                // Deliberately no EnableRetryOnFailure: it does not wrap a user transaction, and
                // BeginWriteScopeAsync opens one around every write in this system.
                options.UseSqlServer(connectionString, x => x.MigrationsAssembly(SqlServerMigrationsAssembly));
                break;
            case Sqlite:
                options.UseSqlite(connectionString);
                break;
            default:
                throw new ArgumentException($"Unknown database provider '{provider}'. Use {SqlServer} or {Sqlite}.", nameof(provider));
        }
        return options;
    }
}
