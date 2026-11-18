import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { I18nService } from '../core/i18n.service';

export type TableStateKind = 'loading' | 'empty' | 'noResults' | 'error' | 'forbidden' | 'offline';

/**
 * What a list shows when it has no rows to show (REDESIGN_PLAN.md §6.4). Six different nothings,
 * each saying what it is and what to do next:
 *
 * - `loading` — a word and static placeholder rows (no shimmer: the system has no motion for it);
 * - `empty` — first use: what the list is for; the primary action goes in the content slot;
 * - `noResults` — the filters matched nothing: offers to clear them;
 * - `error` — it could not be loaded: offers to retry;
 * - `forbidden` — this user may not see it;
 * - `offline` — it needs a connection the device does not have.
 *
 * The message defaults to a generic sentence; a page passes its own when it can say more.
 */
@Component({
  selector: 'app-table-state',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[attr.role]': 'kind() === "loading" ? "status" : null', '[class]': '"kind-" + kind()' },
  template: `
    <p class="word">{{ text() }}</p>
    @if (kind() === 'loading') {
      <span class="rows" aria-hidden="true"><span></span><span></span><span></span></span>
    }
    @if (kind() === 'noResults') {
      <button type="button" class="btn-ghost" (click)="clear.emit()">{{ i18n.t('ui.state.clearFilters') }}</button>
    }
    @if (kind() === 'error') {
      <button type="button" class="btn-ghost" (click)="retry.emit()">{{ i18n.t('ui.state.retry') }}</button>
    }
    <ng-content />
  `,
  styles: [
    `
      :host {
        display: grid;
        justify-items: center;
        gap: var(--sp-3);
        padding: var(--sp-7) var(--sp-5);
        text-align: center;
        border: 1px dashed var(--border-strong);
        border-radius: var(--radius-card);
        background: var(--surface);
      }
      .word {
        margin: 0;
        font-size: var(--fs-sm);
        font-weight: 600;
        color: var(--text);
      }
      :host(.kind-error) .word,
      :host(.kind-forbidden) .word {
        color: var(--danger);
      }
      :host(.kind-offline) .word {
        color: var(--warn);
      }
      .rows {
        display: grid;
        gap: var(--sp-2);
        inline-size: min(100%, 28rem);
      }
      .rows span {
        block-size: 0.75rem;
        border-radius: var(--radius-pill);
        background: var(--surface-2);
      }
    `,
  ],
})
export class TableStateComponent {
  readonly i18n = inject(I18nService);
  readonly kind = input.required<TableStateKind>();
  readonly message = input<string | null>(null);
  readonly retry = output<void>();
  readonly clear = output<void>();

  readonly text = computed(() => this.message() || this.i18n.t(`ui.state.${this.kind()}`));
}
