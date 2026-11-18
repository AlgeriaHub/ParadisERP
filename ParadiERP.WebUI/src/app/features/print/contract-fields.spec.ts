import { TestBed } from '@angular/core/testing';
import { AdminBranch, AdminCustomer, AdminStore } from '../../core/admin.service';
import { I18nService } from '../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../core/i18n/translations-backoffice';
import { DEFAULT_REGIONAL } from '../../core/regional.service';
import { RentalContract } from '../backoffice/rentals/rentals-api.service';
import {
  CONTRACT_FIELDS,
  ITEMS_TOKEN,
  MISSING_FIELD,
  companyIdField,
  contractFieldValues,
  contractFieldsFor,
  fieldToken,
  fillFields,
  insertToken,
  isolateField,
  splitAtItems,
} from './contract-fields';
import { PrintContext, buildPrintContext, hasNonLatinDigits } from './print-context';

/**
 * Fields inside a rental-contract wording (owner, 2026-10-08): `{customer}` in the text prints the
 * contract's own customer, in the document language, with its money and dates as the rest of the
 * paper writes them; a name nobody defined prints as typed, and a value the contract lacks a dash.
 */
describe('contract wording fields', () => {
  const store = {
    id: 's',
    nameFr: 'Béton SARL',
    nameAr: 'شركة الخرسانة',
    nameEn: 'Beton LLC',
    fiscalIds: {},
  } as unknown as AdminStore;
  const branch = { id: 'b1', nameFr: 'Agence Rouiba', nameAr: 'فرع الرويبة', nameEn: 'Rouiba branch' } as AdminBranch;
  const customer = { id: 'c1', code: 'Cust-112610001', fullName: 'Ben Ali BTP' } as AdminCustomer;

  const contract = (over: Partial<RentalContract> = {}): RentalContract =>
    ({
      id: 'k1',
      number: 'CTR-000012',
      customerId: 'c1',
      customerName: 'Ben Ali BTP',
      branchId: 'b1',
      signedAtUtc: '2026-10-05T10:00:00Z',
      lines: [],
      depositAmount: 50000,
      signatoryName: 'Karim Ben Ali',
      signatoryIdNumber: '109876543',
      siteName: 'Chantier Bab Ezzouar',
      ...over,
    }) as RentalContract;

  function context(lang: string): PrintContext {
    const i18n = TestBed.inject(I18nService);
    i18n.extend(BACKOFFICE_TRANSLATIONS);
    return buildPrintContext(i18n, DEFAULT_REGIONAL, lang, {
      store,
      documents: null,
      branches: [branch],
      customers: [customer],
      methods: [],
      branchId: 'b1',
    });
  }

  // The first eleven fields, the ones every contract has had since 2026-10-08.
  const allTokens = CONTRACT_FIELDS.slice(0, 11).map(fieldToken).join('|');

  it('prints every field as the contract\'s own value, each in its own direction', () => {
    const ctx = context('fr');
    const values = contractFieldValues(ctx, contract(), customer, 37500);
    const printed = fillFields(allTokens, values).split('|');
    expect(printed).toEqual(
      [
        'Agence Rouiba',
        'CTR-000012',
        ctx.day('2026-10-05T10:00:00Z'),
        'Ben Ali BTP',
        'Cust-112610001',
        'Karim Ben Ali',
        '109876543',
        'Agence Rouiba',
        'Chantier Bab Ezzouar',
        ctx.money(50000),
        ctx.money(37500),
      ].map(isolateField),
    );
  });

  it('names the company and the branch in the document language', () => {
    const values = contractFieldValues(context('ar'), contract(), customer, 0);
    expect(values.company).toBe('فرع الرويبة');
    expect(values.branch).toBe('فرع الرويبة');
    // The customer's name is the one frozen on the contract, whatever the row says now.
    expect(contractFieldValues(context('ar'), contract({ customerName: 'Ben Ali BTP SPA' }), customer, 0).customer).toBe('Ben Ali BTP SPA');
  });

  it('writes the money and the date in Latin digits on an Arabic contract', () => {
    const ctx = context('ar');
    const text = fillFields('الضمان {deposit} — الإيجار {rentTotal} — بتاريخ {signedOn}', contractFieldValues(ctx, contract(), customer, 37500));
    expect(hasNonLatinDigits(text)).withContext(text).toBeFalse();
    // The figures, and the currency in the document's own language, through the regional service.
    expect(text).toContain(ctx.money(50000));
    expect(text).toContain(DEFAULT_REGIONAL.currencySymbols['ar']!);
    expect(text).toMatch(/50[.,\s  ]?000/);
    expect(text).toContain('2026');
  });

  it('prints a value the contract does not have as a dash, never blank and never the token', () => {
    const bare = contract({
      customerName: null,
      depositAmount: null,
      signatoryName: null,
      signatoryIdNumber: null,
      siteName: null,
      branchId: 'not-known-here',
    });
    const values = contractFieldValues(context('fr'), bare, null, 0);
    const text = fillFields('{customer}|{customerCode}|{deposit}|{signatory}|{signatoryId}|{site}|{branch}', values);
    expect(text).toBe(Array(7).fill(MISSING_FIELD).join('|'));
    // A deposit of zero is no deposit, as on the deposit line: "0.00" would read as one taken and lost.
    expect(contractFieldValues(context('fr'), contract({ depositAmount: 0 }), customer, 0).deposit).toBe('');
  });

  it('keeps a name it does not know, and anything that is not a field, exactly as typed', () => {
    const values = contractFieldValues(context('fr'), contract(), customer, 0);
    expect(fillFields('{client} {Customer} { customer } {} {1x} {toString} {customer}', values)).toBe(
      `{client} {Customer} { customer } {} {1x} {toString} ${isolateField('Ben Ali BTP')}`,
    );
    expect(fillFields('Aucun champ ici.', values)).toBe('Aucun champ ici.');
    expect(fillFields('', values)).toBe('');
  });

  it('offers every field in all three dictionaries', () => {
    for (const lang of ['fr', 'ar', 'en'] as const) {
      for (const field of CONTRACT_FIELDS) {
        expect(BACKOFFICE_TRANSLATIONS[lang]['store.docs.fields.' + field]).withContext(`${lang} ${field}`).toBeTruthy();
      }
    }
  });

  describe('inserting a field into the text', () => {
    it('puts the token at the cursor and the cursor after it', () => {
      expect(insertToken('Entre  et la société.', 6, 6, '{customer}', 4000)).toEqual({
        text: 'Entre {customer} et la société.',
        caret: 16,
      });
    });

    it('replaces what was selected', () => {
      expect(insertToken('Le client X signe.', 10, 11, '{customer}', 4000)?.text).toBe('Le client {customer} signe.');
    });

    it('appends when the box never had a cursor, and keeps a stray position inside the text', () => {
      expect(insertToken('Fin', null, null, '{deposit}', 4000)).toEqual({ text: 'Fin{deposit}', caret: 12 });
      expect(insertToken('Fin', 99, -4, '{site}', 4000)?.text).toBe('Fin{site}');
    });

    it('refuses a field the text has no room left for', () => {
      expect(insertToken('x'.repeat(3995), 0, 0, '{customer}', 4000)).toBeNull();
      expect(insertToken('x'.repeat(3990), 0, 0, '{customer}', 4000)?.text.length).toBe(4000);
    });
  });

  describe("the company's own contract (2026-10-10)", () => {
    const full = () =>
      contract({
        signatoryBirthDate: '1996-08-04',
        signatoryIdType: 'drivingLicence',
        signatoryIdIssuer: 'Ouled Moussa',
        signatoryIdIssuedOn: '2017-07-09',
        truckType: 'Plateau',
        truckNumber: '12345-116-35',
        truckOwnerPhone: '0560474195',
        siteAddress: 'El Mouileha, Ouled Moussa',
      });
    const period = { startDay: '2026-09-30', endDay: '2026-10-01', durationDays: 1, damageTotal: 30000 };

    it('fills the identity, the lorry, the site, the value of the goods and the period', () => {
      const ctx = context('ar');
      const withAddress = { ...customer, address: 'Rue Ibn Khaldoun', phone: '0560474195' };
      const values = contractFieldValues(ctx, full(), withAddress, 600, period);
      expect(values['customerAddress']).toBe('Rue Ibn Khaldoun');
      expect(values['customerPhone']).toBe('0560474195');
      expect(values['signatoryIdType']).toBe(ctx.t('rent.id.kind.drivingLicence'));
      expect(values['signatoryIdIssuer']).toBe('Ouled Moussa');
      expect(values['signatoryBirthDate']).toContain('1996');
      expect(values['signatoryIdIssuedOn']).toContain('2017');
      expect(values['truckNumber']).toBe('12345-116-35');
      expect(values['siteAddress']).toBe('El Mouileha, Ouled Moussa');
      expect(values['damageTotal']).toBe(ctx.money(30000));
      expect(values['startDate']).toContain('2026');
      expect(values['endDate']).toContain('2026');
      expect(values['durationDays']).toBe('1');
      const text = fillFields('من {startDate} إلى {endDate} — {damageTotal}', values);
      expect(hasNonLatinDigits(text)).withContext(text).toBeFalse();
    });

    it("names the company's fiscal identifiers after the profile's keys", () => {
      const fields = contractFieldsFor(DEFAULT_REGIONAL);
      for (const id of DEFAULT_REGIONAL.fiscalIds) expect(fields).toContain(companyIdField(id.key));
      expect(companyIdField('nif')).toBe('companyNif');
      // The company's own value, under the field of its key.
      const withIds = { ...store, fiscalIds: { [DEFAULT_REGIONAL.fiscalIds[0].key]: '002335072992232' } } as unknown as AdminStore;
      const ctx = { ...context('fr'), store: withIds };
      const values = contractFieldValues(ctx, contract(), customer, 0, null);
      expect(values[companyIdField(DEFAULT_REGIONAL.fiscalIds[0].key)]).toBe('002335072992232');
    });

    it('takes the truck and the driver from the first issue note when the contract names none', () => {
      const ctx = context('ar');
      const transport = { truckNumber: 'ر ب ن 1446', driverName: 'يمناوي بن زايد العمري', driverPhone: '0530530536' };
      const values = contractFieldValues(ctx, contract({ truckNumber: null }), customer, 0, null, transport);
      expect(values['truckNumber']).toBe('ر ب ن 1446');
      expect(values['driverName']).toBe('يمناوي بن زايد العمري');
      expect(values['driverPhone']).toBe('0530530536');
      // The contract's own truck wins over the note's.
      expect(contractFieldValues(ctx, contract({ truckNumber: '12345-116-35' }), customer, 0, null, transport)['truckNumber']).toBe('12345-116-35');
      // No note in a lorry: a dash in the text, as for any value the contract lacks.
      expect(fillFields('{driverName}', contractFieldValues(ctx, contract(), customer, 0))).toBe(MISSING_FIELD);
    });

    it('splits the wording at the items table, once', () => {
      expect(splitAtItems('Sans tableau.')).toBeNull();
      expect(splitAtItems(`Avant\n${ITEMS_TOKEN}\nAprès`)).toEqual({ before: 'Avant', after: 'Après' });
      expect(splitAtItems(`${ITEMS_TOKEN} fin ${ITEMS_TOKEN}`)).toEqual({ before: '', after: 'fin ' });
    });

    it('labels every new field in all three dictionaries', () => {
      for (const lang of ['fr', 'ar', 'en'] as const) {
        expect(BACKOFFICE_TRANSLATIONS[lang]['store.docs.fields.companyId']).withContext(lang).toContain('{code}');
        expect(BACKOFFICE_TRANSLATIONS[lang]['store.docs.fields.itemsHint']).withContext(lang).toBeTruthy();
      }
    });
  });
});
