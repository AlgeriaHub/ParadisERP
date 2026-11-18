using ParadiERP.Domain;

namespace ParadiERP.Tests.Security;

/// <summary>AUTH-1 Step 1: the catalogue, the Admin baseline's classification, the seeds and the legacy-grant rule.</summary>
public class PermissionCatalogTests
{
    [Fact]
    public void The_catalogue_has_the_83_approved_keys_in_nine_modules_and_keeps_the_old_spellings()
    {
        PermissionCatalog.Validate();
        // 58 approved in AUTH-1, then customers.opening_balance, finance.receipts.settle_own,
        // suppliers.opening_balance, customers.accounts.filter and finance.invoices.untaxed (2026-10-10),
        // then inventory.transfer.write_off (2026-10-12), then customers.documents.view (2026-10-25),
        // then the manufacturing module's five (Phase 1, 2026-10-25), ten more (Phase 2, 2026-10-26) and
        // manufacturing.costs.view (costing, Phase A, 2026-10-26), then manufacturing.costs.snapshot.create and
        // manufacturing.costs.rates.view (costing B1, 2026-10-29), then accounting.close (CLO-01, 2026-11-01), then
        // sales.untaxed (TAX-01, 2026-11-02), then the maintenance module's seven (Phase M0, 2026-11-09).
        Assert.Equal(92, PermissionCatalog.All.Count);
        Assert.Equal(10, PermissionCatalog.Modules.Length);
        Assert.Equal(18, PermissionCatalog.All.Count(p => p.Module == PermissionCatalog.Manufacturing));
        Assert.Equal(
            ["maintenance.view", "maintenance.jobs.manage", "maintenance.jobs.close", "maintenance.labour.override", "maintenance.pricing",
             "maintenance.costs.view", "maintenance.settings"],
            PermissionCatalog.All.Where(p => p.Module == PermissionCatalog.Maintenance).Select(p => p.Key));
        Assert.Equal(PermissionCatalog.Modules.Order(), PermissionCatalog.All.Select(p => p.Module).Distinct().Order());
        // The five keys tokens and the users column already carry, spelled exactly as before.
        foreach (var legacy in Permissions.All) Assert.True(PermissionCatalog.Contains(legacy), legacy);
        Assert.False(PermissionCatalog.Contains("TREASURY.CASH_BOX"));
        Assert.Equal("perm.finance.vouchers.approve", PermissionCatalog.All.Single(p => p.Key == "finance.vouchers.approve").LabelKey);
    }

    [Fact]
    public void Every_key_is_classified_for_the_Admin_and_only_hr_attendance_is_left_out()
    {
        AdminBaseline.Validate();
        // Every catalogue key but hr.attendance: 83 − 1, the two B1 costing keys included (2026-10-29).
        Assert.Equal(91, AdminBaseline.Included.Count);  // + accounting.close (CLO-01, 2026-11-01), sales.untaxed (TAX-01, 2026-11-02), the seven maintenance keys (M0, 2026-11-09)
        Assert.Equal([PermissionCatalog.HrAttendance], AdminBaseline.Excluded.Keys);
    }

    [Fact]
    public void Payroll_derived_manufacturing_rates_stay_behind_both_keys_and_no_role_below_the_Admin_holds_either()
    {
        // A worker's rate or labour cost needs manufacturing.costs.rates.view AND payroll.manage (B1, B2): the
        // services check both. Neither reaches a Manager or a Cashier by role; each is a deliberate grant.
        Assert.Equal(PermissionCatalog.Manufacturing, PermissionCatalog.All.Single(p => p.Key == PermissionCatalog.ManufacturingCostsRatesView).Module);
        foreach (var key in new[] { PermissionCatalog.ManufacturingCostsRatesView, PermissionCatalog.PayrollManage })
        {
            Assert.Contains(key, AdminBaseline.Included);
            Assert.DoesNotContain(key, SystemRoles.ManagerSeed);
            Assert.DoesNotContain(key, SystemRoles.CashierSeed);
        }
        // The rates key only narrows what a costing route shows; it never opens a route on its own.
        Assert.DoesNotContain(ParadiERP.API.Authorization.RoutePermissions.ByRoute.Values,
            r => r.AnyOf.Contains(PermissionCatalog.ManufacturingCostsRatesView));
    }

    [Fact]
    public void An_unclassified_permission_stops_the_check_and_is_named()
    {
        var catalogue = PermissionCatalog.All.Select(p => p.Key).Append("reports.stock_valuation");
        var failure = Assert.Throws<InvalidOperationException>(() => AdminBaseline.Validate(catalogue));
        Assert.Contains("'reports.stock_valuation'", failure.Message);
        Assert.Contains("AdminBaseline.Included", failure.Message);

        // A key the catalogue lost but the baseline still names is said too.
        var shorter = PermissionCatalog.All.Select(p => p.Key).Where(k => k != PermissionCatalog.RentalsClose);
        Assert.Contains("'rentals.close'", Assert.Throws<InvalidOperationException>(() => AdminBaseline.Validate(shorter)).Message);
    }

    [Fact]
    public void The_seeds_name_only_catalogue_keys_and_carry_the_approved_counts()
    {
        Assert.Equal(48, SystemRoles.ManagerSeed.Count);  // + sales.untaxed (2026-11-02); a cashier holds it per user
        Assert.Equal(4, SystemRoles.CashierSeed.Count);
        Assert.All(SystemRoles.All.SelectMany(r => r.Permissions), k => Assert.True(PermissionCatalog.Contains(k), k));
        Assert.All(SystemRoles.All, r => Assert.Equal(r.Permissions.Count, r.Permissions.Distinct().Count()));

        // Approval: the Manager approves others' vouchers, the Admin their own as well.
        Assert.Contains(PermissionCatalog.VouchersApprove, SystemRoles.ManagerSeed);
        Assert.DoesNotContain(PermissionCatalog.VouchersApproveOwn, SystemRoles.ManagerSeed);
        Assert.Contains(PermissionCatalog.VouchersApproveOwn, AdminBaseline.Included);
        // A receipt one recorded is confirmed by someone else, unless the role says otherwise (2026-10-10).
        Assert.DoesNotContain(PermissionCatalog.ReceiptsSettleOwn, SystemRoles.ManagerSeed);
        Assert.Contains(PermissionCatalog.ReceiptsSettleOwn, AdminBaseline.Included);
        // Identity copies: a permission of their own, the Admin's, never given with customers.view
        // (owner, 2026-10-25).
        Assert.Contains(PermissionCatalog.CustomersDocumentsView, AdminBaseline.Included);
        Assert.DoesNotContain(PermissionCatalog.CustomersDocumentsView, SystemRoles.ManagerSeed);
        Assert.DoesNotContain(PermissionCatalog.CustomersDocumentsView, SystemRoles.CashierSeed);
        // Manufacturing: the Admin's by role, anyone else's only as a grant (Phase 1, audit D36).
        // Maintenance likewise (Phase M0, plan §6.1).
        foreach (var key in Capabilities.Manufacturing.Concat(Capabilities.Maintenance))
        {
            Assert.Contains(key, AdminBaseline.Included);
            Assert.DoesNotContain(key, SystemRoles.ManagerSeed);
            Assert.DoesNotContain(key, SystemRoles.CashierSeed);
        }
        // customers.create: Admin and Manager, never the Cashier.
        Assert.Contains(PermissionCatalog.CustomersCreate, SystemRoles.ManagerSeed);
        Assert.DoesNotContain(PermissionCatalog.CustomersCreate, SystemRoles.CashierSeed);
        // payroll.settings.view is the rules' read only; no payroll key reaches a Manager by role.
        Assert.Contains(PermissionCatalog.PayrollSettingsView, SystemRoles.ManagerSeed);
        foreach (var key in new[] { PermissionCatalog.PayrollManage, PermissionCatalog.HrAttendance, PermissionCatalog.PayrollEditStatutory })
            Assert.DoesNotContain(key, SystemRoles.ManagerSeed);

        Assert.Equal(
            [(SystemRoles.AdminId, RoleScope.Company), (SystemRoles.ManagerId, RoleScope.AssignedBranch), (SystemRoles.CashierId, RoleScope.TillBranch)],
            SystemRoles.All.Select(r => (r.Id, r.Scope)));
    }

    [Theory]
    [InlineData(UserRole.Cashier, "treasury.cash_box", "treasury.cash_box", "")]
    [InlineData(UserRole.Cashier, "payroll.manage,stock.view_other_branches", "", "payroll.manage:role-gate|stock.view_other_branches:role-gate")]
    [InlineData(UserRole.Cashier, "hr.attendance,payroll.edit_statutory", "", "hr.attendance:role-gate|payroll.edit_statutory:role-gate")]
    [InlineData(UserRole.Manager, "payroll.manage,hr.attendance,stock.view_other_branches,payroll.edit_statutory",
        "payroll.manage|hr.attendance|stock.view_other_branches|payroll.edit_statutory", "")]
    [InlineData(UserRole.Manager, "treasury.cash_box", "", "treasury.cash_box:manager-by-role")]
    [InlineData(UserRole.Admin, "hr.attendance,payroll.manage", "", "hr.attendance:admin-already-held|payroll.manage:admin-already-held")]
    [InlineData(UserRole.Manager, " PAYROLL.MANAGE , payroll.manage,everything", "payroll.manage", "everything:unknown-permission")]
    [InlineData(UserRole.Manager, null, "", "")]
    public void A_legacy_grant_is_kept_only_where_it_opened_something(UserRole role, string? stored, string kept, string dropped)
    {
        var outcome = LegacyGrants.Map(role, stored);
        Assert.Equal(SystemRoles.IdFor(role), outcome.RoleId);
        Assert.Equal(kept.Split('|', StringSplitOptions.RemoveEmptyEntries), outcome.Kept);
        Assert.Equal(dropped.Split('|', StringSplitOptions.RemoveEmptyEntries), outcome.Dropped.Select(d => $"{d.Key}:{d.Reason}"));
        Assert.All(outcome.Dropped, d => Assert.False(string.IsNullOrWhiteSpace(d.Detail)));
    }
}
