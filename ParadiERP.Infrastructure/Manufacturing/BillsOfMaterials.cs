using ParadiERP.Application.Contracts;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Manufacturing;

/// <summary>
/// Versioned bills of materials (Phase 1 §4; audit D11, D24, amendment 5).
///
/// <para>
/// A version is written as a draft, activated once it is right, and frozen from then on: an active or
/// retired version is never edited, so whatever is made from it can always be read back as it was
/// defined. A change is a new version, copied from the one it replaces and activated in its turn, which
/// retires the old one. Versions count per product and line; a BOM with no line serves every line.
/// </para>
/// <para>
/// Every quantity is checked to convert to the component's base unit when the draft is saved, and again
/// when it is activated, because a product's units can change in between. A recipe that cannot say how
/// much stock it needs is refused rather than guessed.
/// </para>
/// </summary>
public sealed class BillsOfMaterials(PosDbContext db, ManufacturingSettings settings)
{
    private const int MaxLines = 500;
    private const decimal MaxQuantity = 1_000_000_000m;

    // ---------- reads ----------

    public async Task<List<BomSummaryDto>> ListAsync(MfgCaller caller, Guid? productId, Guid? lineId, string? status, CancellationToken ct)
    {
        var reachable = ManufacturingAccess.Reachable(db, caller).Select(l => l.Id);
        var query = db.BillsOfMaterials.AsNoTracking()
            .Where(b => b.ProductionLineId == null || reachable.Contains(b.ProductionLineId.Value));
        if (productId is Guid p) query = query.Where(b => b.ProductId == p);
        if (lineId is Guid l) query = query.Where(b => b.ProductionLineId == l);
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<BomStatus>(status, true, out var s)) return [];
            query = query.Where(b => b.Status == s);
        }
        var boms = await query.Include(b => b.Product).Include(b => b.ProductionLine).Include(b => b.BatchUnit)
            .Include(b => b.Lines).Include(b => b.Stages).AsSplitQuery().ToListAsync(ct);
        return boms.OrderBy(b => b.Product!.Sku, StringComparer.Ordinal)
            .ThenBy(b => b.ProductionLine?.Code ?? "", StringComparer.Ordinal)
            .ThenByDescending(b => b.Version)
            .Select(Summary).ToList();
    }

    private static BomSummaryDto Summary(BillOfMaterials b) => new(
        b.Id, b.ProductId, b.Product!.Sku, b.Product.NameAr, b.Product.NameFr, b.Product.NameEn,
        b.ProductionLineId, b.ProductionLine?.Code, b.Version, b.Status.ToString(),
        b.BatchQuantity, b.BatchUnitId, b.BatchUnit?.Code ?? "", b.Lines.Count, b.Stages.Count,
        b.CreatedAtUtc, b.ActivatedAtUtc, b.RetiredAtUtc);

    public async Task<MfgResult<BomDto>> GetAsync(MfgCaller caller, Guid id, CancellationToken ct)
    {
        var bom = await LoadAsync(id, tracking: false, ct);
        if (bom is null || !await ManufacturingAccess.ReachesBomAsync(db, caller, bom.ProductionLineId, ct))
            return MfgFail.NotFound<BomDto>("Bill of materials");
        var roles = await settings.RoleCodesAsync(ct);
        var names = await SpecValues.NamesAsync(db, ct);
        var canEdit = bom.Status == BomStatus.Draft && await ManufacturingAccess.CanWriteBomAsync(db, caller, bom.ProductionLineId, ct);
        var canApprove = bom.Status != BomStatus.Retired && await ManufacturingAccess.CanApproveBomAsync(db, caller, bom.ProductionLineId, ct);
        return MfgResult<BomDto>.Done(new BomDto(
            Summary(bom), bom.StageTemplateId, bom.Notes, bom.CreatedBy, bom.ActivatedBy, bom.RetiredBy,
            bom.Lines.OrderBy(l => l.Sequence).Select(l => new BomLineDto(
                l.Id, l.Sequence, l.ComponentProductId, l.ComponentProduct!.Sku, l.ComponentProduct.NameAr,
                l.ComponentProduct.NameFr, l.ComponentProduct.NameEn, l.ComponentProduct.ManufacturingRole.ToString(),
                l.Quantity, l.UnitId, l.Unit?.Code ?? "", l.WastePercent, l.Specification, l.Note,
                SpecValues.Dtos(l.Specs, names))).ToList(),
            bom.Stages.OrderBy(s => s.Sequence).Select(s => new StageDto(
                s.Id, s.Sequence, s.NameAr, s.NameFr, s.NameEn, s.EstimatedMinutes, s.ExpectedWorkers,
                s.IsQualityCheckpoint, s.QualityNote, s.IsOutput,
                s.Roles.Select(r => new StageRoleDto(r.JobRoleId, roles.GetValueOrDefault(r.JobRoleId, "?"), r.Count))
                    .OrderBy(r => r.JobRoleCode, StringComparer.Ordinal).ToList())).ToList(),
            canEdit, canApprove));
    }

    private Task<BillOfMaterials?> LoadAsync(Guid id, bool tracking, CancellationToken ct)
    {
        var query = db.BillsOfMaterials.Include(b => b.Product).Include(b => b.ProductionLine).Include(b => b.BatchUnit)
            .Include(b => b.Lines).ThenInclude(l => l.ComponentProduct)
            .Include(b => b.Lines).ThenInclude(l => l.Unit)
            .Include(b => b.Lines).ThenInclude(l => l.Specs)
            .Include(b => b.Stages).ThenInclude(s => s.Roles)
            .AsSplitQuery();
        return (tracking ? query : query.AsNoTracking()).FirstOrDefaultAsync(b => b.Id == id, ct);
    }

    // ---------- drafts ----------

    public async Task<MfgResult<BomDto>> CreateAsync(MfgCaller caller, UpsertBomRequest r, CancellationToken ct)
    {
        if (r.ProductionLineId is Guid line && !await ManufacturingAccess.ReachesAsync(db, caller, line, ct))
            return MfgFail.NotFound<BomDto>("Production line");
        if (!await ManufacturingAccess.CanWriteBomAsync(db, caller, r.ProductionLineId, ct))
            return MfgFail.Forbidden<BomDto>(r.ProductionLineId is null
                ? "A recipe for every line is written with company-wide scope."
                : "You may not write recipes for this line.");
        if (await DraftExistsAsync(r.ProductId, r.ProductionLineId, null, ct))
            return MfgFail.Conflict<BomDto>("draft_exists", "This product already has a draft recipe for this line; finish or delete it first.");

        var bom = new BillOfMaterials
        {
            ProductId = r.ProductId,
            ProductionLineId = r.ProductionLineId,
            CreatedBy = caller.Name,
        };
        var problem = await ApplyAsync(bom, r, ct);
        if (problem is not null) return problem.As<BomDto>();

        await using var scope = await db.BeginWriteScopeAsync(ct);
        bom.Version = await NextVersionAsync(bom.ProductId, bom.ProductionLineId, ct);
        db.BillsOfMaterials.Add(bom);
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
        return await GetAsync(caller, bom.Id, ct);
    }

    public async Task<MfgResult<BomDto>> UpdateAsync(MfgCaller caller, Guid id, UpsertBomRequest r, CancellationToken ct)
    {
        var bom = await LoadAsync(id, tracking: true, ct);
        if (bom is null || !await ManufacturingAccess.ReachesBomAsync(db, caller, bom.ProductionLineId, ct))
            return MfgFail.NotFound<BomDto>("Bill of materials");
        if (!await ManufacturingAccess.CanWriteBomAsync(db, caller, bom.ProductionLineId, ct))
            return MfgFail.Forbidden<BomDto>("You may not write recipes for this line.");
        if (bom.Status != BomStatus.Draft)
            return MfgFail.Conflict<BomDto>("bom_frozen", "An approved recipe never changes; start a new version instead.");
        if (bom.ProductId != r.ProductId || bom.ProductionLineId != r.ProductionLineId)
            return MfgFail.Invalid<BomDto>("bom_identity_fixed", "A recipe's product and line are fixed; start a new recipe for another one.");

        // Sent whole: the lines and stages are replaced.
        foreach (var old in bom.Lines.ToList()) db.BomLines.Remove(old);
        foreach (var old in bom.Stages.ToList()) db.BomStages.Remove(old);
        bom.Lines.Clear();
        bom.Stages.Clear();
        var problem = await ApplyAsync(bom, r, ct);
        if (problem is not null) return problem.As<BomDto>();
        // Every line and stage now on the draft is new. Found through a tracked parent with its key already
        // set, EF would take each for an existing row and update it; they are inserted.
        foreach (var line in bom.Lines)
        {
            db.Entry(line).State = EntityState.Added;
            foreach (var spec in line.Specs) db.Entry(spec).State = EntityState.Added;
        }
        foreach (var stage in bom.Stages)
        {
            db.Entry(stage).State = EntityState.Added;
            foreach (var role in stage.Roles) db.Entry(role).State = EntityState.Added;
        }
        bom.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return await GetAsync(caller, bom.Id, ct);
    }

    public async Task<MfgResult<bool>> DeleteDraftAsync(MfgCaller caller, Guid id, CancellationToken ct)
    {
        var bom = await db.BillsOfMaterials.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bom is null || !await ManufacturingAccess.ReachesBomAsync(db, caller, bom.ProductionLineId, ct))
            return MfgFail.NotFound<bool>("Bill of materials");
        if (!await ManufacturingAccess.CanWriteBomAsync(db, caller, bom.ProductionLineId, ct))
            return MfgFail.Forbidden<bool>("You may not write recipes for this line.");
        if (bom.Status != BomStatus.Draft)
            return MfgFail.Conflict<bool>("bom_frozen", "Only a draft can be deleted; an approved recipe is retired instead.");
        db.BillsOfMaterials.Remove(bom);
        await db.SaveChangesAsync(ct);
        return MfgResult<bool>.Done(true);
    }

    /// <summary>A new draft, copied whole from a version: the way an approved recipe is changed.</summary>
    public async Task<MfgResult<BomDto>> NewVersionAsync(MfgCaller caller, Guid id, CancellationToken ct)
    {
        var source = await LoadAsync(id, tracking: false, ct);
        if (source is null || !await ManufacturingAccess.ReachesBomAsync(db, caller, source.ProductionLineId, ct))
            return MfgFail.NotFound<BomDto>("Bill of materials");
        if (!await ManufacturingAccess.CanWriteBomAsync(db, caller, source.ProductionLineId, ct))
            return MfgFail.Forbidden<BomDto>("You may not write recipes for this line.");
        if (await DraftExistsAsync(source.ProductId, source.ProductionLineId, null, ct))
            return MfgFail.Conflict<BomDto>("draft_exists", "This product already has a draft recipe for this line; finish or delete it first.");

        var copy = new BillOfMaterials
        {
            ProductId = source.ProductId,
            ProductionLineId = source.ProductionLineId,
            BatchQuantity = source.BatchQuantity,
            BatchUnitId = source.BatchUnitId,
            StageTemplateId = source.StageTemplateId,
            Notes = source.Notes,
            CreatedBy = caller.Name,
            Lines = source.Lines.OrderBy(l => l.Sequence).Select(l => new BomLine
            {
                Sequence = l.Sequence, ComponentProductId = l.ComponentProductId, Quantity = l.Quantity, UnitId = l.UnitId,
                WastePercent = l.WastePercent, Specification = l.Specification, Note = l.Note,
                Specs = l.Specs.Select(s => new BomLineSpec { AttributeId = s.AttributeId, Value = s.Value, UnitId = s.UnitId, Text = s.Text }).ToList(),
            }).ToList(),
            Stages = source.Stages.OrderBy(s => s.Sequence).Select(s => new BomStage
            {
                Sequence = s.Sequence, NameAr = s.NameAr, NameFr = s.NameFr, NameEn = s.NameEn,
                EstimatedMinutes = s.EstimatedMinutes, ExpectedWorkers = s.ExpectedWorkers,
                IsQualityCheckpoint = s.IsQualityCheckpoint, QualityNote = s.QualityNote, IsOutput = s.IsOutput,
                Roles = s.Roles.Select(r => new BomStageRole { JobRoleId = r.JobRoleId, Count = r.Count }).ToList(),
            }).ToList(),
        };
        await using var scope = await db.BeginWriteScopeAsync(ct);
        copy.Version = await NextVersionAsync(copy.ProductId, copy.ProductionLineId, ct);
        db.BillsOfMaterials.Add(copy);
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
        return await GetAsync(caller, copy.Id, ct);
    }

    // ---------- approval ----------

    /// <summary>
    /// Approves a draft: checked again in full, then active, and the version it replaces for the same
    /// product and line retired in the same transaction, so there is never a moment with two.
    /// </summary>
    public async Task<MfgResult<BomDto>> ActivateAsync(MfgCaller caller, Guid id, CancellationToken ct)
    {
        var bom = await LoadAsync(id, tracking: true, ct);
        if (bom is null || !await ManufacturingAccess.ReachesBomAsync(db, caller, bom.ProductionLineId, ct))
            return MfgFail.NotFound<BomDto>("Bill of materials");
        if (!await ManufacturingAccess.CanApproveBomAsync(db, caller, bom.ProductionLineId, ct))
            return MfgFail.Forbidden<BomDto>("Approving this recipe is for a manager of its line who may approve recipes.");
        if (bom.Status != BomStatus.Draft) return MfgFail.Conflict<BomDto>("not_draft", "Only a draft can be activated.");
        if (bom.Lines.Count == 0) return MfgFail.Invalid<BomDto>("lines_required", "A recipe needs at least one component.");

        var request = new UpsertBomRequest(bom.ProductId, bom.ProductionLineId, bom.BatchQuantity, bom.BatchUnitId, bom.StageTemplateId, bom.Notes,
            bom.Lines.OrderBy(l => l.Sequence).Select(l => new BomLineRequest(
                l.ComponentProductId, l.Quantity, l.UnitId, l.WastePercent, l.Specification, l.Note, SpecValues.Requests(l.Specs))).ToList(),
            []);
        var problem = await ValidateAsync(bom.Id, request, ct);
        if (problem is not null) return problem.As<BomDto>();
        if (bom.Stages.Count > 0 && bom.Stages.Count(s => s.IsOutput) != 1)
            return MfgFail.Invalid<BomDto>("one_output_stage", "Mark the one stage that produces the finished output.");

        await using var scope = await db.BeginWriteScopeAsync(ct);
        var now = DateTime.UtcNow;
        var previous = await db.BillsOfMaterials
            .Where(b => b.ProductId == bom.ProductId && b.ProductionLineId == bom.ProductionLineId && b.Status == BomStatus.Active && b.Id != bom.Id)
            .ToListAsync(ct);
        foreach (var old in previous)
        {
            old.Status = BomStatus.Retired;
            old.RetiredAtUtc = now;
            old.RetiredBy = caller.Name;
            old.UpdatedAtUtc = now;
        }
        bom.Status = BomStatus.Active;
        bom.ActivatedAtUtc = now;
        bom.ActivatedBy = caller.Name;
        bom.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
        return await GetAsync(caller, bom.Id, ct);
    }

    /// <summary>Withdraws an active version with nothing in its place. It stays readable.</summary>
    public async Task<MfgResult<BomDto>> RetireAsync(MfgCaller caller, Guid id, CancellationToken ct)
    {
        var bom = await db.BillsOfMaterials.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bom is null || !await ManufacturingAccess.ReachesBomAsync(db, caller, bom.ProductionLineId, ct))
            return MfgFail.NotFound<BomDto>("Bill of materials");
        if (!await ManufacturingAccess.CanApproveBomAsync(db, caller, bom.ProductionLineId, ct))
            return MfgFail.Forbidden<BomDto>("Retiring this recipe is for a manager of its line who may approve recipes.");
        if (bom.Status != BomStatus.Active) return MfgFail.Conflict<BomDto>("not_active", "Only an active recipe can be retired.");
        bom.Status = BomStatus.Retired;
        bom.RetiredAtUtc = DateTime.UtcNow;
        bom.RetiredBy = caller.Name;
        bom.UpdatedAtUtc = bom.RetiredAtUtc.Value;
        await db.SaveChangesAsync(ct);
        return await GetAsync(caller, bom.Id, ct);
    }

    // ---------- requirements ----------

    /// <summary>
    /// What a planned quantity of the product needs, from this version: the planned quantity converted to
    /// batches, then for each component its net quantity, the expected waste on top, and the sum in the
    /// component's base unit — the unit its stock is counted in. Every conversion is explicit; one that
    /// cannot be made refuses the whole calculation in words rather than leaving a figure out.
    /// </summary>
    public async Task<MfgResult<BomRequirementsDto>> RequirementsAsync(MfgCaller caller, Guid id, decimal quantity, Guid? unitId, CancellationToken ct)
    {
        var bom = await LoadAsync(id, tracking: false, ct);
        if (bom is null || !await ManufacturingAccess.ReachesBomAsync(db, caller, bom.ProductionLineId, ct))
            return MfgFail.NotFound<BomRequirementsDto>("Bill of materials");
        if (quantity <= 0 || quantity > MaxQuantity)
            return MfgFail.Invalid<BomRequirementsDto>("invalid_quantity", "The planned quantity must be above zero.");
        if (bom.BatchQuantity <= 0)
            return MfgFail.Invalid<BomRequirementsDto>("invalid_batch", "The recipe's batch quantity must be above zero.");

        var units = await db.UnitsOfMeasure.AsNoTracking().ToDictionaryAsync(u => u.Id, ct);
        var plannedUnitId = unitId ?? bom.BatchUnitId;
        if (!units.TryGetValue(plannedUnitId, out var plannedUnit) || !units.TryGetValue(bom.BatchUnitId, out var batchUnit))
            return MfgFail.NotFound<BomRequirementsDto>("Unit");

        var outputContext = await settings.ProductContextAsync(bom.ProductId, ct);
        decimal? toBatch = outputContext?.Base is UnitConversions.UnitInfo outputBase
            ? UnitConversions.Factor(UnitConversions.Info(plannedUnit), UnitConversions.Info(batchUnit), outputBase, outputContext.Value.Units)
            : UnitConversions.StandardFactor(UnitConversions.Info(plannedUnit), UnitConversions.Info(batchUnit));
        if (toBatch is not decimal plannedToBatch)
            return MfgFail.Invalid<BomRequirementsDto>("no_conversion",
                $"There is no conversion from {plannedUnit.Code} to the recipe's batch unit {batchUnit.Code}.");
        var batches = quantity * plannedToBatch / bom.BatchQuantity;

        var lines = new List<BomRequirementLineDto>();
        foreach (var line in bom.Lines.OrderBy(l => l.Sequence))
        {
            var component = line.ComponentProduct!;
            var context = await settings.ProductContextAsync(component.Id, ct);
            if (context?.Base is not UnitConversions.UnitInfo componentBase || !units.TryGetValue(line.UnitId, out var lineUnit))
                return MfgFail.Invalid<BomRequirementsDto>("component_no_base_unit", $"{component.Sku} has no base unit.");
            if (UnitConversions.FactorToBase(UnitConversions.Info(lineUnit), componentBase, context.Value.Units) is not decimal toBase)
                return MfgFail.Invalid<BomRequirementsDto>("no_conversion",
                    $"There is no conversion from {lineUnit.Code} to {componentBase.Code} for {component.Sku}.");

            var net = line.Quantity * batches;
            var waste = net * line.WastePercent / 100m;
            var gross = net + waste;
            lines.Add(new BomRequirementLineDto(
                line.Id, component.Id, component.Sku, component.NameAr, component.NameFr, component.NameEn,
                lineUnit.Id, lineUnit.Code, Round(net), Round(waste), Round(gross),
                componentBase.Id, componentBase.Code, UnitConversions.Apply(gross, toBase)));
        }

        var totals = lines.GroupBy(l => (l.ComponentProductId, l.ComponentSku, l.BaseUnitId, l.BaseUnitCode))
            .Select(g => new BomRequirementTotalDto(g.Key.ComponentProductId, g.Key.ComponentSku, g.Key.BaseUnitId, g.Key.BaseUnitCode,
                g.Sum(l => l.BaseQuantity)))
            .OrderBy(t => t.ComponentSku, StringComparer.Ordinal).ToList();
        return MfgResult<BomRequirementsDto>.Done(new BomRequirementsDto(
            bom.Id, quantity, plannedUnit.Id, plannedUnit.Code, Round(batches), lines, totals));
    }

    private static decimal Round(decimal value) => Math.Round(value, UnitConversions.Places, MidpointRounding.AwayFromZero);

    // ---------- shared ----------

    private Task<bool> DraftExistsAsync(Guid productId, Guid? lineId, Guid? except, CancellationToken ct) =>
        db.BillsOfMaterials.AnyAsync(b => b.ProductId == productId && b.ProductionLineId == lineId
            && b.Status == BomStatus.Draft && b.Id != (except ?? Guid.Empty), ct);

    /// <summary>
    /// The next version for a product and line, drawn under the version lock so two drafts saved at the
    /// same moment cannot take the same number (neither engine's unique index covers a recipe with no line).
    /// </summary>
    private async Task<int> NextVersionAsync(Guid productId, Guid? lineId, CancellationToken ct)
    {
        await db.LockVersionsAsync(ct);
        var versions = await db.BillsOfMaterials.Where(b => b.ProductId == productId && b.ProductionLineId == lineId)
            .Select(b => b.Version).ToListAsync(ct);
        return versions.Count == 0 ? 1 : versions.Max() + 1;
    }

    /// <summary>Validates the request and writes it onto a draft. Null when it was written.</summary>
    private async Task<MfgResult<bool>?> ApplyAsync(BillOfMaterials bom, UpsertBomRequest r, CancellationToken ct)
    {
        var problem = await ValidateAsync(bom.Id, r, ct);
        if (problem is not null) return problem;

        IReadOnlyList<StageRequest> stages = r.Stages ?? [];
        if (r.Stages is null && r.StageTemplateId is Guid templateId)
        {
            var template = await db.StageTemplates.AsNoTracking().Include(t => t.Steps).ThenInclude(s => s.Roles)
                .AsSplitQuery().FirstOrDefaultAsync(t => t.Id == templateId, ct);
            if (template is null || !template.IsActive) return MfgFail.Invalid<bool>("unknown_template", "The stage template does not exist or is switched off.");
            if (template.ProductId is Guid meant && meant != r.ProductId)
                return MfgFail.Invalid<bool>("template_other_product", "This stage template is meant for another product.");
            stages = template.Steps.OrderBy(s => s.Sequence).Select(s => new StageRequest(
                s.NameAr, s.NameFr, s.NameEn, s.EstimatedMinutes, s.ExpectedWorkers, s.IsQualityCheckpoint, s.QualityNote, false,
                s.Roles.Select(x => new StageRoleRequest(x.JobRoleId, x.Count)).ToList())).ToList();
        }
        else if (r.StageTemplateId is Guid named && !await db.StageTemplates.AnyAsync(t => t.Id == named, ct))
            return MfgFail.Invalid<bool>("unknown_template", "The stage template does not exist.");
        var stageProblem = await settings.ValidateStagesAsync(stages, isBom: true, ct);
        if (stageProblem is not null) return stageProblem;

        bom.BatchQuantity = r.BatchQuantity;
        bom.BatchUnitId = r.BatchUnitId;
        bom.StageTemplateId = r.StageTemplateId;
        bom.Notes = MfgText.Clean(r.Notes);
        var sequence = 0;
        foreach (var l in r.Lines ?? [])
            bom.Lines.Add(new BomLine
            {
                Sequence = ++sequence,
                ComponentProductId = l.ComponentProductId,
                Quantity = l.Quantity,
                UnitId = l.UnitId,
                WastePercent = l.WastePercent,
                Specification = MfgText.Clean(l.Specification),
                Note = MfgText.Clean(l.Note),
                Specs = SpecValues.Rows<BomLineSpec>(l.Specs),
            });
        sequence = 0;
        // With stages and none marked, the last one is the output stage: where the finished units come off.
        var output = stages.ToList().FindIndex(s => s.IsOutput);
        if (output < 0) output = stages.Count - 1;
        foreach (var (s, index) in stages.Select((s, index) => (s, index)))
            bom.Stages.Add(new BomStage
            {
                Sequence = ++sequence,
                NameAr = s.NameAr.Trim(),
                NameFr = s.NameFr.Trim(),
                NameEn = MfgText.Clean(s.NameEn) ?? s.NameFr.Trim(),
                EstimatedMinutes = s.EstimatedMinutes,
                ExpectedWorkers = s.ExpectedWorkers,
                IsQualityCheckpoint = s.IsQualityCheckpoint,
                QualityNote = s.IsQualityCheckpoint ? MfgText.Clean(s.QualityNote) : null,
                IsOutput = index == output,
                Roles = (s.Roles ?? []).Select(x => new BomStageRole { JobRoleId = x.JobRoleId, Count = x.Count }).ToList(),
            });
        return null;
    }

    /// <summary>
    /// Everything a recipe must be: a semi-finished or finished output with a base unit, on a line that
    /// makes it; a batch above zero in a unit that converts to the output's; components that a recipe may
    /// consume, each with a base unit its quantity converts to, none of them the output itself or made
    /// from it; waste from 0 to under 100 %.
    /// </summary>
    private async Task<MfgResult<bool>?> ValidateAsync(Guid bomId, UpsertBomRequest r, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == r.ProductId && !p.IsDeleted, ct);
        if (product is null) return MfgFail.NotFound<bool>("Product");
        if (!ManufacturingRoles.IsProduced(product.ManufacturingRole))
            return MfgFail.Invalid<bool>("not_produced", $"{product.Sku} is not marked semi-finished or finished.");
        if (r.ProductionLineId is Guid lineId)
        {
            var line = await db.ProductionLines.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lineId, ct);
            if (line is null) return MfgFail.NotFound<bool>("Production line");
            if (!await db.ProductionLineProducts.AnyAsync(p => p.LineId == lineId && p.ProductId == r.ProductId, ct))
                return MfgFail.Invalid<bool>("product_not_on_line", $"Add {product.Sku} to the line's products before writing its recipe for that line.");
        }
        if (MfgText.Optional(r.Notes, 1000, "The notes") is string notes) return MfgFail.Invalid<bool>("invalid_input", notes);

        var units = await db.UnitsOfMeasure.AsNoTracking().ToDictionaryAsync(u => u.Id, ct);
        if (r.BatchQuantity <= 0 || r.BatchQuantity > MaxQuantity)
            return MfgFail.Invalid<bool>("invalid_batch", "The batch quantity must be above zero.");
        if (!units.TryGetValue(r.BatchUnitId, out var batchUnit)) return MfgFail.NotFound<bool>("Unit");
        var outputContext = await settings.ProductContextAsync(product.Id, ct);
        if (outputContext?.Base is not UnitConversions.UnitInfo outputBase)
            return MfgFail.Invalid<bool>("product_no_base_unit", $"Set {product.Sku}'s base unit first.");
        if (UnitConversions.FactorToBase(UnitConversions.Info(batchUnit), outputBase, outputContext.Value.Units) is null)
            return MfgFail.Invalid<bool>("no_conversion", $"There is no conversion from {batchUnit.Code} to {outputBase.Code} for {product.Sku}.");

        var lines = r.Lines ?? [];
        if (lines.Count > MaxLines) return MfgFail.Invalid<bool>("too_many_lines", $"Up to {MaxLines} components.");
        var componentIds = lines.Select(l => l.ComponentProductId).Distinct().ToList();
        var components = await db.Products.AsNoTracking().Where(p => componentIds.Contains(p.Id) && !p.IsDeleted).ToDictionaryAsync(p => p.Id, ct);
        var number = 0;
        foreach (var l in lines)
        {
            number++;
            if (!components.TryGetValue(l.ComponentProductId, out var component))
                return MfgFail.Invalid<bool>("unknown_component", $"Component {number} does not exist.");
            if (component.Id == product.Id)
                return MfgFail.Invalid<bool>("bom_cycle", $"{product.Sku} cannot be a component of itself.");
            if (!ManufacturingRoles.IsComponent(component.ManufacturingRole))
                return MfgFail.Invalid<bool>("not_component",
                    $"{component.Sku} is not marked as a raw material, consumable, accessory, spare part or semi-finished product.");
            if (!component.IsActive) return MfgFail.Invalid<bool>("component_inactive", $"{component.Sku} is switched off.");
            if (l.Quantity <= 0 || l.Quantity > MaxQuantity)
                return MfgFail.Invalid<bool>("invalid_quantity", $"{component.Sku}'s quantity must be above zero.");
            if (l.WastePercent < 0 || l.WastePercent >= 100)
                return MfgFail.Invalid<bool>("invalid_waste", $"{component.Sku}'s expected waste must be from 0 to under 100 %.");
            var text = MfgText.First(
                MfgText.Optional(l.Specification, 400, $"{component.Sku}'s specification"),
                MfgText.Optional(l.Note, 400, $"{component.Sku}'s note"));
            if (text is not null) return MfgFail.Invalid<bool>("invalid_input", text);
            if (await SpecValues.ValidateAsync(db, l.Specs, component.Sku, ct) is { } specProblem) return specProblem;
            if (!units.TryGetValue(l.UnitId, out var lineUnit)) return MfgFail.NotFound<bool>("Unit");
            var context = await settings.ProductContextAsync(component.Id, ct);
            if (context?.Base is not UnitConversions.UnitInfo componentBase)
                return MfgFail.Invalid<bool>("component_no_base_unit", $"Set {component.Sku}'s base unit first.");
            if (UnitConversions.FactorToBase(UnitConversions.Info(lineUnit), componentBase, context.Value.Units) is null)
                return MfgFail.Invalid<bool>("no_conversion",
                    $"{lineUnit.Code} does not convert to {componentBase.Code}, {component.Sku}'s base unit. Choose another unit or add the factor on the product.");
        }

        if (await MakesItselfAsync(product.Id, componentIds, ct) is string through)
            return MfgFail.Invalid<bool>("bom_cycle", $"{through} is made from {product.Sku}, so it cannot also go into it.");
        return null;
    }

    /// <summary>
    /// Whether a component is made — through any chain of active recipes — from the product itself. Names
    /// the component that closes the loop, or null.
    /// </summary>
    private async Task<string?> MakesItselfAsync(Guid productId, IReadOnlyCollection<Guid> componentIds, CancellationToken ct)
    {
        var recipes = (await db.BomLines.AsNoTracking().Where(l => l.Bom!.Status == BomStatus.Active)
                .Select(l => new { l.Bom!.ProductId, l.ComponentProductId }).ToListAsync(ct))
            .GroupBy(x => x.ProductId).ToDictionary(g => g.Key, g => g.Select(x => x.ComponentProductId).ToList());
        foreach (var start in componentIds)
        {
            var seen = new HashSet<Guid>();
            var stack = new Stack<Guid>([start]);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current == productId)
                    return await db.Products.AsNoTracking().Where(p => p.Id == start).Select(p => p.Sku).FirstOrDefaultAsync(ct) ?? "A component";
                if (!seen.Add(current)) continue;
                foreach (var next in recipes.GetValueOrDefault(current, [])) stack.Push(next);
            }
        }
        return null;
    }
}
