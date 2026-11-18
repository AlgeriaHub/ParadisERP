using ParadisERP.Tests.Hosting;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ParadisERP.API.Endpoints;
using ParadisERP.Application.Regional;
using ParadisERP.Application.Subscriptions;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Manufacturing;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Tests.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace ParadisERP.Tests.Manufacturing;

/// <summary>
/// The manufacturing routes, served as Program serves them (the same handlers, the same services), against
/// the shared till database: Algiers (main branch), Oran (branch) and the product PIPE-90. Adds the two
/// line warehouses and the logins a test needs.
/// </summary>
internal static class MfgHarness
{
    public static readonly Guid WarehouseA = Guid.Parse("a0000000-0000-4000-8000-00000000000a");
    public static readonly Guid WarehouseB = Guid.Parse("b0000000-0000-4000-8000-00000000000b");
    public static readonly Guid MainWarehouse = Guid.Parse("c0000000-0000-4000-8000-00000000000c");

    internal sealed record Setup(SqliteConnection Connection, PosDbContext Db) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Connection.DisposeAsync(); }
    }

    public static async Task<Setup> NewAsync()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        db.Branches.Add(new Branch { Id = WarehouseA, NameAr = "ورشة أ", NameFr = "Atelier A", IsWarehouse = true, IsOpenForOperation = true });
        db.Branches.Add(new Branch { Id = WarehouseB, NameAr = "ورشة ب", NameFr = "Atelier B", IsWarehouse = true, IsOpenForOperation = true });
        if (!db.Branches.Any(b => b.IsMain && b.IsWarehouse))
            db.Branches.Add(new Branch { Id = MainWarehouse, NameFr = "Dépôt central", IsWarehouse = true, IsMain = true, IsOpenForOperation = true });
        await db.SaveChangesAsync();
        return new Setup(connection, db);
    }

    /// <summary>The company's Admin: every manufacturing permission by role, every line.</summary>
    public static async Task<(User User, ClaimsPrincipal Principal)> AdminAsync(PosDbContext db) =>
        await BranchLockTests.UserAsync(db, UserRole.Admin, TillHarness.Algiers);

    /// <summary>A branch manager with these manufacturing grants, working at <paramref name="branch"/>.</summary>
    public static async Task<(User User, ClaimsPrincipal Principal)> ManagerAsync(PosDbContext db, Guid? branch, params string[] grants)
    {
        var user = new User { Username = $"mgr-{Guid.NewGuid():N}"[..20], FullName = "Chef d'atelier", Role = UserRole.Manager, BranchId = branch };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (user, TillHarness.Principal(UserRole.Manager, null, user.Id, grants));
    }

    public static readonly string[] AllGrants = Capabilities.Manufacturing;

    /// <summary>
    /// Whether the served routes see manufacturing stock operations switched on — on by default, as a
    /// disposable test database is; a test of the switch turns it off for its own calls only.
    /// </summary>
    private static readonly AsyncLocal<bool?> operations = new();
    public static bool StockOperations { get => operations.Value ?? true; set => operations.Value = value; }

    private static readonly AsyncLocal<bool?> costing = new();
    /// <summary>Manufacturing:Costing for the calls of this test; on unless the test says otherwise.</summary>
    public static bool Costing { get => costing.Value ?? true; set => costing.Value = value; }

    public static async Task<(int Status, string Body)> CallAsync(
        PosDbContext db, ClaimsPrincipal caller, string method, string pattern, string json = "", params (string Key, string Value)[] route)
    {
        var (status, bytes, _) = await CallRawAsync(db, caller, method, pattern, json, route);
        return (status, Encoding.UTF8.GetString(bytes));
    }

    /// <summary>The call's raw answer — for a file (an xlsx export) rather than JSON.</summary>
    public static async Task<(int Status, byte[] Body, string? ContentType)> CallRawAsync(
        PosDbContext db, ClaimsPrincipal caller, string method, string pattern, string json = "", params (string Key, string Value)[] route)
    {
        // Built once per test database and switches, then reused (Hosting/EndpointApps): building it for
        // every call was most of a manufacturing test's time.
        var (stockOperations, costing) = (StockOperations, Costing);
        var built = EndpointApps.For(db, (nameof(MfgHarness), stockOperations, costing), () =>
        {
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddSingleton(db);
            builder.Services.AddSingleton<ISubscriptionProvider>(new Unrestricted());
            builder.Services.AddSingleton<IRegionalSettingsProvider>(new Algeria());
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddScoped<ManufacturingSettings>();
            builder.Services.AddScoped<ProductionLines>();
            builder.Services.AddScoped<BillsOfMaterials>();
            builder.Services.AddScoped<ManufacturingGrants>();
            builder.Services.AddSingleton(new ManufacturingOptions(stockOperations));
            builder.Services.AddScoped<ParadisERP.Infrastructure.Payroll.PayrollSettingsStore>();
            builder.Services.AddScoped<ParadisERP.Infrastructure.Hr.EmployeeDirectory>();
            builder.Services.AddScoped<ParadisERP.Infrastructure.Hr.AttendanceSheet>();
            builder.Services.AddScoped<AttendanceCapacity>();
            builder.Services.AddScoped<ProductionOrders>();
            builder.Services.AddScoped<ProductionMovements>();
            builder.Services.AddScoped<ProductionLogs>();
            builder.Services.AddScoped<ManufacturingReports>();
            builder.Services.AddSingleton(new ManufacturingCostingOptions(costing));
            builder.Services.AddScoped<LabourRates>();
            builder.Services.AddScoped<ProductionCosts>();
            builder.Services.AddScoped<ProductionCostSnapshots>();
            builder.Services.AddScoped<ProductionCostHistory>();
            builder.Services.AddScoped<ManufacturingCostingChoices>();
            builder.Services.AddScoped<PracticeLedgerReport>();
            builder.Services.AddScoped<ParadisERP.Infrastructure.Ledger.LedgerPoster>();
            builder.Services.AddScoped<ManufacturingLedgerPostings>();
            builder.Services.AddAuthorization();
            var app = builder.Build();
            app.MapManufacturingEndpoints();
            app.MapManufacturingOperationEndpoints();
            app.MapManufacturingAccountingEndpoints();
            return app;
        });
        var endpoint = built.Endpoint(method, pattern);

        using var scope = built.App.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = caller };
        http.Request.Method = method;
        foreach (var (key, value) in route) http.Request.RouteValues[key] = value;
        if (json.StartsWith('?'))
        {
            http.Request.QueryString = new QueryString(json);
            json = "";
        }
        var body = Encoding.UTF8.GetBytes(json);
        http.Request.Body = new MemoryStream(body);
        http.Request.ContentType = "application/json";
        http.Request.ContentLength = body.Length;
        http.Response.Body = new MemoryStream();
        http.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new HasBody(body.Length > 0));
        await endpoint.RequestDelegate!(http);
        var bytes = ((MemoryStream)http.Response.Body).ToArray();
        db.ChangeTracker.Clear();
        return (http.Response.StatusCode, bytes, http.Response.ContentType);
    }

    /// <summary>The call, asserted to succeed, its JSON answer returned.</summary>
    public static async Task<JsonElement> OkAsync(
        PosDbContext db, ClaimsPrincipal caller, string method, string pattern, string json = "", params (string Key, string Value)[] route)
    {
        var (status, body) = await CallAsync(db, caller, method, pattern, json, route);
        Assert.True(status is >= 200 and < 300, $"{method} {pattern}: {status} {body}");
        return string.IsNullOrEmpty(body) ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>The call, asserted to be refused with this status and code.</summary>
    public static async Task RefusedAsync(
        PosDbContext db, ClaimsPrincipal caller, int status, string code, string method, string pattern, string json = "",
        params (string Key, string Value)[] route)
    {
        var (actual, body) = await CallAsync(db, caller, method, pattern, json, route);
        Assert.True(actual == status, $"{method} {pattern}: expected {status}, got {actual} {body}");
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    public static (string, string) Id(Guid id) => ("id", id.ToString());

    public static Guid IdOf(JsonElement element) => element.GetProperty("id").GetGuid();

    /// <summary>A product with a manufacturing role and base unit, written straight to the database.</summary>
    public static async Task<Product> ProductAsync(PosDbContext db, string sku, ManufacturingRole role, Guid? baseUnit)
    {
        var product = new Product
        {
            Sku = sku, NameFr = sku, NameAr = sku, NameEn = sku, Price = 100m, ManufacturingRole = role, BaseUnitId = baseUnit,
            Unit = baseUnit == SystemUnits.Kilogram ? "kg" : baseUnit == SystemUnits.Metre ? "m" : "pcs",
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return product;
    }

    private sealed class HasBody(bool can) : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => can;
    }

    private sealed class Unrestricted : ISubscriptionProvider
    {
        public SubscriptionAccess GetAccess() => new("Test", "Unrestricted", true, null, null, null, null, DateTimeOffset.UtcNow);
    }

    internal sealed class Algeria : IRegionalSettingsProvider
    {
        private readonly RegionalSettings settings = RegionalPresets.Create(RegionalPresets.DefaultCountry)!;
        public RegionalSettings Get() => settings;
    }
}
