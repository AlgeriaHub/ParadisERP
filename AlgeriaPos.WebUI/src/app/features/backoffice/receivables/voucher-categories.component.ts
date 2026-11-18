import { Component, OnInit, inject, signal } from '@angular/core';
import { adminErrorMessage } from '../../../core/admin.service';
import { I18nService } from '../../../core/i18n.service';
import { ExpenseCategoryInfo, PaymentVouchersApi } from './payment-vouchers-api.service';

/**
 * Which expense categories must name a rental contract (the owner, 2026-10-02, answer 26).
 *
 * The same panel, the same calls and the same rule as the one that sat on top of the voucher list,
 * moved to Finance › Settings (REDESIGN_PLAN.md §3.2): a setting, kept apart from the day's work.
 * The page is a manager's and an admin's only (core/nav.ts), as the panel was (`canManage`).
 */
@Component({
  selector: 'app-voucher-categories',
  standalone: true,
  template: `
    @if (error()) {
      <p class="notice danger" role="alert">{{ error() }}</p>
    }
    @if (categories().length) {
      <section class="ui-surface" aria-labelledby="vc-title">
        <div class="ui-surface__head">
          <h2 id="vc-title">{{ i18n.t('pv.categories.title') }}</h2>
          <p>{{ i18n.t('pv.categories.note') }}</p>
        </div>
        <div class="body bo-form">
          <div class="bo-fields bo-fields--roomy">
            @for (k of categories(); track k.key) {
              <div class="bo-check">
                <input
                  [id]="'pv-req-' + k.key"
                  type="checkbox"
                  [checked]="k.contractRequired"
                  [disabled]="saving()"
                  (change)="toggleContractRule(k.key)"
                />
                <label [for]="'pv-req-' + k.key">{{ i18n.t('pv.category.' + k.key) }}</label>
              </div>
            }
          </div>
        </div>
      </section>
    }
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .ui-surface__head h2 {
        margin: 0;
        font-size: var(--fs-section);
        font-weight: 600;
      }
      .body {
        padding: var(--sp-4) var(--sp-5) var(--sp-5);
      }
    `,
  ],
})
export class VoucherCategoriesComponent implements OnInit {
  private readonly vouchersApi = inject(PaymentVouchersApi);
  readonly i18n = inject(I18nService);

  readonly categories = signal<ExpenseCategoryInfo[]>([]);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    this.categories.set(await this.vouchersApi.categories().catch(() => []));
  }

  async toggleContractRule(key: string): Promise<void> {
    const keys = this.categories()
      .filter((k) => (k.key === key ? !k.contractRequired : k.contractRequired))
      .map((k) => k.key);
    this.saving.set(true);
    try {
      this.categories.set(await this.vouchersApi.setContractCategories(keys));
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.saving.set(false);
    }
  }
}
