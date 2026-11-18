namespace AlgeriaPos.Application.Contracts;

// Phase M7d (MS-38, MNT-10): the repair history of an equipment type, or of one piece of it (docs/maintenance/plan.md).

/// <summary>One piece of the type named on its jobs: its reference, how many jobs repaired it and when the last one was opened.</summary>
public record MaintenancePieceDto(string Reference, int Jobs, DateTime LastAtUtc);

/// <summary>A part one job used: issued less returned, at the values frozen on its movements.</summary>
public record MaintenanceHistoryPartDto(Guid ProductId, string Sku, string NameAr, string NameFr, string NameEn, decimal Qty, decimal Value);

/// <summary>
/// One job on the type (or the piece): its dates, kind, units, parts, outside work and hours, and — to a
/// <c>maintenance.costs.view</c> holder, under M4's privacy rule — its cost and what of it was capitalised (MS-30). A figure
/// withheld is null with <c>CostWithheld</c> set, never a zero.
/// </summary>
public record MaintenanceHistoryJobDto(
    Guid Id, string Number, string Kind, string Status, Guid BranchId, string BranchName, string? ItemReference,
    decimal Qty, decimal RepairedQty, decimal ScrappedQty, DateTime OpenedAtUtc, DateTime? CompletedAtUtc,
    decimal LabourHours, decimal Parts, IReadOnlyList<MaintenanceHistoryPartDto> PartLines, decimal OutsideWork,
    decimal? Cost, bool CostWithheld, decimal? Capitalised, string CostStatus);

/// <summary>
/// Maintenance › Repair history: every job (but drafts and customer jobs) on one equipment type at the reader's branches, or on
/// one piece of it when a reference is asked. <c>Mode</c> is MS-38; <c>TrackedPerPiece</c> says whether, under "per piece",
/// this type's unit value (<c>UnitValue</c>: its asset value, or its cost price) is above the setting's value. Totals of cost
/// and capitalised follow M4's sum rule (two jobs with labour and two workers) for a reader without pay-rate access.
/// </summary>
public record MaintenanceRepairHistoryDto(
    Guid ProductId, string Sku, string NameAr, string NameFr, string NameEn,
    string Mode, bool TrackedPerPiece, decimal UnitValue, decimal PerPieceAboveValue, string? Reference,
    IReadOnlyList<MaintenancePieceDto> Pieces, IReadOnlyList<MaintenanceHistoryJobDto> Jobs,
    decimal LabourHours, decimal Parts, decimal OutsideWork, decimal? Cost, decimal? Capitalised,
    bool CostsShown, bool TotalsWithheld);
