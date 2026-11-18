import { Component, computed, input } from '@angular/core';
import { LedgerEntry, LedgerEntryLine } from '../../core/ledger.service';
import { PrintContext } from './print-context';
import { JOURNAL_PAPER_TAKE, ReportPaper, accountWord, scopeText, sourceWord } from './print-reports';

/**
 * The journal on paper — the book itself, not the screen's list of entries: each entry in date
 * order with the accounts it moved, debit and credit, and at the foot the two totals that a
 * double-entry journal can only print equal.
 *
 * <p>The screen lists the newest first because the reader there is looking for what just
 * happened; a journal is read the way it was written, oldest first. The server returns the newest
 * entries up to its ceiling, so a journal that reaches it is missing its oldest entries and says
 * so rather than starting mid-period without a word.</p>
 */
@Component({
  selector: 'app-print-rep-journal',
  standalone: true,
  template: `
    @if (ctx(); as c) {
      <dl class="print-figures">
        <div>
          <dt>{{ c.t('print.rep.journal.entries') }}</dt>
          <dd class="mono">{{ entries().length }}</dd>
        </div>
        <div>
          <dt>{{ c.t('acc.totalDebit') }}</dt>
          <dd class="mono">{{ c.money(debit()) }}</dd>
        </div>
        <div>
          <dt>{{ c.t('acc.totalCredit') }}</dt>
          <dd class="mono">{{ c.money(credit()) }}</dd>
        </div>
      </dl>

      @if (capped()) {
        <p class="print-notice"><strong>{{ c.t('print.rep.cappedTitle') }}</strong>{{ c.t('print.rep.journal.capped', { n: entries().length }) }}</p>
      }

      <table class="print-table print-table--statement print-table--journal">
        <thead>
          <tr>
            <th>{{ c.t('acc.account') }}</th>
            <th>{{ c.t('print.rep.journal.memo') }}</th>
            <th class="num">{{ c.t('acc.debit') }}</th>
            <th class="num">{{ c.t('acc.credit') }}</th>
          </tr>
        </thead>
        @for (e of entries(); track e.id) {
          <tbody class="print-entry">
            <tr class="print-entry__head">
              <td colspan="4">
                <span class="mono">{{ c.day(e.entryDate) }}</span>
                <span> · {{ source(e.sourceType) }}</span>
                @if (e.isReversal) {
                  <span> · {{ c.t('acc.entry.reversal') }}</span>
                }
                @if (e.isInterBranch) {
                  <span> · {{ c.t('acc.entry.interBranch') }}</span>
                }
                @if (group()) {
                  <span class="print-soft">{{ branch(e) }}</span>
                }
                @if (e.description) {
                  <span class="print-soft">{{ e.description }}</span>
                }
                @if (e.createdBy) {
                  <span class="print-soft">{{ c.t('treasury.by') }} {{ e.createdBy }}</span>
                }
              </td>
            </tr>
            @for (line of e.lines; track line.id) {
              <tr>
                <td><bdi class="mono" dir="ltr">{{ line.accountCode }}</bdi><span> {{ account(line) }}</span></td>
                <td class="print-soft">{{ line.memo || '' }}</td>
                <td class="num mono">{{ line.debit ? c.money(line.debit) : '' }}</td>
                <td class="num mono">{{ line.credit ? c.money(line.credit) : '' }}</td>
              </tr>
            }
          </tbody>
        } @empty {
          <tbody>
            <tr>
              <td colspan="4">{{ c.t('acc.empty') }}</td>
            </tr>
          </tbody>
        }
        <tfoot>
          <tr class="print-total">
            <td colspan="2">{{ c.t('common.total') }}</td>
            <td class="num mono">{{ c.money(debit()) }}</td>
            <td class="num mono">{{ c.money(credit()) }}</td>
          </tr>
        </tfoot>
      </table>
    }
  `,
})
export class PrintRepJournalComponent {
  readonly ctx = input.required<PrintContext>();
  readonly report = input.required<ReportPaper>();

  /** Oldest first, as a journal is written; entries of one moment keep the order they were made. */
  readonly entries = computed<LedgerEntry[]>(() => {
    const r = this.report();
    if (r.name !== 'journal') return [];
    return [...r.entries].sort(
      (a, b) => Date.parse(a.entryDate) - Date.parse(b.entryDate) || Date.parse(a.createdAtUtc) - Date.parse(b.createdAtUtc),
    );
  });

  readonly capped = computed(() => {
    const r = this.report();
    return r.name === 'journal' && (r.capped || r.entries.length >= JOURNAL_PAPER_TAKE);
  });

  readonly group = computed(() => this.report().branchId === null);

  readonly debit = computed(() => this.entries().reduce((sum, e) => sum + e.lines.reduce((s, l) => s + l.debit, 0), 0));
  readonly credit = computed(() => this.entries().reduce((sum, e) => sum + e.lines.reduce((s, l) => s + l.credit, 0), 0));

  private readonly chart = computed(() => {
    const r = this.report();
    return new Map((r.name === 'journal' ? r.accounts : []).map((a) => [a.id, a]));
  });

  /** A line carries the account's French name only; the chart has all three. */
  account(line: LedgerEntryLine): string {
    const account = this.chart().get(line.accountId);
    return accountWord(this.ctx(), account ?? { nameAr: '', nameFr: line.accountNameFr, nameEn: '', code: line.accountCode });
  }

  source(type: string): string {
    return sourceWord(this.ctx(), type);
  }

  branch(e: LedgerEntry): string {
    const known = this.ctx().branches.some((b) => b.id === e.branchId);
    return known ? scopeText(this.ctx(), e.branchId) : e.branchName || '—';
  }
}
