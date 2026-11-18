using ParadiERP.Application.Contracts;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.FixedAssets;
using ParadiERP.Infrastructure.Ledger;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Valuation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ParadiERP.Infrastructure.Catalog;

/// <summary>
/// Who acts on a transfer. <paramref name="OwnBranchId"/> is null for a company-scoped caller (the
/// Admin, who may act at either end) and the caller's branch otherwise. <paramref name="MayTransfer"/>
/// and <paramref name="MayWriteOff"/> are the caller's <c>inventory.transfer</c> and
/// <c>inventory.transfer.write_off</c> as the server resolved them; only the answer to a reported
/// shortfall reads them (<see cref="StockTransfers.MayDecideShortfall"/>) — every other step leaves its
/// permission to the route.
/// </summary>
public sealed record TransferActor(Guid? UserId, string? UserName, Guid? OwnBranchId, bool MayTransfer, bool MayWriteOff);

/// <summary>What a transfer step answered: the transfer, or an HTTP status and the sentence why not.</summary>
public sealed record TransferResult(int Status, string? Error, StockTransfer? Transfer)
{
    public bool Ok => Error is null;
    public static TransferResult Success(StockTransfer t) => new(200, null, t);
    public static TransferResult BadRequest(string e) => new(400, e, null);
    public static TransferResult Forbidden(string e) => new(403, e, null);
    public static TransferResult NotFound(string e) => new(404, e, null);
    public static TransferResult Conflict(string e) => new(409, e, null);
}

/// <summary>
/// The transfer workflow (owner, 2026-10-12): dispatch at the source, receive at the destination —
/// in full or in parts — cancel by the source while nothing has been received, and goods lost in
/// transit declared by a manager with a reason. Since 2026-10-13 the destination may also report part
/// of what is in transit as short at a receipt, and the source (or a write-off holder) confirms it as
/// lost or rejects it. <see cref="StockTransfer"/> carries the design: where in-transit stock lives,
/// what each step posts, which movements it writes.
///
/// <para>
/// Every write takes the version lock before it reads the transfer or a stock figure, so two receipts
/// of one transfer, a receipt and the answer to a shortfall, or two dispatches out of one branch,
/// cannot both pass a check against the same count (the reasoning of <see cref="StockRows"/>).
/// </para>
/// <para>
/// <b>Valued at</b> the line's cost, fixed at dispatch: the product's cost price — or, once sales are costed at the
/// weighted average (C-01), what the goods left at. Then each step is valued as the shadow values it
/// (<see cref="LiveSaleCosting.MovementCostsAsync"/>): with one average per location, the receipt, a cancel and a loss take
/// the goods out of the transfer at what they left the source at (the receiving location takes the sending location's
/// average); with one average for the company, a loss leaves at the company's average of the moment and a receipt moves
/// the goods between the two branches' 310 at it (inter-branch: the company's stock value does not change).
/// </para>
/// </summary>
public sealed class StockTransfers(PosDbContext db, LedgerPoster ledger, ILogger<StockTransfers>? logger = null)
{
    public const int MaxLines = 200;
    public const int MaxText = 400;
    private const int ListLimit = 500;

    /// <summary>The list filter for transfers with a shortfall reported and not yet answered.</summary>
    public const string ShortfallFilter = "shortfall";

    // ---------- dispatch ----------

    public async Task<TransferResult> DispatchAsync(DispatchStockTransferRequest r, TransferActor actor, CancellationToken ct = default)
    {
        if (r.FromBranchId == r.ToBranchId) return TransferResult.BadRequest("Source and destination branch must differ.");
        if (r.Lines is null || r.Lines.Count == 0) return TransferResult.BadRequest("A transfer needs at least one line.");
        if (r.Lines.Count > MaxLines) return TransferResult.BadRequest($"A transfer carries at most {MaxLines} lines.");
        if (r.Lines.Any(l => l.Qty <= 0)) return TransferResult.BadRequest("Every quantity must be positive.");
        var note = Clean(r.Note);
        if (note?.Length > MaxText) return TransferResult.BadRequest($"The note is limited to {MaxText} characters.");

        var from = await db.Branches.FirstOrDefaultAsync(b => b.Id == r.FromBranchId && !b.IsDeleted, ct);
        var to = await db.Branches.FirstOrDefaultAsync(b => b.Id == r.ToBranchId && !b.IsDeleted, ct);
        if (from is null || to is null) return TransferResult.NotFound("Branch not found.");

        // The same product twice on one form is one line of the summed quantity.
        var wanted = r.Lines.GroupBy(l => l.ProductId).Select(g => (ProductId: g.Key, Qty: g.Sum(l => l.Qty))).ToList();
        var ids = wanted.Select(w => w.ProductId).ToList();
        var products = await db.Products.Where(p => ids.Contains(p.Id) && !p.IsDeleted).ToDictionaryAsync(p => p.Id, ct);
        foreach (var (productId, _) in wanted)
        {
            if (!products.TryGetValue(productId, out var product)) return TransferResult.NotFound("Product not found.");
            if (!product.TrackStock) return TransferResult.BadRequest($"{product.Sku} does not track stock and cannot be transferred.");
        }

        await using var tx = await db.BeginWriteScopeAsync(ct);
        await db.LockVersionsAsync(ct);
        var rows = new StockRows(db);

        // Every shortfall is found before anything changes, under the lock: two dispatches out of one
        // branch cannot both pass against the same count.
        // Material a production line holds reserved for its live orders is not a transfer's to take
        // (manufacturing Phase 2, Revision 2 §7): checked here, under the same lock as the order that
        // reserved it. Only a line's location has any; a production manager releases a reservation first.
        var reserved = await Manufacturing.Production.ReservedAtAsync(db, from.Id, ids, null, ct);
        var held = new List<(Product Product, decimal Qty, ProductStock? Stock)>();
        foreach (var (productId, qty) in wanted)
        {
            var product = products[productId];
            var stock = await rows.FindAsync(productId, from.Id, ct);
            var available = stock?.StockQty ?? 0m;
            if (!from.AllowNegativeStock && available - qty < 0)
                return TransferResult.Conflict($"Insufficient stock of {product.Sku} at '{from.NameFr}' ({available:0.##} available).");
            if (reserved.GetValueOrDefault(productId) is > 0 and var claim && available - qty < claim)
                return TransferResult.Conflict(
                    $"{product.Sku} at '{from.NameFr}': {claim:0.##} is reserved for production orders; {Math.Max(0m, available - claim):0.##} can be transferred.");
            held.Add((product, qty, stock));
        }

        var transfer = new StockTransfer
        {
            // Inside the write scope, like every other number: the lock above serialises the count.
            Number = $"TRF-{await db.StockTransfers.CountAsync(ct) + 1:D6}",
            FromBranchId = from.Id, ToBranchId = to.Id, Note = note, Status = StockTransferStatus.Dispatched,
            DispatchedAtUtc = DateTime.UtcNow, DispatchedByUserId = actor.UserId, DispatchedByName = actor.UserName,
        };
        var step = Step(transfer, StockTransferEventKind.Dispatch, actor, note);

        var planned = new List<(StockTransferLine Line, PlannedMovement Plan)>();
        foreach (var (product, qty, found) in held)
        {
            var stock = found ?? await rows.GetAsync(product.Id, from.Id, ct);
            stock.StockQty -= qty;
            var line = new StockTransferLine
            {
                ProductId = product.Id, Sku = product.Sku, NameAr = product.NameAr, NameFr = product.NameFr, NameEn = product.NameEn,
                QtyDispatched = qty, UnitCost = product.CostPrice,
            };
            transfer.Lines.Add(line);
            var movement = Movement(transfer, step, product.Id, from.Id, to.Id, -qty, qty, stock.StockQty, StockTransferReasons.Dispatch, note, product.CostPrice);
            planned.Add((line, new PlannedMovement(movement, product.CostPrice, transfer.Id)));
            await db.TouchAsync(product, ct);
        }
        // Under the weighted average (C-01) the line shows what the goods left at, to the 3 decimals the column keeps.
        // The steps after it are valued by the replay (the movement keeps the exact figure); the line's cost is what
        // they fall back to, and what they post if sales are switched back to the cost price before the goods arrive.
        if (await CostAsync(planned.Select(p => p.Plan), ct) is { } costs)
            foreach (var (line, plan) in planned)
                line.UnitCost = Math.Round(costs[plan.Movement.Id].UnitCost, 3, MidpointRounding.AwayFromZero);
        db.StockTransfers.Add(transfer);

        // Nothing posts: in-transit goods stay in the source's inventory until they are received.
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return TransferResult.Success(transfer);
    }

    // ---------- receive, and a shortfall reported with it ----------

    /// <summary>
    /// What arrived lands on the destination's available stock and posts the inter-branch move; what the
    /// destination reports short (owner, 2026-10-13) stays in transit, counted at the source, waiting
    /// for the source's answer. One call may do either or both: the receipt step, then the report step.
    /// </summary>
    public async Task<TransferResult> ReceiveAsync(Guid id, ReceiveStockTransferRequest r, TransferActor actor, CancellationToken ct = default)
    {
        var note = Clean(r.Note);
        if (note?.Length > MaxText) return TransferResult.BadRequest($"The note is limited to {MaxText} characters.");
        var shortReason = Clean(r.ShortReason);
        if (shortReason?.Length > MaxText) return TransferResult.BadRequest($"The reason is limited to {MaxText} characters.");
        if (shortReason is null && r.Lines?.Any(l => l.Short > 0) == true)
            return TransferResult.BadRequest("Say why the goods are reported short.");

        await using var tx = await db.BeginWriteScopeAsync(ct);
        await db.LockVersionsAsync(ct);
        var transfer = await LoadAsync(id, ct);
        if (transfer is null || !IsParty(transfer, actor)) return TransferResult.NotFound("Transfer not found.");
        if (actor.OwnBranchId is Guid own && own != transfer.ToBranchId)
            return TransferResult.Forbidden("A transfer is received at its destination only.");
        if (!transfer.IsOpen) return NotOpen(transfer);
        if (ValidateReceipt(transfer, r.Lines) is { } problem) return problem;

        var arrived = r.Lines.Where(l => l.Qty > 0).Select(l => (Line: transfer.Lines.First(x => x.Id == l.LineId), l.Qty)).ToList();
        if (arrived.Count > 0)
        {
            var step = Step(transfer, StockTransferEventKind.Receive, actor, note);
            var rows = new StockRows(db);
            var planned = new List<(StockTransferLine Line, decimal Qty, PlannedMovement Plan)>();
            foreach (var (line, qty) in arrived)
            {
                line.QtyReceived += qty;
                // Goods that had been reported short and turn up after all clear that much of the report.
                ClampShortfall(line);
                var stock = await rows.GetAsync(line.ProductId, transfer.ToBranchId, ct);
                stock.StockQty += qty;
                var movement = Movement(transfer, step, line.ProductId, transfer.ToBranchId, transfer.FromBranchId, qty, qty, stock.StockQty,
                    StockTransferReasons.Receive, note, line.UnitCost);
                planned.Add((line, qty, new PlannedMovement(movement, line.UnitCost, transfer.Id)));
                await TouchAsync(line.ProductId, ct);
            }
            var valued = await RegisterAsync(planned, await ValuedAsync(planned, ct), transfer, step, null, ct);

            var names = await NamesAsync([transfer.FromBranchId, transfer.ToBranchId], ct);
            await ledger.PostAsync(StockPosting.TransferReceipt(step.Id, transfer.FromBranchId, transfer.ToBranchId,
                names.GetValueOrDefault(transfer.FromBranchId, ""), names.GetValueOrDefault(transfer.ToBranchId, ""),
                transfer.Number, valued), ct);
        }

        var reported = r.Lines.Where(l => l.Short > 0).Select(l => (Line: transfer.Lines.First(x => x.Id == l.LineId), l.Short)).ToList();
        if (reported.Count > 0)
        {
            // Nothing moves and nothing posts: the units stay in transit, the source's, until it answers.
            Step(transfer, StockTransferEventKind.ShortfallReported, actor, shortReason);
            foreach (var (line, qty) in reported)
            {
                line.QtyShortPending += qty;
                line.QtyShortReported += qty;
            }
        }
        Settle(transfer);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return TransferResult.Success(transfer);
    }

    // ---------- lost in transit ----------

    public async Task<TransferResult> DeclareLostAsync(Guid id, DeclareTransferLossRequest r, TransferActor actor, CancellationToken ct = default)
    {
        var reason = Clean(r.Reason);
        if (reason is null) return TransferResult.BadRequest("Say why the goods are declared lost.");
        if (reason.Length > MaxText) return TransferResult.BadRequest($"The reason is limited to {MaxText} characters.");

        await using var tx = await db.BeginWriteScopeAsync(ct);
        await db.LockVersionsAsync(ct);
        var transfer = await LoadAsync(id, ct);
        if (transfer is null || !IsParty(transfer, actor)) return TransferResult.NotFound("Transfer not found.");
        if (actor.OwnBranchId is Guid own && own != transfer.FromBranchId)
            return TransferResult.Forbidden("Goods lost in transit are written off by the branch that dispatched them.");
        if (!transfer.IsOpen) return NotOpen(transfer);
        if (Validate(transfer, r.Lines) is { } problem) return problem;

        var step = Step(transfer, StockTransferEventKind.Loss, actor, reason);
        var rows = new StockRows(db);
        var planned = new List<(StockTransferLine Line, decimal Qty, PlannedMovement Plan)>();
        foreach (var (line, qty) in Pairs(transfer, r.Lines))
        {
            line.QtyLost += qty;
            // No bucket moves: the units left the source's buckets at dispatch. What leaves now is the
            // company's ownership, which the ledger records as the write-off.
            var balance = (await rows.FindAsync(line.ProductId, transfer.FromBranchId, ct))?.StockQty ?? 0m;
            var movement = Movement(transfer, step, line.ProductId, transfer.FromBranchId, transfer.ToBranchId, 0m, qty, balance,
                StockTransferReasons.Loss, reason, line.UnitCost);
            planned.Add((line, qty, new PlannedMovement(movement, line.UnitCost, transfer.Id)));
        }
        var valued = await RegisterAsync(planned, await ValuedAsync(planned, ct), transfer, step, reason, ct);
        Settle(transfer);

        await ledger.PostAsync(StockPosting.TransferLoss(step.Id, transfer.FromBranchId, transfer.Number, valued, reason), ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return TransferResult.Success(transfer);
    }

    // ---------- a reported shortfall, answered ----------

    /// <summary>
    /// The source confirms what the destination reported short: it is lost, written off at the source
    /// exactly as a declared loss is — 657 against 310, the loss movement — under this step's id.
    /// </summary>
    public async Task<TransferResult> ConfirmShortfallAsync(Guid id, TransferShortfallDecisionRequest r, TransferActor actor, CancellationToken ct = default)
    {
        var remark = Clean(r.Reason);
        if (remark?.Length > MaxText) return TransferResult.BadRequest($"The reason is limited to {MaxText} characters.");

        await using var tx = await db.BeginWriteScopeAsync(ct);
        await db.LockVersionsAsync(ct);
        var transfer = await LoadAsync(id, ct);
        if (transfer is null || !IsParty(transfer, actor)) return TransferResult.NotFound("Transfer not found.");
        if (DecisionRefusal(transfer, actor) is { } refused) return refused;
        if (ValidateShortfall(transfer, r.Lines) is { } problem) return problem;

        // The movement and the entry say why: the confirmer's words, or else the destination's.
        var why = remark ?? LastReportReason(transfer) ?? "shortfall reported at receipt";
        var step = Step(transfer, StockTransferEventKind.ShortfallConfirmed, actor, remark);
        var rows = new StockRows(db);
        var planned = new List<(StockTransferLine Line, decimal Qty, PlannedMovement Plan)>();
        foreach (var (line, qty) in Pairs(transfer, r.Lines))
        {
            line.QtyShortPending -= qty;
            line.QtyShortConfirmed += qty;
            line.QtyLost += qty;
            // As a declared loss: no bucket moves, the company's ownership leaves.
            var balance = (await rows.FindAsync(line.ProductId, transfer.FromBranchId, ct))?.StockQty ?? 0m;
            var movement = Movement(transfer, step, line.ProductId, transfer.FromBranchId, transfer.ToBranchId, 0m, qty, balance,
                StockTransferReasons.Loss, why, line.UnitCost);
            planned.Add((line, qty, new PlannedMovement(movement, line.UnitCost, transfer.Id)));
        }
        var valued = await RegisterAsync(planned, await ValuedAsync(planned, ct), transfer, step, why, ct);
        Settle(transfer);

        await ledger.PostAsync(StockPosting.TransferLoss(step.Id, transfer.FromBranchId, transfer.Number, valued, why), ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return TransferResult.Success(transfer);
    }

    /// <summary>
    /// The source rejects what the destination reported short, with a reason: it is plain in transit
    /// again — still the source's, still counted there — and the destination can receive it later.
    /// Moves nothing and posts nothing.
    /// </summary>
    public async Task<TransferResult> RejectShortfallAsync(Guid id, TransferShortfallDecisionRequest r, TransferActor actor, CancellationToken ct = default)
    {
        var reason = Clean(r.Reason);
        if (reason is null) return TransferResult.BadRequest("Say why the reported shortfall is rejected.");
        if (reason.Length > MaxText) return TransferResult.BadRequest($"The reason is limited to {MaxText} characters.");

        await using var tx = await db.BeginWriteScopeAsync(ct);
        await db.LockVersionsAsync(ct);
        var transfer = await LoadAsync(id, ct);
        if (transfer is null || !IsParty(transfer, actor)) return TransferResult.NotFound("Transfer not found.");
        if (DecisionRefusal(transfer, actor) is { } refused) return refused;
        if (ValidateShortfall(transfer, r.Lines) is { } problem) return problem;

        Step(transfer, StockTransferEventKind.ShortfallRejected, actor, reason);
        foreach (var (line, qty) in Pairs(transfer, r.Lines))
        {
            line.QtyShortPending -= qty;
            line.QtyShortRejected += qty;
        }
        Settle(transfer);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return TransferResult.Success(transfer);
    }

    // ---------- cancel ----------

    /// <summary>
    /// The source calls the transfer off while nothing was received or lost: everything in transit goes
    /// back onto its available stock — including what the destination reported short, which the cancel
    /// answers (the goods never arrived because they are back at the source).
    /// </summary>
    public async Task<TransferResult> CancelAsync(Guid id, CancelStockTransferRequest r, TransferActor actor, CancellationToken ct = default)
    {
        var reason = Clean(r.Reason);
        if (reason?.Length > MaxText) return TransferResult.BadRequest($"The reason is limited to {MaxText} characters.");

        await using var tx = await db.BeginWriteScopeAsync(ct);
        await db.LockVersionsAsync(ct);
        var transfer = await LoadAsync(id, ct);
        if (transfer is null || !IsParty(transfer, actor)) return TransferResult.NotFound("Transfer not found.");
        if (actor.OwnBranchId is Guid own && own != transfer.FromBranchId)
            return TransferResult.Forbidden("A transfer is cancelled by the branch that dispatched it.");
        if (transfer.Status != StockTransferStatus.Dispatched)
            return transfer.Status == StockTransferStatus.Cancelled
                ? TransferResult.Conflict($"{transfer.Number} is already cancelled.")
                : TransferResult.Conflict($"{transfer.Number} has goods received or declared lost; it can no longer be cancelled.");

        var step = Step(transfer, StockTransferEventKind.Cancel, actor, reason);
        var rows = new StockRows(db);
        var planned = new List<(StockTransferLine Line, decimal Qty, PlannedMovement Plan)>();
        foreach (var line in transfer.Lines)
        {
            var qty = line.InTransitQty;
            if (qty <= 0) continue;
            line.QtyReturned += qty;
            var stock = await rows.GetAsync(line.ProductId, transfer.FromBranchId, ct);
            stock.StockQty += qty;
            var movement = Movement(transfer, step, line.ProductId, transfer.FromBranchId, transfer.ToBranchId, qty, qty, stock.StockQty,
                StockTransferReasons.Cancel, reason, line.UnitCost);
            planned.Add((line, qty, new PlannedMovement(movement, line.UnitCost, transfer.Id)));
            await TouchAsync(line.ProductId, ct);
        }
        // Back at what they left at: only the movements say it (nothing posts).
        await ValuedAsync(planned, ct);
        foreach (var line in transfer.Lines) ClampShortfall(line);
        transfer.Status = StockTransferStatus.Cancelled;
        transfer.ClosedAtUtc = DateTime.UtcNow;

        // Nothing posts: nothing was posted at dispatch, and the goods never left the source's books.
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return TransferResult.Success(transfer);
    }

    // ---------- reads ----------

    /// <summary>
    /// The transfers touching a branch (either end), newest first. <paramref name="status"/> is a
    /// status name, <c>open</c> (dispatched or partly received) or <see cref="ShortfallFilter"/> (a
    /// shortfall reported and not yet answered — always on an open transfer).
    /// </summary>
    public async Task<List<StockTransfer>> ListAsync(
        Guid? branchId, string? status, Guid? productId, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var shortfall = string.Equals(status, ShortfallFilter, StringComparison.OrdinalIgnoreCase);
        var query = db.StockTransfers.AsNoTracking().Include(t => t.Lines).AsQueryable();
        if (branchId is Guid b) query = query.Where(t => t.FromBranchId == b || t.ToBranchId == b);
        if (shortfall || string.Equals(status, "open", StringComparison.OrdinalIgnoreCase))
            query = query.Where(t => t.Status == StockTransferStatus.Dispatched || t.Status == StockTransferStatus.PartlyReceived);
        else if (Enum.TryParse<StockTransferStatus>(status, true, out var s))
            query = query.Where(t => t.Status == s);
        if (productId is Guid p) query = query.Where(t => t.Lines.Any(l => l.ProductId == p));
        if (from is DateTime f) query = query.Where(t => t.DispatchedAtUtc >= f);
        if (to is DateTime u) query = query.Where(t => t.DispatchedAtUtc < u);
        var ordered = query.OrderByDescending(t => t.DispatchedAtUtc);
        if (!shortfall) return await ordered.Take(ListLimit).ToListAsync(ct);
        // The reported quantity is read in memory (decimals are text on SQLite), over the open transfers only.
        return (await ordered.ToListAsync(ct)).Where(t => t.HasPendingShortfall).Take(ListLimit).ToList();
    }

    public Task<StockTransfer?> GetAsync(Guid id, CancellationToken ct = default) =>
        db.StockTransfers.AsNoTracking().Include(t => t.Lines).Include(t => t.Events)
            .FirstOrDefaultAsync(t => t.Id == id, ct);

    /// <summary>
    /// What is in transit now: the lines of open transfers with something left, each with its transfer
    /// — for the item movement report. Filtered on the remainder in memory (decimals are text on SQLite).
    /// What the destination reported short and the source has not answered is still in transit here.
    /// </summary>
    public async Task<List<StockTransferLine>> InTransitAsync(Guid? productId, CancellationToken ct = default)
    {
        var query = db.StockTransferLines.AsNoTracking().Include(l => l.Transfer)
            .Where(l => l.Transfer!.Status == StockTransferStatus.Dispatched || l.Transfer!.Status == StockTransferStatus.PartlyReceived);
        if (productId is Guid p) query = query.Where(l => l.ProductId == p);
        return (await query.ToListAsync(ct)).Where(l => l.InTransitQty > 0).ToList();
    }

    /// <summary>The names the screens show, by branch (French, as the other stock routes do).</summary>
    public Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid> branchIds, CancellationToken ct = default)
    {
        var ids = branchIds.Distinct().ToList();
        return db.Branches.AsNoTracking().Where(b => ids.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.NameFr, ct);
    }

    /// <summary>
    /// The transfer as the screens read it. <paramref name="viewer"/> is who asked, for
    /// <see cref="StockTransferDto.CanDecideShortfall"/>; without one that answer is no.
    /// </summary>
    public static StockTransferDto ToDto(StockTransfer t, IReadOnlyDictionary<Guid, string> names, TransferActor? viewer = null) => new(
        t.Id, t.Number, t.Status.ToString(), t.FromBranchId, names.GetValueOrDefault(t.FromBranchId, ""),
        t.ToBranchId, names.GetValueOrDefault(t.ToBranchId, ""), t.Note, t.DispatchedAtUtc, t.DispatchedByName,
        t.ClosedAtUtc, t.Lines.Sum(l => l.QtyDispatched), t.Lines.Sum(l => l.InTransitQty),
        t.Lines.OrderBy(l => l.Sku).Select(l => new StockTransferLineDto(
            l.Id, l.ProductId, l.Sku, l.NameAr, l.NameFr, l.NameEn,
            l.QtyDispatched, l.QtyReceived, l.QtyLost, l.QtyReturned, l.InTransitQty, l.UnitCost,
            l.QtyShortPending, l.QtyShortReported, l.QtyShortConfirmed, l.QtyShortRejected)).ToList(),
        t.Events.OrderBy(e => e.AtUtc).Select(e => new StockTransferEventDto(e.Id, e.Kind.ToString(), e.AtUtc, e.UserName, e.Reason)).ToList(),
        t.Lines.Sum(l => l.QtyShortPending),
        viewer is not null && t.HasPendingShortfall && MayDecideShortfall(t, viewer));

    // ---------- the pieces ----------

    /// <summary>A branch user may see a transfer only from one of its two ends.</summary>
    public static bool IsParty(StockTransfer t, TransferActor actor) =>
        actor.OwnBranchId is not Guid own || own == t.FromBranchId || own == t.ToBranchId;

    /// <summary>
    /// Whether this caller may confirm or reject a shortfall reported on this transfer: the rule the two
    /// routes enforce (<see cref="DecisionRefusal"/>), read by the screen's buttons.
    /// </summary>
    public static bool MayDecideShortfall(StockTransfer t, TransferActor actor) =>
        IsParty(t, actor) && DecisionRefusal(t, actor) is null;

    /// <summary>
    /// Who answers a reported shortfall (owner, 2026-10-13): the branch that dispatched the goods, or a
    /// holder of <c>inventory.transfer.write_off</c> — on any transfer when company-scoped, only on their
    /// own branch's when branch-locked, which the first half already allows. Never the destination that
    /// reported it. Null when the caller may answer, the refusal in words otherwise; the caller is a
    /// party (<see cref="IsParty"/>) by the time this is asked.
    /// </summary>
    private static TransferResult? DecisionRefusal(StockTransfer t, TransferActor actor)
    {
        if (actor.OwnBranchId is Guid own)
        {
            if (own != t.FromBranchId)
                return TransferResult.Forbidden("A reported shortfall is confirmed or rejected by the branch that dispatched the goods, not by the branch that reported it.");
            return actor.MayTransfer || actor.MayWriteOff
                ? null
                : TransferResult.Forbidden("Answering a reported shortfall needs the stock transfer permission.");
        }
        return actor.MayWriteOff
            ? null
            : TransferResult.Forbidden("Answering a reported shortfall for any branch needs the permission to write off goods lost in transit.");
    }

    private Task<StockTransfer?> LoadAsync(Guid id, CancellationToken ct) =>
        // Read after the lock, so the figures are the ones the previous step committed.
        db.StockTransfers.Include(t => t.Lines).Include(t => t.Events).FirstOrDefaultAsync(t => t.Id == id, ct);

    private static TransferResult NotOpen(StockTransfer t) => TransferResult.Conflict(t.Status == StockTransferStatus.Cancelled
        ? $"{t.Number} is cancelled; nothing is in transit."
        : $"{t.Number} is received; nothing is in transit.");

    /// <summary>Each line named once, on this transfer, within what is still in transit, and something to do.</summary>
    private static TransferResult? Validate(StockTransfer t, IReadOnlyList<TransferLineQty>? lines)
    {
        if (lines is null || lines.Count == 0 || lines.All(l => l.Qty == 0)) return TransferResult.BadRequest("Enter a quantity on at least one line.");
        if (lines.Select(l => l.LineId).Distinct().Count() != lines.Count) return TransferResult.BadRequest("A line is named twice.");
        foreach (var asked in lines)
        {
            var line = t.Lines.FirstOrDefault(l => l.Id == asked.LineId);
            if (line is null) return TransferResult.BadRequest($"That line is not on {t.Number}.");
            if (asked.Qty < 0) return TransferResult.BadRequest("A quantity cannot be negative.");
            if (asked.Qty > line.InTransitQty)
                return TransferResult.Conflict($"Only {line.InTransitQty:0.##} of {line.Sku} is in transit on {t.Number}.");
        }
        return null;
    }

    /// <summary>
    /// A receipt: each line named once, on this transfer, something received or reported short;
    /// received within what is in transit; reported short within what the receipt leaves in transit
    /// and is not already reported short and waiting for an answer.
    /// </summary>
    private static TransferResult? ValidateReceipt(StockTransfer t, IReadOnlyList<ReceiveLineQty>? lines)
    {
        if (lines is null || lines.Count == 0 || lines.All(l => l.Qty == 0 && l.Short == 0))
            return TransferResult.BadRequest("Enter a quantity on at least one line.");
        if (lines.Select(l => l.LineId).Distinct().Count() != lines.Count) return TransferResult.BadRequest("A line is named twice.");
        foreach (var asked in lines)
        {
            var line = t.Lines.FirstOrDefault(l => l.Id == asked.LineId);
            if (line is null) return TransferResult.BadRequest($"That line is not on {t.Number}.");
            if (asked.Qty < 0 || asked.Short < 0) return TransferResult.BadRequest("A quantity cannot be negative.");
            if (asked.Qty > line.InTransitQty)
                return TransferResult.Conflict($"Only {line.InTransitQty:0.##} of {line.Sku} is in transit on {t.Number}.");
            // What the receipt leaves in transit, less what is already reported short there.
            var left = line.InTransitQty - asked.Qty;
            var reportable = left - Math.Min(line.QtyShortPending, left);
            if (asked.Short > reportable)
                return TransferResult.Conflict(line.QtyShortPending > 0
                    ? $"Only {reportable:0.##} more of {line.Sku} can be reported short on {t.Number}: {line.QtyShortPending:0.##} is already reported short and waiting for an answer."
                    : $"Only {reportable:0.##} of {line.Sku} is left in transit to report short on {t.Number}.");
        }
        return null;
    }

    /// <summary>
    /// An answer to a reported shortfall: something is waiting, each line named once, on this
    /// transfer, within what it has reported short — the figures the screen showed, so an answer never
    /// covers a report made after it was read.
    /// </summary>
    private static TransferResult? ValidateShortfall(StockTransfer t, IReadOnlyList<TransferLineQty>? lines)
    {
        if (!t.HasPendingShortfall) return TransferResult.Conflict($"Nothing on {t.Number} is reported short and waiting for an answer.");
        if (lines is null || lines.Count == 0 || lines.All(l => l.Qty == 0)) return TransferResult.BadRequest("Enter a quantity on at least one line.");
        if (lines.Select(l => l.LineId).Distinct().Count() != lines.Count) return TransferResult.BadRequest("A line is named twice.");
        foreach (var asked in lines)
        {
            var line = t.Lines.FirstOrDefault(l => l.Id == asked.LineId);
            if (line is null) return TransferResult.BadRequest($"That line is not on {t.Number}.");
            if (asked.Qty < 0) return TransferResult.BadRequest("A quantity cannot be negative.");
            if (asked.Qty > line.QtyShortPending)
                return TransferResult.Conflict($"Only {line.QtyShortPending:0.##} of {line.Sku} is reported short on {t.Number}.");
        }
        return null;
    }

    private static IEnumerable<(StockTransferLine Line, decimal Qty)> Pairs(StockTransfer t, IReadOnlyList<TransferLineQty> lines) =>
        lines.Where(l => l.Qty > 0).Select(l => (t.Lines.First(x => x.Id == l.LineId), l.Qty));

    /// <summary>What the destination said when it last reported something short on this transfer.</summary>
    private static string? LastReportReason(StockTransfer t) => t.Events
        .Where(e => e.Kind == StockTransferEventKind.ShortfallReported)
        .OrderByDescending(e => e.AtUtc).Select(e => e.Reason).FirstOrDefault();

    /// <summary>The status the lines now say, and when it closed; never more reported short than is in transit.</summary>
    private static void Settle(StockTransfer t)
    {
        foreach (var line in t.Lines) ClampShortfall(line);
        t.Status = StockTransfer.StatusFor(t.Lines);
        if (t.Status == StockTransferStatus.Received) t.ClosedAtUtc ??= DateTime.UtcNow;
    }

    /// <summary>
    /// Goods received, declared lost or sent back after being reported short clear that much of the
    /// report: what waits for an answer is never more than what is still in transit.
    /// </summary>
    private static void ClampShortfall(StockTransferLine line) =>
        line.QtyShortPending = Math.Max(0m, Math.Min(line.QtyShortPending, line.InTransitQty));

    private StockTransferEvent Step(StockTransfer t, StockTransferEventKind kind, TransferActor actor, string? reason)
    {
        // Steps read back in time order; two taken in one call (a receipt and its report) must not tie.
        var at = DateTime.UtcNow;
        if (t.Events.Count > 0 && t.Events.Max(e => e.AtUtc) is var last && at <= last) at = last.AddTicks(1);
        var step = new StockTransferEvent { TransferId = t.Id, Kind = kind, AtUtc = at, UserId = actor.UserId, UserName = actor.UserName, Reason = reason };
        t.Events.Add(step);
        // A transfer already in the database is tracked, and a new child of it must be marked new.
        if (db.Entry(t).State != EntityState.Detached && db.Entry(t).State != EntityState.Added) db.StockTransferEvents.Add(step);
        return step;
    }

    /// <summary>
    /// The steps' movements valued as the shadow values them once sales are costed at the weighted average (C-01): each
    /// movement takes its unit cost and basis, and null comes back while they are costed at the cost price (the caller
    /// keeps the line's cost). Never throws: a failure falls back to the line's cost, flagged.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, LiveMovementCost>?> CostAsync(IEnumerable<PlannedMovement> planned, CancellationToken ct)
    {
        var list = planned.ToList();
        var costs = await new LiveSaleCosting(db).MovementCostsOrFallbackAsync(list, logger, ct);
        if (costs is null) return null;
        foreach (var plan in list)
        {
            var cost = costs[plan.Movement.Id];
            plan.Movement.UnitCost = cost.UnitCost;
            plan.Movement.CostBasis = cost.Basis;
        }
        return costs;
    }

    /// <summary>
    /// REN-12 (Phase M7a): the lines of hire products the fixed-asset register holds post through it — a receipt moves the units
    /// between the branches' pools with their cost and depreciation (<paramref name="lostWhy"/> null), a loss takes them out of
    /// the source's pool at their net book value — and are left out of the step's own entry; the other lines are returned
    /// for it. Off (the default), every line is returned and the step posts exactly as before.
    /// </summary>
    private async Task<List<ValuedLine>> RegisterAsync(
        List<(StockTransferLine Line, decimal Qty, PlannedMovement Plan)> planned, List<ValuedLine> valued,
        StockTransfer transfer, StockTransferEvent step, string? lostWhy, CancellationToken ct)
    {
        var assets = new FixedAssetBook(db);
        var kept = new List<ValuedLine>(valued.Count);
        for (var i = 0; i < planned.Count; i++)
        {
            var (line, qty, _) = planned[i];
            if (!await assets.HoldsAsync(line.ProductId, ct))
            {
                kept.Add(valued[i]);
                continue;
            }
            var asset = new FixedAssetEvent(line.ProductId, transfer.FromBranchId, qty, line.Sku, step.Id, transfer.Number,
                lostWhy is null ? "transfer received" : $"lost in transit: {lostWhy}", step.UserName);
            var draft = lostWhy is null
                ? await assets.TransferAsync(asset, transfer.ToBranchId, ct)
                : await assets.DisposeAsync(asset, ct);
            if (draft is not null) await ledger.PostAsync(draft, ct);
        }
        return kept;
    }

    /// <summary>What each line of a step posts: quantity × the line's cost, or what the shadow values the step's movement at.</summary>
    private async Task<List<ValuedLine>> ValuedAsync(
        List<(StockTransferLine Line, decimal Qty, PlannedMovement Plan)> planned, CancellationToken ct)
    {
        var costs = await CostAsync(planned.Select(p => p.Plan), ct);
        return planned.Select(p => new ValuedLine(p.Line.Sku,
            costs is null ? p.Qty * p.Line.UnitCost : costs[p.Plan.Movement.Id].Value)).ToList();
    }

    private StockMovement Movement(StockTransfer t, StockTransferEvent step, Guid productId, Guid branchId, Guid relatedBranchId,
        decimal delta, decimal moved, decimal balance, string reason, string? words, decimal unitCost) =>
        db.StockMovements.Add(new StockMovement
        {
            ProductId = productId, BranchId = branchId, RelatedBranchId = relatedBranchId,
            QtyDelta = delta, MovedQty = moved, BalanceAfter = balance,
            Reason = StockMovement.Label(reason, words),
            Kind = reason switch
            {
                StockTransferReasons.Dispatch => StockMovementKind.TransferOut,
                StockTransferReasons.Receive => StockMovementKind.TransferIn,
                StockTransferReasons.Cancel => StockMovementKind.TransferCancel,
                StockTransferReasons.Loss => StockMovementKind.TransferLoss,
                _ => StockMovementKind.Unclassified,
            },
            // The line's cost, fixed at dispatch: what the entries of this transfer value it at — until sales are costed
            // at the weighted average (C-01), when CostAsync replaces it with the replay's figure.
            UnitCost = unitCost,
            DocumentId = step.Id, DocumentNumber = t.Number,
            UserId = step.UserId, UserName = step.UserName,
        }).Entity;

    private async Task TouchAsync(Guid productId, CancellationToken ct)
    {
        // Terminals watch the product's version, so every branch's stock change bumps it.
        if (await db.Products.FirstOrDefaultAsync(p => p.Id == productId, ct) is { } product) await db.TouchAsync(product, ct);
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
