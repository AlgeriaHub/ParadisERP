using ParadisERP.Application.Security;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Authorization;
using Xunit.Abstractions;

namespace ParadisERP.Tests.Security;

/// <summary>
/// Runs alone: xUnit runs a collection that disables parallelization after every parallel one has
/// finished. The measure is the whole process's heap, so a test allocating beside it is counted
/// as the cache's — on CI (2026-10-06) that read 8.5 KB a user where 500 users cost 2.8 KB each.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HeapMeasureCollection
{
    public const string Name = "heap measure";
}

/// <summary>What the authorization cache costs in memory per cached user (AUTH-1 Step 3).</summary>
[Collection(HeapMeasureCollection.Name)]
public sealed class AuthorizationCacheMemoryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task The_memory_a_cached_decision_costs_is_small_and_bounded_by_the_users()
    {
        var cache = new AuthorizationCache(TimeProvider.System);
        var manager = SystemRoles.ManagerSeed.ToHashSet(StringComparer.Ordinal);
        async Task FillAsync(int users)
        {
            for (var i = 0; i < users; i++)
            {
                var stamp = new AuthorizationStamp(SystemRoles.ManagerId, 1, 0, RoleScope.AssignedBranch, true);
                await cache.GetAsync("essai", Guid.NewGuid(), stamp,
                    _ => Task.FromResult(new ServerAuthorizationContext(SystemRoles.ManagerId, RoleScope.AssignedBranch,
                        ServerAuthorization.Combine(manager, true, [PermissionCatalog.PayrollManage]))), default);
            }
        }

        long Heap() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }
        var baseline = Heap();
        await FillAsync(500);
        var at500 = Heap();
        await FillAsync(4500);
        var at5000 = Heap();
        var perEntry = (at5000 - baseline) / 5000.0;
        output.WriteLine($"500 users: {(at500 - baseline) / 1024.0:F0} KiB; 5000 users: {(at5000 - baseline) / 1024.0:F0} KiB; {perEntry:F0} bytes per user (48 permissions)");
        Assert.Equal(5000, cache.Statistics.Entries);
        Assert.True(perEntry < 8 * 1024, $"{perEntry:F0} bytes per cached user");

        // A user whose decision moves a thousand times still holds one entry.
        var one = Guid.NewGuid();
        for (var v = 0; v < 1000; v++)
            await cache.GetAsync("essai", one, new AuthorizationStamp(SystemRoles.ManagerId, v, 0, RoleScope.AssignedBranch, true),
                _ => Task.FromResult(new ServerAuthorizationContext(SystemRoles.ManagerId, RoleScope.AssignedBranch, manager)), default);
        Assert.Equal(5001, cache.Statistics.Entries);
    }
}
