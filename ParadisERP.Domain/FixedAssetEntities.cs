using System.ComponentModel.DataAnnotations;

namespace ParadisERP.Domain;

/// <summary>
/// REN-12 (owner, 31 Oct 2026): the equipment the company hires out is fixed assets, not merchandise stock. How it is
/// depreciated is the accountant's answer, so every choice is a setting set in the panel (owner, 2026-11-09), the suggested
/// answer the default, and today's behaviour kept until the accountant switches the treatment on and posts the switch-over
/// (docs/accounting/fixed-assets.md, Phase M7a). One row; none saved reads as the defaults.
/// <para>
/// Hire equipment is counted in quantities (2,000 frames), not pieces, so the register works on <b>pools</b>: one per hire
/// product and branch, opened by the switch-over, grown by receiving notes and shrunk by disposals, each carrying its cost
/// and its accumulated depreciation (<see cref="FixedAssetMovement"/>).
/// </para>
/// </summary>
public class FixedAssetSettings
{
    public const int SingletonId = 1;
    public int Id { get; set; } = SingletonId;

    /// <summary>Stock (today, the default): hire equipment stays in 310 and nothing here posts. FixedAssets: the register.</summary>
    public RentalEquipmentTreatment Treatment { get; set; } = RentalEquipmentTreatment.Stock;

    /// <summary>
    /// The company's default method (every type without its own, <see cref="FixedAssetMethodChoice"/>): straight line
    /// (suggested, today's) or declining balance (SCF / the tax code's "amortissement dégressif": the straight-line rate × a
    /// coefficient on the net book value at the start of each year). A type's method is fixed once depreciation is posted on it.
    /// </summary>
    public DepreciationMethod Method { get; set; } = DepreciationMethod.StraightLine;

    /// <summary>
    /// Declining balance only: switch to straight line for the rest of the life in the year the straight-line charge on the
    /// remaining value over the remaining months exceeds the declining charge (on by default), so the asset reaches its
    /// residual value at the end of its life. Off: declining to the last year, which takes what is left.
    /// </summary>
    public bool SwitchToStraightLine { get; set; } = true;

    /// <summary>The useful life of every equipment type without its own (<see cref="FixedAssetLife"/>), in months. None: not chosen.</summary>
    public int? DefaultUsefulLifeMonths { get; set; }

    /// <summary>The share of cost left after the useful life, in percent (0 by default: depreciated to nothing).</summary>
    public decimal ResidualPercent { get; set; }

    /// <summary>
    /// The value the pools open at: the cost method in force (the cost price, or the weighted average once sales are costed
    /// at it, C-01) — suggested, and the value 310 carries them at — or each product's asset value, the difference to the
    /// opening adjustment slot.
    /// </summary>
    public FixedAssetOpeningValue OpeningValue { get; set; } = FixedAssetOpeningValue.CostInForce;

    /// <summary>The month of the switch-over (its first day): the first month depreciated. Posted only during that month.</summary>
    public DateOnly? SwitchMonth { get; set; }

    /// <summary>When the switch-over was posted, and by whom: from then on the register holds the hire equipment.</summary>
    public DateTime? SwitchedAtUtc { get; set; }
    [MaxLength(200)] public string? SwitchedBy { get; set; }
    /// <summary>The switch-over entry's source id (<c>fixed-asset-switch</c>).</summary>
    public Guid? SwitchEntrySourceId { get; set; }

    /// <summary>
    /// The company's time zone when the settings were last saved or the switch-over posted: the register's months are the
    /// company's, read by stock code that has no regional settings at hand.
    /// </summary>
    [MaxLength(64)] public string? TimeZoneId { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }
    [MaxLength(200)] public string? UpdatedBy { get; set; }
}

public enum RentalEquipmentTreatment { Stock = 0, FixedAssets = 1 }

public enum DepreciationMethod { StraightLine = 0, DecliningBalance = 1 }

public enum FixedAssetOpeningValue { CostInForce = 0, AssetUnitValue = 1 }

/// <summary>One equipment type's own useful life, overriding the default (REN-12 question 1: "useful life per type").</summary>
public class FixedAssetLife
{
    [Key] public Guid ProductId { get; set; }
    public int UsefulLifeMonths { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    [MaxLength(200)] public string? UpdatedBy { get; set; }
}

/// <summary>
/// One equipment type's own depreciation method, overriding the company's (<see cref="FixedAssetSettings.Method"/>). Each
/// field left empty follows the company: the method, its coefficient (empty: the legal one for the type's life — 1.5 up to
/// 4 years, 2 for 5–6, 2.5 above 6) and the switch to straight line. Changed only while no depreciation is posted on the
/// type (a live month posting charging it, or a disposal or transfer that carried its accumulated depreciation out).
/// </summary>
public class FixedAssetMethodChoice
{
    [Key] public Guid ProductId { get; set; }
    public DepreciationMethod? Method { get; set; }
    public decimal? DecliningCoefficient { get; set; }
    public bool? SwitchToStraightLine { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    [MaxLength(200)] public string? UpdatedBy { get; set; }
}

/// <summary>
/// One event of the register, append-only: what moved in or out of a pool (product × branch), with the cost and the
/// accumulated depreciation it carried — exactly what its ledger entry posted. Depreciation itself is never stored: it is
/// computed from these rows on every read and posted by month (<c>FixedAssetMonthPostings</c>).
/// <para>
/// A later phase (capitalised repairs, per-piece history) attaches here: a capitalised repair is one more kind adding cost
/// to a pool, and a piece's reference rides on <see cref="Reference"/>.
/// </para>
/// </summary>
public class FixedAssetMovement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    /// <summary>The pool's branch; a transfer's source.</summary>
    public Guid BranchId { get; set; }
    /// <summary>A transfer's destination.</summary>
    public Guid? ToBranchId { get; set; }
    public FixedAssetMovementKind Kind { get; set; }
    public decimal Qty { get; set; }
    /// <summary>The cost added (opening, acquisition, found, back into service) or taken out (disposal, scrap, transfer).</summary>
    public decimal Cost { get; set; }
    /// <summary>The accumulated depreciation taken out with the units (disposal, scrap, transfer); 0 otherwise.</summary>
    public decimal Depreciation { get; set; }
    /// <summary>The first month a layer added here is depreciated (opening: the switch-over month; others: the month after).</summary>
    public DateOnly? StartMonth { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>The document behind it: a receiving note, a return note, a transfer step, a state move.</summary>
    public Guid? DocumentId { get; set; }
    [MaxLength(64)] public string? DocumentNumber { get; set; }
    /// <summary>The ledger entry it posted (<c>sourceType</c> and <c>sourceId</c>), when it posted one of its own.</summary>
    [MaxLength(40)] public string? SourceType { get; set; }
    public Guid? SourceId { get; set; }
    [MaxLength(200)] public string? Reason { get; set; }
    /// <summary>A piece's reference (Phase M7d): a capitalised repair names the piece its job repaired, when it named one.</summary>
    [MaxLength(64)] public string? Reference { get; set; }
    [MaxLength(200)] public string? By { get; set; }
}

public enum FixedAssetMovementKind
{
    /// <summary>The switch-over: a pool opened with what the branch held.</summary>
    Opening = 0,
    /// <summary>A receiving note of a hire product after the switch-over.</summary>
    Acquisition = 1,
    /// <summary>Units leaving the company (lost, written off, counted missing): net book value to the disposal account.</summary>
    Disposal = 2,
    /// <summary>Units wrecked into scrap: out of the pool, into scrap (318) at their net book value.</summary>
    Scrapped = 3,
    /// <summary>Units moving to another branch's pool with their cost and depreciation.</summary>
    Transfer = 4,
    /// <summary>Units found at a count: back into the pool at the value the count gives them.</summary>
    Found = 5,
    /// <summary>Scrap the branch held at the switch-over, at the value 318 carries it at. Posts nothing.</summary>
    ScrapOpening = 6,
    /// <summary>Scrap of a hire product leaving scrap (written off): at the value the register carries it at.</summary>
    ScrapOut = 7,
    /// <summary>Scrap of a hire product put back into service: into the pool at the value it was carried at.</summary>
    BackIntoService = 8,
    /// <summary>
    /// A repair capitalised at completion (Phase M7d, MS-30): its cost joins the pool as a layer of cost only — spread over the
    /// repaired units, it adds no unit — depreciated over the type's life from the month after. Its entry posts with the
    /// maintenance practice ledger's month (Dr asset / Cr the accounts the repair was charged to). <c>Reference</c>: the piece.
    /// </summary>
    CapitalisedRepair = 9,
}

/// <summary>
/// One posting of a month's depreciation to the ledger (source <c>fixed-assets</c>): the same shape and rules as
/// manufacturing's and maintenance's month postings, in its own table.
/// </summary>
public class FixedAssetMonthPosting : ILedgerMonthPosting
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Year { get; set; }
    public int Month { get; set; }
    public int Generation { get; set; }
    public int Entries { get; set; }
    public int Indexes { get; set; }
    public decimal TotalDebit { get; set; }
    public DateTime PostedAtUtc { get; set; }
    [MaxLength(200)] public string? PostedBy { get; set; }
    public DateTime? ReversedAtUtc { get; set; }
    [MaxLength(200)] public string? ReversedBy { get; set; }
}

/// <summary>
/// REN-12's slots of the account map, rows of <see cref="ManufacturingAccountMapping"/> under a <c>fixed_assets.</c>
/// prefix. Seeded empty and never guessed: the seeded chart has no class 2 account, so the accountant adds them
/// (e.g. 215/218, 2815/2818, 681, 652/752) and names them here. The hint is only shown.
/// </summary>
public static class FixedAssetAccountSlots
{
    /// <summary>The equipment at cost (class 2, e.g. 215 or 218).</summary>
    public const string Asset = "fixed_assets.asset";
    /// <summary>Accumulated depreciation (e.g. 2815 or 2818), credited monthly.</summary>
    public const string AccumulatedDepreciation = "fixed_assets.accumulated_depreciation";
    /// <summary>The depreciation charge (e.g. 681).</summary>
    public const string DepreciationExpense = "fixed_assets.depreciation_expense";
    /// <summary>The net book value of units lost or written off, and units found (e.g. 652 / 752).</summary>
    public const string Disposal = "fixed_assets.disposal";
    /// <summary>The difference between the value moved out of 310 and the value the pools open at, at the switch-over.</summary>
    public const string OpeningAdjustment = "fixed_assets.opening_adjustment";

    public static readonly IReadOnlyList<string> All = [Asset, AccumulatedDepreciation, DepreciationExpense, Disposal, OpeningAdjustment];

    /// <summary>The slots the switch-over needs whatever the figures: every later event posts to one of them.</summary>
    public static readonly IReadOnlyList<string> Required = [Asset, AccumulatedDepreciation, DepreciationExpense, Disposal];

    /// <summary>The SCF heading the accountant usually names (scf-chart-review.md), shown as a hint only.</summary>
    public static string? HintFor(string slot) => slot switch
    {
        Asset => "215",
        AccumulatedDepreciation => "2815",
        DepreciationExpense => "681",
        Disposal => "652",
        _ => null,
    };
}
