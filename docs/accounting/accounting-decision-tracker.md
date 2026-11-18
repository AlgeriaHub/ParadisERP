# ParadisERP — Accounting Decision Tracker

*Started 2026-10-31. Updated 2026-10-31 with the owner's decisions, and 2026-11-01 with the internal SCF review of the chart (`scf-chart-review.md`) and the owner's answers of that day. One row per decision in "ParadisERP — Questions for Our Accountant", in the same order. Every original ID is kept; the original wording of revised questions is in `accountant-questions-history.md`. 2026-11-02: the owner's decision that VAT is optional on every bill, rental bills included (TAX-01, REN-01); the first-meeting questions also exist in Arabic (`accountant-questions-first-meeting.ar.md`, and `.ar.pdf` with the questions only). 2026-11-05: the accountant's first answers, through the owner, and the owner's decisions of that day (see "Answers of 5 Nov 2026").*

## Statuses

| Status | Meaning |
|---|---|
| **Approved by Owner** | The owner has decided the business rule. It may still need building, and parts the law decides may still be open (see "Still open"). |
| **Owner Workflow Confirmed — Compliance Pending** | The owner has confirmed how we work; the accountant must confirm it is legally right (tax, CNAS, IRG, SCF). |
| **Internal Technical Verification** | Our own team must check the system (code, settings, live data). Not a question for the accountant, unless noted. |
| **Pending Accountant** | Waiting for the accountant's answer. Silence is not approval. |
| **Pending Owner** | Waiting for the owner's decision. Silence is not approval. |
| **Implemented and Verified** | Built, tested and checked. **No row has this status yet.** |

"Owner decision" records what the owner approved. "Built today?" records what the code actually does, from the code review of 2026-10-31 (`accounting-code-findings.md`); it is filled for decided rows only. An approved rule is **not** the same as built: most approved rules still need software changes.

## Answers of 5 Nov 2026

The accountant's first answers, as the owner relayed them, and the owner's decisions of the same day:
- **REN-01:** 19 % on the whole rental bill (rent, late days, repairs, wrecked and missing items) and on the selling
  bill; optional when the bill is made, at the till by its "taxed" tick. Already how the code bills; production's hire
  rate is still 0 %, to be set to 19 in Rentals › Settings.
- **TAX-01 / REN-01, the owner's rule (same day, kept in their words in `../project-memory.md`):** VAT only when the
  customer asks for a tax invoice — the seller turns it on when creating the rental contract, the sales invoice or the
  manufacturing order, and the rate applies to the whole total; otherwise it stays off. Confirmed the same day: every
  new bill starts without VAT and every seller decides, no permission either way. Built 2026-11-05 (deployed).
- **PAY-01:** CNAS and IRG are ignored on the payslip, as today.
- **COR-E01:** a closed month is corrected in the current month, naming the original; the closed month stays as it is.
  An act that belongs to a closed month (a late cheque confirmation, a payroll run, a rental extension) is booked on the
  first open day, saying so (owner, same day).
- **SAL-03 (owner):** discounts stay booked net; the sales report shows the discounts given.
- **C-01 and the SAL-02 catch-up entry (owner):** wait for the accountant.
- **Manufacturing (owner):** continue. The costing screens are switched on in production (read-only, nothing posts),
  and the costing questions C-01 to C-17 go to the accountant in Arabic before Phase C.

## Summary (31 Oct 2026)

**Closed (no question left for the accountant)**
- GL-01: ParadisERP is the official books.
- D33: the chart of accounts is reviewed internally. Review done 2026-11-01 (`scf-chart-review.md`): 8 of 37 accounts are under the wrong SCF heading. The fix is still to do (Internal Technical Verification).
- SAL-02: sales with a missing cost are allowed, flagged and reported. Built 2026-11-01 (live since 2026-11-02).
- CLO-01: months are closed after review, with controlled corrections. Built 2026-11-01 (live since 2026-11-02): the Admin closes and reopens with a reason; a late till sale is booked on the first open day.
- C-04, REN-18, PAY-02: the account numbering is internal, like D33 (owner, 1 Nov 2026), and removed from the questionnaire. The SCF review answers it: the eight payroll accounts match; 707 and 756 are wrong; the C-04 codes match once 310 moves.

**Partly resolved (owner decided; one part still open)**
- TAX-01: feature confirmed; which sales may legally be untaxed → accountant. Owner, 2 Nov 2026: VAT is optional on every bill, rental bills included (see REN-01).
- TAX-05: tax on the total confirmed; deductible purchase VAT → accountant, then build.
- TAX-06: reporting confirmed; G50 line coverage → internal check (needs a G50 copy).
- TAX-09: owner says stamp duty is configured; the code review did not find it → internal check; rule → accountant.
- D45 and REN-03: owner's VAT timing recorded (REN-03 changed by the owner on 10 Nov 2026: hire VAT when invoiced, i.e. at the issue note, as today); legal timing and part payments → accountant (asked together). D45 (advances) and SAL-12 built as settings on 9 Nov 2026 (M7b), defaults = today.
- REN-01: hire carries VAT; the rate → accountant; live setting → internal check. Owner, 2 Nov 2026: VAT is optional when the bill is made, rental bills too. Built 2026-11-03: the choice is on the rental contract, untaxed needs `finance.invoices.untaxed` (live since 2026-11-03).
- REN-02: full rent on day one; period-end adjustment → accountant.
- REN-12: fixed assets; depreciation policy → accountant; register built 2026-11-09 (M7a, deployed 9711b06) as settings the accountant sets, off by default — [fixed-assets.md](fixed-assets.md). Declining balance built 2026-11-10 (not deployed) as a choice per type with a company default, straight line the default.
- PAY-01: payslip kept; CNAS/IRG compliance → accountant.
- SAL-17: liability policy approved; recording built 2026-11-01 (live since 2026-11-02); expiry rule and VAT timing → accountant.

**Still open for the owner**
- ~~C-01 / D9: stock valuation.~~ 5 Nov 2026: waits for the accountant.
- ~~SAL-03: discounts net or gross.~~ 5 Nov 2026: net (option A).

**Still open for the accountant:** every other row (Pending Accountant). Nothing was assumed from silence.

**Internal technical findings to act on** (details in `accounting-code-findings.md`)
- Stamp duty: the owner says it is configured, but no stamp-duty setting, calculation, printing or posting was found in the code, on any branch. A second search on 2026-11-01 for timbre, stamp and طابع found nothing either. The owner explained (1 Nov 2026) that it is typed in by hand as a product or fee line on the sale or invoice. The live data (live check of 2026-11-01, read-only) has no product named timbre, stamp, طابع or دمغة, and no back-office invoices at all, so the owner should say which product or line is used. A product line posts like any sale: to 700 as income, and with VAT if the product's rate is not 0 %. It also counts in turnover and margin. Whether stamp duty may be booked that way is the accountant's (TAX-09); the line itself is to be found in the live data.
- G50: no report for the declaration as such. The sales summary covers till sales only and puts untaxed sales in the same 0 % line as exempt goods.
- Purchase VAT: none is recorded (no 4456). Supplier balances are off by the VAT once paid.
- Rental VAT is posted in full when the goods go out. The owner's workflow (at payment and invoice) needs a change.
- Rental and stock entries are dated when keyed, not on the document's date. **The rental issue entry is fixed** (REN-02, 2026-11-01, live since 2026-11-02): it is now dated on the first day of the rental period. Return, stock and voucher entries still use the keying date; that is CLO-04 (accountant).
- Till sales start untaxed, with no permission behind the choice. The untaxed-invoice permission is bypassed for invoices raised over a sale. Both are control findings for the owner, not accountant questions. **Both built 2026-11-02 on the owner's choice (live since 2026-11-02):** "sell untaxed at the till" (`sales.untaxed`) is the Admin's and Manager's by role and a cashier's own tick on the users screen, given to every cashier on file the day it arrives; invoicing a sale made without tax needs `finance.invoices.untaxed`.
- **The live hire VAT rate is 0 %** (live check of 2026-11-01, read-only). Rental income booked so far, 1,968,286 DA net in 706, carries no VAT, although the owner decided hire carries VAT (REN-01). Each contract line keeps the rate it was signed with, so contracts signed before the rate is set stay at 0 %. The rate goes in Rentals settings once the accountant gives it.
- CNAS/IRG are off in code, not in a setting. A statutory payment can be recorded with nothing accrued.
- The SCF review of the chart (D33): done 2026-11-01. 8 of 37 accounts are under the wrong SCF heading: 310 (merchandise belongs in 30), 318, 4181 and 4182 (liabilities under 418, an asset heading; belong in 419), 6061 and 6068 (French PCG codes; belong under 607), 707 (no such SCF account) and 756 (the SCF's 756 is recoveries on written-off debts; scrap sales are 703). Owner (1 Nov 2026): fix all eight together at the cutover, the first day of a financial year, with the stock-method switch (D43) and the move of rental equipment out of 310 (REN-12); 756 waits too. Each fix is still a software change with its own go-ahead. Details in `scf-chart-review.md`.

**Accountant-only decisions (first meeting)**
- TAX-01, TAX-05, D45 + REN-03, REN-01, REN-12, REN-02, PAY-01, C-01 (view only), CLO-08, COR-E01, COR-06, SAL-17, TAX-09, TAX-06.

**Future software changes already implied by owner decisions** (not started; each still needs its own go-ahead)
- ~~SAL-17: record gift-card sales as a liability, income on use.~~ Built 2026-11-01 (live since 2026-11-02). Expiry waits for the accountant.
- ~~SAL-02: a missing-cost flag and an incomplete-margin report.~~ Built 2026-11-01 (live since 2026-11-02).
- ~~CLO-01: period close, a block on back-dated postings, controlled corrections with an audit trail.~~ Built 2026-11-01 (live since 2026-11-02). Correction dating still COR-E01.
- ~~REN-12: a fixed-asset register, depreciation, and the transfer of rental equipment out of 310.~~ Built 2026-11-09 (M7a, deployed `9711b06`): settings, switch-over, straight-line depreciation posted by month, disposals at net book value — [fixed-assets.md](fixed-assets.md). Capitalised repairs built 2026-11-09 (M7d, deployed `5454199`); declining balance built 2026-11-10 (deployed `eea95fa`). Till sales of hire items still to come.
- ~~REN-03: post rental VAT at payment and invoice instead of at issue.~~ Not needed: owner, 10 Nov 2026, hire VAT when invoiced (the issue note), which is today's posting.
- TAX-05: deductible purchase VAT (after the accountant's answer).
- ~~REN-02: date the rental entry on the rental start day.~~ Built 2026-11-01 (live since 2026-11-02).
- GL-01 (official books) implies a numbered journal (GL-06) and a year-end close (CLO-08). These are not yet approved and are asked separately.

## Decisions

| ID | Module | Question (as first asked) | Our recommendation | Priority | Owner decision (31 Oct 2026) | Built today? | Still open | Accountant response | Status |
|---|---|---|---|---|---|---|---|---|---|
| SAL-01 | Sales / Ledger | Service income: keep in 700 or own account? (706 is rental today) | Goods/service flag; services to a services sub-account; rental keeps its own | Important |  |  |  |  | Pending Accountant |
| SAL-02 | Sales / Ledger | Cost of goods sold at sale; products with no cost; offline cost date | Post nothing for no-cost items but list them monthly; use sale-day cost | Critical | Allow selling with missing/zero cost; flag for correction; report incomplete margins; never block. | Yes, live since 2026-11-02 (built 2026-11-01) — each sale line is flagged `CostMissing` when it reaches the server with a stock-tracked product at cost 0; the migration flags past lines whose sale posted no 600; Reports › Sales without a cost (`reports.accounting`) lists them by product, with the cost entered since and an estimate that is never posted | 5 Nov 2026, the owner: waits for the accountant. Not decided: whether a catch-up cost entry (Dr 600 / Cr 310) is posted once a flagged product's cost is known (owner / accountant). Past sales that posted some cost are not flagged (one sum per sale). |  | Approved by Owner |
| SAL-03 | Sales / Ledger | Discounts: net in 700 or separate account? | Keep net; discounts in reports | Important | Not decided. Option A recommended, not approved. 5 Nov 2026, the owner: option A, keep net; the discounts given are shown in the sales report. | Net posting today | Built 2026-11-05: the sales report shows the discounts given, already taken off the totals. |  | Approved by Owner |
| SAL-04 (also TRS-15, GL-12) | Sales / Ledger | Cash rounding account (758 both ways) and outside tax | Keep 758 both ways | Later |  |  |  |  | Pending Accountant |
| SAL-06 | Receivables | Due date and ageing for uninvoiced till sales on account | Customer payment terms; automatic due date | Important |  |  |  |  | Pending Accountant |
| SAL-07 | Receivables / Invoices | When to invoice a till sale (incl. cash/card), invoice date, numbering | Invoice any sale; invoice date = sale date; one continuous sequence | Critical |  |  |  |  | Pending Accountant |
| SAL-08 | Receivables | Split 411 for companies vs persons? | One 411 with per-customer detail | Later |  |  |  |  | Pending Accountant |
| SAL-09 | Receivables | Auto-apply unallocated receipts to oldest invoices? | Yes, oldest first, editable | Important |  |  |  |  | Pending Accountant |
| SAL-10 | Receivables | Instalment plans, interest/late fees | Income at sale; schedule of due dates; extra charges as financial income | Important |  |  |  |  | Pending Accountant |
| SAL-12 | Receivables | Customer advances: 419 or 411? | 419, consistent with D45 | Important |  | Built 9 Nov 2026 as a setting (M7b, Finance › Settings › Customer advances, `accounting.manage`), **411 by default** = today: an advance is a receipt allocated to nothing. On "419 until the invoice", a receipt of the kind Advance posts Dr 530/512 / Cr 419 (the customer's) and is applied from the receipt to an invoice (Dr 419 / Cr 411, the invoice reads paid) or released to 411 before a refund; statement and ageing read 411 + 419 together. See docs/accounting/advances.md. | The accountant answers A or B; A = choose 419. Kept advances: see SAL-13. |  | Pending Accountant |
| SAL-13 | Receivables | Order cancelled after advance: refund and kept part | Refund by voucher; kept part to other income with reason | Important | 10 Nov 2026: yes, answer A. | Built 10 Nov 2026 as a setting (Finance › Settings › Customer advances, `accounting.manage`), **Not allowed by default** = today. "Kept as other income" needs an active class 7 income account the accountant chooses (409 `accounts_not_mapped` otherwise). The receipt's ⋯ "Keep part of the advance" (key `finance.vouchers.approve`, required reason) posts Dr 419 / Cr income (from an advance) or Dr 411 / Cr income (from a receipt's unallocated credit), source `customer-advance-kept`; with VAT when received the kept part's VAT stays due on 4457. The rest is refunded by payment voucher. Statement shows "Advance kept — reason" as a debit row. See docs/accounting/advances.md. Deployed `f844fc7` (2026-11-10). | The accountant confirms the setting and names the account; whether VAT is due on a kept advance held on 411 or untaxed (posted wholly to income today). |  | Pending Accountant |
| SAL-14 (also CLO-02) | Sync / Ledger | Offline sales arriving late: booking date/month | Sale date if month open, else first day of open month | Critical |  |  |  |  | Pending Accountant |
| SAL-15 | Receivables / Branches | Customer pays at branch A for branch B invoice | Clear debt at B, cash at A, 181 between them | Important |  |  |  |  | Pending Accountant |
| SAL-16 | Loyalty | How to account for loyalty points (4182 goes debit today) | Spent points as discount, or expense at earning | Important |  |  |  |  | Pending Accountant |
| SAL-17 (also GL-05) | Gift cards | Gift card sale money not recorded; liability and expiry | Dr cash/Cr 4181 at sale; income at use; expiry to other income | Important | Gift-card money is a liability; revenue on redemption; unused balances stay liabilities; expired balances to income only when legally and contractually allowed. | Yes, live since 2026-11-02 (built 2026-11-01) — a card is sold for cash at a branch: Dr 530 / Cr 4181 and the branch box moves in; Adjust asks each time: a paid top-up (the same), a cash refund (Dr 4181 / Cr 530, box out) or a correction (books nothing). Spending a card already posted Dr 4181. No VAT at the card sale; nothing expires into income | Accountant: expiry rule and VAT timing. Cards sold before this change were never booked: their unused balances are not on 4181 (owner/accountant to decide whether to book them once). 4181 moves to a 419 sub-account at the cutover (D33). |  | Approved by Owner |
| SAL-18 | Receivables | Bad debts: provision and write-off (not built) | Provision by age, write-off when final, separate from credit notes | Important |  |  |  |  | Pending Accountant |
| PUR-01 | Purchases | Is the receiving note enough, or must the supplier's invoice (number, date, HT, TVA, TTC) be recorded? | One combined receiving + invoice form, several products per note | Critical |  |  |  |  | Pending Accountant |
| PUR-02 | Purchases | Is the purchase price always typed before tax? | Always before tax, said on screen; non-recoverable tax in cost only where named | Critical |  |  |  |  | Pending Accountant |
| PUR-04 | Purchases | Supplier delivers fewer than invoiced: what is recorded? | Full invoice on 401, missing part as a claim on the supplier (if PUR-01 C) | Important |  |  |  |  | Pending Accountant |
| PUR-05 | Purchases | Supplier changes the price after receipt | Supplier credit/debit note; split between stock on hand and cost of goods sold | Important |  |  |  |  | Pending Accountant |
| PUR-06 | Purchases | Returning goods to a supplier | Supplier return document: 401 / 310 at the original note's price, tax reversed | Important |  |  |  |  | Pending Accountant |
| PUR-07 | Purchases | Must every receiving note name a supplier? | Yes; a "various suppliers" supplier for one-off shops | Important |  |  |  |  | Pending Accountant |
| PUR-08 | Purchases | Supplier advances: stay on 401 or a separate account? | Keep on 401; reclassify debit balances at year end | Important |  |  |  |  | Pending Accountant |
| PUR-09 | Purchases | Transport, customs and handling: in stock cost or expense? | Expense if small; spread by value into cost if imports are regular | Important |  |  |  |  | Pending Accountant |
| PUR-10 | Purchases | Goods paid in cash via an expense voucher "purchase" | Goods for sale/rent always by receiving note; 6068 only for own supplies | Important |  |  |  |  | Pending Accountant |
| PUR-12 | Suppliers | Invoice discounts and early-payment discounts | Net price on the note; early-payment discount option on the payment voucher | Later |  |  |  |  | Pending Accountant |
| PUR-14 | Purchases | Correcting a wrong receiving note | "Cancel receiving note" undoing ledger, stock and supplier balance; then a new note | Important |  |  |  |  | Pending Accountant |
| C-01 | Stock | Stock valuation method (D9) | Weighted average (CMUP), perpetual, company-wide | Critical | Owner keeps this open. 5 Nov 2026: the owner waits for the accountant. | Last purchase price today | Accountant first, then the owner. Nothing changes until then. |  | Pending Accountant |
| D43 | Stock | Switch date and starting value per product | Start of a financial year after the count; counted qty × last purchase price before tax (or accountant's list) | Critical |  |  |  |  | Pending Accountant |
| C-02 | Stock | At the switch, account for the difference with 310/318 (D43) | One entry, figure shown first; account named by the accountant | Critical |  |  |  |  | Pending Accountant |
| C-16 | Stock | Gap when a later inflow covers negative stock (D43) | Accountant's account; default cost of sales (600) | Important |  |  |  |  | Pending Accountant |
| P0.3 | Stock | Direct goods invoices move no stock and post no cost | Take goods out of stock and post 600/310 at the method in force | Critical |  | Built 9 Nov 2026 as a setting, **off by default** (Reports › Stock valuation, `accounting.manage`): on, a direct invoice takes its tracked goods out of its branch and posts 600 / 310 in its own entry at the cost in force (cost price, or the average once C-01 is on); a hand-made credit note brings goods back at the line's cost. Invoices issued while off are never touched. Typed invoices are closed at the route since 10 Oct 2026, so it acts only if that route reopens (B-F15). | The accountant answers A or B; A = switch it on. |  | Pending Accountant |
| STK-01 | Stock | Negative stock: what is allowed? | Never block sales; block transfers and adjustments below zero; weekly negative list | Important |  |  |  |  | Pending Accountant |
| STK-02 (also CLO-09) | Stock | Count differences: which accounts? | Losses 657, gains to an income account named by the accountant | Important |  |  |  |  | Pending Accountant |
| STK-03 | Stock | Year-end count procedure and valued stock list | Count document per location, approved once; valued stock list at any date | Important |  |  |  |  | Pending Accountant |
| STK-04 | Stock | Scrap (318) value and scrap sales | Write down to recovery value at the move; scrap sale to 756 and out of 318 | Important |  |  |  |  | Pending Accountant |
| STK-05 | Stock | Opening stock value (typed / Excel) | From the last valued stock list (or costs before tax set first); check 310 total | Critical |  |  |  |  | Pending Accountant |
| STK-06 | Stock | Changing a product's cost by hand | Admin only with reason now; computed (no typing) after the switch; refuse cost 0 | Important |  |  |  |  | Pending Accountant |
| REN-01 (also TAX-11) | Rentals | Does equipment hire carry TVA, at what rate? (setting is 0 % by default) | Standard rate unless an exemption applies; to be confirmed | Critical | Equipment rental is subject to VAT. 2 Nov 2026: VAT is optional when the bill is made, on every bill, rental bills included. 5 Nov 2026, the accountant through the owner: **19 % on the whole rental bill** of a taxed contract (rent, late days, repairs, wrecked and missing items), optional per bill as before. | Rate setting exists; **live rate is 0 %** (live check of 2026-11-01, read-only); 706 holds 1,968,286 DA net with no VAT The code already bills it so: every charge of a taxed contract follows the rate its line froze, rent at the issue note and every other charge at the return (`RentalTaxModeTests`, 2026-11-05). | Owner: set the hire rate to 19 in Rentals › Settings (production is still 0 %). New contracts take it, a draft takes it when saved again, contracts already issued keep 0 %. When the VAT is due stays REN-03 (accountant). |  | Approved by Owner |
| REN-02 (also CLO-10) | Rentals | Book the whole agreed period as income at issue, or spread it over the days/year end? | Keep booking at issue; add a year-end "income received in advance" adjustment | Critical | Full rental revenue on the first day of the rental period. | Yes, live since 2026-11-02 (built 2026-11-01) — the issue entry, carry-overs included, is dated midnight (company time) on the first day of the rental period. Entries already posted keep their dates | Accountant: is a month-end or year-end adjustment needed? |  | Owner Workflow Confirmed — Compliance Pending |
| REN-03 (also TAX-12) | Rentals | When is TVA on hire due: issue, invoice or payment? | One rule with TAX (D45 / C-18), made a setting | Critical | Owner, 10 Nov 2026: VAT when invoiced (the issue note), not at payment. | Yes — rental VAT posts in full at issue, which is the invoice | Accountant: legal timing, part payments (asked with D45). No software change needed. |  | Owner Workflow Confirmed — Compliance Pending |
| REN-04 | Rentals | Must every issue/return note be invoiced, and by when? (income is booked without an invoice) | Monthly invoice per customer + report of notes not yet invoiced | Important |  |  |  |  | Pending Accountant |
| REN-05 | Rentals | Early / same-day "not used" returns: credit note on return date, "internal adjustment" when untaxed — valid? | Keep as is; confirm the "internal adjustment" wording | Important |  |  |  |  | Pending Accountant |
| REN-06 | Rentals | Late-return charge: extra rent (TVA, 708) or penalty? | Extra rent, same TVA, keep 708 if acceptable | Important |  |  |  |  | Pending Accountant |
| REN-07 | Rentals | Waived/deducted late days: must the waived amount be shown or booked? | No entry; add a waiver report | Later |  |  |  |  | Pending Accountant |
| REN-08 | Rentals | Compensation for lost/wrecked items: TVA or not, account 757? | Accountant chooses; make tax a setting per charge type | Critical |  |  |  |  | Pending Accountant |
| REN-09 | Rentals | Lost units leave stock at catalogue cost or at a worn "book value"? | Decide with REN-12; cost is consistent if no depreciation | Important |  |  |  |  | Pending Accountant |
| REN-10 | Rentals | Wrecked units kept as scrap at full cost — write down? | Write down to expected scrap value at once (to 657 unless told otherwise) | Important |  |  |  |  | Pending Accountant |
| REN-11 | Rentals | Repair recharged to customer = income 707 with TVA, real cost = expense 615? | Keep as is | Important |  |  |  |  | Pending Accountant |
| REN-12 (also STK-07, GL-04) | Rentals | Rental equipment: stock (310, no depreciation) or fixed assets with depreciation? | Accountant decides; if fixed assets, build register and depreciation | Critical | Rental equipment is fixed assets, not merchandise stock. | **Built 2026-11-09 (M7a, not deployed), off by default:** register of pools (product × branch), switch-over out of 310, straight-line depreciation by month posted by the accountant, disposals at net book value ([fixed-assets.md](fixed-assets.md)). **Declining balance built 2026-11-10 (not deployed)** as a setting: company default and per type, coefficient suggested from the life (1.5 / 2 / 2.5) and changeable, switch to straight line on by default; a type's method fixed once depreciation is posted on it (`method_locked`) | Accountant: accounts, method (straight line or declining, per type), useful life, switch value, disposals — set in Reports › Fixed assets. Till sales of hire items not routed yet. |  | Approved by Owner; built as settings |
| REN-13 | Rentals | Deposit account: 419 or 165? (docs/07 Q7, owner Q53) | 165 if it is a returnable guarantee, 419 if an advance on rent | Important |  |  |  |  | Pending Accountant |
| REN-14 | Rentals | Keeping part of a deposit for damage and refunding the rest (not built) | Invoice the charge normally; apply the deposit against the debt; refund the rest | Important |  |  |  |  | Pending Accountant |
| REN-16 | Rentals | Which branch shows the income when goods leave from another branch? | Mainly the owner's choice; contract branch if branch profit matters | Later |  |  |  |  | Pending Accountant |
| REN-17 | Rentals | TVA rate change: keep the rate frozen at signing, or apply the new rate? | Apply the rate in force on each charge date | Later |  |  |  |  | Pending Accountant |
| REN-18 | Rentals | Confirm account numbers 706, 707, 708, 757, 756 (with D33) | Review together with D33 | Important | Numbering is internal, like D33 (1 Nov 2026). Removed from the questionnaire. | Partly — 706 and 757 correct, 708 acceptable; 707 and 756 under the wrong SCF heading | Move 707 and 756 at the cutover with D33. 708 follows REN-06 (accountant). |  | Internal Technical Verification |
| C-03 | Manufacturing | Costs stay by nature (Model N) or flow through work in progress (Model W)? | Model N: finished goods enter stock through 72; confirm SCF presentation | Critical |  |  |  |  | Pending Accountant |
| C-05 | Manufacturing | How is one labour hour priced in product cost? | One blended rate per worker-month from payroll (pay incl. leave, overtime, bonuses ÷ paid hours) | Critical |  |  |  |  | Pending Accountant |
| C-07 | Manufacturing | Order expenses before or after recoverable TVA? | Before recoverable TVA, typed as such; voucher TVA field later | Critical |  |  |  |  | Pending Accountant |
| C-08 | Manufacturing | Add factory overheads to product cost? | Labour-hour rate from normal capacity if required; else none | Important |  |  |  |  | Pending Accountant |
| C-09 / AD-8 | Manufacturing | Value and account for rejects/offcuts kept as scrap | Recovery value per order, scrap into 318, sale to 756 | Important |  |  |  |  | Pending Accountant |
| C-10 / AD-1 / D13 | Manufacturing | Costs known after an order is finished | Stay in the expense accounts, reported; never revalue goods | Critical |  |  |  |  | Pending Accountant |
| C-11 / AD-7 | Manufacturing | Value of partial releases before the order ends | Standard material + conversion to date; final release carries the rest | Critical |  |  |  |  | Pending Accountant |
| C-12 | Manufacturing | May finished goods be sold before live costing? | Keep stock operations off until Phase D | Critical |  |  |  |  | Pending Accountant |
| C-13 / AD-10 / AD-11 | Manufacturing | Post planned-vs-actual differences? | Management figure only; add planned expenses to recipes later | Important |  |  |  |  | Pending Accountant |
| C-15 | Manufacturing | Value unfinished orders every month end? | Monthly entry Dr 331 / Cr 72, reversed on the 1st | Important |  |  |  |  | Pending Accountant |
| C-17 / AD-9 | Manufacturing | Decimals and rounding | 3 decimals; final release absorbs the residual | Later |  |  |  |  | Pending Accountant |
| AD-2 / AD-3 | Manufacturing | Sign and kind of a late cost | Keep sign rule; fixed list of kinds + who entered it | Later |  |  |  |  | Pending Accountant |
| AD-4 | Manufacturing | Voucher cancelled after being used in costing | Drop cancelled-voucher expenses before freeze; negative late cost after | Critical |  |  |  |  | Pending Accountant |
| AD-5 | Manufacturing | Labour correction when payroll is approved after freeze | Automatic late cost = hours × (actual − frozen rate) | Critical |  |  |  |  | Pending Accountant |
| AD-6 | Manufacturing | Sheet frozen long after completion | Freeze automatically at completion; older late sheets = reconstruction after 7 days | Important |  |  |  |  | Pending Accountant |
| AD-12 | Manufacturing | When is an order's cost "final"? | At month close | Critical |  |  |  |  | Pending Accountant |
| MFG-01 | Manufacturing | Fewer good units than planned: all cost into stock? | Absorb as today (D23); add "abnormal" flag sending excess to expense | Critical |  |  |  |  | Pending Accountant |
| MFG-02 | Manufacturing | Order with no good output: record the loss | Leave costs in expenses, close order as "lost", report it | Important |  |  |  |  | Pending Accountant |
| MFG-03 | Manufacturing | Cost of in-house semi-finished parts used in another order | Production cost via average (C-01); typed last frozen cost meanwhile | Critical |  |  |  |  | Pending Accountant |
| MFG-04 | Manufacturing | Material issued with no cost (0) | Refuse to freeze until costed or accepted with a reason | Important |  |  |  |  | Pending Accountant |
| MFG-05 | Manufacturing | Day workers paid by voucher on an order | Keep as order expense, described as day labour | Important |  |  |  |  | Pending Accountant |
| MNT-01 | Maintenance | Repairs on own equipment: always expense (615) or add big repairs to the asset? | Always 615 while equipment is stock | Important |  |  |  |  | Pending Accountant |
| MNT-02 | Maintenance | Spare parts from own stock used in a repair: which account? (today 657 via adjustment) | 615 | Important |  |  |  |  | Pending Accountant |
| MNT-04 | Maintenance (planned) | What goes into a repair job's cost? | Direct costs only (parts, labour, outside work) at first | Important |  |  |  |  | Pending Accountant |
| MNT-05 | Maintenance (planned) | Workers' hours on jobs: what rate, and any book entry? | Costed for the report only, same rate method as manufacturing | Important |  |  |  |  | Pending Accountant |
| MNT-06 | Maintenance (planned) | Income account for repairs sold to outside customers; parts separate? | Decide with service income 700/706 (Sales) | Important |  |  |  |  | Pending Accountant |
| MNT-07 | Maintenance (planned) | TVA on repairs and on repair advances: rate and timing | Follow TAX (D45 / C-18) | Important |  | Repair advances built 9 Nov 2026 (M7b, MS-35 "Taken", allowed only while advances are held on 419): an advance names the job and clears against the job's invoice; its VAT follows the D45 setting. |  |  | Pending Accountant |
| MNT-08 | Maintenance (planned) | Unfinished repair jobs at year end: work in progress? | Leave as expenses unless significant; list open jobs | Later |  |  |  |  | Pending Accountant |
| MNT-09 | Maintenance (planned) | Free re-work under guarantee: where does the cost go? | Add to original job for the report; expense in the books | Later |  |  |  |  | Pending Accountant |
| MNT-10 | Maintenance (planned) | Repair history per piece of equipment or per type? | Per piece above a value threshold; per type otherwise | Later |  |  |  |  | Pending Accountant |
| PAY-01 | Payroll | CNAS and IRG handled outside the system: how do they reach the books, and is a payslip without them acceptable? | Turn CNAS/IRG back on with real figures if workers are declared; otherwise a monthly accountant entry | Critical | Keep the current payslip and deduction workflow. 5 Nov 2026, the accountant through the owner: ignore CNAS and IRG on the payslip. | Yes — payslip without CNAS/IRG (switched off in code) | Nothing to build: payslips already leave CNAS and IRG out. How the monthly amounts reach the books stays outside the system. |  | Approved by Owner |
| C-06 / D11 | Payroll / Manufacturing | Employer CNAS share missing from labour cost: option 1, 2 or 3, and the burden % | Option 1 in stock value, option 2 (burden %) in reports; depends on PAY-01 | Critical |  |  |  |  | Pending Accountant |
| PAY-02 | Payroll | Confirm accounts 421, 425, 431, 442, 447, 631, 635, 641 | Confirm, or give sub-accounts before more runs post | Critical | Numbering is internal, like D33 (1 Nov 2026). Removed from the questionnaire. | Yes — all eight match the SCF (`scf-chart-review.md`) | Nothing for the numbering. The use of 641 for "other" deductions is PAY-03, and CNAS/IRG is PAY-01 (both accountant). |  | Approved by Owner |
| PAY-03 | Payroll | Fines and deductions collected for others: which account? (placeholder credits 641) | Fines to an income account, third-party money to a liability; accounts named by you | Important |  |  |  |  | Pending Accountant |
| PAY-04 | Payroll | Uncollectable negative pay (now 471) and advances never repaid: which account? | Keep on 425; write off to a named loss account on owner's decision | Important |  |  |  |  | Pending Accountant |
| PAY-05 | Payroll | Loans repaid by instalment vs advances; same account 425? | One account 425 with an instalment per loan | Important |  |  |  |  | Pending Accountant |
| PAY-06 | Payroll | Overtime multipliers (×1 workshop, ×2 loading/rest/holiday) and hourly-rate basis | Give the legal minimum premium and basis; settings only | Important |  |  |  |  | Pending Accountant |
| PAY-07 | Payroll | Absence = gross ÷ 30; full pay when no attendance entered | Keep ÷30 and full pay with warning | Later |  |  |  |  | Pending Accountant |
| PAY-08 | Payroll | Late approval and reversal dates; closed months | Reversal follows the original's date while open; a "closed up to" date | Important |  |  |  |  | Pending Accountant |
| PAY-09 | Payroll | Paid month is final; errors corrected in next month's run | Acceptable unless a supplementary payslip is legally needed | Important |  |  |  |  | Pending Accountant |
| PAY-10 | Payroll | Final pay of a leaver (pro-rata, unused leave, advance) | Pro-rata now via adjustments; full settlement when leave module exists | Important |  |  |  |  | Pending Accountant |
| PAY-11 | Payroll | Paid leave: balance, leave pay, year-end provision | Give the rule; build the leave balance first | Important |  |  |  |  | Pending Accountant |
| PAY-12 | Payroll | Day labourers paid by voucher to 631, no CNAS/IRG | Accountant decides; account change is a setting | Important |  |  |  |  | Pending Accountant |
| PAY-13 | Payroll | Worker moved mid-month charged wholly to compute-time branch | Acceptable | Later |  |  |  |  | Pending Accountant |
| P0.2 | Treasury / Reports | Group reports drop payroll cash paid from another branch's box; correct past reports? | Correct past and future after reviewing the list of affected entries | Critical |  |  |  |  | Pending Accountant |
| TRS-01 | Treasury | Opening balances of cash boxes and bank (none today) | One opening entry per box/bank against 890 | Critical |  |  |  |  | Pending Accountant |
| TRS-02 | Treasury | Cash deposited to / withdrawn from the bank (not built) | Through an internal transfers account if deposits take days; else direct | Critical |  |  |  |  | Pending Accountant |
| TRS-03 | Treasury | One 512 for all banks and CCP? | One sub-account per bank if more than one | Important |  |  |  |  | Pending Accountant |
| TRS-04 | Treasury | Bank charges, interest, direct debits; bank reconciliation (not built) | A bank movement document with named accounts; reconciliation later | Important |  |  |  |  | Pending Accountant |
| TRS-05 (also SAL-05) | Treasury | Card payments: 512 on sale day; commission not booked | Keep 512 on sale day; book commission with bank items | Important |  |  |  |  | Pending Accountant |
| TRS-06 (also SAL-11) | Treasury | Cheques received: 413 at till vs nothing until cleared in back office | Always 413 on receipt, 512 on clearing date | Important |  |  |  |  | Pending Accountant |
| TRS-07 | Treasury | Cheque bouncing after confirmation; bounce fees (not built) | Allow late bounce: Dr 411 / Cr 512; fee to bank charges | Important |  |  |  |  | Pending Accountant |
| TRS-08 | Treasury | Cheques we issue: 512 reduced on handover; no outstanding list | Keep; add outstanding-cheques list | Later |  |  |  |  | Pending Accountant |
| TRS-10 | Treasury | Cash box allowed to go negative on payments; remittance refused | Warn and offer to record the missing refill first | Important |  |  |  |  | Pending Accountant |
| TRS-11 | Treasury | Paid payment voucher cannot be reversed; generic reversal leaves box out of step | A "reverse paid voucher" action dated today, cash back in box | Important |  |  |  |  | Pending Accountant |
| TRS-13 | Treasury | Remittance in transit (one-step today) | Keep one step | Later |  |  |  |  | Pending Accountant |
| TRS-14 | Treasury | Separate petty-cash box | One box per branch for now | Later |  |  |  |  | Pending Accountant |
| BRN-01 | Branches | One ledger with a branch tag, or separate books/declarations? | One ledger, unless a branch is registered separately for tax | Important |  |  |  |  | Pending Accountant |
| BRN-02 | Branches | 181 computed on a branch balance sheet, never posted | Acceptable while branches are not separate units | Later |  |  |  |  | Pending Accountant |
| C-14 | Branches | Internal transfers at cost, no sale/tax; transport (D47) | As proposed: at cost, no tax; transport is the receiving branch's 624 | Important |  |  |  |  | Pending Accountant |
| BRN-03 | Branches | Goods in transit at month end stay in the sender's 310 | Acceptable | Later |  |  |  |  | Pending Accountant |
| BRN-04 | Branches | Lost in transit: whose loss; recovery from carrier/driver | 657 at sender; recovery to an income account (e.g. 757); claim when responsible party known | Important |  |  |  |  | Pending Accountant |
| BRN-05 | Branches | Unanswered shortfall reports | No auto limit; month-end list; write off at year-end count | Later |  |  |  |  | Pending Accountant |
| BRN-06 | Branches | One branch buys, another pays the supplier | Acceptable; group figure is what counts | Later |  |  |  |  | Pending Accountant |
| BRN-07 | Branches | Sharing the warehouse's costs over branches | Separate central cost now; management-report sharing later | Later |  |  |  |  | Pending Accountant |
| TAX-01 | Tax / till | May a till sale be recorded without VAT, and who may choose it? | Owner chose untaxed by default; if not acceptable, taxed by default and untaxed only with a permission | Critical | System supports taxed and untaxed sales; do not ask if the feature exists. 2 Nov 2026: VAT is optional when the bill is made, on every bill (sales, invoices, rentals). | Yes — taxed and untaxed sales | Accountant: which sales may legally be untaxed, and what proof to keep. Internal: till default and permission findings (code findings). |  | Owner Workflow Confirmed — Compliance Pending |
| TAX-02 | Tax / invoices | Taxed invoice requested later for an untaxed sale? | Refund the untaxed sale and sell again with VAT | Important |  |  |  |  | Pending Accountant |
| TAX-03 | Tax / settings | Confirm prices before tax and rates 19 / 9 / 0; which items are not 19 %? | Confirm, list exceptions | Important |  |  |  |  | Pending Accountant |
| TAX-04 | Tax / rounding | VAT on each rate's total, rounded to the centime, not per line? | Keep per-rate total | Later |  |  |  |  | Pending Accountant |
| TAX-05 (also PUR-03, MNT-03) | Tax / purchases | How to record deductible VAT on purchases and expenses (4456)? | Supplier invoice and expense split: before-tax + 4456 + full amount to supplier | Critical | Enabling tax calculates it on the total. Not proof that purchase VAT is handled. | No — purchase VAT is not recorded (no 4456); confirmed in code | Accountant: which purchase VAT is deductible, account, invoice details to keep. Then build. |  | Pending Accountant |
| TAX-06 | Tax / reporting | What report is needed for the monthly G50? | Monthly VAT report from the ledger, by rate and branch | Important | ParadisERP has a complete reporting system; do not ask whether reporting exists. | General reports yes; no G50-specific report found | Internal: check G50 coverage line by line. Needs a copy of a G50 from the accountant. |  | Internal Technical Verification |
| TAX-07 | Tax / branches | One VAT declaration for the company or one per branch? | As the tax office requires; report supports both | Important |  |  |  |  | Pending Accountant |
| TAX-08 | Tax / invoices | Invoice numbering: company-wide, per branch, per year? | Per branch (and per year) if branches file separately | Important |  |  |  |  | Pending Accountant |
| TAX-09 (also TRS-12) | Tax / stamp duty | Does stamp duty apply to cash-paid invoices? Rate, limits, account? | Give the rule; make it a company setting | Important | Stamp-duty configuration exists (owner). 1 Nov 2026: it is typed in by hand as a product or fee line. | No calculation in code (searched again 2026-11-01). A product line posts as income to 700, with VAT unless its rate is 0 % | Internal: not found in live data by name (no timbre/stamp/طابع/دمغة product; no back-office invoices) — the owner should name the product or line. Accountant: confirm the rule (rate, limits) and the account; a product line books it as the company's income. |  | Internal Technical Verification |
| TAX-10 | Tax / other | Tax regime; any TAP, IBS, IFU figures needed? | Tell us regime and taxes; system supplies bases only | Important |  |  |  |  | Pending Accountant |
| D45 | Tax / advances | Is VAT due on customer advances when received? | VAT on service advances only; advances held on 419 | Critical | VAT is calculated when the invoice is created. | Yes for sales — advances carry no VAT, VAT at invoice. Built 9 Nov 2026 as a setting (M7b), **at the invoice by default** = today: "when the advance is received" (only with 419) posts the VAT inside an advance the seller ticks (untaxed by default) to 4457 on receipt, Dr 530 / Cr 419 net / Cr 4457, and gives it back as the advance is applied (Dr 419 / Dr 4457 / Cr 411), so the invoice's VAT is counted once. Hire VAT timing (REN-03) unchanged. See docs/accounting/advances.md. | Accountant: is that legal for advances (especially services)? 411 vs 419 for advances. Asked with REN-03. |  | Owner Workflow Confirmed — Compliance Pending |
| TAX-14 | Tax / documents | Required details on a taxed invoice (seller and buyer)? | List required mentions; warn when buyer NIF missing | Important |  |  |  |  | Pending Accountant |
| TAX-15 | Tax / e-invoicing | Any e-invoicing obligation now or soon? | Just tell us | Later |  |  |  |  | Pending Accountant |
| C-18 | Tax / conformity | Does the whole set (method, accounts, stock variation, D45, count) meet SCF and tax rules? | Answer after C-01–C-17, D33, D45, TAX-05/06, CLO-08 | Important |  |  |  |  | Pending Accountant |
| GL-01 | Ledger | Official books in ParadisERP, or ParadisERP feeds your software? | Feed your software (with export) the first year | Critical | ParadisERP is the official accounting/ERP system. No other accounting system. | Yes — the ledger is in ParadisERP | Nothing for the accountant. What official books imply (period lock CLO-01, numbered journal GL-06, year-end CLO-08) is asked separately. |  | Approved by Owner |
| D33 | Ledger / chart | Confirm or rename every account code the system posts to | Mark each OK or give code; add new codes, never renumber | Critical | Chart review is our development responsibility; removed from the accountant questionnaire. | Chart exists; SCF review done 2026-11-01: 8 of 37 accounts under the wrong SCF heading | Owner (1 Nov 2026): fix the 8 together at the cutover (first day of a financial year, with D43 and REN-12). Live balances (live check of 2026-11-01, read-only): 310 59,534,750 DA, 6068 16,100 DA, 6061 1,000 DA; 318, 4181, 4182, 707 and 756 have never been posted to. Software change, its own go-ahead. See `scf-chart-review.md`. Not asked of the accountant. |  | Internal Technical Verification |
| C-04 | Ledger / manufacturing | Account codes for raw materials, WIP, finished goods, scrap, 72, 701 | Proposed codes or yours, with C-03 | Important | Numbering is internal, like D33 (1 Nov 2026). Removed from the questionnaire. | No — codes not created; manufacturing posts nothing | SCF review: the proposed codes match (358, not 318; 72 is 723/724), once merchandise leaves 310. Created when manufacturing posting is built, after C-03 (accountant). |  | Internal Technical Verification |
| GL-02 | Ledger / chart | Which missing accounts to add (capital, fixed assets, loans, 4456, bank fees, taxes)? | Depends on GL-01; at least 4456 now | Important |  |  |  |  | Pending Accountant |
| GL-03 | Ledger / entries | How to record entries with no document (bank fees, depreciation, adjustments)? | Accountant-only hand entries, balanced, dated in open month | Critical |  |  |  |  | Pending Accountant |
| GL-06 | Ledger / journals | Separate journals and continuous entry numbers? | Journal codes by document type + number per journal/year | Important |  |  |  |  | Pending Accountant |
| GL-07 | Ledger / reports | Trial balance with opening, movements and closing? | Six-column trial balance | Important |  |  |  |  | Pending Accountant |
| GL-08 | Ledger / statements | Official SCF layout or simple lists? | Simple lists for now | Later |  |  |  |  | Pending Accountant |
| GL-09 | Ledger / suspense | Who clears 471 and how? | A "clear suspense" document with reason and account | Important |  |  |  |  | Pending Accountant |
| GL-10 (also PUR-11) | Ledger / opening | What the opening entry contains and what balances it (890)? | Full opening balance sheet at go-live, against accounts you name | Critical |  |  |  |  | Pending Accountant |
| GL-11 | Ledger / branches | Branch statements with derived 181: acceptable? | Keep as is | Later |  |  |  |  | Pending Accountant |
| CLO-01 | Closing | Lock a month after review; who may reopen? | Lock on your instruction; reopen with reason | Critical | Close periods after review; block back-dated postings into closed periods; controlled corrections with audit trail. | Yes, live since 2026-11-02 (built 2026-11-01) — Finance › Closing the books: the Admin (`accounting.close`) closes through the end of an ended month, forward only, and reopens back with a required reason; every close and reopen is kept. `LedgerPoster` refuses any entry dated inside closed books (409 `periodClosed`, the document is not saved); a till sale is never refused and its entry is booked on the first open day (owner, 2026-11-01) | Accountant: how a correction of a closed month is dated (COR-E01) — until then a correction means reopen, correct, close; whether a late till sale on the first open day is right (SAL-14). Not built: the year-end close (CLO-08). |  | Approved by Owner |
| CLO-03 (also COR-10) | Closing / dates | How far back may documents be dated? | Only dates in an open month | Important |  |  |  |  | Pending Accountant |
| CLO-04 (also REN-15, PUR-13) | Closing / cut-off | Entry date: document date or keying date? | Document date, never before first open day | Important |  |  |  |  | Pending Accountant |
| CLO-05 | Closing / time zone | Months in Algeria time for all reports? | Yes; reports use company time zone, never the computer's | Important |  |  |  |  | Pending Accountant |
| CLO-06 (also TRS-09) | Closing / till | Daily close per box: count, difference account, Z-report? | Daily close, never blocking sales | Important |  |  |  |  | Pending Accountant |
| CLO-07 | Closing | Month-end checklist items? | Suspense zero, tills synced, payroll approved, boxes counted, 4457 = report | Later |  |  |  |  | Pending Accountant |
| CLO-08 | Closing / year end | Fiscal year and carrying the result forward? | Calendar year; closing entry to account you name; lock closed year | Critical |  |  |  |  | Pending Accountant |
| COR-01 | Refunds / Stock | P0.4c: returned goods back at original sale cost or today's cost | Original sale cost | Critical |  |  |  |  | Pending Accountant |
| COR-02 | Refunds | Return of last month's goods: which month | Book in return month; add back-office return screen | Critical |  |  |  |  | Pending Accountant |
| COR-03 | Refunds / Branches | Return at a different branch | Selling branch's sales, receiving branch's stock/cash, 181 between | Important |  |  |  |  | Pending Accountant |
| COR-04 | Refunds | Refund must follow original payment means? | Yes; cash only up to cash received; exception with manager + reason | Critical |  |  |  |  | Pending Accountant |
| COR-05 | Refunds | Partial returns: discount share and rounding | Pro rata as today; allow second partial return at till | Later |  |  |  |  | Pending Accountant |
| COR-06 (also TAX-13) | Credit notes / Tax | Credit note needed for every refund? | Receipt enough for persons; credit note for companies | Critical |  |  |  |  | Pending Accountant |
| COR-07 | Credit notes | Back-office credit note: price corrections only? | Price/billing only; goods via refund; block double give-back; add amount-only credit note | Critical |  |  |  |  | Pending Accountant |
| COR-08 | Refunds / Stock | Damaged returned goods | Choose sellable/scrap/loss at return | Important |  |  |  |  | Pending Accountant |
| COR-09 | Corrections | Cashier mistake: refund-and-resell vs void | Refund and resell | Later |  |  |  |  | Pending Accountant |
| COR-E01 | Corrections | Correct a closed month in the current month or reopen it? | Current month, referencing the original month | Critical | 5 Nov 2026, the owner with the accountant: **correct in the current month**, naming the original; the closed month stays closed and its figures never change. | Partly: a reversal is dated today and links to its original; a credit note is dated today. Reopening (CLO-01) still exists. | Built 2026-11-05: a reversal carries its original's date ("Corrects the entry of …"); the three acts that belong to a closed month and were refused — confirming a cheque or transfer received in it, approving a payroll run for it, extending a rental whose period ended in it — are booked on the first open day, saying so (owner's choice, the till's rule). A document typed with a closed-month date is still refused. `BookClosings.OntoOpenBooksAsync`; tests `ClosedMonthCorrectionTests`, `BookClosingTests`. |  | Approved by Owner |
| COR-E02 | Corrections | Allow direct reversal of a single journal entry? | Only via the document; direct reversal only for no-document entries | Important |  |  |  |  | Pending Accountant |
| COR-E03 | Corrections / VAT | Show VAT corrections of earlier months separately? | Separate line in the VAT report | Important |  |  |  |  | Pending Accountant |

## Merged questions

These numbers were asked twice from different sides and are answered by one question. Their status follows that question.

| Asked as | Answered by | Status |
|---|---|---|
| COR-10 | CLO-03 | Pending Accountant |
| PUR-13 | CLO-04 | Pending Accountant |
| REN-15 | CLO-04 | Pending Accountant |
| TRS-09 | CLO-06 | Pending Accountant |
| TAX-13 | COR-06 | Pending Accountant |
| PUR-11 | GL-10 | Pending Accountant |
| TAX-11 | REN-01 | Approved by Owner |
| CLO-10 | REN-02 | Owner Workflow Confirmed — Compliance Pending |
| TAX-12 | REN-03 | Owner Workflow Confirmed — Compliance Pending |
| GL-04 | REN-12 | Approved by Owner |
| STK-07 | REN-12 | Approved by Owner |
| GL-12 | SAL-04 | Pending Accountant |
| TRS-15 | SAL-04 | Pending Accountant |
| CLO-02 | SAL-14 | Pending Accountant |
| GL-05 | SAL-17 | Approved by Owner |
| CLO-09 | STK-02 | Pending Accountant |
| MNT-03 | TAX-05 | Pending Accountant |
| PUR-03 | TAX-05 | Pending Accountant |
| TRS-12 | TAX-09 | Internal Technical Verification |
| SAL-05 | TRS-05 | Pending Accountant |
| SAL-11 | TRS-06 | Pending Accountant |
