import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminBranch, AdminCustomer, AdminPaymentMethod, adminErrorMessage } from '../../../core/admin.service';
import { ApiError } from '../../../core/api.service';
import { I18nService } from '../../../core/i18n.service';
import { formatPhone } from '../../../core/phone';
import { RegionalService } from '../../../core/regional.service';
import { AccountPart, CollectRef, DocRef, RECEIPT_TAKE, exclusiveEnd, startOfMonth, toInputDate } from './receivable-view';
import { CustomerStatement, ReceivablesApi } from './receivables-api.service';
import { ReceivableStatementComponent } from './receivable-statement.component';
import { ReceivableInvoicesComponent } from './receivable-invoices.component';
import { ReceivableReceiptsComponent } from './receivable-receipts.component';
import { ReceivableCreditNotesComponent } from './receivable-credit-notes.component';

const PARTS: readonly AccountPart[] = ['statement', 'invoices', 'receipts', 'creditNotes'];

/**
 * One customer's account: **where do they stand with me right now, and which documents make
 * that up.**
 *
 * <p>The head is account 411 said in words and figures, each part named and none netted into
 * another: the trade debt; the till paper still uncleared (413), which is owed but kept beside
 * it; the pending receipt vouchers, which post nothing at all and so appear in neither; the
 * balance written on the customer card and its difference from the ledger, shown as a figure
 * rather than silently reconciled; and the part of the debt no invoice covers — the one ageing
 * cannot see because it has no due date.</p>
 *
 * <p>The standing comes from a statement whose window closes tomorrow, so its closing balance is
 * today's 411, plus a second call for the customer's pending vouchers. Each fails on its own:
 * a head that goes blank because one of two calls failed hides the figure that did arrive.</p>
 *
 * <p>A part change arrives as a new value of the `part` input on this same instance — the shell
 * stays on its account case — so the head is fetched once on init and the four tables below
 * switch on the signal. Each table is its own component and is re-created when the chip moves,
 * which is what refreshes it after a document was raised.</p>
 */
@Component({
  selector: 'app-receivable-account',
  standalone: true,
  imports: [
    FormsModule,
    ReceivableStatementComponent,
    ReceivableInvoicesComponent,
    ReceivableReceiptsComponent,
    ReceivableCreditNotesComponent,
  ],
  template: `
    <div class="bo-stack">
      <div class="bo-actions bo-actions--start no-print">
        <button type="button" class="btn-ghost bo-small" (click)="back.emit()">{{ i18n.t('inv.back.customers') }}</button>
      </div>

      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      @if (done()) {
        <p class="notice ok" role="status">{{ done() }}</p>
      }

      <section class="bo-panel">
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">
            <span>{{ customer()?.fullName || statement()?.customerName || '—' }}</span>
            @if (customer()?.code || statement()?.customerCode; as code) {
              <span class="bo-soft mono" dir="ltr">({{ code }})</span>
            }
          </h2>
          <div class="bo-actions no-print">
            <button type="button" class="btn-primary bo-small" (click)="newInvoice.emit(customerId())">
              {{ i18n.t('inv.acct.newInvoice') }}
            </button>
            <button type="button" class="btn-ghost bo-small" (click)="newReceipt.emit(collectRef())">
              {{ i18n.t('inv.acct.newReceipt') }}
            </button>
          </div>
          <p class="bo-meta bo-panel__note">
            @if (overLimit() || companyOwes()) {
              <span class="bo-states">
                @if (overLimit()) {
                  <span class="bo-state bo-state--danger">{{ i18n.t('backoffice.customers.overLimit') }}</span>
                }
                @if (companyOwes()) {
                  <span class="bo-state bo-state--lead">{{ i18n.t('inv.companyOwes') }}</span>
                }
              </span>
            }
            @if (customer()?.phone; as phone) {
              <span class="mono" dir="ltr">{{ phoneShown(phone) }}</span>
            }
            @if (customer()?.taxId; as taxId) {
              <span>{{ i18n.t('backoffice.customers.taxId') }} <span class="mono">{{ taxId }}</span></span>
            }
          </p>
        </div>

        @if (headBusy()) {
          <p class="bo-loading">
            <span class="bo-loading__word">{{ i18n.t('common.loading') }}</span>
            <span class="bo-loading__rows" aria-hidden="true"></span>
          </p>
        } @else {
          @if (statement(); as s) {
          <div class="bo-panel__body">
            <div class="bo-metrics">
              <div class="bo-metric bo-metric--lead">
                <span class="bo-metric__label">{{ i18n.t('inv.acct.tradeBalance') }}</span>
                <strong class="bo-metric__value mono">{{ i18n.money(s.closingBalance) }}</strong>
                <span class="bo-metric__note"><span>{{ i18n.t('inv.acct.tradeBalanceHint') }}</span></span>
              </div>
              @if (s.pendingNotes !== 0) {
                <div class="bo-metric">
                  <span class="bo-metric__label">{{ i18n.t('inv.acct.pendingNotes') }}</span>
                  <strong class="bo-metric__value mono">{{ i18n.money(s.pendingNotes) }}</strong>
                  <span class="bo-metric__note"><span>{{ i18n.t('inv.acct.pendingNotesHint') }}</span></span>
                </div>
              }
              <div class="bo-metric">
                <span class="bo-metric__label">{{ i18n.t('inv.acct.totalOwed') }}</span>
                <strong class="bo-metric__value mono">{{ i18n.money(s.totalOwed) }}</strong>
              </div>
              @if (paperFailed() || (paper() ?? 0) !== 0) {
                <div class="bo-metric">
                  <span class="bo-metric__label">{{ i18n.t('inv.acct.paperInHand') }}</span>
                  <strong class="bo-metric__value mono">{{ paperFailed() ? '—' : i18n.money(paper() ?? 0) }}</strong>
                  <span class="bo-metric__note">
                    <span>{{ i18n.t(paperFailed() ? 'inv.paperUnknown' : 'inv.acct.paperInHandHint') }}</span>
                  </span>
                </div>
              }
            </div>

            <div class="bo-metrics">
              <div class="bo-metric">
                <span class="bo-metric__label">{{ i18n.t('inv.acct.recorded') }}</span>
                <strong class="bo-metric__value mono">{{ i18n.money(s.recordedBalance) }}</strong>
                <span class="bo-metric__note"><span>{{ i18n.t('inv.acct.recordedHint') }}</span></span>
              </div>
              <div class="bo-metric" [class.bo-metric--alert]="s.balanceDifference !== 0">
                <span class="bo-metric__label">{{ i18n.t('inv.acct.difference') }}</span>
                @if (s.balanceDifference === 0) {
                  <strong class="bo-metric__value bo-metric__value--word">{{ i18n.t('inv.acct.noDifference') }}</strong>
                } @else {
                  <strong class="bo-metric__value mono">{{ i18n.money(s.balanceDifference) }}</strong>
                  <span class="bo-metric__note">
                    <span>{{ i18n.t('inv.acct.drift', { v: i18n.money(s.balanceDifference) }) }}</span>
                  </span>
                }
              </div>
              <div class="bo-metric">
                <span class="bo-metric__label">{{ i18n.t('inv.acct.invoicedOutstanding') }}</span>
                <strong class="bo-metric__value mono">{{ i18n.money(s.invoicedOutstanding) }}</strong>
                <span class="bo-metric__note"><span>{{ i18n.t('inv.acct.invoicedOutstandingHint') }}</span></span>
              </div>
              @if (s.uninvoicedBalance !== 0) {
                <div class="bo-metric">
                  <span class="bo-metric__label">{{ i18n.t('inv.acct.uninvoiced') }}</span>
                  <strong class="bo-metric__value mono">{{ i18n.money(s.uninvoicedBalance) }}</strong>
                  <span class="bo-metric__note"><span>{{ i18n.t('inv.acct.uninvoicedHint') }}</span></span>
                </div>
              }
              <div class="bo-metric">
                <span class="bo-metric__label">{{ i18n.t('backoffice.customers.creditLimit') }}</span>
                @if (s.creditLimit > 0) {
                  <strong class="bo-metric__value mono">{{ i18n.money(s.creditLimit) }}</strong>
                  <span class="bo-metric__note">
                    <span>{{ i18n.t(overLimit() ? 'inv.limit.over' : 'inv.limit.within') }}</span>
                  </span>
                } @else {
                  <strong class="bo-metric__value bo-metric__value--word">{{ i18n.t('inv.limit.none') }}</strong>
                }
              </div>
            </div>
          </div>
          }
        }
      </section>

      <nav class="bo-chips no-print" [attr.aria-label]="i18n.t('inv.openAccount')">
        @for (p of parts; track p) {
          <button
            type="button"
            [class.is-on]="part() === p"
            [attr.aria-current]="part() === p ? 'true' : null"
            (click)="partChanged.emit(p)"
          >
            {{ i18n.t('inv.part.' + p) }}
          </button>
        }
      </nav>

      @switch (part()) {
        @case ('statement') {
          <app-receivable-statement
            [customerId]="customerId()"
            [customers]="customers()"
            [methods]="methods()"
            (openInvoice)="openInvoice.emit($event)"
            (openReceipt)="openReceipt.emit($event)"
          />
        }
        @case ('invoices') {
          <app-receivable-invoices
            [customerId]="customerId()"
            [customers]="customers()"
            (openInvoice)="openInvoice.emit($event)"
            (newInvoice)="newInvoice.emit(customerId())"
            (collect)="newReceipt.emit($event)"
          />
        }
        @case ('receipts') {
          <app-receivable-receipts
            [customerId]="customerId()"
            [customers]="customers()"
            [methods]="methods()"
            (openReceipt)="openReceipt.emit($event)"
            (newReceipt)="newReceipt.emit(collectRef())"
            (bounce)="bounce.emit($event)"
          />
        }
        @case ('creditNotes') {
          <app-receivable-credit-notes [customerId]="customerId()" (openInvoice)="openInvoice.emit($event)" />
        }
      }
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss'],
  styles: [
    `
      /* The shared rule that separates two marks applies inside a table cell; a panel title
         holding a name and a code needs the same gap, and for the same reason — a plain margin
         between two inline runs lands outside the pair under RTL. */
      .bo-panel__title .bo-soft {
        margin-inline-start: 0.45rem;
      }
    `,
  ],
})
export class ReceivableAccountComponent implements OnInit {
  private readonly api = inject(ReceivablesApi);
  readonly i18n = inject(I18nService);
  private readonly regional = inject(RegionalService);

  /** +213 658 41 77 30 (core/phone.ts). */
  phoneShown(phone: string): string {
    return formatPhone(phone, this.regional.settings().phone);
  }

  readonly customerId = input.required<string>();
  readonly part = input<AccountPart>('statement');
  readonly customers = input<AdminCustomer[]>([]);
  readonly branches = input<AdminBranch[]>([]);
  readonly methods = input<AdminPaymentMethod[]>([]);
  readonly done = input<string | null>(null);

  readonly back = output<void>();
  readonly partChanged = output<AccountPart>();
  readonly newInvoice = output<string>();
  readonly newReceipt = output<CollectRef>();
  readonly openInvoice = output<DocRef>();
  readonly openReceipt = output<DocRef>();
  readonly bounce = output<DocRef>();

  readonly parts = PARTS;

  readonly headBusy = signal(false);
  readonly error = signal<string | null>(null);
  readonly statement = signal<CustomerStatement | null>(null);
  /** Σ pending receipt vouchers: paper that posts nothing, so it is in no statement figure. */
  readonly paper = signal<number | null>(null);
  readonly paperFailed = signal(false);

  readonly customer = computed(() => this.customers().find((c) => c.id === this.customerId()) ?? null);

  /** The card's own balance against its limit — what the creation warning reads, till paper included. */
  readonly overLimit = computed(() => {
    const limit = this.statement()?.creditLimit ?? this.customer()?.creditLimit ?? 0;
    const balance = this.statement()?.recordedBalance ?? this.customer()?.balance ?? 0;
    return limit > 0 && balance > limit;
  });

  /** A negative 411: credited or paid past what was owed. */
  readonly companyOwes = computed(() => (this.statement()?.closingBalance ?? 0) < 0);

  readonly collectRef = computed<CollectRef>(() => ({ customerId: this.customerId(), invoiceId: null }));

  async ngOnInit(): Promise<void> {
    this.headBusy.set(true);
    this.error.set(null);
    const id = this.customerId();
    // The window closes tomorrow so the closing balance is today's 411; the rows themselves are
    // the statement part's business and are not shown here.
    const now = new Date();
    const from = startOfMonth(now).toISOString();
    const to = exclusiveEnd(toInputDate(now)).toISOString();
    const [statement, paper] = await Promise.allSettled([
      this.api.statement(id, from, to),
      this.api.listReceipts({ customerId: id, status: 'Pending', take: RECEIPT_TAKE }),
    ]);
    if (statement.status === 'fulfilled') {
      this.statement.set(statement.value);
    } else {
      const e = statement.reason;
      this.error.set(e instanceof ApiError && e.status === 404 ? this.i18n.t('inv.acct.notFound') : adminErrorMessage(e));
    }
    if (paper.status === 'fulfilled') {
      this.paper.set(paper.value.reduce((sum, v) => sum + v.amount, 0));
      this.paperFailed.set(false);
    } else {
      this.paper.set(null);
      this.paperFailed.set(true);
    }
    this.headBusy.set(false);
  }
}
