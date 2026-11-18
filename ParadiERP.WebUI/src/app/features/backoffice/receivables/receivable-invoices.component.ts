import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MENU } from '../../../ui/menu.component';
import { AdminCustomer, adminErrorMessage } from '../../../core/admin.service';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService } from '../../../core/regional.service';
import { formatDay } from '../backoffice-format';
import { DateInputComponent } from '../../../ui/date-input.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { TableStateComponent } from '../../../ui/table-state.component';
import {
  CollectRef,
  DocRef,
  INVOICE_TONE,
  parseInputDate,
  startOfMonth,
  stateKey,
  toInputDate,
  today,
  withZ,
} from './receivable-view';
import { ReceivablePagesApi } from './receivable-pages.api';
import { Invoice, InvoiceOrigin, InvoiceStatus, allocatable } from './receivables-api.service';
import { TaxMode } from '../../../core/models';

const FILTERS = ['all', 'open', 'overdue', 'settled', 'credited'] as const;
type Filter = (typeof FILTERS)[number];

const PERIODS = ['all', 'months12', 'thisMonth', 'lastMonth'] as const;
type Period = (typeof PERIODS)[number];

/** The chip label for each preset; two of them reuse words other tabs already own. */
const PERIOD_KEY: Record<Period, string> = {
  all: 'inv.list.period.all',
  months12: 'inv.list.period.months12',
  thisMonth: 'reports.thisMonth',
  lastMonth: 'acc.lastMonth',
};

/**
 * The question this screen answers: **which invoices are out, which are late, and how much of
 * what is still owed is only spoken for by uncleared paper.**
 *
 * <p>The list is paged by the server (`GET /api/receivables/invoices/page`, owner 2026-11-17):
 * the status chip, the finder and the filters run there over every invoice of the window, never
 * over "the most recent N", so old debts never drop out of the open ones; the tiles are its
 * figures over every invoice the filters keep.</p>
 *
 * <p>`claimed` has its own column and is never taken off `outstanding`: a cheque in the drawer
 * has settled nothing until the bank says so.</p>
 */
@Component({
  selector: 'app-receivable-invoices',
  standalone: true,
  imports: [FormsModule, DateInputComponent, ListFooterComponent, TableStateComponent, ...MENU],
  template: `
    <div class="bo-stack">
      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }

      <!-- The tiles are the server's, over every invoice the filters keep (owner, 2026-11-17). -->
      <div class="bo-metrics">
        <div class="bo-metric">
          <span class="bo-metric__label">{{ i18n.t('inv.list.openCount') }}</span>
          <strong class="bo-metric__value bo-metric__value--word">{{ hasRows() ? totals().openCount : '—' }}</strong>
          <span class="bo-metric__note"><span>{{ i18n.t('inv.list.shownNote') }}</span></span>
        </div>
        <div class="bo-metric bo-metric--lead">
          <span class="bo-metric__label">{{ i18n.t('inv.list.outstandingTotal') }}</span>
          <strong class="bo-metric__value mono">{{ hasRows() ? i18n.money(totals().outstanding) : '—' }}</strong>
          <span class="bo-metric__note"><span>{{ i18n.t('inv.list.shownNote') }}</span></span>
        </div>
        <div class="bo-metric" [class.bo-metric--alert]="totals().overdue > 0">
          <span class="bo-metric__label">{{ i18n.t('inv.list.overdueTotal') }}</span>
          <strong class="bo-metric__value mono">{{ hasRows() ? i18n.money(totals().overdue) : '—' }}</strong>
          <span class="bo-metric__note"><span>{{ i18n.t('inv.overdue') }}</span></span>
        </div>
        @if (totals().claimed > 0) {
          <div class="bo-metric">
            <span class="bo-metric__label">{{ i18n.t('inv.claimed') }}</span>
            <strong class="bo-metric__value mono">{{ i18n.money(totals().claimed) }}</strong>
            <span class="bo-metric__note"><span>{{ i18n.t('inv.claimedHint') }}</span></span>
          </div>
        }
      </div>

      <section class="bo-panel">
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ i18n.t('inv.invoices') }}</h2>
          <button type="button" class="btn-primary bo-small" (click)="newInvoice.emit(customerId() ?? pickedCustomer())">
            {{ i18n.t('inv.acct.newInvoice') }}
          </button>
          <p class="bo-panel__note">{{ i18n.t('inv.claimedHint') }}</p>
        </div>

        <div class="bo-panel__body">
          <nav class="bo-chips" [attr.aria-label]="i18n.t('common.state')">
            @for (choice of filters; track choice) {
              <button
                type="button"
                [class.is-on]="filter() === choice"
                [attr.aria-current]="filter() === choice ? 'true' : null"
                (click)="filter.set(choice)"
              >
                {{ i18n.t('inv.list.filter.' + choice) }}
              </button>
            }
          </nav>

          <nav class="bo-chips" [attr.aria-label]="i18n.t('inv.issuedOn')">
            @for (choice of periods; track choice) {
              <button
                type="button"
                [class.is-on]="period() === choice"
                [attr.aria-current]="period() === choice ? 'true' : null"
                (click)="setPeriod(choice)"
              >
                {{ i18n.t(periodKey[choice]) }}
              </button>
            }
          </nav>

          <div class="bo-fields">
            <div class="bo-field">
              <label for="il-from">{{ i18n.t('reports.from') }}</label>
              <app-date-input id="il-from" [ngModel]="from()" (ngModelChange)="setFrom($event)" />
            </div>
            <div class="bo-field">
              <label for="il-to">{{ i18n.t('reports.to') }}</label>
              <app-date-input id="il-to" [ngModel]="to()" (ngModelChange)="setTo($event)" />
            </div>
            @if (!customerId()) {
              <div class="bo-field">
                <label for="il-customer">{{ i18n.t('common.customer') }}</label>
                <select id="il-customer" [ngModel]="pickedCustomer()" (ngModelChange)="pickCustomer($event)">
                  <option [ngValue]="null">{{ i18n.t('rent.filter.allCustomers') }}</option>
                  @for (c of customers(); track c.id) {
                    <option [ngValue]="c.id">{{ c.fullName }}</option>
                  }
                </select>
              </div>
            }
            <div class="bo-field">
              <label for="il-find">{{ i18n.t('inv.find') }}</label>
              <input
                id="il-find"
                type="search"
                [placeholder]="i18n.t('inv.find')"
                [ngModel]="query()"
                (ngModelChange)="query.set($event)"
              />
            </div>
            <!-- More filters (owner, 2026-11-17), applied by the server; each shows when the window's invoices differ. -->
            @if (origins().length > 1 || originFilter() !== 'all') {
              <div class="bo-field">
                <label for="il-f-origin">{{ i18n.t('inv.origin') }}</label>
                <select id="il-f-origin" [ngModel]="originFilter()" (ngModelChange)="originFilter.set($event)">
                  <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                  @for (o of origins(); track o) {
                    <option [ngValue]="o">{{ word('inv.origin', o) }}</option>
                  }
                </select>
              </div>
            }
            @if (taxModes().length > 1 || taxFilter() !== 'all') {
              <div class="bo-field">
                <label for="il-f-tax">{{ i18n.t('inv.taxMode') }}</label>
                <select id="il-f-tax" [ngModel]="taxFilter()" (ngModelChange)="taxFilter.set($event)">
                  <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                  <option ngValue="Taxed">{{ i18n.t('inv.taxMode.Taxed') }}</option>
                  <option ngValue="Untaxed">{{ i18n.t('inv.taxMode.Untaxed') }}</option>
                </select>
              </div>
            }
            <div class="bo-field">
              <label for="il-f-paper">{{ i18n.t('inv.claimed') }}</label>
              <select id="il-f-paper" [ngModel]="paperFilter()" (ngModelChange)="paperFilter.set($event)">
                <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                <option ngValue="yes">{{ i18n.t('filters.yes') }}</option>
                <option ngValue="no">{{ i18n.t('filters.no') }}</option>
              </select>
            </div>
            @if (filtered()) {
              <div class="bo-actions bo-actions--start">
                <button type="button" class="btn-ghost bo-small" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
              </div>
            }
          </div>
        </div>

        <div class="bo-scroll bo-scroll--tall">
          <table class="bo-data" [attr.aria-label]="i18n.t('inv.invoices')">
            <thead>
              <tr>
                <th>{{ i18n.t('inv.number') }}</th>
                @if (!customerId()) {
                  <th>{{ i18n.t('common.customer') }}</th>
                }
                <th>{{ i18n.t('inv.issuedOn') }}</th>
                <th>{{ i18n.t('inv.dueOn') }}</th>
                <th>{{ i18n.t('inv.origin') }}</th>
                <th class="num">{{ i18n.t('inv.totalTtc') }}</th>
                <th class="num">{{ i18n.t('inv.outstanding') }}</th>
                <th class="num">{{ i18n.t('inv.claimed') }}</th>
                <th>{{ i18n.t('common.state') }}</th>
                <th><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
              </tr>
            </thead>
            <tbody>
              @for (inv of pager.rows(); track inv.id) {
                <tr>
                  <td class="mono" [attr.data-label]="i18n.t('inv.number')">
                    <!-- The number opens the invoice (the list pattern); the rest is in the row menu. -->
                    <button type="button" class="ui-link" (click)="openInvoice.emit({ id: inv.id, customerId: inv.customerId })">{{ inv.number }}</button>
                  </td>
                  @if (!customerId()) {
                    <td [attr.data-label]="i18n.t('common.customer')">{{ customerName(inv) }}</td>
                  }
                  <td class="mono" [attr.data-label]="i18n.t('inv.issuedOn')">{{ day(inv.issuedAtUtc) }}</td>
                  <td [attr.data-label]="i18n.t('inv.dueOn')">
                    <span class="mono">{{ day(inv.dueDateUtc) }}</span>
                    @if (isLate(inv)) {
                      <span class="bo-state bo-state--danger">{{ i18n.t('inv.lateBy', { n: inv.daysOverdue }) }}</span>
                    }
                  </td>
                  <td [attr.data-label]="i18n.t('inv.origin')">{{ word('inv.origin', inv.origin) }}</td>
                  <td class="num mono" [attr.data-label]="i18n.t('inv.totalTtc')">{{ i18n.money(inv.grandTotalTtc) }}</td>
                  <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('inv.outstanding')">
                    <span>{{ i18n.money(inv.outstanding) }}</span>
                    @if (inv.outstanding < 0) {
                      <span class="bo-soft">{{ i18n.t('inv.companyOwes') }}</span>
                    }
                  </td>
                  <!-- Empty rather than a zero: on a phone an empty cell is dropped from the card. -->
                  <td class="num mono" [attr.data-label]="i18n.t('inv.claimed')">{{ inv.claimed > 0 ? i18n.money(inv.claimed) : '' }}</td>
                  <td [attr.data-label]="i18n.t('common.state')">
                    <span class="bo-states">
                      <span class="bo-state" [class]="tone(inv.status)">{{ word('inv.state', inv.status) }}</span>
                      @if (isLate(inv)) {
                        <span class="bo-state bo-state--danger">{{ i18n.t('inv.overdue') }}</span>
                      }
                    </span>
                  </td>
                  <td class="ui-cell-menu">
                    <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + inv.number">
                      <span menuTrigger aria-hidden="true">⋯</span>
                      <button type="button" appMenuItem (click)="openInvoice.emit({ id: inv.id, customerId: inv.customerId })">{{ i18n.t('common.view') }}</button>
                      @if (canCollect(inv)) {
                        <button type="button" appMenuItem (click)="collect.emit({ customerId: inv.customerId, invoiceId: inv.id })">{{ i18n.t('inv.collect') }}</button>
                      }
                      @if (!customerId()) {
                        <button type="button" appMenuItem (click)="openAccount.emit(inv.customerId)">{{ i18n.t('inv.openAccount') }}</button>
                      }
                    </app-menu>
                  </td>
                </tr>
              } @empty {
                <tr>
                  <td [attr.colspan]="columns()" class="bo-empty">
                    @if (!pager.loaded()) {
                      <strong>{{ i18n.t('common.loading') }}</strong>
                      <span class="bo-loading__rows" aria-hidden="true"></span>
                    } @else if (hasRows() && filtered()) {
                      <app-table-state kind="noResults" (clear)="clearFilters()" />
                    } @else {
                      <strong>{{ i18n.t('inv.list.none') }}</strong>
                      <span>{{ i18n.t('inv.list.noneHint') }}</span>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        <app-list-footer [pager]="pager" [loading]="!pager.loaded()" />
      </section>
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
})
export class ReceivableInvoicesComponent implements OnInit {
  private readonly pages = inject(ReceivablePagesApi);
  readonly i18n = inject(I18nService);
  private readonly regional = inject(RegionalService);

  /** Fixed inside an account; null in the cross-customer section, where a select takes its place. */
  readonly customerId = input<string | null>(null);
  readonly customers = input<AdminCustomer[]>([]);

  readonly openInvoice = output<DocRef>();
  readonly openAccount = output<string>();
  readonly newInvoice = output<string | null>();
  readonly collect = output<CollectRef>();

  readonly filters = FILTERS;
  readonly periods = PERIODS;
  readonly periodKey = PERIOD_KEY;

  readonly filter = signal<Filter>('open');
  /** Null once a date was typed by hand: no preset then describes the window. */
  readonly period = signal<Period | null>(null);
  readonly from = signal<string | null>(null);
  readonly to = signal<string | null>(null);
  readonly pickedCustomer = signal<string | null>(null);
  readonly query = signal('');

  /** Set once the first window is chosen, so the first request already carries it. */
  private readonly ready = signal(false);

  readonly columns = computed(() => (this.customerId() ? 9 : 10));

  // More filters (owner, 2026-11-17), applied by the server over every invoice of the window, like the status chips.
  readonly originFilter = signal<'all' | InvoiceOrigin>('all');
  readonly taxFilter = signal<'all' | TaxMode>('all');
  /** Pending paper spoken for the invoice (`claimed`): yes, or none. */
  readonly paperFilter = signal<'all' | 'yes' | 'no'>('all');

  readonly filtered = computed(
    () =>
      !!this.query().trim() || this.originFilter() !== 'all' || this.taxFilter() !== 'all' || this.paperFilter() !== 'all',
  );

  /**
   * The invoices a page at a time from the server (owner, 2026-11-17: "paginate in backend, not
   * frontend"). The window, the customer, the status chip, the finder and the filters all run
   * there, over every invoice of the window — no longer the most recent thousand — and the server
   * sorts what is owed and oldest first. Another filter, window or customer starts at page one.
   */
  readonly pager = createServerPager(
    (page) => this.pages.invoicesPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'receivables.invoices',
      filters: () => ({
        customerId: this.customerId() ?? this.pickedCustomer(),
        // Plain days, both included; the server reads them in the company's zone.
        from: this.from(),
        to: this.to(),
        state: this.filter(),
        q: this.query().trim(),
        origin: this.originFilter(),
        taxMode: this.taxFilter(),
        paper: this.paperFilter(),
      }),
      enabled: () => this.ready(),
    },
  );

  /** The list could not be read, in words. */
  readonly error = computed(() => {
    const e = this.pager.error();
    return e ? adminErrorMessage(e) : null;
  });

  /** The tiles, over every invoice the filters keep, as their notes say. */
  readonly totals = computed(() => this.pager.summary() ?? { openCount: 0, outstanding: 0, overdue: 0, claimed: 0 });

  /** The origins and tax modes the window's invoices hold, so a choice can always match. */
  readonly origins = computed(() => this.pager.summary()?.origins ?? []);
  readonly taxModes = computed(() => this.pager.summary()?.taxModes ?? []);
  /** Whether the window holds any invoice at all (every invoice has an origin), whatever the chip and filters. */
  readonly hasRows = computed(() => this.origins().length > 0);

  clearFilters(): void {
    this.query.set('');
    this.originFilter.set('all');
    this.taxFilter.set('all');
    this.paperFilter.set('all');
  }

  ngOnInit(): void {
    // Inside an account the list is the customer's whole file; across customers a year is the
    // window that stays readable and still catches anything worth chasing.
    this.applyPeriod(this.customerId() ? 'all' : 'months12');
    this.ready.set(true);
  }

  // Each change below is a filter of the pager, which asks the server again from page one.

  setPeriod(period: Period): void {
    this.applyPeriod(period);
  }

  setFrom(value: string | null): void {
    this.from.set(value || null);
    this.period.set(null);
  }

  setTo(value: string | null): void {
    this.to.set(value || null);
    this.period.set(null);
  }

  pickCustomer(id: string | null): void {
    this.pickedCustomer.set(id);
  }

  customerName(inv: Invoice): string {
    return inv.customerName || this.customers().find((c) => c.id === inv.customerId)?.fullName || '—';
  }

  isLate(inv: Invoice): boolean {
    return inv.daysOverdue > 0 && inv.outstanding > 0;
  }

  /** Outstanding less what pending paper already spoke for; the server re-checks on save. */
  canCollect(inv: Invoice): boolean {
    return allocatable(inv) > 0;
  }

  tone(status: InvoiceStatus): string {
    return INVOICE_TONE[status] ?? '';
  }

  /** A server enum as a word, or raw when nobody has named it yet. */
  word(prefix: string, value: string): string {
    const key = stateKey(prefix, value);
    const text = this.i18n.t(key);
    return text === key ? value : text;
  }

  day(value: string | null): string {
    return formatDay(value ? withZ(value) : null, this.i18n.lang(), this.regional.settings().timeZone);
  }

  /** Asks again for the page on screen. */
  async reload(): Promise<void> {
    await this.pager.reload();
  }

  private applyPeriod(period: Period): void {
    this.period.set(period);
    const now = parseInputDate(today(this.regional.settings().timeZone));
    switch (period) {
      case 'all':
        this.from.set(null);
        this.to.set(null);
        break;
      case 'months12':
        this.from.set(toInputDate(new Date(now.getFullYear(), now.getMonth() - 11, 1)));
        this.to.set(toInputDate(now));
        break;
      case 'thisMonth':
        this.from.set(toInputDate(startOfMonth(now)));
        this.to.set(toInputDate(now));
        break;
      case 'lastMonth': {
        const first = new Date(now.getFullYear(), now.getMonth() - 1, 1);
        const last = new Date(now.getFullYear(), now.getMonth(), 0);
        this.from.set(toInputDate(first));
        this.to.set(toInputDate(last));
        break;
      }
    }
  }
}
