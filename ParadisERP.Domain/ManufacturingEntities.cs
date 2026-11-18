using System.ComponentModel.DataAnnotations;

namespace ParadisERP.Domain;

// Manufacturing, Phase 1 (docs/manufacturing/phase-1.md): the configuration a production order will
// stand on — production lines, product roles, units and their conversions, versioned bills of
// materials, stage templates and the job roles of the workforce. Nothing here moves stock, values
// stock or posts to the ledger: orders, issues and costing are later phases.
//
// Every table is server-only (none derives from SyncEntity) and every foreign key is Restrict, so a
// line, a unit or a product that something still names cannot be deleted from under it.

/// <summary>
/// What a product is to manufacturing (Manufacturing &amp; Maintenance audit, revision 2, D4).
///
/// <para>
/// ⚠ Zero MUST stay <see cref="None"/>: it is what every product that predates the column holds
/// (the migration's default, and the CLR default <c>EnsureCreated</c> writes in the tests), and it
/// means "ordinary merchandise, exactly as before". Values are only ever appended.
/// </para>
/// </summary>
public enum ManufacturingRole
{
    /// <summary>Ordinary merchandise: sold and rented as it always was. Every existing product.</summary>
    None = 0,
    RawMaterial = 1,
    /// <summary>Used up in production without becoming part of the product (welding wire, discs, gas).</summary>
    Consumable = 2,
    /// <summary>A part that goes with or into a product (couplers, pins). Still sold at the till.</summary>
    Accessory = 3,
    /// <summary>A part kept for repairs. Still sold at the till.</summary>
    SparePart = 4,
    /// <summary>Made here, then used to make something else; stocked as its own product.</summary>
    SemiFinished = 5,
    /// <summary>Made here and sold or hired out.</summary>
    Finished = 6,
}

/// <summary>The rules each role carries, written once so the till, the BOM and the screens agree.</summary>
public static class ManufacturingRoles
{
    /// <summary>
    /// Whether the till offers the product — in its grid, its search and its barcode lookup. Raw
    /// materials, consumables and semi-finished goods are the workshop's, not the counter's. This only
    /// hides them from the till's lookups: a sale that names one anyway (an older till) is accepted,
    /// because a sale is never refused. A product's own exception is <see cref="ForSale(Product)"/>.
    /// </summary>
    public static bool SoldAtTill(ManufacturingRole role) =>
        role is ManufacturingRole.None or ManufacturingRole.Accessory or ManufacturingRole.SparePart or ManufacturingRole.Finished;

    /// <summary>
    /// The sale policy, in one place (hardening review, 2026-10-26): a product is for sale when its role
    /// is one the counter sells, or when a manufacturing-only item (a raw material, a consumable, a
    /// semi-finished good) is marked <see cref="Product.AlsoForSale"/> — surplus wire sold to another
    /// workshop. What it decides:
    /// <list type="bullet">
    /// <item>The till offers only what is for sale: its grid, search, barcode lookup and price list.</item>
    /// <item>A back-office document typed for a customer refuses a line that is not for sale.</item>
    /// <item>A till's sale is never refused for it — online or queued offline, the money was taken and
    /// the receipt printed (CLAUDE.md, rule one): it is recorded, and the push says so and is logged.</item>
    /// <item>Nothing already recorded is re-judged: a refund, an invoice raised from a recorded sale and
    /// every report read the sale as it was made.</item>
    /// </list>
    /// </summary>
    public static bool ForSale(ManufacturingRole role, bool alsoForSale) => SoldAtTill(role) || alsoForSale;

    /// <inheritdoc cref="ForSale(ManufacturingRole, bool)"/>
    public static bool ForSale(Product product) => ForSale(product.ManufacturingRole, product.AlsoForSale);

    /// <summary>Whether a bill of materials may produce it.</summary>
    public static bool IsProduced(ManufacturingRole role) =>
        role is ManufacturingRole.SemiFinished or ManufacturingRole.Finished;

    /// <summary>Whether it may be a component of a bill of materials.</summary>
    public static bool IsComponent(ManufacturingRole role) =>
        role is ManufacturingRole.RawMaterial or ManufacturingRole.Consumable or ManufacturingRole.Accessory
            or ManufacturingRole.SparePart or ManufacturingRole.SemiFinished;
}

/// <summary>
/// What a unit measures. Two units convert into each other only within one dimension, through
/// their factors to the dimension's reference unit; <see cref="Other"/> converts to nothing, so a
/// "carton" or a "coil" is only ever converted through a factor written on the product.
/// </summary>
public enum UnitDimension
{
    /// <summary>Reference: the piece.</summary>
    Count = 0,
    /// <summary>Reference: the kilogram.</summary>
    Mass = 1,
    /// <summary>Reference: the metre.</summary>
    Length = 2,
    /// <summary>Reference: the litre.</summary>
    Volume = 3,
    /// <summary>Reference: the square metre.</summary>
    Area = 4,
    /// <summary>Packaging and anything else: no standard conversion at all.</summary>
    Other = 5,
}

/// <summary>
/// A unit of measure (audit section 7). The six the owner listed are seeded at fixed ids and are
/// <see cref="IsSystem"/>: their dimension and factor never change, so 1 kg is 1,000 g for good. Others
/// are added in the settings.
/// </summary>
public class UnitOfMeasure
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Short and unique, as printed beside a quantity: <c>kg</c>, <c>m</c>, <c>pcs</c>.</summary>
    [MaxLength(16)] public string Code { get; set; } = string.Empty;
    [MaxLength(80)] public string NameAr { get; set; } = string.Empty;
    [MaxLength(80)] public string NameFr { get; set; } = string.Empty;
    [MaxLength(80)] public string NameEn { get; set; } = string.Empty;

    public UnitDimension Dimension { get; set; }

    /// <summary>
    /// How many of the dimension's reference unit one of this unit is: 1 for the kilogram, 0.001 for
    /// the gram, 0.01 for the centimetre. Always above zero. Ignored for <see cref="UnitDimension.Other"/>.
    /// </summary>
    public decimal FactorToReference { get; set; } = 1m;

    /// <summary>Seeded; its code, dimension and factor are fixed. Only its names may be edited.</summary>
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>The seeded units, at fixed ids so a seed is the same row in every company.</summary>
public static class SystemUnits
{
    public static readonly Guid Piece = Guid.Parse("0a17e000-0000-4000-8000-000000000001");
    public static readonly Guid Kilogram = Guid.Parse("0a17e000-0000-4000-8000-000000000002");
    public static readonly Guid Gram = Guid.Parse("0a17e000-0000-4000-8000-000000000003");
    public static readonly Guid Metre = Guid.Parse("0a17e000-0000-4000-8000-000000000004");
    public static readonly Guid Centimetre = Guid.Parse("0a17e000-0000-4000-8000-000000000005");
    public static readonly Guid Litre = Guid.Parse("0a17e000-0000-4000-8000-000000000006");
    public static readonly Guid Millimetre = Guid.Parse("0a17e000-0000-4000-8000-000000000007");
}

/// <summary>
/// A unit a product is also counted in, with a fixed factor to its base unit (audit section 7): a
/// carton is 25 kg, a bundle 100 pieces. What a unit means for this product only — a carton of nails
/// and a carton of screws are different quantities. An item whose weight varies (a coil of wire) has
/// no fixed factor and is entered in its base unit by weighing.
/// </summary>
public class ProductUnit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public Guid UnitId { get; set; }
    public UnitOfMeasure? Unit { get; set; }

    /// <summary>How many of the product's base unit one of this unit holds. Above zero.</summary>
    public decimal FactorToBase { get; set; }

    /// <summary>Which documents offer it. Phase 1 records the flags; the documents that read them come later.</summary>
    public bool ForPurchase { get; set; }
    public bool ForProduction { get; set; } = true;
    public bool ForSale { get; set; }
}

/// <summary>
/// A job on the shop floor — welder, cutter, painter, supervisor (audit D16). The HR model has only a
/// free-text <see cref="Employee.JobTitle"/>, and a stage template has to name the roles it needs, so
/// manufacturing keeps its own short list. It holds no pay: wages stay on the employee and in payroll.
/// </summary>
public class ManufacturingJobRole
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(24)] public string Code { get; set; } = string.Empty;
    [MaxLength(120)] public string NameAr { get; set; } = string.Empty;
    [MaxLength(120)] public string NameFr { get; set; } = string.Empty;
    [MaxLength(120)] public string NameEn { get; set; } = string.Empty;
    [MaxLength(400)] public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A production line (audit section 3, D1, D2, D34): a record over one warehouse-type location that
/// holds its stock. The location is an ordinary <see cref="Branch"/> with <c>IsWarehouse</c> — never the
/// main warehouse and never another line's — so the line's materials and output keep their own
/// balances in <see cref="ProductStock"/> and nothing is counted twice. As a warehouse it has no till
/// and no cash box.
/// </summary>
public class ProductionLine
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Short, unique, upper case: <c>L1</c>, <c>NAILS</c>.</summary>
    [MaxLength(16)] public string Code { get; set; } = string.Empty;
    [MaxLength(200)] public string NameAr { get; set; } = string.Empty;
    [MaxLength(200)] public string NameFr { get; set; } = string.Empty;
    [MaxLength(200)] public string NameEn { get; set; } = string.Empty;
    [MaxLength(1000)] public string? Description { get; set; }

    /// <summary>The warehouse-type location that holds the line's stock. Unique: one line per location.</summary>
    public Guid LocationId { get; set; }
    public Branch? Location { get; set; }

    /// <summary>
    /// The production manager responsible for the line: a login, because authority is exercised by
    /// signing in. Also a manager of the line for access, without a membership row of its own.
    /// </summary>
    public Guid? ManagerUserId { get; set; }

    public bool IsActive { get; set; } = true;

    [MaxLength(64)] public string? CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<ProductionLineMember> Members { get; set; } = new();
    public List<ProductionLineEmployee> Employees { get; set; } = new();
    public List<ProductionLineProduct> Products { get; set; } = new();
}

/// <summary>What a member login may do on a line (D35).</summary>
public enum ProductionLineMemberRole
{
    /// <summary>Runs the line: its assignments, products and documents.</summary>
    Manager = 0,
    /// <summary>Works on the line's documents; sees the line.</summary>
    Supervisor = 1,
}

/// <summary>
/// A login that works on a line without company-wide scope (D35, D36). A user with company scope
/// sees every line already; anyone else sees the lines they are a member of, plus a line whose location
/// is their own branch. What they may do there still needs the manufacturing permissions.
/// </summary>
public class ProductionLineMember
{
    public Guid LineId { get; set; }
    public ProductionLine? Line { get; set; }
    public Guid UserId { get; set; }
    public ProductionLineMemberRole Role { get; set; }
    [MaxLength(64)] public string? AddedBy { get; set; }
    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// An employee working on a line (D16). The employee stays on the branch that pays them —
/// <see cref="Employee.BranchId"/> is never touched — so payroll runs do not change; labour reaches the
/// line later through bookings. Ended rather than deleted, so who worked where stays on record.
/// </summary>
public class ProductionLineEmployee
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LineId { get; set; }
    public ProductionLine? Line { get; set; }
    public Guid EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public Guid? JobRoleId { get; set; }
    public ManufacturingJobRole? JobRole { get; set; }
    public DateOnly AssignedOn { get; set; }
    /// <summary>Null while the assignment runs.</summary>
    public DateOnly? EndedOn { get; set; }
    [MaxLength(400)] public string? Note { get; set; }
    [MaxLength(64)] public string? AssignedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A product a line makes. Only semi-finished and finished products.</summary>
public class ProductionLineProduct
{
    public Guid LineId { get; set; }
    public ProductionLine? Line { get; set; }
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
}

/// <summary>
/// A reusable sequence of manufacturing stages (cutting, welding, galvanising…). A template may be
/// meant for one product. A bill of materials copies its stages when they are chosen, and a future
/// production order copies them again from the BOM, so editing a template never rewrites the stages of
/// an active BOM or of an order.
/// </summary>
public class StageTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(24)] public string Code { get; set; } = string.Empty;
    [MaxLength(200)] public string NameAr { get; set; } = string.Empty;
    [MaxLength(200)] public string NameFr { get; set; } = string.Empty;
    [MaxLength(200)] public string NameEn { get; set; } = string.Empty;
    [MaxLength(1000)] public string? Description { get; set; }
    /// <summary>Null for a template any product may use.</summary>
    public Guid? ProductId { get; set; }
    public Product? Product { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<StageTemplateStep> Steps { get; set; } = new();
}

/// <summary>One stage of a template.</summary>
public class StageTemplateStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TemplateId { get; set; }
    public StageTemplate? Template { get; set; }
    public int Sequence { get; set; }
    [MaxLength(200)] public string NameAr { get; set; } = string.Empty;
    [MaxLength(200)] public string NameFr { get; set; } = string.Empty;
    [MaxLength(200)] public string NameEn { get; set; } = string.Empty;
    /// <summary>For one batch, in minutes. Zero when unknown.</summary>
    public int EstimatedMinutes { get; set; }
    public int ExpectedWorkers { get; set; }
    /// <summary>The work is checked here before it moves on.</summary>
    public bool IsQualityCheckpoint { get; set; }
    [MaxLength(400)] public string? QualityNote { get; set; }
    public List<StageTemplateStepRole> Roles { get; set; } = new();
}

/// <summary>A job role a template stage needs, and how many people in it.</summary>
public class StageTemplateStepRole
{
    public Guid StepId { get; set; }
    public StageTemplateStep? Step { get; set; }
    public Guid JobRoleId { get; set; }
    public ManufacturingJobRole? JobRole { get; set; }
    public int Count { get; set; } = 1;
}

/// <summary>The life of a BOM version (audit D11, Phase 1 brief §4).</summary>
public enum BomStatus
{
    /// <summary>Being written; the only state in which it may change or be deleted.</summary>
    Draft = 0,
    /// <summary>Approved and in use. Frozen: a change is a new version.</summary>
    Active = 1,
    /// <summary>Replaced or withdrawn. Frozen, and kept, because orders will name it.</summary>
    Retired = 2,
}

/// <summary>
/// One version of a product's recipe: what one batch of it consumes, in which stages. Versions count
/// per product and line (a BOM may serve one line or, with no line, every line); at most one version is
/// active for a product and line at a time, and activating one retires the version it replaces.
/// Once active it never changes — not its lines, not its stages — so whatever was made from it can
/// always be read back exactly as it was defined.
/// </summary>
public class BillOfMaterials
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>The semi-finished or finished product a batch makes.</summary>
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    /// <summary>Null: any line may use it.</summary>
    public Guid? ProductionLineId { get; set; }
    public ProductionLine? ProductionLine { get; set; }
    public int Version { get; set; }
    public BomStatus Status { get; set; } = BomStatus.Draft;

    /// <summary>How much of the product one batch makes, in <see cref="BatchUnitId"/>. Above zero.</summary>
    public decimal BatchQuantity { get; set; } = 1m;
    public Guid BatchUnitId { get; set; }
    public UnitOfMeasure? BatchUnit { get; set; }

    /// <summary>The template the stages were copied from, for reference only; the stages below are the BOM's own.</summary>
    public Guid? StageTemplateId { get; set; }

    [MaxLength(1000)] public string? Notes { get; set; }

    [MaxLength(64)] public string? CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(64)] public string? ActivatedBy { get; set; }
    public DateTime? ActivatedAtUtc { get; set; }
    [MaxLength(64)] public string? RetiredBy { get; set; }
    public DateTime? RetiredAtUtc { get; set; }

    public List<BomLine> Lines { get; set; } = new();
    public List<BomStage> Stages { get; set; } = new();
}

/// <summary>One component of a BOM, for one batch.</summary>
public class BomLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BomId { get; set; }
    public BillOfMaterials? Bom { get; set; }
    public int Sequence { get; set; }
    public Guid ComponentProductId { get; set; }
    public Product? ComponentProduct { get; set; }
    /// <summary>Net quantity per batch, in <see cref="UnitId"/>, before the expected waste. Above zero.</summary>
    public decimal Quantity { get; set; }
    public Guid UnitId { get; set; }
    public UnitOfMeasure? Unit { get; set; }
    /// <summary>Expected waste as a percentage of the net quantity, 0 to under 100. Added on top.</summary>
    public decimal WastePercent { get; set; }
    /// <summary>Dimensions or grade, as the workshop writes them: "Ø 48.3 × 3.2 mm, S235".</summary>
    [MaxLength(400)] public string? Specification { get; set; }
    [MaxLength(400)] public string? Note { get; set; }

    /// <summary>
    /// The same specification as figures a later calculation can read — the piece is cut to 1.20 m — beside
    /// the free text, never instead of it. Optional; frozen with the recipe.
    /// </summary>
    public List<BomLineSpec> Specs { get; set; } = new();
}

/// <summary>A stage of a BOM, copied from a template or written on the BOM, and frozen with it.</summary>
public class BomStage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BomId { get; set; }
    public BillOfMaterials? Bom { get; set; }
    public int Sequence { get; set; }
    [MaxLength(200)] public string NameAr { get; set; } = string.Empty;
    [MaxLength(200)] public string NameFr { get; set; } = string.Empty;
    [MaxLength(200)] public string NameEn { get; set; } = string.Empty;
    public int EstimatedMinutes { get; set; }
    public int ExpectedWorkers { get; set; }
    public bool IsQualityCheckpoint { get; set; }
    [MaxLength(400)] public string? QualityNote { get; set; }
    /// <summary>
    /// The stage whose good quantity is finished output (audit amendment 5): the others record
    /// progress on the same units, so a piece cut and then welded counts once. Exactly one per BOM
    /// that has stages.
    /// </summary>
    public bool IsOutput { get; set; }
    public List<BomStageRole> Roles { get; set; } = new();
}

/// <summary>A job role a BOM stage needs, and how many people in it.</summary>
public class BomStageRole
{
    public Guid StageId { get; set; }
    public BomStage? Stage { get; set; }
    public Guid JobRoleId { get; set; }
    public ManufacturingJobRole? JobRole { get; set; }
    public int Count { get; set; } = 1;
}

/// <summary>What a specification field holds: a measured figure in a unit, or a word.</summary>
public enum SpecKind
{
    /// <summary>A figure with a unit of the field's dimension: a length of 6 m, a thickness of 3.2 mm.</summary>
    Measure = 0,
    /// <summary>A word or a code: a steel grade, a colour, a finish.</summary>
    Text = 1,
}

/// <summary>
/// A specification field (hardening review, 2026-10-26): length, width, thickness, diameter, grade — and
/// whatever else a company's products need, added in the settings. Not a fixed set of columns, so a
/// welded frame, a bag of mortar and a painted panel each carry only the fields that mean something for
/// them, and a product with no physical dimensions carries none.
///
/// <para>
/// A measured field names its <see cref="Dimension"/>, so its value is always written with a unit that
/// converts — a thickness is never typed in kilograms — and a later calculation reads it in any unit of
/// that dimension. Once a value uses the field, its kind and dimension are fixed.
/// </para>
/// </summary>
public class SpecAttribute
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(16)] public string Code { get; set; } = string.Empty;
    [MaxLength(80)] public string NameAr { get; set; } = string.Empty;
    [MaxLength(80)] public string NameFr { get; set; } = string.Empty;
    [MaxLength(80)] public string NameEn { get; set; } = string.Empty;
    public SpecKind Kind { get; set; }
    /// <summary>For a measured field, what its unit must measure. Ignored for <see cref="SpecKind.Text"/>.</summary>
    public UnitDimension Dimension { get; set; } = UnitDimension.Length;
    /// <summary>Seeded; its code, kind and dimension are fixed. Only its names and order may be edited.</summary>
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>The seeded specification fields, at fixed ids so a seed is the same row in every company.</summary>
public static class SystemSpecAttributes
{
    public static readonly Guid Length = Guid.Parse("5bec0000-0000-4000-8000-000000000001");
    public static readonly Guid Width = Guid.Parse("5bec0000-0000-4000-8000-000000000002");
    public static readonly Guid Thickness = Guid.Parse("5bec0000-0000-4000-8000-000000000003");
    public static readonly Guid Diameter = Guid.Parse("5bec0000-0000-4000-8000-000000000004");
    public static readonly Guid Grade = Guid.Parse("5bec0000-0000-4000-8000-000000000005");
}

/// <summary>
/// One value of a specification field: a figure in a unit for a measured field, a word for a text field.
/// Shared by a product's own specification and a recipe line's.
/// </summary>
public abstract class SpecValue
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AttributeId { get; set; }
    public SpecAttribute? Attribute { get; set; }
    /// <summary>The figure, above zero, in <see cref="UnitId"/>. Set for a measured field only.</summary>
    public decimal? Value { get; set; }
    public Guid? UnitId { get; set; }
    public UnitOfMeasure? Unit { get; set; }
    /// <summary>The word. Set for a text field only.</summary>
    [MaxLength(200)] public string? Text { get; set; }
}

/// <summary>What a product is, measured: a bar 6 m long, a sheet 2 × 1 m and 3 mm thick. Optional.</summary>
public class ProductSpec : SpecValue
{
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
}

/// <summary>What a recipe needs of a component, measured: a piece of that bar cut to 1.20 m.</summary>
public class BomLineSpec : SpecValue
{
    public Guid BomLineId { get; set; }
    public BomLine? BomLine { get; set; }
}

/// <summary>
/// One change to whether a product is for sale (owner, 2026-10-26, decision 2: "audit every change,
/// including user, timestamp, previous value, and new value"): its manufacturing role or its
/// <see cref="Product.AlsoForSale"/> mark, as the materials screen saved it. Written in the same save as
/// the change; never edited. It also tells a till sale of a material whether the till sold it before the
/// product was withdrawn from sale (<see cref="ManufacturingSaleException.SoldBeforeWithdrawal"/>).
/// </summary>
public class ProductSaleStatusChange
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public DateTime ChangedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UserId { get; set; }
    /// <summary>The name as it stood, so a renamed or removed account still reads in the history.</summary>
    [MaxLength(200)] public string? UserName { get; set; }
    public ManufacturingRole OldRole { get; set; }
    public ManufacturingRole NewRole { get; set; }
    public bool OldAlsoForSale { get; set; }
    public bool NewAlsoForSale { get; set; }
    /// <summary>The sale policy's answer before and after (ManufacturingRoles.ForSale).</summary>
    public bool OldForSale { get; set; }
    public bool NewForSale { get; set; }
}

/// <summary>
/// A till sale of something not for sale, recorded as made (owner, 2026-10-26, decision 1: accept it,
/// "record the violation with sufficient audit information"). One row per sale line, written in the same
/// transaction as the sale, so a sale is never on file without its exception or the other way round. The
/// sale itself — its money, its receipt, its stock movement — is untouched. Phase 2's exception report
/// reads these rows.
/// </summary>
public class ManufacturingSaleException
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SaleId { get; set; }
    public Guid SaleLineId { get; set; }
    public Guid ProductId { get; set; }
    [MaxLength(64)] public string Sku { get; set; } = string.Empty;
    public Guid BranchId { get; set; }
    [MaxLength(40)] public string DeviceCode { get; set; } = string.Empty;
    /// <summary>The receipt number the sale was recorded under.</summary>
    [MaxLength(40)] public string Reference { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    /// <summary>When the till made the sale, as recorded on it.</summary>
    public DateTime SoldAtUtc { get; set; }
    /// <summary>When the sale reached the server: the gap is how long it waited offline.</summary>
    public DateTime ReceivedAtUtc { get; set; }
    public Guid? CashierUserId { get; set; }
    [MaxLength(200)] public string? CashierName { get; set; }
    /// <summary>The account whose session sent the sale, which may not be the cashier.</summary>
    public Guid? PushedByUserId { get; set; }
    /// <summary>The product's role when the sale arrived.</summary>
    public ManufacturingRole Role { get; set; }
    /// <summary>When the product last stopped being for sale, if the history says (ProductSaleStatusChange).</summary>
    public DateTime? WithdrawnAtUtc { get; set; }
    /// <summary>
    /// The till sold it before it was withdrawn from sale: stale offline data — a sale queued while the
    /// product was still for sale — rather than a till offering what it should no longer have offered.
    /// </summary>
    public bool SoldBeforeWithdrawal { get; set; }
}
