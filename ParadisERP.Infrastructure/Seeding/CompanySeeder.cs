using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Seeding;

public static class CompanySeeder
{
    /// <summary>Provision only the minimum real-company records, with operator-supplied credentials.</summary>
    public static async Task SeedAsync(PosDbContext db, TenantOptions company, CancellationToken ct = default)
    {
        if (await db.Users.AnyAsync(ct)) return;
        if (string.IsNullOrWhiteSpace(company.AdminUsername) || company.BootstrapAdminPassword is not { Length: >= 8 })
            throw new InvalidOperationException($"Company '{company.Id}' requires an admin username and a bootstrap password of at least 8 characters.");
        await using var transaction = await db.BeginWriteAsync(ct);
        if (!await db.SyncCounters.AnyAsync(ct)) db.SyncCounters.Add(new SyncCounter());
        await db.SaveChangesAsync(ct);
        var admin = new User
        {
            Username = company.AdminUsername.Trim(), FullName = company.AdminUsername.Trim(), Role = UserRole.Admin,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(company.BootstrapAdminPassword)
        };
        // No name until the owner gives one in Company settings. The company id is an internal
        // key ("default" for a single-company install) and was printed at the top of every
        // receipt when it stood in for the name.
        var store = new Store();
        // The main branch and the main warehouse, from the first start (owner, 2026-10-05). The first
        // till is the main branch's: the warehouse never sells. Their cash box comes from
        // CoreDataSeeder — the main branch's is the central safe, the warehouse has none. Their
        // location codes, 10 and 11, are given by the save itself (BranchCodeAssignment, 2026-10-05).
        var main = DefaultBranches.NewMainBranch();
        var warehouse = DefaultBranches.NewMainWarehouse();
        db.Users.Add(admin);
        db.Stores.Add(store);
        db.Branches.AddRange(main, warehouse);
        db.Devices.Add(new Device { Code = "C01", Name = "Main terminal", BranchId = main.Id });
        await db.SaveWithVersionsAsync(ct, admin, store, main, warehouse);
        await transaction.CommitAsync(ct);
    }
}
