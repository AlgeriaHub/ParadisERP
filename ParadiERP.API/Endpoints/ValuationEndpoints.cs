using System.Security.Claims;
using ParadiERP.Application.Contracts;
using ParadiERP.Infrastructure.Auth;
using ParadiERP.Infrastructure.Ledger;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Valuation;
using Microsoft.AspNetCore.Mvc;

namespace ParadiERP.API.Endpoints;

/// <summary>
/// The weighted-average shadow (P0.4b): stock valued at a moving average beside today's cost price, read
/// only, under the accounting reports' key. The choices the accountant has not answered are settings the
/// admin sets (owner, 2026-11-08), under accounting.manage.
/// <para>
/// Branches: a caller who sees every branch reads everything. A branch user reads their own location
/// when there is one average per location (CallersBranch sets <c>branchId</c> to it); with one average for
/// the company the figures pool every branch's goods, so they are refused in words (403
/// <c>valuation_company_wide</c>) rather than shown.
/// </para>
/// </summary>
public static class ValuationEndpoints
{
    public const string CompanyWideCode = "valuation_company_wide";
    private const string CompanyWide =
        "With one average cost for the company, the valuation pools every branch's goods: it is read by a user who sees every branch.";

    public static void MapValuationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/valuation/shadow").WithTags("Valuation").AtCallersBranch();

        group.MapGet("", async (Guid? branchId, ClaimsPrincipal caller, PosDbContext db,
            [FromServices] StockValuationShadow shadow, CancellationToken ct) =>
        {
            var only = await BranchScope.ForAsync(caller, db, ct);
            if (only is not null && await CompanyWideAsync(shadow, ct)) return Refused();
            return Results.Ok(await shadow.ReportAsync(only ?? branchId, ct));
        }).WithMetadata(new BranchScopedAttribute("a branch user reads their own location, and only with one average per location (CallersBranch)"));

        group.MapGet("/products/{productId:guid}", async (Guid productId, ClaimsPrincipal caller, PosDbContext db,
            [FromServices] StockValuationShadow shadow, CancellationToken ct) =>
        {
            var only = await BranchScope.ForAsync(caller, db, ct);
            if (only is not null && await CompanyWideAsync(shadow, ct)) return Refused();
            return await shadow.ProductAsync(productId, only, ct) is { } detail ? Results.Ok(detail) : Results.NotFound();
        }).WithMetadata(new BranchScopedAttribute("a branch user reads their own location's movements, and only with one average per location"));
        FinancePageRoutes.MapValuationPage(group); FinancePageRoutes.MapValuationProductPage(group);

        group.MapGet("/settings", async ([FromServices] StockValuationShadow shadow, CancellationToken ct) =>
            Results.Ok(await shadow.SettingsAsync(ct)));

        group.MapPut("/settings", async (UpdateStockValuationSettingsRequest r, ClaimsPrincipal caller,
            [FromServices] StockValuationShadow shadow, CancellationToken ct) =>
        {
            var (saved, error) = await shadow.UpdateSettingsAsync(r, MovementAuthor.Of(caller).UserName, ct);
            return error is null ? Results.Ok(saved) : Results.BadRequest(error);
        });

        // How sales are costed (C-01): the accountant's switch, the opening adjustment it posts (C-02) and the
        // negative-stock gaps (C-16). The figures are the company's stock and stock accounts, so a branch-bound
        // caller is refused in words, as the company-wide shadow is.
        var costing = app.MapGroup("/api/valuation/sales-cost").WithTags("Valuation");

        costing.MapGet("", async (ClaimsPrincipal caller, PosDbContext db, [FromServices] SalesCostSwitch salesCost, CancellationToken ct) =>
            await BranchScope.ForAsync(caller, db, ct) is not null ? Refused() : Results.Ok(await salesCost.StatusAsync(ct)));

        costing.MapPost("/switch", async (SwitchSalesCostRequest r, ClaimsPrincipal caller, PosDbContext db,
            [FromServices] SalesCostSwitch salesCost, CancellationToken ct) =>
        {
            if (await BranchScope.ForAsync(caller, db, ct) is not null) return Refused();
            return Answer(await salesCost.SwitchAsync(r, MovementAuthor.Of(caller).UserName, ct));
        });

        costing.MapPost("/gaps/post", async (ClaimsPrincipal caller, PosDbContext db,
            [FromServices] SalesCostSwitch salesCost, CancellationToken ct) =>
        {
            if (await BranchScope.ForAsync(caller, db, ct) is not null) return Refused();
            return Answer(await salesCost.PostGapsAsync(MovementAuthor.Of(caller).UserName, ct));
        });
        FinancePageRoutes.MapSalesCostGapsPage(costing);
    }

    private static IResult Answer(SalesCostSwitch.Outcome outcome) => outcome.Value is { } value && outcome.Code is null
        ? Results.Ok(value)
        : Results.Problem(outcome.Error, statusCode: outcome.Status,
            extensions: new Dictionary<string, object?> { ["code"] = outcome.Code, ["costing"] = outcome.Value });

    private static async Task<bool> CompanyWideAsync(StockValuationShadow shadow, CancellationToken ct) =>
        (await shadow.SettingsAsync(ct)).Scope == nameof(ParadiERP.Domain.StockValuationScope.Company);

    private static IResult Refused() => Results.Problem(CompanyWide, statusCode: StatusCodes.Status403Forbidden,
        extensions: new Dictionary<string, object?> { ["code"] = CompanyWideCode });
}
