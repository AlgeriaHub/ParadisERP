using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ParadisERP.Infrastructure.Authorization;

/// <summary>A save that would break a rule of the roles model. Refused before anything is written.</summary>
public sealed class AuthorizationIntegrityException(string message) : InvalidOperationException(message);

/// <summary>The roles and users a save changed the authorization of, whose versions it bumps.</summary>
internal sealed record AuthorizationChanges(IReadOnlyCollection<Guid> Roles, IReadOnlyCollection<Guid> Users)
{
    public static readonly AuthorizationChanges None = new([], []);
    public bool IsEmpty => Roles.Count == 0 && Users.Count == 0;
}

/// <summary>
/// Bumps the authorization versions a save changed (AUTH-1 Step 3), as one
/// <c>SET Version = Version + 1</c> per table: the database serialises concurrent writers, so two
/// saves can never leave the same number behind (a read-modify-write here could, and a cached
/// decision would then outlive the second change). Runs inside the save's transaction, so a change
/// and its bump are seen together or not at all.
/// </summary>
internal static class AuthorizationVersions
{
    public static async Task BumpAsync(PosDbContext db, AuthorizationChanges changes, CancellationToken ct)
    {
        var roles = changes.Roles.ToList();
        var users = changes.Users.ToList();
        if (roles.Count > 0)
            await db.Roles.Where(r => roles.Contains(r.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Version, r => r.Version + 1), ct);
        if (users.Count > 0)
            await db.Users.Where(u => users.Contains(u.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.AuthorizationVersion, u => u.AuthorizationVersion + 1), ct);
        await RefreshTrackedAsync(db, roles, users, ct);
    }

    public static void Bump(PosDbContext db, AuthorizationChanges changes) =>
        BumpAsync(db, changes, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Keeps tracked copies in step, so a later save of the same context cannot write an old number back.</summary>
    private static async Task RefreshTrackedAsync(PosDbContext db, List<Guid> roles, List<Guid> users, CancellationToken ct)
    {
        var trackedRoles = db.ChangeTracker.Entries<Role>().Where(e => roles.Contains(e.Entity.Id)).ToList();
        if (trackedRoles.Count > 0)
        {
            var now = await db.Roles.AsNoTracking().Where(r => roles.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Version, ct);
            foreach (var entry in trackedRoles.Where(e => now.ContainsKey(e.Entity.Id)))
                Settle(entry.Property(r => r.Version), now[entry.Entity.Id]);
        }
        var trackedUsers = db.ChangeTracker.Entries<User>().Where(e => users.Contains(e.Entity.Id)).ToList();
        if (trackedUsers.Count > 0)
        {
            var now = await db.Users.AsNoTracking().Where(u => users.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.AuthorizationVersion, ct);
            foreach (var entry in trackedUsers.Where(e => now.ContainsKey(e.Entity.Id)))
                Settle(entry.Property(u => u.AuthorizationVersion), now[entry.Entity.Id]);
        }
    }

    private static void Settle<TEntity>(PropertyEntry<TEntity, long> property, long value) where TEntity : class
    {
        property.CurrentValue = value;
        property.OriginalValue = value;
        property.IsModified = false;
    }
}

/// <summary>
/// Runs inside every <see cref="PosDbContext"/> save, before anything is written (AUTH-1).
///
/// <para>
/// <b>Since Step 2 the role tables decide access, and the legacy columns never write into them.</b>
/// <c>Users.Role</c> is only a projection of the user's role (its scope, for rollback and older
/// tills): a save that moves <c>RoleId</c> rewrites it, and a save that changes <c>Users.Role</c>
/// without <c>RoleId</c> is refused. <c>Users.Permissions</c> is never read back into
/// <c>UserPermissions</c>. The one legacy-shaped path left is a brand-new user saved with no role at
/// all — a seeder's — which is placed once by the migration rule (<see cref="LegacyGrants"/>);
/// there is nothing yet to overwrite.
/// </para>
/// <para>
/// <b>The guards</b>, so no code path can bypass them:
/// <list type="bullet">
/// <item>the last active Admin — a user on the Admin role, active and not deleted — cannot be moved
/// off it, deactivated or deleted; inactive and deleted Admins never count;</item>
/// <item>a system role cannot be deleted, nor its key, scope or system mark changed, and the Admin
/// role cannot be deactivated;</item>
/// <item>the Admin role's permissions are written only from <see cref="AdminBaseline"/>;</item>
/// <item>only catalogue keys are stored, for a role or for a user.</item>
/// </list>
/// </para>
/// </summary>
internal static class AuthorizationShadow
{
    /// <returns>The roles and users whose authorization version the save must bump (AUTH-1 Step 3).</returns>
    public static async Task<AuthorizationChanges> ApplyAsync(PosDbContext db, CancellationToken ct)
    {
        var tracker = db.ChangeTracker;
        var users = tracker.Entries<User>().Where(e => e.State != EntityState.Unchanged).ToList();
        var roles = tracker.Entries<Role>().Where(e => e.State != EntityState.Unchanged).ToList();
        var rolePermissions = tracker.Entries<RolePermission>().Where(e => e.State != EntityState.Unchanged).ToList();
        if (users.Count == 0 && roles.Count == 0 && rolePermissions.Count == 0
            && !tracker.Entries<UserPermission>().Any(e => e.State != EntityState.Unchanged))
            return AuthorizationChanges.None;

        GuardRoles(roles);

        var seeded = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var unplaced = users.Where(e => e.State == EntityState.Added && e.Entity.RoleId is null).ToList();
        if (unplaced.Count > 0 || users.Any(e => e.State == EntityState.Added))
            foreach (var added in await SystemRoleStore.EnsureAsync(db, ct)) seeded.Add(added);
        foreach (var entry in unplaced)
            await MigrateAsync(db, entry.Entity, "first-save", ct);
        NormalizeNames(db);

        await ProjectLegacyRolesAsync(db, users, ct);
        await GuardLastAdminAsync(db, users, ct);
        GuardAdminPermissions(db, seeded);
        GuardKeys(db);
        return ChangesOf(db);
    }

    /// <summary>
    /// What moves a cached decision: a role's row (scope, active state, anything) or its permissions,
    /// a user's role, a user's grants. A row added in this save is cached nowhere yet.
    /// </summary>
    private static AuthorizationChanges ChangesOf(PosDbContext db)
    {
        var tracker = db.ChangeTracker;
        var roles = new HashSet<Guid>();
        var users = new HashSet<Guid>();
        foreach (var entry in tracker.Entries<Role>().Where(e => e.State == EntityState.Modified))
            roles.Add(entry.Entity.Id);
        foreach (var entry in tracker.Entries<RolePermission>().Where(e => e.State is EntityState.Added or EntityState.Deleted or EntityState.Modified))
        {
            roles.Add(entry.Entity.RoleId);
            if (entry.State == EntityState.Modified) roles.Add(entry.OriginalValues.GetValue<Guid>(nameof(RolePermission.RoleId)));
        }
        foreach (var entry in tracker.Entries<UserPermission>().Where(e => e.State is EntityState.Added or EntityState.Deleted or EntityState.Modified))
        {
            users.Add(entry.Entity.UserId);
            if (entry.State == EntityState.Modified) users.Add(entry.OriginalValues.GetValue<Guid>(nameof(UserPermission.UserId)));
        }
        foreach (var entry in tracker.Entries<User>().Where(e => e.State == EntityState.Modified && e.Property(u => u.RoleId).IsModified))
            users.Add(entry.Entity.Id);

        roles.ExceptWith(tracker.Entries<Role>().Where(e => e.State == EntityState.Added).Select(e => e.Entity.Id));
        users.ExceptWith(tracker.Entries<User>().Where(e => e.State == EntityState.Added).Select(e => e.Entity.Id));
        return roles.Count == 0 && users.Count == 0 ? AuthorizationChanges.None : new AuthorizationChanges(roles, users);
    }

    /// <summary>
    /// The migration rule, for a user not yet on a role (the upgrade, or a brand-new user saved with
    /// none): their system role and the legacy grants that give them something.
    /// </summary>
    internal static async Task<LegacyGrants.Outcome> MigrateAsync(PosDbContext db, User user, string source, CancellationToken ct)
    {
        var outcome = LegacyGrants.Map(user.Role, user.Permissions);
        user.RoleId = outcome.RoleId;
        await WriteGrantsAsync(db, user, outcome, source, "legacy", ct);
        return outcome;
    }

    /// <summary>
    /// Sets the user's additional grants <b>among the five legacy keys</b> to exactly <paramref name="outcome"/>'s,
    /// and reports what it drops. A grant outside them — a manufacturing permission, given on its own screen
    /// (ManufacturingAccess) — is not this writer's to remove: the users screen neither shows nor sends it.
    /// </summary>
    internal static async Task WriteGrantsAsync(
        PosDbContext db, User user, LegacyGrants.Outcome outcome, string source, string? grantedBy, CancellationToken ct)
    {
        var wanted = outcome.Kept.ToHashSet(StringComparer.Ordinal);
        var isNew = db.Entry(user).State == EntityState.Added;
        var tracked = db.ChangeTracker.Entries<UserPermission>().Where(e => e.Entity.UserId == user.Id && e.State == EntityState.Added)
            .Select(e => e.Entity);
        var stored = isNew ? [] : await db.UserPermissions.Where(p => p.UserId == user.Id).ToListAsync(ct);
        var current = stored.Concat(tracked).Distinct(ReferenceEqualityComparer.Instance).Cast<UserPermission>()
            .Where(g => Domain.Permissions.All.Contains(g.PermissionKey, StringComparer.Ordinal))
            .ToList();

        foreach (var grant in current.Where(g => !wanted.Contains(g.PermissionKey)))
            db.UserPermissions.Remove(grant);
        foreach (var key in wanted.Where(k => current.All(g => g.PermissionKey != k)))
            db.UserPermissions.Add(new UserPermission { UserId = user.Id, PermissionKey = key, GrantedBy = grantedBy });

        foreach (var drop in outcome.Dropped)
        {
            var already = db.ChangeTracker.Entries<AuthorizationReportEntry>().Any(e =>
                    e.Entity.UserId == user.Id && e.Entity.PermissionKey == drop.Key && e.Entity.Reason == drop.Reason)
                || (!isNew && await db.AuthorizationReport.AnyAsync(r =>
                    r.UserId == user.Id && r.PermissionKey == drop.Key && r.Reason == drop.Reason, ct));
            if (already) continue;
            db.AuthorizationReport.Add(new AuthorizationReportEntry
            {
                UserId = user.Id, Username = user.Username, LegacyRole = user.Role.ToString(),
                PermissionKey = drop.Key, Reason = drop.Reason, Detail = drop.Detail, Source = source,
            });
        }
    }

    /// <summary>
    /// The legacy role follows the role: rewritten when <c>RoleId</c> moves, and a change to the legacy
    /// column alone is refused — it would be a legacy field trying to say what the user may do.
    /// </summary>
    private static async Task ProjectLegacyRolesAsync(PosDbContext db, List<EntityEntry<User>> users, CancellationToken ct)
    {
        foreach (var entry in users.Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            var user = entry.Entity;
            if (user.RoleId is not Guid roleId)
                throw new AuthorizationIntegrityException($"User '{user.Username}' has no role.");
            var projected = ServerAuthorization.LegacyRoleFor(await ScopeOfAsync(db, roleId, ct));

            var roleMoved = entry.State == EntityState.Added || entry.Property(u => u.RoleId).IsModified;
            if (roleMoved)
            {
                user.Role = projected;
                continue;
            }
            // Only a write to the legacy column is refused. A column left behind by a change of the
            // role's scope (Step 3 reads the scope from the role) is rollback data, not a write.
            if (entry.Property(u => u.Role).IsModified && user.Role != projected)
                throw new AuthorizationIntegrityException(
                    $"User '{user.Username}': the legacy role cannot be changed on its own. Assign the role (RoleId); the legacy column follows it.");
        }
    }

    private static async Task<RoleScope> ScopeOfAsync(PosDbContext db, Guid roleId, CancellationToken ct)
    {
        if (SystemRoles.All.FirstOrDefault(d => d.Id == roleId) is { } system) return system.Scope;
        var tracked = db.ChangeTracker.Entries<Role>().FirstOrDefault(e => e.Entity.Id == roleId)?.Entity;
        if (tracked is not null) return tracked.Scope;
        var scope = await db.Roles.AsNoTracking().Where(r => r.Id == roleId).Select(r => (RoleScope?)r.Scope).FirstOrDefaultAsync(ct);
        return scope ?? throw new AuthorizationIntegrityException("That role does not exist.");
    }

    /// <summary>
    /// Every role written carries its name as the unique index compares it, whatever wrote it (a route, a
    /// seed, the bootstrap), so the index decides "same name" alike on SQLite and SQL Server.
    /// </summary>
    private static void NormalizeNames(PosDbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries<Role>().Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            var normalized = RoleNames.Normalize(entry.Entity.Name);
            if (entry.Entity.NormalizedName != normalized) entry.Entity.NormalizedName = normalized;
        }
    }

    // ---------- guards ----------

    private static bool ActiveAdmin(Guid? roleId, bool isActive, bool isDeleted) =>
        roleId == SystemRoles.AdminId && isActive && !isDeleted;

    private static async Task GuardLastAdminAsync(PosDbContext db, List<EntityEntry<User>> users, CancellationToken ct)
    {
        var leaving = users.Where(e => e.State is EntityState.Modified or EntityState.Deleted)
            .Where(e => ActiveAdmin(
                e.OriginalValues.GetValue<Guid?>(nameof(User.RoleId)),
                e.OriginalValues.GetValue<bool>(nameof(User.IsActive)),
                e.OriginalValues.GetValue<bool>(nameof(User.IsDeleted))))
            .Where(e => e.State == EntityState.Deleted || !ActiveAdmin(e.Entity.RoleId, e.Entity.IsActive, e.Entity.IsDeleted))
            .ToList();
        if (leaving.Count == 0) return;

        var changing = users.Select(e => e.Entity.Id).ToList();
        var untouched = await db.Users.AsNoTracking().CountAsync(u =>
            u.RoleId == SystemRoles.AdminId && u.IsActive && !u.IsDeleted && !changing.Contains(u.Id), ct);
        var stayingOrNew = users.Count(e => e.State != EntityState.Deleted && ActiveAdmin(e.Entity.RoleId, e.Entity.IsActive, e.Entity.IsDeleted));
        if (untouched + stayingOrNew == 0)
            throw new AuthorizationIntegrityException("At least one active administrator must remain.");
    }

    private static void GuardRoles(List<EntityEntry<Role>> roles)
    {
        foreach (var entry in roles)
        {
            var wasSystem = entry.State != EntityState.Added && entry.OriginalValues.GetValue<bool>(nameof(Role.IsSystem));
            if (entry.State == EntityState.Deleted && wasSystem)
                throw new AuthorizationIntegrityException($"The system role '{entry.Entity.Name}' cannot be deleted.");
            if (entry.State != EntityState.Modified || !wasSystem) continue;

            if (!entry.Entity.IsSystem
                || entry.Property(r => r.Key).IsModified && entry.OriginalValues.GetValue<string?>(nameof(Role.Key)) != entry.Entity.Key
                || entry.Property(r => r.Scope).IsModified && entry.OriginalValues.GetValue<RoleScope>(nameof(Role.Scope)) != entry.Entity.Scope)
                throw new AuthorizationIntegrityException($"The system role '{entry.Entity.Name}' keeps its key, its scope and its system mark.");
            if (entry.Entity.Id == SystemRoles.AdminId && !entry.Entity.IsActive)
                throw new AuthorizationIntegrityException("The Admin role cannot be deactivated.");
        }
    }

    private static void GuardAdminPermissions(PosDbContext db, HashSet<object> seeded)
    {
        if (db.AdminBaselineWriteAllowed) return;
        var touched = db.ChangeTracker.Entries<RolePermission>()
            .Where(e => e.State is EntityState.Added or EntityState.Deleted or EntityState.Modified)
            .Where(e => e.Entity.RoleId == SystemRoles.AdminId || (e.State == EntityState.Modified
                && e.OriginalValues.GetValue<Guid>(nameof(RolePermission.RoleId)) == SystemRoles.AdminId))
            .FirstOrDefault(e => !seeded.Contains(e.Entity));
        if (touched is not null)
            throw new AuthorizationIntegrityException(
                "The Admin role's permissions are the Admin baseline and cannot be edited (AdminBaseline.cs).");
    }

    private static void GuardKeys(PosDbContext db)
    {
        var stray = db.ChangeTracker.Entries<RolePermission>().Where(e => e.State == EntityState.Added).Select(e => e.Entity.PermissionKey)
            .Concat(db.ChangeTracker.Entries<UserPermission>().Where(e => e.State == EntityState.Added).Select(e => e.Entity.PermissionKey))
            .FirstOrDefault(k => !PermissionCatalog.Contains(k));
        if (stray is not null)
            throw new AuthorizationIntegrityException($"'{stray}' is not a permission of the catalogue.");
    }
}

/// <summary>The three system roles, created where they are missing — with their seed.</summary>
internal static class SystemRoleStore
{
    /// <summary>Adds what is missing to the context (not saved). Returns the rows it added.</summary>
    public static async Task<List<object>> EnsureAsync(PosDbContext db, CancellationToken ct)
    {
        var added = new List<object>();
        var ids = SystemRoles.All.Select(d => d.Id).ToList();
        var present = db.ChangeTracker.Entries<Role>().Where(e => ids.Contains(e.Entity.Id)).Select(e => e.Entity.Id)
            .Concat(await db.Roles.Where(r => ids.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct))
            .ToHashSet();

        foreach (var definition in SystemRoles.All.Where(d => !present.Contains(d.Id)))
        {
            var role = new Role
            {
                Id = definition.Id, Key = definition.Key, Name = definition.Name, NormalizedName = RoleNames.Normalize(definition.Name),
                Description = definition.Description,
                IsActive = true, IsSystem = true, Scope = definition.Scope, Version = 1,
            };
            db.Roles.Add(role);
            added.Add(role);
            foreach (var key in definition.Permissions)
            {
                var grant = new RolePermission { RoleId = role.Id, PermissionKey = key };
                db.RolePermissions.Add(grant);
                added.Add(grant);
            }
        }
        return added;
    }
}
