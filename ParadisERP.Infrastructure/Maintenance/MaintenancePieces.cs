using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Maintenance;

/// <summary>
/// MS-38 "per piece above a value" (Phase M7d, MNT-10). Hire equipment is counted in quantities, so a piece is named by the
/// reference its job carries (<see cref="MaintenanceJob.ItemReference"/>); the type's unit value decides whether its pieces are
/// tracked one by one: the product's asset value (<see cref="Product.AssetUnitValue"/>, the value REN-12 may open the register at)
/// when it has one, its cost price otherwise.
/// </summary>
public static class MaintenancePieces
{
    /// <summary>A type's unit value as MS-38 reads it.</summary>
    public static decimal UnitValue(decimal assetUnitValue, decimal costPrice) => assetUnitValue > 0m ? assetUnitValue : costPrice;

    /// <summary>Among <paramref name="productIds"/>, the types tracked piece by piece: none unless MS-38 says "per piece" with a value.</summary>
    public static async Task<HashSet<Guid>> TrackedAsync(
        PosDbContext db, MaintenanceSettings settings, IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
    {
        if (settings.RepairHistory != MaintenanceHistory.PerPiece || settings.PerPieceAboveValue <= 0m || productIds.Count == 0) return [];
        var values = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id))
            .Select(p => new { p.Id, p.AssetUnitValue, p.CostPrice }).ToListAsync(ct);
        return values.Where(p => UnitValue(p.AssetUnitValue, p.CostPrice) > settings.PerPieceAboveValue).Select(p => p.Id).ToHashSet();
    }

    /// <summary>A reference as the history compares it: trimmed, case ignored.</summary>
    public static string Key(string reference) => reference.Trim().ToUpperInvariant();
}
