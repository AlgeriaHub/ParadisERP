import { withBranch } from './auth.service';
import { PosUser } from './models';
import { sellsUntaxedFrom } from './untaxed-sale';

/**
 * "Sell untaxed at the till" (sales.untaxed; owner, 2026-11-02). Since 2026-11-05 every seller ticks "with VAT" or not
 * (owner), so nothing locks the box; the sign-in's answer is still read and remembered per user.
 */
describe('untaxed sales at the till', () => {
  const cashier = { id: 'u-1', username: 'caisse', fullName: 'Caisse', role: 'Cashier', pinHash: 'h', isActive: true, version: 1 } as PosUser;

  it('reads the sign-in\'s capabilities, and nothing from an answer that carries none', () => {
    expect(sellsUntaxedFrom(['customers.create', 'sales.untaxed'])).toBeTrue();
    expect(sellsUntaxedFrom(['customers.create'])).toBeFalse();
    expect(sellsUntaxedFrom([])).toBeFalse();
    expect(sellsUntaxedFrom(undefined)).toBeUndefined();
    expect(sellsUntaxedFrom(null)).toBeUndefined();
  });

  it('remembers the answer per user, so a PIN unlock after a pull rewrote the row still knows', () => {
    // Another user's session: the branch is not laid over, the untaxed answer is.
    const unlocked = withBranch(cashier, {
      sessionBranch: { userId: 'u-2', branchId: 'b-2', locked: true },
      untaxedByUser: { 'u-1': false, 'u-2': true },
    });
    expect(unlocked.sellsUntaxed).toBeFalse();
    expect(unlocked.branchId).toBeUndefined();

    // Their own session: both.
    const own = withBranch(cashier, { sessionBranch: { userId: 'u-1', branchId: 'b-1', locked: true }, untaxedByUser: { 'u-1': true } });
    expect([own.sellsUntaxed, own.branchId]).toEqual([true, 'b-1']);

    // Never answered on this browser: left as it was.
    const unknown = withBranch(cashier, { sessionBranch: null });
    expect(unknown.sellsUntaxed).toBeUndefined();
  });
});
