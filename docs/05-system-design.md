# POS system design — merged with the current codebase

Last updated: 2026-09-23.

This document merges the product-level "POS System — Architecture Design" (a generic,
multi-vertical, offline-first POS) with what this repository actually implements. Where
the two differ, the codebase decision wins and the reason is recorded. Each section marks
its state: **Implemented**, **In progress**, or **Planned**.

Related documents:
[architecture](01-architecture.md) ·
[SaaS, subscriptions and identity](02-saas-subscriptions-and-identity.md) ·
[work status](03-work-status.md) ·
[platform and Keycloak setup](04-platform-and-keycloak-setup.md)

---

## 1. Principles

1. **Offline-first.** A till completes sales, prints receipts and unlocks cashiers with no
   internet. Sales are written locally first and uploaded later. *(Implemented)*
2. **One core, configured per market.** Algeria is the first market, but nothing
   country-specific is hard-coded: currency, tax rates, fiscal identifiers, languages,
   time zone, cash denominations and payment methods are **settings**. *(Implemented, §5)*
3. **Sync-safe by design.** Each data type flows in one direction, so there is nothing to
   merge. Retries are idempotent. *(Implemented)*
4. **Platform-owned configuration.** Anything that has legal or financial meaning for a
   company — its country profile, subscription and integrations — is set by the platform
   operator (super admin), not by the company's own admin. *(Implemented for country
   profiles and payment methods; card/e-invoicing integrations planned, §6)*
5. **Small PCI scope.** Card data never touches this application. *(Planned, §9)*

## 2. Scope

### Functional

| Area | State | Notes |
|---|---|---|
| Catalog: products, categories, barcodes, units, tax rate per product | Implemented | Trilingual names (ar/fr/en). |
| Variants (size/colour), modifier groups, combos | Planned | Needed for restaurants and fashion retail. |
| Multi-branch stock, transfers, adjustments, low-stock | Implemented | `ProductStock` per (product, branch) plus a `StockMovement` audit trail. |
| Register: cart, discounts, cash/card/credit, change, receipts | Implemented | Payment methods are per-company settings (§5). |
| Split tender (several methods on one sale) | Implemented | Up to four parts; change only from cash; recorded as `Mixed` with one `SalePayment` per method. |
| Refunds | Implemented | One refund per original sale. |
| Customers, credit limit, balance | Implemented | Required fields are an admin setting. |
| Loyalty points, gift cards, layaway | Planned | |
| Restaurant: tables, kitchen display, courses, tips | Planned | See §10 — needs order editing, which changes the sync model. |
| Staff: roles, PIN unlock, session revocation | Implemented | Cashier / Manager / Admin are seeded system roles; since 2026-10-03 access is decided from role and permission tables (AUTH-1, `REDESIGN_PLAN.md` §17). Since 2026-11-03 the company makes its own roles on Administration › Roles & permissions (AUTH-1 Steps 5–6). |
| Clock-in/out | Planned | |
| Reports: Z-report, tax breakdown, per-branch, sales list | Implemented | |
| Multi-company SaaS with operator portal | Implemented, not enabled | Hostname → company database. Decision of 2026-09-23: one company per copy, on its own domain; this layer stays in the code and gets no further investment unless asked for. |
| Country profiles set by the operator | Implemented | §5. |
| Hardware: receipt printing | Implemented | Browser print (80 mm or A4). |
| Hardware: cash drawer, ESC/POS, customer display, scale | Planned | |

### Non-functional

| Requirement | How it is met |
|---|---|
| Checkout survives internet and server outages | IndexedDB replica + outbox on each till. |
| Fast UI regardless of connectivity | All register reads are local. |
| Correct money over real-time consistency | Server re-checks line totals and tax per sale, with a tolerance derived from the currency's decimals. |
| Tenant isolation | One database per company, selected from a trusted hostname registry before authentication; company-bound JWTs. |
| Localisation | Arabic (RTL), French, English UI; country profile per company. |

### Out of scope for v1

E-commerce storefront, payroll, a built-in accounting ledger (export instead).

## 3. Topology

The product design proposed a per-store **edge node** between tills and the cloud. This
codebase uses a simpler shape that covers the same needs for retail:

```text
            ┌───────────── Hosted (SaaS) ─────────────┐
 Till PWA ──┤  API (company resolved from hostname)   ├── Company DB (one per company)
 (IndexedDB)│  Operator portal (separate hostname)    ├── Platform DB (subscriptions,
            └──────────────────────────────────────────┘      country profiles, audit, SSO links)

            ┌──────────── Single shop (LAN) ───────────┐
 Till PWA ──┤  Same API + PWA in one process            ├── MySQL 8 (production) / SQLite (dev)
            └──────────────────────────────────────────┘
```

* The **single-process LAN install** plays the role of the edge node: tills on the shop
  network keep syncing with it even when the shop's internet is down.
* A **hosted** company skips the local server: each till is its own offline replica.
* **Decision:** no separate edge-node product for now. It only pays off once several tills
  must edit the *same* open order while offline (restaurants, §10).

## 4. Technology stack

| Layer | Product design proposed | This codebase | Why |
|---|---|---|---|
| Till app | React + Electron/Capacitor | **Angular 20 PWA** | Installs from the browser, no store or installer; one build for desktop and tablet. |
| Till storage | SQLite | **IndexedDB (Dexie)** | Native to the PWA; transactional; survives reloads. |
| Backend | Node/NestJS or Go | **ASP.NET Core 8, minimal APIs** | Existing team stack; layered Domain/Application/Infrastructure/API. |
| Company database | PostgreSQL + row-level security | **MySQL 8 in production; SQLite for development, CI and tests; one database per company** | Strong isolation without RLS policies. SQL Server was removed on 2026-09-23: a single-company deployment gains nothing from a third provider, and it cost a migration set per schema change. |
| Platform database | — | **SQLite** | Subscriptions, country profiles, audit, SSO links. |
| Sync backbone | Kafka / queues | **Version-cursor pull + idempotent push over HTTPS** | No broker to run; batches resume on weak lines. |
| Identity | Email/password + SSO | **Local accounts + PIN; Keycloak (OIDC) for SSO and operators** | |
| Payments | Stripe Terminal / Adyen / Square | **None integrated yet** | Those providers do not serve Algerian merchants; see §9. |
| Deployment | Kubernetes + Docker Compose | **Single process or one container (non-root, `/data` volume, CI-built); compose files for MySQL and Keycloak** | |

## 5. Country profiles (regional settings) — Implemented

### Goal

The same build must sell in Algeria today and in another country tomorrow, by changing
settings only. The platform operator owns these settings per company.

### What a country profile contains

| Setting | Algeria value | Used by |
|---|---|---|
| `countryCode` | `DZ` | Number/date locale (`fr-DZ`, `ar-DZ`, …). |
| `currencyCode` | `DZD` | Reports, exports. |
| `currencyDecimals` | `2` | Rounding on till and server; server total tolerance. |
| `currencySymbols` | `fr: DA`, `en: DA`, `ar: دج` | Money display per UI language. |
| `currencySymbolFirst` | `false` | `1 250,00 DA` vs `€ 12.50`. |
| `timeZone` | `Africa/Algiers` | Receipt and report time display. |
| `languages` / `defaultLanguage` | `fr, ar, en` / `fr` | Language switcher and first-run language. |
| `taxNames` | `fr: TVA`, `en: VAT`, `ar: ض.م` | Every label that names the tax. All three are the abbreviation, so the till's tick, the receipt line and the invoice column read the same whoever is at the terminal. |
| `taxRates` / `defaultTaxRate` | `19, 9, 0` / `19` | Product form choices; server validates product rates. |
| `taxRateLabels` | `19: Taux normal`, `9: Taux réduit`, `0: Exonéré` (per language) | Product form, till, receipt, reports. |
| `pricesIncludeTax` | `false` | Whether a stored price already contains the tax. `false` means the stored price is the price before tax and a taxed document adds the rate on top. |
| `fiscalIds` | `NIF`, `NIS`, `RC`, `AI` | Company and branch form fields; the receipt and A4 headers, the branch's value first, then the main branch's, then the company's. |
| `customerTaxIdLabels` | `NIF` | Customer form and required-field setting. |
| `cashDenominations` | `100, 200, 500, 1000, 2000, 5000` | Quick-tender buttons. |
| `cashRoundingIncrement` / `cashRoundingMode` | `0` (off) / `Nearest` | Cash amount due rounded to a step (e.g. `0.05`), `Nearest`, `Up` or `Down`. |
| `paymentMethods` | `Cash, Card, Credit` | Which tender buttons the till shows. |
| `receiptSections` | `header, fiscalIds, meta, lines, totals, taxBreakdown, payment, legal, footer` | Which receipt blocks print, in order. `lines` and `totals` are mandatory. |
| `receiptLegalText` | empty (Saudi preset: *Simplified tax invoice*) | Legal wording per language, printed in the `legal` block (500 characters). |

`languages` may include codes other than `ar`, `fr` and `en` (e.g. `tzm`, `es`) once the
operator has uploaded a **language pack** for them (see below). Every per-language text in
the profile then accepts that language too.

Built-in **presets** give the operator a starting point: Algeria, Morocco, Tunisia, France,
Saudi Arabia. A preset is copied into the company's profile and can then be edited.

### Ownership and flow

```text
Operator portal ──PUT /api/platform/companies/{id}/regional──► Platform DB (revisioned, audited)
                                                                   │
Company request ──► hostname → company ──► profile = platform override
                                             ?? company config (Tenancy:Companies[].Regional)
                                             ?? Algeria preset
                                                                   │
Login + every pull ◄── profile returned alongside subscription ◄───┘
Till caches it in IndexedDB and uses it offline.
```

* The company admin **cannot** change currency, tax rates, tax-inclusive pricing or which
  fiscal identifiers exist. The company admin **does** fill in the identifier values
  (e.g. its own NIF number) in Back office → Store.
* Single-shop installs (no platform) read the profile from the `Regional` configuration
  section; omitted → Algeria.
* Edits use the same revision check and audit log as subscription changes.

### Configuring a company

Hosted companies (`Tenancy:Companies`) and single-shop installs accept either a preset code or
a full profile. The operator portal's saved profile wins over both.

```json
{ "Tenancy": { "Companies": [ { "Id": "casa-shop", "Country": "MA", "...": "..." } ] } }
```

Single shop (`appsettings.json` or environment `Country=TN`):

```json
{ "Country": "TN" }
```

A `Regional` section with the full object from the table above (as in
`GET /api/platform/regional-presets`) replaces the preset; when it is set, `Country` is
ignored. Omitting both means Algeria. An invalid profile stops the API at startup with the reason.

In the operator portal (`/#/platform`), select a company → **Country and tax settings** →
optionally **Load preset** → adjust → enter a reason → **Save country settings**. The change
is audited, revision-checked, applies to the next API request, and reaches tills on their next
sign-in or pull.

### Cash rounding

Only cash payments are rounded. The sale keeps its exact `GrandTotalTtc`, line totals and
tax; the difference is stored in `Sale.RoundingAdjustment` (amount due = total + adjustment),
printed on the receipt as *Cash rounding* / *Amount due*, and reported separately as
`roundingTotal` in the Z-report. Refunds are not rounded. The server refuses only an
adjustment larger than the sale itself, because the step may have changed since the sale.

### Language packs

A pack is `{ code, label, rtl, fallbackLanguage, strings }`, where `strings` uses the web app's
translation keys. In the operator portal (**Languages** panel) the operator downloads a
template containing every English text, has it translated, and uploads it; saves are
revision-checked and audited, and a pack cannot be deleted while a company enables it.
Single-shop installs list packs in the `LanguagePacks` configuration section instead.

Tills receive the revision of each enabled pack with login and every pull, download changed
packs from `GET /api/i18n/{code}` (anonymous, only for languages the company enables), and
keep them in IndexedDB for offline use. Missing texts fall back to the pack's fallback
language, then French. Product, category and branch names still come from the Arabic,
French and English name fields.

### Server-side enforcement

* Product create/update: the tax rate must be one of the profile's `taxRates`.
* Store update: fiscal identifier keys must exist in the profile; values are length-limited.
* Sale push: total/tax tolerance is one unit of the currency's smallest subunit. A sale is
  **never** rejected because a rate or payment method was later removed — it already happened.

### Where the code lives

| Concern | Location |
|---|---|
| Profile model, validation, presets | `ParadisERP.Application/Regional/` |
| Per-company resolution (override → config → preset) | `TenantOptions.ConfiguredRegional`, `TenantContext.Regional` |
| Operator storage and audit | `PlatformStore.ReadRegionalAsync/UpdateRegionalAsync`, table `RegionalOverrides` |
| Operator API | `GET /api/platform/regional-presets`, `PUT /api/platform/companies/{id}/regional`, `GET/PUT/DELETE /api/platform/language-packs[/{code}]` |
| Language packs | `LanguagePack`, `ILanguagePackStore` (platform table `LanguagePacks` or `LanguagePacks` configuration), `LanguagePackService` on the till |
| Till | `RegionalService` (cached in IndexedDB), `I18nService` (money, dates, `{tax}`/`{taxId}` labels) |
| Portal editor | `features/platform/regional-editor.component.ts`, `features/platform/language-packs.component.ts` |

### Data model changes

| Before | After |
|---|---|
| `Store.Nif / Nis / Rc / Ai` columns | `Store.FiscalIdsJson` (key → value) |
| `Store.CurrencyCode`, `Store.PricesIncludeTax` | Moved to the country profile |
| `Customer.Nif`, `Store.RequireCustomerNif` | `Customer.TaxId`, `Store.RequireCustomerTaxId` |
| `Product.TaxRate` default `19` | Profile `defaultTaxRate` |

Migrations move the existing values; no data is lost.

## 6. Integrations (operator-controlled)

**Implemented:** the operator chooses which payment methods each company's tills offer
(part of the country profile). Refunds may still go back through the original sale's method.

**Planned:** an `integrations` section per company in the platform database, edited only in
the operator portal, with credentials held server-side and never sent to tills:

| Slot | Algeria candidates | Other markets |
|---|---|---|
| Card terminal / acquirer | SATIM (CIB, Edahabia) via a bank acquirer | Stripe Terminal, Adyen, Square, local acquirers |
| E-invoicing / fiscal reporting | None mandated for retail today | ZATCA (Saudi Arabia), French anti-fraud certification (NF525) |
| SMS / e-mail receipts | Local SMS gateway | Any |
| Accounting export | CSV | QuickBooks / Xero |

## 7. Data model (current)

| Entity | Notes |
|---|---|
| `Store` | One per company database: names, address, fiscal identifier values, receipt footer, customer-field policy. Its identity heads the papers of no branch (statements, the ageing board) and fills what no branch has. |
| `Branch` | Selling locations and warehouses (`IsWarehouse`; `IsMain` is the main one of each kind); negative-stock policy; a three-digit `Code`. Its own names, address, phone and fiscal identifier values head every paper raised there — A4 documents and the till's receipt — with the main branch's where it has none (`core/document-issuer.ts`). |
| `Device` | A till, registered to one branch; issues its own receipt numbers (`C01-000042`). |
| `Category`, `Product` | Replicated to tills; product tax rate from the profile. |
| `ProductStock`, `StockMovement` | Stock per branch plus the audit trail of every change. |
| `Customer` | Credit limit and balance; tax ID labelled by the profile. |
| `User` | Role, password hash (server only), PIN hash (cashiers only replicated). |
| `Sale`, `SaleLine`, `SalePayment` | Immutable; refunds are new sales linked by `RefundOfSaleId`; `RoundingAdjustment` holds cash rounding. |
| `SyncCounter` | Global version used as the pull cursor. |
| Platform: `SubscriptionOverride`, `RegionalOverride`, `LanguagePackRow`, `PlatformAudit`, `ExternalIdentity` | Operator-owned, outside company databases. |

The product design's append-only inventory ledger exists here as `StockMovement`, but the
authoritative figure is the per-branch `ProductStock` row updated in the same transaction.

## 8. Sync strategy

| Data | Direction | Conflict rule |
|---|---|---|
| Store, branches, categories, products, customers, cashiers | server → till | Server wins; tills are read-only replicas. |
| Country profile, subscription | server → till | Delivered on login and every pull; cached for offline use. |
| Sales and refunds | till → server | Client wins, append-only, idempotent on the till-generated ID. |
| Stock, customer balance | server-owned | Tills keep an estimate and overwrite it on the next pull. |

The product design's per-entity conflict resolution with vector clocks is **not needed**
while no row is editable on both sides. It becomes necessary with shared open orders (§10).

## 9. Security and compliance

* Company isolation: trusted hostname registry → per-company database; tokens carry the
  company and are rejected elsewhere.
* Operators: separate hostname, SSO, subject allowlist, antiforgery, audit.
* Offline PIN hashes: cashiers only.
* Payments: when a card integration is added it must tokenise at the terminal so card data
  never reaches this API (PCI SAQ-A/A-EP scope).
* Encryption at rest for till data relies on device disk encryption (IndexedDB has none).

## 10. Roadmap

| Phase | Content | State |
|---|---|---|
| 1 | Offline retail checkout, sync, receipts, back office | Done |
| 2 | Multi-branch stock, reports, SaaS isolation, subscriptions, SSO, operator portal | Done |
| 3 | **Country profiles and operator-owned settings** | Done |
| 4 | Split tender at the till; working container image | Done |
| 4b | Operator-owned integrations (card acquirer, SMS, e-invoicing) | Planned |
| 5 | Vertical depth: variants/modifiers; restaurant tables and kitchen display (requires shared open orders, an in-store sync hub and per-entity conflict rules) | Planned |
| 6 | Portal-driven company provisioning, recurring billing, accounting exports | Planned |

## 11. Risks and open questions

* **Card payments in Algeria** depend on a bank/SATIM agreement; until then card sales are
  recorded as "card" with an external terminal.
* **Removing a tax rate** from a profile leaves existing products on the old rate until they
  are edited; the product form flags it.
* **Changing a company's currency** after it has sales mixes currencies in history. The
  portal warns; conversion is not provided.
* **Restaurant mode** is a larger change than retail variants because it needs editable
  orders shared between tills.
