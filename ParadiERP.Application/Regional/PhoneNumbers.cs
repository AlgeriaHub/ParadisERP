using System.Text;
using System.Text.RegularExpressions;

namespace ParadiERP.Application.Regional;

/// <summary>
/// How a customer's phone is written in this country (owner, 2026-11-17: "phone must be saved as
/// mobile format" and "make the country key required if we use messages in the future"). Country
/// data, so it lives in the profile: Algeria's is +213 and a mobile starting 5, 6 or 7.
/// </summary>
public sealed class PhoneNumberRules
{
    /// <summary>The country calling code, digits only, e.g. "213". Empty: phones are kept as typed.</summary>
    public string CallingCode { get; set; } = "";
    /// <summary>The national trunk prefix people type before a number, e.g. "0"; empty where there is none.</summary>
    public string TrunkPrefix { get; set; } = "";
    /// <summary>A mobile's national number (without trunk prefix or calling code), e.g. <c>^[567][0-9]{8}$</c>.</summary>
    public string MobilePattern { get; set; } = "";
    /// <summary>How the national number is grouped on screen and paper, e.g. [3,2,2,2] → +213 658 41 77 30.</summary>
    public List<int> DisplayGroups { get; set; } = new();
    /// <summary>A mobile as people type it at home, for the message that asks for one, e.g. "0555123456".</summary>
    public string Example { get; set; } = "";

    public bool IsSet => CallingCode.Length > 0 && MobilePattern.Length > 0;
}

/// <summary>What <see cref="PhoneNumbers.Normalize"/> made of a typed phone.</summary>
/// <param name="Value">The phone to store: null when none was typed, the international form when it was read.</param>
/// <param name="IsMobile">False when it is not a mobile of the country, nor a number given with + and another country's code.</param>
public readonly record struct PhoneReading(string? Value, bool IsMobile);

public static partial class PhoneNumbers
{
    /// <summary>The stable code of the refusal, resolved to a sentence by the screens.</summary>
    public const string NotMobile = "phoneNotMobile";

    /// <summary>The English fallback for <see cref="NotMobile"/>.</summary>
    public const string NotMobileMessage =
        "Enter a mobile number of the company's country, or a number written with + and its country code.";

    /// <summary>
    /// Reads a typed phone as a mobile in international form: spaces, dots, dashes and brackets go,
    /// a leading 00 becomes +, the trunk prefix (0) is replaced by + and the calling code.
    /// 06.58.41.77.30, 0658417730, 00213658417730 and +213 658 41 77 30 all read +213658417730.
    /// A number written with + and another country's code is taken as given (8–15 digits): its
    /// country's mobile rules are not known here. Anything else is not a mobile.
    /// </summary>
    public static PhoneReading Normalize(string? typed, PhoneNumberRules? rules)
    {
        if (string.IsNullOrWhiteSpace(typed)) return new(null, true);
        var trimmed = typed.Trim();
        if (rules is null || !rules.IsSet) return new(trimmed, true);

        var compact = Compact(trimmed);
        if (compact.StartsWith("00", StringComparison.Ordinal)) compact = "+" + compact[2..];
        var plus = compact.StartsWith('+');
        var digits = plus ? compact[1..] : compact;
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit)) return new(trimmed, false);

        var code = rules.CallingCode;
        string national;
        if (plus)
        {
            if (!digits.StartsWith(code, StringComparison.Ordinal))
                return digits.Length is >= 8 and <= 15 ? new("+" + digits, true) : new(trimmed, false);
            national = WithoutTrunk(digits[code.Length..], rules);
        }
        else if (rules.TrunkPrefix.Length > 0 && digits.StartsWith(rules.TrunkPrefix, StringComparison.Ordinal))
            national = digits[rules.TrunkPrefix.Length..];
        else if (digits.StartsWith(code, StringComparison.Ordinal) && IsMobile(WithoutTrunk(digits[code.Length..], rules), rules))
            national = WithoutTrunk(digits[code.Length..], rules); // 213658417730, typed without its +
        else
            national = digits;

        return IsMobile(national, rules) ? new("+" + code + national, true) : new(trimmed, false);
    }

    /// <summary>
    /// True when a stored phone is not in the form a save would give it: a phone from before the
    /// rule that the start-up correction could not read (a landline, two numbers in one box).
    /// </summary>
    public static bool NeedsFixing(string? stored, PhoneNumberRules? rules)
    {
        if (string.IsNullOrWhiteSpace(stored) || rules is null || !rules.IsSet) return false;
        var reading = Normalize(stored, rules);
        return !reading.IsMobile || !string.Equals(reading.Value, stored, StringComparison.Ordinal);
    }

    /// <summary>
    /// The profile's rules, or the country preset's when the profile was saved before phones had
    /// rules (a stored profile has no <c>Phone</c> block until the operator saves it again).
    /// </summary>
    public static void FillFromPreset(RegionalSettings settings)
    {
        settings.Phone ??= new();
        if (settings.Phone.IsSet) return;
        if (RegionalPresets.Create(settings.CountryCode)?.Phone is { IsSet: true } preset) settings.Phone = preset;
    }

    /// <summary>Trims and checks the profile's rules; returns the first problem, or null.</summary>
    public static string? NormalizeAndCheck(PhoneNumberRules rules)
    {
        rules.CallingCode = (rules.CallingCode ?? "").Trim().TrimStart('+');
        rules.TrunkPrefix = (rules.TrunkPrefix ?? "").Trim();
        rules.MobilePattern = (rules.MobilePattern ?? "").Trim();
        rules.Example = (rules.Example ?? "").Trim();
        rules.DisplayGroups = (rules.DisplayGroups ?? []).ToList();
        if (rules.CallingCode.Length == 0 && rules.MobilePattern.Length == 0) return null;
        if (!CallingCodePattern().IsMatch(rules.CallingCode)) return "The phone calling code must be 1–3 digits, e.g. 213.";
        if (!TrunkPattern().IsMatch(rules.TrunkPrefix)) return "The phone trunk prefix must be at most 2 digits, e.g. 0.";
        if (rules.MobilePattern.Length == 0) return "A mobile number pattern is required with a calling code.";
        try
        {
            _ = new Regex(rules.MobilePattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            return "The mobile number pattern is not a valid regular expression.";
        }
        if (rules.DisplayGroups.Any(g => g is < 1 or > 6)) return "Phone display groups must be 1–6 digits each.";
        if (rules.Example.Length > 0 && !Normalize(rules.Example, rules).IsMobile)
            return "The phone example must be a mobile number the pattern accepts.";
        return null;
    }

    private static bool IsMobile(string national, PhoneNumberRules rules) =>
        national.Length > 0 && Regex.IsMatch(national, rules.MobilePattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));

    /// <summary>+2130658417730: a trunk 0 typed after the calling code is dropped too.</summary>
    private static string WithoutTrunk(string national, PhoneNumberRules rules) =>
        rules.TrunkPrefix.Length > 0
        && national.StartsWith(rules.TrunkPrefix, StringComparison.Ordinal)
        && !IsMobile(national, rules)
        && IsMobile(national[rules.TrunkPrefix.Length..], rules)
            ? national[rules.TrunkPrefix.Length..]
            : national;

    private static string Compact(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            if (!char.IsWhiteSpace(c) && c is not ('.' or '-' or '(' or ')')) sb.Append(c);
        return sb.ToString();
    }

    [GeneratedRegex("^[0-9]{1,3}$")]
    private static partial Regex CallingCodePattern();

    [GeneratedRegex("^[0-9]{0,2}$")]
    private static partial Regex TrunkPattern();
}
