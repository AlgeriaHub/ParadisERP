import { Component, OnInit, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { PageChrome } from '../shell/page-chrome';
import { FormsModule } from '@angular/forms';
import { AdminBranch, AdminCustomer, AdminPaymentMethod, adminErrorMessage } from '../../../core/admin.service';
import { ApiError } from '../../../core/api.service';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService, localized } from '../../../core/regional.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { actionRequest } from '../../../ui/confirm-requests';
import { PrintService } from '../../print/print.service';
import { TouchedFields } from '../backoffice-forms';
import { formatDay, localizedName } from '../backoffice-format';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { DocRef, DueWord, dueWord, withZ } from './receivable-view';
import { Invoice, ReceiptVoucher, ReceivablesApi, SettleReceiptRequest } from './receivables-api.service';

type Outcome = SettleReceiptRequest['status'];

/** The two ways a pending voucher can end; the chips carry the treasury's own words. */
const OUTCOMES: readonly { value: Outcome; key: string }[] = [
  { value: 'Confirmed', key: 'treasury.markCollected' },
  { value: 'Bounced', key: 'treasury.markBounced' },
];

/**
 * Did this money actually arrive, what was it written against, and — if it is still paper —
 * do I confirm it or declare it bounced?
 *
 * <p>The voucher's status is the whole story. Pending: the allocations are claims that reserve
 * the invoices without settling them, nothing has been posted. Confirmed: the money is in the
 * bank or the cash box, the customer account is reduced, the invoices are paid. Bounced: nothing
 * had posted, so there is nothing to reverse; the claims simply stop counting.</p>
 *
 * <p>Settling is the one act on this screen and it happens once, either way, with no way back —
 * so it is stated before it is done and has to be acknowledged. A voucher already settled shows
 * when and with what note, and offers nothing: there is no void, no edit, no un-settle.</p>
 */
@Component({
  selector: 'app-receivable-receipt',
  standalone: true,
  imports: [FormsModule, StatusComponent],
  template: `
    <div class="bo-stack">
      @if (!addressed()) {
        <div class="bo-actions bo-actions--start no-print">
          <button type="button" class="btn-ghost bo-small" (click)="back.emit(customerId())">
            {{ i18n.t('inv.back.account') }}
          </button>
        </div>
      }

      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      @if (shownDone(); as text) {
        <p class="notice ok" role="status">{{ text }}</p>
      }

      @if (notFound()) {
        <p class="notice danger" role="alert">{{ i18n.t('inv.rcpt.notFound') }}</p>
      } @else {
        @if (voucher(); as v) {
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">
              <span class="mono">{{ i18n.t('inv.rcpt.view.title', { n: v.number }) }}</span>
              <span class="bo-soft">{{ customerName(v) }}</span>
            </h2>
            <p class="bo-meta bo-panel__note">
              <span class="bo-states">
                <app-status [tone]="stateTone(v)">{{ stateWord(v.status) }}</app-status>
                @if (v.status === 'Pending' && due(v) !== 'none') {
                  <app-status [tone]="dueTone(v)">{{ i18n.t('treasury.due.' + due(v)) }}</app-status>
                }
              </span>
              <span>{{ i18n.t('inv.rcpt.receivedOn') }} <span class="mono">{{ day(v.receivedAtUtc) }}</span></span>
              <span>{{ i18n.t('rent.branch') }} {{ branchName(v.branchId) }}</span>
              <span>{{ i18n.t('inv.rcpt.method') }} {{ methodName(v.methodKey) }} <span class="bo-soft">{{ behaviorWord(v.behavior) }}</span></span>
              @if (v.reference) {
                <span>{{ i18n.t('treasury.reference') }} <span class="mono">{{ v.reference }}</span></span>
              }
              @if (v.instrumentNumber) {
                <span>{{ i18n.t('inv.rcpt.instrumentNumber') }} <span class="mono">{{ v.instrumentNumber }}</span></span>
              }
              @if (v.instrumentBank) {
                <span>{{ i18n.t('pay.instrumentBank') }} {{ v.instrumentBank }}</span>
              }
              @if (v.instrumentDueDate) {
                <span>{{ i18n.t('pay.instrumentDueDate') }} <span class="mono">{{ day(v.instrumentDueDate) }}</span></span>
              }
              @if (v.purpose) {
                <span>{{ i18n.t('inv.rcpt.form.purpose') }} {{ v.purpose }}</span>
              }
              @if (v.notes) {
                <span>{{ i18n.t('rent.notes') }} {{ v.notes }}</span>
              }
              @if (v.confirmedAtUtc) {
                <span>{{ i18n.t('inv.rcpt.view.settledOn') }} <span class="mono">{{ i18n.dateTime(withZ(v.confirmedAtUtc)) }}</span></span>
              }
              @if (v.statusNote) {
                <span>{{ i18n.t('inv.rcpt.view.statusNote') }} {{ v.statusNote }}</span>
              }
            </p>
          </div>

          <div class="bo-panel__body">
            <p class="notice">
              {{ i18n.t(statusNoteKey(v)) }}
              {{ i18n.t(v.behavior === 'Cash' ? 'inv.rcpt.landsCash' : 'inv.rcpt.landsBank') }}
            </p>
          </div>

          <div class="bo-panel__body">
            <div class="bo-metrics">
              <div class="bo-metric bo-metric--lead">
                <span class="bo-metric__label">{{ i18n.t('pay.amount') }}</span>
                <strong class="bo-metric__value mono">{{ i18n.money(v.amount) }}</strong>
              </div>
              <div class="bo-metric">
                <span class="bo-metric__label">{{ i18n.t('inv.rcpt.allocated') }}</span>
                <strong class="bo-metric__value mono">{{ i18n.money(v.allocated) }}</strong>
              </div>
              @if (v.kind === 'Advance') {
                <!-- M7b: what the advance still holds on 419, in words beside the figure. -->
                <div class="bo-metric">
                  <span class="bo-metric__label">{{ i18n.t('adv.view.open') }}</span>
                  <strong class="bo-metric__value mono">{{ i18n.money(v.advanceOpen ?? 0) }}</strong>
                  <span class="bo-metric__note"><span>{{ i18n.t('adv.view.openHint') }}</span></span>
                </div>
                @if ((v.advanceTaxAmount ?? 0) > 0) {
                  <div class="bo-metric">
                    <span class="bo-metric__label">{{ i18n.t('adv.view.tax', { tax: taxName(), rate: v.advanceTaxRate ?? 0 }) }}</span>
                    <strong class="bo-metric__value mono">{{ i18n.money(v.advanceTaxAmount ?? 0) }}</strong>
                    <span class="bo-metric__note"><span>{{ i18n.t('adv.view.taxHint', { tax: taxName() }) }}</span></span>
                  </div>
                }
              } @else if (v.unallocated > 0) {
                <div class="bo-metric">
                  <span class="bo-metric__label">{{ i18n.t('inv.rcpt.unallocated') }}</span>
                  <strong class="bo-metric__value mono">{{ i18n.money(v.unallocated) }}</strong>
                  <span class="bo-metric__note"><span>{{ i18n.t('inv.rcpt.unallocatedHint') }}</span></span>
                </div>
              }
              @if ((v.kept ?? 0) > 0) {
                <!-- SAL-13: what of it was kept as other income on a cancelled order. -->
                <div class="bo-metric">
                  <span class="bo-metric__label">{{ i18n.t('adv.view.keptTotal') }}</span>
                  <strong class="bo-metric__value mono">{{ i18n.money(v.kept ?? 0) }}</strong>
                  @for (a of keptRows(v); track a.id) {
                    <span class="bo-metric__note"><span>{{ day(a.appliedAtUtc) }} — {{ a.reason }}</span></span>
                  }
                </div>
              }
            </div>
            @if (v.kind === 'Advance' && v.maintenanceJobNumber) {
              <p class="bo-meta"><span>{{ i18n.t('adv.form.job') }} <span class="mono">{{ v.maintenanceJobNumber }}</span></span></p>
            }
          </div>
        </section>

        @if (v.kind === 'Advance') {
          <section class="bo-panel" aria-labelledby="rv-adv-title">
            <div class="bo-panel__head">
              <h2 id="rv-adv-title" class="bo-panel__title">{{ i18n.t('adv.view.title') }}</h2>
              <p class="bo-panel__note">{{ i18n.t('adv.view.lead') }}</p>
            </div>
            <div class="bo-scroll">
              <table class="bo-data">
                <thead>
                  <tr>
                    <th>{{ i18n.t('adv.view.date') }}</th>
                    <th>{{ i18n.t('inv.invoice') }}</th>
                    <th class="num">{{ i18n.t('pay.amount') }}</th>
                    <th class="num">{{ i18n.t('adv.view.taxReleased', { tax: taxName() }) }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (a of v.advanceApplications ?? []; track a.id) {
                    <tr>
                      <td class="mono" [attr.data-label]="i18n.t('adv.view.date')">{{ day(a.appliedAtUtc) }}</td>
                      <td [attr.data-label]="i18n.t('inv.invoice')">
                        @if (a.kind === 'Kept') {
                          <!-- SAL-13: kept on a cancelled order, with its reason. -->
                          <span>{{ i18n.t('adv.view.kept', { account: a.incomeAccountCode ?? '—' }) }}</span>
                          @if (a.reason) {
                            <span class="bo-soft"> — {{ a.reason }}</span>
                          }
                        } @else if (a.invoiceNumber) {
                          <span class="mono">{{ a.invoiceNumber }}</span>
                        } @else {
                          {{ i18n.t('adv.view.released') }}
                        }
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('pay.amount')">{{ i18n.money(a.amount) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('adv.view.taxReleased', { tax: taxName() })">
                        @if (a.kind === 'Kept') {
                          <span class="bo-soft">{{ i18n.t('adv.view.taxStays', { amount: i18n.money(a.taxAmount) }) }}</span>
                        } @else {
                          {{ i18n.money(a.taxAmount) }}
                        }
                      </td>
                    </tr>
                  } @empty {
                    <tr>
                      <td colspan="4" class="bo-empty"><strong>{{ i18n.t('adv.view.noneYet') }}</strong></td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            @if (v.status === 'Confirmed' && (v.advanceOpen ?? 0) > 0) {
              <div class="bo-panel__body bo-form no-print">
                <div class="bo-fields">
                  <div class="bo-field">
                    <label for="rv-adv-invoice">{{ i18n.t('inv.invoice') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
                    <select id="rv-adv-invoice" [ngModel]="applyInvoiceId()" (ngModelChange)="pickApplyInvoice($event)"
                      aria-describedby="rv-adv-invoice-help">
                      <option [ngValue]="null">{{ i18n.t('adv.view.toAccount') }}</option>
                      @for (inv of openInvoices(); track inv.id) {
                        <option [ngValue]="inv.id">{{ inv.number }} · {{ i18n.money(inv.outstanding) }}</option>
                      }
                    </select>
                    <span class="bo-hint" id="rv-adv-invoice-help">{{ i18n.t('adv.view.invoiceHint') }}</span>
                  </div>
                  <div class="bo-field">
                    <label for="rv-adv-amount">{{ i18n.t('pay.amount') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
                    <input id="rv-adv-amount" type="number" min="0" [step]="regional.moneyStep()" class="num mono"
                      [ngModel]="applyAmount()" (ngModelChange)="applyAmount.set($event === '' || $event === null ? null : +$event)" />
                  </div>
                </div>
              </div>
              <div class="bo-panel__foot no-print">
                <button type="button" class="btn-primary" [disabled]="busy() || !(applyAmount() ?? 0)" (click)="applyAdvance()">
                  {{ i18n.t(applyInvoiceId() ? 'adv.view.apply' : 'adv.view.release') }}
                </button>
              </div>
            }
          </section>
        }

        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('inv.rcpt.view.allocations') }}</h2>
            @if (v.status === 'Pending') {
              <p class="bo-panel__note">{{ i18n.t('inv.claimedHint') }}</p>
            }
          </div>
          <div class="bo-scroll">
            <table class="bo-data">
              <thead>
                <tr>
                  <th>{{ i18n.t('inv.invoice') }}</th>
                  <th class="num">{{ i18n.t('pay.amount') }}</th>
                  <th class="no-print">{{ i18n.t('common.actions') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (a of v.allocations; track a.invoiceId) {
                  <tr>
                    <td class="mono">{{ a.invoiceNumber || '—' }}</td>
                    <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('pay.amount')">{{ i18n.money(a.amount) }}</td>
                    <td class="bo-cell-actions no-print" [attr.data-label]="i18n.t('common.actions')">
                      <button type="button" class="btn-ghost bo-small" (click)="openInvoice.emit({ id: a.invoiceId, customerId: v.customerId })">
                        {{ i18n.t('common.view') }}
                      </button>
                    </td>
                  </tr>
                } @empty {
                  <tr>
                    <td colspan="3" class="bo-empty">
                      <strong>{{ i18n.t('inv.rcpt.view.noAllocations') }}</strong>
                      <span>{{ i18n.t(v.kind === 'Advance' ? 'adv.view.openHint' : 'inv.rcpt.unallocatedHint') }}</span>
                    </td>
                  </tr>
                }
              </tbody>
              <tfoot>
                <tr>
                  <td>{{ i18n.t('inv.rcpt.allocated') }}</td>
                  <td class="num mono" [attr.data-label]="i18n.t('inv.rcpt.allocated')">{{ i18n.money(v.allocated) }}</td>
                  <td class="no-print"></td>
                </tr>
                @if (v.unallocated > 0 && v.kind !== 'Advance') {
                  <tr>
                    <td>{{ i18n.t('inv.rcpt.unallocated') }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('inv.rcpt.unallocated')">{{ i18n.money(v.unallocated) }}</td>
                    <td class="no-print"></td>
                  </tr>
                }
              </tfoot>
            </table>
          </div>
        </section>

        @if (v.status === 'Pending') {
          <section class="bo-panel no-print">
            <div class="bo-panel__head">
              <h2 class="bo-panel__title">{{ i18n.t('inv.rcpt.settle.title') }}</h2>
            </div>
            <div class="bo-panel__body bo-form">
              <p class="notice warn">
                <strong>{{ i18n.t('inv.rcpt.settle.final') }}</strong>
                {{ i18n.t('inv.rcpt.settle.note') }}
              </p>

              <div class="bo-group">
                <h3 class="bo-group__title">
                  {{ i18n.t('inv.rcpt.settle.outcome') }} <span class="bo-req">{{ i18n.t('common.required') }}</span>
                </h3>
                <nav class="bo-chips" [attr.aria-label]="i18n.t('inv.rcpt.settle.outcome')">
                  @for (o of outcomes; track o.value) {
                    <button
                      type="button"
                      [class.is-on]="outcomeChoice() === o.value"
                      [attr.aria-pressed]="outcomeChoice() === o.value ? 'true' : 'false'"
                      (click)="pickOutcome(o.value)"
                    >
                      {{ i18n.t(o.key) }}
                    </button>
                  }
                </nav>
                @if (touched.missing('outcome', outcomeChoice())) {
                  <span class="bo-error" role="alert">{{ i18n.t('inv.problem.outcomeRequired') }}</span>
                }
              </div>

              <div class="bo-group">
                <div class="bo-fields">
                  <div class="bo-field bo-field--wide" [class.is-invalid]="reasonMissing()">
                    <!-- FR-2B: a bounce says why; for a collection the note stays optional. -->
                    <label for="rv-note">
                      {{ i18n.t(outcomeChoice() === 'Bounced' ? 'fin.bounce.reason' : 'inv.rcpt.view.statusNote') }}<span class="bo-req">{{
                        i18n.t(outcomeChoice() === 'Bounced' ? 'common.required' : 'common.optional')
                      }}</span>
                    </label>
                    <textarea
                      id="rv-note"
                      rows="2"
                      maxlength="400"
                      [ngModel]="note()"
                      (ngModelChange)="note.set($event)"
                      [attr.aria-invalid]="reasonMissing() ? 'true' : null"
                      [attr.aria-describedby]="reasonMissing() ? 'rv-note-error' : null"
                    ></textarea>
                    @if (reasonMissing()) {
                      <span id="rv-note-error" class="bo-error" role="alert">{{ i18n.t('fin.bounce.reasonRequired') }}</span>
                    } @else {
                      <span class="bo-hint">{{ i18n.t('inv.rcpt.settle.noteHint') }}</span>
                    }
                  </div>
                </div>
              </div>

              <div class="bo-check">
                <input
                  id="rv-ack"
                  type="checkbox"
                  [ngModel]="acknowledged()"
                  (ngModelChange)="acknowledged.set($event)"
                  [attr.aria-invalid]="ackMissing() ? 'true' : null"
                />
                <label for="rv-ack">{{ i18n.t('inv.rcpt.settle.ack') }}</label>
              </div>
              @if (ackMissing()) {
                <span class="bo-error" role="alert">{{ i18n.t('inv.problem.ackRequired') }}</span>
              }
            </div>
            <div class="bo-panel__foot">
              @if (showAccount()) {
                <button type="button" class="btn-ghost" (click)="openAccount.emit(v.customerId)">{{ i18n.t('inv.openAccount') }}</button>
              }
              <button type="button" class="btn-ghost" (click)="print()">{{ i18n.t('common.print') }}</button>
              <button type="button" class="btn-primary" [disabled]="busy() || !canSettle()" (click)="settle()">
                {{ i18n.t('inv.rcpt.settle.save') }}
              </button>
            </div>
          </section>
        } @else {
          <section class="bo-panel no-print">
            <div class="bo-panel__body">
              <p class="bo-hint">{{ i18n.t('inv.rcpt.view.noUndo') }}</p>
            </div>
            <div class="bo-panel__foot">
              @if (showAccount()) {
                <button type="button" class="btn-ghost" (click)="openAccount.emit(v.customerId)">{{ i18n.t('inv.openAccount') }}</button>
              }
              <button type="button" class="btn-ghost" (click)="print()">{{ i18n.t('common.print') }}</button>
            </div>
          </section>
        }
        } @else if (!error()) {
          <p class="bo-loading">
            <span class="bo-loading__word">{{ i18n.t('common.loading') }}</span>
            <span class="bo-loading__rows" aria-hidden="true"></span>
          </p>
        }
      }
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss'],
  styles: [
    `
      /* The number and the customer are two facts in one heading; a gap, not punctuation,
         keeps them apart on either side of the bidi boundary. */
      .bo-panel__title .bo-soft {
        margin-inline-start: 0.6rem;
        font-weight: 400;
      }
    `,
  ],
})
export class ReceivableReceiptComponent implements OnInit {
  private readonly api = inject(ReceivablesApi);
  readonly i18n = inject(I18nService);
  readonly regional = inject(RegionalService);
  private readonly confirm = inject(ConfirmService);
  private readonly printer = inject(PrintService);

  readonly id = input.required<string>();
  readonly customerId = input.required<string>();
  readonly customers = input<AdminCustomer[]>([]);
  readonly branches = input<AdminBranch[]>([]);
  readonly methods = input<AdminPaymentMethod[]>([]);
  /** A sentence composed by the shell after a save that landed here. */
  readonly done = input<string | null>(null);
  /** Preset the outcome when the reader came from a list's "bounced" button. */
  readonly outcome = input<'Bounced' | null>(null);

  /**
   * The receipt is its own page (`finance/receipts/<id>`): the header names it and the
   * breadcrumb is the way back, so the "back to the account" button is not drawn.
   */
  readonly addressed = input(false);
  /** Whether this reader may open the customer's account (the nav table's rule, core/nav.ts). */
  readonly showAccount = input(true);

  readonly back = output<string>();
  readonly openInvoice = output<DocRef>();
  readonly openAccount = output<string>();

  private readonly chrome = inject(PageChrome, { optional: true });

  constructor() {
    effect(() => {
      if (!this.addressed()) return;
      const v = this.voucher();
      const missing = this.notFound() || !!this.error();
      untracked(() => {
        this.chrome?.heading.set(v ? v.number : missing ? '—' : this.i18n.t('common.loading'));
        this.chrome?.description.set(v ? this.customerName(v) : null);
        this.chrome?.trail.set([]);
      });
    });
  }

  readonly outcomes = OUTCOMES;
  readonly touched = new TouchedFields();
  readonly withZ = withZ;

  readonly voucher = signal<ReceiptVoucher | null>(null);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly notFound = signal(false);
  readonly settledText = signal<string | null>(null);
  readonly outcomeChoice = signal<Outcome | null>(null);
  readonly note = signal('');
  readonly acknowledged = signal(false);

  /** What the ok notice says: the shell's sentence on arrival, this screen's own after a settle. */
  readonly shownDone = computed(() => this.settledText() ?? this.done());

  readonly canSettle = computed(() => this.outcomeChoice() !== null && this.acknowledged());

  /** A bounce with no reason, once the reader has tried to save it (FR-2B). */
  reasonMissing(): boolean {
    return this.outcomeChoice() === 'Bounced' && this.touched.missing('note', this.note().trim());
  }

  async ngOnInit(): Promise<void> {
    this.outcomeChoice.set(this.outcome());
    await this.load();
  }

  private async load(): Promise<void> {
    this.error.set(null);
    try {
      this.voucher.set(await this.api.getReceipt(this.id()));
      await this.loadOpenInvoices();
    } catch (e) {
      // The 404 comes with an empty body, so the generic message would read "HTTP 404".
      if (e instanceof ApiError && e.status === 404) this.notFound.set(true);
      else this.error.set(adminErrorMessage(e));
    }
  }

  // ---------- M7b: an advance held on 419 ----------

  /** The customer's invoices still owing something, for applying the advance; loaded only for a confirmed advance. */
  readonly openInvoices = signal<Invoice[]>([]);
  readonly applyInvoiceId = signal<string | null>(null);
  readonly applyAmount = signal<number | null>(null);

  private async loadOpenInvoices(): Promise<void> {
    const v = this.voucher();
    if (!v || v.kind !== 'Advance' || v.status !== 'Confirmed' || !(v.advanceOpen ?? 0)) {
      this.openInvoices.set([]);
      return;
    }
    try {
      const all = await this.api.listInvoices({ customerId: v.customerId, take: 200 });
      this.openInvoices.set(all.filter((i) => i.outstanding > 0));
    } catch {
      // Without the invoices the advance can still be released to the account.
      this.openInvoices.set([]);
    }
    this.pickApplyInvoice(this.openInvoices()[0]?.id ?? null);
  }

  /** The amount follows the choice: what the advance holds, no more than the invoice still owes. */
  pickApplyInvoice(id: string | null): void {
    this.applyInvoiceId.set(id);
    const open = this.voucher()?.advanceOpen ?? 0;
    const owed = this.openInvoices().find((i) => i.id === id)?.outstanding;
    this.applyAmount.set(owed === undefined ? open : Math.min(open, owed));
  }

  taxName(): string {
    return localized(this.regional.settings().taxNames, this.i18n.lang());
  }

  /** SAL-13: the parts of this receipt kept as other income, shown under a payment's "Kept" figure. */
  keptRows(v: ReceiptVoucher) {
    return v.kind === 'Advance' ? [] : (v.advanceApplications ?? []).filter((a) => a.kind === 'Kept');
  }

  async applyAdvance(): Promise<void> {
    const v = this.voucher();
    const amount = this.applyAmount();
    if (!v || !amount || amount <= 0) return;
    const invoice = this.openInvoices().find((i) => i.id === this.applyInvoiceId()) ?? null;
    const message = invoice
      ? this.i18n.t('adv.view.applyMessage', { n: v.number, invoice: invoice.number, amount: this.i18n.money(amount) })
      : this.i18n.t('adv.view.releaseMessage', { n: v.number, amount: this.i18n.money(amount) });
    const action = this.i18n.t(invoice ? 'adv.view.apply' : 'adv.view.release');
    if (!(await this.confirm.ask(actionRequest(this.i18n, action, message)))) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const updated = await this.api.applyAdvance(v.id, { invoiceId: invoice?.id ?? null, amount });
      this.voucher.set(updated);
      this.settledText.set(this.i18n.t(invoice ? 'adv.view.applied' : 'adv.view.releasedDone', { amount: this.i18n.money(amount) }));
      await this.loadOpenInvoices();
    } catch (e) {
      this.error.set(adminErrorMessage(e));
      await this.load();
    } finally {
      this.busy.set(false);
    }
  }

  pickOutcome(value: Outcome): void {
    this.outcomeChoice.set(value);
    this.touched.touch('outcome');
  }

  ackMissing(): boolean {
    return this.touched.missing('ack', this.acknowledged() ? 'ok' : '');
  }

  due(v: ReceiptVoucher): DueWord {
    return dueWord(v.instrumentDueDate, this.regional.settings().timeZone);
  }

  dueTone(v: ReceiptVoucher) {
    return toneOf('due', this.due(v));
  }

  stateTone(v: ReceiptVoucher) {
    return toneOf('payment', v.status);
  }

  stateWord(status: string): string {
    const key = 'inv.rcpt.state.' + status;
    const word = this.i18n.t(key);
    return word === key ? status : word;
  }

  behaviorWord(behavior: string): string {
    const key = 'inv.behavior.' + behavior;
    const word = this.i18n.t(key);
    return word === key ? behavior : word;
  }

  /** One sentence for what the status means for the debt. */
  statusNoteKey(v: ReceiptVoucher): string {
    switch (v.status) {
      case 'Confirmed':
        return 'inv.rcpt.view.confirmedNote';
      case 'Bounced':
        return 'inv.rcpt.view.bouncedNote';
      default:
        return 'inv.rcpt.view.pendingNote';
    }
  }

  customerName(v: ReceiptVoucher): string {
    return v.customerName || this.customers().find((c) => c.id === v.customerId)?.fullName || '—';
  }

  branchName(id: string): string {
    const branch = this.branches().find((b) => b.id === id);
    return branch ? localizedName(branch, this.i18n.lang()) : '—';
  }

  methodName(key: string): string {
    const method = this.methods().find((m) => m.key === key);
    return method ? localizedName(method, this.i18n.lang()) : key;
  }

  day(value: string | null): string {
    return formatDay(value ? withZ(value) : null, this.i18n.lang(), this.regional.settings().timeZone);
  }

  /** A5, two copies, in its own tab — offered while Pending too: a cheque's paper is wanted at hand-over. */
  print(): void {
    this.printer.open('receipt-voucher', this.id());
  }

  async settle(): Promise<void> {
    this.touched.touchAll('outcome', 'ack', 'note');
    const outcome = this.outcomeChoice();
    if (!outcome || !this.acknowledged()) return;
    // The server refuses a bounce with no reason (FR-2B); said here, under the field, first.
    if (outcome === 'Bounced' && !this.note().trim()) return;
    const current = this.voucher();
    if (!current) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const settled = await this.api.settleReceipt(current.id, { status: outcome, note: this.note().trim() || null });
      this.voucher.set(settled);
      this.settledText.set(
        this.i18n.t('inv.rcpt.settle.done', { n: settled.number, state: this.stateWord(settled.status) }),
      );
      this.touched.reset();
    } catch (e) {
      // "Already Confirmed / already Bounced" is the usual refusal: someone else got there first.
      // The reload shows the voucher as it now is, with the settle panel gone.
      this.error.set(adminErrorMessage(e));
      await this.load();
    } finally {
      this.busy.set(false);
    }
  }
}
