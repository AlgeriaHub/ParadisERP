using ParadiERP.API.Authorization;
using ParadiERP.API.Identity;
using ParadiERP.API.Security;

namespace ParadiERP.API.Endpoints;

/// <summary>
/// Every route the API serves, mapped under one group that carries <see cref="RoutePermissions"/>
/// (AUTH-1). Program and the route-inventory tests both call this, so what the tests inspect is
/// exactly what is served.
/// </summary>
public static class ApiEndpoints
{
    public static RouteGroupBuilder MapApi(this IEndpointRouteBuilder app, SsoOptions sso, PlatformOptions platform)
    {
        // Every route also gets its request limit and body size (RequestLimits); a route added
        // later is limited by default rather than forgotten.
        var limits = app.ServiceProvider.GetService<RequestLimits.Options>() ?? new RequestLimits.Options();
        var api = app.MapGroup("").WithRoutePermissions().WithRequestLimits(limits);
        api.MapAuthEndpoints();
        api.MapAccountEndpoints();
        api.MapSyncEndpoints();
        api.MapCatalogEndpoints();
        api.MapBranchEndpoints();
        api.MapDevicesEndpoints();
        api.MapSalesEndpoints();
        api.MapUsersEndpoints();
        api.MapRolesEndpoints();
        api.MapGiftCardEndpoints();
        api.MapTreasuryEndpoints();
        api.MapStockEndpoints();
        api.MapReportEndpoints();
        api.MapValuationEndpoints();
        api.MapManufacturingAccountingEndpoints();
        api.MapMaintenanceEndpoints();
        api.MapFixedAssetEndpoints();
        api.MapStockTransferEndpoints();
        api.MapLedgerEndpoints();
        api.MapRentalEndpoints();
        api.MapCustomerSiteEndpoints();
        api.MapPaymentVoucherEndpoints();
        api.MapSupplierEndpoints();
        api.MapReceivableEndpoints();
        api.MapHrEndpoints();
        api.MapHrEmployeeEndpoints();
        api.MapHrAttendanceEndpoints();
        api.MapHrOvertimeEndpoints();
        api.MapHrPayrollEndpoints();
        api.MapHrMoneyEndpoints();
        api.MapManufacturingEndpoints();
        api.MapManufacturingOperationEndpoints();
        api.MapSubscriptionEndpoints();
        api.MapSsoEndpoints(sso, platform);
        api.MapPlatformEndpoints(platform);
        api.MapCompanyProvisioningEndpoints(platform);
        api.MapTrialEndpoints(platform);
        api.MapIntegrationEndpoints(platform);
        return api;
    }
}
