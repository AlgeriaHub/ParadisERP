using System.Text.Json;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Engine = ParadisERP.Application.Rentals;

namespace ParadisERP.Infrastructure.Rentals;

/// <summary>
/// Owner-approved historical restatement. The migration queues existing contracts only.
/// Recomputes financial documents; retains original snapshots and posts balanced differences,
/// never edits ledger history or multiplies actual payments. One atomic, serialized company write.
/// </summary>
public sealed class RentalDailyPricingUpgrade(PosDbContext db, IRegionalSettingsProvider regional)
{
    private const string Actor = "upgrade:daily-rental-pricing-v1";
    private readonly LedgerPoster ledger = new(db);
    private readonly RentalDocuments rentals = new(db, new LedgerPoster(db), regional);
    private RegionalSettings Money => regional.Get();

    /// <param name="Hire">
    /// For an issue note's line, its quantity, daily price and days: an invoice raised since
    /// 2026-10-10 carries the three in their own columns (InvoiceLine.Days) rather than quantity 1
    /// and the detail in the name, and is corrected in that shape. Null on a return-note charge.
    /// </param>
    private sealed record Candidate(Guid? ProductId, string Sku, string Name, decimal TaxRate,
        string Account, decimal OldNet, decimal NewNet, (decimal Qty, decimal DailyPrice, int Days)? Hire = null);
    private sealed record Target(string Source, Guid Id, Guid CustomerId, Guid BranchId,
        DateTime CreatedAt, string Number, PostingDraft? Draft, ReturnNote? Return = null);
    private sealed record Work(RentalDailyPricingCorrection Audit, RentalDocuments.Documents Docs,
        List<RentalCharge> Charges, List<Invoice> Invoices, List<CreditNote> Credits);

    public async Task<int> ApplyAsync(CancellationToken ct = default)
    {
        if (!await db.RentalDailyPricingCorrections.AnyAsync(x => x.CompletedAtUtc == null, ct)) return 0;
        await using var tx = await db.BeginWriteScopeAsync(ct);
        // Cross-process serialization, acquired before re-reading the pending queue.
        await db.NextVersionAsync(ct);
        var queue = await db.RentalDailyPricingCorrections.Where(x => x.CompletedAtUtc == null).ToListAsync(ct);
        if (queue.Count == 0) { await tx.CommitAsync(ct); return 0; }
        var works = new List<Work>();
        var targets = new List<Target>();
        foreach (var audit in queue)
        {
            var docs = await rentals.ReadForCorrectionAsync(audit.ContractId, ct)
                ?? throw new InvalidOperationException($"Daily pricing: contract {audit.ContractId} no longer exists.");
            var ids = docs.IssueNotes.Select(n => n.Id).Concat(docs.ReturnNotes.Select(n => n.Id)).ToList();
            var invoices = await db.Invoices.Include(i => i.Lines)
                .Where(i => i.Origin == InvoiceOrigin.Rental && i.SourceId != null && ids.Contains(i.SourceId.Value)).ToListAsync(ct);
            var invoiceIds = invoices.Select(i => i.Id).ToList();
            var credits = await db.CreditNotes.Include(n => n.Lines).Where(n => invoiceIds.Contains(n.InvoiceId)).ToListAsync(ct);
            var charges = await db.RentalCharges.Where(c => c.ContractId == audit.ContractId).ToListAsync(ct);
            var work = new Work(audit, docs, charges, invoices, credits);
            audit.BeforeJson = Snapshot(work);
            works.Add(work);
            Reprice(work, targets);
        }

        var customerIds = works.Select(w => w.Docs.Contract.CustomerId).Distinct().ToList();
        var customers = await db.Customers.Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var accounts = await db.LedgerAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Code, ct);
        var sourceIds = targets.Select(t => t.Id).ToList();
        var originals = await db.LedgerEntries.AsNoTracking().Include(e => e.Lines)
            .Where(e => sourceIds.Contains(e.SourceId)).ToListAsync(ct);
        foreach (var target in targets)
        {
            if (originals.Any(e => e.SourceId == target.Id && e.SourceType == LedgerSources.DailyCorrectionFor(target.Source)))
                throw new InvalidOperationException($"Daily pricing: {target.Number} already has a correction but its audit is pending.");
            if (originals.Where(e => e.SourceId == target.Id && e.SourceType == target.Source)
                .SelectMany(e => e.Lines).Any(l => accounts[l.AccountId] == LedgerAccounts.Customers && l.CustomerId != target.CustomerId))
                throw new InvalidOperationException($"Daily pricing: {target.Number} has inconsistent customer postings.");
        }

        // Reconstruct pre-return balances with corrected source entries in chronological order.
        // Cash receipts/refunds remain their real amounts, including settled/pending cheque behavior.
        var customerAccount = accounts.Single(a => a.Value == LedgerAccounts.Customers).Key;
        var history = await db.LedgerEntries.AsNoTracking().Include(e => e.Lines)
            .Where(e => e.Lines.Any(l => l.AccountId == customerAccount && l.CustomerId != null && customerIds.Contains(l.CustomerId.Value)))
            .ToListAsync(ct);
        foreach (var customerId in customerIds)
        {
            if (!customers.TryGetValue(customerId, out var customer))
                throw new InvalidOperationException($"Daily pricing: customer {customerId} is missing.");
            decimal CustomerNet(LedgerEntry e) => e.Lines.Where(l => l.AccountId == customerAccount && l.CustomerId == customerId).Sum(l => l.Debit - l.Credit);
            var balance = customer.Balance - history.Sum(CustomerNet);
            var ownTargets = targets.Where(t => t.CustomerId == customerId).ToList();
            var replaced = ownTargets.Select(t => (t.Source, t.Id)).ToHashSet();
            var events = history.Where(e => !replaced.Contains((e.SourceType, e.SourceId)))
                .Select(e => (At: e.CreatedAtUtc, Id: e.Id, Net: CustomerNet(e), Target: (Target?)null)).ToList();
            foreach (var target in ownTargets)
            {
                var original = originals.Where(e => e.SourceType == target.Source && e.SourceId == target.Id).ToList();
                if (original.Any(e => e.IsReversal) || original.Count > 1)
                    throw new InvalidOperationException($"Daily pricing: {target.Number} has reversed or ambiguous postings; correction rolled back.");
                events.Add((original.FirstOrDefault()?.CreatedAtUtc ?? target.CreatedAt, target.Id, RentalDailyPricingUpgrade.CustomerNet(target.Draft), target));
            }
            foreach (var e in events.OrderBy(e => e.At).ThenBy(e => e.Id))
            {
                if (e.Target?.Return is ReturnNote note)
                {
                    var charges = works.SelectMany(w => w.Charges).Where(c => c.ReturnNoteId == note.Id && c.IsPosted).Sum(c => c.Amount);
                    var relief = Engine.RentalSettlement.Relieve(note.UnusedRent, balance + charges);
                    note.ReliefDebtReduction = relief.DebtReduction;
                    note.ReliefPaymentVoucher = relief.PaymentVoucher;
                }
                balance += e.Net;
            }
        }

        foreach (var target in targets)
        {
            var old = originals.Where(e => e.SourceType == target.Source && e.SourceId == target.Id).SelectMany(e => e.Lines).ToList();
            var correction = new PostingDraft
            {
                EntryDate = DateTime.UtcNow, BranchId = target.BranchId,
                SourceType = LedgerSources.DailyCorrectionFor(target.Source)!, SourceId = target.Id,
                Description = $"Daily rental pricing correction — {target.Number}", CreatedBy = Actor
            };
            var sums = new Dictionary<(string Account, Guid Branch, Guid? Customer), decimal>();
            foreach (var line in old)
                Add(sums, (accounts[line.AccountId], line.BranchId, line.CustomerId), -(line.Debit - line.Credit));
            foreach (var line in target.Draft?.Lines ?? [])
                Add(sums, (line.AccountCode, line.BranchId ?? target.BranchId, line.CustomerId), line.Debit - line.Credit);
            foreach (var (key, amount) in sums)
                correction.AddIfAny(PostingLine.Signed(key.Account, amount, key.Branch, target.Number, key.Customer));
            var delta = sums.Where(x => x.Key.Account == LedgerAccounts.Customers).Sum(x => x.Value);
            await ledger.PostAsync(correction, ct);
            if (delta != 0)
            {
                var customer = customers[target.CustomerId];
                customer.Balance += delta;
                await db.TouchAsync(customer, ct);
            }
        }
        // Save postings before standing reads, but retain the enclosing atomic transaction.
        await db.SaveChangesAsync(ct);
        foreach (var work in works)
        {
            var contract = work.Docs.Contract;
            if (contract.GoodsBackAtUtc != null && contract.ClosedShortReason == null)
            {
                var standing = await RentalClosure.StandingAsync(db, contract.Id, ct);
                if (!standing.IsPaid && contract.Status == RentalContractStatus.Closed)
                {
                    contract.Status = RentalContractStatus.Open;
                    contract.ClosedAtUtc = null;
                }
                else await RentalClosure.TryCloseAsync(db, contract.Id, ct);
            }
            work.Audit.AfterJson = Snapshot(work);
            work.Audit.CompletedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return works.Count;
    }

    private void Reprice(Work work, List<Target> targets)
    {
        var docs = work.Docs;
        var bySource = new Dictionary<Guid, List<Candidate>>();
        var sourceNet = new Dictionary<Guid, decimal>();
        var engineLines = docs.Lines.Select(RentalDocuments.EngineLine).ToList();
        foreach (var note in docs.IssueNotes)
        {
            var candidates = new List<Candidate>();
            foreach (var issued in note.Lines)
            {
                var line = docs.Lines.Single(l => l.Id == issued.ContractLineId);
                var days = Engine.RentalSettlement.DurationOf(RentalDocuments.EngineLine(line), engineLines);
                var net = Engine.RentalPricing.Amount(Money, line.RentUnitPrice, days, issued.Qty);
                var old = Money.RoundMoney(issued.Qty * days * (line.RentUnitPrice / 30m));
                candidates.Add(new(line.ProductId, line.Sku,
                    $"{line.NameSnapshot} — {issued.Qty:0.##} × {days} j @ {line.RentUnitPrice:0.##}/jour",
                    line.TaxRate, LedgerAccounts.RentalIncome, old, net, (issued.Qty, line.RentUnitPrice, days)));
            }
            bySource[note.Id] = candidates;
            var tax = Tax(candidates.Select(l => (l.TaxRate, l.NewNet)));
            var draft = RentalPosting.Issue(note.Id, note.BranchId, docs.Contract.CustomerId, note.Number,
                candidates.Sum(l => l.NewNet), Actor, rentals.PeriodStartsUtc(docs, note), tax);
            sourceNet[note.Id] = CustomerNet(draft);
            targets.Add(new(LedgerSources.RentalIssue, note.Id, docs.Contract.CustomerId, note.BranchId, note.CreatedAtUtc, note.Number, draft));
        }
        var cumulativeUnused = 0m;
        var prefix = new List<ReturnNote>();
        foreach (var note in docs.ReturnNotes)
        {
            prefix.Add(note);
            var unused = rentals.Progress(docs with { ReturnNotes = prefix }).Values.Sum(l => l.UnusedRent);
            note.UnusedRent = Math.Max(0, unused - cumulativeUnused);
            cumulativeUnused = unused;
            var candidates = new List<Candidate>();
            var onNote = work.Charges.Where(c => c.ReturnNoteId == note.Id).ToList();
            foreach (var charge in onNote)
            {
                var old = charge.Amount;
                if (charge.Kind is RentalChargeKind.Rent or RentalChargeKind.Overdue)
                    charge.Amount = Engine.RentalPricing.Amount(Money, charge.UnitPrice, charge.Days, charge.Qty);
                if (!charge.IsPosted) continue;
                var line = docs.Lines.Single(l => l.Id == charge.ContractLineId);
                var detail = charge.Days > 0 ? $"{charge.Qty:0.##} × {charge.Days} j" : $"{charge.Qty:0.##}";
                candidates.Add(new(line.ProductId, line.Sku, $"{line.NameSnapshot} — {charge.Kind} ({detail})", line.TaxRate,
                    RentalPosting.AccountFor(charge.Kind), old, charge.Amount));
            }
            bySource[note.Id] = candidates;
            var taxable = candidates.Select(l => (l.TaxRate, l.NewNet)).ToList();
            var weight = docs.Lines.Sum(l => l.RentUnitPrice * l.Qty);
            if (weight > 0)
                taxable.AddRange(docs.Lines.Where(l => l.RentUnitPrice > 0).GroupBy(l => l.TaxRate)
                    .Select(g => (g.Key, -note.UnusedRent * g.Sum(l => l.RentUnitPrice * l.Qty) / weight)));
            var draft = RentalPosting.Return(note.Id, note.BranchId, docs.Contract.CustomerId, note.Number,
                onNote.Where(c => c.IsPosted).Select(c => new ChargeAmount(c.Kind, c.Amount, note.Number)), note.UnusedRent, Actor, Tax(taxable));
            sourceNet[note.Id] = CustomerNet(draft);
            targets.Add(new(LedgerSources.RentalReturn, note.Id, docs.Contract.CustomerId, note.BranchId, note.CreatedAtUtc, note.Number, draft, note));
        }
        foreach (var invoice in work.Invoices)
        {
            if (!bySource.TryGetValue(invoice.SourceId!.Value, out var candidates))
                throw new InvalidOperationException($"Daily pricing: invoice {invoice.Number} has no rental source.");
            var remaining = candidates.ToList();
            foreach (var line in invoice.Lines)
            {
                var matching = remaining.Where(c => c.ProductId == line.ProductId && c.Sku == line.Sku && c.TaxRate == line.TaxRate
                    && c.Account == line.IncomeAccountCode && (c.OldNet == line.LineNet || c.NewNet == line.LineNet)).ToList();
                // Two shapes: quantity 1 with the detail in the name (every line before 2026-10-10),
                // or quantity, daily price and days in their own columns.
                var priced = line.Days is not null;
                if ((!priced && line.Qty != 1) || line.LineDiscount != 0 || matching.Count == 0
                    || matching.Select(c => c.NewNet).Distinct().Count() != 1
                    || (priced && matching.Any(c => c.Hire is null)))
                    throw new InvalidOperationException($"Daily pricing: cannot unambiguously trace invoice {invoice.Number}, line {line.Id}; correction rolled back.");
                var chosen = matching[0];
                remaining.Remove(chosen);
                if (priced)
                {
                    (line.Qty, line.UnitPrice, var days) = chosen.Hire!.Value;
                    line.Days = days;
                    line.LineNet = chosen.NewNet;
                }
                else
                {
                    line.UnitPrice = line.LineNet = chosen.NewNet;
                    // Preserve names already handed to the customer; only the rate unit changes.
                    line.NameSnapshot = line.NameSnapshot.Replace("/mois", "/jour");
                }
            }
            // A sub-centime monthly charge may have rounded to zero and been omitted originally.
            foreach (var missing in remaining)
            {
                if (missing.OldNet != 0)
                    throw new InvalidOperationException($"Daily pricing: invoice {invoice.Number} omitted a nonzero source line; correction rolled back.");
                if (missing.NewNet == 0) continue;
                invoice.Lines.Add(new InvoiceLine { InvoiceId = invoice.Id, Position = invoice.Lines.Max(l => l.Position) + 1,
                    ProductId = missing.ProductId, Sku = missing.Sku,
                    NameSnapshot = missing.Name[..Math.Min(200, missing.Name.Length)], Qty = 1, UnitPrice = missing.NewNet,
                    LineNet = missing.NewNet, TaxRate = missing.TaxRate, IncomeAccountCode = missing.Account });
            }
            invoice.SubTotalHt = Money.RoundMoney(invoice.Lines.Sum(l => l.LineNet));
            invoice.TaxTotal = invoice.TaxMode == TaxMode.Untaxed ? 0 : Tax(invoice.Lines.Select(l => (l.TaxRate, l.LineNet)));
            invoice.GrandTotalTtc = invoice.SubTotalHt + invoice.TaxTotal;
            // Rental prepayment is source-note relief, not a receipt; receipts stay untouched.
            invoice.PrepaidAmount = Math.Max(0, invoice.GrandTotalTtc - sourceNet[invoice.SourceId.Value]);
        }
        foreach (var credit in work.Credits)
        {
            var invoice = work.Invoices.Single(i => i.Id == credit.InvoiceId);
            foreach (var line in credit.Lines)
            {
                var source = invoice.Lines.Single(l => l.Id == line.InvoiceLineId);
                line.UnitPrice = source.UnitPrice;
                line.Days = source.Days;
                line.LineNet = source.Qty == 0 ? 0 : Money.RoundMoney(source.LineNet * line.Qty / source.Qty);
                line.NameSnapshot = line.NameSnapshot.Replace("/mois", "/jour");
            }
            credit.SubTotalHt = Money.RoundMoney(credit.Lines.Sum(l => l.LineNet));
            credit.TaxTotal = invoice.TaxMode == TaxMode.Untaxed ? 0 : Tax(credit.Lines.Select(l => (l.TaxRate, l.LineNet)));
            credit.GrandTotalTtc = credit.SubTotalHt + credit.TaxTotal;
            var draft = ReceivablePosting.CreditNote(credit, invoice.Number, credit.Lines.GroupBy(l => l.IncomeAccountCode)
                .Select(g => new IncomeAmount(g.Key, g.Sum(l => l.LineNet), credit.Number)), Actor);
            targets.Add(new(LedgerSources.CreditNote, credit.Id, credit.CustomerId, credit.BranchId, credit.CreatedAtUtc, credit.Number, draft));
        }
    }

    private decimal Tax(IEnumerable<(decimal Rate, decimal Net)> lines) =>
        lines.GroupBy(x => x.Rate).Sum(g => g.Key <= 0 ? 0 : Money.RoundMoney(g.Sum(x => x.Net) * g.Key / 100m));

    private static decimal CustomerNet(PostingDraft? draft) => draft?.Lines
        .Where(l => l.AccountCode == LedgerAccounts.Customers).Sum(l => l.Debit - l.Credit) ?? 0;

    private static void Add<TKey>(Dictionary<TKey, decimal> sums, TKey key, decimal amount) where TKey : notnull =>
        sums[key] = sums.GetValueOrDefault(key) + amount;

    private string Snapshot(Work work)
    {
        object Row(object entity) => db.Entry(entity).Properties.ToDictionary(p => p.Metadata.Name, p => p.CurrentValue);
        return JsonSerializer.Serialize(new
        {
            Contract = Row(work.Docs.Contract), Lines = work.Docs.Lines.Select(Row),
            Charges = work.Charges.Select(Row), Returns = work.Docs.ReturnNotes.Select(Row),
            Invoices = work.Invoices.Select(i => new { Header = Row(i), Lines = i.Lines.Select(Row) }),
            Credits = work.Credits.Select(i => new { Header = Row(i), Lines = i.Lines.Select(Row) })
        });
    }
}
