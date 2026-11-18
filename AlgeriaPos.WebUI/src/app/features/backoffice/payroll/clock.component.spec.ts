import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AdminBranch, AdminService } from '../../../core/admin.service';
import { ApiError } from '../../../core/api.service';
import { BranchLock } from '../../../core/branch-lock';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { ConfirmDialogComponent, ConfirmRequest, ConfirmService } from '../../../ui/confirm.service';
import { HrClockComponent } from './clock.component';
import { ClockPerson, Clocked, HrApi } from './hr-api.service';
import { PayrollPagesApi } from './payroll-pages.api';
import { RegionalService } from '../../../core/regional.service';
import { localTime, zonedTimeToUtcIso } from '../../../core/rentals';

describe('HrClockComponent — punch confirmation', () => {
  let hr: jasmine.SpyObj<HrApi>;
  let pages: jasmine.SpyObj<PayrollPagesApi>;
  let admin: jasmine.SpyObj<AdminService>;
  let confirm: jasmine.SpyObj<ConfirmService>;
  let fixture: ComponentFixture<HrClockComponent>;
  let component: HrClockComponent;

  const samplePerson: ClockPerson = {
    employeeId: 'emp-1',
    employeeNumber: '101',
    employeeName: 'Amina Mansouri',
  };

  const sampleClocked: Clocked = {
    event: {
      id: 'evt-1',
      employeeId: 'emp-1',
      atUtc: '2026-10-10T08:00:00Z',
      kind: 'In',
      source: 'clock-screen',
    },
    person: samplePerson,
  };

  beforeEach(async () => {
    hr = jasmine.createSpyObj<HrApi>('HrApi', ['clockPerson', 'clock']);
    // The month's punches are paged by the server (owner, 2026-11-17).
    pages = jasmine.createSpyObj<PayrollPagesApi>('PayrollPagesApi', ['clockPage']);
    admin = jasmine.createSpyObj<AdminService>('AdminService', ['listBranches']);
    confirm = jasmine.createSpyObj<ConfirmService>('ConfirmService', ['ask']);

    admin.listBranches.and.resolveTo([
      { id: 'b1', nameFr: 'Siège', nameAr: 'المقر', nameEn: 'HQ', isMain: true } as AdminBranch,
    ]);
    pages.clockPage.and.callFake(async (q) => ({ items: [], total: 0, skip: 0, take: q.take, summary: { events: 0, unpaired: 0 } }));
    hr.clockPerson.and.resolveTo(samplePerson);
    hr.clock.and.resolveTo(sampleClocked);
    confirm.ask.and.resolveTo(true);

    TestBed.configureTestingModule({
      imports: [HrClockComponent],
      providers: [
        { provide: HrApi, useValue: hr },
        { provide: PayrollPagesApi, useValue: pages },
        { provide: AdminService, useValue: admin },
        { provide: ConfirmService, useValue: confirm },
        {
          provide: BranchLock,
          useValue: {
            locked: signal(false),
            lockedBranchId: () => null,
            defaultBranch: (b: AdminBranch[]) => b[0]?.id ?? null,
            ownBranchName: () => 'Siège',
          },
        },
      ],
    });

    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);

    fixture = TestBed.createComponent(HrClockComponent);
    component = fixture.componentInstance;
    // The branch arrives, then the log's first page is asked for and drawn.
    for (let i = 0; i < 2; i++) {
      fixture.detectChanges();
      await fixture.whenStable();
    }
    fixture.detectChanges();
  });

  afterEach(() => {
    fixture.destroy();
  });

  it('confirm → hr.clock called once with the number and kind; number cleared', async () => {
    confirm.ask.and.resolveTo(true);
    component.number.set('101');
    component.kind.set('In');

    await component.punch('101');

    expect(hr.clockPerson).toHaveBeenCalledWith('101');
    expect(confirm.ask).toHaveBeenCalledTimes(1);
    expect(hr.clock).toHaveBeenCalledWith({
      employeeNumber: '101',
      kind: 'In',
      source: 'clock-screen',
    });
    expect(component.number()).toBe('');
    expect(component.last()).toEqual(sampleClocked);
    expect(component.busy()).toBeFalse();
  });

  it('cancel → hr.clock not called; number kept', async () => {
    confirm.ask.and.resolveTo(false);
    component.number.set('101');

    await component.punch('101');

    expect(hr.clockPerson).toHaveBeenCalledWith('101');
    expect(confirm.ask).toHaveBeenCalledTimes(1);
    expect(hr.clock).not.toHaveBeenCalled();
    expect(component.number()).toBe('101');
    expect(component.last()).toBeNull();
    expect(component.busy()).toBeFalse();
  });

  it('unknown number (clockPerson rejects) → confirm.ask not called, hr.clock not called, error set', async () => {
    hr.clockPerson.and.rejectWith(
      new ApiError(JSON.stringify({ code: 'employeeNotFound', message: 'Employee not found.' }), 404),
    );
    component.number.set('999');

    await component.punch('999');

    expect(hr.clockPerson).toHaveBeenCalledWith('999');
    expect(confirm.ask).not.toHaveBeenCalled();
    expect(hr.clock).not.toHaveBeenCalled();
    expect(component.error()).toBeTruthy();
    expect(component.number()).toBe('999');
    expect(component.busy()).toBeFalse();
  });

  it('confirm.ask receives focusConfirm: true', async () => {
    confirm.ask.and.resolveTo(true);
    component.kind.set('Out');
    const i18n = TestBed.inject(I18nService);

    await component.punch('101');

    expect(confirm.ask).toHaveBeenCalledTimes(1);
    const req = confirm.ask.calls.mostRecent().args[0];
    expect(req.focusConfirm).toBeTrue();
    expect(req.title).toBe(i18n.t('hr.clock.confirmTitle'));
    expect(req.confirmLabel).toBe(i18n.t('hr.clock.confirmOk'));
    expect(req.message).toContain('Amina Mansouri');
    expect(req.message).toContain('101');
    expect(req.message).toContain(i18n.t('hr.clock.out'));
  });

  it('keeps busy true while confirm dialog is open so a second scan cannot open a second dialog', async () => {
    let resolveConfirm!: (value: boolean) => void;
    const confirmPromise = new Promise<boolean>((resolve) => {
      resolveConfirm = resolve;
    });
    confirm.ask.and.returnValue(confirmPromise);

    const firstPunch = component.punch('101');
    await fixture.whenStable();

    expect(component.busy()).toBeTrue();

    // Second scan while dialog is open: ignored because component is busy
    await component.punch('102');
    expect(hr.clockPerson).toHaveBeenCalledTimes(1);
    expect(confirm.ask).toHaveBeenCalledTimes(1);

    resolveConfirm(true);
    await firstPunch;

    expect(component.busy()).toBeFalse();
    expect(hr.clock).toHaveBeenCalledTimes(1);
  });

  it('focuses the badge number input after confirm and after cancel', async () => {
    const inputEl = fixture.nativeElement.querySelector('#ck-number') as HTMLInputElement;
    spyOn(inputEl, 'focus');

    confirm.ask.and.resolveTo(true);
    await component.punch('101');
    expect(inputEl.focus).toHaveBeenCalled();

    (inputEl.focus as jasmine.Spy).calls.reset();
    confirm.ask.and.resolveTo(false);
    await component.punch('101');
    expect(inputEl.focus).toHaveBeenCalled();
  });

  // Owner, 2026-10-22: "Algeria time, not server time" — the company's zone, never the host's or the browser's.
  it("asks for this month on the company's calendar and names the company's zone beside now", () => {
    const zone = TestBed.inject(RegionalService).settings().timeZone;
    const [year, month] = localTime(new Date().toISOString(), zone).day.split('-').map(Number);
    expect(pages.clockPage).toHaveBeenCalledWith(jasmine.objectContaining({ year, month, branchId: 'b1', skip: 0, take: 10 }));

    const hint = (fixture.nativeElement.querySelector('#ck-when-hint') as HTMLElement).textContent ?? '';
    expect(hint).toContain(zone);
  });

  // Owner, 2026-10-22: set the time in and out, or take now from the server — keep both.
  it('a set time is sent as the instant it names on the company clock, and confirmed with it', async () => {
    const zone = TestBed.inject(RegionalService).settings().timeZone;
    component.setTime();
    expect(component.when()).toBe('set');
    // It starts from the present minute.
    expect(component.day()).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    expect(component.time()).toMatch(/^\d{2}:\d{2}$/);

    component.day.set('2026-10-20');
    component.time.set('07:30');
    await component.punch('101');

    const at = zonedTimeToUtcIso('2026-10-20', '07:30', zone);
    expect(hr.clockPerson).toHaveBeenCalledWith('101', at);
    expect(hr.clock).toHaveBeenCalledWith({ employeeNumber: '101', kind: 'In', atUtc: at, source: 'clock-screen' });
    expect(confirm.ask.calls.mostRecent().args[0].message).toContain(TestBed.inject(I18nService).dateTime(at));
  });

  it('a set time later than now, or not complete, is refused before anything is asked', async () => {
    const i18n = TestBed.inject(I18nService);
    component.setTime();
    component.day.set('2099-01-01');
    fixture.detectChanges();
    expect(component.timeProblem()).toBe(i18n.t('hr.clock.timeInFuture'));

    await component.punch('101');
    expect(hr.clockPerson).not.toHaveBeenCalled();
    expect(hr.clock).not.toHaveBeenCalled();
    expect(component.error()).toBe(i18n.t('hr.clock.timeInFuture'));

    component.time.set('');
    expect(component.timeProblem()).toBe(i18n.t('hr.clock.timeRequired'));

    // Back to now: nothing is in the way, and no time is sent.
    component.when.set('now');
    expect(component.timeProblem()).toBeNull();
    await component.punch('101');
    expect(hr.clock).toHaveBeenCalledWith({ employeeNumber: '101', kind: 'In', source: 'clock-screen' });
  });
});

describe('ConfirmDialogComponent — focusConfirm and danger autofocus', () => {
  let fixture: ComponentFixture<ConfirmDialogComponent>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [ConfirmDialogComponent],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
  });

  afterEach(() => {
    fixture?.destroy();
  });

  it('sets autofocus on the confirm button when focusConfirm is true and danger is false', () => {
    fixture = TestBed.createComponent(ConfirmDialogComponent);
    const request: ConfirmRequest = {
      title: 'Confirm',
      focusConfirm: true,
      danger: false,
    };
    fixture.componentRef.setInput('request', request);
    fixture.detectChanges();

    const btn = fixture.nativeElement.querySelector('.btn-primary') as HTMLButtonElement;
    expect(btn).not.toBeNull();
    expect(btn.hasAttribute('autofocus')).toBeTrue();
  });

  it('does not set autofocus when focusConfirm is false or omitted', () => {
    fixture = TestBed.createComponent(ConfirmDialogComponent);
    const request: ConfirmRequest = {
      title: 'Confirm',
      danger: false,
    };
    fixture.componentRef.setInput('request', request);
    fixture.detectChanges();

    const btn = fixture.nativeElement.querySelector('.btn-primary') as HTMLButtonElement;
    expect(btn).not.toBeNull();
    expect(btn.hasAttribute('autofocus')).toBeFalse();
  });

  it('never sets autofocus when danger is true even if focusConfirm is true', () => {
    fixture = TestBed.createComponent(ConfirmDialogComponent);
    const request: ConfirmRequest = {
      title: 'Delete',
      focusConfirm: true,
      danger: true,
    };
    fixture.componentRef.setInput('request', request);
    fixture.detectChanges();

    const btn = fixture.nativeElement.querySelector('.btn-danger-solid') as HTMLButtonElement;
    expect(btn).not.toBeNull();
    expect(btn.hasAttribute('autofocus')).toBeFalse();
  });
});
