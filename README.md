# Caisse POS — offline-first point of sale (Algeria)

Hosted company isolation and manually activated subscription plans are documented in
[SaaS, subscriptions, and identity](docs/02-saas-subscriptions-and-identity.md).

A retail POS built as an **Angular 20 PWA** on an **ASP.NET Core 8** API. The register runs
entirely from the browser's local database, so it keeps selling with the internet down and
uploads everything once the line is back.

Trilingual interface — **العربية (RTL) · Français · English** — switchable at runtime.
Built for Algeria first — **DZD**, TVA at **19 % / 9 % / 0 %**, receipts carrying **NIF / NIS / RC / AI** —
with currency, taxes, identifiers, languages and payment methods set per company, so other
countries (Morocco, Tunisia, France, Saudi Arabia presets included) need no code change.

---

## How offline actually works

The rule that makes this simple: **the catalog only ever flows down, sales only ever flow up.**
There is no field that both sides can edit, so there is nothing to merge.

| Data | Direction | Conflict rule |
|---|---|---|
| Store, branches, categories, products, customers, cashiers | server → terminal | **Server wins.** Terminals are read-only replicas; you edit the catalog in the back office. |
| Sales | terminal → server | **Client wins, append-only.** A sale that physically happened cannot be rejected. |
| Stock | server is the source of truth, **per branch** | Terminal keeps an optimistic estimate of its own branch's stock, then overwrites it from the next pull. |

**Multi-branch.** A company can have several branches — a main warehouse plus one or more
selling locations — and stock is counted independently per branch (`ProductStock`, keyed by
product + branch), not as one company-wide number. Every till is registered to exactly one
branch (`Device.BranchId`), resolved server-side from the device code — never trusted from the
client — so a sale only ever moves that branch's stock. An admin moves stock from the warehouse
to a branch with `POST /api/catalog/stock/transfers`, blocked from going negative unless the
source branch has `AllowNegativeStock` set. A terminal's catalog pull only ever contains its
own branch's stock figures, so the wire format stays flat and the till code doesn't need to
know branches exist at all. Each branch has its own name, address, phone and fiscal identifiers,
and every paper raised there — the A4 documents and the till's receipt — prints them, taking the
main branch's where the branch has none.

**Selling offline.** A sale is written to IndexedDB and the receipt prints; only then does the
app *try* the network. The register never waits on a request, so a dropped connection cannot
lose a sale.

**Receipt numbers.** Each terminal gets a code (`C01`, `C02`…) and numbers its own receipts:
`C01-000042`. Two tills offline for a day cannot collide, and uniqueness is enforced per
device, never globally.

**Idempotency.** The terminal generates the sale's UUID. If the connection dies mid-upload,
the retry is answered `duplicate` instead of being recorded twice — including when a timed-out
request and its retry are in flight at the same time.

**Sync cursor.** Every replicated row carries a monotonically increasing `Version`, reserved
with an atomic `UPDATE … SET Value = Value + 1` inside the same transaction that writes the
row. A terminal pulls with `since = <last cursor>`; deletes arrive as tombstones. Batches are
capped at 500 rows per entity with a `hasMore` flag, so a till that has been off for weeks
catches up in resumable passes.

**Reconnection.** Connectivity is decided by a probe against `/api/auth/ping`, not
`navigator.onLine` — a shop on a dead ADSL line still reports "online" to the browser. The
outbox drains on reconnect, every 60 s, and after each sale, with exponential backoff on
repeated failures so one bad record can't block the day's takings.

**Signing in with no network.** Cashiers unlock with a numeric PIN checked against the BCrypt
hash replicated with their user record. Only `Cashier` rows are replicated — manager and admin
PIN hashes never leave the server, so a till cannot be used to brute-force an admin PIN.

---

## Running it

### Prerequisites
- .NET 8 SDK
- Node.js 20.19+ or 22.12+ (the minimum Angular 20 accepts)

### 1. API

```bash
dotnet run --project ParadisERP.API
```

Migrations are committed (`ParadisERP.Infrastructure/Persistence/Migrations`) and applied
automatically on startup. Runs on `http://localhost:5080`. Swagger at `/swagger`. SQLite by
default (`pos-dev.db`), seeded with a demo company (one main warehouse plus two selling
branches, Alger-Centre and Oran), 10 products, 2 customers and terminals `C01` / `C02`
(both registered to Alger-Centre).

If you change the schema, add the migration to both sets — SQLite and SQL Server:

```bash
dotnet tool install --global dotnet-ef      # if you don't have it
dotnet ef migrations add <Name> -p ParadisERP.Infrastructure -s ParadisERP.Infrastructure -c PosDbContext -o Persistence/Migrations
dotnet ef migrations add <Name> -p ParadisERP.Migrations.SqlServer -s ParadisERP.Migrations.SqlServer -c PosDbContext
```

Set `Bootstrap__AdminPassword` in your local environment before initializing a new demo database. It is required even in Development. For browser/API tests, pass the same password through `ADMIN_PASSWORD` (or the script-specific `POS_PASSWORD` / `POS_ADMIN_PASSWORD`). Do not commit the value. The mock API and its browser clients also share `ADMIN_PASSWORD`.

**Seed logins** — a *Development-only* convenience, and change them before going anywhere
near production:

| User | Password (online) | PIN (offline) | Role |
|---|---|---|---|
| `admin` | supplied through `Bootstrap__AdminPassword` | — | Admin |
| `caisse1` | `Caisse@123` | `1111` | Cashier |

Outside Development this demo company is not seeded at all (set `Bootstrap__SeedDemoData=true`
if you want it anyway). A real first start names its own administrator instead, through
`Bootstrap__AdminUsername` and `Bootstrap__AdminPassword` — read only while the `Users` table
is empty, so an upgrade of a running deployment needs neither line.

### 2. PWA

```bash
cd ParadisERP.WebUI
npm install
npm start          # http://localhost:4200
```

At the login screen set **Adresse du serveur** to `http://localhost:5080` and the
**Code caisse** to `C01`. The first online sign-in downloads the catalog; after that the
terminal works offline.

> The service worker is disabled in `ng serve`. To test true offline behaviour, build for
> production and serve `dist/`, or use the deployment below.

### 3. Single-process deployment (recommended for a shop)

The API serves the PWA, so one process on one machine runs everything:

```bash
cd ParadisERP.WebUI && npm run build
cp -r dist/paradiserp-web-ui/browser/* ../ParadisERP.API/wwwroot/

cd ..
dotnet publish ParadisERP.API -c Release -o out
cd out && ASPNETCORE_URLS=http://0.0.0.0:5080 dotnet ParadisERP.API.dll
```

Every till on the shop's LAN opens `http://<server-ip>:5080` and installs it from the browser
("Add to home screen" / "Install"). Leave the server address blank on the login screen — the
app then talks to whatever host it was served from.

### Docker

```bash
docker compose up -d --build        # http://localhost:5080
```

The image builds the PWA and the API, runs as an unprivileged user and keeps everything that
must survive a rebuild on the `/data` volume: the company database (`/data/pos.db`), the
platform database (`/data/platform.db`) and the ASP.NET Data Protection keys (`/data/keys`,
so operator sign-ins survive a container replacement). Set `Jwt__Key` (32+ characters), `Country` and the two
`Bootstrap__Admin*` variables in `docker-compose.yml`. CI builds the image and checks that it
serves the app and accepts a login, on SQLite and on SQL Server 2022.

The image bakes no connection string, and outside Development it refuses a file database unless
asked for by name — so a run started by hand needs all of it spelled out:

```bash
docker run -d -p 5080:5080 -v pos-data:/data   -e Jwt__Key=<32+ characters>   -e Database__Provider=Sqlite   -e "ConnectionStrings__Default=Data Source=/data/pos.db"   -e Database__AllowSqliteInProduction=true   -e Bootstrap__AdminUsername=<name>   -e Bootstrap__AdminPassword=<8+ characters>   algeria-pos
```

**This compose file is for trying the system out.** Production is one SmarterASP.NET site on
SQL Server — live at https://sarl.zena4cp.com — deployed from GitHub (Actions → *Deploy to
SmarterASP*) or with `deploy/smarterasp/publish.ps1`; the steps are `deploy/smarterasp/README.md`.

Outside Docker, set `DataProtection__KeysPath` to a persistent folder for the same effect.

### Databases

Two providers, and only two:

- **SQL Server in production.** Set `Database:Provider` to `SqlServer` and put the connection
  string in `ConnectionStrings:Default` through the deployment's own settings or environment
  (`Database__Provider`, `ConnectionStrings__Default`) — never in Git. On SmarterASP that is the
  site's `appsettings.Local.json` (`deploy/smarterasp/`).
- **SQLite for development, CI and the unit tests.** The default (`pos-dev.db`), and what
  `docker compose up` uses.

For a verification run against a real SQL Server — migrations from empty and
`tests/e2e/provider-smoke.cjs` — `deploy/sqlserver` starts SQL Server 2022 in Docker on
`127.0.0.1:14333` (copy `.env.example` to `.env` first), and
`ParadisERP.API/appsettings.Local.example.json` shows the connection. It wants about 2 GB of
memory; stop it when the run is over.

SQL Server replaced MySQL on 2026-10-04, when the owner chose SmarterASP.NET hosting. The system
runs at one company with several branches — another company gets its own copy on its own domain
— so a third provider would mean one more migration set per schema change for nothing.

### Production configuration

`Jwt:Key` must be at least 32 characters or the app refuses to start outside Development.
Set `Cors:AllowedOrigins` if the PWA is served from a different host than the API.

---

## Countries: Algeria first, configurable for others

Everything country-specific is a **country profile** owned by the platform operator per
company (operator portal → *Country and tax settings*), or set in configuration for a single
shop (`"Country": "DZ" | "MA" | "TN" | "FR" | "SA"`, or a full `Regional` object). Algeria is
the default. See [system design §5](docs/05-system-design.md#5-country-profiles-regional-settings--implemented).

A profile sets the currency (code, symbol per language, decimals), cash rounding, time zone,
enabled UI languages, tax name, rates and a label per rate, whether shelf prices include tax,
the fiscal identifiers a company fills in, the receipt layout (which blocks print, in what
order) and its legal text, the customer tax-ID label, cash quick-tender notes and the payment
methods offered. Languages beyond Arabic, French and English are added by uploading a
translation pack in the operator portal (or listing it under `LanguagePacks` for a single shop). Tills receive it with login and every pull and use it offline.

With the Algeria profile:

- **TVA**: 19 % standard, 9 % reduced (staples like bread, milk, semolina), 0 % exempt. Set
  per product; the server only accepts the profile's rates. Prices are stored **before tax**:
  a taxed document adds the rate on top of the figure on the product, the card or the contract.
- **Discounts** are distributed across lines pro rata *before* tax is computed, so each rate's
  taxable base is correct on the receipt and in the report.
- **Receipt** shows the shop's NIF, NIS, RC and AI plus a TVA breakdown per rate.
- **Z-report**: `GET /api/reports/summary?from=&to=&branchId=` gives sale count, gross, net,
  the tax breakdown by rate and the top 20 products, rounded to the currency's decimals; a
  per-branch breakdown is included when the call isn't scoped to one branch. Refunds are
  subtracted, not added. Back office → Rapports adds date presets, a branch filter, a
  printable summary, a sales list with per-sale detail, and a low-stock panel
  (`GET /api/catalog/products/low-stock?branchId=`).
- **Payments**: cash (with change and quick-tender buttons for 100/200/500/1000/2000/5000 DA),
  card, and customer credit with a credit-limit warning — or several of them on one sale
  ("Pay with several methods"). Only cash can exceed the total; the change comes out of the
  cash part, and a split sale is never cash-rounded.
- **Customer record**: code, name, phone, address, tax ID (labelled NIF), email and birth
  date. Which of these are mandatory is an admin setting (Back office → Magasin).

---

## API

| Method | Route | Notes |
|---|---|---|
| `POST` | `/api/auth/login` | Returns a 30-day JWT — long by design, a till may be offline for weeks. |
| `GET` | `/api/auth/ping` | Connectivity probe. |
| `GET` | `/api/sync/pull?since=&deviceCode=` | Catalog delta + `hasMore`, branch-scoped stock. |
| `POST` | `/api/sync/push` | Up to 500 sales; per-item `accepted` / `duplicate` / `rejected`. |
| `GET/POST/PUT/DELETE` | `/api/catalog/{categories,products,customers}` | Back office. Manager/Admin only. |
| `GET/PUT` | `/api/catalog/store` | Company profile, fiscal identifier values, customer-field policy. |
| `GET/POST/PUT/DELETE` | `/api/catalog/branches` | Branch management. |
| `GET` | `/api/catalog/products/low-stock?branchId=` | Products at or below their branch's reorder level. |
| `GET` | `/api/catalog/products/{id}/stock` | Per-branch stock breakdown for one product. |
| `POST` | `/api/catalog/products/{id}/stock` | Stock adjustment at one branch (server-side only). |
| `POST` | `/api/catalog/stock/transfers` | Move stock from one branch to another. |
| `GET/POST/PUT` | `/api/devices` | Till registration and branch assignment. Manager/Admin only. |
| `GET/POST/PUT` | `/api/users` | Cashier/manager/admin accounts, password/PIN reset. Admin only. |
| `GET` | `/api/sales?from=&to=&branchId=&deviceCode=`, `/api/sales/{id}` | Sales history, filterable by branch/device. |
| `GET` | `/api/reports/summary?from=&to=&branchId=` | Z-report with tax breakdown. Range capped at 92 days. |
| `GET` | `/api/platform/regional-presets` | Operator only: built-in country presets. |
| `PUT` | `/api/platform/companies/{id}/regional` | Operator only: save a company's country profile (revision-checked, audited). |
| `GET/PUT/DELETE` | `/api/platform/language-packs[/{code}]` | Operator only: uploaded UI translations. |
| `GET` | `/api/i18n/{code}` | A language pack the company enables (anonymous, for the login screen). |

Push validates each sale against its own lines (totals and tax must reconcile to one unit of
the currency's smallest subunit, e.g. 0.01 DA) and rejects only malformed payloads — never a well-formed sale. A stock shortfall or an exceeded
credit limit is returned as a warning on an accepted sale. A refund is just another sale pushed
through this same endpoint with `status: "Refunded"` and `refundOfSaleId` set — there is no
separate refund API.

---

## Layout

```
ParadisERP.Domain/          entities and core enums
ParadisERP.Application/     contracts and application boundary
ParadisERP.Infrastructure/  persistence, seeding, JWT, and sync engine
ParadisERP.API/             host and minimal API endpoints
docs/                       overview and architecture decisions
ParadisERP.Tests/           backend architecture tests

ParadisERP.WebUI/src/app/
  core/db.ts                Dexie/IndexedDB schema (catalog replica + sales outbox)
  core/sync.service.ts      delta pull, outbox drain, backoff
  core/admin.service.ts     back-office CRUD client (online-only, hits the server directly)
  core/money.ts             tax maths, currency rounding/formatting  ← unit-tested
  core/regional.service.ts  the company's country profile (cached for offline use)
  core/connectivity.service.ts   real reachability probe
  core/auth.service.ts      online password / offline PIN
  core/i18n.service.ts      runtime ar/fr/en + RTL
  features/register/        the till (sale)
  features/{products,sales,sync}/    sales history (reprint + refund), read-only catalog, sync status
  features/backoffice/      store, branches, tills, categories, products, customers, users
  shared/receipt.component.ts    80 mm thermal receipt

scripts/mock-api/           Node mock of the API contract
tests/e2e/                  Playwright offline-to-online cycle
```

## Tests

```bash
cd ParadisERP.WebUI && npm test
```

The backend suite (xUnit, about 2,000 tests, many of them against a real in-process API on its own SQLite file):

```bash
dotnet test ParadisERP.Tests              # one process, about 25 minutes
pwsh scripts/test-parallel.ps1            # three processes side by side, about 15 minutes
pwsh scripts/test-parallel.ps1 -Processes 1   # one process, when memory is short
```

The SQL Server facts run only when `ALGERIAPOS_TEST_SQLSERVER` names a server; otherwise they are skipped.

The end-to-end script drives the real PWA against the mock API through the full cycle —
login, catalog pull, going offline, two sales, page reload, reconnect, automatic drain,
replay idempotency, and offline PIN unlock:

```bash
cd ParadisERP.WebUI && npm run build
node ../scripts/mock-api/server.js &
node ../tests/e2e/offline-cycle.js
```

Known: five of its money checks expect a decimal comma (`65,00`) that the till no longer prints
(`65.00`); they fail on `main` independently of any change (noted 2026-11-12).

---

## Known limits

- **Offline PIN hashes** are replicated to terminals. This is the deliberate trade-off that
  makes offline sign-in possible; it is limited to cashiers, and PINs should be treated as
  till-unlock credentials, not passwords.
- **The back office is online-only.** Store, branch, till, catalog and user management call
  the server directly rather than going through the offline outbox — a manager needs a
  connection to change the catalog or move stock between branches, by design (see
  [01-architecture.md](docs/01-architecture.md)).
- **One refund per sale.** The till can refund a sale in full or in part, but a sale that
  already has a refund recorded against it can't be refunded again — a second, larger refund
  has to be corrected off-system.
- **A branch's stock policy is company-configured, not per-product.** `AllowNegativeStock`
  guards manual stock adjustments and transfers at the branch level; it does not apply to
  sales, which are never blocked for a stock shortfall (see the conflict-rule table above).
- **No cross-deployment reporting.** Back office → Rapports covers everything within one
  deployment (all branches of one company). Consolidating several *separately hosted*
  companies' numbers into one dashboard is left to a periodic export, not built into this
  repo (see [01-architecture.md](docs/01-architecture.md) for why
  that fits the offline-first design better than real-time multi-shop sync).
