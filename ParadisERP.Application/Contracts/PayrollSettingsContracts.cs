using ParadisERP.Application.Payroll;

namespace ParadisERP.Application.Contracts;

/// <summary>
/// The payroll settings as the back-office screen reads them.
///
/// <para>
/// <see cref="Settings"/> is the engine's own <c>PayrollSettings</c> rather than a flattened
/// copy of it. A DTO per figure would have to be extended by hand every time a finance law adds
/// one — and the figure the screen forgot to carry is exactly the figure that then gets computed
/// from a default nobody chose.
/// </para>
/// <para>
/// <see cref="Owners"/> is <c>PayrollOwnership.Fields</c> serialised — field name to
/// <c>Operator</c> or <c>Company</c>. It is what lets the screen put the statutory figures in
/// their own group and say, in words, who may move them. It is not a permission check: the
/// server enforces regardless of what the screen believes.
/// </para>
/// <para>
/// Whether the figures have been confirmed, by whom and when is <b>not</b> repeated here: it is
/// <c>Settings.Confirmed</c>, <c>Settings.ConfirmedBy</c> and <c>Settings.ConfirmedAtUtc</c>, with
/// <c>Settings.NeedsReview</c> the same fact read the other way round. Two places for one fact is
/// two places that can disagree.
/// </para>
/// </summary>
/// <param name="CanEditCompany">Whether this session may save the company half.</param>
/// <param name="CanEditStatutory">Whether this session may save the statutory half and confirm it.</param>
/// <param name="UpdatedBy">Who last saved — which is not who confirmed.</param>
public record PayrollSettingsDto(
    PayrollSettings Settings,
    long Revision,
    IReadOnlyDictionary<string, string> Owners,
    bool CanEditCompany,
    bool CanEditStatutory,
    string? UpdatedBy = null,
    DateTime? UpdatedAtUtc = null);

/// <summary>
/// Saving the company's own half: its allowances, its overtime bands, its deductions, its
/// working day, its attendance default. A statutory figure that differs from the stored one is
/// refused by name rather than quietly dropped.
/// </summary>
public record UpdateCompanyPayrollSettingsRequest(PayrollSettings Settings, long Revision);

/// <summary>
/// Saving the statutory half. Only the fields <c>PayrollOwnership</c> marks operator-owned are
/// read from <see cref="Settings"/>; the company half is taken from the stored row whatever this
/// request contains.
/// </summary>
/// <param name="Reviewed">
/// "Confirmed by our accountant", ticked in the same save. Left false — the default — the save
/// goes through and the figures stay marked unconfirmed, which is what an admin correcting a
/// typo has actually done.
/// </param>
public record UpdateStatutoryPayrollSettingsRequest(
    PayrollSettings Settings, long Revision, bool Reviewed = false, string? ConfirmedBy = null);

/// <summary>
/// Confirming the figures without changing one: the accountant has read them and they are right.
/// </summary>
/// <param name="ConfirmedBy">
/// Who answered for them — the accountant's or the firm's name. Empty falls back to the signed-in
/// user, who is then the person on record.
/// </param>
public record ConfirmPayrollStatutoryRequest(long Revision, string? ConfirmedBy = null);
