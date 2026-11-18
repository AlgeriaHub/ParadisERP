using System.Security.Claims;
using AlgeriaPos.Application.Catalog;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Auth;
using AlgeriaPos.Infrastructure.Authorization;
using AlgeriaPos.Infrastructure.Catalog;
using AlgeriaPos.Infrastructure.FixedAssets;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Sales;
using AlgeriaPos.Infrastructure.Valuation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.API.Endpoints;

/// <summary>
/// The finance lists a page at a time (owner, 2026-11-17: "I need to paginate in backend, not frontend"): the books'
/// reports and journal, the closing trail, the cheque register and the paper awaiting collection, a cash box's
/// movements, the sales report's list, the fixed-asset register, the stock valuation, the sales without a cost and the
/// low stock. Each route sits beside the list route the screen read whole, which stays as it was for the papers, the
/// exports and the tills; it is mapped on that route's own group, so the group's <c>AtCallersBranch</c> (a parameter
/// named <c>branchId</c> is a non-admin's own branch) and subscription filters apply, and it builds its rows exactly as
/// that route does. The filters the screen applied in the browser run here over every row, and every figure above a
/// list — a tile, a totals row, a count per state, the choices a filter offers — is computed over all the filtered
/// rows and sent in <see cref="PageResult{T}.Summary"/>. A running balance is computed over every row in order, then
/// paged: each row carries its own, and the summary the balance before the page.
/// <para>
/// Money is summed in memory after <c>ToListAsync</c>, like every other total here: SQLite (the tests) keeps a decimal
/// as TEXT and can neither sum nor order one. A list whose rows a report builds in memory is paged with
/// <see cref="ListPaging.Of{T}"/>; a list whose filters translate is counted and cut in SQL.
/// </para>
/// </summary>
public static class FinancePageRoutes
{
    // =====================================================================================================
    // The books (/api/ledger: LedgerEndpoints.MapLedgerEndpoints, group with AtCallersBranch)
    // =====================================================================================================

    /// <summary>
    /// <c>GET /api/ledger/reports/trial-balance/page</c>: the trial balance's rows a page at a time; its totals and
    /// whether it balances are the whole balance's (<see cref="LedgerReports.TrialBalanceAsync"/>, as the list route).
    /// </summary>
    public static void MapTrialBalancePage(RouteGroupBuilder group)
    {
        group.MapGet("/reports/trial-balance/page", async (
            DateTime? from, DateTime? to, Guid? branchId, int? skip, int? take, LedgerReports reports, CancellationToken ct) =>
        {
            var (fromUtc, toUtc, error) = LedgerWindow(from, to);
            if (error is not null) return error;
            var trial = await reports.TrialBalanceAsync(fromUtc, toUtc, branchId, ct);
            return Results.Ok(ListPaging.Of(trial.Rows, skip, take, new TrialBalanceSummary(
                trial.From, trial.To, trial.BranchId, trial.Consolidated, trial.TotalDebit, trial.TotalCredit, trial.Balanced)));
        });
    }

    /// <summary>
    /// <c>GET /api/ledger/reports/account-statement/page</c>: one account's movements with their running balance.
    /// Read as <see cref="LedgerReports.AccountStatementAsync"/> reads them (copied from it: the opening from every line
    /// before the window, the window's lines by entry date then entry), over every movement of the window instead of
    /// the first 500, then paged. The summary carries the account, the opening and closing balances, the window's
    /// debit and credit totals, and <c>PageOpening</c> — the balance before the page's first row.
    /// </summary>
    public static void MapAccountStatementPage(RouteGroupBuilder group)
    {
        group.MapGet("/reports/account-statement/page", async (
            Guid accountId, DateTime? from, DateTime? to, Guid? branchId, int? skip, int? take,
            PosDbContext db, CancellationToken ct) =>
        {
            var (start, end, error) = LedgerWindow(from, to);
            if (error is not null) return error;
            var account = await db.LedgerAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct);
            if (account is null) return Results.NotFound("Unknown account.");

            var lines = db.LedgerEntryLines.AsNoTracking()
                .Join(db.LedgerEntries.AsNoTracking(), line => line.EntryId, entry => entry.Id, (line, entry) => new { line, entry })
                .Where(x => x.line.AccountId == accountId);
            if (branchId is not null) lines = lines.Where(x => x.line.BranchId == branchId);

            var before = await lines.Where(x => x.entry.EntryDate < start)
                .Select(x => new { x.line.Debit, x.line.Credit })
                .ToListAsync(ct);
            var opening = Natural(account.Type, before.Sum(x => x.Debit - x.Credit));

            var movements = await lines.Where(x => x.entry.EntryDate >= start && x.entry.EntryDate < end)
                // The list route's order, and the line's id to break a tie inside one entry.
                .OrderBy(x => x.entry.EntryDate).ThenBy(x => x.entry.Id).ThenBy(x => x.line.Id)
                .Select(x => new
                {
                    x.entry.Id, x.entry.EntryDate, x.line.BranchId, x.entry.SourceType, x.entry.SourceId,
                    x.entry.Description, x.entry.IsReversal, x.line.Debit, x.line.Credit
                })
                .ToListAsync(ct);

            var running = opening;
            var rows = new List<AccountStatementRowDto>(movements.Count);
            foreach (var m in movements)
            {
                running += Natural(account.Type, m.Debit - m.Credit);
                rows.Add(new AccountStatementRowDto(
                    m.Id, m.EntryDate, m.BranchId, m.SourceType, m.SourceId, m.Description,
                    m.IsReversal, m.Debit, m.Credit, running));
            }

            var size = ListPaging.Take(take);
            var offset = ListPaging.Fit(ListPaging.Skip(skip), size, rows.Count);
            var pageOpening = offset == 0 ? opening : rows[offset - 1].RunningBalance;
            return Results.Ok(ListPaging.Of(rows, skip, take, new AccountStatementSummary(
                account.Id, account.Code, account.NameAr, account.NameFr, account.NameEn, account.Type.ToString(),
                start, end, branchId, opening, running,
                movements.Sum(m => m.Debit), movements.Sum(m => m.Credit), pageOpening)));
        });
    }

    /// <summary>
    /// <c>GET /api/ledger/entries/page</c>: the journal (the document register), newest first, with the screen's own
    /// filters — a reversal or not, inter-branch or not, who recorded it. The period, branch, document type and source
    /// are the list route's parameters and its query (copied from LedgerEndpoints.MapJournal). The summary names who
    /// recorded the period's entries (the "by" filter's choices) and how many there are before the screen's filters.
    /// </summary>
    public static void MapJournalPage(RouteGroupBuilder group)
    {
        group.MapGet("/entries/page", async (
            DateTime? from, DateTime? to, Guid? branchId, string? sourceType, Guid? sourceId,
            string? reversal, string? interBranch, string? by, int? skip, int? take,
            PosDbContext db, CancellationToken ct) =>
        {
            IQueryable<LedgerEntry> scope = db.LedgerEntries.AsNoTracking();
            if (from is not null) scope = scope.Where(e => e.EntryDate >= from);
            if (to is not null) scope = scope.Where(e => e.EntryDate < to);
            if (branchId is not null) scope = scope.Where(e => e.BranchId == branchId);
            if (!string.IsNullOrWhiteSpace(sourceType)) scope = scope.Where(e => e.SourceType == sourceType);
            if (sourceId is not null) scope = scope.Where(e => e.SourceId == sourceId);

            var authors = await scope.Where(e => e.CreatedBy != null && e.CreatedBy != "")
                .Select(e => e.CreatedBy!).Distinct().ToListAsync(ct);
            var summary = new JournalSummary(authors.Order(StringComparer.Ordinal).ToList(), await scope.CountAsync(ct));

            var query = scope;
            query = reversal switch
            {
                "yes" => query.Where(e => e.IsReversal),
                "no" => query.Where(e => !e.IsReversal),
                _ => query,
            };
            query = interBranch switch
            {
                "yes" => query.Where(e => e.IsInterBranch),
                "no" => query.Where(e => !e.IsInterBranch),
                _ => query,
            };
            if (!string.IsNullOrEmpty(by) && by != "all") query = query.Where(e => e.CreatedBy == by);

            var page = await query.Include(e => e.Lines)
                .OrderByDescending(e => e.EntryDate).ThenByDescending(e => e.CreatedAtUtc).ThenBy(e => e.Id)
                .ToPageAsync(skip, take, async rows =>
                {
                    var accounts = await db.LedgerAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id, ct);
                    var branches = await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.NameFr, ct);
                    var originals = await OriginalDatesAsync(db, rows, ct);
                    return (IReadOnlyList<LedgerEntryDto>)rows.Select(e => MapEntry(e, accounts, branches, originals)).ToList();
                }, ct, summary);
            return Results.Ok(page);
        });
    }

    /// <summary>
    /// <c>GET /api/ledger/closing/page</c>: every close and reopen of the books, newest first, filtered by what was
    /// done and by the day it was done on the company's clock. The summary carries what the page leads with: the day
    /// the books are closed through, the last change (whatever the filters), and how many changes there are.
    /// </summary>
    public static void MapBookClosingPage(RouteGroupBuilder group)
    {
        group.MapGet("/closing/page", async (
            string? action, DateOnly? from, DateOnly? to, int? skip, int? take,
            PosDbContext db, IRegionalSettingsProvider regional, CancellationToken ct) =>
        {
            var current = await BookClosings.CurrentAsync(db, ct);
            var summary = new BookClosingSummary(
                current?.ClosedThrough, current is null ? null : MapClosing(current), await db.BookClosings.CountAsync(ct));

            IQueryable<BookClosing> query = db.BookClosings.AsNoTracking();
            query = action switch
            {
                "closed" => query.Where(c => !c.IsReopen),
                "reopened" => query.Where(c => c.IsReopen),
                _ => query,
            };
            var zone = StoreDocumentHistory.Zone(regional);
            if (from is DateOnly first)
            {
                var fromUtc = TimeZoneInfo.ConvertTimeToUtc(first.ToDateTime(TimeOnly.MinValue), zone);
                query = query.Where(c => c.AtUtc >= fromUtc);
            }
            if (to is DateOnly last)
            {
                var toUtc = TimeZoneInfo.ConvertTimeToUtc(last.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);
                query = query.Where(c => c.AtUtc < toUtc);
            }

            return Results.Ok(await query.OrderByDescending(c => c.AtUtc).ThenBy(c => c.Id).ToPageAsync(skip, take,
                rows => Task.FromResult<IReadOnlyList<BookClosingDto>>(rows.Select(MapClosing).ToList()), ct, summary));
        });
    }

    // =====================================================================================================
    // Treasury (/api/treasury: TreasuryEndpoints.MapTreasuryEndpoints, group with AtCallersBranch)
    // =====================================================================================================

    /// <summary>
    /// <c>GET /api/treasury/pending-payments/page</c>: the register of instruments the accounting tab shows — every
    /// till payment whose tender needs clearing (a payment method that "requires confirmation") or that carries
    /// instrument details, in all three states: pending first, then bounced, then collected, each by due date with the
    /// undated last. Built from the list route's query (status, branch) without its 500-row cut per state, the screen's
    /// clearing rule applied here. Filters: a state, and a due-day window on the company's clock (an undated payment has
    /// no day to compare). The tiles — count and amount per state — are over every payment of the register, whatever
    /// the filters, as on the screen.
    /// </summary>
    public static void MapChequesPage(RouteGroupBuilder group)
    {
        group.MapGet("/pending-payments/page", async (
            string? status, DateOnly? dueFrom, DateOnly? dueTo, Guid? branchId, int? skip, int? take,
            PosDbContext db, IRegionalSettingsProvider regional, CancellationToken ct) =>
        {
            var clearing = await db.PaymentMethodDefs.AsNoTracking()
                .Where(m => !m.IsDeleted && m.RequiresConfirmation).Select(m => m.Key).ToListAsync(ct);

            var rows = await db.SalePayments.AsNoTracking()
                .Where(p => clearing.Contains(p.MethodKey)
                    || (p.InstrumentNumber != null && p.InstrumentNumber != "")
                    || (p.InstrumentBank != null && p.InstrumentBank != "")
                    || p.InstrumentDueDate != null)
                .Join(db.Sales.AsNoTracking(), p => p.SaleId, s => s.Id, (p, s) => new { p, s })
                .Where(x => branchId == null || x.s.BranchId == branchId)
                .ToListAsync(ct);

            var customerIds = rows.Where(x => x.s.CustomerId is not null).Select(x => x.s.CustomerId!.Value).Distinct().ToList();
            var customers = await db.Customers.AsNoTracking()
                .Where(c => customerIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.FullName, ct);

            var all = rows.Select(x => new
            {
                Dto = new PendingPaymentDto(
                    x.p.Id, x.s.Id, x.s.Reference, x.s.SoldAtUtc,
                    x.s.CustomerId, x.s.CustomerId is Guid cid ? customers.GetValueOrDefault(cid) : null,
                    x.p.MethodKey, x.p.Amount, x.p.Status.ToString(),
                    x.p.InstrumentNumber, x.p.InstrumentBank, x.p.InstrumentDueDate,
                    x.p.ConfirmedAtUtc, x.p.StatusNote),
                x.p.Status,
            }).ToList();

            decimal Total(PaymentStatus s) => all.Where(x => x.Status == s).Sum(x => x.Dto.Amount);
            int Count(PaymentStatus s) => all.Count(x => x.Status == s);
            var summary = new ChequesSummary(
                Count(PaymentStatus.Pending), Total(PaymentStatus.Pending),
                Count(PaymentStatus.Confirmed), Total(PaymentStatus.Confirmed),
                Count(PaymentStatus.Bounced), Total(PaymentStatus.Bounced),
                all.Count);

            var zone = StoreDocumentHistory.Zone(regional);
            IEnumerable<PendingPaymentDto> shown = all.Select(x => x.Dto);
            if (Enum.TryParse<PaymentStatus>(status, true, out var wanted) && !string.IsNullOrWhiteSpace(status))
                shown = shown.Where(p => p.Status == wanted.ToString());
            if (dueFrom is not null || dueTo is not null)
                shown = shown.Where(p => p.InstrumentDueDate is DateTime due && InWindow(CompanyDay(due, zone), dueFrom, dueTo));

            static int Rank(string s) => s == nameof(PaymentStatus.Pending) ? 0 : s == nameof(PaymentStatus.Bounced) ? 1 : 2;
            var list = shown
                .OrderBy(p => Rank(p.Status))
                .ThenBy(p => p.InstrumentDueDate ?? DateTime.MaxValue)
                // Within a day, the list route's own order: due date, else the day of the sale.
                .ThenBy(p => p.InstrumentDueDate ?? p.SoldAtUtc)
                .ThenBy(p => p.Id)
                .ToList();
            return Results.Ok(ListPaging.Of(list, skip, take, summary));
        });
    }

    /// <summary>
    /// <c>GET /api/treasury/awaiting/page</c>: Finance › Awaiting collection — every cheque and transfer still in hand,
    /// the till's pending payments (the query of <c>GET /api/treasury/pending-payments?status=Pending</c>) and the
    /// pending receipts (the query of <c>GET /api/receivables/receipts?status=Pending</c>), as one list, earliest due
    /// first and undated last. Filters as the screen applied them: source, a search over the reference, customer and
    /// instrument (folded as the till folds), the due word, the payment method and a due-day window. The figures —
    /// total and overdue, amount and count — are over the filtered rows, as on the screen; the methods the filter
    /// offers are those of every row. The screen read both lists, so the caller needs the receipts' permission too.
    /// </summary>
    public static void MapAwaitingCollectionPage(RouteGroupBuilder group)
    {
        group.MapGet("/awaiting/page", async (
            string? source, string? q, string? due, string? method, DateOnly? dueFrom, DateOnly? dueTo, Guid? branchId,
            int? skip, int? take, ClaimsPrincipal caller, PosDbContext db, IRegionalSettingsProvider regional,
            TimeProvider time, CancellationToken ct) =>
        {
            // The screen also read GET /api/receivables/receipts, which these keys open: without one it failed as a whole.
            if (!ServerAuthorization.Has(caller, "finance.receipts.view") && !ServerAuthorization.Has(caller, "treasury.cash_box"))
                return Results.Problem("Reading the receipts awaiting collection needs the receipts' permission.",
                    statusCode: StatusCodes.Status403Forbidden);

            var sales = await db.SalePayments.AsNoTracking()
                .Where(p => p.Status == PaymentStatus.Pending)
                .Join(db.Sales.AsNoTracking(), p => p.SaleId, s => s.Id, (p, s) => new { p, s })
                .Where(x => branchId == null || x.s.BranchId == branchId)
                .ToListAsync(ct);
            var receiptsQuery = db.ReceiptVouchers.AsNoTracking().Where(v => v.Status == PaymentStatus.Pending);
            if (branchId is Guid branch) receiptsQuery = receiptsQuery.Where(v => v.BranchId == branch);
            var receipts = await receiptsQuery.ToListAsync(ct);

            var customerIds = sales.Where(x => x.s.CustomerId is not null).Select(x => x.s.CustomerId!.Value)
                .Concat(receipts.Select(v => v.CustomerId)).Distinct().ToList();
            var names = await db.Customers.AsNoTracking()
                .Where(c => customerIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.FullName, ct);

            var rows = sales.Select(x => new AwaitingRow(
                    "sale", x.p.Id, x.s.Reference, x.s.CustomerId,
                    x.s.CustomerId is Guid cid ? names.GetValueOrDefault(cid) : null,
                    x.p.MethodKey, x.p.Amount, x.p.InstrumentNumber, x.p.InstrumentBank, x.p.InstrumentDueDate,
                    x.s.SoldAtUtc, null))
                .Concat(receipts.Select(v => new AwaitingRow(
                    "receipt", v.Id, v.Number, v.CustomerId, names.GetValueOrDefault(v.CustomerId),
                    v.MethodKey, v.Amount, v.InstrumentNumber, v.InstrumentBank, v.InstrumentDueDate,
                    v.ReceivedAtUtc, v.BranchId)))
                .OrderBy(r => r.InstrumentDueDate ?? DateTime.MaxValue)
                .ThenBy(r => r.TakenAtUtc)
                .ThenBy(r => r.Source == "sale" ? 0 : 1)
                .ThenBy(r => r.Id)
                .ToList();

            var zone = StoreDocumentHistory.Zone(regional);
            var today = CompanyDay(time.GetUtcNow().UtcDateTime, zone);
            string Due(AwaitingRow r) => DueWord(r.InstrumentDueDate, today, zone);

            var needle = ListText.Fold(q);
            IEnumerable<AwaitingRow> shown = rows;
            if (source is "sale" or "receipt") shown = shown.Where(r => r.Source == source);
            if (needle.Length > 0)
                shown = shown.Where(r => new[] { r.Reference, r.CustomerName, r.InstrumentNumber, r.InstrumentBank }
                    .Any(t => ListText.Fold(t).Contains(needle, StringComparison.Ordinal)));
            if (due is "overdue" or "today" or "soon" or "later" or "none") shown = shown.Where(r => Due(r) == due);
            if (!string.IsNullOrEmpty(method) && method != "all") shown = shown.Where(r => r.MethodKey == method);
            if (dueFrom is not null || dueTo is not null)
                shown = shown.Where(r => r.InstrumentDueDate is DateTime d && InWindow(CompanyDay(d, zone), dueFrom, dueTo));

            var list = shown.ToList();
            var overdue = list.Where(r => Due(r) == "overdue").ToList();
            var summary = new AwaitingSummary(
                list.Sum(r => r.Amount), list.Count, overdue.Sum(r => r.Amount), overdue.Count,
                rows.Select(r => r.MethodKey).Distinct().Order(StringComparer.Ordinal).ToList(),
                rows.Count);
            return Results.Ok(ListPaging.Of(list, skip, take, summary));
        });
    }

    /// <summary>
    /// <c>GET /api/treasury/cash-boxes/{id}/movements/page</c>: a box's movements, newest first, as the list route reads
    /// them (its query and window, without its cut at the latest 500). Every row carries the balance after it, written
    /// with it; the summary carries the money in and out over every movement, the box's latest balance, and the page's
    /// balance before its earliest row and after its latest. Another branch's box is not found (ForBranchOf).
    /// </summary>
    public static void MapCashMovementsPage(RouteGroupBuilder group)
    {
        group.MapGet("/cash-boxes/{id:guid}/movements/page", async (
            Guid id, DateTime? from, DateTime? to, int? skip, int? take, PosDbContext db, CancellationToken ct) =>
        {
            var query = db.CashMovements.AsNoTracking().Where(m => m.CashBoxId == id);
            if (from is not null) query = query.Where(m => m.OccurredAtUtc >= from);
            if (to is not null) query = query.Where(m => m.OccurredAtUtc <= to);

            var amounts = await query.Select(m => new { m.Direction, m.Amount }).ToListAsync(ct);
            var ordered = query.OrderByDescending(m => m.OccurredAtUtc).ThenBy(m => m.Id);
            var latest = await ordered.Select(m => new { m.BalanceAfter }).FirstOrDefaultAsync(ct);

            var page = await ordered.ToPageAsync(skip, take, rows => Task.FromResult<IReadOnlyList<CashMovementDto>>(
                rows.Select(m => new CashMovementDto(
                    m.Id, m.CashBoxId, m.Direction.ToString(), m.Amount, m.BalanceAfter,
                    m.Kind.ToString(), m.RelatedCashBoxId, m.UserName, m.Reference, m.Note, m.OccurredAtUtc)).ToList()), ct);

            var earliest = page.Items.Count > 0 ? page.Items[page.Items.Count - 1] : null;
            var summary = new CashMovementsSummary(
                id,
                amounts.Where(m => m.Direction == CashDirection.In).Sum(m => m.Amount),
                amounts.Where(m => m.Direction == CashDirection.Out).Sum(m => m.Amount),
                latest?.BalanceAfter,
                earliest is null ? null : earliest.BalanceAfter - (earliest.Direction == nameof(CashDirection.In) ? earliest.Amount : -earliest.Amount),
                page.Items.Count > 0 ? page.Items[0].BalanceAfter : null);
            return Results.Ok(page with { Summary = summary });
        }).ForBranchOf(DocumentBranches.CashBox);
    }

    // =====================================================================================================
    // Sales and reports (/api: SalesEndpoints.MapSalesEndpoints, group without a group-wide branch filter)
    // =====================================================================================================

    /// <summary>
    /// <c>GET /api/sales/page</c>: the sales report's list, newest first — the list route's query and branch rule
    /// (copied from <see cref="SalesQueries.ListAsync"/>: a cashier's branch is the till's, a manager's their own) without
    /// its cut at 200, with the screen's filters: refunded or completed, and the cashier. The summary names the cashiers
    /// of the period (the filter's choices) and counts its sales before the screen's filters.
    /// </summary>
    public static void MapSalesPage(RouteGroupBuilder group)
    {
        group.MapGet("/sales/page", async (
            DateTime? from, DateTime? to, string? deviceCode, Guid? branchId, string? status, string? cashier, string? tax,
            int? skip, int? take, ClaimsPrincipal caller, PosDbContext db, CancellationToken ct) =>
        {
            var scope = await BranchScope.ForAsync(caller, db, ct);
            var query = db.Sales.AsNoTracking();
            if (from is not null) query = query.Where(s => s.SoldAtUtc >= from);
            if (to is not null) query = query.Where(s => s.SoldAtUtc < to);
            if (!string.IsNullOrWhiteSpace(deviceCode)) query = query.Where(s => s.DeviceCode == deviceCode);
            if (branchId is not null) query = query.Where(s => s.BranchId == branchId);
            if (scope is Guid own) query = query.Where(s => s.BranchId == own);

            var cashiers = await query.Where(s => s.CashierName != "").Select(s => s.CashierName).Distinct().ToListAsync(ct);
            var summary = new SalesSummary(cashiers.Order(StringComparer.Ordinal).ToList(), await query.CountAsync(ct));

            // The state column's reading: refunded, or else completed.
            query = status switch
            {
                "refunded" => query.Where(s => s.Status == SaleStatus.Refunded),
                "completed" => query.Where(s => s.Status != SaleStatus.Refunded),
                _ => query,
            };
            if (!string.IsNullOrEmpty(cashier) && cashier != "all") query = query.Where(s => s.CashierName == cashier);
            // With or without tax (owner, 2026-11-18): a bill starts untaxed and carries tax only when
            // the seller ticked it for the customer, so a sale with tax is one whose tax total is not zero.
            query = tax switch
            {
                "with" => query.Where(s => s.TaxTotal != 0),
                "without" => query.Where(s => s.TaxTotal == 0),
                _ => query,
            };

            return Results.Ok(await query
                .OrderByDescending(s => s.SoldAtUtc).ThenBy(s => s.Id)
                .Select(s => new SaleRowDto(
                    s.Id, s.Reference, s.DeviceCode, s.BranchId, s.SoldAtUtc, s.ReceivedAtUtc,
                    s.CashierName, s.CustomerId, s.SubTotalHt, s.TaxTotal, s.DiscountTotal,
                    s.GrandTotalTtc, s.RoundingAdjustment, s.PaidAmount, s.ChangeAmount,
                    s.PaymentMethod.ToString(), s.Status.ToString(), s.StockWarning, s.Lines.Count))
                .ToPageAsync(skip, take, ct, summary));
        }).WithMetadata(new BranchScopedAttribute("a cashier's branchId filter is overridden by the till's branch, not honoured; a manager's by their own branch (CallersBranch)"))
        .AtCallersBranch();
    }

    /// <summary>
    /// <c>GET /api/reports/missing-cost/page</c>: the sales without a cost (SAL-02) by product, a page at a time — the
    /// list route's window, branch rule and report (<see cref="SalesQueries.MissingCostAsync"/>), with the screen's
    /// filter on whether a cost has been entered since. The figures above the list are the report's, for the period.
    /// </summary>
    public static void MapMissingCostPage(RouteGroupBuilder group)
    {
        group.MapGet("/reports/missing-cost/page", async (
            DateOnly? from, DateOnly? to, Guid? branchId, string? cost, int? skip, int? take,
            ClaimsPrincipal caller, PosDbContext db, IRegionalSettingsProvider regional, CancellationToken ct) =>
        {
            var zone = StoreDocumentHistory.Zone(regional);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
            var start = from ?? new DateOnly(today.Year, today.Month, 1);
            var end = to ?? start.AddMonths(1).AddDays(-1);
            if (end < start) return Results.BadRequest("'to' must not be before 'from'.");
            if (end.DayNumber - start.DayNumber + 1 > SalesQueries.MaxMissingCostDays)
                return Results.BadRequest($"Date range too wide — max {SalesQueries.MaxMissingCostDays} days per request.");

            var scope = await BranchScope.ForAsync(caller, db, ct);
            var report = await SalesQueries.MissingCostAsync(db, start, end, branchId, scope, zone, regional.Get(), ct);
            // As the state column says it: a cost entered since is "set", none is "missing".
            var rows = cost switch
            {
                "set" => report.Rows.Where(r => r.CostNow > 0).ToList(),
                "missing" => report.Rows.Where(r => r.CostNow <= 0).ToList(),
                _ => report.Rows.ToList(),
            };
            return Results.Ok(ListPaging.Of(rows, skip, take, new MissingCostSummary(
                report.From, report.To, report.SalesHt, report.UncostedHt, report.UncostedSales, report.ProductsWithoutCost,
                report.Rows.Count)));
        }).WithMetadata(new BranchScopedAttribute("a branch user's report is their own branch's, whatever branchId says (CallersBranch)"))
        .AtCallersBranch();
    }

    // =====================================================================================================
    // Low stock (/api/catalog: CatalogEndpoints.MapCatalogEndpoints, the "group" with AtCallersBranch)
    // =====================================================================================================

    /// <summary>
    /// <c>GET /api/catalog/products/low-stock/page</c>: what is at or under its reorder level, largest shortfall first,
    /// exactly as the list route reads it (copied from CatalogEndpoints), with the screen's filter: out of stock (zero or
    /// less) or low but still in stock.
    /// </summary>
    public static void MapLowStockPage(RouteGroupBuilder group)
    {
        group.MapGet("/products/low-stock/page", async (
            Guid? branchId, string? stock, int? skip, int? take, ClaimsPrincipal caller, PosDbContext db, CancellationToken ct) =>
        {
            var query = db.ProductStocks.AsNoTracking()
                .Where(s => s.StockQty <= s.ReorderLevel);
            if (branchId is not null) query = query.Where(s => s.BranchId == branchId);
            if (await BranchScope.ForStockAsync(caller, db, ct) is Guid own) query = query.Where(s => s.BranchId == own);

            var rows = await query
                .Join(db.Products.AsNoTracking().Where(p => p.TrackStock && p.IsActive && !p.IsDeleted),
                    s => s.ProductId, p => p.Id, (s, p) => new { s, p })
                .Join(db.Branches.AsNoTracking(), x => x.s.BranchId, b => b.Id, (x, b) => new LowStockDto(
                    x.p.Id, x.p.Sku, x.p.NameFr, x.s.BranchId, b.NameFr, x.s.StockQty, x.s.ReorderLevel))
                .ToListAsync(ct);
            var all = rows.Count;
            // The same line the stock column draws in red: zero or less is out.
            IEnumerable<LowStockDto> shown = stock switch
            {
                "out" => rows.Where(r => r.StockQty <= 0),
                "low" => rows.Where(r => r.StockQty > 0),
                _ => rows,
            };
            var list = shown.OrderBy(x => x.StockQty - x.ReorderLevel).ThenBy(x => x.ProductId).ThenBy(x => x.BranchId).ToList();
            return Results.Ok(ListPaging.Of(list, skip, take, new LowStockSummary(all)));
        }).WithMetadata(new BranchScopedAttribute("a non-admin's shortfalls are their own branch's (CallersBranch sets branchId)"));
    }

    // =====================================================================================================
    // Fixed assets (/api/fixed-assets: FixedAssetEndpoints.MapFixedAssetEndpoints, group with AtCallersBranch)
    // =====================================================================================================

    /// <summary>
    /// <c>GET /api/fixed-assets/register/page</c>: the register's pools a page at a time, with the screen's filters (the
    /// method as the life column says it; fully depreciated or not). The figures are the register's, whatever the
    /// filters (<see cref="FixedAssetRegisterReport.RegisterAsync"/>, as the list route).
    /// </summary>
    public static void MapFixedAssetRegisterPage(RouteGroupBuilder group)
    {
        group.MapGet("/register/page", async (
            int? year, int? month, Guid? branchId, string? method, string? depreciated, int? skip, int? take,
            [FromServices] FixedAssetRegisterReport register, CancellationToken ct) =>
        {
            var result = await register.RegisterAsync(year, month, branchId, ct);
            if (result.Code is not null || result.Value is null) return Refused(result.Status, result.Code, result.Error);
            var r = result.Value;
            IEnumerable<FixedAssetPoolDto> pools = r.Pools;
            if (method is "StraightLine" or "DecliningBalance")
                pools = pools.Where(p => (p.Method == "DecliningBalance" ? "DecliningBalance" : "StraightLine") == method);
            pools = depreciated switch
            {
                "yes" => pools.Where(p => p.NetBookValue <= 0),
                "no" => pools.Where(p => p.NetBookValue > 0),
                _ => pools,
            };
            return Results.Ok(ListPaging.Of(pools.ToList(), skip, take, new FixedAssetRegisterSummary(
                r.Active, r.SwitchMonth, r.Year, r.Month, r.MonthToDate, r.BranchId,
                r.Cost, r.AccumulatedDepreciation, r.NetBookValue, r.Units, r.PostedThrough, r.Pools.Count)));
        }).WithMetadata(new BranchScopedAttribute("a non-admin's branchId is replaced by their own branch (CallersBranch)"));
    }

    /// <summary>
    /// <c>GET /api/fixed-assets/register/history/page</c>: one pool's events a page at a time, filtered by kind and by
    /// posted or not; the pool, its schedule and the kinds its history holds come in the summary.
    /// </summary>
    public static void MapFixedAssetHistoryPage(RouteGroupBuilder group)
    {
        group.MapGet("/register/history/page", async (
            Guid productId, Guid branchId, string? kind, string? posted, int? skip, int? take,
            [FromServices] FixedAssetRegisterReport register, CancellationToken ct) =>
        {
            var result = await register.HistoryAsync(productId, branchId, ct);
            if (result.Code is not null || result.Value is null) return Refused(result.Status, result.Code, result.Error);
            var h = result.Value;
            IEnumerable<FixedAssetHistoryLineDto> lines = h.Lines;
            if (!string.IsNullOrEmpty(kind) && kind != "all") lines = lines.Where(l => l.Kind == kind);
            lines = posted switch
            {
                "yes" => lines.Where(l => l.Posted),
                "no" => lines.Where(l => !l.Posted),
                _ => lines,
            };
            return Results.Ok(ListPaging.Of(lines.ToList(), skip, take, new FixedAssetHistorySummary(
                h.ProductId, h.Sku, h.NameAr, h.NameFr, h.NameEn, h.BranchId, h.BranchCode, h.Pool, h.Schedule,
                h.Lines.Select(l => l.Kind).Distinct().ToList(), h.Lines.Count)));
        }).WithMetadata(new BranchScopedAttribute("a non-admin's branchId is replaced by their own branch (CallersBranch)"));
    }

    /// <summary>
    /// <c>GET /api/fixed-assets/depreciation/page</c>: the month's depreciation entries a page at a time (the month is
    /// posted whole: no filter); its totals, checks, accounts and postings come in the summary.
    /// </summary>
    public static void MapFixedAssetDepreciationPage(RouteGroupBuilder group)
    {
        group.MapGet("/depreciation/page", async (
            int? year, int? month, Guid? branchId, int? skip, int? take,
            [FromServices] FixedAssetDepreciationReport report, CancellationToken ct) =>
        {
            var result = await report.MonthAsync(year, month, branchId, ct);
            if (result.Code is not null || result.Value is null) return Refused(result.Status, result.Code, result.Error);
            var d = result.Value;
            return Results.Ok(ListPaging.Of(d.Entries, skip, take, new FixedAssetDepreciationSummary(
                d.Year, d.Month, d.MonthToDate, d.BranchId, d.Active, d.SwitchMonth, d.Accounts, d.Checks, d.UnmappedSlots,
                d.Notes, d.TotalDebit, d.TotalCredit, d.Postings, d.Entries.Count)));
        }).WithMetadata(new BranchScopedAttribute("a non-admin's branchId is replaced by their own branch (CallersBranch)"));
    }

    // =====================================================================================================
    // Stock valuation (/api/valuation/shadow "group" and /api/valuation/sales-cost "costing": ValuationEndpoints)
    // =====================================================================================================

    /// <summary>
    /// <c>GET /api/valuation/shadow/page</c>: the shadow valuation's rows a page at a time, with the screen's filters
    /// (the quantity's sign, the difference's sign, whether a row has notes). Settings and totals are the report's,
    /// whatever the filters; the branch rule and the company-wide refusal are the list route's.
    /// </summary>
    public static void MapValuationPage(RouteGroupBuilder group)
    {
        group.MapGet("/page", async (
            Guid? branchId, string? qty, string? difference, string? notes, int? skip, int? take,
            ClaimsPrincipal caller, PosDbContext db, [FromServices] StockValuationShadow shadow, CancellationToken ct) =>
        {
            var only = await BranchScope.ForAsync(caller, db, ct);
            if (only is not null && await ValuationCompanyWideAsync(shadow, ct)) return ValuationRefused();
            var report = await shadow.ReportAsync(only ?? branchId, ct);

            IEnumerable<StockValuationRowDto> rows = report.Rows;
            rows = qty switch
            {
                "positive" => rows.Where(x => x.ClosingQty > 0),
                "zero" => rows.Where(x => x.ClosingQty == 0),
                "negative" => rows.Where(x => x.ClosingQty < 0),
                _ => rows,
            };
            rows = difference switch
            {
                "above" => rows.Where(x => x.Difference > 0),
                "below" => rows.Where(x => x.Difference < 0),
                "none" => rows.Where(x => x.Difference == 0),
                _ => rows,
            };
            rows = notes switch
            {
                "yes" => rows.Where(HasNotes),
                "no" => rows.Where(x => !HasNotes(x)),
                _ => rows,
            };
            return Results.Ok(ListPaging.Of(rows.ToList(), skip, take,
                new ValuationSummary(report.Settings, report.GeneratedAtUtc, report.Totals, report.Rows.Count)));
        }).WithMetadata(new BranchScopedAttribute("a branch user reads their own location, and only with one average per location (CallersBranch)"));
    }

    /// <summary>
    /// <c>GET /api/valuation/shadow/products/{productId}/page</c>: one product's movements as the shadow replayed them,
    /// a page at a time. Each line carries the quantity and average after it, computed over every movement in order
    /// before paging; the settings, the summary row and the places come in the summary.
    /// </summary>
    public static void MapValuationProductPage(RouteGroupBuilder group)
    {
        group.MapGet("/products/{productId:guid}/page", async (
            Guid productId, int? skip, int? take, ClaimsPrincipal caller, PosDbContext db,
            [FromServices] StockValuationShadow shadow, CancellationToken ct) =>
        {
            var only = await BranchScope.ForAsync(caller, db, ct);
            if (only is not null && await ValuationCompanyWideAsync(shadow, ct)) return ValuationRefused();
            if (await shadow.ProductAsync(productId, only, ct) is not { } detail) return Results.NotFound();
            return Results.Ok(ListPaging.Of(detail.Lines, skip, take,
                new ValuationProductSummary(productId, detail.Settings, detail.Summary, detail.Places, detail.Lines.Count)));
        }).WithMetadata(new BranchScopedAttribute("a branch user reads their own location's movements, and only with one average per location"));
    }

    /// <summary>
    /// <c>GET /api/valuation/sales-cost/page</c>: the pending negative-stock gaps a page at a time. The summary is the
    /// costing status itself (<see cref="SalesCostSwitch.StatusAsync"/>) without its list of gaps — its figures,
    /// accounts and the gaps' total stay whole. A branch-bound caller is refused in words, as on the list route.
    /// </summary>
    public static void MapSalesCostGapsPage(RouteGroupBuilder costing)
    {
        costing.MapGet("/page", async (
            int? skip, int? take, ClaimsPrincipal caller, PosDbContext db, [FromServices] SalesCostSwitch salesCost, CancellationToken ct) =>
        {
            if (await BranchScope.ForAsync(caller, db, ct) is not null) return ValuationRefused();
            var status = await salesCost.StatusAsync(ct);
            return Results.Ok(ListPaging.Of(status.PendingGaps, skip, take, status with { PendingGaps = [] }));
        });
    }

    // =====================================================================================================
    // Summaries: the figures over every filtered row, sent beside the page
    // =====================================================================================================

    public sealed record TrialBalanceSummary(
        DateTime From, DateTime To, Guid? BranchId, bool Consolidated, decimal TotalDebit, decimal TotalCredit, bool Balanced);

    public sealed record AccountStatementSummary(
        Guid AccountId, string Code, string NameAr, string NameFr, string NameEn, string Type,
        DateTime From, DateTime To, Guid? BranchId, decimal Opening, decimal Closing,
        decimal TotalDebit, decimal TotalCredit, decimal PageOpening);

    public sealed record JournalSummary(IReadOnlyList<string> Authors, int All);

    public sealed record BookClosingSummary(DateOnly? ClosedThrough, BookClosingDto? Last, int All);

    public sealed record ChequesSummary(
        int PendingCount, decimal PendingTotal, int ConfirmedCount, decimal ConfirmedTotal,
        int BouncedCount, decimal BouncedTotal, int All);

    /// <summary>One row of Awaiting collection: a till payment (<c>sale</c>) or a receipt, as the screen's <c>AwaitingRow</c>.</summary>
    public sealed record AwaitingRow(
        string Source, Guid Id, string Reference, Guid? CustomerId, string? CustomerName, string MethodKey, decimal Amount,
        string? InstrumentNumber, string? InstrumentBank, DateTime? InstrumentDueDate, DateTime TakenAtUtc, Guid? BranchId);

    public sealed record AwaitingSummary(
        decimal Total, int Count, decimal OverdueTotal, int OverdueCount, IReadOnlyList<string> MethodKeys, int All);

    public sealed record CashMovementsSummary(
        Guid CashBoxId, decimal TotalIn, decimal TotalOut, decimal? Balance, decimal? PageOpening, decimal? PageClosing);

    public sealed record SalesSummary(IReadOnlyList<string> Cashiers, int All);

    public sealed record MissingCostSummary(
        DateOnly From, DateOnly To, decimal SalesHt, decimal UncostedHt, int UncostedSales, int ProductsWithoutCost, int All);

    public sealed record LowStockSummary(int All);

    public sealed record FixedAssetRegisterSummary(
        bool Active, string? SwitchMonth, int Year, int Month, bool MonthToDate, Guid? BranchId,
        decimal Cost, decimal AccumulatedDepreciation, decimal NetBookValue, decimal Units, string? PostedThrough, int PoolCount);

    public sealed record FixedAssetHistorySummary(
        Guid ProductId, string Sku, string NameAr, string NameFr, string NameEn, Guid BranchId, string BranchCode,
        FixedAssetPoolDto? Pool, IReadOnlyList<FixedAssetScheduleYearDto>? Schedule, IReadOnlyList<string> Kinds, int LineCount);

    public sealed record FixedAssetDepreciationSummary(
        int Year, int Month, bool MonthToDate, Guid? BranchId, bool Active, string? SwitchMonth,
        IReadOnlyList<PracticeLedgerAccountDto> Accounts, IReadOnlyList<PracticeLedgerCheckDto> Checks,
        IReadOnlyList<string> UnmappedSlots, IReadOnlyList<string> Notes, decimal TotalDebit, decimal TotalCredit,
        IReadOnlyList<PracticeLedgerPostingDto> Postings, int EntryCount);

    public sealed record ValuationSummary(
        StockValuationSettingsDto Settings, DateTime GeneratedAtUtc, StockValuationTotalsDto Totals, int All);

    public sealed record ValuationProductSummary(
        Guid ProductId, StockValuationSettingsDto Settings, StockValuationRowDto? Summary,
        IReadOnlyList<StockValuationRowDto> Places, int LineCount);

    // =====================================================================================================
    // Helpers, copied where the list routes keep theirs private
    // =====================================================================================================

    /// <summary>LedgerEndpoints.Window: the current month by default, and the reports' ceiling on a window's width.</summary>
    private static (DateTime From, DateTime To, IResult? Error) LedgerWindow(DateTime? from, DateTime? to)
    {
        var start = from ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = to ?? start.AddMonths(1);
        if (end <= start) return (start, end, Results.BadRequest("'to' must be after 'from'."));
        if ((end - start).TotalDays > LedgerReports.MaxPeriodDays)
            return (start, end, Results.BadRequest($"Date range too wide — max {LedgerReports.MaxPeriodDays} days per request."));
        return (start, end, null);
    }

    /// <summary>LedgerReports.Natural: assets and expenses grow with debits, everything else with credits.</summary>
    private static decimal Natural(LedgerAccountType type, decimal signed) =>
        type is LedgerAccountType.Asset or LedgerAccountType.Expense ? signed : -signed;

    /// <summary>LedgerEndpoints.OriginalDatesAsync: the date of each entry these reversals correct.</summary>
    private static async Task<Dictionary<Guid, DateTime>> OriginalDatesAsync(
        PosDbContext db, IReadOnlyCollection<LedgerEntry> entries, CancellationToken ct)
    {
        var ids = entries.Where(e => e.ReversalOfEntryId is not null).Select(e => e.ReversalOfEntryId!.Value).Distinct().ToList();
        return ids.Count == 0
            ? []
            : await db.LedgerEntries.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.EntryDate, ct);
    }

    /// <summary>LedgerEndpoints.Map: one journal entry as the journal sends it.</summary>
    private static LedgerEntryDto MapEntry(
        LedgerEntry e, IReadOnlyDictionary<Guid, LedgerAccount> accounts, IReadOnlyDictionary<Guid, string> branches,
        IReadOnlyDictionary<Guid, DateTime> originals) =>
        new(e.Id, e.EntryDate, e.CreatedAtUtc, e.BranchId, branches.GetValueOrDefault(e.BranchId),
            e.SourceType, e.SourceId, e.Description, e.IsReversal, e.ReversalOfEntryId,
            e.IsInterBranch, e.CounterpartBranchId, e.CreatedBy,
            e.Lines.Sum(l => l.Debit),
            e.Lines
                .OrderByDescending(l => l.Debit)
                .Select(l =>
                {
                    var account = accounts.GetValueOrDefault(l.AccountId);
                    return new LedgerEntryLineDto(
                        l.Id, l.AccountId, account?.Code ?? "?", account?.NameFr ?? "?",
                        account?.Type.ToString() ?? "?", l.BranchId, l.Debit, l.Credit, l.Memo);
                })
                .ToList(),
            e.ReversalOfEntryId is Guid of && originals.TryGetValue(of, out var date) ? date : null);

    /// <summary>LedgerEndpoints.MapClosing.</summary>
    private static BookClosingDto MapClosing(BookClosing c) => new(c.Id, c.ClosedThrough, c.IsReopen, c.Reason, c.UserName, c.AtUtc);

    /// <summary>The day an instant falls on, on the company's clock (a stored instant is UTC).</summary>
    private static DateOnly CompanyDay(DateTime at, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(at, DateTimeKind.Utc), zone));

    private static bool InWindow(DateOnly day, DateOnly? from, DateOnly? to) =>
        (from is not DateOnly start || day >= start) && (to is not DateOnly end || day <= end);

    /// <summary>
    /// The due word the screens show (receivable-view.ts <c>dueWord</c>): before today overdue, today, within a week
    /// soon, later after that; none without a date. Days on the company's clock.
    /// </summary>
    private static string DueWord(DateTime? due, DateOnly today, TimeZoneInfo zone)
    {
        if (due is not DateTime at) return "none";
        var days = CompanyDay(at, zone).DayNumber - today.DayNumber;
        return days < 0 ? "overdue" : days == 0 ? "today" : days <= 7 ? "soon" : "later";
    }

    /// <summary>Whether a valuation row has anything under "good to know" (stock-valuation.component.ts <c>hasNotes</c>).</summary>
    private static bool HasNotes(StockValuationRowDto x) =>
        x.OutputsAtReference != 0 || x.OutflowsWithoutAverage != 0 || x.NegativeStockGap != 0 || x.Unclassified != 0;

    /// <summary>FixedAssetEndpoints.Answer's refusal: the report's own status, code and words.</summary>
    private static IResult Refused(int status, string? code, string? error) =>
        Results.Problem(detail: error, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });

    /// <summary>ValuationEndpoints.CompanyWideAsync.</summary>
    private static async Task<bool> ValuationCompanyWideAsync(StockValuationShadow shadow, CancellationToken ct) =>
        (await shadow.SettingsAsync(ct)).Scope == nameof(StockValuationScope.Company);

    /// <summary>ValuationEndpoints.Refused: a branch user under one company-wide average, refused in words.</summary>
    private static IResult ValuationRefused() => Results.Problem(
        "With one average cost for the company, the valuation pools every branch's goods: it is read by a user who sees every branch.",
        statusCode: StatusCodes.Status403Forbidden,
        extensions: new Dictionary<string, object?> { ["code"] = ValuationEndpoints.CompanyWideCode });
}
