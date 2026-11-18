import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { I18nService, LANGS } from '../core/i18n.service';
import { LanguageSwitchService } from '../core/language-switch.service';

@Component({
  selector: 'app-language-switch-overlay',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (switcher.pending(); as lang) {
      <div class="overlay">
        <div class="card">
          <div class="ring-wrap">
            <div class="spinner" aria-hidden="true"></div>
            <span class="countdown" aria-hidden="true">{{ switcher.secondsLeft() }}</span>
          </div>
          <p class="status-text" role="status" aria-live="polite">{{ message() }}</p>
        </div>
      </div>
    }
  `,
  styles: [
    `
      .overlay {
        position: fixed;
        inset: 0;
        z-index: calc(var(--z-toast) + 1);
        background-color: rgba(27, 26, 23, 0.55);
        display: grid;
        place-items: center;
        padding: var(--sp-4);
      }

      .card {
        background: var(--surface);
        border: 2px solid var(--border-strong);
        border-radius: var(--radius-lg);
        box-shadow: var(--shadow-pop);
        padding: var(--sp-6);
        min-inline-size: min(22rem, 90vw);
        display: flex;
        flex-direction: column;
        align-items: center;
        text-align: center;
        gap: var(--sp-4);
      }

      .ring-wrap {
        position: relative;
        inline-size: 56px;
        block-size: 56px;
        display: grid;
        place-items: center;
      }

      .spinner {
        position: absolute;
        inset: 0;
        inline-size: 100%;
        block-size: 100%;
        box-sizing: border-box;
        border: 5px solid var(--surface-2);
        border-block-start-color: var(--accent);
        border-radius: 50%;
        animation: spin 0.9s linear infinite;
      }

      @media (prefers-reduced-motion: reduce) {
        .spinner {
          animation: none;
        }
      }

      @keyframes spin {
        to {
          transform: rotate(360deg);
        }
      }

      .countdown {
        position: relative;
        font-family: var(--font-display);
        font-size: 40px;
        font-weight: 700;
        font-variant-numeric: tabular-nums;
        line-height: 1;
        color: var(--text);
        margin: 0;
      }

      .status-text {
        margin: 0;
        font-size: var(--fs-base);
        line-height: 1.4;
        color: var(--text);
      }
    `,
  ],
})
export class LanguageSwitchOverlayComponent {
  readonly switcher = inject(LanguageSwitchService);
  private readonly i18n = inject(I18nService);

  readonly message = computed(() => {
    const lang = this.switcher.pending();
    if (!lang) return '';
    const label =
      this.i18n.langs().find((l) => l.code === lang)?.label ??
      LANGS.find((l) => l.code === lang)?.label ??
      lang;
    return this.i18n.t('lang.switching', { language: label, n: this.switcher.secondsLeft() });
  });
}
