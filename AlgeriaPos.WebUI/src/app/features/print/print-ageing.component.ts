import { Component, computed, input } from '@angular/core';
import { CustomerAgeing, DebtAgeing } from '../backoffice/receivables/receivables-api.service';
import { PrintDocumentComponent } from './print-document.component';
import { LabelledLine, PrintContext } from './print-context';

/**
 * The debt ageing on **A4 landscape** (docs/07 §4.4): who owes, how much of it is late, and how
 * old the oldest of it is, as at one day.
 *
 * <p>The screen's bar and its action column do not come to paper — one is a picture of a
 * percentage the buckets already state, the other is a button. What does come is the screen's
 * own order, latest first and then largest, so the sheet in the owner's hand reads in the same
 * order as the board he printed it from; the server's alphabetical order answers a different
 * question.</p>
 *
 * <p>The credit limit is a <b>word</b> — over, within, none — beside its figure, never a colour:
 * this sheet is printed in black.</p>
 */
@Component({
  selector: 'app-print-ageing',
  standalone: true,
  imports: [PrintDocumentComponent],
  template: `
    @if (ctx(); as c) {
      @for (copy of copyIndexes(); track copy) {
        <app-print-sheet
          [ctx]="c"
          [kind]="null"
          [title]="c.t('print.ageing.title')"
          titleKey="print.ageing.title"
          [number]="c.day(ageing().asOf)"
          [meta]="meta()"
          [breakBefore]="copy > 0"
        >
          <table class="print-table print-table--wide">
            <thead>
              <tr>
                <th>{{ c.t('common.customer') }}</th>
                <th class="num">{{ c.t('common.total') }}</th>
                <th class="num">{{ c.t('inv.bucket.Current') }}</th>
                <th class="num">{{ c.t('inv.bucket.Days1To30') }}</th>
                <th class="num">{{ c.t('inv.bucket.Days31To60') }}</th>
                <th class="num">{{ c.t('inv.bucket.Days61To90') }}</th>
                <th class="num">{{ c.t('inv.bucket.Over90') }}</th>
                <th>{{ c.t('inv.home.oldest') }}</th>
                <th class="num">{{ c.t('inv.home.uninvoiced') }}</th>
                <th>{{ c.t('backoffice.customers.creditLimit') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (row of rows(); track row.customerId) {
                <tr>
                  <td>
                    <span>{{ row.customerName }}</span>
                    <span class="print-soft"> <bdi class="mono" dir="ltr">{{ row.customerCode }}</bdi></span>
                    @if (row.creditBalance > 0) {
                      <span class="print-soft"> {{ c.t('inv.companyOwes') }} {{ c.money(row.creditBalance) }}</span>
                    }
                  </td>
                  <td class="num mono">{{ c.money(row.total) }}</td>
                  <td class="num mono">{{ cell(row.current) }}</td>
                  <td class="num mono">{{ cell(row.days1To30) }}</td>
                  <td class="num mono">{{ cell(row.days31To60) }}</td>
                  <td class="num mono">{{ cell(row.days61To90) }}</td>
                  <td class="num mono">{{ cell(row.over90) }}</td>
                  <td>
                    {{ row.oldestDaysOverdue > 0 ? c.t('inv.days', { n: row.oldestDaysOverdue }) : c.t('inv.notLate') }}
                  </td>
                  <td class="num mono">{{ cell(row.uninvoiced) }}</td>
                  <td>
                    @if (row.creditLimit > 0) {
                      <span>{{ c.t(row.overCreditLimit ? 'inv.limit.over' : 'inv.limit.within') }}</span>
                      <span class="print-soft mono"> {{ c.money(row.creditLimit) }}</span>
                    } @else {
                      <span>{{ c.t('inv.limit.none') }}</span>
                    }
                  </td>
                </tr>
              } @empty {
                <tr>
                  <td colspan="10">{{ c.t('inv.home.none') }}</td>
                </tr>
              }
            </tbody>
            <tfoot>
              <tr class="print-total">
                <td>{{ c.t('common.total') }}</td>
                <td class="num mono">{{ c.money(ageing().total) }}</td>
                <td class="num mono">{{ c.money(ageing().current) }}</td>
                <td class="num mono">{{ c.money(ageing().days1To30) }}</td>
                <td class="num mono">{{ c.money(ageing().days31To60) }}</td>
                <td class="num mono">{{ c.money(ageing().days61To90) }}</td>
                <td class="num mono">{{ c.money(ageing().over90) }}</td>
                <td></td>
                <td class="num mono">{{ c.money(ageing().uninvoiced) }}</td>
                <td></td>
              </tr>
            </tfoot>
          </table>

          <p class="print-sentence">{{ c.t('inv.home.note') }} {{ c.t('inv.home.limitHint') }}</p>
        </app-print-sheet>
      }
    }
  `,
})
export class PrintAgeingComponent {
  readonly ctx = input.required<PrintContext>();
  readonly ageing = input.required<DebtAgeing>();
  readonly copies = input(1);

  readonly copyIndexes = computed(() => Array.from({ length: Math.max(1, this.copies()) }, (_, i) => i));

  /** The board's order, not the server's: the customer to call first is the first row. */
  readonly rows = computed<CustomerAgeing[]>(() =>
    [...this.ageing().customers].sort((a, b) => b.oldestDaysOverdue - a.oldestDaysOverdue || b.total - a.total),
  );

  readonly meta = computed<LabelledLine[]>(() => {
    const c = this.ctx();
    return [{ label: c.t('inv.home.asOf'), value: c.day(this.ageing().asOf) }];
  });

  /** A zero is an empty cell: a row of zeros reads as the gaps it is. */
  cell(value: number): string {
    return value ? this.ctx().money(value) : '';
  }
}
