import { Component, computed, input } from '@angular/core';
import { AccountStatement, AccountStatementRow } from '../../core/ledger.service';
import { PrintContext } from './print-context';
import { ReportPaper, STATEMENT_PAPER_TAKE, accountWord, natureWord, scopeText, sourceWord } from './print-reports';

/**
 * One account's movements on paper: the balance it opened the window with, every entry in date
 * order with the running balance, and the balance it closed with.
 *
 * <p><b>A cut statement says it is cut.</b> The server returns at most a fixed number of
 * movements, and its closing figure is the running balance after the last one it returned — not
 * the account's balance at the end of the window. The screen warned about this; the paper used to
 * print the figure under "closing balance" regardless. When the ceiling is reached the sheet now
 * says so, and the last line is labelled as the balance at that row.</p>
 *
 * <p>The branch column is drawn for the group only: on a one-branch statement every row would
 * repeat the scope already printed in the head.</p>
 */
@Component({
  selector: 'app-print-rep-account-statement',
  standalone: true,
  template: `
    @if (ctx(); as c) {
      @if (statement(); as s) {
        <div class="print-parties">
          <div class="print-block">
            <span class="print-block__title">{{ c.t('acc.account') }}</span>
            <div class="print-line--lead"><bdi class="mono" dir="ltr">{{ s.code }}</bdi><span> {{ account() }}</span></div>
            <div>{{ nature() }}</div>
          </div>
          <dl class="print-figures print-figures--block">
            <div>
              <dt>{{ c.t('acc.st.opening') }}</dt>
              <dd class="mono">{{ c.money(s.opening) }}</dd>
            </div>
            <div>
              <dt>{{ c.t(capped() ? 'print.rep.st.lastRow' : 'acc.st.closing') }}</dt>
              <dd class="mono">{{ c.money(s.closing) }}</dd>
            </div>
          </dl>
        </div>

        @if (capped()) {
          <p class="print-notice"><strong>{{ c.t('print.rep.cappedTitle') }}</strong>{{ c.t('print.rep.st.capped', { n: s.rows.length }) }}</p>
        }

        <table class="print-table print-table--statement">
          <thead>
            <tr>
              <th>{{ c.t('receipt.date') }}</th>
              <th>{{ c.t('acc.entry.document') }}</th>
              @if (group()) {
                <th>{{ c.t('backoffice.tabs.branches') }}</th>
              }
              <th class="num">{{ c.t('acc.debit') }}</th>
              <th class="num">{{ c.t('acc.credit') }}</th>
              <th class="num">{{ c.t('treasury.balance') }}</th>
            </tr>
          </thead>
          <tbody>
            <tr>
              <td [attr.colspan]="leadCols()">{{ c.t('acc.st.opening') }}</td>
              <td></td>
              <td></td>
              <td class="num mono">{{ c.money(s.opening) }}</td>
            </tr>
            @for (row of s.rows; track $index) {
              <tr>
                <td class="mono">{{ c.day(row.entryDate) }}</td>
                <td>
                  <span>{{ source(row) }}</span>
                  @if (row.isReversal) {
                    <span class="print-soft">{{ c.t('acc.entry.reversal') }}</span>
                  }
                  @if (row.description) {
                    <span class="print-soft">{{ row.description }}</span>
                  }
                </td>
                @if (group()) {
                  <td>{{ branch(row.branchId) }}</td>
                }
                <td class="num mono">{{ row.debit ? c.money(row.debit) : '' }}</td>
                <td class="num mono">{{ row.credit ? c.money(row.credit) : '' }}</td>
                <td class="num mono">{{ c.money(row.runningBalance) }}</td>
              </tr>
            } @empty {
              <tr>
                <td [attr.colspan]="leadCols() + 3">{{ c.t('acc.empty') }}</td>
              </tr>
            }
          </tbody>
          <tfoot>
            <tr>
              <td [attr.colspan]="leadCols()">{{ c.t('common.total') }}</td>
              <td class="num mono">{{ c.money(debit()) }}</td>
              <td class="num mono">{{ c.money(credit()) }}</td>
              <td></td>
            </tr>
            <tr class="print-total">
              <td [attr.colspan]="leadCols()">{{ c.t(capped() ? 'print.rep.st.lastRow' : 'acc.st.closing') }}</td>
              <td></td>
              <td></td>
              <td class="num mono">{{ c.money(s.closing) }}</td>
            </tr>
          </tfoot>
        </table>
      }
    }
  `,
})
export class PrintRepAccountStatementComponent {
  readonly ctx = input.required<PrintContext>();
  readonly report = input.required<ReportPaper>();

  readonly statement = computed<AccountStatement | null>(() => {
    const r = this.report();
    return r.name === 'account-statement' ? r.statement : null;
  });

  /** The tab asked for the server's ceiling; a full page of it means there may be more. */
  readonly capped = computed(() => {
    const r = this.report();
    return r.name === 'account-statement' && (r.capped || r.statement.rows.length >= STATEMENT_PAPER_TAKE);
  });

  readonly group = computed(() => this.report().branchId === null);
  /** Date, document and — for the group — branch, before the three money columns. */
  readonly leadCols = computed(() => (this.group() ? 3 : 2));

  readonly debit = computed(() => (this.statement()?.rows ?? []).reduce((sum, r) => sum + r.debit, 0));
  readonly credit = computed(() => (this.statement()?.rows ?? []).reduce((sum, r) => sum + r.credit, 0));

  account(): string {
    const s = this.statement();
    return s ? accountWord(this.ctx(), s) : '';
  }

  nature(): string {
    const s = this.statement();
    return s ? natureWord(this.ctx(), s.type) : '';
  }

  source(row: AccountStatementRow): string {
    return sourceWord(this.ctx(), row.sourceType);
  }

  branch(id: string): string {
    return scopeText(this.ctx(), id);
  }
}
