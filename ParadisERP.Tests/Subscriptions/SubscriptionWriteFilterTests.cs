using ParadisERP.API.Subscriptions;
using ParadisERP.Application.Contracts;
using ParadisERP.Application.Subscriptions;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Subscriptions;

public class SubscriptionWriteFilterTests
{
    private sealed class Provider(bool allowed, int? users = null) : ISubscriptionProvider
    {
        public SubscriptionAccess GetAccess() => new("Test", allowed ? "Active" : "Expired", allowed,
            null, users, 1, 1, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Expired_plan_blocks_writes_but_allows_reads()
    {
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite("Data Source=:memory:").Options);
        var filter = new SubscriptionWriteFilter(new Provider(false), db);
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        var called = false;
        EndpointFilterDelegate next = _ => { called = true; return ValueTask.FromResult<object?>(Results.Ok()); };
        var result = await filter.InvokeAsync(EndpointFilterInvocationContext.Create(http), next);
        Assert.Equal(403, ((IStatusCodeHttpResult)result!).StatusCode);
        Assert.False(called);
        http.Request.Method = "GET";
        await filter.InvokeAsync(EndpointFilterInvocationContext.Create(http), next);
        Assert.True(called);
    }

    [Fact]
    public async Task Limit_blocks_creating_and_reactivating_but_allows_editing_active_users()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var active = new User { Username = "admin", IsActive = true };
        var inactive = new User { Username = "inactive", IsActive = false };
        db.Users.AddRange(active, inactive);
        db.SyncCounters.Add(new SyncCounter());
        await db.SaveChangesAsync();
        var filter = new SubscriptionWriteFilter(new Provider(true, 1), db);
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        EndpointFilterDelegate next = _ => ValueTask.FromResult<object?>(Results.Ok());
        var create = EndpointFilterInvocationContext.Create(http, new CreateUserRequest("new", "New", "Cashier", "secret", null));
        Assert.Equal(409, ((IStatusCodeHttpResult)(await filter.InvokeAsync(create, next))!).StatusCode);
        http.Request.Method = "PUT";
        var reactivate = EndpointFilterInvocationContext.Create(http, inactive.Id, new UpdateUserRequest("Inactive", "Cashier", true));
        Assert.Equal(409, ((IStatusCodeHttpResult)(await filter.InvokeAsync(reactivate, next))!).StatusCode);
        var edit = EndpointFilterInvocationContext.Create(http, active.Id, new UpdateUserRequest("Renamed", "Admin", true));
        Assert.Equal(200, ((IStatusCodeHttpResult)(await filter.InvokeAsync(edit, next))!).StatusCode);
    }
}
