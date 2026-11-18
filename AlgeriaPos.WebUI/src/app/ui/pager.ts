import { ApplicationRef, Injectable, Signal, computed, inject, signal } from '@angular/core';

/** What a list offers in its footer (owner, 2026-11-17): ten rows to begin with, the user picks more. */
export const PAGE_SIZES: readonly number[] = [10, 20, 50, 100];
export const DEFAULT_PAGE_SIZE = 10;

/**
 * The size picked on each list, for the session: opening a customer and coming back keeps the
 * fifty rows that were asked for. Reloading the page returns to ten, as a first visit does.
 */
const chosenSizes = new Map<string, number>();

/** The size remembered for a list this session, or the default. Shared with `createServerPager`. */
export function rememberedSize(key: string | undefined): number {
  return validSize(key ? chosenSizes.get(key) : undefined);
}

/** Remembers the size picked on a list for the session. */
export function rememberSize(key: string | undefined, size: number): void {
  if (key) chosenSizes.set(key, validSize(size));
}

/** One of PAGE_SIZES, or the default. */
export function validPageSize(size: number | undefined): number {
  return validSize(size);
}

/**
 * True while the browser prints. A back-office screen prints with Ctrl+P (its `@media print`
 * rules), and a list cut to ten rows would print ten: while this is on, every pager shows all its
 * rows, and the footer is hidden by its own print rule.
 *
 * <p>The tick is synchronous because `beforeprint` is the last moment before the browser lays the
 * page out for paper; a change detected on the next turn would come after the print.</p>
 */
@Injectable({ providedIn: 'root' })
export class PrintAllRows {
  readonly active = signal(false);

  constructor() {
    const app = inject(ApplicationRef);
    const set = (value: boolean) => {
      this.active.set(value);
      try {
        app.tick();
      } catch {
        // print() called from inside a change detection pass: that pass renders the rows itself.
      }
    };
    globalThis.addEventListener?.('beforeprint', () => set(true));
    globalThis.addEventListener?.('afterprint', () => set(false));
  }
}

export interface PagerOptions {
  /** Remembers the chosen size for the session under this name (e.g. `'customers'`). */
  key?: string;
  /**
   * What the filters are now. When it changes the list starts again at its first page: page
   * three of the old list means nothing in the new one. Compared by value (JSON), so return the
   * filter values themselves, e.g. `() => [this.query(), this.kind()]`.
   */
  resetOn?: () => unknown;
}

/**
 * A page of a list already in memory: turning a page is a slice, never a request.
 *
 * Create it in a field initializer (it injects), then bind the footer:
 * `readonly pager = createPager(() => this.shown(), { key: 'customers', resetOn: () => [this.query()] });`
 * `@for (row of pager.rows(); track row.id)` and `<app-list-footer [pager]="pager" [loading]="busy()" />`.
 */
export interface Pager<T> {
  /** The rows of the page on screen (every row while printing). */
  readonly rows: Signal<readonly T[]>;
  /** All the rows the list holds after its filters. */
  readonly total: Signal<number>;
  /** The page on screen, from 0; never past the last page, even after a filter shrank the list. */
  readonly index: Signal<number>;
  readonly size: Signal<number>;
  readonly pageCount: Signal<number>;
  go(index: number): void;
  /** Keeps the first row on screen in view at the new size. */
  setSize(size: number): void;
  /** Back to the first page. */
  reset(): void;
}

export function createPager<T>(source: () => readonly T[], options: PagerOptions = {}): Pager<T> {
  const printing = inject(PrintAllRows).active;
  const key = options.key;
  const size = signal(validSize(key ? chosenSizes.get(key) : undefined));
  const signature = options.resetOn ? computed(() => JSON.stringify(options.resetOn!() ?? null)) : () => '';
  // The page is stored with the filters it was turned under; other filters mean page one. Nothing
  // is read here: a field initializer may run before the filter fields it names exist.
  const at = signal<{ filters: string | undefined; index: number }>({ filters: undefined, index: 0 });

  const total = computed(() => source().length);
  const pageCount = computed(() => Math.max(1, Math.ceil(total() / size())));
  const index = computed(() => {
    const page = at();
    const same = page.filters === undefined || page.filters === signature();
    return same ? Math.min(Math.max(0, page.index), pageCount() - 1) : 0;
  });
  const rows = computed(() => {
    const all = source();
    if (printing()) return all;
    const start = index() * size();
    return all.slice(start, start + size());
  });

  return {
    rows,
    total,
    index,
    size,
    pageCount,
    go: (next) => at.set({ filters: signature(), index: next }),
    setSize: (next) => {
      const value = validSize(next);
      const firstRow = index() * size();
      size.set(value);
      if (key) chosenSizes.set(key, value);
      at.set({ filters: signature(), index: Math.floor(firstRow / value) });
    },
    reset: () => at.set({ filters: signature(), index: 0 }),
  };
}

function validSize(value: number | undefined): number {
  return value !== undefined && PAGE_SIZES.includes(value) ? value : DEFAULT_PAGE_SIZE;
}
