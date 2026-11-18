using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Receivables;
using AlgeriaPos.Infrastructure.Rentals;
using AlgeriaPos.Infrastructure.Seeding;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Tests.Receivables;

/// <summary>
/// A company with a contractor on account, against a real database.
///
/// <para>
/// Real because everything phase 5 claims is a claim about what the ledger holds afterwards.
/// The arithmetic on its own is checked in <see cref="DebtAgeingTests"/> and by the rules in
/// <c>AlgeriaPos.Application.Receivables</c>, which need nothing at all to run.
/// </para>
/// </summary>
public sealed class ReceivableHarness(
    SqliteConnection connection,
    PosDbContext db,
    ReceivableDocuments receivables,
    RentalDocuments rentals,
    CustomerStatements statements) : IAsyncDisposable
{
    public static readonly Guid Algiers = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Oran = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid Concrete = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid Scaffold = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid Contractor = Guid.Parse("55555555-5555-5555-5555-555555555555");
    public static readonly Guid Other = Guid.Parse("66666666-6666-6666-6666-666666666666");

    public PosDbContext Db { get; } = db;
    public ReceivableDocuments Receivables { get; } = receivables;
    public RentalDocuments Rentals { get; } = rentals;
    public CustomerStatements Statements { get; } = statements;

    private sealed class Algeria : IRegionalSettingsProvider
    {
        public RegionalSettings Get() => new()
        {
            CountryCode = "DZ", CurrencyCode = "DZD", CurrencyDecimals = 2, TimeZone = "Africa/Algiers"
        };
    }

    public static async Task<ReceivableHarness> NewAsync(decimal creditLimit = 5_000_000m, decimal rentalTaxRate = 0m)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        db.SyncCounters.Add(new SyncCounter { Id = 1, Value = 0 });
        db.Stores.Add(new Store
        {
            NameFr = "Béton & Échafaudages SARL", RentalOverdueMultiplier = 1.5m, RentalTaxRate = rentalTaxRate
        });
        db.Branches.Add(new Branch { Id = Algiers, NameFr = "Alger-Centre", IsMain = true, IsOpenForOperation = true });
        db.Branches.Add(new Branch { Id = Oran, NameFr = "Oran", IsOpenForOperation = true });
        db.Devices.Add(new Device { Code = "C01", BranchId = Algiers });
        db.Products.Add(new Product
        {
            Id = Concrete, Sku = "BET-25", NameFr = "Béton C25/30", CostPrice = 9000m, TrackStock = false
        });
        db.Products.Add(new Product
        {
            Id = Scaffold, Sku = "ECH-01", NameFr = "Échafaudage", IsRentable = true,
            CostPrice = 1200m, AssetUnitValue = 800m
        });
        db.ProductStocks.Add(new ProductStock { ProductId = Scaffold, BranchId = Algiers, StockQty = 500 });
        // Persons, so a contract raised on them derives its signatory from the customer and its
        // first issue note is not refused: the signatory rule working, not weakened.
        // Identified in full and given a site, so the contract's site rule and the first issue
        // note's identity rule are met by the rules working (answers 9 and 10, 2026-09-30).
        db.Customers.Add(AlgeriaPos.Tests.Rentals.RentalTestPaper.Identified(new Customer
        {
            Id = Contractor, Code = "C001", FullName = "Ben Ali BTP", CreditLimit = creditLimit, Kind = CustomerKind.Person
        }));
        db.Customers.Add(new Customer { Id = Other, Code = "C002", FullName = "Sahraoui Travaux", Kind = CustomerKind.Person });
        db.CustomerSites.Add(AlgeriaPos.Tests.Rentals.RentalTestPaper.Site(AlgeriaPos.Tests.Rentals.RentalTestPaper.ReceivableContractorSite, Contractor));
        await db.SaveChangesAsync();
        await CoreDataSeeder.EnsureAsync(db);

        // A cheque is operator-defined, not built in — the five programmed tenders are cash,
        // card, credit, gift card and points. It is the tender the whole "paper is not payment"
        // rule exists for, so the company gets one.
        db.PaymentMethodDefs.Add(new PaymentMethodDef
        {
            Key = "cheque", NameAr = "شيك", NameFr = "Chèque", NameEn = "Cheque",
            Behavior = PaymentBehavior.Generic, RequiresConfirmation = true,
            RequiresReference = true, RequiresInstrumentDetails = true, SortOrder = 10
        });
        await db.SaveChangesAsync();

        var ledger = new LedgerPoster(db);
        var regional = new Algeria();
        var rentals = new RentalDocuments(db, ledger, regional);
        var receivables = new ReceivableDocuments(db, ledger, rentals, regional);
        return new ReceivableHarness(connection, db, receivables, rentals, new CustomerStatements(db, receivables, regional));
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await connection.DisposeAsync();
    }

    // ---------- reading the ledger back ----------

    /// <summary>
    /// Every line the company has posted, by account code. Summed here rather than in the
    /// database: SQLite stores decimal as TEXT, so a server-side Sum compares strings.
    /// </summary>
    public async Task<Dictionary<string, decimal>> PostedAsync(string? sourceType = null)
    {
        var accounts = await Db.LedgerAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Code);
        var query = Db.LedgerEntries.AsNoTracking().Include(e => e.Lines).AsQueryable();
        if (sourceType is not null) query = query.Where(e => e.SourceType == sourceType);

        var entries = await query.ToListAsync();
        return entries.SelectMany(e => e.Lines)
            .GroupBy(l => accounts[l.AccountId])
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));
    }

    /// <summary>
    /// What one customer owes on one account, straight out of the ledger — the figure every
    /// statement, invoice and ageing total in phase 5 has to agree with.
    /// </summary>
    public async Task<decimal> AccountAsync(string code, Guid customerId)
    {
        var account = await Db.LedgerAccounts.AsNoTracking().FirstAsync(a => a.Code == code);
        var lines = await Db.LedgerEntryLines.AsNoTracking()
            .Where(l => l.AccountId == account.Id && l.CustomerId == customerId)
            .Select(l => new { l.Debit, l.Credit })
            .ToListAsync();
        return lines.Sum(l => l.Debit - l.Credit);
    }

    /// <summary>Trade debt (411) for one customer.</summary>
    public Task<decimal> TradeDebtAsync(Guid? customerId = null) =>
        AccountAsync(LedgerAccounts.Customers, customerId ?? Contractor);

    /// <summary>Uncleared paper (413) for one customer.</summary>
    public Task<decimal> UnclearedAsync(Guid? customerId = null) =>
        AccountAsync(LedgerAccounts.NotesReceivable, customerId ?? Contractor);

    public async Task<decimal> BalanceAsync(Guid? customerId = null) =>
        (await Db.Customers.AsNoTracking().FirstAsync(c => c.Id == (customerId ?? Contractor))).Balance;

    public async Task<decimal> OutstandingAsync(Guid invoiceId) =>
        (await Receivables.StandingsAsync([invoiceId]))[invoiceId].Outstanding;

    // ---------- raising documents ----------

    public static CreateInvoiceRequest DirectInvoice(
        decimal qty = 10, decimal unitPrice = 12_000m, decimal taxRate = 19m, int dueInDays = 30,
        Guid? customerId = null) =>
        new(DateTime.UtcNow.AddDays(dueInDays),
            customerId ?? Contractor, Algiers,
            Lines: [new InvoiceLineRequest(Concrete, null, null, qty, unitPrice, taxRate)]);

    public Task<ReceivableResult<Invoice>> RaiseAsync(CreateInvoiceRequest request) =>
        Receivables.CreateInvoiceAsync(request, "admin");

    public Task<ReceivableResult<Invoice>> RaiseAsync(
        decimal qty = 10, decimal unitPrice = 12_000m, decimal taxRate = 19m, int dueInDays = 30) =>
        RaiseAsync(DirectInvoice(qty, unitPrice, taxRate, dueInDays));

    /// <summary>
    /// A sale pushed the way a real till pushes one — through <c>SyncService</c>, so the debt it
    /// raises, the entry it posts and the cash it puts in the drawer are the production rules
    /// and not a second copy of them written for a test.
    /// </summary>
    public async Task<Guid> PushSaleAsync(decimal total, decimal cash, Guid? customerId = null)
    {
        var id = Guid.NewGuid();
        var reference = $"T-{await Db.Sales.CountAsync() + 1:D4}";
        var payments = cash > 0
            ? new[] { new SalePaymentPush(Guid.NewGuid(), nameof(PaymentMethod.Cash), cash, null, "cash") }
            : [];

        var response = await Security.TillHarness.NewService(Db).PushAsync(
            new PushRequest("C01",
            [
                new SalePush(
                    id, reference, await Db.Sales.CountAsync() + 1, DateTime.UtcNow,
                    customerId ?? Contractor, Guid.NewGuid(), "Cashier",
                    total, 0m, 0m, total, cash, 0m,
                    cash >= total ? nameof(PaymentMethod.Cash) : nameof(PaymentMethod.Credit),
                    nameof(SaleStatus.Completed), null, null,
                    [new SaleLinePush(Guid.NewGuid(), Concrete, "BET-25", "Béton C25/30", 1m, total, 0m, 0m, total, 0m)],
                    payments, 0m, null)
            ]), "C01", CancellationToken.None);

        if (response.Rejected > 0)
            throw new InvalidOperationException(
                "The test till's sale was rejected: " + string.Join("; ", response.Results.Select(r => r.Message)));
        return id;
    }

    public Task<ReceivableResult<ReceiptVoucher>> ReceiveAsync(
        decimal amount, string methodKey = "cash", Guid? invoiceId = null, decimal? allocate = null,
        Guid? customerId = null) =>
        Receivables.CreateReceiptAsync(new CreateReceiptRequest(
            customerId ?? Contractor, Algiers, amount, methodKey,
            Allocations: invoiceId is Guid id ? [new ReceiptAllocationRequest(id, allocate ?? amount)] : null), "caissier");
}
