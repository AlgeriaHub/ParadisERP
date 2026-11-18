import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { AdminBranch, AdminService } from '../../../core/admin.service';
import { AuthService } from '../../../core/auth.service';
import { BranchLock } from '../../../core/branch-lock';
import { canOpen } from '../../../core/nav';
import { I18nService } from '../../../core/i18n.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { actionRequest, saveRequest } from '../../../ui/confirm-requests';
import { RegionalService } from '../../../core/regional.service';
import { formatDay, localizedName } from '../backoffice-format';
import { TouchedFields } from '../backoffice-forms';
import { isolate } from '../../../core/bidi';
import { FoldComponent } from '../../../ui/fold.component';
import { DateInputComponent } from '../../../ui/date-input.component';
import { MENU } from '../../../ui/menu.component';
import { StatusComponent } from '../../../ui/status.component';
import { StatusTone, toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { DEFAULT_PAGE_SIZE } from '../../../ui/pager';
import { PAGE_CHROME } from '../shell/page-chrome';
import { Employee, EmployeeAllowance, HrApi, UpsertEmployee, hrProblem } from './hr-api.service';
import { AllowanceDef, AttendanceMode, PayrollApi, PayrollSettings, TextByLang } from './payroll-api.service';
import { moneyErrorMessage } from './wage-money-api.service';

/** How one defined allowance applies to one person. Three states, because the engine reads three. */
type AllowanceChoice = 'default' | 'own' | 'none';

interface AllowanceRow {
  def: AllowanceDef;
  choice: AllowanceChoice;
  value: number;
}

/**
 * The register of people the payroll runs over.
 *
 * <p>Everything a payslip needs about a person is typed here once: which branch holds them,
 * whether they are paid by the day or by the month and at what rate, how they are paid, which
 * allowances they carry, and how their time is captured. The screen is a form over a list rather
 * than a wizard — an employee record is edited far more often than it is created, usually to fix
 * one field.</p>
 *
 * <p>Two things it refuses to invent. <b>Leaving is not a delete</b>: a person who has been paid
 * is recorded as having left on a day, and every payslip, advance and posting that names them
 * stays exactly where it is. And <b>an allowance has three states</b>, not a checkbox: the
 * company's figure, this person's own figure, or nothing at all — an override of zero and "this
 * person does not get it" are different claims, and the engine pays them differently.</p>
 */
@Component({
  selector: 'app-hr-employees',
  standalone: true,
  imports: [
    FormsModule,
    FoldComponent,
    DateInputComponent,
    ...PAGE_CHROME,
    ...MENU,
    StatusComponent,
    TableStateComponent,
    ListFooterComponent,
  ],
  template: `
    <ng-template appPageActions>
      <button type="button" class="btn-primary" (click)="openCreate()">
        <span aria-hidden="true">+</span> {{ i18n.t('hr.employees.newAction') }}
      </button>
    </ng-template>

    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter ui-filter--search">
          <label for="em-q">{{ i18n.t('hr.employees.search') }}</label>
          <input id="em-q" type="search" [ngModel]="query()" (ngModelChange)="setQuery($event)" />
        </div>
        @if (lock.locked()) {
          <p class="bo-scope-note" id="emf-scope">{{ i18n.t('branchLock.listNote', { branch: lock.ownBranchName(branches()) }) }}</p>
        } @else {
          <div class="ui-filter">
            <label for="em-filter-branch">{{ i18n.t('hr.employees.branch') }}</label>
            <select id="em-filter-branch" [ngModel]="filterBranch()" (ngModelChange)="setFilterBranch($event)">
              <option value="">{{ i18n.t('hr.employees.allBranches') }}</option>
              @for (branch of branches(); track branch.id) {
                <option [value]="branch.id">{{ branchName(branch) }}</option>
              }
            </select>
          </div>
        }
        <div class="ui-filter">
          <span class="ui-filter__label" id="em-state-label">{{ i18n.t('common.state') }}</span>
          <div class="bo-chips" role="group" aria-labelledby="em-state-label">
            <button type="button" [class.is-on]="activeOnly()" [attr.aria-pressed]="activeOnly()" (click)="setActiveOnly(true)">
              {{ i18n.t('hr.employees.stillWithUs') }}
            </button>
            <button type="button" [class.is-on]="!activeOnly()" [attr.aria-pressed]="!activeOnly()" (click)="setActiveOnly(false)">
              {{ i18n.t('hr.employees.goneOrSuspended') }}
            </button>
          </div>
        </div>
      </div>
    </ng-template>

    <div class="bo-stack">
      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      @if (notice()) {
        <p class="notice ok" role="status">{{ notice() }}</p>
      }

      <!-- ---------- the form ---------- -->
      @if (editorOpen()) {
        <div class="bo-backdrop" (click)="startNew()"></div>
      }
      <section
        class="bo-panel"
        [class.bo-edit-modal]="editorOpen()"
        [class.bo-editor-hidden]="!editorOpen()"
        [attr.role]="editorOpen() ? 'dialog' : null"
        [attr.aria-modal]="editorOpen() ? 'true' : null"
        [attr.aria-label]="editorOpen() ? (editing() ? i18n.t('common.edit') : i18n.t('hr.employees.new')) : null"
        (keydown.escape)="editorOpen() && startNew()"
      >
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">
            {{ editing() ? i18n.t('hr.employees.edit', { name: editing()!.fullName }) : i18n.t('hr.employees.new') }}
          </h2>
          <p class="bo-panel__note">{{ i18n.t('hr.employees.formHint') }}</p>
        </div>

        <div class="bo-panel__body bo-form">
          <app-fold class="bo-group" [heading]="i18n.t('common.group.identity')" [open]="true">
            <div class="bo-fields employee-identity-fields">
              <!-- The name in two parts (owner, PinSay #301: "name and family name in separate
                   inputs"), in the order it prints: first name, then family name. -->
              <div class="bo-field" [class.is-invalid]="touched.missing('firstName', form.firstName)">
                <label for="em-first-name">
                  {{ i18n.t('hr.employees.firstName') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                </label>
                <input id="em-first-name" [(ngModel)]="form.firstName" (blur)="touched.touch('firstName')" />
                @if (touched.missing('firstName', form.firstName)) {
                  <span class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                }
                <!-- Filed before the split: the whole name sits under the family name until somebody
                     moves the first name out — said here, where that is done. -->
                @if (editing() && !editing()!.firstName) {
                  <span class="bo-hint">{{ i18n.t('hr.employees.splitName') }}</span>
                }
              </div>
              <div class="bo-field" [class.is-invalid]="touched.missing('familyName', form.familyName)">
                <label for="em-family-name">
                  {{ i18n.t('hr.employees.familyName') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                </label>
                <input id="em-family-name" [(ngModel)]="form.familyName" (blur)="touched.touch('familyName')" />
                @if (touched.missing('familyName', form.familyName)) {
                  <span class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                }
              </div>
              <div class="bo-field">
                <label for="em-job">{{ i18n.t('hr.employees.jobTitle') }}</label>
                <input id="em-job" [(ngModel)]="form.jobTitle" />
              </div>
              <div class="bo-field">
                <label for="em-phone">{{ i18n.t('hr.employees.phone') }}</label>
                <input id="em-phone" class="mono" [(ngModel)]="form.phone" />
              </div>
              <!-- Optional and stored only (owner, 2026-10-05): nothing is mailed to it. An address
                   reads left to right in Arabic too. -->
              <div class="bo-field" [class.is-invalid]="emailProblem()">
                <label for="em-email">{{ i18n.t('hr.employees.email') }}</label>
                <input
                  id="em-email"
                  type="email"
                  dir="ltr"
                  autocomplete="email"
                  maxlength="200"
                  [(ngModel)]="form.email"
                  (blur)="touched.touch('email')"
                  [attr.aria-invalid]="emailProblem() ? 'true' : null"
                  [attr.aria-describedby]="emailProblem() ? 'em-email-error' : null"
                />
                @if (emailProblem()) {
                  <span id="em-email-error" class="bo-error" role="alert">{{ i18n.t('hr.problem.emailInvalid') }}</span>
                }
              </div>
              <div class="bo-field">
                <label for="em-national">{{ i18n.t('hr.employees.nationalId') }}</label>
                <input id="em-national" class="mono" [(ngModel)]="form.nationalId" />
              </div>
              <div class="bo-field">
                <label for="em-social">{{ i18n.t('hr.employees.socialNumber', { name: socialLabel() }) }}</label>
                <input id="em-social" class="mono" [(ngModel)]="form.socialSecurityNumber" />
                <p class="bo-hint">{{ i18n.t('hr.employees.socialNumberHint', { name: socialLabel() }) }}</p>
              </div>
              <div class="bo-field">
                <label for="em-birth">{{ i18n.t('hr.employees.birthDate') }}</label>
                <app-date-input id="em-birth" [(ngModel)]="form.birthDate" />
              </div>
              <div class="bo-field bo-field--wide employee-address-field">
                <label for="em-address">{{ i18n.t('hr.employees.address') }}</label>
                <input id="em-address" [(ngModel)]="form.address" />
              </div>
            </div>
          </app-fold>

          <!-- Open: the branch, the hiring day and the salary are required. -->
          <app-fold class="bo-group" [heading]="i18n.t('hr.employees.engagement')" [open]="true">
            <div class="bo-fields">
              @if (lock.locked()) {
                <div class="bo-field">
                  <p class="bo-fixed-label">{{ i18n.t('branchLock.branch') }}</p>
                  <p class="bo-fixed" id="em-fixed">{{ lock.ownBranchName(branches()) }}</p>
                  <span class="bo-hint">{{ i18n.t('branchLock.recordHint') }}</span>
                </div>
              } @else {
                <div class="bo-field" [class.is-invalid]="touched.missing('branchId', form.branchId)">
                  <label for="em-branch">
                    {{ i18n.t('hr.employees.branch') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <select id="em-branch" [(ngModel)]="form.branchId" (blur)="touched.touch('branchId')">
                    <option value="">—</option>
                    @for (branch of branches(); track branch.id) {
                      <option [value]="branch.id">{{ branchName(branch) }}</option>
                    }
                  </select>
                  @if (touched.missing('branchId', form.branchId)) {
                    <span class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                  }
                </div>
              }
              <div class="bo-field" [class.is-invalid]="touched.missing('hiredOn', form.hiredOn)">
                <label for="em-hired">
                  {{ i18n.t('hr.employees.hiredOn') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                </label>
                <app-date-input
                  id="em-hired"
                  [(ngModel)]="form.hiredOn"
                  (blur)="touched.touch('hiredOn')"
                  (ngModelChange)="touched.touch('hiredOn')"
                />
                @if (touched.missing('hiredOn', form.hiredOn)) {
                  <span class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                }
              </div>
              <!-- No basis to choose (owner, 2026-10-05, answers 41/47): payroll has no daily
                   workers — a day worker's wage is a payment voucher under Daily labour. Everyone
                   is filed monthly. Only a record from before says otherwise, and says it here in
                   words, with the one way out. -->
              @if (legacyDaily()) {
                <div class="bo-field bo-field--wide">
                  <p class="bo-fixed-label">{{ i18n.t('hr.employees.basis') }}</p>
                  <p class="bo-fixed" data-basis>
                    {{ i18n.t(form.payBasis === 'Daily' ? 'hr.employees.basis.daily' : 'hr.basis.Monthly') }}
                  </p>
                  <p class="notice warn" role="status" data-daily-retired>{{ i18n.t('hr.employees.dailyRetired') }}</p>
                  <label class="bo-check" for="em-pay-monthly">
                    <input
                      id="em-pay-monthly"
                      type="checkbox"
                      [checked]="payMonthly()"
                      (change)="setPayMonthly($any($event.target).checked)"
                      aria-describedby="em-pay-monthly-hint"
                    />
                    <span>{{ i18n.t('hr.employees.payMonthly') }}</span>
                  </label>
                  <p class="bo-hint" id="em-pay-monthly-hint">
                    {{ i18n.t(payMonthly() ? 'hr.employees.payMonthlyHint' : 'hr.employees.keepsDaily') }}
                  </p>
                </div>
              }
              <div class="bo-field" [class.is-invalid]="salaryMissing()">
                <!-- The label follows the basis: "daily rate" and "monthly salary" are not the
                     same figure, and one label for both is how a month's pay gets typed into a
                     day's box. Only a legacy daily record still reads "daily rate". -->
                <label for="em-rate">
                  {{ i18n.t(form.payBasis === 'Daily' ? 'hr.employees.dailyRate' : 'hr.employees.monthlySalary') }}
                  <span class="bo-req">{{ i18n.t('common.required') }}</span>
                </label>
                <input
                  id="em-rate"
                  type="number"
                  min="0"
                  class="num mono"
                  [step]="regional.moneyStep()"
                  [ngModel]="form.baseRate"
                  (ngModelChange)="form.baseRate = amount($event)"
                  (blur)="touched.touch('baseRate')"
                  [attr.aria-invalid]="salaryMissing() ? 'true' : null"
                  [attr.aria-describedby]="salaryMissing() ? 'em-rate-error' : null"
                />
                @if (salaryMissing()) {
                  <span id="em-rate-error" class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                }
              </div>
              <div class="bo-field">
                <label for="em-payout">{{ i18n.t('hr.employees.payout') }}</label>
                <select id="em-payout" [(ngModel)]="form.payoutMethod">
                  <option value="Cash">{{ i18n.t('hr.payout.Cash') }}</option>
                  <option value="Bank">{{ i18n.t('hr.payout.Bank') }}</option>
                </select>
              </div>
              @if (form.payoutMethod === 'Bank') {
                <div class="bo-field">
                  <label for="em-bank">{{ i18n.t('hr.employees.bankAccount') }}</label>
                  <input id="em-bank" class="mono" [(ngModel)]="form.bankAccount" />
                </div>
              }
              <div class="bo-field">
                <label for="em-mode">{{ i18n.t('hr.employees.attendanceMode') }}</label>
                <select id="em-mode" [(ngModel)]="form.attendanceModeOverride">
                  <option [ngValue]="null">
                    {{ i18n.t('hr.employees.followsCompany', { mode: i18n.t('hr.attendance.mode.' + companyMode()) }) }}
                  </option>
                  <option value="Declared">{{ i18n.t('hr.attendance.mode.Declared') }}</option>
                  <option value="Recorded">{{ i18n.t('hr.attendance.mode.Recorded') }}</option>
                </select>
                <p class="bo-hint">{{ i18n.t('hr.employees.attendanceModeHint') }}</p>
              </div>
              <div class="bo-field">
                <label for="em-fingerprint">{{ i18n.t('hr.employees.fingerprint') }}</label>
                <input
                  id="em-fingerprint"
                  class="mono"
                  maxlength="20"
                  inputmode="numeric"
                  autocomplete="off"
                  [(ngModel)]="form.fingerprintNumber"
                  aria-describedby="em-fingerprint-hint"
                />
                <p class="bo-hint" id="em-fingerprint-hint">{{ i18n.t('hr.employees.fingerprintHint') }}</p>
              </div>
              <div class="bo-field">
                <label class="bo-check" for="em-active">
                  <input id="em-active" type="checkbox" [(ngModel)]="form.isActive" />
                  <span>{{ i18n.t('hr.employees.isActive') }}</span>
                </label>
                <p class="bo-hint">{{ i18n.t('hr.employees.isActiveHint') }}</p>
              </div>
              <div class="bo-field bo-field--wide">
                <label for="em-notes">{{ i18n.t('hr.employees.notes') }}</label>
                <input id="em-notes" [(ngModel)]="form.notes" />
              </div>
            </div>
          </app-fold>

          <!-- ---------- allowances ---------- -->
          <!-- Folded at first: every allowance follows the company's value until it is changed here. -->
          <app-fold class="bo-group" [heading]="i18n.t('hr.employees.allowances')">
            <p class="bo-group__note" foldLead>{{ i18n.t('hr.employees.allowancesHint') }}</p>
            @if (allowances().length === 0) {
              <p class="bo-empty">{{ i18n.t('hr.employees.noAllowances') }}</p>
            } @else {
              <div class="bo-scroll">
                <table class="bo-data">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('hr.field.label') }}</th>
                      <th class="num">{{ i18n.t('hr.employees.companyValue') }}</th>
                      <th>{{ i18n.t('hr.employees.appliesAs') }}</th>
                      <th class="num">{{ i18n.t('hr.employees.ownValue') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (row of allowances(); track row.def.key) {
                      <tr>
                        <td [attr.data-label]="i18n.t('hr.field.label')">
                          <span class="bo-cell-strong">{{ label(row.def.labels) || row.def.key }}</span>
                        </td>
                        <td class="num mono" [attr.data-label]="i18n.t('hr.employees.companyValue')">
                          {{ allowanceText(row.def, row.def.value) }}
                        </td>
                        <td [attr.data-label]="i18n.t('hr.employees.appliesAs')">
                          <select [ngModel]="row.choice" (ngModelChange)="setChoice(row, $event)">
                            <option value="default">{{ i18n.t('hr.employees.allowance.default') }}</option>
                            <option value="own">{{ i18n.t('hr.employees.allowance.own') }}</option>
                            <option value="none">{{ i18n.t('hr.employees.allowance.none') }}</option>
                          </select>
                        </td>
                        <td class="num" [attr.data-label]="i18n.t('hr.employees.ownValue')">
                          @if (row.choice === 'own') {
                            <input
                              type="number"
                              min="0"
                              class="num mono bo-input-narrow"
                              [step]="row.def.calculation === 'PercentOfBase' ? 0.1 : regional.moneyStep()"
                              [ngModel]="row.value"
                              (ngModelChange)="setAllowanceValue(row, $event)"
                            />
                          } @else {
                            <span class="bo-hint">—</span>
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
          </app-fold>

          @if (editing(); as person) {
            <!-- Folded at first, except for someone who has already left: that is said, not hidden. -->
            <app-fold class="bo-group" [heading]="i18n.t('hr.employees.leaving')" [open]="!!person.leftOn">
              <p class="bo-group__note" foldLead>{{ i18n.t('hr.employees.leavingHint') }}</p>
              @if (person.leftOn) {
                <p class="notice warn" role="status">
                  {{ i18n.t('hr.employees.alreadyLeft', { date: day(person.leftOn) }) }}
                </p>
              }
              <div class="bo-fields">
                <div class="bo-field">
                  <label for="em-left">{{ i18n.t('hr.employees.leftOn') }}</label>
                  <app-date-input id="em-left" [ngModel]="leftOn()" (ngModelChange)="leftOn.set($event)" />
                </div>
                <div class="bo-field bo-field--wide">
                  <label for="em-left-note">{{ i18n.t('hr.employees.leaveNote') }}</label>
                  <input id="em-left-note" [ngModel]="leaveNote()" (ngModelChange)="leaveNote.set($event)" />
                </div>
              </div>
              <div class="bo-actions">
                <button type="button" class="btn-ghost" [disabled]="busy() || !leftOn()" (click)="recordLeaving()">
                  {{ i18n.t('hr.employees.leaveAction') }}
                </button>
              </div>
            </app-fold>
          }
        </div>

        <div class="bo-panel__foot">
          <button type="button" class="btn-ghost" (click)="startNew()">{{ i18n.t('common.cancel') }}</button>
          <button type="button" class="btn-primary" [disabled]="busy() || !valid()" (click)="save()">
            {{ i18n.t('common.save') }}
          </button>
        </div>
      </section>

      <!-- ---------- the list ---------- -->
      <section class="ui-surface" [attr.aria-busy]="loading()">
        @if (loading()) {
          <app-table-state kind="loading" />
        } @else if (rows().length === 0) {
          @if (filtered()) {
            <app-table-state kind="noResults" (clear)="clearFilters()" />
          } @else {
            <app-table-state kind="empty" [message]="i18n.t('hr.employees.none')">
              <button type="button" class="btn-primary" (click)="openCreate()">{{ i18n.t('hr.employees.newAction') }}</button>
            </app-table-state>
          }
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('hr.employees.number') }}</th>
                  <th>{{ i18n.t('hr.employees.name') }}</th>
                  <th>{{ i18n.t('hr.employees.branch') }}</th>
                  <!-- Everyone is monthly now; the basis is a column only while a legacy daily
                       record is listed, so the reader sees who is still paid by the day. -->
                  @if (showBasis()) {
                    <th>{{ i18n.t('hr.employees.basis') }}</th>
                  }
                  <th class="num">{{ i18n.t(rateLabel()) }}</th>
                  <th>{{ i18n.t('hr.employees.payout') }}</th>
                  <th>{{ i18n.t('common.state') }}</th>
                  <th class="num">{{ i18n.t('hr.employees.openAdvances') }}</th>
                  <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                </tr>
              </thead>
              <tbody>
                @for (person of rows(); track person.id) {
                  <tr [class.is-muted]="!isHere(person)">
                    <td [attr.data-label]="i18n.t('hr.employees.number')">
                      <span class="mono" dir="ltr">{{ person.number }}</span>
                      @if (person.fingerprintNumber) {
                        <span class="ui-sub">{{ i18n.t('hr.employees.fingerprintShort', { number: person.fingerprintNumber }) }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('hr.employees.name')">
                      <button type="button" class="ui-link" (click)="edit(person)">{{ person.fullName }}</button>
                      @if (person.jobTitle) {
                        <span class="ui-sub">{{ person.jobTitle }}</span>
                      }
                      <!-- Access to the application, given from the employee (2026-10-05): how someone
                           with an account signs in. Giving access is in the row menu. -->
                      @if (person.accountSignIn) {
                        <span class="ui-sub">{{ i18n.t('hr.employees.access', { signIn: person.accountSignIn }) }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('hr.employees.branch')">{{ person.branchName }}</td>
                    @if (showBasis()) {
                      <td [attr.data-label]="i18n.t('hr.employees.basis')">{{ i18n.t('hr.basis.' + person.payBasis) }}</td>
                    }
                    <td class="num mono" [attr.data-label]="i18n.t(rateLabel())">
                      {{ i18n.money(person.baseRate) }}
                    </td>
                    <td [attr.data-label]="i18n.t('hr.employees.payout')">
                      {{ i18n.t('hr.payout.' + person.payoutMethod) }}
                    </td>
                    <td [attr.data-label]="i18n.t('common.state')">
                      <!-- The word carries the state; the tint only makes it findable. -->
                      <app-status [tone]="statusTone(person)">{{ statusText(person) }}</app-status>
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('hr.employees.openAdvances')">
                      {{ person.openAdvances ? i18n.money(person.openAdvances) : '—' }}
                    </td>
                    <td class="ui-cell-menu">
                      <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + person.fullName">
                        <span menuTrigger aria-hidden="true">⋯</span>
                        <button type="button" appMenuItem (click)="edit(person)">{{ i18n.t('common.edit') }}</button>
                        @if (!person.accountSignIn && mayGiveAccess() && isHere(person)) {
                          <button type="button" appMenuItem (click)="giveAccess(person)">{{ i18n.t('hr.employees.giveAccess') }}</button>
                        }
                      </app-menu>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </section>

      <!-- The server pages this register (skip/take); the footer turns its pages and sets its size. -->
      <app-list-footer
        [total]="total()"
        [pageIndex]="pageIndex()"
        [pageSize]="take()"
        [loading]="loading()"
        (pageChange)="goTo($event)"
        (pageSizeChange)="setTake($event)"
      />
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .employee-identity-fields {
        grid-template-columns: repeat(3, minmax(0, 1fr));
      }
      @media (min-width: 720px) and (max-width: 1050px) {
        .employee-identity-fields {
          grid-template-columns: repeat(2, minmax(0, 1fr));
        }
      }
      @media (max-width: 719px) {
        .employee-identity-fields {
          grid-template-columns: 1fr;
        }
      }
    `,
  ],
})
export class HrEmployeesComponent implements OnInit {
  private readonly hr = inject(HrApi);
  private readonly payroll = inject(PayrollApi);
  private readonly admin = inject(AdminService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly confirm = inject(ConfirmService);
  readonly i18n = inject(I18nService);
  readonly lock = inject(BranchLock);
  readonly regional = inject(RegionalService);

  /** Rows a page asks the server for: ten to begin with, 20, 50 or 100 at the user's choice (owner, 2026-11-17). */
  readonly take = signal(DEFAULT_PAGE_SIZE);

  readonly branches = signal<AdminBranch[]>([]);
  readonly settings = signal<PayrollSettings | null>(null);
  readonly rows = signal<Employee[]>([]);
  readonly total = signal(0);
  readonly skip = signal(0);
  readonly query = signal('');
  readonly filterBranch = signal('');
  readonly activeOnly = signal(true);

  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);

  readonly editing = signal<Employee | null>(null);
  readonly editorOpen = signal(false);
  readonly allowances = signal<AllowanceRow[]>([]);
  readonly leftOn = signal('');
  readonly leaveNote = signal('');
  readonly touched = new TouchedFields();
  /** A legacy daily record being moved to a monthly salary ("Pay monthly"). */
  readonly payMonthly = signal(false);
  /** The address the server last refused (`emailInvalid`), said on the field until it is changed. */
  readonly refusedEmail = signal<string | null>(null);

  form: {
    firstName: string;
    familyName: string;
    branchId: string;
    payBasis: 'Daily' | 'Monthly';
    /** Null while nothing is typed — the state "Pay monthly" leaves it in, so the new salary has to be. */
    baseRate: number | null;
    payoutMethod: 'Cash' | 'Bank';
    hiredOn: string;
    isActive: boolean;
    nationalId: string;
    socialSecurityNumber: string;
    fingerprintNumber: string;
    birthDate: string;
    phone: string;
    email: string;
    address: string;
    jobTitle: string;
    bankAccount: string;
    attendanceModeOverride: AttendanceMode | null;
    notes: string;
  } = blankForm();

  /** A search is typed a letter at a time; the list is fetched once the typing stops. */
  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  readonly companyMode = computed<AttendanceMode>(() => this.settings()?.defaultAttendanceMode ?? 'Declared');

  /** The contribution body's own name, as the settings spell it — never a hardcoded fiscal label. */
  readonly socialLabel = computed(() => this.label(this.settings()?.socialNames));

  /**
   * A method, not a `computed`. `form` is a plain mutable object — `[(ngModel)]` writes into its
   * fields and no signal is touched — so a computed over it has no producer, is evaluated once
   * against the blank form, caches `false` and never invalidates. The Save button was then
   * disabled for ever and no employee could be created or edited at all. The neighbours here
   * (`canGrant`, `canPay`) are methods for the same reason.
   */
  valid(): boolean {
    return (
      !!this.form.firstName.trim() &&
      !!this.form.familyName.trim() &&
      !!this.form.branchId &&
      !!this.form.hiredOn &&
      !this.salaryNeeded()
    );
  }

  /**
   * The record being edited is paid by the day — data from before the owner's rule (2026-10-05:
   * "there are no daily workers on payroll"). The server keeps it so on an edit and refuses any new
   * one, so the form shows it in words and offers the one way out, to a monthly salary.
   */
  readonly legacyDaily = computed(() => this.editing()?.payBasis === 'Daily');

  /** The list's basis column, only while a legacy daily record is listed. */
  readonly showBasis = computed(() => this.rows().some((person) => person.payBasis === 'Daily'));

  /** With no daily record listed, every figure in the column is a monthly salary, and it says so. */
  rateLabel(): string {
    return this.showBasis() ? 'hr.employees.rate' : 'hr.employees.monthlySalary';
  }

  /**
   * Moves a legacy daily record to a monthly salary, or back. The daily rate is never carried
   * over as a monthly salary — the field is emptied so the new figure has to be typed — and
   * clearing the box puts the person back on their daily rate as stored.
   */
  setPayMonthly(on: boolean): void {
    const person = this.editing();
    if (!person || person.payBasis !== 'Daily') return;
    this.payMonthly.set(on);
    this.form.payBasis = on ? 'Monthly' : 'Daily';
    this.form.baseRate = on ? null : person.baseRate;
  }

  /** Switched to monthly and no salary typed yet: Save waits for it. */
  private salaryNeeded(): boolean {
    return this.payMonthly() && this.form.baseRate === null;
  }

  /** Said under the field once it was left, or a save was tried, without the new salary. */
  salaryMissing(): boolean {
    return this.salaryNeeded() && this.touched.visited('baseRate');
  }

  /**
   * The address is malformed by the browser's own reading of an email field, once the field was
   * left — or it is the one the server just refused. The server decides; this only says it sooner.
   */
  emailProblem(): boolean {
    const email = this.form.email.trim();
    if (!email) return false;
    if (email === this.refusedEmail()) return true;
    return this.touched.visited('email') && notAnEmail(email);
  }

  /** The page on screen, from 0, as the footer counts it. */
  readonly pageIndex = computed(() => Math.floor(this.skip() / this.take()));

  ngOnInit(): void {
    void this.start();
  }

  // ---------- reading ----------

  private async start(): Promise<void> {
    try {
      const [branches, settings] = await Promise.all([this.admin.listBranches(), this.payroll.settings()]);
      this.branches.set(branches);
      this.filterBranch.set(this.lock.lockedBranchId() ?? '');
      this.form.branchId ||= this.lock.defaultBranch(branches) ?? '';
      this.settings.set(settings.settings);
      this.resetAllowances(null);
    } catch (e) {
      this.error.set(this.message(e));
    }
    await this.load();
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    try {
      const page = await this.hr.employees({
        branchId: this.filterBranch() || null,
        active: this.activeOnly(),
        q: this.query().trim() || null,
        skip: this.skip(),
        take: this.take(),
      });
      this.rows.set(page.rows ?? []);
      this.total.set(page.total ?? 0);
    } catch (e) {
      this.error.set(this.message(e));
    } finally {
      this.loading.set(false);
    }
  }

  setQuery(value: string): void {
    this.query.set(value ?? '');
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => {
      this.skip.set(0);
      void this.load();
    }, 250);
  }

  setFilterBranch(value: string): void {
    this.filterBranch.set(value ?? '');
    this.skip.set(0);
    void this.load();
  }

  setActiveOnly(value: boolean): void {
    this.activeOnly.set(value);
    this.skip.set(0);
    void this.load();
  }

  /** A page from the footer: the server is asked for that page, as the old next/previous did. */
  goTo(index: number): void {
    const next = Math.max(0, index * this.take());
    if (next === this.skip() || (next >= this.total() && next > 0)) return;
    this.skip.set(next);
    void this.load();
  }

  /** Another page size starts again at the first page. */
  setTake(size: number): void {
    if (size === this.take()) return;
    this.take.set(size);
    this.skip.set(0);
    void this.load();
  }

  // ---------- the form ----------

  startNew(): void {
    this.editorOpen.set(false);
    this.editing.set(null);
    this.form = blankForm();
    // A non-admin files people at their own branch; the admin starts on his.
    this.form.branchId = this.lock.defaultBranch(this.branches()) ?? '';
    this.touched.reset();
    this.payMonthly.set(false);
    this.refusedEmail.set(null);
    this.leftOn.set('');
    this.leaveNote.set('');
    this.resetAllowances(null);
  }

  openCreate(): void {
    this.startNew();
    this.editorOpen.set(true);
  }

  edit(person: Employee): void {
    this.editing.set(person);
    this.touched.reset();
    this.payMonthly.set(false);
    this.refusedEmail.set(null);
    this.notice.set(null);
    this.form = {
      firstName: person.firstName ?? '',
      // A server older than the parts sends none: the whole name, where the migration files it too.
      familyName: person.familyName ?? person.fullName,
      branchId: person.branchId,
      payBasis: person.payBasis,
      baseRate: person.baseRate,
      payoutMethod: person.payoutMethod,
      hiredOn: person.hiredOn,
      isActive: person.isActive,
      nationalId: person.nationalId ?? '',
      socialSecurityNumber: person.socialSecurityNumber ?? '',
      fingerprintNumber: person.fingerprintNumber ?? '',
      birthDate: person.birthDate ?? '',
      phone: person.phone ?? '',
      email: person.email ?? '',
      address: person.address ?? '',
      jobTitle: person.jobTitle ?? '',
      bankAccount: person.bankAccount ?? '',
      attendanceModeOverride: person.attendanceModeOverride ?? null,
      notes: person.notes ?? '',
    };
    this.leftOn.set(person.leftOn ?? '');
    this.leaveNote.set('');
    this.resetAllowances(person);
    this.editorOpen.set(true);
  }

  /** The defined allowances, each showing how this person carries it. */
  private resetAllowances(person: Employee | null): void {
    const defs = (this.settings()?.allowances ?? []).filter((a) => a.isActive);
    const own = new Map<string, EmployeeAllowance>();
    for (const row of person?.allowances ?? []) own.set(row.allowanceKey.toLowerCase(), row);
    this.allowances.set(
      defs.map((def) => {
        const stored = own.get(def.key.toLowerCase());
        const choice: AllowanceChoice = !stored ? 'default' : stored.isEnabled ? 'own' : 'none';
        return { def, choice, value: stored?.value ?? def.value };
      }),
    );
  }

  setChoice(row: AllowanceRow, choice: AllowanceChoice): void {
    row.choice = choice;
    this.allowances.update((rows) => [...rows]);
  }

  setAllowanceValue(row: AllowanceRow, value: unknown): void {
    row.value = this.numeric(value);
    this.allowances.update((rows) => [...rows]);
  }

  async save(): Promise<void> {
    this.touched.touchAll('firstName', 'familyName', 'branchId', 'hiredOn', 'baseRate', 'email');
    if (!this.valid() || this.emailProblem()) return;
    const firstName = this.form.firstName.trim();
    const familyName = this.form.familyName.trim();
    const fullName = `${firstName} ${familyName}`;
    if (!(await this.confirm.ask(saveRequest(this.i18n, fullName)))) return;
    const body: UpsertEmployee = {
      fullName,
      firstName,
      familyName,
      branchId: this.form.branchId,
      // Monthly for everyone new; a legacy daily record keeps its basis unless "Pay monthly" moved it.
      payBasis: this.form.payBasis,
      baseRate: this.form.baseRate ?? 0,
      payoutMethod: this.form.payoutMethod,
      hiredOn: this.form.hiredOn,
      isActive: this.form.isActive,
      nationalId: blank(this.form.nationalId),
      socialSecurityNumber: blank(this.form.socialSecurityNumber),
      fingerprintNumber: blank(this.form.fingerprintNumber),
      birthDate: blank(this.form.birthDate),
      phone: blank(this.form.phone),
      // Never null: the server reads a null email as "keep the stored one" (an older client's
      // omission), so a cleared field goes as '' — which it stores as none.
      email: this.form.email.trim(),
      address: blank(this.form.address),
      jobTitle: blank(this.form.jobTitle),
      bankAccount: blank(this.form.bankAccount),
      attendanceModeOverride: this.form.attendanceModeOverride,
      notes: blank(this.form.notes),
      // The list is the whole set: a row left out is an override removed, which is exactly what
      // "the company's figure" means here.
      allowances: this.allowances()
        .filter((row) => row.choice !== 'default')
        .map((row) => ({ allowanceKey: row.def.key, value: row.value, isEnabled: row.choice === 'own' })),
    };

    const current = this.editing();
    await this.run(async () => {
      const saved = current ? await this.hr.updateEmployee(current.id, body) : await this.hr.createEmployee(body);
      this.startNew();
      this.notice.set(this.i18n.t('hr.employees.saved', { number: isolate(saved.number) }));
      await this.load();
    });
  }

  async recordLeaving(): Promise<void> {
    const person = this.editing();
    const on = this.leftOn();
    if (!person || !on) return;
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('hr.employees.leaveAction'), undefined, true)))) return;
    await this.run(async () => {
      const saved = await this.hr.markLeft(person.id, { leftOn: on, note: blank(this.leaveNote()) });
      this.edit(saved);
      this.notice.set(this.i18n.t('hr.employees.leaveRecorded', { date: this.day(on) }));
      await this.load();
    });
  }

  // ---------- words ----------

  branchName(branch: AdminBranch): string {
    return localizedName(branch, this.i18n.lang());
  }

  label(labels: TextByLang | undefined | null): string {
    if (!labels) return '';
    return labels[this.i18n.lang()] ?? labels['fr'] ?? labels['en'] ?? '';
  }

  day(value: string | null | undefined): string {
    return formatDay(value, this.i18n.lang(), this.regional.settings().timeZone);
  }

  /** A percentage is not money; only one of the two goes through the regional formatter. */
  allowanceText(def: AllowanceDef, value: number): string {
    return def.calculation === 'PercentOfBase' ? this.i18n.rateLabel(value) : this.i18n.money(value);
  }

  isHere(person: Employee): boolean {
    return person.isActive && !person.leftOn;
  }

  /** Whether this reader may open Users, where an account is given — the same rule the sidebar follows. */
  readonly mayGiveAccess = computed(() => canOpen(this.auth.currentUser(), 'admin.users'));

  /** Opens Users → New user on this employee: the account is given from them, its Login ID drawn on save. */
  async giveAccess(person: Employee): Promise<void> {
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('hr.employees.giveAccess'))))) return;
    void this.router.navigate(['/backoffice/admin/users'], { queryParams: { employee: person.id } });
  }

  /** Here, suspended or gone: the tone comes from the one mapping (ui/status-tones.ts). */
  statusTone(person: Employee): StatusTone {
    return toneOf('employee', person.leftOn ? 'left' : person.isActive ? 'here' : 'suspended');
  }

  /** Whether a filter narrows the list, so an empty one says "no match", not "nobody yet". */
  filtered(): boolean {
    return !!this.query() || this.filterBranch() !== (this.lock.lockedBranchId() ?? '') || !this.activeOnly();
  }

  /** Every filter back to where the page opens: the search, all branches, people still here. */
  clearFilters(): void {
    this.query.set('');
    this.filterBranch.set(this.lock.lockedBranchId() ?? '');
    this.setActiveOnly(true);
  }

  statusText(person: Employee): string {
    if (person.leftOn) return this.i18n.t('hr.employees.left', { date: this.day(person.leftOn) });
    return person.isActive ? this.i18n.t('hr.employees.here') : this.i18n.t('hr.employees.suspended');
  }

  numeric(value: unknown): number {
    const parsed = typeof value === 'number' ? value : Number(value);
    return Number.isFinite(parsed) && parsed >= 0 ? parsed : 0;
  }

  /** The salary field's value: nothing while it is empty, a figure once one is typed. */
  amount(value: unknown): number | null {
    return value === null || value === undefined || value === '' ? null : this.numeric(value);
  }

  // ---------- plumbing ----------

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    this.notice.set(null);
    try {
      await action();
    } catch (e) {
      this.error.set(this.message(e));
      // Said on the field as well as above the form, until the address is changed.
      if (hrProblem(e)?.code === 'emailInvalid') this.refusedEmail.set(this.form.email.trim());
    } finally {
      this.busy.set(false);
    }
  }

  /**
   * A refusal in the reader's language. The server answers a code because the person who meets
   * one is a clerk reading Arabic or French; the English sentence beside it is for the log.
   */
  private message(error: unknown): string {
    const problem = hrProblem(error);
    // 401, 403 and an unreachable server are said in words here too: four of these screens used
    // to print the bare "403 Forbidden" at a manager who simply lacks payroll.manage.
    if (!problem) return moneyErrorMessage(error, (key, params) => this.i18n.t(key, params));
    const key = `hr.problem.${problem.code}`;
    const text = this.i18n.t(key, { key: problem.key ?? '', run: problem.runNumber ?? '' });
    // `t` hands the key back when there is no wording for it. A code nobody has written a
    // sentence for is still better said in the server's English than as a bare key.
    return text === key ? problem.message || key : text;
  }
}

function blankForm() {
  return {
    firstName: '',
    familyName: '',
    branchId: '',
    // No basis is chosen any more (owner, 2026-10-05): a new employee is paid monthly.
    payBasis: 'Monthly' as 'Daily' | 'Monthly',
    baseRate: 0 as number | null,
    payoutMethod: 'Cash' as const,
    hiredOn: today(),
    isActive: true,
    nationalId: '',
    socialSecurityNumber: '',
    fingerprintNumber: '',
    birthDate: '',
    phone: '',
    email: '',
    address: '',
    jobTitle: '',
    bankAccount: '',
    attendanceModeOverride: null as AttendanceMode | null,
    notes: '',
  };
}

/**
 * What `type="email"` would flag, read from the browser itself rather than a second rule written
 * here: the server's check is the one that decides, and a stricter one on this side would only
 * refuse what it accepts.
 */
export function notAnEmail(value: string): boolean {
  const probe = document.createElement('input');
  probe.type = 'email';
  probe.value = value;
  return probe.validity.typeMismatch;
}

function blank(value: string): string | null {
  const trimmed = (value ?? '').trim();
  return trimmed ? trimmed : null;
}

function today(): string {
  const now = new Date();
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

function pad(value: number): string {
  return String(value).padStart(2, '0');
}
