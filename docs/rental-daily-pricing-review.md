# Rental daily pricing and contract builder review — 2026-10-06

Checkpoint branch: `fix/rental-daily-pricing-builder`. No migration or deployment.
Local starting commit: `0d2323c`; the tree was clean. Remote main was subsequently fetched
with the correct repository account at `297fecb`, containing concurrent customer and contract
layout/signatory changes. Integration with those changes remains pending; this branch
preserves the tested local checkpoint requested by the owner.

## Incorrect calculation and full-period field

`AlgeriaPos.Application/Rentals/RentalPricing.cs` and its frontend counterpart
`AlgeriaPos.WebUI/src/app/core/rentals.ts` treated `RentUnitPrice` as a monthly unit price.
They calculated **quantity × days × (entered rate / 30)**. Quantity was already included;
the incorrect monthly conversion was the cause of the reported daily-rate discrepancy.

The contract form's “Full period” value is a read-only `<strong>` calling `linePeriod()`.
It is not an input, does not travel in the save request, and cannot override the amount.
It is now labelled “Line rental total”. The product's configured rental price only
prefills the editable daily unit rate. The negotiated overdue rate is a separate,
existing rule, still validated against the multiplier floor.

The corrected default calculation is **quantity × durationDays × dailyUnitRate**,
rounded once per line to the configured currency precision. The rental contract total
is the sum of these line totals, before tax, deposits, compensation, and overdue charges.

## Changed production paths

| File / path | Change and consistency guarantee |
| --- | --- |
| `AlgeriaPos.Application/Rentals/RentalPricing.cs` | Shared server amount function uses the daily rate directly. |
| `AlgeriaPos.Infrastructure/Rentals/RentalDocuments.cs` | Adds authoritative contracted-line total; invoice description says `/jour`. Issue creation, issue re-reads, invoice source lines, and extension-as-new issues already call the same shared pricing function. |
| `AlgeriaPos.API/Endpoints/RentalEndpoints.cs` | Create, edit, and read responses return calculated line totals and their sum. |
| `AlgeriaPos.Application/Contracts/Dtos.cs` | Adds response-only `RentalTotal` to line and contract DTOs. Client-supplied total/full-period fields cannot replace it. |
| `AlgeriaPos.WebUI/src/app/core/rentals.ts` | Mirrors daily pricing for editor, issue preview, settlement/returns, early-return relief, and overdue previews. |
| `.../rentals/rentals-api.service.ts` | Types the authoritative response totals. |
| `.../rentals/rental-contract.component.ts` | Displays server line totals and the server contract total. |
| `.../print/print-rental-contract.component.ts` | Uses server totals; fallback for older DTO fixtures uses the same daily helper. Prints the daily formula. |
| `.../core/i18n/translations-backoffice.ts` | Exact Arabic/French/English daily-price labels; daily hints, pricing-setting examples, line-total labels, and print rule. |
| `AlgeriaPos.Application/Rentals/RentalTerms.cs`, `RentalSettlement.cs`, `AlgeriaPos.Domain/RentalEntities.cs`, `Entities.cs` | Price-unit documentation now states daily rates. |
| `.../receivables/receivables-api.service.ts` | Invoice description documentation now uses daily units. |

The existing settlement engine already calls the shared server pricing function for rent,
extensions, overdue, and unused days. Duration, cutoff, FIFO, loss-report dates, tax,
linked-item rules, stock movement, and overdue multiplier rules were not changed.

Invoices over rental notes consume `IssueNoteBillingAsync` / `ReturnNoteBillingAsync`.
Invoice prints consume stored invoice lines and totals. Quantity-only issue and return
prints remain quantity-only. Reports and the contract list consume stored charges or
ledger totals; no separate pricing formula was found in those readers. Newly created
documents therefore use the corrected formula through their source postings.

## Contract builder

`rental-contract-form.component.ts`, `rental-builder.scss`, and `rental-builder-copy.ts`
provide compact two-column contract details, desktop item rows, mobile cards, one line
editor dialog containing all existing fields, a persistent multi-add/search/scanner panel,
an item/unit/value summary, and a sticky total/Cancel/Create action bar.

Repeated independent products continue to aggregate quantity, as before. Linked items
remain separate. No new duplicate rule or duplicate action was introduced. Removing a
meaningful line uses the existing confirmation service. Validation links open/focus the
affected line; failed saves retain the draft and focus the error message. Informational
warnings sit beside their relevant fields.

## Verification

- 250 targeted backend rental, receivable-document, and payment-voucher tests passed.
- Full backend suite: 1,118 passed; seven failed because the sandbox could not write the
  Windows Event Log. All seven passed when rerun outside the sandbox (1,125 tests covered).
- 616 frontend tests passed, including the new compact-builder tests.
- Production and development Angular builds passed; existing budget/compiler warnings remain.
- 56 local mocked-browser assertions passed in Arabic and French: 1/5/15 items, sequential
  scans and focus, repeat scans, middle-line editing, linked duration, confirmed removal,
  client and server validation preservation, desktop/768px/390px layouts and dialogs.
- Server arithmetic covers `1×1×5=5`, `1×30×5=150`, `100×30×5=15000`, `2×30×1000=60000`,
  and the `17000` two-line sum. Integration tests trace creation/editing through issue,
  billing source, and ledger; HTTP tests verify bogus client totals are ignored and a
  rejected edit does not alter the saved contract.
- Browser harness: `tests/e2e/rental-builder-ui.cjs`, synthetic data only. Run after a
  development build; it serves that build on loopback and mocks API responses.
- Before/after screenshots: `artifacts/design/rental-builder/` (local, gitignored).

## Historical contracts: owner-approved correction (2026-10-06)

Existing stored rental and overdue rates were defined as monthly. Simply interpreting
them as daily changes recalculated historical contract/issue amounts by ×30 while frozen
invoices, ledger entries, and stored charges retain their old amounts. No historical data
has been changed, and this change must not be deployed with that discrepancy unresolved.

The owner explicitly instructed: "correct them and their financial documents". The
historical correction is now implemented; it has not been run against a real company.

- `RentalDailyPricingUpgrade` recalculates stored rental/overdue charges, early-return
  relief, invoices, credit notes and taxes using recorded quantities/days/rates and frozen
  tax rates. Damage, maintenance, missing-item compensation, receipts, allocations and
  actual refunds keep their amounts. Full-period pricing does not override the formula.
- `RentalDailyPricingCorrection` and both provider migrations queue only contracts already
  present at upgrade, retaining before/after JSON snapshots and a completion marker.
  New contracts are excluded; already-correct daily documents produce no extra postings.
- `LedgerSources` identifies balanced correction entries posted on the correction date;
  original postings remain intact. `PosDbContext` exposes the audit queue. Customer balance
  changes receive sync versions. All pending corrections for one company share a transaction.
- `ReceivableDocuments.OnAccountAsync` includes the adjustments when invoices are created
  later. `CustomerStatements` resolves adjustment document/invoice numbers. Existing
  invoice/credit-note prints and reports consume the corrected stored totals or ledger.
- `RentalClosure` includes source adjustments. Fully returned contracts reopen if correction
  leaves them unpaid; explicit administrator short closures remain intact.
- API startup and archived-company restoration run the upgrade before serving that company,
  using its regional override for rounding. Ambiguous invoice source mapping, reversed
  postings or inconsistent correction state abort and roll back instead of guessing.
- Downgrade is intentionally refused: dropping the audit cannot undo financial changes.
  Operational rollback requires restoring the pre-upgrade database backup.

Verification: 306 relevant backend tests passed, including eight new migration/correction
tests covering taxes, partial credit notes, receipts, statement links, early returns,
closure, overdue/damage separation, rollback, idempotence and migration queue boundaries.
SQL Server migration script generation passed; actual SQL Server execution remains untested.
Integration with newer remote main and deployment remain separate pending steps.
