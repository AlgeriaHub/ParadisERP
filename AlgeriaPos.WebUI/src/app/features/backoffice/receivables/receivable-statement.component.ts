import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminCustomer, adminErrorMessage, AdminPaymentMethod } from '../../../core/admin.service';
import { I18nService } from '../../../core/i18n.service';
import { AuthService } from '../../../core/auth.service';
import { RegionalService } from '../../../core/regional.service';
import { formatDay } from '../backoffice-format';
import {
  DocRef,
  INVOICE_TAKE,
  MAX_PERIOD_DAYS,
  daysBetween,
  periodDays,
  startOfDay,
  startOfMonth,
  toInputDate,
  withZ,
} from './receivable-view';
import { CustomerStatementHead, ReceivablePagesApi } from './receivable-pages.api';
import { CreditNote, CustomerStatementRow, ReceiptVoucher, ReceivablesApi, StatementFilter } from './receivables-api.service';
import { describeStatementRow, statementSourceLabel } from './statement-rows';
import { PrintService } from '../../print/print.service';
import { DateInputComponent } from '../../../ui/date-input.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';

type Preset = 'month' | 'lastMonth' | 'quarter' | 'year' | 'custom';

/**
 * Account 411 for one customer over a period: **what moved, what they owe at its end, and which
 * paper stands behind each line.**
 *
 * <p>The statement is the customer account itself. Every row is a ledger line; a sale, an
 * invoice, a receipt or a credit note only names it. So a credit sale nobody raised paper for is
 * a row here like any other, and a sale that was invoiced later shows the invoice number beside
 * it — the paper the customer holds — without the invoice adding a second row, because a paper
 * invoice posts nothing.</p>
 *
 * <p>The window is guarded before the request: the server refuses more than `MAX_PERIOD_DAYS`
 * and an end before the start, and a raw 400 tells the reader less than the sentence here. The
 * end date is inclusive on screen and sent as the next day, since the server filters `< to`.</p>
 */
@Component({
  selector: 'app-receivable-statement',
  standalone: true,
  imports: [FormsModule, DateInputComponent, ListFooterComponent],
  template: `
    <div class="bo-stack">
      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }

      @if (statement(); as s) {
        <div class="bo-metrics">
          <div class="bo-metric">
            <span class="bo-metric__label">{{ i18n.t('acc.st.opening') }}</span>
            <strong class="bo-metric__value mono">{{ i18n.money(s.openingBalance) }}</strong>
          </div>
          <div class="bo-metric">
            <span class="bo-metric__label">{{ i18n.t('acc.debit') }}</span>
            <strong class="bo-metric__value mono">{{ i18n.money(s.totalDebit) }}</strong>
          </div>
          <div class="bo-metric">
            <span class="bo-metric__label">{{ i18n.t('acc.credit') }}</span>
            <strong class="bo-metric__value mono">{{ i18n.money(s.totalCredit) }}</strong>
          </div>
          <div class="bo-metric bo-metric--lead">
            <span class="bo-metric__label">{{ i18n.t('acc.st.closing') }}</span>
            <strong class="bo-metric__value mono">{{ i18n.money(s.closingBalance) }}</strong>
            @if (s.closingBalance < 0) {
              <span class="bo-metric__note"><span>{{ i18n.t('inv.companyOwes') }}</span></span>
            }
          </div>
        </div>
      }

      <section class="bo-panel">
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ i18n.t('inv.stmt.title') }}</h2>
          <button type="button" class="btn-ghost bo-small no-print" (click)="print()">{{ i18n.t('common.print') }}</button>
          <p class="bo-panel__note">{{ i18n.t('inv.stmt.note') }} {{ i18n.t('inv.stmt.otherTab') }}</p>
        </div>

        <div class="bo-panel__body no-print">
          <nav class="bo-chips" [attr.aria-label]="i18n.t('reports.filters')">
            <button
              type="button"
              [class.is-on]="preset() === 'month'"
              [attr.aria-current]="preset() === 'month' ? 'true' : null"
              (click)="applyPreset('month')"
            >
              {{ i18n.t('reports.thisMonth') }}
            </button>
            <button
              type="button"
              [class.is-on]="preset() === 'lastMonth'"
              [attr.aria-current]="preset() === 'lastMonth' ? 'true' : null"
              (click)="applyPreset('lastMonth')"
            >
              {{ i18n.t('acc.lastMonth') }}
            </button>
            <button
              type="button"
              [class.is-on]="preset() === 'quarter'"
              [attr.aria-current]="preset() === 'quarter' ? 'true' : null"
              (click)="applyPreset('quarter')"
            >
              {{ i18n.t('acc.thisQuarter') }}
            </button>
            <button
              type="button"
              [class.is-on]="preset() === 'year'"
              [attr.aria-current]="preset() === 'year' ? 'true' : null"
              (click)="applyPreset('year')"
            >
              {{ i18n.t('acc.thisYear') }}
            </button>
          </nav>
          @if (canFilter()) {
            <nav class="bo-chips" [attr.aria-label]="i18n.t('stmt.filter.label')">
              <button
                type="button"
                [class.is-on]="kind() === 'all'"
                [attr.aria-current]="kind() === 'all' ? 'true' : null"
                (click)="kind.set('all'); load()"
              >
                {{ i18n.t('stmt.filter.all') }}
              </button>
              <button
                type="button"
                [class.is-on]="kind() === 'sales'"
                [attr.aria-current]="kind() === 'sales' ? 'true' : null"
                (click)="kind.set('sales'); load()"
              >
                {{ i18n.t('stmt.filter.sales') }}
              </button>
              <button
                type="button"
                [class.is-on]="kind() === 'rentals'"
                [attr.aria-current]="kind() === 'rentals' ? 'true' : null"
                (click)="kind.set('rentals'); load()"
              >
                {{ i18n.t('stmt.filter.rentals') }}
              </button>
            </nav>
          }
          <div class="bo-fields">
            <div class="bo-field">
              <label for="st-from">{{ i18n.t('reports.from') }}</label>
              <app-date-input id="st-from" [ngModel]="from()" (ngModelChange)="setFrom($event)" />
            </div>
            <div class="bo-field">
              <label for="st-to">{{ i18n.t('reports.to') }}</label>
              <app-date-input id="st-to" [ngModel]="to()" (ngModelChange)="setTo($event)" />
            </div>
          </div>
          <div class="bo-actions bo-actions--start">
            <button type="button" class="btn-ghost" [disabled]="busy() || !canLoad()" (click)="load()">
              {{ busy() ? i18n.t('common.loading') : i18n.t('inv.stmt.show') }}
            </button>
          </div>
          @if (tooWide()) {
            <p class="notice warn">{{ i18n.t('acc.tooWide', { days: maxDays }) }}</p>
          }
          @if (orderProblem()) {
            <p class="notice warn">{{ i18n.t('inv.stmt.problem.order') }}</p>
          }
        </div>

        @if (statement(); as s) {
          @if (s.filter && s.filter !== 'all') {
            <p class="notice">{{ i18n.t('stmt.filter.partial', { part: i18n.t('stmt.filter.' + s.filter) }) }}</p>
          }
          <div class="bo-scroll bo-scroll--tall">
            <table class="bo-data" [attr.aria-label]="caption(s)">
              <thead>
                <tr>
                  <th>{{ i18n.t('receipt.date') }}</th>
                  <th>{{ i18n.t('stmt.kind.column') }}</th>
                  <th>{{ i18n.t('inv.stmt.document') }}</th>
                  <th>{{ i18n.t('inv.stmt.invoicedAs') }}</th>
                  <th>{{ i18n.t('inv.stmt.description') }}</th>
                  <th class="num">{{ i18n.t('acc.debit') }}</th>
                  <th class="num">{{ i18n.t('acc.credit') }}</th>
                  <th class="num">{{ i18n.t('treasury.balance') }}</th>
                  <th class="no-print">{{ i18n.t('common.actions') }}</th>
                </tr>
              </thead>
              <tbody>
                <!-- The opening line heads the first page, where the running balance starts from it. -->
                @if (pager.index() === 0) {
                  <tr>
                    <th scope="row">{{ i18n.t('acc.st.opening') }}</th>
                    <td></td>
                    <td></td>
                    <td></td>
                    <td></td>
                    <td></td>
                    <td></td>
                    <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('treasury.balance')">
                      {{ i18n.money(s.openingBalance) }}
                    </td>
                    <td class="no-print"></td>
                  </tr>
                }
                @for (row of pager.rows(); track $index) {
                  <tr>
                    <td class="mono" [attr.data-label]="i18n.t('receipt.date')">{{ day(row.date) }}</td>
                    <td [attr.data-label]="i18n.t('stmt.kind.column')">
                      <span class="bo-soft">{{ row.kind ? i18n.t('stmt.kind.' + row.kind) : '' }}</span>
                    </td>
                    <td [attr.data-label]="i18n.t('inv.stmt.document')">
                      <span class="mono">{{ row.documentNumber || '—' }}</span>
                      <span class="bo-soft">{{ sourceLabel(row.sourceType) }}</span>
                    </td>
                    <td [attr.data-label]="i18n.t('inv.stmt.invoicedAs')">
                      @if (row.invoiceNumber) {
                        <span class="mono">{{ row.invoiceNumber }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('inv.stmt.description')">
                      @if (describe(row); as said) {
                        <span>{{ said }}</span>
                      }
                      @if (row.isReversal) {
                        <span class="bo-state bo-state--warn">{{ i18n.t('inv.stmt.reversal') }}</span>
                      }
                      @if (row.sourceType === 'sale' && row.paidAtTill > 0) {
                        <div>
                          <span class="bo-soft">{{ i18n.t('stmt.kind.paidAtTill') }} </span>
                          <span class="mono">{{ i18n.money(row.paidAtTill) }}</span>
                        </div>
                      }
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('acc.debit')">{{ row.debit ? i18n.money(row.debit) : '' }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('acc.credit')">{{ row.credit ? i18n.money(row.credit) : '' }}</td>
                    <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('treasury.balance')">
                      <span>{{ i18n.money(row.balance) }}</span>
                      @if (row.balance < 0) {
                        <span class="bo-soft">{{ i18n.t('inv.companyOwes') }}</span>
                      }
                    </td>
                    <td class="bo-cell-actions no-print" [attr.data-label]="i18n.t('common.actions')">
                      @if (target(row); as ref) {
                        <button type="button" class="btn-ghost bo-small" (click)="open(row, ref)">
                          {{ i18n.t('common.view') }}
                        </button>
                      }
                    </td>
                  </tr>
                } @empty {
                  <tr>
                    <td colspan="9" class="bo-empty">
                      <strong>{{ i18n.t('inv.stmt.none') }}</strong>
                      <span>{{ i18n.t('inv.stmt.noneHint') }}</span>
                    </td>
                  </tr>
                }
              </tbody>
              <tfoot>
                <tr>
                  <td [attr.data-label]="i18n.t('receipt.date')">{{ i18n.t('common.total') }}</td>
                  <td></td>
                  <td></td>
                  <td></td>
                  <td></td>
                  <td class="num mono" [attr.data-label]="i18n.t('acc.debit')">{{ i18n.money(s.totalDebit) }}</td>
                  <td class="num mono" [attr.data-label]="i18n.t('acc.credit')">{{ i18n.money(s.totalCredit) }}</td>
                  <td></td>
                  <td class="no-print"></td>
                </tr>
                <tr>
                  <td [attr.data-label]="i18n.t('receipt.date')">{{ i18n.t('acc.st.closing') }}</td>
                  <td></td>
                  <td></td>
                  <td></td>
                  <td></td>
                  <td></td>
                  <td></td>
                  <td class="num mono" [attr.data-label]="i18n.t('treasury.balance')">{{ i18n.money(s.closingBalance) }}</td>
                  <td class="no-print"></td>
                </tr>
              </tfoot>
            </table>
          </div>
          <app-list-footer [pager]="pager" [loading]="pager.loading()" />
        } @else if (busy()) {
          <p class="bo-loading">
            <span class="bo-loading__word">{{ i18n.t('common.loading') }}</span>
            <span class="bo-loading__rows" aria-hidden="true"></span>
          </p>
        }
      </section>
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss'],
})
export class ReceivableStatementComponent implements OnInit {
  private readonly api = inject(ReceivablesApi);
  private readonly pages = inject(ReceivablePagesApi);
  private readonly regional = inject(RegionalService);
  private readonly printer = inject(PrintService);
  private readonly auth = inject(AuthService);
  readonly i18n = inject(I18nService);

  readonly customerId = input.required<string>();
  readonly customers = input<AdminCustomer[]>([]);
  /** The tenders, so a receipt row can say what it came in under, in the reader's words. */
  readonly methods = input<AdminPaymentMethod[]>([]);

  readonly openInvoice = output<DocRef>();
  readonly openReceipt = output<DocRef>();

  readonly maxDays = MAX_PERIOD_DAYS;

  /** The customer's credit notes, read once so a credit-note row can open the invoice it corrects. */
  readonly creditNotes = signal<CreditNote[]>([]);
  readonly receipts = signal<ReceiptVoucher[]>([]);
  readonly preset = signal<Preset>('month');
  readonly from = signal<string>(toInputDate(startOfMonth(new Date())));
  readonly to = signal<string>(toInputDate(startOfDay(new Date())));
  readonly kind = signal<StatementFilter>('all');

  /** The statement asked for (Show, a preset, a kind): the customer, its days and its kind. */
  private readonly applied = signal<{ customerId: string; from: string; to: string; kind: StatementFilter } | null>(null);

  /**
   * The statement's lines, ten to a page to begin with, from the server (owner, 2026-11-17:
   * "paginate in backend, not frontend"). The running balance is computed there over every line;
   * the head (opening, totals, closing) comes with each page. Paged only: a running balance and its
   * totals are never filtered. Another window or kind starts at page one.
   */
  readonly pager = createServerPager(
    (page) => this.pages.statementPage({ skip: page.skip, take: page.take, ...page.filters.query! }),
    {
      key: 'receivables.statement',
      filters: () => ({ query: this.applied() }),
      enabled: () => !!this.applied(),
      debounceMs: 0,
    },
  );

  /** The statement on screen: its head, once the server has answered for this customer. */
  readonly statement = computed<CustomerStatementHead | null>(() => {
    const q = this.applied();
    const head = this.pager.summary();
    return q && head && head.customerId === q.customerId ? head : null;
  });
  readonly busy = computed(() => this.pager.loading());
  readonly error = computed(() => {
    const e = this.pager.error();
    return e ? adminErrorMessage(e) : null;
  });

  readonly canFilter = computed(() => this.auth.currentUser()?.capabilities?.includes('customer_statements.filter') ?? false);
  readonly tooWide = computed(() => periodDays(this.from(), this.to()) > MAX_PERIOD_DAYS);
  readonly orderProblem = computed(() => daysBetween(this.from(), this.to()) < 0);
  readonly canLoad = computed(() => !!this.from() && !!this.to() && !this.tooWide() && !this.orderProblem());

  ngOnInit(): Promise<void> {
    return this.load();
  }

  applyPreset(preset: Preset): void {
    this.preset.set(preset);
    const now = new Date();
    if (preset === 'month') {
      this.from.set(toInputDate(startOfMonth(now)));
      this.to.set(toInputDate(startOfDay(now)));
    } else if (preset === 'lastMonth') {
      this.from.set(toInputDate(new Date(now.getFullYear(), now.getMonth() - 1, 1)));
      this.to.set(toInputDate(new Date(now.getFullYear(), now.getMonth(), 0)));
    } else if (preset === 'quarter') {
      this.from.set(toInputDate(new Date(now.getFullYear(), Math.floor(now.getMonth() / 3) * 3, 1)));
      this.to.set(toInputDate(startOfDay(now)));
    } else if (preset === 'year') {
      this.from.set(toInputDate(new Date(now.getFullYear(), 0, 1)));
      this.to.set(toInputDate(startOfDay(now)));
    }
    void this.load();
  }

  setFrom(value: string): void {
    if (!value) return;
    this.from.set(value);
    this.preset.set('custom');
  }

  setTo(value: string): void {
    if (!value) return;
    this.to.set(value);
    this.preset.set('custom');
  }

  async load(): Promise<void> {
    // Refused here, in the reader's words, before the server's raw 400 could say less.
    if (!this.canLoad()) return;
    const id = this.customerId();
    const kind = this.canFilter() ? this.kind() : 'all';
    // Plain days, both included, read in the company's zone by the server.
    this.applied.set({ customerId: id, from: this.from(), to: this.to(), kind });
    this.pager.reset();
    const [, creditNotes, receipts] = await Promise.allSettled([
      this.pager.reload(),
      this.api.listCreditNotes({ customerId: id, take: INVOICE_TAKE }),
      this.api.listReceipts({ customerId: id, take: INVOICE_TAKE }),
    ]);
    // Without the credit notes a credit-note row simply has no button, and without the receipts
    // a receipt row says nothing of its tender; the statement itself still stands.
    if (creditNotes.status === 'fulfilled') this.creditNotes.set(creditNotes.value);
    if (receipts.status === 'fulfilled') this.receipts.set(receipts.value);
  }

  /**
   * The paper is its own document, not this screen with the chips hidden (docs/07 §4.2): the
   * window on it is the window on screen, and the sheet carries the opening and closing rows,
   * the per-customer ageing strip and the company's head, none of which are here.
   */
  print(): void {
    this.printer.open('statement', this.customerId(), {
      lang: this.i18n.lang(),
      from: this.from(),
      to: this.to(),
      kind: this.canFilter() ? this.kind() : 'all',
    });
  }

  day(value: string): string {
    return formatDay(withZ(value), this.i18n.lang(), this.regional.settings().timeZone);
  }

  /** The word for a source, or the raw key for one nobody has named yet (statement-rows.ts). */
  sourceLabel(type: string): string {
    return statementSourceLabel(type, (key) => this.i18n.t(key));
  }

  /** The table's accessible name: whose account, over which days. */
  caption(s: CustomerStatementHead): string {
    const lang = this.i18n.lang();
    const tz = this.regional.settings().timeZone;
    return `${nameOf(s)} ${formatDay(withZ(s.from), lang, tz)} – ${formatDay(withZ(this.lastDay(s)), lang, tz)}`;
  }

  /** The row's story in the reader's language — the rule the printed sheet follows too (statement-rows.ts). */
  describe(row: CustomerStatementRow): string {
    return describeStatementRow(
      row,
      { creditNotes: this.creditNotes(), receipts: this.receipts(), methods: this.methods() },
      this.i18n.lang(),
      (key, params) => this.i18n.t(key, params),
    );
  }

  /**
   * Which document a row opens: an invoice or a receipt directly, a credit note through the
   * invoice it corrects. Sales and rental notes belong to their own sections and get no button.
   */
  target(row: CustomerStatementRow): DocRef | null {
    const customerId = this.customerId();
    switch (row.sourceType) {
      case 'invoice':
      case 'customer-receipt':
        return { id: row.sourceId, customerId };
      case 'credit-note': {
        const note = this.creditNotes().find((n) => n.id === row.sourceId);
        return note ? { id: note.invoiceId, customerId } : null;
      }
      default:
        return null;
    }
  }

  open(row: CustomerStatementRow, ref: DocRef): void {
    if (row.sourceType === 'customer-receipt') this.openReceipt.emit(ref);
    else this.openInvoice.emit(ref);
  }

  /** `to` is exclusive on the wire; the reader is shown the last day the window covers. */
  private lastDay(s: CustomerStatementHead): string {
    const end = new Date(withZ(s.to));
    end.setUTCDate(end.getUTCDate() - 1);
    return end.toISOString();
  }
}

function nameOf(s: CustomerStatementHead): string {
  return s.customerName || s.customerCode || '';
}
