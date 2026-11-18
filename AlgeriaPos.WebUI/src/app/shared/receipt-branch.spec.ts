import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { BranchFace } from '../core/branch-face';
import { Branch, Sale, Store } from '../core/models';
import { PosService } from '../core/pos.service';
import { ReceiptComponent } from './receipt.component';

/**
 * Owner, 2026-10-20: every paper printed at a branch carries that branch's identity, the 80 mm slip
 * too. The till reads it from its synced copy of the branches, so it prints offline.
 */
describe('the till receipt speaks as the till branch', () => {
  const store = {
    id: 's', nameAr: 'الشركة', nameFr: 'Company', nameEn: 'Company', address: 'Company address',
    phone: '0300', fiscalIds: { nif: 'company-nif', nis: 'company-nis' }, receiptFooter: '',
  } as unknown as Store;
  const main = {
    id: 'main', isMain: true, nameAr: 'الفرع الرئيسي', nameFr: 'Main branch', nameEn: 'Main branch',
    address: 'Main address', phone: '0200', fiscalIds: { nif: 'main-nif' },
  } as unknown as Branch;
  const skikda = {
    id: 'skikda', isMain: false, nameAr: 'سكيكدة', nameFr: 'Skikda', nameEn: 'Skikda',
    address: 'Route 44, Skikda', phone: '', fiscalIds: { rc: 'skikda-rc' },
  } as unknown as Branch;
  const sale = {
    id: 'sale', reference: 'C02-000001', localSeq: 1, soldAtUtc: '2026-10-20T08:00:00Z', customerId: null,
    cashierUserId: 'u', cashierName: 'Cashier', subTotalHt: 100, taxTotal: 0, discountTotal: 0,
    grandTotalTtc: 100, paidAmount: 100, changeAmount: 0, paymentMethod: 'Cash', status: 'Completed',
    refundOfSaleId: null, note: null, lines: [], payments: [{ method: 'Cash', amount: 100 }],
    outboxState: 'synced', attempts: 0, lastError: null, syncedAtUtc: '2026-10-20T08:00:01Z',
  } as unknown as Sale;

  function render(till: Branch | null): string {
    TestBed.configureTestingModule({
      providers: [
        { provide: BranchFace, useValue: { till: signal(till), all: signal([main, skikda]) } },
        { provide: PosService, useValue: { giftCards: signal([]), loyalty: signal(null) } },
      ],
    });
    const fixture = TestBed.createComponent(ReceiptComponent);
    fixture.componentRef.setInput('sale', sale);
    fixture.componentRef.setInput('store', store);
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).querySelector('#receipt-print')?.textContent ?? '';
    fixture.destroy();
    return text;
  }

  it("prints the till branch's name, address and identifiers, the main branch's where it has none", () => {
    const text = render(skikda);
    expect(text).toContain('Route 44, Skikda');
    expect(text).toContain('skikda-rc');
    // Skikda has no phone and no NIF: the main branch's, never the company's.
    expect(text).toContain('0200');
    expect(text).toContain('main-nif');
    expect(text).not.toContain('company-nif');
    expect(text).not.toContain('Company address');
    // A key neither branch has still comes from the company settings.
    expect(text).toContain('company-nis');
  });

  it('falls back to the main branch when the till has no branch on record', () => {
    const text = render(null);
    expect(text).toContain('Main address');
    expect(text).toContain('main-nif');
  });
});
