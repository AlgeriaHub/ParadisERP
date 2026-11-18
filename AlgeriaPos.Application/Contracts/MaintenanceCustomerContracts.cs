namespace AlgeriaPos.Application.Contracts;

// ---------- Phase M5a: customer jobs, pricing and invoicing (docs/maintenance/plan.md §2.1, §3.2, §4 MS-06, MS-20…MS-25, MS-34) ----------

/// <summary>An existing customer a customer job may name.</summary>
public record MaintenanceCustomerOptionDto(Guid Id, string Code, string FullName);

/// <summary>A catalogue product, to say what a customer's equipment is (optional) or to give a repair rate (MS-23).</summary>
public record MaintenanceProductOptionDto(Guid Id, string Sku, string NameAr, string NameFr, string NameEn);

/// <summary>What the New job form offers for a customer job: the customers, the products, and whether MS-06 is on.</summary>
public record MaintenanceNewJobOptionsDto(
    bool CustomerJobs, IReadOnlyList<MaintenanceCustomerOptionDto> Customers, IReadOnlyList<MaintenanceProductOptionDto> Products);

/// <summary>
/// A customer job's Price card. <c>Basis</c> is MS-20 in force; the proposal's pieces are those of that basis (plan §3.2).
/// Every amount is before tax.
/// <para>
/// <b>Privacy (plan §6.2).</b> The cost, the loss, and the cost-plus proposal (which gives the cost back by a division)
/// are given only to a reader who may see the job's cost (<c>CostShown</c>); anyone else sees the itemised pieces, the
/// price and, under MS-25, only that the price is below the cost (<c>BelowCost</c>), never by how much.
/// </para>
/// </summary>
public record MaintenanceJobPriceDto(
    Guid JobId, string Number, string Kind, string Status,
    string Basis, decimal LabourChargePerHour, decimal MarkupPercent, decimal Hours, decimal PartsUsedQty, decimal OutsideWork,
    decimal? Parts, decimal? Labour, decimal? Markup, decimal? CostBase, decimal? Proposed,
    bool CostShown, bool CostAvailable, decimal? Cost, bool? CostProvisional,
    decimal? Price, string? PricedBasis, decimal? ProposedAtPricing, string? PricedBy, DateTime? PricedAtUtc,
    string TaxMode, decimal TaxRateWhenTaxed,
    string BelowCostRule, bool BelowCost, decimal? Loss, string? BelowCostConfirmedBy, DateTime? BelowCostConfirmedAtUtc,
    string CustomerIncome, string IncomeAccountCode,
    Guid? InvoiceId, string? InvoiceNumber);

/// <summary>
/// Sets a customer job's price before tax (<c>maintenance.pricing</c>), and whether its invoice will carry VAT (null
/// leaves it as it is; every job starts untaxed). Under MS-25 "needs confirmation", a price below the job's cost is saved
/// only with <c>ConfirmBelowCost</c>; otherwise it is saved with a warning. Never refused for being below the cost.
/// </summary>
public record SetMaintenanceJobPriceRequest(decimal Price, string? TaxMode = null, bool ConfirmBelowCost = false);

/// <summary>
/// Raises the one invoice of a completed customer job (<c>finance.invoices.create</c> + <c>maintenance.view</c>).
/// <c>TaxMode</c> is the seller's tick (null: the job's, untaxed unless ticked); the due date is mandatory as on every invoice.
/// </summary>
public record RaiseMaintenanceJobInvoiceRequest(
    DateTime DueDateUtc, DateTime? IssuedAtUtc = null, string? TaxMode = null, string? Notes = null, string? PaymentMethodKey = null);

/// <summary>The invoice raised from a job, what it billed and on which income account, any warning, and the job as it now stands.</summary>
public record MaintenanceJobInvoiceDto(
    Guid InvoiceId, string InvoiceNumber, decimal SubTotalHt, decimal TaxTotal, decimal GrandTotalTtc, string TaxMode,
    string IncomeAccountCode, string? Warning, MaintenanceJobDto Job);

/// <summary>MS-23: a product's usual recharge for repairing one unit, before tax.</summary>
public record MaintenanceRepairRateDto(
    Guid ProductId, string Sku, string NameAr, string NameFr, string NameEn, decimal RatePerUnit, string? UpdatedBy, DateTime UpdatedAtUtc);

/// <summary>Maintenance › Repair rates: the rates, the rentable products a rate may be given to, and MS-23 as saved.</summary>
public record MaintenanceRepairRatesDto(
    string ReturnRecharge, IReadOnlyList<MaintenanceRepairRateDto> Rates, IReadOnlyList<MaintenanceProductOptionDto> Products);

public record SetMaintenanceRepairRateRequest(decimal RatePerUnit);

/// <summary>
/// What the return-note screen reads: whether it should suggest a recharge (the module on and MS-23 "suggested from a
/// rate") and the rate per unit of each product. Only a suggestion: the clerk types the recharge.
/// </summary>
public record MaintenanceRepairSuggestionsDto(bool Enabled, IReadOnlyList<MaintenanceRepairSuggestionDto> Rates);

public record MaintenanceRepairSuggestionDto(Guid ProductId, decimal RatePerUnit);
