
import { signal, DOCUMENT } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AdminService, AdminStore, UpsertStoreRequest } from '../../core/admin.service';
import { AuthService } from '../../core/auth.service';
import { I18nService } from '../../core/i18n.service';
import { PosService } from '../../core/pos.service';
import { RegionalService } from '../../core/regional.service';
import { Store } from '../../core/models';
import { UiFontService } from '../../core/ui-font.service';
import { StoreDocumentsApi } from '../print/store-documents-api.service';
import { ConfirmService } from '../../ui/confirm.service';
import { BackofficeStoreComponent } from './store-tab.component';

const row = (uiFont: string | null, version = 7): AdminStore => ({
  id: 'store-1',
  nameAr: 'مؤسسة',
  nameFr: 'Béton SARL',
  nameEn: 'Beton LLC',
  address: 'Alger',
  phone: '021',
  fiscalIds: {},
  receiptFooter: '',
  requireCustomerEmail: false,
  requireCustomerBirthDate: false,
  requireCustomerPhone: false,
  requireCustomerAddress: false,
  requireCustomerTaxId: false,
  loyaltyEnabled: false,
  loyaltyPointsPerUnit: 0,
  loyaltyPointValue: 0,
  loyaltyMinRedeemPoints: 0,
  uiFont,
  version,
});

/** Only what the screen and the face service actually read. */
function stubs() {
  const store = signal<Store | null>(null);
  const pos = {
    store,
    applyStore: (saved: Store) => {
      store.set(saved);
      return Promise.resolve();
    },
  };
  const server = { saved: null as UpsertStoreRequest | null };
  const admin = {
    getStore: () => Promise.resolve(row(null)),
    updateStore: (req: UpsertStoreRequest) => {
      server.saved = req;
      // The server answers with the stored row, version bumped — which is what the replica wants.
      return Promise.resolve(row((req as UpsertStoreRequest & { uiFont: string | null }).uiFont, 8));
    },
  };
  const regional = {
    settings: signal({
      fiscalIds: [] as unknown[],
      taxRates: [] as number[],
      uiFont: 'ibm-plex-sans-arabic',
      cashRoundingIncrement: 0,
      cashRoundingMode: 'Nearest',
    }),
  };
  return { pos, admin, regional, server };
}

describe('the company face reaches the screen that chose it', () => {
  let bits: ReturnType<typeof stubs>;

  beforeEach(() => {
    bits = stubs();
    TestBed.configureTestingModule({
      providers: [
        { provide: PosService, useValue: bits.pos },
        { provide: AdminService, useValue: bits.admin },
        { provide: RegionalService, useValue: bits.regional },
        { provide: StoreDocumentsApi, useValue: { get: () => Promise.reject(new Error('not admin')) } },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
        { provide: AuthService, useValue: { currentUser: signal(null) } },
        { provide: I18nService, useValue: { t: () => '', money: () => '', rateLabel: () => '' } },
      ],
    });
  });

  afterEach(() => document.documentElement.style.removeProperty('--font-ui'));

  it('puts the row the server returned into the replica the screens read', async () => {
    const screen = TestBed.runInInjectionContext(() => new BackofficeStoreComponent());
    await screen.ngOnInit();

    screen.uiFont.set('cairo');
    await screen.save();

    // The save reached the server...
    expect((bits.server.saved as { uiFont: string | null } | null)?.uiFont).toBe('cairo');
    // ...and, in the same breath, the local replica — which is the half that was missing. Until
    // this line the operator kept the old face on the very screen that changed it, until the
    // sixty-second auto-sync or a click on the connection button pulled the row back.
    expect(bits.pos.store()?.uiFont).toBe('cairo');
    // The server's version, not the form's: the next pull then has nothing to undo.
    expect(bits.pos.store()?.version).toBe(8);
  });

  it('clearing the face is a choice too, and lands just as fast', async () => {
    const screen = TestBed.runInInjectionContext(() => new BackofficeStoreComponent());
    await screen.ngOnInit();

    screen.uiFont.set('');
    await screen.save();

    expect(bits.pos.store()?.uiFont).toBeNull();
  });

  it('a face in the replica is on the page at once, and the profile decides when there is none', () => {
    TestBed.runInInjectionContext(() => new UiFontService());
    TestBed.flushEffects();
    // No company face yet: the country profile's is applied.
    expect(document.documentElement.style.getPropertyValue('--font-ui')).toContain('IBM Plex Sans Arabic');

    void bits.pos.applyStore(row('cairo') as Store);
    TestBed.flushEffects();

    expect(document.documentElement.style.getPropertyValue('--font-ui')).toContain('Cairo');
  });
});
