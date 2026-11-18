using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ParadiERP.Infrastructure.Persistence;

/// <summary>
/// Migrations do not run inside an HTTP request or select a live company, so the tools build
/// the context here instead of from the API host. This one serves the SQLite set; the
/// SQL Server migrations project has its own factory calling <see cref="Create"/>, because EF
/// only discovers factories in the startup/target assembly.
/// Generating a migration never connects, so the connection strings below are placeholders.
/// </summary>
public sealed class PosDesignTimeFactory : IDesignTimeDbContextFactory<PosDbContext>
{
    public PosDbContext CreateDbContext(string[] args) => Create(PosDbProviders.Sqlite);

    public static PosDbContext Create(string provider)
    {
        var connectionString = provider switch
        {
            PosDbProviders.SqlServer => "Server=localhost;Database=pos_design;User Id=design;Password=design;TrustServerCertificate=True",
            _ => "Data Source=pos-design.db"
        };

        var options = new DbContextOptionsBuilder<PosDbContext>();
        PosDbProviders.Configure(options, provider, connectionString);
        return new PosDbContext(options.Options);
    }
}
