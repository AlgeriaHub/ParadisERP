namespace ParadisERP.Application.Payroll;

/// <summary>
/// Where the days on a payslip came from. Mirrors <c>ParadisERP.Domain.AttendanceSource</c>;
/// the persistence suite pins the two together.
/// </summary>
public enum AttendanceSource
{
    /// <summary>A foreman's period totals, entered by hand.</summary>
    Declared = 0,
    /// <summary>Clock pairs or per-day ticks, added up.</summary>
    Recorded = 1,
    /// <summary>Nothing was captured for this person this month. Not the same as zero days.</summary>
    None = 2
}

/// <summary>
/// One person's month, reduced to the handful of figures the engine actually reads.
///
/// <para>
/// Both capture modes end here (docs/07 §5.5). If the engine read a declaration and a set of
/// day rows directly, every rule would be written twice and the daily basis — which cannot run
/// without days — would take its number from whichever path happened to be filled.
/// </para>
/// <para>
/// <see cref="Source"/> is carried rather than inferred from the numbers, because
/// <c>None</c> and a genuine month of zero days are different facts that need different
/// warnings.
/// </para>
/// </summary>
public sealed record AttendanceSummary(
    decimal DaysPresent,
    decimal PaidLeaveDays,
    decimal UnpaidAbsenceDays,
    IReadOnlyDictionary<string, decimal> OvertimeHoursByRule,
    AttendanceSource Source)
{
    /// <summary>Nothing was captured. A monthly employee is still paid; a daily one is flagged.</summary>
    public static AttendanceSummary Nothing { get; } =
        new(0m, 0m, 0m, new Dictionary<string, decimal>(), AttendanceSource.None);

    /// <summary>Days that are paid at the base rate: worked days plus paid leave.</summary>
    public decimal PaidDays => Math.Max(0m, DaysPresent) + Math.Max(0m, PaidLeaveDays);
}
