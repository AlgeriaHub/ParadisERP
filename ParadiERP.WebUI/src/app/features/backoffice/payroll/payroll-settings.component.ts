import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiError } from '../../../core/api.service';
import { adminErrorMessage } from '../../../core/admin.service';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService } from '../../../core/regional.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { FoldComponent } from '../../../ui/fold.component';
import { actionRequest, removeLineRequest, saveRequest } from '../../../ui/confirm-requests';
import {
  AllowanceDef,
  OvertimeRuleDef,
  PayrollApi,
  PayrollSettings,
  PayrollSettingsView,
  TaxBracket,
  TextByLang,
  WorkSchedule,
} from './payroll-api.service';
import { WorkScheduleSettingsComponent } from './work-schedule-settings.component';

/**
 * The figures every payslip is computed against, in two groups that are not the same kind of
 * thing.
 *
 * <p><b>The company's own practice</b> — what it pays and how it counts time — is an ordinary
 * administrative decision. <b>The statutory figures</b> — contribution rates, the income-tax
 * scale, the minimum wage — come from law. docs/07 §5.2 kept the second group out of every
 * company login's reach; the owner reversed that on 2026-09-24 because the module ships with
 * <b>placeholder</b> figures and somebody in the company has to be able to correct them. So the
 * second group is editable here too — by whoever holds `payroll.edit_statutory`, which an admin
 * holds by the role.</p>
 *
 * <p>What the reversal did not change is that nothing in this system writes a statutory figure
 * from general knowledge. Until an accountant has confirmed them the banner stays up, and every
 * run and payslip computed under them is marked provisional — on screen and on paper. That mark,
 * not a locked control, is the protection.</p>
 *
 * <p>The statutory group is shown to everyone who can open this screen and disabled for whoever
 * may not move it, with the reason in words. A control that simply vanishes teaches nobody that
 * the figure exists or who to ask.</p>
 */
@Component({
  selector: 'app-payroll-settings',
  standalone: true,
  imports: [FormsModule, FoldComponent, WorkScheduleSettingsComponent],
  template: `
    <div class="bo-stack">
      @if (loading()) {
        <p class="notice">{{ i18n.t('common.loading') }}</p>
      } @else {
      @if (view(); as v) {
        @if (v.settings.needsReview) {
          <div class="notice warn banner" role="status">
            <strong>{{ i18n.t('hr.settings.provisional.title') }}</strong>
            <p>{{ i18n.t('hr.settings.provisional.body') }}</p>
            <p>{{ i18n.t('hr.settings.provisional.ask') }}</p>
          </div>
        } @else {
          <p class="notice ok" role="status">
            {{
              i18n.t('hr.settings.confirmed', {
                who: v.settings.confirmedBy ?? '—',
                when: v.settings.confirmedAtUtc ? i18n.dateTime(v.settings.confirmedAtUtc) : '—',
              })
            }}
          </p>
        }

        @if (error()) {
          <p class="notice danger" role="alert">{{ error() }}</p>
        }
        @if (saved()) {
          <p class="notice ok" role="status">{{ saved() }}</p>
        }

        <!-- ---------- the company's own figures ---------- -->
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('hr.settings.company') }}</h2>
            <p class="bo-panel__note">{{ i18n.t('hr.settings.companyHint') }}</p>
          </div>

          <!-- Each group folds. The cycle opens: it is first, and its two figures are the ones Save
               checks. The two tables are set up once and start folded. -->
          <div class="bo-panel__body bo-form bo-form--full">
            <app-fold class="bo-group bo-group--row" [heading]="i18n.t('hr.settings.cycle')" [open]="true">
              <p class="bo-group__note" foldLead>{{ i18n.t('hr.settings.cycleHint') }}</p>
              <div class="bo-fields bo-fields--roomy">
                <div class="bo-field" [class.is-invalid]="daysInvalid()">
                  <label for="ps-days">{{ i18n.t('hr.settings.workingDays') }}</label>
                  <input
                    id="ps-days"
                    type="number"
                    min="1"
                    max="31"
                    step="0.5"
                    class="num mono"
                    [disabled]="!v.canEditCompany"
                    [ngModel]="draft().workingDaysPerMonth"
                    (ngModelChange)="setNumber('workingDaysPerMonth', $event)"
                  />
                  @if (daysInvalid()) {
                    <span class="bo-error" role="alert">{{ i18n.t('hr.settings.workingDaysRange') }}</span>
                  }
                </div>
                <div class="bo-field" [class.is-invalid]="hoursInvalid()">
                  <label for="ps-hours">{{ i18n.t('hr.settings.hoursPerDay') }}</label>
                  <input
                    id="ps-hours"
                    type="number"
                    min="1"
                    max="24"
                    step="0.5"
                    class="num mono"
                    [disabled]="!v.canEditCompany"
                    [ngModel]="draft().hoursPerDay"
                    (ngModelChange)="setNumber('hoursPerDay', $event)"
                  />
                  @if (hoursInvalid()) {
                    <span class="bo-error" role="alert">{{ i18n.t('hr.settings.hoursRange') }}</span>
                  }
                </div>
                <!-- Answer 40 (owner, 2026-10-02): an absent day is the gross ÷ the working days,
                     taken off what the person would receive, not off the base alone. -->
                <div class="bo-check">
                  <input
                    id="ps-absence-gross"
                    type="checkbox"
                    [disabled]="!v.canEditCompany"
                    [ngModel]="!!draft().absenceOffGross"
                    (ngModelChange)="setAbsenceOffGross($event)"
                  />
                  <label for="ps-absence-gross">{{ i18n.t('hr.settings.absenceOffGross') }}</label>
                </div>
                <!-- Workers' bonuses paid by payment voucher (owner, 2026-11-08): the bases they count in,
                     the company's choice until the accountant answers. -->
                <div class="bo-check">
                  <input
                    id="ps-bonus-social"
                    type="checkbox"
                    [disabled]="!v.canEditCompany"
                    [ngModel]="draft().voucherBonusInSocialBase !== false"
                    (ngModelChange)="setBonusFlag('voucherBonusInSocialBase', $event)"
                  />
                  <label for="ps-bonus-social">{{ i18n.t('hr.settings.voucherBonusInSocialBase') }}</label>
                </div>
                <div class="bo-check">
                  <input
                    id="ps-bonus-tax"
                    type="checkbox"
                    [disabled]="!v.canEditCompany"
                    [ngModel]="draft().voucherBonusInTaxBase !== false"
                    (ngModelChange)="setBonusFlag('voucherBonusInTaxBase', $event)"
                  />
                  <label for="ps-bonus-tax">{{ i18n.t('hr.settings.voucherBonusInTaxBase') }}</label>
                </div>
                <div class="bo-field">
                  <label for="ps-mode">{{ i18n.t('hr.settings.attendanceMode') }}</label>
                  <select
                    id="ps-mode"
                    [disabled]="!v.canEditCompany"
                    [ngModel]="draft().defaultAttendanceMode"
                    (ngModelChange)="setAttendanceMode($event)"
                  >
                    <option value="Declared">{{ i18n.t('hr.attendance.mode.Declared') }}</option>
                    <option value="Recorded">{{ i18n.t('hr.attendance.mode.Recorded') }}</option>
                  </select>
                  <p class="bo-hint">{{ i18n.t('hr.settings.attendanceModeHint') }}</p>
                </div>
              </div>
            </app-fold>

            <app-work-schedule-settings
              [schedule]="draft().schedule"
              [disabled]="!v.canEditCompany"
              (scheduleChange)="setSchedule($event)"
            />

            <!-- allowances -->
            <app-fold class="bo-group" [heading]="i18n.t('hr.settings.allowances')">
              <p class="bo-group__note" foldLead>{{ i18n.t('hr.settings.allowancesHint') }}</p>
              <div class="bo-scroll">
                <table class="bo-data">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('hr.field.key') }}</th>
                      <th>{{ i18n.t('hr.field.label') }}</th>
                      <th>{{ i18n.t('hr.field.calc') }}</th>
                      <th class="num">{{ i18n.t('hr.field.value') }}</th>
                      <th>{{ i18n.t('hr.field.inSocialBase') }}</th>
                      <th>{{ i18n.t('hr.field.inTaxBase') }}</th>
                      <th>{{ i18n.t('hr.field.active') }}</th>
                      <th>{{ i18n.t('common.actions') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (row of draft().allowances; track $index) {
                      <tr>
                        <td [attr.data-label]="i18n.t('hr.field.key')">
                          <input
                            class="mono bo-input-narrow"
                            [disabled]="!v.canEditCompany"
                            [ngModel]="row.key"
                            (ngModelChange)="setText(row, 'key', $event)"
                          />
                        </td>
                        <td [attr.data-label]="i18n.t('hr.field.label')">
                          <input
                            [disabled]="!v.canEditCompany"
                            [ngModel]="label(row.labels)"
                            (ngModelChange)="setLabel(row.labels, $event)"
                          />
                        </td>
                        <td [attr.data-label]="i18n.t('hr.field.calc')">
                          <select
                            [disabled]="!v.canEditCompany"
                            [ngModel]="row.calculation"
                            (ngModelChange)="setCalculation(row, $event)"
                          >
                            <option value="FixedAmount">{{ i18n.t('hr.calc.FixedAmount') }}</option>
                            <option value="PercentOfBase">{{ i18n.t('hr.calc.PercentOfBase') }}</option>
                          </select>
                        </td>
                        <td class="num" [attr.data-label]="i18n.t('hr.field.value')">
                          <input
                            type="number"
                            min="0"
                            class="num mono bo-input-narrow"
                            [step]="row.calculation === 'PercentOfBase' ? 0.1 : regional.moneyStep()"
                            [disabled]="!v.canEditCompany"
                            [ngModel]="row.value"
                            (ngModelChange)="setRowNumber(row, 'value', $event)"
                          />
                        </td>
                        <td [attr.data-label]="i18n.t('hr.field.inSocialBase')">
                          <input
                            type="checkbox"
                            [disabled]="!v.canEditCompany"
                            [ngModel]="row.subjectToCnas"
                            (ngModelChange)="setFlag(row, 'subjectToCnas', $event)"
                          />
                        </td>
                        <td [attr.data-label]="i18n.t('hr.field.inTaxBase')">
                          <input
                            type="checkbox"
                            [disabled]="!v.canEditCompany"
                            [ngModel]="row.subjectToIrg"
                            (ngModelChange)="setFlag(row, 'subjectToIrg', $event)"
                          />
                        </td>
                        <td [attr.data-label]="i18n.t('hr.field.active')">
                          <input
                            type="checkbox"
                            [disabled]="!v.canEditCompany"
                            [ngModel]="row.isActive"
                            (ngModelChange)="setFlag(row, 'isActive', $event)"
                          />
                        </td>
                        <td class="bo-cell-actions" [attr.data-label]="i18n.t('common.actions')">
                          <button
                            type="button"
                            class="btn-ghost bo-small"
                            [disabled]="!v.canEditCompany || busy()"
                            (click)="removeAllowance($index)"
                          >
                            {{ i18n.t('hr.settings.remove') }}
                          </button>
                        </td>
                      </tr>
                    } @empty {
                      <tr>
                        <td colspan="8" class="bo-empty">
                          <strong>{{ i18n.t('hr.settings.noAllowances') }}</strong>
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
              <div class="bo-actions bo-actions--start">
                <button type="button" class="btn-ghost" [disabled]="!v.canEditCompany || busy()" (click)="addAllowance()">
                  {{ i18n.t('hr.settings.addAllowance') }}
                </button>
              </div>
              <p class="bo-hint">{{ i18n.t('hr.settings.keyRule') }}</p>
            </app-fold>

            <!-- overtime bands -->
            <app-fold class="bo-group" [heading]="i18n.t('hr.settings.overtime')">
              <p class="bo-group__note" foldLead>{{ i18n.t('hr.settings.overtimeHint') }}</p>
              <div class="bo-scroll">
                <table class="bo-data">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('hr.field.key') }}</th>
                      <th>{{ i18n.t('hr.field.label') }}</th>
                      <th>{{ i18n.t('hr.field.band') }}</th>
                      <th class="num">{{ i18n.t('hr.field.multiplier') }}</th>
                      <th>{{ i18n.t('hr.field.unit') }}</th>
                      <th>{{ i18n.t('hr.field.inSocialBase') }}</th>
                      <th>{{ i18n.t('hr.field.inTaxBase') }}</th>
                      <th>{{ i18n.t('hr.field.active') }}</th>
                      <th>{{ i18n.t('common.actions') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (row of draft().overtimeRules; track $index) {
                      <tr>
                        <td [attr.data-label]="i18n.t('hr.field.key')">
                          <input
                            class="mono bo-input-narrow"
                            [disabled]="!v.canEditCompany"
                            [ngModel]="row.key"
                            (ngModelChange)="setText(row, 'key', $event)"
                          />
                        </td>
                        <td [attr.data-label]="i18n.t('hr.field.label')">
                          <input
                            [disabled]="!v.canEditCompany"
                            [ngModel]="label(row.labels)"
                            (ngModelChange)="setLabel(row.labels, $event)"
                          />
                        </td>
                        <td [attr.data-label]="i18n.t('hr.field.band')">
                          <select
                            [disabled]="!v.canEditCompany"
                            [ngModel]="row.appliesTo"
                            (ngModelChange)="setBand(row, $event)"
                          >
                            <option value="Weekday">{{ i18n.t('hr.band.Weekday') }}</option>
                            <option value="Night">{{ i18n.t('hr.band.Night') }}</option>
                            <option value="WeeklyRest">{{ i18n.t('hr.band.WeeklyRest') }}</option>
                            <option value="PublicHoliday">{{ i18n.t('hr.band.PublicHoliday') }}</option>
                          </select>
                        </td>
                        <td class="num" [attr.data-label]="i18n.t('hr.field.multiplier')">
                          <input
                            type="number"
                            min="1"
                            max="5"
                            step="0.05"
                            class="num mono bo-input-narrow"
                            [disabled]="!v.canEditCompany"
                            [ngModel]="row.multiplier"
                            (ngModelChange)="setRowNumber(row, 'multiplier', $event)"
                          />
                        </td>
                        <td [attr.data-label]="i18n.t('hr.field.unit')">
                          <select
                            [disabled]="!v.canEditCompany"
                            [ngModel]="row.unit ?? 'Hours'"
                            (ngModelChange)="setUnit(row, $event)"
                          >
                            <option value="Hours">{{ i18n.t('hr.unit.Hours') }}</option>
                            <option value="Days">{{ i18n.t('hr.unit.Days') }}</option>
                          </select>
                        </td>
                        <td [attr.data-label]="i18n.t('hr.field.inSocialBase')">
                          <input
                            type="checkbox"
                            [disabled]="!v.canEditCompany"
                            [ngModel]="row.subjectToCnas"
                            (ngModelChange)="setFlag(row, 'subjectToCnas', $event)"
                          />
                        </td>
                        <td [attr.data-label]="i18n.t('hr.field.inTaxBase')">
                          <input
                            type="checkbox"
                            [disabled]="!v.canEditCompany"
                            [ngModel]="row.subjectToIrg"
                            (ngModelChange)="setFlag(row, 'subjectToIrg', $event)"
                          />
                        </td>
                        <td [attr.data-label]="i18n.t('hr.field.active')">
                          <input
                            type="checkbox"
                            [disabled]="!v.canEditCompany"
                            [ngModel]="row.isActive"
                            (ngModelChange)="setFlag(row, 'isActive', $event)"
                          />
                        </td>
                        <td class="bo-cell-actions" [attr.data-label]="i18n.t('common.actions')">
                          <button
                            type="button"
                            class="btn-ghost bo-small"
                            [disabled]="!v.canEditCompany || busy()"
                            (click)="removeOvertime($index)"
                          >
                            {{ i18n.t('hr.settings.remove') }}
                          </button>
                        </td>
                      </tr>
                    } @empty {
                      <tr>
                        <td colspan="8" class="bo-empty">
                          <strong>{{ i18n.t('hr.settings.noOvertime') }}</strong>
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
              <div class="bo-actions bo-actions--start">
                <button type="button" class="btn-ghost" [disabled]="!v.canEditCompany || busy()" (click)="addOvertime()">
                  {{ i18n.t('hr.settings.addOvertime') }}
                </button>
              </div>
              <p class="bo-hint">{{ i18n.t('hr.settings.multiplierRule') }}</p>
            </app-fold>
          </div>

          <div class="bo-panel__foot">
            @if (!v.canEditCompany) {
              <p class="bo-hint">{{ i18n.t('hr.settings.companyReadOnly') }}</p>
            }
            <button
              type="button"
              class="btn-primary"
              [disabled]="!v.canEditCompany || busy() || !companyValid()"
              (click)="saveCompany()"
            >
              {{ i18n.t('hr.settings.saveCompany') }}
            </button>
          </div>
        </section>

        <!-- ---------- the statutory figures ---------- -->
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('hr.settings.statutory') }}</h2>
            <p class="bo-panel__note">{{ i18n.t('hr.settings.statutoryHint') }}</p>
          </div>

          <div class="bo-panel__body bo-form bo-form--full">
            @if (!v.canEditStatutory) {
              <p class="notice warn" role="status">{{ i18n.t('hr.settings.statutoryReadOnly') }}</p>
            }

            <!-- The contributions open, first and with the rates Save checks; the rest start folded. -->
            <app-fold class="bo-group bo-group--row" [heading]="i18n.t('hr.settings.contributions')" [open]="true">
              <p class="bo-group__note" foldLead>{{ i18n.t('hr.settings.contributionsHint') }}</p>
              <div class="bo-fields bo-fields--roomy">
                <div class="bo-field" [class.is-invalid]="rateInvalid(draft().socialEmployeeRate)">
                  <label for="ps-cnas-e">{{ i18n.t('hr.settings.socialEmployee') }}</label>
                  <input
                    id="ps-cnas-e"
                    type="number"
                    min="0"
                    max="100"
                    step="0.01"
                    class="num mono"
                    [disabled]="!v.canEditStatutory"
                    [ngModel]="draft().socialEmployeeRate"
                    (ngModelChange)="setNumber('socialEmployeeRate', $event)"
                  />
                  <p class="bo-hint">{{ i18n.t('hr.settings.percentOfBase') }}</p>
                  @if (rateInvalid(draft().socialEmployeeRate)) {
                    <span class="bo-error" role="alert">{{ i18n.t('hr.settings.rateRange') }}</span>
                  }
                </div>
                <div class="bo-field" [class.is-invalid]="rateInvalid(draft().socialEmployerRate)">
                  <label for="ps-cnas-r">{{ i18n.t('hr.settings.socialEmployer') }}</label>
                  <input
                    id="ps-cnas-r"
                    type="number"
                    min="0"
                    max="100"
                    step="0.01"
                    class="num mono"
                    [disabled]="!v.canEditStatutory"
                    [ngModel]="draft().socialEmployerRate"
                    (ngModelChange)="setNumber('socialEmployerRate', $event)"
                  />
                  <p class="bo-hint">{{ i18n.t('hr.settings.employerCost') }}</p>
                  @if (rateInvalid(draft().socialEmployerRate)) {
                    <span class="bo-error" role="alert">{{ i18n.t('hr.settings.rateRange') }}</span>
                  }
                </div>
                <div class="bo-field" [class.is-invalid]="draft().minimumWage < 0">
                  <label for="ps-min">{{ i18n.t('hr.settings.minimumWage') }}</label>
                  <input
                    id="ps-min"
                    type="number"
                    min="0"
                    class="num mono"
                    [step]="regional.moneyStep()"
                    [disabled]="!v.canEditStatutory"
                    [ngModel]="draft().minimumWage"
                    (ngModelChange)="setNumber('minimumWage', $event)"
                  />
                  <p class="bo-hint">{{ i18n.t('hr.settings.minimumWageHint') }}</p>
                  @if (draft().minimumWage < 0) {
                    <span class="bo-error" role="alert">{{ i18n.t('hr.settings.notNegative') }}</span>
                  }
                </div>
              </div>
            </app-fold>

            <!-- A figure a later finance law adds appears here only once the server both
                 carries it and registers it as statutory. Offering it before either is true
                 would be a control whose save does nothing — the dead wiring this codebase has
                 already paid for five times. -->
            @if (statutory('incomeTaxMode')) {
              <app-fold class="bo-group bo-group--row" [heading]="i18n.t('hr.settings.taxMode')">
                <p class="bo-group__note" foldLead>{{ i18n.t('hr.settings.taxModeHint') }}</p>
                <div class="bo-fields">
                  <div class="bo-field">
                    <label for="ps-taxmode">{{ i18n.t('hr.settings.taxMode') }}</label>
                    <select
                      id="ps-taxmode"
                      [disabled]="!v.canEditStatutory"
                      [ngModel]="text('incomeTaxMode')"
                      (ngModelChange)="setText(draft(), 'incomeTaxMode', $event)"
                    >
                      <option value="NotConfigured">{{ i18n.t('hr.taxMode.NotConfigured') }}</option>
                      <option value="Scale">{{ i18n.t('hr.taxMode.Scale') }}</option>
                      <option value="None">{{ i18n.t('hr.taxMode.None') }}</option>
                    </select>
                  </div>
                </div>
              </app-fold>
            }

            <app-fold class="bo-group bo-group--row" [heading]="i18n.t('hr.settings.taxScale')">
              <p class="bo-group__note" foldLead>{{ i18n.t('hr.settings.taxScaleHint') }}</p>
              <div class="bo-scroll">
                <table class="bo-data">
                  <thead>
                    <tr>
                      <th class="num">{{ i18n.t('hr.settings.bracketUpTo') }}</th>
                      <th class="num">{{ i18n.t('hr.settings.bracketRate') }}</th>
                      <th>{{ i18n.t('common.actions') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (bracket of draft().incomeTaxBrackets; track $index) {
                      <tr>
                        <td class="num" [attr.data-label]="i18n.t('hr.settings.bracketUpTo')">
                          @if ($index === draft().incomeTaxBrackets.length - 1) {
                            <span class="bo-soft">{{ i18n.t('hr.settings.bracketTop') }}</span>
                          } @else {
                            <input
                              type="number"
                              min="0"
                              class="num mono bo-input-narrow"
                              [step]="regional.moneyStep()"
                              [disabled]="!v.canEditStatutory"
                              [ngModel]="bracket.upTo"
                              (ngModelChange)="setBracketCeiling(bracket, $event)"
                            />
                          }
                        </td>
                        <td class="num" [attr.data-label]="i18n.t('hr.settings.bracketRate')">
                          <input
                            type="number"
                            min="0"
                            max="100"
                            step="0.01"
                            class="num mono bo-input-narrow"
                            [class.is-invalid]="rateInvalid(bracket.rate)"
                            [attr.aria-invalid]="rateInvalid(bracket.rate) || null"
                            [disabled]="!v.canEditStatutory"
                            [ngModel]="bracket.rate"
                            (ngModelChange)="setRowNumber(bracket, 'rate', $event)"
                          />
                        </td>
                        <td class="bo-cell-actions" [attr.data-label]="i18n.t('common.actions')">
                          <button
                            type="button"
                            class="btn-ghost bo-small"
                            [disabled]="!v.canEditStatutory || busy()"
                            (click)="removeBracket($index)"
                          >
                            {{ i18n.t('hr.settings.remove') }}
                          </button>
                        </td>
                      </tr>
                    } @empty {
                      <tr>
                        <td colspan="3" class="bo-empty">
                          <strong>{{ i18n.t('hr.settings.noBrackets') }}</strong>
                          <span>{{ i18n.t('hr.settings.noBracketsWhy') }}</span>
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
              <div class="bo-actions bo-actions--start">
                <button type="button" class="btn-ghost" [disabled]="!v.canEditStatutory || busy()" (click)="addBracket()">
                  {{ i18n.t('hr.settings.addBracket') }}
                </button>
              </div>
              <p class="bo-hint">{{ i18n.t('hr.settings.bracketRule') }}</p>
              @if (bracketRatesInvalid()) {
                <span class="bo-error" role="alert">{{ i18n.t('hr.settings.bracketRateRange') }}</span>
              }
            </app-fold>

            @if (
              statutory('employeeContributionReducesTaxBase') ||
              statutory('socialBaseFloor') ||
              statutory('socialBaseCeiling')
            ) {
              <app-fold class="bo-group bo-group--row" [heading]="i18n.t('hr.settings.bases')">
                <p class="bo-group__note" foldLead>{{ i18n.t('hr.settings.basesHint') }}</p>
                @if (statutory('employeeContributionReducesTaxBase')) {
                  <div class="bo-check">
                    <input
                      id="ps-tb"
                      type="checkbox"
                      [disabled]="!v.canEditStatutory"
                      [ngModel]="flag('employeeContributionReducesTaxBase')"
                      (ngModelChange)="setFlag(draft(), 'employeeContributionReducesTaxBase', $event)"
                    />
                    <label for="ps-tb">{{ i18n.t('hr.settings.contributionOffTaxBase') }}</label>
                  </div>
                }
                <div class="bo-fields bo-fields--roomy">
                  @if (statutory('socialBaseFloor')) {
                    <div class="bo-field">
                      <label for="ps-floor">{{ i18n.t('hr.settings.socialFloor') }}</label>
                      <input
                        id="ps-floor"
                        type="number"
                        min="0"
                        class="num mono"
                        [step]="regional.moneyStep()"
                        [disabled]="!v.canEditStatutory"
                        [ngModel]="nullableNumber('socialBaseFloor')"
                        (ngModelChange)="setNullableNumber('socialBaseFloor', $event)"
                      />
                      <p class="bo-hint">{{ i18n.t('hr.settings.boundsHint') }}</p>
                    </div>
                  }
                  @if (statutory('socialBaseCeiling')) {
                    <div class="bo-field">
                      <label for="ps-ceiling">{{ i18n.t('hr.settings.socialCeiling') }}</label>
                      <input
                        id="ps-ceiling"
                        type="number"
                        min="0"
                        class="num mono"
                        [step]="regional.moneyStep()"
                        [disabled]="!v.canEditStatutory"
                        [ngModel]="nullableNumber('socialBaseCeiling')"
                        (ngModelChange)="setNullableNumber('socialBaseCeiling', $event)"
                      />
                      <p class="bo-hint">{{ i18n.t('hr.settings.boundsHint') }}</p>
                    </div>
                  }
                </div>
              </app-fold>
            }
          </div>

          <div class="bo-panel__foot">
            <button
              type="button"
              class="btn-primary"
              [disabled]="!v.canEditStatutory || busy() || !statutoryValid()"
              (click)="saveStatutory()"
            >
              {{ i18n.t('hr.settings.saveStatutory') }}
            </button>
          </div>
        </section>

        <!-- ---------- confirming the figures ---------- -->
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('hr.settings.confirmTitle') }}</h2>
            <p class="bo-panel__note">{{ i18n.t('hr.settings.confirmHint') }}</p>
          </div>
          <div class="bo-panel__body bo-form">
            <div class="bo-fields">
              <div class="bo-field bo-field--wide">
                <label for="ps-who">{{ i18n.t('hr.settings.confirmWho') }}</label>
                <input
                  id="ps-who"
                  [disabled]="!v.canEditStatutory"
                  [ngModel]="confirmedBy()"
                  (ngModelChange)="confirmedBy.set($event)"
                />
                <p class="bo-hint">{{ i18n.t('hr.settings.confirmWhoHint') }}</p>
              </div>
            </div>
            <p class="bo-hint">{{ i18n.t('hr.settings.unconfirmNote') }}</p>
            @if (!v.canEditStatutory) {
              <p class="bo-hint">{{ i18n.t('hr.settings.statutoryReadOnly') }}</p>
            }
          </div>
          <div class="bo-panel__foot">
            @if (v.updatedAtUtc) {
              <p class="bo-hint">
                {{ i18n.t('hr.settings.lastSaved', { who: v.updatedBy ?? '—', when: i18n.dateTime(v.updatedAtUtc) }) }}
              </p>
            }
            <button
              type="button"
              class="btn-primary"
              [disabled]="!v.canEditStatutory || busy() || !v.settings.needsReview"
              (click)="confirm()"
            >
              {{ i18n.t('hr.settings.confirmAction') }}
            </button>
          </div>
        </section>
      }
      }
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss'],
  styles: [
    `
      /* Three sentences, not one paragraph: the title, what the figures are, and what to do
         about them are three separate things to take in. */
      .banner {
        display: grid;
        gap: 0.3rem;
      }
      .banner p {
        margin: 0;
        max-inline-size: 76ch;
      }
      .bo-data input.is-invalid,
      .bo-data input[aria-invalid='true'] {
        border-color: var(--danger);
      }
    `,
  ],
})
export class PayrollSettingsComponent implements OnInit {
  private readonly api = inject(PayrollApi);
  // Not "confirm": that name is the statutory confirmation below.
  private readonly dialog = inject(ConfirmService);
  readonly i18n = inject(I18nService);
  readonly regional = inject(RegionalService);

  readonly view = signal<PayrollSettingsView | null>(null);
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly saved = signal<string | null>(null);
  readonly confirmedBy = signal('');

  /**
   * The figures being edited. A copy rather than the loaded object, so an unsaved figure is not
   * silently replaced by a reload — and so a company save can be rebuilt from the stored
   * statutory half rather than from whatever is half-typed in the other panel.
   */
  private readonly editing = signal<PayrollSettings>(empty());
  readonly draft = computed(() => this.editing());

  ngOnInit(): void {
    void this.load();
  }

  // ---------- reading ----------

  /**
   * Whether a figure belongs in the statutory group and can actually be saved there: the server
   * has to carry it <b>and</b> to have registered it as operator-owned, because the statutory
   * save copies exactly the fields that ownership table names and drops everything else.
   */
  statutory(field: string): boolean {
    return field in this.editing() && this.view()?.owners[field] === 'Operator';
  }

  text(field: string): string {
    const value = this.editing()[field];
    return typeof value === 'string' ? value : '';
  }

  flag(field: string): boolean {
    return this.editing()[field] === true;
  }

  /** A bound the law may or may not set. Empty is "there is none", never a bound of zero. */
  nullableNumber(field: string): number | null {
    const value = this.editing()[field];
    return typeof value === 'number' ? value : null;
  }

  setNullableNumber(field: string, value: unknown): void {
    const blank = value === null || value === undefined || value === '';
    this.editing.update((s) => ({ ...s, [field]: blank ? null : numeric(value) }) as PayrollSettings);
  }

  /** The label in the reader's own language, falling back rather than showing a blank cell. */
  label(labels: TextByLang | undefined): string {
    if (!labels) return '';
    return labels[this.i18n.lang()] ?? labels['fr'] ?? labels['en'] ?? '';
  }

  // ---------- editing ----------

  setNumber(field: string, value: unknown): void {
    this.editing.update((s) => ({ ...s, [field]: numeric(value) }) as PayrollSettings);
  }

  setBonusFlag(field: 'voucherBonusInSocialBase' | 'voucherBonusInTaxBase', value: boolean): void {
    this.editing.update((s) => ({ ...s, [field]: !!value }));
  }

  setAbsenceOffGross(value: boolean): void {
    this.editing.update((s) => ({ ...s, absenceOffGross: !!value }));
  }

  /** Kept for legacy callers; the retired control is no longer rendered. */
  setNetPayOnly(value: boolean): void {
    this.editing.update((s) => ({ ...s, netPayOnly: !!value }));
  }

  setAttendanceMode(value: string): void {
    this.editing.update((s) => ({ ...s, defaultAttendanceMode: value === 'Recorded' ? 'Recorded' : 'Declared' }));
  }

  setSchedule(schedule: WorkSchedule): void {
    this.editing.update((s) => ({ ...s, schedule }));
  }

  /** Rows are mutated in place and the object identity bumped, so a table keeps its focus. */
  setText<T extends object>(row: T, field: keyof T & string, value: string): void {
    (row as Record<string, unknown>)[field] = value;
    this.touch();
  }

  setRowNumber<T extends object>(row: T, field: keyof T & string, value: unknown): void {
    (row as Record<string, unknown>)[field] = numeric(value);
    this.touch();
  }

  setFlag<T extends object>(row: T, field: keyof T & string, value: unknown): void {
    (row as Record<string, unknown>)[field] = value === true;
    this.touch();
  }

  setLabel(labels: TextByLang, value: string): void {
    labels[this.i18n.lang()] = value;
    this.touch();
  }

  setCalculation(row: AllowanceDef, value: string): void {
    row.calculation = value === 'PercentOfBase' ? 'PercentOfBase' : 'FixedAmount';
    this.touch();
  }

  setBand(row: OvertimeRuleDef, value: string): void {
    row.appliesTo = value as OvertimeRuleDef['appliesTo'];
    this.touch();
  }

  setUnit(row: OvertimeRuleDef, value: string): void {
    row.unit = value === 'Days' ? 'Days' : 'Hours';
    this.touch();
  }

  /** An empty ceiling is the open-ended top step, not zero. */
  setBracketCeiling(bracket: TaxBracket, value: unknown): void {
    bracket.upTo = value === null || value === '' || value === undefined ? null : numeric(value);
    this.touch();
  }

  addAllowance(): void {
    this.editing().allowances.push({
      key: '',
      labels: {},
      calculation: 'FixedAmount',
      value: 0,
      subjectToCnas: true,
      subjectToIrg: true,
      isActive: true,
      sortOrder: this.editing().allowances.length + 1,
    });
    this.touch();
  }

  /**
   * The three tables below ask before a row goes only when the row holds something: a key, a
   * label or a figure. A row just added and left empty has nothing to lose.
   */
  async removeAllowance(index: number): Promise<void> {
    const row = this.editing().allowances[index];
    if (!row) return;
    if (filled(row.key, row.labels, row.value !== 0) && !(await this.dialog.ask(removeLineRequest(this.i18n)))) return;
    this.editing().allowances.splice(index, 1);
    this.touch();
  }

  addOvertime(): void {
    this.editing().overtimeRules.push({
      key: '',
      labels: {},
      appliesTo: 'Weekday',
      multiplier: 1,
      unit: 'Hours',
      // Said here rather than left to the server's own defaults. A band that arrives without
      // these comes back with both true, and until the table carried the two columns there was
      // no screen anywhere that could say otherwise — the engine read a flag nobody set.
      subjectToCnas: true,
      subjectToIrg: true,
      isActive: true,
    });
    this.touch();
  }

  async removeOvertime(index: number): Promise<void> {
    const row = this.editing().overtimeRules[index];
    if (!row) return;
    if (filled(row.key, row.labels, row.multiplier !== 1) && !(await this.dialog.ask(removeLineRequest(this.i18n)))) return;
    this.editing().overtimeRules.splice(index, 1);
    this.touch();
  }

  /**
   * A new step is added before the open top one and takes its ceiling from nothing: the server
   * refuses an out-of-order scale, which is the check that matters, and guessing a ceiling here
   * would be inventing a figure.
   */
  addBracket(): void {
    const brackets = this.editing().incomeTaxBrackets;
    brackets.splice(Math.max(brackets.length - 1, 0), 0, { upTo: null, rate: 0 });
    // The step that is no longer last needs a ceiling; the one that is must not have one.
    brackets.forEach((b, i) => {
      if (i === brackets.length - 1) b.upTo = null;
      else b.upTo = b.upTo ?? 0;
    });
    this.touch();
  }

  async removeBracket(index: number): Promise<void> {
    const brackets = this.editing().incomeTaxBrackets;
    const row = brackets[index];
    if (!row) return;
    if ((row.rate !== 0 || (row.upTo ?? 0) !== 0) && !(await this.dialog.ask(removeLineRequest(this.i18n)))) return;
    brackets.splice(index, 1);
    if (brackets.length > 0) brackets[brackets.length - 1].upTo = null;
    this.touch();
  }

  // ---------- validity the screen can judge for itself ----------

  readonly daysInvalid = computed(() => !inRange(this.editing().workingDaysPerMonth, 1, 31));
  readonly hoursInvalid = computed(() => !inRange(this.editing().hoursPerDay, 1, 24));
  readonly companyValid = computed(() => !this.daysInvalid() && !this.hoursInvalid());

  rateInvalid(rate: number): boolean {
    return !inRange(rate, 0, 100);
  }

  readonly bracketRatesInvalid = computed(() =>
    this.editing().incomeTaxBrackets.some((b) => this.rateInvalid(b.rate))
  );

  readonly statutoryValid = computed(() => {
    const s = this.editing();
    return (
      !this.rateInvalid(s.socialEmployeeRate) &&
      !this.rateInvalid(s.socialEmployerRate) &&
      s.minimumWage >= 0 &&
      s.incomeTaxBrackets.every((b) => inRange(b.rate, 0, 100))
    );
  });

  // ---------- saving ----------

  /**
   * The company half only. The statutory figures are taken from what the server last returned
   * rather than from the draft, so a half-typed rate in the other panel cannot ride along and be
   * refused — the refusal would be correct and the admin would have no idea what it referred to.
   */
  async saveCompany(): Promise<void> {
    const loaded = this.view();
    if (!loaded || !this.companyValid()) return;
    if (!(await this.dialog.ask(saveRequest(this.i18n)))) return;
    const draft = this.editing();
    const body: PayrollSettings = {
      ...loaded.settings,
      allowances: draft.allowances,
      overtimeRules: draft.overtimeRules,
      defaultAttendanceMode: draft.defaultAttendanceMode,
      workingDaysPerMonth: draft.workingDaysPerMonth,
      hoursPerDay: draft.hoursPerDay,
      absenceOffGross: !!draft.absenceOffGross,
      voucherBonusInSocialBase: draft.voucherBonusInSocialBase !== false,
      voucherBonusInTaxBase: draft.voucherBonusInTaxBase !== false,
      // Retained for legacy settings snapshots; the control is no longer exposed for new slips.
      netPayOnly: !!draft.netPayOnly,
      schedule: draft.schedule,
    };
    await this.run(() => this.api.saveCompany(body, loaded.revision), 'hr.settings.savedCompany');
  }

  /**
   * The statutory half. The whole draft is sent: the server reads only the fields its own
   * ownership table marks statutory, so nothing else in it can take effect.
   */
  async saveStatutory(): Promise<void> {
    const loaded = this.view();
    if (!loaded || !this.statutoryValid()) return;
    if (!(await this.dialog.ask(saveRequest(this.i18n)))) return;
    await this.run(
      () => this.api.saveStatutory(this.editing(), loaded.revision, false, null),
      'hr.settings.savedStatutory',
    );
  }

  async confirm(): Promise<void> {
    const loaded = this.view();
    if (!loaded) return;
    if (!(await this.dialog.ask(actionRequest(this.i18n, this.i18n.t('hr.settings.confirmAction'))))) return;
    await this.run(
      () => this.api.confirmStatutory(loaded.revision, this.confirmedBy().trim() || null),
      'hr.settings.savedConfirmed',
    );
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    try {
      this.apply(await this.api.settings());
    } catch (e) {
      this.error.set(this.message(e));
    } finally {
      this.loading.set(false);
    }
  }

  private async run(call: () => Promise<PayrollSettingsView>, savedKey: string): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    this.saved.set(null);
    try {
      this.apply(await call());
      this.saved.set(this.i18n.t(savedKey));
    } catch (e) {
      this.error.set(this.message(e));
      // A refusal leaves the stored figures as they were, and the screen has to say so rather
      // than keep showing what was rejected as though it had been accepted.
      if (e instanceof ApiError && e.status === 409) await this.load();
    } finally {
      this.busy.set(false);
    }
  }

  private apply(view: PayrollSettingsView): void {
    this.view.set(view);
    const s = clone(view.settings);
    if (!s.schedule) s.schedule = defaultSchedule();
    this.editing.set(s);
    this.confirmedBy.set(view.settings.confirmedBy ?? '');
  }

  /**
   * Refusals in the reader's language: the framing is translated here, and the server's own
   * sentence is kept inside it because that sentence names the figure — "SocialEmployeeRate",
   * an overlapping bracket — and dropping it would leave a polite message that explains nothing.
   */
  private message(error: unknown): string {
    if (error instanceof ApiError) {
      if (error.status === 403) return this.i18n.t('hr.settings.forbidden');
      if (error.status === 409) return this.i18n.t('hr.settings.stale');
    }
    return this.i18n.t('hr.settings.refused', { message: adminErrorMessage(error) });
  }

  /** Rows are mutated in place; this is what tells the signal the object has moved on. */
  private touch(): void {
    this.editing.update((s) => ({ ...s }));
  }
}

function numeric(value: unknown): number {
  const parsed = typeof value === 'number' ? value : Number(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

function inRange(value: number, min: number, max: number): boolean {
  return Number.isFinite(value) && value >= min && value <= max;
}

function clone(settings: PayrollSettings): PayrollSettings {
  return JSON.parse(JSON.stringify(settings)) as PayrollSettings;
}

/** Whether a settings row holds anything somebody entered: a key, a label, or a changed figure. */
function filled(key: string, labels: TextByLang | undefined, figureChanged: boolean): boolean {
  return !!key?.trim() || Object.values(labels ?? {}).some((text) => !!text?.trim()) || figureChanged;
}

/** Only ever on screen for the instant before the first load answers. */
function empty(): PayrollSettings {
  return {
    countryCode: '',
    socialEmployeeRate: 0,
    socialEmployerRate: 0,
    socialNames: {},
    incomeTaxBrackets: [],
    incomeTaxNames: {},
    minimumWage: 0,
    allowances: [],
    overtimeRules: [],
    defaultAttendanceMode: 'Declared',
    workingDaysPerMonth: 26,
    hoursPerDay: 8,
    schedule: defaultSchedule(),
    confirmed: false,
    needsReview: true,
  };
}

function defaultSchedule(): WorkSchedule {
  return {
    start: '08:00',
    end: '17:00',
    breakMinutes: 60,
    lateGraceMinutes: 0,
    restDays: [5],
    periods: [],
  };
}
