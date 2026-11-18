using System.ComponentModel.DataAnnotations;

namespace ParadiERP.Domain;

/// <summary>
/// The one row the payroll settings live in — rates, the tax scale, the allowances a company
/// pays, the working day — held as the serialised
/// <c>ParadiERP.Application.Payroll.PayrollSettings</c>.
///
/// <para>
/// <b>Not a <see cref="SyncEntity"/>, and it never will be.</b> Nothing in payroll reaches a
/// till, and a contribution rate is no exception: a cashier's device has no reason to carry the
/// figures somebody's salary is computed from.
/// </para>
/// <para>
/// JSON rather than a column per figure, for the same reason the country profile is JSON: the
/// shape moves with each finance law and with each company's own list of allowances, and a
/// migration per allowance is not a design. The trade-off is that nothing here is queryable,
/// which is right — there is exactly one row and it is always read whole.
/// </para>
/// <para>
/// There is deliberately <b>no column for the accountant's confirmation</b>. Whether the
/// statutory figures have been checked, by whom and on what date is
/// <c>PayrollSettings.Confirmed</c> / <c>ConfirmedBy</c> / <c>ConfirmedAtUtc</c>, inside the
/// JSON, where the validator keeps the three in step and where a run's settings snapshot carries
/// it along. A column here would be the same fact in a second place, and the one that would win
/// is whichever the next writer forgot.
/// </para>
/// </summary>
public class PayrollConfiguration
{
    /// <summary>
    /// Always <see cref="SingletonId"/>. A fixed key rather than an identity column so "create
    /// it if it is missing" cannot race itself into two rows of settings.
    /// </summary>
    public Guid Id { get; set; } = SingletonId;

    /// <summary>The one row's key. There is one payroll configuration per company database.</summary>
    public static readonly Guid SingletonId = new("11111111-2222-3333-4444-555555555555");

    /// <summary>The serialised <c>PayrollSettings</c>.</summary>
    public string SettingsJson { get; set; } = "{}";

    /// <summary>
    /// Bumped on every accepted save; the screen sends back what it was given, and a second
    /// administrator saving over an open screen is refused rather than silently overwriting.
    /// </summary>
    public long Revision { get; set; }

    /// <summary>
    /// Who last saved, and when — which is not the same fact as who confirmed the figures. An
    /// admin correcting a typo has saved; he has not thereby checked anything against the
    /// finance law.
    /// </summary>
    [MaxLength(200)] public string? UpdatedBy { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
