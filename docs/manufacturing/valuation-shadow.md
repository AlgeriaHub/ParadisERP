# Weighted-average shadow (P0.4a + P0.4b) — as built

*2026-11-08. The owner chose this as the next manufacturing piece ("Weighted average, shadow"), then asked that
"for the things that need a decision, make it settings and the admin can choose". Branch `feat/valuation-shadow`.*

The books still run on the last purchase price (`Product.CostPrice`). This piece records, beside every stock
movement, the cost it was valued at, and shows the same stock at a moving weighted average next to it. Nothing
here posts, changes a price or refuses anything.

## P0.4a — what each movement and sale line now records

| Field | Filled with | Before 2026-11-08 |
|---|---|---|
| `StockMovement.Kind` | The act: opening, receipt, sale, refund, adjustment, state change, write-off, rental issue / return / return elsewhere, transfer out / in / cancel / loss, production issue / return / output (`StockMovementKind`, fixed numbers) | Classified once by the migration from the reason label; an adjustment typed with the operator's own words stays `Unclassified` |
| `StockMovement.UnitCost` | The reference cost the entry beside it used: the receiving price, the cost price at a sale, refund, adjustment, state change or rental return, the transfer line's cost, a production movement's snapshot. Null on a rental issue (no entry values it) | Null |
| `SaleLine.UnitCost` | The cost per unit the sale's entry posted: the cost price at push, 0 when missing (`CostMissing`), null for a service. Server-only: never sent to a till | Null |

All 12 movement writers set both fields. The figures posted to the ledger are unchanged.

## P0.4b — the shadow

**Opening.** At the first start with this code, after every seeder and before anything is served, the server
records what each location held of each stock-tracked product (available + on rent + maintenance + scrap) and
what was in transit on each open transfer, at the product's cost price (`StockValuationOpenings`). The settings
row is written in the same transaction; its existence means the opening was taken, so it happens once.

**Replay.** Every read replays the movements created after the opening, in time order (`WeightedAverageShadow`,
pure). Nothing is stored, so a changed setting shows its result immediately.

| Movement | Valued at |
|---|---|
| Receipt, opening restated upwards | Its own unit cost |
| Sale, adjustment down, write-off, production issue, loss in transit | The pool's average, rounded to the chosen decimals; the last unit out takes whatever value is left |
| Refund | The setting: the cost its sale took out in the shadow (or, for a sale before the opening, the cost its line posted); or today's average |
| Production return | The average cost of that order's issues of the material |
| Production output | The release's reference snapshot, flagged. Never the order's cost sheet, which would give back an order's labour money to readers of this report (labour privacy, 2026-11-03). The production value comes with Phase D |
| Adjustment up, rental return, unknown kind | The average (changes nothing) |
| State change, rental issue | Nothing: the same goods in another state |
| Transfers, rental return elsewhere | Company scope: nothing (still the company's). Location scope: out at the source's average, in at that value |
| Goods out with no average yet | Their own reference cost, flagged |
| Goods sold below zero | Out at the last average; the inflow that covers them settles the difference as the **negative-stock gap** (C-16), reported per product |

Per pool, value always satisfies: closing = opening + in − out + moved in − moved out − gap (tested).

## Settings the admin chooses (accountant pending)

| Setting | Choices | Default | Question |
|---|---|---|---|
| One average per | Company · Location | Company | C-01 |
| A customer return comes back at | Its sale's cost · Today's average | Its sale's cost | P0.4c / COR-01 |
| Decimals of a unit cost | 0 to 6 | 3 | C-17 |

Stored in `StockValuationSettings` (one row). Read with `reports.accounting`; changed with `accounting.manage`.
They affect only the shadow's figures — until sales are switched to the average (C-01, below), when they decide what
sales post and are locked.

## Where to see it

Reports › Stock valuation (trial), `reports/stock-valuation`, for `reports.accounting`. Totals at average and at
cost price, the difference, cost of sales at average beside what was posted, the negative-stock gap; the three
settings; per product a dialog with every movement as valued.

| Route | Permission |
|---|---|
| `GET /api/valuation/shadow?branchId=` | `reports.accounting` |
| `GET /api/valuation/shadow/products/{productId}` | `reports.accounting` |
| `GET /api/valuation/shadow/settings` | `reports.accounting` |
| `PUT /api/valuation/shadow/settings` | `accounting.manage` |

**Branches.** A caller who sees every branch reads everything. A branch user reads their own location when the
scope is per location (CallersBranch sets `branchId` to it whatever the URL says); with one average for the
company the figures pool every branch's goods, so a branch user is refused in words (403
`valuation_company_wide`), on the report and on the product detail alike.

## Migration

`StockMovementCostsAndValuationShadow`, in both sets: two columns on `StockMovements` (`Kind`, `UnitCost`) plus
an index on `CreatedAtUtc`, one column on `SaleLines` (`UnitCost`), two new tables. Additive; the only data
change classifies the existing movements' `Kind` from their reason.

## Not built here

- ~~P0.4c in the ledger~~ and ~~the switch itself (D9/D43)~~: built 2026-11-09, see "C-01 — the switch, as built" below.
- Showing the shadow average on a production order's issues (costing plan §5.2): the product dialog shows it.
- An Excel export of the report.

## C-01 — the switch, as built (2026-11-09)

*The owner's standing rule (2026-11-09): every accounting decision is a back-office setting the accountant sets in the
panel; the default keeps today's behaviour; never "wait for the accountant's signature".*

**The setting.** `StockValuationSettings.SalesCost`: `CostPrice` (today's rule, the default) or `WeightedAverage`, with
`SalesCostChangedAtUtc` / `SalesCostChangedBy`. Every change is a `StockValuationSwitch` row (method, when, who, the
shadow's choices it was made under, the shadow value, the book value, the adjustment posted, its ledger entry). It is
changed only through the switch, on Reports › Stock valuation (trial), section "Cost of sales", under `accounting.manage`.

| Route | Permission | Does |
|---|---|---|
| `GET /api/valuation/sales-cost` | `accounting.manage` | The method, the figures, the two slots, the gaps waiting, the history |
| `POST /api/valuation/sales-cost/switch` | `accounting.manage` | `{ method, expectedAdjustment }` |
| `POST /api/valuation/sales-cost/gaps/post` | `accounting.manage` | Posts the gaps waiting |

The three answer a branch-bound caller 403 `valuation_company_wide`: the figures are the company's stock accounts.

### What posts

| When | Entry | Source |
|---|---|---|
| **Switching on** | One entry, the opening adjustment (C-02): stock at the shadow's average (in transit included) less the balance of the stock accounts — 310, 318 and any account the map's stock slots name (raw material, supplies, semi-finished, finished, scrap). Dr/Cr 310 against the `opening_adjustment` slot's account. With one average per location, one pair of lines per location (in transit on the main branch's); with one for the company, one pair at the main branch. Dated now | `valuation-switch`, id = the switch row |
| **Each sale, while on** | The sale's entry as before; its cost of sales is quantity × the weighted average of the moment the sale reaches the server, for that product (and location, per-location scope). `SaleLine.UnitCost` and the movement's `UnitCost` carry it (10 decimals); `SaleLine.CostBasis` = `WeightedAverage` | `sale` (unchanged) |
| **Each refund, while on** | Under the refund setting (P0.4c): `SaleCost` — the value its sale took out in the shadow (a sale before the opening: the cost its line posted), `CostBasis` = `SaleCost`; `CurrentAverage` — the average of the moment. Dr 310 / Cr 600 at that value | `sale` (unchanged) |
| **No average** (nothing came in since the opening; or the computation failed) | The product's cost price, as today, and `CostBasis` = `CostPriceFallback`; the page counts those lines. The sale is never refused (rule 1); a failure is logged | `sale` |
| **A covered negative quantity (C-16)** | By default not automatic (a setting since 2026-11-09, see "Three postings the accountant chooses" below): the page lists every gap settled by an inflow after the latest switch-on (and before a switch-off), and the accountant posts them. One entry per covering movement: Dr/Cr the `valuation_difference` slot's account against 310 for the gap (positive: the goods turned out dearer). Idempotent on the movement; a gap dated in closed books is booked on the first open day. Unmapped slot: listed, refused in words, never guessed | `valuation-gap`, id = the covering movement |
| **Switching off** | Nothing. Sales return to the cost price from that moment; past sales are untouched | — |

Why the gap stock credit is right: the inflow that covers negative stock posts its full value to 310 (a receipt at its
price; a refund, while on, at the value above), while the pool keeps that value less the gap — the gap entry brings 310
back to the pool. Switching on again posts the difference of that moment, which takes in any gap left unposted before.

**Refusals of the switch** (409, with `code`): `opening_adjustment_unmapped` (slot without an active account);
`adjustment_changed` (the recomputed figure differs from `expectedAdjustment` by more than 0.0005 — nothing posts, the new
figure comes back with it); `sales_cost_unchanged`; `books_closed`. The figure is recomputed after taking the version
lock that every sale takes, so no sale lands between the figure and the switch.

**Locked while on.** The shadow's three choices (scope, refund cost, decimals) are refused while sales are costed at the
average (`StockValuationShadow.SalesCostLocked`): the replay values every movement since the opening under the current
choices, so changing one would revalue history behind entries already posted. Switch off, change, switch on again —
which posts the difference the change makes.

### The live average: a per-request replay, not a stored running average

`LiveSaleCosting` (Infrastructure/Valuation, read only) is owned by the scoped `SyncService`, so it lives for one push
request. The first time a request needs a product it replays that product alone — its opening and its movements since —
with the shadow's own engine (`WeightedAverageEngine`, the rules `WeightedAverageShadow.Run` folds) over the shadow's own
loader (`StockValuationShadow.LoadMovementsAsync`). It keeps the engine for the rest of the request. Each later sale of the
product reads one count of its movements and, when there are new ones, only those (written since the last one applied).
If the count does not add up — a movement committed late with an earlier time — the product replays from the opening
again. The sale itself is never applied to the cache: the next sale reads it back from the database once committed, so a
sale that rolls back leaves nothing behind. `PreviewOut` / `PreviewRefund` value the sale's quantity without taking it out.

Why not a running average kept by the movement writers: the twelve writers would each need a copy of the replay's rules
(transfers per scope, production returns at their issues' cost, refunds at their sale's cost, the last unit taking the
rest, negative stock), and a stored figure would go stale whenever a choice changed. One set of rules means the cost a
sale posts is the figure the report shows for it — the tests check `PostedCostOfSales == ShadowCostOfSales` after a batch.
The cost: a request's first sale of a product grows with that product's movements since the opening (indexed on
`ProductId, CreatedAtUtc`); a batch of N sales replays each product once, not N times. If that ever becomes the
bottleneck, the next step is a stored checkpoint of each pool, not a second set of rules.

"The moment of the sale" is when it reaches the server (the movement's `CreatedAtUtc`), not the till's clock: the replay
orders by that instant, and an offline till's sale is costed at the average when it arrives.

### The other stock documents follow the switch (built 2026-11-09, same day)

Until this step only sales and refunds (and P0.3 direct invoices) followed the switch; every other document kept posting
the cost price, so the stock accounts drifted from the shadow. Now, while `SalesCost = WeightedAverage`, each of them
values its goods — the `StockMovement.UnitCost` it writes **and** the entry it posts — at the weighted average of the
moment, through the same costing as a sale (`LiveSaleCosting.MovementCostsAsync`) and the same fallback. Switched off
(the default), every document posts exactly what it posted before; `Tests/Valuation/StockDocumentsCostSwitchTests.cs`
pins those figures.

**How a document is valued.** The document's movements are built first, not saved. `MovementCostsAsync` replays the
document's products from the opening with the shadow's own engine and loader, then applies the document's own rows to
that replay in the replay's order (`InReplayOrder`: time, then the side that leaves first, then the id) — exactly as the
report will apply them once saved. Each row takes the value the replay gives it, so a second row of the same product in
one document sees what the first took out. A row the replay moves no value for (a state change; a transfer step under
one average for the company) is valued at what its units would leave their pool at now (`PreviewOut`), without taking
them out. Then the caller writes `UnitCost` (value ÷ quantity, 10 decimals) and `StockMovement.CostBasis`, and posts the
value. Unlike a sale there is no per-request cache: a back-office document is one act, replayed once.

| Document | Switch off (today, unchanged) | Switch on |
|---|---|---|
| Stock adjustment, down (`CatalogEndpoints` `/products/{id}/stock`) | Dr 657 / Cr 310 at qty × cost price | At the value the units leave the pool at (the average rounded to the chosen decimals; the last units take what is left) |
| Stock adjustment, up (goods found) | Dr 310 / Cr 657 at qty × cost price | At the average (it changes nothing), as the shadow values it |
| Write-off (`StockEndpoints` `/stock/state` → WrittenOff) | Dr 657 / Cr 310 (or 318) at qty × cost price | At the value the units leave the pool at |
| State change into or out of scrap | Dr 318 / Cr 310 (or back) at qty × cost price | At what the units would leave the pool at now; the company's stock value does not change (both are stock accounts) |
| State change between available and maintenance | No entry | No entry; the movement records the average |
| Transfer dispatch (`StockTransfers`) | No entry; line and movement at the cost price | No entry. Per location: the goods leave the source at its average, into the transfer. Company: the company average. The line's `UnitCost` shows that figure (3 decimals, the column's) |
| Transfer receipt | Dr 310 destination / Cr 310 source at qty × line cost (inter-branch) | Per location: at what the goods left the source at — the receiving location takes the sending location's average. Company: at the company average of the moment (inter-branch; the company's stock value does not change) |
| Loss in transit (declared, or a shortfall confirmed) | Dr 657 / Cr 310 source at qty × line cost | Per location: at what the goods left the source at (out of the transfer). Company: at the company average of the moment |
| Transfer cancel | No entry | No entry; the movement records the value the goods come back at (per location, what they left at) |
| Rental return, lost (`RentalDocuments`) | Dr 657 / Cr 310 at the issuing branch, qty × cost price | At the value the units leave the issuing branch's pool at |
| Rental return, wrecked | Dr 318 / Cr 310 at qty × cost price | At what the units would leave the pool at |
| Rental return at another branch (implicit transfer) | Dr 310 here / Cr 310 there at qty × cost price | At the issuing branch's average (per location, the returning branch takes it; company: the company's). Wrecked goods then go to 318 at that same value |
| Opening stock restated down (`OpeningStock`, typed or imported; a branch not yet open) | Dr 890 / Cr 310 at qty × the stated value (the unit value typed, else the cost price) | At the value the units leave the pool at, whatever value was typed; no average: the stated value, `CostPriceFallback` |
| Opening stock, first entry or restated up | Dr 310 / Cr 890 at qty × the stated value | **Unchanged**: at the stated value, `CostBasis` stays `CostPrice` (see below) |
| Production issue / return / release | The order's snapshots | **Unchanged** (see below) |
| Maintenance parts issue (M2, `MaintenanceIssue`) | No entry; the movement and the job's line at the cost price | No entry (the practice ledger, M6); the movement and the line at the average of the moment, falling back to the cost price |
| Maintenance parts return (M2, `MaintenanceReturn`) | No entry; at what the job's issues went out at | No entry; at what the job's issues went out at (not replayed; the shadow reads the row's own cost, as a credit note's goods) |

**Goods coming back.** The rule mirrors refunds under `RefundCost`: what comes back from a document that sent it out
comes back at the cost it left at — a transfer's receipt or cancel takes the goods out of the transfer at what they went
in at (per location), a rental return elsewhere arrives at what it left the issuing branch at. Goods found at a count
have no outflow to mirror and come in at the average (which leaves the average unchanged), as the shadow has always valued
them. Goods back from a rental at the branch that issued them move no value (they never left the pool).

**Fallbacks.** No average (nothing came in since the opening; the replay's own "no average" case): the row's reference
cost — the cost price, or for a transfer step the line's cost — and `StockMovement.CostBasis = CostPriceFallback`. A
failure of the computation: every row of the document at its reference cost, flagged the same way, and logged as an error
(`MovementCostsOrFallbackAsync`). The document is never refused (rule 1). The page counts those rows
(`SalesCostingDto.FallbackMovements`, notice "N stock movement(s) … valued at their reference cost") beside the sale lines.

**Production stays as it is.** A production issue goes out at the product's cost price into the order's snapshot, a
return at its issues' mean, a release at its reference snapshot; they reach the ledger only through the practice ledger
(Phase D), built from those snapshots by the cost engine. Valuing an issue at the average would change the order's
material cost and the release value — the cost engine's rule, not the same rule as a write-off — so it is left to Phase D.
Under the switch the shadow still takes an issue out at the average and a release in at its snapshot, so production is
the one remaining source of drift, and the page says so ("Only production still posts its reference cost").

**Opening stock (2026-11-09, follow-up).** An opening restated downwards used to post the stated value while the
shadow took the units out at the average; it now follows the switch like the documents above (same call, same
fallback, the stated value as the reference cost). An opening that raises a quantity — a first entry included — keeps
its stated value with the switch on, by the shadow's own rule: the replay takes an `Opening` row in at the row's own
`UnitCost`, as a `Receipt` at its cost (`WeightedAverageEngine.InboundCost`), not at the average. Posting that same
value is therefore exactly what the shadow takes in; the average follows the opening, not the other way round. Such a
row cannot fall back, so it keeps `CostBasis = CostPrice` and is never counted among the fallbacks. All of a document's
moving rows, raises included, go to the costing in one call, so a lowering row sees what an earlier row of the same
product brought in; only the lowering rows take its answer. `Tests/Valuation/OpeningStockCostSwitchTests.cs` (3 of 3):
switched off, a first entry, two restatements down (cost price, then a typed value) and one up post their stated
values, as before; switched on, a first entry, a restatement down (at the average, though a value was typed) and one
up, then a sale — the stock accounts end on the shadow's value (adjustment 0, no fallback); a product with no average
restated down falls back to its cost price and is counted, a product raised beside it is not. With this, production
is the only remaining drift.
`dotnet build` passes; Valuation, Stock, Opening and Ledger filters: 220 passed, 1 skipped, 0 failed. No migration.

**Schema.** Migration `StockMovementCostBasis`, both sets: one `int` column `StockMovements.CostBasis`, default 0 =
`CostPrice` on every existing row. Additive. `StockValuationSettings` is not touched.

**Verification (2026-11-09).** `StockDocumentsCostSwitchTests` (6 of 6): today's figures pinned with the switch off (adjustment
both ways, write-off, scrap, transfer receipt and loss, rental wreck and loss, every movement's cost and basis); with it
on, a sale, two adjustments, a write-off, a move to scrap, a transfer received and lost and a rental return with goods
lost and wrecked leave the stock accounts **equal** to the shadow's value (`Adjustment` 0, posted and shadow cost of sales
equal); one average per location: a transfer received at the sending location's average, the next sale at the receiving
location's new average, a loss in transit at what left, and each branch's 310/318 equal to its pools (in transit counted at
the main branch, as the switch does); a rental from another branch coming back at its branch's average; a product with no
average falls back to its cost price and is counted; a failed computation falls back without refusing. Backend
Valuation, Stock, Transfer, Rental, Ledger, Sale and Manufacturing filters: 701 passed, 12 skipped (SQL Server only), 0
failed; Security and Receivable filters: 333 passed, 2 skipped, 0 failed. Angular: 1,236 of 1,236 (one new spec in
`stock-valuation.component.spec.ts`); `ng build` passes, initial bundle 796.37 kB (was 795.78 kB). Not run: the SQL
Server provider smoke against `deploy/sqlserver` (the migration is one `int` column with a default).

### What is still at the reference cost while on

Production (above) and an opening restated downwards. The page shows the current difference between the stock accounts
and the average ("Difference between the two now"); switching off and on again posts it.

### Schema

Migration `SalesCostedAtAverage`, both sets: three columns on `StockValuationSettings`, `SaleLines.CostBasis` (0 =
`CostPrice` on every existing line), the `StockValuationSwitches` table, and `SaleLines.UnitCost` widened to (28,10) so a
refund at its sale's cost gives back exactly what went out (SQL Server: an `ALTER COLUMN` from (18,3); SQLite stores
text and needs nothing). Additive; with the switch off nothing reads the new columns differently.

### Verification (2026-11-09)

| Check | Result |
|---|---|
| New backend tests: `Tests/Valuation/WeightedAverageEngineTests.cs` (pure: a preview is exactly what the replay then takes out, refunds both ways, location scope, two halves equal the whole, gaps listed) and `SalesCostSwitchTests.cs` (through the sync and the routes: off by default, the refusals, the adjustment posted once, sales at the average and the shadow agreeing after a batch, a refund at its sale's cost, the fallback, switching back, the locked choices, a gap posted once, the request cache, the branch rule, one average per location: the adjustment per location and sales at their location's average) | 17 of 17; `Tests/Valuation` 36 of 36 |
| Valuation, Sale, Security and Ledger filters (route plan, legacy snapshot, counts 320 → 323) | 362 passed, 5 skipped (SQL Server only), 0 failed |
| Full backend suite (before the per-location test was added) | 1,748 passed, 14 skipped (SQL Server only), 0 failed |
| Angular | 1,220 of 1,220 (6 new in `stock-valuation.component.spec.ts`; the source-label spec counts 31 sources); `ng build` passes, initial bundle unchanged at 795.78 kB |
| Not run | SQL Server facts and the provider smoke against `deploy/sqlserver`: the migration's `ALTER COLUMN` of `SaleLines.UnitCost` is untested on SQL Server |

## B-F01 — the cost price on receipt, as built (2026-11-09)

*Same standing rule: an accounting decision is a setting the accountant sets in the panel; the default keeps today's
behaviour.*

**The problem.** Every receiving note that stated a cost wrote it over the product's company-wide `CostPrice` (last
purchase price, `StockEndpoints.cs` receiving), and a stated 0 was accepted. The receipt itself was valued right (its
movement's `UnitCost`), but everything that reads the cost price afterwards — sales while costed at the cost price,
transfers, adjustments, losses, rental returns, production issues — took the overwritten figure, and nothing revalued
the stock on hand.

**The setting.** `StockValuationSettings.ReceiptCostRule` (`ReceiptCostPriceRule`, stored as its number): "When goods are
received, the product's cost price becomes:"

| Choice | What the receiving note writes to `Product.CostPrice` |
|---|---|
| `LastCost` (default, today's rule) | The cost each line states (a later line of the same note wins), as since the first release |
| `WeightedAverage` | After the note's movements are saved (same transaction), the shadow's weighted average for the product, company-wide, rounded to the 3 decimals the cost price is stored at. Computed by `StockValuationShadow.CompanyAveragesAsync`: the shadow's own loader (`LoadOpeningsAsync`, `LoadMovementsAsync`), engine (`WeightedAverageShadow.Run` / `WeightedAverageEngine`) and settings — the same as the report and the C-01 sale costing. One average for the company: that pool's average, the report row's `AverageCost`. One per location: the locations' value over their quantity, the product dialog's summary row (in transit left out, as there). Every product on the note is restated, whether or not its line stated a cost |
| `Unchanged` | Nothing: the cost price changes only on the product's card |

**No average.** When the company holds none of the product after the receipt (quantity ≤ 0), the shadow was never opened,
or the replay throws: the last cost rule for that product, and it is logged (warning; an error with the exception when the
replay failed). A receipt is never refused for it.

**A stated 0 (B-F06), in every mode — a correction of the old behaviour.** A stated cost of 0 never replaces a non-zero cost
price: a delivery at 0 is a free or unknown cost, not a price. Until 2026-11-09 the last cost rule wrote the 0 over it. A
product carried at 0 still takes its first non-zero price. Under the weighted average the same guard holds for the
result: an average that rounds to 0 never lands on a non-zero cost price. What the note itself is valued at does not
change: a line at 0 still records `UnitCost = 0`, posts nothing to 310 and adds nothing to the supplier's balance, as
before — and so, under the weighted average, it weighs in at 0 (30 at 700 + 5 at 0 = 35 at 600), exactly as the report
counts it.

**What the receipt is valued at, in every mode.** The cost the line states (0 included), or, none stated (or a negative
one), the cost price as it stands when the line is read. The movement's `UnitCost` and the ledger entry carry that figure;
before this change they read the cost price after the line had restated it, which is the same figure under the default.

**Where it is set.** Reports › Stock valuation (trial), its own section "Cost price on receipt", under `accounting.manage`
(read only otherwise). It goes through the existing `PUT /api/valuation/shadow/settings`, whose request gained an optional
`receiptCostRule` (null — every older client — keeps the saved one); the settings answer carries it. No new route. Unlike
the shadow's three choices it is **not** locked while sales post the average: it changes no movement the replay reads,
only the reference figure later documents start from. Logic: `Infrastructure/Catalog/ReceivingCostPrice.cs` (the
Valuation folder stays read-only — the architecture test forbids it to write a cost price).

**Other writers of `Product.CostPrice` (audited 2026-11-09).** Only two: the receiving note (above) and the product card
(`CatalogEndpoints.Apply`, a person typing the price — not a document; left as it is). Opening stock
(`OpeningStock.cs`, typed or imported) reads the cost price and never writes it (B-F05 stands). Transfers, adjustments,
rental returns, sales and refunds only read it. Manufacturing (`ProductionMovements`, `ProductionOrders`, `ProductionCosts`,
`ProductionCostHistory`) only reads it and never writes it. The seeder writes it for demo data only. There are no supplier
invoices or purchase orders (B-F11), so nothing else is a "receipt of goods".

**Schema.** Migration `ReceiptCostRuleSetting`, both sets: one `int` column `StockValuationSettings.ReceiptCostRule`,
default 0 = `LastCost`. Additive; nothing changes until the accountant picks another rule.

**Tests.** `Tests/Valuation/ReceivingCostPriceTests.cs` (through the real receiving route): the default is the last cost;
a 0 never replaces a non-zero price (last cost and unchanged), nor a 0 after a price on the same note; a product at 0 takes
its first price; unchanged never writes it and the receipt is valued at what it states; the weighted average equals the
report's `AverageCost` after the receipt, a free delivery weighs in at 0, rounding to 3 decimals, an average of 0 never lands
on a non-zero price; no average falls back to the last cost; the setting is validated, kept when not sent and open while
sales post the average. Angular: two specs in `stock-valuation.component.spec.ts`.

**Verification (2026-11-09).** Backend Valuation, Stock, Receiving, Security and Sale filters: 474 passed, 6 skipped (SQL
Server only), 0 failed (11 new). Angular: 1,226 of 1,227 — the one failure, `confirm-before-acting.spec.ts` "saves (HR and
payroll) advances", is outside this change (payroll advances) and fails alone too. `ng build` passes, initial bundle
unchanged at 795.78 kB. Not run: the SQL Server provider smoke against `deploy/sqlserver` (the migration is one `int`
column with a default).

## Three postings the accountant chooses (2026-11-09)

*Same standing rule: every accounting decision is a setting the accountant sets in the panel; each default keeps today's
behaviour.* Three columns on `StockValuationSettings`, each stored as its number, default 0. They go through the existing
`PUT /api/valuation/shadow/settings` (optional `gapPosting`, `costPriceRevaluation`, `zeroCostReceipt` on the request: null —
every older client — keeps the saved choice) and come back on the settings answer. No new route. Reports › Stock valuation
(trial), section "Valuation postings", under `accounting.manage` (read only otherwise), with its own save. None of the three is
locked while sales post the average: none changes a movement the replay reads.

| Setting | Choices (default first) | Where it acts |
|---|---|---|
| `GapPosting` (C-16) | `Accountant` · `Automatic` | `PosDbContext.SaveChangesAsync` → `Ledger/ValuationGapsOnInflow.cs` |
| `CostPriceRevaluation` (B-F01, open part) | `None` · `StockOnHand` | `Ledger/CostPriceRevaluationPosting.cs`, called by the receiving route and the product card's `PUT` |
| `ZeroCostReceipt` | `Zero` · `CostPrice` | `ReceivingCostPrice.UnitCostOf`, in the receiving route |

### C-16 — negative-stock gaps posted with the inflow

**`Accountant`** (default): as described under C-01 — listed on the page, posted with its button.

**`Automatic`**: while sales are costed at the average, a save that adds a stock movement with a positive quantity replays
the shadow for that movement's products (`StockValuationShadow.LoadOpeningsAsync` / `LoadMovementsAsync`,
`WeightedAverageShadow.Run`, the saved settings) and posts, in the same transaction, the gap the replay settles on each of
those movements, in the same window the page lists (after the latest switch-on). The rule of what "covers" is therefore the
replay's own — the one the page lists by — whichever writer saved the inflow: receiving, opening stock, an adjustment up,
a transfer in or a rental return elsewhere (per location), a rental return, a production return or output, a till refund, a
credit note's goods. The entry is the one the button posts (`SalesCostSwitch.GapDraft`): source `valuation-gap`, id the covering
movement, Dr/Cr the `valuation_difference` slot's account against 310, dated at the movement, by the movement's author, its
description ending "(C-16, posted with the inflow)". So it posts once, and the button never posts it again (the poster's
re-post guard and the page's "not yet posted" filter).

Why in `SaveChangesAsync`: every writer of a stock movement ends there, so none has to remember (the same place the roles
mirror and the branch code already sit). Under the defaults it costs one read of the settings row, and only for a save that
adds an inflow. With the switch off nothing is read further.

Never refused: while the slot names no active account nothing posts, the gap stays listed for the accountant, and it is
logged (warning); if the replay throws, the same (error). A gap dated in closed books is booked on the first open day, as
the button does.

### B-F01, the open part — revaluing the stock on hand

**`None`** (default): a change of the cost price only changes what later documents start from; 310 keeps the old values.

**`StockOnHand`**: while sales are costed at the cost price, each change of a product's cost price posts one entry: the change
× the quantity held at each branch (available + on rent + maintenance — what 310 carries; scrap sits on 318 at what it was
scrapped at and is left alone), Dr/Cr 310 against the `valuation_difference` slot's account, one pair of lines per product and
branch. Source `cost-price-revaluation` (a new ledger source; the accounting tab labels it), id the receiving note's document
id, or a fresh one for the card. Dated now; in closed books it would be booked on the first open day
(`BookClosings.OntoOpenBooksAsync`, as the other automatic postings).

- **The two triggers.** A receiving note under the **last cost** rule (the only receipt rule that writes a price), and the
  product card's `PUT /api/catalog/products/{id}` (the cost price read under the version lock, the save and the entry in one
  transaction). Under the **weighted-average** receipt rule nothing posts: the note restates the price to the shadow's average,
  not to a price, and revaluing on it would count the note's own goods twice. Under **Unchanged** the note changes nothing.
- **On a receiving note, "on hand" is what was held before it.** The note's own units are already on 310 at what the note
  states, by its own entry; they are subtracted (`ReceivingAsync`: stock after the note's movements are saved, less the note's
  lines at its branch). 20 held at 600, 10 received at 900: the 20 are revalued (+6,000), the note posts its 9,000 — 310 holds
  30 × 900. A note that lists one product twice at two prices revalues from the price before the note to the last one; its
  earlier line stays at what it stated.
- **A quantity below zero is revalued with its sign**: it is goods sold at the old price and not yet replaced, which 310
  already carries below zero.
- **Under the weighted average (C-01) nothing posts**: there is no cost-price valuation to revalue. The page says so beside
  the choice.
- **The account.** The choice is refused (400, "Name the account for valuation differences …") while the `valuation_difference`
  slot names no active account; the page says so and keeps its save button off. If the slot is unmapped afterwards, nothing
  posts and it is logged. The note and the card are never refused for it; a failure is logged and nothing posts.

### A receiving line stated at cost 0

**`Zero`** (default): a free delivery — the movement's `UnitCost` is 0 and the line adds nothing to 310 or 401, as before.

**`CostPrice`**: the cost is unknown, not free — the line is valued at the product's cost price as it stands when the line is
read (the figure an unstated line already took). The movement's `UnitCost`, the receiving entry (Dr 310) and therefore 401
and the supplier's running balance carry it, like any receipt; the shadow weighs it in at that cost. A product carried at 0
still values the line at 0. The B-F06 rule is untouched: a stated 0 never replaces a non-zero cost price, in every mode.

### Schema

Migration `ValuationPostingChoices`, both sets: three `int` columns on `StockValuationSettings` (`GapPosting`,
`CostPriceRevaluation`, `ZeroCostReceipt`), default 0 = today's behaviour. Additive.

### Tests

`Tests/Valuation/ValuationPostingChoicesTests.cs` (11, through the real receiving and product routes and the till's push):
the defaults are today's (no revaluation on the card or a note, the note's 9,000 and its free line at 0, the gap listed and
not posted) and an older client keeps the saved choices; invalid values refused; automatic posts the gap with the inflow
(1,500 = 5 × (900 − 600)), balanced, once, the button then finds nothing and the stock account equals the average; unmapped,
the inflow is accepted and the gap waits for the button; any writer (a movement saved straight through the context) posts it,
and nothing posts while sales are at the cost price; the revaluation is refused while unmapped; the card revalues 10 + 10 at
each branch (+2,000 each, then −1,000 each, the same price twice posts nothing); a last-cost note revalues the 20 held before
it (+6,000) and 310 ends at 30 × 900; nothing under the weighted-average receipt rule, once unmapped, or under the average; a
line at 0 at the cost price (3,000 on 310 and 401, the price kept), and at 0 for a product carried at 0. Angular: three specs
in `stock-valuation.component.spec.ts`; the source-label spec counts 32 sources.

**Verification (2026-11-09).** `dotnet build` passes. Backend Valuation, Stock, Receiving, Catalog, Ledger and Security filters:
633 passed, 3 skipped (SQL Server only), 0 failed (11 new). Angular: 1,241 of 1,241; `ng build` passes, initial bundle
796.37 kB. Not run: the SQL Server provider smoke against `deploy/sqlserver` (the migration is three `int` columns with a
default).

## Verification (2026-11-08)

| Check | Result |
|---|---|
| New backend tests (`Tests/Valuation`: replay examples C-01 and C-16, rounding, negative stock, both scopes, refunds both ways, production return and output, opening once, sale and refund costs, settings, the read-only scan, the branch rule over HTTP) | 19 of 19 |
| Full backend suite, three namespace groups | 532 + 463 (7 skipped, SQL Server only) + 696; the 19 Security failures of the first run were the new routes missing from the route plan, the legacy snapshot and the count pins (312 routes now), and a `[FromServices]` the test hosts need; Security and Valuation rerun: 255 passed + 4 |
| SQL Server facts (throw-away 2022 container on 14334) | 10 of 10 |
| Provider smoke on a fresh demo-seeded SQL Server database | 250 passed, 1 usual skip; the migration and its `Kind` backfill applied; the opening took 16 rows; after the smoke the report has 8 rows, none flagged, the value identity holding on each |
| Angular | 1,200 of 1,200 (5 new in `stock-valuation.component.spec.ts`); `ng build` passes, initial bundle 795.78 kB (last recorded 792.63 kB; the 500 kB budget warning predates this) |
