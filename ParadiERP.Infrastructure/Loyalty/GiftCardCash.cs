using ParadiERP.Domain;
using ParadiERP.Infrastructure.Ledger;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Loyalty;

/// <summary>
/// The money side of a gift card (owner, 2026-10-31 and 2026-11-01, SAL-17). A card is sold for cash at a
/// branch: the cash goes into that branch's box and is owed to the customer (4181) until it is spent, when the
/// till's sale already takes it off 4181 and books the income. A top-up is the same as a sale of the card; a
/// cash refund of part of the balance is the reverse. A correction moves no money and books nothing.
///
/// <para>
/// No VAT is booked here: the sale the card pays for carries it, as before. Whether the law wants it when the
/// card is sold, and what happens to an expired balance, are the accountant's (SAL-17); nothing here expires a
/// card into income.
/// </para>
/// </summary>
public static class GiftCardCash
{
    /// <summary>The kinds of hand adjustment (<c>AdjustGiftCardRequest.Kind</c>).</summary>
    public const string TopUp = "topUp";
    public const string CashOut = "cashOut";
    public const string Correction = "correction";

    /// <summary>The <see cref="GiftCardMovement.Reason"/> each kind writes; a correction keeps "adjustment".</summary>
    public static string ReasonFor(string kind) => kind switch
    {
        TopUp => "topup",
        CashOut => "cashout",
        _ => "adjustment"
    };

    /// <summary>
    /// The entry for cash that changed hands over a card, at <paramref name="branchId"/>, now. Positive
    /// <paramref name="amount"/> is cash taken (Dr cash, Cr the card liability); negative is cash handed back.
    /// </summary>
    public static PostingDraft Draft(Guid movementId, Guid branchId, string code, decimal amount, string? by, DateTime atUtc)
    {
        var taken = amount > 0;
        return new PostingDraft
        {
            EntryDate = atUtc,
            BranchId = branchId,
            SourceType = taken ? LedgerSources.GiftCardSale : LedgerSources.GiftCardCashOut,
            SourceId = movementId,
            Description = (taken ? "Gift card sold " : "Gift card cash refund ") + code,
            CreatedBy = by
        }
            .AddIfAny(PostingLine.Signed(LedgerAccounts.Cash, amount, memo: code))
            .AddIfAny(PostingLine.Signed(LedgerAccounts.GiftCardLiability, -amount, memo: code));
    }

    /// <summary>
    /// Books <paramref name="movement"/>'s cash at <paramref name="branchId"/> and moves that branch's box —
    /// in for a sale or a top-up, out for a cash refund. Call inside the write scope that saves the movement.
    /// A branch with no open box still gets its entry; the box simply has nothing to move.
    /// </summary>
    public static async Task RecordAsync(
        PosDbContext db, LedgerPoster ledger, GiftCard card, GiftCardMovement movement, Guid branchId, string? by,
        CancellationToken ct)
    {
        var at = DateTime.UtcNow;
        await ledger.PostAsync(Draft(movement.Id, branchId, card.Code, movement.Delta, by, at), ct);

        var box = await db.CashBoxes.FirstOrDefaultAsync(x => x.BranchId == branchId && x.IsActive, ct);
        if (box is null) return;
        var balance = await CashBoxBalance.ForWriteAsync(db, box.Id, ct);
        var taken = movement.Delta > 0;
        db.CashMovements.Add(new CashMovement
        {
            CashBoxId = box.Id,
            Direction = taken ? CashDirection.In : CashDirection.Out,
            Amount = Math.Abs(movement.Delta),
            BalanceAfter = balance + movement.Delta,
            Kind = taken ? CashMovementKind.Receipt : CashMovementKind.Refund,
            SourceType = taken ? LedgerSources.GiftCardSale : LedgerSources.GiftCardCashOut,
            SourceId = movement.Id,
            UserName = by,
            Reference = card.Code,
            OccurredAtUtc = at
        });
    }

    /// <summary>The branch a card's cash belongs to: the one asked for, else the main branch (never a warehouse).</summary>
    public static async Task<Guid?> BranchAsync(PosDbContext db, Guid? asked, CancellationToken ct) =>
        asked is Guid id && id != Guid.Empty
            ? id
            : await db.Branches.AsNoTracking()
                .Where(b => !b.IsDeleted && b.IsMain && !b.IsWarehouse)
                .Select(b => (Guid?)b.Id).FirstOrDefaultAsync(ct);
}
