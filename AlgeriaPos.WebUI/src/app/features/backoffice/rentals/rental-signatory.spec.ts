import { AdminCustomer, Representative } from '../../../core/admin.service';
import {
  IdentifiedCustomer,
  IdentifiedRepresentative,
  fromRecord,
  identityMissing,
  representativeChoices,
  signatoryHintKey,
  signatoryMode,
  signatoryPrefill,
  signatoryState,
  signedByKey,
} from './rental-signatory';

function customer(over: Partial<IdentifiedCustomer>): IdentifiedCustomer {
  return {
    id: 'c1',
    code: 'C001',
    fullName: 'SARL El Injaz Travaux',
    phone: null,
    address: null,
    taxId: null,
    email: null,
    birthDate: null,
    creditLimit: 0,
    balance: 0,
    loyaltyPoints: 0,
    version: 1,
    kind: 'Unspecified',
    idDocumentNumber: null,
    representative: null,
    ...over,
  } as IdentifiedCustomer;
}

const NADIA: IdentifiedRepresentative = {
  id: 'r1',
  customerId: 'c1',
  fullName: 'Nadia Bouzid',
  idDocumentNumber: '204 118 337',
  idDocumentType: 'nationalCard',
  idDocumentIssuer: 'Commune de Kouba',
  idDocumentIssuedOn: '2021-02-03',
  phone: null,
  note: 'gérante',
  fromUtc: '2026-01-01T00:00:00Z',
  toUtc: null,
  createdBy: 'seed',
  createdAtUtc: '2026-01-01T00:00:00Z',
};

const SAMIR: Representative = {
  ...NADIA,
  id: 'r0',
  fullName: 'Samir Haddad',
  idDocumentNumber: '090 000 111',
  fromUtc: '2023-01-01T00:00:00Z',
  toUtc: '2026-01-01T00:00:00Z',
};

describe('signatoryMode', () => {
  it('lets nobody sign for a customer: a person signs, a company picks a representative', () => {
    expect(signatoryMode(customer({ kind: 'Person' }))).toBe('person');
    expect(signatoryMode(customer({ kind: 'Company' }))).toBe('company');
    // Only a customer whose kind is not set leaves the name typed — there is nothing to derive it from.
    expect(signatoryMode(customer({ kind: 'Unspecified' }))).toBe('free');
    expect(signatoryMode(undefined)).toBeNull();
  });
});

describe('signatoryPrefill', () => {
  it('fills a person in with himself, identity included', () => {
    expect(
      signatoryPrefill(
        customer({
          kind: 'Person',
          fullName: 'Karim Ben Ali',
          idDocumentNumber: '109 876 543',
          idDocumentType: 'drivingLicence',
          idDocumentIssuer: 'Daïra de Bab El Oued',
          idDocumentIssuedOn: '2019-05-12',
          birthDate: '1980-03-04T00:00:00',
        }),
      ),
    ).toEqual({
      name: 'Karim Ben Ali',
      idNumber: '109 876 543',
      idType: 'drivingLicence',
      idIssuer: 'Daïra de Bab El Oued',
      idIssuedOn: '2019-05-12',
      birthDate: '1980-03-04',
    });
  });

  it('fills a company in with its current representative, not its legal name, and never a birth date', () => {
    expect(signatoryPrefill(customer({ kind: 'Company', representative: NADIA }))).toEqual({
      name: 'Nadia Bouzid',
      idNumber: '204 118 337',
      idType: 'nationalCard',
      idIssuer: 'Commune de Kouba',
      idIssuedOn: '2021-02-03',
      birthDate: '',
    });
  });

  it('fills a company in with the representative chosen from its history', () => {
    expect(signatoryPrefill(customer({ kind: 'Company', representative: NADIA }), SAMIR).name).toBe('Samir Haddad');
  });

  it('leaves everything blank when the company has nobody on file, or the kind is not set', () => {
    expect(signatoryPrefill(customer({ kind: 'Company' })).name).toBe('');
    expect(signatoryPrefill(customer({ kind: 'Unspecified', fullName: 'Someone' })).name).toBe('');
    expect(signatoryPrefill(undefined).idNumber).toBe('');
  });

  it('turns a missing field into an empty one, not the word null', () => {
    const blank = signatoryPrefill(customer({ kind: 'Person', idDocumentNumber: null }));
    expect(blank.idNumber).toBe('');
    expect(blank.idIssuedOn).toBe('');
    expect(blank.birthDate).toBe('');
  });
});

describe('fromRecord', () => {
  it('keeps every field the record holds out of the clerk’s hands, and leaves the rest to type', () => {
    const person = signatoryPrefill(customer({ kind: 'Person', fullName: 'Karim', idDocumentNumber: '1' }));
    expect([...fromRecord('person', person)].sort()).toEqual(['idNumber', 'name']);
    const company = signatoryPrefill(customer({ kind: 'Company', representative: NADIA }));
    expect(fromRecord('company', company).has('birthDate')).toBe(false);
    expect(fromRecord('company', company).has('idIssuer')).toBe(true);
    // Free text is free.
    expect(fromRecord('free', person).size).toBe(0);
  });
});

describe('representativeChoices', () => {
  it('puts the current representative first, then the former ones newest first, one per name', () => {
    const again = { ...SAMIR, id: 'r-1', fromUtc: '2022-01-01T00:00:00Z' };
    expect(representativeChoices([again, SAMIR, NADIA]).map((r) => r.id)).toEqual(['r1', 'r0']);
  });
});

describe('signatoryHintKey', () => {
  it('says nothing until a customer is chosen', () => {
    expect(signatoryHintKey(undefined)).toBeNull();
  });

  it('names the four cases and warns only where nothing could be prefilled', () => {
    expect(signatoryHintKey(customer({ kind: 'Person' }))).toEqual({ key: 'rent.sign.hint.person', warn: false });
    expect(signatoryHintKey(customer({ kind: 'Company', representative: NADIA }))).toEqual({
      key: 'rent.sign.hint.company',
      warn: false,
    });
    expect(signatoryHintKey(customer({ kind: 'Company' }))).toEqual({ key: 'rent.sign.hint.companyNone', warn: true });
    expect(signatoryHintKey(customer({ kind: 'Unspecified' }) as AdminCustomer)).toEqual({ key: 'rent.sign.hint.unset', warn: true });
  });
});

describe('signatoryState', () => {
  it('is signed as soon as a name is present', () => {
    expect(signatoryState({ signatoryName: 'Nadia Bouzid', signatoryRequired: true, isLocked: false })).toBe('signed');
    expect(signatoryState({ signatoryName: 'Nadia Bouzid', signatoryRequired: false, isLocked: false })).toBe('signed');
  });

  it('is missing on a new draft left blank — the first issue note will be refused', () => {
    expect(signatoryState({ signatoryName: '   ', signatoryRequired: true, isLocked: false })).toBe('missing');
  });

  it('names an old contract for what it is rather than flagging it', () => {
    expect(signatoryState({ signatoryName: null, signatoryRequired: false, isLocked: true })).toBe('predates');
  });

  it('is never missing once locked: the rule bit at the first note and bites no more', () => {
    expect(signatoryState({ signatoryName: null, signatoryRequired: true, isLocked: true })).toBe('unrecorded');
  });
});

describe('identityMissing', () => {
  it('names what the first issue note would be refused for, and nothing once locked or before the rule', () => {
    expect(identityMissing({ isLocked: false, signatoryRequired: true, signatoryIdNumber: '1' })).toEqual(['idType']);
    expect(identityMissing({ isLocked: true, signatoryRequired: true })).toEqual([]);
    expect(identityMissing({ isLocked: false, signatoryRequired: false })).toEqual([]);
  });

  it('asks nothing of a person who signs himself, and asks his agent (owner, 2026-10-06)', () => {
    const blank = { isLocked: false, signatoryRequired: true, signatoryName: 'Karim' };
    expect(identityMissing({ ...blank, customerKind: 'Person' })).toEqual([]);
    expect(identityMissing({ ...blank, customerKind: 'Person', signedByAgent: true })).toEqual(['idType', 'idNumber']);
    expect(identityMissing({ ...blank, customerKind: 'Company' })).toEqual(['idType', 'idNumber']);
  });
});

describe('signedByKey', () => {
  it('says "for the company" only when the debtor is a company', () => {
    expect(signedByKey({ customerKind: 'Company' })).toBe('rent.sign.signedByFor');
    expect(signedByKey({ customerKind: 'Person' })).toBe('rent.sign.signedBy');
    expect(signedByKey({ customerKind: 'Unspecified' })).toBe('rent.sign.signedBy');
  });

  it('says "as the customer’s agent" when a person’s agent signed', () => {
    expect(signedByKey({ customerKind: 'Person', signedByAgent: true })).toBe('rent.sign.signedByAgent');
    expect(signedByKey({ customerKind: 'Person', signedByAgent: false })).toBe('rent.sign.signedBy');
  });
});
