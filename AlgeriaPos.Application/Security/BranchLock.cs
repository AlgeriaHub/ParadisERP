using AlgeriaPos.Domain;

namespace AlgeriaPos.Application.Security;

/// <summary>
/// Who may choose a branch, and which branch everyone else works at (the owner, 2026-09-30).
///
/// <para>
/// The owner's words, in the order he gave them: "each employee whatever his role is assigned to
/// a branch, and then can see and choose only his branch"; "each branch is independent"; and,
/// final, "in the whole program, the branch choice does not appear except for the super admin".
/// Read as a rule:
/// </para>
/// <list type="bullet">
/// <item><b>Every user is assigned a branch</b> (<c>User.BranchId</c>), whatever the role. The users
/// tab requires one and the server refuses a new user without one.</item>
/// <item><b>The Admin role is free.</b> "Super admin" is <see cref="UserRole.Admin"/>: every branch
/// select exists for him, filters offer "all branches", and a document he raises goes to the
/// branch he names. His assigned branch is only the default the forms start on.</item>
/// <item><b>Everyone else is locked</b> to their assigned branch — the main branch grants nothing.
/// A Manager or a Cashier sees no branch choice anywhere; a document they raise is written at
/// their branch whatever the request says; their lists and reports read their branch only.</item>
/// <item><b>A legacy row with no branch</b> (every account that predates the rule, the seeded
/// admin among them) works at the <b>main branch</b>, so nobody is locked out on upgrade. The
/// users list says "no branch assigned" in words until the owner assigns one.</item>
/// </list>
/// <para>
/// The login and SSO responses carry the result (<c>UserDto.BranchId</c>, <c>BranchLocked</c>), so
/// a screen never has to reload branches to decide. The terminal refusal of docs/07 §2.2
/// (<c>BranchScope.RefusesTerminal</c>) is a different rule and stays cashier-only.
/// </para>
/// </summary>
public static class BranchLock
{
    /// <summary>
    /// True when a role of this scope may not choose a branch: every scope but <see cref="RoleScope.Company"/>
    /// (AUTH-1 — read from the role's scope, never from its name).
    /// </summary>
    public static bool IsLocked(RoleScope scope) => scope != RoleScope.Company;

    /// <summary>
    /// The branch a user works at: the one assigned to them, else the main branch (a legacy row
    /// with no branch). Null only when neither exists, which a company with branches never is.
    /// </summary>
    public static Guid? EffectiveBranch(Guid? assigned, Guid? mainBranch) => assigned ?? mainBranch;

    /// <summary>The users tab's refusal of a user with no branch.</summary>
    public const string BranchRequiredMessage = "Choose the branch this user works at.";

    /// <summary>The refusal of a PUT that tries to take a user's branch away.</summary>
    public const string BranchNotClearableMessage =
        "A user's branch cannot be removed. Choose the branch this user works at.";

    /// <summary>A locked caller whose account has no branch in a company with no main branch.</summary>
    public const string NoBranchMessage =
        "This account has no branch assigned and the company has no main branch. Ask the administrator to assign one.";

    /// <summary>A locked caller naming a cash box of another branch.</summary>
    public const string ForeignBoxMessage = "That cash box belongs to another branch. Use your own branch's box.";

    /// <summary>An Admin naming a branch that does not exist.</summary>
    public const string UnknownBranchMessage = "Unknown branch.";
}
