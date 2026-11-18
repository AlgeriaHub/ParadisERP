# Platform portal and Keycloak setup

The integration is implemented but production Keycloak details will be supplied later.
Leave `Sso:Enabled` and `Platform:Enabled` false until all settings below are available.
Company password login and offline cashier PIN unlock continue to work in the meantime.

## Production configuration

Configure a dedicated operator hostname, separate from every company hostname. The company
registry under `Tenancy:Companies` still controls company provisioning and database selection.
The portal manages subscriptions and identity links for those configured companies.

```json
{
  "Platform": {
    "Enabled": true,
    "Host": "ops.pos.example.com",
    "ConnectionString": "Data Source=/data/platform.db",
    "OperatorSubjects": ["replace-with-the-operator-keycloak-user-id"]
  },
  "Sso": {
    "Enabled": true,
    "Authority": "https://identity.example.com/realms/pos",
    "ClientId": "pos-web"
  }
}
```

Supply `Sso__ClientSecret` and `Jwt__Key` through the deployment secret store or environment.
Do not commit secrets to the repository. The platform database must be a separate persistent
SQLite database. Back it up along with company databases; it stores subscription overrides,
identity links, and audit history. This platform-store implementation targets one API instance;
shared multi-instance platform storage and coordination are not implemented.

In Keycloak, create an OpenID Connect client with client authentication enabled, Standard
Flow enabled, PKCE S256 required, and Direct Access Grants disabled. Register exact redirect
URIs for the operator host and every company host:

```text
https://ops.pos.example.com/signin-oidc
https://alger.pos.example.com/signin-oidc
https://oran.pos.example.com/signin-oidc
```

The confidential client secret stays on the API. Provider tokens are not saved to browser
storage. The operator portal uses an HttpOnly cookie with a 15-minute lifetime and checks the
configured operator subject allowlist. It does not trust company roles as platform roles.

Serve the PWA at the API's origin. Production SSO requires HTTPS. If TLS is terminated by a
proxy, configure trusted forwarded-header processing in the host environment or use HTTPS
between proxy and API so generated callback URLs use HTTPS; preserve the original Host.
Do not enable unrestricted forwarding of headers from untrusted clients. Persist ASP.NET
Core Data Protection keys across container replacements to preserve protected authentication
state. Enable MFA for operator accounts in Keycloak.

Use `https://ops.pos.example.com/#/platform` for the operator portal. After signing in:

1. Select a configured company.
2. Set its plan, status, paid-through time (UTC), grace period, and active resource limits.
3. Enter the reason and save. Subscription changes apply on the next API request; tills
   receive updates through login/pull. Offline grace semantics still apply.
4. Review the change history. Competing edits are rejected using revision checks.
5. In **Country and tax settings**, optionally load a country preset, adjust currency, tax
   rates, fiscal identifiers, languages, cash notes and payment methods, enter a reason and
   save. The same form sets tax-rate labels, cash rounding, the receipt layout and legal
   text. Company administrators only fill in their identifier values. Changing the currency
   does not convert existing prices or history.
6. To add a language other than Arabic, French or English, use the **Languages** panel below
   the company list: download the template, translate the texts, set `code`, `label`, `rtl`
   and `fallbackLanguage`, upload, give a reason and save. Then enable the language in each
   company's country settings. A pack in use cannot be deleted.
7. In Company SSO access, link a Keycloak user ID to an existing local company account.
   The local account supplies the POS role; no account is created automatically by SSO.

On the company's login screen, choose Sign in with Keycloak. The API checks the verified
issuer and subject against that company's explicit link, checks the account and terminal,
then returns the existing company-bound POS token. That POS token continues to use the
existing local storage and revocation model. Removing a link prevents new SSO sessions;
revoke existing POS sessions separately if immediate API access removal is required.

Signing out of the operator portal clears its local cookie. It does not sign the user out
of unrelated applications or end their central Keycloak session.

## Optional local development setup

The files under `deploy/keycloak` are optional development fixtures. They do not change an
existing Keycloak server. Copy `.env.example` to `.env` and set distinct strong values, then:

```text
docker compose --env-file deploy/keycloak/.env -f deploy/keycloak/compose.yml up -d
```

The container is bound to `127.0.0.1:8180`, runs Keycloak 26.7.3 in development mode, and
imports a `pos` realm on its first start. It includes:

* Client ID `pos-web`, with its secret read from `POS_CLIENT_SECRET`.
* Operator subject `00000000-0000-4000-8000-000000000001`, username `operator`.
* Company subject `00000000-0000-4000-8000-000000000002`, username `owner`.

Use `http://localhost:8180/realms/pos` as the development authority and
`ops.localhost` / `shop.localhost` as the operator/company hosts on port 5080. Set the API
environment to Development. Set the client secret to the same value as the Keycloak fixture.
Configure the company and its bootstrap password using the existing provisioning guide.
Serve the built PWA from the API. Open `http://ops.localhost:5080/#/platform` and link the
owner subject to the company's local owner/admin account.

The import is only applied for a new realm; changing the import file does not update an
existing realm. Manage an existing realm through Keycloak's administration UI. This development
container is not a production Keycloak deployment.

## Verification

```text
dotnet build ParadiERP.API
node tests/e2e/sso-platform.cjs
```

The test runs the actual API and a disposable local OIDC issuer. It validates the code flow,
PKCE, nonce rejection, operator authorization, CSRF, live renewal, audit persistence, revision
conflicts, and explicit tenant identity links. It does not contact the production Keycloak
server. CI includes this test.

For visual review only, `node scripts/mock-api/platform-preview.cjs` serves the built portal
at `http://127.0.0.1:5097/#/platform` with in-memory sample companies. It is not an API or SSO
security test and must not be deployed.

References: [Microsoft OIDC guidance](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-oidc-web-authentication?view=aspnetcore-10.0),
[Keycloak container guide](https://www.keycloak.org/server/containers).
