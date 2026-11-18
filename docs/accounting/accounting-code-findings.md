# ParadisERP — Accounting Review: Code Findings and Gaps

*Read-only review of `main` on 2026-10-31, made while preparing the questions for the accountant. For the owner and developers, not the accountant. Nothing was changed. Paths are relative to the repository root; line numbers are as of this review.*

Each finding is marked **bug** (does something wrong), **gap** (not built), **risk** (works, but can give wrong figures) or **design** (deliberate). "Needs verification" items could not be confirmed from code alone, usually because they depend on live data or settings. A reference such as E#3 means item 3 in section E below; IDs such as F-A05 or B-F02 are the findings' own labels. The question IDs in brackets link each finding to the accountant's questions.

## Owner decisions of 2026-10-31 against the code

The owner decided or narrowed several questions on 2026-10-31 (see `accounting-decision-tracker.md`). An approved rule is not a built one. This table sets each decision against what the code does today. Nothing has been changed, and **no item is "Implemented and Verified"**.

| ID | Owner decision | What the code does today | Gap / next step |
|---|---|---|---|
| GL-01 | ParadisERP is the official books | Full double-entry ledger with trial balance, P&L, balance sheet and statements | Official books normally need a period lock (CLO-01, approved), a numbered journal (GL-06) and a year-end close (CLO-08). GL-06 and CLO-08 are not approved yet (E#8, E#12, E#13). |
| D33 | Chart review is internal | Chart seeded in `LedgerEntities.cs:205-393` | **SCF review done 2026-11-01** (`scf-chart-review.md`, against the arrêté of 26 July 2008, JO n° 19 of 2009): 8 of 37 accounts are under the wrong SCF heading (310, 318, 4181, 4182, 6061, 6068, 707, 756). Fixing them is a software change. C-04, REN-18 and PAY-02 are chart questions too; the review answers their numbering, and the owner should say whether they are internal. |
| TAX-01 | Taxed and untaxed sales are supported | Yes. Till sales start untaxed, with no permission behind the choice (E#1); the untaxed-invoice permission is bypassed for invoices raised over a sale (E#2) | Feature confirmed. E#1 and E#2 stay as **control findings for the owner**. Legal eligibility is for the accountant. |
| TAX-05 | Tax is calculated on the total when enabled | True for sales (E#22). **Purchase VAT is not recorded:** no 4456; receiving posts the before-tax value to 401 (E#3, B-F02) | Confirmed not built. Build after the accountant's answer. |
| TAX-06 | A complete reporting system exists | No G50-specific report. The sales summary covers till sales only and puts untaxed sales in the same 0 % line as exempt goods (E#4) | **Internal Technical Verification:** check coverage against a copy of the G50 from the accountant. |
| TAX-09 | Stamp-duty configuration exists | **Not found.** No setting, calculation, printing or posting for stamp duty, searched in English, French and Arabic, in the code on `main` and in the history of every branch (E#5, D-F15) | **Discrepancy:** the owner should show where it is set (it may be outside the code, such as a product line or a printed text). Then verify its calculation and posting. |
| D45 | VAT is calculated at invoice creation | Matches for sales: advances post Dr cash / Cr 411 with no VAT; 419 is unused | Legal timing → accountant. |
| REN-01 | Hire carries VAT | Hire rate is a company setting, 0 % by default (`Entities.cs:263`), frozen per contract line and applied to every charge (C#3, E#6) | **Internal:** check the live rate. The rate → accountant. |
| REN-03 | VAT when invoiced (owner, 10 Nov 2026; was "on payment and invoice") | **Matches:** rental VAT posts in full at issue, and the issue note is the invoice (`RentalPosting.Issue`) | No change. Legal timing → accountant (with D45). |
| REN-02 | Full rent on the first day of the rental period | **Changed 2026-11-01 (live since 2026-11-02):** the issue entry is dated midnight, company time, on the period's first day (`RentalPosting.Issue`, `RentalDocuments.PeriodStartsUtc`); an extension's carry-over too. Before that it was dated when keyed (C#2). Entries already posted keep their dates | Period-end adjustment → accountant. Return entries are still dated when keyed (CLO-04). |
| REN-12 | Rental equipment is fixed assets | **Built 2026-11-09 (M7a, deployed `9711b06`), off by default:** `Infrastructure/FixedAssets` — settings, switch-over (Dr asset / Cr 310), straight-line depreciation by month (source `fixed-assets`), disposals and transfers through the register from the stock and rental documents ([fixed-assets.md](fixed-assets.md)). Until the accountant switches it on, in 310 as before (E#18) | Policy → accountant, in the panel. Till sales of hire items, direct invoices and capitalised repairs still post as before. |
| CLO-01 | Close after review; block back-dating; controlled corrections with audit trail | **Built 2026-11-01 (live since 2026-11-02):** `BookClosings` (table `BookClosings`, both providers) keeps every close and reopen; `LedgerPoster.PostAsync` throws `PeriodClosedException` for an entry dated before the first open instant (after its re-post check), answered as 409 `periodClosed`; `SyncService` books a late till sale on the first open day; Finance › Closing the books, Admin only (`accounting.close`) | Correction dating (COR-E01). Direct reversal of an entry still leaves the documents unchanged (E#14). |
| PAY-01 | Keep the payslip and deductions | CNAS/IRG off, hard-coded (D-F02). A statutory payment can be recorded with nothing accrued (D-F03) | Compliance → accountant. No change until then. |
| SAL-17 | Liability at sale, income on redemption, expiry only when allowed | **Built 2026-11-01 (live since 2026-11-02):** `GiftCardCash` books a card sold for cash (Dr 530 / Cr 4181, box in) and, on Adjust, a paid top-up the same way or a cash refund the other way; a correction books nothing. Redemption already posted Dr 4181 | Expiry per the accountant. Balances of cards sold before the change are not on 4181. |
| SAL-02 | Allow zero-cost sales; flag; report incomplete margin | **Built 2026-11-01 (live since 2026-11-02):** `SaleLine.CostMissing`, set in `SyncService` by the same rule that skips the cost; migration `SaleLineCostMissing` (both providers) backfills sales whose posting has no 600 line; `GET /api/reports/missing-cost` and Reports › Sales without a cost | A catch-up cost entry for flagged sales is not built and not decided. |
| C-01 / D9 | Kept open by the owner | Last purchase price (B-F01) | Pending Owner. |
| C-16 / D43 | A setting since 2026-11-09 (owner: every accounting decision is a setting) | While sales are costed at the average, a negative-stock gap covered by a later inflow is listed and posted by the accountant (default), or, under `StockValuationSettings.GapPosting = Automatic`, posted in the inflow's own transaction to the `valuation_difference` slot's account (source `valuation-gap`, once per covering movement; slot unmapped: left listed, the inflow never refused) | The account itself → accountant (C-16). See `docs/manufacturing/valuation-shadow.md`, "Three postings the accountant chooses". |
| SAL-03 | Not approved | Net posting to 700 | Pending Owner. |

## The most serious, in short

1. **Money that never reaches the books.** Gift-card sales and loyalty points post nothing, so 4181/4182 only ever receive debits (F-A01, F-A02). VAT paid on purchases and expenses is never recorded; there is no deductible-VAT account (B-F02, E#3).
2. **Double or wrong reversals.** A hand-made credit note followed by a till refund of the same sale gives back income and VAT twice (F-A05). An invoice raised after a till refund still bills the full sale (F-A06). Reversing a ledger entry directly leaves stock, customer, supplier and cash-box balances unchanged (B-F03, D-F09, E#14).
3. **Dates and periods.** There is no period lock: any past month can be posted into (E#8). Rental, stock, remittance and voucher entries are dated when typed, not by the document's date (C#2, B-F10, D-F14, E#9). The ledger screen builds months in the browser's time zone (E#10).
4. **Cost and stock value.** One company-wide cost per product, overwritten by each receiving note (since 2026-11-09 a setting: last cost by default, weighted average or unchanged; a 0 never overwrites), read when a sale reaches the server; cost 0 posts nothing (B-F01, B-F06, F-A03). Products created by the Excel opening import are valued at 0 (B-F05).
5. **Tax.** Till sales start untaxed with no permission behind the choice (E#1); the untaxed-invoice permission is bypassed for invoices raised over a sale (E#2); the hire tax rate defaults to 0 % and covers every rental charge alike (C#3); there is no G50 report and no stamp duty in the code (E#4, E#5, D-F15). The owner says stamp duty is configured; see TAX-09 in the table above.
6. **Payroll.** CNAS/IRG off is hard-coded, not a setting (D-F02); wages paid from another branch's box distort group reports (P0.2, D-F01).
7. **Manufacturing.** Completion never freezes the cost; cancelled vouchers still count in cost; an order whose pieces all fail at the last stage can be neither completed nor cancelled (F#1, F#2, F#5).

## Sales, customer payments, refunds and corrections (A)

### Confirmed in code
- **F-A01 — Gift cards: the money paid for a card is never recorded; 4181 drifts into a debit balance (gap).** *Fixed for new cards 2026-11-01 (SAL-17): the cash taken is booked against 4181.* `ParadisERP.API/Endpoints/GiftCardEndpoints.cs:39-62` issues a card with no ledger entry and no cash movement; `/adjust` (`:77-99`) posts nothing. `ParadisERP.Infrastructure/Ledger/SalePosting.cs:85` debits 4181 when a card is spent; nothing credits 4181 except a refund to a card. Cash taken for cards is off the books; expiry has no posting. (SAL-17)
- **F-A02 — Loyalty: earning points posts nothing; 4182 drifts into a debit balance (gap).** `ParadisERP.Infrastructure/Loyalty/LoyaltyLedger.cs:56-70` only moves points; `SalePosting.cs:86` debits 4182 on redemption with no matching credit. (SAL-16)
- **F-A03 — A refund's cost is today's cost, not the sale's (P0.4c gap).** `ParadisERP.Infrastructure/Sync/SyncService.cs:855-857` takes `Product.CostPrice` at push time; `SalePosting.cs:112-117` posts it; no cost is stored on `SaleLine`. An offline sale is costed at its sync date. `CostPrice == 0` posts no cost of goods sold while stock still drops (`SyncService.cs:728-761`), overstating 310 and the margin. (COR-01, SAL-02)
- **F-A04 — A cash refund is allowed for a sale on account or an uncleared cheque (risk).** `SyncService.cs:364-407` (`CheckRefundAsync`) limits only the Credit tender and the totals, never a cash or card refund to what was actually received. (COR-04)
- **F-A05 — A hand-made credit note plus a later till refund reverses income and tax twice (bug).** Credit note posts at `ReceivableDocuments.cs:876-893` / `ReceivablePosting.cs:118-140`; the refund check looks only at earlier refunds (`SyncService.cs:380-393`) and always posts (`:869`); `CreditTillRefundAsync` (`ReceivableDocuments.cs:904-956`) then returns null, so no paper either. A hand-made credit note never restocks or reverses cost, and works by quantity only. (COR-07)
- **F-A06 — An invoice raised after a till refund still bills the full sale (bug against the owner's rule 2, `docs/owner-notes.md:118-119`).** `ReceivableDocuments.cs:296-328` copies every sale line and never subtracts refunds; the picker (`receivable-invoice-form.component.ts:581-591`) does not exclude refunded sales; `PrepaidAmount` reads only the original sale's 411 lines (`:1078-1093`).
- **F-A07 — Cash or card till sales cannot be invoiced from the screen (gap).** The picker (`:581-591`) offers only Credit or Mixed sales, though the server accepts any sale. (SAL-07)
- **F-A08 — Document dates are free and there is no period lock (risk).** Invoice (`ReceivableDocuments.cs:382`), credit note (`:813`) and receipt (`:433`) take the date typed; `LedgerPoster.PostAsync` (`LedgerPoster.cs:115-171`) has no period check; no period lock exists outside HR. (COR-10, SAL-07)
- **F-A09 — A back-office cheque posts on its voucher date when confirmed later (risk).** `ReceivablePosting.cs:93` uses `ReceivedAtUtc`, called on confirmation (`ReceivableDocuments.cs:636, 651-655`); till cheques post on the clearing day (`DocumentPosting.cs:264-280`). (SAL-11)
- **F-A10 — A customer paying at one branch for another branch's invoice leaves branch-level 411 wrong (gap).** Admins can do it (`ReceivableDocuments.cs:468`); the credit lands on 411 at the receipt's branch (`ReceivablePosting.cs:91-103`) with no 181 entry between branches. Branch-bound users are refused; the owner's rule is pending. (SAL-15)
- **F-A11 — Till refunds are limited to sales stored on that browser, one refund per sale (gap).** `sales.component.ts:515` uses `recentSales(500)`; only `pos.service.ts:309`, `:452` write `db.sales`; `pos.service.ts:365-373` allows one refund per sale though the server supports several; no back-office refund route. (COR-02, COR-03, COR-05)
- **F-A12 — Every refund restocks as sellable (gap).** `SyncService.cs:735-760`; no scrap or loss choice at the return. (COR-08)
- **F-A13 — The automatic credit note is best-effort, outside the push transaction (risk).** `ParadisERP.API/Endpoints/SyncEndpoints.cs:78-90`: an exception is only logged, never retried. (COR-06)
- **F-A14 — Customer advances land on 411; 419 is unused (gap).** No sales order exists; an unallocated receipt credits 411 (`ReceivableDocuments.cs:454-482`, `ReceivablePosting.cs:100-103`); nothing posts to 419 (`LedgerEntities.cs:240-241`); keeping part of an advance has no document. (SAL-12, SAL-13)
- **F-A15 — Unallocated receipts keep invoices open in ageing (gap).** `StandingsAsync` (`ReceivableDocuments.cs:1020-1050`) and the ageing report (`CustomerStatements.cs:339-397`) count only allocated receipts; uninvoiced credit sales have no due date; customers have no payment-terms field. (SAL-06, SAL-09)
- **F-A16 — No bad-debt provision or write-off (gap).** Only a credit note reduces a debt, and it also reverses income and tax. (SAL-18)
- **F-A17 — All till and invoice income goes to 700.** `SalePosting.cs:56-57`, `ReceivableDocuments.cs:252`, `ReceivableEntities.cs:186`; 706 is already rental income (`LedgerEntities.cs:326`); there is no goods/service flag. (SAL-01)
- **F-A18 — Offline sales post on the till's own date, into any month.** `SyncService.cs:646-662`: a future date is clamped to arrival; anything over 31 days old only warns; the date comes from the device clock. (SAL-14)
- **F-A19 — Card takings go straight to 512 on the sale day, with no commission.** `CoreDataSeeder.cs:128`, `SalePosting.cs:164-165`. (SAL-05)
- **F-A20 — Refunds are never cash-rounded; account 758 takes rounding both ways.** `pos.service.ts:382-447`, `SalePosting.cs:62-66`. (SAL-04)
- **F-A21 — INV-, CRN- and RCV- numbers run as one company-wide sequence forever,** not per year or per branch. `ReceivableDocuments.cs:131, 497, 873, 1124`. (SAL-07)

### Needs verification
- Credit-note pro-rata amounts (`ReceivableDocuments.cs:843-845`) against the till's refund amounts (`pos.service.ts:396-409`): they may differ through rounding.
- Drift between `Customer.Balance` and 411 + 413 on every path (bounced till cheque, mixed refund); reported by `CustomerStatements.cs:186-191`, not traced end to end.
- A credit sale with no customer goes to 471 (`SalePosting.cs:96-106`); whether the till can produce one was not traced.
- Manufactured goods sold at the till post to 700 and 600 (see C-04).

## Purchases, stock and branch transfers (B)

### Confirmed in code
- **B-F01 (risk) — One company-wide cost per product, overwritten by every receiving note.** `StockEndpoints.cs:94`, `:120`; a sale's cost is read at `SyncService.cs:855-857`. Nothing revalues 310 when the cost changes, and there is no valued stock report. *Built 2026-11-09 as a setting (`StockValuationSettings.ReceiptCostRule`, Reports › Stock valuation, `accounting.manage`): on receipt the cost price becomes the last cost (default, unchanged behaviour), the weighted average the valuation shadow gives for the product company-wide after the receipt (falling back to the last cost, logged, when none can be computed; a receipt is never refused), or stays unchanged. The receipt itself is valued at what it states in every mode. A valued stock report exists since 2026-11-08 (the shadow). See `docs/manufacturing/valuation-shadow.md`, "B-F01 — the cost price on receipt". The open part — nothing revalued 310 on a change of cost price — is a setting since 2026-11-09 too (`StockValuationSettings.CostPriceRevaluation`): not revalued (default, unchanged behaviour), or, while sales are costed at the cost price, one entry per change (a receiving note under the last cost rule, or the product card): the change × the quantity held before it per branch (available + on rent + maintenance), Dr/Cr 310 against the `valuation_difference` slot's account (source `cost-price-revaluation`); the choice is refused while that slot is unmapped; never under the weighted average, nor under the weighted-average receipt rule; the note or the card is never refused for it. See "Three postings the accountant chooses" in the same document.*
- **B-F02 (gap) — No purchase TVA.** The receiving line has no tax field (`Dtos.cs:717`); receiving posts 310/401 only (`DocumentPosting.cs:29-52`); vouchers record no tax (`PaymentVouchers.cs:240-271`, `:837-862`); the chart (`LedgerEntities.cs:341`) has no deductible-tax account; the price label does not say before or after tax.
- **B-F03 (bug/risk) — Reversing a receiving entry fixes only the ledger.** The reverse route (`LedgerEndpoints.cs:177-198`) changes neither stock quantity, product cost nor the supplier's card balance; the supplier statement then shows a difference (`SupplierAccounts.cs:152`).
- **B-F04 (gap) — Receiving without a supplier credits 401 to nobody** (`DocumentPosting.cs:49-50`). A supplier payment needs a supplier (`PaymentVouchers.cs:243`), so that amount can never be paid off.
- **B-F05 (risk) — The Excel opening import values lines at product cost, which is 0 for products it creates** (`OpeningStock.cs:87`, `:199-216`). A typed opening value never becomes the product's cost.
- **B-F06 (risk) — A cost of 0 posts nothing** (`LedgerPoster.cs:117-121`); a receiving note accepts a cost of 0 and overwrites the product's cost with it (`StockEndpoints.cs:94`). *Overwrite corrected 2026-11-09 (`ReceivingCostPrice`): in every mode a stated 0 never replaces a non-zero cost price — a free or unknown cost, not a price; a product carried at 0 still takes its first price. The note is still accepted and its line still values at 0 (posts nothing to 310, nothing to the supplier), as before — by default: since 2026-11-09 a setting (`StockValuationSettings.ZeroCostReceipt`) may value such a line at the product's cost price instead (an unknown cost, not a free one: 310 and 401 take it like any receipt). The 0 still never replaces a non-zero cost price.*
- **B-F07 (bug) — A manual adjustment ignores "allow negative stock".** The rule is documented at `Entities.cs:353-358`; transfers check it (`StockTransfers.cs:94`); the adjustment route (`CatalogEndpoints.cs:354-406`) does not.
- **B-F08 (gap) — Count gains are credited to the losses account 657** (`DocumentPosting.cs:199-209`). Adjustments are one product at a time, with no count document.
- **B-F09 (gap) — Scrap moves to 318 at full cost** (`DocumentPosting.cs:99-116`); 756 is seeded but nothing posts to it, so a sold scrap item never leaves 318.
- **B-F10 (gap) — Stock entries are dated at the moment of saving** (`DocumentPosting.cs:34,70,108,152,180,202`), not the delivery date.
- **B-F11 (gap) — Not built:** supplier invoices, purchase orders, supplier returns, supplier credit notes, purchase discounts.
- **B-F12 (gap) — The receiving screen takes one product per note** (`stock-tab.component.ts:103-116`), though the API accepts several.
- **B-F13 (gap) — Transport is not linked to the transfer** (D47 not built).
- **B-F14 (design) — 181 is never posted;** it is a computed residual on a single branch's balance sheet (`LedgerReports.cs:212-229`).
- **B-F15 (gap → setting, 2026-11-09) — P0.3 built as the accountant's setting, off by default** (`ReceivableDocuments.TakeGoodsOutAsync`,
  `StockValuationSettings.DirectInvoiceStock`, Reports › Stock valuation › "An invoice typed in the back office", `accounting.manage`).
  Off (today): a direct invoice posts 411 / 70x / 4457 only, moves no stock, posts no cost. On: each line whose product tracks stock
  (and is not billed by the day) writes a `StockMovement` (`InvoiceSale`) out of the invoice's branch, takes it off `ProductStock`
  (never refused for a shortfall, as a till sale; the invoice says "Stock went negative") and adds Dr 600 / Cr 310 to the invoice's
  own entry at the cost price, or at the weighted average once C-01 is on (`LiveSaleCosting`, with its cost-price fallback); the line
  keeps `StockUnitCost` / `StockCostBasis`. A hand-made credit note then takes a per-line "back in stock" quantity (0 by default, at
  most what the note credits and what the line took out less what came back): a `StockMovement` (`InvoiceReturn`) in and Dr 310 /
  Cr 600 in the note's entry, at the invoice line's own cost. Invoices issued while it was off are never touched (no catch-up) and
  their goods cannot come back through a credit note. **Note:** the route has refused typed invoices since 2026-10-10
  (`invoice_needs_source`), so in production the setting takes effect only if that route is reopened; the service, the credit-note
  side and the tests are ready. Pinned by `DirectInvoiceStockTests`.
- **B-F16 (gap) — `AssetUnitValue` is stored but never posted;** there is no depreciation.
- **B-F17 (risk) — Editing a product's cost posts nothing and keeps no history** (`CatalogEndpoints.cs:694`).
- **B-F18 (risk) — A supplier with a balance can be deleted** (`SupplierEndpoints.cs:73-82`).
- **B-F19 (risk) — A sale's cost is read when the sale reaches the server,** not when it happened (same as F-A03).
- **B-F20 — Transfers work as documented** (`StockTransfers.cs:60-274`).

### Needs verification (live database)
- The size of the 310 drift; how many 401 lines name no supplier; products with cost 0 that hold stock; whether any branch has its own tax number.
- Whether branches use the "purchase" expense voucher for goods they sell or rent.

## Payroll, CNAS and treasury (D)

### Confirmed in code
- **D-F01 — P0.2 fixed 2026-11-09.** The three payroll builders (`PayrollPosting.Payment`, `Advance`, `StatutoryPayment`) no longer mark a wage paid from another branch's box as inter-branch; the paying branch is still named and its cash line still sits there. `LedgerReports.ReadAsync` keeps the three payroll source types whatever their old mark, so past group reports read right with no posted row edited. Affected past entries: `LedgerEntries` where `IsInterBranch` and `SourceType` in (`wage-payment`, `wage-advance`, `payroll-statutory-payment`).
- **D-F02 — CNAS/IRG being off is hard-coded, not a setting** (`PayrollDocuments.cs:177`, `:500`). The preset rates are still placeholders (10 % / 30 %, IRG brackets, SNMG 10,000, 26 days).
- **D-F03 — A statutory payment can be recorded with nothing accrued,** taking 431/442 into debit; the system only warns (`WageMoney.cs:529-533`).
- **D-F04 — A run's accrual is dated the end of its pay month, but its reversal is dated today** (`LedgerPoster.cs:193`); there is no closed-period lock.
- **D-F05 — "Other" deductions are credited to 641, and pay that would go below zero goes to 471** (`PayrollPosting.cs:109-134`).
- **D-F06 — The next run recovers the whole open advance.** Loans repaid in instalments are not built, and there is no write-off.
- **D-F07 — No pro-rata by hire or leave date, no final settlement, no leave balance.**
- **D-F08 — The "daily labour" expense voucher posts to 631** (`PaymentVouchers.cs:87`).
- **D-F09 — A paid voucher cannot be undone** (`PaymentVouchers.cs:533`). The generic `/ledger/entries/{id}/reverse` reverses only the entry: no cash back in the box, no voucher status change, no customer or supplier balance change.
- **D-F10 — Not built:** an opening cash movement, a bank deposit, a counted-cash over/short at closing, a manual journal entry, a bank-account list or reconciliation, a petty-cash box.
- **D-F11 — Cheques are booked differently at the till (413) and in the back office** (nothing until confirmed, then dated the receipt day, `ReceivablePosting.cs:93`). A cheque cannot bounce after it has been confirmed.
- **D-F12 — Card payments go straight to 512; fees are never recorded.**
- **D-F13 — Payments can take a cash box negative with only a warning,** while a remittance above the balance is refused.
- **D-F14 — Remittances and voucher payments are always dated now** and cannot be back-dated.
- **D-F15 — Stamp duty is not built anywhere.**

### Needs verification
- Production payroll settings: ÷30 versus 26 days, overtime multipliers, any "other" deductions configured.
- Whether any runs, or cross-branch wage payments, are already posted in production.
- Whether the Z-report's default UTC-day window (`SalesEndpoints.cs:40`) is always overridden by the screen.
- Whether a screen exists to create extra cash boxes; only the seeder creates them.

## Rentals and maintenance (C)

### Confirmed in code
1. **The whole agreed rental period is booked as income at the issue note, never spread (risk).** `ParadisERP.Infrastructure/Ledger/RentalPosting.cs:75-97` posts Dr 411 / Cr 706 / Cr 4457 for the full period, called at `ParadisERP.Infrastructure/Rentals/RentalDocuments.cs:790`. Unused days are reversed only at the return (`RentalPosting.cs:124-157`, `RentalDocuments.cs:1103`). No account for income received in advance is seeded (`ParadisERP.Domain/LedgerEntities.cs:341-396`). Profit is front-loaded at month and year ends. (REN-02)
2. **Rental and return entries are dated the day they are typed, not the note's date (bug/risk).** *Issue entries fixed 2026-11-01 (REN-02): now dated on the period's first day. Return entries unchanged.* `RentalPosting.cs:83`, `:131` use `EntryDate = DateTime.UtcNow`, while the note takes a chosen date (`RentalDocuments.cs:697`, `:865`; the UI sends it, `rental-issue-note.component.ts:576`). The stock entries in the same return also use today (`DocumentPosting.cs:108`, `:131`). Vouchers and receipts use their own dates (`PaymentVouchers.cs:848`, `ReceivablePosting.cs:91`). A note typed after a month or year end lands in the wrong period. (REN-15)
3. **The hire tax rate defaults to 0 % and applies to every charge on a line (risk).** Setting at `ParadisERP.Domain/Entities.cs:254-263`; frozen on the contract line for hire, overdue, repairs and compensation alike (`RentalEntities.cs:258-265`); return tax at `RentalDocuments.cs:1073-1082`. Rent and compensation cannot be taxed differently, and a later rate change never reaches signed contracts. (REN-01, REN-08, REN-17)
4. **Rental invoices are paper only (gap).** Invoices not raised directly post nothing (`ReceivablePosting.cs:39`); a rental invoice is built from the note (`ReceivableDocuments.cs:330-371`), taxed or not by the line rates (`:354`). Nothing forces or reminds anyone to invoice a note. (REN-04)
5. **Early and same-day returns.** The relief posts on the return note (`RentalPosting.cs:146-153`), first netted against the customer's whole balance (`RentalDocuments.cs:1083-1095`, `RentalSettlement.cs:337-342`); any refund is a separately approved voucher, Dr 411 / Cr cash or bank (`PaymentVouchers.cs:840-862`). The credit note is created without a second posting (`RentalReturnCredits.cs:30-103`); an untaxed one carries zero tax (`:88`) and is labelled "Internal adjustment". (REN-05)
6. **Late days go to 708; waived amounts leave no money trace (design).** Mapping `RentalPosting.cs:39-47`; waivers and deductions `RentalLateReturn.cs:41-116`, applied at `RentalDocuments.cs:997-1018`; only the reduced charge posts. (REN-06, REN-07)
7. **Lost and wrecked units are valued at catalogue `CostPrice`, and wrecks are kept at full cost (risk).** Value computed at `RentalDocuments.cs:1175`, posted at `:1229-1233`; condition buckets `:1270-1277`, accounts `LedgerEntities.cs:405-410`. A wreck moves Dr 318 / Cr 310 at full cost while the customer is charged compensation to 757; nothing writes the wreck down, so profit is overstated until the scrap is disposed of. Scrap sales (756) never post anywhere (Not built). A product with `CostPrice` 0 posts nothing. (REN-08, REN-09, REN-10)
8. **`Product.AssetUnitValue` (the design's "book value") is unused by any posting or report (gap).** Defined `Entities.cs:521-528`, written at `CatalogEndpoints.cs:702` and `StockEndpoints.cs:95`; every posting uses `CostPrice`. Contradicts docs/06 §3.3.
9. **Rental equipment is merchandise stock (310) with no depreciation; the decision-8 setting is not built (gap).** *Built 2026-11-09 as REN-12's register (M7a, [fixed-assets.md](fixed-assets.md)), off until the accountant switches it on; `AssetUnitValue` is now one of its opening-value options.* `LedgerEntities.cs:398-410`; no class-2 (fixed asset) or depreciation account in the seeded chart. (REN-12)
10. **Deposit: recorded and shown only (matches the docs).** Logic `RentalRules.cs:261-304`; stored at `RentalDocuments.cs:252`, `:327`; negative refused at `:537-539`; switch off by default (`Entities.cs:251`). Account 419 is seeded but nothing posts to it. Never posted, never netted, never blocks closing. Not built: collecting a deposit, keeping part of it, refunding part. The owner said no deposit is taken today (answer 14); the account (419 or 165) is still open as docs/07 Q7 / owner question 53. (REN-13, REN-14)
11. **One contract's money can be split across branches.** Rent posts at the issuing branch; returns must be taken at the contract's branch (`RentalDocuments.cs:85-86`, `:856-861`, owner 2026-09-30, superseding docs/06 decision 2); goods issued elsewhere come back by implicit transfer (`:1193-1225`); missing units are written off at the issuing branch (`:1167-1170`). (REN-16)
12. **A contract with no customer posts to suspense (471)** (`RentalPosting.cs:90-93`, `:127`). Whether the screens allow such a contract: needs verification.
13. **Maintenance and repair module: Not built.** No repair order, job, service product, or job labour/parts record exists; `docs/manufacturing/costing-plan.md:714` lists it as out of scope. What exists: the "maintenance" stock bucket is moved by hand and posts nothing (`StockEndpoints.cs:229-282`); a repair charged to a rental customer goes to 707 as one typed amount per return line; an expense voucher of category "repair" books the gross amount to 615 (`PaymentVouchers.cs:79-88`) and may name a contract (`:217-231`) but never an item; receipt kinds "repair" and "loss" are labels only; spare parts leave stock only by stock adjustment, Dr 657 / Cr 310 (`DocumentPosting.cs:199-209`). No deductible-TVA account exists. (MNT-01 to MNT-10)

### Needs verification
- The hire tax rate and the deposit switch on the live company.
- How the tax report treats rental TVA booked at issue (Agent E).
- Whether a list of notes not yet invoiced exists.
- Whether same-day usage is deployed (the code is on main; it has been live since at least 2026-10-20 per git — see work status).

## Manufacturing (F)

### Confirmed in code
1. Completion approval never freezes the cost; cost sheets and late costs are created only through the API — there is no screen for either.
2. Expenses count in full in costing even when their payment voucher is cancelled.
3. A late cost naming a source already frozen in the sheet is marked "uncertain" and left out of the adjusted cost (`ParadisERP.Application/Manufacturing/CostReconciliation.cs:175-197`).
4. Expenses can still be attached to an order after it is completed.
5. An order whose pieces all fail at the last stage can be neither completed nor cancelled (`ProductionOrders.cs:364`, `:471`). Found by reading; not tested at run time.
6. An order's rejected quantity sums rejects from every stage, so spoilage is overstated (`ProductionLogs.cs:357`).
7. Rejects and offcuts never enter stock; the recovery value is always 0.
8. A material with no cost does not block freezing the cost sheet.
9. Payment vouchers carry no TVA split, so an expense cannot be counted before recoverable VAT (C-07).
10. "Daily labour" vouchers are counted as order expenses, not as labour.
11. The final release can come out negative.

### Needs verification
- Whether order expenses are rounded to 2 decimals while costing works in 3.
- Finding 5 at run time.

### Source note
The planning document's "Accountant sign-off" tab was read directly: D9, D13, D33, D43, D45, D47, P0.2, P0.3, P0.4c and the service-income row are all still undecided.

## Tax, general ledger and closing (E)

### What posts to the ledger today

| Event | Posts? | Entry | Entry date | Code |
|---|---|---|---|---|
| Till sale or refund | Yes | Cr 700 (total − tax), Cr 4457, 758 rounding (excess to 471); Dr 530 / 512 / 411 / 413 / 4181 / 4182 by tender; Dr 600 / Cr 310 at `CostPrice` | Till's `SoldAtUtc` | `SalePosting.cs:33-120` |
| Invoice raised over a sale or rental note | No | – | – | `ReceivableDocuments.cs:148-166` |
| Invoice typed directly (closed for new invoices) | Yes | Dr 411 / Cr income / Cr 4457 | Date typed | `ReceivablePosting.cs:36-66` |
| Credit note made by hand | Yes | Reverses income, 4457 and 411 | Date typed (`ReceivableDocuments.cs:813`) | `ReceivablePosting.cs:118-140` |
| Automatic credit note | No | – | – | – |
| Confirmed receipt | Yes | Dr 530 or 512 / Cr 411 | Date typed (`:433`) | – |
| Cheque settlement | Yes | Dr 512 or 411 / Cr 413 | Now | – |
| Remittance | Yes | 530 → 530 between branches | Now | – |
| Payment voucher | Yes | Full amount, no VAT split | When marked paid | `PaymentVouchers.cs:79-89, 840-863` |
| Rental issue | Yes | Dr 411 (or 471) / Cr 706 / Cr 4457 | Now, not the note's date | `RentalPosting.cs:75-99` |
| Rental return | Yes | 706 / 707 / 708 / 757, unused rent, ±4457, ±411 | Now | – |
| Receiving note | Yes | Dr 310 / Cr 401, no VAT | Now | `DocumentPosting.cs:29-52` |
| Opening stock | Yes | Dr 310 / Cr 890 | Now | – |
| Customer and supplier opening balances | Yes | Against 890 | Chosen day | – |
| Stock adjustment, state change, transfer, loss | Yes | 310 / 318 / 657 | Now | – |
| Payroll approval | Yes | Accrual (net held at zero to 471) | Period end | – |
| Wage payment, advance, payment to CNAS or the tax office | Yes | – | Date typed | – |
| Gift card issued, loyalty points earned | **No** | – | – | – |
| Manufacturing | **No** | – | – | – |
| Cash count difference, opening float, cash deposited at the bank | **No** (no route) | – | – | – |
| Supplier invoice VAT, bank fees, tax payments, capital, loans, depreciation | **Not built** | – | – | – |

### Confirmed in code
1. **Till sales start untaxed by default, with no permission check.** `pos.service.ts:44-50`; the checkbox has no permission (`register.component.html:305-313`). Possible under-declared VAT. (TAX-01) **Fixed 2026-11-02 (owner's choice; live since 2026-11-02):** a new permission `sales.untaxed`, "sell untaxed at the till". The Admin and Manager roles hold it; a cashier holds it per user, ticked on the users screen, and the migration `SalesUntaxedGrant` gave it to every cashier on file, so nothing changes on deploy day. A seller whose sign-in says no starts every sale taxed and cannot untick the box, which says why in words. A sale is never refused; a till that has not been told (a session from before, an offline PIN unlock of a cashier who has not signed in online since) keeps the untaxed start.
2. **The untaxed-invoice permission is bypassed for invoices raised over a sale.** The check reads `r.TaxMode` (`ReceivableEndpoints.cs:138`), but the invoice inherits the sale's tax mode (`ReceivableDocuments.cs:303-308`). **Fixed 2026-11-02 (live since 2026-11-02):** invoicing a sale made without tax needs `finance.invoices.untaxed` (403 `untaxed_forbidden`), decided after the branch check. Rental invoices are unchanged: their tax comes from the rental rate setting.
3. **No 4456 (VAT paid on purchases).** Receiving posts only the before-tax value to 401 (`StockEndpoints.cs:124-129`); paying the supplier's full invoice leaves 401 in debit by the VAT. (TAX-05)
4. **The sales summary is the only tax report, and it is incomplete.** It groups by rate without a `TaxMode` filter, so untaxed sales land in the same 0 % line as exempt goods (`SalesQueries.cs:118-125`); till sales only. The comment at `Entities.cs:850-856` says untaxed sales are excluded, which is false. No G50 report.
5. **Not built:** stamp duty, TAP, IBS, IFU, G50, any e-invoicing format.
6. **Rental tax:** one company rate, 0 by default (`Entities.cs:263`), frozen on each contract line and applied to every kind of charge (`RentalEntities.cs:258-265`).
7. **One company-wide document number (`Count + 1`)** for invoices, credit notes, receipts and payment vouchers (`ReceivableDocuments.cs:131, 497, 873`).
8. **Any past month can be posted into.** *Fixed 2026-11-01 (CLO-01): closed months refuse postings.* No period or fiscal-year check in `LedgerPoster.PostAsync` (`:115-171`); offline sales older than 31 days only warn (`SyncService.cs:410-413, 645-661`); the only lock is payroll attendance (`AttendanceSheet.cs:560-571`).
9. **Posting dates are inconsistent:** some documents post on their own date, others on the time keyed (`DocumentPosting.cs:34,70,108,131,152,180,202,235,268`; `RentalPosting.cs:83,131`).
10. **Bug: ledger month boundaries depend on the browser's time zone.** `accounting-tab.component.ts:1154-1156, 1338-1350, 1583` builds months in browser-local time; the server's default month is UTC (`LedgerEndpoints.cs:244-247`). Breaks CLAUDE.md rule 4 (dates are the app's).
11. **The trial balance shows only the period's movements** (`LedgerReports.cs:85-113`), not each account's balance.
12. **No year-end close.** The balance sheet's result sums all income and expenses since the first entry (`LedgerReports.cs:201-206`).
13. **No manual journal entries, journal codes or entry numbers** (`LedgerEndpoints.cs:12-27`).
14. **Reversing an entry directly leaves the document and balances unchanged.** `POST /api/ledger/entries/{id}/reverse` touches neither `Customer.Balance`, stock nor the document; the document can never be posted again because the original entry still exists (`LedgerPoster.cs:130-133`). No screen calls this route.
15. **Corrections were posted with today's date.** `ChangeRestatement.cs:104` and `RentalDailyPricingUpgrade.cs:115-130`; the rental pricing correction changed earlier months' VAT in the month it ran.
16. **Gift card and loyalty accounts only ever receive debits** (see F-A01, F-A02).
17. **Chart issues** (`LedgerEntities.cs:205-393`; the full SCF review is in `scf-chart-review.md`): 4181/4182 for gift card and loyalty liabilities; 890 for opening balances; 471 (suspense) typed as an asset; 181 computed in reports but never posted; 758 takes rounding both ways; the expense-category-to-account map is fixed in code; missing classes 10–16, 2/28, 4456/445x, 627, 64, 66/76.
18. **Rental equipment sits in 310 with no depreciation** (design, docs/06 §3).
19. **The opening balance covers stock, customers and suppliers only.**
20. **Stocktake gains are credited to 657** and dated the day keyed; no count-sheet document.
21. **No day close.** The "Z-report" is a sales summary over any date range; no route records a cash count.
22. **No problem found:** tax is calculated on each rate's total, rounded the same way on the till and the server (`money.ts:143-180`, `DocumentTax.cs:26-37`).

### Needs verification (live data)
- Live tax settings and cash rounding step. **Rental tax rate: 0 %** (read-only check of 2026-11-01; deposits off).
- Whether invoices typed directly in the back office exist in production. **None:** production has no back-office invoices of any kind (2026-11-01).
- Live balances of 471, 4181/4182, and suppliers whose 401 shows a debit. **2026-11-01, whole history, all branches:** 471 is 80 DA in credit; 4181 and 4182 have never been posted to; suppliers not checked one by one (401 is 37,638,700 DA in credit overall). VAT collected (4457) is 3,811.40 DA against 2,170,286 DA of net income in 700, 706 and 708, in line with E#1 and the 0 % hire rate.
- 890 has 11,585,750 DA of debits matching 11,585,750 DA of credits to 310: opening stock taken back out at some point. Which documents did it is not traced.
- Whether an export to the accountant's software is planned (nothing found in code or docs).

