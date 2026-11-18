using System.Security.Claims;
using ParadisERP.API.Identity;
using ParadisERP.Infrastructure.Subscriptions;
using ParadisERP.Infrastructure.Tenancy;
using Microsoft.AspNetCore.DataProtection;

namespace ParadisERP.API.Endpoints;

public record CreateCompanyBody(
    string Id, string[] Hosts, string DatabaseProvider, string? ConnectionString,
    string Country, string AdminUsername, SubscriptionOptions Subscription, string Reason);
public record UpdateHostsRequest(string[] Hosts, long Revision, string Reason);
public record CompanyStateRequest(long Revision, string Reason);

/// <summary>Operator routes that create and change portal-provisioned companies without a restart.</summary>
public static class CompanyProvisioningEndpoints
{
    public static void MapCompanyProvisioningEndpoints(this IEndpointRouteBuilder app, PlatformOptions options)
    {
        if (!options.Enabled) return;
        var group = PlatformEndpoints.MapPlatformGroup(app);

        group.MapPost("/companies", async (CreateCompanyBody body, ClaimsPrincipal caller, CompanyProvisioner provisioner, CancellationToken ct) =>
        {
            if (body is null) return Results.BadRequest("Company details are required.");
            try
            {
                var created = await provisioner.CreateAsync(new CreateCompanyRequest(body.Id, body.Hosts, body.DatabaseProvider,
                    body.ConnectionString, body.Country, body.AdminUsername, body.Subscription, body.Reason), Actor(caller), ct);
                return Results.Created($"/api/platform/companies/{created.Id}", created);
            }
            catch (ArgumentException exception) { return Results.BadRequest(exception.Message); }
            catch (InvalidOperationException exception) { return Results.Conflict(exception.Message); }
        });

        group.MapPut("/companies/{id}/hosts", async (string id, UpdateHostsRequest body, ClaimsPrincipal caller,
            CompanyProvisioner provisioner, CancellationToken ct) =>
            await Change(() => provisioner.UpdateHostsAsync(id, body.Hosts, body.Revision, Actor(caller), body.Reason, ct)));

        group.MapPost("/companies/{id}/archive", async (string id, CompanyStateRequest body, ClaimsPrincipal caller,
            CompanyProvisioner provisioner, CancellationToken ct) =>
            await Change(() => provisioner.SetArchivedAsync(id, true, body.Revision, Actor(caller), body.Reason, ct)));

        group.MapPost("/companies/{id}/restore", async (string id, CompanyStateRequest body, ClaimsPrincipal caller,
            CompanyProvisioner provisioner, CancellationToken ct) =>
            await Change(() => provisioner.SetArchivedAsync(id, false, body.Revision, Actor(caller), body.Reason, ct)));
    }

    private static async Task<IResult> Change(Func<Task<bool>> change)
    {
        try
        {
            return await change() ? Results.NoContent() : Results.Conflict("Another operator changed this company. Reload before saving.");
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException exception) { return Results.BadRequest(exception.Message); }
    }

    private static string Actor(ClaimsPrincipal caller) => caller.FindFirstValue("sub")!;
}

public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector protector = provider.CreateProtector("ParadisERP.Companies");

    public string Protect(string value) => protector.Protect(value);

    public string Unprotect(string value) => protector.Unprotect(value);
}
