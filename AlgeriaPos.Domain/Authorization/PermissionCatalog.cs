namespace AlgeriaPos.Domain;

/// <summary>One permission of the catalogue: its stable key and the module the screens group it under.</summary>
public sealed record PermissionDef(string Key, string Module)
{
    /// <summary>The dictionary key of its name on the screens (Step 6 writes the words).</summary>
    public string LabelKey => "perm." + Key;
}

/// <summary>
/// The one server-side list of what a user can be allowed to do (AUTH-1, REDESIGN_PLAN.md §17.5).
///
/// <para>
/// <b>Groundwork only (Step 1).</b> Nothing reads this to decide a request yet: every route is
/// still gated by its role list and <see cref="Permissions"/>. The catalogue, the system roles and
/// the users' grants are written and kept in step so Step 2 can switch the routes over, and a
/// test proves the switch changes nobody's access.
/// </para>
/// <para>
/// A key is spelled once, here, as it will be stored and carried in a token — and compared
/// ordinally, which is why the five keys the old model already used keep their exact spelling.
/// Every key must also be classified for the Admin role in <see cref="AdminBaseline"/>; start-up
/// refuses to run while one is not.
/// </para>
/// </summary>
public static class PermissionCatalog
{
    public const string Sales = "sales";
    public const string Customers = "customers";
    public const string Rentals = "rentals";
    public const string Inventory = "inventory";
    public const string Finance = "finance";
    public const string Accounting = "accounting";
    public const string Payroll = "payroll";
    public const string Manufacturing = "manufacturing";
    public const string Maintenance = "maintenance";
    public const string Administration = "administration";

    /// <summary>The modules in the order the screens list them.</summary>
    public static readonly string[] Modules = [Sales, Customers, Rentals, Inventory, Finance, Accounting, Payroll, Manufacturing, Maintenance, Administration];

    // ---------- sales (the till) ----------
    public const string SalesCreate = "sales.create";
    public const string SalesView = "sales.view";
    public const string SalesRefund = "sales.refund";
    public const string ReportsSales = "reports.sales";
    /// <summary>
    /// Leave a till sale untaxed (TAX-01, finding E#1; owner, 2026-11-02). Till sales start untaxed
    /// (owner, 2026-09-25); a seller without this key starts every sale taxed and cannot untick it.
    /// The Admin and Manager roles hold it; a cashier holds it per user, ticked on the users screen,
    /// and every cashier on file when it arrived was given it, so nothing changed that day. It never
    /// refuses a sale: the till applies it, and a sale pushed untaxed is recorded as it was sold.
    /// </summary>
    public const string SalesUntaxed = "sales.untaxed";

    // ---------- customers ----------
    public const string CustomersView = "customers.view";
    public const string CustomersCreate = "customers.create";
    public const string CustomersEdit = "customers.edit";
    public const string CustomersDelete = "customers.delete";
    public const string CustomersAccountsView = "customers.accounts.view";
    /// <summary>
    /// Set a customer's opening balance after registration — once, when it was left empty — and
    /// correct one with a written reason (owner, 2026-10-10). Setting it while registering the
    /// customer needs only <see cref="CustomersCreate"/>.
    /// </summary>
    public const string CustomersOpeningBalance = "customers.opening_balance";
    /// <summary>
    /// Narrow a customer statement to sales or to rentals (owner, 2026-10-10: "the admin chooses one
    /// of them to show in the report"). The whole statement needs only <see cref="CustomersAccountsView"/>.
    /// </summary>
    public const string CustomersAccountsFilter = "customers.accounts.filter";
    /// <summary>
    /// Open the copies of identity documents kept with a customer or a rental contract: their list,
    /// their files and their names (owner, 2026-10-25: "a separate explicit permission", not granted
    /// with <see cref="CustomersView"/>). The Admin holds it; any other role only if it is given.
    /// Adding a copy at the counter needs no more than before.
    /// </summary>
    public const string CustomersDocumentsView = "customers.documents.view";

    // ---------- rentals ----------
    public const string RentalsView = "rentals.view";
    public const string RentalsCreate = "rentals.create";
    public const string RentalsEdit = "rentals.edit";
    public const string RentalsIssue = "rentals.issue";
    public const string RentalsReturn = "rentals.return";
    public const string RentalsClose = "rentals.close";
    public const string RentalsSettings = "rentals.settings";
    /// <summary>
    /// See cancelled contracts — in the list, by id and in print. Everyone else stops seeing a
    /// contract the moment it is cancelled (owner, 2026-10-06). Cancelling itself needs no key of
    /// its own: whoever may raise or edit a contract may call it off before its exit note.
    /// </summary>
    public const string RentalsCancelled = "rentals.cancelled";

    // ---------- inventory ----------
    public const string ProductsView = "products.view";
    public const string ProductsManage = "products.manage";
    public const string InventoryView = "inventory.view";
    public const string InventoryAdjust = "inventory.adjust";
    public const string InventoryReceive = "inventory.receive";
    public const string InventoryTransfer = "inventory.transfer";
    /// <summary>Goods lost in transit are written off at the source by a manager, with a reason (owner, 2026-10-12).</summary>
    public const string InventoryTransferWriteOff = "inventory.transfer.write_off";
    public const string InventoryOpening = "inventory.opening";
    public const string StockViewOtherBranches = Permissions.ViewOtherBranchStock;
    public const string GiftCardsView = "gift_cards.view";
    public const string GiftCardsManage = "gift_cards.manage";

    // ---------- finance ----------
    public const string TreasuryCashBox = Permissions.CashBox;
    public const string ReceiptsView = "finance.receipts.view";
    public const string ReceiptsCreate = "finance.receipts.create";
    public const string ReceiptsSettle = "finance.receipts.settle";
    /// <summary>Confirm a receipt one recorded oneself (security review, 2026-10-10): the Admin's, as approve_own is.</summary>
    public const string ReceiptsSettleOwn = "finance.receipts.settle_own";
    public const string InvoicesView = "finance.invoices.view";
    public const string InvoicesCreate = "finance.invoices.create";
    /// <summary>Issuing an invoice without tax (owner, 2026-10-10).</summary>
    public const string InvoicesUntaxed = "finance.invoices.untaxed";
    public const string VouchersView = "finance.vouchers.view";
    public const string VouchersCreate = "finance.vouchers.create";
    public const string VouchersPay = "finance.vouchers.pay";
    public const string VouchersCancel = "finance.vouchers.cancel";
    public const string VouchersApprove = "finance.vouchers.approve";
    public const string VouchersApproveOwn = "finance.vouchers.approve_own";
    public const string Collections = "finance.collections";
    public const string CashBoxesView = "finance.cash_boxes.view";
    public const string Remit = "finance.remit";
    public const string FinanceSettings = "finance.settings";
    /// <summary>
    /// Set a supplier's opening balance after registration — once, when it was left empty — and
    /// correct one with a written reason (owner, 2026-10-10, the customers' rules). Registering a
    /// supplier with its balance needs only what registering one needs.
    /// </summary>
    public const string SuppliersOpeningBalance = "suppliers.opening_balance";

    // ---------- accounting ----------
    public const string AccountingView = "accounting.view";
    public const string AccountingManage = "accounting.manage";
    public const string ReportsAccounting = "reports.accounting";
    /// <summary>
    /// Close a month of the books, or reopen one with a reason (CLO-01). The Admin's alone at first (owner, 2026-11-01);
    /// it can later be granted to the accountant's own login.
    /// </summary>
    public const string AccountingClose = "accounting.close";

    // ---------- payroll ----------
    /// <summary>
    /// Read the company's payroll rules (<c>GET /api/hr/settings</c>) — and nothing else: no
    /// employee, wage, run or payment. Every Manager reads them today; the attendance sheet needs
    /// them. Named so nobody takes it for payroll access (the owner, 2026-10-03).
    /// </summary>
    public const string PayrollSettingsView = "payroll.settings.view";
    public const string PayrollManage = Permissions.ManagePayroll;
    public const string PayrollEditStatutory = Permissions.EditPayrollStatutory;
    public const string HrAttendance = Permissions.EnterAttendance;

    // ---------- manufacturing (Phase 1: configuration only) ----------
    /// <summary>
    /// See the manufacturing configuration: the production lines the caller may reach, their stock,
    /// the bills of materials, the units, the job roles and the stage templates. A line is still only
    /// visible to company scope, to its members and to users of its location (ManufacturingAccess).
    /// </summary>
    public const string ManufacturingView = "manufacturing.view";
    /// <summary>
    /// Run production lines: create and edit them and name their members (company scope only), and
    /// assign their employees and products (company scope, or a manager of that line).
    /// </summary>
    public const string ManufacturingLinesManage = "manufacturing.lines.manage";
    /// <summary>Write bills of materials: drafts, their lines and stages, and new versions.</summary>
    public const string ManufacturingBomManage = "manufacturing.bom.manage";
    /// <summary>
    /// Activate and retire a BOM version — the decision that a recipe is the one production follows.
    /// Separate from writing one, so the person who drafts a recipe need not be the one who approves it.
    /// </summary>
    public const string ManufacturingBomApprove = "manufacturing.bom.approve";
    /// <summary>Units and conversions, job roles, stage templates and each product's manufacturing role and units.</summary>
    public const string ManufacturingSettings = "manufacturing.settings";

    // ---------- manufacturing (Phase 2: production orders) ----------
    /// <summary>Create a production order on a line the caller reaches; its materials are reserved there.</summary>
    public const string ManufacturingOrdersCreate = "manufacturing.orders.create";
    /// <summary>Start an order although materials are short, with a reason — never by consuming what is not there.</summary>
    public const string ManufacturingOrdersStartShort = "manufacturing.orders.start_short";
    /// <summary>Hold, resume and cancel an order, release part of its reservation and record its expenses.</summary>
    public const string ManufacturingOrdersManage = "manufacturing.orders.manage";
    /// <summary>Issue, return, scrap and record the consumption of an order's materials.</summary>
    public const string ManufacturingMaterialsMove = "manufacturing.materials.move";
    /// <summary>Write and submit a line's daily production record, book hours on it and request completion.</summary>
    public const string ManufacturingLogSubmit = "manufacturing.log.submit";
    /// <summary>
    /// Approve or return a daily production record, as a production manager of the line — never one
    /// the caller submitted.
    /// </summary>
    public const string ManufacturingLogApprove = "manufacturing.log.approve";
    /// <summary>Book hours above what the employee's attendance shows for the day, with a reason that is kept.</summary>
    public const string ManufacturingLabourOverride = "manufacturing.labour.override";
    /// <summary>Release finished output to the line's stock, as a production manager of the line.</summary>
    public const string ManufacturingOutputRelease = "manufacturing.output.release";
    /// <summary>Approve or refuse an order's completion, as a production manager of the line who did not request it.</summary>
    public const string ManufacturingCompletionApprove = "manufacturing.completion.approve";
    /// <summary>The manufacturing reports: materials sold at the till, and the movements awaiting posting.</summary>
    public const string ManufacturingReportsView = "manufacturing.reports.view";

    // ---------- manufacturing (costing, Phase A: read-only previews) ----------
    /// <summary>
    /// See what orders cost: the cost sheet, the work in progress and the labour absorbed. Order totals
    /// only — an employee's rate or pay also needs payroll.manage (costing plan §3.1).
    /// </summary>
    public const string ManufacturingCostsView = "manufacturing.costs.view";
    /// <summary>Create a provisional manufacturing-cost history record on a reachable completed order.</summary>
    public const string ManufacturingCostSnapshotCreate = "manufacturing.costs.snapshot.create";
    /// <summary>See per-employee payroll-derived manufacturing rates; also requires payroll.manage.</summary>
    public const string ManufacturingCostsRatesView = "manufacturing.costs.rates.view";

    // ---------- maintenance (Phase M0: the keys and the settings; docs/maintenance/plan.md §6.1) ----------
    /// <summary>See jobs and Waiting for repair at reachable branches (from M1).</summary>
    public const string MaintenanceView = "maintenance.view";
    /// <summary>Open and edit jobs, issue and return parts, link outside work, book hours (from M1).</summary>
    public const string MaintenanceJobsManage = "maintenance.jobs.manage";
    /// <summary>Complete a job (units back to available or to scrap) and cancel one (from M1).</summary>
    public const string MaintenanceJobsClose = "maintenance.jobs.close";
    /// <summary>Book hours on a job beyond the day's attendance, with a reason (from M3).</summary>
    public const string MaintenanceLabourOverride = "maintenance.labour.override";
    /// <summary>Price customer jobs and keep the repair rates (from M5).</summary>
    public const string MaintenancePricing = "maintenance.pricing";
    /// <summary>A job's cost and the profit reports; per-job labour money follows CostPrivacy (from M4).</summary>
    public const string MaintenanceCostsView = "maintenance.costs.view";
    /// <summary>The operational maintenance settings, MS-01 to MS-26. MS-30 to MS-40 are accounting.manage's.</summary>
    public const string MaintenanceSettings = "maintenance.settings";

    // ---------- administration ----------
    public const string UsersManage = "users.manage";
    public const string RolesManage = "roles.manage";
    public const string DocumentsManage = "documents.manage";
    public const string BranchesView = "branches.view";
    public const string BranchesManage = "branches.manage";
    public const string DevicesManage = "devices.manage";
    public const string CompanySettings = "company.settings";
    public const string SubscriptionUsage = "subscription.usage";

    public static readonly IReadOnlyList<PermissionDef> All =
    [
        new(SalesCreate, Sales), new(SalesView, Sales), new(SalesRefund, Sales), new(ReportsSales, Sales),
        new(SalesUntaxed, Sales),

        new(CustomersView, Customers), new(CustomersCreate, Customers), new(CustomersEdit, Customers),
        new(CustomersDelete, Customers), new(CustomersAccountsView, Customers), new(CustomersOpeningBalance, Customers), new(CustomersAccountsFilter, Customers),
        new(CustomersDocumentsView, Customers),

        new(RentalsView, Rentals), new(RentalsCreate, Rentals), new(RentalsEdit, Rentals), new(RentalsIssue, Rentals),
        new(RentalsReturn, Rentals), new(RentalsClose, Rentals), new(RentalsSettings, Rentals),
        new(RentalsCancelled, Rentals),

        new(ProductsView, Inventory), new(ProductsManage, Inventory), new(InventoryView, Inventory),
        new(InventoryAdjust, Inventory), new(InventoryReceive, Inventory), new(InventoryTransfer, Inventory), new(InventoryTransferWriteOff, Inventory),
        new(InventoryOpening, Inventory), new(StockViewOtherBranches, Inventory),
        new(GiftCardsView, Inventory), new(GiftCardsManage, Inventory),

        new(TreasuryCashBox, Finance), new(ReceiptsView, Finance), new(ReceiptsCreate, Finance), new(ReceiptsSettle, Finance), new(ReceiptsSettleOwn, Finance),
        new(InvoicesView, Finance), new(InvoicesCreate, Finance), new(InvoicesUntaxed, Finance), new(VouchersView, Finance), new(VouchersCreate, Finance),
        new(VouchersPay, Finance), new(VouchersCancel, Finance), new(VouchersApprove, Finance),
        new(VouchersApproveOwn, Finance), new(Collections, Finance), new(CashBoxesView, Finance), new(Remit, Finance),
        new(FinanceSettings, Finance), new(SuppliersOpeningBalance, Finance),

        new(AccountingView, Accounting), new(AccountingManage, Accounting), new(ReportsAccounting, Accounting),
        new(AccountingClose, Accounting),

        new(PayrollSettingsView, Payroll), new(PayrollManage, Payroll), new(PayrollEditStatutory, Payroll),
        new(HrAttendance, Payroll),

        new(ManufacturingView, Manufacturing), new(ManufacturingLinesManage, Manufacturing), new(ManufacturingBomManage, Manufacturing),
        new(ManufacturingBomApprove, Manufacturing), new(ManufacturingSettings, Manufacturing),
        new(ManufacturingOrdersCreate, Manufacturing), new(ManufacturingOrdersStartShort, Manufacturing),
        new(ManufacturingOrdersManage, Manufacturing), new(ManufacturingMaterialsMove, Manufacturing),
        new(ManufacturingLogSubmit, Manufacturing), new(ManufacturingLogApprove, Manufacturing),
        new(ManufacturingLabourOverride, Manufacturing), new(ManufacturingOutputRelease, Manufacturing),
        new(ManufacturingCompletionApprove, Manufacturing), new(ManufacturingReportsView, Manufacturing),
        new(ManufacturingCostsView, Manufacturing), new(ManufacturingCostSnapshotCreate, Manufacturing), new(ManufacturingCostsRatesView, Manufacturing),

        new(MaintenanceView, Maintenance), new(MaintenanceJobsManage, Maintenance), new(MaintenanceJobsClose, Maintenance),
        new(MaintenanceLabourOverride, Maintenance), new(MaintenancePricing, Maintenance), new(MaintenanceCostsView, Maintenance),
        new(MaintenanceSettings, Maintenance),

        new(UsersManage, Administration), new(RolesManage, Administration), new(DocumentsManage, Administration),
        new(BranchesView, Administration), new(BranchesManage, Administration), new(DevicesManage, Administration),
        new(CompanySettings, Administration), new(SubscriptionUsage, Administration),
    ];

    private static readonly HashSet<string> Keys = All.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>True for a key of the catalogue, spelled exactly.</summary>
    public static bool Contains(string? key) => key is not null && Keys.Contains(key);

    /// <summary>
    /// A narrower key, by the wider key that already opens everything it opens. The one case today is
    /// <c>hr.attendance</c>: every route that accepts it also accepts <c>payroll.manage</c> (a test holds
    /// that), and the Admin holds the second and, deliberately, not the first (<see cref="AdminBaseline"/>).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> CoveredBy = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [HrAttendance] = PayrollManage,
    };

    /// <summary>
    /// Whether a holder of <paramref name="held"/> may give or take away <paramref name="key"/>: they hold
    /// it, or a key that opens everything it opens. Nobody gives more than their own authority (AUTH-1, §17.3).
    /// </summary>
    public static bool Covers(IReadOnlySet<string> held, string key) =>
        held.Contains(key) || (CoveredBy.TryGetValue(key, out var wider) && held.Contains(wider));

    /// <summary>
    /// The catalogue's own consistency: no key twice, every module known. The Admin baseline is
    /// checked separately (<see cref="AdminBaseline.Validate"/>), naming the key it lacks.
    /// </summary>
    public static void Validate()
    {
        var twice = All.GroupBy(p => p.Key, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (twice.Count > 0)
            throw new InvalidOperationException($"Permission '{twice[0]}' is listed twice in PermissionCatalog.All.");
        var stray = All.FirstOrDefault(p => !Modules.Contains(p.Module));
        if (stray is not null)
            throw new InvalidOperationException($"Permission '{stray.Key}' names an unknown module '{stray.Module}'.");
    }
}
