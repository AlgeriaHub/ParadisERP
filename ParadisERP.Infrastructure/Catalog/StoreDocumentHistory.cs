using System.Text.Json;
using ParadisERP.Application.Catalog;
using ParadisERP.Application.Contracts;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Catalog;

/// <summary>
/// The database half of the printed-document wording rule: how many changes a kind has had this
/// year, and the predecessor kept for every one of them.
///
/// <para>
/// The decision itself is not here — it is <see cref="StoreDocumentTexts.Decide"/>, a pure
/// function over values, which is why the interesting cases are tested without a database. This
/// class only counts, serialises and reads back.
/// </para>
/// <para>
/// Static rather than an injected service: it holds no state, and registering it would mean
/// touching the composition root for two queries the one endpoint that needs them can pass its
/// own <see cref="PosDbContext"/> to.
/// </para>
/// </summary>
public static class StoreDocumentHistory
{
    /// <summary>
    /// The company's own time zone, which is the one the allowance's year is counted in. A profile
    /// naming a zone this machine does not know falls back to UTC rather than throwing — exactly
    /// as the rental clock does (<c>RentalDocuments.Zone</c>).
    /// </summary>
    public static TimeZoneInfo Zone(IRegionalSettingsProvider regional)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(regional.Get().TimeZone); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    /// <summary>
    /// How many changes each kind has had in <paramref name="year"/> — one indexed equality per
    /// kind over <c>(Kind, ChangeYear)</c>, never a range over UTC timestamps that would file a
    /// 31 December evening save in the wrong year for Africa/Algiers.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, int>> UsedAsync(
        PosDbContext db, int year, CancellationToken ct = default) =>
        await db.DocumentTextChanges.AsNoTracking()
            .Where(c => c.ChangeYear == year)
            .GroupBy(c => c.Kind)
            .Select(g => new { Kind = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Kind, x => x.Count, ct);

    /// <summary>
    /// Adds one history row per kind this save actually rewrote, carrying the wording as it stood
    /// <b>before</b>. Refused kinds never reach here — the endpoint answers the first refusal and
    /// writes nothing at all, so a save is all-or-nothing across kinds.
    /// </summary>
    public static void Record(
        PosDbContext db, IEnumerable<StoreDocumentTexts.ChangeDecision> decisions, int year, string? changedBy)
    {
        foreach (var decision in decisions.Where(d => d.Verdict == StoreDocumentTexts.ChangeVerdict.Allowed))
        {
            db.DocumentTextChanges.Add(new DocumentTextChange
            {
                Kind = decision.Kind,
                ChangeYear = year,
                ChangedAtUtc = DateTime.UtcNow,
                ChangedBy = changedBy,
                // "{}" when the kind had no wording before: the first save has a predecessor too,
                // and it is "nothing". Reading it back as an empty map is what lets the screen say
                // "there was no text here" instead of showing a gap.
                PreviousTexts = JsonSerializer.Serialize(decision.PreviousTexts, (JsonSerializerOptions?)null)
            });
        }
    }

    /// <summary>
    /// The superseded wordings, newest first, for one kind or for all of them. Paged by a plain
    /// take: the whole point of the allowance is that this list stays short.
    /// </summary>
    public static async Task<IReadOnlyList<DocumentTextChangeDto>> ReadAsync(
        PosDbContext db, string? kind, int take, CancellationToken ct = default)
    {
        var query = db.DocumentTextChanges.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(kind)) query = query.Where(c => c.Kind == kind);

        var rows = await query.OrderByDescending(c => c.ChangedAtUtc).Take(take).ToListAsync(ct);
        // MySQL returns a DATETIME as Unspecified, which serialises without a 'Z' — and a browser
        // reads an unmarked stamp as its own local time. This is the date a customer's signed
        // wording is looked up by, so an hours-out stamp is not cosmetic: it is written as UTC and
        // it goes out saying so.
        return rows.Select(c => new DocumentTextChangeDto(
            c.Id, c.Kind, c.ChangeYear, Utc(c.ChangedAtUtc), Texts(c.PreviousTexts), c.ChangedBy)).ToList();
    }

    private static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>
    /// The stored predecessor as a map. A row this code cannot parse reads as empty rather than
    /// throwing: a history that fails to open is worse than one entry that shows nothing, and the
    /// rest of the chain is still readable.
    /// </summary>
    private static IReadOnlyDictionary<string, string> Texts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json, (JsonSerializerOptions?)null) ?? new(); }
        catch (JsonException) { return new Dictionary<string, string>(); }
    }
}
