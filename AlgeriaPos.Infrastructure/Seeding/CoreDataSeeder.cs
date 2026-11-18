using Microsoft.EntityFrameworkCore;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Persistence;

namespace AlgeriaPos.Infrastructure.Seeding;

/// <summary>
/// Rows the system cannot function without, created on every startup rather than only on a
/// fresh install.
/// <para>
/// <see cref="DbSeeder"/> returns early once a company has users, which is correct for demo
/// data — nobody wants their catalog re-seeded. But the built-in tenders and the per-branch
/// cash boxes are not demo data: an existing company upgrading into this release has neither,
/// and a till with no payment methods cannot take money. So this runs unconditionally and is
/// written to be idempotent.
/// </para>
/// </summary>
public static class CoreDataSeeder
{
    public static async Task EnsureAsync(PosDbContext db, CancellationToken ct = default)
    {
        await EnsurePaymentMethodsAsync(db, ct);
        await EnsureCashBoxesAsync(db, ct);
        await EnsureLedgerAccountsAsync(db, ct);
    }

    /// <summary>
    /// The default chart of accounts. Missing codes are added; anything already there is left
    /// exactly as it is, for the same reason the tenders are — an accountant who renamed 530
    /// to match the firm's own naming meant it, and a startup that quietly undid that would be
    /// worse than the gap it filled.
    /// <para>
    /// This is the seed, not a fence: the admin extends the chart from the back office, and a
    /// module only ever posts to the system codes above.
    /// </para>
    /// </summary>
    private static async Task EnsureLedgerAccountsAsync(PosDbContext db, CancellationToken ct)
    {
        var existing = await db.LedgerAccounts.Select(a => a.Code).ToListAsync(ct);
        var missing = LedgerAccounts.Default
            .Where(a => !existing.Contains(a.Code, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (missing.Count == 0) return;

        // Fresh instances: LedgerAccounts.Default is a static list and attaching its elements
        // to a context would hand every company the same tracked objects.
        foreach (var account in missing)
            db.LedgerAccounts.Add(new LedgerAccount
            {
                Code = account.Code,
                NameAr = account.NameAr,
                NameFr = account.NameFr,
                NameEn = account.NameEn,
                Type = account.Type,
                IsSystem = true,
                IsActive = true,
                SortOrder = account.SortOrder
            });

        // No version stamping: the ledger is server-only and never reaches a till (§5.9).
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The five tenders whose behaviour is programmed. Missing ones are added; existing ones
    /// are left exactly as they are, because the operator may well have renamed or disabled
    /// them deliberately and a "repair" that undoes that is worse than the gap it fills.
    /// </summary>
    private static async Task EnsurePaymentMethodsAsync(PosDbContext db, CancellationToken ct)
    {
        var existing = await db.PaymentMethodDefs.Select(x => x.Key).ToListAsync(ct);
        var missing = BuiltIns.Where(d => !existing.Contains(d.Key, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count == 0) return;

        foreach (var def in missing) db.PaymentMethodDefs.Add(def);
        await db.SaveWithVersionsAsync(ct, missing.Cast<SyncEntity>().ToArray());
    }

    /// <summary>
    /// One cash box per selling branch. The main branch's box is the central safe that branch
    /// remittances land in. A warehouse gets none: it sells nothing and handles no money (owner,
    /// 2026-10-05: "warehouse hasn't cashier").
    /// <para>
    /// Also called when a branch is created (BranchEndpoints): run only at startup, a branch added
    /// in the back office had no box until the next restart, and its cash sales moved no drawer
    /// (SyncService skips the movement when the branch has no box).
    /// </para>
    /// </summary>
    public static async Task EnsureCashBoxesAsync(PosDbContext db, CancellationToken ct)
    {
        var branches = await db.Branches.Where(b => !b.IsDeleted && !b.IsWarehouse).ToListAsync(ct);
        if (branches.Count == 0) return;

        var covered = await db.CashBoxes.Select(x => x.BranchId).ToListAsync(ct);
        var added = false;
        foreach (var branch in branches.Where(b => !covered.Contains(b.Id)))
        {
            db.CashBoxes.Add(new CashBox
            {
                BranchId = branch.Id,
                NameAr = branch.IsMain ? "الخزينة المركزية" : $"صندوق {branch.NameAr}",
                NameFr = branch.IsMain ? "Caisse centrale" : $"Caisse {branch.NameFr}",
                NameEn = branch.IsMain ? "Central safe" : $"{branch.NameEn} cash box",
                IsMain = branch.IsMain
            });
            added = true;
        }
        if (added) await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Cash is the only tender confirmed on the spot and the only one that reaches the
    /// drawer. Card is confirmed too: the terminal's approval number is the confirmation, so
    /// making the cashier confirm it again would be theatre — but it does not touch the cash
    /// drawer, or the Z-report would show a surplus on every card sale.
    /// </summary>
    private static IEnumerable<PaymentMethodDef> BuiltIns =>
    [
        new()
        {
            Key = "cash", NameAr = "نقدًا", NameFr = "Espèces", NameEn = "Cash",
            Behavior = PaymentBehavior.Cash, RequiresConfirmation = false,
            AffectsCashDrawer = true, IsBuiltIn = true, SortOrder = 1
        },
        new()
        {
            Key = "card", NameAr = "بطاقة", NameFr = "Carte", NameEn = "Card",
            Behavior = PaymentBehavior.Card, RequiresConfirmation = false,
            AffectsCashDrawer = false, IsBuiltIn = true, SortOrder = 2
        },
        new()
        {
            Key = "credit", NameAr = "على الحساب", NameFr = "À crédit", NameEn = "On account",
            Behavior = PaymentBehavior.Credit, RequiresConfirmation = false,
            AffectsCashDrawer = false, IsBuiltIn = true, SortOrder = 3
        },
        new()
        {
            Key = "giftcard", NameAr = "بطاقة هدايا", NameFr = "Carte cadeau", NameEn = "Gift card",
            Behavior = PaymentBehavior.GiftCard, RequiresConfirmation = false,
            AffectsCashDrawer = false, RequiresReference = true, IsBuiltIn = true, SortOrder = 4
        },
        new()
        {
            Key = "loyalty", NameAr = "نقاط الولاء", NameFr = "Points de fidélité", NameEn = "Loyalty points",
            Behavior = PaymentBehavior.Loyalty, RequiresConfirmation = false,
            AffectsCashDrawer = false, IsBuiltIn = true, SortOrder = 5
        }
    ];
}
