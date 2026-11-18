# Manufacturing — Costing, Phase A: read-only cost previews

Branch `feat/manufacturing-costing-a`, from `main` `6c6b70d`, on the owner's approval of 2026-10-26 ("Phase A
only"). The plan, the audit it rests on and the accountant's checklist are in [costing-plan.md](costing-plan.md);
this page records what was built. On `main` since 2026-10-28 (merge `1a0caa6`), not deployed: it goes out with
the next deploy, behind `Manufacturing:Costing` (off). Where manufacturing stands: [README.md](README.md).

## 1. What is built

| Area | What it does |
|---|---|
| Switch | `Manufacturing:Costing` in the server's configuration (`ManufacturingCostingOptions`). **Off unless set.** Off, every costing read answers `409 costing_not_enabled`; `GET /operations/status` says `costingEnabled`. It switches nothing that writes. |
| Engine | `Application/Manufacturing/ProductionCostEngine.cs`, pure: estimated, to-date and (for a completed order) final cost by element — materials, consumables, accessories, labour, overhead, expenses, less scrap recovery — the cost per accepted unit, material still at the line, each release's value with the last release of a completed order carrying the remainder, the variances (usage, price, labour efficiency and rate, overhead, expenses, recovery; they sum to final − estimated unit cost × accepted) and the spoilage borne by accepted units. Notes say, in a code the screens translate, every reason a figure is not final. |
| Labour rates | `Infrastructure/Manufacturing/LabourRates.cs`, read-only. Actual once every run holding the employee for the month is approved or paid: the payslips' gross, employer social contribution and share of the run's levies over the hours they paid (days present × hours per day + overtime hours), across branches. Standard before that: a full month from `PayrollEngine.ComputeOne` with today's settings, over working days × hours per day. No rate is stored or typed. |
| Cost sheet | `GET /api/manufacturing/orders/{id}/costing`: the engine's sheet for one order, computed from what Phase 2 recorded. Drawn on the order page under the recorded inputs (`app-mfg-order-costing`). |
| Work in progress | `GET /api/manufacturing/reports/work-in-progress?lineId=`: every live order on the reachable lines, its cost to date, released value, material at the line and work in progress. Page Manufacturing › Work in progress. |
| Labour cost | `GET /api/manufacturing/reports/labour-absorption?year=&month=&lineId=`: per employee booked in the payroll month, hours on orders and on other rows, paid hours, and — with `payroll.manage` — employer cost, rate, absorbed and unabsorbed cost. A month not approved has no employer cost yet (null, never 0). Page Manufacturing › Labour cost. |
| Material flow | `GET /api/manufacturing/reports/material-flow`: manufacturing stock movements without a document, documents without or with a different movement, order-material counters against their documents, line stocks against the sum of their movements. Page Manufacturing › Material flow check. |
| Awaiting posting | Releases are no longer valued at the finished product's catalogue cost (costing plan F1): at the costing preview's value for a caller shown costs, otherwise "not valued" (`releasesValued`, nullable values). Material lines keep their reference cost. |
| Permission | `manufacturing.costs.view`: Admin by role, grantable on Manufacturing › Access (a "Costs" group). An employee's rate or pay also needs `payroll.manage`; without it the per-employee columns are left out and the order's totals remain. Line isolation as everywhere: out of reach is 404. Material flow needs `manufacturing.reports.view`. |

Nothing writes: no table, no column, no migration. `Product.CostPrice`, payroll, attendance and the ledger are
only read; a test compares them before and after every costing route.

### 1.1 Estimated, provisional and approved (owner's review, 2026-10-26)

Every figure says which of three it is, on the sheet and in the API (`costStatus`, `statuses`):

| Label | When |
|---|---|
| **Estimated** | The estimate column: the BOM and routing at the planned quantity, reference costs, standard rate. Never an actual |
| **Provisional** | An actual figure with at least one element not approved (below). A completed order reads "completed order's provisional cost (some inputs not approved)" |
| **Approved actual** | Every element approved, recorded or empty **and** the costing method signed by the accountant (`CostingPolicy.Approved`). Still recomputed on each read until Phase B freezes it ("recomputed, not frozen") |

Each element's own state (`ProductionCostEngine.Element*`, tones in `ui/status-tones.ts` family `costElement`):

| Element state | Means | Shown as |
|---|---|---|
| `reference_cost` | Materials at the last purchase price frozen at issue (until D9/D43) | The figure, state "Reference cost" |
| `incomplete` | A material issued at a cost of 0, or hours with no rate | The figure, state "Incomplete (part unvalued)" |
| `provisional` | Labour at the standard rate (payroll not approved), or from payslips that carry no employer social contribution (C-06 unsigned); expenses with a voucher missing, pending or cancelled (note `expense_unsettled` with the amount) | The figure, state "Provisional" |
| `not_set` | Overhead with no approved rate | "Not set", never 0 |
| `not_recorded` | Rejects whose recovery value is not recorded | "Not recorded", never 0 |
| `approved` / `recorded` | Approved payroll with its employer share, an approved overhead rate / every expense on an approved or paid voucher | The figure |
| `none` | Nothing of that kind on the order | "—" |

The estimate's labour with no standard rate reads "Unavailable".

**No order can show an approved cost in Phase A**, by three independent gates: the method is the unsigned
default (`CostingPolicy.Default`, `Approved` false, note `costing_policy_not_approved` on every sheet);
materials stay at a reference cost until the valuation switch (C-01); overhead has no approved rate (C-08).
Freezing a cost and keeping it immutable is Phase B, not built.

**Employer social contributions (D11, C-06).** A labour line whose payslips carry no employer social
contribution (`withoutEmployerSocial`, new runs since 2026-10-21) is marked "without employer social
contribution (missing)" on the sheet, on the labour element and on the labour-cost report (per row, plus a
hint, `employerSocialMissing`). The labour stays provisional. No burden is added: none is approved.

## 2. Choices Phase A settled (costing plan, *As built in Phase A*)

- **Levies per employee:** the run's total shared by each line's social base — the engine's own figure
  whenever the base is not clamped (the Algeria preset never clamps). No payroll code was changed.
- **Paid hours:** from the payslip (days present × the run's hours per day + overtime hours), not from a
  second reading of attendance, so the rate's denominator is exactly what payroll paid. A Ramadan month's
  shorter days count as full days, slightly lowering that month's rate.
- **Settings:** the defaults in code (`CostingPolicy.Default`: no overhead, blended rate, the §2.9 release
  rule, expenses as recorded, no employer burden). No settings screen until the accountant answers.
- **Releases:** recomputed on every read with the rates known then; Phase B freezes them.
- **Estimate:** the frozen material estimate plus standard hours (stage minutes × expected workers) at the
  line crew's average standard rate today.

## 3. Independent review

Codex and Copilot reviewed the change read-only. Changed after it:

- A completed order's figure is labelled "completed order's cost (recomputed, not frozen)", not "final".
- Release values are rounded so they add up: each partial release to three decimals, the last release of a
  completed order the rounded total less the others (it absorbs the rounding).
- The work-in-progress report reads each line's crew rate and each employee-month's rate once per request.
- The labour report's month defaults on the server (the company's month today in its time zone); the page
  starts from the server's answer, never from the browser's clock.
- A month payroll has not approved shows no employer cost (null), never 0.

Kept as they are, on purpose:

- Material flow is not behind `Manufacturing:Costing`: it compares quantities, values nothing, and needs
  `manufacturing.reports.view` like the other Phase 2 reports. Two lines never share a location (unique
  index), so it cannot show another line's movements.
- A line filter naming a line out of reach answers an empty report, exactly as for a line that does not
  exist, like the Phase 2 reports: it says nothing about the line.
- Levies are shared by social base (section 2), exact without a clamp; recomputing them is C-06.

## 4. Status (2026-10-26)

| Check | Result |
|---|---|
| Engine tests (`ProductionCostEngineTests`) | 8 passing; the costing plan's worked example to the decimal |
| Service tests (`ProductionCostingTests`) | 11 passing: rates from payroll, permissions, money hidden without payroll, 404 across lines, switch off, nothing written, reports, the server's default month, releases adding up |
| Security and manufacturing tests | 306 passing |
| Full .NET suite | 1,596 passed, 0 failed, 5 skipped |
| `ng test` | 1,127 passing |
| Production build | Clean; initial bundle 787.91 kB, unchanged (the pages live in the manufacturing chunk) |
| End-to-end drive `manufacturing-phase2.cjs` with costing on | 122 assertions on SQLite; costing checks gated on `costingEnabled` |
| Screenshots | `artifacts/manufacturing/phase2`: the order pages with their cost sheet, work in progress, labour cost, material flow, in French and Arabic at 1440 px and Arabic at 390 px |

No schema change, so no SQL Server migration check is needed. The drive was not run on SQL Server this time:
the SQL Server container lives in Docker, which the owner shut down the same day. The costing reads use only
constructs both engines translate (money summed in memory, as SQLite requires); running the drive on
`deploy/sqlserver` is the first check to do once Docker is back.

## 5. Known limitations

- Material is valued at the reference cost frozen at issue (the product's last purchase price) until the
  valuation switch (D9/D43, P0.4); every sheet says so.
- New payroll runs compute no employer social contribution, so labour carries none (D11); the sheet marks it
  missing and keeps labour provisional (section 1.1).
- Overhead reads "Not set" until the accountant approves a rate (C-08); it adds nothing to any total.
- Scrap recovery is not recorded yet (Phase B); an order with rejects reads "Not recorded" for it.
- Hours booked on a day payroll treats as absent still count (costing plan F6, step A0, not built).
- A payment voucher can still be linked to two orders' expenses (F9, step A0, not built).
- Nothing is frozen at completion and nothing posts: Phases B to D.
