using System.Security.Claims;
using ParadisERP.API.Subscriptions;
using ParadisERP.Application.Contracts;
using ParadisERP.Application.Payroll;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Payroll;

namespace ParadisERP.API.Endpoints;

/// <summary>
/// Personnel and payroll. This part carries the settings only — the figures every later screen
/// computes against; employees, attendance, runs, advances and wage payments follow.
///
/// <para>
/// Back office, never a till: nothing under <c>/api/hr</c> has a reader on a cashier's device,
/// and nothing it touches derives from <c>SyncEntity</c>.
/// </para>
/// <para>
/// <b>Reading the figures is not editing them.</b> The settings GET asks for no named permission
/// beyond being a back-office user, because the screen has to be able to show the statutory half
/// to somebody who may not move it and say so in words — a control that simply vanishes teaches
/// nobody anything. Every write asks for its permission, and the GET tells the screen which
/// writes it will accept so the refusal is stated before the save rather than after it.
/// </para>
/// </summary>
public static class HrEndpoints
{
    public static void MapHrEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/hr").WithTags("HR")
            .AddEndpointFilter<SubscriptionWriteFilter>();

        MapSettings(group);
    }

    private static void MapSettings(RouteGroupBuilder group)
    {
        group.MapGet("/settings", async (
            PayrollSettingsStore store, HttpContext http, CancellationToken ct) =>
            Results.Ok(Map(await store.ReadAsync(ct), http)));

        // The company's own practice: allowances, overtime bands, deductions, the working day,
        // the attendance default. A statutory figure in this body is refused by name.
        group.MapPut("/settings/company", async (
                UpdateCompanyPayrollSettingsRequest body, PayrollSettingsStore store,
                HttpContext http, CancellationToken ct) =>
            {
                if (body?.Settings is null) return Results.BadRequest("Settings are required.");
                var result = await store.ApplyCompanyEditAsync(body.Settings, body.Revision, UserName(http), ct);
                return result.Ok ? Results.Ok(Map(result, http)) : Problem(result);
            });

        // The statutory half. docs/07 §5.2 kept this out of every company login's reach; the
        // owner's 2026-09-24 decision put it in the admin's hands on purpose, because the figures
        // ship as placeholders and somebody in the company has to be able to correct them. What
        // did not move is that the store reads only the operator-owned fields out of this body,
        // so a statutory save can never carry an allowance change in with it.
        group.MapPut("/settings/statutory", async (
                UpdateStatutoryPayrollSettingsRequest body, PayrollSettingsStore store,
                HttpContext http, CancellationToken ct) =>
            {
                if (body?.Settings is null) return Results.BadRequest("Settings are required.");
                var result = await store.ApplyStatutoryEditAsync(
                    body.Settings, body.Revision, body.Reviewed, body.ConfirmedBy, UserName(http), ct);
                return result.Ok ? Results.Ok(Map(result, http)) : Problem(result);
            });

        // "Our accountant has read these and they are right." It changes no figure — it is the
        // one action that takes the provisional mark off everything computed from here on.
        group.MapPost("/settings/statutory/confirm", async (
                ConfirmPayrollStatutoryRequest body, PayrollSettingsStore store,
                HttpContext http, CancellationToken ct) =>
            {
                if (body is null) return Results.BadRequest("A revision is required.");
                var result = await store.ConfirmStatutoryAsync(body.Revision, body.ConfirmedBy, UserName(http), ct);
                return result.Ok ? Results.Ok(Map(result, http)) : Problem(result);
            });
    }

    // ---------- shaping ----------

    private static PayrollSettingsDto Map(PayrollSettingsResult result, HttpContext http) =>
        new(result.Settings,
            result.Revision,
            // WireFields, not Fields: System.Text.Json renames properties and leaves dictionary
            // keys alone, so the PascalCase table went out beside camelCase properties and the
            // screen's lookups all missed. See PayrollOwnership.WireFields.
            PayrollOwnership.WireFields.ToDictionary(pair => pair.Key, pair => pair.Value.ToString()),
            Holds(http, Permissions.ManagePayroll),
            Holds(http, Permissions.EditPayrollStatutory),
            result.UpdatedBy,
            // MySQL hands a DATETIME back as Unspecified, which serialises with no 'Z' and is
            // then read by the browser as its own local time — the stamp beside "last saved by"
            // was hours out for anyone not sitting in the company's zone. It was written as UTC;
            // saying so costs nothing and is the only place that knows it.
            Utc(result.UpdatedAtUtc));

    /// <summary>A stamp this code stored as UTC, marked as UTC whatever the provider returned.</summary>
    private static DateTime? Utc(DateTime? value) =>
        value is null ? null
        : value.Value.Kind == DateTimeKind.Utc ? value
        : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);

    private static bool Holds(HttpContext http, string permission) =>
        http.User.HasClaim(Permissions.ClaimType, permission);

    private static string? UserName(HttpContext http) =>
        http.User.FindFirstValue("name") ?? http.User.Identity?.Name;

    /// <summary>
    /// Every refusal carries the sentence the screen shows. A stale revision is a 409 because it
    /// is a race, not a mistake — the screen reloads and the admin decides again.
    /// </summary>
    private static IResult Problem(PayrollSettingsResult result) => result.Outcome switch
    {
        PayrollSettingsOutcome.Stale => Results.Conflict(result.Message),
        _ => Results.BadRequest(result.Message)
    };
}
