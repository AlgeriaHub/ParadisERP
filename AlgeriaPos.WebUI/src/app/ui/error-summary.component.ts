import { ChangeDetectionStrategy, Component, ElementRef, effect, inject, input } from '@angular/core';
import { I18nService } from '../core/i18n.service';
import { revealField } from './reveal-field';

export interface FieldError {
  /** The `id` of the invalid control. */
  fieldId: string;
  /** Already translated: the field's label and what is wrong with it. */
  message: string;
}

/**
 * Shown after a save that failed validation (REDESIGN_PLAN.md §11): the list of what to fix,
 * each entry moving focus to its field. It takes focus itself when it appears, so a save that
 * does nothing visible near the button is no longer silent.
 *
 * Buttons rather than `#id` links: the app uses hash routing, and an in-page anchor would be read
 * as a route.
 */
@Component({
  selector: 'app-error-summary',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { tabindex: '-1', '[hidden]': '!errors().length' },
  template: `
    <p class="title">{{ i18n.t('ui.errors.title') }}</p>
    <ul>
      @for (e of errors(); track e.fieldId) {
        <li><button type="button" class="link" (click)="focus(e.fieldId)">{{ e.message }}</button></li>
      }
    </ul>
  `,
  styles: [
    `
      :host {
        display: block;
        padding: var(--sp-4) var(--sp-5);
        border: var(--line) solid var(--danger);
        border-radius: var(--radius);
        background: var(--danger-soft);
        color: var(--danger);
      }
      :host([hidden]) {
        display: none;
      }
      .title {
        margin: 0 0 var(--sp-2);
        font-weight: 700;
        color: var(--danger);
      }
      ul {
        margin: 0;
        padding-inline-start: var(--sp-5);
      }
      .link {
        padding: 0;
        border: 0;
        background: none;
        color: var(--text);
        text-decoration: underline;
        text-align: start;
        cursor: pointer;
      }
    `,
  ],
})
export class ErrorSummaryComponent {
  readonly i18n = inject(I18nService);
  readonly errors = input<FieldError[]>([]);
  private readonly host = inject(ElementRef<HTMLElement>);

  constructor() {
    effect(() => {
      if (this.errors().length) queueMicrotask(() => this.host.nativeElement.focus());
    });
  }

  focus(id: string): void {
    const el = document.getElementById(id);
    revealField(el);
  }
}
