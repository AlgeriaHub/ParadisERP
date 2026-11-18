import { AdminCustomer, Representative } from '../../core/admin.service';
import {
  kindLabelKey,
  kindProblem,
  needsRepresentative,
  representativeEndsOnSave,
  takeoverDayProblem,
  takeoverInstant,
} from './customer-kind';

/** The same cases as CustomerRulesTests on the server, from the screen's side. */
const rep: Representative = {
  id: 'rep-1',
  customerId: 'c-1',
  fullName: 'Karim Ben Ali',
  idDocumentNumber: '109 876 543',
  phone: '0550 11 22 33',
  note: 'gérant',
  fromUtc: '2026-01-15T09:00:00Z',
  toUtc: null,
  createdBy: 'seed',
  createdAtUtc: '2026-01-15T09:00:00Z',
};

const customer = (over: Partial<AdminCustomer> = {}): AdminCustomer => ({
  id: 'c-1',
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
  kind: 'Company',
  idDocumentNumber: null,
  representative: rep,
  ...over,
});

describe('kindLabelKey', () => {
  it('maps the three kinds to their words', () => {
    expect(kindLabelKey('Person')).toBe('cust.kind.person');
    expect(kindLabelKey('Company')).toBe('cust.kind.company');
    expect(kindLabelKey('Unspecified')).toBe('cust.kind.unset');
  });
});

describe('needsRepresentative', () => {
  it('asks for one when a company is created', () => {
    expect(needsRepresentative('Company', null)).toBeTrue();
  });

  it('asks for one on a company that has none on file (a migrated row)', () => {
    expect(needsRepresentative('Company', customer({ representative: null }))).toBeTrue();
  });

  it('asks for one when a person is turned into a company', () => {
    expect(needsRepresentative('Company', customer({ kind: 'Person', representative: null }))).toBeTrue();
  });

  it('never asks a person', () => {
    expect(needsRepresentative('Person', null)).toBeFalse();
    expect(needsRepresentative('Person', customer({ kind: 'Person', representative: null }))).toBeFalse();
  });

  it('never sends a second one while a current one exists — that is a change, not a save', () => {
    expect(needsRepresentative('Company', customer())).toBeFalse();
  });

  it('asks nothing while the kind is still unspecified', () => {
    expect(needsRepresentative('Unspecified', customer({ kind: 'Unspecified', representative: null }))).toBeFalse();
  });
});

describe('representativeEndsOnSave', () => {
  it('warns when a represented company becomes a person', () => {
    expect(representativeEndsOnSave('Person', customer())).toBeTrue();
  });

  it('has nothing to end on a company without one, on a new row, or when the kind stays', () => {
    expect(representativeEndsOnSave('Person', customer({ representative: null }))).toBeFalse();
    expect(representativeEndsOnSave('Person', null)).toBeFalse();
    expect(representativeEndsOnSave('Company', customer())).toBeFalse();
  });
});

describe('kindProblem', () => {
  it('requires a new customer to say what it is', () => {
    expect(kindProblem(true, 'Unspecified', 'Unspecified')).toBe('kindRequired');
    expect(kindProblem(true, 'Unspecified', 'Person')).toBeNull();
    expect(kindProblem(true, 'Unspecified', 'Company')).toBeNull();
  });

  it('lets an unspecified row be edited without deciding yet', () => {
    expect(kindProblem(false, 'Unspecified', 'Unspecified')).toBeNull();
  });

  it('never lets a kind go back to unspecified', () => {
    expect(kindProblem(false, 'Person', 'Unspecified')).toBe('kindCannotRevert');
    expect(kindProblem(false, 'Company', 'Unspecified')).toBe('kindCannotRevert');
  });

  it('allows switching between person and company', () => {
    expect(kindProblem(false, 'Person', 'Company')).toBeNull();
    expect(kindProblem(false, 'Company', 'Person')).toBeNull();
  });
});

describe('takeoverDayProblem', () => {
  const today = '2026-09-24';
  const since = '2026-01-15';

  it('accepts any day from the current start to today', () => {
    expect(takeoverDayProblem(since, since, today)).toBeNull();
    expect(takeoverDayProblem('2026-06-01', since, today)).toBeNull();
    expect(takeoverDayProblem(today, since, today)).toBeNull();
  });

  it('names a day before the current representative started', () => {
    expect(takeoverDayProblem('2026-01-14', since, today)).toBe('fromTooEarly');
  });

  it('names a day in the future', () => {
    expect(takeoverDayProblem('2026-09-25', since, today)).toBe('fromInFuture');
    expect(takeoverDayProblem('2026-09-25', undefined, today)).toBe('fromInFuture');
  });

  it('has no floor on a first appointment and nothing to say about an empty day', () => {
    expect(takeoverDayProblem('2020-01-01', undefined, today)).toBeNull();
    expect(takeoverDayProblem('', since, today)).toBeNull();
  });
});

describe('takeoverInstant', () => {
  const today = '2026-09-24';

  it('sends an earlier day as its start', () => {
    expect(takeoverInstant('2026-06-01', today, '2026-01-15T09:00:00Z')).toBe('2026-06-01T00:00:00Z');
  });

  it('sends today as now', () => {
    const before = Date.now();
    const at = Date.parse(takeoverInstant(today, today, '2026-01-15T09:00:00Z'));
    expect(at).toBeGreaterThanOrEqual(before);
    expect(at).toBeLessThanOrEqual(Date.now());
  });

  it('never starts before the current representative did on their own first day', () => {
    // Appointed at 14:32; the clerk records the next morning that the takeover was that same
    // day. Midnight would be before the appointment and refused; the appointment itself is not.
    expect(takeoverInstant('2026-09-20', today, '2026-09-20T14:32:11.1234567Z')).toBe('2026-09-20T14:32:11.1234567Z');
  });

  it('reads a start the server sent without a zone designator as UTC', () => {
    expect(takeoverInstant('2026-09-20', today, '2026-09-20T14:32:11.1234567')).toBe('2026-09-20T14:32:11.1234567Z');
    expect(takeoverInstant('2026-09-21', today, '2026-09-20T14:32:11.1234567')).toBe('2026-09-21T00:00:00Z');
  });

  it('has no floor on a first appointment', () => {
    expect(takeoverInstant('2020-01-01', today, null)).toBe('2020-01-01T00:00:00Z');
    expect(takeoverInstant('2020-01-01', today, undefined)).toBe('2020-01-01T00:00:00Z');
  });
});
