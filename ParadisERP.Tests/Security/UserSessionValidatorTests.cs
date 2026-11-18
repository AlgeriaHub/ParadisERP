using ParadisERP.Domain;
using ParadisERP.Infrastructure.Auth;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Security;

public class UserSessionValidatorTests
{
    [Fact]
    public async Task Changed_user_or_disabled_device_invalidates_an_existing_session()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new User { Username = "admin", Role = UserRole.Admin, Version = 5 };
        var branch = new Branch();
        var device = new Device { Code = "C01", BranchId = branch.Id };
        // A second Admin: the data layer refuses to deactivate the last one (AUTH-1), and this test
        // is about the session, not about who runs the company.
        db.Users.Add(new User { Username = "admin2", Role = UserRole.Admin });
        db.Users.Add(user); db.Branches.Add(branch); db.Devices.Add(device);
        await db.SaveChangesAsync();
        var validator = new UserSessionValidator(db);
        Assert.True(await validator.IsValidAsync(user.Id, 5, "Admin", "C01"));
        Assert.False(await validator.IsValidAsync(user.Id, 4, "Admin", "C01"));
        Assert.False(await validator.IsValidAsync(user.Id, 5, "Cashier", "C01"));
        user.IsActive = false;
        await db.SaveChangesAsync();
        Assert.False(await validator.IsValidAsync(user.Id, 5, "Admin", "C01"));
        user.IsActive = true; device.IsActive = false;
        await db.SaveChangesAsync();
        Assert.False(await validator.IsValidAsync(user.Id, 5, "Admin", "C01"));
    }
}
