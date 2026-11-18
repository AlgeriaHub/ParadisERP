# Customer advances (M7b, 9 Nov 2026)

SAL-12 (411 or 419?), D45 (when is VAT due on an advance?) and MS-35 (advances on customer repair jobs) are built as
settings the accountant sets (owner, 2026-11-09: every accounting decision is a setting; the default is today's
behaviour; nothing waits for a signature). The answers in `accountant-questions.md` only choose a setting.

## The settings

Finance › Settings › **Customer advances** (`GET/PUT /api/receivables/settings`; changed under `accounting.manage`,
read by whoever takes receipts, keeps Finance settings or manages accounting). One row, `ReceivableSettings`.

| Setting | Options | Default |
|---|---|---|
| Customer advances are held | On the customer's account (411) · On 419 until the invoice | 411 (today) |
| VAT on advances | Due at the invoice · Due when the advance is received | At the invoice (today) |

- 419 is refused while account 419 (`LedgerAccounts.CustomerDeposits`, seeded in every chart) is missing or inactive
  (409 `accounts_not_mapped`).
- "When received" is refused unless advances are held on 419 (400): on 411 an advance is an ordinary credit with no
  place to keep its VAT apart.
- Back to 411 is refused while Maintenance › Settings takes advances (MS-35 = Taken; 409 `maintenance_advances_taken`).
  Advances already on 419 stay there and can still be applied: each receipt knows it was an advance.

## What posts

### Default (411, VAT at the invoice) — unchanged

A receipt allocated to nothing posts Dr 530/512 / Cr 411 (the customer's), exactly as before. The kind "Advance" is
refused, and so is a VAT rate or a maintenance job on a receipt. Nothing ever reaches 419. Pinned by
`CustomerAdvanceTests.By_default_…`.

### On 419

The receipt form offers the kind **Advance** (`ReceiptKind.Advance = 3`). An advance names no invoice, no contract and
no return note when taken (it may name a customer maintenance job, below).

| Event | Entry (source) | Lines (all with the customer) |
|---|---|---|
| Advance received (cash or a cleared cheque) | `customer-receipt` | Dr 530 / 512 · Cr 419 |
| Applied to an invoice (from the receipt page) | `customer-advance-application` | Dr 419 · Cr 411 |
| Released to the customer's account (no invoice) | `customer-advance-application` | Dr 419 · Cr 411 |

- A pending cheque taken as an advance posts nothing and cannot be applied until it is confirmed (§5.4 as for every
  receipt).
- Applying records a `ReceiptAllocation` on the advance, so the invoice reads paid by that amount (standings, ageing,
  rental closure) exactly as if the receipt had been allocated when taken. Each application is a
  `CustomerAdvanceApplication` row (append-only) whose id is the entry's source id. No amount = as much as the advance
  still holds, no more than the invoice owes. The customer's balance (`Customer.Balance`) does not move: the money was
  theirs on both accounts.
- Releasing moves part of an advance to 411 with no invoice: before a refund (a refund voucher debits 411, so its cap
  now leaves out what advances still hold on 419), or against a debt with no invoice behind it.
- The application is dated now; a maintenance job's advances are dated with the job's invoice.

### On 419 with VAT when received (D45)

The seller ticks the VAT on an advance only when the customer asks (VAT only on request, owner 2026-11-05); untaxed
is the default. Ticked, the rate is the country profile's default rate and is frozen on the receipt
(`AdvanceTaxRate`, `AdvanceTaxAmount`). The amount received is tax included, so the tax in it is
amount × rate / (100 + rate), rounded the company's way.

| Event | Lines (all with the customer) |
|---|---|
| Advance of 119,000 at 19 % received | Dr 530 119,000 · Cr 419 100,000 · Cr 4457 19,000 |
| Invoice 120,000 + 19 % (posts as always) | Dr 411 142,800 · Cr 700 120,000 · Cr 4457 22,800 |
| Advance applied to the invoice | Dr 419 100,000 · Dr 4457 19,000 · Cr 411 119,000 |

- Net 4457 = −19,000 − 22,800 + 19,000 = −22,800: the invoice's VAT, counted once. The 19,000 is declared in the
  month the advance arrived and given back in the month it is applied; the invoice's month carries the rest. Apply in
  the invoice's month for the declaration to read invoice VAT − advance VAT that month.
- A part applied gives back the advance's VAT in proportion (rounded); the last part gives back what is left, so the
  whole of it is given back exactly once. Releasing gives it back too (a refunded advance carries no VAT).
- The invoice's posting is not changed at all; the "not again at the invoice" part is the give-back on application.
- The 4457 lines of an advance carry the customer (`LedgerEntryLine.CustomerId`), so the customer's statement can read
  the whole sum the customer handed over.

## Statements and balances

The statement and the ageing read the customer's account as **411 + 419 + the 4457 lines of their advances**:

- an advance is one statement row, credit = the whole sum received (419 and 4457 lines of the receipt merged);
- an application or release nets to nothing over those accounts and is not shown;
- the closing balance, the drift against `Customer.Balance` (0) and the uninvoiced figure therefore read exactly as an
  unallocated receipt on 411 always did. On the default setting nothing changes (no line is ever on 419).

## Maintenance (MS-35)

Maintenance › Settings › Advances on customer jobs: **Taken** is allowed once customer advances are held on 419 (the
"later" refusal is lifted only then). An advance taken on a receipt names the customer's open customer job
(`ReceiptVoucher.MaintenanceJobId`); its purpose prints "Acompte — réparation MJ-…". When the job's invoice is raised,
every confirmed advance of that job still holding something is applied to it, oldest first, as far as the invoice goes,
dated with the invoice (Dr 419 / Cr 411; with VAT when received, its 4457 given back). What is left of a larger advance
stays on 419. An advance cheque confirmed after the invoice is applied at its confirmation.

## Kept on a cancelled order (SAL-13, 10 Nov 2026)

The owner answered SAL-13 "yes, answer A" (2026-11-10): the rest of the advance is refunded by the existing payment voucher,
and the part the company keeps moves from the customer's account to an "other income" account the accountant names. Built
as a setting in the same row and on the same page (Finance › Settings › Customer advances; `PUT /api/receivables/settings`,
`accounting.manage`):

| Setting | Options | Default |
|---|---|---|
| Kept advances | Not allowed · Kept as other income | Not allowed (today) |
| Income account for kept advances | An active class 7 income account of the chart | None |

- "Kept as other income" is refused while no active income account of class 7 is chosen (409 `accounts_not_mapped`); the
  account is checked again at each keep. Left out of a save, the two fields keep what is saved.
- **The action:** "Keep part of the advance" in a receipt's ⋯ menu (Finance › Receipts and the customer's account), shown only
  while the setting allows it and to whoever holds `finance.vouchers.approve` — the key that approves refund vouchers, stricter
  than the one that applies advances (`finance.receipts.create` or the cash box). No new key. `POST
  /api/receivables/receipts/{id}/keep` with an amount (none = all the receipt still holds) and a **required** written reason;
  refused (400 `kept_advances_not_allowed`) while the setting is "Not allowed". Asked once more before it acts; no undo.
- Each keep is a `CustomerAdvanceApplication` row of kind **Kept** (append-only, with its reason and the income account
  frozen) whose id is the entry's source id, under the new source `customer-advance-kept`. It counts against what the advance
  still holds exactly as an application does. Dated now, through the normal posting path (a closed month is refused).
- The customer's balance (`Customer.Balance`, negative while we hold their money) goes up by the amount kept: the money is no
  longer theirs.

| Kept from | Cap | Lines |
|---|---|---|
| An advance on 419, untaxed or VAT at the invoice | What it still holds on 419 | Dr 419 (customer) · Cr income (customer) |
| An advance on 419 with VAT when received | What it still holds on 419 | Dr 419 net (customer) · Dr 4457 its VAT part (customer) · Cr 4457 its VAT part (no customer) · Cr income net (customer) |
| A payment receipt on 411 (unallocated credit) | Its unallocated part not kept before, and the customer's credit on 411 (as a refund's cap: less refunds raised and unpaid, less advances still on 419) | Dr 411 (customer) · Cr income (customer) |

**VAT on a kept advance (D45 "when received").** A kept advance is consideration, so its VAT stays due: the keep does not give
it back as an application does. Its VAT part is the same proportional share an application would take (rounded the company's
way; the last part takes what is left), so the rest of the advance gives back exactly the rest of the VAT, once. The two 4457
lines of the keep net to zero on 4457; they only move that VAT off the customer, so the customer's statement no longer counts
it as theirs.

Example: an advance of 119,000 at 19 % (Cr 419 100,000 · Cr 4457 19,000). The order is cut down; 59,500 is kept: Dr 419 50,000
· Dr 4457 9,500 (customer) · Cr 4457 9,500 · Cr income 50,000. The rest (59,500) is applied to the invoice of 142,800 and gives
back 9,500. Net 4457 = −19,000 + 0 − 22,800 + 9,500 = −32,300: the invoice's VAT and the kept part's, each once.

**Statement.** A keep is its own **debit** row, "Advance kept — <reason>", numbered with the receipt it was kept from (kind
adjustment; under "all" only). From 419 its 419 line and its customer 4457 line are merged into that one row, as an advance's
receipt lines are; from 411 it is the 411 line itself. The closing balance is what the customer is still owed, with no drift
against `Customer.Balance`; the ageing reads the same. The receipt page lists each kept part with its reason; a payment
receipt's "Unallocated" no longer counts what was kept.

On the default setting nothing changes: nothing can be kept, no row of kind Kept exists, nothing posts under
`customer-advance-kept`. Pinned by `CustomerAdvanceKeptTests`.

## Not built

- Hire VAT at payment (REN-03): not wanted. Owner, 2026-11-10: hire VAT is due when invoiced, i.e. at the issue note, which is
  where rentals post it today.
- An automatic application of advances to a new invoice outside maintenance: the clerk applies it from the receipt.
