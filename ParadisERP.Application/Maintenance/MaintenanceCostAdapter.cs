using ParadisERP.Application.Manufacturing;
using ParadisERP.Domain;

namespace ParadisERP.Application.Maintenance;

/// <summary>One product's parts on a job: issued and returned, at the values their stock movements were frozen at.</summary>
public sealed record MaintenanceCostPart(
    Guid ProductId, string Sku, ManufacturingRole Role, string BaseUnitCode,
    decimal IssuedQty, decimal ReturnedQty, decimal IssuedValue, decimal ReturnedValue);

/// <summary>
/// What Maintenance › Settings says a job's cost is (MS-10 to MS-13, MS-40), already read:
/// <list type="bullet">
/// <item><see cref="LabourIncluded"/>: MS-10 is not "parts and outside work only" — labour is in the cost;</item>
/// <item><see cref="OverheadPerLabourHour"/>: MS-11 when MS-10 includes overhead, else 0 (and overhead is then not part of the cost at all);</item>
/// <item><see cref="WorkshopRatePerHour"/>: MS-12 "one workshop rate", or null for the payroll-derived rate per employee-month;</item>
/// <item><see cref="EmployerBurdenPercent"/>, <see cref="ExpenseVatPercent"/>: MS-13, from Manufacturing › Costing settings (C-06, C-07) or the job's own values;</item>
/// <item><see cref="LabourAbsorption"/>: C-05 as Manufacturing › Costing settings has it, since a job's hours are costed at production's rates;</item>
/// <item><see cref="Approved"/>: MS-40, the accountant's confirmation.</item>
/// </list>
/// </summary>
public sealed record MaintenanceCostSettings(
    bool LabourIncluded, bool OverheadIncluded, decimal OverheadPerLabourHour, decimal? WorkshopRatePerHour,
    decimal EmployerBurdenPercent, decimal ExpenseVatPercent, LabourAbsorption LabourAbsorption, bool Approved)
{
    public static MaintenanceCostSettings Default { get; } = new(true, false, 0m, null, 0m, 0m, LabourAbsorption.BlendedActualRate, false);
}

/// <summary>
/// One job as the adapter reads it: how many units, whether it is completed, its parts, its hours (a
/// <see cref="CostingBooking"/> each, the job as their stage), their payroll rates per employee-month, its outside work and the settings.
/// </summary>
public sealed record MaintenanceCostInput(
    decimal Qty, bool Completed,
    IReadOnlyList<MaintenanceCostPart> Parts,
    IReadOnlyList<CostingBooking> Bookings,
    IReadOnlyDictionary<(Guid EmployeeId, int Year, int Month), LabourRate> Rates,
    IReadOnlyList<CostingExpense> Expenses,
    MaintenanceCostSettings Settings);

/// <summary>
/// What each element of a job's cost rests on, in the engine's words (<see cref="ProductionCostEngine"/>'s <c>Element*</c>),
/// plus <see cref="MaintenanceCostAdapter.ElementExcluded"/> for an element MS-10 leaves out of the cost.
/// </summary>
public sealed record MaintenanceCostStatuses(string Parts, string Labour, string Overhead, string OutsideWork);

/// <summary>
/// A job's cost: C = parts + labour + outside work + overhead (plan §3.1). <see cref="LabourHours"/> are every hour booked on
/// the job, costed or not; <see cref="Labour"/> lists the hours that are costed, per employee-month. Full precision.
/// </summary>
public sealed record MaintenanceCostSheet(
    decimal Parts, decimal Labour, decimal Overhead, decimal OutsideWork, decimal Total,
    decimal LabourHours,
    IReadOnlyList<MaterialCost> PartLines, IReadOnlyList<LabourCost> LabourLines, IReadOnlyList<CostNote> Notes,
    MaintenanceCostStatuses Statuses, string Status);

/// <summary>
/// The maintenance costing adapter (docs/maintenance/plan.md §3.1, Phase M4): a job's cost through
/// <see cref="ProductionCostEngine.Compute"/>, unchanged. The engine's <see cref="ProductionCostEngine.Version"/> stays
/// what it is, so no frozen manufacturing sheet is touched. Pure: no database, no clock.
/// <list type="bullet">
/// <item>each product issued becomes a <see cref="CostingMaterial"/> — required = issued, estimate = the issues' average,
/// everything not returned consumed, so nothing is "at the line";</item>
/// <item>each booking stays a <see cref="CostingBooking"/> with the job as its stage; under MS-12 "one workshop rate" every
/// employee-month is costed at that rate (an all-in published rate: no burden is added, no premium spread);</item>
/// <item>each outside-work line becomes a <see cref="CostingExpense"/>, settled once its voucher is approved or paid;</item>
/// <item>no releases, no standard hours; planned and accepted quantity are the job's units (or 1); no scrap recovery;</item>
/// <item>the policy: overhead per MS-10/MS-11, burden and VAT per MS-13, C-05 as manufacturing, approved per MS-40.</item>
/// </list>
/// The adapter reads the actual elements, the labour lines and the notes, and ignores releases, variances and work in
/// progress. It drops the notes that do not apply to a job (no labour estimate, not frozen, a release below zero; no
/// reference cost without parts; overhead "not set" when MS-10 leaves overhead out) and says, instead, when MS-10 leaves
/// labour out or MS-12 costs it at the workshop rate.
/// </summary>
public static class MaintenanceCostAdapter
{
    /// <summary>MS-10 leaves this element out of the job's cost: final, never a zero that waits for something.</summary>
    public const string ElementExcluded = "excluded";

    /// <summary>MS-10 "parts and outside work only": the job has hours, and they stay a general wage cost. Value: those hours.</summary>
    public const string NoteLabourExcluded = "labour_excluded";

    /// <summary>MS-12 "one workshop rate": every hour at the rate set in the panel. Value: the rate.</summary>
    public const string NoteWorkshopRate = "labour_workshop_rate";

    /// <summary>The engine's notes a job never needs.</summary>
    private static readonly HashSet<string> Dropped =
    [
        ProductionCostEngine.NoteNoLabourEstimate, ProductionCostEngine.NoteNotFrozen, ProductionCostEngine.NoteFinalReleaseNegative,
    ];

    private static readonly HashSet<string> Final =
    [
        ProductionCostEngine.ElementApproved, ProductionCostEngine.ElementRecorded, ProductionCostEngine.ElementNone, ElementExcluded,
    ];

    /// <summary>The engine's input for a job.</summary>
    public static CostingInput InputFor(MaintenanceCostInput job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var s = job.Settings;
        var qty = job.Qty > 0 ? job.Qty : 1m;
        var materials = job.Parts.Select(p =>
        {
            var net = p.IssuedQty - p.ReturnedQty;
            return new CostingMaterial(p.ProductId, p.Sku, p.Role, p.BaseUnitCode,
                RequiredBaseQty: p.IssuedQty, EstimatedUnitCost: p.IssuedQty > 0 ? p.IssuedValue / p.IssuedQty : 0m,
                IssuedBaseQty: p.IssuedQty, ReturnedBaseQty: p.ReturnedQty, ConsumedBaseQty: net, ScrappedBaseQty: 0m,
                IssuedValue: p.IssuedValue, ReturnedValue: p.ReturnedValue);
        }).ToList();

        var bookings = s.LabourIncluded ? job.Bookings : [];
        IReadOnlyDictionary<(Guid, int, int), LabourRate> rates = s.WorkshopRatePerHour is decimal workshop
            ? bookings.Select(b => (b.EmployeeId, b.PayrollYear, b.PayrollMonth)).Distinct()
                .ToDictionary(k => k, _ => new LabourRate(workshop, LabourRateKind.Actual))
            : job.Rates;

        return new CostingInput(
            PlannedBaseQty: qty, AcceptedBaseQty: qty, RejectedBaseQty: 0m, Completed: job.Completed,
            StandardHours: 0m, StandardRate: null,
            Materials: materials, Bookings: bookings, Rates: rates, Expenses: job.Expenses, Releases: [],
            Policy: new CostingPolicy(
                s.OverheadIncluded && s.LabourIncluded ? s.OverheadPerLabourHour : 0m, s.Approved,
                ReleaseValuationRule.StandardMaterialPlusConversion,
                s.WorkshopRatePerHour is null ? s.EmployerBurdenPercent : 0m, s.ExpenseVatPercent, ScrapRecoveryPercent: 0m,
                s.WorkshopRatePerHour is null ? s.LabourAbsorption : LabourAbsorption.BlendedActualRate));
    }

    /// <summary>The job's cost, through the engine.</summary>
    public static MaintenanceCostSheet Compute(MaintenanceCostInput job)
    {
        var input = InputFor(job);
        var sheet = ProductionCostEngine.Compute(input);
        var s = job.Settings;
        var overheadIncluded = s.OverheadIncluded && s.LabourIncluded;
        var bookedHours = job.Bookings.Sum(b => b.Hours);

        var parts = PartsStatus(sheet.Statuses);
        var labour = s.LabourIncluded ? sheet.Statuses.Labour : ElementExcluded;
        var overhead = overheadIncluded ? sheet.Statuses.Overhead : ElementExcluded;
        var outside = sheet.Statuses.Expenses;
        var status = s.Approved && new[] { parts, labour, overhead, outside }.All(Final.Contains)
            ? ProductionCostEngine.StatusApproved
            : ProductionCostEngine.StatusProvisional;

        var issued = job.Parts.Any(p => p.IssuedQty > 0);
        var notes = sheet.Notes
            .Where(n => !Dropped.Contains(n.Code))
            .Where(n => issued || n.Code != ProductionCostEngine.NoteReferenceCost)
            .Where(n => overheadIncluded || n.Code != ProductionCostEngine.NoteNoOverhead)
            .ToList();
        if (!s.LabourIncluded && bookedHours > 0) notes.Add(new CostNote(NoteLabourExcluded, bookedHours));
        if (s.LabourIncluded && s.WorkshopRatePerHour is decimal rate && bookedHours > 0) notes.Add(new CostNote(NoteWorkshopRate, rate));

        var a = sheet.Actual;
        return new MaintenanceCostSheet(
            Parts: a.Materials + a.Consumables + a.Accessories, Labour: a.Labour, Overhead: a.Overhead, OutsideWork: a.Expenses,
            Total: a.Total, LabourHours: bookedHours,
            sheet.Materials, sheet.Labour, notes,
            new MaintenanceCostStatuses(parts, labour, overhead, outside), status);
    }

    /// <summary>The three material groups as one parts element: incomplete wins, then the reference cost, else none.</summary>
    private static string PartsStatus(CostElementStatuses s)
    {
        string[] groups = [s.Materials, s.Consumables, s.Accessories];
        if (groups.Contains(ProductionCostEngine.ElementIncomplete)) return ProductionCostEngine.ElementIncomplete;
        if (groups.Contains(ProductionCostEngine.ElementReferenceCost)) return ProductionCostEngine.ElementReferenceCost;
        return ProductionCostEngine.ElementNone;
    }
}
