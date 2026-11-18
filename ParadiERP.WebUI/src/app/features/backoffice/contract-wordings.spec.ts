import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AdminService } from '../../core/admin.service';
import { AuthService } from '../../core/auth.service';
import { I18nService } from '../../core/i18n.service';
import { PosService } from '../../core/pos.service';
import { RegionalService } from '../../core/regional.service';
import { SecureImageService } from '../../core/secure-image.service';
import { ConfirmService } from '../../ui/confirm.service';
import { DOCUMENT_TEXT_KINDS, StoreDocumentsApi, StoreDocumentsDto, newWordingKey } from '../print/store-documents-api.service';
import { BackofficeStoreComponent } from './store-tab.component';

/**
 * Several named rental-contract wordings (owner, PinSay #302): added, named and written on the
 * printed-documents panel beside the standard wording, and sent whole on its one Save.
 */
describe('named contract wordings', () => {
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
  let confirmAsk: jasmine.Spy;

  async function open(): Promise<BackofficeStoreComponent> {
    put = jasmine.createSpy('put').and.callFake((dto: StoreDocumentsDto) => Promise.resolve({ ...dto, allowances: [] }));
    confirmAsk = jasmine.createSpy('ask').and.resolveTo(true);
    TestBed.configureTestingModule({
      providers: [
        { provide: ConfirmService, useValue: { ask: confirmAsk } },
        { provide: AdminService, useValue: { getStore: () => Promise.reject(new Error('not needed')) } },
        { provide: PosService, useValue: {} },
        { provide: RegionalService, useValue: { settings: signal({ fiscalIds: [], taxRates: [], uiFont: null }) } },
        { provide: AuthService, useValue: { currentUser: signal({ role: 'Admin' }) } },
        { provide: SecureImageService, useValue: { src: () => Promise.resolve(null) } },
        {
          provide: StoreDocumentsApi,
          useValue: {
            get: () => Promise.resolve(stored()),
            history: () => Promise.resolve([]),
            sample: () => Promise.resolve({ ...stored(), documentTexts: { rentalContract: { fr: 'EXEMPLE' } }, contractWordings: [] }),
            put,
          },
        },
        {
          provide: I18nService,
          useValue: {
            t: (key: string) => key,
            text: (names: Record<string, string> | undefined) => names?.['fr'] ?? '',
            langs: signal([{ code: 'fr', label: 'Français' }]),
            date: () => '',
            dateTime: () => '',
            money: () => '',
            rateLabel: () => '',
          },
        },
      ],
    });
    const component = TestBed.createComponent(BackofficeStoreComponent).componentInstance;
    await component.ngOnInit();
    return component;
  }

  it('loads each wording with its name and its text beside the kinds', async () => {
    const c = await open();
    const d = c.docs()!;
    expect(d.wordings.map((w) => w.key)).toEqual([SCAF]);
    expect(d.texts[SCAF]['fr']).toBe('Conditions échafaudage');
    expect(d.texts['rentalContract']['fr']).toBe('Conditions générales');
    expect(c.wordingName(d.wordings[0])).toBe('Échafaudage');
  });

  it('adds a wording, opened and said not saved, and sends the list and its text on Save', async () => {
    const c = await open();
    c.addWording();
    const added = c.docs()!.wordings[1];
    expect(added.key).toMatch(/^rentalContract\.[a-z0-9]{8}$/);
    expect(c.isFresh(added.key)).toBeTrue();
    expect(c.wordingName(added)).toBe('store.docs.wordings.unnamed');

    added.names['fr'] = '  Coffrage  ';
    c.docs()!.texts[added.key]['fr'] = 'Conditions coffrage';
    await c.saveDocuments();

    const sent = put.calls.mostRecent().args[0] as StoreDocumentsDto;
    expect(sent.contractWordings).toEqual([
      { key: SCAF, names: { fr: 'Échafaudage' } },
      { key: added.key, names: { fr: 'Coffrage' } },
    ]);
    expect(sent.documentTexts[added.key]).toEqual({ fr: 'Conditions coffrage' });
    expect(sent.documentTexts[SCAF]).toEqual({ fr: 'Conditions échafaudage' });
    // Saved: no longer said "not saved yet".
    expect(c.isFresh(added.key)).toBeFalse();
  });

  it('takes a removed wording and its text off the next Save', async () => {
    const c = await open();
    c.removeWording(SCAF);
    await c.saveDocuments();
    const sent = put.calls.mostRecent().args[0] as StoreDocumentsDto;
    expect(sent.contractWordings).toEqual([]);
    expect(sent.documentTexts[SCAF]).toBeUndefined();
  });

  it('keeps a wording\'s own text when the sample fills the kinds', async () => {
    const c = await open();
    await c.fillWithSample();
    expect(c.docs()!.texts['rentalContract']['fr']).toBe('EXEMPLE');
    expect(c.docs()!.texts[SCAF]['fr']).toBe('Conditions échafaudage');
  });

  describe('as cards that each open one editor', () => {
    it('adds a wording from its card, said unsaved, and Cancel takes it away again', async () => {
      const c = await open();
      c.openCard('addWording');
      const added = c.docs()!.wordings[1];
      expect(c.editing()).toBe(added.key);
      expect(c.editorDirty()).toBeTrue();
      expect(c.cardBadge(added.key)?.text).toBe('store.docs.wordings.notSaved');

      await c.dismissEditor();
      expect(confirmAsk).toHaveBeenCalled();
      expect(c.editing()).toBeNull();
      expect(c.docs()!.wordings.map((w) => w.key)).toEqual([SCAF]);
      expect(c.isFresh(added.key)).toBeFalse();
      expect(put).not.toHaveBeenCalled();
    });

    it('puts back what was typed when the editor is cancelled', async () => {
      const c = await open();
      c.openCard('rentalContract');
      c.docs()!.texts['rentalContract']['fr'] = 'Autre chose';
      expect(c.editorDirty()).toBeTrue();
      await c.dismissEditor();
      expect(c.docs()!.texts['rentalContract']['fr']).toBe('Conditions générales');
    });

    it('closes an untouched editor without asking', async () => {
      const c = await open();
      c.openCard('contact');
      expect(c.editorDirty()).toBeFalse();
      await c.dismissEditor();
      expect(confirmAsk).not.toHaveBeenCalled();
      expect(c.editing()).toBeNull();
    });

    it("saves the group whole from the editor's Save, then closes it", async () => {
      const c = await open();
      c.openCard('contact');
      c.docs()!.email = 'contact@example.dz';
      await c.saveEditor();
      const sent = put.calls.mostRecent().args[0] as StoreDocumentsDto;
      expect(sent.email).toBe('contact@example.dz');
      expect(sent.documentTexts[SCAF]).toEqual({ fr: 'Conditions échafaudage' });
      expect(c.editing()).toBeNull();
    });

    it('keeps the editor open, with its edit, when the server refuses the save', async () => {
      const c = await open();
      put.and.returnValue(Promise.reject(new Error('refused')));
      c.openCard('rentalContract');
      c.docs()!.texts['rentalContract']['fr'] = 'Autre chose';
      await c.saveEditor();
      expect(c.editing()).toBe('rentalContract');
      expect(c.docsError()).toBeTruthy();
      expect(c.docs()!.texts['rentalContract']['fr']).toBe('Autre chose');
    });

    it('draws every setting as one card that opens the dialog, its state said in words', async () => {
      const c = await open();
      const fixture = TestBed.createComponent(BackofficeStoreComponent);
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
      const el = fixture.nativeElement as HTMLElement;
      const cards = el.querySelectorAll<HTMLButtonElement>('button.doc-card');
      expect(cards.length).toBe(c.docGroups().flatMap((g) => g.cards).length);
      // The forward chevron is the bidi-mirrored character itself, never flipped by a rule.
      expect(cards[0].querySelector('.doc-card__chev')?.textContent?.trim()).toBe('›');
      cards[0].click();
      fixture.detectChanges();
      expect(el.querySelector('dialog')?.open).toBeTrue();
      expect(el.querySelector('.docs-foot__state')?.textContent?.trim()).toBe('store.docs.editor.clean');
      (el.querySelector('#sd-email') as HTMLInputElement).value = 'a@b.dz';
      el.querySelector('#sd-email')!.dispatchEvent(new Event('input'));
      fixture.detectChanges();
      expect(el.querySelector('.docs-foot__state')?.textContent?.trim()).toBe('store.docs.editor.unsaved');
    });

    it('groups only the settings that exist, the named wordings beside the standard one', async () => {
      const c = await open();
      const cards = c.docGroups().flatMap((g) => g.cards);
      expect(cards.filter((k) => k !== 'addWording' && !['contact', 'logo', 'titleLangs', 'itemNames', 'limit', 'sample'].includes(k)).sort()).toEqual(
        [...DOCUMENT_TEXT_KINDS, SCAF].sort(),
      );
      const rental = c.docGroups().find((g) => g.id === 'rental')!.cards;
      expect(rental.indexOf(SCAF)).toBe(rental.indexOf('rentalContract') + 1);
    });
  });

  it('draws a new key unlike any already taken', () => {
    const rolls = [0, 0, 0, 0, 0, 0, 0, 0, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5];
    let i = 0;
    const key = newWordingKey(['rentalContract.00000000'], () => rolls[i++]);
    expect(key).toBe('rentalContract.iiiiiiii');
  });
});
