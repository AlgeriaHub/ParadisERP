import { TestBed } from '@angular/core/testing';
import type { AdminPaymentMethod } from '../../core/admin.service';
import { CreditNote, CustomerStatement, ReceiptVoucher } from '../backoffice/receivables/receivables-api.service';
import { PrintContext } from './print-context';
import { PrintStatementComponent } from './print-statement.component';

/**
 * The paper reads its rows by the screen's rule (statement-rows.ts) in the document's language:
 * the kind named, the ledger's English memo left out, and the detail drawn from the customer's
 * credit notes and receipts and the context's tenders.
 */
describe('PrintStatementComponent', () => {
  function statement(): PrintStatementComponent {
    const fixture = TestBed.createComponent(PrintStatementComponent);
    const ctx = {
      lang: 'ar',
      t: (key: string, params?: Record<string, string | number>) => (params ? `«${key}:${params['n']}»` : `«${key}»`),
      methods: [{ key: 'CHEQUE', nameFr: 'Chèque', nameAr: 'شيك', nameEn: 'Cheque' } as AdminPaymentMethod],
    };
    fixture.componentRef.setInput('ctx', ctx as unknown as PrintContext);
    fixture.componentRef.setInput('statement', { rows: [] } as unknown as CustomerStatement);
    fixture.componentRef.setInput('creditNotes', [{ id: 'crn-1', invoiceNumber: 'FAC-0007' } as CreditNote]);
    fixture.componentRef.setInput('receipts', [{ id: 'rcv-1', methodKey: 'CHEQUE', instrumentNumber: '4471' } as ReceiptVoucher]);
    return fixture.componentInstance;
  }
  const row = (sourceType: string, sourceId: string, description: string) =>
    ({ sourceType, sourceId, description }) as CustomerStatement['rows'][number];

  it('names a correction row in the document language, not by its ledger code', () => {
    expect(statement().sourceWord('correction.rental-issue-daily')).toBe('«inv.stmt.source.daily-correction»');
  });

  it('prints no English memo on the rows it can name', () => {
    const c = statement();
    expect(c.detail(row('invoice', 'x', 'Invoice FAC-0001'))).toBe('');
    expect(c.detail(row('rental-issue', 'x', 'Rental issue ISS-000012'))).toBe('');
    expect(c.detail(row('correction.rental-issue-daily', 'x', 'Daily rental pricing correction — ISS-000012'))).toBe('');
  });

  it('draws a credit note and a receipt from the papers it was handed', () => {
    const c = statement();
    expect(c.detail(row('credit-note', 'crn-1', 'Credit note CRN-1 against FAC-0007'))).toBe('«inv.stmt.againstInvoice:FAC-0007»');
    expect(c.detail(row('customer-receipt', 'rcv-1', 'Receipt RCV-1 — CHEQUE'))).toBe('شيك · 4471');
  });
});
