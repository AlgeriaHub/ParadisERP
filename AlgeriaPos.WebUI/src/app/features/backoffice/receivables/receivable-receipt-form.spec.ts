import { receiptProblem } from './receivable-receipt-form.component';

/**
 * The refusals the receipt form predicts before the server is asked, in the server's own
 * order — except the reference and instrument rules, which only the form enforces because the
 * server stores those fields without looking at them.
 */
describe('receiptProblem', () => {
  const ok = {
    customerId: 'c1',
    branchId: 'b1',
    methodKey: 'cash',
    amount: 100,
    requiresReference: false,
    reference: '',
    requiresInstrument: false,
    instrumentNumber: '',
    instrumentBank: '',
    allocations: [{ allocated: 60, allocatable: 80 }],
  };

  it('accepts a complete draft', () => {
    expect(receiptProblem(ok)).toBeNull();
  });

  it('asks for the who, where and how before the how much', () => {
    expect(receiptProblem({ ...ok, customerId: null })).toBe('customerRequired');
    expect(receiptProblem({ ...ok, branchId: null })).toBe('branchRequired');
    expect(receiptProblem({ ...ok, methodKey: null })).toBe('methodRequired');
    expect(receiptProblem({ ...ok, amount: null })).toBe('amountRequired');
    expect(receiptProblem({ ...ok, amount: 0 })).toBe('amountRequired');
  });

  it('enforces the tender rules the server does not', () => {
    expect(receiptProblem({ ...ok, requiresReference: true, reference: '  ' })).toBe('referenceRequired');
    expect(receiptProblem({ ...ok, requiresReference: true, reference: 'CHQ 42' })).toBeNull();
    expect(receiptProblem({ ...ok, requiresInstrument: true, instrumentNumber: '42', instrumentBank: '' })).toBe(
      'instrumentRequired',
    );
    expect(
      receiptProblem({ ...ok, requiresInstrument: true, instrumentNumber: '42', instrumentBank: 'BNA' }),
    ).toBeNull();
  });

  it('refuses an allocation above the row before one above the receipt', () => {
    expect(receiptProblem({ ...ok, allocations: [{ allocated: 90, allocatable: 80 }] })).toBe('allocationExceeds');
    expect(
      receiptProblem({
        ...ok,
        amount: 100,
        allocations: [
          { allocated: 80, allocatable: 80 },
          { allocated: 30, allocatable: 50 },
        ],
      }),
    ).toBe('overAllocated');
    // Money left unallocated is allowed: it goes on account, and the form says so.
    expect(receiptProblem({ ...ok, allocations: [] })).toBeNull();
  });

  it('judges money at the currency precision, as the server does with decimals', () => {
    // 43085.29 + 45439.5 is 88524.79000000001 in binary; the receipt covers both invoices exactly.
    expect(
      receiptProblem({
        ...ok,
        amount: 88524.79,
        allocations: [
          { allocated: 43085.29, allocatable: 43085.29 },
          { allocated: 45439.5, allocatable: 45439.5 },
        ],
      }),
    ).toBeNull();
    // outstanding 2643.01 − claimed 1454.51 is 1188.5000000000002; the screen prints 1 188,50
    // and typing that figure back must be accepted.
    expect(
      receiptProblem({ ...ok, amount: 1188.5, allocations: [{ allocated: 1188.5, allocatable: 2643.01 - 1454.51 }] }),
    ).toBeNull();
    // 375878.35 − 241847.07 is 134031.27999999997; the printed 134 031,28 is not "above" it.
    expect(
      receiptProblem({
        ...ok,
        amount: 134031.28,
        allocations: [{ allocated: 134031.28, allocatable: 375878.35 - 241847.07 }],
      }),
    ).toBeNull();
    // A real cent over is still refused.
    expect(
      receiptProblem({ ...ok, amount: 1188.51, allocations: [{ allocated: 1188.51, allocatable: 2643.01 - 1454.51 }] }),
    ).toBe('allocationExceeds');
    // A millime currency judges at three places.
    expect(receiptProblem({ ...ok, amount: 10.001, allocations: [{ allocated: 10.001, allocatable: 10 }] }, 3)).toBe(
      'allocationExceeds',
    );
  });
});
