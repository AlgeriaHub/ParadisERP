using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Design;

namespace AlgeriaPos.Migrations.SqlServer;

/// <summary>dotnet ef migrations add Name -p AlgeriaPos.Migrations.SqlServer -s AlgeriaPos.Migrations.SqlServer</summary>
public sealed class SqlServerDesignTimeFactory : IDesignTimeDbContextFactory<PosDbContext>
{
    public PosDbContext CreateDbContext(string[] args) => PosDesignTimeFactory.Create(PosDbProviders.SqlServer);
}
