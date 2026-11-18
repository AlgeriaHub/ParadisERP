import { ApiError } from '../../../core/api.service';
import { PayrollSettings } from './payroll-api.service';
import {
  PayrollElement,
  PayrollRunStatus,
  availableActions,
  elementLabel,
  groupElements,
  hasPostingPlaceholder,
  labelBook,
  labelIn,
  locksAttendance,
  parsePeriod,
  parseRefusal,
  periodInput,
  periodLabel,
  postingPreview,
  stillOwed,
  totalWithheld,
} from './payroll-run-view';

/**
 * The rules the run screens and the payslip lean on, tested where they live rather than through
 * a template. Each of these mirrors something the server decided — the lifecycle it enforces,
 * the entry it will post, the sentence it refuses with — so a screen that says it before the
 * request is saying what the server would have said.
 */

function element(over: Partial<PayrollElement> & Pick<PayrollElement, 'key' | 'kind'>): PayrollElement {
  return {
    amount: 0,
    qty: null,
    rate: null,
    inSocialBase: true,
    inTaxBase: true,
    sortOrder: 0,
    ...over,
  };
}

describe('payroll-run-view elements', () => {
  it('Elements_group_by_kind_in_the_payslip_order', () => {
    const groups = groupElements([
      element({ key: 'fine', kind: 'Deduction', amount: -500, sortOrder: 40 }),
      element({ key: 'night', kind: 'Overtime', amount: 1200, sortOrder: 30 }),
      element({ key: 'base', kind: 'Base', amount: 40000, sortOrder: 0 }),
      element({ key: 'bonus', kind: 'Adjustment', amount: 2000, sortOrder: 50 }),
      element({ key: 'transport', kind: 'Allowance', amount: 3000, sortOrder: 20 }),
      element({ key: 'absence', kind: 'Absence', amount: -1500, sortOrder: 10 }),
    ]);

    expect(groups.map((g) => g.kind)).toEqual(['Base', 'Absence', 'Allowance', 'Overtime', 'Deduction', 'Adjustment']);
    expect(groups[0].total).toBe(40000);
    expect(groups[4].total).toBe(-500);
  });

  it('keeps the engine order inside a group and leaves an empty kind out', () => {
    const groups = groupElements([
      element({ key: 'meal', kind: 'Allowance', amount: 1000, sortOrder: 22 }),
      element({ key: 'transport', kind: 'Allowance', amount: 3000, sortOrder: 21 }),
    ]);

    expect(groups.length).toBe(1);
    expect(groups[0].elements.map((e) => e.key)).toEqual(['transport', 'meal']);
    expect(groups[0].total).toBe(4000);
  });

  it('treats no elements at all as no groups rather than as six empty ones', () => {
    expect(groupElements(null)).toEqual([]);
    expect(groupElements([])).toEqual([]);
  });
});

describe('payroll-run-view labels', () => {
  const settings = {
    allowances: [{ key: 'transport', labels: { fr: 'Prime de transport', ar: 'منحة النقل' } }],
    overtimeRules: [{ key: 'night', labels: { fr: 'Heures de nuit' } }],
    deductions: [{ key: 'fine', labels: { fr: 'Retenue', en: 'Deduction' } }],
    socialNames: { fr: 'CNAS', ar: 'الضمان الاجتماعي' },
    incomeTaxNames: { fr: 'IRG' },
  } as unknown as PayrollSettings;

  const t = (key: string) => (key === 'hr.element.base' ? 'Salaire de base' : key === 'hr.element.absence' ? 'Absence' : key);

  it('Labels_resolve_from_the_run_snapshot_in_the_requested_language_and_fall_back_to_French', () => {
    const book = labelBook(settings);

    expect(elementLabel({ key: 'transport', kind: 'Allowance' }, book, 'ar', t)).toBe('منحة النقل');
    // No Arabic wording for the night band: French rather than a blank cell on the payslip.
    expect(elementLabel({ key: 'night', kind: 'Overtime' }, book, 'ar', t)).toBe('Heures de nuit');
    expect(labelIn(book.social, 'ar')).toBe('الضمان الاجتماعي');
    expect(labelIn(book.incomeTax, 'ar')).toBe('IRG');
  });

  it('names the two elements the engine writes itself from the dictionary', () => {
    const book = labelBook(settings);
    expect(elementLabel({ key: 'base', kind: 'Base' }, book, 'fr', t)).toBe('Salaire de base');
    expect(elementLabel({ key: 'absence', kind: 'Absence' }, book, 'fr', t)).toBe('Absence');
  });

  it('prints the key of an element nobody named, and survives settings that never arrived', () => {
    const book = labelBook(settings);
    expect(elementLabel({ key: 'housing', kind: 'Allowance' }, book, 'fr', t)).toBe('housing');
    expect(elementLabel({ key: 'transport', kind: 'Allowance' }, labelBook(null), 'fr', t)).toBe('transport');
  });

  it('falls back to whatever wording exists when neither the language nor French is filled in', () => {
    expect(labelIn({ en: 'Deduction' }, 'ar')).toBe('Deduction');
    expect(labelIn({ fr: '   ' }, 'fr')).toBe('');
    expect(labelIn(undefined, 'fr')).toBe('');
  });
});

describe('payroll-run-view lifecycle', () => {
  it('offers compute on a draft, three actions on a locked run and only a reversal on an approved one', () => {
    expect(availableActions('Draft')).toEqual(['compute']);
    expect(availableActions('Locked')).toEqual(['compute', 'unlock', 'approve']);
    expect(availableActions('Approved')).toEqual(['reverse']);
  });

  it('offers nothing on a paid or a reversed run: a paid run is corrected in the next one', () => {
    expect(availableActions('Paid')).toEqual([]);
    expect(availableActions('Reversed')).toEqual([]);
  });

  it('says the attendance sheet is locked for every state but a draft and a reversal', () => {
    const states: PayrollRunStatus[] = ['Draft', 'Locked', 'Approved', 'Paid', 'Reversed'];
    expect(states.filter(locksAttendance)).toEqual(['Locked', 'Approved', 'Paid']);
  });
});

describe('payroll-run-view periods', () => {
  it('writes and reads the month box, and refuses what is not a month', () => {
    expect(periodInput({ year: 2026, month: 3 })).toBe('2026-03');
    expect(parsePeriod('2026-03')).toEqual({ year: 2026, month: 3 });
    expect(parsePeriod('2026-13')).toBeNull();
    expect(parsePeriod('2026-00')).toBeNull();
    expect(parsePeriod('')).toBeNull();
    expect(parsePeriod('mars')).toBeNull();
  });

  it('says the period in words, in Latin digits, in every language', () => {
    for (const lang of ['fr', 'ar', 'en']) {
      const label = periodLabel({ year: 2026, month: 3 }, lang);
      expect(label).toContain('2026');
      expect(/[٠-٩۰-۹]/.test(label)).toBe(false);
    }
    // A language code no browser will accept as a locale still prints a period.
    expect(periodLabel({ year: 2026, month: 3 }, 'not-a-locale')).toContain('2026');
  });
});

describe('payroll-run-view posting', () => {
  const accrual = [
    { accountCode: '631', debit: 100000, credit: 0 },
    { accountCode: '635', debit: 26000, credit: 0 },
    { accountCode: '431', debit: 0, credit: 35000 },
    { accountCode: '442', debit: 0, credit: 8000 },
    { accountCode: '425', debit: 0, credit: 5000 },
    { accountCode: '421', debit: 0, credit: 78000 },
  ];

  it('splits the entry into the two sides the action bar names', () => {
    const preview = postingPreview(accrual);
    expect(preview.debitCodes).toBe('631, 635');
    expect(preview.creditCodes).toBe('431, 442, 425, 421');
    expect(preview.debitTotal).toBe(126000);
    expect(preview.creditTotal).toBe(126000);
    expect(preview.balanced).toBe(true);
  });

  it('calls an entry that has not been drawn yet unbalanced rather than balanced at zero', () => {
    expect(postingPreview([]).balanced).toBe(false);
    expect(postingPreview(null).debitCodes).toBe('');
  });

  it('shows the unconfirmed-account placeholder instead of tidying it into a credit', () => {
    const withPlaceholder = [...accrual, { accountCode: '641', debit: -500, credit: 0 }];
    const preview = postingPreview(withPlaceholder);
    expect(preview.debitCodes).toBe('631, 635, 641');
    expect(hasPostingPlaceholder(withPlaceholder)).toBe(true);
    expect(hasPostingPlaceholder(accrual)).toBe(false);
  });
});

describe('payroll-run-view figures', () => {
  it('adds up everything withheld from one payslip', () => {
    expect(
      totalWithheld({ socialEmployee: 3500, incomeTax: 1200.5, advanceRecovered: 5000, otherDeductions: 300 }),
    ).toBe(10000.5);
  });

  it('says what is still owed after a part payment, without a floating tail', () => {
    expect(stillOwed({ net: 45678.9, paidAmount: 20000 })).toBe(25678.9);
    expect(stillOwed({ net: 45678.9, paidAmount: 45678.9 })).toBe(0);
  });
});

describe('payroll-run-view refusals', () => {
  it('reads the missing figure and the permission that can set it out of the 409', () => {
    const error = new ApiError(
      JSON.stringify({
        code: 'income_tax_not_configured',
        missing: ['incomeTaxBrackets'],
        fixableBy: 'payroll.edit_statutory',
        message: 'The income tax scale is not configured.',
      }),
      409,
    );

    const refusal = parseRefusal(error);
    expect(refusal?.code).toBe('income_tax_not_configured');
    expect(refusal?.missing).toEqual(['incomeTaxBrackets']);
    expect(refusal?.fixableBy).toBe('payroll.edit_statutory');
  });

  it('leaves an ordinary sentence alone: a conflict is not always a refusal', () => {
    expect(parseRefusal(new ApiError('A run already exists for this branch and period.', 409))).toBeNull();
    expect(parseRefusal(new ApiError('{ not json', 409))).toBeNull();
    expect(parseRefusal(new ApiError('{"message":"no code"}', 409))).toBeNull();
    expect(parseRefusal(null)).toBeNull();
  });
});
