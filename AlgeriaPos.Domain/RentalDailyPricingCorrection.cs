using System.ComponentModel.DataAnnotations;

namespace AlgeriaPos.Domain;

/// <summary>
/// Migration-captured historical contract, corrected once. New contracts are not queued.
/// Before/after snapshots preserve the financial documents as originally issued.
/// </summary>
public sealed class RentalDailyPricingCorrection
{
    [Key] public Guid ContractId { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
}
