import { Injectable, computed, signal } from '@angular/core';
import { AdminProduct, AdminService, UpsertProductRequest } from './admin.service';

/**
 * The back office's one copy of the catalogue.
 *
 * <p>Each screen used to fetch the product list for itself when it opened, so a session
 * that raised a contract, checked stock and edited a price fetched the same rows three
 * times — and a picker on one screen could not know what another had just added. This
 * holds the list once per session and hands it to every screen. A search or a scan runs
 * against memory and never reaches the server: a contract with ten articles is ten lookups
 * here and no requests at all.</p>
 *
 * <p>What makes a copy safe to keep is knowing when to throw it away. Three things do:</p>
 * <ul>
 *   <li>a product created, changed or deleted <em>through this store</em> — the list is
 *   refetched before the caller hears back, so the screen that saved sees its own change,
 *   and so does every other screen open in the session;</li>
 *   <li>a scan that matches nothing — the article may have been added on another machine a
 *   minute ago, so the picker asks for a refresh and looks again before saying "unknown";</li>
 *   <li>the screen that edits the catalogue opening — it reads fresh, because a stale row on
 *   the editing screen is a stale row that gets saved back.</li>
 * </ul>
 *
 * <p>There is deliberately no expiry timer. A list that goes stale "sometimes" cannot be
 * reasoned about at a counter, and the three triggers above cover every case anyone has
 * named. If a fourth turns up, it belongs here as a fourth trigger, not as a timeout.</p>
 *
 * <p>The class is split in two so the rules can be tested by handing it a fake server: the
 * pure part takes a fetch function; the injectable below binds it to the real one.</p>
 */
export class ProductCatalog {
  readonly products = signal<AdminProduct[]>([]);
  readonly loaded = signal(false);
  /** What a document may name. The editing screen shows the rest; nothing else does. */
  readonly active = computed(() => this.products().filter((p) => p.isActive));

  private inFlight: Promise<AdminProduct[]> | null = null;

  constructor(private readonly fetchAll: () => Promise<AdminProduct[]>) {}

  /** The list — fetched on the first call of a session, served from memory after that. */
  ensure(): Promise<AdminProduct[]> {
    if (this.loaded()) return Promise.resolve(this.products());
    return this.refresh();
  }

  /**
   * Fetch again, now.
   *
   * <p>Callers arriving while a fetch is in the air share it rather than starting another:
   * two pickers missing the same scan at the same moment is one request, not two. A fetch
   * that fails leaves the previous list where it was — a screen with yesterday's list still
   * works; a screen with no list does not — and the error goes to the caller to show.</p>
   */
  refresh(): Promise<AdminProduct[]> {
    if (this.inFlight) return this.inFlight;
    this.inFlight = this.fetchAll()
      .then((rows) => {
        this.products.set(rows);
        this.loaded.set(true);
        return rows;
      })
      .finally(() => {
        this.inFlight = null;
      });
    return this.inFlight;
  }

  /** Forget the list. The next `ensure` fetches; nothing is fetched until then. */
  invalidate(): void {
    this.loaded.set(false);
  }
}

/** How much one request carries. The server clamps to this; asking for more gets this. */
const PAGE = 1000;

/**
 * The whole catalogue, a page at a time.
 *
 * <p>The server pages at a thousand rows so that no single response is the one a slow line
 * never finishes; walking the pages here is what lets the copy be complete without any cap
 * on the catalogue. The walk stops at a short page. The id set is a guard against a server
 * that ignored the paging and answered the same rows again — without it that walk would
 * never end.</p>
 */
async function fetchEveryPage(admin: AdminService): Promise<AdminProduct[]> {
  const seen = new Set<string>();
  const all: AdminProduct[] = [];
  for (let skip = 0, pages = 0; pages < 100; skip += PAGE, pages++) {
    const rows = await admin.listProducts(undefined, { skip, take: PAGE });
    let fresh = 0;
    for (const row of rows) {
      if (seen.has(row.id)) continue;
      seen.add(row.id);
      all.push(row);
      fresh++;
    }
    if (rows.length < PAGE || fresh === 0) break;
  }
  return all;
}

@Injectable({ providedIn: 'root' })
export class CatalogStore extends ProductCatalog {
  constructor(private readonly admin: AdminService) {
    super(() => fetchEveryPage(admin));
  }

  /**
   * Changes to the catalogue go through here, so the copy is never a step behind them. The
   * list is refetched rather than patched: the server owns ordering, soft deletion and the
   * version stamp, and a locally patched row would disagree with it on all three.
   */
  async create(request: UpsertProductRequest): Promise<AdminProduct> {
    const created = await this.admin.createProduct(request);
    await this.refresh();
    return created;
  }

  async update(id: string, request: UpsertProductRequest): Promise<AdminProduct> {
    const updated = await this.admin.updateProduct(id, request);
    await this.refresh();
    return updated;
  }

  async remove(id: string): Promise<void> {
    await this.admin.deleteProduct(id);
    await this.refresh();
  }
}
