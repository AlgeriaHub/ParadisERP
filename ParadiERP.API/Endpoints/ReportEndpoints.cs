using System.Globalization;
using System.Security.Claims;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Auth;
using ParadiERP.Infrastructure.Reports;

namespace ParadiERP.API.Endpoints;

/// <summary>
/// Reports › Inventory › Item Movement &amp; Status (owner, 2026-10-12): the screen, its breakdown of one
/// article, and the two Excel files, all four answered from the same query so the file always carries the
/// screen's figures and totals.
///
/// <para>
/// The branch rule is the one <c>GET /api/catalog/stock/availability/{productId}</c> applies: the company's
/// roles see every location in full (and may narrow to one with <c>branchId</c>); anyone else sees their own
/// branch in full — <see cref="CallersBranch"/> sets <c>branchId</c> to it whatever was asked — and, only with
/// <see cref="PermissionCatalog.StockViewOtherBranches"/>, the other locations' available quantity and nothing
/// more: no customer, contract or movement of another branch leaves the server.
/// </para>
/// </summary>
public static class ReportEndpoints
{
    private const string Scoped =
        "a non-admin's branchId is replaced by their own branch: they read it in full and, only with " +
        "stock.view_other_branches, the other locations' available quantity alone";

    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports/inventory").WithTags("Reports").AtCallersBranch();

        group.MapGet("/items", async (
            Guid? branchId, string? q, string? productIds, string? itemType, string? status,
            Guid? customerId, Guid? contractId, bool? includeInactive,
            ClaimsPrincipal caller, ItemStatusReport report, CancellationToken ct) =>
        {
            var (viewer, filter) = Ask(caller, branchId, q, productIds, itemType, status, customerId, contractId, includeInactive);
            return Results.Ok(await report.SummaryAsync(viewer, filter, ct));
        }).WithMetadata(new BranchScopedAttribute(Scoped));
        InventoryPageRoutes.MapItemStatusPages(group);

        group.MapGet("/items/export", async (
            Guid? branchId, string? q, string? productIds, string? itemType, string? status,
            Guid? customerId, Guid? contractId, bool? includeInactive, string? lang,
            ClaimsPrincipal caller, ItemStatusReport report, CancellationToken ct) =>
        {
            var (viewer, filter) = Ask(caller, branchId, q, productIds, itemType, status, customerId, contractId, includeInactive);
            var summary = await report.SummaryAsync(viewer, filter, ct);
            return Results.File(report.SummaryWorkbook(summary, ItemStatusLabels.For(lang)),
                XlsxWriter.ContentType, string.Create(CultureInfo.InvariantCulture, $"item-status-{DateTime.UtcNow:yyyyMMdd}.xlsx"));
        }).WithMetadata(new BranchScopedAttribute(Scoped));

        group.MapGet("/items/{productId:guid}", async (
            Guid productId, Guid? branchId, string? q, string? productIds, string? itemType, string? status,
            Guid? customerId, Guid? contractId, bool? includeInactive, DateOnly? from, DateOnly? to,
            ClaimsPrincipal caller, ItemStatusReport report, CancellationToken ct) =>
        {
            var (viewer, filter) = Ask(caller, branchId, q, productIds, itemType, status, customerId, contractId, includeInactive);
            var detail = await report.DetailAsync(productId, viewer, filter, from, to, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        }).WithMetadata(new BranchScopedAttribute(Scoped));

        group.MapGet("/items/{productId:guid}/export", async (
            Guid productId, Guid? branchId, string? q, string? productIds, string? itemType, string? status,
            Guid? customerId, Guid? contractId, bool? includeInactive, DateOnly? from, DateOnly? to, string? lang,
            ClaimsPrincipal caller, ItemStatusReport report, CancellationToken ct) =>
        {
            var (viewer, filter) = Ask(caller, branchId, q, productIds, itemType, status, customerId, contractId, includeInactive);
            var detail = await report.DetailAsync(productId, viewer, filter, from, to, ct);
            if (detail is null) return Results.NotFound();
            return Results.File(report.DetailWorkbook(detail, ItemStatusLabels.For(lang)),
                XlsxWriter.ContentType, string.Create(CultureInfo.InvariantCulture, $"item-status-{detail.Item.Sku}-{DateTime.UtcNow:yyyyMMdd}.xlsx"));
        }).WithMetadata(new BranchScopedAttribute(Scoped));
    }

    /// <summary>
    /// Who is asking and what for. For anyone without company scope <paramref name="branchId"/> has already
    /// been set to their own branch by <see cref="CallersBranch"/>, so it is their branch, never a filter.
    /// </summary>
    private static (ItemStatusViewer Viewer, ItemStatusFilter Filter) Ask(
        ClaimsPrincipal caller, Guid? branchId, string? q, string? productIds, string? itemType, string? status,
        Guid? customerId, Guid? contractId, bool? includeInactive)
    {
        var companyScoped = BranchAssignment.IsCompanyScoped(caller);
        var viewer = companyScoped
            ? new ItemStatusViewer(null, false)
            : new ItemStatusViewer(branchId ?? Guid.Empty,
                ParadiERP.Infrastructure.Authorization.ServerAuthorization.Has(caller, PermissionCatalog.StockViewOtherBranches));
        var filter = new ItemStatusFilter(companyScoped ? branchId : null, q, Ids(productIds), itemType, status,
            customerId, contractId, includeInactive ?? false);
        return (viewer, filter);
    }

    /// <summary>A comma-separated list of ids; anything that is not one is dropped rather than refused.</summary>
    private static IReadOnlyCollection<Guid>? Ids(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => Guid.TryParse(part, out var id) ? id : (Guid?)null)
            .OfType<Guid>().Distinct().ToList();
    }
}
