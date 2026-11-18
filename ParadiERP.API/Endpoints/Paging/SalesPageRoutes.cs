using ParadiERP.Application.Catalog;
using ParadiERP.Application.Contracts;
using ParadiERP.Application.Loyalty;
using ParadiERP.Application.Receivables;
using ParadiERP.Application.Regional;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Customers;
using ParadiERP.Infrastructure.Ledger;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Receivables;
using ParadiERP.Infrastructure.Rentals;
using ParadiERP.Infrastructure.Suppliers;
using ParadiERP.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.API.Endpoints;

/// <summary>
/// The sales and receivables lists a page at a time (owner, 2026-11-17: "I need to paginate in
/// backend, not frontend"): gift cards and their history, suppliers, their statement and opening
/// corrections, a customer's opening corrections and quantity statement, the debtor book (ageing,
/// invoices, receipts, credit notes, statement) and payment vouchers.
///
/// <para>
/// Each route sits beside the list route it pages, in the same group, so the group's filters
/// (subscription, <c>AtCallersBranch</c> rewriting <c>branchId</c>) apply to it as they do to the
/// list. The list routes are unchanged: tills, pickers, exports and papers read them. Every
/// search and filter the screen used to apply in the browser runs here, over all rows; figures
/// over the list (tiles, totals rows, the choices a filter offers) are computed over every
/// filtered row and sent as the page's summary; a running balance is computed over every row and
/// the summary carries the balance before the page's first row.
/// </para>
/// </summary>
public static class SalesPageRoutes
{
    // ---------- gift cards (beside GiftCardEndpoints, group /api/catalog) ----------

    /// <summary><c>GET /api/catalog/gift-cards/page</c> and <c>GET /api/catalog/gift-cards/{id}/movements/page</c>.</summary>
    public static void MapGiftCardPages(RouteGroupBuilder group)
    {
        group.MapGet("/gift-cards/page", async (
            string? q, string? state, string? balance, string? customer, int? skip, int? take,
            PosDbContext db, CancellationToken ct) =>
        {
            // The list route's query (GiftCardEndpoints, GET /gift-cards): not deleted, the code
            // searched as a till types it. It cut the list at 500 cards; a page needs no cut.
            var query = db.GiftCards.AsNoTracking().Where(g => !g.IsDeleted);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = GiftCardRules.NormalizeCode(q);
                query = query.Where(g => g.Code.Contains(term));
            }
            // The three states the table says, decided as it decides them: inactive first, then
            // expired (its day has come), else valid.
            var now = DateTime.UtcNow;
            query = state switch
            {
                "inactive" => query.Where(g => !g.IsActive),
                "expired" => query.Where(g => g.IsActive && g.ExpiresAtUtc != null && g.ExpiresAtUtc <= now),
                "valid" => query.Where(g => g.IsActive && (g.ExpiresAtUtc == null || g.ExpiresAtUtc > now)),
                _ => query,
            };
            query = balance switch
            {
                "left" => query.Where(g => g.Balance > 0),
                "spent" => query.Where(g => g.Balance <= 0),
                _ => query,
            };
            query = customer switch
            {
                "linked" => query.Where(g => g.CustomerId != null),
                "none" => query.Where(g => g.CustomerId == null),
                _ => query,
            };

            var page = await query.OrderBy(g => g.Code).ThenBy(g => g.Id).ToPageAsync(skip, take,
                rows => Task.FromResult<IReadOnlyList<GiftCardDto>>(rows.Select(SyncService.MapForOffice).ToList()), ct);
            return Results.Ok(page);
        });

        // One card's history, newest first, as GET /gift-cards/{id}/movements orders it (that route
        // stops at 200); each row carries the balance after it, so a page needs nothing before it.
        group.MapGet("/gift-cards/{id:guid}/movements/page", async (
            Guid id, int? skip, int? take, PosDbContext db, CancellationToken ct) =>
            Results.Ok(await db.GiftCardMovements.AsNoTracking().Where(m => m.GiftCardId == id)
                .OrderByDescending(m => m.CreatedAtUtc).ThenBy(m => m.Id)
                .ToPageAsync(skip, take, ct)));
    }

    // ---------- a customer's opening corrections (beside CatalogEndpoints, group /api/catalog) ----------

    /// <summary><c>GET /api/catalog/customers/{id}/opening-corrections/page</c>, oldest first as the list route orders them.</summary>
    public static void MapCustomerOpeningCorrectionsPage(RouteGroupBuilder group)
    {
        group.MapGet("/customers/{id:guid}/opening-corrections/page", async (
            Guid id, int? skip, int? take, PosDbContext db, CancellationToken ct) =>
            Results.Ok(await db.CustomerOpeningCorrections.AsNoTracking()
                .Where(c => c.CustomerId == id)
                .OrderBy(c => c.CreatedAtUtc).ThenBy(c => c.Id)
                .ToPageAsync(skip, take,
                    rows => Task.FromResult<IReadOnlyList<OpeningCorrectionDto>>(rows.Select(CustomerOpeningBalances.ToDto).ToList()),
                    ct)));
    }

    // ---------- suppliers (beside SupplierEndpoints, group /api/suppliers) ----------

    /// <summary>
    /// <c>GET /api/suppliers/page</c>, <c>GET /api/suppliers/{id}/opening-corrections/page</c> and
    /// <c>GET /api/suppliers/{id}/statement/page</c>.
    /// </summary>
    public static void MapSupplierPages(RouteGroupBuilder group)
    {
        group.MapGet("/page", async (
            string? q, string? balance, string? missing, Guid? id, int? skip, int? take,
            PosDbContext db, CancellationToken ct) =>
        {
            // The list route's query (SupplierEndpoints, GET /): not deleted, by name.
            var query = db.Suppliers.AsNoTracking().Where(s => !s.IsDeleted);
            if (id is Guid one) query = query.Where(s => s.Id == one);
            // The same signs the balance column words: above zero we owe them, below zero they owe us.
            query = balance switch
            {
                "weOwe" => query.Where(s => s.Balance > 0),
                "owesUs" => query.Where(s => s.Balance < 0),
                "settled" => query.Where(s => s.Balance == 0),
                _ => query,
            };
            if (missing == "phone") query = query.Where(s => s.Phone == null || s.Phone == "");

            var rows = (await query.OrderBy(s => s.Name).ThenBy(s => s.Id).ToListAsync(ct))
                .Select(SupplierAccounts.ToDto).ToList();
            // In memory, as the screen did: the search ignores case over code, name and phone
            // together, and the fiscal identifiers are a JSON column SQL cannot look inside.
            var needle = (q ?? "").Trim().ToLowerInvariant();
            var kept = rows.Where(s =>
                (needle.Length == 0 || $"{s.Code} {s.Name} {s.Phone ?? ""}".ToLowerInvariant().Contains(needle, StringComparison.Ordinal)) &&
                (missing != "fiscalIds" || !s.FiscalIds.Values.Any(v => !string.IsNullOrWhiteSpace(v))))
                .ToList();
            return Results.Ok(ListPaging.Of(kept, skip, take));
        });

        group.MapGet("/{id:guid}/opening-corrections/page", async (
            Guid id, int? skip, int? take, PosDbContext db, CancellationToken ct) =>
            Results.Ok(await db.SupplierOpeningCorrections.AsNoTracking()
                .Where(c => c.SupplierId == id)
                .OrderBy(c => c.CreatedAtUtc).ThenBy(c => c.Id)
                .ToPageAsync(skip, take,
                    rows => Task.FromResult<IReadOnlyList<SupplierOpeningCorrectionDto>>(rows.Select(SupplierAccounts.ToDto).ToList()),
                    ct)));

        group.MapGet("/{id:guid}/statement/page", async (
            Guid id, string? from, string? to, int? skip, int? take,
            SupplierAccounts accounts, IRegionalSettingsProvider regional, CancellationToken ct) =>
        {
            // Plain dates are the company's days, `to` included, as the customer statement reads
            // them; an instant is still read as one. Then the statement route's own window rule
            // (SupplierEndpoints, GET /{id}/statement): the current month by default, at most
            // LedgerReports.MaxPeriodDays.
            var zone = CompanyDays.Zone(regional.Get().TimeZone);
            if (!TryInstant(from, zone, endOfDay: false, out var fromAt) || !TryInstant(to, zone, endOfDay: true, out var toAt))
                return Results.BadRequest("from and to are dates (yyyy-MM-dd) or instants.");
            var start = fromAt ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var end = toAt ?? start.AddMonths(1);
            if (end <= start) return Results.BadRequest("'to' must be after 'from'.");
            if ((end - start).TotalDays > LedgerReports.MaxPeriodDays)
                return Results.BadRequest($"Date range too wide — max {LedgerReports.MaxPeriodDays} days per request.");

            var statement = await accounts.StatementAsync(id, start, end, ct);
            if (statement is null) return Results.NotFound("Unknown supplier.");
            var summary = new SupplierStatementPageSummary(
                statement.SupplierId, statement.SupplierCode, statement.SupplierName, statement.From, statement.To,
                statement.OpeningBalance, statement.ClosingBalance, statement.TotalDebit, statement.TotalCredit,
                statement.RecordedBalance, statement.BalanceDifference,
                PageOpening(statement.Rows, r => r.Balance, statement.OpeningBalance, skip, take));
            return Results.Ok(ListPaging.Of(statement.Rows, skip, take, summary));
        });
    }

    // ---------- the customer quantity statement (beside RentalEndpoints, group /api/rentals) ----------

    /// <summary>
    /// <c>GET /api/rentals/quantity-statement/items/page</c> and <c>…/contracts/page</c>: the same
    /// query as <c>GET /api/rentals/quantity-statement</c> (read by <see cref="RentalEndpoints.QuantityQuery"/>,
    /// so both refuse the same things), the statement built as it builds it, one of its two tables
    /// paged. A contract pages whole, its lines and its total together.
    /// </summary>
    public static void MapQuantityStatementPages(RouteGroupBuilder group)
    {
        group.MapGet("/quantity-statement/items/page", async (
            Guid customerId, string? from, string? to, bool? current, string? productIds, Guid? contractId,
            Guid? branchId, Guid? siteId, string? status, string? search, int? skip, int? take,
            CustomerQuantityStatements statements, CancellationToken ct) =>
        {
            var (query, error) = RentalEndpoints.QuantityQuery(statements, customerId, from, to, current, productIds, contractId,
                branchId, siteId, status, search);
            if (error is not null) return error;
            var statement = await statements.BuildAsync(query!, ct);
            if (statement is null) return Results.NotFound("Unknown customer.");
            return Results.Ok(ListPaging.Of(statement.Items, skip, take, QuantityHead(statement)));
        });

        group.MapGet("/quantity-statement/contracts/page", async (
            Guid customerId, string? from, string? to, bool? current, string? productIds, Guid? contractId,
            Guid? branchId, Guid? siteId, string? status, string? search, int? skip, int? take,
            CustomerQuantityStatements statements, CancellationToken ct) =>
        {
            var (query, error) = RentalEndpoints.QuantityQuery(statements, customerId, from, to, current, productIds, contractId,
                branchId, siteId, status, search);
            if (error is not null) return error;
            var statement = await statements.BuildAsync(query!, ct);
            if (statement is null) return Results.NotFound("Unknown customer.");
            return Results.Ok(ListPaging.Of(statement.Contracts, skip, take, QuantityHead(statement)));
        });
    }

    private static QuantityStatementPageSummary QuantityHead(QuantityStatementDto s) => new(
        s.CustomerId, s.CustomerCode, s.CustomerName, s.From, s.To, s.Current, s.FirstIssueOn, s.Summary, s.Warnings,
        HasTransfers: s.Items.Any(i => i.TransferNet != 0),
        HasContractTransfers: s.Contracts.Any(c => c.Lines.Any(l => l.TransferredIn != 0 || l.TransferredOut != 0)),
        ItemCount: s.Items.Count, ContractCount: s.Contracts.Count);

    // ---------- the debtor book (beside ReceivableEndpoints, groups `cash` and `group`) ----------

    /// <summary>
    /// <c>GET /api/receivables/invoices/page</c> and <c>GET /api/receivables/receipts/page</c>, on the
    /// <c>cash</c> group where their list routes are mapped.
    /// </summary>
    public static void MapReceivableCashPages(RouteGroupBuilder cash)
    {
        cash.MapGet("/invoices/page", async (
            Guid? customerId, string? status, bool? overdue, string? from, string? to, Guid? branchId,
            string? state, string? q, string? origin, string? taxMode, string? paper, Guid? id, int? skip, int? take,
            ReceivableDocuments receivables, PosDbContext db, IRegionalSettingsProvider regional, CancellationToken ct) =>
        {
            // The window is the company's days, `to` included; an instant is still read as one.
            var zone = CompanyDays.Zone(regional.Get().TimeZone);
            if (!TryInstant(from, zone, endOfDay: false, out var fromAt) || !TryInstant(to, zone, endOfDay: true, out var toAt))
                return Results.BadRequest("from and to are dates (yyyy-MM-dd) or instants.");

            // Copied from ReceivableEndpoints.MapInvoiceReads, GET /invoices: the caller's branch
            // (CallersBranch sets it for everyone but the Admin), the customer, the issue window.
            // That route cut the list at `take` (1000 at most) before its status filters; here
            // every invoice in the window is read, so a filter answers over all of them.
            var query = db.Invoices.AsNoTracking();
            if (branchId is Guid branch) query = query.Where(i => i.BranchId == branch);
            if (customerId is Guid cid) query = query.Where(i => i.CustomerId == cid);
            if (id is Guid one) query = query.Where(i => i.Id == one);
            if (fromAt is not null) query = query.Where(i => i.IssuedAtUtc >= fromAt);
            if (toAt is not null) query = query.Where(i => i.IssuedAtUtc < toAt);

            var heads = await query
                .OrderByDescending(i => i.IssuedAtUtc).ThenBy(i => i.Id)
                .Select(i => new InvoiceHead(i.Id, i.Number, i.CustomerId, i.Notes, i.IssuedAtUtc, i.DueDateUtc, i.Origin, i.TaxMode))
                .ToListAsync(ct);
            var standings = await receivables.StandingsAsync(heads.Select(h => h.Id).ToList(), ct);
            var customerIds = heads.Select(h => h.CustomerId).Distinct().ToList();
            var names = await db.Customers.AsNoTracking()
                .Where(c => customerIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.FullName, ct);

            var now = DateTime.UtcNow;
            var rows = heads.Select(h =>
            {
                var standing = standings[h.Id];
                return new InvoiceRow(
                    h.Id, h.Number, names.GetValueOrDefault(h.CustomerId), h.Notes, h.IssuedAtUtc,
                    h.Origin.ToString(), h.TaxMode.ToString(), standing.Outstanding, standing.Claimed, standing.Status,
                    // Nothing settled is nothing overdue, as the invoice DTO says it.
                    standing.Outstanding > 0 ? DebtAgeing.DaysOverdue(h.DueDateUtc, now) : 0);
            }).ToList();

            // The choices the screen offers: the origins and tax modes the window's invoices hold.
            var origins = rows.Select(r => r.Origin).Distinct().ToList();
            var taxModes = rows.Select(r => r.TaxMode).Distinct().ToList();

            var needle = ListText.Fold(q);
            var kept = rows.Where(r =>
                InvoiceStateMatches(r, state) &&
                // The list route's own two filters, with its meaning.
                (string.IsNullOrWhiteSpace(status) || string.Equals(r.Status, status, StringComparison.OrdinalIgnoreCase)) &&
                (overdue != true || (r.Outstanding > 0 && r.DaysOverdue > 0)) &&
                (needle.Length == 0 || ListText.Fold($"{r.Number} {r.CustomerName ?? "—"} {r.Notes ?? ""}").Contains(needle, StringComparison.Ordinal)) &&
                (IsAll(origin) || string.Equals(r.Origin, origin, StringComparison.OrdinalIgnoreCase)) &&
                (IsAll(taxMode) || string.Equals(r.TaxMode, taxMode, StringComparison.OrdinalIgnoreCase)) &&
                (paper switch { "yes" => r.Claimed > 0, "no" => !(r.Claimed > 0), _ => true }))
                .ToList();

            // What is owed and oldest first, then the newest: the screen's order.
            var ordered = kept
                .OrderByDescending(r => r.Outstanding > 0 ? 1 : 0)
                .ThenByDescending(r => r.DaysOverdue)
                .ThenByDescending(r => r.IssuedAtUtc)
                .ThenBy(r => r.Id)
                .ToList();

            // Over every invoice the filters keep, as the tile notes say.
            var owed = kept.Where(r => r.Outstanding > 0).ToList();
            var summary = new InvoicePageSummary(
                owed.Count, owed.Sum(r => r.Outstanding), owed.Where(r => r.DaysOverdue > 0).Sum(r => r.Outstanding),
                kept.Sum(r => r.Claimed), origins, taxModes);

            var page = ListPaging.Of(ordered, skip, take, summary);
            var ids = page.Items.Select(r => r.Id).ToList();
            var invoices = await db.Invoices.AsNoTracking().Include(i => i.Lines)
                .Where(i => ids.Contains(i.Id))
                .ToDictionaryAsync(i => i.Id, ct);
            var dtos = await MapInvoicesAsync(ids.Select(x => invoices[x]).ToList(), receivables, db, ct);
            return Results.Ok(new PageResult<InvoiceDto>(dtos, page.Total, page.Skip, page.Take, summary));
        });

        cash.MapGet("/receipts/page", async (
            Guid? customerId, string? status, Guid? branchId, string? q, string? method, string? due, string? unallocated,
            Guid? id, int? skip, int? take, PosDbContext db, IRegionalSettingsProvider regional, CancellationToken ct) =>
        {
            // Copied from ReceivableEndpoints.MapReceipts, GET /receipts: the caller's branch, the
            // customer, the state; oldest due first. That route cut the list at `take`.
            var query = db.ReceiptVouchers.AsNoTracking().Include(v => v.Allocations).AsQueryable();
            if (branchId is Guid branch) query = query.Where(v => v.BranchId == branch);
            if (customerId is Guid cid) query = query.Where(v => v.CustomerId == cid);
            if (Enum.TryParse<PaymentStatus>(status, true, out var wanted)) query = query.Where(v => v.Status == wanted);
            if (id is Guid one) query = query.Where(v => v.Id == one);

            var vouchers = await query
                .OrderBy(v => v.InstrumentDueDate ?? v.ReceivedAtUtc).ThenBy(v => v.Id)
                .ToListAsync(ct);
            var all = await MapReceiptsAsync(vouchers, db, ct);

            var zone = CompanyDays.Zone(regional.Get().TimeZone);
            var today = CompanyDays.Today(DateTime.UtcNow, zone);
            string Due(ReceiptVoucherDto v) => DueWord(v.InstrumentDueDate, today, zone);

            // The tiles and the choices are over the receipts of the state, customer and branch
            // asked for — before the search and the other filters, as the screen added them up.
            var pending = all.Where(v => v.Status == nameof(PaymentStatus.Pending)).ToList();
            var summary = new ReceiptPageSummary(
                pending.Sum(v => v.Amount),
                pending.Where(v => Due(v) == "overdue").Sum(v => v.Amount),
                all.Where(v => v.Status == nameof(PaymentStatus.Confirmed)).Sum(v => v.Unallocated),
                all.Select(v => v.MethodKey).Distinct().ToList(),
                pending.Count > 0);

            var needle = ListText.Fold(q);
            var kept = all.Where(v =>
                (needle.Length == 0 ||
                    new[] { v.Number, v.CustomerName ?? "", v.InstrumentNumber ?? "", v.Reference ?? "", v.InstrumentBank ?? "" }
                        .Any(text => ListText.Fold(text).Contains(needle, StringComparison.Ordinal))) &&
                (IsAll(method) || v.MethodKey == method) &&
                // The due date's word applies to paper still pending.
                (IsAll(due) || (v.Status == nameof(PaymentStatus.Pending) && Due(v) == due)) &&
                (unallocated switch { "yes" => v.Unallocated > 0, "no" => !(v.Unallocated > 0), _ => true }))
                .ToList();
            return Results.Ok(ListPaging.Of(kept, skip, take, summary));
        });
    }

    /// <summary>
    /// <c>GET /api/receivables/credit-notes/page</c>, <c>…/statement/page</c> and <c>…/ageing/page</c>,
    /// on the manager's <c>group</c> where their list routes are mapped.
    /// </summary>
    public static void MapReceivableReportPages(RouteGroupBuilder group)
    {
        group.MapGet("/credit-notes/page", async (
            Guid? customerId, Guid? invoiceId, Guid? branchId, int? skip, int? take, PosDbContext db, CancellationToken ct) =>
        {
            // Copied from ReceivableEndpoints.MapInvoices, GET /credit-notes: the caller's branch,
            // the customer, the invoice; newest first.
            IQueryable<CreditNote> query = db.CreditNotes.AsNoTracking();
            if (branchId is Guid branch) query = query.Where(n => n.BranchId == branch);
            if (customerId is Guid cid) query = query.Where(n => n.CustomerId == cid);
            if (invoiceId is Guid iid) query = query.Where(n => n.InvoiceId == iid);

            // The totals line adds up every note, not the page. Summed here: SQLite stores decimal as TEXT.
            var figures = await query.Select(n => new { n.SubTotalHt, n.TaxTotal, n.GrandTotalTtc }).ToListAsync(ct);
            var summary = new CreditNotePageSummary(
                figures.Sum(f => f.SubTotalHt), figures.Sum(f => f.TaxTotal), figures.Sum(f => f.GrandTotalTtc));

            var page = await query.OrderByDescending(n => n.IssuedAtUtc).ThenBy(n => n.Id)
                .Select(n => n.Id).ToPageAsync(skip, take, ct, summary);
            var ids = page.Items.ToList();
            var notes = await db.CreditNotes.AsNoTracking().Include(n => n.Lines)
                .Where(n => ids.Contains(n.Id)).ToDictionaryAsync(n => n.Id, ct);
            var invoiceIds = notes.Values.Select(n => n.InvoiceId).Distinct().ToList();
            var numbers = await db.Invoices.AsNoTracking()
                .Where(i => invoiceIds.Contains(i.Id))
                .ToDictionaryAsync(i => i.Id, i => i.Number, ct);
            var dtos = ids.Select(x => notes[x]).Select(n => MapCreditNote(n, numbers.GetValueOrDefault(n.InvoiceId))).ToList();
            return Results.Ok(new PageResult<CreditNoteDto>(dtos, page.Total, page.Skip, page.Take, summary));
        });

        group.MapGet("/statement/page", async (
            Guid customerId, string? from, string? to, string? kind, int? skip, int? take, HttpContext http,
            IRegionalSettingsProvider regional, CustomerStatements statements, CancellationToken ct) =>
        {
            // Copied from ReceivableEndpoints.MapReports, GET /statement: the window, its ceiling,
            // and the kind filter that is the admin's alone.
            var zone = CompanyDays.Zone(regional.Get().TimeZone);
            if (!TryInstant(from, zone, endOfDay: false, out var fromAt) || !TryInstant(to, zone, endOfDay: true, out var toAt))
                return Results.BadRequest("from and to are dates (yyyy-MM-dd) or instants.");
            var (fromUtc, toUtc, error) = Window(fromAt, toAt);
            if (error is not null) return error;

            if (StatementFilters.Parse(kind) is not StatementFilter filter)
                return Results.BadRequest("kind is all, sales or rentals.");
            if (filter != StatementFilter.All
                && !ParadiERP.Infrastructure.Authorization.ServerAuthorization.Has(http.User, PermissionCatalog.CustomersAccountsFilter))
                return Results.Problem("Narrowing a statement to sales or rentals is not open to this account.",
                    statusCode: StatusCodes.Status403Forbidden);

            var statement = await statements.BuildAsync(customerId, fromUtc, toUtc, ct, filter);
            if (statement is null) return Results.NotFound("Unknown customer.");
            var summary = new CustomerStatementPageSummary(
                statement.CustomerId, statement.CustomerCode, statement.CustomerName, statement.From, statement.To,
                statement.OpeningBalance, statement.ClosingBalance, statement.TotalDebit, statement.TotalCredit,
                statement.PendingNotes, statement.TotalOwed, statement.RecordedBalance, statement.BalanceDifference,
                statement.CreditLimit, statement.InvoicedOutstanding, statement.UninvoicedBalance, statement.Filter,
                PageOpening(statement.Rows, r => r.Balance, statement.OpeningBalance, skip, take));
            return Results.Ok(ListPaging.Of(statement.Rows, skip, take, summary));
        });

        group.MapGet("/ageing/page", async (
            DateTime? asOf, Guid? customerId, string? q, int? skip, int? take, CustomerStatements statements, CancellationToken ct) =>
        {
            // The ageing route's report (GET /ageing), built as it builds it — company-wide, as that route is.
            var ageing = await statements.AgeAsync(asOf ?? DateTime.UtcNow, customerId, ct);
            var needle = ListText.Fold(q);
            // Latest first, then largest; customers equal on both keep the report's order by name
            // (a stable sort), so the customer to call first is the first row.
            var rows = ageing.Customers
                .Where(r => needle.Length == 0
                    || ListText.Fold(r.CustomerName).Contains(needle, StringComparison.Ordinal)
                    || ListText.Fold(r.CustomerCode).Contains(needle, StringComparison.Ordinal))
                .OrderByDescending(r => r.OldestDaysOverdue).ThenByDescending(r => r.Total)
                .ToList();
            // The tiles and the totals line are the whole book's, whatever the search; the ids say
            // which customers the board holds, so the screen can offer the others.
            var summary = new AgeingPageSummary(
                ageing.AsOf, ageing.Total, ageing.Current, ageing.Days1To30, ageing.Days31To60, ageing.Days61To90, ageing.Over90,
                ageing.CreditBalance, ageing.LedgerBalance, ageing.Uninvoiced,
                ageing.Customers.Count, ageing.Customers.Select(c => c.CustomerId).ToList());
            return Results.Ok(ListPaging.Of(rows, skip, take, summary));
        });
    }

    // ---------- payment vouchers (beside PaymentVoucherEndpoints, group /api/payment-vouchers) ----------

    /// <summary><c>GET /api/payment-vouchers/page</c>.</summary>
    public static void MapPaymentVoucherPage(RouteGroupBuilder group)
    {
        group.MapGet("/page", async (
            Guid? branchId, Guid? customerId, Guid? returnNoteId, int? status, int? kind, string? method,
            string? from, string? to, string? q, Guid? id, int? skip, int? take,
            PosDbContext db, IRegionalSettingsProvider regional, CancellationToken ct) =>
        {
            // Copied from PaymentVoucherEndpoints, GET /: the caller's branch (CallersBranch), the
            // customer, the return note; newest first. That route cut the list at `take`.
            var scoped = db.PaymentVouchers.AsNoTracking();
            if (branchId is Guid b) scoped = scoped.Where(v => v.BranchId == b);
            if (customerId is Guid c) scoped = scoped.Where(v => v.CustomerId == c);
            if (returnNoteId is Guid n) scoped = scoped.Where(v => v.ReturnNoteId == n);

            // The two figures a manager opens the screen for: every pending and every approved
            // voucher of the branch, whatever the state chip and the filters.
            var open = await scoped
                .Where(v => v.Status == PaymentVoucherStatus.Pending || v.Status == PaymentVoucherStatus.Approved)
                .Select(v => new { v.Status, v.Amount })
                .ToListAsync(ct);

            var query = scoped;
            if (status is int s && Enum.IsDefined((PaymentVoucherStatus)s)) query = query.Where(v => v.Status == (PaymentVoucherStatus)s);

            // The choices the kind and method filters offer: those the chip's vouchers hold.
            var held = await query.OrderByDescending(v => v.CreatedAtUtc).ThenBy(v => v.Id)
                .Select(v => new { v.Kind, v.MethodKey })
                .ToListAsync(ct);
            var keys = held.Select(h => h.MethodKey).Distinct().ToList();
            var methodNames = await db.PaymentMethodDefs.AsNoTracking()
                .Where(m => keys.Contains(m.Key)).ToDictionaryAsync(m => m.Key, m => m.NameFr, ct);
            var summary = new PaymentVoucherPageSummary(
                open.Where(v => v.Status == PaymentVoucherStatus.Pending).Sum(v => v.Amount),
                open.Where(v => v.Status == PaymentVoucherStatus.Approved).Sum(v => v.Amount),
                held.Select(h => h.Kind).Distinct().OrderBy(voucherKind => voucherKind).ToList(),
                keys.Select(key => new VoucherMethodChoice(key, methodNames.GetValueOrDefault(key) ?? key)).ToList());

            if (kind is int wantedKind && Enum.IsDefined((PaymentVoucherKind)wantedKind))
                query = query.Where(v => v.Kind == (PaymentVoucherKind)wantedKind);
            if (!IsAll(method)) query = query.Where(v => v.MethodKey == method);
            if (id is Guid one) query = query.Where(v => v.Id == one);
            // A day range on the day the voucher was made, read in the company's zone, both days included.
            var zone = CompanyDays.Zone(regional.Get().TimeZone);
            if (CompanyDays.TryParseDay(from, out var fromDay))
            {
                var start = CompanyDays.StartUtc(fromDay, zone);
                query = query.Where(v => v.CreatedAtUtc >= start);
            }
            if (CompanyDays.TryParseDay(to, out var toDay))
            {
                var end = CompanyDays.StartUtc(toDay.AddDays(1), zone);
                query = query.Where(v => v.CreatedAtUtc < end);
            }
            var ordered = query.OrderByDescending(v => v.CreatedAtUtc).ThenBy(v => v.Id);

            var needle = ListText.Fold(q);
            if (needle.Length == 0)
                return Results.Ok(await ordered.ToPageAsync(skip, take,
                    async rows => (IReadOnlyList<PaymentVoucherDto>)await MapVouchersAsync(rows, db, ct), ct, summary));

            // The search folds Arabic and Latin as the till does, which SQL cannot: the vouchers'
            // searchable fields are read and matched here, and only the page is mapped.
            var heads = await ordered
                .Select(v => new { v.Id, v.Number, v.PayeeName, v.CustomerId, v.Purpose, v.InstrumentNumber, v.ReturnNoteId })
                .ToListAsync(ct);
            var customerIds = heads.Where(h => h.CustomerId != null).Select(h => h.CustomerId!.Value).Distinct().ToList();
            var customers = await db.Customers.AsNoTracking()
                .Where(x => customerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.FullName, ct);
            var noteIds = heads.Where(h => h.ReturnNoteId != null).Select(h => h.ReturnNoteId!.Value).Distinct().ToList();
            var notes = await db.ReturnNotes.AsNoTracking()
                .Where(x => noteIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Number, ct);
            var matching = heads.Where(h =>
                    new[]
                    {
                        h.Number, h.PayeeName ?? "", h.CustomerId is Guid hc ? customers.GetValueOrDefault(hc) ?? "" : "",
                        h.Purpose ?? "", h.InstrumentNumber ?? "", h.ReturnNoteId is Guid hn ? notes.GetValueOrDefault(hn) ?? "" : "",
                    }.Any(text => ListText.Fold(text).Contains(needle, StringComparison.Ordinal)))
                .Select(h => h.Id)
                .ToList();
            var page = ListPaging.Of(matching, skip, take, summary);
            var pageIds = page.Items.ToList();
            var vouchers = await db.PaymentVouchers.AsNoTracking()
                .Where(v => pageIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
            var dtos = await MapVouchersAsync(pageIds.Select(x => vouchers[x]).ToList(), db, ct);
            return Results.Ok(new PageResult<PaymentVoucherDto>(dtos, page.Total, page.Skip, page.Take, summary));
        });
    }

    // ---------- shared helpers ----------

    private static bool IsAll(string? value) => string.IsNullOrWhiteSpace(value) || value == "all";

    /// <summary>The invoices screen's state chips: open is anything still owed, overdue owed and late.</summary>
    private static bool InvoiceStateMatches(InvoiceRow r, string? state) => state switch
    {
        "open" => r.Outstanding > 0,
        "overdue" => r.DaysOverdue > 0 && r.Outstanding > 0,
        "settled" => r.Status == "Settled",
        "credited" => r.Status == "Credited",
        _ => true,
    };

    /// <summary>
    /// The word beside a pending receipt's due date (receivable-view.ts <c>dueWord</c>): its day
    /// against today, both in the company's zone; within a week is soon.
    /// </summary>
    private static string DueWord(DateTime? dueDate, DateOnly today, TimeZoneInfo zone)
    {
        if (dueDate is not DateTime due) return "none";
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(due, DateTimeKind.Utc), zone));
        var days = day.DayNumber - today.DayNumber;
        return days < 0 ? "overdue" : days == 0 ? "today" : days <= 7 ? "soon" : "later";
    }

    /// <summary>The running balance before the first row of the page asked for (the last page when it is past the end).</summary>
    private static decimal PageOpening<T>(IReadOnlyList<T> rows, Func<T, decimal> balance, decimal opening, int? skip, int? take)
    {
        var offset = ListPaging.Fit(ListPaging.Skip(skip), ListPaging.Take(take), rows.Count);
        return offset == 0 ? opening : balance(rows[offset - 1]);
    }

    /// <summary>
    /// Copied from ReceivableEndpoints.TryInstant: a plain date is the start of that company day —
    /// or, for the end of a window, the start of the next one, so the day itself is included.
    /// Anything else must parse as an instant.
    /// </summary>
    private static bool TryInstant(string? raw, TimeZoneInfo zone, bool endOfDay, out DateTime? at)
    {
        at = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        if (CompanyDays.TryParseDay(raw, out var day))
        {
            at = CompanyDays.StartUtc(endOfDay ? day.AddDays(1) : day, zone);
            return true;
        }
        if (!DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var instant))
            return false;
        at = instant;
        return true;
    }

    /// <summary>Copied from ReceivableEndpoints.Window: the current month by default, at most LedgerReports.MaxPeriodDays.</summary>
    private static (DateTime From, DateTime To, IResult? Error) Window(DateTime? from, DateTime? to)
    {
        var start = from ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = to ?? start.AddMonths(1);
        if (end <= start) return (start, end, Results.BadRequest("'to' must be after 'from'."));
        if ((end - start).TotalDays > LedgerReports.MaxPeriodDays)
            return (start, end, Results.BadRequest($"Date range too wide — max {LedgerReports.MaxPeriodDays} days per request."));
        return (start, end, null);
    }

    // ---------- mapping, copied from the list routes' files (private there) ----------

    /// <summary>Copied from ReceivableEndpoints.MapAsync(invoices): the invoice DTO the list route sends.</summary>
    private static async Task<List<InvoiceDto>> MapInvoicesAsync(
        IReadOnlyList<Invoice> invoices, ReceivableDocuments receivables, PosDbContext db, CancellationToken ct)
    {
        var standings = await receivables.StandingsAsync(invoices.Select(i => i.Id).ToList(), ct);
        var sourceNumbers = await receivables.SourceNumbersAsync(invoices, ct);

        var customerIds = invoices.Select(i => i.CustomerId).Distinct().ToList();
        var names = await db.Customers.AsNoTracking()
            .Where(c => customerIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.FullName, ct);

        var lineIds = invoices.SelectMany(i => i.Lines).Select(l => l.Id).ToList();
        var creditedRows = await db.CreditNoteLines.AsNoTracking()
            .Where(l => lineIds.Contains(l.InvoiceLineId))
            .Select(l => new { l.InvoiceLineId, l.Qty, l.StockReturnedQty })
            .ToListAsync(ct);
        var credited = creditedRows
            .GroupBy(x => x.InvoiceLineId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Qty));
        var backInStock = creditedRows
            .GroupBy(x => x.InvoiceLineId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.StockReturnedQty));

        var now = DateTime.UtcNow;
        return invoices.Select(i =>
        {
            var standing = standings[i.Id];
            return new InvoiceDto(
                i.Id, i.Number, i.CustomerId, names.GetValueOrDefault(i.CustomerId), i.BranchId,
                i.IssuedAtUtc, i.DueDateUtc, i.TaxMode.ToString(), i.Origin.ToString(),
                i.SourceType, i.SourceId, i.Notes,
                i.SubTotalHt, i.TaxTotal, i.GrandTotalTtc,
                standing.Prepaid, standing.Credited, standing.Received, standing.Claimed,
                standing.Outstanding, standing.Status,
                standing.Outstanding > 0 ? DebtAgeing.DaysOverdue(i.DueDateUtc, now) : 0,
                (standing.Outstanding > 0 ? DebtAgeing.BucketFor(i.DueDateUtc, now) : AgeingBucket.Current).ToString(),
                i.Lines.OrderBy(l => l.Position).Select(l => new InvoiceLineDto(
                    l.Id, l.ProductId, l.Sku, l.NameSnapshot, l.Qty, l.UnitPrice,
                    l.TaxRate, l.LineDiscount, l.LineNet, l.IncomeAccountCode,
                    credited.GetValueOrDefault(l.Id), l.Days,
                    l.StockUnitCost is null ? 0m : l.Qty, backInStock.GetValueOrDefault(l.Id))).ToList(),
                sourceNumbers.GetValueOrDefault(i.Id),
                i.BuyerName, i.BuyerAddress, i.BuyerTaxId,
                i.BuyerFiscalIds, i.PaymentMethodKey);
        }).ToList();
    }

    /// <summary>Copied from ReceivableEndpoints.MapAsync(receipts): the receipt DTO the list route sends.</summary>
    private static async Task<List<ReceiptVoucherDto>> MapReceiptsAsync(
        IReadOnlyList<ReceiptVoucher> vouchers, PosDbContext db, CancellationToken ct)
    {
        var customerIds = vouchers.Select(v => v.CustomerId).Distinct().ToList();
        var names = await db.Customers.AsNoTracking()
            .Where(c => customerIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.FullName, ct);

        var invoiceIds = vouchers.SelectMany(v => v.Allocations).Select(a => a.InvoiceId).Distinct().ToList();
        var numbers = await db.Invoices.AsNoTracking()
            .Where(i => invoiceIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => i.Number, ct);
        var contractIds = vouchers.Where(v => v.ContractId != null).Select(v => v.ContractId!.Value).Distinct().ToList();
        var contractNumbers = await db.RentalContracts.AsNoTracking()
            .Where(x => contractIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Number, ct);
        var noteIds = vouchers.Where(v => v.ReturnNoteId != null).Select(v => v.ReturnNoteId!.Value).Distinct().ToList();
        var noteNumbers = await db.ReturnNotes.AsNoTracking()
            .Where(n => noteIds.Contains(n.Id)).ToDictionaryAsync(n => n.Id, n => n.Number, ct);

        var movedIds = vouchers.Where(v => v.Kind is ReceiptKind.Advance or ReceiptKind.Payment).Select(v => v.Id).ToList();
        var applications = movedIds.Count == 0 ? [] : await db.CustomerAdvanceApplications.AsNoTracking()
            .Where(a => movedIds.Contains(a.ReceiptVoucherId)).ToListAsync(ct);
        var appliedInvoiceIds = applications.Where(a => a.InvoiceId != null).Select(a => a.InvoiceId!.Value).Distinct().ToList();
        var appliedNumbers = appliedInvoiceIds.Count == 0 ? [] : await db.Invoices.AsNoTracking()
            .Where(i => appliedInvoiceIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Number, ct);
        var jobIds = vouchers.Where(v => v.MaintenanceJobId != null).Select(v => v.MaintenanceJobId!.Value).Distinct().ToList();
        var jobNumbers = jobIds.Count == 0 ? [] : await db.MaintenanceJobs.AsNoTracking()
            .Where(j => jobIds.Contains(j.Id)).ToDictionaryAsync(j => j.Id, j => j.Number, ct);

        return vouchers.Select(v =>
        {
            // Summed here, not in the database: SQLite stores decimal as TEXT.
            var allocated = v.Allocations.Sum(a => a.Amount);
            var theirs = applications.Where(a => a.ReceiptVoucherId == v.Id).OrderBy(a => a.AppliedAtUtc).ToList();
            var advanceOpen = v.Kind == ReceiptKind.Advance && v.Status == PaymentStatus.Confirmed
                ? v.Amount - theirs.Sum(a => a.Amount) : 0m;
            var kept = theirs.Where(a => a.Kind == AdvanceMoveKind.Kept).Sum(a => a.Amount);
            return new ReceiptVoucherDto(
                v.Id, v.Number, v.CustomerId, names.GetValueOrDefault(v.CustomerId), v.BranchId,
                v.ReceivedAtUtc, v.MethodKey, v.Behavior.ToString(), v.Status.ToString(),
                v.Amount, allocated, v.Amount - allocated - kept,
                v.ConfirmedAtUtc, v.StatusNote, v.Reference,
                v.InstrumentNumber, v.InstrumentBank, v.InstrumentDueDate, v.Notes,
                v.Allocations
                    .Select(a => new ReceiptAllocationDto(a.InvoiceId, numbers.GetValueOrDefault(a.InvoiceId), a.Amount))
                    .ToList(),
                v.Purpose,
                v.Kind.ToString(),
                v.ContractId, v.ContractId is Guid cid ? contractNumbers.GetValueOrDefault(cid) : null,
                v.ReturnNoteId, v.ReturnNoteId is Guid nid ? noteNumbers.GetValueOrDefault(nid) : null,
                v.AdvanceTaxRate, v.AdvanceTaxAmount, advanceOpen,
                v.MaintenanceJobId, v.MaintenanceJobId is Guid jid ? jobNumbers.GetValueOrDefault(jid) : null,
                theirs.Select(a => new AdvanceApplicationDto(
                    a.Id, a.InvoiceId, a.InvoiceId is Guid iid ? appliedNumbers.GetValueOrDefault(iid) : null,
                    a.Amount, a.TaxAmount, a.AppliedAtUtc, a.CreatedBy,
                    a.Kind.ToString(), a.Reason, a.IncomeAccountCode)).ToList(),
                kept);
        }).ToList();
    }

    /// <summary>Copied from ReceivableEndpoints.Map(CreditNote): the credit-note DTO the list route sends.</summary>
    private static CreditNoteDto MapCreditNote(CreditNote note, string? invoiceNumber) =>
        new(note.Id, note.Number, note.InvoiceId, invoiceNumber, note.CustomerId, note.BranchId,
            note.IssuedAtUtc, note.Reason, note.SubTotalHt, note.TaxTotal, note.GrandTotalTtc,
            note.Lines.OrderBy(l => l.Position).Select(l => new CreditNoteLineDto(
                l.Id, l.InvoiceLineId, l.Sku, l.NameSnapshot, l.Qty, l.UnitPrice,
                l.TaxRate, l.LineNet, l.IncomeAccountCode, l.Days, l.StockReturnedQty)).ToList(),
            note.SourceType, note.SourceId);

    /// <summary>Copied from PaymentVoucherEndpoints.MapAsync: the voucher DTO the list route sends.</summary>
    private static async Task<List<PaymentVoucherDto>> MapVouchersAsync(
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

    // ---------- rows and summaries ----------

    private sealed record InvoiceHead(
        Guid Id, string Number, Guid CustomerId, string? Notes, DateTime IssuedAtUtc, DateTime DueDateUtc,
        InvoiceOrigin Origin, TaxMode TaxMode);

    private sealed record InvoiceRow(
        Guid Id, string Number, string? CustomerName, string? Notes, DateTime IssuedAtUtc,
        string Origin, string TaxMode, decimal Outstanding, decimal Claimed, string Status, int DaysOverdue);
}

/// <summary>The invoices list's tiles over every invoice the filters keep, and the origins and tax modes the window holds.</summary>
public sealed record InvoicePageSummary(
    int OpenCount, decimal Outstanding, decimal Overdue, decimal Claimed,
    IReadOnlyList<string> Origins, IReadOnlyList<string> TaxModes);

/// <summary>The receipts list's tiles and method choices, over the receipts of the state, customer and branch asked for.</summary>
public sealed record ReceiptPageSummary(
    decimal PendingTotal, decimal DueTotal, decimal UnallocatedTotal, IReadOnlyList<string> MethodKeys, bool HasPending);

/// <summary>The credit notes' totals line, over every note of the customer.</summary>
public sealed record CreditNotePageSummary(decimal Ht, decimal Tax, decimal Ttc);

/// <summary>A customer statement without its rows; <paramref name="PageOpening"/> is the balance before the page's first row.</summary>
public sealed record CustomerStatementPageSummary(
    Guid CustomerId, string CustomerCode, string CustomerName, DateTime From, DateTime To,
    decimal OpeningBalance, decimal ClosingBalance, decimal TotalDebit, decimal TotalCredit,
    decimal PendingNotes, decimal TotalOwed, decimal RecordedBalance, decimal BalanceDifference,
    decimal CreditLimit, decimal InvoicedOutstanding, decimal UninvoicedBalance, string Filter,
    decimal PageOpening);

/// <summary>A supplier statement without its rows; <paramref name="PageOpening"/> is the balance before the page's first row.</summary>
public sealed record SupplierStatementPageSummary(
    Guid SupplierId, string SupplierCode, string SupplierName, DateTime From, DateTime To,
    decimal OpeningBalance, decimal ClosingBalance, decimal TotalDebit, decimal TotalCredit,
    decimal RecordedBalance, decimal BalanceDifference, decimal PageOpening);

/// <summary>The whole book's ageing figures (the tiles and the totals line) and which customers the board holds.</summary>
public sealed record AgeingPageSummary(
    DateTime AsOf, decimal Total, decimal Current, decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90,
    decimal CreditBalance, decimal LedgerBalance, decimal Uninvoiced,
    int CustomerCount, IReadOnlyList<Guid> ListedCustomerIds);

/// <summary>A quantity statement without its two tables, and what the tables' columns depend on.</summary>
public sealed record QuantityStatementPageSummary(
    Guid CustomerId, string CustomerCode, string CustomerName, DateOnly From, DateOnly To, bool Current, DateOnly? FirstIssueOn,
    QuantityStatementSummaryDto Summary, IReadOnlyList<QuantityStatementWarningDto> Warnings,
    bool HasTransfers, bool HasContractTransfers, int ItemCount, int ContractCount);

/// <summary>The payment vouchers' two tiles (the branch's pending and approved) and the kinds and methods the filters offer.</summary>
public sealed record PaymentVoucherPageSummary(
    decimal PendingTotal, decimal ApprovedTotal, IReadOnlyList<PaymentVoucherKind> Kinds, IReadOnlyList<VoucherMethodChoice> Methods);

/// <summary>A payment method a voucher was paid by, with the name the list route sends beside it.</summary>
public sealed record VoucherMethodChoice(string Key, string Name);
