# Company provisioning from the operator portal

> **Decision of 2026-09-23:** the system runs at one company with several branches, and another
> company gets its own copy on its own domain — not a second company in this portal. The flow
> below stays in the code but is not enabled, and gets no further investment unless the owner
> says otherwise.

The platform operator can create, re-host, archive and restore companies in the portal
(`/#/platform`) while the API keeps running. Companies defined in `Tenancy:Companies` still
work exactly as before and appear read-only in the portal ("From server configuration").

## How it works

* **Live registry.** `TenantRegistry` holds an immutable snapshot of all companies
  (configuration + portal). Every change builds a new snapshot, re-validates it as a whole
  (unique IDs and hostnames, no shared database, no company on the operator hostname,
  valid country profile) and swaps it in atomically. A request always sees one consistent set.
* **Storage.** Portal companies live in the platform database, table `ProvisionedCompanies`
  (ID, hostnames, provider, admin username, country preset, state, revision). The connection
  string is encrypted with ASP.NET Data Protection (purpose `AlgeriaPos.Companies`).
* **Startup.** After migrating the platform database, the API loads portal companies into
  the registry, then migrates and seeds every non-archived company. If a connection string
  cannot be decrypted, startup stops with a message pointing at `DataProtection:KeysPath`.
* **Hosted mode without configured companies.** With `Platform:Enabled=true` the API now
  starts in hosted mode even when `Tenancy:Companies` is empty.

## Creating a company

Portal → **New company**: ID (`a-z`, `0-9`, `-`, 3–40 characters), hostnames, database
(MySQL or SQLite), country preset, administrator username, initial plan/status/
expiry/grace and a reason.

* **SQLite:** the database is created as `<Platform:CompanyDataDirectory>/<id>.db`
  (default: a `companies` folder next to the platform database). An existing file is never
  adopted or overwritten.
* **MySQL:** give a connection string with a database name. A database that
  already contains a company is refused.
* The API migrates the new database, seeds the store, main branch, till `C01` and the
  administrator, saves the company and its subscription, then publishes it. If any step
  fails, the company is not registered and a SQLite file it created is removed.
* A random 20-character **bootstrap password** is returned once, in the create response
  only. It is never stored (only the BCrypt hash in the company database), logged or audited.
  The portal shows it with a copy button until the operator clicks **Done**.

## Changing a company

In the company detail, **Hostnames and status** (portal companies only):

* **Save hostnames** replaces the hostname list (revision-checked). Old hostnames stop
  resolving immediately.
* **Archive company** keeps the database and hostnames reserved, but the company's addresses
  return 404 and its database is skipped at startup.
* **Restore company** migrates the database to the current schema, then makes it reachable.

Every change needs a reason and is recorded in the company's change history
(`company.created`, `company.hosts`, `company.archived`, `company.restored`) without
connection strings or passwords.

## API (operator only, antiforgery token required on writes)

| Method | Route | Body |
|---|---|---|
| `POST` | `/api/platform/companies` | `{ id, hosts[], databaseProvider, connectionString?, country, adminUsername, subscription, reason }` → `201 { id, adminUsername, bootstrapPassword }` |
| `PUT` | `/api/platform/companies/{id}/hosts` | `{ hosts[], revision, reason }` |
| `POST` | `/api/platform/companies/{id}/archive` | `{ revision, reason }` |
| `POST` | `/api/platform/companies/{id}/restore` | `{ revision, reason }` |

`GET /api/platform/companies` now also returns `source` (`Configuration`/`Portal`),
`state` (`Active`/`Archived`), `companyRevision` and `createdAtUtc`.
Errors: `400` validation or configuration company, `404` unknown, `409` duplicate ID,
existing database or stale revision.

## Operations

* **Persist the Data Protection key ring** (`DataProtection:KeysPath`; the Docker image uses
  `/data/keys`). Without it, portal companies cannot be loaded after the keys are lost.
  Back it up with the platform database.
* Back up the company folder (`Platform:CompanyDataDirectory`) with the platform database.
* Point DNS / the reverse proxy for a new hostname at the API; the proxy must forward the
  original `Host` header. Register the hostname's `/signin-oidc` callback in Keycloak if the
  company will use SSO.
* Provisioning changes are serialized within one API process. As before, the platform store
  targets a single API instance; running several instances needs shared coordination.

## Needed changes in `docs/04-platform-and-keycloak-setup.md`

* Replace "Company creation is still configuration-driven" with the portal flow above.
* Add `Platform:CompanyDataDirectory` to the production configuration example, and state
  that `DataProtection:KeysPath` is required when companies are created in the portal.
* Add steps: **New company** → copy the one-time password → **Done**; archive/restore and
  hostname editing in the company detail.
* Note that the platform may start with no `Tenancy:Companies` entries.

## Not included

* Deleting a company or its database (archive instead; remove data manually if required).
* Moving a company to another database provider, or rotating its connection string.
* Changing the ID or the administrator username after creation.
