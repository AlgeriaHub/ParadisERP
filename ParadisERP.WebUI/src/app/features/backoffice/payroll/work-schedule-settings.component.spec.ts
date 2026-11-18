import { ComponentFixture, TestBed } from '@angular/core/testing';
import { WorkScheduleSettingsComponent } from './work-schedule-settings.component';
import { WorkSchedule } from './payroll-api.service';

describe('WorkScheduleSettingsComponent', () => {
  let fixture: ComponentFixture<WorkScheduleSettingsComponent>;
  let component: WorkScheduleSettingsComponent;

  const initialSchedule: WorkSchedule = {
    start: '08:00',
    end: '17:00',
    breakMinutes: 60,
    lateGraceMinutes: 0,
    restDays: [5], // Friday
    periods: [],
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [WorkScheduleSettingsComponent],
    });
    fixture = TestBed.createComponent(WorkScheduleSettingsComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('schedule', initialSchedule);
    fixture.detectChanges();
  });

  it('editing start emits a new object', () => {
    let emitted: WorkSchedule | undefined;
    component.scheduleChange.subscribe((val) => {
      emitted = val;
    });

    component.setStart('09:00');

    expect(emitted).toBeDefined();
    expect(emitted?.start).toBe('09:00');
    expect(emitted).not.toBe(initialSchedule);
    expect(initialSchedule.start).toBe('08:00'); // not mutated
  });

  it('adds and removes a period', () => {
    const emitted: WorkSchedule[] = [];
    component.scheduleChange.subscribe((val) => {
      emitted.push(val);
    });

    // Add a period
    component.addPeriod();
    expect(emitted.length).toBe(1);
    expect(emitted[0].periods.length).toBe(1);
    expect(emitted[0].periods[0].start).toBe('08:00');

    // Feed the updated schedule back into the input
    fixture.componentRef.setInput('schedule', emitted[0]);
    fixture.detectChanges();

    // Remove the period
    component.removePeriod(0);
    expect(emitted.length).toBe(2);
    expect(emitted[1].periods.length).toBe(0);
  });

  it('the last working day cannot be ticked as rest', () => {
    // 6 days are rest days (0..5: Sun..Fri), leaving only Saturday (6) as working day
    const sixRestDaysSchedule: WorkSchedule = {
      ...initialSchedule,
      restDays: [0, 1, 2, 3, 4, 5],
    };
    fixture.componentRef.setInput('schedule', sixRestDaysSchedule);
    fixture.detectChanges();

    expect(component.isLastUnticked(6)).toBeTrue();
    expect(component.isLastUnticked(0)).toBeFalse();

    const emitted: WorkSchedule[] = [];
    component.scheduleChange.subscribe((val) => {
      emitted.push(val);
    });

    // Attempting to tick the last unticked day (Saturday = 6) must do nothing
    component.toggleRestDay(6, true);
    expect(emitted.length).toBe(0);

    // Unticking an existing rest day (e.g. Friday = 5) is allowed
    component.toggleRestDay(5, false);
    expect(emitted.length).toBe(1);
    expect(emitted[0].restDays).toEqual([0, 1, 2, 3, 4]);
  });
});
