using System.Data.Common;
using AlgeriaPos.Application.Security;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Auth;
using AlgeriaPos.Infrastructure.Authorization;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit.Abstractions;

namespace AlgeriaPos.Tests.Security;

/// <summary>
/// AUTH-1 Step 3 at the data layer: the cached decision is always the one Step 2 would have read from
/// the tables, a concurrent slow load can never bring an old decision back, two concurrent changes
/// never share a version, and what the cache costs per request in statements and memory.
/// </summary>
public sealed class AuthorizationCacheTests(ITestOutputHelper output) : IAsyncDisposable
{
    private const string Company = "essai";
    private readonly string file = Path.Combine(Path.GetTempPath(), $"authz-cache-{Guid.NewGuid():N}.db");
    private string ConnectionString => $"Data Source={file}";

    private PosDbContext Fresh(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(ConnectionString).AddInterceptors(interceptors).Options);

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(file); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    private async Task<List<User>> SeedAsync(int perRole)
    {
        await using var db = Fresh();
        await db.Database.EnsureCreatedAsync();
        await AuthorizationBootstrap.RunAsync(db);
        var users = new List<User>();
        foreach (var role in Enum.GetValues<UserRole>())
            for (var i = 0; i < perRole; i++)
                users.Add(new User { Username = $"{role}-{i}", FullName = $"{role} {i}", Role = role });
        db.Users.AddRange(users);
        var branch = new Branch();
        db.Branches.Add(branch);
        db.Devices.Add(new Device { Code = "C01", BranchId = branch.Id });
        await db.SaveChangesAsync();
        return users;
    }

    /// <summary>Step 2's resolution, word for word: four statements, read now, nothing kept.</summary>
    private static async Task<ServerAuthorizationContext?> Step2ResolveAsync(PosDbContext db, Guid userId)
    {
        var roleId = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.RoleId).FirstOrDefaultAsync();
        if (roleId is not Guid id) return null;
        var role = await db.Roles.AsNoTracking().Where(r => r.Id == id).Select(r => new { r.Scope, r.IsActive }).FirstOrDefaultAsync();
        if (role is null) return null;
        var fromRole = await db.RolePermissions.AsNoTracking().Where(p => p.RoleId == id).Select(p => p.PermissionKey).ToListAsync();
        var granted = await db.UserPermissions.AsNoTracking().Where(p => p.UserId == userId).Select(p => p.PermissionKey).ToListAsync();
        return new ServerAuthorizationContext(id, role.Scope, ServerAuthorization.Combine(fromRole, role.IsActive, granted));
    }

    /// <summary>A request as Program decides it: the session check's stamp, then the cache.</summary>
    private static async Task<ServerAuthorizationContext?> RequestAsync(AuthorizationCache cache, PosDbContext db, User user)
    {
        var session = await new UserSessionValidator(db).CheckAsync(user.Id, user.Version, user.Role.ToString(), "C01");
        Assert.NotNull(session);
        if (session.Authorization is not { } stamp) return null;
        return await cache.GetAsync(Company, user.Id, stamp, ct => ServerAuthorization.LoadAsync(db, user.Id, stamp, ct), default);
    }

    private static string Describe(ServerAuthorizationContext? c) =>
        c is null ? "none" : $"{c.RoleId} {c.Scope} [{string.Join(",", c.Permissions.Order(StringComparer.Ordinal))}]";

    [Fact]
    public async Task Through_four_hundred_random_changes_every_cached_decision_equals_what_step_2_reads_from_the_tables()
    {
        var users = await SeedAsync(perRole: 4);
        var cache = new AuthorizationCache(TimeProvider.System);
        var random = new Random(20261003);
        var custom = new List<Guid>();
        var keys = PermissionCatalog.All.Select(p => p.Key).ToList();
        var editable = new List<Guid> { SystemRoles.ManagerId, SystemRoles.CashierId };
        var mismatches = new List<string>();
        var log = new List<string>();

        for (var step = 0; step < 400; step++)
        {
            await using (var db = Fresh())
            {
                var key = keys[random.Next(keys.Count)];
                var user = users[random.Next(users.Count)];
                switch (random.Next(8))
                {
                    case 0 or 1: // a grant added or removed
                        var grant = await db.UserPermissions.FirstOrDefaultAsync(p => p.UserId == user.Id && p.PermissionKey == key);
                        if (grant is null) db.UserPermissions.Add(new UserPermission { UserId = user.Id, PermissionKey = key });
                        else db.UserPermissions.Remove(grant);
                        log.Add($"grant {key} {user.Username}");
                        break;
                    case 2 or 3: // a role permission added or removed (never the Admin's: it is the baseline)
                        var roles = editable.Concat(custom).ToList();
                        var roleId = roles[random.Next(roles.Count)];
                        var held = await db.RolePermissions.FirstOrDefaultAsync(p => p.RoleId == roleId && p.PermissionKey == key);
                        if (held is null) db.RolePermissions.Add(new RolePermission { RoleId = roleId, PermissionKey = key });
                        else db.RolePermissions.Remove(held);
                        log.Add($"role-permission {key} {roleId}");
                        break;
                    case 4: // a custom role created
                        var role = new Role { Name = $"Rôle {step}", Scope = (RoleScope)random.Next(3) };
                        db.Roles.Add(role);
                        db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionKey = key });
                        custom.Add(role.Id);
                        log.Add($"custom role {role.Scope}");
                        break;
                    case 5: // a user moved to another role (the last active Admin cannot leave)
                        var targets = SystemRoles.All.Select(d => d.Id).Concat(custom).ToList();
                        var row = await db.Users.SingleAsync(u => u.Id == user.Id);
                        row.RoleId = targets[random.Next(targets.Count)];
                        log.Add($"move {user.Username} -> {row.RoleId}");
                        break;
                    case 6 when custom.Count > 0: // a custom role's scope changed
                        var scopedId = custom[random.Next(custom.Count)];
                        var scoped = await db.Roles.SingleAsync(r => r.Id == scopedId);
                        scoped.Scope = (RoleScope)(((int)scoped.Scope + 1 + random.Next(2)) % 3);
                        log.Add($"scope {scoped.Id} {scoped.Scope}");
                        break;
                    case 7 when custom.Count > 0: // a custom role switched off or on
                        var toggledId = custom[random.Next(custom.Count)];
                        var toggled = await db.Roles.SingleAsync(r => r.Id == toggledId);
                        toggled.IsActive = !toggled.IsActive;
                        log.Add($"active {toggled.Id} {toggled.IsActive}");
                        break;
                }
                try { await db.SaveChangesAsync(); }
                catch (AuthorizationIntegrityException) { log.Add("  (refused)"); }
            }

            // Every user, every step, twice: the second is answered from the cache.
            await using var check = Fresh();
            foreach (var user in users)
            {
                var fresh = await check.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
                (user.Role, user.Version) = (fresh.Role, fresh.Version); // a moved legacy role means a new sign-in; the session rule is not under test here
                var expected = Describe(await Step2ResolveAsync(check, user.Id));
                for (var pass = 0; pass < 2; pass++)
                {
                    var actual = Describe(await RequestAsync(cache, check, user));
                    if (actual != expected) mismatches.Add($"step {step} ({log.LastOrDefault()}) {user.Username} pass {pass}: cached {actual}, tables {expected}");
                }
            }
        }

        var stats = cache.Statistics;
        output.WriteLine($"{log.Count} changes; {stats.Hits} hits, {stats.Misses} loads ({stats.HitRate:P1})");
        Assert.True(mismatches.Count == 0, $"{mismatches.Count} differences:\n" + string.Join("\n", mismatches.Take(20)));
        Assert.True(stats.Hits >= users.Count * 400, "the second pass of every step should be a hit");
    }

    [Fact]
    public async Task A_slow_load_that_finishes_after_a_change_never_brings_the_old_decision_back()
    {
        var cache = new AuthorizationCache(TimeProvider.System);
        var user = Guid.NewGuid();
        var before = new AuthorizationStamp(SystemRoles.ManagerId, 7, 3, RoleScope.AssignedBranch, true);
        var after = before with { RoleVersion = 8 };
        var withKey = new ServerAuthorizationContext(SystemRoles.ManagerId, RoleScope.AssignedBranch, new HashSet<string> { PermissionCatalog.AccountingView });
        var without = withKey with { Permissions = new HashSet<string>() };

        // Request 1 read the stamp before the change and is still loading when the change commits.
        var release = new TaskCompletionSource();
        var slow = cache.GetAsync(Company, user, before, async _ => { await release.Task; return withKey; }, default);
        // Request 2 reads the new stamp and loads the new decision.
        Assert.Same(without, await cache.GetAsync(Company, user, after, _ => Task.FromResult(without), default));
        // Request 1 finishes: it answers its own (concurrent) request, and files nothing over the newer entry.
        release.SetResult();
        Assert.Same(withKey, await slow);

        var loads = 0;
        var next = await cache.GetAsync(Company, user, after, _ => { loads++; return Task.FromResult(withKey); }, default);
        Assert.Same(without, next);
        Assert.Equal(0, loads);
        // And a request with the old stamp — impossible once the change committed — would still not be answered from it.
        Assert.Same(withKey, await cache.GetAsync(Company, user, before, _ => { loads++; return Task.FromResult(withKey); }, default));
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task Concurrent_requests_during_concurrent_changes_never_leave_a_removed_permission_usable()
    {
        var users = await SeedAsync(perRole: 3);
        var managers = users.Where(u => u.Role == UserRole.Manager).ToList();
        var cache = new AuthorizationCache(TimeProvider.System);
        using var stop = new CancellationTokenSource();

        // Readers hammer the cache while a writer adds and removes the key from the Manager role.
        var readers = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await using var db = Fresh();
                await RequestAsync(cache, db, managers[i % managers.Count]);
            }
        })).ToList();
        for (var round = 0; round < 25; round++)
        {
            await using var db = Fresh();
            var held = await db.RolePermissions.FirstOrDefaultAsync(p => p.RoleId == SystemRoles.ManagerId && p.PermissionKey == PermissionCatalog.PayrollManage);
            if (held is null) db.RolePermissions.Add(new RolePermission { RoleId = SystemRoles.ManagerId, PermissionKey = PermissionCatalog.PayrollManage });
            else db.RolePermissions.Remove(held);
            await db.SaveChangesAsync();

            // After each commit, no request — however the readers' loads interleaved — sees the old state.
            var expected = held is null;
            await using var check = Fresh();
            foreach (var manager in managers)
                Assert.Equal(expected, (await RequestAsync(cache, check, manager))!.Has(PermissionCatalog.PayrollManage));
        }
        stop.Cancel();
        await Task.WhenAll(readers);
        output.WriteLine($"concurrent run: {cache.Statistics}");
    }

    [Fact]
    public async Task Concurrent_saves_of_the_same_role_or_user_never_share_a_version()
    {
        var users = await SeedAsync(perRole: 1);
        var cashier = users.Single(u => u.Role == UserRole.Cashier);
        long RoleVersion() { using var db = Fresh(); return db.Roles.AsNoTracking().Single(r => r.Id == SystemRoles.CashierId).Version; }
        long UserVersion() { using var db = Fresh(); return db.Users.AsNoTracking().Single(u => u.Id == cashier.Id).AuthorizationVersion; }
        var (role0, user0) = (RoleVersion(), UserVersion());
        var keys = PermissionCatalog.All.Select(p => p.Key).Where(k => !SystemRoles.CashierSeed.Contains(k)).Take(12).ToList();

        // Each save adds one row; SQLite serialises them, and the bump is SET Version = Version + 1.
        await Task.WhenAll(keys.Select(key => Task.Run(async () =>
        {
            for (var attempt = 0; ; attempt++)
                try
                {
                    await using var db = Fresh();
                    db.RolePermissions.Add(new RolePermission { RoleId = SystemRoles.CashierId, PermissionKey = key });
                    db.UserPermissions.Add(new UserPermission { UserId = cashier.Id, PermissionKey = key });
                    await db.SaveChangesAsync();
                    return;
                }
                catch (DbUpdateException) when (attempt < 50) { await Task.Delay(10); } // SQLITE_BUSY: try again
                catch (SqliteException) when (attempt < 50) { await Task.Delay(10); }
        })));

        Assert.Equal(role0 + keys.Count, RoleVersion());
        Assert.Equal(user0 + keys.Count, UserVersion());
    }

    [Fact]
    public async Task Only_the_saves_that_change_authorization_bump_a_version_and_the_bump_commits_with_them()
    {
        var users = await SeedAsync(perRole: 1);
        var manager = users.Single(u => u.Role == UserRole.Manager);
        async Task<(long Role, long User)> VersionsAsync()
        {
            await using var db = Fresh();
            return ((await db.Roles.AsNoTracking().SingleAsync(r => r.Id == SystemRoles.ManagerId)).Version,
                (await db.Users.AsNoTracking().SingleAsync(u => u.Id == manager.Id)).AuthorizationVersion);
        }
        var start = await VersionsAsync();

        // A name, a password, a branch: nothing a decision is made of.
        await using (var db = Fresh())
        {
            var row = await db.Users.SingleAsync(u => u.Id == manager.Id);
            row.FullName = "Autre nom";
            row.PasswordHash = "x";
            await db.SaveChangesAsync();
        }
        Assert.Equal(start, await VersionsAsync());

        // A grant: the user's version only. A role permission: the role's only.
        await using (var db = Fresh())
        {
            db.UserPermissions.Add(new UserPermission { UserId = manager.Id, PermissionKey = PermissionCatalog.PayrollManage });
            await db.SaveChangesAsync();
        }
        Assert.Equal((start.Role, start.User + 1), await VersionsAsync());
        await using (var db = Fresh())
        {
            db.RolePermissions.Add(new RolePermission { RoleId = SystemRoles.ManagerId, PermissionKey = PermissionCatalog.HrAttendance });
            await db.SaveChangesAsync();
        }
        Assert.Equal((start.Role + 1, start.User + 1), await VersionsAsync());

        // Inside the caller's transaction, rolled back: neither the change nor the bump remains.
        await using (var db = Fresh())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            db.UserPermissions.Add(new UserPermission { UserId = manager.Id, PermissionKey = PermissionCatalog.AccountingManage });
            await db.SaveChangesAsync();
            await tx.RollbackAsync();
        }
        Assert.Equal((start.Role + 1, start.User + 1), await VersionsAsync());
        await using (var db = Fresh())
            Assert.False(await db.UserPermissions.AnyAsync(p => p.UserId == manager.Id && p.PermissionKey == PermissionCatalog.AccountingManage));
    }

    [Fact]
    public async Task A_hand_edit_that_bumps_no_version_is_bounded_by_the_maximum_age_and_idle_entries_are_let_go()
    {
        var clock = new ManualClock();
        var cache = new AuthorizationCache(clock);
        var stamp = new AuthorizationStamp(SystemRoles.CashierId, 1, 0, RoleScope.TillBranch, true);
        var loads = 0;
        Task<ServerAuthorizationContext> Load(CancellationToken _)
        {
            loads++;
            return Task.FromResult(new ServerAuthorizationContext(SystemRoles.CashierId, RoleScope.TillBranch, new HashSet<string>()));
        }

        await cache.GetAsync(Company, Guid.Empty, stamp, Load, default);
        clock.Advance(AuthorizationCache.MaxAge - TimeSpan.FromSeconds(1));
        await cache.GetAsync(Company, Guid.Empty, stamp, Load, default);
        Assert.Equal(1, loads);
        clock.Advance(TimeSpan.FromSeconds(1));
        await cache.GetAsync(Company, Guid.Empty, stamp, Load, default);
        Assert.Equal(2, loads);

        // Another user's request after a further max age sweeps the idle entry away.
        clock.Advance(AuthorizationCache.MaxAge);
        await cache.GetAsync(Company, Guid.NewGuid(), stamp, Load, default);
        Assert.Equal(1, cache.Statistics.Entries);

        // And companies never share an entry, whatever the user id.
        await cache.GetAsync("autre", Guid.Empty, stamp, Load, default);
        Assert.Equal(4, loads);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks = 1;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan by) => ticks += by.Ticks;
    }

    private sealed class StatementCounter : DbCommandInterceptor
    {
        public int Count;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Count);
            return base.ReaderExecutingAsync(command, eventData, result, ct);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Count);
            return base.ScalarExecutingAsync(command, eventData, result, ct);
        }
    }

    [Fact]
    public async Task A_request_costs_two_statements_when_nothing_moved_and_three_when_it_did_against_six_in_step_2()
    {
        var users = await SeedAsync(perRole: 1);
        var cache = new AuthorizationCache(TimeProvider.System);
        var counter = new StatementCounter();
        await using var db = Fresh(counter);

        async Task<int> CountAsync(Func<Task> run)
        {
            var before = counter.Count;
            await run();
            return counter.Count - before;
        }

        foreach (var user in users)
        {
            var step2 = await CountAsync(async () =>
            {
                Assert.True(await new UserSessionValidator(db).IsValidAsync(user.Id, user.Version, user.Role.ToString(), "C01"));
                await Step2ResolveAsync(db, user.Id);
            });
            var miss = await CountAsync(() => RequestAsync(cache, db, user));
            var hit = await CountAsync(() => RequestAsync(cache, db, user));
            output.WriteLine($"{user.Role}: step 2 {step2}, cold {miss}, cached {hit}");
            // Step 2 read the user row and the device for the session (IsValidAsync now reads the
            // stamp in that statement, so it is the same two), then four for the decision.
            Assert.Equal(2 + 4, step2);
            Assert.Equal(3, miss);
            Assert.Equal(2, hit);
        }
    }
}
