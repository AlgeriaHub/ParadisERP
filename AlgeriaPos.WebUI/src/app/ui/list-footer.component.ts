import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { I18nService } from '../core/i18n.service';
import { DEFAULT_PAGE_SIZE, PAGE_SIZES, Pager } from './pager';

/**
 * Under a list: which rows are on screen, how many rows a page holds (10, 20, 50 or 100 — ten to
 * begin with, owner 2026-11-17), the pages, and — when the server stopped at its cap — an honest
 * line saying so (REDESIGN_PLAN.md §6.4, §10).
 *
 * Two ways to drive it:
 * - **a list in memory** passes its `createPager()` (`ui/pager.ts`): `[pager]="pager"`; the footer
 *   reads it and turns it;
 * - **a list the server pages** passes `total`, `pageIndex` and `pageSize` and listens to
 *   `pageChange` and `pageSizeChange`, then asks the server again.
 *
 * Hidden while loading, so "0–0 of 0" is never the dominant thing on the screen, and on paper,
 * where the pager prints every row instead (`PrintAllRows`).
 */
@Component({
  selector: 'app-list-footer',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (!loading() && count() > 0) {
      <p class="range">{{ i18n.t('ui.list.range', { from: from(), to: to(), total: count() }) }}</p>
      <div class="controls">
        @if (count() > minSize()) {
          <label class="size">
            <span>{{ i18n.t('ui.list.size') }}</span>
            <select (change)="pickSize($event)">
              @for (s of sizes(); track s) {
                <option [value]="s" [selected]="s === size()">{{ s }}</option>
              }
            </select>
          </label>
        }
        @if (pages() > 1) {
          <nav class="pager" [attr.aria-label]="i18n.t('ui.list.pager')">
            <button type="button" class="btn-ghost pager__step" [disabled]="index() === 0" (click)="go(index() - 1)" [attr.aria-label]="i18n.t('ui.list.prev')">
              <span class="glyph" aria-hidden="true">‹</span><span class="pager__word" aria-hidden="true">{{ i18n.t('ui.list.prev') }}</span>
            </button>
            @for (item of items(); track $index) {
              @if (item === null) {
                <span class="pager__gap" aria-hidden="true">…</span>
              } @else {
                <button
                  type="button"
                  class="pager__num"
                  [class.is-current]="item === index()"
                  [attr.aria-current]="item === index() ? 'page' : null"
                  [attr.aria-label]="i18n.t('ui.list.page', { n: item + 1 })"
                  (click)="go(item)"
                >{{ item + 1 }}</button>
              }
            }
            <span class="pager__of">{{ i18n.t('ui.list.pageOf', { page: index() + 1, pages: pages() }) }}</span>
            <button type="button" class="btn-ghost pager__step" [disabled]="index() >= pages() - 1" (click)="go(index() + 1)" [attr.aria-label]="i18n.t('ui.list.next')">
              <span class="pager__word" aria-hidden="true">{{ i18n.t('ui.list.next') }}</span><span class="glyph" aria-hidden="true">›</span>
            </button>
          </nav>
        }
      </div>
      @if (capReached()) {
        <p class="cap" role="note">{{ i18n.t('ui.list.cap', { n: count() }) }}</p>
      }
    }
  `,
  styles: [
    `
      :host {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        justify-content: space-between;
        gap: var(--sp-3);
        font-size: 13px;
        color: var(--muted);
      }
      :host:empty {
        display: none;
      }
      .range {
        margin: 0;
        font-size: 13px;
        color: var(--muted);
      }
      .controls {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-3);
        margin-inline-start: auto;
      }
      .size {
        display: inline-flex;
        align-items: center;
        gap: var(--sp-2);
        margin: 0;
        font-size: 13px;
        font-weight: 600;
        color: var(--text-2);
        white-space: nowrap;
      }
      .size select {
        inline-size: auto;
        min-inline-size: 4.75rem;
        min-block-size: var(--control-h-sm);
        padding-block: 0;
        font-variant-numeric: tabular-nums;
      }
      .cap {
        margin: 0;
        flex-basis: 100%;
        display: inline-flex;
        align-items: center;
        gap: 0.375rem;
        inline-size: fit-content;
        max-inline-size: 100%;
        min-block-size: 1.5rem;
        padding: 0.125rem 0.625rem;
        border-radius: var(--radius-pill);
        border: var(--line) solid color-mix(in srgb, currentColor 22%, transparent);
        background: var(--warn-soft);
        color: var(--warn);
        font-size: 12px;
        font-weight: 600;
        white-space: normal;
      }
      .cap::before {
        content: '';
        inline-size: 6px;
        block-size: 6px;
        border-radius: 50%;
        background: currentColor;
        flex: none;
      }
      .pager {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-1);
      }
      .pager__step {
        display: inline-flex;
        align-items: center;
        gap: 0.375rem;
        min-block-size: var(--control-h-sm);
        padding-block: 0;
      }
      .pager__num {
        min-inline-size: var(--control-h-sm);
        min-block-size: var(--control-h-sm);
        padding: 0 0.625rem;
        border: var(--line) solid var(--border);
        border-radius: var(--radius-pill);
        background: var(--surface);
        color: var(--text-2);
        font: inherit;
        font-size: 13px;
        font-weight: 600;
        font-variant-numeric: tabular-nums;
        cursor: pointer;
        transition: background var(--t-fast) ease, border-color var(--t-fast) ease, color var(--t-fast) ease;
      }
      .pager__num:hover:not(.is-current) {
        background: var(--primary-soft);
        border-color: var(--primary-300);
        color: var(--primary-dark);
      }
      /* The page on screen is filled, not only outlined: a selected item differs by fill. */
      .pager__num.is-current {
        background: var(--primary);
        border-color: var(--primary);
        color: var(--on-primary);
        font-weight: 700;
        cursor: default;
      }
      .pager__gap {
        min-inline-size: 1.25rem;
        text-align: center;
        color: var(--muted);
      }
      .pager__of {
        display: none;
        font-weight: 600;
        color: var(--text-2);
        font-variant-numeric: tabular-nums;
        white-space: nowrap;
      }
      /* A phone has no room for the numbers: "Page 3 of 12" between the two arrows instead. */
      @media (max-width: 639.98px) {
        .controls {
          inline-size: 100%;
          justify-content: space-between;
        }
        .pager__num,
        .pager__gap,
        .pager__word {
          display: none;
        }
        .pager__of {
          display: inline;
          padding-inline: var(--sp-2);
        }
      }
      @media print {
        :host {
          display: none !important;
        }
      }
      @media (prefers-reduced-motion: reduce) {
        .pager__num {
          transition: none;
        }
      }
    `,
  ],
})
export class ListFooterComponent {
  readonly i18n = inject(I18nService);
  /** A list in memory: its pager supplies the figures below and is turned here. */
  readonly pager = input<Pager<unknown> | null>(null);
  readonly total = input(0);
  readonly pageIndex = input(0);
  readonly pageSize = input(DEFAULT_PAGE_SIZE);
  readonly sizes = input<readonly number[]>(PAGE_SIZES);
  readonly loading = input(false);
  /** True when the server returned as many rows as it ever returns: there may be more. */
  readonly capReached = input(false);
  readonly pageChange = output<number>();
  readonly pageSizeChange = output<number>();

  readonly count = computed(() => this.pager()?.total() ?? this.total());
  readonly index = computed(() => this.pager()?.index() ?? this.pageIndex());
  readonly size = computed(() => this.pager()?.size() ?? this.pageSize());
  readonly minSize = computed(() => Math.min(...this.sizes()));
  readonly pages = computed(() => Math.max(1, Math.ceil(this.count() / this.size())));
  readonly from = computed(() => (this.count() === 0 ? 0 : this.index() * this.size() + 1));
  readonly to = computed(() => Math.min(this.count(), (this.index() + 1) * this.size()));

  /** First, last, and the page on screen with its neighbours; null is a gap ("…"). */
  readonly items = computed<(number | null)[]>(() => pageItems(this.index(), this.pages()));

  go(index: number): void {
    const next = Math.min(Math.max(0, index), this.pages() - 1);
    const pager = this.pager();
    if (pager) pager.go(next);
    this.pageChange.emit(next);
  }

  pickSize(event: Event): void {
    const value = Number((event.target as HTMLSelectElement).value);
    if (!Number.isFinite(value) || value <= 0) return;
    const pager = this.pager();
    if (pager) pager.setSize(value);
    this.pageSizeChange.emit(value);
  }
}

/** Page tokens for `pages` pages around `index` (both from 0): at most seven, gaps as null. */
export function pageItems(index: number, pages: number): (number | null)[] {
  if (pages <= 7) return Array.from({ length: pages }, (_, i) => i);
  const start = Math.max(1, Math.min(index - 1, pages - 4));
  const end = Math.min(pages - 2, Math.max(index + 1, 3));
  const items: (number | null)[] = [0];
  // A gap that would hide a single page shows that page instead.
  if (start > 1) items.push(start === 2 ? 1 : null);
  for (let i = start; i <= end; i++) items.push(i);
  if (end < pages - 2) items.push(end === pages - 3 ? pages - 2 : null);
  items.push(pages - 1);
  return items;
}
