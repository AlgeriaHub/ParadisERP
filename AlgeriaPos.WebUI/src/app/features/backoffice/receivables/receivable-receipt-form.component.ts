import { Component, OnInit, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminBranch, AdminCustomer, AdminPaymentMethod, adminErrorMessage } from '../../../core/admin.service';
import { BranchLock } from '../../../core/branch-lock';
import { BranchScopeService, startBranch } from '../../../core/branch-scope.service';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService, localized } from '../../../core/regional.service';
import { FoldComponent } from '../../../ui/fold.component';
import { DateInputComponent } from '../../../ui/date-input.component';
import { TouchedFields } from '../backoffice-forms';
import { formatDay, localizedName } from '../backoffice-format';
import { ConfirmService } from '../../../ui/confirm.service';
import { actionRequest } from '../../../ui/confirm-requests';
import { MaintenanceApi, MaintenanceJobSummary } from '../maintenance/maintenance-api.service';
import { INVOICE_TAKE, midday, roundTo, spreadOldestFirst, today, withZ } from './receivable-view';
import {
  Invoice,
  ReceiptKind,
  ReceiptVoucher,
  ReceivableSettings,
  ReceivablesApi,
  VoucherContractChoice,
  allocatable,
} from './receivables-api.service';

/** The one refusal the form predicts before the server is asked; keyed to `inv.problem.*`. */
export type ReceiptProblem =
  | 'customerRequired'
  | 'branchRequired'
  | 'methodRequired'
  | 'amountRequired'
  | 'referenceRequired'
  | 'instrumentRequired'
  | 'overAllocated'
  | 'allocationExceeds';

interface ReceiptDraft {
  customerId: string | null;
  branchId: string | null;
  methodKey: string | null;
  amount: number | null;
  requiresReference: boolean;
  reference: string;
  requiresInstrument: boolean;
  instrumentNumber: string;
  instrumentBank: string;
  allocations: { allocated: number; allocatable: number }[];
}

/**
 * The server's own refusals, asked first and in its order. The instrument and reference rules
 * are the exception: the server stores those fields without checking them, so the form is the
 * only place the tender's `requiresReference` / `requiresInstrumentDetails` is enforced.
 *
 * <p>Every money comparison is made at the currency's precision. The server compares decimals;
 * here 43 085,29 + 45 439,50 is a hair above 88 524,79 in binary, and a receipt that covers two
 * invoices exactly must not be refused for it.</p>
 */
export function receiptProblem(d: ReceiptDraft, decimals = 2): ReceiptProblem | null {
  const r = (v: number) => roundTo(v, decimals);
  if (!d.customerId) return 'customerRequired';
  if (!d.branchId) return 'branchRequired';
  if (!d.methodKey) return 'methodRequired';
  if (!d.amount || d.amount <= 0) return 'amountRequired';
  if (d.requiresReference && !d.reference.trim()) return 'referenceRequired';
  if (d.requiresInstrument && (!d.instrumentNumber.trim() || !d.instrumentBank.trim())) return 'instrumentRequired';
  if (d.allocations.some((a) => r(a.allocated) > r(a.allocatable))) return 'allocationExceeds';
  const allocated = d.allocations.reduce((sum, a) => sum + a.allocated, 0);
  if (r(allocated) > r(d.amount)) return 'overAllocated';
  return null;
}

/**
 * How much did this customer hand over, by what means, and which invoices does it cover?
 *
 * <p>Two rules shape the form. The tender decides the voucher's fate: cash and card are money
 * in hand and the receipt is confirmed at once; a cheque or a transfer is paper, the receipt is
 * pending and settles nothing until someone confirms it — the hint under the method says which
 * before anything is saved. And the allocations are decided now or never: what is not written
 * against an invoice stays on account, reduces the customer's balance, and can never be applied
 * to a specific invoice later, because the API has no call for it.</p>
 *
 * <p>The allocatable figure per invoice — outstanding less what pending paper has already
 * claimed — is read when the form opens. If another receipt lands in the meantime the server
 * refuses with how much is left; the form then re-reads the invoices and keeps the draft, so
 * the clerk corrects one figure instead of retyping the receipt.</p>
 */
@Component({
  selector: 'app-receivable-receipt-form',
  standalone: true,
  imports: [FoldComponent, FormsModule, DateInputComponent],
  template: `
    <div class="bo-stack">
      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }

      <section class="bo-panel">
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ i18n.t('inv.acct.newReceipt') }}</h2>
          @if (fixedCustomer(); as c) {
            <p class="bo-meta bo-panel__note">
              <span>{{ c.fullName }}</span>
              <span class="mono" dir="ltr">{{ c.code }}</span>
              <span>{{ i18n.t('backoffice.customers.balance') }} <span class="mono">{{ i18n.money(c.balance) }}</span></span>
            </p>
          }
        </div>

        <div class="bo-panel__body bo-form">
          <app-fold class="bo-group" [heading]="i18n.t('inv.rcpt.form.payment')" [open]="true">
            @if (methods().length === 0) {
              <p class="notice">{{ i18n.t('inv.rcpt.form.noMethods') }}</p>
            }
            <div class="bo-fields bo-fields--roomy">
              @if (!customerId()) {
                <div class="bo-field" [class.is-invalid]="touched.missing('customer', customer())">
                  <label for="rp-customer">
                    {{ i18n.t('common.customer') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <select
                    id="rp-customer"
                    [ngModel]="customer()"
                    (ngModelChange)="pickCustomer($event)"
                    (blur)="touched.touch('customer')"
                    [attr.aria-invalid]="touched.missing('customer', customer()) ? 'true' : null"
                  >
                    <option [ngValue]="null">—</option>
                    @for (c of customers(); track c.id) {
                      <option [ngValue]="c.id">{{ c.fullName }}</option>
                    }
                  </select>
                  @if (touched.missing('customer', customer())) {
                    <span class="bo-error" role="alert">{{ i18n.t('inv.problem.customerRequired') }}</span>
                  }
                </div>
              }

              @if (lock.locked()) {
                <div class="bo-field">
                  <p class="bo-fixed-label">{{ i18n.t('branchLock.branch') }}</p>
                  <p class="bo-fixed" id="rp-fixed">{{ lock.ownBranchName(branches()) }}</p>
                  <span class="bo-hint">{{ i18n.t('inv.rcpt.form.branchHint') }}</span>
                </div>
              } @else {
                <div class="bo-field" [class.is-invalid]="touched.missing('branch', branchId())">
                  <label for="rp-branch">
                    {{ i18n.t('rent.branch') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <select
                    id="rp-branch"
                    [ngModel]="branchId()"
                    (ngModelChange)="branchId.set($event)"
                    (blur)="touched.touch('branch')"
                    [attr.aria-invalid]="touched.missing('branch', branchId()) ? 'true' : null"
                  >
                    <option [ngValue]="null">—</option>
                    @for (b of branches(); track b.id) {
                      <option [ngValue]="b.id">{{ branchName(b) }}</option>
                    }
                  </select>
                  @if (touched.missing('branch', branchId())) {
                    <span class="bo-error" role="alert">{{ i18n.t('inv.problem.branchRequired') }}</span>
                  } @else {
                    <span class="bo-hint">{{ i18n.t('inv.rcpt.form.branchHint') }}</span>
                  }
                </div>
              }

              <div class="bo-field" [class.is-invalid]="touched.missing('method', methodKey())">
                <label for="rp-method">
                  {{ i18n.t('pay.method') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                </label>
                <select
                  id="rp-method"
                  [ngModel]="methodKey()"
                  (ngModelChange)="pickMethod($event)"
                  (blur)="touched.touch('method')"
                  [attr.aria-invalid]="touched.missing('method', methodKey()) ? 'true' : null"
                >
                  <option [ngValue]="null">—</option>
                  @for (m of methods(); track m.id) {
                    <option [ngValue]="m.key">{{ methodName(m) }}</option>
                  }
                </select>
                @if (touched.missing('method', methodKey())) {
                  <span class="bo-error" role="alert">{{ i18n.t('inv.problem.methodRequired') }}</span>
                } @else {
                  @if (method(); as m) {
                    <span class="bo-hint">{{ i18n.t(m.requiresConfirmation ? 'inv.rcpt.form.willPend' : 'inv.rcpt.form.willConfirm') }}</span>
                  }
                }
              </div>

              <div class="bo-field" [class.is-invalid]="amountMissing()">
                <label for="rp-amount">
                  {{ i18n.t('pay.amount') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                </label>
                <input
                  id="rp-amount"
                  type="number"
                  min="0"
                  class="num mono"
                  [step]="regional.moneyStep()"
                  [ngModel]="amount()"
                  (ngModelChange)="setAmount($event)"
                  (blur)="touched.touch('amount')"
                  [attr.aria-invalid]="amountMissing() ? 'true' : null"
                />
                @if (amountMissing()) {
                  <span class="bo-error" role="alert">{{ i18n.t('inv.problem.amountRequired') }}</span>
                }
              </div>

              <div class="bo-field">
                <label for="rp-received">{{ i18n.t('inv.rcpt.receivedOn') }}</label>
                <app-date-input id="rp-received" [ngModel]="receivedOn()" (ngModelChange)="receivedOn.set($event)" />
              </div>

              <div class="bo-field" [class.is-invalid]="referenceMissing()">
                <label for="rp-reference">
                  {{ i18n.t('treasury.reference')
                  }}<span class="bo-req">{{ i18n.t(requiresReference() ? 'common.required' : 'common.optional') }}</span>
                </label>
                <input
                  id="rp-reference"
                  maxlength="100"
                  class="mono"
                  [ngModel]="reference()"
                  (ngModelChange)="reference.set($event)"
                  (blur)="touched.touch('reference')"
                  [attr.aria-invalid]="referenceMissing() ? 'true' : null"
                />
                @if (referenceMissing()) {
                  <span class="bo-error" role="alert">{{ i18n.t('inv.problem.referenceRequired') }}</span>
                }
              </div>

              @if (requiresInstrument()) {
                <div class="bo-field" [class.is-invalid]="instrumentNumberMissing()">
                  <label for="rp-instrument">
                    {{ i18n.t('inv.rcpt.instrumentNumber') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <input
                    id="rp-instrument"
                    maxlength="60"
                    class="mono"
                    [ngModel]="instrumentNumber()"
                    (ngModelChange)="instrumentNumber.set($event)"
                    (blur)="touched.touch('instrumentNumber')"
                    [attr.aria-invalid]="instrumentNumberMissing() ? 'true' : null"
                  />
                  @if (instrumentNumberMissing()) {
                    <span class="bo-error" role="alert">{{ i18n.t('inv.problem.instrumentRequired') }}</span>
                  }
                </div>
                <div class="bo-field" [class.is-invalid]="instrumentBankMissing()">
                  <label for="rp-bank">
                    {{ i18n.t('pay.instrumentBank') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <input
                    id="rp-bank"
                    maxlength="120"
                    [ngModel]="instrumentBank()"
                    (ngModelChange)="instrumentBank.set($event)"
                    (blur)="touched.touch('instrumentBank')"
                    [attr.aria-invalid]="instrumentBankMissing() ? 'true' : null"
                  />
                  @if (instrumentBankMissing()) {
                    <span class="bo-error" role="alert">{{ i18n.t('inv.problem.instrumentRequired') }}</span>
                  }
                </div>
                <div class="bo-field">
                  <label for="rp-due">
                    {{ i18n.t('pay.instrumentDueDate') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                  </label>
                  <app-date-input id="rp-due" [ngModel]="instrumentDueDate()" (ngModelChange)="instrumentDueDate.set($event)" />
                  <span class="bo-hint">{{ i18n.t('inv.rcpt.form.instrumentDueHint') }}</span>
                </div>
              }

              <!-- What the money is for, and the contract (owner, 2026-10-02, answers 19, 20, 26):
                   a repair or a loss names the contract and the return note that recorded it. -->
              <div class="bo-field">
                <label for="rp-kind">{{ i18n.t('inv.rcpt.form.kind') }}</label>
                <select id="rp-kind" [ngModel]="kind()" (ngModelChange)="setKind($event)">
                  @for (k of kinds(); track k) {
                    <option [value]="k">{{ i18n.t('inv.rcpt.kind.' + k) }}</option>
                  }
                </select>
              </div>
              @if (kind() === 'Advance') {
                <!-- M7b: an advance waits on 419 and is applied to its invoice from the receipt later. -->
                <p class="bo-field bo-field--wide bo-hint" id="rp-advance-hint">{{ i18n.t('adv.form.hint') }}</p>
                @if (vatOnReceipt()) {
                  <div class="bo-field">
                    <span class="bo-check">
                      <input id="rp-advance-tax" type="checkbox" [ngModel]="advanceTaxed()" (ngModelChange)="advanceTaxed.set($event)"
                        aria-describedby="rp-advance-tax-help" />
                      <label for="rp-advance-tax">{{ i18n.t('adv.form.taxed', { tax: taxName(), rate: defaultRate() }) }}</label>
                    </span>
                    <span class="bo-hint" id="rp-advance-tax-help">{{ i18n.t('adv.form.taxedHint', { tax: taxName() }) }}</span>
                  </div>
                }
                @if (jobs().length) {
                  <div class="bo-field">
                    <label for="rp-job">{{ i18n.t('adv.form.job') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
                    <select id="rp-job" [ngModel]="jobId()" (ngModelChange)="jobId.set($event)" aria-describedby="rp-job-help">
                      <option [ngValue]="null">—</option>
                      @for (j of jobs(); track j.id) {
                        <option [ngValue]="j.id">{{ j.number }}{{ j.equipment ? ' · ' + j.equipment : '' }}</option>
                      }
                    </select>
                    <span class="bo-hint" id="rp-job-help">{{ i18n.t('adv.form.jobHint') }}</span>
                  </div>
                }
              } @else {
              <div class="bo-field" [class.is-invalid]="contractMissing()">
                <label for="rp-contract">
                  {{ i18n.t('inv.rcpt.form.contract') }}<span class="bo-req">{{ i18n.t(kind() === 'Payment' ? 'common.optional' : 'common.required') }}</span>
                </label>
                <select id="rp-contract" [disabled]="!customer()" [ngModel]="contractId()" (ngModelChange)="setContract($event)">
                  <option [ngValue]="null">—</option>
                  @for (k of contracts(); track k.id) {
                    <option [ngValue]="k.id">{{ k.number }}</option>
                  }
                </select>
                @if (contractMissing()) {
                  <span class="bo-error" role="alert">{{ i18n.t('inv.rcpt.form.contractRequired') }}</span>
                }
              </div>
              @if (kind() !== 'Payment') {
                <div class="bo-field" [class.is-invalid]="returnNoteMissing()">
                  <label for="rp-return">
                    {{ i18n.t('inv.rcpt.form.returnNote') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <select id="rp-return" [disabled]="!contractId()" [ngModel]="returnNoteId()" (ngModelChange)="returnNoteId.set($event)">
                    <option [ngValue]="null">—</option>
                    @for (n of returnNotes(); track n.id) {
                      <option [ngValue]="n.id">{{ n.number }} · {{ day(n.returnedAtUtc) }}</option>
                    }
                  </select>
                  @if (returnNoteMissing()) {
                    <span class="bo-error" role="alert">{{ i18n.t('inv.rcpt.form.returnNoteRequired') }}</span>
                  }
                </div>
              }
              }

              <div class="bo-field bo-field--wide">
                <label for="rp-purpose">
                  {{ i18n.t('inv.rcpt.form.purpose') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                </label>
                <input id="rp-purpose" maxlength="300" [ngModel]="purpose()" (ngModelChange)="setPurpose($event)" />
                <span class="bo-hint">{{ i18n.t('inv.rcpt.form.purposeHint') }}</span>
              </div>

              <div class="bo-field bo-field--wide">
                <label for="rp-notes">
                  {{ i18n.t('rent.notes') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                </label>
                <input id="rp-notes" maxlength="500" [ngModel]="notes()" (ngModelChange)="notes.set($event)" />
              </div>
            </div>

            @if (dueBeforeReceived()) {
              <p class="notice warn">{{ i18n.t('inv.rcpt.form.dueBeforeReceived') }}</p>
            }
          </app-fold>
        </div>

        @if (kind() !== 'Advance') {
        <div class="bo-panel__body bo-form">
          <div class="bo-group">
            <h3 class="bo-group__title">{{ i18n.t('inv.rcpt.form.allocations') }}</h3>
            <p class="bo-group__note">{{ i18n.t('inv.rcpt.form.allocationsNote') }}</p>
            <p class="bo-group__note">{{ i18n.t('inv.rcpt.form.staleHint') }}</p>
            <div class="bo-actions bo-actions--start">
              <button type="button" class="btn-ghost bo-small" [disabled]="allocRows().length === 0" (click)="spread()">
                {{ i18n.t('inv.rcpt.form.oldestFirst') }}
              </button>
              <button type="button" class="btn-ghost bo-small" [disabled]="allocatedTotal() === 0" (click)="clear()">
                {{ i18n.t('inv.rcpt.form.clear') }}
              </button>
            </div>
          </div>
        </div>

        <div class="bo-scroll">
          <table class="bo-data">
            <thead>
              <tr>
                <th>{{ i18n.t('inv.number') }}</th>
                <th>{{ i18n.t('inv.dueOn') }}</th>
                <th class="num">{{ i18n.t('inv.outstanding') }}</th>
                <th class="num">{{ i18n.t('inv.claimed') }}</th>
                <th class="num">{{ i18n.t('inv.allocatable') }}</th>
                <th class="num">{{ i18n.t('inv.rcpt.form.allocatedAmount') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (inv of allocRows(); track inv.id) {
                <tr>
                  <td class="mono">{{ inv.number }}</td>
                  <td [attr.data-label]="i18n.t('inv.dueOn')">
                    <span class="mono">{{ day(inv.dueDateUtc) }}</span>
                    @if (inv.daysOverdue > 0) {
                      <span class="bo-state bo-state--danger">{{ i18n.t('inv.lateBy', { n: inv.daysOverdue }) }}</span>
                    }
                  </td>
                  <td class="num mono" [attr.data-label]="i18n.t('inv.outstanding')">{{ i18n.money(inv.outstanding) }}</td>
                  <td class="num mono" [attr.data-label]="i18n.t('inv.claimed')">
                    @if (inv.claimed > 0) {
                      {{ i18n.money(inv.claimed) }}
                    }
                  </td>
                  <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('inv.allocatable')">{{ i18n.money(open(inv)) }}</td>
                  <td class="num" [attr.data-label]="i18n.t('inv.rcpt.form.allocatedAmount')">
                    <input
                      type="number"
                      min="0"
                      [max]="open(inv)"
                      [step]="regional.moneyStep()"
                      class="num mono bo-input-narrow"
                      [attr.id]="'rp-a-' + inv.id"
                      [attr.aria-label]="i18n.t('inv.rcpt.form.allocatedAmount') + ' ' + inv.number"
                      [attr.aria-invalid]="exceeds(inv) ? 'true' : null"
                      [ngModel]="allocationOf(inv.id) || null"
                      (ngModelChange)="setAllocation(inv.id, $event)"
                    />
                    <button type="button" class="btn-ghost bo-small" (click)="fill(inv)">{{ i18n.t('inv.rcpt.form.fill') }}</button>
                    @if (exceeds(inv)) {
                      <span class="bo-error" role="alert">{{ i18n.t('inv.problem.allocationExceeds', { n: i18n.money(open(inv)) }) }}</span>
                    }
                  </td>
                </tr>
              } @empty {
                <tr>
                  <td colspan="6" class="bo-empty">
                    @if (loadingInvoices()) {
                      <strong>{{ i18n.t('common.loading') }}</strong>
                      <span class="bo-loading__rows" aria-hidden="true"></span>
                    } @else if (!customer()) {
                      <strong>{{ i18n.t('inv.form.pickCustomerFirst') }}</strong>
                    } @else {
                      <strong>{{ i18n.t('inv.rcpt.form.noOpen') }}</strong>
                      <span>{{ i18n.t('inv.rcpt.form.noOpenHint') }}</span>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>

        <div class="bo-panel__body">
          <p class="bo-meta">
            <span>{{ i18n.t('inv.rcpt.form.thisReceipt') }} <strong class="mono">{{ i18n.money(amount() ?? 0) }}</strong></span>
            <span>{{ i18n.t('inv.rcpt.allocated') }} <strong class="mono">{{ i18n.money(allocatedTotal()) }}</strong></span>
            <span>{{ i18n.t('inv.rcpt.form.remainder') }} <strong class="mono">{{ i18n.money(remainder()) }}</strong></span>
          </p>
          @if (remainder() < 0) {
            <span class="bo-error" role="alert">{{ i18n.t('inv.problem.overAllocated') }}</span>
          } @else if (remainder() > 0) {
            <p class="notice warn">{{ i18n.t('inv.rcpt.unallocatedHint') }}</p>
          }
        </div>
        }

        <div class="bo-panel__foot">
          <button type="button" class="btn-ghost" (click)="cancelled.emit({ customerId: customerId(), invoiceId: invoiceId() })">
            {{ i18n.t('common.cancel') }}
          </button>
          <button type="button" class="btn-primary" [disabled]="busy() || !canSave()" (click)="save()">
            {{ i18n.t('inv.rcpt.form.save') }}
          </button>
        </div>
      </section>
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss'],
})
export class ReceivableReceiptFormComponent implements OnInit {
  private readonly api = inject(ReceivablesApi);
  private readonly maintenance = inject(MaintenanceApi);
  readonly i18n = inject(I18nService);
  readonly lock = inject(BranchLock);
  private readonly confirm = inject(ConfirmService);
  private readonly branchScope = inject(BranchScopeService);
  readonly regional = inject(RegionalService);

  /** Fixed when the form was started from an account or an invoice; null from the cross-customer list. */
  readonly customerId = input<string | null>(null);
  /** The invoice being collected, when the form was started from one: its row is pre-filled. */
  readonly invoiceId = input<string | null>(null);
  readonly customers = input<AdminCustomer[]>([]);
  readonly branches = input<AdminBranch[]>([]);
  /** Already narrowed by the shell to tenders that can settle a customer account. */
  readonly methods = input<AdminPaymentMethod[]>([]);

  readonly saved = output<ReceiptVoucher>();
  /** Where the form was started from; the customer is null when it was started from the cross-customer list. */
  readonly cancelled = output<{ customerId: string | null; invoiceId: string | null }>();

  readonly touched = new TouchedFields();

  readonly customer = signal<string | null>(null);
  readonly branchId = signal<string | null>(null);
  readonly methodKey = signal<string | null>(null);
  readonly amount = signal<number | null>(null);
  readonly receivedOn = signal<string>(today(this.regional.settings().timeZone));
  readonly reference = signal('');
  readonly instrumentNumber = signal('');
  readonly instrumentBank = signal('');
  readonly instrumentDueDate = signal('');
  readonly notes = signal('');
  /** The "in exchange for" line (answer 23): prefilled from the allocated rental invoices, the clerk's once typed in. */
  readonly purpose = signal('');
  /** invoiceId → amount written against it in this draft. */
  readonly KINDS: readonly ReceiptKind[] = ['Payment', 'Repair', 'Loss'];
  /** M7b: the accountant's advances settings; null on a server or a caller that cannot read them. */
  readonly advanceSettings = signal<ReceivableSettings | null>(null);
  /** An advance is a kind of its own only while advances are held on 419; on 411 it is a payment allocated to nothing. */
  readonly kinds = computed<readonly ReceiptKind[]>(() =>
    this.advanceSettings()?.advancesHeldOn === 'AdvancesAccount' ? [...this.KINDS, 'Advance'] : this.KINDS,
  );
  /** D45: VAT due when the advance is received — then the seller may tick it (untaxed by default, VAT only on request). */
  readonly vatOnReceipt = computed(() => this.advanceSettings()?.advanceVat === 'WhenReceived');
  readonly advanceTaxed = signal(false);
  /** MS-35: the customer's open maintenance jobs an advance may name; empty when none or not readable. */
  readonly jobs = signal<MaintenanceJobSummary[]>([]);
  readonly jobId = signal<string | null>(null);
  readonly kind = signal<ReceiptKind>('Payment');
  readonly contractId = signal<string | null>(null);
  readonly returnNoteId = signal<string | null>(null);
  readonly contracts = signal<VoucherContractChoice[]>([]);
  readonly returnNotes = computed(() => this.contracts().find((k) => k.id === this.contractId())?.returnNotes ?? []);
  /** A repair or a loss always names its contract and its return note; shown once a save was tried. */
  readonly contractMissing = computed(() => this.kind() !== 'Payment' && !this.contractId() && this.triedSave());
  readonly returnNoteMissing = computed(() => this.kind() !== 'Payment' && !this.returnNoteId() && this.triedSave());
  private readonly triedSave = signal(false);

  readonly allocations = signal<Record<string, number>>({});
  readonly invoices = signal<Invoice[]>([]);
  readonly loadingInvoices = signal(false);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  /**
   * Once the clerk has touched an allocation by hand, the amount box stops steering the
   * pre-filled row: a figure someone typed is not one to overwrite quietly.
   */
  private allocationsTouched = false;
  /** Once the clerk writes the purpose, a change of allocations no longer rewrites it. */
  private purposeTouched = false;
  /** The invoice set the last purpose was asked for; a late answer for an older set is dropped. */
  private purposeFor = '';

  /** The invoices this draft writes money against, as one stable key. */
  private readonly allocatedKey = computed(() =>
    this.allocRows()
      .filter((inv) => this.allocationOf(inv.id) > 0)
      .map((inv) => inv.id)
      .sort()
      .join(','),
  );

  constructor() {
    // The server's own default for the line, asked whenever the allocated set changes, so what
    // the form shows is what the paper will print if the clerk leaves it alone.
    effect(() => {
      const key = this.allocatedKey();
      untracked(() => void this.prefillPurpose(key));
    });
  }

  readonly fixedCustomer = computed(() => {
    const id = this.customerId();
    return id ? (this.customers().find((c) => c.id === id) ?? null) : null;
  });

  readonly method = computed(() => this.methods().find((m) => m.key === this.methodKey()) ?? null);
  readonly requiresReference = computed(() => this.method()?.requiresReference ?? false);
  readonly requiresInstrument = computed(() => this.method()?.requiresInstrumentDetails ?? false);

  /** Oldest due first: the order the spread button follows and the order a clerk pays in. */
  readonly allocRows = computed(() =>
    this.invoices()
      .filter((inv) => this.open(inv) > 0)
      .sort((a, b) => new Date(withZ(a.dueDateUtc)).getTime() - new Date(withZ(b.dueDateUtc)).getTime()),
  );

  readonly allocatedTotal = computed(() => {
    const decimals = this.regional.decimals();
    const draft = this.allocations();
    return roundTo(
      this.allocRows().reduce((sum, inv) => sum + (draft[inv.id] ?? 0), 0),
      decimals,
    );
  });

  /** What goes on account: reduces the balance, can never be applied to an invoice later. */
  readonly remainder = computed(() => roundTo((this.amount() ?? 0) - this.allocatedTotal(), this.regional.decimals()));

  /** Both are "yyyy-MM-dd", so the string order is the day order. Said, not blocked. */
  readonly dueBeforeReceived = computed(() => {
    const due = this.instrumentDueDate();
    const received = this.receivedOn();
    return !!due && !!received && due < received;
  });

  readonly problem = computed<ReceiptProblem | null>(() =>
    receiptProblem(
      {
        customerId: this.customer(),
        branchId: this.branchId(),
        methodKey: this.methodKey(),
        amount: this.amount(),
        requiresReference: this.requiresReference(),
        reference: this.reference(),
        requiresInstrument: this.requiresInstrument(),
        instrumentNumber: this.instrumentNumber(),
        instrumentBank: this.instrumentBank(),
        allocations: this.kind() === 'Advance' ? [] : this.allocRows().map((inv) => ({ allocated: this.allocationOf(inv.id), allocatable: this.open(inv) })),
      },
      this.regional.decimals(),
    ),
  );

  /** Only the two facts nothing can stand in for; every other refusal is said under its field on save. */
  readonly canSave = computed(() => !!this.customer() && (this.amount() ?? 0) > 0);

  async ngOnInit(): Promise<void> {
    this.customer.set(this.customerId());
    // The Admin's working branch when it names one (D2), else where the desk is: the main branch,
    // else the first one. A locked user's is always their own.
    this.branchId.set(startBranch(this.branchScope, this.lock, this.branches()));
    if (this.methods().length === 1) this.methodKey.set(this.methods()[0].key);
    // An older server or a caller without the key: no advance kind, as before M7b.
    this.api.settings().then((s) => this.advanceSettings.set(s), () => undefined);
    await this.loadInvoices();
  }

  setKind(kind: ReceiptKind): void {
    this.kind.set(kind);
    if (kind === 'Payment' || kind === 'Advance') this.returnNoteId.set(null);
    if (kind === 'Advance') {
      this.contractId.set(null);
      void this.loadJobs();
    } else {
      this.advanceTaxed.set(false);
      this.jobId.set(null);
    }
  }

  /** The country profile's name for the tax, in the reader's language, and its default rate. */
  taxName(): string {
    return localized(this.regional.settings().taxNames, this.i18n.lang());
  }

  defaultRate(): number {
    return this.regional.settings().defaultTaxRate ?? 0;
  }

  /** MS-35: the customer's customer jobs still open to an advance. Not readable (no maintenance key, module off): none. */
  private async loadJobs(): Promise<void> {
    const customerId = this.customer();
    this.jobId.set(null);
    if (!customerId) {
      this.jobs.set([]);
      return;
    }
    try {
      const all = await this.maintenance.jobs({ kind: 'Customer' });
      this.jobs.set(all.filter((j) => j.customerId === customerId && !j.invoiceId && j.status !== 'Invoiced' && j.status !== 'Cancelled'));
    } catch {
      this.jobs.set([]);
    }
  }

  setContract(id: string | null): void {
    this.contractId.set(id);
    this.returnNoteId.set(null);
  }

  private async loadContracts(customerId: string | null): Promise<void> {
    this.contractId.set(null);
    this.returnNoteId.set(null);
    if (!customerId) {
      this.contracts.set([]);
      return;
    }
    try {
      this.contracts.set(await this.api.contractChoices(customerId));
    } catch {
      // An older server without the route: the receipt is still a payment, as it always was.
      this.contracts.set([]);
    }
  }

  private async loadInvoices(): Promise<void> {
    const customerId = this.customer();
    void this.loadContracts(customerId);
    if (!customerId) {
      this.invoices.set([]);
      return;
    }
    this.loadingInvoices.set(true);
    try {
      this.invoices.set(await this.api.listInvoices({ customerId, take: INVOICE_TAKE }));
      this.prefillFromInvoice();
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.loadingInvoices.set(false);
    }
  }

  /** Started from one invoice: the amount defaults to what it can still take, written against it. */
  private prefillFromInvoice(): void {
    const target = this.invoiceId();
    if (!target || this.allocationsTouched) return;
    const row = this.allocRows().find((inv) => inv.id === target);
    if (!row) return;
    const open = this.open(row);
    if (this.amount() === null) this.amount.set(open);
    this.allocations.update((a) => ({ ...a, [row.id]: Math.min(this.amount() ?? open, open) }));
  }

  pickCustomer(id: string | null): void {
    this.customer.set(id);
    // Another customer's invoices: the draft allocations named the old ones.
    this.allocations.set({});
    this.allocationsTouched = false;
    void this.loadInvoices();
    if (this.kind() === 'Advance') void this.loadJobs();
  }

  pickMethod(key: string | null): void {
    this.methodKey.set(key);
    this.touched.touch('method');
  }

  setAmount(value: unknown): void {
    const amount = value === null || value === '' ? null : Number(value);
    this.amount.set(amount !== null && Number.isFinite(amount) ? amount : null);
    this.prefillFromInvoice();
  }

  setPurpose(value: string): void {
    this.purposeTouched = true;
    this.purpose.set(value);
  }

  private async prefillPurpose(key: string): Promise<void> {
    this.purposeFor = key;
    if (this.purposeTouched) return;
    if (!key) {
      this.purpose.set('');
      return;
    }
    try {
      const { purpose } = await this.api.receiptPurpose(key.split(','));
      if (this.purposeFor === key && !this.purposeTouched) this.purpose.set(purpose ?? '');
    } catch {
      /* the server writes the same line on save when this one is left blank */
    }
  }

  allocationOf(invoiceId: string): number {
    return this.allocations()[invoiceId] ?? 0;
  }

  setAllocation(invoiceId: string, value: unknown): void {
    const n = value === null || value === '' ? 0 : Number(value);
    this.allocationsTouched = true;
    this.allocations.update((a) => ({ ...a, [invoiceId]: Number.isFinite(n) && n > 0 ? n : 0 }));
  }

  /**
   * What a new voucher may still write against this invoice; the server re-checks on save.
   * Rounded at the boundary: outstanding − claimed in binary can land a hair off the figure
   * the screen prints, and the server would refuse that hair as "only {n} still open".
   */
  open(inv: Invoice): number {
    return roundTo(allocatable(inv), this.regional.decimals());
  }

  exceeds(inv: Invoice): boolean {
    return roundTo(this.allocationOf(inv.id), this.regional.decimals()) > this.open(inv);
  }

  /** The row's whole allocatable, capped by what is left of the amount once one was typed. */
  fill(inv: Invoice): void {
    const open = this.open(inv);
    const amount = this.amount();
    const room = amount === null ? open : Math.max(0, this.remainder() + this.allocationOf(inv.id));
    this.setAllocation(inv.id, Math.min(open, room));
  }

  /** Oldest due first, never above a row's allocatable, until the amount runs out. */
  spread(): void {
    const rows = this.allocRows().map((inv) => ({ id: inv.id, allocatable: this.open(inv) }));
    this.allocationsTouched = true;
    this.allocations.set(spreadOldestFirst(this.amount(), rows, this.regional.decimals()));
  }

  clear(): void {
    this.allocationsTouched = true;
    this.allocations.set({});
  }

  amountMissing(): boolean {
    return this.touched.missing('amount', (this.amount() ?? 0) > 0 ? 'ok' : '');
  }

  referenceMissing(): boolean {
    return this.requiresReference() && this.touched.missing('reference', this.reference());
  }

  instrumentNumberMissing(): boolean {
    return this.requiresInstrument() && this.touched.missing('instrumentNumber', this.instrumentNumber());
  }

  instrumentBankMissing(): boolean {
    return this.requiresInstrument() && this.touched.missing('instrumentBank', this.instrumentBank());
  }

  branchName(branch: AdminBranch): string {
    return localizedName(branch, this.i18n.lang());
  }

  methodName(method: AdminPaymentMethod): string {
    return localizedName(method, this.i18n.lang());
  }

  day(value: string | null): string {
    return formatDay(value ? withZ(value) : null, this.i18n.lang(), this.regional.settings().timeZone);
  }

  async save(): Promise<void> {
    // A save was attempted, so every field may now say what is wrong with it.
    this.touched.touchAll('customer', 'branch', 'method', 'amount', 'reference', 'instrumentNumber', 'instrumentBank');
    this.triedSave.set(true);
    if (this.problem() || this.contractMissing() || this.returnNoteMissing()) return;

    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('inv.rcpt.form.save'))))) return;

    const instrument = this.requiresInstrument();
    const advance = this.kind() === 'Advance';
    this.busy.set(true);
    this.error.set(null);
    try {
      const voucher = await this.api.createReceipt({
        customerId: this.customer()!,
        branchId: this.branchId()!,
        amount: this.amount()!,
        methodKey: this.methodKey()!,
        // Midday rather than midnight: a date field means a day, and midnight UTC is the day
        // before everywhere west of Greenwich.
        receivedAtUtc: this.receivedOn() ? new Date(midday(this.receivedOn())).toISOString() : null,
        reference: this.reference().trim() || null,
        // Instrument fields travel only for a tender that asks for them: a cheque number left
        // over from a previous choice has no business on a cash receipt.
        instrumentNumber: instrument ? this.instrumentNumber().trim() || null : null,
        instrumentBank: instrument ? this.instrumentBank().trim() || null : null,
        instrumentDueDate: instrument && this.instrumentDueDate() ? new Date(midday(this.instrumentDueDate())).toISOString() : null,
        notes: this.notes().trim() || null,
        purpose: this.purpose().trim() || null,
        kind: this.kind(),
        contractId: advance ? null : this.contractId(),
        returnNoteId: this.kind() === 'Payment' || advance ? null : this.returnNoteId(),
        // M7b: an advance pays no invoice when taken (it is applied later from the receipt); its VAT only when ticked.
        allocations: advance
          ? []
          : this.allocRows()
              .filter((inv) => this.allocationOf(inv.id) > 0)
              .map((inv) => ({ invoiceId: inv.id, amount: roundTo(this.allocationOf(inv.id), this.regional.decimals()) })),
        ...(advance
          ? { advanceTaxRate: this.vatOnReceipt() && this.advanceTaxed() ? this.defaultRate() : null, maintenanceJobId: this.jobId() }
          : {}),
      });
      this.saved.emit(voucher);
    } catch (e) {
      // "Only {n} of this invoice is still open to allocation" means another receipt landed
      // between load and save. The rows are re-read so the cap is current; the draft stays,
      // since allocations cannot be added after the fact and retyping a receipt is how one
      // gets forgotten.
      this.error.set(adminErrorMessage(e));
      await this.loadInvoices();
    } finally {
      this.busy.set(false);
    }
  }
}
