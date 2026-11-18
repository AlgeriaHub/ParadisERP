import { PosUser } from './models';

/** The server's `Permissions.CashBox`, spelled exactly as the claim is. */
export const CASH_BOX_PERMISSION = 'treasury.cash_box';

/**
 * Who handles the branch's money on a voucher (owner, 2026-10-02, answer 25): a manager or an admin
 * by their role, and a cashier who has been granted the cash box. The server decides; this only
 * keeps the screens from offering what it would refuse.
 */
export function handlesCash(user: Pick<PosUser, 'role'> & { permissions?: readonly string[] } | null | undefined): boolean {
  if (!user) return false;
  return user.role === 'Manager' || user.role === 'Admin' || (user.permissions ?? []).includes(CASH_BOX_PERMISSION);
}

/** A cashier who holds the cash box: the back office opens on the vouchers and nothing else. */
export function cashBoxOnly(user: Pick<PosUser, 'role'> & { permissions?: readonly string[] } | null | undefined): boolean {
  return !!user && user.role === 'Cashier' && (user.permissions ?? []).includes(CASH_BOX_PERMISSION);
}
