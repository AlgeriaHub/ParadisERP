using ParadiERP.Application.Contracts;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Manufacturing;

/// <summary>
/// The manufacturing settings (Phase 1 §3, §6, §7): units and their conversions, each product's
/// manufacturing role and units, the job roles and the stage templates. Company-wide data, like the
/// product catalogue: a line does not own a unit or a job role.
/// </summary>
public sealed class ManufacturingSettings(PosDbContext db)
{
    private const decimal MaxFactor = 1_000_000_000m;

    // ---------- units ----------

    public async Task<List<UnitDto>> UnitsAsync(CancellationToken ct) =>
        (await db.UnitsOfMeasure.AsNoTracking().ToListAsync(ct))
            .OrderBy(u => u.Dimension).ThenBy(u => u.FactorToReference).ThenBy(u => u.Code, StringComparer.Ordinal)
            .Select(ToDto).ToList();

    public static UnitDto ToDto(UnitOfMeasure u) =>
        new(u.Id, u.Code, u.NameAr, u.NameFr, u.NameEn, u.Dimension.ToString(), u.FactorToReference, u.IsSystem, u.IsActive);

    public async Task<MfgResult<UnitDto>> SaveUnitAsync(Guid? id, UpsertUnitRequest r, CancellationToken ct)
    {
        UnitOfMeasure? unit = null;
        if (id is Guid existing)
        {
            unit = await db.UnitsOfMeasure.FirstOrDefaultAsync(u => u.Id == existing, ct);
            if (unit is null) return MfgFail.NotFound<UnitDto>("Unit");
        }

        var names = MfgText.First(
            MfgText.Required(r.NameAr, 80, "The Arabic name"),
            MfgText.Required(r.NameFr, 80, "The French name"),
            MfgText.Optional(r.NameEn, 80, "The English name"));
        if (names is not null) return MfgFail.Invalid<UnitDto>("invalid_name", names);

        if (unit is { IsSystem: true })
        {
            // A system unit's meaning is fixed for good: 1 kg is 1,000 g in every company and forever.
            if (!r.IsActive) return MfgFail.Invalid<UnitDto>("system_unit_fixed", "A system unit stays active; only its names may be edited.");
            SetNames(unit, r);
            unit.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return MfgResult<UnitDto>.Done(ToDto(unit));
        }

        var code = MfgText.Code(r.Code, keepCase: true);
        if (code.Length is 0 or > 16) return MfgFail.Invalid<UnitDto>("invalid_code", "The unit's code is required, up to 16 characters.");
        if (!Enum.TryParse<UnitDimension>(r.Dimension, true, out var dimension) || !Enum.IsDefined(dimension))
            return MfgFail.Invalid<UnitDto>("invalid_dimension", "Choose what the unit measures.");
        var factor = dimension == UnitDimension.Other ? 1m : r.FactorToReference;
        if (factor <= 0 || factor > MaxFactor)
            return MfgFail.Invalid<UnitDto>("invalid_factor", "The factor to the reference unit must be above zero.");

        var lower = code.ToLowerInvariant();
        var taken = (await db.UnitsOfMeasure.AsNoTracking().Where(u => u.Id != (id ?? Guid.Empty)).Select(u => u.Code).ToListAsync(ct))
            .Any(c => c.ToLowerInvariant() == lower);
        if (taken) return MfgFail.Conflict<UnitDto>("code_taken", $"Another unit already has the code '{code}'.");

        if (unit is not null && (unit.Dimension != dimension || unit.FactorToReference != factor || unit.Code != code)
            && await UnitInUseAsync(unit.Id, ct))
            return MfgFail.Conflict<UnitDto>("unit_in_use",
                "This unit is already used by a product or a recipe; its code, dimension and factor can no longer change.");

        unit ??= db.UnitsOfMeasure.Add(new UnitOfMeasure()).Entity;
        unit.Code = code;
        unit.Dimension = dimension;
        unit.FactorToReference = factor;
        unit.IsActive = r.IsActive;
        SetNames(unit, r);
        unit.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return MfgResult<UnitDto>.Done(ToDto(unit));
    }

    private static void SetNames(UnitOfMeasure unit, UpsertUnitRequest r)
    {
        unit.NameAr = r.NameAr.Trim();
        unit.NameFr = r.NameFr.Trim();
        unit.NameEn = MfgText.Clean(r.NameEn) ?? unit.NameFr;
    }

    private async Task<bool> UnitInUseAsync(Guid unitId, CancellationToken ct) =>
        await db.Products.AnyAsync(p => p.BaseUnitId == unitId, ct)
        || await db.ProductUnits.AnyAsync(u => u.UnitId == unitId, ct)
        || await db.BomLines.AnyAsync(l => l.UnitId == unitId, ct)
        || await db.BillsOfMaterials.AnyAsync(b => b.BatchUnitId == unitId, ct)
        || await db.ProductSpecs.AnyAsync(s => s.UnitId == unitId, ct)
        || await db.BomLineSpecs.AnyAsync(s => s.UnitId == unitId, ct);

    // ---------- specification fields ----------

    public async Task<List<SpecAttributeDto>> SpecAttributesAsync(CancellationToken ct)
    {
        var used = (await db.ProductSpecs.AsNoTracking().Select(s => s.AttributeId).Distinct().ToListAsync(ct))
            .Concat(await db.BomLineSpecs.AsNoTracking().Select(s => s.AttributeId).Distinct().ToListAsync(ct))
            .ToHashSet();
        return (await db.SpecAttributes.AsNoTracking().ToListAsync(ct))
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Code, StringComparer.Ordinal)
            .Select(a => ToDto(a, used.Contains(a.Id))).ToList();
    }

    private static SpecAttributeDto ToDto(SpecAttribute a, bool inUse) => new(
        a.Id, a.Code, a.NameAr, a.NameFr, a.NameEn, a.Kind.ToString(), a.Dimension.ToString(), a.IsSystem, a.IsActive, a.SortOrder, inUse);

    /// <summary>
    /// Adds or edits a specification field. A system field keeps its code, kind and dimension; a field a
    /// value already uses keeps its kind and dimension, because changing either would change what every
    /// figure written with it means.
    /// </summary>
    public async Task<MfgResult<SpecAttributeDto>> SaveSpecAttributeAsync(Guid? id, UpsertSpecAttributeRequest r, CancellationToken ct)
    {
        SpecAttribute? field = null;
        if (id is Guid existing)
        {
            field = await db.SpecAttributes.FirstOrDefaultAsync(a => a.Id == existing, ct);
            if (field is null) return MfgFail.NotFound<SpecAttributeDto>("Specification field");
        }
        var names = MfgText.First(
            MfgText.Required(r.NameAr, 80, "The Arabic name"),
            MfgText.Required(r.NameFr, 80, "The French name"),
            MfgText.Optional(r.NameEn, 80, "The English name"),
            r.SortOrder is < 0 or > 999 ? "The order is from 0 to 999." : null);
        if (names is not null) return MfgFail.Invalid<SpecAttributeDto>("invalid_name", names);
        var inUse = field is not null && await SpecAttributeInUseAsync(field.Id, ct);

        if (field is not { IsSystem: true })
        {
            var code = MfgText.Code(r.Code);
            if (!System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Z0-9][A-Z0-9_-]{0,15}$"))
                return MfgFail.Invalid<SpecAttributeDto>("invalid_code", "The code is required: up to 16 letters, digits, - or _.");
            if (!Enum.TryParse<SpecKind>(r.Kind, true, out var kind) || !Enum.IsDefined(kind))
                return MfgFail.Invalid<SpecAttributeDto>("invalid_kind", "Choose whether the field holds a measurement or a word.");
            var dimension = UnitDimension.Other;
            if (kind == SpecKind.Measure
                && (!Enum.TryParse(r.Dimension, true, out dimension) || !Enum.IsDefined(dimension)))
                return MfgFail.Invalid<SpecAttributeDto>("invalid_dimension", "Choose what the measurement measures.");
            if (await db.SpecAttributes.AnyAsync(a => a.Code == code && a.Id != (id ?? Guid.Empty), ct))
                return MfgFail.Conflict<SpecAttributeDto>("code_taken", $"Another specification field already has the code '{code}'.");
            if (field is not null && inUse && (field.Kind != kind || field.Dimension != dimension))
                return MfgFail.Conflict<SpecAttributeDto>("spec_field_in_use",
                    "Values already use this field; what it measures can no longer change.");

            field ??= db.SpecAttributes.Add(new SpecAttribute()).Entity;
            field.Code = code;
            field.Kind = kind;
            field.Dimension = dimension;
        }
        field.NameAr = r.NameAr.Trim();
        field.NameFr = r.NameFr.Trim();
        field.NameEn = MfgText.Clean(r.NameEn) ?? field.NameFr;
        field.IsActive = r.IsActive;
        field.SortOrder = r.SortOrder;
        field.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return MfgResult<SpecAttributeDto>.Done(ToDto(field, inUse));
    }

    private async Task<bool> SpecAttributeInUseAsync(Guid id, CancellationToken ct) =>
        await db.ProductSpecs.AnyAsync(s => s.AttributeId == id, ct) || await db.BomLineSpecs.AnyAsync(s => s.AttributeId == id, ct);

    /// <summary>
    /// A conversion asked for on its own (the settings screen's calculator): by the standard route, or
    /// through one product's base and alternate units when a product is named.
    /// </summary>
    public async Task<MfgResult<UnitConversionDto>> ConvertAsync(decimal quantity, Guid fromId, Guid toId, Guid? productId, CancellationToken ct)
    {
        var units = await db.UnitsOfMeasure.AsNoTracking().Where(u => u.Id == fromId || u.Id == toId).ToListAsync(ct);
        var from = units.FirstOrDefault(u => u.Id == fromId);
        var to = units.FirstOrDefault(u => u.Id == toId);
        if (from is null || to is null) return MfgFail.NotFound<UnitConversionDto>("Unit");

        decimal? factor;
        if (productId is Guid pid)
        {
            var context = await ProductContextAsync(pid, ct);
            if (context is null) return MfgFail.NotFound<UnitConversionDto>("Product");
            factor = context.Value.Base is UnitConversions.UnitInfo baseUnit
                ? UnitConversions.Factor(UnitConversions.Info(from), UnitConversions.Info(to), baseUnit, context.Value.Units)
                : UnitConversions.StandardFactor(UnitConversions.Info(from), UnitConversions.Info(to));
        }
        else factor = UnitConversions.StandardFactor(UnitConversions.Info(from), UnitConversions.Info(to));

        if (factor is not decimal f)
            return MfgFail.Invalid<UnitConversionDto>("no_conversion",
                $"There is no conversion from {from.Code} to {to.Code}" + (productId is null ? "." : " for this product."));
        return MfgResult<UnitConversionDto>.Done(new UnitConversionDto(quantity, fromId, toId, UnitConversions.Apply(quantity, f), f));
    }

    /// <summary>A product's base unit and alternate-unit factors, for converting its quantities. Null: no such product.</summary>
    public async Task<(UnitConversions.UnitInfo? Base, UnitConversions.ProductUnits Units)?> ProductContextAsync(Guid productId, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking().Where(p => p.Id == productId && !p.IsDeleted)
            .Select(p => new { p.BaseUnitId }).FirstOrDefaultAsync(ct);
        if (product is null) return null;
        var factors = await db.ProductUnits.AsNoTracking().Where(u => u.ProductId == productId)
            .ToDictionaryAsync(u => u.UnitId, u => u.FactorToBase, ct);
        UnitConversions.UnitInfo? baseUnit = null;
        if (product.BaseUnitId is Guid b && await db.UnitsOfMeasure.AsNoTracking().FirstOrDefaultAsync(u => u.Id == b, ct) is { } unit)
            baseUnit = UnitConversions.Info(unit);
        return (baseUnit, new UnitConversions.ProductUnits(product.BaseUnitId, factors));
    }

    // ---------- materials: each product's role and units ----------

    public async Task<List<MaterialDto>> MaterialsAsync(string? role, string? search, bool includeInactive, CancellationToken ct)
    {
        var query = db.Products.AsNoTracking().Where(p => !p.IsDeleted);
        if (!includeInactive) query = query.Where(p => p.IsActive);
        if (!string.IsNullOrWhiteSpace(role))
        {
            if (!Enum.TryParse<ManufacturingRole>(role, true, out var wanted)) return [];
            query = query.Where(p => p.ManufacturingRole == wanted);
        }
        var products = await query.ToListAsync(ct);
        if (MfgText.Clean(search) is string needle)
        {
            var n = needle.ToLowerInvariant();
            products = products.Where(p => $"{p.Sku} {p.Barcode} {p.NameAr} {p.NameFr} {p.NameEn}".ToLowerInvariant().Contains(n)).ToList();
        }
        return await MaterialDtosAsync(products, ct);
    }

    public async Task<MfgResult<MaterialDto>> MaterialAsync(Guid productId, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId && !p.IsDeleted, ct);
        if (product is null) return MfgFail.NotFound<MaterialDto>("Product");
        return MfgResult<MaterialDto>.Done((await MaterialDtosAsync([product], ct))[0]);
    }

    private async Task<List<MaterialDto>> MaterialDtosAsync(List<Product> products, CancellationToken ct)
    {
        var ids = products.Select(p => p.Id).ToList();
        var units = await db.UnitsOfMeasure.AsNoTracking().ToDictionaryAsync(u => u.Id, ct);
        var alternates = (await db.ProductUnits.AsNoTracking().Where(u => ids.Contains(u.ProductId)).ToListAsync(ct))
            .GroupBy(u => u.ProductId).ToDictionary(g => g.Key, g => g.ToList());
        var specs = (await db.ProductSpecs.AsNoTracking().Where(s => ids.Contains(s.ProductId)).ToListAsync(ct))
            .GroupBy(s => s.ProductId).ToDictionary(g => g.Key, g => g.ToList());
        var names = await SpecValues.NamesAsync(db, ct);
        return products.OrderBy(p => p.Sku, StringComparer.Ordinal).Select(p => new MaterialDto(
            p.Id, p.Sku, p.NameAr, p.NameFr, p.NameEn, p.IsActive, p.IsRentable,
            p.ManufacturingRole.ToString(), ManufacturingRoles.ForSale(p),
            p.BaseUnitId, p.BaseUnitId is Guid b && units.TryGetValue(b, out var bu) ? bu.Code : null, p.Unit,
            alternates.GetValueOrDefault(p.Id, [])
                .Select(a => new ProductUnitDto(a.UnitId, units.TryGetValue(a.UnitId, out var u) ? u.Code : "?", a.FactorToBase, a.ForPurchase, a.ForProduction, a.ForSale))
                .OrderBy(a => a.UnitCode, StringComparer.Ordinal).ToList(),
            p.AlsoForSale, SpecValues.Dtos(specs.GetValueOrDefault(p.Id, []), names))).ToList();
    }

    /// <summary>
    /// Sets a product's manufacturing role, base unit and alternate units, whole. Refused where the change
    /// would make something already defined wrong: a role a recipe or a line no longer accepts, a base unit
    /// changed under stock or a recipe, an alternate unit a recipe uses, a product factor that contradicts
    /// the standard one. The product gets a new version, so every till re-pulls it and hides or shows it.
    /// A change to whether it is for sale — its role or its sold-as-well mark — is audited with who made it
    /// (<see cref="ProductSaleStatusChange"/>), in the same save.
    /// </summary>
    public async Task<MfgResult<MaterialDto>> ClassifyAsync(
        Guid productId, ClassifyMaterialRequest r, Guid? userId, string? userName, CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == productId && !p.IsDeleted, ct);
        if (product is null) return MfgFail.NotFound<MaterialDto>("Product");
        if (!Enum.TryParse<ManufacturingRole>(r.Role, true, out var role) || !Enum.IsDefined(role))
            return MfgFail.Invalid<MaterialDto>("invalid_role", "Choose the product's manufacturing role.");

        // ---- the role, against what already names the product ----
        if (role != product.ManufacturingRole)
        {
            if (!ManufacturingRoles.IsProduced(role)
                && (await db.BillsOfMaterials.AnyAsync(b => b.ProductId == productId, ct)
                    || await db.ProductionLineProducts.AnyAsync(l => l.ProductId == productId, ct)
                    || await db.StageTemplates.AnyAsync(t => t.ProductId == productId, ct)))
                return MfgFail.Conflict<MaterialDto>("role_in_use",
                    "A recipe, a production line or a stage template makes this product, so it must stay semi-finished or finished.");
            if (!ManufacturingRoles.IsComponent(role) && await db.BomLines.AnyAsync(l => l.ComponentProductId == productId, ct))
                return MfgFail.Conflict<MaterialDto>("role_in_use",
                    "A recipe uses this product as a component, so its role must stay one a recipe can consume.");
        }

        // ---- the base unit ----
        UnitOfMeasure? baseUnit = null;
        if (r.BaseUnitId is Guid baseId)
        {
            baseUnit = await db.UnitsOfMeasure.FirstOrDefaultAsync(u => u.Id == baseId, ct);
            if (baseUnit is null) return MfgFail.NotFound<MaterialDto>("Unit");
            if (!baseUnit.IsActive && product.BaseUnitId != baseId)
                return MfgFail.Invalid<MaterialDto>("unit_inactive", $"The unit {baseUnit.Code} is switched off.");
        }
        if (product.BaseUnitId is Guid current && current != r.BaseUnitId && await BaseUnitLockedAsync(productId, ct))
            return MfgFail.Conflict<MaterialDto>("base_unit_locked",
                "This product already has stock, movements or a recipe counted in its base unit; changing the unit would change the meaning of every quantity.");

        // ---- the alternate units ----
        var wanted = r.Units ?? [];
        if (wanted.Count > 0 && baseUnit is null)
            return MfgFail.Invalid<MaterialDto>("base_unit_required", "Choose the base unit before adding other units.");
        if (wanted.GroupBy(u => u.UnitId).Any(g => g.Count() > 1))
            return MfgFail.Invalid<MaterialDto>("duplicate_unit", "A unit is listed twice.");
        var unitIds = wanted.Select(u => u.UnitId).ToList();
        var unitRows = await db.UnitsOfMeasure.Where(u => unitIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
        var existing = await db.ProductUnits.Where(u => u.ProductId == productId).ToListAsync(ct);
        foreach (var w in wanted)
        {
            if (!unitRows.TryGetValue(w.UnitId, out var unit)) return MfgFail.NotFound<MaterialDto>("Unit");
            if (unit.Id == baseUnit!.Id)
                return MfgFail.Invalid<MaterialDto>("duplicate_unit", $"{unit.Code} is already the base unit.");
            if (!unit.IsActive && existing.All(e => e.UnitId != unit.Id))
                return MfgFail.Invalid<MaterialDto>("unit_inactive", $"The unit {unit.Code} is switched off.");
            if (w.FactorToBase <= 0 || w.FactorToBase > MaxFactor)
                return MfgFail.Invalid<MaterialDto>("invalid_factor", $"How many {baseUnit.Code} one {unit.Code} holds must be above zero.");
            // A unit that converts by the standard route already has its factor; a different one written on
            // the product would make the same quantity mean two things.
            if (UnitConversions.StandardFactor(UnitConversions.Info(unit), UnitConversions.Info(baseUnit)) is decimal standard
                && standard != w.FactorToBase)
                return MfgFail.Invalid<MaterialDto>("factor_contradicts_standard",
                    $"1 {unit.Code} is {standard:0.##########} {baseUnit.Code} by definition; a product cannot change that.");
        }
        foreach (var gone in existing.Where(e => unitIds.All(id => id != e.UnitId)))
        {
            if (await db.BomLines.AnyAsync(l => l.ComponentProductId == productId && l.UnitId == gone.UnitId, ct)
                || await db.BillsOfMaterials.AnyAsync(b => b.ProductId == productId && b.BatchUnitId == gone.UnitId, ct))
                return MfgFail.Conflict<MaterialDto>("unit_in_use", "A recipe counts this product in a unit you removed.");
        }
        foreach (var changed in existing.Where(e => wanted.Any(w => w.UnitId == e.UnitId && w.FactorToBase != e.FactorToBase)))
        {
            if (await db.BomLines.AnyAsync(l => l.ComponentProductId == productId && l.UnitId == changed.UnitId
                    && l.Bom!.Status != BomStatus.Draft, ct)
                || await db.BillsOfMaterials.AnyAsync(b => b.ProductId == productId && b.BatchUnitId == changed.UnitId
                    && b.Status != BomStatus.Draft, ct))
                return MfgFail.Conflict<MaterialDto>("unit_in_use",
                    "An approved recipe counts this product in that unit; its factor can no longer change.");
        }

        // ---- the measured specification: sent whole, or left as it is ----
        if (await SpecValues.ValidateAsync(db, r.Specs, product.Sku, ct) is { } specProblem)
            return specProblem.As<MaterialDto>();

        // ---- write ----
        var (oldRole, oldAlsoForSale) = (product.ManufacturingRole, product.AlsoForSale);
        product.ManufacturingRole = role;
        // The sale policy's one exception is the product's own (ManufacturingRoles.ForSale); for a role the
        // counter sells anyway it means nothing, so it is not kept.
        product.AlsoForSale = !ManufacturingRoles.SoldAtTill(role) && (r.AlsoForSale ?? product.AlsoForSale);
        if (oldRole != product.ManufacturingRole || oldAlsoForSale != product.AlsoForSale)
            db.ProductSaleStatusChanges.Add(new ProductSaleStatusChange
            {
                ProductId = productId, UserId = userId, UserName = userName,
                OldRole = oldRole, NewRole = product.ManufacturingRole,
                OldAlsoForSale = oldAlsoForSale, NewAlsoForSale = product.AlsoForSale,
                OldForSale = ManufacturingRoles.ForSale(oldRole, oldAlsoForSale), NewForSale = ManufacturingRoles.ForSale(product),
            });
        if (r.Specs is not null)
        {
            db.ProductSpecs.RemoveRange(await db.ProductSpecs.Where(s => s.ProductId == productId).ToListAsync(ct));
            foreach (var row in SpecValues.Rows<ProductSpec>(r.Specs))
            {
                row.ProductId = productId;
                db.ProductSpecs.Add(row);
            }
        }
        product.BaseUnitId = baseUnit?.Id;
        // The free-text unit every screen and receipt prints follows the base unit, so they keep agreeing.
        if (baseUnit is not null) product.Unit = baseUnit.Code;
        foreach (var gone in existing.Where(e => unitIds.All(id => id != e.UnitId))) db.ProductUnits.Remove(gone);
        foreach (var w in wanted)
        {
            var row = existing.FirstOrDefault(e => e.UnitId == w.UnitId);
            if (row is null) db.ProductUnits.Add(row = new ProductUnit { ProductId = productId, UnitId = w.UnitId });
            row.FactorToBase = w.FactorToBase;
            row.ForPurchase = w.ForPurchase;
            row.ForProduction = w.ForProduction;
            row.ForSale = w.ForSale;
        }
        await db.SaveWithVersionsAsync(ct, product);
        return await MaterialAsync(productId, ct);
    }

    /// <summary>Whether a quantity has already been counted in the product's base unit.</summary>
    private async Task<bool> BaseUnitLockedAsync(Guid productId, CancellationToken ct)
    {
        var stocks = await db.ProductStocks.AsNoTracking().Where(s => s.ProductId == productId).ToListAsync(ct);
        return stocks.Any(s => s.OwnedQty != 0)
            || await db.StockMovements.AnyAsync(m => m.ProductId == productId, ct)
            || await db.BomLines.AnyAsync(l => l.ComponentProductId == productId, ct)
            || await db.BillsOfMaterials.AnyAsync(b => b.ProductId == productId, ct);
    }

    // ---------- job roles ----------

    public async Task<List<JobRoleDto>> JobRolesAsync(CancellationToken ct) =>
        (await db.ManufacturingJobRoles.AsNoTracking().ToListAsync(ct))
            .OrderBy(r => r.Code, StringComparer.Ordinal).Select(ToDto).ToList();

    public static JobRoleDto ToDto(ManufacturingJobRole r) => new(r.Id, r.Code, r.NameAr, r.NameFr, r.NameEn, r.Description, r.IsActive);

    public async Task<MfgResult<JobRoleDto>> SaveJobRoleAsync(Guid? id, UpsertJobRoleRequest r, CancellationToken ct)
    {
        ManufacturingJobRole? role = null;
        if (id is Guid existing)
        {
            role = await db.ManufacturingJobRoles.FirstOrDefaultAsync(x => x.Id == existing, ct);
            if (role is null) return MfgFail.NotFound<JobRoleDto>("Job role");
        }
        var code = MfgText.Code(r.Code);
        var problem = MfgText.First(
            code.Length is 0 or > 24 ? "The code is required, up to 24 characters." : null,
            MfgText.Required(r.NameAr, 120, "The Arabic name"),
            MfgText.Required(r.NameFr, 120, "The French name"),
            MfgText.Optional(r.NameEn, 120, "The English name"),
            MfgText.Optional(r.Description, 400, "The description"));
        if (problem is not null) return MfgFail.Invalid<JobRoleDto>("invalid_input", problem);
        if (await db.ManufacturingJobRoles.AnyAsync(x => x.Code == code && x.Id != (id ?? Guid.Empty), ct))
            return MfgFail.Conflict<JobRoleDto>("code_taken", $"Another job role already has the code '{code}'.");

        role ??= db.ManufacturingJobRoles.Add(new ManufacturingJobRole()).Entity;
        role.Code = code;
        role.NameAr = r.NameAr.Trim();
        role.NameFr = r.NameFr.Trim();
        role.NameEn = MfgText.Clean(r.NameEn) ?? role.NameFr;
        role.Description = MfgText.Clean(r.Description);
        role.IsActive = r.IsActive;
        role.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return MfgResult<JobRoleDto>.Done(ToDto(role));
    }

    // ---------- stage templates ----------

    public async Task<List<StageTemplateDto>> StageTemplatesAsync(Guid? productId, CancellationToken ct)
    {
        var query = db.StageTemplates.AsNoTracking().Include(t => t.Product).Include(t => t.Steps).ThenInclude(s => s.Roles).AsQueryable();
        // A product's own templates and the shared ones: what a recipe for that product may copy.
        if (productId is Guid pid) query = query.Where(t => t.ProductId == null || t.ProductId == pid);
        var templates = await query.AsSplitQuery().ToListAsync(ct);
        var roles = await RoleCodesAsync(ct);
        return templates.OrderBy(t => t.Code, StringComparer.Ordinal).Select(t => ToDto(t, roles)).ToList();
    }

    public async Task<MfgResult<StageTemplateDto>> StageTemplateAsync(Guid id, CancellationToken ct)
    {
        var template = await db.StageTemplates.AsNoTracking().Include(t => t.Product).Include(t => t.Steps).ThenInclude(s => s.Roles)
            .AsSplitQuery().FirstOrDefaultAsync(t => t.Id == id, ct);
        return template is null
            ? MfgFail.NotFound<StageTemplateDto>("Stage template")
            : MfgResult<StageTemplateDto>.Done(ToDto(template, await RoleCodesAsync(ct)));
    }

    internal async Task<Dictionary<Guid, string>> RoleCodesAsync(CancellationToken ct) =>
        await db.ManufacturingJobRoles.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Code, ct);

    private static StageTemplateDto ToDto(StageTemplate t, IReadOnlyDictionary<Guid, string> roles) => new(
        t.Id, t.Code, t.NameAr, t.NameFr, t.NameEn, t.Description, t.ProductId, t.Product?.Sku, t.IsActive,
        t.Steps.OrderBy(s => s.Sequence).Select(s => new StageDto(
            s.Id, s.Sequence, s.NameAr, s.NameFr, s.NameEn, s.EstimatedMinutes, s.ExpectedWorkers,
            s.IsQualityCheckpoint, s.QualityNote, false,
            s.Roles.Select(r => new StageRoleDto(r.JobRoleId, roles.GetValueOrDefault(r.JobRoleId, "?"), r.Count))
                .OrderBy(r => r.JobRoleCode, StringComparer.Ordinal).ToList())).ToList());

    public async Task<MfgResult<StageTemplateDto>> SaveStageTemplateAsync(Guid? id, UpsertStageTemplateRequest r, CancellationToken ct)
    {
        StageTemplate? template = null;
        if (id is Guid existing)
        {
            template = await db.StageTemplates.Include(t => t.Steps).ThenInclude(s => s.Roles).AsSplitQuery()
                .FirstOrDefaultAsync(t => t.Id == existing, ct);
            if (template is null) return MfgFail.NotFound<StageTemplateDto>("Stage template");
        }
        var code = MfgText.Code(r.Code);
        var problem = MfgText.First(
            code.Length is 0 or > 24 ? "The code is required, up to 24 characters." : null,
            MfgText.Required(r.NameAr, 200, "The Arabic name"),
            MfgText.Required(r.NameFr, 200, "The French name"),
            MfgText.Optional(r.NameEn, 200, "The English name"),
            MfgText.Optional(r.Description, 1000, "The description"));
        if (problem is not null) return MfgFail.Invalid<StageTemplateDto>("invalid_input", problem);
        if (await db.StageTemplates.AnyAsync(x => x.Code == code && x.Id != (id ?? Guid.Empty), ct))
            return MfgFail.Conflict<StageTemplateDto>("code_taken", $"Another stage template already has the code '{code}'.");
        if (r.ProductId is Guid pid)
        {
            var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid && !p.IsDeleted, ct);
            if (product is null) return MfgFail.NotFound<StageTemplateDto>("Product");
            if (!ManufacturingRoles.IsProduced(product.ManufacturingRole))
                return MfgFail.Invalid<StageTemplateDto>("not_produced", "A template can be meant only for a semi-finished or finished product.");
        }

        var steps = r.Steps ?? [];
        var stepProblem = await ValidateStagesAsync(steps, isBom: false, ct);
        if (stepProblem is not null) return stepProblem.As<StageTemplateDto>();

        template ??= db.StageTemplates.Add(new StageTemplate()).Entity;
        template.Code = code;
        template.NameAr = r.NameAr.Trim();
        template.NameFr = r.NameFr.Trim();
        template.NameEn = MfgText.Clean(r.NameEn) ?? template.NameFr;
        template.Description = MfgText.Clean(r.Description);
        template.ProductId = r.ProductId;
        template.IsActive = r.IsActive;
        template.UpdatedAtUtc = DateTime.UtcNow;
        // Sent whole: the stages are replaced. Nothing points at a template's stage — a recipe copies them.
        foreach (var old in template.Steps.ToList()) db.StageTemplateSteps.Remove(old);
        template.Steps.Clear();
        var sequence = 0;
        foreach (var s in steps)
        {
            var step = new StageTemplateStep
            {
                Sequence = ++sequence,
                NameAr = s.NameAr.Trim(),
                NameFr = s.NameFr.Trim(),
                NameEn = MfgText.Clean(s.NameEn) ?? s.NameFr.Trim(),
                EstimatedMinutes = s.EstimatedMinutes,
                ExpectedWorkers = s.ExpectedWorkers,
                IsQualityCheckpoint = s.IsQualityCheckpoint,
                QualityNote = s.IsQualityCheckpoint ? MfgText.Clean(s.QualityNote) : null,
            };
            foreach (var role in s.Roles ?? []) step.Roles.Add(new StageTemplateStepRole { JobRoleId = role.JobRoleId, Count = role.Count });
            template.Steps.Add(step);
            // Inserted explicitly: under a template that is already stored, a step with its key set would
            // otherwise be taken for an existing row.
            db.StageTemplateSteps.Add(step);
        }
        await db.SaveChangesAsync(ct);
        return await StageTemplateAsync(template.Id, ct);
    }

    /// <summary>
    /// The checks one list of stages passes, on a template or on a recipe: at least one stage, every name
    /// given, plausible minutes and workers, known active job roles each named once, and — on a recipe —
    /// at most one output stage.
    /// </summary>
    internal async Task<MfgResult<bool>?> ValidateStagesAsync(IReadOnlyList<StageRequest> stages, bool isBom, CancellationToken ct)
    {
        if (!isBom && stages.Count == 0) return MfgFail.Invalid<bool>("stages_required", "A template needs at least one stage.");
        if (stages.Count > 100) return MfgFail.Invalid<bool>("too_many_stages", "Up to 100 stages.");
        if (isBom && stages.Count(s => s.IsOutput) > 1)
            return MfgFail.Invalid<bool>("one_output_stage", "Only one stage produces the finished output.");
        var roleIds = stages.SelectMany(s => s.Roles ?? []).Select(r => r.JobRoleId).Distinct().ToList();
        var active = await db.ManufacturingJobRoles.AsNoTracking().Where(r => roleIds.Contains(r.Id) && r.IsActive)
            .Select(r => r.Id).ToListAsync(ct);
        var number = 0;
        foreach (var s in stages)
        {
            number++;
            var problem = MfgText.First(
                MfgText.Required(s.NameAr, 200, $"Stage {number}'s Arabic name"),
                MfgText.Required(s.NameFr, 200, $"Stage {number}'s French name"),
                MfgText.Optional(s.NameEn, 200, $"Stage {number}'s English name"),
                MfgText.Optional(s.QualityNote, 400, $"Stage {number}'s quality note"),
                s.EstimatedMinutes is < 0 or > 1_000_000 ? $"Stage {number}'s duration must be zero or more minutes." : null,
                s.ExpectedWorkers is < 0 or > 1000 ? $"Stage {number}'s number of workers must be between 0 and 1000." : null);
            if (problem is not null) return MfgFail.Invalid<bool>("invalid_stage", problem);
            var roles = s.Roles ?? [];
            if (roles.GroupBy(r => r.JobRoleId).Any(g => g.Count() > 1))
                return MfgFail.Invalid<bool>("invalid_stage", $"Stage {number} names a job role twice.");
            if (roles.Any(r => r.Count is < 1 or > 1000))
                return MfgFail.Invalid<bool>("invalid_stage", $"Stage {number} needs between 1 and 1000 people per job role.");
            if (roles.Any(r => !active.Contains(r.JobRoleId)))
                return MfgFail.Invalid<bool>("unknown_job_role", $"Stage {number} names a job role that does not exist or is switched off.");
        }
        return null;
    }
}
