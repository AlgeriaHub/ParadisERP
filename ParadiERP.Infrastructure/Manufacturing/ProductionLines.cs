using System.Text.RegularExpressions;
using ParadiERP.Application.Contracts;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Manufacturing;

/// <summary>
/// Production lines (Phase 1 §1; audit section 3, D1, D2, D34–D36): the line record over its
/// warehouse-type location, its members, the employees working on it and the products it makes, and
/// the stock its location holds. What a caller reaches and may change is <see cref="ManufacturingAccess"/>'s.
///
/// <para>
/// A line never holds stock of its own: its stock is its location's <see cref="ProductStock"/> rows,
/// which every existing stock document already keeps. That is why two lines never share a location and
/// why the location of a line, once chosen, does not move.
/// </para>
/// </summary>
public sealed partial class ProductionLines(PosDbContext db)
{
    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{0,15}$")]
    private static partial Regex CodePattern();

    // ---------- reads ----------

    public async Task<List<ProductionLineSummaryDto>> ListAsync(MfgCaller caller, bool includeInactive, CancellationToken ct)
    {
        var query = ManufacturingAccess.Reachable(db, caller).AsNoTracking();
        if (!includeInactive) query = query.Where(l => l.IsActive);
        var lines = await query.Include(l => l.Location).ToListAsync(ct);
        return await SummariesAsync(caller, lines, ct);
    }

    public async Task<MfgResult<ProductionLineDto>> GetAsync(MfgCaller caller, Guid id, CancellationToken ct)
    {
        var line = await ManufacturingAccess.Reachable(db, caller).AsNoTracking().Include(l => l.Location)
            .FirstOrDefaultAsync(l => l.Id == id, ct);
        if (line is null) return MfgFail.NotFound<ProductionLineDto>("Production line");

        var summary = (await SummariesAsync(caller, [line], ct))[0];
        var members = await (from m in db.ProductionLineMembers.AsNoTracking()
                             join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                             where m.LineId == id
                             select new LineMemberDto(u.Id, u.Username, u.FullName, m.Role.ToString())).ToListAsync(ct);
        var employees = await db.ProductionLineEmployees.AsNoTracking()
            .Where(a => a.LineId == id).Include(a => a.Employee!).ThenInclude(e => e.Branch).Include(a => a.JobRole)
            .ToListAsync(ct);
        var products = await db.ProductionLineProducts.AsNoTracking().Where(p => p.LineId == id).Include(p => p.Product).ToListAsync(ct);

        return MfgResult<ProductionLineDto>.Done(new ProductionLineDto(
            summary,
            members.OrderBy(m => m.FullName, StringComparer.CurrentCulture).ToList(),
            employees.OrderBy(a => a.EndedOn is null ? 0 : 1).ThenBy(a => a.Employee!.FullName, StringComparer.CurrentCulture)
                .Select(ToDto).ToList(),
            products.Select(p => p.Product!).OrderBy(p => p.Sku, StringComparer.Ordinal)
                .Select(p => new LineProductDto(p.Id, p.Sku, p.NameAr, p.NameFr, p.NameEn, p.ManufacturingRole.ToString())).ToList()));
    }

    private static LineEmployeeDto ToDto(ProductionLineEmployee a) => new(
        a.Id, a.EmployeeId, a.Employee!.Number, a.Employee.FullName,
        a.Employee.BranchId, a.Employee.Branch?.Code ?? "", a.Employee.Branch?.NameAr ?? "", a.Employee.Branch?.NameFr ?? "",
        a.Employee.JobTitle, a.JobRoleId, a.JobRole?.Code, a.AssignedOn, a.EndedOn, a.Note);

    private async Task<List<ProductionLineSummaryDto>> SummariesAsync(MfgCaller caller, List<ProductionLine> lines, CancellationToken ct)
    {
        var ids = lines.Select(l => l.Id).ToList();
        var managerIds = lines.Where(l => l.ManagerUserId != null).Select(l => l.ManagerUserId!.Value).Distinct().ToList();
        var managers = await db.Users.AsNoTracking().Where(u => managerIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        var employees = (await db.ProductionLineEmployees.AsNoTracking().Where(a => ids.Contains(a.LineId) && a.EndedOn == null)
            .Select(a => a.LineId).ToListAsync(ct)).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var products = (await db.ProductionLineProducts.AsNoTracking().Where(p => ids.Contains(p.LineId))
            .Select(p => p.LineId).ToListAsync(ct)).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var boms = (await db.BillsOfMaterials.AsNoTracking()
            .Where(b => b.ProductionLineId != null && ids.Contains(b.ProductionLineId.Value) && b.Status == BomStatus.Active)
            .Select(b => b.ProductionLineId!.Value).ToListAsync(ct)).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var managed = caller.CompanyScoped
            ? ids.ToHashSet()
            : (await db.ProductionLineMembers.AsNoTracking()
                    .Where(m => ids.Contains(m.LineId) && m.UserId == caller.UserId && m.Role == ProductionLineMemberRole.Manager)
                    .Select(m => m.LineId).ToListAsync(ct))
                .Concat(lines.Where(l => l.ManagerUserId == caller.UserId).Select(l => l.Id)).ToHashSet();
        var mayRun = caller.Has(PermissionCatalog.ManufacturingLinesManage);

        return lines.OrderBy(l => l.Code, StringComparer.Ordinal).Select(l => new ProductionLineSummaryDto(
            l.Id, l.Code, l.NameAr, l.NameFr, l.NameEn, l.Description,
            l.LocationId, l.Location?.Code ?? "", l.Location?.NameAr ?? "", l.Location?.NameFr ?? "",
            l.ManagerUserId, l.ManagerUserId is Guid m ? managers.GetValueOrDefault(m) : null, l.IsActive,
            employees.GetValueOrDefault(l.Id), products.GetValueOrDefault(l.Id), boms.GetValueOrDefault(l.Id),
            ManufacturingAccess.CanEditLines(caller), mayRun && managed.Contains(l.Id))).ToList();
    }

    /// <summary>
    /// What the line's location holds, straight from its stock rows: the line's own stock, apart from
    /// every other location's. A row is listed while it holds something or has a reorder level.
    /// </summary>
    public async Task<MfgResult<List<LineStockDto>>> StockAsync(MfgCaller caller, Guid lineId, CancellationToken ct)
    {
        var line = await ManufacturingAccess.Reachable(db, caller).AsNoTracking().FirstOrDefaultAsync(l => l.Id == lineId, ct);
        if (line is null) return MfgFail.NotFound<List<LineStockDto>>("Production line");
        var rows = await db.ProductStocks.AsNoTracking().Where(s => s.BranchId == line.LocationId)
            .Include(s => s.Product).ToListAsync(ct);
        return MfgResult<List<LineStockDto>>.Done(rows
            .Where(s => s.Product is { IsDeleted: false } && (s.OwnedQty != 0 || s.ReorderLevel != 0))
            .OrderBy(s => s.Product!.Sku, StringComparer.Ordinal)
            .Select(s => new LineStockDto(
                s.ProductId, s.Product!.Sku, s.Product.NameAr, s.Product.NameFr, s.Product.NameEn,
                s.Product.ManufacturingRole.ToString(), s.Product.Unit,
                s.StockQty, s.MaintenanceQty, s.ScrapQty, s.ReorderLevel)).ToList());
    }

    /// <summary>The warehouses a line may stand on: every warehouse but the main one, each with the line already on it.</summary>
    public async Task<List<LineLocationDto>> LocationsAsync(CancellationToken ct)
    {
        var used = await db.ProductionLines.AsNoTracking().ToDictionaryAsync(l => l.LocationId, l => l.Id, ct);
        var warehouses = await db.Branches.AsNoTracking().Where(b => !b.IsDeleted && b.IsWarehouse && !b.IsMain).ToListAsync(ct);
        return warehouses.OrderBy(b => b.Code, StringComparer.Ordinal)
            .Select(b => new LineLocationDto(b.Id, b.Code, b.NameAr, b.NameFr, b.IsActive, used.TryGetValue(b.Id, out var l) ? l : null))
            .ToList();
    }

    /// <summary>Logins that may be named on a line — active ones, with no password or permission in sight.</summary>
    public async Task<List<LineUserCandidateDto>> UserCandidatesAsync(CancellationToken ct) =>
        (await db.Users.AsNoTracking().Where(u => u.IsActive && !u.IsDeleted)
            .Select(u => new LineUserCandidateDto(u.Id, u.Username, u.FullName, u.BranchId)).ToListAsync(ct))
        .OrderBy(u => u.FullName, StringComparer.CurrentCulture).ToList();

    /// <summary>
    /// Employees who may be assigned to the line: those still with the company and active. A caller with
    /// company scope picks from everyone; a manager of the line from the employees of their own branch and
    /// of the line's location, because payroll's branch scope (P0.1) keeps other branches' staff files
    /// closed to them. Only the number, name, branch and job title are read — never pay.
    /// </summary>
    public async Task<MfgResult<List<LineEmployeeCandidateDto>>> EmployeeCandidatesAsync(MfgCaller caller, Guid lineId, CancellationToken ct)
    {
        var line = await ManufacturingAccess.Reachable(db, caller).AsNoTracking().FirstOrDefaultAsync(l => l.Id == lineId, ct);
        if (line is null) return MfgFail.NotFound<List<LineEmployeeCandidateDto>>("Production line");
        if (!await ManufacturingAccess.CanAssignAsync(db, caller, lineId, ct))
            return MfgFail.Forbidden<List<LineEmployeeCandidateDto>>("Only a manager of this line assigns its employees.");

        var query = db.Employees.AsNoTracking().Where(e => e.IsActive && e.LeftOn == null);
        if (!caller.CompanyScoped)
        {
            var own = caller.BranchId;
            query = query.Where(e => e.BranchId == line.LocationId || (own != null && e.BranchId == own));
        }
        var rows = await query.Select(e => new
        {
            e.Id, e.Number, e.FullName, e.BranchId, e.JobTitle,
            Code = e.Branch!.Code, NameAr = e.Branch.NameAr, NameFr = e.Branch.NameFr,
        }).ToListAsync(ct);
        return MfgResult<List<LineEmployeeCandidateDto>>.Done(rows.OrderBy(e => e.FullName, StringComparer.CurrentCulture)
            .Select(e => new LineEmployeeCandidateDto(e.Id, e.Number, e.FullName, e.BranchId, e.Code, e.NameAr, e.NameFr, e.JobTitle))
            .ToList());
    }

    // ---------- writes ----------

    public async Task<MfgResult<ProductionLineDto>> SaveAsync(MfgCaller caller, Guid? id, UpsertProductionLineRequest r, CancellationToken ct)
    {
        if (!ManufacturingAccess.CanEditLines(caller))
            return MfgFail.Forbidden<ProductionLineDto>("Production lines are created and edited with company-wide scope.");

        ProductionLine? line = null;
        if (id is Guid existing)
        {
            line = await db.ProductionLines.FirstOrDefaultAsync(l => l.Id == existing, ct);
            if (line is null) return MfgFail.NotFound<ProductionLineDto>("Production line");
        }

        var code = MfgText.Code(r.Code);
        if (!CodePattern().IsMatch(code))
            return MfgFail.Invalid<ProductionLineDto>("invalid_code", "The line's code is 1 to 16 letters, digits, '-' or '_'.");
        var problem = MfgText.First(
            MfgText.Required(r.NameAr, 200, "The Arabic name"),
            MfgText.Required(r.NameFr, 200, "The French name"),
            MfgText.Optional(r.NameEn, 200, "The English name"),
            MfgText.Optional(r.Description, 1000, "The description"));
        if (problem is not null) return MfgFail.Invalid<ProductionLineDto>("invalid_input", problem);
        if (await db.ProductionLines.AnyAsync(l => l.Code == code && l.Id != (id ?? Guid.Empty), ct))
            return MfgFail.Conflict<ProductionLineDto>("code_taken", $"Another production line already has the code '{code}'.");

        // ---- the location: a warehouse, not the main one, not another line's; fixed once chosen ----
        if (line is not null && line.LocationId != r.LocationId)
            return MfgFail.Conflict<ProductionLineDto>("location_fixed",
                "A line's location holds its stock; it cannot be moved to another location.");
        if (line is null)
        {
            var location = await db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == r.LocationId && !b.IsDeleted, ct);
            if (location is null) return MfgFail.NotFound<ProductionLineDto>("Location");
            if (!location.IsWarehouse || location.IsMain)
                return MfgFail.Invalid<ProductionLineDto>("location_not_line_warehouse",
                    "A line stands on a warehouse of its own — not a selling branch and not the main warehouse.");
            if (await db.ProductionLines.AnyAsync(l => l.LocationId == r.LocationId, ct))
                return MfgFail.Conflict<ProductionLineDto>("location_taken", "Another production line already stands on this location.");
        }

        if (r.ManagerUserId is Guid manager
            && !await db.Users.AnyAsync(u => u.Id == manager && u.IsActive && !u.IsDeleted, ct)
            && line?.ManagerUserId != manager)
            return MfgFail.Invalid<ProductionLineDto>("unknown_user", "The responsible manager must be an active login.");

        line ??= db.ProductionLines.Add(new ProductionLine { LocationId = r.LocationId, CreatedBy = caller.Name }).Entity;
        line.Code = code;
        line.NameAr = r.NameAr.Trim();
        line.NameFr = r.NameFr.Trim();
        line.NameEn = MfgText.Clean(r.NameEn) ?? line.NameFr;
        line.Description = MfgText.Clean(r.Description);
        line.ManagerUserId = r.ManagerUserId;
        line.IsActive = r.IsActive;
        line.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return await GetAsync(caller, line.Id, ct);
    }

    /// <summary>The line's members, sent whole. Company scope only: membership is what opens a line to a branch user.</summary>
    public async Task<MfgResult<ProductionLineDto>> SetMembersAsync(MfgCaller caller, Guid lineId, SetLineMembersRequest r, CancellationToken ct)
    {
        if (!ManufacturingAccess.CanEditLines(caller))
            return MfgFail.Forbidden<ProductionLineDto>("A line's members are named with company-wide scope.");
        if (!await db.ProductionLines.AnyAsync(l => l.Id == lineId, ct)) return MfgFail.NotFound<ProductionLineDto>("Production line");

        var wanted = r.Members ?? [];
        if (wanted.GroupBy(m => m.UserId).Any(g => g.Count() > 1))
            return MfgFail.Invalid<ProductionLineDto>("duplicate_member", "A login is listed twice.");
        var roles = new Dictionary<Guid, ProductionLineMemberRole>();
        foreach (var m in wanted)
        {
            if (!Enum.TryParse<ProductionLineMemberRole>(m.Role, true, out var role) || !Enum.IsDefined(role))
                return MfgFail.Invalid<ProductionLineDto>("invalid_role", "A member is a manager or a supervisor.");
            roles[m.UserId] = role;
        }
        var current = await db.ProductionLineMembers.Where(m => m.LineId == lineId).ToListAsync(ct);
        var newIds = roles.Keys.Where(id => current.All(c => c.UserId != id)).ToList();
        var activeNew = await db.Users.Where(u => newIds.Contains(u.Id) && u.IsActive && !u.IsDeleted).Select(u => u.Id).ToListAsync(ct);
        if (activeNew.Count != newIds.Count)
            return MfgFail.Invalid<ProductionLineDto>("unknown_user", "Every member must be an active login.");

        foreach (var gone in current.Where(c => !roles.ContainsKey(c.UserId))) db.ProductionLineMembers.Remove(gone);
        foreach (var (userId, role) in roles)
        {
            var row = current.FirstOrDefault(c => c.UserId == userId);
            if (row is null) db.ProductionLineMembers.Add(new ProductionLineMember { LineId = lineId, UserId = userId, Role = role, AddedBy = caller.Name });
            else row.Role = role;
        }
        await db.SaveChangesAsync(ct);
        return await GetAsync(caller, lineId, ct);
    }

    /// <summary>
    /// Puts an employee to work on the line from a day. The employee's own branch — the one payroll pays
    /// them from — is not touched. One running assignment per employee and line; the same person may
    /// work on both lines.
    /// </summary>
    public async Task<MfgResult<ProductionLineDto>> AssignEmployeeAsync(MfgCaller caller, Guid lineId, AssignLineEmployeeRequest r, DateOnly today, CancellationToken ct)
    {
        var refusal = await AssignGateAsync(caller, lineId, ct);
        if (refusal is not null) return refusal;

        var candidates = await EmployeeCandidatesAsync(caller, lineId, ct);
        if (!candidates.Ok || candidates.Value!.All(e => e.Id != r.EmployeeId))
            return MfgFail.Invalid<ProductionLineDto>("unknown_employee", "Choose an active employee you may assign.");
        if (r.JobRoleId is Guid roleId && !await db.ManufacturingJobRoles.AnyAsync(x => x.Id == roleId && x.IsActive, ct))
            return MfgFail.Invalid<ProductionLineDto>("unknown_job_role", "The job role does not exist or is switched off.");
        if (MfgText.Optional(r.Note, 400, "The note") is string tooLong) return MfgFail.Invalid<ProductionLineDto>("invalid_input", tooLong);
        if (await db.ProductionLineEmployees.AnyAsync(a => a.LineId == lineId && a.EmployeeId == r.EmployeeId && a.EndedOn == null, ct))
            return MfgFail.Conflict<ProductionLineDto>("already_assigned", "This employee already works on this line.");

        db.ProductionLineEmployees.Add(new ProductionLineEmployee
        {
            LineId = lineId,
            EmployeeId = r.EmployeeId,
            JobRoleId = r.JobRoleId,
            AssignedOn = r.AssignedOn ?? today,
            Note = MfgText.Clean(r.Note),
            AssignedBy = caller.Name,
        });
        await db.SaveChangesAsync(ct);
        return await GetAsync(caller, lineId, ct);
    }

    /// <summary>Ends an assignment on a day; the row stays, so who worked where stays on record.</summary>
    public async Task<MfgResult<ProductionLineDto>> EndEmployeeAsync(MfgCaller caller, Guid lineId, Guid assignmentId, EndLineEmployeeRequest r, DateOnly today, CancellationToken ct)
    {
        var refusal = await AssignGateAsync(caller, lineId, ct);
        if (refusal is not null) return refusal;
        var assignment = await db.ProductionLineEmployees.FirstOrDefaultAsync(a => a.Id == assignmentId && a.LineId == lineId, ct);
        if (assignment is null) return MfgFail.NotFound<ProductionLineDto>("Assignment");
        if (assignment.EndedOn is not null) return MfgFail.Conflict<ProductionLineDto>("already_ended", "This assignment has already ended.");
        var end = r.EndedOn ?? today;
        if (end < assignment.AssignedOn)
            return MfgFail.Invalid<ProductionLineDto>("end_before_start", "An assignment cannot end before it started.");
        assignment.EndedOn = end;
        await db.SaveChangesAsync(ct);
        return await GetAsync(caller, lineId, ct);
    }

    /// <summary>
    /// The products the line makes, sent whole: semi-finished and finished products only. A product the
    /// line has an active recipe for stays, because that recipe is how the line makes it.
    /// </summary>
    public async Task<MfgResult<ProductionLineDto>> SetProductsAsync(MfgCaller caller, Guid lineId, SetLineProductsRequest r, CancellationToken ct)
    {
        var refusal = await AssignGateAsync(caller, lineId, ct);
        if (refusal is not null) return refusal;

        var wanted = (r.ProductIds ?? []).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => wanted.Contains(p.Id) && !p.IsDeleted).ToListAsync(ct);
        if (products.Count != wanted.Count) return MfgFail.NotFound<ProductionLineDto>("Product");
        if (products.FirstOrDefault(p => !ManufacturingRoles.IsProduced(p.ManufacturingRole)) is { } wrong)
            return MfgFail.Invalid<ProductionLineDto>("not_produced", $"{wrong.Sku} is not a semi-finished or finished product.");

        var current = await db.ProductionLineProducts.Where(p => p.LineId == lineId).ToListAsync(ct);
        var gone = current.Where(c => !wanted.Contains(c.ProductId)).ToList();
        var goneIds = gone.Select(g => g.ProductId).ToList();
        if (await db.BillsOfMaterials.AnyAsync(b => b.ProductionLineId == lineId && goneIds.Contains(b.ProductId) && b.Status == BomStatus.Active, ct))
            return MfgFail.Conflict<ProductionLineDto>("product_has_bom",
                "The line has an active recipe for a product you removed; retire the recipe first.");
        foreach (var g in gone) db.ProductionLineProducts.Remove(g);
        foreach (var id in wanted.Where(id => current.All(c => c.ProductId != id)))
            db.ProductionLineProducts.Add(new ProductionLineProduct { LineId = lineId, ProductId = id });
        await db.SaveChangesAsync(ct);
        return await GetAsync(caller, lineId, ct);
    }

    /// <summary>Reached, then allowed to assign — in that order, so a line out of reach stays "not found".</summary>
    private async Task<MfgResult<ProductionLineDto>?> AssignGateAsync(MfgCaller caller, Guid lineId, CancellationToken ct)
    {
        if (!await ManufacturingAccess.ReachesAsync(db, caller, lineId, ct)) return MfgFail.NotFound<ProductionLineDto>("Production line");
        if (!await ManufacturingAccess.CanAssignAsync(db, caller, lineId, ct))
            return MfgFail.Forbidden<ProductionLineDto>("Only a manager of this line assigns its employees and products.");
        return null;
    }
}
