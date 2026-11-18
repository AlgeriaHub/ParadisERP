import { Component, computed, input } from '@angular/core';
import {
  ElementGroup,
  LabelBook,
  PayrollRun,
  PayrollRunLine,
  elementLabel,
  groupElements,
  labelIn,
  periodLabel,
  totalWithheld,
} from '../backoffice/payroll/payroll-run-view';
import { PrintDocumentComponent } from './print-document.component';
import { LabelledLine, PrintContext, branchLine } from './print-context';

/** One payslip, ready to draw: the run line with everything the paper needs already resolved. */
interface Payslip {
  line: PayrollRunLine;
  groups: ElementGroup[];
  withheld: number;
}

/**
 * The payslip on A4 — the ninth printed document, through the same frame as the other eight
 * (docs/07 §4.2, §5.12).
 *
 * <p>One sheet per employee, and <b>a run prints in one go</b>: the page hands this component
 * every line of the run and each one gets its own sheet with a page break before it. That is why
 * nothing here fetches — a branch of nine hundred would otherwise open nine hundred tabs.</p>
 *
 * <p>What the paper must be able to answer is: what was earned, what was taken off it, and why.
 * So every element prints with its quantity and its rate — 26 days at 1 500, 12 hours at 375 —
 * under the company's own wording, and the deductions print as their own block with the
 * contribution and the tax under the names this company's accountant gave them, never a
 * hard-coded fiscal label.</p>
 *
 * <p><b>One sheet, measured.</b> The sheet is drawn dense: at the ordinary A4 rhythm the payslip
 * came to about 1 075 px against a content box of 1 052, so every employee cost two pages and the
 * second carried nothing but the repeated footer strip.</p>
 *
 * <p>A run computed while the statutory figures were still unconfirmed prints
 * <b>provisional</b> on every sheet. The mark is the run's own frozen flag, so confirming the
 * figures next month does not turn this month's paper into something it was not.</p>
 */
@Component({
  selector: 'app-print-payslip',
  standalone: true,
  imports: [PrintDocumentComponent],
  template: `
    @if (ctx(); as c) {
      @for (slip of payslips(); track slip.line.id; let i = $index) {
        <app-print-sheet
          [ctx]="c"
          kind="payslip"
          [title]="c.t('print.payslip.title')"
          titleKey="print.payslip.title"
          [subtitle]="period()"
          [number]="slip.line.employeeNumber"
          [meta]="meta(slip)"
          [breakBefore]="i > 0"
          [dense]="true"
        >
          <div class="print-parties">
            <div class="print-block">
              <span class="print-block__title">{{ c.t('print.payslip.employee') }}</span>
              <div class="print-line--lead">{{ slip.line.employeeName }}</div>
              <div>
                <span class="print-line__label">{{ c.t('print.payslip.number') }}</span>
                <span class="mono" dir="ltr">{{ slip.line.employeeNumber }}</span>
              </div>
              @if (slip.line.socialNumber) {
                <div>
                  <span class="print-line__label">{{ c.t('print.payslip.socialNumber', { name: socialName() }) }}</span>
                  <span class="mono">{{ slip.line.socialNumber }}</span>
                </div>
              }
              @if (branch()) {
                <div><span class="print-line__label">{{ c.t('rent.branch') }}</span><span>{{ branch() }}</span></div>
              }
            </div>
            <div class="print-block">
              <span class="print-block__title">{{ c.t('print.payslip.period') }}</span>
              <div class="print-line--lead">{{ period() }}</div>
              <div>
                <span class="print-line__label">{{ c.t('print.payslip.basis') }}</span>
                <span>{{ c.t('hr.basis.' + slip.line.payBasis) }}</span>
              </div>
              <div>
                <span class="print-line__label">{{ c.t('print.payslip.baseRate') }}</span>
                <span class="mono">{{ c.money(slip.line.baseRate) }}</span>
              </div>
              <div>
                <span class="print-line__label">{{ c.t('print.payslip.daysPresent') }}</span>
                <span class="mono">{{ c.qty(slip.line.daysPresent) }}</span>
              </div>
              @if (slip.line.paidLeaveDays) {
                <div>
                  <span class="print-line__label">{{ c.t('print.payslip.paidLeave') }}</span>
                  <span class="mono">{{ c.qty(slip.line.paidLeaveDays) }}</span>
                </div>
              }
              @if (slip.line.unpaidAbsenceDays) {
                <div>
                  <span class="print-line__label">{{ c.t('print.payslip.unpaidAbsence') }}</span>
                  <span class="mono">{{ c.qty(slip.line.unpaidAbsenceDays) }}</span>
                </div>
              }
            </div>
          </div>

          <!-- Full width, not print-table--compact: that is the totals box of an invoice (28rem,
               pushed to the end), and on the payslip it squeezed both tables into half the sheet,
               wrapped their labels and, in French, ran the sheet past A4 onto a second page. -->
          <table class="print-table">
            <caption>{{ c.t('print.payslip.elements') }}</caption>
            <thead>
              <tr>
                <th>{{ c.t('print.payslip.label') }}</th>
                <th class="num">{{ c.t('print.payslip.qty') }}</th>
                <th class="num">{{ c.t('print.payslip.rate') }}</th>
                <th class="num">{{ c.t('print.payslip.amount') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (group of slip.groups; track group.kind) {
                @for (element of group.elements; track element.key + '-' + element.sortOrder) {
                  <tr>
                    <td>
                      <span>{{ label(element) }}</span>
                      <span class="print-soft">{{ c.t('hr.element.kind.' + element.kind) }}</span>
                    </td>
                    <td class="num mono">{{ element.qty === null || element.qty === undefined ? '—' : c.qty(element.qty) }}</td>
                    <td class="num mono">{{ element.rate ? c.money(element.rate) : '—' }}</td>
                    <td class="num mono">{{ c.money(element.amount) }}</td>
                  </tr>
                }
              } @empty {
                <tr>
                  <td colspan="4">{{ c.t('print.payslip.noElements') }}</td>
                </tr>
              }
            </tbody>
            <tfoot>
              <tr class="print-total">
                <td colspan="3">{{ c.t('print.payslip.gross') }}</td>
                <td class="num mono">{{ c.money(slip.line.gross) }}</td>
              </tr>
            </tfoot>
          </table>

          <table class="print-table">
            <caption>{{ c.t('print.payslip.withheld') }}</caption>
            <thead>
              <tr>
                <th>{{ c.t('print.payslip.label') }}</th>
                <th class="num">{{ c.t('print.payslip.base') }}</th>
                <th class="num">{{ c.t('print.payslip.amount') }}</th>
              </tr>
            </thead>
            <tbody>
              @if (slip.line.socialBase || slip.line.socialEmployee) {
                <tr>
                  <td>{{ socialName() }}</td>
                  <td class="num mono">{{ c.money(slip.line.socialBase) }}</td>
                  <td class="num mono">{{ c.money(slip.line.socialEmployee) }}</td>
                </tr>
              }
              @if (slip.line.taxBase || slip.line.incomeTax) {
                <tr>
                  <td>{{ taxName() }}</td>
                  <td class="num mono">{{ c.money(slip.line.taxBase) }}</td>
                  <td class="num mono">{{ c.money(slip.line.incomeTax) }}</td>
                </tr>
              }
              @if (slip.line.paidByVoucher) {
                <!-- A bonus already handed over by voucher: in the gross above, taken back here (owner, 2026-11-08). -->
                <tr>
                  <td>{{ c.t('print.payslip.paidByVoucher') }}</td>
                  <td class="num mono">—</td>
                  <td class="num mono">{{ c.money(slip.line.paidByVoucher) }}</td>
                </tr>
              }
              @if (slip.line.advanceRecovered) {
                <tr>
                  <td>{{ c.t('print.payslip.advance') }}</td>
                  <td class="num mono">—</td>
                  <td class="num mono">{{ c.money(slip.line.advanceRecovered) }}</td>
                </tr>
              }
              @if (slip.line.otherDeductions) {
                <tr>
                  <td>{{ c.t('print.payslip.otherDeductions') }}</td>
                  <td class="num mono">—</td>
                  <td class="num mono">{{ c.money(slip.line.otherDeductions) }}</td>
                </tr>
              }
            </tbody>
            <tfoot>
              <tr>
                <td colspan="2">{{ c.t('print.payslip.totalWithheld') }}</td>
                <td class="num mono">{{ c.money(slip.withheld) }}</td>
              </tr>
              <tr class="print-total">
                <td colspan="2">{{ c.t('print.payslip.net') }}</td>
                <td class="num mono">{{ c.money(slip.line.net) }}</td>
              </tr>
            </tfoot>
          </table>

          @if (words(slip)) {
            <p class="print-sentence print-sentence--strong">{{ c.t('print.payslip.inWords') }} {{ words(slip) }}</p>
          }

          @if (run().isProvisional) {
            <div class="print-notice">
              <strong>{{ c.t('hr.run.provisional') }}</strong>
              <p>{{ c.t('hr.payslip.provisional_note') }}</p>
            </div>
          }

          <div class="print-signatures">
            <div class="print-signature">{{ c.t('print.payslip.employerSignature') }}</div>
            <div class="print-signature">{{ c.t('print.payslip.employeeSignature') }}</div>
          </div>
        </app-print-sheet>
      }
    }
  `,
})
export class PrintPayslipComponent {
  readonly ctx = input.required<PrintContext>();
  readonly run = input.required<PayrollRun>();
  /** Every line the page read, in the server's order. One sheet each. */
  readonly lines = input.required<PayrollRunLine[]>();
  /** The wording each element prints under; empty means every element prints as its key. */
  readonly book = input.required<LabelBook>();

  readonly payslips = computed<Payslip[]>(() =>
    this.lines().map((line) => ({
      line,
      groups: groupElements(line.elements),
      withheld: totalWithheld(line),
    })),
  );

  readonly period = computed(() =>
    periodLabel({ year: this.run().periodYear, month: this.run().periodMonth }, this.ctx().lang),
  );

  readonly branch = computed(() => branchLine(this.ctx(), this.run().branchId));

  /** The contribution's and the tax's own names; a company that named neither still prints a row. */
  readonly socialName = computed(() => labelIn(this.book().social, this.ctx().lang) || this.ctx().t('hr.run.tile.socialFallback'));
  readonly taxName = computed(() => labelIn(this.book().incomeTax, this.ctx().lang) || this.ctx().t('hr.run.tile.taxFallback'));

  /** Under the number: which run this sheet belongs to, its month, and what it comes to. */
  meta(slip: Payslip): LabelledLine[] {
    const c = this.ctx();
    return [
      { label: c.t('print.payslip.runNumber'), value: this.run().number },
      { label: c.t('print.payslip.period'), value: this.period() },
      { label: c.t('print.payslip.net'), value: c.money(slip.line.net) },
    ];
  }

  label(element: { key: string; kind: ElementGroup['kind'] }): string {
    return elementLabel(element, this.book(), this.ctx().lang, (key) => this.ctx().t(key));
  }

  /** The net in words; '' when the currency profile names no units in this language. */
  words(slip: Payslip): string {
    return this.ctx().words(slip.line.net);
  }
}
