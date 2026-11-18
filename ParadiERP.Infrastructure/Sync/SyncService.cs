using Microsoft.EntityFrameworkCore;
using System.Globalization;
using ParadiERP.Application.Catalog;
using ParadiERP.Application.Contracts;
using ParadiERP.Application.Loyalty;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Integrations;
using ParadiERP.Infrastructure.Ledger;
using ParadiERP.Infrastructure.Loyalty;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Valuation;
using ParadiERP.Application.Regional;
using ParadiERP.Application.Subscriptions;
using Microsoft.Extensions.Logging;

namespace ParadiERP.Infrastructure.Sync;

/// <summary>
/// Delta synchronisation between the server and offline POS terminals.
///
/// Conflict policy:
///   * Catalog (store, categories, products, customers, users) — SERVER WINS. Terminals are
///     read-only replicas of the catalog; they never push catalog edits.
///   * Sales — CLIENT WINS and are append-only. A sale that physically happened in the shop
///     cannot be rejected by the server, so push is idempotent on the terminal-generated Id
///     and a stock shortfall is recorded as a warning rather than a refusal.
///   * Stock — the server is the single source of truth. Terminals keep an optimistic local
///     estimate and overwrite it with the server figure on the next pull.
/// </summary>
public class SyncService(PosDbContext db, ISubscriptionProvider subscriptions, IRegionalSettingsProvider regional,
    ILanguagePackStore languagePacks, TenantIntegrations integrations, LedgerPoster ledger, ILogger<SyncService> logger)
{
    private bool queueWebhook;

    /// <summary>Rows per entity type per pull, so a cold terminal on a weak line can resume.</summary>
    public const int PullBatchSize = 500;

    /// <param name="authenticatedDeviceCode">
    /// The terminal this caller's token was issued for, never a code it supplied. It selects
    /// the branch whose stock figures come back and it is the row whose sync bookkeeping is
    /// stamped, so taking it from the request would let any till read another branch's position
    /// (the thing <c>Permissions.ViewOtherBranchStock</c> exists to gate) and scribble on
    /// another terminal's last-seen cursor.
    /// </param>
    /// <param name="actor">
    /// Who pulls (security review, 2026-10-10). Customers and gift cards go only to a till that
    /// sells, cashier PIN hashes only to a till and only its own branch's: before, any signed-in
    /// session got every customer, every gift card code and every cashier's PIN hash in the company.
    /// Omitted only by the tests that drive the service directly.
    /// </param>
    public async Task<PullResponse> PullAsync(long since, string? authenticatedDeviceCode, CancellationToken ct,
        SyncActor? actor = null)
    {
        actor ??= SyncActor.Unchecked;
        var deviceCode = authenticatedDeviceCode;
        var tillSession = !string.IsNullOrWhiteSpace(deviceCode);
        var sells = tillSession && actor.CanSell;

        // Snapshot the cursor first: rows written after this point are simply picked up on
        // the next pull. Reading it afterwards could skip a row committed mid-query.
        var ceiling = await db.CurrentVersionAsync(ct);

        // A terminal only ever sells for one branch, resolved from its own device code — it
        // never needs another branch's stock. Falls back to the main branch — the head office, a selling branch — (or no
        // branch at all) if the device is unknown, so a pull never hard-fails on this.
        Device? device = null;
        if (!string.IsNullOrWhiteSpace(deviceCode))
            device = await db.Devices.FirstOrDefaultAsync(d => d.Code == deviceCode, ct);
        var branchId = device?.BranchId
            ?? (await db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.IsMain && !b.IsWarehouse, ct))?.Id;

        var store = await db.Stores.AsNoTracking()
            .Where(s => s.Version > since && s.Version <= ceiling)
            .OrderByDescending(s => s.Version)
            .FirstOrDefaultAsync(ct);

        var branches = await Page(db.Branches, since, ceiling, ct);
        var categories = await Page(db.Categories, since, ceiling, ct);
        var products = await Page(db.Products, since, ceiling, ct);
        var customers = sells ? await Page(db.Customers, since, ceiling, ct) : [];
        var giftCards = sells ? await Page(db.GiftCards, since, ceiling, ct) : [];
        var paymentMethods = await Page(db.PaymentMethodDefs, since, ceiling, ct);

        var stockByProduct = new Dictionary<Guid, ProductStock>();
        if (branchId is Guid b && products.Count > 0)
        {
            var productIds = products.Select(p => p.Id).ToList();
            stockByProduct = await db.ProductStocks.AsNoTracking()
                .Where(s => s.BranchId == b && productIds.Contains(s.ProductId))
                .ToDictionaryAsync(s => s.ProductId, ct);
        }

        // Only till users' PIN hashes are replicated: those whose role has TillBranch scope (AUTH-1,
        // today exactly the cashiers). Replicating a manager's or admin's hash would let any till
        // harvest it offline and brute-force a 4-digit PIN.
        // And only to a till, only of its own branch: a 4-digit PIN hash is cracked offline in
        // minutes, so the fewer copies the better (security review, 2026-10-10).
        var tillBranchId = device?.BranchId;
        var users = tillSession && tillBranchId is Guid ownBranch
            ? await db.Users.AsNoTracking()
                .Where(u => u.Version > since && u.Version <= ceiling && u.BranchId == ownBranch
                    && db.Roles.Any(r => r.Id == u.RoleId && r.Scope == RoleScope.TillBranch))
                .OrderBy(u => u.Version)
                .Take(PullBatchSize)
                .ToListAsync(ct)
            : [];

        // One cursor serves every table, so it may advance only as far as the shortest truncated
        // page reached. Taking the highest row returned from any table jumped a table that still
        // had rows waiting behind its 500th: a catalogue of 501 products and one later cashier
        // edit sent products 1-500 and the cashier, and product 501 never reached the till.
        // A page that came back whole says nothing about the cursor, so it offers the ceiling;
        // a truncated one always ends above since, so each pull makes progress.
        var cursor = new[]
        {
            PageEnd(branches, ceiling), PageEnd(categories, ceiling), PageEnd(products, ceiling),
            PageEnd(customers, ceiling), PageEnd(giftCards, ceiling), PageEnd(paymentMethods, ceiling),
            PageEnd(users, ceiling)
        }.Min();

        // Rows past the cursor are held back rather than sent twice: the next pull starts there.
        branches = UpTo(branches, cursor);
        categories = UpTo(categories, cursor);
        products = UpTo(products, cursor);
        customers = UpTo(customers, cursor);
        giftCards = UpTo(giftCards, cursor);
        paymentMethods = UpTo(paymentMethods, cursor);
        users = UpTo(users, cursor);
        if (store is not null && store.Version > cursor) store = null;

        if (device is not null)
        {
            device.LastSyncAtUtc = DateTime.UtcNow;
            device.LastCursor = cursor;
            await db.SaveChangesAsync(ct);
        }

        return new PullResponse(
            cursor,
            cursor < ceiling,
            DateTime.UtcNow,
            store is null ? null : Map(store),
            branches.Select(Map).ToList(),
            categories.Select(Map).ToList(),
            products.Select(p => Map(p, stockByProduct.GetValueOrDefault(p.Id))).ToList(),
            customers.Select(Map).ToList(),
            users.Select(Map).ToList(),
            subscriptions.GetAccess(),
            regional.Get(),
            await languagePacks.RevisionsForAsync(regional.Get(), ct),
            giftCards.Select(Map).ToList(),
            await integrations.PublicViewAsync(ct),
            paymentMethods.Select(Map).ToList());
    }

    private static Task<List<T>> Page<T>(DbSet<T> set, long since, long ceiling, CancellationToken ct)
        where T : SyncEntity =>
        set.AsNoTracking()
            .Where(e => e.Version > since && e.Version <= ceiling)
            .OrderBy(e => e.Version)
            .Take(PullBatchSize)
            .ToListAsync(ct);

    /// <summary>How far a page lets the cursor go: its last row if it was cut short, else the ceiling.</summary>
    private static long PageEnd<T>(List<T> page, long ceiling) where T : SyncEntity =>
        page.Count == PullBatchSize ? page[^1].Version : ceiling;

    private static List<T> UpTo<T>(List<T> page, long cursor) where T : SyncEntity =>
        page.Count == 0 || page[^1].Version <= cursor ? page : page.Where(e => e.Version <= cursor).ToList();

    /// <param name="authenticatedDeviceCode">
    /// The terminal this caller's token was issued for. <see cref="PushRequest.DeviceCode"/> is
    /// deliberately <b>not</b> used: it decided which branch's stock, cash box and ledger the
    /// sale moved, and which terminal's <c>(DeviceCode, Reference)</c> / <c>(DeviceCode,
    /// LocalSeq)</c> space it occupied, so any till could have written into another branch's
    /// books and squatted on the receipt numbers that branch's real till was about to use.
    /// Substituting the authenticated code never refuses a sale — it only records it against
    /// the terminal that actually took the money.
    /// </param>
    /// <param name="actor">
    /// Who pushed, from the token (security review, 2026-10-10): whether they may sell and refund,
    /// and who they are when the cashier the till recorded is not a real user of this branch.
    /// Omitted only by the tests that drive the service directly.
    /// </param>
    public async Task<PushResponse> PushAsync(PushRequest request, string? authenticatedDeviceCode, CancellationToken ct,
        SyncActor? actor = null)
    {
        actor ??= SyncActor.Unchecked;
        var deviceCode = authenticatedDeviceCode ?? "";
        var sales = request.Sales ?? Array.Empty<SalePush>();
        var results = new List<PushResultItem>();
        int accepted = 0, duplicates = 0, rejected = 0;

        // The cursor a terminal should resume from is the one from BEFORE this push: the
        // stock and balance rows this push itself touches must still come back on the next
        // pull, which is how the terminal replaces its optimistic estimates.
        var cursorBefore = await db.CurrentVersionAsync(ct);

        var incomingIds = sales.Select(s => s.Id).ToList();
        var known = (await db.Sales.AsNoTracking()
                .Where(s => incomingIds.Contains(s.Id))
                .Select(s => s.Id)
                .ToListAsync(ct))
            .ToHashSet();

        // Resolved once per push, not per sale: which branch's stock this device sells for.
        // Never trusted from the client — a till cannot claim to be a different branch.
        var deviceBranchId = await db.Devices.AsNoTracking()
            .Where(d => d.Code == deviceCode)
            .Select(d => (Guid?)d.BranchId)
            .FirstOrDefaultAsync(ct);
        var branchId = deviceBranchId
            ?? (await db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.IsMain && !b.IsWarehouse, ct))?.Id
            ?? Guid.Empty;

        var (integrationSettings, integrationSecrets) = await integrations.GetAsync(ct);
        queueWebhook = integrationSettings.WebhookReady(integrationSecrets);

        // Process oldest-first so stock movements land in the order they happened in the shop.
        foreach (var push in sales.OrderBy(s => s.SoldAtUtc).ThenBy(s => s.LocalSeq))
        {
            if (known.Contains(push.Id))
            {
                duplicates++;
                results.Add(new PushResultItem(push.Id, "duplicate", "Already recorded on the server."));
                continue;
            }

            var validation = Validate(push, regional.Get().MoneyTolerance());
            if (validation is not null)
            {
                rejected++;
                results.Add(new PushResultItem(push.Id, "rejected", validation));
                continue;
            }

            // The server decides who may sell and who may refund (security review, 2026-10-10):
            // sales.create and sales.refund were granted and checked nowhere, so any signed-in
            // till session could push a refund.
            var refund = string.Equals(push.Status, nameof(SaleStatus.Refunded), StringComparison.OrdinalIgnoreCase);
            if (!actor.CanSell || (refund && !actor.CanRefund))
            {
                rejected++;
                results.Add(new PushResultItem(push.Id, "rejected",
                    refund ? "This account may not refund." : "This account may not sell.", "forbidden"));
                continue;
            }

            try
            {
                var (numbered, renumbered) = await FreeNumberAsync(push, deviceCode, ct);
                var warnings = await PersistSaleAsync(numbered, deviceCode, branchId, actor, ct);
                known.Add(push.Id);
                accepted++;
                results.Add(new PushResultItem(push.Id, "accepted",
                    renumbered is null ? warnings : string.IsNullOrEmpty(warnings) ? renumbered : renumbered + " " + warnings));
            }
            catch (RefundRefusedException refused)
            {
                // A refund that does not stand on a real sale, or asks for more than is left of
                // it. Its own code, so the till can tell "wait for the original" from "no".
                db.ChangeTracker.Clear();
                rejected++;
                results.Add(new PushResultItem(push.Id, "rejected", refused.Message, refused.Code));
            }
            catch (Exception ex)
            {
                // One poison record must never lose the rest of the day's takings, so every
                // failure is contained to its own sale and reported per item.
                db.ChangeTracker.Clear();

                // A concurrent retry of the same batch loses the race on the primary key.
                // That sale IS on the server, so report it as a duplicate, not a rejection —
                // a terminal told "rejected" would treat a recorded sale as lost.
                if (await db.Sales.AsNoTracking().AnyAsync(s => s.Id == push.Id, ct))
                {
                    known.Add(push.Id);
                    duplicates++;
                    results.Add(new PushResultItem(push.Id, "duplicate", "Already recorded on the server."));
                }
                else
                {
                    rejected++;
                    var why = Explain(ex, push.Id, deviceCode);
                    results.Add(new PushResultItem(push.Id, "rejected", why.Message, why.Code));
                }
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        return new PushResponse(cursorBefore, DateTime.UtcNow, accepted, duplicates, rejected, results);
    }

    /// <summary>
    /// A receipt number this till already used for another sale. It happens when one till is opened
    /// on two computers — a cashier's own till on the shop's PC and on a tablet — and both sell
    /// before either has signed in again (sign-in moves a browser past the server's highest number,
    /// CashierTills.LastSequenceAsync). The sale is the money taken, so it is never refused for its
    /// number: it is recorded under the till's next free sequence, the printed number kept inside
    /// the new one so the paper receipt can still be traced, and the push says so in words.
    /// </summary>
    private async Task<(SalePush Push, string? Note)> FreeNumberAsync(SalePush push, string deviceCode, CancellationToken ct)
    {
        var stored = string.IsNullOrWhiteSpace(deviceCode) ? "UNK" : deviceCode;
        var others = db.Sales.AsNoTracking().Where(s => s.DeviceCode == stored && s.Id != push.Id);
        var seqTaken = await others.AnyAsync(s => s.LocalSeq == push.LocalSeq, ct);
        var referenceTaken = await others.AnyAsync(s => s.Reference == push.Reference, ct);
        if (!seqTaken && !referenceTaken) return (push, null);

        // Only what collides moves: a free sequence is kept, and so is a free printed number, so the
        // paper receipt still matches the books whenever it can.
        var seq = seqTaken
            ? (await db.Sales.AsNoTracking().Where(s => s.DeviceCode == stored).MaxAsync(s => (long?)s.LocalSeq, ct) ?? 0) + 1
            : push.LocalSeq;
        var reference = push.Reference;
        if (referenceTaken)
        {
            reference = $"{push.Reference}/R{seq}";
            if (reference.Length > 40) reference = $"{stored}-{seq:D6}/R";
        }
        logger.LogWarning("Sale {SaleId} from {Device}: receipt number already used on this till; recorded as {New} (sequence {Seq})",
            push.Id, stored, reference, seq);
        return (push with { LocalSeq = seq, Reference = reference },
            referenceTaken
                ? $"Receipt {push.Reference} had already been used on till {stored} by another computer; recorded as {reference}."
                : $"Receipt {push.Reference} was recorded; its sequence on till {stored} had been used by another computer.");
    }

    /// <summary>
    /// The cashier a sale is filed under (security review, 2026-10-10). A till may queue sales of
    /// several cashiers while offline and push them later under whoever signs in next, so the
    /// recorded cashier is kept — but only when it is a real, current user of this branch (or of
    /// the whole company). Anyone else is replaced by the account that pushed, and the name is
    /// always the database's, never the till's.
    /// </summary>
    private async Task ResolveCashierAsync(Sale sale, Guid branchId, SyncActor actor, List<string> warnings, CancellationToken ct)
    {
        var declared = await db.Users.AsNoTracking()
            .Where(u => u.Id == sale.CashierUserId && !u.IsDeleted)
            .Select(u => new { u.Id, u.FullName, u.Username, u.BranchId })
            .FirstOrDefaultAsync(ct);
        if (declared is not null && (declared.BranchId is null || declared.BranchId == branchId))
        {
            sale.CashierName = string.IsNullOrWhiteSpace(declared.FullName) ? declared.Username : declared.FullName;
            return;
        }
        if (actor.UserId is not Guid pusher) return;   // the tests' direct calls: nothing to compare with
        var account = await db.Users.AsNoTracking().Where(u => u.Id == pusher)
            .Select(u => new { u.FullName, u.Username }).FirstOrDefaultAsync(ct);
        sale.CashierUserId = pusher;
        sale.CashierName = account is null ? actor.UserName ?? "" : string.IsNullOrWhiteSpace(account.FullName) ? account.Username : account.FullName;
        warnings.Add("The cashier recorded by the till is not a user of this branch; the sale is filed under the account that sent it.");
    }

    /// <summary>
    /// A refund stands on a sale and gives back no more than is left of it (owner, 2026-10-10:
    /// "a refund must name the original sale and never exceed what is left of it"). Before this, a
    /// pushed refund needed nothing behind it and wiped debts, credited gift cards and took cash
    /// out of the drawer. Checked under the version lock, against every earlier refund of the same
    /// sale, so two tills cannot each refund the whole of it.
    /// </summary>
    private async Task CheckRefundAsync(Sale refund, Guid branchId, decimal tolerance, CancellationToken ct)
    {
        if (refund.RefundOfSaleId is not Guid originalId)
            throw new RefundRefusedException("refund_needs_original", "A refund must name the sale it gives back.");

        var original = await db.Sales.AsNoTracking().Include(s => s.Lines).Include(s => s.Payments)
            .FirstOrDefaultAsync(s => s.Id == originalId, ct);
        // Two tills offline: the original may simply not have arrived yet. The till keeps the refund
        // and tries again; it is not refused.
        if (original is null)
            throw new RefundRefusedException(RefundOriginalPendingCode, "The original sale has not reached the server yet; the refund will be sent again.");
        if (original.Status != SaleStatus.Completed)
            throw new RefundRefusedException("refund_of_refund", "A refund gives back a sale, not another refund.");
        if (original.BranchId != branchId)
            throw new RefundRefusedException("refund_other_branch", "A sale is refunded at the branch that made it; another branch goes through the back office.");

        var earlier = await db.Sales.AsNoTracking().Include(s => s.Lines).Include(s => s.Payments)
            .Where(s => s.RefundOfSaleId == originalId && s.Status == SaleStatus.Refunded)
            .ToListAsync(ct);

        // The money: never more than the sale took, all refunds together.
        if (refund.GrandTotalTtc + earlier.Sum(s => s.GrandTotalTtc) > original.GrandTotalTtc + tolerance)
            throw new RefundRefusedException("refund_exceeds_sale", "The refunds of this sale would come to more than it took.");

        // The goods: per article, never more than the sale sold.
        var sold = original.Lines.GroupBy(l => l.ProductId).ToDictionary(g => g.Key, g => g.Sum(l => l.Qty));
        var given = earlier.SelectMany(s => s.Lines).GroupBy(l => l.ProductId).ToDictionary(g => g.Key, g => g.Sum(l => l.Qty));
        foreach (var line in refund.Lines.GroupBy(l => l.ProductId))
            if (line.Sum(l => l.Qty) + given.GetValueOrDefault(line.Key) > sold.GetValueOrDefault(line.Key))
                throw new RefundRefusedException("refund_exceeds_sale", "A refunded article was not on the sale, or more of it is refunded than was sold.");

        // The customer is the sale's: the refund takes back the points the sale earned and may
        // reduce the debt it ran up, so naming anyone else would move those onto a stranger.
        if (refund.CustomerId != original.CustomerId)
            throw new RefundRefusedException("refund_wrong_customer", "A refund is made to the customer of the sale it gives back.");

        // A debt is reduced only by what went on account.
        decimal Tender(IEnumerable<SalePayment> payments, PaymentBehavior behavior) =>
            payments.Where(p => p.Behavior == behavior).Sum(p => p.Amount);
        var credit = Tender(refund.Payments, PaymentBehavior.Credit);
        if (credit > 0 && credit + earlier.Sum(s => Tender(s.Payments, PaymentBehavior.Credit)) > Tender(original.Payments, PaymentBehavior.Credit) + tolerance)
            throw new RefundRefusedException("refund_exceeds_sale", "More is refunded to the account than was put on it.");
        // A gift card or points may take a refund as store credit, within the amounts above.
    }

    /// <summary>A till's clock may run a little fast; beyond this a sale's date is in the future.</summary>
    private static readonly TimeSpan FutureSaleSlack = TimeSpan.FromMinutes(15);

    /// <summary>A sale older than this when it arrives is said: it may land in a reported period.</summary>
    private static readonly TimeSpan OldSaleAge = TimeSpan.FromDays(31);

    /// <summary>
    /// The till's price and tax rate against the catalogue (security review, 2026-10-10). The server
    /// took both from the device; a sale is never refused for a difference — the catalogue may have
    /// changed since, or the price been agreed at the counter — but it is said and logged, so a
    /// till selling under the catalogue does not go unseen. Discounts travel separately
    /// (LineDiscount), so the unit price is the catalogue's. An open price (0 in the catalogue) and
    /// an untaxed sale's rates are not compared.
    /// </summary>
    private void WarnOnCatalogueDifferences(Sale sale, IReadOnlyList<Product> products, decimal tolerance, List<string> warnings)
    {
        var priced = new List<string>();
        var taxed = new List<string>();
        foreach (var line in sale.Lines)
        {
            if (products.FirstOrDefault(p => p.Id == line.ProductId) is not Product product) continue;
            if (product.Price != 0 && Math.Abs(line.UnitPrice - product.Price) > tolerance) priced.Add(line.Sku);
            if (sale.TaxMode == TaxMode.Taxed && line.TaxRate != product.TaxRate) taxed.Add(line.Sku);
        }
        if (priced.Count > 0)
            warnings.Add($"Price differs from the catalogue for {string.Join(", ", priced.Distinct())}.");
        if (taxed.Count > 0)
            warnings.Add($"Tax rate differs from the catalogue for {string.Join(", ", taxed.Distinct())}.");
        if (priced.Count + taxed.Count > 0)
            logger.LogWarning("Sale {SaleId} ({Reference}): catalogue differences, price {Priced}, tax {Taxed}",
                sale.Id, sale.Reference, string.Join(",", priced.Distinct()), string.Join(",", taxed.Distinct()));
    }

    /// <summary>
    /// A sale of a manufacturing-only item — a raw material, a consumable, a semi-finished good not marked
    /// for sale (ManufacturingRoles.ForSale) — that reached the server anyway: a till whose catalogue predates
    /// the product's role, or a sale queued offline before it. The till no longer offers these, so this is
    /// the exception, and the sale is the money taken and the receipt printed: it is recorded exactly as
    /// made (CLAUDE.md, rule one) — its payment, receipt and stock movement untouched — and the exception
    /// is recorded beside it, one row per line, in the same transaction (owner, 2026-10-26, decision 1), as
    /// well as said to the till and logged. A refund is never judged by it: it gives back what was sold.
    /// </summary>
    private async Task RecordNotForSaleAsync(Sale sale, IReadOnlyList<Product> products, SyncActor actor, List<string> warnings, CancellationToken ct)
    {
        var lines = sale.Lines
            .Select(l => (Line: l, Product: products.FirstOrDefault(p => p.Id == l.ProductId)))
            .Where(x => x.Product is not null && !ManufacturingRoles.ForSale(x.Product))
            .ToList();
        if (lines.Count == 0) return;

        // When each product last stopped being for sale: a sale made before that moment was queued while
        // it was still for sale — stale offline data — rather than offered by a till that should have hidden it.
        var ids = lines.Select(x => x.Product!.Id).Distinct().ToList();
        var withdrawn = (await db.ProductSaleStatusChanges.AsNoTracking()
                .Where(c => ids.Contains(c.ProductId) && c.OldForSale && !c.NewForSale)
                .Select(c => new { c.ProductId, c.ChangedAtUtc })
                .ToListAsync(ct))
            .GroupBy(c => c.ProductId).ToDictionary(g => g.Key, g => g.Max(c => c.ChangedAtUtc));

        foreach (var (line, product) in lines)
        {
            var at = withdrawn.TryGetValue(product!.Id, out var w) ? w : (DateTime?)null;
            db.ManufacturingSaleExceptions.Add(new ManufacturingSaleException
            {
                SaleId = sale.Id, SaleLineId = line.Id, ProductId = product.Id, Sku = product.Sku,
                BranchId = sale.BranchId, DeviceCode = sale.DeviceCode, Reference = sale.Reference, Quantity = line.Qty,
                SoldAtUtc = sale.SoldAtUtc, ReceivedAtUtc = sale.ReceivedAtUtc,
                CashierUserId = sale.CashierUserId, CashierName = Shorten(sale.CashierName, 200), PushedByUserId = actor.UserId,
                Role = product.ManufacturingRole, WithdrawnAtUtc = at, SoldBeforeWithdrawal = at is DateTime t && sale.SoldAtUtc < t,
            });
        }

        var skus = lines.Select(x => x.Product!.Sku).Distinct().ToList();
        warnings.Add($"{NotForSaleWarning}: {string.Join(", ", skus)} — recorded as sold.");
        logger.LogWarning("Sale {SaleId} ({Reference}) sold manufacturing material not marked for sale: {Skus}",
            sale.Id, sale.Reference, string.Join(",", skus));
    }

    private static string? Shorten(string? text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max];

    /// <summary>The words a push's warning starts with when it sold a manufacturing-only item.</summary>
    public const string NotForSaleWarning = "Manufacturing material, not for sale";

    /// <summary>The till keeps a refund with this code and sends it again: its original has not arrived yet.</summary>
    public const string RefundOriginalPendingCode = "refund_original_pending";

    /// <summary>More lines than any receipt has; a push of thousands per sale was a way to hold the write lock.</summary>
    public const int MaxLinesPerSale = 500;

    /// <summary>
    /// A sale's own consistency, answered as a reason or null. Never throws: a figure too large to
    /// add up is that sale's refusal, not a failure of the till's whole batch (security review,
    /// 2026-10-10 — one such record used to block every later sale of that till on each retry).
    /// </summary>
    private static string? Validate(SalePush s, decimal tolerance)
    {
        try { return ValidateFigures(s, tolerance); }
        catch (OverflowException) { return "An amount on this sale is out of range."; }
    }

    private static string? ValidateFigures(SalePush s, decimal tolerance)
    {
        if (s.Id == Guid.Empty) return "Missing sale id.";
        if (string.IsNullOrWhiteSpace(s.Reference)) return "Missing reference.";
        if (s.Lines is null || s.Lines.Count == 0) return "Sale has no lines.";
        if (s.Lines.Count > MaxLinesPerSale) return $"A sale has at most {MaxLinesPerSale} lines.";
        if (s.Payments is null) return "Malformed payment list.";
        if (s.Payments.Count > MaxLinesPerSale) return $"A sale has at most {MaxLinesPerSale} payment lines.";

        // Quantities and totals are always positive; a refund is expressed by Status, never
        // by a negative sign, or a bad client could silently inflate stock.
        if (s.Lines.Any(l => l.Qty <= 0)) return "A line has a zero or negative quantity.";
        if (s.GrandTotalTtc < 0) return "Negative sale total.";
        // The rounding step may have changed since the sale, so only an absurd value is refused.
        if (Math.Abs(s.RoundingAdjustment) > s.GrandTotalTtc) return "Cash rounding larger than the sale.";

        // Guard against a terminal with a corrupt total, allowing one smallest currency unit
        // for rounding. Discounts are always distributed onto the lines before they reach
        // here, so LineTotalTtc is already net and DiscountTotal is reported for analytics only.
        // Deliberately no check against the current tax rates or payment methods: the sale
        // already happened, possibly before the operator changed the profile.
        var lineSum = s.Lines.Sum(l => l.LineTotalTtc);
        if (Math.Abs(lineSum - s.GrandTotalTtc) > tolerance)
            return $"Total mismatch: lines {lineSum:0.###} != total {s.GrandTotalTtc:0.###}.";

        var taxSum = s.Lines.Sum(l => l.LineTaxAmount);
        if (Math.Abs(taxSum - s.TaxTotal) > tolerance)
            return $"Tax mismatch: lines {taxSum:0.###} != declared {s.TaxTotal:0.###}.";

        if (!Enum.TryParse<SaleStatus>(s.Status, true, out _)) return "Unknown sale status.";

        // Tenders are no longer a closed set — an operator can define a postal transfer — so
        // the check is on shape, not membership. A name the server does not recognise comes
        // back as a warning on an accepted sale rather than a rejection, the same way an
        // unknown gift card does: the money has already changed hands at the counter and
        // refusing the record would only lose it.
        if (s.Payments.Any(p => string.IsNullOrWhiteSpace(p.Method) && string.IsNullOrWhiteSpace(p.MethodKey)))
            return "A payment line needs a method.";
        // The same rule the quantities above follow, and for the same reason: a refund is
        // expressed by Status, never by a negative sign. A negative tender is not something a
        // counter produces, and each behaviour turns one into a way of moving money the wrong
        // way — a negative credit line cancels a customer's debt, a negative cash line empties
        // the branch's drawer, a negative loyalty line inflates the points a sale earns.
        if (s.Payments.Any(p => p.Amount < 0)) return "A payment line cannot be negative.";
        if (s.Payments.Any(p => (p.MethodKey ?? p.Method).Length > 40))
            return "Payment method names are limited to 40 characters.";
        if (s.TaxMode is not null && !Enum.TryParse<TaxMode>(s.TaxMode, true, out _))
            return "Unknown tax mode.";

        return null;
    }

    /// <summary>
    /// Turns one pushed payment line into a stored payment, resolving the tender it was taken
    /// under and freezing what that tender meant at the time.
    /// <para>
    /// The behaviour and the confirmation rule are copied onto the row rather than looked up
    /// later, for the same reason <see cref="SaleLine.NameSnapshot"/> exists: renaming a
    /// tender, or deciding next year that transfers need confirming, must not retroactively
    /// change what happened at the counter today.
    /// </para>
    /// </summary>
    public static SalePayment BuildPayment(
        Guid saleId, SalePaymentPush p,
        IReadOnlyDictionary<string, PaymentMethodDef> defs, List<string> warnings)
    {
        // Older tills send only the built-in name; newer ones send the definition key.
        var key = string.IsNullOrWhiteSpace(p.MethodKey) ? p.Method : p.MethodKey!;
        key = key.Trim();

        defs.TryGetValue(key, out var def);
        if (def is null)
        {
            // Never rejected — the customer has already paid and walked out. The sale is
            // recorded exactly as pushed and the discrepancy is surfaced for someone to fix.
            warnings.Add($"Unknown payment method '{key}'; recorded as entered.");
        }

        var behavior = def?.Behavior
            ?? (Enum.TryParse<PaymentMethod>(key, true, out var builtIn)
                ? ToBehavior(builtIn)
                : PaymentBehavior.Generic);

        // An unknown tender is treated as needing confirmation. If the server cannot say what
        // it is, it certainly cannot vouch that the money arrived.
        var pending = def?.RequiresConfirmation ?? true;

        return new SalePayment
        {
            Id = p.Id == Guid.Empty ? Guid.NewGuid() : p.Id,
            SaleId = saleId,
            Method = Enum.TryParse<PaymentMethod>(key, true, out var legacy) ? legacy : PaymentMethod.Mixed,
            MethodKey = key.Length > 40 ? key[..40] : key,
            Behavior = behavior,
            Status = pending ? PaymentStatus.Pending : PaymentStatus.Confirmed,
            ConfirmedAtUtc = pending ? null : DateTime.UtcNow,
            Amount = p.Amount,
            Reference = Trim(p.Reference, 100),
            InstrumentNumber = Trim(p.InstrumentNumber, 60),
            InstrumentBank = Trim(p.InstrumentBank, 120),
            InstrumentDueDate = p.InstrumentDueDate
        };

        static string? Trim(string? value, int max) =>
            value is { Length: > 0 } && value.Length > max ? value[..max] : value;
    }

    private static PaymentBehavior ToBehavior(PaymentMethod method) => method switch
    {
        PaymentMethod.Cash => PaymentBehavior.Cash,
        PaymentMethod.Card => PaymentBehavior.Card,
        PaymentMethod.Credit => PaymentBehavior.Credit,
        PaymentMethod.GiftCard => PaymentBehavior.GiftCard,
        PaymentMethod.Loyalty => PaymentBehavior.Loyalty,
        _ => PaymentBehavior.Generic
    };

    /// <summary>Writes one sale plus its stock and balance side effects in a single transaction.</summary>
    private async Task<string?> PersistSaleAsync(SalePush push, string deviceCode, Guid branchId, SyncActor actor, CancellationToken ct)
    {
        var isRefund = string.Equals(push.Status, nameof(SaleStatus.Refunded), StringComparison.OrdinalIgnoreCase);
        var warnings = new List<string>();

        await using var tx = await db.BeginWriteAsync(ct);

        // Before anything is read: the stock, the customer's debt, the gift card, the points and
        // the drawer are all read, changed in memory and written back, and another till's push
        // may be doing the same to the same rows. Under the version lock it commits first or
        // waits. (The tracker is cleared after every sale, so nothing here is an older copy.)
        await db.LockVersionsAsync(ct);

        // Tender definitions decide what each payment means: what it does, and whether the
        // money has actually arrived. Loaded once rather than per payment line.
        var defs = (await db.PaymentMethodDefs.AsNoTracking().ToListAsync(ct))
            .ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);

        // The till's clock (security review, 2026-10-10). A sale cannot have happened after it
        // reached the server: a date in the future would put its revenue and tax into a period not
        // yet reported, so it is recorded as of its arrival. A date far in the past is kept — a till
        // offline for weeks is real — and said, since it lands in a period that may be closed.
        var receivedAtUtc = DateTime.UtcNow;
        var soldAtUtc = push.SoldAtUtc;
        if (soldAtUtc > receivedAtUtc + FutureSaleSlack)
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"The till dated this sale {soldAtUtc:yyyy-MM-dd HH:mm} UTC, after it arrived; recorded as of its arrival."));
            logger.LogWarning("Sale {SaleId} from {Device} dated {SoldAt:o} in the future; recorded at {Received:o}", push.Id, deviceCode, soldAtUtc, receivedAtUtc);
            soldAtUtc = receivedAtUtc;
        }
        else if (soldAtUtc < receivedAtUtc - OldSaleAge)
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"This sale is dated {soldAtUtc:yyyy-MM-dd}, more than {OldSaleAge.TotalDays:0} days before it arrived."));
            logger.LogWarning("Sale {SaleId} from {Device} dated {SoldAt:o}, {Days:0} days before it arrived", push.Id, deviceCode, soldAtUtc, (receivedAtUtc - soldAtUtc).TotalDays);
        }

        var sale = new Sale
        {
            Id = push.Id,
            Reference = push.Reference,
            DeviceCode = string.IsNullOrWhiteSpace(deviceCode) ? "UNK" : deviceCode,
            BranchId = branchId,
            LocalSeq = push.LocalSeq,
            SoldAtUtc = soldAtUtc,
            ReceivedAtUtc = receivedAtUtc,
            CustomerId = push.CustomerId,
            CashierUserId = push.CashierUserId,
            CashierName = push.CashierName,
            SubTotalHt = push.SubTotalHt,
            TaxTotal = push.TaxTotal,
            DiscountTotal = push.DiscountTotal,
            GrandTotalTtc = push.GrandTotalTtc,
            RoundingAdjustment = push.RoundingAdjustment,
            PaidAmount = push.PaidAmount,
            ChangeAmount = push.ChangeAmount,
            PaymentMethod = Enum.TryParse<PaymentMethod>(push.PaymentMethod, true, out var summaryMethod)
                ? summaryMethod
                // A sale settled entirely with an operator-defined tender has no built-in
                // summary to send. Mixed is the honest answer — it says "see the payment
                // lines", which is exactly where the detail now lives.
                : PaymentMethod.Mixed,
            Status = Enum.Parse<SaleStatus>(push.Status, true),
            TaxMode = Enum.TryParse<TaxMode>(push.TaxMode, true, out var taxMode) ? taxMode : TaxMode.Taxed,
            RefundOfSaleId = push.RefundOfSaleId,
            Note = push.Note,
            Lines = push.Lines!.Select(l => new SaleLine
            {
                Id = l.Id == Guid.Empty ? Guid.NewGuid() : l.Id,
                SaleId = push.Id,
                ProductId = l.ProductId,
                Sku = l.Sku,
                NameSnapshot = l.NameSnapshot,
                Qty = l.Qty,
                UnitPrice = l.UnitPrice,
                TaxRate = l.TaxRate,
                LineDiscount = l.LineDiscount,
                LineTotalTtc = l.LineTotalTtc,
                LineTaxAmount = l.LineTaxAmount
            }).ToList(),
            Payments = push.Payments!.Select(p => BuildPayment(push.Id, p, defs, warnings)).ToList()
        };

        // Under the version lock, before anything moves: who the sale belongs to, and — for a
        // refund — that it stands on a real sale and asks for no more than is left of it.
        await ResolveCashierAsync(sale, branchId, actor, warnings, ct);
        if (isRefund) await CheckRefundAsync(sale, branchId, regional.Get().MoneyTolerance(), ct);

        db.Sales.Add(sale);

        // ---------- stock ----------
        var lines = push.Lines!;
        var productIds = lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await db.Products.Where(p => productIds.Contains(p.Id)).ToListAsync(ct);
        WarnOnCatalogueDifferences(sale, products, regional.Get().MoneyTolerance(), warnings);
        if (!isRefund) await RecordNotForSaleAsync(sale, products, actor, warnings, ct);
        // What the goods cost (C-01): null while sales are costed at the cost price, today's rule and the default.
        // Read under the version lock and before this sale's own movements, so it is the average of the moment.
        var live = await LiveCostsAsync(sale, products, isRefund, branchId, ct);
        // Read under the version lock: another till's push for the same article commits first or
        // waits, and never writes back a figure this sale has already moved.
        var stocks = new StockRows(db);
        var touched = new List<SyncEntity>();

        foreach (var line in lines)
        {
            var product = products.FirstOrDefault(p => p.Id == line.ProductId);
            if (product is null || !product.TrackStock) continue;

            var stock = await stocks.GetAsync(product.Id, branchId, ct);

            // A refund puts stock back; a sale takes it out. Never blocked or rejected for a
            // shortfall — a sale that physically happened cannot be refused after the fact —
            // only flagged so the back office can see it happened.
            var delta = isRefund ? line.Qty : -line.Qty;
            stock.StockQty += delta;
            if (stock.StockQty < 0 && !sale.StockWarning)
            {
                sale.StockWarning = true;
                warnings.Add("Stock went negative for one or more lines.");
            }

            touched.Add(product);

            db.StockMovements.Add(new StockMovement
            {
                ProductId = product.Id,
                BranchId = branchId,
                QtyDelta = delta,
                BalanceAfter = stock.StockQty,
                Reason = isRefund ? "refund" : "sale",
                Kind = isRefund ? StockMovementKind.Refund : StockMovementKind.Sale,
                // The cost the entry below posts for it (P0.4a): the cost price, or the average once sales are switched to it (C-01).
                UnitCost = live is not null && live.TryGetValue(product.Id, out var liveCost) ? liveCost.UnitCost : product.CostPrice,
                SaleId = sale.Id,
                UserId = sale.CashierUserId,
                UserName = sale.CashierName,
                DocumentNumber = sale.Reference,
                MovedQty = Math.Abs(delta)
            });
        }

        // ---------- customer credit ----------
        // The debt is whatever was not settled at the till, which covers a mixed payment
        // (part cash, part credit) as well as a pure credit sale.
        var creditAmount = sale.Payments
            .Where(p => p.Behavior == PaymentBehavior.Credit)
            .Sum(p => p.Amount);
        // An older till sends only PaymentMethod = Credit with no line to match, so the debt is
        // whatever the tenders did not cover. Derived from the payment lines — which the push
        // does validate against the sale total — and not from PaidAmount, which nothing checks
        // at all: a till reporting an absurd "paid" figure could otherwise clear a customer's
        // entire balance in one push, and the ledger, which never reads PaidAmount, would not
        // agree with the figure the business actually chases.
        if (creditAmount == 0 && sale.PaymentMethod == PaymentMethod.Credit)
            creditAmount = sale.GrandTotalTtc - sale.Payments.Sum(p => p.Amount);

        // A tender that has not cleared is not a payment yet. A cheque handed over at the
        // counter leaves the customer owing the money until it is collected, so it adds to
        // the debt exactly like a credit line and is subtracted again on confirmation.
        // Reducing a balance against paper is how a credit limit ends up extended on money
        // that never arrived.
        creditAmount += sale.Payments
            .Where(p => p.Behavior != PaymentBehavior.Credit && p.Status == PaymentStatus.Pending)
            .Sum(p => p.Amount);

        // A sale raises a debt or it raises none; it never pays one off. Settling is what the
        // treasury routes are for, and they are back-office and role-gated precisely because
        // deciding that money arrived is not a counter action.
        creditAmount = Math.Max(0m, creditAmount);

        if (sale.CustomerId is Guid customerId && creditAmount != 0)
        {
            var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId, ct);
            if (customer is not null)
            {
                customer.Balance += isRefund ? -creditAmount : creditAmount;
                if (customer.CreditLimit > 0 && customer.Balance > customer.CreditLimit)
                    warnings.Add($"Customer credit limit exceeded ({customer.Balance:0.##} > {customer.CreditLimit:0.##}).");
                touched.Add(customer);
            }
        }

        // ---------- cash box ----------
        // Only physical cash moves a drawer. A card payment is at the bank, a cheque has not
        // cleared, credit is a debt and points are not money — none of them belong here, and
        // including any of them would put a permanent surplus in the branch's cash position.
        var cashTaken = sale.Payments
            .Where(p => p.Behavior == PaymentBehavior.Cash && p.Status == PaymentStatus.Confirmed)
            .Sum(p => p.Amount);
        // Change handed back came out of the same drawer, so the drawer only gained the net.
        // The same figure the ledger uses, clamped in one place, so the two cannot disagree.
        var changeGiven = SalePosting.ChangeFromDrawer(sale, cashTaken);
        if (changeGiven != (isRefund ? 0m : sale.ChangeAmount))
            warnings.Add($"Change of {sale.ChangeAmount:0.##} is not possible against {cashTaken:0.##} in cash; recorded as {changeGiven:0.##}.");
        var cashNet = cashTaken - changeGiven;

        if (cashNet != 0)
        {
            var box = await db.CashBoxes.FirstOrDefaultAsync(x => x.BranchId == branchId && x.IsActive, ct);
            if (box is not null)
            {
                var running = await CashBoxBalance.ForWriteAsync(db, box.Id, ct);
                var delta = isRefund ? -cashNet : cashNet;

                db.CashMovements.Add(new CashMovement
                {
                    CashBoxId = box.Id,
                    Direction = delta >= 0 ? CashDirection.In : CashDirection.Out,
                    Amount = Math.Abs(delta),
                    BalanceAfter = running + delta,
                    Kind = isRefund ? CashMovementKind.Refund : CashMovementKind.Sale,
                    SourceType = "sale",
                    SourceId = sale.Id,
                    UserId = sale.CashierUserId,
                    UserName = sale.CashierName,
                    Reference = sale.Reference,
                    OccurredAtUtc = sale.SoldAtUtc
                });
            }
        }

        // ---------- gift cards and loyalty ----------
        touched.AddRange(await new LoyaltyLedger(db).ApplyAsync(sale, isRefund, warnings, ct));

        // ---------- the ledger ----------
        // Same transaction as the sale, so a sale can never exist without its posting. The
        // posting is idempotent on the sale's terminal-generated id for the same reason the
        // sale itself is: a till re-pushes its outbox whenever it reconnects, and a re-pushed
        // sale must not be recognised as revenue twice.
        //
        // Cost comes from the catalog at the moment of posting rather than from the till,
        // which never knew it. Services and anything nobody has costed contribute nothing,
        // rather than a guess.
        // Once sales are switched to the weighted average (C-01), the average of the moment instead, and a refund
        // under the refund setting (P0.4c); the same "tracked, nothing to post" rule flags a line costed at zero.
        var tracked = live?.ToDictionary(x => x.Key, x => x.Value.UnitCost)
            ?? products.Where(p => p.TrackStock).ToDictionary(p => p.Id, p => p.CostPrice);
        var unitCosts = tracked.Where(x => x.Value != 0).ToDictionary(x => x.Key, x => x.Value);
        // Goods that leave stock with no cost are sold all the same (SAL-02: never blocked) and
        // flagged, line by line, so the incomplete margin can be reported and the cost corrected.
        // The same rule as above, read the other way: tracked, and nothing to post.
        foreach (var line in sale.Lines)
        {
            // What the entry posts for the line (P0.4a): the cost, 0 when it is missing, nothing for a service.
            line.UnitCost = tracked.TryGetValue(line.ProductId, out var cost) ? cost : null;
            line.CostMissing = line.UnitCost == 0m;
            line.CostBasis = live is not null && live.TryGetValue(line.ProductId, out var basis) ? basis.Basis : SaleCostBasis.CostPrice;
        }
        // Cash rounding is at most one step of the profile (security review, 2026-10-10): a till
        // that "rounded" a whole sale to zero let the goods leave with nothing due and the ledger
        // balanced on the rounding account. Anything beyond the step is posted to suspense, where a
        // trial balance shows it, and said; the sale itself is kept as the till recorded it.
        var profile = regional.Get();
        var roundingCap = Math.Max(profile.CashRoundingIncrement, profile.MoneyTolerance());
        if (Math.Abs(sale.RoundingAdjustment) > roundingCap)
        {
            warnings.Add($"Cash rounding of {sale.RoundingAdjustment:0.###} is more than the rounding step ({roundingCap:0.###}); the excess is in suspense.");
            logger.LogWarning("Sale {SaleId} from {Device}: cash rounding {Rounding} beyond the step {Step}", sale.Id, deviceCode, sale.RoundingAdjustment, roundingCap);
        }
        var posting = SalePosting.Build(sale, unitCosts, roundingCap);
        // Rule 1 over CLO-01: a sale is never refused, so one dated inside closed books — a till that was offline —
        // keeps its own date on the receipt and in the sales, and only its entry is booked on the first open day
        // (owner, 2026-11-01; the accountant confirms under SAL-14).
        if (await BookClosings.CurrentAsync(db, ct) is { OpenFromUtc: DateTime openFrom, ClosedThrough: DateOnly closedThrough }
            && posting.EntryDate < openFrom)
        {
            posting = posting.DatedOn(openFrom,
                $"{posting.Description} — sold {sale.SoldAtUtc:yyyy-MM-dd}, books closed through {closedThrough:yyyy-MM-dd}: booked on the first open day");
            warnings.Add($"The books are closed through {closedThrough:yyyy-MM-dd}: this sale is booked on the first open day.");
        }
        await ledger.PostAsync(posting, ct);

        foreach (var entity in touched.Distinct()) await db.TouchAsync(entity, ct);

        // Same transaction as the sale: the event exists exactly when the sale does.
        if (queueWebhook)
            db.IntegrationOutbox.Add(new IntegrationOutbox
            {
                SaleId = sale.Id,
                EventType = SaleEvents.SaleAccepted,
                Payload = SaleEvents.SaleAcceptedPayload(integrations.CompanyId, sale)
            });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return warnings.Count == 0 ? null : string.Join(" ", warnings);
    }

    /// <summary>The live average for this request's sales (C-01), kept for the whole push batch: see <see cref="LiveSaleCosting"/>.</summary>
    private LiveSaleCosting LiveCosting => liveCosting ??= new LiveSaleCosting(db);
    private LiveSaleCosting? liveCosting;

    /// <summary>
    /// The cost per unit of each stock-tracked product of the sale under the weighted average, or null while sales
    /// are costed at the cost price (C-01, off by default). <b>Never throws:</b> a sale that physically happened is
    /// never refused because its cost could not be worked out (rule 1) — every line is then costed at the cost
    /// price and marked <see cref="SaleCostBasis.CostPriceFallback"/>, so the fallback shows on the line.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, LiveLineCost>?> LiveCostsAsync(
        Sale sale, List<Product> products, bool isRefund, Guid branchId, CancellationToken ct)
    {
        var tracked = products.Where(p => p.TrackStock).ToDictionary(p => p.Id);
        var moved = sale.Lines.Where(l => tracked.ContainsKey(l.ProductId))
            .GroupBy(l => l.ProductId)
            .ToDictionary(g => g.Key, g => (Qty: g.Sum(l => l.Qty), tracked[g.Key].CostPrice));
        try
        {
            return await LiveCosting.CostsAsync(branchId, isRefund, sale.RefundOfSaleId, moved, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LiveCosting.Reset();
            logger.LogError(ex, "Sale {SaleId}: the weighted average could not be worked out; its lines are costed at the cost price", sale.Id);
            return moved.ToDictionary(x => x.Key, x => new LiveLineCost(x.Value.CostPrice, SaleCostBasis.CostPriceFallback));
        }
    }

    /// <summary>
    /// Ids only in the log: a <see cref="SalePush"/> carries customer ids and payment
    /// references, and the log is not the place for them. The exception itself is logged in
    /// full — that is where the provider's words belong, under the reference the till shows.
    /// </summary>
    private PushRejection Explain(Exception ex, Guid saleId, string device)
    {
        var rejection = PushRejection.Classify(ex);
        if (rejection.Reference is not null)
            logger.LogError(ex, "Sale {SaleId} from {Device} rejected; reference {Reference}", saleId, device, rejection.Reference);
        else
            logger.LogWarning(ex, "Sale {SaleId} from {Device} rejected as {Code}", saleId, device, rejection.Code);
        return rejection;
    }

    // ---------- mapping ----------

    public static StoreDto Map(Store s) => new(
        s.Id, s.NameAr, s.NameFr, s.NameEn, s.AddressFr, s.Phone, s.FiscalIds, s.ReceiptFooterFr,
        s.RequireCustomerEmail, s.RequireCustomerBirthDate, s.RequireCustomerPhone,
        s.RequireCustomerAddress, s.RequireCustomerTaxId,
        s.Version, s.IsDeleted,
        s.LoyaltyEnabled, s.LoyaltyPointsPerUnit, s.LoyaltyPointValue, s.LoyaltyMinRedeemPoints,
        s.UiFont, s.AddressAr, s.AddressFr, s.AddressEn,
        s.ReceiptFooterAr, s.ReceiptFooterFr, s.ReceiptFooterEn);

    /// <summary>The back office's card: the code itself, for the screens that issue and look cards up.</summary>
    public static GiftCardDto MapForOffice(GiftCard g) => Map(g) with { Code = g.Code };

    /// <summary>A till's card: a hint and the code's hash, never the code (security review, 2026-10-10).</summary>
    public static GiftCardDto Map(GiftCard g) => new(
        g.Id, GiftCardRules.CodeHint(g.Code), g.InitialAmount, g.Balance, g.IsActive, g.ExpiresAtUtc, g.CustomerId,
        g.Version, g.IsDeleted, GiftCardRules.CodeHash(g.Code));

    public static PaymentMethodDto Map(PaymentMethodDef m) => new(
        m.Id, m.Key, m.NameAr, m.NameFr, m.NameEn, m.Behavior.ToString(),
        m.RequiresConfirmation, m.AffectsCashDrawer, m.RequiresReference, m.RequiresInstrumentDetails,
        m.IsBuiltIn, m.IsActive, m.SortOrder, m.Version, m.IsDeleted);

    private static BranchDto Map(Branch b) => new(
        b.Id, b.NameAr, b.NameFr, b.NameEn, b.AddressFr, b.Phone, b.IsMain, b.AllowNegativeStock,
        b.IsActive, b.Version, b.IsDeleted, b.IsOpenForOperation, b.AddressAr, b.AddressFr, b.AddressEn,
        // The till's receipt prints the branch's own identifiers (owner, 2026-10-20).
        FiscalIds: b.FiscalIds,
        LogoUrl: StoreLogo.BranchUrl(b.Id, b.LogoPath), IsWarehouse: b.IsWarehouse, Code: b.Code);

    private static CategoryDto Map(Category c) => new(
        c.Id, c.NameAr, c.NameFr, c.NameEn, c.Color, c.SortOrder, c.Version, c.IsDeleted);

    private static ProductDto Map(Product p, ProductStock? stock) => new(
        p.Id, p.Sku, p.Barcode, p.NameAr, p.NameFr, p.NameEn, p.CategoryId, p.Price, p.TaxRate,
        p.Unit, p.TrackStock, stock?.StockQty ?? 0, stock?.ReorderLevel ?? 0, p.IsActive,
        p.Version, p.IsDeleted, p.IsRentable, p.AssetUnitValue,
        HiddenAtTill: !ManufacturingRoles.ForSale(p));

    // Kind, IdDocumentNumber and the representative history stay off the till on purpose:
    // nothing on a terminal reads them (register.component.ts uses fullName and loyaltyPoints,
    // receipt.component.ts prints fullName), and a replicated field nothing reads is dead
    // wiring. The day a receipt has to say شركة, append Kind — and only Kind — to CustomerDto
    // as a trailing defaulted parameter, here and in models.ts, together with that consumer.
    private static CustomerDto Map(Customer c) => new(
        c.Id, c.Code, c.FullName, c.Phone, c.Address, c.TaxId, c.Email, c.BirthDate,
        c.CreditLimit, c.Balance, c.Version, c.IsDeleted, c.LoyaltyPoints);

    private static PosUserDto Map(User u) => new(
        u.Id, u.Username, u.FullName, u.Role.ToString(), u.PinHash, u.IsActive, u.Version, u.IsDeleted);
}

/// <summary>Who pushed a batch, as the token says: what they may do, and who they are.</summary>
public sealed record SyncActor(Guid? UserId, string? UserName, bool CanSell, bool CanRefund)
{
    /// <summary>For the tests that drive the service directly; the endpoint always passes the caller.</summary>
    public static readonly SyncActor Unchecked = new(null, null, true, true);
}

/// <summary>A pushed refund refused for what it asks, with its own code.</summary>
public sealed class RefundRefusedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
