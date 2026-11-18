using System.ComponentModel.DataAnnotations;

namespace ParadisERP.Domain;

/// <summary>
/// Maintenance › Settings (docs/maintenance/plan.md §4, Phase M0): every maintenance decision as a setting the admin
/// or the accountant sets in the panel (owner, 2026-11-08/09). One row. The first option of each choice is the
/// default and the suggested answer, and every default keeps today's behaviour: the module is off, nothing posts, and
/// nothing in rentals or stock changes. In M0 nothing reads these choices yet; each later phase reads its own.
/// <para>
/// MS-01 to MS-26 are the operational settings, changed under <c>maintenance.settings</c>; MS-30 to MS-40 are the
/// accounting ones, changed under <c>accounting.manage</c>. MS-24 (an internal charge) was dropped when the owner
/// chose company profit (2026-11-09, plan §3.3). MS-31, the account map's maintenance slots, is not a column: the
/// slots join the manufacturing account map in Phase M6.
/// </para>
/// </summary>
public class MaintenanceSettings
{
    public const int SingletonId = 1;
    public int Id { get; set; } = SingletonId;

    // ---------- Module and jobs (maintenance.settings) ----------

    /// <summary>MS-01: the maintenance module — its sidebar module, every route and the waiting-for-repair list. Off by default.</summary>
    public MaintenanceSwitch Module { get; set; } = MaintenanceSwitch.Off;

    /// <summary>MS-02: whether units may still leave maintenance by the manual state move, or only by a job.</summary>
    public MaintenanceUnitRelease UnitsLeaveMaintenance { get; set; } = MaintenanceUnitRelease.ByHandOrByJob;

    /// <summary>MS-03: a damaged return-note line is offered in Waiting for repair, or opens a draft job by itself.</summary>
    public MaintenanceReturnNoteJobs JobsFromReturnNotes { get; set; } = MaintenanceReturnNoteJobs.OfferedInWaitingList;

    /// <summary>MS-04: who may be booked on a job.</summary>
    public MaintenanceBookable BookableEmployees { get; set; } = MaintenanceBookable.AnyEmployeeOfBranch;

    /// <summary>MS-05: who completes a job — any holder of maintenance.jobs.close, or someone other than its opener.</summary>
    public MaintenanceJobCloser JobCloser { get; set; } = MaintenanceJobCloser.AnyHolder;

    /// <summary>MS-06: jobs for outside customers (MNT-06). Off by default.</summary>
    public MaintenanceSwitch CustomerJobs { get; set; } = MaintenanceSwitch.Off;

    // ---------- Cost (maintenance.settings) ----------

    /// <summary>MS-10: what a job's cost includes (MNT-04, suggested A).</summary>
    public MaintenanceCostScope CostIncludes { get; set; } = MaintenanceCostScope.PartsLabourOutsideWork;

    /// <summary>MS-11: overhead per labour hour (MNT-04 B); 0 charges none.</summary>
    public decimal OverheadPerLabourHour { get; set; }

    /// <summary>MS-12: labour valued at the payroll-derived rate, as manufacturing (MNT-05), or one workshop rate per hour.</summary>
    public MaintenanceLabourRate LabourRate { get; set; } = MaintenanceLabourRate.PayrollDerived;

    /// <summary>MS-12: the workshop rate per hour, when <see cref="LabourRate"/> is <see cref="MaintenanceLabourRate.WorkshopRate"/>; 0 otherwise.</summary>
    public decimal WorkshopRatePerHour { get; set; }

    /// <summary>MS-13: the employer burden and the expense VAT follow Manufacturing › Costing settings (C-06, C-07), or the two values below.</summary>
    public MaintenanceBurdenSource BurdenAndVat { get; set; } = MaintenanceBurdenSource.FollowManufacturing;
    public decimal EmployerBurdenPercent { get; set; }
    public decimal ExpenseVatPercent { get; set; }

    // ---------- Pricing (maintenance.settings) ----------

    /// <summary>MS-20: how a customer job is priced.</summary>
    public MaintenancePriceBasis CustomerPrice { get; set; } = MaintenancePriceBasis.Quoted;

    /// <summary>MS-21: the labour charge rate per hour, for the itemised price; 0.</summary>
    public decimal LabourChargePerHour { get; set; }

    /// <summary>MS-22: the markup, a percentage on outside work (itemised) or on cost (cost-plus); 0.</summary>
    public decimal MarkupPercent { get; set; }

    /// <summary>MS-23: the return note's repair recharge is typed at return, as today, or suggested from a repair rate per product (REN-11).</summary>
    public MaintenanceRechargeEntry ReturnRecharge { get; set; } = MaintenanceRechargeEntry.TypedAtReturn;

    /// <summary>MS-25: a customer price below cost is shown as a loss, or needs a maintenance.pricing holder's confirmation. It never refuses.</summary>
    public MaintenanceBelowCost PriceBelowCost { get; set; } = MaintenanceBelowCost.ShownAsLoss;

    /// <summary>MS-26: a recharge covering units repaired by several jobs is shared by units, or goes to the first job (REN-11).</summary>
    public MaintenanceRechargeShare RechargeSharing { get; set; } = MaintenanceRechargeShare.ByUnits;

    /// <summary>
    /// MS-27 (Phase M7c): the guarantee a customer job carries, in months from the day it is completed (company time). A free
    /// rework (MS-37) may be opened against it until that day included; under the provision, the unused provision is released
    /// then. Frozen on each job at its completion (<see cref="MaintenanceJob.GuaranteeUntil"/>), so a later change never moves
    /// a guarantee already given. 0: no guarantee — no rework is linked and no provision is set aside. Default 3.
    /// </summary>
    public int GuaranteeMonths { get; set; } = 3;

    // ---------- Accounting (accounting.manage) ----------

    /// <summary>
    /// MS-30: repairs on own equipment (MNT-01). Always an expense (default), or capitalised above an amount (Phase M7d): taken
    /// only while the REN-12 register holds the hire equipment and its slots are mapped.
    /// </summary>
    public MaintenanceOwnRepairs OwnRepairs { get; set; } = MaintenanceOwnRepairs.AlwaysExpense;

    /// <summary>
    /// MS-30 "capitalised above an amount" (Phase M7d): a repair job on own hire equipment whose cost (M4) is above this amount
    /// is added to the equipment's pool at completion. Kept only under that option, and then above 0; 0 otherwise.
    /// </summary>
    public decimal CapitaliseAboveAmount { get; set; }

    /// <summary>MS-32: parts reach the ledger through the practice ledger, posted by month by the accountant, or at issue (MNT-02).</summary>
    public MaintenancePartsPosting PartsPosting { get; set; } = MaintenancePartsPosting.PracticeLedgerByMonth;

    /// <summary>MS-33: labour stays a wage cost and is reported only, or is moved into jobs in the practice ledger (MNT-05).</summary>
    public MaintenanceLabourBooks LabourInBooks { get; set; } = MaintenanceLabourBooks.ReportOnly;

    /// <summary>MS-34: where customer repair income is booked (MNT-06): the account recharges use today, one service account, or parts and labour apart.</summary>
    public MaintenanceIncomeAccount CustomerIncome { get; set; } = MaintenanceIncomeAccount.SameAsRecharges;

    /// <summary>
    /// MS-35: advances on customer jobs (MNT-07, D45). Taken (M7b) only while customer advances are held on 419
    /// (<see cref="ReceivableSettings.AdvancesHeldOn"/>): an advance names the job and clears against the job's invoice.
    /// </summary>
    public MaintenanceAdvances Advances { get; set; } = MaintenanceAdvances.NotTaken;

    /// <summary>MS-36: open customer jobs at period end (MNT-08).</summary>
    public MaintenanceOpenJobs OpenJobsAtPeriodEnd { get; set; } = MaintenanceOpenJobs.LeftInExpensesListed;

    /// <summary>
    /// MS-37: free rework under guarantee (MNT-09). In reports a rework's cost is added to the job it redoes (default) or
    /// shown as its own job; or (Phase M7c) a provision is set aside at each customer job's invoice and a rework's cost is
    /// taken from it in the practice ledger.
    /// </summary>
    public MaintenanceRework Rework { get; set; } = MaintenanceRework.AddedToOriginalJob;

    /// <summary>
    /// MS-37 "provision" (Phase M7c): the share of a customer job's invoice net before tax set aside for rework under
    /// guarantee, 0 to 100 %. Kept only under <see cref="MaintenanceRework.Provision"/>, and then above 0; 0 otherwise.
    /// Frozen on each job at its invoice (<see cref="MaintenanceJob.ProvisionAmount"/>).
    /// </summary>
    public decimal ProvisionPercent { get; set; }

    /// <summary>
    /// MS-38: repair history per equipment type with an optional item reference (default), or per piece above a value (MNT-10,
    /// Phase M7d): a job on a type whose unit value is above <see cref="PerPieceAboveValue"/> names one piece by its reference.
    /// </summary>
    public MaintenanceHistory RepairHistory { get; set; } = MaintenanceHistory.PerTypeWithReference;

    /// <summary>
    /// MS-38 "per piece" (Phase M7d): the unit value above which a piece is tracked on its own — the product's asset value when it
    /// has one, its cost price otherwise. Kept only under that option, and then above 0; 0 otherwise.
    /// </summary>
    public decimal PerPieceAboveValue { get; set; }

    /// <summary>MS-39: the first month whose maintenance entries the accountant may post (its first day); none, and nothing posts (the default).</summary>
    public DateOnly? PostingFromMonth { get; set; }

    /// <summary>
    /// MS-40: the accountant confirms the cost and accounting choices (MS-10 to MS-13, MS-30 to MS-38). Until then job
    /// costs read provisional. Changing a covered choice asks for the confirmation again.
    /// </summary>
    public bool AccountantApproved { get; set; }
    [MaxLength(200)] public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }
    [MaxLength(200)] public string? UpdatedBy { get; set; }
}

/// <summary>
/// A repair job (Phase M1, docs/maintenance/plan.md §2.2), numbered <c>MJ-000001</c> and held by one branch: the branch
/// where the work is done and where its units sit in maintenance. Server-only, never a <c>SyncEntity</c>: a till never
/// pulls it. M1 builds internal and recharged jobs; a customer job (MS-06) is refused until Phase M5.
/// <para>
/// <b>Units.</b> A job takes units already in the maintenance state of its branch (<see cref="ProductStock.MaintenanceQty"/>):
/// a draft or open job holds them, so no two jobs can take the same unit. Completing moves <see cref="RepairedQty"/>
/// maintenance → available and <see cref="ScrappedQty"/> maintenance → scrap through the same state move as
/// <c>POST /api/catalog/stock/state</c> (the same <c>StockMovement</c>, the same Dr 318 / Cr 310 for scrap).
/// Cancelling moves nothing: the units stay in maintenance and are free for another job.
/// </para>
/// <para>
/// Concurrency: every write reads the job under the version lock (<c>PosDbContext.LockVersionsAsync</c>), as production
/// orders do, so a second completion sees the first one's status and is refused.
/// </para>
/// </summary>
public class MaintenanceJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(20)] public string Number { get; set; } = string.Empty;
    public Guid BranchId { get; set; }
    public MaintenanceJobKind Kind { get; set; }
    public MaintenanceJobStatus Status { get; set; }

    /// <summary>
    /// The equipment type and how many units: tracked by type, not by serial (MNT-10, MS-38). Always set on an internal
    /// or recharged job (its units are the company's, in maintenance). Optional on a customer job (Phase M5a): the
    /// outside equipment is described in <see cref="Equipment"/> and may name a catalogue product only to say what it is.
    /// </summary>
    public Guid? ProductId { get; set; }
    public Product? Product { get; set; }
    /// <summary>How many units (internal and recharged: the company's units it holds in maintenance); on a customer job, how many pieces the customer brought.</summary>
    public decimal Qty { get; set; }

    // ---------- Phase M5a: a customer job (MS-06) ----------

    /// <summary>A customer job: the existing customer who brought the equipment and pays the invoice.</summary>
    public Guid? CustomerId { get; set; }

    /// <summary>A customer job: the outside equipment in words (a make, a model, what it is). Never company stock.</summary>
    [MaxLength(200)] public string? Equipment { get; set; }

    /// <summary>The basis that priced it (MS-20 at the moment the price was set), or null while unpriced.</summary>
    public MaintenancePriceBasis? PriceBasis { get; set; }
    /// <summary>The agreed price before tax (the plan's <c>QuotedPrice</c>): typed, or the basis's proposal accepted. Frozen once invoiced.</summary>
    public decimal? Price { get; set; }
    /// <summary>What the basis proposed when the price was set (itemised or cost-plus); null under Quoted or when no proposal could be made.</summary>
    public decimal? ProposedPrice { get; set; }
    /// <summary>The job's cost as the cost source gave it when the price was set (MS-25's comparison); null when none was available.</summary>
    public decimal? CostAtPricing { get; set; }
    public Guid? PricedByUserId { get; set; }
    [MaxLength(200)] public string? PricedBy { get; set; }
    public DateTime? PricedAtUtc { get; set; }
    /// <summary>MS-25 "needs confirmation": who confirmed a price below the cost, and when.</summary>
    [MaxLength(200)] public string? BelowCostConfirmedBy { get; set; }
    public DateTime? BelowCostConfirmedAtUtc { get; set; }

    /// <summary>
    /// Whether the job's invoice carries VAT. Every bill starts without it; the seller ticks it when the customer asks
    /// (owner, 2026-11-05). Untaxed by default.
    /// </summary>
    public TaxMode TaxMode { get; set; } = TaxMode.Untaxed;

    /// <summary>The one invoice raised from this customer job (<see cref="InvoiceOrigin.MaintenanceJob"/>); a second is refused.</summary>
    public Guid? InvoiceId { get; set; }
    [MaxLength(200)] public string? InvoicedBy { get; set; }
    public DateTime? InvoicedAtUtc { get; set; }

    // ---------- Phase M7c: rework under guarantee and its provision (MS-27, MS-37, MNT-09) ----------

    /// <summary>
    /// A free rework under guarantee: the completed customer job this one redoes (same customer, same branch, opened within
    /// that job's guarantee). Such a job is never priced nor invoiced. Null on every other job.
    /// </summary>
    public Guid? ReworkOfJobId { get; set; }

    /// <summary>
    /// A customer job's guarantee (MS-27): the last company day a rework may be opened against it. Frozen at completion from
    /// the completion day and MS-27's months; null on a rework, a job of another kind, or when MS-27 gave no guarantee.
    /// </summary>
    public DateOnly? GuaranteeUntil { get; set; }

    /// <summary>
    /// MS-37 "provision": the rate (%) and the amount set aside at this job's invoice — the invoice net before tax × the rate,
    /// rounded as money. Frozen at the invoice, so the practice ledger books, uses and releases one fixed sum whatever the
    /// settings say later. Null when the invoice was raised under another treatment (or with no guarantee).
    /// </summary>
    public decimal? ProvisionPercent { get; set; }
    public decimal? ProvisionAmount { get; set; }

    /// <summary>
    /// Optional free text — a serial, a plate, a mixer's number: the piece the job repairs. The repair history reads jobs by product
    /// and this reference (MS-38); under "per piece" a type above the value names it, one piece per job (Phase M7d).
    /// </summary>
    [MaxLength(120)] public string? ItemReference { get; set; }

    // ---------- Phase M7d: a capitalised repair (MS-30) ----------

    /// <summary>
    /// When the repair was capitalised at completion (MS-30 "capitalised above an amount"): its cost went into the equipment's
    /// pool (<c>FixedAssetMovement</c> of kind <c>CapitalisedRepair</c>, <see cref="CapitalisedMovementId"/>), and the practice
    /// ledger of that month moves it out of the accounts it was charged to (<see cref="MaintenanceJobCapitalisedLine"/>). Null on
    /// a repair kept an expense.
    /// </summary>
    public DateTime? CapitalisedAtUtc { get; set; }
    /// <summary>What the pool received: the sum of the capitalised lines, frozen at completion.</summary>
    public decimal? CapitalisedAmount { get; set; }
    public Guid? CapitalisedMovementId { get; set; }

    /// <summary>A recharged job: the return-note line whose repairable units it repairs and whose recharge it earns.</summary>
    public Guid? ContractId { get; set; }
    public Guid? ReturnNoteId { get; set; }
    public Guid? ReturnNoteLineId { get; set; }

    /// <summary>What is wrong, and what was done.</summary>
    [MaxLength(1000)] public string? Description { get; set; }
    [MaxLength(2000)] public string? Diagnosis { get; set; }

    /// <summary>Set at completion: back to available, and found beyond repair (to scrap). Together they are <see cref="Qty"/>.</summary>
    public decimal RepairedQty { get; set; }
    public decimal ScrappedQty { get; set; }

    /// <summary>The request that raised it by hand, or <c>rn:&lt;line id&gt;</c> for a draft a return note opened (MS-03).</summary>
    [MaxLength(64)] public string ClientRequestId { get; set; } = string.Empty;

    public Guid? CreatedByUserId { get; set; }
    [MaxLength(200)] public string? CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Who opened it — the same as the creator for a job opened by hand; the person who confirmed a draft. MS-05 compares the closer with them.</summary>
    public Guid? OpenedByUserId { get; set; }
    [MaxLength(200)] public string? OpenedBy { get; set; }
    public DateTime? OpenedAtUtc { get; set; }
    public Guid? CompletedByUserId { get; set; }
    [MaxLength(200)] public string? CompletedBy { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public Guid? CancelledByUserId { get; set; }
    [MaxLength(200)] public string? CancelledBy { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    [MaxLength(400)] public string? CancelReason { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Who pays decides the kind (plan §2.1): the company, the hire customer through the return note's recharge, or an outside customer (M5).</summary>
public enum MaintenanceJobKind { Internal = 0, Recharged = 1, Customer = 2 }

/// <summary>
/// Draft (opened by a return note under MS-03, waiting for someone to take it) → Open → Completed; or Cancelled from
/// Draft or Open. A completed customer job becomes Invoiced when its invoice is raised (Phase M5a). A draft and an open
/// internal or recharged job hold their units; a customer job holds none (its equipment is not company stock).
/// </summary>
public enum MaintenanceJobStatus { Draft = 0, Open = 1, Completed = 2, Invoiced = 3, Cancelled = 4 }

/// <summary>Each change to a job, kept in <c>MaintenanceJobEvents</c>. Values are only ever appended.</summary>
public enum MaintenanceJobEventKind
{
    Drafted = 0, Opened = 1, Edited = 2, Completed = 3, Cancelled = 4,
    // Phase M2 (parts and outside work) numbers its events from 10, so the phases built beside it (M3, labour) can
    // append from 5 without two kinds ever sharing a value.
    PartsIssued = 10, PartsReturned = 11, ExpenseRecorded = 12,
    // Phase M5a (customer jobs, pricing and invoicing) numbers its events from 20, clear of M4 built beside it.
    Priced = 20, Invoiced = 21,
    // Phase M7d (MS-30) numbers its events from 40, clear of the phases built beside it.
    Capitalisation = 40,
}

/// <summary>
/// One credit line of a capitalised repair's entry (Phase M7d, MS-30), frozen at completion: the element of the job's cost
/// (<c>parts</c>, <c>labour</c>, <c>overhead</c>, <c>outside_work</c>), the slot or account it is taken back from, and the amount.
/// The practice ledger of the completion month posts Dr <c>fixed_assets.asset</c> the sum / Cr each line, so the cost leaves the
/// accounts it was charged to and the pool's asset receives exactly what the register recorded.
/// </summary>
public class MaintenanceJobCapitalisedLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    [MaxLength(20)] public string Element { get; set; } = string.Empty;
    /// <summary>A maintenance or manufacturing slot, or <c>account:&lt;code&gt;</c> for the account a voucher debited.</summary>
    [MaxLength(80)] public string Slot { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

/// <summary>
/// MS-23 (REN-11): what repairing one unit of a product is usually charged to a hire customer. Used only as a
/// suggestion on the return-note screen while MS-23 says "suggested from a rate": the clerk still types the recharge,
/// and the rental engine, its rules and its postings never read this table. One row per product, server-only.
/// </summary>
public class MaintenanceRepairRate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    /// <summary>The usual recharge for repairing one unit, before tax, in the company's currency.</summary>
    public decimal RatePerUnit { get; set; }
    [MaxLength(200)] public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// MS-04's workshop list: who of a branch is workshop staff, so may be booked on its jobs while MS-04 says "only the
/// workshop list". One row per person and branch, server-only. A row counts only while the person still belongs to that
/// branch: someone who moves keeps the row (shown as "now at another branch", to be removed) but is not booked on the old
/// branch's jobs, and is not on the new branch's list until added there. Removing the row deletes it; bookings made before
/// stay as they are.
/// </summary>
public class MaintenanceWorkshopMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public Guid BranchId { get; set; }
    public Guid? AddedByUserId { get; set; }
    [MaxLength(200)] public string? AddedBy { get; set; }
    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>One change to a job: append-only, never updated or deleted.</summary>
public class MaintenanceJobEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public MaintenanceJobEventKind Kind { get; set; }
    [MaxLength(400)] public string? Detail { get; set; }
    public Guid? UserId { get; set; }
    [MaxLength(200)] public string? UserName { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>MS-01, MS-06.</summary>
public enum MaintenanceSwitch { Off = 0, On = 1 }

/// <summary>MS-02.</summary>
public enum MaintenanceUnitRelease { ByHandOrByJob = 0, OnlyByJob = 1 }

/// <summary>MS-03.</summary>
public enum MaintenanceReturnNoteJobs { OfferedInWaitingList = 0, DraftJobPerDamagedLine = 1 }

/// <summary>MS-04.</summary>
public enum MaintenanceBookable { AnyEmployeeOfBranch = 0, WorkshopListOnly = 1 }

/// <summary>MS-05.</summary>
public enum MaintenanceJobCloser { AnyHolder = 0, NotTheOpener = 1 }

/// <summary>MS-10.</summary>
public enum MaintenanceCostScope { PartsLabourOutsideWork = 0, PartsLabourOutsideWorkOverhead = 1, PartsAndOutsideWorkOnly = 2 }

/// <summary>MS-12.</summary>
public enum MaintenanceLabourRate { PayrollDerived = 0, WorkshopRate = 1 }

/// <summary>MS-13.</summary>
public enum MaintenanceBurdenSource { FollowManufacturing = 0, OwnValues = 1 }

/// <summary>MS-20.</summary>
public enum MaintenancePriceBasis { Quoted = 0, Itemised = 1, CostPlus = 2 }

/// <summary>MS-23.</summary>
public enum MaintenanceRechargeEntry { TypedAtReturn = 0, SuggestedFromRate = 1 }

/// <summary>MS-25.</summary>
public enum MaintenanceBelowCost { ShownAsLoss = 0, NeedsConfirmation = 1 }

/// <summary>MS-26.</summary>
public enum MaintenanceRechargeShare { ByUnits = 0, AllToFirstJob = 1 }

/// <summary>MS-30.</summary>
public enum MaintenanceOwnRepairs { AlwaysExpense = 0, CapitalisedAboveAmount = 1 }

/// <summary>MS-32.</summary>
public enum MaintenancePartsPosting { PracticeLedgerByMonth = 0, AtIssue = 1 }

/// <summary>MS-33.</summary>
public enum MaintenanceLabourBooks { ReportOnly = 0, MovedIntoJobs = 1 }

/// <summary>MS-34.</summary>
public enum MaintenanceIncomeAccount { SameAsRecharges = 0, OneServiceAccount = 1, PartsAndLabourApart = 2 }

/// <summary>MS-35.</summary>
public enum MaintenanceAdvances { NotTaken = 0, Taken = 1 }

/// <summary>MS-36.</summary>
public enum MaintenanceOpenJobs { LeftInExpensesListed = 0, WorkInProgressAtYearEnd = 1, WorkInProgressMonthly = 2 }

/// <summary>MS-37.</summary>
public enum MaintenanceRework { AddedToOriginalJob = 0, OwnJob = 1, Provision = 2 }

/// <summary>MS-38.</summary>
public enum MaintenanceHistory { PerTypeWithReference = 0, PerPiece = 1 }
