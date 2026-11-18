import { TestBed } from '@angular/core/testing';
import { SearchableProduct } from '../core/product-search';
import { ProductPickerComponent } from './product-picker.component';

describe('ProductPickerComponent', () => {
  const products: SearchableProduct[] = [
    { id: 'p1', sku: 'RENT-001', barcode: '6130000000001', nameAr: 'مولد 1', nameFr: 'Générateur 1', nameEn: 'Generator 1' },
    { id: 'p2', sku: 'RENT-002', barcode: '6130000000002', nameAr: 'مولد 2', nameFr: 'Générateur 2', nameEn: 'Generator 2' },
  ];

  it('empties the entry box after a scan even when no render ran between the code and Enter', async () => {
    const fixture = TestBed.createComponent(ProductPickerComponent);
    fixture.componentRef.setInput('products', products);
    fixture.componentRef.setInput('clearOnPick', true);
    const picked: string[] = [];
    fixture.componentInstance.picked.subscribe((p) => picked.push(p.id));
    fixture.detectChanges();
    await fixture.whenStable();

    const box = fixture.nativeElement.querySelector('input') as HTMLInputElement;
    // What a scanner does: the whole code, then Enter, inside one animation frame — no change
    // detection in between, which is what event coalescing gives a real page.
    box.value = '6130000000001';
    box.dispatchEvent(new Event('input'));
    box.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    fixture.detectChanges();
    await fixture.whenStable();

    expect(picked).toEqual(['p1']);
    expect(box.value).toBe('');

    // So the next code is read on its own, not appended to the first.
    box.value = '6130000000002';
    box.dispatchEvent(new Event('input'));
    box.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(picked).toEqual(['p1', 'p2']);
    expect(box.value).toBe('');
  });
});
