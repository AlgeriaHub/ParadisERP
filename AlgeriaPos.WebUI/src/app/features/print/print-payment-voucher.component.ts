import { Component, computed, input } from '@angular/core';
import { PaymentVoucher, REFUND } from '../backoffice/receivables/payment-vouchers-api.service';
import { STATUS_NAME } from '../backoffice/receivables/payment-voucher-view';
import { PrintDocumentComponent } from './print-document.component';
import { LabelledLine, PrintContext, branchLine, partyLines } from './print-context';

/**
 * The payment voucher on A5, two copies — company and payee — like the receipt voucher it
 * mirrors (the owner's answers 20–27, 2026-09-30).
 *
 * <p>Number, date and branch at the head; who is paid; the amount in figures and in words; the
 * method, and a cheque's number and bank; the "in exchange for" line; for a refund the contract,
 * the return note and the unused days, with what was taken off the debt stated beside what is
 * paid out; who approved it and when; two signature boxes; the owner's own text at the foot.</p>
 *
 * <p>The state is printed in words. A voucher not yet paid says so — pending approval, or
 * approved and still to pay — so a sheet printed early can never pass for a receipt of money
 * handed over.</p>
 */
@Component({
  selector: 'app-print-payment-voucher',
  standalone: true,
  imports: [PrintDocumentComponent],
  template: `
    @if (ctx(); as c) {
      @for (copy of copyIndexes(); track copy) {
        <app-print-sheet
          [ctx]="c"
          kind="paymentVoucher"
          [title]="c.t('print.pv.title')"
          titleKey="print.pv.title"
          [number]="voucher().number"
          [meta]="meta()"
          [copyLabel]="copyLabel(copy)"
          [breakBefore]="copy > 0"
          [a5]="true"
        >
          <div class="print-parties">
            <div class="print-block">
              <span class="print-block__title">{{ c.t('print.pv.paidTo') }}</span>
              @for (line of payee(); track $index) {
                <div [class.print-line--lead]="!line.label">
                  @if (line.label) {
                    <span class="print-line__label">{{ line.label }}</span>
                  }
                  <span [class.mono]="!!line.label" [attr.dir]="line.ltr ? 'ltr' : null">{{ line.value }}</span>
                </div>
              }
              @if (!isRefund() && voucher().expenseCategory) {
                <div><span class="print-line__label">{{ c.t('print.pv.category') }}</span><span>{{ category() }}</span></div>
              }
            </div>
            <div class="print-block">
              <span class="print-block__title">{{ c.t('pay.amount') }}</span>
              <div class="print-line--lead mono">{{ c.money(voucher().amount) }}</div>
              @if (words()) {
                <div>{{ c.t('print.rcpt.inWords') }} {{ words() }}</div>
              }
              <div><span class="print-line__label">{{ c.t('inv.rcpt.method') }}</span><span>{{ method() }}</span></div>
              @if (voucher().instrumentNumber) {
                <div><span class="print-line__label">{{ c.t('inv.rcpt.instrumentNumber') }}</span><span class="mono">{{ voucher().instrumentNumber }}</span></div>
              }
              @if (voucher().instrumentBank) {
                <div><span class="print-line__label">{{ c.t('pay.instrumentBank') }}</span><span>{{ voucher().instrumentBank }}</span></div>
              }
            </div>
          </div>

          @if (voucher().purpose) {
            <p class="print-sentence print-sentence--strong">
              <span class="print-line__label">{{ c.t('print.pv.inExchangeFor') }}</span>&ngsp;<bdi>{{ voucher().purpose }}</bdi>
            </p>
          }

          <!-- The contract on any voucher that names one (answers 20, 26), a refund or an expense. -->
          @if (voucher().contractNumber || voucher().returnNoteNumber) {
            <ul class="print-meta">
              @if (voucher().contractNumber) {
                <li><span class="print-line__label">{{ c.t('print.pv.contract') }}</span><span class="mono">{{ voucher().contractNumber }}</span></li>
              }
              @if (voucher().returnNoteNumber) {
                <li><span class="print-line__label">{{ c.t('print.pv.returnNote') }}</span><span class="mono">{{ voucher().returnNoteNumber }}</span></li>
              }
              @if (voucher().unusedDays) {
                <li><span class="print-line__label">{{ c.t('print.pv.unusedDays') }}</span><span class="mono">{{ voucher().unusedDays }}</span></li>
              }
            </ul>
          }

          @if (voucher().bonusLines?.length) {
            <!-- Workers' bonuses (owner, 2026-11-08): each worker and their share, on the paper they sign. -->
            <table class="print-table">
              <thead>
                <tr>
                  <th>{{ c.t('print.pv.worker') }}</th>
                  <th class="num">{{ c.t('print.pv.bonus') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (b of voucher().bonusLines; track b.employeeId) {
                  <tr>
                    <td><bdi>{{ b.employeeName }}</bdi> <span class="mono">{{ b.employeeNumber }}</span></td>
                    <td class="num mono">{{ c.money(b.amount) }}</td>
                  </tr>
                }
              </tbody>
            </table>
            <p class="print-sentence">
              <span class="print-line__label">{{ c.t('print.pv.payrollMonth') }}</span>&ngsp;<span class="mono">{{ bonusMonth() }}</span>
            </p>
          }

          @if (isRefund() && hasDebtLines()) {
            <table class="print-table">
              <tbody>
                @if (voucher().returnNoteUnusedRent) {
                  <tr>
                    <td>{{ c.t('print.pv.unusedRent') }}</td>
                    <td class="num mono">{{ c.money(voucher().returnNoteUnusedRent ?? 0) }}</td>
                  </tr>
                }
                @if (voucher().returnNoteDebtReduction) {
                  <tr>
                    <td>{{ c.t('print.pv.returnDeduction') }}</td>
                    <td class="num mono">{{ c.money(-(voucher().returnNoteDebtReduction ?? 0)) }}</td>
                  </tr>
                }
                @if (voucher().deductedFromDebt > 0) {
                  <tr>
                    <td>{{ c.t('print.pv.deducted') }}</td>
                    <td class="num mono">{{ c.money(-voucher().deductedFromDebt) }}</td>
                  </tr>
                }
              </tbody>
              <tfoot>
                <tr class="print-total">
                  <td>{{ c.t('print.pv.paidOut') }}</td>
                  <td class="num mono">{{ c.money(voucher().amount) }}</td>
                </tr>
              </tfoot>
            </table>
          }

          <div class="print-notice">
            @switch (state()) {
              @case ('Pending') {
                <strong>{{ c.t('print.pv.state.Pending') }}</strong>
                {{ c.t('print.pv.notPaidHint') }}
              }
              @case ('Approved') {
                <strong>{{ c.t('print.pv.state.Approved') }}</strong>
                {{ c.t('print.pv.notPaidHint') }}
              }
              @case ('Paid') {
                <strong>{{ c.t('print.pv.state.Paid', { d: c.day(voucher().paidAtUtc) }) }}</strong>
              }
              @case ('Cancelled') {
                <strong>{{ c.t('print.pv.state.Cancelled', { d: c.day(voucher().cancelledAtUtc) }) }}</strong>
                {{ voucher().statusNote }}
              }
            }
            @if (voucher().approvedAtUtc) {
              <p>
                <span class="print-line__label">{{ c.t('print.pv.approvedBy') }}</span>&ngsp;<bdi>{{ voucher().approvedByName || '—' }}</bdi> — <bdi class="mono">{{ c.day(voucher().approvedAtUtc) }}</bdi>
              </p>
            }
          </div>

          @if (voucher().notes) {
            <p class="print-sentence"><span class="print-line__label">{{ c.t('rent.notes') }}</span> {{ voucher().notes }}</p>
          }

          <div class="print-signatures">
            <div class="print-signature">{{ c.t('print.pv.sign.payee') }}</div>
            <div class="print-signature">{{ c.t('print.pv.sign.cashier') }}</div>
          </div>
        </app-print-sheet>
      }
    }
  `,
})
export class PrintPaymentVoucherComponent {
  readonly ctx = input.required<PrintContext>();
  readonly voucher = input.required<PaymentVoucher>();
  /** Two by default: the company keeps one, the payee takes one. */
  readonly copies = input(2);

  readonly copyIndexes = computed(() => Array.from({ length: Math.max(1, this.copies()) }, (_, i) => i));

  readonly isRefund = computed(() => this.voucher().kind === REFUND);
  readonly state = computed(() => STATUS_NAME[this.voucher().status] ?? 'Pending');
  readonly words = computed(() => this.ctx().words(this.voucher().amount));
  readonly branch = computed(() => branchLine(this.ctx(), this.voucher().branchId) || this.voucher().branchName);

  /** A refund to a known customer prints their block; anyone else is the name on the voucher. */
  readonly payee = computed<LabelledLine[]>(() => {
    const v = this.voucher();
    const c = this.ctx();
    const customer = v.customerId ? (c.customers.find((x) => x.id === v.customerId) ?? null) : null;
    if (customer) return partyLines(c, customer, { name: v.payeeName || v.customerName });
    return v.payeeName ? [{ label: '', value: v.payeeName }] : [{ label: '', value: '—' }];
  });

  readonly hasDebtLines = computed(() => {
    const v = this.voucher();
    return (v.returnNoteDebtReduction ?? 0) > 0 || v.deductedFromDebt > 0;
  });

  readonly meta = computed<LabelledLine[]>(() => {
    const c = this.ctx();
    const v = this.voucher();
    const lines: LabelledLine[] = [{ label: c.t('print.pv.date'), value: c.day(v.paidAtUtc ?? v.createdAtUtc) }];
    if (this.branch()) lines.push({ label: c.t('rent.branch'), value: this.branch() });
    return lines;
  });

  readonly category = computed(() => {
    const c = this.ctx();
    const key = 'pv.category.' + this.voucher().expenseCategory;
    const word = c.t(key);
    return word === key ? (this.voucher().expenseCategory ?? '') : word;
  });

  /** The tender's name in the document language; a key this session did not load prints raw. */
  readonly method = computed(() => {
    const c = this.ctx();
    const method = c.methods.find((m) => m.key === this.voucher().methodKey);
    return method ? c.name(method) : this.voucher().methodName;
  });

  copyLabel(index: number): string {
    const c = this.ctx();
    if (this.copies() < 2) return '';
    return index === 0 ? c.t('print.rcpt.copy.company') : index === 1 ? c.t('print.pv.copy.payee') : '';
  }

  /** The payroll month a bonus voucher's lines go on, mm/yyyy. */
  bonusMonth(): string {
    const b = this.voucher().bonusLines?.[0];
    return b ? `${String(b.payrollMonth).padStart(2, '0')}/${b.payrollYear}` : '';
  }
}
