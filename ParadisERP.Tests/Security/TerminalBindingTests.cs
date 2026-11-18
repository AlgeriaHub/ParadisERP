using ParadisERP.Domain;
using ParadisERP.Infrastructure.Auth;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Security;

/// <summary>
/// Branch scoping without terminal binding is a speed bump (docs/07 §2.2): a cashier who types
/// another branch's till code is, for every scoped route and for push, that branch's terminal.
/// So a cashier may be bound to one branch, and the login and SSO handlers refuse a session on
/// any other branch's device with 403 <c>wrong_terminal</c>.
///
/// <para>
/// The refusal is one pure rule, <see cref="BranchScope.RefusesTerminal"/>, shared by both
/// handlers, so it is pinned here without an HTTP host; the status-code tests the plan names
/// (LoginTerminalBindingTests) wait for backlog item 4.
/// </para>
/// </summary>
public class TerminalBindingTests
{
    private static readonly Device AlgiersTill = new() { Code = "C01", BranchId = TillHarness.Algiers };
    private static readonly Device OranTill = new() { Code = "C02", BranchId = TillHarness.Oran };

    [Fact]
    public void A_cashier_assigned_to_Oran_cannot_open_a_session_on_an_Algiers_till()
    {
        var cashier = new User { Role = UserRole.Cashier, BranchId = TillHarness.Oran };

        Assert.True(BranchScope.RefusesTerminal(cashier, RoleScope.TillBranch, AlgiersTill));
        Assert.False(BranchScope.RefusesTerminal(cashier, RoleScope.TillBranch, OranTill));
    }

    [Fact]
    public void A_cashier_with_no_branch_may_still_use_any_till()
    {
        // Every account that predates the column is in this state: nothing is locked out on
        // upgrade, which is the migration-default lesson applied before the fact.
        var cashier = new User { Role = UserRole.Cashier, BranchId = null };

        Assert.False(BranchScope.RefusesTerminal(cashier, RoleScope.TillBranch, AlgiersTill));
        Assert.False(BranchScope.RefusesTerminal(cashier, RoleScope.TillBranch, OranTill));
    }

    [Fact]
    public void A_branch_manager_opens_a_session_only_on_their_own_branch_tills()
    {
        // Security review, 2026-10-10 (owner-approved): a till session writes into its branch's
        // drawer, stock and receipt numbers, so a manager bound to Oran no longer opens Algiers' till.
        var manager = new User { Role = UserRole.Manager, BranchId = TillHarness.Oran };
        Assert.True(BranchScope.RefusesTerminal(manager, SystemRoles.For(UserRole.Manager).Scope, AlgiersTill));
        Assert.False(BranchScope.RefusesTerminal(manager, SystemRoles.For(UserRole.Manager).Scope, OranTill));
    }

    [Fact]
    public void The_administrator_may_still_open_a_session_on_any_till()
    {
        // Company-wide: the row may still carry a branch from before a role change, and it is ignored.
        var admin = new User { Role = UserRole.Admin, BranchId = TillHarness.Oran };
        Assert.False(BranchScope.RefusesTerminal(admin, SystemRoles.For(UserRole.Admin).Scope, AlgiersTill));
        Assert.False(BranchScope.RefusesTerminal(admin, SystemRoles.For(UserRole.Admin).Scope, OranTill));
    }

    [Fact]
    public async Task Moving_a_cashier_to_another_branch_revokes_the_sessions_they_hold()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var cashier = new User { Username = "c.algiers", FullName = "Cashier", Role = UserRole.Cashier, BranchId = TillHarness.Algiers };
        db.Users.Add(cashier);
        await db.SaveWithVersionsAsync(CancellationToken.None, cashier);
        var tokenVersion = cashier.Version;
        var validator = new UserSessionValidator(db);
        Assert.True(await validator.IsValidAsync(cashier.Id, tokenVersion, nameof(UserRole.Cashier), "C01"));

        // The users tab's PUT goes through SaveWithVersionsAsync, so the move bumps Version and
        // the token the cashier holds at the Algiers till stops validating on its next request.
        cashier.BranchId = TillHarness.Oran;
        await db.SaveWithVersionsAsync(CancellationToken.None, cashier);

        Assert.True(cashier.Version > tokenVersion);
        Assert.False(await validator.IsValidAsync(cashier.Id, tokenVersion, nameof(UserRole.Cashier), "C01"));
    }

    [Fact]
    public async Task A_binding_to_a_branch_that_does_not_exist_cannot_be_written()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;

        // A real foreign key, not a loose guid: a stale id cannot be stored, and the users
        // endpoint checks the branch first so this surfaces as 400, never as this exception.
        db.Users.Add(new User { Username = "ghost", FullName = "Ghost", Role = UserRole.Cashier, BranchId = Guid.NewGuid() });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
