import { roundMoney } from './money';

/**
 * Tax on a document, the one way every document in this system charges it — the mirror of
 * `AlgeriaPos.Application.Regional.DocumentTax`.
 *
 * <p>On each rate's own total, not line by line: rounding each line first and adding up drifts
 * by centimes from the figure the customer checks and the business declares. A printed invoice
 * rebuilds its per-rate table with this so it agrees with the `taxTotal` the server stored, to
 * the centime, whatever currency decimals the profile has.</p>
 */

export interface TaxBucket {
  /** Percent — 19 means 19 %. */
  rate: number;
  /** Σ lineNet of the lines at this rate. */
  base: number;
  /** base × rate / 100, rounded once. */
  tax: number;
}

/**
 * Digits carried beyond the currency while a rate's lines are added up. A line net is a
 * quantity times a price; a quantity in thousandths (7.5 m³, 0.125 t) leaves three more
 * places than the price has, and that is what has to survive the sum intact.
 */
const GUARD_DIGITS = 3;

/**
 * A rate's base, summed and rounded the way the server's decimals do it.
 *
 * <p>Adding binary floats and rounding the result is not the same operation as adding decimals:
 * 333.335 + 666.67 is a hair under 1000.005 in a double and rounds down to 1000.00, where the
 * server's decimal lands on 1000.01. The sum is therefore carried in integers of a thousandth of
 * a minor unit and rounded half away from zero in integer arithmetic, which cannot drift.</p>
 */
function sumToMoney(values: readonly number[], decimals: number): number {
  const unit = 10 ** GUARD_DIGITS;
  const scaled = values.reduce((sum, v) => sum + Math.round(v * 10 ** (decimals + GUARD_DIGITS)), 0);
  const whole = Math.trunc(scaled / unit);
  const rest = Math.abs(scaled - whole * unit);
  const minor = rest * 2 >= unit ? whole + Math.sign(scaled) : whole;
  return minor / 10 ** decimals;
}

/** The per-rate table, highest rate first; a zero rate is kept with tax 0 so the base still shows. */
export function taxByRate(lines: readonly { lineNet: number; taxRate: number }[], decimals: number): TaxBucket[] {
  const byRate = new Map<number, number[]>();
  for (const line of lines) {
    const rate = line.taxRate || 0;
    byRate.set(rate, [...(byRate.get(rate) ?? []), line.lineNet]);
  }
  return [...byRate]
    .map(([rate, nets]) => {
      const base = sumToMoney(nets, decimals);
      return {
        rate,
        base,
        tax: rate > 0 && base !== 0 ? roundMoney((base * rate) / 100, decimals) : 0,
      };
    })
    .sort((a, b) => b.rate - a.rate);
}

/** What `DocumentTax.On` answers: the buckets' tax added up. */
export function documentTax(lines: readonly { lineNet: number; taxRate: number }[], decimals: number): number {
  return roundMoney(taxByRate(lines, decimals).reduce((sum, b) => sum + b.tax, 0), decimals);
}
