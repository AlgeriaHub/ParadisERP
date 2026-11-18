namespace ParadiERP.Domain;

/// <summary>
/// What a user may do, as the server decides it — derived, never granted.
///
/// <para>
/// A <see cref="Permissions">permission</see> is stored on the user row and ticked on the users
/// screen. A capability is not: it is computed here from who the user is, every time, and there
/// is nothing to store, tick or revoke. It exists so that the one decision — "may this user
/// create a customer?" — has a single home that the route enforces and the login reports, and a
/// screen reads the answer instead of repeating a rule about role names.
/// </para>
/// <para>
/// None of these is in <see cref="Permissions.All"/>, so <see cref="Permissions.Store"/> drops
/// one sent by the users screen and no login can be given one by hand. Widening a capability to
/// another role is a change to <see cref="Holds"/> and nowhere else.
/// </para>
/// </summary>
public static class Capabilities
{
    /// <summary>
    /// Create a customer (FR-1, the owner, 2026-10-03: from the rental form and the till as well
    /// as the Customers page). A manager and an admin, exactly as the catalogue route allowed
    /// before; a cashier keeps the walk-in customer and gains nothing.
    /// </summary>
    public const string CreateCustomers = "customers.create";

    /// <summary>
    /// Approve a pending payment voucher: a manager and an admin, never the cashier who holds the
    /// cash box — the person who pays is not the one who decides it is owed (answer 25).
    /// <b>Reported, not enforced here</b>: <c>POST /api/payment-vouchers/{id}/approve</c> keeps its
    /// own role gate, unchanged, and <c>PaymentVoucherCapabilityTests</c> pins this decision to that
    /// gate, so the screen's Approve button and the route can never disagree.
    /// </summary>
    public const string ApprovePaymentVouchers = "payment_vouchers.approve";

    /// <summary>
    /// Approve a voucher one raised oneself: the admin alone. Reported, not enforced here: the
    /// approval itself refuses an own voucher without <c>finance.vouchers.approve_own</c> (<c>VoucherActor.MayApproveOwn</c>), and the test
    /// pins the two together.
    /// </summary>
    public const string ApproveOwnPaymentVouchers = "payment_vouchers.approve_own";

    /// <summary>
    /// Set a customer's opening balance after registration (once, when left empty) and correct it
    /// with a reason (owner, 2026-10-10). Reported from <c>customers.opening_balance</c>, which the
    /// routes require; the customer card offers the two actions by reading this.
    /// </summary>
    public const string SetOpeningBalances = "customers.opening_balance";

    /// <summary>The supplier's mirror of <see cref="SetOpeningBalances"/>, from <c>suppliers.opening_balance</c>.</summary>
    public const string SetSupplierOpeningBalances = "suppliers.opening_balance";

    /// <summary>
    /// Narrow a customer statement to sales or to rentals: the admin's (owner, 2026-10-10). Reported
    /// from <c>customers.accounts.filter</c>, which <c>GET /api/receivables/statement</c> asks for
    /// itself whenever a narrowed view is requested.
    /// </summary>
    public const string FilterCustomerStatements = "customer_statements.filter";

    /// <summary>
    /// Issue an invoice without tax (owner, 2026-10-10). Reported from <c>finance.invoices.untaxed</c>,
    /// which the route requires. Admin only.
    /// </summary>
    public const string IssueUntaxedInvoices = "finance.invoices.untaxed";

    /// <summary>
    /// Leave a till sale untaxed (owner, 2026-11-02). Reported from <c>sales.untaxed</c>; the till reads
    /// it to decide whether its "taxed" box may be unticked. Before it existed every seller could.
    /// </summary>
    public const string SellUntaxed = PermissionCatalog.SalesUntaxed;

    /// <summary>
    /// Declare goods lost in transit (owner, 2026-10-12). Reported from <c>inventory.transfer.write_off</c>,
    /// which the loss route requires. Admin only. The same key lets its holder confirm or reject a
    /// shortfall the destination reported (2026-10-13) — on any transfer when company-scoped; the
    /// screen reads that per transfer (<c>StockTransferDto.CanDecideShortfall</c>), not from here.
    /// </summary>
    public const string WriteOffTransferLoss = "inventory.transfer.write_off";

    /// <summary>
    /// Open the identity-document copies kept with a customer or a contract (owner, 2026-10-25).
    /// Reported from <c>customers.documents.view</c>, which those routes require, so a screen
    /// without it says the copies are restricted instead of asking for them. Admin only.
    /// </summary>
    public const string ViewIdentityDocuments = "customers.documents.view";

    /// <summary>
    /// The manufacturing module's five permissions (Phase 1), reported under their own keys so the
    /// sidebar offers the module and each screen offers only what its routes allow. Admin only by
    /// role; anyone else holds them only as a grant.
    /// </summary>
    public const string ManufacturingView = PermissionCatalog.ManufacturingView;
    public const string ManufacturingLinesManage = PermissionCatalog.ManufacturingLinesManage;
    public const string ManufacturingBomManage = PermissionCatalog.ManufacturingBomManage;
    public const string ManufacturingBomApprove = PermissionCatalog.ManufacturingBomApprove;
    public const string ManufacturingSettings = PermissionCatalog.ManufacturingSettings;
    // Phase 2: production orders.
    public const string ManufacturingOrdersCreate = PermissionCatalog.ManufacturingOrdersCreate;
    public const string ManufacturingOrdersStartShort = PermissionCatalog.ManufacturingOrdersStartShort;
    public const string ManufacturingOrdersManage = PermissionCatalog.ManufacturingOrdersManage;
    public const string ManufacturingMaterialsMove = PermissionCatalog.ManufacturingMaterialsMove;
    public const string ManufacturingLogSubmit = PermissionCatalog.ManufacturingLogSubmit;
    public const string ManufacturingLogApprove = PermissionCatalog.ManufacturingLogApprove;
    public const string ManufacturingLabourOverride = PermissionCatalog.ManufacturingLabourOverride;
    public const string ManufacturingOutputRelease = PermissionCatalog.ManufacturingOutputRelease;
    public const string ManufacturingCompletionApprove = PermissionCatalog.ManufacturingCompletionApprove;
    public const string ManufacturingReportsView = PermissionCatalog.ManufacturingReportsView;
    // Costing, Phase A.
    public const string ManufacturingCostsView = PermissionCatalog.ManufacturingCostsView;
    public const string ManufacturingCostSnapshotCreate = PermissionCatalog.ManufacturingCostSnapshotCreate;
    public const string ManufacturingCostsRatesView = PermissionCatalog.ManufacturingCostsRatesView;

    /// <summary>The manufacturing capabilities, in the catalogue's order.</summary>
    public static readonly string[] Manufacturing =
        [ManufacturingView, ManufacturingLinesManage, ManufacturingBomManage, ManufacturingBomApprove, ManufacturingSettings,
         ManufacturingOrdersCreate, ManufacturingOrdersStartShort, ManufacturingOrdersManage, ManufacturingMaterialsMove,
         ManufacturingLogSubmit, ManufacturingLogApprove, ManufacturingLabourOverride, ManufacturingOutputRelease,
         ManufacturingCompletionApprove, ManufacturingReportsView, ManufacturingCostsView, ManufacturingCostSnapshotCreate, ManufacturingCostsRatesView];

    /// <summary>
    /// The maintenance module's seven permissions (Phase M0, docs/maintenance/plan.md §6.1), reported under their own
    /// keys like manufacturing's, so the sidebar offers Maintenance › Settings and, from M1, the module's other pages.
    /// Admin only by role; anyone else holds them only as a grant.
    /// </summary>
    public const string MaintenanceView = PermissionCatalog.MaintenanceView;
    public const string MaintenanceJobsManage = PermissionCatalog.MaintenanceJobsManage;
    public const string MaintenanceJobsClose = PermissionCatalog.MaintenanceJobsClose;
    public const string MaintenanceLabourOverride = PermissionCatalog.MaintenanceLabourOverride;
    public const string MaintenancePricing = PermissionCatalog.MaintenancePricing;
    public const string MaintenanceCostsView = PermissionCatalog.MaintenanceCostsView;
    public const string MaintenanceSettings = PermissionCatalog.MaintenanceSettings;

    /// <summary>The maintenance capabilities, in the catalogue's order.</summary>
    public static readonly string[] Maintenance =
        [MaintenanceView, MaintenanceJobsManage, MaintenanceJobsClose, MaintenanceLabourOverride, MaintenancePricing,
         MaintenanceCostsView, MaintenanceSettings];

    public static readonly string[] All =
        [CreateCustomers, ApprovePaymentVouchers, ApproveOwnPaymentVouchers, SetOpeningBalances,
         SetSupplierOpeningBalances, FilterCustomerStatements, IssueUntaxedInvoices, WriteOffTransferLoss,
         ViewIdentityDocuments, SellUntaxed, .. Manufacturing, .. Maintenance];

    /// <summary>
    /// <b>Legacy reference only (AUTH-1 Step 2)</b>: the role-name rule these names stood for. No
    /// production code reads it; the login reports the names from the user's effective permissions
    /// (<c>ServerAuthorization.LegacyCapabilities</c>), and the routes ask the permissions themselves.
    /// </summary>
    public static bool Holds(UserRole role, string capability) => capability switch
    {
        CreateCustomers => role is UserRole.Manager or UserRole.Admin,
        ApprovePaymentVouchers => role is UserRole.Manager or UserRole.Admin,
        ApproveOwnPaymentVouchers => role is UserRole.Admin,
        SetOpeningBalances => role is UserRole.Admin,
        SetSupplierOpeningBalances => role is UserRole.Admin,
        FilterCustomerStatements => role is UserRole.Admin,
        IssueUntaxedInvoices => role is UserRole.Admin,
        WriteOffTransferLoss => role is UserRole.Admin,
        ViewIdentityDocuments => role is UserRole.Admin,
        SellUntaxed => true,
        _ when Manufacturing.Contains(capability) => role is UserRole.Admin,
        _ when Maintenance.Contains(capability) => role is UserRole.Admin,
        _ => false,
    };

    /// <summary>Legacy reference only: every capability the role held.</summary>
    public static IReadOnlyList<string> For(UserRole role) => All.Where(c => Holds(role, c)).ToList();
}
