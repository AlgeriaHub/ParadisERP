import { NAV } from './nav';
import { MODULE_TONES, TONE_CLASSES, ToneClass, moduleTone } from './nav-tones';

describe('module section tones', () => {
  it('assigns a known tone class to every module in NAV', () => {
    const navKeys = NAV.map((m) => m.key);
    expect(Object.keys(MODULE_TONES).sort()).toEqual([...navKeys].sort());
    for (const key of navKeys) {
      expect(TONE_CLASSES).toContain(MODULE_TONES[key]);
      expect(moduleTone(key)).toBe(MODULE_TONES[key]);
    }
  });

  it('pins the module → tone map', () => {
    const expected: Record<string, ToneClass> = {
      dashboard: 'tone-brand',
      pos: 'tone-brand',
      reports: 'tone-brand',
      sales: 'tone-sales',
      customers: 'tone-sales',
      rentals: 'tone-rentals',
      finance: 'tone-finance',
      inventory: 'tone-stock',
      manufacturing: 'tone-stock',
      maintenance: 'tone-stock',
      employees: 'tone-people',
      admin: 'tone-admin',
    };
    expect(MODULE_TONES).toEqual(expected);
  });

  it('falls back to tone-brand for missing or unknown module keys', () => {
    expect(moduleTone(undefined)).toBe('tone-brand');
    expect(moduleTone(null)).toBe('tone-brand');
    expect(moduleTone('')).toBe('tone-brand');
    expect(moduleTone('nope')).toBe('tone-brand');
  });
});
