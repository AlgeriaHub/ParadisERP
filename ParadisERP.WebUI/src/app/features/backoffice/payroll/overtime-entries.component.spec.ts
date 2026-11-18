import { ComponentFixture, TestBed } from '@angular/core/testing';
import { OvertimeEntriesComponent } from './overtime-entries.component';
import { I18nService } from '../../../core/i18n.service';
import { HrApi, OvertimeEntry, OvertimeKind } from './hr-api.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { AdminService } from '../../../core/admin.service';
import { BranchLock } from '../../../core/branch-lock';
import { PayrollPagesApi } from './payroll-pages.api';

describe('OvertimeEntriesComponent', () => {
  let component: OvertimeEntriesComponent;
  let fixture: ComponentFixture<OvertimeEntriesComponent>;
  let hrApi: jasmine.SpyObj<HrApi>;
  let pages: jasmine.SpyObj<PayrollPagesApi>;
  let confirm: jasmine.SpyObj<ConfirmService>;
  let admin: jasmine.SpyObj<AdminService>;
  let lock: jasmine.SpyObj<BranchLock>;

  /** The month as the server pages it (owner, 2026-11-17): the entries, and the month's head in the summary. */
  function month(entries: OvertimeEntry[], kinds: OvertimeKind[] = []) {
    pages.overtimePage.and.callFake(async (q) => ({
      items: entries,
      total: entries.length,
      skip: 0,
      take: q.take,
      summary: {
        branchId: 'b1', year: 2026, month: 10, locked: false, runNumber: null, runStatus: null, kinds,
        totals: kinds.map((k) => ({ key: k.key, hours: entries.filter((e) => e.ruleKey === k.key).reduce((a, e) => a + e.hours, 0) })),
      },
    }));
  }

  /** Lets the branch arrive, the page be asked for and its answer be drawn. */
  async function settle(): Promise<void> {
    for (let i = 0; i < 3; i++) {
      fixture.detectChanges();
      await fixture.whenStable();
    }
    fixture.detectChanges();
  }

  beforeEach(async () => {
    hrApi = jasmine.createSpyObj('HrApi', [
      'employees',
      'createOvertimeEntry',
      'updateOvertimeEntry',
      'deleteOvertimeEntry',
      'overtimeHistory',
    ]);
    pages = jasmine.createSpyObj('PayrollPagesApi', ['overtimePage']);
    confirm = jasmine.createSpyObj('ConfirmService', ['ask']);
    admin = jasmine.createSpyObj('AdminService', ['listBranches']);
    lock = jasmine.createSpyObj('BranchLock', ['locked', 'lockedBranchId', 'defaultBranch', 'ownBranchName']);

    admin.listBranches.and.returnValue(Promise.resolve([{ id: 'b1', translations: { en: 'B1' } } as any]));
    lock.locked.and.returnValue(false);
    lock.defaultBranch.and.returnValue('b1');

    const i18nStub = {
      lang: () => 'en',
      t: (key: string, params?: Record<string, unknown>) => {
        if (params && key === 'hr.overtime.inRun') {
          return `${key}:${params['run']}`;
        }
        return key;
      },
      dateTime: (d: string) => d,
    };

    await TestBed.configureTestingModule({
      imports: [OvertimeEntriesComponent],
      providers: [
        { provide: I18nService, useValue: i18nStub },
        { provide: HrApi, useValue: hrApi },
        { provide: PayrollPagesApi, useValue: pages },
        { provide: ConfirmService, useValue: confirm },
        { provide: AdminService, useValue: admin },
        { provide: BranchLock, useValue: lock },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(OvertimeEntriesComponent);
    component = fixture.componentInstance;
  });

  const entry = (canEdit: boolean, lockedByRun: string | null): OvertimeEntry => ({
    id: 'e1',
    employeeId: 'emp1',
    employeeNumber: '1',
    employeeName: 'John',
    workDate: '2026-10-10',
    ruleKey: 'R1',
    hours: 2,
    note: null,
    createdBy: 'admin',
    createdAtUtc: '2026-10-10T10:00:00Z',
    updatedBy: null,
    updatedAtUtc: '2026-10-10T10:00:00Z',
    canEdit,
    lockedByRun,
  });

  it('hides edit and delete for entries with canEdit: false', async () => {
    hrApi.employees.and.returnValue(Promise.resolve({ total: 1, skip: 0, take: 500, rows: [] }));
    month([entry(false, 'RUN-1')]);
    fixture.detectChanges();

    component.branchId.set('b1');
    await settle();

    const rowActions = fixture.nativeElement.querySelector('tbody td.bo-actions');
    expect(rowActions).toBeTruthy();
    const buttons = rowActions.querySelectorAll('button');
    expect(buttons.length).toBe(0);
    expect(rowActions.textContent).toContain('hr.overtime.inRun');
  });

  it('asks the server for the branch’s month, ten entries first, and for one employee when filtered', async () => {
    hrApi.employees.and.returnValue(Promise.resolve({ total: 1, skip: 0, take: 500, rows: [] }));
    month([entry(true, null)], [{ key: 'R1', labels: { en: 'Night' }, multiplier: 1.5, unit: 'Hours' }]);
    fixture.detectChanges();
    component.branchId.set('b1');
    await settle();

    expect(pages.overtimePage).toHaveBeenCalledWith(jasmine.objectContaining({ skip: 0, take: 10, branchId: 'b1' }));
    // The totals line is the server's sum over every entry kept, not the page's.
    expect(component.totals().map((t) => [t.key, t.hours])).toEqual([['R1', 2]]);

    component.employeeFilter.set('emp1');
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 350));
    await settle();
    expect(pages.overtimePage).toHaveBeenCalledWith(jasmine.objectContaining({ employeeId: 'emp1', skip: 0 }));
  });

  it('calls deleteOvertimeEntry only if confirm is true', async () => {
    hrApi.employees.and.returnValue(Promise.resolve({ total: 1, skip: 0, take: 500, rows: [] }));
    const one = entry(true, null);
    month([one]);
    fixture.detectChanges();

    component.branchId.set('b1');
    await settle();

    // cancel delete
    confirm.ask.and.returnValue(Promise.resolve(false));
    await component.deleteEntry(one);
    expect(hrApi.deleteOvertimeEntry).not.toHaveBeenCalled();

    // confirm delete
    confirm.ask.and.returnValue(Promise.resolve(true));
    hrApi.deleteOvertimeEntry.and.returnValue(Promise.resolve());
    await component.deleteEntry(one);
    expect(hrApi.deleteOvertimeEntry).toHaveBeenCalledWith('e1');
  });

  it('labels field as days when kind unit is Days', async () => {
    hrApi.employees.and.returnValue(Promise.resolve({ total: 1, skip: 0, take: 500, rows: [] }));
    month([], [{ key: 'R1', labels: { en: 'Days Kind', fr: '', ar: '' }, multiplier: 1, unit: 'Days' }]);
    fixture.detectChanges();

    component.branchId.set('b1');
    await settle();

    component.formRuleKey.set('R1');
    fixture.detectChanges();

    expect(component.isDaysKind()).toBeTrue();
    const label = fixture.nativeElement.querySelector('label[for="ot-hours"]');
    expect(label).toBeTruthy();
    expect(label.textContent.trim()).toBe('hr.overtime.days');
  });
});
