# Manufacturing — Phase 2: production orders (deployed 2026-10-26, stock operations off)

Branch `feat/manufacturing-phase2`, from local main `c15b1dc` (Phase 1 merged). Basis: the owner's Phase 2
approval of 2026-10-26 (decisions 1–9), [phase-2-plan.md](phase-2-plan.md), and the Manufacturing &
Maintenance audit Revision 2. Merged to `main` and deployed 2026-10-26 at `1225f0a`; `Manufacturing:StockOperations`
is not set in production. Status at the end of this page.

## 1. What is built

| Area | What it does |
|---|---|
| Switch | `Manufacturing:StockOperations` in the server's configuration (`ManufacturingOptions`). **Off unless set.** Off, every operational write answers `409 operations_not_enabled`; reads and reports work. Set to `true` only on disposable test databases until the accountant approves the accounting treatment (decision 1). |
| Orders (MO-) | Made from the line's active BOM (the line's own, else the every-line one), copied at creation: materials with net, waste and required quantities in the base unit, reference cost (`CostPrice`, read only) and specification text; stages with planned minutes × batches. Planned start/end; actual end and overdue days on completion; a "late" badge, never an automatic change. Statuses Open, OnHold, CompletionRequested, Completed, Cancelled, each change an event. |
| Reservations | Per material: required − released by a manager − (issued − returned), never below 0, only while the order is live, at the line's location. Creation refuses a shortage (`material_shortage`) unless `start_short` is held and a reason is given (decision 3). A transfer out of a line cannot take reserved material. |
| Materials | Issue (line stock → order), return (order → line stock), scrap and consumption (no stock movement), kept apart. Per material issued − returned − consumed − scrapped never below 0 (`over_balance`); an issue never above the line's stock (`insufficient_stock`) nor into another order's reservation (`reserved_by_other_orders`). |
| Daily record (PL-) | One per line per day (decision 4), rows = operations: an order and its stage, or non-order time (cleaning, breakdown, waiting for material, training, other). Planned, completed, rejected (with reason), specification and dimensions (copied from the product, editable), notes, employees and hours, consumption references. One row per order and stage. Submitted by the line; approved by a production manager of the line who did not submit it (`own_record`); a manager may send it back with a reason. Approval moves the stages' good and rejected quantities. |
| Counting once | Only the output stage's completed quantity is finished output; earlier stages are progress on the same units. A stage ahead of the previous one is said (`stage_ahead_of_previous`), not refused (amendment 5). |
| Labour | `LaborBooking`: one per employee and row; the day's total across every line and order kind (manufacturing now, maintenance later) never above the attendance the day shows (`exceeds_attendance`), unless `labour.override` is held and a reason given — kept with who and when (decision 2). Attendance read with payroll's precedence (day row, then clock pairs, plus overtime) and, since step A0, payroll's month rule: once the employee has a day row anywhere in the calendar month, a day without one shows no hours whatever the clock holds (payroll pays that month from day rows only). A month held by a non-draft payroll run takes no booking (`payroll_month_locked`). Nothing in payroll or attendance is written. |
| Output | Partial releases (OR-) into the line's stock by a production manager of the line, at most the approved output not yet released (`over_output`); the order stays open (decision 5). |
| Completion | Requested by the line when every record naming the order is approved, every material counted to zero and all output released; approved by a production manager of the line who did not request it (`own_request`); refusal reopens with a reason (decision 6). |
| Costing inputs | Per order (decision 7): the frozen estimate (required × reference cost), issued/returned/consumed/scrapped and used quantity at each movement's cost snapshot (a return at its issues' average), hours per employee and stage with the payroll branch, month and run reference (never a rate or wage), expenses (optionally pointing at a payment voucher), accepted and rejected quantities. Since step A0 the expenses linked to one voucher, across every order, never exceed its amount (`voucher_amount_exceeded`, checked under the write lock), and a caller held to a branch links only that branch's vouchers (another's is not found). |
| Reports | Materials sold at POS (decision 8): from Phase 1's exception rows, filters branch, product, period, sale reference and stale indicator (stale / after withdrawal / unknown), branch-scoped. Movements awaiting posting: issued, returned and released at their snapshots, work in progress = issued − returned. |
| Permissions | Ten keys, Admin by role, grantable on Manufacturing › Access with Phase 1's delegation rules: `orders.create`, `orders.start_short`, `orders.manage`, `materials.move`, `log.submit`, `log.approve`, `labour.override`, `output.release`, `completion.approve`, `reports.view`. 29 routes in `RoutePermissions`. Line isolation through Phase 1's `ManufacturingAccess`: out of reach is 404. |
| Screens | Production orders (list, create with availability preview and the start-short override, order page), daily records (list, editor with hours dialog), materials sold at POS, awaiting posting; Access regrouped for fifteen keys. Arabic, French, English. |

Every stock movement keeps (decision 1): the order, the line, its location (source or destination), the
quantity as entered with its unit and factor and the base quantity, the cost snapshot, the time, the user,
the request id (a retried request is the same document — idempotent) and the posting state (Pending).

## 2. Transactions

Every write opens one transaction and takes the version lock (`LockVersionsAsync`) before it reads what it
checks, so two writers never pass the same check: order creation against reservations, issues against stock
and other orders' claims, consumption against the balance, a booking against the day's other bookings.

## 3. Proposed work-in-progress accounting (not active — for the accountant)

Nothing below posts until the accountant approves it. Proposed events, at the cost snapshot each movement
already carries:

| Event | Debit | Credit |
|---|---|---|
| Material issued to an order | Work in progress (33x) | Raw materials / consumables (31x, 32x) |
| Material returned to the line | 31x / 32x | 33x (at the issues' average) |
| Scrap of issued material | Production loss / scrap expense | 33x |
| Labour, at payroll's rate once the month's run is approved | 33x | Labour absorbed (or 64x reclass) |
| Expense linked to an order | 33x | The expense account it was paid from |
| Finished goods released | Finished goods (35x) | 33x |
| Completion: what is left in 33x | Production variance | 33x |

Reconciliation before switching postings on: the "awaiting posting" report lists every pending movement and
its value; work in progress = issued − returned at snapshots; the accountant posts the opening catch-up as
one approved entry, then postings run per movement. Valuation stays as it is (no weighted average, no
`CostPrice` change).

## 4. Schema (migration `ManufacturingPhase2Orders`, SQLite and SQL Server)

13 new server-only tables: `ProductionOrders`, `ProductionOrderMaterials`, `ProductionOrderStages`,
`ProductionOrderEvents`, `ProductionOrderExpenses`, `MaterialMovements`, `MaterialMovementLines`,
`MaterialConsumptions`, `OutputReleases`, `ProductionLogs` (unique line + day), `ProductionLogRows`,
`LaborBookings` (unique employee + row), `LaborBookingChanges`. No existing column changes. Unique request ids
per document kind.

## 5. Daily paper sheet: what is not confirmed

The paper production records are not in this session (D37). The record is built on Revision 2's columns.
Not confirmed against the paper, and therefore not invented:

- whether the sheet records start and end times per person, or hours only (built: hours only);
- whether rejects are split by cause (built: one quantity, one reason);
- whether quantities are counted in pieces, kilograms or both on the same row (built: the product's base unit);
- whether machine or press numbers appear (built: free-text notes);
- whether the foreman signs per row or per sheet (built: per sheet, submit and approve);
- any column the paper has beyond those listed in section 1.

## 6. Status (2026-10-26)

| Item | State |
|---|---|
| Server: domain, migration, services, routes, permissions | Done. Manufacturing and security tests 288/288; full .NET suite 1,577 passed, 0 failed, 5 skipped |
| Screens and dictionaries | Done. `ng test` 1,122 passing; production build clean (initial bundle 787.91 kB, unchanged) |
| End-to-end drive `tests/e2e/manufacturing-phase2.cjs` | Passing, 89 assertions, on SQLite and on SQL Server (fresh databases, stock operations on) |
| SQL Server (container `deploy/sqlserver`) | Every migration applies on a fresh database; `provider-smoke.cjs` 249/249; `ManufacturingPhase2Orders` rolled back to `ManufacturingSaleAudit` with Phase 2 rows present (13 tables dropped; products, lines and stock movements kept) and re-applied |
| Screenshots (French and Arabic 1440, Arabic 390) | 26 in `artifacts/manufacturing/phase2`, from the SQL Server run |

Fixed during the checks (2026-10-26):

- Opening a daily record that a consumption points at answered 500 (`ProductionLogs.GetAsync` read the
  material's SKU through a navigation it had not loaded). The SKU is now projected in the query; a test covers it.
- A client request id is now looked up after the order is reached and under the version lock (issue, return,
  scrap, consumption, release, expense, new order). Another line's order stays 404 even with one's own request
  id, and a copy of a request sent alongside the first answers the first's document instead of an error.
- The order and record pages drew their panels without `.bo-panel__head` / `.bo-panel__body`, so their content
  touched the card's edge; the records, materials sold at POS, awaiting posting and access lists said "nothing
  yet" when loading failed instead of the error state with a retry.
- The end-to-end drive sent a till sale without a cashier id (400); it now sends the signed-in user's.

To run again: build `AlgeriaPos.API` (Release) and the web bundle; start the API on a fresh database with
`Manufacturing__StockOperations=true` and `Bootstrap__AdminPassword` set; run
`ADMIN_PASSWORD=... BASE=http://127.0.0.1:5198 node tests/e2e/manufacturing-phase2.cjs`. For SQL Server, the
same against a fresh database on `deploy/sqlserver`, then `tests/e2e/provider-smoke.cjs` on another fresh one.
The drive needs a fresh database each time: its branch names are fixed.

## 7. Known limitations

- An approved daily record cannot be corrected yet: a manager sends a submitted one back instead.
- An employee whose attendance is declared per month has no day to read, so every booking for them needs the
  audited override. Manufacturing workers are best on recorded attendance.
- Stock adjustments at a line location (a count) are not limited by reservations: a count records reality.
- Scrap of issued material moves no stock (it was already out at the issue); scrap with a recovery value is
  not built.
- Rates and costs are not computed: the costing phase reads the recorded inputs.
- Who submitted, approved or completed is shown by the account's sign-in name, as everywhere else in the app;
  an account made from an employee signs in with a generated number, so that number is what shows.

## 8. Step A0 (costing plan F6 and F9; built 2026-10-26, on `main` since 2026-10-28, not deployed)

Two behaviour changes the costing plan asked for before labour and expenses can be costed, each on its own
approval (owner, 2026-10-26). Branch `fix/manufacturing-a0`, from `main` `6c6b70d`; no migration.

| Change | Before | After |
|---|---|---|
| Hours ceiling (F6, `AttendanceCapacity`) | A day without a row fell back to clock punches, even in a month payroll reads from day rows only | Payroll's month rule: a day row anywhere in the calendar month means the month is read from rows; a day without one shows only its overtime entries (source `none`). A month with no row still reads paired punches. Declared attendance unchanged |
| Expense voucher (F9, `ProductionOrders.AddExpenseAsync`) | The voucher only had to exist | The voucher must be an Expense voucher the caller's branch reaches (the Admin reaches all; another branch's answers 404 `not_found`), must not be cancelled, and Σ expenses linked to it on every order + this one ≤ its amount (409 `voucher_amount_exceeded`). The check is inside the shared write-lock transaction, so distinct orders cannot both take the same remaining amount. |

What it does not change: bookings and expenses already recorded (nothing is rewritten or re-checked);
payroll, attendance, vouchers and stock (only read); the override, which still books beyond the ceiling with
a reason. The voucher has a single amount with no tax split, so the cap is against that amount; the
accountant's C-07 (expenses before recoverable VAT) is still open. A pending, approved or paid Expense
voucher can be linked: pending links remain provisional until the voucher is paid; paid links are actual.
Cancelling a voucher preserves its existing historical links so Phase A can mark their cost provisional,
but refuses new links. A non-Expense voucher is always refused. A paid voucher has no reversal operation
in the voucher model (it cannot be cancelled); a correcting accounting/reversal document is an unresolved
business decision and is deliberately not inferred by manufacturing.

Tests: `ProductionLogTests.A_day_without_a_row_in_a_month_payroll_reads_from_day_rows_shows_no_hours_whatever_the_clock_holds`,
`ProductionOrderTests.The_expenses_linked_to_one_voucher_never_exceed_its_amount_and_another_branch_s_voucher_is_not_found`.
Both pass on SQLite. The end-to-end drive adds concurrent labour bookings on separate lines and concurrent
voucher allocations on separate orders: exactly one request succeeds and the other answers the documented
409 code. It passed 96 assertions against local SQL Server 2022. A0 was merged into `main` on 2026-10-28
(`42d065b`) and goes out with the next deploy; stock operations stay disabled in production.
