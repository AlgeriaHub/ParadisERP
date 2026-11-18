import { ChangeDetectionStrategy, Component, ElementRef, effect, inject, input, signal, untracked } from '@angular/core';
import { I18nService } from '../core/i18n.service';
import { revealField } from './reveal-field';

interface Found {
  id: string;
  label: string;
  message: string;
}

/**
 * The error summary of a long form whose fields already say what is wrong with them
 * (REDESIGN_PLAN.md §11). After each save attempt (`attempt` goes up) it reads the fields the form
 * itself has marked `.is-invalid` inside `scope` — their label and their own error line — and lists
 * them, taking focus so a save that did nothing near its button is no longer silent.
 *
 * Presentation only: it decides nothing. Which field is invalid is still the form's own rule, so
 * no validation is repeated here and none can drift from it. An empty list renders nothing.
 */
@Component({
  selector: 'app-invalid-summary',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { tabindex: '-1', '[hidden]': '!found().length' },
  template: `
    <p class="title">{{ i18n.t('ui.errors.title') }}</p>
    <ul>
      @for (f of found(); track f.id + $index) {
        <li>
          <button type="button" class="link" (click)="go(f.id)">
            {{ f.label }}@if (f.message) {<span> — {{ f.message }}</span>}
          </button>
        </li>
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
        text-align: start;
        text-decoration: underline;
        cursor: pointer;
      }
    `,
  ],
})
export class InvalidSummaryComponent {
  readonly i18n = inject(I18nService);
  /** The form to read. */
  readonly scope = input.required<HTMLElement>();
  /** Bumped by the form on every save attempt. Zero shows nothing. */
  readonly attempt = input(0);
  readonly found = signal<Found[]>([]);
  private readonly host = inject(ElementRef<HTMLElement>);

  constructor() {
    effect(() => {
      const n = this.attempt();
      const scope = this.scope();
      untracked(() => {
        if (n === 0) return;
        // After the form has rendered what the attempt made it show.
        setTimeout(() => {
          this.found.set(collect(scope));
          // Once the list is shown: a hidden element cannot take focus.
          if (this.found().length) setTimeout(() => this.host.nativeElement.focus());
        });
      });
    });
  }

  go(id: string): void {
    const el = document.getElementById(id);
    revealField(el);
  }
}

/** Each `.is-invalid` field: its control's id, its label without the Required/Optional word, its error line. */
export function collect(scope: ParentNode): Found[] {
  return Array.from(scope.querySelectorAll<HTMLElement>('.is-invalid')).flatMap((field) => {
    const control = field.querySelector<HTMLElement>('input, select, textarea');
    if (!control?.id) return [];
    const labelEl = field.querySelector('label');
    const label = labelEl
      ? Array.from(labelEl.childNodes)
          .filter((n) => !(n instanceof HTMLElement && n.classList.contains('bo-req')))
          .map((n) => n.textContent ?? '')
          .join('')
          .trim()
      : control.getAttribute('aria-label') ?? '';
    const message = (field.querySelector('.bo-error')?.textContent ?? '').trim();
    // A field that names nothing and says nothing gives the reader nothing to act on.
    return label || message ? [{ id: control.id, label, message }] : [];
  });
}
