using System.Security.Claims;
using ParadisERP.API.Subscriptions;
using ParadisERP.Application.Contracts;
using ParadisERP.Infrastructure.Auth;
using ParadisERP.Infrastructure.FixedAssets;
using Microsoft.AspNetCore.Mvc;

namespace ParadisERP.API.Endpoints;

/// <summary>
/// REN-12, Phase M7a (docs/accounting/fixed-assets.md): the register of hire equipment as fixed assets.
/// <para>
/// The settings and the account map's slots are read with the accounting reports' key or accounting.manage and changed under
/// accounting.manage; the switch-over is previewed and posted under accounting.manage by a caller who reads every branch (its
/// figures are the company's). The register, a pool's history and the month's depreciation are read with reports.accounting,
/// a non-admin's <c>branchId</c> being their own branch (CallersBranch), as the accounting reports are; a month is posted or
/// reversed under accounting.manage by a caller who reads every branch.
/// </para>
/// </summary>
public static class FixedAssetEndpoints
{
    public const string CompanyWideCode = "fixed_assets_company_wide";

    public static void MapFixedAssetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/fixed-assets").WithTags("Fixed assets")
            .AddEndpointFilter<SubscriptionWriteFilter>()
            .AtCallersBranch();

        group.MapGet("/settings", async ([FromServices] FixedAssetSettingsStore store, CancellationToken ct) =>
            Results.Ok(await store.SettingsAsync(ct)));

        group.MapPut("/settings", async (UpdateFixedAssetSettingsRequest r, ClaimsPrincipal caller,
            [FromServices] FixedAssetSettingsStore store, CancellationToken ct) =>
            Answer(await store.UpdateAsync(r, MovementAuthor.Of(caller).UserName, ct)));

        group.MapPut("/account-map", async (UpdateManufacturingAccountMapRequest r, ClaimsPrincipal caller,
            [FromServices] FixedAssetSettingsStore store, CancellationToken ct) =>
            Answer(await store.UpdateMapAsync(r, MovementAuthor.Of(caller).UserName, ct)));

        // The switch-over moves every branch's hire equipment at once: the company's figures, read and posted by a caller who
        // reads every branch.
        group.MapGet("/switch-over", async (ClaimsPrincipal caller, [FromServices] FixedAssetSwitchOver switchOver, CancellationToken ct) =>
            BranchAssignment.IsCompanyScoped(caller) ? Results.Ok(await switchOver.PreviewAsync(ct)) : CompanyWide());

        group.MapPost("/switch-over", async (PostFixedAssetSwitchRequest r, ClaimsPrincipal caller,
            [FromServices] FixedAssetSwitchOver switchOver, CancellationToken ct) =>
            BranchAssignment.IsCompanyScoped(caller)
                ? Answer(await switchOver.PostAsync(r, MovementAuthor.Of(caller).UserName, ct))
                : CompanyWide());

        group.MapGet("/register", async (int? year, int? month, Guid? branchId,
            [FromServices] FixedAssetRegisterReport register, CancellationToken ct) =>
            Answer(await register.RegisterAsync(year, month, branchId, ct)))
            .WithMetadata(new BranchScopedAttribute("a non-admin's branchId is replaced by their own branch (CallersBranch)"));

        group.MapGet("/register/history", async (Guid productId, Guid branchId,
            [FromServices] FixedAssetRegisterReport register, CancellationToken ct) =>
            Answer(await register.HistoryAsync(productId, branchId, ct)))
            .WithMetadata(new BranchScopedAttribute("a non-admin's branchId is replaced by their own branch (CallersBranch)"));

        group.MapGet("/depreciation", async (int? year, int? month, Guid? branchId,
            [FromServices] FixedAssetDepreciationReport report, CancellationToken ct) =>
            Answer(await report.MonthAsync(year, month, branchId, ct)))
            .WithMetadata(new BranchScopedAttribute("a non-admin's branchId is replaced by their own branch (CallersBranch)"));
        FinancePageRoutes.MapFixedAssetRegisterPage(group); FinancePageRoutes.MapFixedAssetHistoryPage(group); FinancePageRoutes.MapFixedAssetDepreciationPage(group);

        group.MapPost("/depreciation/post", async (PracticeLedgerMonthRequest r, ClaimsPrincipal caller,
            [FromServices] FixedAssetLedgerPostings postings, CancellationToken ct) =>
            Answer(await postings.PostAsync(r.Year, r.Month, BranchAssignment.IsCompanyScoped(caller), MovementAuthor.Of(caller).UserName, ct)));

        group.MapPost("/depreciation/reverse", async (PracticeLedgerMonthRequest r, ClaimsPrincipal caller,
            [FromServices] FixedAssetLedgerPostings postings, CancellationToken ct) =>
            Answer(await postings.ReverseAsync(r.Year, r.Month, BranchAssignment.IsCompanyScoped(caller), MovementAuthor.Of(caller).UserName, ct)));
    }

    private static IResult Answer<T>(FixedAssetResult<T> result) => result.Code is null
        ? Results.Ok(result.Value)
        : Results.Problem(detail: result.Error, statusCode: result.Status,
            extensions: new Dictionary<string, object?> { ["code"] = result.Code });

    private static IResult CompanyWide() => Results.Problem(
        detail: "The switch-over moves every branch's hire equipment: it is read and posted by a user who sees every branch.",
        statusCode: StatusCodes.Status403Forbidden, extensions: new Dictionary<string, object?> { ["code"] = CompanyWideCode });
}
