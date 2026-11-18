import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { StatusTone } from './status-tones';

/**
 * A state as a word with its colour beside it (CLAUDE.md rule 2). The word is the projected
 * content and carries the meaning; the dot and the tint only make it findable. Use `toneOf()`
 * from `status-tones.ts` for the tone, never a literal picked on the page.
 */
@Component({
  selector: 'app-status',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class]': '"tone-" + tone()' },
  template: `<span class="dot" aria-hidden="true"></span><ng-content />`,
  styles: [
    `
      :host {
        display: inline-flex;
        align-items: center;
        gap: 0.375rem;
        min-block-size: 1.5rem;
        padding: 0.125rem 0.625rem;
        border-radius: var(--radius-pill);
        border: var(--line) solid color-mix(in srgb, currentColor 22%, transparent);
        font-size: 12px;
        font-weight: 600;
        white-space: nowrap;
      }
      .dot {
        inline-size: 6px;
        block-size: 6px;
        border-radius: 50%;
        background: currentColor;
        flex: none;
      }
      :host(.tone-neutral) { background: var(--neutral-soft); color: var(--neutral); border-color: var(--border); }
      :host(.tone-info) { background: var(--info-soft); color: var(--info); }
      :host(.tone-success) { background: var(--ok-soft); color: var(--ok); }
      :host(.tone-warning) { background: var(--warn-soft); color: var(--warn); }
      :host(.tone-critical) { background: var(--danger-soft); color: var(--danger); }
    `,
  ],
})
export class StatusComponent {
  readonly tone = input<StatusTone>('neutral');
}
