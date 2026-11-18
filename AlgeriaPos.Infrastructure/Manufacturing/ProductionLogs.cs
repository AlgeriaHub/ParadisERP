using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Hr;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Infrastructure.Manufacturing;

/// <summary>
/// The daily production record (Phase 2 §2C, §2D; owner, 2026-10-26, decisions 2 and 4): one per line per
/// day, its rows the day's operations — a stage of an order and the quantity processed at it, or non-order
/// time — with the employees who worked each and their hours.
///
/// <list type="bullet">
/// <item>Only the output stage's completed quantity becomes finished output; the other stages are progress
/// on the same units, and a stage ahead of the one before it is said, not refused (amendment 5).</item>
/// <item>The same order and stage once per record, the same person once per row; a person's hours that day,
/// across every line and order kind, never above what their attendance shows — unless someone allowed to
/// override says why, and that is kept.</item>
/// <item>Hours are booked only while the record is a draft, never in a payroll month a run holds closed;
/// nothing in payroll or attendance is written.</item>
/// <item>A production manager of the line approves — never the person who submitted it — and an approved
/// record is frozen; until then a manager may send it back with a reason.</item>
/// </list>
/// </summary>
public sealed class ProductionLogs(
    PosDbContext db, ManufacturingOptions options, AttendanceCapacity attendance, AttendanceSheet sheet,
    IRegionalSettingsProvider regional, TimeProvider time)
{
    private const int MaxRows = 200;

    // ---------- reading ----------

    public async Task<List<ProductionLogSummaryDto>> ListAsync(MfgCaller caller, Guid? lineId, DateOnly? from, DateOnly? to, string? status, CancellationToken ct)
    {
        var reachable = ManufacturingAccess.Reachable(db, caller).Select(l => l.Id);
        var query = db.ProductionLogs.AsNoTracking().Where(l => reachable.Contains(l.LineId));
        if (lineId is Guid line) query = query.Where(l => l.LineId == line);
        if (from is DateOnly f) query = query.Where(l => l.Day >= f);
        if (to is DateOnly t) query = query.Where(l => l.Day <= t);
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<ProductionLogStatus>(status, true, out var s)) return [];
            query = query.Where(l => l.Status == s);
        }
        var logs = await query.OrderByDescending(l => l.Day).Take(366).ToListAsync(ct);
        return await SummariesAsync(logs, ct);
    }

    private async Task<List<ProductionLogSummaryDto>> SummariesAsync(List<ProductionLog> logs, CancellationToken ct)
    {
        var ids = logs.Select(l => l.Id).ToList();
        var lineIds = logs.Select(l => l.LineId).Distinct().ToList();
        var codes = await db.ProductionLines.AsNoTracking().Where(l => lineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Code, ct);
        var rows = (await db.ProductionLogRows.AsNoTracking().Where(r => ids.Contains(r.LogId)).Select(r => new { r.Id, r.LogId }).ToListAsync(ct));
        var rowIds = rows.Select(r => r.Id).ToList();
        var hours = (await db.LaborBookings.AsNoTracking().Where(b => b.LogRowId != null && rowIds.Contains(b.LogRowId.Value))
                .Select(b => new { LogRowId = b.LogRowId!.Value, b.Hours }).ToListAsync(ct))
            .Join(rows, b => b.LogRowId, r => r.Id, (b, r) => new { r.LogId, b.Hours })
            .GroupBy(x => x.LogId).ToDictionary(g => g.Key, g => g.Sum(x => x.Hours));
        return logs.Select(l => new ProductionLogSummaryDto(
            l.Id, l.Number, l.LineId, codes.GetValueOrDefault(l.LineId, ""), l.Day, l.Status.ToString(),
            rows.Count(r => r.LogId == l.Id), hours.GetValueOrDefault(l.Id), l.SubmittedBy, l.ApprovedBy)).ToList();
    }

    public async Task<MfgResult<ProductionLogDto>> GetAsync(MfgCaller caller, Guid id, CancellationToken ct)
    {
        var log = await db.ProductionLogs.AsNoTracking().Include(l => l.Rows).FirstOrDefaultAsync(l => l.Id == id, ct);
        if (log is null || !await ManufacturingAccess.ReachesAsync(db, caller, log.LineId, ct))
            return MfgFail.NotFound<ProductionLogDto>("Daily production record");

        var orderIds = log.Rows.Where(r => r.OrderId != null).Select(r => r.OrderId!.Value).Distinct().ToList();
        var orders = await db.ProductionOrders.AsNoTracking().Include(o => o.Stages).Include(o => o.Product)
            .Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        var units = await db.UnitsOfMeasure.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Code, ct);
        var rowIds = log.Rows.Select(r => r.Id).ToList();
        var bookings = await db.LaborBookings.AsNoTracking().Include(b => b.Employee)
            .Where(b => b.LogRowId != null && rowIds.Contains(b.LogRowId.Value)).ToListAsync(ct);
        var consumptions = await db.MaterialConsumptions.AsNoTracking().Where(c => c.LogRowId != null && rowIds.Contains(c.LogRowId.Value)).ToListAsync(ct);
        var materialIds = consumptions.Select(c => c.OrderMaterialId).Distinct().ToList();
        // Projected in the query: an untracked material has no Product loaded to read the SKU from.
        var skus = await db.ProductionOrderMaterials.AsNoTracking().Where(m => materialIds.Contains(m.Id))
            .Select(m => new { m.Id, m.Product!.Sku }).ToDictionaryAsync(m => m.Id, m => m.Sku, ct);

        var rows = log.Rows.OrderBy(r => r.Sequence).Select(r =>
        {
            var order = r.OrderId is Guid o ? orders.GetValueOrDefault(o) : null;
            var stage = order?.Stages.FirstOrDefault(s => s.Id == r.StageId);
            return new ProductionLogRowDto(
                r.Id, r.Sequence, r.OrderId, order?.Number, r.StageId, stage?.NameAr, stage?.NameFr, stage?.NameEn, stage?.IsOutput ?? false,
                r.Category?.ToString(), r.ProductId, order?.Product?.Sku,
                order?.Product?.BaseUnitId is Guid b ? units.GetValueOrDefault(b) : null,
                r.Specification, r.PlannedQty, r.CompletedQty, r.RejectedQty, r.RejectReason, r.Notes,
                bookings.Where(x => x.LogRowId == r.Id).OrderBy(x => x.Employee?.FullName).Select(x => new LaborBookingDto(
                    x.Id, x.EmployeeId, x.Employee?.Number ?? "", x.Employee?.FullName ?? "", x.Hours, x.AttendanceHours,
                    x.OverrideReason, x.OverriddenBy, x.UserName, x.AtUtc, x.OvertimeHours)).ToList(),
                consumptions.Where(c => c.LogRowId == r.Id).Select(c => new ConsumptionDto(
                    c.Id, c.OrderMaterialId, skus.GetValueOrDefault(c.OrderMaterialId, ""), c.LogRowId, c.BaseQty, c.Reason.ToString(),
                    c.Note, c.UserName, c.AtUtc)).ToList());
        }).ToList();

        var summary = (await SummariesAsync([log], ct))[0];
        var manages = await ManufacturingAccess.ManagesAsync(db, caller, log.LineId, ct);
        return MfgResult<ProductionLogDto>.Done(new ProductionLogDto(
            summary, log.Notes, log.ReturnedReason, log.CreatedBy, log.SubmittedAtUtc, log.ApprovedAtUtc, rows,
            Warnings(log, orders.Values),
            CanEdit: log.Status == ProductionLogStatus.Draft && caller.Has(PermissionCatalog.ManufacturingLogSubmit),
            CanApprove: log.Status == ProductionLogStatus.Submitted && manages && caller.Has(PermissionCatalog.ManufacturingLogApprove)
                && log.SubmittedByUserId != caller.UserId));
    }

    /// <summary>
    /// Each stage whose good quantity — approved so far, plus this record's while it is not approved yet —
    /// is above the previous stage's: more units through a later stage than came through the one before.
    /// </summary>
    private static List<ProductionLogWarningDto> Warnings(ProductionLog log, IEnumerable<ProductionOrder> orders)
    {
        var warnings = new List<ProductionLogWarningDto>();
        foreach (var order in orders)
        {
            var pending = log.Status == ProductionLogStatus.Approved
                ? new Dictionary<Guid, decimal>()
                : log.Rows.Where(r => r.OrderId == order.Id && r.StageId != null)
                    .GroupBy(r => r.StageId!.Value).ToDictionary(g => g.Key, g => g.Sum(r => r.CompletedQty));
            var stages = order.Stages.OrderBy(s => s.Sequence).ToList();
            for (var i = 1; i < stages.Count; i++)
            {
                var good = stages[i].GoodBaseQty + pending.GetValueOrDefault(stages[i].Id);
                var before = stages[i - 1].GoodBaseQty + pending.GetValueOrDefault(stages[i - 1].Id);
                if (good > before && log.Rows.Any(r => r.StageId == stages[i].Id || r.StageId == stages[i - 1].Id))
                    warnings.Add(new ProductionLogWarningDto("stage_ahead_of_previous", order.Number,
                        stages[i].NameAr, stages[i].NameFr, stages[i].NameEn, stages[i - 1].NameAr, stages[i - 1].NameFr, stages[i - 1].NameEn,
                        good, before));
            }
        }
        return warnings;
    }

    // ---------- opening and writing ----------

    /// <summary>The line's record for the day: the one that exists, or a new draft. One per line per day.</summary>
    public async Task<MfgResult<ProductionLogDto>> OpenAsync(MfgCaller caller, OpenProductionLogRequest r, CancellationToken ct)
    {
        if (!options.StockOperations) return Production.NotEnabled<ProductionLogDto>();
        if (!await ManufacturingAccess.ReachesAsync(db, caller, r.LineId, ct)) return MfgFail.NotFound<ProductionLogDto>("Production line");
        if (r.Day > Production.Today(regional, time))
            return MfgFail.Invalid<ProductionLogDto>("future_day", "A day's record is kept on or after that day.");
        if (await db.ProductionLogs.AsNoTracking().FirstOrDefaultAsync(l => l.LineId == r.LineId && l.Day == r.Day, ct) is { } existing)
            return await GetAsync(caller, existing.Id, ct);

        await using var scope = await db.BeginWriteScopeAsync(ct);
        await db.LockVersionsAsync(ct);
        if (await db.ProductionLogs.AsNoTracking().FirstOrDefaultAsync(l => l.LineId == r.LineId && l.Day == r.Day, ct) is { } raced)
            return await GetAsync(caller, raced.Id, ct);
        var log = new ProductionLog
        {
            Number = Production.Number("PL", await db.ProductionLogs.CountAsync(ct)),
            LineId = r.LineId, Day = r.Day, Status = ProductionLogStatus.Draft, CreatedByUserId = caller.UserId, CreatedBy = caller.Name,
        };
        db.ProductionLogs.Add(log);
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
        return await GetAsync(caller, log.Id, ct);
    }

    private async Task<(PosDbContext.WriteScope? Scope, ProductionLog? Log, MfgResult<ProductionLogDto>? Refusal)>
        WriteAsync(MfgCaller caller, Guid id, CancellationToken ct)
    {
        if (!options.StockOperations) return (null, null, Production.NotEnabled<ProductionLogDto>());
        var scope = await db.BeginWriteScopeAsync(ct);
        await db.LockVersionsAsync(ct);
        var log = await db.ProductionLogs.Include(l => l.Rows).FirstOrDefaultAsync(l => l.Id == id, ct);
        if (log is null || !await ManufacturingAccess.ReachesAsync(db, caller, log.LineId, ct))
        {
            await scope.DisposeAsync();
            return (null, null, MfgFail.NotFound<ProductionLogDto>("Daily production record"));
        }
        return (scope, log, null);
    }

    private async Task<MfgResult<ProductionLogDto>> CommitAsync(
        PosDbContext.WriteScope scope, MfgCaller caller, ProductionLog log, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
        await scope.DisposeAsync();
        return await GetAsync(caller, log.Id, ct);
    }

    private static async Task<MfgResult<ProductionLogDto>> Refuse(
        PosDbContext.WriteScope scope, MfgResult<ProductionLogDto> refusal)
    {
        await scope.DisposeAsync();
        return refusal;
    }

    /// <summary>
    /// A draft, sent whole. A row that holds hours or consumption keeps its order and stage and cannot be
    /// removed — take those off first — so hours never point at an operation that is no longer there.
    /// </summary>
    public async Task<MfgResult<ProductionLogDto>> SaveAsync(MfgCaller caller, Guid id, SaveProductionLogRequest r, CancellationToken ct)
    {
        var (scope, log, refusal) = await WriteAsync(caller, id, ct);
        if (refusal is not null) return refusal;
        if (log!.Status != ProductionLogStatus.Draft)
            return await Refuse(scope!, MfgFail.Conflict<ProductionLogDto>("log_not_draft", "Only a draft record is edited."));
        var wanted = r.Rows ?? [];
        if (wanted.Count > MaxRows) return await Refuse(scope!, MfgFail.Invalid<ProductionLogDto>("too_many_rows", $"Up to {MaxRows} rows."));
        if (MfgText.Optional(r.Notes, 1000, "The notes") is string notesProblem)
            return await Refuse(scope!, MfgFail.Invalid<ProductionLogDto>("invalid_input", notesProblem));

        var rowIds = log.Rows.Select(x => x.Id).ToList();
        var used = (await db.LaborBookings.AsNoTracking().Where(b => b.LogRowId != null && rowIds.Contains(b.LogRowId.Value))
                .Select(b => b.LogRowId!.Value).ToListAsync(ct))
            .Concat(await db.MaterialConsumptions.AsNoTracking().Where(c => c.LogRowId != null && rowIds.Contains(c.LogRowId.Value))
                .Select(c => c.LogRowId!.Value).ToListAsync(ct))
            .ToHashSet();
        var orderIds = wanted.Where(w => w.OrderId != null).Select(w => w.OrderId!.Value).Distinct().ToList();
        var orders = await db.ProductionOrders.AsNoTracking().Include(o => o.Stages).Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);

        // Every row checked before anything changes.
        var seen = new HashSet<(Guid, Guid)>();
        var number = 0;
        foreach (var w in wanted)
        {
            number++;
            var existing = w.Id is Guid rowId ? log.Rows.FirstOrDefault(x => x.Id == rowId) : null;
            if (w.Id is not null && existing is null) return await Refuse(scope!, MfgFail.NotFound<ProductionLogDto>("Row"));
            if (w.OrderId is Guid orderId)
            {
                if (!orders.TryGetValue(orderId, out var order) || order.LineId != log.LineId)
                    return await Refuse(scope!, MfgFail.Invalid<ProductionLogDto>("order_not_on_line", $"Row {number}: the order is not one of this line's."));
                if (order.Status != ProductionOrderStatus.Open)
                    return await Refuse(scope!, MfgFail.Conflict<ProductionLogDto>("order_not_open", $"Row {number}: {order.Number} is not open."));
                if (w.StageId is not Guid stageId || order.Stages.All(s => s.Id != stageId))
                    return await Refuse(scope!, MfgFail.Invalid<ProductionLogDto>("stage_required", $"Row {number}: choose the stage of {order.Number}."));
                if (!seen.Add((orderId, stageId)))
                    return await Refuse(scope!, MfgFail.Invalid<ProductionLogDto>("duplicate_operation",
                        $"Row {number}: {order.Number} at that stage is already a row; one row per operation, so nothing is counted twice."));
            }
            else if (w.Category is null || !Enum.TryParse<NonOrderCategory>(w.Category, true, out _))
                return await Refuse(scope!, MfgFail.Invalid<ProductionLogDto>("order_or_category", $"Row {number}: name an order and its stage, or the kind of non-order time."));
            if (new[] { w.PlannedQty, w.CompletedQty, w.RejectedQty }.Any(q => q < 0 || q > Production.MaxQuantity))
                return await Refuse(scope!, MfgFail.Invalid<ProductionLogDto>("invalid_quantity", $"Row {number}: quantities are zero or more."));
            if (w.OrderId is null && w.CompletedQty + w.RejectedQty > 0)
                return await Refuse(scope!, MfgFail.Invalid<ProductionLogDto>("quantity_without_order", $"Row {number}: non-order time produces nothing."));
            if (w.RejectedQty > 0 && MfgText.Clean(w.RejectReason) is null)
                return await Refuse(scope!, MfgFail.Invalid<ProductionLogDto>("reject_reason_required", $"Row {number}: say why the rejects were rejected."));
            var text = MfgText.First(MfgText.Optional(w.Specification, 600, $"Row {number}: the specification"),
                MfgText.Optional(w.RejectReason, 300, $"Row {number}: the reject reason"), MfgText.Optional(w.Notes, 400, $"Row {number}: the notes"));
            if (text is not null) return await Refuse(scope!, MfgFail.Invalid<ProductionLogDto>("invalid_input", text));
            if (existing is not null && used.Contains(existing.Id) && (existing.OrderId != w.OrderId || existing.StageId != w.StageId))
                return await Refuse(scope!, MfgFail.Conflict<ProductionLogDto>("row_in_use",
                    $"Row {number} holds hours or consumption; its operation stays. Remove those first."));
        }
        var keep = wanted.Where(w => w.Id != null).Select(w => w.Id!.Value).ToHashSet();
        if (log.Rows.FirstOrDefault(x => !keep.Contains(x.Id) && used.Contains(x.Id)) is not null)
            return await Refuse(scope!, MfgFail.Conflict<ProductionLogDto>("row_in_use", "A removed row holds hours or consumption. Remove those first."));

        // The product's measured specification, as text, for a row that does not say its own.
        var productIds = orders.Values.Select(o => o.ProductId).Distinct().ToList();
        var specs = await ProductSpecsAsync(productIds, ct);

        foreach (var gone in log.Rows.Where(x => !keep.Contains(x.Id)).ToList())
        {
            log.Rows.Remove(gone);
            db.ProductionLogRows.Remove(gone);
        }
        var sequence = 0;
        foreach (var w in wanted)
        {
            var row = w.Id is Guid rowId ? log.Rows.First(x => x.Id == rowId) : null;
            if (row is null)
            {
                row = new ProductionLogRow { LogId = log.Id };
                db.ProductionLogRows.Add(row);
            }
            var order = w.OrderId is Guid o ? orders[o] : null;
            row.Sequence = ++sequence;
            row.OrderId = order?.Id;
            row.StageId = order is null ? null : w.StageId;
            row.Category = order is null ? Enum.Parse<NonOrderCategory>(w.Category!, true) : null;
            row.ProductId = order?.ProductId;
            row.Specification = MfgText.Clean(w.Specification) ?? (order is null ? null : specs.GetValueOrDefault(order.ProductId));
            row.PlannedQty = Production.Round(w.PlannedQty);
            row.CompletedQty = order is null ? 0 : Production.Round(w.CompletedQty);
            row.RejectedQty = order is null ? 0 : Production.Round(w.RejectedQty);
            row.RejectReason = row.RejectedQty > 0 ? MfgText.Clean(w.RejectReason) : null;
            row.Notes = MfgText.Clean(w.Notes);
        }
        log.Notes = MfgText.Clean(r.Notes);
        return await CommitAsync(scope!, caller, log, ct);
    }

    private async Task<Dictionary<Guid, string>> ProductSpecsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct)
    {
        var rows = await db.ProductSpecs.AsNoTracking().Where(s => productIds.Contains(s.ProductId)).ToListAsync(ct);
        var names = await SpecValues.NamesAsync(db, ct);
        return rows.GroupBy(s => s.ProductId).ToDictionary(g => g.Key, g => string.Join(" · ", SpecValues.Dtos(g, names)
            .Select(s => s.Kind == nameof(SpecKind.Text) ? $"{s.AttributeCode} {s.Text}" : $"{s.AttributeCode} {s.Value:0.######} {s.UnitCode}")));
    }

    public async Task<MfgResult<ProductionLogDto>> SubmitAsync(MfgCaller caller, Guid id, CancellationToken ct)
    {
        var (scope, log, refusal) = await WriteAsync(caller, id, ct);
        if (refusal is not null) return refusal;
        if (log!.Status != ProductionLogStatus.Draft)
            return await Refuse(scope!, MfgFail.Conflict<ProductionLogDto>("log_not_draft", "Only a draft record is submitted."));
        if (log.Rows.Count == 0) return await Refuse(scope!, MfgFail.Invalid<ProductionLogDto>("rows_required", "A record has at least one row."));
        log.Status = ProductionLogStatus.Submitted;
        log.SubmittedByUserId = caller.UserId;
        log.SubmittedBy = caller.Name;
        log.SubmittedAtUtc = DateTime.UtcNow;
        log.ReturnedReason = null;
        return await CommitAsync(scope!, caller, log, ct);
    }

    /// <summary>A manager sends a submitted record back to draft, with the reason, for the line to correct.</summary>
    public async Task<MfgResult<ProductionLogDto>> ReturnAsync(MfgCaller caller, Guid id, OrderReasonRequest r, CancellationToken ct)
    {
        var (scope, log, refusal) = await WriteAsync(caller, id, ct);
        if (refusal is not null) return refusal;
        if (log!.Status != ProductionLogStatus.Submitted)
            return await Refuse(scope!, MfgFail.Conflict<ProductionLogDto>("log_not_submitted", "Only a submitted record is sent back."));
        if (!await ManufacturingAccess.ManagesAsync(db, caller, log.LineId, ct))
            return await Refuse(scope!, MfgFail.Forbidden<ProductionLogDto>("Sending a record back is for a production manager of the line."));
        if (Production.Clean(r.Reason, 400) is not string reason)
            return await Refuse(scope!, MfgFail.Invalid<ProductionLogDto>("reason_required", "Say what to correct."));
        log.Status = ProductionLogStatus.Draft;
        log.ReturnedReason = reason;
        return await CommitAsync(scope!, caller, log, ct);
    }

    /// <summary>
    /// Approves a submitted record: the progress of each order's stages moves by its rows, rejects leave the
    /// flow at the stage that rejected them, and the record is frozen.
    /// </summary>
    public async Task<MfgResult<ProductionLogDto>> ApproveAsync(MfgCaller caller, Guid id, CancellationToken ct)
    {
        var (scope, log, refusal) = await WriteAsync(caller, id, ct);
        if (refusal is not null) return refusal;
        if (log!.Status != ProductionLogStatus.Submitted)
            return await Refuse(scope!, MfgFail.Conflict<ProductionLogDto>("log_not_submitted", "Only a submitted record is approved."));
        if (!await ManufacturingAccess.ManagesAsync(db, caller, log.LineId, ct))
            return await Refuse(scope!, MfgFail.Forbidden<ProductionLogDto>("Approving a record is for a production manager of the line."));
        if (log.SubmittedByUserId == caller.UserId)
            return await Refuse(scope!, MfgFail.Forbidden<ProductionLogDto>("The person who submitted a record cannot approve it.", "own_record"));

        var orderIds = log.Rows.Where(x => x.OrderId != null).Select(x => x.OrderId!.Value).Distinct().ToList();
        var orders = await db.ProductionOrders.Include(o => o.Stages).Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        foreach (var row in log.Rows.Where(x => x.OrderId != null))
        {
            var order = orders[row.OrderId!.Value];
            if (order.Status is not (ProductionOrderStatus.Open or ProductionOrderStatus.OnHold))
                return await Refuse(scope!, MfgFail.Conflict<ProductionLogDto>("order_not_open", $"{order.Number} is no longer open."));
            var stage = order.Stages.First(s => s.Id == row.StageId);
            stage.GoodBaseQty += row.CompletedQty;
            stage.RejectedBaseQty += row.RejectedQty;
            order.RejectedBaseQty += row.RejectedQty;
            order.UpdatedAtUtc = DateTime.UtcNow;
        }
        log.Status = ProductionLogStatus.Approved;
        log.ApprovedByUserId = caller.UserId;
        log.ApprovedBy = caller.Name;
        log.ApprovedAtUtc = DateTime.UtcNow;
        return await CommitAsync(scope!, caller, log, ct);
    }

    // ---------- hours ----------

    /// <summary>
    /// Books one employee's hours on a row of a draft record. Refused when the person is not on the line
    /// that day, the payroll month is held by a run, the row already has them, or the day's hours across
    /// every booking would pass their attendance — the last unless the caller may override and says why.
    /// </summary>
    public async Task<MfgResult<ProductionLogDto>> BookAsync(MfgCaller caller, Guid id, Guid rowId, LaborBookingRequest r, CancellationToken ct)
    {
        var (scope, log, refusal) = await WriteAsync(caller, id, ct);
        if (refusal is not null) return refusal;
        if (log!.Status != ProductionLogStatus.Draft)
            return await Refuse(scope!, MfgFail.Conflict<ProductionLogDto>("log_not_draft", "Hours are booked on a draft record."));
        var row = log.Rows.FirstOrDefault(x => x.Id == rowId);
        if (row is null) return await Refuse(scope!, MfgFail.NotFound<ProductionLogDto>("Row"));
        if (r.Hours <= 0 || r.Hours > 24)
            return await Refuse(scope!, MfgFail.Invalid<ProductionLogDto>("invalid_hours", "Hours are above zero and at most 24."));
        // C-05: how many of the hours were overtime — a fact of the day, recorded whatever the costing choice.
        if (r.OvertimeHours < 0 || r.OvertimeHours > r.Hours)
            return await Refuse(scope!, MfgFail.Invalid<ProductionLogDto>("invalid_overtime_hours",
                "Overtime hours are between zero and the hours booked."));
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == r.EmployeeId, ct);
        if (employee is null || !await OnLineAsync(log.LineId, employee.Id, log.Day, ct))
            return await Refuse(scope!, MfgFail.Invalid<ProductionLogDto>("employee_not_on_line", "This person is not assigned to the line that day."));
        if ((await sheet.LockOfAsync(employee.BranchId, new AttendancePeriod(log.Day.Year, log.Day.Month), ct)).Locked)
            return await Refuse(scope!, MfgFail.Conflict<ProductionLogDto>("payroll_month_locked",
                "That month's payroll is closed for this person's branch; its hours no longer change."));
        if (await db.LaborBookings.AnyAsync(b => b.EmployeeId == employee.Id && b.LogRowId == (Guid?)row.Id, ct))
            return await Refuse(scope!, MfgFail.Conflict<ProductionLogDto>("already_booked", "This person already has hours on this row."));

        var day = (await attendance.ForDayAsync([employee], log.Day, ct))[employee.Id];
        // Every booking that day, on every line and order kind — maintenance jobs included: one hour is never allocated twice.
        var booked = await LabourCeiling.BookedAsync(db, employee.Id, log.Day, ct);
        var capacity = day.Hours ?? 0m;
        var overrideReason = Production.Clean(r.OverrideReason, 400);
        var overridden = false;
        if (booked + r.Hours > capacity)
        {
            if (overrideReason is null)
                return await Refuse(scope!, MfgFail.Conflict<ProductionLogDto>(LabourCeiling.ExceedsCode, LabourCeiling.Exceeds(day, booked)));
            if (!caller.Has(PermissionCatalog.ManufacturingLabourOverride))
                return await Refuse(scope!, MfgFail.Forbidden<ProductionLogDto>("Booking beyond attendance needs the override permission.", "override_forbidden"));
            overridden = true;
        }

        var booking = new LaborBooking
        {
            EmployeeId = employee.Id, Day = log.Day, OrderKind = LaborOrderKind.Production, OrderId = row.OrderId, StageId = row.StageId,
            LineId = log.LineId, LogRowId = row.Id, Hours = r.Hours, OvertimeHours = r.OvertimeHours, PayrollBranchId = employee.BranchId,
            PayrollYear = log.Day.Year, PayrollMonth = log.Day.Month, AttendanceHours = capacity,
            OverrideReason = overridden ? overrideReason : null, OverriddenByUserId = overridden ? caller.UserId : null,
            OverriddenBy = overridden ? caller.Name : null, UserId = caller.UserId, UserName = caller.Name,
        };
        db.LaborBookings.Add(booking);
        db.LaborBookingChanges.Add(Change(booking, LaborBookingAction.Created, caller));
        return await CommitAsync(scope!, caller, log, ct);
    }

    public async Task<MfgResult<ProductionLogDto>> UnbookAsync(MfgCaller caller, Guid id, Guid bookingId, CancellationToken ct)
    {
        var (scope, log, refusal) = await WriteAsync(caller, id, ct);
        if (refusal is not null) return refusal;
        if (log!.Status != ProductionLogStatus.Draft)
            return await Refuse(scope!, MfgFail.Conflict<ProductionLogDto>("log_not_draft", "Hours change only on a draft record."));
        var rowIds = log.Rows.Select(x => x.Id).ToList();
        var booking = await db.LaborBookings.FirstOrDefaultAsync(b => b.Id == bookingId && b.LogRowId != null && rowIds.Contains(b.LogRowId.Value), ct);
        if (booking is null) return await Refuse(scope!, MfgFail.NotFound<ProductionLogDto>("Booking"));
        if ((await sheet.LockOfAsync(booking.PayrollBranchId, new AttendancePeriod(booking.PayrollYear, booking.PayrollMonth), ct)).Locked)
            return await Refuse(scope!, MfgFail.Conflict<ProductionLogDto>("payroll_month_locked", "That month's payroll is closed; its hours no longer change."));
        db.LaborBookingChanges.Add(Change(booking, LaborBookingAction.Removed, caller));
        db.LaborBookings.Remove(booking);
        return await CommitAsync(scope!, caller, log, ct);
    }

    private static LaborBookingChange Change(LaborBooking b, LaborBookingAction action, MfgCaller caller) => new()
    {
        BookingId = b.Id, EmployeeId = b.EmployeeId, Day = b.Day, LogRowId = b.LogRowId, OrderKind = b.OrderKind, OrderId = b.OrderId,
        Action = action, Hours = b.Hours,
        OvertimeHours = b.OvertimeHours,
        OverrideReason = b.OverrideReason, UserId = caller.UserId, UserName = caller.Name,
    };

    private Task<bool> OnLineAsync(Guid lineId, Guid employeeId, DateOnly day, CancellationToken ct) =>
        db.ProductionLineEmployees.AnyAsync(a => a.LineId == lineId && a.EmployeeId == employeeId
            && a.AssignedOn <= day && (a.EndedOn == null || a.EndedOn >= day), ct);

    /// <summary>The people the record may book: on the line that day, with the day's attendance and what is booked already.</summary>
    public async Task<MfgResult<List<LogEmployeeCandidateDto>>> CandidatesAsync(MfgCaller caller, Guid id, CancellationToken ct)
    {
        var log = await db.ProductionLogs.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);
        if (log is null || !await ManufacturingAccess.ReachesAsync(db, caller, log.LineId, ct))
            return MfgFail.NotFound<List<LogEmployeeCandidateDto>>("Daily production record");
        var ids = await db.ProductionLineEmployees.AsNoTracking()
            .Where(a => a.LineId == log.LineId && a.AssignedOn <= log.Day && (a.EndedOn == null || a.EndedOn >= log.Day))
            .Select(a => a.EmployeeId).Distinct().ToListAsync(ct);
        var employees = await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id)).OrderBy(e => e.FullName).ToListAsync(ct);
        var days = await attendance.ForDayAsync(employees, log.Day, ct);
        var booked = (await db.LaborBookings.AsNoTracking().Where(b => ids.Contains(b.EmployeeId) && b.Day == log.Day)
                .Select(b => new { b.EmployeeId, b.Hours }).ToListAsync(ct))
            .GroupBy(b => b.EmployeeId).ToDictionary(g => g.Key, g => g.Sum(b => b.Hours));
        var period = new AttendancePeriod(log.Day.Year, log.Day.Month);
        var locks = new Dictionary<Guid, bool>();
        foreach (var branch in employees.Select(e => e.BranchId).Distinct())
            locks[branch] = (await sheet.LockOfAsync(branch, period, ct)).Locked;
        return MfgResult<List<LogEmployeeCandidateDto>>.Done(employees.Select(e => new LogEmployeeCandidateDto(
            e.Id, e.Number, e.FullName, days[e.Id].Hours, days[e.Id].Source, booked.GetValueOrDefault(e.Id), locks[e.BranchId])).ToList());
    }
}
