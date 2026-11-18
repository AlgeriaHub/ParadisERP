using AlgeriaPos.Tests.Hosting;
using System.Security.Claims;
using System.Text;
using AlgeriaPos.API.Endpoints;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Files;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Application.Security;
using AlgeriaPos.Application.Subscriptions;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Auth;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Payroll;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Receivables;
using AlgeriaPos.Tests.Files;
using AlgeriaPos.Tests.Payroll;
using AlgeriaPos.Tests.Receivables;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AlgeriaPos.Tests.Rentals.RentalTestPaper;

namespace AlgeriaPos.Tests.Security;

/// <summary>
/// The owner's final word on branches (2026-09-30): "in the whole program, the branch choice does
/// not appear except for the super admin". The Admin chooses; everyone else — the main branch's
/// staff included — works at the branch assigned to them, and a document they raise lands there
/// whatever the request names (<see cref="BranchLock"/>, <see cref="CallersBranch"/>).
///
/// <para>
/// One test per module the rule reaches — rentals, receivables, payment vouchers, stock, payroll
/// money — each sending a request that names <b>Algiers</b> as a manager assigned to <b>Oran</b>,
/// and each finding the document at Oran. The stock one goes through the real endpoint, filters
/// and all, to pin the wiring as well as the rule.
/// </para>
/// </summary>
public class BranchLockTests
{
    private static readonly Guid Algiers = TillHarness.Algiers; // the main branch in every harness
    private static readonly Guid Oran = TillHarness.Oran;

    // ---------- the rule ----------

    [Theory]
    [InlineData(UserRole.Admin, false)]
    [InlineData(UserRole.Manager, true)]
    [InlineData(UserRole.Cashier, true)]
    public void Only_the_admin_may_choose_a_branch(UserRole role, bool locked) =>
        // AUTH-1: read from the system role's scope, never from the role's name.
        Assert.Equal(locked, BranchLock.IsLocked(SystemRoles.For(role).Scope));

    [Fact]
    public void A_user_works_at_their_assigned_branch_and_a_legacy_row_at_the_main_one()
    {
        Assert.Equal(Oran, BranchLock.EffectiveBranch(Oran, Algiers));
        Assert.Equal(Oran, BranchLock.EffectiveBranch(Oran, null));
        Assert.Equal(Algiers, BranchLock.EffectiveBranch(null, Algiers));
        Assert.Null(BranchLock.EffectiveBranch(null, null));
    }

    [Fact]
    public async Task The_login_response_carries_the_branch_and_whether_it_is_locked()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        // Saved: the login reads the user's role from the tables (AUTH-1), as a real sign-in does.
        var manager = await BranchAssignment.LoginUserAsync((await UserAsync(db, UserRole.Manager, Oran)).User, db, default);
        Assert.Equal((Oran, true), (manager.BranchId, manager.BranchLocked));

        // The main branch grants nothing: a manager there is locked to it all the same.
        var mainManager = await BranchAssignment.LoginUserAsync((await UserAsync(db, UserRole.Manager, Algiers)).User, db, default);
        Assert.Equal((Algiers, true), (mainManager.BranchId, mainManager.BranchLocked));

        // An admin bound to Oran starts his forms on Oran and may change them.
        var admin = await BranchAssignment.LoginUserAsync((await UserAsync(db, UserRole.Admin, Oran)).User, db, default);
        Assert.Equal((Oran, false), (admin.BranchId, admin.BranchLocked));

        // The seeded admin and every other legacy row: no branch, so the main one.
        var legacy = await BranchAssignment.LoginUserAsync((await UserAsync(db, UserRole.Cashier, null)).User, db, default);
        Assert.Equal((Algiers, true), (legacy.BranchId, legacy.BranchLocked));
    }

    // ---------- the filter ----------

    internal static async Task<(User User, ClaimsPrincipal Principal)> UserAsync(PosDbContext db, UserRole role, Guid? branch)
    {
        var user = new User { Username = $"{role}-{Guid.NewGuid():N}"[..20], FullName = "Test", Role = role, BranchId = branch };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (user, TillHarness.Principal(role, null, user.Id));
    }

    private static DefaultHttpContext Http(PosDbContext db, ClaimsPrincipal principal)
    {
        var services = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
        return new DefaultHttpContext { RequestServices = services, User = principal };
    }

    /// <summary>What the handler would receive: the filter run over the arguments, the refusal if any.</summary>
    private static async Task<(IResult? Refusal, T Argument)> ThroughFilterAsync<T>(PosDbContext db, ClaimsPrincipal principal, T request)
    {
        var context = EndpointFilterInvocationContext.Create(Http(db, principal), request);
        int[] at = [0], none = [];
        var plan = new CallersBranch.Plan(-1, (request is IRaisedAtBranch) ? at : none, (request is IPaidFromBox) ? at : none);
        var refusal = await CallersBranch.ApplyAsync(context, plan);
        return (refusal, context.GetArgument<T>(0));
    }

    [Fact]
    public async Task A_list_s_branch_filter_reads_as_the_caller_s_branch_for_everyone_but_the_admin()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var (_, manager) = await UserAsync(db, UserRole.Manager, Oran);
        var (_, admin) = await UserAsync(db, UserRole.Admin, Oran);
        var plan = new CallersBranch.Plan(0, [], []);

        // "All branches" and "Algiers" both read as Oran for the manager…
        foreach (Guid? asked in new Guid?[] { null, Algiers })
        {
            var context = EndpointFilterInvocationContext.Create(Http(db, manager), asked);
            Assert.Null(await CallersBranch.ApplyAsync(context, plan));
            Assert.Equal(Oran, context.GetArgument<Guid?>(0));
        }

        // …and the admin's "all branches" stays all branches.
        var adminContext = EndpointFilterInvocationContext.Create(Http(db, admin), (Guid?)null);
        Assert.Null(await CallersBranch.ApplyAsync(adminContext, plan));
        Assert.Null(adminContext.GetArgument<Guid?>(0));
    }

    [Fact]
    public async Task The_admin_s_branch_is_honoured_and_must_exist()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var (_, admin) = await UserAsync(h.Db, UserRole.Admin, Algiers);
        var request = new CreatePaymentVoucherRequest(Oran, PaymentVoucherKind.Expense, 100m, "cash",
            PayeeName: "Naftal", ExpenseCategory: "fuel");

        var (refusal, honoured) = await ThroughFilterAsync(h.Db, admin, request);
        Assert.Null(refusal);
        Assert.Equal(Oran, honoured.BranchId);

        var (unknown, _) = await ThroughFilterAsync(h.Db, admin, request with { BranchId = Guid.NewGuid() });
        Assert.Equal(400, ((IStatusCodeHttpResult)unknown!).StatusCode);
    }

    [Fact]
    public async Task A_legacy_user_with_no_branch_raises_at_the_main_branch()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var (_, legacy) = await UserAsync(h.Db, UserRole.Manager, null);

        var (refusal, request) = await ThroughFilterAsync(h.Db, legacy,
            new CreatePaymentVoucherRequest(Oran, PaymentVoucherKind.Expense, 100m, "cash", PayeeName: "Naftal", ExpenseCategory: "fuel"));

        Assert.Null(refusal);
        Assert.Equal(Algiers, request.BranchId);
    }

    // ---------- one per module ----------

    [Fact]
    public async Task Vouchers_a_payment_voucher_naming_another_branch_lands_on_the_user_s_branch()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var (_, manager) = await UserAsync(h.Db, UserRole.Manager, Oran);

        var (refusal, request) = await ThroughFilterAsync(h.Db, manager,
            new CreatePaymentVoucherRequest(Algiers, PaymentVoucherKind.Expense, 12_000m, "cash",
                PayeeName: "Station Naftal", ExpenseCategory: "fuel"));
        Assert.Null(refusal);

        var raised = await new PaymentVouchers(h.Db, new LedgerPoster(h.Db), new Algeria()).CreateAsync(request, "gerant");
        Assert.True(raised.Ok, raised.Message);
        Assert.Equal(Oran, raised.Value.Voucher.BranchId);
    }

    [Fact]
    public async Task Receivables_a_receipt_naming_another_branch_lands_on_the_user_s_branch()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var (_, manager) = await UserAsync(h.Db, UserRole.Manager, Oran);

        var (refusal, request) = await ThroughFilterAsync(h.Db, manager,
            new CreateReceiptRequest(ReceivableHarness.Contractor, Algiers, 5_000m, "cash"));
        Assert.Null(refusal);

        var receipt = await h.Receivables.CreateReceiptAsync(request, "gerant");
        Assert.True(receipt.Ok, receipt.Message);
        Assert.Equal(Oran, receipt.Value!.BranchId);
    }

    [Fact]
    public async Task Receivables_a_typed_invoice_naming_another_branch_lands_on_the_user_s_branch()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var (_, manager) = await UserAsync(h.Db, UserRole.Manager, Oran);

        var (refusal, request) = await ThroughFilterAsync(h.Db, manager, ReceivableHarness.DirectInvoice());
        Assert.Null(refusal);
        Assert.Equal(Algiers, ReceivableHarness.DirectInvoice().BranchId); // what was asked for

        var invoice = await h.RaiseAsync(request);
        Assert.True(invoice.Ok, invoice.Message);
        Assert.Equal(Oran, invoice.Value!.BranchId);
    }

    [Fact]
    public async Task Rentals_a_contract_naming_another_branch_lands_on_the_user_s_branch()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var (_, manager) = await UserAsync(h.Db, UserRole.Manager, Oran);

        var (refusal, request) = await ThroughFilterAsync(h.Db, manager, ContractorSited(new UpsertRentalContractRequest(
            ReceivableHarness.Contractor, Algiers,
            [new RentalContractLineRequest(ReceivableHarness.Scaffold, 10, 30, RentUnitPrice: 3_000m, DamageUnitPrice: 2_000m)])));
        Assert.Null(refusal);

        var contract = await h.Rentals.CreateContractAsync(request, "gerant");
        Assert.True(contract.Ok, contract.Message);
        Assert.Equal(Oran, contract.Value!.BranchId);
    }

    [Fact]
    public async Task Rentals_an_issue_note_naming_another_branch_leaves_from_the_user_s_branch()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var (_, manager) = await UserAsync(h.Db, UserRole.Manager, Oran);

        var (refusal, request) = await ThroughFilterAsync(h.Db, manager,
            new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(Guid.NewGuid(), 1)]));

        Assert.Null(refusal);
        Assert.Equal(Oran, request.BranchId);
    }

    [Fact]
    public async Task Payroll_money_a_statutory_payment_naming_another_branch_lands_on_the_user_s_branch()
    {
        await using var h = await WageMoneyHarness.NewAsync();
        var (_, manager) = await UserAsync(h.Db, UserRole.Manager, Oran);

        var (refusal, request) = await ThroughFilterAsync(h.Db, manager, new RecordStatutoryPaymentRequest(
            WageMoneyHarness.Algiers, LedgerAccounts.SocialSecurityPayable, 1_000m,
            nameof(PayoutMethod.Bank), PeriodYear: 2026, PeriodMonth: 9));
        Assert.Null(refusal);

        var paid = await h.Money.PayStatutoryAsync(request, "gerant");
        Assert.True(paid.Ok, paid.Message);
        Assert.Equal(WageMoneyHarness.Oran, paid.Value!.BranchId);
    }

    [Fact]
    public async Task Payroll_money_cash_from_another_branch_s_box_is_refused_in_words()
    {
        await using var h = await WageMoneyHarness.NewAsync();
        var (_, manager) = await UserAsync(h.Db, UserRole.Manager, Oran);
        var (_, admin) = await UserAsync(h.Db, UserRole.Admin, Oran);
        var advance = new GrantWageAdvanceRequest(Guid.NewGuid(), 1_000m, nameof(PayoutMethod.Cash), CashBoxId: h.AlgiersBox.Id);

        var (refused, _) = await ThroughFilterAsync(h.Db, manager, advance);
        var problem = Assert.IsAssignableFrom<IValueHttpResult>(refused);
        Assert.Equal(400, ((IStatusCodeHttpResult)refused!).StatusCode);
        Assert.Equal(BranchLock.ForeignBoxMessage, problem.Value);

        Assert.Null((await ThroughFilterAsync(h.Db, manager, advance with { CashBoxId = h.OranBox.Id })).Refusal);
        Assert.Null((await ThroughFilterAsync(h.Db, admin, advance)).Refusal);
    }

    /// <summary>
    /// One real route, called the way Kestrel would call it once authorisation has passed: the
    /// group's filters, the parameter binding and the handler, against the test's database.
    /// </summary>
    internal static Task<(int Status, string Body)> InvokeAsync(
        PosDbContext db, Action<IEndpointRouteBuilder> map, string method, string pattern,
        ClaimsPrincipal caller, string json, params (string Key, string Value)[] route) =>
        InvokeAsync(db, TestFiles.Fresh(), map, method, pattern, caller, json, route);

    /// <summary>The same, with the files folder the test reads back from.</summary>
    internal static async Task<(int Status, string Body)> InvokeAsync(
        PosDbContext db, IFileStore files, Action<IEndpointRouteBuilder> map, string method, string pattern,
        ClaimsPrincipal caller, string json, params (string Key, string Value)[] route)
    {
        // Built once per test database, file store and route map, then reused (Hosting/EndpointApps). A
        // lambda that captures nothing is one instance, so the same call site finds the same application.
        var built = EndpointApps.For(db, (nameof(InvokeAsync), files, map), () =>
        {
            var builder = WebApplication.CreateBuilder();
            // By instance, so the container never disposes the test's context at the end of the scope.
            builder.Services.AddSingleton(db);
            builder.Services.AddSingleton(files);
            builder.Services.AddSingleton(new LedgerPoster(db));
            builder.Services.AddSingleton<ISubscriptionProvider>(new Unrestricted());
            builder.Services.AddSingleton<IRegionalSettingsProvider>(new Algeria());
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Customers.CustomerRepresentatives>(_ => throw new NotSupportedException());
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Customers.CustomerAttachments>(_ => throw new NotSupportedException());
            builder.Services.AddScoped(_ => new AlgeriaPos.Infrastructure.Customers.CustomerOpeningBalances(
                db, new LedgerPoster(db), new Algeria(), TimeProvider.System));
            builder.Services.AddScoped(_ => new AlgeriaPos.Infrastructure.Suppliers.SupplierAccounts(
                db, new LedgerPoster(db), new Algeria(), TimeProvider.System));
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Rentals.RentalDocuments>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Reports.ItemStatusReport>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Valuation.StockValuationShadow>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Catalog.ReceivingCostPrice>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Ledger.SalesCostSwitch>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Ledger.CostPriceRevaluationPosting>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Manufacturing.ManufacturingCostingChoices>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Maintenance.MaintenanceSettingsStore>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Maintenance.MaintenanceJobs>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Payroll.PayrollSettingsStore>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Hr.EmployeeDirectory>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Hr.AttendanceSheet>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Manufacturing.AttendanceCapacity>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Maintenance.MaintenanceLabour>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Maintenance.MaintenanceWorkshop>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Maintenance.MaintenanceJobParts>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Manufacturing.LabourRates>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Maintenance.MaintenanceCosting>();
            builder.Services.AddScoped<ReceivableDocuments>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Receivables.ReceivableSettingsStore>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Receivables.CustomerStatements>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Maintenance.MaintenanceCustomerJobs>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Maintenance.MaintenanceRepairRates>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Maintenance.MaintenanceAccountMap>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Maintenance.MaintenancePracticeLedgerReport>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.Maintenance.MaintenanceLedgerPostings>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.FixedAssets.FixedAssetSettingsStore>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.FixedAssets.FixedAssetSwitchOver>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.FixedAssets.FixedAssetRegisterReport>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.FixedAssets.FixedAssetDepreciationReport>();
            builder.Services.AddScoped<AlgeriaPos.Infrastructure.FixedAssets.FixedAssetLedgerPostings>();
            builder.Services.AddScoped(_ => new AlgeriaPos.Infrastructure.Catalog.StockTransfers(db, new LedgerPoster(db)));
            // The customer create route reads the clock (the YYMM in the code it draws, 2026-10-05).
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddAuthorization();
            var app = builder.Build();
            map(app);
            return app;
        });
        var endpoint = built.Endpoint(method, pattern);

        using var scope = built.App.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = caller };
        http.Request.Method = method;
        foreach (var (key, value) in route) http.Request.RouteValues[key] = value;
        if (!string.IsNullOrEmpty(json) && json.StartsWith("?"))
        {
            http.Request.QueryString = new QueryString(json);
            json = "";
        }
        var body = Encoding.UTF8.GetBytes(json);
        http.Request.Body = new MemoryStream(body);
        http.Request.ContentType = "application/json";
        http.Request.ContentLength = body.Length;
        http.Response.Body = new MemoryStream();
        http.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new HasBody());

        await endpoint.RequestDelegate!(http);

        http.Response.Body.Position = 0;
        return (http.Response.StatusCode, await new StreamReader(http.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task Stock_an_adjustment_naming_another_branch_moves_the_user_s_branch_through_the_real_endpoint()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var (_, manager) = await UserAsync(db, UserRole.Manager, Oran);

        var (status, answer) = await InvokeAsync(db, app => app.MapCatalogEndpoints(), "POST",
            "/api/catalog/products/{id:guid}/stock", manager,
            $$"""{"branchId":"{{Algiers}}","qtyDelta":5,"reason":"stocktake","reorderLevel":null}""",
            ("id", TillHarness.Pipe.ToString()));

        Assert.True(status < 400, $"status {status}: {answer}");
        var stock = await db.ProductStocks.AsNoTracking().Where(s => s.ProductId == TillHarness.Pipe)
            .ToDictionaryAsync(s => s.BranchId, s => s.StockQty);
        Assert.Equal(10m, stock[Algiers]);
        Assert.Equal(15m, stock[Oran]);
    }

    // ---------- every user has a branch ----------

    // Since 2026-10-05 an account is given to an employee, and an employee always belongs to a branch:
    // the account works where its employee does unless the screen chooses another branch, so a new user
    // can no longer arrive without one. What is refused now is an account with no employee.
    [Fact]
    public async Task A_new_user_works_at_its_employee_s_branch_and_an_account_without_an_employee_is_refused_in_words()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var (_, admin) = await UserAsync(db, UserRole.Admin, Algiers);
        var employee = new Employee { Number = "EMP-000001", FullName = "Gérant Oran", BranchId = Oran, HiredOn = new DateOnly(2026, 1, 1) };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        foreach (var role in new[] { "Cashier", "Manager", "Admin" })
        {
            var (status, answer) = await InvokeAsync(db, app => app.MapUsersEndpoints(), "POST", "/api/users", admin,
                $$"""{"username":"new.{{role.ToLowerInvariant()}}","fullName":"New","role":"{{role}}","password":"secret1","pin":null,"branchId":"{{Oran}}"}""");
            Assert.Equal(400, status);
            Assert.Contains(EmployeeAccounts.EmployeeRequiredCode, answer);
        }

        var (created, _) = await InvokeAsync(db, app => app.MapUsersEndpoints(), "POST", "/api/users", admin,
            $$"""{"employeeId":"{{employee.Id}}","role":"Manager","password":"secret1","pin":null}""");
        Assert.Equal(201, created);
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.EmployeeId == employee.Id);
        Assert.Equal(Oran, user.BranchId);
        Assert.Equal("Gérant Oran", user.FullName);
    }

    [Fact]
    public async Task A_user_s_branch_may_be_changed_but_never_removed()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var (_, admin) = await UserAsync(db, UserRole.Admin, Algiers);
        var (manager, _) = await UserAsync(db, UserRole.Manager, Oran);
        var id = ("id", manager.Id.ToString());

        var (cleared, answer) = await InvokeAsync(db, app => app.MapUsersEndpoints(), "PUT", "/api/users/{id:guid}", admin,
            """{"fullName":"Test","role":"Manager","isActive":true,"branchId":null}""", id);
        Assert.Equal(400, cleared);
        Assert.Contains(BranchLock.BranchNotClearableMessage, answer);

        var (moved, _) = await InvokeAsync(db, app => app.MapUsersEndpoints(), "PUT", "/api/users/{id:guid}", admin,
            $$"""{"fullName":"Test","role":"Manager","isActive":true,"branchId":"{{Algiers}}"}""", id);
        Assert.Equal(200, moved);

        // An older client that does not know the field leaves the branch where it is.
        var (untouched, _) = await InvokeAsync(db, app => app.MapUsersEndpoints(), "PUT", "/api/users/{id:guid}", admin,
            """{"fullName":"Renamed","role":"Manager","isActive":true}""", id);
        Assert.Equal(200, untouched);
        Assert.Equal(Algiers, (await db.Users.AsNoTracking().SingleAsync(u => u.Id == manager.Id)).BranchId);
    }

    private sealed class HasBody : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class Unrestricted : ISubscriptionProvider
    {
        public SubscriptionAccess GetAccess() => new("Test", "Unrestricted", true, null, null, null, null, DateTimeOffset.UtcNow);
    }

    private sealed class Algeria : IRegionalSettingsProvider
    {
        public RegionalSettings Get() => new()
        {
            CountryCode = "DZ", CurrencyCode = "DZD", CurrencyDecimals = 2, TimeZone = "Africa/Algiers"
        };
    }
}
