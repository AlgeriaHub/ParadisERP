import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Router } from '@angular/router';
import { I18nService } from '../../core/i18n.service';

/**
 * What a till address shows in an office session (owner, 2026-10-05: "admin can log in without a
 * till"). The session was opened with no till code, so there is no receipt-number space, branch
 * stock or cash box to sell against; the page says so in words and says how to make this computer
 * a till, instead of opening a register that could not record anything.
 */
@Component({
  selector: 'app-not-a-till',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="not-a-till">
      <section class="panel" role="status">
        <h1>{{ i18n.t('office.notTill.title') }}</h1>
        <p>{{ i18n.t('office.notTill.body') }}</p>
        <p class="muted">{{ i18n.t('office.notTill.register') }}</p>
        <div class="actions">
          <button type="button" class="btn-primary" (click)="go('/backoffice/dashboard')">
            {{ i18n.t('office.notTill.toBackoffice') }}
          </button>
          <button type="button" class="btn-ghost" (click)="go('/backoffice/admin/tills')">
            {{ i18n.t('office.notTill.toTerminals') }}
          </button>
        </div>
      </section>
    </main>
  `,
  styles: [
    `
      .not-a-till {
        min-block-size: 100dvh;
        display: grid;
        place-items: center;
        padding: var(--sp-5);
        background: var(--bg);
      }
      .panel {
        inline-size: 100%;
        max-inline-size: var(--read-max);
        padding: var(--sp-6);
        background: var(--surface);
        border: 1px solid var(--border);
        border-radius: var(--radius);
        text-align: start;
      }
      h1 {
        margin: 0 0 var(--sp-3);
        font-size: var(--fs-page);
      }
      p {
        margin: 0 0 var(--sp-3);
      }
      .actions {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sp-3);
        margin-block-start: var(--sp-5);
      }
      .actions button {
        min-block-size: var(--control-h);
      }
    `,
  ],
})
export class NotATillComponent {
  readonly i18n = inject(I18nService);
  private readonly router = inject(Router);

  go(path: string): void {
    void this.router.navigateByUrl(path);
  }
}
