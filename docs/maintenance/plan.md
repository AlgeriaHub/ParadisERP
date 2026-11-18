# Maintenance — implementation plan (plan only, nothing built)

*Written 2026-11-09 for the owner. It plans the maintenance half of the "Manufacturing & Maintenance" module. The
manufacturing half is built and live ([../manufacturing/README.md](../manufacturing/README.md)). No application code,
schema or setting is changed by this document. Each phase in section 8 waits for its own approval.*

**Sources.** The owner asked on 2026-10-25 for a module covering "maintenance pricing and internal vs company
profit". The first source is the Claude Docs document "Manufacturing & Maintenance Audit"
(https://claude.ai/artifact/Ny3Rhfe1J9EnyGm7xWbpDj), Revision 2, decisions D1–D50. **This session could not read
it:** the docs connector and the artifact reader both refused access. Its maintenance sections and the owner's
answers to D1–D50 are therefore **not** reflected here, except where repository documents quote them. This plan
works from:

- the code at `393a28d`;
- the accountant's questionnaire, maintenance section MNT-01 to MNT-10, plus REN-11
  ([../accounting/accountant-questions.md](../accounting/accountant-questions.md), maintenance at lines 1958–2180,
  REN-11 at line 1263);
- the decision tracker ([../accounting/accounting-decision-tracker.md](../accounting/accounting-decision-tracker.md)
  rows MNT-01 to MNT-10 and REN-11/REN-12; none has been answered);
- the code findings ([../accounting/accounting-code-findings.md](../accounting/accounting-code-findings.md) item 13);
- the SCF chart review ([../accounting/scf-chart-review.md](../accounting/scf-chart-review.md));
- the ERP redesign's rental accounting ([../06-erp-redesign.md](../06-erp-redesign.md) §5.1, and the product report
  near line 561);
- the owner's answers 18–20 ([../owner-answers.txt](../owner-answers.txt));
- the manufacturing records: [costing-plan.md](../manufacturing/costing-plan.md),
  [phase-1.md](../manufacturing/phase-1.md), [phase-2.md](../manufacturing/phase-2.md) and
  [phase-c.md](../manufacturing/phase-c.md).

Before Phase M1 starts, read the audit's maintenance sections and its D-answers. Where they differ from this plan,
they win, and the matching setting's default changes to follow them (section 9, point 1).

**The owner's standing rule (2026-11-08/09).** Every policy or accounting decision is a back-office setting that the
admin or the accountant sets in the panel. Its default is the recommended answer, and that default keeps today's
behaviour. No open question blocks the work. Section 4 lists every such setting.

Contents: 1 what exists today · 2 the module · 3 cost, price and profit · 4 every decision as a setting ·
5 accounting · 6 permissions and privacy · 7 screens · 8 migrations and phases · 9 open points.

---

## 1. What exists today that maintenance touches

There is no maintenance module. The code findings already say so (item 13): there is no repair order, job, service
product, or job labour or parts record. The pieces it will build on are listed below.

### 1.1 Rental equipment states

| What | Where | Behaviour |
|---|---|---|
| Five stock states | `ParadiERP.Domain/Entities.cs:393-431` (`StockState`, `ProductStock`) | `StockQty` (available), `OnRentQty`, `MaintenanceQty` ("being repaired; still an asset, not available to hire"), `ScrapQty`, plus `WrittenOff`, which is not a bucket. All per product and per branch. |
| Moving between states by hand | `ParadiERP.API/Endpoints/StockEndpoints.cs:229-300` (`POST /api/catalog/stock/state`) | Moves a quantity from one state to another under the `StockRows` version lock. It refuses on-rent moves ("contracts' to move") and refuses to move more than the bucket holds. It writes a `StockMovement` of kind `StateChange` with `QtyDelta = 0` and the reason `available->maintenance: <reason>`. It touches the product so tills re-pull it. |
| Its permission | `ParadiERP.API/Authorization/RoutePermissions.cs:115` | `inventory.adjust` |
| Its ledger | `ParadiERP.Infrastructure/Ledger/DocumentPosting.cs:99-116` (`StockPosting.StateChange`), `ParadiERP.Domain/LedgerEntities.cs:444-449` (`ForStockState`) | Available ↔ maintenance stays in 310 and posts **nothing**. To scrap: Dr 318 / Cr 310. To write-off: Dr 657 / Cr 310. Valued at `Product.CostPrice`. |
| Movement kinds | `Entities.cs:1113-1140` (`StockMovementKind`) | 0–17. Manufacturing uses 15–17. Values are only ever appended. |

### 1.2 Damage on return notes (the repair recharge)

| What | Where | Behaviour |
|---|---|---|
| Return line conditions | `ParadiERP.Domain/RentalEntities.cs:504-551` (`ReturnNoteLine`) | `QtyOk`, `QtyPartialDamage` ("damaged but usable: returned stock in need of repair"), `MaintenanceValue` ("what that repair is charged at; income, because the customer is paying for it"), `QtyTotalDamage`, `QtyMissing` |
| Where units go | `ParadiERP.Infrastructure/Rentals/RentalDocuments.cs:1297-1306` (`Allocate`) | sound → available, repairable → **maintenance**, wrecked → scrap, missing → written off. The return note's branch is `ReturnNote.BranchId` (`RentalEntities.cs:386`). |
| Rules | `ParadiERP.Application/Rentals/RentalRules.cs:97-98` | Refuses a negative repair amount, and refuses a repair amount with no damaged units |
| The charge | `ParadiERP.Application/Rentals/RentalSettlement.cs:312-315` | One `RentalCharge` of kind `Maintenance` per line, for the typed amount |
| Its account | `ParadiERP.Infrastructure/Ledger/RentalPosting.cs:39-47`, `LedgerEntities.cs:367` | **707** "maintenance charged to customer", taxed at the line's rate. The code comment states the rule: "the same work, decided by who pays". |
| The money | `ParadiERP.Domain/ReceivableEntities.cs:214-219` (`ReceiptKind.Repair`), owner answers 19–20 | A receipt voucher of kind Repair names the contract and the return note |

So the recharge is a single amount the clerk types at return, before anyone knows the repair cost. Nothing ever
compares it with what the repair cost (REN-11). The REN-11 example: 8 frames charged 16,000 DA → 707, repaired by a
welder for 11,000 DA → 615, a profit of 5,000 DA that no report shows.

### 1.3 Repair costs today

| What | Where | Behaviour |
|---|---|---|
| Outside repair paid by voucher | `ParadiERP.Domain/PaymentVoucherEntities.cs:76-80`, `ParadiERP.Infrastructure/Receivables/PaymentVouchers.cs:83` | Expense category `repair` → **615** for the gross amount, on the day it is paid. The voucher may name a contract (`ContractId`) but never an item. |
| Spare parts used | `DocumentPosting.cs:199-209` (`StockPosting.Adjustment`) | No document exists for this. The only route is a stock adjustment, Dr **657** stock loss / Cr 310, which records the repair as a loss (MNT-02). |
| Spare-part role | `ParadiERP.Domain/ManufacturingEntities.cs:22-35` | `ManufacturingRole.SparePart = 4`, "a part kept for repairs; still sold at the till" |
| Own labour | — | Wages go to 631 and employer social to 635 through payroll. No repair hours are recorded anywhere. |

### 1.4 Labour bookings (built for maintenance to share)

| What | Where | Behaviour |
|---|---|---|
| Kind | `ParadiERP.Domain/ManufacturingOrderEntities.cs:369-374` | `LaborOrderKind { Production = 0, Maintenance = 1 }`, documented as "Maintenance comes later and books into the same table" |
| Booking | `ManufacturingOrderEntities.cs:383-407` (`LaborBooking`) | Employee, day, `OrderKind`, `OrderId`, `StageId`, **`LineId` and `LogRowId` (both required)**, hours, payroll branch, year and month, attendance at booking time, override reason. No money. |
| Index | `ParadiERP.Infrastructure/Persistence/PosDbContext.cs:1340-1348` | Unique on (EmployeeId, LogRowId); indexed on (EmployeeId, Day) and (OrderId, StageId) |
| Ceiling | `ParadiERP.Infrastructure/Manufacturing/ProductionLogs.cs:370-424` | "Every booking that day, on every line and order kind: one hour is never allocated twice" (line 394). Payroll month lock: `payroll_month_locked`. Override: `manufacturing.labour.override`, with a reason. |
| Readers | `ProductionCosts.cs:274`, `ProductionCostSnapshots.cs:259`, `ProductionCostHistory.cs:617`, `ProductionOrders.cs:76,545`, `PracticeLedgerReport.cs:71` | Filter on `OrderKind == Production`, so maintenance bookings stay out of manufacturing figures |
| Readers without the kind filter | `ProductionCosts.cs:177` (labour absorption, by `LineId`), `ProductionLogs.cs:58,77,212,390,432` (by `LogRowId`) | They key on line or row, so a maintenance booking with neither stays out. They still need to compile once both columns become nullable (Phase M3). |

### 1.5 Cost engine, rates, privacy, ledger

| What | Where | Reuse |
|---|---|---|
| Labour rate | `ParadiERP.Infrastructure/Manufacturing/LabourRates.cs` | Gives the rate per employee-month from payroll. It is actual once the runs are approved, standard before that. Nothing stored. Reused unchanged. |
| Cost engine | `ParadiERP.Application/Manufacturing/ProductionCostEngine.cs` (`CostingPolicy` :78-84, `CostingInput` :86-95, `Compute` :204) | Pure. C = materials + consumables + accessories + labour + overhead + expenses − recovery. It also applies the employer burden (C-06) and expense VAT (C-07). Section 3.1 reuses it through an adapter. |
| Labour privacy | `ParadiERP.Infrastructure/Manufacturing/CostPrivacy.cs` | Per-order labour money, and every figure that gives it back, goes only to holders of `payroll.manage` + `manufacturing.costs.rates.view`. A line sum is shown from 2 orders and 2 workers. Section 6.2 applies it to jobs. |
| Costing choices | `ParadiERP.Domain/ManufacturingAccountingEntities.cs:15-78` | One settings row, where every default is the suggested answer. This is the model for `MaintenanceSettings`. |
| Account map | `ManufacturingAccountingEntities.cs:132-171` (`ManufacturingAccountMapping`, `ManufacturingAccountSlots`) | One row per slot key, stored as text. Seeded empty and never guessed. Maintenance appends its own keys. |
| Practice ledger | `ParadiERP.Application/Manufacturing/PracticeLedger.cs`, `ParadiERP.Infrastructure/Manufacturing/PracticeLedgerReport.cs` | Entries computed on read and stored nowhere, with the checks. Maintenance adds its own section (section 5). |
| Month posting | `ParadiERP.Infrastructure/Manufacturing/ManufacturingLedgerPostings.cs` | Posts a closed, checked month through `LedgerPoster` (source `manufacturing`) and reverses it with mirror entries. Maintenance reuses the mechanism under source `maintenance`. |
| Expense on an order | `ManufacturingOrderEntities.cs:181-192` (`ProductionOrderExpense`), A0 cap ([phase-2.md](../manufacturing/phase-2.md) §8) | A typed amount that may point at a voucher. The amounts linked to one voucher never exceed it. The model for a job's outside work. |

### 1.6 Invoicing and the ledger's accounts

- `Invoice` (`ReceivableEntities.cs:43`) has `SourceType`/`SourceId` and `TaxMode`. Each `InvoiceLine` (`:138`) carries
  its own `IncomeAccountCode`. `InvoiceOrigin` is `Direct`, `Sale` or `Rental` (`:16-24`). A customer job can be
  billed through this path without a new poster.
- VAT only on request (owner, 2026-11-05, built): every bill starts without VAT, and the seller ticks it when the
  customer asks. A customer job follows the same rule.
- Accounts in the chart (`LedgerEntities.cs:321,367,410,427`): **615** maintenance and repairs (expense), **707**
  maintenance charged to customer (income). The SCF review lists 707 under a wrong heading: in the SCF a repair is a
  706 sub-account. The owner's decision is to fix it at the cutover, together with D33, D43 and REN-12. 707 has never
  been posted to (live check of 2026-11-01).
- REN-12 (owner-approved, accountant pending): rental equipment is to become fixed assets with depreciation. Until
  then it stays in 310 with no register. This decides MNT-01 and MNT-10.

---

## 2. The module

### 2.1 Three kinds of job, decided by who pays

The ERP redesign's rule ([06-erp-redesign.md](../06-erp-redesign.md) §5.1) is that the same work is income or expense
depending on who pays. That gives three kinds of job:

| Kind | What | Who pays | Source support |
|---|---|---|---|
| **Internal** | The company's own equipment: wear found in the yard, or a unit someone moved to maintenance by hand | The company: cost only | MNT-01, MNT-02, MNT-04, MNT-05, MNT-10 |
| **Recharged** | Rental equipment returned damaged, where the return note charged the customer a repair (707) | The hire customer, through the recharge already on the return note | REN-11, return-note fields (§1.2) |
| **Customer** | An outside customer's equipment, with no hire | The customer, through an invoice | MNT-06, MNT-07, MNT-08, MNT-09 (each "planned, not built") |

The sources support all three. The questionnaire asks about customer jobs only conditionally ("*if* we repair
equipment for outside customers"), so customer jobs sit behind their own setting, off by default (MS-06).

### 2.2 The job (`MaintenanceJob`, number `MJ-000001`)

| Field | Meaning |
|---|---|
| `Number`, `BranchId` | Unique per company. The branch is where the work is done and where the units and parts are held. Branch-held users reach only their own branch's jobs; another branch's job answers 404, the same as any document. |
| `Kind` | Internal, Recharged or Customer |
| `Status` | Open → Completed → (Customer only) Invoiced; or Cancelled. A family in `ui/status-tones.ts`. |
| `ProductId`, `Qty` | The equipment type and how many units, for internal and recharged jobs. Units are tracked by type, not by serial (MNT-10, MS-38). |
| `ItemReference` | Optional free text: a serial, a plate, a mixer's number. It carries per-piece history before the REN-12 register exists. |
| `ContractId`, `ReturnNoteId`, `ReturnNoteLineId` | For a recharged job: the return-note line whose repairable units it repairs and whose recharge it earns |
| `CustomerId` | For a customer job |
| `Description`, `Diagnosis` | What is wrong and what was done |
| `PlannedHours` | Optional. Feeds the estimate. |
| `QuotedPrice`, `TaxMode` | Customer job: the agreed price before tax (MS-20), and whether VAT is ticked (owner, 2026-11-05) |
| `InvoiceId` | The invoice raised from a customer job |
| `ReworkOfJobId` | A free re-do under guarantee points at the original job (MNT-09, MS-37) |
| `RepairedQty`, `ScrappedQty` | Set at completion. Repaired units return to available. Units found beyond repair go to scrap through today's state move (Dr 318 / Cr 310). |
| Opened, completed, cancelled: by and at | The audit trail. Each change is also logged in `MaintenanceJobEvents`. |
| `RowVersion` | Concurrency token, the same as production orders |

**Units.** At opening, an internal or recharged job names units that are **already in maintenance**: from a return
note, or from a manual state move. Alternatively, the job opener may move them from available into maintenance in
the same step, with a reason; this writes exactly the `StateChange` movement the stock route writes today. At
completion, the job moves `RepairedQty` from maintenance to available and `ScrappedQty` from maintenance to scrap.
It uses `ProductStock.Move` under the `StockRows` lock and the same `StockPosting.StateChange` poster as the manual
route, so the stock rules and ledger effects are identical. The manual route stays (MS-02).

**From a return note.** Each return-note line with `QtyPartialDamage > 0` that no job covers yet appears in
Maintenance › Waiting for repair. "Open a repair job" there pre-fills the product, quantity, contract, return note and
recharge. By default, nothing is created inside the return note's own transaction (MS-03), so rentals keep their
current behaviour.

### 2.3 Parts and materials on a job

- `MaintenanceJobPart` (an issue or a return line): product, quantity, unit cost frozen at issue, stock movement id,
  client request id (idempotent retry), by and at.
- Parts come from the job's branch stock (`ProductStock.StockQty`). Each issue writes a `StockMovement` of new kind
  `MaintenanceIssue = 18`, and each return writes `MaintenanceReturn = 19`. The `UnitCost` frozen on the movement
  follows the valuation in force: `Product.CostPrice` today, the weighted average after the C-01 switch. This is the
  same rule as manufacturing issues ([costing-plan.md](../manufacturing/costing-plan.md) §5).
- An issue refuses more than the branch holds, like every other stock move. This does not conflict with rule 1 of
  CLAUDE.md, because a job is not a sale.
- Any stocked product may be issued. Spare parts, consumables and accessories (roles 2–4) are the usual ones. The
  role decides the stock group in the practice ledger (section 5).
- A cancelled job must first return what it holds, or the parts stay as used.

### 2.4 Outside work

`MaintenanceJobExpense`: description, amount, an optional payment voucher. This is the shape of
`ProductionOrderExpense`. The A0 cap becomes one rule across both tables: the expenses linked to a voucher, on
production orders and jobs together, never exceed the voucher's amount. The voucher still posts where it posts
today (category `repair` → 615). The link only puts the amount into the job's cost. It counts as "settled" once the
voucher is approved or paid, the same rule as `CostingExpense.Settled`.

### 2.5 Labour on a job

Hours go into the existing `LaborBooking` table with `OrderKind = Maintenance` and `OrderId = job id`, which is what
that table was designed for. The rules are the same as manufacturing's:

- **One ceiling.** The day's total across production and maintenance never exceeds the attendance the day shows
  (payroll's precedence and month rule, `AttendanceCapacity`). Booking more needs `maintenance.labour.override` and
  a reason that is kept.
- Refused when the payroll month is held by a non-draft run (`payroll_month_locked`).
- A job has no daily record and no stage. Maintenance bookings are made directly on the job: one per employee, job
  and day, enforced in code under the version lock. This needs `LineId` and `LogRowId` to become nullable (migration,
  Phase M3). The existing unique index (EmployeeId, LogRowId) keeps its meaning: EF Core filters NULLs out of a SQL
  Server unique index on a nullable column, and SQLite treats NULLs as distinct. Changes are logged in
  `LaborBookingChanges`, as today.
- Who may be booked: any employee of the job's branch by default, or only the workshop list (MS-04).

---

## 3. Cost, price and profit

### 3.1 The job's cost (reusing the cost engine)

**C = parts (issued − returned, at their frozen values) + labour + outside work + overhead.** This is MNT-04's
suggestion A plus an overhead that defaults to 0. Worked example from MNT-04: parts 3,200 + 6 h × 450 + painting
4,000 = **9,900 DA**. With an overhead of 15 %, it would be 11,385.

**Reuse.** `MaintenanceCosting.InputFor(job)` builds a `CostingInput` and calls `ProductionCostEngine.Compute`
unchanged:

- each issued product becomes a `CostingMaterial` (required = issued, estimate = issue average);
- each booking becomes a `CostingBooking` with `StageId = job id`;
- each expense becomes a `CostingExpense`;
- there are no releases;
- planned and accepted quantity are the job's `Qty`, or 1;
- `Completed` comes from the status;
- the policy is built from the settings (MS-11 to MS-13).

The adapter reads `actual` elements, labour lines and notes. It ignores releases, variances and work in progress,
and drops the notes that do not apply to a job (`no_labour_estimate`, `not_frozen`). The engine's `Version` stays
`B1.0`, so no frozen manufacturing sheet changes. The B1 hash tests are the guard. If a later need cannot be met
through the adapter, a shared labour-valuation helper may be extracted, but only if every manufacturing test passes
byte-for-byte.

**Labour** is valued with `LabourRates.ForAsync`: actual once payroll is approved, standard before that, so a job's
cost stays provisional until its months' payroll is approved. Alternatively, it is valued at one workshop rate per
hour set in the panel (MS-12). The employer burden and the expense VAT basis follow Manufacturing › Costing settings
(C-06, C-07) unless MS-13 says otherwise. This follows MNT-05: "the same hourly rate method as manufacturing, so
both follow one rule".

No cost sheet is frozen. A job's cost is computed on read, like costing Phase A. Freezing at completion (like B1)
is left for later, if the accountant wants it.

### 3.2 The job's price

| Kind | Revenue | Set by |
|---|---|---|
| Internal | None: cost only (the owner's "company profit", 2026-11-09). | — |
| Recharged | The return-note line's `Maintenance` charge (707), typed at return as today. If the job covers only part of the line's damaged units, the charge is shared by units (MS-26). | Rental clerk (`rentals.return`). Optionally suggested from a repair rate per product (MS-23). |
| Customer | The invoice's net before tax | `maintenance.pricing`, by the basis chosen in MS-20 |

Customer job price bases (MS-20):

1. **Quoted**: a typed price before tax (default). It is the simplest option and mirrors how recharges are typed today.
2. **Itemised**: parts at the product's sale price, plus hours × the labour charge rate (MS-21), plus outside work
   at cost plus a markup (MS-22).
3. **Cost-plus**: the job's cost plus a markup.

Under basis 2 or 3 the system proposes the price and the person with `maintenance.pricing` may change it. The price
is frozen when the invoice is raised. Money is formatted through the regional service. No currency or rate is
hard-coded (CLAUDE.md, "Country is configuration").

**Invoicing a customer job.** "Raise the invoice" on a completed customer job calls the existing invoice writer with
`Origin = MaintenanceJob` (a new enum value), `SourceType = "maintenance-job"`, `SourceId = job id`, the job's
`TaxMode`, and lines per MS-34: one service line, or parts and labour split. The invoice's own poster books
411 / income / 4457 exactly as for any other invoice. Payment arrives by an ordinary receipt voucher of kind
`Repair`. Advances on jobs (MS-35) are taken from M7b, once customer advances are held on 419: see docs/accounting/advances.md.

### 3.3 Profit: the company's (owner, 2026-11-09)

The owner answered "company profit": a job's profit is what the company earns from outside, less what the job cost.
There is no workshop or internal profit figure, and no internal charge (MS-24 is dropped).

- **Company profit of a job** = external revenue − C. External revenue is the customer invoice's net, or the
  recharge attributed to the job. An internal job has none, so it shows its cost as upkeep of own equipment. This is
  the figure REN-11 asks for: the 16,000 DA recharge − 11,000 DA repair = **5,000 DA**.
- **Per equipment type**: cumulative maintenance cost against rental revenue, the "repair it again, or replace it?"
  figure in the ERP redesign's product report, and "loss and damage, customer vs internal".

Reports (Phase M5): company profit by job, month, branch, kind and product. Recharges are compared with their jobs' cost.

---

## 4. Every decision as a setting

`MaintenanceSettings` is one row, like `ManufacturingCostingSettings`. The **first option is the default**, and every
default keeps today's behaviour: the module is off, nothing posts, and nothing in rentals or stock changes. Operational
settings are edited with `maintenance.settings`. Accounting settings (MS-30 onwards) are edited with
`accounting.manage`, in the same panel, the way Costing settings works. The policy built from these settings never
reads as approved until the accountant confirms it (MS-40).

| ID | Setting | Options (first = default) | Question | What follows it |
|---|---|---|---|---|
| MS-01 | Maintenance module | Off · On | — | The sidebar module, every route (`409 maintenance_off` when off), the Waiting-for-repair list |
| MS-02 | Units leave maintenance | By hand or by a job · Only by a job | — | Whether `POST /api/catalog/stock/state` still accepts maintenance → available. The default leaves the route exactly as it is. |
| MS-03 | Jobs from return notes | Offered in Waiting for repair · A draft job opened automatically for each damaged return line | — | Rentals. The automatic option writes one job in the return note's transaction; the default touches no rental code. |
| MS-04 | Who may be booked on a job | Any employee of the job's branch · Only the workshop list | — | The booking check (§2.5); the list is Maintenance › Workshop (built 2026-11-10, "MS-04 as built") |
| MS-05 | Who closes a job | Anyone with `maintenance.jobs.close` · Someone other than the opener | — | Completion. The second option is four-eyes, like production completion. |
| MS-06 | Jobs for outside customers | Off · On | MNT-06 "if we repair for outside customers" | The Customer kind, pricing, invoicing |
| MS-10 | What a job's cost includes | Parts, labour and outside work · The same plus overhead · Parts and outside work only (labour stays a general wage cost) | MNT-04 (suggested A) | The adapter's policy, the job cost panel, the reports |
| MS-11 | Overhead per labour hour | An amount; 0 charges none | MNT-04 B | Same |
| MS-12 | Labour rate | Payroll-derived per employee-month, as manufacturing · One workshop rate per hour, set here | MNT-05 ("the same hourly rate method as manufacturing") | Labour value; privacy (§6.2): a single workshop rate reveals no one's pay |
| MS-13 | Employer burden and expense VAT | Follow Manufacturing › Costing settings (C-06, C-07) · Own values | MNT-05, C-06, C-07 | The adapter's `CostingPolicy` |
| MS-20 | Customer job price | Quoted · Itemised (parts at sale price + hours × charge rate + outside work + markup) · Cost + markup | Owner's "maintenance pricing" | The price proposal on a customer job |
| MS-21 | Labour charge rate per hour | An amount; 0 | — | The itemised price |
| MS-22 | Markup % | On outside work (itemised) or on cost (cost-plus); 0 | — | Same |
| MS-23 | Return-note recharge | Typed at return, as today · Suggested from a repair rate per product, still editable | REN-11 | The return-note screen shows a suggestion only. The engine, rules and postings are unchanged. |
| ~~MS-24~~ | ~~Internal charge on internal jobs~~ | Dropped: the owner chose company profit (2026-11-09) | — | — |
| MS-25 | A customer price below cost | Shown as a loss (a warning) · Needs a `maintenance.pricing` holder's confirmation | — | The pricing step. It never refuses. |
| MS-26 | Recharge shared over several jobs | By units · All to the first job | REN-11 | Job profit of recharged jobs |
| MS-27 | Guarantee on a customer job (added in M7c) | Months from completion; 3 (0 gives none) | MNT-09 | Which job a free rework may name; when MS-37's provision is released |
| MS-30 | Repairs on own equipment | Always an expense · Capitalised above an amount | MNT-01 (suggested A while equipment is stock) | The practice ledger. The second option stays disabled until the REN-12 asset register exists. |
| MS-31 | Account map: maintenance slots | Each slot unmapped until named (never guessed); the panel shows today's account for the same event as a hint (615 repair vouchers, 707 recharges) | MNT-02, MNT-06, C-04 | The practice ledger's "not mapped" check; month posting refuses while a used slot is empty |
| MS-32 | When parts reach the ledger | In the practice ledger, posted by month by the accountant · At issue, through `LedgerPoster` | MNT-02 | Section 5. The default posts nothing until the accountant posts a month, as manufacturing does. |
| MS-33 | Labour in the books | Report only; wages stay in 631/635 · Moved into jobs in the practice ledger | MNT-05 (suggested A) | Section 5; privacy of the practice ledger (§6.2) |
| MS-34 | Customer repair income | 707, the account recharges use today · One service account the accountant names · Parts to 700, labour to the service account | MNT-06, service income 700/706, SCF review (707 → 706 sub-account at the cutover) | The invoice lines' `IncomeAccountCode`. All three built (10 Nov 2026): the two others on the `maintenance.service_income` slot, see "MS-34 — as built" |
| MS-35 | Advances on customer jobs | Not taken (the customer pays on the invoice) · Taken on 419, tax per D45 | MNT-07, D45, C-18 | Built M7b (9 Nov 2026): Taken is allowed once customer advances are held on 419 (Finance › Settings); the advance names the job and clears against its invoice |
| MS-36 | Open customer jobs at period end | Left in expenses; open jobs listed · Work in progress at year end · Work in progress every month, reversed on the 1st | MNT-08 (suggested B) | The practice ledger, the year-end list |
| MS-37 | Free rework under guarantee | Added to the original job in reports; an expense in the books · Its own job in reports · A provision | MNT-09 (suggested A + B) | The profit report; the provision (rate %, built in M7c, see "M7c — as built") |
| MS-38 | Repair history | Per equipment type, with an optional item reference · Per piece above a value | MNT-10 (suggested B, needs REN-12) | The history report. Per piece waits for the asset register. |
| MS-39 | Post maintenance to the ledger from month | None · A month | Phase D model | Month posting (§5.3) |
| MS-40 | The accountant confirms | Not confirmed · Confirmed (who and when kept; changing a choice asks again) | §7 of the costing plan, applied here | Job costs read "provisional" until then |

Settings that are **not** choices, because they are rules already decided:

- **VAT on a customer job.** It starts without VAT and the seller ticks it (owner, 2026-11-05).
- **The labour ceiling.** It is shared with production (amendment 7).
- **Branch scope.** Jobs are branch-held like every document.
- **Parts valuation.** It follows the C-01 valuation switch, whichever method is in force.

---

## 5. Accounting: the practice ledger, extended

### 5.1 The principle

Documents that already post keep posting through their own poster, unchanged:

- the return note's 707 recharge;
- a repair voucher's 615;
- the scrap move's 318/310;
- a customer job's invoice (411 / income / 4457) and its receipt.

The **practice ledger** carries only what maintenance adds, and the accountant posts it by month, the same way as
production (Phase C2 and D, [phase-c.md](../manufacturing/phase-c.md)). By default the only new event that needs an
entry is parts leaving stock. Every other choice (labour moved into jobs, work in progress, capitalisation) is an
alternative the accountant can switch on.

### 5.2 Entries per event

The slot keys are appended to the account map (stored as text in `ManufacturingAccountMapping`; no schema change)
under a `maintenance.` prefix, as a `MaintenanceAccountSlots` list:

- `maintenance.own_parts` (hint 615)
- `maintenance.customer_parts` (hint 600 or 602, MNT-06)
- `maintenance.labour_absorbed`
- `maintenance.service_cost`
- `maintenance.wip`
- `maintenance.capitalised` (only after REN-12)
- `maintenance.service_income` (MS-34's own account: an income account of class 7, no hint; added 10 Nov 2026)

The stock credit uses the existing manufacturing stock slots by the part's role (raw material, supplies). Plain
merchandise (role `None`) is credited to 310, the account its receipt debited.

| Event | Default entry | When a setting changes it |
|---|---|---|
| Parts issued to an internal or recharged job (MJI-) | Dr `maintenance.own_parts` / Cr the part's stock, per document and stock group, rounded once | MS-32 "at issue": the same entry posts at once through `LedgerPoster` (source `maintenance-issue`) and leaves the practice ledger |
| Parts issued to a customer job | Dr `maintenance.customer_parts` / Cr the part's stock | Same |
| Parts returned (MJR-) | The issue turned round | Same |
| Outside work linked to a voucher | None. The voucher already posted to its category (615) when paid. | — |
| Labour on jobs | None. Payroll already charged 631/635. | MS-33 "moved into jobs": the month's increase per job, Dr `maintenance.own_parts` (own equipment) or `maintenance.service_cost` (customer) / Cr `maintenance.labour_absorbed`. Carries labour, so it needs rate access (§6.2). |
| Overhead | None | MS-10 with overhead and MS-33 moved: Dr the same / Cr the overhead-absorbed slot |
| Job completed: repaired units back to available | None (same account, as today) | — |
| Job completed: units beyond repair | Posted at once by today's `StockPosting.StateChange` (Dr 318 / Cr 310). Not in the practice ledger. | — |
| Customer job invoiced | Posted by the invoice. Listed in the practice ledger for the tie-out only. | — |
| Open customer jobs at month or year end | None; listed | MS-36: Dr `maintenance.wip` / Cr `maintenance.service_cost` (or the parts and labour slots) for the cost to date, reversed on the 1st |
| Major own repair completed | None | MS-30 capitalise (after REN-12): Dr `maintenance.capitalised` / Cr the slots it was charged to, at completion, for jobs at or above the threshold |
| Rework under guarantee | In the charges like any job; reports add it to the original | MS-37 provision: Phase M7 |

**Checks** (the R8 pattern, each pass or "to review"):

- every entry balances;
- parts issued less returned equal the stock credits;
- every slot the month uses names an account;
- for customer jobs: the month's invoices with `SourceType = "maintenance-job"` equal the jobs invoiced;
- under MS-36: the change in work in progress equals the open jobs' cost to date at the month's end less at its start.

### 5.3 How the existing pieces are reused

- **Pure entry builder.** `Application/Maintenance/MaintenancePracticeLedger.cs` reuses `PracticeLine`,
  `PracticeEntry` (its `OrderId` and `OrderNumber` carry the job), `PracticeCheck` and `PracticeLedgerResult` from
  `Application/Manufacturing/PracticeLedger.cs`.
- **Gatherer.** `Infrastructure/Maintenance/MaintenancePracticeLedgerReport.cs`, modelled on `PracticeLedgerReport`.
- **Page.** Manufacturing › Practice ledger gains a "Maintenance" tab, or the same component is mounted at
  Maintenance › Practice ledger. Both are computed on read, with no table.
- **Month posting.** The guard sequence of `ManufacturingLedgerPostings` (month ended, on or after the first month,
  previous month posted, checks pass, every branch readable, closed books refuse, reversal by mirror entries) is
  extracted into a shared month poster parameterised by source. Maintenance posts under source `maintenance`, with
  its own `MaintenanceMonthPostings` table of the same shape. Manufacturing's behaviour and tests stay unchanged.
- **Account map.** The same table, a second list of keys, edited in Maintenance › Settings with `accounting.manage`.

---

## 6. Permissions and privacy

### 6.1 Permissions

A new module `maintenance` in `PermissionCatalog.Modules` (`ParadiERP.Domain/Authorization/PermissionCatalog.cs`),
listed in `Capabilities.cs` and granted through the roles screen and the per-person editor (built 2026-11-05):

| Key | Allows |
|---|---|
| `maintenance.view` | See jobs and Waiting for repair at reachable branches |
| `maintenance.jobs.manage` | Open and edit jobs, issue and return parts, link outside work, book hours |
| `maintenance.jobs.close` | Complete a job (units back to available or to scrap) and cancel one |
| `maintenance.labour.override` | Book hours beyond attendance, with a reason |
| `maintenance.pricing` | Price customer jobs and keep the repair rates and internal markup |
| `maintenance.costs.view` | A job's cost and the profit reports. Per-job labour money follows §6.2. |
| `maintenance.settings` | The operational settings (MS-01 to MS-26) |

Existing keys are reused rather than duplicated:

- `accounting.manage` for MS-30 to MS-40, the account map and month posting;
- `reports.accounting` to read the practice ledger;
- `finance.invoices.create` to raise a customer job's invoice;
- `payroll.manage` + `manufacturing.costs.rates.view` for pay-rate access (one rule for both modules);
- `inventory.adjust` stays on the manual state route.

Every route goes into `RoutePermissions.cs`, under `AtCallersBranch` scope. The security pins move with them
(`PermissionCatalogTests`, `RoleStoreTests`, the route-plan fixture, the legacy snapshot). The Admin baseline gains
the new keys through `AuthorizationBootstrap`. Jobs are server-only, not a `SyncEntity`: a till never pulls them.

### 6.2 Privacy of labour money (`CostPrivacy`)

A job is usually one or two workers for a few hours, so its labour money gives a pay rate back even more directly
than a production order does. The manufacturing rule applies unchanged:

- **With rate access** (`CostPrivacy.RatesShown`): everything.
- **Without it**, the reader sees hours, parts, outside work, the price and the recharge, but not the job's
  labour money, total cost, profit, or anything that gives them back by subtraction. A month, branch or product sum
  is shown only from `CostPrivacy.MinimumOrders` (2) jobs and `MinimumWorkers` (2) workers
  (`CostPrivacy.LineSumShown`).
- **Exception (MS-12 = one workshop rate):** labour is valued at a published rate, so no figure gives back anyone's
  pay. Then `maintenance.costs.view` sees every job's full cost and profit. This is the setting to choose if the
  maintenance manager must see job profit without payroll access.
- **The practice ledger:** without labour in it (MS-33 default), its maintenance section needs only
  `reports.accounting` or `maintenance.costs.view`. With labour moved into jobs, it needs rate access, the same as
  manufacturing's (`payroll_details_withheld`).

---

## 7. Screens (Riaya, per CLAUDE.md)

A sidebar module **Maintenance** (`maintenance.view`, shown only when MS-01 is on), with icon and label. It never
appears in the topbar or at the till.

| Page | Pattern | Contents |
|---|---|---|
| Jobs | List (Products reference) | `.ui-toolbar` filters in the page toolbar: status, kind, product, date range (`app-date-input`). The table is `.ui-surface` > `.ui-table.ui-table--cards`; the job number is a `.ui-link`; status is an `app-status` pill (new family `maintenance-job` in `status-tones.ts`, its spec listing every status). A `⋯` menu holds Complete and Raise invoice, with Cancel last after a separator and marked `.is-danger`. Uses `app-table-state` and `app-list-footer`. The primary action "New job" goes in `appPageActions`. |
| Waiting for repair | List | Per branch and product: units in maintenance, and the return-note lines not yet covered by a job (contract, note, units, recharge). "Open a repair job" on each row. |
| Job | Entity detail (Rental contract reference) and multi-line (Rental return reference) | Header with number, kind, status in words, branch, and the linked contract, return note or customer. Cards: Units (in maintenance → repaired / beyond repair); Parts (compact line cards, issue and return, collapsed when done); Outside work; Hours (per employee and day, with the ceiling message in words); Cost (provisional or final in words, elements, notes; labour money per §6.2); Price and profit (company and workshop figures, recharge, invoice link). Sticky confirm footer for Complete. |
| New job | Form (`.bo-form`, 52rem) | Kind first, then fields per kind. Labels above fields, required in words. Dates via `app-date-input`. |
| Reports | Working panel, never folded | Job profit (internal vs company), recharge vs cost, cumulative maintenance cost per product against rental revenue, labour hours by job. Excel export through the existing workbook writer. |
| Settings | Long configuration form (`bo-form--full`) | `app-fold` sections: Module and jobs (open), Cost, Pricing, Accounting (editable only with `accounting.manage`), Account map. A section holding an invalid field opens. |
| Practice ledger | A tab on the existing page | Same component and checks; the post and reverse buttons need `accounting.manage`. |

All shown dates go through `I18nService.date` / `formatDay` on the company's time zone, and money goes through the
regional service. New rules use logical properties only. Each phase's review gives Arabic and French screenshots at
1440px and Arabic at 390px, from a capture harness `tests/e2e/capture-maintenance-screenshots.cjs` modelled on the
batch harnesses.

---

## 8. Migrations and phases

Each phase is shipped on its own approval. Every schema change is a migration in both sets
(`ParadiERP.Infrastructure` for SQLite, `ParadiERP.Migrations.SqlServer`), followed by `tests/e2e/provider-smoke.cjs`
on a fresh SQL Server database. Every phase leaves today's behaviour intact while MS-01 is off. Money, stock, ledger,
payroll and migration work stays with Claude (CLAUDE.md, "Small tasks go to Antigravity"). Copy and translation
tasks within a phase may go to Antigravity.

| Phase | What ships | Migration | Tests |
|---|---|---|---|
| **M0** Settings and keys | `MaintenanceSettings` (MS-01 to MS-40, defaults as section 4); the module's permission keys; Maintenance › Settings page (account map section read-only until M6); MS-01 off | `MaintenanceSettings`: one table | Defaults change nothing (rentals, stock and manufacturing suites untouched); settings saved and read back with the right keys, refused without them; permission and route pins; Angular spec for the settings page |
| **M1** Jobs and units (internal and recharged) | `MaintenanceJob` and its events; open from Waiting for repair or by hand; complete (repaired → available, beyond repair → scrap through `StateChange`); cancel; Jobs, Job and Waiting-for-repair pages; MS-02, MS-03, MS-05 | `MaintenanceJobs`, `MaintenanceJobEvents` | Units move exactly as the manual route moves them (same `StockMovement` and the same 318/310 entry for scrap); refuses more than the bucket holds; concurrent completions under the lock; branch scope (404); return-note line coverage; MS-02 "only by a job" refuses the manual route's maintenance → available; rentals' tests pass untouched |
| **M2** Parts and outside work | Issue and return from branch stock (kinds 18/19); outside work linked to vouchers with the shared cap; MS-32 "at issue" option behind the setting | `MaintenanceJobParts`, `MaintenanceJobExpenses` | Stock and movements per issue and return; refuses beyond stock; idempotent retry by client request id; the voucher cap across production expenses and job expenses together; with MS-32 at issue, one balanced entry per document and nothing in the practice ledger; with the default, no `LedgerEntries` row |
| **M3** Labour on jobs | Bookings with `OrderKind = Maintenance`; the shared ceiling; override; payroll lock; MS-04 | `LaborBookings.LineId`, `LogRowId` and `LaborBookingChanges.LogRowId` made nullable (SQLite rebuilds the table; SQL Server alters the columns); index (OrderKind, OrderId) | A production booking plus a maintenance booking on one day stop at attendance; override with a reason; payroll-locked month refused; manufacturing costing, B1 hash, labour absorption and daily-record tests pass unchanged; the migration on a copy of a database with bookings keeps every row |
| **M4** Job cost and privacy | `MaintenanceCosting` adapter over `ProductionCostEngine`; job cost panel; MS-10 to MS-13; recharge-vs-cost report; cumulative cost per product | None | Adapter on the MNT-04 worked example (9,900, and 11,385 at 15 %); standard then actual rate after payroll approval; burden and VAT follow C-06 and C-07; the engine `Version` and every B1 sheet hash unchanged; privacy: no labour money, total or profit without rate access, sums only from 2 jobs and 2 workers, everything with MS-12 workshop rate |
| **M5** Customer jobs, pricing and profit | MS-06 on; customer, quote, price bases MS-20 to MS-25; invoice through the existing writer (`InvoiceOrigin.MaintenanceJob`, income per MS-34, VAT ticked on request); internal vs company profit report; repair rates per product for MS-23 | `MaintenanceRepairRates`; job price columns if not in M1 | Each price basis on a worked example; the invoice posts 411 / income / 4457 through its own poster with the chosen income account; untaxed by default; a job invoiced once (second attempt refused); the profit report: company = revenue − cost, workshop = + internal charge, the company total removes internal charges; REN-11 example gives 5,000 DA |
| **M6** Practice ledger and month posting | Maintenance section of the practice ledger; maintenance slots in the account map; the month poster extracted and shared; MS-30 (expense branch only), MS-33, MS-36, MS-39, MS-40 | `MaintenanceMonthPostings` | Every entry balances; unmapped slots shown, never guessed, posting refused; `LedgerEntries` identical before and after a read; a month posted, refused twice, reversed and posted again; manufacturing's posting tests pass untouched; labour entries need rate access |
| **M7** Later, waiting on decisions | Capitalisation (MS-30, after the REN-12 register); per-piece history (MS-38); advances with tax (MS-35, D45); guarantee provision (MS-37) | Each its own | Each its own |

**Gates.** Live accounting from maintenance (M6 month posting, or MS-32 at issue) waits for the accountant's
confirmation (MS-40), or for the owner to turn it on knowingly, as was done for manufacturing on 2026-11-09. The
REN-12 cutover changes MS-30 and MS-38 and the 707 heading. When it happens, MS-34's default and the return note's
account move together, so that all repair income uses one account.

---

## 9. Open points

Each one comes with the default the plan uses until it is answered, so none of them blocks a phase.

1. **The audit document's own maintenance sections and the D-answers** could not be read (access refused). The
   recommended default is this plan's reading. Before M1, read the document; where it differs, change the matching
   setting's default or section 2/3 to follow it.
2. ~~What "internal vs company profit" means.~~ **Answered (owner, 2026-11-09): company profit** — external revenue
   − cost; no internal charge, no workshop profit (§3.3; MS-24 dropped).
3. **Do outside-customer repairs exist at all?** The recommended default is MS-06 off: built in M5 but hidden.
4. **Where repairs happen:** at each branch, or in a central workshop (a warehouse or a production line's location)?
   The recommended default is at the branch that holds the units in maintenance. Moving maintenance-state units
   between branches is not supported by transfers today, so a central workshop would be a later setting with its own
   transfer rule.
5. **The workshop's paper job card.** Ask for a photo, as D37 did for the daily sheet, so the job's fields match it.
   The recommended default is the fields in §2.2.
6. **Does manufacturing's labour-absorption report show maintenance hours?** The recommended default is no.
   Maintenance has its own hours report; the attendance ceiling already counts both.
7. **Overhead: one workshop rate shared with manufacturing (C-08), or its own?** The recommended default is its own,
   at 0 (MS-11).
8. **The recharge typed at return before the repair cost is known.** Should the clerk see a suggested rate? The
   recommended default is no suggestion (MS-23 "typed").
9. **A recharge covering units repaired by several jobs.** The recommended default is to share it by units (MS-26).
10. **Parts for a repair taken from a production line's stock.** The recommended default is no: parts come from the
    job's branch only. A line's materials stay manufacturing's.
11. **REN-12 (equipment as fixed assets), MNT-01 and MNT-10.** The recommended default is: always an expense, history
    per type plus an item reference. Capitalisation and per-piece history stay disabled until the asset register
    exists.
12. **707 under the wrong SCF heading, and service income 700 vs 706.** The recommended default is MS-34 = 707, the
    same account recharges use, so repair income is one account. It moves to the 706 sub-account at the cutover,
    together with the return note's mapping. Since 10 Nov 2026 the accountant may instead name a service income account
    (the `maintenance.service_income` slot, e.g. a 706 sub-account), alone or with the parts on 700 (MS-34 — as built);
    the default stays 707.
13. **Tax on repair advances (MNT-07, D45, C-18).** The recommended default is no advances on jobs (MS-35). The
    customer pays on the invoice.
14. **Year-end open jobs (MNT-08).** The recommended default is to leave them in expenses and list them (MS-36). The
    accountant can switch on work in progress in the panel.
15. **The accountant's answers to MNT-01 to MNT-10 and REN-11.** None has been answered. Each one is already a
    setting above with the questionnaire's suggestion as its default. The accountant's confirmation (MS-40) replaces
    a paper signature.

---

## M0 — as built (2026-11-09)

Phase M0 ships the settings and the keys only. Nothing reads the settings yet, so no behaviour changes: rentals, stock,
manufacturing and the ledger are untouched while MS-01 is off, and they stay untouched in M0 whatever it says.

### The settings row

`MaintenanceSettings` (`ParadiERP.Domain/MaintenanceEntities.cs`) is one row, `Id = 1`, `ValueGeneratedNever`, in the
style of `ManufacturingCostingSettings`. Reading it never writes it: before the first save the page shows a fresh row's
defaults. Every choice is an enum whose 0 value is the table's first option.

| ID | Column(s) | Options, default first |
|---|---|---|
| MS-01 | `Module` | `Off`, `On` |
| MS-02 | `UnitsLeaveMaintenance` | `ByHandOrByJob`, `OnlyByJob` |
| MS-03 | `JobsFromReturnNotes` | `OfferedInWaitingList`, `DraftJobPerDamagedLine` |
| MS-04 | `BookableEmployees` | `AnyEmployeeOfBranch`, `WorkshopListOnly` (taken since 2026-11-10; the list is its own table, "MS-04 as built") |
| MS-05 | `JobCloser` | `AnyHolder`, `NotTheOpener` |
| MS-06 | `CustomerJobs` | `Off`, `On` |
| MS-10 | `CostIncludes` | `PartsLabourOutsideWork`, `PartsLabourOutsideWorkOverhead`, `PartsAndOutsideWorkOnly` |
| MS-11 | `OverheadPerLabourHour` | an amount, 0; kept only with overhead, and then required |
| MS-12 | `LabourRate`, `WorkshopRatePerHour` | `PayrollDerived`, `WorkshopRate`; the rate is kept only with `WorkshopRate`, and then required |
| MS-13 | `BurdenAndVat`, `EmployerBurdenPercent`, `ExpenseVatPercent` | `FollowManufacturing`, `OwnValues`; the two percentages kept only with `OwnValues` |
| MS-20 | `CustomerPrice` | `Quoted`, `Itemised`, `CostPlus` |
| MS-21 | `LabourChargePerHour` | an amount, 0; kept only with `Itemised` |
| MS-22 | `MarkupPercent` | 0 to 1,000 %; kept unless `Quoted` |
| MS-23 | `ReturnRecharge` | `TypedAtReturn`, `SuggestedFromRate` |
| MS-25 | `PriceBelowCost` | `ShownAsLoss`, `NeedsConfirmation` |
| MS-26 | `RechargeSharing` | `ByUnits`, `AllToFirstJob` |
| MS-30 | `OwnRepairs` | `AlwaysExpense`, `CapitalisedAboveAmount` (refused until the REN-12 register) |
| MS-32 | `PartsPosting` | `PracticeLedgerByMonth`, `AtIssue` |
| MS-33 | `LabourInBooks` | `ReportOnly`, `MovedIntoJobs` |
| MS-34 | `CustomerIncome` | `SameAsRecharges`, `OneServiceAccount`, `PartsAndLabourApart` (the last two taken once `maintenance.service_income` names an account; 10 Nov 2026) |
| MS-35 | `Advances` | `NotTaken`, `Taken` (refused while customer advances are held on 411; M7b) |
| MS-36 | `OpenJobsAtPeriodEnd` | `LeftInExpensesListed`, `WorkInProgressAtYearEnd`, `WorkInProgressMonthly` |
| MS-37 | `Rework` | `AddedToOriginalJob`, `OwnJob`, `Provision` (refused until M7) |
| MS-38 | `RepairHistory` | `PerTypeWithReference`, `PerPiece` (refused until the REN-12 register) |
| MS-39 | `PostingFromMonth` | none, or a month (`yyyy-MM`) |
| MS-40 | `AccountantApproved`, `ApprovedBy`, `ApprovedAtUtc` | not confirmed, confirmed |

Plus `UpdatedAtUtc` and `UpdatedBy`. Amounts are 0 to 1,000,000 and percentages 0 to 100. An amount whose option does
not use it is saved as 0.

**Not built in M0.** MS-24 is dropped (§3.3). MS-31, the account map's maintenance slots, has no column: the slots are
keys of the existing `ManufacturingAccountMapping` table and arrive with the practice ledger in M6; the page says so.
The thresholds of the options waiting for REN-12 ("above an amount", "above a value") are not stored either: those
options are refused, and the amount comes with the phase that builds them.

**The options kept for later** are enum values the server knows and refuses with 400 `maintenance_option_not_yet`:
MS-30 capitalised and MS-38 per piece (REN-12 asset register), MS-35 advances and MS-37 provision (M7). The page lists
them, marked "later phase" and not selectable. The provision of MS-37 is refused as well as the three named in the
brief, because §8 puts it in M7 beside them.

**The accountant's confirmation (MS-40)** covers the cost choices (MS-10 to MS-13) and the accounting ones (MS-30 to
MS-38), not the module, jobs, pricing or the first month to post. It follows `ManufacturingCostingChoices`: unticking
clears who and when; a covered choice changed while confirmed is confirmed again under whoever saved it. One addition:
when the saver lacks `accounting.manage` (a `maintenance.settings` holder changing a cost choice), the confirmation is
withdrawn instead, so job costs read provisional until the accountant confirms again.

### Permissions

A tenth module, `maintenance`, in `PermissionCatalog` with the seven keys of §6.1 (`maintenance.view`,
`maintenance.jobs.manage`, `maintenance.jobs.close`, `maintenance.labour.override`, `maintenance.pricing`,
`maintenance.costs.view`, `maintenance.settings`). They are in `AdminBaseline.Included` (the Admin by role, repaired at
start-up by `AuthorizationBootstrap`), in `Capabilities.Maintenance` (reported at sign-in like manufacturing's), and in
no Manager or Cashier seed. Their names are in `core/i18n/translations-maintenance.ts`.

### Routes

| Route | Requirement |
|---|---|
| `GET /api/maintenance/settings` | `maintenance.view`, `maintenance.settings` or `accounting.manage` |
| `PUT /api/maintenance/settings` | `maintenance.settings` or `accounting.manage` |

Company-wide, with no branch rule, like Costing settings. `MaintenanceSettingsStore` compares the request with the
saved row: a change to MS-01 to MS-26 without `maintenance.settings` answers 403 `maintenance_settings_required`, and a
change to MS-30 to MS-40 (the confirmation included) without `accounting.manage` answers 403
`accounting_manage_required`. An unchanged half is fine, so each holder saves the whole panel. The legacy snapshot gives
both routes the Manager and the Admin (the Manager holds `accounting.manage` by role).

### Screen

Maintenance › Settings (`maintenance/settings`), a sidebar module of its own with a spanner icon, offered to holders of
`maintenance.settings` or `accounting.manage` (`core/nav.ts`). The module's other pages come with M1 and show only while
MS-01 is on. The page is a long configuration form (`bo-form--full`) in four `app-fold` sections — Module and jobs
(open), Cost, Pricing, Accounting — with a sticky save bar. Each select marks its first option "(suggested)" and says its
MS number; the module's state is said in words above the form. The Accounting section is editable only with
`accounting.manage`, the other three only with `maintenance.settings`.

### Migration and tests

Migration `MaintenanceSettings` in both sets (`ParadiERP.Infrastructure`, `ParadiERP.Migrations.SqlServer`): one new
table, nothing else. Tests: `ParadiERP.Tests/Maintenance/MaintenanceSettingsTests.cs` (defaults, saved and read back,
validation, each half refused without its key, the options kept for later, the confirmation, the route over HTTP), the
security pins (catalogue 92 keys in 10 modules, Admin 91, routes 325), and
`features/backoffice/maintenance/maintenance-settings.component.spec.ts`.

---

## M1 — as built (2026-11-09)

Phase M1 ships internal and recharged jobs over units already in maintenance. With MS-01 off (the default) nothing of it
is reachable and nothing in rentals, stock or the ledger changes: every M1 route answers 409 `maintenance_off`, the
manual state route and the return-note writer behave exactly as before.

### The job and its events

`MaintenanceJob` (`ParadiERP.Domain/MaintenanceEntities.cs`), numbered `MJ-000001` under the version lock, server-only.
Columns: `Number`, `BranchId`, `Kind` (`Internal`, `Recharged`, `Customer` — the last refused with 400
`maintenance_option_not_yet` until M5), `Status` (`Draft`, `Open`, `Completed`, `Invoiced` — M5 —, `Cancelled`),
`ProductId`, `Qty`, `ItemReference`, `ContractId`/`ReturnNoteId`/`ReturnNoteLineId` (recharged), `Description`,
`Diagnosis`, `RepairedQty`, `ScrappedQty`, `ClientRequestId` (a retried request answers the job it opened), and who and
when for created, opened, completed and cancelled (with the reason). `MaintenanceJobEvent` keeps each change
(`Drafted`, `Opened`, `Edited`, `Completed`, `Cancelled`), append-only. Not built in M1: `PlannedHours`, `QuotedPrice`,
`TaxMode`, `InvoiceId`, `ReworkOfJobId`, `CustomerId` (M3–M5), and the "move from available into maintenance in the same
step" of §2.2 (a job takes units already in maintenance; the manual route still puts them there). There is no
`RowVersion` column: production orders have none either — concurrency is the version lock, as for every document.

### Lifecycle, and exactly what moves at each step

| Step | Who | Stock | Ledger |
|---|---|---|---|
| Open by hand (`POST /jobs`) | `maintenance.jobs.manage` | Nothing. The job holds its `Qty`: refused (409 `exceeds_maintenance`) when it is more than the branch's `MaintenanceQty` of the product less what draft and open jobs already hold. A recharged job is also refused beyond its return-note line's `QtyPartialDamage` less what non-cancelled jobs cover (409 `line_covered`); a line of another branch is not found. | Nothing |
| Draft from a return note (MS-03) | the return note's clerk | Nothing (the note itself put the units in maintenance) | Nothing |
| Take a draft (`/open`) | `maintenance.jobs.manage` | Nothing; the taker becomes the opener | Nothing |
| Edit the words (`PUT`) | `maintenance.jobs.manage` | Nothing | Nothing |
| Complete (`/complete`) | `maintenance.jobs.close` (MS-05: not the opener) | `RepairedQty` maintenance → available and `ScrappedQty` maintenance → scrap; `RepairedQty + ScrappedQty` must equal `Qty`. Each through `StockStateMoves.MoveAsync`, the manual route's own code: the bucket read under `StockRows`' lock, `ProductStock.Move`, one `StockMovement` of kind `StateChange`, `QtyDelta` 0, `MovedQty`, `UnitCost` = `Product.CostPrice`, reason `maintenance->available` / `maintenance->scrap`, and the product touched so tills re-pull it. The only difference from the manual route: `DocumentNumber` carries the job number. If the bucket no longer holds the units (moved out by hand meanwhile), 409 `stock_state` and nothing moves. | Available: none. Scrap: `StockPosting.StateChange` posts Dr 318 / Cr 310 at `qty × CostPrice`, the same entry as the manual route (its description names the job). |
| Cancel (`/cancel`) | `maintenance.jobs.close`, with a reason | Nothing: the units stay in maintenance, free for another job | Nothing |

A completed job keeps covering its return-note line; a cancelled one gives the units back to Waiting for repair.

### Routes and permissions

| Route | Key |
|---|---|
| `GET /api/maintenance/jobs` (`branchId`, `status`, `kind`, `productId`) | `maintenance.view` |
| `GET /api/maintenance/jobs/{id}` | `maintenance.view` |
| `GET /api/maintenance/waiting` (`branchId`) | `maintenance.view` |
| `POST /api/maintenance/jobs` | `maintenance.jobs.manage` |
| `PUT /api/maintenance/jobs/{id}` | `maintenance.jobs.manage` |
| `POST /api/maintenance/jobs/{id}/open` | `maintenance.jobs.manage` |
| `POST /api/maintenance/jobs/{id}/complete` | `maintenance.jobs.close` |
| `POST /api/maintenance/jobs/{id}/cancel` | `maintenance.jobs.close` |

All under `AtCallersBranch`: a non-admin's `branchId` and a new job's branch are their own; a job of another branch named
by id answers 404 (`ForBranchOf(DocumentBranches.MaintenanceJob)`). The settings routes are unchanged. Security pins: 333
company routes; the legacy snapshot gives the eight routes to the Admin only (the maintenance keys are the Admin's by role).

### Settings honoured

- **MS-01** — off: every route above answers 409 `maintenance_off` (an endpoint filter); the Jobs and Waiting pages are
  not offered. Settings stays reachable as M0 made it.
- **MS-02** "only by a job" — with the module on, `POST /api/catalog/stock/state` refuses maintenance → available with
  409 `maintenance_only_by_job`; into maintenance and maintenance → scrap stay by hand. Default (or module off): untouched.
- **MS-03** "a draft job per damaged line" — with the module on, the return-note writer adds one `Draft` recharged job per
  line with `QtyPartialDamage > 0`, for all of its damaged units, inside the note's transaction
  (`MaintenanceJobs.DraftForReturnNoteAsync`, one call in `RentalDocuments.ReturnAsync`). It never refuses a note. The
  default reads the settings row and adds nothing; a test runs the same return in four companies (default, module on,
  drafts on, drafts with the module off) and finds the same note, stock, charges, movements and ledger in each.
- **MS-05** "not the opener" — the person who opened (or took) the job cannot complete it (403 `own_job`).

### Screens

Maintenance › Jobs (`maintenance/jobs`, a Riaya list: toolbar filters — words, status, kind, opened from/to with
`app-date-input` on the company's clock —, `.ui-table--cards`, the number a `.ui-link`, an `app-status` pill from the new
`maintenanceJob` family, the ⋯ menu with Open, Complete and, last after a separator, Cancel; `app-table-state`,
`app-list-footer`, "New job" in the page actions), Job (`maintenance/jobs/<id>`: facts, units, words, the completion
panel with a sticky action bar, history), New job (`maintenance/jobs/new`, `.bo-form`, kind first; prefilled from
`?line=` or `?product=&branch=`) and Waiting for repair (`maintenance/waiting`). The job pages show in the sidebar only
while MS-01 is on (`core/maintenance-module.service.ts` reads it from the settings route; the settings page updates it on
save) and to holders of `maintenance.view`. Translations fr/ar/en in `core/i18n/translations-maintenance.ts`.

### Migration and tests

Migration `MaintenanceJobs` in both sets: two tables (`MaintenanceJobs`, `MaintenanceJobEvents`) and their indexes, every
foreign key restricted; both report no pending model changes. Tests: `ParadiERP.Tests/Maintenance/MaintenanceJobTests.cs`
(13) — the same movements and 318/310 entry as the manual route, every unit accounted for, the bucket less open jobs,
cancelling frees, retry and customer kind, two completions at once on a file database (one wins, the units move once),
units moved out by hand under an open job, return-note line coverage, the module off, MS-02, MS-03 with the default
unchanged, MS-05, branch scope over the routes, editing. Angular: a spec for each page and the navigation rule.

---

## M2 — as built (2026-11-09)

Phase M2 ships parts issued to a job and returned from it, and outside work linked to payment vouchers. With MS-01 off
nothing of it is reachable (every route answers 409 `maintenance_off`); with it on, nothing posts to the ledger: parts
reach the books only through the practice ledger (M6), outside work through its voucher, as before.

### Parts (`MaintenanceJobPart`, plan §2.3)

One line per product of each issue (`MJI-000001`) or return (`MJR-000001`) document: `JobId`, `Kind` (`Issue`,
`Return`), `DocumentId` and `DocumentNumber` (shared by the request's lines and its stock movements), `ProductId`, `Qty`
(base unit), `UnitCost` and `Value` (frozen, 28,10), `CostBasis`, `StockMovementId`, `ClientRequestId` (unique with the
product), `Note`, who and when. Server-only.

**Movement kinds.** The plan named them 18 and 19; those went to the direct invoices (`InvoiceSale`, `InvoiceReturn`)
first, so the parts take the next free values: `MaintenanceIssue = 20`, `MaintenanceReturn = 21` (reasons `maint-issue`,
`maint-return`).

| Step | Who | Refused when | Stock | Cost on the movement and the line | Ledger |
|---|---|---|---|---|---|
| Issue (`POST /jobs/{id}/parts`, `Kind = Issue`) | `maintenance.jobs.manage` | the job is not open (a draft is taken first, 409 `not_open`; a closed one 409 `not_live`); a product unknown (404) or keeping no stock (400 `not_stocked`); more than the branch holds (409 `insufficient_stock`); more than it holds less what live production orders there still claim (409 `reserved_by_production`, `Production.ReservedAtAsync`, the production issue's own rule) | `StockQty` of the job's branch down, read and written under the version lock through `StockRows`; one `StockMovement` per product (the same product twice in a request is one movement of the sum), `QtyDelta` −qty, `MovedQty`, `BalanceAfter`, `DocumentId`/`DocumentNumber`, the user; the product touched so tills re-pull it | Switch C-01 off (default): `Product.CostPrice`, `CostBasis = CostPrice`. On: the weighted average of the moment through `LiveSaleCosting.MovementCostsOrFallbackAsync`, the same call as every other stock document; no average, or a failed computation, falls back to the cost price with `CostBasis = CostPriceFallback`; never refused | None |
| Return (`Kind = Return`) | `maintenance.jobs.manage` | the job is not open; more than is at the job for that product (409 `over_balance`) | `StockQty` back up, one `StockMovement` per product, `QtyDelta` +qty | **At what the job's issues of that product went out at**: their value ÷ their quantity (the last units back take exactly what is left of the issues' value, so nothing is stranded by rounding). Not replayed: goods come back at the cost they left at, as a production return and a credit note's goods do (valuation-shadow.md, "Goods coming back"). `CostBasis` is the weakest of the issues' (a fallback among them says fallback). The shadow (`WeightedAverageShadow`) reads a `MaintenanceReturn` at its own `UnitCost`, as an `InvoiceReturn`, so under the switch the pool gets back exactly what left | None |

A request carries its own id: a retry answers the job's parts as they stand and moves nothing again; an id already used
on another job answers 409 `request_id_reused`. Every write takes the version lock before it reads the job, the stock or
the history. Quantities are rounded to six places, as production's.

**Completion and cancelling with parts still at the job** (decided here). Completing is the repair, so what is still at
the job is what it used: completion is not refused, its event says "parts used N", and parts move no more on a closed
job. Cancelling is refused (409 `parts_at_job`) while anything is at the job, unless the request says `PartsUsed: true`
("or the parts stay as used", §2.3); the event then keeps "parts kept as used: N". Production completion, by contrast,
refuses until the line counts every material to zero; a production order has a separate consumption step, a job does not.

**The job's parts cost** (for M4) is the issues' values less the returns' (`MaintenanceJobWorkDto.PartsValue`). Under the
switch it is the average at each issue; parts issued before it was switched on keep their cost-price values.

### Outside work (`MaintenanceJobExpense`, plan §2.4)

`Description`, `Amount` (rounded as money through the regional service), optional `PaymentVoucherId`, `ClientRequestId`
(unique), who and when. Recorded on an open or completed job (an invoice that arrives after the repair still belongs to
it); refused on a draft (409 `not_open`) and a cancelled job (409 `not_live`). Posts nothing: the voucher already posted
to its category when paid (`repair` → 615).

**The voucher cap, one rule for both modules.** `VoucherExpenseLinks.LinkedAsync` sums the amounts linked to a voucher on
`ProductionOrderExpenses` **and** `MaintenanceJobExpenses`. Both writers (`ProductionOrders.AddExpenseAsync` and
`MaintenanceJobParts.AddExpenseAsync`) read it under the version lock and refuse with 409 `voucher_amount_exceeded` when
linked + the new amount would exceed the voucher's amount. As production's: only an expense voucher (409
`voucher_not_expense`), never a cancelled one (409 `voucher_cancelled`); a branch-held caller finds only a voucher of the
job's branch (404 otherwise), the company's roles any. The link only puts the amount into the job's cost; a voucher counts
as "settled" for costing only once approved or paid (M4, as `CostingExpense.Settled`).

### MS-32 (when parts reach the ledger)

Default `PracticeLedgerByMonth`: no `LedgerEntries` row from parts; the practice ledger that will carry them comes in M6.
`AtIssue` would post Dr `maintenance.own_parts` / Cr the part's stock slot (§5.2). The plan fixes only the 310 credit for
plain merchandise: the debit is one of the account map's maintenance slots (MS-31), "never guessed" (615 is only a hint),
and the stock credit by role (spare parts, consumables) is the accountant's mapping too. Both arrive with M6. So in M2 the
settings store refuses `AtIssue` with 400 `maintenance_option_not_yet` (the page marks it "later phase"), and the parts
writer posts nothing whatever the row says (a row saved "at issue" in the M0 window is treated as the default; a test pins
it). M6 builds the at-issue entry together with the slots.

### Routes and permissions

| Route | Key |
|---|---|
| `GET /api/maintenance/jobs/{id}/work` (parts per product, document lines, outside work, totals, MS-32 as saved) | `maintenance.view` |
| `GET /api/maintenance/jobs/{id}/work-options` (the branch's issuable products — on hand less production claims — and its expense vouchers with something unlinked) | `maintenance.jobs.manage` |
| `POST /api/maintenance/jobs/{id}/parts` | `maintenance.jobs.manage` |
| `POST /api/maintenance/jobs/{id}/expenses` | `maintenance.jobs.manage` |

All carry `ForBranchOf(DocumentBranches.MaintenanceJob)` (another branch's job answers 404) under the module filter.
`POST /jobs/{id}/cancel` takes `PartsUsed` (default false). Security pins: 337 company routes; the legacy snapshot gives
the four routes to the Admin only.

### Screen

The Job page gains two cards between Units and the words (`maintenance-job-parts.component.ts`). **Parts**: one compact
card per product (issued, returned, at the job, average cost out, value) with its MJI/MJR document lines; a card folds to
its summary once nothing of it is at the job or the job is closed ("Returned in full", "Used by the repair"); on an open
job, an issue form (product picker over the issuable products with what is free, quantity) and a return on each card; the
MS-32 note in words ("nothing is posted at issue …"). **Outside work**: a `.ui-table--cards` list (description, amount,
voucher number and status in words, who and when) and, on an open or completed job, a form whose voucher select offers the
branch's expense vouchers with what is left of each. The completion panel says how many parts will count as used; the
cancel dialog asks to confirm they stay as used. A retried click keeps its request id. Translations fr/ar/en in
`core/i18n/translations-maintenance.ts`; the two movement kinds in `translations-valuation.ts`.

### Valuation note

Under the C-01 switch a maintenance issue takes its parts out of the shadow's pool at the average but posts nothing until
M6, so, like a production issue, it is a source of difference between the stock accounts and the shadow's value until the
practice ledger posts it.

### Migration and tests

Migration `MaintenanceJobParts` in both sets: two tables (`MaintenanceJobParts`, `MaintenanceJobExpenses`) and their
indexes, every foreign key restricted; both report no pending model changes. `MaintenanceJobEventKind` gains
`PartsIssued = 10`, `PartsReturned = 11`, `ExpenseRecorded = 12` (numbered from 10 so the labour phase built beside M2 can
append from 5). Tests: `ParadiERP.Tests/Maintenance/MaintenancePartsTests.cs` (10): stock and movements per issue and
return, back at the issue's cost after the cost price changed, no `LedgerEntries` row; refusals beyond stock, beyond what is
at the job, untracked, unknown; retried request ids; draft, completion and cancel with parts at the job; MS-32 at issue
refused and a saved one still posting nothing; under the weighted average, an issue at the average and a return at its
issue's cost that the replay then agrees with (5,850 for the whole shelf, not 5,880); outside work and the voucher's reach,
kind, status, cap and rounding; the cap across a production order and a job together, with a production reservation
respected; the routes (404 for another branch, 409 with the module off). The M0 settings tests now save other accounting
choices where they saved `AtIssue`, and the later-phase theory covers it. Angular: `maintenance-job-parts.component.spec.ts`
(6) and one more in `maintenance-job.component.spec.ts`; the settings spec marks MS-32 "at issue" later.

## M3 — as built (2026-11-09)

Phase M3 ships labour on jobs: hours only, no rate and no money (a job's cost is M4). With MS-01 off nothing of it is
reachable (every route answers 409 `maintenance_off`), and production's hours, records, costs and reports read exactly
what they read before.

### Where the hours go

Into production's `LaborBookings`, as §1.4 and §2.5 planned: `OrderKind = Maintenance`, `OrderId` = the job, and no line,
record row or stage (`LineId`, `LogRowId` and `StageId` null). `PayrollBranchId` is the employee's branch and
`PayrollYear`/`PayrollMonth` the day's, as for production. `OvertimeHours` (C-05) is recorded on a job booking too, 0 to
the hours. `LaborBookingChanges` keeps each booking and removal as today, and now also names what the booking was on
(`OrderKind`, `OrderId`), so a removed job booking still says which job it was; rows from before M3 read Production with
no order.

### The booking rules

| Rule | As built |
|---|---|
| One ceiling (amendment 7) | `LabourCeiling.BookedAsync` (Infrastructure/Manufacturing) sums every booking of the person that day — production records and jobs alike — and both `ProductionLogs.BookAsync` and `MaintenanceLabour.BookAsync` check `booked + hours ≤ attendance` (`AttendanceCapacity`, payroll's precedence) with it, under the version lock both writes take. Production's check already counted every kind; it now reads the same helper, with the same message. Past it: 409 `exceeds_attendance`. |
| Override | A reason (kept on the booking with who allowed it) and `maintenance.labour.override` (403 `override_forbidden` without it). The manufacturing override does not open a job, nor the maintenance one a production record. |
| Payroll month | Booking or removing in a month a non-draft run holds for the employee's branch: 409 `payroll_month_locked`. |
| One per person, job and day | Checked in code under the lock (409 `already_booked`: remove and book the day's total). The unique (EmployeeId, LogRowId) index keeps its meaning for production rows (SQLite holds NULLs distinct; SQL Server filters them out). |
| The job | Open only (409 `job_not_open` on a draft — take it first — or a closed job); a completed or cancelled job keeps its hours and shows them. |
| The day | Not still to come (400 `future_day`, the company's today on the server's clock); hours in (0, 24], overtime in [0, hours]. |
| MS-04 | `AnyEmployeeOfBranch` (default): an employee of the job's branch, employed that day (hired on or before it, not left before it); 400 `employee_not_of_branch` / `employee_not_employed`. `WorkshopListOnly`: as built in M3, no workshop list existed, so the settings refused it (400 `maintenance_option_not_yet`) and booking answered 409. **Since 2026-11-10** the list exists and the option is taken: only someone on the list of the job's branch is offered and booked, else 400 `employee_not_on_workshop_list` ("MS-04 as built"). |

Nothing in attendance or payroll is written.

### Routes

| Route | Key |
|---|---|
| `GET /api/maintenance/jobs/{id}/hours` | `maintenance.view` — the bookings, their total and the company's today |
| `GET /api/maintenance/jobs/{id}/hours/candidates?day=` | `maintenance.jobs.manage` — MS-04's people that day, with attendance, hours booked on every record and job, already on this job, payroll closed |
| `POST /api/maintenance/jobs/{id}/hours` | `maintenance.jobs.manage` (+ `maintenance.labour.override` past attendance) |
| `DELETE /api/maintenance/jobs/{id}/hours/{bookingId}` | `maintenance.jobs.manage` |

Under the jobs group (`AtCallersBranch`, `ForBranchOf(DocumentBranches.MaintenanceJob)`: another branch's job answers
404), `[FromServices]` throughout (`MaintenanceEndpoints.Labour.cs`). Security pins: 337 company routes; the legacy
snapshot gives the four routes to the Admin only.

### Production's readers of `LaborBookings`, audited

| Reader | What it does with a job booking |
|---|---|
| `ProductionCosts.LabourAbsorptionAsync` (labour absorption report) | Now filters `OrderKind == Production` and a line explicitly; maintenance hours have no line and are not absorbed (open point 6's default: they have their own hours). |
| `ProductionCosts` cost inputs (cost engine), `ProductionCostSnapshots` (B1 sheets), `ProductionCostHistory` (B2 history and `CostReconciliation`, which is fed from it), `ProductionOrders` (order hours, cost inputs), `PracticeLedgerReport` | Already filtered `OrderKind == Production` (the practice ledger also needed its line test made null-safe). Unchanged. |
| `ProductionLogs` (record summaries, record, save, unbook) | Keyed on the record's rows (`LogRowId`), so a job booking never appears; made null-safe only. |
| `ProductionLogs` ceiling and candidates | Count every kind **on purpose**: that is the one ceiling. |
| `LabourRates` marked overtime (C-05) | Counts every kind **on purpose**: it is the employee-month's denominator for spreading payroll's overtime premium over marked hours, so a maintenance overtime hour takes its share and production is not charged the premium of hours spent on jobs. With no job booking marked overtime it reads exactly as before. |

### Migration

`MaintenanceLabour` in both sets. **SQLite** rebuilds `LaborBookings` and `LaborBookingChanges` (EF's table rebuild:
`ef_temp_` copy with `INSERT … SELECT` of every column, drop, rename, indexes recreated, foreign keys kept), makes
`LineId`, `LogRowId` and the change log's `LogRowId` nullable, adds `LaborBookingChanges.OrderKind` (default 0 =
Production) and `OrderId` (null), and indexes (OrderKind, OrderId). **SQL Server** drops the unique (EmployeeId,
LogRowId) index, alters the three columns to `NULL` in place, adds the two columns, and recreates the unique index with
`WHERE [LogRowId] IS NOT NULL` plus the new (OrderKind, OrderId) index. Every existing row keeps every value on both
(they are all production rows with a line and a row, so all stay in the filtered index). Both sets report no pending
model changes.

### Screens

The Job page gains an **Hours** card (`maintenance-job-hours.component.ts`, between Units and the words): the bookings
as a `.ui-table--cards` table — day, person, hours, of which overtime, the attendance the day showed, who booked it and
when, an override in words —, the total, and a `⋯` menu with "Remove these hours" (`.is-danger`) on an open job. "Book
hours" opens an `app-dialog`: the day (`app-date-input`, starting at the company's today from the server), the person
(each option says their attendance and hours already booked, or "declared per month", "month's payroll closed",
"already on this job that day"), then the ceiling in words — "Karim's attendance on 20/10/2026 shows 8 h; 5 h are
already booked on production records and jobs, so 3 h are left." —, hours and overtime; past what is left, the
override holder gives the reason and anyone else is told only an override holder may book it. Translations fr/ar/en
(`maint.hours.*`).

### Tests

`ParadiERP.Tests/Maintenance/MaintenanceLabourTests.cs`: a production booking and a job booking on one day stop at
attendance, both ways round; the override (reason required, the maintenance key and not the manufacturing one, kept on
the booking); a payroll-locked month refuses booking and removal and nothing reaches attendance or payroll; one per
person, job and day, an open job of the branch, the day, the hours, the change log naming the job; branch scope (404)
and the module off; MS-04 candidates and a row holding the workshop list; maintenance hours absent from labour
absorption, the daily record and the order's cost inputs; the migration on SQLite from a database at `MaintenanceJobs`
holding bookings and change rows (`MaintenanceLabourMigration`), every row kept, the unique row index still refusing a
duplicate, two row-less bookings fitting. `MaintenanceLabourSqlServerTests` runs the same upgrade on SQL Server where
`ALGERIAPOS_TEST_SQLSERVER` is set (skipped here: no server for this repo was running). The settings theory gains MS-04's
workshop list. Manufacturing's costing, B1 hash, labour absorption and daily-record tests pass unchanged. Angular:
`maintenance-job-hours.component.spec.ts`.

**Not built in M3:** `PlannedHours` on the job (the estimate belongs to M4/M5), any labour money (M4), a workshop list
(MS-04's second option stayed refused until "MS-04 as built", 2026-11-10), an hours report across jobs (M5).

## M4 — as built (2026-11-09)

Phase M4 ships a job's cost, the labour privacy of §6.2 and two cost reports. Nothing is stored, frozen or posted: a job's
cost is computed on read, like costing Phase A. With MS-01 off every route answers 409 `maintenance_off`. No migration.

### The adapter (`Application/Maintenance/MaintenanceCostAdapter.cs`)

Pure, as §3.1 planned. `InputFor(job)` builds a `CostingInput` and `Compute(job)` calls `ProductionCostEngine.Compute`
unchanged: the engine's file is not touched, its `Version` stays `B1.0`, and no B1 sheet changes (a test freezes a
production sheet, costs a job of the same worker and month, and finds the frozen sheet, its source hash and the live order
cost exactly as before).

| Element | What goes in | Setting |
|---|---|---|
| Parts | Each product issued becomes a `CostingMaterial`: required = issued, estimate = the issues' average, everything not returned consumed (nothing "at the line"). Value = issued less returned at the values frozen on the movements (M2). The engine's three groups (materials, consumables — spare parts are here —, accessories) are shown as one "parts" figure. | — |
| Labour | Each booking (`OrderKind = Maintenance`, `OrderId` = the job) a `CostingBooking`, the job as its stage. Rate per employee-month from `LabourRates.ForAsync`: **standard until the month's payroll runs are approved or paid, actual after**, exactly as production. | MS-12 `WorkshopRate`: every employee-month at the panel's rate, as an all-in published rate (`LabourRateKind.Actual`, no employer burden added, no C-05 premium spread). MS-10 `PartsAndOutsideWorkOnly`: no booking reaches the engine; the hours are still counted and said in a note (`labour_excluded`). |
| Outside work | Each `MaintenanceJobExpense` a `CostingExpense`, settled once its voucher is approved or paid. | C-07 / MS-13: VAT taken out (amount × 100 ÷ (100 + rate)). |
| Overhead | Labour hours × MS-11. | Only under MS-10 `PartsLabourOutsideWorkOverhead`; otherwise the element is `excluded` (final) and the engine's "overhead not set" note is dropped. |
| Employer burden | The engine's C-06 rule: added only to employee-months whose payslips carried no employer social contribution. | MS-13 `FollowManufacturing` (default): Manufacturing › Costing settings' burden as it counts **in reports** (`ReportsOnly` or `StockValue`; `ProductionCosts.PolicyOf(…, forStockValue: false)`, extracted from `PolicyAsync` without change) and its expense VAT (C-07 `BeforeRecoverableVat`). MS-13 `OwnValues`: the two percentages of Maintenance › Settings. |
| C-05 | Labour absorption as Manufacturing › Costing settings has it (not an MS setting): a job's hours are costed at production's rates, and M3 already counts a job's marked overtime hours in the month's spread. | — |

C = parts + labour + outside work + overhead. No releases, no standard hours, no scrap recovery; planned and accepted
quantity are the job's units (or 1). Dropped notes: `labour_estimate_unavailable`, `cost_not_frozen`,
`final_release_below_zero`; `material_reference_cost` when no part was issued.

**Status.** `approved` (shown "Final") only when the accountant confirmed (MS-40, `AccountantApproved`) **and** every element
is final (`approved`, `recorded`, `none` or `excluded`); otherwise `provisional`, with the notes saying why. As on a production
sheet, parts valued at their issue cost read `reference_cost` until the valuation method is signed, so a job with parts stays
provisional for now; a labour line at the standard rate or without employer social stays provisional; an expense without an
approved or paid voucher too.

**MNT-04's worked example** (pinned): parts 3,200 + 6 h × 450 + painting 4,000 = **9,900**. MS-11 is per labour hour, so
"15 %" is reproduced as 247.5 an hour over the job's 6 h: overhead 1,485, total **11,385**.

### Recharge (REN-11, MS-26)

A recharged job's share of its return-note line's `MaintenanceValue` (the amount typed at return, before tax): `ByUnits`
(default) — the line's recharge × the job's units ÷ the line's repairable units; `AllToFirstJob` — all to the line's first
non-cancelled job by opening time, 0 to the others. A cancelled job earns none. REN-11's example gives 16,000 − 11,000 =
**5,000**, split 10,000/6,000 by units.

### Privacy (§6.2, `CostPrivacy`)

One rule for both modules: `CostPrivacy.RatesShown` gains an overload over a key check, and maintenance reads the token's
keys through it.

- **Labour money is private** when it is valued at payroll rates: MS-12 `PayrollDerived` and labour in the cost (MS-10). Then
  a reader without `payroll.manage` **and** `manufacturing.costs.rates.view` sees, on a job that has hours: hours, parts,
  outside work, overhead and the recharge, but not the labour money, the total, the recharge less cost, nor any labour line's
  rate or cost (`labourMoneyShown: false`). A job with no hours shows its total (nothing in it gives a pay back).
- **Sums** (a product's row, a report's totals): shown without rate access only from `MinimumOrders` (2) jobs **with labour**
  and `MinimumWorkers` (2) distinct workers among them; a sum with no labour in it is shown. The product report's totals are
  shown only when no product's row is withheld; the recharge report's totals follow the same 2/2 rule over all its rows.
- **MS-12 `WorkshopRate`** or **MS-10 `PartsAndOutsideWorkOnly`**: nothing gives anyone's pay back, so every
  `maintenance.costs.view` holder sees every figure.
- Hours stay shown to everyone who reads the job (as the M3 Hours card already does); a single-worker job's hours are not
  withheld (production withholds them on an order; the plan says a job's reader sees hours).

### Routes

| Route | Key |
|---|---|
| `GET /api/maintenance/jobs/{id}/cost` | `maintenance.costs.view` |
| `GET /api/maintenance/reports/recharges?from&to&branchId` | `maintenance.costs.view` |
| `GET /api/maintenance/reports/products?from&to&branchId` | `maintenance.costs.view` |

Under the jobs group (`AtCallersBranch`, module filter, `[FromServices]`): another branch's job answers 404; a report's
`branchId` is the caller's own branch for everyone but the Admin. `from`/`to` are company days on the job's opening; a
reversed period answers 400 `invalid_period`. The recharge report lists recharged jobs that are not drafts or cancelled; the
product report every job but drafts (a cancelled job keeps the parts and hours it used). Security pins: 344 company routes; the
legacy snapshot gives the three routes to the Admin only.

### Screens

The Job page gains a **Cost** card (`maintenance-job-cost.component.ts`, shown to `maintenance.costs.view`): the status pill
("Final"/"Provisional"), one sentence on what the cost is made of under MS-10/MS-12 and whether the accountant confirmed
(MS-40), the lead tile (the total, or "Not shown (pay rates)"), parts, labour (with its hours), outside work, overhead when
included, the recharge and recharge less cost for a recharged job; the elements with what each rests on (`costElement` tones,
plus `excluded`); the costed hours per employee and payroll month (rate and amount only when shown); the notes in words
(manufacturing's sentences, and maintenance's own for `labour_excluded`, `labour_workshop_rate`, MS-40, parts valuation and
overhead). **Maintenance › Reports** (`maintenance/reports`, `maintenance.costs.view`, while MS-01 is on): a working panel,
never folded — period filters (`app-date-input`), "Recharge against cost" (job, return note, product, units, share of the
line's recharge, cost, difference, cost status, totals) and "Cumulative cost per equipment type" (jobs, units repaired and
scrapped, parts, outside work, overhead, hours, labour, cost, recharges, totals); a withheld figure is said in words, never a
zero. Translations fr/ar/en in `core/i18n/translations-maintenance-cost.ts`.

### Tests

`ParadiERP.Tests/Maintenance/MaintenanceCostTests.cs` (9): the adapter on MNT-04 (9,900; 11,385 with 247.5/h; 7,200 without
labour; 10,200 at a 500 workshop rate); final only when confirmed and every element final, the engine's `Version` `B1.0` and
the adapter's total the engine's own; the standard rate before payroll approval, 450 actual after (2,700, total 9,900); C-06
burden (20 % in reports: 3,240) and C-07 VAT (4,760 → 4,000) followed, MS-13 own values (10 %: 2,970); privacy — no labour
money or total with neither key or with `payroll.manage` alone, everything with both, everything under the workshop rate and
with labour out of the cost; the product sum from two jobs with labour and two workers only, the same figure as the Admin's;
REN-11 recharge against cost by units and all to the first job, a withheld row and withheld totals; 404 another branch, 400 a
reversed period, 409 module off; production's frozen B1 sheet, its hash and its live cost untouched. Angular:
`maintenance-job-cost.component.spec.ts` (3), `maintenance-reports.component.spec.ts` (3), the Reports page in
`nav-keys.spec.ts`, `excluded` in the `costElement` family.

**Not built in M4:** the Excel export of §7's reports, labour hours by job and internal-vs-company profit (M5's reports),
rental revenue beside the cumulative cost per product (M5), freezing a job's cost at completion.

## M6 — as built (2026-11-09)

Phase M6 ships the maintenance section of the practice ledger, the account map's maintenance slots, the month poster shared
with manufacturing, and MS-32 "at issue". Nothing posts by itself: by default parts reach the books only when the accountant
posts a closed month, and every other entry is an option the accountant switches on. With MS-01 off the practice ledger answers
409 `maintenance_off`; the account map stays reachable beside the settings.

### The account map's maintenance slots (MS-31)

`MaintenanceAccountSlots` (`ParadiERP.Domain/ManufacturingAccountingEntities.cs`): rows of the same `ManufacturingAccountMappings`
table, keys under `maintenance.`, seeded empty and never guessed. Manufacturing's list (`ManufacturingAccountSlots.All`) is
unchanged, so Costing settings and production's practice ledger show what they showed.

| Slot | For | Hint shown (never used) |
|---|---|---|
| `maintenance.own_parts` | Repairs of own equipment (internal and recharged jobs): parts, and labour and overhead when moved | 615 |
| `maintenance.customer_parts` | Parts issued to a customer job | 600 |
| `maintenance.labour_absorbed` | The credit of labour moved into jobs (MS-33) | — |
| `maintenance.overhead_absorbed` | The credit of overhead moved into jobs (MS-10 with overhead and MS-33) | — |
| `maintenance.service_cost` | A customer job's labour and overhead when moved; the credit of its work in progress (MS-36) | — |
| `maintenance.wip` | Open customer jobs' work in progress (MS-36) | — |
| `maintenance.service_income` | Added 10 Nov 2026 (MS-34): a customer job's service income under "one service account" or "parts and labour apart"; an active income account of class 7 only | — |

`maintenance.capitalised` (§5.2) is not added: capitalising (MS-30) stays refused until the REN-12 register, and the slot comes
with it. `maintenance.overhead_absorbed` is added because §5.2's overhead row credits "the overhead-absorbed slot". The stock a
part leaves is the shared manufacturing stock slot of its role — raw material; supplies for consumables, accessories and spare
parts; semi-finished; finished goods — mapped in Costing settings and shown read only on the maintenance map. Plain merchandise
(no role) is credited to the chart's stock account (`LedgerAccounts.Inventory`, 310), the account its receipt debited, as §5.2
fixes; it stands as `maintenance.merchandise_stock` in the entries and is not in the map.

Routes: `GET /api/maintenance/account-map` (the settings' keys: `maintenance.view`, `maintenance.settings` or `accounting.manage`)
lists the six slots and the four shared stock slots with their accounts and hints; `PUT /api/maintenance/account-map`
(`accounting.manage`) takes maintenance's slots only (a stock slot answers 400 `invalid_account_map`), an active account of the
chart or none. Maintenance › Settings gains an Account map fold (open while a slot of its own is unmapped and the reader holds
`accounting.manage`; read only otherwise), saved by its own button.

### Entries per event (`Application/Maintenance/MaintenancePracticeLedger.cs`, pure)

Built from the jobs' events and written nowhere, reusing manufacturing's `PracticeLine`, `PracticeEntry` (its order is the job),
`PracticeCheck` and `PracticeLedgerResult`. Values at the ledger's three decimals, each parts document rounded once per stock group.

| Event | Default | Under the option |
|---|---|---|
| Parts issued (MJI-) to an internal or recharged job | Dr `maintenance.own_parts` / Cr the part's stock, per document and group (MS-30 "always an expense", the only branch built) | MS-32 "at issue": posted at once, not here (below) |
| Parts issued to a customer job | Dr `maintenance.customer_parts` / Cr the part's stock | Same |
| Parts returned (MJR-) | The issue turned round, at the value the return carried (M2: what its issues went out at) | Same |
| Labour on jobs | None (MS-33 "report only": payroll charged 631/635); a note says so | MS-33 "moved": per job, its labour to date at the month's end less at its start, as the job's cost values it (M4: payroll's rate of each employee-month known today, or MS-12's workshop rate), Dr own repairs (own equipment) or service cost (customer) / Cr `maintenance.labour_absorbed`, dated the job's close in the month or the month's last day. Under MS-10 "parts and outside work only" a job's cost holds no labour, so nothing moves (note `labour_not_in_cost`) |
| Overhead | None | MS-10 with overhead and MS-33 "moved": the same way, Cr `maintenance.overhead_absorbed` |
| Open customer jobs at the period end | None; listed with their cost to date (withheld, said in words, when it would give a pay rate back) | MS-36 "monthly": each customer job open at the month's end, its cost to date (M4's cost as of that moment: parts, outside work, labour and overhead per MS-10), Dr `maintenance.wip` / Cr `maintenance.service_cost`, reversed on the 1st. "At year end": December's end only, reversed on 1 January |
| Outside work, the return note's recharge, scrap, a customer job's invoice | Posted by their own documents; nothing here (a note says so) | — |
| A customer job's invoice of the month (`SourceType = maintenance-job`) | Listed for the tie-out only | — |

Customer jobs are Phase M5's, built beside M6: the section reads any job of kind `Customer` and any invoice with `SourceType =
"maintenance-job"` (`MaintenancePracticeLedgerReport.InvoiceSourceType`), so their entries and tie-out appear once M5 writes
them; the test writes such a job and invoice by hand. A customer job is open at a moment when it was created before it and not
completed (or invoiced) or cancelled before it.

**Checks** (the R8 pattern, each pass or "to review"): `entries_balance`; `parts_tie` — the documents' issues less returns equal
the stock credits (manufacturing's `materials_tie`); `wip_tie` — the change in work in progress equals the open customer jobs'
cost to date at the month's end less at its start, when carried; `invoices_tie` — every customer job invoice of the month names a
customer job, invoiced once (counted, not valued: the invoice's poster valued it); `accounts_mapped` — every slot the month uses
names an account. An unmapped slot is shown "not mapped", never guessed, and blocks posting. A note says when the accountant has
not confirmed the choices (MS-40).

### The practice ledger on read (`Infrastructure/Maintenance/MaintenancePracticeLedgerReport.cs`)

`GET /api/maintenance/practice-ledger?year=&month=&branchId=`: no month asked, the company's month on the server's clock; a
month to come answers 400 `month_in_future`. Under the jobs group: a non-admin's `branchId` is their own branch. Access (§6.2):
`reports.accounting` or `maintenance.costs.view`; when the entries carry labour at payroll rates — MS-33 "moved" or MS-36
carrying work in progress, with labour in the cost (MS-10) at the payroll-derived rate (MS-12) — only with pay-rate access
(`payroll.manage` + `manufacturing.costs.rates.view`, 403 `payroll_details_withheld`). Under the workshop rate nothing gives a
pay back, so the reader needs no rate access. Every read only reads: a test compares every ledger line before and after.

### Month posting (MS-39) and the shared month poster

`Infrastructure/Ledger/LedgerMonthPoster.cs` is manufacturing's Phase D sequence extracted and parameterised by source and
postings table (`ILedgerMonthPosting`, implemented by `ManufacturingMonthPosting` and the new `MaintenanceMonthPosting`): month
ended, every check passes, not posted already, the month before posted unless it is the first, then one ledger entry per practice
entry through `LedgerPoster` in one transaction, source id `MD5("{source}/{yyyy-MM}/{generation}/{index}")`, the first month
leaving out the reversal of work in progress that never posted, closed books refusing (`books_closed`); reversal by mirror
entries of the last month posted only. `ManufacturingLedgerPostings` keeps its own checks (permission, first month chosen, month,
every production line) and its codes and sentences, and now calls the shared poster: its source ids, entries and answers are
unchanged (its `EntryId` gives the same ids), and its Phase D test passes untouched. The manufacturing model is unchanged (the
interface adds no column).

`MaintenanceLedgerPostings` (source `maintenance`): `POST /api/maintenance/practice-ledger/post` and `/reverse`
(`accounting.manage`), 409 `posting_off` until MS-39 names the first month, 409 `before_posting_start`, 403
`post_needs_every_branch` for a caller held to a branch (a month is posted whole), then the shared sequence. The posting reads
the month as the caller, so labour at payroll rates needs pay-rate access to post too. MS-40 is not a gate: as for manufacturing,
choosing the first month (an `accounting.manage` act) is the knowing switch (§8 Gates); the page says when the choices are
unconfirmed.

### MS-32 "at issue" — enabled

The slots now exist, so the option is taken (`Infrastructure/Maintenance/MaintenancePartsAtIssue.cs`). The settings store accepts
"at issue" once `maintenance.own_parts` names an account (409 `accounts_not_mapped` before). Then each parts document posts at
once, in the issue's own transaction, through `LedgerPoster`: source `maintenance-issue`, source id the document's id, dated now
at the job's branch, the same lines the practice ledger would have made (Dr own repairs or customer parts / Cr each group's
stock, rounded once per group), so one balanced entry per document. A document posted at issue is left out of the practice ledger
(decided per document by its entry, not by the setting, so switching the option mid-month never posts a document twice) and
listed under "Posted at issue". Never guessed: should a slot the document uses name no account at that moment, nothing posts and
the document waits in the practice ledger like the default, shown "not mapped"; the issue itself is never refused for an account.
The M2 test that pinned the refusal was replaced by the M6 one.

### Screens

Maintenance › Practice ledger (`maintenance/practice-ledger`, shown while MS-01 is on to holders of `reports.accounting` or
`maintenance.costs.view`; a session from before keys, Manager or Admin): manufacturing's page pattern — month filter
(`app-month-input`), the choices in words, the lead tile and counts, the posting panel (post and reverse under
`accounting.manage`, confirmed first), unmapped slots in a warning, the checks with `app-status` pills, the entries and the
accounts tables (`.ui-table--cards`), then the documents posted at issue, open customer jobs and customer jobs' invoices, and the
notes. Both practice ledgers carry a Production / Maintenance tab strip (`practice-ledger-tabs.component.ts`, two addresses)
shown to a reader who may open both. The journal's source list gains `manufacturing`, `maintenance` and `maintenance-issue`.
Translations fr/ar/en in `core/i18n/translations-maintenance-ledger.ts`.

### Migration and tests

Migration `MaintenanceMonthPostings` in both sets: one table of `ManufacturingMonthPostings`' shape, unique (Year, Month,
Generation); both sets report no pending model changes. No settings column: MS-30, MS-33, MS-36, MS-39 and MS-40 were built in M0.
Tests: `ParadiERP.Tests/Maintenance/MaintenanceLedgerTests.cs` (7) — the builder: every entry balances, parts tie to their
stock, each option adds only its entries (labour and overhead moved, work in progress monthly and at year end, the invoice
tie-out); a month on read with unmapped slots shown and the ledger identical before and after, the readers and the module off;
the account map's slots seeded empty, mapped under their own route, manufacturing's list unchanged; a month posted balanced,
refused twice (posted already, before the first month) and for a branch-held caller, the later month blocking a reversal,
reversed by mirror entries and posted again under generation 2, manufacturing's postings untouched; labour moved (6 h at 450 =
2,700) refused without pay-rate access, shown with it, read without it at the workshop rate, gone under "report only"; an open
customer job carried in work in progress and reversed on the 1st, its invoice tied out, and listed under the default; MS-32 at
issue refused until own repairs is mapped, then one balanced entry per document out of the practice ledger, and a document
waiting there once a slot is cleared. Security pins: 349 company routes; the legacy snapshot gives the five routes to the Manager
and the Admin, like Costing settings. Angular: `maintenance-practice-ledger.component.spec.ts` (4), the account map in
`maintenance-settings.component.spec.ts`, the page in `nav-keys.spec.ts`, the sources in `accounting-sources.spec.ts`.

**Not built in M6:** capitalisation (MS-30, after REN-12) and its slot; the guarantee provision (MS-37, M7); customer jobs
themselves (M5). For the accountant: labour moved into jobs is valued at the rates known when the month is read, as production's
practice ledger is, so a month posted before its payroll is approved carries the standard rate and the approval's difference is
not re-posted for that month.

---

## M5a — as built (2026-11-09), with M5's company profit report

Phase M5a ships customer jobs, their price and their one invoice, and the MS-23 repair rates; once M4 merged, the M5
company profit report was added on the same branch. With MS-01 off every route of it answers 409 `maintenance_off` (the
return-note screen's suggestions answer "not enabled" instead, see below); with MS-06 off a customer job is refused.

### The customer job (MS-06)

The Customer kind is the same `MaintenanceJob` row, with these differences:

| | Internal / Recharged (M1) | Customer (M5a) |
|---|---|---|
| Opened by | `maintenance.jobs.manage` | the same, only while MS-06 is On (400 `maintenance_option_not_yet` otherwise) |
| What it names | a product in maintenance at the branch (internal) or a return-note line (recharged) | an existing customer (`CustomerId`, required), the equipment in words (`Equipment`, required, 200), optionally a catalogue product (`ProductId` only says what it is), and how many pieces (`Qty`, default 1) |
| Stock | holds units of the branch's maintenance bucket; completion moves them to available / scrap through the state route | **takes nothing**: the equipment is the customer's, not company stock. It is never counted in the bucket a job may take (`BucketAsync`, Waiting for repair), and completing it moves no stock and posts nothing (repaired / beyond repair are recorded, "back to the customer") |
| Parts, outside work, hours | as M2/M3 | the same: company parts issued to it (MJI-, kind 20), outside work, hours under the one ceiling |
| Revenue | none / the recharge share (M4, MS-26) | its own invoice |

`MaintenanceJob.ProductId` became nullable for this (SQLite rebuilds the table; SQL Server drops the FK and both indexes
over the column, alters it to `NULL`, and recreates them as they were; every existing row keeps its product). M4's
cumulative cost per equipment type leaves customer jobs out (outside equipment is no equipment type of the company's), and
its recharge report reads only recharged jobs, so neither changes.

### The price (MS-20 to MS-25)

`Application/Maintenance/MaintenancePricing.cs`, pure. Every amount is before tax; money is rounded through the regional
rule once per figure, so the pieces add up to the proposal exactly.

| Basis (MS-20) | Proposal | Worked example (tests) |
|---|---|---|
| Quoted (default) | none: the price is typed | 25,000 typed, kept |
| Itemised | parts used (issued − returned) × the product's sale price before tax (`Product.Price`, netted of its own rate only where the country profile stores prices tax-inclusive — not in the Algeria preset) + hours × MS-21 + outside work at cost (as M4 counts it, net of the C-07/MS-13 recoverable VAT) + MS-22 % of the outside work | 4 × 1,500 + 3 h × 1,200 + 4,000 + 10 % × 4,000 = **14,000** |
| Cost-plus | the job's cost C + MS-22 % of it. C is **M4's own figure** (`MaintenanceCosting.FigureAsync`, the adapter over the cost engine), not a seam or a stub | 4 × 800 + 4,000 = 7,200, + 25 % = **9,000** |

`PUT /jobs/{id}/price` (`maintenance.pricing`) saves the price before tax (> 0), the basis in force, the proposal and the
cost at that moment, who and when, and the VAT choice (null leaves it). An invoiced or cancelled job's price no longer
changes (409 `not_priceable`).

**MS-25, never refused.** `ShownAsLoss` (default): the price is saved and the card says the loss. `NeedsConfirmation`: a
price below the cost answers 409 `price_below_cost` until the request carries `ConfirmBelowCost` (a pricing holder's
tick); who confirmed and when are kept. The invoice checks again: if the cost has grown past the cost the confirmation was
given at, it waits for a new confirmation. With no cost available nothing is compared.

**Privacy (§6.2, M4's rule).** The cost, the loss amount and the cost-plus proposal (which gives the cost back by a
division) are given only to a `maintenance.costs.view` holder whose job's labour money is shown (`MaintenanceCosting.MoneyShown`).
Anyone else sees the itemised pieces, the price, and — so MS-25 can work — that the price is below the cost, never by how
much. The saved price is visible to every reader, as §6.2 allows.

### The invoice

`POST /jobs/{id}/invoice` (`finance.invoices.create`; the service also asks `maintenance.view`, 403
`maintenance_view_required`). Only a **completed customer** job (409 `not_customer_job`, `not_completed`); **once**: a
second attempt answers 409 `already_invoiced`, and the writer itself refuses a second invoice with the same source
(`ReceivableDocuments.CreateMaintenanceJobInvoiceAsync`), and `MaintenanceJobs.InvoiceId` is unique. The price is the saved
one, or — none saved — the basis's proposal frozen at that moment (Quoted with no price: 409 `price_required`).

Through the **existing writer**: `CreateInvoiceAsync`'s issuing half was extracted unchanged into `IssueAsync` (numbering,
payment words, tax on each rate's own total, the frozen buyer block, the posting, the customer's balance under the version
lock); the new entry builds the invoice and calls it inside the job writer's transaction, so the invoice and
`Status = Invoiced` commit together. `InvoiceOrigin.MaintenanceJob = 3`, `SourceType = "maintenance-job"`
(`InvoiceSources.MaintenanceJob`), `SourceId` = the job. `ReceivablePosting.PostsItself` is Direct or MaintenanceJob (the
job posted nothing, so its invoice is the economic event): **Dr 411 the gross / Cr the income account the net / Cr 4457 the
tax**, under source `invoice`, through `ReceivablePosting.Invoice` unchanged otherwise. No goods leave stock (the parts left
with the job's MJI documents; the P0.3 stock switch applies only to Direct). One service line: `MJ-…`, the job number, the
equipment and item reference in words, quantity 1, no product.

- **Income (MS-34).** `SameAsRecharges` (default) — read from the return note's own rule,
  `RentalPosting.AccountFor(RentalChargeKind.Maintenance)` = **707**. `OneServiceAccount` and `PartsAndLabourApart` need an
  **income** account the accountant names. M6's maintenance slots were checked for it and none fits: `own_parts`,
  `customer_parts`, `labour_absorbed`, `overhead_absorbed`, `service_cost` and `wip` are all cost or stock accounts, and
  crediting a customer's revenue to one would net it against cost. M5a refused them (400 `maintenance_option_not_yet`).
  **Built since (10 Nov 2026)** on a new income slot, `maintenance.service_income`: see "MS-34 — as built" below.
- **VAT (owner, 2026-11-05).** Every job starts `TaxMode = Untaxed` (the migration gives existing rows 1 = Untaxed). The
  seller ticks it on the Price card or in the invoice dialog; then the line carries the country profile's
  `DefaultTaxRate` (19 % in the Algeria preset) added on top of the price before tax: 20,000 → 3,800 VAT → 23,800.
  Untaxed, no rate is stored on the line and no 4457 line is posted.
- The invoice page treats this origin as posted ("Maintenance job invoice: posted to the customer account when raised");
  the printed invoice names "Maintenance job MJ-…". Payment is an ordinary receipt voucher; a credit note gives the money
  back on each line's own account (707 by default) as on any invoice.

### MS-23 repair rates

`MaintenanceRepairRates` (one row per product: `RatePerUnit` before tax, who and when; unique on the product). Kept on
Maintenance › Repair rates (`maintenance.pricing`). While the module is on **and** MS-23 is `SuggestedFromRate`,
`GET /api/maintenance/repair-rates/suggestions` (outside the module filter, `rentals.return` or `maintenance.view`)
returns every rate; otherwise `enabled: false` and nothing — never an error, so the return-note screen is untouched while
it is off. The return-note screen shows "Suggested: 2 × 2,000 = 4,000" beside the typed recharge with a "Use it" button;
the recharge stays what the clerk types. **The rental engine, its rules, the return-note writer and its postings never
read the table**: a test runs the same return in a default company and in one with MS-23 on and a rate, and finds the same
charges and the same ledger, and a return with nothing typed charges no repair.

### Company profit (M5, plan §3.3)

`GET /api/maintenance/reports/profit?from&to&branchId` (`maintenance.costs.view`, branch-scoped, 409 module off, 400
reversed period), `MaintenanceCosting.ProfitAsync` (a partial of M4's class, reusing its cost, recharge and privacy code).
Every job but drafts, opened in the period. **Company profit = external revenue − C**, no internal charge (owner,
2026-11-09):

- external revenue: a customer job's invoice net before tax less the net of its credit notes (`Invoice`; 0 and
  `NotInvoiced` until invoiced); a recharged job's share of the return-note recharge as M4 computes it (MS-26); an internal
  job none (`None`: its cost is upkeep of own equipment);
- C: M4's cost of the job.

Per job, and by month (of opening, company time), branch, kind and product (customer jobs naming no product form their own
line). REN-11: 16,000 − 11,000 = **5,000**; a customer job invoiced 20,000 (taxed: the 3,800 VAT is not revenue) against
7,200 = **12,800**; an internal job of 1,000 = **−1,000**. **Privacy, M4's rule exactly:** a job's labour, cost and profit are
withheld when its labour money is (no pay-rate access, labour at payroll rates); a breakdown line's cost and profit only
from two jobs with labour and two workers (`CostPrivacy.LineSumShown`); the totals only when the whole passes the same rule
and no breakdown line is withheld (the totals less the lines shown would give a withheld line back); everything under the
MS-12 workshop rate or with labour out of the cost. Revenue is never withheld. Residual, not handled: sums of two
*different* breakdowns that are each shown could in principle be differenced against each other; the same holds between
M4's two reports.

### Routes (nine; security pins 358 company routes, M6's five included)

| Route | Key |
|---|---|
| `GET /api/maintenance/new-job-options` | `maintenance.jobs.manage` |
| `GET /api/maintenance/jobs/{id}/price` | `maintenance.view` |
| `PUT /api/maintenance/jobs/{id}/price` | `maintenance.pricing` |
| `POST /api/maintenance/jobs/{id}/invoice` | `finance.invoices.create` (+ `maintenance.view` in the service) |
| `GET /api/maintenance/repair-rates` | `maintenance.view` or `maintenance.pricing` |
| `PUT /api/maintenance/repair-rates/{productId}` | `maintenance.pricing` |
| `DELETE /api/maintenance/repair-rates/{productId}` | `maintenance.pricing` |
| `GET /api/maintenance/repair-rates/suggestions` | `rentals.return` or `maintenance.view` (no module filter) |
| `GET /api/maintenance/reports/profit` | `maintenance.costs.view` |

`[FromServices]` throughout (`MaintenanceEndpoints.Customer.cs`); the job routes carry `ForBranchOf` (another branch's job
404), the report `BranchScoped`. The legacy snapshot gives the invoice and suggestions routes to the Manager and the Admin
(their keys' legacy holders) and the other seven to the Admin only.

### Screens

New job offers the Customer kind while MS-06 is on (customer, equipment in words, optional product, pieces; opened at the
user's own branch). The Job page shows the customer and equipment, "the customer's equipment: no company stock" in the
Units card, the equipment editable in the words, and a **Price** card (`maintenance-job-price.component.ts`): the basis and
its pieces, the proposal, the price before tax with "Take the proposal", the VAT tick, the loss and the MS-25 confirmation
in words, and "Raise the invoice" once completed (or "Print the invoice" once raised). The Jobs list shows a customer job
by its equipment and customer, and its ⋯ menu offers "Raise the invoice" on a completed one (the same
`maintenance-invoice-dialog`: due date with `app-date-input`, VAT tick off by default). Maintenance › Repair rates
(`maintenance/repair-rates`, sidebar while MS-01 is on, `maintenance.pricing`). Maintenance › Reports gains "Company
profit". Settings marked MS-34's two other options "later phase" (offered since 10 Nov 2026, MS-34 — as built). Translations fr/ar/en in
`core/i18n/translations-maintenance-customer.ts`.

### Migration and tests

`MaintenanceCustomerJobs` in both sets: `MaintenanceJobs.ProductId` nullable; the columns `CustomerId` (FK, indexed),
`Equipment`, `PriceBasis`, `Price`, `ProposedPrice`, `CostAtPricing`, `PricedByUserId`/`PricedBy`/`PricedAtUtc`,
`BelowCostConfirmedBy`/`At`, `TaxMode` (default 1, untaxed), `InvoiceId` (FK, unique, filtered on SQL Server),
`InvoicedBy`/`At`; the table `MaintenanceRepairRates`. Both sets report no pending model changes. `MaintenanceJobEventKind`
gains `Priced = 20`, `Invoiced = 21`. Tests: `ParadiERP.Tests/Maintenance/MaintenanceCustomerJobTests.cs` (12) — MS-06,
nothing taken from stock and nothing posted at completion; each basis on its worked example; MS-25 both ways (and the
confirmation asked again after the cost grew); the invoice posting 411 / 707 untaxed with no 4457 and no cost of goods,
VAT on request (23,800 / 3,800), once per job (the job and the writer both refuse), the job's number as the invoice's source
number; only a customer job, a price first, MS-34's other options refused; the routes (409 module off, 404 another branch,
403 without `maintenance.view`, the suggestions "not enabled"); repair rates and the same return-note charges and ledger
with and without them; company profit (REN-11 5,000, 12,800, −1,000, the breakdowns, a credit note giving revenue back) and
its privacy (withheld row and totals, shown again from two jobs and two workers, everything under the workshop rate). The
settings theory gains MS-34. Angular: `maintenance-customer.spec.ts` (Price card, list menu, repair rates, return-note
suggestion), the New job customer spec, the Reports profit spec, `nav-keys.spec.ts`.

**M6 together.** M6's practice ledger finds a customer job's invoice through `Invoices.SourceType = "maintenance-job"` and
`SourceId` = the job — exactly what this writer stores (its constant now reads `InvoiceSources.MaintenanceJob`); the entry
itself posts under source `invoice` with the invoice's id, as every invoice. A customer job counts as open until completed,
invoiced or cancelled; parts issued to it take `maintenance.customer_parts`. A test raises a real job invoice and reads the
practice ledger: the invoice listed, matched to its job, `invoices_tie` passing, the job not open.

**Not built in M5a:** an invoice line per part and per labour hour, and one service income account (MS-34's other options:
no income slot; built 10 Nov 2026 as one parts line and one labour line, not a line per part, see MS-34 — as built), advances (MS-35, M7),
reworks under guarantee (MS-37), the Excel export of the reports, a branch picker for an Admin's customer job (it opens at
the Admin's own branch).

---

## M7c — as built (2026-11-09): rework under guarantee and the guarantee provision (MS-27, MS-37, MNT-09)

Phase M7c links a free rework to the customer job it redoes, follows MS-37 in the company profit report, and builds the
third MS-37 option, the guarantee provision, in the practice ledger. The owner's standing rule holds (2026-11-09): every
choice is a setting, and the defaults keep today's behaviour. MS-37 stays "added to the original job in reports, an expense
in the books". Nothing posts until the accountant chooses the provision and posts a month.

### What M1–M5 had

M1–M5 built nothing for rework. The plan's `ReworkOfJobId` (§2.2) was listed under "not built", and nothing linked one
job to another. M7c adds the link.

### The rework link

| | As built |
|---|---|
| Field | `MaintenanceJob.ReworkOfJobId` (FK to `MaintenanceJobs`, restricted, indexed) |
| Who may be one | A **customer** job (MS-06 on), opened with `ReworkOfJobId` in `POST /api/maintenance/jobs` |
| What it may name | A customer job of the **same customer**, at the **same branch** (another branch's job answers 404, as every job named by id), **completed or invoiced**, **not itself a rework** (name the job it redoes), opened while its **guarantee runs** |
| Refusals (409) | `rework_not_customer_job`, `rework_other_customer`, `rework_not_completed`, `rework_of_rework`, `outside_guarantee` |
| Defaults | The equipment, product and item reference are taken from the original job unless given |
| Free of charge | Never priced (`PUT /price`) and never invoiced (`POST /invoice`): 409 `rework_free`. It stays Completed, never Invoiced |

**The guarantee (MS-27, new).** `MaintenanceSettings.GuaranteeMonths` is an operational setting
(`maintenance.settings`), 0 to 60 months. Its default is **3**, and the migration gives an existing row 3. At completion of
a customer job that is not a rework, `MaintenanceJob.GuaranteeUntil` is frozen. It equals the completion day plus the
months, on the company's calendar (time zone from the regional settings). A rework may be opened until that day,
inclusive. A later change of MS-27 never moves a guarantee already given. With 0 months a job carries no guarantee and
takes no rework. A job completed before M7c has no frozen date: its guarantee is read as its completion day plus the
months in force, and it is frozen at its invoice. `GET /api/maintenance/rework-candidates?customerId=&branchId=`
(`maintenance.jobs.manage`, branch-scoped) lists the customer's jobs whose guarantee still runs, for the New job form.

### MS-37 in the company profit report (`MaintenanceCosting.ProfitAsync`)

| MS-37 | The report |
|---|---|
| `AddedToOriginalJob` (default) | A rework has no line of its own. Its parts, outside work, overhead, hours, labour and cost join the line of the job it redoes, whenever the rework falls, so that job's profit is its real result (MNT-09 A). The line says `Reworks` (count) and `ReworkCost`. A rework whose original was opened outside the period is not in this period's report (it belongs to its original's). Privacy: the line is withheld when any of its jobs' labour money is (M4's rule over the union of jobs and workers). |
| `OwnJob` | Each rework is its own line, revenue source `Rework`, revenue 0, profit = −cost, naming the job it redoes (`ReworkOfNumber`). |
| `Provision` | The same lines as `OwnJob`. In the books the rework's cost is taken from the provision (below); the report shows the work as it was done. |

The totals are the same under every option. Example (test): an invoice of 20,000 against 800, and a rework of 1,600. The
default shows one line at 2,400 and a profit of 17,600. The other options show 800 / 19,200 and −1,600. Revenue is 20,000
and cost 2,400 in all three. M4's recharge and per-product reports read no customer job, so they do not change.

### The guarantee provision (MS-37 `Provision`)

**Settings.** `MaintenanceSettings.ProvisionPercent` (MS-37, `accounting.manage`, covered by the MS-40 confirmation) is
0 to 100 %. It is kept only under `Provision`, where it must be above 0, and saved as 0 otherwise. Saving `Provision` is
refused in three cases:

- 400 when the rate is 0;
- 400 when MS-27 gives no guarantee;
- 409 `accounts_not_mapped` while either new slot names no account.

While `Provision` is on, the account map refuses to clear either slot. The "later phase" refusal of M0 is gone.

**Account map slots (MS-31).** Two slots are appended to `MaintenanceAccountSlots.All`: `maintenance.provision_expense`
(the charge) and `maintenance.provision_liability` (the provision). They are seeded empty, with no hint, and never
guessed.

**Frozen at the invoice.** When a customer job that is not a rework is invoiced under `Provision`, with a rate above 0 and
its guarantee still running on the invoice's day, the job freezes `ProvisionPercent` and `ProvisionAmount`. The amount is
the invoice net before tax (`SubTotalHt`) × the rate, rounded as money. Everything after reads these two frozen figures.
A provision set aside is used and released to the end even if MS-37 changes later. A job invoiced under another treatment
never gets one.

**Entries** (`Application/Maintenance/MaintenanceGuaranteeProvision.cs`, pure; added to the maintenance section by
`MaintenancePracticeLedger.Build`):

| Event | Entry | Day |
|---|---|---|
| Booked (`provision_booked`) | Dr `provision_expense` / Cr `provision_liability`, the frozen amount | the invoice's day, in its month |
| Used by a rework (`provision_used`) | Dr `provision_liability` / Cr the accounts the rework's costs debited. Parts go back to `maintenance.customer_parts` (as the practice ledger or MS-32 "at issue" charged them). Outside work goes back to the expense account its payment voucher debited (`account:<code>`, e.g. 615), counted once the voucher is **paid**, net of the C-07/MS-13 recoverable VAT as the job's cost counts it. Labour and overhead go back to `maintenance.service_cost`, only under MS-33 "moved into jobs". | the month's last day, or the guarantee's last day in the month it ends |
| Released (`provision_released`) | Dr `provision_liability` / Cr `provision_expense`: what is left when the guarantee ends (the charge reversed) | the guarantee's last day |

**How "used" is measured.** Used to date is the smaller of two figures: the provision booked, and what the job's reworks
had booked to those accounts. Their bookings count from the first day of the invoice's month up to the end of the
guarantee day. Costs before the invoice's month, and costs after the guarantee, stay in expense. Each month moves the
difference between the month's end and its start, so a month read twice gives the same figures and the months add up.
The amount used is shared out in a fixed order: the reworks by number, and within one rework parts first, then outside
work, then labour and overhead. Whatever exceeds the provision stays in expense. If a rework returns parts, the provision
gives back what it no longer covers of them, and that moves to the costs that were waiting.

Example (test): an invoice of 20,000 at 10 % sets aside 2,000 on 10 August. In September a rework uses a bearing (800)
and a welder's 500 on a paid repair voucher. That month's entry is Dr provision 1,300 / Cr customer parts 800 / Cr 615
500. The guarantee ends on 15 October, and October releases 700 on that day. A bearing issued on 20 October, after the
guarantee, stays in expense. After posting August to October, the provision account is at 0, the charge at 1,300 and 615
is credited 500. With the provision at 1,000, the first rework's parts (800) and 200 of its outside work are taken; its
labour and a second rework stay in expense.

**Check** `provision_tie`: the liability's movement equals booked − used − released. It is listed when the month moves a
provision. `accounts_mapped` covers the two slots and the voucher account like any other.

**Posting.** Month posting goes through M6's poster (`MaintenanceLedgerPostings` → `LedgerMonthPoster`, source
`maintenance`), unchanged. A provision whose invoice falls before the first month to post (MS-39) is left out whole. It
never posted, so none of it is used or released. The month's `provisions` list in the practice ledger DTO says, per
provision, the amount and the month's booked, used and released figures, what is held at the month's end, and the
guarantee's last day.

**Free rework and MS-36.** A rework is never invoiced, so it is not carried in work in progress and not listed among open
customer jobs to bill. Its cost stays in expense, or is taken from the provision.

### Screens

- **Settings.** The Module and jobs fold gains the guarantee (MS-27, months). The Accounting fold offers MS-37
  "provision" (no longer "later phase") and, under it, the rate. The account map lists the two new slots.
- **New job.** For a customer, a "Rework under guarantee of job" select lists the customer's jobs whose guarantee runs,
  with their equipment and last day. Choosing one takes its equipment and says "free: no price, no invoice".
- **Job page.** A rework links the job it redoes, and a notice replaces the Price card. A customer job shows its
  guarantee's last day, the provision set aside and its reworks.
- **Jobs list.** A rework says so, and its ⋯ menu offers no invoice.
- **Reports › Company profit.** The page says the MS-37 treatment in words. An original's line says "of which N
  rework(s): cost", and a rework's line says "rework of job MJ-…".
- **Practice ledger.** The three entry kinds, the voucher account in words, the `provision_tie` check, a "Guarantee
  provisions of the month" table, and a note when MS-33 leaves a rework's hours in wages.

Translations fr/ar/en are in `core/i18n/translations-maintenance-rework.ts`. The `accounts_not_mapped` sentence now
names both MS-32 and MS-37.

### Migration and tests

Migration `MaintenanceRework` is in both sets:

- `MaintenanceSettings.GuaranteeMonths` (int, existing row 3) and `ProvisionPercent` (decimal 18,3, 0);
- `MaintenanceJobs.ReworkOfJobId` (FK self, restricted, indexed), `GuaranteeUntil` (date), `ProvisionPercent` and
  `ProvisionAmount` (decimal 18,3, null).

The model has no store default for `GuaranteeMonths`, so that 0 saves as 0. The 3 is in the migrations only. Both sets
report no pending model changes. SQLite applies the self-FK by EF's table rebuild.

One route is added, `GET /api/maintenance/rework-candidates`: `maintenance.jobs.manage`, the Admin only in the legacy
snapshot. The security pins move to **359**. `MaintenanceJobs` now takes `IRegionalSettingsProvider` for the company
calendar.

`ParadiERP.Tests/Maintenance/MaintenanceReworkTests.cs` covers:

- the link: within the guarantee; refused before completion, for another customer, for an internal job, for a rework of a
  rework, at another branch (404), and after the guarantee; candidates; never priced or invoiced;
- no guarantee with MS-27 at 0;
- each MS-37 reporting mode (theory, the same totals);
- the provision refused without its two accounts, a rate or a guarantee, the rate the accountant's and the months the
  workshop's, the slots kept while on, and the default setting nothing aside;
- the provision booked, used, released and posted August to October, balanced, the provision back to 0;
- the pure allocation: cap, order, excess in expense, parts returned, booked and released in one month.

The settings tests were updated: defaults 3 / 0, ranges, the provision no longer "later".

Angular: specs for the settings (MS-27, MS-37 rate, the provision selectable), New job (rework candidates), Job (rework
and original), Reports (both modes) and Practice ledger (provision entries and table).

**Not built in M7c:**

- a separate income account for the release (SCF "reprise sur provisions", 78x): the release reverses the charge, as the
  brief asked;
- labour under MS-33 "report only" taken from the provision (it stays in 631/635);
- credit notes reducing a provision already frozen;
- any change of a posted month when MS-37 or MS-27 change later (frozen figures make that unnecessary).

---

## M7d — as built (2026-11-09): capitalised repairs (MS-30, MNT-01) and per-piece history (MS-38, MNT-10)

Both options were refused as "later phase" in M0, waiting for the REN-12 asset register. M7a built the register
([../accounting/fixed-assets.md](../accounting/fixed-assets.md)), so M7d builds them. The owner's standing rule holds
(2026-11-09): each is a setting the accountant sets, and the defaults — "always an expense" and "per equipment type with an
optional reference" — keep today's behaviour exactly (a test completes a repair above any amount with the register active and
finds no register movement, no capitalisation line, no event and the practice ledger M6 built).

### MS-30 "capitalised above an amount"

**Settings.** `MaintenanceSettings.CapitaliseAboveAmount` (accounting.manage, covered by the MS-40 confirmation), 0 to
100,000,000, kept only under the option and then above 0. Saving the option is refused:

- 400 when the amount is 0;
- 409 `register_not_active` while the register does not hold the hire equipment (FixedAssets chosen **and** the switch-over
  posted, `FixedAssetData.Active`);
- 409 `posting_off` while MS-39 names no first month: the entry posts with the maintenance practice ledger, and a month that
  never posts would leave the register carrying a cost the books do not;
- 409 `accounts_not_mapped` while `fixed_assets.asset` or `maintenance.own_parts` names no account, or — labour being in a
  job's cost (MS-10) — the new slot `maintenance.capitalised`.

While it is on, the account map refuses to clear `maintenance.own_parts` or `maintenance.capitalised` (the register's own
store already keeps `fixed_assets.asset`). The settings DTO says whether the register is active (`assetRegisterActive`), and
the page warns in words when it is not.

**New slot (MS-31):** `maintenance.capitalised` — own work capitalised, the credit for labour and overhead the books carry
outside maintenance's slots. Seeded empty, no hint (SCF "production immobilisée", 73x, is the usual answer; never guessed).

**When a repair is capitalised** — at completion (`MaintenanceCapitalisation.AtCompletionAsync`, called by the job writer in
its transaction, after the units moved, so units found beyond repair have left the pool and carry none of the repair). All of:

1. MS-30 says so, with an amount above 0;
2. an **internal or recharged** job (a customer job's equipment is never the company's: "kept an expense");
3. units repaired (a job that only scrapped units improved nothing);
4. the register holds the product (a hire product, register active) **and** its pool at the job's branch holds units;
5. the job completes in or after MS-39's first month;
6. **C > the amount**, C being M4's cost of the job at completion (`MaintenanceCosting`, the adapter over the cost engine).

Otherwise the repair stays an expense, exactly as today; with MS-30 on, the job's history says why ("kept an expense: cost
3800 not above 3800"). The event kind is `Capitalisation` (40).

**What the pool receives, element by element.** A = the sum of the lines below, frozen at completion in
`MaintenanceJobCapitalisedLines` (element, slot, amount). Nothing is counted twice: every amount an element's own document
debited is credited back once, and the asset receives exactly the sum.

| Element | How the books carry it already | Capitalised line (credit) |
|---|---|---|
| Parts, per issue document (a return negative), at the values frozen on its lines, rounded once per stock group as the practice ledger rounds them | Posted at issue (MS-32), or posted by the practice ledger of its month: Dr `maintenance.own_parts` / Cr the part's stock | **Cr `maintenance.own_parts`** — the expense is taken back |
| Parts of a document dated before MS-39's first month (never posted by maintenance, still in the stock account in the books) | Not at all: the part never left stock in the books | **Cr the part's stock slot** (supplies, raw material… or 310 for merchandise) — the asset takes it straight from stock |
| Labour and overhead (when MS-10 includes them), MS-33 "moved into jobs", hours of months on or after MS-39's first | The practice ledger moved them Dr `maintenance.own_parts` / Cr labour (overhead) absorbed | **Cr `maintenance.own_parts`** |
| Labour and overhead under MS-33 "report only", and hours before MS-39's first month | In the wage charges (631/635); overhead nowhere | **Cr `maintenance.capitalised`** (own work capitalised) |
| Outside work whose payment voucher is **paid** by completion | The voucher debited its expense account (category repair → 615) | **Cr that account** (`account:<code>`), net of the recoverable VAT as the job's cost counts it (C-07 / MS-13) |
| Outside work not yet paid, or with no voucher | Nothing yet | **Not capitalised**: it stays an expense when its voucher posts |

The entry is one more kind of the maintenance practice ledger, **`capitalised_repair`**, dated the completion day in the
completion month: **Dr `fixed_assets.asset` A / Cr each line** (a negative line turns to the debit). It posts with that month
through M6's month poster (source `maintenance`), unchanged; it is read whatever MS-30 says later (a repair once capitalised is
in the register). Two checks: `parts_tie` counts the stock credits of pre-start parts with the month's documents, and the new
**`capitalised_tie`** — the asset debited by the month's capitalised repairs equals what the register added to the pools.
Labour in a capitalisation, valued at payroll rates, gives a pay rate back as moved labour does, so such a month needs pay-rate
access to read (403 `payroll_details_withheld`), as M6.

Worked example (pinned in `MaintenanceCapitalisationTests`): two scaffolds repaired; 5 rods (200, supplies) issued two months
before MS-39's first month, 10 bolts (100, merchandise) this month, 6 h at a workshop rate of 450, 500 of outside work on a
paid voucher (account 6181) and 300 with none. C = 3,800 > 1,000. Lines: supplies 200, own repairs 100, own work capitalised
2,700, 6181 500 → **A = 3,500**; the 300 stays an expense. Own repairs nets to 0 (Dr 100 at the bolts' issue, Cr 100 here).
Posted: 215 +3,500, 731 −2,700, 6181 −500, 322 −200, 310 −100, 615 0.

**In the register** (`FixedAssetMovementKind.CapitalisedRepair = 9`): one movement per capitalised job — the pool (product,
job's branch), Qty = the repaired units, Cost = A, `StartMonth` = the month after completion, the job's number and its piece
reference (`Reference`). The engine adds it as a **layer of cost only**: it carries cost and is depreciated like a purchase
(the type's useful life, from the month after) but adds **no unit** to the pool (`FixedAssetPool.Qty` counts unit layers only),
and leaves pro rata with any disposal or transfer (q/Q of every layer, the repair's included; it goes whole with the last
unit). In the example the pool goes from 132,000 to 135,500 for the same 110 units; the completion month is charged 2,200 and
the next 2,258.333 (+3,500 / 60). The movement posts no entry of its own: its entry is the practice ledger's, and the pool's
history shows it "posted" once a live maintenance posting covers its month.

**Reports.** M4's cumulative cost per equipment type gives, per type and in total, **capitalised** (the frozen A) and
**expensed** (cost − capitalised), under the same privacy rule as the total; the job's Cost card says in words when and how much
was capitalised (the amount withheld with the total); the register's pool history lists "Capitalised repair" with the job and
the piece.

### MS-38 "per piece above a value"

**Settings.** `MaintenanceSettings.PerPieceAboveValue` (accounting.manage, covered by MS-40), kept only under the option and
then above 0. No register is needed: the history is maintenance's own. A type is **tracked per piece** when its unit value —
the product's asset value (`Product.AssetUnitValue`) when it has one, its cost price otherwise — is above the value.

**The piece.** Hire equipment is counted in quantities, so a piece is the reference a job names. M1 already carries it:
`MaintenanceJob.ItemReference` (120, optional, editable while the job is live) — the job's one unit line. Under "per piece", an
internal or recharged job on a tracked type names its piece and holds one: opening it by hand without a reference answers 400
`piece_reference_required`, with more than one unit 400 `one_piece_per_job`; a draft a return note opened (MS-03) is not
refused, but completing it is (409, the same codes) until it names one piece — cancel it and open one job per piece. Customer
jobs and the default are never refused. A capitalised repair carries the piece into the register's movement.

**The history** (`GET /api/maintenance/history?productId=&reference=&branchId=`, `maintenance.view`, branch-scoped like the
reports): every job but drafts and customer jobs on the type at the reader's branches, or on one piece (trimmed, case ignored),
with its number, kind, status, branch, reference, units (repaired, scrapped), opened and completed dates, parts (each product's
net quantity and value), outside work and hours — and, to a `maintenance.costs.view` holder, its cost and its capitalised part
under M4's privacy rule (withheld when its labour money is; totals from two jobs with labour and two workers). The type's named
pieces are listed with their job counts. The same route serves both MS-38 options; "per piece" only adds the rule above and says
the type is tracked.

**Screens.** Maintenance › Repair history (`maintenance/history?product=&reference=`, sidebar while MS-01 is on, to
`maintenance.view`): the type and the piece are the address; a notice says in words whether the type is tracked per piece;
metric tiles; the named pieces (each opens its own history); the jobs. Maintenance › Reports' per-type table links each type to
its history and says "of which capitalised …, expensed …". Settings: MS-30 and MS-38 selectable (no longer "later phase"),
with their amount and value under them; the account map lists `maintenance.capitalised`. Practice ledger: the entry kind, the
asset slot by the register's name, the check. Translations fr/ar/en in `core/i18n/translations-maintenance-capitalised.ts`.

### Migration, routes and tests

Migration `CapitalisedRepairs` in both sets: `MaintenanceSettings.CapitaliseAboveAmount`, `PerPieceAboveValue` (decimal 18,3,
0); `MaintenanceJobs.CapitalisedAtUtc` (indexed), `CapitalisedAmount`, `CapitalisedMovementId`; the table
`MaintenanceJobCapitalisedLines` (FK to the job, restricted, indexed). Both sets report no pending model changes. One route,
`GET /api/maintenance/history` (the Admin only in the legacy snapshot); security pins **370**.

`ParadiERP.Tests/Maintenance/MaintenanceCapitalisationTests.cs` (6): the engine's cost-only layer (no unit, depreciated from the
month after, leaves pro rata and whole with the last unit); the default unchanged; capitalisation above the amount (the lines
per element, the movement, the pool and its depreciation, the cost card and the per-type report, the practice ledger balanced
with own repairs at zero and every check passing, the month posted with each account's net, the pool's history); at or below
the amount, a customer job and a job that only scrapped units stay expenses; the settings refused without the register, MS-39
or the slots, the amount the accountant's, the slots kept while on; per piece (reference and one piece required, the default not,
the history of a piece and of a type, costs hidden from a reader without `maintenance.costs.view`, a several-piece draft refused
at completion). The settings tests drop MS-30 and MS-38 from "later" and add their ranges. Angular: the settings spec (both
selectable, the figures under their options, the register warning), `maintenance-history.component.spec.ts` (3), the Cost card
and Reports specs, `nav-keys.spec.ts`.

**Not built in M7d, for the accountant:**

- a capitalised repair is depreciated over the type's full useful life from the month after, like a purchase — not over the
  remaining life of the units it improved; a different life for repairs would be one more setting (and a column on the
  movement);
- labour moved into jobs (MS-33) is valued in the practice ledger at the rates known when each month is read, while the
  capitalised labour is frozen at completion: a payroll approved after completion leaves the standard-to-actual difference in
  own repairs (an expense), as M6 already leaves it;
- outside work paid after completion is not capitalised (it is an expense when its voucher posts);
- MS-33 changed after a repair was capitalised under "report only" leaves the labour both moved into own repairs and credited to
  own work capitalised: the asset is right, the expense is shown under own repairs and offset in 73x;
- the register's pool history shows a capitalised repair's amount (parts + labour + outside work) to `reports.accounting`
  readers, as the posted journal does; it is not withheld by the pay-rate rule.

---

## MS-34 — as built (2026-11-10): customer repair income on its own account

The owner said "go" on MS-34's two other options. The standing rule holds: the accountant names the account in the panel,
nothing is guessed, and the default (`SameAsRecharges`, 707, read from `RentalPosting.AccountFor(RentalChargeKind.Maintenance)`)
is unchanged. No migration: the slot is a row of `ManufacturingAccountMappings`, and the enum values existed since M0.

### The income slot

`MaintenanceAccountSlots.ServiceIncome` = `maintenance.service_income`, appended to `MaintenanceAccountSlots.All` (so it
shows last in the account map under Maintenance › Settings, and in the practice ledger's account order). Seeded empty, no
hint. `MaintenanceAccountMap.UpdateAsync` takes for it only an **active income account of class 7**
(`MaintenanceAccountSlots.IsIncomeAccount`: `IsActive`, `Type = Income`, code starting with 7); anything else is 400.
While MS-34 is on one of the two options, the slot cannot be cleared (the MS-37 provision's pattern). The page offers
only class-7 income accounts in that slot's list. `MaintenanceAccountMap.ServiceIncomeAsync` reads the slot's account
and gives null when it is unmapped or its account has since been made inactive or retyped.

### Settings and refusals

`OneServiceAccount` and `PartsAndLabourApart` are no longer "later phase" (the `NotYet` refusal and the page's `later`
marks are gone). Saving either answers **409 `accounts_not_mapped`** while `ServiceIncomeAsync` is null. Raising a job's
invoice under either answers the same 409 if the slot has lost its account (a row edited behind the store, an account
made inactive): nothing is raised, the job stays Completed. MS-34 is in the accounting half (`accounting.manage`) and
covered by the MS-40 confirmation, as before.

### The invoice lines

| MS-34 | Lines (`MaintenanceCustomerJobs.InvoiceAsync`) | Entry (`ReceivablePosting.Invoice`, unchanged) |
|---|---|---|
| `SameAsRecharges` (default) | One line `MJ-… — equipment — reference`, the price, on 707 | Dr 411 / Cr 707 / Cr 4457 |
| `OneServiceAccount` | The same one line, on the slot's account | Dr 411 / Cr slot / Cr 4457 |
| `PartsAndLabourApart` | **Parts** = the parts the job used (its MJI issues less its MJR returns, per product) × the product's sale price before tax (`Product.Price`, netted of its own rate only where the profile stores prices tax-inclusive — the same figure as the itemised basis), rounded once, on **700** (`LedgerAccounts.Sales`, the account sales of goods and direct invoices use). **Labour and service** = the price − parts, on the slot's account | Dr 411 / Cr 700 / Cr slot / Cr 4457 |

`MaintenancePricing.SplitPartsAndLabour` (pure) makes the split. **Price below the parts:** when the parts at sale price
exceed the job's price, the parts line is capped at the price and the labour line is 0. **A line of 0 is left out**:
a job with no parts has only its labour line; a capped job only its parts line. The two lines always add up to the price.

**Wording.** The parts line reads `Pièces — MJ-… — equipment — reference`, the labour line `Main-d'œuvre et service — MJ-…
— …` (`MaintenanceCustomerJobs.PartsLineWords` / `LabourLineWords`), stored in French like the other words the server puts
on a document ("Location — contrat …"). The A4 invoice, the A4 credit note and the invoice page say them in the paper's or
reader's language through `invoiceLineName` (`maintenance-words.ts`) and the keys `print.line.maintenanceParts` and
`print.line.maintenanceLabour` (fr "Pièces — {n}" / "Main-d’œuvre et service — {n}", ar "قطع الغيار" / "اليد العاملة والخدمة",
en "Parts" / "Labour and service"). Any other line name is shown as stored.

**VAT.** Ticked, both lines carry the profile's default rate. The tax is computed as for every invoice, once on each
rate's own total (`DocumentTax.On`), so the totals are those of the single line exactly. Worked example (tests): 20,000
before tax with four bearings at 1,500 → parts 6,000 on 700, labour 14,000 on the slot; taxed 3,800 VAT, 23,800 to pay —
Dr 411 23,800 / Cr 700 6,000 / Cr slot 14,000 / Cr 4457 3,800. Rounding: one seal at 1.40 on a job of 99.80 would give
0.27 + 18.70 = 18.97 line by line; the invoice books 18.96 (19 % of 99.80 once), the same as one line.

**No stock moves.** The parts left stock with the job's MJI documents (and reach the books through the practice ledger or
MS-32 "at issue" on `maintenance.customer_parts`); the invoice moves none and posts no cost of goods, as before.

### What reads the invoice

- **Credit notes** inherit each line's `IncomeAccountCode` (unchanged writer): crediting the labour line gives back on the
  slot (Dr slot 14,000 / Dr 4457 2,660 / Cr 411 16,660), the rest on 700 (Dr 700 6,000 / Dr 4457 1,140 / Cr 411 7,140).
- **The practice ledger (M6)** lists the invoice by `SourceType`/`SourceId` and its `SubTotalHt`; `invoices_tie` passes
  with two lines and other accounts (test).
- **Company profit (M5)** reads the invoice's `SubTotalHt` less its credit notes': 20,000, then 0 once fully credited (test).
- The Price card's `IncomeAccountCode` and the invoice result's are the service account (the slot's, or 707); under
  "apart" the parts account 700 is on the parts line.

### Tests

`ParadiERP.Tests/Maintenance/MaintenanceCustomerIncomeTests.cs` (7): the slot seeded empty with no hint, refused for 615,
411 and an inactive 7x account, taken for an active 7x income account, kept while in force; both options 409 while unmapped,
then saved by the accountant only; the invoice refused (409, nothing raised) unmapped and after the account is made
inactive; one service account 20,000 untaxed (411 / slot) and taxed (23,800 / 20,000 / 3,800); parts and labour apart
untaxed and taxed with a return netted (6,000 / 14,000, no stock movement), the rounding example, the cap and the
labour-only job, the pure split; the credit note per line, the practice ledger's `invoices_tie` and company profit.
`MaintenanceCustomerJobTests` now expects 409 `accounts_not_mapped` for the unmapped row, and the settings theory no longer
lists MS-34 among the later options. Angular: `maintenance-settings.component.spec.ts` (MS-34's options selectable; the
slot's list only class-7 income accounts), `maintenance-words.spec.ts` (the line words in fr/ar/en, the refusal in words).

---

## MS-04 — as built (2026-11-10): the workshop list

The owner said "go" for MS-04's second option. "Only the workshop list" is now a setting like the rest: the default, any
employee of the job's branch, keeps today's behaviour exactly — the list is kept but nothing reads it.

### The list

A table, `MaintenanceWorkshopMembers` (`Domain/MaintenanceEntities.cs`, server-only): `EmployeeId`, `BranchId`, `AddedByUserId`,
`AddedBy`, `AddedAtUtc`; unique (EmployeeId, BranchId), indexed by branch, both foreign keys restricted (employees are never
deleted). No column on `Employee`: the list is maintenance's, per branch, and the HR file stays as it is.

**An employee who moves branch** (the simple rule chosen): a person is added only to the list of the branch they belong to
(400 `employee_not_of_branch` otherwise). Their row stays when they move, but counts only while they belong to that branch:
on the old branch they are no longer of the job's branch (booking's first rule), and they are not on the new branch's list
until someone adds them there — nothing is copied. The page shows the old row as "Moved to X: no longer counts here" so it
can be removed; if they come back, it counts again. A person who has left reads "Has left" and is never offered (booking
already requires employment on the day). Someone who has left cannot be added (400 `employee_not_employed`); a second row for
the same person and branch answers 409 `already_on_workshop_list`. Removing a row deletes it; hours booked before stay.

### The settings and the booking check

| | As built |
|---|---|
| Settings | `MaintenanceSettingsStore` takes `WorkshopListOnly` under `maintenance.settings` (MS-04 is in the operational half). **It is taken even while a branch's list is empty**: refusing it would make the owner fill every list before choosing, and an empty list only means nobody is booked there, which the page says — under the option, Maintenance › Settings names the branches that employ people today but have nobody on their list. |
| Candidates | `GET /api/maintenance/jobs/{id}/hours/candidates` offers, under `WorkshopListOnly`, the employees of the job's branch employed that day **and** on that branch's list; under the default, as before. |
| Booking | After "of the job's branch" (400 `employee_not_of_branch`) and "employed that day" (400 `employee_not_employed`): under `WorkshopListOnly`, someone not on the list of the job's branch answers 400 `employee_not_on_workshop_list`. M3's 409 `maintenance_option_not_yet` is gone. Someone on another branch's list only is either of another branch (`employee_not_of_branch`) or, having moved, not on this branch's list (`employee_not_on_workshop_list`). |

The list is read as it stands at booking time, not as it stood on the booked day: a person taken off the list can no longer
be booked for an earlier day either, and one added can be booked for days before they were added.

### Routes (three; security pins 376 company routes)

| Route | Key |
|---|---|
| `GET /api/maintenance/workshop?branchId=` | the settings' read keys: `maintenance.view`, `maintenance.settings` or `accounting.manage` — the members with their state, the employees in post not yet on their branch's list, and per branch how many are employed and how many of them the list may book |
| `POST /api/maintenance/workshop` `{ employeeId, branchId }` | `maintenance.settings` — onto the list of `branchId` (the employee's own branch when none) |
| `DELETE /api/maintenance/workshop/{id}` | `maintenance.settings` |

No new permission key (a new key signs everyone out): the list is kept under `maintenance.settings`. Under the jobs group
(`MaintenanceEndpoints.Workshop.cs`): 409 `maintenance_off` while MS-01 is off; branch-held like jobs — a non-admin's
`branchId` is their own (CallersBranch, on the list and on the add request, which is `IRaisedAtBranch`), and a row of another
branch's list named by id answers 404 (`ForBranchOf(DocumentBranches.MaintenanceWorkshopMember)`). The Admin reads and keeps
every branch's list. The legacy snapshot gives the read to the Manager and the Admin (the Manager holds `accounting.manage` by
role, as for the settings) and the two writes to the Admin only.

### Screens

A **Workshop** page under Maintenance (`maintenance/workshop`, before Settings; `maintenance-workshop.component.ts`), offered
while the module is on to `maintenance.settings` holders (the Admin by role, anyone else by grant), branch-scoped
(`branchOrAll`). It says in words whether MS-04 uses the list, warns of each branch whose list books nobody while it does,
lists the members as a `.ui-table--cards` table — person, the list's branch, the state as an `app-status` pill
(`workshopMember`: may be booked / moved to X / has left), who added them and when — with "Remove from the list" in the `⋯`
menu (`.is-danger`, confirmed first), and an "Add to the list" panel choosing among the employees in post not yet listed
(each with their branch for the Admin). Maintenance › Settings no longer marks "Only the workshop list" as "later phase";
under it, a hint points to the Workshop page and, while the module is on, a warning names the branches with nobody on their
list. The hours dialog says `employee_not_on_workshop_list` in words, and its "nobody to book" line covers an empty list.
Translations fr/ar/en in `core/i18n/translations-maintenance-workshop.ts`.

### Migration and tests

Migration `MaintenanceWorkshopList` in both sets (SQLite and SQL Server): the new table and its two indexes only; no existing
row changes. Both sets report no pending model changes.

`ParadiERP.Tests/Maintenance/MaintenanceWorkshopTests.cs` (3): the Admin's list across branches (added to the employee's own
branch when none is named, once per branch, refused for another branch, a leaver or nobody; one branch named; removed and
offered again); a branch manager reads and keeps only their own branch's list (a named branch replaced, another branch's row
404 and kept); a person who moves keeps the old row, which no longer counts and is not copied; the module off.
`MaintenanceLabourTests`: the default offers and books any employee of the branch with a list present; "only the workshop
list" offers nobody and refuses everyone while the list is empty, then only those on it, refuses someone on another branch's
list (`employee_not_of_branch`) and someone who moved in (`employee_not_on_workshop_list`), stops offering and booking someone
removed (their earlier hours kept), and the default again ignores the list. `MaintenanceSettingsTests`: MS-04 leaves the
"later" theory; `WorkshopListOnly` is saved under `maintenance.settings` (403 for the accountant alone) with every list empty,
and back. Angular: `maintenance-workshop.component.spec.ts` (5), the settings spec (MS-04 selectable, the hint and the
warning, the list not read while the module is off), `nav-keys.spec.ts` (the page's rule), `status-tones.spec.ts`.
