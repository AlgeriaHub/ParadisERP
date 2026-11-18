import {
  accountWordKey,
  addDays,
  daysBetween,
  dueWord,
  exclusiveEnd,
  midday,
  parseCreditWarning,
  periodDays,
  spreadOldestFirst,
  taxByRate,
  toInputDate,
  withZ,
} from './receivable-view';

/**
 * The arithmetic every receivables screen leans on. Each of these mirrors a rule the server
 * enforces — the window length it refuses, the tax it charges, the sentence it warns with —
 * so a screen that says "too wide" or "5.69" before the request is saying what the server
 * would have said.
 */
describe('receivable-view dates', () => {
  it('counts a window inclusively, the way MaxPeriodDays does', () => {
    expect(daysBetween('2026-01-01', '2026-01-31')).toBe(30);
    expect(periodDays('2026-01-01', '2026-01-31')).toBe(31);
    expect(periodDays('2026-03-01', '2026-03-01')).toBe(1);
    expect(daysBetween('2026-03-01', '2026-02-27')).toBe(-2);
  });

  it('shows "to" inclusive but sends the day after as the exclusive end', () => {
    const end = exclusiveEnd('2026-02-28');
    expect(toInputDate(end)).toBe('2026-03-01');
    expect(end.getHours()).toBe(0);
  });

  it('adds days across month and year boundaries', () => {
    expect(addDays('2026-12-25', 30)).toBe('2027-01-24');
    expect(addDays('2026-03-01', -1)).toBe('2026-02-28');
  });

  it('sends a day as midday UTC so no zone moves it', () => {
    expect(midday('2026-05-10')).toBe('2026-05-10T12:00:00Z');
  });

  it('restores the Z that SQL Server drops and leaves a zoned instant alone', () => {
    expect(withZ('2026-05-10T09:00:00')).toBe('2026-05-10T09:00:00Z');
    expect(withZ('2026-05-10T09:00:00Z')).toBe('2026-05-10T09:00:00Z');
    expect(withZ('2026-05-10T09:00:00+01:00')).toBe('2026-05-10T09:00:00+01:00');
  });

  it('reads an instrument due date as a word', () => {
    // The helper is asked about UTC, so "today" must be the UTC day: the local day is already
    // tomorrow late in the evening east of Greenwich.
    const today = new Date().toISOString().slice(0, 10);
    expect(dueWord(null, 'UTC')).toBe('none');
    expect(dueWord('not a date', 'UTC')).toBe('none');
    expect(dueWord(midday(addDays(today, -1)), 'UTC')).toBe('overdue');
    expect(dueWord(midday(today), 'UTC')).toBe('today');
    expect(dueWord(midday(addDays(today, 7)), 'UTC')).toBe('soon');
    expect(dueWord(midday(addDays(today, 8)), 'UTC')).toBe('later');
  });
});

describe('receivable-view money', () => {
  it('charges tax per rate on the rate total, not per line', () => {
    const lines = [
      { lineNet: 9.99, taxRate: 19 },
      { lineNet: 9.99, taxRate: 19 },
      { lineNet: 9.99, taxRate: 19 },
    ];
    // 29.97 × 19 % = 5.6943 → 5.69; line by line it would be 3 × 1.90 = 5.70.
    expect(taxByRate(lines, true, 2)).toBe(5.69);
  });

  it('adds the rates up separately and rounds each', () => {
    const lines = [
      { lineNet: 100, taxRate: 19 },
      { lineNet: 100, taxRate: 9 },
      { lineNet: 50, taxRate: 0 },
    ];
    expect(taxByRate(lines, true, 2)).toBe(28);
  });

  it('reads every rate as zero on an untaxed document', () => {
    expect(taxByRate([{ lineNet: 100, taxRate: 19 }], false, 2)).toBe(0);
  });

  it('spreads a receipt oldest first, never above a row, until the amount runs out', () => {
    const rows = [
      { id: 'a', allocatable: 100 },
      { id: 'b', allocatable: 50.5 },
      { id: 'c', allocatable: 200 },
    ];
    expect(spreadOldestFirst(120, rows, 2)).toEqual({ a: 100, b: 20 });
    expect(spreadOldestFirst(400, rows, 2)).toEqual({ a: 100, b: 50.5, c: 200 });
    // The row that got nothing is absent, not zero: an empty result reads as "all on account".
    expect(spreadOldestFirst(0, rows, 2)).toEqual({});
    expect(spreadOldestFirst(null, rows, 2)).toEqual({});
  });

  it('rounds each slice so the sum never drifts past the amount', () => {
    const rows = [
      { id: 'a', allocatable: 0.1 },
      { id: 'b', allocatable: 0.2 },
      { id: 'c', allocatable: 10 },
    ];
    const spread = spreadOldestFirst(0.3, rows, 2);
    expect(spread).toEqual({ a: 0.1, b: 0.2 });
    expect(Object.values(spread).reduce((sum, v) => sum + v, 0)).toBeCloseTo(0.3, 2);
  });
});

describe('receivable-view words', () => {
  it('parses the two figures out of the server credit-limit sentence', () => {
    expect(parseCreditWarning('Customer credit limit exceeded (1234567.89 > 1000000).')).toEqual({
      balance: 1234567.89,
      limit: 1000000,
    });
    expect(parseCreditWarning('Customer credit limit exceeded.')).toBeNull();
    expect(parseCreditWarning(null)).toBeNull();
    expect(parseCreditWarning('')).toBeNull();
  });

  it('names the income accounts the rental module writes to and nothing else', () => {
    expect(accountWordKey('700')).toBe('inv.view.account.700');
    expect(accountWordKey('706')).toBe('rent.kind.Rent');
    expect(accountWordKey('708')).toBe('rent.kind.Overdue');
    expect(accountWordKey('757')).toBe('rent.kind.Damage');
    expect(accountWordKey('999')).toBeNull();
  });
});
