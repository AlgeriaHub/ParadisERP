using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Design;

namespace ParadiERP.Migrations.SqlServer;

/// <summary>dotnet ef migrations add Name -p ParadiERP.Migrations.SqlServer -s ParadiERP.Migrations.SqlServer</summary>
public sealed class SqlServerDesignTimeFactory : IDesignTimeDbContextFactory<PosDbContext>
{
    public PosDbContext CreateDbContext(string[] args) => PosDesignTimeFactory.Create(PosDbProviders.SqlServer);
}
