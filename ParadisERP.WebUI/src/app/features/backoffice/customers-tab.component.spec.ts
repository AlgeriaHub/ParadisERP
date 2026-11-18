import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AdminCustomer, AdminService, AdminStore, CustomerAttachment, UpsertCustomerRequest } from '../../core/admin.service';
import { ApiError } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { isolate } from '../../core/bidi';
import { BranchScopeService } from '../../core/branch-scope.service';
import { I18nService } from '../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../core/i18n/translations-backoffice';
import { RegionalService } from '../../core/regional.service';
import { BuiltinLang, PosUser } from '../../core/models';
import { PosService } from '../../core/pos.service';
import { SecureImageService } from '../../core/secure-image.service';
import { ConfirmService } from '../../ui/confirm.service';
import { StoreDocumentsApi } from '../print/store-documents-api.service';
import { BackofficeCustomersComponent } from './customers-tab.component';
import { BackofficeStoreComponent } from './store-tab.component';

/**
 * Two owner decisions of 2026-10-05 on the customer editor:
 *
 * - "الرقم الجبائي للشركات فقط" — the tax number is a company's. A person is not asked for one,
 *   the store's "required" applies to companies only, and a person saved with one before the rule
 *   may keep it unchanged or remove it, never retype it.
 * - The code is never typed: the server draws it on Save — the kind's prefix and a dash, the
 *   location's three-digit code, YYMM, a sequence (Cust-3612610001 for a person, Comp-3612610001 for
 *   a company) — and it never changes. The form describes the shape, never predicts a number, and
 *   sends the branch the code is drawn at.
 */
const NIF = '000116001234567';

/** A person as the server files one now: in two parts (owner, 2026-10-06), unless the test says otherwise. */
const customer = (over: Partial<AdminCustomer> = {}): AdminCustomer => ({
  id: 'c1',
  code: 'C001',
  fullName: 'Karim Benali',
  ...((over.kind ?? 'Person') === 'Person' ? { firstName: 'Karim', familyName: 'Benali' } : {}),
  phone: null,
  address: null,
  taxId: null,
  email: null,
  birthDate: null,
  creditLimit: 0,
  balance: 0,
  loyaltyPoints: 0,
  version: 1,
  kind: 'Person',
  idDocumentNumber: null,
  representative: null,
  ...over,
});

const signatory = {
  id: 'r1',
  customerId: 'c2',
  fullName: 'Nadia Bouzid',
  idDocumentNumber: null,
  phone: null,
  note: null,
  fromUtc: '2026-01-05T09:00:00Z',
  toUtc: null,
  createdBy: null,
  createdAtUtc: '2026-01-05T09:00:00Z',
};

const kept = (over: Partial<CustomerAttachment> = {}): CustomerAttachment => ({
  id: 'scan-1',
  kind: 'idDocument',
  fileName: 'carte-recto.jpg',
  contentType: 'image/jpeg',
  length: 48_000,
  uploadedAtUtc: '2026-10-01T09:00:00Z',
  uploadedBy: 'admin',
  ...over,
});

/** A file input's change, as the browser fires it after a pick. */
const picked = (...files: File[]) => ({ target: { files, value: 'C:\\fakepath\\x' } }) as unknown as Event;
const jpeg = (name = 'carte-recto.jpg', size = 1000) => new File([new Uint8Array(size)], name, { type: 'image/jpeg' });

const store = (requireTaxId: boolean): AdminStore =>
  ({
    requireCustomerPhone: false,
    requireCustomerEmail: false,
    requireCustomerBirthDate: false,
    requireCustomerAddress: false,
    requireCustomerTaxId: requireTaxId,
  }) as AdminStore;

describe('BackofficeCustomersComponent — tax number and drawn code', () => {
  let admin: jasmine.SpyObj<AdminService>;
  let fixture: ComponentFixture<BackofficeCustomersComponent>;

  /** The server's answer to every page request: these rows, as one page. */
  function page(rows: AdminCustomer[]) {
    admin.customersPage.and.callFake(async (q) => ({ items: rows, total: rows.length, skip: 0, take: q.take }));
  }

  async function open(requireTaxId = false, rows: AdminCustomer[] = []) {
    admin.getStore.and.resolveTo(store(requireTaxId));
    page(rows);
    fixture = TestBed.createComponent(BackofficeCustomersComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.componentInstance;
  }

  beforeEach(() => {
    admin = jasmine.createSpyObj<AdminService>('AdminService', [
      'getStore',
      'customersPage',
      'createCustomer',
      'updateCustomer',
      'listRepresentatives',
      'listCustomerAttachments',
      'uploadCustomerAttachment',
      'removeCustomerAttachment',
    ]);
    admin.listCustomerAttachments.and.resolveTo([]);
    admin.uploadCustomerAttachment.and.callFake(async (_id: string, file: File) => kept({ fileName: file.name }));
    admin.removeCustomerAttachment.and.resolveTo();
    admin.createCustomer.and.callFake(async (r: UpsertCustomerRequest) =>
      customer({ id: 'new-1', code: r.kind === 'Company' ? 'Comp-112610001' : 'Cust-112610001', ...r } as Partial<AdminCustomer>),
    );
    admin.updateCustomer.and.callFake(async (id: string, r: UpsertCustomerRequest) => customer({ id, ...r } as Partial<AdminCustomer>));
    admin.listRepresentatives.and.resolveTo([]);
    TestBed.configureTestingModule({
      imports: [BackofficeCustomersComponent],
      providers: [
        { provide: AdminService, useValue: admin },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
  });

  const i18n = () => TestBed.inject(I18nService);
  const el = () => fixture.nativeElement as HTMLElement;
  const q = (selector: string) => el().querySelector(selector) as HTMLElement | null;
  const text = (selector: string) => q(selector)?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const sent = (spy: jasmine.Spy) => spy.calls.mostRecent().args.at(-1) as UpsertCustomerRequest;
  const render = () => fixture.detectChanges();

  describe('the tax number is for companies only', () => {
    it('is hidden for a person and shown for a company', async () => {
      const c = await open();
      expect(q('#cu-tax')).toBeNull(); // no kind chosen yet: nobody is a company
      c.chooseKind('Person');
      render();
      expect(q('#cu-tax')).toBeNull();
      c.chooseKind('Company');
      render();
      expect(q('#cu-tax')).not.toBeNull();
      expect(text('label[for="cu-tax"]')).toContain(i18n().t('backoffice.customers.taxId'));
    });

    it('is required only of a company when the store asks for it', async () => {
      const c = await open(true);
      c.form.fullName = 'Béton SARL';
      c.chooseKind('Company');
      c.repFullName.set('Nadia Bouzid');
      render();
      expect(c.taxIdRequired()).toBeTrue();
      expect(text('label[for="cu-tax"]')).toContain(i18n().t('common.required'));
      expect(c.canSave()).toBeFalse();
      c.form.taxId = NIF;
      expect(c.canSave()).toBeTrue();

      c.chooseKind('Person');
      c.form.firstName = 'Karim';
      render();
      expect(c.taxIdRequired()).toBeFalse();
      expect(c.canSave()).toBeTrue();
    });

    it('is not required of a legacy row whose kind is not set, but stays on its form as before', async () => {
      const c = await open(true);
      c.edit(customer({ kind: 'Unspecified' }));
      render();
      expect(q('#cu-tax')).not.toBeNull();
      expect(text('label[for="cu-tax"]')).toContain(i18n().t('common.optional'));
      expect(c.canSave()).toBeTrue();
    });

    it('sends no tax number for a new person', async () => {
      const c = await open();
      c.chooseKind('Person');
      Object.assign(c.form, { firstName: 'Karim', familyName: 'Benali' });
      await c.save();
      expect(admin.createCustomer).toHaveBeenCalledTimes(1);
      expect(sent(admin.createCustomer).taxId).toBeNull();
    });

    it('clears what was typed, and its complaint, when a new customer goes from company to person', async () => {
      const c = await open(true);
      c.chooseKind('Company');
      c.touched.touch('taxId');
      render();
      expect(q('#cu-tax-error')).not.toBeNull(); // required, left empty
      c.form.taxId = NIF;
      render();
      c.chooseKind('Person');
      render();
      expect(c.form.taxId).toBeNull();
      expect(q('#cu-tax')).toBeNull();
      c.chooseKind('Company');
      render();
      expect((q('#cu-tax') as HTMLInputElement).value).toBe('');
      expect(q('#cu-tax-error')).toBeNull();
    });

    it('shows a legacy person their stored number with a notice in words, and keeps it unchanged on save', async () => {
      const legacy = customer({ taxId: NIF });
      const c = await open(false, [legacy]);
      c.edit(legacy);
      render();
      expect(q('input#cu-tax')).toBeNull(); // nothing to retype
      expect(text('#cu-tax-kept')).toBe(NIF);
      expect(text('#cu-tax-person')).toBe(i18n().t('cust.taxId.personHasNone'));
      expect(q('#cu-tax-person')?.classList).toContain('warn');

      c.form.phone = '0550 12 34 56'; // a phone fix must not fail on the old number
      await c.save();
      expect(sent(admin.updateCustomer).taxId).toBe(NIF);
    });

    it('lets a legacy person remove the stored number: Remove, then save sends it blank', async () => {
      const legacy = customer({ taxId: NIF });
      const c = await open(false, [legacy]);
      c.edit(legacy);
      render();
      (q('#cu-tax-remove') as HTMLButtonElement).click();
      render();
      expect(q('#cu-tax-kept')).toBeNull();
      await c.save();
      expect(sent(admin.updateCustomer).taxId).toBeNull();
    });

    it('says before saving that a company made a person loses its number, and sends it blank', async () => {
      const company = customer({ id: 'c2', kind: 'Company', taxId: NIF, representative: signatory });
      const c = await open(false, [company]);
      c.edit(company);
      c.chooseKind('Person');
      render();
      expect(text('#cu-tax-ends')).toBe(i18n().t('cust.taxId.endsOnPerson'));
      Object.assign(c.form, { firstName: 'Nadia', familyName: 'Bouzid' });
      await c.save();
      expect(sent(admin.updateCustomer).taxId).toBeNull();
    });

    it('says the server refusal in the reader’s words', async () => {
      const c = await open();
      admin.createCustomer.and.rejectWith(
        new ApiError(JSON.stringify({ status: 400, code: 'taxIdCompanyOnly', detail: 'A tax number is for companies only: a natural person has none.' }), 400),
      );
      c.chooseKind('Person');
      Object.assign(c.form, { firstName: 'Karim', familyName: 'Benali' });
      await c.save();
      expect(c.error()).toBe(i18n().t('cust.taxId.companyOnly'));
      // The label comes from the country profile, not from the sentence.
      expect(c.error()).toContain(i18n().t('backoffice.customers.taxId'));
    });

    it('labels the store setting as applying to companies, with the profile’s own label, in every language', () => {
      const labels = TestBed.inject(RegionalService).settings().customerTaxIdLabels;
      const companies: Record<BuiltinLang, string> = { fr: 'sociétés', ar: 'للشركات', en: 'companies' };
      for (const lang of ['fr', 'ar', 'en'] as BuiltinLang[]) {
        const label = i18n().tIn(lang, 'backoffice.store.requireTaxId');
        expect(label).toContain(labels[lang]!);
        expect(label).toContain(companies[lang]);
      }
    });
  });

  describe('a new customer starts with the kind alone, in a small dialog that grows (owner, 2026-10-05)', () => {
    const panel = () => q('section.bo-panel') as HTMLElement;
    const saveButton = () => q('.bo-panel__foot .btn-primary');
    /** Opens a new customer the way the page's button does, and lets the dialog draw. */
    async function openNew() {
      const c = await open();
      c.openCreate();
      render();
      await fixture.whenStable();
      return c;
    }
    /** The editor's own motion, not the controls' colour transitions (CSS) that focus and hover start. */
    const motion = (el: Element) => el.getAnimations().filter((a) => !(a instanceof CSSTransition) && !(a instanceof CSSAnimation));
    /** Lets the size change start, then run to its end, as the reader would wait for it. */
    const settle = async () => {
      await fixture.whenStable();
      await Promise.all(panel().getAnimations({ subtree: true }).map((a) => (a.finish(), a.finished)));
      await fixture.whenStable();
    };
    /** The centre of the area the dialog is placed in: the backdrop covers exactly that. */
    const centreOf = (el: Element) => {
      const r = el.getBoundingClientRect();
      return r.left + r.width / 2;
    };

    it('opens small, with only the two choices and Cancel: no field, no Save, no default', async () => {
      const c = await openNew();
      expect(panel().classList).toContain('is-choosing');
      expect(c.kind()).toBe('Unspecified');
      expect(text('#cu-pick-title')).toBe(i18n().t('cust.kind.pick.title'));
      expect(text('#cu-pick-person-name')).toBe(i18n().t('cust.kind.pick.person'));
      expect(text('#cu-pick-company-name')).toBe(i18n().t('cust.kind.pick.company'));
      expect(text('#cu-pick-person-hint')).toBe(i18n().t('cust.kind.pick.personHint'));
      expect(text('#cu-pick-company-hint')).toBe(i18n().t('cust.kind.pick.companyHint'));
      for (const id of ['#cu-pick-person', '#cu-pick-company']) expect(q(id)?.getAttribute('aria-pressed')).toBe('false');
      expect(panel().querySelectorAll('input, select, textarea').length).toBe(0);
      expect(saveButton()).toBeNull();
      expect(text('.bo-panel__foot')).toBe(i18n().t('common.cancel'));
      // The cursor is on the first choice.
      expect(document.activeElement).toBe(q('#cu-pick-person'));
    });

    it('is the small size while it asks, and the editor size once it is answered', async () => {
      const c = await openNew();
      const small = panel().getBoundingClientRect().width;
      c.pickKind('Person');
      render();
      await settle();
      expect(panel().getBoundingClientRect().width).toBeGreaterThan(small);
    });

    it('grows into the person’s fields over the system’s resize length, then puts the cursor on the name', async () => {
      const c = await openNew();
      (q('#cu-pick-person') as HTMLButtonElement).click();
      render();
      await fixture.whenStable();
      expect(panel().classList).not.toContain('is-choosing');
      const growth = motion(panel());
      expect(growth.length).toBe(1);
      const ms = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--t-slide')) * 1000;
      expect(growth[0].effect?.getTiming().duration).toBe(ms);
      const frames = (growth[0].effect as KeyframeEffect).getKeyframes();
      expect(Object.keys(frames[0])).toEqual(jasmine.arrayContaining(['inlineSize', 'blockSize']));
      // The fields fade in over the same time; nothing else moves.
      const fade = motion(q('.bo-panel__body')!);
      expect(fade.length).toBe(1);
      expect(fade[0].effect?.getTiming().duration).toBe(ms);

      await settle();
      expect(c.kind()).toBe('Person');
      expect(q('#cu-first-name')).not.toBeNull();
      expect(q('#cu-family-name')).not.toBeNull();
      expect(q('#cu-name')).toBeNull();
      expect(q('#cu-id-doc')).not.toBeNull(); // the person's own paper
      expect(q('#cu-tax')).toBeNull();
      expect(q('#cu-rep-name')).toBeNull();
      expect(saveButton()).not.toBeNull();
      expect(document.activeElement).toBe(q('#cu-first-name'));
      // The choice stays at the top, as the chips, so it can still be switched.
      expect(q('.bo-chips button[aria-pressed="true"]')?.textContent?.trim()).toBe(i18n().t('cust.kind.person'));
    });

    it('does not move at all when the reader asks for reduced motion', async () => {
      const real = window.matchMedia.bind(window);
      spyOn(window, 'matchMedia').and.callFake((query: string) =>
        query.includes('prefers-reduced-motion') ? ({ matches: true, media: query } as MediaQueryList) : real(query),
      );
      const c = await openNew();
      c.pickKind('Company');
      render();
      await fixture.whenStable();
      expect(motion(panel()).length).toBe(0);
      expect(motion(q('.bo-panel__body')!).length).toBe(0);
      expect(panel().classList).not.toContain('is-choosing');
      expect(q('#cu-tax')).not.toBeNull();
      expect(q('#cu-rep-name')).not.toBeNull();
      expect(document.activeElement).toBe(q('#cu-name'));
    });

    it('grows from the centre in Arabic too: the same dialog, mirrored', async () => {
      const html = document.documentElement;
      const was = html.dir;
      html.dir = 'rtl';
      try {
        const c = await openNew();
        const backdrop = q('.bo-backdrop')!;
        expect(centreOf(panel())).toBeCloseTo(centreOf(backdrop), 0);
        c.pickKind('Person');
        render();
        await settle();
        expect(centreOf(panel())).toBeCloseTo(centreOf(backdrop), 0);
      } finally {
        html.dir = was;
      }
    });

    it('keeps what both kinds share when the kind is switched, and drops the other kind’s fields', async () => {
      const c = await openNew();
      c.pickKind('Company');
      render();
      await settle();
      Object.assign(c.form, {
        fullName: 'Béton SARL', phone: '021 00 00 00', address: 'Alger', email: 'contact@beton.dz',
        birthDate: '1990-01-01', creditLimit: 50000, taxId: NIF,
      });
      c.repFullName.set('Nadia Bouzid');
      c.repPhone.set('0550 00 00 00');
      c.chooseKind('Person');
      // The one name becomes the family name, for the clerk to move the first name out of.
      expect(c.form).toEqual(jasmine.objectContaining({
        firstName: '', familyName: 'Béton SARL', phone: '021 00 00 00', address: 'Alger', email: 'contact@beton.dz',
        birthDate: '1990-01-01', creditLimit: 50000, taxId: null,
      }));
      expect(c.repFullName()).toBe('');
      expect(c.repPhone()).toBe('');

      c.idDocumentNumber.set('109876543');
      c.idDocumentIssuer.set('Daïra d’Alger');
      c.chooseKind('Company');
      expect(c.form.phone).toBe('021 00 00 00');
      expect(c.idDocumentNumber()).toBe('');
      expect(c.idDocumentIssuer()).toBe('');
    });

    it('opens an existing customer straight in the full editor, as before', async () => {
      const row = customer({ kind: 'Person' });
      const c = await open(false, [row]);
      c.edit(row);
      render();
      expect(panel().classList).not.toContain('is-choosing');
      expect(q('#cu-pick-person')).toBeNull();
      expect(q('#cu-first-name')).not.toBeNull();
      expect(saveButton()).not.toBeNull();

      // A legacy row whose kind was never set keeps its form and its warn chip, not the question.
      c.edit(customer({ kind: 'Unspecified' }));
      render();
      expect(q('#cu-pick-person')).toBeNull();
      expect(q('#cu-name')).not.toBeNull();
      expect(text('.bo-states .bo-state--warn')).toBe(i18n().t('cust.kind.unset'));
    });
  });

  // Owner, 2026-10-06: a person's name in two inputs — the first name, then the family name (اللقب).
  describe('a person’s name in two parts', () => {
    it('asks a new person for both, and sends the parts with the full name first name first', async () => {
      const c = await open();
      c.chooseKind('Person');
      render();
      expect(text('label[for="cu-first-name"]')).toContain(i18n().t('cust.firstName'));
      expect(text('label[for="cu-family-name"]')).toContain(i18n().t('cust.familyName'));

      c.form.firstName = '  Karim ';
      expect(c.canSave()).toBeFalse(); // the first name alone is not a name
      c.form.familyName = ' Benali ';
      expect(c.canSave()).toBeTrue();
      await c.save();
      const body = sent(admin.createCustomer);
      expect([body.firstName, body.familyName, body.fullName]).toEqual(['Karim', 'Benali', 'Karim Benali']);
    });

    it('asks for the first name of a person filed before the split, and saves only once it is moved out', async () => {
      // As the migration left them: no first name, the whole name as the family name.
      const legacy = customer({ firstName: null, familyName: 'Karim Benali' });
      const c = await open(false, [legacy]);
      c.edit(legacy);
      render();
      expect(q('#cu-first-name')).not.toBeNull();
      expect([c.form.firstName, c.form.familyName]).toEqual(['', 'Karim Benali']);
      expect(text('#cu-split-name')).toBe(i18n().t('cust.splitName'));
      await c.save();
      expect(admin.updateCustomer).not.toHaveBeenCalled();

      Object.assign(c.form, { firstName: 'Karim', familyName: 'Benali' });
      render();
      expect(q('#cu-split-name')).toBeNull();
      await c.save();
      const body = sent(admin.updateCustomer);
      expect([body.firstName, body.familyName, body.fullName]).toEqual(['Karim', 'Benali', 'Karim Benali']);
    });

    it('gives a company one name: a person’s two parts become it, and no parts are sent', async () => {
      const c = await open();
      c.chooseKind('Person');
      Object.assign(c.form, { firstName: 'Karim', familyName: 'Benali' });
      c.chooseKind('Company');
      render();
      expect(q('#cu-first-name')).toBeNull();
      expect(q('#cu-name')).not.toBeNull();
      expect(c.form.fullName).toBe('Karim Benali');

      c.form.fullName = ' Béton SARL ';
      c.repFullName.set('Nadia Bouzid');
      await c.save();
      const body = sent(admin.createCustomer);
      expect([body.firstName, body.familyName, body.fullName]).toEqual([null, null, 'Béton SARL']);
    });
  });

  describe('the copy of the identity document', () => {
    /** Copies already kept are listed only with customers.documents.view (owner, 2026-10-25). */
    const signedIn = (capabilities: string[]) =>
      TestBed.inject(AuthService).currentUser.set({
        id: 'u-1', username: 'gerant', fullName: 'Gérant', role: 'Admin', pinHash: '', isActive: true,
        version: 1, capabilities,
      } as unknown as PosUser);

    beforeEach(() => signedIn(['customers.documents.view']));

    it('without the permission, says the kept copies are restricted, asks for none, and still adds one', async () => {
      signedIn([]);
      admin.listCustomerAttachments.and.resolveTo([kept()]);
      const row = customer();
      const c = await open(false, [row]);
      c.edit(row);
      await fixture.whenStable();
      render();
      expect(admin.listCustomerAttachments).not.toHaveBeenCalled();
      expect(text('#cu-scan-restricted')).toBe(i18n().t('cust.scan.restricted'));
      expect(q('#cu-scan-none')).toBeNull();
      expect(text('#cu-scans')).not.toContain('carte-recto.jpg');

      c.chooseScans(picked(jpeg('verso.jpg')));
      await c.save();
      expect(admin.uploadCustomerAttachment).toHaveBeenCalledWith(row.id, jasmine.any(File));
    });

    it('is offered to a new person, optional, and the chosen files go up once the customer exists', async () => {
      const c = await open();
      c.chooseKind('Person');
      render();
      expect(text('#cu-scans h3')).toBe(i18n().t('cust.scan.title'));
      expect(text('#cu-scan-none')).toBe(i18n().t('cust.scan.none'));

      c.chooseScans(picked(jpeg('recto.jpg'), jpeg('verso.jpg')));
      render();
      expect(c.pendingScans().map((f) => f.name)).toEqual(['recto.jpg', 'verso.jpg']);
      expect(text('#cu-scans')).toContain(i18n().t('cust.scan.pending'));
      expect(admin.uploadCustomerAttachment).not.toHaveBeenCalled(); // nothing goes up before Save

      Object.assign(c.form, { firstName: 'Karim', familyName: 'Benali' });
      await c.save();
      expect(admin.uploadCustomerAttachment.calls.allArgs().map(([id, f]) => [id, f.name])).toEqual([
        ['new-1', 'recto.jpg'],
        ['new-1', 'verso.jpg'],
      ]);
      expect(c.error()).toBeNull();
    });

    it('saves a person with no copy at all', async () => {
      const c = await open();
      c.chooseKind('Person');
      Object.assign(c.form, { firstName: 'Karim', familyName: 'Benali' });
      await c.save();
      expect(admin.createCustomer).toHaveBeenCalled();
      expect(admin.uploadCustomerAttachment).not.toHaveBeenCalled();
    });

    it('refuses a file of another type or over 5 MB at once, in words', async () => {
      const c = await open();
      c.chooseKind('Person');
      c.chooseScans(picked(new File(['x'], 'notes.txt', { type: 'text/plain' })));
      expect(c.scanError()).toBe(i18n().t('cust.scan.wrongType'));
      c.chooseScans(picked(jpeg('big.jpg', 5 * 1024 * 1024 + 1)));
      expect(c.scanError()).toBe(i18n().t('cust.scan.tooLarge'));
      expect(c.pendingScans()).toEqual([]);
    });

    it('shows what is kept when a customer is edited, adds a copy later, and takes one off', async () => {
      admin.listCustomerAttachments.and.resolveTo([kept()]);
      const row = customer();
      const c = await open(false, [row]);
      c.edit(row);
      await fixture.whenStable();
      render();
      expect(admin.listCustomerAttachments).toHaveBeenCalledWith(row.id);
      expect(text('#cu-scans')).toContain('carte-recto.jpg');
      expect(text('#cu-scans')).toContain(i18n().t('cust.scan.kept'));

      c.chooseScans(picked(jpeg('verso.jpg')));
      await c.save();
      expect(admin.uploadCustomerAttachment).toHaveBeenCalledWith(row.id, jasmine.any(File));

      c.edit(row);
      await fixture.whenStable();
      await c.removeScan(c.scans()[0]);
      expect(admin.removeCustomerAttachment).toHaveBeenCalledWith(row.id, 'scan-1');
      expect(c.scans()).toEqual([]);
    });

    it('is not offered to a company, and a file chosen while it was a person is not sent', async () => {
      const c = await open();
      c.chooseKind('Person');
      c.chooseScans(picked(jpeg()));
      c.chooseKind('Company');
      render();
      expect(q('#cu-scans')).toBeNull();
      c.form.fullName = 'Béton SARL';
      c.repFullName.set('Nadia Bouzid');
      await c.save();
      expect(admin.createCustomer).toHaveBeenCalled();
      expect(admin.uploadCustomerAttachment).not.toHaveBeenCalled();
    });

    it('keeps the customer when a copy fails to go up, and says which one to add again', async () => {
      admin.uploadCustomerAttachment.and.rejectWith(new ApiError('"The file is empty."', 400));
      const c = await open();
      c.chooseKind('Person');
      c.chooseScans(picked(jpeg('recto.jpg')));
      Object.assign(c.form, { firstName: 'Karim', familyName: 'Benali' });
      await c.save();
      expect(admin.createCustomer).toHaveBeenCalled();
      expect(c.error()).toBe(i18n().t('cust.scan.uploadFailed', { name: 'recto.jpg', reason: 'The file is empty.' }));
    });
  });

  describe('the code is drawn by the server', () => {
    it('asks for no code on a new customer: a line says it is generated on save, in the kind’s shape', async () => {
      const c = await open();
      c.chooseKind('Person');
      render();
      expect(q('#cu-code')).toBeNull();
      expect(q('input[id^="cu-code"]')).toBeNull();
      expect(text('#cu-code-new')).toContain(i18n().t('cust.code.onSave'));
      expect(text('#cu-code-hint')).toBe(i18n().t('cust.code.shape'));
      expect(text('#cu-code-new')).toContain(i18n().t('cust.code.pattern.person'));
      expect(text('#cu-code-new')).toContain('Cust-');
      c.chooseKind('Company');
      render();
      expect(text('#cu-code-new')).toContain(i18n().t('cust.code.pattern.company'));
      expect(text('#cu-code-new')).toContain('Comp-');
      // A shape, never a predicted number: the next one is taken only when Save is pressed.
      expect(text('#cu-code-new')).not.toMatch(/(Cust|Comp)-\d/);
      expect(text('#cu-code-new')).not.toContain('Cust-11'); // not even the branch code half of one
    });

    it('describes the shape in words in every language', () => {
      for (const lang of ['fr', 'ar', 'en'] as BuiltinLang[]) {
        const shape = i18n().tIn(lang, 'cust.code.shape');
        expect(shape).toContain('Cust');
        expect(shape).toContain('Comp');
        // One dash: prefix, then the location's code, the year and month, the sequence.
        expect(i18n().tIn(lang, 'cust.code.pattern.person')).toMatch(/^Cust-[A-Z]{6}…$/);
        expect(i18n().tIn(lang, 'cust.code.pattern.company')).toMatch(/^Comp-[A-Z]{6}…$/);
      }
    });

    it('shows a stored code read-only when editing', async () => {
      const row = customer({ code: 'C001' });
      const c = await open(false, [row]);
      c.edit(row);
      render();
      expect(q('#cu-code')).toBeNull();
      expect(q('input[id^="cu-code"]')).toBeNull();
      expect(text('#cu-code-fixed')).toBe('C001');
      expect(q('#cu-code-fixed')?.classList).toContain('mono');
    });

    it('sends no code, on create or on update, and needs none to save', async () => {
      const c = await open();
      c.chooseKind('Person');
      Object.assign(c.form, { firstName: 'Karim', familyName: 'Benali' });
      expect(c.canSave()).toBeTrue();
      await c.save();
      expect('code' in sent(admin.createCustomer)).toBeFalse();

      const row = customer({ code: 'C001' });
      c.edit(row);
      c.form.phone = '0550 12 34 56';
      await c.save();
      expect('code' in sent(admin.updateCustomer)).toBeFalse();
    });

    it('creates at the Admin’s working branch, and says nothing of a branch on an update', async () => {
      const c = await open();
      spyOn(TestBed.inject(BranchScopeService), 'resolve').and.returnValue({ branchId: 'oran', label: 'branch' });
      c.chooseKind('Person');
      Object.assign(c.form, { firstName: 'Karim', familyName: 'Benali' });
      await c.save();
      expect(sent(admin.createCustomer).branchId).toBe('oran');

      c.edit(customer({ code: 'Cust-112610001' }));
      await c.save();
      expect('branchId' in sent(admin.updateCustomer)).toBeFalse();
    });

    it('names the drawn code once the save has answered, and the list shows it', async () => {
      const c = await open();
      const drawn = customer({ id: 'new-1', code: 'Cust-112610001' });
      page([drawn]);
      c.chooseKind('Person');
      Object.assign(c.form, { firstName: 'Karim', familyName: 'Benali' });
      await c.save();
      render();
      expect(c.notice()).toBe(i18n().t('cust.code.created', { code: isolate('Cust-112610001') }));
      expect(text(`td[data-label="${i18n().t('backoffice.customers.code')}"]`)).toBe('Cust-112610001');
    });

    it('asks the server for the page, with the search and the filters (owner, 2026-11-17)', async () => {
      const c = await open(false, [customer({ id: 'a', code: 'C001' })]);
      expect(admin.customersPage).toHaveBeenCalledWith(jasmine.objectContaining({ skip: 0, take: 10, q: '' }));
      c.query.set('comp-112610');
      c.balanceFilter.set('owes');
      fixture.detectChanges();
      await new Promise((resolve) => setTimeout(resolve, 350));
      expect(admin.customersPage).toHaveBeenCalledWith(
        jasmine.objectContaining({ skip: 0, take: 10, q: 'comp-112610', balance: 'owes', kind: 'all' }),
      );
    });
  });
});

describe('the store setting that asks for a tax number', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [BackofficeStoreComponent],
      providers: [
        { provide: AdminService, useValue: { getStore: () => Promise.resolve({ ...store(true), fiscalIds: {} }) } },
        { provide: PosService, useValue: { store: signal(null) } },
        { provide: StoreDocumentsApi, useValue: { get: () => Promise.reject(new Error('not admin')) } },
        { provide: AuthService, useValue: { currentUser: signal(null) } },
        { provide: SecureImageService, useValue: { src: () => Promise.resolve(null) } },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
  });

  it('says on the store screen that it applies to companies, with the profile’s own label', async () => {
    const fixture = TestBed.createComponent(BackofficeStoreComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const i18n = TestBed.inject(I18nService);
    const label = (fixture.nativeElement as HTMLElement).querySelector('label[for="reqTaxId"]')?.textContent?.trim();
    expect(label).toBe(i18n.t('backoffice.store.requireTaxId'));
    expect(label).toContain(i18n.t('backoffice.customers.taxId'));
    expect(label).not.toBe(i18n.t('backoffice.customers.taxId'));
  });
});
