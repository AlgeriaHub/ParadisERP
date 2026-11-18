/**
 * The capability the login reports for "sell untaxed at the till" (Domain/Capabilities.SellUntaxed,
 * permission `sales.untaxed`; TAX-01, finding E#1; owner, 2026-11-02).
 */
export const SELL_UNTAXED = 'sales.untaxed';

/**
 * What an online sign-in's capabilities say about selling untaxed; undefined when the answer carries no
 * capabilities at all (a server from before them), which says nothing either way.
 */
export function sellsUntaxedFrom(capabilities: readonly string[] | null | undefined): boolean | undefined {
  return Array.isArray(capabilities) ? capabilities.includes(SELL_UNTAXED) : undefined;
}

// Since 2026-11-05 every seller ticks "with VAT" or not, as the customer asks for a tax invoice (owner): the till's box
// is never locked, whatever the permission says. The sign-in's answer is still read and remembered (auth.service).
