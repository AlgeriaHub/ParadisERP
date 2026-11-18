import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AdminPaymentMethod, AdminService } from '../../../core/admin.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { PrintService } from '../../print/print.service';
import { ReceivableInvoiceComponent } from './receivable-invoice.component';
import { Invoice, InvoiceLine, ReceivablesApi } from './receivables-api.service';

describe('ReceivableInvoiceComponent', () => {
  let api: jasmine.SpyObj<ReceivablesApi>;
  let admin: jasmine.SpyObj<AdminService>;
  let fixture: ComponentFixture<ReceivableInvoiceComponent>;

  const line = (n: number, over: Partial<InvoiceLine> = {}): InvoiceLine =>
    ({
      id: `l${n}`,
      productId: null,
      sku: `SKU-${n}`,
      name: `Ligne ${n}`,
      qty: 1,
      unitPrice: 100,
      taxRate: 19,
      discount: 0,
      lineNet: 100,
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
        line(1, { name: 'PIED DROIT 4,00 M', qty: 10, unitPrice: 10, days: 30, lineNet: 3000 }),
        line(2, { name: 'POTO 0,40*3,00 M', qty: 1, unitPrice: 750, days: null, lineNet: 750 }),
      ],
      paymentMethodKey: 'cheque',
      ...over,
    }) as Invoice;

  beforeEach(() => {
    api = jasmine.createSpyObj<ReceivablesApi>('ReceivablesApi', ['getInvoice', 'listCreditNotes', 'listReceipts']);
    admin = jasmine.createSpyObj<AdminService>('AdminService', ['listPaymentMethods']);
    api.listCreditNotes.and.resolveTo([]);
    api.listReceipts.and.resolveTo([]);
    admin.listPaymentMethods.and.resolveTo([
      { key: 'cheque', nameFr: 'Chèque', nameAr: 'شيك', nameEn: 'Cheque' } as AdminPaymentMethod,
    ]);

    TestBed.configureTestingModule({
      imports: [ReceivableInvoiceComponent],
      providers: [
        { provide: ReceivablesApi, useValue: api },
        { provide: AdminService, useValue: admin },
        { provide: PrintService, useValue: { open: jasmine.createSpy('open') } },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
  });

  async function render(invoice: Invoice, methods: AdminPaymentMethod[] = []) {
    api.getInvoice.and.resolveTo(invoice);
    fixture = TestBed.createComponent(ReceivableInvoiceComponent);
    fixture.componentRef.setInput('id', invoice.id);
    fixture.componentRef.setInput('customerId', invoice.customerId);
    fixture.componentRef.setInput('methods', methods);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const table = el.querySelector('table[aria-label="' + TestBed.inject(I18nService).t('inv.view.item') + '"]');
    const heads = table ? Array.from(table.querySelectorAll('thead th')).map((th) => th.textContent!.trim()) : [];
    return { el, table, heads, component: fixture.componentInstance };
  }

  it('shows a days column when at least one line has days', async () => {
    const { heads, table, component } = await render(bill());
    const daysLabel = TestBed.inject(I18nService).t('print.inv.days');
    expect(component.hasDays()).toBeTrue();
    expect(heads).toContain(daysLabel);

    const firstRowDays = table?.querySelector('tbody tr:first-child td[data-label="' + daysLabel + '"]');
    expect(firstRowDays?.textContent?.trim()).toBe('30');
    expect(firstRowDays?.classList).toContain('num');
    expect(firstRowDays?.classList).toContain('mono');

    const secondRowDays = table?.querySelector('tbody tr:nth-child(2) td[data-label="' + daysLabel + '"]');
    expect(secondRowDays?.textContent?.trim()).toBe('');
  });

  it('omits the days column when no line has days', async () => {
    const invoiceWithoutDays = bill({
      lines: [
        line(1, { qty: 2, unitPrice: 100, days: null, lineNet: 200 }),
        line(2, { qty: 1, unitPrice: 50, days: 0, lineNet: 50 }),
      ],
    });
    const { heads, component } = await render(invoiceWithoutDays);
    const daysLabel = TestBed.inject(I18nService).t('print.inv.days');
    expect(component.hasDays()).toBeFalse();
    expect(heads).not.toContain(daysLabel);
  });

  it('shows the payment method in the header facts when paymentMethodKey is set', async () => {
    const methods: AdminPaymentMethod[] = [
      { key: 'cheque', nameFr: 'Chèque', nameAr: 'شيك', nameEn: 'Cheque' } as AdminPaymentMethod,
    ];
    const { el, component } = await render(bill(), methods);
    expect(component.paymentMode()).toBe('Chèque');
    expect(el.querySelector('.bo-meta')?.textContent).toContain('Mode de paiement : Chèque');
  });

  it('shows nothing for payment method when paymentMethodKey is not set', async () => {
    const { el, component } = await render(bill({ paymentMethodKey: null }));
    expect(component.paymentMode()).toBe('');
    expect(el.querySelector('.bo-meta')?.textContent).not.toContain('Mode de paiement');
  });

  it('loads payment methods via AdminService when the screen does not have them loaded', async () => {
    const { el, component } = await render(bill(), []);
    expect(admin.listPaymentMethods).toHaveBeenCalled();
    expect(component.paymentMode()).toBe('Chèque');
    expect(el.querySelector('.bo-meta')?.textContent).toContain('Mode de paiement : Chèque');
  });
});
