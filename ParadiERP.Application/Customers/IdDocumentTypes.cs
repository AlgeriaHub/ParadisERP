using System.Globalization;
using System.Text.RegularExpressions;

namespace ParadiERP.Application.Customers;

/// <summary>
/// The identity papers the company's contract accepts (owner's paperwork, 2026-09-30): the
/// national card, the driving licence and the passport. Stored as these keys, never as words, so
/// the screen and the printed contract name them in the reader's language.
/// </summary>
public static partial class IdDocumentTypes
{
    public const string NationalCard = "nationalCard";
    public const string DrivingLicence = "drivingLicence";
    public const string Passport = "passport";

    public static readonly IReadOnlyList<string> All = [NationalCard, DrivingLicence, Passport];

    /// <summary>The issuer column's width (Customer and CustomerRepresentative alike).</summary>
    public const int MaxIssuerLength = 120;

    /// <summary>The number column's width, on the customer, the representative and the contract.</summary>
    public const int MaxNumberLength = 40;

    /// <summary>Nobody alive signs a contract with a paper issued, or a birth, before this day.</summary>
    public static readonly DateOnly Earliest = new(1900, 1, 1);

    /// <summary>
    /// Blank reads as "not said" (<paramref name="key"/> null, true). A known key is taken whatever
    /// its case and returned as spelled here; anything else is false.
    /// </summary>
    public static bool TryParse(string? text, out string? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        key = All.FirstOrDefault(k => string.Equals(k, text.Trim(), StringComparison.OrdinalIgnoreCase));
        return key is not null;
    }

    /// <summary>What is stored: the key as spelled here, or null for blank. Run <see cref="Problem"/> first.</summary>
    public static string? Normalize(string? text) => TryParse(text, out var key) ? key : null;

    /// <summary>The one English sentence for the 400, or null: a known type or none, and an issuer that fits.</summary>
    public static string? Problem(string? type, string? issuer)
    {
        if (!TryParse(type, out _))
            return $"The identity document type must be one of: {string.Join(", ", All)}.";
        if (issuer?.Trim().Length > MaxIssuerLength)
            return $"The identity document's issuer is limited to {MaxIssuerLength} characters.";
        return null;
    }

    /// <summary>
    /// Every part of an identity that was filled in must read as one (owner, 2026-10-02: the issuer,
    /// the issue date and the birth date are optional, "but if entered, correct — or empty"). Blank
    /// is never a problem here; whether a part is <i>required</i> is the caller's rule. The one
    /// English sentence for the 400, or null.
    ///
    /// <para>
    /// The number is letters, digits, spaces, hyphens and slashes with at least one digit — enough
    /// to catch a name typed in the wrong box without guessing any country's card format. The
    /// issuer must contain a letter: a wilaya, daïra or commune is a place, not a figure. A date
    /// is neither after <paramref name="today"/> nor before <see cref="Earliest"/>, and a paper
    /// cannot be issued before its holder was born.
    /// </para>
    /// </summary>
    public static string? IdentityProblem(
        string? type, string? number, string? issuer, DateOnly? issuedOn, DateOnly? birthDate, DateOnly today)
    {
        if (Problem(type, issuer) is string basic) return basic;

        var digits = (number ?? "").Trim();
        if (digits.Length > MaxNumberLength)
            return $"The identity document number is limited to {MaxNumberLength} characters.";
        if (digits.Length > 0 && !NumberPattern().IsMatch(digits))
            return "The identity document number may hold only letters, digits, spaces, hyphens and slashes, and at least one digit.";

        var by = (issuer ?? "").Trim();
        if (by.Length > 0 && !by.Any(char.IsLetter))
            return "The identity document's issuer must name a place or an authority, not only figures.";

        if (birthDate is DateOnly born)
        {
            if (born > today) return "The date of birth cannot be in the future.";
            if (born < Earliest) return $"The date of birth cannot be before {Earliest.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.";
        }
        if (issuedOn is DateOnly issued)
        {
            if (issued > today) return "The identity document's issue date cannot be in the future.";
            if (issued < Earliest) return $"The identity document's issue date cannot be before {Earliest.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.";
            if (birthDate is DateOnly holderBorn && issued < holderBorn)
                return "The identity document cannot have been issued before its holder was born.";
        }
        return null;
    }

    /// <summary>
    /// The latest date that is "today" anywhere: UTC plus fourteen hours. Used where the company's
    /// own time zone is not to hand, so a paper issued this morning in Algiers is never "in the
    /// future" because the server's clock is still on yesterday in UTC.
    /// </summary>
    public static DateOnly TodayAnywhere(DateTime utcNow) => DateOnly.FromDateTime(utcNow.AddHours(14));

    [GeneratedRegex(@"^(?=.*\d)[\p{L}\p{Nd}][\p{L}\p{Nd} /-]*$")]
    private static partial Regex NumberPattern();
}
