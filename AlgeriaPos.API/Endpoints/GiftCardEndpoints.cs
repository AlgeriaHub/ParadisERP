using System.Security.Claims;
using System.Security.Cryptography;
using AlgeriaPos.API.Subscriptions;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Loyalty;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Loyalty;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.API.Endpoints;

/// <summary>
/// Back-office gift cards and loyalty history. Balances change here (issue, adjust) and on
/// sale push; tills only read them. A card is sold for cash at a branch, and a paid top-up or a
/// cash refund of its balance moves that branch's box and the books (SAL-17, <see cref="GiftCardCash"/>).
/// </summary>
public static class GiftCardEndpoints
{
    // No 0/O/1/I, so a code read aloud or retyped is unambiguous.
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static void MapGiftCardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/catalog").WithTags("Gift cards")
            .AddEndpointFilter<SubscriptionWriteFilter>();

        group.MapGet("/gift-cards", async (string? q, PosDbContext db, CancellationToken ct) =>
        {
            var query = db.GiftCards.AsNoTracking().Where(g => !g.IsDeleted);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = GiftCardRules.NormalizeCode(q);
                query = query.Where(g => g.Code.Contains(term));
            }
            var cards = await query.OrderBy(g => g.Code).Take(500).ToListAsync(ct);
            return Results.Ok(cards.Select(SyncService.MapForOffice));
        });
        SalesPageRoutes.MapGiftCardPages(group);

        group.MapPost("/gift-cards", async (IssueGiftCardRequest r, PosDbContext db, IRegionalSettingsProvider regional,
            LedgerPoster ledger, ClaimsPrincipal caller, CancellationToken ct) =>
        {
            var amount = regional.Get().RoundMoney(r.Amount);
            if (amount <= 0) return Results.BadRequest("The amount must be positive.");
            var code = GiftCardRules.NormalizeCode(r.Code);
            if (code.Length == 0)
            {
                do code = RandomNumberGenerator.GetString(CodeAlphabet, 12);
                while (await db.GiftCards.AnyAsync(g => g.Code == code, ct));
            }
            else if (code.Length is < 4 or > 40 || code.Any(char.IsWhiteSpace))
                return Results.BadRequest("A card code has 4-40 characters and no spaces.");
            else if (await db.GiftCards.AnyAsync(g => g.Code == code, ct))
                return Results.Conflict($"Gift card '{code}' already exists.");
            if (r.CustomerId is Guid customerId && !await db.Customers.AnyAsync(c => c.Id == customerId && !c.IsDeleted, ct))
                return Results.BadRequest("Unknown customer.");
            // Sold for cash at a branch (SAL-17): the money is in that branch's box and owed to the customer.
            if (await GiftCardCash.BranchAsync(db, r.BranchId, ct) is not Guid branchId)
                return Results.BadRequest("Name the branch where the card is paid for.");

            await using var tx = await db.BeginWriteScopeAsync(ct);
            var card = new GiftCard
            {
                Code = code, InitialAmount = amount, Balance = amount, ExpiresAtUtc = r.ExpiresAtUtc, CustomerId = r.CustomerId
            };
            db.GiftCards.Add(card);
            var issued = new GiftCardMovement { GiftCardId = card.Id, Delta = amount, BalanceAfter = amount, Reason = "issue" };
            db.GiftCardMovements.Add(issued);
            await GiftCardCash.RecordAsync(db, ledger, card, issued, branchId, MovementAuthor.Of(caller).UserName, ct);
            await db.SaveWithVersionsAsync(ct, card);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/catalog/gift-cards/{card.Id}", SyncService.MapForOffice(card));
        }).AtCallersBranch();

        group.MapPut("/gift-cards/{id:guid}", async (Guid id, UpdateGiftCardRequest r, PosDbContext db, CancellationToken ct) =>
        {
            var card = await db.GiftCards.FirstOrDefaultAsync(g => g.Id == id && !g.IsDeleted, ct);
            if (card is null) return Results.NotFound();
            if (r.CustomerId is Guid customerId && !await db.Customers.AnyAsync(c => c.Id == customerId && !c.IsDeleted, ct))
                return Results.BadRequest("Unknown customer.");
            (card.IsActive, card.ExpiresAtUtc, card.CustomerId) = (r.IsActive, r.ExpiresAtUtc, r.CustomerId);
            await db.SaveWithVersionsAsync(ct, card);
            return Results.Ok(SyncService.MapForOffice(card));
        });

        group.MapPost("/gift-cards/{id:guid}/adjust", async (Guid id, AdjustGiftCardRequest r, PosDbContext db,
            IRegionalSettingsProvider regional, LedgerPoster ledger, ClaimsPrincipal caller, CancellationToken ct) =>
        {
            var delta = regional.Get().RoundMoney(r.Delta);
            if (delta == 0) return Results.BadRequest("The adjustment cannot be zero.");
            if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Length > 200)
                return Results.BadRequest("A reason (up to 200 characters) is required.");
            // SAL-17: a paid top-up adds cash, a cash refund takes it out; a correction moves no money.
            var kind = string.IsNullOrWhiteSpace(r.Kind) ? GiftCardCash.Correction : r.Kind.Trim();
            if (kind is not (GiftCardCash.TopUp or GiftCardCash.CashOut or GiftCardCash.Correction))
                return Results.BadRequest("The adjustment is a paid top-up, a cash refund or a correction.");
            if (kind == GiftCardCash.TopUp && delta < 0) return Results.BadRequest("A paid top-up adds to the balance.");
            if (kind == GiftCardCash.CashOut && delta > 0) return Results.BadRequest("A cash refund takes from the balance.");
            Guid? branchId = null;
            if (kind != GiftCardCash.Correction && (branchId = await GiftCardCash.BranchAsync(db, r.BranchId, ct)) is null)
                return Results.BadRequest("Name the branch whose box the cash goes through.");
            // Under the version lock: a till spending the card at the same moment either lands
            // first or waits, so neither writes its balance over the other's.
            await using var tx = await db.BeginWriteScopeAsync(ct);
            var card = await db.LockedAsync<GiftCard>(id, ct);
            if (card is null || card.IsDeleted) return Results.NotFound();
            if (card.Balance + delta < 0) return Results.BadRequest("The balance cannot go below zero.");
            card.Balance += delta;
            var movement = new GiftCardMovement
            {
                GiftCardId = card.Id, Delta = delta, BalanceAfter = card.Balance, Reason = GiftCardCash.ReasonFor(kind), Note = r.Reason.Trim()
            };
            db.GiftCardMovements.Add(movement);
            if (branchId is Guid at)
                await GiftCardCash.RecordAsync(db, ledger, card, movement, at, MovementAuthor.Of(caller).UserName, ct);
            await db.SaveWithVersionsAsync(ct, card);
            await tx.CommitAsync(ct);
            return Results.Ok(SyncService.MapForOffice(card));
        }).AtCallersBranch();

        group.MapGet("/gift-cards/{id:guid}/movements", async (Guid id, PosDbContext db, CancellationToken ct) =>
            Results.Ok((await db.GiftCardMovements.AsNoTracking().Where(m => m.GiftCardId == id).ToListAsync(ct))
                .OrderByDescending(m => m.CreatedAtUtc).Take(200)));

        group.MapGet("/customers/{id:guid}/loyalty", async (Guid id, PosDbContext db, CancellationToken ct) =>
            Results.Ok((await db.LoyaltyMovements.AsNoTracking().Where(m => m.CustomerId == id).ToListAsync(ct))
                .OrderByDescending(m => m.CreatedAtUtc).Take(200)));
    }
}
