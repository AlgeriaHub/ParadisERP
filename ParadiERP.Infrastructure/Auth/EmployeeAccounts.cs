using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Auth;

/// <summary>
/// An account belongs to an employee (owner, 2026-10-05: "the employee must be added first in the
/// employees form to be given a role").
///
/// <para>
/// An account is created from an employee who is still with the company, takes the employee's name
/// as its display name, and is the only <b>active</b> account that employee has. When the employee
/// leaves — or is switched off in HR — the account is switched off with them, and a cashier's own
/// till with it. Renaming the employee renames the account. None of this touches the Login ID or
/// the employee number: they are separate identifiers and never follow one another.
/// </para>
/// <para>
/// The bootstrap administrator and accounts that predate the rule have no employee and keep working
/// exactly as before; the users list asks for them to be linked.
/// </para>
/// </summary>
public static class EmployeeAccounts
{
    public const string EmployeeRequiredCode = "employee_required";
    public const string EmployeeRequiredMessage =
        "An account is given to an employee: add the person under Staff first, then choose them here.";

    public const string EmployeeUnknownCode = "employee_unknown";
    public const string EmployeeUnknownMessage = "That employee does not exist.";

    public const string EmployeeInactiveCode = "employee_inactive";
    public const string EmployeeInactiveMessage =
        "That employee has left or is switched off in Staff: an account can only be given to someone still working.";

    public const string EmployeeHasAccountCode = "employee_has_account";

    public const string AlreadyLinkedCode = "account_already_linked";
    public const string AlreadyLinkedMessage = "This account already belongs to an employee.";

    public const string LastAdminCode = "employee_holds_last_admin";
    public const string LastAdminMessage =
        "This employee holds the last active administrator account. Give another person administrator access first.";

    /// <summary>The sentence for an employee who already has an active account, naming how it signs in.</summary>
    public static string EmployeeHasAccountMessage(string signInName) =>
        $"This employee already has an active account ({signInName}). Switch it off before giving them another.";

    /// <summary>Whether an employee can be given an account now: they exist and are still working.</summary>
    public static async Task<(Employee? Employee, string? Code, string? Message)> EligibleAsync(
        PosDbContext db, Guid employeeId, CancellationToken ct)
    {
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, ct);
        if (employee is null) return (null, EmployeeUnknownCode, EmployeeUnknownMessage);
        if (!employee.IsActive || employee.LeftOn is not null) return (employee, EmployeeInactiveCode, EmployeeInactiveMessage);
        return (employee, null, null);
    }

    /// <summary>The employee's active, undeleted account other than <paramref name="except"/>, if any.</summary>
    public static Task<User?> ActiveAccountAsync(PosDbContext db, Guid employeeId, Guid? except, CancellationToken ct) =>
        db.Users.AsNoTracking()
            .Where(u => u.EmployeeId == employeeId && u.IsActive && !u.IsDeleted && u.Id != except)
            .FirstOrDefaultAsync(ct);

    /// <summary>The name an account is signed in with, for a sentence: its Login ID, or its legacy username.</summary>
    public static string SignInName(User user) => user.LoginId ?? user.Username;

    /// <summary>
    /// After an employee is saved in HR: their accounts take the employee's name, and an employee who
    /// has left or is switched off loses access — every active account of theirs is switched off and a
    /// cashier's own till with it. Tracked changes; the caller saves them with versions (which also
    /// revokes the sessions those accounts hold). Returns the accounts it changed, or a refusal when
    /// switching one off would leave the company with no active administrator.
    /// </summary>
    public static async Task<(List<User> Changed, string? Code, string? Message)> FollowEmployeeAsync(
        PosDbContext db, Employee employee, CancellationToken ct)
    {
        var accounts = await db.Users.Where(u => u.EmployeeId == employee.Id && !u.IsDeleted).ToListAsync(ct);
        var gone = !employee.IsActive || employee.LeftOn is not null;
        var changed = new List<User>();

        foreach (var account in accounts)
        {
            var touched = false;
            if (account.FullName != employee.FullName)
            {
                account.FullName = employee.FullName;
                touched = true;
            }
            if (gone && account.IsActive)
            {
                if (account.RoleId == SystemRoles.AdminId
                    && await db.Users.CountAsync(u => u.RoleId == SystemRoles.AdminId && u.IsActive && !u.IsDeleted, ct) <= 1)
                    return ([], LastAdminCode, LastAdminMessage);
                account.IsActive = false;
                await CashierTills.AlignAsync(db, account, await CashierTills.ScopeOfAsync(db, account.RoleId, ct), ct);
                touched = true;
            }
            if (touched) changed.Add(account);
        }
        return (changed, null, null);
    }
}
