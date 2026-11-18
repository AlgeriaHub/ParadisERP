using System.Security.Cryptography;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Auth;

/// <summary>
/// The Login ID an account signs in with (owner, 2026-10-05; label «رقم الدخول» / « Identifiant de
/// connexion » / "Login ID").
///
/// <para>
/// <b>Exactly eight digits, never a leading zero</b> (10000000–99999999: the owner wants no zeros
/// in front, and a spreadsheet that reads the number would drop them anyway). <b>Random</b>, drawn
/// from the cryptographic generator, so the next account's ID cannot be guessed from the last one,
/// and <b>derived from nothing</b> — not the name, the phone, the national ID, the branch or the
/// role. Drawn by the server when the account is saved, never when the employee is created: a
/// person may be on the payroll for years without needing to sign in.
/// </para>
/// <para>
/// <b>Unique company-wide.</b> A draw is refused if any account already signs in under it (a legacy
/// username made of digits counts) or if it equals an employee number or a fingerprint number, so
/// the three identifiers can never be read as one another. The unique index on
/// <c>Users.LoginId</c> and <c>Users.Username</c> is the backstop when two administrators save at
/// the same moment; the users endpoint draws again when it trips.
/// </para>
/// </summary>
public static class LoginIds
{
    public const int Length = 8;
    private const int Lowest = 10_000_000;
    private const int AboveHighest = 100_000_000;

    /// <summary>The cryptographic draw.</summary>
    public static int Random() => RandomNumberGenerator.GetInt32(Lowest, AboveHighest);

    /// <summary>
    /// The draw itself. Replaced by a test only, to force a collision; production never sets it.
    /// </summary>
    public static Func<int> Draw { get; set; } = Random;

    /// <summary>Whether a value has the Login ID's shape: eight digits, the first not 0.</summary>
    public static bool IsWellFormed(string? value) =>
        value is { Length: Length } && value[0] != '0' && value.All(char.IsAsciiDigit);

    /// <summary>A free Login ID, checked against every identifier already in use.</summary>
    public static async Task<string> NewAsync(PosDbContext db, IReadOnlyCollection<string>? alsoAvoid = null, CancellationToken ct = default)
    {
        // Ninety million values against a company's few hundred accounts: a second draw is rare and
        // a tenth one would mean the generator is broken, which is worth an error rather than a loop.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var candidate = Draw().ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!IsWellFormed(candidate)) continue;
            if (alsoAvoid is not null && alsoAvoid.Contains(candidate)) continue;
            if (await InUseAsync(db, candidate, ct)) continue;
            return candidate;
        }
        throw new InvalidOperationException("No free Login ID could be drawn.");
    }

    /// <summary>Whether anything already answers to this value: an account, an employee number or a fingerprint number.</summary>
    public static async Task<bool> InUseAsync(PosDbContext db, string value, CancellationToken ct = default) =>
        await db.Users.AsNoTracking().AnyAsync(u => u.Username == value || u.LoginId == value, ct)
        || await db.Employees.AsNoTracking().AnyAsync(e => e.Number == value || e.FingerprintNumber == value, ct);
}
