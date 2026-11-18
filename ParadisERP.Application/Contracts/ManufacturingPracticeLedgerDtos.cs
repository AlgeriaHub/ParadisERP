namespace ParadisERP.Application.Contracts;

/// <summary>One side of a practice entry: the account-map slot, the account it names (none while unmapped), the amount.</summary>
public record PracticeLedgerLineDto(
    string Slot, Guid? AccountId, string? AccountCode, string? AccountNameAr, string? AccountNameFr, string? AccountNameEn,
    decimal Debit, decimal Credit);

/// <summary>An entry the chosen model would make: what caused it, on which company day, for which order and branch.</summary>
public record PracticeLedgerEntryDto(
    string Kind, DateOnly Day, Guid OrderId, string OrderNumber, Guid BranchId, string BranchCode,
    string BranchNameAr, string BranchNameFr, string BranchNameEn, string? Document, IReadOnlyList<PracticeLedgerLineDto> Lines);

/// <summary>A slot's month: what the entries debit and credit it.</summary>
public record PracticeLedgerAccountDto(
    string Slot, Guid? AccountId, string? AccountCode, string? AccountNameAr, string? AccountNameFr, string? AccountNameEn,
    decimal Debit, decimal Credit);

/// <summary>A cost an order gathered after it closed, this month (C-10).</summary>
public record PracticeLedgerLateCostDto(Guid OrderId, string OrderNumber, string Element, decimal Amount);

public record PracticeLedgerCheckDto(string Code, bool Passed, decimal Expected, decimal Actual);

/// <summary>One posting of the month to the ledger (Phase D), and its reversal once reversed.</summary>
public record PracticeLedgerPostingDto(
    int Generation, int Entries, decimal TotalDebit, DateTime PostedAtUtc, string? PostedBy, DateTime? ReversedAtUtc, string? ReversedBy);

/// <summary>Phase D, request body: the month to post or reverse.</summary>
public record PracticeLedgerMonthRequest(int Year, int Month);

/// <summary>
/// The practice ledger of one month (Phase C2): the entries the admin's chosen model would make, never posted; each
/// slot's total; the late costs; the checks of the events against the entries; the slots used that name no account.
/// </summary>
public record PracticeLedgerDto(
    int Year, int Month, bool MonthToDate,
    string Model, string MonthEndWorkInProgress, string LateCostTreatment, string Variances,
    IReadOnlyList<PracticeLedgerEntryDto> Entries, IReadOnlyList<PracticeLedgerAccountDto> Accounts,
    IReadOnlyList<PracticeLedgerLateCostDto> LateCosts, IReadOnlyList<PracticeLedgerCheckDto> Checks,
    IReadOnlyList<string> UnmappedSlots, IReadOnlyList<string> Notes, decimal TotalDebit, decimal TotalCredit,
    string? PostingFromMonth = null, IReadOnlyList<PracticeLedgerPostingDto>? Postings = null);
