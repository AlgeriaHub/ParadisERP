# AlgeriaPos — Accountant Questions: Original Wording

*Internal record, not for the accountant. Kept so that no question is lost.*

On 31 Oct 2026 the owner decided, closed or narrowed the questions below. `accountant-questions.md` now carries the revised wording (or, for GL-01 and D33, no longer asks them). On 1 Nov 2026 the owner also made the account numbering in C-04, REN-18 and PAY-02 internal, so those three are no longer asked either, and `accounting-decision-tracker.md` records each decision and its status. This file keeps each question exactly as it was first written in commit `9750b32`, for traceability.

| ID | What happened |
|---|---|
| SAL-02 | Approved by the owner; now an information note |
| SAL-17 | Policy approved by the owner; narrowed to expiry and VAT timing |
| REN-01 | Owner: hire carries VAT; narrowed to the rate |
| REN-02 | Owner: full rent on day one; narrowed to the period-end adjustment |
| REN-03 | Owner workflow recorded; asked together with D45 |
| REN-12 | Owner: fixed assets; narrowed to depreciation and the switch |
| PAY-01 | Owner keeps the payslip; narrowed to a CNAS/IRG compliance check |
| TAX-01 | Feature question closed; narrowed to legal eligibility |
| TAX-05 | Feature question closed; narrowed to deductible purchase VAT |
| TAX-06 | General reporting question closed; narrowed to the G50 lines |
| TAX-09 | Feature question closed; narrowed to confirming the rule |
| D45 | Owner workflow recorded; merged with REN-03 on legal timing |
| GL-01 | Closed by the owner: AlgeriaPos is the official books. Removed from the questionnaire |
| D33 | Closed for the accountant: the chart is reviewed internally. Removed from the questionnaire |
| CLO-01 | Approved by the owner; now an information note |
| C-04 | Numbering made internal by the owner on 1 Nov 2026; answered by the SCF review (`scf-chart-review.md`). Removed from the questionnaire |
| REN-18 | As C-04. Its wording changed slightly on 31 Oct; the original is below |
| PAY-02 | As C-04 |

---
### SAL-02 — Cost of goods sold at the moment of sale, and products with no cost

**Question:** When a product is sold, the system records its cost (Dr 600 cost of goods sold / Cr 310 stock). If nobody has entered a cost for the product, what should happen?

**What AlgeriaPos does today:** When the sale reaches the server, it posts Dr **600** / Cr **310** at the product's current cost price (the last purchase price, overwritten at each goods receipt). If the cost price is zero, **no cost is posted at all**, but the quantity still leaves stock. Services (no stock tracking) post no cost on purpose. For a sale made offline, the cost used is the one on file **when the sale arrives at the server**, not when it was made. The costing method itself (last price versus average) is a separate question (see STOCK — C-01 / D9).

**Why I'm asking:** A product sold with no cost shows 100% margin, and the stock account (310) keeps a value for goods that are gone. Offline sales can also pick up a newer purchase price than the one in force on the sale day.

**Example:** We sell 10 bags of cement whose cost was never entered, for 12,000 DA. The books show 12,000 DA profit and the 10 bags remain in the value of account 310 although they left the shop.

**Possible answers:**
- A. Post nothing (as today), but list every sale without a cost so it can be corrected later with an adjustment.
- B. Post the cost at a fallback figure (for example the last known purchase price of any branch) and flag it.
- C. Do not let a product be sold without a cost (this would block sales, which the owner does not allow).

**Our suggestion:** A, with a monthly "sold without cost" list and a correction entry once the cost is known. Use the cost on the sale day for offline sales.

**Priority:** Critical

**Accountant's answer:**

> 

---

### SAL-17 — Gift cards: the money received is not recorded

*This question also answers: GL-05 (Which unposted events must post (gift card sale, loyalty, cash differences, bank deposits)?).*

**Question:** When we sell a gift card, how should the money received and the promise to the card holder be recorded, and what happens to an unused balance when the card expires?

**What AlgeriaPos does today:** A gift card is issued in the back office with an amount, but **no money is recorded and no entry is posted**. When the card is spent at the till, the sale goes to 700 and Dr **4181** "Gift card liability". So 4181 grows as a debit balance, and the cash paid for the card never reaches the books. A refund put onto a card credits 4181. Expiry posts nothing.

**Why I'm asking:** Cash received for cards is missing from the books, and the liability for unspent cards is not shown.

**Example:** We sell a 10,000 DA gift card for cash. Today nothing is posted. The holder spends it on a 10,000 DA purchase: 4181 is debited 10,000 DA, with no credit before it.

**Possible answers:**
- A. On sale of the card: Dr 530 / Cr 4181; on use: Dr 4181 / Cr 700 and tax; on expiry: Dr 4181 / Cr other income.
- B. Treat the card sale itself as a sale (income and tax at once).

**Our suggestion:** A, with the tax due when the card is spent. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### REN-01 — Do we charge TVA on equipment hire?

*This question also answers: TAX-11 (Is hire taxable, and at what rate for rent, overdue, repairs, damage?).*

**Question:** Must our equipment hire (scaffolding, formwork, etc.) carry TVA, and at what rate?

**What AlgeriaPos does today:** Hire has its own tax rate setting, separate from the sales rate. It is **0 % by default** ("hire not taxed"). Whatever rate is set is copied onto each contract line when the contract is signed and is then used for everything that line charges: rent, late days, repairs and lost/wrecked items. The rate the live company has actually set: Needs verification.

**Why I'm asking:** If hire is taxable and the setting is still 0 %, every rental invoice since the start is missing its TVA, and the tax we owe is understated.

**Example:** 100 scaffold frames × 30 days × 50 DA/day = 150,000 DA rent. At 0 % the customer pays 150,000 DA. At 19 % it would be 150,000 + 28,500 = 178,500 DA, and 28,500 DA is owed to the tax office.

**Possible answers:**
- A. Hire is taxable at the standard rate (19 % in the Algeria preset); set the hire rate to it.
- B. Hire is taxable at another rate (please name it).
- C. Hire is not taxable for our activity (please say why, so we can write it down).

**Our suggestion:** A, unless you know of an exemption for our activity. If so, also tell us what to do with contracts already signed at 0 % (see REN-17 for rate changes). See TAX for the general TVA rules. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### REN-02 — When should rent count as income: all at once when the goods leave, or day by day?

*This question also answers: CLO-10 (Hire across 31 Dec: defer next year's days?).*

**Question:** Is it acceptable to book the whole agreed rental period as income on the day the goods leave, or must income be spread over the days (or months) the goods are actually out?

**What AlgeriaPos does today:** The full agreed period is booked as income (706) on the issue note's day (owner decision, 2026-09-23). If the customer returns early, the unused days are taken back off income on the return day. Nothing splits a contract's income across months or across the year end.

**Why I'm asking:** A contract that starts in December and runs into February puts all three months of income into the old year. That makes the year's profit (and possibly its tax) too high, and the next year's too low.

**Example:** Contract issued on 1 December: 200 frames × 90 days × 50 DA = 900,000 DA. Today all 900,000 DA is December income. Spread by days, December would get 31 days = 310,000 DA and 590,000 DA would belong to January–February.

**Possible answers:**
- A. Keep it as is (all at issue); it is acceptable for us.
- B. Keep it as is during the year, but at each year end (or month end) move the part that belongs to the next period into "income received in advance" (account 487 is our guess — please name the account) and bring it back next period.
- C. Change the system so income is recognised day by day.

**Our suggestion:** B. It keeps today's working (invoice and customer debt at issue, which the owner chose) and only adds a year-end adjustment, which the system could calculate from open contracts. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### REN-03 — When is the TVA on hire due: at issue, at invoice, or when the customer pays?

*This question also answers: TAX-12 (Rental VAT due at invoicing (issue) or at payment?).*

**Question:** If hire carries TVA, on which date does that TVA become owed to the tax office: the day the goods leave, the day of the invoice, or the day the customer pays?

**What AlgeriaPos does today:** The TVA is booked to account 4457 (tax collected) at the same moment as the rent, on the issue note, for the whole period, whether or not an invoice is ever printed and whether or not the customer has paid. When the rate is 0 % (the default) nothing is booked.

**Why I'm asking:** Hire is a service. Our understanding is that tax on services may fall due at payment rather than at delivery (this is how question D45 / C-18 is phrased for advances). If so, the tax account shows TVA as owed too early, and the monthly declaration would not match it.

**Example:** Goods issued 20 March, 178,500 DA including 28,500 DA TVA. The customer pays in May. Today the 28,500 DA sits in March. If tax falls due on payment, it belongs to the May declaration.

**Possible answers:**
- A. Due when the goods leave (as today).
- B. Due on the invoice date.
- C. Due when the customer pays (and in proportion if he pays part).

**Our suggestion:** Please answer this together with TAX (tax on advances, D45 / C-18), so sales, services and hire follow one rule. We will make it a setting, not fixed in the code. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### REN-12 — Is our rental equipment stock, or fixed assets that must be depreciated?

*This question also answers: STK-07 (Rental equipment kept in 310 without depreciation); GL-04 (Hire equipment as stock (310, no depreciation) or fixed assets?).*

**Question:** Should the scaffolding and other equipment we hire out be in stock (as merchandise) or be fixed assets (equipment) with yearly depreciation?

**What AlgeriaPos does today:** Rental items are ordinary products in merchandise stock (**310**), valued at catalogue cost price. Being out on hire or in repair changes only the quantity columns, not the value. There is **no depreciation** (our design notes: "value falls because items are removed, not because a periodic calculation reduces it"). The owner's decision 8 (2026-09-20) was "current assets, but held as a setting"; that setting is **not built**. The same stock can be sold at the till and hired out (owner, 2026-10-06: one stock). Fixed assets and depreciation: Not built.

**Why I'm asking:** This changes the balance sheet (stock vs equipment), the yearly profit (depreciation expense) and possibly the tax result. It also decides REN-09, REN-10 and MNT-01.

**Example:** We own 2,000 frames bought at 6,500 DA = 13,000,000 DA. Today they stay at 13,000,000 DA in stock forever. As equipment depreciated over, say, 5 years, the yearly charge would be 2,600,000 DA and their value after 3 years 5,200,000 DA.

**Possible answers:**
- A. Stock (310), no depreciation, as today.
- B. Fixed assets (class 2 — please name the account), depreciated (please give the method and the number of years per type).
- C. Mixed: items held mainly for hire are fixed assets; items also sold stay in stock.

**Our suggestion:** Please tell us what the law expects; if B or C, we will need to build an equipment register with depreciation, and decide what happens to the current stock values at the switch. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### PAY-01 — CNAS and IRG are "handled outside the system": how do they reach the books?

**Question:** Since the payroll in AlgeriaPos no longer calculates CNAS (employee and employer share) or IRG, how should the social security and income tax on our wages be recorded in our accounts, and is a payslip without them acceptable as our official payslip?

**What AlgeriaPos does today:** The owner decided on 2026-10-10 that CNAS and IRG are not on the payslip, for any worker, and are handled outside the system. Since 2026-10-21 every new or reopened run has CNAS and IRG switched off. The run still posts 631 and 421, but 635, 431 and 442 stay at zero. The payslip prints no CNAS or IRG line. The switch is fixed in the program, not a setting: turning CNAS/IRG back on needs a developer. There is a "statutory payment" screen that records paying the fund or the tax office (Dr 431 or 442 / Cr bank), and it accepts a payment even when nothing is owed on that account (it only warns), so 431 or 442 would then show a debit balance (as if the fund owed us).

**Why I'm asking:** If CNAS and IRG are paid but never accrued, the books show no employer social cost (635), no debt to the fund (431) and no tax withheld (442), and the bank payment has nowhere sensible to land. Earlier the owner said workers are declared and the cost is split between company and worker (owner answers 29 and 56, 2026-10-01/02); the later decision reversed that for the payslip only.

**Example:** A worker earns 45,000 DA gross. The system pays him 45,000 DA net and books Dr 631 45,000 / Cr 421 45,000. At month end the company pays the fund for him by bank. If that payment is recorded on the statutory screen, 431 goes into debit; if it is not recorded, the bank account in the books is too high.

**Possible answers:**
- A. Turn CNAS and IRG back on in the payroll with the real rates and scale (the accountant enters them), so the payslip, 631/635/431/442 and the payments all match.
- B. Keep them out of the payslip, but each month record one entry per branch for the CNAS employer share and the amounts paid (e.g. Dr 635 / Cr 431, then Dr 431 / Cr 512) from your own payroll file. (This needs a way to enter that entry; today the system has no manual journal entry.)
- C. The accountant keeps CNAS/IRG fully in a separate payroll tool, and AlgeriaPos is only used to pay net wages; the accountant adjusts the books at closing.

**Our suggestion:** A, if the workers are declared: it is the only option where the payslip, the books and the fund's declarations agree. If the owner keeps his decision, B with a monthly accountant-prepared entry. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### TAX-01 — Selling at the till without VAT

**Question:** Is the company allowed to record a counter sale without VAT when the customer does not ask for an invoice, and if so, who should be allowed to choose that?

**What AlgeriaPos does today:** Every till sale starts as "without tax". The cashier ticks a box to add VAT. Any cashier can tick or untick it; there is no permission behind it. An untaxed sale puts nothing on account 4457 (VAT collected). In the back office, by contrast, an invoice without tax needs a special permission that only the Admin holds. The owner chose "untaxed by default" on 2026-09-25 because prices are stored before tax and most counter sales go out without a tax line.

**Why I'm asking:** Our understanding is that a company registered for VAT owes VAT on what it sells whether or not the customer wants a paper. If that is right, every untaxed till sale under-declares VAT. If it is wrong, we still need to know who may decide it.

**Example:** A walk-in customer buys 50,000 DA of pipes and pays cash. Today the cashier leaves the box unticked: the receipt says 50,000 DA "without tax" and nothing goes to 4457. Ticked, the customer would pay 59,500 DA and 9,500 DA would go to 4457.

**Possible answers:**
- A. Every sale is taxed. Remove the "without tax" choice at the till (or keep it only for customers with a written exemption).
- B. Untaxed sales are allowed, but only the manager or Admin may choose them, and the default becomes "taxed".
- C. Keep today's rule (untaxed by default, any cashier may choose).

**Our suggestion:** The owner decided "untaxed by default"; please confirm this is acceptable. If it is not, B is the smallest change: taxed by default, untaxed only with a permission and a reason, as the back office already works. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### TAX-05 — VAT we pay on purchases and expenses (deductible VAT)

*This question also answers: PUR-03 (Must purchase TVA be recorded in a deductible-TVA account?); MNT-03 (Outside repair bills with TVA: separate deductible TVA? (today all into 615)).*

**Question:** How should the system record the VAT on what we buy (goods from suppliers, fuel, repairs, transport), so we can deduct it?

**What AlgeriaPos does today:** Not built. A receiving note records goods at their purchase price only, with no VAT, and puts that same amount on the supplier's account (401). A payment voucher for an expense (fuel, transport, repair, small purchases) puts the whole amount, VAT included, on the expense account. There is no "VAT deductible" account (4456) in the chart. When we then pay the supplier the full invoice including VAT, the supplier's account goes into the wrong direction by the VAT amount.

**Why I'm asking:** Without it, deductible VAT is lost from the books, expenses are overstated, and supplier balances never match their statements. (For manufacturing order expenses, see also C-07, owned by the manufacturing review.)

**Example:** Supplier invoice: goods 200,000 DA + VAT 38,000 DA = 238,000 DA. Today: stock +200,000, supplier owed 200,000. We pay 238,000 DA, so the supplier shows 38,000 DA "paid in advance" and the 38,000 DA of VAT appears nowhere.

**Possible answers:**
- A. Record a supplier invoice with its VAT: stock (or expense) at the price before tax, 4456 for the VAT, supplier at the full amount. Same split on expense vouchers.
- B. Keep the books without deductible VAT; the accountant handles it outside the system.
- C. Only goods purchases carry VAT; expenses stay at the full amount.

**Our suggestion:** A, with the accountant naming the account (4456 or his choice) and saying which expense types carry deductible VAT. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### TAX-06 — The monthly VAT declaration (G50)

**Question:** What report do you need from the system to prepare the monthly declaration (G50), and should it be per branch or for the whole company?

**What AlgeriaPos does today:** Not built as a declaration. The nearest things are: the sales summary ("Z-report"), which shows till sales by tax rate but leaves out invoices, rentals and credit notes and puts untaxed sales in the same 0 % line as exempt goods; and the account statement of 4457 (VAT collected), which includes everything that posted. There is no figure for deductible VAT (see TAX-05).

**Why I'm asking:** The declaration must be right when it is filed. Today someone would have to build it by hand from two reports that do not cover the same things.

**Example:** October: till sales taxed 3,000,000 DA (VAT 570,000), rental issues 1,200,000 DA (VAT 228,000 if rentals are taxed), a credit note of −50,000 DA (VAT −9,500). 4457 shows 788,500 DA; the Z-report shows only 570,000.

**Possible answers:**
- A. A monthly VAT report from the ledger: taxable base and VAT by rate, collected minus deductible, per branch and total, ready to copy into the G50.
- B. A plain export of 4457 (and 4456) movements; the accountant does the rest.
- C. Nothing; the accountant works from his own records.

**Our suggestion:** A, built from the ledger so it matches the books. Please tell us the lines of the G50 you fill from our figures. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### TAX-09 — Stamp duty on cash payments (droit de timbre)

*This question also answers: TRS-12 (Stamp duty on cash payments (not built)).*

**Question:** Does stamp duty apply to our invoices or receipts paid in cash, at what rate and limits, and must it appear on the paper?

**What AlgeriaPos does today:** Not built. Nothing in the system calculates, prints or records stamp duty.

**Why I'm asking:** If it is due on cash-paid invoices, every such invoice is missing it today, and it needs its own account and its own line in the declaration.

**Example:** An invoice of 238,000 DA paid in cash: is a stamp duty added on top, and how much?

**Possible answers:**
- A. Yes: rate, minimum and maximum, which documents, and the account to use.
- B. No, not for our activity.

**Our suggestion:** Please give us the rule as you apply it; we will make the rate a company setting, not a fixed number. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### D45 — Tax on customer advances

**Question:** When a customer pays in advance (before the goods leave or the work is done), is VAT due on that advance at the moment we receive it?

**What AlgeriaPos does today:** A receipt voucher always reduces what the customer owes (account 411). Money received before any invoice simply leaves the customer's account in credit. No VAT is calculated on any advance, and nothing posts to account 419 (customer advances), which is in the chart but unused. The manufacturing plan proposes 419 for advances on orders and treating the tax on advances as a setting (`costing-accountant-signoff.md` §5).

**Why I'm asking:** Our understanding is that VAT on goods falls due at delivery, and on services at payment, so an advance on goods carries no VAT when received but an advance on a service (hire, repair) might. If that is right, today's books miss VAT on service advances.

**Example:** A contractor pays 500,000 DA on 20 October for a hire starting 5 November. Today: cash +500,000, customer account −500,000, no VAT. If VAT is due on receipt: 79,832 DA of it (500,000 × 19/119) belongs to October.

**Possible answers:**
- A. No VAT on any advance; VAT when invoiced (today).
- B. VAT on advances for services (hire, repairs) when received; none on goods.
- C. VAT on every advance when received.
- D. And separately: advances stay on 411 (today) or move to 419 until the invoice.

**Our suggestion:** B for tax, and 419 for advances so they show apart from trade debt, as proposed in the manufacturing sign-off. Please confirm or correct. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### GL-01 — Are these the company's official books?

**Question:** Will you keep the company's official books in AlgeriaPos, or in your own accounting software, with AlgeriaPos only supplying figures?

**What AlgeriaPos does today:** Every sale, rental, invoice, receipt, voucher, stock movement and payroll run posts automatically to a double-entry ledger with trial balance, profit and loss, balance sheet and account statements. There is no export to accounting software (Needs verification of any planned format: none found in code).

**Why I'm asking:** It changes almost every answer below. Official books need period closing, a numbered journal and year-end closing; a feeder system needs a clean monthly export.

**Example:** End of October: either you close October in AlgeriaPos, or you import October's entries into your software.

**Possible answers:**
- A. AlgeriaPos is the official ledger.
- B. AlgeriaPos feeds my software: I need a monthly export (format: …).
- C. I re-key from AlgeriaPos reports.

**Our suggestion:** B for the first year, so you keep control while we close the gaps below; then decide on A.

**Priority:** Critical

**Accountant's answer:**

> 

---

### D33 — The chart of accounts and which account each event uses

**Question:** Please review every account the system posts to and confirm its number and name, or give us yours.

**What AlgeriaPos does today:** The chart seeded on every company: 890 opening balance, 181 inter-branch account; 310 merchandise stock, 318 scrap stock; 401 suppliers, 411 customers, 413 cheques not yet cleared, 4181 gift cards, 4182 loyalty points, 419 customer advances (unused), 4457 VAT collected, 471 suspense; 421, 425, 431, 442, 447 (payroll); 512 bank, 530 cash; 600 cost of goods sold, 615 repairs, 6061 fuel, 6068 small purchases, 624 transport, 630 (old heading, unused), 631, 635, 641 (payroll), 657 stock losses, 658 other expenses; 700 sales, 706 rental income, 707 repairs charged to customers, 708 overdue days, 756 scrap sales, 757 damage compensation, 758 rounding. Expense vouchers map fuel → 6061, transport → 624, repair → 615, purchase → 6068, day labour → 631, other → 658, fixed in code. A code cannot change once something has posted to it.

**Why I'm asking:** Some numbers come from the French chart and may mean something else in the SCF. For example, we believe 418x usually means "customers — invoices to be issued" rather than gift cards, 890 is a French habit for opening balances, and 707 is used for repairs re-charged. Suspense (471) is set up as an asset.

**Example:** A lost frame charged 6,000 DA goes to 757 today; you may want 758 or 75x.

**Possible answers:**
- A. All confirmed.
- B. Confirmed with these changes: … (before go-live of the affected module).

**Our suggestion:** Please mark each line "OK" or give the code. Accounts already used cannot be renumbered, so we would add your code and move future postings to it. (Service income 700/706: see the sales section — service income; payroll codes: see the payroll section — C-06 and the eight payroll accounts.) **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### CLO-01 — Closing a month so nobody changes it

**Question:** After you have checked and declared a month, should the system lock it so nothing can be posted into it any more, and who may unlock it?

**What AlgeriaPos does today:** Not built. There is no period lock, no month close and no fiscal year in the books. Any document can post into any past date. The only lock in the system is for payroll attendance: a month whose payroll is approved cannot have its attendance changed.

**Why I'm asking:** Without a lock, October's figures can change after the October VAT is declared and the accounts are reviewed.

**Example:** You declare October's VAT on 15 November. On 20 November someone records a credit note dated 25 October of −11,900 DA: October's 4457 drops by 1,900 DA after it was declared.

**Possible answers:**
- A. Lock each month when you say so; only you (or the Admin) can reopen it, with a reason.
- B. Lock automatically after a fixed day of the next month (for example the 20th).
- C. No lock.

**Our suggestion:** A, with anything dated in a locked month moved to the first open day (see SAL-14 and COR-E01).

**Priority:** Critical

**Accountant's answer:**

> 

---

### C-04 — Accounts for manufacturing (raw materials, work in progress, finished goods)

**Question:** Which account numbers should manufacturing use for raw materials, supplies, work in progress, finished goods, scrap, consumption, production stocked and sales of our own production?

**What AlgeriaPos does today:** Manufacturing records materials, hours and quantities but posts nothing. The sign-off sheet proposes 311, 321/322, 331, 351/355, 358 (or 318), 601, 602, 72 and 701; none of them exists in the chart and none is created until you name it.

**Why I'm asking:** Nothing in manufacturing can reach the books until these are named, and a code cannot change after its first posting.

**Example:** 31,500 DA of wire issued to an order: Dr 601 / Cr 311 under the proposal.

**Possible answers:**
- A. The proposed codes.
- B. Your codes (please list).
- C. Keep 310 / 600 / 700 for everything (simpler, less detail).

**Our suggestion:** A, or your codes; answer together with C-03 (accounting model). **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---


### REN-18 — Are our rental income accounts the right ones?

**Question:** Are these account numbers suitable in the national chart (SCF) for our rental income: 706 rent, 707 repairs charged to customers, 708 late-return income, 757 compensation for lost or wrecked items, 756 scrap sales?

**What AlgeriaPos does today:** These five accounts are created for every company and used as described in REN-02 to REN-11. Once something has been posted to an account its number cannot be changed. (The chart as a whole is question D33 — see TAX.)

**Why I'm asking:** We chose these numbers ourselves. If one is wrong, it is better to correct it before more entries are made.

**Example:** A return with 22,500 DA late days, 16,000 DA repairs and 108,000 DA lost frames posts 22,500 to 708, 16,000 to 707 and 108,000 to 757.

**Possible answers:**
- A. All correct.
- B. Some need different numbers (please list).

**Our suggestion:** Please review the five together with D33. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---


### PAY-02 — Confirm the eight payroll accounts before more runs are posted

**Question:** Are 421, 425, 431, 442, 447, 631, 635 and 641 the right accounts for our wages, advances, social security, income tax withheld and employer taxes?

**What AlgeriaPos does today:** Payroll posts to exactly these accounts (table above). 630 "Charges de personnel" exists but nothing posts to it. The code notes say the accountant must confirm the eight codes and that a code cannot be changed once something has posted to it. This was already asked and is still unanswered. Whether runs have already been approved in production: Needs verification.

**Why I'm asking:** Once a month is posted, a wrong code can only be fixed by reversing and reposting, month by month.

**Example:** March wages for the Skikda branch: Dr 631 1,250,000 DA / Cr 421 1,180,000 DA and Cr 425 70,000 DA (advances taken back).

**Possible answers:**
- A. Confirm all eight.
- B. Confirm with changes (please give the codes).

**Our suggestion:** A, unless your chart uses different sub-accounts (for example a separate account for each social fund). **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

