# ParadisERP project overview

ParadisERP is an online point-of-sale web application that remains usable when a shop loses
internet access. The Angular PWA stores the catalog and sales locally in IndexedDB. Sales are
completed against local data, placed in an outbox, and synchronized with the ASP.NET Core API
when connectivity returns.

## Product boundaries

- Catalog changes are made online and replicated from server to terminals.
- Sales are created on terminals, including while offline, and pushed to the server.
- Every sale has a client-generated identifier, making retries idempotent.
- Stock is authoritative on the server and refreshed on terminals during pull synchronization.
- The API can serve the compiled PWA for a single-process shop deployment.
- Algeria is the default market; currency, tax rates, fiscal identifiers, languages and
  payment methods come from a per-company country profile set by the platform operator.

See [01-architecture.md](01-architecture.md) for the solution layout and dependency rules, and
[05-system-design.md](05-system-design.md) for the full system design and roadmap.
