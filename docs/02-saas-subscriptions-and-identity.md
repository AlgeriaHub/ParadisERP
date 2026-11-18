# Hosted companies, subscriptions, and identity

> **Deployment decision, 2026-09-23.** The system runs at one company with several branches.
> Another company gets its own copy of the software on its own domain with its own
> configuration and database — not a second entry in this registry. The hosted multi-company
> layer described below stays in the code but is not enabled, and gets no further investment
> unless the owner says otherwise. Production runs on MySQL 8; SQLite is for development, CI
> and the unit tests.

## Implemented deployment model

Each company has an explicit hostname and a separate database. A trusted hostname registry
selects the database before authentication. Unknown hosts return 404. Login tokens contain
the company ID, and a token issued for one company is rejected at another company's hostname.
The browser's IndexedDB is isolated by origin; open each company at its own HTTPS hostname.
Do not serve several companies from paths on a shared browser origin.

This is a modular monolith with database isolation. Application subscription rules and
interfaces do not depend on the HTTP host or a payment processor. Infrastructure supplies
the currently configured subscription; API filters enforce writes and capacity limits.
There is no shared tenant table or client-supplied database selector.

Existing deployments without `Tenancy:Companies` retain the single-company database and
unrestricted plan. Existing JWTs need a new online login after this upgrade because they
lack company and user-version claims. Local unsent sales are preserved.

## Manual provisioning and activation

Supply company entries through server configuration or environment variables. This data is
owned by the platform operator; shop administrators cannot edit it through any POS endpoint.
For example, add the following section to the deployment's configuration, outside Git:

```json
{
  "Tenancy": {
    "Companies": [
      {
        "Id": "shop-alger",
        "Hosts": ["alger.pos.example.com"],
        "DatabaseProvider": "Sqlite",
        "ConnectionString": "Data Source=/data/shop-alger.db",
        "AdminUsername": "owner",
        "Subscription": {
          "Plan": "Standard",
          "Status": "Active",
          "ExpiresAtUtc": "2026-12-01T00:00:00Z",
          "GraceDays": 7,
          "MaxUsers": 10,
          "MaxBranches": 3,
          "MaxDevices": 5
        }
      }
    ]
  }
}
```

Set `Tenancy__Companies__0__BootstrapAdminPassword` using the deployment secret store
(at least 8 characters), and set a strong `Jwt__Key`. Bootstrap credentials are used only
for an empty company database and may be removed after provisioning. Hosted companies get
one administrator, a store, one main branch, and terminal C01; no demo inventory, cashier,
or shared demo password is created. Sign in as the configured administrator using C01.

Add another company entry with a different ID, hostname, database, and bootstrap secret.
Use a dedicated MySQL database and database credential per company when deploying
multiple API replicas. SQLite requires persistent storage and is intended for a single API
instance in development. Back up each company database independently.

To activate or renew manually, update `Status` to `Active` and set `ExpiresAtUtc` to the end
of the paid period. To suspend, set `Status` to `Suspended`. Restart the API after configuration
changes to company/database configuration. When the platform portal is enabled, subscription
changes are stored separately and take effect on the next request without restarting. The
portal uses a separate hostname and operator authorization boundary. See
[Platform and Keycloak setup](04-platform-and-keycloak-setup.md).

Terminate HTTPS at the trusted reverse proxy, preserve the original Host header, and route
only configured company hostnames. Do not rewrite every company to the same upstream Host.
Serve the PWA on each company's hostname and use same-origin API requests. Configure CORS
explicitly if a separate frontend is required. Do not use URL aliases or symbolic links to
point two companies at the same physical database; duplicate configured database identities
are rejected at startup, but infrastructure aliases must also be managed correctly.

## Subscription behavior

Supported configured states: `Trial`, `Active`, `Suspended`, `Cancelled`, `Unrestricted`.
Trial and active subscriptions require an expiry. After the paid period, the effective state
is `Grace`, then `Expired`. Grace is 0–30 days and the final deadline is exclusive.

* Expired, suspended, or cancelled plans block administrative writes and new checkouts/refunds
  in the supplied PWA after it receives the subscription state.
* Login, subscription status, historical reads, catalog pulls, and completed-sale uploads
  remain available. A subscription change never deletes financial records or the outbox.
* Limits count active non-deleted users/branches and active devices. Creation and reactivation
  both enforce limits inside the write transaction. Editing existing active records remains
  possible when at a limit. Existing records are not deleted when a plan is downgraded.
* A cached absolute deadline lets a till sell offline through the granted grace period. A
  suspension reaches a disconnected till on its next successful login or pull.
* Browser storage and clocks are customer-controlled. The cached deadline is an operational
  offline policy, not tamper-proof licensing. The reconciliation API intentionally accepts
  completed sales after expiry; it cannot reliably distinguish a late offline upload from
  a modified client's new sale using client timestamps alone. Stronger licensing requires
  signed terminal leases, bounded renewal, and an explicit commercial offline policy.

The back-office Subscription tab shows plan, state, access deadline, and current usage in
Arabic, French, and English. `GET /api/subscription` returns access state; authenticated
managers/admins can use `GET /api/subscription/usage`. No billing credentials are sent to tills.
The platform operator portal now supports subscription updates, audit history, and company
SSO links. Automatic payments, invoices, subscription billing tax handling, and payment
webhooks remain deferred because activation is manual in this phase.

## SSO decision

For the intended hosted product, prefer **Keycloak for online SSO** when operating an identity
server is acceptable. It supports OpenID Connect, OAuth 2.0, SAML, federation, and organization
identity integration. Keep tenant membership, business permissions, and subscription
entitlements in the POS application; an identity-provider login alone does not grant company access.

ASP.NET Core Identity is a good account-management option for a single application. Its
built-in bearer endpoints are not a full standards-based SSO server. An embedded SSO solution
would need an OIDC server such as OpenIddict in addition to Identity, plus its operational work.

The intended integration is Authorization Code with PKCE for online users, ideally through
a backend-for-frontend with HttpOnly cookies. Map an external identity using issuer + subject
to an explicit company membership; never map by an unverified email or trust a browser's
tenant ID. Use MFA for platform operators and company administrators. Keep the local cashier
PIN workflow separate for offline operation. Do not put subscription state into long-lived
SSO tokens as the only enforcement mechanism.

Keycloak integration is implemented using Authorization Code with PKCE and a server-side
OIDC client. It remains disabled until the real authority and client configuration are supplied.
The current password/JWT login remains functional. API requests check the
current user's active state/version and device status, so account changes and the admin
`POST /api/users/{id}/revoke-sessions` action invalidate existing online sessions. Offline
PIN unlock remains separate. Administrators cannot demote/deactivate themselves; at least
one active administrator must remain.

References:

* [Keycloak application security](https://www.keycloak.org/securing-apps/overview)
* [Keycloak production configuration](https://www.keycloak.org/server/configuration-production)
* [Microsoft Identity API guidance](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-api-authorization?view=aspnetcore-10.0)

## Verification

```text
dotnet test ParadiERP.Tests
cd ParadiERP.WebUI
npm test -- --watch=false --browsers=ChromeHeadless
npm run build
cd ..
node tests/e2e/saas-api.cjs
```

The real-HTTP smoke test provisions two disposable company databases and checks cross-company
token rejection, data isolation, unknown host rejection, subscription write restrictions,
read/sync availability, limits, self-demotion protection, and session revocation.
