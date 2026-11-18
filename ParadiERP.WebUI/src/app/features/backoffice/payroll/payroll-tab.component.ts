import { Component, OnInit, inject, input, signal } from '@angular/core';
import { I18nService } from '../../../core/i18n.service';
import { PayrollAdvancesComponent } from './advances.component';
import { HrAttendanceImportComponent } from './attendance-import.component';
import { HrAttendanceSheetComponent } from './attendance-sheet.component';
import { HrClockComponent } from './clock.component';
import { HrAttendanceReviewComponent } from './attendance-review.component';
import { HrEmployeesComponent } from './employees.component';
import { PayrollRunsComponent } from './payroll-runs.component';
import { PayrollSettingsComponent } from './payroll-settings.component';
import { StatutoryPaymentsComponent } from './statutory-payments.component';
import { WagePaymentsComponent } from './wage-payments.component';
import { OvertimeEntriesComponent } from './overtime-entries.component';

/**
 * One chip, one screen. The order is the order a month is worked in, not alphabetical: set the
 * figures, know who is on the payroll, capture their time, compute and approve the run, then the
 * three money screens in the order the money actually moves.
 */
type Section =
  | 'settings'
  | 'employees'
  | 'attendance'
  | 'overtime'
  | 'attendanceImport'
  | 'clock'
  | 'attendanceReview'
  | 'runs'
  | 'advances'
  | 'payments'
  | 'statutory';

interface Chip {
  section: Section;
  label: string;
}

/**
 * Personnel, as one section of the back office rather than eight entries in the navigation rail.
 *
 * <p>The rail already carries fourteen sections; by the eighth it had become a paragraph, which
 * is why it was grouped in the first place. Adding the employee list, the attendance sheet, the
 * badge clock, the payroll run and three money screens as seven more entries would undo that.
 * They are one entry — Personnel — and the chips inside it are the steps of one month's work.</p>
 *
 * <p>The whole group is one deferred chunk behind that entry: a manager who came to price a
 * product downloads none of it, and a cashier's terminal never downloads it at all. Nothing
 * inside loads its data until its chip is pressed, because each screen fetches in its own
 * <code>ngOnInit</code> and none of them is constructed before then.</p>
 *
 * <p>Every screen the module has is reachable from here. A screen that exists but that no chip
 * opens is the failure this project has already had five times, so the strip is the full list
 * rather than the popular half.</p>
 */
@Component({
  selector: 'app-payroll-tab',
  standalone: true,
  imports: [
    PayrollSettingsComponent,
    HrEmployeesComponent,
    HrAttendanceSheetComponent,
    HrAttendanceImportComponent,
    HrClockComponent,
    HrAttendanceReviewComponent,
    PayrollRunsComponent,
    PayrollAdvancesComponent,
    WagePaymentsComponent,
    StatutoryPaymentsComponent,
    OvertimeEntriesComponent,
  ],
  template: `
    <div class="bo-stack">
      @if (!page()) {
        <nav class="bo-chips no-print" [attr.aria-label]="i18n.t('backoffice.group.people')">
          @for (chip of chips; track chip.section) {
            <button
              type="button"
              [class.is-on]="section() === chip.section"
              [attr.aria-current]="section() === chip.section ? 'true' : null"
              (click)="show(chip.section)"
            >
              {{ i18n.t(chip.label) }}
            </button>
          }
        </nav>
      }

      @switch (section()) {
        @case ('settings') {
          <app-payroll-settings />
        }
        @case ('employees') {
          <app-hr-employees />
        }
        @case ('attendance') {
          <app-hr-attendance-sheet />
        }
        @case ('overtime') {
          <app-hr-overtime />
        }
        @case ('attendanceImport') {
          <app-hr-attendance-import />
        }
        @case ('clock') {
          <app-hr-clock />
        }
        @case ('attendanceReview') {
          <app-hr-attendance-review />
        }
        @case ('runs') {
          <app-payroll-runs />
        }
        @case ('advances') {
          <app-payroll-advances />
        }
        @case ('payments') {
          <app-wage-payments />
        }
        @case ('statutory') {
          <app-statutory-payments />
        }
      }
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss'],
})
export class PayrollTabComponent implements OnInit {
  readonly i18n = inject(I18nService);

  readonly chips: Chip[] = [
    { section: 'settings', label: 'backoffice.tabs.payrollSettings' },
    { section: 'employees', label: 'hr.section.employees' },
    { section: 'attendance', label: 'hr.section.attendance' },
    { section: 'overtime', label: 'hr.section.overtime' },
    { section: 'attendanceImport', label: 'hr.section.attendanceImport' },
    { section: 'clock', label: 'hr.section.clock' },
    { section: 'attendanceReview', label: 'hr.section.attendanceReview' },
    { section: 'runs', label: 'hr.section.runs' },
    { section: 'advances', label: 'hr.section.advances' },
    { section: 'payments', label: 'hr.section.payments' },
    { section: 'statutory', label: 'hr.section.statutory' },
  ];

  // The figures every payslip is computed against are still the placeholders the module ships
  // with, so this is where somebody arriving at Personnel for the first time should land.
  readonly section = signal<Section>('settings');

  /**
   * The chip the route opened this section on (core/nav.ts). Set, the sidebar is the navigation
   * and this component's own chip strip is not drawn; unset, it behaves as it always did.
   */
  readonly page = input<Section | null>(null);

  ngOnInit(): void {
    const page = this.page();
    if (page) this.section.set(page);
  }

  show(section: Section): void {
    this.section.set(section);
  }
}
