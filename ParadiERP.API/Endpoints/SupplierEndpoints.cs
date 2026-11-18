using System.Security.Claims;
using ParadiERP.API.Subscriptions;
using ParadiERP.Application.Contracts;
using ParadiERP.Application.Customers;
using ParadiERP.Application.Regional;
using ParadiERP.Application.Security;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Auth;
using ParadiERP.Infrastructure.Catalog;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Suppliers;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.API.Endpoints;

/// <summary>
/// The suppliers (owner, 2026-10-10: a basic suppliers module): the list and the card, the opening
/// balance and its corrections, and the statement. Receiving goods and paying a supplier are the
/// stock and voucher routes, which name a supplier.
///
/// <para>
/// Whoever receives goods or raises payment vouchers keeps the supplier list (RoutePermissions);
/// setting an opening balance after registration, and correcting one, is
/// <c>suppliers.opening_balance</c> — the customers' rules exactly.
/// </para>
/// </summary>
public static class SupplierEndpoints
{
    public static void MapSupplierEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/suppliers").WithTags("Suppliers")
            .AddEndpointFilter<SubscriptionWriteFilter>()
            .AtCallersBranch();

        group.MapGet("/", async (PosDbContext db, CancellationToken ct) =>
        {
            var rows = await db.Suppliers.AsNoTracking().Where(s => !s.IsDeleted).OrderBy(s => s.Name).ToListAsync(ct);
            return Results.Ok(rows.Select(SupplierAccounts.ToDto).ToList());
        });

        group.MapPost("/", async (
            UpsertSupplierRequest r, PosDbContext db, HttpContext http, SupplierAccounts accounts,
            IRegionalSettingsProvider regional, CancellationToken ct) =>
        {
            var entity = new Supplier();
            if (Apply(entity, r, regional.Get()) is string problem) return Results.BadRequest(problem);
            var opening = r.OpeningBalance is { Amount: not 0m } given ? given : null;
            if (opening is not null && accounts.Problem(opening) is string openingProblem) return Results.BadRequest(openingProblem);
            if (await CallerBranchAsync(http, db, ct) is not Guid branchId) return Results.BadRequest(BranchLock.NoBranchMessage);

            await using var tx = await db.BeginWriteScopeAsync(ct);
            await db.LockVersionsAsync(ct);
            entity.Code = await accounts.NextCodeAsync(ct);
            db.Suppliers.Add(entity);
            if (opening is not null) await accounts.ApplyOpeningAsync(entity, opening, branchId, UserName(http), ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/suppliers/{entity.Id}", SupplierAccounts.ToDto(entity));
        });

        group.MapPut("/{id:guid}", async (
            Guid id, UpsertSupplierRequest r, PosDbContext db, IRegionalSettingsProvider regional, CancellationToken ct) =>
        {
            var entity = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted, ct);
            if (entity is null) return Results.NotFound("Unknown supplier.");
            // The opening balance is never sent through an edit; it has its own route, once.
            if (Apply(entity, r with { OpeningBalance = null }, regional.Get()) is string problem) return Results.BadRequest(problem);
            entity.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(SupplierAccounts.ToDto(entity));
        });

        group.MapDelete("/{id:guid}", async (Guid id, PosDbContext db, CancellationToken ct) =>
        {
            var entity = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted, ct);
            if (entity is null) return Results.NotFound("Unknown supplier.");
            // Soft: receiving notes and vouchers name it as history.
            entity.IsDeleted = true;
            entity.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapPost("/{id:guid}/opening-balance", async (
            Guid id, CustomerOpeningBalanceRequest r, PosDbContext db, HttpContext http, SupplierAccounts accounts,
            CancellationToken ct) =>
        {
            if (accounts.Problem(r) is string problem) return Results.BadRequest(problem);
            if (await CallerBranchAsync(http, db, ct) is not Guid branchId) return Results.BadRequest(BranchLock.NoBranchMessage);

            await using var tx = await db.BeginWriteScopeAsync(ct);
            var supplier = await db.LockedAsync<Supplier>(id, ct);
            if (supplier is null || supplier.IsDeleted) return Results.NotFound("Unknown supplier.");
            if (supplier.OpeningBalanceSetAtUtc is not null)
                return Results.Conflict("This supplier's opening balance is already set. A wrong figure is put right with a correction and its reason.");

            await accounts.ApplyOpeningAsync(supplier, r, branchId, UserName(http), ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(SupplierAccounts.ToDto(supplier));
        });

        group.MapPost("/{id:guid}/opening-corrections", async (
            Guid id, OpeningCorrectionRequest r, PosDbContext db, HttpContext http, SupplierAccounts accounts,
            CancellationToken ct) =>
        {
            if (OpeningBalanceRules.ReasonProblem(r.Reason) is string reasonProblem) return Results.BadRequest(reasonProblem);
            if (accounts.Problem(new CustomerOpeningBalanceRequest(r.Amount, r.Direction, r.On)) is string problem)
                return Results.BadRequest(problem);
            if (await CallerBranchAsync(http, db, ct) is not Guid branchId) return Results.BadRequest(BranchLock.NoBranchMessage);

            await using var tx = await db.BeginWriteScopeAsync(ct);
            var supplier = await db.LockedAsync<Supplier>(id, ct);
            if (supplier is null || supplier.IsDeleted) return Results.NotFound("Unknown supplier.");
            if (supplier.OpeningBalanceSetAtUtc is null)
                return Results.Conflict("This supplier has no opening balance yet. Set it instead of correcting it.");

            var correction = await accounts.CorrectAsync(supplier, r, branchId, UserName(http), ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/suppliers/{id}/opening-corrections/{correction.Id}", SupplierAccounts.ToDto(correction));
        });

        group.MapGet("/{id:guid}/opening-corrections", async (Guid id, PosDbContext db, CancellationToken ct) =>
        {
            var rows = await db.SupplierOpeningCorrections.AsNoTracking()
                .Where(c => c.SupplierId == id).OrderBy(c => c.CreatedAtUtc).ToListAsync(ct);
            return Results.Ok(rows.Select(SupplierAccounts.ToDto).ToList());
        });

        group.MapGet("/{id:guid}/statement", async (
            Guid id, DateTime? from, DateTime? to, SupplierAccounts accounts, CancellationToken ct) =>
        {
            var start = from ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var end = to ?? start.AddMonths(1);
            if (end <= start) return Results.BadRequest("'to' must be after 'from'.");
            if ((end - start).TotalDays > Infrastructure.Ledger.LedgerReports.MaxPeriodDays)
                return Results.BadRequest($"Date range too wide — max {Infrastructure.Ledger.LedgerReports.MaxPeriodDays} days per request.");
            var statement = await accounts.StatementAsync(id, start, end, ct);
            return statement is null ? Results.NotFound("Unknown supplier.") : Results.Ok(statement);
        });
        SalesPageRoutes.MapSupplierPages(group);
    }

    /// <summary>Validates and writes the editable fields; the code, the balance and the opening balance are never taken from a body.</summary>
    private static string? Apply(Supplier entity, UpsertSupplierRequest r, RegionalSettings profile)
    {
        var name = (r.Name ?? "").Trim();
        if (name.Length == 0) return "A supplier needs a name.";
        if (name.Length > 200) return "The name is limited to 200 characters.";
        if (r.Phone?.Trim().Length > 60) return "The phone is limited to 60 characters.";
        if (r.Address?.Trim().Length > 400) return "The address is limited to 400 characters.";
        if (r.Note?.Trim().Length > 400) return "The note is limited to 400 characters.";
        if (r.FiscalIds is not null)
        {
            if (FiscalIdValues.TryNormalize(profile, r.FiscalIds, entity.FiscalIds, out var clean) is string problem) return problem;
            entity.FiscalIds = clean;
        }
        entity.Name = name;
        entity.Phone = TrimOrNull(r.Phone);
        entity.Address = TrimOrNull(r.Address);
        entity.Note = TrimOrNull(r.Note);
        return null;
    }

    private static string? TrimOrNull(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? UserName(HttpContext http) =>
        http.User.FindFirstValue("name") ?? http.User.Identity?.Name;

    /// <summary>The caller's own branch, or the main branch for a company-wide caller: where the entry is filed.</summary>
    private static async Task<Guid?> CallerBranchAsync(HttpContext http, PosDbContext db, CancellationToken ct) =>
        BranchAssignment.IsCompanyScoped(http.User)
            ? await BranchAssignment.MainBranchAsync(db, ct)
            : await BranchAssignment.ForCallerAsync(http.User, db, ct);
}
