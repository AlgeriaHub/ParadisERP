using ParadisERP.Application.Contracts;
using ParadisERP.Application.Receivables;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Infrastructure.Rentals;
using ParadisERP.Infrastructure.Valuation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Engine = ParadisERP.Application.Rentals;

namespace ParadisERP.Infrastructure.Receivables;

/// <summary>One line of a maintenance job's invoice (its service, or its parts or labour under MS-34 "apart"): what it says, its net before tax, and its income account.</summary>
public sealed record MaintenanceJobBillLine(string Sku, string Name, decimal Net, string IncomeAccountCode);

/// <summary>
/// What a customer maintenance job bills (Phase M5a): the job, its customer and branch, the dates, whether VAT is ticked and
/// at what rate, and its lines. Built by the job's writer, issued by <see cref="ReceivableDocuments.CreateMaintenanceJobInvoiceAsync"/>.
/// </summary>
public sealed record MaintenanceJobBill(
    Guid JobId, Guid CustomerId, Guid BranchId, DateTime DueDateUtc, DateTime? IssuedAtUtc, TaxMode TaxMode, decimal TaxRate,
    string? Notes, string? PaymentMethodKey, IReadOnlyList<MaintenanceJobBillLine> Lines);

/// <summary>Why a receivable document could not be raised, in the shape an HTTP layer needs.</summary>
public enum ReceivableFailure { NotFound, Invalid, Conflict, Forbidden }

/// <summary>
/// A document, or the reason there isn't one — and, separately, anything the user should be
/// told about a document that <i>was</i> raised.
///
/// <para>
/// The two are kept apart deliberately. A credit limit in this system is a warning and never a
/// block (CLAUDE.md, rule one), so "the contractor is over their limit" has to travel back
/// beside a successful document rather than in place of one. Folding it into
/// <see cref="Failure"/> is exactly how a warning quietly becomes a refusal.
/// </para>
/// </summary>
public sealed record ReceivableResult<T>(
    T? Value, ReceivableFailure? Failure = null, string? Message = null, string? Warning = null, string? Code = null)
{
    public bool Ok => Failure is null;

    public static ReceivableResult<T> Success(T value, string? warning = null) => new(value, Warning: warning);
    public static ReceivableResult<T> NotFound(string message) => new(default, ReceivableFailure.NotFound, message);
    public static ReceivableResult<T> Invalid(string message) => new(default, ReceivableFailure.Invalid, message);
    public static ReceivableResult<T> Conflict(string message) => new(default, ReceivableFailure.Conflict, message);
    public static ReceivableResult<T> Forbidden(string message, string code) => new(default, ReceivableFailure.Forbidden, message, Code: code);
}

/// <summary>
/// Raising the documents a debt is made of: the invoice, what was received against it, and what
/// was given back.
///
/// <para>
/// <b>The rule this whole class is arranged around:</b> an invoice raised over a sale or a
/// rental note bills money those documents already posted, and therefore posts nothing itself.
/// Its amount is not re-derived from its own lines either — it is read from the ledger, from
/// the debit that document actually put on the customer's account. That is what makes a
/// statement and an ageing report tie to account 411 exactly rather than approximately, and it
/// means neither a rounding rule nor a pricing rule has to be reimplemented here to stay in
/// step with the module that owns it.
/// </para>
/// <para>
/// A service rather than a set of endpoint lambdas because these are the rules worth testing:
/// that paper does not settle an invoice, that an invoice is never edited, that a credit note
/// cannot give back more than was billed, and that every one of them reaches the ledger under
/// its own source.
/// </para>
/// </summary>
public sealed class ReceivableDocuments(
    PosDbContext db, LedgerPoster ledger, RentalDocuments rentals, IRegionalSettingsProvider regionalSettings,
    ILogger<ReceivableDocuments>? logger = null)
{
    // ---------- invoices ----------

    /// <summary>
    /// Raises an invoice: typed in, or over a sale or a rental note that has already posted.
    /// <paramref name="sameBranchOnly"/> is set for a caller bound to a branch (P0.1b): the sale or
    /// note invoiced must then be of the request's branch, which CallersBranch has already made
    /// theirs. Another branch's is answered exactly as one that does not exist.
    /// </summary>
    public async Task<ReceivableResult<Invoice>> CreateInvoiceAsync(
        CreateInvoiceRequest r, string? by, CancellationToken ct = default, bool sameBranchOnly = false)
    {
        var isPaidInFull = string.Equals(r.Settlement, "full", StringComparison.OrdinalIgnoreCase);
        if (isPaidInFull && string.IsNullOrWhiteSpace(r.SettlementMethodKey))
            return ReceivableResult<Invoice>.Invalid("Paid in full names how it was paid.");

        var sources = new[] { r.SaleId, r.IssueNoteId, r.ReturnNoteId }.Count(x => x is not null);
        if (sources > 1)
            return ReceivableResult<Invoice>.Invalid("An invoice bills one document, not several.");

        // Lines typed by hand are refused at the route (owner, 2026-10-10: invoices only from a sale or a
        // rental movement); the service still builds them for the documents already on file and the tests.
        var built = sources == 1 ? await FromSourceAsync(r, sameBranchOnly, ct) : await FromRequestAsync(r, ct);
        if (!built.Ok) return built;

        return await IssueAsync(built.Value!, r.PaymentMethodKey, isPaidInFull, r.SettlementMethodKey, by, ct);
    }

    /// <summary>
    /// Phase M5a: the one invoice of a completed customer maintenance job (docs/maintenance/plan.md §3.2). Built here and
    /// issued exactly as every other invoice (<see cref="IssueAsync"/>): numbered, the buyer frozen, tax on each rate's own
    /// total, and — the job having posted nothing — posted by <see cref="ReceivablePosting.Invoice"/> under its own source
    /// as 411 / income / 4457, the customer's balance raised under the version lock. No goods leave stock: the parts left
    /// with the job's own issue documents (M2). Runs inside the caller's transaction when there is one, so the caller (the
    /// job's writer, under its lock) records the invoice on the job in the same unit of work. Whether a job may be
    /// invoiced, and once only, is the caller's to decide; this refuses a second invoice of the same job too.
    /// </summary>
    public async Task<ReceivableResult<Invoice>> CreateMaintenanceJobInvoiceAsync(MaintenanceJobBill bill, string? by, CancellationToken ct = default)
    {
        if (bill.Lines.Count == 0) return ReceivableResult<Invoice>.Invalid("An invoice needs at least one line.");
        if (await db.Invoices.AnyAsync(i => i.SourceType == InvoiceSources.MaintenanceJob && i.SourceId == bill.JobId, ct))
            return ReceivableResult<Invoice>.Conflict("This job has already been invoiced.");
        var regional = regionalSettings.Get();
        var invoice = new Invoice
        {
            CustomerId = bill.CustomerId,
            BranchId = bill.BranchId,
            Origin = InvoiceOrigin.MaintenanceJob,
            SourceType = InvoiceSources.MaintenanceJob,
            SourceId = bill.JobId,
            TaxMode = bill.TaxMode,
            IssuedAtUtc = bill.IssuedAtUtc ?? DateTime.UtcNow,
            DueDateUtc = bill.DueDateUtc,
            Notes = Trim(bill.Notes, 500)
        };
        foreach (var line in bill.Lines)
            invoice.Lines.Add(new InvoiceLine
            {
                InvoiceId = invoice.Id,
                Sku = Shorten(line.Sku, 64),
                NameSnapshot = Shorten(line.Name, 200),
                Qty = 1m,
                UnitPrice = regional.RoundMoney(line.Net),
                // Untaxed: no rate on the line at all, as every untaxed document here.
                TaxRate = bill.TaxMode == TaxMode.Untaxed ? 0m : bill.TaxRate,
                LineNet = regional.RoundMoney(line.Net),
                IncomeAccountCode = line.IncomeAccountCode
            });
        return await IssueAsync(invoice, bill.PaymentMethodKey, isPaidInFull: false, settlementMethodKey: null, by, ct);
    }

    /// <summary>
    /// Issues a built invoice: its numbering, the payment words, the tax, the buyer block, and — for an invoice that is itself
    /// the economic event (<see cref="ReceivablePosting.PostsItself"/>) — its entry and the customer's balance.
    /// </summary>
    private async Task<ReceivableResult<Invoice>> IssueAsync(
        Invoice invoice, string? paymentMethodKey, bool isPaidInFull, string? settlementMethodKey, string? by, CancellationToken ct)
    {
        var regional = regionalSettings.Get();
        // The N° column: the lines in the order they were entered, kept so every print agrees.
        for (var i = 0; i < invoice.Lines.Count; i++) invoice.Lines[i].Position = i + 1;

        if (ReceivableRules.InvoiceProblem(invoice.Lines.Count, invoice.IssuedAtUtc, invoice.DueDateUtc) is string problem)
            return ReceivableResult<Invoice>.Invalid(problem);

        // How the customer is to pay, as the paper says it. Words only — it settles nothing — but
        // only a tender that can settle an account: an invoice saying "payable in credit" or "in
        // loyalty points" would promise a payment the receipt voucher then refuses.
        if (!string.IsNullOrWhiteSpace(paymentMethodKey))
        {
            var key = paymentMethodKey.Trim().ToLowerInvariant();
            var method = await db.PaymentMethodDefs.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Key == key && m.IsActive && !m.IsDeleted, ct);
            if (method is null) return ReceivableResult<Invoice>.NotFound($"No active payment method '{key}'.");
            if (method.Behavior is PaymentBehavior.Credit or PaymentBehavior.GiftCard or PaymentBehavior.Loyalty)
                return ReceivableResult<Invoice>.Invalid($"'{key}' cannot settle a customer account; it moves a balance of its own.");
            invoice.PaymentMethodKey = method.Key;
        }

        // Tax on each rate's own total, the one rule every document here follows. An untaxed
        // invoice carries no tax and no breakdown at all, so it stays out of the tax report by
        // construction (§5.7).
        invoice.SubTotalHt = regional.RoundMoney(invoice.Lines.Sum(l => l.LineNet));
        invoice.TaxTotal = invoice.TaxMode == TaxMode.Untaxed
            ? 0m
            : DocumentTax.On(regional, NetByRate(invoice.Lines.Select(l => (l.TaxRate, l.LineNet))));
        invoice.GrandTotalTtc = invoice.SubTotalHt + invoice.TaxTotal;

        await using var tx = await db.BeginWriteScopeAsync(ct);

        // What the source document actually put on the customer's account, straight from the
        // ledger. Anything the invoice shows above that was settled before the invoice existed
        // — the cash half of a mixed sale, the unused days a return note gave back — and is
        // neither owed nor allocatable.
        if (!ReceivablePosting.PostsItself(invoice.Origin))
        {
            var onAccount = await OnAccountAsync(invoice.SourceType!, invoice.SourceId!.Value, invoice.CustomerId, ct);
            if (invoice.Origin != InvoiceOrigin.Sale && onAccount <= 0)
                return ReceivableResult<Invoice>.Conflict(
                    "This document put nothing on the customer's account, so there is nothing to invoice.");
            invoice.PrepaidAmount = Math.Max(0m, invoice.GrandTotalTtc - Math.Max(0m, onAccount));
        }

        invoice.Number = Numbered("INV", await db.Invoices.CountAsync(ct) + 1);
        db.Invoices.Add(invoice);

        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == invoice.CustomerId, ct);

        // The buyer block as it will be printed, frozen now (§8 Q12, accepted): a reprint must
        // agree with the paper first handed over, whatever the customer row says by then. Taken
        // from the row loaded at this moment and never recomputed — a credit note takes none,
        // it references the invoice.
        if (customer is not null)
        {
            invoice.BuyerName = customer.FullName;
            invoice.BuyerAddress = customer.Address;
            invoice.BuyerTaxId = customer.TaxId;
            invoice.BuyerFiscalIds = new Dictionary<string, string>(customer.FiscalIds);
        }

        // ⚠ Only a direct invoice (and a maintenance job's, the job having posted nothing) posts. Over a sale or a
        // rental note the revenue and the receivable are already in the ledger under that document, and a second entry
        // here would report the same money twice and make every contractor owe double.
        string? stockWarning = null;
        if (ReceivablePosting.PostsItself(invoice.Origin))
        {
            // P0.3, off by default: once the accountant switches it on, the goods leave stock and their cost posts in
            // this same entry. Off, nothing moves and the entry is exactly what it always was. A maintenance job's
            // invoice takes nothing out: its parts left with the job's own issue documents.
            var (cost, wentNegative) = invoice.Origin == InvoiceOrigin.Direct ? await TakeGoodsOutAsync(invoice, by, ct) : (0m, false);
            if (wentNegative) stockWarning = "Stock went negative for one or more lines.";

            var income = invoice.Lines
                .GroupBy(l => l.IncomeAccountCode)
                .Select(g => new IncomeAmount(g.Key, g.Sum(l => l.LineNet), invoice.Number));
            if (ReceivablePosting.Invoice(invoice, income, by, cost) is PostingDraft draft)
                await ledger.PostAsync(draft, ct);

            // Read again under the version lock: a till's credit sale for the same customer may
            // have committed since the customer was loaded, and this would write its debt over.
            if (customer is not null && invoice.GrandTotalTtc != 0
                && await db.LockedAsync<Customer>(customer.Id, ct) is Customer owing)
            {
                owing.Balance += invoice.GrandTotalTtc;
                await db.TouchAsync(owing, ct);
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var warning = Join(OverLimit(customer), stockWarning);
        if (isPaidInFull)
        {
            var standings = await StandingsAsync([invoice.Id], ct);
            var outstanding = standings.GetValueOrDefault(invoice.Id).Outstanding;
            if (outstanding > 0)
            {
                var receiptResult = await CreateReceiptAsync(new CreateReceiptRequest(
                    invoice.CustomerId,
                    invoice.BranchId,
                    outstanding,
                    settlementMethodKey!.Trim().ToLowerInvariant(),
                    Allocations: [new ReceiptAllocationRequest(invoice.Id, outstanding)],
                    Purpose: $"Invoice {invoice.Number}",
                    Notes: "Paid in full on issue"), by, ct);

                if (!receiptResult.Ok)
                {
                    warning = string.IsNullOrWhiteSpace(warning)
                        ? receiptResult.Message
                        : $"{warning} {receiptResult.Message}";
                }
            }
        }

        return ReceivableResult<Invoice>.Success(invoice, warning);
    }

    /// <summary>An invoice typed in the back office. This document is what creates the debt.</summary>
    private async Task<ReceivableResult<Invoice>> FromRequestAsync(CreateInvoiceRequest r, CancellationToken ct)
    {
        if (r.Lines is null || r.Lines.Count == 0)
            return ReceivableResult<Invoice>.Invalid("An invoice needs at least one line.");
        if (r.CustomerId is not Guid customerId || !await db.Customers.AnyAsync(c => c.Id == customerId && !c.IsDeleted, ct))
            return ReceivableResult<Invoice>.NotFound("Customer not found.");
        if (r.BranchId is not Guid branchId || !await db.Branches.AnyAsync(b => b.Id == branchId && !b.IsDeleted, ct))
            return ReceivableResult<Invoice>.NotFound("Branch not found.");

        var untaxed = string.Equals(r.TaxMode, nameof(TaxMode.Untaxed), StringComparison.OrdinalIgnoreCase);
        var regional = regionalSettings.Get();
        var invoice = NewInvoice(r, customerId, branchId, InvoiceOrigin.Direct);
        invoice.TaxMode = untaxed ? TaxMode.Untaxed : TaxMode.Taxed;

        var productIds = r.Lines.Where(l => l.ProductId is not null).Select(l => l.ProductId!.Value).Distinct().ToList();
        var products = await db.Products.AsNoTracking()
            .Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);

        foreach (var line in r.Lines)
        {
            var discount = Math.Max(0m, line.Discount);
            if (ReceivableRules.LineProblem(line.Qty, line.UnitPrice, discount, line.TaxRate, line.Days) is string problem)
                return ReceivableResult<Invoice>.Invalid(problem);

            var product = line.ProductId is Guid id ? products.GetValueOrDefault(id) : null;
            if (line.ProductId is not null && product is null)
                return ReceivableResult<Invoice>.NotFound($"Product {line.ProductId} not found.");
            // The sale policy (ManufacturingRoles.ForSale): a document typed for a customer is a new sale, so a
            // raw material, consumable or semi-finished good not marked for sale is refused here. An invoice
            // raised from a recorded sale is not: that sale already happened.
            if (product is not null && !ManufacturingRoles.ForSale(product))
                return ReceivableResult<Invoice>.Invalid(
                    $"{product.Sku} is a manufacturing material and is not for sale. Mark it for sale on the manufacturing materials screen first.");

            invoice.Lines.Add(new InvoiceLine
            {
                InvoiceId = invoice.Id,
                ProductId = product?.Id,
                Sku = product?.Sku ?? (line.Sku ?? "").Trim(),
                NameSnapshot = Shorten(
                    !string.IsNullOrWhiteSpace(line.Name) ? line.Name
                    : product is null ? "" : FirstName(product.NameFr, product.NameEn, product.NameAr), 200),
                Qty = line.Qty,
                UnitPrice = line.UnitPrice,
                Days = line.Days,
                // An untaxed invoice has no rates on it at all, not rates that happen to be
                // ignored: a rate stored on a line nothing taxes is the sort of thing a later
                // report picks up and believes.
                TaxRate = untaxed ? 0m : line.TaxRate,
                LineDiscount = discount,
                LineNet = regional.RoundMoney(ReceivableRules.LineGross(line.Qty, line.UnitPrice, line.Days) - discount),
                IncomeAccountCode = LedgerAccounts.Sales
            });
        }

        return ReceivableResult<Invoice>.Success(invoice);
    }

    /// <summary>
    /// An invoice over a document that has already posted. Its lines are copied for the paper;
    /// what it actually bills comes from the ledger.
    /// </summary>
    private async Task<ReceivableResult<Invoice>> FromSourceAsync(
        CreateInvoiceRequest r, bool sameBranchOnly, CancellationToken ct)
    {
        var (sourceType, sourceId) = r.SaleId is Guid sale ? (LedgerSources.Sale, sale)
            : r.IssueNoteId is Guid issue ? (LedgerSources.RentalIssue, issue)
            : (LedgerSources.RentalReturn, r.ReturnNoteId!.Value);

        // Before anything else is said about the document — "already invoiced" included, which
        // would tell a branch that another branch's sale exists (P0.1b).
        if (sameBranchOnly)
        {
            var theirs = sourceType == LedgerSources.Sale
                ? await db.Sales.AsNoTracking().Where(s => s.Id == sourceId).Select(s => (Guid?)s.BranchId).FirstOrDefaultAsync(ct)
                : sourceType == LedgerSources.RentalIssue
                    ? await db.IssueNotes.AsNoTracking().Where(n => n.Id == sourceId).Select(n => (Guid?)n.BranchId).FirstOrDefaultAsync(ct)
                    : await db.ReturnNotes.AsNoTracking().Where(n => n.Id == sourceId).Select(n => (Guid?)n.BranchId).FirstOrDefaultAsync(ct);
            if (theirs is Guid branch && branch != r.BranchId)
                return ReceivableResult<Invoice>.NotFound(
                    sourceType == LedgerSources.Sale ? "Sale not found." : "Rental note not found.");
        }

        // One invoice per document. Without this the same 411 debit could be billed twice and
        // the customer chased for money they owe once.
        if (await db.Invoices.AnyAsync(i => i.SourceType == sourceType && i.SourceId == sourceId, ct))
            return ReceivableResult<Invoice>.Conflict("This document has already been invoiced.");

        return sourceType switch
        {
            LedgerSources.Sale => await FromSaleAsync(r, sourceId, ct),
            _ => await FromRentalAsync(r, sourceType, sourceId, ct)
        };
    }

    private async Task<ReceivableResult<Invoice>> FromSaleAsync(CreateInvoiceRequest r, Guid saleId, CancellationToken ct)
    {
        var sale = await db.Sales.AsNoTracking().Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == saleId, ct);
        if (sale is null) return ReceivableResult<Invoice>.NotFound("Sale not found.");
        if (sale.CustomerId is not Guid customerId)
            return ReceivableResult<Invoice>.Invalid("This sale has no customer, so there is nobody to invoice.");
        // Its invoice inherits the sale's choice, made at the till as the customer asked (owner, 2026-11-05).

        var invoice = NewInvoice(r, customerId, sale.BranchId, InvoiceOrigin.Sale);
        invoice.SourceType = LedgerSources.Sale;
        invoice.SourceId = sale.Id;
        // Inherited, never re-decided: whether that sale was taxed was settled at the till and
        // the tax report reads both documents.
        invoice.TaxMode = sale.TaxMode;
        invoice.Notes ??= $"Sale {sale.Reference}";

        foreach (var line in sale.Lines)
            invoice.Lines.Add(new InvoiceLine
            {
                InvoiceId = invoice.Id,
                ProductId = line.ProductId,
                Sku = line.Sku,
                NameSnapshot = line.NameSnapshot,
                Qty = line.Qty,
                // The till stores its line totals tax-inclusive; an invoice line is net, and
                // the tax is the document's.
                UnitPrice = line.Qty == 0 ? 0m : (line.LineTotalTtc - line.LineTaxAmount) / line.Qty,
                TaxRate = sale.TaxMode == TaxMode.Untaxed ? 0m : line.TaxRate,
                LineNet = line.LineTotalTtc - line.LineTaxAmount,
                IncomeAccountCode = LedgerAccounts.Sales
            });

        return ReceivableResult<Invoice>.Success(invoice);
    }

    private async Task<ReceivableResult<Invoice>> FromRentalAsync(
        CreateInvoiceRequest r, string sourceType, Guid noteId, CancellationToken ct)
    {
        var billing = sourceType == LedgerSources.RentalIssue
            ? await rentals.IssueNoteBillingAsync(noteId, ct)
            : await rentals.ReturnNoteBillingAsync(noteId, ct);
        if (billing is null) return ReceivableResult<Invoice>.NotFound("Rental note not found.");
        if (billing.Count == 0)
            return ReceivableResult<Invoice>.Conflict("This note charged nothing, so there is nothing to invoice.");

        var (contractId, branchId, reference) = sourceType == LedgerSources.RentalIssue
            ? await db.IssueNotes.AsNoTracking().Where(n => n.Id == noteId)
                .Select(n => new ValueTuple<Guid, Guid, string>(n.ContractId, n.BranchId, n.Number)).FirstAsync(ct)
            : await db.ReturnNotes.AsNoTracking().Where(n => n.Id == noteId)
                .Select(n => new ValueTuple<Guid, Guid, string>(n.ContractId, n.BranchId, n.Number)).FirstAsync(ct);

        var contract = await db.RentalContracts.AsNoTracking().FirstAsync(c => c.Id == contractId, ct);

        var invoice = NewInvoice(r, contract.CustomerId, branchId, InvoiceOrigin.Rental);
        invoice.SourceType = sourceType;
        invoice.SourceId = noteId;
        invoice.Notes ??= $"Rental {reference}";
        // Whether hire is taxed here is the company's rental tax rate, decided when the note was
        // raised and frozen on the contract lines. There is nothing to choose at this point.
        invoice.TaxMode = billing.Any(l => l.TaxRate > 0) ? TaxMode.Taxed : TaxMode.Untaxed;

        foreach (var line in billing)
            invoice.Lines.Add(new InvoiceLine
            {
                InvoiceId = invoice.Id,
                ProductId = line.ProductId,
                Sku = line.Sku,
                NameSnapshot = Shorten(line.Name, 200),
                Qty = line.Qty,
                UnitPrice = line.UnitPrice,
                Days = line.Days,
                TaxRate = line.TaxRate,
                LineNet = line.Net,
                RentalContractLineId = line.ContractLineId,
                RentalReturnAllocationId = line.ReturnAllocationId,
                IncomeAccountCode = line.IncomeAccountCode
            });

        return ReceivableResult<Invoice>.Success(invoice);
    }

    /// <summary>
    /// P0.3: a direct invoice's goods leave the invoice's branch, once the accountant has switched it on
    /// (<see cref="StockValuationSettings.DirectInvoiceStock"/>, off by default). Each line whose product tracks stock
    /// writes a movement out (<see cref="StockMovementKind.InvoiceSale"/>), takes the quantity off the branch's stock row
    /// and records on the line the cost it left at: the product's cost price, or the weighted average of the moment once
    /// sales are costed at it (C-01) — through <see cref="LiveSaleCosting"/>, exactly as a till sale, with its fallback to
    /// the cost price. A line billed by the day is hire, not goods sold, and moves nothing.
    /// <para>
    /// Never refused for a shortfall, as a till sale is not (rule 1): stock may go negative, and the caller is told.
    /// Runs inside the invoice's own transaction, before its entry posts.
    /// </para>
    /// </summary>
    /// <returns>Σ quantity × unit cost, rounded as the ledger keeps money; and whether any stock row went below zero.</returns>
    private async Task<(decimal Cost, bool WentNegative)> TakeGoodsOutAsync(Invoice invoice, string? by, CancellationToken ct)
    {
        var settings = await db.StockValuationSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings is not { DirectInvoiceStock: true }) return (0m, false);

        var productIds = invoice.Lines.Where(l => l.ProductId is not null && l.Days is null)
            .Select(l => l.ProductId!.Value).Distinct().ToList();
        if (productIds.Count == 0) return (0m, false);
        // Tracked, so the version stamp below reaches every till that shows this product's stock.
        var products = await db.Products.Where(p => productIds.Contains(p.Id) && p.TrackStock).ToDictionaryAsync(p => p.Id, ct);
        var lines = invoice.Lines.Where(l => l.ProductId is Guid id && l.Days is null && products.ContainsKey(id)).ToList();
        if (lines.Count == 0) return (0m, false);

        var moved = lines.GroupBy(l => l.ProductId!.Value)
            .ToDictionary(g => g.Key, g => (Qty: g.Sum(l => l.Qty), products[g.Key].CostPrice));
        var live = await LiveCostsAsync(invoice, moved, ct);

        // Under the version lock, as a till sale: another writer of the same row commits first or waits.
        var stocks = new StockRows(db);
        var cost = 0m;
        var wentNegative = false;
        foreach (var line in lines)
        {
            var product = products[line.ProductId!.Value];
            var (unitCost, basis) = live is not null && live.TryGetValue(product.Id, out var c)
                ? (c.UnitCost, c.Basis)
                : (product.CostPrice, SaleCostBasis.CostPrice);

            var stock = await stocks.GetAsync(product.Id, invoice.BranchId, ct);
            stock.StockQty -= line.Qty;
            wentNegative |= stock.StockQty < 0;

            db.StockMovements.Add(new StockMovement
            {
                ProductId = product.Id,
                BranchId = invoice.BranchId,
                QtyDelta = -line.Qty,
                BalanceAfter = stock.StockQty,
                Reason = "invoice",
                Kind = StockMovementKind.InvoiceSale,
                UnitCost = unitCost,
                DocumentId = invoice.Id,
                DocumentNumber = invoice.Number,
                UserName = by,
                MovedQty = line.Qty
            });

            line.StockUnitCost = unitCost;
            line.StockCostBasis = basis;
            cost += line.Qty * unitCost;
        }
        foreach (var product in products.Values) await db.TouchAsync(product, ct);

        return (Math.Round(cost, 3, MidpointRounding.AwayFromZero), wentNegative);
    }

    /// <summary>
    /// The weighted average of the moment for each product (C-01), or null while sales are costed at the cost price.
    /// <b>Never throws</b>, like the till's: an invoice is never refused because a cost could not be worked out — every
    /// line then goes out at the cost price, marked <see cref="SaleCostBasis.CostPriceFallback"/>.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, LiveLineCost>?> LiveCostsAsync(
        Invoice invoice, IReadOnlyDictionary<Guid, (decimal Qty, decimal CostPrice)> moved, CancellationToken ct)
    {
        try
        {
            return await new LiveSaleCosting(db).CostsAsync(invoice.BranchId, isRefund: false, refundOfSaleId: null, moved, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogError(ex, "Invoice {InvoiceId}: the weighted average could not be worked out; its lines are costed at the cost price", invoice.Id);
            return moved.ToDictionary(x => x.Key, x => new LiveLineCost(x.Value.CostPrice, SaleCostBasis.CostPriceFallback));
        }
    }

    private static string? Join(string? first, string? second) =>
        string.IsNullOrWhiteSpace(first) ? second : string.IsNullOrWhiteSpace(second) ? first : $"{first} {second}";

    private static Invoice NewInvoice(CreateInvoiceRequest r, Guid customerId, Guid branchId, InvoiceOrigin origin) =>
        new()
        {
            CustomerId = customerId,
            BranchId = branchId,
            Origin = origin,
            IssuedAtUtc = r.IssuedAtUtc ?? DateTime.UtcNow,
            DueDateUtc = r.DueDateUtc,
            Notes = Trim(r.Notes, 500)
        };

    // ---------- receipts ----------

    /// <summary>
    /// Money taken from a customer against their account.
    ///
    /// <para>
    /// ⚠ Whether it counts is decided by the tender, not by the clerk. Cash is money the moment
    /// it is taken; a cheque is <see cref="PaymentStatus.Pending"/> and settles nothing at all
    /// until somebody confirms it cleared. The allocations are still recorded on a pending
    /// voucher — the clerk knows what the cheque is for and nobody will know later — but they
    /// are a claim on those invoices, not a payment of them.
    /// </para>
    /// <para>
    /// <paramref name="sameBranchOnly"/> is set for a caller bound to a branch (P0.1b): every invoice
    /// it settles and the contract it names must be of the receipt's branch, which CallersBranch has
    /// already made theirs; another branch's is answered exactly as one that does not exist. Taking
    /// money at one branch for another branch's documents waits on the owner's rule for it
    /// (explicit authorization, branch checks and an audit record).
    /// </para>
    /// </summary>
    public async Task<ReceivableResult<ReceiptVoucher>> CreateReceiptAsync(
        CreateReceiptRequest r, string? by, CancellationToken ct = default, bool sameBranchOnly = false)
    {
        if (r.Amount <= 0) return ReceivableResult<ReceiptVoucher>.Invalid("The amount must be above zero.");

        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == r.CustomerId && !c.IsDeleted, ct);
        if (customer is null) return ReceivableResult<ReceiptVoucher>.NotFound("Customer not found.");
        if (!await db.Branches.AnyAsync(b => b.Id == r.BranchId && !b.IsDeleted, ct))
            return ReceivableResult<ReceiptVoucher>.NotFound("Branch not found.");

        var key = (r.MethodKey ?? "").Trim().ToLowerInvariant();
        var method = await db.PaymentMethodDefs.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Key == key && m.IsActive && !m.IsDeleted, ct);
        if (method is null) return ReceivableResult<ReceiptVoucher>.NotFound($"No active payment method '{key}'.");

        // A debt is not paid off with more credit, with a gift card or with points. Those three
        // behaviours all move a balance of their own, and letting one settle a receivable would
        // move two balances for one act.
        if (method.Behavior is PaymentBehavior.Credit or PaymentBehavior.GiftCard or PaymentBehavior.Loyalty)
            return ReceivableResult<ReceiptVoucher>.Invalid(
                $"'{key}' cannot settle a customer account; it moves a balance of its own.");

        var voucher = new ReceiptVoucher
        {
            CustomerId = customer.Id,
            BranchId = r.BranchId,
            ReceivedAtUtc = r.ReceivedAtUtc ?? DateTime.UtcNow,
            MethodKey = method.Key,
            Behavior = method.Behavior,
            // The tender decides. Cash is confirmed on the spot; a cheque, a transfer or anything
            // an operator added is paper until someone says otherwise. So is a card typed here
            // (owner, security review 2026-10-10): at the till the terminal's slip stands behind
            // it, in the back office nothing does until someone checks the bank statement.
            Status = method.RequiresConfirmation || method.Behavior == PaymentBehavior.Card
                ? PaymentStatus.Pending : PaymentStatus.Confirmed,
            Amount = r.Amount,
            Reference = Trim(r.Reference, 100),
            InstrumentNumber = Trim(r.InstrumentNumber, 60),
            InstrumentBank = Trim(r.InstrumentBank, 120),
            InstrumentDueDate = r.InstrumentDueDate,
            Notes = Trim(r.Notes, 500),
            CreatedBy = by
        };
        if (voucher.Status == PaymentStatus.Confirmed) voucher.ConfirmedAtUtc = DateTime.UtcNow;

        // One line per invoice: the same invoice listed twice was checked twice against the same
        // open amount, and together could take more than was open on it (security review, 2026-10-10).
        var allocations = (r.Allocations ?? [])
            .GroupBy(a => a.InvoiceId)
            .Select(g => g.First() with { Amount = g.Sum(a => a.Amount) })
            .ToList();
        if (allocations.Sum(a => a.Amount) > r.Amount)
            return ReceivableResult<ReceiptVoucher>.Invalid("More has been allocated than was received.");

        var invoiceIds = allocations.Select(a => a.InvoiceId).Distinct().ToList();
        var invoices = await db.Invoices.AsNoTracking().Where(i => invoiceIds.Contains(i.Id)).ToListAsync(ct);
        var standings = await StandingsAsync(invoiceIds, ct);

        foreach (var allocation in allocations)
        {
            var invoice = invoices.FirstOrDefault(i => i.Id == allocation.InvoiceId);
            if (invoice is null || (sameBranchOnly && invoice.BranchId != r.BranchId))
                return ReceivableResult<ReceiptVoucher>.NotFound("Invoice not found.");
            if (invoice.CustomerId != customer.Id)
                return ReceivableResult<ReceiptVoucher>.Invalid(
                    $"Invoice {invoice.Number} belongs to another customer.");
            if (ReceivableRules.AllocationProblem(allocation.Amount, standings[invoice.Id]) is string problem)
                return ReceivableResult<ReceiptVoucher>.Invalid($"Invoice {invoice.Number}: {problem}");

            voucher.Allocations.Add(new ReceiptAllocation
            {
                ReceiptVoucherId = voucher.Id,
                InvoiceId = invoice.Id,
                Amount = allocation.Amount
            });
        }

        // What the money is for, and the contract it belongs to (answers 19, 20, 26).
        if (await ApplyKindAsync(voucher, r, invoices, sameBranchOnly, ct) is string kindProblem)
            return ReceivableResult<ReceiptVoucher>.Invalid(kindProblem);

        // The "in exchange for" line (answer 23): what the clerk wrote, else the rental contract
        // the allocated invoices bill, or the repair or loss and its return note. Blank is left
        // blank when nothing is rental.
        voucher.Purpose = Trim(r.Purpose?.Trim(), 300) is { Length: > 0 } purpose
            ? purpose
            : await KindPurposeAsync(voucher, ct) ?? await DefaultPurposeAsync(invoiceIds, ct);

        await using var tx = await db.BeginWriteScopeAsync(ct);

        voucher.Number = Numbered("RCV", await db.ReceiptVouchers.CountAsync(ct) + 1);
        db.ReceiptVouchers.Add(voucher);

        if (voucher.Status == PaymentStatus.Confirmed) await ApplyConfirmedAsync(voucher, customer, by, ct);

        await db.SaveChangesAsync(ct);
        // Money that arrived may finish paying a contract whose goods are back (answer 15). A
        // cheque waits for its confirmation (answer 22): a pending receipt closes nothing.
        if (voucher.Status == PaymentStatus.Confirmed && await CloseWhatItPaysAsync(voucher, ct))
            await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return ReceivableResult<ReceiptVoucher>.Success(voucher);
    }

    /// <summary>
    /// Sets what the receipt is for. A repair or a loss is charged on a return note when the goods
    /// come back, so its receipt names that note and its contract, both the customer's (answer 19:
    /// "it is made clear that these are repair or loss amounts for contract number …"). A payment
    /// may name its contract; when it does not and every invoice it settles is one contract's, it
    /// takes that contract, so the contract's paid figure counts it either way.
    /// </summary>
    private async Task<string?> ApplyKindAsync(
        ReceiptVoucher voucher, CreateReceiptRequest r, IReadOnlyList<Invoice> invoices, bool sameBranchOnly,
        CancellationToken ct)
    {
        var kindText = (r.Kind ?? "").Trim();
        if (kindText.Length == 0) voucher.Kind = ReceiptKind.Payment;
        else if (!Enum.TryParse<ReceiptKind>(kindText, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
            return "A receipt is for a payment, a repair, a loss or an advance.";
        else voucher.Kind = kind;

        if (voucher.Kind == ReceiptKind.Advance) return await ApplyAdvanceKindAsync(voucher, r, sameBranchOnly, ct);
        if (r.AdvanceTaxRate is > 0m || r.MaintenanceJobId is not null)
            return "Only an advance carries VAT when it is received or names a maintenance job.";

        if (r.ContractId is Guid contractId)
        {
            var contract = await db.RentalContracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == contractId, ct);
            if (contract is null || (sameBranchOnly && contract.BranchId != voucher.BranchId)) return "Rental contract not found.";
            if (contract.Status == RentalContractStatus.Cancelled) return $"Contract {contract.Number} was cancelled.";
            if (contract.CustomerId != voucher.CustomerId) return $"Contract {contract.Number} is another customer's.";
            voucher.ContractId = contractId;
        }

        if (voucher.Kind is ReceiptKind.Repair or ReceiptKind.Loss)
        {
            var what = voucher.Kind == ReceiptKind.Repair ? "repair" : "loss";
            if (voucher.ContractId is null || r.ReturnNoteId is null)
                return $"A receipt for a {what} names the contract and the return note that recorded it.";
            var note = await db.ReturnNotes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == r.ReturnNoteId, ct);
            if (note is null) return "Return note not found.";
            if (note.ContractId != voucher.ContractId) return $"Return note {note.Number} is another contract's.";
            voucher.ReturnNoteId = note.Id;
            return null;
        }
        if (r.ReturnNoteId is not null) return "Only a receipt for a repair or a loss names a return note.";

        if (voucher.ContractId is null && invoices.Count > 0)
        {
            var contracts = await RentalContractsOfAsync(invoices.Select(i => i.Id).ToList(), ct);
            if (contracts.Count == 1 && invoices.All(i => i.Origin == InvoiceOrigin.Rental)) voucher.ContractId = contracts[0];
        }
        return null;
    }

    /// <summary>
    /// M7b: a receipt taken as an advance. Only while the accountant holds advances on 419 (on 411 an advance is simply a
    /// payment allocated to nothing, as it always was); it pays no invoice and names no contract when taken. Its VAT is
    /// worked out now and frozen — only when the accountant made an advance's VAT due on receipt <b>and</b> the seller ticked
    /// a rate (VAT only on request, owner 2026-11-05): the amount received is tax included, so the tax inside it is
    /// amount × rate / (100 + rate). MS-35: it may name a customer maintenance job of the same customer, still open, once
    /// Maintenance › Settings takes advances.
    /// </summary>
    private async Task<string?> ApplyAdvanceKindAsync(
        ReceiptVoucher voucher, CreateReceiptRequest r, bool sameBranchOnly, CancellationToken ct)
    {
        var settings = await ReceivableSettingsStore.CurrentAsync(db, ct);
        if (settings.AdvancesHeldOn != CustomerAdvanceAccount.AdvancesAccount)
            return "Advances are held on the customer's account (411) in the receivables settings: record a payment allocated to nothing instead.";
        if (voucher.Allocations.Count > 0)
            return "An advance pays no invoice when it is taken: apply it to the invoice once the invoice exists.";
        if (r.ContractId is not null || r.ReturnNoteId is not null)
            return "An advance names no contract and no return note.";

        if (r.AdvanceTaxRate is decimal rate && rate != 0m)
        {
            if (settings.AdvanceVat != AdvanceVatTiming.WhenReceived)
                return "An advance's VAT is due at the invoice in the receivables settings: take it without VAT.";
            var regional = regionalSettings.Get();
            if (rate is < 0m or > 100m || (regional.TaxRates.Count > 0 && !regional.TaxRates.Contains(rate)))
                return $"{rate:0.##} % is not one of the company's tax rates.";
            voucher.AdvanceTaxRate = rate;
            voucher.AdvanceTaxAmount = regional.RoundMoney(voucher.Amount * rate / (100m + rate));
        }

        if (r.MaintenanceJobId is Guid jobId)
        {
            if ((await db.MaintenanceSettings.AsNoTracking().FirstOrDefaultAsync(ct))?.Advances != MaintenanceAdvances.Taken)
                return "Advances on customer maintenance jobs are not taken (Maintenance › Settings, MS-35).";
            var job = await db.MaintenanceJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
            if (job is null || (sameBranchOnly && job.BranchId != voucher.BranchId)) return "Maintenance job not found.";
            if (job.Kind != MaintenanceJobKind.Customer || job.CustomerId != voucher.CustomerId)
                return $"Maintenance job {job.Number} is not this customer's job.";
            if (job.InvoiceId is not null || job.Status is MaintenanceJobStatus.Invoiced or MaintenanceJobStatus.Cancelled)
                return $"Maintenance job {job.Number} is already invoiced or cancelled: it takes no advance.";
            voucher.MaintenanceJobId = job.Id;
        }
        return null;
    }

    // ---------- advances held on 419 (M7b) ----------

    /// <summary>
    /// Moves part of an advance held on 419 to the customer's account: against one of their invoices (which then reads
    /// paid by that much: an allocation is recorded on the advance) or, with no invoice, released to the account — before a
    /// refund, or against a debt with no invoice behind it. Posts Dr 419 / Dr 4457 (the share of the advance's VAT, when it
    /// was due on receipt) / Cr 411, dated <paramref name="at"/> (now by default). The customer's balance does not move:
    /// the money was theirs on both accounts. Allowed whatever the setting says now: an advance knows where it was posted.
    /// </summary>
    /// <param name="sameBranchOnly">A caller bound to a branch: the invoice must be the advance's branch's too.</param>
    public async Task<ReceivableResult<ReceiptVoucher>> ApplyAdvanceAsync(
        Guid voucherId, ApplyAdvanceRequest r, string? by, CancellationToken ct = default, bool sameBranchOnly = false,
        DateTime? at = null)
    {
        if (r.Amount is decimal asked && asked <= 0) return ReceivableResult<ReceiptVoucher>.Invalid("The amount must be above zero.");

        await using var tx = await db.BeginWriteScopeAsync(ct);
        var advance = await db.LockedAsync<ReceiptVoucher>(voucherId, ct);
        if (advance is null) return ReceivableResult<ReceiptVoucher>.NotFound("Receipt not found.");
        if (advance.Kind != ReceiptKind.Advance)
            return ReceivableResult<ReceiptVoucher>.Invalid($"{advance.Number} is not an advance: its allocations were decided when it was taken.");
        if (advance.Status != PaymentStatus.Confirmed)
            return ReceivableResult<ReceiptVoucher>.Conflict($"{advance.Number} is {advance.Status}: an advance is applied once its money has arrived.");
        await db.Entry(advance).Collection(v => v.Allocations).LoadAsync(ct);

        var applied = (await db.CustomerAdvanceApplications.AsNoTracking()
            .Where(a => a.ReceiptVoucherId == advance.Id).Select(a => new { a.Amount, a.TaxAmount }).ToListAsync(ct));
        var open = advance.Amount - applied.Sum(a => a.Amount);
        var openTax = advance.AdvanceTaxAmount - applied.Sum(a => a.TaxAmount);
        if (open <= 0) return ReceivableResult<ReceiptVoucher>.Conflict($"{advance.Number} has been applied in full.");

        Invoice? invoice = null;
        var amount = r.Amount ?? open;
        if (r.InvoiceId is Guid invoiceId)
        {
            invoice = await db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId, ct);
            if (invoice is null || (sameBranchOnly && invoice.BranchId != advance.BranchId))
                return ReceivableResult<ReceiptVoucher>.NotFound("Invoice not found.");
            if (invoice.CustomerId != advance.CustomerId)
                return ReceivableResult<ReceiptVoucher>.Invalid($"Invoice {invoice.Number} belongs to another customer.");
            var standing = (await StandingsAsync([invoice.Id], ct))[invoice.Id];
            if (r.Amount is null) amount = Math.Min(open, standing.Outstanding);
            if (amount <= 0)
                return ReceivableResult<ReceiptVoucher>.Conflict($"Invoice {invoice.Number} has nothing left to pay.");
            if (ReceivableRules.AllocationProblem(amount, standing) is string problem)
                return ReceivableResult<ReceiptVoucher>.Invalid($"Invoice {invoice.Number}: {problem}");
        }
        if (amount > open)
            return ReceivableResult<ReceiptVoucher>.Invalid($"{advance.Number} holds only {open:0.##}, not {amount:0.##}.");

        var tax = AdvanceTaxShare(advance, amount, open, openTax);

        var application = new CustomerAdvanceApplication
        {
            ReceiptVoucherId = advance.Id,
            InvoiceId = invoice?.Id,
            Amount = amount,
            TaxAmount = Math.Max(0m, tax),
            // Never before the money arrived: the entry would otherwise clear 419 before anything was on it.
            AppliedAtUtc = Max(at ?? DateTime.UtcNow, advance.ReceivedAtUtc),
            CreatedBy = by
        };
        if (invoice is not null)
        {
            var allocation = new ReceiptAllocation { ReceiptVoucherId = advance.Id, InvoiceId = invoice.Id, Amount = amount };
            // Added explicitly: found only through the tracked advance's collection, a row with its key already set
            // would be taken for an existing one and updated.
            db.ReceiptAllocations.Add(allocation);
            advance.Allocations.Add(allocation);
            application.ReceiptAllocationId = allocation.Id;
        }
        db.CustomerAdvanceApplications.Add(application);

        if (ReceivablePosting.AdvanceApplication(application, advance, invoice?.Number, by) is PostingDraft draft)
            await ledger.PostAsync(draft, ct);

        await db.SaveChangesAsync(ct);
        // Paid now, a rental contract whose goods are back may close (answer 15), as for any confirmed receipt.
        if (invoice is not null && await CloseWhatItPaysAsync(advance, ct)) await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ReceivableResult<ReceiptVoucher>.Success(advance);
    }

    /// <summary>
    /// The part of an advance's VAT that goes with <paramref name="amount"/> of it, in proportion and rounded the company's
    /// way; the last part takes what is left, so the whole of it is accounted for exactly once whatever the rounding of the
    /// parts before. Shared by an application (which gives it back) and a keep (SAL-13, which leaves it due).
    /// </summary>
    private decimal AdvanceTaxShare(ReceiptVoucher advance, decimal amount, decimal open, decimal openTax) =>
        Math.Max(0m, amount == open
            ? openTax
            : Math.Min(openTax, regionalSettings.Get().RoundMoney(advance.AdvanceTaxAmount * amount / advance.Amount)));

    public const string KeptAdvancesNotAllowedCode = "kept_advances_not_allowed";

    /// <summary>
    /// SAL-13 (owner 2026-11-10, answer A; the accountant's setting): part of a customer's money kept on a cancelled order,
    /// moved to the income account the accountant chose. The rest is refunded the usual way (release, then a refund voucher).
    /// <list type="bullet">
    /// <item>Refused (400) while the setting is "Not allowed" — the default — and without a written reason.</item>
    /// <item>An advance on 419: at most what it still holds. Dr 419 / Cr income for the part net of its VAT; with VAT when
    /// received, its VAT part (the same proportional share an application takes) stays due on 4457, moved off the
    /// customer.</item>
    /// <item>A payment receipt on 411: at most its unallocated part not kept before, and at most the customer's credit on
    /// 411 (as a refund's cap: less refunds raised and not paid, less what advances still hold on 419). Dr 411 / Cr income.</item>
    /// </list>
    /// The customer's balance goes up by the amount: that money is no longer theirs. Dated now, through the ledger (closed
    /// months refused). Recorded as an append-only <see cref="CustomerAdvanceApplication"/> of kind Kept.
    /// </summary>
    public async Task<ReceivableResult<ReceiptVoucher>> KeepAdvanceAsync(
        Guid receiptId, KeepAdvanceRequest r, string? by, CancellationToken ct = default)
    {
        var settings = await ReceivableSettingsStore.CurrentAsync(db, ct);
        if (settings.KeptAdvances != KeptAdvancePolicy.OtherIncome)
            return new(default, ReceivableFailure.Invalid,
                "Keeping an advance is not allowed in the receivables settings (SAL-13): refund it instead.", Code: KeptAdvancesNotAllowedCode);
        var reason = r.Reason?.Trim();
        if (string.IsNullOrEmpty(reason)) return ReceivableResult<ReceiptVoucher>.Invalid("Say why the money is kept.");
        if (reason.Length > 400) return ReceivableResult<ReceiptVoucher>.Invalid("The reason is at most 400 characters.");
        if (r.Amount is decimal asked && asked <= 0) return ReceivableResult<ReceiptVoucher>.Invalid("The amount must be above zero.");
        var account = settings.KeptAdvanceIncomeAccount;
        if (!await ReceivableSettingsStore.IncomeAccountReadyAsync(db, account, ct))
            return new(default, ReceivableFailure.Conflict,
                $"The income account chosen for kept advances ({account ?? "none"}) is not an active income account: choose one in the receivables settings.",
                Code: ReceivableSettingsStore.AccountsNotMappedCode);

        await using var tx = await db.BeginWriteScopeAsync(ct);
        var receipt = await db.LockedAsync<ReceiptVoucher>(receiptId, ct);
        if (receipt is null) return ReceivableResult<ReceiptVoucher>.NotFound("Receipt not found.");
        if (receipt.Kind is not (ReceiptKind.Advance or ReceiptKind.Payment))
            return ReceivableResult<ReceiptVoucher>.Invalid($"{receipt.Number} pays a repair or a loss: nothing of it is an advance.");
        if (receipt.Status != PaymentStatus.Confirmed)
            return ReceivableResult<ReceiptVoucher>.Conflict($"{receipt.Number} is {receipt.Status}: only money that has arrived can be kept.");
        await db.Entry(receipt).Collection(v => v.Allocations).LoadAsync(ct);

        var moved = await db.CustomerAdvanceApplications.AsNoTracking()
            .Where(a => a.ReceiptVoucherId == receipt.Id).Select(a => new { a.Amount, a.TaxAmount, a.Kind }).ToListAsync(ct);
        decimal amount, tax = 0m;
        if (receipt.Kind == ReceiptKind.Advance)
        {
            var open = receipt.Amount - moved.Sum(a => a.Amount);
            var openTax = receipt.AdvanceTaxAmount - moved.Sum(a => a.TaxAmount);
            if (open <= 0) return ReceivableResult<ReceiptVoucher>.Conflict($"{receipt.Number} has been applied in full: nothing is left to keep.");
            amount = r.Amount ?? open;
            if (amount > open)
                return ReceivableResult<ReceiptVoucher>.Invalid($"{receipt.Number} holds only {open:0.##}, not {amount:0.##}.");
            tax = AdvanceTaxShare(receipt, amount, open, openTax);
        }
        else
        {
            var unallocated = receipt.Amount - receipt.Allocations.Sum(a => a.Amount)
                              - moved.Where(a => a.Kind == AdvanceMoveKind.Kept).Sum(a => a.Amount);
            var customer = await db.Customers.AsNoTracking().FirstAsync(c => c.Id == receipt.CustomerId, ct);
            var credit = await PaymentVouchers.RefundableCreditAsync(db, customer, ct);
            var cap = Math.Min(unallocated, credit);
            if (cap <= 0)
                return ReceivableResult<ReceiptVoucher>.Conflict(unallocated <= 0
                    ? $"{receipt.Number} has no unallocated part left to keep."
                    : $"The customer has no credit left on their account: {receipt.Number}'s unallocated part has been used or refunded.");
            amount = r.Amount ?? cap;
            if (amount > cap)
                return ReceivableResult<ReceiptVoucher>.Invalid(
                    $"At most {cap:0.##} of {receipt.Number} can be kept (unallocated {unallocated:0.##}, customer's credit {credit:0.##}), not {amount:0.##}.");
        }

        var kept = new CustomerAdvanceApplication
        {
            ReceiptVoucherId = receipt.Id,
            Kind = AdvanceMoveKind.Kept,
            Amount = amount,
            TaxAmount = tax,
            Reason = reason,
            IncomeAccountCode = account,
            // Never before the money arrived.
            AppliedAtUtc = Max(DateTime.UtcNow, receipt.ReceivedAtUtc),
            CreatedBy = by
        };
        db.CustomerAdvanceApplications.Add(kept);

        // The money is no longer the customer's: their recorded balance (negative while we hold their money) goes up.
        if (await db.LockedAsync<Customer>(receipt.CustomerId, ct) is Customer owner)
        {
            owner.Balance += amount;
            await db.TouchAsync(owner, ct);
        }

        if (ReceivablePosting.AdvanceKept(kept, receipt, by) is PostingDraft draft)
            await ledger.PostAsync(draft, ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ReceivableResult<ReceiptVoucher>.Success(receipt);
    }

    /// <summary>
    /// MS-35: a customer job's invoice clears the advances taken for that job — every confirmed one still holding
    /// something, oldest first, as far as the invoice goes — dated with the invoice. Runs inside the caller's transaction.
    /// What is left of an advance larger than the invoice stays on 419 (applied or released later by hand); a cheque still
    /// pending is applied when it is confirmed. Answers how much was applied.
    /// </summary>
    public async Task<decimal> ApplyJobAdvancesAsync(Guid jobId, Invoice invoice, string? by, CancellationToken ct = default, DateTime? at = null)
    {
        var advances = await db.ReceiptVouchers.AsNoTracking()
            .Where(v => v.MaintenanceJobId == jobId && v.Kind == ReceiptKind.Advance && v.Status == PaymentStatus.Confirmed)
            .OrderBy(v => v.ReceivedAtUtc).Select(v => v.Id).ToListAsync(ct);
        var total = 0m;
        foreach (var id in advances)
        {
            var open = (await CustomerAdvances.OpenAsync(db, invoice.CustomerId, ct)).GetValueOrDefault(id);
            var owed = (await StandingsAsync([invoice.Id], ct))[invoice.Id].Outstanding;
            var amount = Math.Min(open, owed);
            if (amount <= 0) continue;
            var done = await ApplyAdvanceAsync(id, new ApplyAdvanceRequest(invoice.Id, amount), by, ct, at: at ?? invoice.IssuedAtUtc);
            if (done.Ok) total += amount;
        }
        return total;
    }

    private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;

    /// <summary>Closes every contract this confirmed receipt pays that is now paid and has its goods back.</summary>
    private async Task<bool> CloseWhatItPaysAsync(ReceiptVoucher voucher, CancellationToken ct)
    {
        var contracts = await RentalContractsOfAsync(voucher.Allocations.Select(a => a.InvoiceId).ToList(), ct);
        if (voucher.ContractId is Guid named) contracts.Add(named);
        var closedAny = false;
        foreach (var id in contracts.Distinct()) closedAny |= await RentalClosure.TryCloseAsync(db, id, ct);
        return closedAny;
    }

    /// <summary>The rental contracts behind these invoices, through the notes they bill.</summary>
    public async Task<List<Guid>> RentalContractsOfAsync(IReadOnlyCollection<Guid> invoiceIds, CancellationToken ct = default)
    {
        var sources = await db.Invoices.AsNoTracking()
            .Where(i => invoiceIds.Contains(i.Id) && i.Origin == InvoiceOrigin.Rental && i.SourceId != null)
            .Select(i => new { i.SourceType, SourceId = i.SourceId!.Value })
            .ToListAsync(ct);
        var issueIds = sources.Where(x => x.SourceType == LedgerSources.RentalIssue).Select(x => x.SourceId).ToList();
        var returnIds = sources.Where(x => x.SourceType == LedgerSources.RentalReturn).Select(x => x.SourceId).ToList();
        var fromIssues = await db.IssueNotes.AsNoTracking().Where(n => issueIds.Contains(n.Id)).Select(n => n.ContractId).ToListAsync(ct);
        var fromReturns = await db.ReturnNotes.AsNoTracking().Where(n => returnIds.Contains(n.Id)).Select(n => n.ContractId).ToListAsync(ct);
        return fromIssues.Concat(fromReturns).Distinct().ToList();
    }

    /// <summary>"Réparation — contrat CTR-…, bon RET-…" for a repair or a loss; null for a payment.</summary>
    private async Task<string?> KindPurposeAsync(ReceiptVoucher voucher, CancellationToken ct)
    {
        // An advance says so on its paper, and names the repair it is for (MS-35).
        if (voucher.Kind == ReceiptKind.Advance)
        {
            var job = voucher.MaintenanceJobId is Guid jobId
                ? await db.MaintenanceJobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => j.Number).FirstOrDefaultAsync(ct)
                : null;
            return job is null ? "Acompte" : $"Acompte — réparation {job}";
        }
        if (voucher.ContractId is not Guid contractId) return null;
        var number = await db.RentalContracts.AsNoTracking().Where(x => x.Id == contractId).Select(x => x.Number).FirstOrDefaultAsync(ct);
        if (voucher.Kind == ReceiptKind.Payment) return number is null ? null : $"Location — contrat {number}";
        var note = await db.ReturnNotes.AsNoTracking().Where(n => n.Id == voucher.ReturnNoteId).Select(n => n.Number).FirstOrDefaultAsync(ct);
        var label = voucher.Kind == ReceiptKind.Repair ? "Réparation" : "Perte";
        return Trim($"{label} — contrat {number}{(note is null ? "" : $", bon {note}")}", 300);
    }

    /// <summary>
    /// The cheque cleared, or it did not.
    ///
    /// <para>
    /// Confirming is the moment the money arrived, so it is the moment the debt comes down and
    /// the moment anything reaches the ledger. A bounce leaves the debt exactly where it was —
    /// nothing arrived and nothing about the customer's position changed — so there is nothing
    /// to reverse either; the allocations simply stop counting, because they only ever counted
    /// while the voucher was confirmed.
    /// </para>
    /// </summary>
    /// <param name="mayConfirmOwn">
    /// Holds <c>finance.receipts.settle_own</c> (owner, security review 2026-10-10): may confirm a
    /// receipt they recorded themselves. Without it someone else confirms it — the same four eyes
    /// as a payment voucher's approval — so one person cannot write a cheque for a friend's debt
    /// and clear it. A bounce is never refused: it takes nothing off anyone's debt.
    /// </param>
    public async Task<ReceivableResult<ReceiptVoucher>> SettleReceiptAsync(
        Guid id, string? status, string? note, string? by, CancellationToken ct = default, bool mayConfirmOwn = true)
    {
        if (!Enum.TryParse<PaymentStatus>(status, true, out var target) || target == PaymentStatus.Pending)
            return ReceivableResult<ReceiptVoucher>.Invalid("Settle a receipt as Confirmed or Bounced.");
        if (ReceivableRules.SettlementProblem(target, note) is string problem)
            return ReceivableResult<ReceiptVoucher>.Invalid(problem);

        var voucher = await db.ReceiptVouchers.Include(v => v.Allocations).FirstOrDefaultAsync(v => v.Id == id, ct);
        if (voucher is null) return ReceivableResult<ReceiptVoucher>.NotFound("Receipt not found.");
        if (voucher.Status != PaymentStatus.Pending)
            return ReceivableResult<ReceiptVoucher>.Conflict($"This receipt is already {voucher.Status}.");
        if (target == PaymentStatus.Confirmed && !mayConfirmOwn && !string.IsNullOrEmpty(voucher.CreatedBy)
            && string.Equals(voucher.CreatedBy, by, StringComparison.OrdinalIgnoreCase))
            return ReceivableResult<ReceiptVoucher>.Conflict(
                $"{voucher.Number} was recorded by you; someone else confirms it.");

        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == voucher.CustomerId, ct);

        await using var tx = await db.BeginWriteScopeAsync(ct);

        voucher.Status = target;
        voucher.StatusNote = Trim(note, 400);
        voucher.ConfirmedAtUtc = DateTime.UtcNow;

        if (target == PaymentStatus.Confirmed) await ApplyConfirmedAsync(voucher, customer, by, ct, confirmedLater: true);

        await db.SaveChangesAsync(ct);
        // The cheque is collected: only now does it count towards the contract (answer 22).
        if (target == PaymentStatus.Confirmed && await CloseWhatItPaysAsync(voucher, ct))
            await db.SaveChangesAsync(ct);
        // MS-35: an advance cheque for a job invoiced while it was pending clears against that invoice now.
        if (target == PaymentStatus.Confirmed && voucher.Kind == ReceiptKind.Advance && voucher.MaintenanceJobId is Guid jobId
            && await db.MaintenanceJobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => j.InvoiceId).FirstOrDefaultAsync(ct) is Guid jobInvoiceId
            && await db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == jobInvoiceId, ct) is Invoice jobInvoice)
            await ApplyJobAdvancesAsync(jobId, jobInvoice, by, ct, at: DateTime.UtcNow);
        await tx.CommitAsync(ct);

        return ReceivableResult<ReceiptVoucher>.Success(voucher);
    }

    /// <summary>
    /// Everything that happens when money is known to have arrived: the ledger, the customer's
    /// balance and — for cash and only for cash — the branch's box.
    /// </summary>
    private async Task ApplyConfirmedAsync(
        ReceiptVoucher voucher, Customer? customer, string? by, CancellationToken ct, bool confirmedLater = false)
    {
        // A cheque or transfer received in a month whose books have since closed is confirmed on the first open day
        // (COR-E01, owner 2026-11-05); a receipt typed today with a closed-month date is still refused.
        if (ReceivablePosting.Receipt(voucher, by) is PostingDraft draft)
            await ledger.PostAsync(confirmedLater ? await BookClosings.OntoOpenBooksAsync(db, draft, ct) : draft, ct);

        // Read again under the version lock, for the same reason as an invoice's debt.
        if (customer is not null && voucher.Amount != 0
            && await db.LockedAsync<Customer>(customer.Id, ct) is Customer paying)
        {
            paying.Balance -= voucher.Amount;
            await db.TouchAsync(paying, ct);
        }

        // Only physical cash reaches a drawer. A cleared cheque is money at the bank, which is
        // exactly why it can never be remitted and must not appear in the Z-report's cash
        // figure (§5.8).
        if (voucher.Behavior != PaymentBehavior.Cash) return;

        var box = await db.CashBoxes.FirstOrDefaultAsync(x => x.BranchId == voucher.BranchId && x.IsActive, ct);
        if (box is null) return;

        var balance = await CashBoxBalance.ForWriteAsync(db, box.Id, ct);

        db.CashMovements.Add(new CashMovement
        {
            CashBoxId = box.Id,
            Direction = CashDirection.In,
            Amount = voucher.Amount,
            BalanceAfter = balance + voucher.Amount,
            Kind = CashMovementKind.Receipt,
            SourceType = LedgerSources.CustomerReceipt,
            SourceId = voucher.Id,
            UserName = by,
            Reference = voucher.Number,
            OccurredAtUtc = voucher.ReceivedAtUtc
        });
    }

    /// <summary>
    /// The receipt's default "in exchange for" line (answer 23: "the rental contract number"):
    /// the contract behind each allocated invoice raised over a rental note, and nothing else —
    /// "Location — contrat CTR-000012". Null when no
    /// allocated invoice is rental, so a receipt for concrete says nothing it does not know.
    ///
    /// <para>
    /// French, because the document's title language is French (answer 4), and plain enough to
    /// be edited: the form shows it prefilled and the clerk may write over it. An issue note's
    /// period runs from the day the goods left for the longest period on the note — the day it
    /// ends is the return day, which the owner's answer 6 does not count — and a return note is
    /// dated to the day the goods came back.
    /// </para>
    /// </summary>
    public async Task<string?> DefaultPurposeAsync(
        IReadOnlyCollection<Guid> invoiceIds, CancellationToken ct = default, Guid? branchId = null)
    {
        if (invoiceIds.Count == 0) return null;

        var sources = await db.Invoices.AsNoTracking()
            .Where(i => invoiceIds.Contains(i.Id) && i.Origin == InvoiceOrigin.Rental && i.SourceId != null)
            .Where(i => branchId == null || i.BranchId == branchId)
            .OrderBy(i => i.IssuedAtUtc)
            .Select(i => new { i.SourceType, SourceId = i.SourceId!.Value })
            .ToListAsync(ct);
        if (sources.Count == 0) return null;

        var zone = RentalZone();
        var issueIds = sources.Where(s => s.SourceType == LedgerSources.RentalIssue).Select(s => s.SourceId).ToList();
        var returnIds = sources.Where(s => s.SourceType == LedgerSources.RentalReturn).Select(s => s.SourceId).ToList();

        var issues = await db.IssueNotes.AsNoTracking().Include(n => n.Lines)
            .Where(n => issueIds.Contains(n.Id)).ToListAsync(ct);
        var returns = await db.ReturnNotes.AsNoTracking()
            .Where(n => returnIds.Contains(n.Id)).ToListAsync(ct);

        var contractIds = issues.Select(n => n.ContractId).Concat(returns.Select(n => n.ContractId)).Distinct().ToList();
        var contracts = await db.RentalContracts.AsNoTracking()
            .Where(c => contractIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Number, ct);
        var durations = await db.RentalContractLines.AsNoTracking()
            .Where(l => contractIds.Contains(l.ContractId))
            .ToDictionaryAsync(l => l.Id, l => l.DurationDays, ct);

        // One phrase per note, grouped under its contract, in the order the invoices were raised.
        var byContract = new List<(Guid Contract, List<string> Periods)>();
        void Add(Guid contract, string period)
        {
            var entry = byContract.FirstOrDefault(x => x.Contract == contract);
            if (entry.Periods is null) byContract.Add((contract, [period]));
            else if (!entry.Periods.Contains(period)) entry.Periods.Add(period);
        }

        foreach (var source in sources)
        {
            if (issues.FirstOrDefault(n => n.Id == source.SourceId) is { } issue)
            {
                var from = Engine.RentalPricing.LocalDay(issue.IssuedAtUtc, zone);
                var days = issue.Lines.Select(l => durations.GetValueOrDefault(l.ContractLineId)).DefaultIfEmpty(0).Max();
                Add(issue.ContractId, days > 0 ? $"du {Day(from)} au {Day(from.AddDays(days))}" : $"du {Day(from)}");
            }
            else if (returns.FirstOrDefault(n => n.Id == source.SourceId) is { } back)
            {
                Add(back.ContractId, $"retour du {Day(Engine.RentalPricing.LocalDay(back.ReturnedAtUtc, zone))}");
            }
        }

        // The contract number alone (owner, 2026-10-02, answer 23 restated): the periods are on the
        // invoices the receipt settles, and the line the customer reads names the contract.
        var parts = byContract
            .Where(x => contracts.ContainsKey(x.Contract))
            .Select(x => $"contrat {contracts[x.Contract]}")
            .ToList();
        return parts.Count == 0 ? null : Trim($"Location — {string.Join("; ", parts)}", 300);
    }

    private static string Day(DateOnly day) => day.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The company's clock, as the rental module reads it; an unknown zone falls back to UTC.</summary>
    private TimeZoneInfo RentalZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(regionalSettings.Get().TimeZone); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    // ---------- credit notes ----------

    /// <summary>
    /// Reverses an invoice, in whole or in part.
    ///
    /// <para>
    /// There is no route that edits an invoice and none that deletes one, and this is why: the
    /// customer is holding a numbered document. The wrong figure stays, the correction sits
    /// beside it, and the statement shows both — the same rule the ledger has always followed.
    /// </para>
    /// </summary>
    public async Task<ReceivableResult<CreditNote>> CreateCreditNoteAsync(
        Guid invoiceId, CreateCreditNoteRequest r, string? by, CancellationToken ct = default,
        CreditNoteSource? source = null)
    {
        if (source is not null)
        {
            var existing = await db.CreditNotes.Include(n => n.Lines)
                .FirstOrDefaultAsync(n => n.SourceType == source.SourceType && n.SourceId == source.SourceId, ct);
            if (existing is not null)
                return ReceivableResult<CreditNote>.Success(existing);
        }

        if (string.IsNullOrWhiteSpace(r.Reason))
            return ReceivableResult<CreditNote>.Invalid(
                "A credit note needs a reason; an unexplained one is a debt quietly written off.");

        var invoice = await db.Invoices.AsNoTracking().Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, ct);
        if (invoice is null) return ReceivableResult<CreditNote>.NotFound("Invoice not found.");

        var credited = await CreditedQtyByLineAsync(invoiceId, ct);
        var regional = regionalSettings.Get();
        // P0.3: what earlier credit notes have already brought back into stock, line by line.
        var backInStock = await StockReturnedByLineAsync(invoiceId, ct);

        var note = new CreditNote
        {
            InvoiceId = invoice.Id,
            CustomerId = invoice.CustomerId,
            BranchId = invoice.BranchId,
            IssuedAtUtc = r.IssuedAtUtc ?? DateTime.UtcNow,
            Reason = Trim(r.Reason.Trim(), 400)!,
            CreatedBy = by,
            SourceType = source?.SourceType,
            SourceId = source?.SourceId
        };

        // No lines means the whole invoice: the ordinary case is cancelling one outright, and
        // making the caller enumerate every line to do it invites a partial credit by accident.
        var requested = r.Lines is { Count: > 0 }
            ? r.Lines
            : invoice.Lines
                .Select(l => new CreditNoteLineRequest(l.Id, l.Qty - credited.GetValueOrDefault(l.Id)))
                .Where(l => l.Qty > 0)
                .ToList();

        if (requested.Count == 0)
            return ReceivableResult<CreditNote>.Conflict("This invoice has already been credited in full.");

        foreach (var line in requested)
        {
            var invoiceLine = invoice.Lines.FirstOrDefault(l => l.Id == line.InvoiceLineId);
            if (invoiceLine is null) return ReceivableResult<CreditNote>.NotFound("That line is not on this invoice.");
            if (ReceivableRules.CreditProblem(
                    line.Qty, credited.GetValueOrDefault(invoiceLine.Id), invoiceLine.Qty) is string problem)
                return ReceivableResult<CreditNote>.Invalid($"{invoiceLine.Sku}: {problem}");
            if (StockReturnProblem(line, invoiceLine, backInStock.GetValueOrDefault(invoiceLine.Id), source is not null)
                is string stockProblem)
                return ReceivableResult<CreditNote>.Invalid($"{invoiceLine.Sku}: {stockProblem}");
            // The same line twice on one request counts once against what is left to bring back.
            backInStock[invoiceLine.Id] = backInStock.GetValueOrDefault(invoiceLine.Id) + line.StockReturnedQty;

            // Pro rata on the line's own net, so a line that was discounted is credited net of
            // its discount. Crediting quantity × price would give back more than was ever
            // charged on every discounted line.
            var net = invoiceLine.Qty == 0
                ? 0m
                : regional.RoundMoney(invoiceLine.LineNet * line.Qty / invoiceLine.Qty);

            note.Lines.Add(new CreditNoteLine
            {
                CreditNoteId = note.Id,
                InvoiceLineId = invoiceLine.Id,
                Sku = invoiceLine.Sku,
                NameSnapshot = invoiceLine.NameSnapshot,
                Qty = line.Qty,
                UnitPrice = invoiceLine.UnitPrice,
                Days = invoiceLine.Days,
                TaxRate = invoiceLine.TaxRate,
                LineNet = net,
                // Inherited, never chosen: the money goes back on the line it came off, which
                // for a rental invoice is rent or overdue and not merchandise sales.
                IncomeAccountCode = invoiceLine.IncomeAccountCode,
                StockReturnedQty = line.StockReturnedQty
            });
        }

        for (var i = 0; i < note.Lines.Count; i++) note.Lines[i].Position = i + 1;
        note.SubTotalHt = regional.RoundMoney(note.Lines.Sum(l => l.LineNet));
        note.TaxTotal = invoice.TaxMode == TaxMode.Untaxed
            ? 0m
            : DocumentTax.On(regional, NetByRate(note.Lines.Select(l => (l.TaxRate, l.LineNet))));
        note.GrandTotalTtc = note.SubTotalHt + note.TaxTotal;

        await using var tx = await db.BeginWriteScopeAsync(ct);

        note.Number = Numbered("CRN", await db.CreditNotes.CountAsync(ct) + 1);
        db.CreditNotes.Add(note);

        if (source is null)
        {
            // A hand-made credit note posts to the ledger and reduces the customer's balance.
            // An automatic credit note (source is set) skips both: the movement (till refund or
            // rental return) already posted and moved the balance (owner, 2026-10-10).
            var income = note.Lines
                .GroupBy(l => l.IncomeAccountCode)
                .Select(g => new IncomeAmount(g.Key, g.Sum(l => l.LineNet), note.Number));
            // P0.3: goods the clerk says came back go into stock, at what their invoice line took them out at.
            var returnedCost = await BringGoodsBackAsync(note, invoice, by, ct);
            if (ReceivablePosting.CreditNote(note, invoice.Number, income, by, returnedCost) is PostingDraft draft)
                await ledger.PostAsync(draft, ct);

            // Under the version lock, so a debt a till adds at the same moment is not written over.
            if (note.GrandTotalTtc != 0 && await db.LockedAsync<Customer>(invoice.CustomerId, ct) is Customer customer)
            {
                customer.Balance -= note.GrandTotalTtc;
                await db.TouchAsync(customer, ct);
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return ReceivableResult<CreditNote>.Success(note);
    }

    /// <summary>
    /// Why this line cannot bring <see cref="CreditNoteLineRequest.StockReturnedQty"/> back into stock, or null (P0.3).
    /// Only a hand-made note brings goods back — a till refund or a rental return moves its own stock — and only on a
    /// line that took goods out; never more than this note credits, nor more than the line took out less what came back.
    /// </summary>
    private static string? StockReturnProblem(
        CreditNoteLineRequest line, InvoiceLine invoiceLine, decimal alreadyBack, bool automatic)
    {
        if (line.StockReturnedQty == 0) return null;
        if (line.StockReturnedQty < 0) return "The quantity back in stock cannot be negative.";
        if (automatic) return "An automatic credit note brings no goods back; the refund or the return moved them.";
        if (invoiceLine.StockUnitCost is null)
            return "This line took nothing out of stock, so nothing can come back into it.";
        if (line.StockReturnedQty > line.Qty)
            return $"No more can come back into stock than this credit note gives back ({line.Qty:0.###}).";
        var left = invoiceLine.Qty - alreadyBack;
        if (line.StockReturnedQty > left)
            return $"Only {Math.Max(0m, left):0.###} of this line is still out of stock.";
        return null;
    }

    /// <summary>
    /// P0.3: the goods a hand-made credit note says came back, into the invoice's branch at the cost their invoice line
    /// took them out at (<see cref="StockMovementKind.InvoiceReturn"/>). In the note's own transaction, before its entry.
    /// </summary>
    /// <returns>Σ quantity back × that cost, rounded as the ledger keeps money; 0 when nothing came back.</returns>
    private async Task<decimal> BringGoodsBackAsync(CreditNote note, Invoice invoice, string? by, CancellationToken ct)
    {
        var back = note.Lines.Where(l => l.StockReturnedQty > 0).ToList();
        if (back.Count == 0) return 0m;

        var stocks = new StockRows(db);
        var touched = new HashSet<Guid>();
        var cost = 0m;
        foreach (var line in back)
        {
            var invoiceLine = invoice.Lines.First(l => l.Id == line.InvoiceLineId);
            var productId = invoiceLine.ProductId!.Value;
            var unitCost = invoiceLine.StockUnitCost!.Value;

            var stock = await stocks.GetAsync(productId, note.BranchId, ct);
            stock.StockQty += line.StockReturnedQty;

            db.StockMovements.Add(new StockMovement
            {
                ProductId = productId,
                BranchId = note.BranchId,
                QtyDelta = line.StockReturnedQty,
                BalanceAfter = stock.StockQty,
                Reason = "credit-note",
                Kind = StockMovementKind.InvoiceReturn,
                UnitCost = unitCost,
                DocumentId = note.Id,
                DocumentNumber = note.Number,
                UserName = by,
                MovedQty = line.StockReturnedQty
            });
            cost += line.StockReturnedQty * unitCost;
            touched.Add(productId);
        }
        foreach (var product in await db.Products.Where(p => touched.Contains(p.Id)).ToListAsync(ct))
            await db.TouchAsync(product, ct);

        return Math.Round(cost, 3, MidpointRounding.AwayFromZero);
    }

    /// <summary>What credit notes have brought back into stock of each line of this invoice (P0.3).</summary>
    private async Task<Dictionary<Guid, decimal>> StockReturnedByLineAsync(Guid invoiceId, CancellationToken ct)
    {
        // Summed here, not in the database: SQLite stores decimal as TEXT.
        var rows = await db.CreditNoteLines.AsNoTracking()
            .Join(db.CreditNotes.AsNoTracking().Where(n => n.InvoiceId == invoiceId),
                l => l.CreditNoteId, n => n.Id, (l, n) => new { l.InvoiceLineId, l.StockReturnedQty })
            .ToListAsync(ct);
        return rows.GroupBy(x => x.InvoiceLineId).ToDictionary(g => g.Key, g => g.Sum(x => x.StockReturnedQty));
    }

    /// <summary>
    /// A till refund of an invoiced sale raises its credit note (rule 1). Not invoiced: nothing (rule 2).
    /// </summary>
    public async Task<ReceivableResult<CreditNote>?> CreditTillRefundAsync(
        Guid refundSaleId, string? by, CancellationToken ct = default)
    {
        var existing = await db.CreditNotes.Include(n => n.Lines)
            .FirstOrDefaultAsync(n => n.SourceType == "sale" && n.SourceId == refundSaleId, ct);
        if (existing is not null)
            return ReceivableResult<CreditNote>.Success(existing);

        var refund = await db.Sales.AsNoTracking().Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == refundSaleId, ct);
        if (refund is null || refund.Status != SaleStatus.Refunded || refund.RefundOfSaleId is null)
            return null;

        var invoice = await db.Invoices.AsNoTracking().Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.Origin == InvoiceOrigin.Sale && i.SourceId == refund.RefundOfSaleId, ct);
        if (invoice is null) return null;

        var credited = await CreditedQtyByLineAsync(invoice.Id, ct);
        var remaining = invoice.Lines.ToDictionary(
            l => l.Id,
            l => Math.Max(0m, l.Qty - credited.GetValueOrDefault(l.Id)));

        var lineRequests = new List<CreditNoteLineRequest>();
        foreach (var refundLine in refund.Lines)
        {
            var invoiceLine = invoice.Lines
                .Where(l => l.ProductId == refundLine.ProductId && remaining[l.Id] > 0)
                .OrderBy(l => l.Position)
                .FirstOrDefault();
            if (invoiceLine is null) continue;

            var take = Math.Min(refundLine.Qty, remaining[invoiceLine.Id]);
            if (take <= 0) continue;

            remaining[invoiceLine.Id] -= take;
            lineRequests.Add(new CreditNoteLineRequest(invoiceLine.Id, take));
        }

        var lines = lineRequests
            .GroupBy(r => r.InvoiceLineId)
            .Select(g => new CreditNoteLineRequest(g.Key, g.Sum(r => r.Qty)))
            .Where(r => r.Qty > 0)
            .ToList();

        if (lines.Count == 0) return null;

        return await CreateCreditNoteAsync(
            invoice.Id,
            new CreateCreditNoteRequest($"Refund {refund.Reference}", Lines: lines),
            by,
            ct,
            new CreditNoteSource("sale", refund.Id));
    }

    /// <summary>
    /// Rental goods back early on an invoiced issue note raise a credit note for the unused rent (rule 1).
    /// </summary>
    public async Task<ReceivableResult<CreditNote>?> CreditEarlyReturnAsync(
        Guid returnNoteId, string? by, CancellationToken ct = default)
    {
        return await RentalReturnCredits.CreateAsync(db, regionalSettings.Get(), returnNoteId, by, ct);
    }

    // ---------- reading ----------

    /// <summary>One credit note with its lines, or null — the printed CRN reads it here.</summary>
    public Task<CreditNote?> GetCreditNoteAsync(Guid id, CancellationToken ct = default) =>
        db.CreditNotes.AsNoTracking().Include(n => n.Lines).FirstOrDefaultAsync(n => n.Id == id, ct);

    /// <summary>
    /// The number of the paper each invoice bills — the sale's receipt reference, the issue or
    /// return note's number — keyed by invoice id; a direct invoice has no entry. Three set
    /// queries however many invoices, so a list of two hundred costs the same as one.
    /// </summary>
    public async Task<Dictionary<Guid, string>> SourceNumbersAsync(IReadOnlyList<Invoice> invoices, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, string>();
        var sourced = invoices.Where(i => i.SourceId is not null && i.SourceType is not null).ToList();
        if (sourced.Count == 0) return result;

        var saleIds = sourced.Where(i => i.SourceType == LedgerSources.Sale).Select(i => i.SourceId!.Value).Distinct().ToList();
        var issueIds = sourced.Where(i => i.SourceType == LedgerSources.RentalIssue).Select(i => i.SourceId!.Value).Distinct().ToList();
        var returnIds = sourced.Where(i => i.SourceType == LedgerSources.RentalReturn).Select(i => i.SourceId!.Value).Distinct().ToList();

        var numbers = new Dictionary<(string Type, Guid Id), string>();
        if (saleIds.Count > 0)
            foreach (var s in await db.Sales.AsNoTracking().Where(s => saleIds.Contains(s.Id)).Select(s => new { s.Id, s.Reference }).ToListAsync(ct))
                numbers[(LedgerSources.Sale, s.Id)] = s.Reference;
        if (issueIds.Count > 0)
            foreach (var n in await db.IssueNotes.AsNoTracking().Where(n => issueIds.Contains(n.Id)).Select(n => new { n.Id, n.Number }).ToListAsync(ct))
                numbers[(LedgerSources.RentalIssue, n.Id)] = n.Number;
        if (returnIds.Count > 0)
            foreach (var n in await db.ReturnNotes.AsNoTracking().Where(n => returnIds.Contains(n.Id)).Select(n => new { n.Id, n.Number }).ToListAsync(ct))
                numbers[(LedgerSources.RentalReturn, n.Id)] = n.Number;
        // Phase M5a: a customer maintenance job's number (MJ-…).
        var jobIds = sourced.Where(i => i.SourceType == InvoiceSources.MaintenanceJob).Select(i => i.SourceId!.Value).Distinct().ToList();
        if (jobIds.Count > 0)
            foreach (var j in await db.MaintenanceJobs.AsNoTracking().Where(j => jobIds.Contains(j.Id)).Select(j => new { j.Id, j.Number }).ToListAsync(ct))
                numbers[(InvoiceSources.MaintenanceJob, j.Id)] = j.Number;

        foreach (var invoice in sourced)
            if (numbers.TryGetValue((invoice.SourceType!, invoice.SourceId!.Value), out var number))
                result[invoice.Id] = number;
        return result;
    }

    /// <summary>
    /// What each of these invoices still owes — today, or as of a past instant.
    ///
    /// <para>
    /// Every sum runs in memory after the rows are loaded, because SQLite stores decimal as TEXT
    /// and a server-side <c>Sum</c> there compares strings. It is the rule the rest of this
    /// codebase follows and the reason none of these aggregates is pushed down.
    /// </para>
    /// <para>
    /// With <paramref name="asOf"/>, a credit note or a receipt counts only if the ledger had it
    /// by then — the credit note's issue date, the receipt's received date, which are the dates
    /// their entries carry. The ageing report cuts its 411 balance at the same instant, so an
    /// invoice paid last week is owed and late in last month's report, not "uninvoiced".
    /// </para>
    /// </summary>
    public async Task<Dictionary<Guid, InvoiceStanding>> StandingsAsync(
        IReadOnlyCollection<Guid> invoiceIds, CancellationToken ct = default, DateTime? asOf = null)
    {
        if (invoiceIds.Count == 0) return [];

        var invoices = await db.Invoices.AsNoTracking()
            .Where(i => invoiceIds.Contains(i.Id))
            .Select(i => new { i.Id, i.GrandTotalTtc, i.PrepaidAmount })
            .ToListAsync(ct);

        var creditQuery = db.CreditNotes.AsNoTracking().Where(n => invoiceIds.Contains(n.InvoiceId));
        if (asOf is DateTime creditCut) creditQuery = creditQuery.Where(n => n.IssuedAtUtc <= creditCut);
        var credits = await creditQuery
            .Select(n => new { n.InvoiceId, n.GrandTotalTtc })
            .ToListAsync(ct);

        var voucherQuery = db.ReceiptVouchers.AsNoTracking();
        if (asOf is DateTime receiptCut) voucherQuery = voucherQuery.Where(v => v.ReceivedAtUtc <= receiptCut);
        var allocations = await db.ReceiptAllocations.AsNoTracking()
            .Where(a => invoiceIds.Contains(a.InvoiceId))
            .Join(voucherQuery, a => a.ReceiptVoucherId, v => v.Id,
                (a, v) => new { a.InvoiceId, a.Amount, v.Status })
            .ToListAsync(ct);

        return invoices.ToDictionary(i => i.Id, i => new InvoiceStanding(
            i.GrandTotalTtc,
            i.PrepaidAmount,
            credits.Where(c => c.InvoiceId == i.Id).Sum(c => c.GrandTotalTtc),
            allocations.Where(a => a.InvoiceId == i.Id && a.Status == PaymentStatus.Confirmed).Sum(a => a.Amount),
            allocations.Where(a => a.InvoiceId == i.Id && a.Status == PaymentStatus.Pending).Sum(a => a.Amount)));
    }

    public Task<Invoice?> ReadInvoiceAsync(Guid id, CancellationToken ct = default) =>
        db.Invoices.AsNoTracking().Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id, ct);

    public Task<ReceiptVoucher?> ReadReceiptAsync(Guid id, CancellationToken ct = default) =>
        db.ReceiptVouchers.AsNoTracking().Include(v => v.Allocations).FirstOrDefaultAsync(v => v.Id == id, ct);

    /// <summary>How much of each line of this invoice has already been given back.</summary>
    public async Task<Dictionary<Guid, decimal>> CreditedQtyByLineAsync(Guid invoiceId, CancellationToken ct = default)
    {
        var rows = await db.CreditNoteLines.AsNoTracking()
            .Join(db.CreditNotes.AsNoTracking().Where(n => n.InvoiceId == invoiceId),
                l => l.CreditNoteId, n => n.Id, (l, n) => new { l.InvoiceLineId, l.Qty, l.LineNet, l.ReturnAllocationId })
            .ToListAsync(ct);
        var lines = await db.InvoiceLines.AsNoTracking().Where(l => l.InvoiceId == invoiceId)
            .ToDictionaryAsync(l => l.Id, ct);
        return rows.GroupBy(x => x.InvoiceLineId).ToDictionary(g => g.Key, g => g.Sum(x =>
            x.ReturnAllocationId is not null && lines.TryGetValue(x.InvoiceLineId, out var line) && line.LineNet > 0
                ? line.Qty * x.LineNet / line.LineNet : x.Qty));
    }

    // ---------- plumbing ----------

    /// <summary>
    /// What one document put on one customer's trade account, read from the ledger rather than
    /// recomputed. The single figure an invoice over that document is allowed to bill.
    /// </summary>
    private async Task<decimal> OnAccountAsync(string sourceType, Guid sourceId, Guid customerId, CancellationToken ct)
    {
        var account = await db.LedgerAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Code == LedgerAccounts.Customers, ct);
        if (account is null) return 0m;

        var correctionType = LedgerSources.DailyCorrectionFor(sourceType);
        var rows = await db.LedgerEntries.AsNoTracking()
            .Where(e => (e.SourceType == sourceType || e.SourceType == correctionType) && e.SourceId == sourceId)
            .SelectMany(e => e.Lines)
            .Where(l => l.AccountId == account.Id && l.CustomerId == customerId)
            .Select(l => new { l.Debit, l.Credit })
            .ToListAsync(ct);

        return rows.Sum(x => x.Debit - x.Credit);
    }

    /// <summary>
    /// The credit limit, said out loud and never enforced. Nothing in this module refuses a
    /// document because of one — a limit is a warning in this system (CLAUDE.md, rule one), and
    /// the business decides what to do about it, not the software.
    /// </summary>
    private static string? OverLimit(Customer? customer) =>
        customer is { CreditLimit: > 0 } && customer.Balance > customer.CreditLimit
            ? $"Customer credit limit exceeded ({customer.Balance:0.##} > {customer.CreditLimit:0.##})."
            : null;

    private static Dictionary<decimal, decimal> NetByRate(IEnumerable<(decimal Rate, decimal Net)> lines)
    {
        var netByRate = new Dictionary<decimal, decimal>();
        foreach (var (rate, net) in lines)
            netByRate[rate] = netByRate.GetValueOrDefault(rate) + net;
        return netByRate;
    }

    /// <summary>
    /// The number on the customer's copy.
    ///
    /// <para>
    /// A count is safe here for the same reason it is safe for a rental note: every write route
    /// runs behind the subscription filter, which has already taken the version counter's row
    /// lock and therefore serialises writers across processes. The unique index on each number
    /// is the backstop — if that ever stops being true the insert fails loudly instead of
    /// quietly putting the same number on two customers' invoices.
    /// </para>
    /// </summary>
    private static string Numbered(string prefix, int next) => $"{prefix}-{next:D6}";

    private static string? Trim(string? value, int max) =>
        value is { Length: > 0 } && value.Length > max ? value[..max] : value;

    /// <summary>The same, where the column is not nullable and a null would be a blank name.</summary>
    private static string Shorten(string value, int max) => value.Length > max ? value[..max] : value;

    /// <summary>The first of the product's names that is not empty, in the order an invoice line prefers them.</summary>
    private static string FirstName(params string[] names) => names.FirstOrDefault(n => n.Length > 0) ?? string.Empty;
}
