import { isDevMode } from '@angular/core';
import * as Sentry from '@sentry/angular';

/**
 * The web app's Sentry project. A browser DSN is public by design — it only lets a page send
 * events to that project — and it is in the code rather than fetched from the API because a
 * till must be able to report an error before it has ever reached the server, or while it
 * cannot. The API's own project is configured on the server (`Sentry:Dsn`).
 */
const DSN = 'https://83a980353d74c542d79ce6e798718fd7@o4511485603217408.ingest.de.sentry.io/4512199158530128';

/** Hosts that never report: a developer's machine, and the tests that drive it. */
const LOCAL_HOSTS = ['localhost', '127.0.0.1', '[::1]'];

/** Started before Angular, so an error in the bootstrap itself is reported too. */
export function initMonitoring(): void {
  Sentry.init({
    dsn: DSN,
    enabled: !isDevMode() && !LOCAL_HOSTS.includes(location.hostname),
    // One project, several shops someday: the host says which copy an error came from.
    environment: location.hostname,
    integrations: [Sentry.browserTracingIntegration()],
    // A tenth of page loads and navigations: enough to see what is slow, well inside the
    // quota. Same-origin calls to /api carry the trace on, so the API's span joins the page's.
    tracesSampleRate: 0.1,
    // A till that is offline keeps its reports in IndexedDB and sends them when the line
    // comes back, instead of losing exactly the errors that happen in the shop.
    transport: Sentry.makeBrowserOfflineTransport(Sentry.makeFetchTransport),
    // Nothing about people or what they typed: no user, cookies, headers (the bearer token among
    // them), request or response bodies (sales, customers, ID scans), or query strings. An error
    // arrives with its stack, the screen it happened on and the API path it called — no more.
    dataCollection: {
      userInfo: false,
      cookies: false,
      httpHeaders: false,
      httpBodies: [],
      urlQueryParams: false,
    },
  });
}
