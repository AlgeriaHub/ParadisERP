# Manufacturing — Phase 2 plan: production orders (for approval, no code yet)

Branch `feat/manufacturing-phase2`, from local main `c15b1dc` (Phase 1 merged, not pushed). Basis: the
owner's Phase 2 brief of 2026-10-26, the Manufacturing & Maintenance audit Revision 2 (§3 daily log, §4
state machines, §5 costing inputs, §7 reservations and units, amendments 5–9) and Phase 1 as merged.

**Guardrails, from the brief.** No change to inventory valuation or `Product.CostPrice`; no new ledger
posting before the accountant approves; every input estimated and actual unit cost will need is
recorded; no material consumed twice and no hour booked twice; every operation isolated by production
line; SQLite and SQL Server alike; no Maintenance; no production database change; no push or deploy
without authorization.

---

## 0. The one decision to make before code: stock moves, the ledger does not

Phase 2 issues materials, returns them, releases finished goods and declares scrap. Each of those is a
real change in what sits at the line, so the plan **moves stock** (`ProductStock` and `StockMovement`,
through `StockRows` in a write scope, like every stock writer today) **but posts nothing**.

| | A — move stock, post later (recommended) | B — move nothing until postings are approved |
|---|---|---|
| Line stock, reservations, free quantity | True: they show what is physically there | False: issued wire still shows at the line, so a second order can reserve it |
| Ledger | Inventory accounts keep the issued material's value until the accountant approves the postings; finished goods enter stock with no entry | Unchanged |
| Catch-up | Exact: every movement carries `PostingState = Pending` and the inputs a posting needs (quantities, base unit, reference cost) | Every document has to be replayed into stock and ledger at once |
| Sales of finished goods | Unchanged: the sale posting still costs them at `CostPrice` as today | Same |

Under A, the stock-value figures that read `ProductStock` (item status report) drop by the issued material
while the ledger does not, until the postings are approved. The report will say so in words, with the
pending manufacturing value shown beside it. **Recommendation: A**, with a "pending manufacturing
movements" register the accountant reviews before postings are switched on.

---

## 1. Delivery in slices

Each slice ships on its own with `dotnet test`, `ng test`, the production build, the provider smoke on a
fresh SQL Server database, a migration rolled back and re-applied, and French and Arabic screenshots.

| Slice | Content | Depends on |
|---|---|---|
| 2A | Orders: create from an active BOM (snapshot), requirements, reservations, hold/resume, cancel | Phase 1 |
| 2B | Materials: issue, return, actual consumption, scrap, per-order balance, leftover count | 2A |
| 2C | Daily production log per line per day: operation progress, rejects, submit/confirm, corrections | 2A |
| 2D | Labour: bookings linked to log rows, once per employee-hour, checked against attendance, frozen with payroll | 2C |
| 2E | Output: partial releases into line stock by the production manager; progress and planned against actual duration | 2B, 2C |
| 2F | Completion: request, approval or refusal by the line's production manager, close | 2B–2E |
| 2G | Report: manufacturing materials sold at POS | Phase 1 audit rows |

One migration pair per slice (or one for 2A–2B and one for 2C–2F, to keep the count down).

---

## 2. Data model

All new tables are server-only (no `SyncEntity`), every foreign key Restrict except a document's own
children, quantities (28,10) in the entered unit **and** the base unit with the factor as it stood
(Revision 2 §7). Existing tables are untouched apart from additive columns.

### Orders
| Table | Key columns |
|---|---|
| `ProductionOrder` | `Number` (MO-, per company), `LineId`, `ProductId`, `BomId` (the version used), `PlannedQty`, `PlannedUnitId`, `PlannedBaseQty`, `PlannedStart`, `PlannedEnd` (company day), `Status` (Open, OnHold, CompletionRequested, Completed, Closed, Cancelled), `ShortageStarted` + reason, `ProducedQty`, `ReleasedQty`, `RejectedQty` (base unit, maintained by the writers), `EstimateSnapshot` (requirements at creation, JSON), `RowVersion` (concurrency token), created/requested/approved by and at |
| `ProductionOrderMaterial` | `OrderId`, `BomLineId` (source), `ProductId`, `RequiredBaseQty`, `IssuedBaseQty`, `ReturnedBaseQty`, `ConsumedBaseQty`, `ScrappedBaseQty`, `ReservationReducedBaseQty` + reason; specs copied from the BOM line |
| `ProductionOrderStage` | `OrderId`, `Sequence`, names, `PlannedMinutes` (= stage minutes × batches), `PlannedWorkers`, `IsQualityCheckpoint`, `IsOutput`, `GoodQty`, `RejectedQty` (from confirmed log rows), first/last activity day |
| `ProductionOrderEvent` | `OrderId`, `Kind` (created, held, resumed, reservation reduced, completion requested, approved, refused, closed, cancelled), user, time, reason — append-only |

The BOM snapshot is the copy of its lines (`ProductionOrderMaterial`) and stages (`ProductionOrderStage`)
taken at creation; a later BOM version never changes an open order.

### Materials
| Table | Key columns |
|---|---|
| `MaterialMovement` | `Number` (MI-/MR-/MS-), `Kind` (Issue, Return, Scrap), `OrderId`, `LineId`, `LocationId` (the line's), `ClientRequestId` (unique — a retried request is the same document), `PostingState` (Pending), by/at |
| `MaterialMovementLine` | `MovementId`, `OrderMaterialId`, `ProductId`, entered qty, unit, factor, `BaseQty`, `ReferenceUnitCost` (the product's `CostPrice` as it stood — read, never written), `StockMovementId` (the stock row it made) |
| `MaterialConsumption` | `OrderMaterialId`, `LogRowId` (optional), `StageId` (optional), `BaseQty`, `Reason` (normal use, offcut, count difference), `ClientRequestId` (unique), by/at. Moves no stock and posts nothing (amendment 8) |

Invariant per order material, checked under lock: `consumed + returned + scrapped ≤ issued`, and at
completion `issued − returned − consumed − scrapped = 0` after the leftover count.

### Daily log and labour
| Table | Key columns |
|---|---|
| `ProductionLogSheet` | `Number` (PL-), `LineId`, `Day` (company day; unique per line and day), `Status` (Draft, Submitted, Confirmed), submitted/confirmed by and at |
| `ProductionLogRow` | `SheetId`, `OrderId` or a non-order `Category` (cleaning, breakdown, waiting for material), `StageId`, `PlannedQty`, `GoodQty`, `RejectedQty` + reason (entered and base unit), specification text, `CorrectsRowId` + reason (a correction is a new row; the original stays) |
| `LaborBooking` | `EmployeeId`, `Day`, `LineId`, `OrderId` (null for a non-order row), `StageId`, `LogRowId`, `Hours`, optional start/end, `OvertimeKind`, `PayrollBranchId`, `PayrollMonth`; **unique on (employee, day, order, stage, log row)**. No rate and no cost: costing reads them later |
| `LaborBookingChange` | before/after hours, user, time, reason — append-only |

### Output
| Table | Key columns |
|---|---|
| `OutputRelease` | `Number` (OR-), `OrderId`, entered qty, unit, factor, `BaseQty`, `ScrapBaseQty` + `RecoveryNote`, `StockMovementId`, `ApprovedBy`/at, `ClientRequestId` (unique), `PostingState` (Pending) |

### Report
No table: "Manufacturing materials sold at POS" reads `ManufacturingSaleException` (Phase 1) joined to
sales, branches and products.

---

## 3. Transaction boundaries

Every write runs in one `BeginWriteScopeAsync` transaction that takes the version lock **before it reads
the figures it will change** (as `SyncService.PersistSaleAsync` does), so two supervisors, two tills or a
transfer cannot both pass the same check.

| Command | Inside one transaction | Refused when |
|---|---|---|
| Create order | Read the BOM version (active), compute requirements, read line stock and every open reservation at the line under the stock-row lock, write the order, its materials, stages and the "created" event | Not enough free quantity (unless `start_short` with a reason); BOM not active; product not on the line |
| Reduce a reservation | Order material row + event | Below what is already issued |
| Issue | Lock order (row version) and the line's stock rows; write movement, lines, `StockMovement`s; raise `IssuedBaseQty` | Order not open; more than the line's free stock plus this order's own reservation; same `ClientRequestId` → returns the first document, moves nothing |
| Return, scrap | Same locks; lower the balance; stock back to the line (return) or out (scrap) | More than issued − returned − consumed − scrapped |
| Consumption | Lock the order material; write the row | Over the issued balance; same `ClientRequestId` → no second row |
| Log submit / confirm | Sheet and rows; on confirm, stage progress and order produced quantity | Not the line's supervisor/manager; good quantity over the previous stage's (warning, not refusal) |
| Labour booking | Lock the employee-day (the bookings' unique index plus a read of the day's other bookings and the attendance capacity) | Same key again; more hours than the attendance day allows (override with reason, logged); payroll month of that branch locked |
| Release output | Lock order; `StockMovement` into the line's stock; raise `ReleasedQty` | Over confirmed output awaiting release; not a production manager of the line |
| Request / approve completion | Lock order; check all sheets confirmed and every material balance at zero; event | Approver is the requester (unless `approve_own`); open balances |

Nothing runs on a timer: a passed planned date only shows "late" (amendment 9).

---

## 4. Permissions and isolation

New keys in the `manufacturing` module, the Admin's by role, grantable on Manufacturing › Access with the
Phase 1 delegation rules: `orders.create`, `orders.start_short`, `orders.manage` (hold, resume, cancel,
reduce reservations), `materials.move` (issue, return, scrap, consumption), `log.submit`, `log.confirm`,
`labour.book`, `output.release`, `completion.approve`, `completion.approve_own`, `reports.view`. Each route
goes into `RoutePermissions`, the route plan and the legacy snapshot tests.

Isolation by line: every order, sheet, movement and booking carries its line; every read and write goes
through Phase 1's `ManufacturingAccess.Reachable`, and anything out of reach answers 404. Stock is only
ever moved at the order's own line location. The exception report is branch-scoped like other reports.

---

## 5. Attendance and payroll: read only

- Hours are recorded **once**, as `LaborBooking`s; a log row's employee lines are those bookings.
- The capacity check reads the employee's day through the existing attendance precedence
  (`AttendanceSummarizer`: day rows, clock pairs, declarations, overtime entries — never their sum).
- A booking carries the payroll branch and month; once that branch's run for the month is locked, the
  bookings of that month freeze, as overtime entries already do.
- No payroll table, run or figure is written; no production user is given `payroll.manage`; no rate or
  cost is shown in Phase 2.

---

## 6. Recorded inputs for unit costing (no valuation, no posting)

Estimated: the BOM snapshot's requirements and planned stage minutes, frozen on the order.
Actual: issued − returned − scrapped per material (base unit, reference cost as it stood), consumption
rows, labour hours per stage, good, rejected and released quantities, and the dates. The costing phase
values them; nothing in Phase 2 reads or writes `CostPrice`, valuation or the ledger.

---

## 7. Risks

| Risk | Mitigation |
|---|---|
| Stock moves without postings (decision 0, A) | `PostingState = Pending` on every manufacturing movement; a pending register; the item status report says it in words; postings switched on only with the accountant |
| Two orders reserve the same material | Reservation check and order creation in one write scope under the stock-row lock |
| A retried request issues twice | `ClientRequestId` unique per document; the retry returns the first document |
| Consumption counted twice (log and declaration) | One `MaterialConsumption` table, balance invariant under lock, unique request id |
| An hour booked twice, or on two lines | Unique booking key, plus the employee-day total against attendance capacity across all lines |
| Payroll month changed under bookings | Bookings of a locked month refused, as overtime entries are |
| Transfers out of a line ignore reservations | The free-quantity check beside the existing negative-stock check in `StockTransfers`; override with a reason |
| Wages leak through production screens | No rates or costs in Phase 2; bookings show hours only |
| SQLite and SQL Server differ (decimal sums, unique indexes with NULL) | Sums in code where SQLite cannot; uniqueness that needs a filtered index enforced in code too; provider smoke per slice |
| Scope size | Slices 2A–2G each shipped and verified on their own |
| Paper log columns not yet seen (D37) | Log rows built on Revision 2's columns; adjusted when the owner's sheet arrives |

---

## 8. Acceptance tests (each automated)

1. An order snapshots its BOM: activating a new version later changes nothing on the open order.
2. Requirements on the order equal the BOM requirements for the planned quantity and unit.
3. Two orders created at the same moment for the last 100 kg of wire: one succeeds, the other is refused (or starts short with the permission and a reason).
4. A transfer out of the line cannot take reserved material without the override.
5. Issuing the same request twice moves stock once.
6. Consumption plus returns plus scrap can never exceed issues; completion needs the balance at zero.
7. A return goes back to the same line's stock; nothing ever moves at another line's location.
8. A log row's good quantity above the previous stage's is warned, not refused; a confirmed sheet cannot be edited, only corrected by a new row.
9. The same employee, day, order, stage and row booked twice: refused. Hours above the attendance day: refused without the override.
10. A booking in a payroll month whose run is locked: refused. Payroll runs and attendance tables are byte-for-byte unchanged by any Phase 2 action.
11. Partial releases: only confirmed output awaiting release can be released, by a production manager of the line, into that line's stock.
12. Completion: refused with open sheets or open balances; the requester cannot approve their own completion without `approve_own`; refusal reopens with a reason.
13. A user of line A reads and writes nothing of line B (404), across every new route.
14. No ledger entry, no `CostPrice` change and no valuation row is written by any Phase 2 action (asserted on the tables).
15. The POS exception report lists product, branch, quantity, receipt, date and stale-offline (yes / no / not known), branch-scoped, in Arabic and French.
16. The till, the sync push and every Phase 1 behaviour pass unchanged (full suite), on SQLite and SQL Server.

---

## 9. Decisions for the owner

1. **Stock moves, ledger waits** (section 0): option A recommended.
2. **Hours above the attendance day:** refuse, with an override by the production manager and a reason (recommended), or warn only.
3. **Starting short:** refuse an order without enough free material unless `start_short` is used with a reason (recommended), or always allow with a flag.
4. **Who confirms the daily sheet:** a production manager of the line (recommended), not the supervisor who submitted it.
5. **Paper sheet columns:** a photo of the current daily sheet (D37) to match the log's columns exactly.
