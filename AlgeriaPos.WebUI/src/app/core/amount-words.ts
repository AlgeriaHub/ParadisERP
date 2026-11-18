import { Lang, RegionalSettings } from './models';
import { roundMoney } from './money';

/**
 * A number written out in words, for the line every invoice and receipt voucher in this
 * market carries ("Arrêtée la présente facture à la somme de …").
 *
 * <p>The words for the currency are not here. "dinar / centime", "درهم / سنتيم", "euro / cent"
 * are country data and travel in the country profile as `currencyUnitNames`, like the tax
 * name and the currency symbol — the same French code writes dinars in Algiers and euros in
 * Paris. A language the profile names no units for prints figures only, and the caller omits
 * the line: figures alone are correct, a guessed unit word is not.</p>
 *
 * <p>Three languages are written; anything else returns '' and is omitted the same way. Adding
 * a language is one function beside the three, not a change to the callers.</p>
 */

export interface CurrencyUnitNames {
  major: string;
  minor: string;
}

/** The unit names the profile holds for a language, or null when it names none. */
export function currencyUnitNames(settings: RegionalSettings, lang: Lang): CurrencyUnitNames | null {
  const names = settings.currencyUnitNames?.[lang];
  return names && names.major ? names : null;
}

/**
 * "mille deux cent trente-quatre dinars et cinquante centimes", or '' when the language is not
 * written here or `units` is null. Negative amounts are written as their absolute value with the
 * language's minus word in front; the caller decides whether a negative figure is ever printed.
 */
export function amountInWords(value: number, lang: Lang, units: CurrencyUnitNames | null, decimals = 2): string {
  if (!units || !Number.isFinite(value)) return '';
  const writer = WRITERS[lang];
  if (!writer) return '';

  const rounded = roundMoney(Math.abs(value), decimals);
  const major = Math.floor(rounded + 1e-9);
  const minor = Math.round((rounded - major) * 10 ** decimals);
  const text = writer(major, minor, units);
  return value < 0 ? `${MINUS[lang]} ${text}` : text;
}

/** Integer → words in one language; exported for the tests and for anything that counts things rather than money. */
export function integerInWords(n: number, lang: Lang): string {
  const words = INTEGERS[lang];
  return words ? words(Math.floor(Math.abs(n))) : '';
}

type Writer = (major: number, minor: number, units: CurrencyUnitNames) => string;

const MINUS: Record<string, string> = { fr: 'moins', en: 'minus', ar: 'ناقص' };

// ---------- French ----------

const FR_UNITS = ['zéro', 'un', 'deux', 'trois', 'quatre', 'cinq', 'six', 'sept', 'huit', 'neuf', 'dix', 'onze', 'douze', 'treize', 'quatorze', 'quinze', 'seize', 'dix-sept', 'dix-huit', 'dix-neuf'];
const FR_TENS = ['', 'dix', 'vingt', 'trente', 'quarante', 'cinquante', 'soixante', 'soixante', 'quatre-vingt', 'quatre-vingt'];

/**
 * 0–999 in French, with the 70/90 teens, "et un", and the plural of vingt/cent only when final.
 * `final` is false for the group before "mille": cent and vingt lose their s when another
 * numeral adjective follows ("deux cent mille", "quatre-vingt mille"), and keep it before the
 * nouns million and milliard ("deux cents millions").
 */
function frBelowThousand(n: number, final = true): string {
  const parts: string[] = [];
  const hundreds = Math.floor(n / 100);
  const rest = n % 100;
  if (hundreds) {
    if (hundreds === 1) parts.push('cent');
    // "deux cents" but "deux cent trois": the s goes only when nothing follows.
    else parts.push(`${FR_UNITS[hundreds]} cent${rest === 0 && final ? 's' : ''}`);
  }
  if (rest) {
    if (rest < 20) parts.push(FR_UNITS[rest]);
    else {
      const ten = Math.floor(rest / 10);
      let unit = rest % 10;
      // 70–79 and 90–99 count on from sixty and eighty: soixante-dix, quatre-vingt-onze.
      if (ten === 7 || ten === 9) unit += 10;
      let text = FR_TENS[ten];
      if (unit === 0) text += ten === 8 && final ? 's' : ''; // quatre-vingts, but quatre-vingt-un
      else if ((unit === 1 || unit === 11) && ten !== 8 && ten !== 9) text += unit === 11 ? ' et onze' : ' et un';
      else text += `-${FR_UNITS[unit]}`;
      parts.push(text);
    }
  }
  return parts.join(' ');
}

function frInteger(n: number): string {
  if (n === 0) return 'zéro';
  const parts: string[] = [];
  const billions = Math.floor(n / 1_000_000_000);
  const millions = Math.floor((n % 1_000_000_000) / 1_000_000);
  const thousands = Math.floor((n % 1_000_000) / 1000);
  const rest = n % 1000;
  if (billions) parts.push(`${frBelowThousand(billions)} milliard${billions > 1 ? 's' : ''}`);
  if (millions) parts.push(`${frBelowThousand(millions)} million${millions > 1 ? 's' : ''}`);
  // "mille" is invariable and takes no "un" in front of it; the group before it is not final.
  if (thousands) parts.push(thousands === 1 ? 'mille' : `${frBelowThousand(thousands, false)} mille`);
  if (rest) parts.push(frBelowThousand(rest));
  return parts.join(' ');
}

const frPlural = (n: number, word: string) => (n > 1 && !/s$/.test(word) ? `${word}s` : word);

const fr: Writer = (major, minor, units) => {
  const head = `${frInteger(major)} ${frPlural(major, units.major)}`;
  if (!minor || !units.minor) return head;
  return `${head} et ${frInteger(minor)} ${frPlural(minor, units.minor)}`;
};

// ---------- English ----------

const EN_UNITS = ['zero', 'one', 'two', 'three', 'four', 'five', 'six', 'seven', 'eight', 'nine', 'ten', 'eleven', 'twelve', 'thirteen', 'fourteen', 'fifteen', 'sixteen', 'seventeen', 'eighteen', 'nineteen'];
const EN_TENS = ['', 'ten', 'twenty', 'thirty', 'forty', 'fifty', 'sixty', 'seventy', 'eighty', 'ninety'];

function enBelowThousand(n: number): string {
  const parts: string[] = [];
  const hundreds = Math.floor(n / 100);
  const rest = n % 100;
  if (hundreds) parts.push(`${EN_UNITS[hundreds]} hundred`);
  if (rest) {
    if (rest < 20) parts.push(EN_UNITS[rest]);
    else parts.push(rest % 10 ? `${EN_TENS[Math.floor(rest / 10)]}-${EN_UNITS[rest % 10]}` : EN_TENS[Math.floor(rest / 10)]);
  }
  return parts.join(' ');
}

function enInteger(n: number): string {
  if (n === 0) return 'zero';
  const parts: string[] = [];
  const scales: [number, string][] = [[1_000_000_000, 'billion'], [1_000_000, 'million'], [1000, 'thousand']];
  let rest = n;
  for (const [size, word] of scales) {
    const count = Math.floor(rest / size);
    if (count) parts.push(`${enBelowThousand(count)} ${word}`);
    rest %= size;
  }
  if (rest) parts.push(enBelowThousand(rest));
  return parts.join(' ');
}

const enPlural = (n: number, word: string) => (n === 1 || /s$/.test(word) ? word : `${word}s`);

const en: Writer = (major, minor, units) => {
  const head = `${enInteger(major)} ${enPlural(major, units.major)}`;
  if (!minor || !units.minor) return head;
  return `${head} and ${enInteger(minor)} ${enPlural(minor, units.minor)}`;
};

// ---------- Arabic ----------

// Masculine forms, which is how amounts are read on Algerian paper whatever the unit's gender;
// the dual and the 3–10 plural of the unit word itself are left to the unit name (the profile
// gives one form), so "2 dinars" reads اثنان دينار rather than an inflected دينارين.
const AR_UNITS = ['صفر', 'واحد', 'اثنان', 'ثلاثة', 'أربعة', 'خمسة', 'ستة', 'سبعة', 'ثمانية', 'تسعة', 'عشرة', 'أحد عشر', 'اثنا عشر', 'ثلاثة عشر', 'أربعة عشر', 'خمسة عشر', 'ستة عشر', 'سبعة عشر', 'ثمانية عشر', 'تسعة عشر'];
const AR_TENS = ['', 'عشرة', 'عشرون', 'ثلاثون', 'أربعون', 'خمسون', 'ستون', 'سبعون', 'ثمانون', 'تسعون'];
const AR_HUNDREDS = ['', 'مائة', 'مائتان', 'ثلاثمائة', 'أربعمائة', 'خمسمائة', 'ستمائة', 'سبعمائة', 'ثمانمائة', 'تسعمائة'];

function arBelowThousand(n: number): string {
  const parts: string[] = [];
  const hundreds = Math.floor(n / 100);
  const rest = n % 100;
  if (hundreds) parts.push(AR_HUNDREDS[hundreds]);
  if (rest) {
    if (rest < 20) parts.push(AR_UNITS[rest]);
    else {
      const unit = rest % 10;
      const ten = AR_TENS[Math.floor(rest / 10)];
      // Units come before tens, joined by و: خمسة وعشرون.
      parts.push(unit ? `${AR_UNITS[unit]} و${ten}` : ten);
    }
  }
  return parts.join(' و');
}

/**
 * The scale word in the form its count takes: one, two (dual), 3–10 (plural), 11–99 (singular).
 * Above 100 the noun follows the last spoken numeral, not the total: 105 thousand is
 * مائة وخمسة آلاف, 101 thousand is مائة وألف, 102 thousand is مائة وألفان, 110 thousand is
 * مائة وعشرة آلاف, while 100 and 111–199 thousand keep the singular.
 */
function arScale(count: number, forms: [string, string, string, string]): string {
  const [one, two, few, many] = forms;
  if (count === 1) return one;
  if (count === 2) return two;
  if (count >= 3 && count <= 10) return `${arBelowThousand(count)} ${few}`;
  if (count > 100) {
    const rem = count % 100;
    if (rem === 1) return `${arBelowThousand(count - 1)} و${one}`;
    if (rem === 2) return `${arBelowThousand(count - 2)} و${two}`;
    if (rem >= 3 && rem <= 10) return `${arBelowThousand(count)} ${few}`;
  }
  return `${arBelowThousand(count)} ${many}`;
}

function arInteger(n: number): string {
  if (n === 0) return 'صفر';
  const parts: string[] = [];
  const billions = Math.floor(n / 1_000_000_000);
  const millions = Math.floor((n % 1_000_000_000) / 1_000_000);
  const thousands = Math.floor((n % 1_000_000) / 1000);
  const rest = n % 1000;
  if (billions) parts.push(arScale(billions, ['مليار', 'ملياران', 'مليارات', 'مليار']));
  if (millions) parts.push(arScale(millions, ['مليون', 'مليونان', 'ملايين', 'مليون']));
  if (thousands) parts.push(arScale(thousands, ['ألف', 'ألفان', 'آلاف', 'ألف']));
  if (rest) parts.push(arBelowThousand(rest));
  return parts.join(' و');
}

const ar: Writer = (major, minor, units) => {
  const head = `${arInteger(major)} ${units.major}`;
  if (!minor || !units.minor) return head;
  return `${head} و${arInteger(minor)} ${units.minor}`;
};

const WRITERS: Record<string, Writer> = { fr, en, ar };
const INTEGERS: Record<string, (n: number) => string> = { fr: frInteger, en: enInteger, ar: arInteger };
