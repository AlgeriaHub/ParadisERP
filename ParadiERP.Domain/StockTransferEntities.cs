using System.ComponentModel.DataAnnotations;

namespace ParadiERP.Domain;

/// <summary>
/// Where a stock transfer is in its life (owner, 2026-10-12: "Dispatched → Received (or partly
/// received), or Cancelled before anything is received"). There is no draft: a transfer exists from
/// the moment the goods leave.
/// </summary>
public enum StockTransferStatus
{
    /// <summary>Left the source; nothing received or declared lost yet. The only state that may be cancelled.</summary>
    Dispatched = 0,

    /// <summary>
    /// Something has been settled — received at the destination or declared lost — and something is
    /// still in transit.
    /// </summary>
    PartlyReceived = 1,

    /// <summary>
    /// Closed: nothing is in transit any more. Every unit dispatched was received or declared lost;
    /// the lines say which.
    /// </summary>
    Received = 2,

    /// <summary>Called off by the source before anything was received; every unit went back to the source.</summary>
    Cancelled = 3,
}

/// <summary>One step in a transfer's life, by whom and when (<see cref="StockTransferEvent"/>).</summary>
public enum StockTransferEventKind
{
    Dispatch = 0,
    Receive = 1,
    Loss = 2,
    Cancel = 3,

    /// <summary>
    /// The destination reported part of what is in transit as short, at a receipt, with a reason
    /// (owner, 2026-10-13). Moves nothing: the units stay in transit until the source answers.
    /// </summary>
    ShortfallReported = 4,

    /// <summary>
    /// The source (or a holder of <c>inventory.transfer.write_off</c>) confirmed a reported shortfall:
    /// the units are lost, written off exactly as a <see cref="Loss"/> is.
    /// </summary>
    ShortfallConfirmed = 5,

    /// <summary>
    /// The source (or a holder of <c>inventory.transfer.write_off</c>) rejected a reported shortfall,
    /// with a reason. Moves nothing: the units are plain in transit again, and the destination can
    /// still receive them.
    /// </summary>
    ShortfallRejected = 6,
}

/// <summary>
/// Goods moving between two of the company's own locations — branch or warehouse, either way — as a
/// numbered document that is dispatched at the source and received at the destination (owner,
/// 2026-10-12). It replaced the instant transfer, which took the stock off one branch and put it on
/// the other in one call with no document, number or status. The implicit transfer a rental return
/// records at another branch is not one of these and stays as it is.
///
/// <para>
/// <b>Where in-transit stock lives.</b> Nowhere on <see cref="ProductStock"/>. Dispatch takes the
/// quantity off the source's available bucket at once; until it is received it is the open
/// remainder of this document's lines (<see cref="StockTransferLine.InTransitQty"/> on a transfer
/// that is <see cref="StockTransferStatus.Dispatched"/> or <see cref="StockTransferStatus.PartlyReceived"/>).
/// So the company holds, for a product, the sum over branches of
/// <see cref="ProductStock.OwnedQty"/> (available + on rent + maintenance + scrap) <b>plus</b> the
/// in-transit remainder of the open transfers — and each unit is in exactly one of those places: it
/// left a bucket when it was dispatched and enters one only when it is received or sent back. A
/// separate bucket would have held the same figure twice, on the row and on the document, and the
/// two could disagree; the document is the only place that knows where the goods are going.
/// </para>
/// <para>
/// <b>The ledger.</b> In-transit goods are still the source's: they stay in inventory (310) at the
/// source branch until they are received, exactly as on-rent and in-maintenance stock stays in 310 —
/// the same merchandise somewhere else, with the quantity carrying the distinction
/// (<see cref="LedgerAccounts.ForStockState"/>). Hence:
/// </para>
/// <list type="bullet">
/// <item><b>Dispatch</b> and <b>cancel</b> post nothing: no value changes branch or account.</item>
/// <item><b>Receive</b> posts what arrived, valued at <see cref="StockTransferLine.UnitCost"/>, as
/// the inter-branch transfer the instant route used to post: 310 at the destination against 310 at
/// the source, marked inter-branch so consolidation drops it (source <c>stock-transfer</c>, one entry
/// per receipt, the receipt's <see cref="StockTransferEvent.Id"/> as its source id).</item>
/// <item><b>Lost in transit</b> is written off where it was dispatched from: stock losses (657)
/// against 310 at the source, a real loss for the group and therefore <i>not</i> inter-branch
/// (source <c>stock-transfer-loss</c>, the loss event's id as source id). The source declares it
/// directly with <c>inventory.transfer.write_off</c> and a reason, or confirms a shortfall the
/// destination reported (below), which posts the same entry with the confirmation's id.</item>
/// <item><b>A reported shortfall</b> and its <b>rejection</b> post nothing: the units are still in
/// transit, still the source's.</item>
/// </list>
/// <para>
/// So at every step the source's 310 still carries what it dispatched and has not yet seen received,
/// written off or returned, and every entry balances on its own.
/// </para>
/// <para>
/// <b>Shortfalls (owner, 2026-10-13).</b> At a receipt the destination may report part of what is
/// still in transit as short, with a reason (<see cref="StockTransferEventKind.ShortfallReported"/>).
/// The quantity stays in transit — company-owned, available nowhere, counted at the source — and is
/// held on the line as <see cref="StockTransferLine.QtyShortPending"/> until it is answered. The
/// branch that dispatched it, or anyone holding <c>inventory.transfer.write_off</c> (a company-scoped
/// holder for any transfer, a branch-locked one only at their own source), then <b>confirms</b> it —
/// it becomes lost, posted as a declared loss is — or <b>rejects</b> it with a reason, and it is plain
/// in transit again for the destination to receive later. Only the destination reports; it never
/// answers its own report. A receipt of goods reported short clears that much of the report, and a
/// cancel or a direct loss can leave no more reported short than is still in transit.
/// </para>
/// <para>
/// <b>Stock movements.</b> Every step that moves stock or ownership writes one <see cref="StockMovement"/>
/// per line it touched, carrying the step's <see cref="StockTransferEvent.Id"/> as <c>DocumentId</c>
/// (the source id of the ledger entry it raised, if any), this transfer's <see cref="Number"/> as
/// <c>DocumentNumber</c>, the user, <c>MovedQty</c> and the other branch as <c>RelatedBranchId</c> —
/// reasons in <see cref="StockTransferReasons"/>. A confirmed shortfall writes the loss movement; a
/// report and a rejection move nothing and write none — the event and the line's figures record them.
/// </para>
/// <para>
/// Server-only, like every document since the ledger: not a <see cref="SyncEntity"/>, so no till
/// pulls it (back office only, owner 2026-10-12). Restrict on every foreign key: a transfer names
/// branches and products as historical facts.
/// </para>
/// </summary>
public class StockTransfer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The number on the paper, <c>TRF-000001</c>, drawn by the server at dispatch. Unique.</summary>
    [MaxLength(20)] public string Number { get; set; } = string.Empty;

    public StockTransferStatus Status { get; set; } = StockTransferStatus.Dispatched;

    /// <summary>
    /// Where the goods left. Dispatch, cancel, a declared loss and the answer to a reported shortfall
    /// are done here (a branch user at their own branch; the Admin anywhere).
    /// </summary>
    public Guid FromBranchId { get; set; }
    public Branch? FromBranch { get; set; }

    /// <summary>Where the goods go. Receive — and a shortfall reported with it — is done here only.</summary>
    public Guid ToBranchId { get; set; }
    public Branch? ToBranch { get; set; }

    /// <summary>What the dispatcher wrote: the truck, the driver, the slip travelling with the goods.</summary>
    [MaxLength(400)] public string? Note { get; set; }

    public DateTime DispatchedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? DispatchedByUserId { get; set; }
    /// <summary>The name as it stood, so a renamed or removed account still reads.</summary>
    [MaxLength(200)] public string? DispatchedByName { get; set; }

    /// <summary>When it stopped being in transit: fully received or settled, or cancelled. Null while open.</summary>
    public DateTime? ClosedAtUtc { get; set; }

    public List<StockTransferLine> Lines { get; set; } = [];

    /// <summary>
    /// Every step — dispatch, each receipt, each loss, each shortfall reported, confirmed or rejected,
    /// the cancel — in order, with who and when.
    /// </summary>
    public List<StockTransferEvent> Events { get; set; } = [];

    /// <summary>Whether anything on it is still in transit.</summary>
    public bool IsOpen => Status is StockTransferStatus.Dispatched or StockTransferStatus.PartlyReceived;

    /// <summary>Whether the destination has reported something short that is still waiting for an answer.</summary>
    public bool HasPendingShortfall => Lines.Any(l => l.QtyShortPending > 0);

    /// <summary>
    /// What the lines say the status is: closed when nothing is left in transit, partly received once
    /// anything was received or declared lost, dispatched before that. Cancelled is never derived — it
    /// is set by the cancel, which is allowed only while this still says Dispatched. A reported
    /// shortfall settles nothing, so it changes none of this: it is said beside the status.
    /// </summary>
    public static StockTransferStatus StatusFor(IReadOnlyCollection<StockTransferLine> lines) =>
        lines.All(l => l.InTransitQty <= 0) ? StockTransferStatus.Received
        : lines.Any(l => l.QtyReceived > 0 || l.QtyLost > 0) ? StockTransferStatus.PartlyReceived
        : StockTransferStatus.Dispatched;
}

/// <summary>
/// One product on a transfer: what left, what arrived, what was declared lost, what a cancel sent back,
/// and what the destination reported short and how that was answered. The figures stay on the
/// document for good; what is still in transit is the difference.
/// </summary>
public class StockTransferLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TransferId { get; set; }
    public StockTransfer? Transfer { get; set; }

    public Guid ProductId { get; set; }
    public Product? Product { get; set; }

    /// <summary>The code and names as they were at dispatch, so the note reads the same after a rename.</summary>
    [MaxLength(64)] public string Sku { get; set; } = string.Empty;
    [MaxLength(200)] public string NameAr { get; set; } = string.Empty;
    [MaxLength(200)] public string NameFr { get; set; } = string.Empty;
    [MaxLength(200)] public string NameEn { get; set; } = string.Empty;

    /// <summary>Taken off the source's available stock at dispatch. Fixed from then on.</summary>
    public decimal QtyDispatched { get; set; }

    /// <summary>Counted in at the destination, over one or several receipts.</summary>
    public decimal QtyReceived { get; set; }

    /// <summary>
    /// Lost in transit and written off at the source, over one or several steps: declared lost by the
    /// source, or reported short and confirmed (<see cref="QtyShortConfirmed"/> is that part).
    /// </summary>
    public decimal QtyLost { get; set; }

    /// <summary>
    /// The product's cost when it was dispatched: what a receipt moves between the two branches' 310
    /// and what a loss writes off. Fixed at dispatch so a cost changed while the goods travel cannot
    /// leave value behind at the source.
    /// </summary>
    public decimal UnitCost { get; set; }

    /// <summary>
    /// Sent back onto the source's available stock by the cancel — all of <see cref="QtyDispatched"/>,
    /// since a transfer is cancelled only while nothing on it was received or lost. Stored rather than
    /// inferred from the header's status so a line read on its own still says what it holds.
    /// </summary>
    public decimal QtyReturned { get; set; }

    /// <summary>
    /// Reported short by the destination and still waiting for the source's answer (owner, 2026-10-13).
    /// A part of <see cref="InTransitQty"/>, never beside it: the units are still the company's,
    /// available nowhere and counted in transit at the source until the shortfall is confirmed (they
    /// become <see cref="QtyLost"/>), rejected (plain in transit again) or the goods are received after
    /// all. Never more than what is in transit.
    /// </summary>
    public decimal QtyShortPending { get; set; }

    /// <summary>Everything the destination reported short on this line, over one or several receipts. Only grows.</summary>
    public decimal QtyShortReported { get; set; }

    /// <summary>Of what was reported short, what the source confirmed and wrote off — also in <see cref="QtyLost"/>. Only grows.</summary>
    public decimal QtyShortConfirmed { get; set; }

    /// <summary>Of what was reported short, what the source rejected — it went back to plain in transit. Only grows.</summary>
    public decimal QtyShortRejected { get; set; }

    /// <summary>
    /// Left the source and not yet received, declared lost or sent back — including what is reported
    /// short and not yet answered (<see cref="QtyShortPending"/>).
    /// </summary>
    public decimal InTransitQty => QtyDispatched - QtyReceived - QtyLost - QtyReturned;
}

/// <summary>
/// One step on a transfer, with who did it and when. A receipt, a loss or a shortfall can happen
/// several times on one transfer, so these cannot be columns on the header. Its id is the
/// <c>DocumentId</c> of the stock movements the step wrote and the source id of the ledger entry it
/// raised, which is how a figure in an accounting report leads back to the step and from there to the
/// transfer. A receipt that also reports a shortfall is two steps: the receipt, then the report.
/// </summary>
public class StockTransferEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TransferId { get; set; }
    public StockTransfer? Transfer { get; set; }

    public StockTransferEventKind Kind { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UserId { get; set; }
    /// <summary>The name as it stood.</summary>
    [MaxLength(200)] public string? UserName { get; set; }

    /// <summary>
    /// Why — required on a loss, a reported shortfall and a rejection; optional on a cancel, a receipt
    /// (the receiver's remark) and a confirmation (the confirmer's remark).
    /// </summary>
    [MaxLength(400)] public string? Reason { get; set; }
}

/// <summary>
/// The <see cref="StockMovement.Reason"/> prefixes a transfer writes (the operator's words may follow
/// after ": ", cut to the column by <see cref="StockMovement.Label"/>).
/// </summary>
public static class StockTransferReasons
{
    /// <summary>At the source, at dispatch: <c>QtyDelta</c> −qty off available.</summary>
    public const string Dispatch = "transfer-out";

    /// <summary>At the destination, per receipt: <c>QtyDelta</c> +qty onto available.</summary>
    public const string Receive = "transfer-in";

    /// <summary>At the source, on a cancel: <c>QtyDelta</c> +qty back onto available.</summary>
    public const string Cancel = "transfer-cancel";

    /// <summary>
    /// At the source, on a loss — declared, or a reported shortfall confirmed: <c>QtyDelta</c> 0 and
    /// <c>MovedQty</c> the quantity — the units left the source's buckets at dispatch, so no bucket
    /// changes now; what leaves is the company's ownership, which the ledger records as the write-off.
    /// </summary>
    public const string Loss = "transfer-lost";
}
