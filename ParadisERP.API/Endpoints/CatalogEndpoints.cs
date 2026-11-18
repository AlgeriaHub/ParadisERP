using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ParadisERP.Application.Catalog;
using ParadisERP.Application.Contracts;
using ParadisERP.Application.Customers;
using ParadisERP.Application.Files;
using ParadisERP.Application.Loyalty;
using ParadisERP.Application.Regional;
using ParadisERP.Application.Security;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Auth;
using ParadisERP.Infrastructure.Catalog;
using ParadisERP.Infrastructure.Customers;
using ParadisERP.Infrastructure.Files;
using ParadisERP.Infrastructure.FixedAssets;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Infrastructure.Rentals;
using ParadisERP.Infrastructure.Sync;
using ParadisERP.Infrastructure.Valuation;
using ParadisERP.API.Subscriptions;

namespace ParadisERP.API.Endpoints;

/// <summary>
/// Back-office catalog management. Online only, and restricted to Manager/Admin — terminals
/// are read-only replicas of the catalog, which is what removes catalog merge conflicts.
/// </summary>
public static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/catalog").WithTags("Catalog")
            .AddEndpointFilter<SubscriptionWriteFilter>()
            .AtCallersBranch();
        // The reads a voucher form needs (answer 25): a cash-box holder picks a customer, a branch
        // and a payment method like a manager does, and edits none of them.
        var cash = app.MapGroup("/api/catalog").WithTags("Catalog")
            .AddEndpointFilter<SubscriptionWriteFilter>()
            .AtCallersBranch();
        // Creating a customer is decided by the customers.create capability (FR-1), the same
        // decision the login reports, so the rental form and the till offer it to exactly the
        // callers this route accepts. Editing and deleting one stay in the group above.
        var createCustomers = app.MapGroup("/api/catalog").WithTags("Catalog")
            .AddEndpointFilter<SubscriptionWriteFilter>()
            .AtCallersBranch();

        // ---------- store ----------
        // Single row: the shop's own fiscal identity and receipt footer. Read via sync pull
        // by terminals; edited here in the back office. Which identifiers exist, the currency
        // and the tax rules come from the operator-owned country profile.

        group.MapGet("/store", async (PosDbContext db, CancellationToken ct) =>
        {
            var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => !s.IsDeleted, ct);
            return store is null ? Results.NotFound() : Results.Ok(SyncService.Map(store));
        });

        group.MapPut("/store", async (UpsertStoreRequest r, PosDbContext db, IRegionalSettingsProvider regional, CancellationToken ct) =>
        {
            var store = await db.Stores.FirstOrDefaultAsync(s => !s.IsDeleted, ct);
            if (store is null) return Results.NotFound();

            var profile = regional.Get();
            // Values of identifiers the operator has since removed are kept, so re-adding one restores them.
            if (FiscalIdValues.TryNormalize(profile, r.FiscalIds, store.FiscalIds, out var fiscalIds) is string idProblem)
                return Results.BadRequest(idProblem);
            var missing = profile.FiscalIds.Where(d => d.Required && !fiscalIds.ContainsKey(d.Key)).Select(d => d.Code).ToList();
            if (missing.Count > 0) return Results.BadRequest($"Missing required field(s): {string.Join(", ", missing)}.");

            if (UiFonts.TryChoose(store.UiFont, r.UiFont, out var face) is string faceProblem)
                return Results.BadRequest(faceProblem);
            store.UiFont = face;

            store.NameAr = r.NameAr; store.NameFr = r.NameFr; store.NameEn = r.NameEn;
            store.AddressFr = r.Address; store.AddressAr = r.AddressAr ?? string.Empty; store.AddressEn = r.AddressEn ?? string.Empty;
            store.Address = store.AddressFr; store.Phone = r.Phone;
            store.FiscalIds = fiscalIds;
            store.ReceiptFooterFr = r.ReceiptFooter;
            store.ReceiptFooterAr = r.ReceiptFooterAr ?? string.Empty;
            store.ReceiptFooterEn = r.ReceiptFooterEn ?? string.Empty;
            store.ReceiptFooter = store.ReceiptFooterFr;
            store.RequireCustomerEmail = r.RequireCustomerEmail;
            store.RequireCustomerBirthDate = r.RequireCustomerBirthDate;
            store.RequireCustomerPhone = r.RequireCustomerPhone;
            store.RequireCustomerAddress = r.RequireCustomerAddress;
            store.RequireCustomerTaxId = r.RequireCustomerTaxId;
            var loyalty = new LoyaltyProgram(
                r.LoyaltyEnabled ?? store.LoyaltyEnabled, r.LoyaltyPointsPerUnit ?? store.LoyaltyPointsPerUnit,
                r.LoyaltyPointValue ?? store.LoyaltyPointValue, r.LoyaltyMinRedeemPoints ?? store.LoyaltyMinRedeemPoints);
            try { LoyaltyRules.Validate(loyalty); }
            catch (ArgumentException exception) { return Results.BadRequest(exception.Message); }
            (store.LoyaltyEnabled, store.LoyaltyPointsPerUnit, store.LoyaltyPointValue, store.LoyaltyMinRedeemPoints) =
                (loyalty.Enabled, loyalty.PointsPerUnit, loyalty.PointValue, loyalty.MinRedeemPoints);
            await db.SaveWithVersionsAsync(ct, store);
            return Results.Ok(SyncService.Map(store));
        });

        // ---------- printed documents (docs/07 §4.3) ----------
        // Terms, payment terms and bank details for the paper the back office prints. Written by
        // the Admin only; read by whoever prints, since a manager's invoice carries the terms too.
        // NOT on StoreDto: nothing on a till reads them, so they never replicate. The PUT saves
        // with a plain SaveChanges — a version bump here would make every till re-pull a Store row
        // whose synced fields did not change (StoreDocumentsTests pins Store.Version).

        group.MapGet("/store/documents", async (PosDbContext db, IRegionalSettingsProvider regional, CancellationToken ct) =>
        {
            var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => !s.IsDeleted, ct);
            if (store is null) return Results.NotFound();

            // The screen reads what is left of the allowance before the admin types, so a save's
            // cost is known in advance rather than discovered on a refusal.
            var zone = StoreDocumentHistory.Zone(regional);
            var year = StoreDocumentTexts.CompanyYear(DateTime.UtcNow, zone);
            var used = await StoreDocumentHistory.UsedAsync(db, year, ct);
            var allowances = StoreDocumentTexts.Allowances(
                store.DocumentTextChangesPerYear, year, StoreDocumentTexts.YearResetsUtc(year, zone), used,
                StoreDocumentTexts.KindsOf(store.ContractWordings));
            return Results.Ok(StoreDocumentTexts.ToDto(store, allowances));
        });

        // The yearly wording allowance (owner, 2026-09-24). Each kind is counted on its own, in the
        // company's calendar year; one save is one change however many languages it rewrote; a save
        // that changes nothing consumes nothing; and every allowed change keeps its predecessor, so
        // the wording a customer signed under stays readable. The limit is Store's own setting,
        // written from this same PUT — see Store.DocumentTextChangesPerYear for why it is the
        // owner's discipline rather than a control against him.
        group.MapPut("/store/documents", async (
            StoreDocumentsDto r, PosDbContext db, IFileStore files, IRegionalSettingsProvider regional, HttpContext http, CancellationToken ct) =>
        {
            // The wordings on the row stand when the request sends no list (an older screen), so
            // their texts are read against them; the row is read again under the write scope below.
            var storedWordings = await db.Stores.AsNoTracking().Where(s => !s.IsDeleted)
                .Select(s => s.ContractWordings).FirstOrDefaultAsync(ct);
            if (StoreDocumentTexts.TryNormalize(r, out var values, storedWordings) is string problem) return Results.BadRequest(problem);
            // The logo rides on the same PUT: the screen sends the one it shows, so its link keeps
            // it, a data URL replaces it and nothing clears it.
            if (StoreLogo.TryReadChange(r.LogoUrl, StoreLogo.StoreEndpoint, StoreLogo.MaxBytes, out var logo) is string logoProblem)
                return Results.BadRequest(logoProblem);
            // Written before the row and named by its content: a save refused below leaves a file
            // nothing points at, never a row pointing at no file.
            if (logo.File is not null) await files.WriteAsync(logo.File.Path, logo.File.Content, ct);

            // Count and write under one scope: two admins saving at once must not both read "2 used"
            // and both write a third change. BeginWriteScope, not BeginWrite — SubscriptionWriteFilter
            // may already hold a transaction across this handler.
            await using var tx = await db.BeginWriteScopeAsync(ct);

            var store = await db.Stores.FirstOrDefaultAsync(s => !s.IsDeleted, ct);
            if (store is null) return Results.NotFound();

            var zone = StoreDocumentHistory.Zone(regional);
            var year = StoreDocumentTexts.CompanyYear(DateTime.UtcNow, zone);
            // A wording a contract prints cannot be taken off the list (PinSay #302): the contract
            // would print the standard wording in its place, which is not what its customer signed.
            var removed = StoreDocumentTexts.RemovedWordings(store.ContractWordings, values);
            if (removed.Count > 0)
            {
                var inUse = await db.RentalContracts.AsNoTracking()
                    .Where(c => c.WordingKey != null && removed.Contains(c.WordingKey))
                    .Select(c => c.WordingKey!).Distinct().ToListAsync(ct);
                if (inUse.Count > 0)
                {
                    var labels = StoreDocumentTexts.WordingLabels(store.ContractWordings);
                    var key = inUse[0];
                    var count = await db.RentalContracts.CountAsync(c => c.WordingKey == key, ct);
                    return Results.BadRequest(
                        $"The contract wording \"{labels.GetValueOrDefault(key, key)}\" is printed on {count} contract{(count == 1 ? "" : "s")}, so it cannot be removed. Rename it or rewrite its text instead.");
                }
            }

            var used = await StoreDocumentHistory.UsedAsync(db, year, ct);
            // The limit judged against is the stored one, not the one this same request may be
            // raising: a save that rewrote the contract and lifted the limit in one go could never
            // be refused. Raising it takes effect from the next save.
            var decisions = StoreDocumentTexts.Decide(
                store.DocumentTexts, StoreDocumentTexts.TextsAfter(store, values),
                new StoreDocumentTexts.ChangeAllowance(store.DocumentTextChangesPerYear, year, used),
                StoreDocumentTexts.WordingLabels(values.Wordings, store.ContractWordings));

            var refused = decisions.FirstOrDefault(d => d.Verdict == StoreDocumentTexts.ChangeVerdict.Refused);
            if (refused is not null) return Results.BadRequest(refused.Refusal);

            StoreDocumentHistory.Record(db, decisions, year, UserName(http));
            StoreDocumentTexts.Apply(store, values);
            // Not a wording: it spends none of the yearly allowance and keeps no history row.
            var previousLogo = store.LogoPath;
            if (logo.Kind != StoreLogo.ChangeKind.Keep) store.LogoPath = logo.File?.Path;
            // Plain SaveChanges: none of this is on StoreDto, and a version bump would make every
            // till re-pull a Store row whose synced fields did not change (StoreDocumentsTests).
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            if (previousLogo != store.LogoPath) await LogoFiles.ReleaseAsync(db, files, previousLogo, ct);

            var allowances = StoreDocumentTexts.Allowances(
                store.DocumentTextChangesPerYear, year, StoreDocumentTexts.YearResetsUtc(year, zone),
                await StoreDocumentHistory.UsedAsync(db, year, ct),
                StoreDocumentTexts.KindsOf(store.ContractWordings));
            return Results.Ok(StoreDocumentTexts.ToDto(store, allowances));
        });

        // The company logo itself, for the document header and the back office. Signed in, never
        // public: the files folder is reached only through the application (owner, 2026-10-04).
        group.MapGet("/store/logo", async (PosDbContext db, IFileStore files, HttpContext http, CancellationToken ct) =>
        {
            var path = await db.Stores.AsNoTracking().Where(s => !s.IsDeleted).Select(s => s.LogoPath).FirstOrDefaultAsync(ct);
            return await FileResults.LogoAsync(files, path, http, ct);
        });

        // Superseded wordings, newest first — what makes the copy a customer signed under readable
        // years later. Each row carries the text as it stood BEFORE that change; the wording in
        // force today is on the Store row itself.
        // Wording to start from, so the printed documents can be tried before the owner's own
        // paragraphs exist. Read-only: it is handed to the screen, which puts it in the form for
        // the admin to read and save himself. Nothing seeds it — a clause this code invented must
        // never reach a customer's paper without a person having looked at it, which is also why
        // every sample carries its marker line in its own text.
        group.MapGet("/store/documents/sample", () => Results.Ok(SampleDocumentTexts.Dto()));

        group.MapGet("/store/documents/history", async (
            string? kind, int? take, PosDbContext db, CancellationToken ct) =>
        {
            // A named contract wording keeps its history under its key, removed or not.
            if (kind is not null && !DocumentTextKinds.All.Contains(kind) && !DocumentTextKinds.IsContractWording(kind))
                return Results.BadRequest($"'{kind}' is not a printed document kind.");
            return Results.Ok(await StoreDocumentHistory.ReadAsync(db, kind, Math.Clamp(take ?? 100, 1, 500), ct));
        });

        // ---------- categories ----------

        group.MapGet("/categories", async (PosDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Categories.AsNoTracking().Where(c => !c.IsDeleted)
                .OrderBy(c => c.SortOrder).ToListAsync(ct)));

        group.MapPost("/categories", async (UpsertCategoryRequest r, PosDbContext db, CancellationToken ct) =>
        {
            if (CategoryColorProblem(r.Color) is string colorProblem) return Results.BadRequest(colorProblem);
            var entity = new Category
            {
                NameAr = r.NameAr, NameFr = r.NameFr, NameEn = r.NameEn,
                Color = r.Color, SortOrder = r.SortOrder
            };
            db.Categories.Add(entity);
            await db.SaveWithVersionsAsync(ct, entity);
            return Results.Created($"/api/catalog/categories/{entity.Id}", entity);
        });

        group.MapPut("/categories/{id:guid}", async (Guid id, UpsertCategoryRequest r, PosDbContext db, CancellationToken ct) =>
        {
            var entity = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (entity is null) return Results.NotFound();
            if (CategoryColorProblem(r.Color) is string colorProblem) return Results.BadRequest(colorProblem);
            entity.NameAr = r.NameAr; entity.NameFr = r.NameFr; entity.NameEn = r.NameEn;
            entity.Color = r.Color; entity.SortOrder = r.SortOrder;
            await db.SaveWithVersionsAsync(ct, entity);
            return Results.Ok(entity);
        });

        group.MapDelete("/categories/{id:guid}", async (Guid id, PosDbContext db, CancellationToken ct) =>
            await SoftDeleteAsync(db.Categories, id, db, ct));

        // ---------- products ----------

        group.MapGet("/products", async (string? q, int? skip, int? take, PosDbContext db, CancellationToken ct) =>
        {
            var query = db.Products.AsNoTracking().Where(p => !p.IsDeleted);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                query = query.Where(p =>
                    p.Sku.Contains(term) || (p.Barcode != null && p.Barcode.Contains(term)) ||
                    p.NameAr.Contains(term) || p.NameFr.Contains(term) || p.NameEn.Contains(term));
            }
            // Paged when asked — the back office walks the pages to build its one in-memory
            // copy, so no catalogue is too long for it — and capped when not, for the callers
            // that predate paging. The order and the limits live in ProductPaging, with why.
            return Results.Ok(await ProductPaging.Page(query, skip, take).ToListAsync(ct));
        });

        // The products list a page at a time (owner, 2026-11-17: "paginate in backend, not frontend").
        // Built in memory here, not in SQL: the search is the till's Arabic/Latin fold and the order the
        // reader's language with numbers as numbers, neither of which SQL does, and SQLite cannot order a
        // decimal price. Only the page leaves the server. No tax filter (owner, 2026-11-18: the rate is
        // applied when the invoice is made). The whole list above stays for the catalogue copy other screens use.
        group.MapGet("/products/page", async (
            string? q, string? status, string? stock, string? barcode, string? sort, string? dir,
            string? lang, Guid? id, int? skip, int? take, PosDbContext db, CancellationToken ct) =>
        {
            var all = await db.Products.AsNoTracking().Where(p => !p.IsDeleted).ToListAsync(ct);
            var needle = ListText.Fold(q);
            IEnumerable<Product> rows = all;
            if (id is Guid one) rows = rows.Where(p => p.Id == one);
            if (needle.Length > 0)
                rows = rows.Where(p => ListText.Fold($"{p.Sku} {p.Barcode} {p.NameFr} {p.NameAr} {p.NameEn}").Contains(needle, StringComparison.Ordinal));
            rows = status switch
            {
                "active" => rows.Where(p => p.IsActive),
                "inactive" => rows.Where(p => !p.IsActive),
                _ => rows,
            };
            rows = stock switch
            {
                "tracked" => rows.Where(p => p.TrackStock),
                "untracked" => rows.Where(p => !p.TrackStock),
                _ => rows,
            };
            rows = barcode switch
            {
                "yes" => rows.Where(p => !string.IsNullOrWhiteSpace(p.Barcode)),
                "no" => rows.Where(p => string.IsNullOrWhiteSpace(p.Barcode)),
                _ => rows,
            };

            var sign = dir == "desc" ? -1 : 1;
            // The screens' localizedName: the reader's language, then French, English, Arabic.
            string Name(Product p) => new[] { lang == "ar" ? p.NameAr : lang == "en" ? p.NameEn : p.NameFr, p.NameFr, p.NameEn, p.NameAr }
                .FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? string.Empty;
            var list = rows.ToList();
            // Unsorted, the catalogue's own order (ProductPaging): French name, Arabic name, id.
            Comparison<Product> order = sort switch
            {
                "sku" => (a, b) => sign * ListText.Compare(a.Sku, b.Sku, lang),
                "name" => (a, b) => sign * ListText.Compare(Name(a), Name(b), lang),
                "price" => (a, b) => sign * a.Price.CompareTo(b.Price),
                _ => (a, b) =>
                {
                    var byFr = string.CompareOrdinal(a.NameFr, b.NameFr);
                    return byFr != 0 ? byFr : string.CompareOrdinal(a.NameAr, b.NameAr);
                },
            };
            list.Sort((a, b) => order(a, b) is var c && c != 0 ? c : a.Id.CompareTo(b.Id));
            return Results.Ok(ListPaging.Of(list, skip, take));
        });

        group.MapPost("/products", async (UpsertProductRequest r, PosDbContext db, IRegionalSettingsProvider regional, CancellationToken ct) =>
        {
            if (!regional.Get().TaxRates.Contains(r.TaxRate))
                return Results.BadRequest(TaxRateError(regional.Get()));
            if (await db.Products.AnyAsync(p => p.Sku == r.Sku, ct))
                return Results.Conflict($"SKU '{r.Sku}' already exists.");

            var entity = new Product();
            Apply(entity, r);
            db.Products.Add(entity);
            await db.SaveWithVersionsAsync(ct, entity);
            return Results.Created($"/api/catalog/products/{entity.Id}", entity);
        });

        group.MapPut("/products/{id:guid}", async (Guid id, UpsertProductRequest r, PosDbContext db, IRegionalSettingsProvider regional,
            ClaimsPrincipal caller, [FromServices] CostPriceRevaluationPosting revaluation, CancellationToken ct) =>
        {
            var entity = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (entity is null) return Results.NotFound();
            // The unique index would refuse it anyway, but as a 500 the screen can only call a
            // failure; this is the same sentence the create route gives.
            if (await db.Products.AnyAsync(p => p.Id != id && p.Sku == r.Sku, ct))
                return Results.Conflict($"SKU '{r.Sku}' already exists.");
            // A rate the operator has since removed may stay, so a price edit is never blocked.
            if (r.TaxRate != entity.TaxRate && !regional.Get().TaxRates.Contains(r.TaxRate))
                return Results.BadRequest(TaxRateError(regional.Get()));
            // A cost price typed here revalues the stock on hand when the accountant chose it (B-F01, 2026-11-09): read
            // as it stands under the version lock, saved and revalued in one transaction. Never a reason to refuse the save.
            await using var tx = await db.BeginWriteScopeAsync(ct);
            var costBefore = await revaluation.LockedCostPriceAsync(id, ct);
            Apply(entity, r);
            await db.SaveWithVersionsAsync(ct, entity);
            if (await revaluation.ProductCardAsync(entity, costBefore, MovementAuthor.Of(caller).UserName, ct))
                await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(entity);
        });

        group.MapDelete("/products/{id:guid}", async (Guid id, PosDbContext db, CancellationToken ct) =>
            await SoftDeleteAsync(db.Products, id, db, ct));

        // ---------- stock (per branch) ----------

        // Every branch keeps its own reorder level (see ProductStock), so "low" is relative to
        // that branch, not a company-wide number — a warehouse and a kiosk legitimately have
        // different thresholds for the same SKU.
        group.MapGet("/products/low-stock", async (Guid? branchId, ClaimsPrincipal caller, PosDbContext db, CancellationToken ct) =>
        {
            var query = db.ProductStocks.AsNoTracking()
                .Where(s => s.StockQty <= s.ReorderLevel);
            if (branchId is not null) query = query.Where(s => s.BranchId == branchId);
            // A non-admin's branchId is already their own (CallersBranch); the permission is about
            // availability only and never widens a report.
            if (await BranchScope.ForStockAsync(caller, db, ct) is Guid own) query = query.Where(s => s.BranchId == own);

            // SQLite stores decimal as TEXT, so ordering by a decimal expression (the shortfall
            // below) can't run server-side — materialize first, then sort in memory, same as
            // every other decimal aggregate in this codebase (see PosDbContext's precision note).
            var rows = await query
                .Join(db.Products.AsNoTracking().Where(p => p.TrackStock && p.IsActive && !p.IsDeleted),
                    s => s.ProductId, p => p.Id, (s, p) => new { s, p })
                .Join(db.Branches.AsNoTracking(), x => x.s.BranchId, b => b.Id, (x, b) => new LowStockDto(
                    x.p.Id, x.p.Sku, x.p.NameFr, x.s.BranchId, b.NameFr, x.s.StockQty, x.s.ReorderLevel))
                .ToListAsync(ct);
            return Results.Ok(rows.OrderBy(x => x.StockQty - x.ReorderLevel).ToList());
        }).WithMetadata(new BranchScopedAttribute("a non-admin's shortfalls are their own branch's (CallersBranch sets branchId)"));
        FinancePageRoutes.MapLowStockPage(group);

        group.MapGet("/products/{id:guid}/stock", async (Guid id, ClaimsPrincipal caller, PosDbContext db, CancellationToken ct) =>
        {
            // These rows are adjusted in place, so a non-admin gets their own branch's only; the
            // other branches' availability is a separate, read-only lookup (/stock/availability).
            var stocks = db.ProductStocks.AsNoTracking().Where(s => s.ProductId == id);
            if (!BranchAssignment.IsCompanyScoped(caller))
            {
                var own = await BranchAssignment.ForCallerAsync(caller, db, ct) ?? Guid.Empty;
                stocks = stocks.Where(s => s.BranchId == own);
            }
            var rows = await stocks
                .Join(db.Branches.AsNoTracking(), s => s.BranchId, b => b.Id, (s, b) => new ProductStockDto(
                    s.ProductId, s.BranchId, b.NameFr, s.StockQty, s.ReorderLevel))
                .ToListAsync(ct);
            return Results.Ok(rows);
        }).WithMetadata(new BranchScopedAttribute("a non-admin's stock panel is their own branch's rows, which they adjust in place; other branches are the read-only availability lookup"));

        // Stock is only ever adjusted on the server, so terminals cannot fight over it.
        // Never blocks: a manual correction/receiving entry is trusted the same way a sale is.
        group.MapPost("/products/{id:guid}/stock", async (
            Guid id, StockAdjustRequest r, ClaimsPrincipal caller, PosDbContext db, LedgerPoster ledger, ILoggerFactory logs,
            CancellationToken ct) =>
        {
            var author = MovementAuthor.Of(caller);
            var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (product is null) return Results.NotFound();
            if (!await db.Branches.AnyAsync(b => b.Id == r.BranchId, ct)) return Results.NotFound("Branch not found.");

            // Here the caller's words are the whole label, so they are checked rather than cut.
            // Unchecked, an ordinary stocktake comment was a 500 on MySQL and nothing at all on
            // SQLite — so dev, CI and every test passed while production threw.
            var reason = (r.Reason ?? string.Empty).Trim();
            if (reason.Length > StockMovement.ReasonMaxLength)
                return Results.BadRequest(
                    $"A stock reason is at most {StockMovement.ReasonMaxLength} characters; this one is {reason.Length}.");

            // One transaction for the figure, its movement, its ledger entry and the version the
            // tills pull by; the row is read under the version lock (StockRows) so a sale pushed
            // at the same moment is not written over.
            await using var tx = await db.BeginWriteScopeAsync(ct);
            var stock = await new StockRows(db).GetAsync(id, r.BranchId, ct);

            if (r.QtyDelta != 0)
            {
                stock.StockQty += r.QtyDelta;
                var documentId = Guid.NewGuid();
                var movement = new StockMovement
                {
                    ProductId = id,
                    BranchId = r.BranchId,
                    QtyDelta = r.QtyDelta,
                    BalanceAfter = stock.StockQty,
                    DocumentId = documentId,
                    Reason = reason.Length == 0 ? "adjustment" : reason,
                    Kind = StockMovementKind.Adjustment,
                    UnitCost = product.CostPrice,
                    UserId = author.UserId,
                    UserName = author.UserName,
                    MovedQty = Math.Abs(r.QtyDelta)
                };
                db.StockMovements.Add(movement);

                // A stocktake correction is a real gain or loss, not a bookkeeping tidy-up.
                // Three pipes missing is three pipes' worth of loss — at the cost price, or, once sales
                // are costed at the weighted average (C-01), at the average: goods missing leave at it,
                // goods found come in at it, exactly as the shadow values them.
                var value = r.QtyDelta * product.CostPrice;
                var costs = await new LiveSaleCosting(db).MovementCostsOrFallbackAsync(
                    [new PlannedMovement(movement, product.CostPrice)], logs.CreateLogger("StockAdjustment"), ct);
                if (costs?.GetValueOrDefault(movement.Id) is { } cost)
                {
                    movement.UnitCost = cost.UnitCost;
                    movement.CostBasis = cost.Basis;
                    value = Math.Sign(r.QtyDelta) * cost.Value;
                }
                // REN-12 (Phase M7a): a hire product held by the fixed-asset register leaves its pool at its net book value
                // (missing), or joins it at the count's value (found); off (the default), today's entry against 310.
                var assets = new FixedAssetBook(db);
                if (await assets.HoldsAsync(id, ct))
                {
                    var counted = new FixedAssetEvent(id, r.BranchId, Math.Abs(r.QtyDelta), product.Sku, documentId, null,
                        reason.Length == 0 ? "adjustment" : reason, author.UserName);
                    if (await (r.QtyDelta < 0 ? assets.DisposeAsync(counted, ct) : assets.FoundAsync(counted, value, ct)) is { } draft)
                        await ledger.PostAsync(draft, ct);
                }
                else
                    await ledger.PostAsync(StockPosting.Adjustment(
                        documentId, r.BranchId, value, product.Sku, reason), ct);
            }
            if (r.ReorderLevel is not null) stock.ReorderLevel = r.ReorderLevel.Value;

            // The product's own Version is what a terminal's pull actually watches, so every
            // branch's stock change has to bump it — including for branches other than the
            // one that changed. That terminal re-fetches a row whose figures for its own
            // branch didn't move, which is wasted bandwidth but never a correctness problem;
            // splitting sync per branch would be a much larger change for no observed need
            // at today's single-shop/small-chain scale.
            await db.TouchAsync(product, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(stock);
        });

        // ---------- customers ----------
        // A customer is a person or a company; a company acts through its authorised
        // representative (المخوَّل). The back office reads a DTO rather than the row so the current
        // representative rides along; the till's CustomerDto is unchanged and carries none of it.

        // The customers list a page at a time (owner, 2026-11-17: "paginate in backend, not frontend"):
        // the search and the filters run here, over every customer, and only the page is sent. The
        // whole list above stays for the till and the pickers, which work from it offline.
        cash.MapGet("/customers/page", async (
            string? q, string? kind, string? balance, string? points, string? missing, Guid? id, int? skip, int? take,
            PosDbContext db, CustomerRepresentatives representatives, IRegionalSettingsProvider regional, CancellationToken ct) =>
        {
            var rules = regional.Get().Phone;
            var query = db.Customers.AsNoTracking().Where(c => !c.IsDeleted);
            if (id is Guid one) query = query.Where(c => c.Id == one);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                // A phone is found however it is typed: 06.58.41, 0658417730 or 658 41 against +213658417730.
                var digits = new string(term.Where(char.IsAsciiDigit).ToArray());
                var national = rules.IsSet && rules.TrunkPrefix.Length > 0 && digits.StartsWith(rules.TrunkPrefix, StringComparison.Ordinal)
                    ? rules.CallingCode + digits[rules.TrunkPrefix.Length..]
                    : null;
                var byPhone = digits.Length >= 3;
                query = query.Where(c =>
                    c.Code.Contains(term) || c.FullName.Contains(term) ||
                    (c.Phone != null && c.Phone.Contains(term)) || (c.Email != null && c.Email.Contains(term)) ||
                    (byPhone && c.Phone != null && (c.Phone.Contains(digits) || (national != null && c.Phone.Contains(national)))) ||
                    db.CustomerRepresentatives.Any(r => r.CustomerId == c.Id && r.ToUtc == null && r.FullName.Contains(term)));
            }
            if (CustomerRules.TryParseKind(kind, out var wantedKind) && !string.IsNullOrWhiteSpace(kind))
                query = query.Where(c => c.Kind == wantedKind);
            query = balance switch
            {
                "owes" => query.Where(c => c.Balance > 0),
                "overLimit" => query.Where(c => c.CreditLimit > 0 && c.Balance > c.CreditLimit),
                "inCredit" => query.Where(c => c.Balance < 0),
                "settled" => query.Where(c => c.Balance == 0),
                _ => query,
            };
            query = points switch
            {
                "has" => query.Where(c => c.LoyaltyPoints > 0),
                "none" => query.Where(c => c.LoyaltyPoints <= 0),
                _ => query,
            };
            switch (missing)
            {
                case "phone":
                    query = query.Where(c => c.Phone == null || c.Phone == "");
                    break;
                case "email":
                    query = query.Where(c => c.Email == null || c.Email == "");
                    break;
                case "representative":
                    query = query.Where(c => c.Kind == CustomerKind.Company
                        && !db.CustomerRepresentatives.Any(r => r.CustomerId == c.Id && r.ToUtc == null));
                    break;
                case "phoneToFix":
                    // Read in memory: whether a phone is a mobile is the profile's pattern, not SQL.
                    var phones = await db.Customers.AsNoTracking().Where(c => !c.IsDeleted && c.Phone != null)
                        .Select(c => new { c.Id, c.Phone }).ToListAsync(ct);
                    var toFix = phones.Where(p => PhoneNumbers.NeedsFixing(p.Phone, rules)).Select(p => p.Id).ToList();
                    query = query.Where(c => toFix.Contains(c.Id));
                    break;
            }

            var page = await query.OrderBy(c => c.FullName).ThenBy(c => c.Id).ToPageAsync(skip, take, async rows =>
            {
                var current = await representatives.CurrentByCustomerAsync(rows.Select(c => c.Id).ToList(), ct);
                return (IReadOnlyList<AdminCustomerDto>)rows.Select(c => CustomerRepresentatives.ToDto(c, current.GetValueOrDefault(c.Id))).ToList();
            }, ct);
            return Results.Ok(page);
        });

        cash.MapGet("/customers", async (PosDbContext db, CustomerRepresentatives representatives, CancellationToken ct) =>
        {
            var customers = await db.Customers.AsNoTracking().Where(c => !c.IsDeleted)
                .OrderBy(c => c.FullName).ToListAsync(ct);
            var current = await representatives.CurrentByCustomerAsync(customers.Select(c => c.Id).ToList(), ct);
            return Results.Ok(customers.Select(c => CustomerRepresentatives.ToDto(c, current.GetValueOrDefault(c.Id))).ToList());
        });

        createCustomers.MapPost("/customers", async (
            UpsertCustomerRequest r, PosDbContext db, CustomerRepresentatives representatives, HttpContext http,
            IRegionalSettingsProvider regional, TimeProvider time, CustomerOpeningBalances openings, CancellationToken ct) =>
        {
            if (!CustomerRules.TryParseKind(r.Kind, out var kind)) return Results.BadRequest(KindParseError);
            // Kind rules first, store rules second: "say what it is" comes before "fill in the email".
            if (KindRefusal(CustomerRules.KindProblem(
                    isNew: true, CustomerKind.Unspecified, kind, HasRepresentative(r), hasCurrentRepresentative: false)) is IResult refused)
                return refused;
            if (TaxIdRefusal(CustomerRules.TaxIdProblem(
                    isNew: true, CustomerKind.Unspecified, storedTaxId: null, kind, r.TaxId)) is IResult taxRefused)
                return taxRefused;
            if (NameRefusal(r, kind) is IResult nameRefused) return nameRefused;
            if (IdDocumentProblem(r) is string idProblem) return Results.BadRequest(idProblem);
            (r, var phoneRefused) = ReadPhones(r, regional.Get().Phone, storedPhone: null, readRepresentative: true);
            if (phoneRefused is not null) return phoneRefused;
            var validation = await ValidateCustomerAsync(r, kind, db, ct);
            if (validation is not null) return Results.BadRequest(validation);
            if (await RegisteredAtAsync(r, http, db, ct) is not Guid registeredAt)
                return Results.BadRequest(BranchLock.NoBranchMessage);
            // The balance the customer starts with (owner, 2026-10-10): whoever registers the
            // customer may enter it, now or never — an amount of zero is "nothing owed", not set.
            var opening = r.OpeningBalance is { Amount: not 0m } given ? given : null;
            if (opening is not null && openings.Problem(opening) is string openingProblem)
                return Results.BadRequest(openingProblem);

            var entity = new Customer();
            if (ApplyFiscalIds(entity, r, kind, regional.Get()) is string fiscalProblem) return Results.BadRequest(fiscalProblem);
            Apply(entity, r, kind);

            // One unit: the company and the person who signs for it. A scope, never
            // BeginWriteAsync — this group already runs inside the subscription filter's
            // transaction — and the customer still goes through SaveWithVersionsAsync, which
            // joins it; the representative row is not a SyncEntity and is simply saved with it.
            await using var tx = await db.BeginWriteScopeAsync(ct);
            // The code is the server's, drawn here at save time and never from the body (owner,
            // 2026-10-05): Cust-/Comp- + the branch's code + the company's YYMM + the next number of
            // that branch and month (CodeSeries). The filter's SyncCounters lock is already held,
            // so concurrent creates draw one after another.
            var createdAt = time.GetUtcNow();
            var zone = StoreDocumentHistory.Zone(regional);
            entity.Code = await CodeSeriesDraw.CustomerAsync(db, kind, registeredAt, createdAt, zone, ct);
            db.Customers.Add(entity);
            var appointed = kind == CustomerKind.Company && HasRepresentative(r)
                ? representatives.Appoint(entity, r.Representative!, UserName(http))
                : null;
            // Posted in the same unit as the customer, at the branch it is registered at: a customer
            // can never exist with half an opening balance.
            if (opening is not null) await openings.ApplyAsync(entity, opening, registeredAt, UserName(http), ct);
            // The backstop, as for a Login ID: a code written meanwhile outside the lock trips the
            // unique index, and the code is drawn again rather than the clerk shown a 409 or a 500.
            // EF Core rolls a failed SaveChanges back to its own savepoint, so the transaction it ran
            // in stays usable on both providers (SQL Server's XACT_ABORT is left off here).
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await db.SaveWithVersionsAsync(ct, entity);
                    break;
                }
                catch (DbUpdateException) when (attempt < 4)
                {
                    if (!await CodeSeriesDraw.CustomerTakenAsync(db, entity.Code, ct)) throw;
                    entity.Code = await CodeSeriesDraw.CustomerAsync(db, kind, registeredAt, createdAt, zone, ct);
                }
            }
            await tx.CommitAsync(ct);

            return Results.Created($"/api/catalog/customers/{entity.Id}", CustomerRepresentatives.ToDto(entity, appointed));
        });

        group.MapPut("/customers/{id:guid}", async (
            Guid id, UpsertCustomerRequest r, PosDbContext db, CustomerRepresentatives representatives, HttpContext http,
            IRegionalSettingsProvider regional, CancellationToken ct) =>
        {
            if (!CustomerRules.TryParseKind(r.Kind, out var kind)) return Results.BadRequest(KindParseError);

            var entity = await db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (entity is null) return Results.NotFound();
            var current = await representatives.CurrentAsync(id, ct);

            if (KindRefusal(CustomerRules.KindProblem(
                    isNew: false, entity.Kind, kind, HasRepresentative(r), current is not null)) is IResult refused)
                return refused;
            // Read against the row as stored, before Apply: a person's number from before the
            // rule may stay as it is, and only a new or changed one is refused.
            if (TaxIdRefusal(CustomerRules.TaxIdProblem(
                    isNew: false, entity.Kind, entity.TaxId, kind, r.TaxId)) is IResult taxRefused)
                return taxRefused;
            if (NameRefusal(r, kind) is IResult nameRefused) return nameRefused;
            if (IdDocumentProblem(r) is string idProblem) return Results.BadRequest(idProblem);
            // The representative in the body is written only when this save appoints one (below).
            var appointing = kind == CustomerKind.Company && !(entity.Kind == CustomerKind.Company && current is not null);
            (r, var phoneRefused) = ReadPhones(r, regional.Get().Phone, entity.Phone, readRepresentative: appointing);
            if (phoneRefused is not null) return phoneRefused;
            var validation = await ValidateCustomerAsync(r, kind, db, ct);
            if (validation is not null) return Results.BadRequest(validation);
            // No code check: an edit never changes the code (owner, 2026-10-05). Apply leaves it as
            // stored — a drawn Cust-/Comp- code, or a legacy one such as C001 — whatever the body
            // sends and whatever the kind becomes, because printed statements and invoices carry it.

            var stored = entity.Kind;
            if (ApplyFiscalIds(entity, r, kind, regional.Get()) is string fiscalProblem) return Results.BadRequest(fiscalProblem);
            Apply(entity, r, kind);

            await using var tx = await db.BeginWriteScopeAsync(ct);
            var shown = current;
            if (kind == CustomerKind.Company && !(stored == CustomerKind.Company && current is not null))
            {
                // The migrated row's case, and the company-turned-person-turned-company one: the
                // one decision the feature is about is taken when the kind is set, in this save.
                // Any open row a hand edit left behind is ended first, so there is one current.
                if (current is not null) await representatives.EndCurrentAsync(id, DateTime.UtcNow, ct);
                shown = representatives.Appoint(entity, r.Representative!, UserName(http));
            }
            else if (kind != CustomerKind.Company && stored == CustomerKind.Company)
            {
                // Nobody signs for a person. The history stays; it is simply not shown.
                await representatives.EndCurrentAsync(id, DateTime.UtcNow, ct);
                shown = null;
            }
            await db.SaveWithVersionsAsync(ct, entity);
            await tx.CommitAsync(ct);

            return Results.Ok(CustomerRepresentatives.ToDto(entity, shown));
        });

        group.MapDelete("/customers/{id:guid}", async (Guid id, PosDbContext db, CancellationToken ct) =>
            await SoftDeleteAsync(db.Customers, id, db, ct));

        // ---------- opening balances (owner, 2026-10-10, go-live) ----------
        // Set once: at registration (above), or here afterwards when it was left empty, by whoever
        // holds customers.opening_balance. Never edited: a wrong figure is corrected below, with a
        // reason, so the statement keeps both.

        group.MapPost("/customers/{id:guid}/opening-balance", async (
            Guid id, CustomerOpeningBalanceRequest r, PosDbContext db, HttpContext http, CustomerOpeningBalances openings,
            CustomerRepresentatives representatives, CancellationToken ct) =>
        {
            if (openings.Problem(r) is string problem) return Results.BadRequest(problem);
            if (await CallerBranchAsync(http, db, ct) is not Guid branchId) return Results.BadRequest(BranchLock.NoBranchMessage);

            await using var tx = await db.BeginWriteScopeAsync(ct);
            // Under the version lock: a till may be pushing a credit sale onto this same balance.
            var customer = await db.LockedAsync<Customer>(id, ct);
            if (customer is null || customer.IsDeleted) return Results.NotFound("Unknown customer.");
            // The route already asked for customers.opening_balance; what is left is "once".
            if (OpeningBalanceRules.MaySet(customer.OpeningBalanceSetAtUtc is not null, atRegistration: false, holdsPermission: true)
                == OpeningRefusal.AlreadySet)
                return Results.Conflict(OpeningAlreadySetMessage);

            await openings.ApplyAsync(customer, r, branchId, UserName(http), ct);
            await db.SaveWithVersionsAsync(ct, customer);
            await tx.CommitAsync(ct);
            return Results.Ok(CustomerRepresentatives.ToDto(customer, await representatives.CurrentAsync(id, ct)));
        });

        group.MapPost("/customers/{id:guid}/opening-corrections", async (
            Guid id, OpeningCorrectionRequest r, PosDbContext db, HttpContext http, CustomerOpeningBalances openings,
            CancellationToken ct) =>
        {
            if (OpeningBalanceRules.ReasonProblem(r.Reason) is string reasonProblem) return Results.BadRequest(reasonProblem);
            if (openings.Problem(new CustomerOpeningBalanceRequest(r.Amount, r.Direction, r.On)) is string problem)
                return Results.BadRequest(problem);
            if (await CallerBranchAsync(http, db, ct) is not Guid branchId) return Results.BadRequest(BranchLock.NoBranchMessage);

            await using var tx = await db.BeginWriteScopeAsync(ct);
            var customer = await db.LockedAsync<Customer>(id, ct);
            if (customer is null || customer.IsDeleted) return Results.NotFound("Unknown customer.");
            // A correction corrects something: with no opening balance there is nothing to put
            // right, and the empty one is set once instead.
            if (customer.OpeningBalanceSetAtUtc is null)
                return Results.Conflict("This customer has no opening balance yet. Set it instead of correcting it.");

            var correction = await openings.CorrectAsync(customer, r, branchId, UserName(http), ct);
            await db.SaveWithVersionsAsync(ct, customer);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/catalog/customers/{id}/opening-corrections/{correction.Id}",
                CustomerOpeningBalances.ToDto(correction));
        });

        group.MapGet("/customers/{id:guid}/opening-corrections", async (Guid id, PosDbContext db, CancellationToken ct) =>
        {
            var rows = await db.CustomerOpeningCorrections.AsNoTracking()
                .Where(c => c.CustomerId == id)
                .OrderBy(c => c.CreatedAtUtc)
                .ToListAsync(ct);
            return Results.Ok(rows.Select(CustomerOpeningBalances.ToDto).ToList());
        });
        SalesPageRoutes.MapCustomerOpeningCorrectionsPage(group);

        // The representative's history. GET answers for a tombstoned company too (history is
        // history); POST is the one way to change the representative, and there is no PUT and
        // no DELETE by design: a row is never edited or removed, only ended by the next one.
        group.MapGet("/customers/{id:guid}/representatives", async (
            Guid id, CustomerRepresentatives representatives, CancellationToken ct) =>
        {
            var result = await representatives.HistoryAsync(id, ct);
            return result.Ok ? Results.Ok(result.Value) : Problem(result);
        });

        group.MapPost("/customers/{id:guid}/representatives", async (
            Guid id, ChangeRepresentativeRequest r, CustomerRepresentatives representatives, HttpContext http,
            IRegionalSettingsProvider regional, CancellationToken ct) =>
        {
            // The new representative's phone is a mobile in international form, as the customer's is.
            var phone = PhoneNumbers.Normalize(r.Phone, regional.Get().Phone);
            if (!phone.IsMobile) return PhoneRefusal("representativePhone");
            var result = await representatives.ChangeAsync(id, r with { Phone = phone.Value }, UserName(http), ct);
            return result.Ok ? Results.Ok(result.Value) : Problem(result);
        });

        // ---------- the copy of the identity document ----------
        // Attached when the customer is added, optional, and added later from the edit when it
        // was not (owner, 2026-10-06). One file per upload, multipart, read by hand rather than
        // bound, as the contract's copy is: a bound form file asks for antiforgery tokens this
        // bearer-token API has no use for.
        group.MapPost("/customers/{id:guid}/attachments", async (
            Guid id, HttpRequest request, CustomerAttachments attachments, HttpContext http, CancellationToken ct) =>
        {
            // Refused before the body is read when it says it is far too large: the form reader
            // would otherwise buffer it all to find out.
            if (request.ContentLength is long declared && declared > RentalDocuments.MaxAttachmentBytes + 64 * 1024)
                return Results.BadRequest(CustomerAttachments.TooLargeMessage);
            if (!request.HasFormContentType)
                return Results.BadRequest("Send the file as multipart/form-data, one file per request.");

            var form = await request.ReadFormAsync(ct);
            if (form.Files.Count != 1)
                return Results.BadRequest("Send exactly one file.");
            var file = form.Files[0];
            if (file.Length > RentalDocuments.MaxAttachmentBytes)
                return Results.BadRequest(CustomerAttachments.TooLargeMessage);

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            var result = await attachments.AddAsync(id, file.FileName, buffer.ToArray(), UserName(http), ct);
            return result.Ok
                ? Results.Created($"/api/catalog/customers/{id}/attachments/{result.Value!.Id}", result.Value)
                : Problem(result);
        });

        group.MapGet("/customers/{id:guid}/attachments", async (Guid id, CustomerAttachments attachments, CancellationToken ct) =>
        {
            var result = await attachments.ListAsync(id, ct);
            return result.Ok ? Results.Ok(result.Value) : Problem(result);
        });

        group.MapGet("/customers/{id:guid}/attachments/{attachmentId:guid}", async (
            Guid id, Guid attachmentId, CustomerAttachments attachments, HttpContext http, CancellationToken ct) =>
        {
            // From the files folder, through this route only: customers.view, never a public link.
            var opened = await attachments.OpenAsync(id, attachmentId, ct);
            if (opened is null) return Results.NotFound();
            var (attachment, content) = opened.Value;
            FileResults.Harden(http);
            http.Response.Headers.CacheControl = "private, no-store";
            return Results.File(content, attachment.ContentType);
        });

        group.MapDelete("/customers/{id:guid}/attachments/{attachmentId:guid}", async (
            Guid id, Guid attachmentId, CustomerAttachments attachments, CancellationToken ct) =>
        {
            var result = await attachments.RemoveAsync(id, attachmentId, ct);
            return result.Ok ? Results.NoContent() : Problem(result);
        });
    }

    private static void Apply(Product entity, UpsertProductRequest r)
    {
        entity.Sku = r.Sku;
        entity.Barcode = string.IsNullOrWhiteSpace(r.Barcode) ? null : r.Barcode;
        entity.NameAr = r.NameAr;
        entity.NameFr = r.NameFr;
        entity.NameEn = r.NameEn;
        entity.CategoryId = r.CategoryId;
        entity.Price = r.Price;
        entity.CostPrice = r.CostPrice;
        entity.TaxRate = r.TaxRate;
        entity.Unit = string.IsNullOrWhiteSpace(r.Unit) ? "pcs" : r.Unit;
        entity.TrackStock = r.TrackStock;
        // Every item is both rented and sold, from one stock (owner, 2026-10-06): the flag is set
        // whatever the request says, so every item carries the on-rent, maintenance and scrap
        // balances a rental needs, and the till sells it at its shelf price.
        entity.IsRentable = true;
        if (r.AssetUnitValue >= 0) entity.AssetUnitValue = r.AssetUnitValue;
        entity.IsActive = r.IsActive;
    }

    /// <summary>
    /// The fields a clerk edits. Not the code: the create route draws it and nothing changes it,
    /// so <c>r.Code</c> is read nowhere (owner, 2026-10-05).
    /// </summary>
    private static void Apply(Customer entity, UpsertCustomerRequest r, CustomerKind kind)
    {
        // NameRefusal has already refused a person whose parts do not resolve.
        var (first, family, full) = CustomerRules.Names(kind, r.FullName, r.FirstName, r.FamilyName)!.Value;
        entity.FirstName = first;
        entity.FamilyName = family;
        entity.FullName = full;
        entity.Phone = r.Phone;
        entity.Address = r.Address;
        // Trimmed, and blank is none, as the identity number is. TaxIdProblem has already refused a
        // person a new one, so what reaches here for a person is nothing or the number kept as it was.
        entity.TaxId = CustomerRules.NormalizeTaxId(r.TaxId);
        entity.Email = r.Email;
        entity.BirthDate = r.BirthDate;
        entity.CreditLimit = r.CreditLimit;
        entity.Kind = kind;
        // A company has no identity document of its own — its representative carries one — so
        // the field is cleared rather than refused; the form hides it for a company anyway.
        entity.IdDocumentNumber = kind == CustomerKind.Company || string.IsNullOrWhiteSpace(r.IdDocumentNumber)
            ? null
            : r.IdDocumentNumber.Trim();
        // The rest of the paper, cleared with the number and for the same reason. IdDocumentProblem
        // has already refused an unknown type, so the parse here only normalises the spelling.
        entity.IdDocumentType = kind == CustomerKind.Company ? null : IdDocumentTypes.Normalize(r.IdDocumentType);
        entity.IdDocumentIssuer = kind == CustomerKind.Company || string.IsNullOrWhiteSpace(r.IdDocumentIssuer)
            ? null
            : r.IdDocumentIssuer.Trim();
        entity.IdDocumentIssuedOn = kind == CustomerKind.Company ? null : r.IdDocumentIssuedOn;
    }

    /// <summary>
    /// A company's other fiscal identifiers, read the way the company's and a branch's are
    /// (<see cref="FiscalIdValues"/>): keys of the country profile only, trimmed, blanks dropped.
    /// None sent leaves the stored ones — the till's quick-create sends none. A person has none, as
    /// a person has no tax number, so they are cleared whatever was sent. Returns the 400's sentence.
    /// </summary>
    private static string? ApplyFiscalIds(Customer entity, UpsertCustomerRequest r, CustomerKind kind, RegionalSettings profile)
    {
        if (kind == CustomerKind.Person)
        {
            entity.FiscalIds = new();
            return null;
        }
        if (r.FiscalIds is null) return null;
        if (FiscalIdValues.TryNormalize(profile, r.FiscalIds, entity.FiscalIds, out var clean) is string problem) return problem;
        entity.FiscalIds = clean;
        return null;
    }

    /// <summary>
    /// The identity paper's type is one of three keys or nothing, for the customer and for the
    /// representative riding in the same request; the issuer fits its column. Optional on the
    /// customer — the rental contract is where they become mandatory.
    /// </summary>
    private static string? IdDocumentProblem(UpsertCustomerRequest r)
    {
        // Every part that was filled in must be well formed (owner, 2026-10-02); none is required here.
        // A company's own paper is cleared on save (ApplyCustomer), so only its type is looked at.
        var today = IdDocumentTypes.TodayAnywhere(DateTime.UtcNow);
        var company = CustomerRules.TryParseKind(r.Kind, out var kind) && kind == CustomerKind.Company;
        return (company
                   ? IdDocumentTypes.Problem(r.IdDocumentType, null)
                   : IdDocumentTypes.IdentityProblem(r.IdDocumentType, r.IdDocumentNumber, r.IdDocumentIssuer,
                       r.IdDocumentIssuedOn, r.BirthDate is DateTime born ? DateOnly.FromDateTime(born) : null, today))
               ?? (r.Representative is { } rep
                   ? IdDocumentTypes.IdentityProblem(rep.IdDocumentType, rep.IdDocumentNumber, rep.IdDocumentIssuer,
                       rep.IdDocumentIssuedOn, null, today)
                   : null);
    }

    /// <summary>
    /// The location a new customer is registered at, whose code goes into the customer code (owner,
    /// 2026-10-05). A branch-locked caller — a manager, a cashier at the till — registers at their own
    /// branch, read from their user row and never from the request. The company-scoped Admin may name
    /// their working branch in <c>branchId</c>; one that is not a live location, or none, is the main
    /// branch.
    /// </summary>
    private static async Task<Guid?> RegisteredAtAsync(UpsertCustomerRequest r, HttpContext http, PosDbContext db, CancellationToken ct)
    {
        if (!BranchAssignment.IsCompanyScoped(http.User)) return await BranchAssignment.ForCallerAsync(http.User, db, ct);
        if (r.BranchId is Guid named && await db.Branches.AnyAsync(b => b.Id == named && !b.IsDeleted, ct)) return named;
        return await BranchAssignment.MainBranchAsync(db, ct);
    }

    /// <summary>A representative counts as sent only with a name on it; an empty object is no one.</summary>
    private static bool HasRepresentative(UpsertCustomerRequest r) =>
        r.Representative is not null && !string.IsNullOrWhiteSpace(r.Representative.FullName);

    private const string KindParseError = "Customer kind must be Person, Company or Unspecified.";

    /// <summary>
    /// The one English sentence for each kind rule, and its status. The screen says each of these
    /// first, in the reader's language, from the same reason code; these are the fallback.
    /// </summary>
    private static IResult? KindRefusal(string? problem) => problem switch
    {
        null => null,
        "kindRequired" => Results.BadRequest("Say whether the customer is a person or a company."),
        "kindCannotRevert" => Results.BadRequest("A customer's kind cannot go back to unspecified."),
        "personHasNoRepresentative" => Results.BadRequest("A person has no representative; enter their own details instead."),
        "companyNeedsRepresentative" => Results.BadRequest("A company needs an authorised representative: enter their full name."),
        "representativeExists" => Results.Conflict("This company already has a representative; use 'change the representative' so the history is kept."),
        _ => Results.BadRequest(problem)
    };

    /// <summary>The English fallback for <see cref="CustomerRules.PersonNameRequired"/>.</summary>
    public const string PersonNameRequiredMessage = "A person needs a first name and a family name.";

    /// <summary>
    /// A person sent in parts with one of them blank (owner, 2026-10-06), as Problem JSON with its
    /// stable code — the shape <see cref="TaxIdRefusal"/> answers with. The screen never sends one.
    /// </summary>
    private static IResult? NameRefusal(UpsertCustomerRequest r, CustomerKind kind) =>
        CustomerRules.Names(kind, r.FullName, r.FirstName, r.FamilyName) is not null
            ? null
            : Results.Problem(PersonNameRequiredMessage, statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["code"] = CustomerRules.PersonNameRequired });

    /// <summary>The English fallback for <see cref="CustomerRules.TaxIdCompanyOnly"/>.</summary>
    public const string TaxIdCompanyOnlyMessage = "A tax number is for companies only: a natural person has none.";

    /// <summary>
    /// A person's tax number refused (owner, 2026-10-05) as Problem JSON with its stable code, the
    /// shape the branch rules answer with, so the customers screen resolves the code to a sentence
    /// in the reader's language and a caller with no dictionary still reads the English one.
    /// </summary>
    private static IResult? TaxIdRefusal(string? problem) => problem is null
        ? null
        : Results.Problem(TaxIdCompanyOnlyMessage, statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?> { ["code"] = problem });

    /// <summary>
    /// The customer's phone and the representative's read as mobiles in international form
    /// (owner, 2026-11-17: "phone must be saved as mobile format", with the country code, for
    /// messages later). On an edit, a phone sent back exactly as it is stored is kept even when it
    /// is not one: a number from before the rule is corrected by hand, never a reason to refuse a
    /// change to another field. Refused as Problem JSON with its code and the field it is about.
    /// </summary>
    internal static (UpsertCustomerRequest Request, IResult? Refused) ReadPhones(
        UpsertCustomerRequest r, PhoneNumberRules? rules, string? storedPhone, bool readRepresentative)
    {
        var typed = string.IsNullOrWhiteSpace(r.Phone) ? null : r.Phone.Trim();
        var reading = PhoneNumbers.Normalize(typed, rules);
        string? phone;
        if (reading.IsMobile) phone = reading.Value;
        else if (storedPhone is not null && string.Equals(typed, storedPhone.Trim(), StringComparison.Ordinal)) phone = storedPhone;
        else return (r, PhoneRefusal("phone"));

        var representative = r.Representative;
        if (readRepresentative && representative is not null)
        {
            var repReading = PhoneNumbers.Normalize(representative.Phone, rules);
            if (!repReading.IsMobile) return (r, PhoneRefusal("representativePhone"));
            representative = representative with { Phone = repReading.Value };
        }
        return (r with { Phone = phone, Representative = representative }, null);
    }

    internal static IResult PhoneRefusal(string field) =>
        Results.Problem(PhoneNumbers.NotMobileMessage, statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?> { ["code"] = PhoneNumbers.NotMobile, ["field"] = field });

    private static string? UserName(HttpContext http) =>
        http.User.FindFirstValue("name") ?? http.User.Identity?.Name;

    private const string OpeningAlreadySetMessage =
        "This customer's opening balance is already set. A wrong figure is put right with a correction and its reason.";

    /// <summary>The caller's own branch, or the main branch for a company-wide caller: where a back-office entry is filed.</summary>
    private static async Task<Guid?> CallerBranchAsync(HttpContext http, PosDbContext db, CancellationToken ct) =>
        BranchAssignment.IsCompanyScoped(http.User)
            ? await BranchAssignment.MainBranchAsync(db, ct)
            : await BranchAssignment.ForCallerAsync(http.User, db, ct);

    private static IResult Problem<T>(RentalResult<T> result) => result.Failure switch
    {
        RentalFailure.NotFound => Results.NotFound(result.Message),
        RentalFailure.Conflict => Results.Conflict(result.Message),
        _ => Results.BadRequest(result.Message)
    };

    /// <summary>Admin-configurable intake policy (Store.RequireCustomer*) enforced here, not
    /// with data annotations, since which fields are mandatory is a runtime setting.</summary>
    /// <remarks>
    /// The tax number is asked of a company only (owner, 2026-10-05: "الرقم الجبائي للشركات
    /// فقط"): a natural person has none, so a person is never missing one, and a row whose kind
    /// nobody has set yet is not asked for one either — it may well be a person.
    /// </remarks>
    private static async Task<string?> ValidateCustomerAsync(
        UpsertCustomerRequest r, CustomerKind kind, PosDbContext db, CancellationToken ct)
    {
        var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => !s.IsDeleted, ct);
        if (store is null) return null;

        var missing = new List<string>();
        if (store.RequireCustomerEmail && string.IsNullOrWhiteSpace(r.Email)) missing.Add("email");
        if (store.RequireCustomerBirthDate && r.BirthDate is null) missing.Add("birth date");
        if (store.RequireCustomerPhone && string.IsNullOrWhiteSpace(r.Phone)) missing.Add("phone");
        if (store.RequireCustomerAddress && string.IsNullOrWhiteSpace(r.Address)) missing.Add("address");
        if (store.RequireCustomerTaxId && kind == CustomerKind.Company && string.IsNullOrWhiteSpace(r.TaxId))
            missing.Add("tax ID");

        return missing.Count == 0 ? null : $"Missing required field(s): {string.Join(", ", missing)}.";
    }

    private static string TaxRateError(RegionalSettings profile) =>
        $"Tax rate must be one of: {string.Join(", ", profile.TaxRates.Select(x => x.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)))}.";

    /// <summary>Tombstone rather than delete, so terminals learn about the removal on the next pull.</summary>
    private static async Task<IResult> SoftDeleteAsync<T>(DbSet<T> set, Guid id, PosDbContext db, CancellationToken ct)
        where T : SyncEntity
    {
        var entity = await set.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entity is null) return Results.NotFound();
        entity.IsDeleted = true;
        await db.SaveWithVersionsAsync(ct, entity);
        return Results.NoContent();
    }

    /// <summary>
    /// A category's colour is a colour (security review, 2026-10-10): the till binds it to a style,
    /// and any CSS there — <c>url(https://…)</c> — made every till call an outside host. A hex
    /// colour as the colour picker writes it, or none.
    /// </summary>
    public static string? CategoryColorProblem(string? color) =>
        string.IsNullOrEmpty(color) || System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$")
            ? null
            : "A category colour is written #RRGGBB, as the colour picker gives it.";
}
