import { TRANSLATIONS } from './translations';
import { BACKOFFICE_TRANSLATIONS } from './translations-backoffice';
import { BuiltinLang } from '../models';

/**
 * A key missing from one dictionary compiles perfectly well — both are typed
 * `Record<BuiltinLang, Record<string, string>>`, so nothing relates the three languages to each
 * other. The failure is silent and lands in front of a user: `i18n.t` falls through and the raw
 * key is rendered on screen.
 *
 * That already happened. The treasury tab shipped a call to `backoffice.customers.title`,
 * which exists in no dictionary, and printed the key string in the interface until it was
 * spotted by eye during the redesign. `platform-text.ts` gets this parity for free from its
 * `PlatformText` type; the app dictionaries get it here instead.
 *
 * Since the split there is a second way to render a raw key, and it is worse because it only
 * shows on the screen that cannot afford it: putting a word a till needs in the dictionary the
 * till never downloads. The last block below is the guard for that.
 */
const DICTIONARIES: [string, Record<BuiltinLang, Record<string, string>>][] = [
  ['core', TRANSLATIONS],
  ['back office', BACKOFFICE_TRANSLATIONS],
];

describe('translation dictionaries', () => {
  const reference: BuiltinLang = 'fr'; // French is the default and the most complete.

  for (const [name, dictionary] of DICTIONARIES) {
    describe(name, () => {
      const languages = Object.keys(dictionary) as BuiltinLang[];

      it('ships all three built-in languages', () => {
        expect(languages.sort()).toEqual(['ar', 'en', 'fr']);
      });

      for (const lang of ['ar', 'en'] as BuiltinLang[]) {
        it(`has no key in French that ${lang} is missing`, () => {
          const missing = Object.keys(dictionary[reference]).filter((key) => !(key in dictionary[lang]));
          expect(missing).toEqual([]);
        });

        it(`has no key in ${lang} that French is missing`, () => {
          const extra = Object.keys(dictionary[lang]).filter((key) => !(key in dictionary[reference]));
          expect(extra).toEqual([]);
        });
      }

      it('has no blank translation', () => {
        for (const lang of languages) {
          const blank = Object.entries(dictionary[lang])
            .filter(([, value]) => !value || !value.trim())
            .map(([key]) => `${lang}:${key}`);
          expect(blank).toEqual([]);
        }
      });
    });
  }

  it('says each word once: no key is in both dictionaries', () => {
    // Two homes for one key is two texts that drift apart, and the winner depends on whether a
    // lazy route happens to have been opened.
    const both = Object.keys(TRANSLATIONS[reference]).filter((key) => key in BACKOFFICE_TRANSLATIONS[reference]);
    expect(both).toEqual([]);
  });

  it('keeps the till small: the back office carries the bulk of the words', () => {
    // Not a threshold anyone should tune — it is here so that moving a large block back into the
    // core dictionary has to be deliberate. The till downloads the core one on every load.
    const core = Object.keys(TRANSLATIONS[reference]).length;
    const lazy = Object.keys(BACKOFFICE_TRANSLATIONS[reference]).length;
    expect(lazy).toBeGreaterThan(core * 4);
  });
});
