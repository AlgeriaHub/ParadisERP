using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Manufacturing;

/// <summary>
/// What moves on an order (Phase 2 §2B, §2E; amendment 8), kept apart: an issue takes material out of the
/// line's stock to the order; a return sends unused material back; scrap and consumption record what
/// became of issued material and move no stock; a release brings finished goods into the line's stock.
///
/// <para>
/// Each request carries its own id: a retried one answers the first document and moves nothing again.
/// Each runs in one transaction that takes the version lock before it reads a balance or a stock figure,
/// so per material issued − returned − consumed − scrapped never goes below zero, the line's stock never
/// goes below zero, and another order's reservation is never taken. Every stock row it writes names the
/// document, the user and the line's location; every document carries the reference cost snapshot and
/// stays Pending — nothing posts (owner, 2026-10-26, decision 1).
/// </para>
/// </summary>
public sealed class ProductionMovements(PosDbContext db, ManufacturingSettings settings, ManufacturingOptions options, ProductionOrders orders)
{
    /// <summary>Stock reasons, short as the column wants (StockMovement.ReasonMaxLength).</summary>
    public const string IssueReason = "mfg-issue";
    public const string ReturnReason = "mfg-return";
    public const string OutputReason = "mfg-output";

    private async Task<(PosDbContext.WriteScope? Scope, ProductionOrder? Order, MfgResult<ProductionOrderDto>? Refusal)>
        OpenAsync(MfgCaller caller, Guid id, CancellationToken ct)
    {
        if (!options.StockOperations) return (null, null, Production.NotEnabled<ProductionOrderDto>());
        var scope = await db.BeginWriteScopeAsync(ct);
        await db.LockVersionsAsync(ct);
        var order = await db.ProductionOrders.Include(o => o.Materials).ThenInclude(m => m.Product).Include(o => o.Stages).Include(o => o.Product)
            .AsSplitQuery().FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null || !await ManufacturingAccess.ReachesAsync(db, caller, order.LineId, ct))
        {
            await scope.DisposeAsync();
            return (null, null, MfgFail.NotFound<ProductionOrderDto>("Production order"));
        }
        return (scope, order, null);
    }

    private static async Task<MfgResult<ProductionOrderDto>> Refuse(
        PosDbContext.WriteScope scope, MfgResult<ProductionOrderDto> refusal)
    {
        await scope.DisposeAsync();
        return refusal;
    }

    /// <summary>
    /// A request id already used, read once the order is reached and the lock is held: a copy of a request sent
    /// alongside the first then finds the first's document instead of being checked a second time, and another
    /// line's order stays not found. The order again when the id is this order's, else a refusal; null when new.
    /// </summary>
    private async Task<MfgResult<ProductionOrderDto>?> ReplayAsync(
        PosDbContext.WriteScope scope, MfgCaller caller, Guid id, Guid? usedByOrder, CancellationToken ct)
    {
        if (usedByOrder is not Guid owner) return null;
        await scope.DisposeAsync();
        return owner == id
            ? await orders.GetAsync(caller, id, ct)
            : MfgFail.Conflict<ProductionOrderDto>("request_id_reused", "This request id belongs to another order.");
    }

    private async Task<MfgResult<ProductionOrderDto>> CommitAsync(
        PosDbContext.WriteScope scope, MfgCaller caller, ProductionOrder order, CancellationToken ct)
    {
        order.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
        await scope.DisposeAsync();
        return await orders.GetAsync(caller, order.Id, ct);
    }

    private static string? RequestId(string? value) => MfgText.Clean(value) is string id && id.Length <= 64 ? id : null;

    // ---------- issue, return, scrap ----------

    public async Task<MfgResult<ProductionOrderDto>> MoveAsync(MfgCaller caller, Guid id, MaterialMovementRequest r, CancellationToken ct)
    {
        if (RequestId(r.ClientRequestId) is not string requestId)
            return MfgFail.Invalid<ProductionOrderDto>("request_id_required", "Each request carries its own id.");
        if (!Enum.TryParse<MaterialMovementKind>(r.Kind, true, out var kind) || !Enum.IsDefined(kind))
            return MfgFail.Invalid<ProductionOrderDto>("invalid_kind", "Choose an issue, a return or scrap.");
        var wanted = r.Lines ?? [];
        if (wanted.Count == 0 || wanted.Count > 200)
            return MfgFail.Invalid<ProductionOrderDto>("lines_required", "Name at least one material.");

        var (scope, order, refusal) = await OpenAsync(caller, id, ct);
        if (refusal is not null) return refusal;
        var usedBy = await db.MaterialMovements.AsNoTracking().Where(m => m.ClientRequestId == requestId).Select(m => (Guid?)m.OrderId).FirstOrDefaultAsync(ct);
        if (await ReplayAsync(scope!, caller, id, usedBy, ct) is { } replay) return replay;
        if (order!.Status != ProductionOrderStatus.Open)
            return await Refuse(scope!, MfgFail.Conflict<ProductionOrderDto>("not_open", "Materials move only on an open order."));

        // Every line converted and checked before anything changes.
        var lines = new List<(ProductionOrderMaterial Material, MaterialMovementLineRequest Request, decimal Factor, decimal Base)>();
        foreach (var w in wanted)
        {
            var material = order.Materials.FirstOrDefault(m => m.Id == w.OrderMaterialId);
            if (material is null) return await Refuse(scope!, MfgFail.NotFound<ProductionOrderDto>("Material"));
            if (w.Qty <= 0 || w.Qty > Production.MaxQuantity)
                return await Refuse(scope!, MfgFail.Invalid<ProductionOrderDto>("invalid_quantity", $"{material.Product!.Sku}: the quantity must be above zero."));
            if (await Production.ToBaseAsync(db, settings, material.ProductId, w.UnitId, ct) is not { } toBase)
                return await Refuse(scope!, MfgFail.Invalid<ProductionOrderDto>("no_conversion", $"{material.Product!.Sku}: that unit does not convert to its base unit."));
            lines.Add((material, w, toBase.Factor, Production.Round(w.Qty * toBase.Factor)));
        }
        foreach (var group in lines.GroupBy(l => l.Material.Id))
        {
            var material = group.First().Material;
            var total = group.Sum(l => l.Base);
            if (kind is MaterialMovementKind.Return or MaterialMovementKind.Scrap && total > Production.AtLine(material))
                return await Refuse(scope!, MfgFail.Conflict<ProductionOrderDto>("over_balance",
                    $"{material.Product!.Sku}: only {Production.AtLine(material):0.######} is still at the line for this order."));
        }

        var rows = new StockRows(db);
        if (kind == MaterialMovementKind.Issue)
        {
            // Never fictitious stock (owner, decision 3): the location must hold it, and another order's claim is not taken.
            var ids = lines.Select(l => l.Material.ProductId).Distinct().ToList();
            var reserved = await Production.ReservedAtAsync(db, order.LocationId, ids, order.Id, ct);
            foreach (var group in lines.GroupBy(l => l.Material.ProductId))
            {
                var product = group.First().Material.Product!;
                if (!product.TrackStock) continue;
                var onHand = (await rows.FindAsync(product.Id, order.LocationId, ct))?.StockQty ?? 0m;
                var total = group.Sum(l => l.Base);
                if (total > onHand)
                    return await Refuse(scope!, MfgFail.Conflict<ProductionOrderDto>("insufficient_stock",
                        $"{product.Sku}: {onHand:0.######} at the line, {total:0.######} asked."));
                if (total > onHand - reserved.GetValueOrDefault(product.Id))
                    return await Refuse(scope!, MfgFail.Conflict<ProductionOrderDto>("reserved_by_other_orders",
                        $"{product.Sku}: {Math.Max(0m, onHand - reserved.GetValueOrDefault(product.Id)):0.######} is free; the rest is reserved for other orders."));
            }
        }

        var prefix = kind switch { MaterialMovementKind.Issue => "MI", MaterialMovementKind.Return => "MR", _ => "MS" };
        var movement = new MaterialMovement
        {
            Number = Production.Number(prefix, await db.MaterialMovements.CountAsync(m => m.Kind == kind, ct)),
            Kind = kind, OrderId = order.Id, LineId = order.LineId, LocationId = order.LocationId, ClientRequestId = requestId,
            Note = Production.Clean(r.Note, 400), UserId = caller.UserId, UserName = caller.Name,
        };
        var issuedCost = await IssuedCostAsync(order.Id, ct);
        foreach (var (material, request, factor, baseQty) in lines)
        {
            var product = material.Product!;
            // A return goes back at what its issues cost (Revision 2 §5); an issue and scrap at the cost now.
            var unitCost = kind == MaterialMovementKind.Issue ? product.CostPrice : issuedCost.GetValueOrDefault(material.Id, product.CostPrice);
            var line = new MaterialMovementLine
            {
                MovementId = movement.Id, OrderMaterialId = material.Id, ProductId = product.Id, EnteredQty = request.Qty,
                UnitId = request.UnitId, Factor = factor, BaseQty = baseQty, UnitCostSnapshot = unitCost,
            };
            movement.Lines.Add(line);
            switch (kind)
            {
                case MaterialMovementKind.Issue: material.IssuedBaseQty += baseQty; break;
                case MaterialMovementKind.Return: material.ReturnedBaseQty += baseQty; break;
                default: material.ScrappedBaseQty += baseQty; break;
            }
            if (kind == MaterialMovementKind.Scrap || !product.TrackStock) continue;

            var delta = kind == MaterialMovementKind.Issue ? -baseQty : baseQty;
            var stock = await rows.GetAsync(product.Id, order.LocationId, ct);
            stock.StockQty += delta;
            var stockMovement = new StockMovement
            {
                ProductId = product.Id, BranchId = order.LocationId, QtyDelta = delta, BalanceAfter = stock.StockQty,
                Reason = kind == MaterialMovementKind.Issue ? IssueReason : ReturnReason, DocumentId = movement.Id,
                Kind = kind == MaterialMovementKind.Issue ? StockMovementKind.ManufacturingIssue : StockMovementKind.ManufacturingReturn,
                UnitCost = unitCost,
                UserId = caller.UserId, UserName = caller.Name, DocumentNumber = movement.Number, MovedQty = baseQty,
            };
            db.StockMovements.Add(stockMovement);
            line.StockMovementId = stockMovement.Id;
            await db.TouchAsync(product, ct);
        }
        db.MaterialMovements.Add(movement);
        return await CommitAsync(scope!, caller, order, ct);
    }

    /// <summary>Per material of the order, the average reference cost of what was issued to it so far.</summary>
    private async Task<Dictionary<Guid, decimal>> IssuedCostAsync(Guid orderId, CancellationToken ct)
    {
        var issued = await db.MaterialMovementLines.AsNoTracking()
            .Where(l => l.Movement!.OrderId == orderId && l.Movement.Kind == MaterialMovementKind.Issue)
            .Select(l => new { l.OrderMaterialId, l.BaseQty, l.UnitCostSnapshot }).ToListAsync(ct);
        return issued.GroupBy(l => l.OrderMaterialId).Where(g => g.Sum(l => l.BaseQty) > 0)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.BaseQty * l.UnitCostSnapshot) / g.Sum(l => l.BaseQty));
    }

    // ---------- consumption ----------

    /// <summary>What was actually used out of the issued balance, optionally on a daily record row of this order.</summary>
    public async Task<MfgResult<ProductionOrderDto>> ConsumeAsync(MfgCaller caller, Guid id, ConsumptionRequest r, CancellationToken ct)
    {
        if (RequestId(r.ClientRequestId) is not string requestId)
            return MfgFail.Invalid<ProductionOrderDto>("request_id_required", "Each request carries its own id.");
        if (!Enum.TryParse<ConsumptionReason>(r.Reason, true, out var reason) || !Enum.IsDefined(reason))
            return MfgFail.Invalid<ProductionOrderDto>("invalid_reason", "Choose why it was consumed.");

        var (scope, order, refusal) = await OpenAsync(caller, id, ct);
        if (refusal is not null) return refusal;
        var usedBy = await db.MaterialConsumptions.AsNoTracking().Where(c => c.ClientRequestId == requestId).Select(c => (Guid?)c.OrderId).FirstOrDefaultAsync(ct);
        if (await ReplayAsync(scope!, caller, id, usedBy, ct) is { } replay) return replay;
        // An order waiting for completion may still have its leftover count recorded; not a closed one.
        if (order!.Status is not (ProductionOrderStatus.Open or ProductionOrderStatus.OnHold))
            return await Refuse(scope!, MfgFail.Conflict<ProductionOrderDto>("not_open", "Consumption is recorded on an open order."));
        var material = order.Materials.FirstOrDefault(m => m.Id == r.OrderMaterialId);
        if (material is null) return await Refuse(scope!, MfgFail.NotFound<ProductionOrderDto>("Material"));
        if (r.BaseQty <= 0 || r.BaseQty > Production.MaxQuantity)
            return await Refuse(scope!, MfgFail.Invalid<ProductionOrderDto>("invalid_quantity", "The quantity must be above zero."));
        var qty = Production.Round(r.BaseQty);
        if (qty > Production.AtLine(material))
            return await Refuse(scope!, MfgFail.Conflict<ProductionOrderDto>("over_balance",
                $"{material.Product!.Sku}: only {Production.AtLine(material):0.######} is still at the line for this order."));
        if (r.LogRowId is Guid rowId && !await db.ProductionLogRows.AnyAsync(x => x.Id == rowId && x.OrderId == order.Id, ct))
            return await Refuse(scope!, MfgFail.Invalid<ProductionOrderDto>("row_not_on_order", "That daily record row is not this order's."));

        material.ConsumedBaseQty += qty;
        db.MaterialConsumptions.Add(new MaterialConsumption
        {
            OrderId = order.Id, OrderMaterialId = material.Id, LogRowId = r.LogRowId, BaseQty = qty, Reason = reason,
            Note = Production.Clean(r.Note, 400), ClientRequestId = requestId, UserId = caller.UserId, UserName = caller.Name,
        });
        return await CommitAsync(scope!, caller, order, ct);
    }

    // ---------- output ----------

    /// <summary>
    /// Finished goods into the line's stock, by a production manager of the line: never more than the
    /// output stage's approved good quantity not yet released, so a unit counted at several stages is
    /// released once. The order stays open for the rest (owner, decision 5).
    /// </summary>
    public async Task<MfgResult<ProductionOrderDto>> ReleaseAsync(MfgCaller caller, Guid id, OutputReleaseRequest r, CancellationToken ct)
    {
        if (RequestId(r.ClientRequestId) is not string requestId)
            return MfgFail.Invalid<ProductionOrderDto>("request_id_required", "Each request carries its own id.");
        var (scope, order, refusal) = await OpenAsync(caller, id, ct);
        if (refusal is not null) return refusal;
        var usedBy = await db.OutputReleases.AsNoTracking().Where(x => x.ClientRequestId == requestId).Select(x => (Guid?)x.OrderId).FirstOrDefaultAsync(ct);
        if (await ReplayAsync(scope!, caller, id, usedBy, ct) is { } replay) return replay;
        if (order!.Status != ProductionOrderStatus.Open)
            return await Refuse(scope!, MfgFail.Conflict<ProductionOrderDto>("not_open", "Output is released from an open order."));
        if (!await ManufacturingAccess.ManagesAsync(db, caller, order.LineId, ct))
            return await Refuse(scope!, MfgFail.Forbidden<ProductionOrderDto>("Releasing output is for a production manager of the line."));
        if (r.Qty <= 0 || r.Qty > Production.MaxQuantity)
            return await Refuse(scope!, MfgFail.Invalid<ProductionOrderDto>("invalid_quantity", "The quantity must be above zero."));
        if (await Production.ToBaseAsync(db, settings, order.ProductId, r.UnitId, ct) is not { } toBase)
            return await Refuse(scope!, MfgFail.Invalid<ProductionOrderDto>("no_conversion", "That unit does not convert to the product's base unit."));
        var baseQty = Production.Round(r.Qty * toBase.Factor);
        var awaiting = (order.Stages.FirstOrDefault(s => s.IsOutput)?.GoodBaseQty ?? 0m) - order.AcceptedBaseQty;
        if (baseQty > awaiting)
            return await Refuse(scope!, MfgFail.Conflict<ProductionOrderDto>("over_output",
                $"{Math.Max(0m, awaiting):0.######} approved output is awaiting release."));

        var product = order.Product!;
        var release = new OutputRelease
        {
            Number = Production.Number("OR", await db.OutputReleases.CountAsync(ct)),
            OrderId = order.Id, LineId = order.LineId, LocationId = order.LocationId, EnteredQty = r.Qty, UnitId = r.UnitId,
            Factor = toBase.Factor, BaseQty = baseQty, UnitCostSnapshot = product.CostPrice, ClientRequestId = requestId,
            Note = Production.Clean(r.Note, 400), UserId = caller.UserId, UserName = caller.Name,
        };
        if (product.TrackStock)
        {
            var stock = await new StockRows(db).GetAsync(product.Id, order.LocationId, ct);
            stock.StockQty += baseQty;
            var stockMovement = new StockMovement
            {
                ProductId = product.Id, BranchId = order.LocationId, QtyDelta = baseQty, BalanceAfter = stock.StockQty,
                Reason = OutputReason, DocumentId = release.Id, UserId = caller.UserId, UserName = caller.Name,
                Kind = StockMovementKind.ManufacturingOutput, UnitCost = release.UnitCostSnapshot,
                DocumentNumber = release.Number, MovedQty = baseQty,
            };
            db.StockMovements.Add(stockMovement);
            release.StockMovementId = stockMovement.Id;
            await db.TouchAsync(product, ct);
        }
        order.AcceptedBaseQty += baseQty;
        db.OutputReleases.Add(release);
        db.ProductionOrderEvents.Add(new ProductionOrderEvent
        {
            OrderId = order.Id, Kind = ProductionOrderEventKind.OutputReleased, Detail = $"{release.Number} {baseQty:0.######}",
            UserId = caller.UserId, UserName = caller.Name,
        });
        return await CommitAsync(scope!, caller, order, ct);
    }
}
