using System.Reflection;
using ParadiERP.API.Authorization;
using ParadiERP.API.Endpoints;
using ParadiERP.API.Identity;
using ParadiERP.Application.Integrations;
using ParadiERP.Application.Regional;
using ParadiERP.Application.Subscriptions;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Auth;
using ParadiERP.Infrastructure.Customers;
using ParadiERP.Infrastructure.Integrations;
using ParadiERP.Infrastructure.Ledger;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Platform;
using ParadiERP.Infrastructure.Receivables;
using ParadiERP.Infrastructure.Rentals;
using ParadiERP.Infrastructure.Sync;
using ParadiERP.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ParadiERP.Tests.Security;

/// <summary>
/// An inventory of every route the API maps, checked against what each one is supposed to
/// require.
///
/// <para>
/// This exists because the expensive authorisation mistake in a codebase like this is never a
/// wrong rule — it is a route added to a group that already looks protected, or mapped outside
/// one by accident, and nobody noticing. Reading the handlers cannot catch that; an inventory
/// can. A new route therefore fails these tests until somebody states, here, what it needs.
/// </para>
/// <para>
/// The endpoints are only inspected, never invoked: the services they take are registered with
/// a throwing factory purely so minimal-API parameter binding can tell a service apart from a
/// request body.
/// </para>
/// </summary>
public class EndpointAuthorizationTests
{
    /// <summary>Routes that are deliberately reachable without a token, and why.</summary>
    private static readonly HashSet<string> Anonymous = new(StringComparer.Ordinal)
    {
        "/api/auth/login",       // the thing that issues tokens
        "/api/auth/ping",        // connectivity probe a till calls before it has one
        "/api/i18n/{code}",      // the login screen needs its translation before anyone signs in
        "/api/auth/sso/config",  // whether to offer the SSO button at all
        "/api/auth/sso/login",   // starts the OIDC challenge
    };

    /// <summary>
    /// Routes any signed-in user may call, cashiers included, because a till needs them. Every
    /// other route has to name a role — this list is the place a new exception gets argued for.
    /// A route that reads sales here is <b>branch-scoped</b> (BranchScope, docs/07 §2.3): the
    /// cashier gets their own till's branch, derived from the token, whatever the query says.
    /// </summary>
    private static readonly HashSet<string> AnyAuthenticatedRole = new(StringComparer.Ordinal)
    {
        "/api/sync/pull", "/api/sync/push",                     // the till's own reconciliation; binds the device itself
        "/api/sales", "/api/sales/{id:guid}",                   // a refund needs the original — branch-scoped
        "/api/sales/page",                                      // the same list a page at a time (2026-11-17) — branch-scoped
        "/api/reports/summary",                                 // an online Z-report — branch-scoped
        "/api/sales/{id:guid}/send-receipt",                    // the cashier re-sends the ticket — branch-scoped
        "/api/subscription",                                    // the till must learn it has expired
        "/api/auth/sso/csrf", "/api/auth/sso/logout", "/api/auth/sso/session",
        "/api/catalog/store/logo", "/api/catalog/branches/{id:guid}/logo",  // the top bar and the printed header
        "/api/auth/password",                                   // a person changes their own password (owner, 2026-10-05)
        "/api/auth/logout",                                     // a person ends their own sessions (security review, 2026-10-10)
        // The signed-in user's own profile, photo and two-step login (owner, 2026-11-11).
        "/api/auth/profile", "/api/auth/profile/photo", "/api/auth/two-factor", "/api/auth/two-factor/setup", "/api/auth/two-factor/enable", "/api/auth/two-factor/disable", "/api/auth/two-factor/recovery-codes",
    };

    /// <summary>
    /// The any-authenticated routes that read no branch data and so carry no scope, and why.
    /// Everything else in <see cref="AnyAuthenticatedRole"/> must be marked BranchScoped.
    /// </summary>
    private static readonly HashSet<string> ReviewedUnscoped = new(StringComparer.Ordinal)
    {
        "/api/sync/pull", "/api/sync/push",  // take the branch from the token's device already (SyncDeviceBindingTests)
        "/api/subscription",                 // the company's subscription, the same for every branch
        "/api/auth/sso/csrf", "/api/auth/sso/logout", "/api/auth/sso/session",  // session plumbing, no data
        // A logo is the company's face, not a branch figure: a till shows the main branch's when its own has none.
        "/api/catalog/store/logo", "/api/catalog/branches/{id:guid}/logo",
        "/api/auth/password",                // the caller's own row and nothing else
        "/api/auth/logout",                  // the same
        "/api/auth/profile", "/api/auth/profile/photo", "/api/auth/two-factor", "/api/auth/two-factor/setup", "/api/auth/two-factor/enable", "/api/auth/two-factor/disable", "/api/auth/two-factor/recovery-codes",  // the caller's own row and photo, nothing else
    };

    /// <summary>
    /// The money routes a cashier reaches when — and only when — he holds the cash box (answer 25).
    /// Each names all three roles and carries the assertion that keeps a cashier without the grant out.
    /// </summary>
    private static readonly HashSet<string> CashBoxRoutes = new(StringComparer.Ordinal)
    {
        "GET /api/payment-vouchers", "GET /api/payment-vouchers/refundable", "GET /api/payment-vouchers/refundable-contracts",
        "GET /api/payment-vouchers/{id:guid}",
        // Workers' bonuses (owner, 2026-11-08): who raises a voucher picks the branch's workers.
        "GET /api/payment-vouchers/bonus-workers",
        "GET /api/payment-vouchers/categories",
        // The supplier a cash-box cashier's supplier payment names (owner, 2026-10-10).
        "GET /api/suppliers",
        "POST /api/payment-vouchers", "POST /api/payment-vouchers/{id:guid}/pay", "POST /api/payment-vouchers/{id:guid}/cancel",
        "GET /api/receivables/invoices", "GET /api/receivables/invoices/{id:guid}", "GET /api/receivables/contracts",
        "GET /api/receivables/receipts", "GET /api/receivables/receipts/{id:guid}", "GET /api/receivables/receipts/purpose",
        "POST /api/receivables/receipts", "POST /api/receivables/receipts/{id:guid}/settle",
        // M7b: an advance on 419 applied or released, and the advances settings the receipt form reads.
        "POST /api/receivables/receipts/{id:guid}/apply", "GET /api/receivables/settings",
        // What the voucher forms read to be filled in; nothing here edits them.
        "GET /api/catalog/customers", "GET /api/catalog/branches", "GET /api/treasury/payment-methods",
        // The same lists a page at a time (server paging, owner 2026-11-17): each its list route's readers.
        "GET /api/catalog/customers/page", "GET /api/catalog/branches/page", "GET /api/suppliers/page",
        "GET /api/receivables/invoices/page", "GET /api/receivables/receipts/page", "GET /api/payment-vouchers/page",
    };

    internal sealed record Route(string Pattern, IReadOnlyList<string> Methods, Endpoint Endpoint)
    {
        public bool IsAnonymous => Endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
        public IReadOnlyList<IAuthorizeData> Authorize => Endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        public IReadOnlyList<AuthorizationPolicy> Policies => Endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>();

        public IReadOnlyList<string> Roles => Policies
            .SelectMany(p => p.Requirements.OfType<RolesAuthorizationRequirement>())
            .SelectMany(r => r.AllowedRoles).Distinct().Order(StringComparer.Ordinal).ToList();

        public IReadOnlyList<string> RequiredClaims => Policies
            .SelectMany(p => p.Requirements.OfType<ClaimsAuthorizationRequirement>())
            .Select(c => $"{c.ClaimType}={string.Join(",", c.AllowedValues ?? [])}").ToList();

        public MethodInfo? Handler => Endpoint.Metadata.GetMetadata<MethodInfo>();
        /// <summary>What RoutePermissions gave this route (AUTH-1); null only on the platform operator's.</summary>
        public RouteRequirement? Requirement => Endpoint.Metadata.GetMetadata<RoutePermissionAttribute>()?.Requirement;
        public bool HasPermissionRequirement => Policies.SelectMany(p => p.Requirements).OfType<PermissionRequirement>().Any();
        public bool IsBranchScoped => Endpoint.Metadata.GetMetadata<BranchScopedAttribute>() is not null;
        public override string ToString() => $"{string.Join("/", Methods)} {Pattern}";
    }

    private static readonly Type[] HandlerServices =
    [
        typeof(PosDbContext), typeof(TokenService), typeof(SyncService), typeof(ISubscriptionProvider),
        typeof(IRegionalSettingsProvider), typeof(ILanguagePackStore), typeof(TenantIntegrations),
        typeof(LedgerPoster), typeof(LedgerReports), typeof(RentalDocuments), typeof(CustomerQuantityStatements), typeof(TenantRegistry),
        typeof(ReceivableDocuments), typeof(CustomerStatements), typeof(CustomerRepresentatives),
        typeof(PlatformStore), typeof(PlatformDbContext), typeof(CompanyProvisioner), typeof(PlatformIntegrationStore),
        typeof(IntegrationHttp), typeof(SmtpReceiptSender), typeof(ReceiptSendLimiter), typeof(SsoOptions),
        typeof(ICompanyIntegrationsProvider), typeof(ISecretProtector), typeof(IntegrationSecretProtector),
        typeof(TenantContext), typeof(ProvisioningOptions), typeof(PlatformOptions), typeof(LoginThrottle),
        typeof(PaymentVouchers), typeof(ParadiERP.Infrastructure.Hr.EmployeeDirectory), typeof(ParadiERP.Infrastructure.Hr.AttendanceSheet),
        typeof(ParadiERP.Infrastructure.Hr.AttendanceReview),
        typeof(ParadiERP.Infrastructure.Payroll.WageMoney), typeof(ParadiERP.Infrastructure.Payroll.PayrollDocuments),
        typeof(ParadiERP.Infrastructure.Payroll.PayrollSettingsStore),
        typeof(ParadiERP.Application.Files.IFileStore), typeof(RentalAttachments), typeof(CustomerAttachments),
        typeof(CustomerOpeningBalances), typeof(ParadiERP.Infrastructure.Suppliers.SupplierAccounts),
        typeof(ParadiERP.Infrastructure.Reports.ItemStatusReport),
        // The customer create route reads the clock: the YYMM in the code it draws (2026-10-05).
        typeof(TimeProvider),
    ];

    /// <summary>Everything Program.cs maps, with the platform portal and SSO switched on.</summary>
    internal static List<Route> Inventory()
    {
        var builder = WebApplication.CreateBuilder();
        foreach (var type in HandlerServices)
            builder.Services.AddScoped(type, _ => throw new NotSupportedException("Endpoints are inspected, never invoked."));
        builder.Services.AddAntiforgery();
        builder.Services.AddAuthorization();
        var app = builder.Build();

        // Exactly what Program maps (ApiEndpoints.MapApi), with the platform portal and SSO on.
        var platform = new PlatformOptions { Enabled = true, Host = "portal.test", OperatorSubjects = ["operator"] };
        app.MapApi(new SsoOptions { Enabled = true, Authority = "https://id.test", ClientId = "c", ClientSecret = "s" }, platform);

        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => new Route(
                "/" + endpoint.RoutePattern.RawText!.Trim('/'),
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [],
                endpoint))
            .ToList();
    }

    [Fact]
    public void Every_api_route_either_needs_a_token_or_is_on_the_reviewed_anonymous_list()
    {
        var reachable = Inventory()
            .Where(r => r.Pattern.StartsWith("/api", StringComparison.Ordinal))
            .Where(r => r.IsAnonymous || r.Authorize.Count == 0)
            .Select(r => r.Pattern)
            .Distinct()
            .ToList();

        Assert.Equal(Anonymous.Order(StringComparer.Ordinal), reachable.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// AUTH-1: every company route carries exactly the requirement the approved plan gives it, and the
    /// requirement is a server-resolved permission check — no route outside the platform portal names a
    /// role, a claim or an assertion any more.
    /// </summary>
    [Fact]
    public void Every_company_route_requires_exactly_its_approved_permission_and_no_role()
    {
        var company = Inventory().Where(r => !r.Pattern.StartsWith("/api/platform", StringComparison.Ordinal)).ToList();
        Assert.Equal(470, company.Sum(r => r.Methods.Count));  // + the 83 list page routes (server paging, 2026-11-17); + the nine profile, photo and two-step login routes and the admin two-step reset (2026-11-11); + POST /api/receivables/receipts/{id:guid}/keep (SAL-13, 2026-11-10); + the three MS-04 workshop-list routes (2026-11-10); + GET /api/maintenance/history (M7d, 2026-11-09); + the three customer-advance routes (M7b, 2026-11-09); + the ten fixed-asset routes (REN-12, M7a, 2026-11-09); + GET /api/maintenance/rework-candidates (maintenance M7c, 2026-11-09); + the nine customer-job, pricing, invoice, repair-rate and profit routes (maintenance M5a/M5, 2026-11-09); + GET/PUT /api/maintenance/account-map and the maintenance practice ledger with post and reverse (maintenance M6, 2026-11-09); + the maintenance job cost and two cost reports (maintenance M4, 2026-11-09); + the four maintenance parts and outside-work routes (maintenance M2, 2026-11-09); + the four maintenance hours routes (maintenance M3, 2026-11-09); + the eight maintenance job routes (maintenance M1, 2026-11-09); + GET and PUT /api/maintenance/settings (maintenance M0, 2026-11-09); + the sales-cost switch's three routes (C-01, 2026-11-09); + POST practice-ledger/post and /reverse (Phase D, 2026-11-09); + GET /api/manufacturing/accounting/practice-ledger (Phase C2, 2026-11-09); + GET /api/payment-vouchers/bonus-workers (2026-11-08); + 4 manufacturing costing choices routes (Phase C1, 2026-11-08); + 4 weighted-average shadow routes (P0.4b, 2026-11-08); + rental return preview; Phase 1/2 manufacturing; Phase A costing; + 3 B1 snapshot routes; + 7 B2 cost-report routes (2026-10-29); + missing-cost report (SAL-02); + 3 closing routes (CLO-01); + 6 roles routes (AUTH-1 Step 5).
        foreach (var route in company)
            foreach (var method in route.Methods)
            {
                var planned = RoutePermissionPlan.ByRoute[$"{method} {route.Pattern}"];
                var served = route.Requirement;
                Assert.True(served is not null, $"{method} {route.Pattern} has no RoutePermissions entry.");
                Assert.Equal(planned.Kind, served!.Kind);
                Assert.Equal(planned.Keys, served.AnyOf);
                Assert.Equal(planned.Kind == "any-of", route.HasPermissionRequirement);
                Assert.Empty(route.Roles);
                Assert.Empty(route.RequiredClaims);
                Assert.DoesNotContain(route.Policies.SelectMany(p => p.Requirements), q => q is AssertionRequirement);
            }
    }

    /// <summary>The production table is the approved plan, entry for entry: neither can be edited alone.</summary>
    [Fact]
    public void The_served_table_is_the_approved_plan()
    {
        Assert.Equal(RoutePermissionPlan.ByRoute.Keys.Order(StringComparer.Ordinal), RoutePermissions.ByRoute.Keys.Order(StringComparer.Ordinal));
        foreach (var (key, planned) in RoutePermissionPlan.ByRoute)
        {
            Assert.Equal(planned.Kind, RoutePermissions.ByRoute[key].Kind);
            Assert.Equal(planned.Keys, RoutePermissions.ByRoute[key].AnyOf);
        }
    }

    private static bool IsCashBoxRoute(Route route) => route.Requirement?.AnyOf.Contains(Permissions.CashBox) == true;

    [Fact]
    public void Creating_a_customer_needs_customers_create_and_editing_or_deleting_one_does_not()
    {
        var create = Inventory().Single(r => r.Pattern == "/api/catalog/customers" && r.Methods.Contains(HttpMethods.Post));
        Assert.Equal([PermissionCatalog.CustomersCreate], create.Requirement!.AnyOf);
        foreach (var route in Inventory().Where(r => r.Pattern == "/api/catalog/customers/{id:guid}"))
            Assert.DoesNotContain(PermissionCatalog.CustomersCreate, route.Requirement!.AnyOf);
    }

    [Fact]
    public void The_cash_box_routes_are_exactly_the_reviewed_ones_and_each_checks_the_grant()
    {
        var marked = Inventory().Where(IsCashBoxRoute).ToList();
        Assert.Equal(
            CashBoxRoutes.Order(StringComparer.Ordinal),
            marked.SelectMany(r => r.Methods.Select(m => $"{m} {r.Pattern}")).Distinct().Order(StringComparer.Ordinal));
        // Each also takes its own finance or catalogue key, so the role that holds that key passes.
        foreach (var route in marked) Assert.True(route.Requirement!.AnyOf.Count >= 2, route.ToString());

        // Approving a payment voucher is never a cash-box route: the payer does not decide it is owed.
        var approve = Inventory().Single(r => r.Pattern == "/api/payment-vouchers/{id:guid}/approve");
        Assert.False(IsCashBoxRoute(approve));
        Assert.Equal([PermissionCatalog.VouchersApprove], approve.Requirement!.AnyOf);
    }

    [Fact]
    public void Routes_outside_a_role_gate_are_the_ones_a_till_needs_and_nothing_else()
    {
        // A Cashier with no grant: what any signed-in user reaches, and the till's own keys.
        var open = Inventory()
            .Where(r => r.Pattern.StartsWith("/api", StringComparison.Ordinal))
            .Where(r => r.Requirement?.Kind == "signed-in"
                || r.Requirement?.Kind == "any-of" && r.Requirement.AnyOf.Any(SystemRoles.CashierSeed.Contains))
            .Select(r => r.Pattern)
            .Distinct()
            .ToList();

        // A route that reaches this list is readable by a cashier standing at a counter. That
        // may well be right — it is not right by accident.
        Assert.Equal(AnyAuthenticatedRole.Order(StringComparer.Ordinal), open.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Platform_routes_are_operator_only_and_never_reachable_with_a_company_token()
    {
        var platformRoutes = Inventory().Where(r => r.Pattern.StartsWith("/api/platform", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(platformRoutes);
        foreach (var route in platformRoutes)
        {
            // The operator policy pins the cookie scheme, the operator subject allowlist and the
            // portal hostname (SsoConfiguration). A company JWT satisfies none of them, and a
            // role requirement here would mean a company user could satisfy it.
            Assert.All(route.Authorize, data => Assert.Equal(SsoConfiguration.OperatorPolicy, data.Policy));
            Assert.Empty(route.Roles);
        }
    }

    [Fact]
    public void Seeing_another_branch_s_stock_is_decided_inside_the_route_and_marked_as_scoped()
    {
        var availability = Inventory().Single(r => r.Pattern.Contains("/stock/availability", StringComparison.Ordinal));

        // Everyone in the back office may look up their own branch; the permission
        // (Permissions.ViewOtherBranchStock) adds the other branches' available quantity alone.
        // So the route is no longer claim-gated — the handler decides, and says so as metadata.
        // What it answers is pinned in BranchLockTests.
        Assert.True(availability.IsBranchScoped);
        Assert.Equal([PermissionCatalog.InventoryView], availability.Requirement!.AnyOf);
    }

    [Fact]
    public void Every_route_a_till_may_read_is_branch_scoped_or_on_the_reviewed_list()
    {
        var open = Inventory()
            .Where(r => AnyAuthenticatedRole.Contains(r.Pattern))
            .ToList();

        Assert.NotEmpty(open);
        foreach (var route in open)
        {
            // The marker is metadata, not a comment: a new any-authenticated route that reads
            // sales without going through BranchScope fails here until someone either scopes it
            // or writes down why it needs no scope.
            Assert.True(route.IsBranchScoped || ReviewedUnscoped.Contains(route.Pattern),
                $"{route} is reachable by a cashier and neither branch-scoped nor on the reviewed list.");
            Assert.False(route.IsBranchScoped && ReviewedUnscoped.Contains(route.Pattern),
                $"{route} is both scoped and listed as needing no scope — one of the two is stale.");
        }
    }

    /// <summary>
    /// The permission is enforced on /stock/availability and undercut by the two catalog routes
    /// that list every branch's position (docs/07 §2.3). Once CatalogEndpoints confines them
    /// through BranchScope.ForStockAsync and marks them, this asserts it stays that way.
    /// </summary>
    [Fact]
    public void Every_route_that_lists_stock_by_branch_either_needs_the_named_permission_or_scopes_to_the_caller()
    {
        var stockReads = Inventory()
            .Where(r => r.Methods.Contains(HttpMethods.Get))
            .Where(r => r.Pattern is "/api/catalog/products/low-stock" or "/api/catalog/products/{id:guid}/stock"
                or "/api/catalog/stock/availability/{productId:guid}" or "/api/catalog/stock/levels")
            .ToList();

        Assert.Equal(4, stockReads.Count);
        foreach (var route in stockReads)
        {
            var needsPermission = route.Requirement?.AnyOf.Contains(PermissionCatalog.StockViewOtherBranches) == true;
            Assert.True(needsPermission || route.IsBranchScoped,
                $"{route} lists stock by branch and neither requires {Permissions.ViewOtherBranchStock} nor scopes to the caller.");
        }
    }

    [Fact]
    public void A_retroactive_extension_refusal_is_reachable_by_someone_or_not_expressed_at_all()
    {
        var extend = Inventory().Single(r =>
            r.Pattern == "/api/rentals/contracts/{id:guid}/extensions" && r.Methods.Contains(HttpMethods.Post));

        // The engine refuses a retroactive extension to a non-manager. Either a non-manager can
        // reach the route — then it must be branch-scoped like every other till route — or the
        // route is Manager/Admin-only, in which case the handler passes isManager: true and
        // sniffs no role, because a refusal nobody can trigger is dead wiring (§2.3, Q31:
        // extensions stay in the back office).
        var backOfficeOnly = extend.Requirement!.AnyOf.SequenceEqual([PermissionCatalog.RentalsCreate]);
        Assert.True(backOfficeOnly ^ extend.IsBranchScoped,
            $"{extend}: {(backOfficeOnly ? "back-office only and also scoped" : "reachable by a cashier without a branch scope")}.");
    }

    [Fact]
    public void Sync_never_takes_the_terminal_it_acts_as_from_the_caller()
    {
        foreach (var route in Inventory().Where(r => r.Pattern.StartsWith("/api/sync", StringComparison.Ordinal)))
        {
            // A `deviceCode` parameter here binds from the query string or the body, and it
            // decided which branch's stock, cash box, ledger and receipt numbers the caller
            // touched. It comes from the token's claim instead — see SyncEndpoints.
            Assert.DoesNotContain(route.Handler!.GetParameters(),
                p => string.Equals(p.Name, "deviceCode", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// "The branch choice does not appear except for the super admin" (BranchLock). A screen can
    /// hide a select; only the server can make the hidden choice stick. Every route that raises a
    /// document at a branch, or pays out of a named box, must go through CallersBranch.
    /// </summary>
    [Fact]
    public void Every_route_that_raises_a_document_at_a_branch_or_pays_from_a_box_takes_the_caller_s_branch()
    {
        var raising = Inventory()
            .Where(r => r.Handler is not null && r.Handler.GetParameters().Any(p =>
                typeof(ParadiERP.Application.Contracts.IRaisedAtBranch).IsAssignableFrom(p.ParameterType)
                || typeof(ParadiERP.Application.Contracts.IPaidFromBox).IsAssignableFrom(p.ParameterType)))
            .ToList();

        // Vouchers, receipts, invoices, contracts, issue and return notes, stock moves, opening
        // balances, state changes, attendance, payroll runs, advances, wage and statutory payments,
        // remittances, employees and devices — create and update.
        Assert.True(raising.Count >= 20, $"only {raising.Count} routes found");
        foreach (var route in raising)
            Assert.True(route.Endpoint.Metadata.GetMetadata<CallersBranchAttribute>() is not null,
                $"{route} raises a document at a branch without taking the caller's branch.");
    }

    /// <summary>
    /// A list or a report that filters by branch reads the caller's own branch for everyone but the
    /// Admin — set on the server, so "all branches" typed into a URL is still one branch.
    /// </summary>
    [Fact]
    public void Every_list_filtered_by_branch_reads_the_caller_s_branch()
    {
        var filtered = Inventory()
            .Where(r => r.Methods.Contains(HttpMethods.Get))
            .Where(r => r.Handler is not null && r.Handler.GetParameters().Any(p =>
                string.Equals(p.Name, "branchId", StringComparison.Ordinal)))
            .ToList();

        Assert.NotEmpty(filtered);
        foreach (var route in filtered)
            Assert.True(route.Endpoint.Metadata.GetMetadata<CallersBranchAttribute>() is not null,
                $"{route} filters by branch but a manager could still ask for another one.");
    }
}
