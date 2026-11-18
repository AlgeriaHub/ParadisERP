import type { AdminCustomer, OpeningBalanceRequest, OpeningDirection } from './admin.service';
import type { PosUser } from './models';

/**
 * A customer's opening balance on the screen (owner, 2026-10-10: the company goes live today and
 * customers who already owe money start with that balance). The server decides everything —
 * once only, who may, the sign; these helpers only shape what the form sends and read what the
 * login reported.
 */

export interface OpeningParty {
  id: string;
  openingBalance?: number;
  openingBalanceOn?: string | null;
  openingBalanceSetAtUtc?: string | null;
  openingBalanceSetBy?: string | null;
}

/** The server capability that lets a user set an opening balance later and correct one (Domain/Capabilities.cs). */
export const SET_OPENING_BALANCES = 'customers.opening_balance';
export const SET_SUPPLIER_OPENING_BALANCES = 'suppliers.opening_balance';

/** Read from the login, never from a role name; a session from before it existed reads as "not allowed". */
export function canSetOpeningBalance(user: Pick<PosUser, 'capabilities'> | null | undefined): boolean {
  return user?.capabilities?.includes(SET_OPENING_BALANCES) ?? false;
}

export function canSetSupplierOpeningBalance(user: Pick<PosUser, 'capabilities'> | null | undefined): boolean {
  return user?.capabilities?.includes(SET_SUPPLIER_OPENING_BALANCES) ?? false;
}

/** What the inputs hold while the clerk types: the amount stays positive, the direction carries the sign. */
export interface OpeningDraft {
  amount: number | null;
  direction: OpeningDirection;
  /** yyyy-mm-dd in the company's calendar. */
  on: string;
}

export function emptyOpeningDraft(today: string): OpeningDraft {
  return { amount: null, direction: 'owesUs', on: today };
}

/** What to send, or null when there is nothing to send: blank or zero is "owes nothing", not a balance. */
export function openingPayload(draft: OpeningDraft): OpeningBalanceRequest | null {
  if (draft.amount === null || !Number.isFinite(draft.amount) || draft.amount === 0) return null;
  return { amount: draft.amount, direction: draft.direction, on: draft.on || null };
}

/**
 * The i18n key of what is wrong with a draft, or null. The server checks the same and has the last
 * word; this only says it before the round trip. `required` is for the set-later and correction
 * forms, where an empty amount is not "nothing owed" but a missing figure.
 */
export function openingDraftProblem(draft: OpeningDraft, today: string, required = false): string | null {
  if (draft.amount === null || draft.amount === 0) return required ? 'cust.opening.amountRequired' : null;
  if (!Number.isFinite(draft.amount) || draft.amount < 0) return 'cust.opening.amountPositive';
  if (draft.on && draft.on > today) return 'cust.opening.noFuture';
  return null;
}

/** Set means fixed: only a correction changes it from then on. */
export function isOpeningSet(c: Pick<OpeningParty, 'openingBalanceSetAtUtc'> | null | undefined): boolean {
  return !!c?.openingBalanceSetAtUtc;
}

/** The direction a signed figure stands for: customer >= 0 owesUs, supplier >= 0 weOwe. */
export function openingDirectionOf(signed: number, kind: 'customer' | 'supplier' = 'customer'): OpeningDirection {
  if (kind === 'supplier') {
    return signed >= 0 ? 'weOwe' : 'owesUs';
  }
  return signed >= 0 ? 'owesUs' : 'weOwe';
}
