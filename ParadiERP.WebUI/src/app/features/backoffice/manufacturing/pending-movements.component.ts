import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { I18nService } from '../../../core/i18n.service';
import { DateInputComponent } from '../../../ui/date-input.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { TableStateComponent } from '../../../ui/table-state.component';
import { PAGE_CHROME, PageChrome } from '../shell/page-chrome';
import { ManufacturingApiService, ProductionLineSummary } from './manufacturing-api.service';
import { ManufacturingPagesApi } from './manufacturing-pages.api';
import { mfgError, qty } from './manufacturing-shared';

/**
 * Manufacturing › Awaiting posting (owner, 2026-10-26, decision 1: "do not silently create discrepancies"):
 * what manufacturing has moved in stock that the ledger has not seen — materials issued to orders less those
 * returned (work in progress) and finished goods released — each at the reference cost it carried when it
 * moved. The accountant reconciles from here before manufacturing postings are switched on.
 */
@Component({
  selector: 'app-mfg-pending-movements',
  standalone: true,
  imports: [FormsModule, ...PAGE_CHROME, DateInputComponent, ListFooterComponent, TableStateComponent],
  template: `
    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter">
          <label for="pm-line">{{ i18n.t('mfg.boms.line') }}</label>
          <select id="pm-line" [ngModel]="lineId()" (ngModelChange)="lineId.set($event)">
            <option [ngValue]="null">{{ i18n.t('mfg.orders.allLines') }}</option>
            @for (l of lines(); track l.id) {
              <option [ngValue]="l.id">{{ l.code }}</option>
            }
          </select>
        </div>
        <div class="ui-filter">
          <label for="pm-kind">{{ i18n.t('mfg.reports.document') }}</label>
          <select id="pm-kind" [ngModel]="kindFilter()" (ngModelChange)="kindFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            @for (k of kinds(); track k) {
              <option [ngValue]="k">{{ i18n.t('mfg.movementKind.' + k) }}</option>
            }
          </select>
        </div>
        <div class="ui-filter">
          <label for="pm-from">{{ i18n.t('filters.from') }}</label>
          <app-date-input id="pm-from" [ngModel]="dayFrom()" (ngModelChange)="dayFrom.set($event || '')" />
        </div>
        <div class="ui-filter">
          <label for="pm-to">{{ i18n.t('filters.to') }}</label>
          <app-date-input id="pm-to" [ngModel]="dayTo()" (ngModelChange)="dayTo.set($event || '')" />
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
      <p class="notice warn" role="status">{{ i18n.t('mfg.reports.pendingNotice') }}</p>
      @if (error() && data()?.all) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      @if (data(); as d) {
        <div class="ui-metrics">
          <div class="ui-metric ui-metric--lead"><span class="ui-metric__label">{{ i18n.t('mfg.reports.wip') }}</span><strong class="ui-metric__value mono">{{ i18n.money(d.workInProgressValue) }}</strong></div>
          <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.reports.issuedValue') }}</span><strong class="ui-metric__value mono">{{ i18n.money(d.issuedValue) }}</strong></div>
          <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.reports.returnedValue') }}</span><strong class="ui-metric__value mono">{{ i18n.money(d.returnedValue) }}</strong></div>
          <div class="ui-metric">
            <span class="ui-metric__label">{{ i18n.t('mfg.reports.releasedValue') }}</span>
            @if (d.releasesValued) {
              <strong class="ui-metric__value mono">{{ i18n.money(d.releasedValue ?? 0) }}</strong>
            } @else {
              <strong class="ui-metric__value">{{ i18n.t('mfg.reports.notValued') }}</strong>
            }
          </div>
        </div>
        <p class="bo-hint">{{ i18n.t(d.releasesValued ? 'mfg.reports.releasesValuedHint' : 'mfg.reports.releasesNotValuedHint') }}</p>
      }
      <section class="ui-surface" [attr.aria-busy]="pager.loading()">
        @if (error() && !data()?.all) {
          <app-table-state kind="error" [message]="error()" (retry)="reload()" />
        } @else if (!data()) {
          <app-table-state kind="loading" />
        } @else if (!data()?.all) {
          <app-table-state kind="empty" [message]="i18n.t('mfg.reports.noPending')" />
        } @else if (!pager.total()) {
          <app-table-state kind="noResults" (clear)="clearFilters()" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('mfg.orders.when') }}</th>
                  <th>{{ i18n.t('mfg.reports.document') }}</th>
                  <th>{{ i18n.t('mfg.orders.number') }}</th>
                  <th>{{ i18n.t('mfg.boms.line') }}</th>
                  <th>{{ i18n.t('mfg.boms.product') }}</th>
                  <th class="num">{{ i18n.t('mfg.reports.quantity') }}</th>
                  <th class="num">{{ i18n.t('mfg.reports.unitCost') }}</th>
                  <th class="num">{{ i18n.t('mfg.reports.value') }}</th>
                  <th>{{ i18n.t('mfg.reports.direction') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (r of pager.rows(); track $index) {
                  <tr>
                    <td class="mono" [attr.data-label]="i18n.t('mfg.orders.when')">{{ i18n.dateTime(r.atUtc) }}</td>
                    <td class="mono" [attr.data-label]="i18n.t('mfg.reports.document')"><span dir="ltr">{{ r.number }}</span> <span class="ui-sub">{{ i18n.t('mfg.movementKind.' + r.kind) }}</span></td>
                    <td class="mono" [attr.data-label]="i18n.t('mfg.orders.number')"><span dir="ltr">{{ r.orderNumber }}</span></td>
                    <td class="mono" [attr.data-label]="i18n.t('mfg.boms.line')">{{ r.lineCode }}</td>
                    <td class="mono" [attr.data-label]="i18n.t('mfg.boms.product')"><span dir="ltr">{{ r.sku }}</span></td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.reports.quantity')">{{ qty(r.baseQty) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.reports.unitCost')">{{ r.unitCostSnapshot === null ? '—' : i18n.money(r.unitCostSnapshot) }}</td>
                    <td class="num" [attr.data-label]="i18n.t('mfg.reports.value')">
                      @if (r.value === null) {
                        {{ i18n.t('mfg.reports.notValued') }}
                      } @else {
                        <span class="mono">{{ i18n.money(r.value) }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('mfg.reports.direction')">{{ i18n.t('mfg.direction.' + r.direction) }}</td>
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
export class MfgPendingMovementsComponent implements OnInit {
  private readonly pages = inject(ManufacturingPagesApi);
  private readonly api = inject(ManufacturingApiService);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  readonly qty = qty;
  readonly lines = signal<ProductionLineSummary[]>([]);
  readonly lineId = signal<string | null>(null);

  // The document's kind, and its day between two company days (ISO; the server reads each movement's day in the
  // company's zone). The line is the report's own filter.
  readonly kindFilter = signal<string>('all');
  readonly dayFrom = signal('');
  readonly dayTo = signal('');
  readonly filtered = computed(() => this.kindFilter() !== 'all' || !!this.dayFrom() || !!this.dayTo());
  /**
   * The movements a page at a time from the server (owner, 2026-11-17: "paginate in backend, not frontend").
   * The summary is the report's tiles, over every movement of the line, and the kinds it holds.
   */
  readonly pager = createServerPager(
    (page) => this.pages.pendingMovements({ skip: page.skip, take: page.take }, page.filters),
    {
      key: 'mfg.pending',
      filters: () => ({ lineId: this.lineId(), kind: this.kindFilter(), from: this.dayFrom(), to: this.dayTo() }),
    },
  );
  /** The report's tiles and choices; null until the first answer. */
  readonly data = computed(() => this.pager.summary());
  /** The kinds the report holds (issue, return, release…), in the order they first appear. */
  readonly kinds = computed(() => this.data()?.kinds ?? []);

  /** The report could not be read, in words. */
  readonly error = computed(() => {
    const e = this.pager.error();
    return e ? mfgError(e, (k) => this.i18n.t(k)) : null;
  });

  clearFilters(): void {
    this.kindFilter.set('all');
    this.dayFrom.set('');
    this.dayTo.set('');
  }

  async ngOnInit(): Promise<void> {
    this.chrome?.description.set(this.i18n.t('mfg.reports.pendingLead'));
    this.api.lines(true).then((l) => this.lines.set(l), () => undefined);
  }

  /** Asks again for the page on screen. */
  async reload(): Promise<void> {
    await this.pager.reload();
  }
}
