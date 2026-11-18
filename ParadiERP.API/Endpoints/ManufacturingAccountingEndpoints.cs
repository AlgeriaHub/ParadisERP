using System.Security.Claims;
using ParadiERP.Application.Contracts;
using ParadiERP.Infrastructure.Manufacturing;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace ParadiERP.API.Endpoints;

/// <summary>
/// Manufacturing › Costing settings (Phase C1, owner 2026-11-08): the accountant's open costing and accounting
/// choices and the account map, read by whoever sees costs or the accounting reports, changed under
/// accounting.manage. Company-wide choices with no branch and no person's money: no branch rule applies.
/// </summary>
public static class ManufacturingAccountingEndpoints
{
    public static void MapManufacturingAccountingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/manufacturing/accounting").WithTags("Manufacturing");

        group.MapGet("/settings", async ([FromServices] ManufacturingCostingChoices choices, CancellationToken ct) =>
            Results.Ok(await choices.SettingsAsync(ct)));

        group.MapPut("/settings", async (UpdateManufacturingCostingSettingsRequest r, ClaimsPrincipal caller,
            [FromServices] ManufacturingCostingChoices choices, CancellationToken ct) =>
        {
            var (saved, error) = await choices.UpdateSettingsAsync(r, MovementAuthor.Of(caller).UserName, ct);
            return error is null ? Results.Ok(saved) : Results.BadRequest(error);
        });

        group.MapGet("/account-map", async ([FromServices] ManufacturingCostingChoices choices, CancellationToken ct) =>
            Results.Ok(await choices.AccountMapAsync(ct)));

        group.MapPut("/account-map", async (UpdateManufacturingAccountMapRequest r, ClaimsPrincipal caller,
            [FromServices] ManufacturingCostingChoices choices, CancellationToken ct) =>
        {
            var (saved, error) = await choices.UpdateAccountMapAsync(r, MovementAuthor.Of(caller).UserName, ct);
            return error is null ? Results.Ok(saved) : Results.BadRequest(error);
        });

        // Phase C2: one month of the entries the chosen model would make, computed on read and posted nowhere; rate access only.
        group.MapGet("/practice-ledger", async (int? year, int? month, Guid? lineId, [FromServices] PracticeLedgerReport ledger,
            PosDbContext db, HttpContext http, CancellationToken ct) =>
            await ManufacturingEndpoints.CallerAsync(http, db, ct) is MfgCaller caller
                ? ManufacturingEndpoints.Answer(await ledger.MonthAsync(caller, year, month, lineId, ct))
                : ManufacturingEndpoints.NoCaller());
        ManufacturingPageRoutes.MapPracticeLedgerEntriesPage(group); ManufacturingPageRoutes.MapPracticeLedgerLateCostsPage(group);

        // Phase D: the accountant posts a closed month to the ledger, or reverses the last month posted.
        group.MapPost("/practice-ledger/post", async (PracticeLedgerMonthRequest r, [FromServices] ManufacturingLedgerPostings postings,
            PosDbContext db, HttpContext http, CancellationToken ct) =>
            await ManufacturingEndpoints.CallerAsync(http, db, ct) is MfgCaller caller
                ? ManufacturingEndpoints.Answer(await postings.PostAsync(caller, r.Year, r.Month, ct))
                : ManufacturingEndpoints.NoCaller());
        group.MapPost("/practice-ledger/reverse", async (PracticeLedgerMonthRequest r, [FromServices] ManufacturingLedgerPostings postings,
            PosDbContext db, HttpContext http, CancellationToken ct) =>
            await ManufacturingEndpoints.CallerAsync(http, db, ct) is MfgCaller caller
                ? ManufacturingEndpoints.Answer(await postings.ReverseAsync(caller, r.Year, r.Month, ct))
                : ManufacturingEndpoints.NoCaller());
    }
}
