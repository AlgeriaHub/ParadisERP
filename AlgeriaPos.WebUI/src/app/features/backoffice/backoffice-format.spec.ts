import { formatDay } from './backoffice-format';

describe('backoffice-format formatDay', () => {
  it('formats dates consistently as dd/MM/yyyy with Latin numerals', () => {
    expect(formatDay('2026-05-04T12:00:00Z', 'ar', 'Africa/Algiers')).toBe('04/05/2026');
    expect(formatDay('2026-05-04T12:00:00Z', 'fr', 'Africa/Algiers')).toBe('04/05/2026');
    expect(formatDay('2026-10-15T12:00:00Z', 'ar', 'Africa/Algiers')).toBe('15/10/2026');
    expect(formatDay('2026-10-15T12:00:00Z', 'fr', 'Africa/Algiers')).toBe('15/10/2026');
    expect(formatDay('2026-10-15T12:00:00Z', 'en', 'Africa/Algiers')).toBe('15/10/2026');
  });

  it('handles null, undefined and invalid dates with an em-dash', () => {
    expect(formatDay(null, 'fr')).toBe('—');
    expect(formatDay(undefined, 'ar')).toBe('—');
    expect(formatDay('invalid-date', 'fr')).toBe('—');
  });
});
