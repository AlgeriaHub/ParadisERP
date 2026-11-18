using AlgeriaPos.Domain;

namespace AlgeriaPos.Tests.Security;

/// <summary>
/// Who may run payroll, who may only enter a sheet, and who may move a figure the law sets.
///
/// <para>
/// docs/07 §5.2 planned a third state: <c>payroll.edit_statutory</c> held by configuration alone
/// and ungrantable through the users route, so that no login a company admin could create would
/// reach a contribution rate. The owner reversed that on 2026-09-24 — the module ships with
/// placeholder figures and he edits them himself — and these tests pin the reversal, so the next
/// reader finds the decision written down instead of inferring it from a missing check.
/// </para>
/// </summary>
public class PayrollPermissionTests
{
    [Fact]
    public void An_administrator_runs_payroll_and_may_move_the_statutory_figures_without_a_grant()
    {
        var admin = new User { Role = UserRole.Admin };

        // Nothing granted, and held anyway: the owner is the administrator, and the figures he
        // was told to edit himself are behind this one.
        Assert.Empty(Permissions.Parse(admin.Permissions));
        Assert.Contains(Permissions.ManagePayroll, Permissions.For(admin));
        Assert.Contains(Permissions.EditPayrollStatutory, Permissions.For(admin));
    }

    [Fact]
    public void A_manager_holds_neither_until_somebody_grants_it()
    {
        var plain = new User { Role = UserRole.Manager };
        Assert.Empty(Permissions.For(plain));

        // The accountant's own login: the statutory figures without the rest of the module.
        var accountant = new User
        {
            Role = UserRole.Manager,
            Permissions = Permissions.Store([Permissions.EditPayrollStatutory]),
        };
        var held = Permissions.For(accountant).ToList();
        Assert.Equal([Permissions.EditPayrollStatutory], held);
        Assert.DoesNotContain(Permissions.ManagePayroll, held);
    }

    [Fact]
    public void The_statutory_permission_is_grantable_through_the_users_route_which_reverses_the_plan()
    {
        // Permissions.Store is what PUT /api/users/{id} writes with. docs/07 §5.2 required this
        // to come back empty; the owner's decision requires it to come back with the name in it.
        Assert.Equal(Permissions.EditPayrollStatutory, Permissions.Store([Permissions.EditPayrollStatutory]));
        Assert.Equal(Permissions.ManagePayroll, Permissions.Store([Permissions.ManagePayroll]));
        Assert.Equal(Permissions.EnterAttendance, Permissions.Store([Permissions.EnterAttendance]));
    }

    [Fact]
    public void A_foreman_entering_days_is_not_thereby_reading_wages()
    {
        // The whole reason hr.attendance exists: the man who counts the crew each morning holds
        // this and nothing else, so no route that shows a figure of pay will take him.
        var foreman = new User
        {
            Role = UserRole.Manager,
            Permissions = Permissions.Store([Permissions.EnterAttendance]),
        };

        Assert.Equal([Permissions.EnterAttendance], Permissions.For(foreman));
        Assert.DoesNotContain(Permissions.ManagePayroll, Permissions.For(foreman));
        Assert.DoesNotContain(Permissions.EditPayrollStatutory, Permissions.For(foreman));
    }

    [Fact]
    public void Entering_attendance_is_not_something_a_role_hands_out()
    {
        // An admin reaches every attendance route through ManagePayroll, so hr.attendance is
        // never inherited. If that ever changes, the permission has stopped meaning "a foreman
        // who submits days and sees no wages", and this is where it is noticed.
        Assert.DoesNotContain(Permissions.EnterAttendance, Permissions.For(new User { Role = UserRole.Admin }));
    }

    [Fact]
    public void A_cashier_who_is_handed_every_name_still_holds_only_what_was_written_down()
    {
        // Widening is not expressible: a crafted column still yields exactly the known names,
        // and the role adds nothing to a cashier.
        var cashier = new User
        {
            Role = UserRole.Cashier,
            Permissions = "payroll.everything, payroll.manage ,*, admin",
        };

        Assert.Equal([Permissions.ManagePayroll], Permissions.For(cashier));
    }

    [Fact]
    public void Every_payroll_permission_is_spelled_the_way_the_policy_compares_it()
    {
        // RequireClaim compares ordinally, so a name that survives Parse in another case would
        // read as granted in the users list and be refused at the endpoint — silently.
        foreach (var permission in new[]
                 {
                     Permissions.ManagePayroll, Permissions.EnterAttendance, Permissions.EditPayrollStatutory,
                 })
        {
            Assert.Contains(permission, Permissions.All, StringComparer.Ordinal);
            var shouted = new User { Role = UserRole.Cashier, Permissions = permission.ToUpperInvariant() };
            Assert.Equal([permission], Permissions.For(shouted));
        }
    }
}
