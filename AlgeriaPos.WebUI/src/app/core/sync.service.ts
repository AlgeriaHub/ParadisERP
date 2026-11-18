import { Injectable, NgZone, effect, signal } from '@angular/core';
import { ApiError, ApiService } from './api.service';
import { ConnectivityService } from './connectivity.service';
import { LanguagePackService } from './language-pack.service';
import { IntegrationsService } from './integrations.service';
import { RegionalService } from './regional.service';
import { db, getMeta, patchMeta, resetCatalog } from './db';
import { PullResponse, Sale } from './models';

const PUSH_BATCH_SIZE = 100;
const AUTO_SYNC_INTERVAL_MS = 60_000;
const MAX_ATTEMPTS_BEFORE_BACKOFF = 5;

export interface SyncStats {
  pending: number;
  rejected: number;
  synced: number;
  products: number;
  customers: number;
  cursor: number;
  lastSyncAtUtc: string | null;
  lastError: string | null;
}

/**
 * Two-way delta sync.
 *
 * Pull is catalog-only and the server always wins, so there is nothing to merge.
 * Push is sales-only, append-only and idempotent on the terminal-generated sale id, so a
 * connection that dies mid-upload can be retried safely: the server answers "duplicate"
 * for anything it already has.
 */
@Injectable({ providedIn: 'root' })
export class SyncService {
  readonly syncing = signal(false);
  /**
   * Bumped whenever a pull brings catalogue rows in, so whoever holds the catalogue in memory
   * — the till's PosService — knows to read the database again. Until this existed the till
   * re-read only after a sale: a product added at the back office reached the terminal's
   * database within the minute and its screen not until the next customer.
   */
  readonly pulled = signal(0);
  /**
   * Bumped whenever a push writes the outbox state of sales back (synced, rejected, or pending again),
   * so a screen holding a sale in memory — the till's finished sale — reads it again. The push
   * changes IndexedDB only; without this the till kept saying "saved offline", and kept the
   * receipt from being sent, long after the server had the sale.
   */
  readonly pushed = signal(0);
  readonly stats = signal<SyncStats>({
    pending: 0,
    rejected: 0,
    synced: 0,
    products: 0,
    customers: 0,
    cursor: 0,
    lastSyncAtUtc: null,
    lastError: null,
  });

  private timer: ReturnType<typeof setInterval> | null = null;
  private wasOnline = false;

  constructor(
    private api: ApiService,
    private connectivity: ConnectivityService,
    private zone: NgZone,
    private regional: RegionalService,
    private languagePacks: LanguagePackService,
    private integrations: IntegrationsService,
  ) {
    // Reconnecting is the moment that matters: drain the outbox as soon as the line is back.
    effect(() => {
      const online = this.connectivity.online();
      if (online && !this.wasOnline) void this.sync('reconnect');
      this.wasOnline = online;
    });
  }

  startAutoSync(): void {
    if (this.timer) return;
    this.zone.runOutsideAngular(() => {
      this.timer = setInterval(() => {
        if (this.connectivity.online()) void this.zone.run(() => this.sync('timer'));
      }, AUTO_SYNC_INTERVAL_MS);
    });
    void this.refreshStats();
  }

  stopAutoSync(): void {
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
  }

  async refreshStats(): Promise<SyncStats> {
    const meta = await getMeta();
    const [pending, rejected, synced, products, customers] = await Promise.all([
      db.sales.where('outboxState').anyOf('pending', 'sending').count(),
      db.sales.where('outboxState').equals('rejected').count(),
      db.sales.where('outboxState').equals('synced').count(),
      db.products.count(),
      db.customers.count(),
    ]);

    const next: SyncStats = {
      pending,
      rejected,
      synced,
      products,
      customers,
      cursor: meta.cursor,
      lastSyncAtUtc: meta.lastSyncAtUtc,
      lastError: meta.lastSyncError,
    };
    this.stats.set(next);
    return next;
  }

  /**
   * Runs a full cycle. Safe to call at any time; it no-ops when offline, when nobody has
   * signed in yet, or when it is already running.
   */
  async sync(reason: string = 'manual'): Promise<{ ok: boolean; error?: string }> {
    if (this.syncing()) return { ok: false, error: 'already running' };

    // Both halves of a cycle are authenticated routes, so a cycle without a token is a 401
    // and nothing else. The reconnect effect below fires on the login page — the line coming
    // back is not someone signing in — and a terminal that has never been paired would send
    // its first GET /api/sync/pull before anyone had typed a password. No request without a
    // token, ever; the login screen runs a cycle itself the moment there is one.
    const session = await getMeta();
    if (!session.token) {
      // Still count what is waiting: a sale made in a PIN session after an Admin's sign-out
      // (which removes the token) is kept in the outbox, and the till must say so in words
      // rather than show nothing pending until someone signs in online.
      await this.refreshStats();
      return { ok: false, error: 'unauthenticated' };
    }

    const reachable = await this.connectivity.probe();
    if (!reachable) {
      await this.refreshStats();
      return { ok: false, error: 'offline' };
    }

    this.syncing.set(true);
    try {
      await this.pushOutbox();
      await this.pullCatalog();
      await patchMeta({ lastSyncAtUtc: new Date().toISOString(), lastSyncError: null });
      await this.refreshStats();
      return { ok: true };
    } catch (error) {
      const message = describe(error);
      await patchMeta({ lastSyncError: `[${reason}] ${message}` });
      await this.refreshStats();
      return { ok: false, error: message };
    } finally {
      this.syncing.set(false);
    }
  }

  /** Throws away the local cursor and re-downloads everything. Unsent sales are untouched. */
  async fullResync(): Promise<{ ok: boolean; error?: string }> {
    await resetCatalog();
    await patchMeta({ cursor: 0 });
    return this.sync('full-resync');
  }

  // ---------- push ----------

  private async pushOutbox(): Promise<void> {
    const meta = await getMeta();
    // An office session (no till) cannot record sales: the server answers `no_terminal`. Sales
    // this browser took earlier as a till stay pending, counted, until a till session sends them,
    // rather than being sent under no terminal and filed as rejected.
    if (!meta.deviceCode) return;

    // Skip sales that have failed many times in a row unless enough time has passed —
    // one poison record must never block the rest of the day's takings.
    const candidates = (
      await db.sales.where('outboxState').anyOf('pending', 'sending').sortBy('localSeq')
    ).filter((sale) => sale.attempts < MAX_ATTEMPTS_BEFORE_BACKOFF || shouldRetry(sale));

    for (let i = 0; i < candidates.length; i += PUSH_BATCH_SIZE) {
      const batch = candidates.slice(i, i + PUSH_BATCH_SIZE);
      const ids = batch.map((s) => s.id);

      await db.sales.where('id').anyOf(ids).modify({ outboxState: 'sending' });

      try {
        const response = await this.api.push(meta.deviceCode, batch);
        const now = new Date().toISOString();

        await db.transaction('rw', db.sales, async () => {
          for (const result of response.results) {
            if (result.status === 'accepted' || result.status === 'duplicate') {
              await db.sales.update(result.id, {
                outboxState: 'synced',
                syncedAtUtc: now,
                lastError: result.message ?? null,
                lastErrorCode: null,
              });
            } else {
              const existing = await db.sales.get(result.id);
              // A concurrent_update is the server saying "try again", so the sale goes back
              // in the queue rather than to the rejected pile; the attempt counter and its
              // backoff keep a genuinely stuck record from looping.
              // A refund whose original sale has not reached the server yet (two tills offline)
              // waits the same way (security review, 2026-10-10).
              const retryable = result.code === 'concurrent_update' || result.code === 'refund_original_pending';
              await db.sales.update(result.id, {
                outboxState: retryable ? 'pending' : 'rejected',
                attempts: (existing?.attempts ?? 0) + 1,
                lastError: result.message ?? 'Rejected by server',
                lastErrorCode: result.code ?? null,
              });
            }
          }
        });
        this.pushed.update((n) => n + 1);
      } catch (error) {
        // Whole batch failed (network/auth). Put it back so the next cycle retries it.
        const message = describe(error);
        // A 429 is the server's request limit (security review, 2026-10-10), not a fault of these
        // sales: they go back untouched, without an attempt counted against them, so a busy hour
        // never pushes a till into its long backoff.
        const limited = error instanceof ApiError && error.status === 429;
        await db.transaction('rw', db.sales, async () => {
          for (const sale of batch) {
            await db.sales.update(sale.id, {
              outboxState: 'pending',
              attempts: limited ? sale.attempts : sale.attempts + 1,
              lastError: message,
            });
          }
        });
        this.pushed.update((n) => n + 1);
        throw error;
      }
    }
  }

  // ---------- pull ----------

  private async pullCatalog(): Promise<void> {
    const meta = await getMeta();
    let cursor = meta.cursor;

    // The server caps each batch, so a terminal that has been off for weeks catches up in
    // several passes instead of one response it may never finish downloading.
    for (let guard = 0; guard < 200; guard++) {
      const page: PullResponse = await this.api.pull(cursor, meta.deviceCode);
      await this.applyPull(page);

      const advanced = page.cursor > cursor;
      cursor = page.cursor;
      await patchMeta({ cursor });

      if (!page.hasMore || !advanced) break;
    }
  }

  private async applyPull(page: PullResponse): Promise<void> {
    if (page.subscription) await patchMeta({ subscription: page.subscription });
    if (page.regional) {
      await patchMeta({ regional: page.regional });
      this.regional.set(page.regional);
    }
    if (page.integrations) {
      await patchMeta({ integrations: page.integrations });
      this.integrations.set(page.integrations);
    }
    await this.languagePacks.sync(page.languagePacks);
    await db.transaction(
      'rw',
      [db.products, db.categories, db.customers, db.users, db.stores, db.branches, db.giftCards, db.paymentMethods],
      async () => {
        await upsert(db.products, page.products);
        await upsert(db.giftCards, page.giftCards ?? []);
        await upsert(db.paymentMethods, page.paymentMethods ?? []);
        await upsert(db.categories, page.categories);
        await upsert(db.customers, page.customers);
        await upsert(db.users, page.users);
        await upsert(db.branches, page.branches ?? []);
        if (page.store) await upsert(db.stores, [page.store]);
      },
    );

    const carried =
      page.products.length +
      page.categories.length +
      page.customers.length +
      (page.giftCards?.length ?? 0) +
      (page.paymentMethods?.length ?? 0) +
      (page.branches?.length ?? 0) +
      (page.store ? 1 : 0);
    if (carried > 0) this.pulled.update((n) => n + 1);
  }
}

/** Applies a delta page: tombstoned rows are removed locally, the rest are written. */
async function upsert<T extends { id: string; isDeleted?: boolean }>(
  table: { bulkPut(rows: T[]): Promise<unknown>; bulkDelete(keys: string[]): Promise<unknown> },
  rows: T[],
): Promise<void> {
  if (!rows.length) return;
  const deleted = rows.filter((r) => r.isDeleted).map((r) => r.id);
  const live = rows.filter((r) => !r.isDeleted);
  if (live.length) await table.bulkPut(live);
  if (deleted.length) await table.bulkDelete(deleted);
}

/** Exponential backoff, capped at 30 minutes. */
function shouldRetry(sale: Sale): boolean {
  const delayMs = Math.min(2 ** (sale.attempts - MAX_ATTEMPTS_BEFORE_BACKOFF) * 60_000, 30 * 60_000);
  const last = sale.syncedAtUtc ? new Date(sale.syncedAtUtc).getTime() : 0;
  return Date.now() - last > delayMs;
}

function describe(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 401) return 'Session expirée — reconnectez-vous en ligne.';
    if (error.status === 0) return 'Serveur injoignable.';
    if (error.status === 429) return 'Serveur occupé — nouvel essai automatique.';
    return `HTTP ${error.status}: ${error.message}`.slice(0, 300);
  }
  return (error as Error)?.message ?? 'Unknown error';
}
