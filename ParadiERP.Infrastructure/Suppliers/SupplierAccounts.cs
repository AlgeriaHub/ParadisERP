using ParadiERP.Application.Contracts;
using ParadiERP.Application.Customers;
using ParadiERP.Application.Regional;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Catalog;
using ParadiERP.Infrastructure.Ledger;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Suppliers;

/// <summary>
/// A supplier's account (owner, 2026-10-10: a basic suppliers module): its code, its opening
/// balance and the corrections of it, and its statement.
///
/// <para>
/// <b>One sign rule.</b> A supplier's balance is what the company owes them, the mirror of a
/// customer's. The request carries the customers' words — <c>owesUs</c> (the supplier owes the
/// company, an advance) and <c>weOwe</c> (the company owes the supplier) — and
/// <see cref="OpeningBalanceRules.Sign"/> turns them into a debit on 401 (+1) or a credit (−1);
/// the balance moves the other way, because 401 is a liability.
/// </para>
/// <para>
/// <b>The statement is account 401 for this supplier</b>, read from
/// <c>LedgerEntryLine.SupplierId</c> — never assembled from documents, for the reason the customer
/// statement is not (<see cref="Receivables.CustomerStatements"/>).
/// </para>
/// </summary>
public sealed class SupplierAccounts(PosDbContext db, LedgerPoster ledger, IRegionalSettingsProvider regional, TimeProvider time)
{
    public DateOnly Today() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), StoreDocumentHistory.Zone(regional)).DateTime);

    /// <summary>What is wrong with an opening balance or a correction's figure, rounded to the currency first; or null.</summary>
    public string? Problem(CustomerOpeningBalanceRequest r) =>
        OpeningBalanceRules.Problem(regional.Get().RoundMoney(r.Amount), r.Direction, r.On, Today());

    /// <summary>The next code, under the version lock the caller's write scope holds; the unique index is the backstop.</summary>
    public async Task<string> NextCodeAsync(CancellationToken ct) =>
        $"FRN-{await db.Suppliers.CountAsync(ct) + 1:D4}";

    /// <summary>Writes the opening balance on the supplier and posts it. The caller checked "once" and <see cref="Problem"/>.</summary>
    public async Task ApplyOpeningAsync(Supplier supplier, CustomerOpeningBalanceRequest r, Guid branchId, string? by, CancellationToken ct)
    {
        var debit = OpeningBalanceRules.Sign(r.Direction)!.Value * regional.Get().RoundMoney(r.Amount);
        var on = r.On ?? Today();

        supplier.OpeningBalance = -debit;
        supplier.OpeningBalanceOn = on;
        supplier.OpeningBalanceSetAtUtc = time.GetUtcNow().UtcDateTime;
        supplier.OpeningBalanceSetBy = by;
        supplier.Balance -= debit;
        supplier.UpdatedAtUtc = DateTime.UtcNow;

        var draft = new PostingDraft
        {
            EntryDate = EntryDate(on),
            BranchId = branchId,
            // The supplier's own id: one opening entry per supplier, whatever slips past the checks.
            SourceType = LedgerSources.SupplierOpening,
            SourceId = supplier.Id,
            Description = $"Opening balance — {supplier.Code} {supplier.Name}".Trim(),
            CreatedBy = by
        };
        draft.AddIfAny(PostingLine.Signed(LedgerAccounts.Suppliers, debit, memo: supplier.Code, supplierId: supplier.Id));
        draft.AddIfAny(PostingLine.Signed(LedgerAccounts.OpeningBalance, -debit, memo: supplier.Code));
        await ledger.PostAsync(draft, ct);
    }

    /// <summary>A correction with its reason: a numbered paper, an entry against 890 for the supplier, the balance moved by it.</summary>
    public async Task<SupplierOpeningCorrection> CorrectAsync(
        Supplier supplier, OpeningCorrectionRequest r, Guid branchId, string? by, CancellationToken ct)
    {
        var debit = OpeningBalanceRules.Sign(r.Direction)!.Value * regional.Get().RoundMoney(r.Amount);
        var on = r.On ?? Today();
        var correction = new SupplierOpeningCorrection
        {
            SupplierId = supplier.Id,
            BranchId = branchId,
            Amount = -debit,
            EffectiveOn = on,
            Reason = r.Reason.Trim(),
            CreatedBy = by,
            CreatedAtUtc = time.GetUtcNow().UtcDateTime,
            Number = $"SOC-{await db.SupplierOpeningCorrections.CountAsync(ct) + 1:D6}"
        };
        db.SupplierOpeningCorrections.Add(correction);
        supplier.Balance -= debit;
        supplier.UpdatedAtUtc = DateTime.UtcNow;

        var draft = new PostingDraft
        {
            EntryDate = EntryDate(on),
            BranchId = branchId,
            SourceType = LedgerSources.SupplierOpeningCorrection,
            SourceId = correction.Id,
            Description = $"Opening balance correction {correction.Number} — {correction.Reason}",
            CreatedBy = by
        };
        draft.AddIfAny(PostingLine.Signed(LedgerAccounts.Suppliers, debit, memo: correction.Number, supplierId: supplier.Id));
        draft.AddIfAny(PostingLine.Signed(LedgerAccounts.OpeningBalance, -debit, memo: correction.Number));
        await ledger.PostAsync(draft, ct);
        return correction;
    }

    /// <summary>The balance before the period, every 401 movement for the supplier in it, the balance at its end.</summary>
    public async Task<SupplierStatementDto?> StatementAsync(Guid supplierId, DateTime from, DateTime to, CancellationToken ct)
    {
        var supplier = await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == supplierId, ct);
        if (supplier is null) return null;
        var account = await db.LedgerAccounts.AsNoTracking()
            .Where(a => a.Code == LedgerAccounts.Suppliers).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);
        if (account is not Guid payable) return null;

        // Loaded and summed in memory: SQLite stores decimal as TEXT (CustomerStatements says why).
        var lines = await db.LedgerEntryLines.AsNoTracking()
            .Where(l => l.SupplierId == supplierId && l.AccountId == payable)
            .Join(db.LedgerEntries.AsNoTracking(), l => l.EntryId, e => e.Id, (l, e) => new
            {
                l.Debit, l.Credit, l.Memo, e.EntryDate, e.SourceType, e.SourceId, e.Description, e.IsReversal
            })
            .ToListAsync(ct);

        // What the company owes: credits raise it, debits lower it.
        var opening = lines.Where(l => l.EntryDate < from).Sum(l => l.Credit - l.Debit);
        var inPeriod = lines.Where(l => l.EntryDate >= from && l.EntryDate < to).OrderBy(l => l.EntryDate).ToList();

        var voucherIds = inPeriod.Where(l => l.SourceType == LedgerSources.PaymentVoucher).Select(l => l.SourceId).ToList();
        var voucherNumbers = voucherIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.PaymentVouchers.AsNoTracking().Where(v => voucherIds.Contains(v.Id))
                .ToDictionaryAsync(v => v.Id, v => v.Number, ct);

        var running = opening;
        var rows = new List<SupplierStatementRowDto>(inPeriod.Count);
        foreach (var line in inPeriod)
        {
            running += line.Credit - line.Debit;
            var number = line.SourceType == LedgerSources.PaymentVoucher
                ? voucherNumbers.GetValueOrDefault(line.SourceId) ?? line.Memo
                : line.Memo;
            rows.Add(new SupplierStatementRowDto(line.EntryDate, line.SourceType, line.SourceId, number,
                line.Description, line.IsReversal, line.Debit, line.Credit, running));
        }

        var closing = opening + inPeriod.Sum(l => l.Credit - l.Debit);
        return new SupplierStatementDto(
            supplier.Id, supplier.Code, supplier.Name, from, to, opening, closing,
            inPeriod.Sum(l => l.Debit), inPeriod.Sum(l => l.Credit), rows,
            // The running figure the card shows; it should equal the ledger's, and saying the
            // difference turns a drift into a number someone can see.
            supplier.Balance, supplier.Balance - closing);
    }

    public static SupplierDto ToDto(Supplier s) => new(
        s.Id, s.Code, s.Name, s.Phone, s.Address, s.FiscalIds, s.Note, s.Balance,
        s.OpeningBalance, s.OpeningBalanceOn, s.OpeningBalanceSetAtUtc, s.OpeningBalanceSetBy);

    public static SupplierOpeningCorrectionDto ToDto(SupplierOpeningCorrection c) => new(
        c.Id, c.Number, c.SupplierId, c.Amount, c.EffectiveOn, c.Reason, c.CreatedBy, c.CreatedAtUtc);

    /// <summary>The start of that day in the company's zone, as the customer's opening balance is filed.</summary>
    private DateTime EntryDate(DateOnly on)
    {
        var zone = StoreDocumentHistory.Zone(regional);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(on.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), zone);
    }
}
