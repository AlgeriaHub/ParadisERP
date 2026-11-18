using System.Net;
using System.Text;
using System.Security.Claims;
using AlgeriaPos.Application.Files;
using AlgeriaPos.Application.Integrations;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Application.Security;
using AlgeriaPos.Application.Subscriptions;
using AlgeriaPos.Infrastructure.Subscriptions;
using AlgeriaPos.Infrastructure.Tenancy;
using AlgeriaPos.Infrastructure.Platform;
using AlgeriaPos.API.Identity;
using AlgeriaPos.API.Endpoints;
using AlgeriaPos.API.Security;
using AlgeriaPos.Infrastructure.Auth;
using AlgeriaPos.Infrastructure.Files;
using AlgeriaPos.Infrastructure.Integrations;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Seeding;
using AlgeriaPos.Infrastructure.Sync;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Machine-specific settings (a local SQL Server connection string, say) live in this gitignored
// file. Environment variables and command-line arguments are re-added after it so they still
// win, which is the precedence every deployment already relies on.
builder.Configuration
    .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

// ---------- error monitoring ----------
// Sentry, only where Sentry:Dsn is set — the deployed site. Development and the tests leave it
// unset and send nothing. Unhandled exceptions and every LogError become events; a tenth of
// requests are traced, joined to the till's trace when the call came from the web app.
if (!string.IsNullOrWhiteSpace(builder.Configuration["Sentry:Dsn"]))
    builder.WebHost.UseSentry(options =>
    {
        options.TracesSampleRate = builder.Configuration.GetValue("Sentry:TracesSampleRate", 0.1);
        // Nothing about people: no user, IP, cookies or headers (the bearer token among them),
        // and never a request body — sales, customers and ID scans stay in the company's database.
        options.SendDefaultPii = false;
        options.MaxRequestBodySize = Sentry.Extensibility.RequestSize.None;
    });

// ---------- database ----------
// "Sqlite" runs anywhere with no server, for development, CI and the tests;
// "SqlServer" is production, where several branches share one central server.
var provider = PosDbProviders.Normalize(builder.Configuration.GetValue("Database:Provider", PosDbProviders.Sqlite))
               ?? throw new InvalidOperationException("Database:Provider must be SqlServer or Sqlite.");
var connectionString = builder.Configuration.GetConnectionString("Default")
                       ?? "Data Source=pos.db";
var startupWait = TimeSpan.FromSeconds(Math.Max(0, builder.Configuration.GetValue("Database:StartupWaitSeconds", 60)));

var platform = builder.Configuration.GetSection("Platform").Get<PlatformOptions>() ?? new();
platform.Host = platform.Host.Trim().ToLowerInvariant();
var configuredTenants = builder.Configuration.GetSection("Tenancy:Companies").Get<List<TenantOptions>>() ?? [];
// Hosted mode: companies come from configuration and/or the operator portal (which may start empty).
var multiCompany = configuredTenants.Count > 0 || platform.Enabled;

// The one deployment mistake that loses data is running the company on a database nobody is
// backing up, and it is completely silent: the image used to bake
// ConnectionStrings__Default="Data Source=/data/pos.db", so a compose file that forgot
// Database__Provider served every till from a file inside the container while the nightly
// backup faithfully dumped an empty server database. The image no longer bakes it, and outside
// Development a file database now has to be asked for by name. Development is exempt because
// that is what it runs on; "not Development" rather than "Production" so a Staging copy is
// covered too.
if (!multiCompany && !builder.Environment.IsDevelopment() && provider == PosDbProviders.Sqlite
    && !builder.Configuration.GetValue("Database:AllowSqliteInProduction", false))
    throw new InvalidOperationException(
        $"Database:Provider is Sqlite in the {builder.Environment.EnvironmentName} environment. Production runs on SQL Server: set " +
        "Database__Provider=SqlServer and ConnectionStrings__Default. If a single-file database really is what you want " +
        "(CI, a demo, a restore drill), say so with Database__AllowSqliteInProduction=true.");

// A real company is bootstrapped with credentials its operator supplies. The demo company —
// fake fiscal identifiers, ten products, and opening-balance ledger entries the ledger can
// never delete — is a Development convenience and reaches nothing else unless it is asked for
// by name. Both are read only when the Users table turns out to be empty, so the upgrade that
// ships this change does not lock any existing deployment out of its own database.
var bootstrapUsername = builder.Configuration["Bootstrap:AdminUsername"]?.Trim() is { Length: > 0 } configured
    ? configured : "admin";
var bootstrapPassword = builder.Configuration["Bootstrap:AdminPassword"];
var seedDemoData = builder.Configuration.GetValue("Bootstrap:SeedDemoData", builder.Environment.IsDevelopment());

if (!multiCompany)
    configuredTenants.Add(new TenantOptions
    {
        Id = "default", DatabaseProvider = provider, ConnectionString = connectionString,
        Subscription = builder.Configuration.GetSection("Subscription").Get<SubscriptionOptions>() ?? new(),
        Country = builder.Configuration["Country"],
        Regional = builder.Configuration.GetSection("Regional").Get<RegionalSettings>(),
        AdminUsername = bootstrapUsername,
        BootstrapAdminPassword = bootstrapPassword
    });
var tenants = new TenantRegistry(configuredTenants, singleCompany: !multiCompany,
    reservedHost: platform.Enabled ? platform.Host : null);
builder.Services.AddSingleton(tenants);
builder.Services.AddScoped<TenantContext>();

// ---------- files ----------
// Logos and contract papers live in a folder, one per company, and the database keeps only their
// paths (owner, 2026-10-04). Relative roots hang off the content root, like the keys folder.
var filesRoot = Path.Combine(builder.Environment.ContentRootPath,
    builder.Configuration["Files:Root"] is { Length: > 0 } configuredFilesRoot ? configuredFilesRoot : FileStoreOptions.DefaultRoot);
builder.Services.AddSingleton(new FileStoreOptions { Root = filesRoot });
builder.Services.AddScoped<IFileStore>(services =>
    LocalFileStore.ForCompany(filesRoot, services.GetRequiredService<TenantContext>().Current.Id));

builder.Services.AddDbContext<PosDbContext>((services, options) =>
{
    var company = services.GetRequiredService<TenantContext>().Current;
    PosDbProviders.Configure(options, company.DatabaseProvider, company.ConnectionString);
});

// ---------- auth ----------
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.Key) || jwt.Key.Length < 32)
{
    if (builder.Environment.IsDevelopment())
        jwt.Key = "dev-only-signing-key-change-me-please-32+chars";
    else
        throw new InvalidOperationException("Jwt:Key must be set to at least 32 characters in production.");
}
// A key from the repository's own files is a key everyone has (security review, 2026-10-10): with it
// anyone can sign a token for any user. Refused outside Development, where it is the default above.
if (!builder.Environment.IsDevelopment() && JwtKeys.IsPublicPlaceholder(jwt.Key))
    throw new InvalidOperationException("Jwt:Key is a placeholder from the repository; set a long random secret of your own.");
builder.Services.AddSingleton(jwt);
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<TwoFactor>();
// Singleton: the count of recent failures per account has to outlive the request that made one.
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddScoped<SyncService>();
// Scoped, so the chart of accounts is read once per request rather than once per posting: a
// push of 500 sales would otherwise read it 500 times.
builder.Services.AddScoped<LedgerPoster>();
builder.Services.AddScoped<LedgerReports>();
// Renting: back-office documents, the stock they move and the entries they post.
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Rentals.RentalDocuments>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Rentals.CustomerQuantityStatements>();
// The transfer workflow (owner, 2026-10-12): dispatch, receive, loss, cancel.
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Catalog.StockTransfers>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Rentals.RentalAttachments>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Reports.ItemStatusReport>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Valuation.StockValuationShadow>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Catalog.ReceivingCostPrice>();
// How sales are costed (C-01): the accountant's switch, its opening adjustment (C-02) and the gaps (C-16).
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Ledger.SalesCostSwitch>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Ledger.CostPriceRevaluationPosting>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Manufacturing.ManufacturingCostingChoices>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Manufacturing.PracticeLedgerReport>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Manufacturing.ManufacturingLedgerPostings>();
// Maintenance, Phase M0: the settings only (docs/maintenance/plan.md §4).
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Maintenance.MaintenanceSettingsStore>();
// Maintenance, Phase M1: jobs over units in maintenance (plan §2.2).
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Maintenance.MaintenanceJobs>();
// Maintenance, Phase M2: parts issued and returned, outside work linked to vouchers (plan §2.3, §2.4).
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Maintenance.MaintenanceJobParts>();
// Maintenance, Phase M5a: customer jobs, their price and invoice, and the MS-23 repair rates (plan §3.2).
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Maintenance.MaintenanceCustomerJobs>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Maintenance.MaintenanceRepairRates>();
// Manufacturing, Phase 1: lines, units, materials, job roles, stage templates, recipes and access.
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Manufacturing.ManufacturingSettings>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Manufacturing.ProductionLines>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Manufacturing.BillsOfMaterials>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Manufacturing.ManufacturingGrants>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Authorization.RoleEditor>();
// Manufacturing, Phase 2. Stock operations stay off unless this server's configuration says otherwise
// (Manufacturing:StockOperations): switched on for the live company only with the accountant's sign-off.
// C-12 (2026-11-09): the accountant's panel switch, or this server's configuration, read once per request.
var stockOperationsByConfiguration = builder.Configuration.GetValue<bool>(AlgeriaPos.Infrastructure.Manufacturing.ManufacturingOptions.Section);
builder.Services.AddScoped(sp => new AlgeriaPos.Infrastructure.Manufacturing.ManufacturingOptions(
    stockOperationsByConfiguration || AlgeriaPos.Infrastructure.Manufacturing.ManufacturingCostingChoices
        .StockOperations(sp.GetRequiredService<AlgeriaPos.Infrastructure.Persistence.PosDbContext>()),
    stockOperationsByConfiguration));
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Manufacturing.AttendanceCapacity>();
// Maintenance, Phase M3: hours on jobs, in production's LaborBookings under one attendance ceiling (plan §2.5).
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Maintenance.MaintenanceLabour>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Maintenance.MaintenanceWorkshop>();
// Maintenance, Phase M4: a job's cost through the manufacturing cost engine, and the cost reports (plan §3.1, §6.2).
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Maintenance.MaintenanceCosting>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Maintenance.MaintenanceAccountMap>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Maintenance.MaintenancePracticeLedgerReport>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Maintenance.MaintenanceLedgerPostings>();
// REN-12, Phase M7a: the fixed-asset register of hire equipment — settings, switch-over, register and depreciation.
builder.Services.AddScoped<AlgeriaPos.Infrastructure.FixedAssets.FixedAssetSettingsStore>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.FixedAssets.FixedAssetSwitchOver>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.FixedAssets.FixedAssetRegisterReport>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.FixedAssets.FixedAssetDepreciationReport>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.FixedAssets.FixedAssetLedgerPostings>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Manufacturing.ProductionOrders>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Manufacturing.ProductionMovements>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Manufacturing.ProductionLogs>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Manufacturing.ManufacturingReports>();
// Manufacturing costing, Phase A: read-only cost previews, off unless this server's configuration says
// Manufacturing:Costing. It writes nothing; posting waits for the accountant (docs/manufacturing/costing-plan.md).
builder.Services.AddSingleton(new AlgeriaPos.Infrastructure.Manufacturing.ManufacturingCostingOptions(
    builder.Configuration.GetValue<bool>(AlgeriaPos.Infrastructure.Manufacturing.ManufacturingCostingOptions.Section)));
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Manufacturing.LabourRates>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Manufacturing.ProductionCosts>();
// B1 freezes an explicitly requested operational cost sheet. It does not value inventory, post a journal,
// or make an accountant-approved cost available.
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Manufacturing.ProductionCostSnapshots>();
// B2 reads B1's sheets and reconciles them; it writes nothing.
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Manufacturing.ProductionCostHistory>();
// Who signs for a company customer, as a dated history with one current entry.
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Customers.CustomerRepresentatives>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Customers.CustomerAttachments>();
// The balance a customer starts with on the system, and its corrections (owner, 2026-10-10).
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Customers.CustomerOpeningBalances>();
// The suppliers: their accounts on 401, opening balances and statements (owner, 2026-10-10).
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Suppliers.SupplierAccounts>();
// Invoices, receipts and credit notes, and the two reports the cash flow runs on.
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Receivables.ReceivableDocuments>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Receivables.CustomerStatements>();
// M7b: where a customer advance is held and when its VAT is due (the accountant's settings).
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Receivables.ReceivableSettingsStore>();
// Money going out: payment vouchers, approved before they are paid.
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Receivables.PaymentVouchers>();
// The payroll figures: the one row every payslip is computed against.
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Payroll.PayrollSettingsStore>();
// The staff file and the month's attendance: who is paid, and for how many days.
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Hr.EmployeeDirectory>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Hr.AttendanceSheet>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Hr.OvertimeEntries>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Hr.AttendanceReview>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Hr.PunchImportPreview>();
// The run itself, and the cash that leaves on it (advances, wages, the statutory funds).
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Payroll.PayrollDocuments>();
builder.Services.AddScoped<AlgeriaPos.Infrastructure.Payroll.WageMoney>();
builder.Services.AddScoped<IUserSessionValidator, UserSessionValidator>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ISubscriptionProvider, ConfiguredSubscriptionProvider>();
builder.Services.AddScoped<IRegionalSettingsProvider, TenantRegionalSettingsProvider>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Use the raw JWT claim names rather than the legacy WS-Federation URIs. Relying on
        // inbound claim-type mapping is fragile across IdentityModel versions, and if the
        // default ever flips, every role-protected endpoint would 403 for everyone.
        options.MapInboundClaims = false;
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var principal = context.Principal!;
                var services = context.HttpContext.RequestServices;
                var aborted = context.HttpContext.RequestAborted;
                var tenant = services.GetRequiredService<TenantContext>();
                if (!tenant.IsResolved
                    || principal.FindFirstValue("tenant") != tenant.Current.Id
                    || !Guid.TryParse(principal.FindFirstValue("sub"), out var userId)
                    || !long.TryParse(principal.FindFirstValue("user_version"), out var version)
                    || await services.GetRequiredService<IUserSessionValidator>()
                        .CheckAsync(userId, version, principal.FindFirstValue("role") ?? "",
                            principal.FindFirstValue("device") ?? "", aborted) is not { } session)
                {
                    context.Fail("Session revoked. Sign in again.");
                    return;
                }

                // AUTH-1: what this caller may do and on which data — from the role tables, never the
                // token. The session check read the user's authorization stamp now; the permissions
                // are loaded only when the stamp moved since they were cached (Step 3). No role, no access.
                if (session.Authorization is not { } stamp)
                {
                    context.Fail("This account has no role. Ask the administrator.");
                    return;
                }
                var db = services.GetRequiredService<PosDbContext>();
                var decided = await services.GetRequiredService<AlgeriaPos.Infrastructure.Authorization.AuthorizationCache>()
                    .GetAsync(tenant.Current.Id, userId, stamp,
                        ct => AlgeriaPos.Infrastructure.Authorization.ServerAuthorization.LoadAsync(db, userId, stamp, ct), aborted);
                principal.AddIdentity(AlgeriaPos.Infrastructure.Authorization.ServerAuthorization.CreateIdentity(decided));
            }
        };
        options.TokenValidationParameters = new TokenValidationParameters
        {
            NameClaimType = "name",
            RoleClaimType = "role",
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ClockSkew = TimeSpan.FromMinutes(2)
        };
    });
builder.Services.AddAuthorization();
// How much one caller may ask and how big one request may be (security review, 2026-10-10): the
// site is served straight from IIS with no proxy in front, so the limits live here (RequestLimits).
builder.Services.AddRequestLimits(builder.Configuration, out var requestLimits);
// Kestrel's own ceiling is the largest body any route accepts; RequestLimits lowers it per route.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize =
    Math.Max(requestLimits.UploadBodyBytes, Math.Max(requestLimits.BulkBodyBytes, requestLimits.DefaultBodyBytes)));
// The one handler every route permission goes through: it reads the server's identity, never the token.
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, AlgeriaPos.API.Authorization.PermissionHandler>();
builder.Services.AddSingleton<AlgeriaPos.Infrastructure.Authorization.AuthorizationCache>();
var sso = builder.Configuration.GetSection("Sso").Get<SsoOptions>() ?? new();
if (platform.Enabled)
{
    if (!multiCompany || !sso.Enabled || Uri.CheckHostName(platform.Host) == UriHostNameType.Unknown
        || tenants.Resolve(platform.Host) is not null || platform.OperatorSubjects.Length == 0)
        throw new InvalidOperationException("The platform requires hosted companies, SSO, a separate hostname, and operator subject IDs.");
    var platformDatabase = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(platform.ConnectionString);
    if (string.IsNullOrWhiteSpace(platformDatabase.DataSource) || platformDatabase.DataSource == ":memory:"
        || tenants.Tenants.Any(x => x.DatabaseProvider == PosDbProviders.Sqlite && string.Equals(Path.GetFullPath(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(x.ConnectionString).DataSource), Path.GetFullPath(platformDatabase.DataSource), StringComparison.OrdinalIgnoreCase)))
        throw new InvalidOperationException("The platform needs a separate persistent database.");
    builder.Services.AddDbContext<PlatformDbContext>(options => options.UseSqlite(platform.ConnectionString));
    builder.Services.AddScoped<PlatformStore>();
    builder.Services.AddScoped<ILanguagePackStore, PlatformLanguagePackStore>();
    var platformDatabasePath = Path.GetFullPath(platformDatabase.DataSource);
    builder.Services.AddSingleton(new ProvisioningOptions
    {
        PlatformDatabasePath = platformDatabasePath,
        FilesRoot = filesRoot,
        CompanyDataDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(platform.CompanyDataDirectory)
            ? Path.Combine(Path.GetDirectoryName(platformDatabasePath)!, "companies")
            : platform.CompanyDataDirectory)
    });
    builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
    builder.Services.AddScoped<CompanyProvisioner>();
    builder.Services.AddScoped<PlatformIntegrationStore>();
    builder.Services.AddScoped<ICompanyIntegrationsProvider, PlatformIntegrationsProvider>();
}
else
{
    builder.Services.AddSingleton<ICompanyIntegrationsProvider>(new ConfiguredIntegrationsProvider(
        builder.Configuration.GetSection("Integrations").Get<IntegrationSettings>(),
        builder.Configuration.GetSection("IntegrationSecrets").Get<IntegrationSecrets>()));
    builder.Services.AddSingleton<ILanguagePackStore>(new ConfiguredLanguagePackStore(
        builder.Configuration.GetSection("LanguagePacks").Get<List<LanguagePack>>() ?? []));
}
builder.Services.AddPosSso(sso, platform, builder.Environment.IsDevelopment());

// ---------- integrations (operator-owned: card terminal, receipts, sale webhook) ----------
builder.Services.AddDataProtection();
builder.Services.AddHttpClient(IntegrationHttp.ClientName, client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<IntegrationHttp>();
builder.Services.AddSingleton<SmtpReceiptSender>();
builder.Services.AddSingleton<ReceiptSendLimiter>();
builder.Services.AddSingleton<IntegrationSecretProtector>();
builder.Services.AddScoped<TenantIntegrations>();
builder.Services.AddSingleton(builder.Configuration.GetSection("IntegrationOutbox").Get<IntegrationOutboxOptions>() ?? new());
builder.Services.AddHostedService<IntegrationOutboxWorker>();

// Sign-in cookies and antiforgery tokens are protected with these keys; keeping them on a
// volume means replacing the container does not sign every operator out. A relative path is
// taken from the content root, not the working directory: under IIS that is the worker
// process's own folder, which a shared-hosting site cannot write to.
var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(keysPath))
    builder.Services.AddDataProtection().SetApplicationName("AlgeriaPos")
        .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, keysPath)));

// ---------- cors ----------
const string CorsPolicy = "pos-clients";
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                     ?? new[] { "http://localhost:4200" };
builder.Services.AddCors(options => options.AddPolicy(CorsPolicy, policy => policy
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "POS API", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

// Sales are projected to flat shapes before serialisation, but this is a cheap guard so a
// future handler returning an entity graph degrades instead of throwing mid-response.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles);

// A health check that only proves the process is alive is worse than none: it stays green with
// the database down, so the monitor says the shop is fine while no till can sell.
//
// Not AddDbContextCheck<PosDbContext>, though that is what §6.0 names: the health service runs
// every check in a scope of its own, and the registered PosDbContext reads the company out of
// the *request's* TenantContext — so the framework check throws "Company has not been resolved"
// before it reaches the database. The connection is opened against the configured company
// directly instead, which is also the only thing a probe arriving without a company hostname
// could have meant.
// One database check at a time (security review, 2026-10-10): /health is anonymous, and opening a
// connection on every call let a flood of probes use up the connection pool of the shared SQL
// Server that every till and screen also needs. Probes that arrive while a check is running share
// its answer; one that arrives after it gets a fresh one, so the answer is never stale.
var healthLock = new SemaphoreSlim(1, 1);
HealthCheckResult? healthAnswer = null;
var healthAnsweredAt = DateTime.MinValue;
builder.Services.AddHealthChecks().AddAsyncCheck("database", async ct =>
{
    var checkedCompany = multiCompany ? null : configuredTenants[0];
    // A hosted install has as many databases as it has companies, and the operator hostname is
    // where that question belongs; here the process being up is the whole answer.
    if (checkedCompany is null) return HealthCheckResult.Healthy();
    var asked = DateTime.UtcNow;
    await healthLock.WaitAsync(ct);
    try
    {
        if (healthAnswer is HealthCheckResult shared && healthAnsweredAt >= asked)
            return shared;
        var options = new DbContextOptionsBuilder<PosDbContext>();
        PosDbProviders.Configure(options, checkedCompany.DatabaseProvider, checkedCompany.ConnectionString);
        await using var probe = new PosDbContext(options.Options);
        healthAnswer = await probe.Database.CanConnectAsync(ct)
            ? HealthCheckResult.Healthy()
            // No connection string and no provider message: /health is anonymous.
            : HealthCheckResult.Unhealthy("The company database did not answer.");
        healthAnsweredAt = DateTime.UtcNow;
        return healthAnswer.Value;
    }
    finally { healthLock.Release(); }
});

// Behind Caddy the API sees the proxy's address and http, so `Request.Scheme` is wrong — which
// matters the day SSO is enabled, because the OIDC redirect URI is built from it. Off by
// default: forwarding headers are attacker-controlled on a directly exposed API.
var proxyEnabled = builder.Configuration.GetValue("Proxy:Enabled", false);
if (proxyEnabled)
{
    var trustedNetworks = builder.Configuration.GetSection("Proxy:TrustedNetworks").Get<string[]>() ?? [];
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
                                   | ForwardedHeaders.XForwardedHost;
        // Both lists are cleared first, and that is load-bearing rather than tidying. The
        // defaults trust loopback only, and a proxy in another container never comes from
        // loopback — it arrives from the compose bridge on an address nobody chose. With this
        // left at the default the headers were read and then silently discarded, so
        // Proxy__Enabled=true looked switched on and changed nothing (found by asking the
        // running image, not by reading it). Empty lists mean the middleware does no address
        // check at all: behind a reverse proxy that is the only thing that can reach the API.
        // A deployment that does expose the API names the proxy. (Under IIS, as on SmarterASP,
        // there is no proxy hop: leave Proxy__Enabled off.)
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var network in trustedNetworks)
        {
            var slash = network.IndexOf('/');
            if (slash > 0 && IPAddress.TryParse(network[..slash], out var prefix)
                          && int.TryParse(network[(slash + 1)..], out var prefixLength))
                options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, prefixLength));
            else if (IPAddress.TryParse(network, out var proxy))
                options.KnownProxies.Add(proxy);
            else
                throw new InvalidOperationException(
                    $"Proxy:TrustedNetworks contains '{network}', which is neither an IP address nor a CIDR range.");
        }
    });
}

// Every sync payload is repetitive JSON (catalog rows sharing keys, three name languages per
// row), which compresses very well — the single highest-leverage change for a shop on a weak
// ADSL/mobile line. Costs nothing on a fast connection.
builder.Services.AddResponseCompression(options => options.EnableForHttps = true);

// Https:Redirect — set by a deployment that serves HTTPS itself (SmarterASP). A till opened over
// plain http cannot sell (the browser withholds crypto.randomUUID outside a secure context) nor
// work offline (no service worker), so http is sent on to https for good, and HSTS keeps every
// browser that has been there once on https without asking. Off everywhere else: development
// and the tests run on http.
var httpsRedirect = builder.Configuration.GetValue("Https:Redirect", false);
if (httpsRedirect)
{
    builder.Services.AddHttpsRedirection(options =>
    {
        options.RedirectStatusCode = StatusCodes.Status308PermanentRedirect;
        options.HttpsPort = 443;
    });
    builder.Services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(180));
}

var app = builder.Build();

// Say out loud which database this process opened, on every start. Never the password: the
// point is to catch the wrong server or the wrong schema name, and an operator reading
// `docker compose logs pos` should not have to think about what they are pasting into a ticket.
if (!multiCompany)
    app.Logger.LogInformation("Company database — provider {Provider}, {Connection}",
        provider, DescribeConnection(connectionString));

if (platform.Enabled)
{
    using var scope = app.Services.CreateScope();
    var platformDb = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
    await platformDb.Database.MigrateAsync();
    // Companies created in the portal join the configured ones before anything is migrated.
    var provisioned = await CompanyProvisioner.LoadAsync(platformDb, scope.ServiceProvider.GetRequiredService<ISecretProtector>(), CancellationToken.None);
    if (provisioned.Count > 0) tenants.Replace([.. tenants.Tenants, .. provisioned]);
}

// A configured profile may only enable uploaded languages; fail at startup rather than at the till.
{
    using var scope = app.Services.CreateScope();
    var available = await scope.ServiceProvider.GetRequiredService<ILanguagePackStore>().RevisionsAsync(CancellationToken.None);
    foreach (var company in tenants.Tenants)
    {
        var missing = company.ConfiguredRegional().Languages
            .Where(x => !RegionalSettings.BuiltinLanguages.Contains(x) && !available.ContainsKey(x)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"Company '{company.Id}' enables languages without a pack: {string.Join(", ", missing)}.");
    }
}

// ---------- migrate + seed ----------
// Archived companies keep their database untouched until they are restored.
foreach (var company in tenants.Tenants.Where(x => !x.Archived))
{
    using var scope = app.Services.CreateScope();
    var startupRegional = platform.Enabled
        ? (await scope.ServiceProvider.GetRequiredService<PlatformStore>().ReadRegionalAsync(company.Id, CancellationToken.None)).Settings
        : null;
    scope.ServiceProvider.GetRequiredService<TenantContext>().Initialize(company, regional: startupRegional);
    _ = scope.ServiceProvider.GetRequiredService<ISubscriptionProvider>().GetAccess();
    var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();

    // A host reboot starts the API and its database server at the same moment, and the server takes longer; the
    // API would die on the first migration and rely on the restart policy to keep trying,
    // which reads as a crash loop in the logs of the machine nobody is watching. Deliberately
    // not EnableRetryOnFailure: that does not wrap a user transaction, and BeginWriteScopeAsync
    // opens one around every write in this system — it would retry the pings and leave the
    // sales unprotected. Only SQL Server waits: SQLite has no server to come up.
    if (company.DatabaseProvider == PosDbProviders.SqlServer && startupWait > TimeSpan.Zero)
    {
        var deadline = DateTime.UtcNow + startupWait;
        while (!await db.Database.CanConnectAsync() && DateTime.UtcNow < deadline)
        {
            app.Logger.LogInformation("Waiting for the database of company '{Company}' to accept connections…", company.Id);
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        // The wait running out is not itself the error. Fall through and let Migrate raise the
        // provider's own message, which names the host and the reason.
    }

    // Prefer migrations: EnsureCreated builds the schema once and then never upgrades it,
    // which on a shop machine you cannot easily reach is the worst failure mode there is.
    // Generate them with: dotnet ef migrations add Initial -p AlgeriaPos.Infrastructure -s AlgeriaPos.API
    if (db.Database.GetMigrations().Any())
    {
        // Through FileStoreUpgrade, never MigrateAsync alone: an older database still holds files
        // that have to reach the files folder before the migration that drops them.
        await FileStoreUpgrade.MigrateAsync(db, scope.ServiceProvider.GetRequiredService<IFileStore>());
    }
    else if (app.Environment.IsDevelopment())
    {
        await db.Database.EnsureCreatedAsync();
    }
    else
    {
        throw new InvalidOperationException(
            "No EF Core migrations found. Run 'dotnet ef migrations add Initial -p AlgeriaPos.Infrastructure -s AlgeriaPos.API' " +
            "before deploying, so the schema has an upgrade path.");
    }

    // WAL lets pulls/reports (readers) proceed while a sale/catalog edit (writer) is mid-
    // transaction, instead of blocking behind it — asserted explicitly rather than relying on
    // whatever a future Microsoft.Data.Sqlite version defaults to. A busy_timeout gives a
    // second concurrent writer a chance to retry instead of failing immediately.
    if (company.DatabaseProvider == PosDbProviders.Sqlite)
    {
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;");
    }

    // AUTH-1 Step 1: refuse to start with an unclassified permission, create the system roles,
    // hold the Admin role to its baseline, and move every user onto a role. Before the seeders,
    // so the roles exist when they write the first users. Idempotent: says so when it did nothing.
    var authz = await AlgeriaPos.Infrastructure.Authorization.AuthorizationBootstrap.RunAsync(db);
    if (authz.ChangedAnything)
        app.Logger.LogInformation("Roles and permissions for company '{Company}': {Result}.", company.Id, authz);

    if (multiCompany)
    {
        await CompanySeeder.SeedAsync(db, company);
    }
    else if (seedDemoData)
    {
        // Development, or a demo asked for by name: the whole fictional building-supplies
        // company, with an operator-supplied administrator password. Returns early once the company has users.
        await DbSeeder.SeedAsync(db, company.ConfiguredRegional(), bootstrapPassword);
    }
    else if (await db.Users.AnyAsync())
    {
        // An upgrade. The company already has its own admin, so nothing here is read — which is
        // the whole reason the check is on the table and not on the configuration. Requiring
        // Bootstrap__AdminPassword unconditionally would sign every running deployment out of
        // its own database on the release that shipped this.
    }
    else
    {
        // A first real start. CompanySeeder writes the minimum and nothing else: one admin, the
        // store, the main branch, terminal C01 — no demo catalogue, and above all no opening
        // ledger entries, which are the rows that could never be taken back out.
        if (bootstrapPassword is not { Length: >= 8 })
            throw new InvalidOperationException(
                "This database has no users yet, so the first administrator has to be named. Set Bootstrap__AdminPassword " +
                $"(at least 8 characters) and, if '{bootstrapUsername}' is not the username you want, Bootstrap__AdminUsername. " +
                "Remove Bootstrap__AdminPassword from the environment once the first sign-in has worked.");
        await CompanySeeder.SeedAsync(db, company);
        app.Logger.LogInformation("Bootstrapped company '{Company}' with administrator '{Admin}' and terminal C01.",
            company.Id, bootstrapUsername);
    }

    // The rows the system cannot function without, whichever way the company was created.
    // CompanySeeder provisions only the operator-supplied minimum — an admin, a store, a main
    // branch — so a company created from the portal had no payment methods and no cash box,
    // and its till could not take money at all. DbSeeder calls this too and it is idempotent,
    // so running it here unconditionally costs three small queries and removes the chance of
    // a future creation path forgetting it again.
    // First the two places every company has — the main branch and a warehouse (owner,
    // 2026-10-05) — so the cash boxes below follow them: the main branch's is the central safe.
    var places = await DefaultBranches.EnsureAsync(db);
    if (places.ChangedAnything)
        app.Logger.LogInformation("Branches for company '{Company}': {Result}.", company.Id, places);
    await CoreDataSeeder.EnsureAsync(db);

    // The weighted-average shadow's opening (P0.4b), once, after every seeder so the stock it counts is
    // all there, and before anything is served so no movement falls between it and the first costed one.
    var valuationOpening = await StartupCorrection.RunAsync(db, app.Logger, "stock valuation opening", company.Id,
        () => AlgeriaPos.Infrastructure.Valuation.StockValuationStart.EnsureAsync(db));
    if (valuationOpening is int openingRows)
        app.Logger.LogInformation("Stock valuation shadow opened for company '{Company}' with {Rows} rows.", company.Id, openingRows);

    // Both corrections below run through StartupCorrection: one that refuses a record it cannot
    // trace is logged and tried again next start, instead of keeping every company's site down.
    var correctedRentals = await StartupCorrection.RunAsync(db, app.Logger, "daily rental pricing", company.Id,
        () => new AlgeriaPos.Infrastructure.Rentals.RentalDailyPricingUpgrade(db,
            scope.ServiceProvider.GetRequiredService<IRegionalSettingsProvider>()).ApplyAsync());
    if (correctedRentals > 0)
        app.Logger.LogInformation("Corrected daily rental pricing for {Count} historical contracts in company {Company}.", correctedRentals, company.Id);

    // One-time correction for the sales taken while the till netted change off twice, leaving
    // every branch recorded short by the change it had handed out. It measures the shortfall,
    // posts one dated correction per branch and never runs again — the entry carries a fixed
    // id per branch, so the ledger's own duplicate guard is what makes it idempotent. A
    // company with nothing to correct writes nothing.
    var restated = await StartupCorrection.RunAsync(db, app.Logger, "change restatement", company.Id,
        () => ChangeRestatement.ApplyAsync(db, new LedgerPoster(db))) ?? [];
    foreach (var (branchId, amount) in restated)
        app.Logger.LogInformation(
            "Cash restated for branch {BranchId}: {Amount} that had been deducted twice.", branchId, amount);

    // Phones saved before the mobile rule (owner, 2026-11-17) take the stored form,
    // 06.58.41.77.30 → +213658417730; one it cannot read as a mobile stays as typed for a person
    // to fix ("Phone to fix" on the customers list). Reads only phones not yet in that form.
    var phones = await StartupCorrection.RunAsync(db, app.Logger, "customer phones", company.Id,
        () => new AlgeriaPos.Infrastructure.Customers.CustomerPhoneCorrection(db,
            scope.ServiceProvider.GetRequiredService<IRegionalSettingsProvider>()).ApplyAsync());
    if (phones > 0)
        app.Logger.LogInformation("Rewrote {Count} customer phones in international form for company {Company}.", phones, company.Id);
}

// First in the pipeline, so everything below — the tenant lookup, the SSO redirect URIs, the
// access log — sees the client's address and scheme rather than the proxy's.
if (proxyEnabled) app.UseForwardedHeaders();

// Right after the scheme is known, before anything is served over http.
if (httpsRedirect)
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseResponseCompression();
app.UseCors(CorsPolicy);

// CLO-01: an entry dated inside closed books is refused wherever it was asked for — the poster throws before
// anything is saved — and the screens say so in words, with the day the books are closed through.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (AlgeriaPos.Domain.PeriodClosedException closed) when (!context.Response.HasStarted)
    {
        context.Response.Clear();
        await Results.Problem(closed.Message, statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "periodClosed",
                ["closedThrough"] = closed.ClosedThrough.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            }).ExecuteAsync(context);
    }
});

// Headers every response carries, whichever hostname served it.
//
// Framing is the one that matters here. The operator portal signs in with a cookie and
// protects its writes with an antiforgery token, which stops a forged cross-site request but
// does nothing about a real one: framed invisibly, the portal's own scripts send the token
// with whatever the operator was tricked into clicking, and those clicks suspend companies and
// rewrite hostnames. `frame-ancestors` is the control that applies, and it is stated twice
// because older browsers only understand the legacy header. Since the security review of
// 2026-10-10 the policy also limits scripts and connections to this site
// (Security/ContentSecurityPolicy.cs); the inline theme script is allowed by its hash, read from
// the published index.html once at start-up.
//
// Deliberately no HSTS: hosted companies terminate TLS at the reverse proxy, which is where it
// belongs, and a single-shop install may legitimately run over http on the shop's own LAN.
// Sending it from here would lock such an install out of its own till.
var contentSecurityPolicy = AlgeriaPos.API.Security.ContentSecurityPolicy.For(app.Environment.WebRootPath);
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Content-Security-Policy"] = contentSecurityPolicy;
    headers["Referrer-Policy"] = "no-referrer";
    await next(context);
});

app.Use(async (context, next) =>
{
    if (platform.Enabled && string.Equals(context.Request.Host.Host, platform.Host, StringComparison.OrdinalIgnoreCase))
    {
        if (context.Request.Path.StartsWithSegments("/api")
            && !context.Request.Path.StartsWithSegments("/api/platform")
            && !context.Request.Path.StartsWithSegments("/api/auth/sso"))
        { context.Response.StatusCode = 404; return; }
        if (context.Request.Path == "/api/auth/sso/session") { context.Response.StatusCode = 404; return; }
        await next(context);
        return;
    }
    if (context.Request.Path.StartsWithSegments("/api/platform")) { context.Response.StatusCode = 404; return; }
    var company = tenants.Resolve(context.Request.Host.Host);
    if (company is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    SubscriptionOptions? subscription = null;
    RegionalSettings? regional = null;
    if (platform.Enabled)
    {
        var platformStore = context.RequestServices.GetRequiredService<PlatformStore>();
        subscription = (await platformStore.ReadAsync(company.Id, context.RequestAborted)).Settings;
        regional = (await platformStore.ReadRegionalAsync(company.Id, context.RequestAborted)).Settings;
    }
    context.RequestServices.GetRequiredService<TenantContext>().Initialize(company, subscription, regional);
    await next(context);
});
// Serving the built Angular PWA from the API keeps deployment to a single process,
// which matters when the shop has one machine and a flaky connection.
//
// Two of those files must never be cached. Everything Angular emits carries a content hash in
// its name and may be held for a year, but `index.html` names the hashed bundles and
// `ngsw.json` names everything the service worker should hold — so a till that cached either
// one keeps running yesterday's bundle against today's API, and the only cure is clearing the
// browser's storage on a machine in another town. It is set here, not in a proxy, so it is
// true however the app is served.
var staticFiles = new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        var name = context.File.Name;
        if (name.Equals("index.html", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ngsw.json", StringComparison.OrdinalIgnoreCase))
            context.Context.Response.Headers.CacheControl = "no-cache";
    }
};
app.UseDefaultFiles();
app.UseStaticFiles(staticFiles);
// Explicit, and after static files: with the implicit routing at the start of the pipeline the
// SPA fallback endpoint is matched first, and static files then skip every script request.
app.UseRouting();
// Before authentication: a refused request costs no token validation and no database read.
app.UseRequestBodyLimits(requestLimits);
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Answers 200 Healthy or 503 Unhealthy, with a body an operator can read at the end of an SSH
// session. The scheme is in it on purpose: it is how you tell, without enabling SSO first,
// whether the proxy's X-Forwarded-Proto is reaching the app (§6.2). No exception text — an
// anonymous route says what is wrong, never where.
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            scheme = context.Request.Scheme,
            checks = report.Entries.ToDictionary(x => x.Key, x => x.Value.Status.ToString())
        }), context.RequestAborted);
    }
});
// Every route, each requiring what RoutePermissions says (AUTH-1). The endpoints are built here,
// once, so a company route the table does not name stops the start-up instead of the first request.
app.MapApi(sso, platform);
_ = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).Count();

// Angular deep links (/register, /products, ...) fall through to index.html, but /api/*
// must not: a mistyped API route has to 404, not return the HTML shell with status 200.
app.MapFallbackToFile("{*path:regex(^(?!api/).*$)}", "index.html", staticFiles);

app.Run();

/// <summary>
/// The connection string with every password removed, for the startup log. Written as a
/// whitelist of what is printed rather than a blacklist of what is not: a key this does not
/// recognise is dropped, so a future provider option that happens to carry a secret cannot
/// leak by being unlisted.
/// </summary>
static string DescribeConnection(string connectionString)
{
    string[] printable = ["server", "host", "data source", "datasource", "database", "initial catalog", "port", "user id", "uid", "user"];
    var parts = connectionString
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(part => part.Split('=', 2))
        .Where(pair => pair.Length == 2 && printable.Contains(pair[0].Trim().ToLowerInvariant()))
        .Select(pair => $"{pair[0].Trim()}={pair[1].Trim()}")
        .ToList();
    return parts.Count > 0 ? string.Join("; ", parts) : "(no server or database named)";
}

/// <summary>
/// Named so <c>WebApplicationFactory&lt;Program&gt;</c> can start this host in the test suite.
/// A top-level program's generated entry class is internal, and there is nothing else to hang
/// a startup assertion on.
/// </summary>
public partial class Program;
