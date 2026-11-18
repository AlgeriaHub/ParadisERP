using System.Collections.Concurrent;
using ParadiERP.Application.Security;

namespace ParadiERP.Infrastructure.Authorization;

/// <summary>Counters for measurement and tests; not part of any decision.</summary>
public sealed record AuthorizationCacheStatistics(long Hits, long Misses, int Entries)
{
    public double HitRate => Hits + Misses == 0 ? 0 : (double)Hits / (Hits + Misses);
}

/// <summary>
/// The server's decision for a user, kept between their requests (AUTH-1 Step 3) — keyed by the
/// company and the user's id, the server's own identity of the caller, never by anything in the token.
///
/// <para>
/// <b>Versioned, not timed.</b> An entry is filed under the <see cref="AuthorizationStamp"/> it was
/// loaded at: the role, the role's version, the user's authorization version, the scope and whether
/// the role is active. Every request reads the current stamp from the database in the session check's
/// own statement, and an entry is used only when the two are equal — so a change is seen on the very
/// next request, a removed permission first of all. Every save that changes a role, its permissions,
/// a user's role or a user's grants bumps the version in the same transaction (<c>PosDbContext</c>).
/// </para>
/// <para>
/// <b>Never older than its stamp.</b> The stamp is read before the permissions, so an entry can only
/// hold what was true at its stamp or later. A slow load that finishes after a newer entry was filed
/// never replaces it, and whatever is filed, a request whose stamp differs loads again.
/// </para>
/// <para>
/// <see cref="MaxAge"/> is not what makes this correct. It only bounds a hand edit of the tables made
/// directly in the database — which bumps no version — and lets idle entries go.
/// </para>
/// </summary>
public sealed class AuthorizationCache(TimeProvider clock)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    private sealed record Entry(AuthorizationStamp Stamp, ServerAuthorizationContext Context, long LoadedAt);

    private readonly ConcurrentDictionary<(string Company, Guid User), Entry> entries = new();
    private long hits, misses, lastSweep;

    public AuthorizationCacheStatistics Statistics =>
        new(Interlocked.Read(ref hits), Interlocked.Read(ref misses), entries.Count);

    /// <summary>
    /// The decision at <paramref name="stamp"/>: the one held if it was filed under exactly that stamp,
    /// else what <paramref name="load"/> reads now.
    /// </summary>
    public async Task<ServerAuthorizationContext> GetAsync(
        string company, Guid userId, AuthorizationStamp stamp,
        Func<CancellationToken, Task<ServerAuthorizationContext>> load, CancellationToken ct)
    {
        var key = (company, userId);
        if (entries.TryGetValue(key, out var held) && held.Stamp == stamp && !Expired(held))
        {
            Interlocked.Increment(ref hits);
            return held.Context;
        }

        Interlocked.Increment(ref misses);
        var context = await load(ct);
        var loaded = new Entry(stamp, context, clock.GetTimestamp());
        entries.AddOrUpdate(key, loaded, (_, current) => Replaces(loaded, current) ? loaded : current);
        Sweep();
        return context;
    }

    private bool Expired(Entry entry) => clock.GetElapsedTime(entry.LoadedAt) >= MaxAge;

    /// <summary>
    /// Whether a just-loaded entry may take the place of the one held: when it is newer, or the held
    /// one has outlived <see cref="MaxAge"/>. Versions only rise, and a role change bumps the user's.
    /// </summary>
    private bool Replaces(Entry loaded, Entry current)
    {
        if (Expired(current)) return true;
        var (a, b) = (loaded.Stamp, current.Stamp);
        if (a.UserVersion != b.UserVersion) return a.UserVersion > b.UserVersion;
        if (a.RoleId != b.RoleId) return true;
        return a.RoleVersion >= b.RoleVersion;
    }

    /// <summary>At most once per <see cref="MaxAge"/>: drops the entries no request can use any more.</summary>
    private void Sweep()
    {
        var now = clock.GetTimestamp();
        var last = Interlocked.Read(ref lastSweep);
        if (last != 0 && clock.GetElapsedTime(last, now) < MaxAge) return;
        if (Interlocked.CompareExchange(ref lastSweep, now, last) != last) return;
        foreach (var (key, entry) in entries)
            if (Expired(entry)) entries.TryRemove(new KeyValuePair<(string, Guid), Entry>(key, entry));
    }
}
