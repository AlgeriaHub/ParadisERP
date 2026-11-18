import { TestBed } from '@angular/core/testing';
import { ApiError } from '../../../core/api.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { invoiceLineName, maintenanceError } from './maintenance-words';

describe('maintenance words (MS-34, customer repair income)', () => {
  let i18n: I18nService;

  beforeEach(() => {
    i18n = TestBed.inject(I18nService);
    i18n.extend(BACKOFFICE_TRANSLATIONS);
  });

  it('says a split job invoice’s parts and labour lines in the paper’s language, any other line as stored', () => {
    const inLang = (lang: 'fr' | 'ar' | 'en') => (key: string, params?: Record<string, string | number>) => i18n.tIn(lang, key, params);
    const parts = 'Pièces — MJ-000012 — Bétonnière 350 L';
    const labour = "Main-d'œuvre et service — MJ-000012 — Bétonnière 350 L";

    expect(invoiceLineName(parts, inLang('fr'))).toBe('Pièces — MJ-000012 — Bétonnière 350 L');
    expect(invoiceLineName(labour, inLang('fr'))).toBe('Main-d’œuvre et service — MJ-000012 — Bétonnière 350 L');
    expect(invoiceLineName(parts, inLang('en'))).toBe('Parts — MJ-000012 — Bétonnière 350 L');
    expect(invoiceLineName(labour, inLang('en'))).toBe('Labour and service — MJ-000012 — Bétonnière 350 L');
    expect(invoiceLineName(parts, inLang('ar'))).toBe('قطع الغيار — MJ-000012 — Bétonnière 350 L');
    expect(invoiceLineName(labour, inLang('ar'))).toBe('اليد العاملة والخدمة — MJ-000012 — Bétonnière 350 L');

    // The one-line invoice, a sale's product, a rental line: as stored.
    for (const name of ['MJ-000012 — Bétonnière 350 L', 'Roulement', 'Pièces détachées', 'Location — contrat CTR-000004']) {
      expect(invoiceLineName(name, inLang('en'))).toBe(name);
    }
  });

  it('says in words that the invoice waits for the repair service income account', () => {
    const refused = new ApiError(JSON.stringify({ code: 'accounts_not_mapped', detail: 'x' }), 409);
    expect(maintenanceError(refused, (k) => i18n.t(k))).toBe(i18n.t('maint.error.incomeNotMapped'));
    expect(i18n.t('maint.error.incomeNotMapped')).toContain('MS-34');
  });
});
