import { Lang, PaymentMethod, Product, RegionalSettings, SaleLine, TaxMode } from './models';
import { formatNumber, isNumberFormat } from './number-format';
import { localized } from './regional.service';

/** Quantities keep two decimals whatever the currency. */
export function round2(value: number): number {
  return roundMoney(value, 2);
}

/** Rounds half away from zero to the currency's decimals, matching the server. */
export function roundMoney(value: number, decimals = 2): number {
  const factor = 10 ** decimals;
  return (Math.sign(value) * Math.round((Math.abs(value) + Number.EPSILON) * factor)) / factor;
}

/**
 * e.g. "1,250.00 DA", with the company's number format (core/number-format.ts: 1,234.56 unless the company chose
 * another), "€12.50" when the symbol comes first. Latin digits keep receipts readable on thermal printers.
 */
export function formatMoney(value: number, lang: Lang, regional: RegionalSettings, symbolText?: string): string {
  const decimals = regional.currencyDecimals;
  const rounded = roundMoney(value || 0, decimals);
  const amount = formatNumber(
    rounded,
    `${lang}-${regional.countryCode}`,
    { minimumFractionDigits: decimals, maximumFractionDigits: decimals },
    isNumberFormat(regional.numberFormat) ? regional.numberFormat : undefined,
  );
  const symbol = symbolText || localized(regional.currencySymbols, lang) || regional.currencyCode;
  return regional.currencySymbolFirst ? `${symbol} ${amount}` : `${amount} ${symbol}`;
}

export function productName(product: Product, lang: Lang): string {
  const name =
    lang === 'ar' ? product.nameAr : lang === 'en' ? product.nameEn : product.nameFr;
  return name || product.nameFr || product.nameEn || product.nameAr || product.sku;
}

/**
 * Splits a line total into its base (excluding tax) and tax.
 *
 * When shelf prices already include tax the tax is extracted from the gross:
 * tax = gross x rate / (100 + rate). Otherwise it is added on top.
 */
export function splitTax(
  amount: number,
  taxRate: number,
  pricesIncludeTax: boolean,
  decimals = 2,
): { ht: number; tax: number; ttc: number } {
  const r = (v: number) => roundMoney(v, decimals);
  if (!taxRate) return { ht: r(amount), tax: 0, ttc: r(amount) };

  if (pricesIncludeTax) {
    const tax = r((amount * taxRate) / (100 + taxRate));
    return { ht: r(amount - tax), tax, ttc: r(amount) };
  }
  const tax = r((amount * taxRate) / 100);
  return { ht: r(amount), tax, ttc: r(amount + tax) };
}

export interface CartItem {
  productId: string;
  sku: string;
  name: string;
  qty: number;
  unitPrice: number;
  taxRate: number;
  unit: string;
}

export interface CartTotals {
  lines: SaleLine[];
  subTotalHt: number;
  taxTotal: number;
  discountTotal: number;
  grandTotalTtc: number;
  taxBuckets: { rate: number; ht: number; tax: number }[];
}

/**
 * Prices a cart, distributing an order-level discount across the lines pro rata so that
 * each line's tax is computed on its discounted base. That keeps the tax breakdown correct
 * for the fiscal return, and keeps sum(lineTotalTtc) === grandTotalTtc, which is exactly
 * what the server re-checks when the sale is pushed.
 */
export function priceCart(
  items: CartItem[],
  discount: { type: 'none' | 'percent' | 'amount'; value: number },
  pricesIncludeTax: boolean,
  decimals = 2,
  taxMode: TaxMode = 'Taxed',
): CartTotals {
  const r = (v: number) => roundMoney(v, decimals);
  const gross = items.reduce((sum, item) => sum + item.qty * item.unitPrice, 0);

  let discountTotal = 0;
  if (discount.type === 'percent' && discount.value > 0) {
    discountTotal = r((gross * Math.min(discount.value, 100)) / 100);
  } else if (discount.type === 'amount' && discount.value > 0) {
    discountTotal = r(Math.min(discount.value, gross));
  }

  const lines: SaleLine[] = [];
  // Net of discount and of tax, per line. Kept beside the lines rather than on them: it is
  // derivable from what is stored (lineTotalTtc − lineTaxAmount) and does not belong in the
  // shape that gets persisted and pushed.
  const netHt: number[] = [];
  let allocated = 0;

  items.forEach((item, index) => {
    const lineGross = r(item.qty * item.unitPrice);

    // Give the last line whatever centimes remain, so the parts always sum to the whole.
    const isLast = index === items.length - 1;
    const share = gross > 0 ? (lineGross / gross) * discountTotal : 0;
    const lineDiscount = isLast ? r(discountTotal - allocated) : r(share);
    allocated = r(allocated + lineDiscount);

    const net = r(lineGross - lineDiscount);

    lines.push({
      id: crypto.randomUUID(),
      productId: item.productId,
      sku: item.sku,
      nameSnapshot: item.name,
      qty: item.qty,
      unitPrice: item.unitPrice,
      // An untaxed document has no rate at all, not a rate that happens to yield zero:
      // the receipt and the tax report both read this to decide what to show and count.
      taxRate: taxMode === 'Untaxed' ? 0 : item.taxRate,
      lineDiscount,
      // Filled in below, once the rate's own total is known.
      lineTotalTtc: net,
      lineTaxAmount: 0,
    });
    netHt.push(net);
  });

  // Tax is charged on the invoice total, not line by line.
  //
  // Rounding each line's tax and adding them up drifts from the tax on the sum by a few
  // centimes on a long ticket, and it is the figure on the total that the customer checks
  // and that gets declared. Lines are grouped by rate first so a ticket carrying two rates
  // still shows each one correctly; with a single rate — which is every ticket in this
  // trade — this is exactly "tax on the invoice total".
  const bucketMap = new Map<number, { rate: number; ht: number; tax: number }>();
  lines.forEach((line, index) => {
    const bucket = bucketMap.get(line.taxRate) ?? { rate: line.taxRate, ht: 0, tax: 0 };
    bucket.ht = r(bucket.ht + netHt[index]);
    bucketMap.set(line.taxRate, bucket);
  });
  for (const bucket of bucketMap.values()) {
    const settled = splitTax(bucket.ht, bucket.rate, pricesIncludeTax, decimals);
    bucket.ht = settled.ht;
    bucket.tax = settled.tax;
  }

  // Give each line its share of its rate's tax, largest remainder on the last line of the
  // group, so the lines still sum to the invoice the customer is shown.
  for (const bucket of bucketMap.values()) {
    const group = lines
      .map((line, index) => ({ line, net: netHt[index] }))
      .filter((x) => x.line.taxRate === bucket.rate);
    const groupNet = group.reduce((sum, x) => sum + x.net, 0);
    let allocatedTax = 0;
    group.forEach(({ line, net }, index) => {
      const isLastOfRate = index === group.length - 1;
      const share = groupNet > 0 ? (net / groupNet) * bucket.tax : 0;
      line.lineTaxAmount = isLastOfRate ? r(bucket.tax - allocatedTax) : r(share);
      allocatedTax = r(allocatedTax + line.lineTaxAmount);
      line.lineTotalTtc = pricesIncludeTax ? net : r(net + line.lineTaxAmount);
    });
  }

  const taxTotal = r([...bucketMap.values()].reduce((sum, b) => sum + b.tax, 0));
  const grandTotalTtc = r(lines.reduce((sum, l) => sum + l.lineTotalTtc, 0));
  const subTotalHt = r(grandTotalTtc - taxTotal);

  return {
    lines,
    subTotalHt,
    taxTotal,
    discountTotal,
    grandTotalTtc,
    taxBuckets: [...bucketMap.values()].sort((a, b) => b.rate - a.rate),
  };
}

export interface TenderPart {
  method: PaymentMethod;
  amount: number;
  /** Gift card code for GiftCard parts. */
  reference?: string | null;
  /**
   * The tender definition this part was taken under. Built-in tenders repeat their own name
   * here; an operator-defined one (a postal transfer, a cheque) has no built-in name, so it
   * rides as 'Mixed' in `method` and is told apart by this key.
   *
   * Settlement deliberately ignores it. A generic tender is money handed over with no side
   * effect and no change given, which is exactly how every non-cash part already behaves —
   * so the split maths needs no new case, and the server resolves the key on push.
   */
  methodKey?: string;
  /** Cheque or transfer details, when the tender definition asks for them. */
  instrumentNumber?: string | null;
  instrumentBank?: string | null;
  instrumentDueDate?: string | null;
}

export type SplitProblem = 'needsTwo' | 'insufficient' | 'changeOnlyCash';

export interface SplitSettlement {
  /** Amounts actually kept per method: cash parts are reduced by the change given back. */
  parts: TenderPart[];
  /** What the customer handed over, credit excluded. */
  paid: number;
  change: number;
}

/**
 * Settles a payment split across several methods. Only cash can be over-tendered, so the
 * change always comes out of the cash parts; card and credit amounts are taken as they are.
 */
export function settleSplit(parts: TenderPart[], total: number, decimals = 2): SplitSettlement | { problem: SplitProblem } {
  const r = (v: number) => roundMoney(v, decimals);
  const tolerance = 10 ** -decimals / 2;
  const used = parts.map((p) => ({ ...p, amount: r(Number(p.amount) || 0) })).filter((p) => p.amount > 0);
  if (used.length < 2) return { problem: 'needsTwo' };
  const sum = r(used.reduce((s, p) => s + p.amount, 0));
  if (sum + tolerance < total) return { problem: 'insufficient' };
  const change = r(Math.max(0, sum - total));
  const cash = used.filter((p) => p.method === 'Cash').reduce((s, p) => s + p.amount, 0);
  if (change > cash + tolerance) return { problem: 'changeOnlyCash' };

  // The parts are reported as tendered, change and all. The server takes the change off the
  // drawer itself, so taking it off here as well removed it twice — the defect that left every
  // branch's recorded cash short by the change it had handed out.
  return {
    parts: used,
    paid: r(used.filter((p) => p.method !== 'Credit').reduce((s, p) => s + p.amount, 0)),
    change,
  };
}

/** Receipt number that is unique without a server: device code + zero-padded local sequence. */
export function buildReference(deviceCode: string, localSeq: number): string {
  return `${deviceCode}-${String(localSeq).padStart(6, '0')}`;
}
