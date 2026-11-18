namespace AlgeriaPos.Application.Regional;

/// <summary>
/// UI translations for a language the app does not ship with. Keys are the web app's
/// translation keys; anything missing falls back to <see cref="FallbackLanguage"/>.
/// </summary>
public sealed class LanguagePack
{
    public const int MaxStrings = 2000;

    public string Code { get; set; } = "";
    /// <summary>Name shown in language switchers, in the language itself.</summary>
    public string Label { get; set; } = "";
    public bool Rtl { get; set; }
    public string FallbackLanguage { get; set; } = "en";
    public Dictionary<string, string> Strings { get; set; } = new();

    public static void NormalizeAndValidate(LanguagePack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        pack.Code = (pack.Code ?? "").Trim().ToLowerInvariant();
        pack.Label = (pack.Label ?? "").Trim();
        pack.FallbackLanguage = (pack.FallbackLanguage ?? "").Trim().ToLowerInvariant();
        pack.Strings = (pack.Strings ?? []).Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .ToDictionary(x => x.Key.Trim(), x => x.Value);

        if (!RegionalSettingsValidator.IsLanguageCode(pack.Code))
            throw new ArgumentException("Use a language code such as tzm, es or pt-br.");
        if (RegionalSettings.BuiltinLanguages.Contains(pack.Code))
            throw new ArgumentException($"'{pack.Code}' is built in and cannot be replaced by a pack.");
        if (pack.Label.Length is 0 or > 40) throw new ArgumentException("A language name of up to 40 characters is required.");
        if (!RegionalSettings.BuiltinLanguages.Contains(pack.FallbackLanguage))
            throw new ArgumentException($"Fallback language must be one of: {string.Join(", ", RegionalSettings.BuiltinLanguages)}.");
        if (pack.Strings.Count is 0 or > MaxStrings) throw new ArgumentException($"A pack needs 1-{MaxStrings} translated strings.");
        if (pack.Strings.Any(x => x.Key.Length is 0 or > 100 || x.Value.Length > 1000))
            throw new ArgumentException("Keys are limited to 100 characters and translations to 1000.");
    }
}

/// <summary>Where uploaded language packs live: the platform database, or configuration for a single shop.</summary>
public interface ILanguagePackStore
{
    Task<IReadOnlyDictionary<string, long>> RevisionsAsync(CancellationToken ct);
    Task<LanguagePack?> GetAsync(string code, CancellationToken ct);
}

public static class LanguagePackStoreExtensions
{
    /// <summary>Revisions of the packs a company's profile enables, for tills to compare with their cache.</summary>
    public static async Task<IReadOnlyDictionary<string, long>> RevisionsForAsync(
        this ILanguagePackStore store, RegionalSettings settings, CancellationToken ct)
    {
        var all = await store.RevisionsAsync(ct);
        return settings.Languages.Where(all.ContainsKey).ToDictionary(code => code, code => all[code]);
    }
}
