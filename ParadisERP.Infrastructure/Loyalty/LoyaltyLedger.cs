using ParadisERP.Application.Loyalty;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Loyalty;

/// <summary>
/// Applies a pushed sale's gift card and loyalty effects inside the sale's transaction. Like
/// stock and customer credit, nothing here rejects a sale: a sale that happened is recorded,
/// and anything suspicious becomes a warning.
/// </summary>
public sealed class LoyaltyLedger(PosDbContext db)
{
    public async Task<IReadOnlyList<SyncEntity>> ApplyAsync(Sale sale, bool isRefund, List<string> warnings, CancellationToken ct)
    {
        var touched = new List<SyncEntity>();
        var sign = isRefund ? 1 : -1;

        // ---------- gift cards: a sale spends the card, a refund to a card credits it ----------
        foreach (var payment in sale.Payments.Where(p => p.Method == PaymentMethod.GiftCard && p.Amount > 0))
        {
            var code = GiftCardRules.NormalizeCode(payment.Reference);
            var card = code.Length == 0 ? null : await db.GiftCards.FirstOrDefaultAsync(g => g.Code == code && !g.IsDeleted, ct);
            if (card is null)
            {
                warnings.Add($"Unknown gift card '{code}'.");
                continue;
            }
            if (!isRefund)
            {
                var problem = GiftCardRules.Problem(card.IsActive, card.Balance, card.ExpiresAtUtc, payment.Amount, sale.SoldAtUtc);
                if (problem is not null) warnings.Add($"Gift card {card.Code}: {problem}.");
            }
            card.Balance += sign * payment.Amount;
            db.GiftCardMovements.Add(new GiftCardMovement
            {
                GiftCardId = card.Id, Delta = sign * payment.Amount, BalanceAfter = card.Balance,
                Reason = isRefund ? "refund" : "redeem", SaleId = sale.Id
            });
            touched.Add(card);
        }

        // ---------- loyalty ----------
        if (sale.CustomerId is not Guid customerId) return touched;
        var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => !s.IsDeleted, ct);
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId, ct);
        if (store is null || customer is null) return touched;
        var program = new LoyaltyProgram(store.LoyaltyEnabled, store.LoyaltyPointsPerUnit, store.LoyaltyPointValue, store.LoyaltyMinRedeemPoints);

        var loyaltyPaid = sale.Payments.Where(p => p.Method == PaymentMethod.Loyalty).Sum(p => p.Amount);
        var giftCardPaid = sale.Payments.Where(p => p.Method == PaymentMethod.GiftCard).Sum(p => p.Amount);

        // Points spent on the sale (or given back when a refund goes to loyalty). Honoured even
        // if the programme was switched off since the till sold.
        var redeemed = LoyaltyRules.PointsForAmount(program, loyaltyPaid);
        if (redeemed > 0)
        {
            if (!isRefund && redeemed > customer.LoyaltyPoints)
                warnings.Add($"Customer loyalty points went negative ({customer.LoyaltyPoints} < {redeemed}).");
            Move(customer, sign * redeemed, isRefund ? "refund-redeem" : "redeem", sale.Id);
        }

        // Points earned on the eligible amount. A refund takes back what its amount earned,
        // whether the money goes back in cash or onto a gift card; only a refund paid back in
        // points is excluded, since those points are returned above.
        var earned = LoyaltyRules.EarnedPoints(program,
            LoyaltyRules.EligibleAmount(sale.GrandTotalTtc, loyaltyPaid, isRefund ? 0 : giftCardPaid));
        if (earned > 0) Move(customer, isRefund ? -earned : earned, isRefund ? "refund" : "earn", sale.Id);

        if (redeemed > 0 || earned > 0) touched.Add(customer);
        return touched;
    }

    private void Move(Customer customer, int delta, string reason, Guid saleId)
    {
        customer.LoyaltyPoints += delta;
        db.LoyaltyMovements.Add(new LoyaltyMovement
        {
            CustomerId = customer.Id, Delta = delta, BalanceAfter = customer.LoyaltyPoints, Reason = reason, SaleId = saleId
        });
    }
}
