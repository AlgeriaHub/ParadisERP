namespace AlgeriaPos.Domain;

/// <summary>
/// What becomes of a user's legacy grants (<c>Users.Permissions</c>) under the new model
/// (REDESIGN_PLAN.md §17.5): kept as an additional permission where it opened something, dropped —
/// with the reason — where it provably never did.
///
/// <para>
/// "Provably": before AUTH-1 every route those five keys open also required the Manager or Admin
/// role, an Admin held four of them by role, and a Manager passed every cash-box route by role. So
/// the effect of a grant depends only on the holder's role, and this table is the whole rule. The
/// access-equivalence test checks it against the real route policies for every role and every
/// combination of the five grants.
/// </para>
/// </summary>
public static class LegacyGrants
{
    /// <summary>A Cashier holding a key whose every route also required Manager or Admin.</summary>
    public const string RoleGate = "role-gate";

    /// <summary>A Manager holding the cash box: they reached every cash-box route by role.</summary>
    public const string ManagerByRole = "manager-by-role";

    /// <summary>An Admin holding a key the Admin role already gave, or, for hr.attendance, one payroll.manage already covered.</summary>
    public const string AdminAlreadyHeld = "admin-already-held";

    /// <summary>A stored name that is not one of the old model's permissions, which the old model ignored.</summary>
    public const string Unknown = "unknown-permission";

    public sealed record Dropped(string Key, string Reason, string Detail);

    public sealed record Outcome(Guid RoleId, IReadOnlyList<string> Kept, IReadOnlyList<Dropped> Dropped);

    public static Outcome Map(UserRole role, string? stored)
    {
        var kept = new List<string>();
        var dropped = new List<Dropped>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in (stored ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!seen.Add(raw)) continue;
            var key = Permissions.All.FirstOrDefault(k => k.Equals(raw, StringComparison.OrdinalIgnoreCase));
            if (key is null)
            {
                dropped.Add(new(Trim(raw), Unknown, "Not a permission the old model knew; it opened nothing."));
                continue;
            }

            switch (role)
            {
                case UserRole.Admin:
                    dropped.Add(new(key, AdminAlreadyHeld, key == Permissions.EnterAttendance
                        ? "The attendance routes were already open to the Admin through payroll.manage."
                        : "The Admin role already held it."));
                    break;
                case UserRole.Manager when key == Permissions.CashBox:
                    dropped.Add(new(key, ManagerByRole,
                        "A Manager reached every cash-box route by role; the screens read it only for Cashiers."));
                    break;
                case UserRole.Manager:
                    kept.Add(key);
                    break;
                default:
                    if (key == Permissions.CashBox) kept.Add(key);
                    else dropped.Add(new(key, RoleGate, "Every route it opens also required the Manager or Admin role."));
                    break;
            }
        }

        return new Outcome(SystemRoles.IdFor(role), kept, dropped);
    }

    private static string Trim(string value) => value.Length > 60 ? value[..60] : value;
}
