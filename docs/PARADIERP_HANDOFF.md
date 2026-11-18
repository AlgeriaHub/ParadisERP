# ParadisERP handoff — 2026-11-18

## Scope and workspace

This is a deployed release of `AlgeriaHub/ParadisERP` at `origin/main` commit
`4e2a6dd` (and successors) in:

`F:\SeragProjects\AlgeriaPos\.claude\worktrees\paradiserp-public`

The project has been migrated and renamed to **ParadisERP**, hosted on GitHub at:
`https://github.com/AlgeriaHub/ParadisERP`

The service is fully deployed and running in production on the Ubuntu server `195.201.109.103`.

## Completed

- Added a public landing page at the root route (`/`) in Arabic, French, and English.
- The operator experience stays on `/login`, `/register`, and `/backoffice`; its routes and
  behaviour were not changed.
- The page is deliberately styled as an operations desk rather than generic SaaS: a live business
  board, branch-aware features, and an offline-first story.
- Updated the browser title and meta description to ParadisERP.
- Removed the proprietary Thmanyah font declarations from the public-copy stylesheet and changed
  the primary font stack to the open-licensed Readex Pro/Cairo stack.
- Deleted all proprietary `thmanyah-sans-{400,500,700}.woff2` files from `public/fonts/`.
- Sanitized internal repository files: removed `artifacts/`, `Claude outputs/`, `TestResults/`,
  `deploy/smarterasp`, private owner notes, spreadsheets, and personal media from `docs/`.
- Switched GitHub CLI active account from `serageldinahmed` to `seragAhmed`.
- Initialized a fresh Git repository in `paradiserp-public` with `.gitignore` shielding local junctions and databases.
- Implemented dedicated public self-service trial endpoint (`POST /api/trials`) in `TrialEndpoints.cs`
  with slug validation, reserved subdomains blocklist, atomic SQLite tenant provisioning via `CompanyProvisioner`,
  and fixed 14-day trial constraints (max 3 users, 1 branch, 2 devices).
- Added unit and API tests in `TrialEndpointsTests.cs` (8/8 passed).
- Added an interactive self-service trial modal on the landing page in all three languages (AR, FR, EN)
  with live slug formatting, bootstrap password presentation, and direct tenant login handoff.

### Files changed

- `ParadisERP.API/Endpoints/TrialEndpoints.cs`
- `ParadisERP.API/Endpoints/ApiEndpoints.cs`
- `ParadisERP.API/Authorization/RoutePermissions.cs`
- `ParadisERP.Application/Regional/UiFonts.cs`
- `ParadisERP.Tests/Tenancy/TrialEndpointsTests.cs`
- `ParadisERP.WebUI/src/app/core/ui-fonts.ts`
- `ParadisERP.WebUI/src/app/features/landing/landing.component.ts`
- `ParadisERP.WebUI/src/app/features/landing/landing.component.html`
- `ParadisERP.WebUI/src/app/features/landing/landing.component.scss`
- `ParadisERP.WebUI/src/app/core/i18n/landing.translations.ts`
- `ParadisERP.WebUI/src/app/core/i18n.service.ts`
- `ParadisERP.WebUI/src/app/app.routes.ts`
- `ParadisERP.WebUI/src/app/app.component.ts`
- `ParadisERP.WebUI/src/app/app.component.html`
- `ParadisERP.WebUI/src/fonts.scss`
- `ParadisERP.WebUI/src/styles.scss`
- `ParadisERP.WebUI/src/index.html`

## Verification

- `dotnet test ParadisERP.Tests --filter 'FullyQualifiedName~TrialEndpointsTests'`: 8/8 tests passed.
- `dotnet build ParadisERP.API`: Succeeded with 0 errors and 0 warnings.
- `npm run build` passed in `ParadisERP.WebUI` on 2026-11-18. All bundles generated cleanly.

## Public-release setup status

- [x] Deleted proprietary `thmanyah-sans-{400,500,700}.woff2` fonts and updated defaults to Readex Pro/Cairo.
- [x] Internal materials audited and removed: `artifacts/`, `Claude outputs/`, `TestResults/`, `.claude/`, `deploy/smarterasp`, private owner notes & media.
- [x] Switched GitHub CLI active account to `seragAhmed` (`gh auth switch --user seragAhmed`).
- [x] Initialized fresh Git repository in `paradiserp-public`.
- [x] Transferred and renamed GitHub repository to `AlgeriaHub/ParadisERP`.
- [x] Solution, projects, namespaces, and Docker references fully renamed to ParadisERP.
- [x] Production deployment live on server `195.201.109.103` (`paradiserp-web` container, port `5085`).
- [x] DNS A records configured for `paradiserp.zena4cp.com` and `*.paradiserp.zena4cp.com`.
- [x] Nginx reverse proxy configured with Let's Encrypt SSL (HTTP 200 at `https://paradiserp.zena4cp.com`).
- [x] Self-service trial endpoint (`POST /api/trials`) verified live with isolated SQLite database creation and tenant authentication.
- [ ] Wildcard TLS certificate (`*.paradiserp.zena4cp.com`) via DNS-01 TXT record.

## Server facts and deployment status

The target Ubuntu server (`195.201.109.103`) is running:
- **Container**: `paradiserp-web` (built from `Dockerfile` in `/opt/paradiserp/app`, image `paradiserp:latest`).
- **Internal Port**: `127.0.0.1:5085 -> 5080`.
- **Nginx Config**: `/etc/nginx/sites-available/paradiserp` proxying to `http://127.0.0.1:5085`.
- **Data Persistence**: `/opt/paradiserp/data` mounted into container `/data`:
  - `platform.db`: Platform registry containing provisioned trial companies and subscriptions.
  - `companies/<slug>.db`: Separate SQLite database per company/trial.
  - `keys/`: ASP.NET Core Data Protection key ring.
- **Root Landing Page**: `https://paradiserp.zena4cp.com` (200 OK, SSL active).
- **Tenant Subdomains**: `https://<slug>.paradiserp.zena4cp.com` (resolves via wildcard DNS, routed by Nginx).
