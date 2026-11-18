import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { mayDo } from '../../../core/landing';
import { localizedName } from '../../../core/multilingual';
import { RegionalService } from '../../../core/regional.service';
import { DateInputComponent } from '../../../ui/date-input.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { MENU } from '../../../ui/menu.component';
import { createServerPager } from '../../../ui/server-pager';
import { TableStateComponent } from '../../../ui/table-state.component';
import { PAGE_CHROME, PageChrome } from '../shell/page-chrome';
import { MaintenanceCustomerApi, MaintenanceRepairRate, MaintenanceRepairRates } from './maintenance-customer-api.service';
import { MaintenancePagesApi } from './maintenance-pages.api';
import { maintenanceError } from './maintenance-words';

/**
 * Maintenance › Repair rates (Phase M5a, MS-23, REN-11): what repairing one unit of a rental product is usually charged
 * to the hire customer, before tax. While MS-23 says "suggested from a rate", the return-note screen offers the
 * repairable units × this rate as a suggestion the clerk may take or change; the recharge is still typed, and the rental
 * engine, rules and postings never read this table. Kept by `maintenance.pricing`. The table is paged by the server
 * (owner, 2026-11-17: "paginate in backend, not frontend"; ten rows to begin with) and filtered there by the product's
 * words and the day its rate was last set (on the company's clock).
 */
@Component({
  selector: 'app-maintenance-repair-rates',
  standalone: true,
  imports: [FormsModule, ...PAGE_CHROME, ...MENU, DateInputComponent, ListFooterComponent, TableStateComponent],
  template: `
    @if (rateCount()) {
      <ng-template appPageToolbar>
        <div class="ui-toolbar" role="search">
          <div class="ui-filter ui-filter--search">
            <label for="mrr-q">{{ i18n.t('products.search') }}</label>
            <input id="mrr-q" type="search" [ngModel]="query()" (ngModelChange)="query.set($event)" />
          </div>
          <div class="ui-filter">
            <label for="mrr-from">{{ i18n.t('filters.from') }}</label>
            <app-date-input id="mrr-from" [ngModel]="from()" (ngModelChange)="from.set($event || '')" />
          </div>
          <div class="ui-filter">
            <label for="mrr-to">{{ i18n.t('filters.to') }}</label>
            <app-date-input id="mrr-to" [ngModel]="to()" (ngModelChange)="to.set($event || '')" />
          </div>
          @if (filtered()) {
            <div class="ui-toolbar__end">
              <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
            </div>
          }
        </div>
      </ng-template>
    }

    <div class="bo-stack">
      @if (error() || (listError() && pager.loaded())) {
        <p class="notice danger" role="alert">{{ error() || listError() }}</p>
      }
      @if (data(); as d) {
        <p class="notice" [class.info]="d.returnRecharge === 'SuggestedFromRate'" role="status">
          {{ i18n.t(d.returnRecharge === 'SuggestedFromRate' ? 'maint.rates.on' : 'maint.rates.off') }}
        </p>
      }

      @if (canEdit()) {
        <section class="bo-panel" aria-labelledby="mrr-form">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title" id="mrr-form">{{ i18n.t('maint.rates.set') }}</h2>
          </div>
          <div class="bo-panel__body bo-form">
            <div class="bo-field">
              <label for="mrr-product">{{ i18n.t('maint.job.product') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <select id="mrr-product" [ngModel]="productId()" (ngModelChange)="pick($event)">
                <option [ngValue]="null">{{ i18n.t('maint.rates.pickProduct') }}</option>
                @for (p of data()?.products ?? []; track p.id) {
                  <option [ngValue]="p.id">{{ name(p) }} ({{ p.sku }})</option>
                }
              </select>
            </div>
            <div class="bo-field">
              <label for="mrr-rate">{{ i18n.t('maint.rates.rate') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <input id="mrr-rate" type="number" min="0" class="num mono" [step]="regional.moneyStep()" [(ngModel)]="rate" />
              <p class="bo-hint">{{ i18n.t('maint.rates.rateHint') }}</p>
            </div>
            <div class="ui-actionbar">
              <button type="button" class="btn-primary" [disabled]="busy() || !productId() || !(rate && rate > 0)" (click)="save()">{{ i18n.t('maint.rates.save') }}</button>
            </div>
          </div>
        </section>
      }

      <section class="ui-surface">
        @if (!pager.loaded() && !listError()) {
          <app-table-state kind="loading" />
        } @else if (!pager.loaded()) {
          <app-table-state kind="error" [message]="listError()" (retry)="pager.reload()" />
        } @else if (!rateCount()) {
          <app-table-state kind="empty" [message]="i18n.t('maint.rates.none')" />
        } @else if (!pager.total()) {
          <app-table-state kind="noResults" (clear)="clearFilters()" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('maint.job.product') }}</th>
                  <th class="num">{{ i18n.t('maint.rates.rate') }}</th>
                  <th>{{ i18n.t('maint.rates.updated') }}</th>
                  <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                </tr>
              </thead>
              <tbody>
                @for (r of pager.rows(); track r.productId) {
                  <tr>
                    <td [attr.data-label]="i18n.t('maint.job.product')">{{ name(r) }} <span class="ui-sub mono" dir="ltr">{{ r.sku }}</span></td>
                    <td class="num mono" [attr.data-label]="i18n.t('maint.rates.rate')">{{ i18n.money(r.ratePerUnit) }}</td>
                    <td [attr.data-label]="i18n.t('maint.rates.updated')">{{ r.updatedBy ?? '—' }} <span class="ui-sub mono">{{ i18n.dateTime(r.updatedAtUtc) }}</span></td>
                    <td class="ui-cell-menu">
                      @if (canEdit()) {
                        <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + r.sku">
                          <span menuTrigger aria-hidden="true">⋯</span>
                          <button type="button" appMenuItem (click)="edit(r)">{{ i18n.t('maint.rates.edit') }}</button>
                          <hr appMenuSeparator />
                          <button type="button" appMenuItem class="is-danger" (click)="remove(r)">{{ i18n.t('maint.rates.remove') }}</button>
                        </app-menu>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </section>
      <app-list-footer [pager]="pager" [loading]="!pager.loaded()" />
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
})
export class MaintenanceRepairRatesComponent implements OnInit {
  private readonly api = inject(MaintenanceCustomerApi);
  private readonly pages = inject(MaintenancePagesApi);
  private readonly auth = inject(AuthService);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly regional = inject(RegionalService);
  readonly i18n = inject(I18nService);

  readonly busy = signal(false);
  /** A save or a removal refused, in words. */
  readonly error = signal<string | null>(null);
  readonly productId = signal<string | null>(null);
  rate: number | null = null;

  readonly canEdit = computed(() => mayDo(this.auth.currentUser(), 'maintenance.pricing'));

  // The list's filters (owner, 2026-11-17). Each is sent to the server, which filters every rate.
  readonly query = signal('');
  /** The day the rate was last set, on the company's clock (`yyyy-MM-dd`). */
  readonly from = signal('');
  readonly to = signal('');
  readonly filtered = computed(() => !!this.query().trim() || !!this.from() || !!this.to());

  /** Ten rows to begin with, 20, 50 or 100 at the user's choice; each page is a request, a new filter starts at page one. */
  readonly pager = createServerPager(
    (page) => this.pages.repairRates({ skip: page.skip, take: page.take, ...page.filters }),
    { key: 'maintenance.repairRates', filters: () => ({ q: this.query().trim(), from: this.from(), to: this.to() }) },
  );
  /** MS-23 as saved, the products a rate may be set for, and how many rates exist before a filter. */
  readonly data = computed(() => this.pager.summary());
  readonly rateCount = computed(() => this.data()?.rates ?? 0);
  /** The list could not be read, in words. */
  readonly listError = computed(() => {
    const e = this.pager.error();
    return e ? maintenanceError(e, (k) => this.i18n.t(k)) : null;
  });

  clearFilters(): void {
    this.query.set('');
    this.from.set('');
    this.to.set('');
  }

  ngOnInit(): void {
    // The list loads itself through its pager.
    this.chrome?.description.set(this.i18n.t('maint.rates.lead'));
  }

  name(x: { nameAr: string; nameFr: string; nameEn: string; sku: string }): string {
    return localizedName(x, this.i18n.lang()) || x.sku;
  }

  /** A product chosen in the form: its rate today, read from the server whatever page it is on. */
  async pick(productId: string | null): Promise<void> {
    this.productId.set(productId);
    this.rate = null;
    if (!productId) return;
    try {
      const found = (await this.pages.repairRates({ skip: 0, take: 1, productId })).items[0];
      if (this.productId() === productId) this.rate = found?.ratePerUnit ?? null;
    } catch {
      // No rate read: the form starts empty, as for a product without one.
    }
  }

  /** A row's ⋯ Edit: the row is on screen, so its rate is known. */
  edit(r: MaintenanceRepairRate): void {
    this.productId.set(r.productId);
    this.rate = r.ratePerUnit;
  }

  async save(): Promise<void> {
    const id = this.productId();
    if (!id || !this.rate || this.rate <= 0) return;
    await this.act(() => this.api.setRepairRate(id, Number(this.rate)));
  }

  async remove(r: MaintenanceRepairRate): Promise<void> {
    await this.act(() => this.api.removeRepairRate(r.productId));
  }

  /** Sets or removes, then asks again for the page on screen. */
  private async act(call: () => Promise<MaintenanceRepairRates>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await call();
      await this.pager.reload();
    } catch (e) {
      this.error.set(maintenanceError(e, (k) => this.i18n.t(k)));
    } finally {
      this.busy.set(false);
    }
  }
}
