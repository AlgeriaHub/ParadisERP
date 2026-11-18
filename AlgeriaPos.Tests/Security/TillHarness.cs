using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Integrations;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Application.Subscriptions;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Integrations;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Seeding;
using AlgeriaPos.Infrastructure.Sync;
using AlgeriaPos.Infrastructure.Tenancy;
using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlgeriaPos.Tests.Security;

/// <summary>
/// A company with two branches, a till at each and ten pipes on each shelf — the smallest shop
/// in which "what can a till in the wrong hands do" is a question with an answer.
/// </summary>
internal static class TillHarness
{
    public static readonly Guid Algiers = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Oran = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid Pipe = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private sealed class Unrestricted : ISubscriptionProvider
    {
        public SubscriptionAccess GetAccess() =>
            new("Test", "Unrestricted", true, null, null, null, null, DateTimeOffset.UtcNow);
    }

    private sealed class Preset : IRegionalSettingsProvider
    {
        private readonly RegionalSettings settings = RegionalPresets.Create(RegionalPresets.DefaultCountry)!;
        public RegionalSettings Get() => settings;
    }

    private sealed class NoPacks : ILanguagePackStore
    {
        public Task<IReadOnlyDictionary<string, long>> RevisionsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());
        public Task<LanguagePack?> GetAsync(string code, CancellationToken ct) => Task.FromResult<LanguagePack?>(null);
    }

    /// <param name="logger">
    /// Where the service writes what it refuses to tell the till. Silent unless a test wants to
    /// read the log back (PushRejectionMessageTests).
    /// </param>
    public static SyncService NewService(PosDbContext db, ILogger<SyncService>? logger = null)
    {
        var tenant = new TenantContext();
        tenant.Initialize(new TenantOptions { Id = "test", ConnectionString = "x" });
        var integrations = new TenantIntegrations(tenant, new ConfiguredIntegrationsProvider(null, null));
        return new SyncService(db, new Unrestricted(), new Preset(), new NoPacks(), integrations, new LedgerPoster(db),
            logger ?? NullLogger<SyncService>.Instance);
    }

    /// <summary>
    /// The principal a token would carry: the claim names TokenService writes, with the role
    /// registered as the role claim the way Program.cs configures the bearer handler.
    /// </summary>
    public static ClaimsPrincipal Principal(UserRole role, string? deviceCode, Guid? userId = null, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("sub", (userId ?? Guid.NewGuid()).ToString()),
            new("role", role.ToString()),
        };
        if (deviceCode is not null) claims.Add(new Claim("device", deviceCode));
        claims.AddRange(permissions.Select(p => new Claim(Permissions.ClaimType, p)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test", "name", "role"));
        // What the pipeline attaches after the session check (AUTH-1): the system role's scope and
        // permissions, and the given permissions as the user's additional grants.
        var role_ = SystemRoles.For(role);
        principal.AddIdentity(AlgeriaPos.Infrastructure.Authorization.ServerAuthorization.CreateIdentity(
            new AlgeriaPos.Infrastructure.Authorization.ServerAuthorizationContext(role_.Id, role_.Scope,
                AlgeriaPos.Infrastructure.Authorization.ServerAuthorization.Combine(role_.Permissions, true, permissions))));
        return principal;
    }

    /// <summary>The same caller with one more additional grant, as the server would resolve it.</summary>
    public static ClaimsPrincipal WithGrant(ClaimsPrincipal principal, string key)
    {
        var decided = AlgeriaPos.Infrastructure.Authorization.ServerAuthorization.Read(principal)!;
        var token = principal.Identities.First(i => i.AuthenticationType != AlgeriaPos.Infrastructure.Authorization.ServerAuthorization.AuthenticationType);
        var widened = new ClaimsPrincipal(new ClaimsIdentity(token.Claims.Append(new Claim(Permissions.ClaimType, key)), "test", "name", "role"));
        widened.AddIdentity(AlgeriaPos.Infrastructure.Authorization.ServerAuthorization.CreateIdentity(
            decided with { Permissions = decided.Permissions.Append(key).ToHashSet(StringComparer.Ordinal) }));
        return widened;
    }

    public static async Task<(SqliteConnection Connection, PosDbContext Db)> NewDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.SyncCounters.Add(new SyncCounter { Id = 1, Value = 0 });
        db.Branches.Add(new Branch { Id = Algiers, NameFr = "Alger-Centre", IsMain = true, IsOpenForOperation = true });
        db.Branches.Add(new Branch { Id = Oran, NameFr = "Oran", IsOpenForOperation = true });
        db.Devices.Add(new Device { Code = "C01", BranchId = Algiers });
        db.Devices.Add(new Device { Code = "C02", BranchId = Oran });
        db.Products.Add(new Product { Id = Pipe, Sku = "PIPE-90", NameFr = "Tube 90", Price = 1000m, TrackStock = true });
        db.ProductStocks.Add(new ProductStock { ProductId = Pipe, BranchId = Algiers, StockQty = 10 });
        db.ProductStocks.Add(new ProductStock { ProductId = Pipe, BranchId = Oran, StockQty = 10 });
        await db.SaveChangesAsync();
        await CoreDataSeeder.EnsureAsync(db);
        return (connection, db);
    }

    public static async Task<Customer> AddCustomerAsync(PosDbContext db, decimal owing)
    {
        var customer = new Customer { Code = "C-1", FullName = "Entreprise Benali", Balance = owing, CreditLimit = 100_000m };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    /// <summary>One pipe at 1 000, settled however the caller says.</summary>
    public static SalePush Sale(
        string reference, long localSeq, Guid? customerId = null, string method = nameof(PaymentMethod.Cash),
        decimal paidAmount = 1000m, decimal changeAmount = 0m, params SalePaymentPush[] payments) => new(
        Guid.NewGuid(), reference, localSeq, DateTime.UtcNow, customerId, Guid.NewGuid(), "Cashier",
        1000m, 0m, 0m, 1000m, paidAmount, changeAmount, method, nameof(SaleStatus.Completed), null, null,
        [new SaleLinePush(Guid.NewGuid(), Pipe, "PIPE-90", "Tube 90", 1m, 1000m, 0m, 0m, 1000m, 0m)],
        payments, 0m, null);

    public static SalePaymentPush Tender(string key, decimal amount) =>
        new(Guid.NewGuid(), key, amount, null, key, null, null, null);

    public static async Task<decimal> CashInBoxAsync(PosDbContext db, Guid branchId)
    {
        var box = await db.CashBoxes.AsNoTracking().SingleAsync(x => x.BranchId == branchId);
        var movements = await db.CashMovements.AsNoTracking()
            .Where(m => m.CashBoxId == box.Id).Select(m => new { m.Direction, m.Amount }).ToListAsync();
        return movements.Sum(m => m.Direction == CashDirection.In ? m.Amount : -m.Amount);
    }
}
