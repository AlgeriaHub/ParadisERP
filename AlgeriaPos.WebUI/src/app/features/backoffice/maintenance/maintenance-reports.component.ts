import { Component, OnInit, computed, inject, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { I18nService } from '../../../core/i18n.service';
import { localizedName } from '../../../core/multilingual';
import { DateInputComponent } from '../../../ui/date-input.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { formatQty } from '../backoffice-format';
import { PAGE_CHROME, PageChrome } from '../shell/page-chrome';
import { MaintenanceProfitGroup, MaintenanceProfitRow } from './maintenance-customer-api.service';
import { MaintenancePagesApi, MaintenanceProductCostSummary, MaintenanceProfitSummary, MaintenanceRechargeSummary } from './maintenance-pages.api';
import { maintenanceError } from './maintenance-words';

/**
 * Maintenance › Reports (Phase M4, docs/maintenance/plan.md §3.3, §6.2 and §7): a working panel, never folded.
 * <ul>
 * <li><b>Recharge against cost</b> — every recharged job of the period: the return-note line's recharge typed at return,
 * the job's share of it (MS-26), the job's cost and the difference (REN-11's figure).</li>
 * <li><b>Cost per equipment type</b> — every job's cost added up per product: parts, outside work, overhead, hours and
 * labour, and the recharges earned.</li>
 * </ul>
 * Without pay-rate access a job's labour money and total are not shown, and a sum only from two jobs with labour and two
 * workers (the server leaves them out; the page says so in words). The period is the day each job was opened, on the
 * company's calendar; the reader's own branch, every branch for the Admin. The three detail tables are paged by the server
 * (owner, 2026-11-17: "paginate in backend, not frontend"; ten rows to begin with) and take no filter beyond the period:
 * each has a totals line, the server's over every row of the period. The profit breakdowns are summaries by group and
 * stay whole (they travel in the profit page's summary).
 */
@Component({
  selector: 'app-maintenance-reports',
  standalone: true,
  imports: [FormsModule, RouterLink, ...PAGE_CHROME, DateInputComponent, ListFooterComponent, StatusComponent, TableStateComponent],
  template: `
    <ng-template appPageToolbar>
      <div class="ui-toolbar">
        <div class="ui-filter">
          <label for="mr-from">{{ i18n.t('maint.jobs.from') }}</label>
          <app-date-input id="mr-from" [ngModel]="from()" (ngModelChange)="from.set($event || '')" />
        </div>
        <div class="ui-filter">
          <label for="mr-to">{{ i18n.t('maint.jobs.to') }}</label>
          <app-date-input id="mr-to" [ngModel]="to()" (ngModelChange)="to.set($event || '')" />
        </div>
        <div class="ui-toolbar__end">
          <button type="button" class="btn-ghost" [disabled]="busy()" (click)="reload()">{{ i18n.t('subscription.refresh') }}</button>
        </div>
      </div>
    </ng-template>

    <div class="bo-stack">
      @if (error() && loaded()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      @if (loaded() && (!recharges().labourMoneyShown || !products().labourMoneyShown || !profit().moneyShown)) {
        <p class="notice info" role="status">{{ i18n.t('maint.reports.withheld') }}</p>
      }

      <section class="ui-surface" aria-labelledby="mr-recharges" [attr.aria-busy]="!rechargePager.loaded()">
        <div class="ui-surface__head">
          <h2 id="mr-recharges">{{ i18n.t('maint.reports.recharges') }}</h2>
        </div>
        <div class="ui-surface__body report">
          <p class="bo-hint">{{ i18n.t('maint.reports.rechargesLead') }} {{ i18n.t('maint.reports.sharing.' + recharges().rechargeSharing) }}</p>
          @if (!rechargePager.loaded() && !error()) {
            <app-table-state kind="loading" />
          } @else if (!rechargePager.loaded()) {
            <app-table-state kind="error" [message]="error()" (retry)="reload()" />
          } @else if (!rechargePager.total()) {
            <app-table-state kind="empty" [message]="i18n.t('maint.reports.noRecharges')" />
          } @else {
            <div class="ui-scroll">
              <table class="ui-table ui-table--cards">
                <thead>
                  <tr>
                    <th>{{ i18n.t('maint.job.number') }}</th>
                    <th>{{ i18n.t('maint.job.returnNote') }}</th>
                    <th>{{ i18n.t('maint.job.product') }}</th>
                    <th class="num">{{ i18n.t('maint.job.qty') }}</th>
                    <th class="num">{{ i18n.t('maint.reports.recharge') }}</th>
                    <th class="num">{{ i18n.t('maint.reports.cost') }}</th>
                    <th class="num">{{ i18n.t('maint.reports.difference') }}</th>
                    <th>{{ i18n.t('maint.reports.costStatus') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (r of rechargePager.rows(); track r.jobId) {
                    <tr>
                      <td [attr.data-label]="i18n.t('maint.job.number')">
                        <button type="button" class="ui-link" (click)="openJob.emit(r.jobId)"><span class="mono" dir="ltr">{{ r.number }}</span></button>
                        <span class="ui-sub">{{ i18n.t('maint.status.' + r.status) }} · {{ r.branchName }}</span>
                      </td>
                      <td [attr.data-label]="i18n.t('maint.job.returnNote')">
                        <span class="mono" dir="ltr">{{ r.returnNoteNumber ?? '—' }}</span>
                        <span class="ui-sub"><span dir="ltr">{{ r.contractNumber ?? '—' }}</span> · {{ r.customerName ?? '—' }}</span>
                      </td>
                      <td [attr.data-label]="i18n.t('maint.job.product')">{{ name(r) }} <span class="ui-sub mono" dir="ltr">{{ r.sku }}</span></td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.job.qty')"><span dir="ltr">{{ qty(r.qty) }}</span></td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.reports.recharge')">
                        {{ i18n.money(r.recharge) }}
                        @if (r.recharge !== r.lineRecharge) {
                          <span class="ui-sub">{{ i18n.t('maint.reports.ofLine', { amount: i18n.money(r.lineRecharge) }) }}</span>
                        }
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.reports.cost')">{{ money(r.total) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.reports.difference')"><strong>{{ money(r.difference) }}</strong></td>
                      <td [attr.data-label]="i18n.t('maint.reports.costStatus')">
                        <app-status [tone]="sheetTone(r.costStatus)">{{ i18n.t('maint.cost.status.' + r.costStatus) }}</app-status>
                      </td>
                    </tr>
                  }
                </tbody>
                <tfoot>
                  <tr>
                    <th scope="row" colspan="4">{{ i18n.t('maint.reports.totals') }}</th>
                    <td class="num mono" [attr.data-label]="i18n.t('maint.reports.recharge')">{{ i18n.money(recharges().recharge) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('maint.reports.cost')">{{ money(recharges().total) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('maint.reports.difference')"><strong>{{ money(recharges().difference) }}</strong></td>
                    <td></td>
                  </tr>
                </tfoot>
              </table>
            </div>
            <app-list-footer [pager]="rechargePager" />
          }
        </div>
      </section>

      <section class="ui-surface" aria-labelledby="mr-products">
        <div class="ui-surface__head">
          <h2 id="mr-products">{{ i18n.t('maint.reports.products') }}</h2>
        </div>
        <div class="ui-surface__body report">
          <p class="bo-hint">{{ i18n.t('maint.reports.productsLead') }}</p>
          @if (productPager.loaded() && !productPager.total()) {
            <app-table-state kind="empty" [message]="i18n.t('maint.reports.noProducts')" />
          } @else if (productPager.loaded()) {
            <div class="ui-scroll">
              <table class="ui-table ui-table--cards">
                <thead>
                  <tr>
                    <th>{{ i18n.t('maint.job.product') }}</th>
                    <th class="num">{{ i18n.t('maint.reports.jobs') }}</th>
                    <th class="num">{{ i18n.t('maint.reports.units') }}</th>
                    <th class="num">{{ i18n.t('maint.cost.parts') }}</th>
                    <th class="num">{{ i18n.t('maint.cost.outsideWork') }}</th>
                    <th class="num">{{ i18n.t('maint.cost.overhead') }}</th>
                    <th class="num">{{ i18n.t('maint.hours.hours') }}</th>
                    <th class="num">{{ i18n.t('maint.cost.labour') }}</th>
                    <th class="num">{{ i18n.t('maint.reports.cost') }}</th>
                    <th class="num">{{ i18n.t('maint.reports.recharges') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (p of productPager.rows(); track p.productId) {
                    <tr>
                      <td [attr.data-label]="i18n.t('maint.job.product')">
                        {{ name(p) }} <span class="ui-sub mono" dir="ltr">{{ p.sku }}</span>
                        <!-- Phase M7d (MS-38): every job on this equipment type, or on one piece of it. -->
                        <a class="ui-link ui-sub" [routerLink]="['/backoffice/maintenance/history']" [queryParams]="{ product: p.productId }">{{ i18n.t('maint.history.open') }}</a>
                        @if (p.provisional) {
                          <span class="ui-sub">{{ i18n.t('maint.reports.someProvisional') }}</span>
                        }
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.reports.jobs')"><span dir="ltr">{{ p.jobs }}</span></td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.reports.units')">
                        <span dir="ltr">{{ qty(p.units) }}</span>
                        <span class="ui-sub">{{ i18n.t('maint.reports.repairedScrapped', { repaired: qty(p.repairedQty), scrapped: qty(p.scrappedQty) }) }}</span>
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.cost.parts')">{{ i18n.money(p.parts) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.cost.outsideWork')">{{ i18n.money(p.outsideWork) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.cost.overhead')">{{ i18n.money(p.overhead) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.hours.hours')"><span dir="ltr">{{ qty(p.labourHours) }}</span></td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.cost.labour')">{{ money(p.labour) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.reports.cost')">
                        <strong>{{ money(p.total) }}</strong>
                        <!-- Phase M7d (MS-30): capitalised into the register vs kept an expense, in words beside the figure. -->
                        @if (p.capitalised) {
                          <span class="ui-sub">{{ i18n.t('maint.reports.capitalisedSplit', { capitalised: i18n.money(p.capitalised), expensed: money(p.expensed ?? null) }) }}</span>
                        }
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.reports.recharges')">{{ i18n.money(p.recharge) }}</td>
                    </tr>
                  }
                </tbody>
                <tfoot>
                  <tr>
                    <th scope="row">{{ i18n.t('maint.reports.totals') }}</th>
                    <td class="num mono" [attr.data-label]="i18n.t('maint.reports.jobs')"><span dir="ltr">{{ products().jobs }}</span></td>
                    <td></td>
                    <td class="num mono" [attr.data-label]="i18n.t('maint.cost.parts')">{{ i18n.money(products().parts) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('maint.cost.outsideWork')">{{ i18n.money(products().outsideWork) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('maint.cost.overhead')">{{ i18n.money(products().overhead) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('maint.hours.hours')"><span dir="ltr">{{ qty(products().labourHours) }}</span></td>
                    <td class="num mono" [attr.data-label]="i18n.t('maint.cost.labour')">{{ money(products().labour) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('maint.reports.cost')">
                      <strong>{{ money(products().total) }}</strong>
                      @if (products().capitalised) {
                        <span class="ui-sub">{{ i18n.t('maint.reports.capitalisedSplit', { capitalised: i18n.money(products().capitalised ?? 0), expensed: money(products().expensed ?? null) }) }}</span>
                      }
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('maint.reports.recharges')">{{ i18n.money(products().recharge) }}</td>
                  </tr>
                </tfoot>
              </table>
            </div>
            <app-list-footer [pager]="productPager" />
          }
        </div>
      </section>

      <!-- Phase M5 (owner, 2026-11-09: company profit): external revenue less cost, per job and by month, branch, kind and product. -->
      <section class="ui-surface" aria-labelledby="mr-profit">
        <div class="ui-surface__head">
          <h2 id="mr-profit">{{ i18n.t('maint.profit.title') }}</h2>
        </div>
        <div class="ui-surface__body report">
          <p class="bo-hint">{{ i18n.t('maint.profit.lead') }}</p>
          <!-- Phase M7c (MS-37): how reworks under guarantee are reported, in words. -->
          <p class="bo-hint">{{ i18n.t('maint.profit.rework.' + (profit().rework ?? 'AddedToOriginalJob')) }}</p>
          @if (profitPager.loaded() && !profitPager.total()) {
            <app-table-state kind="empty" [message]="i18n.t('maint.profit.none')" />
          } @else if (profitPager.loaded()) {
            <div class="ui-scroll">
              <table class="ui-table ui-table--cards">
                <thead>
                  <tr>
                    <th>{{ i18n.t('maint.job.number') }}</th>
                    <th>{{ i18n.t('maint.job.kind') }}</th>
                    <th>{{ i18n.t('maint.profit.what') }}</th>
                    <th class="num">{{ i18n.t('maint.profit.revenue') }}</th>
                    <th class="num">{{ i18n.t('maint.reports.cost') }}</th>
                    <th class="num">{{ i18n.t('maint.profit.profit') }}</th>
                    <th>{{ i18n.t('maint.reports.costStatus') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (r of profitPager.rows(); track r.jobId) {
                    <tr>
                      <td [attr.data-label]="i18n.t('maint.job.number')">
                        <button type="button" class="ui-link" (click)="openJob.emit(r.jobId)"><span class="mono" dir="ltr">{{ r.number }}</span></button>
                        <span class="ui-sub">{{ i18n.t('maint.status.' + r.status) }} · {{ r.branchName }} · <span dir="ltr">{{ r.month }}</span></span>
                      </td>
                      <td [attr.data-label]="i18n.t('maint.job.kind')">{{ i18n.t('maint.kind.' + r.kind) }}</td>
                      <td [attr.data-label]="i18n.t('maint.profit.what')">
                        {{ r.equipment ?? productName(r) }}
                        @if (r.customerName) {
                          <span class="ui-sub">{{ r.customerName }}</span>
                        }
                        @if (r.reworkOfNumber) {
                          <span class="ui-sub">{{ i18n.t('maint.profit.reworkOf', { number: r.reworkOfNumber }) }}</span>
                        }
                        @if (r.reworks) {
                          <span class="ui-sub">{{ i18n.t('maint.profit.withReworks', { n: r.reworks, cost: money(r.reworkCost ?? null) }) }}</span>
                        }
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.profit.revenue')">
                        {{ i18n.money(r.revenue) }}
                        <span class="ui-sub">{{ i18n.t('maint.profit.source.' + r.revenueSource) }}@if (r.invoiceNumber) { · <span dir="ltr">{{ r.invoiceNumber }}</span>}</span>
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.reports.cost')">{{ money(r.cost) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.profit.profit')"><strong>{{ money(r.profit) }}</strong></td>
                      <td [attr.data-label]="i18n.t('maint.reports.costStatus')">
                        <app-status [tone]="sheetTone(r.costStatus)">{{ i18n.t('maint.cost.status.' + r.costStatus) }}</app-status>
                      </td>
                    </tr>
                  }
                </tbody>
                <tfoot>
                  <tr>
                    <th scope="row" colspan="3">{{ i18n.t('maint.reports.totals') }}</th>
                    <td class="num mono" [attr.data-label]="i18n.t('maint.profit.revenue')">{{ i18n.money(profit().revenue) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('maint.reports.cost')">{{ money(profit().cost) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('maint.profit.profit')"><strong>{{ money(profit().profit) }}</strong></td>
                    <td></td>
                  </tr>
                </tfoot>
              </table>
            </div>
            <app-list-footer [pager]="profitPager" />

            <div class="breakdowns">
              @for (b of breakdowns(); track b.key) {
                <div class="ui-scroll">
                  <table class="ui-table ui-table--cards">
                    <caption>{{ i18n.t('maint.profit.by.' + b.key) }}</caption>
                    <thead>
                      <tr>
                        <th>{{ i18n.t('maint.profit.by.' + b.key) }}</th>
                        <th class="num">{{ i18n.t('maint.reports.jobs') }}</th>
                        <th class="num">{{ i18n.t('maint.profit.revenue') }}</th>
                        <th class="num">{{ i18n.t('maint.reports.cost') }}</th>
                        <th class="num">{{ i18n.t('maint.profit.profit') }}</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (g of b.groups; track g.key) {
                        <tr>
                          <td [attr.data-label]="i18n.t('maint.profit.by.' + b.key)">{{ groupLabel(b.key, g) }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('maint.reports.jobs')"><span dir="ltr">{{ g.jobs }}</span></td>
                          <td class="num mono" [attr.data-label]="i18n.t('maint.profit.revenue')">{{ i18n.money(g.revenue) }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('maint.reports.cost')">{{ money(g.cost) }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('maint.profit.profit')"><strong>{{ money(g.profit) }}</strong></td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
              }
            </div>
          }
        </div>
      </section>
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .report {
        display: grid;
        gap: var(--sp-3);
      }
      .breakdowns {
        display: grid;
        gap: var(--sp-4);
        grid-template-columns: repeat(auto-fit, minmax(min(100%, 22rem), 1fr));
      }
      .breakdowns caption {
        text-align: start;
        font-weight: 600;
        padding-block-end: var(--sp-2);
      }
      .report > .bo-hint {
        margin: 0;
      }
    `,
  ],
})
export class MaintenanceReportsComponent implements OnInit {
  private readonly pages = inject(MaintenancePagesApi);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  /** A job's number was clicked: its page. */
  readonly openJob = output<string>();

  readonly from = signal('');
  readonly to = signal('');

  /**
   * Ten rows to begin with, 20, 50 or 100 at the user's choice (owner, 2026-11-17), each page asked of the server; another
   * period starts at page one. Paging only: each table's totals line is the server's over the whole period.
   */
  readonly rechargePager = createServerPager(
    (page) => this.pages.recharges({ skip: page.skip, take: page.take, ...page.filters }),
    { key: 'maintenance.reports.recharges', filters: () => ({ from: this.from(), to: this.to() }) },
  );
  readonly productPager = createServerPager(
    (page) => this.pages.products({ skip: page.skip, take: page.take, ...page.filters }),
    { key: 'maintenance.reports.products', filters: () => ({ from: this.from(), to: this.to() }) },
  );
  readonly profitPager = createServerPager(
    (page) => this.pages.profit({ skip: page.skip, take: page.take, ...page.filters }),
    { key: 'maintenance.reports.profit', filters: () => ({ from: this.from(), to: this.to() }) },
  );

  /** Each report's totals line and words (the server's, over the period), or nothing yet. */
  readonly recharges = computed<MaintenanceRechargeSummary>(() => this.rechargePager.summary() ?? emptyRecharges());
  readonly products = computed<MaintenanceProductCostSummary>(() => this.productPager.summary() ?? emptyProducts());
  /** Phase M5: company profit = external revenue − cost (plan §3.3). */
  readonly profit = computed<MaintenanceProfitSummary>(() => this.profitPager.summary() ?? emptyProfit());
  readonly breakdowns = computed(() => {
    const p = this.profit();
    return [
      { key: 'month', groups: p.byMonth },
      { key: 'branch', groups: p.byBranch },
      { key: 'kind', groups: p.byKind },
      { key: 'product', groups: p.byProduct },
    ];
  });
  /** Every report has answered once. */
  readonly loaded = computed(() => this.rechargePager.loaded() && this.productPager.loaded() && this.profitPager.loaded());
  readonly busy = computed(() => this.rechargePager.loading() || this.productPager.loading() || this.profitPager.loading());
  /** A report could not be read, in words. */
  readonly error = computed(() => {
    const e = this.rechargePager.error() ?? this.productPager.error() ?? this.profitPager.error();
    return e ? maintenanceError(e, (k) => this.i18n.t(k)) : null;
  });

  ngOnInit(): void {
    // The reports load themselves through their pagers; a new period asks again.
    this.chrome?.description.set(this.i18n.t('maint.reports.lead'));
  }

  /** Refresh: the three pages on screen, asked again. */
  async reload(): Promise<void> {
    await Promise.all([this.rechargePager.reload(), this.productPager.reload(), this.profitPager.reload()]);
  }

  /** A figure the server left out (labour privacy) is said in words, never shown as a zero. */
  money(value: number | null): string {
    return value === null ? this.i18n.t('maint.cost.notShown') : this.i18n.money(value);
  }

  name(x: { nameAr: string; nameFr: string; nameEn: string; sku: string }): string {
    return localizedName(x, this.i18n.lang()) || x.sku;
  }

  qty(value: number): string {
    return formatQty(value, this.i18n.lang());
  }

  productName(r: MaintenanceProfitRow): string {
    return localizedName({ nameAr: r.productNameAr, nameFr: r.productNameFr, nameEn: r.productNameEn }, this.i18n.lang()) || r.productSku || '—';
  }

  /** A breakdown line's words: a month as it is, a kind by its name, a product by its name (none: the customer jobs that name none). */
  groupLabel(key: string, g: MaintenanceProfitGroup): string {
    if (key === 'kind') return this.i18n.t('maint.kind.' + g.key);
    if (key === 'product') {
      if (!g.key) return this.i18n.t('maint.profit.noProduct');
      return localizedName({ nameAr: g.nameAr ?? '', nameFr: g.nameFr ?? '', nameEn: g.nameEn ?? '' }, this.i18n.lang()) || g.label;
    }
    return g.label;
  }

  sheetTone(status: string) {
    return toneOf('costSheet', status);
  }
}

function emptyRecharges(): MaintenanceRechargeSummary {
  return {
    from: null, to: null, rechargeSharing: 'ByUnits', labourMoneyShown: true, recharge: 0, parts: 0, overhead: 0, outsideWork: 0,
    labourHours: 0, labour: 0, total: 0, difference: 0, totalsWithheld: false,
  };
}

function emptyProfit(): MaintenanceProfitSummary {
  return {
    from: null, to: null, moneyShown: true, byMonth: [], byBranch: [], byKind: [], byProduct: [], revenue: 0, cost: 0, profit: 0,
    totalsWithheld: false,
  };
}

function emptyProducts(): MaintenanceProductCostSummary {
  return {
    from: null, to: null, labourMoneyShown: true, jobs: 0, parts: 0, overhead: 0, outsideWork: 0, labourHours: 0, recharge: 0,
    labour: 0, total: 0, totalsWithheld: false,
  };
}
