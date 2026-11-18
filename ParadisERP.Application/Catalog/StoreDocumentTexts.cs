using ParadisERP.Application.Contracts;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;

namespace ParadisERP.Application.Catalog;

/// <summary>
/// The company's printed-document settings (docs/07 §4.3): the text under each document kind
/// per language, the three contact lines an invoice carries beside them, and the rule that
/// governs how often a kind's wording may be rewritten.
///
/// <para>
/// Pure, so everything worth a test needs no HTTP host: that a text over the cap or a kind
/// nobody defined is refused rather than stored, that saving these never touches the replicated
/// part of the Store row, and — the part the owner asked for — that a save which changes nothing
/// consumes nothing while the change past the allowance is refused with the figures in the
/// sentence. The same habit as <c>CustomerRules</c> and <c>RentalRules</c>: the decision is a
/// function of values, the database only supplies the count and stores the outcome.
/// </para>
/// <para>
/// The endpoint applies what comes back with a plain <c>SaveChangesAsync</c> — never
/// <c>SaveWithVersionsAsync</c>, because none of this is on <c>StoreDto</c> and a version bump
/// would only make every till re-pull a row whose synced fields did not change.
/// </para>
/// </summary>
public static class StoreDocumentTexts
{
    public const int MaxEmailLength = 200;
    public const int MaxWebsiteLength = 200;
    public const int MaxBankDetailsLength = 400;

    /// <summary>A title in more languages than this no longer fits beside the company block.</summary>
    public const int MaxTitleLanguages = 3;

    /// <summary>
    /// What a valid request becomes on the row; null texts mean "nothing configured".
    /// <paramref name="Wordings"/> null means the request did not send the list and the stored one
    /// stays; an empty list removes them all.
    /// </summary>
    public sealed record Normalized(
        Dictionary<string, Dictionary<string, string>>? Texts, string? Email, string? Website, string? BankDetails,
        int ChangesPerYear = Store.DefaultDocumentTextChangesPerYear,
        IReadOnlyList<string>? TitleLanguages = null,
        IReadOnlyList<string>? ItemNameLanguages = null,
        IReadOnlyList<ContractWording>? Wordings = null);

    /// <summary>
    /// Trims, drops empty texts and unknown languages, and refuses what cannot be stored. Returns
    /// the one English sentence for the 400, or null and the normalised values.
    /// </summary>
    /// <param name="storedWordings">
    /// The wordings on the row, which stand when the request does not send the list (an older
    /// screen): their texts are still accepted under their keys.
    /// </param>
    public static string? TryNormalize(StoreDocumentsDto? dto, out Normalized result, IReadOnlyList<ContractWording>? storedWordings = null)
    {
        result = new Normalized(null, null, null, null);
        if (dto is null) return "A body is required.";

        if (Wordings(dto.ContractWordings, out var wordings) is string wordingProblem) return wordingProblem;
        var wordingKeys = (wordings ?? storedWordings ?? []).Select(w => w.Key).ToHashSet(StringComparer.Ordinal);

        var texts = new Dictionary<string, Dictionary<string, string>>();
        foreach (var (kind, byLang) in dto.DocumentTexts ?? new Dictionary<string, IReadOnlyDictionary<string, string>>())
        {
            var key = (kind ?? "").Trim();
            if (!DocumentTextKinds.All.Contains(key) && !wordingKeys.Contains(key))
                return DocumentTextKinds.IsContractWording(key)
                    ? $"'{key}' is not one of this company's contract wordings: add it to the list with a name first."
                    : $"'{key}' is not a printed document kind ({string.Join(", ", DocumentTextKinds.All)}).";

            var clean = new Dictionary<string, string>();
            foreach (var (lang, text) in byLang ?? new Dictionary<string, string>())
            {
                var code = (lang ?? "").Trim().ToLowerInvariant();
                var value = (text ?? "").Trim();
                if (!RegionalSettingsValidator.IsLanguageCode(code) || value.Length == 0) continue;
                if (value.Length > Store.DocumentTextMaxLength)
                    return $"The {key} text in '{code}' is limited to {Store.DocumentTextMaxLength} characters.";
                clean[code] = value;
            }
            if (clean.Count > 0) texts[key] = clean;
        }

        // A named wording is a wording: one with no text would print a contract with no terms at
        // all under a name that promises some. The standard wording may still be empty, as it always could.
        foreach (var wording in wordings ?? [])
            if (!texts.ContainsKey(wording.Key))
                return $"The contract wording \"{NameOf(wording)}\" has no text: write it in at least one language, or remove the wording.";

        var email = Blank(dto.Email);
        var website = Blank(dto.Website);
        var bank = Blank(dto.BankDetails);
        if (email?.Length > MaxEmailLength) return $"E-mail is limited to {MaxEmailLength} characters.";
        if (website?.Length > MaxWebsiteLength) return $"Website is limited to {MaxWebsiteLength} characters.";
        if (bank?.Length > MaxBankDetailsLength) return $"Bank details are limited to {MaxBankDetailsLength} characters.";

        // Zero is a legitimate setting and means "frozen until he raises it again"; there is no
        // upper bound, because the limit is the owner's own discipline and he is entitled to
        // put it wherever he likes. Only a negative one is meaningless, so only it is refused.
        if (dto.ChangesPerYear < 0)
            return "The number of wording changes allowed per year cannot be negative; use 0 to freeze the wording.";

        if (Languages(dto.TitleLanguages, MaxTitleLanguages, "A document title", out var titleLanguages) is string titleProblem)
            return titleProblem;
        if (Languages(dto.ItemNameLanguages, MaxItemNameLanguages, "An item's name", out var itemLanguages) is string itemProblem)
            return itemProblem;

        result = new Normalized(texts.Count > 0 ? texts : null, email, website, bank, dto.ChangesPerYear,
            titleLanguages, itemLanguages, wordings);
        return null;
    }

    /// <summary>
    /// The named contract wordings as sent, cleaned: a key of the right shape, sent once; a name in
    /// at least one language the profile could enable, none longer than a select can show. Null when
    /// the request did not send the list at all.
    /// </summary>
    private static string? Wordings(IReadOnlyList<ContractWordingDto>? sent, out List<ContractWording>? clean)
    {
        clean = null;
        if (sent is null) return null;
        if (sent.Count > DocumentTextKinds.MaxContractWordings)
            return $"A company keeps at most {DocumentTextKinds.MaxContractWordings} contract wordings besides the standard one.";

        var list = new List<ContractWording>();
        foreach (var wording in sent)
        {
            var key = (wording?.Key ?? "").Trim();
            if (!DocumentTextKinds.IsContractWording(key))
                return $"'{key}' is not a contract wording key ({DocumentTextKinds.ContractWordingPrefix} and up to 16 lower-case letters or digits).";
            if (list.Any(w => w.Key == key)) return $"The contract wording '{key}' is listed twice.";

            var names = new Dictionary<string, string>();
            foreach (var (lang, name) in wording!.Names ?? new Dictionary<string, string>())
            {
                var code = (lang ?? "").Trim().ToLowerInvariant();
                var value = (name ?? "").Trim();
                if (!RegionalSettingsValidator.IsLanguageCode(code) || value.Length == 0) continue;
                if (value.Length > DocumentTextKinds.MaxContractWordingName)
                    return $"A contract wording's name is limited to {DocumentTextKinds.MaxContractWordingName} characters.";
                names[code] = value;
            }
            if (names.Count == 0) return "Every contract wording needs a name, so it can be chosen on a contract.";
            list.Add(new ContractWording { Key = key, Names = names });
        }
        clean = list;
        return null;
    }

    /// <summary>The name a server sentence uses: the French one, or whichever there is.</summary>
    public static string NameOf(ContractWording wording) =>
        wording.Names.TryGetValue("fr", out var fr) ? fr : wording.Names.Values.FirstOrDefault() ?? wording.Key;

    /// <summary>Every wording a sentence about these rows may need to name, keyed by wording key.</summary>
    public static IReadOnlyDictionary<string, string> WordingLabels(params IEnumerable<ContractWording>?[] lists)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var list in lists)
            foreach (var wording in list ?? [])
                labels.TryAdd(wording.Key, NameOf(wording));
        return labels;
    }

    /// <summary>
    /// The keys a save removes from the list: the ones on the row the request no longer sends.
    /// Empty when the request did not send the list, which leaves it as it is.
    /// </summary>
    public static IReadOnlyList<string> RemovedWordings(IReadOnlyList<ContractWording>? stored, Normalized values) =>
        values.Wordings is null
            ? []
            : (stored ?? []).Select(w => w.Key).Except(values.Wordings.Select(w => w.Key), StringComparer.Ordinal).ToList();

    /// <summary>A name prints once, or with one more language beneath; a third would not fit a table cell.</summary>
    public const int MaxItemNameLanguages = 2;

    /// <summary>Trimmed, lower-cased, de-duplicated codes in the order sent; null for none.</summary>
    private static string? Languages(IReadOnlyList<string>? sent, int max, string what, out List<string>? clean)
    {
        clean = null;
        var codes = new List<string>();
        foreach (var lang in sent ?? [])
        {
            var code = (lang ?? "").Trim().ToLowerInvariant();
            if (code.Length == 0) continue;
            if (!RegionalSettingsValidator.IsLanguageCode(code)) return $"'{code}' is not a language code.";
            if (!codes.Contains(code)) codes.Add(code);
        }
        if (codes.Count > max) return $"{what} prints in at most {max} languages.";
        clean = codes.Count > 0 ? codes : null;
        return null;
    }

    /// <summary>
    /// Writes the normalised values onto the row. A <b>new</b> dictionary is assigned rather than
    /// the old one edited: the column's comparer detects a replaced reference for certain, and a
    /// nested edit only through its serialised form.
    /// </summary>
    public static void Apply(Store store, Normalized values)
    {
        store.DocumentTexts = TextsAfter(store, values);
        store.Email = values.Email;
        store.Website = values.Website;
        store.BankDetails = values.BankDetails;
        store.DocumentTextChangesPerYear = values.ChangesPerYear;
        store.DocumentTitleLanguages = values.TitleLanguages is { Count: > 0 } langs ? string.Join(",", langs) : null;
        store.DocumentItemNameLanguages = values.ItemNameLanguages is { Count: > 0 } items ? string.Join(",", items) : null;
        // A new list, for the same reason as the texts; left as it is when the request sent none.
        if (values.Wordings is not null)
            store.ContractWordings = values.Wordings.Count > 0
                ? values.Wordings.Select(w => new ContractWording { Key = w.Key, Names = new Dictionary<string, string>(w.Names) }).ToList()
                : null;
    }

    /// <summary>
    /// The texts a save leaves on the row. Without a list of wordings in the request, the wordings
    /// on the row stand, and so do their texts: an older screen sends only the kinds it knows.
    /// </summary>
    public static Dictionary<string, Dictionary<string, string>>? TextsAfter(Store store, Normalized values)
    {
        if (values.Wordings is not null || store.DocumentTexts is null) return values.Texts;
        var texts = values.Texts is null
            ? new Dictionary<string, Dictionary<string, string>>()
            : new Dictionary<string, Dictionary<string, string>>(values.Texts);
        foreach (var (key, byLang) in store.DocumentTexts)
            if (DocumentTextKinds.IsContractWording(key) && !texts.ContainsKey(key))
                texts[key] = new Dictionary<string, string>(byLang);
        return texts.Count > 0 ? texts : null;
    }

    /// <summary>
    /// The row as the admin screen and the print frame read it; null read as nothing configured.
    /// <paramref name="allowances"/> is what the server counted — the print frame passes none.
    /// </summary>
    public static StoreDocumentsDto ToDto(Store store, IReadOnlyList<DocumentTextAllowanceDto>? allowances = null) =>
        new(
            (store.DocumentTexts ?? new Dictionary<string, Dictionary<string, string>>())
                .ToDictionary(k => k.Key, k => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(k.Value)),
            store.Email, store.Website, store.BankDetails,
            store.DocumentTextChangesPerYear, allowances,
            LogoUrl: StoreLogo.StoreUrl(store.LogoPath),
            TitleLanguages: TitleLanguagesOf(store),
            ItemNameLanguages: Split(store.DocumentItemNameLanguages),
            ContractWordings: (store.ContractWordings ?? [])
                .Select(w => new ContractWordingDto(w.Key, new Dictionary<string, string>(w.Names))).ToList());

    /// <summary>The kinds counted on this row: the fixed ones, then each named contract wording.</summary>
    public static IReadOnlyList<string> KindsOf(IEnumerable<ContractWording>? wordings) =>
        DocumentTextKinds.All.Concat((wordings ?? []).Select(w => w.Key)).ToList();

    /// <summary>The stored list in print order; empty when none is set.</summary>
    public static IReadOnlyList<string> TitleLanguagesOf(Store store) => Split(store.DocumentTitleLanguages);

    private static IReadOnlyList<string> Split(string? stored) =>
        (stored ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ---------- the yearly wording allowance (owner, 2026-09-24) ----------
    //
    // "اشتغل كله وحطه ستنج الادمن يحددها وتتعدل 3 مرة بالسنة" — build it for every kind, and make
    // the limit itself a setting the admin sets, three by default.
    //
    // Read Store.DocumentTextChangesPerYear for why this is here at all: it is the owner's own
    // discipline, not a control against him. He can raise the number from the same screen that
    // shows what is left of it. What actually protects the copy a customer signed under is the
    // DocumentTextChange history, which keeps the predecessor of every rewrite whatever the
    // limit says. Nothing below should be hardened into a lock a future maintainer cannot open.

    /// <summary>What this save may do to one kind's wording.</summary>
    public enum ChangeVerdict
    {
        /// <summary>Within the allowance: write it, and keep the predecessor.</summary>
        Allowed = 0,

        /// <summary>The allowance for this kind and year is spent. Nothing is written.</summary>
        Refused = 1
    }

    /// <summary>
    /// The counting context a save is judged against: the limit in force, the company's calendar
    /// year and how many changes each kind has already had in it.
    /// </summary>
    /// <param name="Limit">
    /// <b>The limit as it stood before this save</b>, deliberately — not the one the same request
    /// may be raising. A save that rewrote the contract and lifted the limit in one go could never
    /// be refused, which would make the discipline decorative. Raising it takes effect from the
    /// next save; he loses nothing but a second click.
    /// </param>
    /// <param name="Used">Changes already made this year, keyed by kind. A kind absent has none.</param>
    public sealed record ChangeAllowance(int Limit, int Year, IReadOnlyDictionary<string, int> Used);

    /// <summary>
    /// One kind's verdict. <see cref="PreviousTexts"/> is the wording as it stood <b>before</b>
    /// this save — what the history row carries, and the reason a signed copy stays readable.
    /// </summary>
    public sealed record ChangeDecision(
        string Kind, ChangeVerdict Verdict, IReadOnlyDictionary<string, string> PreviousTexts,
        int Used, int Limit, int Year, string? Refusal);

    /// <summary>
    /// Compares the incoming wording against the stored wording kind by kind and answers what may
    /// happen. <b>Only the kinds that actually differ come back</b> — an empty list is the
    /// "nothing changed" answer, and it is the common one: a clerk who opens the form and saves it
    /// back has not changed the contract and must not spend an allowance for it.
    ///
    /// <para>
    /// Each kind is counted on its own, so rewriting the invoice footer does not spend a rental
    /// contract change. Within a kind, one save is one change however many languages it rewrote —
    /// that is how the owner thinks of it: he changed the contract.
    /// </para>
    /// <para>
    /// Both sides must already be through <see cref="TryNormalize"/> (trimmed, empties dropped),
    /// or an untrimmed space would read as a rewrite.
    /// </para>
    /// </summary>
    /// <param name="wordingLabels">
    /// The named contract wordings on either side of the save, by key (PinSay #302). Each is counted
    /// as a kind of its own — adding one, rewriting it and removing it are each a change to it — and
    /// the label is the name a refusal says.
    /// </param>
    public static IReadOnlyList<ChangeDecision> Decide(
        IReadOnlyDictionary<string, Dictionary<string, string>>? stored,
        IReadOnlyDictionary<string, Dictionary<string, string>>? incoming,
        ChangeAllowance allowance,
        IReadOnlyDictionary<string, string>? wordingLabels = null)
    {
        var decisions = new List<ChangeDecision>();
        // The fixed kinds, then every named wording either side holds, whether listed or not: a text
        // under a wording key is a wording, and its removal is a change like its rewrite.
        var kinds = DocumentTextKinds.All
            .Concat((wordingLabels?.Keys ?? [])
                .Concat(stored?.Keys ?? []).Concat(incoming?.Keys ?? [])
                .Where(DocumentTextKinds.IsContractWording)
                .Distinct(StringComparer.Ordinal))
            .ToList();
        foreach (var kind in kinds)
        {
            var before = Wording(stored, kind);
            var after = Wording(incoming, kind);
            if (SameWording(before, after)) continue;

            var used = allowance.Used.TryGetValue(kind, out var count) ? count : 0;
            decisions.Add(used >= allowance.Limit
                ? new ChangeDecision(kind, ChangeVerdict.Refused, before, used, allowance.Limit, allowance.Year,
                    Refusal(Label(kind, wordingLabels), used, allowance))
                : new ChangeDecision(kind, ChangeVerdict.Allowed, before, used, allowance.Limit, allowance.Year, null));
        }
        return decisions;
    }

    /// <summary>The one English sentence a refused save answers with: how many were used, out of what, and when it resets.</summary>
    private static string Refusal(string label, int used, ChangeAllowance allowance) =>
        allowance.Limit == 0
            ? $"The {label} wording is frozen: this company allows no changes per year. Raise the allowance on the printed-documents screen to change it."
            : $"The {label} wording has already been changed {used} time{(used == 1 ? "" : "s")} in {allowance.Year}, which is all this company allows per year ({allowance.Limit}). The allowance resets on 1 January {allowance.Year + 1}; until then, raise it on the printed-documents screen.";

    /// <summary>The kind as a sentence reads it. Not translated: server messages are English here, as elsewhere.</summary>
    private static string Label(string kind, IReadOnlyDictionary<string, string>? wordingLabels) =>
        DocumentTextKinds.IsContractWording(kind)
            ? $"rental contract \"{(wordingLabels is not null && wordingLabels.TryGetValue(kind, out var name) ? name : kind)}\""
            : Label(kind);

    private static string Label(string kind) => kind switch
    {
        DocumentTextKinds.Invoice => "invoice",
        DocumentTextKinds.CreditNote => "credit note",
        DocumentTextKinds.ReceiptVoucher => "receipt voucher",
        DocumentTextKinds.Statement => "statement",
        DocumentTextKinds.RentalContract => "rental contract",
        DocumentTextKinds.IssueNote => "issue note",
        DocumentTextKinds.ReturnNote => "return note",
        DocumentTextKinds.Payslip => "payslip",
        DocumentTextKinds.PaymentVoucher => "payment voucher",
        _ => kind
    };

    /// <summary>One kind's per-language wording, empty when the kind has none — the first save has a predecessor too, and it is "nothing".</summary>
    private static IReadOnlyDictionary<string, string> Wording(
        IReadOnlyDictionary<string, Dictionary<string, string>>? texts, string kind) =>
        texts is not null && texts.TryGetValue(kind, out var byLang)
            ? new Dictionary<string, string>(byLang)
            : new Dictionary<string, string>();

    private static bool SameWording(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && string.Equals(pair.Value, other, StringComparison.Ordinal));

    /// <summary>
    /// The calendar year the instant falls in <b>in the company's time zone</b>. A save at 23:30
    /// on 31 December in Algiers is 22:30 UTC the same day, but a save at 00:30 on 1 January is
    /// still 31 December in UTC — counting on the UTC year would put that change in the year the
    /// owner had already closed.
    /// </summary>
    public static int CompanyYear(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(
            utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone).Year;

    /// <summary>
    /// The instant the count for <paramref name="companyYear"/> goes back to zero: the company's
    /// own 1 January at midnight, expressed in UTC so the screen states the date without doing
    /// time-zone arithmetic of its own. A zone this machine mishandles falls back to the plain UTC
    /// midnight rather than throwing, as the receipt clock does.
    /// </summary>
    public static DateTime YearResetsUtc(int companyYear, TimeZoneInfo zone)
    {
        var midnight = new DateTime(companyYear + 1, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        try { return TimeZoneInfo.ConvertTimeToUtc(midnight, zone); }
        catch (ArgumentException) { return DateTime.SpecifyKind(midnight, DateTimeKind.Utc); }
    }

    /// <summary>
    /// What is left of this year's allowance for every kind — the screen reads it before the admin
    /// types, so he knows what a save will cost him rather than discovering it on the refusal.
    /// </summary>
    /// <param name="kinds">The kinds to answer for; the fixed ones when not given (<see cref="KindsOf"/> adds the wordings).</param>
    public static IReadOnlyList<DocumentTextAllowanceDto> Allowances(
        int limit, int year, DateTime resetsOnUtc, IReadOnlyDictionary<string, int> used, IEnumerable<string>? kinds = null) =>
        (kinds ?? DocumentTextKinds.All).Select(kind =>
        {
            var spent = used.TryGetValue(kind, out var count) ? count : 0;
            // Lowering the limit below what a kind has already spent leaves nothing rather than a
            // negative: the changes already made are made, and the screen should say "0 left".
            return new DocumentTextAllowanceDto(kind, year, limit, spent, Math.Max(0, limit - spent), resetsOnUtc);
        }).ToList();

    private static string? Blank(string? value)
    {
        var text = (value ?? "").Trim();
        return text.Length == 0 ? null : text;
    }
}
