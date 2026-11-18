# Hire equipment as fixed assets — the register (REN-12, Phase M7a, as built)

**Decision (owner, 31 Oct 2026, REN-12; also answers STK-07 and GL-04):** the equipment the company hires out is fixed
assets, not merchandise stock. The depreciation policy was asked of the accountant and is not answered yet. Under the
owner's standing rule (2026-11-09) every answer is a back-office setting the accountant sets in the panel, the suggested
answer as default, and **today's behaviour is kept until the accountant switches it on**.

Where: **Reports › Fixed assets** (`reports/fixed-assets`), three tabs — Register, Month's depreciation, Settings.
Code: `ParadiERP.Infrastructure/FixedAssets/`, routes `ParadiERP.API/Endpoints/FixedAssetEndpoints.cs`, entities
`ParadiERP.Domain/FixedAssetEntities.cs`, migration `FixedAssetRegister` (SQLite and SQL Server sets).

## The model

Hire equipment is counted in quantities (2,000 frames), not serial pieces, so the register works on **pools**: one per
hire product (`Product.IsRentable`) and branch. A pool holds **layers** — the switch-over's opening, each receiving note,
units found or put back into service — each with its units, cost per unit and first month depreciated. A pool carries
its cost and its accumulated depreciation as running figures.

- Every register event is a stored, append-only row (`FixedAssetMovements`): what moved, how many, and the cost and
  accumulated depreciation it carried — exactly what its ledger entry posted.
- **Depreciation is never stored.** It is computed from the rows on every read (`FixedAssetEngine`) and posted only when
  the accountant posts an ended month.
- Units leave a pool **pro rata**: q of the pool's Q units take q/Q of its cost and of its accumulated depreciation, and
  q/Q of every layer, so the net book value per unit is unchanged by a disposal.

### Straight line, by month

Each month a layer's units **at the month's end** are charged `unit cost × (1 − residual %) / life` until the life is
spent. The charge is the rounded cumulative figure less the rounded figure a month before, so a layer that keeps its units
is depreciated exactly to the thousandth.

REN-12's worked example (pinned in `FixedAssetRegisterTests`): 2,000 frames × 6,500 = 13,000,000 over 60 months →
216,666.667 or .666 a month, **2,600,000 a year, 5,200,000 net book value after 3 years**, 0 after 5.

**Month rules (decided here, written in the settings page's words):**

- The opening layer is depreciated from the switch-over month, that month included.
- A purchase (or units found, or scrap put back into service) is depreciated **from the month after** it.
- Units disposed of are not depreciated in the month they leave; they leave with the depreciation accumulated **through
  the month before**, whether or not those months are posted yet (a later posting of those months still charges them,
  since it counts the units at each month's end).

## Settings (accounting.manage; read with reports.accounting)

| Setting | Options | Default | Notes |
|---|---|---|---|
| Hire equipment is | `Stock` (310, today) · `FixedAssets` | **Stock** | Nothing in the register posts until FixedAssets is chosen **and** the switch-over posted. Once posted, it does not go back to stock here. |
| Depreciation method | `StraightLine` (suggested) · `DecliningBalance` | StraightLine | The company's default; each type may have its own (below). Built 2026-11-10. |
| Switch to straight line when it is higher | on · off | **on** | Declining balance only; each type may have its own. |
| Method per type | per hire product: method, coefficient, switch — each "as the company" or its own | none | `FixedAssetMethods`. The coefficient, when empty, is the legal one for the type's life. |
| Default useful life (months) | 1–600 | none | Needed before the switch-over unless every type has its own. |
| Useful life per type | per hire product, 1–600 or "default" | none | `FixedAssetLives`. |
| Residual value | 0–<100 % of cost | 0 | Never depreciated. |
| Pools open at | `CostInForce` (suggested) · `AssetUnitValue` | CostInForce | CostInForce: the value 310 carries them at — the cost price, or the shadow's weighted average once sales are costed at it (C-01). AssetUnitValue: the product's asset value (`Product.AssetUnitValue`, when above 0), the difference to the opening adjustment slot. |
| Switch-over month | `yyyy-MM` | none | The first month depreciated. The switch-over posts **during that month only** (a future month waits; a past one is refused: what was held then is not known). |

**Account map slots** (rows of `ManufacturingAccountMappings` under `fixed_assets.`, seeded **empty, never guessed**; the
seeded chart has no class 2 account, so the accountant adds them to the chart first):

| Slot | Hint (SCF) | Needed |
|---|---|---|
| `fixed_assets.asset` | 215 / 218 | for the switch-over |
| `fixed_assets.accumulated_depreciation` | 2815 / 2818 | for the switch-over |
| `fixed_assets.depreciation_expense` | 681 | for the switch-over |
| `fixed_assets.disposal` | 652 / 752 | for the switch-over |
| `fixed_assets.opening_adjustment` | — | only when the pools open at a value other than 310's |

**Locks.** Once the switch-over is posted: the default life, residual, opening value and month are fixed (every
month and every disposal was valued with them); a type's own life can still be set while the register holds none of it;
the four required slots can be changed but never emptied (`slot_in_use`). The **method** is fixed type by type, once
depreciation is posted on the type (see "Declining balance" below).

## Entries per event

Off (the default, or FixedAssets chosen but no switch-over posted): **every document posts exactly as before**
(`Until_the_switch_over_is_posted_…` pins it; the existing stock, rental and valuation suites are unchanged). On, for a
hire product:

| Event | Today (off) | With the register |
|---|---|---|
| **Switch-over** (once) | — | One entry `fixed-asset-switch`, dated the switch-over month's 1st, per branch: Dr asset (opening value) / Cr 310 (stock value) / Dr or Cr opening adjustment (the difference). Value held = on hand + on rent + in maintenance + in transit from the branch, at the cost method in force. The pools open; the hire scrap each branch holds (318) is recorded at qty × that unit cost and stays in 318. |
| **Receiving note** | Dr 310 / Cr 401 | Dr **asset** / Cr 401 for the hire lines (others unchanged); the units join the pool, depreciated from the month after. |
| **Monthly depreciation** (posted by the accountant) | — | Per branch, a line per pool: Dr depreciation charge / Cr accumulated depreciation. Source `fixed-assets`. |
| **Lost on rent** (return note, missing) · **written off** (state route) · **counted missing** (stocktake −) · **lost in transit** (declared, or shortfall confirmed) | Dr 657 / Cr 310 at cost | Source `fixed-asset`: Dr accumulated depreciation (share) / Dr **disposal** (net book value) / Cr asset (cost share). |
| **Wrecked** (return note, beyond repair; state route or maintenance job → scrap) | Dr 318 / Cr 310 at cost | Dr accumulated depreciation / Dr 318 at **net book value** / Cr asset — the wreck stays an asset (scrap) at its book value, as today it moves at cost with no loss. REN-10 (writing scrap down) is unchanged and still open. |
| **Hire scrap written off** (state route Scrap → WrittenOff) | Dr 657 / Cr 318 at cost | Dr 657 / Cr 318 at the value the register carries the scrap at (switch-over scrap at its value then, later wrecks at their net book value; average per unit). |
| **Hire scrap back into service** (Scrap → Available/Maintenance) | Dr 310 / Cr 318 | Dr asset / Cr 318 at the carried value; a new layer from the month after. |
| **Found at a count** (stocktake +) | Dr 310 / Cr 657 | Dr asset / Cr disposal at the count's value (today's figure); a new layer from the month after. |
| **Transfer received**, and goods **returned to another branch** than issued them | Dr 310 @to / Cr 310 @from, inter-branch | Dr asset @to / Cr asset @from (cost share), Dr accumulated depreciation @from / Cr @to (its share), inter-branch. The units keep their layers' months. |
| Dispatch, cancel, available ↔ on rent ↔ maintenance | nothing | nothing (units in transit stay in the source's pool). |

**Month posting** (`/api/fixed-assets/depreciation/post`): the shared `LedgerMonthPoster` — from the switch-over month,
ended months only, in order, never twice, closed books refused (`books_closed`), the whole company only; reversal
(`/reverse`) by mirror entries, the last month posted first, then the month may post again under its next generation.

## What changed in stock and rental code (all behind `FixedAssetBook.HoldsAsync`, which is false while off)

- `StockPosting.Receiving`: a `ValuedLine` may name its account (`Account`, null = 310 as always).
- `StockEndpoints` receiving: a hire line asks the register for the asset account.
- `CatalogEndpoints` stock adjustment: a hire product's count posts through the register instead of `StockPosting.Adjustment`.
- `StockStateMoves` (manual route and maintenance job completion): a hire product's move posts through the register
  instead of `StockPosting.StateChange`.
- `RentalDocuments.MoveReturnedStockAsync`: a hire product's implicit transfer and its wrecked/lost units post through the
  register (the record `ReturnPosting` gained its product id). Charges, rent, compensation and quantities are untouched.
- `StockTransfers` receive / declare lost / confirm shortfall: hire lines post through the register and are left out of
  the step's own entry.
- `CostPriceRevaluationPosting` (B-F01): products the register holds are not revalued (they are at historical cost).
- `SalesCostSwitch` (C-01): once the register is active, the asset account counts as a stock account in the switch's
  figure (the shadow still counts the hire goods), so a later C-01 switch does not post them back into 310.
- Stock movements, quantities, the weighted-average shadow and every `UnitCost` written on a movement are unchanged.

## Not handled in M7a (shown, not hidden)

The register screen shows **units in the register beside units in stock** for each pool, so any difference these leave is
visible:

- **A hire item sold at the till** (or refunded), and **direct invoices** that move stock (P0.3): they still post cost of
  sales against 310 at cost. The till's path is "never block the sale"; selling a fixed asset (proceeds to 752? net book
  value to 652?) is the accountant's answer to REN-12 question 3. Next step.
- **Opening stock** entered for a hire product after the switch-over (still 310), and manufacturing or maintenance-part
  movements of a hire product (still posted as today, through their own practice ledgers).
- **C-16 negative-stock gaps** of hire products: still against 310.
- A product marked rentable (or no longer rentable) **after** the switch-over is held (or released) from that moment;
  its earlier units stay where they were posted.
- The scrap held at the switch-over is carried at qty × the unit cost then (318 has no per-product balance).
- Units moved by an untouched flow show as a units difference; the pool's money is never guessed for them.

## Left for the next phases (designed to attach)

- ~~Capitalised repairs (MS-30) and per-piece history (MS-38)~~: built in M7d (below).
- ~~Declining balance~~: built 2026-11-10 (below). Changing a life prospectively after the switch-over; undoing a switch-over.

## M7d — as built (2026-11-09): capitalised repairs and pieces

Maintenance's MS-30 "capitalised above an amount" and MS-38 "per piece" attach here as M7a designed them
([../maintenance/plan.md](../maintenance/plan.md), "M7d — as built", for the settings, the conditions and every entry).

- **A new kind, `CapitalisedRepair = 9`.** Written at a repair job's completion when MS-30 capitalises it
  (`FixedAssetBook.CapitaliseRepairAsync`): the pool (the product at the job's branch), Qty = the units repaired, Cost = what
  the repair's entry debits to the asset, `StartMonth` = the month after, the job as its document, the piece as its
  `Reference`. Only onto a pool that holds units.
- **A layer of cost only.** The engine adds it as a layer that carries cost and is depreciated like a purchase (the type's
  life, from the month after) but adds no unit: `FixedAssetPool.Qty` counts unit layers only. A disposal or transfer of q of
  Q units takes q/Q of it as of every layer; the last unit takes it whole. A pool's net book value per unit therefore rises by
  the repair's share and falls back as it depreciates.
- **Its entry is maintenance's.** The movement posts nothing of its own (no `SourceType`): its entry, Dr
  `fixed_assets.asset` / Cr the accounts the repair was charged to (own repairs, a stock slot, own work capitalised, the
  voucher's expense account), is in the maintenance practice ledger of the completion month and posts with it (source
  `maintenance`), checked against the register by `capitalised_tie`. The pool's history shows it as "Capitalised repair" with
  the job and the piece, "posted" once a live maintenance posting covers its month. MS-30 is refused while the register is not
  active, so a capitalised repair always lands in an active register.
- **`Reference`** carries a piece's reference (MS-38) on a capitalised repair; pools stay the accounting unit and pieces a
  history beside them (Maintenance › Repair history).

Pinned (`MaintenanceCapitalisationTests`): 110 scaffolds at 132,000 over 60 months and a repair of 3,500 on two of them — the
pool at 135,500 for the same 110 units, 2,200 charged in the completion month and 2,258.333 the next; 100 units at 120,000 and a
repair of 6,000 — half the units leave in May with 63,000 of cost and 4,050 of depreciation, the rest takes the repair's layer
whole.

## Declining balance — as built (2026-11-10, deployed `eea95fa`)

**Decision (owner, "go", 2026-11-10):** declining balance is a choice the accountant makes, per type with a company default,
under the standing rule — a back-office setting, the default today's behaviour (straight line), nothing waiting on a
signature. Every existing asset stays straight line (the migration leaves `Method` at `StraightLine`).

**The method (SCF; the tax code's "amortissement dégressif"):** the declining rate is the straight-line rate × a
coefficient, applied to the net book value at the start of each **calendar year**.

- **Coefficient.** Suggested from the type's life, as the tax code sets it: **1.5** for 3–4 years (and below), **2** for 5–6
  years, **2.5** above 6 years (`FixedAssetDeclining.LegalCoefficient`: up to 59 months 1.5, 60–72 months 2, 73 and more
  2.5). The accountant may enter another, from 1 to 5. The tax code allows the declining method only from three years; a
  shorter life is not refused, the accountant decides.
- **First year.** Prorated by its months from the month the layer starts — the month a straight-line layer starts too: the
  switch-over month for the opening, the month after for a purchase, units found, scrap back into service or a capitalised
  repair. The life then runs its months from there, as on the straight line, so a mid-year start ends mid-year and the last
  year is prorated the same way.
- **Each year:** declining charge = opening value × rate × months / 12; straight-line charge = (opening value − residual) ×
  months / months left. **Switch to straight line** (an option, **on** by default): in the first year the straight-line charge
  exceeds the declining one, the straight line takes over for the rest of the life. The last year always takes what is left
  down to the residual value, so the asset reaches it exactly at the end of its life (with the switch off, the last year
  carries the rest). A residual value is never depreciated; the declining rate runs on the whole net book value.
- **By month.** A year's charge is spread evenly over its months. As on the straight line, each month charges the rounded
  cumulative figure (units × a unit's cumulative depreciation, to the thousandth) less the one a month before, so the total
  over the life is exactly cost − residual and the rounding is absorbed in the last month. The monthly poster, closed
  months, the order of months and the reversal logic are unchanged.
- **Disposals** (lost, written off, counted missing, wrecked, transferred) work as before: q of Q units take q/Q of the
  pool's cost and of its accumulated depreciation through the month before, whatever the method; the units left keep their
  declining schedule.

**Worked example** (pinned in `FixedAssetDecliningBalanceTests`): 1,000,000 DA over 5 years, coefficient 2 (40 %), from
January.

| Year | Opening value | Declining (40 %) | Straight line on what is left | Charge | Closing value |
|---|---:|---:|---:|---:|---:|
| 1 | 1,000,000 | 400,000 | 200,000 (÷ 5) | **400,000** | 600,000 |
| 2 | 600,000 | 240,000 | 150,000 (÷ 4) | **240,000** | 360,000 |
| 3 | 360,000 | 144,000 | 120,000 (÷ 3) | **144,000** | 216,000 |
| 4 — **switch** | 216,000 | 86,400 | 108,000 (÷ 2) | **108,000** | 108,000 |
| 5 | 108,000 | — | 108,000 (÷ 1) | **108,000** | 0 |

Monthly: 33,333.333 or .334 in year 1, 20,000 in year 2, 12,000 in year 3, 9,000 in years 4 and 5. With the switch off, year 4
charges 86,400 and year 5 the remaining 129,600.

- **Mid-year start** (April): 300,000 for the 9 months of year 1, then 280,000, 168,000, the switch in year 4 at 112,000
  (252,000 over the 27 months left × 12), 112,000, and 28,000 for the 3 months of the last year — the life ends in March,
  sixty months after it began.
- **Residual 10 %:** 400,000, 240,000, 144,000, 86,400 (the straight line on 116,000 over two years, 58,000, is lower: no
  switch), and 29,600 in the last year, ending at 100,000.
- **Disposal:** 2,000 frames at 6,500 over five years at 40 %: after the first year (5,200,000 charged), 200 lost leave with
  1,300,000 of cost and 520,000 of depreciation — 3,900 each net, as before the loss; the 1,800 left are charged 2,808,000 the
  next year.

**The setting.** Reports › Fixed assets › Settings: the company's method and its "switch to straight line when it is higher"
(on); in the table of types, each type's own method ("as the company", straight line or declining), its coefficient (the
legal one suggested and shown) and its switch ("as the company", yes or no). `PUT /api/fixed-assets/settings` takes
`switchToStraightLine` and `methods` (`productId`, `method`, `decliningCoefficient`, `switchToStraightLine`, each null to follow
the company; a line with every field null clears the type's row). No new route or permission: the settings route
(accounting.manage) as before.

**The lock.** A type's method may change only while no depreciation is posted on it: a live (not reversed) month posting
that charged it, or an event (a disposal, a wreck, a transfer) that carried its accumulated depreciation out in its own
entry. After that, any change to what it is depreciated by — its own row, or the company's default for a type that follows
it — is refused **409 `method_locked`**, naming the types. Restating the same method in other words is not a change. Once its
months are reversed (and no event carried depreciation out), the type's method may change again. Before the switch-over
nothing is posted, so nothing is fixed. The other figures fixed at the switch-over (default life, residual, opening value,
month) are unchanged; the method is no longer among them.

**The schedule.** A pool's history (`/api/fixed-assets/register/history`) carries its schedule year by year — the value at the
start of the year (the switch-over's opening in the first), the year's charge, what events added or took out at net book
value, the value at the end — from the switch-over to the end of its last layer's life: the months ended as computed, the
rest projected at the units held. The year a declining-balance layer turns to straight line is marked ("Switch to straight
line", in words). The register's life column says each type's method ("Declining × 2.0, then straight line").

Code: `FixedAssetDeclining` and `FixedAssetMethodTerms` (`FixedAssetEngine.cs`), `FixedAssetData.DepreciationPostedAsync`,
`FixedAssetSettingsStore`, `FixedAssetRegisterReport.Schedule`; entity `FixedAssetMethodChoice` (table `FixedAssetMethods`),
`FixedAssetSettings.SwitchToStraightLine`; migration `DecliningBalance` in both sets (the switch's column defaults to on).
