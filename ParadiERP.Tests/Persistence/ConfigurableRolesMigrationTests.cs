using ParadiERP.Domain;
using ParadiERP.Infrastructure.Authorization;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ParadiERP.Tests.Persistence;

/// <summary>
/// AUTH-1 Step 1 as an installation that is already trading performs it: the real migrations run
/// on a real SQLite database holding users in the previous release's shape, then the start-up
/// bootstrap moves them. Each user lands on their role with exactly the grants that gave them
/// something, the rest is reported with its reason, nobody's session is touched — and a second run
/// changes nothing at all.
/// </summary>
public class ConfigurableRolesMigrationTests
{
    /// <summary>The release before this one.</summary>
    private const string Before = "EmployeeFingerprintNumber";

    private sealed record Legacy(Guid Id, string Username, UserRole Role, string? Grants, bool Active = true, bool Deleted = false);

    [Fact]
    public async Task Users_in_the_old_shape_move_onto_roles_with_their_effective_grants_once_and_only_once()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        PosDbContext Open() => new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);

        await using (var old = Open()) await old.Database.GetService<IMigrator>().MigrateAsync(Before);

        Legacy[] people =
        [
            new(Guid.NewGuid(), "admin", UserRole.Admin, "hr.attendance,payroll.manage"),
            new(Guid.NewGuid(), "gerant-paie", UserRole.Manager, "payroll.manage,treasury.cash_box,something-old"),
            new(Guid.NewGuid(), "gerant", UserRole.Manager, null),
            new(Guid.NewGuid(), "caisse-box", UserRole.Cashier, "treasury.cash_box"),
            new(Guid.NewGuid(), "caisse-dead", UserRole.Cashier, "payroll.manage,stock.view_other_branches"),
            new(Guid.NewGuid(), "caisse-partie", UserRole.Cashier, "", Active: false, Deleted: true),
        ];
        await using (var old = Open())
        {
            var now = DateTime.UtcNow;
            foreach (var p in people)
                // Raw SQL: the current model would insist on RoleId, which the old table lacks.
                await old.Database.ExecuteSqlRawAsync(
                    "INSERT INTO Users (Id, Version, UpdatedAtUtc, IsDeleted, Username, FullName, PasswordHash, PinHash, Role, IsActive, Permissions) " +
                    "VALUES ({0}, 7, {1}, {2}, {3}, {3}, 'x', '', {4}, {5}, {6})",
                    p.Id, now, p.Deleted, p.Username, (int)p.Role, p.Active, new SqliteParameter("grants", (object?)p.Grants ?? DBNull.Value));
        }

        await using (var upgrade = Open())
        {
            await upgrade.Database.MigrateAsync();
            Assert.Empty(await upgrade.Database.GetPendingMigrationsAsync());
        }
        AuthorizationBootstrapResult first;
        await using (var start = Open()) first = await AuthorizationBootstrap.RunAsync(start);

        Assert.Equal(new AuthorizationBootstrapResult(3, 0, 0, 0, 6, 2, 6), first);

        await using var read = Open();
        var users = await read.Users.AsNoTracking().ToDictionaryAsync(u => u.Username);
        var grants = (await read.UserPermissions.AsNoTracking().ToListAsync())
            .GroupBy(g => g.UserId).ToDictionary(g => g.Key, g => g.Select(x => x.PermissionKey).Order().ToList());
        List<string> GrantsOf(string name) => grants.GetValueOrDefault(users[name].Id, []);

        Assert.Equal(SystemRoles.AdminId, users["admin"].RoleId);
        Assert.Equal(SystemRoles.ManagerId, users["gerant-paie"].RoleId);
        Assert.Equal(SystemRoles.CashierId, users["caisse-partie"].RoleId);
        Assert.Empty(GrantsOf("admin"));
        Assert.Equal(["payroll.manage"], GrantsOf("gerant-paie"));
        Assert.Empty(GrantsOf("gerant"));
        // Every cashier on file keeps selling untaxed at the till when that became a permission (SalesUntaxedGrant,
        // 2026-11-02): their own grant, outside the five legacy keys, so the move onto roles leaves it. Not for the
        // deleted one.
        Assert.Equal(["sales.untaxed", "treasury.cash_box"], GrantsOf("caisse-box"));
        Assert.Equal(["sales.untaxed"], GrantsOf("caisse-dead"));
        Assert.Empty(GrantsOf("caisse-partie"));

        var report = await read.AuthorizationReport.AsNoTracking().OrderBy(r => r.Username).ThenBy(r => r.PermissionKey)
            .Select(r => $"{r.Username} {r.LegacyRole} {r.PermissionKey} {r.Reason} {r.Source}").ToListAsync();
        Assert.Equal(
        [
            "admin Admin hr.attendance admin-already-held migration",
            "admin Admin payroll.manage admin-already-held migration",
            "caisse-dead Cashier payroll.manage role-gate migration",
            "caisse-dead Cashier stock.view_other_branches role-gate migration",
            "gerant-paie Manager something-old unknown-permission migration",
            "gerant-paie Manager treasury.cash_box manager-by-role migration",
        ], report);

        // The legacy columns are untouched — they still decide access until Step 2 — and so is
        // every session: the version a token is checked against has not moved.
        Assert.All(people, p =>
        {
            Assert.Equal(p.Grants ?? null, users[p.Username].Permissions);
            Assert.Equal(7, users[p.Username].Version);
        });

        // Idempotent: a second start changes nothing, and the tables read the same.
        string Snapshot(PosDbContext c) => string.Join(";",
            c.Users.AsNoTracking().OrderBy(u => u.Username).Select(u => u.Username + "=" + u.RoleId).ToList()
                .Concat(c.UserPermissions.AsNoTracking().OrderBy(p => p.PermissionKey).Select(p => p.UserId + ":" + p.PermissionKey).ToList())
                .Concat(c.RolePermissions.AsNoTracking().OrderBy(p => p.PermissionKey).Select(p => p.RoleId + ":" + p.PermissionKey).ToList())
                .Append("report=" + c.AuthorizationReport.Count()));
        var before = Snapshot(read);
        AuthorizationBootstrapResult second;
        await using (var again = Open()) second = await AuthorizationBootstrap.RunAsync(again);
        Assert.False(second.ChangedAnything, second.ToString());
        await using var after = Open();
        Assert.Equal(before, Snapshot(after));
    }
}
