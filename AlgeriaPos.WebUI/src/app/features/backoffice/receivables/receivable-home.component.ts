import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminCustomer, adminErrorMessage } from '../../../core/admin.service';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService } from '../../../core/regional.service';
import { midday, today } from './receivable-view';
import { AgeingPageSummary, ReceivablePagesApi } from './receivable-pages.api';
import { CustomerAgeing } from './receivables-api.service';
import { PrintService } from '../../print/print.service';
import { DateInputComponent } from '../../../ui/date-input.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { formatNumber } from '../../../core/number-format';

/** The four late buckets, in the order they age; `current` is owed but not late and stays out of this list. */
type LateBucket = 'days1To30' | 'days31To60' | 'days61To90' | 'over90';
const LATE_BUCKETS: readonly LateBucket[] = ['days1To30', 'days31To60', 'days61To90', 'over90'];

/** The translation key for each bucket column, spelled as the server spells the enum. */
const BUCKET_KEY: Record<'current' | LateBucket, string> = {
  current: 'inv.bucket.Current',
  days1To30: 'inv.bucket.Days1To30',
  days31To60: 'inv.bucket.Days31To60',
  days61To90: 'inv.bucket.Days61To90',
  over90: 'inv.bucket.Over90',
};

/**
 * The front door of the debtor book: **who owes me, how much of it is late, who is the latest,
 * and how much of what I hold is only paper I cannot yet trust.**
 *
 * <p>The ageing report is the customer index here, not a report tucked behind a chip, because
 * lateness is the question the owner opens this section for. The server sorts by name; the
 * board re-sorts by the oldest overdue day, then by the amount, so the customer to call first is
 * the first row.</p>
 *
 * <p>Two things the buckets cannot see are stated beside them rather than folded in. Debt no
 * invoice covers has no due date, so it has no age: it gets its own column and its own tile.
 * Pending receipt vouchers post nothing, so they are not in any figure the server returns: the
 * "paper in hand" tile sums them from a second call, and says so when that call fails instead
 * of showing a zero that would read as "nothing pending".</p>
 */
@Component({
  selector: 'app-receivable-home',
  standalone: true,
  imports: [FormsModule, DateInputComponent, ListFooterComponent],
  template: `
    <div class="bo-stack">
      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }

      <div class="bo-metrics">
        <div class="bo-metric bo-metric--lead">
          <span class="bo-metric__label">{{ i18n.t('inv.home.total') }}</span>
          <strong class="bo-metric__value mono">{{ figure(ageing()?.total) }}</strong>
        </div>
        <div class="bo-metric" [class.bo-metric--alert]="late() > 0">
          <span class="bo-metric__label">{{ i18n.t('inv.home.late') }}</span>
          <strong class="bo-metric__value mono">{{ ageing() ? i18n.money(late()) : '—' }}</strong>
          <span class="bo-metric__note"><span>{{ i18n.t('inv.home.lateNote') }}</span></span>
        </div>
        <div class="bo-metric" [class.bo-metric--alert]="(ageing()?.over90 ?? 0) > 0">
          <span class="bo-metric__label">{{ i18n.t('inv.bucket.Over90') }}</span>
          <strong class="bo-metric__value mono">{{ figure(ageing()?.over90) }}</strong>
        </div>
        <div class="bo-metric">
          <span class="bo-metric__label">{{ i18n.t('inv.home.paperInHand') }}</span>
          <strong class="bo-metric__value mono">{{ figure(paper()) }}</strong>
          <span class="bo-metric__note">
            @if (paperFailed()) {
              <span>{{ i18n.t('inv.paperUnknown') }}</span>
            } @else {
              <span>{{ i18n.t('inv.home.paperInHandNote') }}</span>
            }
          </span>
        </div>
      </div>

      @if (ageing(); as a) {
        @if (a.creditBalance > 0 || a.uninvoiced !== 0 || a.customerCount > 0) {
          <div class="bo-metrics">
            @if (a.creditBalance > 0) {
              <div class="bo-metric">
                <span class="bo-metric__label">{{ i18n.t('inv.home.creditBalance') }}</span>
                <strong class="bo-metric__value mono">{{ i18n.money(a.creditBalance) }}</strong>
                <span class="bo-metric__note"><span>{{ i18n.t('inv.home.creditBalanceNote') }}</span></span>
              </div>
            }
            @if (a.uninvoiced !== 0) {
              <div class="bo-metric">
                <span class="bo-metric__label">{{ i18n.t('inv.home.uninvoiced') }}</span>
                <strong class="bo-metric__value mono">{{ i18n.money(a.uninvoiced) }}</strong>
                <span class="bo-metric__note"><span>{{ i18n.t('inv.home.uninvoicedNote') }}</span></span>
              </div>
            }
            @if (a.customerCount > 0) {
              <div class="bo-metric">
                <span class="bo-metric__label">{{ i18n.t('backoffice.tabs.customers') }}</span>
                <strong class="bo-metric__value bo-metric__value--word">
                  {{ i18n.t('inv.home.customers', { n: a.customerCount }) }}
                </strong>
              </div>
            }
          </div>
        }
      }

      <section class="bo-panel">
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ i18n.t('inv.home.tableTitle') }}</h2>
          <div class="bo-actions no-print">
            <button type="button" class="btn-primary bo-small" (click)="newInvoice.emit()">
              {{ i18n.t('inv.acct.newInvoice') }}
            </button>
            <button type="button" class="btn-ghost bo-small" (click)="newReceipt.emit()">
              {{ i18n.t('inv.acct.newReceipt') }}
            </button>
            <button type="button" class="btn-ghost bo-small" (click)="print()">{{ i18n.t('common.print') }}</button>
          </div>
          <p class="bo-panel__note">{{ i18n.t('inv.home.note') }} {{ i18n.t('inv.home.limitHint') }}</p>
        </div>

        <div class="bo-panel__body no-print">
          <div class="bo-fields">
            <div class="bo-field">
              <label for="ag-asof">{{ i18n.t('inv.home.asOf') }}</label>
              <app-date-input id="ag-asof" [ngModel]="asOf()" (ngModelChange)="setAsOf($event)" />
            </div>
            <div class="bo-field bo-field--wide">
              <label for="ag-find">{{ i18n.t('inv.find') }}</label>
              <input
                id="ag-find"
                type="search"
                class="finder"
                [placeholder]="i18n.t('inv.find')"
                [ngModel]="query()"
                (ngModelChange)="query.set($event)"
              />
            </div>
          </div>
        </div>

        <div class="bo-scroll bo-scroll--tall">
          <table class="bo-data" [attr.aria-label]="i18n.t('inv.home.tableTitle')">
            <thead>
              <tr>
                <th>{{ i18n.t('common.customer') }}</th>
                <th class="num">{{ i18n.t('common.total') }}</th>
                <th class="num">{{ i18n.t('inv.bucket.Current') }}</th>
                <th class="num">{{ i18n.t('inv.bucket.Days1To30') }}</th>
                <th class="num">{{ i18n.t('inv.bucket.Days31To60') }}</th>
                <th class="num">{{ i18n.t('inv.bucket.Days61To90') }}</th>
                <th class="num">{{ i18n.t('inv.bucket.Over90') }}</th>
                <th>{{ i18n.t('inv.home.oldest') }}</th>
                <th class="num">{{ i18n.t('inv.home.uninvoiced') }}</th>
                <th>{{ i18n.t('backoffice.customers.creditLimit') }}</th>
                <th>{{ i18n.t('inv.home.lateShare') }}</th>
                <th class="no-print">{{ i18n.t('common.actions') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (row of pager.rows(); track row.customerId) {
                <tr>
                  <td [attr.data-label]="i18n.t('common.customer')">
                    <span>{{ row.customerName }}</span>
                    <span class="bo-soft mono" dir="ltr">{{ row.customerCode }}</span>
                  </td>
                  <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('common.total')">
                    <span>{{ i18n.money(row.total) }}</span>
                    @if (row.creditBalance > 0) {
                      <span class="bo-state bo-state--lead">{{ i18n.t('inv.companyOwes') }}</span>
                      <span class="bo-soft mono">{{ i18n.money(row.creditBalance) }}</span>
                    }
                  </td>
                  <td class="num mono" [attr.data-label]="i18n.t('inv.bucket.Current')">{{ cell(row.current) }}</td>
                  @for (bucket of buckets; track bucket) {
                    <td
                      class="num mono"
                      [class.bo-cell-strong]="strongest(row) === bucket"
                      [attr.data-label]="i18n.t(bucketKey[bucket])"
                    >{{ cell(row[bucket]) }}</td>
                  }
                  <td [attr.data-label]="i18n.t('inv.home.oldest')">
                    @if (row.oldestDaysOverdue > 0) {
                      <span class="bo-state bo-state--danger">{{ i18n.t('inv.days', { n: row.oldestDaysOverdue }) }}</span>
                    } @else {
                      <span class="bo-state">{{ i18n.t('inv.notLate') }}</span>
                    }
                  </td>
                  <td class="num mono" [attr.data-label]="i18n.t('inv.home.uninvoiced')">{{ cell(row.uninvoiced) }}</td>
                  <td [attr.data-label]="i18n.t('backoffice.customers.creditLimit')">
                    @if (row.creditLimit > 0) {
                      <span class="bo-state" [class]="row.overCreditLimit ? 'bo-state--danger' : 'bo-state--ok'">
                        {{ i18n.t(row.overCreditLimit ? 'inv.limit.over' : 'inv.limit.within') }}
                      </span>
                      <span class="bo-soft mono">{{ i18n.money(row.creditLimit) }}</span>
                    } @else {
                      <span class="bo-state">{{ i18n.t('inv.limit.none') }}</span>
                    }
                  </td>
                  <td class="bar-col" [attr.data-label]="i18n.t('inv.home.lateShare')">
                    <span class="bo-bar" [class.bo-bar--loss]="row.over90 > 0" aria-hidden="true">
                      <span [style.inline-size.%]="lateShare(row)"></span>
                    </span>
                    <span class="bo-soft mono">{{ percent(lateShare(row)) }}</span>
                  </td>
                  <td class="bo-cell-actions no-print" [attr.data-label]="i18n.t('common.actions')">
                    <button type="button" class="btn-ghost bo-small" (click)="openAccount.emit(row.customerId)">
                      {{ i18n.t('inv.openAccount') }}
                    </button>
                  </td>
                </tr>
              } @empty {
                <tr>
                  <td colspan="12" class="bo-empty">
                    @if (busy()) {
                      <strong>{{ i18n.t('common.loading') }}</strong>
                      <span class="bo-loading__rows" aria-hidden="true"></span>
                    } @else {
                      <strong>{{ i18n.t('inv.home.none') }}</strong>
                      <span>{{ i18n.t('inv.home.noneHint') }}</span>
                    }
                  </td>
                </tr>
              }
            </tbody>
            @if (ageing(); as a) {
              <tfoot>
                <tr>
                  <td [attr.data-label]="i18n.t('common.customer')">{{ i18n.t('common.total') }}</td>
                  <td class="num mono" [attr.data-label]="i18n.t('common.total')">{{ i18n.money(a.total) }}</td>
                  <td class="num mono" [attr.data-label]="i18n.t('inv.bucket.Current')">{{ i18n.money(a.current) }}</td>
                  <td class="num mono" [attr.data-label]="i18n.t('inv.bucket.Days1To30')">{{ i18n.money(a.days1To30) }}</td>
                  <td class="num mono" [attr.data-label]="i18n.t('inv.bucket.Days31To60')">{{ i18n.money(a.days31To60) }}</td>
                  <td class="num mono" [attr.data-label]="i18n.t('inv.bucket.Days61To90')">{{ i18n.money(a.days61To90) }}</td>
                  <td class="num mono" [attr.data-label]="i18n.t('inv.bucket.Over90')">{{ i18n.money(a.over90) }}</td>
                  <td></td>
                  <td class="num mono" [attr.data-label]="i18n.t('inv.home.uninvoiced')">{{ i18n.money(a.uninvoiced) }}</td>
                  <td></td>
                  <td></td>
                  <td class="no-print"></td>
                </tr>
              </tfoot>
            }
          </table>
        </div>
        <app-list-footer [pager]="pager" [loading]="busy()" />

        <div class="bo-panel__body no-print">
          <div class="bo-fields">
            <div class="bo-field bo-field--wide">
              <label for="ag-other">{{ i18n.t('inv.home.other') }}</label>
              <select id="ag-other" [ngModel]="otherId()" (ngModelChange)="otherId.set($event)">
                <option [ngValue]="null">—</option>
                @for (c of others(); track c.id) {
                  <option [ngValue]="c.id">{{ c.fullName }}</option>
                }
              </select>
              <span class="bo-hint">{{ i18n.t('inv.home.otherHint') }}</span>
            </div>
          </div>
          <div class="bo-actions bo-actions--start">
            <button type="button" class="btn-ghost bo-small" [disabled]="!otherId()" (click)="openOther()">
              {{ i18n.t('inv.openAccount') }}
            </button>
          </div>
        </div>
      </section>
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss'],
})
export class ReceivableHomeComponent implements OnInit {
  private readonly pages = inject(ReceivablePagesApi);
  private readonly regional = inject(RegionalService);
  private readonly printer = inject(PrintService);
  readonly i18n = inject(I18nService);

  readonly customers = input<AdminCustomer[]>([]);

  readonly openAccount = output<string>();
  readonly newInvoice = output<void>();
  readonly newReceipt = output<void>();

  readonly buckets = LATE_BUCKETS;
  readonly bucketKey = BUCKET_KEY;

  /** Σ pending receipt vouchers — paper that posts nothing and so appears in no ageing figure. */
  readonly paper = signal<number | null>(null);
  readonly paperFailed = signal(false);
  readonly asOf = signal<string>(today(this.regional.settings().timeZone));
  readonly query = signal('');
  readonly otherId = signal<string | null>(null);

  /**
   * The board a page at a time from the server (owner, 2026-11-17: "paginate in backend, not
   * frontend"): ten customers to begin with. The server builds the ageing as of the day, searches
   * it, and sorts it latest first, then largest — its alphabetical order answers a different
   * question. Another day or search starts at page one.
   */
  readonly pager = createServerPager(
    (page) => this.pages.ageingPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'receivables.ageing',
      filters: () => {
        const tz = this.regional.settings().timeZone;
        // Today is the server's own default; sending it would only pin the report to this browser's idea of noon.
        return { asOf: this.asOf() === today(tz) ? null : midday(this.asOf()), q: this.query().trim() };
      },
    },
  );

  /**
   * The whole book's figures — the tiles and the total line under the table — whatever the search:
   * paged only, so no filter narrows what the total adds up.
   */
  readonly ageing = computed<AgeingPageSummary | null>(() => this.pager.summary());
  readonly busy = computed(() => !this.pager.loaded() && !this.pager.error());
  readonly error = computed(() => {
    const e = this.pager.error();
    return e ? adminErrorMessage(e) : null;
  });

  /** Everything owed that is at least one day past due. */
  readonly late = computed(() => {
    const a = this.ageing();
    return a ? a.total - a.current : 0;
  });

  /** Customers the ageing left out: they owe nothing, but their account can still be opened. */
  readonly others = computed(() => {
    const listed = new Set(this.ageing()?.listedCustomerIds ?? []);
    return this.customers().filter((c) => !listed.has(c.id));
  });

  ngOnInit(): Promise<void> {
    return this.load();
  }

  setAsOf(value: string): void {
    if (!value) return;
    // A filter of the pager: the board is asked again for the new day.
    this.asOf.set(value);
    void this.load();
  }

  /**
   * The paper-in-hand tile; the board loads itself through its pager. The sum of every pending
   * receipt is the server's (the receipts page's summary), so only one row travels for it.
   */
  async load(): Promise<void> {
    const [paper] = await Promise.allSettled([this.pages.receiptsPage({ skip: 0, take: 1, status: 'Pending' })]);
    // The board still stands when the receipts call fails; the tile says it could not be read
    // rather than showing a zero that would mean "no paper pending".
    if (paper.status === 'fulfilled') {
      this.paper.set(paper.value.summary?.pendingTotal ?? 0);
      this.paperFailed.set(false);
    } else {
      this.paper.set(null);
      this.paperFailed.set(true);
    }
  }

  openOther(): void {
    const id = this.otherId();
    if (id) this.openAccount.emit(id);
  }

  /**
   * The board on landscape paper (docs/07 §4.4). It has no id of its own — it is every customer
   * at one day — so the route's id segment is a dash and the day travels as `asOf`, exactly as
   * this screen asked the server for it.
   */
  print(): void {
    const tz = this.regional.settings().timeZone;
    this.printer.open('ageing', '-', {
      lang: this.i18n.lang(),
      asOf: this.asOf() === today(tz) ? undefined : midday(this.asOf()),
    });
  }

  /** A tile figure, or the dash while nothing has landed. */
  figure(value: number | null | undefined): string {
    return value === null || value === undefined ? '—' : this.i18n.money(value);
  }

  /** A zero is an empty cell: the phone layout hides it, and a row of zeros reads as the gaps it is. */
  cell(value: number): string {
    return value ? this.i18n.money(value) : '';
  }

  /** The late bucket holding the most money — the one to read first on a wide row; null when nothing is late. */
  strongest(row: CustomerAgeing): LateBucket | null {
    let best: LateBucket | null = null;
    for (const bucket of LATE_BUCKETS) {
      if (row[bucket] > 0 && (best === null || row[bucket] > row[best])) best = bucket;
    }
    return best;
  }

  /** Share of the invoiced total that is past due, 0..100. */
  lateShare(row: CustomerAgeing): number {
    if (row.total <= 0) return 0;
    return Math.max(0, Math.min(100, ((row.total - row.current) / row.total) * 100));
  }

  percent(share: number): string {
    // The company's number format, like every other figure (core/number-format.ts).
    return formatNumber(share / 100, this.i18n.lang(), { style: 'percent', maximumFractionDigits: 0 });
  }
}
