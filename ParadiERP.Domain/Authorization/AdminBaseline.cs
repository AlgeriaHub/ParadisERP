using static ParadiERP.Domain.PermissionCatalog;

namespace ParadiERP.Domain;

/// <summary>
/// What the protected Admin role holds (the owner, 2026-10-03; REDESIGN_PLAN.md §17.6) — written
/// out key by key, never "everything".
///
/// <para>
/// "Everything" would have changed access on the day it shipped: an Admin does not hold
/// <c>hr.attendance</c> today, on purpose. So every catalogue key is classified here, one way or
/// the other, and a key added to <see cref="PermissionCatalog"/> without being classified stops
/// the server at start-up and fails the tests, naming the key. Adding a permission is therefore
/// always also a decision about the Admin.
/// </para>
/// <para>
/// The Admin role's permissions are written only from this list — by the migration and by the
/// start-up check, which repairs any drift — and no screen or route may edit them.
/// </para>
/// </summary>
public static class AdminBaseline
{
    /// <summary>Held by the Admin role. Equals today's Admin (§17.6).</summary>
    public static readonly IReadOnlyList<string> Included =
    [
        SalesCreate, SalesView, SalesRefund, ReportsSales, SalesUntaxed,
        CustomersView, CustomersCreate, CustomersEdit, CustomersDelete, CustomersAccountsView, CustomersOpeningBalance, CustomersAccountsFilter,
        CustomersDocumentsView,
        RentalsView, RentalsCreate, RentalsEdit, RentalsIssue, RentalsReturn, RentalsClose, RentalsSettings,
        RentalsCancelled,
        ProductsView, ProductsManage, InventoryView, InventoryAdjust, InventoryReceive, InventoryTransfer, InventoryTransferWriteOff,
        InventoryOpening, StockViewOtherBranches, GiftCardsView, GiftCardsManage,
        TreasuryCashBox, ReceiptsView, ReceiptsCreate, ReceiptsSettle, ReceiptsSettleOwn, InvoicesView, InvoicesCreate, InvoicesUntaxed,
        VouchersView, VouchersCreate, VouchersPay, VouchersCancel, VouchersApprove, VouchersApproveOwn,
        Collections, CashBoxesView, Remit, FinanceSettings, SuppliersOpeningBalance,
        AccountingView, AccountingManage, ReportsAccounting, AccountingClose,
        PayrollSettingsView, PayrollManage, PayrollEditStatutory,
        ManufacturingView, ManufacturingLinesManage, ManufacturingBomManage, ManufacturingBomApprove, ManufacturingSettings,
        ManufacturingOrdersCreate, ManufacturingOrdersStartShort, ManufacturingOrdersManage, ManufacturingMaterialsMove,
        ManufacturingLogSubmit, ManufacturingLogApprove, ManufacturingLabourOverride, ManufacturingOutputRelease,
        ManufacturingCompletionApprove, ManufacturingReportsView, ManufacturingCostsView, ManufacturingCostSnapshotCreate, ManufacturingCostsRatesView,
        MaintenanceView, MaintenanceJobsManage, MaintenanceJobsClose, MaintenanceLabourOverride, MaintenancePricing, MaintenanceCostsView,
        PermissionCatalog.MaintenanceSettings, // qualified: the settings row is also a Domain type of that name
        UsersManage, RolesManage, DocumentsManage, BranchesView, BranchesManage, DevicesManage,
        CompanySettings, SubscriptionUsage,
    ];

    /// <summary>Deliberately not held by the Admin role, each with the reason.</summary>
    public static readonly IReadOnlyDictionary<string, string> Excluded = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [HrAttendance] =
            "Means a foreman who enters days and sees no wages. An Admin reaches every attendance route through payroll.manage, and never held it.",
    };

    /// <summary>
    /// Every catalogue key classified exactly once, and nothing classified that the catalogue
    /// lacks. The message names the key, so the failure says what to do.
    /// </summary>
    public static void Validate() => Validate(PermissionCatalog.All.Select(p => p.Key));

    public static void Validate(IEnumerable<string> catalogue)
    {
        var keys = catalogue.ToList();
        var included = Included.ToHashSet(StringComparer.Ordinal);

        foreach (var key in keys)
        {
            var inside = included.Contains(key);
            var outside = Excluded.ContainsKey(key);
            if (!inside && !outside)
                throw new InvalidOperationException(
                    $"Permission '{key}' is not classified for the Admin baseline. Add it to AdminBaseline.Included, or to " +
                    "AdminBaseline.Excluded with the reason, in ParadiERP.Domain/Authorization/AdminBaseline.cs.");
            if (inside && outside)
                throw new InvalidOperationException(
                    $"Permission '{key}' is both included in and excluded from the Admin baseline. Keep it in one list.");
        }

        var known = keys.ToHashSet(StringComparer.Ordinal);
        var stray = Included.Concat(Excluded.Keys).FirstOrDefault(k => !known.Contains(k));
        if (stray is not null)
            throw new InvalidOperationException(
                $"The Admin baseline classifies '{stray}', which is not in PermissionCatalog.All.");
        var twice = Included.GroupBy(k => k, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (twice is not null)
            throw new InvalidOperationException($"Permission '{twice.Key}' is listed twice in AdminBaseline.Included.");
    }
}
