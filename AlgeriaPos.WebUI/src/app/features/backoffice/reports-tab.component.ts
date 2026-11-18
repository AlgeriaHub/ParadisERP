import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  AdminBranch,
  AdminSaleDetail,
  AdminService,
  SalesSummary,
  adminErrorMessage,
} from '../../core/admin.service';
import { BranchLock } from '../../core/branch-lock';
import { I18nService } from '../../core/i18n.service';
import { MultilingualName, localizedName } from './backoffice-format';
import { PrintService } from '../print/print.service';
import { DateInputComponent } from '../../ui/date-input.component';
import { ListFooterComponent } from '../../ui/list-footer.component';
import { createServerPager } from '../../ui/server-pager';
import { TableStateComponent } from '../../ui/table-state.component';
import { FinancePagesApi } from './finance/finance-pages.api';
import { PAGE_CHROME } from './shell/page-chrome';

type Preset = 'today' | 'yesterday' | 'week' | 'month';

/**
 * What sold, over a period and a branch.
 *
 * <p>The only mark that is not a number is the share bar beside the branch and product totals:
 * one series, one hue, anchored to the inline start, with the figure itself in the next column
 * doing the reading. It is there because "which branch carried the week" is a comparison, and a
 * column of formatted money is the one thing the eye cannot compare at a glance.</p>
 *
 * <p>The sales below the figures are read a page at a time (`GET /api/sales/page`, owner 2026-11-17): the period,
 * branch, state and cashier run on the server over every sale of the period.</p>
 */
@Component({
  selector: 'app-backoffice-reports',
  standalone: true,
  imports: [FormsModule, DateInputComponent, ListFooterComponent, TableStateComponent, ...PAGE_CHROME],
  template: `
    <ng-template appPageActions>
      <button type="button" class="btn-ghost" (click)="print()">{{ i18n.t('common.print') }}</button>
      <button type="button" class="btn-ghost" [disabled]="busy() || salesPager.loading()" (click)="refresh()">
        {{ busy() || salesPager.loading() ? i18n.t('common.loading') : i18n.t('subscription.refresh') }}
      </button>
    </ng-template>

    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter">
          <span class="ui-filter__label" id="rp-preset-label">{{ i18n.t('reports.period') }}</span>
          <div class="bo-chips" role="group" aria-labelledby="rp-preset-label">
            @for (p of presets; track p.key) {
              <button
                type="button"
                [class.is-on]="preset() === p.key"
                [attr.aria-pressed]="preset() === p.key"
                (click)="applyPreset(p.key)"
              >
                {{ i18n.t(p.label) }}
              </button>
            }
          </div>
        </div>
        <div class="ui-filter">
          <label for="rp-from">{{ i18n.t('reports.from') }}</label>
          <app-date-input id="rp-from" [ngModel]="fromDate()" (ngModelChange)="setFrom($event)" />
        </div>
        <div class="ui-filter">
          <label for="rp-to">{{ i18n.t('reports.to') }}</label>
          <app-date-input id="rp-to" [ngModel]="toDate()" (ngModelChange)="setTo($event)" />
        </div>
        @if (lock.locked()) {
          <p class="bo-scope-note" id="rp-scope">{{ i18n.t('branchLock.listNote', { branch: lock.ownBranchName(branches()) }) }}</p>
        } @else {
          <div class="ui-filter">
            <label for="rp-branch">{{ i18n.t('backoffice.tabs.branches') }}</label>
            <select id="rp-branch" [ngModel]="branchId()" (ngModelChange)="setBranch($event)">
              <option [ngValue]="null">{{ i18n.t('reports.allBranches') }}</option>
              @for (b of branches(); track b.id) {
                <option [ngValue]="b.id">{{ name(b) }}</option>
              }
            </select>
          </div>
        }
      </div>
    </ng-template>

    <div class="bo-stack">
      @if (error() || salesError()) {
        <p class="notice danger" role="alert">{{ error() || salesError() }}</p>
      }

      @if (busy() && !summary()) {
        <section class="bo-panel" aria-busy="true">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('backoffice.tabs.reports') }}</h2>
          </div>
          <p class="bo-loading" role="status">
            <span class="bo-loading__word">{{ i18n.t('common.loading') }}</span>
            <span class="bo-loading__rows" aria-hidden="true"></span>
          </p>
        </section>
      }

      @if (summary(); as s) {
        <div id="report-print" class="bo-stack">
          <div class="bo-metrics">
            <div class="bo-metric bo-metric--lead">
              <span class="bo-metric__label">{{ i18n.t('reports.grossTtc') }}</span>
              <strong class="bo-metric__value mono">{{ i18n.money(s.grossTtc) }}</strong>
              <span class="bo-metric__note"><span>{{ i18n.t('reports.saleCount') }}</span><span class="mono">{{ s.saleCount }}</span></span>
            </div>
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('reports.netHt') }}</span>
              <strong class="bo-metric__value mono">{{ i18n.money(s.netHt) }}</strong>
            </div>
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.taxName() }}</span>
              <strong class="bo-metric__value mono">{{ i18n.money(s.taxTotal) }}</strong>
            </div>
            <div class="bo-metric" [class.bo-metric--alert]="s.refundTotal > 0">
              <span class="bo-metric__label">{{ i18n.t('reports.refundTotal') }}</span>
              <strong class="bo-metric__value mono">{{ i18n.money(s.refundTotal) }}</strong>
              <span class="bo-metric__note"><span>{{ i18n.t('reports.refundCount') }}</span><span class="mono">{{ s.refundCount }}</span></span>
            </div>
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('reports.discountTotal') }}</span>
              <strong class="bo-metric__value mono">{{ i18n.money(s.discountTotal) }}</strong>
              <span class="bo-metric__note">{{ i18n.t('reports.discountTotal.note') }}</span>
            </div>
            @if (s.giftCardTotal) {
              <div class="bo-metric">
                <span class="bo-metric__label">{{ i18n.t('reports.giftCards') }}</span>
                <strong class="bo-metric__value mono">{{ i18n.money(s.giftCardTotal) }}</strong>
              </div>
            }
            @if (s.loyaltyTotal) {
              <div class="bo-metric">
                <span class="bo-metric__label">{{ i18n.t('reports.loyalty') }}</span>
                <strong class="bo-metric__value mono">{{ i18n.money(s.loyaltyTotal) }}</strong>
              </div>
            }
            @if (s.roundingTotal) {
              <div class="bo-metric">
                <span class="bo-metric__label">{{ i18n.t('reports.rounding') }}</span>
                <strong class="bo-metric__value mono">{{ i18n.money(s.roundingTotal) }}</strong>
              </div>
            }
          </div>

          <div class="bo-split">
            <section class="bo-panel">
              <div class="bo-panel__head">
                <h2 class="bo-panel__title">{{ i18n.t('reports.taxBreakdown') }}</h2>
              </div>
              <div class="bo-scroll">
                <table class="bo-data">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('backoffice.products.taxRate') }}</th>
                      <th class="num">{{ i18n.t('receipt.taxBase') }}</th>
                      <th class="num">{{ i18n.taxName() }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (t of s.taxBreakdown; track t.rate) {
                      <tr>
                        <td class="mono" [attr.data-label]="i18n.t('backoffice.products.taxRate')">
                          <span>{{ t.rate }}%</span>
                          @if (i18n.rateLabel(t.rate)) {
                            <span class="bo-soft">{{ i18n.rateLabel(t.rate) }}</span>
                          }
                        </td>
                        <td class="num mono" [attr.data-label]="i18n.t('receipt.taxBase')">{{ i18n.money(t.baseHt) }}</td>
                        <td class="num mono" [attr.data-label]="i18n.taxName()">{{ i18n.money(t.taxAmount) }}</td>
                      </tr>
                    } @empty {
                      <tr>
                        <td colspan="3" class="bo-empty">
                          @if (busy()) {
                            <strong>{{ i18n.t('common.loading') }}</strong>
                            <span class="bo-loading__rows" aria-hidden="true"></span>
                          } @else {
                            <strong>{{ i18n.t('sales.none') }}</strong>
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            </section>

            @if (s.branchBreakdown.length > 0) {
              <section class="bo-panel">
                <div class="bo-panel__head">
                  <h2 class="bo-panel__title">{{ i18n.t('backoffice.tabs.branches') }}</h2>
                </div>
                <div class="bo-scroll">
                  <table class="bo-data">
                    <thead>
                      <tr>
                        <th>{{ i18n.t('backoffice.tabs.branches') }}</th>
                        <th class="bar-col">{{ i18n.t('reports.share') }}</th>
                        <th class="num">{{ i18n.t('reports.saleCount') }}</th>
                        <th class="num">{{ i18n.t('reports.grossTtc') }}</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (b of s.branchBreakdown; track b.branchId) {
                        <tr>
                          <td [attr.data-label]="i18n.t('backoffice.tabs.branches')">{{ b.branchName }}</td>
                          <td class="bar-col" [attr.data-label]="i18n.t('reports.share')">
                            <span class="bo-bar" aria-hidden="true">
                              <span [style.inline-size.%]="share(b.grossTtc, branchTop())"></span>
                            </span>
                          </td>
                          <td class="num mono" [attr.data-label]="i18n.t('reports.saleCount')">{{ b.saleCount }}</td>
                          <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('reports.grossTtc')">
                            {{ i18n.money(b.grossTtc) }}
                          </td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
              </section>
            }
          </div>

          <section class="bo-panel">
            <div class="bo-panel__head">
              <h2 class="bo-panel__title">{{ i18n.t('reports.topProducts') }}</h2>
            </div>
            <div class="bo-scroll">
              <table class="bo-data">
                <thead>
                  <tr>
                    <th>{{ i18n.t('receipt.item') }}</th>
                    <th class="bar-col">{{ i18n.t('reports.share') }}</th>
                    <th class="num">{{ i18n.t('register.qty') }}</th>
                    <th class="num">{{ i18n.t('reports.grossTtc') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (p of s.topProducts; track p.productId) {
                    <tr>
                      <td [attr.data-label]="i18n.t('receipt.item')">{{ p.name }}</td>
                      <td class="bar-col" [attr.data-label]="i18n.t('reports.share')">
                        <span class="bo-bar" aria-hidden="true">
                          <span [style.inline-size.%]="share(p.revenue, productTop())"></span>
                        </span>
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('register.qty')">{{ p.qty }}</td>
                      <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('reports.grossTtc')">
                        {{ i18n.money(p.revenue) }}
                      </td>
                    </tr>
                  } @empty {
                    <tr>
                      <td colspan="4" class="bo-empty">
                        @if (busy()) {
                          <strong>{{ i18n.t('common.loading') }}</strong>
                          <span class="bo-loading__rows" aria-hidden="true"></span>
                        } @else {
                          <strong>{{ i18n.t('reports.emptyTitle') }}</strong>
                          {{ i18n.t('reports.emptyHint') }}
                        }
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          </section>
        </div>
      }

      <!-- The low-stock list moved to Inventory › Low stock (REDESIGN_PLAN.md §3.2): a stock
           question, asked on the stock pages. -->

      <section class="bo-panel no-print">
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ i18n.t('nav.sales') }}</h2>
        </div>
        <!-- The list's own filters (owner, 2026-11-17): they narrow the sales below, never the figures above,
             which the period and branch in the page toolbar set. -->
        @if (anySales()) {
          <div class="bo-panel__body">
            <div class="ui-toolbar" role="search">
              <div class="ui-filter">
                <label for="rp-f-status">{{ i18n.t('common.state') }}</label>
                <select id="rp-f-status" [ngModel]="saleStatus()" (ngModelChange)="saleStatus.set($event)">
                  <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                  <option ngValue="completed">{{ i18n.t('sales.completed') }}</option>
                  <option ngValue="refunded">{{ i18n.t('sales.refunded') }}</option>
                </select>
              </div>
              <div class="ui-filter">
                <label for="rp-f-cashier">{{ i18n.t('receipt.cashier') }}</label>
                <select id="rp-f-cashier" [ngModel]="saleCashier()" (ngModelChange)="saleCashier.set($event)">
                  <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                  @for (who of cashiers(); track who) {
                    <option [ngValue]="who">{{ who }}</option>
                  }
                </select>
              </div>
              <div class="ui-filter">
                <label for="rp-f-tax">{{ i18n.taxName() }}</label>
                <select id="rp-f-tax" [ngModel]="saleTax()" (ngModelChange)="saleTax.set($event)">
                  <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                  <option ngValue="with">{{ i18n.t('reports.filter.withTax', { tax: i18n.taxName() }) }}</option>
                  <option ngValue="without">{{ i18n.t('reports.filter.withoutTax', { tax: i18n.taxName() }) }}</option>
                </select>
              </div>
              @if (salesFiltered()) {
                <div class="ui-toolbar__end">
                  <button type="button" class="btn-ghost" (click)="clearSaleFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
                </div>
              }
            </div>
          </div>
        }
        <div class="bo-scroll bo-scroll--tall">
          <table class="bo-data">
            <thead>
              <tr>
                <th>{{ i18n.t('receipt.no') }}</th>
                <th>{{ i18n.t('receipt.date') }}</th>
                <th>{{ i18n.t('backoffice.devices.branch') }}</th>
                <th>{{ i18n.t('receipt.cashier') }}</th>
                <th>{{ i18n.t('common.state') }}</th>
                <th class="num">{{ i18n.t('register.total') }}</th>
                <th>{{ i18n.t('common.actions') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (sale of salesPager.rows(); track sale.id) {
                <tr>
                  <td class="mono" [attr.data-label]="i18n.t('receipt.no')">{{ sale.reference }}</td>
                  <td [attr.data-label]="i18n.t('receipt.date')">{{ i18n.dateTime(sale.soldAtUtc) }}</td>
                  <td [attr.data-label]="i18n.t('backoffice.devices.branch')">{{ branchName(sale.branchId) }}</td>
                  <td [attr.data-label]="i18n.t('receipt.cashier')">{{ sale.cashierName }}</td>
                  <td [attr.data-label]="i18n.t('common.state')">
                    <span class="bo-state" [class]="sale.status === 'Refunded' ? 'bo-state--warn' : 'bo-state--ok'">
                      {{ i18n.t(sale.status === 'Refunded' ? 'sales.refunded' : 'sales.completed') }}
                    </span>
                  </td>
                  <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('register.total')">
                    {{ i18n.money(sale.grandTotalTtc) }}
                  </td>
                  <td class="bo-cell-actions" [attr.data-label]="i18n.t('common.actions')">
                    <button type="button" class="btn-ghost bo-small" (click)="openDetail(sale.id)">{{ i18n.t('common.view') }}</button>
                  </td>
                </tr>
              } @empty {
                <tr>
                  <td colspan="7" class="bo-empty">
                    @if (!salesPager.loaded() || salesPager.loading()) {
                      <strong>{{ i18n.t('common.loading') }}</strong>
                      <span class="bo-loading__rows" aria-hidden="true"></span>
                    } @else if (anySales() && salesFiltered()) {
                      <app-table-state kind="noResults" (clear)="clearSaleFilters()" />
                    } @else {
                      <strong>{{ i18n.t('reports.emptyTitle') }}</strong>
                      {{ i18n.t('reports.emptyHint') }}
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      </section>
      <app-list-footer [pager]="salesPager" [loading]="!salesPager.loaded()" />
    </div>

    @if (detail(); as d) {
      <div class="bo-backdrop no-print" (click)="detail.set(null)">
        <div class="bo-modal" role="dialog" aria-modal="true" (click)="$event.stopPropagation()">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title mono">{{ d.reference }}</h2>
            <p class="bo-meta">
              <span>{{ i18n.dateTime(d.soldAtUtc) }}</span>
              <span>{{ d.cashierName }}</span>
              <span>{{ branchName(d.branchId) }}</span>
            </p>
          </div>
          <div class="bo-scroll">
            <table class="bo-data">
              <thead>
                <tr>
                  <th>{{ i18n.t('receipt.item') }}</th>
                  <th class="num">{{ i18n.t('register.qty') }}</th>
                  <th class="num">{{ i18n.t('backoffice.products.price') }}</th>
                  <th class="num">{{ i18n.t('register.total') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (line of d.lines; track line.id) {
                  <tr>
                    <td [attr.data-label]="i18n.t('receipt.item')">{{ line.nameSnapshot }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('register.qty')">{{ line.qty }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('backoffice.products.price')">{{ i18n.money(line.unitPrice) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('register.total')">{{ i18n.money(line.lineTotalTtc) }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          <div class="totals">
            <div><span>{{ i18n.t('register.subtotal') }}</span><b class="mono">{{ i18n.money(d.subTotalHt) }}</b></div>
            <div><span>{{ i18n.taxName() }}</span><b class="mono">{{ i18n.money(d.taxTotal) }}</b></div>
            <div class="grand"><span>{{ i18n.t('register.total') }}</span><b class="mono">{{ i18n.money(d.grandTotalTtc) }}</b></div>
          </div>
          <div class="bo-panel__foot">
            <button type="button" class="btn-ghost" (click)="detail.set(null)">{{ i18n.t('common.close') }}</button>
          </div>
        </div>
      </div>
    }
  `,
  styleUrls: ['./backoffice-ui.scss', '../../ui/ui-patterns.scss'],
  styles: [
    `
      /* Out of stock is written as the figure zero in the danger hue, beside a reorder level
         it is plainly under — the comparison is the message, the colour only finds the row. */
      .totals {
        display: grid;
        gap: 0.2rem;
        padding: 0.85rem 1rem;
      }
      .totals div {
        display: flex;
        justify-content: space-between;
        gap: 1rem;
        font-size: 0.9rem;
      }
      .totals .grand {
        font-size: 1.1rem;
        font-weight: 700;
        border-top: 1px solid var(--border);
        padding-block-start: 0.35rem;
        margin-block-start: 0.15rem;
      }
    `,
  ],
})
export class BackofficeReportsComponent implements OnInit {
  private readonly admin = inject(AdminService);
  readonly i18n = inject(I18nService);
  readonly lock = inject(BranchLock);
  private readonly printer = inject(PrintService);

  readonly branches = signal<AdminBranch[]>([]);
  private readonly pages = inject(FinancePagesApi);
  readonly summary = signal<SalesSummary | null>(null);
  readonly detail = signal<AdminSaleDetail | null>(null);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  /** The quick periods, in the order a manager asks for them. */
  readonly presets: readonly { key: Preset; label: string }[] = [
    { key: 'today', label: 'sales.today' },
    { key: 'yesterday', label: 'reports.yesterday' },
    { key: 'week', label: 'reports.thisWeek' },
    { key: 'month', label: 'reports.thisMonth' },
  ];
  readonly preset = signal<Preset>('today');

  private readonly from = signal<Date>(startOfDay(new Date()));
  private readonly to = signal<Date>(endOfDay(new Date()));
  readonly branchId = signal<string | null>(null);

  readonly fromDate = computed(() => toInputDate(this.from()));
  readonly toDate = computed(() => toInputDate(this.to()));

  // The sales list's filters (owner, 2026-11-17): applied by the server over every sale of the period;
  // the period and branch above are the list's too.
  readonly saleStatus = signal<'all' | 'completed' | 'refunded'>('all');
  /** A cashier's name as the column shows it; `all` is anyone. */
  readonly saleCashier = signal<string>('all');
  /** With or without tax (owner, 2026-11-18): a sale is taxed only when the seller ticked it. */
  readonly saleTax = signal<'all' | 'with' | 'without'>('all');
  /** Whether the branch is known: a locked user's is read before the first page is asked. */
  private readonly started = signal(false);

  readonly salesFiltered = computed(() => this.saleStatus() !== 'all' || this.saleCashier() !== 'all' || this.saleTax() !== 'all');

  /**
   * The period's sales a page at a time from the server (owner, 2026-11-17: "paginate in backend, not frontend"), newest
   * first; ten rows to begin with, a new period, branch or filter starts at page one.
   */
  readonly salesPager = createServerPager(
    (page) => this.pages.salesPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'reports.sales',
      filters: () => ({
        from: this.from().toISOString(),
        to: this.to().toISOString(),
        branchId: this.branchId(),
        status: this.saleStatus(),
        cashier: this.saleCashier(),
        tax: this.saleTax(),
      }),
      enabled: () => this.started(),
    },
  );

  /** The cashiers of the period, so the filter offers only who sold. */
  readonly cashiers = computed(() => [...(this.salesPager.summary()?.cashiers ?? [])].sort((a, b) => a.localeCompare(b)));
  /** Whether the period has any sale at all, before the list's filters. */
  readonly anySales = computed(() => (this.salesPager.summary()?.all ?? 0) > 0);
  readonly salesError = computed(() => {
    const e = this.salesPager.error();
    return e ? adminErrorMessage(e) : null;
  });

  clearSaleFilters(): void {
    this.saleStatus.set('all');
    this.saleCashier.set('all');
    this.saleTax.set('all');
  }

  /** The bars are read against the largest row, not the total: the comparison is row to row. */
  readonly branchTop = computed(() => Math.max(0, ...(this.summary()?.branchBreakdown ?? []).map((b) => b.grossTtc)));
  readonly productTop = computed(() => Math.max(0, ...(this.summary()?.topProducts ?? []).map((p) => p.revenue)));

  private readonly branchNames = computed(
    () => new Map(this.branches().map((b) => [b.id, localizedName(b, this.i18n.lang())])),
  );

  /**
   * A record named in the reader's language. Partial on purpose: a record served with the French
   * name only falls through to it.
   */
  name(record: Partial<MultilingualName>): string {
    return localizedName(record, this.i18n.lang());
  }

  async ngOnInit(): Promise<void> {
    try {
      this.branches.set(await this.admin.listBranches());
      // A non-admin's figures are their branch's; the server answers that whatever is asked.
      this.branchId.set(this.lock.lockedBranchId());
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    }
    this.started.set(true);
    await this.run();
  }

  branchName(id: string): string {
    return this.branchNames().get(id) ?? '—';
  }

  share(value: number, top: number): number {
    if (!(top > 0) || !(value > 0)) return 0;
    return Math.round((value / top) * 100);
  }

  applyPreset(preset: Preset): void {
    this.preset.set(preset);
    const now = new Date();
    if (preset === 'today') {
      this.from.set(startOfDay(now));
      this.to.set(endOfDay(now));
    } else if (preset === 'yesterday') {
      const y = new Date(now);
      y.setDate(y.getDate() - 1);
      this.from.set(startOfDay(y));
      this.to.set(endOfDay(y));
    } else if (preset === 'week') {
      const start = new Date(now);
      const day = (start.getDay() + 6) % 7; // Monday-based week
      start.setDate(start.getDate() - day);
      this.from.set(startOfDay(start));
      this.to.set(endOfDay(now));
    } else {
      const start = new Date(now.getFullYear(), now.getMonth(), 1);
      this.from.set(startOfDay(start));
      this.to.set(endOfDay(now));
    }
    void this.run();
  }

  setFrom(value: string): void {
    this.preset.set('today');
    this.from.set(startOfDay(parseInputDate(value)));
    void this.run();
  }

  setTo(value: string): void {
    this.preset.set('today');
    this.to.set(endOfDay(parseInputDate(value)));
    void this.run();
  }

  setBranch(value: string | null): void {
    this.branchId.set(value || null);
    void this.run();
  }

  /** The figures for the period and branch; the sales list follows them through its pager. */
  async run(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      this.summary.set(await this.admin.getSalesSummary(this.from().toISOString(), this.to().toISOString(), this.branchId()));
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  /** Refresh: the figures and the page of sales on screen, asked again. */
  async refresh(): Promise<void> {
    await Promise.all([this.run(), this.salesPager.reload()]);
  }

  async openDetail(id: string): Promise<void> {
    this.error.set(null);
    try {
      this.detail.set(await this.admin.getSale(id));
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    }
  }

  /**
   * The report on paper, in its own tab (print-reports.ts), with this window and branch. It used
   * to print this screen, sidebar and filters included, without saying which period it covered.
   */
  print(): void {
    this.printer.open('report', 'sales', {
      lang: this.i18n.lang(),
      from: this.from().toISOString(),
      to: this.to().toISOString(),
      branch: this.branchId(),
    });
  }
}

function startOfDay(date: Date): Date {
  const d = new Date(date);
  d.setHours(0, 0, 0, 0);
  return d;
}

function endOfDay(date: Date): Date {
  const d = new Date(date);
  d.setHours(23, 59, 59, 999);
  return d;
}

function toInputDate(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

/** Parses a "yyyy-MM-dd" <input type="date"> value as a local date, not UTC midnight —
 * `new Date(value)` would shift the day in any timezone ahead of UTC. */
function parseInputDate(value: string): Date {
  const [year, month, day] = value.split('-').map(Number);
  return new Date(year, month - 1, day);
}
