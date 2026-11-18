# Integrations (operator-owned)

Every integration below is configured **per company by the platform operator** in the portal
(*Integrations* section under the selected company). Company administrators cannot see or change
them, and tills only learn the few facts they need.

## What exists

| Integration | What it does |
|---|---|
| Card terminal | `None`, or `ExternalTerminal` for a standalone bank terminal (CIB / Edahabia through a SATIM-certified bank). With *require approval number*, the cashier must type the number the terminal printed; it is stored on the payment line and printed on the receipt. |
| SMS receipts | Sends the receipt as text through a generic HTTP gateway (URL, method, token header, content type, body template with `{to}` and `{message}`). |
| E-mail receipts | Sends the receipt through SMTP (host, port, TLS, user, sender address). |
| Sales webhook | Posts every accepted sale as JSON to an accounting/e-invoicing endpoint, signed with HMAC-SHA256. |

## Storage and secrets

* Non-secret settings live in the platform database (`CompanyIntegrations.SettingsJson`).
* The SMS token, SMTP password and webhook secret are encrypted with ASP.NET Data Protection
  (purpose `AlgeriaPos.Integrations`) in the same row.
* Secrets are **write-only**. `GET` returns only `hasSmsToken` / `hasSmtpPassword` /
  `hasWebhookSecret`; saving with an empty field keeps the stored value, and a *remove* flag
  clears it. Audit entries record which secret changed, never its value.
* Saves are revision-checked (409 on a competing edit) and require a reason, like subscriptions.
* Persist the Data Protection keys (`DataProtection__KeysPath`, `/data/keys` in the container).
  Losing them means re-entering the secrets.

## Endpoints

| Method | Route | Notes |
|---|---|---|
| `GET` | `/api/platform/companies/{id}/integrations` | Operator only. Settings, revision, which secrets are stored, outbox status. |
| `PUT` | `/api/platform/companies/{id}/integrations` | Operator only. `{ settings, secrets, revision, reason }`. |
| `POST` | `/api/platform/companies/{id}/integrations/test-webhook` | Operator only. Sends a signed `test` event. |
| `POST` | `/api/sales/{id}/send-receipt` | Any signed-in company user. `{ channel: "sms" \| "email", to }`. Online only, max 5 sends per sale. |

Tills receive a public view with login and every pull: `cardMode`, `requireCardReference`,
`smsReceipts`, `emailReceipts`. No URLs and no secrets ever reach a till.

## Sales webhook

* On each accepted sale, an `IntegrationOutbox` row is written **in the same transaction** as
  the sale, so an event exists exactly when its sale does.
* `IntegrationOutboxWorker` (a background service, `IntegrationOutbox:IntervalSeconds`, default
  15) delivers pending events per company and retries with exponential backoff (1, 2, 4 … minutes,
  capped at 6 hours). Events stay queued while the webhook is disabled.
* Request headers: `X-AlgeriaPos-Event` and `X-AlgeriaPos-Signature: sha256=<hex>`, the HMAC of
  the exact request body with the company's signing secret. Verify it before trusting a payload.
* Payload: `{ type: "sale.accepted", companyId, sale: { …, lines: [...], payments: [...] } }`.

## Single-shop installs (no portal)

Configure the `Integrations` and `IntegrationSecrets` sections instead, e.g. in
`appsettings.Local.json` or environment variables:

```json
{
  "Integrations": { "Card": { "Mode": "ExternalTerminal", "RequireCardReference": true } },
  "IntegrationSecrets": { "WebhookSecret": "…" }
}
```

## What is not included

* **No direct SATIM / CIB payment API.** In-person card payments go through the bank's own
  certified terminal; the till only records the approval number. A direct integration needs a
  bank agreement and certification, and would be a separate adapter.
* **No e-invoicing format.** The webhook carries the sale as JSON; mapping it to a national
  e-invoicing schema (for example ZATCA) belongs in the receiving system for now.
* Receipt sending is online-only and is not queued; the cashier retries or prints instead.
* The per-sale send limit is in memory, so it resets when the API restarts.
* Operator-configured URLs are trusted (they are not restricted to public addresses); only
  operators can set them.
