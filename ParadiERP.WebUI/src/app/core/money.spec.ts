import { CartItem, buildReference, formatMoney, priceCart, round2, roundMoney, settleSplit, splitTax } from './money';
import { DEFAULT_REGIONAL, roundCash } from './regional.service';

function item(over: Partial<CartItem> = {}): CartItem {
  return {
    productId: crypto.randomUUID(),
    sku: 'P001',
    name: 'Article',
    qty: 1,
    unitPrice: 100,
    taxRate: 19,
    unit: 'pcs',
    ...over,
  };
}

describe('tax maths', () => {
  it('extracts tax from a tax-inclusive price', () => {
    // 119 incl. tax at 19% => 100 base + 19 tax
    const { ht, tax, ttc } = splitTax(119, 19, true);
    expect(tax).toBe(19);
    expect(ht).toBe(100);
    expect(ttc).toBe(119);
  });

  it('adds tax on top of a tax-exclusive price', () => {
    const { ht, tax, ttc } = splitTax(100, 19, false);
    expect(ht).toBe(100);
    expect(tax).toBe(19);
    expect(ttc).toBe(119);
  });

  it('handles the 9% reduced rate used for staples', () => {
    const { tax } = splitTax(109, 9, true);
    expect(tax).toBe(9);
  });

  it('treats an exempt product as zero-rated', () => {
    const { ht, tax } = splitTax(250, 0, true);
    expect(tax).toBe(0);
    expect(ht).toBe(250);
  });
});

describe('priceCart', () => {
  it('sums lines and splits tax per rate', () => {
    const totals = priceCart(
      [item({ unitPrice: 119, qty: 2, taxRate: 19 }), item({ unitPrice: 109, qty: 1, taxRate: 9 })],
      { type: 'none', value: 0 },
      true,
    );

    expect(totals.grandTotalTtc).toBe(347);
    expect(totals.taxTotal).toBe(47);
    expect(totals.subTotalHt).toBe(300);
    expect(totals.taxBuckets.map((b) => b.rate)).toEqual([19, 9]);
  });

  it('keeps sum(lineTotalTtc) equal to grandTotalTtc — the server re-checks this', () => {
    const totals = priceCart(
      [
        item({ unitPrice: 33.33, qty: 3 }),
        item({ unitPrice: 17.77, qty: 7, taxRate: 9 }),
        item({ unitPrice: 5.55, qty: 11, taxRate: 0 }),
      ],
      { type: 'percent', value: 7 },
      true,
    );

    const lineSum = round2(totals.lines.reduce((sum, l) => sum + l.lineTotalTtc, 0));
    expect(lineSum).toBe(totals.grandTotalTtc);

    const taxSum = round2(totals.lines.reduce((sum, l) => sum + l.lineTaxAmount, 0));
    expect(taxSum).toBe(totals.taxTotal);
  });

  it('distributes a percentage discount across lines without losing centimes', () => {
    const totals = priceCart(
      [item({ unitPrice: 100, qty: 1 }), item({ unitPrice: 200, qty: 1 }), item({ unitPrice: 33, qty: 1 })],
      { type: 'percent', value: 10 },
      true,
    );

    const distributed = round2(totals.lines.reduce((sum, l) => sum + l.lineDiscount, 0));
    expect(distributed).toBe(totals.discountTotal);
    expect(totals.discountTotal).toBe(33.3);
    expect(totals.grandTotalTtc).toBe(299.7);
  });

  it('never discounts more than the cart is worth', () => {
    const totals = priceCart([item({ unitPrice: 50, qty: 1 })], { type: 'amount', value: 999 }, true);
    expect(totals.discountTotal).toBe(50);
    expect(totals.grandTotalTtc).toBe(0);
  });

  it('prices an empty cart as zero', () => {
    const totals = priceCart([], { type: 'percent', value: 20 }, true);
    expect(totals.grandTotalTtc).toBe(0);
    expect(totals.lines.length).toBe(0);
  });
});

describe('currency settings', () => {
  it('rounds to three decimals for a millime currency and keeps totals consistent', () => {
    const totals = priceCart(
      [item({ unitPrice: 1.235, qty: 3, taxRate: 19 }), item({ unitPrice: 0.777, qty: 7, taxRate: 7 })],
      { type: 'percent', value: 3 },
      true,
      3,
    );
    const lineSum = roundMoney(totals.lines.reduce((sum, l) => sum + l.lineTotalTtc, 0), 3);
    expect(lineSum).toBe(totals.grandTotalTtc);
    expect(totals.grandTotalTtc).toBe(roundMoney((1.235 * 3 + 0.777 * 7) * 0.97, 3));
  });

  it('rounds to whole units for a currency without decimals', () => {
    expect(splitTax(1000, 18, false, 0)).toEqual({ ht: 1000, tax: 180, ttc: 1180 });
    expect(roundMoney(2.5, 0)).toBe(3);
    expect(roundMoney(-2.5, 0)).toBe(-3);
  });

  it('formats with the profile symbol and position, in the system’s number format by default', () => {
    // 5,300,256.25 in every language (owner, 2026-11-09).
    expect(formatMoney(5300256.25, 'fr', DEFAULT_REGIONAL)).toBe('5,300,256.25 DA');
    expect(formatMoney(5300256.25, 'ar', DEFAULT_REGIONAL)).toBe('5,300,256.25 دج');
    const euro = { ...DEFAULT_REGIONAL, countryCode: 'FR', currencyCode: 'EUR', currencySymbols: { en: '€' }, currencySymbolFirst: true };
    expect(formatMoney(12.5, 'en', euro)).toBe('€ 12.50');
    const dinar = { ...DEFAULT_REGIONAL, countryCode: 'TN', currencyDecimals: 3, currencySymbols: { fr: 'DT' } };
    expect(formatMoney(1.5, 'fr', dinar)).toBe('1.500 DT');
  });

  it('writes the marks the company chose, or each language’s own', () => {
    const with_ = (numberFormat: string) => ({ ...DEFAULT_REGIONAL, numberFormat });
    expect(formatMoney(5300256.25, 'fr', with_('1.234,56'))).toBe('5.300.256,25 DA');
    expect(formatMoney(5300256.25, 'fr', with_('1 234,56'))).toBe('5\u202f300\u202f256,25 DA');
    expect(formatMoney(1250, 'fr', with_('locale'))).toMatch(/^1\s?250,00 DA$/);
    // An unknown value is the system's own.
    expect(formatMoney(1250, 'fr', with_('nonsense'))).toBe('1,250.00 DA');
  });
});

describe('cash rounding', () => {
  const profile = (step: number, mode: 'Nearest' | 'Up' | 'Down', decimals = 2) => ({
    ...DEFAULT_REGIONAL,
    currencyDecimals: decimals,
    cashRoundingIncrement: step,
    cashRoundingMode: mode,
  });

  it('is off when the step is zero', () => {
    expect(roundCash(12.34, profile(0, 'Nearest'))).toBe(12.34);
  });

  it('rounds to the nearest step, halves away from zero', () => {
    expect(roundCash(12.32, profile(0.05, 'Nearest'))).toBe(12.3);
    expect(roundCash(12.325, profile(0.05, 'Nearest'))).toBe(12.35);
    expect(roundCash(12.35, profile(0.05, 'Nearest'))).toBe(12.35);
    expect(roundCash(1247, profile(5, 'Nearest', 0))).toBe(1245);
  });

  it('rounds up or down on request without drifting on exact multiples', () => {
    expect(roundCash(12.31, profile(0.05, 'Up'))).toBe(12.35);
    expect(roundCash(12.35, profile(0.05, 'Up'))).toBe(12.35);
    expect(roundCash(12.39, profile(0.05, 'Down'))).toBe(12.35);
    expect(roundCash(0.7, profile(0.1, 'Down'))).toBe(0.7);
  });
});

describe('split payments', () => {
  it('needs at least two non-zero parts', () => {
    expect(settleSplit([{ method: 'Cash', amount: 100 }, { method: 'Card', amount: 0 }], 100)).toEqual({ problem: 'needsTwo' });
  });

  it('refuses parts that do not cover the total', () => {
    expect(settleSplit([{ method: 'Cash', amount: 40 }, { method: 'Card', amount: 50 }], 100)).toEqual({ problem: 'insufficient' });
  });

  it('gives change out of cash only', () => {
    // The parts come back as tendered — 50 in cash, not 50 less the 30 given back. The server
    // subtracts the change from the drawer itself; this used to subtract it here as well, so
    // every cash sale recorded the branch short by exactly the change it had handed out.
    expect(settleSplit([{ method: 'Card', amount: 80 }, { method: 'Cash', amount: 50 }], 100)).toEqual({
      parts: [{ method: 'Card', amount: 80 }, { method: 'Cash', amount: 50 }],
      paid: 130,
      change: 30,
    });
    expect(settleSplit([{ method: 'Card', amount: 90 }, { method: 'Cash', amount: 5 }, { method: 'Credit', amount: 20 }], 100))
      .toEqual({ problem: 'changeOnlyCash' });
  });

  it('keeps credit out of the amount handed over', () => {
    const settled = settleSplit([{ method: 'Credit', amount: 60.5 }, { method: 'Cash', amount: 39.5 }], 100);
    expect(settled).toEqual({ parts: [{ method: 'Credit', amount: 60.5 }, { method: 'Cash', amount: 39.5 }], paid: 39.5, change: 0 });
  });
});

describe('buildReference', () => {
  it('namespaces receipt numbers by terminal so offline tills cannot collide', () => {
    expect(buildReference('C01', 42)).toBe('C01-000042');
    expect(buildReference('C02', 42)).toBe('C02-000042');
  });
});

describe('tax on the invoice total', () => {
  const item = (sku: string, unitPrice: number, qty = 1, taxRate = 19): CartItem => ({
    productId: sku,
    sku,
    name: sku,
    unit: 'u',
    qty,
    unitPrice,
    taxRate,
  });
  const none = { type: 'none' as const, value: 0 };

  it('charges the rate on the sum rather than on each line', () => {
    // Three lines whose individual tax each lands on a half-centime. Rounding them one by
    // one and adding up drifts from the tax the customer checks on the total.
    const items = [item('A', 10.53), item('B', 10.53), item('C', 10.53)];
    const totals = priceCart(items, none, false, 2);

    const net = 31.59;
    expect(totals.subTotalHt).toBe(net);
    expect(totals.taxTotal).toBe(round2((net * 19) / 100));
    expect(totals.grandTotalTtc).toBe(round2(net + totals.taxTotal));
  });

  it('still sums the lines to the invoice', () => {
    const items = [item('A', 10.53), item('B', 10.53), item('C', 10.53)];
    const totals = priceCart(items, none, false, 2);

    const lineSum = round2(totals.lines.reduce((s, l) => s + l.lineTotalTtc, 0));
    const taxSum = round2(totals.lines.reduce((s, l) => s + l.lineTaxAmount, 0));
    expect(lineSum).toBe(totals.grandTotalTtc);
    // The server rejects a push whose lines disagree with the declared total, so this has
    // to hold exactly and not merely to within a centime.
    expect(taxSum).toBe(totals.taxTotal);
  });

  it('keeps two rates apart on one invoice', () => {
    const totals = priceCart([item('A', 100, 1, 19), item('B', 100, 1, 9)], none, false, 2);

    expect(totals.taxBuckets.map((b) => b.rate)).toEqual([19, 9]);
    expect(totals.taxTotal).toBe(28);
    expect(totals.grandTotalTtc).toBe(228);
  });

  it('charges tax on the discounted amount, not the list price', () => {
    const totals = priceCart([item('A', 1000)], { type: 'percent', value: 10 }, false, 2);

    expect(totals.discountTotal).toBe(100);
    expect(totals.subTotalHt).toBe(900);
    expect(totals.taxTotal).toBe(171);
    expect(totals.grandTotalTtc).toBe(1071);
  });

  it('adds nothing to an untaxed invoice', () => {
    const items = [item('A', 1000), item('B', 500, 1, 9)];
    const totals = priceCart(items, none, false, 2, 'Untaxed');

    // Prices are stored net, so an untaxed document is simply the net with nothing added.
    expect(totals.taxTotal).toBe(0);
    expect(totals.grandTotalTtc).toBe(1500);
    expect(totals.subTotalHt).toBe(1500);
    // The rate is cleared rather than left sitting at 19 with a zero beside it: the receipt
    // and the tax report both read it to decide what to print and what to count.
    expect(totals.lines.every((l) => l.taxRate === 0)).toBe(true);
    expect(totals.taxBuckets.every((b) => b.rate === 0)).toBe(true);
  });

  it('leaves tax-inclusive pricing extracting rather than adding', () => {
    const totals = priceCart([item('A', 1190)], { type: 'none', value: 0 }, true, 2);

    expect(totals.grandTotalTtc).toBe(1190);
    expect(totals.taxTotal).toBe(190);
    expect(totals.subTotalHt).toBe(1000);
  });
});
