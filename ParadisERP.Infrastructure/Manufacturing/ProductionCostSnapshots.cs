using ParadisERP.Application.Contracts;
using ParadisERP.Application.Manufacturing;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;

namespace ParadisERP.Infrastructure.Manufacturing;

/// <summary>
/// B1's immutable operational cost record. It freezes the Phase A engine's inputs and result without
/// valuing inventory or creating any accounting entry. Financial finalization deliberately does not exist here.
/// </summary>
public sealed class ProductionCostSnapshots(
    PosDbContext db, ProductionCosts costs, ManufacturingCostingOptions options, TimeProvider time)
{
    public async Task<MfgResult<ManufacturingCostSnapshotDto>> GetAsync(MfgCaller caller, Guid orderId, CancellationToken ct)
    {
        if (ReadRefusal<ManufacturingCostSnapshotDto>(caller) is { } refusal) return refusal;
        if (await AuthorizedOrderAsync(caller, orderId, ct) is null)
            return MfgFail.NotFound<ManufacturingCostSnapshotDto>("Manufacturing cost snapshot");
        var sheet = await db.ManufacturingCostSheets.AsNoTracking().FirstOrDefaultAsync(x => x.OrderId == orderId, ct);
        if (sheet is null)
            return MfgFail.NotFound<ManufacturingCostSnapshotDto>("Manufacturing cost snapshot");
        return MfgResult<ManufacturingCostSnapshotDto>.Done(Shown(caller, await MapAsync(sheet, ct)));
    }

    /// <summary>
    /// The frozen sheet as this reader may see it (CostPrivacy): without pay-rate access, the order's total and unit cost,
    /// its release values and its late costs' amounts are withheld, each giving the labour back. What is stored is never
    /// touched: only the answer.
    /// </summary>
    private static ManufacturingCostSnapshotDto Shown(MfgCaller caller, ManufacturingCostSnapshotDto dto) =>
        CostPrivacy.RatesShown(caller) ? dto : dto with
        {
            TotalCost = null, UnitCost = null, OrderMoneyWithheld = true,
            Releases = dto.Releases.Select(r => r with { Value = null, UnitValue = null }).ToList(),
            LateCosts = dto.LateCosts.Select(l => l with { Amount = null }).ToList(),
        };

    public async Task<MfgResult<ManufacturingCostSnapshotDto>> CreateAsync(
        MfgCaller caller, Guid orderId, CreateManufacturingCostSnapshotRequest request, CancellationToken ct)
    {
        if (WriteRefusal<ManufacturingCostSnapshotDto>(caller) is { } refusal) return refusal;
        var requestId = Production.Clean(request.RequestId, 64);
        if (requestId is null) return MfgFail.Invalid<ManufacturingCostSnapshotDto>("invalid_request_id", "A request id is required.");
        var order = await AuthorizedOrderAsync(caller, orderId, ct);
        if (order is null) return MfgFail.NotFound<ManufacturingCostSnapshotDto>("Production order");
        if (order.Status != ProductionOrderStatus.Completed)
            return MfgFail.Conflict<ManufacturingCostSnapshotDto>("order_not_completed", "Complete the production order before freezing its cost snapshot.");

        // The write scope serializes the existence check and insert for both SQLite and SQL Server. The unique
        // indexes remain the final protection for callers retrying concurrently from different processes.
        try
        {
            await using var scope = await db.BeginWriteScopeAsync(ct);
            var existingRequest = await db.ManufacturingCostSheets.FirstOrDefaultAsync(x => x.RequestId == requestId, ct);
            if (existingRequest is not null)
            {
                if (existingRequest.OrderId != orderId)
                    return MfgFail.Conflict<ManufacturingCostSnapshotDto>("snapshot_request_reused", "This request id belongs to another order.");
                return MfgResult<ManufacturingCostSnapshotDto>.Done(Shown(caller, await MapAsync(existingRequest, ct)));
            }
            var prior = await db.ManufacturingCostSheets.FirstOrDefaultAsync(x => x.OrderId == orderId, ct);
            if (prior is not null) return MfgFail.Conflict<ManufacturingCostSnapshotDto>("snapshot_exists", "This order already has an immutable cost snapshot.");

            var units = await db.UnitsOfMeasure.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Code, ct);
            var (input, _, _) = await costs.InputAsync(order, units, ct, forStockValue: true);
            var computed = ProductionCostEngine.Compute(input);
            var source = await CaptureAsync(input, computed, order.Id, ct);
            var total = Money(computed.Actual.Total);
            var status = ManufacturingCostSnapshotStatus.Provisional; // Accountant policy has not approved B1 financial finalization.
            var sheet = new ManufacturingCostSheet
        {
            OrderId = order.Id, LineId = order.LineId,
            Kind = request.Reconstruct ? ManufacturingCostSnapshotKind.Reconstructed : ManufacturingCostSnapshotKind.Created,
            Status = status, CalculationVersion = ProductionCostEngine.Version, RequestId = requestId,
            SourceHash = source.Hash, SourceSnapshotJson = source.Json, SourceState = ManufacturingCostSourceState.Complete,
            AcceptedBaseQty = order.AcceptedBaseQty, RejectedBaseQty = order.RejectedBaseQty,
            TotalCost = total, UnitCost = order.AcceptedBaseQty > 0m ? Unit(total / order.AcceptedBaseQty) : null,
            ProvisionalReasons = Reasons(computed), CreatedByUserId = caller.UserId, CreatedBy = caller.Name,
            CreatedAtUtc = time.GetUtcNow().UtcDateTime
            };
            db.ManufacturingCostSheets.Add(sheet);
            AddElements(sheet, computed, input);
            AddMaterials(sheet, order, computed);
            AddLabour(sheet, computed);
            await AddExpensesAsync(sheet, orderId, ct);
            AddScrap(sheet, order, computed);
            AddReleases(sheet, computed, total);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return MfgResult<ManufacturingCostSnapshotDto>.Done(Shown(caller, await MapAsync(sheet, ct)));
        }
        catch (DbUpdateException)
        {
            // A competing process may have passed the read before this process committed. The
            // two database unique keys are the authority; after their collision, return the
            // winner for the same request and never manufacture a second immutable sheet.
            db.ChangeTracker.Clear();
            if (await AuthorizedOrderAsync(caller, orderId, ct) is null)
                return MfgFail.NotFound<ManufacturingCostSnapshotDto>("Production order");
            var winner = await db.ManufacturingCostSheets.AsNoTracking().FirstOrDefaultAsync(x => x.RequestId == requestId, ct);
            if (winner is not null && winner.OrderId == orderId)
                return MfgResult<ManufacturingCostSnapshotDto>.Done(Shown(caller, await MapAsync(winner, ct)));
            return MfgFail.Conflict<ManufacturingCostSnapshotDto>("snapshot_exists", "This order already has an immutable cost snapshot.");
        }
    }

    public async Task<MfgResult<ManufacturingLateCostDto>> AddLateCostAsync(
        MfgCaller caller, Guid orderId, CreateManufacturingLateCostRequest request, CancellationToken ct)
    {
        if (WriteRefusal<ManufacturingLateCostDto>(caller) is { } refusal) return refusal;
        var kind = Production.Clean(request.Kind, 32); var source = Production.Clean(request.SourceReference, 128);
        var reason = Production.Clean(request.Reason, 400);
        if (kind is null || source is null || reason is null || request.Amount == 0m)
            return MfgFail.Invalid<ManufacturingLateCostDto>("invalid_late_cost", "Kind, source reference, reason and a non-zero amount are required.");
        if (await AuthorizedOrderAsync(caller, orderId, ct) is null)
            return MfgFail.NotFound<ManufacturingLateCostDto>("Manufacturing cost snapshot");
        try
        {
            await using var scope = await db.BeginWriteScopeAsync(ct);
            var sheet = await db.ManufacturingCostSheets.FirstOrDefaultAsync(x => x.OrderId == orderId, ct);
            if (sheet is null)
                return MfgFail.NotFound<ManufacturingLateCostDto>("Manufacturing cost snapshot");
            var duplicate = await db.ManufacturingLateCosts.FirstOrDefaultAsync(x => x.SheetId == sheet.Id && x.Kind == kind && x.SourceReference == source, ct);
            if (duplicate is not null)
                return MfgResult<ManufacturingLateCostDto>.Done(Late(duplicate));
            var late = new ManufacturingLateCost { SheetId = sheet.Id, Kind = kind, SourceReference = source, Amount = Money(request.Amount), Reason = reason, RecordedAtUtc = time.GetUtcNow().UtcDateTime };
            db.ManufacturingLateCosts.Add(late);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return MfgResult<ManufacturingLateCostDto>.Done(Late(late));
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await AuthorizedOrderAsync(caller, orderId, ct) is null)
                return MfgFail.NotFound<ManufacturingLateCostDto>("Manufacturing cost snapshot");
            var winner = await (from late in db.ManufacturingLateCosts.AsNoTracking()
                                join sheet in db.ManufacturingCostSheets.AsNoTracking() on late.SheetId equals sheet.Id
                                where sheet.OrderId == orderId && late.Kind == kind && late.SourceReference == source
                                select late).FirstOrDefaultAsync(ct);
            return winner is not null
                ? MfgResult<ManufacturingLateCostDto>.Done(Late(winner))
                : MfgFail.Conflict<ManufacturingLateCostDto>("late_cost_exists", "A late-cost record already exists for this source.");
        }
    }

    private MfgResult<T>? ReadRefusal<T>(MfgCaller caller)
    {
        if (!options.Enabled) return MfgFail.Conflict<T>("costing_not_enabled", "Manufacturing costing is not switched on for this server.");
        if (!caller.Has(PermissionCatalog.ManufacturingCostsView))
            return MfgFail.Forbidden<T>("Seeing a manufacturing cost snapshot needs its own permission.");
        return null;
    }

    private MfgResult<T>? WriteRefusal<T>(MfgCaller caller) =>
        ReadRefusal<T>(caller) ?? (!caller.Has(PermissionCatalog.ManufacturingCostSnapshotCreate)
            ? MfgFail.Forbidden<T>("Creating a provisional cost snapshot or late-cost record needs its own manufacturing permission.") : null);

    /// <summary>Loads the target and applies the line scope before any idempotency or history lookup.</summary>
    private async Task<ProductionOrder?> AuthorizedOrderAsync(MfgCaller caller, Guid orderId, CancellationToken ct)
    {
        var order = await db.ProductionOrders.Include(o => o.Materials).ThenInclude(m => m.Product)
            .Include(o => o.Stages).Include(o => o.Product).AsSplitQuery().FirstOrDefaultAsync(o => o.Id == orderId, ct);
        return order is not null && await ManufacturingAccess.ReachesAsync(db, caller, order.LineId, ct) ? order : null;
    }

    private static void AddElements(ManufacturingCostSheet sheet, ProductionCostSheet calculated, CostingInput input)
    {
        var elements = calculated.Actual;
        var rows = new[]
        {
            ("materials", elements.Materials), ("consumables", elements.Consumables), ("accessories", elements.Accessories),
            ("labour", elements.Labour), ("overhead", elements.Overhead), ("expenses", elements.Expenses), ("recovery", elements.Recovery)
        };
        foreach (var (element, value) in rows)
            sheet.Elements.Add(new ManufacturingCostElementLine { SheetId = sheet.Id, Element = element, Quantity = element == "labour" ? calculated.LabourHours : 1m,
                UnitCost = element == "labour" && calculated.LabourHours > 0m ? Unit(value / calculated.LabourHours) : Money(value), TotalCost = Money(value),
                Status = ElementStatus(element, calculated.Statuses), SourceReference = element == "overhead" ? $"rate:{input.Policy.OverheadPerLabourHour}" : "B1.0" });
    }

    private void AddMaterials(ManufacturingCostSheet sheet, ProductionOrder order, ProductionCostSheet calculated)
    {
        foreach (var line in calculated.Materials)
        {
            var source = order.Materials.Single(m => m.Id == line.Id);
            var quantity = source.ConsumedBaseQty + source.ScrappedBaseQty;
            db.ManufacturingMaterialCostSnapshots.Add(new ManufacturingMaterialCostSnapshot
            {
                SheetId = sheet.Id, OrderMaterialId = source.Id, ProductId = source.ProductId, UnitId = source.BaseUnitId,
                Quantity = quantity, UnitCost = Unit(line.IssueAverage), TotalCost = Money(line.ConsumedValue + line.ScrapValue),
                CostBasis = "reference_cost", Status = "provisional"
            });
        }
    }

    private void AddLabour(ManufacturingCostSheet sheet, ProductionCostSheet calculated)
    {
        foreach (var line in calculated.Labour)
            db.ManufacturingLabourRateSnapshots.Add(new ManufacturingLabourRateSnapshot
            {
                SheetId = sheet.Id, EmployeeId = line.EmployeeId, PayrollYear = line.Year, PayrollMonth = line.Month,
                Hours = line.Hours, Rate = line.Rate is decimal rate ? Unit(rate) : null,
                TotalCost = line.Cost is decimal cost ? Money(cost) : null,
                Status = line.Kind == LabourRateKind.Actual ? "recorded" : "provisional", Reason = line.Reason
            });
    }

    private async Task AddExpensesAsync(ManufacturingCostSheet sheet, Guid orderId, CancellationToken ct)
    {
        var expenses = await db.ProductionOrderExpenses.AsNoTracking().Where(x => x.OrderId == orderId).ToListAsync(ct);
        foreach (var expense in expenses)
            db.ManufacturingExpenseAllocationSnapshots.Add(new ManufacturingExpenseAllocationSnapshot
            { SheetId = sheet.Id, ExpenseId = expense.Id, PaymentVoucherId = expense.PaymentVoucherId, Amount = Money(expense.Amount), Status = "provisional" });
    }

    private void AddScrap(ManufacturingCostSheet sheet, ProductionOrder order, ProductionCostSheet calculated) =>
        db.ManufacturingScrapRecoverySnapshots.Add(new ManufacturingScrapRecoverySnapshot
        { SheetId = sheet.Id, RejectedQty = order.RejectedBaseQty, RecoveryValue = Money(calculated.Actual.Recovery), Status = "provisional" });

    private void AddReleases(ManufacturingCostSheet sheet, ProductionCostSheet calculated, decimal total)
    {
        var carried = 0m;
        foreach (var release in calculated.Releases)
        {
            var value = release.CarriesRemainder ? total - carried : Money(release.Value);
            carried += value;
            db.ManufacturingReleaseValueSnapshots.Add(new ManufacturingReleaseValueSnapshot
            { SheetId = sheet.Id, OutputReleaseId = release.Id, Quantity = release.BaseQty, Value = value, UnitValue = Unit(release.UnitValue), Status = "provisional" });
        }
    }

    private async Task<ManufacturingCostSnapshotDto> MapAsync(ManufacturingCostSheet sheet, CancellationToken ct)
    {
        var releases = await db.ManufacturingReleaseValueSnapshots.AsNoTracking().Where(x => x.SheetId == sheet.Id).OrderBy(x => x.Id)
            .Select(x => new CostReleaseDto(x.OutputReleaseId, "", x.Quantity, x.Value, x.UnitValue, false)).ToListAsync(ct);
        var late = await db.ManufacturingLateCosts.AsNoTracking().Where(x => x.SheetId == sheet.Id).OrderBy(x => x.RecordedAtUtc).ToListAsync(ct);
        return new ManufacturingCostSnapshotDto(sheet.Id, sheet.OrderId, sheet.LineId, sheet.Kind.ToString().ToLowerInvariant(), sheet.Status.ToString().ToLowerInvariant(),
            sheet.CalculationVersion, sheet.RequestId, sheet.SourceHash, sheet.SourceState.ToString().ToLowerInvariant(), sheet.AcceptedBaseQty, sheet.RejectedBaseQty, sheet.TotalCost, sheet.UnitCost,
            sheet.ProvisionalReasons, sheet.CreatedAtUtc, sheet.Kind == ManufacturingCostSnapshotKind.Reconstructed, releases, late.Select(Late).ToList());
    }

    private async Task<(string Json, string Hash)> CaptureAsync(CostingInput input, ProductionCostSheet calculated, Guid orderId, CancellationToken ct)
    {
        // Keep this deliberately explicit. It is the complete, deterministic source set needed to
        // reconstruct B1's calculation without reading mutable order, payroll or voucher rows later.
        var movements = await db.MaterialMovementLines.AsNoTracking().Where(x => x.Movement!.OrderId == orderId)
            .Select(x => new { x.Id, x.MovementId, Number = x.Movement!.Number, Kind = x.Movement.Kind, x.OrderMaterialId, x.ProductId, x.EnteredQty, x.UnitId, x.Factor, x.BaseQty, x.UnitCostSnapshot })
            .OrderBy(x => x.Id).ToListAsync(ct);
        var consumptions = await db.MaterialConsumptions.AsNoTracking().Where(x => x.OrderId == orderId)
            .Select(x => new { x.Id, x.OrderMaterialId, x.LogRowId, x.BaseQty, x.Reason, x.ClientRequestId, x.AtUtc })
            .OrderBy(x => x.Id).ToListAsync(ct);
        var bookings = await db.LaborBookings.AsNoTracking().Where(x => x.OrderId == orderId && x.OrderKind == LaborOrderKind.Production)
            .Select(x => new { x.Id, x.EmployeeId, x.StageId, x.LogRowId, x.Day, x.PayrollBranchId, x.PayrollYear, x.PayrollMonth, x.Hours, PayrollRunId = (Guid?)null, PayrollRunLineId = (Guid?)null })
            .OrderBy(x => x.Id).ToListAsync(ct);
        var expenses = await db.ProductionOrderExpenses.AsNoTracking().Where(x => x.OrderId == orderId)
            .Select(x => new { x.Id, x.Description, x.Amount, x.AtUtc, x.PaymentVoucherId }).OrderBy(x => x.Id).ToListAsync(ct);
        var voucherIds = expenses.Where(x => x.PaymentVoucherId is not null).Select(x => x.PaymentVoucherId!.Value).Distinct().ToList();
        var vouchers = await db.PaymentVouchers.AsNoTracking().Where(x => voucherIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Status }).OrderBy(x => x.Id).ToListAsync(ct);
        var outputReleases = await db.OutputReleases.AsNoTracking().Where(x => x.OrderId == orderId)
            .Select(x => new { x.Id, x.Number, x.BaseQty, x.UnitCostSnapshot, x.PostingState, x.AtUtc })
            .OrderBy(x => x.Id).ToListAsync(ct);
        // C-05: the overtime figures only change a sheet frozen under "premium to overtime hours"; under the blended rate
        // they are left out, so a sheet frozen under the defaults serializes exactly as before.
        var premium = input.Policy.LabourAbsorption == LabourAbsorption.PremiumToOvertime;
        var json = JsonSerializer.Serialize(new
        {
            input.PlannedBaseQty, input.AcceptedBaseQty, input.RejectedBaseQty, input.Completed,
            input.StandardHours, input.StandardRate,
            Materials = input.Materials.OrderBy(x => x.Id).Select(x => new
            {
                x.Id, x.Sku, x.Role, x.BaseUnitCode, x.RequiredBaseQty, x.EstimatedUnitCost,
                x.IssuedBaseQty, x.ReturnedBaseQty, x.ConsumedBaseQty, x.ScrappedBaseQty, x.IssuedValue, x.ReturnedValue
            }),
            Bookings = input.Bookings.OrderBy(x => x.EmployeeId).ThenBy(x => x.PayrollYear).ThenBy(x => x.PayrollMonth).ThenBy(x => x.Day).ThenBy(x => x.StageId)
                .Select(x => new BookingDocument(x)),
            Rates = input.Rates.OrderBy(x => x.Key.EmployeeId).ThenBy(x => x.Key.Year).ThenBy(x => x.Key.Month)
                .Select(x => new CapturedRateDocument(x.Key, x.Value, premium)),
            Expenses = input.Expenses.OrderBy(x => x.Id),
            Releases = input.Releases.OrderBy(x => x.Id),
            Policy = new CapturedPolicy(input.Policy), input.RecoveryValue,
            ElementStatuses = calculated.Statuses,
            Notes = calculated.Notes.OrderBy(x => x.Code),
            ReleaseAllocations = calculated.Releases.OrderBy(x => x.Id),
            SourceIdentities = new { MaterialMovements = movements, MaterialConsumptions = consumptions, LabourBookings = bookings, Expenses = expenses, Vouchers = vouchers, OutputReleases = outputReleases }
        }, CanonicalJson);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        return (json, hash);
    }

    /// <summary>
    /// The policy as a sheet records it. The choices added on 2026-11-08 (C-06, C-11) are written only when they
    /// differ from the default, so a sheet frozen under the defaults serializes — and hashes — exactly as one
    /// frozen before they existed.
    /// </summary>
    private sealed class CapturedPolicy(CostingPolicy policy)
    {
        public decimal OverheadPerLabourHour { get; } = policy.OverheadPerLabourHour;
        public bool Approved { get; } = policy.Approved;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ReleaseValuationRule ReleaseRule { get; } = policy.ReleaseRule;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public decimal EmployerBurdenPercent { get; } = policy.EmployerBurdenPercent;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public decimal ExpenseVatPercent { get; } = policy.ExpenseVatPercent;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public decimal ScrapRecoveryPercent { get; } = policy.ScrapRecoveryPercent;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public LabourAbsorption LabourAbsorption { get; } = policy.LabourAbsorption;
    }

    /// <summary>
    /// A booking as a sheet records it, in the engine input's own field order. Its overtime hours (C-05, 2026-11-09) are
    /// written only when there are some, so a booking with none serializes as before.
    /// </summary>
    private sealed class BookingDocument(CostingBooking b)
    {
        public Guid EmployeeId { get; } = b.EmployeeId;
        public Guid StageId { get; } = b.StageId;
        public DateOnly Day { get; } = b.Day;
        public int PayrollYear { get; } = b.PayrollYear;
        public int PayrollMonth { get; } = b.PayrollMonth;
        public decimal Hours { get; } = b.Hours;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public decimal OvertimeHours { get; } = b.OvertimeHours;
    }

    /// <summary>
    /// An employee-month's rate as a sheet records it. The overtime figures (C-05) are written only under "premium to overtime
    /// hours" and only when not 0: under the blended rate they change nothing, so they are not part of the sheet's source.
    /// </summary>
    private sealed class CapturedRateDocument((Guid EmployeeId, int Year, int Month) key, LabourRate rate, bool premium)
    {
        public Guid EmployeeId { get; } = key.EmployeeId;
        public int Year { get; } = key.Year;
        public int Month { get; } = key.Month;
        public decimal? PerHour { get; } = rate.PerHour;
        public LabourRateKind Kind { get; } = rate.Kind;
        public string? Reason { get; } = rate.Reason;
        public decimal? EmployerCost { get; } = rate.EmployerCost;
        public decimal? PaidHours { get; } = rate.PaidHours;
        public bool WithoutEmployerSocial { get; } = rate.WithoutEmployerSocial;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public decimal OvertimePremium { get; } = premium ? rate.OvertimePremium : 0m;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public decimal PaidOvertimeHours { get; } = premium ? rate.PaidOvertimeHours : 0m;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public decimal MarkedOvertimeHours { get; } = premium ? rate.MarkedOvertimeHours : 0m;
    }

    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        WriteIndented = false, PropertyNamingPolicy = null, Encoder = JavaScriptEncoder.Default
    };

    private static string ElementStatus(string element, CostElementStatuses statuses) => element switch
    {
        "materials" => statuses.Materials, "consumables" => statuses.Consumables, "accessories" => statuses.Accessories,
        "labour" => statuses.Labour, "overhead" => statuses.Overhead, "expenses" => statuses.Expenses, "recovery" => statuses.Recovery,
        _ => "provisional"
    };

    private static ManufacturingLateCostDto Late(ManufacturingLateCost x) => new(x.Id, x.Kind, x.SourceReference, x.Amount, x.Reason, x.RecordedAtUtc);
    private static string Reasons(ProductionCostSheet sheet) => string.Join(",", new[] { sheet.Statuses.Materials, sheet.Statuses.Consumables, sheet.Statuses.Accessories, sheet.Statuses.Labour, sheet.Statuses.Overhead, sheet.Statuses.Expenses, sheet.Statuses.Recovery }.Where(x => x != ProductionCostEngine.StatusApproved).Distinct());
    private static decimal Money(decimal value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);
    private static decimal Unit(decimal value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);
}
