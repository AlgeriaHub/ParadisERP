import { AdminCustomer, AdminStore } from '../../core/admin.service';
import { I18nService } from '../../core/i18n.service';
import { RegionalService, DEFAULT_REGIONAL } from '../../core/regional.service';
import {
  buildPrintContext,
  documentBranchId,
  fiscalIdLines,
  hasNonLatinDigits,
  isolate,
  logoSource,
  partyLines,
  utc,
} from './print-context';

/**
 * The frame's own rules: a fiscal identifier prints with its code only when it has a value; a
 * document set to Arabic is right-to-left whatever the interface says; every figure is in Latin
 * digits; the buyer block prefers what the invoice froze over what the customer row says now.
 */
function fakeI18n(uiLang: string): I18nService {
  const regional = new RegionalService();
  // A tiny stand-in for the service: the formatters the context needs, over the default profile.
  const i18n = {
    lang: () => uiLang,
    isRtlLang: (l: string) => l === 'ar',
    tIn: (_l: string, key: string, p?: Record<string, string | number>) => (p ? `${key}:${JSON.stringify(p)}` : key),
    textIn: (l: string, v: Record<string, string | undefined> | undefined) => v?.[l] ?? v?.['fr'] ?? '',
    moneyIn: (l: string, v: number) => new Intl.NumberFormat(l, { numberingSystem: 'latn', minimumFractionDigits: 2 }).format(v),
    dateTimeIn: (l: string, v: string) => new Intl.DateTimeFormat(l, { numberingSystem: 'latn', dateStyle: 'short', timeStyle: 'short', timeZone: 'UTC' }).format(new Date(v)),
    rateLabelIn: (l: string, r: number) => regional.taxRateLabel(r, l),
    langs: () => [{ code: 'fr', label: 'Français' }, { code: 'ar', label: 'العربية' }],
  } as unknown as I18nService;
  return i18n;
}

const store: AdminStore = {
  id: 's',
  uiFont: null,
  nameAr: 'شركة الخرسانة',
  nameFr: 'Béton SARL',
  nameEn: '',
  address: '12 rue X',
  phone: '021 00 00 00',
  fiscalIds: { nif: '000116001234567', rc: ' 16/00-123 ', nis: '' },
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
  version: 1,
};

const customer = {
  id: 'c',
  code: 'C001',
  fullName: 'Ben Ali BTP SPA',
  phone: '0550 00 00 00',
  address: 'Rouiba',
  taxId: '999',
} as AdminCustomer;

const data = { store, documents: null, branches: [], customers: [customer], methods: [] };

describe('print context', () => {
  it('prints every fiscal identifier that has a value, with its code, and omits the empty ones', () => {
    expect(fiscalIdLines(DEFAULT_REGIONAL, store)).toEqual([
      { label: 'NIF', value: '000116001234567' },
      { label: 'RC', value: '16/00-123' },
    ]);
    expect(fiscalIdLines(DEFAULT_REGIONAL, null)).toEqual([]);
  });

  it("prints a branch's own identifier where it has one and the company's where it does not, key by key", () => {
    // Skikda has its own register and NIF; its NIS field is blank and its article number absent.
    const skikda = { fiscalIds: { rc: ' 21/06-0729922B23 ', nif: '001621060729922', nis: '  ' } };
    const lines = fiscalIdLines(DEFAULT_REGIONAL, store, skikda);
    expect(lines).toContain({ label: 'NIF', value: '001621060729922' });
    expect(lines).toContain({ label: 'RC', value: '21/06-0729922B23' });
    // The company has no NIS either, so it is omitted rather than printed empty.
    expect(lines.map((l) => l.label)).not.toContain('NIS');
    expect(lines.length).toBe(2);

    // A branch that filled in only its register keeps the company's NIF.
    expect(fiscalIdLines(DEFAULT_REGIONAL, store, { fiscalIds: { rc: '35/00-0729922B23' } })).toEqual([
      { label: 'NIF', value: '000116001234567' },
      { label: 'RC', value: '35/00-0729922B23' },
    ]);
    // A branch with nothing, or none at all, prints the company's exactly as before.
    expect(fiscalIdLines(DEFAULT_REGIONAL, store, { fiscalIds: null })).toEqual(fiscalIdLines(DEFAULT_REGIONAL, store));
    expect(fiscalIdLines(DEFAULT_REGIONAL, store, null)).toEqual(fiscalIdLines(DEFAULT_REGIONAL, store));
    // The branch alone, when the company row is not readable.
    expect(fiscalIdLines(DEFAULT_REGIONAL, null, skikda).length).toBe(2);
  });

  it("finds the document's branch on whichever record of it carries one", () => {
    expect(documentBranchId({ kind: 'invoice', invoice: { branchId: 'b1', number: 'INV-1' } })).toBe('b1');
    // An issue note travels with its contract's documents, which carry no branch at the top.
    expect(
      documentBranchId({ kind: 'issue-note', docs: { contract: { branchId: 'b0' }, issueNotes: [] }, note: { branchId: 'b2' }, products: [] }),
    ).toBe('b2');
    expect(documentBranchId({ kind: 'statement', statement: { customerId: 'c' }, ageing: null, asOf: null })).toBeNull();
    expect(documentBranchId(null)).toBeNull();

    // A stock transfer is the sending branch's paper.
    expect(documentBranchId({ kind: 'stock-transfer', transfer: { fromBranchId: 'b3', toBranchId: 'b4' } })).toBe('b3');
    expect(buildPrintContext(fakeI18n('fr'), DEFAULT_REGIONAL, 'fr', data).branchId).toBeNull();
    expect(buildPrintContext(fakeI18n('fr'), DEFAULT_REGIONAL, 'fr', { ...data, branchId: 'b2' }).branchId).toBe('b2');
  });

  it('prints the logo only when it is inline: the page turns the signed-in link into one first', () => {
    const documents = { documentTexts: {}, email: null, website: null, bankDetails: null, changesPerYear: 3 };
    expect(logoSource(null)).toBe('');
    expect(logoSource(documents)).toBe('');
    expect(logoSource({ ...documents, logoUrl: null })).toBe('');
    expect(logoSource({ ...documents, logoUrl: 'data:image/png;base64,iVBORw0KGgo=' })).toBe('data:image/png;base64,iVBORw0KGgo=');
    expect(logoSource({ ...documents, logoUrl: '/api/catalog/store/logo?v=3f9a1c2b7d4e' })).toBe('');
    expect(logoSource({ ...documents, logoUrl: 'https://example.com/logo.png' })).toBe('');
  });

  it('is right-to-left for an Arabic document while the interface stays French, and the reverse', () => {
    expect(buildPrintContext(fakeI18n('fr'), DEFAULT_REGIONAL, 'ar', data).dir).toBe('rtl');
    expect(buildPrintContext(fakeI18n('ar'), DEFAULT_REGIONAL, 'fr', data).dir).toBe('ltr');
  });

  it('writes every figure in Latin digits in Arabic', () => {
    const ctx = buildPrintContext(fakeI18n('ar'), DEFAULT_REGIONAL, 'ar', data);
    for (const text of [ctx.money(1234.5), ctx.qty(12.5), ctx.day('2026-09-24T10:00:00Z'), ctx.dateTime('2026-09-24T10:00:00')]) {
      expect(hasNonLatinDigits(text)).withContext(text).toBeFalse();
    }
    expect(ctx.words(12)).toBe('اثنا عشر دينار');
  });

  it('prefers the buyer block the invoice froze over the customer row, and fills only what was not recorded', () => {
    const ctx = buildPrintContext(fakeI18n('fr'), DEFAULT_REGIONAL, 'fr', data);
    const lines = partyLines(ctx, customer, { name: 'Ben Ali BTP', address: '12 rue Didouche Mourad', taxId: '000116001234567' });
    expect(lines.map((l) => l.value)).toEqual(['Ben Ali BTP', 'C001', '12 rue Didouche Mourad', '0550 00 00 00', '000116001234567']);
    // Nothing frozen (a pre-backfill row with no address): the row fills in.
    expect(partyLines(ctx, customer, { name: 'Ben Ali BTP', address: null, taxId: null }).map((l) => l.value)).toEqual([
      'Ben Ali BTP',
      'C001',
      'Rouiba',
      '0550 00 00 00',
      '999',
    ]);
    // The tax id line carries the profile's own label, never a hardcoded NIF.
    expect(lines[4].label).toBe(DEFAULT_REGIONAL.customerTaxIdLabels['fr'] ?? '');
  });

  it('sets phones, codes and tax ids left to right so an Arabic page keeps their digits in order', () => {
    const ctx = buildPrintContext(fakeI18n('ar'), DEFAULT_REGIONAL, 'ar', data);
    const lines = partyLines(ctx, customer, { name: 'Ben Ali BTP', address: '12 rue Didouche Mourad', taxId: '000116001234567' });
    // Name and address follow the page; code, phone and tax id do not.
    expect(lines.map((l) => !!l.ltr)).toEqual([false, true, false, true, true]);
    // Inside a sentence the number is isolated, so "رقم 109 876 543" never reads "543 876 109".
    expect(isolate('109 876 543')).toBe('\u2066109 876 543\u2069');
  });

  it('reads a SQL Server timestamp without its Z as UTC', () => {
    expect(utc('2026-09-24T10:00:00')).toBe('2026-09-24T10:00:00Z');
    expect(utc('2026-09-24T10:00:00Z')).toBe('2026-09-24T10:00:00Z');
    expect(utc('2026-09-24T10:00:00+01:00')).toBe('2026-09-24T10:00:00+01:00');
  });

  it('sets the title again in the other title languages, never the document language twice', () => {
    const documents = { documentTexts: {}, email: null, website: null, bankDetails: null, changesPerYear: 3 };
    // Arabic and French ticked, English too though this company has not enabled it.
    const both = { ...data, documents: { ...documents, titleLanguages: ['ar', 'fr', 'en'] } };

    const french = buildPrintContext(fakeI18n('fr'), DEFAULT_REGIONAL, 'fr', both).alsoTitled('rent.returnNote');
    expect(french).toEqual([{ lang: 'ar', dir: 'rtl', text: 'rent.returnNote' }]);

    const arabic = buildPrintContext(fakeI18n('ar'), DEFAULT_REGIONAL, 'ar', both).alsoTitled('rent.returnNote');
    expect(arabic).toEqual([{ lang: 'fr', dir: 'ltr', text: 'rent.returnNote' }]);

    // Nothing ticked: the document language alone, as before the setting existed.
    expect(buildPrintContext(fakeI18n('fr'), DEFAULT_REGIONAL, 'fr', { ...data, documents }).alsoTitled('x')).toEqual([]);
    expect(buildPrintContext(fakeI18n('fr'), DEFAULT_REGIONAL, 'fr', data).alsoTitled('x')).toEqual([]);
  });
});
