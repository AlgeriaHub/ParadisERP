using ParadiERP.Application.Regional;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Receivables;

/// <summary>Immutable invoice evidence for relief already posted by a rental return.</summary>
internal static class RentalReturnCredits
{
    public static async Task<ReceivableResult<CreditNote>?> CreateAsync(
        PosDbContext db, RegionalSettings regional, Guid returnNoteId, string? by, CancellationToken ct)
    {
        var note = await db.ReturnNotes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == returnNoteId, ct);
        if (note is null) return null;
        var allocations = await db.Set<ReturnNoteLineAllocation>().AsNoTracking()
            .Join(db.ReturnNoteLines.Where(l => l.ReturnNoteId == returnNoteId),
                a => a.ReturnNoteLineId, l => l.Id, (a, l) => a).ToListAsync(ct);
        // No allocation snapshots means historical policy. Never retrospectively reinterpret it.
        if (allocations.Count == 0) return null;
        var issueIds = allocations.Select(a => a.IssueNoteId).Distinct().ToList();
        var invoices = await db.Invoices.AsNoTracking().Include(i => i.Lines)
            .Where(i => i.Origin == InvoiceOrigin.Rental && i.SourceType == LedgerSources.RentalIssue
                && i.SourceId != null && issueIds.Contains(i.SourceId.Value)).ToListAsync(ct);
        var contractLines = await db.RentalContractLines.AsNoTracking()
            .Where(l => l.ContractId == note.ContractId).ToDictionaryAsync(l => l.Id, ct);
        var charges = await db.RentalCharges.AsNoTracking()
            .Where(c => c.ContractId == note.ContractId).ToDictionaryAsync(c => c.Id, ct);
        ReceivableResult<CreditNote>? first = null;
        await using var tx = await db.BeginWriteScopeAsync(ct);
        foreach (var invoice in invoices)
        {
            var existing = await db.CreditNotes.Include(n => n.Lines)
                .FirstOrDefaultAsync(n => n.ReturnNoteId == note.Id && n.InvoiceId == invoice.Id, ct);
            if (existing is not null)
            {
                first ??= ReceivableResult<CreditNote>.Success(existing);
                continue;
            }
            // A later invoice bills final settled fragments and open quantities, not the advance.
            if (invoice.CreatedAtUtc > note.CreatedAtUtc) continue;
            var credit = new CreditNote
            {
                InvoiceId = invoice.Id, CustomerId = invoice.CustomerId, BranchId = invoice.BranchId,
                ReturnNoteId = note.Id, ContractId = note.ContractId,
                SourceType = "rental-return", SourceId = SourceId(note.Id, invoice.Id),
                IssuedAtUtc = note.ReturnedAtUtc, CreatedBy = by,
                Reason = $"Rental return {note.Number}"
            };
            var previous = await db.CreditNoteLines.AsNoTracking()
                .Join(db.CreditNotes.Where(n => n.InvoiceId == invoice.Id),
                    l => l.CreditNoteId, n => n.Id, (l, n) => l).ToListAsync(ct);
            foreach (var allocation in allocations.Where(a => a.IssueNoteId == invoice.SourceId && a.UnusedRentNet > 0))
            {
                var source = contractLines[allocation.ContractLineId];
                var candidates = invoice.Lines.Where(l => l.RentalReturnAllocationId is null
                    && (l.RentalContractLineId == allocation.ContractLineId
                        || l.RentalChargeId is Guid chargeId && charges.TryGetValue(chargeId, out var charge)
                            && charge.ContractLineId == allocation.ContractLineId)).ToList();
                if (candidates.Count == 0)
                    candidates = invoice.Lines.Where(l => l.RentalContractLineId is null
                        && l.RentalReturnAllocationId is null && l.ProductId == source.ProductId
                        && l.Sku == source.Sku && l.UnitPrice == source.RentUnitPrice
                        && l.TaxRate == allocation.TaxRate && l.Days > 0
                        && l.IncomeAccountCode == LedgerAccounts.RentalIncome).ToList();
                if (candidates.Count != 1)
                    return ReceivableResult<CreditNote>.Conflict(
                        "The issued invoice cannot be matched unambiguously to the returned rental line. Correct its attribution before returning these goods.");
                var line = candidates[0];
                var already = previous.Where(l => l.InvoiceLineId == line.Id).Sum(l => l.LineNet)
                    + credit.Lines.Where(l => l.InvoiceLineId == line.Id).Sum(l => l.LineNet);
                var net = regional.RoundMoney(allocation.UnusedRentNet);
                if (net > line.LineNet - already)
                    return ReceivableResult<CreditNote>.Conflict(
                        "This rental invoice was already credited. Resolve the existing credit before recording additional return relief.");
                credit.Lines.Add(new CreditNoteLine
                {
                    CreditNoteId = credit.Id, InvoiceLineId = line.Id,
                    ReturnAllocationId = allocation.Id, ReturnedQty = allocation.Qty,
                    Position = credit.Lines.Count + 1, Sku = line.Sku, NameSnapshot = line.NameSnapshot,
                    Qty = allocation.Qty, UnitPrice = line.UnitPrice,
                    Days = Math.Max(0, (line.Days ?? 0) - allocation.ChargeableDays),
                    TaxRate = line.TaxRate, LineNet = net, IncomeAccountCode = line.IncomeAccountCode
                });
            }
            if (credit.Lines.Count == 0) continue;
            credit.SubTotalHt = regional.RoundMoney(credit.Lines.Sum(l => l.LineNet));
            credit.TaxTotal = invoice.TaxMode == TaxMode.Untaxed ? 0m
                : DocumentTax.On(regional, credit.Lines.GroupBy(l => l.TaxRate)
                    .ToDictionary(g => g.Key, g => g.Sum(l => l.LineNet)));
            credit.GrandTotalTtc = credit.SubTotalHt + credit.TaxTotal;
            credit.Number = $"CRN-{await db.CreditNotes.CountAsync(ct) + 1:D6}";
            db.CreditNotes.Add(credit);
            // Return already posted relief: no second ledger/balance movement, no automatic refund.
            await db.SaveChangesAsync(ct);
            first ??= ReceivableResult<CreditNote>.Success(credit);
        }
        await tx.CommitAsync(ct);
        return first;
    }

    private static Guid SourceId(Guid note, Guid invoice)
    {
        var bytes = note.ToByteArray();
        var other = invoice.ToByteArray();
        for (var i = 0; i < bytes.Length; i++) bytes[i] ^= other[i];
        return new Guid(bytes);
    }
}
