using ParadiERP.Domain;
using Engine = ParadiERP.Application.Rentals;

namespace ParadiERP.Infrastructure.Ledger;

/// <summary>One priced charge as the ledger needs to see it: what it is for, and how much.</summary>
public readonly record struct ChargeAmount(RentalChargeKind Kind, decimal Amount, string? Memo);

/// <summary>
/// What a rental document means in the ledger.
///
/// <para>
/// Pure and static like <see cref="SalePosting"/> and <see cref="StockPosting"/>, for the same
/// reason: these are the rules that decide whether renting reports revenue twice, and a rule
/// that can be checked without a server is a rule that gets checked.
/// </para>
/// <para>
/// <b>The single fact the whole module hangs off:</b> the full agreed period is invoiced when
/// the goods are issued (§5.5, confirmed 2026-09-23). So rent is recognised at the issue note,
/// and the return note's job is to charge what the period could not know in advance — overdue
/// days, repairs, compensation — and to give back the days the customer paid for and never
/// used. Post the return's rent again and every contract reports its hire twice.
/// </para>
/// </summary>
public static class RentalPosting
{
    /// <summary>
    /// Which income line a charge belongs to. The chart already carries all four, seeded and
    /// waiting for exactly this.
    ///
    /// <para>
    /// Rent and overdue are kept apart because the owner asked for overdue as its own line;
    /// maintenance the customer pays for is income (707) while maintenance on the company's own
    /// assets is an expense (615) — the same work, decided by who pays, at the moment of
    /// return. Damage and loss are the same event from the company's side: an asset that is not
    /// coming back, compensated at the price frozen on the contract.
    /// </para>
    /// </summary>
    public static string AccountFor(RentalChargeKind kind) => kind switch
    {
        RentalChargeKind.Rent => LedgerAccounts.RentalIncome,          // 706
        RentalChargeKind.Overdue => LedgerAccounts.LateIncome,         // 708
        RentalChargeKind.Maintenance => LedgerAccounts.MaintenanceIncome, // 707
        RentalChargeKind.Damage => LedgerAccounts.DamageCompensation,  // 757
        RentalChargeKind.Missing => LedgerAccounts.DamageCompensation, // 757
        _ => LedgerAccounts.Suspense
    };

    /// <summary>The same mapping for the engine's own enum, so no call site converts by hand.</summary>
    public static string AccountFor(Engine.RentalChargeKind kind) => AccountFor((RentalChargeKind)kind);

    /// <summary>
    /// Goods going out: the customer is invoiced the full agreed period on everything issued.
    ///
    /// <para>
    /// Against the customer's account rather than cash, deliberately. A contract is raised in
    /// the back office for a contractor on account; nothing has been collected at the moment
    /// the lorry leaves, and debiting cash here would move a figure no branch drawer ever saw
    /// — the same reasoning that keeps <see cref="StockPosting.Receiving"/> off the cash
    /// account.
    /// </para>
    /// <para>
    /// ⚠ <b>No stock entry belongs here.</b> Available → on rent is the same merchandise in a
    /// different place: the company still owns it and it is still worth what it was worth
    /// yesterday. The quantity buckets carry the distinction, and
    /// <see cref="StockPosting.StateChange"/> returns null for it.
    /// </para>
    /// </summary>
    /// <returns>Null when the note carries no money at all — every line an accessory, which rents at zero.</returns>
    /// <param name="periodStartsUtc">
    /// The first day of the note's rental period, at the start of that day on the company's
    /// clock. The whole period's rent is the company's income on that day (owner, 2026-10-31,
    /// REN-02) — not on the day someone types the note. A note keyed on 3 November for goods
    /// that went out on 28 October is October's rent.
    /// </param>
    /// <param name="tax">
    /// Tax on <paramref name="periodRent"/>, at the rate frozen on the contract lines. Zero
    /// when the company has set no rental tax rate, which is the default — whether hire is
    /// taxed is a question for the business and its jurisdiction, not something to infer.
    /// </param>
    public static PostingDraft? Issue(
        Guid noteId, Guid branchId, Guid? customerId, string reference, decimal periodRent,
        string? by, DateTime periodStartsUtc, decimal tax = 0)
    {
        if (periodRent == 0 && tax == 0) return null;

        return new PostingDraft
        {
            EntryDate = periodStartsUtc,
            BranchId = branchId,
            SourceType = LedgerSources.RentalIssue,
            SourceId = noteId,
            Description = $"Rental issue {reference}",
            CreatedBy = by
        }
            .AddIfAny(PostingLine.Signed(
                // With no customer on the contract there is nobody to bill, and an invoice
                // against nobody is a fault rather than a receivable — so it lands in suspense,
                // where the trial balance shows it as something to investigate.
                // The customer is billed the whole invoice; only the net is income.
                customerId is null ? LedgerAccounts.Suspense : LedgerAccounts.Customers, periodRent + tax,
                memo: reference, customerId: customerId))
            .AddIfAny(PostingLine.Signed(LedgerAccounts.RentalIncome, -periodRent, memo: reference))
            .AddIfAny(PostingLine.Signed(LedgerAccounts.TaxCollected, -tax, memo: reference));
    }

    /// <summary>
    /// Goods coming back, and what the condition they came back in costs.
    ///
    /// <para>
    /// <paramref name="charges"/> must exclude the rent already invoiced at issue and include
    /// everything else: overdue days, extension days (which are charged as they are consumed,
    /// not in advance), repairs recharged, and compensation for what will not come back.
    /// </para>
    /// <para>
    /// <paramref name="unusedRent"/> is the part of the advance invoice the customer never
    /// used. It is a reversal of revenue against the customer's account — not an expense, and
    /// not a discount: the money was recognised as rent when the goods went out, so it has to
    /// come off the same line it went on to. Whether any of it then leaves as cash is a
    /// separate act with its own document; until someone pays it the customer simply owes less
    /// (or, past zero, is owed).
    /// </para>
    /// </summary>
    /// <returns>Null when nothing was charged and nothing refunded — a plain, on-time return of sound goods.</returns>
    /// <param name="tax">
    /// Net tax on this note: on everything charged, less the tax on the part of the advance
    /// invoice being given back. It can be negative on a large early return, which is correct
    /// — refunding an invoice refunds the tax with it.
    /// </param>
    public static PostingDraft? Return(
        Guid noteId, Guid branchId, Guid? customerId, string reference,
        IEnumerable<ChargeAmount> charges, decimal unusedRent, string? by, decimal tax = 0)
    {
        var account = customerId is null ? LedgerAccounts.Suspense : LedgerAccounts.Customers;
        var draft = new PostingDraft
        {
            EntryDate = DateTime.UtcNow,
            BranchId = branchId,
            SourceType = LedgerSources.RentalReturn,
            SourceId = noteId,
            Description = $"Rental return {reference}",
            CreatedBy = by
        };

        var owed = 0m;
        foreach (var charge in charges)
        {
            if (charge.Amount == 0) continue;
            owed += charge.Amount;
            draft.AddIfAny(PostingLine.Signed(AccountFor(charge.Kind), -charge.Amount, memo: charge.Memo));
        }

        var relief = Math.Max(0m, unusedRent);
        // Revenue given back, on the line it was recognised on. Netting it against the charges
        // instead would understate both the rent earned and the overdue billed, and the two
        // are separate lines precisely because the owner wants to see them apart.
        draft.AddIfAny(PostingLine.Signed(LedgerAccounts.RentalIncome, relief, memo: reference));
        draft.AddIfAny(PostingLine.Signed(LedgerAccounts.TaxCollected, -tax, memo: reference));
        draft.AddIfAny(PostingLine.Signed(account, owed - relief + tax, memo: reference, customerId: customerId));

        return draft.Lines.Count == 0 ? null : draft;
    }
}
