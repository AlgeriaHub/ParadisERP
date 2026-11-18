using System.Security.Claims;
using AlgeriaPos.API.Subscriptions;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Receivables;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Authorization;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.API.Endpoints;

/// <summary>
/// Money as opposed to goods: which tenders exist, whether the money behind a payment has
/// actually arrived, and where the cash physically is.
/// <para>
/// These are deliberately back-office routes. A till takes payments; deciding that a cheque
/// cleared, or that the manager collected the day's takings, is not a counter action and
/// should not be reachable from one.
/// </para>
/// </summary>
public static class TreasuryEndpoints
{
    public static void MapTreasuryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/treasury").WithTags("Treasury")
            .AddEndpointFilter<SubscriptionWriteFilter>()
            .AtCallersBranch();
        // The reads a voucher form needs (answer 25): a cash-box holder picks a customer, a branch
        // and a payment method like a manager does, and edits none of them.
        var cash = app.MapGroup("/api/treasury").WithTags("Treasury")
            .AddEndpointFilter<SubscriptionWriteFilter>()
            .AtCallersBranch();

        MapPaymentMethodReads(cash);
        MapPaymentMethods(group);
        MapPendingPayments(group);
        MapCashBoxes(group);
        FinancePageRoutes.MapChequesPage(group); FinancePageRoutes.MapAwaitingCollectionPage(group); FinancePageRoutes.MapCashMovementsPage(group);
    }

    // ---------- payment methods ----------

    private static void MapPaymentMethodReads(RouteGroupBuilder group)
    {
        group.MapGet("/payment-methods", async (PosDbContext db, CancellationToken ct) =>
        {
            var defs = await db.PaymentMethodDefs.AsNoTracking()
                .Where(m => !m.IsDeleted)
                .OrderBy(m => m.SortOrder).ThenBy(m => m.Key)
                .ToListAsync(ct);
            return Results.Ok(defs.Select(SyncService.Map));
        });
    }

    private static void MapPaymentMethods(RouteGroupBuilder group)
    {
        group.MapPost("/payment-methods", async (
            UpsertPaymentMethodRequest r, PosDbContext db, CancellationToken ct) =>
        {
            var key = (r.Key ?? "").Trim().ToLowerInvariant();
            if (key.Length is 0 or > 40) return Results.BadRequest("A key of 1 to 40 characters is required.");
            if (!key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                return Results.BadRequest("Keys use lower-case letters, digits, '-' and '_'.");
            if (await db.PaymentMethodDefs.AnyAsync(m => m.Key == key, ct))
                return Results.Conflict($"A payment method with the key '{key}' already exists.");
            if (string.IsNullOrWhiteSpace(r.NameFr)) return Results.BadRequest("A French name is required.");

            var def = new PaymentMethodDef
            {
                Key = key,
                // An operator-added tender never carries programmed behaviour. Letting one
                // claim to be Credit or GiftCard would hand it side effects — moving a
                // customer balance, spending a card — that nothing else knows about.
                Behavior = PaymentBehavior.Generic,
                IsBuiltIn = false
            };
            Apply(def, r);
            db.PaymentMethodDefs.Add(def);
            await db.SaveWithVersionsAsync(ct, def);
            return Results.Created($"/api/treasury/payment-methods/{def.Id}", SyncService.Map(def));
        });

        group.MapPut("/payment-methods/{id:guid}", async (
            Guid id, UpsertPaymentMethodRequest r, PosDbContext db, CancellationToken ct) =>
        {
            var def = await db.PaymentMethodDefs.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (def is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(r.NameFr)) return Results.BadRequest("A French name is required.");

            // Cash is the only tender that needs no device and no confirmation; a company
            // that cannot take cash cannot trade, so it stays enabled.
            if (def.IsBuiltIn && def.Behavior == PaymentBehavior.Cash && !r.IsActive)
                return Results.BadRequest("Cash cannot be disabled.");

            Apply(def, r);
            await db.SaveWithVersionsAsync(ct, def);
            return Results.Ok(SyncService.Map(def));
        });

        group.MapDelete("/payment-methods/{id:guid}", async (Guid id, PosDbContext db, CancellationToken ct) =>
        {
            var def = await db.PaymentMethodDefs.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (def is null) return Results.NotFound();
            // Built-ins are dispatched on by behaviour. Disable one, never remove it.
            if (def.IsBuiltIn) return Results.BadRequest("Built-in payment methods can be disabled but not deleted.");
            if (await db.SalePayments.AnyAsync(p => p.MethodKey == def.Key, ct))
                return Results.BadRequest("This method has been used on a sale; disable it instead.");

            def.IsDeleted = true;
            def.IsActive = false;
            await db.SaveWithVersionsAsync(ct, def);
            return Results.NoContent();
        });

        static void Apply(PaymentMethodDef def, UpsertPaymentMethodRequest r)
        {
            def.NameAr = (r.NameAr ?? "").Trim();
            def.NameFr = (r.NameFr ?? "").Trim();
            def.NameEn = (r.NameEn ?? "").Trim();
            def.RequiresConfirmation = r.RequiresConfirmation;
            def.RequiresReference = r.RequiresReference;
            def.RequiresInstrumentDetails = r.RequiresInstrumentDetails;
            def.IsActive = r.IsActive;
            def.SortOrder = r.SortOrder;
            // Only physical cash reaches the drawer. Letting a cheque claim otherwise would
            // put a shortfall in the Z-report on every day one is taken.
            def.AffectsCashDrawer = def.Behavior == PaymentBehavior.Cash;
        }
    }

    // ---------- pending payments (cheques and transfers) ----------

    private static void MapPendingPayments(RouteGroupBuilder group)
    {
        // The screen that stops money being forgotten: what is outstanding, oldest due first.
        group.MapGet("/pending-payments", async (
            string? status, Guid? branchId, PosDbContext db, CancellationToken ct) =>
        {
            var wanted = Enum.TryParse<PaymentStatus>(status, true, out var parsed) ? parsed : PaymentStatus.Pending;

            var rows = await db.SalePayments.AsNoTracking()
                .Where(p => p.Status == wanted)
                .Join(db.Sales.AsNoTracking(), p => p.SaleId, s => s.Id, (p, s) => new { p, s })
                .Where(x => branchId == null || x.s.BranchId == branchId)
                .OrderBy(x => x.p.InstrumentDueDate ?? x.s.SoldAtUtc)
                .Take(500)
                .ToListAsync(ct);

            var customerIds = rows.Where(x => x.s.CustomerId is not null).Select(x => x.s.CustomerId!.Value).Distinct().ToList();
            var customers = await db.Customers.AsNoTracking()
                .Where(c => customerIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.FullName, ct);

            return Results.Ok(rows.Select(x => new PendingPaymentDto(
                x.p.Id, x.s.Id, x.s.Reference, x.s.SoldAtUtc,
                x.s.CustomerId, x.s.CustomerId is Guid cid ? customers.GetValueOrDefault(cid) : null,
                x.p.MethodKey, x.p.Amount, x.p.Status.ToString(),
                x.p.InstrumentNumber, x.p.InstrumentBank, x.p.InstrumentDueDate,
                x.p.ConfirmedAtUtc, x.p.StatusNote)));
        });

        group.MapPost("/pending-payments/{id:guid}/settle", async (
            Guid id, SettlePaymentRequest r, PosDbContext db, LedgerPoster ledger, HttpContext http,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<PaymentStatus>(r.Status, true, out var target) || target == PaymentStatus.Pending)
                return Results.BadRequest("Settle a payment as Confirmed or Bounced.");
            // A bounce says why (FR-2B): the reason is the note this route always stored.
            if (ReceivableRules.SettlementProblem(target, r.Note) is string problem)
                return Results.BadRequest(problem);

            // Read under the version lock: two clicks on Confirm cannot both find it pending and
            // take the money off the debt twice, and the customer's debt is the one as it stands.
            await using var tx = await db.BeginWriteScopeAsync(ct);
            await db.LockVersionsAsync(ct);

            var payment = await db.SalePayments.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (payment is null) return Results.NotFound();
            if (payment.Status != PaymentStatus.Pending)
                return Results.BadRequest($"This payment is already {payment.Status}.");

            var sale = await db.Sales.FirstOrDefaultAsync(s => s.Id == payment.SaleId, ct);

            // A cheque taken at the till needs a second person to confirm it (owner rule,
            // docs/owner-notes.md, 2026-10-11, answer 4). A bounce is never refused.
            var callerUserId = Guid.TryParse(http.User.FindFirstValue("sub"), out var uid) ? uid : (Guid?)null;
            if (target == PaymentStatus.Confirmed
                && !ServerAuthorization.Has(http.User, PermissionCatalog.ReceiptsSettleOwn)
                && sale is not null && callerUserId == sale.CashierUserId)
            {
                return Results.Problem(
                    $"Sale {sale.Reference} was taken by you; someone else confirms its payment.",
                    statusCode: 409,
                    extensions: new Dictionary<string, object?> { ["code"] = "confirm_own_forbidden" });
            }

            payment.Status = target;
            payment.StatusNote = r.Note is { Length: > 400 } note ? note[..400] : r.Note;
            payment.ConfirmedAtUtc = DateTime.UtcNow;

            // The debt was raised when the sale was pushed, because a pending tender is not
            // a payment. Confirming it is the moment the money actually arrived, so that is
            // when the balance comes down. A bounce leaves the debt exactly where it was —
            // nothing arrived, and nothing about the customer's position has changed.
            if (target == PaymentStatus.Confirmed && sale?.CustomerId is Guid customerId)
            {
                var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId, ct);
                if (customer is not null)
                {
                    var settled = sale.Status == SaleStatus.Refunded ? -payment.Amount : payment.Amount;
                    customer.Balance -= settled;
                    await db.TouchAsync(customer, ct);
                }
            }

            // ⚠ A cheque clearing moves a receivable; it does not create revenue — see
            // TreasuryPosting.Settlement.
            await ledger.PostAsync(TreasuryPosting.Settlement(
                payment, target, sale?.BranchId ?? Guid.Empty,
                http.User.FindFirstValue("name") ?? http.User.Identity?.Name, sale?.CustomerId), ct);

            // Cash never arrives this way — a confirmed cheque is money at the bank, not in
            // the branch drawer — so no cash movement is written here.
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Results.Ok(new { payment.Id, Status = payment.Status.ToString() });
        }).ForBranchOf(DocumentBranches.SalePayment);
    }

    // ---------- cash boxes and remittance ----------

    private static void MapCashBoxes(RouteGroupBuilder group)
    {
        group.MapGet("/cash-boxes", async (Guid? branchId, PosDbContext db, CancellationToken ct) =>
        {
            // branchId is the caller's own branch for everyone but the Admin (CallersBranch): their
            // branch's boxes, and the main boxes as the places takings are remitted to — named only,
            // their balance is not another branch's to read, so it comes back as 0.
            var boxes = await db.CashBoxes.AsNoTracking().Where(x => x.IsActive)
                .Where(x => branchId == null || x.BranchId == branchId || x.IsMain)
                .OrderByDescending(x => x.IsMain).ThenBy(x => x.NameFr)
                .ToListAsync(ct);

            // Decimal aggregates run client-side: SQLite stores decimal as TEXT, so summing
            // server-side would compare strings. The same rule the rest of this codebase follows.
            var movements = await db.CashMovements.AsNoTracking()
                .Select(m => new { m.CashBoxId, m.Direction, m.Amount })
                .ToListAsync(ct);

            var balances = movements
                .GroupBy(m => m.CashBoxId)
                .ToDictionary(g => g.Key, g => g.Sum(m => m.Direction == CashDirection.In ? m.Amount : -m.Amount));

            return Results.Ok(boxes.Select(b => new CashBoxDto(
                b.Id, b.BranchId, b.NameAr, b.NameFr, b.NameEn, b.IsMain,
                branchId is null || b.BranchId == branchId ? balances.GetValueOrDefault(b.Id) : 0m)));
        });

        group.MapGet("/cash-boxes/{id:guid}/movements", async (
            Guid id, DateTime? from, DateTime? to, PosDbContext db, CancellationToken ct) =>
        {
            var query = db.CashMovements.AsNoTracking().Where(m => m.CashBoxId == id);
            if (from is not null) query = query.Where(m => m.OccurredAtUtc >= from);
            if (to is not null) query = query.Where(m => m.OccurredAtUtc <= to);

            var rows = await query.OrderByDescending(m => m.OccurredAtUtc).Take(500).ToListAsync(ct);
            return Results.Ok(rows.Select(m => new CashMovementDto(
                m.Id, m.CashBoxId, m.Direction.ToString(), m.Amount, m.BalanceAfter,
                m.Kind.ToString(), m.RelatedCashBoxId, m.UserName, m.Reference, m.Note, m.OccurredAtUtc)));
        }).ForBranchOf(DocumentBranches.CashBox);

        // The manager collecting a branch's takings. Two rows, one act.
        group.MapPost("/remittances", async (
            RemittanceRequest r, PosDbContext db, LedgerPoster ledger, HttpContext http, CancellationToken ct) =>
        {
            if (r.Amount <= 0) return Results.BadRequest("The amount must be positive.");
            if (r.FromCashBoxId == r.ToCashBoxId) return Results.BadRequest("Source and destination must differ.");

            var from = await db.CashBoxes.FirstOrDefaultAsync(x => x.Id == r.FromCashBoxId, ct);
            var to = await db.CashBoxes.FirstOrDefaultAsync(x => x.Id == r.ToCashBoxId, ct);
            if (from is null || to is null) return Results.NotFound("Cash box not found.");

            await using var tx = await db.BeginWriteScopeAsync(ct);

            // Under the version lock: two remittances out of one box cannot both pass the check
            // below against the same count, and each records the balance the other left.
            var fromBalance = await CashBoxBalance.ForWriteAsync(db, from.Id, ct);
            var toBalance = await CashBoxBalance.ForWriteAsync(db, to.Id, ct);

            // Reject before either cash movement or its ledger entry is created.
            if (r.Amount > fromBalance)
                return Results.BadRequest("The remittance amount exceeds the available cash box balance.");

            // Who collected it: a remittance that never reaches the safe needs a name on it.
            var userId = Guid.TryParse(http.User.FindFirstValue("sub"), out var uid) ? uid : (Guid?)null;
            var userName = http.User.FindFirstValue("name") ?? http.User.Identity?.Name;
            var reference = r.Reference is { Length: > 100 } longRef ? longRef[..100] : r.Reference;
            var movementId = Guid.NewGuid();

            db.CashMovements.Add(new CashMovement
            {
                CashBoxId = from.Id, Direction = CashDirection.Out, Amount = r.Amount,
                BalanceAfter = fromBalance - r.Amount, Kind = CashMovementKind.Remittance,
                RelatedCashBoxId = to.Id, SourceType = "remittance", SourceId = movementId,
                UserId = userId, UserName = userName, Reference = reference, Note = r.Note
            });
            db.CashMovements.Add(new CashMovement
            {
                CashBoxId = to.Id, Direction = CashDirection.In, Amount = r.Amount,
                BalanceAfter = toBalance + r.Amount, Kind = CashMovementKind.Remittance,
                RelatedCashBoxId = from.Id, SourceType = "remittance", SourceId = movementId,
                UserId = userId, UserName = userName, Reference = reference, Note = r.Note
            });

            // ⚠ The rule that must not be got wrong: a remittance is an asset moving between
            // two locations, not income at the safe and not an expense at the branch — see
            // TreasuryPosting.Remittance.
            await ledger.PostAsync(TreasuryPosting.Remittance(
                movementId, from.BranchId, to.BranchId, from.NameFr, to.NameFr,
                r.Amount, reference, userName), ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Results.Ok(new { Id = movementId, r.Amount, Warning = (string?)null });
        });
    }
}
