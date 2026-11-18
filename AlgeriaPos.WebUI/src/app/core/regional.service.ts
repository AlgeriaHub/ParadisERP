import { Injectable, computed, signal } from '@angular/core';
import { Lang, LocalizedText, RegionalSettings } from './models';
import { setNumberFormat } from './number-format';

const rateLabels = (standard: number, reduced: number, exempt: number) => [
  { rate: standard, labels: { ar: 'النسبة العادية', fr: 'Taux normal', en: 'Standard rate' } },
  { rate: reduced, labels: { ar: 'النسبة المخفضة', fr: 'Taux réduit', en: 'Reduced rate' } },
  { rate: exempt, labels: { ar: 'معفى', fr: 'Exonéré', en: 'Exempt' } },
];

/**
 * Used until the server has sent a profile (first start, or an older server). Mirrors the
 * server's Algeria preset, which is also the server-side default.
 */
export const DEFAULT_REGIONAL: RegionalSettings = {
  // How the dinar is spoken, for the amount in words on invoices and vouchers. The server's
  // DZ preset carries the same words and every profile it sends includes them
  // (`CurrencyUnitNamesTests` pins both).
  currencyUnitNames: { ar: { major: 'دينار', minor: 'سنتيم' }, fr: { major: 'dinar', minor: 'centime' }, en: { major: 'dinar', minor: 'centime' } },
  countryCode: 'DZ',
  // Mirrors UiFonts.Default on the server: a professional face out of the box, the device's
  // own only when a company chooses it.
  uiFont: 'thmanyah-sans',
  currencyCode: 'DZD',
  currencyDecimals: 2,
  currencySymbols: { ar: 'دج', fr: 'DA', en: 'DA' },
  currencySymbolFirst: false,
  timeZone: 'Africa/Algiers',
  languages: ['fr', 'ar', 'en'],
  defaultLanguage: 'fr',
  taxNames: { ar: 'ض.م', fr: 'TVA', en: 'VAT' },
  taxRates: [19, 9, 0],
  taxRateLabels: rateLabels(19, 9, 0),
  defaultTaxRate: 19,
  showZeroTax: false,
  pricesIncludeTax: false,
  fiscalIds: [
    { key: 'nif', code: 'NIF', labels: { ar: 'الرقم الجبائي', fr: "Numéro d'identification fiscale (NIF)", en: 'Tax ID (NIF)' }, required: false, showOnReceipt: true },
    { key: 'nis', code: 'NIS', labels: { ar: 'الرقم الإحصائي', fr: "Numéro d'identification statistique (NIS)", en: 'Statistical ID (NIS)' }, required: false, showOnReceipt: true },
    { key: 'rc', code: 'RC', labels: { ar: 'السجل التجاري', fr: 'Registre du commerce (RC)', en: 'Trade register (RC)' }, required: false, showOnReceipt: true },
    { key: 'ai', code: 'AI', labels: { ar: 'رقم المادة الجبائية', fr: "Article d'imposition (AI)", en: 'Tax article (AI)' }, required: false, showOnReceipt: true },
  ],
  customerTaxIdLabels: { ar: 'الرقم الجبائي', fr: 'NIF', en: 'Tax ID (NIF)' },
  cashDenominations: [100, 200, 500, 1000, 2000, 5000],
  cashRoundingIncrement: 0,
  cashRoundingMode: 'Nearest',
  paymentMethods: ['Cash', 'Card', 'Credit'],
  receiptSections: ['header', 'fiscalIds', 'meta', 'lines', 'totals', 'taxBreakdown', 'payment', 'legal', 'footer'],
  receiptLegalText: {},
  // Mirrors the DZ preset's phone rules (RegionalPresets.cs, PhoneNumbersTests): +213, a mobile 5/6/7 + 8 digits.
  phone: { callingCode: '213', trunkPrefix: '0', mobilePattern: '^[567][0-9]{8}$', displayGroups: [3, 2, 2, 2], example: '0555123456' },
};

/** Picks the text for a language, falling back to the given language, then French, English, Arabic, then anything. */
export function localized(text: LocalizedText | undefined, lang: Lang, fallback?: Lang): string {
  if (!text) return '';
  return (
    text[lang] ||
    (fallback && text[fallback]) ||
    text['fr'] ||
    text['en'] ||
    text['ar'] ||
    Object.values(text).find(Boolean) ||
    ''
  );
}

/** Rounds a cash amount due to the profile's step (0 = off), keeping the currency's decimals. */
export function roundCash(amount: number, settings: RegionalSettings): number {
  const step = settings.cashRoundingIncrement;
  if (!step) return amount;
  const units = amount / step;
  // A tiny epsilon keeps 12.35 / 0.05 = 246.99999… from rounding the wrong way.
  const eps = 1e-9;
  const rounded =
    settings.cashRoundingMode === 'Up'
      ? Math.ceil(units - eps)
      : settings.cashRoundingMode === 'Down'
        ? Math.floor(units + eps)
        : Math.sign(units) * Math.round(Math.abs(units) + eps);
  const factor = 10 ** settings.currencyDecimals;
  return Math.round(rounded * step * factor) / factor;
}

/** The active company's country profile. Everything that differs by country reads it from here. */
@Injectable({ providedIn: 'root' })
export class RegionalService {
  readonly settings = signal<RegionalSettings>(DEFAULT_REGIONAL);

  readonly decimals = computed(() => this.settings().currencyDecimals);
  /** Input step for money fields: 1, 0.01, 0.001... */
  readonly moneyStep = computed(() => 1 / 10 ** this.decimals());

  set(settings: RegionalSettings | null | undefined): void {
    // Every figure the app writes follows the company's number format (core/number-format.ts).
    if (settings) setNumberFormat(settings.numberFormat);
    // Profiles cached before these settings existed get the defaults.
    if (settings)
      this.settings.set({
        ...settings,
        taxRateLabels: settings.taxRateLabels ?? [],
        cashRoundingIncrement: settings.cashRoundingIncrement ?? 0,
        cashRoundingMode: settings.cashRoundingMode ?? 'Nearest',
        receiptSections: settings.receiptSections?.length ? settings.receiptSections : DEFAULT_REGIONAL.receiptSections,
        receiptLegalText: settings.receiptLegalText ?? {},
        // A profile cached at a sign-in before 2026-11-17 has no phone rules: the profile arrives only
        // with a sign-in, so its country's are used until the next one (the server always has them).
        phone: settings.phone ?? (settings.countryCode === DEFAULT_REGIONAL.countryCode ? DEFAULT_REGIONAL.phone : undefined),
      });
  }

  taxRateLabel(rate: number, lang: Lang, fallback?: Lang): string {
    return localized(this.settings().taxRateLabels.find((l) => l.rate === rate)?.labels, lang, fallback);
  }
}
