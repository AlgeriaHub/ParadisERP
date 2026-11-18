using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Catalog;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Infrastructure.Manufacturing;

/// <summary>
/// Whether this server lets manufacturing operate: create orders, move their materials, keep daily records
/// and release output (owner, 2026-10-26, decision 1: "do not activate inventory-affecting manufacturing
/// workflows in production until the accountant has approved the accounting treatment").
///
/// <para>
/// Since 2026-11-09 the accountant switches it in Manufacturing › Costing settings (C-12; owner: "make everything as
/// settings and accountant set it in cpanel"), under <c>accounting.manage</c>; it was a configuration-only decision
/// before. Read per request. Off by default, so a company that has not switched it on refuses every operational
/// write with <c>operations_not_enabled</c>; reads and the reports always work. The server's configuration
/// (<c>Manufacturing:StockOperations</c>, <see cref="ByConfiguration"/>) still switches it on for disposable test and
/// development databases, and can never switch off what the panel switched on.
/// </para>
/// </summary>
public sealed record ManufacturingOptions(bool StockOperations, bool ByConfiguration = false)
{
    public const string Section = "Manufacturing:StockOperations";
}

/// <summary>
/// Whether this server computes manufacturing costs (costing plan, Phase A): read from configuration
/// (<c>Manufacturing:Costing</c>), off unless set. Off, every costing read answers
/// <c>costing_not_enabled</c>. Nothing it switches on writes anything.
/// </summary>
public sealed record ManufacturingCostingOptions(bool Enabled)
{
    public const string Section = "Manufacturing:Costing";
}

/// <summary>Who did it, from the token: kept on every document and event, with the name as it stood.</summary>
public readonly record struct MfgActor(Guid? UserId, string? UserName);

/// <summary>The rules Phase 2's services share, written once.</summary>
internal static class Production
{
    public const decimal MaxQuantity = 1_000_000_000m;
    /// <summary>Six places in the base unit, like the requirements: a gram of a tonne still shows.</summary>
    public const int Places = 6;

    public static decimal Round(decimal value) => Math.Round(value, Places, MidpointRounding.AwayFromZero);

    /// <summary>Orders whose materials are still claimed at their line.</summary>
    public static readonly ProductionOrderStatus[] Live =
        [ProductionOrderStatus.Open, ProductionOrderStatus.OnHold, ProductionOrderStatus.CompletionRequested];

    /// <summary>Still at the line for this order: issued less returned, consumed and scrapped.</summary>
    public static decimal AtLine(ProductionOrderMaterial m) => m.IssuedBaseQty - m.ReturnedBaseQty - m.ConsumedBaseQty - m.ScrappedBaseQty;

    /// <summary>
    /// What this material still claims of the line's stock (Revision 2 §7): required, less what a manager
    /// released, less what has already left the stock for it (issued net of returns); never below zero, and
    /// nothing once the order is no longer live.
    /// </summary>
    public static decimal Reserved(ProductionOrderMaterial m, ProductionOrderStatus status) =>
        Live.Contains(status)
            ? Math.Max(0m, m.RequiredBaseQty - m.ReservationReleasedBaseQty - (m.IssuedBaseQty - m.ReturnedBaseQty))
            : 0m;

    /// <summary>
    /// Each product's open reservation at a location, over every live order there except <paramref name="except"/>.
    /// Summed in memory: SQLite cannot sum decimals.
    /// </summary>
    public static async Task<Dictionary<Guid, decimal>> ReservedAtAsync(
        PosDbContext db, Guid locationId, IReadOnlyCollection<Guid>? productIds, Guid? except, CancellationToken ct)
    {
        var query = db.ProductionOrderMaterials.AsNoTracking()
            .Where(m => m.Order!.LocationId == locationId && Live.Contains(m.Order.Status) && m.OrderId != except);
        if (productIds is not null) query = query.Where(m => productIds.Contains(m.ProductId));
        var rows = await query.Select(m => new { m.ProductId, Material = m, m.Order!.Status }).ToListAsync(ct);
        return rows.GroupBy(r => r.ProductId).ToDictionary(g => g.Key, g => g.Sum(r => Reserved(r.Material, r.Status)));
    }

    /// <summary>The next number of a kind (MO-000001), drawn inside the write scope under the version lock.</summary>
    public static string Number(string prefix, int count) => $"{prefix}-{count + 1:D6}";

    /// <summary>The company's day today, in its own time zone — never the host's.</summary>
    public static DateOnly Today(IRegionalSettingsProvider regional, TimeProvider time) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), StoreDocumentHistory.Zone(regional)).DateTime);

    /// <summary>The factor from a unit to the product's base unit, or null when there is none.</summary>
    public static async Task<(decimal Factor, UnitOfMeasure Unit)?> ToBaseAsync(
        PosDbContext db, ManufacturingSettings settings, Guid productId, Guid unitId, CancellationToken ct)
    {
        var unit = await db.UnitsOfMeasure.AsNoTracking().FirstOrDefaultAsync(u => u.Id == unitId, ct);
        if (unit is null) return null;
        var context = await settings.ProductContextAsync(productId, ct);
        if (context?.Base is not UnitConversions.UnitInfo baseUnit) return null;
        return UnitConversions.FactorToBase(UnitConversions.Info(unit), baseUnit, context.Value.Units) is decimal f ? (f, unit) : null;
    }

    public static MfgResult<T> NotEnabled<T>() =>
        MfgResult<T>.Fail(409, "operations_not_enabled",
            "Manufacturing operations are switched off on this server until the accounting treatment is approved.");

    public static string? Clean(string? text, int max) =>
        MfgText.Clean(text) is string t ? (t.Length <= max ? t : t[..max]) : null;
}
