using ParadiERP.Application.Catalog;
using ParadiERP.Application.Contracts;
using ParadiERP.Application.Regional;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Ledger;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Valuation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ParadiERP.Infrastructure.Catalog;

public enum OpeningStockFailure { NotFound, Invalid }

/// <summary>An opening balance, or the sentence that says why there isn't one.</summary>
public sealed record OpeningStockResult<T>(T? Value, OpeningStockFailure? Failure = null, string? Message = null)
{
    public bool Ok => Failure is null;

    public static OpeningStockResult<T> Success(T value) => new(value);
    public static OpeningStockResult<T> NotFound(string message) => new(default, OpeningStockFailure.NotFound, message);
    public static OpeningStockResult<T> Invalid(string message) => new(default, OpeningStockFailure.Invalid, message);
}

/// <summary>One line of an opening balance, its product already found.</summary>
public readonly record struct OpeningQuantity(Product Product, decimal Qty, decimal? UnitValue = null);

public sealed record OpeningApplied(Guid DocumentId, decimal Value);

/// <summary>
/// What a branch already held on the day the company adopted the system — typed one line at a
/// time, or loaded from the branch's own stock sheet.
///
/// <para>
/// Both ways end in <see cref="ApplyAsync"/>, so the rules are stated once: the quantity is
/// <b>set</b>, not added, and the movement and the posting carry the difference; the value goes
/// against opening-balance equity and never to a supplier or to revenue. The sheet path adds
/// only what a sheet needs — finding each line's article by name, and creating the ones the
/// catalogue does not have yet.
/// </para>
/// </summary>
public static class OpeningStock
{
    public const int MaxImportLines = 5_000;
    public const string DefaultSkuPrefix = "ART-";
    public const int MaxSkuPrefixLength = 20;

    public const string BranchAlreadyOpen =
        "This branch is already open for operation; receive centrally and transfer, or record a stock adjustment.";

    /// <summary>
    /// Sets each line's available quantity at the branch and posts the value. The caller owns the
    /// transaction and the save.
    ///
    /// <para>
    /// <b>The value of a line</b> is its stated unit value (the one typed, else the product's cost
    /// price) times the difference — today's rule, and the rule while sales are costed at the cost
    /// price (the default). Once they are costed at the weighted average (C-01), a line follows what
    /// the valuation shadow does with its movement, so the stock accounts stay the shadow's figure:
    /// </para>
    /// <list type="bullet">
    /// <item>A restatement that <b>lowers</b> a quantity takes its units out at the live average,
    /// through <see cref="LiveSaleCosting.MovementCostsOrFallbackAsync"/> like every other stock
    /// document: the movement's <c>UnitCost</c>, its <c>CostBasis</c> and the entry all carry it.
    /// Never refused: with no average, or when it cannot be worked out, the stated value, flagged
    /// <see cref="SaleCostBasis.CostPriceFallback"/>.</item>
    /// <item>One that <b>raises</b> it (a first opening included) brings its units in at the stated
    /// value, switch on or off: the shadow takes an opening in at its movement's own unit cost, as a
    /// receipt at its cost (<c>WeightedAverageEngine.InboundCost</c>), so the stated value <i>is</i>
    /// what the shadow takes in — the average follows it, not the other way round. It cannot fall
    /// back, so it keeps <see cref="SaleCostBasis.CostPrice"/> and is never counted as a fallback.</item>
    /// </list>
    /// </summary>
    public static async Task<OpeningApplied> ApplyAsync(
        PosDbContext db, LedgerPoster ledger, Branch branch, IReadOnlyList<OpeningQuantity> lines, string? note,
        CancellationToken ct, Guid? userId = null, string? userName = null, ILogger? logger = null)
    {
        var rows = new StockRows(db);
        var documentId = Guid.NewGuid();
        var moved = new List<(StockMovement Movement, string Sku, decimal Stated)>(lines.Count);

        foreach (var line in lines)
        {
            var stock = await rows.GetAsync(line.Product.Id, branch.Id, ct);
            // An opening balance states what is there, not what changed, so the quantity is set
            // rather than added. The movement and the posting therefore record the difference:
            // restating a line that was entered wrong must correct it, not count the whole
            // quantity a second time.
            var before = stock.StockQty;
            stock.StockQty = line.Qty;
            var delta = stock.StockQty - before;
            var stated = line.UnitValue is decimal given && given >= 0 ? given : line.Product.CostPrice;

            var movement = new StockMovement
            {
                ProductId = line.Product.Id,
                BranchId = branch.Id,
                QtyDelta = delta,
                BalanceAfter = stock.StockQty,
                DocumentId = documentId,
                // Never "purchase": an opening balance did not cost anything today. Posting it as
                // one would show a cost that never occurred in the first period.
                Reason = StockMovement.Label("opening", note),
                Kind = StockMovementKind.Opening,
                UnitCost = stated,
                UserId = userId,
                UserName = userName,
                MovedQty = Math.Abs(delta)
            };
            db.StockMovements.Add(movement);
            moved.Add((movement, line.Product.Sku, stated));
        }

        // Every row that moves goes to the costing, the raises too: a lowering row of a product
        // raised earlier in the same document must see what came in. Only a lowering row takes
        // the answer; a raise is worth its stated value (see above). Null: switched off.
        var planned = moved.Where(x => x.Movement.QtyDelta != 0)
            .Select(x => new PlannedMovement(x.Movement, x.Stated)).ToList();
        var costs = planned.Any(p => p.Movement.QtyDelta < 0)
            ? await new LiveSaleCosting(db).MovementCostsOrFallbackAsync(planned, logger, ct)
            : null;

        var valued = new List<ValuedLine>(moved.Count);
        foreach (var (movement, sku, stated) in moved)
        {
            var value = movement.QtyDelta * stated;
            if (movement.QtyDelta < 0 && costs?.GetValueOrDefault(movement.Id) is { } cost)
            {
                movement.UnitCost = cost.UnitCost;
                movement.CostBasis = cost.Basis;
                value = -cost.Value;
            }
            valued.Add(new ValuedLine(sku, value));
        }

        // ⚠ Against opening-balance equity, never a supplier and never revenue — see
        // StockPosting.Opening for why that distinction decides whether the first period shows a
        // cost that never occurred.
        await ledger.PostAsync(StockPosting.Opening(documentId, branch.Id, branch.NameFr, valued), ct);
        return new OpeningApplied(documentId, valued.Sum(v => v.Value));
    }

    /// <summary>What each line of the sheet would become, written nowhere.</summary>
    public static async Task<OpeningImportPreviewDto> PreviewAsync(
        PosDbContext db, string fileName, Worksheet sheet, CancellationToken ct)
    {
        var layout = StockSheet.Read(sheet);
        var index = await ProductIndex.LoadAsync(db, tracked: false, ct);

        var firstRowOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new List<OpeningImportRowDto>(layout.Lines.Count);
        foreach (var line in layout.Lines)
        {
            var key = ProductNameKey.For(line.Name);
            int? duplicateOf = firstRowOf.TryGetValue(key, out var first) ? first : null;
            firstRowOf.TryAdd(key, line.Row);

            var found = index.Find(line.Name);
            var product = found.Count == 1 ? found[0] : null;
            rows.Add(new OpeningImportRowDto(
                line.Row, line.Name, line.Values,
                found.Count switch { 0 => "new", 1 => "existing", _ => "ambiguous" },
                product?.Id, product?.Sku, product?.NameAr, product?.NameFr, product?.NameEn,
                duplicateOf));
        }

        return new OpeningImportPreviewDto(
            fileName, sheet.Name, sheet.SheetNames,
            layout.HeaderRow, SheetColumns.Letter(layout.NameColumn), layout.NameHeader,
            layout.Columns.Select(c => new OpeningImportColumnDto(c.Index, c.Letter, c.Header)).ToList(),
            layout.QuantityColumn, rows);
    }

    /// <summary>
    /// Every line becomes the branch's opening quantity; a line whose article the catalogue does
    /// not have creates it first, with a generated code and only the name the sheet gives.
    /// </summary>
    public static async Task<OpeningStockResult<OpeningImportResultDto>> ImportAsync(
        PosDbContext db, LedgerPoster ledger, IRegionalSettingsProvider regional, OpeningImportRequest r,
        CancellationToken ct, Guid? userId = null, string? userName = null, ILogger? logger = null)
    {
        if (r.Lines is null || r.Lines.Count == 0)
            return Invalid("An opening balance needs at least one line.");
        if (r.Lines.Count > MaxImportLines)
            return Invalid($"A sheet can load at most {MaxImportLines:N0} lines at once; split it.");
        foreach (var line in r.Lines)
        {
            var name = ProductNameKey.Clean(line.Name);
            if (name.Length == 0 && line.ProductId is null)
                return Invalid("Every line needs the article's name.");
            if (name.Length > 200)
                return Invalid($"\"{name[..40]}…\" is longer than 200 characters; shorten it in the sheet.");
            if (line.Qty < 0)
                return Invalid($"\"{name}\": an opening quantity cannot be negative ({line.Qty:0.###}).");
        }

        var prefix = string.IsNullOrWhiteSpace(r.SkuPrefix) ? DefaultSkuPrefix : r.SkuPrefix.Trim();
        if (prefix.Length > MaxSkuPrefixLength || !prefix.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            return Invalid($"The code prefix may hold up to {MaxSkuPrefixLength} Latin letters, digits, '-', '_' or '.'.");

        var branch = await db.Branches.FirstOrDefaultAsync(b => b.Id == r.BranchId && !b.IsDeleted, ct);
        if (branch is null) return NotFound("Branch not found.");
        if (branch.IsOpenForOperation) return Invalid(BranchAlreadyOpen);

        var index = await ProductIndex.LoadAsync(db, tracked: true, ct);

        // Every line resolved before anything is written, so a refusal leaves nothing behind.
        var resolved = new List<(OpeningImportLine Line, string Name, Product? Product)>(r.Lines.Count);
        foreach (var line in r.Lines)
        {
            var name = ProductNameKey.Clean(line.Name);
            if (line.ProductId is Guid id)
            {
                if (index.ById(id) is not { } chosen) return NotFound($"Product {id} not found.");
                resolved.Add((line, name, chosen));
                continue;
            }
            var found = index.Find(name);
            if (found.Count > 1)
                return Invalid($"\"{name}\" matches {found.Count} products ({string.Join(", ", found.Select(p => p.Sku))}); choose one on the preview.");
            resolved.Add((line, name, found.Count == 1 ? found[0] : null));
        }

        // The same article twice would set its quantity twice, and only the last would stand.
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, name, product) in resolved)
        {
            var identity = product is not null ? "id:" + product.Id : "new:" + ProductNameKey.For(name);
            var shown = product is null || name.Length > 0 ? name : product.Sku;
            if (!seen.TryAdd(identity, shown))
                return Invalid($"\"{seen[identity]}\" and \"{shown}\" are the same article; keep one line.");
        }

        await using var tx = await db.BeginWriteScopeAsync(ct);

        var codes = await SkuSequence.LoadAsync(db, prefix, ct);
        var taxRate = regional.Get().DefaultTaxRate;
        var created = new List<OpeningImportCreatedDto>();
        var quantities = new List<OpeningQuantity>(resolved.Count);
        foreach (var (line, name, existing) in resolved)
        {
            var product = existing;
            if (product is null)
            {
                // Only what the sheet says. The French and English names, prices and the asset
                // value are the owner's to give; an invented translation would be printed on
                // contracts beside the Arabic as if he had written it.
                product = new Product
                {
                    Sku = codes.Next(),
                    NameAr = name,
                    TaxRate = taxRate,
                    TrackStock = true,
                    IsActive = true,
                    // Every item is rented and sold (owner, 2026-10-06), whatever the request says.
                    IsRentable = true,
                };
                db.Products.Add(product);
                await db.TouchAsync(product, ct);
                created.Add(new OpeningImportCreatedDto(product.Id, product.Sku, product.NameAr));
            }
            quantities.Add(new OpeningQuantity(product, line.Qty));
        }

        var applied = await ApplyAsync(db, ledger, branch, quantities, r.Note, ct, userId, userName, logger);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return OpeningStockResult<OpeningImportResultDto>.Success(new OpeningImportResultDto(
            branch.Id, applied.DocumentId, quantities.Count, quantities.Count - created.Count, created.Count,
            applied.Value, created));
    }

    private static OpeningStockResult<OpeningImportResultDto> Invalid(string message) =>
        OpeningStockResult<OpeningImportResultDto>.Invalid(message);

    private static OpeningStockResult<OpeningImportResultDto> NotFound(string message) =>
        OpeningStockResult<OpeningImportResultDto>.NotFound(message);

    /// <summary>The live catalogue, findable by any of its three names or by its code.</summary>
    private sealed class ProductIndex
    {
        private readonly Dictionary<Guid, Product> byId = [];
        private readonly Dictionary<string, List<Product>> byName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Product> bySku = new(StringComparer.OrdinalIgnoreCase);

        public static async Task<ProductIndex> LoadAsync(PosDbContext db, bool tracked, CancellationToken ct)
        {
            var query = db.Products.Where(p => !p.IsDeleted);
            var products = await (tracked ? query : query.AsNoTracking()).ToListAsync(ct);
            var index = new ProductIndex();
            foreach (var p in products)
            {
                index.byId[p.Id] = p;
                index.bySku.TryAdd(p.Sku.Trim(), p);
                foreach (var key in new[] { p.NameAr, p.NameFr, p.NameEn }.Select(ProductNameKey.For).Where(k => k.Length > 0).Distinct())
                {
                    if (!index.byName.TryGetValue(key, out var list)) index.byName[key] = list = [];
                    list.Add(p);
                }
            }
            return index;
        }

        public Product? ById(Guid id) => byId.GetValueOrDefault(id);

        /// <summary>Every product answering to this name, or carrying it as its code.</summary>
        public List<Product> Find(string name)
        {
            var found = byName.TryGetValue(ProductNameKey.For(name), out var named) ? new List<Product>(named) : [];
            if (bySku.TryGetValue(name.Trim(), out var coded) && found.All(p => p.Id != coded.Id)) found.Add(coded);
            return found;
        }
    }

    /// <summary>
    /// Codes for new articles: the prefix and a four-digit number, continuing after the highest
    /// one already used with that prefix. Deleted products are counted too — the unique index
    /// still holds their codes.
    /// </summary>
    private sealed class SkuSequence(HashSet<string> taken, string prefix, int next)
    {
        public static async Task<SkuSequence> LoadAsync(PosDbContext db, string prefix, CancellationToken ct)
        {
            var skus = await db.Products.AsNoTracking().Select(p => p.Sku).ToListAsync(ct);
            var highest = skus
                .Where(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(s => int.TryParse(s[prefix.Length..], out var n) ? n : 0)
                .DefaultIfEmpty(0)
                .Max();
            return new SkuSequence(new HashSet<string>(skus, StringComparer.OrdinalIgnoreCase), prefix, highest + 1);
        }

        public string Next()
        {
            string sku;
            do sku = prefix + (next++).ToString("D4");
            while (!taken.Add(sku));
            return sku;
        }
    }
}
