using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Authorization;
using ParadiERP.Infrastructure.Catalog;
using ParadiERP.Infrastructure.Ledger;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ParadiERP.Infrastructure.Persistence;

public class PosDbContext : DbContext
{
    public PosDbContext(DbContextOptions<PosDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<RentalDailyPricingCorrection> RentalDailyPricingCorrections => Set<RentalDailyPricingCorrection>();

    // Roles and permissions (AUTH-1, REDESIGN_PLAN.md §17). Server-only: none derives from
    // SyncEntity. Step 1 writes and guards them; no request is decided by them yet.
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserPermission> UserPermissions => Set<UserPermission>();
    public DbSet<AuthorizationReportEntry> AuthorizationReport => Set<AuthorizationReportEntry>();
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductStock> ProductStocks => Set<ProductStock>();
    public DbSet<Customer> Customers => Set<Customer>();
    // Who signs for a company customer, as a dated history. Back-office only: not a SyncEntity,
    // so not in a pull — a till reads the customer's name and points and nothing else.
    public DbSet<CustomerRepresentative> CustomerRepresentatives => Set<CustomerRepresentative>();
    // Where a customer takes hired goods. Back-office only for the same reason.
    public DbSet<CustomerSite> CustomerSites => Set<CustomerSite>();
    public DbSet<CustomerAttachment> CustomerAttachments => Set<CustomerAttachment>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleLine> SaleLines => Set<SaleLine>();
    public DbSet<SalePayment> SalePayments => Set<SalePayment>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<SyncCounter> SyncCounters => Set<SyncCounter>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<StockValuationOpening> StockValuationOpenings => Set<StockValuationOpening>();
    public DbSet<StockValuationSettings> StockValuationSettings => Set<StockValuationSettings>();
    public DbSet<StockValuationSwitch> StockValuationSwitches => Set<StockValuationSwitch>();
    public DbSet<ManufacturingCostingSettings> ManufacturingCostingSettings => Set<ManufacturingCostingSettings>();
    public DbSet<ManufacturingAccountMapping> ManufacturingAccountMappings => Set<ManufacturingAccountMapping>();
    public DbSet<ManufacturingMonthPosting> ManufacturingMonthPostings => Set<ManufacturingMonthPosting>();
    public DbSet<MaintenanceMonthPosting> MaintenanceMonthPostings => Set<MaintenanceMonthPosting>();
    public DbSet<MaintenanceSettings> MaintenanceSettings => Set<MaintenanceSettings>();
    // REN-12, Phase M7a: the fixed-asset register's settings, lives, events and month postings; server-only, never synced.
    public DbSet<FixedAssetSettings> FixedAssetSettings => Set<FixedAssetSettings>();
    public DbSet<FixedAssetLife> FixedAssetLives => Set<FixedAssetLife>();
    public DbSet<FixedAssetMethodChoice> FixedAssetMethods => Set<FixedAssetMethodChoice>();
    public DbSet<FixedAssetMovement> FixedAssetMovements => Set<FixedAssetMovement>();
    public DbSet<FixedAssetMonthPosting> FixedAssetMonthPostings => Set<FixedAssetMonthPosting>();
    // Maintenance jobs (Phase M1): server-only, never a SyncEntity — a till never pulls them.
    public DbSet<MaintenanceJob> MaintenanceJobs => Set<MaintenanceJob>();
    public DbSet<MaintenanceRepairRate> MaintenanceRepairRates => Set<MaintenanceRepairRate>();
    public DbSet<MaintenanceWorkshopMember> MaintenanceWorkshopMembers => Set<MaintenanceWorkshopMember>();
    public DbSet<MaintenanceJobEvent> MaintenanceJobEvents => Set<MaintenanceJobEvent>();
    // Maintenance parts and outside work (Phase M2): server-only like the jobs.
    public DbSet<MaintenanceJobPart> MaintenanceJobParts => Set<MaintenanceJobPart>();
    public DbSet<MaintenanceJobExpense> MaintenanceJobExpenses => Set<MaintenanceJobExpense>();
    // Phase M7d (MS-30): the frozen credit lines of a repair capitalised at completion.
    public DbSet<MaintenanceJobCapitalisedLine> MaintenanceJobCapitalisedLines => Set<MaintenanceJobCapitalisedLine>();
    public DbSet<PaymentVoucherBonusLine> PaymentVoucherBonusLines => Set<PaymentVoucherBonusLine>();
    // Transfers between two of the company's locations (owner, 2026-10-12). Server-only: not a
    // SyncEntity, so no till pulls them — the transfer workflow is back office only.
    public DbSet<StockTransfer> StockTransfers => Set<StockTransfer>();
    public DbSet<StockTransferLine> StockTransferLines => Set<StockTransferLine>();
    public DbSet<StockTransferEvent> StockTransferEvents => Set<StockTransferEvent>();
    public DbSet<GiftCard> GiftCards => Set<GiftCard>();
    public DbSet<GiftCardMovement> GiftCardMovements => Set<GiftCardMovement>();
    public DbSet<LoyaltyMovement> LoyaltyMovements => Set<LoyaltyMovement>();
    public DbSet<IntegrationOutbox> IntegrationOutbox => Set<IntegrationOutbox>();
    public DbSet<PaymentMethodDef> PaymentMethodDefs => Set<PaymentMethodDef>();
    public DbSet<CashBox> CashBoxes => Set<CashBox>();
    public DbSet<CashMovement> CashMovements => Set<CashMovement>();

    // The accounting spine. Server-only: none of these derives from SyncEntity and none of
    // them is in a pull, because the ledger never replicates to a till (design §5.9).
    public DbSet<LedgerAccount> LedgerAccounts => Set<LedgerAccount>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<LedgerEntryLine> LedgerEntryLines => Set<LedgerEntryLine>();
    public DbSet<BookClosing> BookClosings => Set<BookClosing>();

    // Renting. Server-only for the same reason and by the same mechanism: none of these
    // derives from SyncEntity, so none of them is in a pull (design §5.8/§5.9). A contract is
    // a signed back-office document, and the largest new module in the product therefore does
    // not touch the sync engine at all.
    public DbSet<RentalContract> RentalContracts => Set<RentalContract>();
    public DbSet<RentalContractLine> RentalContractLines => Set<RentalContractLine>();
    public DbSet<IssueNote> IssueNotes => Set<IssueNote>();
    public DbSet<IssueNoteLine> IssueNoteLines => Set<IssueNoteLine>();
    public DbSet<ReturnNote> ReturnNotes => Set<ReturnNote>();
    public DbSet<ReturnNoteLine> ReturnNoteLines => Set<ReturnNoteLine>();
    public DbSet<ReturnNoteLineAllocation> ReturnNoteLineAllocations => Set<ReturnNoteLineAllocation>();
    public DbSet<ContractExtension> ContractExtensions => Set<ContractExtension>();
    public DbSet<RentalCharge> RentalCharges => Set<RentalCharge>();
    public DbSet<RentalContractAttachment> RentalContractAttachments => Set<RentalContractAttachment>();

    // Invoices, what was received against them and what was given back. Server-only for the
    // third time and by the same mechanism: none of these derives from SyncEntity, so none of
    // them is in a pull. The debtor book of a business whose customers hold million-dinar
    // limits is the last thing that belongs on a cashier's device.
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<ReceiptVoucher> ReceiptVouchers => Set<ReceiptVoucher>();
    public DbSet<ReceiptAllocation> ReceiptAllocations => Set<ReceiptAllocation>();
    // M7b: an advance held on 419 applied to an invoice or released to 411, and the accountant's choices for advances.
    public DbSet<CustomerAdvanceApplication> CustomerAdvanceApplications => Set<CustomerAdvanceApplication>();
    public DbSet<ReceivableSettings> ReceivableSettings => Set<ReceivableSettings>();
    public DbSet<CreditNote> CreditNotes => Set<CreditNote>();
    public DbSet<CustomerOpeningCorrection> CustomerOpeningCorrections => Set<CustomerOpeningCorrection>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<SupplierOpeningCorrection> SupplierOpeningCorrections => Set<SupplierOpeningCorrection>();
    public DbSet<CreditNoteLine> CreditNoteLines => Set<CreditNoteLine>();

    // Money going out: the mirror of ReceiptVouchers, server-only by the same mechanism.
    public DbSet<PaymentVoucher> PaymentVouchers => Set<PaymentVoucher>();

    // Personnel and payroll (docs/07 §5.4). Server-only for the fourth time and by the same
    // mechanism: none of these derives from SyncEntity, so none of them is in a pull. Salaries,
    // identity numbers and advances are the last thing that belongs on a cashier's device, and
    // the base type is the only thing standing between them and one — HrArchitectureTests pins it.
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<EmployeeAllowance> EmployeeAllowances => Set<EmployeeAllowance>();
    public DbSet<AttendanceDeclaration> AttendanceDeclarations => Set<AttendanceDeclaration>();
    public DbSet<AttendanceDeclarationOvertime> AttendanceDeclarationOvertimes => Set<AttendanceDeclarationOvertime>();
    public DbSet<AttendanceDay> AttendanceDays => Set<AttendanceDay>();
    public DbSet<AttendanceDayOvertime> AttendanceDayOvertimes => Set<AttendanceDayOvertime>();
    public DbSet<ClockEvent> ClockEvents => Set<ClockEvent>();
    public DbSet<OvertimeEntry> OvertimeEntries => Set<OvertimeEntry>();
    public DbSet<OvertimeEntryChange> OvertimeEntryChanges => Set<OvertimeEntryChange>();
    public DbSet<WageAdvance> WageAdvances => Set<WageAdvance>();
    public DbSet<PayrollRun> PayrollRuns => Set<PayrollRun>();
    public DbSet<PayrollRunLine> PayrollRunLines => Set<PayrollRunLine>();
    public DbSet<PayrollRunLineElement> PayrollRunLineElements => Set<PayrollRunLineElement>();
    public DbSet<PayrollAdjustment> PayrollAdjustments => Set<PayrollAdjustment>();
    public DbSet<WagePayment> WagePayments => Set<WagePayment>();
    public DbSet<WagePaymentLine> WagePaymentLines => Set<WagePaymentLine>();
    public DbSet<PayrollStatutoryPayment> PayrollStatutoryPayments => Set<PayrollStatutoryPayment>();

    // The predecessor of every printed wording the admin has replaced. Server-only, like the
    // texts themselves: StoreDto deliberately does not carry DocumentTexts, so a till has neither
    // the current wording nor its history.
    public DbSet<DocumentTextChange> DocumentTextChanges => Set<DocumentTextChange>();

    // The payroll figures, one row, server-only like everything else in payroll. Read whole and
    // written whole through PayrollSettingsStore, never queried into.
    public DbSet<PayrollConfiguration> PayrollConfigurations => Set<PayrollConfiguration>();

    // Manufacturing, Phase 1 (docs/manufacturing/phase-1.md): configuration only, server-only. Nothing
    // here moves or values stock; a line's stock is its location's rows in ProductStocks.
    public DbSet<UnitOfMeasure> UnitsOfMeasure => Set<UnitOfMeasure>();
    public DbSet<ProductUnit> ProductUnits => Set<ProductUnit>();
    public DbSet<ManufacturingJobRole> ManufacturingJobRoles => Set<ManufacturingJobRole>();
    public DbSet<ProductionLine> ProductionLines => Set<ProductionLine>();
    public DbSet<ProductionLineMember> ProductionLineMembers => Set<ProductionLineMember>();
    public DbSet<ProductionLineEmployee> ProductionLineEmployees => Set<ProductionLineEmployee>();
    public DbSet<ProductionLineProduct> ProductionLineProducts => Set<ProductionLineProduct>();
    public DbSet<StageTemplate> StageTemplates => Set<StageTemplate>();
    public DbSet<StageTemplateStep> StageTemplateSteps => Set<StageTemplateStep>();
    public DbSet<StageTemplateStepRole> StageTemplateStepRoles => Set<StageTemplateStepRole>();
    public DbSet<BillOfMaterials> BillsOfMaterials => Set<BillOfMaterials>();
    public DbSet<BomLine> BomLines => Set<BomLine>();
    public DbSet<BomStage> BomStages => Set<BomStage>();
    public DbSet<BomStageRole> BomStageRoles => Set<BomStageRole>();
    public DbSet<SpecAttribute> SpecAttributes => Set<SpecAttribute>();
    public DbSet<ProductSpec> ProductSpecs => Set<ProductSpec>();
    public DbSet<BomLineSpec> BomLineSpecs => Set<BomLineSpec>();
    public DbSet<ProductSaleStatusChange> ProductSaleStatusChanges => Set<ProductSaleStatusChange>();
    public DbSet<ManufacturingSaleException> ManufacturingSaleExceptions => Set<ManufacturingSaleException>();

    // Manufacturing, Phase 2 (docs/manufacturing/phase-2.md): production orders and what happens on them.
    // Server-only. Stock moves through ProductStocks and StockMovements as everywhere else; nothing posts.
    public DbSet<ProductionOrder> ProductionOrders => Set<ProductionOrder>();
    public DbSet<ProductionOrderMaterial> ProductionOrderMaterials => Set<ProductionOrderMaterial>();
    public DbSet<ProductionOrderStage> ProductionOrderStages => Set<ProductionOrderStage>();
    public DbSet<ProductionOrderEvent> ProductionOrderEvents => Set<ProductionOrderEvent>();
    public DbSet<ProductionOrderExpense> ProductionOrderExpenses => Set<ProductionOrderExpense>();
    public DbSet<ManufacturingCostSheet> ManufacturingCostSheets => Set<ManufacturingCostSheet>();
    public DbSet<ManufacturingCostElementLine> ManufacturingCostElementLines => Set<ManufacturingCostElementLine>();
    public DbSet<ManufacturingMaterialCostSnapshot> ManufacturingMaterialCostSnapshots => Set<ManufacturingMaterialCostSnapshot>();
    public DbSet<ManufacturingLabourRateSnapshot> ManufacturingLabourRateSnapshots => Set<ManufacturingLabourRateSnapshot>();
    public DbSet<ManufacturingExpenseAllocationSnapshot> ManufacturingExpenseAllocationSnapshots => Set<ManufacturingExpenseAllocationSnapshot>();
    public DbSet<ManufacturingScrapRecoverySnapshot> ManufacturingScrapRecoverySnapshots => Set<ManufacturingScrapRecoverySnapshot>();
    public DbSet<ManufacturingReleaseValueSnapshot> ManufacturingReleaseValueSnapshots => Set<ManufacturingReleaseValueSnapshot>();
    public DbSet<ManufacturingLateCost> ManufacturingLateCosts => Set<ManufacturingLateCost>();
    public DbSet<MaterialMovement> MaterialMovements => Set<MaterialMovement>();
    public DbSet<MaterialMovementLine> MaterialMovementLines => Set<MaterialMovementLine>();
    public DbSet<MaterialConsumption> MaterialConsumptions => Set<MaterialConsumption>();
    public DbSet<OutputRelease> OutputReleases => Set<OutputRelease>();
    public DbSet<ProductionLog> ProductionLogs => Set<ProductionLog>();
    public DbSet<ProductionLogRow> ProductionLogRows => Set<ProductionLogRow>();
    public DbSet<LaborBooking> LaborBookings => Set<LaborBooking>();
    public DbSet<LaborBookingChange> LaborBookingChanges => Set<LaborBookingChange>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // Fixed precision so SQLite and SQL Server agree on money and quantities.
        // NOTE: SQLite stores decimal as TEXT and ignores this, so decimal comparisons must
        // not be pushed to the SQLite server side — every aggregate in this codebase runs
        // client-side after ToListAsync for that reason.
        foreach (var property in b.Model.GetEntityTypes()
                     .SelectMany(t => t.GetProperties())
                     .Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
        {
            property.SetPrecision(18);
            property.SetScale(3);
        }

        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Username).IsUnique();
            e.HasIndex(x => x.Version);
            // A cashier's terminal binding. Restrict, not SetNull: a branch that still binds a
            // login is in use, and silently unbinding its cashiers on delete would widen their
            // reach to every terminal — the opposite of what deleting a branch should do.
            // No navigation property on purpose: User is the auth entity and is replicated;
            // the login check compares two ids and needs no branch row.
            e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
            // The user's role (AUTH-1). Restrict: a role with members cannot be deleted from under them.
            e.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
            // The employee the account belongs to (owner, 2026-10-05). Restrict: employees are never
            // deleted, and one with an account is in use.
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            // One active account per employee. An old, switched-off or deleted account does not count,
            // so a person who comes back can be given a new one. The endpoint refuses in words first;
            // this is the backstop when two administrators save at the same moment.
            e.HasIndex(x => x.EmployeeId, "UX_Users_OneActiveAccountPerEmployee")
                .IsUnique()
                .HasFilter(Database.IsSqlServer()
                    ? "[EmployeeId] IS NOT NULL AND [IsActive] = 1 AND [IsDeleted] = 0"
                    : "\"EmployeeId\" IS NOT NULL AND \"IsActive\" = 1 AND \"IsDeleted\" = 0");
            // The Login ID, unique company-wide. Null on legacy accounts; both providers allow any
            // number of NULLs under it (SQL Server through the filter EF writes for a nullable column).
            e.HasIndex(x => x.LoginId).IsUnique();
        });

        b.Entity<Role>(e =>
        {
            // The system roles' stable keys; null for every custom role, which a unique index allows.
            e.HasIndex(x => x.Key).IsUnique();
            // Unique as the screens compare names — case and spacing aside — on every provider (RoleNames).
            e.HasIndex(x => x.NormalizedName).IsUnique();
        });

        b.Entity<RolePermission>(e =>
        {
            e.HasKey(x => new { x.RoleId, x.PermissionKey });
            e.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UserPermission>(e =>
        {
            e.HasKey(x => new { x.UserId, x.PermissionKey });
            // "Who holds this key" — the question the roles screen asks.
            e.HasIndex(x => x.PermissionKey);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AuthorizationReportEntry>(e =>
        {
            e.ToTable("AuthorizationReport");
            e.HasIndex(x => new { x.UserId, x.PermissionKey, x.Reason });
        });

        b.Entity<Store>(e =>
        {
            e.HasIndex(x => x.Version);
            e.Property(x => x.FiscalIds)
                .HasMaxLength(2000)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null) ?? new(),
                    new ValueComparer<Dictionary<string, string>>(
                        (a, b) => a!.Count == b!.Count && !a.Except(b).Any(),
                        v => v.Aggregate(0, (hash, pair) => hash ^ HashCode.Combine(pair.Key, pair.Value)),
                        v => new Dictionary<string, string>(v)));
            // The printed-document texts, JSON like FiscalIds but nullable and unbounded: eight
            // kinds × the enabled languages × up to 4,000 characters does not fit a varchar, and
            // NULL — every row that predates the column — reads as "nothing configured". EF
            // never runs a converter on NULL, so the property is nullable rather than defaulted
            // to an empty dictionary that would only exist in memory. Compared by serialised
            // form: a nested dictionary has no cheap structural equality, and the row is
            // written from one admin screen, so a false "modified" costs one UPDATE at most.
            e.Property(x => x.DocumentTexts)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(v, (JsonSerializerOptions?)null),
                    new ValueComparer<Dictionary<string, Dictionary<string, string>>?>(
                        (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
                        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
                        v => v == null ? null : JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(JsonSerializer.Serialize(v, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null)));
            // The named contract wordings (PinSay #302): a short ordered list, JSON for the same
            // reasons as the texts beside it, compared through the same serialised form.
            e.Property(x => x.ContractWordings)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<ContractWording>>(v, (JsonSerializerOptions?)null),
                    new ValueComparer<List<ContractWording>?>(
                        (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
                        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
                        v => v == null ? null : JsonSerializer.Deserialize<List<ContractWording>>(JsonSerializer.Serialize(v, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null)));
        });

        b.Entity<Branch>(e =>
        {
            e.HasIndex(x => x.Version);
            // The three-digit location code (owner, 2026-10-05). Drawn by BranchCodeAssignment on the
            // first save; the index is what keeps two locations from ever sharing one.
            e.HasIndex(x => x.Code).IsUnique();
            // One primary per kind — one primary branch, one primary warehouse (owner, 2026-10-05) —
            // held by the database itself, not only by BranchEndpoints. Unique on the kind among the
            // live primaries. The filter is plain SQL that SQLite and SQL Server both read as written.
            e.HasIndex(x => x.IsWarehouse)
                .IsUnique()
                .HasFilter("IsMain = 1 AND IsDeleted = 0")
                .HasDatabaseName("IX_Branches_OnePrimaryPerKind");
            e.Ignore(x => x.IsMainBranch);
            e.Ignore(x => x.IsMainWarehouse);
            // Mapped exactly like Store.FiscalIds. The migration gives every existing row '{}'
            // (as the Store column got), and the reader tolerates NULL or blank as well, so a
            // hand-inserted row can never throw the branch list.
            e.Property(x => x.FiscalIds)
                .HasMaxLength(2000)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => string.IsNullOrWhiteSpace(v)
                        ? new()
                        : JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null) ?? new(),
                    new ValueComparer<Dictionary<string, string>>(
                        (a, b) => a!.Count == b!.Count && !a.Except(b).Any(),
                        v => v.Aggregate(0, (hash, pair) => hash ^ HashCode.Combine(pair.Key, pair.Value)),
                        v => new Dictionary<string, string>(v)));
        });

        b.Entity<Category>(e => e.HasIndex(x => x.Version));

        b.Entity<Product>(e =>
        {
            e.HasIndex(x => x.Version);
            e.HasIndex(x => x.Sku).IsUnique();
            e.HasIndex(x => x.Barcode);
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<ProductStock>(e =>
        {
            // One stock row per (product, branch) — the invariant the whole per-branch
            // model depends on.
            e.HasIndex(x => new { x.ProductId, x.BranchId }).IsUnique();
            // Derived from the four buckets; storing it would let it drift from them.
            e.Ignore(x => x.OwnedQty);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Customer>(e =>
        {
            e.HasIndex(x => x.Version);
            e.HasIndex(x => x.Code).IsUnique();
            // Mapped exactly like Branch.FiscalIds: '{}' on every existing row, and NULL or blank
            // read as none, so a hand-inserted row can never throw the customer list.
            e.Property(x => x.FiscalIds)
                .HasMaxLength(2000)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => string.IsNullOrWhiteSpace(v)
                        ? new()
                        : JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null) ?? new(),
                    new ValueComparer<Dictionary<string, string>>(
                        (a, b) => a!.Count == b!.Count && !a.Except(b).Any(),
                        v => v.Aggregate(0, (hash, pair) => hash ^ HashCode.Combine(pair.Key, pair.Value)),
                        v => new Dictionary<string, string>(v)));
        });

        b.Entity<CustomerRepresentative>(e =>
        {
            // The "current representative" lookup: ToUtc null for one customer. History per
            // company is a handful of rows, so this one index serves the list too.
            // "Exactly one current" is enforced in code inside a write scope, not by a filtered
            // unique index over ToUtc: a unique index over a nullable column does not mean the
            // same thing on SQLite and SQL Server, and one rule written once beats one per provider.
            e.HasIndex(x => new { x.CustomerId, x.ToUtc });
            // Restrict, not cascade: customers are only ever soft-deleted, and the history of who
            // signed for a company is never lost — the same choice the contract and the invoice
            // make about the customer they name.
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<CustomerSite>(e =>
        {
            e.HasIndex(x => x.CustomerId);
            // Restrict, as for the representative history: a site a signed contract points at is
            // never lost, and customers are only ever soft-deleted anyway.
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<CustomerAttachment>(e =>
        {
            e.HasIndex(x => x.CustomerId);
            // Restrict, as for the sites: customers are only ever soft-deleted, so a scan is
            // never lost with its row.
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Sale>(e =>
        {
            // Receipt numbers are generated offline per terminal, so two tills legitimately
            // produce the same sequence. Uniqueness is per device, never global.
            e.HasIndex(x => new { x.DeviceCode, x.Reference }).IsUnique();
            e.HasIndex(x => new { x.DeviceCode, x.LocalSeq }).IsUnique();
            e.HasIndex(x => x.SoldAtUtc);
            e.HasIndex(x => x.BranchId);
            e.HasMany(x => x.Lines).WithOne(x => x.Sale!).HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Payments).WithOne(x => x.Sale!).HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<SaleLine>(e =>
        {
            e.HasIndex(x => x.ProductId);
            // A cost per unit can be a weighted average (C-01): kept as the stock movement keeps it, so a refund at its
            // sale's cost gives back exactly what went out.
            e.Property(x => x.UnitCost).HasPrecision(28, 10);
        });

        b.Entity<Device>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
            // A cashier's own till. Users are only ever soft-deleted, so Restrict never bites; one
            // till per cashier is kept by CashierTills, not by a filtered index the two providers
            // spell differently.
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<StockMovement>(e =>
        {
            e.HasIndex(x => x.ProductId);
            e.HasIndex(x => x.BranchId);
            e.HasIndex(x => x.SaleId);
            // "Show me the lines of this receiving note" is a lookup, not a scan.
            e.HasIndex(x => x.DocumentId);
            // The item movement report reads one article's history in date order.
            e.HasIndex(x => new { x.ProductId, x.CreatedAtUtc });
            // The weighted-average shadow replays every movement after its opening, in time order.
            e.HasIndex(x => x.CreatedAtUtc);
            // A unit cost can be an average (a production return goes back at its issues' mean).
            e.Property(x => x.UnitCost).HasPrecision(28, 10);
        });

        // Server-only, written once at start-up and read only by the weighted-average shadow (P0.4b).
        b.Entity<StockValuationOpening>(e =>
        {
            e.HasIndex(x => x.ProductId);
            e.Property(x => x.Qty).HasPrecision(28, 10);
            e.Property(x => x.UnitCost).HasPrecision(28, 10);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<StockValuationSettings>(e => e.Property(x => x.Id).ValueGeneratedNever());
        // Each change of how sales are costed (C-01), server-only, read newest first.
        b.Entity<StockValuationSwitch>(e => e.HasIndex(x => x.AtUtc));

        // Manufacturing costing choices and account map (Phase C1): server-only, one settings row, one row per slot.
        b.Entity<ManufacturingCostingSettings>(e => e.Property(x => x.Id).ValueGeneratedNever());
        // Maintenance › Settings (Phase M0): one row, every default the suggested answer; server-only, never synced.
        // Phase M7c, MS-27: no store default for GuaranteeMonths in the model (0, "no guarantee", must save as 0); the migration
        // gives a row saved before the column today's default of three months.
        b.Entity<MaintenanceSettings>(e => e.Property(x => x.Id).ValueGeneratedNever());
        // Maintenance jobs (Phase M1): numbered once, retried by their request id, held by a branch; their events append-only.
        b.Entity<MaintenanceJob>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.ClientRequestId).IsUnique();
            e.HasIndex(x => new { x.BranchId, x.Status });
            e.HasIndex(x => new { x.BranchId, x.ProductId, x.Status });
            e.HasIndex(x => x.ReturnNoteLineId);
            foreach (var p in new[] { nameof(MaintenanceJob.Qty), nameof(MaintenanceJob.RepairedQty), nameof(MaintenanceJob.ScrappedQty) })
                e.Property(p).HasPrecision(28, 10);
            e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<RentalContract>().WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ReturnNote>().WithMany().HasForeignKey(x => x.ReturnNoteId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ReturnNoteLine>().WithMany().HasForeignKey(x => x.ReturnNoteLineId).OnDelete(DeleteBehavior.Restrict);
            // Phase M5a: a customer job names its customer and, once invoiced, its one invoice (unique: an invoice bills
            // one job; SQLite holds NULLs distinct and SQL Server filters them out of the index).
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Invoice>().WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.InvoiceId).IsUnique();
            e.HasIndex(x => x.CustomerId);
            // Phase M7c: a free rework under guarantee names the customer job it redoes.
            e.HasOne<MaintenanceJob>().WithMany().HasForeignKey(x => x.ReworkOfJobId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ReworkOfJobId);
            // Phase M7d (MS-30): capitalised repairs are read by their completion month.
            e.HasIndex(x => x.CapitalisedAtUtc);
        });
        // MS-23 repair rates (Phase M5a): one per product, a suggestion only.
        b.Entity<MaintenanceRepairRate>(e =>
        {
            e.HasIndex(x => x.ProductId).IsUnique();
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });
        // MS-04's workshop list: one row per person and branch, server-only.
        b.Entity<MaintenanceWorkshopMember>(e =>
        {
            e.HasIndex(x => new { x.EmployeeId, x.BranchId }).IsUnique();
            e.HasIndex(x => x.BranchId);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<MaintenanceJobEvent>(e =>
        {
            e.HasIndex(x => new { x.JobId, x.AtUtc });
            e.HasOne<MaintenanceJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
        });
        // Parts on a job (Phase M2): a line per product of each issue or return document, retried by its request id.
        b.Entity<MaintenanceJobPart>(e =>
        {
            e.HasIndex(x => new { x.JobId, x.ProductId });
            e.HasIndex(x => new { x.ClientRequestId, x.ProductId }).IsUnique();
            e.HasIndex(x => x.DocumentId);
            e.HasIndex(x => x.DocumentNumber);
            foreach (var p in new[] { nameof(MaintenanceJobPart.Qty), nameof(MaintenanceJobPart.UnitCost), nameof(MaintenanceJobPart.Value) })
                e.Property(p).HasPrecision(28, 10);
            e.HasOne<MaintenanceJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<StockMovement>().WithMany().HasForeignKey(x => x.StockMovementId).OnDelete(DeleteBehavior.Restrict);
        });
        // Outside work on a job (Phase M2): retried by its request id; a voucher is found by the amounts linked to it.
        b.Entity<MaintenanceJobExpense>(e =>
        {
            e.HasIndex(x => x.JobId);
            e.HasIndex(x => x.ClientRequestId).IsUnique();
            e.HasIndex(x => x.PaymentVoucherId);
            e.HasOne<MaintenanceJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PaymentVoucher>().WithMany().HasForeignKey(x => x.PaymentVoucherId).OnDelete(DeleteBehavior.Restrict);
        });
        // Phase M7d (MS-30): a capitalised repair's credit lines, frozen at completion, read by the practice ledger of that month.
        b.Entity<MaintenanceJobCapitalisedLine>(e =>
        {
            e.HasIndex(x => x.JobId);
            e.HasOne<MaintenanceJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<ManufacturingAccountMapping>(e =>
            e.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => x.LedgerAccountId).OnDelete(DeleteBehavior.Restrict));
        // Phase D: each posting of a month, server-only; a month posts again only under the next generation.
        b.Entity<ManufacturingMonthPosting>(e => e.HasIndex(x => new { x.Year, x.Month, x.Generation }).IsUnique());
        b.Entity<MaintenanceMonthPosting>(e => e.HasIndex(x => new { x.Year, x.Month, x.Generation }).IsUnique());
        // REN-12 (Phase M7a): the fixed-asset register. One settings row; a life per product; the events replayed by product in
        // time order; each posting of a month's depreciation, a month posting again only under the next generation.
        b.Entity<FixedAssetSettings>(e => e.Property(x => x.Id).ValueGeneratedNever());
        b.Entity<FixedAssetLife>(e =>
        {
            e.Property(x => x.ProductId).ValueGeneratedNever();
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });
        // A type's own depreciation method (declining balance, 2026-11-10); an empty field follows the company's.
        b.Entity<FixedAssetMethodChoice>(e =>
        {
            e.Property(x => x.ProductId).ValueGeneratedNever();
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<FixedAssetMovement>(e =>
        {
            e.HasIndex(x => new { x.ProductId, x.AtUtc });
            e.HasIndex(x => x.AtUtc);
            e.HasIndex(x => x.DocumentId);
            e.Property(x => x.Qty).HasPrecision(28, 10);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Branch>().WithMany().HasForeignKey(x => x.ToBranchId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<FixedAssetMonthPosting>(e => e.HasIndex(x => new { x.Year, x.Month, x.Generation }).IsUnique());

        // Workers' bonuses on a payment voucher (owner, 2026-11-08): server-only, read by payroll by employee and month.
        b.Entity<PaymentVoucherBonusLine>(e =>
        {
            e.HasOne(x => x.Voucher).WithMany(v => v.BonusLines).HasForeignKey(x => x.VoucherId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PayrollRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.EmployeeId, x.PayrollYear, x.PayrollMonth });
            e.HasIndex(x => x.RunId);
        });

        b.Entity<StockTransfer>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            // The list asks "what is open, and what touches my branch" — both ends, and the status.
            e.HasIndex(x => new { x.FromBranchId, x.Status });
            e.HasIndex(x => new { x.ToBranchId, x.Status });
            e.HasIndex(x => x.DispatchedAtUtc);
            // Restrict: a transfer names its two branches as historical facts.
            e.HasOne(x => x.FromBranch).WithMany().HasForeignKey(x => x.FromBranchId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ToBranch).WithMany().HasForeignKey(x => x.ToBranchId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne(x => x.Transfer!).HasForeignKey(x => x.TransferId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Events).WithOne(x => x.Transfer!).HasForeignKey(x => x.TransferId).OnDelete(DeleteBehavior.Cascade);
            e.Ignore(x => x.IsOpen);
        });

        b.Entity<StockTransferLine>(e =>
        {
            // "What of this product is in transit" — the item movement report's drill-down.
            e.HasIndex(x => x.ProductId);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.Ignore(x => x.InTransitQty);
        });

        ConfigureManufacturing(b);

        b.Entity<LedgerAccount>(e =>
        {
            // Code, not id, is what code posts by, so it has to be unique per company.
            e.HasIndex(x => x.Code).IsUnique();
        });

        // CLO-01: the latest close or reopen is the one in force.
        b.Entity<BookClosing>(e => e.HasIndex(x => x.AtUtc));

        b.Entity<LedgerEntry>(e =>
        {
            // Every accounting report filters on period and branch; §5.10 says explicitly
            // that they read the ledger with indexed filters rather than looping documents.
            e.HasIndex(x => new { x.EntryDate, x.BranchId });
            // The drill-down from a figure back to the document that produced it, and the
            // check that stops a re-pushed sale posting twice.
            e.HasIndex(x => new { x.SourceType, x.SourceId });
            e.HasMany(x => x.Lines).WithOne(x => x.Entry!)
                .HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<LedgerEntryLine>(e =>
        {
            // An account statement is a single indexed read; a per-branch P&L filters the
            // line rather than joining back for the branch, which is why the line carries it.
            e.HasIndex(x => new { x.AccountId, x.BranchId });
            // And a customer statement is the same read against 411 for one customer. It is
            // the query the whole of phase 5 reconciles against, so it is indexed rather than
            // scanned across every line the company has ever posted.
            // No foreign key, deliberately, and for the same reason LedgerEntry.BranchId has
            // none: a dimension on a ledger line is a label on a historical fact, not a
            // relationship. A key here would also force SQLite to rebuild the largest table in
            // the database to add one nullable column, which is the riskiest shape a migration
            // against a live installation can take.
            e.HasIndex(x => new { x.AccountId, x.CustomerId });
            // The supplier statement's read against 401 (owner, 2026-10-10); no key, as above.
            e.HasIndex(x => new { x.AccountId, x.SupplierId });
            e.Ignore(x => x.Signed);
            e.HasOne(x => x.Account).WithMany()
                .HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<PaymentMethodDef>(e =>
        {
            e.HasIndex(x => x.Version);
            e.HasIndex(x => x.Key).IsUnique();
        });

        b.Entity<SalePayment>(e =>
        {
            // "Which cheques are due this week" is the screen that stops money being
            // forgotten, so the due date is indexed rather than scanned.
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.InstrumentDueDate);
        });

        b.Entity<CashBox>(e =>
        {
            // One box per branch keeps the cash position unambiguous; a branch with two
            // boxes has no single figure to reconcile the drawer against.
            e.HasIndex(x => x.BranchId).IsUnique();
            e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CashMovement>(e =>
        {
            e.HasIndex(x => x.CashBoxId);
            e.HasIndex(x => x.OccurredAtUtc);
            e.HasIndex(x => new { x.SourceType, x.SourceId });
            e.HasOne(x => x.CashBox).WithMany().HasForeignKey(x => x.CashBoxId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RentalContract>(e =>
        {
            // The number printed on the customer's copy. Unique, so two notes can never carry
            // the same one however the generator is later changed.
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => new { x.Status, x.CustomerId });
            e.HasMany(x => x.Lines).WithOne(x => x.Contract!)
                .HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
            // The site is a snapshot plus a pointer; the pointer is Restrict so the row it names
            // outlives the contract, like the customer and the branch.
            e.HasOne(x => x.Site).WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<RentalContractLine>(e =>
        {
            e.HasIndex(x => x.ProductId);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            // Self-reference for the accessory that goes out bolted to its parent. No cascade:
            // deleting a parent line out from under its couplers would leave them hired to
            // nothing, with a period inherited from a row that no longer exists.
            e.HasOne<RentalContractLine>().WithMany()
                .HasForeignKey(x => x.ParentLineId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<IssueNote>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            // "What is out, and with whom" is the report the business runs on, so the contract
            // and the date are both indexed rather than scanned.
            e.HasIndex(x => new { x.ContractId, x.IssuedAtUtc });
            e.HasIndex(x => x.BranchId);
            e.HasMany(x => x.Lines).WithOne(x => x.IssueNote!)
                .HasForeignKey(x => x.IssueNoteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Contract).WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<IssueNoteLine>(e =>
        {
            e.HasIndex(x => x.ContractLineId);
            e.HasOne(x => x.ContractLine).WithMany()
                .HasForeignKey(x => x.ContractLineId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ReturnNote>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => new { x.ContractId, x.ReturnedAtUtc });
            e.HasIndex(x => x.BranchId);
            e.HasMany(x => x.Lines).WithOne(x => x.ReturnNote!)
                .HasForeignKey(x => x.ReturnNoteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Contract).WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ReturnNoteLine>(e =>
        {
            e.HasIndex(x => x.ContractLineId);
            // Derived from the four condition quantities; storing it would let it drift from them.
            e.Ignore(x => x.SettledQty);
            e.HasOne(x => x.ContractLine).WithMany()
                .HasForeignKey(x => x.ContractLineId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Allocations).WithOne(x => x.ReturnNoteLine!)
                .HasForeignKey(x => x.ReturnNoteLineId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ReturnNoteLineAllocation>(e =>
        {
            e.HasIndex(x => new { x.ReturnNoteLineId, x.IssueNoteId, x.Condition });
            e.HasIndex(x => x.IssueNoteId);
            e.HasOne<RentalContractLine>().WithMany().HasForeignKey(x => x.ContractLineId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ContractExtension>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.ContractId);
            // "Which retroactive extensions were approved, and by whom" has to be a query: it
            // is the control that stops every overdue quietly becoming an extension.
            e.HasIndex(x => x.IsRetroactive);
            e.HasOne(x => x.Contract).WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ContractLine).WithMany()
                .HasForeignKey(x => x.ContractLineId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<RentalCharge>(e =>
        {
            e.HasIndex(x => new { x.ContractId, x.Kind });
            e.HasIndex(x => x.ReturnNoteId);
            e.HasOne(x => x.Contract).WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RentalContractAttachment>(e =>
        {
            e.HasIndex(x => x.ContractId);
            // Cascade: the scan of the signatory's id is part of the contract, not a document of its own.
            e.HasOne(x => x.Contract).WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Invoice>(e =>
        {
            // The number on the customer's copy. Unique, so two invoices can never carry the
            // same one however the generator is later changed.
            e.HasIndex(x => x.Number).IsUnique();
            // The debt ageing report, which is the one that gets a contractor chased.
            e.HasIndex(x => new { x.CustomerId, x.DueDateUtc });
            // "Has this sale already been invoiced?" — asked on every raise, so a lookup.
            e.HasIndex(x => new { x.SourceType, x.SourceId });
            e.HasMany(x => x.Lines).WithOne(x => x.Invoice!)
                .HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
            // The buyer's identifiers frozen at issue. Nullable — every invoice raised before the
            // column reads NULL, "not recorded" — and EF never runs a converter on NULL.
            e.Property(x => x.BuyerFiscalIds)
                .HasMaxLength(2000)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null),
                    new ValueComparer<Dictionary<string, string>?>(
                        (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
                        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
                        v => v == null ? null : new Dictionary<string, string>(v)));
        });

        b.Entity<InvoiceLine>(e =>
        {
            e.HasIndex(x => x.ProductId);
            // Plain, not unique: "a rental charge is billed once" is enforced in code because a
            // unique index over a nullable column does not mean the same thing on every
            // provider — SQLite lets any number of nulls through, SQL Server counts two nulls as a
            // collision unless filtered. One rule written once beats one per provider.
            e.HasIndex(x => x.RentalChargeId);
            // The cost the line's goods left stock at (P0.3) can be a weighted average: kept as the movement keeps it, so a
            // credit note brings them back at exactly what went out.
            e.Property(x => x.StockUnitCost).HasPrecision(28, 10);
        });

        b.Entity<ReceiptVoucher>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => new { x.CustomerId, x.ReceivedAtUtc });
            // The cheques-due screen, exactly as for a payment taken at the till.
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.InstrumentDueDate);
            // A contract's paid figure is the receipts that name it (answers 15, 22).
            e.HasIndex(x => x.ContractId);
            // MS-35: a job's invoice finds the advances taken for it.
            e.HasIndex(x => x.MaintenanceJobId);
            e.HasOne<MaintenanceJob>().WithMany().HasForeignKey(x => x.MaintenanceJobId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Allocations).WithOne(x => x.ReceiptVoucher!)
                .HasForeignKey(x => x.ReceiptVoucherId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ReceiptAllocation>(e =>
        {
            e.HasIndex(x => x.InvoiceId);
            // No cascade from the invoice: an invoice that has been paid against cannot be
            // removed, and nothing in this module removes one anyway.
            e.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        });

        // M7b: append-only, read per advance; server-only, never synced.
        b.Entity<CustomerAdvanceApplication>(e =>
        {
            e.HasIndex(x => x.ReceiptVoucherId);
            e.HasIndex(x => x.InvoiceId);
            e.HasOne<ReceiptVoucher>().WithMany().HasForeignKey(x => x.ReceiptVoucherId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Invoice>().WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<ReceivableSettings>(e => e.Property(x => x.Id).ValueGeneratedNever());

        b.Entity<PaymentVoucher>(e =>
        {
            // A payroll document finds the voucher that records it (answer 24).
            e.HasIndex(x => new { x.SourceType, x.SourceId });
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.BranchId);
            e.HasIndex(x => x.CustomerId);
            // "What is waiting for the owner's signature" — the approval queue is a status lookup.
            e.HasIndex(x => x.Status);
            // "Has this return note already been refunded?" — asked on every raise, so a lookup.
            // Plain, not unique, for the reason InvoiceLine.RentalChargeId is: a unique index over
            // a nullable column does not mean the same thing on every provider.
            e.HasIndex(x => x.ReturnNoteId);
            // Restrict everywhere: a voucher names historical facts, none of which may vanish under it.
            e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Contract).WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ReturnNote).WithMany().HasForeignKey(x => x.ReturnNoteId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<CreditNote>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.InvoiceId);
            e.HasIndex(x => new { x.CustomerId, x.IssuedAtUtc });
            e.HasIndex(x => new { x.SourceType, x.SourceId });
            e.HasMany(x => x.Lines).WithOne(x => x.CreditNote!)
                .HasForeignKey(x => x.CreditNoteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<CreditNoteLine>(e => e.HasIndex(x => x.InvoiceLineId));

        b.Entity<Supplier>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => x.Name);
            // Mapped like Customer.FiscalIds: '{}' when empty, NULL or blank read as none.
            e.Property(x => x.FiscalIds)
                .HasMaxLength(2000)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => string.IsNullOrWhiteSpace(v)
                        ? new()
                        : JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null) ?? new(),
                    new ValueComparer<Dictionary<string, string>>(
                        (a, c) => a!.Count == c!.Count && !a.Except(c).Any(),
                        v => v.Aggregate(0, (hash, pair) => hash ^ HashCode.Combine(pair.Key, pair.Value)),
                        v => new Dictionary<string, string>(v)));
        });

        b.Entity<SupplierOpeningCorrection>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => new { x.SupplierId, x.CreatedAtUtc });
            e.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<CustomerOpeningCorrection>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => new { x.CustomerId, x.CreatedAtUtc });
            // Restrict, as for a credit note: a correction is history and outlives nothing it names.
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Employee>(e =>
        {
            // The number on the payslip. Unique, so two people can never carry the same one
            // however the generator is later changed.
            e.HasIndex(x => x.Number).IsUnique();
            // The fingerprint terminal's number, unique across the company when set (FR-3A).
            // Both providers let any number of rows leave it NULL under a unique index.
            e.HasIndex(x => x.FingerprintNumber).IsUnique();
            // "Who does this branch pay" — the query the run, the sheet and the list all open with.
            e.HasIndex(x => new { x.BranchId, x.IsActive });
            // Searching staff by name, which is how anyone actually looks for a person.
            e.HasIndex(x => x.FullName);
            // Restrict: a branch that still employs someone is in use. There is no delete on an
            // employee either — leaving is LeftOn — so nothing here ever cascades from a person.
            e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<EmployeeAllowance>(e =>
        {
            // One value per allowance per person: the override, not a history of overrides.
            e.HasIndex(x => new { x.EmployeeId, x.AllowanceKey }).IsUnique();
            // Cascade, and the only cascade from an employee: these rows are part of the record,
            // not documents of their own.
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AttendanceDeclaration>(e =>
        {
            // One declaration per person per month — the upsert the sheet screen depends on, so
            // a second submission replaces a row rather than adding one.
            e.HasIndex(x => new { x.EmployeeId, x.PeriodYear, x.PeriodMonth }).IsUnique();
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Overtime).WithOne(x => x.Declaration!)
                .HasForeignKey(x => x.DeclarationId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AttendanceDeclarationOvertime>(e =>
            e.HasIndex(x => new { x.DeclarationId, x.RuleKey }).IsUnique());

        b.Entity<AttendanceDay>(e =>
        {
            // One row per person per working day, for the same reason.
            e.HasIndex(x => new { x.EmployeeId, x.WorkDate }).IsUnique();
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Overtime).WithOne(x => x.Day!)
                .HasForeignKey(x => x.DayId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AttendanceDayOvertime>(e =>
            e.HasIndex(x => new { x.DayId, x.RuleKey }).IsUnique());

        b.Entity<OvertimeEntry>(e =>
        {
            // A month's entries for a branch's staff, read on every compute and by the list.
            e.HasIndex(x => new { x.EmployeeId, x.WorkDate });
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        });

        // The history outlives the entry, so no foreign key to it.
        b.Entity<OvertimeEntryChange>(e => e.HasIndex(x => new { x.EntryId, x.ChangedAtUtc }));

        b.Entity<ClockEvent>(e =>
        {
            // Pairing reads one person's punches in order; the index is the ordering.
            e.HasIndex(x => new { x.EmployeeId, x.AtUtc });
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WageAdvance>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            // "What does this person still owe back" — read on every compute.
            e.HasIndex(x => new { x.EmployeeId, x.Status });
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<PayrollRun>(e =>
        {
            // Historical runs retain statutory deductions unless a draft was explicitly opted
            // into the new payslip policy during the migration.
            e.Property(x => x.CalculateStatutoryDeductions).HasDefaultValue(true);
            e.HasIndex(x => x.Number).IsUnique();
            // The lookup every transition opens with, named explicitly because the filtered
            // unique index below covers the same columns and EF needs the two told apart.
            e.HasIndex(x => new { x.BranchId, x.PeriodYear, x.PeriodMonth }, "IX_PayrollRuns_BranchId_PeriodYear_PeriodMonth");

            // One live run per branch and month. Uniqueness is over non-Reversed runs only: a
            // reversed run keeps its LedgerEntryId and ReversalEntryId — the record of what was
            // posted and what undid it — and a plain unique index would let that row block the
            // month from ever being run again.
            //
            // Both providers have partial indexes and get the constraint, each in its own quoting.
            // Either way the check inside BeginWriteScopeAsync is the enforcement — the index is a
            // second pair of hands, not the rule.
            e.HasIndex(x => new { x.BranchId, x.PeriodYear, x.PeriodMonth }, "UX_PayrollRuns_LiveBranchPeriod")
                .IsUnique()
                .HasFilter(Database.IsSqlServer() ? "[Status] <> 4" : "\"Status\" <> 4");

            e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne(x => x.Run!)
                .HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PayrollRunLine>(e =>
        {
            // One line per person per run, and the index that pages the lines.
            e.HasIndex(x => new { x.RunId, x.EmployeeId }).IsUnique();
            // "Every payslip this person has ever had" — the employee screen's history tab.
            e.HasIndex(x => x.EmployeeId);
            e.HasMany(x => x.Elements).WithOne(x => x.Line!)
                .HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Cascade);
            // No foreign key to Employee: the line names a person through its snapshots and is
            // meant to outlive any edit to them, the same choice the invoice's buyer block makes.
        });

        b.Entity<PayrollRunLineElement>(e => e.HasIndex(x => new { x.LineId, x.SortOrder }));

        b.Entity<PayrollAdjustment>(e =>
        {
            // Re-read on every compute, so it is a lookup rather than a scan of the run.
            e.HasIndex(x => new { x.RunId, x.EmployeeId });
            e.HasOne(x => x.Run).WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<WagePayment>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => new { x.RunId, x.PaidAtUtc });
            // Restrict: a run that has been paid against is not deletable, which is also what
            // stops a recompute from removing the lines the payment points at.
            e.HasOne(x => x.Run).WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne(x => x.WagePayment!)
                .HasForeignKey(x => x.WagePaymentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<WagePaymentLine>(e =>
        {
            // Plain, not unique: a part-paid crew is paid again later, and two payments against
            // one payslip line are legitimate. "One line per payment per run line" is a code rule.
            e.HasIndex(x => x.RunLineId);
            e.HasOne<PayrollRunLine>().WithMany()
                .HasForeignKey(x => x.RunLineId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<PayrollStatutoryPayment>(e =>
        {
            // "What have we actually paid the fund this year" — the read that stops 431/442/447
            // looking like debt forever.
            e.HasIndex(x => new { x.BranchId, x.AccountCode, x.PaidAtUtc });
        });

        b.Entity<DocumentTextChange>(e =>
        {
            // The allowance question — "how many changes has the rental contract had this year" —
            // is one indexed equality per kind and year, and this index answers the history list
            // for a kind as well.
            e.HasIndex(x => new { x.Kind, x.ChangeYear });
            // No foreign key to Stores: there is one company per copy, the change belongs to the
            // company rather than to a row that could be replaced, and the history must survive
            // anything that happens to the store record.
        });

        b.Entity<GiftCard>(e =>
        {
            e.HasIndex(x => x.Version);
            e.HasIndex(x => x.Code).IsUnique();
        });
        b.Entity<GiftCardMovement>(e => e.HasIndex(x => x.GiftCardId));
        b.Entity<LoyaltyMovement>(e => e.HasIndex(x => x.CustomerId));

        b.Entity<IntegrationOutbox>(e =>
        {
            e.ToTable("IntegrationOutbox");
            e.HasIndex(x => new { x.SentAtUtc, x.NextAttemptAtUtc });
        });

        // Fixed single row; the key is assigned by us, never by an IDENTITY column.
        b.Entity<SyncCounter>(e => e.Property(x => x.Id).ValueGeneratedNever());

        // Last, once every property is in the model: each stored moment reads back as UTC, so the
        // API sends it with its Z and a screen shows the hour it happened (UtcDateTimes).
        UtcDateTimes.Apply(b);
    }

    /// <summary>
    /// Opens the transaction every catalog write must run inside. Version reservation and
    /// the row updates it stamps have to commit together, or a terminal can advance its
    /// cursor past a row that is not visible yet and never see that row again.
    /// </summary>
    public Task<IDbContextTransaction> BeginWriteAsync(CancellationToken ct = default) =>
        Database.BeginTransactionAsync(ct);

    /// <summary>
    /// Opens a write transaction only if the request is not already inside one.
    ///
    /// <para>
    /// Some routes run behind a filter that opens a transaction and holds it across the
    /// handler, so that a capacity check and the write it guards cannot be separated. A
    /// handler that unconditionally opens its own then fails outright — the connection is
    /// already in a transaction. This is the same reasoning
    /// <see cref="SaveWithVersionsAsync"/> already applies, made available to handlers that
    /// need to group several writes rather than one.
    /// </para>
    /// <para>
    /// The scope commits only what it opened. Committing a transaction it merely joined would
    /// end the caller's unit of work early and let a later failure leave half a write behind.
    /// </para>
    /// </summary>
    public async Task<WriteScope> BeginWriteScopeAsync(CancellationToken ct = default) =>
        new(Database.CurrentTransaction is null ? await BeginWriteAsync(ct) : null);

    /// <summary>A transaction this code owns, or a borrowed one it must not commit.</summary>
    public sealed class WriteScope(IDbContextTransaction? owned) : IAsyncDisposable
    {
        public Task CommitAsync(CancellationToken ct = default) =>
            owned?.CommitAsync(ct) ?? Task.CompletedTask;

        public ValueTask DisposeAsync() => owned?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    /// <summary>
    /// Manufacturing, Phase 1. Every foreign key is Restrict except a document's own children, so a line,
    /// a unit, a product or an employee that something names cannot be deleted from under it. Uniqueness
    /// that SQLite cannot hold the way SQL Server does (a nullable column in a unique key) is also checked
    /// in code (<c>Infrastructure/Manufacturing</c>).
    /// </summary>
    private static void ConfigureManufacturing(ModelBuilder b)
    {
        // The product's role and base unit. The role is stored as its number, like every enum here;
        // zero is ordinary merchandise, the value every existing product takes.
        b.Entity<Product>(e =>
        {
            e.HasIndex(x => x.ManufacturingRole);
            e.HasOne<UnitOfMeasure>().WithMany().HasForeignKey(x => x.BaseUnitId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<UnitOfMeasure>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            // Factors need more places than money: a gram is 0.001 kg and a millimetre 0.001 m, and a
            // factor rounded to three places would turn some future unit into nonsense silently.
            e.Property(x => x.FactorToReference).HasPrecision(28, 10);
            var at = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);
            e.HasData(
                new UnitOfMeasure { Id = SystemUnits.Piece, Code = "pcs", NameAr = "قطعة", NameFr = "Pièce", NameEn = "Piece", Dimension = UnitDimension.Count, FactorToReference = 1m, IsSystem = true, CreatedAtUtc = at, UpdatedAtUtc = at },
                new UnitOfMeasure { Id = SystemUnits.Kilogram, Code = "kg", NameAr = "كيلوغرام", NameFr = "Kilogramme", NameEn = "Kilogram", Dimension = UnitDimension.Mass, FactorToReference = 1m, IsSystem = true, CreatedAtUtc = at, UpdatedAtUtc = at },
                new UnitOfMeasure { Id = SystemUnits.Gram, Code = "g", NameAr = "غرام", NameFr = "Gramme", NameEn = "Gram", Dimension = UnitDimension.Mass, FactorToReference = 0.001m, IsSystem = true, CreatedAtUtc = at, UpdatedAtUtc = at },
                new UnitOfMeasure { Id = SystemUnits.Metre, Code = "m", NameAr = "متر", NameFr = "Mètre", NameEn = "Metre", Dimension = UnitDimension.Length, FactorToReference = 1m, IsSystem = true, CreatedAtUtc = at, UpdatedAtUtc = at },
                new UnitOfMeasure { Id = SystemUnits.Centimetre, Code = "cm", NameAr = "سنتيمتر", NameFr = "Centimètre", NameEn = "Centimetre", Dimension = UnitDimension.Length, FactorToReference = 0.01m, IsSystem = true, CreatedAtUtc = at, UpdatedAtUtc = at },
                new UnitOfMeasure { Id = SystemUnits.Litre, Code = "l", NameAr = "لتر", NameFr = "Litre", NameEn = "Litre", Dimension = UnitDimension.Volume, FactorToReference = 1m, IsSystem = true, CreatedAtUtc = at, UpdatedAtUtc = at },
                // Added with the specification fields (2026-10-26): a thickness or a diameter is written in
                // millimetres, and a company-made "mm" would have been a second, unfixed definition of it.
                new UnitOfMeasure { Id = SystemUnits.Millimetre, Code = "mm", NameAr = "مليمتر", NameFr = "Millimètre", NameEn = "Millimetre", Dimension = UnitDimension.Length, FactorToReference = 0.001m, IsSystem = true, CreatedAtUtc = at, UpdatedAtUtc = at });
        });

        b.Entity<ProductUnit>(e =>
        {
            e.HasIndex(x => new { x.ProductId, x.UnitId }).IsUnique();
            e.Property(x => x.FactorToBase).HasPrecision(28, 10);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Unit).WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ManufacturingJobRole>(e => e.HasIndex(x => x.Code).IsUnique());

        b.Entity<ProductionLine>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            // One line per location: two lines on one location would share one stock, which is
            // exactly what a line exists to prevent (D34).
            e.HasIndex(x => x.LocationId).IsUnique();
            e.HasOne(x => x.Location).WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ManagerUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Members).WithOne(x => x.Line!).HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Employees).WithOne(x => x.Line!).HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Products).WithOne(x => x.Line!).HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ProductionLineMember>(e =>
        {
            e.HasKey(x => new { x.LineId, x.UserId });
            // "Which lines is this user on" — asked on every manufacturing request a branch user makes.
            e.HasIndex(x => x.UserId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ProductionLineEmployee>(e =>
        {
            e.HasIndex(x => new { x.LineId, x.EmployeeId });
            e.HasIndex(x => x.EmployeeId);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.JobRole).WithMany().HasForeignKey(x => x.JobRoleId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ProductionLineProduct>(e =>
        {
            e.HasKey(x => new { x.LineId, x.ProductId });
            e.HasIndex(x => x.ProductId);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<StageTemplate>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Steps).WithOne(x => x.Template!).HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<StageTemplateStep>(e =>
        {
            e.HasIndex(x => new { x.TemplateId, x.Sequence });
            e.HasMany(x => x.Roles).WithOne(x => x.Step!).HasForeignKey(x => x.StepId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<StageTemplateStepRole>(e =>
        {
            e.HasKey(x => new { x.StepId, x.JobRoleId });
            e.HasOne(x => x.JobRole).WithMany().HasForeignKey(x => x.JobRoleId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<BillOfMaterials>(e =>
        {
            // A product's versions within one line. Neither engine holds it for a recipe with no line (SQL
            // Server filters NULLs out of a unique index, SQLite lets them repeat), so the next version is
            // drawn in code under the version lock (BillsOfMaterials.NextVersionAsync) for every recipe.
            e.HasIndex(x => new { x.ProductId, x.ProductionLineId, x.Version }).IsUnique();
            e.HasIndex(x => new { x.ProductId, x.Status });
            e.HasIndex(x => x.ProductionLineId);
            e.Property(x => x.BatchQuantity).HasPrecision(28, 10);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ProductionLine).WithMany().HasForeignKey(x => x.ProductionLineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.BatchUnit).WithMany().HasForeignKey(x => x.BatchUnitId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<StageTemplate>().WithMany().HasForeignKey(x => x.StageTemplateId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne(x => x.Bom!).HasForeignKey(x => x.BomId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Stages).WithOne(x => x.Bom!).HasForeignKey(x => x.BomId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<BomLine>(e =>
        {
            // "Which recipes use this material" — the cycle check and the base-unit guard ask it.
            e.HasIndex(x => x.ComponentProductId);
            e.Property(x => x.Quantity).HasPrecision(28, 10);
            e.Property(x => x.WastePercent).HasPrecision(9, 4);
            e.HasOne(x => x.ComponentProduct).WithMany().HasForeignKey(x => x.ComponentProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Unit).WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<BomStage>(e =>
        {
            e.HasIndex(x => new { x.BomId, x.Sequence });
            e.HasMany(x => x.Roles).WithOne(x => x.Stage!).HasForeignKey(x => x.StageId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<BomStageRole>(e =>
        {
            e.HasKey(x => new { x.StageId, x.JobRoleId });
            e.HasOne(x => x.JobRole).WithMany().HasForeignKey(x => x.JobRoleId).OnDelete(DeleteBehavior.Restrict);
        });

        // Specification fields and their values (hardening review, 2026-10-26). Two value tables of one
        // shape, not a column per dimension: each product carries only the fields that mean something for it.
        b.Entity<SpecAttribute>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            var at = new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);
            e.HasData(
                new SpecAttribute { Id = SystemSpecAttributes.Length, Code = "LENGTH", NameAr = "الطول", NameFr = "Longueur", NameEn = "Length", Kind = SpecKind.Measure, Dimension = UnitDimension.Length, IsSystem = true, SortOrder = 1, CreatedAtUtc = at, UpdatedAtUtc = at },
                new SpecAttribute { Id = SystemSpecAttributes.Width, Code = "WIDTH", NameAr = "العرض", NameFr = "Largeur", NameEn = "Width", Kind = SpecKind.Measure, Dimension = UnitDimension.Length, IsSystem = true, SortOrder = 2, CreatedAtUtc = at, UpdatedAtUtc = at },
                new SpecAttribute { Id = SystemSpecAttributes.Thickness, Code = "THICKNESS", NameAr = "السماكة", NameFr = "Épaisseur", NameEn = "Thickness", Kind = SpecKind.Measure, Dimension = UnitDimension.Length, IsSystem = true, SortOrder = 3, CreatedAtUtc = at, UpdatedAtUtc = at },
                new SpecAttribute { Id = SystemSpecAttributes.Diameter, Code = "DIAMETER", NameAr = "القطر", NameFr = "Diamètre", NameEn = "Diameter", Kind = SpecKind.Measure, Dimension = UnitDimension.Length, IsSystem = true, SortOrder = 4, CreatedAtUtc = at, UpdatedAtUtc = at },
                new SpecAttribute { Id = SystemSpecAttributes.Grade, Code = "GRADE", NameAr = "الدرجة", NameFr = "Nuance", NameEn = "Grade", Kind = SpecKind.Text, Dimension = UnitDimension.Other, IsSystem = true, SortOrder = 5, CreatedAtUtc = at, UpdatedAtUtc = at });
        });

        b.Entity<ProductSpec>(e =>
        {
            e.HasIndex(x => new { x.ProductId, x.AttributeId }).IsUnique();
            e.Property(x => x.Value).HasPrecision(28, 10);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Attribute).WithMany().HasForeignKey(x => x.AttributeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Unit).WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<BomLineSpec>(e =>
        {
            e.HasIndex(x => new { x.BomLineId, x.AttributeId }).IsUnique();
            e.Property(x => x.Value).HasPrecision(28, 10);
            // Goes with its line, as the line goes with its draft.
            e.HasOne(x => x.BomLine).WithMany(x => x.Specs).HasForeignKey(x => x.BomLineId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Attribute).WithMany().HasForeignKey(x => x.AttributeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Unit).WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict);
        });

        // The sale policy's audit (owner, 2026-10-26): append-only, server-only.
        b.Entity<ProductSaleStatusChange>(e =>
        {
            // "When did this product last stop being for sale" — asked for every exception a push records.
            e.HasIndex(x => new { x.ProductId, x.ChangedAtUtc });
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        ConfigureProductionOrders(b);

        b.Entity<ManufacturingSaleException>(e =>
        {
            // One per sale line: a push is idempotent, and so is its exception.
            e.HasIndex(x => x.SaleLineId).IsUnique();
            e.HasIndex(x => new { x.BranchId, x.SoldAtUtc });
            e.HasIndex(x => new { x.ProductId, x.SoldAtUtc });
            e.Property(x => x.Quantity).HasPrecision(18, 3);
            e.HasOne<Sale>().WithMany().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    /// <summary>
    /// Phase 2's tables. A request id is unique per document kind, so a retried request finds its first
    /// document instead of making a second; a booking is unique per employee and record row; a record per
    /// line and day. Quantities and snapshots keep (28,10) like the BOM's; an expense is money, (18,3).
    /// </summary>
    private static void ConfigureProductionOrders(ModelBuilder b)
    {
        b.Entity<ProductionOrder>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.ClientRequestId).IsUnique();
            e.HasIndex(x => new { x.LineId, x.Status });
            e.HasIndex(x => new { x.LocationId, x.Status });
            foreach (var p in new[] { nameof(ProductionOrder.PlannedQty), nameof(ProductionOrder.PlannedBaseQty), nameof(ProductionOrder.Batches),
                         nameof(ProductionOrder.AcceptedBaseQty), nameof(ProductionOrder.RejectedBaseQty), nameof(ProductionOrder.EstimatedMaterialCost) })
                e.Property(p).HasPrecision(28, 10);
            e.HasOne(x => x.Line).WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Branch>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BillOfMaterials>().WithMany().HasForeignKey(x => x.BomId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<UnitOfMeasure>().WithMany().HasForeignKey(x => x.PlannedUnitId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Materials).WithOne(x => x.Order!).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Stages).WithOne(x => x.Order!).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ProductionOrderMaterial>(e =>
        {
            e.HasIndex(x => x.ProductId);
            foreach (var p in new[] { nameof(ProductionOrderMaterial.NetBaseQty), nameof(ProductionOrderMaterial.WasteBaseQty),
                         nameof(ProductionOrderMaterial.RequiredBaseQty), nameof(ProductionOrderMaterial.EstimatedUnitCost),
                         nameof(ProductionOrderMaterial.IssuedBaseQty), nameof(ProductionOrderMaterial.ReturnedBaseQty),
                         nameof(ProductionOrderMaterial.ConsumedBaseQty), nameof(ProductionOrderMaterial.ScrappedBaseQty),
                         nameof(ProductionOrderMaterial.ReservationReleasedBaseQty) })
                e.Property(p).HasPrecision(28, 10);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<UnitOfMeasure>().WithMany().HasForeignKey(x => x.BaseUnitId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ProductionOrderStage>(e =>
        {
            e.HasIndex(x => new { x.OrderId, x.Sequence });
            e.Property(x => x.PlannedMinutes).HasPrecision(28, 10);
            e.Property(x => x.GoodBaseQty).HasPrecision(28, 10);
            e.Property(x => x.RejectedBaseQty).HasPrecision(28, 10);
        });

        b.Entity<ProductionOrderEvent>(e =>
        {
            e.HasIndex(x => new { x.OrderId, x.AtUtc });
            e.HasOne<ProductionOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ProductionOrderExpense>(e =>
        {
            e.HasIndex(x => x.OrderId);
            e.HasIndex(x => x.ClientRequestId).IsUnique();
            e.HasOne<ProductionOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PaymentVoucher>().WithMany().HasForeignKey(x => x.PaymentVoucherId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ManufacturingCostSheet>(e =>
        {
            e.HasIndex(x => x.OrderId).IsUnique(); e.HasIndex(x => x.RequestId).IsUnique();
            // A later provenance migration must not silently bless the short-lived B1 rows that
            // predate source capture; new writers explicitly set Complete.
            e.Property(x => x.SourceState).HasDefaultValueSql("1").HasSentinel((ManufacturingCostSourceState)(-1));
            e.Property(x => x.AcceptedBaseQty).HasPrecision(28, 10); e.Property(x => x.RejectedBaseQty).HasPrecision(28, 10);
            e.Property(x => x.TotalCost).HasPrecision(18, 3); e.Property(x => x.UnitCost).HasPrecision(28, 10);
            e.HasOne<ProductionOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Elements).WithOne().HasForeignKey(x => x.SheetId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ManufacturingCostElementLine>(e => { e.HasIndex(x => new { x.SheetId, x.Element }).IsUnique(); e.Property(x => x.Quantity).HasPrecision(28,10); e.Property(x => x.UnitCost).HasPrecision(28,10); e.Property(x => x.TotalCost).HasPrecision(18,3); });
        b.Entity<ManufacturingMaterialCostSnapshot>(e => { e.HasIndex(x => new { x.SheetId, x.OrderMaterialId }).IsUnique(); e.Property(x => x.Quantity).HasPrecision(28,10); e.Property(x => x.UnitCost).HasPrecision(28,10); e.Property(x => x.TotalCost).HasPrecision(18,3); e.HasOne<ManufacturingCostSheet>().WithMany().HasForeignKey(x => x.SheetId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<ManufacturingLabourRateSnapshot>(e => { e.HasIndex(x => new { x.SheetId, x.EmployeeId, x.PayrollYear, x.PayrollMonth }).IsUnique(); e.Property(x => x.Hours).HasPrecision(28,10); e.Property(x => x.Rate).HasPrecision(28,10); e.Property(x => x.TotalCost).HasPrecision(18,3); e.HasOne<ManufacturingCostSheet>().WithMany().HasForeignKey(x => x.SheetId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<ManufacturingExpenseAllocationSnapshot>(e => { e.HasIndex(x => new { x.SheetId, x.ExpenseId }).IsUnique(); e.Property(x => x.Amount).HasPrecision(18,3); e.HasOne<ManufacturingCostSheet>().WithMany().HasForeignKey(x => x.SheetId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<ManufacturingScrapRecoverySnapshot>(e => { e.HasIndex(x => x.SheetId).IsUnique(); e.Property(x => x.RejectedQty).HasPrecision(28,10); e.Property(x => x.RecoveryValue).HasPrecision(18,3); e.HasOne<ManufacturingCostSheet>().WithMany().HasForeignKey(x => x.SheetId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<ManufacturingReleaseValueSnapshot>(e => { e.HasIndex(x => new { x.SheetId, x.OutputReleaseId }).IsUnique(); e.Property(x => x.Quantity).HasPrecision(28,10); e.Property(x => x.Value).HasPrecision(18,3); e.Property(x => x.UnitValue).HasPrecision(28,10); e.HasOne<ManufacturingCostSheet>().WithMany().HasForeignKey(x => x.SheetId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<ManufacturingLateCost>(e => { e.HasIndex(x => new { x.SheetId, x.Kind, x.SourceReference }).IsUnique(); e.Property(x => x.Amount).HasPrecision(18,3); e.HasOne<ManufacturingCostSheet>().WithMany().HasForeignKey(x => x.SheetId).OnDelete(DeleteBehavior.Cascade); });

        b.Entity<MaterialMovement>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.ClientRequestId).IsUnique();
            e.HasIndex(x => new { x.OrderId, x.AtUtc });
            e.HasIndex(x => new { x.LineId, x.AtUtc });
            e.HasOne<ProductionOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne(x => x.Movement!).HasForeignKey(x => x.MovementId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<MaterialMovementLine>(e =>
        {
            e.HasIndex(x => x.OrderMaterialId);
            foreach (var p in new[] { nameof(MaterialMovementLine.EnteredQty), nameof(MaterialMovementLine.Factor),
                         nameof(MaterialMovementLine.BaseQty), nameof(MaterialMovementLine.UnitCostSnapshot) })
                e.Property(p).HasPrecision(28, 10);
            e.HasOne<ProductionOrderMaterial>().WithMany().HasForeignKey(x => x.OrderMaterialId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<UnitOfMeasure>().WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<StockMovement>().WithMany().HasForeignKey(x => x.StockMovementId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<MaterialConsumption>(e =>
        {
            e.HasIndex(x => x.ClientRequestId).IsUnique();
            e.HasIndex(x => x.OrderMaterialId);
            e.HasIndex(x => x.LogRowId);
            e.Property(x => x.BaseQty).HasPrecision(28, 10);
            e.HasOne<ProductionOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProductionOrderMaterial>().WithMany().HasForeignKey(x => x.OrderMaterialId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProductionLogRow>().WithMany().HasForeignKey(x => x.LogRowId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<OutputRelease>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.ClientRequestId).IsUnique();
            e.HasIndex(x => x.OrderId);
            foreach (var p in new[] { nameof(OutputRelease.EnteredQty), nameof(OutputRelease.Factor),
                         nameof(OutputRelease.BaseQty), nameof(OutputRelease.UnitCostSnapshot) })
                e.Property(p).HasPrecision(28, 10);
            e.HasOne<ProductionOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<UnitOfMeasure>().WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<StockMovement>().WithMany().HasForeignKey(x => x.StockMovementId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ProductionLog>(e =>
        {
            // One record per line per day (owner, 2026-10-26, decision 4).
            e.HasIndex(x => new { x.LineId, x.Day }).IsUnique();
            e.HasIndex(x => x.Number).IsUnique();
            e.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Rows).WithOne(x => x.Log!).HasForeignKey(x => x.LogId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ProductionLogRow>(e =>
        {
            e.HasIndex(x => x.OrderId);
            e.HasIndex(x => x.StageId);
            foreach (var p in new[] { nameof(ProductionLogRow.PlannedQty), nameof(ProductionLogRow.CompletedQty), nameof(ProductionLogRow.RejectedQty) })
                e.Property(p).HasPrecision(28, 10);
            e.HasOne<ProductionOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProductionOrderStage>().WithMany().HasForeignKey(x => x.StageId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<LaborBooking>(e =>
        {
            // The same person on the same row once; the day's total across lines and order kinds is checked
            // against attendance in code, under the version lock. A maintenance booking (Phase M3) has no row:
            // SQLite holds NULLs distinct and SQL Server filters them out of the unique index, so the index keeps
            // its meaning for production rows; one booking per person, job and day is checked in code.
            e.HasIndex(x => new { x.EmployeeId, x.LogRowId }).IsUnique();
            e.HasIndex(x => new { x.EmployeeId, x.Day });
            e.HasIndex(x => new { x.OrderId, x.StageId });
            e.HasIndex(x => new { x.OrderKind, x.OrderId });
            e.Property(x => x.Hours).HasPrecision(9, 2);
            e.Property(x => x.OvertimeHours).HasPrecision(9, 2);
            e.Property(x => x.AttendanceHours).HasPrecision(9, 2);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProductionLogRow>().WithMany().HasForeignKey(x => x.LogRowId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<LaborBookingChange>(e =>
        {
            e.HasIndex(x => new { x.EmployeeId, x.Day });
            e.Property(x => x.Hours).HasPrecision(9, 2);
            e.Property(x => x.OvertimeHours).HasPrecision(9, 2);
        });
    }

    /// <summary>
    /// Takes the version counter's write lock now, without moving the counter, and holds it until
    /// the surrounding transaction ends.
    ///
    /// <para>
    /// Every write to replicated data already ends on this lock: <see cref="NextVersionAsync"/>
    /// takes it and keeps it to the commit, so those writes were serialised — but only at the end,
    /// after each had read the figures it was about to change. A writer that takes the lock
    /// before it reads sees the figure the previous one committed, not the one both started from.
    /// Taking the same lock first, rather than a lock of its own, also keeps one order for
    /// everyone, so two writers cannot each hold what the other is waiting for.
    /// </para>
    /// </summary>
    public async Task LockVersionsAsync(CancellationToken ct = default)
    {
        if (Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "The version lock lasts as long as a transaction; outside one it is released at once. Open one first (BeginWriteScopeAsync).");
        await Database.ExecuteSqlRawAsync("UPDATE SyncCounters SET Value = Value WHERE Id = 1", ct);
    }

    /// <summary>
    /// The row with this id as the database holds it now, read under the version lock — for a
    /// write that changes a running figure on it (a customer's debt, a gift card's balance).
    ///
    /// <para>
    /// A copy the request loaded earlier holds the figure from before the lock, and a query would
    /// hand that same tracked instance back unchanged, so it is reloaded instead. Reloading drops
    /// anything changed on it in memory: ask for the row before changing it, not after.
    /// </para>
    /// </summary>
    public async Task<T?> LockedAsync<T>(Guid id, CancellationToken ct = default) where T : class
    {
        await LockVersionsAsync(ct);
        var entry = ChangeTracker.Entries<T>().FirstOrDefault(e => e.Property("Id").CurrentValue is Guid key && key == id);
        if (entry is null) return await Set<T>().FirstOrDefaultAsync(e => EF.Property<Guid>(e, "Id") == id, ct);
        if (entry.State == EntityState.Added) return entry.Entity;
        await entry.ReloadAsync(ct);
        return entry.State == EntityState.Detached ? null : entry.Entity;
    }

    /// <summary>
    /// Atomically reserves the next global version number.
    ///
    /// The UPDATE ... SET Value = Value + 1 is a single statement, so the database — not the
    /// application — serialises concurrent writers, and the row stays write-locked until the
    /// surrounding transaction commits. A read-modify-write in C# would let two requests
    /// hand out the same number and silently drop one row from every terminal's next pull.
    /// </summary>
    public async Task<long> NextVersionAsync(CancellationToken ct = default)
    {
        await Database.ExecuteSqlRawAsync("UPDATE SyncCounters SET Value = Value + 1 WHERE Id = 1", ct);

        // Read back inside the same transaction, bypassing anything already tracked.
        var counter = await SyncCounters.AsNoTracking().FirstAsync(c => c.Id == 1, ct);

        // Keep a tracked copy in step so a later SaveChanges cannot write a stale value back.
        var tracked = ChangeTracker.Entries<SyncCounter>().FirstOrDefault();
        if (tracked is not null) tracked.Entity.Value = counter.Value;

        return counter.Value;
    }

    public async Task<long> CurrentVersionAsync(CancellationToken ct = default)
    {
        var counter = await SyncCounters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == 1, ct);
        return counter?.Value ?? 0;
    }

    /// <summary>Stamps a new version + timestamp on a replicated row before saving.</summary>
    public async Task TouchAsync(SyncEntity entity, CancellationToken ct = default)
    {
        entity.Version = await NextVersionAsync(ct);
        entity.UpdatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Stamps the given rows with fresh versions and commits them in one transaction.
    /// Every catalog mutation goes through here so version reservation and the row write
    /// are never visible to a pulling terminal out of order.
    /// </summary>
    /// <summary>
    /// Every save first runs the roles model's mirror and guards (AUTH-1,
    /// <see cref="AuthorizationShadow"/>): nothing a save would write can leave the company without
    /// an active Admin, touch a system role's identity or the Admin baseline, or store an unknown key.
    /// <para>
    /// A save that changes what someone may do also bumps the authorization versions it touched
    /// (<see cref="AuthorizationVersions"/>), in the same transaction — opened here when the caller
    /// has none — so the server's cached decisions can never outlive the change (AUTH-1 Step 3).
    /// </para>
    /// <para>
    /// And a location added without a code gets its three-digit one here (owner, 2026-10-05,
    /// <see cref="BranchCodeAssignment"/>), so none can be saved unnumbered by any path.
    /// </para>
    /// <para>
    /// And, only when the accountant chose it (C-16, <see cref="StockValuationSettings.GapPosting"/>) while sales are
    /// costed at the average, a save that adds an inflow posts the negative-stock gaps it covers in the same transaction
    /// (<see cref="ValuationGapsOnInflow"/>) — here, so that every writer of a stock movement does it without each having
    /// to remember. Under the defaults it costs one read of the settings, and only for a save that adds an inflow.
    /// </para>
    /// </summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardManufacturingCostHistory();
        await BranchCodeAssignment.AssignAsync(this, cancellationToken);
        var changes = await AuthorizationShadow.ApplyAsync(this, cancellationToken);
        var inflows = await ValuationGapsOnInflow.CandidatesAsync(this, cancellationToken);
        if (changes.IsEmpty && inflows.Count == 0) return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        await using var scope = await BeginWriteScopeAsync(cancellationToken);
        var written = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (!changes.IsEmpty) await AuthorizationVersions.BumpAsync(this, changes, cancellationToken);
        if (inflows.Count > 0 && await ValuationGapsOnInflow.PostAsync(this, inflows, GapLogger(), cancellationToken) > 0)
            written += await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return written;
    }

    private ILogger GapLogger()
    {
        try
        {
            return this.GetService<ILoggerFactory>().CreateLogger(typeof(ValuationGapsOnInflow).FullName!);
        }
        catch (InvalidOperationException)
        {
            return NullLogger.Instance;
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardManufacturingCostHistory();
        BranchCodeAssignment.Assign(this);
        var changes = AuthorizationShadow.ApplyAsync(this, CancellationToken.None).GetAwaiter().GetResult();
        if (changes.IsEmpty) return base.SaveChanges(acceptAllChangesOnSuccess);

        using var owned = Database.CurrentTransaction is null ? Database.BeginTransaction() : null;
        var written = base.SaveChanges(acceptAllChangesOnSuccess);
        AuthorizationVersions.Bump(this, changes);
        owned?.Commit();
        return written;
    }

    /// <summary>Set only while <see cref="AuthorizationBootstrap"/> writes the Admin baseline.</summary>
    internal bool AdminBaselineWriteAllowed { get; private set; }

    /// <summary>
    /// A B1 sheet records what was known at one instant. Altering it would turn an audit trail into
    /// a recomputation; new information must instead be a separately keyed late-cost record.
    /// </summary>
    private void GuardManufacturingCostHistory()
    {
        var changed = ChangeTracker.Entries()
            .FirstOrDefault(entry => entry.Entity is IManufacturingCostHistory
                && entry.State is EntityState.Modified or EntityState.Deleted);
        if (changed is not null)
            throw new InvalidOperationException("Manufacturing cost history is immutable; record a separate late cost instead.");
    }

    internal IDisposable AllowAdminBaselineWrite()
    {
        AdminBaselineWriteAllowed = true;
        return new Release(() => AdminBaselineWriteAllowed = false);
    }

    private sealed class Release(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    public async Task SaveWithVersionsAsync(CancellationToken ct, params SyncEntity[] entities)
    {
        if (Database.CurrentTransaction is not null)
        {
            foreach (var entity in entities) await TouchAsync(entity, ct);
            await SaveChangesAsync(ct);
            return;
        }
        await using var tx = await BeginWriteAsync(ct);
        foreach (var entity in entities) await TouchAsync(entity, ct);
        await SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
