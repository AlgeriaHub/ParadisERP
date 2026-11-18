import { documentTax, taxByRate } from './document-tax';

describe('documentTax — the mirror of DocumentTax.On', () => {
  it('charges each rate on its own total, so two rates on one sheet each round once', () => {
    // Concrete at 19 % and haulage at 9 % on the same invoice: 1000.005 × 19 % would round
    // differently line by line (190.00095 → 190.00 + …) than on the grouped base.
    const lines = [
      { lineNet: 333.335, taxRate: 19 },
      { lineNet: 666.67, taxRate: 19 },
      { lineNet: 100, taxRate: 9 },
      { lineNet: 50, taxRate: 0 },
    ];
    const buckets = taxByRate(lines, 2);
    expect(buckets.map((b) => b.rate)).toEqual([19, 9, 0]);
    expect(buckets[0]).toEqual({ rate: 19, base: 1000.01, tax: 190 });
    expect(buckets[1]).toEqual({ rate: 9, base: 100, tax: 9 });
    expect(buckets[2]).toEqual({ rate: 0, base: 50, tax: 0 });
    expect(documentTax(lines, 2)).toBe(199);
  });

  it('agrees with the server on the figure that decides an invoice: 10 × 12 000 at 19 %', () => {
    expect(documentTax([{ lineNet: 120_000, taxRate: 19 }], 2)).toBe(22_800);
  });

  it('rounds through the currency, not to two places', () => {
    // A millime currency: 7 % on 12.345 is 0.86415 → 0.864, not 0.86.
    expect(taxByRate([{ lineNet: 12.345, taxRate: 7 }], 3)[0].tax).toBe(0.864);
  });

  it('is empty for no lines and zero for an untaxed sheet', () => {
    expect(taxByRate([], 2)).toEqual([]);
    expect(documentTax([{ lineNet: 500, taxRate: 0 }], 2)).toBe(0);
  });
});
