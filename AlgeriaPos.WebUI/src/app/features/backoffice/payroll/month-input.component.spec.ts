import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MonthInputComponent } from './month-input.component';

describe('MonthInputComponent', () => {
  let fixture: ComponentFixture<MonthInputComponent>;
  let component: MonthInputComponent;
  const selects = () => Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('select'));

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [MonthInputComponent] }).compileComponents();
    fixture = TestBed.createComponent(MonthInputComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('shows the YYYY-MM it is given as a month and a year, in Latin digits', () => {
    component.writeValue('2025-03');
    fixture.detectChanges();
    const [month, year] = selects();
    expect(month.value).toBe('3');
    expect(year.value).toBe('2025');
    expect(year.options[year.selectedIndex].textContent?.trim()).toBe('2025');
  });

  it('hands back YYYY-MM when either list changes', () => {
    const seen: string[] = [];
    component.registerOnChange((v) => seen.push(v));
    component.writeValue('2026-10');
    fixture.detectChanges();
    const [month, year] = selects();

    month.value = '9';
    month.dispatchEvent(new Event('change'));
    year.value = String(new Date().getFullYear() - 1);
    year.dispatchEvent(new Event('change'));

    expect(seen).toEqual(['2026-09', `${new Date().getFullYear() - 1}-09`]);
  });

  it('gives its id to the month list, not the host, so the label names it', () => {
    fixture.componentRef.setInput('id', 'run-period');
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).getAttribute('id')).toBeNull();
    expect(selects()[0].id).toBe('run-period');
  });

  it('keeps an old year it is given in the list', () => {
    component.writeValue('2019-12');
    fixture.detectChanges();
    expect(selects()[1].value).toBe('2019');
  });
});
