# Phase C — the accountant's open choices as settings, then the practice ledger

*Started 2026-11-08. The owner approved starting Phase C "with each C-xx answer as a setting" (owner, 2026-11-08:
"for the things that need a decision, make it settings and the admin can choose"). The plan is
[costing-plan.md](costing-plan.md) §4, §7 and §8 (Phase C).*

Phase C is split in two:

| Step | What | State |
|---|---|---|
| C1 | The choices as stored settings the engine reads; the account map, seeded empty; Manufacturing › Costing settings | Built 2026-11-08, this page |
| C2 | The practice ledger: the entries of the chosen model per event, computed on read and never in `LedgerEntries`; month-end work in progress; the ledger tie-out (R8) | Built 2026-11-09, below |

## C1 — what was built

**Settings** (`ManufacturingCostingSettings`, one row). The first option of each is the suggested answer and
the default; with the defaults, every figure is what it was.

| Choice | Options | Question | Who follows it |
|---|---|---|---|
| Accounting model | Charges by nature, production stocked · perpetual work in progress | C-03 | The practice ledger (C2) |
| Overhead per labour hour | An amount; 0 charges none | C-08 | Cost previews, reports, new frozen sheets |
| Value of a partial release | Standard material + conversion to date (§2.9) · the estimated unit cost · cost to date | C-11 | Same |
| Missing employer share | None · a burden % in reports only · in stock value too | C-06 | Reports-only: previews and reports. Stock value: also frozen sheets and the practice ledger |
| Period-end work in progress | Every month, reversed on the 1st · year end only | C-15 | The practice ledger (C2) |
| Costs known after completion | Stay in their charges, reported · to a variance account | C-10 | The practice ledger (C2) |
| Planned against actual | Management figures only · a variance account | C-13 | The practice ledger (C2) |

Not settings, because nothing could follow them yet (C-05 became one on 2026-11-09, once bookings marked their overtime
hours — see "Labour absorption (C-05)"): C-07 (vouchers carry no tax split), C-09 (scrap recovery values are not recorded), C-12
(stock operations stay a server switch until Phase D), C-14 and C-16 (answered by the account map and the
valuation shadow's gap). C-01 and C-17 are the valuation shadow's settings
([valuation-shadow.md](valuation-shadow.md)).

**Account map** (`ManufacturingAccountMapping`, one row per slot): raw-material stock and consumption, supplies
stock and consumption, semi-finished, finished goods, scrap, work in progress, production stocked, labour,
overhead and expenses absorbed (perpetual work in progress), variances. Seeded empty; an account must be active
in the chart; an empty slot stays unmapped and is never guessed.

**Frozen sheets.** A B1 sheet records the policy it was frozen under. The two new choices are written into its
capture only when they differ from the default, so a sheet frozen under the defaults hashes exactly as before.
When a choice changes after a freeze, the live comparison says "not comparable: a costing choice changed since
the freeze" instead of reporting the difference as drift.

**Never approved.** The policy built from these settings is never `Approved`: a choice made while the
accountant has not answered is not a signature, so no sheet reads as an approved cost.

| Route | Permission |
|---|---|
| `GET /api/manufacturing/accounting/settings`, `GET …/account-map` | `manufacturing.costs.view` or `reports.accounting` |
| `PUT /api/manufacturing/accounting/settings`, `PUT …/account-map` | `accounting.manage` |

The page, Manufacturing › Costing settings, is offered with `manufacturing.costs.view`, like the other costing
pages (manufacturing is never opened by role); its fields are editable only with `accounting.manage`.

Migration `ManufacturingCostingChoices`, both sets: two new tables, nothing else.

### Verification (2026-11-08)

| Check | Result |
|---|---|
| New tests (`CostingChoicesTests`: the three release rules on the worked example, the burden, defaults change nothing, settings checked and followed by the cost preview through its route, the account map) | 5 of 5 |
| Full backend suite, three namespace groups | 532 + 468 + 716 passed, 14 skipped (SQL Server only), 0 failed |
| SQL Server facts (throw-away 2022 container) | 10 of 10 |
| Provider smoke on a fresh demo-seeded SQL Server database | 250 passed; migration applied; the four routes saved and read back the choices and a map line |
| Angular | 1,203 of 1,203 (3 new in `costing-settings.component.spec.ts`); `ng build` passes, initial bundle 795.78 kB (the page is in the manufacturing chunk) |
| CI | Not run: GitHub refuses to start jobs ("recent account payments have failed or your spending limit needs to be increased"), since `d1e9ec8` |

## C2 — the practice ledger (built 2026-11-09)

Manufacturing › Practice ledger, `GET /api/manufacturing/accounting/practice-ledger?year=&month=&lineId=`. One month
of the entries the admin's chosen model would make for production. It is **computed on every read and stored
nowhere**: no table, no `LedgerEntries` row, no migration. The plan's "shadow journal tables" (§8 Phase C) became
unnecessary once every figure can be recomputed from the events; Phase D will post through `LedgerPoster` instead.

**Entries** (`Application/Manufacturing/PracticeLedger.cs`, pure; gathered by `Infrastructure/Manufacturing/PracticeLedgerReport.cs`):

| Event | Charges by nature (C-03 suggested) | Perpetual work in progress |
|---|---|---|
| Issue (MI-), per stock group | Dr consumption (raw or supplies) / Cr its stock; a semi-finished product Dr production stocked / Cr semi-finished stock | Dr work in progress / Cr the stock |
| Return (MR-) | The issue turned round | Same |
| Scrap (MS-) | None | None |
| Labour, overhead, order expenses | None: payroll and the vouchers charge them | Dr work in progress / Cr labour, overhead, expenses absorbed — the month's increase in each, per order |
| Release (OR-) | Dr finished goods / Cr production stocked | Dr finished goods / Cr work in progress |
| Month end, open orders (C-15) | Dr work in progress / Cr production stocked for cost to date − released; reversed on the 1st. "Year end only": December's end, reversed on 1 January | None |
| An order closed (completed or cancelled) | Nothing left over: the last release carries the remainder, and with no release the cost stays in the charges | What it leaves in work in progress → Dr variance / Cr work in progress |
| Costs after the close (C-10) | Listed; already in their charges | Listed; with "variance account", Dr variance / Cr the absorbed account; else they stay in their charges |

**Values.** An order's cost at a moment is the cost engine run on what was recorded before that moment
(movements, bookings by day, expenses, releases), with the rates known today and the employer burden only when the
admin put it in stock value. Each material document is rounded once, per stock group. A release is valued as the
order's sheet values it at the order's close, or today while it is open, so an open order's past releases can move
until it closes (said on the page); the last release of a closed order carries the remainder, so an order's entries
add up to its cost exactly. Each entry is dated on its company day and belongs to the order's location.

**Checks** (R8), each pass or "to review": every entry balances; issues less returns equal the stock credits; releases
equal the finished-goods debits; the change in work in progress equals the register (open orders' cost to date less
released) at the month's end less at its start; every slot the month uses names an account. A slot with no account
is shown "not mapped" and never guessed: Phase D cannot post while one is.

**Not in C2:** scrap recovery (C-09, not recorded), the cost of finished goods sold (waits for the valuation switch),
and planned-against-actual variances (C-13: releases are at actual cost, so there is nothing to post; said when the
admin chose a variance account).

**Access.** `manufacturing.costs.view` on the route; in the service, pay-rate access too (`payroll.manage` and
`manufacturing.costs.rates.view`), because every entry carries one order's labour — the cost history's rule
(`payroll_details_withheld`). Orders of lines out of reach are left out. The sidebar offers it with the cost history's
gate. Off with `Manufacturing:Costing`.

### Verification (2026-11-09)

| Check | Result |
|---|---|
| New tests (`PracticeLedgerTests`: both models on the plan's worked example, the reversal and year-end choice, the closed order's remainder, late costs, the month built from real events through the route into the next month, the account map, the refusals) | 6 of 6 |
| Security fixtures (route plan, legacy snapshot, counts 317 → 318) | 237 passed |
| Angular | 1,207 of 1,207 (2 new in `practice-ledger.component.spec.ts`); `ng build` passes, initial bundle unchanged at 795.78 kB |
| Migration | None |

## The accountant's panel (2026-11-09)

The owner: "make everything as settings and accountant set it in cpanel", then "finish please as recommended". Every
remaining question of §7 is now answered in Manufacturing › Costing settings, under `accounting.manage`; each default is
the suggested answer and changes no figure:

| Setting | Question | Default | Effect |
|---|---|---|---|
| Order expenses before recoverable VAT, at a rate | C-07 | Before VAT, rate 0 | The engine takes `amount × 100 ÷ (100 + rate)` |
| Rejects kept as scrap recover a share of their standard material | C-09 | None | At completion; the practice ledger books it to scrap stock |
| Labour absorption | C-05 | Blended actual rate | A choice since 2026-11-09: or the overtime premium to overtime hours only — see "Labour absorption (C-05)" below |
| Production may move stock | C-12 | Off | Replaces `Manufacturing:StockOperations` as the switch; the configuration still opens test databases |
| The accountant confirms the choices | §7 | Not confirmed | Confirmed, cost sheets with final inputs read approved; saved while confirmed, the saver confirms |
| Post production to the ledger from month | Phase D | None | See below |
| Account slots: valuation difference, opening adjustment | C-16, C-02 | Unmapped | Ready for the valuation switch |

Frozen sheets record C-07 and C-09 only when not default, so earlier sheets hash as before.

## Labour absorption (C-05, 2026-11-09)

The owner's rule (2026-11-09): every accounting decision is a setting the accountant sets in the panel, the default
today's behaviour; where data is missing to honour an option, add what is needed. C-05 asked for the overtime premium to
be kept to overtime hours, which bookings could not say. Now they can.

**What a booking records.** `LaborBooking.OvertimeHours`: how many of the booking's hours were overtime, from 0 (the
default) to its hours — hours, not a flag, because a person often works a normal day and two more on the same row, and
payroll prices overtime in hours (a band counted in days is that many full days). Entered in the daily record's "add
hours" dialog ("Of which overtime", optional), sent as `overtimeHours` on `POST …/logs/{id}/rows/{rowId}/bookings`,
refused outside 0…hours (`invalid_overtime_hours`), kept on the booking's history row too. A booking is never edited:
remove it and book again, as for its hours. It is a fact of the day, recorded whatever the costing choice.

**What payroll gives.** For an employee-month whose runs are approved or paid (`LabourRates`), besides the employer cost
C and the paid hours H (§3.3 of the plan, unchanged):

- **P, the overtime premium's employer cost.** On each payslip overtime line, the part above the normal rate:
  amount × (m − 1) ÷ m, with m the band's multiplier read from the run's own settings snapshot by the line's key — the
  engine paid amount = hours × normal rate × m (`PayrollEngine`, step 3), so amount ÷ m is the hours' ordinary pay. When
  the overtime line is in the social base, its premium also takes its share of the line's employer social contribution
  and levy share: (employer social + levy share) × premium ÷ the line's social base. New runs carry no employer social,
  and the Algeria preset no levy, so today P is the gross premium. A key the snapshot does not know, or a multiplier of 1,
  gives no premium: that pay counts as ordinary hours.
- **O, the overtime hours paid** (inside H).
- **M, the hours marked overtime** on every booking of the employee-month, on every order and row.

A month not approved yet has a standard rate: a full month without overtime, so no premium.

**The choice** (`ManufacturingCostingSettings.LabourAbsorption`, Manufacturing › Costing settings, `accounting.manage`):

| Option | A normal hour | A marked overtime hour |
|---|---|---|
| Blended actual rate (suggested, default) | C ÷ H | C ÷ H — the same; marking changes nothing, and the figures are computed exactly as before |
| Premium to overtime hours only | (C − P) ÷ H | (C − P) ÷ H + P ÷ max(O, M) |

Under the second, every paid hour, overtime ones included, is first costed without the premium (an overtime hour's
ordinary pay is an ordinary hour's), and the premium is spread over the marked hours only. Each marked hour carries
P ÷ O — the premium payroll paid per overtime hour — while bookings mark no more hours than payroll paid; if they mark
more, the premium is spread over the marked hours instead (P ÷ M), so it is never absorbed more than once. The C-06 burden,
when chosen, multiplies both rates. A partial release's conversion to date uses the same two rates.

**The two edge cases:**

- **Payroll paid overtime, no booking (or too few) marked.** Normal hours cost (C − P) ÷ H; the premium the unmarked
  overtime hours would have carried, P − (P ÷ max(O, M)) × M, is absorbed by no order: it stays in the wage charges like
  any unabsorbed labour (§3.5). The cost sheet says so (note `labour_overtime_premium_in_charges`, no figure, since it is
  a month's pay); the labour absorption report (R4) gives, per employee with pay-rate access, the overtime hours paid,
  the premium, and the premium no order hour carried ("of which premium not absorbed"), and the month's total.
- **Bookings marked overtime, payroll paid none** (no overtime line, or a month not approved yet). There is no premium
  to spread: the marked hours cost the normal rate, which is then the blended one. The sheet says so with the hours
  (note `labour_overtime_without_premium`); once the month is approved with overtime, the next computation carries the
  premium.

**Privacy** (`CostPrivacy`, unchanged). An order's overtime rate and the R4 premium figures go with the money: only with
`payroll.manage` and `manufacturing.costs.rates.view`. The overtime hours go with the hours: on an order one employee
worked they are withheld from a reader without the rate keys, and the "without premium" note loses its figure.

**Frozen sheets (B1).** The policy records the choice only when it is not the default; a booking records its overtime
hours only when it has some; a rate records P, O and M only on a sheet frozen under the premium choice. A sheet frozen
under the defaults with nothing marked serializes, and hashes, as before. Changing the choice after a freeze makes the
live comparison "not comparable: a costing choice changed"; under the premium choice, a change of P, O or M counts as a
rate change. The practice ledger and Phase D post what the engine values, so they follow the choice.

Migration `LabourOvertimeAbsorption`, both sets: `LaborBookings.OvertimeHours` and `LaborBookingChanges.OvertimeHours`
(decimal 9,2, default 0) and `ManufacturingCostingSettings.LabourAbsorption` (int, default 0, the blended rate).

### Verification (2026-11-09)

| Check | Result |
|---|---|
| `LabourAbsorptionTests` (engine: blended unchanged with marked hours, premium to marked hours, the month absorbed to the dinar, more marked than paid, no premium said, the burden on the premium, a partial release; routes: the booking's overtime and its refusals, the premium read from an approved run, the panel's choice followed by the cost sheet and R4 with privacy, a frozen sheet's capture under the default and under the choice) | 11 of 11 |
| Manufacturing, payroll and security suites | 589 passed, 8 skipped (SQL Server only), 0 failed |
| Angular | 1,211 of 1,211 (2 new: the record's overtime hours, the panel's choice); `ng build` passes, initial bundle 795.78 kB |
| Provider smoke on SQL Server | Not run here |

## Phase D — posting a month (2026-11-09)

From Manufacturing › Practice ledger the accountant posts a closed month: each practice entry becomes one ledger entry
through `LedgerPoster` (source `manufacturing`, a source id drawn from the month, the posting and the entry's place),
at the accounts of the map. A month posts when it has ended, is on or after the chosen first month, follows a posted
month (unless it is the first), passes every check — so every slot it uses names an account — and the poster reads
every line. The first month leaves out the reversal of a month that never posted. Closed books refuse it. "Reverse"
takes the last posted month back with mirror entries (never deletes) and the month may post again under its next
generation (`ManufacturingMonthPostings`). Routes: `POST …/practice-ledger/post` and `/reverse`, `accounting.manage`,
plus pay-rate access in the service (the entries carry labour).

The switch of sales costing to the weighted average (C-01) is its own setting, off by default, on Reports › Stock
valuation (trial): see [valuation-shadow.md](valuation-shadow.md), "C-01 — the switch, as built". It posts the opening
adjustment to the `opening_adjustment` slot and the negative-stock gaps to the `valuation_difference` slot.

Migration `ManufacturingAccountantPanel`, both sets: nine settings columns and the postings table.

### Verification (2026-11-09)

| Check | Result |
|---|---|
| `AccountantPanelTests` (VAT basis and scrap recovery in the engine, the scrap entry in both models, the panel's confirmation, switch and first month, a month posted, refused twice, reversed and posted again) | 4 of 4 |
| Costing, practice ledger and security suites | 309 passed |
| Provider smoke, fresh demo-seeded SQL Server 2022 | 250 passed; migration applied; the settings, the panel switch and a month posted, refused and reversed through the API |
| Angular | 1,209 of 1,209; `ng build` passes, initial bundle 795.78 kB |
