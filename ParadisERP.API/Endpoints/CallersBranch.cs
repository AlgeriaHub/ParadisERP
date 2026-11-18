using ParadisERP.Application.Contracts;
using ParadisERP.Application.Security;
using ParadisERP.Infrastructure.Auth;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.API.Endpoints;

/// <summary>
/// Marks a route whose branch is the caller's for everyone but the Admin (<see cref="CallersBranch"/>).
/// Attached as metadata beside the filter so the endpoint inventory can assert which routes carry it.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Delegate, AllowMultiple = false)]
public sealed class CallersBranchAttribute : Attribute;

/// <summary>
/// The server half of <see cref="BranchLock"/>: "the branch choice does not appear except for
/// the super admin", enforced where a screen cannot be trusted to have hidden it.
///
/// <para>
/// Applied to a whole group; for each route it looks, once, at the handler's parameters and does
/// nothing at all on a route with none of these:
/// </para>
/// <list type="bullet">
/// <item>a request that raises a document at a branch (<see cref="IRaisedAtBranch"/>) — for a
/// non-admin its branch is <b>replaced</b> by the caller's, so a request naming another branch
/// still lands on theirs;</item>
/// <item>a request paid from a named box (<see cref="IPaidFromBox"/>) — for a non-admin a box of
/// another branch is refused in words (400), since a box cannot be swapped for another;</item>
/// <item>a query parameter named <c>branchId</c> on a list or a report — for a non-admin it is
/// <b>set</b> to their branch, so "all branches" or another branch reads as theirs.</item>
/// </list>
/// <para>
/// The Admin's request is left as sent, except that a branch he names must exist (400
/// "Unknown branch."). The caller's branch is read from their user row by the token's
/// <c>sub</c>; a legacy row with no branch works at the main branch.
/// </para>
/// <para>
/// What this does <b>not</b> do: a read or a write by id (<c>…/{id}</c>) is not scoped here. Each
/// such route carries <see cref="ForBranchOf{TBuilder}"/>, or decides in its handler where one id
/// is not enough — a sale through <c>BranchScope</c>, a journal entry by its lines (P0.1b).
/// </para>
/// </summary>
public static class CallersBranch
{
    public static TBuilder AtCallersBranch<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new CallersBranchAttribute());
        builder.AddEndpointFilterFactory((factory, next) =>
        {
            var plan = Plan.For(factory.MethodInfo.GetParameters());
            if (plan.IsEmpty) return next;
            return async context => await ApplyAsync(context, plan) ?? await next(context);
        });
        return builder;
    }

    /// <summary>
    /// The by-id half (security review, 2026-10-10): a write on one document named by its
    /// <c>{id}</c> — a contract changed or cancelled, a voucher approved or paid, a receipt
    /// confirmed, a ledger entry reversed — is answered 404 for a caller without company scope when
    /// the document belongs to another branch, as if it did not exist. Before, the lock covered
    /// documents raised, lists and reports, and any id a list let slip could be acted on.
    /// <paramref name="branchOf"/> reads the document's branch: <c>null</c> when there is no such
    /// document (the handler then says so itself), <see cref="Guid.Empty"/> for one that belongs to
    /// no branch, which only the company's roles may touch.
    /// <para>
    /// Reads by id take it too (P0.1, 2026-10-25): a payroll run, its payslips or an employee's file
    /// read by id is as much another branch's as one changed by id. <paramref name="routeKey"/> names
    /// the route value holding the id, for a route that calls it something else (<c>{runId}</c>).
    /// Every other document read by id joined them in P0.1b — contracts and what hangs off them,
    /// invoices, credit notes, receipts and vouchers — with the same answer: 404 and no body, exactly
    /// what an id nobody holds gets, so the answer says nothing about whether the document exists.
    /// </para>
    /// <para>
    /// Closed when in doubt (P0.1b): a route whose id cannot be read — a misspelt
    /// <paramref name="routeKey"/>, say — refuses a branch-bound caller instead of letting the
    /// request through unchecked.
    /// </para>
    /// </summary>
    public static TBuilder ForBranchOf<TBuilder>(this TBuilder builder,
        Func<PosDbContext, Guid, CancellationToken, Task<Guid?>> branchOf, string routeKey = "id")
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (BranchAssignment.IsCompanyScoped(http.User)) return await next(context);
            if (!http.Request.RouteValues.TryGetValue(routeKey, out var raw) || !Guid.TryParse(raw?.ToString(), out var id))
                return Results.NotFound();
            var db = http.RequestServices.GetRequiredService<PosDbContext>();
            if (await branchOf(db, id, http.RequestAborted) is Guid theirs
                && theirs != await BranchAssignment.ForCallerAsync(http.User, db, http.RequestAborted))
                return Results.NotFound();
            return await next(context);
        });
        return builder;
    }

    /// <summary>Where, in a handler's arguments, the branch-bearing ones are.</summary>
    public sealed record Plan(int BranchQuery, int[] Raised, int[] Boxes)
    {
        public bool IsEmpty => BranchQuery < 0 && Raised.Length == 0 && Boxes.Length == 0;

        public static Plan For(System.Reflection.ParameterInfo[] parameters)
        {
            var query = Array.FindIndex(parameters, p =>
                string.Equals(p.Name, "branchId", StringComparison.Ordinal)
                && (p.ParameterType == typeof(Guid) || p.ParameterType == typeof(Guid?)));
            int[] Where(Type type) => parameters
                .Select((p, i) => (p, i))
                .Where(x => type.IsAssignableFrom(x.p.ParameterType))
                .Select(x => x.i).ToArray();
            return new Plan(query, Where(typeof(IRaisedAtBranch)), Where(typeof(IPaidFromBox)));
        }
    }

    /// <summary>
    /// Rewrites the arguments in place; answers the refusal when there is one, else null. Public
    /// so the rule can be pinned without an HTTP host.
    /// </summary>
    public static async ValueTask<IResult?> ApplyAsync(EndpointFilterInvocationContext context, Plan plan)
    {
        var http = context.HttpContext;
        var db = http.RequestServices.GetRequiredService<PosDbContext>();
        var ct = http.RequestAborted;

        if (BranchAssignment.IsCompanyScoped(http.User))
        {
            foreach (var i in plan.Raised)
                if (context.Arguments[i] is IRaisedAtBranch { RaisedAtBranch: Guid named } && named != Guid.Empty
                    && !await db.Branches.AnyAsync(b => b.Id == named && !b.IsDeleted, ct))
                    return Results.BadRequest(BranchLock.UnknownBranchMessage);
            return null;
        }

        if (await BranchAssignment.ForCallerAsync(http.User, db, ct) is not Guid own)
            return Results.BadRequest(BranchLock.NoBranchMessage);

        if (plan.BranchQuery >= 0) context.Arguments[plan.BranchQuery] = own;
        foreach (var i in plan.Raised)
            if (context.Arguments[i] is IRaisedAtBranch request)
                context.Arguments[i] = request.AtBranch(own);
        foreach (var i in plan.Boxes)
        {
            if (context.Arguments[i] is not IPaidFromBox { PaidFromBox: Guid box }) continue;
            var boxBranch = await db.CashBoxes.AsNoTracking()
                .Where(b => b.Id == box).Select(b => (Guid?)b.BranchId).FirstOrDefaultAsync(ct);
            // An unknown box is left for the handler, which already says so in its own words.
            if (boxBranch is Guid theirs && theirs != own)
                return Results.BadRequest(BranchLock.ForeignBoxMessage);
        }
        return null;
    }
}
