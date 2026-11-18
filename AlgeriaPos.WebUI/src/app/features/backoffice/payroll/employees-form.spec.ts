import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { AdminService } from '../../../core/admin.service';
import { ApiError } from '../../../core/api.service';
import { AuthService } from '../../../core/auth.service';
import { isolate } from '../../../core/bidi';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { ConfirmService } from '../../../ui/confirm.service';
import { HrEmployeesComponent, notAnEmail } from './employees.component';
import { Employee, HrApi, UpsertEmployee } from './hr-api.service';
import { PayrollApi } from './payroll-api.service';

const BRANCHES = [{ id: 'alger', nameFr: 'Alger', nameAr: 'الجزائر', nameEn: 'Algiers', isActive: true }];

/** Someone filed in two parts (PinSay #301): the first word is the first name, the rest the family name. */
const person = (id: string, fullName: string, payBasis: 'Daily' | 'Monthly', baseRate: number, email: string | null = null): Employee => ({
  id, number: 'EMP-00000' + id.slice(-1), fullName, branchId: 'alger', branchName: 'Alger', payBasis, baseRate,
  payoutMethod: 'Cash', hiredOn: '2026-01-01', isActive: true, openAdvances: 0, email,
  firstName: fullName.split(' ')[0], familyName: fullName.split(' ').slice(1).join(' ') || fullName,
} as Employee);

/** The refusal body the personnel routes answer with: a code the screen translates, English for the log. */
const refusal = (code: string) => new ApiError(JSON.stringify({ code, message: 'English for the log' }), 400);

/**
 * The employee form after the owner's answers 41/47 (2026-10-05): no daily workers on payroll —
 * a day worker is paid by a payment voucher under Daily labour — and an optional email, stored only.
 */
describe('HrEmployeesComponent — monthly only, and an optional email', () => {
  let fixture: ComponentFixture<HrEmployeesComponent>;
  let rows: Employee[];
  let created: UpsertEmployee[];
  let updated: [string, UpsertEmployee][];
  let refuse: ApiError | null;

  const setUp = async () => {
    TestBed.configureTestingModule({
      imports: [HrEmployeesComponent],
      providers: [
        {
          provide: HrApi,
          useValue: {
            employees: () => Promise.resolve({ total: rows.length, skip: 0, take: 50, rows }),
            createEmployee: (body: UpsertEmployee) => {
              created.push(body);
              return refuse ? Promise.reject(refuse) : Promise.resolve({ ...person('e9', body.fullName, body.payBasis, body.baseRate), number: 'EMP-000009' });
            },
            updateEmployee: (id: string, body: UpsertEmployee) => {
              updated.push([id, body]);
              return refuse ? Promise.reject(refuse) : Promise.resolve({ ...person(id, body.fullName, body.payBasis, body.baseRate) });
            },
          },
        },
        { provide: PayrollApi, useValue: { settings: () => Promise.resolve({ settings: { allowances: [], defaultAttendanceMode: 'Declared' } }) } },
        { provide: AdminService, useValue: { listBranches: () => Promise.resolve(BRANCHES) } },
        { provide: AuthService, useValue: { currentUser: signal({ id: 'me', role: 'Admin', permissions: [], capabilities: [] }) } },
        { provide: Router, useValue: { navigate: () => Promise.resolve(true) } },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    fixture = TestBed.createComponent(HrEmployeesComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  };

  beforeEach(() => {
    rows = [person('e1', 'Ahmed', 'Monthly', 45000), person('e2', 'Nadia', 'Monthly', 38000)];
    created = [];
    updated = [];
    refuse = null;
  });

  const i18n = () => TestBed.inject(I18nService);
  const el = () => fixture.nativeElement as HTMLElement;
  const q = (selector: string) => el().querySelector(selector) as HTMLElement | null;
  const text = (selector: string) => q(selector)?.textContent?.replace(/\s+/g, ' ').trim();
  const rateLabel = () => el().querySelector('label[for="em-rate"]')?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const listHeaders = () => {
    const tables = [...el().querySelectorAll('table')] as HTMLTableElement[];
    // The register is the table that lists people; the allowances table sits in the form.
    const register = tables.find((t) => t.textContent?.includes(i18n().t('hr.employees.number')))!;
    return [...register.querySelectorAll('th')].map((th) => th.textContent?.trim());
  };
  const fillNew = () => {
    const c = fixture.componentInstance;
    c.openCreate();
    c.form.firstName = 'Karim';
    c.form.familyName = 'Haddad';
    c.form.branchId = 'alger';
    c.form.hiredOn = '2026-10-01';
    c.form.baseRate = 40000;
    fixture.detectChanges();
  };

  describe('the pay basis', () => {
    it('offers no basis on a new employee: no Daily, no hint, and the amount is the monthly salary', async () => {
      await setUp();
      fixture.componentInstance.openCreate();
      fixture.detectChanges();
      const form = q('.bo-edit-modal')!;
      const words = form.textContent ?? '';
      expect(words).not.toContain(i18n().t('hr.basis.Daily'));
      expect(words).not.toContain('عمال اليومية');
      expect(form.querySelector('[data-basis]')).toBeNull();
      expect(form.querySelector('[data-daily-retired]')).toBeNull();
      expect(form.querySelector('#em-pay-monthly')).toBeNull();
      expect(rateLabel()).toContain(i18n().t('hr.employees.monthlySalary'));
      expect('hr.employees.basisHint' in BACKOFFICE_TRANSLATIONS.fr).toBeFalse();
    });

    it('sends a new employee as Monthly', async () => {
      await setUp();
      fillNew();
      await fixture.componentInstance.save();
      expect(created.length).toBe(1);
      expect(created[0].payBasis).toBe('Monthly');
      expect(created[0].baseRate).toBe(40000);
    });

    it('keeps the list free of a basis column when everyone listed is monthly, and heads the figures as the monthly salary', async () => {
      await setUp();
      const cols = listHeaders();
      expect(cols).not.toContain(i18n().t('hr.employees.basis'));
      expect(cols).toContain(i18n().t('hr.employees.monthlySalary'));
    });

    it('shows the basis column while a legacy daily employee is listed, naming who is paid by the day', async () => {
      rows = [...rows, person('e3', 'Rachid', 'Daily', 1500)];
      await setUp();
      const cols = listHeaders();
      expect(cols).toContain(i18n().t('hr.employees.basis'));
      expect(cols).toContain(i18n().t('hr.employees.rate'));
      const basisCells = [...el().querySelectorAll(`td[data-label="${i18n().t('hr.employees.basis')}"]`)].map((td) => td.textContent?.trim());
      expect(basisCells).toEqual([i18n().t('hr.basis.Monthly'), i18n().t('hr.basis.Monthly'), i18n().t('hr.basis.Daily')]);
    });

    it('shows a monthly employee’s edit with no basis at all', async () => {
      await setUp();
      fixture.componentInstance.edit(rows[0]);
      fixture.detectChanges();
      expect(q('[data-basis]')).toBeNull();
      expect(q('#em-pay-monthly')).toBeNull();
      expect(rateLabel()).toContain(i18n().t('hr.employees.monthlySalary'));
    });

    describe('a legacy daily employee', () => {
      const daily = () => person('e3', 'Rachid', 'Daily', 1500);

      beforeEach(async () => {
        rows = [...rows, daily()];
        await setUp();
        fixture.componentInstance.edit(daily());
        fixture.detectChanges();
      });

      it('reads "paid by the day" as text, with the warning and the way out in words', () => {
        expect(text('[data-basis]')).toBe(i18n().t('hr.employees.basis.daily'));
        const notice = q('[data-daily-retired]')!;
        expect(notice.classList).toContain('warn');
        expect(notice.textContent?.trim()).toBe(i18n().t('hr.employees.dailyRetired'));
        expect(rateLabel()).toContain(i18n().t('hr.employees.dailyRate'));
        expect((q('#em-pay-monthly') as HTMLInputElement).checked).toBeFalse();
        expect(text('#em-pay-monthly-hint')).toBe(i18n().t('hr.employees.keepsDaily'));
        // No choice to make Daily anything: the only control is the way to monthly.
        expect(el().querySelectorAll('.bo-edit-modal .bo-chips button[aria-pressed]').length).toBe(0);
      });

      it('stays daily, at its own rate, when saved without "Pay monthly"', async () => {
        const c = fixture.componentInstance;
        await c.save();
        expect(updated.length).toBe(1);
        expect(updated[0][1].payBasis).toBe('Daily');
        expect(updated[0][1].baseRate).toBe(1500);
      });

      it('"Pay monthly" switches to monthly, relabels the amount and clears the daily rate', async () => {
        const box = q('#em-pay-monthly') as HTMLInputElement;
        box.click();
        fixture.detectChanges();
        await fixture.whenStable();
        fixture.detectChanges();
        const c = fixture.componentInstance;
        expect(c.form.payBasis).toBe('Monthly');
        expect(c.form.baseRate).toBeNull();
        expect((q('#em-rate') as HTMLInputElement).value).toBe('');
        expect(rateLabel()).toContain(i18n().t('hr.employees.monthlySalary'));
        expect(text('#em-pay-monthly-hint')).toBe(i18n().t('hr.employees.payMonthlyHint'));
        expect(text('[data-basis]')).toBe(i18n().t('hr.basis.Monthly'));
      });

      it('will not save the switch until the monthly salary is typed, and then sends it as Monthly', async () => {
        const c = fixture.componentInstance;
        c.setPayMonthly(true);
        fixture.detectChanges();
        const save = el().querySelector('.bo-edit-modal .bo-panel__foot .btn-primary') as HTMLButtonElement;
        expect(save.disabled).toBeTrue();
        await c.save();
        fixture.detectChanges();
        expect(updated).toEqual([]);
        expect(text('#em-rate-error')).toBe(i18n().t('common.fieldRequired'));

        const rate = q('#em-rate') as HTMLInputElement;
        rate.value = '42000';
        rate.dispatchEvent(new Event('input'));
        fixture.detectChanges();
        expect(save.disabled).toBeFalse();
        await c.save();
        expect(updated[0][1].payBasis).toBe('Monthly');
        expect(updated[0][1].baseRate).toBe(42000);
      });

      it('undoing "Pay monthly" puts the stored daily rate back', () => {
        const c = fixture.componentInstance;
        c.setPayMonthly(true);
        c.setPayMonthly(false);
        expect(c.form.payBasis).toBe('Daily');
        expect(c.form.baseRate).toBe(1500);
      });
    });

    it('says the server’s refusal of a daily worker in words, in the reader’s language', async () => {
      await setUp();
      refuse = refusal('payBasisDailyRetired');
      fillNew();
      await fixture.componentInstance.save();
      fixture.detectChanges();
      expect(fixture.componentInstance.error()).toBe(i18n().t('hr.problem.payBasisDailyRetired'));
      expect(text('.notice.danger')).toBe(i18n().t('hr.problem.payBasisDailyRetired'));
      expect(BACKOFFICE_TRANSLATIONS.ar['hr.problem.payBasisDailyRetired']).toContain('عمالة يومية');
      expect(BACKOFFICE_TRANSLATIONS.en['hr.problem.payBasisDailyRetired']).toBe(
        'There are no daily workers on payroll: pay a day worker with a payment voucher under Daily labour.');
    });
  });

  describe('the email', () => {
    it('is beside the phone, optional, and an email field read left to right', async () => {
      await setUp();
      fixture.componentInstance.openCreate();
      fixture.detectChanges();
      const input = q('#em-email') as HTMLInputElement;
      expect(input.type).toBe('email');
      expect(input.getAttribute('dir')).toBe('ltr');
      expect(input.getAttribute('autocomplete')).toBe('email');
      expect(input.required).toBeFalse();
      const field = input.closest('.bo-field')!;
      expect(field.querySelector('label')?.textContent?.trim()).toBe(i18n().t('hr.employees.email'));
      expect(field.querySelector('.bo-req')).toBeNull();
      // The phone's own field comes right before it.
      expect(field.previousElementSibling?.querySelector('#em-phone')).not.toBeNull();
    });

    it('is sent trimmed, and an empty one as empty text, on a create', async () => {
      await setUp();
      fillNew();
      const c = fixture.componentInstance;
      c.form.email = '  nadia@zena4cp.com  ';
      await c.save();
      fillNew();
      c.form.email = '   ';
      await c.save();
      expect(created.map((b) => b.email)).toEqual(['nadia@zena4cp.com', '']);
    });

    // The server keeps the stored address on a null (an older client's omission) and clears it on
    // '', so a cleared field must never go as null — that would silently keep the old address.
    it('is loaded on an edit and always sent on the update, cleared as empty text', async () => {
      rows = [person('e1', 'Ahmed', 'Monthly', 45000, 'ahmed@zena4cp.com')];
      await setUp();
      const c = fixture.componentInstance;
      c.edit(rows[0]);
      fixture.detectChanges();
      await fixture.whenStable();
      expect((q('#em-email') as HTMLInputElement).value).toBe('ahmed@zena4cp.com');
      await c.save();
      c.edit(rows[0]);
      c.form.email = '';
      await c.save();
      expect(updated.map(([, b]) => b.email)).toEqual(['ahmed@zena4cp.com', '']);
    });

    it('says a malformed address on the field once it was left, and does not send it', async () => {
      await setUp();
      fillNew();
      const c = fixture.componentInstance;
      c.form.email = 'nadia@';
      fixture.detectChanges();
      expect(q('#em-email-error')).toBeNull();
      q('#em-email')!.dispatchEvent(new Event('blur'));
      fixture.detectChanges();
      expect(text('#em-email-error')).toBe(i18n().t('hr.problem.emailInvalid'));
      expect(q('#em-email')?.getAttribute('aria-invalid')).toBe('true');
      await c.save();
      expect(created).toEqual([]);
    });

    it('says the server’s refusal on the field and above the form, in words, until the address changes', async () => {
      await setUp();
      refuse = refusal('emailInvalid');
      fillNew();
      const c = fixture.componentInstance;
      // One the browser accepts and the server does not.
      c.form.email = 'nadia@localhost';
      await c.save();
      fixture.detectChanges();
      expect(created.length).toBe(1);
      expect(c.error()).toBe(i18n().t('hr.problem.emailInvalid'));
      expect(text('#em-email-error')).toBe(i18n().t('hr.problem.emailInvalid'));

      c.form.email = 'nadia@zena4cp.com';
      fixture.detectChanges();
      expect(q('#em-email-error')).toBeNull();
    });

    it('reads malformed by the browser’s own rule, not a stricter one', () => {
      expect(notAnEmail('nadia@zena4cp.com')).toBeFalse();
      expect(notAnEmail('nadia@localhost')).toBeFalse();
      expect(notAnEmail('nadia@')).toBeTrue();
      expect(notAnEmail('nadia zena4cp.com')).toBeTrue();
    });
  });

  // PinSay #301: the name in two parts, printed first name first.
  describe('the name in two parts', () => {
    it('asks for the first name of someone filed before the split, and sends both parts', async () => {
      // As the migration left them: no first name, the whole name as the family name.
      rows = [{ ...person('e1', 'Karim Haddad', 'Monthly', 45000), firstName: null, familyName: 'Karim Haddad' }];
      await setUp();
      const c = fixture.componentInstance;
      c.edit(rows[0]);
      fixture.detectChanges();

      expect(c.form.firstName).toBe('');
      expect(c.form.familyName).toBe('Karim Haddad');
      expect(el().textContent).toContain(i18n().t('hr.employees.splitName'));
      await c.save();
      expect(updated.length).toBe(0);

      c.form.firstName = 'Karim';
      c.form.familyName = 'Haddad';
      await c.save();
      expect(updated.length).toBe(1);
      expect([updated[0][1].firstName, updated[0][1].familyName, updated[0][1].fullName]).toEqual(['Karim', 'Haddad', 'Karim Haddad']);
    });
  });

  describe('the drawn number (owner, 2026-10-05: Emp-112610001)', () => {
    it('shows each number in the register as one left-to-right run, in Arabic too', async () => {
      rows = [{ ...person('e1', 'أحمد', 'Monthly', 45000), number: 'Emp-112610001' }, person('e2', 'Nadia', 'Monthly', 38000)];
      await setUp();
      i18n().lang.set('ar');
      fixture.detectChanges();
      const cells = [...el().querySelectorAll(`td[data-label="${i18n().t('hr.employees.number')}"]`)];
      const numbers = cells.map((td) => td.querySelector('.mono') as HTMLElement);
      expect(numbers.map((n) => n.textContent?.trim())).toEqual(['Emp-112610001', 'EMP-000002']);
      for (const n of numbers) expect(n.getAttribute('dir')).toBe('ltr');
      i18n().lang.set('fr');
    });

    it('names the new number in the saved notice as an isolated run', async () => {
      await setUp();
      fillNew();
      await fixture.componentInstance.save();
      expect(fixture.componentInstance.notice()).toContain(isolate('EMP-000009'));
    });
  });

  describe('the hire date', () => {
    it('marks the field invalid and disables Save when cleared and touched; with a date set, neither shows', async () => {
      await setUp();
      fillNew();
      const input = q('#em-hired') as HTMLInputElement;
      const field = input.closest('.bo-field')!;
      const save = el().querySelector('.bo-edit-modal .bo-panel__foot .btn-primary') as HTMLButtonElement;

      // With a date set, touching the field shows neither marker nor message, and Save is enabled.
      input.dispatchEvent(new Event('blur'));
      fixture.detectChanges();
      expect(field.classList.contains('is-invalid')).toBeFalse();
      expect(field.querySelector('.bo-error')).toBeNull();
      expect(save.disabled).toBeFalse();

      // Clear the hire date and touch it: .is-invalid and required message appear, Save is disabled.
      fixture.componentInstance.form.hiredOn = '';
      input.dispatchEvent(new Event('blur'));
      fixture.detectChanges();
      expect(field.classList.contains('is-invalid')).toBeTrue();
      expect(field.querySelector('.bo-error')?.textContent?.trim()).toBe(i18n().t('common.fieldRequired'));
      expect(save.disabled).toBeTrue();

      // With a date set again, neither shows and Save is re-enabled.
      fixture.componentInstance.form.hiredOn = '2026-10-01';
      fixture.detectChanges();
      expect(field.classList.contains('is-invalid')).toBeFalse();
      expect(field.querySelector('.bo-error')).toBeNull();
      expect(save.disabled).toBeFalse();
    });

    it('marks the hire date touched on change as well as blur', async () => {
      await setUp();
      fillNew();
      const input = q('#em-hired') as HTMLInputElement;
      const field = input.closest('.bo-field')!;
      const save = el().querySelector('.bo-edit-modal .bo-panel__foot .btn-primary') as HTMLButtonElement;

      // A day cleared from the calendar, the field never focused: the change alone touches it.
      const picker = input.closest('app-date-input')!.querySelector('input[type=date]') as HTMLInputElement;
      picker.value = '';
      picker.dispatchEvent(new Event('change'));
      fixture.detectChanges();

      expect(field.classList.contains('is-invalid')).toBeTrue();
      expect(field.querySelector('.bo-error')?.textContent?.trim()).toBe(i18n().t('common.fieldRequired'));
      expect(save.disabled).toBeTrue();
    });

    it('marks the hire date invalid if save is attempted while it is empty', async () => {
      await setUp();
      fillNew();
      const field = q('#em-hired')!.closest('.bo-field')!;

      fixture.componentInstance.form.hiredOn = '';
      await fixture.componentInstance.save();
      fixture.detectChanges();

      expect(field.classList.contains('is-invalid')).toBeTrue();
      expect(field.querySelector('.bo-error')?.textContent?.trim()).toBe(i18n().t('common.fieldRequired'));
      expect(created).toEqual([]);
    });
  });
});
