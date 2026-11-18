using System.Text.Json;
using ParadisERP.Application.Payroll;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Payroll;

/// <summary>What a save did, or why it did nothing.</summary>
public enum PayrollSettingsOutcome
{
    Saved = 0,
    /// <summary>Somebody else saved first. The screen reloads and the admin decides again.</summary>
    Stale = 1,
    /// <summary>The edit moved a figure this caller does not own. <c>Message</c> names them.</summary>
    Refused = 2,
    /// <summary>The figures themselves are impossible — an overlapping bracket, a rate above 100.</summary>
    Invalid = 3
}

/// <param name="Settings">The settings as they now stand — on a refusal, as they still stand.</param>
/// <param name="UpdatedBy">Who last saved. Who <i>confirmed</i> is <c>Settings.ConfirmedBy</c>.</param>
public record PayrollSettingsResult(
    PayrollSettingsOutcome Outcome,
    PayrollSettings Settings,
    long Revision,
    string? Message = null,
    string? UpdatedBy = null,
    DateTime? UpdatedAtUtc = null)
{
    public bool Ok => Outcome == PayrollSettingsOutcome.Saved;
}

/// <summary>
/// The one reader and the one writer of the payroll figures.
///
/// <para>
/// Two halves, one row. <c>PayrollOwnership</c> says which figures are the company's own
/// practice and which come from law; this store is what makes that split a rule rather than a
/// label, by building the candidate settings itself and copying only the half the caller is
/// editing out of the request. Handing <c>PayrollOwnership.ApplyOperatorEdit</c> a whole request
/// would let a statutory save rewrite an allowance in the same call — it clones whatever it is
/// given — so the statutory path never passes the request through.
/// </para>
/// <para>
/// <b>Who may call which half is no longer configuration.</b> docs/07 §5.2 put the statutory
/// figures behind <c>Payroll:StatutoryEditors</c>, unreachable by any login a company admin
/// could create. The owner reversed that on 2026-09-24: the module ships with placeholder
/// figures and he edits them himself. The split survives as the thing the "not yet confirmed"
/// mark is computed from, and the gate is the named permission
/// <see cref="Permissions.EditPayrollStatutory"/>, checked at the endpoint.
/// </para>
/// </summary>
public sealed class PayrollSettingsStore(PosDbContext db, IRegionalSettingsProvider regional)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads the figures, falling back to the country's preset when nobody has saved yet.
    ///
    /// <para>
    /// The fallback is <b>not</b> written to the database here. A GET that creates a row means
    /// the first person to open the screen silently becomes the author of the settings, and it
    /// means two simultaneous readers race to create the same row. Revision 0 says "nothing
    /// saved yet" honestly, and the first save creates the row with the revision check already
    /// in place.
    /// </para>
    /// <para>
    /// A country with no preset gets empty settings that need review: an empty scale that
    /// nobody chose must never read as a decision to withhold nothing.
    /// </para>
    /// </summary>
    public async Task<PayrollSettingsResult> ReadAsync(CancellationToken ct = default)
    {
        var row = await db.PayrollConfigurations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == PayrollConfiguration.SingletonId, ct);

        if (row is null) return new PayrollSettingsResult(PayrollSettingsOutcome.Saved, Fresh(), 0);

        return new PayrollSettingsResult(
            PayrollSettingsOutcome.Saved,
            Deserialize(row.SettingsJson),
            row.Revision,
            UpdatedBy: row.UpdatedBy,
            UpdatedAtUtc: row.UpdatedAtUtc);
    }

    /// <summary>
    /// A company administrator's save. <c>PayrollOwnership.ApplyCompanyEdit</c> is the gate: it
    /// refuses a statutory difference by name rather than discarding it, because an admin who
    /// believes they lowered a contribution rate and did not would find out from a payslip.
    /// </summary>
    public Task<PayrollSettingsResult> ApplyCompanyEditAsync(
        PayrollSettings incoming, long revision, string? by, CancellationToken ct = default) =>
        SaveAsync(revision, by, ct, current => PayrollOwnership.ApplyCompanyEdit(current, incoming));

    /// <summary>
    /// A statutory save. The candidate is the stored settings with <b>only</b> the operator-owned
    /// fields taken from the request — driven by <c>PayrollOwnership.Fields</c> itself, so a
    /// figure a later finance law adds is covered the moment it is registered there rather than
    /// the moment somebody remembers this method.
    /// </summary>
    /// <param name="reviewed">
    /// "Confirmed by our accountant", ticked in the same save. False leaves the figures marked
    /// unconfirmed — and clears an earlier confirmation, because a figure that has just moved is
    /// not a figure anybody has checked.
    /// </param>
    public Task<PayrollSettingsResult> ApplyStatutoryEditAsync(
        PayrollSettings incoming, long revision, bool reviewed, string? confirmedBy, string? by,
        CancellationToken ct = default) =>
        SaveAsync(revision, by, ct, current => PayrollOwnership.ApplyOperatorEdit(
            CopyOperatorFields(current, incoming), reviewed, Named(confirmedBy, by), DateTime.UtcNow));

    /// <summary>
    /// "Our accountant has read these and they are right" — with no figure changed. It is a
    /// separate action from an edit on purpose: confirming is a statement about the figures
    /// that are already there, and asking the admin to re-save the whole form to make it would
    /// invite a typo into the one moment the figures are supposed to stop moving.
    /// </summary>
    public Task<PayrollSettingsResult> ConfirmStatutoryAsync(
        long revision, string? confirmedBy, string? by, CancellationToken ct = default) =>
        SaveAsync(revision, by, ct, current => PayrollOwnership.ApplyOperatorEdit(
            current, reviewed: true, Named(confirmedBy, by), DateTime.UtcNow));

    // ---------- the one write path ----------

    private async Task<PayrollSettingsResult> SaveAsync(
        long revision, string? by, CancellationToken ct, Func<PayrollSettings, PayrollSettings> apply)
    {
        // The route runs behind SubscriptionWriteFilter, which may already hold a transaction —
        // BeginWriteScopeAsync joins it rather than failing on a nested BEGIN.
        await using var scope = await db.BeginWriteScopeAsync(ct);

        var row = await db.PayrollConfigurations
            .SingleOrDefaultAsync(x => x.Id == PayrollConfiguration.SingletonId, ct);

        // Revision 0 is "nothing saved yet", which is what ReadAsync returned to the screen that
        // is now saving. A second admin who got there first has already bumped it.
        if ((row?.Revision ?? 0) != revision)
        {
            var stored = row is null ? Fresh() : Deserialize(row.SettingsJson);
            return new PayrollSettingsResult(PayrollSettingsOutcome.Stale, stored, row?.Revision ?? 0,
                "These settings were saved by somebody else while this screen was open.");
        }

        var current = row is null ? Fresh() : Deserialize(row.SettingsJson);
        PayrollSettings next;
        try
        {
            next = apply(current);
        }
        catch (UnauthorizedAccessException refusal)
        {
            return new PayrollSettingsResult(PayrollSettingsOutcome.Refused, current, revision, refusal.Message);
        }
        catch (ArgumentException invalid)
        {
            return new PayrollSettingsResult(PayrollSettingsOutcome.Invalid, current, revision, invalid.Message);
        }

        if (row is null)
        {
            row = new PayrollConfiguration();
            db.PayrollConfigurations.Add(row);
        }

        row.SettingsJson = JsonSerializer.Serialize(next, Json);
        row.Revision = revision + 1;
        row.UpdatedBy = Trim(by);
        row.UpdatedAtUtc = DateTime.UtcNow;

        // A plain SaveChanges: nothing here is a SyncEntity, so there is no version to reserve
        // and no terminal to notify.
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);

        return new PayrollSettingsResult(
            PayrollSettingsOutcome.Saved, next, row.Revision,
            UpdatedBy: row.UpdatedBy,
            UpdatedAtUtc: row.UpdatedAtUtc);
    }

    // ---------- helpers ----------

    /// <summary>
    /// The stored settings with every operator-owned property replaced by the request's. Done by
    /// reflection over <c>PayrollOwnership.Fields</c> rather than as a list of assignments: the
    /// list is what goes stale, and a statutory field this method forgot would be a figure the
    /// accountant edits on screen and never sees saved.
    /// <para>
    /// The confirmation fields are operator-owned too and are therefore copied here — and then
    /// overwritten by <c>ApplyOperatorEdit</c>, which is the only thing that decides them. A
    /// request cannot confirm the figures by sending <c>Confirmed: true</c>; it confirms them by
    /// asking to, which is a separate parameter the endpoint's permission guards.
    /// </para>
    /// </summary>
    private static PayrollSettings CopyOperatorFields(PayrollSettings current, PayrollSettings incoming)
    {
        var candidate = Clone(current);
        foreach (var (name, owner) in PayrollOwnership.Fields)
        {
            if (owner != PayrollFieldOwner.Operator) continue;
            var property = typeof(PayrollSettings).GetProperty(name);
            if (property is null || !property.CanWrite) continue;
            property.SetValue(candidate, property.GetValue(incoming));
        }
        return candidate;
    }

    /// <summary>
    /// A deep copy through the serialiser. <c>PayrollOwnership</c>'s own clone is private, and a
    /// hand-written one here would be a second place to forget a new field.
    /// </summary>
    private static PayrollSettings Clone(PayrollSettings settings) =>
        JsonSerializer.Deserialize<PayrollSettings>(JsonSerializer.Serialize(settings, Json), Json)!;

    private static PayrollSettings Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<PayrollSettings>(json, Json) ?? new PayrollSettings(); }
        catch (JsonException) { return new PayrollSettings(); }
    }

    /// <summary>
    /// The starting point for a company that has never saved: the country's preset, or — for a
    /// country nobody has written a preset for — an empty set that needs review rather than a
    /// silent set of zeroes.
    /// </summary>
    private PayrollSettings Fresh()
    {
        var country = regional.Get().CountryCode;
        return PayrollPresets.Create(country) ?? new PayrollSettings { CountryCode = country };
    }

    private static string? Named(string? confirmedBy, string? actor) => Trim(confirmedBy) ?? Trim(actor);

    private static string? Trim(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length <= 200 ? trimmed : trimmed[..200];
    }
}
