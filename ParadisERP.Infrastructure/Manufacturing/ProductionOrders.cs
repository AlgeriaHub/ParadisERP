using ParadisERP.Application.Contracts;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Manufacturing;

/// <summary>
/// Production orders (Phase 2 §2A, §2F): made from the line's active BOM version, copied at creation; their
/// materials reserved at the line's location; held, resumed, cancelled; completed only when a production
/// manager of the line who did not ask for it approves. Nothing changes on its own: a passed planned end
/// only shows "late".
///
/// <para>
/// Every write takes the version lock first (<see cref="PosDbContext.LockVersionsAsync"/>) inside one
/// transaction, then reads what it checks: two supervisors creating orders for the last of the wire at
/// the same moment cannot both pass.
/// </para>
/// </summary>
public sealed class ProductionOrders(
    PosDbContext db, BillsOfMaterials boms, ManufacturingSettings settings, ManufacturingOptions options,
    IRegionalSettingsProvider regional, TimeProvider time)
{
    // ---------- reading ----------

    public async Task<List<ProductionOrderSummaryDto>> ListAsync(
        MfgCaller caller, Guid? lineId, string? status, Guid? productId, CancellationToken ct)
    {
        var reachable = ManufacturingAccess.Reachable(db, caller).Select(l => l.Id);
        var query = db.ProductionOrders.AsNoTracking().Where(o => reachable.Contains(o.LineId));
        if (lineId is Guid l) query = query.Where(o => o.LineId == l);
        if (productId is Guid p) query = query.Where(o => o.ProductId == p);
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<ProductionOrderStatus>(status, true, out var s)) return [];
            query = query.Where(o => o.Status == s);
        }
        var orders = await query.Include(o => o.Line).Include(o => o.Product).OrderByDescending(o => o.CreatedAtUtc).Take(500).ToListAsync(ct);
        var units = await UnitCodesAsync(ct);
        var today = Production.Today(regional, time);
        return orders.Select(o => Summary(o, units, today)).ToList();
    }

    private async Task<Dictionary<Guid, string>> UnitCodesAsync(CancellationToken ct) =>
        await db.UnitsOfMeasure.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Code, ct);

    private static ProductionOrderSummaryDto Summary(ProductionOrder o, IReadOnlyDictionary<Guid, string> units, DateOnly today)
    {
        var end = o.ActualEnd ?? (o.Status is ProductionOrderStatus.Cancelled ? null : today);
        var overdue = end is DateOnly e && e > o.PlannedEnd ? e.DayNumber - o.PlannedEnd.DayNumber : 0;
        return new ProductionOrderSummaryDto(
            o.Id, o.Number, o.LineId, o.Line?.Code ?? "", o.ProductId, o.Product?.Sku ?? "",
            o.Product?.NameAr ?? "", o.Product?.NameFr ?? "", o.Product?.NameEn ?? "", o.Status.ToString(),
            o.PlannedBaseQty, o.Product?.BaseUnitId is Guid b ? units.GetValueOrDefault(b, "") : "", o.AcceptedBaseQty, o.RejectedBaseQty,
            o.PlannedStart, o.PlannedEnd, o.ActualEnd, overdue,
            Late: o.ActualEnd is null && Production.Live.Contains(o.Status) && today > o.PlannedEnd,
            o.StartedShort, o.CreatedAtUtc);
    }

    public async Task<MfgResult<ProductionOrderDto>> GetAsync(MfgCaller caller, Guid id, CancellationToken ct)
    {
        var order = await db.ProductionOrders.AsNoTracking().Include(o => o.Line).Include(o => o.Product)
            .Include(o => o.Materials).ThenInclude(m => m.Product).Include(o => o.Stages)
            .AsSplitQuery().FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null || !await ManufacturingAccess.ReachesAsync(db, caller, order.LineId, ct))
            return MfgFail.NotFound<ProductionOrderDto>("Production order");

        var units = await UnitCodesAsync(ct);
        var movements = await db.MaterialMovements.AsNoTracking().Include(m => m.Lines).Where(m => m.OrderId == id)
            .OrderBy(m => m.AtUtc).ToListAsync(ct);
        var consumptions = await db.MaterialConsumptions.AsNoTracking().Where(c => c.OrderId == id).OrderBy(c => c.AtUtc).ToListAsync(ct);
        var releases = await db.OutputReleases.AsNoTracking().Where(r => r.OrderId == id).OrderBy(r => r.AtUtc).ToListAsync(ct);
        var expenses = await db.ProductionOrderExpenses.AsNoTracking().Where(e => e.OrderId == id).OrderBy(e => e.AtUtc).ToListAsync(ct);
        var events = await db.ProductionOrderEvents.AsNoTracking().Where(e => e.OrderId == id).OrderBy(e => e.AtUtc).ToListAsync(ct);
        var hours = (await db.LaborBookings.AsNoTracking().Where(b => b.OrderId == id && b.OrderKind == LaborOrderKind.Production)
                .Select(b => new { b.StageId, b.Hours }).ToListAsync(ct))
            .GroupBy(b => b.StageId).ToDictionary(g => g.Key ?? Guid.Empty, g => g.Sum(b => b.Hours));
        var skus = order.Materials.ToDictionary(m => m.Id, m => m.Product?.Sku ?? "");

        var output = order.Stages.FirstOrDefault(s => s.IsOutput);
        var produced = output?.GoodBaseQty ?? 0m;
        var manages = await ManufacturingAccess.ManagesAsync(db, caller, order.LineId, ct);
        var live = Production.Live.Contains(order.Status);
        var today = Production.Today(regional, time);

        return MfgResult<ProductionOrderDto>.Done(new ProductionOrderDto(
            Summary(order, units, today), order.BomId, order.BomVersion, order.PlannedQty, units.GetValueOrDefault(order.PlannedUnitId, ""),
            order.Batches, order.ShortageReason, order.Notes, order.EstimatedMaterialCost,
            produced, Math.Max(0m, produced - order.AcceptedBaseQty), Math.Max(0m, order.PlannedBaseQty - order.AcceptedBaseQty),
            order.CreatedBy, null, order.CompletionRequestedAtUtc, order.CompletedBy, order.CompletedAtUtc,
            order.Materials.OrderBy(m => m.Sequence).Select(m => new OrderMaterialDto(
                m.Id, m.Sequence, m.ProductId, m.Product?.Sku ?? "", m.Product?.NameAr ?? "", m.Product?.NameFr ?? "", m.Product?.NameEn ?? "",
                m.Role.ToString(), m.BaseUnitId, units.GetValueOrDefault(m.BaseUnitId, ""), m.NetBaseQty, m.WasteBaseQty, m.RequiredBaseQty,
                m.EstimatedUnitCost, m.Specification, m.IssuedBaseQty, m.ReturnedBaseQty, m.ConsumedBaseQty, m.ScrappedBaseQty,
                m.ReservationReleasedBaseQty, Production.Reserved(m, order.Status), Production.AtLine(m))).ToList(),
            order.Stages.OrderBy(s => s.Sequence).Select(s => new OrderStageDto(
                s.Id, s.Sequence, s.NameAr, s.NameFr, s.NameEn, s.PlannedMinutes, s.ExpectedWorkers, s.IsQualityCheckpoint, s.IsOutput,
                s.GoodBaseQty, s.RejectedBaseQty, hours.GetValueOrDefault(s.Id))).ToList(),
            movements.Select(m => new MaterialMovementDto(m.Id, m.Number, m.Kind.ToString(), m.PostingState.ToString(), m.Note, m.UserName, m.AtUtc,
                m.Lines.Select(l => new MaterialMovementLineDto(l.OrderMaterialId, l.ProductId, skus.GetValueOrDefault(l.OrderMaterialId, ""),
                    l.EnteredQty, units.GetValueOrDefault(l.UnitId, ""), l.BaseQty, l.UnitCostSnapshot)).ToList())).ToList(),
            consumptions.Select(c => new ConsumptionDto(c.Id, c.OrderMaterialId, skus.GetValueOrDefault(c.OrderMaterialId, ""), c.LogRowId,
                c.BaseQty, c.Reason.ToString(), c.Note, c.UserName, c.AtUtc)).ToList(),
            releases.Select(r => new OutputReleaseDto(r.Id, r.Number, r.EnteredQty, units.GetValueOrDefault(r.UnitId, ""), r.BaseQty,
                r.UnitCostSnapshot, r.PostingState.ToString(), r.Note, r.UserName, r.AtUtc)).ToList(),
            expenses.Select(ToDto).ToList(),
            events.Select(e => new OrderEventDto(e.Kind.ToString(), e.Detail, e.UserName, e.AtUtc)).ToList(),
            CanManage: live && caller.Has(PermissionCatalog.ManufacturingOrdersManage),
            CanMoveMaterials: order.Status == ProductionOrderStatus.Open && caller.Has(PermissionCatalog.ManufacturingMaterialsMove),
            CanRelease: order.Status == ProductionOrderStatus.Open && manages && caller.Has(PermissionCatalog.ManufacturingOutputRelease),
            CanRequestCompletion: order.Status == ProductionOrderStatus.Open && caller.Has(PermissionCatalog.ManufacturingLogSubmit),
            CanApproveCompletion: order.Status == ProductionOrderStatus.CompletionRequested && manages
                && caller.Has(PermissionCatalog.ManufacturingCompletionApprove) && order.CompletionRequestedByUserId != caller.UserId));
    }

    private static OrderExpenseDto ToDto(ProductionOrderExpense e) => new(e.Id, e.Description, e.Amount, e.PaymentVoucherId, e.UserName, e.AtUtc);

    // ---------- creating ----------

    private sealed record Plan(
        BillOfMaterials Bom, Product Product, ProductionLine Line, decimal PlannedBaseQty, decimal Batches,
        List<(BomRequirementLineDto Need, Product Component, decimal NetBase, decimal WasteBase)> Lines);

    /// <summary>The order a request would make: the line's active BOM, the requirements, each product checked.</summary>
    private async Task<MfgResult<Plan>> PlanAsync(MfgCaller caller, CreateProductionOrderRequest r, CancellationToken ct)
    {
        var line = await ManufacturingAccess.Reachable(db, caller).AsNoTracking().FirstOrDefaultAsync(l => l.Id == r.LineId, ct);
        if (line is null) return MfgFail.NotFound<Plan>("Production line");
        if (!line.IsActive) return MfgFail.Invalid<Plan>("line_inactive", "This production line is switched off.");
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == r.ProductId && !p.IsDeleted, ct);
        if (product is null) return MfgFail.NotFound<Plan>("Product");
        if (!await db.ProductionLineProducts.AnyAsync(p => p.LineId == line.Id && p.ProductId == product.Id, ct))
            return MfgFail.Invalid<Plan>("product_not_on_line", $"{product.Sku} is not one of this line's products.");
        if (r.PlannedQty <= 0 || r.PlannedQty > Production.MaxQuantity)
            return MfgFail.Invalid<Plan>("invalid_quantity", "The planned quantity must be above zero.");
        if (r.PlannedEnd < r.PlannedStart)
            return MfgFail.Invalid<Plan>("invalid_dates", "The planned end cannot be before the planned start.");

        // The line's own active version first, then one for every line (Phase 1: a BOM may serve both).
        var bom = await db.BillsOfMaterials.AsNoTracking()
                      .Where(b => b.ProductId == product.Id && b.Status == BomStatus.Active && b.ProductionLineId == line.Id)
                      .FirstOrDefaultAsync(ct)
                  ?? await db.BillsOfMaterials.AsNoTracking()
                      .Where(b => b.ProductId == product.Id && b.Status == BomStatus.Active && b.ProductionLineId == null)
                      .FirstOrDefaultAsync(ct);
        if (bom is null) return MfgFail.Invalid<Plan>("no_active_bom", $"{product.Sku} has no approved recipe for this line.");

        if (await Production.ToBaseAsync(db, settings, product.Id, r.PlannedUnitId, ct) is not { } toBase)
            return MfgFail.Invalid<Plan>("no_conversion", "The planned unit does not convert to the product's base unit.");
        var need = await boms.RequirementsAsync(caller, bom.Id, r.PlannedQty, r.PlannedUnitId, ct);
        if (!need.Ok) return need.As<Plan>();

        var ids = need.Value!.Lines.Select(l => l.ComponentProductId).Distinct().ToList();
        var components = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var lines = need.Value.Lines.Select(l =>
        {
            // The BOM line's own unit to the base, as the requirement already converted the gross figure.
            var toComponentBase = l.GrossQuantity == 0 ? 0m : l.BaseQuantity / l.GrossQuantity;
            return (l, components[l.ComponentProductId], Production.Round(l.NetQuantity * toComponentBase), Production.Round(l.WasteQuantity * toComponentBase));
        }).ToList();
        return MfgResult<Plan>.Done(new Plan(bom, product, line, Production.Round(r.PlannedQty * toBase.Factor), need.Value.Batches, lines));
    }

    /// <summary>What each material needs against what the line can promise: stock less every other live order's reservation.</summary>
    private async Task<List<OrderMaterialAvailabilityDto>> AvailabilityAsync(Plan plan, CancellationToken ct)
    {
        var ids = plan.Lines.Select(l => l.Component.Id).Distinct().ToList();
        var stock = await db.ProductStocks.AsNoTracking().Where(s => s.BranchId == plan.Line.LocationId && ids.Contains(s.ProductId))
            .ToDictionaryAsync(s => s.ProductId, s => s.StockQty, ct);
        var reserved = await Production.ReservedAtAsync(db, plan.Line.LocationId, ids, null, ct);
        var units = await UnitCodesAsync(ct);
        // A component on two lines of the BOM is one claim on the stock.
        return plan.Lines.GroupBy(l => l.Component.Id).Select(g =>
        {
            var c = g.First().Component;
            var required = g.Sum(l => l.Need.BaseQuantity);
            var onHand = stock.GetValueOrDefault(c.Id);
            var others = reserved.GetValueOrDefault(c.Id);
            var free = Math.Max(0m, onHand - others);
            return new OrderMaterialAvailabilityDto(c.Id, c.Sku, c.NameAr, c.NameFr, c.NameEn, units.GetValueOrDefault(c.BaseUnitId ?? Guid.Empty, ""),
                required, onHand, others, free, c.TrackStock ? Math.Max(0m, required - free) : 0m);
        }).ToList();
    }

    public async Task<MfgResult<ProductionOrderPreviewDto>> PreviewAsync(MfgCaller caller, CreateProductionOrderRequest r, CancellationToken ct)
    {
        var plan = await PlanAsync(caller, r, ct);
        if (!plan.Ok) return plan.As<ProductionOrderPreviewDto>();
        var materials = await AvailabilityAsync(plan.Value!, ct);
        var units = await UnitCodesAsync(ct);
        return MfgResult<ProductionOrderPreviewDto>.Done(new ProductionOrderPreviewDto(
            plan.Value!.Bom.Id, plan.Value.Bom.Version, plan.Value.PlannedBaseQty,
            units.GetValueOrDefault(plan.Value.Product.BaseUnitId ?? Guid.Empty, ""), plan.Value.Batches,
            materials, materials.Any(m => m.ShortBaseQty > 0)));
    }

    /// <summary>
    /// Makes the order, reserving every material at the line's location. Short of free material it is
    /// refused, unless the caller may start short and says why (owner, 2026-10-26, decision 3) — the
    /// reservation then claims what is missing too, and nothing can be issued that is not in stock.
    /// A retried request (same <c>ClientRequestId</c>) answers the order it already made.
    /// </summary>
    public async Task<MfgResult<ProductionOrderDto>> CreateAsync(MfgCaller caller, CreateProductionOrderRequest r, CancellationToken ct)
    {
        if (!options.StockOperations) return Production.NotEnabled<ProductionOrderDto>();
        var requestId = MfgText.Clean(r.ClientRequestId);
        if (requestId is null || requestId.Length > 64) return MfgFail.Invalid<ProductionOrderDto>("request_id_required", "Each request carries its own id.");
        await using var scope = await db.BeginWriteScopeAsync(ct);
        await db.LockVersionsAsync(ct);
        // Read under the lock, so a copy of the request sent alongside the first answers the order the first made.
        if (await db.ProductionOrders.AsNoTracking().FirstOrDefaultAsync(o => o.ClientRequestId == requestId, ct) is { } done)
            return await GetAsync(caller, done.Id, ct);
        var plan = await PlanAsync(caller, r, ct);
        if (!plan.Ok) return plan.As<ProductionOrderDto>();
        var p = plan.Value!;

        var materials = await AvailabilityAsync(p, ct);
        var short_ = materials.Where(m => m.ShortBaseQty > 0).ToList();
        var reason = Production.Clean(r.ShortageReason, 400);
        if (short_.Count > 0)
        {
            if (!r.StartShort)
                return MfgFail.Conflict<ProductionOrderDto>("material_shortage",
                    "Not enough free material at the line: " + string.Join(", ", short_.Select(m => $"{m.Sku} short {m.ShortBaseQty:0.######} {m.BaseUnitCode}")) + ".");
            if (!caller.Has(PermissionCatalog.ManufacturingOrdersStartShort))
                return MfgFail.Forbidden<ProductionOrderDto>("Starting an order short of material needs its own permission.", "start_short_forbidden");
            if (reason is null) return MfgFail.Invalid<ProductionOrderDto>("reason_required", "Say why the order starts short of material.");
        }

        var stages = await db.BomStages.AsNoTracking().Where(s => s.BomId == p.Bom.Id).OrderBy(s => s.Sequence).ToListAsync(ct);
        var order = new ProductionOrder
        {
            Number = Production.Number("MO", await db.ProductionOrders.CountAsync(ct)),
            LineId = p.Line.Id, LocationId = p.Line.LocationId, ProductId = p.Product.Id, BomId = p.Bom.Id, BomVersion = p.Bom.Version,
            PlannedQty = r.PlannedQty, PlannedUnitId = r.PlannedUnitId, PlannedBaseQty = p.PlannedBaseQty, Batches = p.Batches,
            PlannedStart = r.PlannedStart, PlannedEnd = r.PlannedEnd, Status = ProductionOrderStatus.Open,
            StartedShort = short_.Count > 0, ShortageReason = short_.Count > 0 ? reason : null,
            Notes = Production.Clean(r.Notes, 1000), ClientRequestId = requestId,
            CreatedByUserId = caller.UserId, CreatedBy = caller.Name,
        };
        var sequence = 0;
        foreach (var (need, component, net, waste) in p.Lines)
        {
            order.Materials.Add(new ProductionOrderMaterial
            {
                Sequence = ++sequence, BomLineId = need.BomLineId, ProductId = component.Id, Role = component.ManufacturingRole,
                BaseUnitId = component.BaseUnitId ?? need.BaseUnitId, NetBaseQty = net, WasteBaseQty = waste, RequiredBaseQty = need.BaseQuantity,
                EstimatedUnitCost = component.CostPrice,
                Specification = await SpecificationAsync(need.BomLineId, ct),
            });
        }
        order.EstimatedMaterialCost = order.Materials.Sum(m => m.RequiredBaseQty * m.EstimatedUnitCost);
        sequence = 0;
        foreach (var s in stages)
            order.Stages.Add(new ProductionOrderStage
            {
                Sequence = ++sequence, NameAr = s.NameAr, NameFr = s.NameFr, NameEn = s.NameEn,
                PlannedMinutes = Production.Round(s.EstimatedMinutes * p.Batches), ExpectedWorkers = s.ExpectedWorkers,
                IsQualityCheckpoint = s.IsQualityCheckpoint, IsOutput = s.IsOutput,
            });
        // A recipe with no stages still has one place its finished units come off.
        if (order.Stages.Count == 0)
            order.Stages.Add(new ProductionOrderStage { Sequence = 1, NameAr = "الإنتاج", NameFr = "Production", NameEn = "Production", IsOutput = true });

        db.ProductionOrders.Add(order);
        Event(order.Id, ProductionOrderEventKind.Created, $"{p.Bom.Version}", caller);
        if (order.StartedShort) Event(order.Id, ProductionOrderEventKind.StartedShort, reason, caller);
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
        return await GetAsync(caller, order.Id, ct);
    }

    /// <summary>The BOM line's free text and figures, as one readable line kept with the order.</summary>
    private async Task<string?> SpecificationAsync(Guid bomLineId, CancellationToken ct)
    {
        var line = await db.BomLines.AsNoTracking().Include(l => l.Specs).FirstOrDefaultAsync(l => l.Id == bomLineId, ct);
        if (line is null) return null;
        var names = await SpecValues.NamesAsync(db, ct);
        var figures = SpecValues.Dtos(line.Specs, names)
            .Select(s => s.Kind == nameof(SpecKind.Text) ? $"{s.AttributeCode} {s.Text}" : $"{s.AttributeCode} {s.Value:0.######} {s.UnitCode}");
        var text = string.Join(" · ", new[] { line.Specification }.Concat(figures).Where(t => !string.IsNullOrWhiteSpace(t)));
        return Production.Clean(text, 600);
    }

    private void Event(Guid orderId, ProductionOrderEventKind kind, string? detail, MfgCaller caller) =>
        db.ProductionOrderEvents.Add(new ProductionOrderEvent
        {
            OrderId = orderId, Kind = kind, Detail = Production.Clean(detail, 400), UserId = caller.UserId, UserName = caller.Name,
        });

    // ---------- transitions ----------

    /// <summary>
    /// Opens a write: the switch, the lock, the order read under it and in the caller's reach. The caller
    /// commits. Null order means not found (or out of reach — answered the same, 404).
    /// </summary>
    private async Task<(PosDbContext.WriteScope? Scope, ProductionOrder? Order, MfgResult<ProductionOrderDto>? Refusal)>
        OpenAsync(MfgCaller caller, Guid id, CancellationToken ct)
    {
        if (!options.StockOperations) return (null, null, Production.NotEnabled<ProductionOrderDto>());
        var scope = await db.BeginWriteScopeAsync(ct);
        await db.LockVersionsAsync(ct);
        var order = await db.ProductionOrders.Include(o => o.Materials).Include(o => o.Stages).AsSplitQuery()
            .FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null || !await ManufacturingAccess.ReachesAsync(db, caller, order.LineId, ct))
        {
            await scope.DisposeAsync();
            return (null, null, MfgFail.NotFound<ProductionOrderDto>("Production order"));
        }
        return (scope, order, null);
    }

    private async Task<MfgResult<ProductionOrderDto>> SaveAsync(
        PosDbContext.WriteScope scope, MfgCaller caller, ProductionOrder order, CancellationToken ct)
    {
        order.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
        await scope.DisposeAsync();
        return await GetAsync(caller, order.Id, ct);
    }

    private static async Task<MfgResult<ProductionOrderDto>> Refuse(
        PosDbContext.WriteScope scope, MfgResult<ProductionOrderDto> refusal)
    {
        await scope.DisposeAsync();
        return refusal;
    }

    public async Task<MfgResult<ProductionOrderDto>> HoldAsync(MfgCaller caller, Guid id, OrderReasonRequest r, CancellationToken ct)
    {
        var (scope, order, refusal) = await OpenAsync(caller, id, ct);
        if (refusal is not null) return refusal;
        if (order!.Status != ProductionOrderStatus.Open)
            return await Refuse(scope!, MfgFail.Conflict<ProductionOrderDto>("not_open", "Only an open order is put on hold."));
        if (Production.Clean(r.Reason, 400) is not string reason)
            return await Refuse(scope!, MfgFail.Invalid<ProductionOrderDto>("reason_required", "Say why the order is held."));
        order.Status = ProductionOrderStatus.OnHold;
        Event(order.Id, ProductionOrderEventKind.Held, reason, caller);
        return await SaveAsync(scope!, caller, order, ct);
    }

    public async Task<MfgResult<ProductionOrderDto>> ResumeAsync(MfgCaller caller, Guid id, CancellationToken ct)
    {
        var (scope, order, refusal) = await OpenAsync(caller, id, ct);
        if (refusal is not null) return refusal;
        if (order!.Status != ProductionOrderStatus.OnHold)
            return await Refuse(scope!, MfgFail.Conflict<ProductionOrderDto>("not_on_hold", "Only a held order is resumed."));
        order.Status = ProductionOrderStatus.Open;
        Event(order.Id, ProductionOrderEventKind.Resumed, null, caller);
        return await SaveAsync(scope!, caller, order, ct);
    }

    /// <summary>Cancelled only with nothing produced, every issue returned or accounted for, and no record waiting.</summary>
    public async Task<MfgResult<ProductionOrderDto>> CancelAsync(MfgCaller caller, Guid id, OrderReasonRequest r, CancellationToken ct)
    {
        var (scope, order, refusal) = await OpenAsync(caller, id, ct);
        if (refusal is not null) return refusal;
        if (!Production.Live.Contains(order!.Status))
            return await Refuse(scope!, MfgFail.Conflict<ProductionOrderDto>("not_live", "This order is already closed."));
        if (Production.Clean(r.Reason, 400) is not string reason)
            return await Refuse(scope!, MfgFail.Invalid<ProductionOrderDto>("reason_required", "Say why the order is cancelled."));
        if (order.AcceptedBaseQty > 0 || order.Stages.Any(s => s.GoodBaseQty > 0))
            return await Refuse(scope!, MfgFail.Conflict<ProductionOrderDto>("has_output", "Output has been recorded; the order is completed, not cancelled."));
        if (order.Materials.Any(m => Production.AtLine(m) != 0))
            return await Refuse(scope!, MfgFail.Conflict<ProductionOrderDto>("materials_at_line", "Return or account for every issued material first."));
        if (await PendingRecordsAsync(order.Id, ct))
            return await Refuse(scope!, MfgFail.Conflict<ProductionOrderDto>("records_pending", "A daily record naming this order is not approved yet."));
        order.Status = ProductionOrderStatus.Cancelled;
        Event(order.Id, ProductionOrderEventKind.Cancelled, reason, caller);
        return await SaveAsync(scope!, caller, order, ct);
    }

    private Task<bool> PendingRecordsAsync(Guid orderId, CancellationToken ct) =>
        db.ProductionLogRows.AnyAsync(r => r.OrderId == orderId && r.Log!.Status != ProductionLogStatus.Approved, ct);

    /// <summary>Frees part of a material's reservation for another order; never below what has left the stock for it.</summary>
    public async Task<MfgResult<ProductionOrderDto>> ReleaseReservationAsync(
        MfgCaller caller, Guid id, Guid materialId, ReleaseReservationRequest r, CancellationToken ct)
    {
        var (scope, order, refusal) = await OpenAsync(caller, id, ct);
        if (refusal is not null) return refusal;
        if (!await ManufacturingAccess.ManagesAsync(db, caller, order!.LineId, ct))
            return await Refuse(scope!, MfgFail.Forbidden<ProductionOrderDto>("Releasing a reservation is for a production manager of the line."));
        var material = order.Materials.FirstOrDefault(m => m.Id == materialId);
        if (material is null) return await Refuse(scope!, MfgFail.NotFound<ProductionOrderDto>("Material"));
        if (Production.Clean(r.Reason, 400) is not string reason)
            return await Refuse(scope!, MfgFail.Invalid<ProductionOrderDto>("reason_required", "Say why the reservation is released."));
        var open = Production.Reserved(material, order.Status);
        if (r.BaseQty <= 0 || r.BaseQty > open)
            return await Refuse(scope!, MfgFail.Invalid<ProductionOrderDto>("invalid_quantity", $"Up to {open:0.######} can be released."));
        material.ReservationReleasedBaseQty += r.BaseQty;
        Event(order.Id, ProductionOrderEventKind.ReservationReleased, $"{r.BaseQty:0.######} — {reason}", caller);
        return await SaveAsync(scope!, caller, order, ct);
    }

    /// <summary>An expense of the order, kept for the cost sheet. Posts nothing; a retried request adds nothing.</summary>
    public async Task<MfgResult<ProductionOrderDto>> AddExpenseAsync(MfgCaller caller, Guid id, OrderExpenseRequest r, CancellationToken ct)
    {
        var requestId = MfgText.Clean(r.ClientRequestId);
        if (requestId is null || requestId.Length > 64) return MfgFail.Invalid<ProductionOrderDto>("request_id_required", "Each request carries its own id.");
        var (scope, order, refusal) = await OpenAsync(caller, id, ct);
        if (refusal is not null) return refusal;
        // Read under the lock, once the order is reached: a copy sent alongside the first adds nothing either.
        if (await db.ProductionOrderExpenses.AsNoTracking().AnyAsync(e => e.ClientRequestId == requestId, ct))
        {
            await scope!.DisposeAsync();
            return await GetAsync(caller, id, ct);
        }
        if (order!.Status is ProductionOrderStatus.Cancelled)
            return await Refuse(scope!, MfgFail.Conflict<ProductionOrderDto>("not_live", "This order is cancelled."));
        if (Production.Clean(r.Description, 300) is not string description)
            return await Refuse(scope!, MfgFail.Invalid<ProductionOrderDto>("invalid_input", "Describe the expense."));
        if (r.Amount <= 0 || r.Amount > Production.MaxQuantity)
            return await Refuse(scope!, MfgFail.Invalid<ProductionOrderDto>("invalid_amount", "The amount must be above zero."));
        var amount = regional.Get().RoundMoney(r.Amount);
        if (r.PaymentVoucherId is Guid voucherId)
        {
            // Another branch's voucher is not found for a caller held to their own branch, as on the voucher routes.
            var voucher = await db.PaymentVouchers.AsNoTracking()
                .Where(v => v.Id == voucherId && (caller.CompanyScoped || v.BranchId == caller.BranchId))
                .Select(v => new { v.Amount, v.Kind, v.Status }).FirstOrDefaultAsync(ct);
            if (voucher is null) return await Refuse(scope!, MfgFail.NotFound<ProductionOrderDto>("Payment voucher"));
            // A production expense can only allocate an expense voucher. A cancellation keeps the
            // historical link (so Phase A can say that cost is provisional), but cannot fund a new one.
            if (voucher.Kind != PaymentVoucherKind.Expense)
                return await Refuse(scope!, MfgFail.Conflict<ProductionOrderDto>("voucher_not_expense",
                    "Only an expense payment voucher can fund a production expense."));
            if (voucher.Status == PaymentVoucherStatus.Cancelled)
                return await Refuse(scope!, MfgFail.Conflict<ProductionOrderDto>("voucher_cancelled",
                    "A cancelled payment voucher cannot fund a new production expense."));
            // Costing plan F9, step A0, one rule with maintenance (plan §2.4): the expenses linked to one voucher, across
            // every order and every maintenance job, never exceed its amount. Read under the write lock, so an order and
            // a job cannot both take its last part.
            var linked = await Receivables.VoucherExpenseLinks.LinkedAsync(db, voucherId, ct);
            if (linked + amount > voucher.Amount)
                return await Refuse(scope!, MfgFail.Conflict<ProductionOrderDto>("voucher_amount_exceeded",
                    $"The voucher's amount is {voucher.Amount:0.###} and {linked:0.###} of it is already linked to orders' and maintenance jobs' expenses."));
        }
        db.ProductionOrderExpenses.Add(new ProductionOrderExpense
        {
            OrderId = order.Id, Description = description, Amount = amount, PaymentVoucherId = r.PaymentVoucherId,
            ClientRequestId = requestId, UserId = caller.UserId, UserName = caller.Name,
        });
        Event(order.Id, ProductionOrderEventKind.ExpenseRecorded, description, caller);
        return await SaveAsync(scope!, caller, order, ct);
    }

    // ---------- completion ----------

    /// <summary>
    /// The line asks to complete: every record naming the order approved, every material counted to zero
    /// at the line (returned, consumed or scrapped), all output released.
    /// </summary>
    public async Task<MfgResult<ProductionOrderDto>> RequestCompletionAsync(MfgCaller caller, Guid id, CancellationToken ct)
    {
        var (scope, order, refusal) = await OpenAsync(caller, id, ct);
        if (refusal is not null) return refusal;
        if (order!.Status != ProductionOrderStatus.Open)
            return await Refuse(scope!, MfgFail.Conflict<ProductionOrderDto>("not_open", "Only an open order asks for completion."));
        if (await PendingRecordsAsync(order.Id, ct))
            return await Refuse(scope!, MfgFail.Conflict<ProductionOrderDto>("records_pending", "A daily record naming this order is not approved yet."));
        if (order.Materials.FirstOrDefault(m => Production.AtLine(m) != 0) is { } open)
            return await Refuse(scope!, MfgFail.Conflict<ProductionOrderDto>("materials_at_line",
                "Count what is left of each issued material and return it or record it as consumed first."));
        var produced = order.Stages.FirstOrDefault(s => s.IsOutput)?.GoodBaseQty ?? 0m;
        if (produced > order.AcceptedBaseQty)
            return await Refuse(scope!, MfgFail.Conflict<ProductionOrderDto>("output_awaiting_release", "Release the approved output to stock first."));
        if (order.AcceptedBaseQty <= 0)
            return await Refuse(scope!, MfgFail.Conflict<ProductionOrderDto>("nothing_produced", "Nothing was produced; cancel the order instead."));
        order.Status = ProductionOrderStatus.CompletionRequested;
        order.CompletionRequestedByUserId = caller.UserId;
        order.CompletionRequestedAtUtc = DateTime.UtcNow;
        Event(order.Id, ProductionOrderEventKind.CompletionRequested, null, caller);
        return await SaveAsync(scope!, caller, order, ct);
    }

    /// <summary>A production manager of the line, never the one who asked, approves; the date never does.</summary>
    public async Task<MfgResult<ProductionOrderDto>> ApproveCompletionAsync(MfgCaller caller, Guid id, CancellationToken ct)
    {
        var (scope, order, refusal) = await OpenAsync(caller, id, ct);
        if (refusal is not null) return refusal;
        if (order!.Status != ProductionOrderStatus.CompletionRequested)
            return await Refuse(scope!, MfgFail.Conflict<ProductionOrderDto>("not_requested", "Completion has not been requested."));
        if (!await ManufacturingAccess.ManagesAsync(db, caller, order.LineId, ct))
            return await Refuse(scope!, MfgFail.Forbidden<ProductionOrderDto>("Approving completion is for a production manager of the line."));
        if (order.CompletionRequestedByUserId == caller.UserId)
            return await Refuse(scope!, MfgFail.Forbidden<ProductionOrderDto>("The person who asked for completion cannot approve it.", "own_request"));
        order.Status = ProductionOrderStatus.Completed;
        order.CompletedByUserId = caller.UserId;
        order.CompletedBy = caller.Name;
        order.CompletedAtUtc = DateTime.UtcNow;
        order.ActualEnd = Production.Today(regional, time);
        Event(order.Id, ProductionOrderEventKind.CompletionApproved, null, caller);
        return await SaveAsync(scope!, caller, order, ct);
    }

    public async Task<MfgResult<ProductionOrderDto>> RefuseCompletionAsync(MfgCaller caller, Guid id, OrderReasonRequest r, CancellationToken ct)
    {
        var (scope, order, refusal) = await OpenAsync(caller, id, ct);
        if (refusal is not null) return refusal;
        if (order!.Status != ProductionOrderStatus.CompletionRequested)
            return await Refuse(scope!, MfgFail.Conflict<ProductionOrderDto>("not_requested", "Completion has not been requested."));
        if (!await ManufacturingAccess.ManagesAsync(db, caller, order.LineId, ct))
            return await Refuse(scope!, MfgFail.Forbidden<ProductionOrderDto>("Refusing completion is for a production manager of the line."));
        if (Production.Clean(r.Reason, 400) is not string reason)
            return await Refuse(scope!, MfgFail.Invalid<ProductionOrderDto>("reason_required", "Say why completion is refused."));
        order.Status = ProductionOrderStatus.Open;
        order.CompletionRequestedByUserId = null;
        order.CompletionRequestedAtUtc = null;
        Event(order.Id, ProductionOrderEventKind.CompletionRefused, reason, caller);
        return await SaveAsync(scope!, caller, order, ct);
    }

    // ---------- costing inputs ----------

    /// <summary>
    /// Everything the costing will need, recorded and nothing valued (owner, 2026-10-26, decision 7): the
    /// estimate frozen at creation, each material's movements at their snapshots, the hours per employee and
    /// stage with the payroll they belong to (the run, once there is one — never its rate or wage), the
    /// expenses and the accepted quantity.
    /// </summary>
    public async Task<MfgResult<ProductionCostInputsDto>> CostInputsAsync(MfgCaller caller, Guid id, CancellationToken ct)
    {
        var order = await db.ProductionOrders.AsNoTracking().Include(o => o.Materials).ThenInclude(m => m.Product)
            .AsSplitQuery().FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null || !await ManufacturingAccess.ReachesAsync(db, caller, order.LineId, ct))
            return MfgFail.NotFound<ProductionCostInputsDto>("Production order");
        var units = await UnitCodesAsync(ct);
        var lines = await db.MaterialMovementLines.AsNoTracking().Where(l => l.Movement!.OrderId == id)
            .Select(l => new { l.OrderMaterialId, l.Movement!.Kind, l.BaseQty, l.UnitCostSnapshot }).ToListAsync(ct);
        decimal Valued(Guid material, MaterialMovementKind kind) =>
            lines.Where(l => l.OrderMaterialId == material && l.Kind == kind).Sum(l => l.BaseQty * l.UnitCostSnapshot);

        var materials = order.Materials.OrderBy(m => m.Sequence).Select(m => new CostMaterialDto(
            m.Id, m.Product?.Sku ?? "", m.Role.ToString(), units.GetValueOrDefault(m.BaseUnitId, ""),
            m.RequiredBaseQty, m.EstimatedUnitCost, m.RequiredBaseQty * m.EstimatedUnitCost,
            m.IssuedBaseQty, m.ReturnedBaseQty, m.ConsumedBaseQty, m.ScrappedBaseQty,
            m.IssuedBaseQty - m.ReturnedBaseQty,
            Valued(m.Id, MaterialMovementKind.Issue) - Valued(m.Id, MaterialMovementKind.Return))).ToList();

        var bookings = await db.LaborBookings.AsNoTracking().Include(b => b.Employee)
            .Where(b => b.OrderId == id && b.OrderKind == LaborOrderKind.Production).ToListAsync(ct);
        var branches = bookings.Select(b => b.PayrollBranchId).Distinct().ToList();
        var runs = await db.PayrollRuns.AsNoTracking()
            .Where(x => branches.Contains(x.BranchId) && x.Status != PayrollRunStatus.Reversed)
            .Select(x => new { x.Id, x.BranchId, x.PeriodYear, x.PeriodMonth, x.Number, x.Status, x.CreatedAtUtc }).ToListAsync(ct);
        var labour = bookings.GroupBy(b => (b.EmployeeId, b.StageId, b.PayrollBranchId, b.PayrollYear, b.PayrollMonth)).Select(g =>
        {
            var run = runs.Where(x => x.BranchId == g.Key.PayrollBranchId && x.PeriodYear == g.Key.PayrollYear && x.PeriodMonth == g.Key.PayrollMonth)
                .OrderByDescending(x => x.CreatedAtUtc).FirstOrDefault();
            var employee = g.First().Employee;
            return new CostLabourDto(g.Key.EmployeeId, employee?.Number ?? "", employee?.FullName ?? "", g.Key.StageId, g.Sum(b => b.Hours),
                g.Key.PayrollBranchId, g.Key.PayrollYear, g.Key.PayrollMonth, run?.Id, run?.Number, run?.Status.ToString());
        }).ToList();
        var expenses = await db.ProductionOrderExpenses.AsNoTracking().Where(e => e.OrderId == id).OrderBy(e => e.AtUtc).ToListAsync(ct);

        return MfgResult<ProductionCostInputsDto>.Done(new ProductionCostInputsDto(
            order.Id, order.Number, order.PlannedBaseQty, order.AcceptedBaseQty, order.RejectedBaseQty, order.EstimatedMaterialCost,
            materials, labour, labour.Sum(l => l.Hours), expenses.Select(ToDto).ToList(), expenses.Sum(e => e.Amount)));
    }
}
