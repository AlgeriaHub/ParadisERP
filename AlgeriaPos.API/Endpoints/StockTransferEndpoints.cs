using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using AlgeriaPos.API.Subscriptions;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Security;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Auth;
using AlgeriaPos.Infrastructure.Authorization;
using AlgeriaPos.Infrastructure.Catalog;
using AlgeriaPos.Infrastructure.Persistence;

namespace AlgeriaPos.API.Endpoints;

/// <summary>
/// Stock transfers with dispatch and receive (owner, 2026-10-12), replacing the instant transfer:
/// <list type="bullet">
/// <item><c>POST</c> dispatches from the source — a branch user's own branch (the lock rewrites
/// <c>FromBranchId</c>), any branch for the Admin. The goods leave available stock and are in transit.</item>
/// <item><c>/receive</c> at the destination only, in full or in parts, and may report part of what is
/// left as short with a reason (2026-10-13); <c>/lost</c> by the source, with a reason and
/// <c>inventory.transfer.write_off</c>; <c>/cancel</c> by the source while nothing was received.</item>
/// <item><c>/shortfall/confirm</c> and <c>/shortfall/reject</c> answer a reported shortfall: the
/// source branch, or a holder of <c>inventory.transfer.write_off</c> (company-scoped: any transfer);
/// never the destination. Confirmed, it is written off as a loss; rejected, it stays in transit.</item>
/// <item>The list's <c>branchId</c> is pinned to a branch user's branch and matches either end; a
/// transfer read or acted on by id is 404 to a branch user at neither end.</item>
/// </list>
/// The rules themselves are in <see cref="StockTransfers"/>; this file is the HTTP shape.
/// </summary>
public static class StockTransferEndpoints
{
    public static void MapStockTransferEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/catalog/stock/transfers").WithTags("Stock")
            .AddEndpointFilter<SubscriptionWriteFilter>()
            .AtCallersBranch();

        group.MapGet("", async (
            Guid? branchId, string? status, Guid? productId, DateTime? from, DateTime? to,
            [FromServices] StockTransfers transfers, PosDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var viewer = await ActorAsync(http, db, ct);
            var list = await transfers.ListAsync(branchId, status, productId, from, to, ct);
            var names = await transfers.NamesAsync(list.SelectMany(t => new[] { t.FromBranchId, t.ToBranchId }), ct);
            return Results.Ok(list.Select(t => StockTransfers.ToDto(t, names, viewer)).ToList());
        });
        InventoryPageRoutes.MapTransfersPage(group);

        group.MapGet("/{id:guid}", async (Guid id, [FromServices] StockTransfers transfers, PosDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (await ActorAsync(http, db, ct) is not TransferActor actor) return Results.BadRequest(BranchLock.NoBranchMessage);
            var transfer = await transfers.GetAsync(id, ct);
            if (transfer is null || !StockTransfers.IsParty(transfer, actor)) return Results.NotFound("Transfer not found.");
            return Results.Ok(StockTransfers.ToDto(transfer, await transfers.NamesAsync([transfer.FromBranchId, transfer.ToBranchId], ct), actor));
        });

        group.MapPost("", async (DispatchStockTransferRequest r, [FromServices] StockTransfers transfers, PosDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (await ActorAsync(http, db, ct) is not TransferActor actor) return Results.BadRequest(BranchLock.NoBranchMessage);
            var result = await transfers.DispatchAsync(r, actor, ct);
            if (!result.Ok) return Refusal(result);
            var dto = StockTransfers.ToDto(result.Transfer!, await transfers.NamesAsync([r.FromBranchId, r.ToBranchId], ct), actor);
            return Results.Created($"/api/catalog/stock/transfers/{dto.Id}", dto);
        });

        group.MapPost("/{id:guid}/receive", (Guid id, ReceiveStockTransferRequest r, [FromServices] StockTransfers transfers, PosDbContext db, HttpContext http, CancellationToken ct) =>
            ActAsync(http, db, transfers, actor => transfers.ReceiveAsync(id, r, actor, ct), ct));

        group.MapPost("/{id:guid}/lost", (Guid id, DeclareTransferLossRequest r, [FromServices] StockTransfers transfers, PosDbContext db, HttpContext http, CancellationToken ct) =>
            ActAsync(http, db, transfers, actor => transfers.DeclareLostAsync(id, r, actor, ct), ct));

        group.MapPost("/{id:guid}/cancel", (Guid id, CancelStockTransferRequest r, [FromServices] StockTransfers transfers, PosDbContext db, HttpContext http, CancellationToken ct) =>
            ActAsync(http, db, transfers, actor => transfers.CancelAsync(id, r, actor, ct), ct));

        group.MapPost("/{id:guid}/shortfall/confirm", (Guid id, TransferShortfallDecisionRequest r, [FromServices] StockTransfers transfers, PosDbContext db, HttpContext http, CancellationToken ct) =>
            ActAsync(http, db, transfers, actor => transfers.ConfirmShortfallAsync(id, r, actor, ct), ct));

        group.MapPost("/{id:guid}/shortfall/reject", (Guid id, TransferShortfallDecisionRequest r, [FromServices] StockTransfers transfers, PosDbContext db, HttpContext http, CancellationToken ct) =>
            ActAsync(http, db, transfers, actor => transfers.RejectShortfallAsync(id, r, actor, ct), ct));
    }

    private static async Task<IResult> ActAsync(
        HttpContext http, PosDbContext db, StockTransfers transfers, Func<TransferActor, Task<TransferResult>> act, CancellationToken ct)
    {
        if (await ActorAsync(http, db, ct) is not TransferActor actor) return Results.BadRequest(BranchLock.NoBranchMessage);
        var result = await act(actor);
        if (!result.Ok) return Refusal(result);
        var t = result.Transfer!;
        return Results.Ok(StockTransfers.ToDto(t, await transfers.NamesAsync([t.FromBranchId, t.ToBranchId], ct), actor));
    }

    /// <summary>
    /// Who is acting: the token's user, their branch unless their role spans the company, and the two
    /// transfer permissions the server resolved for them. Null: a branch user with no branch.
    /// </summary>
    private static async Task<TransferActor?> ActorAsync(HttpContext http, PosDbContext db, CancellationToken ct)
    {
        // The same author every stock movement records (MovementAuthor): the token's sub and name.
        var (userId, name) = MovementAuthor.Of(http.User);
        var mayTransfer = ServerAuthorization.Has(http.User, PermissionCatalog.InventoryTransfer);
        var mayWriteOff = ServerAuthorization.Has(http.User, PermissionCatalog.InventoryTransferWriteOff);
        if (BranchAssignment.IsCompanyScoped(http.User)) return new TransferActor(userId, name, null, mayTransfer, mayWriteOff);
        return await BranchAssignment.ForCallerAsync(http.User, db, ct) is Guid own
            ? new TransferActor(userId, name, own, mayTransfer, mayWriteOff)
            : null;
    }

    private static IResult Refusal(TransferResult result) => result.Status switch
    {
        403 => Results.Problem(result.Error, statusCode: StatusCodes.Status403Forbidden),
        404 => Results.NotFound(result.Error),
        409 => Results.Conflict(result.Error),
        _ => Results.BadRequest(result.Error),
    };
}
