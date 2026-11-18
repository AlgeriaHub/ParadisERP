import { ChangeDetectionStrategy, Component, inject, input, output } from '@angular/core';
import { I18nService } from '../core/i18n.service';
import { MODES, THEMES, THEME_SWATCHES, ThemeService } from '../core/theme.service';
import { DialogComponent } from '../ui/dialog.component';

/**
 * Atelier appearance dialog:
 * Displays the theme choices (tiles) and mode choices (light, dark, system).
 */
@Component({
  selector: 'app-appearance-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DialogComponent],
  template: `
    <app-dialog [open]="open()" [heading]="i18n.t('appearance.title')" (dismiss)="closed.emit()">
      <div class="look">
        <fieldset>
          <legend>{{ i18n.t('appearance.palette') }}</legend>
          <div class="themes" role="radiogroup" [attr.aria-label]="i18n.t('appearance.palette')">
            @for (t of themes; track t) {
              <label class="theme-tile" [class.is-on]="theme.theme() === t">
                <span class="swatches" aria-hidden="true">
                  <span class="swatch" [style.background-color]="swatches[t].accent"></span>
                  <span class="swatch" [style.background-color]="swatches[t].ink"></span>
                </span>
                <span class="theme-name">{{ i18n.t('appearance.palette.' + t) }}</span>
                <input
                  type="radio"
                  name="appearance-theme"
                  [value]="t"
                  [checked]="theme.theme() === t"
                  (change)="theme.setTheme(t)"
                />
              </label>
            }
          </div>
        </fieldset>

        <fieldset>
          <legend>{{ i18n.t('appearance.mode') }}</legend>
          <div class="modes" role="radiogroup" [attr.aria-label]="i18n.t('appearance.mode')">
            @for (m of modes; track m) {
              <label class="mode" [class.is-on]="theme.mode() === m">
                <input
                  type="radio"
                  name="appearance-mode"
                  [value]="m"
                  [checked]="theme.mode() === m"
                  (change)="theme.setMode(m)"
                />
                {{ i18n.t('appearance.mode.' + m) }}
              </label>
            }
          </div>
        </fieldset>

        <p class="note">{{ i18n.t('appearance.deviceNote') }}</p>
      </div>
      <button dialogFoot type="button" class="btn-primary" (click)="closed.emit()">{{ i18n.t('common.close') }}</button>
    </app-dialog>
  `,
  styles: [
    `
      .look {
        display: grid;
        gap: var(--sp-5);
      }
      fieldset {
        margin: 0;
        padding: 0;
        border: 0;
        display: grid;
        gap: var(--sp-3);
      }
      legend {
        padding: 0;
        margin-block-end: var(--sp-3);
        font-size: var(--fs-meta);
        font-weight: 600;
        color: var(--muted);
      }
      .themes {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(10rem, 1fr));
        gap: var(--sp-3);
      }
      .theme-tile {
        display: flex;
        align-items: center;
        gap: var(--sp-3);
        min-block-size: var(--control-h);
        padding: var(--sp-3) var(--sp-4);
        border: var(--line, 1.5px) solid var(--border-strong);
        border-radius: var(--radius);
        background: var(--surface);
        cursor: pointer;
        transition:
          background var(--t-fast) ease,
          border-color var(--t-fast) ease;
      }
      .theme-tile:hover {
        background: var(--surface-2);
      }
      .theme-tile.is-on {
        background: var(--primary-soft);
        border-color: var(--primary);
        box-shadow: inset 0 0 0 1px var(--primary);
        color: var(--primary-dark);
        font-weight: 600;
      }
      .swatches {
        display: inline-flex;
        align-items: center;
        gap: var(--sp-1);
        flex: none;
      }
      .swatch {
        inline-size: 18px;
        block-size: 18px;
        border-radius: 50%;
        box-shadow: 0 0 0 1px var(--border-strong);
        flex: none;
      }
      .theme-name {
        flex: 1;
        min-inline-size: 0;
      }
      .theme-tile input {
        flex: none;
        inline-size: auto;
        min-block-size: 0;
        accent-color: var(--accent);
        margin: 0;
      }
      .modes {
        display: grid;
        grid-template-columns: repeat(3, minmax(0, 1fr));
        gap: var(--sp-3);
      }
      .mode {
        display: flex;
        align-items: center;
        gap: var(--sp-3);
        min-block-size: var(--control-h);
        padding: var(--sp-3) var(--sp-4);
        border: var(--line, 1.5px) solid var(--border-strong);
        border-radius: var(--radius);
        background: var(--surface);
        cursor: pointer;
        transition:
          background var(--t-fast) ease,
          border-color var(--t-fast) ease;
      }
      .mode:hover {
        background: var(--surface-2);
      }
      /* Fill, weight and ring together. */
      .mode.is-on {
        background: var(--primary-soft);
        border-color: var(--primary);
        box-shadow: inset 0 0 0 1px var(--primary);
        color: var(--primary-dark);
        font-weight: 600;
      }
      .mode input {
        flex: none;
        inline-size: auto;
        min-block-size: 0;
        accent-color: var(--accent);
        margin: 0;
      }
      .note {
        margin: 0;
        color: var(--muted);
        font-size: var(--fs-meta);
      }
      @media (max-width: 30rem) {
        .modes {
          grid-template-columns: 1fr;
        }
      }
    `,
  ],
})
export class AppearanceDialogComponent {
  readonly i18n = inject(I18nService);
  readonly theme = inject(ThemeService);

  readonly open = input(false);
  readonly closed = output<void>();

  readonly themes = THEMES;
  readonly swatches = THEME_SWATCHES;
  readonly modes = MODES;
}
