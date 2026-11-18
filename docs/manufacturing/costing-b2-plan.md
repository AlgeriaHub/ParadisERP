# Manufacturing costing B2 — cost reconciliation and reporting (plan, approved; as built in [costing-b2.md](costing-b2.md))

Status: **plan only, 2026-10-29.** Audited against `main` at `d463c54`, which is B1's merge `2172f11`
plus two evidence/docs commits. Nothing is implemented, migrated, committed, pushed or deployed.
Production is still `1225f0a`. B1's migrations are not in production.

B2 is **read-only**. It adds:
- no table, column or migration;
- no write route;
- no ledger entry, inventory valuation, `Product.CostPrice` write or payroll change;
- no stock-setting change;
- no financial finalization.

It reads B1's frozen sheets and late costs, and (optionally) today's live inputs. Everything it shows is
**provisional**.

---

## 1. What B1 actually stores (audit)

### 1.1 Entities (`ParadisERP.Domain/ManufacturingCostSnapshotEntities.cs`)

| Table | Key / unique | What it holds | Precision |
|---|---|---|---|
| `ManufacturingCostSheet` | unique `OrderId`, unique `RequestId` | One per order. `Kind` Created/Reconstructed; `Status` Provisional only; `CalculationVersion` `"B1.0"`; `SourceState` Complete/IncompleteLegacy (DB default 1 = legacy); `SourceSnapshotJson` + `SourceHash` (SHA-256 hex); `AcceptedBaseQty`, `RejectedBaseQty`, `TotalCost`, `UnitCost`, `ProvisionalReasons`, `CreatedBy(UserId)`, `CreatedAtUtc` | qty 28,10; total 18,3; unit 28,10 |
| `ManufacturingCostElementLine` | (Sheet, Element) | 7 rows: materials, consumables, accessories, labour, overhead, expenses, recovery. Quantity (labour hours, else 1), UnitCost, TotalCost, Status, SourceReference | total 18,3 |
| `ManufacturingMaterialCostSnapshot` | (Sheet, OrderMaterial) | Qty = consumed + scrapped, UnitCost = issue average, TotalCost = consumed + scrap value, `CostBasis` `reference_cost` | total 18,3 |
| `ManufacturingLabourRateSnapshot` | (Sheet, Employee, Year, Month) | Hours, **Rate**, **TotalCost** (nullable when no rate), Status recorded/provisional, Reason | **payroll-sensitive** |
| `ManufacturingExpenseAllocationSnapshot` | (Sheet, Expense) | ExpenseId, PaymentVoucherId, Amount | 18,3 |
| `ManufacturingScrapRecoverySnapshot` | (Sheet) | RejectedQty, RecoveryValue (always 0 today) | 18,3 |
| `ManufacturingReleaseValueSnapshot` | (Sheet, OutputRelease) | Quantity, Value, UnitValue | 18,3 / 28,10 |
| `ManufacturingLateCost` | (Sheet, Kind, SourceReference) | Kind (free text ≤ 32), SourceReference (free text ≤ 128), Amount (≠ 0, may be negative), Reason, RecordedAtUtc | 18,3 |

All eight implement `IManufacturingCostHistory`. `PosDbContext.GuardManufacturingCostHistory` refuses
Modified/Deleted entries at `SaveChanges`.

### 1.2 Write paths (B1, unchanged by B2)

| Route | Permission | Notes |
|---|---|---|
| `GET /api/manufacturing/orders/{id}/cost-snapshot` | `manufacturing.costs.view` | Returns totals, releases and late costs. It returns no element lines and no labour rows. |
| `POST …/cost-snapshot` | `manufacturing.costs.snapshot.create` | Completed orders only; explicit; idempotent by request id; `reconstruct` flag |
| `POST …/cost-snapshot/late-costs` | same | Deduplicated by (sheet, kind, source) |

All three need the `Manufacturing:Costing` switch, and they resolve line reach before any lookup.

### 1.3 Source capture

`SourceSnapshotJson` holds the **complete `CostingInput`**:
- planned, accepted and rejected quantities, and the completed flag;
- standard hours and rate;
- materials with issued, returned, consumed and scrapped quantities and values;
- bookings, and rates per employee-month (PerHour, Kind, EmployerCost, PaidHours, WithoutEmployerSocial);
- expenses with their settled flag, and releases with their good-to-date quantities;
- the policy (overhead rate, Approved) and the recovery value.

It also holds the engine's element statuses, notes and release allocations, and the identities of the
source rows:
- movement lines with their `UnitCostSnapshot`, and consumptions;
- bookings;
- expenses, and vouchers with their status;
- output releases with their `UnitCostSnapshot` and `PostingState`.

So **B2 can replay the B1.0 calculation exactly from the capture**, without reading mutable rows.

### 1.4 Findings that shape B2

| # | Finding | Consequence for B2 |
|---|---|---|
| G1 | Completion does not freeze; a sheet exists only when someone asks. | A **missing sheet** is a reportable gap. A `Created` sheet can be made days after completion: report the **freeze lag**. |
| G2 | Expenses can still be added to a **completed** order (only Cancelled is refused, `ProductionOrders.AddExpenseAsync`). Payroll runs approve or reverse after the freeze. Voucher statuses change. | The live cost drifts from the frozen one. B2 reconciles the drift against the recorded late costs (§4.6). |
| G3 | Late cost `Kind` is free text. Tests use `"expense"`. There is no element column. | B2 needs a **classifier** (§4.3). An unknown kind is shown as *unclassified*, never guessed. |
| G4 | A late cost has no recorder identity. | The register shows "recorder not recorded". Adding a column is a schema change, so it is out of B2 (decision OD-7). |
| G5 | The unique (Sheet, Kind, SourceReference) index is **case-sensitive on SQLite and case-insensitive on SQL Server** (default collation). | B2 flags references that differ only in case or spacing (§4.5). |
| G6 | The B1 GET orders releases by `Guid` and returns `Number = ""` and `CarriesRemainder = false`. SQL Server and SQLite sort Guids differently. | B2's read model orders releases by `OutputRelease.AtUtc`, then `Number` (the engine's order). It takes the carrier from the capture's `ReleaseAllocations`. B1's GET stays as it is. |
| G7 | The capture stores `PayrollRunId = null` for every booking. | A labour late cost cannot be traced to a payroll run line from the capture: a stated limitation. |
| G8 | Release values are frozen only when the sheet is made, not when each release happens (plan §2.9 said the latter). | Early releases carry the rates known at freeze time: a stated limitation. |
| G9 | Completion refuses any material still at the line (`materials_at_line`). | At freeze, Σ material rows = M + K + A within rounding. That is a checkable equation. |
| G10 | The capture includes **EmployerCost, PaidHours and per-hour rates**. | The capture is **never returned raw** by any route (§6). |
| G11 | Immutability is enforced in EF's change tracker, not by database triggers. SHA-256 is tamper-evident only against an editor who does not also rewrite the hash. | B2's integrity checks (§4.7) detect edits made outside EF. They do not prevent them. |

---

## 2. Architecture

```
Application/Manufacturing/
  ProductionCostEngine.cs        + public const string Version = "B1.0"   (constant only; no behaviour change)
  CostReconciliation.cs          NEW, pure: equations, rounding, classification, duplicate flags, allocation.
                                 No DB, no clock; same input → same output on both providers.
  CostingCapture.cs              NEW, pure: parse SourceSnapshotJson → CostingInput (+ release allocations),
                                 version-gated; returns null with a reason when it cannot.
Infrastructure/Manufacturing/
  ProductionCostHistory.cs       NEW, read-only service: loads sheets and children with AsNoTracking,
                                 applies line reach, sums in memory, maps DTOs, redacts per caller.
Application/Contracts/
  ManufacturingCostReportDtos.cs NEW
API/Endpoints/ManufacturingOperationEndpoints.cs   + 7 GET routes (§5)
API/Authorization/RoutePermissions.cs             + 7 entries, all manufacturing.costs.view
WebUI: order-costing.component (frozen panel), cost-reconciliation.component (new page), api service,
       status-tones, i18n ar/fr/en, nav entry
```

Principles:
1. **The stored rows are the authority** for every original figure. Replay is used only to verify them
   (§4.7) and to derive what B1 did not store: variances, scrap value split, estimate.
2. **Money is summed in memory, never in SQL.** SQLite stores decimals as TEXT, and its `SUM`/`ORDER BY` would
   use floating point. Queries filter and project; C# adds.
3. **One query per child table**, keyed by `SheetId`, combined in memory. No join between two one-to-many
   children, so a late cost is never multiplied by the number of releases.
4. **Behind `Manufacturing:Costing`**, the switch B1 already uses. Production has no `Manufacturing`
   key, so B2 is off there until the owner switches it on.
5. **No writes:** the service takes a context with `QueryTrackingBehavior.NoTracking`, and a test proves
   that every table is identical before and after each route.

---

## 3. Reports and screens

| # | Report | Question it answers | Source | Route |
|---|---|---|---|---|
| R-B1 | **Order cost reconciliation** | For one order: original frozen sheet, late adjustments, adjusted provisional, finalized (n/a), and every check | Stored rows + capture replay (+ live, optional) | `GET /orders/{id}/cost-reconciliation` |
| R-B2 | **Cost history** (original frozen cost sheets) | Every frozen sheet in a period: original, late, adjusted, unit cost, kind, provenance, lag, flags | Stored rows | `GET /reports/cost-history` |
| R-B3 | **Late-cost register** | Every late cost: element, amount, source, duplicate and overlap flags; totals by element | Late rows + sheet sources | `GET /reports/late-costs` |
| R-B4 | **Cost variances** (material, labour, expense) | Original and late-adjusted variance by component, against the frozen estimate | Capture replay + late rows | `GET /reports/cost-variances` |
| R-B5 | **Release reconciliation** (partial production) | Do release values add up to the cost? What was left unreleased, and what did the final release carry? Stock reference value against production value | Release rows + capture + `OutputRelease` | `GET /reports/release-reconciliation` |
| R-B6 | **Scrap and rejected output** | Rejected quantity and rate, material scrap value, spoilage absorbed, recovery status | Scrap row + capture replay + late rows | `GET /reports/scrap-costs` |
| R-B7 | **Cost gaps** (incomplete or missing components) | Completed orders with no sheet, legacy sources, incomplete or provisional elements, unclassified or duplicate late costs, failed checks | Everything above | `GET /reports/cost-gaps` |
| R-B8 | **Reconstructed and legacy sheets** | Which sheets are not contemporaneous freezes, by how much, and what they can still prove | R-B2 filtered: `provenance=reconstructed\|legacy\|late` | (R-B2 filter) |
| R-B9 | *Optional B2.4:* **Unrecorded drift** | Where today's live cost differs from adjusted provisional and no late cost explains it | Live engine + stored | `GET /reports/late-cost-candidates` |

Every report shows the **four figures** with words, never a colour alone:

| Figure | Definition | Shown as |
|---|---|---|
| **Original snapshot** | `sheet.TotalCost`, unchanged forever | "Original (frozen dd/MM/yyyy HH:mm)" plus a kind pill: *Created* / *Reconstructed* |
| **Late adjustments** | Σ late amounts recorded up to the as-of day | "Late adjustments (n)", signed |
| **Adjusted provisional** | Original + late adjustments | "Adjusted provisional" plus a *Provisional* pill |
| **Financially finalized** | Not implemented | The words "Not available — financial finalization is not implemented (accountant approval pending)". **Never a number, never 0.** DTO: `null` plus `finalizationState: "not_implemented"`. |

### 3.1 Screens (Angular, Riaya patterns; no behaviour change elsewhere)

**S1. Order page, Cost tab: a "Frozen cost" panel** (`order-costing.component`), above the live Phase A
sheet:
- **When a sheet exists:**
  - a state line in words: "Provisional · frozen 12/10/2026 14:05 by … · not financially finalized";
  - a metrics strip of the four figures (lead tile: Adjusted provisional);
  - an As-of date field (`app-date-input`), defaulting to today in the company's zone;
  - the element table (original / late / adjusted), late costs, releases, scrap, variances, labour and checks.
- **Without a sheet:** an *Not frozen* pill and one sentence. There is no create button: B2 stays
  read-only (OD-6).
- Per-employee money columns appear only when the DTO says `perEmployeeMoneyShown`.

**S2. New page "Cost reconciliation"** (`manufacturing/cost-reconciliation`): one sidebar entry under
Manufacturing, `access: mfgCosts`, `scope: 'none'`.
- **Page toolbar:** a view group with a `.ui-filter__label` (History · Late costs · Variances · Releases ·
  Scrap and rejects · Gaps · Reconstructed), then line, From/To (`app-date-input`), Basis (completed / frozen /
  recorded) and As-of.
- **State in the URL:** `?view=late-costs&line=…&from=…&to=…`, so deep links, Back and Forward work.
- **Layout:**
  - `.ui-metrics` with one lead tile;
  - `.ui-surface > .ui-table.ui-table--cards` (cards under 640px through `data-label`);
  - `app-table-state` for loading, empty, no results and error; `app-list-footer` for paging.
- **Order number** is a `.ui-link` to the order's Cost tab. Row menus are not needed (read-only).
- **New status families** in `ui/status-tones.ts`:
  - sheet kind: created, reconstructed;
  - provenance: complete, incompleteLegacy;
  - check: pass, fail, notApplicable;
  - finalization: provisional, notImplemented;
  - late: classified, unclassified, possibleDuplicate, overlapsOriginal.
- **Formatting:** money through the regional service, dates through `I18nService`.
- **Mirroring:** logical properties only, so Arabic RTL mirrors French LTR.

Print and CSV are out of B2 unless the owner asks (OD-4).

---

## 4. Reconciliation formulas

Notation, per sheet *s*:
- **O** = `TotalCost`; **Qa**, **Qr** = accepted and rejected quantity.
- **Oₑ** = the stored element `TotalCost` for e ∈ {M, K, A, L, Ov, E, R}.
- **sₑ** = +1 for every element except R, where sₑ = −1.
- **ℓᵢ** = late amounts; **T** = the as-of instant (end of the as-of company day, in the company's zone).

### 4.1 Rounding (deterministic, both providers)

- `Money(x) = Math.Round(x, 3, MidpointRounding.AwayFromZero)` and `Unit(x) = Math.Round(x, 6, AwayFromZero)`,
  exactly as B1. Percentages are `Math.Round(x, 2, AwayFromZero)`.
- Stored values are already rounded. **Sums of stored values are never re-rounded**; a full-precision value is
  rounded once, where it is shown.
- **The residual is shown, never hidden.** Where rounded parts must equal a rounded whole, the difference is a
  visible "rounding" line with a tolerance:
  - element residual |ρ| ≤ 7 × 0.0005 = 0.0035;
  - material rows ≤ n × 0.0005;
  - labour rows ≤ m × 0.0005.

  Beyond the tolerance, the residual is a **failed check**, not a rounding line.
- The final release absorbs the release rounding (B1's convention, C-17 pending).
- No figure is computed in the browser.

### 4.2 Core equations

```
(1) Element identity   O = Σₑ sₑ·Oₑ + ρ                                   |ρ| ≤ 0.0035
(2) Late to date       L(T) = Σ ℓᵢ   for RecordedAtUtc ≤ T                (stored 3 dp, no re-rounding)
(3) Adjusted           A(T) = O + L(T)
(4) By element         Aₑ(T) = sₑ·Oₑ + Lₑ(T)   (signed contribution to cost; recovery appears negative)
                       Σₑ Aₑ(T) + L_unclassified(T) + ρ = A(T)
(5) Unit cost          u₀ = sheet.UnitCost (= Unit(O ÷ Qa));   u(T) = Unit(A(T) ÷ Qa);   Qa = 0 → none, in words
(6) Finalized          F = not available (null, "not_implemented")
```

**Sign convention:** a late amount is its effect on the order's total cost. Positive raises C. A recovery
late cost of −50 means 50 more recovered. The accountant confirms this as AD-2.

### 4.3 Late-cost classification

The kind is trimmed and lower-cased (invariant), then mapped:

| Kind | Element |
|---|---|
| material, materials | M |
| consumable, consumables | K |
| accessory, accessories | A |
| labour, labor | L |
| overhead | Ov |
| expense, expenses | E |
| recovery, scrap_recovery | R |

Anything else is **unclassified**: shown in its own row and counted in A(T), never spread over the
elements. A classified share is never inferred from the reason text.

### 4.4 Partial releases (completed orders, per B1)

Releases are ordered by `OutputRelease.AtUtc`, then `Number` (ordinal), matching the engine.

```
(7)  If releases exist:   Σᵣ valueᵣ = O            exactly (B1 gives the carrier O − Σ earlier)
     If none:             kept in charges = O       (D13)
(8)  Released quantity    Qrel = Σᵣ qtyᵣ;   unreleased = Qa − Qrel
     unreleased > 0 → "final release carried the cost of {unreleased} accepted units not released"
     unreleased < 0 → "released more than accepted"
(9)  Carrier unit value   v_last = value_last ÷ qty_last;  compared with earlier releases' unit values
     value_last < 0 → "final release below zero" (engine note final_release_below_zero)
(10) Stock reference      refᵣ = Money(qtyᵣ × OutputRelease.UnitCostSnapshot)   (Product.CostPrice at release, F1)
     gapᵣ = valueᵣ − refᵣ      informational: what stock would have received against the production value
(11) Late adjustments and releases: under C-10 (recommended "stay in the charges") late costs are NOT
     added to release values: release values stay original; A(T) − O is shown as "kept in charges".
     Optional, only if the accountant chooses allocation (AD-1): shareᵣ = Money(L × qtyᵣ ÷ Qrel) for every
     release but the last; last = L − Σ others; Qrel = 0 → everything stays in the charges.
```

### 4.5 Duplicate prevention and overlap

**Guaranteed by the database (B1):**
- one sheet per order, and one per request id;
- one late cost per (sheet, kind, source).

**Guaranteed by B2's reading:**
- Each late cost counts once. Every child table is loaded separately by SheetId, so a join cannot fan out.
- Totals add row values, and a row belongs to exactly one sheet.
- Order is deterministic (order number ordinal, then RecordedAtUtc, then Id as a string), so two exports
  differ only where the data does.

**Flags (reported, never merged or netted).** `norm(x)` is trim, internal spaces collapsed, upper-invariant.

| Flag | Rule |
|---|---|
| `possible_duplicate` | Same sheet, same `norm(SourceReference)`, different kind or a case/space-only difference (G5) |
| `source_on_several_orders` | Same `norm(SourceReference)` on more than one sheet |
| `overlaps_original` | The reference names something already inside the frozen sheet: a voucher `Number` or Id, or an expense Id, found in the capture's `SourceIdentities`. Only references that parse as such can be checked; others are "unverifiable". |
| `unclassified` | §4.3 |

### 4.6 Late-cost reconciliation against today (optional B2.4, R-B9)

```
(12) V   = Money(live engine total today)          (ProductionCosts.InputAsync + engine, CostingPolicy.Default)
(13) Uₑ  = (Vₑ − sₑ·Oₑ) − Lₑ(now)                  unexplained drift per element
     U   = V − A(now) = Σ Uₑ − L_unclassified + rounding
```

Identity changes are listed against the capture:
- expenses added or removed;
- bookings added or removed;
- an employee-month's rate kind changed (standard → actual, actual → none);
- voucher status changes.

Comparable only when `CalculationVersion == ProductionCostEngine.Version`. Otherwise the report says "method
changed since freeze — not comparable" and computes nothing. Nothing is written; recording a late cost stays
B1's POST route.

### 4.7 Integrity checks (each pass / fail / not applicable with a reason)

| Code | Check |
|---|---|
| I1 `hash` | `SHA256(SourceSnapshotJson) == SourceHash` (Complete only) |
| I2 `replay` | The B1.0 engine on the parsed capture reproduces every stored element, material, labour and release row (after `Money`/`Unit`) |
| I3 `element_identity` | Equation (1) within its tolerance |
| I4 `release_sum` | Equation (7) |
| I5 `material_rows` | Σ material rows = Oₘ + Oₖ + Oₐ within n × 0.0005 (G9) |
| I6 `expense_rows` | Σ expense rows = Oₑ exactly |
| I7 `labour_rows` | Σ non-null labour TotalCost = O_L within m × 0.0005 |
| I8 `scrap_row` | RecoveryValue = O_R |
| I9 `unit_cost` | `UnitCost == Unit(O ÷ Qa)` (or null when Qa = 0) |

`IncompleteLegacy` sheets have I1 and I2 *not applicable — source not captured*; I3–I9 still run.

### 4.8 Variances (R-B4) — from the capture, per plan §2.11

The variances below are the engine's, replayed on the frozen inputs. The estimate is the one frozen
in the capture, never today's BOM.

| Component | Original | Late | Adjusted |
|---|---|---|---|
| Material usage | Σ (net qty − std qty) × est. unit cost | — | original |
| Material price | Σ net value − net qty × est. unit cost | L_M + L_K + L_A (unsplit) | original + late |
| Labour efficiency | (hours − std hours for output) × std rate | — | original |
| Labour rate | labour − hours × std rate | L_L (unsplit) | original + late |
| Overhead | applied − std hours × rate | L_Ov | |
| Expenses | actual − estimate (estimate is 0 in B1.0: AD-11) | L_E | |
| Recovery | −R | L_R | |
| Unclassified late | — | L_unclassified | |
| **Total** | **O − Money(est. unit cost × Qa)**, with a rounding line | **L(T)** | **A(T) − Money(est. unit cost × Qa)** |

Not available, with the reason in words:
- Qa = 0 or planned quantity = 0;
- legacy source;
- version mismatch.

Per-employee labour variance lines are shown only to rate holders (§6).

### 4.9 Scrap and rejected output (R-B6)

```
(14) rejection rate     = Round2(Qr ÷ (Qa + Qr) × 100)                      Qa + Qr = 0 → none
(15) material scrap     S = Σ Money(scrapped qty × issue average)           (capture replay)
(16) spoilage absorbed  P₀ = Money((O + R) × Qr ÷ (Qa + Qr) − R)            on stored O and R
                        P(T) = Money((A(T) + R') × Qr ÷ (Qa + Qr) − R'),   R' = R − L_R(T)
(17) material wastage   W = material usage variance + S
```

Spoilage is **part of C** (rejects absorbed, D23). It is shown as "of which", never added on top. While
`R = 0` and `Qr > 0`, recovery shows *Not recorded* (C-09 pending), never 0.

### 4.10 Gap codes (R-B7)

| Group | Codes |
|---|---|
| Missing sheet or provenance | `not_frozen` (completed order without a sheet), `source_incomplete_legacy`, `reconstructed`, `freeze_lag` (informational, in company days) |
| Elements (from the stored statuses and capture notes) | `material_reference_cost`, `material_unvalued`, `labour_standard_rate` (hours), `labour_unvalued` (hours), `labour_without_employer_social`, `overhead_not_set`, `expense_unsettled` (amount), `recovery_not_recorded` |
| Releases | `unreleased_output`, `final_release_negative` |
| Late costs | `late_unclassified`, `late_possible_duplicate`, `late_overlaps_original` |
| Checks | `check_failed:<code>` |
| Live (B2.4) | `unrecorded_drift` |

### 4.11 Historical limitations (shown on screen and in the DTO as notes)

1. A sheet exists only when someone requested it. Before that, a completed order has only the moving
   live figure (G1).
2. A `Created` sheet made N days after completion includes everything that changed in those N days; those
   changes are not late costs. The lag is shown.
3. A `Reconstructed` sheet reflects the data on the day it was made, not on completion day. It is
   never presented as a contemporaneous freeze.
4. `IncompleteLegacy` sheets cannot be replayed or hash-checked. They have no variance and no scrap split.
5. Labour late costs cannot be traced to a payroll run line (G7). Partial-release values were frozen at sheet
   time, not release time (G8).
6. Late costs have no recorder identity (G4) and a free-text kind (G3).
7. Materials are at reference cost (C-01). Labour may lack employer social contributions (C-06). Overhead is
   not set (C-08). Recovery is not recorded (C-09). Nothing is financially final.
8. Integrity checks detect edits made outside the application; they do not prevent them (G11).

---

## 5. API contracts (all `GET`, all read-only)

**Common:**
- Switch off → `409 costing_not_enabled`. Missing `manufacturing.costs.view` → `403`.
- An order out of reach → `404`, with the same body as a missing order.
- Registers quietly leave out lines out of reach. A `lineId` filter on such a line returns an empty
  result (as the Phase A WIP report does).

**Query parameters:**
- Days are company days (`yyyy-MM-dd`), converted with `StoreDocumentHistory.Zone(regional)`.
- `from`/`to` default to the current company month and span at most 366 days.
- `basis=completed|frozen|recorded`, default `completed`. `asOf` defaults to now.
- `page` ≥ 1; `pageSize` ≤ 200, default 50.
- Totals cover the whole filtered set, not just the page.

```csharp
// ---- shared ----
public record CostFiguresDto(
    decimal Original, decimal LateAdjustments, decimal AdjustedProvisional,
    decimal? FinanciallyFinalized,            // always null in B2
    string FinalizationState,                 // "not_implemented"
    decimal? OriginalUnitCost, decimal? AdjustedUnitCost);

public record CostSheetHeaderDto(
    Guid SheetId, string Kind, string SourceState, string CalculationVersion, string Status,   // status "provisional"
    DateTime FrozenAtUtc, string? FrozenBy, DateTime? CompletedAtUtc, int? FreezeLagDays, string SourceHash);

public record CostElementReconciliationDto(
    string Element, string Status, decimal Original, decimal Late, decimal Adjusted);   // signed contribution

public record LateCostRowDto(
    Guid Id, Guid OrderId, string OrderNumber, string LineCode, string Kind, string Element,  // element or "unclassified"
    string? SourceReference, string? Reason, bool Redacted,     // redacted: labour kinds without rate access (OD-2)
    decimal Amount, DateTime RecordedAtUtc, IReadOnlyList<string> Flags);

public record CostCheckDto(string Code, string Result, decimal? Expected, decimal? Actual, string? Reason);
public record CostGapDto(string Code, string? Element, decimal? Value);

public record ReleaseReconciliationRowDto(
    Guid ReleaseId, string Number, DateTime AtUtc, decimal Quantity, decimal OriginalValue, decimal UnitValue,
    bool CarriesRemainder, decimal? StockReferenceValue, decimal? StockReferenceGap, decimal? IndicativeLateShare); // share only under AD-1

public record ReleaseReconciliationDto(
    IReadOnlyList<ReleaseReconciliationRowDto> Rows, decimal ReleasedQty, decimal UnreleasedAcceptedQty,
    decimal ReleasedValue, decimal KeptInCharges, decimal LateKeptInCharges, IReadOnlyList<string> Flags);

public record ScrapCostDto(
    decimal RejectedQty, decimal? RejectionRatePercent, decimal? MaterialScrapValue,
    decimal RecoveryValue, string RecoveryStatus, decimal? SpoilageOriginal, decimal? SpoilageAdjusted, decimal? MaterialWastage);

public record VarianceLineDto(string Component, decimal? Original, decimal? Late, decimal? Adjusted);
public record CostVarianceDto(bool Available, string? Reason, IReadOnlyList<VarianceLineDto> Lines, decimal? RoundingResidual);

public record LabourSnapshotRowDto(        // Rate/Cost null unless rates.view + payroll.manage
    string EmployeeNumber, string EmployeeName, int Year, int Month, decimal Hours, string Basis,
    string? Reason, decimal? Rate, decimal? Cost);

public record LiveDriftDto(                // B2.4 only; null unless live=true
    bool Comparable, string? Reason, decimal? LiveTotal, IReadOnlyList<CostElementReconciliationDto> Unexplained,
    IReadOnlyList<CostGapDto> IdentityChanges);

// ---- R-B1  GET /api/manufacturing/orders/{id}/cost-reconciliation?asOf=&live=false ----
public record OrderCostReconciliationDto(
    Guid OrderId, string OrderNumber, Guid LineId, string LineCode, string ProductSku, string BaseUnitCode,
    string State,                             // "frozen" | "not_frozen" | "not_frozen_at_date"
    CostSheetHeaderDto? Sheet, DateOnly AsOf, decimal AcceptedBaseQty, decimal RejectedBaseQty,
    CostFiguresDto? Figures, IReadOnlyList<CostElementReconciliationDto> Elements, decimal RoundingResidual,
    IReadOnlyList<LateCostRowDto> LateCosts, ReleaseReconciliationDto? Releases, ScrapCostDto? Scrap,
    CostVarianceDto? Variances, bool PerEmployeeMoneyShown, IReadOnlyList<LabourSnapshotRowDto> Labour,
    IReadOnlyList<CostCheckDto> Checks, IReadOnlyList<CostGapDto> Gaps, IReadOnlyList<string> Limitations,
    LiveDriftDto? Live);

// ---- registers ----
public record PageDto(int Page, int PageSize, int Total);

// R-B2  GET /api/manufacturing/reports/cost-history?lineId&from&to&basis&asOf&kind&sourceState&provenance&flagged&page&pageSize
public record CostHistoryRowDto(
    Guid OrderId, string OrderNumber, string LineCode, string ProductSku, CostSheetHeaderDto Sheet,
    decimal AcceptedBaseQty, CostFiguresDto Figures, int LateCount, IReadOnlyList<string> GapCodes, string Integrity); // pass|fail|partial
public record CostHistoryReportDto(IReadOnlyList<CostHistoryRowDto> Rows, PageDto Page,
    decimal Original, decimal LateAdjustments, decimal AdjustedProvisional);

// R-B3  GET /api/manufacturing/reports/late-costs?lineId&from&to&basis=recorded&element&flag&page&pageSize
public record LateCostReportDto(IReadOnlyList<LateCostRowDto> Rows, PageDto Page,
    IReadOnlyList<CostElementReconciliationDto> ByElement, decimal Total);

// R-B4  GET /api/manufacturing/reports/cost-variances?lineId&from&to&asOf&page&pageSize
public record CostVarianceRowDto(Guid OrderId, string OrderNumber, string LineCode, CostVarianceDto Variance);
public record CostVarianceReportDto(IReadOnlyList<CostVarianceRowDto> Rows, PageDto Page, IReadOnlyList<VarianceLineDto> Totals);

// R-B5  GET /api/manufacturing/reports/release-reconciliation?lineId&from&to&asOf&flagged&page&pageSize
public record ReleaseReconciliationRowsDto(Guid OrderId, string OrderNumber, string LineCode, decimal Original,
    ReleaseReconciliationDto Reconciliation);
public record ReleaseReconciliationReportDto(IReadOnlyList<ReleaseReconciliationRowsDto> Rows, PageDto Page,
    decimal ReleasedValue, decimal KeptInCharges, decimal UnreleasedAcceptedQty);

// R-B6  GET /api/manufacturing/reports/scrap-costs?lineId&from&to&asOf&page&pageSize
public record ScrapCostRowDto(Guid OrderId, string OrderNumber, string LineCode, decimal AcceptedBaseQty, ScrapCostDto Scrap);
public record ScrapCostReportDto(IReadOnlyList<ScrapCostRowDto> Rows, PageDto Page,
    decimal RejectedQty, decimal? MaterialScrapValue, decimal? SpoilageOriginal, decimal? SpoilageAdjusted);

// R-B7  GET /api/manufacturing/reports/cost-gaps?lineId&from&to&code&page&pageSize
public record CostGapRowDto(Guid OrderId, string OrderNumber, string LineCode, Guid? SheetId, IReadOnlyList<CostGapDto> Gaps);
public record CostGapReportDto(IReadOnlyList<CostGapRowDto> Rows, PageDto Page, IReadOnlyDictionary<string, int> CountByCode);

// R-B9 (B2.4)  GET /api/manufacturing/reports/late-cost-candidates?lineId (required)&from&to   ≤ 100 orders
public record LateCostCandidateRowDto(Guid OrderId, string OrderNumber, decimal AdjustedProvisional, LiveDriftDto Drift);
public record LateCostCandidateReportDto(IReadOnlyList<LateCostCandidateRowDto> Rows, bool Truncated);
```

---

## 6. Authorization

| Rule | Enforcement |
|---|---|
| Every B2 route needs `manufacturing.costs.view` and `Manufacturing:Costing` | `RoutePermissions` (`AnyOfKeys`) **and** the service's refusal, as in B1 |
| Line and branch scope | `ManufacturingAccess.Reachable`/`ReachesAsync`: company scope reaches every line; others reach lines they manage, are members of, or that stand on their branch (`ProductionLine.LocationId`). An order out of reach answers 404 like a missing one; registers drop such rows from rows **and totals** |
| Per-employee money (rate, cost, and EmployerCost/PaidHours, which B2 never returns) | `manufacturing.costs.rates.view` **and** `payroll.manage`, B1's rule. Without both, names and hours only |
| Labour-kind late costs | Amount shown at order level (like the labour element). `SourceReference` and `Reason` are **redacted** for callers without rate access, because they often name an employee-month (OD-2) |
| Source capture JSON | **Never returned** by any route. Integrity results only (I1/I2) |
| Variances | Order-level labour efficiency and rate variance follow Phase A (visible with `costs.view`). Per-employee variance only with rate access |
| No new permission key | B2 reads; `snapshot.create` is not needed or granted. No change to `AdminBaseline`. The Angular nav uses the existing `mfgCosts` |
| Route table | Seven entries in `RoutePermissions`, `RoutePermissionPlan`, `LegacyAccessSnapshot`; `AuthorizationEquivalenceTests` and `EndpointAuthorizationTests` updated |

**A disclosure that already exists (OD-5).** Phase A and B1 show an order's labour total and hours to
`costs.view` holders. For an order worked by **one** employee in a month, total ÷ hours is that employee's
blended rate. B2 adds no new channel: its labour figures are the same aggregates. Full suppression would
also mean hiding the order total. The recommendation: keep `costs.view` for people allowed to see blended
labour cost (Admin by default), and say so on the access page. The alternative is suppressing labour figures
for single-employee orders, which also hides the order total from those users.

---

## 7. Implementation plan

Each step is shipped and reviewed on its own, in a worktree on a `feat/manufacturing-costing-b2` branch from
`origin/main`. Nothing is pushed or merged without the owner's go-ahead.

| Step | Content | Est. |
|---|---|---|
| **B2.0** Foundations | `ProductionCostEngine.Version`; `CostingCapture` (parse, version gate, golden-fixture tests); `CostReconciliation` (equations 1–17, classifier, duplicate flags, allocation helper); pure unit tests | 1 day |
| **B2.1** Order reconciliation | `ProductionCostHistory.OrderAsync`; R-B1 route; checks I1–I9; redaction; read-only proof test; auth tests | 1 day |
| **B2.2** Registers | R-B2 (with the R-B8 provenance filter), R-B3, R-B7 (including completed orders with no sheet); paging and totals; date bases | 1 day |
| **B2.3** Replay reports | R-B4, R-B5, R-B6 | 1 day |
| **B2.4** *(optional, OD-3)* Live drift | R-B9 and `live=true` on R-B1; identity diff; version gate | 0.5–1 day |
| **B2.5** Angular | S1 panel, S2 page, API service, status tones, i18n ar/fr/en (local model drafts, Claude reviews), nav entry, specs; screenshot harness `tests/e2e/capture-b2-screenshots.cjs` (mocked API, no browser locale) | 1.5 days |
| **B2.6** Verification and docs | Full .NET suite; `ng test`; production build; SQL Server provider tests on a disposable container; Codex and Copilot read-only review; as-built doc `costing-b2.md`; status docs; review artifact for the owner | 1 day |

**No migration in any step**, so no provider smoke for schema is needed. The SQL Server read-path tests
(§8, group G) are still required.

**Rollout:**
- B2 ships behind the existing `Manufacturing:Costing` switch, off in production.
- B2 can only be useful in production after B1's migrations are deployed. That deploy is a separate owner
  decision, and needs a confirmed production backup first; it is still unconfirmed.

**Rollback:** revert the code. Nothing was written.

---

## 8. Acceptance-test matrix

Legend: **S** SQLite (default suite) · **Q** SQL Server (`[SqlServerFact]`, `ALGERIAPOS_TEST_SQLSERVER`,
disposable container) · **U** pure unit (provider-free) · **N** Angular (`ng test`) · **E** screenshots.

| # | Case | Expected | U | S | Q | N |
|---|---|---|---|---|---|---|
| **A. Equations and rounding** |
| A1 | Element identity on a sheet with all seven elements | \|ρ\| ≤ 0.0035; residual line shown | ✓ | ✓ | ✓ | |
| A2 | Adjusted = original + Σ late, with a positive and a negative late cost | Exact to 0.001; original unchanged | ✓ | ✓ | ✓ | |
| A3 | As-of boundary: late cost at 23:59:59 and at 00:00:00 of the next company day (Africa/Algiers) | First counted, second not | ✓ | ✓ | ✓ | |
| A4 | As-of before the sheet's freeze day | `not_frozen_at_date`; no figures | | ✓ | | |
| A5 | Qa = 0 (everything rejected) | Unit costs null with words; variance not available | ✓ | ✓ | | ✓ |
| A6 | Midpoint rounding: 0.0005 → 0.001, −0.0005 → −0.001 | AwayFromZero on both providers | ✓ | ✓ | ✓ | |
| A7 | Variances sum to O − Money(est. unit × Qa), including a rounding line | Pinned | ✓ | | | |
| A8 | Recovery late cost of −50 | R' = R + 50; adjusted total −50; signed column correct | ✓ | ✓ | | |
| A9 | Unclassified kind `"freight-fix"` | Own row; counted in A(T); no element changes | ✓ | ✓ | | |
| A10 | Spoilage P₀ and P(T) by formula 16; recovery `not_recorded` when Qr > 0 and R = 0 | Exact; never shown as 0 | ✓ | ✓ | | ✓ |
| **B. Releases (partial production)** |
| B1 | Three releases; the last carries the remainder | Σ values = O exactly; carrier flagged; order by AtUtc, then Number | ✓ | ✓ | ✓ | |
| B2 | Two releases with the same AtUtc | Ordered by Number, ordinal; same on S and Q (no Guid ordering, G6) | | ✓ | ✓ | |
| B3 | Completed with no release | Kept in charges = O | ✓ | ✓ | | |
| B4 | Accepted 100, released 80 | `unreleased_output` 20; carrier warning | ✓ | ✓ | | ✓ |
| B5 | Negative final release | `final_release_negative` | ✓ | ✓ | | |
| B6 | Stock reference gap = value − qty × UnitCostSnapshot | Exact, informational | ✓ | ✓ | | |
| B7 | Late costs under C-10 default | Release values unchanged; `LateKeptInCharges` = L | ✓ | ✓ | | |
| **C. Duplicates and overlap** |
| C1 | Same source under `expense` and `expenses` | `possible_duplicate` on both; both counted once each | ✓ | ✓ | ✓ | |
| C2 | `PV-701` and `pv-701 ` on one sheet | S: both stored, flagged. Q: the second is refused by the unique index (G5); a test pins the difference | | ✓ | ✓ | |
| C3 | One source on two orders | `source_on_several_orders` | ✓ | ✓ | | |
| C4 | Late source = a voucher number already in the frozen expenses | `overlaps_original` | | ✓ | | |
| C5 | Fan-out: 3 releases × 2 materials × 2 late costs | Totals exact (no multiplication) | | ✓ | ✓ | |
| **D. Integrity and history** |
| D1 | Golden B1.0 capture replays to the stored rows | I2 pass; fixture pinned | ✓ | ✓ | ✓ | |
| D2 | JSON altered by raw SQL | I1 fail; nothing repaired | | ✓ | ✓ | |
| D3 | An element row altered by raw SQL (bypassing EF) | I2 or I3 fail | | ✓ | ✓ | |
| D4 | `IncompleteLegacy` sheet | I1 and I2 not applicable with a reason; I3–I9 run; no variance | | ✓ | ✓ | ✓ |
| D5 | Reconstructed sheet, created 9 days after completion | Kind pill *Reconstructed*; lag 9; never labelled frozen at completion | | ✓ | | ✓ |
| D6 | Unknown `CalculationVersion` | Replay and live drift "not comparable" | ✓ | ✓ | | |
| D7 | Completed order without a sheet | Appears in R-B7 as `not_frozen`; R-B1 state `not_frozen` | | ✓ | ✓ | ✓ |
| **E. Authorization** |
| E1 | No `costs.view` | 403 on all 7 routes | | ✓ | ✓ | |
| E2 | Switch off | 409 `costing_not_enabled` on all 7 | | ✓ | | |
| E3 | Another line's order; a random id | Byte-identical 404 bodies | | ✓ | ✓ | |
| E4 | Branch-held manager: registers | Only lines they reach; totals exclude the others | | ✓ | ✓ | |
| E5 | `lineId` filter on a line out of reach | Empty result, totals 0, no 404 | | ✓ | | |
| E6 | `costs.view` only | Rate and cost null; `perEmployeeMoneyShown` false; labour late source and reason redacted | | ✓ | ✓ | ✓ |
| E7 | `rates.view` without `payroll.manage`, and the reverse | Still redacted | | ✓ | | |
| E8 | Both | Shown | | ✓ | | ✓ |
| E9 | No response from any route contains `SourceSnapshotJson`, `EmployerCost` or `PaidHours` | Body scan over every route and every role fixture | | ✓ | | |
| E10 | Route table, plan and legacy snapshot | Seven entries; equivalence tests green | | ✓ | | |
| E11 | Nav entry hidden without `costs.view` | | | | | ✓ |
| **F. Read-only guarantee** |
| F1 | Every route, every role | Row counts and a checksum of every table identical before and after; `ChangeTracker` empty | | ✓ | ✓ | |
| F2 | `Product.CostPrice`, payroll lines, ledger entries, stock and manufacturing settings | Unchanged | | ✓ | ✓ | |
| F3 | Route scan | No POST, PUT or DELETE under the B2 paths | | ✓ | | |
| **G. Provider specifics** |
| G1 | Decimal round-trip of 28,10 and 18,3 values (e.g. 1234567.123456789, 0.0005) | Identical figures on S and Q | | ✓ | ✓ | |
| G2 | Sums of 500 sheets | Computed in memory; identical on S and Q | | ✓ | ✓ | |
| G3 | Date filters across UTC midnight and the company-zone boundary | Same rows on S and Q | | ✓ | ✓ | |
| G4 | Paging stable with equal sort keys | Deterministic tiebreak | | ✓ | ✓ | |
| G5 | Register over 500 sheets within the time budget (≤ 2 s locally) | | | ✓ | ✓ | |
| **H. Live drift (B2.4)** |
| H1 | Expense added after the freeze | `expenses` unexplained = amount; after a matching late cost → 0 | | ✓ | ✓ | |
| H2 | Payroll month approved after the freeze | Labour unexplained ≠ 0; rate kind change listed (detail for rate holders only) | | ✓ | | |
| H3 | Voucher cancelled after the freeze | Status change listed; amount drift 0 | | ✓ | | |
| **I. UI** |
| I1 | Arabic RTL and French LTR at 1440px; Arabic at 390px | Mirrored, no sideways scroll | | | | E |
| I2 | "Financially finalized" shows words, never a number | | | | | ✓ E |
| I3 | Every state pill has its word (`toneOf`) | | | | | ✓ |
| I4 | Dates through `I18nService`; money through the regional service | | | | | ✓ |
| I5 | `?view=` deep link with Back and Forward | | | | | ✓ |

---

## 9. Dependencies

- B1 (`2172f11`, on `origin/main`); Phase A engine; A0 (integrated).
- `Manufacturing:Costing` switch (configuration only).
- **B1 in production:** its three migrations are not deployed (production is `1225f0a`). B2 reports return
  nothing useful until they are. That deploy needs the owner's go-ahead and a **confirmed production backup**,
  which is still unconfirmed.
- A disposable SQL Server container for group Q (as used for B1, port 14334).
- Accountant answers for anything beyond provisional (below). B2 does not wait for them; it labels.
- Not needed: P0.4a/b, Phase C, payroll changes.

## 10. Unresolved decisions

**Accounting (for the accountant; B2 ships with the recommended default labelled as pending):**

| # | Question | Default in B2 |
|---|---|---|
| AD-1 (C-10) | Late costs: stay in the charges, a variance account, or allocated to released units? | Kept in charges; no release allocation shown |
| AD-2 | Late amount = its effect on total cost (recovery included)? | Yes |
| AD-3 | A fixed list of late-cost kinds (the element codes), enforced at write? This changes B1's POST, so it needs separate approval | Classifier and "unclassified" row |
| AD-4 | A voucher cancelled after the freeze: negative late cost, or no change? | Reported as a status change only |
| AD-5 | Labour true-up when payroll is approved after the freeze: hours × (actual − frozen rate)? Who records it, and does it include employer social (C-06)? | Shown as drift (B2.4), never written |
| AD-6 | A `Created` sheet made long after completion: original, or treated as a reconstruction past N days? | Shown as original, with the lag |
| AD-7 (C-11) | Partial-release rule | B1.0's rule, labelled by version |
| AD-8 (C-09) | Scrap recovery value and account | "Not recorded" |
| AD-9 (C-17) | Three decimals, AwayFromZero, final release absorbs the residual; late amounts rounded at entry | As B1 |
| AD-10 (C-13) | Post variances? | Management only |
| AD-11 | The estimate has no planned expenses or overhead (plan §2.1 said "added in Phase B") | Expense variance = all expenses; noted |
| AD-12 | What "financially finalized" will mean (signature, period close, Phase D posting) | Not implemented |

**Owner (product and security):**

| # | Question | Recommendation |
|---|---|---|
| OD-1 | One sidebar entry with seven views, or separate pages? | One entry |
| OD-2 | Redact source and reason of labour late costs for callers without rate access? | Yes |
| OD-3 | Live drift (B2.4) now or later? | Later; it reads payroll on every call |
| OD-4 | CSV or A4 print of the reports? | Later |
| OD-5 | Accept the existing single-employee blended-rate disclosure to `costs.view` holders (§6)? | Accept and document |
| OD-6 | A screen to create snapshots and late costs (B1 write UI)? | Separate phase; B2 stays read-only |
| OD-7 | Record who entered a late cost (needs a column) | Separate approved migration after B2 |
