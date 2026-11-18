import { Component, OnInit, inject, signal } from '@angular/core';
import { ApiService } from '../../core/api.service';
import { adminErrorMessage } from '../../core/admin.service';
import { I18nService } from '../../core/i18n.service';
import { SubscriptionAccess } from '../../core/models';
import { patchMeta } from '../../core/db';

@Component({
  selector: 'app-backoffice-subscription',
  standalone: true,
  template: `
    <div class="bo-stack">
      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }

      @if (subscription(); as s) {
        <!-- Whether the company may operate is the whole point of the screen, so it is
             stated in words before any limit is listed. -->
        <p class="notice" [class.danger]="!s.canOperate" [class.ok]="s.canOperate">
          {{ i18n.t(s.canOperate ? 'subscription.activeHint' : 'subscription.inactive') }}
        </p>

        <div class="bo-metrics">
          <div class="bo-metric bo-metric--lead">
            <span class="bo-metric__label">{{ i18n.t('subscription.title') }}</span>
            <strong class="bo-metric__value">{{ s.plan }}</strong>
            <span class="bo-metric__note">{{ i18n.t('subscription.status.' + s.status) }}</span>
          </div>
          @if (s.accessUntilUtc) {
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('subscription.until') }}</span>
              <strong class="bo-metric__value date">{{ formatDate(s.accessUntilUtc) }}</strong>
            </div>
          }
        </div>

        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('subscription.title') }}</h2>
            <p class="bo-panel__note">{{ i18n.t('subscription.providerHint') }}</p>
          </div>
          <div class="bo-scroll">
            <table class="bo-data">
              <thead>
                <tr>
                  <th></th>
                  <th class="num">{{ i18n.t('common.total') }}</th>
                  <th class="num">{{ i18n.t('subscription.title') }}</th>
                </tr>
              </thead>
              <tbody>
                <tr>
                  <td [attr.data-label]="i18n.t('backoffice.tabs.users')">{{ i18n.t('backoffice.tabs.users') }}</td>
                  <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('common.total')">{{ usage()?.users ?? '—' }}</td>
                  <td class="num mono" [attr.data-label]="i18n.t('subscription.title')">
                    {{ s.maxUsers ?? i18n.t('subscription.unlimited') }}
                  </td>
                </tr>
                <tr>
                  <td [attr.data-label]="i18n.t('backoffice.tabs.branches')">{{ i18n.t('backoffice.tabs.branches') }}</td>
                  <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('common.total')">{{ usage()?.branches ?? '—' }}</td>
                  <td class="num mono" [attr.data-label]="i18n.t('subscription.title')">
                    {{ s.maxBranches ?? i18n.t('subscription.unlimited') }}
                  </td>
                </tr>
                <tr>
                  <td [attr.data-label]="i18n.t('backoffice.tabs.devices')">{{ i18n.t('backoffice.tabs.devices') }}</td>
                  <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('common.total')">{{ usage()?.devices ?? '—' }}</td>
                  <td class="num mono" [attr.data-label]="i18n.t('subscription.title')">
                    {{ s.maxDevices ?? i18n.t('subscription.unlimited') }}
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
          <div class="bo-panel__foot">
            <button type="button" class="btn-primary" [disabled]="busy()" (click)="reload()">
              {{ i18n.t('subscription.refresh') }}
            </button>
          </div>
        </section>
      } @else {
        <section class="bo-panel">
          <p class="bo-empty">
            <strong>{{ i18n.t('common.loading') }}</strong>
          </p>
          <div class="bo-panel__foot">
            <button type="button" class="btn-primary" [disabled]="busy()" (click)="reload()">
              {{ i18n.t('subscription.refresh') }}
            </button>
          </div>
        </section>
      }
    </div>
  `,
  styleUrls: ['./backoffice-ui.scss'],
  styles: [
    `
      .date {
        font-size: 1rem;
      }
    `,
  ],
})
export class BackofficeSubscriptionComponent implements OnInit {
  private readonly api = inject(ApiService);
  readonly i18n = inject(I18nService);
  readonly subscription = signal<SubscriptionAccess | null>(null);
  readonly usage = signal<{ users: number; branches: number; devices: number } | null>(null);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    void this.reload();
  }

  formatDate(value: string): string {
    return this.i18n.dateTime(value);
  }

  async reload(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      const subscription = await this.api.getJson<SubscriptionAccess>('/api/subscription');
      this.subscription.set(subscription);
      await patchMeta({ subscription });
      this.usage.set(await this.api.getJson('/api/subscription/usage'));
    } catch (error) {
      this.error.set(adminErrorMessage(error));
    } finally {
      this.busy.set(false);
    }
  }
}
