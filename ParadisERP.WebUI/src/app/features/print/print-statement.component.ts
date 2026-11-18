import { Component, computed, input } from '@angular/core';
import { CreditNote, CustomerAgeing, CustomerStatement, ReceiptVoucher } from '../backoffice/receivables/receivables-api.service';
import { describeStatementRow, statementSourceLabel } from '../backoffice/receivables/statement-rows';
import { PrintDocumentComponent } from './print-document.component';
import { LabelledLine, PrintContext, partyLines, utc } from './print-context';

/**
 * The customer statement on A4 portrait (docs/07 §4.4): account 411 for one customer over a
 * period — opening, every movement, closing.
 *
 * <p><b>Stated, never netted.</b> The running balance is the ledger's own; beside it the sheet
 * says the four things the ledger cannot: till paper not yet cleared, the total actually owed,
 * debt no invoice covers, and the credit limit. The drift between the recorded customer balance
 * and this statement prints only when it is not zero — a zero drift is the normal case, and a
 * row saying "0.00" would only invite the reader to wonder what it means.</p>
 *
 * <p>The period states the <b>last day the window covers</b>: `to` is exclusive on the wire, and
 * a customer reading "to the 1st" on a statement that stops at the 31st would be right to
 * complain.</p>
 */
@Component({
  selector: 'app-print-statement',
  standalone: true,
  imports: [PrintDocumentComponent],
  template: `
    @if (ctx(); as c) {
      @for (copy of copyIndexes(); track copy) {
        <app-print-sheet
          [ctx]="c"
          kind="statement"
          [title]="c.t('print.stmt.title')"
          titleKey="print.stmt.title"
          [number]="statement().customerCode"
          [meta]="meta()"
          [breakBefore]="copy > 0"
        >
          <div class="print-parties">
            <div class="print-block">
              <span class="print-block__title">{{ c.t('common.customer') }}</span>
              @for (line of party(); track $index) {
                <div [class.print-line--lead]="!line.label">
                  @if (line.label) {
                    <span class="print-line__label">{{ line.label }}</span>
                  }
                  <span [class.mono]="!!line.label" [attr.dir]="line.ltr ? 'ltr' : null">{{ line.value }}</span>
                </div>
              }
            </div>
            <div class="print-block">
              <span class="print-block__title">{{ c.t('print.stmt.summary') }}</span>
              <div>
                <span class="print-line__label">{{ c.t('acc.st.opening') }}</span>
                <span class="mono">{{ c.money(statement().openingBalance) }}</span>
              </div>
              <div class="print-line--lead">
                <span class="print-line__label">{{ c.t('acc.st.closing') }}</span>
                <span class="mono">{{ c.money(statement().closingBalance) }}</span>
              </div>
              @if (statement().closingBalance < 0) {
                <div>{{ c.t('inv.companyOwes') }}</div>
              }
            </div>
          </div>

          <p class="print-sentence">{{ period() }}</p>
          @if (statement().filter && statement().filter !== 'all') {
            <p class="print-sentence">{{ c.t('stmt.filter.partial', { part: c.t('stmt.filter.' + statement().filter) }) }}</p>
          }

          <table class="print-table print-table--statement">
            <thead>
              <tr>
                <th>{{ c.t('receipt.date') }}</th>
                <th>{{ c.t('stmt.kind.column') }}</th>
                <th>{{ c.t('inv.stmt.document') }}</th>
                <th>{{ c.t('inv.stmt.invoicedAs') }}</th>
                <th>{{ c.t('inv.stmt.description') }}</th>
                <th class="num">{{ c.t('acc.debit') }}</th>
                <th class="num">{{ c.t('acc.credit') }}</th>
                <th class="num">{{ c.t('treasury.balance') }}</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td>{{ c.t('acc.st.opening') }}</td>
                <td></td>
                <td></td>
                <td></td>
                <td></td>
                <td></td>
                <td></td>
                <td class="num mono">{{ c.money(statement().openingBalance) }}</td>
              </tr>
              @for (row of statement().rows; track $index) {
                <tr>
                  <td class="mono">{{ c.day(row.date) }}</td>
                  <td>{{ row.kind ? c.t('stmt.kind.' + row.kind) : '' }}</td>
                  <td>
                    <span class="mono">{{ row.documentNumber || '—' }}</span>
                    <span class="print-soft"> {{ sourceWord(row.sourceType) }}</span>
                  </td>
                  <td class="mono">{{ row.invoiceNumber || '' }}</td>
                  <td>
                    <span>{{ detail(row) }}</span>
                    @if (row.isReversal) {
                      <span class="print-soft"> {{ c.t('inv.stmt.reversal') }}</span>
                    }
                    @if (row.sourceType === 'sale' && row.paidAtTill > 0) {
                      <div>
                        <span>{{ c.t('stmt.kind.paidAtTill') }} </span>
                        <span class="mono">{{ c.money(row.paidAtTill) }}</span>
                      </div>
                    }
                  </td>
                  <td class="num mono">{{ row.debit ? c.money(row.debit) : '' }}</td>
                  <td class="num mono">{{ row.credit ? c.money(row.credit) : '' }}</td>
                  <td class="num mono">{{ c.money(row.balance) }}</td>
                </tr>
              } @empty {
                <tr>
                  <td colspan="8">{{ c.t('inv.stmt.none') }}</td>
                </tr>
              }
            </tbody>
            <tfoot>
              <tr>
                <td>{{ c.t('common.total') }}</td>
                <td></td>
                <td></td>
                <td></td>
                <td></td>
                <td class="num mono">{{ c.money(statement().totalDebit) }}</td>
                <td class="num mono">{{ c.money(statement().totalCredit) }}</td>
                <td></td>
              </tr>
              <tr class="print-total">
                <td>{{ c.t('acc.st.closing') }}</td>
                <td></td>
                <td></td>
                <td></td>
                <td></td>
                <td></td>
                <td></td>
                <td class="num mono">{{ c.money(statement().closingBalance) }}</td>
              </tr>
            </tfoot>
          </table>

          <table class="print-table print-table--compact">
            <caption>{{ c.t('print.stmt.stated') }}</caption>
            <tbody>
              <tr>
                <td>{{ c.t('inv.acct.pendingNotes') }}</td>
                <td class="num mono">{{ c.money(statement().pendingNotes) }}</td>
              </tr>
              <tr class="print-total">
                <td>{{ c.t('inv.acct.totalOwed') }}</td>
                <td class="num mono">{{ c.money(statement().totalOwed) }}</td>
              </tr>
              <tr>
                <td>{{ c.t('inv.acct.uninvoiced') }}</td>
                <td class="num mono">{{ c.money(statement().uninvoicedBalance) }}</td>
              </tr>
              <tr>
                <td>{{ c.t('backoffice.customers.creditLimit') }}</td>
                <td class="num mono">
                  {{ statement().creditLimit > 0 ? c.money(statement().creditLimit) : c.t('inv.limit.none') }}
                </td>
              </tr>
              @if (statement().balanceDifference !== 0) {
                <tr>
                  <td>{{ c.t('inv.acct.recorded') }}</td>
                  <td class="num mono">{{ c.money(statement().recordedBalance) }}</td>
                </tr>
              }
            </tbody>
          </table>

          @if (statement().balanceDifference !== 0) {
            <p class="print-sentence">{{ c.t('inv.acct.drift', { v: c.money(statement().balanceDifference) }) }}</p>
          }

          @if (ageing(); as a) {
            <table class="print-table">
              <caption>{{ c.t('print.stmt.ageing', { d: asOfDay() }) }}</caption>
              <thead>
                <tr>
                  <th class="num">{{ c.t('inv.bucket.Current') }}</th>
                  <th class="num">{{ c.t('inv.bucket.Days1To30') }}</th>
                  <th class="num">{{ c.t('inv.bucket.Days31To60') }}</th>
                  <th class="num">{{ c.t('inv.bucket.Days61To90') }}</th>
                  <th class="num">{{ c.t('inv.bucket.Over90') }}</th>
                  <th>{{ c.t('inv.home.oldest') }}</th>
                </tr>
              </thead>
              <tbody>
                <tr>
                  <td class="num mono">{{ c.money(a.current) }}</td>
                  <td class="num mono">{{ c.money(a.days1To30) }}</td>
                  <td class="num mono">{{ c.money(a.days31To60) }}</td>
                  <td class="num mono">{{ c.money(a.days61To90) }}</td>
                  <td class="num mono">{{ c.money(a.over90) }}</td>
                  <td>
                    {{ a.oldestDaysOverdue > 0 ? c.t('inv.days', { n: a.oldestDaysOverdue }) : c.t('inv.notLate') }}
                  </td>
                </tr>
              </tbody>
            </table>
          }
        </app-print-sheet>
      }
    }
  `,
})
export class PrintStatementComponent {
  readonly ctx = input.required<PrintContext>();
  readonly statement = input.required<CustomerStatement>();
  /** This customer's own ageing strip, or null when that call could not be made. */
  readonly ageing = input<CustomerAgeing | null>(null);
  /** The day the ageing was counted at, as the route asked for it. */
  readonly asOf = input<string | null>(null);
  readonly copies = input(1);
  /** This customer's credit notes and receipts: what says which invoice a note corrects and how a receipt was paid. */
  readonly creditNotes = input<readonly CreditNote[]>([]);
  readonly receipts = input<readonly ReceiptVoucher[]>([]);

  readonly copyIndexes = computed(() => Array.from({ length: Math.max(1, this.copies()) }, (_, i) => i));

  readonly customer = computed(() => this.ctx().customers.find((x) => x.id === this.statement().customerId) ?? null);
  readonly party = computed(() => partyLines(this.ctx(), this.customer(), { name: this.statement().customerName }));

  readonly meta = computed<LabelledLine[]>(() => {
    const c = this.ctx();
    return [{ label: c.t('inv.stmt.title'), value: `${c.day(this.statement().from)} – ${c.day(this.lastDay())}` }];
  });

  /** The window in words, with the end stated as the last day it covers. */
  readonly period = computed(() => {
    const c = this.ctx();
    return c.t('print.stmt.period', { from: c.day(this.statement().from), to: c.day(this.lastDay()) });
  });

  readonly asOfDay = computed(() => this.ctx().day(this.asOf() ?? this.lastDay()));

  /** The word for a source, in the document's language (statement-rows.ts, shared with the screen). */
  sourceWord(type: string): string {
    return statementSourceLabel(type, (key) => this.ctx().t(key));
  }

  /**
   * What the kind and number columns cannot say, in the document's language: which invoice a
   * credit note corrects, what tender a receipt came in under. The ledger's own memo is English
   * and prints only for a kind nobody has named — the rule the screen follows (statement-rows.ts).
   */
  detail(row: CustomerStatement['rows'][number]): string {
    const c = this.ctx();
    return describeStatementRow(
      row,
      { creditNotes: this.creditNotes(), receipts: this.receipts(), methods: c.methods },
      c.lang,
      (key, params) => c.t(key, params),
    );
  }

  /** `to` is exclusive on the wire; paper states the last day the window covers. */
  private lastDay(): string {
    const end = new Date(utc(this.statement().to));
    end.setUTCDate(end.getUTCDate() - 1);
    return end.toISOString();
  }
}
