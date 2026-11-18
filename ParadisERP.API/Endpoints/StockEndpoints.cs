using System.Security.Claims;
using ParadisERP.API.Subscriptions;
using ParadisERP.Application.Catalog;
using ParadisERP.Application.Contracts;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Auth;
using ParadisERP.Infrastructure.Catalog;
using ParadisERP.Infrastructure.FixedAssets;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Infrastructure.Valuation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.API.Endpoints;

/// <summary>
/// How goods enter the company and what state they are in once inside.
///
/// <para>
/// Receiving is the one way new goods enter, and it enters them <b>at the branch that buys
/// them</b> (owner, 2026-10-12: "a branch can buy items from suppliers itself"). A branch user
/// receives into their own branch — the branch lock decides it — and the Admin names any branch,
/// the main warehouse when he names none. Until 2026-10-12 the rule was "new goods enter the
/// warehouse first and reach a branch by transfer", and receiving had no branch at all.
/// </para>
/// <para>
/// Before these routes one generic adjustment endpoint did receiving, breakage and stocktake alike
/// and accepted any branch, so no rule about where goods enter could be stated, let alone enforced.
/// </para>
/// <para>
/// Receiving and adjustment are separated rather than merged into a ban on branch entries. A
/// branch that counts and finds three pipes missing has to be able to correct it; forbid every
/// branch entry and stocktaking becomes impossible.
/// </para>
/// </summary>
public static class StockEndpoints
{
    public static void MapStockEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/catalog/stock").WithTags("Stock")
            .AddEndpointFilter<SubscriptionWriteFilter>()
            .AtCallersBranch();

        // ---------- receiving: the only way new goods enter, at the branch that buys them ----------
        group.MapPost("/receiving", async (
            ReceivingRequest r, ClaimsPrincipal caller, PosDbContext db, LedgerPoster ledger,
            [FromServices] ReceivingCostPrice costPrice, [FromServices] CostPriceRevaluationPosting revaluation, CancellationToken ct) =>
        {
            if (r.Lines is null || r.Lines.Count == 0) return Results.BadRequest("A receiving note needs at least one line.");
            if (r.Lines.Any(l => l.Qty <= 0)) return Results.BadRequest("Every received quantity must be positive.");

            // Where the goods enter (owner, 2026-10-12). For a branch user the lock (AtCallersBranch) has
            // already replaced BranchId with their own branch; the Admin's choice stands as sent. None
            // named — the Admin's default and every older client — is the main warehouse, as before.
            Branch? at;
            if (r.BranchId is Guid branchId)
            {
                at = await db.Branches.FirstOrDefaultAsync(b => b.Id == branchId && !b.IsDeleted, ct);
                if (at is null) return Results.NotFound("Branch not found.");
            }
            else
            {
                at = await db.Branches.FirstOrDefaultAsync(b => b.IsMain && b.IsWarehouse && !b.IsDeleted, ct);
                if (at is null) return Results.BadRequest("No main warehouse is configured.");
            }

            await using var tx = await db.BeginWriteScopeAsync(ct);
            var author = MovementAuthor.Of(caller);
            // The supplier who delivered, read under the version lock: the note moves their balance.
            Supplier? supplier = null;
            if (r.SupplierId is Guid supplierId)
            {
                supplier = await db.LockedAsync<Supplier>(supplierId, ct);
                if (supplier is null || supplier.IsDeleted) return Results.NotFound("Unknown supplier.");
            }
            var touched = new List<SyncEntity>();
            // One cache for the whole note. A clerk listing the same product on two lines is
            // ordinary, and a fresh query cannot see the row the first line just added — so
            // without this the second line adds a duplicate and the unique index refuses it.
            var rows = new StockRows(db);

            // The note's own identity. Five lines of one delivery were five unrelated rows
            // before this; it is what the ledger entry points back to and what groups the
            // movements when someone asks where a figure came from.
            var documentId = Guid.NewGuid();
            var valued = new List<ValuedLine>();
            // What the note does to each product's cost price is the accountant's setting (B-F01, 2026-11-09).
            var rule = await costPrice.RuleAsync(ct);
            // And what a line stated at 0 is valued at; and the cost prices before the note, for a revaluation (2026-11-09).
            var zero = await costPrice.ZeroCostAsync(ct);
            var costsBefore = new Dictionary<Guid, decimal>();
            var received = new List<(Product Product, decimal? Stated)>(r.Lines.Count);
            // REN-12 (Phase M7a): while the fixed-asset register holds the hire equipment, a hire product's line joins its pool.
            var assets = new FixedAssetBook(db);

            foreach (var line in r.Lines)
            {
                var product = await db.Products.FirstOrDefaultAsync(p => p.Id == line.ProductId, ct);
                if (product is null) return Results.NotFound($"Product {line.ProductId} not found.");

                // What this line was bought at: the cost it states (0 included: a free delivery is
                // valued at nothing), or the cost price as it stands. The movement and the entry carry
                // it whatever the setting does to the cost price.
                var unitCost = ReceivingCostPrice.UnitCostOf(line.UnitCost, product.CostPrice, zero);
                costsBefore.TryAdd(product.Id, product.CostPrice);
                // The receiving note is the only place the purchase price and the asset value are
                // known. Under the last cost rule (the default) the stated cost becomes the cost
                // price here — except a 0 over a non-zero one (B-F06); under the weighted average it
                // is restated once the movements are saved, below; unchanged, never.
                ReceivingCostPrice.ApplyLine(product, line.UnitCost, rule);
                received.Add((product, line.UnitCost));
                if (line.AssetUnitValue is decimal asset && asset >= 0) product.AssetUnitValue = asset;
                if (line.UnitCost is not null || line.AssetUnitValue is not null) touched.Add(product);

                var stock = await rows.GetAsync(line.ProductId, at.Id, ct);
                stock.StockQty += line.Qty;

                db.StockMovements.Add(new StockMovement
                {
                    ProductId = line.ProductId,
                    BranchId = at.Id,
                    QtyDelta = line.Qty,
                    BalanceAfter = stock.StockQty,
                    DocumentId = documentId,
                    // A supplier's own reference can be any length; the label is cut, and the
                    // whole reference stays on the ledger entry raised below.
                    Reason = StockMovement.Label("receiving", r.Reference),
                    Kind = StockMovementKind.Receipt,
                    // What was paid: the same figure the entry values.
                    UnitCost = unitCost,
                    UserId = author.UserId,
                    UserName = author.UserName,
                    DocumentNumber = MovementAuthor.DocumentNumber(r.Reference),
                    SupplierId = supplier?.Id,
                    MovedQty = line.Qty
                });

                // Valued at what was paid for it. A hire product held by the fixed-asset register (REN-12) is debited to the
                // asset account instead of 310 and joins its pool at that cost; off (the default), every line is 310 as before.
                var account = await assets.HoldsAsync(product.Id, ct)
                    ? await assets.AcquireAsync(new FixedAssetEvent(product.Id, at.Id, line.Qty, product.Sku, documentId,
                        MovementAuthor.DocumentNumber(r.Reference), "receiving", author.UserName), line.Qty * unitCost, ct)
                    : null;
                valued.Add(new ValuedLine(product.Sku, line.Qty * unitCost, account));
            }

            var receivedValue = valued.Sum(v => v.Value);
            await ledger.PostAsync(StockPosting.Receiving(documentId, at.Id, r.Reference, valued, supplier?.Id), ct);
            if (supplier is not null)
            {
                // At the ledger's own scale, so the running balance equals what 401 holds for them.
                supplier.Balance += Math.Round(receivedValue, 3, MidpointRounding.AwayFromZero);
                supplier.UpdatedAtUtc = DateTime.UtcNow;
            }

            if (rule == ReceiptCostPriceRule.WeightedAverage)
            {
                // The average after this receipt: its movements are saved first, in this transaction,
                // so the shadow's replay reads them as the report will.
                await db.SaveChangesAsync(ct);
                touched.AddRange(await costPrice.ApplyAverageAsync(r.Reference, received, ct));
            }

            foreach (var entity in touched.Distinct()) await db.TouchAsync(entity, ct);
            await db.SaveChangesAsync(ct);
            // A cost price the last cost rule changed revalues what was held before the note, when the accountant chose it
            // (B-F01); never under the weighted average rule, and never a reason to refuse the note.
            if (rule == ReceiptCostPriceRule.LastCost && await revaluation.ReceivingAsync(
                    documentId, r.Reference, at.Id, costsBefore, received.Select(x => x.Product),
                    r.Lines.Select(l => (l.ProductId, l.Qty)), author.UserName, ct))
                await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { received = r.Lines.Count, branchId = at.Id, documentId, value = receivedValue });
        });

        // ---------- opening balance: the exception that has to exist ----------
        group.MapPost("/opening", async (
            OpeningBalanceRequest r, ClaimsPrincipal caller, PosDbContext db, LedgerPoster ledger, ILoggerFactory logs,
            CancellationToken ct) =>
        {
            if (r.Lines is null || r.Lines.Count == 0) return Results.BadRequest("An opening balance needs at least one line.");
            if (r.Lines.Any(l => l.Qty < 0)) return Results.BadRequest("An opening quantity cannot be negative.");

            var branch = await db.Branches.FirstOrDefaultAsync(b => b.Id == r.BranchId && !b.IsDeleted, ct);
            if (branch is null) return Results.NotFound("Branch not found.");

            // Once a branch is trading, "opening balance" would become the back door people use
            // to add goods no supplier delivered — skipping the receiving note, its cost and the
            // supplier's account — and the rule above would stop meaning anything.
            if (branch.IsOpenForOperation)
                return Results.BadRequest(OpeningStock.BranchAlreadyOpen);

            await using var tx = await db.BeginWriteScopeAsync(ct);

            var lines = new List<OpeningQuantity>(r.Lines.Count);
            foreach (var line in r.Lines)
            {
                var product = await db.Products.FirstOrDefaultAsync(p => p.Id == line.ProductId, ct);
                if (product is null) return Results.NotFound($"Product {line.ProductId} not found.");
                lines.Add(new OpeningQuantity(product, line.Qty, line.UnitValue));
            }

            var author = MovementAuthor.Of(caller);
            var applied = await OpeningStock.ApplyAsync(db, ledger, branch, lines, note: null, ct, author.UserId, author.UserName,
                logs.CreateLogger("OpeningStock"));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { branch.Id, lines = r.Lines.Count, applied.DocumentId, value = applied.Value });
        });

        // ---------- opening balance from the branch's own stock sheet ----------

        // Reads the workbook and says what each line would become; writes nothing. Multipart read
        // by hand rather than bound, like the contract scans: a bound form file asks for
        // antiforgery tokens this bearer-token API has no use for.
        group.MapPost("/opening/import/preview", async (HttpRequest request, PosDbContext db, CancellationToken ct) =>
        {
            if (request.ContentLength is long declared && declared > MaxSheetBytes + 64 * 1024)
                return Results.BadRequest(SheetTooLarge);
            if (!request.HasFormContentType)
                return Results.BadRequest("Send the workbook as multipart/form-data, one file per request.");

            var form = await request.ReadFormAsync(ct);
            if (form.Files.Count != 1) return Results.BadRequest("Send exactly one file.");
            var file = form.Files[0];
            if (file.Length > MaxSheetBytes) return Results.BadRequest(SheetTooLarge);

            try
            {
                await using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer, ct);
                buffer.Position = 0;
                var sheet = XlsxReader.Read(buffer, form["sheet"].ToString() is { Length: > 0 } named ? named : null);
                return Results.Ok(await OpeningStock.PreviewAsync(db, Path.GetFileName(file.FileName), sheet, ct));
            }
            catch (SpreadsheetException e)
            {
                return Results.BadRequest(e.Message);
            }
        });

        group.MapPost("/opening/import", async (
            OpeningImportRequest r, ClaimsPrincipal caller, PosDbContext db, LedgerPoster ledger, IRegionalSettingsProvider regional,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var author = MovementAuthor.Of(caller);
            var result = await OpeningStock.ImportAsync(db, ledger, regional, r, ct, author.UserId, author.UserName,
                logs.CreateLogger("OpeningStock"));
            return result.Failure switch
            {
                null => Results.Ok(result.Value),
                OpeningStockFailure.NotFound => Results.NotFound(result.Message),
                _ => Results.BadRequest(result.Message),
            };
        });

        // ---------- declaring a branch open ----------
        group.MapPost("/branches/{id:guid}/open", async (Guid id, PosDbContext db, CancellationToken ct) =>
        {
            var branch = await db.Branches.FirstOrDefaultAsync(b => b.Id == id, ct);
            if (branch is null) return Results.NotFound();
            if (branch.IsOpenForOperation) return Results.Ok(new { branch.Id, branch.IsOpenForOperation });

            branch.IsOpenForOperation = true;
            await db.SaveWithVersionsAsync(ct, branch);
            return Results.Ok(new { branch.Id, branch.IsOpenForOperation });
        });

        // ---------- moving stock between its states ----------
        group.MapPost("/state", async (
            StockStateChangeRequest r, ClaimsPrincipal caller, PosDbContext db, LedgerPoster ledger, ILoggerFactory logs,
            CancellationToken ct) =>
        {
            if (r.Qty <= 0) return Results.BadRequest("The quantity must be positive.");
            if (!Enum.TryParse<StockState>(r.From, true, out var from) || !Enum.TryParse<StockState>(r.To, true, out var to))
                return Results.BadRequest($"States are: {string.Join(", ", Enum.GetNames<StockState>())}.");
            if (from == to) return Results.BadRequest("The source and destination state must differ.");
            // On rent is the contracts' to move (owner, 2026-10-12): goods typed in or out of it by
            // hand have no customer and no return, and the report could not explain them.
            if (from == StockState.OnRent || to == StockState.OnRent)
                return Results.BadRequest(OnRentByContract);
            if (from == StockState.WrittenOff) return Results.BadRequest("Written-off stock has left the business and cannot come back.");

            // MS-02 (docs/maintenance/plan.md §4): with the maintenance module on and "only by a job" chosen, units leave
            // maintenance for available only by completing a job. The default — and the module off — leaves the route as it was.
            if (from == StockState.Maintenance && to == StockState.Available
                && await ParadisERP.Infrastructure.Maintenance.MaintenanceSettingsStore.CurrentAsync(db, ct) is
                    { Module: MaintenanceSwitch.On, UnitsLeaveMaintenance: MaintenanceUnitRelease.OnlyByJob })
                return Results.Problem(detail: MaintenanceOnlyByJob, statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?> { ["code"] = MaintenanceOnlyByJobCode });

            var author = MovementAuthor.Of(caller);
            // The move reads the bucket under the version lock (StockRows), so two moves out of one
            // bucket cannot both pass against the same count. A maintenance job's completion moves its
            // units through the same code (StockStateMoves), so the two can never disagree.
            await using var tx = await db.BeginWriteScopeAsync(ct);
            var moved = await StockStateMoves.MoveAsync(
                db, ledger, new StockRows(db), r.ProductId, r.BranchId, from, to, r.Qty, r.Reason, documentNumber: null,
                author.UserId, author.UserName, ct, logs.CreateLogger("StockStateChange"));
            if (!moved.Ok)
                return moved.Status == StatusCodes.Status404NotFound ? Results.NotFound(moved.Error) : Results.Conflict(moved.Error);
            var stock = moved.Stock!;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Results.Ok(new
            {
                available = stock.StockQty,
                onRent = stock.OnRentQty,
                maintenance = stock.MaintenanceQty,
                scrap = stock.ScrapQty,
                owned = stock.OwnedQty
            });
        });

        // ---------- what another branch has ----------
        // Read-only and reserves nothing: seeing stock elsewhere is not a claim on it, and the
        // transfer stays a separate, deliberate act with its own document.
        //
        // The Admin sees every branch in full. Anyone else sees their own branch in full, and —
        // only with ViewOtherBranchStock — the other branches' available quantity and nothing more
        // (the owner, 2026-09-30). Without the permission the other branches are not in the answer.
        // What every article stands at in one branch, in one read (2026-10-06): the rental form
        // shows it beside each item it offers. AtCallersBranch replaces a non-admin's branchId with
        // their own, so they read their branch whatever they ask for; no branch answers nothing.
        group.MapGet("/levels", async (Guid? branchId, PosDbContext db, CancellationToken ct) =>
        {
            if (branchId is not Guid branch) return Results.Ok(Array.Empty<StockLevelDto>());
            var levels = await db.ProductStocks.AsNoTracking()
                .Where(s => s.BranchId == branch)
                .Select(s => new StockLevelDto(s.ProductId, s.StockQty))
                .ToListAsync(ct);
            return Results.Ok(levels);
        }).WithMetadata(new BranchScopedAttribute(
            "a non-admin's branchId is replaced by their own branch, so they read their branch's levels only"));

        group.MapGet("/availability/{productId:guid}", async (
            Guid productId, ClaimsPrincipal caller, PosDbContext db, CancellationToken ct) =>
        {
            Guid? own = BranchAssignment.IsCompanyScoped(caller)
                ? null
                : await BranchAssignment.ForCallerAsync(caller, db, ct) ?? Guid.Empty;
            var mayLookElsewhere = own is null
                || ParadisERP.Infrastructure.Authorization.ServerAuthorization.Has(caller, PermissionCatalog.StockViewOtherBranches);

            var rows = await db.ProductStocks.AsNoTracking()
                .Where(s => s.ProductId == productId)
                .Where(s => mayLookElsewhere || s.BranchId == own)
                .Join(db.Branches.AsNoTracking().Where(b => !b.IsDeleted && b.IsActive),
                    s => s.BranchId, b => b.Id, (s, b) => new { s, b })
                .ToListAsync(ct);

            return Results.Ok(rows
                .OrderByDescending(x => x.b.IsMain && x.b.IsWarehouse).ThenBy(x => x.b.NameFr)
                .Select(x => own is null || x.b.Id == own
                    ? new BranchAvailabilityDto(
                        x.b.Id, x.b.NameAr, x.b.NameFr, x.b.NameEn, x.b.IsMain,
                        x.s.StockQty, x.s.OnRentQty, x.s.MaintenanceQty, x.s.ScrapQty, x.s.OwnedQty, x.b.IsWarehouse)
                    // Another branch, to a non-admin: what is there to be had, nothing else.
                    : new BranchAvailabilityDto(
                        x.b.Id, x.b.NameAr, x.b.NameFr, x.b.NameEn, x.b.IsMain,
                        x.s.StockQty, null, null, null, null, x.b.IsWarehouse)));
        }).WithMetadata(new BranchScopedAttribute(
            "a non-admin reads their own branch in full and, only with ViewOtherBranchStock, the other branches' available quantity alone"));
    }

    public const string OnRentByContract = "Goods go on rent and come back only through a contract's issue and return notes.";

    /// <summary>MS-02 "only by a job": the manual route's maintenance → available is refused.</summary>
    public const string MaintenanceOnlyByJob =
        "Units leave maintenance for available only by completing a maintenance job (Maintenance › Settings, MS-02).";
    public const string MaintenanceOnlyByJobCode = "maintenance_only_by_job";

    /// <summary>A stock sheet is a few hundred rows of text; 5 MB is many times that, and well short of a photo dump.</summary>
    private const long MaxSheetBytes = 5L * 1024 * 1024;

    private const string SheetTooLarge =
        "The workbook is larger than 5 MB. Keep only the sheet with the stock on it, or save it without pictures.";
}
