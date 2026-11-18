using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Manufacturing;

/// <summary>
/// The values of the specification fields (hardening review, 2026-10-26), checked and written the same way
/// for a product and for a recipe line. Optional throughout: no field is required and a product with no
/// physical dimensions carries none. The free-text specification beside them is left exactly as it was.
///
/// <para>
/// What is refused is what a later calculation could not read honestly: a measured field without a figure
/// above zero, a figure in a unit that measures something else (a thickness in kilograms), a word written
/// into a measured field or a figure into a text one, and a field named twice. A switched-off field or
/// unit is not refused: it only leaves the pickers, and the values already written with it stay valid —
/// as a switched-off unit stays valid on a recipe line.
/// </para>
/// </summary>
internal static class SpecValues
{
    public const int MaxPerOwner = 20;
    private const int MaxText = 200;
    private const decimal MaxValue = 1_000_000_000m;

    /// <summary>The first problem with these values, or null. <paramref name="owner"/> names them in the message.</summary>
    public static async Task<MfgResult<bool>?> ValidateAsync(PosDbContext db, IReadOnlyList<SpecValueRequest>? specs, string owner, CancellationToken ct)
    {
        if (specs is null || specs.Count == 0) return null;
        if (specs.Count > MaxPerOwner)
            return MfgFail.Invalid<bool>("too_many_specs", $"{owner}: up to {MaxPerOwner} specification fields.");
        if (specs.GroupBy(s => s.AttributeId).Any(g => g.Count() > 1))
            return MfgFail.Invalid<bool>("duplicate_spec", $"{owner}: a specification field is given twice.");

        var attributeIds = specs.Select(s => s.AttributeId).ToList();
        var attributes = await db.SpecAttributes.AsNoTracking().Where(a => attributeIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var unitIds = specs.Where(s => s.UnitId is not null).Select(s => s.UnitId!.Value).Distinct().ToList();
        var units = await db.UnitsOfMeasure.AsNoTracking().Where(u => unitIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);

        foreach (var s in specs)
        {
            if (!attributes.TryGetValue(s.AttributeId, out var field))
                return MfgFail.Invalid<bool>("unknown_spec_field", $"{owner}: a specification field does not exist.");
            if (field.Kind == SpecKind.Text)
            {
                if (s.Value is not null || s.UnitId is not null)
                    return MfgFail.Invalid<bool>("spec_kind_mismatch", $"{owner}: {field.Code} is written as a word, without a figure or unit.");
                if (MfgText.Required(s.Text, MaxText, $"{owner}: {field.Code}") is string problem)
                    return MfgFail.Invalid<bool>("spec_value_required", problem);
                continue;
            }
            if (!string.IsNullOrWhiteSpace(s.Text))
                return MfgFail.Invalid<bool>("spec_kind_mismatch", $"{owner}: {field.Code} is a figure with a unit, not a word.");
            if (s.Value is not decimal value || value <= 0 || value > MaxValue)
                return MfgFail.Invalid<bool>("spec_value_required", $"{owner}: {field.Code} needs a figure above zero.");
            if (s.UnitId is not Guid unitId || !units.TryGetValue(unitId, out var unit))
                return MfgFail.Invalid<bool>("spec_unit_required", $"{owner}: {field.Code} needs its unit.");
            if (unit.Dimension != field.Dimension)
                return MfgFail.Invalid<bool>("spec_unit_mismatch",
                    $"{owner}: {field.Code} measures {field.Dimension.ToString().ToLowerInvariant()}; {unit.Code} does not.");
        }
        return null;
    }

    /// <summary>The validated values as rows, in the order given. Call <see cref="ValidateAsync"/> first.</summary>
    public static List<T> Rows<T>(IReadOnlyList<SpecValueRequest>? specs) where T : SpecValue, new() =>
        (specs ?? []).Select(s => new T
        {
            AttributeId = s.AttributeId,
            Value = string.IsNullOrWhiteSpace(s.Text) ? s.Value : null,
            UnitId = string.IsNullOrWhiteSpace(s.Text) ? s.UnitId : null,
            Text = MfgText.Clean(s.Text),
        }).ToList();

    /// <summary>The rows as requests again: how an approval re-checks a draft's lines as they stand.</summary>
    public static List<SpecValueRequest> Requests(IEnumerable<SpecValue> rows) =>
        rows.Select(r => new SpecValueRequest(r.AttributeId, r.Value, r.UnitId, r.Text)).ToList();

    /// <summary>The field and unit codes every value is shown with, read once per answer.</summary>
    public sealed record Names(IReadOnlyDictionary<Guid, SpecAttribute> Fields, IReadOnlyDictionary<Guid, string> Units);

    public static async Task<Names> NamesAsync(PosDbContext db, CancellationToken ct) => new(
        await db.SpecAttributes.AsNoTracking().ToDictionaryAsync(a => a.Id, ct),
        await db.UnitsOfMeasure.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Code, ct));

    /// <summary>The values as shown, in the fields' own order.</summary>
    public static List<SpecValueDto> Dtos(IEnumerable<SpecValue> rows, Names names) =>
        rows.Select(r => (Row: r, Field: names.Fields.GetValueOrDefault(r.AttributeId)))
            .OrderBy(x => x.Field?.SortOrder ?? int.MaxValue).ThenBy(x => x.Field?.Code, StringComparer.Ordinal)
            .Select(x => new SpecValueDto(
                x.Row.AttributeId, x.Field?.Code ?? "?", (x.Field?.Kind ?? SpecKind.Text).ToString(),
                x.Row.Value, x.Row.UnitId, x.Row.UnitId is Guid u ? names.Units.GetValueOrDefault(u) : null, x.Row.Text))
            .ToList();
}
