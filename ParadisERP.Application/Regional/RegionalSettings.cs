using System.Text.RegularExpressions;

namespace ParadisERP.Application.Regional;

/// <summary>A company-level identifier printed on receipts (NIF, ICE, SIRET, VAT number...).</summary>
public sealed class FiscalIdDefinition
{
    /// <summary>Stable storage key inside Store.FiscalIds. Renaming it orphans stored values.</summary>
    public string Key { get; set; } = "";
    /// <summary>Short code printed on the receipt, e.g. "NIF".</summary>
    public string Code { get; set; } = "";
    /// <summary>Form label per UI language (ar, fr, en).</summary>
    public Dictionary<string, string> Labels { get; set; } = new();
    public bool Required { get; set; }
    public bool ShowOnReceipt { get; set; } = true;
}

/// <summary>Display label of one tax rate, e.g. 9% -> "Taux réduit".</summary>
/// <summary>
/// How the currency is spoken in one language — the words an amount in words is built from
/// ("dinar" / "centime", "درهم" / "سنتيم"). Country data, so it lives in the profile and not in
/// the converter: the same French code writes dinars in Algiers and euros in Paris.
/// </summary>
public sealed class CurrencyUnitNames
{
    public string Major { get; set; } = "";
    public string Minor { get; set; } = "";
}

public sealed class TaxRateLabel
{
    public decimal Rate { get; set; }
    public Dictionary<string, string> Labels { get; set; } = new();
}

public static class CashRoundingModes
{
    public const string Nearest = "Nearest";
    public const string Up = "Up";
    public const string Down = "Down";
    public static readonly string[] All = [Nearest, Up, Down];
}

/// <summary>Blocks a receipt is built from; the profile chooses which print and in what order.</summary>
public static class ReceiptSections
{
    public const string Header = "header";
    public const string FiscalIds = "fiscalIds";
    public const string Meta = "meta";
    public const string Lines = "lines";
    public const string Totals = "totals";
    public const string TaxBreakdown = "taxBreakdown";
    public const string Payment = "payment";
    public const string Legal = "legal";
    public const string Footer = "footer";
    public static readonly string[] Default = [Header, FiscalIds, Meta, Lines, Totals, TaxBreakdown, Payment, Legal, Footer];
    public static readonly string[] Required = [Lines, Totals];
}

/// <summary>
/// Everything that differs between countries. Owned by the platform operator per company and
/// delivered to tills with login and every pull, so it also applies offline.
/// </summary>
public sealed class RegionalSettings
{
    public const int MaxFiscalIdLength = 40;

    public const int MaxLegalTextLength = 500;

    /// <summary>Languages whose translations ship with the app; others need an operator-uploaded pack.</summary>
    public static readonly string[] BuiltinLanguages = ["ar", "fr", "en"];
    /// <summary>
    /// The tenders whose behaviour is programmed: Credit moves the customer balance, GiftCard
    /// a card balance, Loyalty points. They can be enabled, disabled and renamed but never
    /// invented or removed.
    /// <para>
    /// This is deliberately no longer a closed whitelist. A company may also enable tenders
    /// its operator defined — a postal transfer, a bank transfer, a cheque — which carry no
    /// programmed behaviour and are described by a <c>PaymentMethodDef</c> row in the company
    /// database. Validating against a fixed list here is exactly what made those impossible.
    /// </para>
    /// </summary>
    public static readonly string[] BuiltInPaymentMethods = ["Cash", "Card", "Credit", "GiftCard", "Loyalty"];

    /// <summary>Kept as the old name so existing callers and tests keep compiling.</summary>
    public static readonly string[] SupportedPaymentMethods = BuiltInPaymentMethods;

    /// <summary>Upper limit on enabled tenders — a payment screen past this is unusable.</summary>
    public const int MaxPaymentMethods = 12;

    public string CountryCode { get; set; } = "";
    public string CurrencyCode { get; set; } = "";
    public int CurrencyDecimals { get; set; } = 2;
    public Dictionary<string, string> CurrencySymbols { get; set; } = new();
    public bool CurrencySymbolFirst { get; set; }
    /// <summary>
    /// How figures are written on screen and on paper, in every language (owner, 2026-11-09: "5,300,256.25 … for all
    /// places in the system"): <c>1,234.56</c> (the default), <c>1.234,56</c>, <c>1 234,56</c>, or <c>locale</c> for each
    /// language's own marks. The web app formats; the server only keeps the choice.
    /// </summary>
    public string NumberFormat { get; set; } = NumberFormats.Default;
    /// <summary>IANA zone, e.g. "Africa/Algiers".</summary>
    public string TimeZone { get; set; } = "";
    public List<string> Languages { get; set; } = new();
    public string DefaultLanguage { get; set; } = "";
    public Dictionary<string, string> TaxNames { get; set; } = new();
    /// <summary>Percentages a product may use.</summary>
    public List<decimal> TaxRates { get; set; } = new();
    public List<TaxRateLabel> TaxRateLabels { get; set; } = new();
    public decimal DefaultTaxRate { get; set; }
    public bool PricesIncludeTax { get; set; } = true;

    /// <summary>
    /// Whether a document prints its tax line when the tax is zero.
    ///
    /// <para>
    /// Deliberately separate from whether tax applies. They are two different questions and a
    /// business can want either answer to each: an untaxed invoice that simply says nothing
    /// about tax, or one that states "tax 0.00" so the customer can see it was considered
    /// rather than forgotten. Some buyers ask for the second in writing.
    /// </para>
    /// <para>
    /// Off by default, because an empty line on a receipt is noise until someone asks for it.
    /// </para>
    /// </summary>
    public bool ShowZeroTax { get; set; }
    public List<FiscalIdDefinition> FiscalIds { get; set; } = new();
    public Dictionary<string, string> CustomerTaxIdLabels { get; set; } = new();
    public List<decimal> CashDenominations { get; set; } = new();
    /// <summary>Cash payments are rounded to a multiple of this (e.g. 0.05); 0 turns rounding off.</summary>
    public decimal CashRoundingIncrement { get; set; }
    public string CashRoundingMode { get; set; } = CashRoundingModes.Nearest;
    public List<string> PaymentMethods { get; set; } = new();
    /// <summary>Receipt blocks in print order; blocks left out are not printed.</summary>
    public List<string> ReceiptSections { get; set; } = new();
    /// <summary>Mandatory legal wording per language, printed in the "legal" block.</summary>
    public Dictionary<string, string> ReceiptLegalText { get; set; } = new();
    /// <summary>
    /// Unit names per language for the amount in words on invoices and receipt vouchers
    /// (docs/07 §4.5). A language with no entry prints figures only — never a guessed word.
    /// </summary>
    public Dictionary<string, CurrencyUnitNames> CurrencyUnitNames { get; set; } = new();
    /// <summary>Interface face for this company's tills; see <see cref="UiFonts"/>. Screen only —
    /// the receipt is printed by the thermal driver and never follows it.</summary>
    public string UiFont { get; set; } = UiFonts.Default;
    /// <summary>How a customer's phone is read and stored: a mobile in international form (owner, 2026-11-17).</summary>
    public PhoneNumberRules Phone { get; set; } = new();

    /// <summary>One unit of the currency's smallest subunit: the rounding slack allowed on totals.</summary>
    public decimal MoneyTolerance() => 1m / (decimal)Math.Pow(10, CurrencyDecimals);

    public decimal RoundMoney(decimal value) => Math.Round(value, CurrencyDecimals, MidpointRounding.AwayFromZero);
}

/// <summary>The number formats a company may choose (<see cref="RegionalSettings.NumberFormat"/>).</summary>
public static class NumberFormats
{
    public const string Default = "1,234.56";
    public static readonly string[] All = ["1,234.56", "1.234,56", "1 234,56", "locale"];
}

public interface IRegionalSettingsProvider
{
    RegionalSettings Get();
}

public static partial class RegionalSettingsValidator
{
    [GeneratedRegex("^[A-Z]{2}$")] private static partial Regex CountryPattern();
    [GeneratedRegex("^[A-Z]{3}$")] private static partial Regex CurrencyPattern();
    [GeneratedRegex(@"^[A-Za-z_]+(/[A-Za-z0-9_+\-]+){0,2}$|^UTC$")] private static partial Regex TimeZonePattern();
    [GeneratedRegex("^[a-z][a-z0-9_]{0,19}$")] private static partial Regex KeyPattern();
    [GeneratedRegex("^[a-z]{2,3}(-[a-z0-9]{2,8})?$")] private static partial Regex LanguagePattern();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 _\-]{0,39}$")] private static partial Regex PaymentMethodKeyPattern();

    public static bool IsLanguageCode(string code) => LanguagePattern().IsMatch(code);

    /// <summary>
    /// Trims and upper-cases codes in place, then throws <see cref="ArgumentException"/> on the first problem.
    /// <paramref name="languageAvailable"/> says whether a non-built-in language has a pack; when
    /// null, any well-formed language code is accepted.
    /// </summary>
    public static void NormalizeAndValidate(RegionalSettings s, Func<string, bool>? languageAvailable = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.CountryCode = (s.CountryCode ?? "").Trim().ToUpperInvariant();
        s.CurrencyCode = (s.CurrencyCode ?? "").Trim().ToUpperInvariant();
        s.TimeZone = (s.TimeZone ?? "").Trim();
        s.Languages = (s.Languages ?? []).Select(x => x.Trim().ToLowerInvariant()).Distinct().ToList();
        s.DefaultLanguage = (s.DefaultLanguage ?? "").Trim().ToLowerInvariant();
        s.UiFont = UiFonts.Normalize(s.UiFont);
        // A profile saved before phones had rules takes its country preset's.
        PhoneNumbers.FillFromPreset(s);
        s.TaxRates = (s.TaxRates ?? []).Distinct().OrderByDescending(x => x).ToList();
        s.CashDenominations = (s.CashDenominations ?? []).Distinct().Order().ToList();
        // A built-in keeps its canonical casing so code can compare against it; anything else
        // is an operator-defined tender and is kept as the operator spelled it.
        s.PaymentMethods = (s.PaymentMethods ?? []).Select(x => x.Trim()).Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(x => RegionalSettings.BuiltInPaymentMethods.FirstOrDefault(m => m.Equals(x, StringComparison.OrdinalIgnoreCase)) ?? x).ToList();
        s.CurrencySymbols = Clean(s.CurrencySymbols);
        s.TaxNames = Clean(s.TaxNames);
        s.CustomerTaxIdLabels = Clean(s.CustomerTaxIdLabels);
        s.ReceiptLegalText = Clean(s.ReceiptLegalText, RegionalSettings.MaxLegalTextLength);
        s.CurrencyUnitNames = CleanUnitNames(s.CurrencyUnitNames);
        s.TaxRateLabels = (s.TaxRateLabels ?? [])
            .Select(x => new TaxRateLabel { Rate = x.Rate, Labels = Clean(x.Labels) })
            .Where(x => x.Labels.Count > 0)
            .GroupBy(x => x.Rate).Select(g => g.Last()).ToList();
        var mode = (s.CashRoundingMode ?? "").Trim();
        s.CashRoundingMode = CashRoundingModes.All.FirstOrDefault(m => m.Equals(mode, StringComparison.OrdinalIgnoreCase)) ?? mode;
        s.ReceiptSections = (s.ReceiptSections ?? []).Select(x => (x ?? "").Trim())
            .Select(x => ReceiptSections.Default.FirstOrDefault(d => d.Equals(x, StringComparison.OrdinalIgnoreCase)) ?? x)
            .Distinct().ToList();
        if (s.ReceiptSections.Count == 0) s.ReceiptSections = [.. ReceiptSections.Default];
        s.FiscalIds ??= [];
        foreach (var id in s.FiscalIds)
        {
            id.Key = (id.Key ?? "").Trim().ToLowerInvariant();
            id.Code = (id.Code ?? "").Trim();
            id.Labels = Clean(id.Labels);
        }

        if (!CountryPattern().IsMatch(s.CountryCode)) Fail("Country code must be two letters (ISO 3166), e.g. DZ.");
        if (PhoneNumbers.NormalizeAndCheck(s.Phone) is string phoneProblem) Fail(phoneProblem);
        if (!CurrencyPattern().IsMatch(s.CurrencyCode)) Fail("Currency code must be three letters (ISO 4217), e.g. DZD.");
        if (s.CurrencyDecimals is < 0 or > 3) Fail("Currency decimals must be between 0 and 3.");
        if (s.CurrencySymbols.Count == 0) Fail("At least one currency symbol is required.");
        if (!TimeZonePattern().IsMatch(s.TimeZone)) Fail("Time zone must be an IANA name, e.g. Africa/Algiers.");
        if (s.Languages.Count is 0 or > 10 || s.Languages.Any(x => !IsLanguageCode(x)))
            Fail("Enable 1-10 languages using codes such as ar, fr, en or tzm.");
        var missing = s.Languages.Where(x => !RegionalSettings.BuiltinLanguages.Contains(x) && languageAvailable?.Invoke(x) == false).ToList();
        if (missing.Count > 0) Fail($"Upload a language pack first for: {string.Join(", ", missing)}.");
        if (!s.Languages.Contains(s.DefaultLanguage)) Fail("The default language must be one of the enabled languages.");
        if (s.TaxNames.Count == 0) Fail("A tax name is required.");
        if (s.TaxRates.Count is 0 or > 10 || s.TaxRates.Any(x => x is < 0 or > 100))
            Fail("Provide 1–10 tax rates between 0 and 100.");
        if (!s.TaxRates.Contains(s.DefaultTaxRate)) Fail("The default tax rate must be one of the tax rates.");
        if (s.TaxRateLabels.Any(x => !s.TaxRates.Contains(x.Rate))) Fail("Tax rate labels must belong to one of the tax rates.");
        if (s.CashRoundingIncrement < 0 || s.CashRoundingIncrement > 1000
            || (s.CashRoundingIncrement > 0 && s.CashRoundingIncrement < s.MoneyTolerance())
            || decimal.Round(s.CashRoundingIncrement, s.CurrencyDecimals) != s.CashRoundingIncrement)
            Fail("Cash rounding must be 0 (off) or a positive step the currency can express, e.g. 0.05.");
        if (!CashRoundingModes.All.Contains(s.CashRoundingMode))
            Fail($"Cash rounding mode must be one of: {string.Join(", ", CashRoundingModes.All)}.");
        // A profile saved before the setting existed keeps the system's own format.
        if (string.IsNullOrWhiteSpace(s.NumberFormat)) s.NumberFormat = NumberFormats.Default;
        if (!NumberFormats.All.Contains(s.NumberFormat))
            Fail($"Number format must be one of: {string.Join(", ", NumberFormats.All)}.");
        if (s.ReceiptSections.Any(x => !ReceiptSections.Default.Contains(x)))
            Fail($"Receipt sections must be chosen from: {string.Join(", ", ReceiptSections.Default)}.");
        if (ReceiptSections.Required.Any(x => !s.ReceiptSections.Contains(x)))
            Fail("A receipt must include the lines and totals sections.");
        if (s.FiscalIds.Count > 10) Fail("At most 10 fiscal identifiers are allowed.");
        foreach (var id in s.FiscalIds)
        {
            if (!KeyPattern().IsMatch(id.Key)) Fail("Fiscal identifier keys use lower-case letters, digits and _ (max 20).");
            if (id.Code.Length is 0 or > 20) Fail("Each fiscal identifier needs a receipt code of up to 20 characters.");
        }
        if (s.FiscalIds.Select(x => x.Key).Distinct().Count() != s.FiscalIds.Count) Fail("Fiscal identifier keys must be unique.");
        if (s.CashDenominations.Count > 12 || s.CashDenominations.Any(x => x <= 0))
            Fail("Provide up to 12 positive cash denominations.");
        // Operator-defined tenders are allowed, so the check is on shape rather than
        // membership of a fixed list. Cash stays mandatory: it is the only tender that needs
        // no confirmation and no device, and a till that cannot take cash cannot trade.
        if (s.PaymentMethods.Count is 0 or > RegionalSettings.MaxPaymentMethods)
            Fail($"Enable between 1 and {RegionalSettings.MaxPaymentMethods} payment methods.");
        if (!s.PaymentMethods.Contains("Cash", StringComparer.OrdinalIgnoreCase))
            Fail("Cash cannot be disabled.");
        if (s.PaymentMethods.Any(x => !PaymentMethodKeyPattern().IsMatch(x)))
            Fail("Payment method names use letters, digits, spaces, '-' and '_' (max 40).");
    }

    private static Dictionary<string, string> Clean(Dictionary<string, string>? values, int maxLength = 60)
    {
        var result = new Dictionary<string, string>();
        foreach (var (lang, text) in values ?? [])
        {
            var key = lang.Trim().ToLowerInvariant();
            var value = (text ?? "").Trim();
            if (!IsLanguageCode(key) || value.Length == 0) continue;
            if (value.Length > maxLength) Fail($"Texts here are limited to {maxLength} characters.");
            result[key] = value;
        }
        return result;
    }

    /// <summary>
    /// Like <see cref="Clean"/>, for the pair of unit names: an entry with no major name is dropped
    /// (figures only for that language), a blank minor name is kept as empty, so a currency with
    /// no subunit in daily speech still gets its major word.
    /// </summary>
    private static Dictionary<string, CurrencyUnitNames> CleanUnitNames(Dictionary<string, CurrencyUnitNames>? values)
    {
        var result = new Dictionary<string, CurrencyUnitNames>();
        foreach (var (lang, names) in values ?? [])
        {
            var key = (lang ?? "").Trim().ToLowerInvariant();
            var major = (names?.Major ?? "").Trim();
            var minor = (names?.Minor ?? "").Trim();
            if (!IsLanguageCode(key) || major.Length == 0) continue;
            if (major.Length > 60 || minor.Length > 60) Fail("Currency unit names are limited to 60 characters.");
            result[key] = new CurrencyUnitNames { Major = major, Minor = minor };
        }
        return result;
    }

    private static void Fail(string message) => throw new ArgumentException(message);
}
