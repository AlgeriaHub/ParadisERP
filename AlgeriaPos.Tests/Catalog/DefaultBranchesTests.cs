using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Authorization;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Seeding;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlgeriaPos.Tests.Catalog;

/// <summary>
/// Every start makes sure the company has a primary branch and a primary warehouse (owner,
/// 2026-10-05), preferring to flag one of the right kind over creating one, and repairs the shape
/// every company had before: one branch — the warehouse — holding the tills and the central safe.
/// Stock is never touched. The CompanyBranchKinds migration is checked on a real older database.
/// </summary>
public class DefaultBranchesTests : IAsyncLifetime
{
    private SqliteConnection connection = null!;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
    }

    public async Task DisposeAsync() => await connection.DisposeAsync();

    private PosDbContext Open() => new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);

    private async Task<PosDbContext> FreshAsync()
    {
        var db = Open();
        await db.Database.MigrateAsync();
        if (!await db.SyncCounters.AnyAsync()) db.SyncCounters.Add(new SyncCounter());
        await db.SaveChangesAsync();
        await AuthorizationBootstrap.RunAsync(db);
        return db;
    }

    [Fact]
    public async Task A_live_shaped_company_gets_its_primary_branch_and_its_warehouse_keeps_its_stock_and_id()
    {
        Guid warehouseId, stockId, tillId, cashierId, managerId, safeId;
        await using (var db = await FreshAsync())
        {
            // Exactly the live site after CompanyBranchKinds: "Principal" (now the primary warehouse)
            // holding the counted stock, the first till, a cashier and the never-used central safe.
            var warehouse = new Branch { NameFr = "Principal", NameEn = "Main", IsMain = true, IsWarehouse = true, IsActive = true };
            db.Branches.Add(warehouse);
            var product = new Product { Sku = "ART-0001", NameAr = "ارجل جالفا", Price = 0 };
            db.Products.Add(product);
            var stock = new ProductStock { ProductId = product.Id, BranchId = warehouse.Id, StockQty = 972 };
            db.ProductStocks.Add(stock);
            var till = new Device { Code = "C01", Name = "Main terminal", BranchId = warehouse.Id };
            db.Devices.Add(till);
            var cashier = new User { Username = "caisse", FullName = "Caisse", PasswordHash = "x", RoleId = SystemRoles.CashierId, BranchId = warehouse.Id };
            var manager = new User { Username = "stock", FullName = "Magasinier", PasswordHash = "x", RoleId = SystemRoles.ManagerId, BranchId = warehouse.Id };
            db.Users.AddRange(cashier, manager);
            var safe = new CashBox { BranchId = warehouse.Id, NameAr = "الخزينة المركزية", NameFr = "Caisse centrale", NameEn = "Central safe", IsMain = true };
            db.CashBoxes.Add(safe);
            await db.SaveChangesAsync();
            (warehouseId, stockId, tillId, cashierId, managerId, safeId) = (warehouse.Id, stock.Id, till.Id, cashier.Id, manager.Id, safe.Id);
        }

        await using (var db = Open())
        {
            var result = await DefaultBranches.EnsureAsync(db);
            Assert.Equal(DefaultBranches.Created, result.MainBranch);
            Assert.Equal(DefaultBranches.Kept, result.MainWarehouse);
            Assert.True(result.RenamedWarehouse);
            Assert.Equal(1, result.TillsMoved);
            Assert.Equal(1, result.CashiersMoved);
            Assert.Equal(1, result.CashBoxesMoved);
        }

        await using (var db = Open())
        {
            var warehouse = await db.Branches.SingleAsync(b => b.Id == warehouseId);
            Assert.True(warehouse.IsWarehouse && warehouse.IsMain);
            Assert.Equal(DefaultBranches.WarehouseNameAr, warehouse.NameAr);
            Assert.Equal(DefaultBranches.WarehouseNameFr, warehouse.NameFr);
            var main = await db.Branches.SingleAsync(b => b.IsMain && !b.IsWarehouse);
            Assert.Equal(DefaultBranches.MainNameFr, main.NameFr);

            var stock = await db.ProductStocks.SingleAsync(s => s.Id == stockId);
            Assert.Equal(warehouseId, stock.BranchId);
            Assert.Equal(972m, stock.StockQty);

            Assert.Equal(main.Id, (await db.Devices.SingleAsync(d => d.Id == tillId)).BranchId);
            Assert.Equal(main.Id, (await db.Users.SingleAsync(u => u.Id == cashierId)).BranchId);
            Assert.Equal(warehouseId, (await db.Users.SingleAsync(u => u.Id == managerId)).BranchId);

            var safe = await db.CashBoxes.SingleAsync(c => c.Id == safeId);
            Assert.Equal(main.Id, safe.BranchId);
            Assert.True(safe.IsMain && safe.IsActive);
        }

        // Idempotent: a second start finds both and writes nothing.
        await using (var db = Open())
            Assert.False((await DefaultBranches.EnsureAsync(db)).ChangedAnything);
        await using (var db = Open())
        {
            await CoreDataSeeder.EnsureCashBoxesAsync(db, default);
            Assert.Single(await db.CashBoxes.ToListAsync());
        }
    }

    [Fact]
    public async Task An_existing_place_of_the_right_kind_is_flagged_before_one_is_created_and_a_given_name_is_kept()
    {
        await using (var db = await FreshAsync())
        {
            db.Branches.Add(new Branch { NameFr = "Dépôt Skikda", IsWarehouse = true, IsActive = true });
            db.Branches.Add(new Branch { NameFr = "Oran", IsActive = true });
            await db.SaveChangesAsync();
        }
        await using (var db = Open())
        {
            var result = await DefaultBranches.EnsureAsync(db);
            Assert.Equal(DefaultBranches.Flagged, result.MainBranch);
            Assert.Equal(DefaultBranches.Flagged, result.MainWarehouse);
            Assert.False(result.RenamedWarehouse);
        }
        await using (var db = Open())
        {
            Assert.Equal(2, await db.Branches.CountAsync());
            Assert.Equal("Oran", (await db.Branches.SingleAsync(b => b.IsMain && !b.IsWarehouse)).NameFr);
            Assert.Equal("Dépôt Skikda", (await db.Branches.SingleAsync(b => b.IsMain && b.IsWarehouse)).NameFr);
        }
    }

    [Fact]
    public async Task A_company_with_only_a_branch_gets_its_warehouse_and_nothing_else_moves()
    {
        Guid branchId;
        await using (var db = await FreshAsync())
        {
            var branch = new Branch { NameFr = "Alger-Centre", IsMain = true, IsActive = true };
            db.Branches.Add(branch);
            db.Devices.Add(new Device { Code = "C01", Name = "Caisse 1", BranchId = branch.Id });
            await db.SaveChangesAsync();
            branchId = branch.Id;
        }
        await using (var db = Open())
        {
            var result = await DefaultBranches.EnsureAsync(db);
            Assert.Equal(DefaultBranches.Kept, result.MainBranch);
            Assert.Equal(DefaultBranches.Created, result.MainWarehouse);
            Assert.Equal(0, result.TillsMoved);
        }
        await using (var db = Open())
        {
            Assert.Equal(DefaultBranches.WarehouseNameFr, (await db.Branches.SingleAsync(b => b.IsMain && b.IsWarehouse)).NameFr);
            Assert.Equal(branchId, (await db.Devices.SingleAsync()).BranchId);
        }
    }

    [Fact]
    public async Task A_warehouse_cash_box_with_movements_keeps_them_and_stops_being_the_central_safe()
    {
        Guid boxId;
        await using (var db = await FreshAsync())
        {
            var warehouse = new Branch { NameFr = "Dépôt", IsMain = true, IsWarehouse = true, IsActive = true };
            db.Branches.Add(warehouse);
            var box = new CashBox { BranchId = warehouse.Id, NameFr = "Caisse centrale", IsMain = true };
            db.CashBoxes.Add(box);
            db.CashMovements.Add(new CashMovement { CashBoxId = box.Id, Direction = CashDirection.In, Amount = 5000, BalanceAfter = 5000 });
            await db.SaveChangesAsync();
            boxId = box.Id;
        }
        await using (var db = Open())
        {
            var result = await DefaultBranches.EnsureAsync(db);
            Assert.Equal(1, result.CashBoxesKept);
            await CoreDataSeeder.EnsureCashBoxesAsync(db, default);
        }
        await using (var db = Open())
        {
            var kept = await db.CashBoxes.SingleAsync(c => c.Id == boxId);
            Assert.False(kept.IsMain);
            Assert.True(kept.IsActive);
            Assert.Single(await db.CashMovements.Where(m => m.CashBoxId == boxId).ToListAsync());
            var main = await db.Branches.SingleAsync(b => b.IsMain && !b.IsWarehouse);
            Assert.True((await db.CashBoxes.SingleAsync(c => c.BranchId == main.Id)).IsMain);
        }
    }

    // ---------- the live row, through the real migration and the real start ----------

    /// <summary>The live Branches row as the owner read it on 2026-10-05.</summary>
    private static readonly Guid LiveId = Guid.Parse("F4D9F3FD-AE3C-4A74-8B68-41B9A81542BA");

    /// <summary>
    /// A row in the schema the database is at, written with raw SQL: every column the caller does not
    /// give gets NULL when it may, else a plain zero value — so the test holds whatever a later
    /// migration adds to the table.
    /// </summary>
    private static async Task InsertAsync(PosDbContext db, string table, Dictionary<string, object?> given)
    {
        var columns = new List<(string Name, string Type, bool NotNull)>();
        await using (var cmd = db.Database.GetDbConnection().CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info('{table}')";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) columns.Add((reader.GetString(1), reader.GetString(2), reader.GetInt32(3) == 1));
        }
        // SQLite keeps decimals, dates and Guids as TEXT; the model says which a column is.
        var entity = db.Model.GetEntityTypes().FirstOrDefault(e => e.GetTableName() == table);
        Type? ClrOf(string column) => entity?.GetProperties().FirstOrDefault(p => p.GetColumnName() == column)?.ClrType;
        var values = new List<object?>();
        foreach (var (name, type, notNull) in columns)
        {
            if (given.TryGetValue(name, out var v)) { values.Add(v); continue; }
            if (!notNull) { values.Add(null); continue; }
            var clr = Nullable.GetUnderlyingType(ClrOf(name) ?? typeof(string)) ?? ClrOf(name) ?? typeof(string);
            values.Add(clr == typeof(decimal) ? 0m
                : clr == typeof(DateTime) ? DateTime.UtcNow
                : clr == typeof(Guid) ? Guid.Empty
                : name == "FiscalIds" ? "{}"
                : type.Equals("TEXT", StringComparison.OrdinalIgnoreCase) ? "" : 0);
        }
        // NULL is written as a literal; every other value is bound, so the provider spells Guids and
        // dates the way it reads them back.
        var bound = new List<object>();
        var slots = new List<string>();
        foreach (var v in values)
        {
            if (v is null) { slots.Add("NULL"); continue; }
            slots.Add("{" + bound.Count + "}");
            bound.Add(v);
        }
        var sql = $"INSERT INTO {table} ({string.Join(", ", columns.Select(c => c.Name))}) VALUES ({string.Join(", ", slots)})";
        await db.Database.ExecuteSqlRawAsync(sql, bound.ToArray());
    }

    /// <summary>
    /// The live company at the release before this one: "Principal" (the owner's exact row) holding
    /// the opening stock, the first till, the never-used central safe and an admin with no branch.
    /// </summary>
    private async Task<(PosDbContext Db, Guid Stock, Guid Movement, Guid Till, Guid Safe)> LiveShapeAsync()
    {
        var db = Open();
        await db.Database.GetService<IMigrator>().MigrateAsync("CashierOwnTill");
        await InsertAsync(db, "SyncCounters", new() { ["Id"] = 1, ["Value"] = 10 });
        await InsertAsync(db, "Branches", new()
        {
            ["Id"] = LiveId, ["Version"] = 1, ["NameAr"] = "المستودع الرئيسي", ["NameFr"] = "Principal", ["NameEn"] = "Main",
            ["IsMain"] = 1, ["IsActive"] = 1, ["IsOpenForOperation"] = 0, ["IsDeleted"] = 0,
        });
        var product = Guid.NewGuid();
        await InsertAsync(db, "Products", new() { ["Id"] = product, ["Version"] = 2, ["Sku"] = "ART-0001", ["NameAr"] = "ارجل جالفا", ["Price"] = "0" });
        var stock = Guid.NewGuid();
        await InsertAsync(db, "ProductStocks", new() { ["Id"] = stock, ["ProductId"] = product, ["BranchId"] = LiveId, ["StockQty"] = "972" });
        var movement = Guid.NewGuid();
        await InsertAsync(db, "StockMovements", new()
        {
            ["Id"] = movement, ["ProductId"] = product, ["BranchId"] = LiveId, ["QtyDelta"] = "972", ["BalanceAfter"] = "972", ["Reason"] = "opening",
        });
        var till = Guid.NewGuid();
        await InsertAsync(db, "Devices", new() { ["Id"] = till, ["Code"] = "C01", ["Name"] = "Main terminal", ["BranchId"] = LiveId, ["IsActive"] = 1 });
        var safe = Guid.NewGuid();
        await InsertAsync(db, "CashBoxes", new()
        {
            ["Id"] = safe, ["BranchId"] = LiveId, ["NameAr"] = "الخزينة المركزية", ["NameFr"] = "Caisse centrale", ["NameEn"] = "Central safe",
            ["IsMain"] = 1, ["IsActive"] = 1,
        });
        return (db, stock, movement, till, safe);
    }

    [Fact]
    public async Task The_live_row_with_no_selling_history_becomes_the_primary_warehouse_named_field_by_field()
    {
        var (db, stock, movement, till, safe) = await LiveShapeAsync();
        await using (db)
        {
            await db.Database.MigrateAsync();
            await DefaultBranches.EnsureAsync(db);
        }

        await using var after = Open();
        var principal = await after.Branches.AsNoTracking().SingleAsync(b => b.Id == LiveId);
        Assert.True(principal.IsWarehouse && principal.IsMain);
        // Each name on its own: the two the seeder wrote are replaced, the Arabic someone typed is kept.
        Assert.Equal("Entrepôt central", principal.NameFr);
        Assert.Equal("Central warehouse", principal.NameEn);
        Assert.Equal("المستودع الرئيسي", principal.NameAr);

        var branch = await after.Branches.AsNoTracking().SingleAsync(b => b.IsMain && !b.IsWarehouse);
        Assert.NotEqual(LiveId, branch.Id);
        // Stock and its opening movement stay where they were counted.
        Assert.Equal(LiveId, (await after.ProductStocks.AsNoTracking().SingleAsync(s => s.Id == stock)).BranchId);
        Assert.Equal(LiveId, (await after.StockMovements.AsNoTracking().SingleAsync(m => m.Id == movement)).BranchId);
        // What a warehouse cannot hold moved to the branch: the till and the unused central safe.
        Assert.Equal(branch.Id, (await after.Devices.AsNoTracking().SingleAsync(d => d.Id == till)).BranchId);
        var box = await after.CashBoxes.AsNoTracking().SingleAsync(c => c.Id == safe);
        Assert.Equal(branch.Id, box.BranchId);
        Assert.True(box.IsMain);
    }

    [Fact]
    public async Task The_live_row_with_a_sale_stays_the_primary_branch_and_a_warehouse_is_created_beside_it()
    {
        var (db, stock, movement, till, safe) = await LiveShapeAsync();
        var sale = Guid.NewGuid();
        await using (db)
        {
            await InsertAsync(db, "Sales", new()
            {
                ["Id"] = sale, ["Reference"] = "C01-000001", ["DeviceCode"] = "C01", ["BranchId"] = LiveId, ["LocalSeq"] = 1,
                ["CashierUserId"] = Guid.NewGuid(), ["SoldAtUtc"] = DateTime.UtcNow, ["GrandTotalTtc"] = "1", ["SubTotalHt"] = "1",
            });
            await db.Database.MigrateAsync();
            await DefaultBranches.EnsureAsync(db);
        }

        await using var after = Open();
        var principal = await after.Branches.AsNoTracking().SingleAsync(b => b.Id == LiveId);
        // It sold, so it stays a branch — the primary one — and its names are left alone.
        Assert.False(principal.IsWarehouse);
        Assert.True(principal.IsMain);
        Assert.Equal("Principal", principal.NameFr);
        Assert.Equal("Main", principal.NameEn);

        var warehouse = await after.Branches.AsNoTracking().SingleAsync(b => b.IsMain && b.IsWarehouse);
        Assert.NotEqual(LiveId, warehouse.Id);
        Assert.Equal(DefaultBranches.WarehouseNameFr, warehouse.NameFr);

        // Nothing historical moved: the sale, the stock, the movement, the till and the safe.
        Assert.Equal(LiveId, (await after.Sales.AsNoTracking().SingleAsync(s => s.Id == sale)).BranchId);
        Assert.Equal(LiveId, (await after.ProductStocks.AsNoTracking().SingleAsync(s => s.Id == stock)).BranchId);
        Assert.Equal(LiveId, (await after.StockMovements.AsNoTracking().SingleAsync(m => m.Id == movement)).BranchId);
        Assert.Equal(LiveId, (await after.Devices.AsNoTracking().SingleAsync(d => d.Id == till)).BranchId);
        var box = await after.CashBoxes.AsNoTracking().SingleAsync(c => c.Id == safe);
        Assert.Equal(LiveId, box.BranchId);
        Assert.True(box.IsMain);
        Assert.Equal(2, await after.Branches.CountAsync());
    }

    [Fact]
    public async Task The_migration_makes_the_old_main_branch_the_primary_warehouse_and_keeps_its_id()
    {
        await using var db = Open();
        await db.Database.GetService<IMigrator>().MigrateAsync("CashierOwnTill");
        var legacy = Guid.NewGuid();
        var deleted = Guid.NewGuid();
        var other = Guid.NewGuid();
        var now = DateTime.UtcNow;
        foreach (var (id, isMain, isDeleted, name) in new[] { (legacy, 1, 0, "Principal"), (deleted, 1, 1, "Ancien"), (other, 0, 0, "Oran") })
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Branches (Id, Version, UpdatedAtUtc, IsDeleted, NameAr, NameFr, NameEn, Address, AddressAr, AddressFr, AddressEn, Phone, IsMain, AllowNegativeStock, IsActive, IsOpenForOperation, FiscalIds) " +
                "VALUES ({0}, 1, {1}, {2}, '', {3}, '', '', '', '', '', '', {4}, 0, 1, 0, '{{}}')",
                id, now, isDeleted, name, isMain);

        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        var rows = await db.Branches.AsNoTracking().ToListAsync();
        var principal = rows.Single(b => b.Id == legacy);
        Assert.True(principal.IsWarehouse && principal.IsMain);
        Assert.False(rows.Single(b => b.Id == deleted).IsMain);
        Assert.False(rows.Single(b => b.Id == other).IsWarehouse);
    }
}
