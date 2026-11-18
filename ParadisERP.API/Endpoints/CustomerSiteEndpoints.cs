using ParadisERP.API.Subscriptions;
using ParadisERP.Application.Contracts;
using ParadisERP.Application.Rentals;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.API.Endpoints;

/// <summary>
/// A customer's sites — the places hired goods go to, named on the contract and the issue note.
///
/// <para>
/// Back-office routes under the same policy as renting (§7 decision 5): a site is part of the
/// rental paperwork, nothing on a till reads one, and <c>CustomerSite</c> is not a
/// <c>SyncEntity</c>, so none of this replicates.
/// </para>
/// <para>
/// Answer 9 (2026-09-30): the site is mandatory on every contract, and one customer may have
/// several. There is no DELETE: a contract points at its site, so a site nobody uses any more is
/// closed (<c>IsActive = false</c>) and stays readable on the papers that name it. A contract
/// snapshots the name and address, so renaming a site here never rewrites a signed paper.
/// </para>
/// </summary>
public static class CustomerSiteEndpoints
{
    public static void MapCustomerSiteEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/customers/{customerId:guid}/sites").WithTags("Customer sites")
            .AddEndpointFilter<SubscriptionWriteFilter>();

        // Every site of the customer, open ones first, then by name — closed ones stay listed
        // because a contract may still name them.
        group.MapGet("", async (Guid customerId, PosDbContext db, CancellationToken ct) =>
        {
            if (!await CustomerExistsAsync(db, customerId, ct)) return Results.NotFound("Customer not found.");
            var sites = await db.CustomerSites.AsNoTracking()
                .Where(s => s.CustomerId == customerId)
                .ToListAsync(ct);
            return Results.Ok(sites
                .OrderByDescending(s => s.IsActive).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(Map).ToList());
        });

        group.MapPost("", async (Guid customerId, UpsertCustomerSiteRequest r, PosDbContext db, CancellationToken ct) =>
        {
            if (!await CustomerExistsAsync(db, customerId, ct)) return Results.NotFound("Customer not found.");
            if (Problem(r) is string problem) return Results.BadRequest(problem);

            var site = new CustomerSite { CustomerId = customerId };
            Apply(site, r);
            db.CustomerSites.Add(site);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/customers/{customerId}/sites/{site.Id}", Map(site));
        });

        group.MapPut("/{siteId:guid}", async (
            Guid customerId, Guid siteId, UpsertCustomerSiteRequest r, PosDbContext db, CancellationToken ct) =>
        {
            if (!await CustomerExistsAsync(db, customerId, ct)) return Results.NotFound("Customer not found.");
            var site = await db.CustomerSites.FirstOrDefaultAsync(s => s.Id == siteId && s.CustomerId == customerId, ct);
            if (site is null) return Results.NotFound("Site not found.");
            if (Problem(r) is string problem) return Results.BadRequest(problem);

            Apply(site, r);
            await db.SaveChangesAsync(ct);
            return Results.Ok(Map(site));
        });
    }

    /// <summary>A soft-deleted customer is gone for the back office too: no new sites, no list.</summary>
    private static Task<bool> CustomerExistsAsync(PosDbContext db, Guid customerId, CancellationToken ct) =>
        db.Customers.AnyAsync(c => c.Id == customerId && !c.IsDeleted, ct);

    private static string? Problem(UpsertCustomerSiteRequest r) =>
        CustomerSiteRules.Problem(r.Name, r.Address, r.Phone) switch
        {
            null => null,
            "siteNameRequired" => "A site needs a name — the one the paper prints, such as the project or the street.",
            "siteNameTooLong" => $"A site's name is at most {CustomerSiteRules.NameMaxLength} characters.",
            "siteAddressTooLong" => $"A site's address is at most {CustomerSiteRules.AddressMaxLength} characters.",
            "sitePhoneTooLong" => $"A site's phone is at most {CustomerSiteRules.PhoneMaxLength} characters.",
            var code => code
        };

    private static void Apply(CustomerSite site, UpsertCustomerSiteRequest r)
    {
        site.Name = r.Name.Trim();
        site.Address = string.IsNullOrWhiteSpace(r.Address) ? null : r.Address.Trim();
        site.Phone = string.IsNullOrWhiteSpace(r.Phone) ? null : r.Phone.Trim();
        site.IsActive = r.IsActive;
    }

    private static CustomerSiteDto Map(CustomerSite s) => new(s.Id, s.CustomerId, s.Name, s.Address, s.Phone, s.IsActive);
}
