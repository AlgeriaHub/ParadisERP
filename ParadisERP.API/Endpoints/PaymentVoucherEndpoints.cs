using System.Security.Claims;
using ParadisERP.API.Subscriptions;
using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Auth;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Infrastructure.Receivables;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.API.Endpoints;

/// <summary>
/// Payment vouchers: money going out, the mirror of the receipt voucher.
///
/// <para>
/// Back-office routes under the same policy as the treasury: a voucher moves a branch's cash
/// box and posts to the ledger, both server-owned, so none of this is reachable from a till and
/// <c>PaymentVoucher</c> is not a <c>SyncEntity</c>. A pending voucher moves nothing — like a
/// pending receipt, it is paper until the money actually leaves.
/// </para>
/// <para>
/// Every write answers the same envelope, <see cref="PaymentVoucherResponse"/>: the voucher and
/// any warning about it. A short box and a refund partly taken off the debt are warnings beside
/// a recorded voucher, never a refusal in place of one.
/// </para>
/// </summary>
public static class PaymentVoucherEndpoints
{
    public static void MapPaymentVoucherEndpoints(this IEndpointRouteBuilder app)
    {
        // Raising, paying and cancelling: whoever holds the branch's cash box (answer 25).
        var group = app.MapGroup("/api/payment-vouchers").WithTags("Payment vouchers")
            .AddEndpointFilter<SubscriptionWriteFilter>()
            .AtCallersBranch();
        // Approving: the manager's alone — the person who pays is not the one who decides it is owed.
        var approvals = app.MapGroup("/api/payment-vouchers").WithTags("Payment vouchers")
            .AddEndpointFilter<SubscriptionWriteFilter>()
            .AtCallersBranch();

        group.MapGet("/", async (
            Guid? branchId, Guid? customerId, Guid? returnNoteId, int? status, int? kind, int? take,
            PosDbContext db, CancellationToken ct) =>
        {
            var query = db.PaymentVouchers.AsNoTracking();
            if (branchId is Guid b) query = query.Where(v => v.BranchId == b);
            if (customerId is Guid c) query = query.Where(v => v.CustomerId == c);
            if (returnNoteId is Guid n) query = query.Where(v => v.ReturnNoteId == n);
            if (status is int s && Enum.IsDefined((PaymentVoucherStatus)s)) query = query.Where(v => v.Status == (PaymentVoucherStatus)s);
            if (kind is int k && Enum.IsDefined((PaymentVoucherKind)k)) query = query.Where(v => v.Kind == (PaymentVoucherKind)k);

            var vouchers = await query
                .OrderByDescending(v => v.CreatedAtUtc)
                .Take(Math.Clamp(take ?? 200, 1, 1000))
                .ToListAsync(ct);
            return Results.Ok(await MapAsync(vouchers, db, ct));
        });
        SalesPageRoutes.MapPaymentVoucherPage(group);

        // Return notes with a refund still owed as a voucher — the picker on the create form, and
        // what `?returnNote=<id>` prefills from. A note asked for by id comes back even when
        // nothing is left on it, so the form can say so. branchId is the caller's own branch for
        // everyone but the Admin (CallersBranch): before P0.1b this listed every branch's notes.
        group.MapGet("/refundable", async (
            Guid? customerId, Guid? returnNoteId, Guid? branchId, PaymentVouchers vouchers, CancellationToken ct) =>
            Results.Ok(await vouchers.RefundableAsync(customerId, returnNoteId, ct, branchId)));

        // Contracts whose unused period may still be refunded, whatever their return notes did with
        // it (owner, 2026-10-06) — the contract picker of a refund with no note, and what
        // `?contract=<id>` prefills from.
        // branchId is the caller's own branch for everyone but the Admin (CallersBranch, security review 2026-10-10).
        group.MapGet("/refundable-contracts", async (
            Guid? customerId, Guid? contractId, Guid? branchId, PaymentVouchers vouchers, CancellationToken ct) =>
            Results.Ok(await vouchers.RefundableContractsAsync(customerId, contractId, ct, branchId)));

        group.MapGet("/{id:guid}", async (
            Guid id, PaymentVouchers vouchers, PosDbContext db, ClaimsPrincipal caller, CancellationToken ct) =>
        {
            if (await HiddenFromAsync(id, caller, db, ct)) return Results.NotFound();
            var voucher = await vouchers.ReadAsync(id, ct);
            if (voucher is null) return Results.NotFound();
            return Results.Ok((await MapAsync([voucher], db, ct)).Single());
        }).ForBranchOf(DocumentBranches.Voucher).WithMetadata(new BranchScopedAttribute(HiddenReason));

        group.MapPost("/", async (
            CreatePaymentVoucherRequest r, PaymentVouchers vouchers, PosDbContext db, HttpContext http,
            CancellationToken ct) =>
        {
            var result = await vouchers.CreateAsync(r, UserName(http), ct,
                sameBranchOnly: !BranchAssignment.IsCompanyScoped(http.User));
            if (!result.Ok) return Problem(result);
            var response = await RespondAsync(result, db, ct);
            return Results.Created($"/api/payment-vouchers/{response.Voucher.Id}", response);
        });

        // The workers a workers' bonus may name (owner, 2026-11-08): the active staff of the voucher's branch, number and
        // name only — the clerk raising a voucher needs no HR access to pick them, and sees no pay. branchId is the
        // caller's own branch for everyone but the Admin (CallersBranch).
        group.MapGet("/bonus-workers", async (Guid? branchId, PosDbContext db, CancellationToken ct) =>
            branchId is not Guid branch
                ? Results.Ok(Array.Empty<BonusWorkerDto>())
                : Results.Ok(await db.Employees.AsNoTracking().Where(e => e.BranchId == branch && e.IsActive)
                    .OrderBy(e => e.FullName).Select(e => new BonusWorkerDto(e.Id, e.Number, e.FullName)).ToListAsync(ct)))
            .WithMetadata(new BranchScopedAttribute("a branch user lists their own branch's workers (CallersBranch)"));

        // The categories and which of them must name a contract (answer 26): read by whoever
        // raises a voucher, set by a manager.
        group.MapGet("/categories", async (PaymentVouchers vouchers, CancellationToken ct) =>
            Results.Ok(await vouchers.CategoriesAsync(ct)));

        approvals.MapPut("/categories", async (ExpenseCategoriesRequest r, PaymentVouchers vouchers, CancellationToken ct) =>
        {
            var result = await vouchers.SetContractCategoriesAsync(r.ContractRequired, ct);
            return result.Ok ? Results.Ok(result.Value) : Results.BadRequest(result.Message);
        });

        approvals.MapPost("/{id:guid}/approve", async (
            Guid id, PaymentVoucherDecisionRequest? r, PaymentVouchers vouchers, PosDbContext db, HttpContext http,
            CancellationToken ct) =>
        {
            var result = await vouchers.ApproveAsync(id, r?.Note, Actor(http), ct);
            return result.Ok ? Results.Ok(await RespondAsync(result, db, ct)) : Problem(result);
        }).ForBranchOf(DocumentBranches.Voucher);

        group.MapPost("/{id:guid}/pay", async (
            Guid id, PaymentVoucherDecisionRequest? r, PaymentVouchers vouchers, PosDbContext db, HttpContext http,
            CancellationToken ct) =>
        {
            if (await HiddenFromAsync(id, http.User, db, ct)) return Results.NotFound();
            var result = await vouchers.PayAsync(id, r?.Note, Actor(http), ct);
            return result.Ok ? Results.Ok(await RespondAsync(result, db, ct)) : Problem(result);
        }).ForBranchOf(DocumentBranches.Voucher).WithMetadata(new BranchScopedAttribute(HiddenReason));

        group.MapPost("/{id:guid}/cancel", async (
            Guid id, PaymentVoucherDecisionRequest? r, PaymentVouchers vouchers, PosDbContext db, HttpContext http,
            CancellationToken ct) =>
        {
            if (await HiddenFromAsync(id, http.User, db, ct)) return Results.NotFound();
            var result = await vouchers.CancelAsync(id, r?.Note, ct);
            return result.Ok ? Results.Ok(await RespondAsync(result, db, ct)) : Problem(result);
        }).ForBranchOf(DocumentBranches.Voucher).WithMetadata(new BranchScopedAttribute(HiddenReason));
    }

    private static async Task<PaymentVoucherResponse> RespondAsync(
        ReceivableResult<(PaymentVoucher Voucher, Warned? Warning)> result, PosDbContext db, CancellationToken ct)
    {
        var (voucher, warning) = result.Value;
        var dto = (await MapAsync([voucher], db, ct)).Single();
        return new PaymentVoucherResponse(dto, warning?.Sentence, warning?.Code, warning?.Args);
    }

    /// <summary>The voucher with the names the screen and the paper print resolved, in three set queries.</summary>
    private static async Task<List<PaymentVoucherDto>> MapAsync(
        IReadOnlyList<PaymentVoucher> vouchers, PosDbContext db, CancellationToken ct)
    {
        var branchIds = vouchers.Select(v => v.BranchId).Distinct().ToList();
        var branches = await db.Branches.AsNoTracking()
            .Where(b => branchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.NameFr, ct);

        var customerIds = vouchers.Where(v => v.CustomerId != null).Select(v => v.CustomerId!.Value).Distinct().ToList();
        var customers = await db.Customers.AsNoTracking()
            .Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.FullName, ct);

        var keys = vouchers.Select(v => v.MethodKey).Distinct().ToList();
        var methods = await db.PaymentMethodDefs.AsNoTracking()
            .Where(m => keys.Contains(m.Key)).ToDictionaryAsync(m => m.Key, m => m.NameFr, ct);

        var contractIds = vouchers.Where(v => v.ContractId != null).Select(v => v.ContractId!.Value).Distinct().ToList();
        var contracts = await db.RentalContracts.AsNoTracking()
            .Where(c => contractIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Number, ct);

        var noteIds = vouchers.Where(v => v.ReturnNoteId != null).Select(v => v.ReturnNoteId!.Value).Distinct().ToList();
        var notes = await db.ReturnNotes.AsNoTracking()
            .Where(n => noteIds.Contains(n.Id))
            .Select(n => new { n.Id, n.Number, n.UnusedRent, n.ReliefDebtReduction })
            .ToDictionaryAsync(n => n.Id, ct);

        // Workers' bonuses (owner, 2026-11-08): who received what, and the run that put each on a payslip.
        var voucherIds = vouchers.Select(v => v.Id).ToList();
        var bonusLines = await db.PaymentVoucherBonusLines.AsNoTracking().Where(l => voucherIds.Contains(l.VoucherId)).ToListAsync(ct);
        var runIds = bonusLines.Where(l => l.RunId != null).Select(l => l.RunId!.Value).Distinct().ToList();
        var runs = runIds.Count == 0 ? [] : await db.PayrollRuns.AsNoTracking().Where(r => runIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Number, r.Status }).ToDictionaryAsync(r => r.Id, ct);
        var bonusesBy = bonusLines.GroupBy(l => l.VoucherId).ToDictionary(g => g.Key, g => g
            .OrderBy(l => l.EmployeeNameSnapshot, StringComparer.Ordinal)
            .Select(l =>
            {
                var run = l.RunId is Guid rid ? runs.GetValueOrDefault(rid) : null;
                return new PaymentVoucherBonusLineDto(l.EmployeeId, l.EmployeeNumberSnapshot, l.EmployeeNameSnapshot, l.Amount,
                    l.PayrollYear, l.PayrollMonth, run?.Id, run?.Number,
                    run is not null && (run.Status == PayrollRunStatus.Approved || run.Status == PayrollRunStatus.Paid));
            }).ToList());

        return vouchers.Select(v =>
        {
            var note = v.ReturnNoteId is Guid nid ? notes.GetValueOrDefault(nid) : null;
            return new PaymentVoucherDto(
                v.Id, v.Number, v.BranchId, branches.GetValueOrDefault(v.BranchId) ?? "",
                v.Kind,
                v.CustomerId, v.CustomerId is Guid cid ? customers.GetValueOrDefault(cid) : null,
                v.PayeeName, v.ExpenseCategory, v.ExpenseAccountCode,
                v.Amount, v.MethodKey, methods.GetValueOrDefault(v.MethodKey) ?? v.MethodKey,
                v.InstrumentNumber, v.InstrumentBank,
                v.Purpose,
                v.ContractId, v.ReturnNoteId, v.UnusedDays, v.DeductedFromDebt,
                v.Status,
                v.RequestedBy, v.CreatedAtUtc,
                v.ApprovedByUserId, v.ApprovedByName, v.ApprovedAtUtc,
                v.PaidAtUtc, v.PaidByName,
                v.CancelledAtUtc,
                v.StatusNote, v.Notes,
                v.ContractId is Guid kid ? contracts.GetValueOrDefault(kid) : null,
                note?.Number, note?.UnusedRent, note?.ReliefDebtReduction,
                SupplierId: v.SupplierId,
                BonusLines: bonusesBy.GetValueOrDefault(v.Id));
        }).ToList();
    }

    private const string HiddenReason =
        "a cash-box cashier reads, pays and cancels their own branch's vouchers only; another branch's is not found, not forbidden — a 403 would say it exists";

    /// <summary>
    /// Whether this voucher is out of the caller's sight (FR-2A, the owner, 2026-10-03). A cashier
    /// who holds the cash box works at one branch — the branch of the till they signed in at
    /// (<see cref="BranchScope.ForAsync"/>, the rule <c>GET /api/sales/{id}</c> already follows) —
    /// and another branch's voucher is <b>not found</b> for them, whether they read it, pay it or
    /// cancel it: a 403 would say it exists. Since P0.1b a branch manager is held to their own
    /// branch as well — by <see cref="CallersBranch.ForBranchOf{TBuilder}"/> on these routes and by
    /// <see cref="BranchScope.ForAsync"/> here; the Admin reads every branch. An unknown id is left to
    /// the handler, which answers the same bodiless 404.
    /// </summary>
    private static async Task<bool> HiddenFromAsync(Guid id, ClaimsPrincipal caller, PosDbContext db, CancellationToken ct)
    {
        if (await BranchScope.ForAsync(caller, db, ct) is not Guid own) return false;
        var branch = await db.PaymentVouchers.AsNoTracking()
            .Where(v => v.Id == id)
            .Select(v => (Guid?)v.BranchId)
            .FirstOrDefaultAsync(ct);
        return branch is Guid b && b != own;
    }

    private static string? UserName(HttpContext http) =>
        http.User.FindFirstValue("name") ?? http.User.Identity?.Name;

    private static VoucherActor Actor(HttpContext http) => new(
        Guid.TryParse(http.User.FindFirstValue("sub") ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null,
        UserName(http),
        // AUTH-1: one's own voucher needs approve_own as well as approve (the route asks approve).
        ParadisERP.Infrastructure.Authorization.ServerAuthorization.Has(http.User, PermissionCatalog.VouchersApproveOwn));

    private static IResult Problem<T>(ReceivableResult<T> result) => result.Failure switch
    {
        ReceivableFailure.NotFound => Results.NotFound(result.Message),
        ReceivableFailure.Conflict => Results.Conflict(result.Message),
        _ => Results.BadRequest(result.Message)
    };
}
