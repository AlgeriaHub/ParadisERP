import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AdminService } from '../../../core/admin.service';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService } from '../../../core/regional.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { HrEmployeesComponent } from './employees.component';
import { HrApi, UpsertEmployee } from './hr-api.service';
import { PayrollApi, PayrollSettings, PayrollSettingsView } from './payroll-api.service';

/**
 * The first screen of the personnel module, and the one that could not write at all.
 *
 * <p>`valid` was a `computed` over `form`, a plain mutable object that `[(ngModel)]` writes into
 * without touching a signal. A computed with no producer is evaluated once — against the blank
 * form — caches `false` and is never invalidated, so `[disabled]="busy() || !valid()"` on the Save
 * button stayed true for ever: with a name, a branch and a hire date all in the DOM, no employee
 * could be created or edited. Nothing type-checked would ever have caught it, which is why the
 * assertion below is about the button element and not about the expression behind it.</p>
 */

const BRANCHES = [
  { id: 'b-oran', nameFr: 'Oran', nameAr: 'وهران', isActive: true },
  { id: 'b-alger', nameFr: 'Alger-Centre', nameAr: 'الجزائر الوسطى', isActive: true },
];

function settings(): PayrollSettings {
  return {
    countryCode: 'DZ',
    socialNames: { fr: 'Sécurité sociale' },
    incomeTaxNames: { fr: 'IRG' },
    allowances: [],
    overtimeRules: [],
    deductions: [],
    defaultAttendanceMode: 'Declared',
  } as unknown as PayrollSettings;
}

function mount() {
  const created: UpsertEmployee[] = [];
  TestBed.configureTestingModule({
    imports: [HrEmployeesComponent],
    providers: [
      {
        provide: HrApi,
        useValue: {
          employees: () => Promise.resolve({ total: 0, skip: 0, take: 50, rows: [] }),
          createEmployee: (body: UpsertEmployee) => {
            created.push(body);
            return Promise.resolve({
              ...body,
              id: 'e-1',
              number: 'EMP-000001',
              branchName: 'Oran',
              allowances: [],
              openAdvances: 0,
              attendanceMode: 'Declared',
            });
          },
          updateEmployee: () => Promise.reject(new Error('not used')),
          markLeft: () => Promise.reject(new Error('not used')),
        },
      },
      {
        provide: PayrollApi,
        useValue: {
          settings: () =>
            Promise.resolve({
              settings: settings(),
              revision: 1,
              owners: {},
              canEditCompany: true,
              canEditStatutory: true,
            } as unknown as PayrollSettingsView),
        },
      },
      { provide: AdminService, useValue: { listBranches: () => Promise.resolve(BRANCHES) } },
      {
        provide: I18nService,
        useValue: {
          t: (key: string) => key,
          lang: signal('fr'),
          money: (value: number) => String(value),
          rateLabel: (value: number) => String(value),
        },
      },
      { provide: RegionalService, useValue: { settings: () => ({ timeZone: 'Africa/Algiers' }), moneyStep: () => 1 } },
      { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
    ],
  });
  return created;
}

/** Two settle rounds: `NgModel` writes the loaded values into the DOM on a promise of its own. */
async function render() {
  const fixture = TestBed.createComponent(HrEmployeesComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return fixture;
}

function saveButton(fixture: { nativeElement: HTMLElement }): HTMLButtonElement {
  const buttons = Array.from(fixture.nativeElement.querySelectorAll('button.btn-primary'));
  return buttons[0] as HTMLButtonElement;
}

describe('the employee register', () => {
  it('leaves Save disabled while a required field is empty', async () => {
    mount();
    const fixture = await render();

    // The blank form already carries today as the hire date; the name and the branch are not set.
    expect(saveButton(fixture).disabled).toBe(true);
  });

  it('enables Save once both names, the branch and the hire date are filled', async () => {
    mount();
    const fixture = await render();
    const screen = fixture.componentInstance;

    // The first name alone is not a name (PinSay #301): Save waits for the family name too.
    screen.form.firstName = 'PROBE';
    screen.form.branchId = 'b-oran';
    screen.form.hiredOn = '2026-01-01';
    fixture.detectChanges();
    expect(saveButton(fixture).disabled).toBe(true);

    screen.form.familyName = 'Person';
    screen.form.branchId = 'b-oran';
    screen.form.hiredOn = '2026-01-01';
    fixture.detectChanges();

    expect(saveButton(fixture).disabled).toBe(false);
  });

  it('sends what was typed when Save is pressed', async () => {
    const created = mount();
    const fixture = await render();
    const screen = fixture.componentInstance;

    screen.form.firstName = '  PROBE ';
    screen.form.familyName = ' Person  ';
    screen.form.branchId = 'b-oran';
    screen.form.hiredOn = '2026-01-01';
    screen.form.baseRate = 18000;
    await screen.save();

    expect(created.length).toBe(1);
    // The parts trimmed, and the full name composed first name first for a server older than them.
    expect(created[0].firstName).toBe('PROBE');
    expect(created[0].familyName).toBe('Person');
    expect(created[0].fullName).toBe('PROBE Person');
    expect(created[0].branchId).toBe('b-oran');
    expect(created[0].baseRate).toBe(18000);
  });
  it('sends the fingerprint number when one is typed, and nothing when it is left empty (FR-3A)', async () => {
    const created = mount();
    const fixture = await render();
    const screen = fixture.componentInstance;

    expect(fixture.nativeElement.querySelector('#em-fingerprint')).not.toBeNull();
    screen.form.firstName = 'PROBE';
    screen.form.familyName = 'Person';
    screen.form.branchId = 'b-oran';
    screen.form.fingerprintNumber = ' 0042 ';
    await screen.save();
    screen.form.firstName = 'PROBE';
    screen.form.familyName = 'Other';
    screen.form.branchId = 'b-oran';
    screen.form.fingerprintNumber = '   ';
    await screen.save();

    // Trimmed only: the one spelling (no leading zeros) is the server's, so it is decided once.
    expect(created[0].fingerprintNumber).toBe('0042');
    expect(created[1].fingerprintNumber).toBeNull();
  });
});
