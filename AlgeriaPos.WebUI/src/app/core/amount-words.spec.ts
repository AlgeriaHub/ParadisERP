import { amountInWords, currencyUnitNames, integerInWords } from './amount-words';
import { DEFAULT_REGIONAL } from './regional.service';

const DA = { major: 'dinar', minor: 'centime' };
const DZ_AR = { major: 'دينار', minor: 'سنتيم' };
const EN = { major: 'dinar', minor: 'centime' };

describe('amountInWords — French', () => {
  const fr = (n: number) => integerInWords(n, 'fr');

  it('writes the numbers French gets wrong most often', () => {
    expect(fr(0)).toBe('zéro');
    expect(fr(1)).toBe('un');
    expect(fr(21)).toBe('vingt et un');
    expect(fr(71)).toBe('soixante et onze');
    expect(fr(80)).toBe('quatre-vingts');
    expect(fr(81)).toBe('quatre-vingt-un');
    expect(fr(91)).toBe('quatre-vingt-onze');
    expect(fr(100)).toBe('cent');
    expect(fr(200)).toBe('deux cents');
    expect(fr(201)).toBe('deux cent un');
    expect(fr(1000)).toBe('mille');
    expect(fr(2000)).toBe('deux mille');
    expect(fr(1_000_000)).toBe('un million');
    expect(fr(2_000_000)).toBe('deux millions');
  });

  it('drops the s of cent and vingt before mille and keeps it before million', () => {
    expect(fr(80_000)).toBe('quatre-vingt mille');
    expect(fr(200_000)).toBe('deux cent mille');
    expect(fr(280_000)).toBe('deux cent quatre-vingt mille');
    expect(fr(1_200_000)).toBe('un million deux cent mille');
    expect(fr(200_000_000)).toBe('deux cents millions');
    expect(fr(80_000_000)).toBe('quatre-vingts millions');
    expect(fr(200_200)).toBe('deux cent mille deux cents');
  });

  it('writes an amount with its centimes and pluralises the unit', () => {
    expect(amountInWords(1_234_567.5, 'fr', DA)).toBe(
      'un million deux cent trente-quatre mille cinq cent soixante-sept dinars et cinquante centimes',
    );
    expect(amountInWords(1, 'fr', DA)).toBe('un dinar');
    expect(amountInWords(0.01, 'fr', DA)).toBe('zéro dinar et un centime');
    expect(amountInWords(-5, 'fr', DA)).toBe('moins cinq dinars');
  });
});

describe('amountInWords — Arabic', () => {
  const ar = (n: number) => integerInWords(n, 'ar');

  it('puts units before tens, uses the dual, and inflects the scale words', () => {
    expect(ar(1)).toBe('واحد');
    expect(ar(2)).toBe('اثنان');
    expect(ar(11)).toBe('أحد عشر');
    expect(ar(25)).toBe('خمسة وعشرون');
    expect(ar(100)).toBe('مائة');
    expect(ar(1000)).toBe('ألف');
    expect(ar(2000)).toBe('ألفان');
    expect(ar(3000)).toBe('ثلاثة آلاف');
    expect(ar(1_234_567)).toBe('مليون ومائتان وأربعة وثلاثون ألف وخمسمائة وسبعة وستون');
  });

  it('inflects the scale word after the last spoken numeral above one hundred', () => {
    expect(ar(100_000)).toBe('مائة ألف');
    expect(ar(101_000)).toBe('مائة وألف');
    expect(ar(102_000)).toBe('مائة وألفان');
    expect(ar(105_000)).toBe('مائة وخمسة آلاف');
    expect(ar(110_000)).toBe('مائة وعشرة آلاف');
    expect(ar(111_000)).toBe('مائة وأحد عشر ألف');
    expect(ar(203_000)).toBe('مائتان وثلاثة آلاف');
    expect(ar(1_005_000)).toBe('مليون وخمسة آلاف');
    expect(ar(105_000_000)).toBe('مائة وخمسة ملايين');
  });

  it('writes the amount with its subunit', () => {
    expect(amountInWords(1_234_567.5, 'ar', DZ_AR)).toBe('مليون ومائتان وأربعة وثلاثون ألف وخمسمائة وسبعة وستون دينار وخمسون سنتيم');
  });
});

describe('amountInWords — English and the edges', () => {
  it('writes English with hyphenated tens', () => {
    expect(amountInWords(1_234_567.5, 'en', EN)).toBe(
      'one million two hundred thirty-four thousand five hundred sixty-seven dinars and fifty centimes',
    );
    expect(amountInWords(1, 'en', EN)).toBe('one dinar');
    expect(amountInWords(21, 'en', EN)).toBe('twenty-one dinars');
  });

  it('handles a three-decimal minor unit', () => {
    expect(amountInWords(12.345, 'fr', { major: 'dinar', minor: 'millime' }, 3)).toBe('douze dinars et trois cent quarante-cinq millimes');
  });

  it('returns nothing for a language it cannot write or a profile with no unit names', () => {
    expect(amountInWords(12, 'tzm', DA)).toBe('');
    expect(amountInWords(12, 'fr', null)).toBe('');
    expect(amountInWords(12, 'fr', { major: 'dinar', minor: '' })).toBe('douze dinars');
    expect(amountInWords(12.5, 'fr', { major: 'dinar', minor: '' })).toBe('douze dinars');
  });

  it('reads the unit names from the country profile and the Algeria default carries them', () => {
    expect(currencyUnitNames(DEFAULT_REGIONAL, 'fr')).toEqual(DA);
    expect(currencyUnitNames(DEFAULT_REGIONAL, 'ar')).toEqual(DZ_AR);
    expect(currencyUnitNames({ ...DEFAULT_REGIONAL }, 'tzm')).toBeNull();
  });
});
