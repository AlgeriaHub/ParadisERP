import { BACKOFFICE_TRANSLATIONS } from './i18n/translations-backoffice';
import { TestBed } from '@angular/core/testing';
import { PhoneNumberRules, formatPhone, normalizePhone, phoneExample, phoneNeedsFixing } from './phone';
import { DEFAULT_REGIONAL, RegionalService } from './regional.service';

/** The Algeria preset's rules, as the server sends them (RegionalPresets.cs). */
const ALGERIA: PhoneNumberRules = {
  callingCode: '213',
  trunkPrefix: '0',
  mobilePattern: '^[567][0-9]{8}$',
  displayGroups: [3, 2, 2, 2],
  example: '0555123456',
};

/**
 * "Phone must be saved as mobile format", with the country code (owner, 2026-11-17). Mirrors
 * PhoneNumbersTests.cs: the screen refuses what the server refuses and shows what it stores.
 */
describe('phone', () => {
  it('reads every way of typing an Algerian mobile as the same stored number', () => {
    for (const typed of ['06.58.41.77.30', '0658417730', '06 58 41 77 30', '658417730', '+213 658 41 77 30', '00213658417730', '213658417730', '+213 0658417730']) {
      expect(normalizePhone(typed, ALGERIA)).withContext(typed).toEqual({ value: '+213658417730', isMobile: true });
    }
  });

  it('refuses what is not a mobile and keeps it as typed', () => {
    for (const typed of ['021 23 45 67', '041582207', '065841773', '0555 11 22 33 / 0666 44 55 66', '+21321234567']) {
      expect(normalizePhone(typed, ALGERIA)).withContext(typed).toEqual({ value: typed, isMobile: false });
    }
  });

  it('takes a number with another country code as given, and none as none', () => {
    expect(normalizePhone('+33 6 12 34 56 78', ALGERIA)).toEqual({ value: '+33612345678', isMobile: true });
    expect(normalizePhone('  ', ALGERIA)).toEqual({ value: null, isMobile: true });
    expect(normalizePhone('021 23 45 67', undefined)).toEqual({ value: '021 23 45 67', isMobile: true });
  });

  it('shows a stored mobile grouped for reading, and anything else as it is', () => {
    expect(formatPhone('+213658417730', ALGERIA)).toBe('+213 658 41 77 30');
    expect(formatPhone('+33612345678', ALGERIA)).toBe('+33612345678');
    expect(formatPhone('021 23 45 67', ALGERIA)).toBe('021 23 45 67');
    // Read back, the grouped form is the same stored number.
    expect(normalizePhone(formatPhone('+213658417730', ALGERIA), ALGERIA).value).toBe('+213658417730');
  });

  it('flags a stored phone a save would write otherwise', () => {
    expect(phoneNeedsFixing('+213658417730', ALGERIA)).toBeFalse();
    expect(phoneNeedsFixing('06.58.41.77.30', ALGERIA)).toBeTrue();
    expect(phoneNeedsFixing('021 23 45 67', ALGERIA)).toBeTrue();
    expect(phoneNeedsFixing(null, ALGERIA)).toBeFalse();
  });

  it('takes its example from the profile', () => {
    expect(phoneExample(ALGERIA)).toEqual({ local: '0555123456', international: '+213 555 12 34 56' });
    expect(phoneExample(undefined)).toBeNull();
  });

  it('keeps the browser default the same as the server preset (PhoneNumbersTests pins the other side)', () => {
    expect(DEFAULT_REGIONAL.phone).toEqual(ALGERIA);
  });

  it('gives a profile cached before the phone rules its country rules', () => {
    const regional = TestBed.inject(RegionalService);
    const { phone: _none, ...cached } = DEFAULT_REGIONAL;
    regional.set({ ...cached });
    expect(regional.settings().phone).toEqual(ALGERIA);
    regional.set({ ...cached, countryCode: 'FR' });
    expect(regional.settings().phone).toBeUndefined();
    const own = { ...ALGERIA, example: '0661000000' };
    regional.set({ ...cached, phone: own });
    expect(regional.settings().phone).toEqual(own);
  });

  it('has its words in all three languages', () => {
    for (const lang of ['fr', 'ar', 'en'] as const) {
      for (const key of ['cust.phone.notMobile', 'cust.phone.notMobileShort', 'cust.filter.phoneToFix']) {
        expect(BACKOFFICE_TRANSLATIONS[lang][key]).withContext(`${lang} ${key}`).toBeTruthy();
      }
    }
  });
});
