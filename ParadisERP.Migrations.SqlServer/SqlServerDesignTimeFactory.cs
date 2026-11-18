using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Design;

namespace ParadisERP.Migrations.SqlServer;

/// <summary>dotnet ef migrations add Name -p ParadisERP.Migrations.SqlServer -s ParadisERP.Migrations.SqlServer</summary>
public sealed class SqlServerDesignTimeFactory : IDesignTimeDbContextFactory<PosDbContext>
{
    public PosDbContext CreateDbContext(string[] args) => PosDesignTimeFactory.Create(PosDbProviders.SqlServer);
}
