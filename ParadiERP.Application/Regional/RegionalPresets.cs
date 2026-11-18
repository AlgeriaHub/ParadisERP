namespace ParadiERP.Application.Regional;

/// <summary>
/// Starting points the operator copies into a company's profile and then adjusts. Rates and
/// identifiers must be re-checked against current law before a company goes live.
/// </summary>
public static class RegionalPresets
{
    public const string DefaultCountry = "DZ";

    public static IReadOnlyList<string> Countries { get; } = ["DZ", "MA", "TN", "FR", "SA"];

    /// <summary>Returns a fresh copy, so callers may mutate it.</summary>
    public static RegionalSettings? Create(string? countryCode) => (countryCode ?? "").Trim().ToUpperInvariant() switch
    {
        "DZ" => Algeria(),
        "MA" => Morocco(),
        "TN" => Tunisia(),
        "FR" => France(),
        "SA" => SaudiArabia(),
        _ => null
    };

    private static Dictionary<string, string> Text(string ar, string fr, string en) => new() { ["ar"] = ar, ["fr"] = fr, ["en"] = en };

    /// <summary>Unit names per language; a preset names every language it enables so the amount in words never falls back to figures by accident.</summary>
    private static Dictionary<string, CurrencyUnitNames> Units(
        (string Major, string Minor) ar, (string Major, string Minor) fr, (string Major, string Minor) en) => new()
    {
        ["ar"] = new CurrencyUnitNames { Major = ar.Major, Minor = ar.Minor },
        ["fr"] = new CurrencyUnitNames { Major = fr.Major, Minor = fr.Minor },
        ["en"] = new CurrencyUnitNames { Major = en.Major, Minor = en.Minor },
    };

    private static TaxRateLabel Rate(decimal rate, string ar, string fr, string en) =>
        new() { Rate = rate, Labels = Text(ar, fr, en) };

    private static readonly (string Ar, string Fr, string En) Standard = ("النسبة العادية", "Taux normal", "Standard rate");
    private static readonly (string Ar, string Fr, string En) Reduced = ("النسبة المخفضة", "Taux réduit", "Reduced rate");
    private static readonly (string Ar, string Fr, string En) Exempt = ("معفى", "Exonéré", "Exempt");

    private static TaxRateLabel Rate(decimal rate, (string Ar, string Fr, string En) label) => Rate(rate, label.Ar, label.Fr, label.En);

    private static FiscalIdDefinition Id(string key, string code, string ar, string fr, string en, bool required = false) =>
        new() { Key = key, Code = code, Labels = Text(ar, fr, en), Required = required };

    private static RegionalSettings Algeria() => new()
    {
        CountryCode = "DZ",
        Phone = new PhoneNumberRules { CallingCode = "213", TrunkPrefix = "0", MobilePattern = "^[567][0-9]{8}$", DisplayGroups = [3, 2, 2, 2], Example = "0555123456" },
        CurrencyCode = "DZD",
        CurrencyDecimals = 2,
        CurrencySymbols = Text("دج", "DA", "DA"),
        CurrencyUnitNames = Units(("دينار", "سنتيم"), ("dinar", "centime"), ("dinar", "centime")),
        TimeZone = "Africa/Algiers",
        Languages = ["fr", "ar", "en"],
        DefaultLanguage = "fr",
        TaxNames = Text("ض.م", "TVA", "VAT"),
        TaxRates = [19m, 9m, 0m],
        TaxRateLabels = [Rate(19m, Standard), Rate(9m, Reduced), Rate(0m, Exempt)],
        DefaultTaxRate = 19m,
        // The figure on the shelf, the product card and the contract is the price itself; the tax
        // is charged on top of it when the document carries tax. The company sells to trades at
        // a price before tax, so a price read as tax-inclusive would quietly shrink every base.
        PricesIncludeTax = false,
        FiscalIds =
        [
            Id("nif", "NIF", "الرقم الجبائي", "Numéro d'identification fiscale (NIF)", "Tax ID (NIF)"),
            Id("nis", "NIS", "الرقم الإحصائي", "Numéro d'identification statistique (NIS)", "Statistical ID (NIS)"),
            Id("rc", "RC", "السجل التجاري", "Registre du commerce (RC)", "Trade register (RC)"),
            Id("ai", "AI", "رقم المادة الجبائية", "Article d'imposition (AI)", "Tax article (AI)")
        ],
        CustomerTaxIdLabels = Text("الرقم الجبائي", "NIF", "Tax ID (NIF)"),
        CashDenominations = [100m, 200m, 500m, 1000m, 2000m, 5000m],
        PaymentMethods = ["Cash", "Card", "Credit"]
    };

    private static RegionalSettings Morocco() => new()
    {
        CountryCode = "MA",
        Phone = new PhoneNumberRules { CallingCode = "212", TrunkPrefix = "0", MobilePattern = "^[67][0-9]{8}$", DisplayGroups = [1, 2, 2, 2, 2], Example = "0612345678" },
        CurrencyCode = "MAD",
        CurrencyDecimals = 2,
        CurrencySymbols = Text("د.م.", "DH", "MAD"),
        CurrencyUnitNames = Units(("درهم", "سنتيم"), ("dirham", "centime"), ("dirham", "centime")),
        TimeZone = "Africa/Casablanca",
        Languages = ["fr", "ar", "en"],
        DefaultLanguage = "fr",
        TaxNames = Text("الضريبة على القيمة المضافة", "TVA", "VAT"),
        TaxRates = [20m, 14m, 10m, 7m, 0m],
        TaxRateLabels = [Rate(20m, Standard), Rate(14m, Reduced), Rate(10m, Reduced), Rate(7m, Reduced), Rate(0m, Exempt)],
        DefaultTaxRate = 20m,
        PricesIncludeTax = true,
        FiscalIds =
        [
            Id("ice", "ICE", "المعرف الموحد للمقاولة", "Identifiant commun de l'entreprise (ICE)", "Common company ID (ICE)"),
            Id("if", "IF", "التعريف الضريبي", "Identifiant fiscal (IF)", "Tax ID (IF)"),
            Id("rc", "RC", "السجل التجاري", "Registre du commerce (RC)", "Trade register (RC)"),
            Id("tp", "TP", "الرسم المهني", "Taxe professionnelle (TP)", "Business tax (TP)"),
            Id("cnss", "CNSS", "الضمان الاجتماعي", "CNSS", "Social security (CNSS)")
        ],
        CustomerTaxIdLabels = Text("المعرف الموحد للمقاولة", "ICE", "Company ID (ICE)"),
        CashDenominations = [20m, 50m, 100m, 200m],
        PaymentMethods = ["Cash", "Card", "Credit"]
    };

    private static RegionalSettings Tunisia() => new()
    {
        CountryCode = "TN",
        Phone = new PhoneNumberRules { CallingCode = "216", TrunkPrefix = "", MobilePattern = "^[2459][0-9]{7}$", DisplayGroups = [2, 3, 3], Example = "20123456" },
        CurrencyCode = "TND",
        CurrencyDecimals = 3,
        CurrencySymbols = Text("د.ت", "DT", "TND"),
        CurrencyUnitNames = Units(("دينار", "مليم"), ("dinar", "millime"), ("dinar", "millime")),
        TimeZone = "Africa/Tunis",
        Languages = ["fr", "ar", "en"],
        DefaultLanguage = "fr",
        TaxNames = Text("الأداء على القيمة المضافة", "TVA", "VAT"),
        TaxRates = [19m, 13m, 7m, 0m],
        TaxRateLabels = [Rate(19m, Standard), Rate(13m, Reduced), Rate(7m, Reduced), Rate(0m, Exempt)],
        DefaultTaxRate = 19m,
        PricesIncludeTax = true,
        FiscalIds =
        [
            Id("mf", "MF", "المعرف الجبائي", "Matricule fiscal (MF)", "Tax ID (MF)"),
            Id("rne", "RNE", "السجل الوطني للمؤسسات", "Registre national des entreprises (RNE)", "Business register (RNE)")
        ],
        CustomerTaxIdLabels = Text("المعرف الجبائي", "Matricule fiscal", "Tax ID (MF)"),
        CashDenominations = [5m, 10m, 20m, 50m],
        PaymentMethods = ["Cash", "Card", "Credit"]
    };

    private static RegionalSettings France() => new()
    {
        CountryCode = "FR",
        Phone = new PhoneNumberRules { CallingCode = "33", TrunkPrefix = "0", MobilePattern = "^[67][0-9]{8}$", DisplayGroups = [1, 2, 2, 2, 2], Example = "0612345678" },
        CurrencyCode = "EUR",
        CurrencyDecimals = 2,
        CurrencySymbols = Text("€", "€", "€"),
        CurrencyUnitNames = Units(("أورو", "سنت"), ("euro", "centime"), ("euro", "cent")),
        TimeZone = "Europe/Paris",
        Languages = ["fr", "en"],
        DefaultLanguage = "fr",
        TaxNames = Text("ضريبة القيمة المضافة", "TVA", "VAT"),
        TaxRates = [20m, 10m, 5.5m, 2.1m, 0m],
        TaxRateLabels =
        [
            Rate(20m, Standard), Rate(10m, "النسبة الوسيطة", "Taux intermédiaire", "Intermediate rate"),
            Rate(5.5m, Reduced), Rate(2.1m, "النسبة الخاصة", "Taux particulier", "Special rate"), Rate(0m, Exempt)
        ],
        DefaultTaxRate = 20m,
        PricesIncludeTax = true,
        FiscalIds =
        [
            Id("siret", "SIRET", "SIRET", "SIRET", "SIRET"),
            Id("vat", "TVA", "الرقم الضريبي", "N° TVA intracommunautaire", "EU VAT number"),
            Id("rcs", "RCS", "السجل التجاري", "RCS", "Trade register (RCS)")
        ],
        CustomerTaxIdLabels = Text("الرقم الضريبي", "N° TVA", "VAT number"),
        CashDenominations = [5m, 10m, 20m, 50m, 100m],
        PaymentMethods = ["Cash", "Card", "Credit"]
    };

    private static RegionalSettings SaudiArabia() => new()
    {
        CountryCode = "SA",
        Phone = new PhoneNumberRules { CallingCode = "966", TrunkPrefix = "0", MobilePattern = "^5[0-9]{8}$", DisplayGroups = [2, 3, 4], Example = "0512345678" },
        CurrencyCode = "SAR",
        CurrencyDecimals = 2,
        CurrencySymbols = Text("ر.س", "SAR", "SAR"),
        CurrencyUnitNames = Units(("ريال", "هللة"), ("riyal", "halala"), ("riyal", "halala")),
        TimeZone = "Asia/Riyadh",
        Languages = ["ar", "en"],
        DefaultLanguage = "ar",
        TaxNames = Text("ضريبة القيمة المضافة", "TVA", "VAT"),
        TaxRates = [15m, 0m],
        TaxRateLabels = [Rate(15m, Standard), Rate(0m, "نسبة صفرية", "Taux zéro", "Zero-rated")],
        ReceiptLegalText = Text("فاتورة ضريبية مبسطة", "Facture fiscale simplifiée", "Simplified tax invoice"),
        // The invoice title belongs at the top of the ticket.
        ReceiptSections = ["header", "legal", "fiscalIds", "meta", "lines", "totals", "taxBreakdown", "payment", "footer"],
        DefaultTaxRate = 15m,
        PricesIncludeTax = true,
        FiscalIds =
        [
            Id("vat", "VAT", "الرقم الضريبي", "Numéro de TVA", "VAT registration number"),
            Id("cr", "CR", "السجل التجاري", "Registre du commerce", "Commercial registration (CR)")
        ],
        CustomerTaxIdLabels = Text("الرقم الضريبي", "N° TVA", "VAT number"),
        CashDenominations = [5m, 10m, 50m, 100m, 500m],
        PaymentMethods = ["Cash", "Card", "Credit"]
    };
}
