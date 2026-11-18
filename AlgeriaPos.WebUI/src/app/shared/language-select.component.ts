import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { I18nService } from '../core/i18n.service';
import { LanguageSwitchService } from '../core/language-switch.service';
import { Lang } from '../core/models';

/** The interface language, as the top bar always offered it, for either header's ground. */
@Component({
  selector: 'app-language-select',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <select class="lang" [class.on-ink]="tone() === 'ink'" [value]="i18n.lang()" (change)="onChange($event)" [attr.aria-label]="i18n.t('nav.language')">
      @for (l of i18n.langs(); track l.code) {
        <option [value]="l.code" [selected]="l.code === i18n.lang()">{{ l.label }}</option>
      }
    </select>
  `,
  styles: [
    `
      .lang {
        width: auto;
        min-height: var(--control-h-sm);
        padding: 0 var(--sp-4);
        border: 1px solid var(--border);
        border-radius: var(--radius-btn);
        background-color: var(--bg);
        box-shadow: none;
        font-size: var(--fs-sm);
        font-weight: 600;
      }
      /* On ink (the till's header) the select drops its box and takes a ground only under the
         pointer, so it reads as a quiet utility beside the till's pages. */
      .lang.on-ink {
        min-height: var(--control-h-sm);
        border-radius: var(--radius);
        background-color: transparent;
        color: var(--on-ink);
        border-color: transparent;
        box-shadow: none;
      }
      .lang.on-ink:hover {
        background-color: var(--on-ink-line);
      }
      /* The open list is the browser's: its options get the page's own colours, which every
         engine honours, instead of inheriting white from the closed control on ink. */
      .lang option {
        color: var(--text);
        background: var(--surface);
      }
      .lang.on-ink:focus-visible {
        outline-color: var(--accent);
        outline-offset: 2px;
      }
    `,
  ],
})
export class LanguageSelectComponent {
  readonly i18n = inject(I18nService);
  private readonly switcher = inject(LanguageSwitchService);
  readonly tone = input<'ink' | 'light'>('light');

  onChange(event: Event): void {
    const target = event.target as HTMLSelectElement;
    const value = target.value as Lang;
    target.value = this.i18n.lang();
    void this.switcher.request(value);
  }
}
