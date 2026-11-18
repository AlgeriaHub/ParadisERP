import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AdminService } from '../../core/admin.service';
import { AuthService } from '../../core/auth.service';
import { I18nService } from '../../core/i18n.service';
import { PosService } from '../../core/pos.service';
import { RegionalService } from '../../core/regional.service';
import { SecureImageService } from '../../core/secure-image.service';
import { ConfirmService } from '../../ui/confirm.service';
import { CONTRACT_FIELDS } from '../print/contract-fields';
import { StoreDocumentsApi, StoreDocumentsDto } from '../print/store-documents-api.service';
import { BackofficeStoreComponent } from './store-tab.component';

/**
 * A rental-contract wording as a template (owner, 2026-10-08: "the admin can create the rental
 * contract template and preview it"): fields inserted into the text where the cursor is, and a
 * preview of the printed contract that reads the editor's draft and saves nothing.
 */
describe('a contract wording as a template', () => {
  const SCAF = 'rentalContract.scaf0001' as const;

  const stored = (): StoreDocumentsDto => ({
    documentTexts: {
      rentalContract: { fr: 'Conditions générales' },
      [SCAF]: { fr: 'Conditions échafaudage' },
    },
    email: null,
    website: null,
    bankDetails: null,
    changesPerYear: 3,
    contractWordings: [{ key: SCAF, names: { fr: 'Échafaudage' } }],
  });

  let put: jasmine.Spy;

  beforeEach(() => {
    put = jasmine.createSpy('put').and.callFake((dto: StoreDocumentsDto) => Promise.resolve({ ...dto, allowances: [] }));
    TestBed.configureTestingModule({
      providers: [
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
        { provide: AdminService, useValue: { getStore: () => Promise.reject(new Error('not needed')) } },
        { provide: PosService, useValue: {} },
        { provide: RegionalService, useValue: { settings: signal({ fiscalIds: [], taxRates: [], uiFont: null }) } },
        { provide: AuthService, useValue: { currentUser: signal({ role: 'Admin' }) } },
        { provide: SecureImageService, useValue: { src: () => Promise.resolve(null) } },
        {
          provide: StoreDocumentsApi,
          useValue: { get: () => Promise.resolve(stored()), history: () => Promise.resolve([]), put },
        },
        {
          provide: I18nService,
          useValue: {
            t: (key: string) => key,
            text: (names: Record<string, string> | undefined) => names?.['fr'] ?? '',
            lang: signal('fr'),
            langs: signal([
              { code: 'fr', label: 'Français' },
              { code: 'ar', label: 'العربية' },
            ]),
            date: () => '',
            dateTime: () => '',
            money: () => '',
            rateLabel: () => '',
          },
        },
      ],
    });
  });

  async function open(): Promise<BackofficeStoreComponent> {
    const component = TestBed.createComponent(BackofficeStoreComponent).componentInstance;
    await component.ngOnInit();
    return component;
  }

  it('opens the preview on the draft in the editor, and saves nothing', async () => {
    const c = await open();
    c.openCard(SCAF);
    c.docs()!.texts[SCAF]['fr'] = '  Brouillon pour {customer}  ';
    c.openPreview(SCAF);

    const p = c.preview()!;
    expect(p.key).toBe(SCAF);
    expect(p.lang).toBe('fr');
    // The text as Save would send it, never what the server holds.
    expect(p.documents.documentTexts[SCAF]).toEqual({ fr: 'Brouillon pour {customer}' });
    expect(p.documents.documentTexts.rentalContract).toEqual({ fr: 'Conditions générales' });
    expect(p.documents.contractWordings).toEqual([{ key: SCAF, names: { fr: 'Échafaudage' } }]);
    // Previewing is not saving: no PUT, so none of the year's changes is spent.
    expect(put).not.toHaveBeenCalled();

    // Closing goes back to the editor, with its draft.
    c.closePreview();
    expect(c.preview()).toBeNull();
    expect(c.editing()).toBe(SCAF);
    expect(c.editorDirty()).toBeTrue();
    expect(c.docs()!.texts[SCAF]['fr']).toBe('  Brouillon pour {customer}  ');
  });

  it('opens in the language of the box last typed in, and switches without re-reading the draft', async () => {
    const c = await open();
    c.openCard('rentalContract');
    c.noteBox('rentalContract', 'ar');
    c.openPreview('rentalContract');
    expect(c.preview()!.lang).toBe('ar');

    const documents = c.preview()!.documents;
    c.setPreviewLang('fr');
    expect(c.preview()!.lang).toBe('fr');
    expect(c.preview()!.documents).toBe(documents);
  });

  it('previews a named wording with no text yet as empty, never as the standard one', async () => {
    const c = await open();
    c.openCard('addWording');
    const key = c.editing()!;
    c.openPreview(key);
    expect(c.preview()!.documents.documentTexts[key as typeof SCAF]).toEqual({});
  });

  it('offers fields and a preview on the contract wordings only', async () => {
    const c = await open();
    expect(c.isContractWording('rentalContract')).toBeTrue();
    expect(c.isContractWording(SCAF)).toBeTrue();
    expect(c.isContractWording('invoice')).toBeFalse();
    expect(c.isContractWording('contact')).toBeFalse();
    c.openPreview('invoice');
    expect(c.preview()).toBeNull();
  });

  it('cancelling the editor closes its preview and takes an inserted field away again', async () => {
    const c = await open();
    c.openCard('rentalContract');
    c.insertField('rentalContract', 'deposit');
    expect(c.docs()!.texts['rentalContract']['fr']).toBe('Conditions générales{deposit}');
    c.openPreview('rentalContract');
    await c.dismissEditor();
    expect(c.preview()).toBeNull();
    expect(c.docs()!.texts['rentalContract']['fr']).toBe('Conditions générales');
  });

  it('puts a field where the cursor is, in the box last typed in, and leaves the cursor after it', async () => {
    const fixture = TestBed.createComponent(BackofficeStoreComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const c = fixture.componentInstance;
    const el = fixture.nativeElement as HTMLElement;

    c.openCard('rentalContract');
    fixture.detectChanges();
    await fixture.whenStable();

    const chips = Array.from(el.querySelectorAll<HTMLButtonElement>('.field-chips button'));
    expect(chips.map((b) => b.getAttribute('title'))).toEqual(CONTRACT_FIELDS.map((f) => `{${f}}`));
    const preview = Array.from(el.querySelectorAll<HTMLButtonElement>('.docs-foot button')).find(
      (b) => b.textContent?.trim() === 'store.docs.preview.open',
    );
    expect(preview).withContext('the Preview action beside the wording').toBeDefined();

    const box = el.querySelector('#sd-rentalContract-ar') as HTMLTextAreaElement;
    box.value = 'بين  والشركة';
    box.dispatchEvent(new Event('input'));
    box.focus();
    box.dispatchEvent(new FocusEvent('focus'));
    box.setSelectionRange(4, 4);

    chips.find((b) => b.getAttribute('title') === '{customer}')!.click();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(c.docs()!.texts['rentalContract']['ar']).toBe('بين {customer} والشركة');
    expect(box.value).toBe('بين {customer} والشركة');
    expect(box.selectionStart).toBe(4 + '{customer}'.length);
    // The French text is untouched, and the edit is unsaved like anything typed.
    expect(c.docs()!.texts['rentalContract']['fr']).toBe('Conditions générales');
    expect(c.editorDirty()).toBeTrue();
    expect(put).not.toHaveBeenCalled();
  });
});
