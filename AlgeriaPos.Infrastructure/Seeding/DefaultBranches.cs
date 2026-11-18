using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Auth;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Infrastructure.Seeding;

/// <summary>
/// The two places every company has (owner, 2026-10-05): exactly one main branch — the head office,
/// a selling branch whose cash box is the central safe and where the first till goes — and exactly
/// one main warehouse, which holds the stock, takes supplier deliveries and never sells. Other
/// branches and warehouses are ordinary. Run at every start, after the migrations and the company
/// seeding; idempotent, so a start that finds both writes nothing.
/// <para>
/// What it repairs is the shape every company had before the two kinds were told apart: one
/// branch, the warehouse, carrying the tills and the central safe. The CompanyBranchKinds migration
/// already made that branch the main warehouse; this flags or creates the main branch beside it and
/// moves onto it what a warehouse cannot hold — tills, cashiers and money. Stock, products and
/// documents are never touched: the stock stays in the warehouse it was counted in.
/// </para>
/// <para>
/// History-safe: this step never changes a location's kind — only the migration does, and only for
/// a location with no selling, money or staff history (a location with such history stays a branch,
/// so its sales and documents keep their branch meaning, and the warehouse is created beside it).
/// It never re-points a historical row: it moves tills (configuration), cashiers' user rows, and a
/// cash box only when that box has never moved money. Sales, documents, ledger lines, stock rows and
/// stock movements keep the location they were written at.
/// </para>
/// </summary>
public static class DefaultBranches
{
    // The names a place gets when this step creates it. Words, not country data: the same in every
    // company, like the central safe's name in CoreDataSeeder.
    public const string MainNameAr = "الفرع الرئيسي";
    public const string MainNameFr = "Agence principale";
    public const string MainNameEn = "Main branch";
    public const string WarehouseNameAr = "المستودع الرئيسي";
    public const string WarehouseNameFr = "Entrepôt central";
    public const string WarehouseNameEn = "Central warehouse";

    // What CompanySeeder wrote on the one branch it created until 2026-10-05: these two names and no
    // Arabic one. A warehouse still carrying one of them is that row, and each of its three names is
    // replaced on its own — only while it is still the seeder's value or empty. A name someone typed
    // (the live row's Arabic «المستودع الرئيسي», for one) is kept.
    private const string SeededFr = "Principal";
    private const string SeededEn = "Main";

    public sealed record Result(
        string MainBranch, string MainWarehouse, bool RenamedWarehouse,
        int TillsMoved, int CashiersMoved, int CashBoxesMoved, int CashBoxesRetired, int CashBoxesKept)
    {
        /// <summary>"kept", "flagged" (an existing one of the right kind was made main) or "created".</summary>
        public bool ChangedAnything =>
            MainBranch != Kept || MainWarehouse != Kept || RenamedWarehouse
            || TillsMoved + CashiersMoved + CashBoxesMoved + CashBoxesRetired + CashBoxesKept > 0;
    }

    public const string Kept = "kept";
    public const string Flagged = "flagged";
    public const string Created = "created";

    public static Branch NewMainBranch() => new()
    {
        NameAr = MainNameAr, NameFr = MainNameFr, NameEn = MainNameEn, IsMain = true, IsActive = true,
    };

    public static Branch NewMainWarehouse() => new()
    {
        NameAr = WarehouseNameAr, NameFr = WarehouseNameFr, NameEn = WarehouseNameEn,
        IsWarehouse = true, IsMain = true, IsActive = true,
    };

    public static async Task<Result> EnsureAsync(PosDbContext db, CancellationToken ct = default)
    {
        var branches = await db.Branches.Where(b => !b.IsDeleted).ToListAsync(ct);
        // A company with nothing yet is CompanySeeder's to create, with its admin and its first till.
        if (branches.Count == 0) return new Result(Kept, Kept, false, 0, 0, 0, 0, 0);

        var changed = new List<SyncEntity>();
        void Mark(SyncEntity e) { if (!changed.Contains(e)) changed.Add(e); }

        var (mainWarehouse, warehouseState) = EnsureMainOfKind(db, branches, warehouse: true, NewMainWarehouse, Mark);
        var (mainBranch, branchState) = EnsureMainOfKind(db, branches, warehouse: false, NewMainBranch, Mark);

        var renamed = false;
        foreach (var w in branches.Where(b => b.IsWarehouse && (b.NameFr == SeededFr || b.NameEn == SeededEn)))
        {
            static bool Untouched(string value, string seeded) => value.Length == 0 || value == seeded;
            if (Untouched(w.NameFr, SeededFr)) w.NameFr = WarehouseNameFr;
            if (Untouched(w.NameEn, SeededEn)) w.NameEn = WarehouseNameEn;
            if (w.NameAr.Length == 0) w.NameAr = WarehouseNameAr;
            Mark(w);
            renamed = true;
        }

        var warehouseIds = branches.Where(b => b.IsWarehouse).Select(b => b.Id).ToHashSet();
        warehouseIds.Add(mainWarehouse.Id);

        // Tills cannot be on a warehouse: it does not sell. Each moves to the main branch with its
        // code, its receipt numbering and its cashier (Devices.UserId), so it keeps selling.
        var tills = await db.Devices.Where(d => warehouseIds.Contains(d.BranchId)).ToListAsync(ct);
        foreach (var till in tills) till.BranchId = mainBranch.Id;

        // Nor cashiers: a till-scoped user sells. Managers and admins placed on a warehouse for stock
        // work stay where they are.
        var cashiersMoved = 0;
        var onWarehouse = await db.Users
            .Where(u => !u.IsDeleted && u.BranchId != null && warehouseIds.Contains(u.BranchId.Value)).ToListAsync(ct);
        foreach (var user in onWarehouse)
        {
            if (await CashierTills.ScopeOfAsync(db, user.RoleId, ct) != RoleScope.TillBranch) continue;
            user.BranchId = mainBranch.Id;
            Mark(user);
            cashiersMoved++;
        }

        // Nor money ("warehouse hasn't cashier"). A warehouse's cash box that never moved becomes the
        // main branch's — the central safe — when the main branch has none; a further one is
        // switched off. One that holds movements keeps them and stays open, so its balance can still
        // be remitted out, but it is no longer the central safe: the main branch's box is.
        var boxes = await db.CashBoxes.ToListAsync(ct);
        var mainHasBox = boxes.Any(b => b.BranchId == mainBranch.Id);
        int moved = 0, retired = 0, kept = 0;
        foreach (var box in boxes.Where(b => warehouseIds.Contains(b.BranchId)))
        {
            if (await db.CashMovements.AnyAsync(m => m.CashBoxId == box.Id, ct))
            {
                if (box.IsMain) { box.IsMain = false; kept++; }
                continue;
            }
            if (!mainHasBox)
            {
                box.BranchId = mainBranch.Id;
                box.IsActive = true;
                mainHasBox = true;
                moved++;
            }
            else if (box.IsActive || box.IsMain)
            {
                box.IsActive = false;
                box.IsMain = false;
                retired++;
            }
        }
        AlignCentralSafe(boxes, mainBranch.Id);

        if (changed.Count > 0) await db.SaveWithVersionsAsync(ct, changed.ToArray());
        else await db.SaveChangesAsync(ct);

        return new Result(branchState, warehouseState, renamed, tills.Count, cashiersMoved, moved, retired, kept);
    }

    /// <summary>
    /// The main one of a kind: kept when exactly one carries the flag; when several do (rows written
    /// by hand) the first by name keeps it; when none does, the first of that kind by name is
    /// flagged — an existing place is preferred over a new one — and only a kind with no place at
    /// all gets a new one.
    /// </summary>
    private static (Branch Main, string State) EnsureMainOfKind(
        PosDbContext db, List<Branch> branches, bool warehouse, Func<Branch> create, Action<SyncEntity> mark)
    {
        var ofKind = branches.Where(b => b.IsWarehouse == warehouse).OrderBy(b => b.NameFr).ThenBy(b => b.Id).ToList();
        var mains = ofKind.Where(b => b.IsMain).ToList();
        if (mains.Count > 0)
        {
            foreach (var extra in mains.Skip(1)) { extra.IsMain = false; mark(extra); }
            return (mains[0], Kept);
        }
        var pick = ofKind.FirstOrDefault(b => b.IsActive) ?? ofKind.FirstOrDefault();
        if (pick is not null)
        {
            pick.IsMain = true;
            mark(pick);
            return (pick, Flagged);
        }
        // Its three-digit code comes with the save (BranchCodeAssignment): a random free one.
        var made = create();
        db.Branches.Add(made);
        branches.Add(made);
        mark(made);
        return (made, Created);
    }

    /// <summary>
    /// The central safe is the main branch's cash box and no other: called here and whenever the
    /// main branch changes hands (BranchEndpoints). A box's movements never move with the flag.
    /// </summary>
    public static void AlignCentralSafe(IEnumerable<CashBox> boxes, Guid mainBranchId)
    {
        foreach (var box in boxes)
            box.IsMain = box.BranchId == mainBranchId && box.IsActive;
    }
}
