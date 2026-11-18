import { ReceivablesApi, VoucherContractChoice } from './receivables-api.service';
import { Component, OnInit, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminBranch, AdminCustomer, AdminPaymentMethod, AdminService, SupplierOption, adminErrorMessage } from '../../../core/admin.service';
import { BranchLock } from '../../../core/branch-lock';
import { BranchScopeService, startBranch } from '../../../core/branch-scope.service';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService } from '../../../core/regional.service';
import { FoldComponent } from '../../../ui/fold.component';
import { formatDay, localizedName } from '../backoffice-format';
import { ConfirmService } from '../../../ui/confirm.service';
import { actionRequest } from '../../../ui/confirm-requests';
import { roundTo, withZ } from './receivable-view';
import { VoucherProblem, refundContractPurpose, refundPurpose, refundSplit, voucherProblem } from './payment-voucher-view';
import { MonthInputComponent } from '../payroll/month-input.component';
import { RefundableContract,
  BonusWorker,
  EXPENSE,
  EXPENSE_CATEGORIES,
  ExpenseCategory,
  PaymentVoucherKind,
  PaymentVoucherResponse,
  PaymentVouchersApi,
  REFUND,
  RefundableReturnNote,
  SUPPLIER_PAYMENT,
  WORKERS_BONUS,
} from './payment-vouchers-api.service';

/**
 * Raising a payment voucher: money back to a customer, or an expense the branch paid.
 *
 * <p>Saving records the paper and nothing else. The voucher is pending until a manager other
 * than the requester approves it, and only paying it moves the box or the books — the note
 * under the save button says so before anything is saved (answer 25).</p>
 *
 * <p>A refund is usually the rest of an early return. Started from a return note
 * (`#/backoffice/finance/vouchers?returnNote=<id>`) the note is picked already, the customer is
 * the contract's, and the amount is what the note still owes. The picker shows, per note, what
 * is still owed and what a voucher raised now would actually pay out: a customer who has run up
 * debt since the return has that much taken off the refund instead (answer 27), and the split is
 * shown in figures before the save.</p>
 */
@Component({
  selector: 'app-payment-voucher-form',
  standalone: true,
  imports: [FoldComponent, FormsModule, MonthInputComponent],
  template: `
    <div class="bo-stack">
      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }

      <section class="bo-panel">
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ i18n.t('pv.form.title') }}</h2>
          <p class="bo-panel__note">{{ i18n.t('pv.form.lead') }}</p>
        </div>

        <div class="bo-panel__body bo-form">
          <nav class="bo-chips" [attr.aria-label]="i18n.t('pv.form.kind')">
            @for (k of KINDS; track k.value) {
              <button
                type="button"
                [class.is-on]="kind() === k.value"
                [attr.aria-pressed]="kind() === k.value"
                (click)="pickKind(k.value)"
              >
                {{ i18n.t(k.key) }}
              </button>
            }
          </nav>

          @if (kind() === REFUND) {
            <app-fold class="bo-group" [heading]="i18n.t('pv.form.refundTo')" [open]="true">
              <div class="bo-fields bo-fields--roomy">
                <div class="bo-field" [class.is-invalid]="shows('customerRequired')">
                  <label for="pv-customer">
                    {{ i18n.t('common.customer') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <select id="pv-customer" [ngModel]="customerId()" (ngModelChange)="pickCustomer($event)">
                    <option [ngValue]="null">—</option>
                    @for (c of customers(); track c.id) {
                      <option [ngValue]="c.id">{{ c.fullName }}</option>
                    }
                  </select>
                  @if (customer(); as c) {
                    <span class="bo-hint">
                      {{ i18n.t('backoffice.customers.balance') }} <span class="mono">{{ i18n.money(c.balance) }}</span>
                      — {{ i18n.t(c.balance < 0 ? 'pv.form.inFavour' : c.balance > 0 ? 'pv.form.owes' : 'pv.form.square') }}
                    </span>
                  }
                </div>
              </div>

              <h3 class="bo-group__title">{{ i18n.t('pv.form.returnNote') }}</h3>
              <div class="bo-scroll">
                <table class="bo-data" [attr.aria-label]="i18n.t('pv.form.returnNote')">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('pv.form.pick') }}</th>
                      <th>{{ i18n.t('rent.returnNote') }}</th>
                      <th>{{ i18n.t('pv.view.contract') }}</th>
                      @if (!customerId()) {
                        <th>{{ i18n.t('common.customer') }}</th>
                      }
                      <th class="num">{{ i18n.t('pv.view.unusedDays') }}</th>
                      <th class="num">{{ i18n.t('pv.form.owedOnNote') }}</th>
                      <th class="num">{{ i18n.t('pv.form.stillOwed') }}</th>
                      <th class="num">{{ i18n.t('pv.form.payable') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    <tr>
                      <td>
                        <input
                          type="radio"
                          name="pv-note"
                          id="pv-note-none"
                          [checked]="!note()"
                          (change)="pickNote(null)"
                        />
                      </td>
                      <td [attr.colspan]="customerId() ? 6 : 7">
                        <label for="pv-note-none">{{ i18n.t('pv.form.noNote') }}</label>
                      </td>
                    </tr>
                    @for (n of refundable(); track n.returnNoteId) {
                      <tr>
                        <td>
                          <input
                            type="radio"
                            name="pv-note"
                            [id]="'pv-note-' + n.returnNoteId"
                            [attr.aria-label]="n.number"
                            [checked]="note()?.returnNoteId === n.returnNoteId"
                            [disabled]="n.stillOwed <= 0"
                            (change)="pickNote(n)"
                          />
                        </td>
                        <td class="mono" [attr.data-label]="i18n.t('rent.returnNote')">
                          <span>{{ n.number }}</span>
                          <span class="bo-soft">{{ day(n.returnedAtUtc) }}</span>
                        </td>
                        <td class="mono" [attr.data-label]="i18n.t('pv.view.contract')">{{ n.contractNumber }}</td>
                        @if (!customerId()) {
                          <td [attr.data-label]="i18n.t('common.customer')">{{ n.customerName }}</td>
                        }
                        <td class="num mono" [attr.data-label]="i18n.t('pv.view.unusedDays')">{{ n.unusedDays ?? '—' }}</td>
                        <td class="num mono" [attr.data-label]="i18n.t('pv.form.owedOnNote')">{{ i18n.money(n.reliefPaymentVoucher) }}</td>
                        <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('pv.form.stillOwed')">{{ i18n.money(n.stillOwed) }}</td>
                        <td class="num" [attr.data-label]="i18n.t('pv.form.payable')">
                          <span class="mono">{{ i18n.money(n.payable) }}</span>
                          @if (n.stillOwed > 0 && n.payable < n.stillOwed) {
                            <span class="bo-state bo-state--warn">{{ i18n.t('pv.form.partlyOffDebt') }}</span>
                          }
                          @if (n.stillOwed <= 0) {
                            <span class="bo-state">{{ i18n.t('pv.form.nothingLeft') }}</span>
                          }
                        </td>
                      </tr>
                    } @empty {
                      <tr>
                        <td [attr.colspan]="customerId() ? 7 : 8" class="bo-empty">
                          @if (loadingNotes()) {
                            <strong>{{ i18n.t('common.loading') }}</strong>
                          } @else {
                            <strong>{{ i18n.t('pv.form.noNotes') }}</strong>
                            <span>{{ i18n.t('pv.form.noNotesHint') }}</span>
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>

              <!-- No note still owes it — the unused rent went off a debt since paid, say: the
                   contract itself is refunded for its unused period (owner, 2026-10-06). -->
              @if (!note() && refundContracts().length) {
                <div class="bo-fields bo-fields--roomy">
                  <div class="bo-field">
                    <label for="pv-refund-contract">{{ i18n.t('pv.form.unusedOnContract') }}</label>
                    <select id="pv-refund-contract" [ngModel]="refundContract()?.contractId ?? null" (ngModelChange)="pickRefundContract($event)">
                      <option [ngValue]="null">{{ i18n.t('pv.form.noContract') }}</option>
                      @for (c of refundContracts(); track c.contractId) {
                        <option [ngValue]="c.contractId" [disabled]="c.stillOwed <= 0">
                          {{ c.number }} — {{ i18n.t('pv.form.contractStill', { n: i18n.money(c.stillOwed) }) }}
                        </option>
                      }
                    </select>
                    @if (refundContract(); as c) {
                      <span class="bo-hint">
                        {{ i18n.t('pv.form.contractFigures', { given: i18n.money(c.unusedRent), raised: i18n.money(c.alreadyRaised), payable: i18n.money(c.payable) }) }}
                      </span>
                    }
                  </div>
                </div>
              }

              @if (note(); as n) {
                @if (split(); as s) {
                  <p class="notice" [class.warn]="s.deducted > 0">
                    {{ i18n.t('pv.form.splitPaid', { paid: i18n.money(s.paid) }) }}
                    @if (s.deducted > 0) {
                      {{ i18n.t('pv.form.splitDeducted', { deducted: i18n.money(s.deducted) }) }}
                    }
                    @if (n.reliefDebtReduction > 0) {
                      {{ i18n.t('pv.form.returnDeducted', { n: n.number, v: i18n.money(n.reliefDebtReduction) }) }}
                    }
                  </p>
                }
              }
            </app-fold>
          } @else if (kind() === SUPPLIER_PAYMENT) {
            <app-fold class="bo-group" [heading]="i18n.t('pv.kind.supplierPayment')" [open]="true">
              <div class="bo-fields bo-fields--roomy">
                <div class="bo-field" [class.is-invalid]="shows('supplierRequired')">
                  <label for="pv-supplier">
                    {{ i18n.t('pv.form.supplier') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <select id="pv-supplier" [ngModel]="supplierId()" (ngModelChange)="pickSupplier($event)">
                    <option [ngValue]="null">—</option>
                    @for (s of suppliers(); track s.id) {
                      <option [ngValue]="s.id">{{ s.name }} ({{ s.code }})</option>
                    }
                  </select>
                  @if (shows('supplierRequired')) {
                    <span class="bo-error" role="alert">{{ i18n.t('pv.problem.supplierRequired') }}</span>
                  } @else if (supplier(); as s) {
                    <span class="bo-hint">
                      {{ i18n.t('pv.form.companyOwes') }} <span class="num mono">{{ i18n.money(s.balance) }}</span>
                    </span>
                  }
                </div>
              </div>
            </app-fold>
          } @else {
            <app-fold class="bo-group" [heading]="i18n.t('pv.form.expense')" [open]="true">
              <div class="bo-fields bo-fields--roomy">
                <div class="bo-field" [class.is-invalid]="shows('categoryRequired')">
                  <label for="pv-category">
                    {{ i18n.t('pv.form.category') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <select id="pv-category" [ngModel]="category()" (ngModelChange)="category.set($event)">
                    <option [ngValue]="null">—</option>
                    @for (c of CATEGORIES; track c) {
                      <option [ngValue]="c">{{ i18n.t('pv.category.' + c) }}</option>
                    }
                  </select>
                  @if (shows('categoryRequired')) {
                    <span class="bo-error" role="alert">{{ i18n.t('pv.problem.categoryRequired') }}</span>
                  }
                </div>
                @if (!isBonus()) {
                  <div class="bo-field" [class.is-invalid]="shows('payeeRequired')">
                    <label for="pv-payee">
                      {{ i18n.t('pv.form.payee') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                    </label>
                    <input id="pv-payee" maxlength="160" [ngModel]="payeeName()" (ngModelChange)="payeeName.set($event)" />
                    @if (shows('payeeRequired')) {
                      <span class="bo-error" role="alert">{{ i18n.t('pv.problem.payeeRequired') }}</span>
                    } @else {
                      <span class="bo-hint">{{ i18n.t('pv.form.payeeHint') }}</span>
                    }
                  </div>
                } @else {
                  <div class="bo-field">
                    <label for="pv-payroll-month">
                      {{ i18n.t('pv.bonus.month') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                    </label>
                    <app-month-input id="pv-payroll-month" [ngModel]="payrollMonth()" (ngModelChange)="payrollMonth.set($event ?? '')" />
                    <span class="bo-hint">{{ i18n.t('pv.bonus.monthHint') }}</span>
                  </div>
                }
                <!-- The contract an expense is for (owner, 2026-10-02, answer 26): required for
                     the categories the company marked, optional for every other. A workers' bonus is
                     pay and names none (owner, 2026-11-09). -->
                @if (!isBonus()) {
                <div class="bo-field" [class.is-invalid]="contractMissing()">
                  <label for="pv-contract">
                    {{ i18n.t('pv.form.contract') }}<span class="bo-req">{{ i18n.t(contractRequired() ? 'common.required' : 'common.optional') }}</span>
                  </label>
                  <select id="pv-contract" [ngModel]="expenseContractId()" (ngModelChange)="expenseContractId.set($event)">
                    <option [ngValue]="null">—</option>
                    @for (k of contractChoices(); track k.id) {
                      <option [ngValue]="k.id">{{ k.number }}{{ k.customerName ? ' · ' + k.customerName : '' }}</option>
                    }
                  </select>
                  @if (contractMissing()) {
                    <span class="bo-error" role="alert">{{ i18n.t('pv.problem.contractRequired') }}</span>
                  }
                </div>
                }
              </div>
              @if (isBonus()) {
                <!-- Workers' bonuses (owner, 2026-11-08): one voucher, several workers, each on their own payslip. -->
                <div class="bonus" [class.is-invalid]="shows('workersRequired') || shows('workerAmountRequired') || shows('workerTwice')">
                  <p class="bo-hint">{{ i18n.t('pv.bonus.lead') }}</p>
                  @if (!branchId()) {
                    <p class="notice">{{ i18n.t('pv.bonus.pickBranch') }}</p>
                  } @else if (workers().length === 0) {
                    <p class="notice">{{ i18n.t('pv.bonus.noWorkers') }}</p>
                  }
                  <div class="ui-scroll">
                    <table class="ui-table ui-table--cards">
                      <thead>
                        <tr>
                          <th>{{ i18n.t('pv.bonus.worker') }}</th>
                          <th class="num">{{ i18n.t('pv.bonus.amount') }}</th>
                          <th><span class="sr-only">{{ i18n.t('pv.bonus.remove') }}</span></th>
                        </tr>
                      </thead>
                      <tbody>
                        @for (row of bonusRows(); track row.key; let i = $index) {
                          <tr>
                            <td [attr.data-label]="i18n.t('pv.bonus.worker')">
                              <label class="sr-only" [for]="'pv-worker-' + row.key">{{ i18n.t('pv.bonus.worker') }} {{ i + 1 }}</label>
                              <select [id]="'pv-worker-' + row.key" [ngModel]="row.employeeId" (ngModelChange)="setWorker(row.key, $event)">
                                <option [ngValue]="null">—</option>
                                <!-- A worker already on another line is not offered again (owner, 2026-11-09). -->
                                @for (w of workers(); track w.id) {
                                  @if (w.id === row.employeeId || !chosenWorkers().has(w.id)) {
                                    <option [ngValue]="w.id">{{ w.fullName }} · {{ w.number }}</option>
                                  }
                                }
                              </select>
                            </td>
                            <td class="num" [attr.data-label]="i18n.t('pv.bonus.amount')">
                              <label class="sr-only" [for]="'pv-worker-amount-' + row.key">{{ i18n.t('pv.bonus.amount') }} {{ i + 1 }}</label>
                              <input [id]="'pv-worker-amount-' + row.key" type="number" min="0" class="num mono" [step]="regional.moneyStep()"
                                [ngModel]="row.amount" (ngModelChange)="setWorkerAmount(row.key, $event)" />
                            </td>
                            <td>
                              <button type="button" class="btn-danger" (click)="removeWorker(row.key)">{{ i18n.t('pv.bonus.remove') }}</button>
                            </td>
                          </tr>
                        }
                      </tbody>
                    </table>
                  </div>
                  <div class="bonus-foot">
                    <button type="button" class="btn-ghost" [disabled]="!branchId() || bonusRows().length >= workers().length" (click)="addWorker()">{{ i18n.t('pv.bonus.add') }}</button>
                    <span>{{ i18n.t('pv.bonus.total') }} <strong class="num mono">{{ i18n.money(bonusTotal()) }}</strong></span>
                  </div>
                  @if (shows('workersRequired') || shows('workerAmountRequired') || shows('workerTwice')) {
                    <span class="bo-error" role="alert">{{ i18n.t('pv.problem.' + problem()) }}</span>
                  }
                </div>
              }
            </app-fold>
          }

          <app-fold class="bo-group" [heading]="i18n.t('pv.form.payment')" [open]="true">
            @if (methods().length === 0) {
              <p class="notice">{{ i18n.t('inv.rcpt.form.noMethods') }}</p>
            }
            <div class="bo-fields bo-fields--roomy">
              @if (lock.locked()) {
                <!-- Not a choice for anyone but the Admin: the voucher is the employee's branch,
                     whatever branch the return note was taken at (the owner, 2026-09-30). -->
                <div class="bo-field">
                  <p class="bo-fixed-label">{{ i18n.t('branchLock.branch') }}</p>
                  <p class="bo-fixed" id="pv-branch-fixed">{{ lock.ownBranchName(branches()) }}</p>
                  <span class="bo-hint">{{ i18n.t('pv.form.branchHint') }}</span>
                </div>
              } @else {
                <div class="bo-field" [class.is-invalid]="shows('branchRequired')">
                  <label for="pv-branch">
                    {{ i18n.t('rent.branch') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <select id="pv-branch" [ngModel]="branchId()" (ngModelChange)="branchId.set($event)">
                    <option [ngValue]="null">—</option>
                    @for (b of branches(); track b.id) {
                      <option [ngValue]="b.id">{{ name(b) }}</option>
                    }
                  </select>
                  <span class="bo-hint">{{ i18n.t('pv.form.branchHint') }}</span>
                </div>
              }

              <div class="bo-field" [class.is-invalid]="shows('methodRequired')">
                <label for="pv-method">
                  {{ i18n.t('pay.method') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                </label>
                <select id="pv-method" [ngModel]="methodKey()" (ngModelChange)="methodKey.set($event)">
                  <option [ngValue]="null">—</option>
                  @for (m of methods(); track m.id) {
                    <option [ngValue]="m.key">{{ name(m) }}</option>
                  }
                </select>
                @if (method(); as m) {
                  <span class="bo-hint">{{ i18n.t(m.behavior === 'Cash' ? 'pv.form.fromBox' : 'pv.form.fromBank') }}</span>
                }
              </div>

              @if (isBonus()) {
                <div class="bo-field">
                  <p class="bo-fixed-label">{{ i18n.t('pay.amount') }}</p>
                  <p class="bo-fixed num mono">{{ i18n.money(bonusTotal()) }}</p>
                  <span class="bo-hint">{{ i18n.t('pv.bonus.amountHint') }}</span>
                </div>
              } @else {
              <div
                class="bo-field"
                [class.is-invalid]="shows('amountRequired') || shows('overOwed') || shows('overContract') || shows('overCredit')"
              >
                <label for="pv-amount">
                  {{ i18n.t('pay.amount')
                  }}<span class="bo-req">{{ i18n.t(note() || refundContract() ? 'common.optional' : 'common.required') }}</span>
                </label>
                <input
                  id="pv-amount"
                  type="number"
                  min="0"
                  class="num mono"
                  [step]="regional.moneyStep()"
                  [placeholder]="note() ? i18n.money(note()!.stillOwed) : refundContract() ? i18n.money(refundContract()!.payable) : ''"
                  [ngModel]="amount()"
                  (ngModelChange)="setAmount($event)"
                />
                @if (note()) {
                  <span class="bo-hint">{{ i18n.t('pv.form.amountFromNote') }}</span>
                } @else if (refundContract()) {
                  <span class="bo-hint">{{ i18n.t('pv.form.amountFromContract') }}</span>
                }
              </div>
              }

              @if (requiresInstrument()) {
                <div class="bo-field" [class.is-invalid]="shows('instrumentRequired') && !instrumentNumber().trim()">
                  <label for="pv-instrument">
                    {{ i18n.t('inv.rcpt.instrumentNumber') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <input id="pv-instrument" maxlength="60" class="mono" [ngModel]="instrumentNumber()" (ngModelChange)="instrumentNumber.set($event)" />
                </div>
                <div class="bo-field" [class.is-invalid]="shows('instrumentRequired') && !instrumentBank().trim()">
                  <label for="pv-bank">
                    {{ i18n.t('pay.instrumentBank') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <input id="pv-bank" maxlength="120" [ngModel]="instrumentBank()" (ngModelChange)="instrumentBank.set($event)" />
                </div>
              }

              <div class="bo-field bo-field--wide">
                <label for="pv-purpose">
                  {{ i18n.t('pv.form.purpose') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                </label>
                <input id="pv-purpose" maxlength="300" [ngModel]="purpose()" (ngModelChange)="setPurpose($event)" />
                <span class="bo-hint">{{ i18n.t('pv.form.purposeHint') }}</span>
              </div>

              <div class="bo-field bo-field--wide">
                <label for="pv-notes">
                  {{ i18n.t('rent.notes') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                </label>
                <input id="pv-notes" maxlength="500" [ngModel]="notes()" (ngModelChange)="notes.set($event)" />
              </div>
            </div>
          </app-fold>
        </div>

        <div class="bo-panel__body">
          @if (attempted() && problem(); as p) {
            <p class="bo-error" role="alert">{{ problemText(p) }}</p>
          }
          <p class="notice warn">
            <strong>{{ i18n.t('pv.state.Pending') }}</strong>
            {{ i18n.t('pv.form.pendingNote') }}
          </p>
        </div>

        <div class="bo-panel__foot">
          <button type="button" class="btn-ghost" (click)="cancelled.emit()">{{ i18n.t('common.cancel') }}</button>
          <button type="button" class="btn-primary" [disabled]="busy()" (click)="save()">{{ i18n.t('pv.form.save') }}</button>
        </div>
      </section>
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .bonus {
        display: grid;
        gap: var(--sp-3);
        margin-block-start: var(--sp-3);
      }
      .bonus-foot {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        justify-content: space-between;
        gap: var(--sp-3);
      }
    `,
  ],
})
export class PaymentVoucherFormComponent implements OnInit {
  private readonly api = inject(PaymentVouchersApi);
  private readonly receivables = inject(ReceivablesApi);
  private readonly admin = inject(AdminService);
  readonly i18n = inject(I18nService);
  readonly regional = inject(RegionalService);
  readonly lock = inject(BranchLock);
  private readonly confirm = inject(ConfirmService);
  private readonly branchScope = inject(BranchScopeService);

  /** A return note to refund, from `?returnNote=` — the note is picked and its customer fixed. */
  readonly returnNoteId = input<string | null>(null);
  /** `?contract=<id>`: the refund of that contract's unused period (owner, 2026-10-06). */
  readonly refundContractId = input<string | null>(null);
  readonly customers = input<AdminCustomer[]>([]);
  readonly branches = input<AdminBranch[]>([]);
  /** Already narrowed by the shell to tenders that can pay money out. */
  readonly methods = input<AdminPaymentMethod[]>([]);

  readonly saved = output<PaymentVoucherResponse>();
  readonly cancelled = output<void>();

  readonly REFUND = REFUND;
  readonly EXPENSE = EXPENSE;
  readonly SUPPLIER_PAYMENT = SUPPLIER_PAYMENT;
  readonly CATEGORIES = EXPENSE_CATEGORIES;
  readonly KINDS: readonly { value: PaymentVoucherKind; key: string }[] = [
    { value: REFUND, key: 'pv.kind.refund' },
    { value: EXPENSE, key: 'pv.kind.expense' },
    { value: SUPPLIER_PAYMENT, key: 'pv.kind.supplierPayment' },
  ];

  readonly kind = signal<PaymentVoucherKind>(EXPENSE);
  readonly customerId = signal<string | null>(null);
  readonly note = signal<RefundableReturnNote | null>(null);
  readonly refundable = signal<RefundableReturnNote[]>([]);
  readonly loadingNotes = signal(false);
  readonly refundContracts = signal<RefundableContract[]>([]);
  readonly refundContract = signal<RefundableContract | null>(null);
  readonly category = signal<ExpenseCategory | null>(null);
  readonly payeeName = signal('');
  readonly supplierId = signal<string | null>(null);
  readonly suppliers = signal<SupplierOption[]>([]);
  readonly branchId = signal<string | null>(null);
  readonly methodKey = signal<string | null>(null);
  readonly amount = signal<number | null>(null);
  readonly instrumentNumber = signal('');
  readonly instrumentBank = signal('');
  readonly purpose = signal('');
  readonly notes = signal('');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly attempted = signal(false);
  readonly expenseContractId = signal<string | null>(null);
  /** Workers' bonuses (owner, 2026-11-08): the rows of the voucher, the branch's workers, the payroll month ('' = the company's current one). */
  readonly bonusRows = signal<{ key: number; employeeId: string | null; amount: number | null }[]>([]);
  readonly workers = signal<BonusWorker[]>([]);
  readonly payrollMonth = signal('');
  private nextRow = 1;
  private workersFor: string | null = null;
  readonly isBonus = computed(() => this.kind() === EXPENSE && this.category() === WORKERS_BONUS);
  /** The workers already on a line of the voucher. */
  readonly chosenWorkers = computed(() => new Set(this.bonusRows().map((r) => r.employeeId).filter((id): id is string => !!id)));
  readonly bonusTotal = computed(() =>
    roundTo(this.bonusRows().reduce((sum, r) => sum + (r.amount && r.amount > 0 ? r.amount : 0), 0), this.regional.decimals()),
  );
  readonly contractChoices = signal<VoucherContractChoice[]>([]);
  readonly requiredCategories = signal<ReadonlySet<string>>(new Set());
  readonly contractRequired = computed(() => !!this.category() && this.requiredCategories().has(this.category()!));
  readonly contractMissing = computed(
    () => this.kind() === EXPENSE && !this.isBonus() && this.contractRequired() && !this.expenseContractId() && this.attempted(),
  );

  constructor() {
    // A workers' bonus lists the voucher branch's workers: read when the title is chosen and when the branch changes.
    effect(() => {
      const branch = this.branchId();
      if (this.isBonus() && branch) untracked(() => void this.loadWorkers(branch));
    });
  }

  /** Once the clerk writes the purpose, picking another note no longer overwrites it. */
  private purposeTouched = false;

  readonly customer = computed(() => this.customers().find((c) => c.id === this.customerId()) ?? null);
  readonly supplier = computed(() => this.suppliers().find((s) => s.id === this.supplierId()) ?? null);
  readonly method = computed(() => this.methods().find((m) => m.key === this.methodKey()) ?? null);
  readonly requiresInstrument = computed(() => this.method()?.requiresInstrumentDetails ?? false);
  readonly split = computed(() => {
    const note = this.note();
    return note ? refundSplit(this.amount(), note, this.regional.decimals()) : null;
  });

  readonly problem = computed<VoucherProblem | null>(() =>
    voucherProblem(
      {
        kind: this.kind(),
        branchId: this.branchId(),
        methodKey: this.methodKey(),
        amount: this.amount(),
        requiresInstrument: this.requiresInstrument(),
        instrumentNumber: this.instrumentNumber(),
        instrumentBank: this.instrumentBank(),
        customerId: this.customerId(),
        customerBalance: this.customer()?.balance ?? null,
        returnNote: this.note(),
        refundContract: this.note() ? null : this.refundContract(),
        category: this.category(),
        payeeName: this.payeeName(),
        supplierId: this.supplierId(),
        bonusLines: this.isBonus() ? this.bonusRows() : undefined,
      },
      this.regional.decimals(),
    ),
  );

  async ngOnInit(): Promise<void> {
    void this.loadContractRules();
    void this.loadSuppliers();
    this.branchId.set(startBranch(this.branchScope, this.lock, this.branches()));
    const cash = this.methods().find((m) => m.behavior === 'Cash');
    this.methodKey.set(cash?.key ?? (this.methods().length === 1 ? this.methods()[0].key : null));

    const noteId = this.returnNoteId();
    if (noteId) {
      this.kind.set(REFUND);
      this.loadingNotes.set(true);
      try {
        const [found] = await this.api.refundable(null, noteId);
        if (found) {
          this.customerId.set(found.customerId);
          await this.loadNotes();
          // The note itself, even when nothing is left on it: the table then says so in words.
          if (!this.refundable().some((n) => n.returnNoteId === found.returnNoteId)) this.refundable.update((l) => [found, ...l]);
          // The admin starts on the note's branch and may change it; anyone else pays from their own.
          if (!this.lock.locked()) this.branchId.set(found.branchId);
          if (found.stillOwed > 0) this.pickNote(found);
        }
      } catch (e) {
        this.error.set(adminErrorMessage(e));
      } finally {
        this.loadingNotes.set(false);
      }
    }

    const contractId = this.refundContractId();
    if (contractId && !noteId) {
      this.kind.set(REFUND);
      try {
        const [found] = await this.api.refundableContracts(null, contractId);
        if (found) {
          this.customerId.set(found.customerId);
          await this.loadNotes();
          if (!this.refundContracts().some((c) => c.contractId === found.contractId)) this.refundContracts.update((l) => [found, ...l]);
          if (found.stillOwed > 0) this.pickRefundContract(found.contractId);
        }
      } catch (e) {
        this.error.set(adminErrorMessage(e));
      }
    }
  }

  pickKind(kind: PaymentVoucherKind): void {
    this.kind.set(kind);
    this.attempted.set(false);
    if (kind === REFUND && this.refundable().length === 0) void this.loadNotes();
    if (kind === SUPPLIER_PAYMENT && this.suppliers().length === 0) void this.loadSuppliers();
  }

  pickSupplier(id: string | null): void {
    this.supplierId.set(id);
  }

  async loadSuppliers(): Promise<void> {
    try {
      this.suppliers.set(await this.admin.listSuppliers());
    } catch {
      this.suppliers.set([]);
    }
  }

  pickCustomer(id: string | null): void {
    this.customerId.set(id);
    this.pickNote(null);
    void this.loadNotes();
  }

  pickNote(note: RefundableReturnNote | null): void {
    this.note.set(note);
    if (note) this.refundContract.set(null);
    if (note) {
      this.customerId.set(note.customerId);
      this.amount.set(null);
      if (!this.purposeTouched) this.purpose.set(refundPurpose(note));
    } else if (!this.purposeTouched) {
      this.purpose.set('');
    }
  }

  pickRefundContract(id: string | null): void {
    const contract = this.refundContracts().find((c) => c.contractId === id) ?? null;
    this.refundContract.set(contract);
    this.amount.set(null);
    if (!this.purposeTouched) this.purpose.set(contract ? refundContractPurpose(contract) : '');
  }

  setAmount(value: unknown): void {
    const n = value === null || value === '' ? null : Number(value);
    this.amount.set(n !== null && Number.isFinite(n) ? n : null);
  }

  addWorker(): void {
    this.bonusRows.update((rows) => [...rows, { key: this.nextRow++, employeeId: null, amount: null }]);
  }

  removeWorker(key: number): void {
    this.bonusRows.update((rows) => rows.filter((r) => r.key !== key));
  }

  setWorker(key: number, employeeId: string | null): void {
    this.bonusRows.update((rows) => rows.map((r) => (r.key === key ? { ...r, employeeId } : r)));
  }

  setWorkerAmount(key: number, value: unknown): void {
    const n = value === null || value === '' ? null : Number(value);
    const amount = n !== null && Number.isFinite(n) ? n : null;
    this.bonusRows.update((rows) => rows.map((r) => (r.key === key ? { ...r, amount } : r)));
  }

  /** The branch's workers, read again when the branch changes; rows naming another branch's worker are emptied. */
  private async loadWorkers(branchId: string): Promise<void> {
    if (this.workersFor === branchId) return;
    this.workersFor = branchId;
    try {
      const workers = await this.api.bonusWorkers(branchId);
      if (this.workersFor !== branchId) return;
      this.workers.set(workers);
      const known = new Set(workers.map((w) => w.id));
      this.bonusRows.update((rows) => rows.map((r) => (r.employeeId && !known.has(r.employeeId) ? { ...r, employeeId: null } : r)));
    } catch (e) {
      this.workers.set([]);
      this.error.set(adminErrorMessage(e));
    }
  }

  setPurpose(value: string): void {
    this.purposeTouched = true;
    this.purpose.set(value);
  }

  shows(problem: VoucherProblem): boolean {
    return this.attempted() && this.problem() === problem;
  }

  problemText(problem: VoucherProblem): string {
    const note = this.note();
    const credit = Math.max(0, -(this.customer()?.balance ?? 0));
    const onContract = this.refundContract();
    return this.i18n.t('pv.problem.' + problem, {
      n: note ? this.i18n.money(note.stillOwed) : onContract ? this.i18n.money(onContract.stillOwed) : '',
      v: this.i18n.money(credit),
    });
  }

  name(value: { nameAr: string; nameFr: string; nameEn: string }): string {
    return localizedName(value, this.i18n.lang());
  }

  day(value: string | null): string {
    return formatDay(value ? withZ(value) : null, this.i18n.lang(), this.regional.settings().timeZone);
  }

  private async loadNotes(): Promise<void> {
    this.loadingNotes.set(true);
    try {
      const customer = this.customerId();
      const [notes, contracts] = await Promise.all([
        this.api.refundable(customer, null),
        // An older server has no contract refunds: the form keeps its notes.
        customer ? this.api.refundableContracts(customer, null).catch(() => []) : Promise.resolve([]),
      ]);
      this.refundable.set(notes);
      this.refundContracts.set(contracts);
      const picked = this.refundContract();
      this.refundContract.set(picked ? (contracts.find((c) => c.contractId === picked.contractId) ?? null) : null);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.loadingNotes.set(false);
    }
  }

  /** The categories that must name a contract, and the contracts an expense may name. */
  private async loadContractRules(): Promise<void> {
    try {
      const [categories, contracts] = await Promise.all([this.api.categories(), this.receivables.contractChoices()]);
      this.requiredCategories.set(new Set(categories.filter((x) => x.contractRequired).map((x) => x.key)));
      this.contractChoices.set(contracts);
    } catch {
      // An older server: no category requires a contract and none is offered.
    }
  }

  async save(): Promise<void> {
    this.attempted.set(true);
    if (this.problem() || this.contractMissing()) return;

    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('pv.form.save'))))) return;

    const refund = this.kind() === REFUND;
    const isSupplier = this.kind() === SUPPLIER_PAYMENT;
    const bonus = this.isBonus();
    const instrument = this.requiresInstrument();
    this.busy.set(true);
    this.error.set(null);
    try {
      const response = await this.api.create({
        branchId: this.branchId()!,
        kind: this.kind(),
        // Blank on a note means "what the note still owes"; the server works it out.
        amount: bonus ? this.bonusTotal() : this.amount() === null ? 0 : roundTo(this.amount()!, this.regional.decimals()),
        methodKey: this.methodKey()!,
        customerId: refund ? this.customerId() : null,
        returnNoteId: refund ? (this.note()?.returnNoteId ?? null) : null,
        expenseCategory: !refund && !isSupplier ? this.category() : null,
        payeeName: !refund && !isSupplier && !bonus ? (this.payeeName().trim() || null) : null,
        bonusLines: bonus
          ? this.bonusRows().map((r) => ({ employeeId: r.employeeId!, amount: roundTo(r.amount!, this.regional.decimals()) }))
          : null,
        payrollMonth: bonus ? this.payrollMonth() || null : null,
        supplierId: isSupplier ? this.supplierId() : null,
        instrumentNumber: instrument ? this.instrumentNumber().trim() || null : null,
        instrumentBank: instrument ? this.instrumentBank().trim() || null : null,
        purpose: this.purpose().trim() || null,
        notes: this.notes().trim() || null,
        // A refund names its contract only when no note is picked: a note names its own.
        contractId: refund
          ? (this.note() ? null : (this.refundContract()?.contractId ?? null))
          : isSupplier || bonus
            ? null
            : this.expenseContractId(),
      });
      this.saved.emit(response);
    } catch (e) {
      // Another voucher raised on the same note in the meantime is the usual refusal: the
      // figures are re-read so the table shows what is left, and the draft is kept.
      this.error.set(adminErrorMessage(e));
      if (refund) await this.loadNotes();
    } finally {
      this.busy.set(false);
    }
  }
}
