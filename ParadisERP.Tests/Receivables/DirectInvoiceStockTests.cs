using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Receivables;
using ParadisERP.Infrastructure.Valuation;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Receivables;

/// <summary>
/// P0.3: an invoice typed in the back office takes its goods out of stock and posts their cost — only once the accountant
/// switches it on (off by default, today's behaviour). The shop: 100 couplers at Algiers, carried at 2,000, sold at 3,000.
/// </summary>
public class DirectInvoiceStockTests
{
    private static readonly Guid Coupler = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private static async Task<ReceivableHarness> ShopAsync()
    {
        var h = await ReceivableHarness.NewAsync();
        h.Db.Products.Add(new Product { Id = Coupler, Sku = "COUP-01", NameFr = "Collier", CostPrice = 2000m, TrackStock = true });
        h.Db.ProductStocks.Add(new ProductStock { ProductId = Coupler, BranchId = ReceivableHarness.Algiers, StockQty = 100 });
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        return h;
    }

    private static Task<(StockValuationSettingsDto? Saved, string? Error)> SwitchAsync(ReceivableHarness h, bool? on) =>
        new StockValuationShadow(h.Db).UpdateSettingsAsync(new UpdateStockValuationSettingsRequest("Company", "SaleCost", 3, on), "accountant");

    private static CreateInvoiceRequest Goods(decimal qty, params InvoiceLineRequest[] more) =>
        new(DateTime.UtcNow.AddDays(30), ReceivableHarness.Contractor, ReceivableHarness.Algiers,
            Lines: [new InvoiceLineRequest(Coupler, null, null, qty, 3000m, 19m), .. more]);

    private static async Task<Invoice> RaiseAsync(ReceivableHarness h, CreateInvoiceRequest request)
    {
        var raised = await h.RaiseAsync(request);
        Assert.True(raised.Ok, raised.Message);
        h.Db.ChangeTracker.Clear();
        return raised.Value!;
    }

    private static async Task<decimal> StockAsync(ReceivableHarness h) =>
        (await h.Db.ProductStocks.AsNoTracking().SingleAsync(s => s.ProductId == Coupler && s.BranchId == ReceivableHarness.Algiers)).StockQty;

    /// <summary>Debit less credit per account on the one entry a document posted; asserts it balances.</summary>
    private static async Task<Dictionary<string, decimal>> EntryAsync(ReceivableHarness h, string sourceType, Guid sourceId)
    {
        var entry = await h.Db.LedgerEntries.AsNoTracking().Include(e => e.Lines).ThenInclude(l => l.Account)
            .SingleAsync(e => e.SourceType == sourceType && e.SourceId == sourceId);
        Assert.Equal(entry.Lines.Sum(l => l.Debit), entry.Lines.Sum(l => l.Credit));
        return entry.Lines.GroupBy(l => l.Account!.Code).ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));
    }

    private static Task<ReceivableResult<CreditNote>> CreditAsync(ReceivableHarness h, Invoice invoice, decimal qty, decimal back) =>
        h.Receivables.CreateCreditNoteAsync(invoice.Id,
            new CreateCreditNoteRequest("Returned by the customer", Lines: [new CreditNoteLineRequest(invoice.Lines[0].Id, qty, back)]), "admin");

    [Fact]
    public async Task Off_by_default_a_direct_invoice_moves_no_stock_and_posts_exactly_todays_lines()
    {
        await using var h = await ShopAsync();
        Assert.False((await new StockValuationShadow(h.Db).SettingsAsync()).DirectInvoiceStock);

        var invoice = await RaiseAsync(h, Goods(10));

        Assert.Equal(100m, await StockAsync(h));
        Assert.Empty(await h.Db.StockMovements.AsNoTracking().ToListAsync());
        Assert.Null((await h.Db.InvoiceLines.AsNoTracking().SingleAsync()).StockUnitCost);
        var entry = await EntryAsync(h, LedgerSources.Invoice, invoice.Id);
        Assert.Equal(
            new Dictionary<string, decimal> { [LedgerAccounts.Customers] = 35_700m, [LedgerAccounts.Sales] = -30_000m, [LedgerAccounts.TaxCollected] = -5_700m },
            entry);

        // Saving the other choices without naming this one leaves it off.
        Assert.Null((await new StockValuationShadow(h.Db).UpdateSettingsAsync(new UpdateStockValuationSettingsRequest("Company", "SaleCost", 3), "accountant")).Error);
        Assert.False((await new StockValuationShadow(h.Db).SettingsAsync()).DirectInvoiceStock);
    }

    [Fact]
    public async Task On_the_goods_leave_the_invoices_branch_and_their_cost_posts_in_the_invoices_own_entry()
    {
        await using var h = await ShopAsync();
        var (saved, error) = await SwitchAsync(h, true);
        Assert.Null(error);
        Assert.True(saved!.DirectInvoiceStock);
        Assert.Equal("accountant", saved.DirectInvoiceStockChangedBy);

        // A line of concrete (stock not tracked) and a hire line billed by the day ride along and move nothing.
        var invoice = await RaiseAsync(h, Goods(10,
            new InvoiceLineRequest(ReceivableHarness.Concrete, null, null, 2, 12_000m, 19m),
            new InvoiceLineRequest(Coupler, null, "Location collier", 5, 100m, 19m, Days: 3)));

        Assert.Equal(90m, await StockAsync(h));
        var movement = Assert.Single(await h.Db.StockMovements.AsNoTracking().ToListAsync());
        Assert.Equal((StockMovementKind.InvoiceSale, -10m, 90m, 2000m, invoice.Id, invoice.Number),
            (movement.Kind, movement.QtyDelta, movement.BalanceAfter, movement.UnitCost!.Value, movement.DocumentId!.Value, movement.DocumentNumber));

        var lines = await h.Db.InvoiceLines.AsNoTracking().OrderBy(l => l.Position).ToListAsync();
        Assert.Equal((2000m, SaleCostBasis.CostPrice), (lines[0].StockUnitCost!.Value, lines[0].StockCostBasis!.Value));
        Assert.Null(lines[1].StockUnitCost);
        Assert.Null(lines[2].StockUnitCost);

        var entry = await EntryAsync(h, LedgerSources.Invoice, invoice.Id);
        Assert.Equal(20_000m, entry[LedgerAccounts.CostOfGoodsSold]);
        Assert.Equal(-20_000m, entry[LedgerAccounts.Inventory]);
        Assert.Equal(-(30_000m + 24_000m + 1_500m), entry[LedgerAccounts.Sales]);
    }

    [Fact]
    public async Task On_an_invoice_for_more_than_the_branch_holds_is_issued_and_says_stock_went_negative()
    {
        await using var h = await ShopAsync();
        await SwitchAsync(h, true);

        var raised = await h.RaiseAsync(Goods(150));

        Assert.True(raised.Ok, raised.Message);
        Assert.Contains("Stock went negative", raised.Warning);
        h.Db.ChangeTracker.Clear();
        Assert.Equal(-50m, await StockAsync(h));
        Assert.Equal(300_000m, (await EntryAsync(h, LedgerSources.Invoice, raised.Value!.Id))[LedgerAccounts.CostOfGoodsSold]);
    }

    [Fact]
    public async Task A_credit_note_brings_goods_back_at_the_cost_the_invoice_line_took_out_and_never_more_than_left()
    {
        await using var h = await ShopAsync();
        await SwitchAsync(h, true);
        var invoice = await RaiseAsync(h, Goods(10));
        // The cost price moves after the invoice: the goods still come back at what they left at.
        (await h.Db.Products.SingleAsync(p => p.Id == Coupler)).CostPrice = 2600m;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var first = await CreditAsync(h, invoice, qty: 4, back: 3);
        Assert.True(first.Ok, first.Message);
        h.Db.ChangeTracker.Clear();

        Assert.Equal(93m, await StockAsync(h));
        var back = await h.Db.StockMovements.AsNoTracking().SingleAsync(m => m.Kind == StockMovementKind.InvoiceReturn);
        Assert.Equal((3m, 2000m, first.Value!.Id, first.Value.Number), (back.QtyDelta, back.UnitCost!.Value, back.DocumentId!.Value, back.DocumentNumber));
        var entry = await EntryAsync(h, LedgerSources.CreditNote, first.Value.Id);
        Assert.Equal(6_000m, entry[LedgerAccounts.Inventory]);
        Assert.Equal(-6_000m, entry[LedgerAccounts.CostOfGoodsSold]);
        Assert.Equal(12_000m, entry[LedgerAccounts.Sales]);
        Assert.Equal(-14_280m, entry[LedgerAccounts.Customers]);
        Assert.Equal(3m, (await h.Db.CreditNoteLines.AsNoTracking().SingleAsync()).StockReturnedQty);

        // More than this note credits, or below zero: refused, and nothing moves.
        Assert.Equal(ReceivableFailure.Invalid, (await CreditAsync(h, invoice, qty: 2, back: 3)).Failure);
        Assert.Equal(ReceivableFailure.Invalid, (await CreditAsync(h, invoice, qty: 1, back: -1)).Failure);
        Assert.Equal(93m, await StockAsync(h));

        // A credit of money only brings nothing back, and the rest can still come back later.
        var moneyOnly = await CreditAsync(h, invoice, qty: 1, back: 0);
        Assert.True(moneyOnly.Ok, moneyOnly.Message);
        Assert.DoesNotContain(LedgerAccounts.Inventory, (await EntryAsync(h, LedgerSources.CreditNote, moneyOnly.Value!.Id)).Keys);
        var rest = await CreditAsync(h, invoice, qty: 5, back: 5);
        Assert.True(rest.Ok, rest.Message);
        h.Db.ChangeTracker.Clear();
        Assert.Equal(98m, await StockAsync(h));
    }

    [Fact]
    public async Task An_invoice_issued_while_off_is_never_touched_and_its_goods_cannot_come_back_into_stock()
    {
        await using var h = await ShopAsync();
        var before = await RaiseAsync(h, Goods(10));

        await SwitchAsync(h, true);

        Assert.Empty(await h.Db.StockMovements.AsNoTracking().ToListAsync());
        Assert.Equal(100m, await StockAsync(h));
        var refused = await CreditAsync(h, before, qty: 2, back: 2);
        Assert.Equal(ReceivableFailure.Invalid, refused.Failure);
        Assert.Contains("took nothing out of stock", refused.Message);
        Assert.True((await CreditAsync(h, before, qty: 2, back: 0)).Ok);
        Assert.Equal(100m, await StockAsync(h));
    }

    [Fact]
    public async Task Goods_that_left_while_on_can_still_come_back_after_the_switch_is_turned_off()
    {
        await using var h = await ShopAsync();
        await SwitchAsync(h, true);
        var invoice = await RaiseAsync(h, Goods(10));
        await SwitchAsync(h, false);

        Assert.True((await CreditAsync(h, invoice, qty: 2, back: 2)).Ok);
        h.Db.ChangeTracker.Clear();
        Assert.Equal(92m, await StockAsync(h));
        // And a new invoice moves nothing again.
        await RaiseAsync(h, Goods(1));
        Assert.Equal(92m, await StockAsync(h));
    }

    [Fact]
    public async Task Once_sales_are_costed_at_the_weighted_average_an_invoice_goes_out_at_it_and_comes_back_at_it()
    {
        await using var h = await ShopAsync();
        // Opened with 100 at 2,000, then 100 bought at 3,000: the average is 2,500.
        await StockValuationStart.EnsureAsync(h.Db);
        var settings = await h.Db.StockValuationSettings.SingleAsync();
        h.Db.StockMovements.Add(new StockMovement
        {
            ProductId = Coupler, BranchId = ReceivableHarness.Algiers, QtyDelta = 100, MovedQty = 100, BalanceAfter = 200,
            Reason = "receiving", Kind = StockMovementKind.Receipt, UnitCost = 3000m, CreatedAtUtc = settings.StartedAtUtc.AddTicks(10),
        });
        (await h.Db.ProductStocks.SingleAsync(s => s.ProductId == Coupler)).StockQty = 200;
        settings.SalesCost = SalesCostMethod.WeightedAverage;
        settings.DirectInvoiceStock = true;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var invoice = await RaiseAsync(h, Goods(10));

        var line = await h.Db.InvoiceLines.AsNoTracking().SingleAsync();
        Assert.Equal((2500m, SaleCostBasis.WeightedAverage), (line.StockUnitCost!.Value, line.StockCostBasis!.Value));
        Assert.Equal(25_000m, (await EntryAsync(h, LedgerSources.Invoice, invoice.Id))[LedgerAccounts.CostOfGoodsSold]);

        var credit = await CreditAsync(h, invoice, qty: 4, back: 4);
        Assert.True(credit.Ok, credit.Message);
        Assert.Equal(10_000m, (await EntryAsync(h, LedgerSources.CreditNote, credit.Value!.Id))[LedgerAccounts.Inventory]);

        // The shadow replays both movements at the figures posted: cost of sales posted and at the average agree.
        var report = await new StockValuationShadow(h.Db).ReportAsync(null);
        var row = Assert.Single(report.Rows, r => r.ProductId == Coupler);
        Assert.Equal(15_000m, row.PostedCostOfSales);
        Assert.Equal(15_000m, row.ShadowCostOfSales);
        Assert.Equal(194m, row.ClosingQty);
        Assert.Equal(485_000m, row.ClosingValue);
    }

    [Fact]
    public async Task With_no_average_yet_the_goods_go_out_at_the_cost_price_and_say_so()
    {
        await using var h = await ShopAsync();
        await StockValuationStart.EnsureAsync(h.Db);
        // A product created after the opening, with nothing received since: there is no average to take.
        var fresh = Guid.NewGuid();
        h.Db.Products.Add(new Product { Id = fresh, Sku = "NEW-01", NameFr = "Nouveau", CostPrice = 700m, TrackStock = true });
        var settings = await h.Db.StockValuationSettings.SingleAsync();
        settings.SalesCost = SalesCostMethod.WeightedAverage;
        settings.DirectInvoiceStock = true;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var raised = await h.RaiseAsync(new CreateInvoiceRequest(DateTime.UtcNow.AddDays(30), ReceivableHarness.Contractor, ReceivableHarness.Algiers,
            Lines: [new InvoiceLineRequest(fresh, null, null, 3, 1000m, 19m)]));

        Assert.True(raised.Ok, raised.Message);
        h.Db.ChangeTracker.Clear();
        var line = await h.Db.InvoiceLines.AsNoTracking().SingleAsync();
        Assert.Equal((700m, SaleCostBasis.CostPriceFallback), (line.StockUnitCost!.Value, line.StockCostBasis!.Value));
        Assert.Equal(2_100m, (await EntryAsync(h, LedgerSources.Invoice, raised.Value!.Id))[LedgerAccounts.CostOfGoodsSold]);
        Assert.Equal(-3m, (await h.Db.ProductStocks.AsNoTracking().SingleAsync(s => s.ProductId == fresh)).StockQty);
    }
}
