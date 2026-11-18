using System.Text.Json;
using ParadiERP.Domain;

namespace ParadiERP.Application.Manufacturing;

/// <summary>
/// An employee-month's rate as a B1 sheet captured it. Payroll-derived: never sent to a caller as such. The overtime
/// figures (C-05) are captured only for a sheet frozen under "premium to overtime hours"; absent, they are 0.
/// </summary>
public sealed record CapturedRate(
    Guid EmployeeId, int Year, int Month, decimal? PerHour, LabourRateKind Kind, string? Reason,
    decimal? EmployerCost, decimal? PaidHours, bool WithoutEmployerSocial,
    decimal OvertimePremium = 0m, decimal PaidOvertimeHours = 0m, decimal MarkedOvertimeHours = 0m);

/// <summary>An order expense as the sheet saw it, with the voucher it named.</summary>
public sealed record CapturedExpense(Guid Id, decimal Amount, DateTime AtUtc, Guid? PaymentVoucherId);

public sealed record CapturedVoucher(Guid Id, PaymentVoucherStatus Status);

/// <summary>A labour booking's identity and hours, as captured.</summary>
public sealed record CapturedBooking(Guid Id, Guid EmployeeId, int PayrollYear, int PayrollMonth, decimal Hours);

public sealed record CapturedOutputRelease(Guid Id, string Number, decimal BaseQty, decimal UnitCostSnapshot, DateTime AtUtc);

/// <summary>
/// What a B1 sheet froze, read back: the engine's complete input, the engine's own release allocations,
/// notes and element statuses, and the identities of the rows it was computed from.
/// </summary>
public sealed record CostingCaptureSource(
    CostingInput Input,
    IReadOnlyList<ReleaseCost> ReleaseAllocations,
    IReadOnlyList<CostNote> Notes,
    CostElementStatuses? ElementStatuses,
    IReadOnlyList<CapturedRate> Rates,
    IReadOnlyList<CapturedExpense> Expenses,
    IReadOnlyList<CapturedVoucher> Vouchers,
    IReadOnlyList<CapturedBooking> Bookings,
    IReadOnlyList<Guid> MaterialMovementLines,
    IReadOnlyList<CapturedOutputRelease> OutputReleases);

/// <summary>
/// Reads the canonical source capture a B1 sheet stores (<c>ProductionCostSnapshots.CaptureAsync</c>) back into
/// the engine's input, so B2 can replay the frozen calculation without reading a single mutable row. Pure: no
/// database, no clock. A capture is read only under a calculation version this engine still computes; anything
/// else is answered with the reason, never guessed.
/// </summary>
public static class CostingCapture
{
    public const string ReasonNotCaptured = "source_not_captured";
    public const string ReasonUnreadable = "source_unreadable";
    public const string ReasonVersion = "method_version_unsupported";

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = false };

    public static (CostingCaptureSource? Source, string? Reason) Parse(string? json, string calculationVersion, ManufacturingCostSourceState state)
    {
        if (state != ManufacturingCostSourceState.Complete || string.IsNullOrWhiteSpace(json)) return (null, ReasonNotCaptured);
        if (calculationVersion != ProductionCostEngine.Version) return (null, ReasonVersion);
        try
        {
            var doc = JsonSerializer.Deserialize<Document>(json, Options);
            if (doc?.Materials is null || doc.Bookings is null || doc.Rates is null || doc.Expenses is null || doc.Releases is null
                || doc.Policy is null || doc.ReleaseAllocations is null || doc.SourceIdentities is null)
                return (null, ReasonUnreadable);
            var rates = doc.Rates.ToDictionary(
                r => (r.EmployeeId, r.Year, r.Month),
                r => new LabourRate(r.PerHour, r.Kind, r.Reason, r.EmployerCost, r.PaidHours, r.WithoutEmployerSocial,
                    r.OvertimePremium, r.PaidOvertimeHours, r.MarkedOvertimeHours));
            var input = new CostingInput(
                doc.PlannedBaseQty, doc.AcceptedBaseQty, doc.RejectedBaseQty, doc.Completed, doc.StandardHours, doc.StandardRate,
                doc.Materials, doc.Bookings, rates, doc.Expenses, doc.Releases,
                new CostingPolicy(doc.Policy.OverheadPerLabourHour, doc.Policy.Approved, doc.Policy.ReleaseRule, doc.Policy.EmployerBurdenPercent,
                doc.Policy.ExpenseVatPercent, doc.Policy.ScrapRecoveryPercent, doc.Policy.LabourAbsorption),
                doc.RecoveryValue);
            var ids = doc.SourceIdentities;
            return (new CostingCaptureSource(
                input, doc.ReleaseAllocations, doc.Notes ?? [], doc.ElementStatuses, doc.Rates,
                ids.Expenses ?? [], ids.Vouchers ?? [], ids.LabourBookings ?? [],
                (ids.MaterialMovements ?? []).Select(m => m.Id).ToList(), ids.OutputReleases ?? []), null);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return (null, ReasonUnreadable);
        }
    }

    // The shape CaptureAsync serializes: PascalCase names, enums as numbers.
    private sealed class Document
    {
        public decimal PlannedBaseQty { get; set; }
        public decimal AcceptedBaseQty { get; set; }
        public decimal RejectedBaseQty { get; set; }
        public bool Completed { get; set; }
        public decimal StandardHours { get; set; }
        public decimal? StandardRate { get; set; }
        public List<CostingMaterial>? Materials { get; set; }
        public List<CostingBooking>? Bookings { get; set; }
        public List<CapturedRate>? Rates { get; set; }
        public List<CostingExpense>? Expenses { get; set; }
        public List<CostingRelease>? Releases { get; set; }
        public PolicyDocument? Policy { get; set; }
        public decimal RecoveryValue { get; set; }
        public CostElementStatuses? ElementStatuses { get; set; }
        public List<CostNote>? Notes { get; set; }
        public List<ReleaseCost>? ReleaseAllocations { get; set; }
        public IdentitiesDocument? SourceIdentities { get; set; }
    }

    private sealed class PolicyDocument
    {
        public decimal OverheadPerLabourHour { get; set; }
        public bool Approved { get; set; }
        /// <summary>Absent from a sheet frozen under the default (and from every sheet before 2026-11-08): the default.</summary>
        public ReleaseValuationRule ReleaseRule { get; set; }
        public decimal EmployerBurdenPercent { get; set; }
        /// <summary>C-07 and C-09 (2026-11-09), absent under the default.</summary>
        public decimal ExpenseVatPercent { get; set; }
        public decimal ScrapRecoveryPercent { get; set; }
        /// <summary>C-05 (2026-11-09), absent under the default (the blended rate).</summary>
        public LabourAbsorption LabourAbsorption { get; set; }
    }

    private sealed class IdentitiesDocument
    {
        public List<IdOnly>? MaterialMovements { get; set; }
        public List<CapturedBooking>? LabourBookings { get; set; }
        public List<CapturedExpense>? Expenses { get; set; }
        public List<CapturedVoucher>? Vouchers { get; set; }
        public List<CapturedOutputRelease>? OutputReleases { get; set; }
    }

    private sealed record IdOnly(Guid Id);
}
