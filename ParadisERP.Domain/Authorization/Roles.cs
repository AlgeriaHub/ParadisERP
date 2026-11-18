using System.ComponentModel.DataAnnotations;
using static ParadisERP.Domain.PermissionCatalog;

namespace ParadisERP.Domain;

/// <summary>
/// On which data a role's members act — never what they may do (REDESIGN_PLAN.md §17.6). A column
/// of the role, so no code has to read a scope out of a role's name.
/// </summary>
public enum RoleScope
{
    /// <summary>Every branch, and the choice of branch (today: the Admin).</summary>
    Company = 0,

    /// <summary>The user's own branch for documents, lists and reports (today: the Manager).</summary>
    AssignedBranch = 1,

    /// <summary>The till's branch, including reads by id; replicated to tills for offline sign-in (today: the Cashier).</summary>
    TillBranch = 2,
}

/// <summary>A named set of permissions with a branch scope. Each user has exactly one (AUTH-1).</summary>
public class Role
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary><c>admin</c>, <c>manager</c> or <c>cashier</c> for the system roles; null for a role an Admin created.</summary>
    [MaxLength(40)] public string? Key { get; set; }

    [MaxLength(80)] public string Name { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="Name"/> as <see cref="RoleNames.Normalize"/> writes it, unique in the database, so two
    /// roles never differ only by case or spacing on any provider (SQLite compares case-sensitively, SQL
    /// Server not; AUTH-1 constraint 2, 2026-10-03). Written by every save from the name, never by hand.
    /// </summary>
    [MaxLength(80)] public string NormalizedName { get; set; } = string.Empty;

    [MaxLength(400)] public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>A system role cannot be deleted and keeps its key and scope.</summary>
    public bool IsSystem { get; set; }

    public RoleScope Scope { get; set; } = RoleScope.AssignedBranch;

    /// <summary>
    /// Moves whenever the role or its permissions change — scope and active state included. Never
    /// written by hand: every save that changes one bumps it in the database, in the same transaction
    /// (AUTH-1 Step 3), and the server's cached decision for a member is used only at this version.
    /// </summary>
    public long Version { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>How a role's name is compared: trimmed, inner spaces collapsed, upper case (invariant).</summary>
public static class RoleNames
{
    public const int MaxLength = 80;

    public static string Normalize(string? name) =>
        string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    /// <summary>The name as it is stored: trimmed, inner spaces collapsed, its letters as typed.</summary>
    public static string Clean(string? name) =>
        string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>One permission a role grants.</summary>
public class RolePermission
{
    public Guid RoleId { get; set; }
    [MaxLength(60)] public string PermissionKey { get; set; } = string.Empty;
}

/// <summary>A permission given to one user on top of their role. There is no deny.</summary>
public class UserPermission
{
    public Guid UserId { get; set; }
    [MaxLength(60)] public string PermissionKey { get; set; } = string.Empty;
    [MaxLength(64)] public string? GrantedBy { get; set; }
    public DateTime GrantedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A legacy per-user grant that was not carried over because it never gave the user anything,
/// with the reason (REDESIGN_PLAN.md §17.5). Written by the migration and, until the users screen
/// writes roles itself (Step 5), whenever an edit stores such a grant again.
/// </summary>
public class AuthorizationReportEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    [MaxLength(64)] public string Username { get; set; } = string.Empty;
    [MaxLength(20)] public string LegacyRole { get; set; } = string.Empty;
    [MaxLength(60)] public string PermissionKey { get; set; } = string.Empty;
    /// <summary>A code from <see cref="LegacyGrants"/>; the sentence beside it is for a person.</summary>
    [MaxLength(40)] public string Reason { get; set; } = string.Empty;
    [MaxLength(400)] public string Detail { get; set; } = string.Empty;
    /// <summary><c>migration</c> (the upgrade) or <c>user-save</c> (an edit made after it).</summary>
    [MaxLength(20)] public string Source { get; set; } = string.Empty;
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>The three roles every company starts with, at fixed ids so a seed is the same row everywhere.</summary>
public static class SystemRoles
{
    public static readonly Guid AdminId = Guid.Parse("5a17e000-0000-4000-8000-0000000000a1");
    public static readonly Guid ManagerId = Guid.Parse("5a17e000-0000-4000-8000-0000000000a2");
    public static readonly Guid CashierId = Guid.Parse("5a17e000-0000-4000-8000-0000000000a3");

    public const string AdminKey = "admin";
    public const string ManagerKey = "manager";
    public const string CashierKey = "cashier";

    /// <summary>
    /// What the Manager role starts with: every key a Manager reaches today, by role or by the two
    /// capabilities they hold (§17.5). Editable later; the seed is written once.
    /// </summary>
    public static readonly IReadOnlyList<string> ManagerSeed =
    [
        SalesCreate, SalesView, SalesRefund, ReportsSales, SalesUntaxed,
        CustomersView, CustomersCreate, CustomersEdit, CustomersDelete, CustomersAccountsView,
        RentalsView, RentalsCreate, RentalsEdit, RentalsIssue, RentalsReturn, RentalsSettings,
        ProductsView, ProductsManage, InventoryView, InventoryAdjust, InventoryReceive, InventoryTransfer,
        InventoryOpening, GiftCardsView, GiftCardsManage,
        ReceiptsView, ReceiptsCreate, ReceiptsSettle, InvoicesView, InvoicesCreate,
        VouchersView, VouchersCreate, VouchersPay, VouchersCancel, VouchersApprove,
        Collections, CashBoxesView, Remit, FinanceSettings,
        AccountingView, AccountingManage, ReportsAccounting,
        PayrollSettingsView,
        BranchesView, BranchesManage, DevicesManage, CompanySettings, SubscriptionUsage,
    ];

    /// <summary>
    /// What the Cashier role starts with: the till, as every cashier has it today. Not
    /// <c>sales.untaxed</c>: a cashier holds that per user (owner, 2026-11-02).
    /// </summary>
    public static readonly IReadOnlyList<string> CashierSeed = [SalesCreate, SalesView, SalesRefund, ReportsSales];

    public sealed record Definition(Guid Id, string Key, string Name, string Description, RoleScope Scope, IReadOnlyList<string> Permissions);

    public static readonly IReadOnlyList<Definition> All =
    [
        new(AdminId, AdminKey, "Admin", "Runs the company: every branch, the users and the roles. Protected.", RoleScope.Company, AdminBaseline.Included),
        new(ManagerId, ManagerKey, "Manager", "Runs one branch.", RoleScope.AssignedBranch, ManagerSeed),
        new(CashierId, CashierKey, "Cashier", "Sells at the till of one branch.", RoleScope.TillBranch, CashierSeed),
    ];

    /// <summary>The system role a legacy <see cref="UserRole"/> becomes.</summary>
    public static Guid IdFor(UserRole role) => role switch
    {
        UserRole.Admin => AdminId,
        UserRole.Manager => ManagerId,
        _ => CashierId,
    };

    public static Definition For(UserRole role) => All.Single(d => d.Id == IdFor(role));
}
