# Loyalty points and gift cards

Two new tenders at the till, both server-owned and offline-tolerant like stock and customer
credit: **gift cards** (prepaid balance on a code) and **loyalty points** (earned on sales,
spent as payment).

## Who sets what

| Setting | Owner | Where |
|---|---|---|
| Whether `GiftCard` / `Loyalty` tenders exist for a company | Platform operator (super admin) | Country profile → payment methods |
| Loyalty programme: on/off, points per currency unit, value of a point, minimum points to redeem | Company admin | Back office → Magasin |
| Gift cards: issue, deactivate, adjust | Company admin | Back office → Cartes cadeaux |

A company can therefore run loyalty only if its operator enabled the tender **and** its own
admin switched the programme on.

## Rules

* **Earning.** A completed sale with a customer earns `floor(eligible × pointsPerUnit)` points,
  where `eligible = total − paid with points − paid with gift card`. Points are not earned on
  rewards: gift cards already earned points when they were bought.
* **Spending.** A `Loyalty` payment of *X* costs `ceil(X / pointValue)` points. The till only
  offers redemption above `minRedeemPoints`.
* **Refunds.** A refund takes back the points its amount earned (gift-card refunds included —
  the money goes back either way); only the part refunded *as points* is excluded, because
  those points are credited back separately.
* **Gift cards.** Codes are stored upper case, are unique, and are replicated to tills so a
  card can be checked offline; the balance is the server's figure and is overwritten on the
  next pull. Refunds may be paid back onto a card.
* **Never rejected.** As with stock and credit, the server records a pushed sale even when a
  card is unknown, inactive, expired or overspent, or the customer lacks points; each case is
  returned as a warning on the accepted sale. Obviously wrong cards are stopped in the till UI
  instead, from the local replica.

## Where the code lives

| Concern | Location |
|---|---|
| Point/card arithmetic (shared spec) | `AlgeriaPos.Application/Loyalty/LoyaltyRules.cs`, mirrored in `AlgeriaPos.WebUI/src/app/core/loyalty.ts` |
| Applying a pushed sale | `AlgeriaPos.Infrastructure/Loyalty/LoyaltyLedger.cs` (inside the sale transaction) |
| Entities | `AlgeriaPos.Domain/LoyaltyEntities.cs` (`GiftCard`, `GiftCardMovement`, `LoyaltyMovement`), `Store.Loyalty*`, `Customer.LoyaltyPoints` |
| API | `AlgeriaPos.API/Endpoints/GiftCardEndpoints.cs` (`/api/catalog/gift-cards…`, `/api/catalog/customers/{id}/loyalty`) |
| Till | payment dialog and split parts in `features/register`, balances/points on the receipt |
| Back office | `features/backoffice/gift-cards-tab.component.ts`, loyalty fields in the store tab, points column in customers |
| Migration | `LoyaltyGiftCards` (SQLite, MySQL) — additive: three tables plus five columns |

## Reports

`GET /api/reports/summary` adds `giftCardTotal` and `loyaltyTotal` (net of refunds), shown as
tiles in Back office → Rapports when non-zero.

## Tests

* `AlgeriaPos.Tests/Loyalty/LoyaltyRulesTests.cs` — earning, redemption, thresholds, card problems.
* `AlgeriaPos.WebUI/src/app/core/loyalty.spec.ts` — the same maths plus the till's tender checks.
* `tests/e2e/loyalty-giftcards.cjs` (in CI) — real API: settings validation, issue/duplicate/generated
  codes, redeem, overspend and unknown-card warnings, refunds to a card and in points, adjustments,
  movement history, replication to a till, and the report totals.

## Known gaps

* Points and card balances shown offline are estimates until the next pull, like stock.
* Gift card codes are replicated to every till of the company, so a till's local database
  reveals card codes and balances; treat tills as trusted devices (the same trade-off as the
  replicated cashier PIN hashes).
* No expiry of points, no card-to-card transfer, and no bulk card issuing.
* Selling a gift card is recorded as a normal product sale; the issue step is separate.
