namespace AlgeriaPos.Application.Contracts;

/// <summary>
/// A supplier as the back office writes it (owner, 2026-10-10: a basic suppliers module). The code
/// is the server's; <paramref name="OpeningBalance"/> is read on create only — the same request and
/// the same words as a customer's (<c>owesUs</c>: the supplier owes the company; <c>weOwe</c>: the
/// company owes the supplier).
/// </summary>
public record UpsertSupplierRequest(
    string Name, string? Phone = null, string? Address = null,
    IReadOnlyDictionary<string, string>? FiscalIds = null, string? Note = null,
    CustomerOpeningBalanceRequest? OpeningBalance = null);

/// <summary>
/// A supplier as the list and the card show it. <paramref name="Balance"/> and
/// <paramref name="OpeningBalance"/> are what the company owes: positive, the company owes the
/// supplier; negative, the supplier owes the company.
/// </summary>
public record SupplierDto(
    Guid Id, string Code, string Name, string? Phone, string? Address,
    IReadOnlyDictionary<string, string> FiscalIds, string? Note, decimal Balance,
    decimal OpeningBalance, DateOnly? OpeningBalanceOn, DateTime? OpeningBalanceSetAtUtc, string? OpeningBalanceSetBy);

/// <summary>One line of a supplier statement: account 401 for that supplier, in the order it posted.</summary>
public record SupplierStatementRowDto(
    DateTime Date, string SourceType, Guid SourceId, string? DocumentNumber, string? Description, bool IsReversal,
    decimal Debit, decimal Credit, decimal Balance);

/// <summary>
/// What the company owes one supplier, and how it came to: the balance before the period, every
/// movement in it, the balance at its end. Read from the ledger (401 lines carrying the supplier),
/// with the supplier's own running balance beside it so any drift shows as a number.
/// </summary>
public record SupplierStatementDto(
    Guid SupplierId, string SupplierCode, string SupplierName, DateTime From, DateTime To,
    decimal OpeningBalance, decimal ClosingBalance, decimal TotalDebit, decimal TotalCredit,
    IReadOnlyList<SupplierStatementRowDto> Rows, decimal RecordedBalance, decimal BalanceDifference);

/// <summary>One correction of a supplier's opening balance; <paramref name="Amount"/> is signed like the supplier's balance.</summary>
public record SupplierOpeningCorrectionDto(
    Guid Id, string Number, Guid SupplierId, decimal Amount, DateOnly EffectiveOn, string Reason,
    string? CreatedBy, DateTime CreatedAtUtc);
