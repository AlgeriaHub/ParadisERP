using ParadiERP.Domain;
using ParadiERP.Infrastructure.Authorization;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Tests.Security;

/// <summary>
/// AUTH-1 Step 1 at the data layer: the system roles and the Admin baseline written and held, every
/// user kept on the role and grants their legacy columns give, dropped grants reported once, and
/// the guards no save can get past — the last active Admin above all.
/// </summary>
public sealed class RoleStoreTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private PosDbContext db = null!;

    private async Task<PosDbContext> OpenAsync()
    {
        await connection.OpenAsync();
        db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private PosDbContext Fresh() => new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);

    public async ValueTask DisposeAsync()
    {
        if (db is not null) await db.DisposeAsync();
        await connection.DisposeAsync();
    }

    private static User Person(string name, UserRole role, string? grants = null, bool active = true) =>
        new() { Username = name, FullName = name, Role = role, Permissions = grants, IsActive = active };

    private async Task<HashSet<string>> GrantsOfAsync(Guid userId)
    {
        await using var read = Fresh();
        return (await read.UserPermissions.Where(p => p.UserId == userId).Select(p => p.PermissionKey).ToListAsync())
            .ToHashSet();
    }

    [Fact]
    public async Task A_first_start_creates_the_three_system_roles_with_their_seeds_and_a_second_changes_nothing()
    {
        await OpenAsync();

        var first = await AuthorizationBootstrap.RunAsync(db);
        var second = await AuthorizationBootstrap.RunAsync(Fresh());

        Assert.Equal(3, first.RolesCreated);
        Assert.False(second.ChangedAnything, second.ToString());
        await using var read = Fresh();
        var counts = await read.RolePermissions.GroupBy(p => p.RoleId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        Assert.Equal(91, counts[SystemRoles.AdminId]);  // + the seven maintenance keys (M0, 2026-11-09); + customers.opening_balance, finance.receipts.settle_own, suppliers.opening_balance, customers.accounts.filter, finance.invoices.untaxed (2026-10-10), inventory.transfer.write_off (2026-10-12), customers.documents.view (2026-10-25), the five manufacturing keys (2026-10-25), the ten Phase 2 manufacturing keys (2026-10-26), manufacturing.costs.view (2026-10-26), manufacturing.costs.snapshot.create and manufacturing.costs.rates.view (B1, 2026-10-29), accounting.close (CLO-01, 2026-11-01), sales.untaxed (2026-11-02)
        Assert.Equal(48, counts[SystemRoles.ManagerId]);
        Assert.Equal(4, counts[SystemRoles.CashierId]);
        Assert.All(await read.Roles.ToListAsync(), r => Assert.True(r.IsSystem && r.IsActive));
    }

    [Fact]
    public async Task A_new_user_saved_without_a_role_is_placed_once_by_the_migration_rule()
    {
        await OpenAsync();
        var keeper = Person("caisse-x", UserRole.Cashier, "treasury.cash_box,payroll.manage");
        var manager = Person("gerant", UserRole.Manager, "payroll.manage,treasury.cash_box");
        db.Users.AddRange(keeper, manager); // no bootstrap first: the save creates the roles it needs
        await db.SaveChangesAsync();

        Assert.Equal(SystemRoles.CashierId, keeper.RoleId);
        Assert.Equal(["treasury.cash_box"], await GrantsOfAsync(keeper.Id));
        Assert.Equal(["payroll.manage"], await GrantsOfAsync(manager.Id));
        await using var read = Fresh();
        var report = await read.AuthorizationReport.OrderBy(r => r.Username).Select(r => $"{r.Username}:{r.PermissionKey}:{r.Reason}").ToListAsync();
        Assert.Equal(["caisse-x:payroll.manage:role-gate", "gerant:treasury.cash_box:manager-by-role"], report);

        // After that the legacy columns never write into the tables (AUTH-1 Step 2): the grants
        // column moves nothing, and the legacy role alone is refused.
        keeper.Permissions = "treasury.cash_box,payroll.manage,stock.view_other_branches";
        await db.SaveChangesAsync();
        Assert.Equal(["treasury.cash_box"], await GrantsOfAsync(keeper.Id));
        keeper.Role = UserRole.Manager;
        await Assert.ThrowsAsync<AuthorizationIntegrityException>(() => db.SaveChangesAsync());
        keeper.Role = UserRole.Cashier;

        // Moving the role is what moves a user; the legacy role follows it, for rollback only.
        keeper.RoleId = SystemRoles.ManagerId;
        await db.SaveChangesAsync();
        await using var after = Fresh();
        Assert.Equal(UserRole.Manager, (await after.Users.SingleAsync(u => u.Id == keeper.Id)).Role);
    }

    [Fact]
    public async Task The_last_active_Admin_can_be_neither_demoted_nor_deactivated_nor_deleted()
    {
        await OpenAsync();
        var admin = Person("admin", UserRole.Admin);
        var manager = Person("gerant", UserRole.Manager);
        db.Users.AddRange(admin, manager);
        await db.SaveChangesAsync();

        foreach (var change in new Action<User>[]
                 {
                     u => u.RoleId = SystemRoles.ManagerId,
                     u => u.IsActive = false,
                     u => { u.IsDeleted = true; u.IsActive = false; },
                 })
        {
            await using var attempt = Fresh();
            var row = await attempt.Users.SingleAsync(u => u.Id == admin.Id);
            change(row);
            await Assert.ThrowsAsync<AuthorizationIntegrityException>(() => attempt.SaveChangesAsync());
        }
        await using (var attempt = Fresh())
        {
            attempt.Users.Remove(await attempt.Users.SingleAsync(u => u.Id == admin.Id));
            await Assert.ThrowsAsync<AuthorizationIntegrityException>(() => attempt.SaveChangesAsync());
        }
        // Handing over in one save is fine: another Admin stays.
        await using (var handover = Fresh())
        {
            (await handover.Users.SingleAsync(u => u.Id == manager.Id)).RoleId = SystemRoles.AdminId;
            (await handover.Users.SingleAsync(u => u.Id == admin.Id)).RoleId = SystemRoles.ManagerId;
            await handover.SaveChangesAsync();
        }
        Assert.Equal(SystemRoles.AdminId, (await Fresh().Users.SingleAsync(u => u.Id == manager.Id)).RoleId);
    }

    [Fact]
    public async Task Inactive_and_deleted_Admins_never_count_toward_the_last_active_Admin()
    {
        await OpenAsync();
        var active = Person("admin", UserRole.Admin);
        var inactive = Person("admin-off", UserRole.Admin, active: false);
        var gone = Person("admin-gone", UserRole.Admin);
        gone.IsDeleted = true;
        gone.IsActive = false;
        db.Users.AddRange(active, inactive, gone);
        await db.SaveChangesAsync();
        Assert.All(new[] { active, inactive, gone }, u => Assert.Equal(SystemRoles.AdminId, u.RoleId));

        // Two more Admin rows exist, but neither is active: the one that is cannot go.
        foreach (var change in new Action<User>[] { u => u.IsActive = false, u => u.RoleId = SystemRoles.CashierId })
        {
            await using var attempt = Fresh();
            change(await attempt.Users.SingleAsync(u => u.Id == active.Id));
            var refused = await Assert.ThrowsAsync<AuthorizationIntegrityException>(() => attempt.SaveChangesAsync());
            Assert.Contains("active administrator", refused.Message);
        }

        // Reactivating the inactive one makes the first free to go.
        await using (var change = Fresh())
        {
            (await change.Users.SingleAsync(u => u.Id == inactive.Id)).IsActive = true;
            await change.SaveChangesAsync();
        }
        await using (var change = Fresh())
        {
            (await change.Users.SingleAsync(u => u.Id == active.Id)).IsActive = false;
            await change.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task System_roles_keep_their_identity_and_the_Admin_role_its_baseline()
    {
        await OpenAsync();
        await AuthorizationBootstrap.RunAsync(db);

        async Task RefusedAsync(Action<PosDbContext, Role, Role> change)
        {
            await using var attempt = Fresh();
            var admin = await attempt.Roles.SingleAsync(r => r.Id == SystemRoles.AdminId);
            var cashier = await attempt.Roles.SingleAsync(r => r.Id == SystemRoles.CashierId);
            change(attempt, admin, cashier);
            await Assert.ThrowsAsync<AuthorizationIntegrityException>(() => attempt.SaveChangesAsync());
        }

        await RefusedAsync((c, _, cashier) => c.Roles.Remove(cashier));
        await RefusedAsync((_, admin, _) => admin.IsActive = false);
        await RefusedAsync((_, _, cashier) => cashier.Scope = RoleScope.Company);
        await RefusedAsync((_, _, cashier) => cashier.Key = "caisse");
        await RefusedAsync((c, _, _) => c.RolePermissions.Add(new RolePermission { RoleId = SystemRoles.AdminId, PermissionKey = PermissionCatalog.HrAttendance }));
        await RefusedAsync((c, _, _) => c.RolePermissions.Remove(c.RolePermissions.Single(p => p.RoleId == SystemRoles.AdminId && p.PermissionKey == PermissionCatalog.UsersManage)));
        await RefusedAsync((c, _, _) => c.RolePermissions.Add(new RolePermission { RoleId = SystemRoles.CashierId, PermissionKey = "sales.everything" }));

        // The Manager and Cashier roles' permissions are editable; a custom role is an ordinary row.
        await using var edit = Fresh();
        edit.RolePermissions.Add(new RolePermission { RoleId = SystemRoles.CashierId, PermissionKey = PermissionCatalog.CustomersView });
        var keeper = new Role { Name = "Store keeper", Scope = RoleScope.AssignedBranch };
        edit.Roles.Add(keeper);
        edit.RolePermissions.Add(new RolePermission { RoleId = keeper.Id, PermissionKey = PermissionCatalog.InventoryReceive });
        await edit.SaveChangesAsync();
        edit.Roles.Remove(keeper);
        await edit.SaveChangesAsync();
    }

    [Fact]
    public async Task A_drifted_Admin_baseline_is_put_back_at_start_up_and_counted()
    {
        await OpenAsync();
        await AuthorizationBootstrap.RunAsync(db);
        // A hand edit of the database, which no save allows.
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM RolePermissions WHERE RoleId = {0} AND PermissionKey = 'users.manage'", SystemRoles.AdminId);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO RolePermissions (RoleId, PermissionKey) VALUES ({0}, 'hr.attendance')", SystemRoles.AdminId);

        var repaired = await AuthorizationBootstrap.RunAsync(Fresh());

        Assert.Equal((1, 1), (repaired.AdminPermissionsAdded, repaired.AdminPermissionsRemoved));
        var held = await Fresh().RolePermissions.Where(p => p.RoleId == SystemRoles.AdminId).Select(p => p.PermissionKey).ToListAsync();
        Assert.Equal(AdminBaseline.Included.Order(StringComparer.Ordinal), held.Order(StringComparer.Ordinal));
        Assert.False((await AuthorizationBootstrap.RunAsync(Fresh())).ChangedAnything);
    }
}
