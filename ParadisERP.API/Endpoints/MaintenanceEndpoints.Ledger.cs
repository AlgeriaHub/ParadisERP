using ParadisERP.Application.Contracts;
using ParadisERP.Infrastructure.Auth;
using ParadisERP.Infrastructure.Authorization;
using ParadisERP.Infrastructure.Maintenance;
using Microsoft.AspNetCore.Mvc;

namespace ParadisERP.API.Endpoints;

/// <summary>
/// Phase M6: the account map's maintenance slots and the maintenance practice ledger (docs/maintenance/plan.md §5).
/// <para>
/// The account map (MS-31) sits beside the settings, reachable while the module is off like them: read with the settings' keys,
/// changed under accounting.manage. The practice ledger is under the jobs group (409 <c>maintenance_off</c> while MS-01 is off):
/// read with reports.accounting or maintenance.costs.view, a non-admin's <c>branchId</c> being their own branch (CallersBranch);
/// labour money at payroll rates in the entries is withheld in the service from anyone without pay-rate access. A month is
/// posted or reversed under accounting.manage, by a caller who reads every branch.
/// </para>
/// </summary>
public static partial class MaintenanceEndpoints
{
    private static void MapAccountMap(RouteGroupBuilder group)
    {
        group.MapGet("/account-map", async ([FromServices] MaintenanceAccountMap map, CancellationToken ct) =>
            Results.Ok(await map.MapAsync(ct)));

        group.MapPut("/account-map", async (UpdateManufacturingAccountMapRequest r, HttpContext http,
            [FromServices] MaintenanceAccountMap map, CancellationToken ct) =>
        {
            var (saved, error) = await map.UpdateAsync(r, MovementAuthor.Of(http.User).UserName, ct);
            return error is null ? Results.Ok(saved)
                : Results.Problem(detail: error, statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["code"] = "invalid_account_map" });
        });
    }

    private static void MapLedger(RouteGroupBuilder jobs)
    {
        jobs.MapGet("/practice-ledger", async (int? year, int? month, Guid? branchId, HttpContext http,
            [FromServices] MaintenancePracticeLedgerReport ledger, CancellationToken ct) =>
            Answer(await ledger.MonthAsync(year, month, branchId, key => ServerAuthorization.Has(http.User, key), ct)))
            .WithMetadata(new BranchScopedAttribute("a non-admin's branchId is replaced by their own branch (CallersBranch)"));

        jobs.MapPost("/practice-ledger/post", async (PracticeLedgerMonthRequest r, HttpContext http,
            [FromServices] MaintenanceLedgerPostings postings, CancellationToken ct) =>
            Answer(await postings.PostAsync(r.Year, r.Month, key => ServerAuthorization.Has(http.User, key),
                BranchAssignment.IsCompanyScoped(http.User), MovementAuthor.Of(http.User).UserName, ct)));

        jobs.MapPost("/practice-ledger/reverse", async (PracticeLedgerMonthRequest r, HttpContext http,
            [FromServices] MaintenanceLedgerPostings postings, CancellationToken ct) =>
            Answer(await postings.ReverseAsync(r.Year, r.Month, key => ServerAuthorization.Has(http.User, key),
                BranchAssignment.IsCompanyScoped(http.User), MovementAuthor.Of(http.User).UserName, ct)));
    }
}
