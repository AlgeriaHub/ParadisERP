using System.ComponentModel.DataAnnotations;

namespace ParadiERP.Domain;

/// <summary>How a historical sheet was obtained. A reconstruction is never a contemporaneous freeze.</summary>
public enum ManufacturingCostSnapshotKind { Created = 0, Reconstructed = 1 }
/// <summary>B1 preserves the state observed; accountant-approved financial finalization is out of scope.</summary>
public enum ManufacturingCostSnapshotStatus { Provisional = 0 }
/// <summary>Whether the source payload was captured at the freeze, rather than inferred during an upgrade.</summary>
public enum ManufacturingCostSourceState { Complete = 0, IncompleteLegacy = 1 }

/// <summary>
/// B1 cost history is append-only. The persistence boundary rejects modifications and deletes once
/// a row has been written; later information is a separate <see cref="ManufacturingLateCost"/>.
/// </summary>
public interface IManufacturingCostHistory { }

public sealed class ManufacturingCostSheet : IManufacturingCostHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public Guid LineId { get; set; }
    public ManufacturingCostSnapshotKind Kind { get; set; }
    public ManufacturingCostSnapshotStatus Status { get; set; }
    [MaxLength(32)] public string CalculationVersion { get; set; } = "B1.0";
    [MaxLength(64)] public string RequestId { get; set; } = string.Empty;
    [MaxLength(64)] public string SourceHash { get; set; } = string.Empty;
    public ManufacturingCostSourceState SourceState { get; set; } = ManufacturingCostSourceState.Complete;
    /// <summary>Canonical B1 input capture: source quantities, rates, references, expenses and releases.</summary>
    public string SourceSnapshotJson { get; set; } = string.Empty;
    public decimal AcceptedBaseQty { get; set; }
    public decimal RejectedBaseQty { get; set; }
    public decimal TotalCost { get; set; }
    public decimal? UnitCost { get; set; }
    [MaxLength(1000)] public string? ProvisionalReasons { get; set; }
    public Guid? CreatedByUserId { get; set; }
    [MaxLength(200)] public string? CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<ManufacturingCostElementLine> Elements { get; set; } = new();
}

public sealed class ManufacturingCostElementLine : IManufacturingCostHistory
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid SheetId { get; set; }
    [MaxLength(32)] public string Element { get; set; } = string.Empty;
    public decimal Quantity { get; set; } public decimal UnitCost { get; set; } public decimal TotalCost { get; set; }
    [MaxLength(32)] public string Status { get; set; } = "provisional";
    [MaxLength(400)] public string? SourceReference { get; set; }
}
public sealed class ManufacturingMaterialCostSnapshot : IManufacturingCostHistory
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid SheetId { get; set; } public Guid OrderMaterialId { get; set; }
    public Guid ProductId { get; set; } public Guid UnitId { get; set; } public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; } public decimal TotalCost { get; set; }
    [MaxLength(32)] public string CostBasis { get; set; } = "reference_cost";
    [MaxLength(32)] public string Status { get; set; } = "provisional";
}
public sealed class ManufacturingLabourRateSnapshot : IManufacturingCostHistory
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid SheetId { get; set; } public Guid EmployeeId { get; set; }
    public int PayrollYear { get; set; } public int PayrollMonth { get; set; } public decimal Hours { get; set; }
    public decimal? Rate { get; set; } public decimal? TotalCost { get; set; }
    [MaxLength(32)] public string Status { get; set; } = "provisional"; [MaxLength(200)] public string? Reason { get; set; }
}
public sealed class ManufacturingExpenseAllocationSnapshot : IManufacturingCostHistory
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid SheetId { get; set; } public Guid ExpenseId { get; set; }
    public Guid? PaymentVoucherId { get; set; } public decimal Amount { get; set; }
    [MaxLength(32)] public string Status { get; set; } = "provisional";
}
public sealed class ManufacturingScrapRecoverySnapshot : IManufacturingCostHistory
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid SheetId { get; set; } public decimal RejectedQty { get; set; }
    public decimal RecoveryValue { get; set; } [MaxLength(32)] public string Status { get; set; } = "provisional";
}
public sealed class ManufacturingReleaseValueSnapshot : IManufacturingCostHistory
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid SheetId { get; set; } public Guid OutputReleaseId { get; set; }
    public decimal Quantity { get; set; } public decimal Value { get; set; } public decimal UnitValue { get; set; }
    [MaxLength(32)] public string Status { get; set; } = "provisional";
}
public sealed class ManufacturingLateCost : IManufacturingCostHistory
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid SheetId { get; set; }
    [MaxLength(32)] public string Kind { get; set; } = string.Empty; [MaxLength(128)] public string SourceReference { get; set; } = string.Empty;
    public decimal Amount { get; set; } [MaxLength(400)] public string Reason { get; set; } = string.Empty;
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
}
