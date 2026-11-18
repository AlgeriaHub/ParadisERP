import { FiscalIdDefinition, RegionalSettings } from './models';

/**
 * A customer's fiscal identifiers, as the country profile defines them (owner's Facture N° 45,
 * 2026-10-10: the buyer's RC, NIF and AI under the name).
 *
 * <p>The customer already has one of them in its own field — `taxId`, labelled by the profile's
 * `customerTaxIdLabels` ("NIF" in the Algeria preset). That one is the profile identifier whose
 * code the label names as a whole word; the others are kept in `fiscalIds` by key. A profile whose
 * label names no code keeps every identifier in `fiscalIds` beside the tax id.</p>
 */

/** The profile identifier the customer's `taxId` field stands for, or null when the label names none. */
export function customerTaxIdKey(regional: Pick<RegionalSettings, 'fiscalIds' | 'customerTaxIdLabels'>): string | null {
  const labels = Object.values(regional.customerTaxIdLabels ?? {}).filter((l): l is string => !!l);
  for (const id of regional.fiscalIds ?? []) {
    const code = id.code.trim();
    if (!code) continue;
    const word = new RegExp(`(^|[^\\p{L}\\p{N}])${escape(code)}($|[^\\p{L}\\p{N}])`, 'iu');
    if (labels.some((label) => word.test(label))) return id.key;
  }
  return null;
}

/** The identifiers a customer form asks beside the tax id, in the profile's order. */
export function customerOtherFiscalIds(regional: Pick<RegionalSettings, 'fiscalIds' | 'customerTaxIdLabels'>): FiscalIdDefinition[] {
  const taxKey = customerTaxIdKey(regional);
  return (regional.fiscalIds ?? []).filter((id) => id.key !== taxKey);
}

/** One identifier as a paper prints it: its code and its value. */
export interface CustomerFiscalIdLine {
  key: string;
  code: string;
  value: string;
}

/**
 * Every identifier a customer has, in the profile's order, the tax id in its own place: RC, NIF,
 * AI… as the buyer block prints them. Blank values are left out; a value under a key the profile
 * no longer defines is not printed (it has no code to print it under).
 */
export function customerFiscalIdLines(
  regional: Pick<RegionalSettings, 'fiscalIds' | 'customerTaxIdLabels'>,
  taxId: string | null | undefined,
  fiscalIds: Record<string, string> | null | undefined,
): CustomerFiscalIdLine[] {
  const taxKey = customerTaxIdKey(regional);
  const lines: CustomerFiscalIdLine[] = [];
  for (const id of regional.fiscalIds ?? []) {
    const value = (id.key === taxKey ? taxId : fiscalIds?.[id.key])?.trim() ?? '';
    if (value) lines.push({ key: id.key, code: id.code, value });
  }
  return lines;
}

function escape(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
