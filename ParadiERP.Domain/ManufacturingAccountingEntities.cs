using System.ComponentModel.DataAnnotations;

namespace ParadiERP.Domain;

/// <summary>
/// The manufacturing costing and accounting choices the accountant has not answered yet (costing plan section 7),
/// set by the admin in the meantime (owner, 2026-11-08: "for the things that need a decision, make it settings
/// and the admin can choose"). One row. The defaults are the suggested answers and change no figure.
/// <para>
/// What follows each choice: the cost previews and reports (Phase A, B2) read the overhead, release rule and
/// burden at once; a frozen cost sheet (B1) keeps the policy it was frozen under; the practice ledger (Phase C)
/// reads the model and the month-end, late-cost and variance choices. Nothing here posts to the ledger.
/// </para>
/// </summary>
public class ManufacturingCostingSettings
{
    public const int SingletonId = 1;
    public int Id { get; set; } = SingletonId;

    /// <summary>C-03: costs stay by nature and production enters stock through production stocked (suggested), or flow through work in progress.</summary>
    public ManufacturingAccountingModel Model { get; set; } = ManufacturingAccountingModel.ByNature;

    /// <summary>C-08: overhead charged per booked labour hour; 0 charges none (suggested until a rate is set).</summary>
    public decimal OverheadPerLabourHour { get; set; }

    /// <summary>C-11: how a release is valued while the order is open; the last release of a completed order carries the rest.</summary>
    public ManufacturingReleaseRule ReleaseRule { get; set; } = ManufacturingReleaseRule.StandardMaterialPlusConversion;

    /// <summary>C-06: whether an employer burden stands in for the employer social contribution new payroll runs leave out, and where.</summary>
    public EmployerBurdenUse EmployerBurdenUse { get; set; } = EmployerBurdenUse.None;

    /// <summary>C-06: the burden, a percentage of the hourly rate, for payroll months whose payslips carried no employer social.</summary>
    public decimal EmployerBurdenPercent { get; set; }

    /// <summary>C-15: open orders valued in work in progress at every month end and reversed on the first (suggested), or only at year end.</summary>
    public MonthEndWorkInProgress MonthEndWorkInProgress { get; set; } = MonthEndWorkInProgress.Monthly;

    /// <summary>C-10: a cost known after an order was completed stays in its charges and is reported (suggested), or goes to a variance account.</summary>
    public LateCostTreatment LateCosts { get; set; } = LateCostTreatment.StayInCharges;

    /// <summary>C-13: planned-against-actual differences are management figures only (suggested), or posted to a variance account.</summary>
    public VarianceTreatment Variances { get; set; } = VarianceTreatment.ManagementOnly;

    /// <summary>
    /// C-07: an order expense counts before the VAT the company recovers (suggested), at <see cref="ExpenseVatPercent"/>,
    /// or as recorded. With the rate at 0, both give the recorded amount.
    /// </summary>
    public ExpenseBasis ExpenseBasis { get; set; } = ExpenseBasis.BeforeRecoverableVat;
    public decimal ExpenseVatPercent { get; set; }

    /// <summary>C-09: rejects kept as scrap recover nothing (suggested until a value is known), or a share of their standard material value.</summary>
    public ScrapRecoveryBasis ScrapRecovery { get; set; } = ScrapRecoveryBasis.None;
    public decimal ScrapRecoveryPercent { get; set; }

    /// <summary>
    /// C-05: how direct labour is absorbed — at the employee-month's blended actual rate on every worked hour, overtime
    /// premium included (suggested, and how labour was always costed), or with payroll's overtime premium kept to the
    /// hours a booking marks as overtime (docs/manufacturing/phase-c.md, "Labour absorption").
    /// </summary>
    public ManufacturingLabourAbsorption LabourAbsorption { get; set; } = ManufacturingLabourAbsorption.BlendedActualRate;

    /// <summary>
    /// C-12: whether manufacturing may move stock — create orders, issue, record, release (owner, 2026-11-09: "make
    /// everything as settings and accountant set it in cpanel"). Off (suggested) until live costing; the server's
    /// <c>Manufacturing:StockOperations</c> still switches it on for test and development databases.
    /// </summary>
    public bool StockOperations { get; set; }

    /// <summary>
    /// The accountant confirms the choices above (costing plan §7, in the panel instead of on paper). Until then every
    /// cost sheet stays provisional; confirmed, a sheet whose inputs are all final reads approved. Changing a choice
    /// asks for the confirmation again.
    /// </summary>
    public bool AccountantApproved { get; set; }
    [MaxLength(200)] public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }

    /// <summary>
    /// Phase D: the first month whose practice entries the accountant may post to the ledger (its first day); none, and
    /// nothing posts (the default). Set in the panel like every other choice.
    /// </summary>
    public DateOnly? PostingFromMonth { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }
    [MaxLength(200)] public string? UpdatedBy { get; set; }
}

public enum ManufacturingAccountingModel { ByNature = 0, WorkInProgress = 1 }

/// <summary>The same values as the engine's <c>ReleaseValuationRule</c>.</summary>
public enum ManufacturingReleaseRule { StandardMaterialPlusConversion = 0, EstimatedUnitCost = 1, CostToDate = 2 }

public enum EmployerBurdenUse
{
    /// <summary>Labour costs what payroll recorded (C-06 option 1).</summary>
    None = 0,
    /// <summary>The burden in cost reports and previews only; stock value and the practice ledger leave it out (option 2).</summary>
    ReportsOnly = 1,
    /// <summary>The burden in stock value too: frozen cost sheets and the practice ledger carry it (option 3, not suggested).</summary>
    StockValue = 2,
}

public enum MonthEndWorkInProgress { Monthly = 0, YearEndOnly = 1 }

/// <summary>C-05. The same values as the engine's <c>LabourAbsorption</c>.</summary>
public enum ManufacturingLabourAbsorption { BlendedActualRate = 0, PremiumToOvertime = 1 }

public enum LateCostTreatment { StayInCharges = 0, VarianceAccount = 1 }

public enum VarianceTreatment { ManagementOnly = 0, VarianceAccount = 1 }

/// <summary>
/// One posting of a month's practice entries to the ledger (Phase D): who posted it and when, how many entries, and —
/// once reversed — who reversed it. A month is posted again only after its last posting was reversed, under the next
/// <see cref="Generation"/>, so each posting's entries keep their own source ids.
/// </summary>
public class ManufacturingMonthPosting : ILedgerMonthPosting
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Year { get; set; }
    public int Month { get; set; }
    public int Generation { get; set; }
    public int Entries { get; set; }
    /// <summary>How many practice entries the month had when posted: each one's source id is drawn from its index, so a reversal finds them all.</summary>
    public int Indexes { get; set; }
    public decimal TotalDebit { get; set; }
    public DateTime PostedAtUtc { get; set; }
    [MaxLength(200)] public string? PostedBy { get; set; }
    public DateTime? ReversedAtUtc { get; set; }
    [MaxLength(200)] public string? ReversedBy { get; set; }
}

/// <summary>
/// One posting of a month of practice entries to the ledger, whatever module they come from (manufacturing's Phase D,
/// maintenance's Phase M6): the shape the shared month poster reads and writes. Each module keeps its own table.
/// </summary>
public interface ILedgerMonthPosting
{
    int Year { get; set; }
    int Month { get; set; }
    int Generation { get; set; }
    int Entries { get; set; }
    int Indexes { get; set; }
    decimal TotalDebit { get; set; }
    DateTime PostedAtUtc { get; set; }
    string? PostedBy { get; set; }
    DateTime? ReversedAtUtc { get; set; }
    string? ReversedBy { get; set; }
}

/// <summary>
/// One posting of a month of maintenance practice entries to the ledger (Phase M6, docs/maintenance/plan.md §5.3): the same
/// shape and rules as <see cref="ManufacturingMonthPosting"/>, in its own table, under source <c>maintenance</c>.
/// </summary>
public class MaintenanceMonthPosting : ILedgerMonthPosting
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Year { get; set; }
    public int Month { get; set; }
    public int Generation { get; set; }
    public int Entries { get; set; }
    /// <summary>How many practice entries the month had when posted: each one's source id is drawn from its index.</summary>
    public int Indexes { get; set; }
    public decimal TotalDebit { get; set; }
    public DateTime PostedAtUtc { get; set; }
    [MaxLength(200)] public string? PostedBy { get; set; }
    public DateTime? ReversedAtUtc { get; set; }
    [MaxLength(200)] public string? ReversedBy { get; set; }
}

public enum ExpenseBasis { BeforeRecoverableVat = 0, AsRecorded = 1 }

public enum ScrapRecoveryBasis { None = 0, PercentOfMaterial = 1 }

/// <summary>
/// One line of the manufacturing account map (costing plan §4.1, C-04): which ledger account a role of goods or
/// an event books to. Seeded empty: a slot with no account shows as unmapped in the practice ledger and is never
/// guessed. Merchandise keeps today's accounts and is not in the map.
/// </summary>
public class ManufacturingAccountMapping
{
    /// <summary>One of <c>ManufacturingAccountSlots</c>' keys.</summary>
    [Key, MaxLength(64)] public string Slot { get; set; } = string.Empty;
    public Guid? LedgerAccountId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    [MaxLength(200)] public string? UpdatedBy { get; set; }
}

/// <summary>
/// The account map's slots: the stock and consumption account of each material role, then the accounts every
/// order uses. Fixed keys, stored as text; a new slot is added here and never renamed.
/// </summary>
public static class ManufacturingAccountSlots
{
    public const string RawMaterialStock = "stock.raw_material";
    public const string RawMaterialConsumption = "consumption.raw_material";
    public const string SuppliesStock = "stock.supplies";
    public const string SuppliesConsumption = "consumption.supplies";
    public const string SemiFinishedStock = "stock.semi_finished";
    public const string FinishedGoodsStock = "stock.finished";
    public const string ScrapStock = "stock.scrap";
    public const string WorkInProgress = "work_in_progress";
    public const string ProductionStocked = "production_stocked";
    public const string LabourAbsorbed = "labour_absorbed";
    public const string OverheadAbsorbed = "overhead_absorbed";
    public const string ExpensesAbsorbed = "expenses_absorbed";
    public const string Variance = "variance";
    /// <summary>C-16: the gap when a later inflow covers a negative quantity, at the switch to the weighted average.</summary>
    public const string ValuationDifference = "valuation_difference";
    /// <summary>C-02: the difference between the opening valuation and the stock accounts, at the switch.</summary>
    public const string OpeningAdjustment = "opening_adjustment";

    public static readonly IReadOnlyList<string> All =
    [
        RawMaterialStock, RawMaterialConsumption, SuppliesStock, SuppliesConsumption, SemiFinishedStock,
        FinishedGoodsStock, ScrapStock, WorkInProgress, ProductionStocked,
        LabourAbsorbed, OverheadAbsorbed, ExpensesAbsorbed, Variance, ValuationDifference, OpeningAdjustment,
    ];
}

/// <summary>
/// MS-31 (docs/maintenance/plan.md §5.2, Phase M6): maintenance's slots of the same account map, rows of
/// <see cref="ManufacturingAccountMapping"/> under a <c>maintenance.</c> prefix. Seeded empty and never guessed; edited in
/// Maintenance › Settings under <c>accounting.manage</c>. Manufacturing's own list (<see cref="ManufacturingAccountSlots.All"/>)
/// does not include them, so its page and practice ledger are unchanged.
/// <para>
/// A part leaving stock is credited to the stock slot of its role, shared with manufacturing (raw material; supplies for
/// consumables, accessories and spare parts; semi-finished; finished goods). Plain merchandise (no role) is credited to the
/// chart's stock account (<see cref="LedgerAccounts.Inventory"/>), the account its receipt debited: <see cref="MerchandiseStock"/>
/// stands for it and is not in the map.
/// </para>
/// </summary>
public static class MaintenanceAccountSlots
{
    /// <summary>Repairs on own equipment (internal and recharged jobs): parts, and labour and overhead when moved (MS-33). Hint: the repair account.</summary>
    public const string OwnParts = "maintenance.own_parts";
    /// <summary>Parts issued to a customer job (MNT-06).</summary>
    public const string CustomerParts = "maintenance.customer_parts";
    /// <summary>Labour moved into jobs (MS-33 "moved"): the credit against the wage charges.</summary>
    public const string LabourAbsorbed = "maintenance.labour_absorbed";
    /// <summary>Overhead moved into jobs (MS-10 with overhead and MS-33 "moved"): the credit.</summary>
    public const string OverheadAbsorbed = "maintenance.overhead_absorbed";
    /// <summary>A customer job's service cost: its labour and overhead when moved; the credit of its work in progress (MS-36).</summary>
    public const string ServiceCost = "maintenance.service_cost";
    /// <summary>Open customer jobs' work in progress at a period end (MS-36).</summary>
    public const string WorkInProgress = "maintenance.wip";

    /// <summary>MS-37 "provision" (Phase M7c): the charge of the provision for rework under guarantee; credited when it is released.</summary>
    public const string ProvisionExpense = "maintenance.provision_expense";
    /// <summary>MS-37 "provision" (Phase M7c): the provision held for rework under guarantee, a liability.</summary>
    public const string ProvisionLiability = "maintenance.provision_liability";

    /// <summary>
    /// MS-30 "capitalised" (Phase M7d): own work capitalised — the credit of a capitalised repair's labour and overhead that the
    /// books carry outside maintenance's slots (MS-33 "report only": the wages stay in 631/635). SCF "production immobilisée"
    /// (73x) is the usual answer; never guessed, no hint.
    /// </summary>
    public const string Capitalised = "maintenance.capitalised";

    /// <summary>
    /// MS-34 (one service account, or parts and labour apart): the income a customer job's invoice credits for its service —
    /// the whole price, or the price less the parts. An active income account of class 7 only; never guessed, no hint. The
    /// default (the account recharges use, 707) needs none.
    /// </summary>
    public const string ServiceIncome = "maintenance.service_income";

    /// <summary>Plain merchandise's stock: always the chart's stock account, never mapped (plan §5.2).</summary>
    public const string MerchandiseStock = "maintenance.merchandise_stock";

    /// <summary>
    /// A line on an account the event itself already named, not a slot of the map (Phase M7c): a rework's outside work taken
    /// from the provision is credited back to the expense account its payment voucher debited. The code follows the prefix.
    /// </summary>
    public const string AccountPrefix = "account:";
    public static string Account(string code) => AccountPrefix + code;

    /// <summary>The slots the accountant maps in Maintenance › Settings, in their order on the page.</summary>
    public static readonly IReadOnlyList<string> All =
        [OwnParts, CustomerParts, LabourAbsorbed, OverheadAbsorbed, ServiceCost, WorkInProgress, ProvisionExpense, ProvisionLiability, Capitalised,
         ServiceIncome];

    /// <summary>The slots MS-37 "provision" posts to: chosen only once both name an account, never guessed.</summary>
    public static readonly IReadOnlyList<string> Provision = [ProvisionExpense, ProvisionLiability];

    /// <summary>MS-34: what <see cref="ServiceIncome"/> may name — an active income account of class 7 (SCF "comptes de produits").</summary>
    public static bool IsIncomeAccount(LedgerAccount a) =>
        a.IsActive && a.Type == LedgerAccountType.Income && a.Code.StartsWith('7');

    /// <summary>Manufacturing's stock slots a part may leave, shown beside the maintenance slots (mapped in Costing settings).</summary>
    public static readonly IReadOnlyList<string> SharedStock =
    [
        ManufacturingAccountSlots.RawMaterialStock, ManufacturingAccountSlots.SuppliesStock,
        ManufacturingAccountSlots.SemiFinishedStock, ManufacturingAccountSlots.FinishedGoodsStock,
    ];

    /// <summary>Today's account for the same event, shown as a hint only (MS-31): never used unless the accountant names it.</summary>
    public static string? HintFor(string slot) => slot switch
    {
        OwnParts => LedgerAccounts.MaintenanceExpense,
        CustomerParts => LedgerAccounts.CostOfGoodsSold,
        _ => null,
    };
}
