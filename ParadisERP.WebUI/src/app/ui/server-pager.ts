import { DestroyRef, Signal, computed, effect, inject, signal, untracked } from '@angular/core';
import { PageQuery, PageResult } from '../core/paging';
import { Pager, rememberSize, rememberedSize, validPageSize } from './pager';

export type { PageQuery, PageResult } from '../core/paging';
export { pageQueryString } from '../core/paging';

export interface ServerPagerOptions<F> {
  /** Remembers the chosen size for the session under this name. */
  key?: string;
  /** The filters the server applies. A change goes back to page one and asks again. */
  filters: () => F;
  /** Wait this long after a filter changes before asking (typing in a search box). Default 300 ms. */
  debounceMs?: number;
  /** Ask only while this is true (e.g. a required choice is made). Default: always. */
  enabled?: () => boolean;
}

/** A pager whose pages come from the server: every page, size or filter change is a request. */
export interface ServerPager<T, S = unknown> extends Pager<T> {
  readonly loading: Signal<boolean>;
  readonly error: Signal<unknown>;
  /** The server's figures over all the filtered rows; null until the first answer. */
  readonly summary: Signal<S | null>;
  /** True once a first answer arrived (an empty list is then really empty). */
  readonly loaded: Signal<boolean>;
  /** Asks again for the page on screen — after a save or a delete. */
  reload(): Promise<void>;
}

/**
 * Server-side paging (owner, 2026-11-17: "I need to paginate in backend, not frontend"). Create
 * it in a field initializer (it injects and runs an effect):
 *
 * ```ts
 * readonly pager = createServerPager((q) => this.api.customersPage({ ...q.filters, skip: q.skip, take: q.take }), {
 *   key: 'customers', filters: () => ({ q: this.query().trim(), kind: this.kindFilter() }),
 * });
 * ```
 * then `@for (c of pager.rows(); …)` and `<app-list-footer [pager]="pager" [loading]="pager.loading()" />`.
 * Only the last request's answer is kept, so a slow answer to an old filter never overwrites a new one.
 */
export function createServerPager<T, F, S = unknown>(
  load: (query: PageQuery<F>) => Promise<PageResult<T, S>>,
  options: ServerPagerOptions<F>,
): ServerPager<T, S> {
  const destroyRef = inject(DestroyRef);
  const debounceMs = options.debounceMs ?? 300;
  const size = signal(rememberedSize(options.key));
  const index = signal(0);
  const result = signal<PageResult<T, S> | null>(null);
  const loading = signal(false);
  const error = signal<unknown>(null);
  const loaded = signal(false);
  const filters = computed(() => options.filters(), { equal: (a, b) => JSON.stringify(a) === JSON.stringify(b) });

  let seq = 0;
  let timer: ReturnType<typeof setTimeout> | null = null;
  let lastFilters: string | undefined;
  /** The filters, page and size of the page on screen, so the same page is not asked twice. */
  let shown: string | undefined;
  let destroyed = false;
  destroyRef.onDestroy(() => {
    destroyed = true;
    if (timer) clearTimeout(timer);
  });

  async function fetchPage(): Promise<void> {
    const mine = ++seq;
    const take = untracked(size);
    const query: PageQuery<F> = { skip: untracked(index) * take, take, filters: untracked(filters) };
    loading.set(true);
    try {
      const page = await load(query);
      if (mine !== seq || destroyed) return;
      result.set(page);
      error.set(null);
      loaded.set(true);
      // The server answered with another page (the asked one was past the end): show where it is.
      const at = page.take > 0 ? Math.floor(page.skip / page.take) : 0;
      shown = `${JSON.stringify(query.filters)}|${at}|${take}`;
      if (at !== untracked(index)) index.set(at);
    } catch (e) {
      if (mine !== seq || destroyed) return;
      error.set(e);
    } finally {
      if (mine === seq && !destroyed) loading.set(false);
    }
  }

  effect(() => {
    const f = JSON.stringify(filters());
    const i = index();
    const s = size();
    const on = options.enabled ? options.enabled() : true;
    untracked(() => {
      if (!on) return;
      if (timer) clearTimeout(timer);
      const filtersChanged = lastFilters !== undefined && lastFilters !== f;
      lastFilters = f;
      if (filtersChanged && i !== 0) {
        index.set(0); // the effect runs again for page one
        return;
      }
      if (`${f}|${i}|${s}` === shown) return; // already on screen (the server moved us here)
      if (filtersChanged && debounceMs > 0) timer = setTimeout(() => void fetchPage(), debounceMs);
      else void fetchPage();
    });
  });

  const total = computed(() => result()?.total ?? 0);
  const pageCount = computed(() => Math.max(1, Math.ceil(total() / size())));

  return {
    rows: computed(() => result()?.items ?? []),
    total,
    index: computed(() => Math.min(index(), pageCount() - 1)),
    size,
    pageCount,
    loading,
    error,
    loaded,
    summary: computed(() => (result()?.summary ?? null) as S | null),
    go: (next) => index.set(Math.max(0, next)),
    setSize: (next) => {
      const firstRow = untracked(index) * untracked(size);
      const value = validPageSize(next);
      rememberSize(options.key, value);
      size.set(value);
      index.set(Math.floor(firstRow / value));
    },
    reset: () => index.set(0),
    reload: () => fetchPage(),
  };
}
