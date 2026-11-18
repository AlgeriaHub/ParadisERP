import { ACCOUNTING_SOURCES } from './accounting-tab.component';
import { BACKOFFICE_TRANSLATIONS } from '../../core/i18n/translations-backoffice';
import { VOUCHERS_TRANSLATIONS } from '../../core/i18n/translations-vouchers';
import { BuiltinLang } from '../../core/models';

describe('accounting source labels', () => {
  const languages: BuiltinLang[] = ['fr', 'ar', 'en'];

  // + valuation-switch and valuation-gap (C-01, 2026-11-09), + cost-price-revaluation (B-F01, 2026-11-09),
  // + manufacturing, maintenance and maintenance-issue (Phase D, M6, 2026-11-09)
  // + fixed-asset-switch, fixed-asset and fixed-assets (REN-12, M7a, 2026-11-09)
  // + customer-advance-application (M7b, 2026-11-09)
  // + customer-advance-kept (SAL-13, 2026-11-10)
  it('has 40 unique source types', () => {
    expect(ACCOUNTING_SOURCES.length).toBe(40);
    expect(new Set(ACCOUNTING_SOURCES).size).toBe(40);
  });

  for (const lang of languages) {
    describe(`in ${lang}`, () => {
      for (const source of ACCOUNTING_SOURCES) {
        it(`translates source "${source}"`, () => {
          const key = `acc.src.${source}`;
          const label = BACKOFFICE_TRANSLATIONS[lang]?.[key] ?? VOUCHERS_TRANSLATIONS[lang]?.[key];
          expect(label).toBeDefined();
          expect(typeof label).toBe('string');
          expect(label?.trim().length).toBeGreaterThan(0);
        });
      }
    });
  }
});
