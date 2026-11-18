namespace ParadisERP.Domain;

/// <summary>
/// Named permissions, for the few things a role cannot express.
///
/// <para>
/// The product has three roles — Cashier, Manager, Admin — and they carry almost everything.
/// What they cannot carry is a permission that some managers have and others do not, which is
/// exactly the shape of "may see what another branch is holding": a branch manager in Oran
/// legitimately needs it to arrange a transfer, and legitimately may not be the person the
/// owner wants browsing every other branch's position.
/// </para>
/// <para>
/// So this stays deliberately small. A named permission is added only when a role genuinely
/// cannot express the rule, never as a second way of saying "manager".
/// </para>
/// </summary>
public static class Permissions
{
    /// <summary>The JWT claim each granted permission is issued as.</summary>
    public const string ClaimType = "perm";

    /// <summary>
    /// Look up a product's <b>available quantity</b> at the other branches — and nothing else of
    /// theirs: not the other stock states, not a cost, a value, a movement, a document or a sale
    /// (the owner, 2026-09-30: "one of the employees may inquire about a product's availability at
    /// another branch … if he has been given the permission"). Read-only; it reserves nothing.
    /// Without it a non-admin sees their own branch's stock only.
    /// </summary>
    public const string ViewOtherBranchStock = "stock.view_other_branches";

    /// <summary>
    /// Run payroll: employees, runs, advances, wage payments and the payroll settings screen.
    /// A manager who orders stock has no business reading what every colleague earns, so this
    /// is a grant rather than a consequence of the role.
    /// </summary>
    public const string ManagePayroll = "payroll.manage";

    /// <summary>
    /// Enter the attendance sheet, and nothing else. The foreman who counts the crew each
    /// morning needs to submit days worked; he must not see a wage. This is the only reason
    /// the attendance routes are a policy of their own rather than part of
    /// <see cref="ManagePayroll"/>.
    /// </summary>
    public const string EnterAttendance = "hr.attendance";

    /// <summary>
    /// Edit the statutory payroll figures — contribution rates, the income-tax scale, the
    /// minimum wage — and confirm them as checked against the current law.
    ///
    /// <para>
    /// <b>This is grantable, and that reverses what docs/07 §5.2 planned.</b> The plan had it
    /// held by configuration alone (<c>Payroll:StatutoryEditors</c>), never writable through the
    /// users screen, so that no login a company Admin can create could reach a rate set by law.
    /// The owner decided otherwise on 2026-09-24: the module ships with <b>placeholder</b>
    /// figures and he edits them himself from the back office, because waiting on an accountant
    /// would mean no payroll module at all. So it is an ordinary grant, auto-held by an Admin
    /// like <see cref="ViewOtherBranchStock"/>, and giving it to the accountant's own login is a
    /// tick on the users screen.
    /// </para>
    /// <para>
    /// What the reversal did <b>not</b> change is that no figure in this system is written from
    /// general knowledge. The split in <c>PayrollOwnership</c> stays exactly as it was — it is
    /// now the record of which figures are statutory, and therefore of what the "not yet
    /// confirmed by an accountant" mark is computed from, rather than a wall. The protection is
    /// that everything computed against unconfirmed figures says so, on screen and on paper.
    /// </para>
    /// </summary>
    public const string EditPayrollStatutory = "payroll.edit_statutory";

    /// <summary>
    /// Hold the branch's cash box: take money in on a receipt voucher and pay it out on a payment
    /// voucher (owner, 2026-10-02, answer 25: "the employee who has the cash-box permission").
    /// A manager and an admin hold it by their role. Approving a payment voucher stays the
    /// manager's: the person who pays is not the person who decides that it is owed.
    /// </summary>
    public const string CashBox = "treasury.cash_box";

    public static readonly string[] All =
        [ViewOtherBranchStock, ManagePayroll, EnterAttendance, EditPayrollStatutory, CashBox];

    /// <summary>
    /// What an administrator holds without anybody granting it. An admin already administers
    /// every branch and every setting in the back office, so withholding one of these would be
    /// theatre rather than a control — and, for the statutory figures, would lock the one person
    /// the owner's decision puts in charge of them out of his own control panel.
    /// <para>
    /// <see cref="EnterAttendance"/> is deliberately absent: an admin reaches every attendance
    /// route through <see cref="ManagePayroll"/> already, and leaving it out keeps it meaning
    /// the one thing it exists to mean — a foreman who submits days and sees no wages.
    /// </para>
    /// </summary>
    private static readonly string[] AdminHolds = [ViewOtherBranchStock, ManagePayroll, EditPayrollStatutory, CashBox];

    /// <summary>
    /// <b>Legacy reference only (AUTH-1 Step 2).</b> No production code reads this any more: access is
    /// decided from the role tables per request (<c>ServerAuthorization</c>). It is kept as the
    /// written-down rule of the model that was replaced — the rule the frozen legacy snapshot and the
    /// rollback build follow — until the rollback window closes.
    /// <para>
    /// What a user held under that model: their explicit grants, plus everything an administrator had
    /// by virtue of the role.
    /// </summary>
    /// </para>
    public static IEnumerable<string> For(User user)
    {
        var granted = Parse(user.Permissions).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (user.Role == UserRole.Admin) foreach (var permission in AdminHolds) granted.Add(permission);
        return granted.Where(p => All.Contains(p, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reads the stored list, ignoring anything that is not a known permission and returning
    /// each one in its canonical spelling.
    /// <para>
    /// The spelling matters because these become JWT claim values, and an authorization policy
    /// compares a claim value <b>case-sensitively</b> (<c>RequireClaim</c> uses an ordinal
    /// comparer). A row written as <c>STOCK.VIEW_OTHER_BRANCHES</c> — by a migration, by hand,
    /// or by a future write path that forgets <see cref="Store"/> — would otherwise mint a
    /// claim that reads as granted everywhere except where it is actually checked. That fails
    /// closed rather than open, which is the right direction, but silently.
    /// </para>
    /// </summary>
    public static IEnumerable<string> Parse(string? stored) =>
        (stored ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => All.FirstOrDefault(known => known.Equals(p, StringComparison.OrdinalIgnoreCase)))
            .Where(p => p is not null)
            .Select(p => p!)
            .Distinct(StringComparer.Ordinal);

    /// <summary>Normalises a set of grants for storage, dropping unknown names.</summary>
    public static string Store(IEnumerable<string>? permissions) =>
        string.Join(',', (permissions ?? [])
            .Select(p => All.FirstOrDefault(known => known.Equals(p?.Trim(), StringComparison.OrdinalIgnoreCase)))
            .Where(p => p is not null)
            .Distinct());
}
