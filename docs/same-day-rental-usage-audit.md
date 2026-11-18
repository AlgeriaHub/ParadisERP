# Same-day rental usage — Phase 1 audit

Baseline: `677bfed`, 2026-10-13. Read-only inspection before business implementation.

## Current implementation and required changes

1. **Authoritative duration:** `ParadisERP.Application/Rentals/RentalStay.cs` prices a stay using `RentalPricing.DaysOut` (currently minimum one), `RentalPeriod.ChargedDays` (contract minimum), and priced entitlement/extension spans. `RentalSettlement.cs` matches returns to issue batches using `RentalFifo.cs`. `QuantityStatement.cs` reuses `RentalStay`.
2. **Duplicates:** `ParadisERP.WebUI/src/app/core/rentals.ts` mirrors days, cutoff, minimum, FIFO settlement and relief. `rental-return-note.component.ts` computes before/after settlement locally. Replace its financial preview with server calculation. Contract and issue draft amount previews are separate from actual returned-stay pricing; server values remain authoritative. `PaymentVouchers.UnusedDaysAsync` derives days from stored charges and needs allocation-based handling of zero-charge returns.
3. **Return model:** `ReturnNote` holds date, user, return-day/overdue decisions and aggregate relief. `ReturnNoteLine` holds contract line, sound/repairable/scrap/missing quantities and reported-loss date. It does not persist the usage decision or exact issue allocations. One line may consume multiple issue batches by FIFO.
4. **Decision granularity:** store explicit usage per returned quantity allocation, linked to ReturnNoteLine and issue line/note. A whole-note flag cannot represent mixed items, partial returns or mixed issue dates. Preserve physical and missing quantities separately. Audit decision actor/time and frozen calculation inputs/results. Legacy rows must remain explicitly legacy rather than acquiring a guessed answer.
5. **Invoice timing:** issue posts agreed-period rent and receivable; official invoice creation is a separate `ReceivableDocuments.CreateInvoiceAsync/FromRentalAsync` operation billing source charges. Invoice records are append-only. Future billing after an un-invoiced return must reflect prior relief, not recreate original full debt.
6. **Credits:** `CreditEarlyReturnAsync` currently distributes aggregate unused rent over earliest issue invoice lines, converting amount into fractional quantity. It lacks exact returned-line attribution. `RentalEndpoints` currently invokes it after return commit and catches failures. Replace this with exact allocations and atomic processing. Automatic credit documents must not repost the ledger/customer adjustment already made by the return. Preserve original invoice tax and income accounts.
7. **Payments/refunds:** confirmed receipts reduce customer balance; pending cheques settle nothing until collection. Return creates relief, not payment. `PaymentVouchers` provides separate auditable customer-refund creation/approval/payment with credit reservation and cash/bank movement. Preserve receipts and cheque state; add missing invoice/credit linkage as needed.
8. **Balances:** positive Customer.Balance is debt, negative is credit. Preserve the existing account-wide sign convention and statement logic. Apply relief once; only actually collected excess is available for refund. Invoice standings separately account for confirmed and pending allocations.
9. **Inventory:** returned sound/repairable/scrap quantities move through existing stock states and branch transfers; missing quantities are written off. Usage never decides whether stock moved. Quantity statements retain physical issue/return events even for zero rental charge.
10. **Migration:** additive migrations in SQLite and SQL Server, with legacy defaults and no financial recalculation/backfill. Preserve old return charges, postings, invoices, payments and credits. New immutable allocation records distinguish new calculation versions. Never invoke the historical daily-pricing upgrade to reinterpret old returns.
11. **Timezone:** server `RentalPricing.LocalDay`/`RentalDays.LocalTime` use configured company zone and UTC timestamps. Issue batches currently carry the effective cutoff-adjusted start date; retain actual issued timestamp/local day separately for same-day detection. Same-day 0/1 overrides cutoff and minimum; later-day rules remain unchanged.
12. **Offline:** rental actions use direct authenticated API calls; no rental return IndexedDB/outbox replication was found. Keep returns and financial preview online-only.
13. **Files/endpoints:** domain rental/receivable entities; Application rental pricing, terms, settlement, quantity statement and DTOs; Infrastructure RentalDocuments, ReceivableDocuments, PaymentVouchers, CustomerStatements/report queries and persistence configurations; both migration projects; API POST `/api/rentals/contracts/{id}/return-notes` plus a server preview endpoint; Angular rental API/return form/core pricing consumers; credit/return/invoice/statement prints; translations fr/ar/en; domain, service, HTTP, frontend and synthetic browser tests.

## Decisions confirmed by the owner

- Same-day physically returned Used = exactly one day; NotUsed = zero days. Payment never implies usage.
- Missing goods retain existing rental charges; usage applies only to physical returns. Damage/loss compensation remains separate.
- Untaxed invoices retain the existing untaxed credit workflow, explicitly labelled internal adjustment, with no VAT or tax-report entry. No new independent accounting document category is required.
- Existing return permission and branch scope must not grant independent refund/manual-credit rights. Financial documents and payments remain immutable; corrections are additional auditable events.

## Verification plan

Cover all twenty requested pricing, partial return, invoice/payment/cheque, stock, statements, timezone, legacy and authorization cases. Include mixed issue batches, mixed VAT, repeat requests, credit failure rollback and pre-invoice returns. Verify Arabic/French desktop/tablet/phone, retained form state on validation failure, and server preview/save agreement. Return paper remains quantity-focused; usage may be shown, while financial details belong in preview/settlement documents.

Implementation is complete in the isolated worktree `F:/SeragProjects/ParadisERP-same-day-usage` on branch `feat/same-day-rental-usage`. It remains uncommitted and not deployed while final checks are being completed.

## Implemented behavior and current verification

- `RentalStay.Price` and `RentalSettlement.AllocateLine` are the authoritative server/domain pricing path. Physical same-company-calendar-day return uses 1 day for `Used`, 0 for `NotUsed`; later-day and missing-goods rules preserve the prior behavior. Quantity and daily rate are multiplied once using the existing pricing helper.
- Same-day usage belongs to each return line and is validated against the actual FIFO issue allocation. Partial returns and multiple same-day return lines can carry independent decisions. Inventory return is based on physical quantity and condition, irrespective of usage.
- The return endpoint requires a decision for every same-day physical allocation, stores selector/time audit metadata, and freezes allocation details. A server preview endpoint returns allocation-specific usage, days, amount, payment/credit summary and `canConfirm`; the Angular UI uses that preview and retains draft data when validation fails.
- Existing issue invoices are not rewritten. Usage credits are linked to exact return allocation, return note, contract and source invoice, retain original tax treatment, and do not duplicate return-ledger relief. Untaxed credits stay in the existing untaxed workflow and are labelled as an internal adjustment. Pending cheques remain unsettled and are not refunded automatically. Existing receipts are retained; any real refund remains a separately authorized and auditable workflow.
- Quantity statements remain physical. Return print includes usage status where applicable. Historical usage defaults to `LegacyUnknown`; the migrations add fields and allocation tables without recalculating old totals or creating historical credits. Both SQLite and SQL Server migrations are present.
- Rental returns use direct API calls; no offline return queue was found, so this remains online-only. Existing return/Credit Note permission keys and branch checks are retained; there is no implicit refund permission expansion.
- Owner decisions applied: missing goods keep existing rental charge; usage applies only to physical returns. Untaxed credits use the current untaxed model as internal adjustments without tax-report entries.

Focused backend regression run: **156 passed, 0 failed** (same-day and late returns, rental usage credits, rental documents, quantity statements and authorization). Angular unit suite: **1,040 passed**. Production Angular build passed with existing bundle-budget and Sass deprecation warnings. The full `dotnet test ParadisERP.Tests` run did not finish: a serialized run remained CPU-bound for over five minutes without reporting progress and was stopped; an earlier interrupted run reported 1,233 passed, 2 skipped, and 1 failure (the runner removed its TRX artifact, so the failed test is unidentified). A clean full-suite pass remains outstanding. SQL Server provider smoke and live browser/E2E checks are also still required before deployment.

Separate UI requests are complete: columns label removed in `40b4e78`; fiscal row-label contrast fixed in `677bfed`; both deployed with healthy site/database and synthetic live browser checks. Deployment record is on main at `b6a4279`. Fiscal contrast verified in Arabic/French, all five themes light/dark, desktop/tablet/phone; 1,040 frontend tests and production build passed. These UI changes are unrelated to and do not implement the rental feature.


## Same-day rental returns — takeover verification (2026-10-14)

The owner authorized finishing the existing `feat/same-day-rental-usage` worktree.
Same-company-calendar-day physical returns require an explicit Used/NotUsed choice by
someone with `rentals.return` permission and access to the contract branch. Used costs
exactly one day; NotUsed costs zero, overriding minimum rental days and cutoff rules.
Unpaid rent is removed from debt. Collected excess is refundable through the existing
separately authorized payment-voucher approval/payment flow; saving a return does not
claim that cash has already been handed back. Partial returns affect only returned quantities.
Historical returns keep their old calculation and unknown usage state.

Review fixes: prevent tax on already-posted base rent from entering the preview twice;
keep late-return decisions visible with accurate server previews; scope allocation snapshots
to their own item/extensions; save final waived charges and rounding; load audit allocations
on contract and return-note reads; join the endpoint filter transaction so a credit failure
rolls back the return; reject invalid preview quantities. Both database migrations are present.

Verification: the inherited full backend baseline passed 1,467 tests (4 SQL-only skips);
114 focused regressions passed after the pricing/allocation fixes. All 1,040 frontend tests
and the production build passed (existing budget/Sass/template warnings).
SQL Server startup applied migrations to an isolated synthetic database. The dedicated
`tests/e2e/same-day-return-api.cjs` passed 109 assertions covering paid/unpaid Used/NotUsed,
required decisions, original invoices, exact credits, actual approved refunds and saved audit reads.
`tests/e2e/same-day-return-ui.cjs` passed 20 assertions with real local previews in Arabic/French
at 1440/768/390 px, preserving the selected usage and quantity on server rejection. Screenshots
in `artifacts/same-day-ui/` were visually reviewed. These scripts require a local demo-seeded API;
the UI script also requires a production Angular build and Playwright.

The existing broad `provider-smoke.cjs` passed its rental/stock/ledger section, then stopped
at its obsolete direct-invoice fixture (around line 929): it sends manually typed invoice lines,
which the owner prohibited on 2026-10-10. This is not a same-day feature failure; the focused
SQL Server check creates invoices from issue notes and passes. No production data or deployment
was changed. Final backend rerun and landing are recorded below when complete.

Final verification (2026-10-14): full backend suite **1,472 passed, 4 SQL-only skipped, 0 failed**. Both provider snapshots report no pending model changes. Dedicated SQL Server API and Arabic/French browser checks passed as recorded above. Ready to land on main; **not deployed**.
