import { Component, computed, input } from '@angular/core';
import { ProfitAndLoss, ProfitAndLossLine } from '../../core/ledger.service';
import { PrintContext } from './print-context';
import { ReportPaper, accountWord } from './print-reports';

/**
 * The profit and loss account on paper: income, then expenses, then the result — profit or loss
 * said as a word beside its figure, since a minus sign is the easiest mark on a page to miss.
 *
 * <p>For the group it opens with each branch's own result and what consolidation took out, as
 * the screen does: the consolidated figure is only believable next to the parts it came from.</p>
 */
@Component({
  selector: 'app-print-rep-profit-loss',
  standalone: true,
  template: `
    @if (ctx(); as c) {
      @if (pl(); as p) {
        <dl class="print-figures">
          <div>
            <dt>{{ c.t('acc.pl.income') }}</dt>
            <dd class="mono">{{ c.money(p.totalIncome) }}</dd>
          </div>
          <div>
            <dt>{{ c.t('acc.pl.expenses') }}</dt>
            <dd class="mono">{{ c.money(p.totalExpenses) }}</dd>
          </div>
          <div>
            <dt>{{ c.t('acc.pl.net') }}</dt>
            <dd class="mono">{{ c.money(p.netProfit) }}</dd>
            <dd class="print-figures__word">{{ c.t(p.netProfit < 0 ? 'acc.pl.loss' : 'acc.pl.profit') }}</dd>
          </div>
        </dl>

        @if (p.consolidated && p.byBranch.length) {
          <table class="print-table print-table--statement">
            <caption>{{ c.t('acc.pl.byBranch') }}</caption>
            <thead>
              <tr>
                <th>{{ c.t('backoffice.tabs.branches') }}</th>
                <th class="num">{{ c.t('acc.pl.income') }}</th>
                <th class="num">{{ c.t('acc.pl.expenses') }}</th>
                <th class="num">{{ c.t('acc.pl.net') }}</th>
                <th>{{ c.t('common.state') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (b of p.byBranch; track b.branchId) {
                <tr>
                  <td>{{ branchName(b.branchId, b.branchName) }}</td>
                  <td class="num mono">{{ c.money(b.income) }}</td>
                  <td class="num mono">{{ c.money(b.expenses) }}</td>
                  <td class="num mono">{{ c.money(b.netProfit) }}</td>
                  <td>{{ c.t(b.netProfit < 0 ? 'acc.pl.loss' : 'acc.pl.profit') }}</td>
                </tr>
              }
            </tbody>
            <tfoot>
              @if (p.eliminatedInterBranch !== 0) {
                <tr>
                  <td colspan="3">{{ c.t('acc.pl.eliminated') }}</td>
                  <td class="num mono">{{ c.money(p.eliminatedInterBranch) }}</td>
                  <td></td>
                </tr>
              }
              <tr class="print-total">
                <td>{{ c.t('acc.pl.consolidatedNet') }}</td>
                <td class="num mono">{{ c.money(p.totalIncome) }}</td>
                <td class="num mono">{{ c.money(p.totalExpenses) }}</td>
                <td class="num mono">{{ c.money(p.netProfit) }}</td>
                <td></td>
              </tr>
            </tfoot>
          </table>
          <p class="print-sentence">{{ c.t('acc.pl.consolidatedNote') }}</p>
        }

        <table class="print-table print-table--statement">
          <caption>{{ c.t('acc.pl.income') }}</caption>
          <thead>
            <tr>
              <th>{{ c.t('acc.account') }}</th>
              <th class="num">{{ c.t('pay.amount') }}</th>
            </tr>
          </thead>
          <tbody>
            @for (line of p.income; track line.code) {
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
              <td>{{ c.t('common.total') }}</td>
              <td class="num mono">{{ c.money(p.totalIncome) }}</td>
            </tr>
          </tfoot>
        </table>

        <table class="print-table print-table--statement">
          <caption>{{ c.t('acc.pl.expenses') }}</caption>
          <thead>
            <tr>
              <th>{{ c.t('acc.account') }}</th>
              <th class="num">{{ c.t('pay.amount') }}</th>
            </tr>
          </thead>
          <tbody>
            @for (line of p.expenses; track line.code) {
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
              <td>{{ c.t('common.total') }}</td>
              <td class="num mono">{{ c.money(p.totalExpenses) }}</td>
            </tr>
          </tfoot>
        </table>

        <table class="print-table print-table--compact">
          <tbody>
            <tr>
              <td>{{ c.t('acc.pl.income') }}</td>
              <td class="num mono">{{ c.money(p.totalIncome) }}</td>
            </tr>
            <tr>
              <td>{{ c.t('acc.pl.expenses') }}</td>
              <td class="num mono">{{ c.money(p.totalExpenses) }}</td>
            </tr>
            <tr class="print-total">
              <td>{{ c.t('acc.pl.net') }} · {{ c.t(p.netProfit < 0 ? 'acc.pl.loss' : 'acc.pl.profit') }}</td>
              <td class="num mono">{{ c.money(p.netProfit) }}</td>
            </tr>
          </tbody>
        </table>
      }
    }
  `,
})
export class PrintRepProfitLossComponent {
  readonly ctx = input.required<PrintContext>();
  readonly report = input.required<ReportPaper>();

  readonly pl = computed<ProfitAndLoss | null>(() => {
    const r = this.report();
    return r.name === 'profit-loss' ? r.pl : null;
  });

  account(line: ProfitAndLossLine): string {
    return accountWord(this.ctx(), line);
  }

  /** The branch in the document language; the server sends its French name only. */
  branchName(id: string, fallback: string): string {
    const branch = this.ctx().branches.find((b) => b.id === id);
    return branch ? this.ctx().name(branch) : fallback;
  }
}
