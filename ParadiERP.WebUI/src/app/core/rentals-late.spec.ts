import { dueBack, lateDeductionProblem, lateWaiverProblem, minutesBetween, settleContract } from './rentals';

/**
 * Goods back after their period (the owner, 2026-10-08), with the figures of
 * `ParadiERP.Tests/Rentals/RentalLateReturnTests.cs`: ten units for three days from the 1st at
 * 100 a day, 150 a late day, back on the 5th — one day late.
 */
function backOnThe5th(waivesOverdue: boolean) {
  return settleContract({
    lines: [
      { id: 'l', qty: 10, durationDays: 3, rentUnitPrice: 100, damageUnitPrice: 2000, overdueUnitPrice: 150, parentLineId: null },
    ],
    issues: [{ issueNoteId: 'i', lineId: 'l', issuedOn: '2026-03-01', qty: 10 }],
    returns: [
      {
        returnNoteId: 'r',
        lineId: 'l',
        returnedOn: '2026-03-05',
        qtyOk: 10,
        qtyPartialDamage: 0,
        maintenanceValue: 0,
        qtyTotalDamage: 0,
        qtyMissing: 0,
        waivesOverdue,
      },
    ],
    extensions: [],
  });
}

describe('back after the due time (mirrors RentalLateReturnTests)', () => {
  it('charges the late day as overdue at the price signed on the line', () => {
    const settlement = backOnThe5th(false);
    const late = settlement.charges.filter((c) => c.kind === 'Overdue');
    expect(late.map((c) => [c.days, c.unitPrice, c.amount])).toEqual([[1, 150, 1500]]);
    expect(settlement.totals.Rent).toBe(3000);
  });

  it('waived, charges no late day and leaves the agreed period as it was', () => {
    const settlement = backOnThe5th(true);
    expect(settlement.charges.some((c) => c.kind === 'Overdue')).toBeFalse();
    expect(settlement.totals.Rent).toBe(3000);
    expect(settlement.unusedRent).toBe(0);
    expect(settlement.isClosed).toBeTrue();
  });

  it('is due on the last day at the cutoff hour, or by the end of it with no cutoff', () => {
    expect(dueBack('2026-09-30', 12)).toEqual({ day: '2026-09-30', hour: 12, minute: 0 });
    expect(dueBack('2026-09-30', null)).toEqual({ day: '2026-10-01', hour: 0, minute: 0 });
    expect(dueBack('2026-09-30', undefined)).toEqual({ day: '2026-10-01', hour: 0, minute: 0 });
  });

  it('counts the minutes from the due time to the return, across days', () => {
    const due = dueBack('2026-09-30', 12);
    expect(minutesBetween(due, { day: '2026-09-30', hour: 15, minute: 0 })).toBe(180);
    expect(minutesBetween(due, { day: '2026-10-02', hour: 9, minute: 30 })).toBe(2 * 1440 - 150);
    expect(minutesBetween(due, { day: '2026-09-30', hour: 9, minute: 0 })).toBe(-180);
  });

  it('writes a waiver down and asks nothing of a charge', () => {
    expect(lateWaiverProblem(false, '')).toBe('lateWaiverNeedsReason');
    expect(lateWaiverProblem(false, '   ')).toBe('lateWaiverNeedsReason');
    expect(lateWaiverProblem(false, 'Client fidèle')).toBeNull();
    expect(lateWaiverProblem(true, null)).toBeNull();
    expect(lateWaiverProblem(null, null)).toBeNull();
  });
});

/**
 * Yes, less a deduction (the owner, 2026-10-22), with the figures of the same C# tests: ten units
 * for three days from the 1st at 100 a day, 150 a late day, back on the 8th — four late days, 6,000.
 */
function backOnThe8th(deduction: { lateDaysWaived?: number; lateAmountWaived?: number }) {
  return settleContract({
    lines: [
      { id: 'l', qty: 10, durationDays: 3, rentUnitPrice: 100, damageUnitPrice: 2000, overdueUnitPrice: 150, parentLineId: null },
    ],
    issues: [{ issueNoteId: 'i', lineId: 'l', issuedOn: '2026-03-01', qty: 10 }],
    returns: [
      {
        returnNoteId: 'r',
        lineId: 'l',
        returnedOn: '2026-03-08',
        qtyOk: 10,
        qtyPartialDamage: 0,
        maintenanceValue: 0,
        qtyTotalDamage: 0,
        qtyMissing: 0,
        lateDaysWaived: deduction.lateDaysWaived ?? null,
        lateAmountWaived: deduction.lateAmountWaived ?? null,
        lateAmountFull: deduction.lateAmountWaived == null ? null : 6000,
      },
    ],
    extensions: [],
  });
}

describe('back late, charged less a deduction (mirrors RentalLateReturnTests)', () => {
  it('in days, takes that many late days off and charges the rest at the signed price', () => {
    const settlement = backOnThe8th({ lateDaysWaived: 1 });
    expect(settlement.charges.filter((c) => c.kind === 'Overdue').map((c) => [c.days, c.unitPrice, c.amount])).toEqual([
      [3, 150, 4500],
    ]);
    expect(settlement.totals.Rent).toBe(3000);
  });

  it('of every late day, charges none of them', () => {
    expect(backOnThe8th({ lateDaysWaived: 4 }).charges.some((c) => c.kind === 'Overdue')).toBeFalse();
  });

  it('as an amount, charges every late day at the same share of its price', () => {
    const settlement = backOnThe8th({ lateAmountWaived: 1000 });
    expect(settlement.charges.filter((c) => c.kind === 'Overdue').map((c) => [c.days, c.unitPrice, c.amount])).toEqual([
      [4, 125, 5000],
    ]);
  });

  it('as an amount spread over lines, charges exactly what was decided whatever the rounding', () => {
    const ids = ['a', 'b', 'c'];
    const settlement = settleContract({
      lines: ids.map((id) => ({
        id, qty: 1, durationDays: 3, rentUnitPrice: 100, damageUnitPrice: 2000, overdueUnitPrice: 100, parentLineId: null,
      })),
      issues: ids.map((id) => ({ issueNoteId: 'i' + id, lineId: id, issuedOn: '2026-03-01', qty: 1 })),
      returns: ids.map((id) => ({
        returnNoteId: 'r', lineId: id, returnedOn: '2026-03-05', qtyOk: 1, qtyPartialDamage: 0, maintenanceValue: 0,
        qtyTotalDamage: 0, qtyMissing: 0, lateAmountWaived: 100, lateAmountFull: 300,
      })),
      extensions: [],
    });
    const late = settlement.charges.filter((c) => c.kind === 'Overdue').map((c) => c.amount).sort();
    expect(late).toEqual([66.66, 66.67, 66.67]);
    expect(settlement.totals.Overdue).toBe(200);
  });

  it('is at least a day or a unit of money, and never more than the lateness', () => {
    const problem = (charge: boolean | null, days: number | null, amount: number | null) =>
      lateDeductionProblem(charge, days, amount, 4, 6000, 2);
    expect(problem(true, null, null)).toBeNull();
    expect(problem(true, 1, null)).toBeNull();
    expect(problem(true, 4, null)).toBeNull();
    expect(problem(true, 0, null)).toBe('lateDeductionDaysInvalid');
    expect(problem(true, 5, null)).toBe('lateDeductionDaysInvalid');
    expect(problem(true, 1.5, null)).toBe('lateDeductionDaysInvalid');
    expect(problem(true, null, 0.01)).toBeNull();
    expect(problem(true, null, 6000)).toBeNull();
    expect(problem(true, null, 0)).toBe('lateDeductionAmountInvalid');
    expect(problem(true, null, 6000.01)).toBe('lateDeductionAmountInvalid');
    expect(problem(true, null, 10.005)).toBe('lateDeductionAmountInvalid');
    expect(problem(true, 1, 100)).toBe('lateDeductionInvalid');
    expect(problem(false, 1, null)).toBe('lateDeductionInvalid');
  });

  it('is written down like a waiver', () => {
    expect(lateWaiverProblem(true, '', true)).toBe('lateWaiverNeedsReason');
    expect(lateWaiverProblem(true, 'Client fidèle', true)).toBeNull();
  });
});
