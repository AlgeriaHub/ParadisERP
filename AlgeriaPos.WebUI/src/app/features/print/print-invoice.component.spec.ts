import { TestBed } from '@angular/core/testing';
import { AdminCustomer, AdminPaymentMethod, AdminStore } from '../../core/admin.service';
import { I18nService } from '../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../core/i18n/translations-backoffice';
import { DEFAULT_REGIONAL } from '../../core/regional.service';
import { Invoice, InvoiceLine } from '../backoffice/receivables/receivables-api.service';
import { buildPrintContext } from './print-context';
import { PrintInvoiceComponent } from './print-invoice.component';

/**
 * The invoice laid out as the company's Facture N° 45 (owner, 2026-10-10): quantity, price and days
 * in their own columns, the buyer's identifiers under the name, the total, the tax and the total
 * with tax, the way of paying, and nothing about settlement until there is some.
 */
describe('PrintInvoiceComponent', () => {
  const nif = DEFAULT_REGIONAL.fiscalIds.find((d) => d.key === 'nif')!;
  const rc = DEFAULT_REGIONAL.fiscalIds.find((d) => d.key === 'rc')!;

  const line = (n: number, over: Partial<InvoiceLine>): InvoiceLine =>
    ({
      id: `l${n}`,
      productId: null,
      sku: '',
      name: `Ligne ${n}`,
      qty: 1,
      unitPrice: 0,
      taxRate: 19,
      discount: 0,
      lineNet: 0,
      incomeAccountCode: '700',
      creditedQty: 0,
      ...over,
    }) as InvoiceLine;

  const bill = (over: Partial<Invoice> = {}): Invoice =>
    ({
      id: 'i1',
      number: 'INV-000045',
      customerId: 'c1',
      customerName: 'SARL BMLT CONSTRUCTION',
      branchId: 'b1',
      issuedAtUtc: '2026-09-30T10:00:00Z',
      dueDateUtc: '2026-09-30T10:00:00Z',
      taxMode: 'Taxed',
      origin: 'Direct',
      sourceType: null,
      sourceId: null,
      notes: null,
      subTotalHt: 386_300,
      taxTotal: 73_397,
      grandTotalTtc: 459_697,
      prepaid: 0,
      credited: 0,
      received: 0,
      claimed: 0,
      outstanding: 459_697,
      status: 'Open',
      daysOverdue: 0,
      ageingBucket: 'Current',
      lines: [
        line(1, { name: 'PIED DROIT 4,00 M', qty: 955, unitPrice: 10, days: 30, lineNet: 286_500 }),
        line(2, { name: 'POTO 0,40*3,00 M', qty: 12, unitPrice: 750, days: 1, lineNet: 9_000 }),
        line(3, { name: 'POTO 0,30*3,00 M', qty: 10, unitPrice: 908, days: 10, lineNet: 90_800 }),
      ],
      buyerName: 'SARL BMLT CONSTRUCTION',
      buyerAddress: 'Rue Ibn Khaldoun RDC N°15 Boufarik',
      buyerTaxId: '002609081352527',
      buyerFiscalIds: { rc: '26B0813525 00/09', ai: '21000478402' },
      paymentMethodKey: 'cheque',
      ...over,
    }) as Invoice;

  function render(invoice: Invoice): { text: string; heads: string[]; component: PrintInvoiceComponent } {
    const i18n = TestBed.inject(I18nService);
    i18n.extend(BACKOFFICE_TRANSLATIONS);
    const ctx = buildPrintContext(i18n, DEFAULT_REGIONAL, 'fr', {
      store: { id: 's', nameFr: 'SARL TOP SEVENTY FIVE', nameAr: '', nameEn: '', fiscalIds: {}, receiptFooter: '' } as unknown as AdminStore,
      documents: null,
      branches: [],
      customers: [{ id: 'c1', code: 'Comp-112609001', fullName: 'SARL BMLT CONSTRUCTION' } as AdminCustomer],
      methods: [{ key: 'cheque', nameFr: 'Chèque', nameAr: 'شيك', nameEn: 'Cheque' } as AdminPaymentMethod],
      branchId: null,
    });
    const fixture = TestBed.createComponent(PrintInvoiceComponent);
    fixture.componentRef.setInput('ctx', ctx);
    fixture.componentRef.setInput('invoice', invoice);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const heads = Array.from(el.querySelectorAll('.print-table:not(.print-table--compact) thead th')).map((th) => th.textContent!.trim());
    return { text: el.textContent ?? '', heads, component: fixture.componentInstance };
  }

  it('prints quantity, price and days in their own columns, and no rate column for one rate', () => {
    const { heads, text } = render(bill());
    expect(heads).toEqual(['N°', 'Désignation', 'Qté', 'Prix', 'Jours', 'Total HT']);
    expect(text).toContain('PIED DROIT 4,00 M');
  });

  it('ends the paper with its end-of-document line, after everything else (owner, 2026-11-09)', () => {
    const { text } = render(bill());
    expect(text.trimEnd().endsWith('— Fin du document —')).toBeTrue();
  });

  it('prints the four columns a sale always had when no line is priced by the day', () => {
    const sale = bill({ lines: [line(1, { qty: 2, unitPrice: 100, lineNet: 200 })] });
    expect(render(sale).heads).toEqual(['N°', 'Désignation', 'Qté', 'Prix', 'Total HT']);
  });

  it("states the buyer's identifiers under the name, each under its code", () => {
    const { component } = render(bill());
    const lines = component.buyer().map((l) => `${l.label}|${l.value}`);
    expect(lines).toContain(`${nif.code}|002609081352527`);
    expect(lines).toContain(`${rc.code}|26B0813525 00/09`);
  });

  it('says the way of paying in the document language', () => {
    expect(render(bill()).component.paymentMode()).toBe('Chèque');
    expect(render(bill({ paymentMethodKey: null })).component.paymentMode()).toBe('');
  });

  it('prints no settlement and no due date on a fresh invoice payable on the day', () => {
    const { component, text } = render(bill());
    expect(component.settled()).toBeFalse();
    expect(text).not.toContain('Reste à payer');
    expect(component.meta().length).toBe(1);
  });

  it('prints the balance due once something was received', () => {
    const paid = render(bill({ received: 100_000, outstanding: 359_697, dueDateUtc: '2026-10-30T10:00:00Z' }));
    expect(paid.component.settled()).toBeTrue();
    expect(paid.text).toContain('Reste à payer');
    expect(paid.component.meta().length).toBe(2);
  });
});
