import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { AdminService } from '../../../core/admin.service';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { ConfirmService } from '../../../ui/confirm.service';
import { HrEmployeesComponent } from './employees.component';
import { Employee, HrApi } from './hr-api.service';
import { PayrollApi } from './payroll-api.service';

const person = (id: string, fullName: string, accountSignIn: string | null, here = true): Employee => ({
  id, number: 'EMP-00000' + id.slice(-1), fullName, branchId: 'alger', branchName: 'Alger', payBasis: 'Monthly',
  baseRate: 30000, payoutMethod: 'Cash', hiredOn: '2026-01-01', isActive: here, openAdvances: 0,
  accountUserId: accountSignIn ? 'u-' + id : null, accountSignIn,
} as Employee);

/**
 * "Give access" on an employee (2026-10-05): an account is given from the employee, so the row of
 * someone with no access offers it — to a reader who may open Users — and the row of someone with an
 * account says what they sign in with.
 */
describe('HrEmployeesComponent — giving access from the employee', () => {
  let fixture: ComponentFixture<HrEmployeesComponent>;
  let navigated: unknown[];

  const setUp = async (role: 'Admin' | 'Manager') => {
    navigated = [];
    TestBed.configureTestingModule({
      imports: [HrEmployeesComponent],
      providers: [
        {
          provide: HrApi,
          useValue: {
            employees: () => Promise.resolve({
              total: 3, skip: 0, take: 50,
              rows: [person('e1', 'Ahmed', '58310427'), person('e2', 'Nadia', null), person('e3', 'Parti', null, false)],
            }),
          },
        },
        { provide: PayrollApi, useValue: { settings: () => Promise.resolve({ allowances: [], defaultAttendanceMode: 'Declared' }) } },
        { provide: AdminService, useValue: { listBranches: () => Promise.resolve([]) } },
        {
          provide: AuthService,
          useValue: { currentUser: signal({ id: 'me', role, permissions: ['payroll.manage'], capabilities: [] }) },
        },
        { provide: Router, useValue: { navigate: (...args: unknown[]) => (navigated.push(args), Promise.resolve(true)) } },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    fixture = TestBed.createComponent(HrEmployeesComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  };

  const i18n = () => TestBed.inject(I18nService);
  const giveButtons = () =>
    ([...fixture.nativeElement.querySelectorAll('button')] as HTMLButtonElement[]).filter(
      (b) => b.textContent?.trim() === i18n().t('hr.employees.giveAccess'));

  it('offers access only to someone still working with none, and names the Login ID of someone with one', async () => {
    await setUp('Admin');
    expect(giveButtons().length).toBe(1);
    expect(fixture.nativeElement.textContent).toContain(i18n().t('hr.employees.access', { signIn: '58310427' }));
  });

  it('opens Users on that employee', async () => {
    await setUp('Admin');
    giveButtons()[0].click();
    await fixture.whenStable();
    expect(navigated[0]).toEqual([['/backoffice/admin/users'], { queryParams: { employee: 'e2' } }]);
  });

  it('is not offered to a reader who cannot open Users', async () => {
    await setUp('Manager');
    expect(giveButtons().length).toBe(0);
  });
});
