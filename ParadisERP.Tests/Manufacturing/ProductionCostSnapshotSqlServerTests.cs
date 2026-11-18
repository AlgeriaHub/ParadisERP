using System.Security.Cryptography;
using System.Text;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Infrastructure.Seeding;
using ParadisERP.Tests.Hosting;
using ParadisERP.Tests.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using static ParadisERP.Tests.Manufacturing.MfgHarness;
using static ParadisERP.Tests.Manufacturing.ProductionWorld;

namespace ParadisERP.Tests.Manufacturing;

/// <summary>Provider runtime proof for B1. SQLite cannot exercise SQL Server's concurrent writers or DDL.</summary>
public sealed class ProductionCostSnapshotSqlServerTests
{
    private const string Snapshot = "/api/manufacturing/orders/{id:guid}/cost-snapshot";
    private const string Late = "/api/manufacturing/orders/{id:guid}/cost-snapshot/late-costs";
    private const string BeforeB1 = "20261026055000_ManufacturingPhase2Orders";
    private const string B1 = "20261028154218_ManufacturingCostSnapshotsB1";
    private const string SourceCapture = "20261029132325_ManufacturingCostSnapshotSourceCapture";
    private const string Provenance = "20261029150951_ManufacturingCostSnapshotProvenance";

    [SqlServerFact]
    public async Task Fresh_install_has_the_B1_history_migrations_and_required_schema()
    {
        await using var database = new SqlServerTestDatabase();
        await using var db = Open(database.ConnectionString);
        await db.Database.MigrateAsync();

        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.Contains(B1, applied);
        Assert.Contains(SourceCapture, applied);
        Assert.Contains(Provenance, applied);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        Assert.Equal("decimal(18,3)", await ScalarAsync(connection, "SELECT CONCAT(t.name, '(', c.precision, ',', c.scale, ')') FROM sys.columns c JOIN sys.types t ON c.user_type_id=t.user_type_id WHERE c.object_id=OBJECT_ID(N'dbo.ManufacturingCostSheets') AND c.name=N'TotalCost'"));
        Assert.Equal("False", await ScalarAsync(connection, "SELECT c.is_nullable FROM sys.columns c WHERE c.object_id=OBJECT_ID(N'dbo.ManufacturingCostSheets') AND c.name=N'SourceState'"));
        Assert.Equal("False", await ScalarAsync(connection, "SELECT c.is_nullable FROM sys.columns c WHERE c.object_id=OBJECT_ID(N'dbo.ManufacturingCostSheets') AND c.name=N'SourceSnapshotJson'"));
        Assert.Equal("3", await ScalarAsync(connection, "SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.ManufacturingCostSheets') AND name IN (N'IX_ManufacturingCostSheets_OrderId',N'IX_ManufacturingCostSheets_RequestId',N'IX_ManufacturingCostSheets_LineId')"));
        Assert.Equal("2", await ScalarAsync(connection, "SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.ManufacturingCostSheets')"));
        Assert.Equal("1", await ScalarAsync(connection, "SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.ManufacturingLateCosts') AND name=N'IX_ManufacturingLateCosts_SheetId_Kind_SourceReference' AND is_unique=1"));
    }

    [SqlServerFact]
    public async Task Upgrade_from_pre_B1_schema_applies_all_B1_migrations()
    {
        await using var database = new SqlServerTestDatabase();
        await using var db = Open(database.ConnectionString);
        await db.Database.GetService<IMigrator>().MigrateAsync(BeforeB1);
        Assert.DoesNotContain(B1, await db.Database.GetAppliedMigrationsAsync());

        await db.Database.MigrateAsync();
        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.Contains(B1, applied);
        Assert.Contains(SourceCapture, applied);
        Assert.Contains(Provenance, applied);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [SqlServerFact]
    public async Task B1_operations_are_immutable_scoped_and_concurrency_safe_on_sql_server()
    {
        await using var database = new SqlServerTestDatabase();
        var options = Options(database.ConnectionString);
        await using var seed = new PosDbContext(options);
        await seed.Database.MigrateAsync();
        await SeedHarnessBaselineAsync(seed);
        var world = await SetupAsync(seed);
        var id = await CompletedAsync(seed, world);
        await AddPartialReleasesAsync(seed, id, world.LineA);

        var created = await OkAsync(seed, world.AdminPrincipal, "POST", Snapshot, "{\"requestId\":\"sql-b1-freeze\"}", Of(id));
        var retry = await OkAsync(seed, world.AdminPrincipal, "POST", Snapshot, "{\"requestId\":\"sql-b1-freeze\"}", Of(id));
        Assert.Equal(created.GetProperty("id").GetGuid(), retry.GetProperty("id").GetGuid());
        await RefusedAsync(seed, world.AdminPrincipal, 409, "snapshot_exists", "POST", Snapshot, "{\"requestId\":\"sql-b1-second\"}", Of(id));

        var sheet = await seed.ManufacturingCostSheets.SingleAsync(x => x.OrderId == id);
        var source = sheet.SourceSnapshotJson;
        Assert.Equal(sheet.SourceHash, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))));
        Assert.Equal("complete", created.GetProperty("sourceState").GetString());
        var original = sheet.TotalCost;
        Assert.Equal(original, await seed.ManufacturingReleaseValueSnapshots.Where(x => x.SheetId == sheet.Id).SumAsync(x => x.Value));
        Assert.Equal(2, await seed.ManufacturingReleaseValueSnapshots.CountAsync(x => x.SheetId == sheet.Id));
        var order = await seed.ProductionOrders.SingleAsync(x => x.Id == id);
        (await seed.Products.SingleAsync(x => x.Id == order.ProductId)).CostPrice += 777m;
        await seed.SaveChangesAsync();
        var frozen = await OkAsync(seed, world.AdminPrincipal, "GET", Snapshot, "", Of(id));
        Assert.Equal(original, frozen.GetProperty("totalCost").GetDecimal());

        sheet = await seed.ManufacturingCostSheets.SingleAsync(x => x.OrderId == id);
        sheet.TotalCost += 1m;
        await Assert.ThrowsAsync<InvalidOperationException>(() => seed.SaveChangesAsync());
        seed.ChangeTracker.Clear();
        seed.Remove(await seed.ManufacturingCostSheets.SingleAsync(x => x.OrderId == id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => seed.SaveChangesAsync());
        seed.ChangeTracker.Clear();

        var (_, foreign) = await ManagerAsync(seed, WarehouseB, AllGrants);
        await RefusedAsync(seed, foreign, 404, "not_found", "GET", Snapshot, "", Of(id));
        await RefusedAsync(seed, foreign, 404, "not_found", "POST", Snapshot, "{\"requestId\":\"sql-b1-freeze\"}", Of(id));

        await using var lateA = new PosDbContext(options);
        await using var lateB = new PosDbContext(options);
        const string latePayload = "{\"kind\":\"expense\",\"sourceReference\":\"PV-SQL-RACE\",\"amount\":25,\"reason\":\"late\"}";
        var late = await Task.WhenAll(
            OkAsync(lateA, world.AdminPrincipal, "POST", Late, latePayload, Of(id)),
            OkAsync(lateB, world.AdminPrincipal, "POST", Late, latePayload, Of(id)));
        Assert.Equal(late[0].GetProperty("id").GetGuid(), late[1].GetProperty("id").GetGuid());

        var racedOrder = await CompletedAsync(seed, world);
        await using var first = new PosDbContext(options);
        await using var second = new PosDbContext(options);
        var replies = await Task.WhenAll(
            CallAsync(first, world.AdminPrincipal, "POST", Snapshot, "{\"requestId\":\"sql-b1-race-a\"}", Of(racedOrder)),
            CallAsync(second, world.AdminPrincipal, "POST", Snapshot, "{\"requestId\":\"sql-b1-race-b\"}", Of(racedOrder)));
        Assert.Contains(replies, x => x.Status is >= 200 and < 300);
        Assert.Contains(replies, x => x.Status == 409 && x.Body.Contains("snapshot_exists", StringComparison.Ordinal));
        await using var inspection = new PosDbContext(options);
        Assert.Equal(1, await inspection.ManufacturingCostSheets.CountAsync(x => x.OrderId == racedOrder));
        Assert.Equal(1, await inspection.ManufacturingLateCosts.CountAsync());
    }

    private static DbContextOptions<PosDbContext> Options(string connectionString)
    {
        var options = new DbContextOptionsBuilder<PosDbContext>();
        PosDbProviders.Configure(options, PosDbProviders.SqlServer, connectionString);
        return options.Options;
    }

    private static PosDbContext Open(string connectionString) => new(Options(connectionString));

    private static async Task<Guid> CompletedAsync(PosDbContext db, ProductionWorld world)
    {
        var created = await world.CreateOrderAsync(db, world.LineA, 100m);
        var id = OrderId(created);
        await OkAsync(db, world.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/movements", Movement("Issue", MaterialId(created), 100m), Of(id));
        var order = await db.ProductionOrders.SingleAsync(x => x.Id == id);
        order.Status = ProductionOrderStatus.Completed;
        order.AcceptedBaseQty = 100m;
        order.CompletedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task AddPartialReleasesAsync(PosDbContext db, Guid orderId, Guid lineId)
    {
        var now = DateTime.UtcNow;
        db.OutputReleases.AddRange(
            new OutputRelease { Number = "OR-SQL-001", OrderId = orderId, LineId = lineId, LocationId = WarehouseA, EnteredQty = 40m, UnitId = SystemUnits.Kilogram, Factor = 1m, BaseQty = 40m, UnitCostSnapshot = 400m, ClientRequestId = "sql-release-1", AtUtc = now },
            new OutputRelease { Number = "OR-SQL-002", OrderId = orderId, LineId = lineId, LocationId = WarehouseA, EnteredQty = 60m, UnitId = SystemUnits.Kilogram, Factor = 1m, BaseQty = 60m, UnitCostSnapshot = 400m, ClientRequestId = "sql-release-2", AtUtc = now.AddSeconds(1) });
        await db.SaveChangesAsync();
    }

    private static async Task SeedHarnessBaselineAsync(PosDbContext db)
    {
        db.SyncCounters.Add(new SyncCounter { Id = 1, Value = 0 });
        db.Branches.AddRange(
            new Branch { Id = TillHarness.Algiers, NameFr = "Alger-Centre", IsMain = true, IsOpenForOperation = true },
            new Branch { Id = TillHarness.Oran, NameFr = "Oran", IsOpenForOperation = true },
            new Branch { Id = WarehouseA, NameFr = "Atelier A", IsWarehouse = true, IsOpenForOperation = true },
            new Branch { Id = WarehouseB, NameFr = "Atelier B", IsWarehouse = true, IsOpenForOperation = true },
            new Branch { Id = MainWarehouse, NameFr = "Dépôt central", IsWarehouse = true, IsMain = true, IsOpenForOperation = true });
        db.Devices.AddRange(
            new Device { Code = "C01", BranchId = TillHarness.Algiers },
            new Device { Code = "C02", BranchId = TillHarness.Oran });
        db.Products.Add(new Product { Id = TillHarness.Pipe, Sku = "PIPE-90", NameFr = "Tube 90", Price = 1000m, TrackStock = true });
        db.ProductStocks.AddRange(
            new ProductStock { ProductId = TillHarness.Pipe, BranchId = TillHarness.Algiers, StockQty = 10 },
            new ProductStock { ProductId = TillHarness.Pipe, BranchId = TillHarness.Oran, StockQty = 10 });
        await db.SaveChangesAsync();
        await CoreDataSeeder.EnsureAsync(db);
    }

    private static async Task<string> ScalarAsync(SqlConnection connection, string commandText)
    {
        await using var command = new SqlCommand(commandText, connection);
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }
}
