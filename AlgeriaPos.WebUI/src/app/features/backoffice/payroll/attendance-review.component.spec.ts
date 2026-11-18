import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AdminService } from '../../../core/admin.service';
import { BranchLock } from '../../../core/branch-lock';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { HrAttendanceReviewComponent } from './attendance-review.component';
import { AttendanceReviewResult } from './hr-api.service';
import { PayrollPagesApi, ReviewPageQuery } from './payroll-pages.api';

describe('HrAttendanceReviewComponent', () => {
  let fixture: ComponentFixture<HrAttendanceReviewComponent>;
  let component: HrAttendanceReviewComponent;
  let reviewSpy: jasmine.Spy;

  const mockReviewResult: AttendanceReviewResult = {
    year: 2026,
    month: 3,
    days: [
      {
        employeeId: 'emp-1',
        workDate: '2026-03-02',
        isRestDay: false,
        periodName: null,
        scheduledStart: '08:00',
        scheduledEnd: '17:00',
        scheduledHours: 8,
        firstIn: '08:07',
        lastOut: '17:45',
        workedHours: 9.63,
        lateMinutes: 7,
        earlyLeaveMinutes: 30,
        missingCheckOut: true,
        possibleOvertimeMinutes: 45,
        workedOnRestDay: true,
      },
    ],
    people: [
      {
        employeeId: 'emp-1',
        employeeNumber: 'EMP-001',
        employeeName: 'Ahmed Ben',
      },
    ],
  };

  /** Lets the branch arrive, the page be asked for and its answer be drawn. */
  async function settle(): Promise<void> {
    for (let i = 0; i < 3; i++) {
      fixture.detectChanges();
      await fixture.whenStable();
    }
    fixture.detectChanges();
  }

  beforeEach(() => {
    // The month's days are paged by the server (owner, 2026-11-17); the summary carries its people and day count.
    reviewSpy = jasmine.createSpy('reviewPage').and.callFake(async (q: ReviewPageQuery) => ({
      items: mockReviewResult.days,
      total: mockReviewResult.days.length,
      skip: 0,
      take: q.take,
      summary: { year: 2026, month: 3, days: mockReviewResult.days.length, people: mockReviewResult.people },
    }));

    const stubAdminService = {
      listBranches: jasmine.createSpy('listBranches').and.resolveTo([
        { id: 'b-1', name: 'Main Branch', isMain: true },
      ]),
    };

    const stubBranchLock = {
      locked: jasmine.createSpy('locked').and.returnValue(false),
      lockedBranchId: jasmine.createSpy('lockedBranchId').and.returnValue(null),
      defaultBranch: jasmine.createSpy('defaultBranch').and.returnValue('b-1'),
      ownBranchName: jasmine.createSpy('ownBranchName').and.returnValue('Main Branch'),
    };

    TestBed.configureTestingModule({
      imports: [HrAttendanceReviewComponent],
      providers: [
        { provide: PayrollPagesApi, useValue: { reviewPage: reviewSpy } },
        { provide: AdminService, useValue: stubAdminService },
        { provide: BranchLock, useValue: stubBranchLock },
      ],
    });

    const i18n = TestBed.inject(I18nService);
    i18n.extend(BACKOFFICE_TRANSLATIONS);
  });

  it('renders flags as words from a stubbed page', async () => {
    fixture = TestBed.createComponent(HrAttendanceReviewComponent);
    component = fixture.componentInstance;
    await settle();

    expect(component.pager.rows()).toEqual(mockReviewResult.days);
    expect(component.personOf('emp-1')?.employeeName).toBe('Ahmed Ben');
    const day = component.pager.rows()[0];
    const flags = component.rowFlags(day);

    // Words and tones
    expect(flags.length).toBe(5);
    expect(flags.map((f) => f.kind)).toEqual(['late', 'earlyLeave', 'missingOut', 'possibleOvertime', 'restDayWork']);
    const lateFlag = flags.find((f) => f.tone === 'warning' && f.text.includes('7'));
    expect(lateFlag).toBeDefined();

    const earlyFlag = flags.find((f) => f.tone === 'warning' && f.text.includes('30'));
    expect(earlyFlag).toBeDefined();

    const missingOutFlag = flags.find((f) => f.tone === 'warning' && !f.text.includes('7') && !f.text.includes('30'));
    expect(missingOutFlag).toBeDefined();

    const otFlag = flags.find((f) => f.tone === 'info' && f.text.includes('45'));
    expect(otFlag).toBeDefined();

    const restWorkFlag = flags.find((f) => f.tone === 'info' && !f.text.includes('45'));
    expect(restWorkFlag).toBeDefined();

    // Verify rendered in DOM
    const text = fixture.nativeElement.textContent;
    for (const flag of flags) {
      expect(text).toContain(flag.text);
    }
  });

  it('the "only flagged" checkbox sends flagged=false when unticked', async () => {
    fixture = TestBed.createComponent(HrAttendanceReviewComponent);
    component = fixture.componentInstance;
    await settle();

    // Default load calls with flagged: true, ten days first, at the branch
    expect(reviewSpy).toHaveBeenCalledWith(jasmine.objectContaining({ flagged: true, branchId: 'b-1', skip: 0, take: 10 }));

    // Untick: the server is asked again, from page one, once the filter has settled.
    component.setFlaggedOnly(false);
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 350));
    await settle();

    // Sends flagged: false
    expect(reviewSpy).toHaveBeenCalledWith(jasmine.objectContaining({ flagged: false, skip: 0 }));
  });

  it('sends the person and the point to check to the server, never filtering the page itself', async () => {
    fixture = TestBed.createComponent(HrAttendanceReviewComponent);
    component = fixture.componentInstance;
    await settle();

    component.employeeFilter.set('emp-1');
    component.flagFilter.set('missingOut');
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 350));
    await settle();

    expect(reviewSpy).toHaveBeenCalledWith(jasmine.objectContaining({ person: 'emp-1', flag: 'missingOut', skip: 0 }));
  });
});
