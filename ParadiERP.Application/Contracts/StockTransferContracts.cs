namespace ParadiERP.Application.Contracts;

public record StockTransferLineRequest(Guid ProductId, decimal Qty);
public partial record DispatchStockTransferRequest(Guid FromBranchId, Guid ToBranchId, string? Note, IReadOnlyList<StockTransferLineRequest> Lines);
public record TransferLineQty(Guid LineId, decimal Qty);

/// <summary>
/// One line of a receipt: what arrived, and what the destination reports short of what is left
/// (owner, 2026-10-13) — the difference, or part of it. <paramref name="Short"/> stays in transit until
/// the source confirms or rejects it.
/// </summary>
public record ReceiveLineQty(Guid LineId, decimal Qty, decimal Short = 0);

/// <summary>A receipt; <paramref name="ShortReason"/> is required when any line reports something short.</summary>
public record ReceiveStockTransferRequest(IReadOnlyList<ReceiveLineQty> Lines, string? Note, string? ShortReason = null);
public record DeclareTransferLossRequest(IReadOnlyList<TransferLineQty> Lines, string Reason);
public record CancelStockTransferRequest(string? Reason);

/// <summary>
/// The source's answer to a reported shortfall: the lines and quantities answered — what the screen
/// showed as reported short — and why (required to reject, optional to confirm).
/// </summary>
public record TransferShortfallDecisionRequest(IReadOnlyList<TransferLineQty> Lines, string? Reason);

/// <param name="QtyShortPending">Reported short and still waiting for an answer; part of <paramref name="InTransit"/>.</param>
/// <param name="QtyShortReported">Everything reported short on the line, over every receipt.</param>
/// <param name="QtyShortConfirmed">Of that, confirmed and written off (also in <paramref name="QtyLost"/>).</param>
/// <param name="QtyShortRejected">Of that, rejected by the source.</param>
public record StockTransferLineDto(Guid Id, Guid ProductId, string Sku, string NameAr, string NameFr, string NameEn,
    decimal QtyDispatched, decimal QtyReceived, decimal QtyLost, decimal QtyReturned, decimal InTransit, decimal UnitCost,
    decimal QtyShortPending, decimal QtyShortReported, decimal QtyShortConfirmed, decimal QtyShortRejected);
public record StockTransferEventDto(Guid Id, string Kind, DateTime AtUtc, string? UserName, string? Reason);

/// <param name="QtyShortPending">What the destination reported short and is still waiting for an answer, over every line.</param>
/// <param name="CanDecideShortfall">
/// Whether the caller may confirm or reject that shortfall — the server's own rule
/// (<c>StockTransfers.MayDecideShortfall</c>), so the screen's buttons and the routes never disagree.
/// </param>
public record StockTransferDto(Guid Id, string Number, string Status, Guid FromBranchId, string FromBranchName,
    Guid ToBranchId, string ToBranchName, string? Note, DateTime DispatchedAtUtc, string? DispatchedByName,
    DateTime? ClosedAtUtc, decimal QtyDispatched, decimal InTransit,
    IReadOnlyList<StockTransferLineDto> Lines, IReadOnlyList<StockTransferEventDto> Events,
    decimal QtyShortPending, bool CanDecideShortfall);
