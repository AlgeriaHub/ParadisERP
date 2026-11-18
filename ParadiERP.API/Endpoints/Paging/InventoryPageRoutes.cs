using System.Security.Claims;
using ParadiERP.Application.Catalog;
using ParadiERP.Application.Contracts;
using ParadiERP.Application.Regional;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Auth;
using ParadiERP.Infrastructure.Authorization;
using ParadiERP.Infrastructure.Catalog;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Rentals;
using ParadiERP.Infrastructure.Reports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Engine = ParadiERP.Application.Rentals;

namespace ParadiERP.API.Endpoints;

/// <summary>
/// The inventory and rentals lists a page at a time (owner, 2026-11-17: "I need to paginate in backend,
/// not frontend"): stock transfers, the item status report with its breakdown's two long lists, the
/// rental contracts and the items on a rental figure of their own. Each route sits beside the list
/// route it pages — mapped on the same group, so the same caller's-branch rewrite and the same filters
/// apply — and builds its rows exactly as that route does; the search and the filters the screens
/// applied in the browser run here, over every row, and only the page leaves. The list routes stay as
/// they were for the papers, the exports, the dashboard and the pickers.
/// </summary>
public static class InventoryPageRoutes
{
    // ---------- stock transfers ----------

    /// <summary>
    /// <c>GET /api/catalog/stock/transfers/page</c>, on the group of <c>GET /api/catalog/stock/transfers</c>.
    /// The query is <see cref="StockTransfers.ListAsync"/>'s, copied (same filters, same order, newest first),
    /// with an id tie-break and without its 500-row cap: the page is the limit now. <paramref name="group"/>'s
    /// <c>AtCallersBranch</c> sets <c>branchId</c> to a branch user's own branch, as it does for the list.
    /// </summary>
    public static void MapTransfersPage(RouteGroupBuilder group)
    {
        group.MapGet("/page", async (
            Guid? branchId, string? status, Guid? productId, DateTime? from, DateTime? to, string? direction,
            int? skip, int? take,
            [FromServices] StockTransfers transfers, PosDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var viewer = await TransferViewerAsync(http, db, ct);
            var shortfall = string.Equals(status, StockTransfers.ShortfallFilter, StringComparison.OrdinalIgnoreCase);
            var query = db.StockTransfers.AsNoTracking().Include(t => t.Lines).AsQueryable();
            if (branchId is Guid b)
            {
                query = query.Where(t => t.FromBranchId == b || t.ToBranchId == b);
                // Sent from or received at the branch in view (owner, 2026-11-17), moved here from the
                // browser: the branch is the one the list is read for, the user's own for a branch user.
                if (string.Equals(direction, "out", StringComparison.OrdinalIgnoreCase)) query = query.Where(t => t.FromBranchId == b);
                else if (string.Equals(direction, "in", StringComparison.OrdinalIgnoreCase)) query = query.Where(t => t.ToBranchId == b);
            }
            if (shortfall || string.Equals(status, "open", StringComparison.OrdinalIgnoreCase))
                query = query.Where(t => t.Status == StockTransferStatus.Dispatched || t.Status == StockTransferStatus.PartlyReceived);
            else if (Enum.TryParse<StockTransferStatus>(status, true, out var s))
                query = query.Where(t => t.Status == s);
            if (productId is Guid p) query = query.Where(t => t.Lines.Any(l => l.ProductId == p));
            // As the list route reads them (the dates bound as sent, compared with the dispatch instant).
            if (from is DateTime f) query = query.Where(t => t.DispatchedAtUtc >= f);
            if (to is DateTime u) query = query.Where(t => t.DispatchedAtUtc < u);
            var ordered = query.OrderByDescending(t => t.DispatchedAtUtc).ThenBy(t => t.Id);

            // A reported shortfall is a sum of decimals, read in memory as the list does (SQLite stores
            // decimals as text), over the open transfers only; every other filter pages in SQL.
            var page = shortfall
                ? ListPaging.Of((await ordered.ToListAsync(ct)).Where(t => t.HasPendingShortfall).ToList(), skip, take)
                : await ordered.ToPageAsync(skip, take, ct);
            var names = await transfers.NamesAsync(page.Items.SelectMany(t => new[] { t.FromBranchId, t.ToBranchId }), ct);
            IReadOnlyList<StockTransferDto> rows = page.Items.Select(t => StockTransfers.ToDto(t, names, viewer)).ToList();
            return Results.Ok(new PageResult<StockTransferDto>(rows, page.Total, page.Skip, page.Take));
        });
    }

    /// <summary>
    /// <c>StockTransferEndpoints.ActorAsync</c>, copied: the caller, their branch unless their role spans
    /// the company, and the two transfer permissions — what <see cref="StockTransfers.ToDto"/> reads for
    /// <c>canDecideShortfall</c>. Null for a branch user with no branch, which the list tolerates too.
    /// </summary>
    private static async Task<TransferActor?> TransferViewerAsync(HttpContext http, PosDbContext db, CancellationToken ct)
    {
        var (userId, name) = MovementAuthor.Of(http.User);
        var mayTransfer = ServerAuthorization.Has(http.User, PermissionCatalog.InventoryTransfer);
        var mayWriteOff = ServerAuthorization.Has(http.User, PermissionCatalog.InventoryTransferWriteOff);
        if (BranchAssignment.IsCompanyScoped(http.User)) return new TransferActor(userId, name, null, mayTransfer, mayWriteOff);
        return await BranchAssignment.ForCallerAsync(http.User, db, ct) is Guid own
            ? new TransferActor(userId, name, own, mayTransfer, mayWriteOff)
            : null;
    }

    // ---------- the item status report ----------

    /// <summary>ReportEndpoints' sentence for these routes, the same rule.</summary>
    private const string ItemStatusScoped =
        "a non-admin's branchId is replaced by their own branch: they read it in full and, only with " +
        "stock.view_other_branches, the other locations' available quantity alone";

    /// <summary>
    /// On the group of <c>GET /api/reports/inventory/items</c> (<c>/api/reports/inventory</c>):
    /// <list type="bullet">
    /// <item><c>/items/page</c>: the report's rows a page at a time; the summary carries the totals row over
    /// every row, the scope and when it was read, and every row's product id, so "select all" still
    /// selects the whole report.</item>
    /// <item><c>/items/{productId}/overview</c>: one article's breakdown without its two long lists (the
    /// rentals out and the movements), which page through the next two routes.</item>
    /// <item><c>/items/{productId}/rented/page</c>: what is out on rent, filtered by state and by the branch it
    /// went out from; the summary carries the count before those filters and the branches present.</item>
    /// <item><c>/items/{productId}/movements/page</c>: the movements with their running balance, computed over
    /// every row in order before the page is cut; the summary carries the balance before the page's first
    /// row, the period's opening and closing balances and the report's two notices.</item>
    /// </list>
    /// Each is answered from <see cref="ItemStatusReport"/> exactly as the existing route is (built in memory
    /// there), so the figures are the screen's, the export's and the paper's.
    /// </summary>
    public static void MapItemStatusPages(RouteGroupBuilder group)
    {
        group.MapGet("/items/page", async (
            Guid? branchId, string? q, string? productIds, string? itemType, string? status,
            Guid? customerId, Guid? contractId, bool? includeInactive, int? skip, int? take,
            ClaimsPrincipal caller, ItemStatusReport report, CancellationToken ct) =>
        {
            var (viewer, filter) = AskItemStatus(caller, branchId, q, productIds, itemType, status, customerId, contractId, includeInactive);
            var summary = await report.SummaryAsync(viewer, filter, ct);
            return Results.Ok(ListPaging.Of(summary.Rows, skip, take, new ItemStatusPageSummary(
                summary.AsOfUtc, summary.Scope, summary.Totals, summary.Rows.Select(r => r.ProductId).ToList())));
        }).WithMetadata(new BranchScopedAttribute(ItemStatusScoped));

        group.MapGet("/items/{productId:guid}/overview", async (
            Guid productId, Guid? branchId, string? q, string? productIds, string? itemType, string? status,
            Guid? customerId, Guid? contractId, bool? includeInactive, DateOnly? from, DateOnly? to,
            ClaimsPrincipal caller, ItemStatusReport report, CancellationToken ct) =>
        {
            var (viewer, filter) = AskItemStatus(caller, branchId, q, productIds, itemType, status, customerId, contractId, includeInactive);
            var detail = await report.DetailAsync(productId, viewer, filter, from, to, ct);
            if (detail is null) return Results.NotFound();
            return Results.Ok(detail with { Rented = [], Movements = detail.Movements with { Rows = [] } });
        }).WithMetadata(new BranchScopedAttribute(ItemStatusScoped));

        group.MapGet("/items/{productId:guid}/rented/page", async (
            Guid productId, Guid? branchId, string? q, string? productIds, string? itemType, string? status,
            Guid? customerId, Guid? contractId, bool? includeInactive, string? state, Guid? issueBranchId,
            int? skip, int? take,
            ClaimsPrincipal caller, ItemStatusReport report, CancellationToken ct) =>
        {
            var (viewer, filter) = AskItemStatus(caller, branchId, q, productIds, itemType, status, customerId, contractId, includeInactive);
            var detail = await report.DetailAsync(productId, viewer, filter, null, null, ct);
            if (detail is null) return Results.NotFound();
            var all = detail.Rented;
            // The branches the goods went out from, as the rows name them, in the order they first appear.
            var branches = all.GroupBy(r => r.IssueBranchId)
                .Select(g => g.First())
                .Select(r => new ItemIssueBranch(r.IssueBranchId, r.IssueBranchNameAr, r.IssueBranchNameFr, r.IssueBranchNameEn))
                .ToList();
            IEnumerable<ItemRentedDto> rows = all;
            if (string.Equals(state, "overdue", StringComparison.OrdinalIgnoreCase)) rows = rows.Where(r => r.Overdue);
            else if (string.Equals(state, "onTime", StringComparison.OrdinalIgnoreCase)) rows = rows.Where(r => !r.Overdue);
            if (issueBranchId is Guid issuedAt) rows = rows.Where(r => r.IssueBranchId == issuedAt);
            return Results.Ok(ListPaging.Of(rows.ToList(), skip, take, new ItemRentedPageSummary(all.Count, branches)));
        }).WithMetadata(new BranchScopedAttribute(ItemStatusScoped));

        group.MapGet("/items/{productId:guid}/movements/page", async (
            Guid productId, Guid? branchId, string? q, string? productIds, string? itemType, string? status,
            Guid? customerId, Guid? contractId, bool? includeInactive, DateOnly? from, DateOnly? to,
            int? skip, int? take,
            ClaimsPrincipal caller, ItemStatusReport report, CancellationToken ct) =>
        {
            var (viewer, filter) = AskItemStatus(caller, branchId, q, productIds, itemType, status, customerId, contractId, includeInactive);
            var detail = await report.DetailAsync(productId, viewer, filter, from, to, ct);
            if (detail is null) return Results.NotFound();
            var movements = detail.Movements;
            // Each row already carries the balance after it, run over every row of the period in order;
            // paging only cuts. What stood before the page's first row is the row before it, or the opening.
            var size = ListPaging.Take(take);
            var offset = ListPaging.Fit(ListPaging.Skip(skip), size, movements.Rows.Count);
            var pageOpening = offset == 0 ? movements.Opening : movements.Rows[offset - 1].Balance;
            return Results.Ok(ListPaging.Of(movements.Rows, skip, take, new ItemMovementsPageSummary(
                movements.From, movements.To, movements.Opening, movements.Closing,
                movements.UnrecordedDifference, movements.Truncated, pageOpening)));
        }).WithMetadata(new BranchScopedAttribute(ItemStatusScoped));
    }

    /// <summary>
    /// <c>ReportEndpoints.Ask</c>, copied: who is asking and what for. For anyone without company scope
    /// <paramref name="branchId"/> has already been set to their own branch by <see cref="CallersBranch"/>.
    /// </summary>
    private static (ItemStatusViewer Viewer, ItemStatusFilter Filter) AskItemStatus(
        ClaimsPrincipal caller, Guid? branchId, string? q, string? productIds, string? itemType, string? status,
        Guid? customerId, Guid? contractId, bool? includeInactive)
    {
        var companyScoped = BranchAssignment.IsCompanyScoped(caller);
        var viewer = companyScoped
            ? new ItemStatusViewer(null, false)
            : new ItemStatusViewer(branchId ?? Guid.Empty, ServerAuthorization.Has(caller, PermissionCatalog.StockViewOtherBranches));
        var filter = new ItemStatusFilter(companyScoped ? branchId : null, q, ProductIdList(productIds), itemType, status,
            customerId, contractId, includeInactive ?? false);
        return (viewer, filter);
    }

    /// <summary><c>ReportEndpoints.Ids</c>, copied: a comma-separated list of ids; anything that is not one is dropped.</summary>
    private static IReadOnlyCollection<Guid>? ProductIdList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => Guid.TryParse(part, out var id) ? id : (Guid?)null)
            .OfType<Guid>().Distinct().ToList();
    }

    // ---------- rental contracts ----------

    /// <summary>The progress words of a list row, in the order a contract goes through them (rental-view.ts).</summary>
    private static readonly string[] ContractStages = ["draft", "out", "partly", "unpaid", "closed", "cancelled"];

    /// <summary>
    /// <c>GET /api/rentals/contracts/page</c>, on the group of <c>GET /api/rentals/contracts</c>. The rows are
    /// built exactly as that route builds them (copied from <c>RentalEndpoints.MapContracts</c>: the same
    /// cancelled rule, branch, state and customer, newest signed first, the same three set queries for what
    /// is out and what was charged), over every contract instead of the latest 500.
    /// <para>
    /// The tiles are <c>rentalTotals</c> (rental-totals.ts) over those rows — the state chip and the customer,
    /// as the screen summed them before — so the yard's figures do not move with the search. Then the screen's
    /// own filters, moved here with the same meaning: the till's fold over number and customer, the progress
    /// word, returned late, the admin's branch (<paramref name="branch"/>; <c>branchId</c> stays the caller's
    /// scope) and the signing day on the company's calendar.
    /// </para>
    /// </summary>
    public static void MapRentalContractsPage(RouteGroupBuilder group)
    {
        group.MapGet("/contracts/page", async (
            string? status, Guid? customerId, Guid? branchId, string? q, string? stage, string? late, Guid? branch,
            string? from, string? to, int? skip, int? take,
            RentalDocuments rentals, PosDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var query = db.RentalContracts.AsNoTracking();
            // A cancelled contract leaves every list but the one rentals.cancelled reads (owner, 2026-10-06).
            if (!SeesCancelled(http.User)) query = query.Where(c => c.Status != RentalContractStatus.Cancelled);
            // Set to the caller's own branch for everyone but the Admin (CallersBranch).
            if (branchId is Guid scope) query = query.Where(c => c.BranchId == scope);
            if (Enum.TryParse<RentalContractStatus>(status, true, out var wanted))
                query = query.Where(c => c.Status == wanted);
            if (customerId is Guid id) query = query.Where(c => c.CustomerId == id);

            var contracts = await query.OrderByDescending(c => c.SignedAtUtc).ThenBy(c => c.Id).ToListAsync(ct);
            var customerIds = contracts.Select(c => c.CustomerId).Distinct().ToList();
            var names = await db.Customers.AsNoTracking()
                .Where(c => customerIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.FullName, ct);
            // RentalDocuments.TotalsAsync is written for the list's 500 ids at most: asked in slices of that.
            var totals = new Dictionary<Guid, ContractTotals>();
            foreach (var slice in contracts.Select(c => c.Id).Chunk(500))
                foreach (var (contractId, figures) in await rentals.TotalsAsync(slice, ct))
                    totals[contractId] = figures;
            var settings = await rentals.SettingsAsync(ct);
            var depositVisible = Engine.RentalDeposit.IsVisible(settings);

            var rows = contracts.Select(c =>
            {
                var t = totals.GetValueOrDefault(c.Id, ContractTotals.Zero);
                return new RentalContractRowDto(
                    c.Id, c.Number, c.CustomerId, names.GetValueOrDefault(c.CustomerId),
                    c.BranchId, c.Status.ToString(),
                    c.LockedAtUtc is not null, c.LockedAtUtc, c.SignedAtUtc, c.ClosedAtUtc,
                    t.IssuedQty, t.SettledQty, t.StillOutQty, t.ChargeTotal, t.OverdueTotal,
                    // Invisible while the switch is off: no field, no zero column (§5.6).
                    depositVisible ? c.DepositAmount : null,
                    c.CancelledAtUtc, c.CancelledBy, c.CancelReason);
            }).ToList();

            var present = rows.Select(ContractStage).ToHashSet(StringComparer.Ordinal);
            var summary = new RentalContractsPageSummary(
                ContractTiles(rows),
                ContractStages.Where(present.Contains).ToList(),
                rows.Any(r => r.DepositAmount is not null),
                rows.Count);

            IEnumerable<RentalContractRowDto> shown = rows;
            var needle = ListText.Fold(q);
            if (needle.Length > 0)
                shown = shown.Where(r => ListText.Fold($"{r.Number} {r.CustomerName ?? ""}").Contains(needle, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(stage) && !string.Equals(stage, "all", StringComparison.OrdinalIgnoreCase))
                shown = shown.Where(r => string.Equals(ContractStage(r), stage, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(late, "yes", StringComparison.OrdinalIgnoreCase)) shown = shown.Where(r => r.OverdueTotal > 0);
            else if (string.Equals(late, "no", StringComparison.OrdinalIgnoreCase)) shown = shown.Where(r => !(r.OverdueTotal > 0));
            if (branch is Guid only) shown = shown.Where(r => r.BranchId == only);
            // The signing day read in the company's zone, as the screen read it (localDay).
            var zone = rentals.CompanyZone();
            if (CompanyDays.TryParseDay(from, out var fromDay))
            {
                var start = CompanyDays.StartUtc(fromDay, zone);
                shown = shown.Where(r => r.SignedAtUtc >= start);
            }
            if (CompanyDays.TryParseDay(to, out var toDay))
            {
                var end = CompanyDays.StartUtc(toDay.AddDays(1), zone);
                shown = shown.Where(r => r.SignedAtUtc < end);
            }
            return Results.Ok(ListPaging.Of(shown.ToList(), skip, take, summary));
        });
    }

    /// <summary><c>RentalEndpoints.SeesCancelled</c>, copied: rentals.cancelled, the Admin's.</summary>
    private static bool SeesCancelled(ClaimsPrincipal user) =>
        ServerAuthorization.Read(user)?.Has(PermissionCatalog.RentalsCancelled) ?? false;

    /// <summary>
    /// <c>contractState</c> (rental-view.ts) for a list row: a list row carries no goods-back instant, so it
    /// is never "unpaid" here, exactly as the screen read it; its quantities are always known.
    /// </summary>
    private static string ContractStage(RentalContractRowDto r) => r.Status switch
    {
        "Cancelled" => "cancelled",
        "Closed" => "closed",
        _ when !r.IsLocked => "draft",
        _ => r.SettledQty > 0 ? "partly" : "out",
    };

    /// <summary><c>rentalTotals</c> (rental-totals.ts): open contracts only — a closed one is finished, a cancelled one never started.</summary>
    private static RentalContractTiles ContractTiles(IEnumerable<RentalContractRowDto> rows)
    {
        decimal stillOut = 0m, charged = 0m, overdue = 0m;
        int drafts = 0, openWithGoods = 0;
        foreach (var row in rows)
        {
            if (row.Status != nameof(RentalContractStatus.Open)) continue;
            if (!row.IsLocked) drafts++;
            stillOut += row.StillOutQty;
            charged += row.ChargeTotal;
            overdue += row.OverdueTotal;
            if (row.StillOutQty > 0) openWithGoods++;
        }
        return new RentalContractTiles(stillOut, charged, overdue, drafts, openWithGoods);
    }

    // ---------- items on a rental figure of their own ----------

    /// <summary>
    /// <c>GET /api/rentals/settings/products/page</c>, on the group of <c>GET /api/rentals/settings/products</c>.
    /// The query is <see cref="RentalDocuments.ProductMultipliersAsync"/>'s, copied (every item carrying its own
    /// overdue multiplier, hire price or replacement value, by SKU), with an id tie-break; the hire price set
    /// (above zero) or not and the replacement value set or not filter in SQL, the till's fold over SKU and
    /// names in memory.
    /// </summary>
    public static void MapRentalItemFiguresPage(RouteGroupBuilder group)
    {
        group.MapGet("/settings/products/page", async (
            string? q, string? price, string? damage, int? skip, int? take, PosDbContext db, CancellationToken ct) =>
        {
            var query = db.Products.AsNoTracking()
                .Where(p => !p.IsDeleted && (p.RentalOverdueMultiplier != null || p.RentalUnitPrice > 0 || p.RentalDamageUnitPrice != null));
            query = price switch
            {
                "set" => query.Where(p => p.RentalUnitPrice > 0),
                "unset" => query.Where(p => !(p.RentalUnitPrice > 0)),
                _ => query,
            };
            query = damage switch
            {
                "set" => query.Where(p => p.RentalDamageUnitPrice != null),
                "unset" => query.Where(p => p.RentalDamageUnitPrice == null),
                _ => query,
            };
            var rows = query.OrderBy(p => p.Sku).ThenBy(p => p.Id)
                .Select(p => new ProductOverdueMultiplierDto(
                    p.Id, p.Sku, p.NameAr, p.NameFr, p.NameEn, p.RentalOverdueMultiplier, p.RentalUnitPrice, p.RentalDamageUnitPrice));
            var needle = ListText.Fold(q);
            if (needle.Length == 0) return Results.Ok(await rows.ToPageAsync(skip, take, ct));
            var found = (await rows.ToListAsync(ct))
                .Where(r => ListText.Fold($"{r.Sku} {r.NameFr} {r.NameAr} {r.NameEn}").Contains(needle, StringComparison.Ordinal))
                .ToList();
            return Results.Ok(ListPaging.Of(found, skip, take));
        });
    }
}

/// <summary>The item status report's figures over every row: the totals row, the scope, when it was read, every row's product.</summary>
public sealed record ItemStatusPageSummary(
    DateTime AsOfUtc, ItemStatusScopeDto Scope, ItemStatusTotalsDto Totals, IReadOnlyList<Guid> ProductIds);

/// <summary>A branch goods went out on rent from, in its three names.</summary>
public sealed record ItemIssueBranch(Guid Id, string NameAr, string NameFr, string NameEn);

/// <summary>An article's rentals out: how many before the state and branch filters, and the branches they went out from.</summary>
public sealed record ItemRentedPageSummary(int Count, IReadOnlyList<ItemIssueBranch> IssueBranches);

/// <summary>
/// An article's movements over the period: its opening and closing balances, the report's two notices, and
/// <paramref name="PageOpening"/>, the balance before the page's first row.
/// </summary>
public sealed record ItemMovementsPageSummary(
    DateOnly? From, DateOnly? To, decimal Opening, decimal Closing, decimal? UnrecordedDifference, bool Truncated,
    decimal PageOpening);

/// <summary>The yard at a glance (rental-totals.ts), over open contracts.</summary>
public sealed record RentalContractTiles(decimal StillOut, decimal Charged, decimal Overdue, int Drafts, int OpenWithGoods);

/// <summary>
/// The contract list's figures over the rows of the state chip and the customer, before the screen's own
/// filters: the tiles, the progress words those rows carry, whether the deposit column shows, and how many
/// there are (none: the list is empty, not filtered to nothing).
/// </summary>
public sealed record RentalContractsPageSummary(RentalContractTiles Totals, IReadOnlyList<string> Stages, bool DepositVisible, int Count);
