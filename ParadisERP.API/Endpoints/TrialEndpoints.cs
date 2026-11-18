using System.Text.RegularExpressions;
using ParadisERP.Application.Regional;
using ParadisERP.Application.Subscriptions;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Infrastructure.Subscriptions;
using ParadisERP.Infrastructure.Tenancy;

namespace ParadisERP.API.Endpoints;

public record TrialSignupRequest(
    string Slug,
    string? StoreName,
    string? AdminEmail,
    string? Country);

public record TrialSignupResponse(
    string Slug,
    string Host,
    string LoginUrl,
    string AdminUsername,
    string BootstrapPassword,
    DateTimeOffset ExpiresAtUtc);

public static partial class TrialEndpoints
{
    private static readonly HashSet<string> ReservedSubdomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "api", "admin", "app", "www", "mail", "platform", "login", "register",
        "paradiserp", "test", "demo", "auth", "pos", "status", "docs", "help",
        "support", "dashboard", "portal", "cloud", "billing", "host", "staging"
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,28}[a-z0-9]$")]
    private static partial Regex SlugPattern();

    public const string DefaultDomainSuffix = "erp.paradistack.com";

    public static void MapTrialEndpoints(this IEndpointRouteBuilder app, Identity.PlatformOptions options)
    {
        if (!options.Enabled) return;

        var domainSuffix = !string.IsNullOrWhiteSpace(options.Host) ? options.Host : DefaultDomainSuffix;

        // Dedicated public endpoint for self-service trial provisioning
        app.MapPost("/api/trials", async (TrialSignupRequest request, CompanyProvisioner provisioner, CancellationToken ct) =>
        {
            if (request is null)
                return Results.BadRequest(new { error = "Signup details are required." });

            var slug = (request.Slug ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(slug))
                return Results.BadRequest(new { error = "Subdomain slug is required." });

            if (slug.Length is < 3 or > 30 || !SlugPattern().IsMatch(slug))
                return Results.BadRequest(new { error = "Slug must be 3-30 lowercase characters (letters, numbers, hyphens) and cannot start or end with a hyphen." });

            if (ReservedSubdomains.Contains(slug))
                return Results.BadRequest(new { error = $"The subdomain '{slug}' is reserved. Please choose another." });

            var country = string.IsNullOrWhiteSpace(request.Country) ? "DZ" : request.Country.Trim().ToUpperInvariant();
            if (RegionalPresets.Create(country) is null)
                return Results.BadRequest(new { error = $"Invalid country code. Supported: {string.Join(", ", RegionalPresets.Countries)}." });

            var adminUsername = string.IsNullOrWhiteSpace(request.AdminEmail) ? "admin" : request.AdminEmail.Trim();
            var host = $"{slug}.{domainSuffix}";

            var trialSubscription = new SubscriptionOptions
            {
                Plan = "Trial",
                Status = "Trial",
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(14),
                GraceDays = 3,
                MaxUsers = 3,
                MaxBranches = 1,
                MaxDevices = 2
            };

            try
            {
                var created = await provisioner.CreateAsync(new CreateCompanyRequest(
                    Id: slug,
                    Hosts: [host],
                    DatabaseProvider: PosDbProviders.Sqlite,
                    ConnectionString: null,
                    Country: country,
                    AdminUsername: adminUsername,
                    Subscription: trialSubscription,
                    Reason: "Public self-service trial signup"
                ), actor: "public-trial", ct);

                var response = new TrialSignupResponse(
                    Slug: created.Id,
                    Host: host,
                    LoginUrl: $"https://{host}/#/login",
                    AdminUsername: created.AdminUsername,
                    BootstrapPassword: created.BootstrapPassword,
                    ExpiresAtUtc: trialSubscription.ExpiresAtUtc.Value
                );

                return Results.Created($"/api/trials/{created.Id}", response);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).AllowAnonymous().DisableAntiforgery();
    }
}
