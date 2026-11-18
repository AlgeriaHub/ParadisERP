namespace ParadiERP.Application.Contracts;

/// <summary>
/// One article's line in the report. Total = Warehouses + Branches + Rented + Maintenance + Scrap, by construction
/// (all from ProductStock over the locations the caller sees in full). Available = Warehouses + Branches.
/// </summary>
public sealed record ItemStatusRowDto(
    Guid ProductId, string Sku, string? Barcode, string NameAr, string NameFr, string NameEn, string Unit,
    bool IsRentable, bool IsActive,
    decimal Total, decimal Warehouses, decimal Branches, decimal Available,
    decimal Rented, decimal Maintenance, decimal Scrap,
    decimal StillOut,            // what the contracts say is still out (FIFO), issued from the locations seen in full
    decimal UnexplainedOnRent,   // Rented - StillOut; 0 when they agree
    decimal LeftToIssue,         // contracted on open contracts, not yet issued — information only, never subtracted
    decimal? OtherAvailable,     // branch user with stock.view_other_branches: available at the other locations; else null
    decimal InTransit);          // dispatched from the locations seen in full and not yet received (already in Total)

public sealed record ItemStatusTotalsDto(
    decimal Total, decimal Warehouses, decimal Branches, decimal Available, decimal Rented,
    decimal Maintenance, decimal Scrap, decimal StillOut, decimal UnexplainedOnRent, decimal LeftToIssue,
    decimal? OtherAvailable, decimal InTransit);

/// <summary>
/// Scope says in words what the figures cover: "company" (every location), "location" (the Admin filtered one
/// location), "own" (a branch-locked user: their branch in full).
/// </summary>
public sealed record ItemStatusScopeDto(string Kind, Guid? LocationId, bool OthersAvailableShown);

public sealed record ItemStatusReportDto(
    DateTime AsOfUtc, ItemStatusScopeDto Scope,
    IReadOnlyList<ItemStatusRowDto> Rows, ItemStatusTotalsDto Totals);

/// <summary>
/// A location's figures for one article. For another branch shown to a branch user only Available is filled.
/// </summary>
public sealed record ItemLocationDto(
    Guid BranchId, string Code, string NameAr, string NameFr, string NameEn,
    bool IsWarehouse, bool IsMain, bool IsActive, bool AvailableOnly,
    decimal Available, decimal? OnRent, decimal? Maintenance, decimal? Scrap, decimal? Total,
    decimal? StillOut, decimal? UnexplainedOnRent,
    decimal? InTransit);         // dispatched from this location and not yet received (in Total); null when available only

/// <summary>
/// One open transfer line of the article touching a location seen in full, at either end (owner,
/// 2026-10-12). <paramref name="CountedHere"/>: its source is seen in full, so it is in this scope's
/// Total — in-transit goods count at the location that dispatched them until they are received; an
/// incoming one is shown for information only.
/// </summary>
public sealed record ItemInTransitDto(
    Guid TransferId, string Number, string Status,
    Guid FromBranchId, string FromNameAr, string FromNameFr, string FromNameEn,
    Guid ToBranchId, string ToNameAr, string ToNameFr, string ToNameEn,
    DateTime DispatchedAtUtc, decimal Qty, bool CountedHere);

public sealed record ItemRentedDto(
    Guid ContractId, string ContractNumber, string ContractStatus,
    Guid CustomerId, string CustomerCode, string CustomerName,
    Guid IssueBranchId, string IssueBranchNameAr, string IssueBranchNameFr, string IssueBranchNameEn,
    string? SiteName, Guid IssueNoteId, string IssueNoteNumber,
    DateOnly IssuedOn, DateOnly ExpectedReturnOn, bool Overdue, decimal Qty);

public sealed record ItemLeftToIssueDto(Guid ContractId, string ContractNumber, Guid CustomerId, string CustomerName, decimal Qty);

public sealed record ItemMovementDto(
    DateTime AtUtc, string Type,          // sale | refund | receiving | opening | transfer | transfer-out | transfer-in | transfer-cancel | transfer-lost | rental-issue | rental-return | rental-return-transfer | state | adjustment
    string? FromState, string? ToState,   // for "state": the two lowercase state words from the reason, e.g. available / maintenance
    string? Detail,                       // the text after "prefix: " in the reason (supplier reference, note); for adjustment the whole reason
    string? DocumentNumber, Guid? DocumentId, Guid? SaleId, Guid? ContractId,
    Guid? FromBranchId, Guid? ToBranchId, // a location id when that side is a location
    string? CustomerName, string? SupplierName,
    decimal? Moved, decimal In, decimal Out, decimal Balance, string? UserName);

public sealed record ItemMovementsDto(
    DateOnly? From, DateOnly? To, decimal Opening, decimal Closing,
    decimal? UnrecordedDifference,   // when To is open/today: OwnedNow (Item.Total) - Closing, if not 0; else null
    bool Truncated, IReadOnlyList<ItemMovementDto> Rows);

public sealed record ItemStatusDetailDto(
    DateTime AsOfUtc, ItemStatusScopeDto Scope, ItemStatusRowDto Item,
    IReadOnlyList<ItemLocationDto> Locations, IReadOnlyList<ItemRentedDto> Rented,
    IReadOnlyList<ItemLeftToIssueDto> LeftToIssue, ItemMovementsDto Movements,
    IReadOnlyList<ItemInTransitDto> InTransit);
