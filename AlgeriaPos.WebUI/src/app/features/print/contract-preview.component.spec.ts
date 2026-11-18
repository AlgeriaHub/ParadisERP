import { TestBed } from '@angular/core/testing';
import { AdminBranch, AdminService, AdminStore } from '../../core/admin.service';
import { I18nService } from '../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../core/i18n/translations-backoffice';
import { SecureImageService } from '../../core/secure-image.service';
import { ContractPreviewComponent, sampleContract } from './contract-preview.component';
import { hasNonLatinDigits } from './print-context';
import { DocumentTextKey, StoreDocumentsDto } from './store-documents-api.service';

/**
 * The contract preview in Company settings (owner, 2026-10-08): the real rental-contract paper over
 * the company's own head, with a sample contract and whatever wording the editor holds — no
 * contract read, nothing saved.
 */
describe('ContractPreviewComponent', () => {
  const store = {
    id: 's',
    nameFr: 'Béton SARL',
    nameAr: 'شركة الخرسانة',
    nameEn: 'Beton LLC',
    address: 'Rouiba',
    phone: '',
    fiscalIds: {},
    receiptFooter: '',
  } as unknown as AdminStore;
  const branches = [
    { id: 'w1', nameFr: 'Dépôt', nameAr: 'المستودع', nameEn: 'Depot', isMain: true, isWarehouse: true },
    { id: 'b1', nameFr: 'Agence Rouiba', nameAr: 'فرع الرويبة', nameEn: 'Rouiba branch', isMain: true, isWarehouse: false },
  ] as AdminBranch[];

  let getStore: jasmine.Spy;

  beforeEach(() => {
    getStore = jasmine.createSpy('getStore').and.resolveTo(store);
    TestBed.configureTestingModule({
      providers: [
        { provide: AdminService, useValue: { getStore, listBranches: () => Promise.resolve(branches) } },
        { provide: SecureImageService, useValue: { dataUrl: () => Promise.resolve(null) } },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
  });

  const documents = (texts: StoreDocumentsDto['documentTexts']): StoreDocumentsDto => ({
    documentTexts: texts,
    email: null,
    website: null,
    bankDetails: null,
    changesPerYear: 3,
  });

  async function render(texts: StoreDocumentsDto['documentTexts'], lang: string, key: DocumentTextKey = 'rentalContract') {
    const fixture = TestBed.createComponent(ContractPreviewComponent);
    fixture.componentRef.setInput('documents', documents(texts));
    fixture.componentRef.setInput('wordingKey', key);
    fixture.componentRef.setInput('lang', lang);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  const text = (el: HTMLElement, selector: string) => el.querySelector(selector)?.textContent?.trim() ?? '';

  it('prints the draft wording on the real paper, its fields filled from the sample contract', async () => {
    const { el } = await render({ rentalContract: { fr: 'Entre {company} et {customer}, contrat {contractNumber}. {inconnu}' } }, 'fr');

    // The company's own head, one sheet, the sample's three lines.
    expect(text(el, '.print-company__name')).toBe('Agence Rouiba');
    expect(el.querySelectorAll('.print-sheet').length).toBe(1);
    expect(el.querySelectorAll('.print-table tbody tr').length).toBeGreaterThanOrEqual(3);
    // The place is the main branch that trades, not the warehouse.
    expect(el.querySelector('.print-parties')?.textContent).toContain('Agence Rouiba');

    const foot = text(el, '.print-foot');
    expect(foot).toContain('Agence Rouiba');
    expect(foot).toContain('Client exemple');
    expect(foot).toContain('CTR-000000');
    // A field nobody defined prints as typed.
    expect(foot).toContain('{inconnu}');
    expect(foot).not.toContain('{customer}');
  });

  it('mirrors an Arabic preview and prints the Arabic draft, its figures in Latin digits', async () => {
    const { el } = await render({ rentalContract: { fr: 'Texte français {customer}', ar: 'النص العربي {customer} {deposit}' } }, 'ar');

    const page = el.querySelector('.print-page')!;
    expect(page.getAttribute('dir')).toBe('rtl');
    expect(page.getAttribute('lang')).toBe('ar');
    const foot = text(el, '.print-foot');
    expect(foot).toContain('النص العربي');
    expect(foot).toContain('زبون للتجربة');
    expect(foot).not.toContain('Texte français');
    expect(hasNonLatinDigits(page.textContent ?? '')).toBeFalse();
  });

  it('previews a named wording with its own text, never the standard one', async () => {
    const key = 'rentalContract.scaf0001' as const;
    const { el } = await render({ rentalContract: { fr: 'STANDARD' }, [key]: { fr: 'Rédaction nommée, chantier {site}' } }, 'fr', key);

    const foot = text(el, '.print-foot');
    expect(foot).toContain('Rédaction nommée');
    expect(foot).toContain('Chantier exemple');
    expect(foot).not.toContain('STANDARD');
  });

  it('redraws in another language without reading anything again', async () => {
    const { fixture, el } = await render({ rentalContract: { fr: 'FR {customer}', en: 'EN {customer}' } }, 'fr');
    fixture.componentRef.setInput('lang', 'en');
    fixture.detectChanges();

    expect(text(el, '.print-foot')).toContain('EN');
    expect(text(el, '.print-foot')).toContain('Sample customer');
    expect(getStore).toHaveBeenCalledTimes(1);
  });

  it('builds a sample with every block of the paper to show, and says it is one', () => {
    const t = (lang: string, key: string) => TestBed.inject(I18nService).tIn(lang, key);
    const { contract, customer, products } = sampleContract(t, 'fr', 'rentalContract', 'b1', '2026-10-08T09:00:00Z');
    expect(contract.wordingKey).toBeNull();
    expect(contract.depositAmount).toBeGreaterThan(0);
    expect(contract.lines.some((l) => l.parentLineId)).toBeTrue();
    expect(customer.fullName).toBe('Client exemple');
    // Item names in every language, as a real line carries them.
    expect(products.every((p) => p.nameFr && p.nameAr && p.nameEn)).toBeTrue();
    expect(sampleContract(t, 'fr', 'rentalContract.scaf0001', 'b1', '').contract.wordingKey).toBe('rentalContract.scaf0001');
  });
});
