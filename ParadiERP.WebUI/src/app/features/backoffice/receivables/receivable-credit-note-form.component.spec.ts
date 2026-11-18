import { ComponentFixture, TestBed } from '@angular/core/testing';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { ConfirmService } from '../../../ui/confirm.service';
import { ReceivableCreditNoteFormComponent } from './receivable-credit-note-form.component';
import { CreditNote, Invoice, InvoiceLine, ReceivablesApi } from './receivables-api.service';

/** P0.3: goods back into stock on a hand-made credit note, only on lines whose invoice took them out. */
describe('ReceivableCreditNoteFormComponent (goods back in stock)', () => {
  let api: jasmine.SpyObj<ReceivablesApi>;
  let fixture: ComponentFixture<ReceivableCreditNoteFormComponent>;

  const line = (n: number, over: Partial<InvoiceLine> = {}): InvoiceLine =>
    ({
      id: `l${n}`, productId: `p${n}`, sku: `SKU-${n}`, name: `Ligne ${n}`, qty: 10, unitPrice: 3000, taxRate: 19,
      discount: 0, lineNet: 30_000, incomeAccountCode: '700', creditedQty: 0, stockOutQty: 0, stockReturnedQty: 0,
      ...over,
    }) as InvoiceLine;

  const bill = (lines: InvoiceLine[]): Invoice =>
    ({
      id: 'i1', number: 'INV-000001', customerId: 'c1', customerName: 'Ben Ali BTP', branchId: 'b1',
      issuedAtUtc: '2026-11-09T10:00:00Z', dueDateUtc: '2026-12-09T10:00:00Z', taxMode: 'Taxed', origin: 'Direct',
      sourceType: null, sourceId: null, notes: null, subTotalHt: 30_000, taxTotal: 5_700, grandTotalTtc: 35_700,
      prepaid: 0, credited: 0, received: 0, claimed: 0, outstanding: 35_700, status: 'Open', daysOverdue: 0,
      ageingBucket: 'Current', lines,
    }) as Invoice;

  async function render(invoice: Invoice) {
    api = jasmine.createSpyObj<ReceivablesApi>('ReceivablesApi', ['getInvoice', 'createCreditNote']);
    api.getInvoice.and.resolveTo(invoice);
    api.createCreditNote.and.resolveTo({ id: 'n1' } as CreditNote);
    TestBed.configureTestingModule({
      imports: [ReceivableCreditNoteFormComponent],
      providers: [
        { provide: ReceivablesApi, useValue: api },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    fixture = TestBed.createComponent(ReceivableCreditNoteFormComponent);
    fixture.componentRef.setInput('invoiceId', invoice.id);
    fixture.componentRef.setInput('customerId', invoice.customerId);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return { el: fixture.nativeElement as HTMLElement, c: fixture.componentInstance };
  }

  const t = (key: string) => TestBed.inject(I18nService).t(key);
  const heads = (el: HTMLElement) => Array.from(el.querySelectorAll('thead th')).map((th) => th.textContent!.trim());

  it('shows no back-in-stock column when no line took goods out', async () => {
    const { el, c } = await render(bill([line(1)]));
    expect(c.hasStock()).toBeFalse();
    expect(heads(el)).not.toContain(t('inv.credit.backToStock'));
  });

  it('sends nothing about stock for a credit of money only', async () => {
    const { c } = await render(bill([line(1, { stockOutQty: 10 })]));
    c.reason.set('Prix corrigé');
    c.acknowledged.set(true);
    await c.save();
    expect(api.createCreditNote).toHaveBeenCalledWith('i1', jasmine.objectContaining({ reason: 'Prix corrigé' }));
    expect(api.createCreditNote.calls.mostRecent().args[1].lines).toBeUndefined();
  });

  it('offers the column on a line that took goods out and sends the quantity back, line by line', async () => {
    const { el, c } = await render(bill([line(1, { stockOutQty: 10, stockReturnedQty: 2 }), line(2)]));
    expect(c.hasStock()).toBeTrue();
    expect(heads(el)).toContain(t('inv.credit.backToStock'));
    expect(el.querySelector('#cn-bl1')).not.toBeNull();
    expect(el.querySelector('#cn-bl2')).toBeNull();

    c.scope.set('some');
    c.setQty('l1', 4);
    c.setQty('l2', 1);
    c.setBack('l1', 3);
    c.reason.set('Rendu par le client');
    c.acknowledged.set(true);
    await c.save();
    expect(api.createCreditNote).toHaveBeenCalledWith('i1', jasmine.objectContaining({
      lines: [{ invoiceLineId: 'l1', qty: 4, stockReturnedQty: 3 }, { invoiceLineId: 'l2', qty: 1 }],
    }));
  });

  it('under "everything left", still names the lines when goods come back', async () => {
    const { c } = await render(bill([line(1, { stockOutQty: 10, creditedQty: 4 })]));
    c.setBack('l1', 6);
    c.reason.set('Retour complet');
    c.acknowledged.set(true);
    await c.save();
    expect(api.createCreditNote.calls.mostRecent().args[1].lines).toEqual([{ invoiceLineId: 'l1', qty: 6, stockReturnedQty: 6 }]);
  });

  it('refuses more back in stock than the note credits or than is still out, and sends nothing', async () => {
    const { c } = await render(bill([line(1, { stockOutQty: 10, stockReturnedQty: 7 })]));
    c.scope.set('some');
    c.setQty('l1', 5);
    c.setBack('l1', 4);
    c.reason.set('Rendu');
    c.acknowledged.set(true);
    await c.save();
    expect(api.createCreditNote).not.toHaveBeenCalled();
    expect(c.backInvalid(c.rows()[0])).toBeTrue();

    c.setBack('l1', 3);
    await c.save();
    expect(api.createCreditNote).toHaveBeenCalled();
  });
});
