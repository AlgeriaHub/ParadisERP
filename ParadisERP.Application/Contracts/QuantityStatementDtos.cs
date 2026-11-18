namespace ParadisERP.Application.Contracts;

/// <summary>
/// The customer quantity statement (كشف كميات العميل — Relevé des quantités client): what went out
/// to one customer, what came back, and what is still with them as of <see cref="To"/>, with the
/// rental value of the window. No money owed or paid appears here — that is the account statement.
/// </summary>
/// <param name="From">First day of the window, in the company's time zone.</param>
/// <param name="To">Last day of the window, included ("as of" the end of that day).</param>
/// <param name="Current">The "current quantities" view: as of today, no window of its own.</param>
/// <param name="FirstIssueOn">
/// The customer's first issue note. Paper contracts from before go-live were not keyed in, so
/// quantities before this day are not recorded — the screen and the paper say so.
/// </param>
public sealed record QuantityStatementDto(
    Guid CustomerId, string CustomerCode, string CustomerName,
    DateOnly From, DateOnly To, bool Current, DateOnly? FirstIssueOn,
    QuantityStatementSummaryDto Summary,
    IReadOnlyList<QuantityStatementItemDto> Items,
    IReadOnlyList<QuantityStatementContractDto> Contracts,
    IReadOnlyList<QuantityStatementWarningDto> Warnings);

/// <param name="ProvisionalValue">The part of <see cref="Value"/> for goods still out, priced through To: it moves when they come back.</param>
public sealed record QuantityStatementSummaryDto(
    int ItemTypes, decimal Issued, decimal Returned, decimal MissingDamaged, decimal WithCustomer,
    decimal Value, decimal ProvisionalValue);

/// <summary>A daily unit rate that applied: <c>rent</c>, <c>extension</c> or <c>overdue</c>.</summary>
public sealed record QuantityStatementRateDto(string Kind, decimal UnitPrice);

/// <summary>
/// One item across every contract in scope. Remaining = Opening + Issued − Returned −
/// MissingDamaged + TransferNet. Transfers ("extension as a new contract") are outside Issued and
/// Returned; across all of a customer's contracts they net to zero.
/// </summary>
public sealed record QuantityStatementItemDto(
    Guid ProductId, string Sku, string Name, string? NameAr, string? Barcode,
    decimal Opening, decimal Issued, decimal Returned, decimal MissingDamaged, decimal TransferNet, decimal Remaining,
    IReadOnlyList<QuantityStatementRateDto> Rates, bool NoRent,
    decimal Value, decimal ProvisionalValue, bool NegativeBalance,
    IReadOnlyList<QuantityStatementMovementDto> Movements);

/// <summary>
/// One row of an item's movements, oldest first. Kind is <c>opening</c>, <c>issue</c>,
/// <c>return</c>, <c>partialReturn</c>, <c>transferIn</c> or <c>transferOut</c>. Qty is the signed
/// change to what the customer holds; Balance is the running total after it.
/// </summary>
public sealed record QuantityStatementMovementDto(
    DateOnly On, DateTime? AtUtc, string Kind,
    Guid? NoteId, string? DocumentNumber, Guid? ContractId, string? ContractNumber,
    decimal Qty, decimal Returned, decimal MissingDamaged, decimal? UnitPrice, decimal Balance,
    Guid? BranchId, string? SiteName);

/// <summary>One contract in scope; Status is the contract's state as of To: <c>open</c>, <c>goodsBack</c> or <c>closed</c>.</summary>
public sealed record QuantityStatementContractDto(
    Guid ContractId, string Number, string Status, Guid BranchId,
    Guid? SiteId, string? SiteName, DateOnly? FirstIssueOn, bool PricingCorrectionPending,
    decimal Value, decimal ProvisionalValue,
    IReadOnlyList<QuantityStatementContractLineDto> Lines);

public sealed record QuantityStatementContractLineDto(
    Guid LineId, Guid ProductId, string Sku, string Name, string? NameAr, DateOnly? FirstIssueOn,
    decimal Opening, decimal Issued, decimal Returned, decimal MissingDamaged,
    decimal TransferredIn, decimal TransferredOut, decimal Remaining,
    decimal RentUnitPrice, bool NoRent, IReadOnlyList<QuantityStatementRateDto> Rates,
    decimal Value, decimal ProvisionalValue);

/// <summary>
/// Something the reader must not take as a silent zero. Code is <c>negativeBalance</c> (the held
/// quantity dipped below zero — issue dates are typed freely), <c>overReturned</c> (a return
/// claimed more than was issued) or <c>pricingCorrectionPending</c> (the monthly→daily price
/// correction has not finished on this contract, so its value may still move).
/// </summary>
public sealed record QuantityStatementWarningDto(
    string Code, Guid? ProductId, string? Sku, string? Name, string? NameAr,
    Guid? ContractId, string? ContractNumber, DateOnly? On, decimal? Qty);
