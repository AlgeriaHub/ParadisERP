using System.Security.Claims;
using System.Text.Json;
using ParadiERP.API.Endpoints;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;

namespace ParadiERP.Tests.Security;

/// <summary>
/// The one thing a non-admin may see of another branch (the owner, 2026-09-30): "one of the
/// employees may inquire about a product's availability at another branch" — "if he has been
/// given the permission". So: with <see cref="Permissions.ViewOtherBranchStock"/>, the other
/// branches' available quantity and nothing else; without it, nothing of them at all; and the
/// editable stock panel stays the caller's own branch either way.
/// </summary>
public class BranchAvailabilityTests
{
    private static readonly Guid Algiers = TillHarness.Algiers; // the main branch, standing in for Douera
    private static readonly Guid Oran = TillHarness.Oran;       // the caller's branch, standing in for Skikda

    private static ClaimsPrincipal WithPermission(ClaimsPrincipal principal) =>
        TillHarness.WithGrant(principal, Permissions.ViewOtherBranchStock);

    private static async Task<JsonElement[]> AvailabilityAsync(PosDbContext db, ClaimsPrincipal caller)
    {
        var (status, body) = await BranchLockTests.InvokeAsync(db, app => app.MapStockEndpoints(), "GET",
            "/api/catalog/stock/availability/{productId:guid}", caller, "", ("productId", TillHarness.Pipe.ToString()));
        Assert.Equal(200, status);
        return JsonDocument.Parse(body).RootElement.EnumerateArray().ToArray();
    }

    private static JsonElement Row(JsonElement[] rows, Guid branch) =>
        rows.Single(r => r.GetProperty("branchId").GetGuid() == branch);

    [Fact]
    public async Task Without_the_permission_a_manager_sees_no_other_branch()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var (_, manager) = await BranchLockTests.UserAsync(db, UserRole.Manager, Oran);

        var rows = await AvailabilityAsync(db, manager);

        Assert.Equal([Oran], rows.Select(r => r.GetProperty("branchId").GetGuid()));
        Assert.Equal(0m, Row(rows, Oran).GetProperty("onRent").GetDecimal()); // their own, in full
    }

    [Fact]
    public async Task With_the_permission_a_manager_reads_another_branch_s_available_quantity_and_nothing_else()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var (_, manager) = await BranchLockTests.UserAsync(db, UserRole.Manager, Oran);

        var rows = await AvailabilityAsync(db, WithPermission(manager));

        var other = Row(rows, Algiers);
        Assert.Equal(10m, other.GetProperty("available").GetDecimal());
        foreach (var hidden in new[] { "onRent", "maintenance", "scrap", "owned" })
            Assert.Equal(JsonValueKind.Null, other.GetProperty(hidden).ValueKind);
        // No figure of value travels at all: the row has no cost, value or price field.
        Assert.DoesNotContain(other.EnumerateObject(), p =>
            p.Name.Contains("cost", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("value", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("price", StringComparison.OrdinalIgnoreCase));

        Assert.NotEqual(JsonValueKind.Null, Row(rows, Oran).GetProperty("owned").ValueKind);
    }

    [Fact]
    public async Task The_admin_reads_every_branch_in_full()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var (_, admin) = await BranchLockTests.UserAsync(db, UserRole.Admin, Oran);

        var rows = await AvailabilityAsync(db, admin);

        Assert.Equal(2, rows.Length);
        Assert.All(rows, r => Assert.NotEqual(JsonValueKind.Null, r.GetProperty("owned").ValueKind));
    }

    [Fact]
    public async Task The_editable_stock_panel_stays_the_caller_s_own_branch_even_with_the_permission()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var (_, manager) = await BranchLockTests.UserAsync(db, UserRole.Manager, Oran);

        var (status, body) = await BranchLockTests.InvokeAsync(db, app => app.MapCatalogEndpoints(), "GET",
            "/api/catalog/products/{id:guid}/stock", WithPermission(manager), "", ("id", TillHarness.Pipe.ToString()));

        Assert.Equal(200, status);
        var branches = JsonDocument.Parse(body).RootElement.EnumerateArray().Select(r => r.GetProperty("branchId").GetGuid());
        Assert.Equal([Oran], branches);
    }
}
