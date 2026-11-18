using System.Security.Claims;
using ParadiERP.API.Subscriptions;
using ParadiERP.Application.Contracts;
using ParadiERP.Application.Receivables;
using ParadiERP.Application.Regional;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Auth;
using ParadiERP.Infrastructure.Authorization;
using ParadiERP.Infrastructure.Ledger;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Receivables;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.API.Endpoints;

/// <summary>
/// The debtor book: invoices, what came in against them, what was given back, and the two
/// reports the owner's cash flow actually runs on.
///
/// <para>
/// There is deliberately <b>no route that edits an invoice and none that deletes one</b>. The
/// customer is holding a numbered document; changing it behind their back is not a correction,
/// it is a different figure with the same number on it. The correction is a credit note, which
/// leaves both visible — the same rule the ledger has followed since it existed.
/// </para>
/// <para>
/// Back office only, like renting and treasury. A till takes money at a counter; deciding that
/// a contractor owes 4 million dinars by the end of the month is not a counter action.
/// </para>
/// </summary>
public static class ReceivableEndpoints
{
    public static void MapReceivableEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/receivables").WithTags("Receivables")
            .AddEndpointFilter<SubscriptionWriteFilter>()
            .AtCallersBranch();
        // Taking money in, and reading the invoices it is taken against: whoever holds the cash
        // box (answer 25). Raising an invoice, a credit note and the reports stay the manager's.
        var cash = app.MapGroup("/api/receivables").WithTags("Receivables")
            .AddEndpointFilter<SubscriptionWriteFilter>()
            .AtCallersBranch();

        MapInvoiceReads(cash);
        MapContractChoices(cash);
        MapInvoices(group);
        MapReceipts(cash);
        MapSettings(cash);
        MapReports(group);
        SalesPageRoutes.MapReceivableCashPages(cash); SalesPageRoutes.MapReceivableReportPages(group);
    }

    // ---------- invoices ----------

    /// <summary>
    /// The contracts a voucher may name (answers 19, 20, 26): one customer's, or the newest when no
    /// customer is given (an expense for a contract), each with its return notes for a repair or a
    /// loss. Read-only and number-level, so a cash-box cashier can fill the form without the
    /// rentals screens, which stay the manager's.
    /// </summary>
    private static void MapContractChoices(RouteGroupBuilder group)
    {
        group.MapGet("/contracts", async (Guid? customerId, int? take, Guid? branchId, PosDbContext db, CancellationToken ct) =>
        {
            // A cancelled contract takes no receipt or voucher; it is not offered (owner, 2026-10-06).
            var query = db.RentalContracts.AsNoTracking().Where(x => x.Status != RentalContractStatus.Cancelled);
            // branchId is the caller's own branch for everyone but the Admin (CallersBranch): this
            // list handed every branch's contract ids to a branch manager (security review, 2026-10-10).
            if (branchId is Guid b) query = query.Where(x => x.BranchId == b);
            if (customerId is Guid c) query = query.Where(x => x.CustomerId == c);
            var contracts = await query.OrderByDescending(x => x.SignedAtUtc)
                .Take(Math.Clamp(take ?? 200, 1, 500))
                .Select(x => new { x.Id, x.Number, x.CustomerId, x.Status })
                .ToListAsync(ct);
            var ids = contracts.Select(x => x.Id).ToList();
            var customerIds = contracts.Select(x => x.CustomerId).Distinct().ToList();
            var names = await db.Customers.AsNoTracking().Where(x => customerIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.FullName, ct);
            var notes = await db.ReturnNotes.AsNoTracking().Where(n => ids.Contains(n.ContractId))
                .Select(n => new { n.Id, n.Number, n.ContractId, n.ReturnedAtUtc }).ToListAsync(ct);
            return Results.Ok(contracts.Select(x => new VoucherContractChoiceDto(
                x.Id, x.Number, x.CustomerId, names.GetValueOrDefault(x.CustomerId), x.Status.ToString(),
                notes.Where(n => n.ContractId == x.Id).OrderByDescending(n => n.ReturnedAtUtc)
                    .Select(n => new VoucherReturnNoteChoiceDto(n.Id, n.Number, n.ReturnedAtUtc)).ToList())).ToList());
        });
    }

    private static void MapInvoiceReads(RouteGroupBuilder group)
    {
        group.MapGet("/invoices", async (
            Guid? customerId, string? status, bool? overdue, DateTime? from, DateTime? to, int? take, Guid? branchId,
            ReceivableDocuments receivables, PosDbContext db, CancellationToken ct) =>
        {
            var query = db.Invoices.AsNoTracking();
            // Set to the caller's own branch for everyone but the Admin (CallersBranch).
            if (branchId is Guid branch) query = query.Where(i => i.BranchId == branch);
            if (customerId is Guid id) query = query.Where(i => i.CustomerId == id);
            if (from is not null) query = query.Where(i => i.IssuedAtUtc >= from);
            if (to is not null) query = query.Where(i => i.IssuedAtUtc < to);

            var invoices = await query
                .Include(i => i.Lines)
                .OrderByDescending(i => i.IssuedAtUtc)
                .Take(Math.Clamp(take ?? 200, 1, 1000))
                .ToListAsync(ct);

            var dtos = await MapAsync(invoices, receivables, db, ct);

            // Filtered here rather than in SQL: "settled" is an arithmetic fact about payments
            // and credit notes, not a column, and the moment it became a column it would be a
            // column that disagrees with them.
            if (!string.IsNullOrWhiteSpace(status))
                dtos = dtos.Where(i => string.Equals(i.Status, status, StringComparison.OrdinalIgnoreCase)).ToList();
            if (overdue == true)
                dtos = dtos.Where(i => i.Outstanding > 0 && i.DaysOverdue > 0).ToList();

            return Results.Ok(dtos);
        });

        group.MapGet("/invoices/{id:guid}", async (
            Guid id, ReceivableDocuments receivables, PosDbContext db, CancellationToken ct) =>
        {
            var invoice = await receivables.ReadInvoiceAsync(id, ct);
            if (invoice is null) return Results.NotFound();
            return Results.Ok((await MapAsync([invoice], receivables, db, ct)).Single());
        }).ForBranchOf(DocumentBranches.Invoice);
    }

    private static void MapInvoices(RouteGroupBuilder group)
    {
        group.MapPost("/invoices", async (
            CreateInvoiceRequest r, ReceivableDocuments receivables, PosDbContext db, HttpContext http,
            CancellationToken ct) =>
        {
            // An invoice is issued from a sale or a rental movement; typing its lines is closed (owner,
            // 2026-10-10). Direct invoices already on file stay readable, printable and creditable.
            if (r.SaleId is null && r.IssueNoteId is null && r.ReturnNoteId is null)
                return Results.Problem("An invoice is issued from a sale or a rental movement; typing its lines is closed.",
                    statusCode: 400, extensions: new Dictionary<string, object?> { ["code"] = "invoice_needs_source" });
            // With or without VAT was chosen when the sale or the contract was made, as the customer asked (owner,
            // 2026-11-05); the invoice follows it and needs no permission of its own.
            var result = await receivables.CreateInvoiceAsync(r, UserName(http), ct,
                sameBranchOnly: !BranchAssignment.IsCompanyScoped(http.User));
            if (!result.Ok) return Problem(result);

            var dto = (await MapAsync([result.Value!], receivables, db, ct)).Single();
            // The warning travels beside the document, never instead of it: a credit limit is a
            // warning in this system and nothing here may refuse a sale (CLAUDE.md, rule one).
            // The envelope is the same shape whether there is one or not, so no client has to
            // discover that the response changes shape on the day a contractor goes over.
            return Results.Created($"/api/receivables/invoices/{dto.Id}",
                new { invoice = dto, warning = result.Warning });
        });

        group.MapPost("/invoices/{id:guid}/credit-notes", async (
            Guid id, CreateCreditNoteRequest r, ReceivableDocuments receivables, PosDbContext db,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await receivables.CreateCreditNoteAsync(id, r, UserName(http), ct);
            if (!result.Ok) return Problem(result);

            var invoice = await db.Invoices.AsNoTracking().FirstAsync(i => i.Id == id, ct);
            return Results.Created($"/api/receivables/credit-notes/{result.Value!.Id}",
                Map(result.Value, invoice.Number));
        }).ForBranchOf(DocumentBranches.Invoice);

        group.MapGet("/credit-notes", async (
            Guid? customerId, Guid? invoiceId, int? take, Guid? branchId, PosDbContext db, CancellationToken ct) =>
        {
            IQueryable<CreditNote> query = db.CreditNotes.AsNoTracking().Include(n => n.Lines);
            if (branchId is Guid branch) query = query.Where(n => n.BranchId == branch);
            if (customerId is Guid cid) query = query.Where(n => n.CustomerId == cid);
            if (invoiceId is Guid iid) query = query.Where(n => n.InvoiceId == iid);

            var notes = await query
                .OrderByDescending(n => n.IssuedAtUtc)
                .Take(Math.Clamp(take ?? 200, 1, 1000))
                .ToListAsync(ct);

            var invoiceIds = notes.Select(n => n.InvoiceId).Distinct().ToList();
            var numbers = await db.Invoices.AsNoTracking()
                .Where(i => invoiceIds.Contains(i.Id))
                .ToDictionaryAsync(i => i.Id, i => i.Number, ct);

            return Results.Ok(notes.Select(n => Map(n, numbers.GetValueOrDefault(n.InvoiceId))));
        });

        // The Location the POST above has always returned, now answering: the printed credit
        // note reads one document by id rather than filtering a list for it.
        group.MapGet("/credit-notes/{id:guid}", async (
            Guid id, ReceivableDocuments receivables, PosDbContext db, CancellationToken ct) =>
        {
            var note = await receivables.GetCreditNoteAsync(id, ct);
            if (note is null) return Results.NotFound();
            var invoiceNumber = await db.Invoices.AsNoTracking()
                .Where(i => i.Id == note.InvoiceId).Select(i => i.Number).FirstOrDefaultAsync(ct);
            return Results.Ok(Map(note, invoiceNumber));
        }).ForBranchOf(DocumentBranches.CreditNote);
    }

    // ---------- receipts ----------

    private static void MapReceipts(RouteGroupBuilder group)
    {
        group.MapGet("/receipts", async (
            Guid? customerId, string? status, int? take, Guid? branchId, PosDbContext db, CancellationToken ct) =>
        {
            var query = db.ReceiptVouchers.AsNoTracking().Include(v => v.Allocations).AsQueryable();
            if (branchId is Guid branch) query = query.Where(v => v.BranchId == branch);
            if (customerId is Guid id) query = query.Where(v => v.CustomerId == id);
            if (Enum.TryParse<PaymentStatus>(status, true, out var wanted)) query = query.Where(v => v.Status == wanted);

            var vouchers = await query
                // Oldest due first, because the screen this feeds is the one that stops a cheque
                // being forgotten.
                .OrderBy(v => v.InstrumentDueDate ?? v.ReceivedAtUtc)
                .Take(Math.Clamp(take ?? 200, 1, 1000))
                .ToListAsync(ct);

            return Results.Ok(await MapAsync(vouchers, db, ct));
        });

        group.MapGet("/receipts/{id:guid}", async (
            Guid id, ReceivableDocuments receivables, PosDbContext db, CancellationToken ct) =>
        {
            var voucher = await receivables.ReadReceiptAsync(id, ct);
            if (voucher is null) return Results.NotFound();
            return Results.Ok((await MapAsync([voucher], db, ct)).Single());
        }).ForBranchOf(DocumentBranches.Receipt);

        // The "in exchange for" line a receipt against these invoices gets when the clerk leaves
        // it blank (answer 23) — asked by the form so it can show the line prefilled and let the
        // clerk write over it. `invoiceIds` is a comma-separated list. branchId is the caller's
        // own for everyone but the Admin (CallersBranch): another branch's invoice adds nothing to
        // the line, as one that does not exist adds nothing (P0.1b).
        group.MapGet("/receipts/purpose", async (
            string? invoiceIds, Guid? branchId, ReceivableDocuments receivables, CancellationToken ct) =>
        {
            var ids = (invoiceIds ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => Guid.TryParse(x, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .Take(200)
                .ToList();
            return Results.Ok(new ReceiptPurposeDto(await receivables.DefaultPurposeAsync(ids, ct, branchId)));
        });

        group.MapPost("/receipts", async (
            CreateReceiptRequest r, ReceivableDocuments receivables, PosDbContext db, HttpContext http,
            CancellationToken ct) =>
        {
            var result = await receivables.CreateReceiptAsync(r, UserName(http), ct,
                sameBranchOnly: !BranchAssignment.IsCompanyScoped(http.User));
            if (!result.Ok) return Problem(result);

            var dto = (await MapAsync([result.Value!], db, ct)).Single();
            return Results.Created($"/api/receivables/receipts/{dto.Id}", dto);
        });

        // A cheque cleared, or it did not. This is the act that settles an invoice — never the
        // act of handing the paper over (§5.4).
        group.MapPost("/receipts/{id:guid}/settle", async (
            Guid id, SettleReceiptRequest r, ReceivableDocuments receivables, PosDbContext db,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await receivables.SettleReceiptAsync(id, r.Status, r.Note, UserName(http), ct,
                ParadiERP.Infrastructure.Authorization.ServerAuthorization.Has(http.User, PermissionCatalog.ReceiptsSettleOwn));
            if (!result.Ok) return Problem(result);
            return Results.Ok((await MapAsync([result.Value!], db, ct)).Single());
        }).ForBranchOf(DocumentBranches.Receipt);

        // M7b: part of an advance held on 419 applied to one of the customer's invoices, or released to their account
        // (no invoice). Moves no cash: Dr 419 / Dr 4457 (its VAT, when due on receipt) / Cr 411. The advance's branch is
        // the caller's (ForBranchOf); a branch-bound caller's invoice must be that branch's too.
        group.MapPost("/receipts/{id:guid}/apply", async (
            Guid id, ApplyAdvanceRequest r, [FromServices] ReceivableDocuments receivables, [FromServices] PosDbContext db,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await receivables.ApplyAdvanceAsync(id, r, UserName(http), ct,
                sameBranchOnly: !BranchAssignment.IsCompanyScoped(http.User));
            if (!result.Ok) return Problem(result);
            var voucher = await receivables.ReadReceiptAsync(id, ct);
            return Results.Ok((await MapAsync([voucher!], db, ct)).Single());
        }).ForBranchOf(DocumentBranches.Receipt);

        // SAL-13: part of an advance (on 419) or of a receipt's unallocated credit (on 411) kept on a cancelled order, moved
        // to the income account the accountant chose. Refused while the setting says Not allowed (the default). Under the
        // key that approves refund payment vouchers — the stricter of it and the key that applies advances: keeping the money
        // is the other side of refunding it. The receipt's branch is the caller's (ForBranchOf).
        group.MapPost("/receipts/{id:guid}/keep", async (
            Guid id, KeepAdvanceRequest r, [FromServices] ReceivableDocuments receivables, [FromServices] PosDbContext db,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await receivables.KeepAdvanceAsync(id, r, UserName(http), ct);
            if (!result.Ok) return Problem(result);
            var voucher = await receivables.ReadReceiptAsync(id, ct);
            return Results.Ok((await MapAsync([voucher!], db, ct)).Single());
        }).ForBranchOf(DocumentBranches.Receipt);
    }

    // ---------- the debtor book's accounting choices (M7b) ----------

    /// <summary>
    /// Where a customer advance is held (411, the default, or 419 until the invoice) and when its VAT is due (at the
    /// invoice, the default, or when received). Read by the receipt form and the settings page; changed under
    /// accounting.manage. Company-wide: no branch rule applies.
    /// </summary>
    private static void MapSettings(RouteGroupBuilder group)
    {
        group.MapGet("/settings", async ([FromServices] ReceivableSettingsStore store, CancellationToken ct) =>
            Results.Ok(await store.SettingsAsync(ct)));

        group.MapPut("/settings", async (
            UpdateReceivableSettingsRequest r, [FromServices] ReceivableSettingsStore store, HttpContext http, CancellationToken ct) =>
        {
            var result = await store.UpdateAsync(r, UserName(http), ct);
            return result.Saved is not null
                ? Results.Ok(result.Saved)
                : Results.Problem(result.Error, statusCode: result.Status,
                    extensions: new Dictionary<string, object?> { ["code"] = result.Code });
        });
    }

    // ---------- the two reports the cash flow runs on ----------

    private static void MapReports(RouteGroupBuilder group)
    {
        group.MapGet("/statement", async (
            Guid customerId, string? from, string? to, string? kind, HttpContext http,
            IRegionalSettingsProvider regional,
            CustomerStatements statements, CancellationToken ct) =>
        {
            // Plain dates are the company's days, `to` included (2026-10-12): the screen no longer
            // sends its own browser's midnight. A full instant is still read as one, for callers
            // that send them (the provider smoke test).
            var zone = CompanyDays.Zone(regional.Get().TimeZone);
            if (!TryInstant(from, zone, endOfDay: false, out var fromAt) || !TryInstant(to, zone, endOfDay: true, out var toAt))
                return Results.BadRequest("from and to are dates (yyyy-MM-dd) or instants.");
            var (fromUtc, toUtc, error) = Window(fromAt, toAt);
            if (error is not null) return error;

            // The whole statement is anyone's who reads accounts; narrowing it to sales or rentals
            // is the admin's (owner, 2026-10-10), decided here on every request.
            if (StatementFilters.Parse(kind) is not StatementFilter filter)
                return Results.BadRequest("kind is all, sales or rentals.");
            if (filter != StatementFilter.All
                && !ParadiERP.Infrastructure.Authorization.ServerAuthorization.Has(http.User, PermissionCatalog.CustomersAccountsFilter))
                return Results.Problem("Narrowing a statement to sales or rentals is not open to this account.",
                    statusCode: StatusCodes.Status403Forbidden);

            var statement = await statements.BuildAsync(customerId, fromUtc, toUtc, ct, filter);
            return statement is null ? Results.NotFound("Unknown customer.") : Results.Ok(statement);
        });

        group.MapGet("/ageing", async (
            DateTime? asOf, Guid? customerId, CustomerStatements statements, CancellationToken ct) =>
            Results.Ok(await statements.AgeAsync(asOf ?? DateTime.UtcNow, customerId, ct)));
    }

    /// <summary>
    /// A plain date is the start of that company day — or, for the end of a window, the start of
    /// the next one, so the day itself is included. Anything else must parse as an instant.
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

    /// <summary>
    /// A default window of the current month and a hard ceiling on how wide one may be — the
    /// same rule the ledger reports follow, for the same reason: an unbounded report is an
    /// unbounded query with a nicer name.
    /// </summary>
    private static (DateTime From, DateTime To, IResult? Error) Window(DateTime? from, DateTime? to)
    {
        var start = from ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = to ?? start.AddMonths(1);
        if (end <= start) return (start, end, Results.BadRequest("'to' must be after 'from'."));
        if ((end - start).TotalDays > LedgerReports.MaxPeriodDays)
            return (start, end, Results.BadRequest($"Date range too wide — max {LedgerReports.MaxPeriodDays} days per request."));
        return (start, end, null);
    }

    // ---------- mapping ----------

    private static async Task<List<InvoiceDto>> MapAsync(
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
        // P0.3: what credit notes have brought back into stock of each line that took goods out.
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
                // Nothing settled is nothing overdue, however old the paper is.
                standing.Outstanding > 0 ? DebtAgeing.DaysOverdue(i.DueDateUtc, now) : 0,
                (standing.Outstanding > 0 ? DebtAgeing.BucketFor(i.DueDateUtc, now) : AgeingBucket.Current).ToString(),
                // In the order entered; a line from before positions were kept sorts as it was read.
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

    private static async Task<List<ReceiptVoucherDto>> MapAsync(
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

        // M7b: an advance's applications, the invoices they paid and the job it was taken for; SAL-13: what of any advance or
        // payment receipt was kept as other income.
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

    private static CreditNoteDto Map(CreditNote note, string? invoiceNumber) =>
        new(note.Id, note.Number, note.InvoiceId, invoiceNumber, note.CustomerId, note.BranchId,
            note.IssuedAtUtc, note.Reason, note.SubTotalHt, note.TaxTotal, note.GrandTotalTtc,
            note.Lines.OrderBy(l => l.Position).Select(l => new CreditNoteLineDto(
                l.Id, l.InvoiceLineId, l.Sku, l.NameSnapshot, l.Qty, l.UnitPrice,
                l.TaxRate, l.LineNet, l.IncomeAccountCode, l.Days, l.StockReturnedQty)).ToList(),
            note.SourceType, note.SourceId);

    private static string? UserName(HttpContext http) =>
        http.User.FindFirstValue("name") ?? http.User.Identity?.Name;

    private static IResult Problem<T>(ReceivableResult<T> result) => result.Failure switch
    {
        ReceivableFailure.NotFound => Results.NotFound(result.Message),
        // A refusal with a code the screen words itself (SAL-13's keep: not allowed, account not mapped).
        ReceivableFailure.Conflict or ReceivableFailure.Invalid when result.Code is not null => Results.Problem(result.Message,
            statusCode: result.Failure == ReceivableFailure.Conflict ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?> { ["code"] = result.Code }),
        ReceivableFailure.Conflict => Results.Conflict(result.Message),
        ReceivableFailure.Forbidden => Results.Problem(result.Message, statusCode: StatusCodes.Status403Forbidden,
            extensions: new Dictionary<string, object?> { ["code"] = result.Code }),
        _ => Results.BadRequest(result.Message)
    };
}
