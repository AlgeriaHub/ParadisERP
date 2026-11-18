# ParadiERP handoff — 2026-11-18

## Scope and workspace

This is a **fresh, no-history snapshot** of `AlgeriaHub/ParadiERP` at `origin/main` commit
`25387c8` in:

`F:\SeragProjects\ParadiERP\.claude\worktrees\paradierp-public`

It is not yet a Git repository, has not been pushed, and nothing has been changed on the Ubuntu
server. The source ParadiERP working tree remains untouched.

## Completed

- Added a public landing page at the root route (`/`) in Arabic, French, and English.
- The operator experience stays on `/login`, `/register`, and `/backoffice`; its routes and
  behaviour were not changed.
- The page is deliberately styled as an operations desk rather than generic SaaS: a live business
  board, branch-aware features, and an offline-first story.
- Updated the browser title and meta description to ParadiERP.
- Removed the proprietary Thmanyah font declarations from the public-copy stylesheet and changed
  the primary font stack to the open-licensed Readex Pro/Cairo stack.
- Deleted all proprietary `thmanyah-sans-{400,500,700}.woff2` files from `public/fonts/`.
- Sanitized internal repository files: removed `artifacts/`, `Claude outputs/`, `TestResults/`,
  `deploy/smarterasp`, private owner notes, spreadsheets, and personal media from `docs/`.
- Switched GitHub CLI active account from `serageldinahmed` to `seragAhmed`.
- Initialized a fresh Git repository in `paradierp-public` with `.gitignore` shielding local junctions and databases.
- Implemented dedicated public self-service trial endpoint (`POST /api/trials`) in `TrialEndpoints.cs`
  with slug validation, reserved subdomains blocklist, atomic SQLite tenant provisioning via `CompanyProvisioner`,
  and fixed 14-day trial constraints (max 3 users, 1 branch, 2 devices).
- Added unit and API tests in `TrialEndpointsTests.cs` (8/8 passed).
- Added an interactive self-service trial modal on the landing page in all three languages (AR, FR, EN)
  with live slug formatting, bootstrap password presentation, and direct tenant login handoff.

### Files changed

- `ParadiERP.API/Endpoints/TrialEndpoints.cs`
- `ParadiERP.API/Endpoints/ApiEndpoints.cs`
- `ParadiERP.API/Authorization/RoutePermissions.cs`
- `ParadiERP.Application/Regional/UiFonts.cs`
- `ParadiERP.Tests/Tenancy/TrialEndpointsTests.cs`
- `ParadiERP.WebUI/src/app/core/ui-fonts.ts`
- `ParadiERP.WebUI/src/app/features/landing/landing.component.ts`
- `ParadiERP.WebUI/src/app/features/landing/landing.component.html`
- `ParadiERP.WebUI/src/app/features/landing/landing.component.scss`
- `ParadiERP.WebUI/src/app/core/i18n/landing.translations.ts`
- `ParadiERP.WebUI/src/app/core/i18n.service.ts`
- `ParadiERP.WebUI/src/app/app.routes.ts`
- `ParadiERP.WebUI/src/app/app.component.ts`
- `ParadiERP.WebUI/src/app/app.component.html`
- `ParadiERP.WebUI/src/fonts.scss`
- `ParadiERP.WebUI/src/styles.scss`
- `ParadiERP.WebUI/src/index.html`

## Verification

- `dotnet test ParadiERP.Tests --filter 'FullyQualifiedName~TrialEndpointsTests'`: 8/8 tests passed.
- `dotnet build ParadiERP.API`: Succeeded with 0 errors and 0 warnings.
- `npm run build` passed in `ParadiERP.WebUI` on 2026-11-18. All bundles generated cleanly.

## Public-release setup status

- [x] Deleted proprietary `thmanyah-sans-{400,500,700}.woff2` fonts and updated defaults to Readex Pro/Cairo.
- [x] Internal materials audited and removed: `artifacts/`, `Claude outputs/`, `TestResults/`, `.claude/`, `deploy/smarterasp`, private owner notes & media.
- [x] Switched GitHub CLI active account to `seragAhmed` (`gh auth switch --user seragAhmed`).
- [x] Initialized fresh Git repository in `paradierp-public`.
- [ ] Add open-source license file (e.g. MIT).
- [ ] Create remote GitHub repository under `seragAhmed/ParadiERP` and push initial commit when ready.

## Self-service trial design

The existing application already provides the core tenant model:

- Each company has a separate database and file folder.
- `CompanyProvisioner` atomically creates a company database and an administrator.
- Subscription status already supports `Trial` with expiry and resource limits.
- The existing provisioning routes are intentionally platform-operator-only.

The agreed trial URL shape is:

`https://<company-slug>.paradierp.zena4cp.com`

Do **not** use a path such as `/company-slug`: tenancy resolves the request host, so a hostname is
the isolation boundary.

Before public self-service signup can be deployed, arrange:

1. DNS wildcard `*.paradierp.zena4cp.com` pointing to `195.201.109.103`.
2. A wildcard TLS certificate for that hostname family (normally DNS-01 via the DNS provider).
3. A separate platform control-plane configuration at `paradierp.zena4cp.com`, including its
   persistent platform database, Data Protection key storage, and SSO/identity-provider client.
4. Explicit trial rules: duration, permitted users/branches/devices, whether demo data is seeded,
   email verification, abuse/rate limits, expiry cleanup and backup/retention policy.

Never make the existing operator provisioning endpoint anonymous or put trials into a shared
customer database. Implement a dedicated public trial endpoint that validates a slug, provisions
only the derived tenant hostname, applies fixed trial limits, uses anti-abuse controls, and never
returns/logs a long-lived secret beyond the one-time bootstrap handoff.

## Server facts (read-only inspection)

The target Ubuntu server is reachable through the existing `siraj` SSH key, has Docker 29 and
Compose 2.40, 164 GB free disk, about 10 GB readily available RAM, and Nginx already serves
`paradis.zena4cp.com`, `school.zena4cp.com`, `paradistack.com`, and `carasl.com`.

No ParadiERP Nginx site, container, database, DNS change, certificate, or deployment has been
created.
