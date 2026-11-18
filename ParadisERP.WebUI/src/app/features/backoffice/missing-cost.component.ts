import { Component, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AdminBranch, AdminService, MissingCostRow, adminErrorMessage } from '../../core/admin.service';
import { BranchLock } from '../../core/branch-lock';
import { I18nService } from '../../core/i18n.service';
import { localizedName } from '../../core/multilingual';
import { DateInputComponent } from '../../ui/date-input.component';
import { ListFooterComponent } from '../../ui/list-footer.component';
import { createServerPager } from '../../ui/server-pager';
import { StatusComponent } from '../../ui/status.component';
import { toneOf } from '../../ui/status-tones';
import { TableStateComponent } from '../../ui/table-state.component';
import { FinancePagesApi } from './finance/finance-pages.api';
import { PAGE_CHROME, PageChrome } from './shell/page-chrome';

/**
 * Reports › Sales without a cost (owner, 2026-10-31, SAL-02). A sale is never blocked because its goods
 * have no cost; the server flags the line instead, and this page says which products sold that way in a
 * period, how much of the period's sales they are, and whether a cost has been entered since. The name
 * opens the product, where the cost is set. The estimate is today's cost times the quantity — shown, never
 * posted. Days are the company's (`from`/`to` as `yyyy-MM-dd`); with none the server answers the current
 * month, and the filters take the period it answered. A branch user reads their own branch; the server sets it.
 * Read a page at a time (`GET /api/reports/missing-cost/page`, owner 2026-11-17): the period, branch and cost filter run
 * on the server, and the figures above are the period's, whatever page is shown.
 */
@Component({
  selector: 'app-backoffice-missing-cost',
  standalone: true,
  imports: [FormsModule, RouterLink, ...PAGE_CHROME, DateInputComponent, ListFooterComponent, StatusComponent, TableStateComponent],
  template: `
    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter">
          <label for="mc-from">{{ i18n.t('missingCost.from') }}</label>
          <app-date-input id="mc-from" [(ngModel)]="from" />
        </div>
        <div class="ui-filter">
          <label for="mc-to">{{ i18n.t('missingCost.to') }}</label>
          <app-date-input id="mc-to" [(ngModel)]="to" />
        </div>
        @if (!lock.locked() && branches().length > 1) {
          <div class="ui-filter">
            <label for="mc-branch">{{ i18n.t('missingCost.branch') }}</label>
            <select id="mc-branch" [(ngModel)]="branchId">
              <option [ngValue]="null">{{ i18n.t('reports.allBranches') }}</option>
              @for (b of branches(); track b.id) {
                <option [ngValue]="b.id">{{ branchName(b) }}</option>
              }
            </select>
          </div>
        }
        <!-- The list's own filter (owner, 2026-11-17): it narrows the products listed, never the figures above. -->
        @if (anyRows()) {
          <div class="ui-filter">
            <label for="mc-f-cost">{{ i18n.t('missingCost.costNow') }}</label>
            <select id="mc-f-cost" [ngModel]="costFilter()" (ngModelChange)="costFilter.set($event)">
              <option ngValue="all">{{ i18n.t('filters.all') }}</option>
              <option ngValue="missing">{{ i18n.t('missingCost.state.missing') }}</option>
              <option ngValue="set">{{ i18n.t('missingCost.state.set') }}</option>
            </select>
          </div>
        }
        <div class="ui-toolbar__end">
          @if (filtered()) {
            <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
          }
          <button type="button" class="btn-primary" [disabled]="busy()" (click)="reload()">{{ i18n.t('missingCost.show') }}</button>
        </div>
      </div>
    </ng-template>

    <div class="bo-stack">
      @if (error() && anyRows()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      @if (report(); as r) {
        <div class="ui-metrics">
          <div class="ui-metric ui-metric--lead">
            <span class="ui-metric__label">{{ i18n.t('missingCost.uncostedHt') }}</span>
            <strong class="ui-metric__value mono">{{ i18n.money(r.uncostedHt) }}</strong>
          </div>
          <div class="ui-metric">
            <span class="ui-metric__label">{{ i18n.t('missingCost.share') }}</span>
            <strong class="ui-metric__value mono">{{ share() }} %</strong>
            <span class="ui-metric__note">{{ i18n.t('missingCost.shareOf', { total: i18n.money(r.salesHt) }) }}</span>
          </div>
          <div class="ui-metric">
            <span class="ui-metric__label">{{ i18n.t('missingCost.sales') }}</span>
            <strong class="ui-metric__value mono">{{ r.uncostedSales }}</strong>
          </div>
          <div class="ui-metric">
            <span class="ui-metric__label">{{ i18n.t('missingCost.productsWithoutCost') }}</span>
            <strong class="ui-metric__value mono">{{ r.productsWithoutCost }}</strong>
          </div>
        </div>
      }
      <section class="ui-surface" [attr.aria-busy]="busy()">
        @if (!pager.loaded() && !error()) {
          <app-table-state kind="loading" />
        } @else if (error() && !anyRows()) {
          <app-table-state kind="error" [message]="error()" (retry)="reload()" />
        } @else if (!anyRows()) {
          <app-table-state kind="empty" [message]="i18n.t('missingCost.empty')" />
        } @else if (!pager.total()) {
          <app-table-state kind="noResults" (clear)="clearFilters()" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('missingCost.item') }}</th>
                  <th class="num">{{ i18n.t('missingCost.qty') }}</th>
                  <th class="num">{{ i18n.t('missingCost.revenueHt') }}</th>
                  <th class="num">{{ i18n.t('missingCost.salesCount') }}</th>
                  <th>{{ i18n.t('missingCost.lastSold') }}</th>
                  <th>{{ i18n.t('missingCost.costNow') }}</th>
                  <th class="num">{{ i18n.t('missingCost.valueAtCostNow') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (x of pager.rows(); track x.productId) {
                  <tr>
                    <td [attr.data-label]="i18n.t('missingCost.item')">
                      <a class="ui-link" [routerLink]="['/backoffice', 'inventory', 'products']" [queryParams]="{ product: x.productId }">{{ productName(x) }}</a>
                      <span class="ui-sub mono" dir="ltr">{{ x.sku }}</span>
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('missingCost.qty')">{{ x.qty }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('missingCost.revenueHt')">{{ i18n.money(x.revenueHt) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('missingCost.salesCount')">{{ x.sales }}</td>
                    <td class="mono" [attr.data-label]="i18n.t('missingCost.lastSold')">{{ i18n.date(x.lastSoldAtUtc) }}</td>
                    <td [attr.data-label]="i18n.t('missingCost.costNow')">
                      <app-status [tone]="tone(x)">{{ i18n.t('missingCost.state.' + state(x)) }}</app-status>
                      @if (x.costNow > 0) {
                        <span class="ui-sub mono">{{ i18n.money(x.costNow) }}</span>
                      }
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('missingCost.valueAtCostNow')">
                      {{ x.valueAtCostNow === null ? '—' : i18n.money(x.valueAtCostNow) }}
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          <p class="notice info">{{ i18n.t('missingCost.estimateNote') }}</p>
        }
      </section>
      <app-list-footer [pager]="pager" [loading]="!pager.loaded()" />
    </div>
  `,
  styleUrls: ['./backoffice-ui.scss', '../../ui/ui-patterns.scss'],
  styles: [
    `
      .ui-surface > .notice {
        margin: var(--sp-3);
      }
      /* The lead figure is money: on a phone it takes the row, so the amount never breaks over two lines. */
      @media (max-width: 640px) {
        .ui-metric--lead {
          grid-column: 1 / -1;
        }
      }
    `,
  ],
})
export class BackofficeMissingCostComponent implements OnInit {
  private readonly admin = inject(AdminService);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);
  readonly lock = inject(BranchLock);

  private readonly pages = inject(FinancePagesApi);

  readonly branches = signal<AdminBranch[]>([]);
  from: string | null = null;
  to: string | null = null;
  branchId: string | null = null;

  /** The list's filter (owner, 2026-11-17): it narrows the products listed, never the figures above. */
  readonly costFilter = signal<'all' | 'missing' | 'set'>('all');
  readonly filtered = computed(() => this.costFilter() !== 'all');

  /** The period and branch last asked for with "Show": the fields above are a draft until then. */
  private readonly applied = signal<{ from: string | null; to: string | null; branchId: string | null }>({
    from: null,
    to: null,
    branchId: null,
  });
  /** Set by "Show" (and at first): the next answer's period goes back into the fields. */
  private syncPeriod = true;

  /**
   * The products a page at a time from the server (owner, 2026-11-17: "paginate in backend, not frontend"); a new
   * period, branch or filter starts at page one. Ten rows to begin with, 20, 50 or 100 at the user's choice.
   */
  readonly pager = createServerPager(
    (page) => this.pages.missingCostPage({ skip: page.skip, take: page.take, ...page.filters }),
    { key: 'reports.missingCost', filters: () => ({ ...this.applied(), cost: this.costFilter() }) },
  );

  /** The period's figures, whatever page or filter is shown. */
  readonly report = computed(() => this.pager.summary());
  /** Whether anything sold without a cost in the period, before the filter. */
  readonly anyRows = computed(() => (this.report()?.all ?? 0) > 0);
  readonly busy = computed(() => this.pager.loading());
  readonly error = computed(() => {
    const e = this.pager.error();
    return e ? adminErrorMessage(e) : null;
  });
  /** Whole percent of the period's sales before tax that carry no cost; 0 when nothing sold. */
  readonly share = computed(() => {
    const r = this.report();
    return r && r.salesHt > 0 ? Math.round((r.uncostedHt / r.salesHt) * 100) : 0;
  });

  constructor() {
    // The period the server answered — the current month on its clock when none was asked.
    effect(() => {
      const r = this.pager.summary();
      if (!r || !this.syncPeriod) return;
      untracked(() => {
        this.from = r.from;
        this.to = r.to;
        this.syncPeriod = false;
      });
    });
  }

  clearFilters(): void {
    this.costFilter.set('all');
  }

  ngOnInit(): void {
    this.chrome?.description.set(this.i18n.t('missingCost.lead'));
    // Sales are taken at branches, never at a warehouse.
    this.admin.listBranches().then((b) => this.branches.set(b.filter((x) => !x.isWarehouse)), () => undefined);
  }

  /** "Show": the period and branch in the fields, from page one; the same ones asked again. */
  async reload(): Promise<void> {
    this.syncPeriod = true;
    const next = { from: this.from, to: this.to, branchId: this.branchId };
    if (JSON.stringify(next) === JSON.stringify(this.applied())) await this.pager.reload();
    else this.applied.set(next);
  }

  state(x: MissingCostRow): 'missing' | 'set' {
    return x.costNow > 0 ? 'set' : 'missing';
  }

  tone(x: MissingCostRow) {
    return toneOf('productCost', this.state(x));
  }

  productName(x: MissingCostRow): string {
    return localizedName(x, this.i18n.lang()) || x.sku;
  }

  branchName(b: AdminBranch): string {
    return localizedName(b, this.i18n.lang());
  }
}
