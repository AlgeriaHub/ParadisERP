# Manufacturing costing B2: cost reconciliation and reporting (as built)

**Status, 2026-10-29.**
- Built on branch `feat/manufacturing-costing-b2` from `d463c54`, which is B1 (`2172f11`) plus evidence commits.
- Live since 2026-11-02 (on `main` since 2026-10-29, `3ee4450`), behind
  `Manufacturing:Costing` (off).
- The plan is [costing-b2-plan.md](costing-b2-plan.md). The owner approved it with the decisions and corrections in §1.

B2 is **read only**. It adds seven `GET` routes and nothing else:
- no table, column or migration;
- no new permission and no write route;
- no ledger entry, inventory valuation or `Product.CostPrice` write;
- no change to payroll or to stock settings;
- no financial finalization.

It sits behind the existing `Manufacturing:Costing` switch, which is off by default. The switch is not set in
production.

## 1. Owner decisions and technical corrections, as applied

| Owner instruction | As built |
|---|---|
| One costing page, with tabs Overview, Historical Cost Sheets, Late Costs, Reconciliation, Partial Releases, Integrity and Missing Costs | Manufacturing › **Costing** (`manufacturing/costing`) with those six tabs. Variances and rejects/scrap are views of the Reconciliation tab, so the seven routes cover them (§3). |
| B2.4 live comparison now, diagnostic only | `live=true` on the order route and on the history register (the page shown, at most 50 rows). Nothing is written. |
| Hide payroll-derived source and reasons without rate permissions | §5 |
| No migration for the late-cost creator | None. Shown as a limitation. |
| Completed orders without a snapshot, distinct from zero-cost orders | State `not_frozen`, with null figures and "No snapshot", never `0`. A sheet frozen at zero carries the gap `zero_cost`. |
| Exports under the same authorization and redaction | `format=xlsx` on the **same** seven routes. The workbook is written from the response DTO itself, already scoped and redacted. |
| Adjusted provisional apart from the original; finalized "Not available", never zero | `financiallyFinalized: null` and `finalizationState: "not_implemented"`. Screens and workbooks say "Not available" in words. |
| Reuse B1's allocation rules; no contradicting equation | Only B1's rules are used (§4). Removed from the plan: the adjusted unit cost, the pro-rata allocation of late costs to releases, and late effects on spoilage. All three would allocate or capitalize without an approved policy. |
| Late costs: recorded amount, recognized effect, unclassified or uncertain | Each late cost carries all four. Only the recognized effect enters the adjusted total (§4.2). |
| Record time apart from accounting time | Every time is labelled a record time. No effective date exists and none is invented. `asOf` filters on record time. |
| Integrity: hashes, completeness, components, releases, duplicate sources; PASS / FAIL / NOT APPLICABLE; never repair | Eleven checks (§4.5). A test proves tampered rows are left exactly as found. |

## 2. Code

| Layer | File | Role |
|---|---|---|
| Application | `Manufacturing/CostReconciliation.cs` | Pure rules: rounding, element identity, late-cost treatment, releases, scrap, variances, drift, checks, gaps. No database and no clock. |
| Application | `Manufacturing/CostingCapture.cs` | Reads B1's `SourceSnapshotJson` back into the engine's `CostingInput`, for `CalculationVersion == ProductionCostEngine.Version` only. |
| Application | `Manufacturing/ProductionCostEngine.cs` | Adds `Version = "B1.0"`. B1's writer now uses the constant; its value is unchanged. |
| Application | `Contracts/ManufacturingCostReportDtos.cs` | B2's contracts |
| Infrastructure | `Manufacturing/ProductionCostHistory.cs` | Read service. Every query is untracked. Children load in chunks of 500 per table, and money is added in memory. |
| Infrastructure | `Manufacturing/CostReportWorkbook.cs`, `CostReportLabels.cs` | Workbooks through the existing `XlsxWriter`. Arabic, French and English labels; Arabic sheets run right to left. |
| API | `Endpoints/ManufacturingOperationEndpoints.cs` | Seven routes and the `Report` helper (JSON or xlsx) |
| API | `Authorization/RoutePermissions.cs` | Seven entries, all on `manufacturing.costs.view` |
| WebUI | `manufacturing/cost-reconciliation.component.ts` | The costing page and its tabs |
| WebUI | `manufacturing/order-frozen-cost.component.ts` | The "Frozen cost" panel on the order's page |
| WebUI | `manufacturing/cost-words.ts` | Wording for gap and change codes |
| WebUI | `ui/status-tones.ts` | Families `costHistory`, `costSnapshotKind`, `costSource`, `costCheck`, `lateTreatment`, `costGap` |
| WebUI | `core/i18n/translations-manufacturing-costing-b2.ts` | Arabic, French and English copy |

## 3. Routes

All seven are `GET`. Each needs `manufacturing.costs.view` and the `Manufacturing:Costing` switch. Each answers in
JSON, or in xlsx with `format=xlsx&lang=ar|fr|en`.

| Route | Tab or view |
|---|---|
| `/api/manufacturing/orders/{id}/cost-reconciliation?asOf=&live=` | Order page, "Frozen cost" panel |
| `/api/manufacturing/reports/cost-history` | Overview totals, Historical Cost Sheets, Reconciliation › comparison (`live=true`) |
| `/api/manufacturing/reports/late-costs` | Late Costs, Overview by element |
| `/api/manufacturing/reports/cost-variances` | Reconciliation › variances |
| `/api/manufacturing/reports/release-reconciliation` | Partial Releases |
| `/api/manufacturing/reports/scrap-costs` | Reconciliation › rejects and scrap |
| `/api/manufacturing/reports/cost-gaps` | Integrity and Missing Costs, Overview severities |

**Register filters:** `lineId`, `from`, `to` (company days), `basis`, `asOf`, `page`, `pageSize`, `kind`,
`sourceState`, `state`, `element`, `treatment`, `flag`, `code`, `severity`, `live`.
- `basis` is `completed` or `frozen`, plus `recorded` for late costs.
- `pageSize` is at most 200, or 50 with `live`.
- Without dates, the period is the company's current month. A period is at most 366 days.
- Totals cover the whole filtered set, not just the page shown.
- An export holds at most 5,000 rows, or 200 with `live`; above that it answers `409 export_too_large`.

## 4. Rules and formulas

### 4.1 Rounding

Money is rounded to 3 decimals and unit values to 6, away from zero, exactly as B1 does it. Stored values are
never rounded again. Each residual is shown on its own line.

### 4.2 Late costs

- **Element.** The kind is trimmed and lower-cased, then mapped to an element: `material(s)`, `consumable(s)`,
  `accessory/ies`, `labour/labor`, `overhead`, `expense(s)`, `recovery/scrap_recovery`. Any other kind is
  **unclassified**.
- **Uncertain.** A late cost is uncertain in either of two cases:
  - **Repeated source:** another record names the same source for the same element. "Same" ignores case and
    spacing, because SQL Server's collation ignores case and SQLite does not.
  - **Source already frozen:** the source names an expense or a voucher (by id or number) already inside the
    frozen sheet.
- **Flags only.** These are flagged but still recognized: one source shared across elements, and one source
  used on several orders.
- **Totals:**
  - `LateRecorded = Σ amounts`
  - `LateRecognized = Σ recognized`
  - `LateUnclassified` and `LateUncertain` are kept apart.
  - **`AdjustedProvisional = Original + LateRecognized`**
- **Sign.** An amount is its effect on the order's total cost.
- **Dates.** `asOf` counts records made by the end of that company day, Africa/Algiers in the Algeria preset.

### 4.3 Elements

```
Original = M + K + A + L + O + E − R + ρ           ρ shown; |ρ| ≤ 7 × 0.0005, else element_identity fails
Adjusted line(e) = sign(e) × Original(e) + LateRecognized(e);   Σ lines + ρ = AdjustedProvisional
```

### 4.4 B1's rules, reused

| Topic | Rule |
|---|---|
| Releases | Ordered by release time, then number. The last one carries the remainder, so Σ values equals Original exactly. With no release, the cost is kept in the charges (D13). Accepted quantity left unreleased, over-release and a negative last release are flagged. Recognized late effects are shown as **not allocated**. The catalogue value of each release document is shown for reference only. |
| Rejects | Absorbed in the cost (D23). Spoilage is (C + R) × Qr ÷ (Qa + Qr) − R, on the frozen C and R. It is "of which", never added on top. A recovery with no value reads "Not recorded". Material scrap value and wastage come from replaying the frozen sources. |
| Variances | The engine's own variances, replayed on the frozen inputs against the frozen estimate. Each group line adds the recognized late effect of its elements. The detail lines (usage/price, efficiency/rate) take no late effect, and they include a rounding line. |
| Live comparison | `Unexplained = Live(today) − AdjustedProvisional(asOf)`, element by element. The sources that changed since the freeze are counted: expenses added, removed or changed; voucher status changes; bookings added or removed; employee-months whose rate changed; material movement lines and releases added. The comparison is refused when the method version differs. |

### 4.5 Integrity checks

Each check answers pass, fail or not applicable, and never repairs anything.

| Check | Fails or does not apply when |
|---|---|
| `source_complete` | Fails when the sheet is legacy or its capture cannot be read. Not applicable under another method version. |
| `source_hash` | Fails when SHA-256 of the capture differs from the stored hash. |
| `source_replay` | Fails when the B1.0 engine, run on the capture, does not reproduce every stored element, material, labour and release row and the recovery. |
| `element_identity` | Fails when the residual ρ is beyond its tolerance. |
| `material_rows` | Fails when the material rows do not add up to the three material elements. |
| `expense_rows` | Fails when the expense rows do not add up to the expenses element. |
| `labour_rows` | Fails when the labour rows do not add up to the labour element. |
| `scrap_row` | Fails when the scrap row does not match the recovery element. |
| `release_sum` | Fails when the releases do not add up to Original. |
| `unit_cost` | Fails when the stored unit cost differs from Original ÷ Qa. |
| `late_sources` | Fails when a late cost repeats a source or names a frozen one. |

## 5. Privacy

- **Line and branch scope** use `ManufacturingAccess`. An order out of reach answers 404 with a body
  byte-identical to a missing id. Registers drop such rows from both rows and totals. A `lineId` filter on a line
  out of reach returns an empty result, not a 404.
- **Worker-level figures** need **both** `manufacturing.costs.rates.view` and `payroll.manage`. These are the
  per-employee rate and cost, and the split of labour variance into efficiency and rate.
- **Labour and unclassified late costs.** Without both permissions, they lose `sourceReference` and `reason` and
  are marked `redacted`. Their amount stays, at order level, like the labour element.
- **Inference risk.** On an order worked by **one** employee, the labour total divided by the hours would be that
  employee's rate. So for such an order, without rate access:
  - the labour hours are withheld;
  - the labour-hour notes are said without hours;
  - booking changes are counted without hours;
  - rate changes are only ever a count of employee-months.

  No register filters by employee.
- **Residual risks:**
  - Phase A's live costing page (`/orders/{id}/costing`) still shows hours and the labour total to
    `costs.view`. That is unchanged, because changing it is outside B2.
  - The order total minus its other elements still gives the labour total.

  So `costs.view` should stay with people allowed to see blended labour cost.
- **The source capture**, `EmployerCost` and `PaidHours` never appear in any answer or workbook. A test scans
  every route.

## 6. Known limitations

These are shown on screen as notes.
- Nothing is financially final.
- Times are record times, not accounting dates.
- A late cost does not record who entered it, and its kind is free text.
- A sheet does not capture the payroll run behind its labour.
- Release values were frozen with the sheet, not at each release.
- The history guard is in the application. A database administrator could edit rows; the checks would detect it
  but not prevent it.
- The live calculation is always today's.
- Materials are at reference cost (C-01).
- Overhead is not set (C-08).
- Recovery is not recorded (C-09).
- Labour may lack the employer social contribution (C-06).

## 7. Accountant decisions still open

| Ref | Decision |
|---|---|
| AD-1 / C-10 | Late costs: stay in the charges, go to a variance account, or be allocated to released units? (B2 allocates nothing.) |
| AD-2 | Is an amount's sign its effect on total cost, including for recovery? |
| AD-3 | A fixed list of late-cost kinds. This changes B1's write route and needs separate approval. |
| AD-4 | A voucher cancelled after the freeze. |
| AD-5 | A labour true-up when payroll is approved after the freeze. |
| AD-6 | A long freeze lag: count the sheet as original, or as a reconstruction? |
| AD-7 / C-11 | The partial-release rule |
| AD-8 / C-09 | Scrap recovery |
| AD-9 / C-17 | Rounding |
| AD-10 / C-13 | Variance posting |
| AD-11 | An estimate with no planned expenses or overhead |
| AD-12 | What "financially finalized" will mean |
| New | How to record a correction to a source already frozen, which B2 shows as "uncertain". |

## 8. Phase A privacy (outside B2, proposal)

Phase A's `/orders/{id}/costing` still lets a `costs.view` holder without rate access infer rates:
- a single worker's blended rate, from the labour total and the hours;
- the crew's standard rate, from `estimate.labour` ÷ `standardHours`, or from the labour variance split.

Across several orders in one payroll month, the hours can be solved for individual rates. The minimal and complete
remediations are proposed in docs/03-work-status.md.

**Minimal remediation applied (owner, 2026-11-02; `27406f2`, live since 2026-11-02).** For a reader without both
`payroll.manage` and `manufacturing.costs.rates.view`, Phase A's sheet withholds the hours of an order one employee
worked (sheet, labour lines and the two hour notes), the labour estimate with the estimated total and unit cost, the
labour variance and the variance total when the crew's standard rate rests on one employee, and the labour variance
split; the labour-cost report withholds the hours payroll paid. The screens say "Withheld (payroll details)" in
their place. Still open, as the proposal said: the labour-cost report's per-employee order hours across a month,
which the complete remediation (an owner policy decision) would close; and an overhead rate, once one exists,
would give a one-worker order's hours back from its overhead (today it is not set, so it is zero).

**Complete remediation chosen and built (owner, 2026-11-03; live since 2026-11-03 16:31 UTC, `602f5c1`).** Order-level labour money only with
rate access (`CostPrivacy`); for everyone else, labour and totals added up by production line (and month, for labour),
withheld for a line of one order or one worker; the cost history (this document's registers) is rate access only.
The record and what it leaves open are in docs/03-work-status.md.

## 9. Verification

See [docs/03-work-status.md](../03-work-status.md) (Manufacturing costing B2) for the commands, counts and
evidence files of this branch.
