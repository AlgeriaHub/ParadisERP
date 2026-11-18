namespace AlgeriaPos.Application.Contracts;

/// <summary>
/// Maintenance › Settings (Phase M0, docs/maintenance/plan.md §4): every choice by its option's name, the amounts and
/// percentages, the first month that may post (<c>yyyy-MM</c>, or none) and the accountant's confirmation.
/// MS-01 to MS-26 first, then MS-30 to MS-40. MS-24 was dropped (owner, 2026-11-09); MS-31 comes with M6.
/// </summary>
public record MaintenanceSettingsDto(
    // Module and jobs
    string Module, string UnitsLeaveMaintenance, string JobsFromReturnNotes, string BookableEmployees, string JobCloser,
    string CustomerJobs,
    // Cost
    string CostIncludes, decimal OverheadPerLabourHour, string LabourRate, decimal WorkshopRatePerHour, string BurdenAndVat,
    decimal EmployerBurdenPercent, decimal ExpenseVatPercent,
    // Pricing
    string CustomerPrice, decimal LabourChargePerHour, decimal MarkupPercent, string ReturnRecharge, string PriceBelowCost,
    string RechargeSharing,
    // Accounting
    string OwnRepairs, string PartsPosting, string LabourInBooks, string CustomerIncome, string Advances, string OpenJobsAtPeriodEnd,
    string Rework, string RepairHistory, string? PostingFromMonth,
    bool AccountantApproved, string? ApprovedBy, DateTime? ApprovedAtUtc,
    DateTime? UpdatedAtUtc, string? UpdatedBy,
    // Phase M7c: MS-27 the guarantee in months (maintenance.settings), MS-37's provision rate (accounting.manage).
    int GuaranteeMonths = 3, decimal ProvisionPercent = 0m,
    // Phase M7d (accounting.manage): MS-30's amount above which a repair is capitalised, MS-38's unit value above which a piece
    // is tracked on its own; and whether the register holds the hire equipment (MS-30 is chosen only then).
    decimal CapitaliseAboveAmount = 0m, decimal PerPieceAboveValue = 0m, bool AssetRegisterActive = false);

/// <summary>
/// The whole panel, as the page sends it. A field the caller may not change must come back as saved: MS-01 to MS-26
/// need <c>maintenance.settings</c> and MS-30 to MS-40 need <c>accounting.manage</c> (403 otherwise).
/// </summary>
public record UpdateMaintenanceSettingsRequest(
    string Module, string UnitsLeaveMaintenance, string JobsFromReturnNotes, string BookableEmployees, string JobCloser,
    string CustomerJobs,
    string CostIncludes, decimal OverheadPerLabourHour, string LabourRate, decimal WorkshopRatePerHour, string BurdenAndVat,
    decimal EmployerBurdenPercent, decimal ExpenseVatPercent,
    string CustomerPrice, decimal LabourChargePerHour, decimal MarkupPercent, string ReturnRecharge, string PriceBelowCost,
    string RechargeSharing,
    string OwnRepairs, string PartsPosting, string LabourInBooks, string CustomerIncome, string Advances, string OpenJobsAtPeriodEnd,
    string Rework, string RepairHistory, string? PostingFromMonth,
    bool AccountantApproved,
    // Phase M7c: MS-27 (maintenance.settings) and MS-37's provision rate (accounting.manage). Omitted, today's defaults.
    int GuaranteeMonths = 3, decimal ProvisionPercent = 0m,
    // Phase M7d (accounting.manage): MS-30's amount and MS-38's value, each kept only under its option. Omitted, 0.
    decimal CapitaliseAboveAmount = 0m, decimal PerPieceAboveValue = 0m);

// ---------- Phase M1: jobs and units (docs/maintenance/plan.md §2.2) ----------

/// <summary>One job in the list: what, where, how many, and where it stands. Kind and status travel by name.</summary>
public record MaintenanceJobSummaryDto(
    Guid Id, string Number, Guid BranchId, string BranchName, string Kind, string Status,
    Guid? ProductId, string ProductSku, string ProductNameAr, string ProductNameFr, string ProductNameEn,
    decimal Qty, decimal RepairedQty, decimal ScrappedQty, string? ItemReference,
    Guid? ContractId, string? ContractNumber, Guid? ReturnNoteId, string? ReturnNoteNumber,
    DateTime CreatedAtUtc, DateTime? CompletedAtUtc,
    // Phase M5a: a customer job's outside equipment in words, its customer, and its invoice once raised.
    string? Equipment = null, Guid? CustomerId = null, string? JobCustomerName = null, Guid? InvoiceId = null,
    // Phase M7c: a free rework under guarantee names the job it redoes (never priced nor invoiced).
    Guid? ReworkOfJobId = null);

/// <summary>One change to a job, as <c>MaintenanceJobEvents</c> keeps it.</summary>
public record MaintenanceJobEventDto(string Kind, string? Detail, string? UserName, DateTime AtUtc);

/// <summary>
/// A job in full: the summary's fields, the return-note line it repairs (its damaged units and its recharge, typed at
/// return), the words, who did what and when, what its branch holds in maintenance of the product today, MS-05 in force,
/// and every change.
/// </summary>
public record MaintenanceJobDto(
    Guid Id, string Number, Guid BranchId, string BranchName, string Kind, string Status,
    Guid? ProductId, string ProductSku, string ProductNameAr, string ProductNameFr, string ProductNameEn,
    decimal Qty, decimal RepairedQty, decimal ScrappedQty, string? ItemReference,
    Guid? ContractId, string? ContractNumber, Guid? ReturnNoteId, string? ReturnNoteNumber, Guid? ReturnNoteLineId,
    string? CustomerName, decimal? LineDamagedQty, decimal? LineRecharge,
    string? Description, string? Diagnosis,
    string? CreatedBy, DateTime CreatedAtUtc, Guid? OpenedByUserId, string? OpenedBy, DateTime? OpenedAtUtc,
    string? CompletedBy, DateTime? CompletedAtUtc, string? CancelledBy, DateTime? CancelledAtUtc, string? CancelReason,
    decimal InMaintenanceAtBranch, string JobCloser,
    IReadOnlyList<MaintenanceJobEventDto> Events,
    // Phase M5a: a customer job — its customer (CustomerName above is then theirs), the outside equipment in words, the
    // price before tax once set, whether its invoice carries VAT, and the invoice once raised.
    Guid? CustomerId = null, string? Equipment = null, decimal? Price = null, string TaxMode = "Untaxed",
    Guid? InvoiceId = null, string? InvoiceNumber = null,
    // Phase M7c (MS-27, MS-37): the job a free rework redoes; a customer job's guarantee, the provision set aside at its
    // invoice (MS-37 "provision") and the reworks opened against it.
    Guid? ReworkOfJobId = null, string? ReworkOfNumber = null, DateOnly? GuaranteeUntil = null, decimal? ProvisionAmount = null,
    IReadOnlyList<MaintenanceReworkRefDto>? Reworks = null);

/// <summary>A rework under guarantee opened against a customer job: its number and where it stands.</summary>
public record MaintenanceReworkRefDto(Guid Id, string Number, string Status, DateTime CreatedAtUtc);

/// <summary>
/// A completed customer job a free rework may still be opened against (MS-27, MS-37): same customer, same branch, its
/// guarantee running until <c>GuaranteeUntil</c> included.
/// </summary>
public record MaintenanceReworkCandidateDto(
    Guid Id, string Number, string Status, string? Equipment, string? ItemReference, Guid? ProductId,
    DateTime? CompletedAtUtc, DateOnly GuaranteeUntil);

/// <summary>
/// Opens a job by hand (<c>maintenance.jobs.manage</c>). <c>Kind</c> is <c>Internal</c> or <c>Recharged</c>; a
/// <c>Customer</c> job is refused until Phase M5. An internal job names the product; a recharged job names the
/// return-note line, which gives the product. The units must be in maintenance at the branch and not held by another
/// job. <c>BranchId</c> is ignored for a non-admin (the caller's own branch). A retried request (same
/// <c>ClientRequestId</c>) answers the job it already opened.
/// </summary>
public partial record CreateMaintenanceJobRequest(
    string ClientRequestId, Guid? BranchId, string Kind, Guid? ProductId, decimal Qty, Guid? ReturnNoteLineId,
    string? ItemReference, string? Description,
    // Phase M5a, a Customer job (MS-06 on): the existing customer and the outside equipment in words; ProductId optional.
    Guid? CustomerId = null, string? Equipment = null,
    // Phase M7c: a customer job that is a free rework under guarantee names the completed customer job it redoes (same
    // customer and branch, within that job's guarantee, MS-27). Its equipment, product and reference default to that job's.
    Guid? ReworkOfJobId = null);

/// <summary>The words of a draft or open job: its item reference, what is wrong, and what was done; a customer job's equipment too.</summary>
public record UpdateMaintenanceJobRequest(string? ItemReference, string? Description, string? Diagnosis, string? Equipment = null);

/// <summary>Completes an open job: how many of its units are repaired and how many beyond repair; together, all of them.</summary>
public record CompleteMaintenanceJobRequest(decimal RepairedQty, decimal ScrappedQty, string? Diagnosis);

/// <summary>
/// Cancels a draft or open job, with the reason; its units stay in maintenance. Parts still at the job (issued, not
/// returned) are returned first, or <c>PartsUsed</c> says they stay as used (plan §2.3); without either the cancel is
/// refused (409 <c>parts_at_job</c>).
/// </summary>
public record CancelMaintenanceJobRequest(string? Reason, bool PartsUsed = false);

/// <summary>What a branch holds in maintenance of one product, how much of it jobs hold, and what is free for a new job.</summary>
public record MaintenanceBucketDto(
    Guid BranchId, string BranchName, Guid ProductId, string Sku, string NameAr, string NameFr, string NameEn,
    decimal InMaintenance, decimal HeldByJobs, decimal Free);

/// <summary>
/// A return-note line with repairable units no job covers yet (plan §1.2): the contract, the note, the units, how many
/// a job already covers, and the recharge typed at return.
/// </summary>
public record MaintenanceWaitingLineDto(
    Guid ReturnNoteLineId, Guid ReturnNoteId, string ReturnNoteNumber, Guid ContractId, string ContractNumber, string? CustomerName,
    Guid BranchId, string BranchName, Guid ProductId, string Sku, string NameAr, string NameFr, string NameEn,
    DateTime ReturnedAtUtc, decimal DamagedQty, decimal CoveredQty, decimal OpenQty, decimal Recharge);

/// <summary>Maintenance › Waiting for repair: units in maintenance per branch and product, and the uncovered return-note lines.</summary>
public record MaintenanceWaitingDto(IReadOnlyList<MaintenanceBucketDto> Buckets, IReadOnlyList<MaintenanceWaitingLineDto> Lines);

// ---------- Phase M2: parts and outside work (docs/maintenance/plan.md §2.3, §2.4) ----------

/// <summary>
/// Issues parts from the job's branch stock to an open job (<c>Kind</c> = <c>Issue</c>), or brings unused ones back
/// (<c>Return</c>). Quantities in each product's base unit. A retried request (same <c>ClientRequestId</c>) answers the
/// job's parts as they stand and moves nothing again.
/// </summary>
public record MaintenancePartsRequest(string ClientRequestId, string Kind, IReadOnlyList<MaintenancePartLineRequest>? Lines, string? Note);

public record MaintenancePartLineRequest(Guid ProductId, decimal Qty);

/// <summary>Outside work on a job: what it was, what it cost, and optionally the expense payment voucher that paid it.</summary>
public record MaintenanceJobExpenseRequest(string ClientRequestId, string? Description, decimal Amount, Guid? PaymentVoucherId);

/// <summary>
/// One product's parts on a job: issued, returned, still at the job, and their value at the frozen costs (issued less
/// returned). <c>IssueUnitCost</c> is the average the issues went out at: what a return comes back at.
/// </summary>
public record MaintenancePartBalanceDto(
    Guid ProductId, string Sku, string NameAr, string NameFr, string NameEn,
    decimal IssuedQty, decimal ReturnedQty, decimal AtJobQty, decimal IssueUnitCost, decimal Value);

/// <summary>One line of an issue or return document, as its stock movement was valued.</summary>
public record MaintenancePartMovementDto(
    Guid DocumentId, string DocumentNumber, string Kind, Guid ProductId, string Sku, decimal Qty, decimal UnitCost, decimal Value,
    string CostBasis, string? Note, string? UserName, DateTime AtUtc);

/// <summary>Outside work recorded on a job, with the voucher it points at (number and status in words) when it does.</summary>
public record MaintenanceJobExpenseDto(
    Guid Id, string Description, decimal Amount, Guid? PaymentVoucherId, string? VoucherNumber, string? VoucherStatus,
    string? UserName, DateTime AtUtc);

/// <summary>
/// A job's parts and outside work: per product what is at the job, every document line, every expense, and the totals.
/// <c>PartsPosting</c> is MS-32 as saved; parts post nothing in M2 whatever it says (the M2 record in the plan).
/// </summary>
public record MaintenanceJobWorkDto(
    Guid JobId, string JobStatus, IReadOnlyList<MaintenancePartBalanceDto> Parts, IReadOnlyList<MaintenancePartMovementDto> Movements,
    IReadOnlyList<MaintenanceJobExpenseDto> Expenses, decimal PartsValue, decimal PartsAtJobQty, decimal ExpensesTotal, string PartsPosting);

/// <summary>A stocked product the job's branch can issue now: on hand less what production orders there still claim.</summary>
public record MaintenancePartOptionDto(Guid Id, string Sku, string? Barcode, string NameAr, string NameFr, string NameEn, decimal Available);

/// <summary>An expense payment voucher of the job's branch with some of its amount not yet linked to orders' or jobs' expenses.</summary>
public record MaintenanceVoucherOptionDto(
    Guid Id, string Number, string Status, string? ExpenseCategory, string? PayeeName, string? Purpose,
    decimal Amount, decimal Linked, decimal Free, DateTime CreatedAtUtc);

/// <summary>What the job's Parts and Outside work cards offer to pick from.</summary>
public record MaintenanceWorkOptionsDto(IReadOnlyList<MaintenancePartOptionDto> Products, IReadOnlyList<MaintenanceVoucherOptionDto> Vouchers);
