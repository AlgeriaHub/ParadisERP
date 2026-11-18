namespace ParadisERP.Application.Contracts;

// Manufacturing, Phase 1 (docs/manufacturing/phase-1.md). Back office only: none of these reaches a
// till. Quantities and factors are decimals as typed; nothing here is money.

// ---------- units ----------

public record UnitDto(
    Guid Id, string Code, string NameAr, string NameFr, string NameEn,
    string Dimension, decimal FactorToReference, bool IsSystem, bool IsActive);

/// <summary>
/// A unit to add or edit. On a system unit only the names and the active flag are taken; its code,
/// dimension and factor are fixed.
/// </summary>
public record UpsertUnitRequest(
    string Code, string NameAr, string NameFr, string? NameEn,
    string Dimension, decimal FactorToReference, bool IsActive = true);

public record UnitConversionDto(decimal Quantity, Guid FromUnitId, Guid ToUnitId, decimal Result, decimal Factor);

// ---------- specification fields ----------

/// <summary>A specification field. <c>InUse</c>: a value names it, so its kind and dimension are fixed.</summary>
public record SpecAttributeDto(
    Guid Id, string Code, string NameAr, string NameFr, string NameEn,
    string Kind, string Dimension, bool IsSystem, bool IsActive, int SortOrder, bool InUse);

/// <summary>
/// A field to add or edit. <c>Kind</c> is Measure or Text; a measured field names the dimension its unit
/// must measure. On a system field only the names, order and active flag are taken.
/// </summary>
public record UpsertSpecAttributeRequest(
    string Code, string NameAr, string NameFr, string? NameEn,
    string Kind, string? Dimension, bool IsActive = true, int SortOrder = 0);

/// <summary>One value: a figure and its unit for a measured field, a word for a text field.</summary>
public record SpecValueDto(
    Guid AttributeId, string AttributeCode, string Kind, decimal? Value, Guid? UnitId, string? UnitCode, string? Text);

public record SpecValueRequest(Guid AttributeId, decimal? Value = null, Guid? UnitId = null, string? Text = null);

// ---------- materials: a product's role and units ----------

public record ProductUnitDto(Guid UnitId, string UnitCode, decimal FactorToBase, bool ForPurchase, bool ForProduction, bool ForSale);

/// <summary>
/// <c>SoldAtTill</c> is the sale policy's answer (ManufacturingRoles.ForSale): the role, or the product's
/// own <c>AlsoForSale</c>.
/// </summary>
public record MaterialDto(
    Guid ProductId, string Sku, string NameAr, string NameFr, string NameEn, bool IsActive, bool IsRentable,
    string Role, bool SoldAtTill, Guid? BaseUnitId, string? BaseUnitCode, string Unit,
    IReadOnlyList<ProductUnitDto> Units, bool AlsoForSale, IReadOnlyList<SpecValueDto> Specs);

public record ProductUnitRequest(Guid UnitId, decimal FactorToBase, bool ForPurchase = false, bool ForProduction = true, bool ForSale = false);

/// <summary>
/// A product's manufacturing classification, sent whole: the role, the base unit and every alternate
/// unit. A unit left out of <see cref="Units"/> is removed. <see cref="AlsoForSale"/> and
/// <see cref="Specs"/> are left as they are when absent (null), so a client that predates them changes
/// neither; <see cref="Specs"/> sent is the whole specification.
/// </summary>
public record ClassifyMaterialRequest(
    string Role, Guid? BaseUnitId, IReadOnlyList<ProductUnitRequest>? Units,
    bool? AlsoForSale = null, IReadOnlyList<SpecValueRequest>? Specs = null);

// ---------- job roles ----------

public record JobRoleDto(Guid Id, string Code, string NameAr, string NameFr, string NameEn, string? Description, bool IsActive);

public record UpsertJobRoleRequest(string Code, string NameAr, string NameFr, string? NameEn, string? Description, bool IsActive = true);

// ---------- stage templates ----------

public record StageRoleDto(Guid JobRoleId, string JobRoleCode, int Count);

public record StageDto(
    Guid Id, int Sequence, string NameAr, string NameFr, string NameEn,
    int EstimatedMinutes, int ExpectedWorkers, bool IsQualityCheckpoint, string? QualityNote,
    bool IsOutput, IReadOnlyList<StageRoleDto> Roles);

public record StageTemplateDto(
    Guid Id, string Code, string NameAr, string NameFr, string NameEn, string? Description,
    Guid? ProductId, string? ProductSku, bool IsActive, IReadOnlyList<StageDto> Steps);

public record StageRoleRequest(Guid JobRoleId, int Count = 1);

/// <summary>One stage, on a template or a BOM. <c>IsOutput</c> is read on a BOM only.</summary>
public record StageRequest(
    string NameAr, string NameFr, string? NameEn, int EstimatedMinutes = 0, int ExpectedWorkers = 0,
    bool IsQualityCheckpoint = false, string? QualityNote = null, bool IsOutput = false,
    IReadOnlyList<StageRoleRequest>? Roles = null);

/// <summary>A template, sent whole: its stages replace the stored ones, in the order given.</summary>
public record UpsertStageTemplateRequest(
    string Code, string NameAr, string NameFr, string? NameEn, string? Description,
    Guid? ProductId, bool IsActive, IReadOnlyList<StageRequest>? Steps);

// ---------- production lines ----------

public record LineMemberDto(Guid UserId, string Username, string FullName, string Role);

public record LineEmployeeDto(
    Guid Id, Guid EmployeeId, string Number, string FullName,
    Guid PayrollBranchId, string PayrollBranchCode, string PayrollBranchNameAr, string PayrollBranchNameFr, string? JobTitle,
    Guid? JobRoleId, string? JobRoleCode, DateOnly AssignedOn, DateOnly? EndedOn, string? Note);

public record LineProductDto(Guid ProductId, string Sku, string NameAr, string NameFr, string NameEn, string Role);

/// <summary>
/// A line as a list shows it. <c>CanEdit</c> and <c>CanAssign</c> are what the caller may do on it, decided
/// by the server; the screen only follows them.
/// </summary>
public record ProductionLineSummaryDto(
    Guid Id, string Code, string NameAr, string NameFr, string NameEn, string? Description,
    Guid LocationId, string LocationCode, string LocationNameAr, string LocationNameFr,
    Guid? ManagerUserId, string? ManagerName, bool IsActive,
    int EmployeeCount, int ProductCount, int ActiveBomCount,
    bool CanEdit, bool CanAssign);

public record ProductionLineDto(
    ProductionLineSummaryDto Line,
    IReadOnlyList<LineMemberDto> Members,
    IReadOnlyList<LineEmployeeDto> Employees,
    IReadOnlyList<LineProductDto> Products);

public record UpsertProductionLineRequest(
    string Code, string NameAr, string NameFr, string? NameEn, string? Description,
    Guid LocationId, Guid? ManagerUserId, bool IsActive = true);

public record LineMemberRequest(Guid UserId, string Role);

public record SetLineMembersRequest(IReadOnlyList<LineMemberRequest>? Members);

public record AssignLineEmployeeRequest(Guid EmployeeId, Guid? JobRoleId, DateOnly? AssignedOn, string? Note);

public record EndLineEmployeeRequest(DateOnly? EndedOn);

public record SetLineProductsRequest(IReadOnlyList<Guid>? ProductIds);

/// <summary>One product's quantities at the line's location, straight from its stock row.</summary>
public record LineStockDto(
    Guid ProductId, string Sku, string NameAr, string NameFr, string NameEn, string Role, string Unit,
    decimal Available, decimal Maintenance, decimal Scrap, decimal ReorderLevel);

/// <summary>A warehouse a new line may stand on, or the one it stands on.</summary>
public record LineLocationDto(Guid Id, string Code, string NameAr, string NameFr, bool IsActive, Guid? LineId);

/// <summary>A login that may be named on a line: no password, no permission list.</summary>
public record LineUserCandidateDto(Guid Id, string Username, string FullName, Guid? BranchId);

/// <summary>An employee that may be assigned: no pay, no identity numbers.</summary>
public record LineEmployeeCandidateDto(
    Guid Id, string Number, string FullName, Guid BranchId, string BranchCode, string BranchNameAr, string BranchNameFr, string? JobTitle);

// ---------- bills of materials ----------

public record BomLineDto(
    Guid Id, int Sequence, Guid ComponentProductId, string ComponentSku, string ComponentNameAr,
    string ComponentNameFr, string ComponentNameEn, string ComponentRole,
    decimal Quantity, Guid UnitId, string UnitCode, decimal WastePercent, string? Specification, string? Note,
    IReadOnlyList<SpecValueDto> Specs);

public record BomSummaryDto(
    Guid Id, Guid ProductId, string ProductSku, string ProductNameAr, string ProductNameFr, string ProductNameEn,
    Guid? ProductionLineId, string? ProductionLineCode, int Version, string Status,
    decimal BatchQuantity, Guid BatchUnitId, string BatchUnitCode,
    int LineCount, int StageCount, DateTime CreatedAtUtc, DateTime? ActivatedAtUtc, DateTime? RetiredAtUtc);

public record BomDto(
    BomSummaryDto Bom, Guid? StageTemplateId, string? Notes,
    string? CreatedBy, string? ActivatedBy, string? RetiredBy,
    IReadOnlyList<BomLineDto> Lines, IReadOnlyList<StageDto> Stages,
    bool CanEdit, bool CanApprove);

/// <summary>A component of a draft. <c>Specs</c>: the line's measured specification, beside the free text.</summary>
public record BomLineRequest(
    Guid ComponentProductId, decimal Quantity, Guid UnitId, decimal WastePercent = 0,
    string? Specification = null, string? Note = null, IReadOnlyList<SpecValueRequest>? Specs = null);

/// <summary>
/// A draft, sent whole: its lines and stages replace the stored ones, in the order given. With
/// <c>StageTemplateId</c> and no <c>Stages</c>, the template's stages are copied.
/// </summary>
public record UpsertBomRequest(
    Guid ProductId, Guid? ProductionLineId, decimal BatchQuantity, Guid BatchUnitId,
    Guid? StageTemplateId, string? Notes,
    IReadOnlyList<BomLineRequest>? Lines, IReadOnlyList<StageRequest>? Stages);

/// <summary>
/// One component's need for a planned quantity. In the line's own unit: the net quantity, the expected
/// waste and their sum; then the sum in the component's base unit, which is what stock is counted in.
/// </summary>
public record BomRequirementLineDto(
    Guid BomLineId, Guid ComponentProductId, string ComponentSku, string ComponentNameAr,
    string ComponentNameFr, string ComponentNameEn,
    Guid UnitId, string UnitCode, decimal NetQuantity, decimal WasteQuantity, decimal GrossQuantity,
    Guid BaseUnitId, string BaseUnitCode, decimal BaseQuantity);

/// <summary>What one component needs altogether, when several lines name it.</summary>
public record BomRequirementTotalDto(
    Guid ComponentProductId, string ComponentSku, Guid BaseUnitId, string BaseUnitCode, decimal BaseQuantity);

public record BomRequirementsDto(
    Guid BomId, decimal PlannedQuantity, Guid PlannedUnitId, string PlannedUnitCode,
    decimal Batches, IReadOnlyList<BomRequirementLineDto> Lines, IReadOnlyList<BomRequirementTotalDto> Totals);

// ---------- access ----------

/// <summary>A login and the manufacturing permissions they hold, by role and by grant.</summary>
public record ManufacturingAccessDto(
    Guid UserId, string Username, string FullName, string Role, Guid? BranchId,
    IReadOnlyList<string> FromRole, IReadOnlyList<string> Granted);

/// <summary>The user's manufacturing grants, sent whole. Keys outside the manufacturing module are refused.</summary>
public record SetManufacturingAccessRequest(IReadOnlyList<string>? Keys);
