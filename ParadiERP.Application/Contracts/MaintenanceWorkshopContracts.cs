namespace ParadiERP.Application.Contracts;

// ---------- MS-04: the workshop list (docs/maintenance/plan.md, "MS-04 as built") ----------

/// <summary>
/// One person on a branch's workshop list. <c>Effective</c>: they still belong to that branch and are employed today, so
/// MS-04's "only the workshop list" may book them on its jobs; otherwise <c>EmployeeBranchName</c> and <c>Employed</c> say why not.
/// </summary>
public record MaintenanceWorkshopMemberDto(
    Guid Id, Guid EmployeeId, string Number, string FullName, Guid BranchId, string BranchName,
    Guid EmployeeBranchId, string EmployeeBranchName, bool Employed, bool Effective, string? AddedBy, DateTime AddedAtUtc);

/// <summary>Someone employed today at a branch in the reader's scope and not yet on that branch's list.</summary>
public record MaintenanceWorkshopEmployeeDto(Guid EmployeeId, string Number, string FullName, Guid BranchId, string BranchName);

/// <summary>A branch in the reader's scope: how many people it employs today, and how many of them its list may book.</summary>
public record MaintenanceWorkshopBranchDto(Guid BranchId, string BranchName, int Employees, int OnList);

/// <summary>The workshop list of the reader's branch (every branch for the Admin), with MS-04's choice in force.</summary>
public record MaintenanceWorkshopDto(
    string BookableEmployees, List<MaintenanceWorkshopMemberDto> Members, List<MaintenanceWorkshopEmployeeDto> Available,
    List<MaintenanceWorkshopBranchDto> Branches);

/// <summary>
/// Puts an employee on their branch's workshop list. <c>BranchId</c> is the list's branch: the Admin's is honoured, anyone
/// else's is replaced by their own (CallersBranch); the employee must belong to it.
/// </summary>
public partial record AddMaintenanceWorkshopMemberRequest(Guid EmployeeId, Guid? BranchId);

/// <remarks><c>BranchId</c> is ignored for a non-admin: the list is their own branch's.</remarks>
public partial record AddMaintenanceWorkshopMemberRequest : IRaisedAtBranch
{
    Guid? IRaisedAtBranch.RaisedAtBranch => BranchId;
    IRaisedAtBranch IRaisedAtBranch.AtBranch(Guid branchId) => this with { BranchId = branchId };
}
