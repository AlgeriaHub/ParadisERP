import { WritableSignal, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { ProductPickerComponent } from '../../../shared/product-picker.component';
import { AdminService } from '../../../core/admin.service';
import { CatalogStore } from '../../../core/catalog-store';
import { AuthService } from '../../../core/auth.service';
import { ConnectivityService } from '../../../core/connectivity.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { SearchableProduct } from '../../../core/product-search';
import { StoreDocumentsApi } from '../../print/store-documents-api.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { RentalContractFormComponent } from './rental-contract-form.component';
import { RentalSettings, RentalsApi } from './rentals-api.service';

describe('compact rental contract builder', () => {
  const products = Array.from({ length: 15 }, (_, i) => ({ id: 'p' + i, sku: 'ITEM-' + i, nameFr: 'Article ' + i, nameAr: 'صنف ' + i, nameEn: 'Item ' + i, isRentable: true })) as SearchableProduct[];
  let create: jasmine.Spy;
  let confirm: jasmine.Spy;
  beforeEach(() => {
    create = jasmine.createSpy('createContract').and.rejectWith(new Error('Server rejected the contract'));
    confirm = jasmine.createSpy('ask').and.resolveTo(true);
    TestBed.configureTestingModule({ providers: [
      { provide: RentalsApi, useValue: { createContract: create, listProductMultipliers: async () => [], listSites: async () => [] } },
      { provide: AdminService, useValue: { listRepresentatives: async () => [] } },
      { provide: StoreDocumentsApi, useValue: { get: async () => ({ contractWordings: [] }) } },
      { provide: CatalogStore, useValue: { active: signal(products), all: signal(products), ensure: async () => products, refresh: async () => products } },
      { provide: AuthService, useValue: { currentUser: signal({ role: 'Admin', branchId: 'b1', branchLocked: false, permissions: [], capabilities: [] }) } },
      { provide: ConnectivityService, useValue: { online: signal(true) } },
      { provide: ConfirmService, useValue: { ask: confirm } },
    ] });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
  });
  async function open() {
    const fixture = TestBed.createComponent(RentalContractFormComponent);
    fixture.componentRef.setInput('settings', { overdueMultiplier: 1.5, depositEnabled: false, taxRate: 0, cutoffHour: null } as RentalSettings);
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture;
  }
  it('keeps 15 lines compact and edits a middle line without rebuilding any others', async () => {
    const fixture = await open(); const form = fixture.componentInstance;
    products.forEach(p => form.take(p)); fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('.rental-row').length).toBe(15);
    expect(fixture.nativeElement.querySelectorAll('.line-fields').length).toBe(0);
    const first = form.lines()[0]; const middle = form.lines()[7];
    form.editLine(middle.key); fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('.line-fields').length).toBe(1);
    const qty = fixture.nativeElement.querySelector('#rf-q' + middle.key) as HTMLInputElement;
    qty.value = '4'; qty.dispatchEvent(new Event('input')); fixture.detectChanges();
    expect(form.lines()[7].qty).toBe(4); expect(form.lines()[0]).toBe(first);
    form.closeEditor(); expect(form.lines().length).toBe(15);
  });
  it('aggregates repeated independent scans while preserving linked items separately', async () => {
    const form = (await open()).componentInstance;
    form.take(products[0]); form.take(products[1]);
    form.setParent(form.lines()[1].key, form.lines()[0].key);
    form.take(products[1]); form.take(products[1]);
    expect(form.lines().length).toBe(3);
    expect(form.lines()[1].qty).toBe(1); expect(form.lines()[2].qty).toBe(2);
    form.openPicker(); form.take(products[2]); expect(form.addingItems()).toBeTrue();
  });
  it('refreshes displayed line and contract totals when quantity, days or the daily rate changes', async () => {
    const fixture = await open(); const form = fixture.componentInstance;
    form.take(products[0]); const key = form.lines()[0].key;
    for (const [qty, days, rate, total] of [[1, 1, 5, 5], [1, 30, 5, 150], [100, 30, 5, 15000], [2, 30, 1000, 60000]]) {
      form.patch(key, { qty, durationDays: days, rentUnitPrice: rate }); fixture.detectChanges();
      expect(form.linePeriod(form.lines()[0])).toBe(total); expect(form.total()).toBe(total);
      expect(fixture.nativeElement.querySelector('.rental-cell-total').textContent).toContain(form.i18n.money(total));
    }
    form.patch(key, { qty: 100, durationDays: 30, rentUnitPrice: 5 });
    form.take(products[1]); form.patch(form.lines()[1].key, { qty: 2, durationDays: 10, rentUnitPrice: 100 });
    expect(form.total()).toBe(17000);
    form.editLine(key); fixture.detectChanges();
    for (const [prefix, value, total] of [['q', '10', 3500], ['d', '20', 3000], ['r', '10', 4000]] as const) {
      const control = fixture.nativeElement.querySelector('#rf-' + prefix + key) as HTMLInputElement;
      control.value = value; control.dispatchEvent(new Event('input')); fixture.detectChanges();
      expect(form.total()).toBe(total);
    }
  });
  it('takes quantity, days and the daily rate in the row; a choice from the list goes to the quantity', async () => {
    const fixture = await open(); const form = fixture.componentInstance; const el = fixture.nativeElement as HTMLElement;
    const picker = fixture.debugElement.query(By.directive(ProductPickerComponent)).componentInstance as ProductPickerComponent;
    // A scan-style pick keeps the box focused for the next code.
    picker.choose(products[1]); fixture.detectChanges(); await new Promise((resolve) => setTimeout(resolve));
    expect((document.activeElement as HTMLElement)?.id).toBe('rf-scan');
    // A choice from the list moves on to that line's quantity, selected for typing over.
    picker.choose(products[0], true); fixture.detectChanges(); await new Promise((resolve) => setTimeout(resolve));
    const key = form.lines().find((l) => l.productId === products[0].id)!.key;
    expect((document.activeElement as HTMLElement)?.id).toBe('rf-rq' + key);
    // No configured price: allowed, and said beside the price.
    expect(el.querySelector('#rf-rr' + key)?.parentElement?.querySelector('.rental-warn')).not.toBeNull();
    for (const [prefix, value, total] of [['rq', '10', 0], ['rr', '5', 1500], ['rd', '20', 1000]] as const) {
      const control = el.querySelector('#rf-' + prefix + key) as HTMLInputElement;
      control.value = value; control.dispatchEvent(new Event('input')); fixture.detectChanges();
      expect(form.linePeriod(form.lines().find((l) => l.key === key)!)).toBe(total);
    }
    expect(el.querySelector('#rf-rr' + key)?.parentElement?.querySelector('.rental-warn')).toBeNull();
    expect(form.editingKey()).toBeNull();
  });
  it("prices a lost piece at the item's sale price by default", async () => {
    const form = (await open()).componentInstance;
    const catalog = TestBed.inject(CatalogStore) as unknown as { active: WritableSignal<SearchableProduct[]> };
    catalog.active.set(products.map((p, i) => (i === 4 ? { ...p, price: 250 } : p)) as SearchableProduct[]);
    expect(form.itemValueOf(products[4].id)).toBe(250);
    expect(form.itemValueOf(products[5].id)).toBeNull();
    form.take(products[4]);
    expect(form.lines().find((l) => l.productId === products[4].id)!.damageUnitPrice).toBe(250);
  });
  it('says beside each item in the search what the branch has of it', async () => {
    const form = (await open()).componentInstance;
    expect(form.itemNote(products[0])).toBeNull();
    form.stockLevels.set(new Map([[products[0].id, 93]]));
    expect(form.itemNote(products[0])).toContain('93');
  });
  it('opens with no line, and saving with none says to add one instead of sending it', async () => {
    const fixture = await open(); const form = fixture.componentInstance;
    expect(form.lines().length).toBe(0);
    expect(fixture.nativeElement.querySelector('.rental-empty')).not.toBeNull();
    form.customerId.set('customer'); form.branchId.set('branch'); form.siteId.set('site');
    await form.save(); fixture.detectChanges();
    expect(create).not.toHaveBeenCalled();
    expect(fixture.nativeElement.querySelector('.rental-items .bo-error')).not.toBeNull();
  });
  it('retains every field and line after authoritative server validation fails', async () => {
    const fixture = await open(); const form = fixture.componentInstance;
    products.slice(0, 5).forEach(p => form.take(p));
    form.customerId.set('customer'); form.branchId.set('branch'); form.siteId.set('site'); form.notes.set('Keep this note');
    const lines = form.lines(); const signed = form.signedOn();
    await form.save(); fixture.detectChanges(); await fixture.whenStable();
    expect(create).toHaveBeenCalledTimes(1); expect(form.lines()).toBe(lines);
    expect(form.customerId()).toBe('customer'); expect(form.siteId()).toBe('site');
    expect(form.notes()).toBe('Keep this note'); expect(form.signedOn()).toBe(signed); expect(form.error()).toBeTruthy();
  });
  it('opens a line from its validation summary and confirms a meaningful removal', async () => {
    const fixture = await open(); const form = fixture.componentInstance;
    form.take(products[0]); form.take(products[1]); const key = form.lines()[1].key;
    form.patch(key, { qty: 0 }); await form.save(); fixture.detectChanges(); await fixture.whenStable();
    const error = fixture.nativeElement.querySelector('#rf-line-errors button') as HTMLButtonElement;
    error.click(); fixture.detectChanges(); await fixture.whenStable();
    expect(form.editingKey()).toBe(key);
    expect((document.activeElement as HTMLElement)?.id).toBe('rf-q' + key);
    confirm.and.resolveTo(false);
    await form.removeLine(key); expect(form.lines().length).toBe(2);
    confirm.and.resolveTo(true); await form.removeLine(key); expect(form.lines().length).toBe(1); expect(form.editingKey()).toBeNull();
  });
});
