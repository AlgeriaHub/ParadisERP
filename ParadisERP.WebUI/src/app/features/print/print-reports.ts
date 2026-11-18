import { AdminProduct, SalesSummary } from '../../core/admin.service';
import {
  AccountStatement,
  BalanceSheet,
  LedgerAccount,
  LedgerAccountType,
  LedgerEntry,
  ProfitAndLoss,
  TrialBalance,
} from '../../core/ledger.service';
import { ItemStatusDetail, ItemStatusReport } from '../backoffice/item-status/item-status-api.service';
import { LabelledLine, PrintContext, isolate, utc } from './print-context';

/**
 * The back office's reports on paper (`/print/report/:name`).
 *
 * <p>Until 2026-10 every report's Print button called `window.print()` on the working screen, so
 * the sheet carried the sidebar, the top bar and the screen's own cards, and never said which
 * company, which period or which branch it was run for. A trial balance that does not say what it
 * is cannot be filed. Each report is now drawn on the same sheet as an invoice — the company's
 * identity at the head, the title, the period and the scope beside it, the figures, the totals,
 * and two signature lines — in the language chosen at print time.</p>
 *
 * <p>The figures are read again by the print tab, from the same endpoints and with the same
 * filters the screen sent: the paper is what the server says for that window, not a copy of
 * whatever the screen happened to hold.</p>
 */
export type ReportName = 'trial-balance' | 'profit-loss' | 'balance-sheet' | 'account-statement' | 'journal' | 'sales' | 'item-status';

/** The title key of each report on paper: the formal name, not the screen's short chip label. */
export const REPORT_TITLES: Record<ReportName, string> = {
  'trial-balance': 'print.rep.trial.title',
  'profit-loss': 'print.rep.pl.title',
  'balance-sheet': 'print.rep.bs.title',
  'account-statement': 'print.rep.st.title',
  journal: 'print.rep.journal.title',
  sales: 'print.rep.sales.title',
  'item-status': 'print.rep.itemStatus.title',
};

export function isReportName(value: string): value is ReportName {
  return value in REPORT_TITLES;
}

/**
 * The most rows the paper asks for. The screen stops at 500 movements and 300 entries because a
 * screen scrolls; paper asks for the server's own ceiling, and says so in words when it is reached.
 */
export const STATEMENT_PAPER_TAKE = 2000;
export const JOURNAL_PAPER_TAKE = 1000;

interface ReportBase {
  /** The branch the report was run for; null is the whole group. The head prints its identifiers. */
  branchId: string | null;
  /** When the tab read the figures; a filed report says when it was true. */
  printedAt: string;
}

export type ReportPaper = ReportBase &
  (
    | { name: 'trial-balance'; trial: TrialBalance }
    | { name: 'profit-loss'; pl: ProfitAndLoss }
    | { name: 'balance-sheet'; sheet: BalanceSheet }
    | { name: 'account-statement'; statement: AccountStatement; capped: boolean }
    | {
        name: 'journal';
        from: string;
        to: string;
        source: string | null;
        entries: LedgerEntry[];
        capped: boolean;
        /** The chart, so a line is named in the document language: the entry carries its French name only. */
        accounts: LedgerAccount[];
      }
    | {
        name: 'sales';
        summary: SalesSummary;
        /** The catalogue, so a product is named in the document language: the summary names it once. */
        products: AdminProduct[];
      }
    | {
        name: 'item-status';
        report: ItemStatusReport;
        detail: ItemStatusDetail | null;
      }
  );

/**
 * The last day a window covers. The ledger's `to` is exclusive on the wire, and a report reading
 * "to the 9th" that stops at the 8th would be wrong on its face. Midday, so no time zone moves it.
 */
export function lastCoveredDay(toIso: string): string {
  const end = new Date(utc(toIso));
  end.setUTCDate(end.getUTCDate() - 1);
  end.setUTCHours(12, 0, 0, 0);
  return end.toISOString();
}

/** The window as the report states it: first and last day covered, or the one day a balance sheet is at. */
export function reportWindow(r: ReportPaper): { from: string | null; to: string } {
  switch (r.name) {
    case 'trial-balance':
      return { from: r.trial.from, to: lastCoveredDay(r.trial.to) };
    case 'profit-loss':
      return { from: r.pl.from, to: lastCoveredDay(r.pl.to) };
    case 'balance-sheet':
      return { from: null, to: lastCoveredDay(r.sheet.asOf) };
    case 'account-statement':
      return { from: r.statement.from, to: lastCoveredDay(r.statement.to) };
    case 'journal':
      return { from: r.from, to: lastCoveredDay(r.to) };
    case 'sales':
      // The sales window is two instants, the second the last millisecond of the last day.
      return { from: r.summary.fromUtc, to: r.summary.toUtc };
    case 'item-status':
      return { from: null, to: r.report.asOfUtc };
  }
}

/** The period in words: "Du 01/10/2026 au 08/10/2026", or "Au 08/10/2026" for a balance sheet. */
export function periodText(c: PrintContext, r: ReportPaper): string {
  const w = reportWindow(r);
  return w.from ? c.t('print.rep.period', { from: c.day(w.from), to: c.day(w.to) }) : c.t('print.rep.asOf', { d: c.day(w.to) });
}

/** The branch the figures belong to, or the group. */
export function scopeText(c: PrintContext, branchId: string | null): string {
  if (!branchId) return c.t('acc.group');
  const branch = c.branches.find((b) => b.id === branchId);
  return branch ? c.name(branch) : '—';
}

/** Lines under the title: the scope, then the report's own filters, then when it was printed. */
export function reportMeta(c: PrintContext, r: ReportPaper): LabelledLine[] {
  const lines: LabelledLine[] = [{ label: c.t('acc.scope'), value: scopeText(c, r.branchId) }];
  if (r.name === 'journal' && r.source) lines.push({ label: c.t('acc.entry.document'), value: sourceWord(c, r.source) });
  lines.push({ label: c.t('print.rep.printedAt'), value: c.dateTime(r.printedAt) });
  return lines;
}

/** A file name and a foot strip: the title and the window in ISO days, which sort and never mirror. */
export function reportLabel(c: PrintContext, r: ReportPaper): string {
  const w = reportWindow(r);
  const day = (iso: string) => utc(iso).slice(0, 10);
  const span = w.from ? `${day(w.from)}…${day(w.to)}` : day(w.to);
  return `${c.t(REPORT_TITLES[r.name])} ${span}`;
}

/** An account in the document language, its code beside it on the sheet. */
export function accountWord(c: PrintContext, x: { nameAr: string; nameFr: string; nameEn: string; code?: string }): string {
  return c.name(x) || x.code || '';
}

/** What an account is, in the word the owner uses (the screen's own `acc.nature.*`). */
export function natureWord(c: PrintContext, type: LedgerAccountType | string): string {
  return c.t(`acc.nature.${type.toLowerCase()}`);
}

/**
 * A document type in words. `sourceType` is open-ended — modules are still being added — so an
 * unknown one prints as itself rather than as a translation key.
 */
export function sourceWord(c: PrintContext, type: string): string {
  const key = `acc.src.${type}`;
  const word = c.t(key);
  return word === key ? type : word;
}

/**
 * Which side a trial-balance figure sits on. The report signs each balance in its account's own
 * direction, so a negative figure is always the other side (the screen's `balanceSide`).
 */
export function sideWord(c: PrintContext, row: { type: LedgerAccountType; balance: number }): string {
  const naturallyDebit = row.type === 'Asset' || row.type === 'Expense';
  return c.t(naturallyDebit === row.balance >= 0 ? 'acc.debit' : 'acc.credit');
}

/**
 * A row's share of the whole, as a figure. The screen draws a bar against the largest row; paper
 * is printed in black and read with a pen, so it states the part of the total each row carried.
 * Isolated, or "45 %" prints as "% 45" on an Arabic sheet.
 */
export function shareOf(value: number, total: number): string {
  if (!(total > 0) || !(value > 0)) return '';
  return isolate(`${Math.round((value / total) * 100)} %`);
}
