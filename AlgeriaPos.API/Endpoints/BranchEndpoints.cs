using Microsoft.EntityFrameworkCore;
using AlgeriaPos.Application.Catalog;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Files;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Auth;
using AlgeriaPos.Infrastructure.Catalog;
using AlgeriaPos.Infrastructure.Files;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Seeding;
using AlgeriaPos.API.Subscriptions;

namespace AlgeriaPos.API.Endpoints;

/// <summary>
/// Back-office branch management. A branch is a physical location — the main warehouse or a
/// selling store — that every till and every stock figure is scoped to (see ProductStock).
/// </summary>
public static class BranchEndpoints
{
    public static void MapBranchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/catalog/branches").WithTags("Branches")
            .AddEndpointFilter<SubscriptionWriteFilter>();
        // The reads a voucher form needs (answer 25): a cash-box holder picks a customer, a branch
        // and a payment method like a manager does, and edits none of them.
        var cash = app.MapGroup("/api/catalog/branches").WithTags("Branches")
            .AddEndpointFilter<SubscriptionWriteFilter>();

        cash.MapGet("/", async (PosDbContext db, CancellationToken ct) =>
            Results.Ok((await db.Branches.AsNoTracking().Where(b => !b.IsDeleted)
                .OrderByDescending(b => b.IsMain).ThenBy(b => b.IsWarehouse).ThenBy(b => b.NameFr).ToListAsync(ct)).Select(BranchResponse.Of)));
        PeoplePageRoutes.MapBranchesPage(cash);

        // A branch's logo itself, for the top bar of every screen: any signed-in user, because a
        // till shows its own branch's logo or the main branch's. Never public (owner, 2026-10-04).
        cash.MapGet("/{id:guid}/logo", async (Guid id, PosDbContext db, IFileStore files, HttpContext http, CancellationToken ct) =>
        {
            var path = await db.Branches.AsNoTracking().Where(b => b.Id == id && !b.IsDeleted).Select(b => b.LogoPath).FirstOrDefaultAsync(ct);
            return await FileResults.LogoAsync(files, path, http, ct);
        });

        group.MapPost("/", async (UpsertBranchRequest r, HttpContext http, PosDbContext db, IFileStore files, IRegionalSettingsProvider regional, CancellationToken ct) =>
        {
            // A new branch is the company's decision (security review, 2026-10-10): branches.manage
            // lets a branch's manager keep their own branch's details, not open new ones.
            if (!BranchAssignment.IsCompanyScoped(http.User))
                return Kind(CompanyOnlyCode, CompanyOnlyMessage, StatusCodes.Status403Forbidden);
            // The location's three-digit code is drawn here, before anything is written (owner,
            // 2026-10-05; three digits since 2026-10-19): the 901st location is refused in words, not
            // saved with a code that does not fit. The filter's SyncCounters lock is held, so two creates draw one after the other.
            if (await BranchCodeAssignment.NextAsync(db, ct) is not string code)
                return Kind(BranchCodes.ExhaustedCode, BranchCodes.ExhaustedMessage);
            if (await NameTakenAsync(db, r, null, ct)) return Kind(NameTakenCode, NameTakenMessage);
            var entity = new Branch { Code = code };
            if (ApplyFiscalIds(entity, r, regional.Get()) is string problem) return Results.BadRequest(problem);
            if (await ApplyLogoAsync(entity, r, http, files, ct) is string logoProblem) return Results.BadRequest(logoProblem);
            Apply(entity, r);
            db.Branches.Add(entity);
            // "Set as primary" on a new one hands the flag over from the current primary of its kind
            // in one transaction: there is always exactly one of each.
            await using (var tx = await db.BeginWriteScopeAsync(ct))
            {
                await SaveTakingPrimaryAsync(db, entity, r.IsMain, ct);
                await tx.CommitAsync(ct);
            }
            // The branch's cash box comes with it (owner, 2026-10-05: "the cash box belongs to the
            // branch"), not at the next restart. A warehouse gets none.
            await CoreDataSeeder.EnsureCashBoxesAsync(db, ct);
            if (entity.IsMain && !entity.IsWarehouse) await AlignCentralSafeAsync(db, entity.Id, ct);
            return Results.Created($"/api/catalog/branches/{entity.Id}", BranchResponse.Of(entity));
        });

        group.MapPut("/{id:guid}", async (Guid id, UpsertBranchRequest r, HttpContext http, PosDbContext db, IFileStore files, IRegionalSettingsProvider regional, CancellationToken ct) =>
        {
            var entity = await db.Branches.FirstOrDefaultAsync(b => b.Id == id, ct);
            if (entity is null) return Results.NotFound();
            // A branch's manager edits their own branch only, and only its details (security review,
            // 2026-10-10): taking the "main" flag moved the central safe to their cash box, and
            // another branch could be switched off or given other fiscal identifiers. What is the
            // company's to decide is kept as stored, the way the logo already was.
            if (!BranchAssignment.IsCompanyScoped(http.User))
            {
                if (await BranchAssignment.ForCallerAsync(http.User, db, ct) != id) return Results.NotFound();
                r = r with { IsMain = entity.IsMain, IsWarehouse = entity.IsWarehouse, IsActive = entity.IsActive, FiscalIds = null };
            }
            if (await NameTakenAsync(db, r, id, ct)) return Kind(NameTakenCode, NameTakenMessage);
            var kindChanges = r.IsWarehouse != entity.IsWarehouse;

            // The main one of a kind stays main, and stays of its kind, until another takes the flag:
            // unticking it, or turning it into the other kind, would leave the company without one.
            if (entity.IsMain && (!r.IsMain || kindChanges))
                return entity.IsWarehouse
                    ? Kind(MainWarehouseRequiredCode, MainWarehouseRequiredMessage)
                    : Kind(MainBranchRequiredCode, MainBranchRequiredMessage);

            CashBox? retiring = null;
            if (kindChanges && r.IsWarehouse)
            {
                // A branch becoming a warehouse hands over first what a warehouse cannot hold: its
                // tills, its cashiers and any money. Refused in words, never silently moved — the
                // administrator decides where they go. A cash box that never moved is switched off.
                if (await db.Devices.AnyAsync(d => d.BranchId == id, ct) || await HasCashiersAsync(db, id, ct))
                    return Kind(WarehouseHasTillsCode, WarehouseHasTillsMessage);
                foreach (var box in await db.CashBoxes.Where(b => b.BranchId == id).ToListAsync(ct))
                {
                    if (await db.CashMovements.AnyAsync(m => m.CashBoxId == box.Id, ct))
                        return Kind(WarehouseHasCashCode, WarehouseHasCashMessage);
                    retiring = box;
                }
            }
            else if (kindChanges)
            {
                // A warehouse becoming a selling branch must be empty: its stock was counted as stock
                // held for the branches, and a till would start selling it as shelf stock. Transfer it
                // to another warehouse first.
                if (await HoldsStockAsync(db, id, ct)) return Kind(WarehouseHasStockCode, WarehouseHasStockMessage);
            }

            if (ApplyFiscalIds(entity, r, regional.Get()) is string problem) return Results.BadRequest(problem);
            var previousLogo = entity.LogoPath;
            if (await ApplyLogoAsync(entity, r, http, files, ct) is string logoProblem) return Results.BadRequest(logoProblem);
            var wasPrimary = entity.IsMain;
            Apply(entity, r);
            if (retiring is not null) { retiring.IsActive = false; retiring.IsMain = false; }
            await using (var tx = await db.BeginWriteScopeAsync(ct))
            {
                // The row is updated in place — same id, stock, documents and references.
                await SaveTakingPrimaryAsync(db, entity, r.IsMain && !wasPrimary, ct);
                await tx.CommitAsync(ct);
            }
            if (previousLogo != entity.LogoPath) await LogoFiles.ReleaseAsync(db, files, previousLogo, ct);
            if (!entity.IsWarehouse)
            {
                // A warehouse that became a selling branch gets a cash box like any other.
                var own = await db.CashBoxes.Where(b => b.BranchId == id).ToListAsync(ct);
                if (own.Count > 0 && own.All(b => !b.IsActive)) { own[0].IsActive = true; await db.SaveChangesAsync(ct); }
                await CoreDataSeeder.EnsureCashBoxesAsync(db, ct);
            }
            // The central safe follows the main branch when the flag changes hands.
            var mainBranch = await db.Branches.AsNoTracking().Where(b => b.IsMain && !b.IsWarehouse && !b.IsDeleted).Select(b => (Guid?)b.Id).FirstOrDefaultAsync(ct);
            if (mainBranch is Guid m) await AlignCentralSafeAsync(db, m, ct);
            return Results.Ok(BranchResponse.Of(entity));
        });

        // A branch is never deleted (owner, 2026-10-06). A deleted branch kept what it held out of
        // reach: on production the stock of 121 products sat on one, and every transfer out of it
        // was refused as "Unknown branch.". A branch no longer used is deactivated instead — the
        // edit's "active" box — and keeps its stock, documents and history. The route stays so a
        // back office still showing the old Delete button is told why in words, not with a 404.
        group.MapDelete("/{id:guid}", async (Guid id, PosDbContext db, CancellationToken ct) =>
            await db.Branches.AnyAsync(b => b.Id == id, ct)
                ? Kind(NotDeletableCode, NotDeletableMessage)
                : Results.NotFound());
    }

    /// <summary>
    /// The branch's own commercial register, NIF, NIS and article number (owner, 2026-09-30: every
    /// branch has its own and prints them on its documents). Read exactly as the company's are —
    /// the profile's keys only, trimmed, blanks dropped — but nothing is required: a blank field
    /// prints the company's value instead. Null leaves what is stored untouched, so a client that
    /// predates the field cannot wipe them by omission. A new dictionary is assigned, never the
    /// stored one edited, so the column's comparer sees the change.
    /// </summary>
    private static string? ApplyFiscalIds(Branch entity, UpsertBranchRequest r, RegionalSettings profile)
    {
        if (r.FiscalIds is null) return null;
        if (FiscalIdValues.TryNormalize(profile, r.FiscalIds, entity.FiscalIds, out var clean) is string problem)
            return problem;
        entity.FiscalIds = clean;
        return null;
    }

    /// <summary>
    /// The branch's logo, read like the company's (StoreLogo) but capped at
    /// <see cref="StoreLogo.BranchMaxBytes"/>: every till keeps a copy. The link it was given keeps
    /// it, a data URL replaces it, null or empty removes it. Checked before anything on the row is
    /// touched, so a refused logo changes nothing; a new picture is written to the files folder
    /// before the row names it.
    /// <para>
    /// <b>The Admin's alone</b> (the owner, 2026-09-30: "the branch logo is edited by the super
    /// admin"). For anyone else the field is ignored and the stored logo stays as it is — the same
    /// reading as a branch a non-admin names on a document — so a manager saving his branch's
    /// address never touches it.
    /// </para>
    /// </summary>
    private static async Task<string?> ApplyLogoAsync(Branch entity, UpsertBranchRequest r, HttpContext http, IFileStore files, CancellationToken ct)
    {
        if (!BranchAssignment.IsCompanyScoped(http.User)) return null;
        if (StoreLogo.TryReadChange(r.LogoUrl, StoreLogo.BranchEndpoint(entity.Id), StoreLogo.BranchMaxBytes, out var logo) is string problem)
            return problem;
        if (logo.Kind == StoreLogo.ChangeKind.Keep) return null;
        if (logo.File is not null) await files.WriteAsync(logo.File.Path, logo.File.Content, ct);
        entity.LogoPath = logo.File?.Path;
        return null;
    }

    // ---------- the main branch and the warehouses (owner, 2026-10-05) ----------
    // Each refusal carries a stable code the branches screen turns into a sentence in the reader's
    // language. Configuration rules only: none is ever checked at a sale.

    public const string MainBranchRequiredCode = "main_branch_required";
    public const string MainBranchRequiredMessage = "This is the main branch, and the company always has one: tick \"main\" on another branch first.";
    public const string MainWarehouseRequiredCode = "main_warehouse_required";
    public const string MainWarehouseRequiredMessage = "This is the main warehouse, and the company always has one: tick \"main\" on another warehouse first.";
    public const string NameTakenCode = "branch_name_taken";
    public const string NameTakenMessage = "Another branch already has this name. Give each branch its own name in every language.";
    public const string NotDeletableCode = "branch_not_deletable";
    public const string NotDeletableMessage = "A branch is never deleted: its stock, documents and history stay with it. Untick \"active\" to stop using it.";
    public const string WarehouseHasTillsCode = "warehouse_has_tills";
    public const string WarehouseHasTillsMessage = "A warehouse does not sell: move this branch's tills and cashiers to a selling branch first.";
    public const string WarehouseHasCashCode = "warehouse_has_cash";
    public const string WarehouseHasCashMessage = "A warehouse handles no money, and this branch's cash box has movements: it stays a selling branch.";
    public const string WarehouseHasStockCode = "warehouse_has_stock";
    public const string WarehouseHasStockMessage = "This warehouse still holds stock: transfer it to another warehouse before making it a selling branch.";
    /// <summary>A branch is opened by a company-wide role only (security review, 2026-10-10).</summary>
    public const string CompanyOnlyCode = "branch_company_only";
    public const string CompanyOnlyMessage = "Only an administrator of the whole company opens a branch.";

    private static IResult Kind(string code, string message, int status = StatusCodes.Status409Conflict) =>
        Results.Problem(message, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });

    /// <summary>
    /// Whether another location already carries this name in the same language (owner, 2026-10-06):
    /// a list shows one language at a time, and two "المستودع الرئيسي" in the Arabic one made a
    /// transfer pick the wrong place. Compared trimmed and without case, a blank name never
    /// clashes, and a deleted location's name is free again.
    /// </summary>
    private static async Task<bool> NameTakenAsync(PosDbContext db, UpsertBranchRequest r, Guid? self, CancellationToken ct)
    {
        var others = await db.Branches.AsNoTracking()
            .Where(b => !b.IsDeleted && (self == null || b.Id != self))
            .Select(b => new { b.NameAr, b.NameFr, b.NameEn })
            .ToListAsync(ct);
        static bool Same(string? a, string? b)
        {
            var x = (a ?? string.Empty).Trim();
            return x.Length > 0 && string.Equals(x, (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        }
        return others.Any(o => Same(r.NameAr, o.NameAr) || Same(r.NameFr, o.NameFr) || Same(r.NameEn, o.NameEn));
    }

    /// <summary>Whether an active till-scoped user (a cashier) works at this branch.</summary>
    private static async Task<bool> HasCashiersAsync(PosDbContext db, Guid branchId, CancellationToken ct)
    {
        var roles = await db.Users.AsNoTracking()
            .Where(u => u.BranchId == branchId && u.IsActive && !u.IsDeleted)
            .Select(u => u.RoleId).ToListAsync(ct);
        foreach (var role in roles)
            if (await CashierTills.ScopeOfAsync(db, role, ct) == RoleScope.TillBranch) return true;
        return false;
    }

    /// <summary>Whether any stock is recorded at the branch, in any state.</summary>
    // Compared in memory: SQLite stores decimals as text, the same rule as the cash-box balances.
    private static async Task<bool> HoldsStockAsync(PosDbContext db, Guid branchId, CancellationToken ct) =>
        (await db.ProductStocks.AsNoTracking().Where(s => s.BranchId == branchId)
            .Select(s => new { s.StockQty, s.OnRentQty, s.MaintenanceQty, s.ScrapQty }).ToListAsync(ct))
        .Any(s => s.StockQty != 0 || s.OnRentQty != 0 || s.MaintenanceQty != 0 || s.ScrapQty != 0);

    /// <summary>
    /// Saves <paramref name="row"/>, and when <paramref name="takePrimary"/> makes it the primary of
    /// its kind: the current primary loses the flag first, in its own statement, and the row gains it
    /// in a second one — so the one-primary-per-kind index (IX_Branches_OnePrimaryPerKind) never sees
    /// two, and the caller's transaction makes the pair one change.
    /// </summary>
    private static async Task SaveTakingPrimaryAsync(PosDbContext db, Branch row, bool takePrimary, CancellationToken ct)
    {
        if (!takePrimary)
        {
            await db.SaveWithVersionsAsync(ct, row);
            return;
        }
        row.IsMain = false;
        var holders = await db.Branches
            .Where(b => b.IsMain && b.IsWarehouse == row.IsWarehouse && !b.IsDeleted && b.Id != row.Id)
            .ToListAsync(ct);
        foreach (var h in holders) h.IsMain = false;
        await db.SaveWithVersionsAsync(ct, [row, .. holders]);
        row.IsMain = true;
        await db.SaveWithVersionsAsync(ct, row);
    }

    /// <summary>The central safe is the main branch's cash box (DefaultBranches.AlignCentralSafe).</summary>
    private static async Task AlignCentralSafeAsync(PosDbContext db, Guid mainBranchId, CancellationToken ct)
    {
        var boxes = await db.CashBoxes.ToListAsync(ct);
        DefaultBranches.AlignCentralSafe(boxes, mainBranchId);
        await db.SaveChangesAsync(ct);
    }
    /// <summary>The fields an administrator edits. Never the code: it is the server's, and fixed for life.</summary>
    private static void Apply(Branch entity, UpsertBranchRequest r)
    {
        entity.NameAr = r.NameAr;
        entity.NameFr = r.NameFr;
        entity.NameEn = r.NameEn;
        entity.AddressFr = r.Address;
        entity.AddressAr = r.AddressAr ?? string.Empty;
        entity.AddressEn = r.AddressEn ?? string.Empty;
        // Keep the legacy single-address property aligned with French for older API readers.
        entity.Address = entity.AddressFr;
        entity.Phone = r.Phone;
        entity.IsMain = r.IsMain;
        entity.IsWarehouse = r.IsWarehouse;
        entity.AllowNegativeStock = r.AllowNegativeStock;
        entity.IsActive = r.IsActive;
    }
}

/// <summary>
/// A branch as the back office reads it: the row's own fields, with its logo as the signed-in link
/// a screen fetches rather than the path the files folder keeps it at. <paramref name="Code"/> is
/// the three-digit location code, read-only: the server gives it and nothing changes it.
/// </summary>
public sealed record BranchResponse(
    Guid Id, string NameAr, string NameFr, string NameEn,
    string Address, string AddressAr, string AddressFr, string AddressEn, string Phone,
    bool IsOpenForOperation, bool IsMain, bool AllowNegativeStock, bool IsActive,
    Dictionary<string, string> FiscalIds, string? LogoUrl,
    long Version, DateTime UpdatedAtUtc, bool IsDeleted, bool IsWarehouse, string Code)
{
    public static BranchResponse Of(Branch b) => new(
        b.Id, b.NameAr, b.NameFr, b.NameEn,
        b.Address, b.AddressAr, b.AddressFr, b.AddressEn, b.Phone,
        b.IsOpenForOperation, b.IsMain, b.AllowNegativeStock, b.IsActive,
        b.FiscalIds, StoreLogo.BranchUrl(b.Id, b.LogoPath),
        b.Version, b.UpdatedAtUtc, b.IsDeleted, b.IsWarehouse, b.Code);
}
