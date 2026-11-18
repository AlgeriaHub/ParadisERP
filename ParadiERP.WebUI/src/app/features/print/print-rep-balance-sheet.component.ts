import { Component, computed, input } from '@angular/core';
import { BalanceSheet, BalanceSheetLine } from '../../core/ledger.service';
import { PrintContext } from './print-context';
import { ReportPaper, accountWord } from './print-reports';

/**
 * The balance sheet on paper: what the company owns, then what it owes and what was put in, with
 * the period's result on the claims side — there is no year-end close, so the result is computed
 * at read time and stated as its own line (the screen's rule). Whether the two sides agree is a
 * word in the summary.
 */
@Component({
  selector: 'app-print-rep-balance-sheet',
  standalone: true,
  template: `
    @if (ctx(); as c) {
      @if (sheet(); as b) {
        <dl class="print-figures">
          <div>
            <dt>{{ c.t('acc.bs.totalAssets') }}</dt>
            <dd class="mono">{{ c.money(b.totalAssets) }}</dd>
          </div>
          <div>
            <dt>{{ c.t('acc.bs.totalClaims') }}</dt>
            <dd class="mono">{{ c.money(claims()) }}</dd>
          </div>
          <div>
            <dt>{{ c.t('common.state') }}</dt>
            <dd>{{ c.t(b.balanced ? 'acc.balanced' : 'acc.unbalanced') }}</dd>
          </div>
        </dl>

        <table class="print-table print-table--statement">
          <caption>{{ c.t('acc.bs.assets') }}</caption>
          <thead>
            <tr>
              <th>{{ c.t('acc.account') }}</th>
              <th class="num">{{ c.t('pay.amount') }}</th>
            </tr>
          </thead>
          <tbody>
            @for (line of b.assets; track line.code) {
              <tr>
                <td><bdi class="mono" dir="ltr">{{ line.code }}</bdi><span> {{ account(line) }}</span></td>
                <td class="num mono">{{ c.money(line.amount) }}</td>
              </tr>
            } @empty {
              <tr>
                <td colspan="2">{{ c.t('acc.empty') }}</td>
              </tr>
            }
          </tbody>
          <tfoot>
            <tr class="print-total">
              <td>{{ c.t('acc.bs.totalAssets') }}</td>
              <td class="num mono">{{ c.money(b.totalAssets) }}</td>
            </tr>
          </tfoot>
        </table>

        <table class="print-table print-table--statement">
          <caption>{{ c.t('acc.bs.totalClaims') }}</caption>
          <thead>
            <tr>
              <th>{{ c.t('acc.account') }}</th>
              <th class="num">{{ c.t('pay.amount') }}</th>
            </tr>
          </thead>
          <tbody>
            @for (line of b.liabilities; track line.code) {
              <tr>
                <td><bdi class="mono" dir="ltr">{{ line.code }}</bdi><span> {{ account(line) }}</span></td>
                <td class="num mono">{{ c.money(line.amount) }}</td>
              </tr>
            }
            @for (line of b.equity; track line.code) {
              <tr>
                <td><bdi class="mono" dir="ltr">{{ line.code }}</bdi><span> {{ account(line) }}</span></td>
                <td class="num mono">{{ c.money(line.amount) }}</td>
              </tr>
            }
            <tr>
              <td>
                <span>{{ c.t('acc.bs.netResult') }}</span>
                <span class="print-soft">{{ c.t(b.netResult < 0 ? 'acc.pl.loss' : 'acc.pl.profit') }}</span>
              </td>
              <td class="num mono">{{ c.money(b.netResult) }}</td>
            </tr>
          </tbody>
          <tfoot>
            <tr class="print-total">
              <td>{{ c.t('acc.bs.totalClaims') }}</td>
              <td class="num mono">{{ c.money(claims()) }}</td>
            </tr>
          </tfoot>
        </table>

        <p class="print-sentence">{{ c.t('acc.bs.equation') }}</p>
        <p class="print-sentence">{{ c.t('acc.bs.netResultNote') }}</p>
        @if (!b.consolidated) {
          <p class="print-sentence">{{ c.t('acc.bs.interBranchNote') }}</p>
        }
        <p class="print-sentence">{{ c.t('acc.openingNote') }}</p>
      }
    }
  `,
})
export class PrintRepBalanceSheetComponent {
  readonly ctx = input.required<PrintContext>();
  readonly report = input.required<ReportPaper>();

  readonly sheet = computed<BalanceSheet | null>(() => {
    const r = this.report();
    return r.name === 'balance-sheet' ? r.sheet : null;
  });

  /** Liabilities, equity and the period's result: the side that has to equal the assets. */
  readonly claims = computed(() => {
    const b = this.sheet();
    return b ? b.totalLiabilities + b.totalEquity + b.netResult : 0;
  });

  account(line: BalanceSheetLine): string {
    return accountWord(this.ctx(), line);
  }
}
