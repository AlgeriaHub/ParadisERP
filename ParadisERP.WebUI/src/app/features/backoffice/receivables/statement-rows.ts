import type { AdminPaymentMethod } from '../../../core/admin.service';
import type { Lang } from '../../../core/models';
import { localizedName } from '../../../core/multilingual';
import type { CreditNote, CustomerStatementRow, ReceiptVoucher } from './receivables-api.service';

/**
 * How a customer statement names its rows — one copy for the screen and the printed sheet.
 *
 * <p>Each used to keep its own, and they drifted: the paper printed the ledger's English memo on
 * every row ("Invoice FAC-…", "Rental issue ISS-…") beside Arabic or French column heads, where
 * the screen showed it only for a kind it could not name. Nothing here is heavier than a type
 * import, so the print chunk reads it without pulling in the receivables screen.</p>
 */

/** The word for each ledger source that can label a 411 line. */
export const STATEMENT_SOURCE_KEY: Readonly<Record<string, string>> = {
  sale: 'inv.stmt.source.sale',
  'payment-settlement': 'inv.stmt.source.payment-settlement',
  invoice: 'inv.stmt.source.invoice',
  'customer-receipt': 'inv.stmt.source.customer-receipt',
  // SAL-13: part of an advance kept on a cancelled order — a debit, its number the receipt's.
  'customer-advance-kept': 'inv.stmt.source.customer-advance-kept',
  'credit-note': 'inv.stmt.source.credit-note',
  'payment-voucher': 'inv.stmt.source.payment-voucher',
  'rental-issue': 'rent.issueNote',
  'rental-return': 'rent.returnNote',
  // A customer's balance on the day the company started on the system, and its corrections (2026-10-10).
  'customer-opening': 'inv.stmt.source.customer-opening',
  'customer-opening-correction': 'inv.stmt.source.customer-opening-correction',
  // The daily-pricing restatement's adjustments (2026-10-06). One word for all three: the number
  // column already names the note or credit note each one corrects.
  'correction.rental-issue-daily': 'inv.stmt.source.daily-correction',
  'correction.rental-return-daily': 'inv.stmt.source.daily-correction',
  'correction.rental-credit-daily': 'inv.stmt.source.daily-correction',
};

/** The papers a row's detail is read from: the customer's credit notes and receipts, and the tenders. */
export interface StatementPapers {
  readonly creditNotes: readonly CreditNote[];
  readonly receipts: readonly ReceiptVoucher[];
  readonly methods: readonly AdminPaymentMethod[];
}

type Translate = (key: string, params?: Record<string, string | number>) => string;

/** The word for a source, or the raw key for one nobody has named yet. */
export function statementSourceLabel(type: string, t: Translate): string {
  return Object.hasOwn(STATEMENT_SOURCE_KEY, type) ? t(STATEMENT_SOURCE_KEY[type]) : type;
}

/**
 * The row's story in the reader's language.
 *
 * <p>The ledger memo behind a row is written once, in English, when the document posts — right
 * for the books, wrong beside Arabic column heads. The kind and number columns already say what
 * the document is, so this adds only what they cannot: which invoice a credit note corrects,
 * what tender a receipt came in under. The memo is shown only for a row of a kind nobody has
 * named.</p>
 */
export function describeStatementRow(
  row: Pick<CustomerStatementRow, 'sourceType' | 'sourceId' | 'description'>,
  papers: StatementPapers,
  lang: Lang,
  t: Translate,
): string {
  switch (row.sourceType) {
    case 'credit-note': {
      const note = papers.creditNotes.find((n) => n.id === row.sourceId);
      return note?.invoiceNumber ? t('inv.stmt.againstInvoice', { n: note.invoiceNumber }) : '';
    }
    case 'customer-receipt': {
      const voucher = papers.receipts.find((v) => v.id === row.sourceId);
      if (!voucher) return '';
      const method = papers.methods.find((m) => m.key === voucher.methodKey);
      const tender = method ? localizedName(method, lang) : voucher.methodKey;
      return voucher.instrumentNumber ? `${tender} · ${voucher.instrumentNumber}` : tender;
    }
    case 'customer-advance-kept': {
      // The reason written when it was kept; the ledger's description is "Advance kept — <reason>".
      for (const voucher of papers.receipts) {
        const kept = voucher.advanceApplications?.find((a) => a.id === row.sourceId);
        if (kept?.reason) return kept.reason;
      }
      return (row.description ?? '').replace(/^Advance kept — /, '');
    }
    default:
      return Object.hasOwn(STATEMENT_SOURCE_KEY, row.sourceType) ? '' : (row.description ?? '');
  }
}
