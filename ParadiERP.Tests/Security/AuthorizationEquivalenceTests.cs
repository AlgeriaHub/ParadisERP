using System.Security.Claims;
using ParadiERP.API.Authorization;
using ParadiERP.Application.Security;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Auth;
using ParadiERP.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace ParadiERP.Tests.Security;

/// <summary>
/// AUTH-1's proof (REDESIGN_PLAN.md §17.9): the cutover to the role tables changes nobody's access.
///
/// <para>
/// The <b>old</b> side is <see cref="LegacyAccessSnapshot"/> — every legacy decision, frozen from the
/// live policies just before Step 2 removed them. The <b>new</b> side is the live, converted endpoints,
/// evaluated by ASP.NET Core's authorization service with the production <see cref="PermissionHandler"/>,
/// against a principal carrying the server identity the migration gives that user: their system role
/// and the grants <see cref="LegacyGrants"/> keeps. The branch-scope and self-approval rules are read
/// through the same production helpers the handlers call.
/// </para>
/// <para>
/// That the server identity is really what the tables hold, per request, is shown over HTTP in
/// <see cref="RoleAuthorizationRouteTests"/>.
/// </para>
/// </summary>
public class AuthorizationEquivalenceTests
{
    private static readonly string[] LegacyKeys = Permissions.All;

    private static readonly IAuthorizationService Authorization = new ServiceCollection()
        .AddLogging()
        .AddAuthorization()
        .AddSingleton<IAuthorizationHandler, PermissionHandler>()
        .BuildServiceProvider()
        .GetRequiredService<IAuthorizationService>();

    private static string KeyOf(EndpointAuthorizationTests.Route route, string method) => $"{method} {route.Pattern}";

    private static bool IsPlatform(EndpointAuthorizationTests.Route route) =>
        route.Pattern.StartsWith("/api/platform", StringComparison.Ordinal);

    private static IEnumerable<(EndpointAuthorizationTests.Route Route, string Key)> CompanyRoutes() =>
        EndpointAuthorizationTests.Inventory().Where(r => !IsPlatform(r))
            .SelectMany(r => r.Methods.Select(m => (r, KeyOf(r, m))));

    private static async Task<bool> AllowsAsync(EndpointAuthorizationTests.Route route, ClaimsPrincipal? caller)
    {
        if (route.IsAnonymous) return true;
        var gated = route.Policies.Count > 0 || route.Authorize.Count > 0;
        if (caller is null) return !gated;
        if (route.Policies.Count == 0) return true;
        return (await Authorization.AuthorizeAsync(caller, null, AuthorizationPolicy.Combine(route.Policies))).Succeeded;
    }

    /// <summary>The 96 legacy users, in the order the snapshot was frozen in.</summary>
    internal static IEnumerable<User> EveryLegacyUser()
    {
        foreach (var role in Enum.GetValues<UserRole>())
            for (var mask = 0; mask < 1 << LegacyKeys.Length; mask++)
                yield return new User
                {
                    Id = Guid.NewGuid(), Username = $"{role}-{mask}", Role = role,
                    Permissions = Permissions.Store(LegacyKeys.Where((_, i) => (mask & (1 << i)) != 0)),
                };
    }

    /// <summary>What the migration gives this user, as the server would resolve it from the tables.</summary>
    private static ServerAuthorizationContext Migrated(User user)
    {
        var role = SystemRoles.For(user.Role);
        return new ServerAuthorizationContext(role.Id, role.Scope,
            ServerAuthorization.Combine(role.Permissions, true, LegacyGrants.Map(user.Role, user.Permissions).Kept));
    }

    /// <summary>A signed-in principal: the token's identity, and the server's decision beside it.</summary>
    private static ClaimsPrincipal Principal(User user)
    {
        var token = new ClaimsIdentity(
            [new Claim("sub", user.Id.ToString()), new Claim("name", user.Username), new Claim("role", user.Role.ToString())],
            "Bearer", "name", "role");
        var principal = new ClaimsPrincipal(token);
        principal.AddIdentity(ServerAuthorization.CreateIdentity(Migrated(user)));
        return principal;
    }

    [Fact]
    public void The_plan_names_every_company_route_once_and_only_catalogue_keys()
    {
        var routes = CompanyRoutes().Select(x => x.Key).ToList();
        Assert.Equal(routes.Count, routes.Distinct().Count());
        Assert.Equal(routes.Order(StringComparer.Ordinal), RoutePermissionPlan.ByRoute.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(470, routes.Count);  // + the 83 list page routes (server paging, 2026-11-17); + the nine profile, photo and two-step login routes and the admin two-step reset (2026-11-11); + POST /api/receivables/receipts/{id:guid}/keep (SAL-13, 2026-11-10); + the three MS-04 workshop-list routes (2026-11-10); + GET /api/maintenance/history (M7d, 2026-11-09); + the three customer-advance routes (M7b, 2026-11-09); + the ten fixed-asset routes (REN-12, M7a, 2026-11-09); + GET /api/maintenance/rework-candidates (maintenance M7c, 2026-11-09); + the nine customer-job, pricing, invoice, repair-rate and profit routes (maintenance M5a/M5, 2026-11-09); + GET/PUT /api/maintenance/account-map and the maintenance practice ledger with post and reverse (maintenance M6, 2026-11-09); + the maintenance job cost and two cost reports (maintenance M4, 2026-11-09); + the four maintenance parts and outside-work routes (maintenance M2, 2026-11-09); + the four maintenance hours routes (maintenance M3, 2026-11-09); + the eight maintenance job routes (maintenance M1, 2026-11-09); + GET and PUT /api/maintenance/settings (maintenance M0, 2026-11-09); + the sales-cost switch's three routes (C-01, 2026-11-09); + POST practice-ledger/post and /reverse (Phase D, 2026-11-09); + GET /api/manufacturing/accounting/practice-ledger (Phase C2, 2026-11-09); + GET /api/payment-vouchers/bonus-workers (2026-11-08); + 4 manufacturing costing choices routes (Phase C1, 2026-11-08); + 4 weighted-average shadow routes (P0.4b, 2026-11-08); + POST /api/rentals/contracts/{id:guid}/return-notes/preview; Phase 1/2 manufacturing; Phase A costing; + 3 B1 snapshot routes (2026-10-29); + 7 B2 cost-report routes (2026-10-29); + GET /api/reports/missing-cost (SAL-02, 2026-11-01); + 3 closing routes (CLO-01, 2026-11-01); + 6 roles routes (AUTH-1 Step 5, 2026-11-03).
        var unknown = RoutePermissionPlan.ByRoute.Values.SelectMany(r => r.Keys).Where(k => !PermissionCatalog.Contains(k)).Distinct().ToList();
        Assert.True(unknown.Count == 0, "Not in the catalogue: " + string.Join(", ", unknown));
        Assert.Equal(470 + 6, LegacyAccessSnapshot.Decisions.Count);
    }

    [Fact]
    public async Task Every_role_with_every_combination_of_legacy_grants_and_an_anonymous_caller_reaches_exactly_the_same_routes()
    {
        var users = EveryLegacyUser().ToList();
        Assert.Equal(96, users.Count);
        var principals = users.Select(Principal).ToList();
        var mismatches = new List<string>();

        foreach (var (route, key) in CompanyRoutes())
        {
            var frozen = LegacyAccessSnapshot.Decisions[key];
            for (var i = 0; i < users.Count; i++)
            {
                var now = await AllowsAsync(route, principals[i]);
                if (now != (frozen[i] == '1'))
                    mismatches.Add($"{users[i].Role} [{users[i].Permissions}] {key}: before {(frozen[i] == '1' ? "allowed" : "refused")}, now {(now ? "allowed" : "refused")}");
            }
            var anonymous = await AllowsAsync(route, null);
            if (anonymous != (frozen[96] == '1')) mismatches.Add($"anonymous {key}: before {frozen[96]}, now {anonymous}");
        }
        Assert.True(mismatches.Count == 0, $"{mismatches.Count} differences:\n" + string.Join("\n", mismatches.Take(40)));
    }

    [Fact]
    public async Task The_rules_decided_inside_a_handler_and_the_branch_scope_are_the_same_too()
    {
        var routes = CompanyRoutes().ToDictionary(x => x.Key, x => x.Route);
        var availability = routes.Single(r => r.Key.StartsWith("GET /api/catalog/stock/availability/", StringComparison.Ordinal)).Value;
        var approve = routes["POST /api/payment-vouchers/{id:guid}/approve"];
        var users = EveryLegacyUser().ToList();
        var mismatches = new List<string>();

        async Task CheckAsync(string rule, Func<ClaimsPrincipal, Task<bool>> now)
        {
            var frozen = LegacyAccessSnapshot.Decisions[rule];
            for (var i = 0; i < users.Count; i++)
                if (await now(Principal(users[i])) != (frozen[i] == '1'))
                    mismatches.Add($"{users[i].Role} [{users[i].Permissions}]: {rule}");
        }

        // StockEndpoints: Company scope, or the permission, widens availability to other branches.
        await CheckAsync("rule: other branches' availability", async p => await AllowsAsync(availability, p)
            && (BranchAssignment.IsCompanyScoped(p) || ServerAuthorization.Has(p, PermissionCatalog.StockViewOtherBranches)));
        // PaymentVouchers: one's own voucher needs approve (the route) and approve_own (VoucherActor).
        await CheckAsync("rule: approve one's own voucher", async p => await AllowsAsync(approve, p)
            && ServerAuthorization.Has(p, PermissionCatalog.VouchersApproveOwn));
        // CallersBranch, BranchLock, BranchScope, sync replication: the role's scope, never its name.
        await CheckAsync("rule: branch-locked", p => Task.FromResult(BranchLock.IsLocked(ServerAuthorization.ScopeOf(p))));
        await CheckAsync("rule: till-bound", p => Task.FromResult(ServerAuthorization.ScopeOf(p) == RoleScope.TillBranch));
        await CheckAsync("rule: sees every branch (BranchScope.ForAsync)",
            p => Task.FromResult(ServerAuthorization.ScopeOf(p) == RoleScope.Company));
        // ForStockAsync serves two routes no Cashier reaches; it is compared where it can be reached
        // (a Cashier's dead stock grant read as a token claim before, and is gone now).
        var lowStock = routes["GET /api/catalog/products/low-stock"];
        var reachable = LegacyAccessSnapshot.Decisions["GET /api/catalog/products/low-stock"];
        for (var i = 0; i < users.Count; i++)
        {
            var before = reachable[i] == '1' && LegacyAccessSnapshot.Decisions["rule: stock reads unrestricted (BranchScope.ForStockAsync)"][i] == '1';
            var p = Principal(users[i]);
            var now = await AllowsAsync(lowStock, p) && ServerAuthorization.Has(p, PermissionCatalog.StockViewOtherBranches);
            if (before != now) mismatches.Add($"{users[i].Role} [{users[i].Permissions}]: stock reads unrestricted");
        }

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    [Fact]
    public async Task A_token_that_carries_permission_claims_but_no_server_decision_is_refused_everywhere_a_permission_is_needed()
    {
        // Every key, written into the token's own identity, and even an identity named like the
        // server's but added twice: neither is the server's single decision.
        var keys = PermissionCatalog.All.Select(p => new Claim(ServerAuthorization.PermissionClaim, p.Key)).ToList();
        var forged = new ClaimsPrincipal(new ClaimsIdentity(
            keys.Append(new Claim("sub", Guid.NewGuid().ToString())).Append(new Claim("role", "Admin"))
                .Append(new Claim(Permissions.ClaimType, Permissions.ManagePayroll)), "Bearer", "name", "role"));
        var doubled = Principal(new User { Role = UserRole.Admin });
        doubled.AddIdentity(ServerAuthorization.CreateIdentity(new ServerAuthorizationContext(SystemRoles.AdminId, RoleScope.Company, new HashSet<string>())));

        foreach (var (route, key) in CompanyRoutes().Where(x => x.Route.Requirement?.Kind == "any-of"))
        {
            Assert.False(await AllowsAsync(route, forged), $"{key} let a token's own claims through.");
            Assert.False(await AllowsAsync(route, doubled), $"{key} accepted two server decisions.");
        }
        Assert.False(BranchAssignment.IsCompanyScoped(forged));
        Assert.Equal(RoleScope.TillBranch, ServerAuthorization.ScopeOf(forged));
    }
}
