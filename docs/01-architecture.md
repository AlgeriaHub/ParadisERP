# Architecture

Hosted deployments now support an explicit company hostname registry, one database per
company, company-bound tokens, and operator-owned subscription entitlements. See
[SaaS, subscriptions, and identity](02-saas-subscriptions-and-identity.md) for provisioning,
offline expiry behavior, and the SSO decision.

Country-specific behavior (currency, taxes, fiscal identifiers, languages, payment methods)
comes from an operator-owned country profile per company; see
[system design §5](05-system-design.md). The merged product design and roadmap live in
[05-system-design.md](05-system-design.md).

The solution follows the layered structure used by the Riaya, CarRental, and AfaqGrin projects.

## Projects

| Project | Responsibility |
|---|---|
| `AlgeriaPos.Domain` | Entities and enums with no dependency on outer layers. |
| `AlgeriaPos.Application` | Transport-neutral application contracts and future use cases. |
| `AlgeriaPos.Infrastructure` | EF Core persistence, seeding, token generation, and sync. |
| `AlgeriaPos.API` | Composition root, authentication, middleware, and HTTP endpoints. |
| `AlgeriaPos.WebUI` | Angular PWA, IndexedDB replica, offline outbox, and register UI. |
| `AlgeriaPos.Tests` | Backend tests and dependency-boundary guards. |

## Dependency direction

```text
Domain <- Application <- Infrastructure <- API
WebUI -- HTTP/sync contract --> API
```

The web UI does not depend on server availability to complete a sale. IndexedDB is the
terminal's working store. The API is needed for online authentication, catalog pulls, and
outbox pushes, while a provisioned cashier can unlock and sell offline.

## Synchronization rules

1. Pull catalog changes using a monotonically increasing cursor.
2. Store catalog rows in IndexedDB; the server wins catalog conflicts.
3. Write completed sales to the IndexedDB outbox before trying the network.
4. Push queued sales in bounded batches after the reachability probe succeeds.
5. Treat repeated sale IDs as duplicates, so retries cannot create double sales.
6. Pull after pushing to reconcile server-owned stock and customer balances.

## Branches and stock

A company can have several branches — a main warehouse plus one or more selling locations.
Stock is a `ProductStock` row keyed by (product, branch), not a single company-wide number, so
two branches selling the same SKU keep independent counts. Every till (`Device`) is registered
to exactly one branch; a sale decrements only that branch's stock, resolved server-side from
the device code at push time and never trusted from the client. A terminal's catalog pull only
ever contains its own branch's stock figures — the wire shape (`ProductDto.StockQty`) stays
flat, and the till's own code never needs to know branches exist. Moving stock between branches
(typically warehouse → store) is an explicit admin action (`POST /api/catalog/stock/transfers`),
guarded by a per-branch `AllowNegativeStock` policy; this guard never applies to a sale itself,
which is still never blocked for a shortfall (rule 5's client-wins guarantee extends to stock).

**Why not real-time sync across branches/shops.** Nothing about this system's actual reporting
need (a manager wanting to see a week's or a quarter's numbers per location) requires the
catalog/stock/sales sync engine itself to span multiple independently-deployed shop servers.
Each shop (or, for a single company with several branches served by one API instance, each
`Branch` row) is already fully self-sufficient offline-first on its own. Consolidating several
*separately deployed* shop servers into one head-office view is better served by a periodic
export/pull of each shop's `/api/reports/summary` and `/api/sales`, reusing endpoints that
already exist, than by extending the delta-cursor protocol to reconcile catalogs and stock
across machines that may each be offline for days at a time — that would be a materially larger
and riskier change for a need this system doesn't have evidence of today.
