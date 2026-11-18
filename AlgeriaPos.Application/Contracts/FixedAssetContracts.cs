namespace AlgeriaPos.Application.Contracts;

// REN-12, Phase M7a: the fixed-asset register of hire equipment (docs/accounting/fixed-assets.md).

/// <summary>One slot of the register's account map: the active account it names (none: not mapped, never guessed).</summary>
public record FixedAssetSlotDto(
    string Slot, Guid? LedgerAccountId, string? AccountCode, string? AccountNameAr, string? AccountNameFr, string? AccountNameEn,
    string? HintCode, bool Required, DateTime? UpdatedAtUtc, string? UpdatedBy);

/// <summary>
/// A hire product, its own useful life (null: the default) and its own depreciation method (each null: the company's).
/// <c>Locked</c>: the register holds it since the switch-over (its life is fixed). <c>MethodLocked</c>: depreciation is posted
/// on it (its method, coefficient and switch are fixed). <c>Effective…</c>: what it is depreciated by; <c>SuggestedCoefficient</c>
/// the tax code's coefficient for its life (1.5 for 3–4 years, 2 for 5–6, 2.5 above 6).
/// </summary>
public record FixedAssetLifeDto(Guid ProductId, string Sku, string NameAr, string NameFr, string NameEn, int? UsefulLifeMonths, bool Locked,
    string? Method = null, decimal? DecliningCoefficient = null, bool? SwitchToStraightLine = null, decimal? SuggestedCoefficient = null,
    bool MethodLocked = false, string EffectiveMethod = "StraightLine", decimal? EffectiveCoefficient = null, bool EffectiveSwitchToStraightLine = false);

/// <summary>
/// The settings (accounting.manage changes them): the treatment, the method, the lives, the residual, the opening value, the
/// switch-over month — with the switch-over once posted, and the account map's slots.
/// </summary>
public record FixedAssetSettingsDto(
    string Treatment, string Method, int? DefaultUsefulLifeMonths, decimal ResidualPercent, string OpeningValue,
    string? SwitchMonth, DateTime? SwitchedAtUtc, string? SwitchedBy, bool Active, bool DepreciationLocked,
    IReadOnlyList<FixedAssetLifeDto> Lives, IReadOnlyList<FixedAssetSlotDto> Slots, DateTime? UpdatedAtUtc, string? UpdatedBy,
    // Declining balance (2026-11-10): the company's switch to straight line (on by default), and whether the company's method is
    // fixed — a type that follows it has depreciation posted on it.
    bool SwitchToStraightLine = true, bool MethodLocked = false);

/// <param name="SwitchMonth"><c>yyyy-MM</c>, or null for none.</param>
/// <param name="Lives">The types' own lives to set (a null life clears one); types not named keep theirs.</param>
/// <param name="SwitchToStraightLine">The company's switch to straight line under declining balance; null keeps the saved one.</param>
/// <param name="Methods">The types' own methods to set (every field null clears one); types not named keep theirs.</param>
public record UpdateFixedAssetSettingsRequest(
    string Treatment, string Method, int? DefaultUsefulLifeMonths, decimal ResidualPercent, string OpeningValue,
    string? SwitchMonth, IReadOnlyList<FixedAssetLifeLine>? Lives = null,
    bool? SwitchToStraightLine = null, IReadOnlyList<FixedAssetMethodLine>? Methods = null);

public record FixedAssetLifeLine(Guid ProductId, int? UsefulLifeMonths);

/// <summary>A type's own method: <c>StraightLine</c> or <c>DecliningBalance</c>, its coefficient and its switch; each null follows the company.</summary>
public record FixedAssetMethodLine(Guid ProductId, string? Method, decimal? DecliningCoefficient = null, bool? SwitchToStraightLine = null);

/// <summary>One pool the switch-over would open: what the branch holds of a hire product, and the values it moves.</summary>
public record FixedAssetSwitchLineDto(
    Guid ProductId, string Sku, string NameAr, string NameFr, string NameEn, Guid BranchId, string BranchCode,
    string BranchNameAr, string BranchNameFr, string BranchNameEn,
    decimal OnHand, decimal OnRent, decimal InMaintenance, decimal InTransit, decimal Qty,
    decimal UnitCost, decimal StockValue, decimal OpeningValue, decimal Difference, int? UsefulLifeMonths,
    decimal ScrapQty, decimal ScrapValue);

/// <summary>Why the switch-over cannot post yet, in a code and the words.</summary>
public record FixedAssetCheckDto(string Code, string Message);

/// <summary>
/// The switch-over, previewed (nothing written) or as posted: every pool with what it moves out of 310 (the cost method in
/// force) and what it opens at, the totals the confirmation repeats, and what still stands in the way.
/// </summary>
public record FixedAssetSwitchDto(
    string? SwitchMonth, string CostMethod, string OpeningValue, bool Posted, DateTime? SwitchedAtUtc, string? SwitchedBy,
    IReadOnlyList<FixedAssetSwitchLineDto> Lines, decimal StockValue, decimal OpeningTotal, decimal Difference,
    IReadOnlyList<FixedAssetCheckDto> Blocking);

/// <summary>Posts the switch-over; the two totals shown, which the server recomputes under the version lock.</summary>
public record PostFixedAssetSwitchRequest(decimal ExpectedStockValue, decimal ExpectedOpeningValue);

/// <summary>One pool of the register as at a month's end.</summary>
public record FixedAssetPoolDto(
    Guid ProductId, string Sku, string NameAr, string NameFr, string NameEn, Guid BranchId, string BranchCode,
    string BranchNameAr, string BranchNameFr, string BranchNameEn,
    decimal Units, decimal UnitsInStock, decimal Cost, decimal AccumulatedDepreciation, decimal NetBookValue,
    int? UsefulLifeMonths, decimal MonthlyCharge, decimal ScrapQty, decimal ScrapValue,
    // Declining balance (2026-11-10): the method the type is depreciated by.
    string Method = "StraightLine", decimal? DecliningCoefficient = null, bool SwitchToStraightLine = false);

/// <summary>The register as at the end of <c>Year</c>-<c>Month</c> (the last month ended unless another is asked).</summary>
public record FixedAssetRegisterDto(
    bool Active, string? SwitchMonth, int Year, int Month, bool MonthToDate, Guid? BranchId,
    IReadOnlyList<FixedAssetPoolDto> Pools, decimal Cost, decimal AccumulatedDepreciation, decimal NetBookValue, decimal Units,
    string? PostedThrough);

/// <summary>One line of a pool's history: a stored event, or a month's depreciation (computed; <c>Posted</c> once its month is).</summary>
public record FixedAssetHistoryLineDto(
    DateTime AtUtc, string Kind, decimal Qty, decimal Cost, decimal Depreciation, Guid? OtherBranchId, string? OtherBranchCode,
    string? DocumentNumber, string? Reason, string? By, bool Posted,
    // Phase M7d: a capitalised repair names the piece its job repaired, when it named one.
    string? Reference = null);

public record FixedAssetHistoryDto(
    Guid ProductId, string Sku, string NameAr, string NameFr, string NameEn, Guid BranchId, string BranchCode,
    FixedAssetPoolDto? Pool, IReadOnlyList<FixedAssetHistoryLineDto> Lines,
    IReadOnlyList<FixedAssetScheduleYearDto>? Schedule = null);

/// <summary>
/// One calendar year of a pool's schedule, from the switch-over to the end of its last layer's life: the net book value at
/// the start, the year's depreciation, what events added or took out (<c>Movements</c>: purchases, units found, repairs in;
/// disposals, wrecks, transfers out, at net book value), the value at the end. Past months as computed, later ones projected
/// at the units held. <c>Switches</c>: a declining-balance layer turns to straight line that year.
/// </summary>
public record FixedAssetScheduleYearDto(int Year, decimal Opening, decimal Charge, decimal Movements, decimal Closing, bool Switches, bool Projected);

/// <summary>
/// One month's depreciation as the practice ledger shows it (computed on read, written nowhere until posted): an entry per
/// pool, each account's total, the checks, and the month's postings.
/// </summary>
public record FixedAssetDepreciationDto(
    int Year, int Month, bool MonthToDate, Guid? BranchId, bool Active, string? SwitchMonth,
    IReadOnlyList<PracticeLedgerEntryDto> Entries, IReadOnlyList<PracticeLedgerAccountDto> Accounts,
    IReadOnlyList<PracticeLedgerCheckDto> Checks, IReadOnlyList<string> UnmappedSlots, IReadOnlyList<string> Notes,
    decimal TotalDebit, decimal TotalCredit, IReadOnlyList<PracticeLedgerPostingDto> Postings);
