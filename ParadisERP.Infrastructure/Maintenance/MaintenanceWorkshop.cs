using ParadisERP.Application.Contracts;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Manufacturing;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Maintenance;

/// <summary>
/// MS-04's workshop list (docs/maintenance/plan.md, "MS-04 as built"): which employees of a branch are workshop staff.
/// While MS-04 says "only the workshop list", <see cref="MaintenanceLabour"/> offers and books on a job only the people on
/// the list of the job's branch who still belong to that branch and are employed that day. Under the default ("any employee
/// of the job's branch") the list is kept but nothing reads it.
/// <list type="bullet">
/// <item>One row per person and branch (unique). A person is added to the list of the branch they belong to; a row stays
/// when they move, but counts only while they belong to that branch again — the page shows it as "now at another branch"
/// so it can be removed. Nothing is copied to the new branch's list.</item>
/// <item>Branch scope as the rest of maintenance: the Admin reads and keeps every branch's list; anyone else their own
/// branch's (the API's CallersBranch and ForBranchOf).</item>
/// <item>Removing a row deletes it; hours booked before stay as they are.</item>
/// </list>
/// </summary>
public sealed class MaintenanceWorkshop(PosDbContext db, IRegionalSettingsProvider regional, TimeProvider time)
{
    public const string NotOnListCode = "employee_not_on_workshop_list";
    public const string AlreadyOnListCode = "already_on_workshop_list";
    public const string NotOfBranchCode = MaintenanceLabour.NotOfBranchCode;
    public const string NotEmployedCode = "employee_not_employed";

    /// <summary>The people on <paramref name="branchId"/>'s list (ids only): what MS-04's "only the workshop list" may book.</summary>
    public static IQueryable<Guid> OnListOf(PosDbContext db, Guid branchId) =>
        db.MaintenanceWorkshopMembers.Where(m => m.BranchId == branchId).Select(m => m.EmployeeId);

    /// <summary>The list of <paramref name="branchId"/>, or of every branch when null (the Admin's "all branches").</summary>
    public async Task<MaintenanceWorkshopDto> ListAsync(Guid? branchId, CancellationToken ct = default)
    {
        var today = Production.Today(regional, time);
        var settings = await MaintenanceSettingsStore.CurrentAsync(db, ct);
        var branches = await db.Branches.AsNoTracking().Where(b => !b.IsDeleted && (branchId == null || b.Id == branchId))
            .Select(b => new { b.Id, b.NameFr }).ToListAsync(ct);
        var names = await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.NameFr, ct);
        string Name(Guid id) => names.TryGetValue(id, out var n) ? n : "";

        var rows = await db.MaintenanceWorkshopMembers.AsNoTracking().Include(m => m.Employee)
            .Where(m => branchId == null || m.BranchId == branchId).ToListAsync(ct);
        static bool EmployedOn(Employee e, DateOnly day) => e.HiredOn <= day && (e.LeftOn == null || e.LeftOn >= day);
        var members = rows.Where(m => m.Employee is not null).Select(m =>
            {
                var e = m.Employee!;
                var employed = EmployedOn(e, today);
                return new MaintenanceWorkshopMemberDto(m.Id, e.Id, e.Number, e.FullName, m.BranchId, Name(m.BranchId),
                    e.BranchId, Name(e.BranchId), employed, employed && e.BranchId == m.BranchId, m.AddedBy, m.AddedAtUtc);
            })
            .OrderBy(m => m.BranchName, StringComparer.Ordinal).ThenBy(m => m.FullName, StringComparer.Ordinal).ToList();

        var employees = await db.Employees.AsNoTracking()
            .Where(e => (branchId == null || e.BranchId == branchId) && e.HiredOn <= today && (e.LeftOn == null || e.LeftOn >= today))
            .Select(e => new { e.Id, e.Number, e.FullName, e.BranchId }).ToListAsync(ct);
        var listed = rows.Select(m => (m.EmployeeId, m.BranchId)).ToHashSet();
        var available = employees.Where(e => !listed.Contains((e.Id, e.BranchId)))
            .Select(e => new MaintenanceWorkshopEmployeeDto(e.Id, e.Number, e.FullName, e.BranchId, Name(e.BranchId)))
            .OrderBy(e => e.BranchName, StringComparer.Ordinal).ThenBy(e => e.FullName, StringComparer.Ordinal).ToList();

        var summary = branches.Select(b => new MaintenanceWorkshopBranchDto(b.Id, b.NameFr,
                employees.Count(e => e.BranchId == b.Id), members.Count(m => m.BranchId == b.Id && m.Effective)))
            .OrderBy(b => b.BranchName, StringComparer.Ordinal).ToList();
        return new MaintenanceWorkshopDto(settings.BookableEmployees.ToString(), members, available, summary);
    }

    /// <summary>
    /// Puts an employee on the list of <c>r.BranchId</c> (their own branch when none is named). Refused: an unknown
    /// employee (404), one of another branch (400 <c>employee_not_of_branch</c>), one who has left (400
    /// <c>employee_not_employed</c>), one already on that list (409 <c>already_on_workshop_list</c>).
    /// </summary>
    /// <param name="scope">The branch whose list the answer shows; null for every branch.</param>
    public async Task<MaintenanceResult<MaintenanceWorkshopDto>> AddAsync(
        AddMaintenanceWorkshopMemberRequest r, MaintenanceActor actor, Guid? scope, CancellationToken ct = default)
    {
        await using (var tx = await db.BeginWriteScopeAsync(ct))
        {
            await db.LockVersionsAsync(ct);
            var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == r.EmployeeId, ct);
            if (employee is null) return Fail(404, "not_found", "Employee not found.");
            var branch = r.BranchId ?? employee.BranchId;
            if (employee.BranchId != branch)
                return Fail(400, NotOfBranchCode, "Only an employee of the branch goes on its workshop list (MS-04).");
            var today = Production.Today(regional, time);
            if (employee.LeftOn is DateOnly left && left < today)
                return Fail(400, NotEmployedCode, "This person has left; they are not added to the workshop list.");
            if (await db.MaintenanceWorkshopMembers.AnyAsync(m => m.EmployeeId == employee.Id && m.BranchId == branch, ct))
                return Fail(409, AlreadyOnListCode, "This person is already on the branch's workshop list.");
            db.MaintenanceWorkshopMembers.Add(new MaintenanceWorkshopMember
            {
                EmployeeId = employee.Id, BranchId = branch, AddedByUserId = actor.UserId, AddedBy = actor.Name, AddedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        return MaintenanceResult<MaintenanceWorkshopDto>.Done(await ListAsync(scope, ct));
    }

    /// <summary>Takes a row off the list (404 when there is none). Hours already booked are not touched.</summary>
    /// <param name="scope">The branch whose list the answer shows; null for every branch.</param>
    public async Task<MaintenanceResult<MaintenanceWorkshopDto>> RemoveAsync(Guid id, Guid? scope, CancellationToken ct = default)
    {
        await using (var tx = await db.BeginWriteScopeAsync(ct))
        {
            await db.LockVersionsAsync(ct);
            var row = await db.MaintenanceWorkshopMembers.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (row is null) return Fail(404, "not_found", "This person is not on the workshop list.");
            db.MaintenanceWorkshopMembers.Remove(row);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        return MaintenanceResult<MaintenanceWorkshopDto>.Done(await ListAsync(scope, ct));
    }

    private static MaintenanceResult<MaintenanceWorkshopDto> Fail(int status, string code, string error) =>
        MaintenanceResult<MaintenanceWorkshopDto>.Fail(status, code, error);
}
