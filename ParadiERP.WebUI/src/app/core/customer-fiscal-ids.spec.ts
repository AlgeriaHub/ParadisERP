import { customerFiscalIdLines, customerOtherFiscalIds, customerTaxIdKey } from './customer-fiscal-ids';

const algeriaIds = [
  { key: 'nif', code: 'NIF', labels: { fr: 'NIF' }, required: false, showOnReceipt: true },
  { key: 'nis', code: 'NIS', labels: { fr: 'NIS' }, required: false, showOnReceipt: true },
  { key: 'rc', code: 'RC', labels: { fr: 'RC' }, required: false, showOnReceipt: true },
  { key: 'ai', code: 'AI', labels: { fr: 'AI' }, required: false, showOnReceipt: true },
];

const algeria = {
  fiscalIds: algeriaIds,
  customerTaxIdLabels: { ar: 'الرقم الجبائي', fr: 'NIF', en: 'Tax ID (NIF)' },
} as never;

describe('customer fiscal identifiers', () => {
  it('reads the tax id field as the identifier its label names', () => {
    expect(customerTaxIdKey(algeria)).toBe('nif');
    expect(customerOtherFiscalIds(algeria).map((d) => d.key)).toEqual(['nis', 'rc', 'ai']);
  });

  it('matches the code as a whole word only', () => {
    // "AI" is inside no word of "Tax ID (NIF)"; "Matricule fiscal (MF)" names MF.
    const tunisia = {
      fiscalIds: [{ key: 'mf', code: 'MF', labels: {}, required: false, showOnReceipt: true }],
      customerTaxIdLabels: { fr: 'Matricule fiscal', en: 'Tax ID (MF)' },
    } as never;
    expect(customerTaxIdKey(tunisia)).toBe('mf');
    const none = { fiscalIds: algeriaIds, customerTaxIdLabels: { fr: 'Identifiant' } } as never;
    expect(customerTaxIdKey(none)).toBeNull();
    expect(customerOtherFiscalIds(none).length).toBe(4);
  });

  it('prints every identifier in the profile order, the tax id in its own place, blanks left out', () => {
    const lines = customerFiscalIdLines(algeria, ' 002609081352527 ', { rc: '26B0813525 00/09', ai: '21000478402', nis: ' ' });
    expect(lines.map((l) => `${l.code} ${l.value}`)).toEqual(['NIF 002609081352527', 'RC 26B0813525 00/09', 'AI 21000478402']);
  });

  it('prints nothing for a customer with none', () => {
    expect(customerFiscalIdLines(algeria, null, null)).toEqual([]);
  });
});
