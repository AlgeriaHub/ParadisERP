# ParadisERP — Questions for Our Accountant

*Prepared 2026-10-31 from a review of the ParadisERP system as it is today. Nothing in the system was changed for this review.*

## How to use this document

This is the list of accounting decisions I need your help with. It covers the whole system, not only manufacturing: sales, purchases, stock, rentals, payroll, cash and bank, tax, the books and closing.

Each question says what ParadisERP does **today**, why I'm asking, an example in dinars, a few possible answers, and what we suggest. **Our suggestions are only suggestions.** They are not confirmed Algerian accounting or tax rules. Anything about VAT (TVA), stamp duty, CNAS, IRG or the SCF is marked *(To be confirmed by the accountant.)* There is a space under each question for your answer.

**Priority:** *Critical* = affects money, stock value, tax or the financial statements now, or blocks something we want to switch on. *Important* = should be settled soon. *Later* = can wait.

**Account numbers** are the ones the system uses today (for example 411 = what customers owe us, 401 = what we owe suppliers, 310 = goods in stock, 4457 = VAT we collect, 530 = cash, 512 = bank). Our own team checked the chart against the SCF on 1 Nov 2026 and will move the few accounts found under the wrong heading; if an account still looks wrong to you, please say so.

**IDs:** questions already sent to you earlier keep their numbers (C-01 to C-18, AD-1 to AD-12, D9, D11, D13, D43, D45, D47, P0.2, P0.3, P0.4c). Where the same decision was asked twice, it appears once and lists the other numbers it answers. A separate decision tracker lists every question with a place to record your answer. Five earlier questions are no longer asked here: GL-01 (ParadisERP is the official books), and D33, C-04, REN-18 and PAY-02 (account numbers, checked by our own team against the SCF on 1 Nov 2026); the tracker keeps their record.

There are 158 questions: 43 critical, 87 important, 28 later (REN-03 is now asked together with D45). Where the owner has already decided, the question says so at the top (**Status** and **Owner's decision**) and asks you only about the part the law decides. The 14 questions in the list below are the ones to start with.

## Decisions the owner has already made (31 Oct 2026)

These are settled on our side, so we are not asking you about them. Where the law still has a say, the question below asks only about that part.

- **ParadisERP is the company's official accounting system.** All the books are kept in it.
- **The chart of accounts** is reviewed by our own team against the SCF, including the account numbers for manufacturing, rental income and payroll (D33, C-04, REN-18, PAY-02; owner, 1 Nov 2026).
- **Taxed and untaxed sales** are both recorded in the system; we ask only which untaxed sales the law allows (TAX-01).
- **Equipment hire carries VAT** (REN-01); we ask only the rate.
- **Rent is booked in full on the first day of the rental period** (REN-02); we ask only whether a period-end adjustment is needed.
- **VAT timing in our workflow:** at the invoice for sales and for hire (D45; REN-03, owner 10 Nov 2026: hire VAT when invoiced, as today); we ask whether the law agrees.
- **Rental equipment is fixed assets** (REN-12); we ask how to depreciate it.
- **Months are closed after review**, with no back-dated postings and controlled corrections only (CLO-01).
- **Payslips and deductions stay as they are** (PAY-01); we ask you to check CNAS and IRG.
- **Gift-card money is a debt until the card is used** (SAL-17); we ask about expired balances.
- **A sale is never blocked by a missing cost**; such sales are flagged and their margin shown as incomplete (SAL-02).

## Questions We Should Ask First

| # | ID | Question | Why it matters |
|---|---|---|---|
| 1 | TAX-01 | Which sales may legally go out without VAT, and what proof must we keep? | So every untaxed sale we record is one the law allows. |
| 2 | TAX-05 | Which purchase VAT can we deduct, on which account, and what must we record from the supplier's invoice? | VAT on purchases is not recorded separately yet; we will build it from your answer. |
| 3 | D45 + REN-03 | Is VAT due on advances when received, and on hire payments as they are paid (including part payments)? | Decides which month's declaration carries the VAT. |
| 4 | REN-01 | Which VAT rate applies to hire, and to late days, repairs and lost items? | Hire carries VAT; we need the right rate on every contract. |
| 5 | REN-12 | How do we depreciate our rental equipment, and how do we move it out of stock? | The owner has decided it is fixed assets; the register will be built from your answer. |
| 6 | REN-02 | With rent booked on day one, is an adjustment needed at month end or year end? | Contracts across 31 December move income between years. |
| 7 | PAY-01 | Please check our payroll against CNAS and IRG rules: what must be declared and paid, and how should it appear in the books? | Payslips stay as they are; we need to be sure we are compliant. |
| 8 | C-01 | How should stock be valued: last purchase price, weighted average or FIFO? | The owner is keeping this decision open and would like your view. |
| 9 | CLO-08 | What is our fiscal year, and where does the year's result go at year end? | ParadisERP keeps the official books, so the year must be closed in it. |
| 10 | COR-E01 | When a mistake is found in a closed month, do we correct it in the current month or in the month itself? | The owner has decided months are locked; we need the dating rule for corrections. |
| 11 | COR-06 | Do we need a credit note for every refund, or is the refund receipt enough for private customers? | Decides which paper each refund needs. |
| 12 | SAL-17 | When may an expired gift-card balance be taken to income, and is VAT due when the card is used? | The owner has set the policy; the expiry rule needs you. |
| 13 | TAX-09 | Please confirm the stamp-duty rule: documents, rate, limits and account. | So we can check our setting against it. |
| 14 | TAX-06 | Which lines of the monthly G50 do we fill? A copy of a recent one is enough. | So we can check our reports line by line. |

## 1. Sales and Customer Payments

### SAL-01 — Service income: keep it in 700 or give it its own account?

**Question:** When we sell a service at the till or on an invoice (delivery, labour, installation), should the income stay in account 700 (merchandise sales) or go to a separate service-income account?

**What ParadisERP does today:** Every till sale and every invoice line that is not rental posts its income to **700** "Merchandise sales", whether it is a stocked product or a service. A product with stock tracking turned off is treated as a service only in the sense that no cost is posted for it. Account **706** already exists in our chart but is used for **rental income** (hire charges). 707 is maintenance charged to the customer, 708 late-return income. This is the open "service income (keep 700, or name a service account such as 706)" item from the sign-off tab.

**Why I'm asking:** If services sit in 700, the income statement shows them as sales of goods, and the gross margin on goods looks better than it is (services have no cost of goods). If we add an account, we must do it before more history builds up, because an account cannot be recoded once something has posted to it.

**Example:** A customer buys a water heater for 45,000 DA and pays 5,000 DA for delivery and installation. Today both amounts, 50,000 DA before tax, go to 700.

**Possible answers:**
- A. Keep everything in 700. Simple, but goods and services are mixed.
- B. Post services to a services account, and keep rental in its own account (for example rental moves under a sub-account of 706, and services use another sub-account).
- C. Post services to 706 and move rental to a different code you name.

**Our suggestion:** B. Mark each product as "goods" or "service" and post services to a services sub-account you name, while rental keeps its own line as the owner asked. Nothing already posted is moved. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### SAL-02 — Selling products whose cost is missing

**Status:** Approved by Owner — built on 1 Nov 2026 (not yet live). One small question below.

**Owner's decision (31 Oct 2026):** We never stop a sale because a product has no cost. The sale goes through, the product is flagged so its cost can be fixed later, and the reports show that the margin for those sales is incomplete.

**What ParadisERP does today:** The sale is allowed. When a product's cost is zero, no cost of sale is posted (nothing on 600 / 310) while the quantity still leaves stock. Each such sale line is now flagged, and a report (Reports › Sales without a cost) lists them by product for any period: the sales before tax, how many sales, and, once the cost has been entered, what those quantities would have cost. That figure is shown only; nothing is posted for past sales.

**Question for you:** once a missing cost has been entered, should we post the cost of the past sales (Dr 600 / Cr 310) in the month the cost is entered, or leave them as they are and rely on the report? **(To be confirmed by the accountant.)**

**Example:** We sell 10 bags of cement whose cost was never entered, for 12,000 DA. The sale goes through; the report will show these 12,000 DA as "margin incomplete — cost missing" until the cost is entered and corrected.

**Priority:** Critical

**Accountant's comment (optional):**

> 

---

### SAL-03 — Discounts: record the net price or show discounts separately?

**Status:** Pending Owner — we recommend option A, but the owner has not decided yet. Your view is welcome.

**Question:** When the cashier gives a discount on a line or on the whole ticket, should the sale be booked at the net price, or at the full price with the discount in its own account?

**What ParadisERP does today:** Line discounts and whole-ticket discounts are spread over the lines before the sale is saved. The sale is posted **net** to 700. The total discount is kept on the sale for reports only; no account receives it.

**Why I'm asking:** If you need to see "discounts granted" as a figure in the accounts (for example account 709), we must change the posting before the year's figures build up.

**Example:** A 20,000 DA ticket with a 1,000 DA discount. Today: 19,000 DA to 700 (plus tax on 19,000 DA). The alternative: 20,000 DA to 700 and 1,000 DA to a discounts account.

**Possible answers:**
- A. Keep posting net (as today); discounts are visible only in sales reports.
- B. Post gross to 700 and the discount to a discounts account.

**Our suggestion:** A. Discounts given on the ticket are part of the agreed price; the reports already show them. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### SAL-04 — Cash rounding: which account, and is it outside the tax?

*This question also answers: TRS-15 (Cash rounding gains and losses both on 758); GL-12 (Cash rounding both ways on 758, or split gains/losses?).*

**Question:** When a cash total is rounded (for example 1,003 DA becomes 1,000 DA), where should the small difference go, and is it right to keep it out of the tax calculation?

**What ParadisERP does today:** Rounding applies only to sales paid fully in cash, and only if the company sets a rounding step (it is off by default). The difference posts to **758** "Rounding differences" in both directions (gain or loss). It is kept out of the sales figure and out of the tax. Anything larger than one rounding step goes to **471** (suspense) as an error to investigate. A refund is never rounded: it pays back the full unrounded amount of the returned lines.

**Why I'm asking:** 758 is an income account; rounding losses may belong in an expense account. And a refund that pays back 1,003 DA on a sale where the customer handed over 1,000 DA leaves a small loss.

**Example:** Sale 1,003 DA, customer pays 1,000 DA: 3 DA debit on 758. Sale 998 DA, customer pays 1,000 DA: 2 DA credit on 758.

**Possible answers:**
- A. Keep one account (758) for both directions.
- B. Gains to 758, losses to an expense account you name.

**Our suggestion:** A, since the amounts are tiny, unless your presentation requires B. **(To be confirmed by the accountant.)**

**Priority:** Later

**Accountant's answer:**

> 

---

### SAL-06 — Sales on account at the till: due date and ageing

**Question:** When a customer takes goods on account at the till, by when must he pay, and must that debt appear in the debt-ageing report?

**What ParadisERP does today:** A sale on account immediately puts the amount on the customer's account (Dr **411**, what customers owe us). But a due date exists only on an invoice. The ageing report (current, 1–30, 31–60, 61–90, over 90 days) is built from invoices only; a credit sale that was never invoiced is shown only as an "uninvoiced" gap beside the customer's ledger balance. Customers have no payment-terms field.

**Why I'm asking:** An uninvoiced debt can never become "overdue", so nobody chases it, and your provision for doubtful debts (SAL-18) has no age to work from.

**Example:** A contractor takes 80,000 DA of materials on account on 5 September and is never invoiced. On 31 October the ageing report shows no overdue amount for him.

**Possible answers:**
- A. Give each customer payment terms (for example 30 days) and give every credit sale a due date automatically.
- B. Require an invoice for every credit sale (one per sale or one grouped invoice per month).
- C. Keep today's behaviour.

**Our suggestion:** A, plus B for company customers who need a tax invoice anyway (see SAL-07).

**Priority:** Important

**Accountant's answer:**

> 

---

### SAL-07 — Invoices for till sales: when, which date, and what number

**Question:** For a till sale, when must we issue a proper invoice (facture), which date must it carry, and must invoice numbers restart each year or follow any other rule?

**What ParadisERP does today:**
- The till prints a receipt (ticket). An invoice can be raised later in the back office "over" a sale. It copies the sale's lines and posts nothing (the sale already posted the income and tax).
- The back-office screen only offers sales paid **on account or part on account**. A sale paid fully in cash or by card cannot be invoiced from the screen (the server would accept it).
- The invoice date is typed by the user and can differ from the sale date; the income and tax stay on the sale date.
- Invoice numbers are one running sequence for the whole company (INV-000001, INV-000002 …) across all branches and all years. Credit notes (CRN-) and receipts (RCV-) work the same way.

**Why I'm asking:** A company customer who pays cash often needs a facture to deduct the tax. And if the invoice is dated in November for an October sale, the tax was declared in October while the paper says November.

**Example:** A company buys 300,000 DA of goods before tax on 28 October, pays in cash, and asks for a facture on 3 November. Today the screen cannot raise it; if raised through the server it would be dated 3 November while the tax is in October.

**Possible answers:**
- A. Allow an invoice over any sale (cash, card or on account) and force the invoice date to be the sale date.
- B. Allow any sale, and let the invoice date differ, printing the sale date on it.
- C. For numbering: keep one sequence; or restart each year; or one sequence per branch.

**Our suggestion:** A, and keep one continuous company sequence unless the rules require a yearly restart. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### SAL-08 — Customers who are companies and customers who are persons

**Question:** Should debts of company customers and of private persons be kept in separate accounts (sub-accounts of 411), or is one account 411 with a detail per customer enough?

**What ParadisERP does today:** Each customer is marked Person, Company or Unspecified (older records). All of them share one account **411**; each ledger line carries the customer, so a statement per customer and a balance per customer come straight from the ledger. Invoices freeze the buyer's name, address and fiscal identifiers at issue.

**Why I'm asking:** If your trial balance or annual statements need the split, it is easiest to add it now.

**Example:** 411 shows 2,400,000 DA owed in total: 1,900,000 DA by 12 contractors (companies) and 500,000 DA by 140 private customers.

**Possible answers:**
- A. One 411 with the per-customer detail (as today).
- B. Two sub-accounts (companies / persons).
- C. A sub-account per major customer.

**Our suggestion:** A; a report can split by customer type without new accounts.

**Priority:** Later

**Accountant's answer:**

> 

---

### SAL-09 — Customer payments not matched to an invoice

**Question:** When a customer pays without saying which invoice it is for, should the system apply the money to his oldest invoices automatically?

**What ParadisERP does today:** A receipt voucher (سند قبض) can be split over several invoices by the clerk, or left unallocated. Unallocated money reduces the customer's balance in 411 correctly, but his invoices stay "open" in the ageing report, so the ageing shows more overdue debt than the customer really owes. There is no automatic allocation.

**Why I'm asking:** The ageing report and dunning would chase a customer who has already paid.

**Example:** A customer owes two invoices of 50,000 DA (September) and 30,000 DA (October) and pays 50,000 DA "on account". 411 shows 30,000 DA owed, but ageing still shows 80,000 DA, 50,000 DA of it overdue.

**Possible answers:**
- A. Apply unallocated money to the oldest invoice first, automatically, with a way to change it.
- B. Require the clerk to allocate every receipt.
- C. Keep as today.

**Our suggestion:** A.

**Priority:** Important

**Accountant's answer:**

> 

---

### SAL-10 — Instalment payments

**Question:** When a customer buys and pays in several instalments, how should we record the plan, and can we charge interest or late fees?

**What ParadisERP does today:** No instalment plan exists. An invoice has one due date. The customer can pay any part at any time with receipt vouchers, and the full sale is income on the sale day. No interest or late-payment charge exists for sales (late income 708 is for rentals only).

**Why I'm asking:** With one due date, an instalment plan is either all "overdue" or all "not due", so the ageing is wrong; and if a price includes credit charges, part of it may not be sales income.

**Example:** A customer buys equipment for 120,000 DA (before tax) on 1 October and pays 30,000 DA on the 1st of each month from October to January.

**Possible answers:**
- A. Keep the whole sale as income on the sale day and add an instalment schedule (several due dates) for ageing only.
- B. As A, and record any extra charged for paying later as financial income, not sales.
- C. No instalment schedule; one invoice per instalment.

**Our suggestion:** A, and B only if the company starts charging for credit. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### SAL-12 — Customer advances before delivery

**Question:** When a customer pays in advance for goods we will deliver later, should the advance go to a customer-advances account (419) or simply reduce his account 411?

**What ParadisERP does today:** There are no sales orders. An advance is taken as a receipt voucher not linked to any invoice, so it credits **411** and the customer shows a credit balance. Account **419** "Customer deposits" exists in the chart but nothing posts to it (the rental deposit feature is switched off). For manufacturing orders, decision D45 already proposes 419 (Dr 530/512 / Cr 419, then Dr 419 / Cr 411 at delivery).

**Why I'm asking:** Mixing advances into 411 hides how much the company owes customers for goods not yet delivered, which the balance sheet should show as a liability. The tax on advances is a separate point (see TAX — tax on advances, D45).

**Example:** On 15 October a customer pays 100,000 DA for kitchen units to be delivered on 20 November. On 31 October, 411 shows him at −100,000 DA.

**Possible answers:**
- A. All customer advances to 419, as D45 proposes for manufacturing orders, cleared against 411 at delivery.
- B. Keep advances in 411 (as today) and list credit balances separately at year end.

**Our suggestion:** A, for consistency with D45. **(To be confirmed by the accountant.)**

**Built as a setting (9 Nov 2026, M7b):** Finance › Settings › Customer advances. 411 (today) stays the default; choosing "419 until the invoice" is answer A, nothing else to change in the program (docs/accounting/advances.md).

**Priority:** Important

**Accountant's answer:**

> 

---

### SAL-13 — Order cancelled after an advance was paid

**Question:** If a customer cancels after paying an advance, how do we record a refund, and how do we record an advance the company keeps (fully or partly) as compensation?

**What ParadisERP does today:** A refund is a payment voucher (سند صرف) to the customer: it needs a manager's approval, is capped at what the customer's account holds in his favour, and posts Dr 411 / Cr 530 or 512 when paid. Keeping all or part of an advance as compensation is **not built**: the credit would sit on 411 until someone corrects it.

**Why I'm asking:** A kept advance is income of some kind; leaving it on 411 overstates what we owe the customer.

**Example:** Advance 100,000 DA. The customer cancels; by the contract the company keeps 20,000 DA and refunds 80,000 DA.

**Possible answers:**
- A. Refund 80,000 DA by payment voucher; move 20,000 DA from the customer's account to an "other income" account you name.
- B. Refund everything and invoice the 20,000 DA separately as a cancellation charge.

**Our suggestion:** A, with a written reason on the document. The tax side of a kept advance follows TAX — tax on advances (D45). **(To be confirmed by the accountant.)**

Owner (2026-11-10): yes, answer A. Built as a setting in Finance › Settings › Customer advances: "Kept advances" is **Not
allowed** by default (today: everything is refunded); "Kept as other income" needs an active class 7 income account you
choose. The kept part posts Dr 419 (or Dr 411 for an unallocated receipt) / Cr that account, with a required written reason;
the rest is refunded by payment voucher as before. With VAT due when the advance is received, the VAT of the kept part stays
due (it is not given back). Your answer only confirms or changes the setting and the account. See
[advances.md](advances.md) "Kept on a cancelled order".

**Priority:** Important

**Accountant's answer:**

> 

---

### SAL-14 — Offline sales that reach the server late: which day and month?

*This question also answers: CLO-02 (Late offline sale dated in a closed month: which month?).*

**Question:** A till that loses its connection keeps selling and sends its sales later. Should those sales be booked on the day they were made, or on the day they reach the server?

**What ParadisERP does today:** The sale is booked on the date and time the till recorded (the device's clock). Only a date in the future is replaced by the arrival time. A sale more than 31 days old is accepted with a warning. Nothing stops it from landing in a month that was already closed or declared; there is no period lock in the ledger. Stock, cash box, customer balance and the accounting entry all take the sale date.

**Why I'm asking:** A late sale changes the sales and tax of a month already declared. Closing a month is covered elsewhere (see TAX/GL — closed-period corrections); here we need the booking date rule.

**Example:** A branch sells 350,000 DA during a five-day outage, 28 September to 2 October. The sales arrive on 15 October, after the September declaration.

**Possible answers:**
- A. Always book on the sale date, and handle a closed month by a correction in the open month.
- B. Book on the sale date if the month is still open; otherwise on the first day of the open month, keeping the real sale date on the document.
- C. Always book on the arrival date.

**Our suggestion:** B. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### SAL-15 — A customer pays at one branch for another branch's invoice

**Question:** If a customer pays at branch A for an invoice raised at branch B, which branch's accounts should show the payment?

**What ParadisERP does today:** Only a company-wide user (admin) can do it; a branch manager or cashier cannot (the owner's rule for this is still open). When an admin does it, the cash goes into branch A's box and 411 is credited **at branch A**, while the debt sits on 411 **at branch B**. No inter-branch entry (181) is made, so branch A shows the customer in credit and branch B shows him still owing, although the company total is right.

**Why I'm asking:** Branch reports and branch balance sheets show a debt that has been paid and a credit that does not exist.

**Example:** Invoice of 60,000 DA raised at Oran. The customer pays 60,000 DA in cash at Algiers. Oran 411: +60,000 DA. Algiers 411: −60,000 DA.

**Possible answers:**
- A. Cash in at A, the debt cleared at B, and an inter-branch entry (181) between them, as remittances do.
- B. Leave it as today and read customer balances only company-wide.
- C. Do not allow it; the customer pays at the branch that invoiced.

**Our suggestion:** A.

**Priority:** Important

**Accountant's answer:**

> 

---

### SAL-16 — Loyalty points

**Question:** How should loyalty points be recorded: as a discount when they are spent, or as a liability when they are earned?

**What ParadisERP does today:** Earning points posts **nothing**. Spending points as a payment posts the full sale to 700 and Dr **4182** "Loyalty liability" for the value of the points. Because nothing is ever credited to 4182, the account becomes a debit balance (it looks like an asset) that grows with every redemption. A refund takes back the points the sale earned.

**Why I'm asking:** The account 4182 is wrong today, and points that customers hold are a promise the company may have to honour.

**Example:** A customer has earned 1,000 points (worth 1,000 DA) and spends them on a 5,000 DA purchase, paying 4,000 DA in cash. Today: 700 credited 5,000 DA (before tax), 530 debited 4,000 DA, 4182 debited 1,000 DA, with no matching credit ever made.

**Possible answers:**
- A. Treat spent points as a discount: book the sale net (4,000 DA) when points are used, no liability.
- B. When points are earned, set aside part of the sale as a liability (4182); release it when they are spent or expire.
- C. When points are earned, book a marketing expense against 4182; use it when they are spent.

**Our suggestion:** A for its simplicity, or C if you want the outstanding points on the balance sheet. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### SAL-17 — Gift cards: when may an unused balance be taken to income?

*This question also answers: GL-05 (Which unposted events must post (gift card sale, loyalty, cash differences, bank deposits)?).*

**Status:** Approved by Owner (policy) — recording built on 1 Nov 2026 (not yet live); the expiry rule and VAT timing need your confirmation.

**Owner's decision (31 Oct 2026):** Money received for a gift card is a debt to the card holder, not income. It becomes income when the card is used. An unused balance stays a debt; it becomes income only when the law and the card's terms allow it.

**What ParadisERP does now (1 Nov 2026):** cards are sold for cash at a branch. The sale of a card books Dr 530 cash / Cr 4181 (the debt to the holder) and the cash goes into that branch's box. A paid top-up is booked the same way; part of a balance handed back in cash is booked the other way; a correction of a mistake books nothing. When the card is spent, the sale it pays for takes it off 4181 and books the income and the VAT. No VAT is booked when the card is sold, and nothing expires into income.

**Question:** When a card expires with money still on it, when (if ever) may we take that balance to income, and to which account? And is the VAT due when the card is used (not when it is sold)?

**Why I'm asking:** We want to build the recording once, the right way.

**Example:** A customer buys a 10,000 DA card in January and spends 6,000 DA in March. The card expires in December with 4,000 DA unused. Plan: January 10,000 DA debt; March 6,000 DA income plus VAT; December — can the 4,000 DA become income?

**Possible answers:**
- A. Expired balances go to other income on the expiry date (please name the account).
- B. Expired balances stay a debt for a legal period (please give it), then go to income.
- C. They are never taken to income.

**Our suggestion:** Your call on expiry. VAT when the card is spent. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### SAL-18 — Customers who will not pay (bad debts)

**Question:** How should we record a customer debt we no longer expect to collect: a provision first, then a final write-off? And what happens to the tax already paid on it?

**What ParadisERP does today:** Not built. There is no provision and no write-off. The only tool that reduces a customer's debt is a hand-made credit note, which also cancels the sale income and the tax, which is not what a bad debt is.

**Why I'm asking:** Without a proper write-off, staff may use credit notes, which would wrongly reduce declared sales and tax.

**Example:** A contractor owes 450,000 DA (tax included) from an invoice of March and has stopped answering. In December we judge half is lost; in the next year a court confirms it is all lost.

**Possible answers:**
- A. Provision at year end by age (from the ageing report), then a write-off when the loss is final; tax handled by your rule.
- B. Only a write-off when the loss is final; no provision.
- C. Handle bad debts outside the system, in your own entries.

**Our suggestion:** A, with permission-controlled write-off documents separate from credit notes. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

## 2. Purchases and Suppliers

### PUR-01 — Is the receiving note enough, or do we also need the supplier's invoice?

**Question:** Is it acceptable that the receiving note (the paper that says "these goods arrived") is our only purchase document, or must the system also record the supplier's invoice itself — its number, its date, the amount before tax, the tax and the total?

**What ParadisERP does today:** Goods enter only through a receiving note: the branch that receives, the product, the quantity, an optional unit purchase price, an optional supplier and an optional free-text reference. It posts stock up, account 310 (goods in stock), against account 401 (what we owe suppliers), for quantity × purchase price, dated the moment it is saved. There are no purchase orders and no supplier invoices; the owner left them out on purpose on 2026-10-10 ("not in this step"). The screen takes one product per note, so an invoice with 15 lines becomes 15 receiving notes.

**Why I'm asking:** The supplier's invoice is the legal paper behind the purchase and behind any VAT we deduct. Today the amount we owe a supplier rests only on our own count and the price the clerk typed, and the invoice date, number and tax are not kept.

**Example:** Invoice FA-2026-311 from a supplier: 40 couplers at 1,250 DA = 50,000 DA before tax, 9,500 DA TVA, 59,500 DA total. Today the clerk types 40 × 1,250 and "FA-2026-311" as the reference. The books get 50,000 DA. The 9,500 DA of tax, the invoice date and the 59,500 DA we will actually pay are recorded nowhere.

**Possible answers:**
- A. The receiving note is enough; put the supplier's invoice number in the reference (today).
- B. Add a separate supplier invoice (number, date, before tax, tax, total) that creates the debt on 401; receiving notes only move stock and are matched to the invoice.
- C. One combined form: the receiving note also carries the supplier's invoice number, its date and its tax, and can hold several products.

**Our suggestion:** C. It is the smallest change to how the branches work today, it keeps the legal invoice data with the goods, and it gives the VAT return its purchase figures (TAX-05). Separate matching (B) can come later if deliveries and invoices often differ. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### PUR-02 — Is the purchase price typed before tax?

**Question:** Should the purchase price on a receiving note always be the price before tax, so that stock and the cost of what we sell are always valued before tax?

**What ParadisERP does today:** The field is labelled "Unit purchase price" ("Prix d'achat unitaire", "سعر الشراء للوحدة"). Nothing says whether it is before or after tax, and the system records no tax on a purchase. Whatever is typed becomes both the stock value and the product's cost, which every later sale uses as its cost of sales. The costing sign-off sheet proposes "purchases enter stock at invoice cost before tax" (D43, §5 of `costing-accountant-signoff.md`); it is not signed yet.

**Why I'm asking:** If one clerk types 1,250 DA (before tax) and another 1,487.50 DA (with tax) for the same product, stock and cost of sales are overstated by 19 % on the second note, profit is understated, and the tax we could deduct is buried in stock.

**Example:** 100 props bought at 2,000 DA before tax (2,380 DA with tax). Typed before tax: stock 200,000 DA. Typed with tax: stock 238,000 DA, and when they are sold the cost of sales is 38,000 DA too high.

**Possible answers:**
- A. Always before tax; the screen says so in words.
- B. Before tax, except when the tax cannot be recovered (a supplier that charges no deductible tax, goods whose tax is not deductible); then the tax is part of the cost.
- C. The clerk types the price with tax and the system splits it.

**Our suggestion:** A, with the label changed to "price before tax", and B's exceptions only for cases you name. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### PUR-04 — The supplier delivers fewer items than invoiced

**Question:** When a supplier invoices 100 units but only 95 arrive, what should we record, and where does the missing part wait until the supplier sends the goods or a credit note?

**What ParadisERP does today:** The receiving note records only the quantity the clerk types, normally what arrived. Nothing records the invoiced quantity, so the 5 missing units appear nowhere: not as a claim on the supplier, not as a difference to follow up. Purchase orders and invoice matching are not built.

**Why I'm asking:** The supplier's statement will say we owe for 100 and ours will say 95. The difference is only found when someone compares the two statements, often months later.

**Example:** Invoice: 100 bags of cement at 900 DA = 90,000 DA before tax. 95 arrive. Typing 95 posts 85,500 DA to the supplier. The supplier claims 90,000 DA; the 4,500 DA gap is invisible in our books.

**Possible answers:**
- A. Record what arrived (today); keep the invoice outside the system and ask the supplier for a credit note for the rest.
- B. Record the whole invoice on 401 (90,000 DA) and the missing 5 bags as an amount the supplier owes us (a claim account you name) until the goods or the credit note arrive.
- C. Record the whole invoice and treat the missing part as a loss.

**Our suggestion:** B if the receiving note also carries the invoice (PUR-01 C); otherwise A, with a note on the receiving note saying "5 short, credit note asked".

**Priority:** Important

**Accountant's answer:**

> 

---

### PUR-05 — The supplier changes the price after the goods are in stock

**Question:** When a supplier corrects the price after delivery (a credit note for a lower price, or an extra invoice for a higher one), how should the correction reach the supplier's balance, the stock value and the cost of the goods we already sold?

**What ParadisERP does today:** Not built. There is no supplier credit note or supplier debit note. The tools that exist all do something else: an opening-balance correction (meant only for the go-live balance, it posts against 890), reversing the whole receiving entry in the ledger (which fixes 401 in the books but not the stock quantity or the supplier's balance on their card — see findings), or typing a new cost on the product (which changes only the cost of future sales and posts nothing).

**Why I'm asking:** Price corrections and year-end rebates are normal with suppliers. Without a way to record them, the supplier's balance will not match their statement and the stock cost will be wrong.

**Example:** We received 200 props at 2,000 DA = 400,000 DA. Two weeks later the supplier sends a credit note: the agreed price was 1,900 DA, so 20,000 DA less. In the meantime 120 props were sold with a cost of 2,000 DA each.

**Possible answers:**
- A. A supplier credit note document: 401 down 20,000 DA, stock 310 down 20,000 DA.
- B. Split it: the part still in stock (80 × 100 = 8,000 DA) lowers stock; the part already sold (120 × 100 = 12,000 DA) lowers cost of sales (600) or goes to a "discounts and rebates received" account you name.
- C. The whole 20,000 DA to a "discounts received" account; stock unchanged.

**Our suggestion:** B once stock is valued at a weighted average (C-01); A is acceptable before that. Your choice of account for the sold part. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### PUR-06 — Returning goods to a supplier

**Question:** How should goods we send back to a supplier be recorded?

**What ParadisERP does today:** Not built. The only way to take goods off stock by hand is a stock adjustment, which posts account 657 (stock losses) against 310 and does not touch the supplier. A return entered that way shows as a loss, and the supplier still appears to be owed the full amount.

**Why I'm asking:** Defective goods go back regularly. Recorded as a loss, profit is understated and the supplier's balance is overstated.

**Example:** 10 defective panels bought at 3,500 DA are sent back. We should owe 35,000 DA less (plus 6,650 DA less TVA if purchase tax is recorded, TAX-05). Today the only option makes a 35,000 DA loss and leaves the 35,000 DA owed.

**Possible answers:**
- A. A supplier return document: 401 down for that supplier, stock 310 down, valued at the price on the original receiving note; the purchase tax reversed if it was recorded.
- B. Recorded by the accountant outside the system.

**Our suggestion:** A, valued at the price on the original receiving note (not today's cost).

**Priority:** Important

**Accountant's answer:**

> 

---

### PUR-07 — Receiving goods without naming a supplier

**Question:** Should every receiving note have to name a supplier, with one "various suppliers" or "cash purchases" supplier for one-off shops?

**What ParadisERP does today:** The supplier is optional. Without one, the note still adds the amount to 401 (what we owe suppliers), but owed to nobody. A supplier payment voucher must name a supplier, so nothing can ever pay that amount off; it stays in 401 for good.

**Why I'm asking:** 401 grows with debts that belong to no one, and if the branch also pays the shop through an expense voucher, the same goods are counted twice (stock and expense).

**Example:** A branch buys 15,000 DA of clamps at a hardware shop, receives them without picking a supplier, and pays cash with an expense voucher "purchase". The books show 15,000 DA in stock, 15,000 DA owed to nobody on 401, and 15,000 DA of expense: 30,000 DA of movement for a 15,000 DA purchase.

**Possible answers:**
- A. A supplier is required; create one "various suppliers" supplier for occasional shops, then pay it with a supplier payment voucher.
- B. Optional, but a note with no supplier is a cash purchase: stock 310 against the cash box (530) directly.

**Our suggestion:** A. It keeps every purchase on a supplier statement and has one way to pay.

**Priority:** Important

**Accountant's answer:**

> 

---

### PUR-08 — Paying a supplier before delivery (advances)

**Question:** When we pay a supplier before the goods arrive, should the advance stay on 401 (the supplier's balance goes below zero) or go to a separate account for advances to suppliers?

**What ParadisERP does today:** A supplier payment voucher may pay more than we owe. It lowers 401 for that supplier, whose balance then goes negative (the supplier owes us). There is no separate advances account.

**Why I'm asking:** On the balance sheet, 401 then shows what we owe suppliers minus what suppliers owe us, as one net figure.

**Example:** We pay 500,000 DA in advance on an order of scaffolding. Until delivery the supplier's card shows −500,000 DA, and total 401 is 500,000 DA lower than what we really owe the other suppliers.

**Possible answers:**
- A. Keep it on 401 (today).
- B. Post advances to a separate advances-to-suppliers account and move them to 401 at delivery.
- C. Keep it on 401 day to day; at year end the accountant moves the suppliers with a negative balance to the advances account.

**Our suggestion:** C: no change to daily work, and a correct year-end balance sheet. A list of suppliers with a negative balance would be added to help. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### PUR-09 — Transport, customs and other costs of buying (landed cost)

**Question:** Should transport, customs duties and handling paid to bring goods in be added to the stock cost of those goods, or recorded as expenses of the month?

**What ParadisERP does today:** Not built as part of stock cost. Such costs can only be paid with an expense voucher (transport goes to 624, purchase to 6068, other to 658). The voucher is not linked to a receiving note and does not change the product's cost.

**Why I'm asking:** If these costs are large, leaving them out makes the stock and the margin on each product look better than they are; adding them needs a rule for how to spread them.

**Example:** An import of 1,000 couplers at 1,000 DA (1,000,000 DA), customs 150,000 DA, transport from the port 50,000 DA. Stock cost per coupler: 1,000 DA today, 1,200 DA if the costs are included.

**Possible answers:**
- A. Include them in stock cost, spread by value (or by quantity).
- B. Expenses of the month, as today.
- C. Include customs and import costs only; ordinary local delivery stays an expense.

**Our suggestion:** First, do we import at all, and how big are these costs? If small: B. If imports are regular: A, spread by value. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### PUR-10 — Goods bought for cash through an expense voucher

**Question:** When a branch buys goods to sell or rent and pays cash, must they always come in through a receiving note (stock) and never through an expense voucher "purchase" (an expense, not stock)? Where is the line?

**What ParadisERP does today:** An expense voucher with the category "purchase" posts account 6068 (purchases not stocked) against the cash box and moves no stock. A receiving note posts 310 against 401 and adds the quantity. Nothing stops the same goods from going through both.

**Why I'm asking:** Goods paid by expense voucher are not in stock, so the shop sells goods the system says it never had, and the expense is counted at purchase while a cost of sale is also posted at sale.

**Example:** A branch buys 30 planks at 2,800 DA (84,000 DA) and pays with an expense voucher "purchase". 84,000 DA goes to expense. When the 30 planks are sold, stock goes to −30 and another 84,000 DA of cost of sales is posted.

**Possible answers:**
- A. Goods for sale or rent always come in by receiving note and are paid by a supplier payment voucher; "purchase" expense vouchers only for supplies the company uses itself (cleaning, office, small tools).
- B. A value limit below which an expense voucher is allowed.

**Our suggestion:** A, with the voucher screen saying so in words.

**Priority:** Important

**Accountant's answer:**

> 

---

### PUR-12 — Discounts from suppliers

**Question:** How should we record a discount printed on the supplier's invoice, and a discount the supplier gives for paying early or in cash?

**What ParadisERP does today:** Not built. The receiving note takes one unit price. A payment voucher pays one amount. If the supplier accepts less than we owe, the difference stays on the supplier's balance with no proper way to clear it.

**Why I'm asking:** An unexplained balance on a supplier we no longer owe makes the statement wrong and 401 overstated.

**Example:** We owe 200,000 DA; the supplier accepts 196,000 DA if paid in cash today. We pay 196,000 DA; 4,000 DA stays on their balance for ever.

**Possible answers:**
- A. A discount on the invoice: type the net price (it lowers the stock cost). An early-payment discount: an option on the supplier payment voucher that clears the rest to an income account you name.
- B. Both to an income account you name.

**Our suggestion:** A. **(To be confirmed by the accountant.)**

**Priority:** Later

**Accountant's answer:**

> 

---

### PUR-14 — Correcting a receiving note that was entered wrong

**Question:** When a receiving note was entered wrong (wrong quantity, price or supplier), how should it be corrected so that the books, the stock and the supplier's balance all follow?

**What ParadisERP does today:** There is no "cancel" or "correct" for a receiving note. The accounting screen lets an authorised user reverse any ledger entry with a reason, including a receiving entry, but that changes only the ledger: the stock quantity stays, the product's cost stays as the wrong note set it, and the balance on the supplier's card stays (the supplier statement then shows a difference between the card and the books).

**Why I'm asking:** Typing mistakes happen every week. The only correction available today leaves stock and the supplier's balance wrong.

**Example:** The clerk types 1,000 couplers instead of 100 at 1,250 DA: 1,250,000 DA instead of 125,000 DA. Reversing the entry removes 1,250,000 DA from 310 and 401, but the 1,000 units stay in stock and the supplier card still says we owe 1,250,000 DA.

**Possible answers:**
- A. A "cancel receiving note" that undoes everything together (ledger, stock, supplier balance) with a reason; then a new, correct note.
- B. A correcting note for the difference only (−900 units).

**Our suggestion:** A: one clear rule, both papers kept, nothing deleted. Until it exists, receiving entries should not be reversible from the accounting screen.

**Priority:** Important

**Accountant's answer:**

> 

---

## 3. Stock and Warehouses

### C-01 — How should stock be valued? (D9)

**Status:** Pending Owner — the owner is keeping this decision open and would like your view on the options before deciding.

**Question:** When we buy the same product at different prices, how should the system calculate its stock value and the cost of each sale?

**What ParadisERP does today:** One cost per product for the whole company. It is replaced by the price on each receiving note, at any branch, or typed by hand on the product: in effect the last purchase price. Every sale posts cost of sales, account 600, against stock, 310, at that cost when the sale reaches the server. There is no average, no FIFO and no cost layers; stock movements carry no unit cost, and there is no valued stock list (the stock report shows quantities only).

**Why I'm asking:** When the price changes, the goods already in stock are costed at the new price when sold, while 310 still holds what they really cost. 310 slowly drifts away from "quantity × cost" and nothing explains the difference.

**Example:** We buy 100 props at 2,000 DA (310 = 200,000 DA), then 100 at 2,400 DA (310 = 440,000 DA). The product's cost is now 2,400 DA. We sell all 200: cost posted 480,000 DA, so 310 ends at −40,000 DA with no props left. With a weighted average (2,200 DA) the cost posted is 440,000 DA and 310 ends at 0.

**Possible answers:**
- A. Last purchase price (today).
- B. Weighted average cost (CMUP), updated at each receipt ("perpetual"), one average per product for the whole company.
- C. Weighted average per branch.
- D. FIFO (first in, first out).

**Our suggestion:** B, perpetual and company-wide, as in the costing sign-off sheet. Transfers between branches then move goods at that average and change it for no one. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### D43 — Switching to the new method: which date, and what starting value?

**Question:** On which date should the system switch to the method you choose in C-01, and what value should each product start with?

**What ParadisERP does today:** Not built. The costing plan foresees a cut-off date, a starting value per product, and from then on purchases entering stock at their price before tax (PUR-02). Until the switch, every material figure in manufacturing is labelled a "reference cost". Nothing changes until you sign.

**Why I'm asking:** The switch is the one moment the stock value is reset. A switch in the middle of a year with no count behind it would carry today's drift (C-01) into the new method.

**Example:** We switch on 1 January 2027. A full count finds 2,400 couplers; the last purchase price before tax is 1,250 DA, so couplers start at 3,000,000 DA. The same for every product; the total is compared with 310 and 318 (C-02).

**Possible answers:**
- A. Switch at the start of a financial year, right after the year-end physical count; each product starts at counted quantity × last purchase price before tax.
- B. Same, but each product's starting value comes from your year-end valued stock list.
- C. Switch at the start of the next month, without waiting for the year end.

**Our suggestion:** A, or B if you already prepare a valued year-end stock list. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### C-02 — At the switch, where does the difference go? (D43)

**Question:** At the switch, the new starting value of stock will differ from what accounts 310 (goods in stock) and 318 (scrap stock) show. Which account takes the difference?

**What ParadisERP does today:** Not built. Today 310 is the sum of everything posted to it at the costs of the moment, so it will not equal "counted quantity × starting cost".

**Why I'm asking:** The difference must go somewhere once, in one entry, and you decide whether it is this year's result or a correction of earlier years.

**Example:** 310 shows 12,450,000 DA. Counted quantities × starting costs = 11,980,000 DA. The difference of 470,000 DA has to be posted.

**Possible answers:**
- A. To this period's result (a stock variation or stock-loss account you name).
- B. To equity, as a correction of earlier years, if the amount is large.

**Our suggestion:** One entry, the figure shown to you before it is posted, to the account you name. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> Account:

---

### C-16 — The gap when goods sold below zero are later bought in (D43)

**Question:** When a product is sold while its stock is below zero and the goods arrive later at a different cost, which account takes the difference?

**What ParadisERP does today:** A till sale is never blocked when stock is short (the owner's rule "never block the sale"); it is recorded with a warning and the stock goes below zero. Its cost is the product's cost at that moment. When the goods arrive later at another price, nothing is adjusted. Under a weighted average (C-01) this difference becomes visible and needs an account.

**Why I'm asking:** Every negative-stock sale leaves a small difference between the cost posted and the cost of the goods that later covered it.

**Example:** A branch sells 10 couplers with none in stock. Cost posted: 10 × 1,200 DA = 12,000 DA. The delivery that covers them costs 1,300 DA each: 13,000 DA. Gap: 1,000 DA.

**Possible answers:**
- A. To cost of sales (600), so the gap lands in the margin of the period.
- B. To a stock valuation-difference account you name, reported per product.

**Our suggestion:** Your account. If you have no preference, A: the gap is part of the cost of those sales. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> Account:

---

### P0.3 — Invoices typed in the back office move no stock and post no cost

**Question:** An invoice for goods raised directly in the back office (not from a till sale) records the sale but takes nothing out of stock and posts no cost. Should it take the goods out of stock and post cost of sales (600 against 310) at the method in force (today the last purchase price, later C-01)?

**What ParadisERP does today:** A direct invoice posts what the customer owes (411) and the sale (700, with its tax) only. It moves no stock and posts no cost. A till sale, by contrast, does both.

**Why I'm asking:** Every goods invoice typed in the back office shows full revenue with zero cost, so margin is overstated and the goods stay in stock on the screen and in 310.

**Example:** A direct invoice for 50 props at 3,000 DA = 150,000 DA. Their cost (2,000 DA each, 100,000 DA) is never posted, so the month's margin looks 100,000 DA better than it is, and 50 props that have left remain in stock.

**Possible answers:**
- A. Yes: a direct goods invoice takes the goods out of stock and posts 600 / 310 at today's method, and at C-01's method after the switch.
- B. No: goods must always leave through a till sale or a delivery note; a direct invoice is for services only.

**Our suggestion:** A. (Refunds reversing the cost of the original sale are P0.4c, in the sales section.)

**Priority:** Critical

**Accountant's answer:**

> 

---

### STK-01 — Negative stock: what is allowed?

**Question:** Should a branch be allowed to sell, transfer or adjust more than it has in stock, and if so, what must happen afterwards?

**What ParadisERP does today:** A till sale is never blocked; it is recorded with the warning "Stock went negative". Each branch has an "allow negative stock" setting, but it is only checked for transfers; a manual stock adjustment ignores it (see findings). The main warehouse is created with negative stock allowed.

**Why I'm asking:** Negative stock means the books show goods leaving that never entered: the cost posted is a guess, and real count differences hide behind it.

**Example:** The Oran branch shows −35 planks. Either a receiving note or a transfer was never typed, or 35 planks were sold that the company never had. Until it is found, 310 at Oran is wrong by 35 × 2,800 DA = 98,000 DA.

**Possible answers:**
- A. Never block a sale (the owner's rule); block transfers and manual adjustments below zero everywhere; a weekly list of negative products that each branch must explain.
- B. Allow everything, as today.
- C. Block sales too.

**Our suggestion:** A. Blocking sales (C) goes against the owner's rule "never block the sale".

**Priority:** Important

**Accountant's answer:**

> 

---

### STK-02 — Stock count differences: which accounts?

*This question also answers: CLO-09 (Year-end count: sheet, date, valuation, gain/loss accounts?).*

**Question:** When a count finds fewer or more items than the system shows, which accounts should the loss and the gain go to?

**What ParadisERP does today:** A stock adjustment is one product at a time, with a reason. It is valued at the product's current cost: missing goods post 657 (stock losses) against 310; extra goods post 310 against 657, so a gain lowers the losses account instead of showing as a gain. When the product's cost is 0, nothing is posted.

**Why I'm asking:** Losses and gains netted in one account hide both, and the tax treatment of stock losses may need them shown separately.

**Example:** A count finds 12 clamps missing at 450 DA (5,400 DA loss) and 3 planks extra at 2,800 DA (8,400 DA gain). Account 657 shows a credit of 3,000 DA, an "expense" account with a negative balance.

**Possible answers:**
- A. Both to 657, netted (today).
- B. Losses to 657, gains to an income account you name.
- C. Both through a stock variation account you name.

**Our suggestion:** B. Please also tell us whether losses need any supporting paper to be accepted by the tax office (a signed count sheet, a report of the loss). **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### STK-03 — The year-end count and the valued stock list

**Question:** How must the physical count be done and documented (how often, who counts, who signs, on which date stock is frozen), and must the system print a valued stock list (quantity × cost per product and per location) for the year-end accounts?

**What ParadisERP does today:** Not built as a count document: there is no count session, no frozen count sheet, and no valued stock list. The Item Movement & Status report shows quantities per location (available, on rent, maintenance, scrap, in transit), not values. Differences are typed one product at a time as adjustments.

**Why I'm asking:** The year-end stock figure on the balance sheet must rest on a count and a valued list. Today neither can be produced from the system.

**Example:** On 31 December each of 4 locations counts about 300 products. Today that is up to 1,200 separate adjustments, with nothing printed that shows "counted vs system, × cost = value".

**Possible answers:**
- A. A count document per location: print the sheet, type the counted figures, show the differences, approve once (posting STK-02); plus a valued stock list at any date.
- B. Count on paper; only the differences are typed (today).

**Our suggestion:** A, at least once a year at year end, and a valued stock list from now on. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### STK-04 — Scrap (account 318): at what value?

**Question:** When goods are moved to scrap (damaged beyond use, to be sold as scrap), should they stay at their full purchase cost in 318 (scrap stock), or be written down to what they will fetch as scrap? And how should a scrap sale be recorded?

**What ParadisERP does today:** Moving goods to scrap posts 318 against 310 at the product's full current cost. Writing scrap off posts 657 against 318. A scrap sale is not built: account 756 (scrap sales) exists in the chart, but nothing posts to it and nothing takes sold scrap out of 318. (Scrap from manufacturing is C-09, in the manufacturing section.)

**Why I'm asking:** Damaged goods kept at full cost overstate stock, and scrap that is sold but never leaves 318 stays on the balance sheet.

**Example:** 20 damaged panels that cost 3,500 DA each go to scrap: 70,000 DA in 318. As metal they are worth about 300 DA each, 6,000 DA. Sold for 6,000 DA, the 70,000 DA stays in 318.

**Possible answers:**
- A. Full cost in 318 (today); written down at year end.
- B. At the move, 318 takes the recovery value (6,000 DA) and the rest (64,000 DA) goes to 657.
- C. Scrap sale: cash against 756 for the price, and the scrap's value out of 318 against a charge account you name.

**Our suggestion:** B for the move and C for the sale. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### STK-05 — Opening stock on the go-live day (typed or from Excel)

**Question:** At what value should the stock already on hand on the go-live day be entered, and must the total equal the stock on the last closing balance sheet?

**What ParadisERP does today:** Opening stock is accepted only before a branch is declared open. It posts 310 against 890 (opening balance), never as a purchase. Typed lines may carry a unit value; otherwise the product's cost is used. The Excel import reads names and quantities only, so each line is valued at the product's current cost; a product the import creates has a cost of 0, so its opening value is 0 and nothing is posted for it. A typed opening unit value does not become the product's cost, so later sales may cost those goods at a different figure.

**Why I'm asking:** If the opening value is 0 or differs from the cost used at sale, 310 is wrong from day one and goes below zero as the goods are sold.

**Example:** The Excel sheet brings in 300 planks as a new product with cost 0: opening value 0 DA. Later the cost is typed as 2,800 DA. When the 300 planks are sold, 840,000 DA of cost leaves a 310 that never received it.

**Possible answers:**
- A. Each product's cost (before tax) is entered before the opening is posted, and the opening uses that same cost.
- B. The opening values come from your last valued stock list; the products' costs are set from it.
- C. As today.

**Our suggestion:** B if the company already had a closing balance sheet with stock, otherwise A. In both cases the opening total of 310 is checked against your figure, and any branch already opened at 0 is corrected once with your agreement. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### STK-06 — Changing a product's cost by hand

**Question:** Who may change a product's cost by hand, and should such a change revalue the stock in the books?

**What ParadisERP does today:** Anyone allowed to edit products can type a new cost. It posts nothing; every later sale, transfer, adjustment and scrap move uses the new figure. A receiving note may also set the cost to 0, after which sales post no cost at all.

**Why I'm asking:** A typed cost changes the cost of sales from that moment, with no paper and no entry, and 310 drifts further from reality (C-01).

**Example:** A product with 400 units in stock at 1,000 DA (400,000 DA in 310) is edited to 1,500 DA. Selling the 400 posts 600,000 DA of cost; 310 ends at −200,000 DA.

**Possible answers:**
- A. Under the average (C-01) the cost is computed, never typed; a correction is a revaluation entry you approve.
- B. Until then, only an administrator may change it, with a reason kept in a history.
- C. As today.

**Our suggestion:** B now, A after the switch. A cost of 0 should be refused on a receiving note.

**Priority:** Important

**Accountant's answer:**

> 

---

## 4. Rentals and Deposits

### REN-01 — Which VAT rate applies to our equipment hire?

*This question also answers: TAX-11 (Is hire taxable, and at what rate for rent, overdue, repairs, damage?).*

**Status:** Owner Workflow Confirmed — Compliance Pending (rate to confirm).

**Owner's decision (31 Oct 2026):** Equipment hire is subject to VAT.

**Question:** Which VAT rate applies to our hire (scaffolding, formwork, props), and does the same rate apply to the extra charges on a contract: late days, repairs we charge back, and lost or wrecked items?

**What ParadisERP does today:** Hire has its own VAT rate setting, separate from sales. The rate in force is copied onto each contract line and applies to everything that line charges. The live rate will be checked on our side.

**Example:** 100 scaffold frames × 30 days × 50 DA/day = 150,000 DA rent. At 19 %, the customer pays 178,500 DA, of which 28,500 DA is VAT.

**Possible answers:**
- A. The standard rate on everything (rent, late days, repairs, lost items).
- B. The standard rate on rent and late days; lost-item compensation is outside VAT (see REN-08).
- C. Another rate (please name it).

**Our suggestion:** A, unless lost-item compensation is treated differently. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### REN-02 — Rent booked on the first day: is an adjustment needed at month end or year end?

*This question also answers: CLO-10 (Hire across 31 Dec: defer next year's days?).*

**Status:** Owner Workflow Confirmed — Compliance Pending (period adjustment to confirm).

**Owner's decision (31 Oct 2026):** The full rent for the agreed period is booked as income on the first day of the rental period.

**Question:** With that rule, do you need us to adjust contracts that run across a month end or the year end, so that part of the rent is moved to the next period?

**Why I'm asking:** A contract that starts in December and runs into February puts all three months of rent into the old year.

**Example:** Contract starting 1 December: 200 frames × 90 days × 50 DA = 900,000 DA, all booked in December. Spread by days, December would get 310,000 DA and 590,000 DA would belong to January–February.

**Possible answers:**
- A. No adjustment needed.
- B. An adjustment only at year end (the part for next year goes to "income received in advance"; please name the account).
- C. An adjustment at every month end.

**Our suggestion:** B, calculated by the system from open contracts. Day-to-day work stays as the owner decided. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### REN-03 — When is the VAT on hire due?

*This question also answers: TAX-12 (Rental VAT due at invoicing (issue) or at payment?).*

**Status:** Owner Workflow Confirmed — Compliance Pending.

**Owner (10 Nov 2026):** hire VAT is due **when invoiced**, not at payment. A hire contract is invoiced when the goods go out (the issue note is the invoice, dated the period's first day, REN-02), and its VAT already posts there, so nothing changes in the program. Part payments carry no VAT of their own.

**Asked together with D45** in the tax section ("D45 + REN-03 — When exactly is VAT due on advances and on hire payments?"), so sales, advances and hire follow one rule.

---

### REN-04 — Can rent be booked without an invoice?

**Question:** The system books rent when the goods leave, and the invoice is a separate, optional step. Must every issue note (and every return note that charges something) have an invoice, and by when?

**What ParadisERP does today:** Issue and return notes post the income and the customer's debt. An invoice can then be made "from the note" at any later time; it copies the amounts and posts nothing (to avoid counting income twice). Nothing forces an invoice to be made, and nothing reports notes that were never invoiced: Needs verification for a report.

**Why I'm asking:** Our books can show income and TVA with no invoice behind them, or an invoice dated weeks after the income. If the law requires an invoice for every hire within a time limit, the system should remind us or make it automatic.

**Example:** Issue note ISS-000120 on 5 April books 150,000 DA. The invoice is made on 30 May. Which date does the tax office look at, and is 55 days acceptable?

**Possible answers:**
- A. Every issue note must be invoiced; make the invoice automatically with the note.
- B. Invoices may be grouped (for example one per customer per month); add a report of notes not yet invoiced.
- C. An invoice only when the customer asks for one is acceptable.

**Our suggestion:** B: a monthly invoice per customer from the notes, plus a list of notes not yet invoiced, so nothing is forgotten. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### REN-05 — Early returns and "same day, not used": how do we give back rent already booked?

**Question:** When the customer returns goods early (or the same day, unused), is it correct to reduce income and the customer's debt on the return date, with a credit note against the original invoice — and, when hire is untaxed, with a document marked "internal adjustment" instead?

**What ParadisERP does today:**
- The unused days are taken off rental income (706) and off the customer's account on the return date, with their TVA if any.
- If an invoice already exists for the issue, a credit note (CRN-…) is made automatically for exactly those days, keeping the invoice's tax rate.
- If the invoice was untaxed, the credit carries no TVA and is labelled "internal adjustment", and it is left out of the tax report (owner, 2026-10-14).
- Same-day returns: the clerk must say "used" (charged 1 day) or "not used" (charged 0 days), owner decision 2026-10-14.
- If the customer has already paid, the extra money is not paid out automatically: a refund voucher has to be raised, approved and paid separately.

**Why I'm asking:** We want to be sure the credit note is a valid document to reduce income and TVA, especially when the original invoice is in an earlier month or an earlier year.

**Example:** 100 frames booked for 30 days = 150,000 DA (+28,500 DA TVA at 19 %). They come back after 10 days. 20 days × 100 × 50 = 100,000 DA (+19,000 DA TVA) is given back on a credit note. If the customer paid 178,500 DA, we owe him 119,000 DA; if he paid nothing, he now owes 59,500 DA.

**Possible answers:**
- A. Correct as it is.
- B. Correct, but a credit note on an invoice from a previous year needs different handling (please explain).
- C. The "internal adjustment" for untaxed hire must be a normal credit note too.

**Our suggestion:** A, and please confirm the "internal adjustment" wording is acceptable for untaxed hire. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### REN-06 — Late-return charges: extra rent, or a penalty?

**Question:** When goods come back after the agreed period, is the extra charge more rent (with TVA) or a late penalty (possibly without TVA), and which account should it go to?

**What ParadisERP does today:** Late days are charged at the "overdue" daily price on the contract (a multiplier on the rent, 1.5 by default). They go to their own income account, **708 "late-return income"**, because the owner asked to see them separately. They carry the same tax rate as the rent on that line.

**Why I'm asking:** If late days are a penalty in the legal sense, the tax and the account may differ from ordinary rent. We also want to know whether 708 is a suitable account for it in the national chart of accounts (SCF).

**Example:** 50 frames come back 6 days late. Overdue price 75 DA/day (1.5 × 50). Charge: 50 × 6 × 75 = 22,500 DA, today posted to 708 and taxed like the rent.

**Possible answers:**
- A. It is additional rent: same TVA as rent; account 706 or 708 is fine.
- B. It is a penalty: no TVA; another account (please name it).
- C. Other (please explain).

**Our suggestion:** A, keeping 708 so the owner sees late income apart, if 708 is acceptable to you. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### REN-07 — Late days we decide not to charge

**Question:** When a branch manager waives some or all late days, do we need to show the waived amount anywhere (as a discount on the invoice, or in the books), or is it enough that it is simply not charged?

**What ParadisERP does today:** The manager can waive all late days, or deduct some days or an amount (owner, 2026-10-08 and 2026-10-22). A written reason and his name are kept on the return note. The waived amount is **not** booked anywhere: only the reduced charge is posted.

**Why I'm asking:** Nothing in the books shows how much late income was given up. That may be fine, but if the tax office expects a discount line on the invoice, we need to know.

**Example:** Late days worth 22,500 DA; the manager deducts 10,000 DA. 12,500 DA is posted to 708. The 10,000 DA appears only on the return note with the reason.

**Possible answers:**
- A. Not charging is enough; no entry needed.
- B. Show it on the invoice as a discount, but no separate entry.
- C. Book it as a discount granted (please name the account).

**Our suggestion:** A, since the charge never existed until the return; we can add a report of waivers for the owner. **(To be confirmed by the accountant.)**

**Priority:** Later

**Accountant's answer:**

> 

---

### REN-08 — Customer loses or wrecks an item: is our compensation charge taxed, and where does it go?

**Question:** When a customer pays us for items lost or destroyed (at the damage price on the contract), is that payment subject to TVA, and is account 757 the right one?

**What ParadisERP does today:** Lost units and units "beyond repair" are charged at the damage price frozen on the contract, to **757 "damage and loss compensation"**. The same tax rate as the rent on that line is added. The customer also keeps paying rent for those units until the day the loss is reported (owner, 2026-09-23).

**Why I'm asking:** Compensation for a loss may be treated differently for TVA from a sale of the item. If our treatment is wrong, either we over-charge the customer TVA or under-declare it.

**Example:** 12 frames lost, damage price 9,000 DA each = 108,000 DA. At a 19 % line rate the customer is charged 128,520 DA, of which 20,520 DA TVA.

**Possible answers:**
- A. Taxed like the rent; 757 is fine.
- B. Not subject to TVA (it is compensation); keep 757.
- C. Treat it as a sale of the item (sales account, sales TVA).

**Our suggestion:** Please choose; whichever you choose we will make it a setting per charge type (rent, late, repair, loss), not one rate for everything. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### REN-09 — Items that never come back: at what value do we remove them from stock?

**Question:** When a rented unit is lost, which value should leave the books: what we paid for it, or a "book value" that is lower because it is worn?

**What ParadisERP does today:** The lost unit is removed from stock (310) into stock losses (**657**) at the product's **catalogue cost price**. Each product also has a separate "asset value" field that the design calls the book value, but no accounting entry uses it. If a product's cost price is 0, nothing is posted at all.

**Why I'm asking:** The loss and the stock value depend on which figure is used. A scaffold bought for 6,500 DA five years ago is probably not worth 6,500 DA today.

**Example:** 12 frames lost. Cost price 6,500 DA → loss 78,000 DA (657). Asset value 4,000 DA → loss 48,000 DA. The customer pays 108,000 DA compensation (757) either way.

**Possible answers:**
- A. Purchase cost (as today).
- B. Book value after wear (needs REN-12 to be settled: depreciation or a value we maintain).
- C. Other (please explain).

**Our suggestion:** Decide together with REN-12. If the equipment stays in stock with no depreciation, A is consistent. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### REN-10 — Wrecked items kept as scrap: at full cost?

**Question:** When a unit comes back beyond repair and we keep the wreck as scrap, should the scrap be written down to what it can be sold for?

**What ParadisERP does today:** The wreck moves from merchandise stock (310) to scrap stock (**318**) at its **full catalogue cost price**. Nothing reduces it. The customer is also charged the full damage price (757). Selling scrap (account 756 is seeded) has no document yet: Not built.

**Why I'm asking:** We keep the full value of the wreck **and** book the customer's compensation, so the profit looks better than it is until the scrap is sold or thrown away.

**Example:** 5 frames wrecked. Cost 6,500 DA → 32,500 DA moved into scrap (318). As scrap metal they might fetch 800 DA each = 4,000 DA. The customer pays 45,000 DA (5 × 9,000 DA).

**Possible answers:**
- A. Write the scrap down to its expected sale value at once (difference to a loss account — please name it).
- B. Keep it at cost until it is sold, then book the difference.
- C. Write it off completely (no scrap value).

**Our suggestion:** A, with an expected scrap value per product that we enter, and the difference to stock losses (657) unless you prefer another account. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### REN-11 — Customer damages an item that we can repair

**Question:** When an item comes back damaged but repairable and we charge the customer the repair, is it correct to book that charge as income (707) with TVA, while our real repair cost is booked separately as an expense (615)?

**What ParadisERP does today:** The return note records how many units are repairable and one repair amount typed by the clerk. It goes to **707 "maintenance charged to customer"**, taxed at the line's rate. The units move to "in maintenance" (still in stock 310, no entry). What the repair really costs us is only recorded if someone raises an expense voucher of category "repair" (615); it can name the contract but not the item, and nothing compares it with what we charged.

**Why I'm asking:** We want to confirm the two-sided treatment (income 707 vs expense 615), and whether recharging a repair is a taxed service.

**Example:** 8 frames come back bent. We charge the customer 16,000 DA (2,000 DA each) → 707, plus TVA at the line rate. A welder repairs them for 11,000 DA, paid in cash → 615. Profit on the repair: 5,000 DA.

**Possible answers:**
- A. Correct as it is.
- B. The recharge should reduce our repair expense (615) instead of being income.
- C. The recharge is compensation without TVA (as in REN-08 B).

**Our suggestion:** A. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### REN-12 — Rental equipment as fixed assets: how to depreciate it?

*This question also answers: STK-07 (Rental equipment kept in 310 without depreciation); GL-04 (Hire equipment as stock (310, no depreciation) or fixed assets?).*

**Status:** Approved by Owner (classification) — depreciation policy needs your answer. Built 2026-11-09 as settings you set in Reports › Fixed assets (off until you switch it on; see [fixed-assets.md](fixed-assets.md)).

**Owner's decision (31 Oct 2026):** The equipment we hire out is fixed assets, not merchandise stock.

**Question:** How should we depreciate it, and how do we move it out of stock?
1. Which account(s) and which depreciation method and useful life per type (frames, props, formwork …)?
2. At what value and on what date do we move the equipment now held in stock into fixed assets?
3. What do we record when an item is lost, wrecked or scrapped, or when a hire item is sold?

**What ParadisERP does today:** Rental items sit in merchandise stock (310) with no depreciation. The register is built and waits for your choices: the treatment, straight line (suggested), the useful life per type and a default, the residual value, the value the equipment opens at, the switch-over month and the accounts (none is guessed). Lost and written-off units then leave at net book value to the disposal account, wrecks go to scrap at net book value, and each ended month's depreciation is posted by you.

**Example:** We own 2,000 frames bought at 6,500 DA = 13,000,000 DA. Depreciated over 5 years on a straight line, the yearly charge is 2,600,000 DA and their book value after 3 years is 5,200,000 DA.

**Possible answers:** Please give the method, useful life per type, the accounts, and the value to use at the switch.

**Our suggestion:** Straight line, by item type, starting from the purchase cost known today. **(To be confirmed by the accountant.)**

**Built as a setting (10 Nov 2026, deployed `eea95fa`):** Reports › Fixed assets › Settings. Straight line stays the default. Declining balance (the tax code's "dégressif": the straight-line rate × a coefficient on the net book value at the start of each year, the first year prorated by its months) can be chosen for the company and for each type, with the coefficient suggested from the life (1.5 for 3–4 years, 2 for 5–6, 2.5 above 6) and changeable, and the switch to straight line when it becomes higher on by default. Example: 1,000,000 DA over 5 years at coefficient 2 (40 %) is charged 400,000, 240,000, 144,000, then 108,000 and 108,000 on the straight line from year 4. A type's method is fixed once depreciation is posted on it. Nothing to change in the program if you answer "declining" ([fixed-assets.md](fixed-assets.md), "Declining balance").

**Priority:** Critical

**Accountant's answer:**

> 

---

### REN-13 — If we take a deposit (caution), which account holds it?

**Question:** If the company starts taking a deposit from customers, should it be held in account 419 (customers' advances) or 165 (deposits and guarantees received)?

**What ParadisERP does today:** A deposit can be switched on in the settings (off by default). When on, the agreed amount is written on the contract, printed, and shown as "deposit to return" when the contract closes. It is **never posted**, never counted in the customer's balance, never set against what he owes, and never blocks closing. Account 419 is seeded but nothing posts to it. The owner said the company takes **no deposit today**; the contract states the value of the goods instead (owner answer 14). We asked this before and have no answer yet.

**Why I'm asking:** The account cannot be changed once something has been posted to it, so it must be chosen before deposits are collected through the system.

**Example:** Contract with a 50,000 DA deposit paid in cash: Debit cash 50,000 / Credit 419 (or 165) 50,000. At the end it is paid back: the reverse entry.

**Possible answers:**
- A. 419.
- B. 165.
- C. Another account (please name it).

**Our suggestion:** B (165) if the deposit is a guarantee returned at the end; 419 if it is really an advance on rent. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### REN-14 — Keeping part of a deposit for damage, and refunding the rest

**Question:** If we hold a deposit and the customer damages or loses goods, how should we record keeping part of the deposit and returning the rest, and does the part we keep carry TVA?

**What ParadisERP does today:** Not built. At close the system only states the full deposit as "to return". It cannot collect a deposit, set part of it against damage, or refund the rest.

**Why I'm asking:** This is the usual reason to take a deposit. Before we build it we need the entries and the tax treatment.

**Example:** Deposit 50,000 DA. At return: 12 frames lost → 108,000 DA compensation, plus late days 22,500 DA. The deposit covers 50,000 DA; the customer still owes 80,500 DA. Another case: damage 12,000 DA → we keep 12,000 DA and return 38,000 DA.

**Possible answers:**
- A. The charge is invoiced normally (with its TVA per REN-08); the deposit is then transferred against the customer's debt (Debit 165/419, Credit 411) and the rest refunded.
- B. The kept part is booked directly as income without an invoice.
- C. Other (please explain).

**Our suggestion:** A: every charge stays on an invoice, and the deposit is only a means of payment against it. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### REN-16 — Which branch earns the income when goods leave from another branch?

**Question:** When a contract belongs to branch A but some goods are issued from branch B's yard, which branch should show the rent, and which branch should show the later adjustments?

**What ParadisERP does today:** The rent for each issue note is booked at the branch that issued the goods. Return notes must be taken at the contract's branch (owner, 2026-09-30), so late days, repairs, compensation and the refund of unused days are booked there; goods issued from another branch are moved back with an internal transfer (account 181 between branches). A lost unit is written off at the branch that issued it.

**Why I'm asking:** One contract can show income in one branch and the refund of that income in another, which distorts each branch's profit (but not the company's).

**Example:** Contract at Oran; 100 frames issued from Algiers: 150,000 DA rent in Algiers. Early return at Oran: 100,000 DA given back in Oran. Algiers +150,000, Oran −100,000.

**Possible answers:**
- A. Acceptable; branch figures are only for management.
- B. Everything should be booked at the contract's branch.
- C. The refund should go back to the branch that booked the rent.

**Our suggestion:** B or C if the owner judges branches on their profit; otherwise A. This is mainly the owner's choice; please say if it matters for the official accounts. **(To be confirmed by the accountant.)**

**Priority:** Later

**Accountant's answer:**

> 

---

### REN-17 — What happens to signed contracts if the TVA rate changes?

**Question:** If the hire TVA rate changes, should running contracts keep the rate they were signed at, or use the new rate from the date of the change?

**What ParadisERP does today:** The rate is frozen on each contract line when it is signed, together with the prices, and every later charge on that line (rent, late, repair, loss) uses it.

**Why I'm asking:** Prices agreed with the customer should not change, but the tax rate is set by law, usually by the date of the service or invoice.

**Example:** A contract signed at 0 % in October; the company then sets 19 % in December (see REN-01). A return in January with 22,500 DA late charges would still carry 0 %.

**Possible answers:**
- A. Keep the signed rate (as today).
- B. Apply the rate in force on the date of each charge.
- C. Apply the new rate only to new issue notes.

**Our suggestion:** B for tax (the price before tax stays as signed). **(To be confirmed by the accountant.)**

**Priority:** Later

**Accountant's answer:**

> 

---

## 5. Manufacturing and Production Costs

### C-03 — Should production costs stay in their normal expense accounts, or flow through a "work in progress" account?

**Question:** When the factory makes goods, do we leave each cost where it is first recorded (materials used, wages, expenses) and only record finished goods coming into stock, or do we move every cost into a "work in progress" account as it happens?

**What ParadisERP does today:** Nothing posts from manufacturing. Wages already post when a payroll run is approved (631 wages, 635 employer social charges, 641 other employer charges), and paid expense vouchers post to their expense account. The plan proposes two ways to add manufacturing:
- **Model N (by nature):** costs stay where they are charged. Finished goods enter stock through account 72 (production stocked, i.e. "the value we produced this period") at their cost. Open orders are valued once a month and reversed on the 1st.
- **Model W (perpetual work in progress):** every cost is moved into a work-in-progress account (e.g. 331) as it happens, and leaves it when goods are released. It needs extra "labour absorbed" and "overhead absorbed" accounts.

**Why I'm asking:** This choice decides every manufacturing entry and which accounts are needed. Model W can count wages twice if one step is missed, because payroll already posts wages to 631.

**Example:** Karim and Samir's October wages (5,300 DA of their time on the nails) are already in 631 when payroll is approved. Under Model N, nothing more is posted for labour; when 100 kg is released at 22,070 DA we post Dr 35x (finished goods) / Cr 72 22,070 DA. Under Model W we would also post Dr 331 / Cr "labour absorbed" 5,300 DA, then Dr 35x / Cr 331 at release.

**Possible answers:**
- A. Model N: costs by nature, finished goods enter stock through 72, monthly work-in-progress entry.
- B. Model W: everything through a work-in-progress account, with absorbed-cost and variance accounts you name.

**Our suggestion:** A (Model N). It reuses the wage and expense entries the system already makes, needs fewer new accounts, and avoids double-counting wages. Please also confirm that this presentation (charges by nature, with production stocked and stock variation) is how you present the income statement under the SCF. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### C-05 — How should one hour of a worker's time be priced in a product's cost?

**Question:** To put labour into a product's cost, we divide what the worker cost the company in a month by the hours they were paid for. Is that the right way, and which hours should we divide by?

**What ParadisERP does today:** Labour cost is calculated, never typed. Once the month's payroll is approved, the hourly rate = (gross pay + employer social contribution + the worker's share of other employer charges) ÷ paid hours. Paid hours = days present × hours per day + overtime hours from the payslip. Paid leave and holidays are in the cost but not in the hours, so they are spread over the hours worked. Overtime premium is blended into one rate for all hours (bookings do not say which hours were overtime). Bonuses and allowances in the month's gross are included in that month's rate. Before payroll is approved, a "standard" rate from a normal full month is used and the cost is marked provisional. Hours booked on an order are multiplied by that rate; hours not booked on any order (cleaning, breakdowns, waiting) stay in the wage expense.

**Why I'm asking:** This rate sets the labour part of every product's cost and stock value. A one-off bonus or a month with a lot of leave changes the rate for that month only.

**Example:** Karim earns 70,000 DA gross in October and was paid for 22 days × 8 h = 176 h, plus 4 h overtime = 180 h. His rate is 70,000 ÷ 180 = 388.89 DA/h. He booked 8 h on the nails order, so the order carries 3,111 DA of his time. If he also received a 10,000 DA bonus that month, his rate becomes 444.44 DA/h and the nails cost 444 DA more.

**Possible answers:**
- A. One blended rate per worker per month (as built): all pay, including leave, overtime premium and bonuses, over paid hours.
- B. Same, but overtime premium charged only to the hours booked as overtime (needs bookings to mark overtime; not built).
- C. Same as A, but exclude one-off bonuses from the rate (they stay in wage expense).

**Our suggestion:** A for now. It uses payroll's own figures, so the labour in products never exceeds what the books record. Tell us if bonuses should be left out (C).

**Priority:** Critical

**Accountant's answer:**

> 

---

### C-07 — Should order expenses be counted before or after recoverable VAT (TVA)?

**Question:** When an outside expense is charged to a production order (for example a galvanising job), should the cost of the product include the TVA we can recover, or only the amount before TVA?

**What ParadisERP does today:** A person types the expense amount on the order and may link it to an expense payment voucher. The cost sheet counts the amount **as typed**. The payment voucher holds only one amount, with no TVA split, and a paid voucher posts that full amount to its expense account. Since step A0, the expenses linked to one voucher can never add up to more than the voucher's amount. The system cannot tell by itself whether a typed amount includes TVA.

**Why I'm asking:** If recoverable TVA is left in, stock value and product cost are overstated by 19 % on those expenses, and the TVA would be counted twice (in the product and as a deduction).

**Example:** Galvanising invoice: 12,000 DA before tax + 2,280 DA TVA (19 %) = 14,280 DA paid by voucher. Before tax, the nails cost 53,480 DA (272.86 DA/kg). With TVA left in, they cost 55,760 DA (284.49 DA/kg).

**Possible answers:**
- A. Before recoverable TVA: staff type the amount before TVA on the order; non-recoverable TVA stays in the cost.
- B. As recorded (whatever is typed, as today).
- C. Before TVA, and the payment voucher also gets a TVA field so the system can check it (a change to vouchers).

**Our suggestion:** A, with clear wording on the expense screen ("amount before TVA"), and C later if vouchers start carrying TVA. Please also tell us which expenses carry TVA we cannot recover. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### C-08 — Should factory overheads (electricity, rent, machine wear) be part of the product's cost?

**Question:** Must part of the factory's running costs be added to the cost of the goods we make and keep in stock, and if yes, at what rate and on what basis?

**What ParadisERP does today:** No overhead is added. The overhead line on every cost sheet reads "Not set", never 0, and keeps the cost provisional. There is no screen to enter a rate; the rate is fixed at zero in code until you approve one. These costs stay in their normal expense accounts.

**Why I'm asking:** If stock should carry its share of factory overheads, our stock value and the cost of goods sold are understated today. If you choose a rate, we need its basis (per labour hour is proposed), the amount and the date it starts; and what happens when the factory works below normal capacity (the unabsorbed part).

**Example:** Factory electricity, rent and machine wear come to 160,000 DA a month, for about 1,600 normal labour hours: 100 DA per labour hour. The nails order used 14 h, so it would carry 1,400 DA of overhead (53,480 DA instead of 52,080 DA). In a quiet month with only 800 hours worked, only 80,000 DA goes into products and the other 80,000 DA stays as an expense of the month.

**Possible answers:**
- A. No overhead in stock value for now (as today).
- B. A rate per labour hour, set from normal capacity, changed by dated versions; the unabsorbed part stays in expenses.
- C. A rate per machine hour (needs machine hours to be recorded; not built).

**Our suggestion:** Our understanding is that stock cost should normally include a fair share of production overheads based on normal capacity. If that applies to us, B, with one company-wide rate reviewed once a year; otherwise A. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### C-09 / AD-8 — What value do we give to rejected pieces and offcuts we keep and sell as scrap?

**Question:** When rejected products or material offcuts are kept to sell as scrap, at what value should they reduce the order's cost, in which account should they sit, and should they be entered into stock at all?

**What ParadisERP does today:**
- Rejected units are counted on the daily production record, but they **do not enter any stock**.
- Material scrapped during production (offcuts) is counted on the order but moves no stock (it already left at the issue).
- There is no place to record a recovery value: it is always 0, and the cost sheet says "Not recorded".
- The full order cost is spread over the good units only (the owner's plan, D23: "rejects absorbed, scrap value credited").
- The chart already has 318 (scrap stock, used today), and scrap sales post to 756.

**Why I'm asking:** If scrap with real resale value never enters stock, the good units carry too much cost. And when the scrap is later sold, its stock is missing (it may go negative or need a manual adjustment).

**Example:** The nails order rejects 4 kg, which we sell as scrap metal at 30 DA/kg = 120 DA. With the recovery recorded, the 196 good kg cost 53,480 DA (272.86 DA/kg). Without it, they cost 53,600 DA (273.47 DA/kg), and the 4 kg is not in any stock when we sell it for 120 DA.

**Possible answers:**
- A. Record a recovery value per order; scrap enters stock at that value in **318**; its sale posts to 756 as today.
- B. Same, but in a new scrap account for production (358, proposed).
- C. Give scrap no value (it is sold for very little): its sale is simply other income.

**Our suggestion:** A, using the existing 318, with the value typed per order by a manager (a fixed price per kg as a setting could come later). Please name the account.

**Priority:** Important

**Accountant's answer:**

> 

---

### C-10 / AD-1 / D13 — What do we do with costs we only learn after an order is finished?

**Question:** When a cost of a finished order becomes known later (payroll approved at month end, a late invoice, a correction), where should that difference go: leave it in the month's expenses, put it in a special variance account, or add it to the goods we made?

**What ParadisERP does today:**
- An order's cost sheet is frozen only when someone asks for it through the system's interface for programs (there is no screen yet), not automatically at completion.
- After the freeze, someone can record a "late cost" (amount, kind, source, reason), again with no screen.
- Late costs are **reported only**: never added to the frozen cost, to stock value or to released goods. The reconciliation page shows "frozen cost + recognised late costs = adjusted provisional cost".
- Expenses can still be added to an order after it is completed; they change the live figure but do not become late-cost records by themselves.
- If an order is completed with nothing left to release, its remaining cost stays in the expenses (D13).

**Why I'm asking:** Payroll is approved at month end, so most orders finish before their true labour cost is known. Some goods are already sold by then, and we must not revalue them.

**Example:** The nails order is frozen at 53,480 DA. Next week a 1,500 DA transport invoice for the wire arrives. All 196 kg are already in stock and 50 kg are sold.
- A: the 1,500 DA stays in transport expense; the nails keep 53,480 DA.
- B: 1,500 DA goes to a class-6 "production variance" account.
- C: the 1,500 DA is added to the 146 kg still in stock (about 10.27 DA/kg more).

**Possible answers:**
- A. Leave it in the expense accounts where it was charged, and report it on the cost sheet (the planning document's recommendation, D13).
- B. Post it to a production variance account in class 6 that you name.
- C. Spread it over the units still in stock.

**Our suggestion:** A. It never changes goods already sold and needs no extra account, and the reports already show it. Please confirm this is acceptable under the SCF. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### C-11 / AD-7 — How do we value finished goods released to stock before the order is finished?

**Question:** When part of an order's good units are moved into stock while the order is still running, what cost per unit should they carry?

**What ParadisERP does today:** Each partial release is valued at the recipe's standard material per unit (at the actual price of the material issued) plus the labour and expenses booked so far, shared over the good units made and not yet released. The last release of a completed order carries whatever is left, so all releases add up exactly to the order's cost. Until the cost is frozen, these values are recalculated each time they are read, so an early release's value moves when that month's payroll is approved. The frozen sheet saves them when the sheet is made, not at each release. The last release can come out negative, which is flagged.

**Why I'm asking:** Material is usually issued for the whole order on day one. If we used "cost so far ÷ units so far", the first release would carry nearly all the material and look very expensive. The planning document's D13 example uses "cost to date per unit", while C-11 proposes the rule above, so we need one answer.

**Example:** Release 1: 100 kg after day 1. Standard material 177.50 DA/kg + labour and overhead so far 43.20 DA/kg = 220.70 DA/kg, so 22,070 DA. Release 2 (final, 96 kg) carries the rest: 53,480 − 22,070 = 31,410 DA (327.19 DA/kg), including the galvanising and the payroll difference. With "cost to date ÷ units", release 1 would carry all the material issued so far: 39,820 DA ÷ 100 kg = 398.20 DA/kg, and release 2 only about 142 DA/kg.

**Possible answers:**
- A. Standard material per unit + labour and expenses to date per unit; the final release carries the rest (as built).
- B. Every partial release at the order's estimated unit cost; the final release carries the rest.
- C. Cost to date divided by good units to date.

**Our suggestion:** A. It is already built and tested, and it keeps early releases close to their real cost. Please also confirm that a release's value may be fixed at the moment it is made (not when the sheet is frozen) once Phase B is finished.

**Priority:** Critical

**Accountant's answer:**

> 

---

### C-12 — Until manufacturing accounting is live, may the factory sell what it makes?

**Question:** Before the system can post the real cost of our own products, should we keep factory stock movements switched off, or allow them with a temporary cost typed on each product?

**What ParadisERP does today:** Stock operations are off on the live site. If they were switched on now, a sale of a finished product would post its cost of sale from the product's catalogue cost (Dr 600 / Cr 310), and post **no cost at all** if that cost is 0. Manufacturing never writes a product's catalogue cost, so for our own products it is either typed by hand or 0.

**Why I'm asking:** Turning stock operations on early would give wrong margins and a wrong stock value for finished goods. Keeping them off delays using the factory module for real.

**Example:** We sell 50 kg of nails at 400 DA/kg = 20,000 DA before tax. Their real cost is about 13,643 DA. If the catalogue cost is 0, the books show 20,000 DA of pure profit and no cost. If someone typed an old cost of 200 DA/kg, the cost shows 10,000 DA.

**Possible answers:**
- A. Keep stock operations off until live posting (Phase D).
- B. Allow them, with each finished product's cost typed from its last approved unit cost, logged and reviewed monthly.

**Our suggestion:** A, as the plan recommends. If the business needs to start earlier, B, with a monthly check of each typed cost against the cost sheets.

**Priority:** Critical

**Accountant's answer:**

> 

---

### C-13 / AD-10 / AD-11 — Should the difference between planned and actual cost be posted?

**Question:** Each order shows how far its actual cost is from the planned cost (material used, labour hours, prices). Is that only a management figure, or should it be posted to an account?

**What ParadisERP does today:** The cost sheet and the reconciliation page show these differences (material usage and price, labour efficiency and rate, overhead, expenses, scrap). Nothing is posted. The planned cost has no line for expenses or overhead, so every expense shows entirely as a difference (AD-11).

**Why I'm asking:** Under Model N (C-03), no variance account is needed. Under Model W, a variance account is needed. Also, a plan without expenses makes the expense difference look large on every order.

**Example:** The nails planned cost was 215.90 DA/kg × 196 kg = 42,316 DA; actual 53,480 DA; difference 11,164 DA, of which 12,000 DA is the galvanising that was never in the plan.

**Possible answers:**
- A. Management figure only, no account (with Model N).
- B. Post to a variance account you name (with Model W).
- C. A and, in addition, add planned expenses to the recipe so the comparison is fair.

**Our suggestion:** A, plus C later as a product improvement (no accounting effect).

**Priority:** Important

**Accountant's answer:**

> 

---

### C-15 — Should orders still running at month end be valued in the books every month?

**Question:** At the end of each month, should we record the value of unfinished orders as work in progress, and reverse it on the 1st, or do this only at year end?

**What ParadisERP does today:** A "Work in progress" report shows, for each open order, its cost so far, what was already released and what remains, calculated when the report is opened. Nothing posts. Under Model N the plan proposes Dr 331 (work in progress) / Cr 72 at month end, reversed on the 1st.

**Why I'm asking:** Without this entry, materials and wages spent on unfinished orders show as expenses of the month with no matching stock, so monthly profit looks lower than it is.

**Example:** On 31 October the nails order has cost 39,820 DA so far and 22,070 DA was released: Dr 331 / Cr 72 17,750 DA, reversed on 1 November.

**Possible answers:**
- A. Every month, reversed on the 1st.
- B. Only at year end (for the annual accounts).

**Our suggestion:** A, so monthly results are fair. It is one automatic entry per month.

**Priority:** Important

**Accountant's answer:**

> 

---

### C-17 / AD-9 — How many decimals, and where does the rounding go?

**Question:** Is it acceptable to keep amounts at three decimals and let the final release of each order absorb the small rounding difference?

**What ParadisERP does today:** Money is rounded to 3 decimals and unit costs to 6, rounding halves away from zero. The final release takes the order's total less the other releases, so the releases always add up exactly. Late-cost amounts are rounded to 3 decimals when entered. Each rounding residual is shown on its own line.

**Why I'm asking:** The books must balance to the dinar and the cost sheets must match the entries exactly.

**Example:** Suppose release 1 works out at 22,070.37037 DA. It is kept as 22,070.370 DA, and the final release takes 53,480.000 − 22,070.370 = 31,409.630 DA, so the two add up exactly.

**Possible answers:**
- A. As built: 3 decimals, final release absorbs the residual.
- B. 2 decimals (centimes) in the ledger, same rule.

**Our suggestion:** A, unless your ledger works in 2 decimals.

**Priority:** Later

**Accountant's answer:**

> 

---

### AD-2 / AD-3 — How should a late cost be written down?

**Question:** When someone records a late cost, is it right that the amount is "its effect on the order's total cost" (a recovery is negative), and should the kind of cost be picked from a fixed list?

**What ParadisERP does today:** A positive amount raises the order's cost and a negative one lowers it; more scrap recovered is entered as a negative amount (AD-2). The kind is free text up to 32 characters. The reports read words like "material", "labour", "expense" or "recovery"; any other word is shown as "unclassified" and kept out of the adjusted cost. The record does not keep who entered it.

**Why I'm asking:** A late cost entered with the wrong sign or an unknown kind will be left out or counted the wrong way.

**Example:** Someone enters "labor diff" for +160 DA: the report shows it as unclassified and leaves it out of the adjusted cost. Entered as kind "labour", it is counted.

**Possible answers:**
- A. Keep the sign rule; make the kind a fixed list (materials, consumables, accessories, labour, overhead, expenses, recovery) and record who entered it.
- B. Keep both as today.

**Our suggestion:** A. The fixed list and the "who" field are a small change that needs separate approval by the owner.

**Priority:** Later

**Accountant's answer:**

> 

---

### AD-4 — A payment voucher used in an order's cost is cancelled afterwards: what happens?

**Question:** If an expense voucher already counted in an order's cost is cancelled, should the order's cost go down, and how do we record that if the cost is already frozen?

**What ParadisERP does today:**
- Only a pending or approved voucher can be cancelled; a paid one cannot.
- An approved voucher counts as "backed" in the cost sheet, but posts nothing to the books until it is paid. So a cancelled voucher was never in the books.
- The expense **stays in the order's cost** even after the voucher is cancelled. It is only marked provisional ("expense not settled").
- If the cost is already frozen, nothing changes. If someone records a negative late cost naming that voucher, the reports flag it as "uncertain" (it names something already in the frozen sheet) and leave it **out** of the adjusted cost.

**Why I'm asking:** The product's cost can include money that was never spent, and today the natural way to correct it is not counted.

**Example:** The galvanising voucher PV-000120 for 12,000 DA is approved and linked to the nails order, which is frozen at 53,480 DA. The job is then cancelled and so is the voucher. The frozen cost still includes the 12,000 DA. A late cost of −12,000 DA "PV-000120" is shown as uncertain, and the adjusted cost stays 53,480 DA instead of 41,480 DA.

**Possible answers:**
- A. Record a negative late cost (−12,000 DA) and count it, once a manager confirms it is not a duplicate.
- B. Before the freeze, drop expenses whose voucher is cancelled from the cost automatically; after the freeze, use A.
- C. No change to the order's cost; the difference is only reported.

**Our suggestion:** B. Under C-10 option A, the correction stays out of stock value either way; it only fixes the cost sheet and reports.

**Priority:** Critical

**Accountant's answer:**

> 

---

### AD-5 — Payroll is approved after the order is finished: how do we correct the labour cost?

**Question:** When the month's payroll is approved after an order's cost was frozen at the provisional labour rate, should we record the difference as a late cost, and should it include the employer's social contributions?

**What ParadisERP does today:** Before payroll is approved, labour is valued at a standard rate and marked provisional. Once payroll is approved, the live calculation switches to the actual rate, but a frozen cost sheet does not change. No late-cost record is written automatically: the reconciliation page can show the difference as "unexplained" when asked to compare with today. New payroll runs compute no employer social contribution (CNAS) since 2026-10-21 (see PAY — C-06 / D11).

**Why I'm asking:** This happens on almost every order that finishes before month end. Without a rule, labour on frozen orders stays at the provisional rate.

**Example:** The nails order is frozen with Karim's 8 h at the standard 380 DA/h = 3,040 DA. October payroll is approved and his actual rate is 400 DA/h = 3,200 DA. The difference is 8 × (400 − 380) = 160 DA. If an employer contribution of, say, 26 % were added (a figure for illustration only), his rate would be 504 DA/h and the difference 8 × (504 − 380) = 992 DA.

**Possible answers:**
- A. The system writes the late cost automatically when payroll is approved: hours × (actual rate − frozen rate), using payroll's own employer cost.
- B. A manager records it by hand each month from the reconciliation report.
- C. Do nothing; the difference stays in wages (C-10 option A) and is only reported.

**Our suggestion:** A, so the cost sheets are right without manual work, and whatever C-06 decides about CNAS. Under C-10 option A it changes reports only, not stock value.

**Priority:** Critical

**Accountant's answer:**

> 

---

### AD-6 — If an order's cost is frozen long after it was finished, is that still its "original" cost?

**Question:** If a cost sheet is frozen days or weeks after the order was completed, should it still count as the order's original cost, or as a reconstruction?

**What ParadisERP does today:** Completion does not freeze the cost; a sheet exists only when someone asks for it. A sheet made later includes everything that changed in between (payroll approved, vouchers changed), and those changes are not shown as late costs. The reports show the delay in days. A sheet can also be marked "reconstructed" on request.

**Why I'm asking:** A sheet frozen three weeks late looks like an original but already contains later information, so the late-cost reports would understate what changed.

**Example:** The nails order is completed on 10 October; its sheet is frozen on 5 November, after October payroll was approved. The 160 DA labour difference is already inside, and no late cost shows.

**Possible answers:**
- A. Freeze automatically when completion is approved (Phase B plan), so the question disappears.
- B. Treat any sheet made more than N days after completion as a reconstruction.
- C. Keep it as original and only show the delay (as today).

**Our suggestion:** A, with B (for example 7 days) for sheets made before that change.

**Priority:** Important

**Accountant's answer:**

> 

---

### AD-12 — When is an order's cost "final" for the accounts?

**Question:** What event should make an order's cost financially final: your signature on each sheet, the month being closed, or the cost being posted to the books?

**What ParadisERP does today:** Nothing is final. Every frozen sheet is labelled "provisional", and the "financially finalised" figure reads "Not available" (never 0). The system refuses to call any cost approved until the costing method is signed (C-01, C-05 to C-08, C-11).

**Why I'm asking:** Until this is defined, every manufacturing figure stays provisional, and the next phases (shadow entries, then real posting) have no point at which a cost stops changing.

**Example:** All October orders are frozen by 5 November, payroll is approved on 3 November, and the month is closed on 10 November. Option B makes all of them final on 10 November; later differences go to November under C-10.

**Possible answers:**
- A. Final when you approve each order's sheet.
- B. Final when the month is closed (all orders completed in that month together).
- C. Final when it is posted to the books (Phase D).

**Our suggestion:** B, the month close, because payroll and late invoices are known by then and it matches how the books are closed.

**Priority:** Critical

**Accountant's answer:**

> 

---

### MFG-01 — If an order produces fewer good units than planned, should all the extra cost go into stock?

**Question:** When a batch goes badly (many rejects, wasted material, extra hours), should the good units carry the whole cost, or should the part above a normal loss be treated as an expense of the period?

**What ParadisERP does today:** The whole cost of the order is divided by the good units released, whatever the loss. The share carried by rejects ("spoilage") and material wastage are shown for information only. The owner's plan (Revision 2, D23) says "rejects absorbed". There is no "normal loss" rate and no abnormal-loss account. Completion does not compare output with the planned quantity.

**Why I'm asking:** A very bad batch can make a product look far more expensive in stock than it really is. Our understanding is that abnormal waste is normally an expense of the period, not part of the stock value.

**Example:** We plan 200 kg of nails (estimated 215.90 DA/kg). A machine fault means only 120 kg is good and 80 kg is rejected. The order cost 50,000 DA, so the good nails go into stock at 416.67 DA/kg. If a normal loss is 5 %, only about 10 kg of rejects are "normal"; the cost of the other ~70 kg (about 17,500 DA) could go to an expense instead.

**Possible answers:**
- A. Good units carry the whole cost (as today; D23).
- B. Set a normal loss % per product; cost above it goes to an expense account you name.
- C. A, but a manager can mark an order as "abnormal" and its excess cost goes to expense.

**Our suggestion:** A for now, which the owner already planned (please confirm it is acceptable), with C added before live posting so exceptional batches do not inflate stock value. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### MFG-02 — An order makes nothing good at all: how do we record the loss?

**Question:** If a whole order is lost (every piece rejected), how should its materials, labour and expenses be recorded, and in which account?

**What ParadisERP does today:** An order with no good units released cannot be completed ("nothing produced, cancel the order instead"), but it also cannot be cancelled once any stage has recorded good pieces. So an order whose parts passed the first stage but were all rejected at the last stage appears to stay open. Under Model N its costs simply stay in materials used, wages and expenses. Nothing names a loss account.

**Why I'm asking:** A total loss should be visible as a loss, and the order should be closable.

**Example:** An order for 50 metal frames uses 30,000 DA of steel and 8,000 DA of labour. All 50 frames pass cutting but fail welding inspection. The 38,000 DA has no good units to go to, and the order cannot be closed today.

**Possible answers:**
- A. Leave the costs in their expense accounts (Model N) and close the order as "lost" with a reason; report it as a production loss.
- B. Also post the material part to a separate production-loss account you name.

**Our suggestion:** A, with a way to close such an order (a small change for the developers).

**Priority:** Important

**Accountant's answer:**

> 

---

### MFG-03 — When one order makes a part used by another order, what cost should that part carry?

**Question:** If we make a semi-finished part ourselves (for example cut and bent tubes) and then use it in another order, should it be charged at what it cost us to make, and until the system can do that, what value should we use?

**What ParadisERP does today:** A semi-finished product released from one order goes into stock like any finished product. When it is issued to the next order, its cost is the product's **catalogue cost**, which manufacturing never updates. So it is either a cost someone typed or 0 ("material with no cost"). The cost sheet adds it to raw materials.

**Why I'm asking:** In a factory with several steps, the final product's cost would miss everything spent making its parts, or use an old typed figure.

**Example:** Order A makes 100 bent tubes for 25,000 DA (250 DA each). Order B uses 40 of them to make chairs. The tubes' catalogue cost is 0, so order B's chairs carry 0 DA for tubes instead of 10,000 DA. If someone typed 180 DA, they carry 7,200 DA.

**Possible answers:**
- A. At its production cost, through the weighted average of that part, including production inflows (needs the valuation change, C-01).
- B. Until then, a manager types the part's cost from its last frozen cost sheet.
- C. Accept the catalogue cost (as today).

**Our suggestion:** A when the average cost method is introduced, and B in the meantime, logged.

**Priority:** Critical

**Accountant's answer:**

> 

---

### MFG-04 — A material was used before it had any cost in the system: what do we do?

**Question:** If a material is issued to an order while its cost in the system is 0 (never bought through a receiving note, or no cost entered), what value should we use?

**What ParadisERP does today:** The issue records a cost of 0. The cost sheet marks the material "Incomplete (part unvalued)" and the order stays provisional. The plan says a cost should not be frozen with an unvalued material unless someone accepts it with a reason, but **this check is not built**: a sheet can be frozen with it.

**Why I'm asking:** The order's cost and its stock value would be too low, with no warning once frozen.

**Example:** 10 cutting discs at 300 DA each are issued at 0. The nails' cost is 3,000 DA too low (about 15 DA/kg).

**Possible answers:**
- A. Refuse to freeze until the cost is entered, or a manager accepts it with a reason.
- B. Use the latest supplier price on record.
- C. Accept 0.

**Our suggestion:** A.

**Priority:** Important

**Accountant's answer:**

> 

---

### MFG-05 — Day workers paid by voucher on an order: labour or expense?

**Question:** When we pay casual day workers in cash by voucher for a production job, should their pay count as labour in the product's cost, or as an order expense?

**What ParadisERP does today:** A "daily labour" expense voucher posts to the wages account. It can be linked to an order as an **expense** (an expense voucher). It then shows in the expense line of the cost sheet, with no hours, while payroll workers show in the labour line.

**Why I'm asking:** The total cost is the same, but the labour figures, hourly comparisons and the planned-versus-actual differences are wrong if day workers are counted as expenses.

**Example:** Two day workers paid 3,000 DA each for one day on the nails order: 6,000 DA shows as "expenses", and the nails seem to use only 14 h of labour.

**Possible answers:**
- A. Keep them as order expenses (as today), described as day labour.
- B. Show them in the labour line, with the hours typed on the expense.

**Our suggestion:** A for now; B only if you need labour hours to be complete.

**Priority:** Important

**Accountant's answer:**

> 

---

## 6. Maintenance and Repair Profitability

### MNT-01 — Repairs on our own rental equipment: expense or added to the equipment's value?

**Question:** When we repair our own rental equipment, is the cost always an expense of the month (615), or must a large repair be added to the equipment's value?

**What ParadisERP does today:** Any repair paid by expense voucher goes in full to 615, the day it is paid. Nothing can add a repair to an item's value.

**Why I'm asking:** If our equipment becomes fixed assets (REN-12), a major overhaul that extends its life may have to be added to its value and depreciated, not expensed.

**Example:** Welding 20 frames: 11,000 DA → 615, clearly an expense. Rebuilding a concrete mixer's engine: 180,000 DA. Expense now, or added to the mixer's value?

**Possible answers:**
- A. Always an expense (615).
- B. Expense, except major work above an amount you set (please give it), which is added to the asset.

**Our suggestion:** A while equipment stays in stock; B only if REN-12 makes it fixed assets. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### MNT-02 — Spare parts from our own stock used in a repair

**Question:** When we take a spare part from our own stock to repair our equipment, which account should its cost go to?

**What ParadisERP does today:** There is no document for it. The only way is a stock adjustment, which books the part's cost as a stock loss (657), the same as breakage or theft. The manufacturing sign-off sheet already notes "615 when used in a repair (maintenance, out of scope)".

**Why I'm asking:** Repair costs then appear as "stock losses" instead of maintenance, so neither figure is right.

**Example:** 4 couplers at 350 DA and 2 base plates at 900 DA = 3,200 DA taken from stock to repair frames. Today: Debit 657 / Credit 310, 3,200 DA. Probably should be: Debit 615 / Credit 310 (or the supplies stock account).

**Possible answers:**
- A. 615 (maintenance and repairs).
- B. 602 or another consumption account (please name it).
- C. Leave it in 657.

**Our suggestion:** A, with a small "used for repair" reason on the stock movement until a repair module exists. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### MNT-04 — (Planned feature, not built) How should the cost of a repair job be calculated?

**Question:** If we build repair jobs, what should a job's cost include: spare parts, workers' time, outside work, and a share of workshop costs (electricity, tools, rent)?

**What ParadisERP does today:** Not built.

**Why I'm asking:** The answer decides what the system must record on each job and whether repair profit is real profit or only "parts and labour" margin.

**Example:** A job: parts 3,200 DA + 6 hours of a welder + outside painting 4,000 DA. At an hourly cost of 450 DA, direct cost is 3,200 + 2,700 + 4,000 = 9,900 DA. With 15 % workshop overhead: 11,385 DA.

**Possible answers:**
- A. Direct costs only (parts, labour, outside work).
- B. Direct costs plus a workshop overhead rate (please say how to set it).
- C. Parts and outside work only; labour stays a general wage cost.

**Our suggestion:** A for the first version: simple and checkable, with overhead added later if needed. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### MNT-05 — (Planned feature, not built) Workers' hours on a repair job

**Question:** If workers' hours are recorded on repair jobs, at what hourly rate should they be costed, and should this move any amounts in the books or only serve the profit report?

**What ParadisERP does today:** Not built for repairs. (Manufacturing already records hours on production orders without a rate; payroll books wages to 631 and employer social contributions to 635.)

**Why I'm asking:** Wages are already booked by payroll. Booking them again on jobs would count them twice; leaving them out would make repairs look too profitable.

**Example:** A welder costs 60,000 DA gross a month plus employer contributions (see Payroll — C-06, CNAS) ≈ 75,000 DA, for about 173 hours: ≈ 434 DA/hour. 6 hours on a job = 2,600 DA.

**Possible answers:**
- A. Cost hours for the report only; wages stay in 631/635 (no extra entry).
- B. Move the labour cost from wages into the job (and then into 615 for our own equipment, or into the cost of the service sold).
- C. Do not cost labour on jobs.

**Our suggestion:** A, using the same hourly rate method as manufacturing (see Manufacturing — labour rate) so both follow one rule. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### MNT-06 — (Planned feature, not built) Repairs sold to outside customers: which income account?

**Question:** If we repair equipment for outside customers (not linked to a hire), which income account should the repair go to, and should parts and labour be shown separately?

**What ParadisERP does today:** Not built. Repairs charged to hire customers on a return note go to 707 (REN-11). Ordinary sales go to 700.

**Why I'm asking:** We should use one account for repair income whatever the route, and know whether parts sold inside a repair are a sale of goods or part of the service.

**Example:** Repair invoice: parts 3,200 DA + labour 5,000 DA = 8,200 DA before tax. All to 706 / 707? Or 3,200 DA to 700 and 5,000 DA to 706?

**Possible answers:**
- A. All to one service account (706 or 707 — please choose).
- B. Parts to sales of goods (700), labour to services (706).

**Our suggestion:** Decide together with the service-income question (see Sales — service income 700/706). **(To be confirmed by the accountant.)**

**Built as a setting (10 Nov 2026, MS-34):** Maintenance › Settings › Customer repair income. 707, the account repair recharges use, stays the default. "One service account the accountant names" is answer A on the account you name in the account map (Repair service income, an income account of class 7, e.g. a 706 sub-account); "Parts to sales, labour to services" is answer B: the parts the job used at their sale price before tax to 700, the rest of the price to that account (parts above the price are capped at it). With VAT ticked the total and the tax are the same as on one line. Nothing else to change in the program (docs/maintenance/plan.md, "MS-34 — as built").

**Priority:** Important

**Accountant's answer:**

> 

---

### MNT-07 — (Planned feature, not built) TVA on repair services and on advances for repairs

**Question:** What TVA applies to a repair invoice (parts and labour), and when is it due — at the invoice, or when the customer pays, including when he pays an advance before the work?

**What ParadisERP does today:** Not built.

**Why I'm asking:** Our understanding (written as D45 / C-18) is that tax on services may fall due at payment, so an advance on a repair may carry tax when received. The repair module must follow the same rule as hire (REN-03).

**Example:** Customer pays a 20,000 DA advance on a 50,000 DA repair (before tax). Is TVA due on the 20,000 DA now, or on the whole 50,000 DA at the invoice?

**Possible answers:**
- A. Due on payment, including advances.
- B. Due on the invoice.

**Our suggestion:** Answer under TAX (tax on advances, D45 / C-18); we will apply it here. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### MNT-08 — (Planned feature, not built) Repair jobs not finished at month or year end

**Question:** At a year end, must we value the parts and labour already spent on repair jobs that are not finished (work in progress), or can they stay as expenses until the job is invoiced?

**What ParadisERP does today:** Not built.

**Why I'm asking:** For outside customers, costs spent in December on a job invoiced in January may need to move to January so that cost and income are in the same year.

**Example:** On 31 December, 3 open jobs with 45,000 DA of parts and labour; they will be invoiced in January for 70,000 DA.

**Possible answers:**
- A. Value them as work in progress at year end (please name the account).
- B. Leave them as expenses; the amounts are small.

**Our suggestion:** B unless the amounts are significant; the system can list open jobs at year end so you can decide. **(To be confirmed by the accountant.)**

**Priority:** Later

**Accountant's answer:**

> 

---

### MNT-09 — (Planned feature, not built) Free re-work under guarantee

**Question:** If a customer's repair has to be redone for free, should the cost be added to the original job, booked as a separate expense, or covered by a provision set aside in advance?

**What ParadisERP does today:** Not built.

**Why I'm asking:** It decides whether the repair profit report shows the real result of each job.

**Example:** A job invoiced 8,200 DA; the customer comes back within a month and we spend 2,000 DA of parts and labour to fix it again.

**Possible answers:**
- A. Add it to the original job.
- B. Separate expense (615 or other).
- C. A provision for guarantees.

**Our suggestion:** A for the profit report, B in the books. **(To be confirmed by the accountant.)**

**Priority:** Later

**Accountant's answer:**

> 

---

### MNT-10 — (Planned feature, not built) Keeping repair history per item of equipment

**Question:** Do you need, for the books or for the tax office, the repair and maintenance cost per piece of equipment (for example per mixer or per lorry), or only per type of item?

**What ParadisERP does today:** Not built. Rental stock is counted by product type (e.g. "frame 1.5 m"), not by serial number; expense vouchers can name a contract but not an item.

**Why I'm asking:** If equipment becomes fixed assets (REN-12), larger items may need their own record and history. This changes how the module is designed.

**Example:** 2,000 identical frames can be tracked as one type; 3 concrete mixers at 900,000 DA each may need one record each.

**Possible answers:**
- A. Per type is enough.
- B. Per piece for items above a value you set (please give it); per type for the rest.

**Our suggestion:** B. **(To be confirmed by the accountant.)**

**Priority:** Later

**Accountant's answer:**

> 

---
## 7. Employees, Payroll and CNAS

### PAY-01 — Please check our payroll against CNAS and IRG rules

**Status:** Owner Workflow Confirmed — Compliance Pending (CNAS/IRG to check).

**Owner's decision (31 Oct 2026):** We keep our current payslip and deductions exactly as they are.

**Question:** Looking at our payslip and the way we pay wages, what must we still declare and pay to CNAS and the tax office (IRG), and how do you want those amounts to appear in our books each month?

**What ParadisERP does today:** The payslip shows gross pay, the deductions we use, and the net paid; it does not calculate CNAS or IRG. The books record wages (631) and wages owed (421). Payments to CNAS or the tax office can be recorded on their own screen.

**Example:** A worker earns 45,000 DA gross and is paid 45,000 DA. If CNAS and IRG are due on that pay, how much, and which entries should we record (for example employer CNAS on 635/431, then the payment from the bank)?

**Possible answers:**
- A. What we do is compliant; you prepare the monthly CNAS/IRG figures and we record them as one entry per month.
- B. Something must change on the payslip (please say what).

**Our suggestion:** Please review a real payslip and month with us. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### C-06 / D11 — Employer social contributions (CNAS) in product cost

**Question:** Because new payroll runs record no employer CNAS share, should the labour cost of what we manufacture (a) be only what payroll records, (b) carry an added "employer burden %" in management reports only, or (c) carry that burden in the stock value too — and if (b) or (c), what percentage?

**What ParadisERP does today:** Manufacturing costing (Phase A) is built but posts nothing. It costs labour at what payroll records, so on months with no CNAS the employer share is missing; each such employee-month is marked "without employer social contribution (missing)" and the order's cost stays "provisional", never shown as final. No burden % is applied because none is approved. The Algeria preset's 30 % employer rate is a placeholder ("not the real split") and is not used on new runs. The preset defines no other employer levy.

**Why I'm asking:** Labour cost per product is understated by the employer share, so selling prices based on it could be too low. Putting an amount in stock value that the books never record as an expense would make stock worth more than what was spent.

**Example:** A welder costs 40,000 DA a month and spends 100 hours on an order. Without the employer share, labour on the order is about 23,000 DA. With a 26 % burden, about 29,000 DA. The difference changes the unit cost of every scaffold frame made that month.

**Possible answers:**
- A. Option 1: labour = what payroll records, in stock and in reports.
- B. Option 1 in stock value, plus option 2 (a burden % the accountant gives) in management cost reports only.
- C. Option 3: the burden in stock value too (not recommended).

**Our suggestion:** B — stock value follows the books; reports show a realistic cost for pricing. Employer levies, if any are ever set, are recomputed per employee through the payroll engine. Note this depends on PAY-01: if CNAS is turned back on in payroll, the burden is no longer needed. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### PAY-03 — Fines and other deductions that are not a wage saving

**Question:** When we deduct a fine (a penalty) or collect money for someone else from a worker's pay, which account should receive it?

**What ParadisERP does today:** Two ways exist. (1) A company-wide "deduction" can be marked "reduces the wage cost" (it lowers 631) or "other". An "other" deduction is posted, as a deliberate visible placeholder, as a credit to 641 (employer taxes on wages), which lowers that expense line until you name the right account. (2) A one-off figure for one person in one month ("adjustment", e.g. a fine of −2,000 DA) is counted inside the gross pay, so it lowers 631. The owner's payslip includes "absences or penalties if any" (owner note on answers 37–48, 2026-10-01). We asked this before.

**Why I'm asking:** A fine kept by the company is not the same as lower wages, and money collected for a third party is a debt, not a saving. Today both end up reducing an expense line.

**Example:** A driver is fined 3,000 DA for a damaged load. Gross 40,000 DA, net 37,000 DA. Today 631 shows 37,000 DA (adjustment) or 641 is credited 3,000 DA (an "other" deduction).

**Possible answers:**
- A. Fines reduce the wage cost (631) — keep as today.
- B. Fines go to an income account you name (e.g. a "other operating income" account).
- C. Money collected for a third party goes to a liability account you name.

**Our suggestion:** B for fines and C for anything collected for someone else, with the accounts named by you; the placeholder on 641 is then removed. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### PAY-04 — Pay that would go below zero, and advances that will never come back

**Question:** Where should the system put (a) the part of a payslip that could not be collected because deductions were larger than the pay, and (b) an advance that will never be repaid (the worker left, or the owner forgives it)?

**What ParadisERP does today:** (a) Net pay is never below zero. The amount that could not be taken is posted to 471 (suspense) so the month can still be approved, with a warning. (b) An advance stays "open" until a run takes it back; a "written off" state exists but there is no button for it, on purpose, until you name the expense account. When an advance is given to someone who has already left, the system warns that no run will recover it.

**Why I'm asking:** Without an answer, 425 (advances to staff) keeps money that will never come back, and 471 fills with amounts nobody clears.

**Example:** A labourer took a 30,000 DA advance and left after earning 12,000 DA in his last month. 12,000 DA is recovered; 18,000 DA stays on 425 indefinitely.

**Possible answers:**
- A. A loss account you name (e.g. a "other operating charges" account) after a manager's written decision.
- B. Keep it on 425 and pursue the person; write off only at year end.
- C. Treat a forgiven advance as extra pay (631) for that month.

**Our suggestion:** B by default, A when the owner signs a write-off; the button is built once the account is named. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### PAY-05 — Advance against salary versus a loan repaid in instalments

**Question:** Should a loan to an employee that is repaid over several months be kept in the same account as a small advance against this month's salary, and is it acceptable that the system takes back the whole open balance from the next pay?

**What ParadisERP does today:** There is one kind of advance. It is posted Dr 425 / Cr cash or bank the day it is given, and the next run takes back as much of the whole open balance as the pay allows (owner answer 37: "advances are deducted in full"). Loans repaid by a fixed monthly instalment (owner answer 38, with postponement allowed by the manager, answer 59) are **not built**: there is no instalment amount and no "loan" type.

**Why I'm asking:** A 100,000 DA loan repaid over 10 months would be taken entirely from the next payslip today, leaving the worker with nothing. Long loans may also belong in a different account from a short advance.

**Example:** A worker earning 42,000 DA borrows 100,000 DA, to repay 10,000 DA a month. Today the next run takes 42,000 DA (all of his pay) and warns that 58,000 DA is still open.

**Possible answers:**
- A. Same account 425 for both; the system just needs the instalment.
- B. Loans in a separate account you name; advances stay on 425.

**Our suggestion:** A — one account, with an instalment per loan (the developers build it); unless you want loans shown separately on the balance sheet. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### PAY-06 — Overtime rates and the hourly rate they are based on

**Question:** Are our overtime multipliers and the way the hourly rate is worked out acceptable?

**What ParadisERP does today:** Overtime kinds and multipliers are settings. The owner chose: nail-workshop overtime ×1 (normal rate), loading goods ×2, night hours ×2, work on the weekly rest day or a public holiday ×2 the daily rate (owner notes 2026-10-10; answers 39 and 58). The hourly rate = monthly base ÷ working days per month ÷ 8 hours. The owner uses ÷30 for a day (answer 40); the Algeria preset ships 26 days. Which figure is set in production: Needs verification. Overtime is calculated on the base wage only, not on allowances.

**Why I'm asking:** A ×1 rate is "no overtime premium". Whether that is allowed, and whether the hourly rate should come from the month divided by days, or by a number of legal monthly hours, is a labour-law and payslip question, not ours to decide.

**Example:** Base 36,000 DA. Day rate 36,000 ÷ 30 = 1,200 DA; hourly rate 1,200 ÷ 8 = 150 DA. 10 hours of loading overtime ×2 = 3,000 DA; 10 hours of workshop overtime ×1 = 1,500 DA.

**Possible answers:**
- A. Acceptable as is.
- B. Change the minimum multipliers to the figures you give.
- C. Change the hourly-rate basis (e.g. a fixed number of hours per month you give).

**Our suggestion:** Tell us the minimum legal premium and basis; the settings are changed, no code. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### PAY-07 — Absences and a month with no attendance entered

**Question:** Is it right to deduct an absent day as one thirtieth of the gross pay, and to pay a monthly worker in full when nobody entered his attendance?

**What ParadisERP does today:** An unpaid absent day = gross (base plus allowances) ÷ the working days per month setting, taken off the gross (owner answer 40: "absence day = gross ÷ 30"). A monthly employee with no attendance recorded is paid the full month, with a warning on the run. Days with no fingerprint are absences (owner answer 45).

**Why I'm asking:** The divisor changes every absence deduction, and paying in full by default can overpay when a sheet was simply forgotten.

**Example:** Gross 39,000 DA, 3 unpaid absences: 39,000 ÷ 30 × 3 = 3,900 DA deducted. With ÷26 it would be 4,500 DA.

**Possible answers:**
- A. ÷30 and pay in full with a warning — as today.
- B. Another divisor (please give it).
- C. Hold the line of anyone with no attendance until it is entered.

**Our suggestion:** A; the warning already stops a forgotten sheet going unnoticed. **(To be confirmed by the accountant.)**

**Priority:** Later

**Accountant's answer:**

> 

---

### PAY-08 — A month approved late, and which month a correction belongs to

**Question:** When March payroll is approved in May, or an approved March run is reversed in May and approved again, which dates should the entries carry?

**What ParadisERP does today:** The approval entry is always dated the last day of the payroll month (a March run approved on 5 May is dated 31 March). There is no "closed period": the system will post into a month the accountant has already closed or declared. A reversal is dated the day it is made (5 May), while the corrected run is dated 31 March again.

**Why I'm asking:** Posting into a closed month changes figures already reported. And reversing in May but re-posting in March counts March's wages twice in March and takes them off May.

**Example:** March wages 1,200,000 DA approved 3 April, reversed 5 May for an error, re-approved at 1,210,000 DA. March shows 2,410,000 DA, May shows −1,200,000 DA.

**Possible answers:**
- A. Reversal dated like the original (end of the payroll month), so the correction stays in March.
- B. Keep today's dates, and lock a month once the accountant closes it; after that, corrections go into the next open month.
- C. Both: the reversal follows the original's date while the month is open; after closing, everything goes into the current month.

**Our suggestion:** C, with a "closed up to" date the accountant sets. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### PAY-09 — Correcting a month that was already paid

**Question:** Is it acceptable that a paid payroll month is never reopened, and any error is corrected by an adjustment in the next month's payroll?

**What ParadisERP does today:** An approved run can be reversed only if nothing has been paid against it (reverse the wage payments first). A paid run is final; the fix is a signed adjustment (bonus or deduction, with a reason) on the next run. A recompute after approval is impossible without reversal. Overtime entries in an approved or paid month cannot be edited (owner, 2026-10-10).

**Why I'm asking:** The payslip of the month with the error stays wrong; the correction appears on the next payslip, and in the next month's books.

**Example:** In April we notice a worker was underpaid 4,000 DA in March. The April payslip shows "+4,000 DA correction March"; March's books and payslip stay as they were.

**Possible answers:**
- A. Acceptable.
- B. We need a corrected March payslip (a supplementary payslip for March).

**Our suggestion:** A; a supplementary payslip would only be built if a declaration needs it. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### PAY-10 — An employee leaving mid-month: the final pay

**Question:** What must the last payslip of an employee who leaves contain, and how should it be booked?

**What ParadisERP does today:** Not built as a final settlement. The leaver stays in the run of the month he left (the employee list includes anyone employed on at least one day of the month). The base is **not** cut automatically by the leaving date: a monthly employee is paid the full month unless absences are entered for the days after he left (with no attendance at all, he is paid in full with a warning). There is no leave-pay compensation, no notice or end-of-contract payment, and an open advance is only taken back up to his last net pay.

**Why I'm asking:** We could overpay a leaver, or forget amounts he is owed (unused leave).

**Example:** A worker on 36,000 DA leaves on 10 June with 8 days of unused leave and a 15,000 DA advance open. Today June pays 36,000 DA minus any absences typed, minus up to the net for the advance, and nothing for the leave.

**Possible answers:**
- A. Pro-rata of the month by days employed, plus unused leave paid, minus the advance; any shortfall handled as in PAY-04.
- B. Pro-rata only; leave and other amounts entered by hand as adjustments.

**Our suggestion:** B now (adjustments exist), A when the leave module is built. Please list what the final pay must contain. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### PAY-11 — Paid leave: balance, pay and year-end provision

**Question:** How should paid annual leave be paid and accounted for?

**What ParadisERP does today:** The attendance sheet has a "paid leave" day status; such days are paid like worked days (a monthly worker loses nothing). There is no leave balance: the owner's rule of 2.5 days per month worked, carried over (answer 48), is **not built**. There is no provision at year end for leave earned but not taken.

**Why I'm asking:** Leave earned but not taken is a debt to staff; and the leave pay itself may not simply be "the normal salary for those days".

**Example:** 20 workers each have 10 days of leave not taken on 31 December, at 1,200 DA a day: 240,000 DA owed but not in the books.

**Possible answers:**
- A. Leave days paid at the normal salary; no year-end provision.
- B. Leave pay calculated by a rule you give; year-end provision for leave not taken (accounts named by you).

**Our suggestion:** Tell us the rule; we would build the balance first (owner already asked for it). **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### PAY-12 — Day labourers paid by payment voucher

**Question:** Is it acceptable to pay casual day labourers by a payment voucher, posted straight to 631 wages, with no payslip, no CNAS and no IRG?

**What ParadisERP does today:** The owner said there are no daily workers in payroll; a day labourer is paid by a payment voucher under the category "daily labour" (owner answers 41/47, 2026-10-02). That category posts Dr 631 / Cr cash or bank. The worker is not an employee in the system and nothing is withheld or declared. The code marks this account as "the accountant confirms".

**Why I'm asking:** These are real wages in 631 with no social or tax treatment and no record per person beyond the payee name on the voucher.

**Example:** Five labourers unload a lorry for 2,500 DA each: one voucher of 12,500 DA, Dr 631 / Cr 530.

**Possible answers:**
- A. Acceptable as is.
- B. Acceptable, but post to a different account (e.g. outside labour / services) you name.
- C. They must be declared; put them in payroll.

**Our suggestion:** Your answer decides; the account is a setting change, putting them in payroll is a larger change. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### PAY-13 — Which branch carries a worker's wages

**Question:** Is it acceptable that a worker moved to another branch during the month is charged in full to the branch that holds him when payroll is computed?

**What ParadisERP does today:** One run per branch per month. A worker who changes branch is paid the whole month by the branch he belongs to when the run is computed; there is no split by days. Wages paid in cash from another branch's box are booked against the paying box's branch (see P0.2 in section 8 for the effect on company-wide reports).

**Why I'm asking:** Branch profit figures move with the date of the transfer, not the work done.

**Example:** A mason works 20 days at Douera and 10 at Ouled Moussa in May (36,000 DA). If he is at Ouled Moussa on compute day, Ouled Moussa carries all 36,000 DA.

**Possible answers:**
- A. Acceptable.
- B. Split by days (needs a dated branch history and a line in each branch's run).

**Our suggestion:** A; transfers mid-month are rare. **(To be confirmed by the accountant.)**

**Priority:** Later

**Accountant's answer:**

> 

---

*Related, written by Agent F:* payroll approved after a manufacturing order is complete (labour true-up) is **AD-5**, see section on manufacturing costing; this section does not repeat it.

---

## 8. Cash, Banks and Treasury

### P0.2 — Wages, advances and fund payments paid from another branch's box vanish from company-wide reports

**Question:** We want to correct the company-wide (group) reports so that wages, advances and payments to the fund or tax office paid from another branch's cash box are included again, for past months too. Is that acceptable, and do you want the list of affected entries before it is done?

**What ParadisERP does today:** When a wage payment, an advance or a statutory payment is paid from a box belonging to another branch, its entry is marked "inter-branch". Company-wide reports leave out every entry marked "inter-branch", which is right for a remittance (cash simply moving between our own boxes) but wrong here: the money really left the company. The fix was planned (Phase 0, item P0.2): stop marking these three kinds as inter-branch, and have group reports include past ones by their type, without editing any entry. **Not built yet** (the code still marks them). Owner decision P0-D2 "correct past group reports, with the list of affected entries sent to the accountant first" is pending your review.

**Why I'm asking:** Group figures for past periods will change; profit does not change, but cash, wages owed (421), advances (425) and the fund/tax liabilities (431/442/447) do.

**Example:** Branch A's wages of 50,000 DA are paid from branch B's box. Branch A's sheet shows the debt settled; branch B's sheet shows the cash gone. The group sheet still shows the 50,000 DA in cash and the 50,000 DA still owed to staff.

**Possible answers:**
- A. Correct past and future group reports as planned, after you review the list of affected entries.
- B. Correct only from now on; leave past group reports as they were.

**Our suggestion:** A. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### TRS-01 — Opening balances of cash and bank

**Question:** How should we bring into the books the cash in each box and the money in each bank account on the day we started using ParadisERP?

**What ParadisERP does today:** Not built. There is an opening-balance entry for stock (against 890) and for customer and supplier balances, but nothing for cash or bank. A box has an "opening" movement type that nothing writes. So 530 and 512 started at zero.

**Why I'm asking:** Every cash and bank figure in the balance sheet is off by the amounts held on day one.

**Example:** On the first day the Skikda box held 85,000 DA, the central safe 420,000 DA and the bank 3,600,000 DA. The books show 0 for all three.

**Possible answers:**
- A. One opening entry per box and per bank: Dr 530/512 / Cr 890 (opening balance), dated the start day.
- B. The accountant posts them from his own closing balance sheet.

**Our suggestion:** A, with the figures given by you from the last closed balance sheet; the box's own count gets the same opening figure. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### TRS-02 — Cash deposited at the bank, and cash withdrawn from it

**Question:** How should we record a branch's or the safe's cash being deposited at the bank, and cash withdrawn from the bank into a box?

**What ParadisERP does today:** Not built. A remittance moves cash only between our own boxes. There is no document for box → bank or bank → box, and no manual entry.

**Why I'm asking:** If takings are deposited at the bank, the box stays too high and the bank too low in the books, forever.

**Example:** The central safe deposits 1,500,000 DA at the bank on Thursday. Books after: 530 still +1,500,000 DA, 512 still missing 1,500,000 DA.

**Possible answers:**
- A. Directly Dr 512 / Cr 530 on the deposit day.
- B. Through an "internal transfers" account (Dr 58x / Cr 530 at deposit, then Dr 512 / Cr 58x when the bank statement shows it).

**Our suggestion:** B if deposits take a day or more to appear on the statement; otherwise A. Please name the account. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### TRS-03 — One "bank" account for every bank and the postal account

**Question:** Do you need a separate account for each bank account (and for a CCP postal account, if we have one), or is one bank account in the books enough?

**What ParadisERP does today:** Everything that is not cash posts to one account, 512. There is no list of bank accounts; the system cannot say which bank received a card payment, a transfer or a cleared cheque, or which bank a supplier was paid from.

**Why I'm asking:** With more than one bank, 512 cannot be compared with any single bank statement.

**Example:** We have accounts at BNA and CPA. A 250,000 DA transfer from a customer and a 180,000 DA cheque to a supplier both just show as 512.

**Possible answers:**
- A. One 512 is enough.
- B. One sub-account per bank (e.g. 5121, 5122…) chosen on each document.

**Our suggestion:** B if there is more than one bank account; tell us the list and the codes. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### TRS-04 — Bank reconciliation, bank charges and interest

**Question:** How do you want bank charges, bank interest and direct debits to reach the books, and do you need a monthly bank reconciliation in ParadisERP?

**What ParadisERP does today:** Not built. There is no bank statement import, no reconciliation screen and no manual journal entry (the only correction is reversing an entry a document posted). So bank fees, account charges, interest and direct debits cannot be recorded at all.

**Why I'm asking:** Without them, 512 never matches the bank statement.

**Example:** The March statement shows 4,200 DA of account fees and 1,800 DA of transfer commissions that appear nowhere in the books.

**Possible answers:**
- A. A "bank movement" document (fees, interest, other) with an account you name for each type.
- B. A general manual journal entry for the accountant only.
- C. The accountant keeps the bank in his own tool and adjusts at closing.

**Our suggestion:** A for daily items, plus a simple monthly reconciliation screen later. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### TRS-05 — Card payments: when do they count as money at the bank, and the card fees

*This question also answers: SAL-05 (Card takings straight to 512 or via a clearing account with commission?).*

**Question:** Should a card payment be recorded at the bank on the day of the sale, and where should the card commission go?

**What ParadisERP does today:** A card payment at the till is treated as confirmed at once and posts Dr 512 on the sale date. The commission the bank or the card network keeps is not recorded anywhere. A card payment typed in the back office (a receipt voucher) waits "pending" until someone confirms it, then posts Dr 512 / Cr 411.

**Why I'm asking:** The money arrives later and net of commission, so 512 is higher than the bank statement until fees are recorded.

**Example:** A 60,000 DA card sale on 3 March; the bank credits 59,100 DA on 5 March (900 DA commission). Books: 60,000 DA on 512 on 3 March, and 900 DA never booked.

**Possible answers:**
- A. Keep 512 on the sale day; record the commission with the bank items in TRS-04.
- B. Card sales go to a "card payments to receive" account and move to 512, net of commission, when the bank credits them.

**Our suggestion:** A while card volume is small; B if card sales become frequent. Please name the commission account. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### TRS-06 — Cheques we receive: when are they recorded?

*This question also answers: SAL-11 (When a customer cheque reduces the debt; which date (till vs back office differ)).*

**Question:** When a customer hands us a cheque, should it be recorded as "cheques to collect" (413) straight away, or only when the bank has paid it?

**What ParadisERP does today:** It depends where the cheque is taken. At the till: on the sale, Dr 413 (notes receivable); on confirmation, Dr 512 / Cr 413; if it bounces, Dr 411 / Cr 413 (the customer owes it again). On a back-office receipt voucher: nothing is posted until someone confirms it; then Dr 512 / Cr 411, dated the day the cheque was received, not the day it cleared. A second person must confirm a cheque (owner, 2026-10-11). A rental contract paid by cheque is not closed until the cheque is confirmed (owner answer 22).

**Why I'm asking:** The two paths give different balances for 411 and 413 for the same kind of paper, and the bank is credited on the receipt date even when the cheque clears weeks later.

**Example:** A customer pays a 300,000 DA invoice by cheque on 2 March; it clears 15 March. Back office: 411 stays 300,000 DA until 15 March, then 512 is debited dated 2 March. Till: 413 shows 300,000 DA from 2 March, 512 on 15 March.

**Possible answers:**
- A. Always 413 on receipt, 512 on the clearing date.
- B. Always nothing until cleared, then 512 on the clearing date.
- C. Keep the two paths as they are.

**Our suggestion:** A — it matches the till and the "cheques due" screen. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### TRS-07 — A cheque that bounces after it was confirmed, and bounce fees

**Question:** How should we record a cheque that we had confirmed but the bank later returns unpaid, and the fees the bank charges for it?

**What ParadisERP does today:** Not built. Once confirmed, a cheque cannot be marked bounced (only a pending one can). There is no way to record the bank's fee or to charge it to the customer.

**Why I'm asking:** A late bounce leaves 512 too high and the customer's debt too low.

**Example:** A 120,000 DA cheque is confirmed on 10 March and returned unpaid on 14 March with 1,500 DA bank fees.

**Possible answers:**
- A. Allow "bounced after confirmation": Dr 411 / Cr 512 for the amount; the fee Dr bank charges / Cr 512.
- B. As A, and the fee is also charged to the customer (Dr 411 / Cr an income account you name).

**Our suggestion:** A; B only if the company actually re-bills the fee. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### TRS-08 — Cheques and transfers we issue

**Question:** When we pay a supplier or an expense by cheque, should the bank be reduced on the day we hand over the cheque or on the day the bank pays it?

**What ParadisERP does today:** A payment voucher paid by any method other than cash posts Dr expense/supplier / Cr 512 on the day it is marked paid. The cheque number and bank are stored. There is no list of our cheques not yet cashed.

**Why I'm asking:** Cheques not yet cashed make 512 lower than the bank statement; without a list nobody can reconcile.

**Example:** We pay a supplier 450,000 DA by cheque on 28 March; he cashes it on 9 April. 512 is reduced on 28 March.

**Possible answers:**
- A. Reduce 512 on the day the cheque is handed over (as today), plus a list of outstanding cheques.
- B. Use a "cheques issued" account and move to 512 when cashed.

**Our suggestion:** A, with the outstanding-cheques list as part of the reconciliation (TRS-04). **(To be confirmed by the accountant.)**

**Priority:** Later

**Accountant's answer:**

> 

---

### TRS-10 — A cash box going below zero

**Question:** May the books show a cash box with less than zero, and if the system sees a payment larger than the box balance, should it record it anyway?

**What ParadisERP does today:** Cash payments (payment vouchers, wages, advances, fund payments) larger than the box balance are recorded with a warning, never refused (because a box is often refilled before anyone records it). A remittance larger than the box balance is refused (owner rule, 2026-10-08). So a box can go negative from payments, and then no remittance can leave it.

**Why I'm asking:** Physical cash cannot be negative; a negative box means a missing record (an unrecorded refill or receipt).

**Example:** The Tipaza box shows 20,000 DA. The manager pays 35,000 DA of wages from it (he brought cash from the safe without recording it). The box shows −15,000 DA.

**Possible answers:**
- A. Keep warnings; the accountant checks negative boxes each month.
- B. Refuse any cash payment above the box balance (the remittance rule for everything).
- C. Keep warnings, and the warning offers to record the missing refill (a remittance from the safe) first.

**Our suggestion:** C — it keeps the owner's "never block" rule for payday and keeps the box right. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### TRS-11 — A payment voucher paid by mistake

**Question:** When a payment voucher was paid by mistake (wrong amount, wrong supplier, or the money came back), how should it be corrected?

**What ParadisERP does today:** A pending or approved voucher can be cancelled with a reason. A **paid** voucher cannot be cancelled ("the money has left the box and the entry is in the books"), and there is no "reverse" for it. Payroll vouchers (advance, wages, fund) are cancelled automatically only when their payroll document is reversed, and that puts the cash back. The generic "reverse an entry" action in accounting reverses the ledger lines only: it does not put the cash back in the box, does not reopen the voucher and does not restore the supplier's or customer's balance, so books and box would disagree. (How a cancelled voucher affects manufacturing costing is AD-4, Agent F.)

**Why I'm asking:** Mistakes happen; today the only fix leaves the cash box, the voucher list and the ledger out of step.

**Example:** A fuel voucher of 18,000 DA was paid twice. The second payment should be undone: the 18,000 DA is put back in the box.

**Possible answers:**
- A. A "reverse paid voucher" action: mirror entry dated today, cash back in the box, voucher marked reversed, balances restored.
- B. No reversal; the money returned is recorded as a receipt (a new document) against the same expense account.

**Our suggestion:** A, with a reason and the manager's approval, dated the day of the correction. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### TRS-13 — Cash on its way to the safe

**Question:** Should cash collected from a branch be shown as "in transit" until it reaches the central safe, or is it enough to move it from box to box in one step?

**What ParadisERP does today:** A remittance moves the cash in one step: out of the branch box and into the safe at the same moment, dated the moment it is recorded, with the collector's name. There is no "in transit" state and no confirmation by the safe.

**Why I'm asking:** If the manager collects on Monday and reaches the safe on Wednesday, the safe shows money it does not yet hold.

**Example:** The manager collects 380,000 DA from Skikda and Constantine on Monday and hands it in on Wednesday.

**Possible answers:**
- A. One step, as today.
- B. Two steps through an internal transfers account: out of the branch on collection, into the safe when counted there.

**Our suggestion:** A unless collections often take more than a day. **(To be confirmed by the accountant.)**

**Priority:** Later

**Accountant's answer:**

> 

---

### TRS-14 — Petty cash

**Question:** Do you need a separate petty-cash box (for small expenses) beside each branch's till cash?

**What ParadisERP does today:** Not built as a separate box. Each branch has exactly one box (created automatically), and the main branch's box is the central safe. Small expenses are paid by payment voucher from that box (any amount needs a manager's approval — owner answer 25).

**Why I'm asking:** Mixing takings and expense cash in one box makes daily counts harder.

**Example:** The Douera branch keeps 20,000 DA for fuel and small purchases apart from its sales cash.

**Possible answers:**
- A. One box per branch is enough.
- B. A second box per branch for petty cash, topped up from the safe.

**Our suggestion:** A for now. **(To be confirmed by the accountant.)**

**Priority:** Later

**Accountant's answer:**

> 

---

## 9. Branches and Internal Transfers

### BRN-01 — One set of books with a branch on each line?

**Question:** Is one company ledger, with the branch recorded on every line, enough, or must any branch keep its own separate books or file its own declarations?

**What ParadisERP does today:** One ledger for the company. Every entry and every line carries a branch, so reports can be run for the group or for one branch. Movements between branches (cash remittances, stock transfers) are marked and dropped from the group figures. A branch can have its own fiscal identifiers, printed on its papers instead of the company's.

**Why I'm asking:** If a branch is registered separately for tax (its own tax office or article number), it may need its own declarations or its own figures; the system would then have to produce them per branch.

**Example:** The company has a head office in Algiers, a warehouse and a branch in Oran. The Oran branch prints its own article number on its invoices. Does Oran file anything on its own, or does the head office declare for all?

**Possible answers:**
- A. One ledger with a branch tag is enough; all declarations are made by the company.
- B. One ledger, but some declarations must be produced per branch (tell us which).
- C. Separate books per branch.

**Our suggestion:** A, unless a branch is registered on its own with the tax office. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### BRN-02 — The account between branches (181) is computed, not posted

**Question:** Is it acceptable that the inter-branch current account (181) is computed on a single branch's balance sheet instead of being posted with each movement between branches?

**What ParadisERP does today:** No entry posts to 181. A transfer or a remittance moves the same account (310 or 530) from one branch to the other in one entry. A single branch's balance sheet would not balance on its own, so the system adds the missing amount as a computed 181 line. On the group balance sheet it is zero.

**Why I'm asking:** If you need to read or reconcile 181 movement by movement (for example per pair of branches), a computed figure is not enough.

**Example:** The warehouse transfers 300,000 DA of goods to Oran, and Oran pays a supplier 150,000 DA for goods received at the warehouse. Oran's balance sheet shows a computed 181 of 150,000 DA owed to the group; nothing in the journal shows 181.

**Possible answers:**
- A. Computed is acceptable while branches are not separate accounting units.
- B. Post 181 on both branches with every movement between them.

**Our suggestion:** A, if BRN-01 is A.

**Priority:** Later

**Accountant's answer:**

> 

---

### C-14 — Goods transfers between branches (D47)

**Question:** We propose: goods move between the company's own locations at cost (the average once C-01 is switched), with no sale, no tax and no invoice, because both ends belong to one company with one tax number; transport paid for a transfer is the receiving branch's expense (624), balanced through 181. Do you agree, or should transport be added to the goods' value?

**What ParadisERP does today:** A transfer is a numbered document (TRF-000001). Dispatch takes the quantity off the sending location and posts nothing; the goods stay in the sender's 310 while in transit. Each receipt posts 310 at the receiving branch against 310 at the sending branch, at the product's cost frozen at dispatch (today the last purchase price), marked inter-branch so group figures drop it. No sale, no tax, no invoice. Transport for a transfer is not built: an expense voucher "transport" posts 624 against the cash box of the branch that paid, with no link to the transfer and no move to the receiving branch. Note: each branch may carry its own fiscal identifiers (BRN-01).

**Why I'm asking:** We must be sure an internal movement is not a taxable delivery, and we must know where transfer transport belongs.

**Example:** The warehouse sends 200 couplers (cost 1,250 DA, 250,000 DA) to Oran; a hired truck costs 18,000 DA, paid from the head office's cash box (the warehouse has none). Today: 250,000 DA moves from the warehouse's 310 to Oran's; 18,000 DA of transport sits at the head office. Proposed: the 18,000 DA is Oran's expense.

**Possible answers:**
- A. As proposed: at cost, no tax; transport is the receiving branch's expense (624).
- B. At cost, no tax; transport added to the goods' value at the receiving branch.
- C. A taxable internal delivery is needed for some transfers (please say which).

**Our suggestion:** A. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### BRN-03 — Goods in transit at month end

**Question:** Is it acceptable that goods dispatched but not yet received at month end stay in the sending branch's stock value?

**What ParadisERP does today:** Dispatch posts nothing. Until the goods are received, declared lost or the transfer is cancelled, their value stays in 310 at the sending branch; the stock report shows them "in transit" at the sending branch, not as available anywhere.

**Why I'm asking:** At month end each branch's stock value includes or excludes these goods; the group total is right either way.

**Example:** On 31 October the warehouse dispatches 120,000 DA of planks to Oran; Oran receives them on 2 November. On 31 October they are in the warehouse's 310, not Oran's.

**Possible answers:**
- A. Acceptable (today).
- B. Show in-transit goods in a separate stock account until received.

**Our suggestion:** A.

**Priority:** Later

**Accountant's answer:**

> 

---

### BRN-04 — Goods lost in transit: who bears the loss, and what if someone pays it back?

**Question:** Is it right that goods lost between two branches are a loss (657) of the sending branch, and how should we record money recovered from the driver, the carrier or an insurer?

**What ParadisERP does today:** The sending branch (or a user allowed to write off) declares a loss with a reason, or the receiving branch reports a shortfall and the sending branch confirms it. Both post 657 against 310 at the sending branch, at the cost frozen at dispatch, and are not marked inter-branch (a real loss for the company). A rejected shortfall goes back to "in transit". Recovering the loss from someone is not built.

**Why I'm asking:** If a driver or carrier is responsible, the amount is a claim, not only a loss; and losses may need a paper to be accepted for tax.

**Example:** TRF-000042: 50 couplers dispatched at 1,250 DA; 46 arrive. 4 confirmed lost: 5,000 DA to 657 at the warehouse. The carrier agrees to pay 5,000 DA.

**Possible answers:**
- A. Loss at the sending branch (today); a recovery is income to an account you name (for example 757, the account we already use for damage and loss compensation).
- B. Loss at the receiving branch.
- C. When someone is responsible, a claim on that person or company (a receivable) instead of a loss.

**Our suggestion:** A, with C when a responsible party is known. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### BRN-05 — A reported shortfall left unanswered

**Question:** Should a shortfall reported by the receiving branch, and not answered by the sending branch, be written off automatically after some time or at month end?

**What ParadisERP does today:** A reported shortfall stays "in transit" at the sending branch, waiting for confirmation or rejection, with no time limit.

**Why I'm asking:** Goods nobody can find stay in stock value for months.

**Example:** On 5 October Oran reports 6 planks short (16,800 DA). By 31 December nobody has answered; the 16,800 DA is still in the warehouse's stock.

**Possible answers:**
- A. No limit; a list of open shortfalls at each month end for the manager.
- B. Written off automatically after 30 days, or at year end.

**Our suggestion:** A, plus write-off at the year-end count (STK-03).

**Priority:** Later

**Accountant's answer:**

> 

---

### BRN-06 — One branch buys, another pays the supplier

**Question:** Is it acceptable that a purchase is recorded at the branch that received the goods, while the payment is recorded at the branch whose cash box paid?

**What ParadisERP does today:** The receiving note raises 401 at the receiving branch (a warehouse has no cash box). A supplier payment voucher lowers 401 at the branch that paid. The company's total for each supplier is right; per branch, the difference appears in the computed 181 (BRN-02).

**Why I'm asking:** Branch reports then show a branch owing a supplier it never paid, and another branch paying a supplier it never bought from.

**Example:** The warehouse receives 600,000 DA of scaffolding; the head office pays it. Warehouse: 401 = 600,000 DA owed. Head office: 401 = −600,000 DA. Group: 0.

**Possible answers:**
- A. Acceptable: only the group figure matters for suppliers.
- B. Record the payment at the branch that bought, with 181 between the two.

**Our suggestion:** A, if BRN-01 is A.

**Priority:** Later

**Accountant's answer:**

> 

---

### BRN-07 — The warehouse's own costs

**Question:** For branch profit reports, should the warehouse's costs (wages, rent, transport) be shared out over the selling branches, and how?

**What ParadisERP does today:** A warehouse holds and moves stock and never sells (no till, no cashier, no cash box). Transfers move goods at cost with no mark-up, so the warehouse shows costs and no income, and branches show margins that carry none of the warehouse's costs. Sharing out costs is not built.

**Why I'm asking:** Branch profits look better than they are, and the warehouse looks like a loss-maker.

**Example:** The warehouse costs 450,000 DA a month. Algiers sells 60 % and Oran 40 % of the goods. Shared by sales, Algiers would carry 270,000 DA and Oran 180,000 DA.

**Possible answers:**
- A. Show the warehouse as a separate central cost (today).
- B. Share it in management reports only (by sales, by goods received), never in the books.
- C. Charge it in the books through 181.

**Our suggestion:** A now; B later if the owner wants branch profit after central costs.

**Priority:** Later

**Accountant's answer:**

> 

---
## 10. VAT/TVA and Algerian Taxes

### TAX-01 — Which sales may legally go out without VAT?

**Status:** Owner Workflow Confirmed — Compliance Pending (legal eligibility to confirm).

**Owner's decision (31 Oct 2026):** ParadisERP already records both taxed and untaxed sales; the choice is made on each sale.

**Question:** In which cases may we legally record a sale without VAT (which customers, which goods, which situations), and what proof must we keep for each one?

**Why I'm asking:** We want to make sure every untaxed sale we record is one the law allows.

**Example:** A walk-in customer buys 50,000 DA of pipes and pays cash without asking for an invoice. Taxed, he would pay 59,500 DA and 9,500 DA would be owed to the tax office. May this sale go out without VAT?

**Possible answers:**
- A. Every sale is taxed, unless the customer has a written exemption (please say which documents).
- B. Certain goods or customers are exempt (please list them).
- C. Other (please explain).

**Our suggestion:** Your answer will set our rule for when a sale may be untaxed. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### TAX-02 — Asking for a taxed invoice after an untaxed sale

**Question:** When a customer bought without VAT at the till and comes back days later asking for an invoice with VAT, may we issue it, and in which month does that VAT belong?

**What ParadisERP does today:** An invoice is always raised from an existing sale or rental note. It copies the sale's choice: an untaxed sale can only produce an untaxed invoice. The invoice itself never posts to the books; the sale already did. So there is no way today to turn an untaxed sale into a taxed one.

**Why I'm asking:** Contractors often need a VAT invoice to recover the tax. If the rule is "the sale decides", the cashier's tick at the counter is final. If a later taxed invoice is allowed, the system has to add the VAT to 4457 and to what the customer owes.

**Example:** Monday a contractor pays 100,000 DA cash, untaxed. Thursday he asks for an invoice with VAT. Either he pays 19,000 DA more and gets a taxed invoice dated Thursday, or we refuse.

**Possible answers:**
- A. Not allowed: the sale's tax choice is final.
- B. Allowed: the invoice adds the VAT (19,000 DA) in the month the invoice is issued, and the customer owes it.
- C. Allowed only by cancelling (refunding) the untaxed sale and selling again with VAT.

**Our suggestion:** C keeps one rule ("the sale decides the tax") and leaves a clean trail. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### TAX-03 — Prices before tax, and the three rates

**Question:** Please confirm the tax set-up: prices stored before tax, VAT added on top, and the rates 19 % (standard), 9 % (reduced) and 0 % (exempt). Which of our products or services are at 9 % or exempt?

**What ParadisERP does today:** The Algeria preset stores prices before tax, offers 19 %, 9 % and 0 %, and defaults new products to 19 %. Each product carries one rate. The rates are a company setting, not fixed in code. Whether the live company still has exactly these settings: Needs verification.

**Why I'm asking:** A wrong rate on a product is wrong on every sale of it. Until 2026-09-25 the system treated prices as tax-included by mistake, so we want the accountant to confirm the setting as it is now.

**Example:** A scaffold coupler priced 1,000 DA: taxed sale = 1,000 + 190 = 1,190 DA. If it should be at 9 %: 1,090 DA.

**Possible answers:**
- A. Confirmed: before tax, 19 / 9 / 0, and here is the list of 9 % and exempt items.
- B. Some prices should include tax (for example retail counter prices).
- C. Other rates apply.

**Our suggestion:** A, with a short list of any product that is not at 19 %. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### TAX-04 — VAT on the invoice total and rounding

**Question:** Is it correct to calculate VAT once on the total of each rate (and round to the centime), rather than on each line?

**What ParadisERP does today:** On the till, invoices, credit notes and rental notes, VAT is worked out on the total of each rate, rounded to two decimals (half up), then shared back over the lines so the lines still add up. The books keep three decimals.

**Why I'm asking:** Line-by-line rounding and total rounding can differ by a few centimes. The tax office and the customer must see the same figure.

**Example:** Three lines of 333.33 DA at 19 %: on the total 999.99 × 19 % = 190.00 DA; line by line 63.33 × 3 = 189.99 DA.

**Possible answers:**
- A. Per rate on the total (today).
- B. Line by line.

**Our suggestion:** A (today's rule). **(To be confirmed by the accountant.)**

**Priority:** Later

**Accountant's answer:**

> 

---

### TAX-05 — Which purchase VAT can we deduct, and what do we need to keep?

*This question also answers: PUR-03 (Must purchase TVA be recorded in a deductible-TVA account?); MNT-03 (Outside repair bills with TVA: separate deductible TVA? (today all into 615)).*

**Status:** Pending Accountant (purchase VAT recording).

**Owner's note (31 Oct 2026):** When tax is switched on, ParadisERP calculates it on the total. This question is only about the VAT we pay to suppliers.

**Question:** Which VAT on our purchases and expenses can we deduct (goods, fuel, repairs, transport …), which account should it go to, and what details from the supplier's invoice must we record to deduct it?

**What ParadisERP does today:** Goods received are recorded at their before-tax value, and expense vouchers at the full amount paid. The VAT on purchases is not recorded separately yet; we will add it from your answer.

**Example:** Supplier invoice: goods 200,000 DA + VAT 38,000 DA = 238,000 DA. We would record stock 200,000 DA, deductible VAT 38,000 DA, supplier owed 238,000 DA.

**Possible answers:**
- A. All purchase VAT on a proper invoice is deductible, on account 4456 (or the account you name).
- B. Some expenses are not deductible (please list, e.g. fuel or vehicles).

**Our suggestion:** A, with the exceptions you name. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### TAX-06 — The monthly G50: which lines apply to us?

**Status:** Internal Technical Verification (we will check our reports against your answer).

**Owner's note (31 Oct 2026):** ParadisERP has a full reporting system. We want to be sure it covers every figure the G50 needs.

**Question:** Which lines of the monthly G50 do we fill, and from which figures? A copy of a recent G50 you prepared for us (or a blank one) would be enough.

**Why I'm asking:** So we can check our reports line by line against the declaration.

**Example:** —

**Possible answers:** A copy of the G50 with the lines we use marked.

**Our suggestion:** — **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### TAX-07 — One VAT declaration for the company, or one per branch?

**Question:** The owner says each branch has its own trade register and tax number. Does each branch file its own VAT declaration, or does the company file one?

**What ParadisERP does today:** Every entry carries its branch, so VAT can be shown per branch or for the company. Each branch's own identifiers print on its papers (owner, 2026-10-20). No declaration exists yet (TAX-06).

**Why I'm asking:** It decides how the VAT report is grouped and whose tax number appears on it.

**Example:** Skikda collects 300,000 DA of VAT in October and Constantine 150,000 DA: one G50 for 450,000 DA, or two?

**Possible answers:**
- A. One declaration for the company.
- B. One per branch.

**Our suggestion:** Whichever the tax office requires; the report can do both. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### TAX-08 — How invoices are numbered

**Question:** Must invoice numbers be continuous per year, per branch, or is one continuous number for the whole company acceptable?

**What ParadisERP does today:** Invoices are numbered INV-000001, INV-000002 … for the whole company, all branches together, never restarting. Credit notes (CRN-), receipts (RCV-) and payment vouchers (PV-) work the same way. Till receipts have their own number per till.

**Why I'm asking:** If each branch has its own tax number, the tax office may expect each branch's invoices to run without gaps. Changing numbering later is harder once papers are out.

**Example:** Skikda issues INV-000101 and INV-000103; Douera issued INV-000102 in between. Skikda's invoices look like they have a gap.

**Possible answers:**
- A. One company-wide sequence (today).
- B. One sequence per branch (for example SKD-2026-0001).
- C. Per branch and restarting each year.

**Our suggestion:** Your choice; B or C if each branch files its own VAT. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### TAX-09 — Stamp duty: please confirm the rule

*This question also answers: TRS-12 (Stamp duty on cash payments (not built)).*

**Status:** Internal Technical Verification (we will check the setting and its posting).

**Owner's note (31 Oct 2026):** Stamp duty is set up in ParadisERP. We want to check that it matches the rule.

**Question:** Please confirm the stamp-duty rule for our cash payments: which documents, the rate, any minimum and maximum, and the account it goes to.

**Example:** An invoice of 238,000 DA paid in cash: how much stamp duty, and on which account?

**Possible answers:** The rule as you apply it.

**Our suggestion:** — **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### TAX-10 — Other taxes: tax regime, TAP, IBS, IFU

**Question:** Which tax regime is the company under, and which other taxes (on turnover or on profit) should the system prepare figures or accounts for?

**What ParadisERP does today:** Not built. There is nothing for a tax on turnover (TAP), the company profit tax (IBS), the flat-rate tax (IFU) or their accounts. Payroll taxes are handled by payroll (see the payroll section — payroll taxes).

**Why I'm asking:** We understand TAP may have changed in recent finance laws, but we do not want to guess. If a turnover tax applies, the monthly report should show its base.

**Example:** October turnover of 4,500,000 DA: does any tax apply to it besides VAT?

**Possible answers:**
- A. Real regime; only VAT monthly; IBS once a year from the accountant's own calculation.
- B. A turnover tax also applies (please give its base and account).
- C. Flat-rate regime (IFU).

**Our suggestion:** Tell us the regime and the list of taxes; the system only needs to supply the bases. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### D45 + REN-03 — When exactly is VAT due on advances and on hire payments?

*REN-03 also answers: TAX-12 (Rental VAT due at invoicing (issue) or at payment?).*

**Status:** Owner Workflow Confirmed — Compliance Pending (legal timing to confirm).

**Owner's way of working (31 Oct 2026):**
- **D45 — sales and advances:** VAT is calculated when the invoice is created. An advance received before the invoice carries no VAT.
- **REN-03 — hire:** VAT is recorded when the customer pays and when the invoice is created. **Changed by the owner on 10 Nov 2026: when invoiced only** (the issue note), which is what the program already does.

**Question:** Is this timing right under the law? In particular:
1. When a customer pays an advance before we deliver or start a hire, is VAT due on the advance when we receive it?
2. When a hire customer pays only part of the amount, is VAT due on the part paid, or on the whole invoice?
3. Should advances sit in their own account (419, customer advances) until the invoice, or stay on the customer's account (411)?

**What ParadisERP does today:** Sales: VAT is recorded with the sale or invoice; advances carry no VAT and stay on the customer's account (411). Hire: VAT is recorded for the whole period when the equipment goes out. Moving hire VAT to payment or invoice, as the owner wants, will be a change on our side once you confirm the rule.

**Example:** A contractor pays 500,000 DA on 20 October for a hire starting 5 November, invoiced on 5 November, and pays the rest in December. Which month carries the VAT: October (79,832 DA = 500,000 × 19/119), November, or December for the second payment?

**Possible answers:**
- A. VAT at the invoice for both goods and hire; advances carry none.
- B. VAT at payment for hire (in proportion to each payment); at the invoice for goods.
- C. VAT on every advance when received, then the balance at the invoice.

**Our suggestion:** Please tell us the rule; we will make the timing a setting rather than fixed in the program. **(To be confirmed by the accountant.)**

**Built as a setting for advances (9 Nov 2026, M7b):** Finance › Settings › Customer advances › VAT on advances: at the invoice (today, the default) or when the advance is received (answer C for the advance part; only with advances held on 419). Hire VAT timing (REN-03, question 2): owner, 10 Nov 2026, at the invoice (the issue note), as today; no change.

**Priority:** Critical

**Accountant's answer:**

> 

---

### TAX-14 — What must appear on our invoices

**Question:** Which details are required on a taxed invoice, for the seller and for a business buyer?

**What ParadisERP does today:** Each paper prints the issuing branch's name, address, phone and NIF, NIS, RC and AI (falling back to the main branch, then the company). The invoice prints the buyer's name, address and tax number as they were when it was issued, the lines, VAT per rate, totals, payment mode and the amount in words. None of the fiscal identifiers is marked "required"; an invoice can be issued to a business customer with no NIF.

**Why I'm asking:** A taxed invoice missing a required detail may be refused by the buyer's tax office, and the buyer cannot recover the VAT.

**Example:** A contractor with no NIF on file receives a taxed invoice of 238,000 DA that shows no buyer NIF.

**Possible answers:**
- A. Seller NIF, NIS, RC, AI required; buyer NIF, RC (and others?) required on a taxed invoice to a business.
- B. Today's papers are enough.

**Our suggestion:** A: the system warns (not blocks) when a taxed invoice's buyer has no NIF. Please list the required mentions. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### TAX-15 — Electronic invoicing

**Question:** Is there any electronic invoicing or electronic reporting to the tax office that applies, or will soon apply, to the company?

**What ParadisERP does today:** Not built. An integration point for e-invoicing exists for the platform operator, but no Algerian format is implemented.

**Why I'm asking:** If it is coming, numbering and invoice data should be ready for it.

**Example:** —

**Possible answers:**
- A. Nothing applies today.
- B. Yes: name of the system and the date it applies.

**Our suggestion:** Just tell us what you know. **(To be confirmed by the accountant.)**

**Priority:** Later

**Accountant's answer:**

> 

---

### C-18 — Overall conformity with the Algerian rules

**Question:** Once you have answered the stock, manufacturing and tax questions, do the chosen valuation method, the accounts, the presentation of charges by nature, the stock variation, the tax on advances (D45) and the year-end count meet the SCF and the tax rules as you apply them?

**What ParadisERP does today:** Nothing is asserted. This is the closing check of the manufacturing costing sign-off; nothing new posts until every row is signed.

**Why I'm asking:** We do not make regulatory conclusions ourselves; we need your confirmation that the whole set fits together.

**Example:** —

**Possible answers:**
- A. Confirmed.
- B. Confirmed with corrections (please list).

**Our suggestion:** Answer after C-01 to C-17, D45, TAX-05, TAX-06 and CLO-08. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

## 11. General Accounting and Journal Entries

### GL-02 — Accounts that do not exist yet

**Question:** Which accounts must we add for things the business does that have no account today?

**What ParadisERP does today:** The chart has no capital or reserves (class 10/11), no result account (12), no loans (16), no fixed assets or depreciation (class 2 / 28), no deductible VAT (4456), no VAT payable (445x), no bank charges (627), no taxes and duties (64), no financial income or charges (66/76). The admin can add accounts, but nothing posts to an added account (see GL-03).

**Why I'm asking:** A balance sheet without capital, fixed assets or loans is not the company's real balance sheet.

**Example:** The company's capital of 1,000,000 DA, a lorry worth 4,000,000 DA and a bank loan of 2,500,000 DA appear nowhere.

**Possible answers:**
- A. Add these accounts (list) and a way to post to them (GL-03).

**Our suggestion:** ParadisERP keeps the official books (GL-01, owner's decision), so these accounts will be needed. Please list them with their numbers, starting with deductible VAT (TAX-05). **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### GL-03 — Entries that have no document behind them

**Question:** How should we record things that are not a sale, a rental, a receipt or a voucher: bank charges, tax payments, capital, loans, depreciation, year-end adjustments, corrections?

**What ParadisERP does today:** There is no way to type a journal entry. This was a design choice: every entry must come from a document so every figure can be traced. The only correction is reversing a whole existing entry.

**Why I'm asking:** An accountant always needs a few hand entries (opening the year, depreciation, accruals, reclassifying a mistake). Without them, the books cannot be finished in the system.

**Example:** The bank takes 2,350 DA of fees in October. Today there is nowhere to record it, so the bank account in the books never matches the bank statement.

**Possible answers:**
- A. Allow hand entries ("OD" journal) for the accountant only, with a reason and an attachment, never on system accounts like 411/401/530 without a third party.
- B. Add specific documents instead (bank fee voucher, tax payment voucher, depreciation run).
- C. Keep it closed; adjustments are made in my own software.

**Our suggestion:** A, limited to an "accountant" permission, balanced, dated in an open month, and reversible. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### GL-06 — Journals and entry numbers

**Question:** Do you need separate journals (sales, purchases, cash, bank, miscellaneous) and a continuous number on every entry?

**What ParadisERP does today:** There is one journal. Each entry carries the kind of document that caused it (sale, receipt, voucher, payroll …) and the screen can filter by it, but there are no journal codes and no running entry number; entries are identified by an internal code and their document's number.

**Why I'm asking:** The legal books are normally a numbered chronological journal. ParadisERP keeps the official books (GL-01, owner's decision), so we expect this is required.

**Example:** Your software expects "VT" for sales, "AC" for purchases, "CA" for cash, "BQ" for bank, "OD" for miscellaneous.

**Possible answers:**
- A. Add journal codes by document type and a continuous number per journal and year.
- B. Journal codes only (for the export).
- C. Not needed.

**Our suggestion:** A; the mapping from document type to journal is a small table you approve. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### GL-07 — The trial balance shows only the period's movements

**Question:** Should the trial balance show opening balance, movements and closing balance for each account?

**What ParadisERP does today:** The trial balance adds up the debits and credits of the chosen period only. A balance sheet account such as cash or customers shows only what moved in the month, not what it holds. The balance sheet and the account statement do show full balances.

**Why I'm asking:** A trial balance used to check the books normally shows the balances.

**Example:** Cash (530) held 800,000 DA on 1 October and took in 300,000 DA net in October. The October trial balance shows 300,000 DA, not 1,100,000 DA.

**Possible answers:**
- A. Six columns: opening debit/credit, period debit/credit, closing debit/credit.
- B. Period only (today).

**Our suggestion:** A.

**Priority:** Important

**Accountant's answer:**

> 

---

### GL-08 — Format of the financial statements

**Question:** Do you need the balance sheet and profit and loss in the official SCF layout, or are simple lists by account enough?

**What ParadisERP does today:** Both reports are lists by account (assets, liabilities, equity; income, expenses), per branch or for the group. The balance sheet adds "result" as income minus expenses since the very first entry, because there is no year-end closing (CLO-08). There is no cash-flow statement and no notes.

**Why I'm asking:** For the yearly filing the statements must be in the official layout. If you prepare them in your own software, simple lists are enough.

**Example:** —

**Possible answers:**
- A. Official SCF layout from ParadisERP.
- B. Simple lists; I prepare the official statements.

**Our suggestion:** A in time, since ParadisERP keeps the official books (GL-01, owner's decision); B until it is built. **(To be confirmed by the accountant.)**

**Priority:** Later

**Accountant's answer:**

> 

---

### GL-09 — Suspense account (471): who clears it, and how?

**Question:** When something lands in suspense (471), who investigates it and how should it be cleared?

**What ParadisERP does today:** The system sends to 471 anything it cannot place without blocking a sale: an unpaid amount on a sale with no customer, a cash rounding larger than the rounding step, a rental issued to a contract with no customer, and the part of a payslip's deductions that exceeds the pay (net held at zero). There is no document to move an amount out of 471 (no hand entries, GL-03).

**Why I'm asking:** 471 should be empty at month end. Today it can only grow.

**Example:** A cashier records a 15,000 DA sale as paid 12,000 DA with no customer: 3,000 DA goes to 471. Once you find it was a shortage in the cash box, there is no way to move it to a loss.

**Possible answers:**
- A. The accountant clears 471 monthly with a correcting entry (needs GL-03 A).
- B. A specific "clear suspense" document with reason and target account.

**Our suggestion:** B, so every clearing has a reason the owner can read.

**Priority:** Important

**Accountant's answer:**

> 

---

### GL-10 — Opening balances when we started

*This question also answers: PUR-11 (Supplier opening balances against 890 (owner decided 2026-10-10)).*

**Question:** What should the opening entry contain, and which account should balance it?

**What ParadisERP does today:** Stock, customer balances and supplier balances at go-live are posted against 890 (opening balance). There is no way to enter the opening cash in boxes, bank balances, fixed assets, capital, loans, VAT owed or payroll owed. The stock opening is dated the day it was keyed.

**Why I'm asking:** Without the full opening position, the balance sheet will not match the last accounts you prepared, and 890 holds a mixed figure.

**Example:** At go-live: stock 18,000,000 DA, customers 6,500,000 DA, suppliers 3,200,000 DA are in; bank 2,100,000 DA and cash 450,000 DA are not.

**Possible answers:**
- A. Enter the full balance sheet at the go-live date from your last closing, against capital/reserves accounts you name.
- B. Keep 890 as a temporary account and you split it later.

**Our suggestion:** A, as one dated opening document you approve. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### GL-11 — Branch accounts and the inter-branch account (181)

**Question:** Is it acceptable that each branch has its own profit and loss and balance sheet, with money and goods moved between branches shown through account 181 only on a single branch's sheet?

**What ParadisERP does today:** Every line carries a branch. For the whole company, moves between branches (cash remittances, stock transfers) cancel out. For one branch, the difference is shown as 181 (calculated, never posted).

**Why I'm asking:** It decides how branch results are read and whether 181 should be a real posted account.

**Example:** Douera sends 1,000,000 DA cash to the main safe: Douera's sheet shows 181 +1,000,000 DA; the company sheet shows nothing.

**Possible answers:**
- A. Acceptable as is.
- B. Post 181 for real on every inter-branch move.

**Our suggestion:** A.

**Priority:** Later

**Accountant's answer:**

> 

---

## 12. Month-End and Year-End Closing

### CLO-01 — Closing a month after review

**Status:** Approved by Owner — built on 1 Nov 2026 (not yet live). No answer needed; tell us if you see a problem.

**Owner's decision (31 Oct 2026):** A month is closed after it has been reviewed. Nothing can then be posted with a date in a closed month. A mistake found later is fixed by a controlled correction that keeps a full record of who changed what and why.

**What ParadisERP does now (1 Nov 2026):** the Admin closes the books through the end of a month that has ended; from then on nothing dated in that month or before can be posted, and the document is refused with a message. To correct a closed month the Admin reopens it, with a reason, corrects, and closes it again; every close and reopen is kept with who, when and why. A sale from a till that was offline is never refused: it keeps its date, and its accounting entry is dated the first open day (owner, 1 Nov 2026 — please confirm under SAL-14). How a correction to a closed month should be dated is question COR-E01.

**Example:** You declare October's VAT on 15 November and October is closed. On 20 November someone tries to record a credit note dated 25 October: the system refuses the date, and the correction goes through the controlled route instead.

**Priority:** Critical

**Accountant's comment (optional):**

> 

---

### CLO-03 — Back-dated documents

*This question also answers: COR-10 (Back-dating credit notes, invoices, receipts).*

**Question:** How far back may a user date a receipt, credit note, wage payment, advance or opening balance?

**What ParadisERP does today:** These documents accept any date the user types and post on it, with no limit: receipts, hand-made credit notes, invoices (which post nothing themselves), wage payments, advances, customer and supplier opening balances.

**Why I'm asking:** A back-dated document can change a month already reviewed, and a typing mistake (2025 instead of 2026) moves money into the wrong year.

**Example:** A cashier records a 50,000 DA receipt dated 12/09/2025 instead of 12/10/2026: it lands a year back.

**Possible answers:**
- A. Only dates in an open month (CLO-01); otherwise refused with a clear message.
- B. Any date, but a warning when it falls in a closed month.
- C. Today's date only, except for the accountant.

**Our suggestion:** A. (Receipts are not sales, so refusing a wrong date does not break "never block the sale".)

**Priority:** Important

**Accountant's answer:**

> 

---

### CLO-04 — Which date an entry carries: the document's or the day it was keyed?

*This question also answers: REN-15 (Backdated notes: entry dated movement date or typing date? (today: typing date)); PUR-13 (Which date does a purchase carry?).*

**Question:** When a document is entered a few days after it happened, which date should its accounting entry carry?

**What ParadisERP does today:** It depends on the document. Sales, receipts, credit notes and payroll use the document's date. Rental issue and return notes, receiving notes, stock adjustments and stock transfers use the moment they are keyed, even when the rental note itself carries an earlier date; a payment voucher uses the moment it is marked paid.

**Why I'm asking:** At a month end this moves revenue and VAT into the wrong month.

**Example:** A rental issue note for 30 September (rent 200,000 DA + 38,000 DA VAT) is keyed on 2 October with the date 30/09. The note says September; the books and the VAT say October.

**Possible answers:**
- A. Always the document's date (inside an open month).
- B. Always the day it is keyed.
- C. The document's date, but never earlier than the first open day.

**Our suggestion:** C: consistent with CLO-01 and SAL-14.

**Priority:** Important

**Accountant's answer:**

> 

---

### CLO-05 — Month boundaries in Algeria time

**Question:** Please confirm that a month runs from 00:00 on the 1st to 24:00 on the last day, Algeria time, for every report and declaration.

**What ParadisERP does today:** Entries are stored in universal time. The accounting screen builds the month from the computer's own clock and time zone, not the company's; the server, when no dates are sent, uses universal time. Algeria is one hour ahead of universal time.

**Why I'm asking:** A sale at 00:30 on 1 November in Algeria is 23:30 on 31 October in universal time. On a computer set to another time zone, it can fall into October's report.

**Example:** A 60,000 DA sale at 00:30 on 1 November shows in October on a laptop set to universal time.

**Possible answers:**
- A. Algeria time always (our fix: reports use the company's time zone, never the computer's).
- B. Something else.

**Our suggestion:** A.

**Priority:** Important

**Accountant's answer:**

> 

---

### CLO-06 — End of day at the till (Z-report and cash count)

*This question also answers: TRS-09 (Cash count over/short at closing (not built)).*

**Question:** What should happen at the end of each day at each till: a closing report, a cash count, a signature?

**What ParadisERP does today:** There is no day close. The "Z-report" is a back-office summary for any date range (sales, refunds, tax by rate, rounding, gift cards). Nothing locks the day, and there is no screen to record a counted cash difference in a box (the system can record one, but no screen uses it).

**Why I'm asking:** A daily count is where cash shortages are found. Without it, a missing 5,000 DA is noticed only at month end, if at all.

**Example:** Expected cash in the Skikda box: 182,300 DA; counted: 177,300 DA. Where does the 5,000 DA difference go?

**Possible answers:**
- A. A daily close per box: count, difference posted to a shortage/overage account you name, the Z-report printed and kept.
- B. Weekly count.
- C. Not needed.

**Our suggestion:** A, without ever blocking the next day's sales. (Cash procedures: see the payroll and treasury section — cash boxes.) **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---

### CLO-07 — Month-end checklist

**Question:** What must be done and checked every month before you consider the month closed?

**What ParadisERP does today:** Not built as a checklist. Possible checks the system could show: 471 suspense is zero, cheques past due, payroll for the month approved, cash boxes counted, every till synced, 4457 matches the VAT report, customer and supplier balances match the ledger.

**Why I'm asking:** A short, fixed list lets the owner see when a month is ready for you.

**Example:** "October: 2 tills not synced since 30/10; suspense 3,000 DA; payroll Douera not approved."

**Possible answers:**
- A. This list (add or remove items).
- B. Your own list.

**Our suggestion:** A, shown before the lock in CLO-01.

**Priority:** Later

**Accountant's answer:**

> 

---

### CLO-08 — Closing the year and carrying the result forward

**Question:** What is the company's fiscal year, and how should the year's result be closed and carried into the next year?

**What ParadisERP does today:** Not built. There is no fiscal year, no closing entry and no retained-earnings account. The balance sheet calculates "result" as all income minus all expenses since the very first entry, so in the second year it shows two years' results as one figure. Reports are limited to 400 days per request.

**Why I'm asking:** From 1 January 2027 the 2026 result must stop being "this year's result" and become last year's (to reserves, retained earnings or dividends as decided).

**Example:** 2026 profit 3,200,000 DA. In March 2027 the balance sheet should show 2026's 3,200,000 DA under retained earnings and only 2027's profit as the year's result. Today it shows one combined figure.

**Possible answers:**
- A. Calendar year; a year-end closing that moves the result to an account you name (for example 12 → 11), after you approve.
- B. Another fiscal year (dates: …).

**Our suggestion:** A, plus a lock on the closed year (CLO-01). **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

## 13. Corrections, Refunds and Exceptional Cases

### COR-01 — Returned goods: back into stock at which cost? (P0.4c)

**Question:** When a customer returns goods, should they go back into stock at the cost recorded when they were sold, or at today's cost?

**What ParadisERP does today:** A refund puts the goods back (Dr **310** stock / Cr **600** cost of goods sold) at the product's **current** cost price, which is the last purchase price on the day the refund reaches the server. The cost recorded at the sale is not stored per sale line, so it cannot be used. This is item P0.4c on the sign-off tab ("refunds reverse the cost the sale posted"); it needs the cost stored on each sale line (P0.4a).

**Why I'm asking:** If prices moved between the sale and the return, the return creates or removes stock value that never existed.

**Example:** We sold a pump in September when its cost was 8,000 DA (Dr 600 / Cr 310 8,000 DA). In October we buy pumps at 9,500 DA. The customer returns it: today we post Dr 310 / Cr 600 9,500 DA, so the stock rises by 1,500 DA more than it fell, and cost of goods sold falls by 1,500 DA too much.

**Possible answers:**
- A. Use the cost recorded on the original sale.
- B. Use today's cost (as today).
- C. Use the average cost on the return day.

**Our suggestion:** A (P0.4c as proposed).

**Priority:** Critical

**Accountant's answer:**

> 

---

### COR-02 — Return of goods bought last month

**Question:** When goods sold in one month come back in a later month, should the return reduce this month's sales, tax and cost, or should we correct the original month?

**What ParadisERP does today:** The refund is its own document dated the day of the return. It reduces sales (700), tax collected (4457) and cost (600) in the month of the return; the original month is not touched. A refund at the till must name the original sale, cannot exceed what is left of it, and must be made at the same branch. In practice the till can only refund sales it holds itself (the last 500 sales recorded on that computer), and there is no back-office refund screen.

**Why I'm asking:** The tax effect of a return made in a later month must follow your rule (see TAX — returns and credit notes), and the till limit means some returns cannot be entered at all.

**Example:** A customer bought tiles for 238,000 DA (200,000 DA + 19% tax) on 20 September and returns half on 5 October. Today October shows −100,000 DA sales and −19,000 DA tax; September stays at 200,000 DA / 38,000 DA.

**Possible answers:**
- A. Book the return in the month it happens (as today).
- B. Correct the original month.

**Our suggestion:** A, and add a back-office return screen so any earlier sale can be returned. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### COR-03 — Goods returned at a different branch

**Question:** If a customer returns goods at a branch other than the one that sold them, which branch's sales and stock should change?

**What ParadisERP does today:** The till refuses it ("a sale is refunded at the branch that made it; another branch goes through the back office"), but the back office has no refund screen, so such a return cannot be recorded with its stock movement today.

**Why I'm asking:** Customers do this; without a rule, staff will give money back from the drawer with no record.

**Example:** Sold at Blida for 30,000 DA, returned at Algiers a week later; Algiers pays 30,000 DA cash back from its drawer.

**Possible answers:**
- A. Reduce the selling branch's sales; the goods and the cash paid out are at the receiving branch, balanced between the two branches through 181.
- B. Treat it entirely as the receiving branch's return.
- C. Refuse; the customer goes back to the selling branch.

**Our suggestion:** A.

**Priority:** Important

**Accountant's answer:**

> 

---

### COR-04 — Must a refund go back the way the customer paid?

**Question:** Should a refund always go back by the same means the customer used to pay (cash for cash, debt reduction for a sale on account, card for card)?

**What ParadisERP does today:** The cashier picks the refund method. The server checks only that the amount refunded "to the account" is not more than what was put on the account, and that all refunds together do not exceed the sale. It does **not** stop a cash refund for a sale that was put on account or paid by a cheque not yet cleared.

**Why I'm asking:** A cash refund of a sale on account takes cash out of the drawer while the customer still owes the money; a cash refund of an unpaid cheque pays out money that never arrived.

**Example:** A customer takes 40,000 DA of goods on account, returns them the next day, and the cashier refunds 40,000 DA in cash. The drawer is 40,000 DA short and the customer still owes 40,000 DA.

**Possible answers:**
- A. Refund by the original means; cash only up to the cash actually received for the sale.
- B. Allow any means with a manager's approval and a written reason.
- C. Allow any means (as today).

**Our suggestion:** A, with B as the exception.

**Priority:** Critical

**Accountant's answer:**

> 

---

### COR-05 — Partial returns, discounts and rounding

**Question:** When only part of a ticket is returned, is it right to give back the returned lines' share of the discount and not the cash rounding?

**What ParadisERP does today:** A refund line gives back its share of the line's price, discount and tax in proportion to the quantity returned. A whole-ticket discount was already spread over the lines, so it is given back pro rata too. The cash rounding of the original sale is not given back. The server accepts several partial refunds of one sale; the till screen allows only **one** refund per sale.

**Why I'm asking:** We want the refund paper and the books to agree with what the customer actually paid.

**Example:** Ticket of 3 items, 30,000 DA, with a 3,000 DA ticket discount (27,000 DA paid). The customer returns one 10,000 DA item: he gets 9,000 DA back.

**Possible answers:**
- A. Pro rata as today.
- B. Give back the full price of the returned item and keep the discount only on what stays.

**Our suggestion:** A, and allow a second partial return of the same sale at the till.

**Priority:** Later

**Accountant's answer:**

> 

---

### COR-06 — Is a credit note required for every refund?

*This question also answers: TAX-13 (VAT reduced in refund month; is a credit note always required?).*

**Question:** For tax purposes, is the till's refund receipt enough to reduce our sales tax, or must we issue a credit note (avoir) for every refund?

**What ParadisERP does today:** By the owner's decision of 2026-10-10 (rules 1–4), a credit note is created automatically only when the refunded sale had already been invoiced; it posts nothing (the refund already did). A refund of a sale that was never invoiced reduces sales and tax with only the refund receipt as paper. The automatic credit note is made after the sale is saved, in a separate step; if it fails, only a log line records it.

**Why I'm asking:** If the tax office requires a credit note for the tax reduction, our uninvoiced refunds reduce tax without the right paper.

**Example:** A till sale of 11,900 DA (10,000 DA + 1,900 DA tax), not invoiced, is refunded. Tax collected falls by 1,900 DA with only the refund receipt.

**Possible answers:**
- A. The refund receipt is enough for an uninvoiced till sale.
- B. Every refund needs a numbered credit note.

**Our suggestion:** A for tickets to persons, B for sales to companies. The owner decided rules 1–4 on 2026-10-10; please confirm they are acceptable. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### COR-07 — What is a back-office credit note for?

**Question:** Should a credit note made by hand be used only for price corrections and billing mistakes, with returned goods always going through a refund that puts them back in stock?

**What ParadisERP does today:** A hand-made credit note against an invoice posts Dr 700 (or the line's income account) and Dr 4457 / Cr 411. It works by quantity (part of a line's quantity), not by amount. It does **not** put goods back in stock and does **not** reverse the cost. If the same sale is later refunded at the till, the refund posts again, so the income and tax are given back twice (the till refund does not check credit notes).

**Why I'm asking:** Staff may use a credit note for a return; stock and cost would then be wrong, and a later till refund would double it.

**Example:** Invoice of 100,000 DA + 19,000 DA tax. A credit note for half: sales −50,000 DA, tax −9,500 DA, but the goods do not come back into stock. Next day the till refunds the same half: sales and tax fall again.

**Possible answers:**
- A. Credit notes for price and billing corrections only; goods come back only through a refund; a refund is blocked for what a credit note already gave back.
- B. Allow credit notes for returns too, and make them put goods back and reverse the cost.

**Our suggestion:** A, and add an "amount-only" credit note for a price reduction.

**Priority:** Critical

**Accountant's answer:**

> 

---

### COR-08 — Returned goods that are damaged

**Question:** When returned goods cannot be sold again, how should they be recorded?

**What ParadisERP does today:** Every refund puts the full quantity back into the branch's sellable stock. There is no choice at the return to send it to scrap or to a loss. Moving it afterwards is possible through stock states (to scrap 318 or written off to 657).

**Why I'm asking:** Sellable stock and its value are overstated until someone remembers to move the item.

**Example:** A customer returns a broken mixer sold for 25,000 DA (cost 18,000 DA). It is back in stock at 18,000 DA though it is worth nothing.

**Possible answers:**
- A. Back to stock, then moved by a separate stock document (as today).
- B. At the return, the cashier chooses "sellable", "scrap" or "loss", and the entry follows.

**Our suggestion:** B.

**Priority:** Important

**Accountant's answer:**

> 

---

### COR-09 — Correcting a cashier's mistake on a sale

**Question:** When a cashier makes a mistake (wrong price, wrong customer), is "refund and sell again" an acceptable correction, or do you need a cancellation that keeps the same number?

**What ParadisERP does today:** A completed sale can never be edited, deleted or voided. The only correction is a refund (a new numbered document) followed by a new sale. A refund must go to the same customer as the sale, so moving a sale to another customer is also refund-and-resell.

**Why I'm asking:** Both documents stay in the books and in the daily report; we want to know this trail is acceptable.

**Example:** A sale of 15,000 DA is put on customer X's account instead of customer Y's. The cashier refunds it to X and sells again to Y.

**Possible answers:**
- A. Refund and resell (as today).
- B. A "cancel" status on the same number, allowed only on the same day with a manager.

**Our suggestion:** A.

**Priority:** Later

**Accountant's answer:**

> 

---

### COR-E01 — Correcting a month that is already closed

**Question:** When we find a mistake in a month that is closed and declared, should the correction go into the current month, or should the old month be reopened and changed?

**What ParadisERP does today:** No month is ever closed (CLO-01). When something is put right, the system adds a new entry dated today and leaves the old one as it was. Two such corrections have already been made this way: the cash short by the change given (cash and suspense, one entry per branch) and the daily rental pricing correction (rent, VAT and customer balances of past contracts), both dated the day they ran.

**Why I'm asking:** Correcting in the current month keeps declared months unchanged, but the VAT of the old month may need its own correction on the declaration.

**Example:** In November we find that a rental return of 12 October charged 30,000 DA of overdue days twice. The correction (−30,000 DA rent, −5,700 DA VAT) is dated in November.

**Possible answers:**
- A. Always in the current month, with a reference to the old document and month; the VAT adjustment goes on the current declaration.
- B. Reopen the old month, correct it, and file a corrected declaration.
- C. A, except above an amount you set, where you decide.

**Our suggestion:** A (it is how the system already behaves), with each correction's original month shown in the VAT report. **(To be confirmed by the accountant.)**

**Priority:** Critical

**Accountant's answer:**

> 

---

### COR-E02 — Reversing one journal entry on its own

**Question:** Should anyone be able to reverse a single journal entry directly, or only by cancelling or correcting the document behind it?

**What ParadisERP does today:** A user with the "accounting manage" permission can reverse any entry through the server, with a reason; there is no screen for it yet. The reversal is dated today and is a mirror image. It does not touch the document: a reversed sale stays a sale, a reversed receipt stays confirmed, the customer's balance and the stock do not move. Once reversed, the same document can never be posted again.

**Why I'm asking:** After such a reversal the books and the customer's balance disagree, and there is no way to post the corrected version.

**Example:** A receipt of 80,000 DA is reversed in the books. The customer's statement in the books says he owes 80,000 DA more; his account card still says he paid.

**Possible answers:**
- A. Remove direct reversal; corrections only through the document (cancel a receipt, refund a sale, credit note …).
- B. Keep it for the accountant only, and allow the corrected entry to be posted afterwards.

**Our suggestion:** A for documents that have their own cancel/refund; B only for entries with no document (once GL-03 exists).

**Priority:** Important

**Accountant's answer:**

> 

---

### COR-E03 — A correction that changes VAT already declared

**Question:** When a correction changes the VAT of a month already declared, how do you want to see it so you can report it?

**What ParadisERP does today:** A correction simply adds to or takes from 4457 on its own date (see COR-E01). There is no list of "VAT corrections relating to earlier months". The daily rental pricing correction changed the VAT on past rentals in this way.

**Why I'm asking:** You need to know which part of a month's VAT relates to earlier months.

**Example:** November's 4457 is 600,000 DA, of which −5,700 DA corrects October.

**Possible answers:**
- A. The VAT report shows a separate line "corrections of earlier months", with each document.
- B. Not needed.

**Our suggestion:** A. **(To be confirmed by the accountant.)**

**Priority:** Important

**Accountant's answer:**

> 

---
## 14. Final Decisions Needed Before Activating Manufacturing Accounting


This checklist comes from the costing plan and the sign-off
sheet. Each step is switched on separately, only
after the answers listed are signed. Signing a row allows a method; it does not post anything by itself.
Aliases: D9 = C-01, D43 = C-02 / C-16, D33 = C-04, D11 = C-06, D13 = C-10, D47 = C-14.

| Step | What it turns on | Needs these answers first |
|---|---|---|
| (a) Production stock operations on the live site | Production orders really move stock: materials leave the line's stock, finished goods enter it, and finished goods can be sold. Nothing posts until (d). | **C-12** first: it decides whether this step may come before (d). If C-12 = keep it off (recommended), this step waits for everything in (d) and is switched on together with live posting. If C-12 = interim typed cost: C-12, C-01, C-16 (finished goods sold before release), C-09 / AD-8 (scrap in stock), C-14 (transfers of finished goods), MFG-03 (parts made in-house). |
| (b) Phase B costs become financially final | A frozen cost sheet stops being "provisional" and counts as the order's cost for the accounts. | **AD-12** (what "final" means), C-01 (materials no longer at last purchase price), C-05, C-06 / D11, C-07, C-08, C-09 / AD-8, C-10 / AD-1 / D13, C-11 / AD-7, C-17 / AD-9, AD-2 / AD-3, AD-4, AD-5, AD-6, MFG-01, MFG-03, MFG-04. |
| (c) Phase C shadow journal | Simulated manufacturing entries in a separate journal, never the real ledger, reviewed with you for at least one month. Also needs the average-cost shadow (P0.4a/b) built. | Everything in (b), plus C-02 / D43, **C-03** (which model), **C-04 / D33** (account codes; our own team's, not the accountant's), C-13 / AD-10 / AD-11, C-14 / D47, C-15, C-16 / D43 — that is, every row C-01 to C-17 — plus MFG-02 and MFG-05. |
| (d) Phase D live posting | Manufacturing entries post to the real ledger from a cut-off date, plus one approved catch-up entry. production stock operations and manufacturing postings are switched on together. | Every row signed: everything in (c) plus **C-18** (Algerian rules overall) and **D45** (customer advances and the tax on them), and P0.2, P0.3, P0.4c and the service-income account (700 / 706). Also: the average-cost switch done (D9 / D43), one month of (c) reviewed with you, and the catch-up figure approved. |

## Appendix — What ParadisERP records in the books today

For reference while answering. "Now" means the entry is dated the moment it is typed, not the date on the document.

| Event | Posts? | Entry | Entry date |
|---|---|---|---|
| Till sale or refund | Yes | Cr 700 (total − tax), Cr 4457, 758 rounding (excess to 471); Dr 530 / 512 / 411 / 413 / 4181 / 4182 by tender; Dr 600 / Cr 310 at the product's cost price | The time of the sale on the till |
| Invoice raised over a sale or rental note | No | – | – |
| Invoice typed directly (closed for new invoices) | Yes | Dr 411 / Cr income / Cr 4457 | Date typed |
| Credit note made by hand | Yes | Reverses income, 4457 and 411 | Date typed |
| Automatic credit note | No | – | – | – |
| Confirmed receipt | Yes | Dr 530 or 512 / Cr 411 | Date typed | – |
| Cheque settlement | Yes | Dr 512 or 411 / Cr 413 | Now | – |
| Remittance | Yes | 530 → 530 between branches | Now | – |
| Payment voucher | Yes | Full amount, no VAT split | When marked paid |
| Rental issue | Yes | Dr 411 (or 471) / Cr 706 / Cr 4457 | Now, not the note's date |
| Rental return | Yes | 706 / 707 / 708 / 757, unused rent, ±4457, ±411 | Now | – |
| Receiving note | Yes | Dr 310 / Cr 401, no VAT | Now |
| Opening stock | Yes | Dr 310 / Cr 890 | Now | – |
| Customer and supplier opening balances | Yes | Against 890 | Chosen day | – |
| Stock adjustment, state change, transfer, loss | Yes | 310 / 318 / 657 | Now | – |
| Payroll approval | Yes | Accrual (net held at zero to 471) | Period end | – |
| Wage payment, advance, payment to CNAS or the tax office | Yes | – | Date typed | – |
| Gift card issued, loyalty points earned | **No** | – | – | – |
| Manufacturing | **No** | – | – | – |
| Cash count difference, opening float, cash deposited at the bank | **No** (no route) | – | – | – |
| Supplier invoice VAT, bank fees, tax payments, capital, loans, depreciation | **Not built** | – | – | – |