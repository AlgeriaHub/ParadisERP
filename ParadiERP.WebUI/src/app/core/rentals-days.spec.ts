import {
  chargedUntil,
  countsReturnDayByDefault,
  damagePriceFor,
  localTime,
  periodEndsOn,
  periodStartsOn,
  returnDayProblem,
  settleContract,
  signatoryIdentityMissing,
  transportMissing,
  zonedTimeToUtcIso,
} from './rentals';

/**
 * The delivery hour and the return day (answers 5 and 6, 2026-09-30), on the owner's own example,
 * with the same figures as `ParadiERP.Tests/Rentals/RentalDaysTests.cs`: one unit at 3,000 a month
 * is 100 a day, and the cutoff is noon. "The contract from 27 to 29 counts two days only, because
 * the day the goods come back is not counted (if they come back in the morning, and as the branch
 * manager decides)."
 */
const ALGIERS = 'Africa/Algiers';

function hire(durationDays: number, issued: string, returned: string, cutoff: number | null, decision?: boolean) {
  const [issueDay, issueTime] = issued.split('T');
  const [returnDay, returnTime] = returned.split('T');
  const issuedAt = localTime(zonedTimeToUtcIso(issueDay, issueTime, ALGIERS), ALGIERS);
  const returnedAt = localTime(zonedTimeToUtcIso(returnDay, returnTime, ALGIERS), ALGIERS);
  return settleContract({
    lines: [
      { id: 'l', qty: 1, durationDays, rentUnitPrice: 100, damageUnitPrice: 2000, overdueUnitPrice: 100, parentLineId: null },
    ],
    issues: [{ issueNoteId: 'i', lineId: 'l', issuedOn: periodStartsOn(issuedAt, cutoff), qty: 1 }],
    returns: [
      {
        returnNoteId: 'r',
        lineId: 'l',
        returnedOn: returnedAt.day,
        qtyOk: 1,
        qtyPartialDamage: 0,
        maintenanceValue: 0,
        qtyTotalDamage: 0,
        qtyMissing: 0,
        countsReturnDay: decision ?? countsReturnDayByDefault(returnedAt, cutoff),
      },
    ],
    extensions: [],
  });
}

const daysCharged = (s: ReturnType<typeof settleContract>) =>
  s.charges.filter((c) => c.kind === 'Rent' || c.kind === 'Overdue').reduce((sum, c) => sum + c.days, 0);

describe('the delivery hour and the return day (mirrors RentalDaysTests)', () => {
  it('reads the wall clock in the company zone and writes it back to the same instant', () => {
    // Algiers is UTC+1 all year: 15:00 there is 14:00 UTC.
    const utc = zonedTimeToUtcIso('2026-09-26', '15:00', ALGIERS);
    expect(utc).toBe('2026-09-26T14:00:00.000Z');
    expect(localTime(utc, ALGIERS)).toEqual({ day: '2026-09-26', hour: 15, minute: 0 });
    // After midnight locally is still the previous day in UTC.
    expect(localTime('2026-09-26T23:30:00Z', ALGIERS)).toEqual({ day: '2026-09-27', hour: 0, minute: 30 });
  });

  it('starts goods out after the cutoff on the next day', () => {
    expect(periodStartsOn({ day: '2026-09-26', hour: 15, minute: 0 }, 12)).toBe('2026-09-27');
    expect(periodStartsOn({ day: '2026-09-26', hour: 12, minute: 0 }, 12)).toBe('2026-09-27');
    expect(periodStartsOn({ day: '2026-09-26', hour: 11, minute: 59 }, 12)).toBe('2026-09-26');
    expect(periodStartsOn({ day: '2026-09-26', hour: 23, minute: 30 }, null)).toBe('2026-09-26');
  });

  it('dues a three-day hire from the 27th back by the morning of the 30th', () => {
    expect(periodEndsOn(periodStartsOn({ day: '2026-09-26', hour: 15, minute: 0 }, 12), 3)).toBe('2026-09-30');
  });

  it('charges two days for goods back on the 29th in the morning', () => {
    const s = hire(3, '2026-09-26T15:00', '2026-09-29T09:00', 12);
    expect(daysCharged(s)).toBe(2);
    expect(s.totals.Rent).toBe(200);
    expect(s.unusedRent).toBe(100);
  });

  it('charges three days for goods back on the 29th in the afternoon', () => {
    const s = hire(3, '2026-09-26T15:00', '2026-09-29T16:00', 12);
    expect(daysCharged(s)).toBe(3);
    expect(s.totals.Rent).toBe(300);
    expect(s.unusedRent).toBe(0);
  });

  it('charges two days again when the manager waives the afternoon, and asks why', () => {
    const s = hire(3, '2026-09-26T15:00', '2026-09-29T16:00', 12, false);
    expect(daysCharged(s)).toBe(2);
    expect(s.totals.Rent).toBe(200);
    expect(returnDayProblem(false, true, '')).toBe('returnDayNeedsReason');
    expect(returnDayProblem(false, true, '  ')).toBe('returnDayNeedsReason');
    expect(returnDayProblem(false, true, 'Client fidèle')).toBeNull();
    expect(returnDayProblem(null, true, null)).toBeNull();
    expect(returnDayProblem(true, true, null)).toBeNull();
  });

  it('turns a counted afternoon on a two-day hire into a day overdue', () => {
    const morning = hire(2, '2026-09-26T15:00', '2026-09-29T09:00', 12);
    expect(morning.totals.Overdue).toBe(0);
    const afternoon = hire(2, '2026-09-26T15:00', '2026-09-29T16:00', 12);
    expect(afternoon.totals.Rent).toBe(200);
    expect(afternoon.totals.Overdue).toBe(100);
  });

  it('changes nothing with no cutoff', () => {
    expect(countsReturnDayByDefault({ day: '2026-09-29', hour: 23, minute: 0 }, null)).toBe(false);
    expect(daysCharged(hire(3, '2026-09-26T15:00', '2026-09-29T16:00', null))).toBe(3);
    expect(chargedUntil('2026-09-29', false)).toBe('2026-09-29');
    expect(chargedUntil('2026-09-29', true)).toBe('2026-09-30');
  });
});

describe('the paperwork a note cannot go without (mirrors RentalPaperRulesTests)', () => {
  it('requires only the document and its number (owner, 2026-10-02)', () => {
    expect(signatoryIdentityMissing(false, true, {})).toEqual(['idType', 'idNumber']);
    expect(signatoryIdentityMissing(false, true, { idType: 'passport', idNumber: 'P1' })).toEqual([]);
    expect(
      signatoryIdentityMissing(false, true, {
        idType: 'passport',
        idNumber: 'P1',
        idIssuer: 'Consulat',
        idIssuedOn: '2024-01-01',
        birthDate: '1980-01-01',
      }),
    ).toEqual([]);
    expect(signatoryIdentityMissing(true, true, {})).toEqual([]);
    expect(signatoryIdentityMissing(false, false, {})).toEqual([]);
  });

  it('needs the driver, his phone and the truck on every note', () => {
    expect(transportMissing('', ' ', '')).toEqual(['driverName', 'driverPhone', 'truckNumber']);
    expect(transportMissing('Rachid', '0555', '01234-116-16')).toEqual([]);
  });

  it('starts a line from the item replacement value, and a typed price wins', () => {
    expect(damagePriceFor(0, 3500)).toBe(3500);
    expect(damagePriceFor(2000, 3500)).toBe(2000);
    expect(damagePriceFor(0, null)).toBe(0);
  });
});
