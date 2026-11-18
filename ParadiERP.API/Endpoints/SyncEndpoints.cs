using System.Security.Claims;
using ParadiERP.Application.Contracts;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Auth;
using ParadiERP.Infrastructure.Authorization;
using ParadiERP.Infrastructure.Receivables;
using ParadiERP.Infrastructure.Sync;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Logging;

namespace ParadiERP.API.Endpoints;

public static class SyncEndpoints
{
    /// <summary>
    /// The terminal a request may act as is the one its token was issued for — never one named
    /// in a query string or a request body.
    ///
    /// <para>
    /// The claim is trustworthy: JwtBearer's <c>OnTokenValidated</c> hands it to
    /// <c>IUserSessionValidator</c> on every request, which refuses the token unless the code
    /// still names a registered, active terminal. So reading it here can never refuse a sale —
    /// it only stops one till acting as another, whose branch stock, cash box, ledger entries
    /// and receipt-number space are not its to touch.
    /// </para>
    /// </summary>
    private static string? AuthenticatedDevice(HttpContext http) => http.User.FindFirstValue("device");

    /// <summary>
    /// The caller as the server knows them (security review, 2026-10-10): may they sell, may they
    /// refund — the role tables, read on this request — and who they are.
    /// </summary>
    private static SyncActor Actor(HttpContext http) => new(
        Guid.TryParse(http.User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id) ? id : null,
        http.User.FindFirstValue("name") ?? http.User.Identity?.Name,
        ServerAuthorization.Has(http.User, PermissionCatalog.SalesCreate),
        ServerAuthorization.Has(http.User, PermissionCatalog.SalesRefund));

    public static void MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sync").WithTags("Sync").RequireAuthorization();

        // Delta pull: everything the terminal has not seen since `since`. The branch whose stock
        // figures come back is the one the token's terminal belongs to; a `deviceCode` in the
        // query string is ignored, because it used to decide which branch a caller could read.
        // An office session has no terminal and reads the main branch's figures (SyncService's
        // fallback): the back office reads stock per branch through its own routes.
        group.MapGet("/pull", async (long? since, HttpContext http, SyncService sync, CancellationToken ct) =>
                Results.Ok(await sync.PullAsync(since ?? 0, AuthenticatedDevice(http), ct, Actor(http))))
            .WithName("SyncPull");

        // Push the offline outbox. Idempotent: re-sending an already-recorded sale is a no-op.
        group.MapPost("/push", async (
            PushRequest request, HttpContext http, SyncService sync, ReceivableDocuments receivables,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
            {
                // An office session (signed in with no till) has no receipt-number space, branch
                // stock or cash box to record a sale against. Said in words with a stable code; a
                // till's own token never reaches this (its device claim is validated on every request).
                if (string.IsNullOrEmpty(AuthenticatedDevice(http)))
                    return Results.Problem(BranchScope.NoTerminalMessage, statusCode: StatusCodes.Status409Conflict,
                        extensions: new Dictionary<string, object?> { ["code"] = BranchScope.NoTerminalCode });
                if (request.Sales is null)
                    return Results.BadRequest("The 'sales' array is required.");
                if (request.Sales.Count > 500)
                    return Results.Problem("Send at most 500 sales per batch.", statusCode: StatusCodes.Status413PayloadTooLarge);

                var response = await sync.PushAsync(request, AuthenticatedDevice(http), ct, Actor(http));

                var acceptedIds = response.Results
                    .Where(r => string.Equals(r.Status, "accepted", StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.Id)
                    .ToHashSet();

                var callerName = http.User.FindFirstValue("name") ?? http.User.Identity?.Name;
                var logger = loggerFactory.CreateLogger("ParadiERP.API.Endpoints.SyncEndpoints");

                foreach (var sale in request.Sales.Where(s =>
                    string.Equals(s.Status, nameof(SaleStatus.Refunded), StringComparison.OrdinalIgnoreCase)
                    && acceptedIds.Contains(s.Id)))
                {
                    try
                    {
                        await receivables.CreditTillRefundAsync(sale.Id, callerName, ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to raise automatic credit note for refund sale {SaleId}", sale.Id);
                    }
                }

                return Results.Ok(response);
            })
            .WithName("SyncPush");
    }
}
