# 07 — Hardening, printing and payroll: the plan

**Status:** plan for review. Synthesised 2026-09-24 from five read-only scouts (security, rental
gaps, printing, payroll, deployment) whose claims were spot-checked against the tree. No code was
written, built or run for this document.
**Baseline:** `main` at `85d8c43`, tree clean apart from this document and its memory note. The
statement-language session that was in flight when the scouts ran has landed in that commit, so
every `file:line` reference into `core/i18n/translations.ts` and
`receivables/receivable-statement.component.ts` below is **to be re-resolved** by the builder
against `85d8c43` before use (they were taken one commit earlier and have drifted by a few lines);
the other references were spot-checked at this HEAD.
**Read first:** [project-memory.md](project-memory.md) for what is done and verified,
[06-erp-redesign.md](06-erp-redesign.md) for the specification this plan extends.

Conventions. `file:line` references are to the baseline. **⚠ accountant** marks a figure or a rule
that comes from the owner's accountant and is never written from general knowledge — the same
rule §8.2 of the redesign already states for CNAS and IRG, applied to every place it bites. Effort
is for one builder: *hours* (under half a day), *day*, *days* (two to four), *week*. Where two
scouts disagreed, the disagreement is stated and the pick is given with its reason.

---

## 1. Where the system stands

Phases 1–5 of the redesign are done, server and screens, and both providers were exercised at the
current schema on 2026-09-23 (333 backend tests, 129 frontend). What is not done is everything
around that core. **Payroll is a settings model with no home**: `PayrollSettings`, its validator,
the DZ preset and `PayrollOwnership` are referenced by nothing outside `ParadisERP.Tests/Payroll`
and one seeded account (`LedgerAccounts.PayrollExpense = "630"`, `LedgerEntities.cs:251`) that
nothing posts to — the dead-wiring shape the memory warns about. **Printing does not exist beyond
the 80 mm receipt**: the global rule at `styles.scss:731-753` hides `body *` for the whole SPA, so
the six back-office Print buttons (invoice, receipt voucher, statement, ageing, trial balance,
reports) each print one blank 80 mm strip today, and no document has a URL of its own. **The
till's token reads too much**: `GET /api/sales`, `GET /api/sales/{id}`, `GET /api/reports/summary`
and `send-receipt` are on the any-authenticated list (`EndpointAuthorizationTests.cs:58-66`) with
branch as an optional filter, the `device` claim is minted from whatever terminal code the cashier
types at login (`AuthEndpoints.cs:58`), and a rejected push hands the database's own words to the
till with nothing logged server-side (`SyncService.cs:579-585`). **There is no way to run this in
production yet**: no proxy, no backup, no restore drill, a `/health` with no checks, EF command
logging at Information, an image that defaults to SQLite, and — the single most likely mistake — a
single-company start on MySQL that seeds `the historical shared demo administrator login` and demo ledger entries that can never
be deleted (`Program.cs:44-54, 291-292`; `DbSeeder.cs:21-140`). The rental module carries three
known server gaps (list N+1, write-only per-item multiplier, a deposit switch that stores nothing)
and two places where the code and §5.5 can be read differently. Everything below is the order in
which to close these.

---

## 2. Security: branch scoping and error leakage

### 2.1 The rule

* **A cashier sees and touches one branch — the branch of the terminal the session is bound to.**
  The branch is derived server-side from the `device` claim through `Device.BranchId`, never from
  a `branchId` or `deviceCode` query parameter. Sync already works this way (`SyncEndpoints.cs:20-40`,
  `SyncService.cs:40-55, 153-180`, pinned by `SyncDeviceBindingTests`); the rule extends what sync
  does to the other routes a till may call.
* **A Manager or Admin sees every branch** unless a named permission says otherwise. Today only
  `ViewOtherBranchStock` exists (`Permissions.cs:24`) and its name says stock; whether a manager
  without it should also lose other branches' sales and reports is the owner's call (Q3). Until
  answered, Manager → all.
* **A cross-branch lookup by id answers 404, not 403.** A 403 tells the caller the sale exists;
  the sync engine already avoids that oracle and the sales routes should not introduce one.
* **A client never receives database text.** The client gets a stable code and a safe sentence;
  the full exception goes to the server log under a reference the client also shows, so an
  operator can join the two. Domain sentences written by us (`RentalDocuments.cs:89,274`,
  `CatalogEndpoints.cs:72`) are fine; provider messages are not.
* **Nothing here may block a sale.** `BranchScope` is read-only, never throws, and an unknown
  device scopes to nothing rather than to everything.

### 2.2 The device claim is only as strong as the code the cashier types

Login accepts any registered, active terminal code from the body (`AuthEndpoints.cs:58-70`), the
SSO session route the same from a query string (`SsoEndpoints.cs:46,57-58`), and `User` has no
branch (`Entities.cs:21-37`). So a cashier who types `C01` at an Oran till is, for every
branch-scoped route and for `push`, an Algiers terminal — including the receipt-number space
(`SyncService.cs:174-180`). **Branch scoping without terminal binding is a speed bump. The two
ship together.**

Change:

* `User.BranchId : Guid?` (`Entities.cs:21`), **a real foreign key to `Branches` with
  `DeleteBehavior.Restrict`** in both migration sets — a branch that still binds a login cannot be
  deleted, and a stale id cannot be written. **Nullable, backfilled `NULL` = "any terminal"**, so
  no existing account is locked out on upgrade — this is the migration-default lesson applied
  before the fact. Two migration sets, same name `AddUserBranch`
  (`ParadisERP.Infrastructure/Persistence/Migrations`, `ParadisERP.Migrations.MySql`).
* **The column binds cashiers only.** A Manager's or Admin's row may carry a `BranchId` (the users
  tab may have set it before a role change) and it is **ignored for scoping and for login** —
  `BranchScope.ForAsync` returns `null` for those roles before it reads anything, and the users tab
  says so in words next to the field ("not used for managers and admins").
* `AuthEndpoints.cs:70` and `SsoEndpoints.cs:58`: when `user.Role == Cashier && user.BranchId is
  Guid b && device.BranchId != b` → `Results.Problem("This account may not open a session on that
  terminal.", 403)` with `code = "wrong_terminal"`. Managers and admins are not bound.
* Reassigning a cashier's branch goes through `SaveWithVersionsAsync`, which bumps `Version` and
  so revokes live tokens through `UserSessionValidator` — no validator change.
* DTOs `CreateUserRequest` / `UpdateUserRequest` / `UserAdminDto` gain `Guid? BranchId`, copied
  explicitly in `UsersEndpoints.cs:46-59, 80-85, 147-153` (no mass assignment). The users tab gets
  a branch `<select>` shown **only while the role is Cashier**, with "any terminal" as the null
  choice; for Manager and Admin the field is replaced by the text "any terminal" and the request
  sends `null`; the login screen gets the 403 text. i18n keys `backoffice.users.branch`, `backoffice.users.branch.any`,
  `login.error.wrongTerminal` in ar/fr/en.
* Q1 decides the shape: one branch per cashier (proposed — matches "salaries allocated per
  branch" and is one nullable column) or a User→Devices list. `User.BranchId` is a login's
  binding; it is **not** the payroll `Employee.BranchId` of §5 — an employee is not a login (§8.3)
  and there is no link between the two, deliberately, because nothing would read it.

### 2.3 Every enforcement point

| Route | Today | Change |
|---|---|---|
| `GET /api/sales` (`SalesEndpoints.cs:18-44`) | any authenticated; `from/to/deviceCode/branchId/take` all optional; no filter → 200 most recent sales of every branch with cashier, customer, totals | after the optional filters: `if (scope is Guid own) query = query.Where(s => s.BranchId == own)` — the cashier's `branchId` parameter is **overridden, not honoured** |
| `GET /api/sales/{id:guid}` (`:46-72`) | any sale by guid, no branch check — an IDOR | `.Where(s => s.Id == id && (scope == null \|\| s.BranchId == scope))` → 404 |
| `GET /api/reports/summary` (`:78-160`) | `branchId` optional; omitted → company-wide figures and a **named per-branch breakdown** (`:128-143`) | `if (scope is Guid own) branchId = own;` before `:96`; the existing `branchId is null` test at `:129` then empties the breakdown |
| `POST /api/sales/{id}/send-receipt` (`IntegrationEndpoints.cs:34-37`) | sale loaded by guid only; any branch's rendered receipt to any phone or e-mail, five times | same scope in the `FirstOrDefaultAsync` → 404. The till only ever sends the receipt it just printed (`shared/send-receipt.component.ts`), so no UI change |
| `POST /api/auth/login`, `GET /api/auth/sso/session` | any registered active terminal | the `wrong_terminal` refusal of §2.2 |
| `GET /api/catalog/products/{id}/stock` (`CatalogEndpoints.cs:180-188`), `GET /api/catalog/products/low-stock` (`:162-178`) | Manager/Admin; every branch's `StockQty`/`ReorderLevel`, with no `branchId` on low-stock | the permission `ViewOtherBranchStock` is enforced on one route (`StockEndpoints.cs:252`) and undercut by these two. **After §2.2 lands:** `if (!user.HasClaim(perm) && callerBranch is Guid b) rows = rows.Where(x => x.BranchId == b)` where `callerBranch = User.BranchId ?? device branch`. The scout's alternative — `RequireClaim` on `/products/{id}/stock` alone — breaks the products tab's stock panel for every manager without the claim, so it is not the pick |
| `POST /api/rentals/contracts/{id}/extensions` (`RentalEndpoints.cs:227`) | `isManager` computed from roles the group at `:33` already requires — **always true**; `RentalFailure.Forbidden` is unreachable from HTTP | dead wiring, one of two ways: extensions stay back-office → pass `true` with a comment and delete the role sniff; or a cashier may extend non-retroactively → the route gets its own `.RequireAuthorization()` plus `BranchScoped` and joins the reviewed list with its reason. Recommendation: the first. §7 decision 5 says only where the **contract is raised** (back office, not the till); it is silent on extensions, so keeping them Manager/Admin-only is this plan's recommendation by consistency, not a transcription — Q31 asks the owner |
| `POST /api/sync/push` (`SyncService.cs:579-585`) | `Explain` returns `"Server could not record the sale: " + ex.InnerException?.Message ?? ex.Message`; duplicate detection is a substring match on `"Reference"`/`"LocalSeq"`; `SyncService` has no `ILogger` (`:25-26`) | §2.4 |
| `POST /api/platform/companies/{id}/integrations/test-webhook` (`IntegrationEndpoints.cs:111`) | returns `result.Error` (gateway text) verbatim to the operator | portal is disabled in the deployed shape; apply the same code+log pattern when the portal is next touched, not before. `send-receipt` at `:47-49` already does it right |

Mechanics:

* `ParadisERP.Infrastructure/Auth/BranchScope.cs` — `static Task<Guid?> ForAsync(ClaimsPrincipal
  user, PosDbContext db, CancellationToken ct)`: `null` for Manager/Admin; otherwise
  `db.Devices.Where(d => d.Code == user.FindFirstValue("device")).Select(d => (Guid?)d.BranchId)
  .FirstOrDefaultAsync()` falling back to `Guid.Empty` (matches nothing, never throws). An unknown
  or inactive device already fails token validation (`UserSessionValidator.cs:64`); the fallback is
  defence, covered by its own test.
* A `BranchScopedAttribute` marker in `ParadisERP.API/Endpoints`, attached with
  `.WithMetadata(...)`, so the endpoint inventory test can assert which any-authenticated routes
  are scoped instead of trusting a comment.
* The query bodies of list/read/summary move into
  `ParadisERP.Infrastructure/Sales/SalesQueries.cs` taking `Guid? scope`, the way `LedgerReports`
  is separate from `LedgerEndpoints`, so `TillHarness` can test them without HTTP.
* **Scope first, remove later.** The whitelist comments at `EndpointAuthorizationTests.cs:61-62`
  say the till needs `/api/sales` and `/api/reports/summary`; the shipped till does not call them
  (`features/sales/sales.component.ts:7` and `core/pos.service.ts:320-333` read IndexedDB; the only
  callers are back-office screens behind `backofficeGuard` — `reports-tab.component.ts:555-556`,
  `receivable-invoice-form.component.ts:988`). Removing them from the list is the stronger
  control and the smaller change, but it forecloses a future online Z-report; Q2 decides. The
  comments are rewritten to say "branch-scoped" either way.

### 2.4 Error leakage on push

* `SyncService` takes `ILogger<SyncService>`; `TillHarness.NewService` passes
  `NullLogger<SyncService>.Instance`.
* `Explain(Exception ex, Guid saleId, string device)` → `(string Code, string Message)`, classified
  **by type, not by text**: `DbUpdateException` whose inner is `SqliteException` with
  `SqliteErrorCode == 19` or `MySqlException` with `Number == 1062` →
  `duplicate_receipt_number` with today's sentence; `DbUpdateConcurrencyException` →
  `concurrent_update`, "Another terminal changed this record; the sale will be retried."; anything
  else → `server_error`, "The server could not record this sale. Reference {ref}." with
  `ref = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N")[..12]` and
  `logger.LogError(ex, "Sale {SaleId} from {Device} rejected; reference {Reference}", …)`. **Ids
  only in the log** — `SalePush` carries customer ids and payment references. The packages are
  `Microsoft.EntityFrameworkCore.Sqlite 8.0.11` and `Pomelo.EntityFrameworkCore.MySql 8.0.2`
  (`ParadisERP.Infrastructure.csproj:19-20`). **To verify at build time:** the property names
  `SqliteException.SqliteErrorCode` and `MySqlException.Number`, and the values 19 and 1062, are
  this plan's expectation, not something checked against those exact package versions; the
  builder confirms them in the packages' own types before coding, or duplicates regress to
  `server_error` and the pinning test below catches it.
* `PushResultItem(Guid Id, string Status, string? Message, string? Code = null)` (`Dtos.cs:151`) —
  additive, so an old offline bundle keeps reading `message`. `models.ts` gains `code?`;
  `sync-status.component.ts` renders `i18n.t('sync.rejected.' + code)` and **falls back to
  `message`** when `code` is absent (a till may meet an older server during a rolling upgrade).
  Keys `sync.rejected.duplicate_receipt_number`, `.concurrent_update`, `.server_error` in ar/fr/en.
* There is no `UseExceptionHandler`, ProblemDetails or correlation id anywhere in the API;
  Production returns the framework's empty 500. Acceptable for now; any future ProblemDetails
  middleware must not echo `ex.Message`.

### 2.5 Tests

* `ParadisERP.Tests/Security/CashierBranchScopeTests.cs` (TillHarness, C01→Algiers, C02→Oran):
  `A_cashier_sees_only_the_sales_its_terminal_s_branch_took`,
  `A_cashier_s_branchId_query_parameter_is_overridden_not_honoured`,
  `A_cashier_cannot_open_another_branch_s_sale_by_id`,
  `A_cashier_s_Z_report_names_no_other_branch`, `A_manager_still_sees_every_branch`,
  `An_unknown_device_claim_scopes_to_nothing_rather_than_everything`,
  `A_cashier_cannot_send_another_branch_s_receipt`.
* `ParadisERP.Tests/Security/LoginTerminalBindingTests.cs` — **needs the HTTP test host
  (backlog item 4)**, because the refusal lives in the login and SSO handlers and there is no
  service to call around them:
  `A_cashier_assigned_to_Oran_cannot_open_a_session_on_an_Algiers_till`,
  `A_cashier_with_no_branch_may_still_use_any_till`, `A_manager_may_open_a_session_on_any_till`,
  `Moving_a_cashier_to_another_branch_revokes_the_sessions_they_hold`.
* `EndpointAuthorizationTests`: `Every_route_a_till_may_read_is_branch_scoped_or_on_the_reviewed_list`;
  `Every_route_that_lists_stock_by_branch_either_needs_the_named_permission_or_scopes_to_the_caller`;
  `A_retroactive_extension_refusal_is_reachable_by_someone_or_not_expressed_at_all`.
* `ParadisERP.Tests/Security/PushRejectionMessageTests.cs` (force a failure with
  `DROP TABLE LedgerEntries` on the harness connection before the push):
  `A_rejected_sale_never_carries_the_database_s_own_words` (no "SQLite", "table", "constraint"),
  `A_duplicate_receipt_number_is_recognised_by_error_code_not_by_text`,
  `A_rejection_is_logged_in_full_under_the_reference_the_till_was_given`,
  `A_rejection_carries_a_stable_code_the_till_can_translate`.
* Architecture check that both providers carry the `AddUserBranch` migration; re-run
  `tests/e2e/provider-smoke.cjs` on MySQL afterwards.

**There is no HTTP test host today.** No project references `Microsoft.AspNetCore.Mvc.Testing`
and nothing builds a `WebApplicationFactory`; every backend test drives services directly
(`TillHarness`) or inspects endpoint metadata by reflection (`EndpointAuthorizationTests`). Any
test above or in §5.11 and §6.0 that asserts a status code, a login, a policy or a startup throw
is therefore preceded by **backlog item 4**: add `Microsoft.AspNetCore.Mvc.Testing` to
`ParadisERP.Tests`, a `WebApplicationFactory<Program>` fixture on a SQLite file per test class
with the single-company tenant configured in code, and `Program` made visible to the test
assembly (`public partial class Program` or `InternalsVisibleTo`). The `CashierBranchScopeTests`
and `PushRejectionMessageTests` above do **not** depend on it — they go through `SalesQueries`
and `SyncService` on the harness.

Follow-up, not in scope: `PullAsync` replicates every cashier's `PinHash` to every till
(`SyncService.cs:78-84, 622`) by design for offline PIN. Once `User.BranchId` exists, replicating
only the device's branch is cheap — but it changes offline unlock for floating staff, so it waits
for the owner.

Effort: terminal binding *day*, branch scoping *day*, push messages *day*, stock routes and the
extension flag *hours* each.

---

## 3. Rental server gaps

None of the three fixes needs a migration: `RentalContracts.DepositAmount` exists non-null in both
sets (`…/20260923115043_Rentals.cs:68`, `…/20260923115102_Rentals.cs:69`), `Stores.RentalDepositEnabled`
exists with default false, `Product.RentalOverdueMultiplier` exists (`Entities.cs:311`).

### 3.1 The list carries its own answer: quantities and money on the row

`GET /api/rentals/contracts` (`RentalEndpoints.cs:75-93`) projects id, number, customer, branch,
status and dates — no quantity, no money — up to 500 rows. The screen therefore calls
`GET /contracts/{id}/settlement` once per contract, capped at 60, eight at a time
(`rental-contracts.component.ts:193, 277-292`); each call is a five-query load plus a full engine
run, the header tiles silently exclude contracts past the cap, and rows 61–120 keep the "out"
chip even when partly returned because `contractState` receives null.

Everything the row needs is already persisted: `IssueNoteLine.Qty`, the four `ReturnNoteLine`
quantities, and `RentalCharges` — which `StoreCharges` writes for every engine charge including
unposted in-period rent, so `Sum(Amount)` equals `settlement.ChargeTotal` by construction.

* `Dtos.cs` (after `RentalContractDto`, ~`:519`): `RentalContractRowDto(Id, Number, CustomerId,
  CustomerName, BranchId, Status, IsLocked, LockedAtUtc, SignedAtUtc, ClosedAtUtc, IssuedQty,
  SettledQty, StillOutQty, ChargeTotal, OverdueTotal, decimal? DepositAmount = null)` replaces the
  anonymous projection. Property names that exist today are unchanged; the rest are additions.
* `RentalDocuments.TotalsAsync(IReadOnlyCollection<Guid> ids, ct)` → per-contract
  `ContractTotals(IssuedQty, SettledQty, StillOutQty, ChargeTotal, OverdueTotal)` from **three set
  queries** on the id list — issue lines, return lines, charges — each `AsNoTracking`, each
  `ids.Contains(...)` (the pattern the route already uses for customer ids at `:86`), each summed
  **client-side after `ToListAsync`** because SQLite stores decimal as TEXT and the provider cannot
  translate `Sum` over it. `StillOutQty = Math.Max(0, issued − settled)`, equal to the engine's
  because `ReturnAsync` refuses over-returns. `OverdueTotal` is the sum where
  `Kind == RentalChargeKind.Overdue`. `DepositAmount` is filled only when
  `RentalDeposit.IsVisible(settings)`.
* Screen: `RentalContractRow` gains the six fields; `interface Row`, `MAX_ENRICHED`, `enrich()`
  and the `settlement: null` seeding are deleted; cells read `row.stillOutQty`, `row.chargeTotal`,
  `row.overdueTotal > 0` for the late chip; `totals()` sums rows. `RentalsApi.getSettlement` stays
  for the detail screen. No i18n change.
* `project-memory.md:345-353` ("Two API gaps the screens work around") is rewritten when this
  lands.

Two things to state in the DTO's doc comment: the list shows **what was charged** (stored
`RentalCharges`) while the detail screen recomputes from documents — they agree today by
construction, and a future engine correction would show on the contract before the list; and the
`IN` list of up to 500 ids is fine on MySQL 8 and on bundled SQLite (whose parameter limit is
**to verify at build time** against the `Microsoft.Data.Sqlite` native build actually shipped —
32,766 is the modern default, 999 the old one; the builder reads `SQLITE_MAX_VARIABLE_NUMBER` off
the bundled library rather than asserting either) but a `Take` above the verified limit would need
chunking.

Tests (`RentalDocumentTests`):
`The_list_totals_say_what_is_out_and_what_it_has_cost_without_settling_each_contract` (two
contracts; the first issues 100, returns 40 twenty days late with 2 missing; totals equal
`SettleAsync` on `StillOutQty`, `ChargeTotal`, overdue; the second is all zeros; nothing leaks
between them); `Totals_are_asked_for_in_one_round_trip_per_table_not_per_contract` (25 contracts,
a command-counting `DbCommandInterceptor`, exactly 3 commands). Effort: *day*.

### 3.2 The per-item multiplier can be read

`PUT /api/rentals/settings/products/{id}` (`RentalEndpoints.cs:61-68` →
`RentalDocuments.SetProductMultiplierAsync :108-119`) is the only route touching
`Product.RentalOverdueMultiplier`, and its response is the only place the value appears. The
settings screen says so (`rental-settings.component.ts:166`, key `rent.settings.perItemReadGap`)
and the contract form previews overdue with the company multiplier even for an item that has its
own (`rental-contract-form.component.ts:493-500`) — so the figure the clerk sees is not the figure
frozen on the line.

* `ProductOverdueMultiplierDto(ProductId, Sku, NameAr, NameFr, NameEn, decimal? OverdueMultiplier)`
  beside `SetProductOverdueMultiplierRequest` (`Dtos.cs:447`).
* `RentalDocuments.ProductMultipliersAsync` (`!IsDeleted && RentalOverdueMultiplier != null`,
  ordered by SKU) and `ProductMultiplierAsync(id)` (null when missing or deleted).
* Routes on the rentals group: `GET /settings/products`, `GET /settings/products/{productId}`
  (404 when unknown); the PUT returns the same DTO.
* **Not on `ProductDto`** (`Dtos.cs:77-81`): it is the sync payload to every till, and renting does
  not touch the sync engine — the boundary a test already guards for the entities.
* Screen: the settings tab lists the overrides in a `bo-data` table (name, SKU `mono`, multiplier
  `num mono`, a "back to the company figure" ghost button that sends null), prefills the field on
  pick, and drops the hint and its key from all three dictionaries; the rentals tab loads the list
  in the same `Promise.all` as settings and hands it to the form, whose `derivedOverdue(line)`
  uses the item's override first. New keys `rent.settings.perItemList`, `.perItemNone`,
  `.perItemClearAction` in ar/fr/en.

Tests: `An_items_own_multiplier_can_be_read_back_and_listed` (set → read 2.0 and one row; clear →
null and empty; a deleted product is not listed; unknown id → null); extend
`An_items_own_multiplier_beats_the_companys` (`:131`) to assert the read route agrees with what
`BuildLinesAsync` froze. Effort: *hours*.

### 3.3 The deposit is stored when the switch is on, shown in words, owed back at close

`CreateContractAsync` writes `contract.DepositAmount = 0m` unconditionally
(`RentalDocuments.cs:150-153`) although `UpsertRentalContractRequest.DepositAmount` exists
(`Dtos.cs:468`), `RentalDeposit.Held/IsVisible` exist (`RentalRules.cs:143-158`), the DTO returns
the field when the switch is on (`RentalEndpoints.cs:260`) and account 419
`LedgerAccounts.CustomerDeposits` is seeded with nothing posting to it. With
`RentalDepositEnabled = true` the request's amount is accepted and discarded — the switch is dead
wiring.

The rule, from §5.1 ("liability, not income") and §5.6, plus the code's own comments — the
scout is right that the spec says nothing about collection or refund, so the last two points are
this plan's proposal, not a transcription. One honest correction to the spec's sentence: §5.6 of
the redesign says "when switched on: a liability, not income", and this section posts nothing.
**What the switch does after this phase is "recorded and shown; the liability posting waits on
Q7's account."** The deposit is a liability in nature and will be booked as one the day a money
document collects it; until then the switch records a figure and prints it, and no line reaches
419 or any other account. The settings notice and `rent.settings.depositOn` are reworded to say
exactly that, so nobody reads a ledger promise into a stored figure.

* The deposit is a figure signed on the contract and **held on the customer's behalf**. It is
  never income, never netted against charges or against `Customer.Balance`
  (`RentalDocuments.cs:406, 581` untouched), never enters `RentalSettlement.Relieve`.
* Visible only while the switch is on; **turning the switch off hides it without erasing it**
  (`Held` returns 0, `IsVisible` false; the row keeps its figure).
* **Frozen with the prices at the first issue note** — it is a signed figure (the scout's
  recommendation, adopted rather than asked).
* **Owed back when the contract closes.** Nothing posts at signing — a figure on a contract is a
  promise, not money. Collection and refund each need a money document: collection is a
  `ReceiptVoucher` whose credit side is a deposit liability (a purpose/account column on
  `ReceiptVoucher`, two migration sets, a branch in `ReceivablePosting.Receipt`); refund is the
  `PaymentVoucher` phase 5 left unbuilt (`docs/03-work-status.md:429`). Both are a later phase;
  until then close **states** "deposit to return X" as an owed, visible figure — the stance the
  memory already takes on the early-return refund. Q6 confirms; Q7 (accountant) decides the
  account before the first posting, because system accounts may be renamed but never recoded.

Change: in `CreateContractAsync` hoist `settings`, refuse a negative deposit, and write
`contract.DepositAmount = Engine.RentalDeposit.Held(settings, r.DepositAmount)` — while off this
still writes 0, so the test at `RentalDocumentTests.cs:795` keeps passing with its comment
rewritten; the same two lines in `UpdateContractAsync` inside the existing lock check.
`RentalDeposit.ToReturn(settings, contractDeposit, isClosed)`. `RentalSettlementDto` (`Dtos.cs:566`)
gains trailing `decimal? DepositHeld = null, decimal? DepositToReturn = null`, both null while
invisible, filled by the settlement endpoint (`RentalEndpoints.cs ~:142-160`). Screens: a deposit
field in the contract form's "who" group inside `@if (settings().depositEnabled)` (`type=number
min=0 class="num mono" [step]="regional.moneyStep()"`), sent as
`settings().depositEnabled ? deposit() : 0`; a metric tile on the contract after `toGiveBack`
(`rental-contract.component.ts:116-122`) **hidden when the figure is 0, even while the switch is
on** — §5.6's "no zero line" holds in both directions, and a contract signed without a deposit
shows no deposit tile — whose label switches to "deposit to return" in words when
`depositToReturn > 0`; the settings notice reworded to say what the switch now does and that the
system neither collects nor refunds yet. Keys `rent.deposit`, `rent.form.depositHint`,
`rent.depositHeld`, `rent.depositHeldNote`, `rent.depositToReturn`; `rent.settings.depositHint`
and `.depositOn` reworded (`translations.ts:1198/2399/3600`).

Tests: `The_deposit_is_stored_and_shown_once_the_switch_is_on`,
`The_deposit_never_nets_against_what_the_contract_owes` (charge total, `customer.Balance`,
`PostedAsync(RentalIssue|RentalReturn)` identical to the no-deposit run; no line hits 419),
`A_negative_deposit_is_refused`, `Switching_the_deposit_off_hides_it_without_erasing_it`,
`The_deposit_is_owed_back_when_the_last_line_clears`; `RentalRulesTests` extended with `ToReturn`
cases. Effort: *day*.

### 3.4 Two drift checks — no code until the owner answers

* **A signed overdue price may undercut the admin's multiplier.** The derived path honours §7
  decision 4 and freezes the figure (`RentalDocuments.cs:247-249`), but `line.OverdueUnitPrice ??`
  accepts any client figure and `Validate` only demands ≥ the hire price (`RentalRules.cs:27-29`;
  form field `rental-contract-form.component.ts:245-262`). A manager can sign 1.0× when the admin
  set 1.5×. Recommendation: **the admin's multiplier is a floor** — in `BuildLinesAsync` compute the
  derived figure and refuse anything below it with the figure named; a negotiated price may only
  go up. The alternative (drop the field and always derive) removes a negotiation the form was
  built for. Q4. Test: `An_overdue_price_below_the_admins_multiplier_is_refused`.
* **A reported wreck stops rent on the report day.** `RentStopsOn` (`RentalTerms.cs:160-165`)
  applies the report date when `QtyOk == 0 && QtyPartialDamage == 0`, so a `QtyTotalDamage` line
  with a report date — a wreck that physically came back and goes to scrap — also stops rent on
  the report day; §5.5's literal wording covers "a note where nothing came back", and
  `RentalReportedLossTests` pins `QtyMissing` and `QtyOk` only. Recommendation: **keep the current
  behaviour** — a unit reported destroyed is no more usable than one reported lost — and reword
  §5.5 to "a line where no usable unit came back". Q5. Tests either way:
  `A_reported_wreck_that_comes_back_stops_rent_on_the_report_day` (or its inverse),
  `A_report_on_one_line_does_not_shorten_another_lines_hire_on_the_same_note`.

Effort: *hours* each, after the answers.

---

## 4. Document printing

### 4.1 First, stop printing blank strips

`styles.scss:731-753` is `@media print { body * { visibility: hidden } #receipt-print, #receipt-print
* { visibility: visible } … @page { size: 80mm auto; margin: 3mm } }`, unconditional for the
whole SPA. `backoffice-ui.scss:785-829` only hides `.no-print` and repeats `thead`; it never
restores visibility or resets `@page`. So every back-office Print button
(`receivable-invoice.component.ts:564`, `receivable-receipt.component.ts:399`,
`receivable-statement.component.ts:340`, `receivable-home.component.ts:352`,
`accounting-tab.component.ts:1354`, `reports-tab.component.ts:578`) prints one empty 80 mm page;
`id="report-print"` at `reports-tab.component.ts:115` is referenced by no stylesheet. Only the
receipt was ever checked in a print preview (memory, "Cashier-screen design pass").

Change: scope the receipt rule to `html.print-receipt`, toggled by the till while its receipt
dialog is open (`register.component.ts` payment-done dialog, `sales.component.ts` reprint/refund
dialogs). An `@page` size cannot be gated by a class, so `ReceiptComponent` injects
`<style id="page-size">@page{size:80mm auto;margin:3mm}</style>` on init and removes it on
destroy; the default everywhere else is `@page { size: A4; margin: 14mm 12mm }` in the shared
print stylesheet of §4.2. Delete `id="report-print"` or make it the reports tab's print root.
Effort: *hours*. Everything else in this section is invisible until this lands.

### 4.2 One mechanism: a shell-less print route in a new tab

Documents live inside in-memory `View` unions with no URL (`receivables-tab.component.ts:24-33`,
`rentals-tab.component.ts:15-22`); the UI language sets `<html dir>` globally
(`i18n.service.ts:164-166`) and `I18nService.t/money/dateTime` have no language parameter, so a
document can only print in the UI language, portrait, with the app chrome around it, and the
whole SPA shares one `@page`.

* Route `{ path: 'print/:kind/:id', canActivate: [authGuard, backofficeGuard], loadComponent:
  PrintPageComponent }` (lazy chunk). Query parameters `lang=ar|fr|en`, `from/to` (statement),
  `asOf` (ageing), `copies`, `auto=1` (call `window.print()` after data and `document.fonts.ready`).
  The token is IndexedDB meta (`auth.service.ts:82`), shared with the new tab.
  **The shell must not draw its nav for this route.** `app.component.html:2` renders the top bar
  on `auth.isAuthenticated()` alone — the portal route does not suppress it, so the print route
  needs an explicit exclusion (a signal on the router URL) rather than anything borrowed.
* `shared/print/print-document.component.ts` — inputs `kind`, `number`, `date`, `lang`, `dir`
  (set as `[attr.dir]`/`[attr.lang]` on its root), `party`, `branch`; slots: company identity
  header (store name in the document language with the `shopName()` fallback chain of
  `receipt.component.ts:339-345`, address, phone, **every configured fiscal identifier that has a
  value**, with its code, from `RegionalService.settings().fiscalIds` × `Store.fiscalIds` — the
  `showOnReceipt` flag was designed for the 80 mm slip and does not govern A4; *superseded
  2026-10-20: a document raised at a branch prints that branch's name, address, phone and
  identifiers, the main branch's where it has none, then the company's — `documentIssuer`*), body
  `<ng-content>`, footer (the kind's text from §4.3, then `store.receiptFooter`), a
  `position: fixed; inset-block-end: 0` strip with document number and page word, and `copies=2`
  → `break-before: page`. **To verify at build time**, on the first Playwright PDF of item 10:
  that Chromium repeats a `position: fixed` footer on every printed page, and that it ignores
  `@page` margin boxes (`@bottom-center` and friends) — both are this plan's expectation, so page
  numbers are declared best-effort until the PDF assertion shows what actually renders. If the
  footer does not repeat, the fallback is a per-page footer row inside the table (`tfoot { display:
  table-footer-group }`), which is not gated on either behaviour.
* `PrintService.open(kind, id, opts)` = `window.open('#/print/...', '_blank')`; each screen's Print
  button calls it instead of `window.print()`. `PrintPageComponent` injects the runtime `@page`
  (`A4`, `A4 landscape` for ageing, `A5` for the voucher). **To verify at build time:** that a
  `@page { size }` injected into a `<style>` after load is honoured by Chromium's print dialog and
  by `page.pdf({preferCSSPageSize:true})`; the MediaBox assertion in §4.4 is the check, and item 7
  (the till's own injected 80 mm rule) is the first place it is exercised.
* `I18nService.tIn(lang, key, params)`, `moneyIn(lang, value)`, `textIn(lang, localized)` — pure
  wrappers over `TRANSLATIONS[lang]`, `formatMoney`, `localized` — so a page renders in `lang`
  without switching the app. `formatDay`/`formatRentalDay` already take a language;
  `i18n.dateTime()` does not.
* `print.scss`: black on white, `mono`/`num` on money (design system), state in words never in
  colour, `thead { display: table-header-group }`, `tr { break-inside: avoid }`, logical properties
  only, 15px base. The till receipt stays as it is: offline, in place.
* Bundle: the route and its ~60 keys × 3 languages stay in lazy chunks; the initial bundle is
  already over its 500 kB budget.

Effort: *days*.

### 4.3 Company document settings — admin-owned, never replicated

The only printable texts are `Store.ReceiptFooter` (400 chars) and the operator's
`ReceiptLegalText` (≤500, receipt-scoped). A signed contract needs paragraphs of terms, an invoice
needs payment terms and bank details, a voucher its own wording — and none of it belongs on the
till replica (`StoreDto`, `Dtos.cs:51-59`, is synced; the precedent is `RentalOverdueMultiplier`,
deliberately absent from it).

* `Store.DocumentTexts` (JSON, same converter as `FiscalIds`) shaped
  `{ [kind]: { [lang]: string } }` for `invoice | creditNote | receiptVoucher | statement |
  rentalContract | issueNote | returnNote | payslip`, 4,000 chars per text; `Store.Email` (200),
  `Store.Website` (200), `Store.BankDetails` (400, free text — RIB/IBAN labelling differs by
  country, so no field per country). Migration `DocumentTexts` in **both sets**, nullable columns,
  `NULL` read as empty — no backfill needed; verify with `provider-smoke.cjs` on MySQL.
* `GET/PUT /api/catalog/store/documents` (Admin) with `StoreDocumentsDto` — **not** added to
  `StoreDto` or `UpsertStoreRequest`, so tills never pull it. **The PUT writes the replicated
  `Store` row with a plain `SaveChangesAsync`, never `SaveWithVersionsAsync`**: the texts are not
  on `StoreDto`, so a `Version` bump would only make every till re-pull a `Store` row whose synced
  fields did not change. A test asserts `Store.Version` is unchanged after the PUT. `store-tab.component.ts` gains a
  "printed documents" group: one textarea per kind × enabled language, plus e-mail, website, bank.
  Keys `store.docs.title`, `store.docs.kind.<kind>`, `store.docs.hint`, `store.bank`,
  `store.email`, `store.website`.
* **The code writes no legal terms.** The owner pastes his current paper wording (Q8); the frame
  prints `documentTexts[kind][lang]` with the `localized()` fallback chain.

Tests: `StoreDocumentsTests` (round trip, cap, `Store.Version` unchanged by the PUT, and the sync
pull payload has no `documentTexts`) under both providers — the round trip is at the `DbContext`
level, so no HTTP host is needed; Karma for the textareas per language. Effort: *day*.

### 4.4 The documents

Each is a component under `features/print/`, rendered inside the frame, with the fields the
DTOs already carry and the additions named. Numbering is `{PREFIX}-{n:D6}` throughout — today a
`private static Numbered` copied into `RentalDocuments.cs:1147` and `ReceivableDocuments.cs:702`;
§5 needs it a third time, so it becomes the shared `DocumentNumbers` helper (backlog item 19).

| Document | Page | Fields | Needs on the server | Reach |
|---|---|---|---|---|
| **Sale invoice** (INV) | A4 | title from `taxMode` (untaxed → the `receipt.withoutTax` sentence; `showZeroTax` → a 0.00 line); number, issued, due; branch block; buyer block (name, code, address, phone, `customerTaxIdLabels`: taxId — from the `customers` input, the DTO carries `customerName` only); lines (name, sku, qty, unit HT, discount, rate label, net); **tax table per rate** computed as `DocumentTax.On` does (mirror in `core/document-tax.ts`: group `lineNet` by `taxRate`, round per group); HT / tax / TTC; then stated, not netted: `prepaid` ("settled at the till on <source>"), `credited`, `received`, **balance due** = `outstanding`; payment terms, bank details, legal text (§4.3); amount in words (§4.5); source line when `origin != Direct` | `InvoiceDto` gains `SourceNumber` (join `Sales.Reference` / `IssueNotes.Number` / `ReturnNotes.Number` in the map at `ReceivableEndpoints.cs:242`) | `receivable-invoice.component.ts:564` → `PrintService.open('invoice', id)` |
| **Credit note** (CRN) | A4 | number, "corrects invoice INV-…", reason (mandatory), pro-rata lines, totals shown under a "to the customer's credit" caption | **`GET /api/receivables/credit-notes/{id}`** — today there is only a list (`receivables-api.service.ts:385-392`), no detail screen, nothing to print from | a Print action per row in `receivable-credit-notes.component.ts` and in the invoice's credit-note table (`:271-282`); a Print link in the `done` sentence after `creditNoteSaved` |
| **Receipt voucher** (RCV) | **A5, two copies** (company / customer) | number, received on, branch, "received from" block, amount in figures and words, tender name with instrument number / bank / due date when present, reference, allocations (invoice, amount) + "on account" for `unallocated`, **status in words**: Pending → the `inv.claimedHint` sentence ("subject to collection, settles nothing until confirmed"), Confirmed → confirmed on <date>, Bounced → bounced + `statusNote`; "received by" signature line (`userName` is not on the DTO) | none | **the Print button leaves the `@else` branch** (`receivable-receipt.component.ts:252-262`) so a customer handing over a cheque gets paper *while it is pending* — which is when they need it; also offered in the `done` banner after `receiptSaved`. Q10 on the 80 mm alternative |
| **Customer statement** | A4 portrait | company and customer blocks, period caption (inclusive end), opening row, rows (date, document number + `SOURCE_KEY` word, invoiced as, description, reversal word, debit, credit, running balance), totals, closing; then stated in words: `pendingNotes`, `totalOwed`, `uninvoicedBalance`, `creditLimit`, `balanceDifference` only when ≠ 0; per-customer ageing strip via `ReceivablesApi.ageing(asOf, customerId)` | none (known: description snapshots are English in the Arabic UI — memory, "Known, not fixed") | `receivable-statement.component.ts:90` → `open('statement', customerId, {from, to, lang})`; also from the customers tab's account button |
| **Debt ageing** | **A4 landscape** | as-of date, company block, table without the bar and action columns (customer, total, five buckets, oldest, uninvoiced, credit-limit state in words), totals row, the two footnotes (`inv.home.note`, `inv.home.limitHint`) | none | `receivable-home.component.ts:114` → `open('ageing', '-', {asOf})` |
| **Rental contract** (CTR) | A4, two copies | title, number, signed on, place = branch, company and customer blocks; lines: item (**catalogue name in the document language, with the French snapshot in parentheses when it differs — the snapshot is what was signed**), sku, qty, duration (accessory rows indented under their parent, "inherits <parent>'s period"), **rent / overdue / damage unit prices**, tax rate per line, indicative rent for the agreed period (`rentalAmount` from `core/rentals.ts`) and total; deposit line only when the figure is above zero (a zero deposit prints nothing, switch on or off); notes; terms = `documentTexts.rentalContract[lang]`; the rules the system can state factually from settings (period starts at issue, daily = monthly ÷ 30, the multiplier in force); two signature blocks. No "draft" watermark: a contract is signed before it is issued, so lock state stays off the paper | `RentalContractLine.TaxRate` is frozen at signing (`RentalEntities.cs:152`) but **absent from `RentalContractLineDto`** — add it | Print in the contract head (`rental-contract.component.ts:45-50`) and after save in the form. This is the paper §5.5's dispute is about: the signed overdue price the customer never saw |
| **Issue note** (ISS) | A4 | number, contract, customer, issuing branch, issued on, raised by, lines joined to the contract lines (name, sku, qty out, duration, **due back on** = `periodEndsOn(issuedOn, entitledDays)`), the `rent.issue.thisNoteInvoices` sentence with `periodRent`, note, "received by (customer / driver)" | none — route is `/print/issue-note/:contractId/:noteId` and reads the contract (`IssueNoteLine` has no name or due date; both derive via `openBatchesOf`) | Print per row in the contract's issues table (`:258-330`); after raising, the rentals shell returns to the contract with a `done` sentence and a Print link (add `done` to its `View` as receivables did) |
| **Return note** (RET) | A4 | number, contract, customer, taken at branch (+ "issued at" when different), returned on, lines with the five condition columns and `reportedOn`, charges (kind word, item, qty, days, unit price, amount), `chargeTotal`, `unusedRent` with its relief split (`rent.return.offDebt` / `rent.return.paymentVoucher`), "this note closes the contract" when `contractClosed`, signatures. Transfers are internal and stay off the customer copy (`transfers` exists only on the POST response anyway) | none | Print per row in the returns table; the same `done` link |
| **Payslip** (§5) | A4, one per employee, `break-after: page` | see §5.12 | none beyond §5 | "print all" streams the paged payslips 100 at a time into one print document |
| **Reports** (added 2026-10-08) | A4 | `/print/report/:name` for the trial balance, profit and loss, balance sheet, account statement, journal and sales overview: company head, formal title, the period as a sentence where a document has its number, scope and print time, a ruled row of headline figures with states in words, tables with repeating heads and totals, prepared-by / checked-by signatures, page n / m in the margin; a statement or journal that reaches the server's row ceiling says so (and a statement's last figure is then "balance at the last row") | none | the accounting reports' and the sales overview's Print buttons → `PrintService.open('report', name, {from, to, branch, account, source, lang})`; they used to print the screen (`03-work-status.md`, *Reports on paper*) |

The payroll scout proposed payslips through per-screen `@media print` blocks on the pattern of
`accounting-tab.component.ts:1055-1066`; that pattern is exactly what prints blank today. **Payslips
ride the §4.2 route like every other document.**

Tests: Karma per component (fiscal ids with values print with their code and empty ones are
omitted; `dir=rtl` when `lang=ar` whatever the UI language; Latin digits only; untaxed invoice shows
the sentence and no rate table; `prepaid` line only when > 0; pending voucher prints the
subject-to-collection sentence and no "paid" word; accessory row follows its parent with the
parent's duration; charges sum equals `chargeTotal`; `balanceDifference` row hidden at 0);
`core/document-tax.spec.ts` (two rates equal `DocumentTax.On`; TND three decimals). Backend:
`GetCreditNote_ById` (200/404), `InvoiceDto_CarriesSourceNumber` for the three origins,
`ContractLineDto_CarriesTaxRate`. Playwright `tests/e2e/print-documents.cjs`: for each kind ×
{fr, ar} open the route, `emulateMedia({media:'print'})`, screenshot to `artifacts/print/`, assert
`documentElement.dir`, no Arabic-Indic digits, `page.pdf({preferCSSPageSize:true})` MediaBox
(226.77 pt on the till, 595.28 × 841.89 elsewhere, landscape for ageing, two pages for two copies,
`thead` text present on page 2 of a 60-row statement); wired into `.github/workflows/build.yml`
after `platform-ui.cjs` (`:66`). Effort: invoice + credit note *days*, voucher *day*, statement +
ageing *days*, contract *days*, issue + return *days*.

### 4.5 Amount in words

Vouchers and invoices in this market carry the amount in words; nothing converts a number to words
in any language, and the currency's unit names (dinars/centimes, dirhams/millimes, euros/cents) are
country data that `RegionalSettings` does not hold.

* `core/amount-words.ts`: `amountInWords(value, lang, unitNames)` for ar, fr (quatre-vingts,
  cent/cents, mille invariable), en; `''` for any other language, so the line is omitted.
* `currencyUnitNames: { [lang]: { major, minor } }` added to `RegionalSettings` (server
  `RegionalSettings.cs` + presets DZ/MA/TN/FR/SA + `Clean`; client `models.ts`/`DEFAULT_REGIONAL`),
  operator-editable like the other localized dictionaries. Empty names → figures only.

Tests: `amount-words.spec.ts` (fr 0, 1, 21, 71, 80, 81, 100, 200, 1000, 1 000 000,
1 234 567,50; ar 1, 2, 11, 100, 1000, 2000, 1 234 567.50; en; a three-decimal minor unit; unknown
lang → ''); `RegionalSettingsValidatorTests` for the new dictionary. Q11 decides which documents
and languages. Effort: *day*.

### 4.6 PDF: the browser now, the same route from the server later, never a second layout

There is no PDF library in any csproj and the only server sender is `SmtpReceiptSender` with a text
body (`IntegrationDelivery.cs:80-95`). Three options:

* **(A) Browser print → "Save as PDF".** No server cost, exact fidelity, works offline; the user
  attaches files by hand and nothing can be scheduled.
* **(B) QuestPDF / SkiaSharp on the server.** A second layout of every document in C# that must
  agree with the Angular one, Arabic shaping to re-verify, native libraries in the image. **Not
  chosen**: two layouts of a signed contract is the double-source problem the ledger design exists
  to avoid.
* **(C) Headless Chromium on the server** (Playwright for .NET / PuppeteerSharp) rendering the same
  `/#/print/...` route: one layout, exact fidelity, Arabic already proven in Chromium; costs
  ~300 MB in the image, 100–200 MB RAM per render, a short-lived print token for the headless
  session, and an SMTP attachment path (`System.Net.Mail.SmtpClient` is not recommended for new
  development; a MailKit switch may come with it).

**Decision: ship (A) now; design the route so (C) is additive later.** The route is URL-addressable
and data-only (no clicks to render), which is exactly what (C) would drive; the Playwright
`page.pdf()` assertions of §4.4 are the contract test (C) inherits. When e-mailing statements is
asked for (Q9): `IPdfRenderer.RenderAsync(route, lang)` behind a feature flag and
`POST /api/receivables/statement/send` composing `MailMessage.Attachments` over the existing
`SmtpSettings`.

Two questions change the data model, not the mechanism: whether the invoice freezes the buyer's
name, address and tax id at issue (snapshot columns + backfill from `Customers`, two sets — Q12), and
whether issue/return notes print a driver, vehicle or site (none exists in the data — Q13).

---

## 5. HR and payroll

### 5.1 What exists, and what it means

`ParadisERP.Application/Payroll`: `PayBasis {Daily, Monthly}`, `AttendanceMode {Recorded,
Declared}` (`PayrollSettings.cs:6-21`); `AllowanceDef` (fixed or % of base, `SubjectToCnas`,
`SubjectToIrg`; `:42-63`); `OvertimeRuleDef` (band, multiplier; `:66-73`); `TaxBracket` (`:80-85`);
`PayrollSettings` (rates, brackets, minimum wage, allowances, overtime, default attendance mode,
`WorkingDaysPerMonth = 26`, `HoursPerDay = 8`, `NeedsReview`; `:102-147`); a validator (`:149-215`);
`PayrollPresets.Create("DZ")` with CNAS 9/26, an empty IRG scale, SNMG 0, `NeedsReview = true`
(`PayrollPresets.cs:52-94`); `PayrollOwnership.Fields` naming each field's owner and refusing a
company edit of a statutory field (`PayrollOwnership.cs:39-56, 68-88`). Eighteen tests. **Nothing
persists, serves or reads any of it.** The single seeded account `630` (`LedgerEntities.cs:251,
293`) and `CashMovementKind.Disbursement = 4` (`CashEntities.cs:44-45`) are declared and written by
nobody. This section gives all of it a reader or retires it; leaving either ambiguous is the fifth
dead-wiring bug already in the tree.

The spec that binds: §8.2 (pay basis per employee; both attendance modes, company default with
per-employee override; admin-defined allowances and overtime; large headcount → batch run
draft → approved → posted with reversal, bulk attendance, **one ledger entry per run per branch**,
no N+1); §8.3 (User is a login, HR is from zero); §5.9 (employees and payroll never replicate to
tills). §7 decision 7 (monthly ÷ 30) is a **rental** rule; payroll uses the company-owned
`WorkingDaysPerMonth`.

### 5.2 Where the statutory figures live, and who may touch them

> **Superseded in part on 2026-09-24 (built).** The owner decided the module ships with
> **placeholder** figures that he edits himself from the back office, so the statutory half is
> reached by a **named permission an Admin holds by role**, not by configuration. What this section
> says about `PayrollConfigurations`, the lazy creation, the store and the statutory-only PUT is
> what was built. What is **struck**: the `Grantable`/`Configured` split of `Permissions.All`
> (there is one list), `Payroll:StatutoryEditors` and the whole configured-grant paragraph (there
> is no `Payroll` configuration section), and the third bullet below. The row has no
> `StatutoryReviewedBy`/`StatutoryReviewedAtUtc` either — `Confirmed`/`ConfirmedBy`/`ConfirmedAtUtc`
> live inside the settings JSON, with `NeedsReview => !Confirmed` derived from them. The login
> response was **not** changed; `GET /api/hr/settings` returns `canEditCompany`/`canEditStatutory`
> instead. The protection that replaces the wall: `PayrollOwnership`'s split is unchanged and is now
> what the "not confirmed by an accountant" mark is computed from, and everything computed against
> unconfirmed figures says so on screen and on paper.

The operator half — CNAS rates, IRG scale, SNMG — has no writer in the deployed shape: the platform
portal needs hosted SSO and a separate hostname (`Program.cs:131-135`), `/api/platform` is 404 on
company hosts (`:357`), and the single-company tenant is built from root configuration
(`:42-56`). Without a defined home a builder will either let the company admin edit statutory
figures or bury them in `appsettings` with no validation and no audit — the mistyped bracket the
validator was written to catch.

* **One server-only row, `PayrollConfigurations`**: `Id`, `SettingsJson` (the serialised
  `PayrollSettings`), `Revision`, `UpdatedBy`, `UpdatedAtUtc`, `StatutoryReviewedBy?`,
  `StatutoryReviewedAtUtc?`. Created lazily from `PayrollPresets.Create(regional.CountryCode)`, or
  for a country with no preset from an empty `PayrollSettings` with `NeedsReview = true` and
  `IncomeTaxMode = NotConfigured`.
* **Three named permissions** added to `Permissions`, and `Permissions.All` **split in two**:
  `Grantable` (what the users screen may write) and `Configured` (what only configuration sets),
  with `All = Grantable ∪ Configured` kept for `Parse` and `For`. Today `Parse` drops unknown names
  (`Permissions.cs:52-58`) and `Permissions.Store` keeps anything in `All`
  (`UsersEndpoints.cs:54, 85`), so a name that is merely added to `All` becomes grantable by the
  company Admin through `PUT /api/users/{id}` — which is exactly what §8.2 of the redesign forbids
  for statutory figures.
  `ManagePayroll = "payroll.manage"` — *Grantable*, auto-granted to Admin in `For`, like
  `ViewOtherBranchStock`.
  `EnterAttendance = "hr.attendance"` — *Grantable* (foremen who enter sheets and must not see
  wages).
  `EditPayrollStatutory = "payroll.edit_statutory"` — *Configured*, **never auto-granted, never
  writable by the users routes**: `Permissions.Store` takes the `Grantable` list, so a request
  carrying the name is stored without it (and the users tab does not offer it), and a test pins
  that a company Admin cannot grant it to anyone, including themselves. It is held by the
  usernames listed under `Payroll:StatutoryEditors` in configuration (`Payroll__StatutoryEditors__0`
  in the compose `.env`), read at token issue in `Permissions.For(user, options)` so no row is
  touched; or — if the owner picks "the accountant's own login" in Q18 — by a **one-time configured
  grant** the same section performs at startup (`Payroll:StatutoryEditors` still names the user; the
  claim is minted from configuration either way, the difference is only whether the accountant
  signs in with their own account or the deployer's). Either shape keeps the figure out of the
  hands of any login the company Admin can create.
* ~~**This is how the earlier direction is honoured.**~~ *(Struck 2026-09-24 — see the note at the
  head of this section. In a one-company copy the owner *is* the operator, and he asked for the
  figures on his own control panel.)* `project-memory.md` ("Earlier direction",
  a hosted service where the platform operator owns the statutory half) and §8.2 of the redesign
  (statutory settings belong to the super admin, not company admins) still hold: in a one-company
  copy there is no platform operator with a screen, so **the operator's ownership is exercised by
  whoever controls the configuration**, not by the company Admin. `PayrollOwnership`'s refusal
  message ("set by the platform operator") stays true — the deployer is that operator here.
* `ParadisERP.Infrastructure/Payroll/PayrollSettingsStore`: `ReadAsync()`,
  `ApplyCompanyEditAsync(incoming, revision, by)` → `PayrollOwnership.ApplyCompanyEdit`,
  `ApplyStatutoryEditAsync(incoming, revision, reviewed, reason, by)`; a stale revision is a 409.
  **The statutory route accepts only statutory fields.** `PayrollOwnership.ApplyOperatorEdit`
  (`PayrollOwnership.cs:99-107`) clones whatever it is given and would let the statutory editor
  rewrite the company half in the same call; the store therefore builds the candidate as *current
  settings with only the fields `PayrollOwnership.Fields` marks operator-owned copied from the
  request*, and only then calls `ApplyOperatorEdit`. A test asserts a statutory PUT that also
  changes an allowance leaves the allowance as it was. When the platform is enabled later,
  `PUT /api/platform/companies/{id}/payroll-statutory` mirrors `…/regional`
  (`PlatformEndpoints.cs:49`) and calls the same store method.
* The login response carries the session's effective permissions (`auth.service.ts:101` holds
  the role only) so the screen can say which half is editable; the server enforces regardless.

### 5.3 Income tax gets a tri-state; "empty scale = zero tax" stops being a meaning

> **Built, with one sentence superseded (2026-09-24).** The tri-state, the refusal and
> `IncomeTax.Progressive` are as written. But the DZ preset no longer ships `NotConfigured` with an
> empty scale: after the placeholder-figures decision it ships `Scale` with three **round,
> arbitrary** placeholder brackets, all unconfirmed. A country with **no** preset still gets an
> empty `PayrollSettings` with `NotConfigured` — that is the state the engine refuses on, and it is
> the right one for a jurisdiction nobody has described.

Today an empty `IncomeTaxBrackets` passes validation and `PayrollPresets.cs:38-43` defines it as
"no tax is withheld" — §8.2 of the redesign repeats that sentence. The owner's instruction for this
phase is that **the run must refuse to compute IRG while the scale is empty and never zero it**.
But a jurisdiction, or a company entirely below threshold, may legitimately withhold nothing, and
that has to be a deliberate statement, not the absence of one.

* `IncomeTaxMode { NotConfigured = 0, Scale = 1, None = 2 }` on `PayrollSettings`, operator-owned,
  registered in `PayrollOwnership.Fields` and `StatutoryDifferences`; the DZ preset ships
  `NotConfigured`. Validator: `Scale` requires ≥ 1 bracket with the existing ordered/open-top rule;
  `NotConfigured` and `None` require an **empty** list (a scale nobody applies is a scale nobody
  checks).
* `PayrollEngine.Refusal(settings)` → `RunRefusal { Code: "income_tax_not_configured",
  MissingFields: ["IncomeTaxBrackets"], FixableBy: "payroll.edit_statutory" }` when
  `NotConfigured`; compute answers 409 with that payload and the run stays Draft. `None` computes
  IRG = 0 with no warning. `Scale` applies `IncomeTax.Progressive(taxBase, brackets)` — **new
  code**: nothing in `ParadisERP.Application/Payroll` computes tax today, only `TaxBracket` and its
  validator exist. It is a pure static in `IncomeTax.cs`: walk the ordered brackets, tax the slice
  of `taxBase` inside each at its rate, the open-top bracket takes the remainder, round once at the
  end to `currencyDecimals`; a `taxBase ≤ 0` returns 0. Its tests use **synthetic brackets only**
  (e.g. 10 % to 1,000 then 50 %), so no real scale ever appears in the repository.
  **`NeedsReview` never refuses** — it marks the run and every payslip "provisional", in words.
* Optional statutory hooks, null by default, all operator-owned: `SocialBaseFloor?`,
  `SocialBaseCeiling?` (⚠ accountant: whether the contribution base has a floor or ceiling — Q21);
  `List<LevyDef> EmployerLevies` (key, labels, rate %; ⚠ accountant: any employer levy beyond
  social security and its rate — Q22). `SubjectToCnas`/`SubjectToIrg` added to `OvertimeRuleDef`,
  default true (⚠ accountant confirms — Q23), so overtime enters the bases by the same explicit flag
  as allowances.
* The comment at `PayrollPresets.cs:38-43`, the preset, `Every_setting_has_a_declared_owner` and
  `The_preset_refuses_to_invent_the_figures_that_move_with_the_finance_law` move together, and the
  §8.2 sentence "An empty scale withholds nothing" is superseded by this section when the mode lands.

Tests (`PayrollSettingsTests` extended): `A_scale_mode_with_no_brackets_is_rejected`,
`Not_configured_and_none_modes_must_carry_no_brackets`,
`Income_tax_mode_and_the_social_base_bounds_are_owned_by_the_operator`. `PayrollEngineTests`:
`The_run_refuses_to_compute_while_the_tax_scale_is_not_configured_and_says_what_is_missing`,
`An_explicit_no_withholding_mode_withholds_nothing_and_is_not_a_refusal`,
`Income_tax_is_progressive_over_the_configured_brackets` — **with obviously synthetic brackets**
(e.g. 10 % to 1,000 then 50 %), so a test never becomes the place a "real" scale is asserted.

### 5.4 Entities — `ParadisERP.Domain/HrEntities.cs`, none deriving from `SyncEntity`

The mechanism that keeps renting and the debtor book off the tills is the base type; one word in a
class declaration would replicate salaries to every cashier's IndexedDB. A reflection test asserts
it for every entity here.

| Entity | Columns (types per the project's conventions: Guid, decimal 18,3, DateTime UTC, enums int) | Indexes / rules |
|---|---|---|
| `Employee` | `Number` (40, `EMP-000001` via `Numbered`), `FullName` (200), `NationalId?` (40; generic "identity number"), `SocialSecurityNumber?` (40; labelled from `SocialNames`, no meaning attached), `BirthDate?`, `Phone?` (60), `Address?` (400), `BranchId` FK Restrict, `JobTitle?` (120; text — no grade table), `HiredOn`, `LeftOn?`, `PayBasis`, `BaseRate` (monthly salary or daily rate per basis), `PayoutMethod {Cash, Bank}`, `BankAccount?` (60), `AttendanceModeOverride?` (null = company default), `IsActive`, `Notes?` (500), `CreatedAtUtc`, `UpdatedAtUtc` | `Number` unique; `(BranchId, IsActive)`; `FullName`. No `UserId` (nothing would read it). No delete: a paid person is not un-personed; leaving = `LeftOn` |
| `EmployeeAllowance` | `EmployeeId` FK Cascade, `AllowanceKey` (30), `Value`, `IsEnabled` | unique `(EmployeeId, AllowanceKey)`; overrides the definition's value |
| `AttendanceDeclaration` | `EmployeeId`, `PeriodYear`, `PeriodMonth`, `DaysPresent`, `PaidLeaveDays`, `UnpaidAbsenceDays`, `UpdatedBy` (200), `UpdatedAtUtc` | unique `(EmployeeId, PeriodYear, PeriodMonth)`; child `AttendanceDeclarationOvertime (DeclarationId Cascade, RuleKey 30, Hours)` unique `(DeclarationId, RuleKey)` |
| `AttendanceDay` | `EmployeeId`, `WorkDate` (DateOnly — EF 8 maps it to TEXT / `date`; if Pomelo scaffolds it oddly, `DateTime` at midnight with a comment), `Status {Present, Absent, PaidLeave, UnpaidLeave, WeeklyRest, PublicHoliday}`, `HoursWorked?`, `UpdatedBy`, `UpdatedAtUtc` | unique `(EmployeeId, WorkDate)`; child `AttendanceDayOvertime (DayId Cascade, RuleKey, Hours)` |
| `ClockEvent` | `EmployeeId`, `AtUtc`, `Kind {In, Out}`, `Source` (40: back-office / device), `CreatedBy` | `(EmployeeId, AtUtc)` |
| `WageAdvance` | `Number` (`ADV-`), `EmployeeId` FK Restrict, `BranchId` (employee's branch at grant), `Amount`, `GrantedAtUtc`, `PayoutMethod`, `CashBoxId?`, `Recovered` (running), `Status {Open, Recovered, WrittenOff}`, `RecoveredInRunId?`, `Reference?` (100), `Note?` (400), `CreatedBy` | `Number` unique; `(EmployeeId, Status)` |
| `PayrollRun` | `Number` (`RUN-`), `BranchId` FK Restrict, `PeriodYear`, `PeriodMonth`, `Status {Draft, Locked, Approved, Paid, Reversed}`, `SettingsSnapshotJson` (**the settings it computed with** — a later law change never moves a closed month), `CurrencyDecimals`, `HeadCount`, `Gross`, `SocialBase`, `SocialEmployee`, `SocialEmployer`, `TaxBase`, `IncomeTax`, `EmployerLevies`, `AdvancesRecovered`, `OtherDeductions`, `Net`, `IsProvisional` (= snapshot `NeedsReview`), `ComputedAtUtc?`, `LockedBy?`, `ApprovedAtUtc?`, `ApprovedBy?`, `PaidAtUtc?`, `LedgerEntryId?`, `ReversalEntryId?`, `CreatedBy`, `CreatedAtUtc` | **one non-Reversed run per `(BranchId, PeriodYear, PeriodMonth)`**, see the rule below the table; plain index `(BranchId, PeriodYear, PeriodMonth)` on both providers |
| `PayrollRunLine` | `RunId` Cascade, `EmployeeId`, snapshots `EmployeeNumberSnapshot`, `EmployeeNameSnapshot`, `SocialNumberSnapshot`, `PayBasis`, `BaseRate`; `DaysPresent`, `PaidLeaveDays`, `UnpaidAbsenceDays`, `AttendanceSource`; `Gross`, `SocialBase`, `SocialEmployee`, `SocialEmployer`, `TaxBase`, `IncomeTax`, `AdvanceRecovered`, `OtherDeductions`, `Net`, `PaidAmount`, `WagePaymentId?`, `WarningsJson` | unique `(RunId, EmployeeId)` — the index that pages the lines. Snapshots follow the `NameSnapshot` rule: an employee edit never rewrites a payslip |
| `PayrollRunLineElement` | `LineId` Cascade, `Key` (30), `Kind {Base, Absence, Allowance, Overtime, Deduction, Adjustment}`, `Qty?`, `Rate?`, `Amount` (signed), `InSocialBase`, `InTaxBase`, `SortOrder` | |
| `PayrollAdjustment` | `RunId` Cascade, `EmployeeId`, `Key`, `Amount` (signed), `InSocialBase`, `InTaxBase`, `Reason` (400, required), `CreatedBy` | an **input** re-read on every compute, not an element |
| `WagePayment` | `Number` (`WP-`), `RunId` FK Restrict, `BranchId`, `PaidAtUtc`, `PayoutMethod`, `CashBoxId?`, `Amount`, `Reference?`, `Note?`, `CreatedBy`, `LedgerEntryId?` | `Number` unique; child `WagePaymentLine (WagePaymentId Cascade, RunLineId Restrict, Amount)`, one per run line enforced in code |
| `PayrollStatutoryPayment` (may ship later) | `BranchId`, `AccountCode ∈ {431, 442, 447}`, `Amount`, `PayoutMethod`, `CashBoxId?`, `Reference?`, `LedgerEntryId?` | the small document that lets the accountant record paying the social fund or the tax office from the bank, so the liabilities do not read as debt forever |

**One live run per branch and month, and a reversed month is recoverable.** A plain unique index
over `(BranchId, PeriodYear, PeriodMonth)` would make a month unrecoverable after Reverse: the
Reversed run must stay (it holds `LedgerEntryId` and `ReversalEntryId`, the audit trail of what
was posted and undone) and the row it occupies would block the new Draft. So uniqueness is over
**non-Reversed runs only**, enforced in code inside the write scope (`Create` refuses when a run
for the period exists with `Status != Reversed`), and backed by a **filtered unique index where
the provider supports it** — SQLite does (`HasFilter("Status <> 4")`, the SQLite migration only);
MySQL 8 has no partial indexes, so there the code check stands alone and the plain index above
keeps the lookup cheap. `HrMigrationTests` asserts the SQLite filter exists and
`PayrollRunTests` asserts the rule on both providers through the harness.

**An employee whose branch changes mid-period is paid whole by the branch that holds them at
compute time.** Compute reads `Employee.BranchId` when it runs and puts the whole month's line in
that branch's run; `PayrollRunLine` snapshots the branch with the name. There is no cost split
between the two branches (Q30 offers it as the alternative — it needs a dated branch history the
employee has no room for today). Because a recompute re-reads the branch, an employee moved
*after* one branch's run was Locked and *before* the other's is computed is caught by the
`(RunId, EmployeeId)` uniqueness within a run and by a compute-time check that the employee is not
already on a non-Reversed run for the same period — a warning naming the other run, and the line
is left out.

Document numbers `EMP-`, `ADV-`, `RUN-`, `WP-` come from a shared
`ParadisERP.Infrastructure/Documents/DocumentNumbers.Next(prefix, count)` (`{prefix}-{n:D6}`) —
today the same one-liner is a `private static Numbered` in both `RentalDocuments.cs:1147` and
`ReceivableDocuments.cs:702`, and a third copy is where the shapes start to drift. The helper is
extracted first (backlog item 19) and the two existing callers switched with no behaviour change.
**The `CountAsync + 1` stays inside `BeginWriteScopeAsync`**, as it is today for CTR/ISS/RET/INV:
the write scope serialises document writers on the sync-counter row, which is what keeps two runs
created in the same second from sharing `RUN-000012`.

`AttendanceModes.Resolve(employee.AttendanceModeOverride, settings.DefaultAttendanceMode)` in
Application/Payroll is the one place the mode is decided, read by the sheet screen and the run.
A per-branch default (Q14) would be **one nullable column on `Branches` in its own migration with
its own backfill thought** — not part of this migration.

### 5.5 Attendance: two capture modes, one summary shape

Recorded (clock pairs or per-day ticks) and Declared (a foreman's period totals) produce different
data; if the engine read both shapes every rule would be written twice, and the daily basis —
which cannot run without days — would take its number from whichever path happened to be filled.

* `ParadisERP.Application/Payroll/AttendanceSummarizer.cs`, pure:
  `record AttendanceSummary(DaysPresent, PaidLeaveDays, UnpaidAbsenceDays,
  IReadOnlyDictionary<string, decimal> OvertimeHoursByRule, AttendanceSource {Declared, Recorded,
  None})`; `FromDeclaration(...)`, `FromDays(...)`, `PairClockEvents(events, TimeZoneInfo) →
  (shifts, unpaired)` — **an unpaired In is reported, never guessed**.
* **A period is a calendar month in the company time zone** (`Regional.TimeZone`), stored as
  `(Year, Month)` ints — never a UTC `DateTime` that drifts a day in Africa/Algiers. A shift
  crossing midnight belongs to the day it started in.
* **Period lock**: any attendance write for a `(branch, period)` whose run is Locked, Approved or
  Paid is refused 409 — "unlock the run first".

Tests: `AttendanceSummaryTests` (`A_declared_total_and_a_recorded_month_reduce_to_the_same_summary_shape`,
`Clock_events_pair_into_hours_and_an_unpaired_in_is_reported_not_guessed`,
`A_shift_crossing_midnight_belongs_to_the_day_it_started_in_the_company_time_zone`,
`Overtime_hours_are_kept_per_band`, `A_period_is_a_calendar_month_in_the_company_time_zone_not_in_utc`);
`AttendanceSheetTests` (`The_sheet_upsert_replaces_a_row_and_never_duplicates_it`,
`Attendance_for_a_period_whose_run_is_locked_is_refused`,
`A_foreman_with_the_attendance_permission_can_write_a_sheet_and_cannot_read_a_run`).

### 5.6 The engine — pure functions, in this order

`ParadisERP.Application/Payroll/PayrollEngine.cs`, checkable without a server, on the pattern of
`RentalFifo`. `EmployeeInput(EmployeeId, Basis, BaseRate, Allowances, AttendanceSummary,
OpenAdvances, Adjustments)`; `Refusal(settings)`; `Compute(settings, inputs, currencyDecimals)`;
`ComputeOne(...)`. Every element is rounded to `currencyDecimals` first and totals are built from
rounded elements, so Σ lines == run totals to the centime.

> **Built 2026-09-24, with two corrections.** The signature takes a `RegionalSettings`, not an int:
> `Compute(PayrollSettings settings, RegionalSettings regional, IEnumerable<EmployeeInput> inputs)`,
> because rounding goes through `RegionalSettings.RoundMoney` exactly as `RentalPricing` does; the
> run's `CurrencyDecimals` column is `regional.CurrencyDecimals`. And `EmployeePayroll.Gross`
> **excludes** deduction elements, which come back as `OtherDeductions` (a positive total whose
> elements are signed negative) — step 10 below and §5.9's accrual identity cannot both hold
> otherwise. `PayrollPosting.Accrual` debits 631 with `Gross` and handles `OtherDeductions`
> separately; do not re-add them.

| Step | Rule | Source of the figure |
|---|---|---|
| 1 Base | **Monthly**: `BaseRate − BaseRate × UnpaidAbsenceDays ÷ WorkingDaysPerMonth`. **Daily**: `BaseRate × (DaysPresent + PaidLeaveDays)` | `WorkingDaysPerMonth` is the company's (26 by default; ⚠ accountant: 26, 30 or 22) |
| 2 Allowances | fixed, or % of the step-1 base; a per-employee override wins; a disabled one pays nothing; each carries `InSocialBase`/`InTaxBase` from its definition | which allowance is in which base: ⚠ accountant (Q23) |
| 3 Overtime | `hours × hourlyRate × multiplier`; `hourlyRate = dailyRate ÷ HoursPerDay`; `dailyRate = BaseRate` (daily) or `BaseRate ÷ WorkingDaysPerMonth` (monthly) | multipliers: company-defined bands; base flags ⚠ accountant |
| 4 Deductions | company-defined `DeductionDef { Key, Labels, Fixed \| PercentOfBase, Value, ReducesSocialBase, ReducesTaxBase, Treatment { ReducesWageCost, Other } }`. `Other` is a placeholder posting (§5.9) until the accountant names its account — there is **no `OtherIncome` treatment** in this phase | treatment per deduction and the account for `Other`: ⚠ accountant (Q23, Q24) |
| 5 Social base | Σ elements with `InSocialBase`, clamped to `[SocialBaseFloor, SocialBaseCeiling]` **only when set** | floor/ceiling: ⚠ accountant (Q21) |
| 6 Contributions | `SocialEmployee = SocialBase × SocialEmployeeRate %` (withheld); `SocialEmployer = SocialBase × SocialEmployerRate %` (a cost, never a deduction) | the DZ preset's 9 / 26 — the one statutory figure shipped, and it stays under `NeedsReview` |
| 7 Tax base | Σ elements with `InTaxBase`, **minus `SocialEmployee` only when `EmployeeContributionReducesTaxBase` is true** — an operator-owned boolean on `PayrollSettings`, default `true`, registered in `PayrollOwnership.Fields`; the engine asserts nothing about it, it reads the flag | ⚠ accountant: the flag (Q20 asks it beside the abatement); and whether an abatement (a percentage with floor and ceiling) sits between this and the scale — if so, an operator-owned relief step is added here; the scale alone cannot express it (Q20) |
| 8 Income tax | per `IncomeTaxMode` (§5.3) | the scale: ⚠ accountant, entered under `payroll.edit_statutory` |
| 9 Employer levies | `SocialBase × rate` for each configured levy | ⚠ accountant (Q22) |
| 10 Net before advances | `Gross − SocialEmployee − IncomeTax − deductions` | |
| 11 Advances | `AdvanceRecovered = min(open, max(0, NetBeforeAdvance))`; the remainder stays Open | whole balance vs instalment: Q15 |
| 12 Net | `NetBeforeAdvance − AdvanceRecovered`, **never below zero**; a warning line names the clamp | |

**Warnings, never refusals**: a daily worker with zero days (paid zero, flagged); a monthly employee
with `Source = None` (paid in full and flagged — or held, Q17); net below `MinimumWage` when
`MinimumWage > 0`, basis Monthly, no unpaid absence; a clamped net. **The only refusal is the tax
mode** (§5.3). CLAUDE.md's first rule — never block — has a payroll reading: the run must always be
able to say what it would pay and why, so the accountant can fix the input.

Tests (`PayrollEngineTests`, synthetic figures only):
`A_monthly_employee_is_paid_the_base_less_unpaid_absence_at_the_working_day_rate`,
`A_daily_worker_is_paid_days_present_times_the_daily_rate_and_nothing_else`,
`A_daily_worker_with_no_attendance_is_paid_zero_and_flagged_not_refused`,
`Allowances_enter_the_social_and_tax_bases_only_when_flagged_and_a_percentage_follows_the_base`,
`Overtime_multiplies_the_hourly_rate_by_the_band`,
`The_employee_share_is_withheld_and_the_employer_share_is_a_cost_not_a_deduction`,
`The_social_base_respects_a_floor_and_ceiling_only_when_they_are_set`,
`An_advance_is_recovered_up_to_the_net_and_the_remainder_stays_open`,
`Net_pay_never_goes_below_zero_and_says_so`,
`A_wage_below_the_minimum_is_a_warning_not_a_refusal_and_zero_turns_the_check_off`,
`Elements_round_to_the_currency_decimals_and_sum_to_the_totals`,
`A_deduction_reduces_the_bases_only_when_flagged`,
`The_employee_contribution_leaves_the_tax_base_only_when_the_flag_says_so`,
`Employer_levies_are_a_cost_and_never_touch_the_employees_net`.

### 5.7 The run — one document per branch per month

`ParadisERP.Infrastructure/Payroll/PayrollDocuments`, on the pattern of `RentalDocuments`, every
write inside `db.BeginWriteScopeAsync` because `SubscriptionWriteFilter` already opened the
transaction. **The filter holds the sync-counter row for the whole handler**, so compute must be
short: bulk loads, in-memory arithmetic, no per-line `SaveChanges` — or every till pushing at
month-end stalls.

| Transition | Rule |
|---|---|
| Create | refused if the branch is inactive or a **non-Reversed** run exists for `(branch, period)` (§5.4); after a Reverse a new Draft may be created and the Reversed run stays with its `LedgerEntryId` / `ReversalEntryId` |
| Compute (Draft or Locked → Locked) | **six bulk queries**: employees active in the period (hired ≤ period end, `LeftOn` null or ≥ period start), allowances, declarations, days + overtime for Recorded employees, open advances, adjustments → `Compute` in memory → previous lines removed with `ExecuteDeleteAsync` → lines and elements added with `AutoDetectChanges` off. Recompute while Locked re-snapshots the **current** settings |
| Unlock (Locked → Draft) | attendance edits become possible again |
| Approve (Locked → Approved) | posts the accrual (§5.9), stamps `LedgerEntryId`, closes the advances recovered |
| Pay (Approved → Paid) | §5.10; Paid when every line's `PaidAmount == Net` |
| Reverse (Approved → Reversed, reason required) | `LedgerPoster.ReverseAsync`, `ReversalEntryId` stamped, advances reopened; the row keeps both entry links and no longer counts against the one-live-run rule, so the month can be created and computed again. **A Paid run is final** and is corrected by adjustments in the next run — the nothing-is-edited rule the ledger and invoices already follow |

Tests (`PayrollRunTests`, harness): `Two_live_runs_for_the_same_branch_and_period_are_refused`,
`Compute_is_refused_while_the_tax_scale_is_not_configured_and_the_run_stays_draft`,
`A_run_snapshots_the_settings_it_computed_with_and_a_later_change_does_not_move_it_until_recomputed`,
`Recompute_replaces_the_lines_and_never_duplicates_them`,
`A_run_of_nine_hundred_employees_computes_with_a_bounded_number_of_queries` (a command-counting
`DbCommandInterceptor`), `A_paid_run_cannot_be_reversed_and_an_approved_one_can`,
`Approving_a_draft_run_is_refused`, `Editing_an_employee_after_a_run_does_not_change_the_run_line_snapshot`,
`A_reversed_month_can_be_run_again_and_the_reversed_run_keeps_its_ledger_link`,
`An_employee_moved_to_another_branch_mid_month_is_paid_whole_by_the_branch_holding_them_at_compute_time`.

### 5.8 Advances (سلفة): a cash-out document today, an automatic deduction next run

Site labour draws cash mid-month; nothing records money leaving a box for a person, and the next
payslip has nothing to deduct — the Z-report is short with no name against it.

`GrantAdvanceAsync`: `BeginWriteScopeAsync`; cash → one `CashMovement` Out, **`Kind = Disbursement`
(its first real writer)**, `SourceType = "wage-advance"`, `BalanceAfter` from the client-side sum
(`TreasuryEndpoints.cs:297-304` pattern); **box short → warning, never refusal** (the remittance
precedent, `TreasuryEndpoints.cs:241-295`); posting `PayrollPosting.Advance`: Dr 425 (employee's
branch) / Cr 530 (paying box's branch) or Cr 512; `IsInterBranch` + `CounterpartBranchId` when the
two branches differ. Recovery is step 11 of the engine; approval credits 425 for the recovered
total and stamps `RecoveredInRunId`; reversal reopens. `WrittenOff` is a manual admin act with a
reason — **the route is left out until the accountant names the account that absorbs it (Q24)**.

Tests (`WageAdvanceTests`): `Granting_an_advance_takes_cash_from_the_chosen_box_and_debits_425_at_the_employees_branch`,
`An_advance_paid_from_another_branchs_box_is_marked_inter_branch`, `A_short_box_warns_and_does_not_refuse`,
`An_advance_is_recovered_in_the_next_run_up_to_the_net_and_the_remainder_stays_open`,
`Reversing_the_run_reopens_the_advance`, `Cash_position_and_425_agree_after_grant_run_and_payment`.

### 5.9 Postings — the accounts and the one entry per branch

The chart has a single `630 Charges de personnel` heading and no liability for staff, the social
fund or withheld tax; `LedgerPoster` throws on an unknown code (`LedgerPoster.cs:148-150`). New
system accounts enter every company through `CoreDataSeeder.EnsureLedgerAccountsAsync`
(`CoreDataSeeder.cs:37-62`), which adds missing codes on every startup and leaves existing rows
alone — **no `InsertData` migration**. P&L and balance sheet group by `LedgerAccount.Type`
(`LedgerReports.cs:81, 126-127`), so the new accounts appear in the reports without a change there.

| Code | Constant | Type | Name (ar / fr / en) — carries no rate, currency or fiscal label |
|---|---|---|---|
| `421` | `WagesPayable` | Liability | أجور مستحقة للمستخدمين / Personnel — rémunérations dues / Wages payable to staff |
| `425` | `StaffAdvances` | Asset | تسبيقات للمستخدمين / Personnel — avances et acomptes / Advances to staff |
| `431` | `SocialSecurityPayable` | Liability | الضمان الاجتماعي / Organismes sociaux / Social security payable |
| `442` | `IncomeTaxWithheld` | Liability | ضرائب مقتطعة من المنبع / État — impôts retenus à la source / Income tax withheld payable |
| `447` | `OtherPayrollTaxesPayable` | Liability | ضرائب ورسوم أخرى مستحقة / État — autres impôts et taxes / Other payroll taxes payable |
| `631` | `Wages` | Expense | أجور المستخدمين / Rémunérations du personnel / Wages and salaries |
| `635` | `EmployerSocialContributions` | Expense | اشتراكات الهيئات الاجتماعية / Cotisations aux organismes sociaux / Employer social contributions |
| `641` | `EmployerPayrollTaxes` | Expense | ضرائب ورسوم على الأجور / Impôts et taxes sur rémunérations / Employer taxes on wages |

`630` stays seeded (system accounts are never removed or recoded), documented in
`docs/03-work-status.md`'s chart table as "heading — nothing posts", and `PayrollExpense` is kept
with `[Obsolete("Heading; post to Wages (631) or EmployerSocialContributions (635).")]`. **⚠
accountant confirms the eight codes before the first run posts** — they follow the classes the
chart already uses, and once posted to they cannot change (Q24). A startup guard in the seeder
asserts `Type` on every system code present and logs an error naming the code if an admin had
already created, say, `421` with another type (`LedgerEndpoints.cs:66-80` lets them).

`LedgerSources`: `PayrollRun = "payroll-run"`, `WagePayment = "wage-payment"`,
`WageAdvance = "wage-advance"`, `PayrollStatutoryPayment = "payroll-statutory-payment"`.

`ParadisERP.Infrastructure/Ledger/PayrollPosting.cs`, pure and static like `DocumentPosting`:

| Entry | Debit | Credit |
|---|---|---|
| **Accrual** — one per run, per branch, at Approve | 631 Gross (allowances and overtime included, net of `ReducesWageCost` deductions); 635 SocialEmployer; 641 EmployerLevies (if any) | 431 SocialEmployee + SocialEmployer; 442 IncomeTax; 447 EmployerLevies (if any); 425 AdvancesRecovered (if any); **641 for deductions with `Treatment = Other`** (if any) — ⚠ a placeholder, see below; **421 Net** |
| **Payment** | 421 amount paid | 530 (cash — the branch box's account) or 512 (bank) |
| **Advance** | 425 at the employee's branch | 530 at the paying box's branch, or 512 |
| **Statutory payment** | 431 / 442 / 447 | 512 or 530 |

**⚠ accountant — deductions that are not a wage-cost reduction.** An earlier draft credited such
deductions (fines, dues collected for a third party) to `758`; in this chart `758` is
`RoundingDifference` (`LedgerEntities.cs:263, 302`) and posting anything else there would bury a
control account. Until the accountant names the account (Q23 says which deductions are "other",
Q24 names where they go), a `Treatment = Other` deduction is posted as a **credit to `641`,
reducing the employer's payroll taxes line** — visibly wrong in the P&L if it is ever large, which
is the point: it cannot hide, the settings screen flags every `Other` deduction with "posting
placeholder — account not yet confirmed", and the run's action bar says so in its preview. When the
account is named it enters through the seeder like the eight above and the placeholder branch in
`PayrollPosting.Accrual` is replaced; nothing already posted moves.

The accrual balances by construction: `631 + 635 + (641 − OtherDeductions) = 431 + 442 + 447 + 425
+ 421` — written with 641 net on the debit side, so a negative 641 is possible and is exactly what
the placeholder makes visible — whenever `Gross − SocialEmployee − IncomeTax − deductions −
advances = Net`; a property-style test asserts it over random runs.

Tests: `PayrollPostingTests` (`The_accrual_balances_for_any_totals_the_engine_can_produce`,
`The_accrual_debits_wages_and_employer_contribution_and_credits_staff_social_tax_and_advances`,
`Employer_levies_post_only_when_configured`,
`Payment_by_cash_credits_the_branch_box_account_and_by_bank_credits_the_bank`); `PayrollRunTests`
(`A_run_posts_one_balanced_entry_per_branch_not_one_per_employee`, `Approving_twice_posts_once`,
`Every_account_code_payroll_posts_to_is_in_the_default_chart`,
`The_seeder_adds_the_payroll_accounts_to_an_existing_company_and_leaves_renamed_ones_alone`,
`A_branch_profit_and_loss_carries_its_own_wage_bill_and_the_group_sums_them`);
`LedgerArchitectureTests.No_posting_rule_references_the_630_heading`.

### 5.10 Paying wages — the first money-out document

Payday at a branch is the largest regular cash outflow the business has, over two days with some
workers absent on the first. Without a document either 421 is cleared at approval (the books say
staff were paid while the cash sits in the safe) or the box is short with nothing to explain it.

`PayAsync(runId, PayRequest { Method, CashBoxId?, EmployeeIds? (null = every unpaid line whose
employee's `PayoutMethod` matches), Reference, Note })`: Approved runs only; lines already paid are
skipped; `Amount` = Σ Net of the lines taken; cash → **one** `CashMovement` Out, `Kind =
Disbursement`, `SourceType = "wage-payment"`, box short → warning; posting `PayrollPosting.Payment`;
lines stamped; run → Paid when none remain. Several payments per run are normal. **Bank payments
write no `CashMovement`** — a bank is not a drawer (the receipt-voucher precedent,
`ReceivableDocuments.cs:445-452`). A payment is never edited: `POST /payments/{id}/reverse`
reverses its entry, writes a mirror `CashMovement` In with `Kind = Adjustment` and a reason, and
returns the lines to unpaid.

This is specific to wages. The generic `PaymentVoucher` phase 5 left unbuilt — for the deposit
refund of §3.3 and the early-return refund — remains; it should share `Disbursement` and the shape
of `PayrollPosting.Payment` when it is built.

Tests (`WagePaymentTests`): `Paying_in_cash_leaves_the_branch_box_and_clears_421_for_the_paid_lines_only`,
`Paying_by_bank_moves_no_cash_box_and_credits_512`,
`A_second_payment_takes_only_the_lines_still_unpaid_and_the_run_becomes_paid_when_none_remain`,
`A_short_box_warns_and_does_not_refuse`, `Paying_a_draft_or_locked_run_is_refused`,
`Reversing_a_payment_puts_the_cash_back_and_reopens_the_lines`,
`Cash_movement_and_ledger_agree_after_advance_accrual_and_payment`;
`TreasuryTests.A_wage_payment_and_an_advance_appear_on_the_branch_cash_report_as_disbursements`.
No i18n work is needed for the kind: `treasury.kind.disbursement` already exists in fr/ar/en
(`translations.ts`, "Décaissement / صرف / Disbursement") and `treasury-tab.component.ts:324`
renders `treasury.kind.<kind>` for whatever `CashMovementKind` arrives — the test asserts the
existing label is what a movement written by `PayAsync` shows, not that a label was added.

### 5.11 Endpoints — `/api/hr`

`ParadisERP.API/Endpoints/HrEndpoints.cs`, `app.MapHrEndpoints()` after receivables. Group
`/api/hr` `.WithTags("HR").AddEndpointFilter<SubscriptionWriteFilter>().RequireAuthorization(RequireRole(Manager, Admin))`;
every sub-group additionally `RequireClaim(perm, ManagePayroll)` **except** the attendance sheet
routes, which accept `ManagePayroll` **or** `EnterAttendance` (a `RequireAssertion` policy).
`PayrollResult<T>` mirrors `ReceivableResult` (`ReceivableDocuments.cs:26-35`) with the same
`Problem` mapper; a compute refusal is 409 `{ code, missing, fixableBy }`. Anything that scales with
headcount is paged (`take` clamped 1..1000).

| Area | Routes |
|---|---|
| Settings | `GET /settings` → `PayrollSettingsDto(Settings, Revision, Owners, NeedsReview, CanEditStatutory)`; `PUT /settings/company` (Admin; refuses a statutory change, §8.2 of the redesign); `PUT /settings/statutory` (`EditPayrollStatutory`; `Reviewed`, `Reason` required; **only the operator-owned fields are read from the body, the company half is merged from the current row** — §5.2); `GET /settings/presets` |
| Employees | `GET /employees?branchId&active&q&skip&take`; `GET /employees/{id}`; `POST /employees`; `PUT /employees/{id}`; `POST /employees/{id}/leave` |
| Attendance | `GET /attendance/sheet?branchId&year&month` → rows (employee, basis, mode, days, leave, absence, overtime per rule, source, `ReadOnly` for Recorded) + `Locked`, `RunStatus`; `PUT /attendance/sheet` (one upsert per row in one scope; 409 when locked); `GET/PUT /attendance/days`; `POST /attendance/clock` (`EmployeeId` or `EmployeeNumber`, `Kind`, `AtUtc?`); `GET /attendance/clock` |
| Advances | `GET /advances?employeeId&branchId&status&take`; `GET /advances/{id}`; `POST /advances` → 201 `{ advance, warning }` |
| Runs | `GET /payroll/runs?branchId&year&status`; `POST /payroll/runs`; `GET /payroll/runs/{id}` (header, totals, warning **counts** — **no lines**); `GET /payroll/runs/{id}/lines?skip&take&q`; `PUT /payroll/runs/{id}/adjustments/{employeeId}` (Draft/Locked); `POST …/compute`, `…/unlock`, `…/approve`, `…/reverse`; `POST /payroll/runs/{id}/payments` → 201 `{ payment, warning }`; `GET /payroll/runs/{id}/payments`; `POST /payroll/payments/{id}/reverse`; `GET /payroll/runs/{id}/payslips?skip&take`, `…/payslips/{employeeId}` → `PayslipDto` with labels resolved from the run's snapshot in `lang`; `POST /payroll/statutory-payments` (may defer); `GET /payroll/summary?year&month` (per branch); `GET /payroll/runs/{id}/declaration` → per-employee social number, bases, contributions, tax — **data for the accountant's filing, no official format asserted** |

Tests (`HrEndpointTests`) — **every one of these needs the HTTP test host of backlog item 4**,
since they assert status codes and policies: `A_manager_without_the_payroll_permission_gets_403_on_runs`,
`A_foreman_with_the_attendance_permission_can_save_a_sheet_and_gets_403_on_runs`,
`A_company_admin_cannot_grant_payroll_edit_statutory_through_the_users_route`,
`A_statutory_put_that_also_moves_an_allowance_leaves_the_allowance_alone`,
`Compute_returns_409_with_the_missing_fields_while_the_scale_is_not_configured`,
`Run_lines_are_paged_and_two_pages_are_disjoint_and_complete`,
`A_write_that_fails_inside_the_filter_transaction_leaves_no_run_behind`. The engine, run, posting,
advance and payment tests of §5.6–5.10 do not need it — they drive `PayrollDocuments` on the
harness. `provider-smoke.cjs` extended with the HR chain (settings → employees → sheet → run →
compute → approve → pay → ledger lines) against MySQL.

### 5.12 Screens — a Personnel section

`features/backoffice/hr/`, `@defer (on immediate)` like rentals; nav group `backoffice.group.people`
(الموظفون / Personnel / Staff) before Setup, shown only when the session holds `payroll.manage` or
`hr.attendance`; `hr-api.service.ts` beside the screens so it stays in the deferred chunk. **i18n
prefix `hr.` — `pay.` is the till's payment dialog** (`translations.ts:81`). Every key in ar/fr/en;
`translations.spec.ts` enforces parity and refuses copied French. Chips: employees · attendance ·
payroll · advances · settings (attendance alone for `hr.attendance` holders). Design rules
throughout: logical properties, state in words beside its dot, text glyphs, secondary text inside
panels, money through `formatMoney` and the regional service, no currency or fiscal label literal.

1. **Employees** — search with `fold` from `core/product-search` (Arabic without harakat), branch
   filter, active toggle, 50-row client pager like products; row = number, name, branch, basis **in
   words**, base `mono num`, payout method, status text + dot. Form: identity (the social-security
   label from `SocialNames[lang]`), branch, dates, basis + rate whose label switches (daily rate /
   monthly salary), payout + bank account, attendance override with the company default shown as
   text, allowances table (definition default, override, enabled).
2. **Attendance sheet — the screen the module lives or dies on.** Branch + month; one `<table>`
   (no per-cell components): rows = employees, columns = days present · paid leave · unpaid · one
   per active overtime rule; each cell one `<input inputmode=decimal>`. Keyboard contract in a
   pure helper `hr/sheet-keys.ts`: Enter/↓ same column next row, ↑ previous, Tab/→ next column,
   Shift+Tab/← previous, typing replaces, **Ctrl+D fills the column downwards**, Esc restores.
   Changed cells tinted `--warn-soft` with an "n unsaved" text counter; Recorded rows read-only with
   "from clock records" and a link to the day grid; footer totals; one PUT on save; a locked period
   says "this month's payroll is locked — unlock the run to edit" and disables inputs. 100 rows
   per page, the edit buffer a `Map` kept across pages. Plus a **day grid** (one employee, one
   month, 31 status selects + hours) and a **clock** screen (a large employee-number box with the
   document-wide scan catch from `core/barcode-scan` for badge scans → `POST /attendance/clock`).
3. **Payroll runs** — list (branch, period, status in words, headcount, net `mono`) and the run:
   six stat tiles (gross, employee contribution, employer contribution, income tax, advances
   recovered, net) labelled from `SocialNames`/`IncomeTaxNames[lang]`; a "provisional" banner in
   words when `IsProvisional`; a **refusal panel** naming the missing statutory figure and who can
   set it; a warnings panel (zero-day workers, monthly staff with no attendance, clamped nets, below
   minimum) each linking to its line; the lines table paged server-side, a line expanding to its
   elements and adjustments; an action bar per state that **previews in words what it will post**
   ("will debit 631 and 635 at Oran and credit 421/431/442 — one entry"), mirroring the issue-note
   acknowledgement; a pay dialog (method, box preselected to the branch's, everyone-unpaid or a
   checklist, box balance beside the amount with the warning stated, never blocking).
4. **Payslip** — A4 through the §4.2 print route: store, branch, period, employee number/name,
   social-security number with its configured label, basis and days; elements with configured
   labels; contribution and tax lines; net `mono` weight 700; "provisional — statutory figures not
   yet confirmed" when applicable; signature line; "print all" streams 100 at a time. Test the
   preview at 900 payslips before calling it done.
5. **Advances** — list with open balance per employee; form (employee picker, amount, the
   employee's branch box preselected, note); the warning shown beside the document.
6. **Settings** — company half editable by Admin: allowances (fixed/percent, value, in contribution
   base, in tax base, active), overtime bands, deductions, working days/hours, attendance default;
   statutory half read-only with "set by the holder of payroll.edit_statutory" and the
   `NeedsReview` notice, editable — with a "checked against the current law" checkbox and a reason
   — only when the session holds the claim; the income-tax mode selector (not configured / scale /
   no withholding) with the bracket editor.

Tests: Karma `hr/sheet-keys.spec.ts` (`Enter_moves_down_the_same_column_and_wraps_to_the_next_page_boundary`,
`Ctrl_D_fills_the_column_downwards_from_the_current_cell`,
`Esc_restores_the_cell_and_clears_its_dirty_mark`, `Typing_into_a_read_only_recorded_row_is_ignored`);
`hr/payroll-view.spec.ts` (`Elements_group_by_kind_in_the_payslip_order`,
`Labels_resolve_from_the_run_snapshot_in_the_requested_language_and_fall_back_to_French`,
`A_provisional_run_renders_the_provisional_line`); parity covers every `hr.*` key. Playwright: a
sheet filled for 30 rows by keyboard only, Arabic RTL, 1366 and 390 px with no horizontal overflow;
run computed, approved, paid; payslip preview; screenshots under `artifacts/design/`.

### 5.13 Migrations — new tables only, zero ALTER

Defaults have bitten four times. This phase is shaped so the class of bug cannot occur: **it adds
tables and touches no existing one**. One migration `HrAndPayroll` scaffolded twice (SQLite in
`ParadisERP.Infrastructure/Persistence/Migrations`, MySQL in `ParadisERP.Migrations.MySql/Migrations`),
identical shape: Guid TEXT / `char(36) ascii_general_ci`; decimal TEXT (18,3) / `decimal(18,3)`;
bool INTEGER / `tinyint(1)`; DateTime TEXT / `datetime(6)`; DateOnly TEXT / `date`; enums int; JSON
TEXT / `longtext`. Every non-null column has a CLR default the entity sets (Status = Draft, IsActive
= true, Recovered = 0, PaidAmount = 0) so `EnsureCreated` tests and migrations agree. FKs as in
§5.4. Unique indexes only over non-nullable columns; the one filtered index (`PayrollRuns`, §5.4)
exists in the SQLite set only, and the MySQL set carries the plain index with the rule in code —
the one deliberate difference between the two sets, commented in both. The ledger accounts arrive through the seeder,
not the migration. `Branches`, `Users`, `Stores`, `LedgerEntryLines` and every replicated table are
**untouched**. Verify: SQLite through the suite; MySQL by `provider-smoke.cjs` from empty **and** by
rolling back one migration and re-applying on populated tables (the phase-3/5 procedure).

Tests: `HrMigrationTests.The_hr_migration_applies_to_a_populated_previous_release_sqlite_database_and_leaves_every_existing_row_untouched`
(pattern of `LedgerCustomerBackfillTests`), `The_model_snapshot_and_the_migration_agree`
(`HasPendingModelChanges` false on both providers);
`HrArchitectureTests.No_hr_or_payroll_entity_derives_from_SyncEntity`.

### 5.14 Risks specific to this phase

* Statutory figures written from general knowledge — every rate, bracket, floor, ceiling and
  base-inclusion flag above is the accountant's; tests use synthetic brackets; the only shipped
  figure remains the CNAS 9/26 split under `NeedsReview`.
* Liabilities 431/442/447 accumulate until the statutory-payment route exists; if it is deferred,
  the accounting screen must say the balance is paid from the bank outside the system rather than
  let it read as debt.
* Cash payout of hundreds of wages from one box will often exceed the recorded balance (the safe is
  refilled that morning): the design warns; a builder who "fixes" it into a refusal breaks the first
  rule.
* The `pay.` prefix, the `Grantable` / `Configured` split of `Permissions.All` (a statutory grant
  that lands in `Grantable` by a slip is exactly the hole §5.2 closes), the login response carrying
  permissions, the seeder's type guard — each is a place where forgetting one line fails silently.
* The `Treatment = Other` placeholder on 641 is meant to be replaced; if the accountant's answer
  to Q24 is slow, a builder must not "tidy" it into a permanent account of their own choosing.
* A seventh deferred section adds ~50 kB of dictionaries to an initial bundle already over budget;
  per-section dictionaries are a change to `I18nService`, not to these screens.

Effort: settings store + permissions + tri-state *day* and *hours*; entities + migrations *days*;
attendance *days*; engine + run *week*; postings *day*; advances *day*; payments *day*;
endpoints *days*; screens *week*. Payroll touches new files only, apart from the nav in
`backoffice.component.ts` and `translations.ts`, so a second builder can run it in parallel with §4.

---

## 6. Production runbook — one company, MySQL 8, about ten branches

### 6.0 Five things to land before the first real start

Each is *hours* except the third; together they make the runbook below safe to follow.

1. **A bootstrap admin for the single-company path.** With `Database__Provider=MySql` and no
   `Tenancy:Companies`, `Program.cs:44-54` builds the tenant from root config and `:292` calls
   `DbSeeder`, which writes `the historical shared demo administrator login` (PIN 1234), `caisse1/Caisse@123`, a demo store with
   fake fiscal ids, three branches, ten products, stock and opening-balance ledger entries
   (`DbSeeder.cs:26-140`) — and the ledger never deletes. Bind a `Bootstrap` section
   (`AdminUsername`, `AdminPassword` ≥ 12 chars, `SeedDemoData = false`) onto the single-company
   `TenantOptions`; `DbSeeder` on an empty `Users` table creates only admin, store, main branch
   and `C01` (reuse `CompanySeeder`'s body, `CompanySeeder.cs:19-29`); the demo rows only with
   `SeedDemoData` or in Development. **The throw is conditional on an empty `Users` table**: only
   when `Users` is empty *and* the environment is not Development *and* `Bootstrap:AdminPassword`
   is missing does startup throw, naming `Bootstrap__AdminPassword`. **When `Users` is non-empty
   no `Bootstrap__*` variable is required** and the section is not read — otherwise every existing
   deployment would lock itself out on the upgrade that ships this item. A test starts the host
   against a database with one user and no `Bootstrap` section and asserts it comes up; another
   against an empty database and asserts the named throw (both on the HTTP host of backlog
   item 4, or on a `DbSeeder` unit test if the fixture is not yet there — the seeder, not the host,
   owns the rule). The deployment scout's runbook bootstrapped through
   `Tenancy__Companies__0__*` until this lands; that path binds every request to the configured
   hostname (`TenantRegistry.cs:72`) and needs a `Host` header on every probe, so **the pick is to
   do this first** — it is hours, and it is the deployed shape the owner decided on.
2. **`/health` that knows about the database.** `AddHealthChecks()` registers nothing
   (`Program.cs:223`), so it answers Healthy with MySQL down; and it is mapped after the tenant
   middleware (`:385` vs `:345-374`). Add `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore`,
   `AddDbContextCheck<PosDbContext>("database")`, and let `/health` resolve the single company
   without a `Host` match. Dockerfile: install `curl` in the final stage and add
   `HEALTHCHECK --interval=30s --timeout=5s --start-period=90s`.
3. **Backup and restore scripts** (*day*) — there is nothing in the repository today (grep over
   docs, deploy, scripts, tests, CI for backup/mysqldump: none). See 6.6.
4. **EF command logging off in Production.** `appsettings.json` lowers only `Microsoft.AspNetCore`;
   every till's 20-second ping and 60-second pull logs its SQL at Information. Add
   `"Microsoft.EntityFrameworkCore.Database.Command": "Warning"`.
5. **No silent SQLite in production.** The image bakes `ConnectionStrings__Default="Data
   Source=/data/pos.db"` (Dockerfile ENV block); a compose that forgets `Database__Provider` runs
   the company on a SQLite file while the MySQL dumps back up an empty database. Drop the two
   connection-string defaults from the image (keep `DataProtection__KeysPath`), log provider +
   server + database (never the password) after `Program.cs:40`, and in Production refuse `Sqlite`
   unless `Database__AllowSqliteInProduction=true`.

   **Items 1 and 5 break the CI docker job and the smoke script as they stand, so these ship in
   the same commit.** `.github/workflows/build.yml:74-87` runs the image with only `Jwt__Key`
   (so `ASPNETCORE_ENVIRONMENT` defaults to Production in the `aspnet:8.0` base), relies on the
   baked SQLite connection string, logs in as `the historical shared demo administrator login`, and asserts
   `docker exec pos ls /data/pos.db`; `tests/e2e/provider-smoke.cjs:47` hardcodes the same
   credentials. The changes that go with the two items:
   * `build.yml` docker step passes `-e ASPNETCORE_ENVIRONMENT=Development` (the seeded demo
     login is a Development behaviour after item 1 — this keeps the existing `the historical shared demo administrator login`
     assertion meaningful) **or**, better, stays Production and passes
     `-e Bootstrap__AdminUsername=ci-admin -e Bootstrap__AdminPassword=<12+ chars, CI-only>`
     and logs in with those; the pick is the second, because it exercises the path a real deploy
     uses. The step also passes `-e Database__Provider=Sqlite -e "ConnectionStrings__Default=Data
     Source=/data/pos.db" -e Database__AllowSqliteInProduction=true`, since the image no longer
     bakes a connection string and Production refuses SQLite without the opt-in; the
     `ls /data/pos.db` assertion then still holds and is kept. A comment above the step says why
     each variable is there.
   * `provider-smoke.cjs:47` reads `ADMIN_USER` / `ADMIN_PASSWORD` from the environment, defaulting
     to the demo pair so local SQLite runs keep working; the CI MySQL job (item 24 in §7) and the
     restore drill of §6.6 pass the bootstrap pair.
   * `README`'s "run the image" line gains the three database variables, and the compose in
     `deploy/mysql/compose.yml` gets `Database__Provider=MySql` spelled out if it is not already.
   * A workflow assertion that the container **log** names the provider and database after
     item 5's log line, so the job fails if the message is ever dropped.

Also worth doing early, *hours* each: a bounded startup wait (`CanConnectAsync` every 2 s for
`Database:StartupWaitSeconds`, default 60, before the migrate loop at `Program.cs:266` — **not**
`EnableRetryOnFailure`, which does not wrap the user transactions `BeginWriteScopeAsync` opens);
`UseForwardedHeaders` behind `Proxy__Enabled` / `Proxy__TrustedNetworks__0` (the day SSO is enabled,
`Request.Scheme` must be https, `docs/04:49-51`); a text warning on the login screen when
`!window.isSecureContext` (service workers need HTTPS, so the README's LAN `http://<ip>:5080`
mode has no offline reload). And *day* each: an update banner (`SwUpdate.checkForUpdate()` on each
sync tick, `VERSION_READY` → "new version available — reload", reload only on click and only with
an empty cart; `index.html` and `ngsw.json` served `Cache-Control: no-cache` — **to verify at
build time**: `Program.cs:378` is a bare `UseStaticFiles()` with no `OnPrepareResponse`, and the
expectation that it therefore sends no `Cache-Control` today is checked with one `curl -I` against
the running image before the header is added, since the fallback middleware or the proxy may
already be setting one) — today nothing injects `SwUpdate`, so tills run yesterday's bundle
against today's API after every upgrade; and a
CI job that runs the container against a `mysql:8.0` service and `provider-smoke.cjs` — CI proves
the image on SQLite only (`build.yml:70-87`).

### 6.1 Server

Ubuntu 22.04/24.04 LTS, 2 vCPU, 4 GB RAM, 60 GB SSD (MySQL buffer pool 1 GB, API ~300 MB, Caddy
~50 MB; 20–30 tills produce about 2 requests/s), public IPv4, a DNS A record for the shop domain,
firewall 22 (key-only) / 80 / 443. Docker Engine with the compose plugin. Keep the Debian-based
`aspnet:8.0` image: time-zone conversion for rentals and integrations uses the container's tzdata
(`RentalDocuments.cs:1099` falls back silently), so an alpine or chiseled base would move due dates
to UTC without a message.

### 6.2 Compose — `deploy/prod/` (new), `.env` gitignored (`.gitignore:16` already ignores it)

* **caddy** (`caddy:2`) — ports 80/443; `Caddyfile`: `{$POS_DOMAIN} { encode zstd gzip; header
  Strict-Transport-Security "max-age=31536000; includeSubDomains"; header /index.html Cache-Control
  "no-cache"; header /ngsw.json Cache-Control "no-cache"; reverse_proxy pos:5080 }`. Caddy
  preserves `Host` by default and obtains Let's Encrypt certificates itself; volumes
  `caddy-data:/data`, `caddy-config:/config`. HSTS belongs here, as `Program.cs:332-334` intends.
* **pos** — built from `./Dockerfile` or a tagged image; **no published ports**; `depends_on:
  mysql: condition: service_healthy`; `env_file: .env`; volume `pos-data:/data` (Data Protection
  ring at `/data/keys` — irrelevant to till sessions today, since JWTs are HMAC with `Jwt:Key`, but
  critical the day SSO is enabled); `restart: unless-stopped`; `logging: json-file, max-size 20m,
  max-file 5`. **Exactly one replica**: the login throttle is in-memory and migrations run at
  startup.
* **mysql** (`mysql:8.0` — the code pins server version 8.0.36, `PosDbProviders.cs:24`; not 8.4
  until Pomelo and the pin move together) — no published ports; `--character-set-server=utf8mb4
  --collation-server=utf8mb4_unicode_ci --innodb-buffer-pool-size=1G --performance-schema=OFF
  --binlog_expire_logs_seconds=604800`; volume `mysql-data:/var/lib/mysql`; the `mysqladmin ping`
  healthcheck from `deploy/mysql/compose.yml`.
* **backup** (`mysql:8.0`, a shell loop or host cron) — 6.6.

Environment variable **names** (values only in `.env` or a secret store, never in the repository):
API — `ASPNETCORE_ENVIRONMENT=Production`, `Database__Provider=MySql`, `ConnectionStrings__Default`
(shape `Server=mysql;Port=3306;Database=…;User=…;Password=…;SslMode=Preferred;ConnectionTimeout=15;DefaultCommandTimeout=60`),
`Jwt__Key` (≥ 32 chars or startup throws, `Program.cs:66-73`), `Jwt__Issuer`, `Jwt__Audience`,
`Jwt__ExpiryDays`, `AllowedHosts` (the domain), `Country` or `Regional__*`,
`Bootstrap__AdminUsername`, `Bootstrap__AdminPassword` (removed after first start),
`Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=Warning` until 6.0 item 4 lands,
`Proxy__Enabled`, `Proxy__TrustedNetworks__0`, optionally `IntegrationOutbox__IntervalSeconds`,
`Integrations__*` / `IntegrationSecrets__*`. MySQL — `MYSQL_ROOT_PASSWORD`, `MYSQL_DATABASE`,
`MYSQL_USER`, `MYSQL_PASSWORD`. Caddy — `POS_DOMAIN`. Backup — `BACKUP_RETENTION_DAYS`.

### 6.3 First start

`docker compose up -d`. The API waits for MySQL, applies every migration (`Program.cs:266-268`),
seeds the bootstrap admin, store, main branch and `C01`, then tenders, cash boxes and the chart of
accounts (`CoreDataSeeder`, `:300`). Check `docker compose logs pos` for the migration names and
`docker compose exec pos curl -fs http://127.0.0.1:5080/health` — `pos` publishes no port, so the
probe runs inside the container, which is why item 2 installs `curl` in the final stage. Remove
`Bootstrap__AdminPassword` from `.env` and `docker compose up -d pos` again.

### 6.4 Company, branches, tills

Sign in at `https://<domain>` as the bootstrap admin, device code `C01`. Store: names, the fiscal
identifiers the country profile defines, receipt footer and legal text — and, once §4.3 lands, the
printed-document texts and bank details. Branches: rename "Principal", add one per shop. Tills: one
device code per till, each bound to one branch (`DevicesEndpoints.cs:29-39`). Users: cashiers with
password and PIN and — after §2.2 — their branch. Stock: receiving or opening balances per branch.
Then change the admin password and create a second admin.

### 6.5 What a branch needs

A Windows 10/11 PC or Android tablet with Chrome/Edge; internet of any kind — the till survives
outages by design; a USB HID barcode scanner sending Enter as suffix (`ScanBuffer`: gaps under
55 ms, ≥ 6 chars, Enter — `core/barcode-scan.ts`); an 80 mm thermal printer as the OS default
(printing is `window.print()`; Chrome with `--kiosk-printing` skips the dialog — Q29). Setup: open
`https://<domain>`, "Install app", server address blank (same origin), device code, one **online**
sign-in to pull the catalogue; then test: disconnect, reload, PIN unlock, a sale, reconnect, watch
the outbox drain. Never incognito, never "clear site data on exit" — IndexedDB holds unsent sales.
No local server, static IP or VPN.

### 6.6 Backups and the restore drill

Daily at 02:00 local: `mysqldump --single-transaction --quick --routines --triggers | gzip` to
`/backups/pos-YYYYMMDD-HHMM.sql.gz`, a tar of `/data/keys`, an encrypted copy of `.env`; prune by
`BACKUP_RETENTION_DAYS`; copy off-site nightly (rclone/rsync; destination and alert recipient — Q28).
RPO 24 h with the nightly dump; MySQL binlog with 7-day expiry makes point-in-time recovery
possible if Q27 asks for it. **Tills that already marked a sale `synced` will not re-push it**
(idempotency is by client id on `pending` rows only), so sales between the last dump and a disaster
are lost on the server without binlog recovery.

Monthly drill, documented in `docs/07-production-runbook.md` when the scripts land: restore into a
scratch database, start a second API container on `127.0.0.1:5125` pointed at it (single-company
mode, so any `Host` works), verify admin login, `GET /api/sales` count and the trial-balance totals
against production, run `PROVIDER=MySQL BASE=http://127.0.0.1:5125 node tests/e2e/provider-smoke.cjs`
(with `ADMIN_USER` / `ADMIN_PASSWORD` set to the bootstrap pair once §6.0 lands; until then its
credentials at `:47-49` are the demo ones), drop the scratch database. Disaster restore
is the same against the real database name.

### 6.7 Logs and monitoring

stdout only, retained by the json-file driver; `docker compose logs -f pos`. External HTTPS check on
`/api/auth/ping` (anonymous, `AuthEndpoints.cs:93-95`) and, once 6.0 item 2 lands, `/health`.
Flood protection beyond the per-account throttle is delegated to the proxy
(`LoginThrottle.cs:26-27`); the standard Caddy build has no rate-limit module — fail2ban on Caddy's
access log, or a Caddy build with the plugin.

### 6.8 Upgrade and rollback

(a) `docker compose run --rm backup` — **not optional**; (b) `docker compose pull` or `build`;
(c) `docker compose up -d pos` — migrations apply at startup, both sets are committed per schema
change; (d) logs for the migration names, `/health`, one login; (e) tills reload twice today
(until the update banner exists). Rollback: stop `pos`, restore the pre-upgrade dump, start the
previous image tag — EF never runs `Down` automatically. **Never change `Jwt__Key` in an upgrade**:
every till is signed out, offline PIN unlock still works but the outbox cannot push until each till
signs in online again.

---

## 7. Ordered backlog

The order puts a safe production start first, closes the holes a cashier's token opens second,
takes the cheap fixes third, and then the two large pieces — printing, because contracts are signed
on paper and §5.5's dispute is about a price the customer never saw; payroll, which touches new
files only and can run in parallel. Efforts are the scouts', spot-checked; a *week* is five days.

Items 1–17 and 24 landed over three batches on 2026-09-24, and **18 landed in a fourth** the same
day, together with the parts of 19 and 23 that payroll's foundation needed. **20, 21, 22 and 23
landed in a fifth on 2026-09-25** — payroll part B, the whole of §5.5 to §5.12. **Landed** below
says which, and what a starred row still owes. Only 25 has not been started, and 19's one
remaining piece is the shared numbering helper.

| # | Item | Why here | Effort | Landed |
|---|---|---|---|---|
| 1 | Bootstrap admin for the single-company path; EF command logging off in Production; image drops the SQLite default, logs provider, refuses SQLite in Production unless opted in | the most likely deployment mistake seeds demo credentials and ledger entries that can never be deleted; the others fill the disk and hide the wrong database | hours ×3 | done |
| 2 | `deploy/prod/` compose (caddy + pos + mysql + backup), `Caddyfile`, `.env.example` (names only), `backup.sh`, `restore.sh`, `docs/07-production-runbook.md`, `tests/e2e/backup-restore.sh` | there is no backup or restore anywhere; five months of ledger would live on one volume | day | done * |
| 3 | `/health` with a database check and a `HEALTHCHECK`; bounded startup wait | a green monitor with MySQL down; a crash loop on host reboot | hours ×2 | done |
| 4 | HTTP test host: `Microsoft.AspNetCore.Mvc.Testing` in `ParadisERP.Tests`, a `WebApplicationFactory<Program>` fixture on SQLite with the single-company tenant configured in code, `Program` visible to the tests | nothing today can assert a status code, a login refusal, a policy or a startup throw; `LoginTerminalBindingTests` (§2.5), `HrEndpointTests` (§5.11) and the bootstrap tests (§6.0) all wait on it | hours | done |
| 5 | Terminal binding (`User.BranchId`, both migration sets, users tab, login text) **together with** branch scoping of `/api/sales`, `/api/sales/{id}`, `/api/reports/summary`, `send-receipt` via `BranchScope` + `SalesQueries` + `BranchScoped` marker | a cashier token reads every branch's sales and Z-report; scoping alone is a speed bump while the cashier picks the device code | 2 days | done |
| 6 | Push rejections: code + safe sentence to the till, full exception logged under a reference; `PushResultItem.Code`; UI fallback to `message` | the database's own words on the till and nothing in the server log | day | done |
| 7 | Stop the blank 80 mm strip: scope the receipt rule to `html.print-receipt`, runtime `@page`, A4 default, dead `#report-print` | six Print buttons print nothing today; everything in §4 is invisible until this | hours | done * |
| 8 | Rental list totals (`TotalsAsync`, `RentalContractRowDto`, delete `enrich()`); per-item multiplier read routes and list; deposit stored/shown/owed-back | the three known gaps; none needs a migration; the list N+1 is the one a yard of 120 contracts feels | day + hours + day | done |
| 9 | Stock routes agree with `ViewOtherBranchStock` (after 5); the dead `isManager` at `RentalEndpoints.cs:227` | a permission enforced on one route and undercut by two is theatre; a parameter that can never be false hides the rule | hours ×2 | done * |
| 10 | Print mechanism: `/print/:kind/:id` route with shell exclusion, `PrintDocumentComponent`, `PrintService`, `tIn/moneyIn/textIn`, `print.scss`, Playwright `print-documents.cjs` in CI | every document below needs it; designed so server-side PDF is additive later | days | done |
| 11 | Company document settings (`Store.DocumentTexts`, `Email`, `Website`, `BankDetails`; both migration sets; `GET/PUT /api/catalog/store/documents`; store tab) | terms, payment terms and bank details have nowhere to live; must never reach `StoreDto` | day | done |
| 12 | Receipt voucher print (A5 ×2, printable while pending) | the paper a customer needs the moment they hand over a cheque | day | done |
| 13 | Rental contract print (+ `TaxRate` on the line DTO) | the signed paper with the three prices — the §5.5 dispute | days | done |
| 14 | Sale invoice + credit note print (+ `SourceNumber`, `GET /credit-notes/{id}`) | contractors need the facture with the tax split for their own filing | days | done |
| 15 | Amount in words (`core/amount-words.ts`, `currencyUnitNames` in the profile) | vouchers and invoices carry it in this market; figures come from configuration | day | done |
| 16 | Issue and return note prints (+ `done` sentence with a Print link in the rentals shell) | nothing signed says what left or what came back | days | done |
| 17 | Statement (A4) and ageing (landscape) prints | the monthly statement to each contractor is a screenshot today | days | done |
| 18 | Payroll: `PayrollConfigurations` store, three permissions with `Permissions.All` split into `Grantable` / `Configured` (`payroll.edit_statutory` held only through `Payroll:StatutoryEditors`), the statutory PUT reading statutory fields only, `IncomeTaxMode` tri-state, `IncomeTax.Progressive`, `EmployeeContributionReducesTaxBase`, statutory hooks, login response with permissions | gives the existing settings model a reader and the statutory half an owner in a one-company copy | day + hours | done * |
| 19 | Shared `DocumentNumbers` helper extracted from the two private `Numbered` copies (rentals, receivables — no behaviour change, `Count + 1` stays in the write scope); then HR entities and the `HrAndPayroll` migration in both sets (SQLite filtered index on live runs, code rule on both), employees endpoints and screen | a third copy of the numbering one-liner is where the shapes drift; payroll reads from the employee; new tables only, zero ALTER | hours + days | partly * |
| 20 | Attendance: declarations, days, clock, `AttendanceSummarizer`, period lock, the keyboard-first sheet | the screen the module lives or dies on; the daily basis cannot run without it | days | done |
| 21 | Engine + run lifecycle + postings + accounts 421/425/431/442/447/631/635/641 + seeder guard | one entry per run per branch; refuses only on the tax mode; snapshots what it computed with | week + day | done * |
| 22 | Advances and wage payments (first `Disbursement` writers), reversal, treasury label | the largest regular cash outflow gets a document; the box count has a name against it | day + day | done * |
| 23 | Run and payslip screens, settings screen, `provider-smoke` HR chain | review before posting; payslip through the print route | week | done |
| 24 | PWA update banner + `no-cache` on `index.html`/`ngsw.json`; forwarded headers; insecure-context warning; CI MySQL container job | tills learn about a deploy; SSO-ready scheme; LAN installs told the truth; MySQL migrations proven in CI | day + hours + hours + day | done * |
| 25 | Rental drift checks (overdue floor; wreck report day) with pinning tests | after Q4/Q5 | hours ×2 | — |
| 26 | Later, when asked: server-side PDF via headless Chromium over the same route + statement mailing; `ReceiptVoucher` purpose column for deposit collection; generic `PaymentVoucher`; `PinHash` replication scoped by branch; test-webhook code+log | each waits on an owner answer or a disabled surface | — | — |

\* What the starred rows still owe:

* **2** — `tests/e2e/backup-restore.sh` was not written; the restore drill is spelled out as steps in
  `docs/08-production-runbook.md` §6 instead, and CI does not run it. Off-site copying is not
  automated either: `backup.sh` copies only when `BACKUP_OFFSITE` names a destination.
* **7** — `#report-print` in the reports tab is still dead; everything else in §4.1 landed.
* **9** — the stock routes agree with `ViewOtherBranchStock`, and `BranchScope.ForStockAsync` now
  reads `User.BranchId` only for callers who do not already see every branch. The dead `isManager`
  parameter in `RentalEndpoints` is the part still outstanding.
* **18** — the store, the three permissions, the statutory-only PUT, the tri-state,
  `IncomeTax.Progressive` and `EmployeeContributionReducesTaxBase` all landed. Two parts did not,
  for opposite reasons. The `Grantable`/`Configured` split is **superseded**, not outstanding: the
  owner's 2026-09-24 decision makes `payroll.edit_statutory` an ordinary grant, `Permissions.All`
  stays one list, and §5.2's third bullet and the Q18 answer are struck below. The **login response
  still does not carry the session's effective permissions** — that one is genuinely outstanding,
  and it is why the Personnel nav group is not gated on a claim.
* **19** — the HR entities and the migration landed in both migration sets, under the name
  `PayrollAndDocumentHistory`, with the SQLite filtered index and the code rule on both providers.
  The employees endpoints and screen landed on 2026-09-25, and `EMP-`, `ADV-`, `RUN-` and `WP-`
  numbers all exist. The shared `DocumentNumbers` helper is still **not** extracted, and the
  warning in this row came true: there are now **four** private `Numbered` copies —
  `ReceivableDocuments`, `RentalDocuments`, `PayrollDocuments` (`RUN-`) and `WageMoney`
  (`ADV-`/`WP-`) — plus `EmployeeDirectory`'s `EMP-`. Every one of them is `$"{prefix}-{n:D6}"`
  over `CountAsync + 1` inside the write scope, so extracting it is a mechanical pass over five
  call sites with no behaviour change. This is the one piece of 19 still owed.
* **21** — the accounts, the accrual, the one-entry-per-branch-per-run rule and the reversal all
  landed, and `CoreDataSeeder` adds the eight accounts to an existing company without touching one
  an admin has renamed. §5.9's **startup guard** — an error logged when an admin had already
  created, say, 421 with the wrong type — was **not** built: `CoreDataSeeder.EnsureAsync` takes no
  logger, and adding the parameter would change its call sites. A deduction with no confirmed
  account posts to **641** with the ⚠ mark, never to 758, and an accrual whose net was held at zero
  posts the uncollectable remainder to **471 Suspense** so the month can still be approved and the
  trial balance shows it as something to investigate. Both want a line in §8 beside the 419-vs-165
  question.
* **22** — advances, wage payments, their reversals and the statutory payments all landed, and all
  of them move cash as `CashMovement` kind `Disbursement` (a reversal returns it as `Adjustment`,
  because a counted correction is not the payout it undoes). There is **no write-off route**: it
  waits on Q24 naming the account that absorbs an abandoned advance, and the advances screen says
  so in a panel rather than offering a button that cannot post. `WageAdvanceStatus.WrittenOff` is
  therefore reachable by nothing — deliberately, not by omission.
* **23** — the settings screen landed on 2026-09-24; the **run, payslip, employee, attendance,
  clock, advances, wage-payment and statutory screens**, the ninth printed document and the
  `provider-smoke` HR chain all landed on 2026-09-25. What is still missing is the same thing §4.4
  is missing: Karma specs per screen. The pure logic that was worth pinning is pinned
  (`sheet-keys.spec.ts`, 30 cases; `payroll-run-view.spec.ts`).
* **24** — the update banner, the `no-cache` headers, forwarded headers behind `Proxy__Enabled` and
  the CI MySQL job all landed. The **insecure-context warning** for LAN installs did not.

Also outstanding from §4.4, which is otherwise complete: the per-document Karma specs, and the
platform regional editor's `currencyUnitNames` (see `docs/project-memory.md`, "Document printing").

Two gaps that 18 and 19 left, and where they stand after part B. The users screen **can** now grant
a named permission, so `hr.attendance` can actually reach a foreman's login and the attendance
group's policy has a way of being exercised — that half of 18's star is closed. The **login
response still carries no effective permissions** (`UserDto` is id, username, full name and role),
so the Personnel nav group is still shown to every back-office user rather than gated on a claim;
every screen behind it refuses in words and a 403 falls through to the server's own sentence.

Sum of the scouts' estimates at one builder: security about four days; rental about three; the
runbook items about a week; printing three to four weeks; payroll five to six weeks. Payroll and
printing share only `backoffice.component.ts` (nav) and `translations.ts`.

---

## 8. Questions for the owner

Each is given in English with the Arabic to ask it verbatim. Numbers are stable — the text above
cites them — so they are written as `Qn` rather than as a running list. The first group changes
the work; the accountant's block is marked and nothing in §5 is computed with a real figure until
it is answered; the last group does **not** change a decision — each already has a recommendation
the builders proceed with, and the answer only confirms or adjusts a detail.

**Security**

**Q1.** Is a cashier bound to **one branch**, or to **specific terminals by name**? (One branch is
proposed: one nullable column, and it matches "salaries per branch".)
هل يُربط الكاشير بفرع واحد، أم بأجهزة محددة بالاسم؟ (المقترح: فرع واحد.)

**Q2.** Does the till ever need the sales list or the Z-report **from the server** while online?
Today its screens read only the local copy. If never, both routes leave the any-authenticated list.
هل تحتاج نقطة البيع في أي حالة إلى قائمة المبيعات أو تقرير Z من الخادم؟ اليوم شاشاتها تقرأ من النسخة المحلية فقط.

**Q3.** A manager who may **not** see other branches' stock — should he also be limited to his own
branch's sales and reports, or is that permission about stock only?
المدير الممنوع من رؤية مخزون الفروع الأخرى، هل يُحصر أيضاً في مبيعات فرعه وتقاريره، أم أن الصلاحية تخص المخزون فقط؟

**Q31.** May a **cashier** grant a contract extension at the till (never retroactive — the
retroactive case stays a manager's), or do extensions stay in the back office with the contract?
(Back office is recommended; §7 decision 5 of the redesign only settles where the contract is
raised.)
هل يجوز للكاشير منح تمديد للعقد من نقطة البيع (دون أثر رجعي — التمديد بأثر رجعي يبقى للمدير)، أم تبقى التمديدات في المكتب مع العقد؟ (المقترح: المكتب.)

**Renting**

**Q4.** May a manager sign an overdue price **below** the company multiplier (today anything at or
above the hire price is accepted), or is the admin's figure a floor? (Floor is recommended.)
هل يجوز للمدير أن يوقّع سعر تأخير أقل من مضاعف الشركة، أم أن رقم الإدارة هو الحد الأدنى؟ (المقترح: حد أدنى.)

**Q5.** A unit reported destroyed and later brought back as a wreck: does rent stop on the **report
day** (as the code does now) or on the **return day**?
وحدة أُبلغ عن تلفها الكلي ثم أُعيدت خردة: هل يتوقف الإيجار يوم الإبلاغ (كما هو الآن) أم يوم الإرجاع؟

**Q7.** *(accountant)* When deposit collection is wired, which account holds it: the seeded **419**
(clients créditeurs — acomptes) or a new **165** (dépôts et cautionnements reçus)? Codes cannot
change after the first posting.
(المحاسب) عند تفعيل قبض الضمان، أي حساب يحمله: 419 الموجود أم حساب جديد 165؟ الرقم لا يتغير بعد أول ترحيل.

**Printing**

**Q9.** Is **e-mailing** statements and invoices to customers needed now (server-side PDF), or is
print / save-as-PDF from the browser enough for this phase?
هل إرسال كشوف الحساب والفواتير بالبريد الإلكتروني مطلوب الآن، أم تكفي الطباعة أو الحفظ PDF من المتصفح في هذه المرحلة؟

**Q10.** Receipt voucher: two A5 copies on the office printer, or **also** on the 80 mm till
printer at the branch?
سند القبض: نسختان A5 على طابعة المكتب، أم أيضاً على طابعة 80 مم في الفرع؟

**Q12.** Should the invoice **freeze** the customer's name, address and tax id at issue, so a
reprint matches the paper first handed over (new columns + backfill), or print the current details?
هل تُثبَّت بيانات الزبون (الاسم والعنوان والرقم الجبائي) على الفاتورة عند إصدارها بحيث تطابق إعادة الطباعة الورقة الأصلية، أم تُطبع البيانات الحالية؟

**Q13.** Issue and return notes: is a **driver, vehicle or site address** to be printed? None
exists in the data today.
سند الخروج وسند الإرجاع: هل يُطبع اسم السائق أو رقم المركبة أو عنوان الورشة؟ لا يوجد شيء منها في البيانات اليوم.

**Payroll — the owner**

**Q14.** Do you want a **per-branch** attendance default (sites declared, shops recorded) on top
of the company default and the per-employee override?
هل تريد وضع حضور افتراضياً لكل فرع (الورشات تصريح، المحلات تسجيل) إضافة إلى الافتراضي العام وتجاوز كل موظف؟

**Q15.** Advances: recover the **whole** open balance from the next payslip, or a **monthly
instalment** fixed when the advance is granted?
السلفة: تُستقطع كاملة من الراتب التالي، أم بقسط شهري يُحدد عند منحها؟

**Q16.** Daily workers: paid **once a month** in the same run as monthly staff, or weekly /
fortnightly? (Shorter than a month needs the accountant's ruling on prorating the tax scale and a
second run type.)
عمال اليومية: يُدفع لهم مرة شهرياً في نفس الدورة مع الموظفين الشهريين، أم أسبوعياً أو كل أسبوعين؟

**Q17.** A monthly employee with **no attendance entered** for the month: pay in full and warn, or
hold that line until a figure is entered?
موظف شهري بلا حضور مُدخل للشهر: يُدفع كاملاً مع تحذير، أم يُحجز سطره حتى يُدخل رقم؟

**Q18 — answered 2026-09-24, and the answer supersedes this question's own premises.** Neither:
the **company Admin** sets them, from a back-office screen, starting from placeholders marked
unconfirmed. "ابني المحرك والشاشات بأرقام وهمية … تتعدل من لوحة التحكم". So
`payroll.edit_statutory` is an ordinary grant, `Payroll:StatutoryEditors` does not exist, and an
Admin can grant it to the accountant's login once the users screen offers permissions at all.

~~**Q18.** Who sets the **statutory figures** in your copy: your accountant's **own login**, named in
the deployment configuration so that the permission is minted for that account (the admin cannot
grant it from the users screen either way), or **only the deployer** through configuration with no
screen?~~
~~من يضبط الأرقام القانونية في نسختك: حساب المحاسب نفسه، يُسمّى في إعدادات النشر فتُمنح له الصلاحية (المشرف لا يستطيع منحها من شاشة المستخدمين في الحالتين)، أم من ينشر النظام فقط عبر الإعدادات دون شاشة؟~~

**Q19.** Should **seeing wages** need a named permission (admins hold it, managers must be granted
it), or is the Manager role enough?
هل تحتاج رؤية الأجور صلاحية مسماة (المشرف يملكها والمدير يُمنحها)، أم يكفي دور المدير؟

**Q30.** An employee **moved to another branch during the month**: is the whole month paid by
the branch that holds them when payroll is computed (proposed — one line, one branch), or is the
cost **split** between the two branches by days? (A split needs a dated branch history the
employee record does not have, and a second line on a second run.)
موظف نُقل إلى فرع آخر أثناء الشهر: هل يُحمَّل الشهر كله على الفرع الذي يتبعه عند حساب الرواتب (المقترح — سطر واحد في فرع واحد)، أم تُقسَّم التكلفة بين الفرعين بالأيام؟ (التقسيم يحتاج سجلاً مؤرخاً للفروع لا يوجد في بطاقة الموظف اليوم.)

**Payroll — the accountant** (each answer changes the engine, not just a figure)

**Q20.** Is the income-tax scale applied to the taxable base **directly**, or after an
**abatement** (a percentage with a floor and a ceiling)? And does the **employee's social
contribution reduce the taxable base** before the scale (the engine ships with this switched on,
under review)?
هل يُطبَّق جدول الضريبة على الدخل (IRG) على الوعاء مباشرة، أم بعد تخفيض (نسبة بحد أدنى وأقصى)؟ وهل يُنقَص اشتراك العامل في الضمان الاجتماعي من الوعاء الضريبي قبل تطبيق الجدول (النظام يبدأ بهذا الخيار مفعّلاً، بانتظار التأكيد)؟

**Q21.** Does the social-contribution base have a **floor** or a **ceiling**?
هل لوعاء اشتراكات الضمان الاجتماعي حد أدنى أو حد أقصى؟

**Q22.** Are there **employer levies** on the wage bill other than social security, and at what
rate?
هل توجد اقتطاعات على رب العمل غير الضمان الاجتماعي، وبأي نسبة؟

**Q23.** For each allowance, deduction and overtime band: does it enter the **contribution base**?
the **tax base**? Which deductions reduce the wage cost, and which are collected for someone else
(a fine, a union due) and are not a wage cost at all?
لكل منحة وخصم وساعات إضافية: هل تدخل في وعاء الاشتراك؟ في وعاء الضريبة؟ وأي الخصومات تُنقص تكلفة الأجور وأيها يُقتطع لحساب طرف آخر (غرامة، اشتراك نقابة) ولا يُعدّ تكلفة أجور أصلاً؟

**Q24.** Which account absorbs an **unrecoverable advance**? Which account receives a deduction
that is **not** a wage-cost reduction (until it is named, the system nets it against 641 and flags
it)? And please confirm the eight account codes — 421, 425, 431, 442, 447, 631, 635, 641 — before
the first run posts; they cannot change afterwards.
أي حساب يستوعب سلفة لا تُسترد؟ وأي حساب يستقبل الخصم الذي لا يُنقص تكلفة الأجور (إلى أن يُسمّى، يُقيّده النظام على 641 مع تنبيه)؟ وتأكيد أرقام الحسابات الثمانية — 421، 425، 431، 442، 447، 631، 635، 641 — قبل أول ترحيل؛ لا تتغير بعده.

**Deployment**

**Q27.** After a disaster, is losing up to **24 hours** of already-synced sales acceptable (nightly
dump), or do you want hourly dumps plus binlog for point-in-time recovery?
بعد كارثة، هل يُقبل فقدان حتى 24 ساعة من المبيعات المرفوعة (نسخة ليلية)، أم تريد نسخاً كل ساعة مع سجل binlog للاسترجاع إلى نقطة زمنية؟

**Q29.** Receipt printing at the till: the browser dialog each time, or Chrome in
`--kiosk-printing` straight to the 80 mm printer?
طباعة التذكرة في نقطة البيع: نافذة الطباعة كل مرة، أم كروم بوضع kiosk-printing مباشرة إلى طابعة 80 مم؟

### Not decision-changing — proceed with the recommendation

These are kept so the owner is asked, but no builder waits on them: each item has a recommendation
in the text and the work goes ahead on it.

**Q6.** At close, is it enough that the system **states** "deposit to return: X" and moves no cash
until a payment voucher exists — or must closing wait for the refund? (State it, never block, is
recommended and is what §3.3 builds.)
عند إغلاق العقد، هل يكفي أن يُظهر النظام «ضمان مستحق الرد: X» دون تحريك نقد حتى يوجد سند صرف، أم يجب أن ينتظر الإغلاق حتى يُرد الضمان؟

**Q8.** Do you have the wording of your **current paper rental contract**, in each language, to
paste into the settings? The system will not write legal terms. (The settings field ships either
way; an empty text prints nothing.)
هل لديك نص عقد الإيجار الورقي الحالي بكل لغة لنضعه في الإعدادات؟ النظام لن يكتب شروطاً قانونية.

**Q11.** Amount in words: on which documents (invoice, voucher, contract) and in which languages?
(Recommended and built: invoice and receipt voucher, in the document's language; a language with
no unit names prints figures only.)
المبلغ بالحروف: على أي مستندات (الفاتورة، سند القبض، العقد) وبأي لغات؟

**Q25.** Which **domain**, who controls its DNS, and are ports 80/443 reachable from the internet
(for Let's Encrypt) — or do you have your own certificate? (Caddy with Let's Encrypt is the
recommendation; a supplied certificate is a two-line change to the `Caddyfile`.)
ما النطاق، ومن يتحكم في DNS، وهل المنفذان 80 و443 مفتوحان من الإنترنت، أم لديك شهادة خاصة؟

**Q26.** Where does the server live: a rented VPS (which country and provider is acceptable for the
company's sales data) or a machine at head office with a public IP? (The compose is the same on
either; the runbook in §6.1 assumes a VPS.)
أين يعمل الخادم: خادم مستأجر (أي دولة ومزوّد مقبولان لبيانات مبيعات الشركة) أم جهاز في المقر بعنوان IP عام؟

**Q28.** Off-site backup destination (S3-compatible bucket, a second server, a drive), and **who
receives the alert** when a night's backup fails? (The scripts take the destination and the
recipient from `.env`; nothing else changes.)
أين تُحفظ النسخة الاحتياطية خارج الخادم (تخزين S3، خادم ثانٍ، قرص)، ومن يستلم التنبيه إذا فشلت نسخة ليلة؟

---

## References

* `docs/project-memory.md` — accepted decisions, what is implemented and verified
* `docs/06-erp-redesign.md` — the specification: glossary, §5.5 renting, §5.6 deposit, §7 decisions, §8.2 payroll
* `docs/03-work-status.md` — chart of accounts, what phase 5 left (no `PaymentVoucher`, no PDF)
* `CLAUDE.md` — the design system and its three rules
