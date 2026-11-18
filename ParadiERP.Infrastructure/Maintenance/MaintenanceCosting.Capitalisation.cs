using ParadiERP.Application.Maintenance;
using ParadiERP.Domain;

namespace ParadiERP.Infrastructure.Maintenance;

/// <summary>
/// A job's cost as the capitalisation reads it at completion (Phase M7d, MS-30): M4's total C (the threshold's figure), its
/// labour and overhead, and the same two figures for the hours booked before a day (<c>bookedBefore</c>: the first day of the
/// first month the maintenance practice ledger posts, MS-39), so the capitalised labour can be taken back from where the books
/// carry it. Ledger precision (three decimals), as the practice ledger.
/// </summary>
public sealed record MaintenanceCapitalisationBasis(
    decimal Total, decimal Labour, decimal Overhead, decimal LabourBefore, decimal OverheadBefore, decimal ExpenseVatPercent);

public sealed partial class MaintenanceCosting
{
    /// <summary>
    /// The job's cost C and its labour and overhead, computed exactly as its Cost card (M4's adapter over the manufacturing
    /// engine), with the labour and overhead of the hours booked before <paramref name="bookedBefore"/> beside them.
    /// </summary>
    internal async Task<MaintenanceCapitalisationBasis> CapitalisationBasisAsync(MaintenanceJob job, DateOnly bookedBefore, CancellationToken ct)
    {
        var (_, policy) = await SettingsAsync(ct);
        var costed = (await CostAsync([job], policy, ct))[job.Id];
        var before = MaintenanceCostAdapter.Compute(costed.Input with
        {
            Bookings = costed.Input.Bookings.Where(b => b.Day < bookedBefore).ToList(),
        });
        return new MaintenanceCapitalisationBasis(
            Ledger(costed.Sheet.Total), Ledger(costed.Sheet.Labour), Ledger(costed.Sheet.Overhead),
            Ledger(before.Labour), Ledger(before.Overhead), policy.ExpenseVatPercent);
    }

    private static decimal Ledger(decimal value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);
}
