import { parseInstant, utcIso } from './instant';

/**
 * A moment from the API is UTC, with or without the Z that says so. Until 2026-10-13 times read
 * back from the database came without it, and a screen took them for the browser's own clock.
 */
describe('utcIso', () => {
  it('gives a date and time without a zone its Z', () => {
    expect(utcIso('2026-10-13T22:12:00')).toBe('2026-10-13T22:12:00Z');
    expect(utcIso('2026-10-13T22:12')).toBe('2026-10-13T22:12Z');
    expect(utcIso('2026-10-13T22:12:00.1234567')).toBe('2026-10-13T22:12:00.1234567Z');
  });

  it('leaves a Z, an offset, a date alone and anything else as they are', () => {
    for (const value of [
      '2026-10-13T22:12:00Z',
      '2026-10-13T22:12:00.123Z',
      '2026-10-13T23:12:00+01:00',
      '2026-10-13T19:12:00-0300',
      '2026-10-13',
      '',
      'not a date',
    ]) {
      expect(utcIso(value)).withContext(value).toBe(value);
    }
  });
});

describe('parseInstant', () => {
  it('reads a zone-less value as the UTC moment, whatever the browser clock', () => {
    expect(parseInstant('2026-10-13T22:12:00').toISOString()).toBe('2026-10-13T22:12:00.000Z');
    expect(parseInstant('2026-10-13T22:12:00.1234567').toISOString()).toBe('2026-10-13T22:12:00.123Z');
  });

  it('takes a Z or an offset at its word and keeps a Date as it is', () => {
    expect(parseInstant('2026-10-13T22:12:00Z').toISOString()).toBe('2026-10-13T22:12:00.000Z');
    expect(parseInstant('2026-10-13T23:12:00+01:00').toISOString()).toBe('2026-10-13T22:12:00.000Z');
    const date = new Date(Date.UTC(2026, 9, 13, 22, 12));
    expect(parseInstant(date)).toBe(date);
  });
});
