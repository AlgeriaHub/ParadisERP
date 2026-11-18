import { TestBed } from '@angular/core/testing';
import { AdminBranch, AdminStore } from '../../core/admin.service';
import { I18nService } from '../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../core/i18n/translations-backoffice';
import { DEFAULT_REGIONAL } from '../../core/regional.service';
import { sampleContract } from './contract-preview.component';
import { contractFieldValues } from './contract-fields';
import { buildPrintContext, paperIssuer } from './print-context';
import { PrintRentalContractComponent } from './print-rental-contract.component';

describe('rental contract branch identity', () => {
  const store = {
    nameFr: 'Company', nameAr: 'الشركة', nameEn: 'Company',
    address: 'Company address', phone: '0300', receiptFooter: '', fiscalIds: { nif: 'company-nif', rc: 'company-rc' },
  } as unknown as AdminStore;
  const main = {
    id: 'main', isMain: true, nameFr: 'Main branch', nameAr: 'الفرع الرئيسي',
    address: 'Main address', phone: '0200', fiscalIds: { nif: 'main-nif' },
  } as unknown as AdminBranch;
  const own = {
    id: 'own', nameFr: 'Local branch', nameAr: 'الفرع المحلي', nameEn: 'Local branch',
    addressFr: 'Local address', addressAr: 'عنوان الفرع المحلي', phone: '0100',
    fiscalIds: { rc: 'local-rc', nif: '  ' },
  } as unknown as AdminBranch;
  const warehouse = {
    id: 'warehouse', isMain: true, isWarehouse: true, nameFr: 'Warehouse',
    address: 'Warehouse address', phone: '0900', fiscalIds: { nif: 'warehouse-nif' },
  } as unknown as AdminBranch;
  const data = { store, branches: [warehouse, main, own], lang: 'fr', regional: DEFAULT_REGIONAL };

  it('prefers branch fields and fills each missing identifier from the main trading branch', () => {
    expect(paperIssuer(data, 'own')).toEqual({
      name: 'Local branch', address: 'Local address', phone: '0100',
      fiscalIds: [{ label: 'NIF', value: 'main-nif' }, { label: 'RC', value: 'local-rc' }],
    });
  });

  it('treats whitespace as missing and falls back field by field, then to company settings', () => {
    const sparse = { id: 'sparse', nameFr: ' ', address: ' ', phone: ' ', fiscalIds: {} } as AdminBranch;
    const ctx = { ...data, branches: [warehouse, { ...main, address: '', phone: '' }, sparse] };
    expect(paperIssuer(ctx, 'sparse')).toEqual({
      name: 'Main branch', address: 'Company address', phone: '0300',
      fiscalIds: [{ label: 'NIF', value: 'main-nif' }, { label: 'RC', value: 'company-rc' }],
    });
    expect(paperIssuer(data, 'unknown').name).toBe('Main branch');
    expect(paperIssuer({ ...data, branches: [warehouse] }, 'unknown').name).toBe('Company');
    expect(paperIssuer({ ...data, branches: [], store: null }, 'unknown').name).toBe('');
  });

  for (const lang of ['ar', 'fr', 'en']) {
    for (const wording of ['full', 'footer']) {
      it(`prints the contract branch consistently in the ${lang} ${wording} header and wording`, () => {
        const i18n = TestBed.inject(I18nService);
        i18n.extend(BACKOFFICE_TRANSLATIONS);
        const text = '{company}|{companyAddress}|{companyNif}|{companyRc}';
        const ctx = buildPrintContext(i18n, DEFAULT_REGIONAL, lang, {
          store, branches: data.branches, branchId: 'main', customers: [], methods: [],
          documents: { documentTexts: { rentalContract: { [lang]: text + (wording === 'full' ? '{items}' : '') } } } as never,
        });
        const sample = sampleContract((l, key) => i18n.tIn(l, key), lang, 'rentalContract', 'own', '2026-10-19');
        const fixture = TestBed.createComponent(PrintRentalContractComponent);
        fixture.componentRef.setInput('ctx', ctx);
        fixture.componentRef.setInput('contract', sample.contract);
        fixture.detectChanges();
        const el = fixture.nativeElement as HTMLElement;
        const expectedName = lang === 'ar' ? 'الفرع المحلي' : 'Local branch';
        const expectedAddress = lang === 'ar' ? 'عنوان الفرع المحلي' : 'Local address';
        expect(el.querySelector('.print-company__name')?.textContent).toBe(expectedName);
        expect(el.querySelector('.print-company')?.textContent).toContain(expectedAddress);
        expect(el.querySelector('.print-company')?.textContent).toContain('main-nif');
        expect(el.querySelector('.print-company')?.textContent).toContain('local-rc');
        const fields = contractFieldValues(ctx, sample.contract, null, 0);
        expect(fields.company).toBe(expectedName);
        expect(fields.companyAddress).toBe(expectedAddress);
        expect(fields['companyRc']).toBe('local-rc');
        expect(el.textContent).not.toContain('Company address');
        expect(el.textContent?.split(expectedName).length).toBeGreaterThanOrEqual(3);
        fixture.destroy();
      });
    }
  }
});
