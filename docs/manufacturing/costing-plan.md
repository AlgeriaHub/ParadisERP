# Manufacturing — Costing and accounting plan (Phase A, A0 and Phase B built; C and D await approval)

Requested by the owner on 2026-10-26. Production runs `1225f0a` (manufacturing Phases 1 and 2, stock operations
off); the code was read at `6c6b70d`, which only adds documentation. Basis: Revision 2 of the Manufacturing &
Maintenance audit (sections 5 and 6, decisions D7–D9, D11–D14, D23, D41, D43), the Phase 0 tab (P0.4 and
decisions P0-D1 to P0-D9) and the accountant's sign-off tab (D9, D13, D33, D43, D45, D47), all in the Claude
Docs planning document (https://claude.ai/artifact/Ny3Rhfe1J9EnyGm7xWbpDj), plus [phase-1.md](phase-1.md),
[phase-2-plan.md](phase-2-plan.md) and [phase-2.md](phase-2.md).

**Phase A is built** (owner's approval of 2026-10-26, "Phase A only"; as built in
[costing-phase-a.md](costing-phase-a.md)); where it settled a choice this plan left open, the section says
*As built in Phase A*. Step A0 ([phase-2.md](phase-2.md) §8) and Phase B were built later, each on its own
approval, and are on `main`, not deployed. Phase B came in two steps that differ from its section below in
places: B1, the cost sheets, made on request after completion rather than inside the completion approval
(work status), and B2, the read-only reconciliation ([costing-b2.md](costing-b2.md)). Phases C and D are not
built and each waits for its own approval; where manufacturing stands is in [README.md](README.md). No
accounting treatment below is approved until the accountant signs the checklist in section 7. An assumption
in this document is never an approval.

Contents: 0 summary · 1 implementation audit · 2 costing specification · 3 labour cost from payroll ·
4 journal-entry matrix · 5 inventory valuation · 6 reconciliation and audit trail · 7 accountant sign-off ·
8 implementation phases · 9 risks, edge cases and acceptance criteria.

---

## 0. Summary

The system already records everything a cost sheet needs except money for labour, overhead and scrap
recovery: materials issued, returned, consumed and scrapped at a frozen reference cost; hours per employee,
stage and day; expenses; good, rejected and released quantities. It values none of it, posts nothing, and
nothing reads it into stock value.

The plan adds a costing engine in four phases, each switched on separately:

| Phase | What it does | Writes | Needs first |
|---|---|---|---|
| A | Computes estimated, provisional and actual cost per order and per accepted unit; read-only screens and reports | Nothing (computed on request) | Owner approval of this plan; the settings in §2.7 |
| B | Freezes the final cost at completion approval; history, late costs and reconciliation reports | New cost-sheet tables only | A accepted; owner approval |
| C | Simulates the journal entries in a separate shadow journal; reconciles them with the ledger | Shadow tables only, never the ledger | Accountant's answers to §7; P0.4a and P0.4b built (valuation shadow) |
| D | Posts manufacturing entries to the ledger | The ledger, through the existing poster | Every §7 item signed; the valuation switch (D9/D43); at least one month of C reviewed |

Four facts from the audit shape the design (section 1 has the full list):

1. **Material costs today are a last purchase price, not an average.** Each issue freezes `Product.CostPrice`,
   which every receiving note overwrites. The weighted-average shadow (P0.4) is not built. Until the
   accountant signs D9/D43 and the switch happens, every material figure is labelled a *reference cost*.
2. **Payroll stores money per person per month, but no hours.** A labour rate has to be derived: employer cost
   from the approved payslip line, hours from attendance as payroll reads it. New payroll runs compute no
   employer social contribution (CNAS), so labour cost is understated unless the accountant decides otherwise
   (D11).
3. **Every account code in posting code is a literal constant**, and the chart holds no raw-material,
   supplies, work-in-progress, finished-goods or production-stocked account. New postings must come from an
   account map the accountant fills, never from new constants.
4. **A finished-goods release records the finished product's `CostPrice`**, not what it cost to make. If
   finished goods were sold today their cost of sale would be that figure, or nothing when it is zero.
   Nothing may read that snapshot as a production cost.

---

## 1. Repository-based implementation audit

Read against the code, not the documents. Two independent read-only audits (Codex for manufacturing and
inventory, Copilot for payroll, attendance and the ledger) were checked line by line where they mattered.

### 1.1 What is implemented

| Area | What the code does | Evidence |
|---|---|---|
| Manufacturing orders | Created from the line's active BOM; planned, accepted and rejected base quantities; statuses with events; an estimated material cost frozen at creation | `Domain/ManufacturingOrderEntities.cs:54-74`; `Infrastructure/Manufacturing/ProductionOrders.cs:245-264` |
| BOM snapshot | Each material copied with net, waste and required base quantity, role, specification and `EstimatedUnitCost = component.CostPrice` at creation; stages copied with planned minutes and expected workers | `ProductionOrders.cs:245-253`; `ManufacturingOrderEntities.cs:101-148` |
| Reservations | Required − released − (issued − returned), only while the order is live, at the line's location; creation refuses a shortage unless `start_short` | `Infrastructure/Manufacturing/Production*.cs` (Phase 2, tested) |
| Material issue | Refuses more than the line holds or what other orders reserve; freezes `UnitCostSnapshot = Product.CostPrice`; moves `ProductStock` and writes a `StockMovement` for tracked products | `ProductionMovements.cs:119-180` |
| Material return | Back to the line's stock at the **average of that order's issue snapshots** for the material | `ProductionMovements.cs:147-152, 184-192` |
| Consumption | Quantity, reason, optional daily-record row; **no cost field**; moves no stock | `ManufacturingOrderEntities.cs:259-271` |
| Scrap of issued material | Counted on the order material and snapshotted at the issue average; moves no stock (already out at the issue); **no recovery value** | `ProductionMovements.cs:159-176` |
| Daily production record | One per line and day; rows per order and stage; approval by another production manager moves stages' good and rejected quantities | `ProductionLogs.cs` (Phase 2) |
| Labour booking | Employee, day, order, stage, line, row, hours, attendance hours, payroll branch, year and month, override reason; **no rate, amount or run id** | `ManufacturingOrderEntities.cs:383-406`; `ProductionLogs.cs:393-421` |
| Attendance capacity | Per day: the day row if any, else that day's paired clock shifts, plus overtime; declared-mode employees have no day figure | `AttendanceCapacity.cs:58-77` |
| Payroll | Runs per branch and month: Draft → Locked → Approved → Paid, or Reversed; lines carry gross, social base, employee and employer social, tax, net; elements carry days or hours, rate and amount | `Domain/HrEntities.cs:94-100, 510-684`; `PayrollDocuments.cs` |
| Partial output | Releases up to approved output-stage good quantity not yet released; moves finished stock; order stays open | `ProductionMovements.cs:255-289` |
| Completion | Requested when every record is approved, every material counted to zero and all output released; approved by another production manager | `ProductionOrders.cs` (Phase 2, tested) |
| Cost inputs read | `GET /api/manufacturing/orders/{id}/cost-inputs`: estimate, per-material quantities and **issued − returned** value, hours per employee and stage with the newest non-reversed run of that branch and month, expenses | `ProductionOrders.cs:502-540` |
| Posting state | `PostingState` has one value, `Pending`; no manufacturing code creates a journal entry | `ManufacturingOrderEntities.cs:17-21`; `ProductionMovements.cs` |
| Stock movements | Quantity, balance, reason, document and user; **no kind, no unit cost** | `Domain/Entities.cs:997-1054` |
| Product cost | `CostPrice` written by product create/edit and by every receiving note line | `CatalogEndpoints.cs:278-289, 685-694`; `StockEndpoints.cs:87-120` |
| Stock valuation | **No valuation report exists**; the inventory report holds quantities only; no weighted average, FIFO or cost layers anywhere | `ItemStatusReport.cs:686-712` |
| Cost of a sale | Catalogue `CostPrice` at the moment the sale is posted, Dr 600 / Cr 310; products costed at 0 post nothing; `SaleLine` stores no cost | `SyncService.cs:850-869`; `SalePosting.cs:112-116`; `Entities.cs:911-926` |
| BOM components | Only products marked raw material, consumable, accessory, spare part or semi-finished; merchandise (role None) is refused | `BillsOfMaterials.cs:460-462` |
| Transfers | Freeze `CostPrice` on the line at dispatch; receipt posts 310 between branches; a confirmed loss posts 657 / 310 | `StockTransfers.cs:111-126`; `DocumentPosting.cs:127-190` |
| Direct goods invoices | Post receivable and income only; move no stock, post no cost (P0.3 not built) | `ReceivableDocuments.cs:151-157, 212-253` |
| Ledger accounts | Seeded from `LedgerAccounts.Default`; class 3 holds only 310 and 318; every posting builder uses literal constants | `Domain/LedgerEntities.cs:214-333, 341-396` |
| Payroll posting | Approval posts one accrual per run: 631 wages, 635 employer social, 641 employer levies against 421, 425, 431, 442, 447 (and 471) | `PayrollPosting.cs:88-135` |

### 1.2 Findings that change the design

| # | Finding | Consequence |
|---|---|---|
| F1 | A release snapshots the **finished product's** `CostPrice` (`ProductionMovements.cs:265`), and the awaiting-posting report values releases with it (`ManufacturingReports.cs:111`) | That figure is not a production cost. Costing never reads it; Phase A relabels the report (§8) |
| F2 | Cost inputs value materials as issued − returned only; consumption and material scrap are counted but not valued. `phase-2.md` §1 says "used quantity at each movement's cost snapshot" | The document overstates what is built. The engine values consumption and scrap at the issue average (§2.3) |
| F3 | Issue cost is `Product.CostPrice`, the last purchase price, overwritten by receiving; P0.4a/b/c (movement kind, unit cost, sale-line cost, weighted-average shadow) are **not built** | Material cost is a *reference cost* until the valuation switch. Accurate costs need P0.4 first |
| F4 | Payroll lines store no hours; there is no hourly pay basis (daily and monthly only); `EmployerLevies` is computed per employee but stored only as a run total | The labour rate is derived (§3); levies per employee are recomputed from the run's settings snapshot |
| F5 | New runs set `CalculateStatutoryDeductions = false`, so `SocialEmployer` is 0 on them (`PayrollDocuments.cs:169-177`); the DZ preset's 30% employer rate is a deliberate round placeholder, "not the real split", and the preset defines no employer levy (`PayrollPresets.cs:88-100`) | Labour cost misses the employer share unless D11 adds a costing burden; that burden would not be in the books (§3.6) |
| F6 | Payroll reads day rows for the **whole month** once any exists for the employee (`AttendanceSheet.cs:659-662`); capacity reads them **per day** and falls back to clock shifts on days without a row (`AttendanceCapacity.cs:64-77`) | Hours can be booked on a day payroll treats as absent. Must be aligned before labour cost is final (§3.5) |
| F7 | A run is unique per branch and month, not per employee; an employee who changes branch can appear in two runs for one month (`PosDbContext.cs:807-844`; `EmployeeDirectory.cs:37-54`) | The actual rate combines every non-reversed line of the employee's month, whatever the branch |
| F8 | An approved, unpaid run can be reversed and recomputed; a paid run is final and corrected by an adjustment in the next run (`PayrollDocuments.cs:592-630`, `:604`) | Rates stay provisional while a run can still change; late corrections land in another month (§3.4) |
| F9 | An order expense's voucher link only checks that the voucher exists (`ProductionOrders.cs:399-425`) | The same voucher can be counted on two orders, or above its amount. Costing needs a cap per voucher (§2.4) |
| F10 | Ledger accounts are literal constants; the chart has no 31x, 32x, 33x, 35x or 72 account | New postings need an account map filled by the accountant (§4.1), seeded empty |
| F11 | Material issues never go negative (enforced in code); till sales of finished goods can (`SyncService.cs:729-760`) | Finished goods may leave before they are released; valuation must cover it (§5.4) |
| F12 | Sale cost skips products whose `CostPrice` is 0 | A manufactured product with no typed cost would post no cost of sale at all under today's method (§5.5) |

### 1.3 What is documented but not built

- The weighted-average valuation ledger, its opening and the switch (Revision 2 §6; P0.4a–c).
- The labour-rate computation, provisional and final (Revision 2 §5; Revision 1 §4).
- Overhead, scrap recovery value, cost sheets, variances and any manufacturing posting.
- P0.2 (payroll cash in group reports) and P0.3 (goods invoices move stock and post cost).

---

## 2. Manufacturing costing specification

### 2.1 Principles

1. Cost comes from what was **actually** issued, returned, consumed and worked, never from the BOM. The BOM
   gives the estimate, the reservation and the standard for variances.
2. Three figures per order, always shown with their basis:
   - **Estimated:** frozen at creation (materials already are; labour and overhead estimates added in Phase B).
   - **Provisional (actual to date):** recomputed on request while the order is open; every element says
     whether it is final or provisional (for example "labour: October payroll not approved").
   - **Final:** computed and frozen when completion is approved; never edited afterwards (§2.8).
3. No master pay rate, no salary copy: labour comes from payroll (section 3).
4. Nothing writes `Product.CostPrice`, payroll, attendance or the ledger before Phase D.
5. Full precision inside; money rounded only where it is shown or posted. The final release absorbs rounding,
   so the order's value always clears to zero.

### 2.2 Cost elements

| Code | Element | Quantity source | Value |
|---|---|---|---|
| M | Raw materials | Issued − returned (base unit) | Issue unit cost frozen on each issue line; a return at the order's issue average |
| K | Consumables | Same | Same |
| A | Accessories and packaging | Same | Same |
| L | Direct labour | Booked hours on approved daily-record rows | Hours × the employee's labour rate for that payroll month (section 3) |
| E | Order expenses | `ProductionOrderExpense` | Amount before recoverable tax (§2.4, accountant to confirm) |
| O | Overhead | Booked hours (or another approved basis) | Approved rate × basis; 0 unless the accountant approves a rate (§2.6) |
| R | Scrap recovery | Rejected output and offcuts kept as saleable scrap | Recovery value entered on a scrap-recovery record (new, Phase B) |
| Qa | Accepted quantity | Σ released base quantity | — |
| Qr | Rejected quantity | Rejected at the output stage (from approved records) | — |

Total actual cost **C = M + K + A + L + E + O − R**. Actual cost per accepted unit **= C ÷ Qa** (Qa > 0).

"Issue unit cost" is the reference cost today (`Product.CostPrice` at issue, already frozen). After the
valuation switch it is the weighted average at issue, frozen on the line the same way (§5.2).

### 2.3 Materials in detail

Per order material, at completion: issued − returned = consumed + scrapped (the leftover count enforces it).
The engine values:

- **Net issued value** = Σ issue value − Σ return value (what left the line's stock for good).
- **Consumed value** = consumed quantity × the order material's issue average.
- **Material scrap value** = scrapped quantity × the issue average (offcuts and spoiled material).
- While the order is open, **value still at the line** = (issued − returned − consumed − scrapped) × issue
  average. It is part of work in progress, not of any released unit.

Consumed + scrap value equals net issued value at completion; the engine checks it and refuses to freeze a
cost that does not reconcile.

### 2.4 Expenses

- Counted at the amount recorded on the order, before recoverable VAT (§7 C-07 asks the accountant).
- New rule (step A0, §8): the expenses linked to one payment voucher never exceed that voucher's amount
  before tax, and a voucher cannot be linked from an order of a line the caller cannot reach (F9).
- An expense dated after completion approval is a **late cost** (§2.8), never added to the frozen cost.

### 2.5 Labour

Hours × rate per employee and payroll month, as section 3 defines. A booking's hours are counted once
(unique per employee and row; the day's total never above attendance unless an audited override).

### 2.6 Overhead

- Off by default: revision 2 records that the owner's list has none.
- When the accountant approves it, an **overhead rate** setting with a basis (labour hours is proposed; a
  per-line rate may follow), a value, and an effective-from date. Rates are versioned, never edited in
  place; an order uses the version in force on each booking's day.
- Applied overhead = Σ basis × rate. The plan does not build an actual-overhead pool or an over/under
  absorption account; the accountant may ask for one later (§7 C-08).

### 2.7 Settings introduced (none exist today)

Stored like the payroll settings (one JSON document, Admin only, history kept), read by the engine:

| Setting | Default | Approved by |
|---|---|---|
| Labour-rate method: blended actual rate (§3.3) | On | Accountant (C-05) |
| Employer burden % used only when a payslip carries no employer social (D11) | Off | Accountant (C-06) |
| Overhead rate versions (basis, value, effective date) | None | Accountant (C-08) |
| Partial-release valuation rule (§2.9) | Standard materials + actual conversion | Accountant (C-11) |
| Expense VAT basis | Amount as recorded | Accountant (C-07) |

*As built in Phase A:* none of these has an approved value yet, so Phase A ships the defaults in code
(`CostingPolicy.Default`: no overhead; blended rate; the §2.9 rule; amounts as recorded; no burden) and no
settings screen. The stored, editable document arrives with the accountant's answers, so nobody can type
an unapproved overhead rate into live figures in the meantime.

### 2.8 Snapshots, immutability and late costs

- **Final cost sheet** (Phase B): written once, in the completion-approval transaction: every element by
  material, employee-month, expense and overhead version; Qa, Qr; unit cost; the inputs' identities (issue
  lines, bookings, payroll lines used, overhead version) and a hash of them.
- A completed order's cost is never recomputed. Later changes — a payroll run approved or reversed, a voucher
  re-dated, a product's cost edited, a BOM changed — produce **late-cost records**: the order, the element,
  the difference, the cause and the date. They are reported, never added (D13).
- A correction to a frozen cost is a separate, approved adjusting document that names the original; the
  original stays readable.

### 2.9 Partial releases

Revision 2 values each release "at the cost accumulated so far on the units produced and not yet released",
and the final release carries the remainder. Because material is usually issued for the whole order on day
one and counted at the end, cost to date over good units would load the first release with all the
material. The plan therefore proposes (accountant to confirm, C-11):

- **Release value per unit = standard material per unit at actual issue averages + conversion to date per
  good unit not yet released**, where conversion = labour + overhead + expenses booked so far, less the
  conversion already carried by earlier releases, divided by good output-stage units not yet released.
- **The final release carries the remainder:** C − Σ earlier release values. It may be higher or lower per
  unit than the earlier ones; the cost sheet shows each release's unit value and the order's average.
- Alternative (simpler): every partial release at the frozen estimated unit cost; the final one carries
  the rest.
- If completion is approved with nothing left to release, the remainder is not added to stock: it stays in
  the charges and the cost sheet shows it as the order's unabsorbed variance (D13).
- *As built in Phase A:* release values are recomputed on every read with the rates known at that moment,
  so an early release's value moves when its month's payroll is approved; the sum of a completed order's
  releases is always its cost. Phase B freezes each release's value when it is made.

### 2.10 Rejected production, scrap and wastage

- Rejected units absorb their share of cost: the cost per accepted unit is C ÷ Qa (D23, "rejects absorbed,
  scrap value credited").
- Rejects or offcuts kept as saleable scrap enter scrap stock at their recovery value R, which reduces C.
- Shown for management: **spoilage cost** = (C + R) × Qr ÷ (Qa + Qr) − R, and **material wastage** = the
  material usage variance (§2.11) plus material scrap value.

### 2.11 Variances (management figures)

Standards for the actual output scale the frozen estimate: standard quantity = required base quantity ×
Qa ÷ planned base quantity; standard hours = planned stage hours × Qa ÷ planned quantity.

| Variance | Formula |
|---|---|
| Material usage | (actual net quantity − standard quantity) × estimated unit cost |
| Material price | actual net quantity × (actual issue average − estimated unit cost) |
| Labour efficiency | (actual hours − standard hours) × standard rate |
| Labour rate | actual labour cost − actual hours × standard rate |
| Overhead | applied overhead − standard hours × the estimate's overhead rate |
| Expenses | actual expenses − estimated expenses |
| Scrap recovery | − R |
| **Total** | final cost − estimated unit cost × Qa |

The engine checks that the variances sum to the total; a test pins it.

### 2.12 Worked example (illustrative figures, amounts in DZD)

Order for **200 kg of nails**, in two batches. Per kg of output, the BOM gives wire 1.05 kg (5% waste),
cutting discs 0.05 and boxes 0.1. Stages: cut 4 h and form 4 h per batch, one worker each.

**Estimated at creation** (reference costs: wire 150/kg, disc 300, box 50; a standard labour rate of 380/h
from payroll; an approved overhead of 100 per labour hour):

| Element | Calculation | Amount |
|---|---|---|
| Wire (M) | 210 kg × 150 | 31,500.00 |
| Discs (K) | 10 × 300 | 3,000.00 |
| Boxes (A) | 20 × 50 | 1,000.00 |
| Labour (L) | 16 h × 380 | 6,080.00 |
| Overhead (O) | 16 h × 100 | 1,600.00 |
| **Estimated total** | | **43,180.00** |
| Estimated unit cost | 43,180 ÷ 200 kg | 215.900 |

**Actual:** wire 210 kg issued and 4 kg returned (206 kg: 200 used and 6 kg of offcuts declared consumed);
all discs and boxes used; 14 h booked (Karim 8 h, Samir 6 h); payroll approved at 400/h for Karim and 350/h
for Samir (provisional 380/h before); a galvanizing subcontract of 12,000 before tax; 4 kg rejected and kept
as scrap at 30/kg; 196 kg accepted, released as 100 kg then 96 kg.

| Element | Calculation | Amount |
|---|---|---|
| Wire, net | 31,500 − 600 | 30,900.00 |
| Discs | | 3,000.00 |
| Boxes | | 1,000.00 |
| Labour | 8 × 400 + 6 × 350 | 5,300.00 |
| Expenses | | 12,000.00 |
| Overhead | 14 h × 100 | 1,400.00 |
| Scrap recovery | − 4 × 30 | − 120.00 |
| **Final cost C** | | **53,480.00** |
| Actual cost per accepted kg | 53,480 ÷ 196 | 272.857 |

**Releases** (rule of §2.9). Release 1, 100 kg, after day 1 (9 h booked at the provisional 380, overhead 900,
no expense yet): standard materials 1.05 × 150 + 0.05 × 300 + 0.1 × 50 = 177.50/kg; conversion
(3,420 + 900) ÷ 100 = 43.20/kg; **22,070.00**. Release 2, 96 kg, final: 53,480 − 22,070 =
**31,410.00** (327.1875/kg), carrying the expense and the payroll difference.

**Variances** for 196 kg (standards: wire 205.8 kg, discs 9.8, boxes 19.6, 15.68 h):

| Variance | Calculation | Amount |
|---|---|---|
| Material usage | (206 − 205.8) × 150 + (10 − 9.8) × 300 + (20 − 19.6) × 50 | 110.00 U |
| Material price | issue cost equals the estimate here | 0.00 |
| Labour efficiency | (14 − 15.68) × 380 | 638.40 F |
| Labour rate | 5,300 − 14 × 380 | 20.00 F |
| Overhead | 1,400 − 15.68 × 100 | 168.00 F |
| Expenses | 12,000 − 0 | 12,000.00 U |
| Scrap recovery | | 120.00 F |
| **Total** | 53,480 − 215.90 × 196 (= 42,316.40) | **11,163.60 U** |

Spoilage cost for management: (53,480 + 120) × 4 ÷ 200 − 120 = **952.00**, absorbed by the 196 accepted kg.

---

## 3. Payroll-to-manufacturing labour cost

### 3.1 Rules

- No pay rate is stored anywhere new and none is typed: every rate is computed from the payroll engine and
  the employee's payroll lines, then frozen on the cost sheet (Phase B).
- Rates and per-employee costs are computed on the server. A production user sees hours and the order's
  total labour cost; per-employee money is shown only to holders of `payroll.manage`. No production user is
  given `payroll.manage`.
- Payroll calculations, runs, lines and attendance are only **read**.

### 3.2 Standard (provisional) rate

Used for the estimate and for bookings in a month whose run is not approved:

**standard rate = (G + S + V) ÷ (N × h)**

- G: a full month's gross from `PayrollEngine.ComputeOne` (`PayrollEngine.cs:247`), run in memory with full
  attendance and no overtime, using the payroll settings in force; for a daily worker, the daily rate × N.
- S: the employer social contribution the engine computes for G when statutory deductions apply; otherwise
  the D11 burden if the accountant enables it (§3.6), else 0.
- V: the employer levies the engine computes for G (`PayrollEngine.cs:302-343`); none unless the payroll
  settings define one, since the Algeria preset has no levy.
- N: working days per month and h: hours per day, from the payroll settings (26 and 8 by default,
  `PayrollSettings.cs:284, 287`), or the special period's paid hours (Ramadan).

### 3.3 Actual rate (per employee and payroll month)

Once every run that holds the employee for the month is Approved or Paid:

**actual rate = (Σ line.Gross + Σ line.SocialEmployer + Σ employer levies for the employee) ÷ H**

- Lines: every non-reversed line of the employee for that year and month, across branches (F7).
- Employer levies per employee: the run's `EmployerLevies` shared by each line's social base (the elements
  the run counted in it). That is the engine's own per-employee figure whenever the base is not clamped,
  which the Algeria preset never does, and it needs no change to payroll code. Recomputing through the
  engine is the alternative offered to the accountant (C-06). *As built in Phase A.*
- H, paid hours: the payslip's own days — days present × the run's hours per day (its settings snapshot)
  — plus the overtime hours on its overtime elements (a band counted in days is that many full days). A
  monthly employee nobody entered attendance for, whom payroll pays the full month, counts working days ×
  hours per day. Paid leave and holidays are spread over the hours worked (Revision 1, D12), so they are in
  the numerator and not the denominator. *As built in Phase A:* reading the payslip rather than
  re-reading attendance keeps the denominator exactly what payroll paid for; a special period's shorter
  days (Ramadan) still count as full days, which slightly lowers the rate for that month.
- One blended rate per worked hour, overtime premium included (Revision 1). The alternative — charging the
  premium only to hours booked as overtime — is a setting since 2026-11-09: bookings mark their overtime hours, and
  the rule and its edge cases are in [phase-c.md](phase-c.md), "Labour absorption (C-05)".

### 3.4 Pending, late and corrected payroll

| Situation | Open order | Completed order (frozen) |
|---|---|---|
| The month's run is not approved | Bookings at the standard rate, marked provisional | Frozen at the rate used at approval; listed as "frozen on a provisional rate" |
| The run is approved | Actual rate replaces the standard one on the next computation | Unchanged; the difference is a late-cost record (D13) |
| An approved, unpaid run is reversed | Back to provisional until the new run is approved | Unchanged; late-cost record |
| A paid run is corrected by an adjustment in the next run | The adjustment counts in the month it is paid, as payroll does; it changes that month's rate | Unchanged; the change is reported for orders that used the month |
| The employee is paid in two branches' runs for one month | Lines combined | Same |

### 3.5 One hour, one cost

- Hours: the existing rules hold — one booking per employee and row, and the day's total across every line
  and order kind never above the attendance the day shows, unless the audited `labour.override`.
- **Prerequisite (F6):** the capacity check must read attendance by payroll's month rule. Otherwise an hour
  booked on a punch-only day of a month that also has day rows is costed against a payslip that never paid
  it. This is a behaviour change to Phase 2 and needs its own approval (§8, Phase A, step A0).
- Money: an employee-month's employer cost is the numerator once. Hours booked to orders take
  rate × hours; **unabsorbed labour** (hours worked but not booked, plus non-order rows such as cleaning or
  breakdowns) stays in the payroll charges and is reported per line and month.
- A booking keeps the payroll branch it was made under for reporting; the rate uses the employee's lines
  wherever they were paid.

### 3.6 Employer contributions (D11)

New runs compute no employer social contribution (F5), so S = 0 on them. Three options for the accountant
(C-06):

1. Labour cost = what payroll records (no CNAS on new runs): consistent with the books, but understated.
2. A costing burden % on months whose payslips carry no employer social, **in management cost only**: unit
   cost reports include it, stock value and postings do not.
3. The burden in stock value too: production would then enter stock at a cost that includes a charge the
   books never record. Not recommended.

The plan recommends 1 for stock value, with 2 available for pricing and reports.

---

## 4. Proposed journal-entry matrix

Nothing here posts before Phase D and the accountant's signature. Account codes in **bold** exist in the
seeded chart today (`LedgerEntities.cs:341-396`). Codes marked *(D33)* are the accountant-sheet proposals
(311, 321/322, 331, 351/355, 358, 601, 602, 72, 701): **none exists in the chart, none is approved**, and
the system will post to whatever the accountant enters in the account map, never to a constant.

### 4.1 The account map (new, Phase C)

One row per product role and event — material issue, return, scrap, WIP, release, sale cost, revenue,
transfer, variance (if chosen), valuation difference — naming an account from the chart. It is seeded
**empty**. A missing row is shown in the simulation as "unmapped" and blocks Phase D; merchandise (every
existing product) keeps today's 310/600/700 exactly, so no current posting changes.

### 4.2 Two models

**Model N — charges by nature (recommended; Revision 2 and D13).** Costs stay where they are charged:
materials in consumption accounts when issued, wages in 631/635/641 when payroll is approved, expenses where
their voucher posts them. Production enters stock through *production stocked* at its cost; open orders are
valued once a month in work in progress against the same account, reversed on the first day. No variance
account: what is not carried into stock stays in the charges.

**Model W — perpetual work in progress (alternative).** Every cost flows through a work-in-progress
account as it happens and leaves it at release; labour and overhead enter it through absorbed-cost
accounts the accountant names; residual differences go to a variance account. It needs credit accounts
that offset wages and overhead already in their charges; the accountant must confirm how the SCF presents
them. More entries, more accounts, and a risk of counting wages twice if an absorption is missed.

### 4.3 Matrix

| Event | Model N (recommended) | Model W (alternative) |
|---|---|---|
| Raw material issue | Dr consumption — raw materials *(601, D33)* / Cr raw-material stock *(311, D33)* at issue cost | Dr WIP *(331)* / Cr raw-material stock |
| Consumable, accessory or packaging issue | Dr consumption — supplies *(602, D33)* / Cr supplies stock *(32x, D33)* | Dr WIP / Cr supplies stock |
| Semi-finished product issued to another order | Dr production stocked *(72, destocking)* / Cr semi-finished stock *(35x, D33)* | Dr WIP / Cr semi-finished stock |
| Material return | The issue reversed at the order's issue average | Dr stock / Cr WIP |
| Material scrap (offcuts) | None: already consumed at issue | None (stays in WIP) |
| Direct labour | None from manufacturing: payroll approval already posts **631/635/641** | Dr WIP / Cr labour absorbed (account to be named) |
| Order expenses | None: the voucher already posts its expense | Dr WIP / Cr the expense account (reclassification) |
| Overhead | None: absorbed only through the stock value | Dr WIP / Cr overhead absorbed (account to be named) |
| Month-end, open orders | Dr WIP *(331)* / Cr production stocked *(72)* for cost to date − released; reversed on the 1st | None (WIP is perpetual) |
| Partial release | Dr finished goods *(35x)* / Cr production stocked *(72)* at release value | Dr finished goods / Cr WIP |
| Final release and completion | Same, carrying the remainder; anything not releasable stays in the charges | Same; a residual goes to the variance account |
| Rejects kept as scrap | Dr scrap stock *(358, D33, or **318**)* / Cr production stocked *(72)* at recovery value | Dr scrap stock / Cr WIP |
| Rejects with no value | None | None |
| Variance | None (management figure on the cost sheet) | Dr/Cr variance account / Cr/Dr WIP |
| Transfer of finished goods between locations | Dr finished goods (receiving branch) / Cr finished goods (sending branch) at average, as **310** transfers post today | Same |
| Internal transport (D47) | Dr **624** at the receiving branch, balanced through **181**, never marked inter-branch | Same |
| Sale of finished goods: cost | Dr production stocked *(72, destocking)* / Cr finished goods *(35x)* at average | Dr cost of goods sold (account to be named) / Cr finished goods |
| Sale of finished goods: revenue | Dr **411** or cash / Cr sales of own production *(701, D33)*, tax on **4457** per the tax profile | Same |
| Scrap sold | Dr cash / Cr **756**, as today; its stock leaves at its value | Same |

### 4.4 The worked example posted (Model N, amounts in DZD)

| # | Event | Debit | Credit | Amount |
|---|---|---|---|---|
| 1 | Issue wire | 601 *(D33)* | 311 *(D33)* | 31,500.00 |
| 2 | Issue discs and boxes | 602 *(D33)* | 32x *(D33)* | 4,000.00 |
| 3 | Return 4 kg of wire | 311 | 601 | 600.00 |
| 4 | October payroll (existing posting, unchanged) | **631**, **635**, **641** | **421**, **431**, **447** … | payroll's own figures |
| 5 | Galvanizing voucher (existing posting, unchanged) | its expense account | **530** or **512** | 12,000.00 |
| 6 | Month-end, order open after release 1 (cost to date 39,820 − released 22,070) | 331 *(D33)* | 72 *(D33)* | 17,750.00 |
| 7 | Reversal on the 1st | 72 | 331 | 17,750.00 |
| 8 | Release 1, 100 kg | 35x *(D33)* | 72 | 22,070.00 |
| 9 | Release 2, 96 kg, final | 35x | 72 | 31,410.00 |
| 10 | 4 kg of rejects kept as scrap | 358 *(D33)* | 72 | 120.00 |
| 11 | Sale of 50 kg at the average 272.857 (cost side) | 72 | 35x | 13,642.857 |
| 12 | Same sale, revenue at a price of 400/kg | **411** | 701 *(D33)*; tax per profile on **4457** | 20,000.00 + tax |
| 13 | Transfer of 96 kg to another branch at the average | 35x (receiving) | 35x (sending) | 26,194.286 |

Production stocked receives 22,070 + 31,410 + 120 = 53,600, exactly the materials, labour, expenses and
overhead the order used (30,900 + 4,000 + 5,300 + 12,000 + 1,400). Overhead enters stock value while the
actual overhead costs stay in their own charges.

---

## 5. Inventory valuation strategy

### 5.1 Today

One `CostPrice` per product for the whole company, overwritten by every receiving note and editable on the
product; sale cost read from it when the sale is posted; transfers freeze it; no stock-valuation report; no
average, FIFO or layers (§1.1). No new method is activated by this plan.

### 5.2 Material issues

- **Until the switch:** the issue line's reference cost, already frozen, is what the engine uses. Every
  figure built on it is labelled "reference cost (last purchase price at issue)".
- **When P0.4b exists (shadow average):** each manufacturing issue also records the shadow average at
  issue, beside the reference cost, without changing anything else. Previews can show both.
- **After the switch (D9/D43 signed):** the issue line's cost is the weighted average at issue, frozen; the
  return goes back at the order's issue average, as today.

### 5.3 Finished-goods receipts

- Each release records its own **production value** (new field, Phase B) from §2.9; the existing
  `UnitCostSnapshot` (the product's `CostPrice`, F1) is left as is and never used for value.
- In the valuation ledger a release is an inflow at that value; scrap kept enters the scrap pool at its
  recovery value.
- `Product.CostPrice` is never written by manufacturing (Revision 2 §6).

### 5.4 Edge cases

| Case | Treatment |
|---|---|
| Historical stock | Never revalued: an inflow changes the average for what follows only; a completed order's cost is frozen; late costs are reported, not pushed into goods already sold |
| Negative finished stock (a till sale before the release) | The outflow leaves at the current average; the next release first covers the negative quantity; the gap is a valuation difference to an account the accountant names (D43), reported per product |
| Material issues | Cannot go negative (D25, enforced) |
| Returns and refunds of finished goods | At the original sale's cost (P0.4c; needs `SaleLine.UnitCost`, P0.4a). Until then refunds follow today's rule, which is wrong for manufactured goods (§5.5) |
| Mixed costs (several orders, several purchases) | One weighted average per product company-wide; transfers move value at that average and change it for no one |
| Partial production | Releases at their own value; open orders' cost to date as month-end work in progress (Model N) |
| Untracked materials (`TrackStock` false) | Valued at their issue snapshot; no stock pool |
| A material issued at a reference cost of 0 | Flagged "unvalued material"; completion approval refuses to freeze the cost until it is costed or explicitly accepted with a reason |

### 5.5 Selling finished goods before live costing

If stock operations were switched on before Phase D, finished goods would sell at their typed `CostPrice`,
or post no cost at all when it is 0 (F12). Options for the owner and accountant (C-12): keep stock
operations off until Phase D (recommended); or allow them with finished-goods sales costed at a manually
typed `CostPrice` taken from the last approved unit cost, as an explicit, logged interim rule.

---

## 6. Reconciliation and audit design

### 6.1 Reports (read-only)

| # | Report | Compares | Phase |
|---|---|---|---|
| R1 | Order cost sheet | Estimated, provisional and final cost by element; unit cost; variances; each figure drills to its documents | A (provisional), B (frozen) |
| R2 | Work-in-progress register | Per open order: cost to date, released value, value still at the line, WIP; totals per line and month | A |
| R3 | Material flow | Per product and location: `StockMovement` total against `ProductStock`; every manufacturing stock movement has exactly one source line, and none is orphaned | A |
| R4 | Labour absorption | Per employee and month: payroll employer cost, cost absorbed into orders, unabsorbed cost; booked hours against attendance hours as payroll read them | A |
| R5 | Pending manufacturing events | The existing awaiting-posting register, valued by the engine (not by `CostPrice`), with posting state | A |
| R6 | Late costs | Every difference after completion: cause, element, amount, order | B |
| R7 | Valuation | Shadow average against today's method and, after the switch, against the stock accounts, per product and role | C (needs P0.4b) |
| R8 | Ledger tie-out | Simulated (then posted) entries per account and month against the events behind them: consumption = issues − returns; production stocked = releases + scrap kept + month-end WIP net of reversals | C, D |

### 6.2 Audit trail

Order → material movements (MI-, MR-, MS-) → their `StockMovement` (`StockMovementId` on each line) →
(Phase D) the journal entry, by source type and id. Releases (OR-) the same way. Bookings → daily-record
row → payroll lines used and the rate frozen on the cost sheet. Expenses → voucher. The final cost sheet
lists each input's identity and a hash, so a later change to any input is detectable.

### 6.3 Invariants checked automatically

- At completion, for each material: issued − returned − consumed − scrapped = 0.
- At completion: C = Σ release values + scrap kept + remainder kept in the charges.
- Every booked hour is costed once; the employee-month's absorbed cost never exceeds its employer cost.
- Simulated and posted entries balance per entry and per branch.
- Nothing in costing writes `Product.CostPrice`, payroll, attendance or (before D) the ledger: an
  architecture test, as P0.4b has.

---

## 7. Accountant sign-off checklist

Every row is **pending**. Rows that repeat an existing decision cite it; C-numbers are new. Nothing is built
on a row until it is signed. The accountant's copy, with the account mapping and the tax points gathered,
is [costing-accountant-signoff.md](costing-accountant-signoff.md).

| # | Item | Question | Options | Recommendation |
|---|---|---|---|---|
| C-01 | Valuation method (D9) | How is stock valued? | Last purchase price / weighted average (CMUP) / FIFO | Weighted average, perpetual, company-wide |
| C-02 | Opening adjustment (D43) | At the switch, the difference between the opening valuation and 310/318 goes to? | Current-period stock variation / equity if material | One entry, figure shown first; account named by you |
| C-03 | Accounting model | Charges by nature with production stocked, or perpetual WIP | Model N / Model W (§4.2) | Model N |
| C-04 | Accounts by role (D33) | Raw materials, supplies, WIP, finished goods, scrap, consumption, production stocked, own-production sales | Codes in §4.3, or yours | Your codes; nothing is created before you name them |
| C-05 | Direct labour absorption | Rate method and denominator | Blended actual rate over worked hours, leave spread (§3.3) / premium to overtime hours only | Blended actual rate |
| C-06 | Employer costs (D11) | CNAS absent from new runs; levies per employee | §3.6 options 1–3; levies recomputed or allocated by gross | Option 1 in stock value, 2 in reports; levies recomputed |
| C-07 | Expenses | Amount basis | Before recoverable VAT / as recorded | Before recoverable VAT |
| C-08 | Overhead | Apply any? Basis, rate, normal capacity; over/under absorption | None / labour-hour rate / machine-hour rate | None until you set a rate |
| C-09 | Scrap | Recovery value at release, its stock account, its sale | Account 358 (D33) / 318 (existing) | Your choice of account; recovery entered per order |
| C-10 | Late costs (D13) | After completion | Stay in the charges and reported / a class-6 variance account | Stay in the charges, reported |
| C-11 | Partial production | Release valuation | §2.9 rule / estimated unit cost / cost to date | §2.9 rule; final release carries the rest |
| C-12 | Finished goods before live costing | Sales cost of finished goods until Phase D | Stock operations off / interim typed cost | Stock operations off |
| C-13 | Variance accounting | Post variances? | No account (management only) / variance account | No account under Model N |
| C-14 | Internal transfers (D47) | Goods at average, no sale or tax; transport as the receiving branch's expense through 181 | As proposed / transport in goods' value | As proposed |
| C-15 | Month-end WIP | Value open orders monthly | Yes, reversed on the 1st / only at year end | Monthly |
| C-16 | Negative stock gap (D43) | Account for the difference when a later inflow covers a negative quantity | Your account | Your account |
| C-17 | Rounding | Ledger precision | Three decimals, final release absorbs the residual | As proposed |
| C-18 | Algerian requirements | Do the chosen method, accounts, by-nature presentation, stock variation, VAT on advances (D45) and the year-end count meet the SCF and tax rules as you apply them? | Your confirmation or corrections | — (we do not assert regulatory conclusions) |

Signing a row authorises a method; it does not post anything.

---

## 8. Safe implementation phases

Each phase ships on its own: `dotnet test`, `ng test`, `npm run build`, the provider smoke on a fresh SQL
Server database for any migration, Arabic and French screenshots, and the owner's go-ahead to deploy after a
full database backup (P0-D9). Every phase is behind a server setting that is off by default, like
`Manufacturing:StockOperations`.

### Phase A — Cost calculation and read-only previews

- **A0 (separate approval, behaviour change; not built):** align `AttendanceCapacity` with payroll's month
  rule (F6); cap expenses per voucher (F9).
- A pure costing engine in the Application layer (no database), fed by a read model: elements, partial
  releases, variances, invariants.
- A labour-rate service that only reads: standard rate through `PayrollEngine.ComputeOne` with the
  settings in force; actual rate from payroll lines; per-employee levies shared by social base (§3.3).
- Order cost tab and R1–R5, provisional only; the awaiting-posting report valued by the engine (F1).
- The settings of §2.7 as defaults in code; the editable document comes with the accountant's answers.
- **Migration:** none.
- **Security:** a new key `manufacturing.costs.view` (Admin by role, grantable through Manufacturing ›
  Access); line isolation (404 out of reach); per-employee money only with `payroll.manage`.
- **Rollout:** `Manufacturing:Costing` off by default; on for the Admin first.
- **Rollback:** switch it off or revert the code; nothing was written.

### Phase B — Historical cost snapshots and reconciliation

- Tables: cost sheets, cost-sheet lines, labour-rate snapshots, late-cost records, scrap recovery; a
  production-value field on releases.
- Completion approval freezes the final cost in the same transaction (and refuses unvalued materials
  without a reason); late-cost records written by the events of §3.4.
- Reports R1 (frozen), R6; per-release values recorded.
- **Migration:** additive, both providers; orders completed before B get a reconstructed sheet marked
  "reconstructed", never presented as frozen at the time.
- **Security:** freezing happens only inside completion approval; nothing edits a frozen sheet.
- **Rollout:** `Manufacturing:CostFreeze` off by default.
- **Rollback:** revert the code; the Down step drops the new tables. Frozen sheets are lost but can be
  reconstructed, because their inputs remain.

### Phase C — Shadow accounting simulation

- **Needs:** P0.4a and P0.4b (movement kind and unit cost; the weighted-average shadow), separately approved;
  the accountant's answers to C-01 to C-17.
- The account map of §4.1, seeded empty and filled by the accountant.
- A shadow journal (its own tables) receiving the simulated entries of the chosen model per event, plus
  month-end WIP; never `LedgerEntries`.
- Reports R7 and R8; at least one full month reviewed with the accountant (as P0-D8).
- **Migration:** additive (account map, shadow journal).
- **Security:** the account map is editable by the Admin only, with history; simulations readable by
  accounting roles.
- **Rollout:** `Manufacturing:AccountingShadow` off by default.
- **Rollback:** revert the code; drop the shadow tables; the ledger was never touched.

### Phase D — Accountant-approved live manufacturing accounting

- **Needs:** every row of §7 signed; the valuation switch done (D9/D43); Phase C's month reviewed; an
  opening catch-up entry for movements made while postings were off, approved as one figure.
- Posting through the existing `LedgerPoster` with the account map; one entry per source document,
  idempotent; month-end WIP and its reversal; `PostingState` gains Posted.
- Only then may `Manufacturing:StockOperations` be switched on in production, together with
  `Manufacturing:Postings`.
- **Migration:** none expected beyond the posting-state value.
- **Security:** postings by the server only; nobody types a manufacturing journal entry.
- **Rollout:** a cut-off date; posting from that date; nothing back-posted except the approved catch-up.
- **Rollback:** switch postings off; posted entries are reversed by reversal entries, never deleted.

---

## 9. Risks, edge cases and acceptance criteria

### 9.1 Risks

| Risk | Mitigation |
|---|---|
| Material costs stay at last purchase price | Labelled as reference cost until the switch; P0.4 is a prerequisite of C and D |
| Labour understated while new runs skip CNAS | C-06; the burden shown in management reports only |
| Hours booked on days payroll does not pay (F6) | A0 aligns the rule before costs are final |
| Wages visible to production users | Server projection; order totals only without `payroll.manage` |
| One voucher counted on two orders (F9) | Cap per voucher in A0 |
| Late payroll corrections move a closed cost | Never: late-cost records only |
| A wrong account posts | Account map seeded empty; unmapped rows block Phase D; simulation first |
| Finished goods sold before costing is live | Stock operations stay off (C-12) |
| Front-loaded issues distort release values | §2.9 rule; the final release carries the remainder |
| SQLite and SQL Server compute differently | Money computed in memory; the same tests on both; provider smoke |
| Other sessions edit the same files | Separate worktree; rebase on `origin/main`; status docs updated |

### 9.2 Edge cases the tests must cover

An order completed with nothing accepted (all rejected); an order cancelled after issues (everything
returned first); bookings across two payroll months; an employee in two branch runs for one month; a run
reversed after its rate was used; a paid run corrected next month; a declared-mode employee; overtime in
the month; a material at reference cost 0; an untracked material; a unit conversion on issue and release;
a release followed by a negative finished stock; an expense larger than its voucher; two releases sent at
once; rounding residue on the final release; an order spanning a month end (WIP entry and reversal).

### 9.3 Acceptance criteria

**Phase A**
- The worked example of §2.12 gives exactly the figures shown, to the third decimal.
- Variances always sum to final cost − estimated unit cost × Qa.
- Each element of a provisional cost says whether it is provisional and why.
- A production user without `payroll.manage` never receives a per-employee rate or amount.
- Another line's order answers 404 on every costing route.
- No table outside costing changes after any costing request (architecture and database tests).
- The same results on SQLite and on SQL Server.

**Phase B**
- Completion approval writes one final cost sheet. Approving a payroll run, reversing one, editing a product's
  cost or changing the BOM afterwards leaves it byte-identical and writes a late-cost record.
- Releases carry their production value and their sum plus scrap kept plus the remainder equals C.

**Phase C**
- Every simulated entry balances per entry and per branch.
- An unmapped account appears as unmapped and is never guessed.
- `LedgerEntries` is identical before and after a simulation.
- The ledger tie-out (R8) reconciles to zero for a scripted month.

**Phase D**
- Each source document posts once (a retry posts nothing new).
- The catch-up entry equals the reviewed pending total.
- Merchandise postings are unchanged: the existing posting tests pass untouched.
- Turning postings off stops new entries; reversing a document posts a reversal.

---

## 10. Out of scope

The Maintenance module; pricing and markup policies; customer manufacturing contracts and advances (D44–D46);
P0.2 and P0.3 (each awaits its own approval); any change to payroll calculations or `Product.CostPrice`
behaviour.

**Status:** plan delivered 2026-10-26; Phase A built the same day on the owner's approval (see
[costing-phase-a.md](costing-phase-a.md)). Phases B to D and step A0 wait for their own approval, and every
accounting choice for the accountant's answers to section 7.
