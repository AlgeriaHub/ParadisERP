namespace ParadiERP.Application.Contracts;

// The weighted-average shadow (P0.4b): read-only figures computed from the movements since the opening.
// Nothing here is posted, stored or used to price anything.

/// <summary>
/// The choices the accountant has not answered yet, as the admin set them (owner, 2026-11-08), and how sales are
/// costed (C-01, 2026-11-09): <c>CostPrice</c> (today's rule, the default) or <c>WeightedAverage</c>, since when and by whom.
/// <c>ReceiptCostRule</c> (B-F01, 2026-11-09): what a receiving note makes the product's cost price — <c>LastCost</c>
/// (today's rule, the default), <c>WeightedAverage</c> or <c>Unchanged</c>. Three more of the accountant's choices
/// (2026-11-09; each default is today's behaviour): <c>GapPosting</c> (C-16) — <c>Accountant</c> or <c>Automatic</c>;
/// <c>CostPriceRevaluation</c> (B-F01) — <c>None</c> or <c>StockOnHand</c>; <c>ZeroCostReceipt</c> — <c>Zero</c> or
/// <c>CostPrice</c>.
/// </summary>
public record StockValuationSettingsDto(
    string Scope, string RefundCost, int UnitCostDecimals, DateTime? StartedAtUtc, DateTime? UpdatedAtUtc, string? UpdatedBy,
    string SalesCost = "CostPrice", DateTime? SalesCostChangedAtUtc = null, string? SalesCostChangedBy = null,
    bool DirectInvoiceStock = false, DateTime? DirectInvoiceStockChangedAtUtc = null, string? DirectInvoiceStockChangedBy = null,
    string ReceiptCostRule = "LastCost", string GapPosting = "Accountant", string CostPriceRevaluation = "None",
    string ZeroCostReceipt = "Zero");

/// <summary>
/// <paramref name="DirectInvoiceStock"/> (P0.3): whether an invoice typed in the back office takes its goods out of stock
/// and posts their cost. Null leaves it as it is, so a client that does not know the choice never changes it.
/// <paramref name="ReceiptCostRule"/> (B-F01): <c>LastCost</c>, <c>WeightedAverage</c> or <c>Unchanged</c>; null keeps the saved one.
/// <paramref name="GapPosting"/>, <paramref name="CostPriceRevaluation"/> and <paramref name="ZeroCostReceipt"/>: the same —
/// null keeps the saved choice, so an older client never changes one.
/// </summary>
public record UpdateStockValuationSettingsRequest(
    string Scope, string RefundCost, int UnitCostDecimals, bool? DirectInvoiceStock = null, string? ReceiptCostRule = null,
    string? GapPosting = null, string? CostPriceRevaluation = null, string? ZeroCostReceipt = null);

// How sales are costed (C-01): the switch, the opening adjustment it posts (C-02) and the negative-stock gaps (C-16).

/// <summary>A ledger account a slot of the account map names; null fields when the slot is unmapped.</summary>
public record ValuationAccountDto(string Slot, bool Mapped, string? Code, string? NameAr, string? NameFr, string? NameEn);

/// <summary>A negative-stock gap settled by an inflow since sales were switched to the average, waiting to post.</summary>
public record ValuationGapDto(
    Guid MovementId, DateTime AtUtc, Guid ProductId, string Sku, string NameAr, string NameFr, string NameEn,
    Guid BranchId, string Kind, string? DocumentNumber, decimal Amount);

/// <summary>One change of how sales are costed, with the figures it was made on.</summary>
public record SalesCostSwitchDto(
    Guid Id, string Method, DateTime AtUtc, string? By, decimal ShadowValue, decimal BookValue, decimal Adjustment, Guid? LedgerEntryId);

/// <summary>
/// How sales are costed and what switching would post. <c>ShadowValue</c>: stock at the shadow's average (in transit
/// included); <c>BookValue</c>: the balance of the stock accounts (<c>StockAccounts</c>); <c>Adjustment</c>: the
/// difference, to 3 decimals — what switching on posts once to the opening-adjustment account (C-02). While on, the
/// same figures show how far the stock accounts have drifted from the average (production still posts its own
/// snapshot). <c>FallbackLines</c>: sale lines costed at the cost price because no average existed.
/// <c>FallbackMovements</c>: the same for the other stock documents' rows (adjustments, write-offs, state changes,
/// transfer steps, rental returns) valued at their reference cost while sales were costed at the average.
/// </summary>
public record SalesCostingDto(
    StockValuationSettingsDto Settings, decimal ShadowValue, decimal BookValue, decimal Adjustment,
    IReadOnlyList<string> StockAccounts, ValuationAccountDto OpeningAdjustmentAccount, ValuationAccountDto ValuationDifferenceAccount,
    IReadOnlyList<ValuationGapDto> PendingGaps, decimal PendingGapTotal, int FallbackLines,
    IReadOnlyList<SalesCostSwitchDto> History, int FallbackMovements = 0);

/// <summary>
/// Switch how sales are costed. Switching on states the adjustment the caller was shown (<c>ExpectedAdjustment</c>):
/// if the figure has moved since, nothing is posted and the new figure is returned to be confirmed again.
/// </summary>
public record SwitchSalesCostRequest(string Method, decimal? ExpectedAdjustment);

/// <summary>
/// One product (and, with the per-location scope, one location): the opening, what came in and went out,
/// the closing at the weighted average beside the same quantity at today's cost price.
/// <para>Value identity: closing = opening + in − out + moved in − moved out − negative-stock gap.</para>
/// </summary>
public record StockValuationRowDto(
    Guid ProductId, string Sku, string NameAr, string NameFr, string NameEn,
    Guid? BranchId, string? BranchNameAr, string? BranchNameFr, string? BranchNameEn,
    decimal OpeningQty, decimal OpeningValue,
    decimal InQty, decimal InValue, decimal OutQty, decimal OutValue,
    decimal MovedInQty, decimal MovedInValue, decimal MovedOutQty, decimal MovedOutValue,
    decimal ClosingQty, decimal ClosingValue, decimal? AverageCost,
    decimal ReferenceCost, decimal ReferenceValue, decimal Difference,
    decimal NegativeStockGap,
    decimal PostedCostOfSales, decimal ShadowCostOfSales,
    int OutputsAtReference, int OutflowsWithoutAverage, int Unclassified);

public record StockValuationTotalsDto(
    decimal OpeningValue, decimal InValue, decimal OutValue, decimal ClosingValue, decimal ReferenceValue,
    decimal Difference, decimal NegativeStockGap, decimal InTransitValue,
    decimal PostedCostOfSales, decimal ShadowCostOfSales);

public record StockValuationReportDto(
    StockValuationSettingsDto Settings, DateTime GeneratedAtUtc,
    IReadOnlyList<StockValuationRowDto> Rows, StockValuationTotalsDto Totals);

/// <summary>One movement of one product as the shadow valued it, in the order it was replayed.</summary>
public record StockValuationLineDto(
    Guid MovementId, DateTime AtUtc, string Kind, Guid BranchId, string? BranchNameAr, string? BranchNameFr, string? BranchNameEn,
    string? DocumentNumber,
    decimal QtyIn, decimal QtyOut,
    decimal? ReferenceUnitCost, decimal? ShadowUnitCost, decimal ShadowValue,
    decimal PoolQtyAfter, decimal? AverageAfter, decimal Gap,
    IReadOnlyList<string> Notes);

public record StockValuationProductDto(
    StockValuationSettingsDto Settings, StockValuationRowDto? Summary, IReadOnlyList<StockValuationRowDto> Places,
    IReadOnlyList<StockValuationLineDto> Lines);

// Manufacturing costing choices and account map (Phase C1, owner 2026-11-08): the accountant's open answers as settings.

/// <summary>
/// The choices as saved (Phase C1, completed 2026-11-09 with C-07, C-09, C-12 and the accountant's confirmation).
/// <c>StockOperationsByServer</c>: the server's configuration switches stock operations on whatever the panel says
/// (test and development databases).
/// </summary>
public record ManufacturingCostingSettingsDto(
    string Model, decimal OverheadPerLabourHour, string ReleaseRule, string EmployerBurdenUse, decimal EmployerBurdenPercent,
    string MonthEndWorkInProgress, string LateCosts, string Variances, DateTime? UpdatedAtUtc, string? UpdatedBy,
    string ExpenseBasis = "BeforeRecoverableVat", decimal ExpenseVatPercent = 0m, string ScrapRecovery = "None",
    decimal ScrapRecoveryPercent = 0m, bool StockOperations = false, bool StockOperationsByServer = false,
    bool AccountantApproved = false, string? ApprovedBy = null, DateTime? ApprovedAtUtc = null, string? PostingFromMonth = null,
    string LabourAbsorption = "BlendedActualRate");

public record UpdateManufacturingCostingSettingsRequest(
    string Model, decimal OverheadPerLabourHour, string ReleaseRule, string EmployerBurdenUse, decimal EmployerBurdenPercent,
    string MonthEndWorkInProgress, string LateCosts, string Variances,
    string ExpenseBasis = "BeforeRecoverableVat", decimal ExpenseVatPercent = 0m, string ScrapRecovery = "None",
    decimal ScrapRecoveryPercent = 0m, bool StockOperations = false, bool AccountantApproved = false, string? PostingFromMonth = null,
    string LabourAbsorption = "BlendedActualRate");

/// <summary>One slot of the account map, with the account it names (null: unmapped).</summary>
public record ManufacturingAccountSlotDto(
    string Slot, Guid? LedgerAccountId, string? AccountCode, string? AccountNameAr, string? AccountNameFr, string? AccountNameEn,
    DateTime? UpdatedAtUtc, string? UpdatedBy);

public record ManufacturingAccountMapLine(string Slot, Guid? LedgerAccountId);

public record UpdateManufacturingAccountMapRequest(IReadOnlyList<ManufacturingAccountMapLine> Lines);
