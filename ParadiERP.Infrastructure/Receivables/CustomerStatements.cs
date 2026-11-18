using ParadiERP.Application.Contracts;
using ParadiERP.Application.Receivables;
using ParadiERP.Application.Regional;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Receivables;

/// <summary>
/// What a customer owes, and how they came to owe it.
///
/// <para>
/// <b>Both reports read the ledger, not the documents.</b> A statement assembled from invoices
/// and receipts would be a second set of figures kept beside account 411 and hoped to agree; the
/// first time they disagreed nobody could say which was right, and a statement that disagrees
/// with the books is worse than no statement at all. So the statement <i>is</i> account 411 for
/// one customer, and the documents are what each line is labelled with.
/// </para>
/// <para>
/// It still states the other figures beside it — what pending paper would add, what the
/// denormalised customer balance says, how much of the debt has been invoiced — because the
/// useful property is not that they agree but that any disagreement is visible as a number
/// rather than discovered months later.
/// </para>
/// </summary>
public sealed class CustomerStatements(
    PosDbContext db, ReceivableDocuments receivables, IRegionalSettingsProvider regionalSettings)
{
    /// <summary>Every document kind that can put money on a customer's account, and where its number lives.</summary>
    private static readonly string[] Numbered =
    [
        LedgerSources.Invoice, LedgerSources.CreditNote, LedgerSources.CustomerReceipt, LedgerSources.CustomerAdvanceKept,
        LedgerSources.Sale, LedgerSources.RentalIssue, LedgerSources.RentalReturn,
        LedgerSources.PaymentSettlement, LedgerSources.PaymentVoucher,
        LedgerSources.RentalIssueDailyCorrection, LedgerSources.RentalReturnDailyCorrection,
        LedgerSources.RentalCreditDailyCorrection
    ];

    /// <summary>
    /// Opening balance, every movement in the period, closing balance — for one customer, off
    /// account 411, with every sale the till took payment for on the spot beside it.
    ///
    /// <para>
    /// <b>One statement for everything</b> (owner, 2026-10-10): sales, rentals, receipts, payments
    /// paid out to the customer, the opening balance and adjustments, each line with its
    /// <see cref="CustomerStatementKinds">kind</see>. A sale paid in full at the till never touches
    /// 411, so the ledger alone left it out; it is added from the sale itself as one row that
    /// debits what was sold and credits what the till took — a row whose net is exactly what the
    /// sale put on 411 (nothing, for a cash sale). The sale's own 411 lines are folded into that
    /// row, so every balance stays the ledger's to the last unit.
    /// </para>
    /// <para>
    /// <b>Narrowed</b> to sales or rentals (the admin's choice), a receipt or a payment counts by
    /// what it was for: a contract or a return note makes it a rental's; otherwise its allocations
    /// to invoices decide, part by part, by each invoice's origin. What was allocated to nothing
    /// shows under "all" only. The opening balance is that part of the account before the period.
    /// </para>
    /// </summary>
    public async Task<CustomerStatementDto?> BuildAsync(
        Guid customerId, DateTime from, DateTime to, CancellationToken ct = default,
        StatementFilter filter = StatementFilter.All)
    {
        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId, ct);
        if (customer is null) return null;

        var accounts = await db.LedgerAccounts.AsNoTracking()
            .Where(a => a.Code == LedgerAccounts.Customers || a.Code == LedgerAccounts.NotesReceivable
                        || a.Code == LedgerAccounts.CustomerDeposits || a.Code == LedgerAccounts.TaxCollected)
            .ToDictionaryAsync(a => a.Code, a => a.Id, ct);
        if (!accounts.TryGetValue(LedgerAccounts.Customers, out var trade)) return null;

        // Loaded and summed in memory: SQLite stores decimal as TEXT, so a server-side Sum
        // there compares strings. The filter itself is the indexed part.
        var lines = await db.LedgerEntryLines.AsNoTracking()
            .Where(l => l.CustomerId == customerId)
            .Join(db.LedgerEntries.AsNoTracking(), l => l.EntryId, e => e.Id, (l, e) => new
            {
                l.AccountId, l.Debit, l.Credit, l.Memo,
                e.EntryDate, e.SourceType, e.SourceId, e.Description, e.IsReversal
            })
            .ToListAsync(ct);

        // M7b: an advance held on 419 is still the customer's money with us, so their account is 411 plus what an advance
        // put on 419 — and on 4457, the VAT inside it when the accountant made it due on receipt (those lines carry the
        // customer for exactly this). An advance's lines become one row: the whole sum handed over. Applying an advance
        // moves it from 419 (and 4457) to 411, all the customer's: it nets to nothing here and is left out.
        var held = new HashSet<Guid>();
        if (accounts.TryGetValue(LedgerAccounts.CustomerDeposits, out var deposits)) held.Add(deposits);
        if (accounts.TryGetValue(LedgerAccounts.TaxCollected, out var tax)) held.Add(tax);
        // SAL-13: a part of an advance kept on a cancelled order is one row too, a debit of the whole sum kept (its 419 line
        // and its 4457 line moved off the customer, merged). Kept from 411 instead, it is an ordinary 411 line below.
        var advanceLines = lines
            .Where(l => held.Contains(l.AccountId)
                        && (l.SourceType == LedgerSources.CustomerReceipt || l.SourceType == LedgerSources.CustomerAdvanceKept))
            .GroupBy(l => (l.SourceId, l.IsReversal, l.EntryDate))
            .Select(g => g.First() with { AccountId = trade, Debit = g.Sum(l => l.Debit), Credit = g.Sum(l => l.Credit) });

        var tradeLines = lines.Where(l => l.AccountId == trade)
            .Concat(advanceLines)
            .Where(l => l.SourceType != LedgerSources.CustomerAdvanceApplication)
            .OrderBy(l => l.EntryDate).ToList();

        // ---- every movement of the account, each with its kind and the part of it that is a sale's or a rental's ----
        var sales = await db.Sales.AsNoTracking()
            .Where(s => s.CustomerId == customerId)
            .Select(s => new { s.Id, s.Reference, s.SoldAtUtc, s.GrandTotalTtc, s.RoundingAdjustment, s.Status })
            .ToListAsync(ct);
        var saleIds = sales.Select(s => s.Id).ToHashSet();
        var parts = await PartsAsync(customerId, tradeLines.Select(l => (l.SourceType, l.SourceId)), ct);

        var movements = new List<Movement>(tradeLines.Count + sales.Count);
        foreach (var line in tradeLines)
        {
            // A sale's own 411 lines are folded into the sale's row below.
            if (line.SourceType == LedgerSources.Sale && saleIds.Contains(line.SourceId)) continue;
            var part = parts.GetValueOrDefault((line.SourceType, line.SourceId)) ?? Part.Of(KindOf(line.SourceType));
            movements.Add(new Movement(line.EntryDate, line.SourceType, line.SourceId, line.Memo, line.Description,
                line.IsReversal, line.Debit, line.Credit, 0m, part));
        }

        var saleLines = tradeLines.Where(l => l.SourceType == LedgerSources.Sale && saleIds.Contains(l.SourceId))
            .GroupBy(l => l.SourceId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var sale in sales)
        {
            var posted = saleLines.GetValueOrDefault(sale.Id) ?? [];
            var net = posted.Sum(l => l.Debit - l.Credit);
            var total = sale.GrandTotalTtc + sale.RoundingAdjustment;
            decimal debit, credit, paid;
            if (sale.Status == SaleStatus.Refunded)
            {
                // Goods given back are a credit; what the till paid back over the counter is a
                // debit beside it. What is left is what the refund took off the account.
                credit = Math.Max(total, -net);
                debit = credit + net;
                paid = debit;
            }
            else
            {
                // What was sold is a debit; what the till took on the spot is a credit beside it.
                // What is left is what the sale put on the account — nothing, for a cash sale.
                debit = Math.Max(total, net);
                credit = debit - net;
                paid = credit;
            }
            movements.Add(new Movement(
                posted.Count > 0 ? posted.Min(l => l.EntryDate) : sale.SoldAtUtc,
                LedgerSources.Sale, sale.Id, sale.Reference,
                posted.Select(l => l.Description).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)),
                posted.Any(l => l.IsReversal), debit, credit, paid, Part.Of(CustomerStatementKinds.Sale)));
        }

        movements = movements.OrderBy(m => m.Date).ToList();

        // ---- the period, in the part asked for ----
        var regional = regionalSettings.Get();
        var shown = movements.Select(m => m.In(filter, regional.RoundMoney)).OfType<Movement>().ToList();
        var opening = shown.Where(m => m.Date < from).Sum(m => m.Debit - m.Credit);
        var inPeriod = shown.Where(m => m.Date >= from && m.Date < to).ToList();

        var numbers = await NumbersAsync(inPeriod.Select(l => (l.SourceType, l.SourceId)), ct);
        var invoicesBySource = await db.Invoices.AsNoTracking()
            .Where(i => i.CustomerId == customerId && i.SourceId != null)
            .Select(i => new { i.SourceType, i.SourceId, i.Number })
            .ToListAsync(ct);

        var rows = new List<CustomerStatementRowDto>(inPeriod.Count);
        var running = opening;
        foreach (var line in inPeriod)
        {
            running += line.Debit - line.Credit;
            var key = (line.SourceType, line.SourceId);
            rows.Add(new CustomerStatementRowDto(
                line.Date, line.SourceType, line.SourceId,
                numbers.GetValueOrDefault(key) ?? line.Memo,
                // A sale or a rental note that has since been invoiced shows its invoice number
                // too: the customer is holding that piece of paper, and a statement they cannot
                // match against their own file is a statement they will dispute.
                invoicesBySource
                    .FirstOrDefault(i => (i.SourceType == line.SourceType || LedgerSources.DailyCorrectionFor(i.SourceType!) == line.SourceType)
                        && i.SourceId == line.SourceId)?.Number,
                line.Description, line.IsReversal, line.Debit, line.Credit, running,
                line.Part.Kind, line.PaidAtTill));
        }

        var closing = opening + inPeriod.Sum(l => l.Debit - l.Credit);
        // The whole account, whatever is shown: the reconciliation figures below are always its.
        var wholeClosing = tradeLines.Where(l => l.EntryDate < to).Sum(l => l.Debit - l.Credit);

        // Uncleared paper: money the customer has handed over that has not arrived. It is not
        // part of the trade balance and it is not subtracted from it — that is the whole of
        // §5.4 — but it is theirs and it belongs on their statement.
        var pending = accounts.TryGetValue(LedgerAccounts.NotesReceivable, out var notes)
            ? lines.Where(l => l.AccountId == notes && l.EntryDate < to).Sum(l => l.Debit - l.Credit)
            : 0m;

        var invoices = await db.Invoices.AsNoTracking()
            .Where(i => i.CustomerId == customerId)
            .Select(i => i.Id)
            .ToListAsync(ct);
        var standings = await receivables.StandingsAsync(invoices, ct);
        var invoiced = standings.Values.Sum(s => s.Outstanding);

        return new CustomerStatementDto(
            customer.Id, customer.Code, customer.FullName, from, to,
            opening, closing,
            inPeriod.Sum(l => l.Debit), inPeriod.Sum(l => l.Credit),
            rows,
            pending, wholeClosing + pending,
            // The denormalised balance the till and the credit-limit warning read. It should
            // equal trade debt plus uncleared paper; stating the difference is what turns a
            // drift into something someone can see rather than something that has to be found.
            customer.Balance, customer.Balance - (wholeClosing + pending),
            customer.CreditLimit,
            invoiced, wholeClosing - invoiced,
            StatementFilters.Key(filter));
    }

    /// <summary>
    /// The kind of a movement from its source alone. Receipts, payments and credit notes are
    /// worked out from their documents in <see cref="PartsAsync"/>; this is what the rest are.
    /// A new source that puts money on 411 (an opening balance's adjustment, say) is named here.
    /// </summary>
    private static string KindOf(string sourceType) => sourceType switch
    {
        LedgerSources.Sale or LedgerSources.PaymentSettlement or LedgerSources.Invoice => CustomerStatementKinds.Sale,
        LedgerSources.RentalIssue or LedgerSources.RentalReturn or LedgerSources.RentalIssueDailyCorrection
            or LedgerSources.RentalReturnDailyCorrection or LedgerSources.RentalCreditDailyCorrection => CustomerStatementKinds.Rental,
        LedgerSources.CustomerReceipt => CustomerStatementKinds.Receipt,
        LedgerSources.PaymentVoucher => CustomerStatementKinds.Payment,
        LedgerSources.OpeningBalance or LedgerSources.CustomerOpening => CustomerStatementKinds.Opening,
        LedgerSources.CustomerOpeningCorrection => CustomerStatementKinds.Adjustment,
        // SAL-13: money of theirs the company kept on a cancelled order.
        LedgerSources.CustomerAdvanceKept => CustomerStatementKinds.Adjustment,
        _ => CustomerStatementKinds.Other,
    };

    /// <summary>
    /// What part of each receipt, payment voucher and credit note belongs to sales and to rentals,
    /// read from the documents themselves.
    /// </summary>
    private async Task<Dictionary<(string, Guid), Part>> PartsAsync(
        Guid customerId, IEnumerable<(string SourceType, Guid SourceId)> sources, CancellationToken ct)
    {
        var wanted = sources.Distinct().ToList();
        var result = new Dictionary<(string, Guid), Part>();
        var origins = await db.Invoices.AsNoTracking()
            .Where(i => i.CustomerId == customerId)
            .Select(i => new { i.Id, i.Origin })
            .ToDictionaryAsync(i => i.Id, i => i.Origin, ct);

        var receiptIds = wanted.Where(w => w.SourceType == LedgerSources.CustomerReceipt).Select(w => w.SourceId).ToList();
        if (receiptIds.Count > 0)
        {
            var receipts = await db.ReceiptVouchers.AsNoTracking().Include(r => r.Allocations)
                .Where(r => receiptIds.Contains(r.Id)).ToListAsync(ct);
            foreach (var r in receipts)
            {
                Part part;
                if (r.ContractId is not null || r.ReturnNoteId is not null)
                    part = new Part(CustomerStatementKinds.Receipt, 0m, r.Amount, r.Amount);
                else
                {
                    var rental = r.Allocations.Where(a => origins.GetValueOrDefault(a.InvoiceId) == InvoiceOrigin.Rental).Sum(a => a.Amount);
                    var sale = r.Allocations.Where(a => origins.GetValueOrDefault(a.InvoiceId) != InvoiceOrigin.Rental).Sum(a => a.Amount);
                    part = new Part(CustomerStatementKinds.Receipt, sale, rental, r.Amount);
                }
                result[(LedgerSources.CustomerReceipt, r.Id)] = part;
            }
        }

        var voucherIds = wanted.Where(w => w.SourceType == LedgerSources.PaymentVoucher).Select(w => w.SourceId).ToList();
        if (voucherIds.Count > 0)
        {
            var vouchers = await db.PaymentVouchers.AsNoTracking().Where(v => voucherIds.Contains(v.Id))
                .Select(v => new { v.Id, v.ContractId, v.ReturnNoteId, v.SourceType }).ToListAsync(ct);
            foreach (var v in vouchers)
                result[(LedgerSources.PaymentVoucher, v.Id)] =
                    v.ContractId is not null || v.ReturnNoteId is not null
                        || v.SourceType == LedgerSources.RentalReturn || v.SourceType == LedgerSources.RentalIssue
                        ? Part.Whole(CustomerStatementKinds.Payment, StatementFilter.Rentals)
                        : v.SourceType == LedgerSources.Sale
                            ? Part.Whole(CustomerStatementKinds.Payment, StatementFilter.Sales)
                            : Part.Of(CustomerStatementKinds.Payment);
        }

        var creditIds = wanted.Where(w => w.SourceType == LedgerSources.CreditNote).Select(w => w.SourceId).ToList();
        if (creditIds.Count > 0)
        {
            var credits = await db.CreditNotes.AsNoTracking().Where(c => creditIds.Contains(c.Id))
                .Select(c => new { c.Id, c.InvoiceId }).ToListAsync(ct);
            foreach (var c in credits)
                result[(LedgerSources.CreditNote, c.Id)] = Part.Of(
                    origins.GetValueOrDefault(c.InvoiceId) == InvoiceOrigin.Rental ? CustomerStatementKinds.Rental : CustomerStatementKinds.Sale);
        }

        return result;
    }

    /// <summary>
    /// A movement's kind and how much of it is a sale's and a rental's, out of <paramref name="Base"/>:
    /// a receipt of 1 000 with 600 allocated to a rental invoice is (0, 600, 1 000).
    /// </summary>
    private sealed record Part(string Kind, decimal SalesShare, decimal RentalsShare, decimal Base)
    {
        public static Part Of(string kind) => kind switch
        {
            CustomerStatementKinds.Sale => new Part(kind, 1m, 0m, 1m),
            CustomerStatementKinds.Rental => new Part(kind, 0m, 1m, 1m),
            _ => new Part(kind, 0m, 0m, 1m),
        };

        public static Part Whole(string kind, StatementFilter to) =>
            to == StatementFilter.Rentals ? new Part(kind, 0m, 1m, 1m) : new Part(kind, 1m, 0m, 1m);

        public decimal ShareOf(StatementFilter filter) => filter switch
        {
            StatementFilter.Sales => SalesShare,
            StatementFilter.Rentals => RentalsShare,
            _ => Base,
        };
    }

    private sealed record Movement(
        DateTime Date, string SourceType, Guid SourceId, string? Memo, string? Description, bool IsReversal,
        decimal Debit, decimal Credit, decimal PaidAtTill, Part Part)
    {
        /// <summary>
        /// This movement as the narrowed view shows it, or null when none of it belongs there.
        /// Multiplied before it is divided, so a line equal to its document's amount comes out as
        /// the allocation itself, to the last unit; any other line (a part reversed) is rounded the
        /// company's way.
        /// </summary>
        public Movement? In(StatementFilter filter, Func<decimal, decimal> round)
        {
            if (filter == StatementFilter.All) return this;
            var share = Part.ShareOf(filter);
            if (share == 0m || Part.Base == 0m) return null;
            if (share == Part.Base) return this;
            return this with
            {
                Debit = round(Debit * share / Part.Base),
                Credit = round(Credit * share / Part.Base),
                PaidAtTill = round(PaidAtTill * share / Part.Base),
            };
        }
    }

    /// <summary>
    /// How old the debt is, per customer and in total.
    ///
    /// <para>
    /// This one is built from invoices, because a due date is a property of an invoice and
    /// nothing else in the system has one. It therefore covers invoiced debt only — and rather
    /// than leave that as something a reader has to work out, every customer carries the 411
    /// balance beside the invoiced total and the gap between them as its own figure, exactly as
    /// the consolidated P&amp;L reports what it eliminated.
    /// </para>
    /// </summary>
    public async Task<DebtAgeingDto> AgeAsync(DateTime asOf, Guid? customerId = null, CancellationToken ct = default)
    {
        var invoiceQuery = db.Invoices.AsNoTracking().Where(i => i.IssuedAtUtc <= asOf);
        if (customerId is Guid one) invoiceQuery = invoiceQuery.Where(i => i.CustomerId == one);

        var invoices = await invoiceQuery
            .Select(i => new { i.Id, i.CustomerId, i.Number, i.DueDateUtc })
            .ToListAsync(ct);

        var standings = await receivables.StandingsAsync(invoices.Select(i => i.Id).ToList(), ct, asOf);
        var balances = await TradeBalancesAsync(customerId, asOf, ct);

        var customerIds = invoices.Select(i => i.CustomerId).Concat(balances.Keys).Distinct().ToList();
        var customers = await db.Customers.AsNoTracking()
            .Where(c => customerIds.Contains(c.Id))
            .ToListAsync(ct);

        var rows = new List<CustomerAgeingDto>();
        foreach (var customer in customers.OrderBy(c => c.FullName, StringComparer.CurrentCulture))
        {
            var theirs = invoices.Where(i => i.CustomerId == customer.Id).ToList();
            var buckets = DebtAgeing.Buckets.ToDictionary(b => b, _ => 0m);
            var credit = 0m;
            var oldest = 0;

            foreach (var invoice in theirs)
            {
                var outstanding = standings[invoice.Id].Outstanding;
                // A credited invoice can go past zero: the customer paid and was then given the
                // money back on paper. That is a credit the company owes, not a debt of negative
                // age, and adding it into a bucket would quietly reduce what somebody else owes.
                if (outstanding < 0) { credit += -outstanding; continue; }
                if (outstanding == 0) continue;

                buckets[DebtAgeing.BucketFor(invoice.DueDateUtc, asOf)] += outstanding;
                oldest = Math.Max(oldest, DebtAgeing.DaysOverdue(invoice.DueDateUtc, asOf));
            }

            var total = buckets.Values.Sum();
            var ledger = balances.GetValueOrDefault(customer.Id);
            if (total == 0 && credit == 0 && ledger == 0) continue;

            rows.Add(new CustomerAgeingDto(
                customer.Id, customer.Code, customer.FullName, customer.CreditLimit,
                total,
                buckets[AgeingBucket.Current], buckets[AgeingBucket.Days1To30],
                buckets[AgeingBucket.Days31To60], buckets[AgeingBucket.Days61To90],
                buckets[AgeingBucket.Over90],
                oldest, credit, ledger, ledger - (total - credit),
                customer.CreditLimit > 0 && ledger > customer.CreditLimit));
        }

        return new DebtAgeingDto(
            asOf,
            rows.Sum(r => r.Total), rows.Sum(r => r.Current), rows.Sum(r => r.Days1To30),
            rows.Sum(r => r.Days31To60), rows.Sum(r => r.Days61To90), rows.Sum(r => r.Over90),
            rows.Sum(r => r.CreditBalance), rows.Sum(r => r.LedgerBalance), rows.Sum(r => r.Uninvoiced),
            rows);
    }

    /// <summary>Account 411 per customer, up to a date. The figure every other total is checked against.</summary>
    private async Task<Dictionary<Guid, decimal>> TradeBalancesAsync(
        Guid? customerId, DateTime asOf, CancellationToken ct)
    {
        // M7b: 411 plus what the customer's advances still hold on 419 (and 4457, their VAT due on receipt) — the same
        // account the statement reads, so an advance on 419 reads exactly as an unallocated receipt on 411 always has.
        var ids = await db.LedgerAccounts.AsNoTracking()
            .Where(a => a.Code == LedgerAccounts.Customers || a.Code == LedgerAccounts.CustomerDeposits || a.Code == LedgerAccounts.TaxCollected)
            .Select(a => new { a.Id, a.Code }).ToListAsync(ct);
        if (!ids.Any(a => a.Code == LedgerAccounts.Customers)) return [];
        var accountIds = ids.Select(a => a.Id).ToList();

        var query = db.LedgerEntryLines.AsNoTracking()
            .Where(l => accountIds.Contains(l.AccountId) && l.CustomerId != null);
        if (customerId is Guid one) query = query.Where(l => l.CustomerId == one);

        var rows = await query
            .Join(db.LedgerEntries.AsNoTracking().Where(e => e.EntryDate <= asOf),
                l => l.EntryId, e => e.Id, (l, e) => new { l.CustomerId, l.Debit, l.Credit })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.CustomerId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Debit - r.Credit));
    }

    /// <summary>
    /// The document number behind each entry, so every line of a statement leads back to a piece
    /// of paper the customer can find in their own file.
    /// </summary>
    private async Task<Dictionary<(string, Guid), string>> NumbersAsync(
        IEnumerable<(string SourceType, Guid SourceId)> sources, CancellationToken ct)
    {
        var wanted = sources.Distinct().ToList();
        var numbers = new Dictionary<(string, Guid), string>();

        foreach (var sourceType in Numbered)
        {
            var ids = wanted.Where(s => s.SourceType == sourceType).Select(s => s.SourceId).ToList();
            if (ids.Count == 0) continue;

            List<(Guid Id, string Number)> found = sourceType switch
            {
                LedgerSources.Invoice => await db.Invoices.AsNoTracking()
                    .Where(x => ids.Contains(x.Id)).Select(x => new ValueTuple<Guid, string>(x.Id, x.Number)).ToListAsync(ct),
                LedgerSources.CreditNote or LedgerSources.RentalCreditDailyCorrection => await db.CreditNotes.AsNoTracking()
                    .Where(x => ids.Contains(x.Id)).Select(x => new ValueTuple<Guid, string>(x.Id, x.Number)).ToListAsync(ct),
                LedgerSources.CustomerReceipt => await db.ReceiptVouchers.AsNoTracking()
                    .Where(x => ids.Contains(x.Id)).Select(x => new ValueTuple<Guid, string>(x.Id, x.Number)).ToListAsync(ct),
                // SAL-13: a kept part reads as the receipt it was kept from.
                LedgerSources.CustomerAdvanceKept => await db.CustomerAdvanceApplications.AsNoTracking()
                    .Where(x => ids.Contains(x.Id))
                    .Join(db.ReceiptVouchers.AsNoTracking(), a => a.ReceiptVoucherId, v => v.Id,
                        (a, v) => new ValueTuple<Guid, string>(a.Id, v.Number))
                    .ToListAsync(ct),
                LedgerSources.Sale => await db.Sales.AsNoTracking()
                    .Where(x => ids.Contains(x.Id)).Select(x => new ValueTuple<Guid, string>(x.Id, x.Reference)).ToListAsync(ct),
                LedgerSources.RentalIssue or LedgerSources.RentalIssueDailyCorrection => await db.IssueNotes.AsNoTracking()
                    .Where(x => ids.Contains(x.Id)).Select(x => new ValueTuple<Guid, string>(x.Id, x.Number)).ToListAsync(ct),
                LedgerSources.RentalReturn or LedgerSources.RentalReturnDailyCorrection => await db.ReturnNotes.AsNoTracking()
                    .Where(x => ids.Contains(x.Id)).Select(x => new ValueTuple<Guid, string>(x.Id, x.Number)).ToListAsync(ct),
                // A settlement's own id is a payment line; the paper the customer knows is the
                // receipt the sale was written on.
                LedgerSources.PaymentSettlement => await db.SalePayments.AsNoTracking()
                    .Where(x => ids.Contains(x.Id))
                    .Join(db.Sales.AsNoTracking(), p => p.SaleId, s => s.Id,
                        (p, s) => new ValueTuple<Guid, string>(p.Id, s.Reference))
                    .ToListAsync(ct),
                // A refund paid back to the customer, on its own numbered paper.
                LedgerSources.PaymentVoucher => await db.PaymentVouchers.AsNoTracking()
                    .Where(x => ids.Contains(x.Id)).Select(x => new ValueTuple<Guid, string>(x.Id, x.Number)).ToListAsync(ct),
                _ => []
            };

            foreach (var (id, number) in found) numbers[(sourceType, id)] = number;
        }

        return numbers;
    }
}
