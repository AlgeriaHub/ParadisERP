import { RentalContractRow } from './rentals-api.service';
import { rentalTotals } from './rental-totals';

describe('rentalTotals', () => {
  const row = (status: string, isLocked: boolean, stillOutQty = 0): RentalContractRow =>
    ({
      id: status, number: status, customerId: 'k', customerName: null, branchId: 'b', status, isLocked,
      lockedAtUtc: null, signedAtUtc: '2026-10-06T00:00:00Z', closedAtUtc: null,
      issuedQty: stillOutQty, settledQty: 0, stillOutQty, chargeTotal: 0, overdueTotal: 0,
    }) as RentalContractRow;

  it('counts only live contracts: a cancelled draft is not one still to go out', () => {
    const totals = rentalTotals([row('Open', false), row('Cancelled', false), row('Closed', true), row('Open', true, 4)]);
    expect(totals.drafts).toBe(1);
    expect(totals.openWithGoods).toBe(1);
    expect(totals.stillOut).toBe(4);
  });
});
