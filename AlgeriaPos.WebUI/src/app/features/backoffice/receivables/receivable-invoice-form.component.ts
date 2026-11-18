import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  AdminBranch,
  AdminCustomer,
  AdminPaymentMethod,
  AdminSale,
  AdminService,
  adminErrorMessage,
} from '../../../core/admin.service';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { actionRequest } from '../../../ui/confirm-requests';
import { FoldComponent } from '../../../ui/fold.component';
import { DateInputComponent } from '../../../ui/date-input.component';
import { RegionalService } from '../../../core/regional.service';
import { formatDay, localizedName } from '../backoffice-format';
import { TouchedFields } from '../backoffice-forms';
import { IssueNote, RentalContractRow, RentalsApi, ReturnNote } from '../rentals/rentals-api.service';
import {
  DEFAULT_DUE_DAYS,
  INVOICE_TAKE,
  SALES_WINDOW_DAYS,
  addDays,
  daysBetween,
  exclusiveEnd,
  midday,
  parseInputDate,
  roundTo,
  today,
  withZ,
} from './receivable-view';
import {
  CreateInvoiceRequest,
  CreateInvoiceResponse,
  ReceivablesApi,
  TaxMode,
} from './receivables-api.service';

/** What the invoice is raised over. The server decides by which source id is present; the screen decides by a chip. */
type Mode = 'sale' | 'rental';

/** An issue or return note offered for invoicing, flattened so one table can hold both kinds. */
interface NoteCandidate {
  kind: 'issue' | 'return';
  id: string;
  number: string;
  date: string;
  charges: number;
  invoiced: boolean;
  chargedNothing: boolean;
}

/**
 * What am I invoicing this customer for — a credit sale or a rental movement — and when is it due?
 *
 * <p>The invoice is issued from existing movements only; direct line typing is not available.
 * The lines and branch come from the linked document, and the invoice gives the debt a number,
 * tax regime and a due date.</p>
 */
@Component({
  selector: 'app-receivable-invoice-form',
  standalone: true,
  imports: [FoldComponent, FormsModule, DateInputComponent],
  template: `
    <div class="bo-stack">
      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }

      <section class="bo-panel">
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ i18n.t('inv.acct.newInvoice') }}</h2>
          <button type="button" class="btn-ghost bo-small" (click)="cancelled.emit(customerId())">
            {{ i18n.t('common.cancel') }}
          </button>
          @if (customerRow(); as c) {
            @if (customerId()) {
              <p class="bo-meta">
                <span>{{ c.fullName }}</span>
                <span class="mono bo-soft" dir="ltr">{{ c.code }}</span>
              </p>
            }
            <p class="bo-panel__note">
              @if (c.creditLimit > 0) {
                {{
                  i18n.t('inv.form.limitNote', {
                    balance: i18n.money(c.balance),
                    limit: i18n.money(c.creditLimit),
                  })
                }}
              } @else {
                {{
                  i18n.t('inv.form.limitNoteNone', {
                    balance: i18n.money(c.balance),
                  })
                }}
              }
            </p>
          }
        </div>

        @if (willExceed(); as projected) {
          <p class="notice warn">
            {{
              i18n.t('inv.creditLimitWillExceed', {
                balance: i18n.money(projected.balance),
                limit: i18n.money(projected.limit),
              })
            }}
          </p>
        }

        <div class="bo-panel__body bo-form">
          @if (!customerId()) {
            <div class="bo-group">
              <div class="bo-fields bo-fields--roomy">
                <div class="bo-field bo-field--wide" [class.is-invalid]="touched.missing('customer', customer())">
                  <label for="if-customer">
                    {{ i18n.t('common.customer') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <select
                    id="if-customer"
                    [ngModel]="customer()"
                    (ngModelChange)="setCustomer($event)"
                    (blur)="touched.touch('customer')"
                    [attr.aria-invalid]="touched.missing('customer', customer()) ? 'true' : null"
                    [attr.aria-describedby]="touched.missing('customer', customer()) ? 'if-customer-error' : null"
                  >
                    <option [ngValue]="null">—</option>
                    @for (c of customers(); track c.id) {
                      <option [ngValue]="c.id">{{ c.fullName }}</option>
                    }
                  </select>
                  @if (touched.missing('customer', customer())) {
                    <span id="if-customer-error" class="bo-error" role="alert">{{
                      i18n.t('inv.problem.customerRequired')
                    }}</span>
                  }
                </div>
              </div>
            </div>
          }

          <!-- Never folded: a folded mode hid which document the invoice is raised over, and
               with it why Save stayed shut. -->
          <div class="bo-group">
            <h3 class="bo-group__title">{{ i18n.t('inv.form.mode') }}</h3>
            <nav class="bo-chips" [attr.aria-label]="i18n.t('inv.form.mode')">
              @for (m of MODES; track m) {
                <button
                  type="button"
                  [class.is-on]="mode() === m"
                  [attr.aria-current]="mode() === m ? 'true' : null"
                  (click)="setMode(m)"
                >
                  {{ i18n.t('inv.form.mode.' + m) }}
                </button>
              }
            </nav>
            <p class="bo-group__note">
              {{ i18n.t('inv.form.modeNote.source') }}
            </p>
          </div>

          <app-fold class="bo-group" [heading]="i18n.t('inv.form.dates')" [open]="true">
            <div class="bo-fields bo-fields--roomy">
              <div class="bo-field">
                <label for="if-issued">{{ i18n.t('inv.issuedOn') }}</label>
                <app-date-input id="if-issued" [ngModel]="issuedOn()" (ngModelChange)="setIssuedOn($event)" />
              </div>
              <div class="bo-field" [class.is-invalid]="dueInvalid()">
                <label for="if-due">
                  {{ i18n.t('inv.dueOn') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                </label>
                <app-date-input
                  id="if-due"
                  [ngModel]="dueOn()"
                  (ngModelChange)="dueOn.set($event)"
                  (blur)="touched.touch('due')"
                  [ariaInvalid]="dueInvalid() ? 'true' : null"
                  [ariaDescribedby]="dueInvalid() ? 'if-due-error' : null"
                />
                <p class="bo-hint">{{ i18n.t('inv.form.dueHint') }}</p>
                @if (dueInvalid()) {
                  <span id="if-due-error" class="bo-error" role="alert">{{
                    i18n.t('inv.problem.' + dueProblem())
                  }}</span>
                }
              </div>
              @if (canUntax()) {
                <div class="bo-field">
                  <label for="if-tax">{{ i18n.t('inv.taxMode') }}</label>
                  <select id="if-tax" [ngModel]="taxMode()" (ngModelChange)="taxMode.set($event)">
                    <option value="Taxed">
                      {{ i18n.t('inv.taxMode.Taxed') }}
                    </option>
                    <option value="Untaxed">
                      {{ i18n.t('inv.taxMode.Untaxed') }}
                    </option>
                  </select>
                  @if (taxMode() === 'Untaxed') {
                    <p class="bo-hint">
                      {{ i18n.t('inv.form.untaxedNote') }}
                    </p>
                  }
                </div>
              }
              <div class="bo-field bo-field--wide">
                <label for="if-notes">
                  {{ i18n.t('rent.notes') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                </label>
                <input id="if-notes" maxlength="500" [ngModel]="notes()" (ngModelChange)="notes.set($event)" />
              </div>

              @if (saleAlreadyPaid()) {
                <p class="notice info bo-field--wide" role="status">
                  {{ i18n.t('inv.form.saleAlreadyPaid') }}
                </p>
              }

              <div class="bo-field">
                <label for="if-settlement">
                  {{ i18n.t('inv.form.settlement') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                </label>
                <select
                  id="if-settlement"
                  [ngModel]="settlement()"
                  (ngModelChange)="setSettlement($event)"
                >
                  <option value="full">{{ i18n.t('inv.form.settlement.full') }}</option>
                  <option value="deferred">{{ i18n.t('inv.form.settlement.deferred') }}</option>
                </select>
              </div>

              @if (settlement() === 'full') {
                <div class="bo-field" [class.is-invalid]="methodInvalid()">
                  <label for="if-settle-method">
                    {{ i18n.t('inv.form.settlementMethod') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <select
                    id="if-settle-method"
                    [ngModel]="settlementMethodKey()"
                    (ngModelChange)="setSettlementMethod($event)"
                    (blur)="touched.touch('settlementMethod')"
                    [attr.aria-invalid]="methodInvalid() ? 'true' : null"
                    [attr.aria-describedby]="methodInvalid() ? 'if-settle-method-error' : null"
                  >
                    <option [ngValue]="null">—</option>
                    @for (m of paymentMethods(); track m.key) {
                      <option [ngValue]="m.key">{{ methodName(m) }}</option>
                    }
                  </select>
                  @if (methodInvalid()) {
                    <span id="if-settle-method-error" class="bo-error" role="alert">
                      {{ i18n.t('inv.problem.methodRequired') }}
                    </span>
                  }
                </div>
              } @else if (paymentMethods().length) {
                <div class="bo-field">
                  <label for="if-pay">
                    {{ i18n.t('inv.form.paymentMode') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                  </label>
                  <select id="if-pay" [ngModel]="paymentMethodKey()" (ngModelChange)="setPaymentMethod($event)">
                    <option [ngValue]="null">{{ i18n.t('inv.form.paymentModeNone') }}</option>
                    @for (m of paymentMethods(); track m.key) {
                      <option [ngValue]="m.key">{{ methodName(m) }}</option>
                    }
                  </select>
                  <p class="bo-hint">{{ i18n.t('inv.form.paymentModeHint') }}</p>
                </div>
              }
            </div>
          </app-fold>

          @switch (mode()) {
            @case ('sale') {
              <app-fold class="bo-group" [heading]="i18n.t('inv.form.sale')" [open]="true">
                <p class="bo-group__note">{{ i18n.t('inv.form.salesHint') }}</p>
                @if (!customer()) {
                  <p class="notice">
                    {{ i18n.t('inv.form.pickCustomerFirst') }}
                  </p>
                } @else {
                  <div class="bo-fields">
                    <div class="bo-field">
                      <label for="if-sfrom">{{ i18n.t('reports.from') }}</label>
                      <app-date-input
                        id="if-sfrom"
                        [ngModel]="salesFrom()"
                        (ngModelChange)="setSalesWindow($event, salesTo())"
                      />
                    </div>
                    <div class="bo-field">
                      <label for="if-sto">{{ i18n.t('reports.to') }}</label>
                      <app-date-input
                        id="if-sto"
                        [ngModel]="salesTo()"
                        (ngModelChange)="setSalesWindow(salesFrom(), $event)"
                      />
                    </div>
                  </div>
                  <div class="bo-scroll">
                    <table class="bo-data" [attr.aria-label]="i18n.t('inv.form.sale')">
                      <thead>
                        <tr>
                          <th>{{ i18n.t('inv.number') }}</th>
                          <th>{{ i18n.t('inv.form.soldOn') }}</th>
                          <th class="num">{{ i18n.t('inv.totalTtc') }}</th>
                          <th class="num">
                            {{ i18n.t('inv.form.paidAtTill') }}
                          </th>
                          <th class="num">
                            {{ i18n.t('inv.form.onAccountApprox') }}
                          </th>
                        </tr>
                      </thead>
                      <tbody>
                        @for (s of saleCandidates(); track s.id) {
                          <tr>
                            <td [attr.data-label]="i18n.t('inv.number')">
                              <input
                                type="radio"
                                name="if-sale"
                                [checked]="saleId() === s.id"
                                (change)="pickSale(s.id)"
                                [attr.aria-label]="s.reference"
                                [attr.aria-invalid]="sourceMissing() ? 'true' : null"
                              />
                              <span class="mono">{{ s.reference }}</span>
                            </td>
                            <td class="mono" [attr.data-label]="i18n.t('inv.form.soldOn')">
                              {{ day(s.soldAtUtc) }}
                            </td>
                            <td class="num mono" [attr.data-label]="i18n.t('inv.totalTtc')">
                              {{ i18n.money(s.grandTotalTtc) }}
                            </td>
                            <td class="num mono" [attr.data-label]="i18n.t('inv.form.paidAtTill')">
                              {{ i18n.money(s.paidAmount) }}
                            </td>
                            <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('inv.form.onAccountApprox')">
                              {{ i18n.money(onAccount(s)) }}
                            </td>
                          </tr>
                        } @empty {
                          <tr>
                            <td colspan="5" class="bo-empty">
                              @if (candidatesBusy()) {
                                <strong>{{ i18n.t('common.loading') }}</strong>
                                <span class="bo-loading__rows" aria-hidden="true"></span>
                              } @else {
                                <strong>{{ i18n.t('inv.form.saleNone') }}</strong>
                                <span>{{ i18n.t('inv.form.saleNoneHint') }}</span>
                              }
                            </td>
                          </tr>
                        }
                      </tbody>
                    </table>
                  </div>
                  @if (sourceMissing()) {
                    <span class="bo-error" role="alert">{{ i18n.t('inv.problem.sourceRequired') }}</span>
                  }
                }
              </app-fold>
            }

            @case ('rental') {
              <app-fold class="bo-group" [heading]="i18n.t('inv.form.note')" [open]="true">
                <p class="bo-group__note">{{ i18n.t('inv.form.notesHint') }}</p>
                @if (!customer()) {
                  <p class="notice">
                    {{ i18n.t('inv.form.pickCustomerFirst') }}
                  </p>
                } @else {
                  <div class="bo-fields">
                    <div class="bo-field">
                      <label for="if-contract">{{ i18n.t('inv.form.contract') }}</label>
                      <select id="if-contract" [ngModel]="contractId()" (ngModelChange)="setContract($event)">
                        <option [ngValue]="null">—</option>
                        @for (c of contracts(); track c.id) {
                          <option [ngValue]="c.id">{{ c.number }}</option>
                        }
                      </select>
                    </div>
                  </div>
                  <div class="bo-scroll">
                    <table class="bo-data" [attr.aria-label]="i18n.t('inv.form.note')">
                      <thead>
                        <tr>
                          <th>{{ i18n.t('inv.form.noteKind') }}</th>
                          <th>{{ i18n.t('inv.number') }}</th>
                          <th>{{ i18n.t('receipt.date') }}</th>
                          <th class="num">
                            {{ i18n.t('inv.form.noteCharges') }}
                          </th>
                          <th>{{ i18n.t('common.state') }}</th>
                        </tr>
                      </thead>
                      <tbody>
                        @for (n of noteCandidates(); track n.id) {
                          <tr [class.is-muted]="n.invoiced || n.chargedNothing">
                            <td [attr.data-label]="i18n.t('inv.form.noteKind')">
                              <input
                                type="radio"
                                name="if-note"
                                [checked]="noteId() === n.id"
                                [disabled]="n.invoiced || n.chargedNothing"
                                (change)="pickNote(n)"
                                [attr.aria-label]="n.number"
                                [attr.aria-invalid]="sourceMissing() && !(n.invoiced || n.chargedNothing) ? 'true' : null"
                              />
                              <span>{{ i18n.t(n.kind === 'issue' ? 'rent.issueNote' : 'rent.returnNote') }}</span>
                            </td>
                            <td class="mono" [attr.data-label]="i18n.t('inv.number')">
                              {{ n.number }}
                            </td>
                            <td class="mono" [attr.data-label]="i18n.t('receipt.date')">
                              {{ day(n.date) }}
                            </td>
                            <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('inv.form.noteCharges')">
                              {{ i18n.money(n.charges) }}
                            </td>
                            <td [attr.data-label]="i18n.t('common.state')">
                              @if (n.invoiced) {
                                <span class="bo-state">{{ i18n.t('inv.form.alreadyInvoiced') }}</span>
                              } @else if (n.chargedNothing) {
                                <span class="bo-state">{{ i18n.t('inv.form.noteChargedNothing') }}</span>
                              }
                            </td>
                          </tr>
                        } @empty {
                          <tr>
                            <td colspan="5" class="bo-empty">
                              @if (candidatesBusy()) {
                                <strong>{{ i18n.t('common.loading') }}</strong>
                                <span class="bo-loading__rows" aria-hidden="true"></span>
                              } @else {
                                <strong>{{ i18n.t('inv.form.noteNone') }}</strong>
                                <span>{{ i18n.t('inv.form.noteNoneHint') }}</span>
                              }
                            </td>
                          </tr>
                        }
                      </tbody>
                    </table>
                  </div>
                  @if (sourceMissing()) {
                    <span class="bo-error" role="alert">{{ i18n.t('inv.problem.sourceRequired') }}</span>
                  }
                }
              </app-fold>
            }
          }
        </div>

        <div class="bo-panel__foot">
          @if (estimate() !== null) {
            <p class="total">
              <span>{{ i18n.t(mode() === 'sale' ? 'inv.form.onAccountApprox' : 'inv.form.noteCharges') }}</span>
              <strong class="mono">{{ i18n.money(estimate()!) }}</strong>
            </p>
          }
          <button type="button" class="btn-ghost" (click)="cancelled.emit(customerId())">
            {{ i18n.t('common.cancel') }}
          </button>
          <button type="button" class="btn-primary" [disabled]="busy() || !canSave()" (click)="save()">
            {{ i18n.t('inv.form.save') }}
          </button>
        </div>
      </section>
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss'],
  styles: [
    `
      /* The running figure sits at the start of the foot, the buttons at its end. */
      .total {
        margin: 0;
        margin-inline-end: auto;
        display: flex;
        flex-wrap: wrap;
        align-items: baseline;
        gap: 0.5rem;
        font-size: 0.86rem;
        color: var(--bo-ink-2);
      }
      .total strong {
        font-size: 1.05rem;
        font-weight: 700;
        color: var(--text);
        font-variant-numeric: tabular-nums;
      }
      /* Inputs are full-width by default; a radio that fills the cell is a stripe, not a choice. */
      .bo-data input[type='radio'] {
        width: auto;
        margin-inline-end: 0.4rem;
      }
    `,
  ],
})
export class ReceivableInvoiceFormComponent implements OnInit {
  private readonly api = inject(ReceivablesApi);
  private readonly admin = inject(AdminService);
  private readonly rentals = inject(RentalsApi);
  private readonly auth = inject(AuthService);
  private readonly confirm = inject(ConfirmService);
  readonly i18n = inject(I18nService);
  readonly regional = inject(RegionalService);

  readonly customerId = input<string | null>(null);
  readonly customers = input<AdminCustomer[]>([]);
  readonly branches = input<AdminBranch[]>([]);

  readonly saved = output<CreateInvoiceResponse>();
  readonly cancelled = output<string | null>();

  readonly MODES: readonly Mode[] = ['sale', 'rental'];

  readonly touched = new TouchedFields();
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  // ---------- shared by every mode ----------

  /** The customer being invoiced: the input when it was fixed, the select otherwise. */
  readonly customer = signal<string | null>(null);
  readonly mode = signal<Mode>('sale');
  readonly issuedOn = signal<string>(today(this.regional.settings().timeZone));
  readonly dueOn = signal<string>(addDays(this.issuedOn(), DEFAULT_DUE_DAYS));
  readonly notes = signal('');

  /** Tax choice is only available to users with the untaxed capability. */
  readonly canUntax = computed(() => {
    const caps = this.auth.currentUser()?.capabilities;
    return Array.isArray(caps) && caps.includes('finance.invoices.untaxed');
  });
  readonly taxMode = signal<TaxMode>('Taxed');

  /**
   * Required settlement choice: 'full' settles immediately and records payment with settlementMethodKey;
   * 'deferred' leaves invoice open for subsequent receipts.
   */
  readonly settlement = signal<'full' | 'deferred'>('deferred');
  readonly settlementMethodKey = signal<string | null>(null);
  /** How the customer is to pay, printed on the invoice. Words only; null says nothing. */
  readonly paymentMethodKey = signal<string | null>(null);

  /**
   * The tenders an invoice may name: active ones that can settle an account — never credit, a gift
   * card or points, which the server refuses here as a receipt voucher refuses them. Empty when the
   * list cannot be read, and then the choice is simply not offered.
   */
  readonly paymentMethods = signal<AdminPaymentMethod[]>([]);

  // ---------- over a sale ----------

  readonly salesFrom = signal<string>(addDays(this.issuedOn(), -SALES_WINDOW_DAYS));
  readonly salesTo = signal<string>(this.issuedOn());
  readonly sales = signal<AdminSale[]>([]);
  readonly saleId = signal<string | null>(null);

  // ---------- over a rental note ----------

  readonly contracts = signal<RentalContractRow[]>([]);
  readonly contractId = signal<string | null>(null);
  readonly issueNotes = signal<IssueNote[]>([]);
  readonly returnNotes = signal<ReturnNote[]>([]);
  readonly noteId = signal<string | null>(null);
  readonly noteKind = signal<'issue' | 'return' | null>(null);

  /** `sourceType:sourceId` of every invoice this customer already holds — what the candidates are crossed against. */
  readonly invoicedKeys = signal<Set<string>>(new Set());
  readonly candidatesBusy = signal(false);

  readonly customerRow = computed(() => this.customers().find((c) => c.id === this.customer()) ?? null);

  /** Sales of this customer that went partly or wholly on account and have no paper yet. */
  readonly saleCandidates = computed(() => {
    const customer = this.customer();
    const invoiced = this.invoicedKeys();
    return this.sales().filter(
      (s) =>
        s.customerId === customer &&
        (s.paymentMethod === 'Credit' || s.paymentMethod === 'Mixed') &&
        s.status === 'Completed' &&
        !invoiced.has(`sale:${s.id}`),
    );
  });

  /** The sale currently picked, if in sale mode. */
  readonly selectedSale = computed(() => {
    if (this.mode() !== 'sale') return null;
    const id = this.saleId();
    return id ? this.sales().find((s) => s.id === id) ?? null : null;
  });

  /** True when the picked sale was already paid in full at the till. */
  readonly saleAlreadyPaid = computed(() => {
    const s = this.selectedSale();
    return s !== null && this.onAccount(s) <= 0;
  });

  /**
   * The selected contract's notes, both kinds in one list, newest last. A note already
   * invoiced or one that charged nothing stays in the list, greyed and unselectable, so the
   * reader sees why it is not offered rather than wondering where it went.
   */
  readonly noteCandidates = computed<NoteCandidate[]>(() => {
    const invoiced = this.invoicedKeys();
    const issue = this.issueNotes().map<NoteCandidate>((n) => ({
      kind: 'issue',
      id: n.id,
      number: n.number,
      date: n.issuedAtUtc,
      charges: n.periodRent,
      invoiced: invoiced.has(`rental-issue:${n.id}`),
      chargedNothing: !(n.periodRent > 0),
    }));
    const ret = this.returnNotes().map<NoteCandidate>((n) => ({
      kind: 'return',
      id: n.id,
      number: n.number,
      date: n.returnedAtUtc,
      charges: n.chargeTotal,
      invoiced: invoiced.has(`rental-return:${n.id}`),
      chargedNothing: !(n.chargeTotal > 0),
    }));
    return [...issue, ...ret].sort((a, b) => withZ(a.date).localeCompare(withZ(b.date)));
  });

  /**
   * What the chosen document bills, as far as this screen can tell. Over a sale it is total
   * minus paid — an approximation the copy labels as such, since the server bills the 411 debit
   * the sale actually posted. Null while nothing is chosen.
   */
  readonly estimate = computed<number | null>(() => {
    switch (this.mode()) {
      case 'sale': {
        const sale = this.sales().find((s) => s.id === this.saleId());
        return sale ? this.onAccount(sale) : null;
      }
      case 'rental': {
        const note = this.noteCandidates().find((n) => n.id === this.noteId());
        return note ? note.charges : null;
      }
    }
  });

  /**
   * Where the customer's balance will stand after this invoice, when that is over the limit.
   */
  readonly willExceed = computed<{ balance: number; limit: number } | null>(() => {
    const c = this.customerRow();
    if (!c || !(c.creditLimit > 0)) return null;
    return c.balance > c.creditLimit ? { balance: c.balance, limit: c.creditLimit } : null;
  });

  /** Coarse readiness; the finer refusals appear under their fields when a save is attempted. */
  readonly canSave = computed(() => {
    if (!this.customer()) return false;
    switch (this.mode()) {
      case 'sale':
        return !!this.saleId();
      case 'rental':
        return !!this.noteId();
    }
  });

  async ngOnInit(): Promise<void> {
    this.customer.set(this.customerId());
    void this.loadPaymentMethods();
    await this.refreshCandidates();
  }

  private async loadPaymentMethods(): Promise<void> {
    try {
      const methods = await this.admin.listPaymentMethods();
      this.paymentMethods.set(
        methods
          .filter((m) => m.isActive && m.behavior !== 'Credit' && m.behavior !== 'GiftCard' && m.behavior !== 'Loyalty')
          .sort((a, b) => a.sortOrder - b.sortOrder),
      );
    } catch {
      this.paymentMethods.set([]);
    }
  }

  methodName(method: AdminPaymentMethod): string {
    return localizedName(method, this.i18n.lang());
  }

  // ---------- who and how ----------

  setCustomer(id: string | null): void {
    this.customer.set(id);
    this.saleId.set(null);
    this.contractId.set(null);
    this.issueNotes.set([]);
    this.returnNotes.set([]);
    this.noteId.set(null);
    this.noteKind.set(null);
    this.invoicedKeys.set(new Set());
    void this.refreshCandidates();
  }

  setMode(mode: Mode): void {
    if (this.mode() === mode) return;
    this.mode.set(mode);
    void this.refreshCandidates();
  }

  /** The due date follows the issue date while nobody has touched it: thirty days is thirty days from the paper. */
  setIssuedOn(day: string): void {
    const before = this.issuedOn();
    this.issuedOn.set(day);
    if (day && this.dueOn() === addDays(before, DEFAULT_DUE_DAYS)) this.dueOn.set(addDays(day, DEFAULT_DUE_DAYS));
  }

  setSettlement(choice: 'full' | 'deferred'): void {
    this.settlement.set(choice);
    if (choice === 'deferred') {
      this.settlementMethodKey.set(null);
    } else if (this.paymentMethodKey()) {
      this.settlementMethodKey.set(this.paymentMethodKey());
    }
  }

  setSettlementMethod(key: string | null): void {
    this.settlementMethodKey.set(key);
    this.paymentMethodKey.set(key);
  }

  setPaymentMethod(key: string | null): void {
    this.paymentMethodKey.set(key);
  }

  setSalesWindow(from: string, to: string): void {
    this.salesFrom.set(from);
    this.salesTo.set(to);
    void this.loadSales();
  }

  pickSale(id: string): void {
    this.saleId.set(id);
    this.touched.touch('source');
  }

  setContract(id: string | null): void {
    this.contractId.set(id);
    this.noteId.set(null);
    this.noteKind.set(null);
    void this.loadNotes();
  }

  pickNote(note: NoteCandidate): void {
    if (note.invoiced || note.chargedNothing) return;
    this.noteId.set(note.id);
    this.noteKind.set(note.kind);
    this.touched.touch('source');
  }

  // ---------- the candidates ----------

  private async refreshCandidates(): Promise<void> {
    const customer = this.customer();
    if (!customer) return;
    this.candidatesBusy.set(true);
    try {
      const invoices = await this.api.listInvoices({
        customerId: customer,
        take: INVOICE_TAKE,
      });
      if (this.customer() !== customer) return;
      this.invoicedKeys.set(new Set(invoices.filter((i) => i.sourceId).map((i) => `${i.sourceType}:${i.sourceId}`)));
      if (this.mode() === 'sale') await this.loadSales();
      else await this.loadContracts();
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.candidatesBusy.set(false);
    }
  }

  /** GET /api/sales has no customer filter: the window is fetched whole and narrowed here. */
  private async loadSales(): Promise<void> {
    const customer = this.customer();
    if (!customer || !this.salesFrom() || !this.salesTo()) return;
    this.candidatesBusy.set(true);
    try {
      const rows = await this.admin.listSales({
        fromUtc: parseInputDate(this.salesFrom()).toISOString(),
        toUtc: exclusiveEnd(this.salesTo()).toISOString(),
        take: INVOICE_TAKE,
      });
      if (this.customer() !== customer) return;
      this.sales.set(rows);
      if (!rows.some((s) => s.id === this.saleId())) this.saleId.set(null);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.candidatesBusy.set(false);
    }
  }

  private async loadContracts(): Promise<void> {
    const customer = this.customer();
    if (!customer) return;
    this.candidatesBusy.set(true);
    try {
      const rows = await this.rentals.listContracts(undefined, customer);
      if (this.customer() !== customer) return;
      this.contracts.set(rows);
      if (!rows.some((c) => c.id === this.contractId())) this.contractId.set(rows.length === 1 ? rows[0].id : null);
      await this.loadNotes();
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.candidatesBusy.set(false);
    }
  }

  private async loadNotes(): Promise<void> {
    const contract = this.contractId();
    if (!contract) {
      this.issueNotes.set([]);
      this.returnNotes.set([]);
      return;
    }
    this.candidatesBusy.set(true);
    try {
      const [issue, ret] = await Promise.all([
        this.rentals.listIssueNotes(contract),
        this.rentals.listReturnNotes(contract),
      ]);
      if (this.contractId() !== contract) return;
      this.issueNotes.set(issue);
      this.returnNotes.set(ret);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.candidatesBusy.set(false);
    }
  }

  onAccount(sale: AdminSale): number {
    return roundTo(Math.max(0, sale.grandTotalTtc - sale.paidAmount), this.regional.decimals());
  }

  // ---------- what the server will refuse ----------

  dueProblem(): 'dueRequired' | 'dueBeforeIssue' | null {
    if (!this.dueOn()) return 'dueRequired';
    if (this.issuedOn() && daysBetween(this.issuedOn(), this.dueOn()) < 0) return 'dueBeforeIssue';
    return null;
  }

  dueInvalid(): boolean {
    return this.touched.missing('due', this.dueProblem() ? '' : 'ok');
  }

  methodInvalid(): boolean {
    return this.settlement() === 'full' && this.touched.missing('settlementMethod', this.settlementMethodKey());
  }

  sourceMissing(): boolean {
    const chosen = this.mode() === 'sale' ? this.saleId() : this.noteId();
    return this.touched.missing('source', chosen ? 'ok' : '');
  }

  private problem(): boolean {
    if (!this.customer() || this.dueProblem()) return true;
    if (this.settlement() === 'full' && !this.settlementMethodKey()) return true;
    switch (this.mode()) {
      case 'sale':
        return !this.saleId();
      case 'rental':
        return !this.noteId() || !this.noteKind();
    }
  }

  day(iso: string): string {
    return formatDay(withZ(iso), this.i18n.lang(), this.regional.settings().timeZone);
  }

  // ---------- the save ----------

  async save(): Promise<void> {
    this.touched.touchAll(
      'customer',
      'due',
      'source',
      'settlementMethod',
    );
    if (this.problem()) return;

    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('inv.form.save'))))) return;
    const request: CreateInvoiceRequest = {
      dueDateUtc: midday(this.dueOn()),
      issuedAtUtc: this.issuedOn() ? midday(this.issuedOn()) : null,
      taxMode: this.canUntax() ? this.taxMode() : 'Taxed',
      notes: this.notes().trim() || null,
      settlement: this.settlement(),
      settlementMethodKey: this.settlement() === 'full' ? this.settlementMethodKey() : null,
      paymentMethodKey: this.settlement() === 'full' ? this.settlementMethodKey() : this.paymentMethodKey(),
    };
    switch (this.mode()) {
      case 'sale':
        request.saleId = this.saleId();
        break;
      case 'rental':
        if (this.noteKind() === 'issue') request.issueNoteId = this.noteId();
        else request.returnNoteId = this.noteId();
        break;
    }

    this.busy.set(true);
    this.error.set(null);
    try {
      const envelope = await this.api.createInvoice(request);
      this.saved.emit(envelope);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
      await this.refreshCandidates();
    } finally {
      this.busy.set(false);
    }
  }
}
