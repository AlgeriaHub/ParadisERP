import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminBranch, AdminCustomer, AdminPaymentMethod, AdminService, adminErrorMessage } from '../../../core/admin.service';
import { ApiError } from '../../../core/api.service';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService } from '../../../core/regional.service';
import { PrintService } from '../../print/print.service';
import { formatDay, localizedName } from '../backoffice-format';
import { invoiceLineName } from '../maintenance/maintenance-words';
import {
  CollectRef,
  DocRef,
  INVOICE_TONE,
  RECEIPT_TAKE,
  RECEIPT_TONE,
  accountWordKey,
  parseCreditWarning,
  stateKey,
  withZ,
} from './receivable-view';
import {
  CreditNote,
  Invoice,
  InvoiceStatus,
  ReceiptStatus,
  ReceiptVoucher,
  ReceivablesApi,
  allocatable,
} from './receivables-api.service';

type Side = 'creditNotes' | 'receipts';

/** A voucher that named this invoice, with the one figure of it that was written here. */
interface ReceiptRow {
  voucher: ReceiptVoucher;
  onThisInvoice: number;
}

/**
 * One invoice, opened to answer: **what does it bill, what has actually arrived, what is merely
 * claimed, and what is still owed.**
 *
 * <p>The standing is shown as its parts and never netted. `prepaid` was settled on the source
 * document before this paper existed and is neither owed nor allocatable; `received` is money
 * that cleared; `claimed` is a pending cheque that has settled nothing yet and is not taken off
 * `outstanding`. A reader who sees one figure argues with it; a reader who sees the four knows
 * which one to chase.</p>
 *
 * <p>Nothing here edits or deletes. The correction is a credit note with a reason, and the
 * collection is a receipt; both are tasks that replace this screen and come back to it. The
 * credit-limit warning that travelled beside a freshly raised invoice is shown here in the
 * reader's currency — the invoice exists whatever the limit said.</p>
 *
 * <p>The receipts table is derived: the server lists vouchers by customer, not by invoice, so
 * the customer's vouchers are read and the ones whose allocations name this invoice kept.</p>
 */
@Component({
  selector: 'app-receivable-invoice',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="bo-stack">
      <div class="bo-actions bo-actions--start no-print">
        <button type="button" class="btn-ghost bo-small" (click)="back.emit(customerId())">{{ i18n.t('inv.back.account') }}</button>
      </div>

      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      @if (notFound()) {
        <p class="notice danger" role="alert">{{ i18n.t('inv.view.notFound') }}</p>
      }
      @if (creditWarning()) {
        <p class="notice warn no-print">{{ creditWarning() }}</p>
      }
      @if (done()) {
        <p class="notice ok no-print" role="status">{{ done() }}</p>
      }

      @if (invoice(); as inv) {
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">
              <span>{{ i18n.t('inv.invoice') }}</span>
              <span class="mono">{{ inv.number }}</span>
              <span class="bo-soft">{{ customerName() }}</span>
            </h2>
            <span class="bo-states">
              <span class="bo-state" [class]="tone(inv.status)">{{ word('inv.state', inv.status) }}</span>
              @if (late()) {
                <span class="bo-state bo-state--danger">{{ i18n.t('inv.lateBy', { n: inv.daysOverdue }) }}</span>
              }
              <span class="bo-state" [class.bo-state--lead]="posts(inv.origin)">
                {{ i18n.t(posts(inv.origin) ? 'inv.postedShort' : 'inv.paperShort') }}
              </span>
            </span>
            <p class="bo-meta">
              <span>{{ i18n.t('inv.issuedOn') }} {{ day(inv.issuedAtUtc) }}</span>
              <span>{{ i18n.t('inv.dueOn') }} {{ day(inv.dueDateUtc) }}</span>
              <span>{{ branchName(inv.branchId) }}</span>
              <span>{{ word('inv.taxMode', inv.taxMode) }}</span>
              <span>{{ word('inv.origin', inv.origin) }}</span>
              @if (paymentMode()) {
                <span>{{ i18n.t('inv.form.paymentMode') }} : {{ paymentMode() }}</span>
              }
              @if (inv.notes) {
                <span>{{ inv.notes }}</span>
              }
            </p>
          </div>
        </section>

        <p class="notice">{{ i18n.t(inv.origin === 'MaintenanceJob' ? 'inv.postedJob' : posts(inv.origin) ? 'inv.posted' : 'inv.paper') }}</p>

        <!-- ---------- what do they owe on it ---------- -->
        <div class="bo-metrics">
          <div class="bo-metric bo-metric--lead">
            <span class="bo-metric__label">{{ i18n.t('inv.totalTtc') }}</span>
            <strong class="bo-metric__value mono">{{ i18n.money(inv.grandTotalTtc) }}</strong>
          </div>
          @if (inv.prepaid > 0) {
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('inv.prepaid') }}</span>
              <strong class="bo-metric__value mono">{{ i18n.money(inv.prepaid) }}</strong>
              <span class="bo-metric__note"><span>{{ i18n.t('inv.prepaidHint') }}</span></span>
            </div>
          }
          @if (inv.credited > 0) {
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('inv.credited') }}</span>
              <strong class="bo-metric__value mono">{{ i18n.money(inv.credited) }}</strong>
            </div>
          }
          <div class="bo-metric">
            <span class="bo-metric__label">{{ i18n.t('inv.received') }}</span>
            <strong class="bo-metric__value mono">{{ i18n.money(inv.received) }}</strong>
          </div>
          @if (inv.claimed > 0) {
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('inv.claimed') }}</span>
              <strong class="bo-metric__value mono">{{ i18n.money(inv.claimed) }}</strong>
              <span class="bo-metric__note"><span>{{ i18n.t('inv.claimedHint') }}</span></span>
            </div>
          }
          <div class="bo-metric" [class.bo-metric--alert]="late()">
            <span class="bo-metric__label">{{ i18n.t('inv.outstanding') }}</span>
            <strong class="bo-metric__value mono">{{ i18n.money(inv.outstanding) }}</strong>
            @if (inv.outstanding < 0) {
              <span class="bo-metric__note"><span>{{ i18n.t('inv.companyOwes') }}</span></span>
            }
          </div>
          @if (allocatableAmount() > 0) {
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('inv.allocatable') }}</span>
              <strong class="bo-metric__value mono">{{ i18n.money(allocatableAmount()) }}</strong>
              <span class="bo-metric__note"><span>{{ i18n.t('inv.allocatableHint') }}</span></span>
            </div>
          }
          <div class="bo-metric" [class.bo-metric--alert]="late()">
            <span class="bo-metric__label">{{ i18n.t('inv.daysOverdue') }}</span>
            <strong class="bo-metric__value bo-metric__value--word">
              {{ late() ? i18n.t('inv.days', { n: inv.daysOverdue }) : i18n.t('inv.notLate') }}
            </strong>
          </div>
        </div>

        <div class="bo-split">
          <!-- ---------- the lines ---------- -->
          <section class="bo-panel">
            <div class="bo-panel__head">
              <h2 class="bo-panel__title">{{ i18n.t('inv.view.item') }}</h2>
            </div>
            <div class="bo-scroll">
              <table class="bo-data" [attr.aria-label]="i18n.t('inv.view.item')">
                <thead>
                  <tr>
                    <th>{{ i18n.t('inv.view.item') }}</th>
                    <th class="num">{{ i18n.t('register.qty') }}</th>
                    <th class="num">{{ i18n.t('inv.view.unitPrice') }}</th>
                    @if (hasDays()) {
                      <th class="num">{{ i18n.t('print.inv.days') }}</th>
                    }
                    @if (hasDiscount()) {
                      <th class="num">{{ i18n.t('inv.view.discount') }}</th>
                    }
                    <th>{{ i18n.t('receipt.rate') }}</th>
                    <th class="num">{{ i18n.t('inv.view.lineNet') }}</th>
                    <th>{{ i18n.t('inv.view.account') }}</th>
                    <th class="num">{{ i18n.t('inv.view.creditedQty') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (line of inv.lines; track line.id) {
                    <tr>
                      <td [attr.data-label]="i18n.t('inv.view.item')">
                        <span>{{ lineName(line.name) }}</span>
                        @if (line.sku) {
                          <span class="bo-soft mono">{{ line.sku }}</span>
                        }
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('register.qty')">{{ line.qty }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('inv.view.unitPrice')">{{ i18n.money(line.unitPrice) }}</td>
                      @if (hasDays()) {
                        <td class="num mono" [attr.data-label]="i18n.t('print.inv.days')">
                          {{ (line.days ?? 0) > 0 ? line.days : '' }}
                        </td>
                      }
                      @if (hasDiscount()) {
                        <td class="num mono" [attr.data-label]="i18n.t('inv.view.discount')">
                          {{ line.discount > 0 ? i18n.money(line.discount) : '' }}
                        </td>
                      }
                      <td [attr.data-label]="i18n.t('receipt.rate')">{{ i18n.rateLabel(line.taxRate) }}</td>
                      <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('inv.view.lineNet')">{{ i18n.money(line.lineNet) }}</td>
                      <td [attr.data-label]="i18n.t('inv.view.account')">
                        <span>{{ accountWord(line.incomeAccountCode) }}</span>
                        @if (accountWord(line.incomeAccountCode) !== line.incomeAccountCode) {
                          <span class="bo-soft mono">{{ line.incomeAccountCode }}</span>
                        }
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('inv.view.creditedQty')">
                        {{ line.creditedQty > 0 ? line.creditedQty : '' }}
                      </td>
                    </tr>
                  }
                </tbody>
                <tfoot>
                  <tr>
                    <td>{{ i18n.t('inv.totalHt') }}</td>
                    <td></td>
                    <td></td>
                    @if (hasDays()) {
                      <td></td>
                    }
                    @if (hasDiscount()) {
                      <td></td>
                    }
                    <td></td>
                    <td class="num mono" [attr.data-label]="i18n.t('inv.totalHt')">{{ i18n.money(inv.subTotalHt) }}</td>
                    <td></td>
                    <td></td>
                  </tr>
                  <tr>
                    <td>{{ i18n.t('inv.taxTotal') }}</td>
                    <td></td>
                    <td></td>
                    @if (hasDays()) {
                      <td></td>
                    }
                    @if (hasDiscount()) {
                      <td></td>
                    }
                    <td></td>
                    <td class="num mono" [attr.data-label]="i18n.t('inv.taxTotal')">{{ i18n.money(inv.taxTotal) }}</td>
                    <td></td>
                    <td></td>
                  </tr>
                  <tr>
                    <td>{{ i18n.t('inv.totalTtc') }}</td>
                    <td></td>
                    <td></td>
                    @if (hasDays()) {
                      <td></td>
                    }
                    @if (hasDiscount()) {
                      <td></td>
                    }
                    <td></td>
                    <td class="num mono" [attr.data-label]="i18n.t('inv.totalTtc')">{{ i18n.money(inv.grandTotalTtc) }}</td>
                    <td></td>
                    <td></td>
                  </tr>
                </tfoot>
              </table>
            </div>
          </section>

          <!-- ---------- what was written against it ---------- -->
          <section class="bo-panel">
            <div class="bo-panel__head">
              <h2 class="bo-panel__title">{{ i18n.t('inv.view.' + side()) }}</h2>
              @if (side() === 'receipts') {
                <p class="bo-panel__note">{{ i18n.t('inv.view.receiptsHint') }}</p>
              }
            </div>
            <div class="bo-panel__body no-print">
              <nav class="bo-chips" [attr.aria-label]="i18n.t('inv.view.receipts')">
                @for (choice of sides; track choice) {
                  <button
                    type="button"
                    [class.is-on]="side() === choice"
                    [attr.aria-current]="side() === choice ? 'true' : null"
                    (click)="side.set(choice)"
                  >
                    {{ i18n.t('inv.view.' + choice) }}
                  </button>
                }
              </nav>
            </div>

            @if (side() === 'creditNotes') {
              <div class="bo-scroll">
                <table class="bo-data" [attr.aria-label]="i18n.t('inv.view.creditNotes')">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('inv.number') }}</th>
                      <th>{{ i18n.t('receipt.date') }}</th>
                      <th>{{ i18n.t('inv.view.reason') }}</th>
                      <th class="num">{{ i18n.t('common.total') }}</th>
                      <th class="no-print">{{ i18n.t('common.actions') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (note of creditNotes(); track note.id) {
                      <tr>
                        <td class="mono" [attr.data-label]="i18n.t('inv.number')">{{ note.number }}</td>
                        <td class="mono" [attr.data-label]="i18n.t('receipt.date')">{{ day(note.issuedAtUtc) }}</td>
                        <td [attr.data-label]="i18n.t('inv.view.reason')">{{ note.reason }}</td>
                        <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('common.total')">{{ i18n.money(note.grandTotalTtc) }}</td>
                        <td class="bo-cell-actions no-print" [attr.data-label]="i18n.t('common.actions')">
                          <button type="button" class="btn-ghost bo-small" (click)="printer.open('credit-note', note.id)">{{ i18n.t('common.print') }}</button>
                        </td>
                      </tr>
                    } @empty {
                      <tr>
                        <td colspan="5" class="bo-empty">
                          @if (sideBusy()) {
                            <strong>{{ i18n.t('common.loading') }}</strong>
                            <span class="bo-loading__rows" aria-hidden="true"></span>
                          } @else {
                            <strong>{{ i18n.t('inv.view.noCreditNotes') }}</strong>
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                  @if (creditNotes().length) {
                    <tfoot>
                      <tr>
                        <td>{{ i18n.t('inv.credited') }}</td>
                        <td></td>
                        <td></td>
                        <td class="num mono" [attr.data-label]="i18n.t('inv.credited')">{{ i18n.money(creditedTotal()) }}</td>
                        <td class="no-print"></td>
                      </tr>
                    </tfoot>
                  }
                </table>
              </div>
            }

            @if (side() === 'receipts') {
              <div class="bo-scroll">
                <table class="bo-data" [attr.aria-label]="i18n.t('inv.view.receipts')">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('inv.number') }}</th>
                      <th>{{ i18n.t('inv.rcpt.receivedOn') }}</th>
                      <th>{{ i18n.t('inv.rcpt.method') }}</th>
                      <th>{{ i18n.t('common.state') }}</th>
                      <th class="num">{{ i18n.t('inv.view.onThisInvoice') }}</th>
                      <th>{{ i18n.t('common.actions') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (row of receipts(); track row.voucher.id) {
                      <tr>
                        <td class="mono" [attr.data-label]="i18n.t('inv.number')">{{ row.voucher.number }}</td>
                        <td class="mono" [attr.data-label]="i18n.t('inv.rcpt.receivedOn')">{{ day(row.voucher.receivedAtUtc) }}</td>
                        <td [attr.data-label]="i18n.t('inv.rcpt.method')">{{ methodName(row.voucher.methodKey) }}</td>
                        <td [attr.data-label]="i18n.t('common.state')">
                          <span class="bo-state" [class]="receiptTone(row.voucher.status)">{{ word('inv.rcpt.state', row.voucher.status) }}</span>
                        </td>
                        <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('inv.view.onThisInvoice')">
                          {{ i18n.money(row.onThisInvoice) }}
                        </td>
                        <td class="bo-cell-actions no-print" [attr.data-label]="i18n.t('common.actions')">
                          <button
                            type="button"
                            class="btn-ghost bo-small"
                            (click)="openReceipt.emit({ id: row.voucher.id, customerId: row.voucher.customerId })"
                          >
                            {{ i18n.t('common.view') }}
                          </button>
                        </td>
                      </tr>
                    } @empty {
                      <tr>
                        <td colspan="6" class="bo-empty">
                          @if (sideBusy()) {
                            <strong>{{ i18n.t('common.loading') }}</strong>
                            <span class="bo-loading__rows" aria-hidden="true"></span>
                          } @else {
                            <strong>{{ i18n.t('inv.view.noReceipts') }}</strong>
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
          </section>
        </div>

        <!-- ---------- what can be done about it ---------- -->
        <section class="bo-panel no-print">
          <div class="bo-panel__foot">
            <p class="bo-panel__note foot-note">{{ i18n.t('inv.view.noEdit') }}</p>
            <button type="button" class="btn-ghost" (click)="print()">{{ i18n.t('common.print') }}</button>
            <button type="button" class="btn-ghost" (click)="openAccount.emit(inv.customerId)">{{ i18n.t('inv.openAccount') }}</button>
            @if (canCredit()) {
              <button type="button" class="btn-ghost" (click)="creditNote.emit({ id: inv.id, customerId: inv.customerId })">
                {{ i18n.t('inv.view.newCreditNote') }}
              </button>
            } @else {
              <span class="bo-state">{{ i18n.t('inv.view.fullyCredited') }}</span>
            }
            @if (allocatableAmount() > 0) {
              <button type="button" class="btn-primary" (click)="collect.emit({ customerId: inv.customerId, invoiceId: inv.id })">
                {{ i18n.t('inv.collect') }}
              </button>
            } @else {
              <span class="bo-state">{{ i18n.t('inv.view.nothingAllocatable') }}</span>
            }
          </div>
        </section>
      } @else if (busy()) {
        <p class="bo-loading">
          <span class="bo-loading__word">{{ i18n.t('common.loading') }}</span>
          <span class="bo-loading__rows" aria-hidden="true"></span>
        </p>
      }
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss'],
  styles: [
    `
      /* The shared rule that separates two marks applies inside a table cell; a panel title
         holding a word, a number and a name needs the same gap, for the same reason — a plain
         margin between two inline runs lands outside the pair under RTL. */
      .bo-panel__title > * + * {
        margin-inline-start: 0.45rem;
      }

      /* The sentence about the rule sits at the start of the foot and pushes the buttons to
         the end; a foot is a flex row, so the note takes the room the buttons leave. */
      .foot-note {
        margin-inline-end: auto;
        align-self: center;
      }
    `,
  ],
})
export class ReceivableInvoiceComponent implements OnInit {
  private readonly api = inject(ReceivablesApi);
  readonly i18n = inject(I18nService);

  /** MS-34: a job invoice's parts or labour line in the reader's language; any other line as stored. */
  lineName(name: string): string {
    return invoiceLineName(name, (key, params) => this.i18n.t(key, params));
  }
  private readonly regional = inject(RegionalService);
  readonly printer = inject(PrintService);

  /**
   * Whether this invoice is itself the economic event and posted to the customer's account: one raised directly, or one
   * raised from a customer maintenance job (Phase M5a), whose job posted nothing. Over a sale or a rental note it is paper.
   */
  posts(origin: string): boolean {
    return origin === 'Direct' || origin === 'MaintenanceJob';
  }
  private readonly admin = inject(AdminService, { optional: true });

  readonly id = input.required<string>();
  readonly customerId = input.required<string>();
  readonly customers = input<AdminCustomer[]>([]);
  readonly branches = input<AdminBranch[]>([]);
  readonly methods = input<AdminPaymentMethod[]>([]);
  private readonly loadedMethods = signal<AdminPaymentMethod[]>([]);
  /** A sentence about what just happened here (raised, credited), composed by the shell. */
  readonly done = input<string | null>(null);
  /** The server's raw credit-limit sentence from the 201 envelope; localised below. */
  readonly warning = input<string | null>(null);

  readonly back = output<string>();
  readonly creditNote = output<DocRef>();
  readonly collect = output<CollectRef>();
  readonly openReceipt = output<DocRef>();
  readonly openAccount = output<string>();

  readonly sides: Side[] = ['creditNotes', 'receipts'];
  readonly side = signal<Side>('creditNotes');

  readonly invoice = signal<Invoice | null>(null);
  readonly creditNotes = signal<CreditNote[]>([]);
  readonly receipts = signal<ReceiptRow[]>([]);
  readonly busy = signal(true);
  readonly sideBusy = signal(true);
  readonly notFound = signal(false);
  readonly error = signal<string | null>(null);

  readonly late = computed(() => {
    const inv = this.invoice();
    return !!inv && inv.daysOverdue > 0 && inv.outstanding > 0;
  });

  /** Outstanding less the paper already claimed — what a new receipt may still be written against. */
  readonly allocatableAmount = computed(() => {
    const inv = this.invoice();
    return inv ? allocatable(inv) : 0;
  });

  /** A credit note needs something left on at least one line; the server refuses otherwise. */
  readonly canCredit = computed(() => (this.invoice()?.lines ?? []).some((l) => l.qty > l.creditedQty));

  /** A discount column only when a line has one: the room goes to the days and the money. */
  readonly hasDiscount = computed(() => (this.invoice()?.lines ?? []).some((l) => l.discount > 0));

  /** A days column only when a line is priced by the day; a sale shows the columns it always did. */
  readonly hasDays = computed(() => (this.invoice()?.lines ?? []).some((l) => (l.days ?? 0) > 0));

  /** The way of paying in the reader's language, or the stored key for a method since removed. */
  readonly paymentMode = computed(() => {
    const key = this.invoice()?.paymentMethodKey;
    if (!key) return '';
    return this.methodName(key);
  });

  readonly creditedTotal = computed(() => this.creditNotes().reduce((sum, n) => sum + n.grandTotalTtc, 0));

  readonly customerName = computed(() => {
    const inv = this.invoice();
    return inv?.customerName || this.customers().find((c) => c.id === this.customerId())?.fullName || '—';
  });

  /**
   * The warning in the reader's language and currency. The server's sentence carries the two
   * figures in brackets; when its shape changes the plain sentence is the honest fallback, not
   * figures re-derived from a customers list that may have moved on since.
   */
  readonly creditWarning = computed(() => {
    const raw = this.warning();
    if (!raw) return null;
    const parsed = parseCreditWarning(raw);
    return parsed
      ? this.i18n.t('inv.creditLimitWarn', { balance: this.i18n.money(parsed.balance), limit: this.i18n.money(parsed.limit) })
      : this.i18n.t('inv.creditLimitWarnRaw');
  });

  async ngOnInit(): Promise<void> {
    // The head and the lines render as soon as the invoice lands; the two side tables fill in
    // behind it rather than holding the whole page for the slowest of three calls.
    const side = this.loadSide();
    const methods = this.loadMethods();
    try {
      const inv = await this.api.getInvoice(this.id());
      this.invoice.set(inv);
      if (inv.paymentMethodKey && !this.methods().some((m) => m.key === inv.paymentMethodKey) && !this.loadedMethods().some((m) => m.key === inv.paymentMethodKey)) {
        await this.loadMethods(true);
      }
    } catch (e) {
      // An unknown id answers 404 with an empty body, which adminErrorMessage can only call "HTTP 404".
      if (e instanceof ApiError && e.status === 404) this.notFound.set(true);
      else this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
    await Promise.all([side, methods]);
  }

  private async loadMethods(force = false): Promise<void> {
    if (!this.admin) return;
    if (!force && this.methods().length > 0) return;
    try {
      this.loadedMethods.set(await this.admin.listPaymentMethods());
    } catch {
      // catch errors -> just show the key
    }
  }

  private async loadSide(): Promise<void> {
    try {
      const [creditNotes, vouchers] = await Promise.all([
        this.api.listCreditNotes({ invoiceId: this.id(), take: RECEIPT_TAKE }),
        this.api.listReceipts({ customerId: this.customerId(), take: RECEIPT_TAKE }),
      ]);
      this.creditNotes.set(creditNotes);
      this.receipts.set(this.allocatedHere(vouchers));
    } catch (e) {
      // An unknown invoice already has its own sentence; a second banner would only repeat it.
      if (!this.notFound()) this.error.set(adminErrorMessage(e));
    } finally {
      this.sideBusy.set(false);
    }
  }

  /** Only the vouchers that named this invoice, each with the amount they wrote on it. */
  private allocatedHere(vouchers: ReceiptVoucher[]): ReceiptRow[] {
    const id = this.id();
    const rows: ReceiptRow[] = [];
    for (const voucher of vouchers) {
      const onThisInvoice = voucher.allocations.filter((a) => a.invoiceId === id).reduce((sum, a) => sum + a.amount, 0);
      if (onThisInvoice > 0) rows.push({ voucher, onThisInvoice });
    }
    return rows;
  }

  tone(status: InvoiceStatus): string {
    return INVOICE_TONE[status] ?? '';
  }

  receiptTone(status: ReceiptStatus): string {
    return RECEIPT_TONE[status] ?? '';
  }

  /** A server enum as a word, or raw when nobody has named it yet. */
  word(prefix: string, value: string): string {
    const key = stateKey(prefix, value);
    const text = this.i18n.t(key);
    return text === key ? value : text;
  }

  /** The income account as the word the business uses for it; a code nobody named prints raw. */
  accountWord(code: string): string {
    const key = accountWordKey(code);
    return key ? this.i18n.t(key) : code;
  }

  branchName(id: string): string {
    const branch = this.branches().find((b) => b.id === id);
    return branch ? localizedName(branch, this.i18n.lang()) : '—';
  }

  /** The tender's name for the reader; a key the shell did not load (deactivated since) prints raw. */
  methodName(key: string): string {
    const method = this.methods().find((m) => m.key === key) ?? this.loadedMethods().find((m) => m.key === key);
    return method ? localizedName(method, this.i18n.lang()) : key;
  }

  day(value: string | null): string {
    return formatDay(value ? withZ(value) : null, this.i18n.lang(), this.regional.settings().timeZone);
  }

  /** The invoice on A4 in its own tab (docs/07 §4.2); this working screen is not the paper. */
  print(): void {
    this.printer.open('invoice', this.id());
  }
}
