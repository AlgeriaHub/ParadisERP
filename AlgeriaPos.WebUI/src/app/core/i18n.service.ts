import { ApplicationRef, Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { BuiltinLang, Lang, LanguagePack } from './models';
import { TRANSLATIONS } from './i18n/translations';
import { LANDING_TRANSLATIONS } from './i18n/landing.translations';
import { numericDate, numericDateTime } from './day-format';
import { parseInstant } from './instant';
import { formatMoney } from './money';
import { RegionalService, localized } from './regional.service';
import { runViewTransition } from './view-transition';

const STORAGE_KEY = 'pos.lang';
export const LANGS: { code: BuiltinLang; label: string }[] = [
  { code: 'fr', label: 'Français' },
  { code: 'ar', label: 'العربية' },
  { code: 'en', label: 'English' },
];

@Injectable({ providedIn: 'root' })
export class I18nService {
  private readonly regional = inject(RegionalService);
  private readonly appRef = inject(ApplicationRef);

  /**
   * Every built-in text the app currently knows: the core dictionary a till downloads, plus
   * whatever a lazy route has registered through {@link extend}. One object per language rather
   * than a chain of them, because `t` is called from templates on every change-detection pass
   * and a miss must cost one property read, not four.
   */
  private readonly texts: Record<BuiltinLang, Record<string, string>> = {
    fr: { ...TRANSLATIONS.fr, ...LANDING_TRANSLATIONS.fr },
    ar: { ...TRANSLATIONS.ar, ...LANDING_TRANSLATIONS.ar },
    en: { ...TRANSLATIONS.en, ...LANDING_TRANSLATIONS.en },
  };

  private chosen = readStoredLang();
  /** The language the reader last chose, ahead of {@link lang} while its cross-fade is pending. */
  private requested: Lang | null = null;
  /** Operator-uploaded translations, by language code. */
  readonly packs = signal<Record<string, LanguagePack>>({});
  readonly lang = signal<Lang>(this.chosen ?? this.fallbackLang());
  readonly dir = computed<'rtl' | 'ltr'>(() => (this.isRtl(this.lang()) ? 'rtl' : 'ltr'));
  /** Languages the company's country profile enables and this till can show, in profile order. */
  readonly langs = computed(() =>
    this.regional
      .settings()
      .languages.map((code) => {
        const builtin = LANGS.find((l) => l.code === code);
        if (builtin) return builtin;
        const pack = this.packs()[code];
        return pack ? { code, label: pack.label } : null;
      })
      .filter((l): l is { code: Lang; label: string } => l !== null),
  );
  /** For an uploaded language, the built-in language its missing texts come from. */
  readonly fallback = computed<BuiltinLang>(() => this.packs()[this.lang()]?.fallbackLanguage ?? 'fr');
  readonly countryCode = computed(() => this.regional.settings().countryCode);
  readonly taxName = computed(() => this.text(this.regional.settings().taxNames));
  readonly currencySymbol = computed(
    () => this.text(this.regional.settings().currencySymbols) || this.regional.settings().currencyCode,
  );

  constructor() {
    this.apply(this.lang());
    // Until someone picks a language, follow the company's default; and a profile that no
    // longer offers the current language moves the UI to its default.
    effect(() => {
      const settings = this.regional.settings();
      const current = untracked(this.lang);
      const chosen = this.chosen;
      const wanted = chosen && settings.languages.includes(chosen) && this.isUsable(chosen) ? chosen : this.fallbackLang();
      if (wanted !== current) this.switchTo(wanted, false);
    });
  }

  /**
   * Adds the vocabulary of a lazily loaded area — the back office, the print documents, the
   * operator portal. Called by the component that owns that route, in a field initialiser, so
   * the keys are in place before its template first renders.
   *
   * <p>Merging rather than replacing, and idempotent: two routes may carry the same dictionary,
   * and the second registration must not undo the first. Nothing is ever removed — a chunk that
   * has been downloaded stays downloaded, and forgetting its words would only make a key that
   * worked a moment ago render as itself.</p>
   *
   * @returns how many texts were new, across the three languages — so the caller's field holds a
   *   figure rather than nothing, and a test can see the dictionary arrive.
   */
  extend(extra: Record<BuiltinLang, Record<string, string>>): number {
    let added = 0;
    for (const lang of Object.keys(this.texts) as BuiltinLang[]) {
      const words = extra[lang];
      if (!words) continue;
      for (const key of Object.keys(words)) if (!(key in this.texts[lang])) added++;
      Object.assign(this.texts[lang], words);
    }
    return added;
  }

  setPacks(packs: LanguagePack[]): void {
    this.packs.set(Object.fromEntries(packs.map((p) => [p.code, p])));
  }

  setLang(lang: Lang): void {
    // Compared with the language last asked for, not the one on screen: while a cross-fade is
    // pending the screen still shows the old one, and choosing it again must undo the switch
    // rather than be dropped as "already showing".
    if (lang === (this.requested ?? this.lang())) return;
    this.requested = lang;
    // A deliberate choice by the cashier: cross-fade it, because the text and the whole
    // left-to-right / right-to-left layout turn over at once. The automatic switch a profile
    // forces (see the effect above) stays instant — nobody asked for it.
    runViewTransition(() => {
      // Several quick choices may each queue one of these; the first to run applies the
      // latest choice, and the rest find nothing left to do.
      const wanted = this.requested;
      this.requested = null;
      if (wanted !== null && wanted !== this.lang()) this.switchTo(wanted, true);
      // Zone change detection has not run yet inside the callback, and the browser snapshots
      // the DOM as soon as it returns; flush so the new frame is the translated one.
      this.appRef.tick();
    });
  }

  /**
   * Translates a key, substituting {placeholders}. Uploaded languages fall back to their pack's
   * fallback language; everything falls back to French, then the key itself.
   */
  t(key: string, params?: Record<string, string | number>): string {
    const lang = this.lang();
    let text =
      this.texts[lang as BuiltinLang]?.[key] ??
      this.packs()[lang]?.strings[key] ??
      this.texts[this.fallback()][key] ??
      this.texts['fr'][key] ??
      key;
    const values: Record<string, string | number> = {
      tax: this.taxName(),
      taxId: this.text(this.regional.settings().customerTaxIdLabels),
      currency: this.currencySymbol(),
      ...params,
    };
    for (const [name, value] of Object.entries(values)) {
      text = text.replace(new RegExp(`\\{${name}\\}`, 'g'), String(value));
    }
    return text;
  }

  /**
   * `t` for a language that is not the interface's — a document printed in the customer's
   * language while the app stays in the clerk's. The {tax}/{taxId}/{currency} placeholders are
   * filled from the profile in that language too. Pure over the dictionaries: nothing is
   * switched, nothing is remembered.
   */
  tIn(lang: Lang, key: string, params?: Record<string, string | number>): string {
    const fallback = this.packs()[lang]?.fallbackLanguage ?? 'fr';
    let text =
      this.texts[lang as BuiltinLang]?.[key] ??
      this.packs()[lang]?.strings[key] ??
      this.texts[fallback][key] ??
      this.texts['fr'][key] ??
      key;
    const settings = this.regional.settings();
    const values: Record<string, string | number> = {
      tax: localized(settings.taxNames, lang, fallback),
      taxId: localized(settings.customerTaxIdLabels, lang, fallback),
      currency: localized(settings.currencySymbols, lang, fallback) || settings.currencyCode,
      ...params,
    };
    for (const [name, value] of Object.entries(values)) {
      text = text.replace(new RegExp(`\\{${name}\\}`, 'g'), String(value));
    }
    return text;
  }

  /** Picks the current language's entry of a per-language text from the country profile. */
  text(value: Record<Lang, string | undefined> | undefined): string {
    return localized(value, this.lang(), this.fallback());
  }

  /** `text` in a given language. */
  textIn(lang: Lang, value: Record<Lang, string | undefined> | undefined): string {
    return localized(value, lang, this.packs()[lang]?.fallbackLanguage ?? 'fr');
  }

  /** `rateLabel` in a given language. */
  rateLabelIn(lang: Lang, rate: number): string {
    return this.regional.taxRateLabel(rate, lang, this.packs()[lang]?.fallbackLanguage ?? 'fr');
  }

  /** `money` in a given language: separators and the symbol follow it, the profile stays the same. */
  moneyIn(lang: Lang, value: number): string {
    const settings = this.regional.settings();
    const symbol = localized(settings.currencySymbols, lang, this.packs()[lang]?.fallbackLanguage ?? 'fr') || settings.currencyCode;
    return formatMoney(value, lang, settings, symbol);
  }

  /** `dateTime` for a document in a given language; dates read dd/MM/yyyy in all of them. */
  dateTimeIn(lang: Lang, value: string | Date): string {
    return this.formatDateTime(value);
  }

  /** Whether a language reads right to left; public so a document can set its own `dir`. */
  isRtlLang(lang: Lang): boolean {
    return this.isRtl(lang);
  }

  /** Display label of a tax rate in the current language, or '' when none is set. */
  rateLabel(rate: number): string {
    return this.regional.taxRateLabel(rate, this.lang(), this.fallback());
  }

  money(value: number): string {
    return formatMoney(value, this.lang(), this.regional.settings(), this.currencySymbol());
  }

  dateTime(value: string | Date): string {
    return this.formatDateTime(value);
  }

  /**
   * A date with no time on it. Some facts are dates and nothing else — the day a yearly
   * allowance resets, a period's end — and rendering those through `dateTime` printed a midnight
   * beside them that says something the fact does not.
   */
  date(value: string | Date): string {
    return this.formatDate(value);
  }

  /** `date` in a given language. */
  dateIn(lang: Lang, value: string | Date): string {
    return this.formatDate(value);
  }

  /** dd/MM/yyyy HH:mm in every language, on the company's clock (`core/day-format.ts`). */
  private formatDateTime(value: string | Date): string {
    // A moment from the API is UTC even when it arrives without its Z (`parseInstant`).
    const date = parseInstant(value);
    if (Number.isNaN(date.getTime())) return '—';
    return numericDateTime(date, this.regional.settings().timeZone);
  }

  private formatDate(value: string | Date): string {
    const date = parseInstant(value);
    if (Number.isNaN(date.getTime())) return '—';
    return numericDate(date, this.regional.settings().timeZone);
  }

  private isRtl(lang: Lang): boolean {
    return lang === 'ar' || !!this.packs()[lang]?.rtl;
  }

  /** Built-in languages always work; an uploaded one only once its pack is on this device. */
  private isUsable(lang: Lang): boolean {
    return LANGS.some((l) => l.code === lang) || !!this.packs()[lang];
  }

  private fallbackLang(): Lang {
    const settings = this.regional.settings();
    const browser = (navigator.language || '').slice(0, 2).toLowerCase();
    if (browser === 'ar' && settings.languages.includes('ar')) return 'ar';
    if (this.isUsable(settings.defaultLanguage)) return settings.defaultLanguage;
    return settings.languages.find((l) => this.isUsable(l)) ?? 'fr';
  }

  private switchTo(lang: Lang, remember: boolean): void {
    this.lang.set(lang);
    if (remember) {
      this.chosen = lang;
      try {
        localStorage.setItem(STORAGE_KEY, lang);
      } catch {
        /* private mode — the choice just won't persist */
      }
    }
    this.apply(lang);
  }

  private apply(lang: Lang): void {
    const html = document.documentElement;
    html.lang = lang;
    html.dir = this.isRtl(lang) ? 'rtl' : 'ltr';
  }
}

function readStoredLang(): Lang | null {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    if (stored && /^[a-z]{2,3}(-[a-z0-9]{2,8})?$/.test(stored)) return stored;
  } catch {
    /* ignore */
  }
  return null;
}
