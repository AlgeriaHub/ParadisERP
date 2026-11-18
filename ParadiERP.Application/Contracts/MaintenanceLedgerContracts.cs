namespace ParadiERP.Application.Contracts;

/// <summary>
/// One slot of the maintenance account map (MS-31): the account it names (null: unmapped, never guessed), today's account
/// for the same event as a hint only, and whether it is one of manufacturing's stock slots, shown here and mapped in
/// Manufacturing › Costing settings (<see cref="Shared"/>).
/// </summary>
public record MaintenanceAccountSlotDto(
    string Slot, Guid? LedgerAccountId, string? AccountCode, string? AccountNameAr, string? AccountNameFr, string? AccountNameEn,
    string? HintCode, bool Shared, DateTime? UpdatedAtUtc, string? UpdatedBy);

/// <summary>A parts document posted at issue (MS-32 "at issue"): already in the ledger, so not in the month's entries.</summary>
public record MaintenancePostedAtIssueDto(string Document, Guid JobId, string JobNumber, DateOnly Day, decimal Value);

/// <summary>An open customer job at the month's end (MS-36): its cost to date, or null when it would give a pay rate back.</summary>
public record MaintenanceOpenJobDto(Guid JobId, string JobNumber, Guid BranchId, string BranchCode, decimal? CostToDate);

/// <summary>A customer job's invoice of the month, posted by the invoice itself; listed for the tie-out.</summary>
public record MaintenanceLedgerInvoiceDto(string Number, DateOnly Day, Guid? JobId, string? JobNumber, bool JobMatched, decimal NetAmount);

/// <summary>
/// The maintenance section of the practice ledger for one month (Phase M6, docs/maintenance/plan.md §5): the entries the
/// accountant's choices would make, never posted until the month is; each slot's total; the checks; the slots used that name no
/// account; the documents already posted at issue; open customer jobs; customer jobs' invoices; and the month's postings.
/// <see cref="BranchId"/> is the branch read (null: every branch). The entry DTOs are manufacturing's: an entry's order is the job.
/// </summary>
public record MaintenancePracticeLedgerDto(
    int Year, int Month, bool MonthToDate, Guid? BranchId,
    string LabourInBooks, string OpenJobsAtPeriodEnd, string PartsPosting, string OwnRepairs, bool AccountantApproved,
    IReadOnlyList<PracticeLedgerEntryDto> Entries, IReadOnlyList<PracticeLedgerAccountDto> Accounts,
    IReadOnlyList<PracticeLedgerCheckDto> Checks, IReadOnlyList<string> UnmappedSlots, IReadOnlyList<string> Notes,
    decimal TotalDebit, decimal TotalCredit,
    IReadOnlyList<MaintenancePostedAtIssueDto> PostedAtIssue, IReadOnlyList<MaintenanceOpenJobDto> OpenCustomerJobs,
    IReadOnlyList<MaintenanceLedgerInvoiceDto> Invoices,
    string? PostingFromMonth, IReadOnlyList<PracticeLedgerPostingDto> Postings,
    // Phase M7c: MS-37 as saved, and the guarantee provisions the month moves (booked, used by reworks, released).
    string Rework = "AddedToOriginalJob", IReadOnlyList<MaintenanceLedgerProvisionDto>? Provisions = null);

/// <summary>
/// A guarantee provision the month moves (MS-37 "provision", Phase M7c): the customer job and invoice that set it aside, the
/// amount, what this month booked, what its reworks used, what was released, what is held at the month's end, and the last
/// day of the guarantee.
/// </summary>
public record MaintenanceLedgerProvisionDto(
    Guid JobId, string JobNumber, string? InvoiceNumber, decimal Amount, decimal Booked, decimal Used, decimal Released,
    decimal HeldAtEnd, DateOnly GuaranteeUntil);
