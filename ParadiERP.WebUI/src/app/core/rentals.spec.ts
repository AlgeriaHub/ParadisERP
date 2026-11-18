import {
  chargedDays,
  depositHeld,
  depositVisible,
  durationOf,
  extensionProblem,
  fifoMatch,
  IssueBatch,
  isRetroactive,
  localDay,
  overdueUnitPrice,
  periodEndsOn,
  relieve,
  rentalAmount,
  RentalExtension,
  RentalLine,
  RentalSettings,
  ReturnEntry,
  returnProblem,
  settleContract,
} from './rentals';

/**
 * The same cases as ParadiERP.Tests/Rentals on the server, with the same figures. Scaffolding at
 * 100 a day; overdue at 1.5x is 150 a day; 2,000 a unit if it is lost.
 */
const line = (over: Partial<RentalLine> = {}): RentalLine => ({
  id: 'line-1',
  qty: 100,
  durationDays: 30,
  rentUnitPrice: 100,
  damageUnitPrice: 2000,
  overdueUnitPrice: 150,
  parentLineId: null,
  ...over,
});

const issued = (issueNoteId: string, issuedOn: string, qty: number, lineId = 'line-1'): IssueBatch => ({
  issueNoteId,
  lineId,
  issuedOn,
  qty,
});

const back = (returnedOn: string, over: Partial<ReturnEntry> = {}): ReturnEntry => ({
  returnNoteId: `r-${returnedOn}`,
  lineId: 'line-1',
  returnedOn,
  qtyOk: 0,
  qtyPartialDamage: 0,
  maintenanceValue: 0,
  qtyTotalDamage: 0,
  qtyMissing: 0,
  ...over,
});

describe('rental days and prices (mirrors the server)', () => {
  for (const [qty, days, rate, total] of [[1, 1, 5, 5], [1, 30, 5, 150], [100, 30, 5, 15000], [2, 30, 1000, 60000]]) {
    it(`calculates ${qty} × ${days} × ${rate} = ${total}`, () => {
      expect(rentalAmount(rate, days, qty)).toBe(total);
    });
  }

  it('sums the rental line totals', () => {
    expect(rentalAmount(5, 30, 100) + rentalAmount(100, 10, 2)).toBe(17000);
  });
  it('runs the period from the issue note, with a one-day minimum', () => {
    const l = line({ qty: 5 });
    const same = settleContract({
      lines: [l],
      issues: [issued('i1', '2026-03-01', 5)],
      returns: [back('2026-03-01', { qtyOk: 5 })],
      extensions: [],
    });

    expect(same.charges[0].days).toBe(1);
    expect(same.totals.Rent).toBe(500);
  });

  it('charges the same daily rate whatever the calendar says', () => {
    // The identical fortnight, in February and in March.
    expect(rentalAmount(100, 14, 1)).toBe(1400);
    expect(periodEndsOn('2026-03-01', 30)).toBe('2026-03-31');
  });

  it('freezes the overdue price as a multiple of the rental price', () => {
    expect(overdueUnitPrice(3000, 1.5)).toBe(4500);
    expect(overdueUnitPrice(1000.03, 1.5)).toBe(1500.05);
  });

  it('dates a note by the company day, not by UTC', () => {
    // 00:30 in Algiers is still the previous day in UTC.
    expect(localDay('2026-03-01T23:30:00Z', 'Africa/Algiers')).toBe('2026-03-02');
  });
});

describe('matching returns to issues', () => {
  it('closes the oldest batch first and reports what was never issued', () => {
    const older = issued('older', '2026-03-01', 60);
    const newer = issued('newer', '2026-03-10', 40);

    const matches = fifoMatch([newer, older], [70, 40]);
    expect(matches[0].takes.map((t) => [t.batch.issueNoteId, t.qty])).toEqual([
      ['older', 60],
      ['newer', 10],
    ]);
    expect(matches[1].unmatched).toBe(10);
  });

  it('decides how many days each returned unit is charged for', () => {
    const settlement = settleContract({
      lines: [line()],
      issues: [issued('older', '2026-03-01', 60), issued('newer', '2026-03-10', 40)],
      returns: [back('2026-03-20', { qtyOk: 70 })],
      extensions: [],
    });

    expect(settlement.charges.map((c) => [c.issueNoteId, c.qty, c.days, c.amount])).toEqual([
      ['older', 60, 19, 114_000],
      ['newer', 10, 10, 10_000],
    ]);
    expect(settlement.stillOutQty).toBe(30);
    expect(settlement.isClosed).toBeFalse();
  });
});

describe('an extension is a decision, overdue is a consequence', () => {
  const issue = issued('i1', '2026-03-01', 1);
  const returned = back('2026-04-10', { qtyOk: 1 });
  const l = line({ qty: 1 });

  it('prices ten days over as overdue when nothing was signed', () => {
    const settlement = settleContract({ lines: [l], issues: [issue], returns: [returned], extensions: [] });

    expect(settlement.totals.Rent).toBe(3000);
    expect(settlement.totals.Overdue).toBe(1500);
    expect(settlement.chargeTotal).toBe(4500);
  });

  it('prices the same ten days at the rental rate when they were agreed in advance', () => {
    const extension: RentalExtension = {
      id: 'x1',
      lineId: 'line-1',
      extraDays: 10,
      unitPrice: 100,
      raisedOn: '2026-03-25',
      reason: null,
    };
    expect(isRetroactive(extension, periodEndsOn(issue.issuedOn, 30))).toBeFalse();

    const settlement = settleContract({ lines: [l], issues: [issue], returns: [returned], extensions: [extension] });

    expect(settlement.totals.Rent).toBe(4000);
    expect(settlement.totals.Overdue).toBe(0);
    // If these two came to the same figure, nobody would ever sign an extension again.
    expect(settlement.chargeTotal).toBeLessThan(4500);
    expect(settlement.charges[1].extensionId).toBe('x1');
  });

  it('keeps a retroactive extension a manager decision, written down', () => {
    const late: RentalExtension = {
      id: 'x2',
      lineId: null,
      extraDays: 10,
      unitPrice: 100,
      raisedOn: '2026-04-03',
      reason: null,
    };
    const ends = '2026-03-31';

    expect(extensionProblem(late, ends, false)).toBe('retroactiveNeedsManager');
    expect(extensionProblem(late, ends, true)).toBe('retroactiveNeedsReason');
    expect(extensionProblem({ ...late, reason: 'Site flooded' }, ends, true)).toBeNull();
    expect(extensionProblem({ ...late, raisedOn: '2026-03-25' }, ends, false)).toBeNull();
  });
});

describe('minimum hire (mirrors RentalMinimumDaysTests)', () => {
  const tenFor30 = (minimumDays: number, ...returns: ReturnEntry[]) =>
    settleContract({ lines: [line({ qty: 10 })], issues: [issued('i1', '2026-03-01', 10)], returns, extensions: [], minimumDays });

  it('charges the days out, never below the minimum or above the period for it', () => {
    expect(chargedDays(5, 30, 0)).toBe(5);
    expect(chargedDays(5, 30, 15)).toBe(15);
    expect(chargedDays(20, 30, 15)).toBe(20);
    expect(chargedDays(3, 7, 15)).toBe(7);
    expect(chargedDays(10, 7, 15)).toBe(10);
  });

  it('holds an early return to the minimum and gives back only the days beyond', () => {
    expect(tenFor30(0, back('2026-03-06', { qtyOk: 10 })).unusedRent).toBe(25_000);
    const day5 = tenFor30(15, back('2026-03-06', { qtyOk: 10 }));
    expect(day5.totals.Rent).toBe(15_000);
    expect(day5.unusedRent).toBe(15_000);
    const day20 = tenFor30(15, back('2026-03-21', { qtyOk: 10 }));
    expect(day20.totals.Rent).toBe(20_000);
    expect(day20.unusedRent).toBe(10_000);
  });

  it('holds each part returned to the minimum on its own days', () => {
    const s = tenFor30(15, back('2026-03-06', { qtyOk: 4 }), back('2026-03-21', { qtyOk: 3 }), back('2026-03-31', { qtyOk: 3 }));
    expect(s.charges.map((c) => c.days)).toEqual([15, 20, 30]);
    expect(s.unusedRent).toBe(9_000);
  });
});

describe('early return, damage and loss', () => {
  it('gives back the unused period, off the debt first and only then as a voucher', () => {
    const settlement = settleContract({
      lines: [line({ qty: 10 })],
      issues: [issued('i1', '2026-03-01', 10)],
      returns: [back('2026-03-11', { qtyOk: 10 })],
      extensions: [],
    });

    expect(settlement.lines[0].issuedPeriodRent).toBe(30_000);
    expect(settlement.totals.Rent).toBe(10_000);
    expect(settlement.unusedRent).toBe(20_000);

    expect(relieve(20_000, 0)).toEqual({ debtReduction: 0, paymentVoucher: 20_000 });
    expect(relieve(20_000, 50_000)).toEqual({ debtReduction: 20_000, paymentVoucher: 0 });
    expect(relieve(20_000, 5_000)).toEqual({ debtReduction: 5_000, paymentVoucher: 15_000 });
  });

  it('leaves the rest out on a partial return and closes the line on the second', () => {
    const settlement = settleContract({
      lines: [line()],
      issues: [issued('i1', '2026-03-01', 100)],
      returns: [back('2026-03-15', { qtyOk: 40 }), back('2026-03-31', { qtyOk: 60 })],
      extensions: [],
    });

    expect(settlement.charges.map((c) => c.amount)).toEqual([56_000, 180_000]);
    expect(settlement.unusedRent).toBe(64_000);
    expect(settlement.lines[0].issuedPeriodRent).toBe(300_000);
    expect(settlement.totals.Rent + settlement.unusedRent).toBe(300_000);
    expect(settlement.isClosed).toBeTrue();
  });

  it('charges forty units short at the price signed on the contract', () => {
    const settlement = settleContract({
      lines: [line()],
      issues: [issued('i1', '2026-03-01', 100)],
      returns: [back('2026-03-11', { qtyOk: 60, qtyMissing: 40 })],
      extensions: [],
    });

    expect(settlement.totals.Missing).toBe(80_000);
    // The forty that are gone were on site those ten days like everything else.
    expect(settlement.totals.Rent).toBe(100_000);
    expect(settlement.isClosed).toBeTrue();
  });

  it('charges an accessory nothing to hire and everything it loses', () => {
    const scaffold = line({ id: 'scaffold', qty: 20, durationDays: 45 });
    const couplers = line({
      id: 'couplers',
      qty: 500,
      durationDays: 1,
      rentUnitPrice: 0,
      damageUnitPrice: 500,
      overdueUnitPrice: 0,
      parentLineId: 'scaffold',
    });

    // A coupler cannot expire before the scaffold it is bolted to.
    expect(durationOf(couplers, [scaffold, couplers])).toBe(45);

    const settlement = settleContract({
      lines: [scaffold, couplers],
      issues: [issued('i1', '2026-03-01', 500, 'couplers')],
      returns: [{ ...back('2026-04-20', { qtyOk: 460, qtyMissing: 40 }), lineId: 'couplers' }],
      // An extension on the scaffold reaches the couplers as days, never as price.
      extensions: [{ id: 'x1', lineId: 'scaffold', extraDays: 10, unitPrice: 100, raisedOn: '2026-04-10' }],
    });

    expect(settlement.charges.map((c) => c.kind)).toEqual(['Missing']);
    expect(settlement.totals.Missing).toBe(20_000);
    expect(settlement.totals.Rent).toBe(0);
    expect(settlement.totals.Overdue).toBe(0);
  });

  it('charges the repair of what came back damaged but usable', () => {
    const settlement = settleContract({
      lines: [line({ qty: 20 })],
      issues: [issued('i1', '2026-03-01', 20)],
      returns: [back('2026-03-11', { qtyOk: 12, qtyPartialDamage: 5, maintenanceValue: 1250, qtyTotalDamage: 3 })],
      extensions: [],
    });

    expect(settlement.totals.Maintenance).toBe(1250);
    expect(settlement.totals.Damage).toBe(6000);
    expect(settlement.totals.Rent).toBe(20_000);
    expect(settlement.isClosed).toBeTrue();
  });
});

describe('the figures the customer is shown', () => {
  it('does not drift a centime between the invoice, the charge and the refund', () => {
    // The daily rate is 100/30 (3.3333...). Pricing used and unused days separately gives 23.34
    // against an invoice of 23.33.
    const settlement = settleContract({
      lines: [line({ qty: 1, durationDays: 7, rentUnitPrice: 100 / 30, damageUnitPrice: 0, overdueUnitPrice: 5 })],
      issues: [issued('i1', '2026-03-01', 1)],
      returns: [back('2026-03-03', { qtyOk: 1 })],
      extensions: [],
    });

    expect(settlement.lines[0].issuedPeriodRent).toBe(23.33);
    expect(settlement.totals.Rent).toBe(6.67);
    expect(settlement.unusedRent).toBe(16.66);
    expect(settlement.chargeTotal + settlement.unusedRent).toBe(23.33);
  });

  it('reconciles in a currency that does not have two decimals', () => {
    const contract = {
      lines: [line({ qty: 7, rentUnitPrice: 1000 / 30, damageUnitPrice: 0, overdueUnitPrice: 50 })],
      issues: [issued('i1', '2026-03-01', 7)],
      returns: [back('2026-03-12', { qtyOk: 7 })],
      extensions: [],
    };

    expect(settleContract(contract, 0).totals.Rent).toBe(2567);
    expect(settleContract(contract, 0).unusedRent).toBe(4433);
    expect(settleContract(contract, 2).totals.Rent).toBe(2566.67);
    expect(settleContract(contract, 2).unusedRent).toBe(4433.33);
    expect(settleContract(contract, 3).totals.Rent).toBe(2566.667);
    expect(settleContract(contract, 3).unusedRent).toBe(4433.333);
    expect(settleContract(contract, 3).chargeTotal + settleContract(contract, 3).unusedRent).toBe(7000);
  });
});

describe('closing, and what may be raised at all', () => {
  it('closes on what went out, not on what was contracted', () => {
    const issuedLine = line({ id: 'line-1', qty: 100 });
    const neverIssued = line({ id: 'line-2', qty: 50 });
    const issues = [issued('i1', '2026-03-01', 60)];

    expect(
      settleContract({
        lines: [issuedLine, neverIssued],
        issues,
        returns: [back('2026-03-20', { qtyOk: 60 })],
        extensions: [],
      }).isClosed,
    ).toBeTrue();

    expect(
      settleContract({
        lines: [issuedLine, neverIssued],
        issues,
        returns: [back('2026-03-20', { qtyOk: 59 })],
        extensions: [],
      }).isClosed,
    ).toBeFalse();

    // Nothing issued: the contract has not finished, it has not started.
    expect(settleContract({ lines: [issuedLine, neverIssued], issues: [], returns: [], extensions: [] }).isClosed)
      .toBeFalse();
  });

  it('refuses a return bigger than what went out, and reports it if it is saved anyway', () => {
    const note = back('2026-03-11', { qtyOk: 12 });
    expect(returnProblem(note, 10, 0)).toBe('exceedsIssued');
    expect(returnProblem(back('2026-03-11', { qtyOk: 40 }), 60, 20)).toBeNull();
    expect(returnProblem(back('2026-03-11', { qtyOk: 40, maintenanceValue: 500 }), 60, 0))
      .toBe('maintenanceNeedsDamagedUnits');

    const settlement = settleContract({
      lines: [line()],
      issues: [issued('i1', '2026-03-01', 10)],
      returns: [note],
      extensions: [],
    });
    expect(settlement.lines[0].overReturnedQty).toBe(2);
    expect(settlement.lines[0].settledQty).toBe(10);
  });

  it('keeps the deposit switched off and out of every figure', () => {
    const off: RentalSettings = { overdueMultiplier: 1.5, depositEnabled: false };

    expect(depositVisible(off)).toBeFalse();
    expect(depositHeld(off, 50_000)).toBe(0);
    expect(depositHeld({ ...off, depositEnabled: true }, 50_000)).toBe(50_000);

    const settlement = settleContract({
      lines: [line({ qty: 10 })],
      issues: [issued('i1', '2026-03-01', 10)],
      returns: [back('2026-03-31', { qtyOk: 10 })],
      extensions: [],
    });
    expect(settlement.chargeTotal).toBe(30_000);
    expect(settlement.unusedRent).toBe(0);
  });
});
