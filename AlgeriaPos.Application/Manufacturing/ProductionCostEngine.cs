using AlgeriaPos.Domain;

namespace AlgeriaPos.Application.Manufacturing;

/// <summary>What one labour hour of an employee's payroll month is costed at, and why.</summary>
public enum LabourRateKind
{
    /// <summary>From the employee's approved or paid payslip lines for the month.</summary>
    Actual = 0,
    /// <summary>From a full month computed by the payroll engine: the month's run is not approved yet.</summary>
    Standard = 1,
    /// <summary>Nothing to cost the hour with (no base rate, settings payroll refuses); the hours count, the money is 0.</summary>
    None = 2,
}

/// <summary>
/// One employee-month's rate. <see cref="EmployerCost"/> and <see cref="PaidHours"/> are the rate's own
/// numerator and denominator when it is actual — the labour absorption report needs them; a standard
/// rate leaves them null. <see cref="WithoutEmployerSocial"/> says the payslips carried no employer
/// social contribution (new runs compute none, costing plan F5 and D11).
/// </summary>
/// <param name="OvertimePremium">
/// C-05: the employer cost of the month's overtime premium — on each payslip overtime line, the part above the normal
/// rate (amount × (multiplier − 1) ÷ multiplier), plus its share of the line's employer social and levies when the line
/// is in the social base. 0 when payroll paid no overtime, and always 0 on a standard rate. Inside <see cref="EmployerCost"/>.
/// </param>
/// <param name="PaidOvertimeHours">C-05: the overtime hours the payslips paid (inside <see cref="PaidHours"/>; a band in days is that many full days).</param>
/// <param name="MarkedOvertimeHours">C-05: the hours marked overtime on every booking of the employee-month, on every order and row.</param>
public sealed record LabourRate(
    decimal? PerHour, LabourRateKind Kind, string? Reason = null,
    decimal? EmployerCost = null, decimal? PaidHours = null, bool WithoutEmployerSocial = false,
    decimal OvertimePremium = 0m, decimal PaidOvertimeHours = 0m, decimal MarkedOvertimeHours = 0m)
{
    public static LabourRate Missing(string reason) => new(null, LabourRateKind.None, reason);
}

/// <summary>An order material as recorded: the frozen estimate and every quantity and value moved.</summary>
public sealed record CostingMaterial(
    Guid Id, string Sku, ManufacturingRole Role, string BaseUnitCode,
    decimal RequiredBaseQty, decimal EstimatedUnitCost,
    decimal IssuedBaseQty, decimal ReturnedBaseQty, decimal ConsumedBaseQty, decimal ScrappedBaseQty,
    decimal IssuedValue, decimal ReturnedValue);

/// <summary>
/// A labour booking: the hours one employee worked on the order on a day, in a payroll month, and how many of them
/// were marked overtime (C-05; 0 to <see cref="Hours"/>).
/// </summary>
public sealed record CostingBooking(Guid EmployeeId, Guid StageId, DateOnly Day, int PayrollYear, int PayrollMonth, decimal Hours,
    decimal OvertimeHours = 0m);

/// <summary>C-05: how direct labour is absorbed.</summary>
public enum LabourAbsorption
{
    /// <summary>Every worked hour at the employee-month's blended actual rate, overtime premium included (§3.3, suggested).</summary>
    BlendedActualRate = 0,
    /// <summary>
    /// Normal hours at the rate without the overtime premium; the premium spread over the hours bookings mark as overtime
    /// (<see cref="ProductionCostEngine.HourRatesOf"/>). Premium no marked hour carries stays in the wage charges.
    /// </summary>
    PremiumToOvertime = 1,
}

/// <summary>What one normal hour and one overtime hour of an employee-month cost under the policy; null without a rate.</summary>
public sealed record HourRates(decimal? Normal, decimal? Overtime)
{
    /// <summary>The overtime premium per marked hour (0 when the policy or the month gives none).</summary>
    public decimal Premium => Normal is decimal n && Overtime is decimal o ? o - n : 0m;
}

/// <summary>
/// An expense of the order. <see cref="Settled"/>: it names a payment voucher that is approved or paid;
/// one with no voucher, or a voucher still pending or cancelled, is a figure somebody typed, not yet backed.
/// </summary>
public sealed record CostingExpense(Guid Id, decimal Amount, DateTime AtUtc, bool Settled = false);

/// <summary>
/// A finished-goods release, with the output-stage good quantity approved by the time it was made
/// (<see cref="GoodToDate"/>) and the company's day it fell on.
/// </summary>
public sealed record CostingRelease(Guid Id, string Number, decimal BaseQty, DateTime AtUtc, DateOnly Day, decimal GoodToDate);

/// <summary>
/// How a release is valued while the order is open (C-11). The last release of a completed order always carries
/// whatever is left, whichever rule valued the earlier ones.
/// </summary>
public enum ReleaseValuationRule
{
    /// <summary>Standard material per unit at the issue averages plus the conversion to date per good unit not yet released (§2.9, suggested).</summary>
    StandardMaterialPlusConversion = 0,
    /// <summary>The frozen estimated unit cost.</summary>
    EstimatedUnitCost = 1,
    /// <summary>The whole cost to date, less what earlier releases carried, over the good units not yet released.</summary>
    CostToDate = 2,
}

/// <summary>
/// The figures the accountant decides (costing plan section 7). Until they answer, the admin chooses them in
/// Manufacturing › Costing settings (owner, 2026-11-08); the defaults are the suggested answers and change
/// nothing: no overhead (C-08), the §2.9 release rule (C-11), no employer burden (C-06).
/// <see cref="Approved"/> stays false — a choice made in the meantime is not a signature — so no sheet computed
/// under it can read as an approved cost, whatever its inputs.
/// </summary>
/// <param name="EmployerBurdenPercent">C-06: added to the hourly rate of a payroll month whose payslips carried no employer social contribution; 0 adds nothing.</param>
/// <param name="ExpenseVatPercent">C-07: the recoverable VAT taken out of each recorded expense (amount × 100 ÷ (100 + rate)); 0 takes the amount as recorded.</param>
/// <param name="ScrapRecoveryPercent">
/// C-09: rejects kept as scrap recover this share of the standard material value per unit once the order is completed;
/// 0 recovers nothing. A recovery value passed in the input is used as it is.
/// </param>
/// <param name="LabourAbsorption">C-05: the blended rate (default), or the overtime premium kept to the hours marked overtime.</param>
public sealed record CostingPolicy(
    decimal OverheadPerLabourHour, bool Approved = false,
    ReleaseValuationRule ReleaseRule = ReleaseValuationRule.StandardMaterialPlusConversion,
    decimal EmployerBurdenPercent = 0m, decimal ExpenseVatPercent = 0m, decimal ScrapRecoveryPercent = 0m,
    LabourAbsorption LabourAbsorption = LabourAbsorption.BlendedActualRate)
{
    public static CostingPolicy Default { get; } = new(0m);
}

public sealed record CostingInput(
    decimal PlannedBaseQty, decimal AcceptedBaseQty, decimal RejectedBaseQty, bool Completed,
    decimal StandardHours, decimal? StandardRate,
    IReadOnlyList<CostingMaterial> Materials,
    IReadOnlyList<CostingBooking> Bookings,
    IReadOnlyDictionary<(Guid EmployeeId, int Year, int Month), LabourRate> Rates,
    IReadOnlyList<CostingExpense> Expenses,
    IReadOnlyList<CostingRelease> Releases,
    CostingPolicy Policy,
    decimal RecoveryValue = 0m);

/// <summary>Cost by element (costing plan §2.2). Recovery is subtracted: C = M + K + A + L + E + O − R.</summary>
public sealed record CostElements(
    decimal Materials, decimal Consumables, decimal Accessories, decimal Labour, decimal Overhead, decimal Expenses, decimal Recovery)
{
    public decimal Total => Materials + Consumables + Accessories + Labour + Overhead + Expenses - Recovery;
}

public sealed record MaterialCost(
    Guid Id, string Sku, ManufacturingRole Role, string BaseUnitCode,
    decimal RequiredBaseQty, decimal EstimatedUnitCost, decimal EstimatedValue,
    decimal NetBaseQty, decimal NetValue, decimal IssueAverage,
    decimal ConsumedValue, decimal ScrapValue, decimal AtLineBaseQty, decimal AtLineValue,
    decimal? StandardBaseQty, decimal? UsageVariance, decimal? PriceVariance);

/// <summary>
/// An employee-month on the order. <see cref="Rate"/> is a normal hour's; <see cref="OvertimeHours"/> of the hours were marked
/// overtime and cost <see cref="OvertimeRate"/> (the same rate under the blended choice, C-05).
/// </summary>
public sealed record LabourCost(
    Guid EmployeeId, int Year, int Month, decimal Hours, LabourRateKind Kind, decimal? Rate, decimal Cost, string? Reason,
    bool WithoutEmployerSocial, decimal OvertimeHours = 0m, decimal? OvertimeRate = null);

public sealed record ReleaseCost(Guid Id, string Number, decimal BaseQty, decimal Value, decimal UnitValue, bool CarriesRemainder);

public sealed record CostVariances(
    decimal MaterialUsage, decimal MaterialPrice, decimal LabourEfficiency, decimal LabourRate,
    decimal Overhead, decimal Expenses, decimal Recovery, decimal Total);

/// <summary>A reason a figure is not final, in a code the screens translate; <see cref="Value"/> when it has one (hours, an amount).</summary>
public sealed record CostNote(string Code, decimal? Value = null);

/// <summary>
/// What each actual element rests on, in a word the screens translate (<see cref="ProductionCostEngine"/>'s
/// <c>Element*</c> constants): an element that is not <c>approved</c>, <c>recorded</c> or <c>none</c> keeps
/// the whole sheet provisional. A missing component is said, never shown as a zero.
/// </summary>
public sealed record CostElementStatuses(
    string Materials, string Consumables, string Accessories, string Labour, string Overhead, string Expenses, string Recovery);

public sealed record ProductionCostSheet(
    CostElements Estimate, decimal? EstimatedUnitCost,
    CostElements Actual, decimal? ActualUnitCost,
    decimal AtLineValue, decimal ReleasedValue, decimal WorkInProgress, decimal KeptInCharges,
    decimal LabourHours, decimal StandardHoursForOutput,
    IReadOnlyList<MaterialCost> Materials, IReadOnlyList<LabourCost> Labour, IReadOnlyList<ReleaseCost> Releases,
    CostVariances? Variances, decimal? Spoilage, IReadOnlyList<CostNote> Notes,
    CostElementStatuses Statuses, string Status, bool EmployerSocialMissing);

/// <summary>
/// The manufacturing cost of one production order, computed from what was recorded (costing plan
/// section 2). Pure: no database, no clock, nothing written; the same inputs give the same sheet, to
/// the last decimal, on SQLite and SQL Server alike. Values are full precision; callers round where
/// they show them.
///
/// <para>
/// <b>Materials</b> at the value frozen on their movements — the product's reference cost at issue
/// today, the weighted average once the valuation switch is signed (D9/D43). <b>Labour</b> at the
/// rate of each employee's payroll month. <b>Releases</b> at standard material usage plus the
/// conversion cost to date per good unit not yet released; the last release of a completed order
/// carries the remainder (§2.9, D13). Nothing here is posted or frozen: that is Phase B and D.
/// </para>
/// </summary>
public static class ProductionCostEngine
{
    /// <summary>
    /// The calculation a B1 sheet records as its <c>CalculationVersion</c>. A frozen sheet is replayed or compared
    /// with a live computation only under the same version; a change to what this engine computes changes it.
    /// </summary>
    public const string Version = "B1.0";

    public const string NoteReferenceCost = "material_reference_cost";
    public const string NoteStandardRate = "labour_standard_rate";
    public const string NoteUnvaluedLabour = "labour_unvalued";
    public const string NoteNoEmployerSocial = "labour_without_employer_social";
    public const string NoteNoLabourEstimate = "labour_estimate_unavailable";
    public const string NoteNoOverhead = "overhead_not_set";
    public const string NoteUnvaluedMaterial = "material_unvalued";
    public const string NoteNotFrozen = "cost_not_frozen";
    public const string NoteFinalReleaseNegative = "final_release_below_zero";
    public const string NoteUnsettledExpense = "expense_unsettled";
    public const string NotePolicyNotApproved = "costing_policy_not_approved";
    /// <summary>The employer burden of C-06 was added to hours whose payslips carried no employer social contribution.</summary>
    public const string NoteEmployerBurden = "labour_employer_burden";
    /// <summary>
    /// C-05, premium to overtime hours: hours marked overtime that carry no premium — the month's payroll paid no overtime,
    /// or it is not approved yet (a standard rate has none). Value: those hours.
    /// </summary>
    public const string NoteOvertimeWithoutPremium = "labour_overtime_without_premium";
    /// <summary>
    /// C-05, premium to overtime hours: payroll paid more overtime hours than bookings marked in a month this order's labour
    /// comes from, so part of that premium stays in the wage charges (the labour absorption report shows how much).
    /// </summary>
    public const string NoteOvertimePremiumInCharges = "labour_overtime_premium_in_charges";

    /// <summary>
    /// The sheet: approved only when every element is final <b>and</b> the costing policy is signed; else
    /// provisional (an input provisional, missing or undecided, or the method itself not approved).
    /// </summary>
    public const string StatusApproved = "approved";
    public const string StatusProvisional = "provisional";

    /// <summary>Element statuses. Only approved, recorded and none leave an element final.</summary>
    public const string ElementApproved = "approved";
    public const string ElementRecorded = "recorded";
    public const string ElementNone = "none";
    /// <summary>Valued at the product's reference cost at issue, until the valuation method is signed (D9/D43).</summary>
    public const string ElementReferenceCost = "reference_cost";
    /// <summary>
    /// Valued, but on an input not approved yet: labour at the standard rate or without its employer
    /// share; an expense whose voucher is missing, pending or cancelled.
    /// </summary>
    public const string ElementProvisional = "provisional";
    /// <summary>Part of it has no value at all: hours without a rate, a material issued at a cost of zero.</summary>
    public const string ElementIncomplete = "incomplete";
    /// <summary>A rate nobody has approved yet (overhead): no value, never a zero.</summary>
    public const string ElementNotSet = "not_set";
    /// <summary>Rejects were kept but their recovery value is not recorded yet (Phase B).</summary>
    public const string ElementNotRecorded = "not_recorded";

    private static readonly HashSet<string> Final = [ElementApproved, ElementRecorded, ElementNone];

    public static ProductionCostSheet Compute(CostingInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        // C-07: expenses before recoverable VAT, as the accountant chose; the rate 0 keeps them as recorded.
        if (input.Policy.ExpenseVatPercent > 0m)
            input = input with
            {
                Expenses = input.Expenses.Select(e => e with { Amount = e.Amount * 100m / (100m + input.Policy.ExpenseVatPercent) }).ToList(),
            };
        var notes = new List<CostNote> { new(NoteReferenceCost) };

        // ---------- materials ----------

        var materials = input.Materials.Select(Material).ToList();
        if (input.Materials.Any(m => m.IssuedBaseQty > 0 && m.IssuedValue == 0m))
            notes.Add(new CostNote(NoteUnvaluedMaterial));

        // ---------- labour ----------

        var premiumPolicy = input.Policy.LabourAbsorption == LabourAbsorption.PremiumToOvertime;
        var hourRates = new Dictionary<(Guid, int, int), HourRates>();
        var premiumLeftInCharges = false;
        var labour = input.Bookings
            .GroupBy(b => (b.EmployeeId, b.PayrollYear, b.PayrollMonth))
            .Select(g =>
            {
                var rate = input.Rates.GetValueOrDefault((g.Key.EmployeeId, g.Key.PayrollYear, g.Key.PayrollMonth))
                    ?? LabourRate.Missing("no_rate");
                var hours = g.Sum(b => b.Hours);
                var overtimeHours = g.Sum(Overtime);
                var rates = HourRatesOf(rate, input.Policy, overtimeHours);
                hourRates[g.Key] = rates;
                if (premiumPolicy && rate.Kind == LabourRateKind.Actual && rate.OvertimePremium > 0m
                    && rate.PaidOvertimeHours > Math.Max(rate.MarkedOvertimeHours, overtimeHours))
                    premiumLeftInCharges = true;
                // Under the blended rate (and whenever no premium applies) every hour costs the same, computed exactly as before.
                var cost = rates.Premium == 0m
                    ? hours * (rates.Normal ?? 0m)
                    : (hours - overtimeHours) * rates.Normal!.Value + overtimeHours * rates.Overtime!.Value;
                return new LabourCost(g.Key.EmployeeId, g.Key.PayrollYear, g.Key.PayrollMonth, hours, rate.Kind, rates.Normal,
                    cost, rate.Reason, rate.WithoutEmployerSocial, overtimeHours, rates.Overtime);
            })
            .OrderBy(l => l.Year).ThenBy(l => l.Month).ThenBy(l => l.EmployeeId)
            .ToList();
        var labourHours = labour.Sum(l => l.Hours);
        if (labour.Where(l => l.Kind == LabourRateKind.Standard).Sum(l => l.Hours) is var standardHours and > 0)
            notes.Add(new CostNote(NoteStandardRate, standardHours));
        if (labour.Where(l => l.Kind == LabourRateKind.None).Sum(l => l.Hours) is var unvaluedHours and > 0)
            notes.Add(new CostNote(NoteUnvaluedLabour, unvaluedHours));
        if (labour.Any(l => l.Kind != LabourRateKind.None && l.WithoutEmployerSocial))
            notes.Add(new CostNote(input.Policy.EmployerBurdenPercent > 0m ? NoteEmployerBurden : NoteNoEmployerSocial,
                input.Policy.EmployerBurdenPercent > 0m ? input.Policy.EmployerBurdenPercent : null));
        // C-05: said only under the premium choice; under the blended rate a marked hour is just an hour.
        if (premiumPolicy)
        {
            if (labour.Where(l => l.OvertimeHours > 0m && l.Kind != LabourRateKind.None && l.OvertimeRate == l.Rate).Sum(l => l.OvertimeHours)
                is var withoutPremium and > 0)
                notes.Add(new CostNote(NoteOvertimeWithoutPremium, withoutPremium));
            if (premiumLeftInCharges) notes.Add(new CostNote(NoteOvertimePremiumInCharges));
        }

        var overheadRate = input.Policy.OverheadPerLabourHour;
        if (overheadRate == 0m) notes.Add(new CostNote(NoteNoOverhead));
        if (input.Expenses.Where(e => !e.Settled).Sum(e => e.Amount) is var unsettled and > 0)
            notes.Add(new CostNote(NoteUnsettledExpense, unsettled));
        if (!input.Policy.Approved) notes.Add(new CostNote(NotePolicyNotApproved));
        if (input.StandardRate is null) notes.Add(new CostNote(NoteNoLabourEstimate));

        // ---------- the estimate ----------

        var estimate = new CostElements(
            Materials: materials.Where(m => Group(m.Role) == 0).Sum(m => m.EstimatedValue),
            Consumables: materials.Where(m => Group(m.Role) == 1).Sum(m => m.EstimatedValue),
            Accessories: materials.Where(m => Group(m.Role) == 2).Sum(m => m.EstimatedValue),
            Labour: input.StandardHours * (input.StandardRate ?? 0m),
            Overhead: input.StandardHours * overheadRate,
            Expenses: 0m,
            Recovery: 0m);
        decimal? estimatedUnitCost = input.PlannedBaseQty > 0 ? estimate.Total / input.PlannedBaseQty : null;

        // C-09: rejects kept as scrap, valued at the chosen share of the standard material per unit, once completed.
        var recovery = input.RecoveryValue;
        if (recovery == 0m && input.Completed && input.Policy.ScrapRecoveryPercent > 0m && input.RejectedBaseQty > 0m && input.PlannedBaseQty > 0m)
            recovery = input.RejectedBaseQty * materials.Sum(m => m.RequiredBaseQty / input.PlannedBaseQty * m.IssueAverage)
                * input.Policy.ScrapRecoveryPercent / 100m;

        // ---------- actual to date ----------

        var actual = new CostElements(
            Materials: materials.Where(m => Group(m.Role) == 0).Sum(m => m.NetValue),
            Consumables: materials.Where(m => Group(m.Role) == 1).Sum(m => m.NetValue),
            Accessories: materials.Where(m => Group(m.Role) == 2).Sum(m => m.NetValue),
            Labour: labour.Sum(l => l.Cost),
            Overhead: labourHours * overheadRate,
            Expenses: input.Expenses.Sum(e => e.Amount),
            Recovery: recovery);
        decimal? actualUnitCost = input.AcceptedBaseQty > 0 ? actual.Total / input.AcceptedBaseQty : null;

        // ---------- releases ----------

        var releases = Releases(input, materials, hourRates, overheadRate, actual.Total, estimatedUnitCost, notes);
        var released = releases.Sum(r => r.Value);
        var atLine = materials.Sum(m => m.AtLineValue);
        var completedWithoutRelease = input.Completed && releases.Count == 0;
        var workInProgress = input.Completed ? 0m : actual.Total - released;
        // A completed order with nothing to release leaves its whole cost in the charges (D13).
        var keptInCharges = completedWithoutRelease ? actual.Total : 0m;
        if (input.Completed) notes.Add(new CostNote(NoteNotFrozen));

        // ---------- variances, against the estimate scaled to the accepted output ----------

        CostVariances? variances = null;
        decimal standardHoursForOutput = 0m;
        if (input.AcceptedBaseQty > 0 && input.PlannedBaseQty > 0)
        {
            var scale = input.AcceptedBaseQty / input.PlannedBaseQty;
            standardHoursForOutput = input.StandardHours * scale;
            var standardRate = input.StandardRate ?? 0m;
            var usage = materials.Sum(m => m.UsageVariance ?? 0m);
            var price = materials.Sum(m => m.PriceVariance ?? 0m);
            var efficiency = (labourHours - standardHoursForOutput) * standardRate;
            var rate = actual.Labour - labourHours * standardRate;
            var overhead = actual.Overhead - standardHoursForOutput * overheadRate;
            var expenses = actual.Expenses - estimate.Expenses * scale;
            var recoveryVariance = -actual.Recovery;
            variances = new CostVariances(usage, price, efficiency, rate, overhead, expenses, recoveryVariance,
                usage + price + efficiency + rate + overhead + expenses + recoveryVariance);
        }

        decimal? spoilage = input.AcceptedBaseQty + input.RejectedBaseQty > 0
            ? (actual.Total + actual.Recovery) * input.RejectedBaseQty / (input.AcceptedBaseQty + input.RejectedBaseQty) - actual.Recovery
            : null;

        // ---------- what each figure rests on ----------

        string MaterialStatus(int group)
        {
            var mine = input.Materials.Where(m => Group(m.Role) == group && m.IssuedBaseQty > 0).ToList();
            if (mine.Count == 0) return ElementNone;
            return mine.Any(m => m.IssuedValue == 0m) ? ElementIncomplete : ElementReferenceCost;
        }
        // Employer social contributions absent from the payroll the labour came from (D11): said, not costed as
        // zero, and the labour stays provisional until the accountant decides how they count (C-06).
        var employerSocialMissing = labour.Any(l => l.Kind != LabourRateKind.None && l.WithoutEmployerSocial);
        var labourStatus = labour.Count == 0 ? ElementNone
            : labour.Any(l => l.Kind == LabourRateKind.None) ? ElementIncomplete
            : labour.Any(l => l.Kind == LabourRateKind.Standard) || employerSocialMissing ? ElementProvisional
            : ElementApproved;
        var statuses = new CostElementStatuses(
            MaterialStatus(0), MaterialStatus(1), MaterialStatus(2), labourStatus,
            Overhead: overheadRate == 0m ? ElementNotSet : ElementApproved,
            Expenses: input.Expenses.Count == 0 ? ElementNone : input.Expenses.All(e => e.Settled) ? ElementRecorded : ElementProvisional,
            Recovery: recovery != 0m ? ElementRecorded : input.RejectedBaseQty > 0 ? ElementNotRecorded : ElementNone);
        string[] all = [statuses.Materials, statuses.Consumables, statuses.Accessories, statuses.Labour, statuses.Overhead,
            statuses.Expenses, statuses.Recovery];
        var status = input.Policy.Approved && all.All(Final.Contains) ? StatusApproved : StatusProvisional;

        return new ProductionCostSheet(
            estimate, estimatedUnitCost, actual, actualUnitCost,
            atLine, released, workInProgress, keptInCharges,
            labourHours, standardHoursForOutput,
            materials, labour, releases, variances, spoilage, notes,
            statuses, status, employerSocialMissing);

        MaterialCost Material(CostingMaterial m)
        {
            var netQty = m.IssuedBaseQty - m.ReturnedBaseQty;
            var netValue = m.IssuedValue - m.ReturnedValue;
            var average = m.IssuedBaseQty > 0 ? m.IssuedValue / m.IssuedBaseQty : m.EstimatedUnitCost;
            var atLineQty = Math.Max(0m, netQty - m.ConsumedBaseQty - m.ScrappedBaseQty);
            var atLineValue = atLineQty * average;
            var scrapValue = m.ScrappedBaseQty * average;
            // Consumption takes what is left, so consumed + scrap + at the line is the net value exactly.
            var consumedValue = netValue - scrapValue - atLineValue;
            decimal? standardQty = null, usageVariance = null, priceVariance = null;
            if (input.AcceptedBaseQty > 0 && input.PlannedBaseQty > 0)
            {
                standardQty = m.RequiredBaseQty * input.AcceptedBaseQty / input.PlannedBaseQty;
                usageVariance = (netQty - standardQty.Value) * m.EstimatedUnitCost;
                priceVariance = netValue - netQty * m.EstimatedUnitCost;
            }
            return new MaterialCost(m.Id, m.Sku, m.Role, m.BaseUnitCode, m.RequiredBaseQty, m.EstimatedUnitCost,
                m.RequiredBaseQty * m.EstimatedUnitCost, netQty, netValue, average, consumedValue, scrapValue, atLineQty, atLineValue,
                standardQty, usageVariance, priceVariance);
        }
    }

    /// <summary>
    /// Each release in the order it was made: standard material per unit at the issue averages, plus
    /// the conversion cost booked by its day (labour, overhead) and its moment (expenses) that earlier
    /// releases have not carried, spread over the good units approved and not yet released. The last
    /// release of a completed order carries whatever is left.
    /// </summary>
    private static List<ReleaseCost> Releases(
        CostingInput input, IReadOnlyList<MaterialCost> materials, IReadOnlyDictionary<(Guid, int, int), HourRates> hourRates,
        decimal overheadRate, decimal actualTotal, decimal? estimatedUnitCost, List<CostNote> notes)
    {
        var ordered = input.Releases.OrderBy(r => r.AtUtc).ThenBy(r => r.Number, StringComparer.Ordinal).ToList();
        var result = new List<ReleaseCost>(ordered.Count);
        if (ordered.Count == 0) return result;

        var materialPerUnit = input.PlannedBaseQty > 0
            ? materials.Sum(m => m.RequiredBaseQty / input.PlannedBaseQty * m.IssueAverage)
            : 0m;
        // A booking's conversion: its hours at the month's rate plus overhead; with a premium (C-05), its overtime hours at the overtime rate.
        decimal Conversion(CostingBooking b)
        {
            var rates = hourRates.GetValueOrDefault((b.EmployeeId, b.PayrollYear, b.PayrollMonth));
            var normal = rates?.Normal ?? 0m;
            if (rates is null || rates.Premium == 0m) return b.Hours * (normal + overheadRate);
            var overtime = Overtime(b);
            return (b.Hours - overtime) * normal + overtime * rates.Overtime!.Value + b.Hours * overheadRate;
        }
        decimal releasedQty = 0m, carriedConversion = 0m, releasedValue = 0m;
        for (var i = 0; i < ordered.Count; i++)
        {
            var release = ordered[i];
            var last = i == ordered.Count - 1;
            if (last && input.Completed)
            {
                var remainder = actualTotal - releasedValue;
                if (remainder < 0) notes.Add(new CostNote(NoteFinalReleaseNegative, remainder));
                result.Add(new ReleaseCost(release.Id, release.Number, release.BaseQty, remainder,
                    release.BaseQty > 0 ? remainder / release.BaseQty : 0m, CarriesRemainder: true));
                break;
            }

            var bookedHours = input.Bookings.Where(b => b.Day <= release.Day);
            var conversionToDate =
                bookedHours.Sum(Conversion)
                + input.Expenses.Where(e => e.AtUtc <= release.AtUtc).Sum(e => e.Amount);
            var unreleasedGood = release.GoodToDate - releasedQty;
            var units = unreleasedGood > 0 ? unreleasedGood : release.BaseQty;
            var conversionPerUnit = units > 0 ? Math.Max(0m, conversionToDate - carriedConversion) / units : 0m;
            var unitValue = input.Policy.ReleaseRule switch
            {
                // C-11, the simpler alternative: every partial release at the frozen estimate.
                ReleaseValuationRule.EstimatedUnitCost => estimatedUnitCost ?? materialPerUnit + conversionPerUnit,
                // C-11, cost to date: every material issued so far (front-loaded, as issued) plus the conversion,
                // less what earlier releases carried, over the good units not yet released.
                ReleaseValuationRule.CostToDate => units > 0
                    ? Math.Max(0m, materials.Sum(m => m.NetValue) + conversionToDate - releasedValue) / units
                    : 0m,
                _ => materialPerUnit + conversionPerUnit,
            };
            var value = release.BaseQty * unitValue;
            result.Add(new ReleaseCost(release.Id, release.Number, release.BaseQty, value, unitValue, CarriesRemainder: false));
            releasedQty += release.BaseQty;
            carriedConversion += release.BaseQty * conversionPerUnit;
            releasedValue += value;
        }
        return result;
    }

    /// <summary>
    /// C-05: what a normal and an overtime hour of the employee-month cost under the policy, the employer burden (C-06)
    /// included. Under the blended rate, or on a month that is not actual, or one whose payroll paid no overtime premium,
    /// both are the rate. Under "premium to overtime hours" on an actual month with a premium P:
    /// <list type="bullet">
    /// <item>normal = (employer cost − P) ÷ paid hours — the rate without the premium, on every paid hour, overtime ones too
    /// (an overtime hour's base pay is an ordinary hour's);</item>
    /// <item>overtime = normal + P ÷ max(overtime hours paid, hours marked overtime in the month, <paramref name="markedOnInput"/>).
    /// While bookings mark no more hours than payroll paid, each marked hour carries the premium payroll paid per overtime
    /// hour and what unmarked hours would have carried stays in the wage charges; marking more hours than were paid spreads
    /// the premium thinner, so it is never absorbed twice.</item>
    /// </list>
    /// </summary>
    /// <param name="markedOnInput">The hours marked on the input being costed, in case the rate's month total does not count them.</param>
    public static HourRates HourRatesOf(LabourRate rate, CostingPolicy policy, decimal markedOnInput = 0m)
    {
        ArgumentNullException.ThrowIfNull(rate);
        ArgumentNullException.ThrowIfNull(policy);
        if (rate.PerHour is not decimal blended) return new HourRates(null, null);
        decimal normal = blended, premium = 0m;
        if (policy.LabourAbsorption == LabourAbsorption.PremiumToOvertime && rate.Kind == LabourRateKind.Actual
            && rate.OvertimePremium > 0m && rate.EmployerCost is decimal cost && rate.PaidHours is decimal paid and > 0m)
        {
            normal = (cost - rate.OvertimePremium) / paid;
            var spread = Math.Max(rate.PaidOvertimeHours, Math.Max(rate.MarkedOvertimeHours, markedOnInput));
            premium = spread > 0m ? rate.OvertimePremium / spread : 0m;
        }
        // C-06: the admin's burden stands in for the employer share the payslips did not carry — on the premium too.
        if (rate.WithoutEmployerSocial && policy.EmployerBurdenPercent > 0m)
        {
            var factor = 1m + policy.EmployerBurdenPercent / 100m;
            normal *= factor;
            premium *= factor;
        }
        return new HourRates(normal, premium == 0m ? normal : normal + premium);
    }

    /// <summary>A booking's overtime hours, never below 0 nor above its hours.</summary>
    private static decimal Overtime(CostingBooking b) => Math.Clamp(b.OvertimeHours, 0m, Math.Max(0m, b.Hours));

    /// <summary>0 materials (raw, semi-finished), 1 consumables (consumable, spare part), 2 accessories.</summary>
    private static int Group(ManufacturingRole role) => role switch
    {
        ManufacturingRole.Consumable or ManufacturingRole.SparePart => 1,
        ManufacturingRole.Accessory => 2,
        _ => 0,
    };
}
