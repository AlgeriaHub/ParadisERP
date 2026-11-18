using System.ComponentModel.DataAnnotations;

namespace ParadiERP.Domain;

/// <summary>
/// One change to one document kind's printed wording (<c>Store.DocumentTexts</c>).
///
/// <para>
/// <b>Not a <see cref="SyncEntity"/>.</b> The texts themselves are deliberately absent from
/// <c>StoreDto</c> so a till never pulls paragraphs of contract terms; their history has even
/// less business on a cashier's device.
/// </para>
/// <para>
/// <b>It stores the wording as it stood BEFORE the change, not after.</b> The wording in force
/// today is on the <c>Store</c> row, so keeping the predecessor makes the chain complete without
/// writing the same paragraphs twice: to read what a customer signed under on a given date, take
/// the earliest change for that kind after that date and read its <see cref="PreviousTexts"/> —
/// or the live row when there is none. Storing the result instead would look equivalent and
/// would silently lose the original wording, the one version that has no successor to carry it.
/// </para>
/// <para>
/// It is also the evidence for the yearly change allowance
/// (<c>Store.DocumentTextChangesPerYear</c>), which is why <see cref="ChangeYear"/> is stored as
/// an int beside the instant and indexed with the kind: the count is a plain indexed equality
/// per kind and year, never a range over UTC timestamps that would put a 31 December evening
/// save in the wrong year for Africa/Algiers.
/// </para>
/// </summary>
/// <summary>
/// One named rental-contract wording (<c>Store.ContractWordings</c>): what the contract form offers
/// by name. The wording itself is a document text like any other, kept under <see cref="Key"/>.
/// </summary>
public class ContractWording
{
    /// <summary><c>rentalContract.</c> and up to 16 lower-case letters or digits. Never changes once saved.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The name per language, <c>{ [lang]: name }</c>: "Échafaudage", "سقالات". At least one.</summary>
    public Dictionary<string, string> Names { get; set; } = new();
}

public class DocumentTextChange
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>One of <c>ParadiERP.Application.Contracts.DocumentTextKinds</c>. Each kind is counted on its own.</summary>
    [MaxLength(40)] public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// The calendar year the change falls in <b>in the company's time zone</b>, never UTC. The
    /// allowance the owner set himself is a yearly one, and the year has to be the one he would
    /// name looking at a calendar on the wall.
    /// </summary>
    public int ChangeYear { get; set; }

    public DateTime ChangedAtUtc { get; set; } = DateTime.UtcNow;

    [MaxLength(200)] public string? ChangedBy { get; set; }

    /// <summary>
    /// The per-language texts for this kind as they stood before the save, serialised
    /// <c>{ [lang]: text }</c>. An empty object is a real value and means the kind had no wording
    /// configured before — the first save has a predecessor too, and it is "nothing".
    /// </summary>
    public string PreviousTexts { get; set; } = "{}";
}
