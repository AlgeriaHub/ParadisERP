using System.Security.Claims;
using ParadisERP.API.Subscriptions;
using ParadisERP.Application.Contracts;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Auth;
using ParadisERP.Infrastructure.Catalog;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.API.Endpoints;

/// <summary>
/// The chart of accounts, the journal and the accounting reports.
///
/// <para>
/// There is deliberately <b>no route that writes a journal entry</b>. This is an operational
/// system that posts, not an accounting package: entries are raised by the documents that
/// cause them — a sale, a receiving note, a remittance — and every one carries
/// <c>sourceType</c>/<c>sourceId</c> back to that document. A hand-written entry would be a
/// figure with nothing behind it, and the first time one appeared the reports would stop being
/// traceable, which is the property they exist for.
/// </para>
/// <para>
/// The one correction there is, is a reversal: the original stays, the mirror image goes
/// beside it, and both are visible. Nothing is deleted, exactly as a <see cref="Sale"/> is
/// append-only.
/// </para>
/// </summary>
public static class LedgerEndpoints
{
    public static void MapLedgerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ledger").WithTags("Ledger")
            .AddEndpointFilter<SubscriptionWriteFilter>()
            .AtCallersBranch();

        MapAccounts(group);
        MapJournal(group);
        MapReports(group);
        MapClosing(group);
        FinancePageRoutes.MapTrialBalancePage(group); FinancePageRoutes.MapAccountStatementPage(group); FinancePageRoutes.MapJournalPage(group); FinancePageRoutes.MapBookClosingPage(group);
    }

    // ---------- closing the books (CLO-01) ----------

    private static void MapClosing(RouteGroupBuilder group)
    {
        group.MapGet("/closing", async (PosDbContext db, CancellationToken ct) =>
        {
            var current = await BookClosings.CurrentAsync(db, ct);
            var history = await BookClosings.HistoryAsync(db, 200, ct);
            return Results.Ok(new BookClosingStateDto(current?.ClosedThrough, history.Select(MapClosing).ToList()));
        });

        // The Admin's (owner, 2026-11-01): accounting.close, held by the Admin role alone until granted.
        group.MapPost("/closing/close", async (CloseBooksRequest r, PosDbContext db, IRegionalSettingsProvider regional,
            ClaimsPrincipal caller, CancellationToken ct) =>
        {
            var who = MovementAuthor.Of(caller);
            var result = await BookClosings.CloseAsync(db, r.Through, r.Note, who.UserId, who.UserName,
                StoreDocumentHistory.Zone(regional), DateTime.UtcNow, ct);
            return result.Ok ? Results.Ok(MapClosing(result.Closing!)) : Refused(result.Problem!);
        });

        group.MapPost("/closing/reopen", async (ReopenBooksRequest r, PosDbContext db, IRegionalSettingsProvider regional,
            ClaimsPrincipal caller, CancellationToken ct) =>
        {
            var who = MovementAuthor.Of(caller);
            var result = await BookClosings.ReopenAsync(db, r.Through, r.Reason, who.UserId, who.UserName,
                StoreDocumentHistory.Zone(regional), DateTime.UtcNow, ct);
            return result.Ok ? Results.Ok(MapClosing(result.Closing!)) : Refused(result.Problem!);
        });
    }

    private static IResult Refused(string message) =>
        Results.Problem(message, statusCode: StatusCodes.Status400BadRequest, extensions: new Dictionary<string, object?> { ["code"] = "closingRefused" });

    private static BookClosingDto MapClosing(BookClosing c) => new(c.Id, c.ClosedThrough, c.IsReopen, c.Reason, c.UserName, c.AtUtc);

    // ---------- chart of accounts ----------

    private static void MapAccounts(RouteGroupBuilder group)
    {
        group.MapGet("/accounts", async (bool? includeInactive, PosDbContext db, CancellationToken ct) =>
        {
            var query = db.LedgerAccounts.AsNoTracking();
            if (includeInactive != true) query = query.Where(a => a.IsActive);
            var accounts = await query.ToListAsync(ct);
            return Results.Ok(accounts
                .OrderBy(a => a.SortOrder).ThenBy(a => a.Code, StringComparer.Ordinal)
                .Select(Map));
        });

        // The admin extends the seeded chart. An added account is never a system one: nothing
        // in code posts to it, so nothing breaks if it is later renamed or switched off.
        group.MapPost("/accounts", async (UpsertLedgerAccountRequest r, PosDbContext db, CancellationToken ct) =>
        {
            var code = (r.Code ?? "").Trim();
            if (code.Length is 0 or > 16) return Results.BadRequest("An account code of 1 to 16 characters is required.");
            if (!code.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.'))
                return Results.BadRequest("Account codes use letters, digits, '-' and '.'.");
            if (string.IsNullOrWhiteSpace(r.NameFr)) return Results.BadRequest("A French name is required.");
            if (!Enum.TryParse<LedgerAccountType>(r.Type, true, out var type))
                return Results.BadRequest($"Types are: {string.Join(", ", Enum.GetNames<LedgerAccountType>())}.");
            if (await db.LedgerAccounts.AnyAsync(a => a.Code == code, ct))
                return Results.Conflict($"Account '{code}' already exists.");

            var account = new LedgerAccount
            {
                Code = code,
                NameAr = (r.NameAr ?? "").Trim(),
                NameFr = r.NameFr.Trim(),
                NameEn = (r.NameEn ?? "").Trim(),
                Type = type,
                IsSystem = false,
                IsActive = r.IsActive,
                SortOrder = r.SortOrder
            };
            db.LedgerAccounts.Add(account);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/ledger/accounts/{account.Id}", Map(account));
        });

        group.MapPut("/accounts/{id:guid}", async (
            Guid id, UpsertLedgerAccountRequest r, PosDbContext db, CancellationToken ct) =>
        {
            var account = await db.LedgerAccounts.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (account is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(r.NameFr)) return Results.BadRequest("A French name is required.");
            if (!Enum.TryParse<LedgerAccountType>(r.Type, true, out var type))
                return Results.BadRequest($"Types are: {string.Join(", ", Enum.GetNames<LedgerAccountType>())}.");

            // A system account may be renamed — a firm's accountant may well call 530
            // something else — but never re-coded, re-typed or switched off. Code posts to it
            // by code, and moving it to another side of the balance sheet would silently make
            // every past figure wrong with nothing failing anywhere.
            if (account.IsSystem)
            {
                if (type != account.Type)
                    return Results.BadRequest($"Account {account.Code} is posted to by the system; its type cannot change.");
                if (!r.IsActive)
                    return Results.BadRequest($"Account {account.Code} is posted to by the system and cannot be deactivated.");
            }
            else if (type != account.Type &&
                     await db.LedgerEntryLines.AnyAsync(l => l.AccountId == id, ct))
            {
                return Results.BadRequest("This account already has entries; its type can no longer change.");
            }

            account.NameAr = (r.NameAr ?? "").Trim();
            account.NameFr = r.NameFr.Trim();
            account.NameEn = (r.NameEn ?? "").Trim();
            account.Type = type;
            account.IsActive = r.IsActive;
            account.SortOrder = r.SortOrder;
            await db.SaveChangesAsync(ct);
            return Results.Ok(Map(account));
        });

        // No delete. An account with history cannot go without taking the history with it, and
        // one without history costs nothing to leave switched off.
    }

    // ---------- the journal ----------

    private static void MapJournal(RouteGroupBuilder group)
    {
        group.MapGet("/entries", async (
            DateTime? from, DateTime? to, Guid? branchId, string? sourceType, Guid? sourceId, int? take,
            PosDbContext db, CancellationToken ct) =>
        {
            IQueryable<LedgerEntry> query = db.LedgerEntries.AsNoTracking().Include(e => e.Lines);
            if (from is not null) query = query.Where(e => e.EntryDate >= from);
            if (to is not null) query = query.Where(e => e.EntryDate < to);
            if (branchId is not null) query = query.Where(e => e.BranchId == branchId);
            if (!string.IsNullOrWhiteSpace(sourceType)) query = query.Where(e => e.SourceType == sourceType);
            if (sourceId is not null) query = query.Where(e => e.SourceId == sourceId);

            var entries = await query
                .OrderByDescending(e => e.EntryDate).ThenByDescending(e => e.CreatedAtUtc)
                .Take(Math.Clamp(take ?? 200, 1, 1000))
                .ToListAsync(ct);

            var accounts = await db.LedgerAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id, ct);
            var branches = await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.NameFr, ct);
            var originals = await OriginalDatesAsync(db, entries, ct);
            return Results.Ok(entries.Select(e => Map(e, accounts, branches, originals)));
        });

        // One entry by id (P0.1b, owner 2026-10-25). The branch it is booked to reads it whole, as its
        // journal does. A branch that only holds some of its lines — the other side of an
        // inter-branch entry, reached from its account statement's drill — reads the header and
        // its own lines, the rest folded into one line of totals: no account, branch or memo of
        // another branch. Anyone else is answered as for an entry that does not exist.
        group.MapGet("/entries/{id:guid}", async (Guid id, PosDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var entry = await db.LedgerEntries.AsNoTracking().Include(e => e.Lines)
                .FirstOrDefaultAsync(e => e.Id == id, ct);
            if (entry is null) return Results.NotFound();

            Guid? viewer = BranchAssignment.IsCompanyScoped(http.User)
                ? null
                : await BranchAssignment.ForCallerAsync(http.User, db, ct) ?? Guid.Empty;
            if (viewer is Guid own && entry.BranchId != own && entry.Lines.All(l => l.BranchId != own))
                return Results.NotFound();

            var accounts = await db.LedgerAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id, ct);
            var branches = await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.NameFr, ct);
            var dto = Map(entry, accounts, branches, await OriginalDatesAsync(db, [entry], ct));
            return Results.Ok(viewer is Guid mine && entry.BranchId != mine ? FoldOtherBranches(dto, mine) : dto);
        });

        // The only correction there is. The user picks an entry and says why; they never
        // compose the lines, so this is still a system that posts rather than one that is
        // typed into.
        group.MapPost("/entries/{id:guid}/reverse", async (
            Guid id, ReverseEntryRequest r, PosDbContext db, LedgerPoster ledger, HttpContext http,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(r.Reason))
                return Results.BadRequest("A reversal needs a reason; an unexplained correction is an unexplained figure.");

            var entry = await db.LedgerEntries.AsNoTracking().Include(e => e.Lines)
                .FirstOrDefaultAsync(e => e.Id == id, ct);
            if (entry is null) return Results.NotFound();
            if (entry.IsReversal) return Results.BadRequest("A reversal cannot itself be reversed; post the original document again.");
            if (await db.LedgerEntries.AnyAsync(e => e.ReversalOfEntryId == id, ct))
                return Results.BadRequest("This entry has already been reversed.");

            await using var tx = await db.BeginWriteScopeAsync(ct);
            var by = http.User.FindFirstValue("name") ?? http.User.Identity?.Name;
            var reversal = await ledger.ReverseAsync(entry, r.Reason.Trim(), by, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Results.Ok(new { reversalId = reversal.Id, reversedEntryId = entry.Id });
        }).ForBranchOf(DocumentBranches.LedgerEntry);
    }

    // ---------- reports ----------

    private static void MapReports(RouteGroupBuilder group)
    {
        group.MapGet("/reports/trial-balance", async (
            DateTime? from, DateTime? to, Guid? branchId, LedgerReports reports, CancellationToken ct) =>
        {
            var (fromUtc, toUtc, error) = Window(from, to);
            if (error is not null) return error;
            return Results.Ok(await reports.TrialBalanceAsync(fromUtc, toUtc, branchId, ct));
        });

        // Branch and period are dimensions everywhere. Omitting branchId is not "all branches
        // added up" — it is the group, with movements between the company's own branches
        // eliminated (design §5.10).
        group.MapGet("/reports/profit-and-loss", async (
            DateTime? from, DateTime? to, Guid? branchId, LedgerReports reports, CancellationToken ct) =>
        {
            var (fromUtc, toUtc, error) = Window(from, to);
            if (error is not null) return error;
            return Results.Ok(await reports.ProfitAndLossAsync(fromUtc, toUtc, branchId, ct));
        });

        group.MapGet("/reports/balance-sheet", async (
            DateTime? asOf, Guid? branchId, LedgerReports reports, CancellationToken ct) =>
            Results.Ok(await reports.BalanceSheetAsync(asOf ?? DateTime.UtcNow, branchId, ct)));

        group.MapGet("/reports/account-statement", async (
            Guid accountId, DateTime? from, DateTime? to, Guid? branchId, int? take,
            LedgerReports reports, CancellationToken ct) =>
        {
            var (fromUtc, toUtc, error) = Window(from, to);
            if (error is not null) return error;
            var statement = await reports.AccountStatementAsync(accountId, fromUtc, toUtc, branchId, take ?? 500, ct);
            return statement is null ? Results.NotFound("Unknown account.") : Results.Ok(statement);
        });
    }

    /// <summary>
    /// A default window of the current month and a hard ceiling on how wide one may be. Every
    /// other list route in this API caps what a single call loads; an unbounded report is the
    /// same problem with a nicer name.
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

    private static LedgerAccountDto Map(LedgerAccount a) => new(
        a.Id, a.Code, a.NameAr, a.NameFr, a.NameEn, a.Type.ToString(), a.IsSystem, a.IsActive, a.SortOrder);

    /// <summary>The date of each entry these reversals correct, by its id: one query for the whole page.</summary>
    private static async Task<Dictionary<Guid, DateTime>> OriginalDatesAsync(
        PosDbContext db, IReadOnlyCollection<LedgerEntry> entries, CancellationToken ct)
    {
        var ids = entries.Where(e => e.ReversalOfEntryId is not null).Select(e => e.ReversalOfEntryId!.Value).Distinct().ToList();
        return ids.Count == 0
            ? []
            : await db.LedgerEntries.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.EntryDate, ct);
    }

    private static LedgerEntryDto Map(
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

    /// <summary>
    /// The entry as a branch holding only some of its lines reads it: its own lines as they are, and
    /// every other branch's lines summed into one folded line — the debit and credit totals alone, so
    /// the entry still balances on the screen and says nothing else about the other side.
    /// </summary>
    internal static LedgerEntryDto FoldOtherBranches(LedgerEntryDto entry, Guid own)
    {
        var mine = entry.Lines.Where(l => l.BranchId == own).ToList();
        var others = entry.Lines.Where(l => l.BranchId != own).ToList();
        if (others.Count == 0) return entry;
        var folded = new LedgerEntryLineDto(
            Guid.Empty, Guid.Empty, "", "", "", Guid.Empty,
            others.Sum(l => l.Debit), others.Sum(l => l.Credit), null, Folded: true);
        return entry with { Lines = [.. mine, folded] };
    }
}
