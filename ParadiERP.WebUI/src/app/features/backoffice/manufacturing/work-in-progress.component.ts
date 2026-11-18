import { Component, OnInit, computed, inject, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { I18nService } from '../../../core/i18n.service';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { PAGE_CHROME, PageChrome } from '../shell/page-chrome';
import { ManufacturingApiService, ProductionLineSummary } from './manufacturing-api.service';
import { ManufacturingPagesApi } from './manufacturing-pages.api';
import { mfgError, qty } from './manufacturing-shared';

/**
 * Manufacturing › Work in progress (costing, Phase A — R2 of docs/manufacturing/costing-plan.md): every live
 * order on the lines the reader reaches, what it has cost so far, what it released to stock, and what is
 * still in progress — material at the line included. Computed now from what was recorded; nothing is
 * posted, and an order whose figures are not final says so.
 */
@Component({
  selector: 'app-mfg-work-in-progress',
  standalone: true,
  imports: [FormsModule, ...PAGE_CHROME, ListFooterComponent, StatusComponent, TableStateComponent],
  template: `
    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter">
          <label for="wip-line">{{ i18n.t('mfg.boms.line') }}</label>
          <select id="wip-line" [ngModel]="lineId()" (ngModelChange)="lineId.set($event)">
            <option [ngValue]="null">{{ i18n.t('mfg.orders.allLines') }}</option>
            @for (l of lines(); track l.id) {
              <option [ngValue]="l.id">{{ l.code }}</option>
            }
          </select>
        </div>
        <div class="ui-filter">
          <label for="wip-status">{{ i18n.t('common.state') }}</label>
          <select id="wip-status" [ngModel]="statusFilter()" (ngModelChange)="statusFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            @for (s of statuses(); track s) {
              <option [ngValue]="s">{{ i18n.t('mfg.orderStatus.' + s) }}</option>
            }
          </select>
        </div>
        <div class="ui-filter">
          <label for="wip-provisional">{{ i18n.t('mfg.filter.provisional') }}</label>
          <select id="wip-provisional" [ngModel]="provisionalFilter()" (ngModelChange)="provisionalFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            <option ngValue="yes">{{ i18n.t('filters.yes') }}</option>
            <option ngValue="no">{{ i18n.t('filters.no') }}</option>
          </select>
        </div>
        <div class="ui-toolbar__end">
          @if (filtered()) {
            <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
          }
          <button type="button" class="btn-ghost" [disabled]="pager.loading()" (click)="reload()">{{ i18n.t('subscription.refresh') }}</button>
        </div>
      </div>
    </ng-template>

    <div class="bo-stack">
      <p class="bo-hint">{{ i18n.t('mfg.cost.previewNotice') }}</p>
      @if (error() && data()?.all) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      @if (data(); as d) {
        <div class="ui-metrics">
          <div class="ui-metric ui-metric--lead"><span class="ui-metric__label">{{ i18n.t('mfg.cost.wip') }}</span><strong class="ui-metric__value mono">{{ amount(d.workInProgress) }}</strong></div>
          <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.cost.toDate') }}</span><strong class="ui-metric__value mono">{{ amount(d.costToDate) }}</strong></div>
          <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.cost.released') }}</span><strong class="ui-metric__value mono">{{ amount(d.releasedValue) }}</strong></div>
          <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.cost.atLineValue') }}</span><strong class="ui-metric__value mono">{{ i18n.money(d.atLineValue) }}</strong></div>
        </div>
        @if (d.moneyShown === false) {
          <p class="notice info">{{ i18n.t('mfg.cost.wipWithheld') }}</p>
        }
        @if (d.lines?.length) {
          <section class="ui-surface" [attr.aria-label]="i18n.t('mfg.cost.byLine')">
            <div class="ui-scroll">
              <table class="ui-table ui-table--cards">
                <caption class="sr-only">{{ i18n.t('mfg.cost.byLine') }}</caption>
                <thead>
                  <tr>
                    <th>{{ i18n.t('mfg.boms.line') }}</th>
                    <th class="num">{{ i18n.t('mfg.cost.ordersCount') }}</th>
                    <th class="num">{{ i18n.t('mfg.cost.workersCount') }}</th>
                    <th class="num">{{ i18n.t('mfg.cost.toDate') }}</th>
                    <th class="num">{{ i18n.t('mfg.cost.released') }}</th>
                    <th class="num">{{ i18n.t('mfg.cost.atLineValue') }}</th>
                    <th class="num">{{ i18n.t('mfg.cost.wip') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (l of d.lines; track l.lineId) {
                    <tr>
                      <td class="mono" [attr.data-label]="i18n.t('mfg.boms.line')">
                        {{ l.lineCode }}
                        @if (l.withheld) {
                          <span class="ui-sub">{{ i18n.t('mfg.cost.lineWithheld') }}</span>
                        }
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.ordersCount')">{{ l.orders }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.workersCount')">{{ l.workers }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.toDate')">{{ amount(l.costToDate) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.released')">{{ amount(l.releasedValue) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.atLineValue')">{{ i18n.money(l.atLineValue) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.wip')"><strong>{{ amount(l.workInProgress) }}</strong></td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          </section>
        }
      }
      <section class="ui-surface" [attr.aria-busy]="pager.loading()">
        @if (error() && !data()?.all) {
          <app-table-state kind="error" [message]="error()" (retry)="reload()" />
        } @else if (!data()) {
          <app-table-state kind="loading" />
        } @else if (!data()?.all) {
          <app-table-state kind="empty" [message]="i18n.t('mfg.cost.noWip')" />
        } @else if (!pager.total()) {
          <app-table-state kind="noResults" (clear)="clearFilters()" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('mfg.orders.number') }}</th>
                  <th>{{ i18n.t('mfg.boms.line') }}</th>
                  <th>{{ i18n.t('mfg.boms.product') }}</th>
                  <th>{{ i18n.t('common.state') }}</th>
                  <th class="num">{{ i18n.t('mfg.cost.toDate') }}</th>
                  <th class="num">{{ i18n.t('mfg.cost.released') }}</th>
                  <th class="num">{{ i18n.t('mfg.cost.atLineValue') }}</th>
                  <th class="num">{{ i18n.t('mfg.cost.wip') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (r of pager.rows(); track r.orderId) {
                  <tr>
                    <td [attr.data-label]="i18n.t('mfg.orders.number')">
                      <button type="button" class="ui-link" (click)="open.emit(r.orderId)"><span dir="ltr">{{ r.orderNumber }}</span></button>
                      @if (r.provisional) {
                        <span class="ui-sub">{{ i18n.t('mfg.cost.provisional') }}</span>
                      }
                    </td>
                    <td class="mono" [attr.data-label]="i18n.t('mfg.boms.line')">{{ r.lineCode }}</td>
                    <td class="mono" [attr.data-label]="i18n.t('mfg.boms.product')">
                      <span dir="ltr">{{ r.productSku }}</span>
                      <span class="ui-sub" dir="ltr">{{ qty(r.acceptedBaseQty) }} / {{ qty(r.plannedBaseQty) }} {{ r.baseUnitCode }}</span>
                    </td>
                    <td [attr.data-label]="i18n.t('common.state')"><app-status [tone]="tone(r.status)">{{ i18n.t('mfg.orderStatus.' + r.status) }}</app-status></td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.toDate')">{{ amount(r.costToDate) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.released')">{{ amount(r.releasedValue) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.atLineValue')">{{ i18n.money(r.atLineValue) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.wip')"><strong>{{ amount(r.workInProgress) }}</strong></td>
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
export class MfgWorkInProgressComponent implements OnInit {
  private readonly pages = inject(ManufacturingPagesApi);
  private readonly api = inject(ManufacturingApiService);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  readonly open = output<string>();

  readonly qty = qty;
  readonly lines = signal<ProductionLineSummary[]>([]);
  readonly lineId = signal<string | null>(null);

  // The order's state, and whether its figures are still provisional — filtered on the server. The line is the report's own filter.
  readonly statusFilter = signal<string>('all');
  readonly provisionalFilter = signal<'all' | 'yes' | 'no'>('all');
  readonly filtered = computed(() => this.statusFilter() !== 'all' || this.provisionalFilter() !== 'all');
  /**
   * The live orders a page at a time from the server (owner, 2026-11-17: "paginate in backend, not frontend").
   * The summary is the report without its rows: the tiles and per-line table over every live order of the line,
   * withheld where the report withholds them, and the states it holds.
   */
  readonly pager = createServerPager(
    (page) => this.pages.workInProgress({ skip: page.skip, take: page.take }, page.filters),
    {
      key: 'mfg.wip',
      filters: () => ({ lineId: this.lineId(), status: this.statusFilter(), provisional: this.provisionalFilter() }),
    },
  );
  /** The report's tiles; null until the first answer. */
  readonly data = computed(() => this.pager.summary());
  /** The states the report holds, in the order they first appear. */
  readonly statuses = computed(() => this.data()?.statuses ?? []);

  /** The report could not be read, in words. */
  readonly error = computed(() => {
    const e = this.pager.error();
    return e ? mfgError(e, (k) => this.i18n.t(k)) : null;
  });

  clearFilters(): void {
    this.statusFilter.set('all');
    this.provisionalFilter.set('all');
  }

  async ngOnInit(): Promise<void> {
    this.chrome?.description.set(this.i18n.t('mfg.cost.wipLead'));
    this.api.lines(true).then((l) => this.lines.set(l), () => undefined);
  }

  /** Asks again for the page on screen. */
  async reload(): Promise<void> {
    await this.pager.reload();
  }

  tone(status: string) {
    return toneOf('productionOrder', status);
  }

  /** An amount, or the word for one withheld from this reader. */
  amount(value: number | null): string {
    return value === null ? this.i18n.t('mfg.cost.withheld') : this.i18n.money(value);
  }
}
