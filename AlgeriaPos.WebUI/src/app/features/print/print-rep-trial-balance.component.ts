import { Component, computed, input } from '@angular/core';
import { TrialBalance, TrialBalanceRow } from '../../core/ledger.service';
import { PrintContext } from './print-context';
import { ReportPaper, accountWord, natureWord, sideWord } from './print-reports';

/**
 * The trial balance on paper: every account that moved in the window, its debits and credits, the
 * balance on its own side, and the two totals that must agree.
 *
 * <p>Whether they agree is a word in the summary, never a colour. A balance on the side its
 * account does not belong on carries the screen's "to check" beside the side it landed on — the
 * one row a trial balance exists to find.</p>
 */
@Component({
  selector: 'app-print-rep-trial-balance',
  standalone: true,
  template: `
    @if (ctx(); as c) {
      @if (trial(); as t) {
        <dl class="print-figures">
          <div>
            <dt>{{ c.t('acc.totalDebit') }}</dt>
            <dd class="mono">{{ c.money(t.totalDebit) }}</dd>
          </div>
          <div>
            <dt>{{ c.t('acc.totalCredit') }}</dt>
            <dd class="mono">{{ c.money(t.totalCredit) }}</dd>
          </div>
          <div>
            <dt>{{ c.t('common.state') }}</dt>
            <dd>{{ c.t(t.balanced ? 'acc.balanced' : 'acc.unbalanced') }}</dd>
          </div>
        </dl>
        @if (!t.balanced) {
          <p class="print-notice"><strong>{{ c.t('acc.unbalanced') }}</strong>{{ c.t('acc.unbalancedHint') }}</p>
        }

        <table class="print-table print-table--statement">
          <thead>
            <tr>
              <th>{{ c.t('acc.account') }}</th>
              <th>{{ c.t('common.meaning') }}</th>
              <th class="num">{{ c.t('acc.debit') }}</th>
              <th class="num">{{ c.t('acc.credit') }}</th>
              <th class="num">{{ c.t('treasury.balance') }}</th>
              <th>{{ c.t('acc.side') }}</th>
            </tr>
          </thead>
          <tbody>
            @for (row of rows(); track row.accountId) {
              <tr>
                <td>
                  <bdi class="mono" dir="ltr">{{ row.code }}</bdi><span> {{ account(row) }}</span>
                </td>
                <td>{{ nature(row) }}</td>
                <td class="num mono">{{ row.debit ? c.money(row.debit) : '' }}</td>
                <td class="num mono">{{ row.credit ? c.money(row.credit) : '' }}</td>
                <td class="num mono">{{ c.money(abs(row.balance)) }}</td>
                <td>
                  <span>{{ side(row) }}</span>
                  @if (row.balance < 0) {
                    <span class="print-soft">{{ c.t('acc.check') }}</span>
                  }
                </td>
              </tr>
            } @empty {
              <tr>
                <td colspan="6">{{ c.t('acc.empty') }}</td>
              </tr>
            }
          </tbody>
          <tfoot>
            <tr class="print-total">
              <td colspan="2">{{ c.t('common.total') }}</td>
              <td class="num mono">{{ c.money(t.totalDebit) }}</td>
              <td class="num mono">{{ c.money(t.totalCredit) }}</td>
              <td colspan="2"></td>
            </tr>
          </tfoot>
        </table>

        <p class="print-sentence">{{ c.t('acc.doubleEntry') }} {{ c.t('acc.natureLegend') }}</p>
      }
    }
  `,
})
export class PrintRepTrialBalanceComponent {
  readonly ctx = input.required<PrintContext>();
  readonly report = input.required<ReportPaper>();

  readonly trial = computed<TrialBalance | null>(() => {
    const r = this.report();
    return r.name === 'trial-balance' ? r.trial : null;
  });

  /** By account code, the chart's own order: a reader looks up 411 between 401 and 512. */
  readonly rows = computed(() => [...(this.trial()?.rows ?? [])].sort((a, b) => a.code.localeCompare(b.code, 'en', { numeric: true })));

  account(row: TrialBalanceRow): string {
    return accountWord(this.ctx(), row);
  }

  nature(row: TrialBalanceRow): string {
    return natureWord(this.ctx(), row.type);
  }

  side(row: TrialBalanceRow): string {
    return sideWord(this.ctx(), row);
  }

  abs(value: number): number {
    return Math.abs(value);
  }
}
