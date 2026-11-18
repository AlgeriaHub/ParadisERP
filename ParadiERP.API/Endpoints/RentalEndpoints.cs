using System.Globalization;
using System.Security.Claims;
using ParadiERP.API.Subscriptions;
using ParadiERP.Application.Contracts;
using ParadiERP.Application.Regional;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Authorization;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Receivables;
using ParadiERP.Infrastructure.Rentals;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Engine = ParadiERP.Application.Rentals;

namespace ParadiERP.API.Endpoints;

/// <summary>
/// Renting: the contract and the chain of documents raised against it.
///
/// <para>
/// Deliberately back-office routes (§7 decision 5). A contract is a signed document with legal
/// weight, raising one is not a thirty-second checkout, and the balances it moves are
/// server-owned — so none of this is reachable from a till, and none of it replicates to one.
/// </para>
/// <para>
/// The handlers are thin on purpose. Everything a document means — what it costs, what stock it
/// moves, what it posts, whether it may be raised at all — is in
/// <see cref="RentalDocuments"/> and the engine beneath it, where it can be tested without a
/// web server.
/// </para>
/// </summary>
public static class RentalEndpoints
{
    public static void MapRentalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/rentals").WithTags("Rentals")
            .AddEndpointFilter<SubscriptionWriteFilter>()
            .AddEndpointFilter(HideCancelledAsync)
            .AtCallersBranch();

        MapSettings(group);
        MapContracts(group);
        MapNotes(group);
        MapQuantityStatement(group);
        SalesPageRoutes.MapQuantityStatementPages(group);
    }

    // ---------- settings ----------

    private static void MapSettings(RouteGroupBuilder group)
    {
        group.MapGet("/settings", async (RentalDocuments rentals, CancellationToken ct) =>
        {
            var settings = await rentals.SettingsAsync(ct);
            return Results.Ok(new RentalSettingsDto(
                settings.OverdueMultiplier, settings.DepositEnabled, await rentals.TaxRateAsync(ct), settings.DayCutoffHour,
                settings.MinimumDays));
        });

        group.MapPut("/settings", async (
            UpdateRentalSettingsRequest r, RentalDocuments rentals, CancellationToken ct) =>
        {
            var result = await rentals.UpdateSettingsAsync(r, ct);
            return result.Ok
                ? Results.Ok(new RentalSettingsDto(
                    result.Value!.RentalOverdueMultiplier, result.Value.RentalDepositEnabled, result.Value.RentalTaxRate,
                    result.Value.RentalDayCutoffHour, result.Value.RentalMinimumDays))
                : Problem(result);
        });

        // The overrides, readable (docs/07 §3.2). Until these two routes existed the figure could
        // be written and never read back, and the contract form previewed overdue with the
        // company multiplier for an item that had its own — the figure the clerk saw was not the
        // figure frozen on the line. Never a member of ProductDto: that is the sync payload.
        group.MapGet("/settings/products", async (RentalDocuments rentals, CancellationToken ct) =>
            Results.Ok(await rentals.ProductMultipliersAsync(ct)));
        InventoryPageRoutes.MapRentalItemFiguresPage(group);

        group.MapGet("/settings/products/{productId:guid}", async (
            Guid productId, RentalDocuments rentals, CancellationToken ct) =>
        {
            var row = await rentals.ProductMultiplierAsync(productId, ct);
            return row is null ? Results.NotFound() : Results.Ok(row);
        });

        // Its own route rather than a field on the product upsert: a client that knows nothing
        // about renting must not be able to clear an item's override simply by omitting it.
        group.MapPut("/settings/products/{productId:guid}", async (
            Guid productId, SetProductOverdueMultiplierRequest r, RentalDocuments rentals, CancellationToken ct) =>
        {
            var result = await rentals.SetProductMultiplierAsync(
                productId, r.OverdueMultiplier, r.RentalUnitPrice, r.RentalDamageUnitPrice, ct);
            if (!result.Ok) return Problem(result);
            var p = result.Value!;
            return Results.Ok(new ProductOverdueMultiplierDto(
                p.Id, p.Sku, p.NameAr, p.NameFr, p.NameEn, p.RentalOverdueMultiplier, p.RentalUnitPrice, p.RentalDamageUnitPrice));
        });
    }

    // ---------- contracts ----------

    private static void MapContracts(RouteGroupBuilder group)
    {
        // The row carries its own answer — what is out, and what it has cost — so the list no
        // longer settles each contract to find out (docs/07 §3.1). Three set queries over the
        // listed ids, whatever their number; the figures are what was charged, not a recompute.
        group.MapGet("/contracts", async (
            string? status, Guid? customerId, Guid? branchId, RentalDocuments rentals, PosDbContext db, HttpContext http,
            CancellationToken ct) =>
        {
            var query = db.RentalContracts.AsNoTracking();
            // A cancelled contract leaves every list but the one rentals.cancelled reads (owner,
            // 2026-10-06: "hide it for cashier and only appear for admin").
            if (!SeesCancelled(http.User)) query = query.Where(c => c.Status != RentalContractStatus.Cancelled);
            // Set to the caller's own branch for everyone but the Admin (CallersBranch).
            if (branchId is Guid branch) query = query.Where(c => c.BranchId == branch);
            if (Enum.TryParse<RentalContractStatus>(status, true, out var wanted))
                query = query.Where(c => c.Status == wanted);
            if (customerId is Guid id) query = query.Where(c => c.CustomerId == id);

            var contracts = await query.OrderByDescending(c => c.SignedAtUtc).Take(500).ToListAsync(ct);
            var customerIds = contracts.Select(c => c.CustomerId).Distinct().ToList();
            var names = await db.Customers.AsNoTracking()
                .Where(c => customerIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.FullName, ct);
            var totals = await rentals.TotalsAsync(contracts.Select(c => c.Id).ToList(), ct);
            var settings = await rentals.SettingsAsync(ct);
            var depositVisible = Engine.RentalDeposit.IsVisible(settings);

            IReadOnlyList<RentalContractRowDto> rows = contracts.Select(c =>
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
            return Results.Ok(rows);
        });
        InventoryPageRoutes.MapRentalContractsPage(group);

        // Another branch's contract is not found for a branch-bound caller, exactly as an id nobody
        // holds (P0.1b); so is everything read through it below.
        group.MapGet("/contracts/{id:guid}", async (
            Guid id, RentalDocuments rentals, PosDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var docs = await rentals.ReadAsync(id, ct);
            if (docs is null) return Results.NotFound();
            return Results.Ok(await ContractDtoAsync(docs, rentals, db, http.User, ct));
        }).ForBranchOf(DocumentBranches.Contract);

        group.MapPost("/contracts", async (
            UpsertRentalContractRequest r, RentalDocuments rentals, PosDbContext db, HttpContext http,
            CancellationToken ct) =>
        {
            // With or without VAT is the seller's choice, as the customer asks (owner, 2026-11-05).
            var result = await rentals.CreateContractAsync(r, UserName(http), ct);
            if (!result.Ok) return Problem(result);

            var docs = await rentals.ReadAsync(result.Value!.Id, ct);
            return Results.Created($"/api/rentals/contracts/{result.Value.Id}",
                await ContractDtoAsync(docs!, rentals, db, http.User, ct));
        });

        // Editable until the first issue note and locked afterwards. Enforced server-side,
        // because the rule protects prices a customer has signed and goods that have moved.
        group.MapPut("/contracts/{id:guid}", async (
            Guid id, UpsertRentalContractRequest r, RentalDocuments rentals, PosDbContext db, HttpContext http,
            CancellationToken ct) =>
        {
            var result = await rentals.UpdateContractAsync(id, r, ct);
            if (!result.Ok) return Problem(result);

            var docs = await rentals.ReadAsync(id, ct);
            return Results.Ok(await ContractDtoAsync(docs!, rentals, db, http.User, ct));
        }).ForBranchOf(DocumentBranches.Contract);

        group.MapGet("/contracts/{id:guid}/settlement", async (
            Guid id, RentalDocuments rentals, PosDbContext db, CancellationToken ct) =>
        {
            var settlement = await rentals.SettleAsync(id, ct);
            if (settlement is null) return Results.NotFound();
            var contract = await db.RentalContracts.AsNoTracking().FirstAsync(c => c.Id == id, ct);
            var settings = await rentals.SettingsAsync(ct);
            // Outside every figure below: held on the customer's behalf, never netted. Null
            // while the switch is off so no zero ever prints; ToReturn is the same figure once
            // the contract is closed — stated, never a payment (docs/07 §3.3).
            var depositVisible = Engine.RentalDeposit.IsVisible(settings);

            return Results.Ok(new RentalSettlementDto(
                id, contract.Status.ToString(), settlement.IsClosed, settlement.StillOutQty,
                settlement.ChargeTotal, settlement.UnusedRent,
                settlement.Total(Engine.RentalChargeKind.Rent),
                settlement.Total(Engine.RentalChargeKind.Overdue),
                settlement.Total(Engine.RentalChargeKind.Maintenance),
                settlement.Total(Engine.RentalChargeKind.Damage),
                settlement.Total(Engine.RentalChargeKind.Missing),
                settlement.Lines.Select(l => new RentalLineSettlementDto(
                    l.LineId, l.IssuedQty, l.SettledQty, l.StillOutQty, l.OverReturnedQty,
                    l.IssuedPeriodRent, l.UnusedRent, l.IsCleared,
                    l.Charges.Select(c => new RentalChargeDto(
                        Guid.Empty, c.LineId, c.Kind.ToString(), c.Amount, c.ReturnNoteId,
                        c.IssueNoteId, c.ExtensionId, c.Qty, c.Days, c.UnitPrice,
                        // Recomputed, not read back: this is what the arithmetic says today,
                        // beside the frozen charges the customer was actually handed.
                        c.Kind != Engine.RentalChargeKind.Rent || c.ExtensionId is not null)).ToList())).ToList(),
                DepositHeld: depositVisible ? Engine.RentalDeposit.Held(settings, contract.DepositAmount) : null,
                DepositToReturn: depositVisible
                    ? Engine.RentalDeposit.ToReturn(settings, contract.DepositAmount, settlement.IsClosed)
                    : null));
        }).ForBranchOf(DocumentBranches.Contract);

        MapAttachments(group);

        group.MapGet("/contracts/{id:guid}/charges", async (Guid id, PosDbContext db, CancellationToken ct) =>
        {
            // An unknown contract is not found, as another branch's is; an empty list would tell them apart.
            if (!await ContractExistsAsync(db, id, ct)) return Results.NotFound();
            var charges = await db.RentalCharges.AsNoTracking()
                .Where(c => c.ContractId == id).OrderBy(c => c.CreatedAtUtc).ToListAsync(ct);
            return Results.Ok(charges.Select(Map));
        }).ForBranchOf(DocumentBranches.Contract);
    }

    private static Task<bool> ContractExistsAsync(PosDbContext db, Guid id, CancellationToken ct) =>
        db.RentalContracts.AsNoTracking().AnyAsync(c => c.Id == id, ct);

    // ---------- issue, return, extend ----------

    private static void MapNotes(RouteGroupBuilder group)
    {
        group.MapGet("/contracts/{id:guid}/issue-notes", async (
            Guid id, RentalDocuments rentals, PosDbContext db, CancellationToken ct) =>
        {
            var docs = await rentals.ReadAsync(id, ct);
            if (docs is null) return Results.NotFound();
            var skus = await SkusAsync(db, id, ct);
            // Each note's own invoice, worked out the way it was when the note was raised —
            // not the contract's running total, which would report the first delivery's money
            // again beside the second.
            return Results.Ok(docs.IssueNotes.Select(n => Map(n, skus, rentals.PeriodRent(docs, n), rentals.PeriodStartsOn(docs, n))));
        }).ForBranchOf(DocumentBranches.Contract);

        group.MapPost("/contracts/{id:guid}/issue-notes", async (
            Guid id, IssueNoteRequest r, RentalDocuments rentals, PosDbContext db, HttpContext http,
            CancellationToken ct) =>
        {
            var result = await rentals.IssueAsync(id, r, UserId(http), UserName(http), ct);
            if (!result.Ok) return Problem(result);

            var skus = await SkusAsync(db, id, ct);
            var docs = await rentals.ReadAsync(id, ct);
            return Results.Created($"/api/rentals/contracts/{id}/issue-notes/{result.Value!.Note.Id}",
                Map(result.Value.Note, skus, result.Value.PeriodRent, docs is null ? null : rentals.PeriodStartsOn(docs, result.Value.Note)));
        }).ForBranchOf(DocumentBranches.Contract);

        group.MapGet("/contracts/{id:guid}/return-notes", async (Guid id, PosDbContext db, CancellationToken ct) =>
        {
            if (!await ContractExistsAsync(db, id, ct)) return Results.NotFound();
            var notes = await db.ReturnNotes.AsNoTracking().Include(n => n.Lines).ThenInclude(l => l.Allocations)
                .Where(n => n.ContractId == id).OrderBy(n => n.ReturnedAtUtc).ToListAsync(ct);
            var skus = await SkusAsync(db, id, ct);
            var charges = await db.RentalCharges.AsNoTracking().Where(c => c.ContractId == id).ToListAsync(ct);
            return Results.Ok(notes.Select(n => Map(
                n, skus, charges.Where(c => c.ReturnNoteId == n.Id).ToList(), [], false)));
        }).ForBranchOf(DocumentBranches.Contract);

        group.MapPost("/contracts/{id:guid}/return-notes", async (
            Guid id, ReturnNoteRequest r, RentalDocuments rentals, ReceivableDocuments receivables,
            PosDbContext db, HttpContext http,
            CancellationToken ct) =>
        {
            await using var transaction = await db.BeginWriteScopeAsync(ct);
            var result = await rentals.ReturnAsync(id, r, UserId(http), UserName(http), ct);
            if (!result.Ok) return Problem(result);

            var outcome = result.Value!;
            var credit = await receivables.CreditEarlyReturnAsync(outcome.Note.Id, UserName(http), ct);
            if (credit is { Ok: false })
                return Results.Conflict(new { code = "rentalCreditFailed", message = credit.Message ?? "The return could not be credited." });
            await transaction.CommitAsync(ct);

            var skus = await SkusAsync(db, id, ct);
            return Results.Created($"/api/rentals/contracts/{id}/return-notes/{outcome.Note.Id}",
                Map(outcome.Note, skus, outcome.Charges, outcome.Transfers, outcome.ContractClosed));
        }).ForBranchOf(DocumentBranches.Contract);

        group.MapPost("/contracts/{id:guid}/return-notes/preview", async (
            Guid id, ReturnNoteRequest r, RentalDocuments rentals, CancellationToken ct) =>
        {
            var preview = await rentals.PreviewReturnAsync(id, r, ct);
            return preview is null ? Results.NotFound() : Results.Ok(preview);
        }).ForBranchOf(DocumentBranches.Contract);

        // Answer 8: an extension is a new contract for the customer, its goods carried over.
        group.MapPost("/contracts/{id:guid}/extend-as-new", async (
            Guid id, ExtendAsNewContractRequest r, RentalDocuments rentals, PosDbContext db, HttpContext http,
            CancellationToken ct) =>
        {
            var result = await rentals.ExtendAsNewAsync(id, r, UserId(http), UserName(http), ct);
            if (!result.Ok) return Problem(result);
            var docs = await rentals.ReadAsync(result.Value!.Id, ct);
            return Results.Created($"/api/rentals/contracts/{result.Value.Id}", await ContractDtoAsync(docs!, rentals, db, http.User, ct));
        }).ForBranchOf(DocumentBranches.Contract);

        // Answer 15: the Admin may close a contract that is not paid in full, saying why.
        group.MapPost("/contracts/{id:guid}/close", async (
            Guid id, CloseRentalContractRequest r, RentalDocuments rentals, PosDbContext db, HttpContext http,
            CancellationToken ct) =>
        {
            var result = await rentals.CloseShortAsync(id, r.Reason, UserName(http), ct);
            if (!result.Ok) return Problem(result);
            var docs = await rentals.ReadAsync(id, ct);
            return Results.Ok(await ContractDtoAsync(docs!, rentals, db, http.User, ct));
        }).ForBranchOf(DocumentBranches.Contract);

        // Called off before its exit note (owner, 2026-10-06), by whoever may raise or edit one;
        // refused once anything has gone out. The row is kept, and from now on only
        // rentals.cancelled reads it.
        group.MapPost("/contracts/{id:guid}/cancel", async (
            Guid id, CancelRentalContractRequest r, RentalDocuments rentals, PosDbContext db, HttpContext http,
            CancellationToken ct) =>
        {
            var result = await rentals.CancelContractAsync(id, r.Reason, UserName(http), ct);
            if (!result.Ok) return Problem(result);
            var docs = await rentals.ReadAsync(id, ct);
            return Results.Ok(await ContractDtoAsync(docs!, rentals, db, http.User, ct));
        }).ForBranchOf(DocumentBranches.Contract);

        group.MapGet("/contracts/{id:guid}/extensions", async (Guid id, PosDbContext db, CancellationToken ct) =>
        {
            if (!await ContractExistsAsync(db, id, ct)) return Results.NotFound();
            var extensions = await db.ContractExtensions.AsNoTracking()
                .Where(e => e.ContractId == id).OrderBy(e => e.RaisedOnUtc).ToListAsync(ct);
            return Results.Ok(extensions.Select(Map));
        }).ForBranchOf(DocumentBranches.Contract);

        group.MapPost("/contracts/{id:guid}/extensions", async (
            Guid id, ContractExtensionRequest r, RentalDocuments rentals, HttpContext http, CancellationToken ct) =>
        {
            // A retroactive extension needs a manager, and the role check is the engine's
            // question to answer — the handler only says who is asking.
            // Back-office only (group policy), so the engine's retroactive-needs-a-manager
            // refusal is satisfied by construction; a till route would have to pass the real
            // answer and be BranchScoped. The role sniff that stood here was always true.
            var result = await rentals.ExtendAsync(id, r, isManager: true, UserId(http), UserName(http), ct);
            return result.Ok
                ? Results.Created($"/api/rentals/contracts/{id}/extensions/{result.Value!.Id}", Map(result.Value))
                : Problem(result);
        }).ForBranchOf(DocumentBranches.Contract);
    }

    // ---------- the identity scan ----------

    /// <summary>
    /// The files kept with a contract (answer 10a: a copy of the signatory's identity document).
    /// One file per upload, multipart, read by hand rather than bound, because a bound form file
    /// asks for antiforgery tokens this bearer-token API has no use for.
    /// </summary>
    private static void MapAttachments(RouteGroupBuilder group)
    {
        group.MapPost("/contracts/{id:guid}/attachments", async (
            Guid id, HttpRequest request, RentalAttachments attachments, HttpContext http, CancellationToken ct) =>
        {
            // Refused before the body is read when it says it is far too large: the form reader
            // would otherwise buffer it all to find out.
            if (request.ContentLength is long declared && declared > RentalDocuments.MaxAttachmentBytes + 64 * 1024)
                return Results.BadRequest("The file is larger than 5 MB. Take the photo again at a lower resolution, or scan it as a smaller PDF.");
            if (!request.HasFormContentType)
                return Results.BadRequest("Send the file as multipart/form-data, one file per request.");

            var form = await request.ReadFormAsync(ct);
            if (form.Files.Count != 1)
                return Results.BadRequest("Send exactly one file.");
            var file = form.Files[0];
            if (file.Length > RentalDocuments.MaxAttachmentBytes)
                return Results.BadRequest("The file is larger than 5 MB. Take the photo again at a lower resolution, or scan it as a smaller PDF.");

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            var result = await attachments.AddAsync(
                id, form["kind"].ToString(), file.FileName, buffer.ToArray(), UserName(http), ct);
            if (!result.Ok) return Problem(result);

            var a = result.Value!;
            return Results.Created($"/api/rentals/contracts/{id}/attachments/{a.Id}",
                new RentalAttachmentDto(a.Id, a.Kind, a.FileName, a.ContentType, a.Length, a.UploadedAtUtc));
        }).ForBranchOf(DocumentBranches.Contract);

        // Every file kept with a contract is a copy of an identity document, so reading the list or a
        // file takes customers.documents.view (RoutePermissions, owner 2026-10-25), and the contract
        // must be of the caller's branch (P0.1b): checked before any file is opened.
        group.MapGet("/contracts/{id:guid}/attachments", async (
            Guid id, RentalDocuments rentals, PosDbContext db, CancellationToken ct) =>
        {
            if (!await ContractExistsAsync(db, id, ct)) return Results.NotFound();
            return Results.Ok(await rentals.AttachmentsAsync(id, ct));
        }).ForBranchOf(DocumentBranches.Contract);

        group.MapGet("/contracts/{id:guid}/attachments/{attachmentId:guid}", async (
            Guid id, Guid attachmentId, RentalAttachments attachments, HttpContext http, CancellationToken ct) =>
        {
            // From the files folder, through this endpoint only, never a public link. The file must
            // also be this contract's: another contract's id under this one is not found.
            var opened = await attachments.OpenAsync(id, attachmentId, ct);
            if (opened is null) return Results.NotFound();
            var (attachment, content) = opened.Value;
            // Its own sniffed type, never guessed again by the browser; private, never cached by a proxy.
            FileResults.Harden(http);
            http.Response.Headers.CacheControl = "private, no-store";
            return Results.File(content, attachment.ContentType);
        }).ForBranchOf(DocumentBranches.Contract);

        group.MapDelete("/contracts/{id:guid}/attachments/{attachmentId:guid}", async (
            Guid id, Guid attachmentId, RentalAttachments attachments, CancellationToken ct) =>
        {
            var result = await attachments.RemoveAsync(id, attachmentId, ct);
            return result.Ok ? Results.NoContent() : Problem(result);
        }).ForBranchOf(DocumentBranches.Contract);
    }

    // ---------- customer quantity statement ----------

    /// <summary>
    /// كشف كميات العميل / Relevé des quantités client (owner, 2026-10-12): what went out to one
    /// customer, what came back and what is still with them, with the rental value of the window.
    /// Plain dates, read in the company's zone; <c>to</c> is included. <c>current=true</c> is the
    /// "current quantities" view: as of today, no dates needed. Read with rentals.view.
    /// </summary>
    private static void MapQuantityStatement(RouteGroupBuilder group)
    {
        group.MapGet("/quantity-statement", async (
            Guid customerId, string? from, string? to, bool? current, string? productIds, Guid? contractId,
            Guid? branchId, Guid? siteId, string? status, string? search,
            CustomerQuantityStatements statements, CancellationToken ct) =>
        {
            var (query, error) = QuantityQuery(statements, customerId, from, to, current, productIds, contractId,
                branchId, siteId, status, search);
            if (error is not null) return error;
            var statement = await statements.BuildAsync(query!, ct);
            return statement is null ? Results.NotFound("Unknown customer.") : Results.Ok(statement);
        });

        // The same query, the same statement, as an .xlsx through the shared writer: what the
        // export totals is what the screen totals.
        group.MapGet("/quantity-statement/export", async (
            Guid customerId, string? from, string? to, bool? current, string? productIds, Guid? contractId,
            Guid? branchId, Guid? siteId, string? status, string? search, string? lang,
            CustomerQuantityStatements statements, PosDbContext db, IRegionalSettingsProvider regional,
            CancellationToken ct) =>
        {
            var (query, error) = QuantityQuery(statements, customerId, from, to, current, productIds, contractId,
                branchId, siteId, status, search);
            if (error is not null) return error;
            var statement = await statements.BuildAsync(query!, ct);
            if (statement is null) return Results.NotFound("Unknown customer.");

            var branches = await db.Branches.AsNoTracking().ToListAsync(ct);
            string BranchName(Guid? id)
            {
                var b = branches.FirstOrDefault(x => x.Id == id);
                if (b is null) return "—";
                var name = lang switch { "ar" => b.NameAr, "en" => b.NameEn, _ => b.NameFr };
                return string.IsNullOrWhiteSpace(name) ? b.NameFr : name;
            }

            var bytes = QuantityStatementWorkbook.Build(statement, lang, BranchName, regional.Get().CurrencyDecimals);
            return Results.File(bytes, ParadiERP.Infrastructure.Reports.XlsxWriter.ContentType,
                string.Create(CultureInfo.InvariantCulture, $"quantity-statement-{statement.CustomerCode}-{statement.To:yyyy-MM-dd}.xlsx"));
        });
    }

    /// <summary>The statement's filters, read and refused in words; shared with the export so both read the same query.</summary>
    internal static (QuantityStatementQuery? Query, IResult? Error) QuantityQuery(
        CustomerQuantityStatements statements, Guid customerId, string? from, string? to, bool? current,
        string? productIds, Guid? contractId, Guid? branchId, Guid? siteId, string? status, string? search)
    {
        DateOnly start, end;
        if (current == true)
        {
            start = end = statements.Today();
        }
        else
        {
            if (!CompanyDays.TryParseDay(from, out start) || !CompanyDays.TryParseDay(to, out end))
                return (null, Results.BadRequest("from and to are required, as plain dates (yyyy-MM-dd)."));
            if (end < start) return (null, Results.BadRequest("'to' must not be before 'from'."));
        }

        var wantedStatus = string.IsNullOrWhiteSpace(status) || status == "all" ? null : status;
        if (wantedStatus is not null && !CustomerQuantityStatements.Statuses.Contains(wantedStatus))
            return (null, Results.BadRequest("status is all, open, goodsBack or closed."));

        var products = new List<Guid>();
        foreach (var raw in (productIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(raw, out var id)) return (null, Results.BadRequest("productIds is a comma-separated list of ids."));
            products.Add(id);
        }

        return (new QuantityStatementQuery(customerId, start, end, current == true, products, contractId, branchId,
            siteId, wantedStatus, search), null);
    }

    // ---------- mapping ----------

    /// <summary>
    /// The contract as every screen reads it. Its identity-document copies — their names, sizes and
    /// dates — travel only to a caller holding customers.documents.view; anyone else gets none,
    /// whatever is on file, so the contract says nothing about them (owner, 2026-10-25).
    /// </summary>
    private static async Task<RentalContractDto> ContractDtoAsync(
        RentalDocuments.Documents docs, RentalDocuments rentals, PosDbContext db, ClaimsPrincipal caller,
        CancellationToken ct)
    {
        var progress = rentals.Progress(docs);
        var settings = await rentals.SettingsAsync(ct);
        var standing = await RentalClosure.StandingAsync(db, docs.Contract.Id, ct);
        var extendedBy = await db.RentalContracts.AsNoTracking()
            .Where(x => x.ExtendsContractId == docs.Contract.Id)
            .OrderByDescending(x => x.SignedAtUtc).Select(x => new { x.Id, x.Number }).FirstOrDefaultAsync(ct);
        var customer = await db.Customers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == docs.Contract.CustomerId, ct);

        return new RentalContractDto(
            docs.Contract.Id, docs.Contract.Number, docs.Contract.CustomerId, customer?.FullName,
            docs.Contract.BranchId, docs.Contract.Status.ToString(),
            docs.Contract.LockedAtUtc is not null, docs.Contract.LockedAtUtc,
            docs.Contract.SignedAtUtc, docs.Contract.ClosedAtUtc, docs.Contract.Notes,
            docs.Lines.Select(l =>
            {
                var line = progress.GetValueOrDefault(l.Id);
                var issued = line?.IssuedQty ?? 0m;
                return new RentalContractLineDto(
                    l.Id, l.ProductId, l.Sku, l.NameSnapshot, l.Qty, l.DurationDays,
                    l.RentUnitPrice, l.DamageUnitPrice, l.OverdueUnitPrice, l.ParentLineId,
                    issued, Math.Max(0m, l.Qty - issued), line?.SettledQty ?? 0m, line?.StillOutQty ?? 0m,
                    // The rate frozen at signing, so the printed contract shows what was agreed.
                    TaxRate: l.TaxRate,
                    // Printed beside the French name (answer 28); null on lines signed before it.
                    NameAr: l.NameArSnapshot,
                    RentalTotal: rentals.ContractLineRent(docs, l));
            }).ToList(),
            // Invisible while the switch is off: no field, no zero line, no report column (§5.6).
            Engine.RentalDeposit.IsVisible(settings) ? docs.Contract.DepositAmount : null,
            // The snapshot as stored — never re-read from the customer — and the customer's kind,
            // so the screen can say "for the company" or "predates the signatory rule" in words.
            docs.Contract.SignatoryName, docs.Contract.SignatoryIdNumber, docs.Contract.SignatoryRequired,
            customer?.Kind.ToString() ?? nameof(CustomerKind.Unspecified),
            docs.Contract.SiteId, docs.Contract.SiteName, docs.Contract.SiteAddress,
            docs.Contract.SignatoryIdType, docs.Contract.SignatoryIdIssuer,
            docs.Contract.SignatoryIdIssuedOn, docs.Contract.SignatoryBirthDate,
            docs.Contract.TruckType, docs.Contract.TruckNumber, docs.Contract.TruckOwnerPhone,
            ServerAuthorization.Has(caller, PermissionCatalog.CustomersDocumentsView)
                ? await rentals.AttachmentsAsync(docs.Contract.Id, ct)
                : [],
            docs.Contract.GoodsBackAtUtc, standing.Owed, standing.Paid,
            docs.Contract.ClosedShortReason, docs.Contract.ClosedShortBy,
            docs.Contract.ExtendsContractId,
            docs.Contract.ExtendsContractId is Guid parentId
                ? await db.RentalContracts.AsNoTracking().Where(x => x.Id == parentId).Select(x => x.Number).FirstOrDefaultAsync(ct)
                : null,
            extendedBy?.Id, extendedBy?.Number,
            docs.Contract.WordingKey,
            docs.Contract.SignedByAgent,
            RentalTotal: docs.Lines.Sum(l => rentals.ContractLineRent(docs, l)),
            CancelledAtUtc: docs.Contract.CancelledAtUtc, CancelledBy: docs.Contract.CancelledBy,
            CancelReason: docs.Contract.CancelReason,
            MinimumRentalDays: docs.Contract.MinimumRentalDays,
            TaxMode: docs.Contract.TaxMode.ToString());
    }

    private static IssueNoteDto Map(
        IssueNote note, IReadOnlyDictionary<Guid, (Guid ProductId, string Sku)> skus, decimal periodRent, DateOnly? startsOn) =>
        new(note.Id, note.Number, note.ContractId, note.BranchId, note.IssuedAtUtc,
            note.UserName, note.Note, periodRent,
            note.Lines.Select(l => new IssueNoteLineDto(
                l.ContractLineId, skus.GetValueOrDefault(l.ContractLineId).ProductId,
                skus.GetValueOrDefault(l.ContractLineId).Sku, l.Qty)).ToList(),
            note.DriverName, note.DriverPhone, note.TruckNumber, startsOn, note.CustomerTransport, note.IsCarryOver);

    private static ReturnNoteDto Map(
        ReturnNote note, IReadOnlyDictionary<Guid, (Guid ProductId, string Sku)> skus,
        IReadOnlyList<RentalCharge> charges, IReadOnlyList<ImplicitTransfer> transfers, bool closed) =>
        new(note.Id, note.Number, note.ContractId, note.BranchId, note.ReturnedAtUtc,
            note.UserName, note.Note, note.UnusedRent, note.ReliefDebtReduction, note.ReliefPaymentVoucher,
            // Summed here rather than in the database: SQLite stores decimal as TEXT, so a
            // server-side Sum would compare strings.
            charges.Sum(c => c.Amount), closed,
            note.Lines.Select(l => new ReturnNoteLineDto(
                l.ContractLineId, skus.GetValueOrDefault(l.ContractLineId).ProductId,
                skus.GetValueOrDefault(l.ContractLineId).Sku,
                l.QtyOk, l.QtyPartialDamage, l.MaintenanceValue, l.QtyTotalDamage, l.QtyMissing,
                l.ReportedOnUtc, l.SameDayUsage.ToString(), l.Id,
                l.Allocations.Select(a => new ReturnAllocationDto(a.Id, a.IssueNoteId, a.Qty,
                    a.Condition.ToString(), a.ChargeableDays, a.ActualRentNet, a.SameDayUsage.ToString(), a.IssuedAtUtc)).ToList())).ToList(),
            charges.Select(Map).ToList(),
            transfers.Select(t => new ImplicitTransferDto(
                t.ProductId, t.FromBranchId, t.ToBranchId, t.Qty, t.State.ToString())).ToList(),
            note.DriverName, note.DriverPhone, note.TruckNumber,
            note.CountReturnDay, note.ReturnDayReason, note.ReturnDayDecidedBy, note.CustomerTransport, note.IsCarryOver,
            note.WaiveOverdue, note.LateWaiverReason, note.LateDecidedBy,
            note.LateDaysWaived, note.LateAmountWaived, note.LateAmountFull);

    private static RentalChargeDto Map(RentalCharge charge) =>
        new(charge.Id, charge.ContractLineId, charge.Kind.ToString(), charge.Amount, charge.ReturnNoteId,
            charge.IssueNoteId, charge.ExtensionId, charge.Qty, charge.Days, charge.UnitPrice, charge.IsPosted);

    private static ContractExtensionDto Map(ContractExtension extension) =>
        new(extension.Id, extension.Number, extension.ContractId, extension.ContractLineId,
            extension.ExtraDays, extension.UnitPrice, extension.RaisedOnUtc, extension.PeriodEndsOnUtc,
            extension.IsRetroactive, extension.ApprovedByName, extension.Reason);

    private static async Task<IReadOnlyDictionary<Guid, (Guid ProductId, string Sku)>> SkusAsync(
        PosDbContext db, Guid contractId, CancellationToken ct) =>
        (await db.RentalContractLines.AsNoTracking().Where(l => l.ContractId == contractId).ToListAsync(ct))
        .ToDictionary(l => l.Id, l => (l.ProductId, l.Sku));

    /// <summary>Whether the caller still sees a contract once it is cancelled: rentals.cancelled, the Admin's.</summary>
    private static bool SeesCancelled(ClaimsPrincipal user) =>
        ServerAuthorization.Read(user)?.Has(PermissionCatalog.RentalsCancelled) ?? false;

    /// <summary>
    /// Every route on one contract (<c>/contracts/{id}/…</c>) answers 404 for a cancelled contract
    /// to whoever lacks rentals.cancelled — read, print, files, notes alike — so a cashier who
    /// cancels one stops seeing it at once (owner, 2026-10-06). Routes on other ids pass untouched.
    /// </summary>
    private static async ValueTask<object?> HideCancelledAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var pattern = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? string.Empty;
        if (pattern.Contains("/contracts/{id", StringComparison.Ordinal)
            && Guid.TryParse(http.Request.RouteValues["id"]?.ToString(), out var id)
            && !SeesCancelled(http.User))
        {
            var db = http.RequestServices.GetRequiredService<PosDbContext>();
            if (await db.RentalContracts.AsNoTracking()
                    .AnyAsync(c => c.Id == id && c.Status == RentalContractStatus.Cancelled, http.RequestAborted))
                return Results.NotFound("Contract not found.");
        }
        return await next(context);
    }

    private static Guid? UserId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue("sub"), out var id) ? id : null;

    private static string? UserName(HttpContext http) =>
        http.User.FindFirstValue("name") ?? http.User.Identity?.Name;

    private static IResult Problem<T>(RentalResult<T> result) => result.Failure switch
    {
        RentalFailure.NotFound => Results.NotFound(result.Message),
        RentalFailure.Conflict => Results.Conflict(result.Message),
        // A refusal that names its code says why itself; the rest are the manager-approval ones.
        RentalFailure.Forbidden when result.Code is not null => Results.Problem(
            result.Message, statusCode: StatusCodes.Status403Forbidden,
            extensions: new Dictionary<string, object?> { ["code"] = result.Code }),
        RentalFailure.Forbidden => Results.Problem(
            result.Message, statusCode: StatusCodes.Status403Forbidden, title: "Manager approval required"),
        _ => Results.BadRequest(result.Message)
    };
}
