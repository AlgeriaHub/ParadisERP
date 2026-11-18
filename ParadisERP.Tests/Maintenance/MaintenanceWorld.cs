using System.Security.Claims;
using ParadisERP.API.Endpoints;
using ParadisERP.Application.Contracts;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Maintenance;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Infrastructure.Rentals;
using ParadisERP.Tests.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using static ParadisERP.Tests.Rentals.RentalTestPaper;

namespace ParadisERP.Tests.Maintenance;

/// <summary>
/// The smallest company maintenance jobs need (Phase M1): two branches (TillHarness), a hired-out scaffold costed at
/// 1,200 with a hundred on the shelf in Algiers, a contractor with a site, and the module switched as the test says.
/// </summary>
internal sealed class MaintenanceWorld : IAsyncDisposable
{
    public static readonly Guid Algiers = TillHarness.Algiers;
    public static readonly Guid Oran = TillHarness.Oran;
    public static readonly Guid Scaffold = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001");
    public static readonly Guid Contractor = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000002");
    public static readonly Guid ContractorSite = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000003");
    public const decimal Cost = 1200m;

    public static readonly MaintenanceActor Chef = new(Guid.Parse("a1a1a1a1-0000-0000-0000-0000000000c1"), "chef");
    public static readonly MaintenanceActor Second = new(Guid.Parse("a1a1a1a1-0000-0000-0000-0000000000c2"), "adjoint");

    private readonly SqliteConnection connection;
    public PosDbContext Db { get; }
    public MaintenanceJobs Jobs { get; }
    public RentalDocuments Rentals { get; }

    private MaintenanceWorld(SqliteConnection connection, PosDbContext db)
    {
        this.connection = connection;
        Db = db;
        Jobs = new MaintenanceJobs(db, new LedgerPoster(db), Regional);
        Rentals = new RentalDocuments(db, new LedgerPoster(db), new Algeria());
    }

    public SqliteConnection Connection => connection;

    /// <summary>The Algeria preset's regional settings the jobs read their company time zone from.</summary>
    public static readonly IRegionalSettingsProvider Regional = new Algeria();

    private sealed class Algeria : IRegionalSettingsProvider
    {
        public RegionalSettings Get() => new() { CountryCode = "DZ", CurrencyCode = "DZD", CurrencyDecimals = 2, TimeZone = "Africa/Algiers" };
    }

    /// <param name="settings">What Maintenance › Settings says; null leaves the row unsaved (every default, the module off).</param>
    public static async Task<MaintenanceWorld> NewAsync(Action<MaintenanceSettings>? settings = null, decimal inMaintenance = 0m)
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        db.Stores.Add(new Store { NameFr = "Échafaudages SARL", RentalOverdueMultiplier = 1.5m });
        db.Products.Add(new Product { Id = Scaffold, Sku = "ECH-01", NameFr = "Échafaudage", IsRentable = true, CostPrice = Cost, TrackStock = true });
        db.ProductStocks.Add(new ProductStock { ProductId = Scaffold, BranchId = Algiers, StockQty = 100, MaintenanceQty = inMaintenance });
        db.ProductStocks.Add(new ProductStock { ProductId = Scaffold, BranchId = Oran, StockQty = 0 });
        db.Customers.Add(Identified(new Customer
        {
            Id = Contractor, Code = "C001", FullName = "Ben Ali BTP", CreditLimit = 5_000_000m, Kind = CustomerKind.Person,
        }));
        db.CustomerSites.Add(Site(ContractorSite, Contractor));
        await db.SaveChangesAsync();
        if (settings is not null)
        {
            var row = new MaintenanceSettings();
            settings(row);
            db.MaintenanceSettings.Add(row);
            await db.SaveChangesAsync();
        }
        db.ChangeTracker.Clear();
        return new MaintenanceWorld(connection, db);
    }

    /// <summary>The module on, every other choice its default.</summary>
    public static Action<MaintenanceSettings> On(Action<MaintenanceSettings>? more = null) => s =>
    {
        s.Module = MaintenanceSwitch.On;
        more?.Invoke(s);
    };

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await connection.DisposeAsync();
    }

    public Task<ProductStock> StockAsync(Guid branch, Guid? product = null) =>
        Db.ProductStocks.AsNoTracking().SingleAsync(s => s.ProductId == (product ?? Scaffold) && s.BranchId == branch);

    /// <summary>A contract for 100 scaffolds, all issued on 1 March and back on 31 March: <paramref name="damaged"/> repairable, charged <paramref name="recharge"/>, the rest sound.</summary>
    public async Task<(Guid ContractId, Guid NoteId, Guid LineId, ReturnOutcome Outcome)> ReturnDamagedAsync(decimal damaged, decimal recharge = 15_000m)
    {
        var contract = (await Rentals.CreateContractAsync(new UpsertRentalContractRequest(Contractor, Algiers,
            [new RentalContractLineRequest(Scaffold, 100, 30, RentUnitPrice: 100, DamageUnitPrice: 2000)])
        {
            SiteId = ContractorSite, SignatoryBirthDate = new DateOnly(1979, 11, 20),
        }, "admin")).Value!;
        var lineId = contract.Lines.Single().Id;
        var issued = await Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 100)], Day("2026-03-01"))), null, null);
        Assert.True(issued.Ok, issued.Message);
        var back = await Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(Algiers,
            [new ReturnNoteLineRequest(lineId, QtyOk: 100 - damaged, QtyPartialDamage: damaged, MaintenanceValue: damaged > 0 ? recharge : 0)],
            Day("2026-03-31"))), Chef.UserId, "storeman");
        Assert.True(back.Ok, back.Message);
        var note = back.Value!.Note;
        return (contract.Id, note.Id, note.Lines.Single().Id, back.Value);
    }

    private static DateTime Day(string iso) => DateTime.Parse(iso + "T09:00:00Z").ToUniversalTime();

    public static CreateMaintenanceJobRequest Internal(decimal qty, Guid? branch = null) =>
        new(Guid.NewGuid().ToString("N"), branch ?? Algiers, "Internal", Scaffold, qty, null, null, "Soudure");

    public static CreateMaintenanceJobRequest Recharged(Guid lineId, decimal qty, Guid? branch = null) =>
        new(Guid.NewGuid().ToString("N"), branch ?? Algiers, "Recharged", null, qty, lineId, null, "Cadres tordus");

    public async Task<MaintenanceJobDto> OpenAsync(CreateMaintenanceJobRequest r, MaintenanceActor? by = null)
    {
        var made = await Jobs.CreateAsync(r, by ?? Chef);
        Assert.True(made.Ok, made.Error);
        return made.Value!;
    }

    /// <summary>A signed-in caller as the API sees one, with a user row so their branch can be read.</summary>
    public async Task<ClaimsPrincipal> CallerAsync(UserRole role, Guid branch, MaintenanceActor who)
    {
        if (!await Db.Users.AnyAsync(u => u.Id == who.UserId))
        {
            Db.Users.Add(new User { Id = who.UserId!.Value, Username = who.Name!, FullName = who.Name!, Role = role, BranchId = branch });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }
        var principal = TillHarness.Principal(role, null, who.UserId);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("name", who.Name!));
        return principal;
    }

    /// <summary>One maintenance route, through its filters, as Kestrel would call it once authorisation passed.</summary>
    public Task<(int Status, string Body)> CallAsync(ClaimsPrincipal caller, string method, string pattern, string json = "",
        params (string Key, string Value)[] route) =>
        BranchLockTests.InvokeAsync(Db, a => a.MapMaintenanceEndpoints(), method, pattern, caller, json, route);

    /// <summary>The manual state route, as the stock screen calls it.</summary>
    public Task<(int Status, string Body)> StateAsync(ClaimsPrincipal caller, StockState from, StockState to, decimal qty, string? reason = null) =>
        BranchLockTests.InvokeAsync(Db, a => a.MapStockEndpoints(), "POST", "/api/catalog/stock/state", caller,
            System.Text.Json.JsonSerializer.Serialize(new StockStateChangeRequest(Scaffold, Algiers, qty, from.ToString(), to.ToString(), reason)));
}
