import type { AdminPaymentMethod } from '../../../core/admin.service';
import type { CreditNote, ReceiptVoucher } from './receivables-api.service';
import { StatementPapers, describeStatementRow, statementSourceLabel } from './statement-rows';

/**
 * The statement's rows in the reader's language, on screen and on paper alike: a kind is named,
 * never coded; the ledger's English memo appears only for a kind nobody has named; and what the
 * kind and number cannot say — which invoice, which tender — comes from the customer's papers.
 */
describe('statement rows', () => {
  const t = (key: string, params?: Record<string, string | number>) => (params ? `${key}:${JSON.stringify(params)}` : key);
  const papers: StatementPapers = {
    creditNotes: [{ id: 'crn-1', invoiceNumber: 'FAC-0007' } as CreditNote],
    receipts: [
      { id: 'rcv-1', methodKey: 'CHEQUE', instrumentNumber: '4471' } as ReceiptVoucher,
      { id: 'rcv-2', methodKey: 'CASH', instrumentNumber: null } as unknown as ReceiptVoucher,
    ],
    methods: [
      { key: 'CHEQUE', nameFr: 'Chèque', nameAr: 'شيك', nameEn: 'Cheque' } as AdminPaymentMethod,
      { key: 'CASH', nameFr: 'Espèces', nameAr: 'نقدًا', nameEn: 'Cash' } as AdminPaymentMethod,
    ],
  };
  const row = (sourceType: string, sourceId = 'x', description: string | null = 'English memo') =>
    ({ sourceType, sourceId, description }) as Parameters<typeof describeStatementRow>[0];

  it('names every kind it knows, the three corrections with one word, and leaves an unknown kind raw', () => {
    expect(statementSourceLabel('invoice', t)).toBe('inv.stmt.source.invoice');
    expect(statementSourceLabel('rental-issue', t)).toBe('rent.issueNote');
    for (const type of ['correction.rental-issue-daily', 'correction.rental-return-daily', 'correction.rental-credit-daily']) {
      expect(statementSourceLabel(type, t)).toBe('inv.stmt.source.daily-correction');
    }
    expect(statementSourceLabel('mystery', t)).toBe('mystery');
    expect(statementSourceLabel('constructor', t)).toBe('constructor');
  });

  it('prints no English memo on a row whose kind it names', () => {
    for (const type of ['sale', 'payment-settlement', 'invoice', 'rental-issue', 'rental-return', 'payment-voucher', 'correction.rental-return-daily']) {
      expect(describeStatementRow(row(type, 'x', 'Invoice FAC-0001'), papers, 'ar', t)).toBe('');
    }
  });

  it('keeps the memo for a kind nobody has named, rather than saying nothing', () => {
    expect(describeStatementRow(row('mystery', 'x', 'Something the books said'), papers, 'fr', t)).toBe('Something the books said');
    expect(describeStatementRow(row('mystery', 'x', null), papers, 'fr', t)).toBe('');
  });

  it('says which invoice a credit note corrects, and nothing when the note is not at hand', () => {
    expect(describeStatementRow(row('credit-note', 'crn-1'), papers, 'fr', t)).toBe('inv.stmt.againstInvoice:{"n":"FAC-0007"}');
    expect(describeStatementRow(row('credit-note', 'gone'), papers, 'fr', t)).toBe('');
  });

  it("says how a receipt was paid, in the reader's language, with the cheque's number", () => {
    expect(describeStatementRow(row('customer-receipt', 'rcv-1'), papers, 'ar', t)).toBe('شيك · 4471');
    expect(describeStatementRow(row('customer-receipt', 'rcv-2'), papers, 'fr', t)).toBe('Espèces');
    expect(describeStatementRow(row('customer-receipt', 'gone'), papers, 'fr', t)).toBe('');
  });

  it('names a kept advance and gives its reason, from the receipt or from the ledger line (SAL-13)', () => {
    expect(statementSourceLabel('customer-advance-kept', t)).toBe('inv.stmt.source.customer-advance-kept');
    const withKept: StatementPapers = {
      ...papers,
      receipts: [
        ...papers.receipts,
        { id: 'rcv-3', advanceApplications: [{ id: 'keep-1', kind: 'Kept', reason: 'Commande annulée' }] } as unknown as ReceiptVoucher,
      ],
    };
    expect(describeStatementRow(row('customer-advance-kept', 'keep-1'), withKept, 'ar', t)).toBe('Commande annulée');
    expect(describeStatementRow(row('customer-advance-kept', 'other', 'Advance kept — Client parti'), papers, 'fr', t)).toBe('Client parti');
  });
});
