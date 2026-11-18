# Manufacturing costing — accountant sign-off

Prepared 2026-10-26 from section 7 of [costing-plan.md](costing-plan.md), for the accountant. Every row is
**pending**: nothing is approved, nothing posts, and no row is built on until it is signed. A signature
authorises a method; it does not post anything by itself. Rows that repeat a decision already on the
"Accountant sign-off" tab of the planning document (https://claude.ai/artifact/Ny3Rhfe1J9EnyGm7xWbpDj) cite
it (D9, D13, D33, D43, D45, D47); answer each once, in either place.

## 1. What the system does today, and what Phase A shows

| Area | Today |
|---|---|
| Stock value | One cost per product for the whole company, replaced by each receiving note's purchase price or typed on the product (last purchase price) |
| Cost of a sale | That cost when the sale reaches the server: Dr **600** / Cr **310** |
| Stock accounts | **310** for everything owned, **318** for scrap |
| Wages | Posted when a payroll run is approved: **631**, **635**, **641** against **421**, **425**, **431**, **442**, **447** (and **471**) |
| Manufacturing | Records materials, hours, expenses and quantities. Values none of it in the books and posts nothing; stock operations are off in production |

Phase A (built, not deployed) computes a **cost preview** per order on request. It writes nothing and posts
nothing. Every figure carries one of three labels:

| Label | Meaning |
|---|---|
| **Estimated** | From the bill of materials and routing at the order's planned quantity, at reference costs and the standard labour rate. Never an actual cost |
| **Provisional** | An actual cost to date in which at least one element rests on an input that is not approved: materials at reference cost (last purchase price, until C-01 is signed and the switch made), labour at the standard rate (month's payroll not approved), hours or materials with no value, no approved overhead rate, rejects with no recovery value recorded |
| **Approved actual** | Every element rests on approved inputs (approved payroll with its employer share, expenses on approved or paid vouchers, an approved overhead rate) **and** you have signed the costing method. Until Phase B it is still recomputed on every read and is never frozen |

Until you sign the method (C-01, C-05 to C-08, C-11), **every actual cost is provisional**: the system
refuses to call any cost approved under an unsigned method, whatever its inputs.
A completed order's cost is labelled "provisional (some inputs not approved)" in that case, never as final.
An element with nothing behind it reads "Not set", "Not recorded" or "Unavailable", never 0.

## 2. Sign-off checklist (section 7 of the plan)

| # | Item | Question | Options | Recommendation | Decision |
|---|---|---|---|---|---|
| C-01 | Valuation method (D9) | How is stock valued? | Last purchase price / weighted average (CMUP) / FIFO | Weighted average, perpetual, company-wide | |
| C-02 | Opening adjustment (D43) | At the switch, the difference between the opening valuation and 310/318 goes to? | Current-period stock variation / equity (prior-period adjustment) if material | One entry, figure shown to you first; account named by you | Account: |
| C-03 | Accounting model | Charges by nature with production stocked, or perpetual WIP? | Model N / Model W (§3 below) | Model N | |
| C-04 | Accounts by role (D33) | Raw materials, supplies, WIP, finished goods, scrap, consumption, production stocked, own-production sales | The codes in §4 below, or yours | Your codes; nothing is created before you name them | Codes: |
| C-05 | Direct labour absorption | Rate method and denominator | Blended actual rate over paid hours, leave spread over hours worked / overtime premium charged to overtime hours only | Blended actual rate | |
| C-06 | Employer costs (D11) | New payroll runs compute no employer social contribution (CNAS); levies per employee | 1. Labour = what payroll records (no CNAS on new runs) · 2. A costing burden %, in management reports only · 3. The burden in stock value too (not recommended); levies recomputed through the payroll engine or shared by social base | Option 1 in stock value, 2 in reports; levies recomputed | |
| C-07 | Expenses | Amount basis | Before recoverable VAT / as recorded | Before recoverable VAT | |
| C-08 | Overhead | Apply any? Basis, rate, normal capacity; over/under absorption | None / labour-hour rate / machine-hour rate | None until you set a rate | Rate: |
| C-09 | Scrap | Recovery value at release, its stock account, its sale | 358 (D33) / 318 (existing) | Your choice of account; recovery entered per order | Account: |
| C-10 | Late costs (D13) | Costs known after completion | Stay in the charges and reported / a class-6 variance account | Stay in the charges, reported | |
| C-11 | Partial production | How is a partial release valued? | Standard material per unit at issue average + conversion to date per good unit not yet released, final release carries the rest / estimated unit cost / cost to date | The first; the final release carries the remainder | |
| C-12 | Finished goods before live costing | Cost of sale of finished goods until Phase D | Stock operations off / interim typed cost from the last approved unit cost, logged | Stock operations off | |
| C-13 | Variance accounting | Post variances? | No account (management only) / variance account | No account under Model N | |
| C-14 | Internal transfers (D47) | Goods move at average, no sale or tax; transport is the receiving branch's expense through 181 | As proposed / transport in the goods' value | As proposed | |
| C-15 | Month-end WIP | Value open orders | Monthly, reversed on the 1st / only at year end | Monthly | |
| C-16 | Negative-stock gap (D43) | Account for the difference when a later inflow covers a negative quantity | Your account | Your account | Account: |
| C-17 | Rounding | Ledger precision | Three decimals; the final release absorbs the residual | As proposed | |
| C-18 | Algerian requirements | Do the chosen method, accounts, by-nature presentation, stock variation, tax on advances (D45) and the year-end count meet the SCF and the tax rules as you apply them? | Your confirmation or corrections | None: we do not assert regulatory conclusions | |

## 3. The two accounting models (C-03)

**Model N — charges by nature (recommended).** Costs stay where they are charged: materials in consumption
accounts when issued, wages in 631/635/641 when payroll is approved, expenses where their voucher posts
them. Production enters stock through *production stocked* (72) at its cost; open orders are valued once a
month in work in progress against the same account and reversed on the 1st. No variance account: what is
not carried into stock stays in the charges.

**Model W — perpetual work in progress (alternative).** Every cost flows through a WIP account as it
happens and leaves it at release; labour and overhead enter through absorbed-cost accounts you name;
residual differences go to a variance account. It needs credit accounts offsetting wages and overhead
already in their charges (please confirm their SCF presentation), more entries, and it risks counting
wages twice if an absorption is missed.

| Event | Model N | Model W |
|---|---|---|
| Raw material issue | Dr 601 / Cr 311, at issue cost | Dr WIP (331) / Cr raw-material stock |
| Consumable, accessory or packaging issue | Dr 602 / Cr 32x | Dr WIP / Cr supplies stock |
| Semi-finished product issued to another order | Dr 72 (destocking) / Cr 35x | Dr WIP / Cr semi-finished stock |
| Material return | The issue reversed at the order's issue average | Dr stock / Cr WIP |
| Material scrap (offcuts) | None: already consumed at issue | None (stays in WIP) |
| Direct labour | None from manufacturing: payroll approval already posts **631/635/641** | Dr WIP / Cr labour absorbed (account to be named) |
| Order expenses | None: the voucher already posts its expense | Dr WIP / Cr the expense account (reclassification) |
| Overhead | None: absorbed only through the stock value | Dr WIP / Cr overhead absorbed (account to be named) |
| Month-end, open orders | Dr 331 / Cr 72 for cost to date − released; reversed on the 1st | None (WIP is perpetual) |
| Partial release | Dr 35x / Cr 72 at release value | Dr finished goods / Cr WIP |
| Final release and completion | Same, carrying the remainder; anything not releasable stays in the charges | Same; a residual goes to the variance account |
| Rejects kept as scrap | Dr 358 (or **318**) / Cr 72 at recovery value | Dr scrap stock / Cr WIP |
| Rejects with no value | None | None |
| Variance | None (management figure on the cost sheet) | Dr/Cr variance account / Cr/Dr WIP |
| Transfer of finished goods between locations | Dr 35x (receiving) / Cr 35x (sending) at average, as **310** transfers post today | Same |
| Internal transport (D47) | Dr **624** at the receiving branch, balanced through **181**, never marked inter-branch | Same |
| Sale of finished goods: cost | Dr 72 (destocking) / Cr 35x at average | Dr cost of goods sold (account to be named) / Cr finished goods |
| Sale of finished goods: revenue | Dr **411** or cash / Cr 701, tax on **4457** per the tax profile | Same |
| Scrap sold | Dr cash / Cr **756**, as today; its stock leaves at its value | Same |

## 4. Proposed account mapping (C-04, D33)

Codes in **bold** exist in the seeded chart. Every other code is a proposal: **none exists, none is
approved**, and the system will post to whatever you enter in the account map, never to a constant. A code
cannot be changed once something has posted to it.

| Role or event | Stock (SCF class) | Proposed account | Leaves through | Revenue |
|---|---|---|---|---|
| Merchandise: every existing product | 30 | **310**, as today | **600**, as today | **700**, as today |
| Raw material | 31 | 311 | 601 when issued to production | — |
| Supplies: consumable, accessory, packaging, spare part | 32 | 321 / 322 | 602 when issued; 615 when used in a repair (maintenance, out of scope) | — |
| Work in progress, goods | 33 | 331 | Month-end entry against 72, reversed on the 1st | — |
| Semi-finished and finished goods | 35 | 351 / 355 | Enter through 72 at release; leave through 72 at sale | 701 |
| Production scrap with resale value | 35 | 358 (or **318**, C-09) | Against 72 at recovery value | **756** (existing scrap sales) |

Accounts you are asked to **name** (no proposal):

- The counter-account of the opening adjustment (C-02, D43).
- The valuation-difference account for the negative-stock gap (C-16, D43).
- Under Model W only: labour absorbed, overhead absorbed, the variance account and cost of goods sold.
- Under the D13 alternative only: a class-6 production-variance account (C-10).

Existing accounts the matrix uses unchanged: **181**, **310**, **318**, **411**, **419**, **421**, **425**,
**431**, **442**, **447**, **471**, **4457**, **512**, **530**, **600**, **624**, **631**, **635**,
**641**, **700**, **756**.

**Alternative to the role scheme:** keep 310, 600 and 700 for everything. Less change, but the statements no
longer separate materials, supplies and finished goods as the chart intends.

## 5. Tax treatment to confirm

| Point | Proposed treatment | Where |
|---|---|---|
| Purchases into stock | Inflow at invoice cost **before tax** | D43 |
| Order expenses in cost | Before recoverable VAT (Phase A counts the amount as recorded until you answer) | C-07 |
| Expense voucher cap | Expenses linked to one voucher never exceed its amount (before tax, once C-07 is answered) | Plan §2.4, step A0 |
| Sale of finished goods | Revenue on 701 before tax, tax on **4457** per the company's tax profile; stored prices are before tax | §3, D45 |
| Customer advances on manufacturing orders | Dr 530/512 / Cr **419** at receipt; at delivery Dr 411 / Cr 701 and Cr 4457, then Dr 419 / Cr 411 up to the delivery; a cancelled contract refunds (Dr 419 / Cr 530 or 512) or leaves a credit (Dr 419 / Cr 411). Alternative: credit 411 directly | D45 |
| **Tax on advances** | Our understanding: tax on goods falls due at delivery and on services at payment, so an advance on goods carries no tax at receipt and an advance on services (a repair) does. **Please confirm or correct.** The rule will be a setting, not code | D45, C-18 |
| Transfers between the company's own locations | At cost, no sale, no tax, no invoice: both ends share one tax number | D47, C-14 |
| Scrap sold | Cr **756** as today | §3 |
| Overall conformity | Method, accounts, by-nature presentation, stock variation, tax on advances and the year-end count against the SCF and tax rules | C-18 |

## 6. Employer social contributions (C-06, D11)

New payroll runs compute no employer social contribution (CNAS) since 2026-10-21, and the Algeria preset's
30 % employer rate is a placeholder, "not the real split". Phase A therefore:

- costs labour at what payroll records (option 1), so labour is **understated** by the employer share on
  those months;
- marks each such employee-month "without employer social contribution (missing)" on the order's cost
  sheet and on the labour-cost report, and keeps the order's cost provisional in words, never as a zero
  charge;
- applies no burden %, because none is approved (option 2 needs your rate).

Please choose option 1, 2 or 3, and give the burden % if you choose 2.

## 7. Also pending on the existing sign-off tab (Phase 0)

Not part of section 7, but unresolved and for the same signature: P0.2 (group reports include payroll cash
paid from another branch's box), P0.3 (direct goods invoices post Dr 600 / Cr 310 at today's method until
D9), P0.4c (refunds reverse the cost the sale posted), and service income (keep **700**, or name a service
account such as **706**).

## 8. What happens after you sign

| Phase | Needs | Writes |
|---|---|---|
| A (built) | — | Nothing: previews only |
| B | Owner approval | Frozen cost sheets and late-cost records only |
| C | Your answers to C-01 to C-17; the weighted-average shadow (P0.4a/b) | A shadow journal, never the ledger; at least one month reviewed with you |
| D | Every row signed; the valuation switch; Phase C's month reviewed | The ledger, through the existing poster, from a cut-off date |
