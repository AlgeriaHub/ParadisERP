using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Manufacturing;

/// <summary>
/// Who is calling: the token's user, whether their role spans the company, the branch they work at
/// otherwise, and the manufacturing permissions the server resolved for them.
/// </summary>
public sealed record MfgCaller(Guid UserId, string Name, bool CompanyScoped, Guid? BranchId, IReadOnlySet<string> Keys)
{
    public bool Has(string key) => Keys.Contains(key);
}

/// <summary>
/// Which production lines a caller reaches, and what they may do on one (D35, D36).
///
/// <para>
/// The route requirement (RoutePermissions) decides <i>what</i> a caller may do — read, run lines,
/// write or approve recipes. This decides <i>where</i>: a caller with company scope reaches every line;
/// anyone else only a line they are a member of, the line they are named the responsible manager of, and
/// a line standing on the branch they work at. A line out of reach is answered "not found", as a
/// document of another branch is, so its existence is not disclosed either.
/// </para>
/// <para>
/// Creating and editing a line and naming its members stay with company scope: a line is a part of the
/// company's structure, like a branch. Assigning a line's employees and products is also open to a
/// manager of that line, who runs it day to day.
/// </para>
/// </summary>
public static class ManufacturingAccess
{
    /// <summary>The lines this caller reaches.</summary>
    public static IQueryable<ProductionLine> Reachable(PosDbContext db, MfgCaller caller)
    {
        var lines = db.ProductionLines.AsQueryable();
        if (caller.CompanyScoped) return lines;
        var user = caller.UserId;
        var branch = caller.BranchId;
        return lines.Where(l =>
            l.ManagerUserId == user
            || db.ProductionLineMembers.Any(m => m.LineId == l.Id && m.UserId == user)
            || (branch != null && l.LocationId == branch));
    }

    /// <summary>Whether this caller reaches the line with this id.</summary>
    public static Task<bool> ReachesAsync(PosDbContext db, MfgCaller caller, Guid lineId, CancellationToken ct) =>
        Reachable(db, caller).AnyAsync(l => l.Id == lineId, ct);

    /// <summary>Create and edit lines, and name their members.</summary>
    public static bool CanEditLines(MfgCaller caller) =>
        caller.CompanyScoped && caller.Has(PermissionCatalog.ManufacturingLinesManage);

    /// <summary>Whether this caller manages this line: its responsible manager, or a member with the manager role.</summary>
    public static async Task<bool> ManagesAsync(PosDbContext db, MfgCaller caller, Guid lineId, CancellationToken ct)
    {
        if (caller.CompanyScoped) return true;
        return await db.ProductionLines.AnyAsync(l => l.Id == lineId && l.ManagerUserId == caller.UserId, ct)
            || await db.ProductionLineMembers.AnyAsync(m =>
                m.LineId == lineId && m.UserId == caller.UserId && m.Role == ProductionLineMemberRole.Manager, ct);
    }

    /// <summary>Assign the line's employees and products: company scope or a manager of the line, with the permission.</summary>
    public static async Task<bool> CanAssignAsync(PosDbContext db, MfgCaller caller, Guid lineId, CancellationToken ct) =>
        caller.Has(PermissionCatalog.ManufacturingLinesManage) && await ManagesAsync(db, caller, lineId, ct);

    /// <summary>
    /// Whether the caller reaches a BOM: one with no line is the company's and readable by anyone who may
    /// read manufacturing; one with a line only by those who reach that line.
    /// </summary>
    public static async Task<bool> ReachesBomAsync(PosDbContext db, MfgCaller caller, Guid? lineId, CancellationToken ct) =>
        lineId is not Guid line || await ReachesAsync(db, caller, line, ct);

    /// <summary>
    /// Write a draft: with the permission, on a line the caller reaches; a BOM for every line (no line)
    /// only with company scope, because it changes what both lines make.
    /// </summary>
    public static async Task<bool> CanWriteBomAsync(PosDbContext db, MfgCaller caller, Guid? lineId, CancellationToken ct)
    {
        if (!caller.Has(PermissionCatalog.ManufacturingBomManage)) return false;
        if (lineId is not Guid line) return caller.CompanyScoped;
        return await ReachesAsync(db, caller, line, ct);
    }

    /// <summary>
    /// Activate or retire: with the permission, as a manager of the BOM's line; a BOM for every line only
    /// with company scope.
    /// </summary>
    public static async Task<bool> CanApproveBomAsync(PosDbContext db, MfgCaller caller, Guid? lineId, CancellationToken ct)
    {
        if (!caller.Has(PermissionCatalog.ManufacturingBomApprove)) return false;
        if (lineId is not Guid line) return caller.CompanyScoped;
        return await ManagesAsync(db, caller, line, ct);
    }
}
