import { FiscalIdDefinition } from '../../core/models';
import { customerFiscalIdsPayload } from './customers-tab.component';

/**
 * A company customer's other fiscal identifiers (the owner's Facture N° 45, 2026-10-10): the form
 * sends what was typed for each identifier the profile asks, trimmed, and nothing for a person.
 */
describe('customerFiscalIdsPayload', () => {
  const defs = [
    { key: 'nis', code: 'NIS', labels: {}, required: false, showOnReceipt: true },
    { key: 'rc', code: 'RC', labels: {}, required: false, showOnReceipt: true },
    { key: 'ai', code: 'AI', labels: {}, required: false, showOnReceipt: true },
  ] as unknown as FiscalIdDefinition[];

  it("sends a company's identifiers, trimmed, one per profile identifier", () => {
    expect(customerFiscalIdsPayload('Company', defs, { rc: ' 26B0813525 00/09 ', ai: '21000478402' })).toEqual({
      nis: '',
      rc: '26B0813525 00/09',
      ai: '21000478402',
    });
  });

  it('sends none for a person or a customer whose kind is not set', () => {
    expect(customerFiscalIdsPayload('Person', defs, { rc: '26B0813525 00/09' })).toEqual({});
    expect(customerFiscalIdsPayload('Unspecified', defs, { rc: '26B0813525 00/09' })).toEqual({});
  });

  it('leaves out a value under a key the profile no longer asks', () => {
    expect(customerFiscalIdsPayload('Company', defs, { siret: '123' })).toEqual({ nis: '', rc: '', ai: '' });
  });
});
