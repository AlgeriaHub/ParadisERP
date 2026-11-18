using ParadisERP.Domain;

namespace ParadisERP.Application.Customers;

/// <summary>
/// The rules of the person / company distinction and of the company's authorised
/// representative (المخوَّل), stated once.
///
/// <para>
/// The <c>Problem</c> methods return a short reason code or null, the way
/// <c>RentalRules.IssueProblem</c> does, so the endpoint turns the code into its one English
/// sentence and the screen turns the same code into an i18n key — one answer in the system,
/// said on the screen first and by the server as the fallback.
/// </para>
/// </summary>
public static class CustomerRules
{
    /// <summary>
    /// A kind travels as a string ("Person", "Company", "Unspecified"), case-insensitively; null or
    /// empty is Unspecified, the request default. Anything else is refused rather than defaulted:
    /// a typo must not silently register a company as "we do not know".
    /// </summary>
    public static bool TryParseKind(string? text, out CustomerKind kind)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            kind = CustomerKind.Unspecified;
            return true;
        }
        return Enum.TryParse(text.Trim(), ignoreCase: true, out kind) && Enum.IsDefined(kind);
    }

    /// <summary>
    /// Why a customer save cannot go through, or null.
    ///
    /// <para>
    /// Order matters and is the order a clerk meets them: a new customer says what it is; a kind
    /// never goes back to unspecified (an existing unspecified row may be edited without deciding,
    /// so fixing a phone number never forces the decision); a person carries their own details
    /// and has no representative; a company acts through one — the one decision the feature is
    /// about is taken when the kind is set, in the same save; and a form never overwrites a
    /// current representative, because the history is the point.
    /// </para>
    /// </summary>
    public static string? KindProblem(
        bool isNew, CustomerKind stored, CustomerKind requested,
        bool representativeInBody, bool hasCurrentRepresentative)
    {
        if (isNew && requested == CustomerKind.Unspecified) return "kindRequired";
        if (!isNew && requested == CustomerKind.Unspecified && stored != CustomerKind.Unspecified)
            return "kindCannotRevert";

        if (requested != CustomerKind.Company)
            return representativeInBody ? "personHasNoRepresentative" : null;

        // The stored current representative counts only while the row is still a company: a
        // company turned person had its representative ended, so turning it back appoints again.
        var current = !isNew && stored == CustomerKind.Company && hasCurrentRepresentative;
        if (current && representativeInBody) return "representativeExists";
        if (!current && !representativeInBody) return "companyNeedsRepresentative";
        return null;
    }

    /// <summary>The reason code for a person sent in parts with one of them blank.</summary>
    public const string PersonNameRequired = "personNameRequired";

    /// <summary>
    /// The name a save files (owner, 2026-10-06: "الاسم واللقب" in two inputs, first name first —
    /// the employee form's rule). A person sent in parts needs both, and the full name is composed
    /// from them whatever <paramref name="fullName"/> says. A person sent whole, by an older client,
    /// is filed as the family name with no first name — what the migration did with everyone already
    /// on file. A company or an unspecified row has one name, the one sent, and no parts. Null when
    /// a person's parts were sent and one of them is blank.
    /// </summary>
    public static (string? First, string? Family, string Full)? Names(
        CustomerKind kind, string? fullName, string? firstName, string? familyName)
    {
        var whole = Clip(fullName, 200);
        if (kind != CustomerKind.Person) return (null, null, whole ?? "");
        if (firstName is null && familyName is null) return (null, whole, whole ?? "");
        var first = Clip(firstName, 100);
        var family = Clip(familyName, 200);
        if (first is null || family is null) return null;
        return (first, family, Clip($"{first} {family}", 200)!);
    }

    private static string? Clip(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max].TrimEnd();
    }

    /// <summary>The reason code for a tax number sent for a natural person.</summary>
    public const string TaxIdCompanyOnly = "taxIdCompanyOnly";

    /// <summary>The tax number as it is stored: trimmed, and a blank one is none.</summary>
    public static string? NormalizeTaxId(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>
    /// Why the tax number cannot be saved with this kind, or null.
    ///
    /// <para>
    /// "الرقم الجبائي للشركات فقط" (owner, 2026-10-05): the customer's tax number — labelled by the
    /// country profile, NIF in the Algeria preset — belongs to a company; a natural person has
    /// none. So a person is refused a new or a changed one, and is never "missing" one either
    /// (the store's RequireCustomerTaxId asks it of companies only).
    /// </para>
    /// <para>
    /// The one thing a person may carry is the number they were saved with before this rule,
    /// unchanged — production holds some. An edit that leaves it alone is not a new tax number
    /// and must not fail for a phone fix, and the server never wipes it silently: sending it
    /// blank is how it goes. That allowance is for a row that was <i>already</i> a person: a
    /// company or an unspecified row being made a person meets the rule as a new person does,
    /// so the save that changes the kind clears the number in the same request or is refused.
    /// An unspecified kind, requested, keeps its behaviour from before: nobody has said what it is.
    /// </para>
    /// </summary>
    public static string? TaxIdProblem(
        bool isNew, CustomerKind stored, string? storedTaxId, CustomerKind requested, string? requestedTaxId)
    {
        if (requested != CustomerKind.Person) return null;
        var sent = NormalizeTaxId(requestedTaxId);
        if (sent is null) return null;
        if (!isNew && stored == CustomerKind.Person && string.Equals(sent, NormalizeTaxId(storedTaxId), StringComparison.Ordinal))
            return null;
        return TaxIdCompanyOnly;
    }

    /// <summary>
    /// One day of slack on the future check: the form sends a company-time-zone day, and UTC
    /// midnight of "today" can be a few hours ahead of UTC now.
    /// </summary>
    public static readonly TimeSpan FutureSlack = TimeSpan.FromDays(1);

    /// <summary>
    /// Why a representative cannot be appointed, or null. A takeover dated before the current
    /// one started would put two people in charge at once; one dated in the future is a
    /// typo, because the history records what happened.
    ///
    /// <para>
    /// The start comparison is deliberately to the instant, not the day: it is what keeps a
    /// closed row from ending before it began. The customers screen therefore floors a
    /// same-day takeover at the current one's own start (<c>takeoverInstant</c> in
    /// <c>customer-kind.ts</c>) and says both refusals in the reader's language before sending.
    /// </para>
    /// </summary>
    public static string? RepresentativeProblem(
        string? fullName, DateTime fromUtc, DateTime? currentFromUtc, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return "nameRequired";
        if (currentFromUtc is DateTime current && fromUtc < current) return "startsBeforeCurrent";
        if (fromUtc > nowUtc + FutureSlack) return "startsInFuture";
        return null;
    }
}
