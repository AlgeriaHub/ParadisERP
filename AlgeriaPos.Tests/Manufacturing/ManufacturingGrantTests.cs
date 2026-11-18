using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Authorization;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Tests.Security;
using Microsoft.EntityFrameworkCore;
using static AlgeriaPos.Tests.Manufacturing.MfgHarness;

namespace AlgeriaPos.Tests.Manufacturing;

/// <summary>
/// Giving a login the manufacturing permissions (audit D36): stored as ordinary additional grants, read
/// by the server's own authorization, and never removed by the users screen's save.
/// </summary>
public class ManufacturingGrantTests
{
    private const string Access = "/api/manufacturing/access/{userId:guid}";

    [Fact]
    public async Task A_grant_is_stored_as_the_user_s_own_permission_and_the_users_screen_leaves_it_alone()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);
        var (manager, _) = await ManagerAsync(s.Db, TillHarness.Oran);
        var before = (await s.Db.Users.AsNoTracking().SingleAsync(u => u.Id == manager.Id)).AuthorizationVersion;

        var row = await OkAsync(s.Db, admin, "PUT", Access,
            """{"keys":["manufacturing.view","manufacturing.bom.manage"]}""", ("userId", manager.Id.ToString()));
        Assert.Equal(["manufacturing.view", "manufacturing.bom.manage"], row.GetProperty("granted").EnumerateArray().Select(k => k.GetString()));
        Assert.Empty(row.GetProperty("fromRole").EnumerateArray());
        // The next request already sees it: the user's authorization version moved with the save.
        Assert.True((await s.Db.Users.AsNoTracking().SingleAsync(u => u.Id == manager.Id)).AuthorizationVersion > before);

        // The users screen saves the person with a legacy grant; the manufacturing grants stay.
        var tracked = await s.Db.Users.SingleAsync(u => u.Id == manager.Id);
        await UserRoleWriter.AssignAsync(s.Db, tracked, UserRole.Manager, Permissions.Store([Permissions.ViewOtherBranchStock]), "admin", CancellationToken.None);
        await s.Db.SaveChangesAsync();
        var keys = await s.Db.UserPermissions.AsNoTracking().Where(p => p.UserId == manager.Id).Select(p => p.PermissionKey).ToListAsync();
        Assert.Equal(["manufacturing.bom.manage", "manufacturing.view", "stock.view_other_branches"], keys.Order(StringComparer.Ordinal));

        // And the users screen's grants survive a manufacturing save.
        await OkAsync(s.Db, admin, "PUT", Access, """{"keys":[]}""", ("userId", manager.Id.ToString()));
        keys = await s.Db.UserPermissions.AsNoTracking().Where(p => p.UserId == manager.Id).Select(p => p.PermissionKey).ToListAsync();
        Assert.Equal(["stock.view_other_branches"], keys);
    }

    [Fact]
    public async Task Only_manufacturing_keys_are_granted_here_and_an_admin_s_role_already_holds_them()
    {
        await using var s = await NewAsync();
        var (adminUser, admin) = await AdminAsync(s.Db);
        await RefusedAsync(s.Db, admin, 400, "unknown_permission", "PUT", Access,
            """{"keys":["users.manage"]}""", ("userId", adminUser.Id.ToString()));

        var row = await OkAsync(s.Db, admin, "PUT", Access, """{"keys":["manufacturing.view"]}""", ("userId", adminUser.Id.ToString()));
        Assert.Equal(Capabilities.Manufacturing, row.GetProperty("fromRole").EnumerateArray().Select(k => k.GetString()!));
        Assert.Empty(row.GetProperty("granted").EnumerateArray());
        Assert.False(await s.Db.UserPermissions.AnyAsync(p => p.UserId == adminUser.Id));
    }

    // ---------- delegation (hardening review, 2026-10-26) ----------

    /// <summary>
    /// A branch manager whose custom role would carry users.manage — no system role does today but the
    /// Admin's — with only manufacturing.view of the five.
    /// </summary>
    private static Task<(User User, System.Security.Claims.ClaimsPrincipal Principal)> DelegateAsync(PosDbContext db) =>
        ManagerAsync(db, TillHarness.Oran, PermissionCatalog.UsersManage, PermissionCatalog.ManufacturingView);

    private static async Task<List<string>> GrantsOfAsync(PosDbContext db, Guid userId) =>
        (await db.UserPermissions.AsNoTracking().Where(p => p.UserId == userId).Select(p => p.PermissionKey).ToListAsync())
            .Order(StringComparer.Ordinal).ToList();

    [Fact]
    public async Task A_caller_gives_or_takes_away_only_what_they_hold_themselves()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);
        var (me, caller) = await DelegateAsync(s.Db);
        var (colleague, _) = await ManagerAsync(s.Db, TillHarness.Oran);
        var them = ("userId", colleague.Id.ToString());

        // What they hold, they may pass on.
        await OkAsync(s.Db, caller, "PUT", Access, """{"keys":["manufacturing.view"]}""", them);
        Assert.Equal(["manufacturing.view"], await GrantsOfAsync(s.Db, colleague.Id));

        // Beyond their own authority — to a colleague or to themselves — nothing is stored.
        await RefusedAsync(s.Db, caller, 403, "grant_beyond_own", "PUT", Access,
            """{"keys":["manufacturing.view","manufacturing.bom.approve"]}""", them);
        await RefusedAsync(s.Db, caller, 403, "grant_beyond_own", "PUT", Access,
            """{"keys":["manufacturing.bom.approve"]}""", ("userId", me.Id.ToString()));
        Assert.Equal(["manufacturing.view"], await GrantsOfAsync(s.Db, colleague.Id));
        Assert.DoesNotContain(PermissionCatalog.ManufacturingBomApprove, await GrantsOfAsync(s.Db, me.Id));

        // Nor may they take away what the Admin gave and they do not hold — but they may keep it while
        // changing what they do hold.
        await OkAsync(s.Db, admin, "PUT", Access, """{"keys":["manufacturing.view","manufacturing.bom.approve"]}""", them);
        await RefusedAsync(s.Db, caller, 403, "grant_beyond_own", "PUT", Access, """{"keys":[]}""", them);
        await OkAsync(s.Db, caller, "PUT", Access, """{"keys":["manufacturing.bom.approve"]}""", them);
        Assert.Equal(["manufacturing.bom.approve"], await GrantsOfAsync(s.Db, colleague.Id));

        // And this page never hands out another module's permission, legacy ones included.
        await RefusedAsync(s.Db, admin, 400, "unknown_permission", "PUT", Access, """{"keys":["treasury.cash_box"]}""", them);
        await RefusedAsync(s.Db, admin, 400, "unknown_permission", "PUT", Access, """{"keys":["Manufacturing.View"]}""", them);
    }

    [Fact]
    public async Task A_caller_without_company_scope_reaches_only_their_own_branch_and_no_company_wide_account()
    {
        await using var s = await NewAsync();
        var (_, caller) = await DelegateAsync(s.Db);
        var (colleague, _) = await ManagerAsync(s.Db, TillHarness.Oran);
        var (elsewhere, _) = await ManagerAsync(s.Db, TillHarness.Algiers);
        var (adminHere, _) = await BranchLockTests.UserAsync(s.Db, UserRole.Admin, TillHarness.Oran);

        var listed = (await OkAsync(s.Db, caller, "GET", "/api/manufacturing/access")).EnumerateArray()
            .Select(r => r.GetProperty("userId").GetGuid()).ToList();
        Assert.Contains(colleague.Id, listed);
        Assert.DoesNotContain(elsewhere.Id, listed);
        Assert.DoesNotContain(adminHere.Id, listed);

        await RefusedAsync(s.Db, caller, 404, "not_found", "PUT", Access, """{"keys":["manufacturing.view"]}""", ("userId", elsewhere.Id.ToString()));
        await RefusedAsync(s.Db, caller, 404, "not_found", "PUT", Access, """{"keys":[]}""", ("userId", adminHere.Id.ToString()));
        Assert.Empty(await GrantsOfAsync(s.Db, elsewhere.Id));
    }

    [Fact]
    public async Task The_users_screen_cannot_grant_a_manufacturing_permission_and_keeps_every_legacy_one()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);
        var (manager, _) = await ManagerAsync(s.Db, TillHarness.Oran);
        await OkAsync(s.Db, admin, "PUT", Access, """{"keys":["manufacturing.bom.manage"]}""", ("userId", manager.Id.ToString()));

        // The users screen sends legacy names only; a manufacturing key smuggled into that list gives nothing.
        var tracked = await s.Db.Users.SingleAsync(u => u.Id == manager.Id);
        await UserRoleWriter.AssignAsync(s.Db, tracked, UserRole.Manager,
            "manufacturing.bom.approve," + Permissions.Store([Permissions.ViewOtherBranchStock, Permissions.ManagePayroll]), "admin", CancellationToken.None);
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        Assert.Equal(["manufacturing.bom.manage", "payroll.manage", "stock.view_other_branches"], await GrantsOfAsync(s.Db, manager.Id));

        // A manufacturing save changes none of the legacy grants.
        await OkAsync(s.Db, admin, "PUT", Access, """{"keys":["manufacturing.view"]}""", ("userId", manager.Id.ToString()));
        Assert.Equal(["manufacturing.view", "payroll.manage", "stock.view_other_branches"], await GrantsOfAsync(s.Db, manager.Id));
    }

    [Fact]
    public async Task A_switched_off_role_gives_nothing_so_the_grant_is_stored()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);
        var (user, _) = await ManagerAsync(s.Db, TillHarness.Oran);
        var workshop = new Role { Name = "Atelier", Scope = RoleScope.AssignedBranch, IsActive = false };
        s.Db.Roles.Add(workshop);
        s.Db.RolePermissions.Add(new RolePermission { RoleId = workshop.Id, PermissionKey = PermissionCatalog.ManufacturingView });
        (await s.Db.Users.SingleAsync(u => u.Id == user.Id)).RoleId = workshop.Id;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        var row = await OkAsync(s.Db, admin, "PUT", Access, """{"keys":["manufacturing.view"]}""", ("userId", user.Id.ToString()));
        Assert.Empty(row.GetProperty("fromRole").EnumerateArray());
        Assert.Equal(["manufacturing.view"], await GrantsOfAsync(s.Db, user.Id));
    }

    [Fact]
    public void The_screens_learn_the_manufacturing_permissions_from_the_server_s_decision()
    {
        var granted = ServerAuthorization.LegacyCapabilities(new HashSet<string> { PermissionCatalog.ManufacturingView, PermissionCatalog.SalesCreate });
        Assert.Contains(Capabilities.ManufacturingView, granted);
        Assert.DoesNotContain(Capabilities.ManufacturingSettings, granted);
        Assert.Equal(Capabilities.Manufacturing, Capabilities.For(UserRole.Admin).Where(Capabilities.Manufacturing.Contains));
        Assert.DoesNotContain(Capabilities.ManufacturingView, Capabilities.For(UserRole.Manager));
    }
}
