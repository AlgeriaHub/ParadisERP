using System.Text;

namespace ParadiERP.Infrastructure.Auth;

/// <summary>
/// How many wrong passwords one account may be offered before the server stops answering for a
/// minute.
///
/// <para>
/// Two things make an unthrottled login worse here than the usual. The back-office minimum is
/// six characters, which is inside reach of a sustained online run; and every attempt costs a
/// BCrypt verification, so a few hundred concurrent guesses pin every core the shop has — and
/// a server too busy to answer is a server the tills cannot sync against and the back office
/// cannot use. Refusing the attempt, rather than delaying it, is what protects both: a refused
/// attempt hashes nothing.
/// </para>
/// <para>
/// Counted twice, per company (security review, 2026-10-10):
/// </para>
/// <list type="bullet">
/// <item><b>Per account and address</b> — <see cref="MaxFailures"/> wrong passwords from one place
/// start the cooldown for that place only, so someone guessing at the administrator from outside
/// cannot lock the administrator out of the office.</item>
/// <item><b>Per account from anywhere</b> — <see cref="MaxAccountFailures"/>, a higher ceiling, so the
/// same guessing spread over many addresses is still capped.</item>
/// </list>
/// <para>
/// The attempt is <b>reserved before</b> the password is checked and given back when it was right,
/// all under one lock: two hundred guesses fired at the same instant are two hundred reservations,
/// not two hundred reads of a count that has not moved yet. The username is normalised the way the
/// database compares it (Unicode compatibility form, case and surrounding space ignored), so
/// "ａｄｍｉｎ" in full-width letters is the same account here as it is to SQL Server.
/// </para>
/// <para>
/// A flood of requests that are not one account's logins — invented usernames, other routes — is
/// the rate limiter's (RequestLimits, per address); this class is about one account's password.
/// </para>
/// </summary>
public sealed class LoginThrottle(TimeProvider time)
{
    /// <summary>Failures from one address inside <see cref="Window"/> before that address waits.</summary>
    public const int MaxFailures = 10;
    /// <summary>Failures from every address together before the account waits everywhere.</summary>
    public const int MaxAccountFailures = 50;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(1);

    /// <summary>A ceiling on what a stream of invented usernames can make this hold.</summary>
    private const int MaxTracked = 20_000;

    private sealed class Attempts
    {
        public int Failures;
        public DateTimeOffset LastFailureUtc;
    }

    private readonly Dictionary<string, Attempts> attempts = new(StringComparer.Ordinal);
    private readonly object gate = new();

    /// <summary>
    /// Reserves one attempt for this account from this address, or says how long to wait. A
    /// reserved attempt counts as a failure until <see cref="Succeeded"/> gives it back.
    /// </summary>
    public TimeSpan? TryBegin(string companyId, string? username, string? address)
    {
        var account = AccountKey(companyId, username);
        var place = PlaceKey(account, address);
        var now = time.GetUtcNow();
        lock (gate)
        {
            var wait = Wait(place, MaxFailures, now) ?? Wait(account, MaxAccountFailures, now);
            if (wait is not null) return wait;
            Count(place, now);
            Count(account, now);
            if (attempts.Count > MaxTracked) Prune(now);
            return null;
        }
    }

    /// <summary>The right password: this address's history goes, and the account's reservation is given back.</summary>
    public void Succeeded(string companyId, string? username, string? address = null)
    {
        var account = AccountKey(companyId, username);
        lock (gate)
        {
            attempts.Remove(PlaceKey(account, address));
            if (attempts.TryGetValue(account, out var state))
            {
                state.Failures = Math.Max(0, state.Failures - 1);
                if (state.Failures == 0) attempts.Remove(account);
            }
        }
    }

    /// <summary>How long this account must wait from this address, or null when it may try now. Reserves nothing.</summary>
    public TimeSpan? RetryAfter(string companyId, string? username, string? address = null)
    {
        var account = AccountKey(companyId, username);
        var now = time.GetUtcNow();
        lock (gate)
            return Wait(PlaceKey(account, address), MaxFailures, now) ?? Wait(account, MaxAccountFailures, now);
    }

    /// <summary>Records one wrong password without reserving first — the shape the older callers and the tests use.</summary>
    public void Failed(string companyId, string? username, string? address = null)
    {
        var account = AccountKey(companyId, username);
        var now = time.GetUtcNow();
        lock (gate)
        {
            Count(PlaceKey(account, address), now);
            Count(account, now);
            if (attempts.Count > MaxTracked) Prune(now);
        }
    }

    private TimeSpan? Wait(string key, int max, DateTimeOffset now)
    {
        if (!attempts.TryGetValue(key, out var state)) return null;
        if (state.Failures < max || now - state.LastFailureUtc > Window) return null;
        var until = state.LastFailureUtc + Cooldown;
        return until > now ? until - now : null;
    }

    private void Count(string key, DateTimeOffset now)
    {
        if (!attempts.TryGetValue(key, out var state))
        {
            attempts[key] = new Attempts { Failures = 1, LastFailureUtc = now };
            return;
        }
        // A quiet quarter of an hour is the account's own evidence that the run has stopped, so
        // the count starts again rather than compounding for ever.
        state.Failures = now - state.LastFailureUtc > Window ? 1 : state.Failures + 1;
        state.LastFailureUtc = now;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, state) in attempts.ToList())
            if (now - state.LastFailureUtc > Window)
                attempts.Remove(key);
        // Still full of fresh entries: the oldest go, so memory stays bounded whatever arrives.
        if (attempts.Count > MaxTracked)
            foreach (var key in attempts.OrderBy(x => x.Value.LastFailureUtc).Take(attempts.Count - MaxTracked).Select(x => x.Key).ToList())
                attempts.Remove(key);
    }

    /// <summary>The account as the database compares it: compatibility form, case and outer space ignored, bounded in length.</summary>
    public static string Normalize(string? username)
    {
        var text = (username ?? "").Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant();
        return text.Length > 128 ? text[..128] : text;
    }

    private static string AccountKey(string companyId, string? username) => companyId + "\n" + Normalize(username);
    private static string PlaceKey(string account, string? address) => account + "\n@" + (address ?? "");
}
