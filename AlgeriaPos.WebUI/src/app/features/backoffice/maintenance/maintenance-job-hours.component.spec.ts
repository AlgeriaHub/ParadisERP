import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ApiError } from '../../../core/api.service';
import { MaintenanceJob } from './maintenance-api.service';
import { MaintenanceHoursApi, MaintenanceJobHours, MaintenanceLabourCandidate } from './maintenance-hours-api.service';
import { MaintenanceJobHoursComponent } from './maintenance-job-hours.component';
import { MaintenancePagesApi } from './maintenance-pages.api';
import { job, pageOf, signIn, t } from './maintenance-test-data';

const HOURS: MaintenanceJobHours = {
  jobId: 'j1', jobStatus: 'Open', totalHours: 5.5, today: '2026-11-09',
  bookings: [
    { id: 'b1', employeeId: 'e1', employeeNumber: 'E-001', employeeName: 'Karim Benali', day: '2026-11-08', hours: 3, overtimeHours: 1,
      attendanceHours: 8, overrideReason: null, overriddenBy: null, userName: 'chef', atUtc: '2026-11-08T16:00:00Z' },
    { id: 'b2', employeeId: 'e2', employeeNumber: 'E-002', employeeName: 'Samir Haddad', day: '2026-11-07', hours: 2.5, overtimeHours: 0,
      attendanceHours: 0, overrideReason: 'Foreman confirmed', overriddenBy: 'chef', userName: 'chef', atUtc: '2026-11-07T16:00:00Z' },
  ],
};

const CANDIDATES: MaintenanceLabourCandidate[] = [
  { employeeId: 'e1', number: 'E-001', fullName: 'Karim Benali', attendanceHours: 8, attendanceSource: 'recorded', bookedHours: 5,
    bookedOnJob: false, payrollLocked: false },
  { employeeId: 'e2', number: 'E-002', fullName: 'Samir Haddad', attendanceHours: null, attendanceSource: 'declared', bookedHours: 0,
    bookedOnJob: false, payrollLocked: false },
];

describe('MaintenanceJobHoursComponent (maintenance, Phase M3)', () => {
  let fixture: ComponentFixture<MaintenanceJobHoursComponent>;
  let api: jasmine.SpyObj<MaintenanceHoursApi>;
  let pages: jasmine.SpyObj<MaintenancePagesApi>;

  /** The server's page of the job's hours: these bookings, and the rest of the hours as the summary. */
  function serve(hours: MaintenanceJobHours): void {
    const { bookings, ...head } = hours;
    pages.jobHours.and.resolveTo(pageOf(bookings, head));
  }

  async function render(keys: string[], j: MaintenanceJob = job()): Promise<HTMLElement> {
    api = jasmine.createSpyObj<MaintenanceHoursApi>('MaintenanceHoursApi', ['candidates', 'book', 'remove']);
    api.candidates.and.resolveTo(CANDIDATES);
    api.book.and.resolveTo({ ...HOURS, totalHours: 7.5 });
    api.remove.and.resolveTo({ ...HOURS, totalHours: 2.5, bookings: [HOURS.bookings[1]] });
    pages = jasmine.createSpyObj<MaintenancePagesApi>('MaintenancePagesApi', ['jobHours']);
    serve(HOURS);
    TestBed.configureTestingModule({
      imports: [MaintenanceJobHoursComponent],
      providers: [{ provide: MaintenanceHoursApi, useValue: api }, { provide: MaintenancePagesApi, useValue: pages }],
    });
    signIn(keys);
    fixture = TestBed.createComponent(MaintenanceJobHoursComponent);
    fixture.componentRef.setInput('job', j);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('lists the hours per person and day with the override in words, and gives a reader nothing to change', async () => {
    const page = await render(['maintenance.view']);
    expect(pages.jobHours).toHaveBeenCalledWith('j1', { skip: 0, take: 10 });
    const rows = page.querySelectorAll('tbody tr');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('Karim Benali');
    expect(rows[0].textContent).toContain('08/11/2026');
    expect(rows[1].textContent).toContain(t('maint.hours.overridden', { by: 'chef', reason: 'Foreman confirmed' }));
    expect(page.textContent).toContain(t('maint.hours.total', { n: fixture.componentInstance.qty(5.5) }));
    expect(page.querySelector('.head-action')).toBeNull();
    expect(page.querySelector('app-menu')).toBeNull();
  });

  it('says the day’s ceiling in words and asks the override holder why past it', async () => {
    await render(['maintenance.view', 'maintenance.jobs.manage', 'maintenance.labour.override']);
    const c = fixture.componentInstance;
    await c.openBooking();
    fixture.detectChanges();
    expect(api.candidates).toHaveBeenCalledWith('j1', '2026-11-09');
    expect(c.bookEmployee).toBe('e1');
    const karim = c.chosen()!;
    expect(c.ceiling(karim)).toBe(t('maint.hours.ceiling', { who: 'Karim Benali', day: '09/11/2026', attendance: c.qty(8), booked: c.qty(5), left: c.qty(3) }));

    c.bookHours = 3;
    expect(c.overNeeded()).toBeFalse();
    expect(c.canSubmit()).toBeTrue();
    c.bookHours = 4;
    expect(c.overNeeded()).toBeTrue();
    expect(c.canSubmit()).toBeFalse();
    c.bookReason = 'Urgent repair';
    expect(c.canSubmit()).toBeTrue();
    serve({ ...HOURS, totalHours: 7.5 });
    await c.book();
    expect(api.book).toHaveBeenCalledWith('j1', { employeeId: 'e1', day: '2026-11-09', hours: 4, overtimeHours: 0, overrideReason: 'Urgent repair' });
    expect(c.booking()).toBeFalse();
    expect(c.hours()?.totalHours).toBe(7.5);
  });

  it('keeps someone without the override from booking past attendance, and says a refusal in words', async () => {
    await render(['maintenance.view', 'maintenance.jobs.manage']);
    const c = fixture.componentInstance;
    await c.openBooking();
    c.bookEmployee = 'e2';
    fixture.detectChanges();
    expect(c.ceiling(c.chosen()!)).toBe(t('maint.hours.ceilingDeclared', { who: 'Samir Haddad' }));
    expect(c.overNeeded()).toBeTrue();
    expect(c.canSubmit()).toBeFalse();
    expect((fixture.nativeElement as HTMLElement).ownerDocument.body.textContent).toContain(t('maint.hours.overAttendance'));

    c.bookEmployee = 'e1';
    c.bookHours = 2;
    api.book.and.rejectWith(new ApiError(JSON.stringify({ detail: 'closed', code: 'payroll_month_locked' }), 409));
    await c.book();
    expect(c.formError()).toBe(t('maint.hours.error.payrollLocked'));
  });

  it('removes a booking of an open job, and offers nothing on a completed one', async () => {
    const page = await render(['maintenance.view', 'maintenance.jobs.manage']);
    expect(page.querySelectorAll('app-menu').length).toBe(2);
    expect(page.querySelector('.head-action')?.textContent).toContain(t('maint.hours.book'));
    serve({ ...HOURS, totalHours: 2.5, bookings: [HOURS.bookings[1]] });
    await fixture.componentInstance.remove(HOURS.bookings[0]);
    fixture.detectChanges();
    expect(api.remove).toHaveBeenCalledWith('j1', 'b1');
    expect(page.querySelectorAll('tbody tr').length).toBe(1);
    expect(page.textContent).toContain(t('maint.hours.removedNotice'));

    TestBed.resetTestingModule();
    const closed = await render(['maintenance.view', 'maintenance.jobs.manage'], job({ status: 'Completed' }));
    expect(closed.querySelector('app-menu')).toBeNull();
    expect(closed.querySelector('.head-action')).toBeNull();
  });
});
